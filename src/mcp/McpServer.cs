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
    // Model Context Protocol server, "Streamable HTTP" transport (spec 2025-06-18, also answers 2025-03-26 / 2024-11-05
    // clients). Unity-free: the game side plugs in through IToolHost. Listens on 127.0.0.1 only.
    //   POST /mcp   one JSON-RPC message (or a batch) -> application/json response, 202 for notifications
    //   GET  /mcp   405 (this server never pushes messages)
    //   DELETE /mcp ends the session
    // Requests arrive on worker threads; the host decides how to get onto the game's main thread.
    public sealed class ToolDef
    {
        public string name, title, description;
        public Json.Obj inputSchema;
        public bool readOnly;
    }

    public sealed class ToolResult
    {
        public readonly List<Json.Obj> content = new List<Json.Obj>();
        public bool isError;
        public static ToolResult Text(string s, bool error = false) { var r = new ToolResult { isError = error }; r.AddText(s); return r; }
        public ToolResult AddText(string s) { var c = new Json.Obj(); c["type"] = "text"; c["text"] = s ?? ""; content.Add(c); return this; }
        public ToolResult AddImage(byte[] png, string mime) { var c = new Json.Obj(); c["type"] = "image"; c["data"] = Convert.ToBase64String(png); c["mimeType"] = mime; content.Add(c); return this; }
    }

    public interface IToolHost
    {
        string ServerVersion { get; }
        string Instructions { get; }
        List<ToolDef> Tools();
        ToolResult Call(string name, Json.Obj args);     // called on a worker thread
        void Log(string msg);
    }

    public sealed class McpServer
    {
        public static readonly string[] Versions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
        public const string Path = "/mcp";

        readonly IToolHost host;
        TcpListener listener;
        Thread acceptThread;
        volatile bool running;
        readonly Dictionary<string, DateTime> sessions = new Dictionary<string, DateTime>();
        public int Port { get; private set; }
        public int Requests, ToolCalls, Errors;
        public string LastClient = "";
        public string LastError = "";

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
            host.Log("MCP server on http://127.0.0.1:" + port + Path);
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
            listener = null;
            lock (sessions) sessions.Clear();
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
            try
            {
                c.NoDelay = true;
                using (c)
                using (var s = c.GetStream())
                {
                    s.ReadTimeout = 120000;
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
            catch (Exception e) { LastError = e.Message; host.Log("MCP connection: " + e.Message); }
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
            while ((line = ReadLine(s)) != null && line.Length > 0)
            {
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

        // only browsers send Origin; allow local pages only (DNS-rebinding protection)
        static bool OriginOk(Req r)
        {
            string o; if (!r.h.TryGetValue("Origin", out o) || o.Length == 0 || o == "null") return true;
            try { var u = new Uri(o); return u.Host == "localhost" || u.Host == "127.0.0.1" || u.Host == "[::1]"; } catch { return false; }
        }

        bool Handle(Req r, Stream s)
        {
            Interlocked.Increment(ref Requests);
            string ua; if (r.h.TryGetValue("User-Agent", out ua)) LastClient = ua;
            if (!OriginOk(r)) { WriteText(s, 403, "Forbidden", "origin not allowed"); return true; }
            if (r.path != Path && r.path != Path + "/")
            {
                if (r.method == "GET" && (r.path == "/" || r.path == "/health")) { WriteText(s, 200, "OK", "InsideDev MCP server " + host.ServerVersion + " - endpoint http://127.0.0.1:" + Port + Path); return false; }
                WriteText(s, 404, "Not Found", "MCP endpoint is " + Path); return false;
            }
            string sid; r.h.TryGetValue("Mcp-Session-Id", out sid);
            if (r.method == "DELETE")
            {
                if (sid != null) lock (sessions) sessions.Remove(sid);
                Write(s, 200, "OK", null, null, null); return false;
            }
            if (r.method == "GET") { Write(s, 405, "Method Not Allowed", null, null, new Dictionary<string, string> { { "Allow", "POST, DELETE" } }); return false; }
            if (r.method != "POST") { Write(s, 405, "Method Not Allowed", null, null, new Dictionary<string, string> { { "Allow", "POST, DELETE" } }); return false; }

            object msg;
            try { msg = Json.Parse(Encoding.UTF8.GetString(r.body)); }
            catch (Exception e) { Respond(s, Error(null, -32700, "parse error: " + e.Message), null); return false; }

            // a session id the server doesn't know (e.g. the game restarted) -> 404, the client starts a new session
            bool isInit = IsInitialize(msg);
            if (sid != null && !isInit) { bool known; lock (sessions) known = sessions.ContainsKey(sid); if (!known) { WriteText(s, 404, "Not Found", "unknown or expired session; initialize again"); return false; } }

            string newSid = null;
            object reply;
            var list = msg as List<object>;
            if (list != null)
            {
                var outs = new List<object>();
                foreach (var m in list) { var o = Dispatch(m as Json.Obj, ref newSid); if (o != null) outs.Add(o); }
                reply = outs.Count > 0 ? outs : null;
            }
            else reply = Dispatch(msg as Json.Obj, ref newSid);

            Dictionary<string, string> extra = null;
            if (newSid != null) extra = new Dictionary<string, string> { { "Mcp-Session-Id", newSid } };
            if (reply == null) { Write(s, 202, "Accepted", null, null, extra); return false; }
            Respond(s, reply, extra);
            return false;
        }

        static bool IsInitialize(object msg)
        {
            var o = msg as Json.Obj; if (o != null) return o.Str("method") == "initialize";
            var l = msg as List<object>; if (l != null) foreach (var m in l) { var x = m as Json.Obj; if (x != null && x.Str("method") == "initialize") return true; }
            return false;
        }

        void Respond(Stream s, object reply, Dictionary<string, string> extra)
        {
            Write(s, 200, "OK", "application/json", Encoding.UTF8.GetBytes(JsonOut.Write(reply)), extra);
        }

        // ---------------------------------------------------------------- JSON-RPC
        static Json.Obj Error(object id, int code, string message)
        {
            var e = new Json.Obj(); e["code"] = (double)code; e["message"] = message;
            var o = new Json.Obj(); o["jsonrpc"] = "2.0"; o["id"] = id; o["error"] = e;
            return o;
        }
        static Json.Obj Result(object id, object result) { var o = new Json.Obj(); o["jsonrpc"] = "2.0"; o["id"] = id; o["result"] = result; return o; }

        // returns null for notifications and client responses
        public object Dispatch(Json.Obj m, ref string newSid)
        {
            if (m == null) return Error(null, -32600, "invalid request");
            string method = m.Str("method");
            object id = m["id"];
            bool isRequest = m.ContainsKey("id") && method != null;
            if (method == null) return null;                // a response from the client: nothing to do
            if (!isRequest) { if (method == "notifications/initialized") host.Log("MCP client initialized (" + LastClient + ")"); return null; }
            var p = m["params"] as Json.Obj ?? new Json.Obj();
            try
            {
                switch (method)
                {
                    case "initialize":
                        {
                            string want = p.Str("protocolVersion", Versions[0]);
                            string ver = Array.IndexOf(Versions, want) >= 0 ? want : Versions[0];
                            var info = p["clientInfo"] as Json.Obj;
                            newSid = Guid.NewGuid().ToString("N");
                            lock (sessions) sessions[newSid] = DateTime.UtcNow;
                            host.Log("MCP initialize from " + (info != null ? info.Str("name", "?") + " " + info.Str("version", "") : "?") + " (protocol " + want + " -> " + ver + ")");
                            var caps = new Json.Obj(); var tools = new Json.Obj(); tools["listChanged"] = false; caps["tools"] = tools;
                            var si = new Json.Obj(); si["name"] = "insidedev"; si["title"] = "InsideDev (INSIDE runtime editor)"; si["version"] = host.ServerVersion;
                            var res = new Json.Obj(); res["protocolVersion"] = ver; res["capabilities"] = caps; res["serverInfo"] = si; res["instructions"] = host.Instructions;
                            return Result(id, res);
                        }
                    case "ping": return Result(id, new Json.Obj());
                    case "tools/list":
                        {
                            var arr = new List<object>();
                            foreach (var t in host.Tools())
                            {
                                var o = new Json.Obj(); o["name"] = t.name; if (t.title != null) o["title"] = t.title; o["description"] = t.description; o["inputSchema"] = t.inputSchema;
                                var an = new Json.Obj(); an["readOnlyHint"] = t.readOnly; an["openWorldHint"] = false; if (t.title != null) an["title"] = t.title; o["annotations"] = an;
                                arr.Add(o);
                            }
                            var res = new Json.Obj(); res["tools"] = arr; return Result(id, res);
                        }
                    case "tools/call":
                        {
                            string name = p.Str("name");
                            var args = p["arguments"] as Json.Obj ?? new Json.Obj();
                            bool known = false; foreach (var t in host.Tools()) if (t.name == name) known = true;
                            if (!known) return Error(id, -32602, "unknown tool: " + name);
                            Interlocked.Increment(ref ToolCalls);
                            ToolResult tr;
                            try { tr = host.Call(name, args) ?? ToolResult.Text(""); }
                            catch (Exception e) { tr = ToolResult.Text(name + " failed: " + e.Message, true); }
                            if (tr.isError) Interlocked.Increment(ref Errors);
                            var res = new Json.Obj(); res["content"] = new List<object>(tr.content.ConvertAll(x => (object)x)); res["isError"] = tr.isError;
                            return Result(id, res);
                        }
                    case "resources/list": { var res = new Json.Obj(); res["resources"] = new List<object>(); return Result(id, res); }
                    case "prompts/list": { var res = new Json.Obj(); res["prompts"] = new List<object>(); return Result(id, res); }
                    default: return Error(id, -32601, "method not found: " + method);
                }
            }
            catch (Exception e) { Interlocked.Increment(ref Errors); return Error(id, -32603, e.Message); }
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
            if (v is double) { double d = (double)v; sb.Append(d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(IC) : d.ToString("R", IC)); return; }
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
                    default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            sb.Append('"');
        }
    }

    // schema helpers
    public static class Schema
    {
        public static Json.Obj Object(params object[] props)   // name, type, description, required(bool) ...
        {
            var o = new Json.Obj(); o["type"] = "object";
            var p = new Json.Obj(); var req = new List<object>();
            for (int i = 0; i + 3 < props.Length; i += 4)
            {
                var d = new Json.Obj(); d["type"] = (string)props[i + 1]; d["description"] = (string)props[i + 2];
                p[(string)props[i]] = d;
                if ((bool)props[i + 3]) req.Add(props[i]);
            }
            o["properties"] = p;
            if (req.Count > 0) o["required"] = req;
            o["additionalProperties"] = false;
            return o;
        }
    }
}
