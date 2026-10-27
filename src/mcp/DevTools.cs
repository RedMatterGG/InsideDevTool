using System;
using System.Collections.Generic;

namespace InsideDev.McpExt
{
    // DevTools.cs (owner HOST, tool names / parameters / annotations frozen): the host's own provider, the 13 dev tools
    // of v0.5 with honest annotations, output schemas and caps, plus screenshot (play and dev) and jobs. The handlers are
    // in DevToolsCalls.cs (game side) and JobsTool below (Unity-free); text output is structured {ok, text, truncated,
    // total_chars, offset, next_offset}. Resource template inside://shots/{file}: full-size PNGs of screenshots.
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
                    description = "Full-size PNG of an earlier screenshot (the uri field of a screenshot result). The newest 200 are kept.", profiles = McpProfiles.Both }
            };
        }

        public ResourceContents ReadResource(string uri, CallContext ctx) { return McpShot.ReadShot(uri); }
        public List<PromptDef> Prompts() { return new List<PromptDef>(); }
        public PromptResult GetPrompt(string name, Json.Obj args, CallContext ctx) { return null; }

        public string Instructions(string profile)
        {
            if (profile == McpProfile.Play)
                return "screenshot is optional (observe is the main view): mode marked draws the observe ids on the picture; mode full keeps only the PNG file (uri inside://shots/...).";
            return "Text tools (game_state, find_objects, inspect, fsm_graph, fsm_find, log, command) return structured {text, truncated, total_chars}; page inspect with offset = next_offset. " +
                   "isError is set from the game's own failure text (not found, usage, busy, ERROR in ...). jobs lists multi-frame work: op await waits for one, op cancel stops it.";
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

        static Json.Obj TextOut()
        {
            return Schema.Obj().Str("text", "Command output (capped; see truncated).").Bool("truncated", "Output was cut at max_chars.")
                .Int("total_chars", "Length before cutting.", 0, int.MaxValue).Int("offset", "First character returned.", 0, int.MaxValue).BuildOutput();
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
                description = "Capture the game screen. preview = inline PNG (default 960 px wide, about 700 image tokens); marked = preview with observe ids drawn on objects; full or path = PNG file only. The JSON text has ok, mode, width, height, src_width, src_height, path, black (an almost black frame), frame and marks [{id, label, x, y}] (pixels in the image). No structuredContent, so clients that pass only structured results still get the image.",
                inputSchema = Schema.Obj().Enum("mode", "preview, marked, full or path.", "preview", "preview", "marked", "full", "path")
                    .Int("max_width", "Scale down to at most this width (preview / marked). 0 = no scaling.", 960, 0, 4096)
                    .Bool("clean", "Hide InsideDev panels, HUD and overlays for the shot.", true).Build() }.ReadOnly());
            l.Add(T("find_objects", "Find objects",
                "Smart search over loaded objects by name, ranked by match and what the object does. Returns up to limit rows.",
                Schema.Obj().Str("query", "Words to match against object names.")
                    .Enum("category", "Optional filter.", null, "animation", "sounds", "scripts", "triggers", "fsm", "characters")
                    .Int("limit", "Rows.", 25, 1, 100).Required("query").Build()).ReadOnly());
            l.Add(T("inspect", "Inspect object",
                "Describe an object: path, active state, components and their fields, references, FSM dump. Output is capped at max_chars; page with offset.",
                Schema.Obj().Str("object", "Object id (o12), hierarchy path, #instanceID or unique name.")
                    .Bool("deep", "Expand nested serialized data.", false)
                    .Int("max_chars", "Cap on returned text.", 20000, 1000, 200000).Int("offset", "Start of the page (characters).", 0, 0, 100000000)
                    .Required("object").Build()).ReadOnly());
            l.Add(T("select", "Select in editor",
                "Select an object in the InsideDev editor (Inspector, Explorer and graph follow the selection). Editor-only, no game change.",
                Schema.Obj().Str("object", "Object id, path, #instanceID or unique name.").Required("object").Build()).Idempotent());
            l.Add(T("set_active", "Switch object on/off",
                "Switch a GameObject on or off (recorded in History, undo with command 'undo'). Re-reads the state after 2 frames.",
                Schema.Obj().Str("object", "Object id, path, #instanceID or unique name.").Bool("active", "true = on, false = off.")
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
                    .Str("fsm", "FSM name when the object has several.").Required("object", "action", "name").Build()).Destructive());
            l.Add(T("signal", "Fire a signal",
                "Fire one of an object's Playdead signal outputs (raises every connection of that output, like the game does). Cannot be undone.",
                Schema.Obj().Str("object", "Object with the signal output.").Str("output", "Signal output name (see inspect / fsm_find).")
                    .Required("object", "output").Build()).Destructive());
            l.Add(T("show_panel", "Show an editor panel",
                "Open the InsideDev editor on a panel, optionally docking and sizing it. Panels: spawns, levels, triggers, hidden, world, audio, inspector, cheats, console, diag, settings, objects, explorer, graph, events, sounds, atimeline, wwiseapi, history, audiodb, logic, modified, fsmgraph, fsmfind; none closes the editor.",
                Schema.Obj().Str("panel", "Panel id, or none.").Enum("dock", "Dock side.", null, "left", "right", "bottom")
                    .Num("size", "Dock size as a fraction of the screen (left/right 0.12-0.6, bottom 0.1-0.7).", 0.1, 0.7).Required("panel").Build()).Idempotent());
            l.Add(T("log", "InsideDev log", "The last lines of InsideDev's log (command output, errors, events).",
                Schema.Obj().Int("lines", "How many lines.", 60, 1, 400).Build()).ReadOnly());
            l.Add(T("command", "Run InsideDev command",
                "Run any InsideDev bridge/console command and return its output plus the log lines it wrote; 'help' lists them. OS-input commands (keyhold, focustest) are refused: use act. isError is set from the command's own failure text.",
                Schema.Obj().Str("command", "The command line, e.g. 'near 10' or 'get \"Housing (4)\" Transform localPosition'.")
                    .Int("max_chars", "Cap on returned text.", 20000, 1000, 200000).Required("command").Build()).Destructive());
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
