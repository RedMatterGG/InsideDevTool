using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace InsideDev.McpExt
{
    // InsideDev.Mcp.dll: Model Context Protocol server inside the game, for AI assistants / LLM clients.
    // Loaded by InsideDev from _mod\InsideDev.Mcp.dll. Endpoint http://127.0.0.1:47811/mcp (Streamable HTTP).
    // Clients that only speak stdio use InsideDev.McpStdio.exe, which forwards to that endpoint.
    // Bridge / console: mcp [status|on|off|port <n>|selftest|test]
    public sealed class McpExtension : IInsideDevExtension, IToolHost
    {
        public string Name { get { return "MCP server (" + (server != null && server.Running ? "http://127.0.0.1:" + server.Port + McpServer.Path : "off") + ")"; } }
        public string ServerVersion { get { return Boot.Version; } }

        McpServer server;
        DevCore core;
        List<ToolDef> tools;

        // ---------------------------------------------------------------- main-thread dispatch
        sealed class Job { public Func<ToolResult> fn; public ToolResult result; public readonly ManualResetEvent done = new ManualResetEvent(false); }
        readonly Queue<Job> jobs = new Queue<Job>();
        static readonly Queue<string> logq = new Queue<string>();

        ToolResult OnMain(Func<ToolResult> fn, int timeoutMs = 60000)
        {
            var j = new Job { fn = fn };
            lock (jobs) jobs.Enqueue(j);
            if (!j.done.WaitOne(timeoutMs)) return ToolResult.Text("the game did not run the request within " + (timeoutMs / 1000) + " s (is it frozen or loading?)", true);
            return j.result;
        }

        public void Log(string msg) { lock (logq) logq.Enqueue(msg); }

        public void Start(DevCore core)
        {
            this.core = core;
            tools = BuildTools();
            if (EditorState.Get("mcp.enabled", "1") == "1") StartServer();
        }

        string StartServer()
        {
            int port; if (!int.TryParse(EditorState.Get("mcp.port", "47811"), out port)) port = 47811;
            try { if (server == null) server = new McpServer(this); server.Start(port); return "MCP server on http://127.0.0.1:" + port + McpServer.Path; }
            catch (Exception e) { DevLog.Write("[mcp] could not listen on 127.0.0.1:" + port + ": " + e.Message); return "could not listen on port " + port + ": " + e.Message; }
        }

        public void Tick(DevCore core)
        {
            lock (logq) while (logq.Count > 0) DevLog.Write("[mcp] " + logq.Dequeue());
            for (int n = 0; n < 8; n++)
            {
                Job j; lock (jobs) { if (jobs.Count == 0) break; j = jobs.Dequeue(); }
                try { j.result = j.fn(); } catch (Exception e) { j.result = ToolResult.Text(e.GetType().Name + ": " + e.Message, true); }
                j.done.Set();
            }
        }

        public string Command(List<string> a, DevCore core)
        {
            if (a.Count == 0 || a[0].ToLowerInvariant() != "mcp") return null;
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "on": EditorState.Set("mcp.enabled", "1"); return StartServer();
                case "off": EditorState.Set("mcp.enabled", "0"); if (server != null) server.Stop(); return "MCP server off";
                case "port":
                    {
                        int p; if (a.Count < 3 || !int.TryParse(a[2], out p) || p < 1024 || p > 65535) return "usage: mcp port <1024-65535>";
                        EditorState.Set("mcp.port", p.ToString()); return StartServer();
                    }
                case "selftest": SelfTest.Start(server != null ? server.Port : 47811); return "MCP self-test started (real HTTP to the endpoint from a worker thread); 'mcp test' shows the result";
                case "test": return SelfTest.Result;
                default:
                    if (server == null || !server.Running) return "MCP server off (mcp on)";
                    return "MCP server http://127.0.0.1:" + server.Port + McpServer.Path + "   requests " + server.Requests + "  tool calls " + server.ToolCalls + "  errors " + server.Errors +
                           "\n  last client: " + server.LastClient + (server.LastError.Length > 0 ? "\n  last error: " + server.LastError : "") + "\n  tools: " + tools.Count;
            }
        }

        // ---------------------------------------------------------------- tools
        public List<ToolDef> Tools() { return tools; }

        public string Instructions
        {
            get
            {
                return "InsideDev runs inside Playdead's INSIDE (Unity 5.0) and edits the live game. " +
                       "Objects are named by GameObject name (nearest match), a hierarchy path (A/B/C) or #instanceID. " +
                       "Start with game_state, find_objects and inspect; use screenshot to see the game. " +
                       "Edits (set_active, fsm, signal, command) change the running game and are recorded in InsideDev's History (undo with command 'undo'). " +
                       "command runs any InsideDev bridge/console command; command 'help' lists them.";
            }
        }

        static string Q(string s) { s = (s ?? "").Trim(); return s.IndexOf(' ') >= 0 && !s.StartsWith("\"") ? "\"" + s.Replace("\"", "") + "\"" : s; }

        List<ToolDef> BuildTools()
        {
            var l = new List<ToolDef>();
            l.Add(new ToolDef { name = "game_state", title = "Game state", readOnly = true,
                description = "Snapshot of the running game: InsideDev version, frame, the boy's position, current savepoint/chapter, music position, active scenes.",
                inputSchema = Schema.Object() });
            l.Add(new ToolDef { name = "screenshot", title = "Screenshot", readOnly = true,
                description = "Capture the game screen (including the InsideDev panels if open) and return it as a PNG image.",
                inputSchema = Schema.Object("max_width", "integer", "Scale the image down to at most this width in pixels (default 1280, 0 = full size).", false) });
            l.Add(new ToolDef { name = "find_objects", title = "Find objects", readOnly = true,
                description = "Smart search over loaded objects by name, ranked by match and what the object does (animation, sounds, scripts, triggers, state machines, characters). Returns up to 25 rows.",
                inputSchema = Schema.Object("query", "string", "Words to match against object names.", true,
                                            "category", "string", "Optional filter: animation, sounds, scripts, triggers, fsm, characters.", false) });
            l.Add(new ToolDef { name = "inspect", title = "Inspect object", readOnly = true,
                description = "Describe an object: path, active state, components and their fields, references. deep=true also expands nested game data.",
                inputSchema = Schema.Object("object", "string", "Object name, hierarchy path or #instanceID.", true,
                                            "deep", "boolean", "Expand nested serialized data.", false) });
            l.Add(new ToolDef { name = "select", title = "Select in editor", readOnly = false,
                description = "Select an object in the InsideDev editor (Inspector, Explorer and graph follow the selection).",
                inputSchema = Schema.Object("object", "string", "Object name, hierarchy path or #instanceID.", true) });
            l.Add(new ToolDef { name = "set_active", title = "Switch object on/off", readOnly = false,
                description = "Switch a GameObject on or off (recorded in History, undoable).",
                inputSchema = Schema.Object("object", "string", "Object name, hierarchy path or #instanceID.", true,
                                            "active", "boolean", "true = on, false = off.", true) });
            l.Add(new ToolDef { name = "fsm_graph", title = "PlayMaker FSM", readOnly = true,
                description = "Dump a PlayMaker state machine: states, actions, exits with events, current state, signal inputs. Also opens it in the FSM Graph panel.",
                inputSchema = Schema.Object("object", "string", "Object that has the FSM.", true,
                                            "fsm", "string", "FSM name when the object has several.", false) });
            l.Add(new ToolDef { name = "fsm_find", title = "Find in FSMs", readOnly = true,
                description = "Search every loaded PlayMaker FSM (including switched-off ones) for events, signal inputs, states, action settings and signal connections.",
                inputSchema = Schema.Object("text", "string", "Text to search for.", true) });
            l.Add(new ToolDef { name = "fsm", title = "Drive an FSM", readOnly = false,
                description = "Send an event to a PlayMaker FSM, force it into a state, or read/set one of its variables.",
                inputSchema = Schema.Object("object", "string", "Object that has the FSM.", true,
                                            "action", "string", "event, state or var.", true,
                                            "name", "string", "Event, state or variable name.", true,
                                            "value", "string", "New variable value (action var only; omit to read).", false,
                                            "fsm", "string", "FSM name when the object has several.", false) });
            l.Add(new ToolDef { name = "signal", title = "Fire a signal", readOnly = false,
                description = "Fire one of an object's Playdead signal outputs (raises every connection of that output, like the game does).",
                inputSchema = Schema.Object("object", "string", "Object with the signal output.", true,
                                            "output", "string", "Signal output name (see inspect / fsm_find).", true) });
            l.Add(new ToolDef { name = "show_panel", title = "Show an editor panel", readOnly = false,
                description = "Open the InsideDev editor and show a panel, optionally moving it to a dock and resizing that dock (useful before a screenshot). Panels: spawns, levels, triggers, hidden, world, audio, inspector, cheats, console, diag, settings, objects, explorer, graph, events, sounds, atimeline, wwiseapi, history, audiodb, logic, modified (Mods), fsmgraph, fsmfind. panel 'none' closes the editor.",
                inputSchema = Schema.Object("panel", "string", "Panel id (see description), or 'none' to close the editor.", true,
                                            "dock", "string", "Optional: left, right or bottom.", false,
                                            "size", "number", "Optional dock size as a fraction of the screen (left/right 0.12-0.6, bottom 0.1-0.7).", false) });
            l.Add(new ToolDef { name = "log", title = "InsideDev log", readOnly = true,
                description = "The last lines of InsideDev's log (command output, errors, events).",
                inputSchema = Schema.Object("lines", "integer", "How many lines (default 60, max 400).", false) });
            l.Add(new ToolDef { name = "command", title = "Run InsideDev command", readOnly = false,
                description = "Run any InsideDev bridge/console command and return its output (plus log lines it wrote). 'help' lists the commands, e.g. near, triggers, children, get/set, enable, call, sigs, mats, origin, postfx, history, undo, mod list, tp, god.",
                inputSchema = Schema.Object("command", "string", "The command line, e.g. 'near 10' or 'get \"Housing (4)\" Transform localPosition'.", true) });
            return l;
        }

        static int IntArg(Json.Obj a, string k, int def) { var v = a[k]; return v is double ? (int)(double)v : v is string ? (int.TryParse((string)v, out def) ? def : def) : def; }
        static bool BoolArg(Json.Obj a, string k, bool def) { var v = a[k]; if (v is bool) return (bool)v; var s = v as string; return s != null ? (s == "true" || s == "1" || s == "on") : def; }

        public ToolResult Call(string name, Json.Obj a)
        {
            switch (name)
            {
                case "game_state": return Run("state");
                case "screenshot": return Screenshot(IntArg(a, "max_width", 1280));
                case "find_objects":
                    {
                        string cat = a.Str("category", "");
                        return Run("sfind " + a.Str("query", "") + (cat.Length > 0 ? " cat:" + cat : ""));
                    }
                case "inspect": return Run("inspect " + a.Str("object", "") + (BoolArg(a, "deep", false) ? " deep" : ""));
                case "select": return Run("select " + a.Str("object", ""));
                case "set_active": return Run("setactive " + Q(a.Str("object")) + (BoolArg(a, "active", true) ? " on" : " off"));
                case "fsm_graph": return Run("fsmgraph " + a.Str("object", "") + (a.Str("fsm", "").Length > 0 ? " " + a.Str("fsm") : ""));
                case "fsm_find": return Run("fsmfind " + a.Str("text", ""));
                case "fsm":
                    {
                        string act = a.Str("action", "").ToLowerInvariant();
                        if (act != "event" && act != "state" && act != "var") return ToolResult.Text("action must be event, state or var", true);
                        string fs = a.Str("fsm", "");
                        return Run("fsm " + Q(a.Str("object")) + (fs.Length > 0 ? " " + Q(fs) : "") + " " + act + " " + Q(a.Str("name")) + (a["value"] != null ? " " + Q(a.Str("value")) : ""));
                    }
                case "signal": return Run("signal " + Q(a.Str("object")) + " " + Q(a.Str("output")));
                case "show_panel":
                    {
                        string pn = a.Str("panel", "").Trim(), dock = a.Str("dock", "").Trim().ToLowerInvariant();
                        if (pn == "none") return Run("panel off");
                        var r = Run("ui layout dock");
                        if (dock.Length > 0) r = Run("ui put " + pn + " " + dock); else r = Run("ui show " + pn);
                        var sz = a["size"];
                        if (dock.Length > 0 && sz is double) r = Run("ui dock " + dock + " " + ((double)sz).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        return r;
                    }
                case "log":
                    {
                        int n = Mathf.Clamp(IntArg(a, "lines", 60), 1, DevLog.MaxLines);
                        return OnMain(() =>
                        {
                            var ls = DevLog.Lines; int from = Mathf.Max(0, ls.Count - n);
                            return ToolResult.Text(string.Join("\n", ls.GetRange(from, ls.Count - from).ToArray()));
                        });
                    }
                case "command":
                    {
                        string c = a.Str("command", "").Trim();
                        if (c.Length == 0) return ToolResult.Text("empty command", true);
                        return Run(c);
                    }
            }
            return ToolResult.Text("unknown tool " + name, true);
        }

        // run a bridge command on the main thread; returns its output plus any log lines it wrote
        ToolResult Run(string line)
        {
            return OnMain(() =>
            {
                var ls = DevLog.Lines; int before = ls.Count; string last = before > 0 ? ls[before - 1] : null;
                string res = Remote.RunCommand(line, core);
                int start = 0;
                if (last != null) { start = ls.Count; for (int i = ls.Count - 1; i >= 0; i--) if (ReferenceEquals(ls[i], last)) { start = i + 1; break; } }
                var extra = new StringBuilder();
                for (int i = start; i < ls.Count; i++) extra.Append(ls[i]).Append('\n');
                string text = (res ?? "") + (extra.Length > 0 ? (res != null && res.Length > 0 ? "\n--- log ---\n" : "") + extra.ToString() : "");
                if (text.Trim().Length == 0) text = "done (no output)";
                bool err = res != null && (res == "not found" || res.StartsWith("usage") || res.StartsWith("unknown"));
                return ToolResult.Text(text, err);
            });
        }

        // ---------------------------------------------------------------- screenshot
        ToolResult Screenshot(int maxW)
        {
            string tmp = Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "mcp_shot_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
            var r = OnMain(() => { Application.CaptureScreenshot(tmp); return null; });
            if (r != null) return r;
            // the file is written at the end of the frame
            long lastLen = -1; int stable = 0;
            for (int i = 0; i < 200 && stable < 2; i++)
            {
                Thread.Sleep(50);
                try { var fi = new FileInfo(tmp); if (fi.Exists && fi.Length > 0) { if (fi.Length == lastLen) stable++; else { lastLen = fi.Length; stable = 0; } } } catch { }
            }
            if (lastLen <= 0) return ToolResult.Text("the screenshot was not written (is the game window rendering?)", true);
            byte[] png;
            try { png = File.ReadAllBytes(tmp); } catch (Exception e) { return ToolResult.Text("could not read the screenshot: " + e.Message, true); }
            finally { try { File.Delete(tmp); } catch { } }
            if (maxW > 0)
            {
                // decode / scale / encode on this worker thread: the game's main thread is not touched
                string note; byte[] small = null;
                try { small = Png.Downscale(png, maxW, out note); } catch (Exception e) { note = "not scaled: " + e.Message; }
                if (small != null) png = small;
                var res = new ToolResult(); res.AddImage(png, "image/png"); if (note != null) res.AddText(note); return res;
            }
            var full = new ToolResult(); full.AddImage(png, "image/png"); return full;
        }

    }

    // in-game check: a real HTTP MCP session against our own endpoint, from a worker thread
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
                    var r1 = Post(port, null, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"insidedev-selftest\",\"version\":\"1\"}}}", out sid);
                    sb.Append("initialize: ").Append(Cut(r1)).Append("\n  session ").Append(sid).Append('\n');
                    string dummy;
                    var r2 = Post(port, sid, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", out dummy);
                    sb.Append("initialized: ").Append(Cut(r2)).Append('\n');
                    var r3 = Post(port, sid, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", out dummy);
                    int n = 0, i = 0; while ((i = r3.IndexOf("\"inputSchema\"", i, StringComparison.Ordinal)) >= 0) { n++; i++; }
                    sb.Append("tools/list: ").Append(n).Append(" tools\n");
                    var r4 = Post(port, sid, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"game_state\",\"arguments\":{}}}", out dummy);
                    sb.Append("tools/call game_state: ").Append(Cut(r4)).Append('\n');
                    var r5 = Post(port, sid, "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"screenshot\",\"arguments\":{\"max_width\":640}}}", out dummy);
                    sb.Append("tools/call screenshot: ").Append(r5.Length).Append(" bytes, image ").Append(r5.Contains("\"type\":\"image\"") ? "yes" : "NO").Append("  ").Append(Cut(System.Text.RegularExpressions.Regex.Replace(r5, "\"data\":\"[^\"]*\"", "\"data\":\"...\""))).Append('\n');
                    var r6 = Post(port, "not-a-session", "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"ping\"}", out dummy);
                    sb.Append("unknown session: ").Append(Cut(r6)).Append('\n');
                    sb.Append("PASS");
                }
                catch (Exception e) { sb.Append("FAIL: ").Append(e.Message); }
                Result = sb.ToString();
            }) { IsBackground = true, Name = "InsideDev MCP selftest" }.Start();
        }

        static string Cut(string s) { s = s.Replace("\r", "").Replace("\n", " | "); return s.Length > 400 ? s.Substring(0, 400) + "..." : s; }

        static string Post(int port, string sid, string body, out string newSid)
        {
            newSid = null;
            using (var c = new TcpClient("127.0.0.1", port))
            using (var s = c.GetStream())
            {
                var b = Encoding.UTF8.GetBytes(body);
                var h = "POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nContent-Type: application/json\r\nAccept: application/json, text/event-stream\r\nMCP-Protocol-Version: 2025-06-18\r\n" +
                        (sid != null ? "Mcp-Session-Id: " + sid + "\r\n" : "") + "Content-Length: " + b.Length + "\r\nConnection: close\r\n\r\n";
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
