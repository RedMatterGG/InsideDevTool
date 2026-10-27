using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace InsideDev.McpExt
{
    // Extension.cs (owner HOST): InsideDev.Mcp.dll, the Model Context Protocol server inside the game. Loaded by InsideDev
    // from _mod\InsideDev.Mcp.dll. Endpoints http://127.0.0.1:47811/mcp (dev), /mcp/play, /mcp/all (Streamable HTTP).
    // Clients that only speak stdio use InsideDev.McpStdio.exe --profile play|dev|all. Tools come from McpRegistry
    // providers: DevToolsProvider (DevTools.cs, handlers in DevToolsCalls.cs) and InsideDev.Play.dll, plus providers
    // HostDiscovery finds in other InsideDev.*.dll files. Call path: HostDispatch (HostDispatch.cs); main-thread queue:
    // MainQueue (HostContext.cs), pumped from Tick. Contract: docs/MCP_PLAY_DESIGN.md.
    // Restore (McpRegistry.RestoreAll after cancelling running jobs): mcp off, game quit, mcp restore, the last MCP
    // session ending by DELETE (McpServer.SessionClosed; Tick restores only after EditorState mcp.restore_on_close_s,
    // default 60, 0 = never, with still no session, no call in flight and no control job: clients that open a session
    // per call must not get the game restored between calls), and EditorState mcp.idle_restore_s (default 600, 0 =
    // never) seconds without any tool call. A change of the mcp.tools allowlist bumps the tool list (list_changed).
    // Bridge / console: mcp [status|on|off|port <n>|jobs|providers|restore|selftest|test]
    public sealed class McpExtension : IInsideDevExtension, IToolHost, IDisposable
    {
        public string Name { get { return "MCP server (" + (server != null && server.Running ? "http://127.0.0.1:" + server.Port + McpServer.Path : "off") + ")"; } }
        // serverInfo.version, /health, /ping: <mcp version>+mcp.<id>.play.<id> (McpBuild); the core's Boot.Version is
        // reported by mcp status, observe meta and the bridge ping
        public string ServerVersion { get { return McpBuild.ServerVersion(PlayId()); } }

        static volatile string playId;

        // the build id of the loaded InsideDev.Play.dll (null until it is loaded)
        static string PlayId()
        {
            var id = playId;
            if (id != null) return id;
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "InsideDev.Play") { id = McpBuild.IdOf(a); playId = id; return id; }
            }
            catch (Exception) { }
            return null;
        }

        static string BuildLine()
        {
            string p = PlayId();
            return "build: mcp " + McpBuild.Self + (p != null ? ", play " + PlayVersion() + "+" + p : ", play not loaded") + ", core " + Boot.Version;
        }

        static string PlayVersion()
        {
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "InsideDev.Play") continue;
                    var t = a.GetType("InsideDev.Play.PlayShared", false);
                    var f = t != null ? t.GetField("BuildVersion") : null;
                    return f != null ? Convert.ToString(f.GetValue(null), CultureInfo.InvariantCulture) : "?";
                }
            }
            catch (Exception) { }
            return "?";
        }

        McpServer server;
        DevCore core;
        DevToolsProvider dev;
        DevToolsCalls calls;
        static volatile bool captureBusy;
        static long closedAtTicks;
        int ticks;

        public void Log(string msg) { HostLog.Write(msg); }

        // game quit: close the listener and open connections, or their threads keep the process alive
        public void Dispose()
        {
            try { if (server != null) server.Stop(); } catch { }
            try { RestoreNow("game quit"); } catch { }
        }

        public void Start(DevCore core)
        {
            this.core = core;
            string dir = Extensions.Dir;
            AuditLog.FilePath = Path.Combine(dir, "mcp_audit.jsonl");
            McpShot.ShotsDir = Path.Combine(dir, "shots");
            MainQueue.FrameSource = () => Time.frameCount;
            HostJobs.CaptureBusy = () => captureBusy;
            ReadSettings();
            calls = new DevToolsCalls(core);
            dev = new DevToolsProvider(calls.Call);
            dev.restoreHook = calls.Restore;
            McpRegistry.Add(dev);
            try { HostDiscovery.Check(); } catch (Exception e) { DevLog.Error("[mcp] provider discovery", e); }
            if (EditorState.Get("mcp.enabled", "1") == "1") StartServer();
        }

        static void ReadSettings()
        {
            string tools = HostSettings.Tools;
            HostSettings.Tools = EditorState.Get("mcp.tools", "");
            HostSettings.Token = EditorState.Get("mcp.token", "");
            int idle;
            if (!int.TryParse(EditorState.Get("mcp.idle_restore_s", "600"), NumberStyles.Integer, CultureInfo.InvariantCulture, out idle)) idle = 600;
            HostSettings.IdleRestoreS = idle;
            int close;
            if (!int.TryParse(EditorState.Get("mcp.restore_on_close_s", "60"), NumberStyles.Integer, CultureInfo.InvariantCulture, out close)) close = 60;
            HostSettings.RestoreOnCloseS = close;
            HostSettings.SaveGuard = EditorState.Get("play.save_guard", false);
            if (tools != HostSettings.Tools && settingsRead) { try { McpRegistry.Changed(); } catch { } }
            settingsRead = true;
        }

        static bool settingsRead;

        string StartServer()
        {
            int port; if (!int.TryParse(EditorState.Get("mcp.port", "47811"), out port)) port = 47811;
            try
            {
                if (server == null) { server = new McpServer(this); server.SessionClosed += OnSessionClosed; }
                server.Start(port);
                return "MCP server on http://127.0.0.1:" + port + McpServer.Path;
            }
            catch (Exception e) { DevLog.Write("[mcp] could not listen on 127.0.0.1:" + port + ": " + e.Message); return "could not listen on port " + port + ": " + e.Message; }
        }

        // worker thread (DELETE of a session): the restore itself runs in Tick after the grace time; server Stop is
        // covered by mcp off / quit
        void OnSessionClosed(string sid, int left)
        {
            var s = server;
            if (left == 0 && s != null && s.Running) Interlocked.Exchange(ref closedAtTicks, DateTime.UtcNow.Ticks);
        }

        // main thread: a new session cancels the pending restore; it runs once the grace time passed with no session,
        // no call in flight and no control job
        void CheckClosed()
        {
            long closed = Interlocked.Read(ref closedAtTicks);
            if (closed == 0) return;
            var s = server;
            int grace = HostSettings.RestoreOnCloseS;
            if (s == null || !s.Running || grace <= 0 || s.SessionCount > 0) { Interlocked.CompareExchange(ref closedAtTicks, 0, closed); return; }
            if (HostDispatch.Active > 0 || HostJobs.Running(McpLanes.Control) != null) return;
            if (DateTime.UtcNow.Ticks - closed < grace * TimeSpan.TicksPerSecond) return;
            if (Interlocked.CompareExchange(ref closedAtTicks, 0, closed) != closed) return;
            try { RestoreNow("last MCP session closed " + grace + " s ago"); } catch (Exception e) { DevLog.Error("[mcp] restore after the last session", e); }
        }

        public void Tick(DevCore core)
        {
            ticks++;
            foreach (var line in HostLog.Drain()) DevLog.Write("[mcp] " + line);
            try { captureBusy = MultiCam.Capturing; } catch { captureBusy = false; }
            if (ticks % 30 == 0) { try { ReadSettings(); } catch { } }
            MainQueue.Pump();
            CheckClosed();
            if (ticks % 15 == 0) { try { HostDiscovery.Check(); } catch (Exception e) { DevLog.Error("[mcp] provider discovery", e); } }
            if (ticks % 60 == 0)
            {
                HostJobs.Sweep();
                if (HostDispatch.IdleRestoreDue(HostSettings.IdleRestoreS)) RestoreNow("no tool call for " + HostSettings.IdleRestoreS + " s");
            }
        }

        // main thread: cancel running jobs, then every provider's Restore
        string RestoreNow(string why)
        {
            int n = HostJobs.CancelAll(why);
            var rep = McpRegistry.RestoreAll();
            HostDispatch.MarkRestored();
            string s = "restore (" + why + "): " + (n > 0 ? n + " job(s) cancelled, " : "") + (rep.Count == 0 ? "nothing to put back" : string.Join("; ", rep.ToArray()));
            DevLog.Write("[mcp] " + s);
            return s;
        }

        public string Command(List<string> a, DevCore core)
        {
            if (a.Count == 0 || a[0].ToLowerInvariant() != "mcp") return null;
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "on": EditorState.Set("mcp.enabled", "1"); return StartServer();
                case "off":
                    {
                        EditorState.Set("mcp.enabled", "0");
                        if (server != null) server.Stop();
                        return "MCP server off\n" + RestoreNow("mcp off");
                    }
                case "port":
                    {
                        int p; if (a.Count < 3 || !int.TryParse(a[2], out p) || p < 1024 || p > 65535) return "usage: mcp port <1024-65535>";
                        EditorState.Set("mcp.port", p.ToString(CultureInfo.InvariantCulture)); return StartServer();
                    }
                case "restore": return RestoreNow("mcp restore");
                case "jobs":
                    {
                        var sb = new StringBuilder();
                        foreach (var j in HostJobs.All()) sb.Append(JsonOut.Write(j.Describe())).Append('\n');
                        return sb.Length == 0 ? "no jobs" : sb.ToString();
                    }
                case "providers":
                    {
                        var sb = new StringBuilder();
                        foreach (var p in McpRegistry.Providers())
                        {
                            int n = 0; try { var t = p.Tools(); n = t != null ? t.Count : 0; } catch { }
                            sb.Append(p.ProviderName).Append(": ").Append(n).Append(" tools (").Append(p.GetType().FullName).Append(")\n");
                        }
                        return sb.Length == 0 ? "no providers" : sb.ToString();
                    }
                case "selftest": SelfTest.Start(server != null ? server.Port : 47811); return "MCP self-test started (real HTTP to the endpoint from a worker thread); 'mcp test' shows the result";
                case "test": return SelfTest.Result;
                default:
                    if (server == null || !server.Running) return "MCP server off (mcp on)";
                    var running = HostJobs.Running(McpLanes.Control);
                    return "MCP server http://127.0.0.1:" + server.Port + McpServer.Path + " (/mcp/play, /mcp/all)   requests " + server.Requests + "  tool calls " + server.ToolCalls + "  errors " + server.Errors +
                           "\n  " + BuildLine() +
                           "\n  last client: " + server.LastClient + (server.LastError.Length > 0 ? "\n  last error: " + server.LastError : "") +
                           "\n  tools: play " + McpRegistry.ToolsFor(McpProfile.Play).Count + ", dev " + McpRegistry.ToolsFor(McpProfile.Dev).Count + ", all " + McpRegistry.ToolsFor(McpProfile.All).Count +
                           "  providers " + McpRegistry.Providers().Count + "  list version " + McpRegistry.Version +
                           "\n  control lane: " + (running != null ? running.Tool + " (" + running.Id + ")" : captureBusy ? "mcam capture" : "free") + "  calls in flight " + HostDispatch.Active + "  main queue " + MainQueue.Pending +
                           (HostSettings.Tools.Length > 0 ? "\n  allowlist: " + HostSettings.Tools : "") + (HostSettings.Token.Length > 0 ? "\n  token required" : "");
            }
        }

        // ---------------------------------------------------------------- IToolHost (server side, worker threads)
        public string AuthToken { get { return HostSettings.Token; } }
        public long ListVersion { get { return McpRegistry.Version; } }
        public bool Allowed(string profile, string tool) { return HostSettings.Allowed(profile, tool); }
        public List<ToolDef> Tools(string profile) { return McpRegistry.ToolsFor(profile); }
        public List<ResourceDef> Resources(string profile) { return McpRegistry.ResourcesFor(profile); }
        public List<ResourceTemplateDef> ResourceTemplates(string profile) { return McpRegistry.TemplatesFor(profile); }
        public List<PromptDef> Prompts(string profile) { return McpRegistry.PromptsFor(profile); }
        public string Instructions(string profile) { return McpRegistry.InstructionsFor(profile, BaseInstructions(profile)); }
        public ToolResult Call(ToolDef tool, Json.Obj args, CallInfo call) { return HostDispatch.Call(tool, args, call); }
        public ResourceContents ReadResource(string uri, CallInfo call) { return HostDispatch.ReadResource(uri, call); }
        public PromptResult GetPrompt(string name, Json.Obj args, CallInfo call) { return HostDispatch.GetPrompt(name, args, call); }

        static string BaseInstructions(string profile)
        {
            if (profile == McpProfile.Play) return "InsideDev play tools for INSIDE.";
            return "InsideDev runs inside Playdead's INSIDE (Unity 5.0) and edits the live game. " +
                   "Objects are named by id from observe (o12), a hierarchy path (A/B/C), #instanceID or a unique name. " +
                   "Start with game_state, find_objects and inspect; use screenshot to see the game. " +
                   "Edits (set_active, fsm, signal, command) change the running game and are recorded in InsideDev's History (undo with command 'undo'). " +
                   "command runs any InsideDev bridge/console command; command 'help' lists them.";
        }
    }

    // in-game check: a real HTTP MCP session against our own endpoints, from a worker thread
    static class SelfTest
    {
        public static volatile string Result = "not run (mcp selftest)";
        public static void Start(int port)
        {
            Result = "running...";
            new Thread(() =>
            {
                var sb = new StringBuilder();
                try
                {
                    string sid;
                    var r1 = Post(port, "/mcp", null, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"insidedev-selftest\",\"version\":\"1\"}}}", out sid);
                    sb.Append("initialize: ").Append(Cut(r1)).Append("\n  session ").Append(sid).Append('\n');
                    string dummy;
                    var r2 = Post(port, "/mcp", sid, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", out dummy);
                    sb.Append("initialized: ").Append(Cut(r2)).Append('\n');
                    var r3 = Post(port, "/mcp", sid, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", out dummy);
                    sb.Append("tools/list (dev): ").Append(Count(r3, "\"inputSchema\"")).Append(" tools\n");
                    var r4 = Post(port, "/mcp", sid, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"game_state\",\"arguments\":{}}}", out dummy);
                    sb.Append("tools/call game_state: ").Append(Cut(r4)).Append('\n');
                    var r5 = Post(port, "/mcp", sid, "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"screenshot\",\"arguments\":{\"max_width\":640}}}", out dummy);
                    sb.Append("tools/call screenshot: ").Append(r5.Length).Append(" bytes, image ").Append(r5.Contains("\"type\":\"image\"") ? "yes" : "NO").Append("  ").Append(Cut(System.Text.RegularExpressions.Regex.Replace(r5, "\"data\":\"[^\"]*\"", "\"data\":\"...\""))).Append('\n');
                    var r6 = Post(port, "/mcp", "not-a-session", "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"ping\"}", out dummy);
                    sb.Append("unknown session: ").Append(Cut(r6)).Append('\n');
                    string psid;
                    var p1 = Post(port, "/mcp/play", null, "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"insidedev-selftest\",\"version\":\"1\"}}}", out psid);
                    sb.Append("initialize /mcp/play: ").Append(Cut(p1)).Append('\n');
                    Post(port, "/mcp/play", psid, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", out dummy);
                    var p2 = Post(port, "/mcp/play", psid, "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"tools/list\"}", out dummy);
                    sb.Append("tools/list (play): ").Append(Count(p2, "\"inputSchema\"")).Append(" tools\n");
                    var p3 = Post(port, "/mcp/play", psid, "{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"tools/call\",\"params\":{\"name\":\"observe\",\"arguments\":{}}}", out dummy);
                    sb.Append("tools/call observe: structured ").Append(p3.Contains("\"structuredContent\"") ? "yes" : "NO").Append("  ").Append(Cut(p3)).Append('\n');
                    var p4 = Post(port, "/mcp/play", psid, "{\"jsonrpc\":\"2.0\",\"id\":14,\"method\":\"tools/call\",\"params\":{\"name\":\"game_state\",\"arguments\":{}}}", out dummy);
                    sb.Append("dev tool on /mcp/play (expect unknown tool): ").Append(Cut(p4)).Append('\n');
                    string none;
                    sb.Append("close both sessions (DELETE): ").Append(Cut(Send("DELETE", port, "/mcp", sid, "", out none))).Append(" / ")
                      .Append(Cut(Send("DELETE", port, "/mcp/play", psid, "", out none))).Append('\n');
                    sb.Append("PASS");
                }
                catch (Exception e) { sb.Append("FAIL: ").Append(e.Message); }
                Result = sb.ToString();
            }) { IsBackground = true, Name = "InsideDev MCP selftest" }.Start();
        }

        static int Count(string s, string what) { int n = 0, i = 0; while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i++; } return n; }

        static string Cut(string s) { s = s.Replace("\r", "").Replace("\n", " | "); return s.Length > 400 ? s.Substring(0, 400) + "..." : s; }

        static string Post(int port, string path, string sid, string body, out string newSid) { return Send("POST", port, path, sid, body, out newSid); }

        static string Send(string method, int port, string path, string sid, string body, out string newSid)
        {
            newSid = null;
            using (var c = new TcpClient("127.0.0.1", port))
            using (var s = c.GetStream())
            {
                var b = Encoding.UTF8.GetBytes(body);
                var h = method + " " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nContent-Type: application/json\r\nAccept: application/json, text/event-stream\r\nMCP-Protocol-Version: 2025-06-18\r\n" +
                        (sid != null ? "Mcp-Session-Id: " + sid + "\r\n" : "") + (HostSettings.Token.Length > 0 ? "Authorization: Bearer " + HostSettings.Token + "\r\n" : "") +
                        "Content-Length: " + b.Length + "\r\nConnection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(h); s.Write(hb, 0, hb.Length); s.Write(b, 0, b.Length); s.Flush();
                s.ReadTimeout = 70000;
                var ms = new MemoryStream(); var buf = new byte[65536]; int k;
                while ((k = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, k);
                string resp = Encoding.UTF8.GetString(ms.ToArray());
                int he = resp.IndexOf("\r\n\r\n");
                string head = he >= 0 ? resp.Substring(0, he) : resp;
                foreach (var line in head.Split('\n')) if (line.StartsWith("Mcp-Session-Id:", StringComparison.OrdinalIgnoreCase)) newSid = line.Substring(15).Trim();
                return head.Split('\n')[0].Trim() + " " + (he >= 0 ? resp.Substring(he + 4) : "");
            }
        }
    }
}
