using System;
using System.Collections.Generic;

namespace InsideDev.McpExt
{
    // DevTools.cs (owner HOST, tool names / parameters / annotations frozen; parameters are only ever added): the host's
    // own provider, 14 tools: the 12 dev text tools (game_state, find_objects, inspect, select, set_active, fsm_graph,
    // fsm_find, fsm, signal, show_panel, log, command) with honest annotations, output schemas and caps, plus screenshot
    // (play and dev) and jobs. The handlers are in DevToolsCalls.cs (game side) and JobsTool below (Unity-free); text
    // output is structured {ok, text, truncated, total_chars, offset, next_offset}. Resource template
    // inside://shots/{file}: the image files of screenshots.
    public sealed class DevToolsProvider : IMcpToolProvider
    {
        public const string Name = "dev";
        readonly Func<string, Json.Obj, CallContext, ToolResult> call;
        List<ToolDef> defs;
        // main thread: undo what this provider changed for MCP (editor panels opened by show_panel); set by McpExtension
        internal Action<List<string>> restoreHook;

        public DevToolsProvider(Func<string, Json.Obj, CallContext, ToolResult> call) { this.call = call; }

        public string ProviderName { get { return Name; } }
        public List<ToolDef> Tools() { if (defs == null) defs = Defs(); return defs; }

        public ToolResult Call(string tool, Json.Obj args, CallContext ctx)
        {
            if (tool == "jobs") return JobsTool(args ?? new Json.Obj(), ctx);
            if (call == null) return ToolResult.Fail(McpErr.NotBuilt, tool + " has no handler");
            return call(tool, args ?? new Json.Obj(), ctx);
        }

        public List<ResourceDef> Resources() { return new List<ResourceDef>(); }

        public List<ResourceTemplateDef> ResourceTemplates()
        {
            return new List<ResourceTemplateDef>
            {
                new ResourceTemplateDef { uriTemplate = McpShot.UriPrefix + "{file}", name = "screenshot", title = "Screenshot file", mimeType = "image/png",
                    description = "Image file of an earlier screenshot (the uri field of a screenshot result): the full-size PNG of mode full / path, the returned image of preview / marked. The newest 200 are kept.", profiles = McpProfiles.Both }
            };
        }

        public ResourceContents ReadResource(string uri, CallContext ctx) { return McpShot.ReadShot(uri); }
        public List<PromptDef> Prompts() { return new List<PromptDef>(); }
        public PromptResult GetPrompt(string name, Json.Obj args, CallContext ctx) { return null; }

        public string Instructions(string profile)
        {
            if (profile == McpProfile.Play)
                return "screenshot is optional (observe is the main view): mode marked draws the observe ids on the picture; mode full keeps only the PNG file (uri inside://shots/...); format jpeg makes the picture several times smaller.";
            return "Text tools (game_state, find_objects, inspect, fsm_graph, fsm_find, log, command) return structured {text, truncated, total_chars}; page inspect with offset = next_offset. " +
                   "isError is set from the game's own failure text (not found, usage, busy, ERROR in ...). command also takes commands:[...] and wait:'<status command>'. " +
                   "A plain object name is matched loosely: set_active, fsm and signal report matches / candidates when several objects fit, and refuse with strict:true. " +
                   "jobs lists multi-frame work: op await waits for one, op cancel stops it.";
        }

        public void Restore(List<string> report)
        {
            var h = restoreHook;
            if (h != null) h(report);
        }

