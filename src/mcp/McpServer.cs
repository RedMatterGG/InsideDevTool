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
    // and the stdio proxy compiles this file too. Listens on 127.0.0.1 (and [::1] when the system has IPv6) only;
    // requests run on one worker thread per connection. Behaviour (docs/MCP_PLAY_DESIGN.md section 4.6):
    //   - profiles by path (McpProfile.FromPath): /mcp and /mcp/dev = dev, /mcp/play = play, /mcp/all = all. tools/list
    //     depends only on the path (and the host's allowlist), never on the connection.
    //   - POST: JSON-RPC (batches accepted, no streaming inside a batch). tools/call validates arguments strictly
    //     (Schema.Validate, defaults filled); a schema failure is an isError result with code bad_args. A tools/call with
    //     params._meta.progressToken from a client that accepts text/event-stream is answered as SSE: progress events,
    //     the response, then the connection closes. Other requests get one application/json reply.
    //   - notifications/cancelled {requestId, reason} cancels the CallInfo of (session, requestId); a cancel that
    //     arrives before its request is kept for 10 s. A tools/call whose client closed the connection is cancelled too.
    //   - GET with Accept: text/event-stream opens the session's notification stream: tools / resources / prompts
    //     list_changed whenever IToolHost.ListVersion moves, keep-alive comment every 15 s. DELETE ends the session, its
    //     stream and its running calls.
    //   - who may call (every path): a Host header must name 127.0.0.1, localhost or [::1] (any port; ExtraHosts adds
    //     names; a request without Host is accepted, browsers always send one), an Origin header must be a page of one of
    //     those three hosts ("null" and every other origin get 403), Sec-Fetch-Site: cross-site gets 403, and a POST to
    //     an MCP path with a Content-Type header must name application/json (415; no header is accepted). Together these
    //     stop pages of web sites, sandboxed frames and DNS rebinding; programs on this machine send none of the browser
    //     headers and are not affected. Pages served from this machine (http://localhost:<any port>) are trusted like
    //     local programs; mcp.token makes every caller, those pages included, show the token.
    //   - an unknown MCP-Protocol-Version header gets HTTP 400 (not checked on initialize and server/discover, so newer
    //     clients can fall back); an unknown Mcp-Session-Id gets 404; when IToolHost.AuthToken is set, every MCP request
    //     needs "Authorization: Bearer <token>" (401 otherwise). GET / and /health stay plain text.
    //   - plain endpoints without a session (ServerPlain below): POST /cmd (one console command, text reply), POST /cmds
    //     (several, JSON reply), GET /state (concise observe JSON), GET /status (server and game health as JSON, never
    //     waits for the game's main thread), GET /ping; the token rule applies to all of them except /ping.
    public sealed class McpServer
    {
        public static readonly string[] Versions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
        public const string Path = "/mcp";

        const int KeepAliveMs = 15000, StreamPollMs = 250, MaxSessions = 128, EarlyCancelMs = 10000, MaxWarned = 200;
        const int MaxBody = 16 * 1024 * 1024, MaxHeaders = 200, MaxLine = 65536, ListCacheMs = 10000;
        static readonly TimeSpan SessionIdle = TimeSpan.FromHours(6);
        // A session counts as live while a call of it runs, its notification stream is open, or (a session that never
        // had a stream) it sent a request in the last LiveWindow. A session whose stream ended counts for StreamGrace
        // after its last sign of life: a client that was killed drops out, one that reconnects stays. Liveness only
        // feeds the host's restore decisions and the status; the session id itself stays valid for SessionIdle.
        internal static TimeSpan LiveWindow = TimeSpan.FromMinutes(15), StreamGrace = TimeSpan.FromSeconds(30);
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly byte[] Continue100 = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");

        readonly IToolHost host;
        TcpListener listener, listener6;
        volatile bool running;
        readonly object gate = new object();
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly Dictionary<string, CallInfo> inflight = new Dictionary<string, CallInfo>();
        readonly Dictionary<string, EarlyCancel> earlyCancels = new Dictionary<string, EarlyCancel>();
        readonly Dictionary<string, CachedList> lists = new Dictionary<string, CachedList>();
        readonly HashSet<string> warned = new HashSet<string>();
        readonly List<TcpClient> clients = new List<TcpClient>();
        DateTime lastAuthLog = DateTime.MinValue, lastRefusalLog = DateTime.MinValue;
        public int Port { get; private set; }
        public int Requests, ToolCalls, Errors, Refused;
        public string LastClient = "";
        public string LastError = "";
        // host names (lower case, no port: McpServer.HostName) accepted in the Host header next to the three local ones
        public volatile string[] ExtraHosts;
        // more fields for GET /status; called on a connection thread, so it must not wait for the game's main thread
        public volatile Func<Json.Obj> StatusSource;

        // raised (on a worker thread) when a session ends by DELETE, by pruning or by Stop: session id and the sessions left
        internal event Action<string, int> SessionClosed;
        internal int SessionCount { get { lock (gate) return sessions.Count; } }
        internal int CallsRunning { get { lock (gate) return inflight.Count; } }
        public bool Ipv6 { get { return listener6 != null; } }

        sealed class Session
        {
            public string id, profile, protocol, client;
            public DateTime created, seen, streamLost;
            public long notified;
            public SseWriter stream;
            public bool hadStream;
            public int calls, requests;
        }

        sealed class EarlyCancel { public DateTime at; public string reason; }
        sealed class CachedList { public long version; public DateTime at; public JsonRaw json; }

        sealed class HttpError : Exception
        {
            public readonly int status;
            public readonly string reason;
            public HttpError(int status, string reason, string message) : base(message) { this.status = status; this.reason = reason; }
        }

        public McpServer(IToolHost host) { this.host = host; }
        public bool Running { get { return running; } }

        public void Start(int port)
        {
            Stop();
            Port = port;
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            listener = l;
            running = true;
            Accept(l, "InsideDev MCP accept");
            try
            {
                var l6 = new TcpListener(IPAddress.IPv6Loopback, port);
                l6.Start();
                listener6 = l6;
                Accept(l6, "InsideDev MCP accept v6");
            }
            catch (Exception) { listener6 = null; }
            host.Log("MCP server on http://127.0.0.1:" + port + Path + " (also " + Path + "/play, " + Path + "/all" + (listener6 != null ? "; [::1] too" : "") + ")");
        }

        void Accept(TcpListener l, string name)
        {
            new Thread(() => AcceptLoop(l)) { IsBackground = true, Name = name }.Start();
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
            try { if (listener6 != null) listener6.Stop(); } catch { }
            listener = null;
            listener6 = null;
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

        // ends when its listener was stopped or replaced
        void AcceptLoop(TcpListener l)
        {
            while (running && (ReferenceEquals(l, listener) || ReferenceEquals(l, listener6)))
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch { if (!running) return; Thread.Sleep(50); continue; }
                var t = new Thread(() => Serve(c)) { IsBackground = true, Name = "InsideDev MCP conn" };
                t.Start();
            }
        }

        // ---------------------------------------------------------------- HTTP
        // One connection: buffered reads (headers arrive in one or two packets, not one receive call per byte); writes go
        // straight to the stream.
        sealed class Conn
        {
            public readonly Socket sock;
            public readonly Stream s;
            readonly byte[] buf = new byte[4096];
            readonly StringBuilder line = new StringBuilder(128);
            int pos, len;

            public Conn(Socket sock, Stream s) { this.sock = sock; this.s = s; }

            bool Fill()
            {
                pos = 0; len = 0;
                int k = s.Read(buf, 0, buf.Length);
                if (k <= 0) return false;
                len = k;
                return true;
            }

            // one header line without its line break; null at the end of the stream before any byte of a line
            public string ReadLine()
            {
                line.Length = 0;
                bool any = false;
                while (true)
                {
                    if (pos >= len && !Fill()) return any ? line.ToString() : null;
                    while (pos < len)
                    {
                        byte b = buf[pos++];
                        any = true;
                        if (b == (byte)'\n')
                        {
                            if (line.Length > 0 && line[line.Length - 1] == '\r') line.Length--;
                            return line.ToString();
                        }
                        line.Append((char)b);
                        if (line.Length > MaxLine) throw new HttpError(431, "Request Header Fields Too Large", "header line too long");
                    }
                }
            }

            public void ReadExact(byte[] dst, int off, int n)
            {
                int have = Math.Min(n, len - pos);
                if (have > 0) { Buffer.BlockCopy(buf, pos, dst, off, have); pos += have; off += have; n -= have; }
                while (n > 0)
                {
                    int k = s.Read(dst, off, n);
                    if (k <= 0) throw new IOException("connection closed");
                    off += k; n -= k;
                }
            }

            // true when the peer closed its side and nothing it sent is left unread (connection thread only)
            public bool PeerClosed()
            {
                if (pos < len) return false;
                try { return sock.Poll(0, SelectMode.SelectRead) && sock.Available == 0; }
                catch (Exception) { return true; }
            }

            // PeerClosed as a "client went away" test, or null when it cannot be trusted here: asked right after a
            // request arrived, a peer that already reads as closed either half-closed its side or the socket poll of
            // this runtime is not usable, and in both cases a closed read side must not cancel anything
            public Func<bool> GoneTest()
            {
                if (PeerClosed()) return null;
                return PeerClosed;
            }
        }

        sealed class Req
        {
            public string method, path, query = "", version = "HTTP/1.1", client = "";
            public readonly Dictionary<string, string> h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public byte[] body;
            public Conn conn;
            // the reply says Connection: close and the connection ends after it
            public bool close;
            // the client may half-close after sending (Connection: close, HTTP/1.0): a closed read side is then no sign
            // that it stopped waiting for the reply
            public bool mayHalfClose;
            public string H(string name) { string v; return h.TryGetValue(name, out v) ? v : null; }
        }

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
                    var conn = new Conn(c.Client, s);
                    while (running)
                    {
                        Req r;
                        try { r = ReadRequest(conn); }
                        catch (HttpError e)
                        {
                            Interlocked.Increment(ref Errors);
                            LastError = "bad request: " + e.Message;
                            WriteText(new Req { conn = conn, close = true }, e.status, e.reason, e.Message);
                            return;
                        }
                        if (r == null) return;
                        if (Handle(r) || r.close) return;
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
            catch (Exception e) { if (running) { LastError = e.Message; host.Log("MCP connection: " + e.Message); } }
            finally { lock (clients) clients.Remove(c); }
        }

        static Req ReadRequest(Conn conn)
        {
            string first = conn.ReadLine();
            while (first != null && first.Length == 0) first = conn.ReadLine();
            if (first == null) return null;
            var p = first.Split(' ');
            if (p.Length < 2 || p[0].Length == 0 || p[1].Length == 0) throw new HttpError(400, "Bad Request", "bad request line");
            var r = new Req { conn = conn, method = p[0].ToUpperInvariant(), path = p[1] };
            if (p.Length > 2 && p[2].Trim().Length > 0) r.version = p[2].Trim().ToUpperInvariant();
            int q = r.path.IndexOf('?');
            if (q >= 0) { r.query = r.path.Substring(q + 1); r.path = r.path.Substring(0, q); }
            string line;
            int headers = 0;
            while ((line = conn.ReadLine()) != null && line.Length > 0)
            {
                if (++headers > MaxHeaders) throw new HttpError(431, "Request Header Fields Too Large", "more than " + MaxHeaders + " header lines");
                int c = line.IndexOf(':'); if (c <= 0) continue;
                r.h[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
            }
            if (line == null) throw new IOException("connection closed inside the headers");
            string cv = r.H("Connection");
            bool http10 = r.version == "HTTP/1.0";
            r.close = cv != null ? cv.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0 : http10;
            if (http10 && cv != null && cv.IndexOf("keep-alive", StringComparison.OrdinalIgnoreCase) < 0) r.close = true;
            r.mayHalfClose = r.close || http10;
            r.client = r.H("User-Agent") ?? "";
            string te = r.H("Transfer-Encoding"), cl = r.H("Content-Length");
            bool chunked = te != null && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;
            long n = 0;
            if (!chunked && cl != null)
            {
                if (!long.TryParse(cl.Trim(), NumberStyles.None, IC, out n)) throw new HttpError(400, "Bad Request", "bad Content-Length");
                if (n > MaxBody) throw new HttpError(413, "Payload Too Large", "the body is larger than " + (MaxBody / (1024 * 1024)) + " MB");
            }
            string expect = r.H("Expect");
            if (expect != null && expect.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0 && (chunked || n > 0))
            {
                conn.s.Write(Continue100, 0, Continue100.Length);
                conn.s.Flush();
            }
            if (chunked)
            {
                var ms = new MemoryStream();
                while (true)
                {
                    string sz = conn.ReadLine();
                    if (sz == null) throw new IOException("connection closed inside a chunked body");
                    int semi = sz.IndexOf(';'); if (semi >= 0) sz = sz.Substring(0, semi);
                    int k;
                    if (!int.TryParse(sz.Trim(), NumberStyles.AllowHexSpecifier, IC, out k)) throw new HttpError(400, "Bad Request", "bad chunk size");
                    if (k == 0) { while ((line = conn.ReadLine()) != null && line.Length > 0) { } break; }
                    if (k < 0 || ms.Length + k > MaxBody) throw new HttpError(413, "Payload Too Large", "the body is larger than " + (MaxBody / (1024 * 1024)) + " MB");
                    var b = new byte[k];
                    conn.ReadExact(b, 0, k);
                    ms.Write(b, 0, k);
                    conn.ReadLine();
                }
                r.body = ms.ToArray();
            }
            else if (n > 0) { r.body = new byte[(int)n]; conn.ReadExact(r.body, 0, (int)n); }
            else r.body = NoBody;
            return r;
        }

        static readonly byte[] NoBody = new byte[0];

        static void Write(Req r, int code, string reason, string contentType, byte[] body, Dictionary<string, string> extra)
        {
            int n = body != null ? body.Length : 0;
            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.1 ").Append(code.ToString(IC)).Append(' ').Append(reason).Append("\r\n");
            if (contentType != null) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(n.ToString(IC)).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            if (r.close) sb.Append("Connection: close\r\n");
            if (extra != null) foreach (var kv in extra) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            sb.Append("\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            var s = r.conn.s;
            if (n > 0 && n <= 65536)
            {
                var one = new byte[head.Length + n];
                Buffer.BlockCopy(head, 0, one, 0, head.Length);
                Buffer.BlockCopy(body, 0, one, head.Length, n);
                s.Write(one, 0, one.Length);
            }
            else
            {
                s.Write(head, 0, head.Length);
                if (n > 0) s.Write(body, 0, n);
            }
            s.Flush();
        }
        static void WriteText(Req r, int code, string reason, string text) { Write(r, code, reason, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text ?? ""), null); }
        static void WriteRpcError(Req r, int http, string reason, int code, string message, Dictionary<string, string> extra)
        {
            Write(r, http, reason, "application/json", Encoding.UTF8.GetBytes(JsonOut.Write(Error(null, code, message))), extra);
        }
        static readonly Dictionary<string, string> AllowAll = new Dictionary<string, string> { { "Allow", "GET, POST, DELETE" } };

        // "Localhost:47811" -> "localhost", "[::1]:47811" -> "[::1]" (lower case, port removed)
        public static string HostName(string host)
        {
            string h = (host ?? "").Trim().ToLowerInvariant();
            if (h.Length > 0 && h[0] == '[')
            {
                int e = h.IndexOf(']');
                return e > 0 ? h.Substring(0, e + 1) : h;
            }
            int c = h.LastIndexOf(':');
            return c >= 0 ? h.Substring(0, c) : h;
        }

        // n = a host name from HostName. The IPv6 loopback has many spellings ([0:0:0:0:0:0:0:1]), so it is parsed.
        static bool LocalName(string n)
        {
            if (n == "127.0.0.1" || n == "localhost" || n == "[::1]") return true;
            if (n.Length > 2 && n[0] == '[' && n[n.Length - 1] == ']')
            {
                IPAddress ip;
                return IPAddress.TryParse(n.Substring(1, n.Length - 2), out ip) && ip.Equals(IPAddress.IPv6Loopback);
            }
            return false;
        }

        // a web page reaches this port under its own host name when that name is re-pointed to 127.0.0.1 (DNS
        // rebinding); the browser then still sends the page's name in Host
        bool HostOk(string hostHeader)
        {
            string n = HostName(hostHeader);
            if (LocalName(n)) return true;
            var extra = ExtraHosts;
            if (extra != null) foreach (var e in extra) if (e == n) return true;
            return false;
        }

        // only browsers send Origin: pages of this machine's own servers may call, "null" (sandboxed frames, data: and
        // file: pages, redirects) and every other site may not
        static bool OriginOk(Req r)
        {
            string o = r.H("Origin");
            if (o == null) return true;
            o = o.Trim();
            if (o.Length == 0) return true;
            // scheme://host[:port]; "null" and everything else that is not of this shape is refused
            int i = o.IndexOf("://", StringComparison.Ordinal);
            if (i <= 0) return false;
            string authority = o.Substring(i + 3);
            int end = authority.IndexOfAny(OriginEnd);
            if (end >= 0) authority = authority.Substring(0, end);
            if (authority.Length == 0 || authority.IndexOf('@') >= 0) return false;
            return LocalName(HostName(authority));
        }

        static readonly char[] OriginEnd = { '/', '?', '#' };

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
            if ((DateTime.UtcNow - lastAuthLog).TotalSeconds > 10) { lastAuthLog = DateTime.UtcNow; host.Log("MCP request without a valid bearer token refused (" + r.client + ")"); }
            return 401;
        }

        // 403 and the connection ends; logged at most every 10 s
        bool Refuse(Req r, string why)
        {
            Interlocked.Increment(ref Refused);
            LastError = "refused: " + why;
            if ((DateTime.UtcNow - lastRefusalLog).TotalSeconds > 10)
            {
                lastRefusalLog = DateTime.UtcNow;
                host.Log("MCP request refused: " + why + " (" + r.method + " " + r.path + ", client " + (r.client.Length > 0 ? r.client : "?") + ")");
            }
            r.close = true;
            WriteText(r, 403, "Forbidden", why);
            return true;
        }

        // true = close the connection
        bool Handle(Req r)
        {
            Interlocked.Increment(ref Requests);
            if (r.client.Length > 0) LastClient = r.client;
            string hh = r.H("Host");
            if (hh != null && hh.Trim().Length > 0 && !HostOk(hh)) return Refuse(r, "host not allowed: this server answers to 127.0.0.1, localhost and [::1] (EditorState mcp.hosts adds names)");
            if (!OriginOk(r)) return Refuse(r, "origin not allowed: only pages of 127.0.0.1, localhost and [::1] may call this server");
            string site = r.H("Sec-Fetch-Site");
            if (site != null && site.Trim().Equals("cross-site", StringComparison.OrdinalIgnoreCase)) return Refuse(r, "cross-site requests are not allowed");
            string profile = McpProfile.FromPath(r.path);
            if (profile == null)
            {
                if (ServerPlain.Owns(r.path)) return ServePlain(r);
                if (r.method == "GET" && (r.path == "/" || r.path == "/health")) { WriteText(r, 200, "OK", "InsideDev MCP server " + host.ServerVersion + " - endpoints http://127.0.0.1:" + Port + Path + " (dev), " + Path + "/play, " + Path + "/all; " + ServerPlain.Endpoints); return false; }
                WriteText(r, 404, "Not Found", "MCP endpoints are " + Path + ", " + Path + "/dev, " + Path + "/play and " + Path + "/all; " + ServerPlain.Endpoints); return false;
            }
            int? auth = AuthFailure(r);
            if (auth == 401)
            {
                WriteRpcError(r, 401, "Unauthorized", -32001, "unauthorized: this InsideDev MCP server needs the header Authorization: Bearer <token> (the token is EditorState mcp.token in the game)",
                    new Dictionary<string, string> { { "WWW-Authenticate", "Bearer realm=\"insidedev\"" } });
                return false;
            }
            if (auth != null) { r.close = true; WriteRpcError(r, 500, "Internal Server Error", -32603, "the server could not read its auth token", null); return true; }
            string sid; r.h.TryGetValue("Mcp-Session-Id", out sid);
            if (sid != null && sid.Length == 0) sid = null;
            string pv; r.h.TryGetValue("MCP-Protocol-Version", out pv);
            bool badVersion = !string.IsNullOrEmpty(pv) && !KnownVersion(pv);
            switch (r.method)
            {
                case "POST": return HandlePost(r, profile, sid, badVersion ? pv : null);
                case "GET":
                    if (!Accepts(r, "text/event-stream")) { Write(r, 405, "Method Not Allowed", null, null, AllowAll); return false; }
                    if (badVersion) { UnsupportedVersion(r, pv); return false; }
                    return ServeStream(r, sid);
                case "DELETE":
                    if (badVersion) { UnsupportedVersion(r, pv); return false; }
                    if (sid == null) { WriteText(r, 400, "Bad Request", "Mcp-Session-Id header required"); return false; }
                    if (!Drop(sid, "session closed by the client")) { WriteText(r, 404, "Not Found", "unknown or expired session"); return false; }
                    Write(r, 200, "OK", null, null, null); return false;
                default:
                    Write(r, 405, "Method Not Allowed", null, null, AllowAll); return false;
            }
        }

        // POST /cmd, POST /cmds, GET /state, GET /status, GET /ping (ServerPlain): no session; the bearer token rule of
        // the MCP paths applies except to /ping
        bool ServePlain(Req r)
        {
            if (ServerPlain.NeedsAuth(r.path))
            {
                int? auth = AuthFailure(r);
                if (auth == 401)
                {
                    Write(r, 401, "Unauthorized", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("unauthorized: send the header Authorization: Bearer <token> (EditorState mcp.token in the game)"),
                        new Dictionary<string, string> { { "WWW-Authenticate", "Bearer realm=\"insidedev\"" } });
                    return false;
                }
                if (auth != null) { r.close = true; WriteText(r, 500, "Internal Server Error", "the server could not read its auth token"); return true; }
            }
            PlainReply p;
            try
            {
                if (ServerPlain.IsStatus(r.path))
                    p = r.method == "GET" ? new PlainReply { contentType = "application/json", body = JsonOut.Write(Status()) } : ServerPlain.NotAllowed("GET");
                else p = ServerPlain.Serve(host, r.method, r.path, r.query, r.body, r.client);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref Errors);
                LastError = r.path + ": " + e.Message;
                p = new PlainReply { code = 500, reason = "Internal Server Error", body = r.path + " failed: " + e.GetType().Name + ": " + e.Message };
            }
            if (p.code >= 400 || (p.headers != null && p.headers.ContainsKey(ServerPlain.ErrorHeader))) Interlocked.Increment(ref Errors);
            Write(r, p.code, p.reason, p.contentType, Encoding.UTF8.GetBytes(p.body ?? ""), p.headers);
            return false;
        }

        // GET /status: what this server knows without asking the game's main thread, plus the host's fields
        // (StatusSource: main thread alive, frame, control job, screen, window)
        public Json.Obj Status()
        {
            var o = new Json.Obj();
            o["ok"] = true;
            string v; try { v = host.ServerVersion ?? "?"; } catch (Exception) { v = "?"; }
            o["version"] = v;
            o["port"] = (double)Port;
            o["requests"] = (double)Requests; o["tool_calls"] = (double)ToolCalls; o["errors"] = (double)Errors; o["refused"] = (double)Refused;
            int total, live = 0, calls;
            var now = DateTime.UtcNow;
            lock (gate)
            {
                total = sessions.Count; calls = inflight.Count;
                foreach (var s in sessions.Values) if (Live(s, now)) live++;
            }
            o["calls_in_flight"] = (double)calls;
            var ss = new Json.Obj(); ss["total"] = (double)total; ss["live"] = (double)live;
            o["sessions"] = ss;
            o["last_client"] = LastClient ?? "";
            o["last_error"] = LastError ?? "";
            var src = StatusSource;
            if (src != null)
            {
                try
                {
                    var x = src();
                    if (x != null) foreach (var k in x.order) if (x.ContainsKey(k)) o[k] = x[k];
                }
                catch (Exception e) { o["status_error"] = e.GetType().Name + ": " + e.Message; }
            }
            return o;
        }

        // no header (scripts that send a bare body) or the media type application/json exactly, parameters and case ignored
        public static bool JsonContentType(string ct)
        {
            if (ct == null) return true;
            int semi = ct.IndexOf(';');
            string media = (semi >= 0 ? ct.Substring(0, semi) : ct).Trim();
            return media.Equals("application/json", StringComparison.OrdinalIgnoreCase);
        }

        static void UnsupportedVersion(Req r, string pv)
        {
            WriteRpcError(r, 400, "Bad Request", -32600, "unsupported MCP-Protocol-Version " + pv + "; supported: " + string.Join(", ", Versions), null);
        }

        bool HandlePost(Req r, string profile, string sid, string badVersion)
        {
            if (!JsonContentType(r.H("Content-Type")))
            {
                WriteRpcError(r, 415, "Unsupported Media Type", -32600, "an MCP request with a Content-Type header needs Content-Type: application/json", null);
                return false;
            }
            object msg;
            try { msg = Json.Parse(Encoding.UTF8.GetString(r.body)); }
            catch (Exception e) { WriteRpcError(r, 400, "Bad Request", -32700, "parse error: " + e.Message, null); return false; }
            bool isInit = IsInitialize(msg);
            if (badVersion != null && !isInit && !IsMethod(msg, "server/discover")) { UnsupportedVersion(r, badVersion); return false; }
            if (sid != null && !isInit && !Touch(sid)) { WriteText(r, 404, "Not Found", "unknown or expired session; initialize again"); return false; }
            string newSid = null;
            object reply;
            var list = msg as List<object>;
            if (list != null)
            {
                if (list.Count == 0) { WriteRpcError(r, 400, "Bad Request", -32600, "empty batch", null); return false; }
                var outs = new List<object>();
                foreach (var m in list) { var o = Dispatch(m as Json.Obj, profile, sid, ref newSid, null, r, null); if (o != null) outs.Add(o); }
                reply = outs.Count > 0 ? outs : null;
            }
            else
            {
                var m = msg as Json.Obj;
                if (m != null && WantsStream(r, m)) return ServeCallStream(r, m, profile, sid);
                Func<bool> gone = r.mayHalfClose ? null : r.conn.GoneTest();
                reply = Dispatch(m, profile, sid, ref newSid, null, r, gone);
            }
            Dictionary<string, string> extra = null;
            if (newSid != null) extra = new Dictionary<string, string> { { "Mcp-Session-Id", newSid } };
            if (reply == null) { Write(r, 202, "Accepted", null, null, extra); return false; }
            Write(r, 200, "OK", "application/json", Encoding.UTF8.GetBytes(JsonOut.Write(reply)), extra);
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

        // a tools/call with a progress token: SSE with notifications/progress, then the response; the connection closes
        bool ServeCallStream(Req r, Json.Obj m, string profile, string sid)
        {
            object token = ProgressToken(m);
            var w = new SseWriter(r.conn.s);
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
            Func<bool> closed = r.mayHalfClose ? null : r.conn.GoneTest();
            Func<bool> gone = () => w.Broken || (closed != null && closed());
            object reply;
            string newSid = null;
            try { reply = Dispatch(m, profile, sid, ref newSid, sink, r, gone); }
            finally { ka.Dispose(); lock (plock) done = true; }
            if (reply != null) w.Send(reply);
            w.Dispose();
            return true;
        }

        // the session's notification stream (GET): list_changed when ListVersion moves, keep-alive comments. The loop
        // ends when the client closed the connection, which is how a killed client is noticed.
        bool ServeStream(Req r, string sid)
        {
            if (sid == null) { WriteText(r, 400, "Bad Request", "the notification stream needs the Mcp-Session-Id of an initialized session"); return false; }
            Session ses;
            lock (gate) sessions.TryGetValue(sid, out ses);
            if (ses == null) { WriteText(r, 404, "Not Found", "unknown or expired session; initialize again"); return false; }
            var w = new SseWriter(r.conn.s);
            SseWriter old;
            lock (gate) { old = ses.stream; ses.stream = w; ses.hadStream = true; ses.seen = DateTime.UtcNow; }
            if (old != null) old.Close();
            Func<bool> closed = r.conn.GoneTest();
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
                else if (w.IdleMs >= KeepAliveMs)
                {
                    w.KeepAlive();
                    if (!w.Broken) lock (gate) ses.seen = DateTime.UtcNow;
                }
                if (closed != null && closed()) break;
                Thread.Sleep(StreamPollMs);
            }
            lock (gate) if (ses.stream == w) { ses.stream = null; ses.streamLost = DateTime.UtcNow; }
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
            lock (gate) { Session ses; if (!sessions.TryGetValue(sid, out ses)) return false; ses.seen = DateTime.UtcNow; ses.requests++; return true; }
        }

        // gate held
        static bool Live(Session s, DateTime now)
        {
            if (s.calls > 0) return true;
            if (s.stream != null && !s.stream.Broken) return true;
            if (s.hadStream) return now - (s.seen > s.streamLost ? s.seen : s.streamLost) < StreamGrace;
            return now - s.seen < LiveWindow;
        }

        // sessions whose client still shows signs of life (see LiveWindow); no allocation, any thread
        internal int LiveSessions
        {
            get
            {
                var now = DateTime.UtcNow;
                int n = 0;
                lock (gate) foreach (var s in sessions.Values) if (Live(s, now)) n++;
                return n;
            }
        }

        // false for an unknown, ended or silent session
        internal bool SessionLive(string sid)
        {
            if (string.IsNullOrEmpty(sid)) return false;
            lock (gate) { Session s; return sessions.TryGetValue(sid, out s) && Live(s, DateTime.UtcNow); }
        }

        // one object per session: {id (first 8 characters), profile, client, protocol, age_s, idle_s, stream, calls, requests, live}
        public List<object> DescribeSessions()
        {
            var l = new List<object>();
            var now = DateTime.UtcNow;
            lock (gate)
                foreach (var s in sessions.Values)
                {
                    var o = new Json.Obj();
                    o["id"] = s.id.Substring(0, Math.Min(8, s.id.Length));
                    o["profile"] = s.profile; o["client"] = s.client; o["protocol"] = s.protocol;
                    o["age_s"] = Math.Round((now - s.created).TotalSeconds);
                    o["idle_s"] = Math.Round((now - s.seen).TotalSeconds);
                    o["stream"] = s.stream != null && !s.stream.Broken;
                    o["calls"] = (double)s.calls; o["requests"] = (double)s.requests;
                    o["live"] = Live(s, now);
                    l.Add(o);
                }
            return l;
        }

        string NewSession(string profile, string protocol, string client)
        {
            string id = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            var ses = new Session { id = id, profile = profile, protocol = protocol, client = client, created = now, seen = now, notified = ListVersion() };
            var dropped = new List<string>();
            lock (gate)
            {
                foreach (var kv in sessions) if (now - kv.Value.seen > SessionIdle && kv.Value.calls == 0) dropped.Add(kv.Key);
                while (sessions.Count - dropped.Count >= MaxSessions)
                {
                    Session oldest = null;
                    foreach (var x in sessions.Values) if (!dropped.Contains(x.id) && (oldest == null || x.seen < oldest.seen)) oldest = x;
                    if (oldest == null) break;
                    dropped.Add(oldest.id);
                }
            }
            foreach (var d in dropped) Drop(d, "session expired");
            lock (gate) sessions[id] = ses;
            return id;
        }

        // the one way a session ends (DELETE, expiry, the session cap): its stream closes, its running calls are cancelled
        bool Drop(string sid, string reason)
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

        void Register(string sid, string key, CallInfo info)
        {
            lock (gate)
            {
                inflight[key] = info;
                Session ses;
                if (sid != null && sessions.TryGetValue(sid, out ses)) ses.calls++;
                EarlyCancel ec;
                if (earlyCancels.TryGetValue(key, out ec)) { earlyCancels.Remove(key); if ((DateTime.UtcNow - ec.at).TotalMilliseconds <= EarlyCancelMs) info.Cancel(ec.reason); }
            }
        }

        void Unregister(string sid, string key, CallInfo info)
        {
            lock (gate)
            {
                CallInfo cur;
                if (inflight.TryGetValue(key, out cur) && ReferenceEquals(cur, info)) inflight.Remove(key);
                Session ses;
                if (sid != null && sessions.TryGetValue(sid, out ses)) { if (ses.calls > 0) ses.calls--; ses.seen = DateTime.UtcNow; }
            }
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
        public object Dispatch(Json.Obj m, string profile, string sid, ref string newSid) { return Dispatch(m, profile, sid, ref newSid, null, null, null); }

        internal object Dispatch(Json.Obj m, string profile, string sid, ref string newSid, Action<double, double, string> progress) { return Dispatch(m, profile, sid, ref newSid, progress, null, null); }

        // r = the HTTP request (null in direct calls); gone = asks whether the client of a tools/call went away
        object Dispatch(Json.Obj m, string profile, string sid, ref string newSid, Action<double, double, string> progress, Req r, Func<bool> gone)
        {
            if (m == null) return Error(null, -32600, "invalid request");
            string method = m.Str("method");
            object id = m["id"];
            if (method == null) return null;
            string client = r != null ? r.client : LastClient;
            if (!m.ContainsKey("id")) { OnNotification(method, m["params"] as Json.Obj ?? new Json.Obj(), sid, client); return null; }
            var p = m["params"] as Json.Obj ?? new Json.Obj();
            try
            {
                switch (method)
                {
                    case "initialize": return Initialize(id, p, profile, ref newSid);
                    case "ping": return Result(id, new Json.Obj());
                    case "tools/list": return Result(id, Listed("tools", profile));
                    case "tools/call": return CallTool(id, p, profile, sid, progress, client, gone);
                    case "resources/list": return Result(id, Listed("resources", profile));
                    case "resources/templates/list": return Result(id, Listed("resourceTemplates", profile));
                    case "resources/read": return ReadResource(id, p, profile, sid, client);
                    case "prompts/list": return Result(id, Listed("prompts", profile));
                    case "prompts/get": return GetPrompt(id, p, profile, sid, client);
                    default: return Error(id, -32601, "method not found: " + method);
                }
            }
            catch (McpException e) { Interlocked.Increment(ref Errors); return RpcError(id, e, -32603); }
            catch (Exception e) { Interlocked.Increment(ref Errors); LastError = method + ": " + e.Message; return Error(id, -32603, method + " failed: " + e.GetType().Name + ": " + e.Message); }
        }

        void OnNotification(string method, Json.Obj p, string sid, string client)
        {
            if (method == "notifications/initialized") host.Log("MCP client initialized (" + client + ")");
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

        // The result of tools/list, resources/list, resources/templates/list and prompts/list as ready JSON text, built
        // once per profile and list version (the allowlist bumps the version too) and at most ListCacheMs old, so a
        // provider whose list failed once is asked again.
        JsonRaw Listed(string kind, string profile)
        {
            long v = ListVersion();
            string key = kind + "\n" + profile;
            var now = DateTime.UtcNow;
            CachedList c;
            lock (lists) lists.TryGetValue(key, out c);
            if (c != null && c.version == v && (now - c.at).TotalMilliseconds < ListCacheMs) return c.json;
            var arr = new List<object>();
            switch (kind)
            {
                case "tools": foreach (var t in host.Tools(profile) ?? new List<ToolDef>()) if (t != null && host.Allowed(profile, t.name)) arr.Add(t.ToJson()); break;
                case "resources": foreach (var d in host.Resources(profile) ?? new List<ResourceDef>()) if (d != null) arr.Add(d.ToJson()); break;
                case "resourceTemplates": foreach (var d in host.ResourceTemplates(profile) ?? new List<ResourceTemplateDef>()) if (d != null) arr.Add(d.ToJson()); break;
                default: foreach (var d in host.Prompts(profile) ?? new List<PromptDef>()) if (d != null) arr.Add(d.ToJson()); break;
            }
            var res = new Json.Obj(); res[kind] = arr;
            c = new CachedList { version = v, at = now, json = new JsonRaw(JsonOut.Write(res)) };
            lock (lists) lists[key] = c;
            return c.json;
        }

        ToolDef FindTool(string profile, string name)
        {
            foreach (var t in host.Tools(profile) ?? new List<ToolDef>()) if (t != null && t.name == name) return host.Allowed(profile, name) ? t : null;
            return null;
        }

        // the tools of InsideDev.Play.dll: an unknown tool with one of these names means that module is not loaded
        static readonly string[] PlayTools =
        {
            "observe", "time", "wait_until", "restore", "geometry", "input_track", "act", "move_to", "interact", "checkpoint", "menu", "guide", "notes",
            "level", "stream", "ui", "save", "run_scenario", "camera", "logic", "body", "hazard", "audio", "render", "reflect", "state_diff"
        };

        string WhereIs(string profile, string name)
        {
            foreach (var t in host.Tools(McpProfile.All) ?? new List<ToolDef>())
            {
                if (t == null || t.name != name) continue;
                if (McpProfile.Includes(profile, t.profiles)) return "; it is disabled by the game's tool allowlist (EditorState mcp.tools)";
                return "; it is not in the " + profile + " profile, it is served on " + ((t.profiles & McpProfiles.Dev) != 0 ? Path + "/dev" : Path + "/play") + " and " + Path + "/all";
            }
            if (Array.IndexOf(PlayTools, name) >= 0)
                return "; " + name + " is a tool of InsideDev.Play.dll, which is not loaded (copy InsideDev.Play.dll into the game's _mod folder, then run the console command 'ext load' or restart the game)";
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

        Json.Obj CallTool(object id, Json.Obj p, string profile, string sid, Action<double, double, string> progress, string client, Func<bool> gone)
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
                var info = new CallInfo { profile = profile, tool = name, sessionId = sid ?? "", requestId = id, client = client ?? "", budgetMs = def.timeoutMs > 0 ? def.timeoutMs : 55000, clientGone = gone };
                var pm = p["_meta"] as Json.Obj;
                if (pm != null) info.progressToken = pm["progressToken"];
                if (info.progressToken != null) info.progress = progress;
                string key = CallKey(sid, id);
                Register(sid, key, info);
                try { tr = host.Call(def, args, info) ?? ToolResult.Fail(McpErr.Internal, name + " returned nothing"); }
                catch (McpException e) { tr = ToolResult.Fail(e); }
                catch (Exception e) { tr = ToolResult.Fail(McpErr.Internal, name + " failed: " + e.GetType().Name + ": " + e.Message); }
                finally { Unregister(sid, key, info); }
            }
            if (coerced.Count > 0) AddWarnings(def, tr, coerced);
            CheckOutput(def, tr);
            if (tr.isError) Interlocked.Increment(ref Errors);
            // a tool without an outputSchema that returns an image goes out as content only (the JSON text block, then
            // the image): Claude Code passes only structuredContent to the model when it is present, which would drop
            // the image
            bool imageOnly = def.outputSchema == null && tr.HasImage;
            var res = tr.ToJson(ToolResult.ShortTextCopy && !imageOnly);
            if (imageOnly) { res.Remove("structuredContent"); res.order.Remove("structuredContent"); }
            return Result(id, res);
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

        static CallInfo Info(string profile, string what, string sid, object id, string client)
        {
            return new CallInfo { profile = profile, tool = what, sessionId = sid ?? "", requestId = id, client = client ?? "", budgetMs = 55000 };
        }

        Json.Obj ReadResource(object id, Json.Obj p, string profile, string sid, string client)
        {
            string uri = p.Str("uri");
            if (string.IsNullOrEmpty(uri)) return Error(id, -32602, "resources/read needs params.uri");
            var info = Info(profile, "resources/read", sid, id, client);
            string key = CallKey(sid, id);
            Register(sid, key, info);
            ResourceContents rc;
            try { rc = host.ReadResource(uri, info); }
            catch (McpException e) { return RpcError(id, e, -32002); }
            finally { Unregister(sid, key, info); }
            if (rc == null) { var d = new Json.Obj(); d["uri"] = uri; return Error(id, -32002, "resource not found: " + uri, d); }
            if (string.IsNullOrEmpty(rc.uri)) rc.uri = uri;
            var res = new Json.Obj(); res["contents"] = new List<object> { rc.ToJson() };
            return Result(id, res);
        }

        Json.Obj GetPrompt(object id, Json.Obj p, string profile, string sid, string client)
        {
            string name = p.Str("name");
            if (string.IsNullOrEmpty(name)) return Error(id, -32602, "prompts/get needs params.name");
            PromptDef def = null;
            foreach (var d in host.Prompts(profile) ?? new List<PromptDef>()) if (d != null && d.name == name) { def = d; break; }
            if (def == null) return Error(id, -32602, "unknown prompt: " + name);
            var args = p["arguments"] as Json.Obj ?? new Json.Obj();
            foreach (var a in def.arguments)
                if (a != null && a.required && string.IsNullOrEmpty(args.Str(a.name))) return Error(id, -32602, "prompt " + name + " needs the argument " + a.name);
            var info = Info(profile, "prompts/get", sid, id, client);
            string key = CallKey(sid, id);
            Register(sid, key, info);
            PromptResult pr;
            try { pr = host.GetPrompt(name, args, info); }
            catch (McpException e) { return RpcError(id, e, -32602); }
            finally { Unregister(sid, key, info); }
            if (pr == null) return Error(id, -32602, "unknown prompt: " + name);
            return Result(id, pr.ToJson());
        }
    }

    // McpBuild (owner SERVER; here so the DLL, the stdio exe and the pure tests compile it): the version of this MCP
    // build and a build id = the first 8 hex digits of the SHA-256 of the assembly file (the same digits as the build's
    // hashes.txt), so a client can tell which build answers. Version is a literal because the stdio exe is built without
    // InsideDev.dll; the game side warns at start when it differs from the core's Boot.Version.
    public static class McpBuild
    {
        public const string Version = "0.11.0";
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

    // JSON text that is already serialized (a cached list result); JsonOut writes it as it is
    public sealed class JsonRaw
    {
        public readonly string text;
        public JsonRaw(string text) { this.text = text ?? "null"; }
    }

    // compact JSON writer (one line; MCP stdio framing needs that)
    public static class JsonOut
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public static string Write(object v) { var sb = new StringBuilder(256); W(sb, v); return sb.ToString(); }
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
            if (v is float)
            {
                // the float's own shortest form ("0.1"), not its widening to double ("0.10000000149011612")
                float f = (float)v;
                if (float.IsNaN(f) || float.IsInfinity(f)) { sb.Append("null"); return; }
                sb.Append(f == Math.Floor(f) && Math.Abs(f) < 1e15f ? ((long)f).ToString(IC) : f.ToString("R", IC)); return;
            }
            if (v is int || v is long) { sb.Append(Convert.ToInt64(v, IC).ToString(IC)); return; }
            var raw = v as JsonRaw; if (raw != null) { sb.Append(raw.text); return; }
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

        // text without a character that needs escaping (base64 image data, most names) is appended in one piece
        static void Q(StringBuilder sb, string s)
        {
            sb.Append('"');
            int n = s.Length, run = 0;
            for (int i = 0; i < n; i++)
            {
                char c = s[i];
                if (c >= 0x20 && c != '"' && c != '\\' && c != (char)0x2028 && c != (char)0x2029) continue;
                if (i > run) sb.Append(s, run, i - run);
                run = i + 1;
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append("\\u").Append(((int)c).ToString("x4", IC)); break;
                }
            }
            if (run == 0) sb.Append(s);
            else if (n > run) sb.Append(s, run, n - run);
            sb.Append('"');
        }
    }

    // ServerPlain (owner SERVER; in this file so every build and pure test that lists the SERVER files compiles it): plain
    // local HTTP next to the MCP endpoints, for scripts and people without an MCP client or session
    // (docs/MCP_PLAY_DESIGN.md section 19). Same listener (127.0.0.1 only, port 47811), the same Host / Origin checks
    // and the same bearer token rule as the MCP paths (McpServer.Handle; /ping needs no token):
    //   POST /cmd    text body = one console / bridge command (ctl ..., boypos, mcam ...); it runs on the main thread
    //                through the host's command tool (HostDispatch: allowlist, refusals, audit, 55 s budget) and the
    //                reply is its text (text/plain). A failed command still answers 200 with header X-InsideDev-Error:
    //                <code>; 400 empty body / more than one line, 413 longer than MaxCommandChars, 403 not allowed.
    //                Query ?wait=<status command>[&until=<text|text>][&timeout=<s>] keeps the call open until the reply
    //                of the status command contains one of the until texts (header X-InsideDev-Wait: done | timeout).
    //   POST /cmds   several commands in one call: the body is one command per line (empty lines and lines that start
    //                with # are skipped) or a JSON array of strings; they run in order on the main thread. The reply is
    //                JSON [{command, ok, code, text}]. Query ?stop_on_error=1 stops at the first failed command (the
    //                rest are reported with code "skipped"), ?dry_run=1 only checks which commands would be refused.
    //   GET /state   the concise observe JSON (the observe tool with its defaults); 503 with the error JSON when observe
    //                fails (no game world, not ready).
    //   GET /status  JSON about the server and the game, answered by McpServer.Status without the main thread.
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
        public const string Cmd = "/cmd", Cmds = "/cmds", State = "/state", Status = "/status", Ping = "/ping", ErrorHeader = "X-InsideDev-Error", WaitHeader = "X-InsideDev-Wait";
        public const int MaxCommandChars = 8192, MaxReplyChars = 200000, MaxBatch = 200;

        static string Norm(string path) { return path == null || path == "/" ? path : path.TrimEnd('/'); }

        public static bool Owns(string path)
        {
            string p = Norm(path);
            return p == Cmd || p == Cmds || p == State || p == Status || p == Ping;
        }

        public static bool IsStatus(string path) { return Norm(path) == Status; }

        public static bool NeedsAuth(string path) { return Norm(path) != Ping; }

        public const string Endpoints = "plain: POST /cmd (one console command, text), POST /cmds (several, JSON), GET /state (observe JSON), GET /status (health JSON), GET /ping";

        public static PlainReply Serve(IToolHost host, string method, string path, byte[] body, string client) { return Serve(host, method, path, "", body, client); }

        public static PlainReply Serve(IToolHost host, string method, string path, string query, byte[] body, string client)
        {
            string p = Norm(path);
            if (p == Ping) return method == "GET" ? Text(200, "OK", "pong InsideDev " + Version(host)) : NotAllowed("GET");
            if (p == State) return method == "GET" ? StateReply(host, client) : NotAllowed("GET");
            if (p == Cmd) return method == "POST" ? CmdReply(host, body, query, client) : NotAllowed("POST");
            if (p == Cmds) return method == "POST" ? CmdsReply(host, body, query, client) : NotAllowed("POST");
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

        internal static PlainReply NotAllowed(string allow)
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

        // "a=1&b=x%20y" -> value of key (percent-decoded, + = space); null when the key is absent
        public static string Query(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (var part in query.Split('&'))
            {
                int e = part.IndexOf('=');
                string k = e < 0 ? part : part.Substring(0, e);
                if (Decode(k) != key) continue;
                return e < 0 ? "" : Decode(part.Substring(e + 1));
            }
            return null;
        }

        static string Decode(string s)
        {
            if (s.IndexOf('%') < 0 && s.IndexOf('+') < 0) return s;
            var bytes = new List<byte>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                int hi, lo;
                if (c == '+') bytes.Add((byte)' ');
                else if (c == '%' && i + 2 < s.Length && (hi = Hex(s[i + 1])) >= 0 && (lo = Hex(s[i + 2])) >= 0) { bytes.Add((byte)(hi * 16 + lo)); i += 2; }
                else foreach (var b in Encoding.UTF8.GetBytes(c.ToString())) bytes.Add(b);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        static int Hex(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        static bool Flag(string query, string key)
        {
            string v = Query(query, key);
            return v != null && v != "0" && !v.Equals("false", StringComparison.OrdinalIgnoreCase) && !v.Equals("off", StringComparison.OrdinalIgnoreCase);
        }

        static string BodyText(byte[] body)
        {
            string s = Encoding.UTF8.GetString(body ?? new byte[0]);
            if (s.Length > 0 && s[0] == (char)0xFEFF) s = s.Substring(1);
            return s;
        }

        static PlainReply CommandTool(IToolHost host, out ToolDef def)
        {
            def = Find(host, McpProfile.Dev, "command");
            if (def == null) return Text(503, "Service Unavailable", "the command tool is not served (InsideDev.Mcp has no dev tools loaded)");
            if (!host.Allowed(McpProfile.Dev, "command")) return Text(403, "Forbidden", "the command tool is not in the tool allowlist (EditorState mcp.tools)");
            return null;
        }

        static string ErrorCode(Json.Obj s)
        {
            var e = s != null ? s["error"] as Json.Obj : null;
            return e != null ? e.Str("code", McpErr.Internal) : McpErr.Internal;
        }

        static PlainReply CmdReply(IToolHost host, byte[] body, string query, string client)
        {
            string line = BodyText(body).Trim();
            if (line.Length == 0) return Text(400, "Bad Request", "POST /cmd needs one console command as the text body, e.g. ctl status");
            if (line.Length > MaxCommandChars) return Text(413, "Payload Too Large", "a command is at most " + MaxCommandChars + " characters");
            if (line.IndexOf('\n') >= 0 || line.IndexOf('\r') >= 0) return Text(400, "Bad Request", "one command per request (the body has a line break); POST /cmds takes several");
            ToolDef def;
            var no = CommandTool(host, out def);
            if (no != null) return no;
            var args = new Json.Obj();
            args["command"] = line;
            args["max_chars"] = (double)MaxReplyChars;
            string wait = Query(query, "wait");
            if (!string.IsNullOrEmpty(wait))
            {
                args["wait"] = wait;
                string until = Query(query, "until");
                if (!string.IsNullOrEmpty(until)) args["until"] = until;
                double t;
                string ts = Query(query, "timeout");
                if (!string.IsNullOrEmpty(ts))
                {
                    if (!double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out t) || t < 0 || t > 50) return Text(400, "Bad Request", "timeout must be a number of seconds from 0 to 50");
                    args["wait_timeout_s"] = t;
                }
            }
            var tr = Call(host, def, args, "http " + Cmd, client);
            var s = tr.structured;
            string text = s != null ? s.Str("text", null) : null;
            var r = Text(200, "OK", text);
            if (tr.isError)
            {
                var e = s != null ? s["error"] as Json.Obj : null;
                string code = ErrorCode(s);
                if (text == null) r.body = "error " + code + ": " + (e != null ? e.Str("message", "") : "failed") + (e != null && e.Str("hint", null) != null ? "\nhint: " + e.Str("hint") : "");
                r.headers = new Dictionary<string, string> { { ErrorHeader, code } };
            }
            else if (text == null) r.body = s != null ? JsonOut.Write(s) : "done (no output)";
            if (s != null && s.Bool("truncated")) { if (r.headers == null) r.headers = new Dictionary<string, string>(); r.headers["X-InsideDev-Truncated"] = "1"; }
            if (s != null && s["wait_done"] is bool) { if (r.headers == null) r.headers = new Dictionary<string, string>(); r.headers[WaitHeader] = (bool)s["wait_done"] ? "done" : "timeout"; }
            return r;
        }

        // the commands of a POST /cmds body; null plus error for a body that is neither lines nor a JSON array of strings
        public static List<string> BatchLines(string text, out string error)
        {
            error = null;
            var l = new List<string>();
            string t = (text ?? "").Trim();
            if (t.Length > 0 && t[0] == '[')
            {
                object v;
                try { v = Json.Parse(t); }
                catch (Exception e) { error = "the body starts with [ but is not a JSON array: " + e.Message; return null; }
                var arr = v as List<object>;
                if (arr == null) { error = "a JSON body must be an array of command strings"; return null; }
                foreach (var x in arr)
                {
                    var s = x as string;
                    if (s == null) { error = "a JSON body must be an array of command strings"; return null; }
                    if (s.Trim().Length > 0) l.Add(s.Trim());
                }
            }
            else
                foreach (var raw in t.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    string s = raw.Trim();
                    if (s.Length == 0 || s[0] == '#') continue;
                    l.Add(s);
                }
            if (l.Count == 0) { error = "POST /cmds needs one console command per line (or a JSON array of strings)"; return null; }
            if (l.Count > MaxBatch) { error = "at most " + MaxBatch + " commands per call (got " + l.Count + ")"; return null; }
            foreach (var s in l)
            {
                if (s.Length > MaxCommandChars) { error = "a command is at most " + MaxCommandChars + " characters"; return null; }
                if (s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0) { error = "a command must not contain a line break"; return null; }
            }
            return l;
        }

        static PlainReply CmdsReply(IToolHost host, byte[] body, string query, string client)
        {
            string error;
            var lines = BatchLines(BodyText(body), out error);
            if (lines == null) return Text(400, "Bad Request", error);
            ToolDef def;
            var no = CommandTool(host, out def);
            if (no != null) return no;
            var args = new Json.Obj();
            var list = new List<object>();
            foreach (var s in lines) list.Add(s);
            args["commands"] = list;
            args["stop_on_error"] = Flag(query, "stop_on_error");
            args["dry_run"] = Flag(query, "dry_run");
            args["max_chars"] = (double)MaxReplyChars;
            var tr = Call(host, def, args, "http " + Cmds, client);
            var st = tr.structured;
            var results = st != null ? st["results"] as List<object> : null;
            var r = new PlainReply { contentType = "application/json" };
            if (results == null)
            {
                // the whole call failed before any command ran (not allowed, bad arguments, main thread not running)
                var e = st != null ? st["error"] as Json.Obj : null;
                var one = new Json.Obj();
                one["command"] = ""; one["ok"] = false; one["code"] = ErrorCode(st); one["text"] = e != null ? e.Str("message", "failed") : "failed";
                r.body = JsonOut.Write(new List<object> { one });
                r.headers = new Dictionary<string, string> { { ErrorHeader, ErrorCode(st) } };
                return r;
            }
            r.body = JsonOut.Write(results);
            foreach (var x in results)
            {
                var o = x as Json.Obj;
                if (o == null || o.Bool("ok", true)) continue;
                r.headers = new Dictionary<string, string> { { ErrorHeader, o.Str("code", McpErr.Internal) } };
                break;
            }
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
