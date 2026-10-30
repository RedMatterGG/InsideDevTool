using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace InsideDev.McpExt
{
    // McpServer.cs (owner SERVER): Model Context Protocol server over the "Streamable HTTP" transport. Negotiates
    // 2025-11-25 and also answers 2025-06-18, 2025-03-26 and 2024-11-05 clients (Versions; an unknown requested version
    // gets the newest). Unity-free and InsideDev-free except Json: the game side plugs in through IToolHost (ServerApi.cs),
    // and the stdio proxy compiles this file too. Listens on 127.0.0.1 only; requests run on one worker thread per
    // connection. Behaviour (docs/MCP_PLAY_DESIGN.md section 4.6):
    //   - profiles by path (McpProfile.FromPath): /mcp and /mcp/dev = dev, /mcp/play = play, /mcp/all = all. tools/list
    //     depends only on the path (and the host's allowlist), never on the connection.
    //   - POST: JSON-RPC (batches accepted, no streaming inside a batch). tools/call validates arguments strictly
    //     (Schema.Validate, defaults filled); a schema failure is an isError result with code bad_args. A tools/call with
    //     params._meta.progressToken from a client that accepts text/event-stream is answered as SSE: progress events,
    //     the response, then the connection closes. Other requests get one application/json reply.
    //   - notifications/cancelled {requestId, reason} cancels the CallInfo of (session, requestId); a cancel that
    //     arrives before its request is kept for 10 s.
    //   - GET with Accept: text/event-stream opens the session's notification stream: tools / resources / prompts
    //     list_changed whenever IToolHost.ListVersion moves, keep-alive comment every 15 s. DELETE ends the session, its
    //     stream and its running calls.
    //   - an unknown MCP-Protocol-Version header gets HTTP 400 (not checked on initialize and server/discover, so newer
    //     clients can fall back); an unknown Mcp-Session-Id gets 404; a non-local Origin 403; when IToolHost.AuthToken is
    //     set, every MCP request needs "Authorization: Bearer <token>" (401 otherwise). GET / and /health stay plain text.
    //   - plain endpoints without a session (ServerPlain below): POST /cmd (one console command, text reply), GET /state
    //     (concise observe JSON), GET /ping; the token rule applies to /cmd and /state.
    public sealed class McpServer
    {
        public static readonly string[] Versions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
        public const string Path = "/mcp";

        const int KeepAliveMs = 15000, StreamPollMs = 250, MaxSessions = 128, EarlyCancelMs = 10000, MaxWarned = 200;
        static readonly TimeSpan SessionIdle = TimeSpan.FromHours(6);

        readonly IToolHost host;
        TcpListener listener;
        Thread acceptThread;
        volatile bool running;
        readonly object gate = new object();
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly Dictionary<string, CallInfo> inflight = new Dictionary<string, CallInfo>();
        readonly Dictionary<string, EarlyCancel> earlyCancels = new Dictionary<string, EarlyCancel>();
        readonly HashSet<string> warned = new HashSet<string>();
        readonly List<TcpClient> clients = new List<TcpClient>();
        DateTime lastAuthLog = DateTime.MinValue;
        public int Port { get; private set; }
        public int Requests, ToolCalls, Errors;
        public string LastClient = "";
        public string LastError = "";

        // raised (on a worker thread) when a session ends by DELETE or Stop: session id and the sessions left
        internal event Action<string, int> SessionClosed;
        internal int SessionCount { get { lock (gate) return sessions.Count; } }
        internal int CallsRunning { get { lock (gate) return inflight.Count; } }

        sealed class Session
        {
            public string id, profile, protocol, client;
            public DateTime created, seen;
            public long notified;
            public SseWriter stream;
        }

        sealed class EarlyCancel { public DateTime at; public string reason; }

        public McpServer(IToolHost host) { this.host = host; }
        public bool Running { get { return running; } }

        public void Start(int port)
        {
            Stop();
            Port = port;
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "InsideDev MCP accept" };
            acceptThread.Start();
            host.Log("MCP server on http://127.0.0.1:" + port + Path + " (also " + Path + "/play, " + Path + "/all)");
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
            listener = null;
            lock (clients) { foreach (var c in clients) { try { c.Close(); } catch { } } clients.Clear(); }
            var ended = new List<string>();
            lock (gate)
            {
                foreach (var ci in inflight.Values) ci.Cancel("server stopped");
                inflight.Clear();
                earlyCancels.Clear();
                foreach (var ses in sessions.Values) { if (ses.stream != null) ses.stream.Close(); ended.Add(ses.id); }
                sessions.Clear();
            }
            foreach (var id in ended) RaiseClosed(id, 0);
        }

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { if (!running) return; Thread.Sleep(50); continue; }
                var t = new Thread(() => Serve(c)) { IsBackground = true, Name = "InsideDev MCP conn" };
                t.Start();
            }
        }

        // ---------------------------------------------------------------- HTTP
        sealed class Req { public string method, path; public readonly Dictionary<string, string> h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); public byte[] body; }

        void Serve(TcpClient c)
        {
            lock (clients) clients.Add(c);
            try
            {
                c.NoDelay = true;
                using (c)
                using (var s = c.GetStream())
                {
                    s.ReadTimeout = 120000;
                    try { s.WriteTimeout = 30000; } catch (NotSupportedException) { }
                    while (running)
                    {
                        var r = ReadRequest(s);
                        if (r == null) return;
                        bool close = Handle(r, s);
                        string conn; if (close || (r.h.TryGetValue("Connection", out conn) && conn.Equals("close", StringComparison.OrdinalIgnoreCase))) return;
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception e) { if (running) { LastError = e.Message; host.Log("MCP connection: " + e.Message); } }
            finally { lock (clients) clients.Remove(c); }
        }

        static string ReadLine(Stream s)
        {
            var sb = new StringBuilder();
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) return sb.Length == 0 ? null : sb.ToString();
                if (b == '\n') { if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--; return sb.ToString(); }
                sb.Append((char)b);
                if (sb.Length > 65536) throw new IOException("header line too long");
            }
        }

        static void ReadExact(Stream s, byte[] buf, int off, int n)
        {
            while (n > 0) { int k = s.Read(buf, off, n); if (k <= 0) throw new IOException("connection closed"); off += k; n -= k; }
        }

        static Req ReadRequest(Stream s)
        {
            string first = ReadLine(s);
            while (first != null && first.Length == 0) first = ReadLine(s);
            if (first == null) return null;
            var p = first.Split(' ');
            if (p.Length < 2) throw new IOException("bad request line");
            var r = new Req { method = p[0].ToUpperInvariant(), path = p[1] };
            int q = r.path.IndexOf('?'); if (q >= 0) r.path = r.path.Substring(0, q);
            string line;
            int headers = 0;
            while ((line = ReadLine(s)) != null && line.Length > 0)
            {
                if (++headers > 200) throw new IOException("too many headers");
                int c = line.IndexOf(':'); if (c <= 0) continue;
                r.h[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
            }
            string te, cl;
            if (r.h.TryGetValue("Transfer-Encoding", out te) && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var ms = new MemoryStream();
                while (true)
                {
                    string sz = ReadLine(s); if (sz == null) throw new IOException("bad chunk");
                    int semi = sz.IndexOf(';'); if (semi >= 0) sz = sz.Substring(0, semi);
                    int n = int.Parse(sz.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (n == 0) { while ((line = ReadLine(s)) != null && line.Length > 0) { } break; }
                    if (n < 0 || ms.Length + n > 16 * 1024 * 1024) throw new IOException("body too large");
                    var b = new byte[n]; ReadExact(s, b, 0, n); ms.Write(b, 0, n); ReadLine(s);
                }
                r.body = ms.ToArray();
            }
            else if (r.h.TryGetValue("Content-Length", out cl))
            {
                int n = int.Parse(cl, CultureInfo.InvariantCulture);
                if (n < 0 || n > 16 * 1024 * 1024) throw new IOException("body too large");
                r.body = new byte[n]; ReadExact(s, r.body, 0, n);
            }
            else r.body = new byte[0];
            return r;
        }

        static void Write(Stream s, int code, string reason, string contentType, byte[] body, Dictionary<string, string> extra)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
            if (contentType != null) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body != null ? body.Length : 0).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            if (extra != null) foreach (var kv in extra) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            sb.Append("\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            s.Write(head, 0, head.Length);
            if (body != null && body.Length > 0) s.Write(body, 0, body.Length);
            s.Flush();
        }
        static void WriteText(Stream s, int code, string reason, string text) { Write(s, code, reason, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), null); }
        static void WriteRpcError(Stream s, int http, string reason, int code, string message, Dictionary<string, string> extra)
        {
            Write(s, http, reason, "application/json", Encoding.UTF8.GetBytes(JsonOut.Write(Error(null, code, message))), extra);
        }
        static readonly Dictionary<string, string> AllowAll = new Dictionary<string, string> { { "Allow", "GET, POST, DELETE" } };

        // only browsers send Origin; allow local pages only (DNS-rebinding protection)
        static bool OriginOk(Req r)
        {
            string o; if (!r.h.TryGetValue("Origin", out o) || o.Length == 0 || o == "null") return true;
            try { var u = new Uri(o); return u.Host == "localhost" || u.Host == "127.0.0.1" || u.Host == "[::1]" || u.Host == "::1"; } catch { return false; }
        }

        static bool Accepts(Req r, string type)
        {
            string a; return r.h.TryGetValue("Accept", out a) && a.IndexOf(type, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool KnownVersion(string v) { return Array.IndexOf(Versions, v) >= 0; }

        static bool FixedEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < a.Length && i < b.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // null = allowed; otherwise the HTTP status to answer (401, or 500 when the host could not tell its token)
        int? AuthFailure(Req r)
        {
            string token;
            try { token = host.AuthToken ?? ""; }
            catch (Exception e) { LastError = "auth token: " + e.Message; return 500; }
            if (token.Length == 0) return null;
            string h;
            if (r.h.TryGetValue("Authorization", out h) && h.Length > 7 && h.Substring(0, 7).Equals("Bearer ", StringComparison.OrdinalIgnoreCase) && FixedEquals(h.Substring(7).Trim(), token)) return null;
            if ((DateTime.UtcNow - lastAuthLog).TotalSeconds > 10) { lastAuthLog = DateTime.UtcNow; host.Log("MCP request without a valid bearer token refused (" + LastClient + ")"); }
            return 401;
        }

        bool Handle(Req r, Stream s)
        {
            Interlocked.Increment(ref Requests);
            string ua; if (r.h.TryGetValue("User-Agent", out ua)) LastClient = ua;
            if (!OriginOk(r)) { WriteText(s, 403, "Forbidden", "origin not allowed"); return true; }
            string profile = McpProfile.FromPath(r.path);
            if (profile == null)
            {
                if (ServerPlain.Owns(r.path)) return ServePlain(r, s);
                if (r.method == "GET" && (r.path == "/" || r.path == "/health")) { WriteText(s, 200, "OK", "InsideDev MCP server " + host.ServerVersion + " - endpoints http://127.0.0.1:" + Port + Path + " (dev), " + Path + "/play, " + Path + "/all; " + ServerPlain.Endpoints); return false; }
                WriteText(s, 404, "Not Found", "MCP endpoints are " + Path + ", " + Path + "/dev, " + Path + "/play and " + Path + "/all; " + ServerPlain.Endpoints); return false;
            }
            int? auth = AuthFailure(r);
            if (auth == 401)
            {
                WriteRpcError(s, 401, "Unauthorized", -32001, "unauthorized: this InsideDev MCP server needs the header Authorization: Bearer <token> (the token is EditorState mcp.token in the game)",
                    new Dictionary<string, string> { { "WWW-Authenticate", "Bearer realm=\"insidedev\"" } });
                return false;
            }
            if (auth != null) { WriteRpcError(s, 500, "Internal Server Error", -32603, "the server could not read its auth token", null); return true; }
            string sid; r.h.TryGetValue("Mcp-Session-Id", out sid);
            if (sid != null && sid.Length == 0) sid = null;
            string pv; r.h.TryGetValue("MCP-Protocol-Version", out pv);
            bool badVersion = !string.IsNullOrEmpty(pv) && !KnownVersion(pv);
            switch (r.method)
            {
                case "POST": return HandlePost(r, s, profile, sid, badVersion ? pv : null);
                case "GET":
                    if (!Accepts(r, "text/event-stream")) { Write(s, 405, "Method Not Allowed", null, null, AllowAll); return false; }
                    if (badVersion) { UnsupportedVersion(s, pv); return false; }
                    return ServeStream(s, sid);
                case "DELETE":
                    if (badVersion) { UnsupportedVersion(s, pv); return false; }
                    if (sid == null) { WriteText(s, 400, "Bad Request", "Mcp-Session-Id header required"); return false; }
                    if (!EndSession(sid, "session closed by the client")) { WriteText(s, 404, "Not Found", "unknown or expired session"); return false; }
                    Write(s, 200, "OK", null, null, null); return false;
                default:
                    Write(s, 405, "Method Not Allowed", null, null, AllowAll); return false;
            }
        }

        // POST /cmd, GET /state, GET /ping (ServerPlain): no session; the bearer token rule of the MCP paths applies
        // except to /ping
        bool ServePlain(Req r, Stream s)
        {
            if (ServerPlain.NeedsAuth(r.path))
            {
                int? auth = AuthFailure(r);
                if (auth == 401)
                {
                    Write(s, 401, "Unauthorized", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("unauthorized: send the header Authorization: Bearer <token> (EditorState mcp.token in the game)"),
                        new Dictionary<string, string> { { "WWW-Authenticate", "Bearer realm=\"insidedev\"" } });
                    return false;
                }
                if (auth != null) { WriteText(s, 500, "Internal Server Error", "the server could not read its auth token"); return true; }
            }
            PlainReply p;
            try { p = ServerPlain.Serve(host, r.method, r.path, r.body, LastClient); }
            catch (Exception e)
            {
                Interlocked.Increment(ref Errors);
                LastError = r.path + ": " + e.Message;
                p = new PlainReply { code = 500, reason = "Internal Server Error", body = r.path + " failed: " + e.GetType().Name + ": " + e.Message };
            }
            if (p.code >= 400 || (p.headers != null && p.headers.ContainsKey(ServerPlain.ErrorHeader))) Interlocked.Increment(ref Errors);
            Write(s, p.code, p.reason, p.contentType, Encoding.UTF8.GetBytes(p.body ?? ""), p.headers);
            return false;
        }

        static void UnsupportedVersion(Stream s, string pv)
        {
            WriteRpcError(s, 400, "Bad Request", -32600, "unsupported MCP-Protocol-Version " + pv + "; supported: " + string.Join(", ", Versions), null);
        }

        bool HandlePost(Req r, Stream s, string profile, string sid, string badVersion)
        {
            object msg;
            try { msg = Json.Parse(Encoding.UTF8.GetString(r.body)); }
            catch (Exception e) { WriteRpcError(s, 400, "Bad Request", -32700, "parse error: " + e.Message, null); return false; }
            bool isInit = IsInitialize(msg);
            if (badVersion != null && !isInit && !IsMethod(msg, "server/discover")) { UnsupportedVersion(s, badVersion); return false; }
            if (sid != null && !isInit && !Touch(sid)) { WriteText(s, 404, "Not Found", "unknown or expired session; initialize again"); return false; }
            string newSid = null;
            object reply;
            var list = msg as List<object>;
            if (list != null)
            {
                if (list.Count == 0) { WriteRpcError(s, 400, "Bad Request", -32600, "empty batch", null); return false; }
                var outs = new List<object>();
                foreach (var m in list) { var o = Dispatch(m as Json.Obj, profile, sid, ref newSid, null); if (o != null) outs.Add(o); }
                reply = outs.Count > 0 ? outs : null;
            }
            else
            {
                var m = msg as Json.Obj;
                if (m != null && WantsStream(r, m)) return ServeCallStream(s, m, profile, sid);
                reply = Dispatch(m, profile, sid, ref newSid, null);
            }
            Dictionary<string, string> extra = null;
            if (newSid != null) extra = new Dictionary<string, string> { { "Mcp-Session-Id", newSid } };
            if (reply == null) { Write(s, 202, "Accepted", null, null, extra); return false; }
            Respond(s, reply, extra);
            return false;
        }

        static bool IsInitialize(object msg) { return IsMethod(msg, "initialize"); }

        static bool IsMethod(object msg, string method)
        {
            var o = msg as Json.Obj; if (o != null) return o.Str("method") == method;
            var l = msg as List<object>; if (l != null) foreach (var m in l) { var x = m as Json.Obj; if (x != null && x.Str("method") == method) return true; }
            return false;
        }

        static object ProgressToken(Json.Obj m)
        {
            var p = m["params"] as Json.Obj; if (p == null) return null;
            var meta = p["_meta"] as Json.Obj; if (meta == null) return null;
            var t = meta["progressToken"];
            return t is string || t is double ? t : null;
        }

        static bool WantsStream(Req r, Json.Obj m)
        {
            return m.Str("method") == "tools/call" && m.ContainsKey("id") && ProgressToken(m) != null && Accepts(r, "text/event-stream");
        }

        void Respond(Stream s, object reply, Dictionary<string, string> extra)
        {
            Write(s, 200, "OK", "application/json", Encoding.UTF8.GetBytes(JsonOut.Write(reply)), extra);
        }

        // a tools/call with a progress token: SSE with notifications/progress, then the response; the connection closes
        bool ServeCallStream(Stream s, Json.Obj m, string profile, string sid)
        {
            object token = ProgressToken(m);
            var w = new SseWriter(s);
            w.Begin(sid);
            var plock = new object();
            double last = double.NegativeInfinity;
            bool done = false;
            Action<double, double, string> sink = (progress, total, message) =>
            {
                Json.Obj note;
                lock (plock)
                {
                    if (done || w.Broken) return;
                    if (progress < last) progress = last;
                    last = progress;
                    var np = new Json.Obj(); np["progressToken"] = token; np["progress"] = progress;
                    if (total > 0) np["total"] = total;
                    if (!string.IsNullOrEmpty(message)) np["message"] = message;
                    note = Notification("notifications/progress", np);
                    w.Send(note);
                }
            };
            var ka = new Timer(_ => { try { if (!w.Broken && w.IdleMs >= KeepAliveMs - 1000) w.KeepAlive(); } catch { } }, null, KeepAliveMs, KeepAliveMs);
            object reply;
            string newSid = null;
            try { reply = Dispatch(m, profile, sid, ref newSid, sink); }
            finally { ka.Dispose(); lock (plock) done = true; }
            if (reply != null) w.Send(reply);
            w.Dispose();
            return true;
        }

        // the session's notification stream (GET): list_changed when ListVersion moves, keep-alive comments
        bool ServeStream(Stream s, string sid)
        {
            if (sid == null) { WriteText(s, 400, "Bad Request", "the notification stream needs the Mcp-Session-Id of an initialized session"); return false; }
            Session ses;
            lock (gate) sessions.TryGetValue(sid, out ses);
            if (ses == null) { WriteText(s, 404, "Not Found", "unknown or expired session; initialize again"); return false; }
            var w = new SseWriter(s);
            SseWriter old;
            lock (gate) { old = ses.stream; ses.stream = w; ses.seen = DateTime.UtcNow; }
            if (old != null) old.Close();
            w.Begin(sid);
            w.KeepAlive();
            while (running && !w.Broken)
            {
                bool alive; long v = ListVersion(); bool changed;
                lock (gate)
                {
                    alive = ses.stream == w && sessions.ContainsKey(sid);
                    changed = alive && v != ses.notified;
                    if (changed) ses.notified = v;
                }
                if (!alive) break;
                if (changed) foreach (var n in ListChangedNotes()) w.Send(n);
                else if (w.IdleMs >= KeepAliveMs) w.KeepAlive();
                Thread.Sleep(StreamPollMs);
            }
            lock (gate) if (ses.stream == w) ses.stream = null;
            return true;
        }

        static List<Json.Obj> ListChangedNotes()
        {
            return new List<Json.Obj> { Notification("notifications/tools/list_changed", null), Notification("notifications/resources/list_changed", null), Notification("notifications/prompts/list_changed", null) };
        }

        long ListVersion() { try { return host.ListVersion; } catch { return 0; } }

        // ---------------------------------------------------------------- sessions and running calls
        bool Touch(string sid)
        {
            lock (gate) { Session ses; if (!sessions.TryGetValue(sid, out ses)) return false; ses.seen = DateTime.UtcNow; return true; }
        }

        string NewSession(string profile, string protocol, string client)
        {
            string id = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            var ses = new Session { id = id, profile = profile, protocol = protocol, client = client, created = now, seen = now, notified = ListVersion() };
            var dropped = new List<string>();
            lock (gate)
            {
                foreach (var kv in sessions) if (now - kv.Value.seen > SessionIdle) dropped.Add(kv.Key);
                while (sessions.Count - dropped.Count >= MaxSessions)
                {
                    Session oldest = null;
                    foreach (var x in sessions.Values) if (!dropped.Contains(x.id) && (oldest == null || x.seen < oldest.seen)) oldest = x;
                    if (oldest == null) break;
                    dropped.Add(oldest.id);
                }
                foreach (var d in dropped) { Session x; if (sessions.TryGetValue(d, out x) && x.stream != null) x.stream.Close(); sessions.Remove(d); }
                sessions[id] = ses;
            }
            return id;
        }

        bool EndSession(string sid, string reason)
        {
            int left;
            lock (gate)
            {
                Session ses;
                if (!sessions.TryGetValue(sid, out ses)) return false;
                sessions.Remove(sid);
                if (ses.stream != null) ses.stream.Close();
                string prefix = sid + "\n";
                var keys = new List<string>();
                foreach (var kv in inflight) if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) { kv.Value.Cancel(reason); keys.Add(kv.Key); }
                foreach (var k in keys) inflight.Remove(k);
                left = sessions.Count;
            }
            host.Log("MCP session " + sid.Substring(0, Math.Min(8, sid.Length)) + " ended (" + reason + "), " + left + " left");
            RaiseClosed(sid, left);
            return true;
        }

        void RaiseClosed(string sid, int left)
        {
            var h = SessionClosed;
            if (h == null) return;
            try { h(sid, left); } catch (Exception e) { LastError = e.Message; }
        }

        static string CallKey(string sid, object requestId) { return (sid ?? "") + "\n" + JsonOut.Write(requestId); }

        void Register(string key, CallInfo info)
        {
            lock (gate)
            {
                inflight[key] = info;
                EarlyCancel ec;
                if (earlyCancels.TryGetValue(key, out ec)) { earlyCancels.Remove(key); if ((DateTime.UtcNow - ec.at).TotalMilliseconds <= EarlyCancelMs) info.Cancel(ec.reason); }
            }
        }

        void Unregister(string key, CallInfo info)
        {
            lock (gate) { CallInfo cur; if (inflight.TryGetValue(key, out cur) && ReferenceEquals(cur, info)) inflight.Remove(key); }
        }

        void Cancel(string sid, object requestId, string reason)
        {
            if (requestId == null) return;
            string key = CallKey(sid, requestId);
            bool found;
            lock (gate)
            {
                CallInfo info;
                found = inflight.TryGetValue(key, out info);
                if (found) info.Cancel(reason);
                else
                {
                    var now = DateTime.UtcNow;
                    var stale = new List<string>();
                    foreach (var kv in earlyCancels) if ((now - kv.Value.at).TotalMilliseconds > EarlyCancelMs) stale.Add(kv.Key);
                    foreach (var k in stale) earlyCancels.Remove(k);
                    if (earlyCancels.Count < 1000) earlyCancels[key] = new EarlyCancel { at = now, reason = reason };
                }
            }
            host.Log("MCP cancel of request " + JsonOut.Write(requestId) + (found ? "" : " (not running)") + ": " + reason);
        }

        // ---------------------------------------------------------------- JSON-RPC
        static Json.Obj Error(object id, int code, string message) { return Error(id, code, message, null); }
        static Json.Obj Error(object id, int code, string message, Json.Obj data)
        {
            var e = new Json.Obj(); e["code"] = (double)code; e["message"] = message;
            if (data != null) e["data"] = data;
            var o = new Json.Obj(); o["jsonrpc"] = "2.0"; o["id"] = id; o["error"] = e;
            return o;
        }
        static Json.Obj Result(object id, object result) { var o = new Json.Obj(); o["jsonrpc"] = "2.0"; o["id"] = id; o["result"] = result; return o; }
        static Json.Obj Notification(string method, Json.Obj p) { var o = new Json.Obj(); o["jsonrpc"] = "2.0"; o["method"] = method; if (p != null) o["params"] = p; return o; }

        static Json.Obj RpcError(object id, McpException e, int notFoundCode)
        {
            int code = e.code == McpErr.NotFound ? notFoundCode : e.code == McpErr.BadArgs ? -32602 : -32603;
            var d = new Json.Obj(); d["code"] = e.code; if (e.hint != null) d["hint"] = e.hint;
            return Error(id, code, e.Message, d);
        }

        // returns null for notifications and client responses
        public object Dispatch(Json.Obj m, string profile, string sid, ref string newSid) { return Dispatch(m, profile, sid, ref newSid, null); }

        internal object Dispatch(Json.Obj m, string profile, string sid, ref string newSid, Action<double, double, string> progress)
        {
            if (m == null) return Error(null, -32600, "invalid request");
            string method = m.Str("method");
            object id = m["id"];
            if (method == null) return null;
            if (!m.ContainsKey("id")) { OnNotification(method, m["params"] as Json.Obj ?? new Json.Obj(), sid); return null; }
            var p = m["params"] as Json.Obj ?? new Json.Obj();
            try
            {
                switch (method)
                {
                    case "initialize": return Initialize(id, p, profile, ref newSid);
                    case "ping": return Result(id, new Json.Obj());
                    case "tools/list": return ToolsList(id, profile);
                    case "tools/call": return CallTool(id, p, profile, sid, progress);
                    case "resources/list":
                        {
                            var arr = new List<object>();
                            foreach (var d in host.Resources(profile) ?? new List<ResourceDef>()) if (d != null) arr.Add(d.ToJson());
                            var res = new Json.Obj(); res["resources"] = arr; return Result(id, res);
                        }
                    case "resources/templates/list":
                        {
                            var arr = new List<object>();
                            foreach (var d in host.ResourceTemplates(profile) ?? new List<ResourceTemplateDef>()) if (d != null) arr.Add(d.ToJson());
                            var res = new Json.Obj(); res["resourceTemplates"] = arr; return Result(id, res);
                        }
                    case "resources/read": return ReadResource(id, p, profile, sid);
                    case "prompts/list":
                        {
                            var arr = new List<object>();
                            foreach (var d in host.Prompts(profile) ?? new List<PromptDef>()) if (d != null) arr.Add(d.ToJson());
                            var res = new Json.Obj(); res["prompts"] = arr; return Result(id, res);
                        }
                    case "prompts/get": return GetPrompt(id, p, profile, sid);
                    default: return Error(id, -32601, "method not found: " + method);
                }
            }
            catch (McpException e) { Interlocked.Increment(ref Errors); return RpcError(id, e, -32603); }
            catch (Exception e) { Interlocked.Increment(ref Errors); LastError = method + ": " + e.Message; return Error(id, -32603, method + " failed: " + e.GetType().Name + ": " + e.Message); }
        }

        void OnNotification(string method, Json.Obj p, string sid)
        {
            if (method == "notifications/initialized") host.Log("MCP client initialized (" + LastClient + ")");
            else if (method == "notifications/cancelled") Cancel(sid, p["requestId"], p.Str("reason", "cancelled by the client"));
        }

        Json.Obj Initialize(object id, Json.Obj p, string profile, ref string newSid)
        {
            string want = p.Str("protocolVersion", Versions[0]);
            string ver = KnownVersion(want) ? want : Versions[0];
            var info = p["clientInfo"] as Json.Obj;
            string client = info != null ? info.Str("name", "?") + " " + info.Str("version", "") : "?";
            newSid = NewSession(profile, ver, client);
            host.Log("MCP initialize from " + client + " on " + profile + " (protocol " + want + " -> " + ver + ")");
            var caps = new Json.Obj();
            caps["tools"] = ListChangedCap();
            caps["resources"] = ListChangedCap();
            caps["prompts"] = ListChangedCap();
            var si = new Json.Obj(); si["name"] = "insidedev"; si["title"] = "InsideDev (INSIDE runtime editor)"; si["version"] = host.ServerVersion;
            var res = new Json.Obj(); res["protocolVersion"] = ver; res["capabilities"] = caps; res["serverInfo"] = si;
            string text = host.Instructions(profile);
            if (!string.IsNullOrEmpty(text)) res["instructions"] = text;
            return Result(id, res);
        }

        static Json.Obj ListChangedCap() { var o = new Json.Obj(); o["listChanged"] = true; return o; }

        Json.Obj ToolsList(object id, string profile)
        {
            var arr = new List<object>();
            foreach (var t in host.Tools(profile) ?? new List<ToolDef>()) if (t != null && host.Allowed(profile, t.name)) arr.Add(t.ToJson());
            var res = new Json.Obj(); res["tools"] = arr; return Result(id, res);
        }

        ToolDef FindTool(string profile, string name)
        {
            foreach (var t in host.Tools(profile) ?? new List<ToolDef>()) if (t != null && t.name == name) return host.Allowed(profile, name) ? t : null;
            return null;
        }

        string WhereIs(string profile, string name)
        {
            foreach (var t in host.Tools(McpProfile.All) ?? new List<ToolDef>())
            {
                if (t == null || t.name != name) continue;
                if (McpProfile.Includes(profile, t.profiles)) return "; it is disabled by the game's tool allowlist (EditorState mcp.tools)";
                return "; it is not in the " + profile + " profile, it is served on " + ((t.profiles & McpProfiles.Dev) != 0 ? Path + "/dev" : Path + "/play") + " and " + Path + "/all";
            }
            return "; tools/list shows the tools of this endpoint";
        }

        static Json.Obj Arguments(Json.Obj p)
        {
            var a = p["arguments"];
            if (a == null) return new Json.Obj();
            var o = a as Json.Obj;
            if (o != null) return o;
            var str = a as string;
            if (str != null) { try { o = Json.Parse(str.Trim().Length == 0 ? "{}" : str) as Json.Obj; } catch { o = null; } }
            return o;
        }

        Json.Obj CallTool(object id, Json.Obj p, string profile, string sid, Action<double, double, string> progress)
        {
            string name = p.Str("name");
            if (string.IsNullOrEmpty(name)) return Error(id, -32602, "tools/call needs params.name");
            var def = FindTool(profile, name);
            if (def == null) return Error(id, -32602, "unknown tool: " + name + WhereIs(profile, name));
            Interlocked.Increment(ref ToolCalls);
            ToolResult tr;
            string verr;
            var args = Arguments(p);
            var coerced = new List<string>();
            if (args == null) tr = ToolResult.Fail(McpErr.BadArgs, "arguments: expected an object", "pass the arguments as a JSON object");
            else if (!Schema.Validate(def.inputSchema, args, out verr, coerced)) tr = ToolResult.Fail(McpErr.BadArgs, verr, "see the inputSchema of " + name + " in tools/list");
            else
            {
                var info = new CallInfo { profile = profile, tool = name, sessionId = sid ?? "", requestId = id, client = LastClient, budgetMs = def.timeoutMs > 0 ? def.timeoutMs : 55000 };
                var pm = p["_meta"] as Json.Obj;
                if (pm != null) info.progressToken = pm["progressToken"];
                if (info.progressToken != null) info.progress = progress;
                string key = CallKey(sid, id);
                Register(key, info);
                try { tr = host.Call(def, args, info) ?? ToolResult.Fail(McpErr.Internal, name + " returned nothing"); }
                catch (McpException e) { tr = ToolResult.Fail(e); }
                catch (Exception e) { tr = ToolResult.Fail(McpErr.Internal, name + " failed: " + e.GetType().Name + ": " + e.Message); }
                finally { Unregister(key, info); }
            }
            if (coerced.Count > 0) AddWarnings(def, tr, coerced);
            CheckOutput(def, tr);
            if (tr.isError) Interlocked.Increment(ref Errors);
            var res = tr.ToJson();
            if (def.outputSchema == null && HasImage(res)) { res.Remove("structuredContent"); res.order.Remove("structuredContent"); }
            return Result(id, res);
        }

        // a tool without an outputSchema that returns an image goes out as content only (the JSON text block, then the
        // image): Claude Code passes only structuredContent to the model when it is present, which would drop the image
        static bool HasImage(Json.Obj res)
        {
            var l = res["content"] as List<object>;
            if (l != null) foreach (var c in l) { var o = c as Json.Obj; if (o != null && o.Str("type") == "image") return true; }
            return false;
        }

        // notes go first into structured.warnings (kept at 20) when the output schema allows a warnings list
        static void AddWarnings(ToolDef def, ToolResult tr, List<string> notes)
        {
            if (tr.structured == null || notes.Count == 0) return;
            var props = def.outputSchema != null ? def.outputSchema["properties"] as Json.Obj : null;
            if (props != null && !props.ContainsKey("warnings")) return;
            var w = tr.structured["warnings"] as List<object>;
            if (w == null) { if (tr.structured["warnings"] != null) return; w = new List<object>(); tr.structured["warnings"] = w; }
            for (int i = notes.Count - 1; i >= 0; i--) w.Insert(0, notes[i]);
            if (w.Count > 20) w.RemoveRange(20, w.Count - 20);
        }

        // clients such as the TypeScript SDK reject a structuredContent that does not match the tool's outputSchema:
        // the offending properties are removed (Schema.Repair) and named in warnings, and the first mismatch is logged
        void CheckOutput(ToolDef def, ToolResult tr)
        {
            if (def.outputSchema == null || tr.structured == null) return;
            string problem;
            bool ok;
            var fixes = new List<string>();
            try { ok = Schema.Repair(def.outputSchema, tr.structured, fixes, out problem); }
            catch (Exception e) { ok = false; problem = e.GetType().Name + ": " + e.Message; }
            if (fixes.Count > 0)
            {
                var notes = new List<string>();
                foreach (var f in fixes) notes.Add("schema_repair: removed " + f);
                AddWarnings(def, tr, notes);
                string again;
                if (!Schema.Conform(def.outputSchema, tr.structured, out again))
                {
                    var w = tr.structured["warnings"] as List<object>;
                    if (w != null) w.RemoveAll(x => x is string && ((string)x).StartsWith("schema_repair: ", StringComparison.Ordinal));
                }
            }
            if (ok && fixes.Count == 0) return;
            if (problem == null && fixes.Count > 0) problem = fixes[0];
            string what = def.name + ": " + problem;
            lock (gate) { if (warned.Count >= MaxWarned || !warned.Add(what)) return; }
            LastError = "output of " + what;
            host.Log("MCP output of " + def.name + " does not match its outputSchema (" + problem + ")" + (ok ? "; removed, the result now conforms" : ""));
        }

        CallInfo Info(string profile, string what, string sid, object id)
        {
            return new CallInfo { profile = profile, tool = what, sessionId = sid ?? "", requestId = id, client = LastClient, budgetMs = 55000 };
        }

        Json.Obj ReadResource(object id, Json.Obj p, string profile, string sid)
        {
            string uri = p.Str("uri");
            if (string.IsNullOrEmpty(uri)) return Error(id, -32602, "resources/read needs params.uri");
            var info = Info(profile, "resources/read", sid, id);
            string key = CallKey(sid, id);
            Register(key, info);
            ResourceContents rc;
            try { rc = host.ReadResource(uri, info); }
            catch (McpException e) { return RpcError(id, e, -32002); }
            finally { Unregister(key, info); }
            if (rc == null) { var d = new Json.Obj(); d["uri"] = uri; return Error(id, -32002, "resource not found: " + uri, d); }
            if (string.IsNullOrEmpty(rc.uri)) rc.uri = uri;
            var res = new Json.Obj(); res["contents"] = new List<object> { rc.ToJson() };
            return Result(id, res);
        }

        Json.Obj GetPrompt(object id, Json.Obj p, string profile, string sid)
        {
            string name = p.Str("name");
            if (string.IsNullOrEmpty(name)) return Error(id, -32602, "prompts/get needs params.name");
            PromptDef def = null;
            foreach (var d in host.Prompts(profile) ?? new List<PromptDef>()) if (d != null && d.name == name) { def = d; break; }
            if (def == null) return Error(id, -32602, "unknown prompt: " + name);
            var args = p["arguments"] as Json.Obj ?? new Json.Obj();
            foreach (var a in def.arguments)
                if (a != null && a.required && string.IsNullOrEmpty(args.Str(a.name))) return Error(id, -32602, "prompt " + name + " needs the argument " + a.name);
            var info = Info(profile, "prompts/get", sid, id);
            string key = CallKey(sid, id);
            Register(key, info);
            PromptResult pr;
            try { pr = host.GetPrompt(name, args, info); }
            catch (McpException e) { return RpcError(id, e, -32602); }
            finally { Unregister(key, info); }
            if (pr == null) return Error(id, -32602, "unknown prompt: " + name);
            return Result(id, pr.ToJson());
        }
    }

    // McpBuild (owner SERVER; here so the DLL, the stdio exe and the pure tests compile it): the version of this MCP
    // build and a build id = the first 8 hex digits of the SHA-256 of the assembly file (the same digits as the build's
    // hashes.txt), so a client can tell which build answers. The core's Boot.Version is reported next to it, unchanged.
    public static class McpBuild
    {
        public const string Version = "0.9.0";
        static readonly Dictionary<string, string> ids = new Dictionary<string, string>();

        // the build id of an assembly file ("unknown" when it has no file or cannot be read); cached per path
        public static string IdOf(System.Reflection.Assembly a)
        {
            string path = null;
            try { path = a != null ? a.Location : null; } catch (Exception) { path = null; }
            if (string.IsNullOrEmpty(path)) return "unknown";
            lock (ids)
            {
                string id;
                if (ids.TryGetValue(path, out id)) return id;
                id = FileId(path);
                ids[path] = id;
                return id;
            }
        }

        public static string FileId(string path)
        {
            try
            {
                byte[] h;
                using (var s = System.IO.File.OpenRead(path))
                using (var sha = new System.Security.Cryptography.SHA256Managed()) h = sha.ComputeHash(s);
                return Hex(h, 4);
            }
            catch (Exception) { return "unknown"; }
        }

        public static string Hex(byte[] b, int n)
        {
            var sb = new StringBuilder(n * 2);
            for (int i = 0; i < n && b != null && i < b.Length; i++) sb.Append(b[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // "<version>+<id>" of an assembly
        public static string Of(System.Reflection.Assembly a) { return Version + "+" + IdOf(a); }

        // this build: the assembly McpBuild is compiled into (InsideDev.Mcp.dll in the game, InsideDev.McpStdio.exe)
        public static string Self { get { return Of(typeof(McpBuild).Assembly); } }

        // the server version string: <mcp version>+mcp.<id>[.play.<id>] (semver build metadata; play when loaded)
        public static string ServerVersion(string playId)
        {
            return Version + "+mcp." + IdOf(typeof(McpBuild).Assembly) + (string.IsNullOrEmpty(playId) ? "" : ".play." + playId);
        }
    }

    // compact JSON writer (one line; MCP stdio framing needs that)
    public static class JsonOut
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public static string Write(object v) { var sb = new StringBuilder(); W(sb, v); return sb.ToString(); }
        static void W(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            var s = v as string; if (s != null) { Q(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is double)
            {
                double d = (double)v;
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(IC) : d.ToString("R", IC)); return;
            }
            if (v is int || v is long || v is float) { W(sb, Convert.ToDouble(v, IC)); return; }
            var o = v as Json.Obj;
            if (o != null)
            {
                sb.Append('{'); bool first = true;
                foreach (var k in o.order) { if (!o.ContainsKey(k)) continue; if (!first) sb.Append(','); first = false; Q(sb, k); sb.Append(':'); W(sb, o[k]); }
                sb.Append('}'); return;
            }
            var d2 = v as Dictionary<string, object>;
            if (d2 != null) { sb.Append('{'); bool first = true; foreach (var kv in d2) { if (!first) sb.Append(','); first = false; Q(sb, kv.Key); sb.Append(':'); W(sb, kv.Value); } sb.Append('}'); return; }
            var l = v as System.Collections.IEnumerable;
            if (l != null) { sb.Append('['); bool first = true; foreach (var x in l) { if (!first) sb.Append(','); first = false; W(sb, x); } sb.Append(']'); return; }
            Q(sb, Convert.ToString(v, IC));
        }
        static void Q(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            sb.Append('"');
        }
    }

    // ServerPlain (owner SERVER; in this file so every build and pure test that lists the SERVER files compiles it): plain
    // local HTTP next to the MCP endpoints, for scripts and people without an MCP client or session
    // (docs/MCP_PLAY_DESIGN.md section 19). Same listener (127.0.0.1 only, port 47811), the same
    // Origin check and the same bearer token rule as the MCP paths (McpServer.Handle; /ping needs no token):
    //   POST /cmd    text body = one console / bridge command (ctl ..., boypos, mcam ...); it runs on the main thread
    //                through the host's command tool (HostDispatch: allowlist, refusals, audit, 55 s budget) and the
    //                reply is its text (text/plain). A failed command still answers 200 with header X-InsideDev-Error:
    //                <code>; 400 empty body / more than one line, 413 longer than MaxCommandChars, 403 not allowed.
    //   GET /state   the concise observe JSON (the observe tool with its defaults); 503 with the error JSON when observe
    //                fails (no game world, not ready).
    //   GET /ping    "pong InsideDev <version>".
    // Unity-free and InsideDev-free except Json, like the rest of this file (the stdio proxy never listens).
    public sealed class PlainReply
    {
        public int code = 200;
        public string reason = "OK", contentType = "text/plain; charset=utf-8", body = "";
        public Dictionary<string, string> headers;
    }

    public static class ServerPlain
    {
        public const string Cmd = "/cmd", State = "/state", Ping = "/ping", ErrorHeader = "X-InsideDev-Error";
        public const int MaxCommandChars = 8192, MaxReplyChars = 200000;

        static string Norm(string path) { return path == null || path == "/" ? path : path.TrimEnd('/'); }

        public static bool Owns(string path)
        {
            string p = Norm(path);
            return p == Cmd || p == State || p == Ping;
        }

        public static bool NeedsAuth(string path) { return Norm(path) != Ping; }

        public const string Endpoints = "plain: POST /cmd (one console command, text), GET /state (observe JSON), GET /ping";

        public static PlainReply Serve(IToolHost host, string method, string path, byte[] body, string client)
        {
            string p = Norm(path);
            if (p == Ping) return method == "GET" ? Text(200, "OK", "pong InsideDev " + Version(host)) : NotAllowed("GET");
            if (p == State) return method == "GET" ? StateReply(host, client) : NotAllowed("GET");
            if (p == Cmd) return method == "POST" ? CmdReply(host, body, client) : NotAllowed("POST");
            return Text(404, "Not Found", Endpoints);
        }

        static string Version(IToolHost host)
        {
            try { return host.ServerVersion ?? "?"; } catch (Exception) { return "?"; }
        }

        static PlainReply Text(int code, string reason, string text)
        {
            return new PlainReply { code = code, reason = reason, body = text ?? "" };
        }

        static PlainReply NotAllowed(string allow)
        {
            var r = Text(405, "Method Not Allowed", "use " + allow);
            r.headers = new Dictionary<string, string> { { "Allow", allow } };
            return r;
        }

        static ToolDef Find(IToolHost host, string profile, string name)
        {
            var l = host.Tools(profile);
            if (l != null) foreach (var t in l) if (t != null && t.name == name) return t;
            return null;
        }

        static PlainReply CmdReply(IToolHost host, byte[] body, string client)
        {
            string line = Encoding.UTF8.GetString(body ?? new byte[0]);
            if (line.Length > 0 && line[0] == '\uFEFF') line = line.Substring(1);
            line = line.Trim();
            if (line.Length == 0) return Text(400, "Bad Request", "POST /cmd needs one console command as the text body, e.g. ctl status");
            if (line.Length > MaxCommandChars) return Text(413, "Payload Too Large", "a command is at most " + MaxCommandChars + " characters");
            if (line.IndexOf('\n') >= 0 || line.IndexOf('\r') >= 0) return Text(400, "Bad Request", "one command per request (the body has a line break)");
            var def = Find(host, McpProfile.Dev, "command");
            if (def == null) return Text(503, "Service Unavailable", "the command tool is not served (InsideDev.Mcp has no dev tools loaded)");
            if (!host.Allowed(McpProfile.Dev, "command")) return Text(403, "Forbidden", "the command tool is not in the tool allowlist (EditorState mcp.tools)");
            var args = new Json.Obj();
            args["command"] = line;
            args["max_chars"] = (double)MaxReplyChars;
            var tr = Call(host, def, args, "http " + Cmd, client);
            var s = tr.structured;
            string text = s != null ? s.Str("text", null) : null;
            var r = Text(200, "OK", text);
            if (tr.isError)
            {
                var e = s != null ? s["error"] as Json.Obj : null;
                string code = e != null ? e.Str("code", McpErr.Internal) : McpErr.Internal;
                if (text == null) r.body = "error " + code + ": " + (e != null ? e.Str("message", "") : "failed") + (e != null && e.Str("hint", null) != null ? "\nhint: " + e.Str("hint") : "");
                r.headers = new Dictionary<string, string> { { ErrorHeader, code } };
            }
            else if (text == null) r.body = s != null ? JsonOut.Write(s) : "done (no output)";
            if (s != null && s.Bool("truncated")) { if (r.headers == null) r.headers = new Dictionary<string, string>(); r.headers["X-InsideDev-Truncated"] = "1"; }
            return r;
        }

        static PlainReply StateReply(IToolHost host, string client)
        {
            var def = Find(host, McpProfile.Dev, "observe");
            if (def == null) return Text(503, "Service Unavailable", "the observe tool is not served (InsideDev.Play is not loaded)");
            if (!host.Allowed(McpProfile.Dev, "observe")) return Text(403, "Forbidden", "the observe tool is not in the tool allowlist (EditorState mcp.tools)");
            var tr = Call(host, def, new Json.Obj(), "http " + State, client);
            var s = tr.structured;
            var r = new PlainReply { contentType = "application/json", body = s != null ? JsonOut.Write(s) : "{\"ok\":false}" };
            if (tr.isError) { r.code = 503; r.reason = "Service Unavailable"; }
            return r;
        }

        static ToolResult Call(IToolHost host, ToolDef def, Json.Obj args, string what, string client)
        {
            var info = new CallInfo { profile = McpProfile.Dev, tool = def.name, client = what + (string.IsNullOrEmpty(client) ? "" : " " + client), budgetMs = def.timeoutMs > 0 ? def.timeoutMs : 55000 };
            try { return host.Call(def, args, info) ?? ToolResult.Fail(McpErr.Internal, def.name + " returned nothing"); }
            catch (McpException e) { return ToolResult.Fail(e); }
            catch (Exception e) { return ToolResult.Fail(McpErr.Internal, def.name + " failed: " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