        // the jobs tool (worker thread, never takes a lane)
        internal static ToolResult JobsTool(Json.Obj a, CallContext ctx)
        {
            string op = HostText.Str(a, "op", "list");
            string id = HostText.Str(a, "id", null);
            double timeoutS = HostText.Num(a, "timeout_s", 10);
            if (op == "list")
            {
                var l = new List<object>();
                foreach (var j in HostJobs.All()) { l.Add(j.Describe()); if (l.Count >= 60) break; }
                var o = new Json.Obj(); o["jobs"] = l;
                return ToolResult.Ok(o);
            }
            if (op != "status" && op != "await" && op != "cancel") return ToolResult.Fail(McpErr.BadArgs, "op: " + op + " is not one of list, status, await, cancel");
            McpJob job = null;
            if (!string.IsNullOrEmpty(id)) job = HostJobs.Find(id);
            else if (op == "cancel") job = HostJobs.Running(McpLanes.Control);
            else return ToolResult.Fail(McpErr.BadArgs, "id: required for op " + op, "jobs {op:'list'} shows the ids");
            if (job == null)
                return ToolResult.Fail(McpErr.NotFound, string.IsNullOrEmpty(id) ? "no job is running" : "no job " + id + " (finished jobs are kept 10 minutes, the last 50)", "jobs {op:'list'} shows the ids");
            var r = new Json.Obj();
            if (op == "status")
            {
                r["job"] = job.Describe();
                if (job.State != McpJobState.Running && job.Result != null) r["result"] = job.Result;
                return ToolResult.Ok(r);
            }
            if (op == "await")
            {
                int ms = (int)Math.Round(timeoutS * 1000);
                if (ctx != null) ms = Math.Max(0, Math.Min(ms, ctx.RemainingMs - 1500));
                var until = DateTime.UtcNow.AddMilliseconds(ms);
                bool fin = job.State != McpJobState.Running;
                while (!fin)
                {
                    int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) break;
                    fin = job.Await(Math.Min(left, 200));
                    if (ctx != null)
                    {
                        if (ctx.Cancelled) break;
                        ctx.Progress(Math.Round(ms / 1000.0 - left / 1000.0, 1), Math.Round(ms / 1000.0, 1), "waiting for " + job.Id + " (" + job.Tool + ")");
                    }
                }
                r["job"] = job.Describe();
                r["finished"] = fin;
                if (fin && job.Result != null) r["result"] = job.Result;
                return ToolResult.Ok(r);
            }
            bool wasRunning = job.State == McpJobState.Running;
            if (wasRunning) job.Cancel("jobs cancel");
            bool done = !wasRunning || job.Await(2000);
            r["job"] = job.Describe();
            r["finished"] = done;
            if (job.Id == "mcam") r["hint"] = "a MultiCam capture is stopped with command 'mcam stop'";
            else if (!wasRunning) r["hint"] = "the job had already ended";
            return ToolResult.Ok(r);
        }

        static SchemaObj TextFields()
        {
            return Schema.Obj().Str("text", "Command output (capped; see truncated).").Bool("truncated", "Output was cut at max_chars.")
                .Int("total_chars", "Length before cutting.", 0, int.MaxValue).Int("offset", "First character returned.", 0, int.MaxValue)
                .Int("next_offset", "Offset of the next page, when there is one.", 0, int.MaxValue);
        }

        static Json.Obj TextOut() { return TextFields().BuildOutput(); }

        // the command tool: where text comes from, the results of commands:[...], the outcome of wait
        static Json.Obj CommandOut()
        {
            var row = Schema.Obj().Str("command", "The line.").Bool("ok", "It did not fail.").Str("code", "Error code, or null.").Str("text", "Its output.");
            return TextFields().Str("command", "The line that ran.")
                .Enum("from", "What text holds: reply = what the command returned, log = the log lines it wrote, both (then reply and log are also given apart), none.", null, "reply", "log", "both", "none")
                .Str("reply", "What the command returned (only when from is both).").Str("log", "The log lines it wrote (only when from is both).")
                .Arr("results", "commands: one entry per line, in order.", Schema.OutOf(row), 0, 200)
                .Int("count", "commands: lines given.", 0, 200).Int("failed", "commands: lines that failed.", 0, 200)
                .Bool("wait_done", "wait: the status command reported one of the until texts.").Str("wait_hit", "wait: the until text that was found.")
                .Num("waited_s", "wait: seconds waited.", 0, 60).Str("wait_reply", "wait: the last reply of the status command.").BuildOutput();
        }

        static ToolDef T(string name, string title, string description, Json.Obj input)
        {
            return new ToolDef { name = name, title = title, description = description, inputSchema = input, outputSchema = TextOut(), profiles = McpProfiles.Dev, owner = "HOST" };
        }

