using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
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
    // Restore (running jobs are cancelled, then McpRegistry.RestoreAll once they have ended): mcp off, game quit, mcp
    // restore, and two automatic ones: EditorState mcp.restore_on_close_s (default 60, 0 = never) seconds after the last
    // live MCP session went away (DELETE, or a client that was killed: McpServer.LiveSessions; clients that open a
    // session per call must not get the game restored between calls), and EditorState mcp.idle_restore_s (default 600,
    // 0 = never) seconds without any tool call. The automatic ones wait while a call runs, while a job with a live owner
    // runs and while a module registered with McpKeepAlive is busy. A change of the mcp.tools allowlist bumps the tool
    // list (list_changed).
    // Bridge / console: mcp [status [json]|on|off|port <n>|jobs|providers|sessions [json]|restore|selftest|test]
    // Console only (they decide who may use the server): mcp token new|clear|show, mcp tools [<allowlist>|clear],
    // mcp hosts [<names>|clear]
    public sealed class McpExtension : IInsideDevExtension, IToolHost, IDisposable
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public string Name { get { return "MCP server (" + (server != null && server.Running ? "http://127.0.0.1:" + server.Port + McpServer.Path : "off") + ")"; } }
        // serverInfo.version, /health, /ping: <mcp version>+mcp.<id>.play.<id> (McpBuild); the core's version is
        // reported by mcp status, observe meta and the bridge ping
        public string ServerVersion { get { return McpBuild.ServerVersion(PlayId()); } }

        static volatile string playId, coreVersion, playVersion, versionProblem = "";

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

        // The version of the InsideDev.dll that is loaded. Boot.Version is a constant: reading it in code gives the
        // version this DLL was built against, so it is read from the loaded assembly.
        static string CoreVersion()
        {
            var v = coreVersion;
            if (v != null) return v;
            try
            {
                var f = typeof(Boot).GetField("Version", BindingFlags.Public | BindingFlags.Static);
                v = f != null ? Convert.ToString(f.GetValue(null), IC) : null;
            }
            catch (Exception) { v = null; }
            coreVersion = v ?? "?";
            return coreVersion;
        }

        static string PlayVersion()
        {
            var v = playVersion;
            if (v != null) return v;
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "InsideDev.Play") continue;
                    var t = a.GetType("InsideDev.Play.PlayShared", false);
                    var f = t != null ? t.GetField("BuildVersion") : null;
                    v = f != null ? Convert.ToString(f.GetValue(null), IC) : "?";
                    playVersion = v;
                    return v;
                }
            }
            catch (Exception) { }
            return "?";
        }

        static string BuildLine()
        {
            string p = PlayId();
            return "build: mcp " + McpBuild.Self + (p != null ? ", play " + PlayVersion() + "+" + p : ", play not loaded") + ", core " + CoreVersion();
        }

        // null when the loaded modules are of one release; else what differs (files of two releases mixed in _mod)
        static string VersionProblem()
        {
            string core = CoreVersion();
            string play = PlayId() != null ? PlayVersion() : null;
            var sb = new StringBuilder();
            if (core != McpBuild.Version) sb.Append("InsideDev.Mcp.dll is ").Append(McpBuild.Version).Append(" but InsideDev.dll is ").Append(core);
            if (play != null && play != McpBuild.Version) sb.Append(sb.Length > 0 ? "; " : "").Append("InsideDev.Play.dll is ").Append(play).Append(" but InsideDev.Mcp.dll is ").Append(McpBuild.Version);
            string s = sb.Length == 0 ? null : sb.Append(": the files in _mod come from different releases; copy all of them from one release").ToString();
            versionProblem = s ?? "";
            return s;
        }

        McpServer server;
        DevCore core;
        DevToolsProvider dev;
        DevToolsCalls calls;
        static volatile bool captureBusy, sessionEnded;
        static volatile string heldBy;
        int ticks;
        // main thread: the restore after the last live session went away, and the restore that waits for cancelled jobs
        bool hadLive;
        long closedAtTicks, pendingAtTicks;
        string pendingRestore, listenError, hostsParsed;
        int pendingJobs;
        string[] extraHosts;

        public void Log(string msg) { HostLog.Write(msg); }

        // game quit: close the listener and open connections, or their threads keep the process alive
        public void Dispose()
        {
            try { if (server != null) server.Stop(); } catch { }
            try { pendingRestore = null; RestoreNow("game quit", 0); } catch { }
            try { McpShot.FreeTextures(); } catch { }
        }

        public void Start(DevCore core)
        {
            this.core = core;
            string dir = Extensions.Dir;
            AuditLog.FilePath = Path.Combine(dir, "mcp_audit.jsonl");
            McpShot.ShotsDir = Path.Combine(dir, "shots");
            MainQueue.FrameSource = () => Time.frameCount;
            HostJobs.CaptureBusy = () => captureBusy;
            Remote.ObjectRefusal = DevToolsCalls.ObjectRefusal;
            DescribeKeys();
            ReadSettings();
            calls = new DevToolsCalls(core);
            dev = new DevToolsProvider(calls.Call);
            dev.restoreHook = calls.Restore;
            McpRegistry.Add(dev);
            McpKeepAlive.Register("mcam", () => captureBusy ? "a MultiCam capture is running" : null);
            McpKeepAlive.Register("acap", () => AudioCapture.capturing ? "an audio capture is running" : AudioCapture.finalizing ? "an audio capture is being finished" : null);
            try { Doctor.Register("mcp", DoctorRows); } catch (Exception e) { DevLog.Error("[mcp] doctor", e); }
            try { HostDiscovery.Check(); } catch (Exception e) { DevLog.Error("[mcp] provider discovery", e); }
            string problem = VersionProblem();
            if (problem != null) DevLog.Write("[mcp] warning: " + problem);
            if (EditorState.Get("mcp.enabled", "1") == "1") StartServer();
        }

        static void DescribeKeys()
        {
            EditorState.Describe("mcp", "mcp.enabled", "1", "MCP server on at game start (1 / 0); the commands mcp on / mcp off set it");
            EditorState.Describe("mcp", "mcp.port", "47811", "TCP port of the MCP server on 127.0.0.1 and [::1] (1024-65535); mcp port <n> sets it");
            EditorState.Describe("mcp", "mcp.tools", "", "tool allowlist: names, <profile>:<tool>, <profile>:* or *, separated by commas or spaces; empty = every tool");
            EditorState.Describe("mcp", "mcp.token", "", "when set, every request needs the header Authorization: Bearer <token>; mcp token new / clear in the console");
            EditorState.Describe("mcp", "mcp.hosts", "", "more names accepted in the Host header next to 127.0.0.1, localhost and [::1] (a port forward under another name)");
            EditorState.Describe("mcp", "mcp.idle_restore_s", "600", "seconds without a tool call after which the game is put back to normal play (0 = never)");
            EditorState.Describe("mcp", "mcp.restore_on_close_s", "60", "seconds after the last MCP client went away before the game is put back to normal play (0 = never)");
            EditorState.Describe("mcp", "mcp.text_copy", "full", "full = every tool result also carries its JSON as text; short = a one-line summary there (for clients that read structuredContent)");
            EditorState.KeyInfo known;
            if (!EditorState.Known("play.save_guard", out known))
                EditorState.Describe("mcp", "play.save_guard", "0", "1 = the command tool refuses game loads that skip the save backup (load, spawn, reload, respawn, cspawn go)");
        }

        void ReadSettings()
        {
            string tools = HostSettings.Tools;
            HostSettings.Tools = EditorState.Get("mcp.tools", "");
            HostSettings.Token = EditorState.Get("mcp.token", "");
            int idle;
            if (!int.TryParse(EditorState.Get("mcp.idle_restore_s", "600"), NumberStyles.Integer, IC, out idle)) idle = 600;
            HostSettings.IdleRestoreS = idle;
            int close;
            if (!int.TryParse(EditorState.Get("mcp.restore_on_close_s", "60"), NumberStyles.Integer, IC, out close)) close = 60;
            HostSettings.RestoreOnCloseS = close;
            HostSettings.SaveGuard = EditorState.Get("play.save_guard", false);
            HostSettings.TextCopy = EditorState.Get("mcp.text_copy", "full");
            ToolResult.ShortTextCopy = HostSettings.TextCopy == "short";
            string hosts = EditorState.Get("mcp.hosts", "");
            if (!ReferenceEquals(hosts, hostsParsed) && hosts != hostsParsed)
            {
                var l = new List<string>();
                foreach (var part in hosts.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string n = McpServer.HostName(part);
                    if (n.Length > 0 && !l.Contains(n)) l.Add(n);
                }
                extraHosts = l.Count > 0 ? l.ToArray() : null;
                HostSettings.Hosts = hosts;
                var s = server;
                if (s != null) s.ExtraHosts = extraHosts;
            }
            hostsParsed = hosts;
            if (tools != HostSettings.Tools && settingsRead) { try { McpRegistry.Changed(); } catch { } }
            settingsRead = true;
        }

        static bool settingsRead;

        static int Port()
        {
            int port;
            if (!int.TryParse(EditorState.Get("mcp.port", "47811"), NumberStyles.Integer, IC, out port) || port < 1 || port > 65535) port = 47811;
            return port;
        }

        // the lines a client needs; the token itself is not printed (mcp token show)
        static string ClientLines(int port)
        {
            bool token = HostSettings.Token.Length > 0;
            string p = port.ToString(IC);
            return "\n  HTTP client:  http://127.0.0.1:" + p + "/mcp/play (an AI that plays), /mcp/dev (inspect and edit), /mcp/all" + (token ? "   with the header  Authorization: Bearer <token>" : "") +
                   "\n  stdio client: InsideDev.McpStdio.exe --profile play|dev|all" + (port != 47811 ? " --port " + p : "") + (token ? " --token <token>" : "") +
                   "\n  example:      claude mcp add --transport http inside-play http://127.0.0.1:" + p + "/mcp/play" + (token ? " --header \"Authorization: Bearer <token>\"" : "");
        }

        string StartServer()
        {
            int port = Port();
            if (server != null && server.Running && server.Port == port)
                return "MCP server already on http://127.0.0.1:" + port.ToString(IC) + McpServer.Path + ClientLines(port);
            try
            {
                if (server == null)
                {
                    server = new McpServer(this);
                    server.SessionClosed += OnSessionClosed;
                    server.StatusSource = StatusFields;
                    HostDispatch.SessionLive = server.SessionLive;
                }
                server.ExtraHosts = extraHosts;
                server.Start(port);
                listenError = null;
                hadLive = false; closedAtTicks = 0;
                return "MCP server on http://127.0.0.1:" + port.ToString(IC) + McpServer.Path + ClientLines(port);
            }
            catch (Exception e)
            {
                listenError = "could not listen on 127.0.0.1:" + port.ToString(IC) + ": " + e.Message;
                DevLog.Write("[mcp] " + listenError);
                return "could not listen on port " + port.ToString(IC) + ": " + e.Message;
            }
        }

        // worker thread: a session ended (DELETE, expiry). Sessions that live for one call are over before the next
        // look at the live count, so their end is noted here.
        static void OnSessionClosed(string sid, int left) { sessionEnded = true; }

        // main thread, about once a second: the grace time starts when the last live session goes away (a new one
        // cancels it); the restore runs once it passed with no call in flight, no job that has an owner and no busy
        // module, and only when a tool or command ran since the last restore
        void CheckSessions(McpServer s)
        {
            bool ended = sessionEnded;
            if (ended) sessionEnded = false;
            int live = s.LiveSessions;
            long now = DateTime.UtcNow.Ticks;
            if (live > 0) { hadLive = true; closedAtTicks = 0; return; }
            if (hadLive || ended) { hadLive = false; closedAtTicks = now; }
            if (closedAtTicks == 0) return;
            int grace = HostSettings.RestoreOnCloseS;
            if (grace <= 0 || !HostDispatch.CallsSinceRestore) { closedAtTicks = 0; return; }
            if (now - closedAtTicks < grace * TimeSpan.TicksPerSecond) return;
            string held = null;
            if (HostDispatch.Active > 0) held = "a tool call is running";
            else
            {
                var job = HostJobs.Blocking(McpLanes.Control, HostDispatch.SessionLive);
                held = job != null ? "job " + job.Id + " (" + job.Tool + ")" : McpKeepAlive.Busy();
            }
            if (held != null) { heldBy = "restore after the last session waits for " + held; return; }
            closedAtTicks = 0;
            try { RequestRestore("last MCP session closed " + grace.ToString(IC) + " s ago"); } catch (Exception e) { DevLog.Error("[mcp] restore after the last session", e); }
        }

        static readonly Action pumpStep = MainQueue.Pump;
        static readonly Action logStep = DrainLog;
        static readonly Action discoveryStep = Discover;

        static void DrainLog()
        {
            foreach (var line in HostLog.Drain()) DevLog.Write("[mcp] " + line);
        }

        static void Discover()
        {
            try { HostDiscovery.Check(); } catch (Exception e) { DevLog.Error("[mcp] provider discovery", e); }
        }

        // every frame; with no client and nothing queued this takes no lock and allocates nothing
        public void Tick(DevCore core)
        {
            ticks++;
            if (HostLog.Pending > 0) Perf.RunNested("mcp.log", logStep);
            try { captureBusy = MultiCam.Capturing; } catch { captureBusy = false; }
            if (MainQueue.HasWork) Perf.RunNested("mcp.pump", pumpStep);
            else MainQueue.Pump();
            if (pendingRestore != null) CheckPendingRestore();
            if (ticks % 15 == 0) Perf.RunNested("mcp.discovery", discoveryStep);
            if (ticks % 30 == 0) { try { ReadSettings(); } catch { } }
            if (ticks % 60 == 0) Slow();
        }

        void Slow()
        {
            HostJobs.Sweep();
            McpShot.Trim();
            heldBy = null;
            var s = server;
            if (s != null && s.Running) CheckSessions(s);
            string held;
            if (HostDispatch.IdleRestoreDue(HostSettings.IdleRestoreS, out held)) RequestRestore("no tool call for " + HostSettings.IdleRestoreS.ToString(IC) + " s");
            else if (held != null) heldBy = "idle restore waits for " + held;
        }

        // main thread. Cancelling a job only sets its flag: the job's own loop still needs the main thread to release
        // inputs and re-freeze. So with jobs running the providers' Restore runs a moment later (when the jobs have
        // ended, 2 s at most), or it would be undone in part by the jobs' clean-up.
        string RequestRestore(string why)
        {
            int n = HostJobs.CancelAll(why);
            if (n == 0) return RestoreNow(why, 0);
            pendingRestore = why; pendingJobs = n; pendingAtTicks = DateTime.UtcNow.Ticks;
            string s = "restore (" + why + "): " + n.ToString(IC) + " job(s) cancelled; the game is put back when they have ended (2 s at most)";
            DevLog.Write("[mcp] " + s);
            return s;
        }

        void CheckPendingRestore()
        {
            if (HostJobs.AnyRunning && DateTime.UtcNow.Ticks - pendingAtTicks < 2 * TimeSpan.TicksPerSecond) return;
            string why = pendingRestore;
            int n = pendingJobs;
            pendingRestore = null;
            try { RestoreNow(why, n); } catch (Exception e) { DevLog.Error("[mcp] restore", e); }
        }

        // main thread: cancel what still runs, then every provider's Restore
        string RestoreNow(string why, int cancelledBefore)
        {
            int n = cancelledBefore + HostJobs.CancelAll(why);
            var rep = McpRegistry.RestoreAll();
            HostDispatch.MarkRestored();
            string s = "restore (" + why + "): " + (n > 0 ? n.ToString(IC) + " job(s) cancelled, " : "") + (rep.Count == 0 ? "nothing to put back" : string.Join("; ", rep.ToArray()));
            DevLog.Write("[mcp] " + s);
            return s;
        }

        static string ConsoleOnly(string what)
        {
            return "refused: " + what + " decides who may use this MCP server; it works in the in-game console only, not through the server";
        }

        public string Command(List<string> a, DevCore core)
        {
            if (a.Count == 0 || a[0].ToLowerInvariant() != "mcp") return null;
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            bool json = a.Count > 1 && a[a.Count - 1].ToLowerInvariant() == "json";
            bool viaServer = DevToolsCalls.InMcp > 0;
            switch (sub)
            {
                case "on":
                    // from inside a call the server is never restarted (the port in the config may have changed)
                    if (viaServer && server != null && server.Running) return "MCP server already on http://127.0.0.1:" + server.Port.ToString(IC) + McpServer.Path;
                    EditorState.Set("mcp.enabled", "1");
                    return StartServer();
                case "off":
                    {
                        if (viaServer) return "refused: mcp off would stop this MCP server from inside a call; use the in-game console";
                        EditorState.Set("mcp.enabled", "0");
                        if (server != null) server.Stop();
                        hadLive = false; closedAtTicks = 0;
                        return "MCP server off\n" + RequestRestore("mcp off");
                    }
                case "port":
                    {
                        if (viaServer) return "refused: mcp port would stop this MCP server from inside a call; use the in-game console";
                        int p; if (a.Count < 3 || !int.TryParse(a[2], NumberStyles.Integer, IC, out p) || p < 1024 || p > 65535) return "usage: mcp port <1024-65535>";
                        EditorState.Set("mcp.port", p.ToString(IC)); return StartServer();
                    }
                case "restore": return RequestRestore("mcp restore");
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
                case "sessions": return Sessions(json);
                case "token": return viaServer ? ConsoleOnly("mcp token") : Token(a);
                case "tools": return viaServer ? ConsoleOnly("mcp tools") : Setting(a, "mcp.tools", "allowlist", "every tool is served");
                case "hosts": return viaServer ? ConsoleOnly("mcp hosts") : Setting(a, "mcp.hosts", "extra host names", "only 127.0.0.1, localhost and [::1] are accepted");
                case "selftest": SelfTest.Start(server != null ? server.Port : 47811); return "MCP self-test started (real HTTP to the endpoint from a worker thread); 'mcp test' shows the result";
                case "test": return SelfTest.Result;
                default: return Status(json);
            }
        }

        string Status(bool json)
        {
            if (json)
            {
                if (server == null || !server.Running) return JsonLine.Obj("ok", true, "running", false, "enabled", EditorState.Get("mcp.enabled", "1") == "1", "error", listenError ?? "");
                var o = server.Status();
                o["running"] = true;
                return JsonLine.Write(o);
            }
            if (server == null || !server.Running) return "MCP server off (mcp on)" + (listenError != null ? "\n  " + listenError : "");
            var running = HostJobs.Running(McpLanes.Control);
            string problem = VersionProblem();
            var busy = McpKeepAlive.BusyAll();
            string held = heldBy;
            return "MCP server http://127.0.0.1:" + server.Port + McpServer.Path + " (/mcp/play, /mcp/all)   requests " + server.Requests + "  tool calls " + server.ToolCalls + "  errors " + server.Errors +
                   "\n  " + BuildLine() + (problem != null ? "\n  warning: " + problem : "") +
                   "\n  last client: " + server.LastClient + (server.LastError.Length > 0 ? "\n  last error: " + server.LastError : "") +
                   "\n  tools: play " + McpRegistry.ToolsFor(McpProfile.Play).Count + ", dev " + McpRegistry.ToolsFor(McpProfile.Dev).Count + ", all " + McpRegistry.ToolsFor(McpProfile.All).Count +
                   "  providers " + McpRegistry.Providers().Count + "  list version " + McpRegistry.Version +
                   "\n  control lane: " + (running != null ? running.Tool + " (" + running.Id + ")" : captureBusy ? "mcam capture" : "free") + "  calls in flight " + HostDispatch.Active + "  main queue " + MainQueue.Pending +
                   "\n  sessions: " + server.SessionCount + " (" + server.LiveSessions + " live)  refused requests " + server.Refused + (server.Ipv6 ? "  also on [::1]" : "") +
                   "\n  restore: idle after " + HostSettings.IdleRestoreS + " s, " + HostSettings.RestoreOnCloseS + " s after the last client" + (held != null ? "; " + held : "") +
                   (busy.Count > 0 ? "\n  busy modules (automatic restores wait): " + string.Join("; ", busy.ToArray()) : "") +
                   (HostSettings.Tools.Length > 0 ? "\n  allowlist: " + HostSettings.Tools : "") + (HostSettings.Token.Length > 0 ? "\n  token required" : "") +
                   (HostSettings.Hosts.Length > 0 ? "\n  extra hosts: " + HostSettings.Hosts : "") + (HostSettings.TextCopy == "short" ? "\n  text copy: short" : "");
        }

        string Sessions(bool json)
        {
            var list = server != null && server.Running ? server.DescribeSessions() : new List<object>();
            if (json) return JsonLine.Obj("ok", true, "count", list.Count, "sessions", list);
            if (list.Count == 0) return "no MCP sessions";
            var sb = new StringBuilder();
            sb.Append(list.Count).Append(" MCP session(s)");
            foreach (var x in list)
            {
                var o = (Json.Obj)x;
                sb.Append("\n  ").Append(o.Str("id")).Append("  ").Append(o.Str("profile")).Append("  ").Append(o.Bool("live") ? "live" : "silent")
                  .Append("  idle ").Append(o.Str("idle_s")).Append(" s  age ").Append(o.Str("age_s")).Append(" s  ").Append(o.Bool("stream") ? "stream open" : "no stream")
                  .Append("  calls ").Append(o.Str("calls")).Append("  requests ").Append(o.Str("requests")).Append("  ").Append(o.Str("client"));
            }
            return sb.ToString();
        }

        // mcp token new | clear | show
        string Token(List<string> a)
        {
            string op = a.Count > 2 ? a[2].ToLowerInvariant() : "";
            switch (op)
            {
                case "new":
                    {
                        var b = new byte[16];
                        new System.Security.Cryptography.RNGCryptoServiceProvider().GetBytes(b);
                        string t = McpBuild.Hex(b, b.Length);
                        EditorState.Set("mcp.token", t);
                        HostSettings.Token = t;
                        return "mcp token set: " + t + "\n  every client now needs the header  Authorization: Bearer " + t +
                               "\n  stdio launcher: --token " + t + "  (or the environment variable INSIDEDEV_MCP_TOKEN); inside_ctl.py: --token " + t +
                               "\n  clients that are connected without it get 401 from their next request on";
                    }
                case "clear":
                    EditorState.Set("mcp.token", "");
                    HostSettings.Token = "";
                    return "mcp token cleared: requests need no token (the Host / Origin checks still keep web pages out)";
                case "show":
                    return HostSettings.Token.Length > 0 ? "mcp token: " + HostSettings.Token : "mcp token: none (mcp token new makes one)";
                default: return "usage: mcp token new|clear|show";
            }
        }

        // mcp tools | mcp hosts: show, set or clear one list setting
        string Setting(List<string> a, string key, string what, string whenEmpty)
        {
            if (a.Count < 3)
            {
                string cur = EditorState.Get(key, "");
                return key + ": " + (cur.Trim().Length > 0 ? cur : "(empty: " + whenEmpty + ")");
            }
            string v = a.Count == 3 && a[2].ToLowerInvariant() == "clear" ? "" : string.Join(" ", a.GetRange(2, a.Count - 2).ToArray());
            EditorState.Set(key, v);
            try { ReadSettings(); } catch { }
            return key + " = " + (v.Length > 0 ? v : "(empty: " + whenEmpty + ")") + "  (" + what + ")";
        }

        // ---------------------------------------------------------------- status without the main thread
        // GET /status and inside_status (called on a connection thread): only values that are safe to read from any
        // thread. Nothing here waits for the game or touches a Unity object.
        Json.Obj StatusFields()
        {
            var o = new Json.Obj();
            o["mcp"] = McpBuild.Self;
            o["core"] = CoreVersion();
            string problem = versionProblem;
            if (!string.IsNullOrEmpty(problem)) o["version_problem"] = problem;
            var mt = new Json.Obj();
            double stalled = MainQueue.StalledS;
            mt["alive"] = MainQueue.Alive;
            mt["stalled_s"] = stalled < 0 ? -1 : Math.Round(stalled, 2);
            mt["queue"] = (double)MainQueue.Pending;
            o["main_thread"] = mt;
            o["frame"] = (double)MainQueue.Frame;
            var job = HostJobs.Running(McpLanes.Control);
            if (job != null)
            {
                var c = new Json.Obj();
                c["job"] = job.Id; c["tool"] = job.Tool; c["elapsed_s"] = Math.Round(job.ElapsedMs / 1000.0, 1);
                o["control"] = c;
            }
            else if (captureBusy)
            {
                var c = new Json.Obj();
                c["job"] = "mcam"; c["tool"] = "mcam";
                o["control"] = c;
            }
            else o["control"] = null;
            o["calls_running"] = (double)HostDispatch.Active;
            string screen = HostDispatch.LastScreen;
            if (screen != null) { o["screen"] = screen; o["screen_frame"] = (double)HostDispatch.LastScreenFrame; }
            var err = HostDispatch.LastError();
            if (err != null) o["last_tool_error"] = err;
            var w = HostWindow.State();
            if (w != null) o["window"] = w;
            var tools = new Json.Obj();
            tools["play"] = (double)McpRegistry.ToolsFor(McpProfile.Play).Count;
            tools["dev"] = (double)McpRegistry.ToolsFor(McpProfile.Dev).Count;
            tools["all"] = (double)McpRegistry.ToolsFor(McpProfile.All).Count;
            o["tools"] = tools;
            o["play_loaded"] = PlayId() != null;
            var rs = new Json.Obj();
            rs["idle_s"] = (double)HostSettings.IdleRestoreS;
            rs["on_close_s"] = (double)HostSettings.RestoreOnCloseS;
            var last = HostDispatch.LastCallUtc;
            if (last != DateTime.MinValue) rs["last_call_ago_s"] = Math.Round((DateTime.UtcNow - last).TotalSeconds, 1);
            string held = heldBy;
            if (held != null) rs["held"] = held;
            o["restore"] = rs;
            o["token_required"] = HostSettings.Token.Length > 0;
            return o;
        }

        // the "mcp" rows of the doctor command (main thread, read-only)
        List<Doctor.Row> DoctorRows()
        {
            var rows = new List<Doctor.Row>();
            bool want = EditorState.Get("mcp.enabled", "1") == "1";
            var s = server;
            if (s != null && s.Running)
                rows.Add(new Doctor.Row("mcp", "server", Doctor.Ok, "listening on 127.0.0.1:" + s.Port.ToString(IC) + (s.Ipv6 ? " and [::1]" : "") + ", " + s.SessionCount.ToString(IC) + " session(s), " + s.Requests.ToString(IC) + " request(s)"));
            else if (listenError != null) rows.Add(new Doctor.Row("mcp", "server", Doctor.Fail, listenError, "another program holds the port: close it, or pick another with mcp port <n>"));
            else rows.Add(new Doctor.Row("mcp", "server", want ? Doctor.Warn : Doctor.Info, "off", "mcp on"));
            string problem = VersionProblem();
            rows.Add(problem == null
                ? new Doctor.Row("mcp", "versions", Doctor.Ok, BuildLine())
                : new Doctor.Row("mcp", "versions", Doctor.Warn, problem, "copy every file of one release into _mod"));
            int play = McpRegistry.ToolsFor(McpProfile.Play).Count, devCount = McpRegistry.ToolsFor(McpProfile.Dev).Count;
            rows.Add(new Doctor.Row("mcp", "tools", PlayId() != null ? Doctor.Ok : Doctor.Info,
                "play " + play.ToString(IC) + ", dev " + devCount.ToString(IC) + ", all " + McpRegistry.ToolsFor(McpProfile.All).Count.ToString(IC) + (PlayId() != null ? "" : "; InsideDev.Play.dll is not loaded (observe, act and the other play tools are missing)"),
                PlayId() != null ? null : "copy InsideDev.Play.dll into _mod, then ext load"));
            rows.Add(new Doctor.Row("mcp", "access", Doctor.Info,
                (HostSettings.Token.Length > 0 ? "token required" : "no token (any program of this machine may call; web pages are refused by the Host / Origin checks)") +
                (HostSettings.Tools.Trim().Length > 0 ? "; allowlist " + HostSettings.Tools : "") + (HostSettings.Hosts.Trim().Length > 0 ? "; extra hosts " + HostSettings.Hosts : "") +
                (s != null && s.Refused > 0 ? "; " + s.Refused.ToString(IC) + " request(s) refused" : "")));
            double stalled = MainQueue.StalledS;
            rows.Add(new Doctor.Row("mcp", "queue", MainQueue.Pending > 20 ? Doctor.Warn : Doctor.Ok,
                MainQueue.Pending.ToString(IC) + " main-thread item(s) waiting, " + HostDispatch.Active.ToString(IC) + " call(s) running" + (stalled >= 0 ? "" : ", never pumped")));
            if (s != null && s.LastError.Length > 0) rows.Add(new Doctor.Row("mcp", "last error", Doctor.Info, s.LastError));
            return rows;
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
        // every request that reaches a tool or command counts as use of the editor: background scanners wake up
        public ToolResult Call(ToolDef tool, Json.Obj args, CallInfo call) { Background.Touch(); return HostDispatch.Call(tool, args, call); }
        public ResourceContents ReadResource(string uri, CallInfo call) { Background.Touch(); return HostDispatch.ReadResource(uri, call); }
        public PromptResult GetPrompt(string name, Json.Obj args, CallInfo call) { Background.Touch(); return HostDispatch.GetPrompt(name, args, call); }

        static string BaseInstructions(string profile)
        {
            if (profile == McpProfile.Play) return "InsideDev play tools for INSIDE.";
            return "InsideDev runs inside Playdead's INSIDE (Unity 5.0) and edits the live game. " +
                   "Objects are named by id from observe (o12), a hierarchy path (A/B/C), #instanceID or a name (exact name, else text inside the name; of several matches the one nearest to the boy). " +
                   "Start with game_state, find_objects and inspect; use screenshot to see the game. " +
                   "Edits (set_active, fsm, signal, command) change the running game and are recorded in InsideDev's History (undo with command 'undo'). " +
                   "command runs any InsideDev bridge/console command; command 'help' lists them.";
        }
    }

    // the game window from any thread (Win32 only; null where that is not available): {minimized, focused}
    static class HostWindow
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder name, int max);

        static readonly object gate = new object();
        static IntPtr window;
        static bool unavailable;
        static uint pid;
        static DateTime lookedAt = DateTime.MinValue;

        internal static Json.Obj State()
        {
            if (unavailable) return null;
            try
            {
                IntPtr h;
                lock (gate)
                {
                    if (window != IntPtr.Zero && !IsWindow(window)) window = IntPtr.Zero;
                    if (window == IntPtr.Zero && (DateTime.UtcNow - lookedAt).TotalSeconds > 2)
                    {
                        lookedAt = DateTime.UtcNow;
                        if (pid == 0) pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                        IntPtr found = IntPtr.Zero;
                        EnumProc cb = (w, l) =>
                        {
                            uint p;
                            GetWindowThreadProcessId(w, out p);
                            if (p != pid || !IsWindowVisible(w)) return true;
                            var sb = new StringBuilder(64);
                            GetClassName(w, sb, 64);
                            if (sb.ToString() != "UnityWndClass") return true;
                            found = w;
                            return false;
                        };
                        EnumWindows(cb, IntPtr.Zero);
                        GC.KeepAlive(cb);
                        window = found;
                    }
                    h = window;
                }
                if (h == IntPtr.Zero) return null;
                var o = new Json.Obj();
                o["minimized"] = IsIconic(h);
                o["focused"] = GetForegroundWindow() == h;
                return o;
            }
            catch (Exception) { unavailable = true; return null; }
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
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var r5 = Post(port, "/mcp", sid, "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"screenshot\",\"arguments\":{\"max_width\":640}}}", out dummy);
                    sb.Append("tools/call screenshot: ").Append(r5.Length).Append(" bytes in ").Append(clock.ElapsedMilliseconds).Append(" ms, image ").Append(r5.Contains("\"type\":\"image\"") ? "yes" : "NO").Append("  ").Append(Cut(System.Text.RegularExpressions.Regex.Replace(r5, "\"data\":\"[^\"]*\"", "\"data\":\"...\""))).Append('\n');
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
                    string ping = "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"ping\"}";
                    sb.Append("Origin: null (expect 403): ").Append(Cut(Send("POST", port, "/mcp", sid, ping, out dummy, "Origin: null\r\n", null))).Append('\n');
                    sb.Append("Origin of a web site (expect 403): ").Append(Cut(Send("POST", port, "/mcp", sid, ping, out dummy, "Origin: https://example.com\r\n", null))).Append('\n');
                    sb.Append("Host of a web site (expect 403): ").Append(Cut(Send("POST", port, "/mcp", sid, ping, out dummy, null, "example.com"))).Append('\n');
                    sb.Append("Sec-Fetch-Site: cross-site (expect 403): ").Append(Cut(Send("POST", port, "/mcp", sid, ping, out dummy, "Sec-Fetch-Site: cross-site\r\n", null))).Append('\n');
                    sb.Append("text/plain body on /mcp (expect 415): ").Append(Cut(Send("POST", port, "/mcp", sid, ping, out dummy, "Content-Type: text/plain\r\n", null))).Append('\n');
                    sb.Append("GET /status: ").Append(Cut(Send("GET", port, "/status", null, "", out dummy, null, null))).Append('\n');
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

        static string Send(string method, int port, string path, string sid, string body, out string newSid) { return Send(method, port, path, sid, body, out newSid, null, null); }

        // extra = more header lines (a Content-Type line there replaces application/json); host = another Host value
        static string Send(string method, int port, string path, string sid, string body, out string newSid, string extra, string host)
        {
            newSid = null;
            using (var c = new TcpClient("127.0.0.1", port))
            using (var s = c.GetStream())
            {
                var b = Encoding.UTF8.GetBytes(body);
                bool ownType = extra != null && extra.IndexOf("Content-Type:", StringComparison.OrdinalIgnoreCase) >= 0;
                var h = method + " " + path + " HTTP/1.1\r\nHost: " + (host ?? "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)) + "\r\n" + (ownType ? "" : "Content-Type: application/json\r\n") +
                        "Accept: application/json, text/event-stream\r\nMCP-Protocol-Version: 2025-06-18\r\n" + (extra ?? "") +
                        (sid != null ? "Mcp-Session-Id: " + sid + "\r\n" : "") + (HostSettings.Token.Length > 0 ? "Authorization: Bearer " + HostSettings.Token + "\r\n" : "") +
                        "Content-Length: " + b.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(h); s.Write(hb, 0, hb.Length); s.Write(b, 0, b.Length); s.Flush();
                s.ReadTimeout = 70000;
                var ms = new MemoryStream(); var buf = new byte[65536]; int k;
                while ((k = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, k);
                string resp = Encoding.UTF8.GetString(ms.ToArray());
                int he = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                string head = he >= 0 ? resp.Substring(0, he) : resp;
                foreach (var line in head.Split('\n')) if (line.StartsWith("Mcp-Session-Id:", StringComparison.OrdinalIgnoreCase)) newSid = line.Substring(15).Trim();
                return head.Split('\n')[0].Trim() + " " + (he >= 0 ? resp.Substring(he + 4) : "");
            }
        }
    }
}
