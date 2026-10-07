using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace InsideDev.McpExt
{
    // InsideDev.McpStdio.exe: MCP over stdio for clients that launch a local process (Claude Desktop and most others).
    // Forwards every message to the game's endpoint http://127.0.0.1:<port>/mcp. Answers initialize/ping itself, so the
    // client can connect before the game is running; while INSIDE is not running, tools/list offers only inside_status,
    // and when the game appears (or goes away) the client is told the tool list changed.
    //   InsideDev.McpStdio.exe [--port 47811]
    public static class StdioProxy
    {
        static string url = "http://127.0.0.1:47811/mcp";
        static string sid, protocol = McpServer.Versions[0];
        static readonly object outLock = new object(), gameLock = new object();
        static volatile bool reportedUp = false, listed = false;
        static TextWriter stdout;

        public static int Main(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--port") url = "http://127.0.0.1:" + int.Parse(args[i + 1]) + "/mcp"; else if (args[i] == "--url") url = args[i + 1];
            var enc = new UTF8Encoding(false);
            stdout = new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true, NewLine = "\n" };
            var stdin = new StreamReader(Console.OpenStandardInput(), enc);
            ServicePointManager.Expect100Continue = false;
            new Thread(Watch) { IsBackground = true }.Start();
            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                object msg;
                try { msg = Json.Parse(line); }
                catch (Exception e) { Send(Err(null, -32700, "parse error: " + e.Message)); continue; }
                var m = msg as Json.Obj;
                if (m == null) { Send(Err(null, -32600, "batches are not supported over stdio")); continue; }
                // handle each request on its own thread so a slow tool call doesn't block pings
                var mm = m;
                new Thread(() => { try { var r = Handle(mm); if (r != null) Send(r); } catch (Exception e) { Send(Err(mm["id"], -32603, e.Message)); } }) { IsBackground = true }.Start();
            }
            return 0;
        }

        static void Send(object o) { string s = JsonOut.Write(o); lock (outLock) stdout.WriteLine(s); }
        static Json.Obj Err(object id, int code, string msg) { var e = new Json.Obj(); e["code"] = (double)code; e["message"] = msg; var o = new Json.Obj(); o["jsonrpc"] = "2.0"; o["id"] = id; o["error"] = e; return o; }
        static Json.Obj Res(object id, object r) { var o = new Json.Obj(); o["jsonrpc"] = "2.0"; o["id"] = id; o["result"] = r; return o; }

        const string DownText = "INSIDE is not running, or InsideDev / InsideDev.Mcp.dll is not loaded. Start the game; the tool list updates when it is up.";

        static object Handle(Json.Obj m)
        {
            string method = m.Str("method"); object id = m["id"];
            if (method == null) return null;
            bool isRequest = m.ContainsKey("id");
            var p = m["params"] as Json.Obj ?? new Json.Obj();
            if (method == "initialize")
            {
                string want = p.Str("protocolVersion", McpServer.Versions[0]);
                protocol = Array.IndexOf(McpServer.Versions, want) >= 0 ? want : McpServer.Versions[0];
                var caps = new Json.Obj(); var t = new Json.Obj(); t["listChanged"] = true; caps["tools"] = t;
                var si = new Json.Obj(); si["name"] = "insidedev"; si["title"] = "InsideDev (INSIDE runtime editor)"; si["version"] = "0.5.0";
                var r = new Json.Obj(); r["protocolVersion"] = protocol; r["capabilities"] = caps; r["serverInfo"] = si;
                r["instructions"] = "Tools act on the running game INSIDE through the InsideDev editor. If only inside_status is listed, the game is not running yet.";
                return Res(id, r);
            }
            if (!isRequest) return null;          // notifications (initialized, cancelled) stay here
            if (method == "ping") return Res(id, new Json.Obj());

            string body = JsonOut.Write(m), reply;
            int code = Forward(body, out reply);
            if (code == 200 && reply.Length > 0)
            {
                if (method == "tools/list") { listed = true; reportedUp = true; }
                return Json.Parse(reply);
            }
            if (code == 202) return null;
            // game not reachable
            if (method == "tools/list")
            {
                listed = true; reportedUp = false;
                var tool = new Json.Obj(); tool["name"] = "inside_status"; tool["description"] = "Whether INSIDE with InsideDev is running and reachable.";
                tool["inputSchema"] = Schema.Object();
                var r = new Json.Obj(); r["tools"] = new List<object> { tool }; return Res(id, r);
            }
            if (method == "tools/call")
            {
                var r = new Json.Obj(); var c = new Json.Obj(); c["type"] = "text";
                c["text"] = p.Str("name") == "inside_status" && code == 200 ? "INSIDE is running." : DownText + (reply.Length > 0 ? " (" + reply + ")" : "");
                r["content"] = new List<object> { c }; r["isError"] = p.Str("name") != "inside_status"; return Res(id, r);
            }
            return Err(id, -32000, DownText);
        }

        // POST to the game; opens (or reopens after a game restart) the game-side session as needed
        static int Forward(string body, out string reply)
        {
            lock (gameLock)
            {
                if (sid == null && !OpenSession(out reply)) return 0;
            }
            int code = Post(body, sid, out reply);
            if (code == 404)
            {
                lock (gameLock) { sid = null; if (!OpenSession(out reply)) return 0; }
                code = Post(body, sid, out reply);
            }
            if (code == 0) sid = null;
            return code;
        }

        static bool OpenSession(out string err)
        {
            string r; string s;
            int code = Post("{\"jsonrpc\":\"2.0\",\"id\":\"proxy-init\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + protocol + "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"insidedev-stdio\",\"version\":\"0.5.0\"}}}", null, out r, out s);
            if (code != 200 || s == null) { err = r; return false; }
            sid = s;
            string d; Post("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", sid, out d);
            err = ""; return true;
        }

        static int Post(string body, string session, out string reply) { string s; return Post(body, session, out reply, out s); }
        static int Post(string body, string session, out string reply, out string newSid)
        {
            newSid = null; reply = "";
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST"; req.ContentType = "application/json"; req.Accept = "application/json, text/event-stream";
                req.Timeout = 90000; req.ReadWriteTimeout = 90000; req.Proxy = null; req.KeepAlive = true;
                req.Headers["MCP-Protocol-Version"] = protocol;
                if (session != null) req.Headers["Mcp-Session-Id"] = session;
                var b = Encoding.UTF8.GetBytes(body); req.ContentLength = b.Length;
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    newSid = resp.Headers["Mcp-Session-Id"];
                    using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) reply = rd.ReadToEnd();
                    return (int)resp.StatusCode;
                }
            }
            catch (WebException e)
            {
                var hr = e.Response as HttpWebResponse;
                if (hr != null) { using (hr) using (var rd = new StreamReader(hr.GetResponseStream())) reply = rd.ReadToEnd(); return (int)hr.StatusCode; }
                reply = e.Message; return 0;
            }
            catch (Exception e) { reply = e.Message; return 0; }
        }

        // tell the client when the game comes up or goes away (after it has seen a tool list)
        static void Watch()
        {
            while (true)
            {
                Thread.Sleep(3000);
                if (!listed) continue;
                bool up;
                lock (gameLock) { string r; up = sid != null ? Post("{\"jsonrpc\":\"2.0\",\"id\":\"proxy-ping\",\"method\":\"ping\"}", sid, out r) == 200 : OpenSession(out r); if (!up) sid = null; }
                if (up != reportedUp)
                {
                    reportedUp = up;
                    var n = new Json.Obj(); n["jsonrpc"] = "2.0"; n["method"] = "notifications/tools/list_changed";
                    Send(n);
                }
            }
        }
    }
}
