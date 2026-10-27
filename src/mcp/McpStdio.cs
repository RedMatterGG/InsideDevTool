using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace InsideDev.McpExt
{
    // McpStdio.cs (owner SERVER): InsideDev.McpStdio.exe, MCP over stdio for clients that launch a local process. Relays
    // every message to the game's endpoint (docs/MCP_PLAY_DESIGN.md 4.6):
    //   InsideDev.McpStdio.exe [--profile play|dev|all] [--port 47811] [--url http://127.0.0.1:47811/mcp] [--token <t>]
    // --profile picks /mcp (dev, default), /mcp/play or /mcp/all; the token also comes from INSIDEDEV_MCP_TOKEN.
    // initialize: when the game is up, its capabilities, serverInfo (version) and instructions; when it is down, the
    // proxy's own short instructions and a tool list with only inside_status. Requests with a progress token are relayed
    // as they stream (each SSE event is one stdout line); notifications/cancelled is forwarded to the game; the game's
    // list_changed notifications are relayed from its GET stream, and the proxy itself sends list_changed when the game
    // comes up or goes away. The HTTP timeout (90 s) is above the game's tool budget (55 s). On stdin EOF the game
    // session is closed (DELETE), which lets the game restore its state. inside_status, also while the game is up,
    // returns the game's instructions: a client that initialized before the game started never got them otherwise.
    public static class StdioProxy
    {
        // the proxy build: <version>+stdio.<id> (McpBuild: id = the first 8 hex digits of the exe's SHA-256)
        static readonly string ProxyVersion = McpBuild.Version + "+stdio." + McpBuild.IdOf(typeof(McpBuild).Assembly);
        const int CallTimeoutMs = 90000, ShortTimeoutMs = 5000, StreamReadTimeoutMs = 60000;
        static string url = "http://127.0.0.1:47811/mcp", profile = McpProfile.Dev, token = "";
        static string protocol = McpServer.Versions[0];
        static volatile string sid;
        static Json.Obj gameInit;
        static readonly object outLock = new object(), gameLock = new object();
        static volatile bool reportedUp, listed, stopping;
        static volatile int lastCode;
        static TextWriter stdout;

        public static int Main(string[] args)
        {
            int port = 47811;
            string fullUrl = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i], next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a)
                {
                    case "--port":
                        if (next == null || !int.TryParse(next, out port) || port < 1 || port > 65535) return Usage("--port needs a number 1-65535");
                        i++; break;
                    case "--url":
                        if (next == null) return Usage("--url needs a value");
                        fullUrl = next; i++; break;
                    case "--profile":
                        if (next != McpProfile.Play && next != McpProfile.Dev && next != McpProfile.All) return Usage("--profile must be play, dev or all");
                        profile = next; i++; break;
                    case "--token":
                        if (next == null) return Usage("--token needs a value");
                        token = next; i++; break;
                    case "--help": case "-h": case "/?": Usage(null); return 0;
                    default: return Usage("unknown argument " + a);
                }
            }
            if (token.Length == 0) token = (Environment.GetEnvironmentVariable("INSIDEDEV_MCP_TOKEN") ?? "").Trim();
            url = fullUrl ?? "http://127.0.0.1:" + port + (profile == McpProfile.Dev ? McpServer.Path : McpServer.Path + "/" + profile);
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 64;
            ServicePointManager.UseNagleAlgorithm = false;
            var enc = new UTF8Encoding(false);
            stdout = new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true, NewLine = "\n" };
            var stdin = new StreamReader(Console.OpenStandardInput(), enc);
            new Thread(Watch) { IsBackground = true, Name = "insidedev watch" }.Start();
            new Thread(Listen) { IsBackground = true, Name = "insidedev listen" }.Start();
            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                object msg;
                try { msg = Json.Parse(line); }
                catch (Exception e) { Send(Err(null, -32700, "parse error: " + e.Message)); continue; }
                var m = msg as Json.Obj;
                if (m == null) { Send(Err(null, -32600, "batches are not supported over stdio")); continue; }
                var mm = m;
                new Thread(() => { try { var r = Handle(mm); if (r != null) Send(r); } catch (Exception e) { if (mm.ContainsKey("id")) Send(Err(mm["id"], -32603, e.Message)); } }) { IsBackground = true }.Start();
            }
            stopping = true;
            CloseSession();
            return 0;
        }

        static int Usage(string error)
        {
            if (error != null) Console.Error.WriteLine("InsideDev.McpStdio: " + error);
            Console.Error.WriteLine("usage: InsideDev.McpStdio.exe [--profile play|dev|all] [--port 47811] [--url <endpoint>] [--token <token>]   (token also from INSIDEDEV_MCP_TOKEN)");
            return error != null ? 2 : 0;
        }

        static void Send(object o)
        {
            string s = o as string ?? JsonOut.Write(o);
            lock (outLock) stdout.WriteLine(s);
        }

        static Json.Obj Err(object id, int code, string msg) { var e = new Json.Obj(); e["code"] = (double)code; e["message"] = msg; var o = new Json.Obj(); o["jsonrpc"] = "2.0"; o["id"] = id; o["error"] = e; return o; }
        static Json.Obj Res(object id, object r) { var o = new Json.Obj(); o["jsonrpc"] = "2.0"; o["id"] = id; o["result"] = r; return o; }
        static Json.Obj Note(string method) { var o = new Json.Obj(); o["jsonrpc"] = "2.0"; o["method"] = method; return o; }

        static string DownText
        {
            get
            {
                if (lastCode == 401) return "INSIDE refused the token: the game has EditorState mcp.token set; pass --token <token> or set INSIDEDEV_MCP_TOKEN.";
                return "INSIDE is not running at " + url + ", or InsideDev / InsideDev.Mcp.dll is not loaded. Start the game; the tool list updates when it is up.";
            }
        }

        static string OwnInstructions()
        {
            return "Tools act on the running game INSIDE through the InsideDev editor (profile " + profile + "). INSIDE is not reachable yet, so only inside_status is listed; " +
                   "the client is told when the tool list changes. Once the game is up, call inside_status once: its result has the game's instructions (step mode: the game is frozen between action calls; ids die on every load; at the title menu submit starts; call restore when done).";
        }

        static object Handle(Json.Obj m)
        {
            string method = m.Str("method");
            object id = m["id"];
            if (method == null) return null;
            bool isRequest = m.ContainsKey("id");
            var p = m["params"] as Json.Obj ?? new Json.Obj();
            if (method == "initialize") return Initialize(id, p);
            if (!isRequest) { if (method == "notifications/cancelled") ForwardNotification(m); return null; }
            if (method == "ping") return Res(id, new Json.Obj());
            if (method == "tools/call" && p.Str("name") == "inside_status") return Res(id, Status());
            string reply;
            bool relayed;
            int code = Forward(JsonOut.Write(m), id, out reply, out relayed);
            if (relayed) return null;
            if (code == 200 && reply.Length > 0)
            {
                if (method == "tools/list") { listed = true; reportedUp = true; }
                return OneLine(reply);
            }
            if (code == 202) return null;
            if (code == 400) return Err(id, -32600, "the game refused the request: " + Short(reply));
            string why = DownText + (reply.Length > 0 && code != 401 ? " (" + Short(reply) + ")" : "");
            switch (method)
            {
                case "tools/list":
                    {
                        listed = true; reportedUp = false;
                        var r = new Json.Obj(); r["tools"] = new List<object> { StatusTool().ToJson() }; return Res(id, r);
                    }
                case "tools/call":
                    return Res(id, ToolResult.Fail(McpErr.NotReady, why, "call inside_status to check, then retry when the game is up").ToJson());
                case "resources/list": { var r = new Json.Obj(); r["resources"] = new List<object>(); return Res(id, r); }
                case "resources/templates/list": { var r = new Json.Obj(); r["resourceTemplates"] = new List<object>(); return Res(id, r); }
                case "prompts/list": { var r = new Json.Obj(); r["prompts"] = new List<object>(); return Res(id, r); }
                default: return Err(id, -32000, why);
            }
        }

        static ToolDef StatusTool()
        {
            return new ToolDef
            {
                name = "inside_status", title = "INSIDE status", owner = "SERVER",
                description = "Whether INSIDE with InsideDev is running and reachable, and the game's instructions once it is up (read them if the session started before the game). While the game is down this is the only tool; the full list appears when it is up.",
                inputSchema = Schema.Obj().Build()
            }.ReadOnly();
        }

        static Json.Obj Status()
        {
            bool up;
            lock (gameLock) { up = sid != null ? Ping(sid) : OpenSession() == 200; if (!up) sid = null; }
            var s = new Json.Obj();
            s["running"] = up; s["endpoint"] = url; s["profile"] = profile;
            var si = gameInit != null ? gameInit["serverInfo"] as Json.Obj : null;
            if (up && si != null) s["version"] = si.Str("version", "");
            s["proxy_version"] = ProxyVersion;
            string ins = up && gameInit != null ? gameInit.Str("instructions", "") : "";
            if (ins.Length > 0) s["instructions"] = ins;
            s["text"] = up ? "INSIDE is running (InsideDev " + (si != null ? si.Str("version", "?") : "?") + ", " + url + ")." + (ins.Length > 0 ? " Read instructions: they say how to use the tools." : "") : DownText;
            return ToolResult.Ok(s).ToJson();
        }

        static Json.Obj Initialize(object id, Json.Obj p)
        {
            string want = p.Str("protocolVersion", McpServer.Versions[0]);
            protocol = Array.IndexOf(McpServer.Versions, want) >= 0 ? want : McpServer.Versions[0];
            Json.Obj game = null;
            lock (gameLock) { sid = null; if (OpenSession() == 200) game = gameInit; }
            reportedUp = game != null;
            var caps = new Json.Obj();
            var gc = game != null ? game["capabilities"] as Json.Obj : null;
            if (gc != null) foreach (var k in gc.order) if (gc.ContainsKey(k)) caps[k] = gc[k];
            foreach (var k in new[] { "tools", "resources", "prompts" })
            {
                var c = caps[k] as Json.Obj ?? new Json.Obj();
                c["listChanged"] = true;
                caps[k] = c;
            }
            var si = game != null ? game["serverInfo"] as Json.Obj : null;
            if (si == null) { si = new Json.Obj(); si["name"] = "insidedev"; si["title"] = "InsideDev (INSIDE runtime editor)"; si["version"] = ProxyVersion; }
            var r = new Json.Obj();
            r["protocolVersion"] = protocol; r["capabilities"] = caps; r["serverInfo"] = si;
            string text = game != null ? game.Str("instructions", "") : OwnInstructions();
            if (!string.IsNullOrEmpty(text)) r["instructions"] = text;
            return Res(id, r);
        }

        static string Short(string s) { s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); return s.Length > 300 ? s.Substring(0, 300) + "..." : s; }

        static string OneLine(string json)
        {
            if (json.IndexOf('\n') < 0 && json.IndexOf('\r') < 0) return json;
            try { return JsonOut.Write(Json.Parse(json)); } catch { return json.Replace("\r", "").Replace("\n", " "); }
        }

        // POST to the game; opens (or reopens after a game restart) the game-side session as needed
        static int Forward(string body, object id, out string reply, out bool relayed)
        {
            relayed = false;
            string s0;
            lock (gameLock)
            {
                if (sid == null) { int oc = OpenSession(); if (oc != 200) { reply = ""; return oc == 401 ? 401 : 0; } }
                s0 = sid;
            }
            string ns;
            int code = Post(body, s0, CallTimeoutMs, id, out reply, out ns, out relayed);
            if (code == 404 && !relayed)
            {
                lock (gameLock)
                {
                    if (sid == s0) { sid = null; int oc = OpenSession(); if (oc != 200) return oc == 401 ? 401 : 0; }
                    s0 = sid;
                }
                code = Post(body, s0, CallTimeoutMs, id, out reply, out ns, out relayed);
            }
            if (code == 0) lock (gameLock) if (sid == s0) sid = null;
            lastCode = code;
            return code;
        }

        static void ForwardNotification(Json.Obj m)
        {
            string s0 = sid;
            if (s0 == null) return;
            string reply, ns; bool relayed;
            Post(JsonOut.Write(m), s0, ShortTimeoutMs, null, out reply, out ns, out relayed);
        }

        // gameLock held: initialize + initialized on the game; 200 = ok (sid and gameInit set)
        static int OpenSession()
        {
            string r, s; bool relayed;
            string body = "{\"jsonrpc\":\"2.0\",\"id\":\"proxy-init\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + protocol +
                          "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"insidedev-stdio\",\"version\":\"" + ProxyVersion + "\"}}}";
            int code = Post(body, null, ShortTimeoutMs, null, out r, out s, out relayed);
            lastCode = code;
            if (code != 200 || s == null) return code == 200 ? 0 : code;
            try { var o = Json.Parse(r) as Json.Obj; gameInit = o != null ? o["result"] as Json.Obj : null; } catch { gameInit = null; }
            sid = s;
            string d, ns;
            Post("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", sid, ShortTimeoutMs, null, out d, out ns, out relayed);
            return 200;
        }

        static bool Ping(string session)
        {
            string r, ns; bool relayed;
            return Post("{\"jsonrpc\":\"2.0\",\"id\":\"proxy-ping\",\"method\":\"ping\"}", session, ShortTimeoutMs, null, out r, out ns, out relayed) == 200;
        }

        static HttpWebRequest Request(string method, string session, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs; req.Proxy = null;
            req.Headers["MCP-Protocol-Version"] = protocol;
            if (session != null) req.Headers["Mcp-Session-Id"] = session;
            if (token.Length > 0) req.Headers["Authorization"] = "Bearer " + token;
            return req;
        }

        // expectId != null: an SSE reply is relayed event by event to stdout (relayed = true); when the stream ends
        // without the response for expectId, an error response is sent instead
        static int Post(string body, string session, int timeoutMs, object expectId, out string reply, out string newSid, out bool relayed)
        {
            newSid = null; reply = ""; relayed = false;
            try
            {
                var req = Request("POST", session, timeoutMs);
                req.ContentType = "application/json"; req.Accept = "application/json, text/event-stream"; req.KeepAlive = true;
                var b = Encoding.UTF8.GetBytes(body); req.ContentLength = b.Length;
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    newSid = resp.Headers["Mcp-Session-Id"];
                    string ct = resp.ContentType ?? "";
                    using (var st = resp.GetResponseStream())
                    {
                        if (ct.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            relayed = expectId != null;
                            bool answered = Relay(st, expectId, ref reply);
                            if (expectId != null && !answered) Send(Err(expectId, -32000, "the game closed the stream before it answered (game quit or crashed?)"));
                        }
                        else using (var rd = new StreamReader(st, Encoding.UTF8)) reply = rd.ReadToEnd();
                    }
                    return (int)resp.StatusCode;
                }
            }
            catch (WebException e)
            {
                var hr = e.Response as HttpWebResponse;
                if (hr == null) { reply = e.Message; return 0; }
                using (hr)
                {
                    int code = (int)hr.StatusCode;
                    try { using (var rd = new StreamReader(hr.GetResponseStream(), Encoding.UTF8)) reply = rd.ReadToEnd(); } catch (Exception re) { reply = re.Message; }
                    return code;
                }
            }
            catch (Exception e) { reply = e.Message; return 0; }
        }

        // relays each event (expectId != null) or keeps the response in reply; true once the response was seen
        static bool Relay(Stream st, object expectId, ref string reply)
        {
            var rd = new SseReader(st);
            string ev, data;
            bool answered = false;
            string want = expectId != null ? JsonOut.Write(expectId) : null;
            while (rd.Next(out ev, out data))
            {
                if (data.Trim().Length == 0) continue;
                Json.Obj o = null;
                try { o = Json.Parse(data) as Json.Obj; } catch { }
                bool isResponse = o != null && o.Str("method") == null && o.ContainsKey("id");
                if (expectId == null) { if (isResponse) { reply = data; answered = true; } continue; }
                Send(OneLine(data));
                if (isResponse && JsonOut.Write(o["id"]) == want) answered = true;
            }
            return answered;
        }

        static void CloseSession()
        {
            string s0 = sid;
            if (s0 == null) return;
            try
            {
                var req = Request("DELETE", s0, 2000);
                using (var resp = (HttpWebResponse)req.GetResponse()) { }
            }
            catch { }
        }

        static void SendListChanged()
        {
            Send(Note("notifications/tools/list_changed"));
            Send(Note("notifications/resources/list_changed"));
            Send(Note("notifications/prompts/list_changed"));
        }

        // tell the client when the game comes up or goes away (after it has seen a tool list)
        static void Watch()
        {
            while (!stopping)
            {
                Thread.Sleep(3000);
                if (!listed) continue;
                bool up;
                lock (gameLock) { up = sid != null ? Ping(sid) : OpenSession() == 200; if (!up) sid = null; }
                if (up != reportedUp) { reportedUp = up; SendListChanged(); }
            }
        }

        // holds the game's GET notification stream while the game is up and relays its list_changed notifications
        static void Listen()
        {
            string unsupported = null;
            while (!stopping)
            {
                string s0 = sid;
                if (s0 == null || s0 == unsupported || !reportedUp) { Thread.Sleep(1000); continue; }
                if (OpenStream(s0) == 405) unsupported = s0;
                Thread.Sleep(2000);
            }
        }

        static int OpenStream(string s0)
        {
            try
            {
                var req = Request("GET", s0, ShortTimeoutMs);
                req.ReadWriteTimeout = StreamReadTimeoutMs;
                req.Accept = "text/event-stream"; req.KeepAlive = false;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((resp.ContentType ?? "").IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) < 0) return (int)resp.StatusCode;
                    using (var st = resp.GetResponseStream())
                    {
                        var rd = new SseReader(st);
                        string ev, data;
                        while (!stopping && sid == s0 && rd.Next(out ev, out data))
                        {
                            Json.Obj o = null;
                            try { o = Json.Parse(data) as Json.Obj; } catch { }
                            string method = o != null ? o.Str("method") : null;
                            if (method != null && method.StartsWith("notifications/", StringComparison.Ordinal) && method.EndsWith("list_changed", StringComparison.Ordinal)) Send(OneLine(data));
                        }
                    }
                    return (int)resp.StatusCode;
                }
            }
            catch (WebException e) { var hr = e.Response as HttpWebResponse; if (hr != null) { int c = (int)hr.StatusCode; hr.Close(); return c; } return 0; }
            catch { return 0; }
        }
    }
}
