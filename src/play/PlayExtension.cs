using System;
using System.Collections.Generic;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // PlayExtension.cs (owner ARCH, frozen; changes go through the orchestrator): InsideDev.Play.dll, the MCP play and
    // world tools (docs/MCP_PLAY_DESIGN.md). Hot-loadable with "ext load" after InsideDev.Mcp.dll. Start calls every
    // module's static Register() in a fixed order; modules add tools, resources, prompts, instruction paragraphs and
    // per-frame ticks to PlayRegistry. The extension is one IMcpToolProvider registered with McpRegistry.
    // Bridge / console: play [status|tools|restore], rec start|stop|status (Recorder.cs), vsync off|on|status (Stepper.cs)
    public sealed class PlayExtension : IInsideDevExtension, IMcpToolProvider, IDisposable
    {
        public string Name { get { return "Play (MCP play/world tools: " + PlayRegistry.ToolCount + ", problems: " + PlayRegistry.Problems.Count + ")"; } }
        public string ProviderName { get { return "play"; } }

        static readonly KeyValuePair<string, Action>[] Modules =
        {
            M("AgentCore", AgentCore.Register), M("Stepper", Stepper.Register), M("ChangeLog", ChangeLog.Register),
            M("Perception", Perception.Register), M("Observer", Observer.Register), M("Act", Act.Register),
            M("Navigator", Navigator.Register), M("Interact", Interact.Register), M("Waits", Waits.Register),
            M("Restorer", Restorer.Register), M("PlayLevels", PlayLevels.Register), M("Menus", Menus.Register),
            M("Saves", Saves.Register), M("GuideBook", GuideBook.Register), M("Notes", Notes.Register),
            M("NavGeometry", NavGeometry.Register), M("WorldCamera", WorldCamera.Register), M("WorldLogic", WorldLogic.Register),
            M("Hazards", Hazards.Register), M("AudioRender", AudioRender.Register), M("Reflect", Reflect.Register),
            M("StateDiff", StateDiff.Register), M("ActTrack", ActTrack.Register),
        };

        static KeyValuePair<string, Action> M(string name, Action register) { return new KeyValuePair<string, Action>(name, register); }

        public void Start(DevCore core)
        {
            RegisterAll();
            McpShot.MarkSource = Observer.ScreenMarks;
            McpRegistry.Add(this);
        }

        // also used by offline tests (qa/mcp/ARCH) to list every tool without a game
        public const string PlayIntro =
            "You play INSIDE (Playdead 2016), a 2.5D puzzle platformer, through these tools. Loop: observe, then act / move_to / interact / wait_until, then read outcome, changes and observe in the result. " +
            "The game is frozen between calls (step mode) and runs only inside those calls; 60 logic steps = 1 s. Ids from observe (o12 objects, h3 hazards, b1 bodies) are valid until the next load; after a load observe again. " +
            "Positions are metres: dx > 0 is right, dy > 0 is up. Never use screen coordinates. Dying is normal: wait_until {alive:true} or checkpoint reload. " +
            "If stuck.calls_without_progress grows, change the approach. Call restore when you stop.";

        public const string DevIntro =
            "World tools take op: read ops (status, list, state, near ...) are safe. Loads and teleports need no confirm and make no save backup; only when the user set EditorState play.save_guard = 1 do loads that write the Steam Cloud save (chapter mode, load_chapter, reload chapter / newgame) need confirm:true. " +
            "Changes report a verdict (confirmed, reverted_by_game, not_applied, unreadable); call restore to undo MCP changes.";

        public static void RegisterAll()
        {
            PlayRegistry.Reset();
            PlayRegistry.Instructions(McpProfiles.Play, 0, PlayIntro);
            PlayRegistry.Instructions(McpProfiles.Dev, 0, DevIntro);
            foreach (var m in Modules)
            {
                try { m.Value(); }
                catch (Exception e) { PlayRegistry.Problems.Add(m.Key + ": " + e.GetType().Name + ": " + e.Message); }
            }
        }

        public void Tick(DevCore core) { PlayRegistry.RunTicks(); }

        public string Command(List<string> a, DevCore core)
        {
            if (a.Count > 0 && a[0].ToLowerInvariant() == "rec") return Recorder.Command(a);
            if (a.Count > 0 && a[0].ToLowerInvariant() == "vsync")
            {
                string v = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
                if (v == "off") return Stepper.Host.SetVsyncOff(true);
                if (v == "on") return Stepper.Host.SetVsyncOff(false);
                var s = Stepper.State();
                return "vsync " + s["vsync"] + (Stepper.Host.VsyncOff ? " (held off)" : "") + ", fps " + s["fps"] + ", longest frame " + s["frame_ms_max"] + " ms   (vsync off | on)";
            }
            if (a.Count == 0 || a[0].ToLowerInvariant() != "play") return null;
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            if (sub == "tools")
            {
                var s = new System.Text.StringBuilder();
                foreach (var t in PlayRegistry.Tools) s.Append(t.def.name).Append("  [").Append(t.def.profiles).Append("] ").Append(t.def.owner).Append('\n');
                return s.ToString();
            }
            if (sub == "restore")
            {
                var report = new List<string>();
                Restore(report);
                return report.Count == 0 ? "nothing to restore" : string.Join("\n", report.ToArray());
            }
            var st = Name + "\n  ticks: " + PlayRegistry.TickCount;
            foreach (var p in PlayRegistry.Problems) st += "\n  problem: " + p;
            return st;
        }

        public void Dispose()
        {
            try { var r = new List<string>(); Restore(r); } catch { }
            try { McpRegistry.Remove(this); } catch { }
        }

        // ---------------------------------------------------------------- IMcpToolProvider
        public List<ToolDef> Tools()
        {
            var l = new List<ToolDef>();
            foreach (var t in PlayRegistry.Tools) l.Add(t.def);
            return l;
        }

        public ToolResult Call(string tool, Json.Obj args, CallContext ctx)
        {
            var t = PlayRegistry.Find(tool);
            if (t == null) return ToolResult.Fail(McpErr.NotFound, "no play tool " + tool);
            try { return t.handler(args ?? new Json.Obj(), ctx) ?? ToolResult.Fail(McpErr.Internal, tool + " returned nothing"); }
            catch (McpException e) { return ToolResult.Fail(e); }
            catch (NotImplementedException e) { return ToolResult.Fail(McpErr.NotBuilt, tool + ": " + e.Message, "owner " + t.def.owner + " has not implemented it"); }
            catch (Exception e) { return ToolResult.Fail(McpErr.Internal, tool + ": " + e.GetType().Name + ": " + e.Message); }
        }

        public List<ResourceDef> Resources() { return new List<ResourceDef>(PlayRegistry.ResourceDefs); }
        public List<ResourceTemplateDef> ResourceTemplates() { return new List<ResourceTemplateDef>(PlayRegistry.TemplateDefs); }

        public ResourceContents ReadResource(string uri, CallContext ctx)
        {
            foreach (var r in PlayRegistry.Readers)
            {
                var c = r(uri, ctx);
                if (c != null) return c;
            }
            return null;
        }

        public List<PromptDef> Prompts()
        {
            var l = new List<PromptDef>();
            foreach (var p in PlayRegistry.PromptList) l.Add(p.Key);
            return l;
        }

        public PromptResult GetPrompt(string name, Json.Obj args, CallContext ctx)
        {
            foreach (var p in PlayRegistry.PromptList) if (p.Key.name == name) return p.Value(args ?? new Json.Obj(), ctx);
            return null;
        }

        public string Instructions(string profile) { return PlayRegistry.InstructionsFor(profile); }

        public void Restore(List<string> report)
        {
            var r = Restorer.RunAll(null, false);
            var reset = r != null ? r.Arr("reset") : null;
            if (reset != null) foreach (var x in reset) { var o = x as Json.Obj; report.Add(o != null ? o.Str("key", "?") + ": " + o.Str("result", "") : Convert.ToString(x)); }
        }
    }

    // What the modules register into (main thread, from Register). Reset on every RegisterAll.
    public static class PlayRegistry
    {
        public sealed class Tool { public ToolDef def; public PlayHandler handler; }
        sealed class TickEntry { public string name; public Action fn; public bool failed; }
        sealed class Para { public McpProfiles profiles; public int order; public string text; }

        public static readonly List<Tool> Tools = new List<Tool>();
        public static readonly List<ResourceDef> ResourceDefs = new List<ResourceDef>();
        public static readonly List<ResourceTemplateDef> TemplateDefs = new List<ResourceTemplateDef>();
        public static readonly List<Func<string, CallContext, ResourceContents>> Readers = new List<Func<string, CallContext, ResourceContents>>();
        public static readonly List<KeyValuePair<PromptDef, Func<Json.Obj, CallContext, PromptResult>>> PromptList = new List<KeyValuePair<PromptDef, Func<Json.Obj, CallContext, PromptResult>>>();
        public static readonly List<string> Problems = new List<string>();
        static readonly List<TickEntry> ticks = new List<TickEntry>();
        static readonly List<Para> paras = new List<Para>();

        public static int ToolCount { get { return Tools.Count; } }
        public static int TickCount { get { return ticks.Count; } }

        public static void Reset()
        {
            Tools.Clear(); ResourceDefs.Clear(); TemplateDefs.Clear(); Readers.Clear(); PromptList.Clear(); Problems.Clear(); ticks.Clear(); paras.Clear();
        }

        // one tool; the name must be unique (docs/MCP_PLAY_DESIGN.md section 3 owns the list)
        public static void Add(ToolDef def, PlayHandler handler)
        {
            if (Find(def.name) != null) { Problems.Add("duplicate tool " + def.name + " (" + def.owner + ")"); return; }
            Tools.Add(new Tool { def = def, handler = handler });
        }

        public static Tool Find(string name)
        {
            foreach (var t in Tools) if (t.def.name == name) return t;
            return null;
        }

        public static void Resource(ResourceDef def) { ResourceDefs.Add(def); }
        public static void Template(ResourceTemplateDef def) { TemplateDefs.Add(def); }
        // reader for resources/read: return null when the uri is not yours
        public static void Reader(Func<string, CallContext, ResourceContents> read) { Readers.Add(read); }
        public static void Prompt(PromptDef def, Func<Json.Obj, CallContext, PromptResult> get) { PromptList.Add(new KeyValuePair<PromptDef, Func<Json.Obj, CallContext, PromptResult>>(def, get)); }

        // a paragraph of InitializeResult.instructions; lower order first
        public static void Instructions(McpProfiles profiles, int order, string text) { paras.Add(new Para { profiles = profiles, order = order, text = text }); }

        public static string InstructionsFor(string profile)
        {
            var l = new List<Para>();
            foreach (var p in paras) if (McpProfile.Includes(profile, p.profiles)) l.Add(p);
            if (l.Count == 0) return null;
            l.Sort((a, b) => a.order.CompareTo(b.order));
            var parts = new List<string>();
            foreach (var p in l) parts.Add(p.text);
            return string.Join(" ", parts.ToArray());
        }

        // main thread, every frame from PlayExtension.Tick (DevRoot.Update, after the frame's FixedUpdates). A tick that
        // throws is reported once in DevLog and keeps running.
        public static void OnTick(string name, Action fn) { ticks.Add(new TickEntry { name = name, fn = fn }); }

        // every tick shows in the bridge command perf as play.<name> (part of Extensions); off when the core's Perf is not
        // there (offline tests)
        public static bool timed = true;

        static void Timed(TickEntry t)
        {
            try { Perf.RunNested("play." + t.name, t.fn); }
            catch (Exception) { timed = false; t.fn(); }
        }

        public static void RunTicks()
        {
            for (int i = 0; i < ticks.Count; i++)
            {
                var t = ticks[i];
                try { if (timed) Timed(t); else t.fn(); }
                catch (Exception e)
                {
                    if (t.failed) continue;
                    t.failed = true;
                    DevLog.Error("[play] tick " + t.name, e);
                }
            }
        }
    }
}