        public static List<ToolDef> Defs()
        {
            var l = new List<ToolDef>();
            l.Add(T("game_state", "Game state",
                "Text snapshot of the running game: InsideDev version, frame, boy position, savepoint, music position, active scenes, nearby triggers. For structured state use observe.",
                Schema.Obj().Build()).ReadOnly());
            l.Add(new ToolDef { name = "screenshot", title = "Screenshot", profiles = McpProfiles.Both, owner = "HOST",
                description = "Capture the game screen. preview = inline image (default 960 px wide, about 700 image tokens); marked = preview with observe ids drawn on objects; full or path = full-size PNG file only. format jpeg returns a JPEG several times smaller than the PNG. The JSON text has ok, mode, format, width, height, src_width, src_height, path and uri (the image as a file), bytes, black (an almost black frame), frame, ms and marks [{id, label, x, y}] (pixels in the image). No structuredContent, so clients that pass only structured results still get the image.",
                inputSchema = Schema.Obj().Enum("mode", "preview, marked, full or path.", "preview", "preview", "marked", "full", "path")
                    .Int("max_width", "Scale down to at most this width (preview / marked). 0 = no scaling.", 960, 0, 4096)
                    .Bool("clean", "Hide InsideDev panels, HUD and overlays for the shot.", true)
                    .Bool("fast", "full / path: return once the file is written, without the black check (for many frames use time {op:\"record\"}).", false)
                    .Enum("format", "preview / marked: png (lossless) or jpeg (much smaller).", "png", "png", "jpeg")
                    .Int("quality", "jpeg quality.", 80, 1, 100).Build() }.ReadOnly());
            l.Add(T("find_objects", "Find objects",
                "Smart search over loaded objects by name, ranked by match and what the object does. Returns up to limit rows.",
                Schema.Obj().Str("query", "Words to match against object names.")
                    .Enum("category", "Optional filter.", null, "animation", "sounds", "scripts", "triggers", "fsm", "characters")
                    .Int("limit", "Rows.", 25, 1, 100).Required("query").Build()).ReadOnly());
            l.Add(T("inspect", "Inspect object",
                "Describe an object: path, active state, components and their fields, references, FSM dump. Output is capped at max_chars; page with offset.",
                Schema.Obj().Str("object", "Object id from observe (o12), hierarchy path (A/B/C), #instanceID, or a name: the exact name first, else text inside the name; of several matches the one nearest to the boy is used.")
                    .Bool("deep", "Expand nested serialized data.", false)
                    .Int("max_chars", "Cap on returned text.", 20000, 1000, 200000).Int("offset", "Start of the page (characters).", 0, 0, 100000000)
                    .Required("object").Build()).ReadOnly());
            l.Add(T("select", "Select in editor",
                "Select an object in the InsideDev editor (Inspector, Explorer and graph follow the selection). Editor-only, no game change.",
                Schema.Obj().Str("object", "Object id, path, #instanceID or name (see inspect).").Required("object").Build()).Idempotent());
            l.Add(T("set_active", "Switch object on/off",
                "Switch a GameObject on or off (recorded in History, undo with command 'undo'). Re-reads the state after 2 frames. A name that fits several objects acts on the nearest one and says so (matches, candidates).",
                Schema.Obj().Str("object", "Object id, path, #instanceID or name (see inspect).").Bool("active", "true = on, false = off.")
                    .Bool("strict", "Refuse (error ambiguous) when a plain name fits more than one object.", false)
                    .Required("object", "active").Build()).Idempotent());
            l.Add(T("fsm_graph", "PlayMaker FSM",
                "Dump a PlayMaker state machine: states, exits with events, current state. open_panel also shows it in the FSM Graph panel.",
                Schema.Obj().Str("object", "Object that has the FSM.").Str("fsm", "FSM name when the object has several.")
                    .Bool("open_panel", "Also select it and open the FSM Graph panel.", false).Required("object").Build()).ReadOnly());
            l.Add(T("fsm_find", "Find in FSMs",
                "Search every loaded PlayMaker FSM (including switched-off ones) for events, signal inputs, states, action settings and signal connections.",
                Schema.Obj().Str("text", "Text to search for.").Int("limit", "Hits.", 30, 1, 200).Required("text").Build()).ReadOnly());
            l.Add(T("fsm", "Drive an FSM",
                "Send an event to a PlayMaker FSM, force it into a state, or read/set one of its variables. event and state cannot be undone.",
                Schema.Obj().Str("object", "Object that has the FSM.").Enum("action", "event, state or var.", null, "event", "state", "var")
                    .Str("name", "Event, state or variable name.").Str("value", "New variable value (action var; omit to read).")
                    .Str("fsm", "FSM name when the object has several.")
                    .Bool("strict", "Refuse (error ambiguous) when a plain object name fits more than one object.", false).Required("object", "action", "name").Build()).Destructive());
            l.Add(T("signal", "Fire a signal",
                "Fire one of an object's Playdead signal outputs (raises every connection of that output, like the game does). Cannot be undone.",
                Schema.Obj().Str("object", "Object with the signal output.").Str("output", "Signal output name (see inspect / fsm_find).")
                    .Bool("strict", "Refuse (error ambiguous) when a plain object name fits more than one object.", false)
                    .Required("object", "output").Build()).Destructive());
            l.Add(T("show_panel", "Show an editor panel",
                "Open the InsideDev editor on a panel, optionally docking and sizing it. Panels: spawns, levels, triggers, hidden, world, audio, inspector, cheats, console, diag, settings, objects, explorer, graph, events, sounds, atimeline, wwiseapi, history, audiodb, logic, modified, fsmgraph, fsmfind; none closes the editor.",
                Schema.Obj().Str("panel", "Panel id, or none.").Enum("dock", "Dock side.", null, "left", "right", "bottom")
                    .Num("size", "Dock size as a fraction of the screen (left/right 0.12-0.6, bottom 0.1-0.7).", 0.1, 0.7).Required("panel").Build()).Idempotent());
            l.Add(T("log", "InsideDev log", "The last lines of InsideDev's log (command output, errors, events).",
                Schema.Obj().Int("lines", "How many lines.", 60, 1, 400).Build()).ReadOnly());
            var command = T("command", "Run InsideDev command",
                "Run any InsideDev bridge/console command and return its output plus the log lines it wrote; 'help' lists them. OS-input commands (keyhold, focustest), commands that change this server (mcp off / port / token / tools / hosts, remote off), lines that name the server's settings (mcp.token, mcp.tools, mcp.hosts, mcp.enabled, mcp.port) and lines that drive secret / achievement objects are refused; the same list applies to POST /cmd and /cmds, run_scenario steps, det cues and mcam spec lines (quit is allowed here and through POST /cmd only). This server trusts programs and pages of this machine (a page served from http://localhost on any port may call it); set mcp.token in the in-game console to require a token. isError is set from the command's own failure text. commands:[...] runs several lines in order in one call (results has one entry per line). wait:'<status command>' keeps the call open after the command until the status command's reply contains one of the until texts, e.g. command 'pano shot.png', wait 'pano status'.",
                Schema.Obj().Str("command", "The command line, e.g. 'near 10' or 'get \"Housing (4)\" Transform localPosition'.")
                    .Int("max_chars", "Cap on returned text.", 20000, 1000, 200000)
                    .Arr("commands", "Several command lines instead of command; they run in order.", Schema.Str(), 1, 200)
                    .Bool("stop_on_error", "commands: stop at the first line that fails (the rest get code skipped).", false)
                    .Bool("dry_run", "Do not run anything: only report which lines would be refused.", false)
                    .Str("wait", "A status command to ask after command ran, until its reply contains one of the until texts.")
                    .Str("until", "Texts that end the wait, separated by | (case does not matter).", HostText.DefaultUntil)
                    .Num("wait_timeout_s", "wait: give up after this many seconds (wait_done is then false).", 45, 0, 50).Build()).Destructive();
            command.outputSchema = CommandOut();
            l.Add(command);
            var jobOut = Schema.Obj()
                .Arr("jobs", "Running jobs first, then recent finished ones.", Schema.OutOf(JobSchema()), 0, 60)
                .OutObj("job", "The job of op status / await / cancel.", JobSchema())
                .Any("result", "Result of a finished job (op await / status).").Bool("finished", "op await: the job ended within timeout_s.").BuildOutput();
            l.Add(new ToolDef { name = "jobs", title = "Jobs", profiles = McpProfiles.Dev, owner = "HOST", outputSchema = jobOut,
                description = "List, inspect, await or cancel multi-frame jobs (loads, actions, waits). Cancelling releases inputs and re-freezes the game.",
                inputSchema = Schema.Obj().Enum("op", "list, status, await or cancel.", "list", "list", "status", "await", "cancel")
                    .Str("id", "Job id (j12) for status / await / cancel.").Num("timeout_s", "op await: how long to wait.", 10, 0, 50).Build() });
            return l;
        }

        public static SchemaObj JobSchema()
        {
            return Schema.Obj().Str("id", "Job id.").Str("tool", "Tool that started it.").Str("lane", "Lane.")
                .Enum("state", "State.", null, McpJobState.Running, McpJobState.Done, McpJobState.Failed, McpJobState.Cancelled)
                .Num("elapsed_s", "Seconds since start.", 0, 1e9).Num("progress", "Progress value.", -1e12, 1e12).Num("total", "Progress total.", -1e12, 1e12)
                .Str("message", "Last progress message.").Str("error", "Error text when failed.");
        }
    }
}
