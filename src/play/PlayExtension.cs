using System;
using System.Collections.Generic;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // PlayExtension.cs (owner ARCH, frozen; changes go through the orchestrator): InsideDev.Play.dll, the MCP play and
    // world tools (docs/MCP_PLAY_DESIGN.md). Hot-loadable with "ext load" after InsideDev.Mcp.dll. Start calls every
    // module's static Register() in a fixed order; modules add tools, resources, prompts, instruction paragraphs and
    // per-frame ticks to PlayRegistry. The extension is one IMcpToolProvider registered with McpRegistry.
    // Bridge / console: play [status [json] | tools | restore [list [json]]], rec start|stop|status (Recorder.cs),
    // vsync off|on|auto|status [json] (Stepper.cs), pano <file> ... | status (Pano.cs)
    // Idle cost: Tick only walks the tick list. A tick runs while its module has work (PlayRegistry.OnTick with TickWhen,
    // PlayTick.Active); every tool call and ctl / play console command marks the module as used (PlayRegistry.Used), and
    // the first one after a quiet stretch runs the wake actions (OnWake) on the main thread before the call goes on.
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
            M("StateDiff", StateDiff.Register), M("ActTrack", ActTrack.Register), M("CamPath", CamPath.Register),
        };

        static KeyValuePair<string, Action> M(string name, Action register) { return new KeyValuePair<string, Action>(name, register); }

        public void Start(DevCore core)
        {
            PlayRegistry.MainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            RegisterAll();
            McpShot.MarkSource = Observer.ScreenMarks;
            McpRegistry.Add(this);
            try { KeepAlive(); } catch (Exception e) { PlayRegistry.Problems.Add("keep-alive: " + e.Message); }
            try { DescribeKeys(); } catch (Exception e) { PlayRegistry.Problems.Add("config keys: " + e.Message); }
            try { Doctor.Register("play", DoctorCheck); } catch (Exception e) { PlayRegistry.Problems.Add("doctor: " + e.Message); }
        }

        // the MCP server's automatic restores (idle, last session closed) wait while these record or write a file
        static readonly string[] KeepAliveNames = { "grec", "pano", "rec", "input_track" };

        static void KeepAlive()
        {
            McpKeepAlive.Register("grec", () => GameRec.Running ? "game video recording (grec " + GameRec.Phase + ")" : null);
            McpKeepAlive.Register("pano", () => Pano.Idle ? null : "a panorama is being captured or written");
            McpKeepAlive.Register("rec", () => Recorder.On || Recorder.Pending ? "real-time recording (rec)" : null);
            McpKeepAlive.Register("input_track", () => ActTrack.Recording ? "an input track is recording" : null);
        }

        // the editor.cfg keys this half of Play reads (cfg lists them)
        static void DescribeKeys()
        {
            EditorState.Describe("play", "play.vsync_off", "1", "vSync: 1 = off while an agent's tool calls arrive (30 s after the last one) and while the game clock is held (first action or timed ctl command until restore), else the game's own (default); 0 = always the game's own; 2 = off from game start (as before 0.11.0). vsync on / off / auto overrides it until restore.");
            EditorState.Describe("play", "play.save_guard", "0", "1 = loads and edits that write INSIDE.sav back the save up first and need confirm; 0 = no backup, no confirm.");
            EditorState.Describe("play", "play.hold_timeout_s", "0", "Optional safety release (off by default): seconds without a play tool call after which input held by an agent (act hold, a grab grip) is released while the game runs in realtime; 0 = never (default). Input held with ctl by a person is not timed.");
            EditorState.Describe("play", "play.step_pacing", "auto", "Step-mode runs: auto = as fast as the game renders, paced to real time only in music-timed places; always = every run paced to 60 steps per second (watchable, sound keeps up); never = no pacing.");
        }

        // also used by offline tests (qa/mcp/ARCH) to list every tool without a game
        public const string PlayIntro =
            "You play INSIDE (Playdead 2016), a 2.5D puzzle platformer, through these tools. Loop: observe, then act / move_to / interact / wait_until, then read outcome, changes and observe in the result. " +
            "The game is frozen between calls (step mode) and runs only inside those calls; 60 logic steps = 1 s. Ids from observe (o12 objects, h3 hazards, b1 bodies) are valid until the next load; after a load observe again. " +
            "Positions are metres: dx > 0 is right, dy > 0 is up. Never use screen coordinates. Dying is normal: wait_until {any:[{alive:true}]} or checkpoint reload. " +
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
            if (a.Count > 0 && a[0].ToLowerInvariant() == "campath") return CamPath.Command(a);
            if (a.Count > 0 && a[0].ToLowerInvariant() == "grec") return GameRec.Command(a);
            if (a.Count > 0 && a[0].ToLowerInvariant() == "pano") return Pano.Command(a, core);
            if (a.Count > 0 && a[0].ToLowerInvariant() == "vsync")
            {
                PlayRegistry.UsedHere(false);
                bool json = a.Count > 1 && a[a.Count - 1].ToLowerInvariant() == "json";
                string v = a.Count > 1 && !(json && a.Count == 2) ? a[1].ToLowerInvariant() : "status";
                string line = null;
                if (v == "off") line = Stepper.Host.SetVsyncOff(true);
                else if (v == "on") line = Stepper.Host.SetVsyncOff(false);
                else if (v == "auto") line = Stepper.Host.SetVsyncAuto();
                else if (v != "status") return "usage: vsync [off | on | auto | status] [json]";
                var s = Stepper.State();
                if (json)
                    return InsideDev.JsonLine.Obj("ok", true, "vsync", s["vsync"], "rule", Stepper.Host.VsyncRule, "held_off", Stepper.Host.VsyncHeldOff, "override", Stepper.Host.VsyncOverride,
                        "fps", s["fps"], "frame_ms_max", s["frame_ms_max"], "note", line ?? "");
                if (line != null) return line;
                return "vsync " + PlayShared.F(s.Num("vsync")) + (Stepper.Host.VsyncHeldOff ? " (held off)" : "") + ", fps " + PlayShared.F(s.Num("fps")) + ", longest frame " + PlayShared.F(s.Num("frame_ms_max")) +
                       " ms   (vsync off | on | auto; rule: " + Stepper.Host.VsyncRule + ")";
            }
            if (a.Count == 0 || a[0].ToLowerInvariant() != "play") return null;
            PlayRegistry.UsedHere(false);
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            bool wantJson = a.Count > 1 && a[a.Count - 1].ToLowerInvariant() == "json";
            if (sub == "json") sub = "status";
            if (sub == "tools")
            {
                var s = new System.Text.StringBuilder();
                foreach (var t in PlayRegistry.Tools) s.Append(t.def.name).Append("  [").Append(t.def.profiles).Append("] ").Append(t.def.owner).Append('\n');
                return s.ToString();
            }
            if (sub == "restore")
            {
                string what = a.Count > 2 ? a[2].ToLowerInvariant() : "";
                if (what == "list" || what == "dry" || what == "dry_run")
                {
                    var plan = Restorer.Plan(null, false, false);
                    return wantJson ? InsideDev.JsonLine.Write(plan) : Restorer.PlanText(plan);
                }
                if (what != "") return "usage: play restore [list [json]]";
                var report = new List<string>();
                Restore(report);
                return report.Count == 0 ? "nothing to restore" : string.Join("\n", report.ToArray());
            }
            if (sub != "status") return "usage: play [status [json] | tools | restore [list [json]]]";
            if (wantJson) return InsideDev.JsonLine.Write(StatusJson());
            var st = Name + "\n  ticks: " + PlayRegistry.TickCount + " (running now: " + PlayRegistry.RunningText() + ")";
            st += "\n  agent: " + UseText();
            foreach (var d in PlayRegistry.DisabledTicks()) st += "\n  problem: tick " + d + " was switched off after 30 errors in a row (perf enable play." + d + ")";
            foreach (var p in PlayRegistry.Problems) st += "\n  problem: " + p;
            return st;
        }

        static string UseText()
        {
            double idle = PlayRegistry.IdleSeconds;
            if (idle < 0) return "no play tool call or ctl command yet";
            return "last " + (PlayRegistry.LastUseWasTool ? "tool call" : "console command") + " " + PlayShared.F(Math.Round(idle, 1)) + " s ago" + (PlayRegistry.InUse ? "" : " (asleep)");
        }

        // main thread: play status json
        static Json.Obj StatusJson()
        {
            var o = new Json.Obj();
            o["ok"] = true;
            o["build"] = PlayShared.PlayBuild;
            o["tools"] = (double)PlayRegistry.ToolCount;
            o["ticks"] = (double)PlayRegistry.TickCount;
            o["ticks_running"] = PlayShared.L(PlayRegistry.Running());
            o["ticks_disabled"] = PlayShared.L(PlayRegistry.DisabledTicks());
            o["in_use"] = PlayRegistry.InUse;
            o["idle_s"] = Math.Round(PlayRegistry.IdleSeconds, 1);
            o["last_use"] = PlayRegistry.IdleSeconds < 0 ? "none" : PlayRegistry.LastUseWasTool ? "tool" : "console";
            o["clock"] = ClockText();
            o["clock_touched"] = Stepper.Clock.Touched;
            o["input"] = AgentCore.Describe();
            var track = ActTrack.Brief();
            if (track != null) o["track"] = track;
            o["save_guard"] = Saves.Guard;
            o["restore_keys"] = PlayShared.L(Restorer.Keys());
            o["problems"] = PlayShared.L(PlayRegistry.Problems);
            return o;
        }

        static string ClockText()
        {
            if (!Stepper.Clock.Touched) return "untouched (the game's own clock)";
            return CtlCore.Clock(Stepper.Mode == StepMode.Step, Stepper.Frozen, Stepper.Speed) + (Stepper.Running ? ", a run is active" : "");
        }

        // main thread, read-only: the rows of the doctor command for this part
        static List<Doctor.Row> DoctorCheck()
        {
            var rows = new List<Doctor.Row>();
            double idle = PlayRegistry.IdleSeconds;
            rows.Add(new Doctor.Row("play", "agent", Doctor.Info, UseText() + "; " + PlayRegistry.ToolCount + " tools, build " + PlayShared.PlayBuild));
            bool run = Stepper.Running;
            var job = HostJobs.Running(McpLanes.Control);
            rows.Add(new Doctor.Row("play", "clock", Doctor.Ok, ClockText() + (job != null ? "; control lane held by " + job.Tool + " job " + job.Id + " for " + PlayShared.F(Math.Round(job.ElapsedMs / 1000.0, 1)) + " s" : "; control lane free")));
            if (run && job == null)
                rows.Add(new Doctor.Row("play", "run", Doctor.Warn, "a clock run is active but no job holds the control lane (its caller is gone)", "play restore"));
            var held = AgentCore.HeldNow();
            string mode = AgentQueue.ModeName(AgentCore.Mode);
            string input = AgentCore.Installed ? mode + (held != null ? ", held " + JsonOut.Write(held) : ", nothing held") : "human (the agent input core is not installed)";
            bool stale = held != null && !Stepper.Frozen && idle > 30 && job == null;
            rows.Add(new Doctor.Row("play", "input", stale ? Doctor.Warn : Doctor.Ok, input + (stale ? "; no call for " + PlayShared.F(Math.Round(idle)) + " s while the game runs" : ""), stale ? "ctl release, or play restore" : null));
            var track = ActTrack.Brief();
            string tr = track == null ? "idle" : track.ContainsKey("recording") ? "recording, " + PlayShared.F(track.Num("recording")) + " steps so far" : "playing " + track.Str("playing", "?") + " step " + PlayShared.F(track.Num("step")) + "/" + PlayShared.F(track.Num("of"));
            rows.Add(new Doctor.Row("play", "input track", Doctor.Info, tr));
            string cheat = Saves.Cheat;
            string guard = Saves.Guard ? "on (play.save_guard=1)" + (Saves.BackupPath != null ? ", backup " + Saves.BackupPath : ", no backup made yet") : "off (play.save_guard=0: no save backup, no confirm)";
            bool locked = Saves.Locked;
            rows.Add(new Doctor.Row("play", "save guard", cheat != null ? Doctor.Warn : Doctor.Info, guard + (locked ? "; save writes are locked" : "") + (cheat != null ? "; a cheat ran under the lock (" + cheat + ")" : ""),
                cheat != null ? "quit to drop it, or save {op:\"unlock\", confirm:true} to write it" : null));
            foreach (var d in PlayRegistry.DisabledTicks())
                rows.Add(new Doctor.Row("play", "tick " + d, Doctor.Fail, "switched off after 30 errors in a row", "perf enable play." + d + " (the log has the error)"));
            foreach (var p in PlayRegistry.Problems) rows.Add(new Doctor.Row("play", "problem", Doctor.Fail, p));
            var keys = Restorer.Keys();
            rows.Add(new Doctor.Row("play", "restore", Doctor.Info, keys.Count == 0 ? "nothing to restore" : keys.Count + " change(s) held: " + string.Join(", ", keys.ToArray())));
            return rows;
        }

        public void Dispose()
        {
            try { var r = new List<string>(); Restore(r); } catch { }
            try { McpRegistry.Remove(this); } catch { }
            try { Doctor.Unregister("play"); } catch { }
            try { foreach (var n in KeepAliveNames) McpKeepAlive.Unregister(n); } catch { }
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
            Awake(ctx);
            try { return t.handler(args ?? new Json.Obj(), ctx) ?? ToolResult.Fail(McpErr.Internal, tool + " returned nothing"); }
            catch (McpException e) { return ToolResult.Fail(e); }
            catch (NotImplementedException e) { return ToolResult.Fail(McpErr.NotBuilt, tool + ": " + e.Message, "owner " + t.def.owner + " has not implemented it"); }
            catch (Exception e) { return ToolResult.Fail(McpErr.Internal, tool + ": " + e.GetType().Name + ": " + e.Message); }
        }

        // worker thread: mark the module as used; the first call after a quiet stretch lets the parts that slept catch up
        // on the main thread before the handler reads them (a main thread that does not answer is the handler's error)
        static void Awake(CallContext ctx)
        {
            if (!PlayRegistry.Used(true)) return;
            try { ctx.MainDo(PlayRegistry.WakeNow, 3000); } catch (McpException) { }
        }

        public List<ResourceDef> Resources() { return new List<ResourceDef>(PlayRegistry.ResourceDefs); }
        public List<ResourceTemplateDef> ResourceTemplates() { return new List<ResourceTemplateDef>(PlayRegistry.TemplateDefs); }

        public ResourceContents ReadResource(string uri, CallContext ctx)
        {
            PlayRegistry.Used(true);
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
            try { ChangeLog.Idle(); } catch { }
        }
    }

    // What the modules register into (main thread, from Register). Reset on every RegisterAll.
    public static class PlayRegistry
    {
        public sealed class Tool { public ToolDef def; public PlayHandler handler; }
        sealed class Para { public McpProfiles profiles; public int order; public string text; }

        public static readonly List<Tool> Tools = new List<Tool>();
        public static readonly List<ResourceDef> ResourceDefs = new List<ResourceDef>();
        public static readonly List<ResourceTemplateDef> TemplateDefs = new List<ResourceTemplateDef>();
        public static readonly List<Func<string, CallContext, ResourceContents>> Readers = new List<Func<string, CallContext, ResourceContents>>();
        public static readonly List<KeyValuePair<PromptDef, Func<Json.Obj, CallContext, PromptResult>>> PromptList = new List<KeyValuePair<PromptDef, Func<Json.Obj, CallContext, PromptResult>>>();
        public static readonly List<string> Problems = new List<string>();
        static readonly List<PlayTick> ticks = new List<PlayTick>();
        static readonly List<Action> wakes = new List<Action>();
        static readonly List<Para> paras = new List<Para>();

        public static int ToolCount { get { return Tools.Count; } }
        public static int TickCount { get { return ticks.Count; } }

        public static void Reset()
        {
            Tools.Clear(); ResourceDefs.Clear(); TemplateDefs.Clear(); Readers.Clear(); PromptList.Clear(); Problems.Clear(); ticks.Clear(); paras.Clear(); wakes.Clear();
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

        // main thread, from Register: fn runs from PlayExtension.Tick (DevRoot.Update, after the frame's FixedUpdates).
        // Without a TickWhen it runs every frame; a module that knows when it has work says OnDemand and switches the
        // returned PlayTick on and off itself, or InUse (only while Play was used in the last UseWindowS seconds). A tick
        // that throws is logged (at most once in 5 s) and, after 30 errors in a row, switched off unless it is critical.
        public static PlayTick OnTick(string name, Action fn) { return OnTick(name, fn, TickWhen.Always); }

        public static PlayTick OnTick(string name, Action fn, TickWhen when)
        {
            var t = new PlayTick { name = name, perfName = "play." + name, fn = fn, when = when };
            ticks.Add(t);
            return t;
        }

        // any thread: switch an OnDemand tick by its name (modules that kept their PlayTick set Active directly)
        public static bool SetActive(string name, bool on)
        {
            for (int i = 0; i < ticks.Count; i++)
                if (ticks[i].name == name) { ticks[i].Active = on; return true; }
            return false;
        }

        // main thread, from Register: runs on the main thread when Play is used again after a quiet stretch, before the
        // call that woke it reads anything (the parts that slept bring themselves up to date here)
        public static void OnWake(Action a) { if (a != null) wakes.Add(a); }

        // ---------------------------------------------------------------- use (who is driving)
        public const double UseWindowS = 30;
        internal static int MainThread = -1;
        static long usedAt, toolUsedAt;
        static volatile bool everUsed, everTool, usedByTool, wakePending;

        // any thread: a play tool call (tool) or a play / ctl / vsync console command reached the module. True when the
        // module was asleep: the caller then runs WakeNow on the main thread
        public static bool Used(bool tool)
        {
            bool asleep = !InUse;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            System.Threading.Interlocked.Exchange(ref usedAt, now);
            if (tool)
            {
                System.Threading.Interlocked.Exchange(ref toolUsedAt, now);
                everTool = true;
            }
            usedByTool = tool;
            everUsed = true;
            if (asleep) wakePending = true;
            try { TouchCore(); } catch (Exception) { }
            return asleep;
        }

        static void TouchCore() { Background.Touch(); }

        // main thread: Used plus the wake actions when the module slept
        public static void UsedHere(bool tool) { if (Used(tool)) WakeNow(); }

        // any thread: a tool call or console command in the last UseWindowS seconds
        public static bool InUse
        {
            get
            {
                if (!everUsed) return false;
                long t = System.Threading.Interlocked.Read(ref usedAt);
                return System.Diagnostics.Stopwatch.GetTimestamp() - t < (long)(UseWindowS * System.Diagnostics.Stopwatch.Frequency);
            }
        }

        // any thread: a client's tool call in the last UseWindowS seconds (an agent is connected and working)
        public static bool ToolInUse
        {
            get
            {
                if (!everTool) return false;
                long t = System.Threading.Interlocked.Read(ref toolUsedAt);
                return System.Diagnostics.Stopwatch.GetTimestamp() - t < (long)(UseWindowS * System.Diagnostics.Stopwatch.Frequency);
            }
        }

        // any thread: Stopwatch timestamp of the last tool call (0 = none yet)
        public static long ToolUsedAt { get { return everTool ? System.Threading.Interlocked.Read(ref toolUsedAt) : 0; } }

        // any thread: seconds since the last use, -1 = never used
        public static double IdleSeconds
        {
            get
            {
                if (!everUsed) return -1;
                long t = System.Threading.Interlocked.Read(ref usedAt);
                return (System.Diagnostics.Stopwatch.GetTimestamp() - t) / (double)System.Diagnostics.Stopwatch.Frequency;
            }
        }

        // any thread: the last use was a tool call of a client (false: a console / bridge command)
        public static bool LastUseWasTool { get { return everUsed && usedByTool; } }

        // any thread: the caller runs on the game's main thread (false before the extension started)
        public static bool OnMainThread { get { return MainThread == System.Threading.Thread.CurrentThread.ManagedThreadId; } }

        // main thread
        public static void WakeNow()
        {
            if (!wakePending) return;
            wakePending = false;
            for (int i = 0; i < wakes.Count; i++)
            {
                try { wakes[i](); }
                catch (Exception e) { DevLog.Error("[play] wake", e); }
            }
        }

        // ---------------------------------------------------------------- the frame
        // every tick shows in the bridge command perf as play.<name> (part of Extensions) while somebody uses the tool
        // (Background.InUse: the rows are read by the panel and the perf command only); off when the core's Perf is not
        // there (offline tests)
        public static bool timed = true;

        static bool Watched() { return Background.InUse; }

        static void Timed(PlayTick t)
        {
            var s = t.sub as Perf.Sub;
            if (s == null) { s = Perf.Get(t.perfName); t.sub = s; }
            // the perf fail-safe switched the row off: the clock and the input must keep running (runs end there, the
            // control lane depends on it)
            if (s.disabled) { if (t.critical) Plain(t); return; }
            Perf.RunNested(t.perfName, t.fn);
        }

        static void Plain(PlayTick t)
        {
            try { t.fn(); t.fails = 0; }
            catch (Exception e)
            {
                t.fails++;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (t.lastLog == 0 || now - t.lastLog > 5 * System.Diagnostics.Stopwatch.Frequency)
                {
                    t.lastLog = now;
                    DevLog.Error("[play] tick " + t.name, e);
                }
                if (t.fails >= 30 && !t.critical && !t.off)
                {
                    t.off = true;
                    DevLog.Write("[play] tick " + t.name + " threw 30 times in a row and is now off");
                }
            }
        }

        public static void RunTicks()
        {
            if (wakePending) WakeNow();
            bool use = InUse, watched = false;
            if (timed)
            {
                try { watched = Watched(); }
                catch (Exception) { timed = false; }
            }
            for (int i = 0; i < ticks.Count; i++)
            {
                var t = ticks[i];
                if (t.off) continue;
                if (t.when == TickWhen.OnDemand ? !t.active : t.when == TickWhen.InUse && !use) continue;
                if (watched && timed)
                {
                    try { Timed(t); continue; }
                    catch (Exception) { timed = false; }
                }
                Plain(t);
            }
        }

        // main thread: the names of the ticks that run in this frame
        public static List<string> Running()
        {
            var l = new List<string>();
            bool use = InUse;
            foreach (var t in ticks)
            {
                if (t.off) continue;
                if (t.when == TickWhen.OnDemand ? !t.active : t.when == TickWhen.InUse && !use) continue;
                l.Add(t.name);
            }
            return l;
        }

        public static string RunningText()
        {
            var l = Running();
            return l.Count == 0 ? "none" : l.Count + ": " + string.Join(", ", l.ToArray());
        }

        // main thread: ticks that no longer run because they kept throwing (the perf fail-safe or the registry's own)
        public static List<string> DisabledTicks()
        {
            var l = new List<string>();
            foreach (var t in ticks)
            {
                bool dis = t.off;
                if (!dis && timed) { try { dis = PerfDisabled(t); } catch (Exception) { } }
                if (dis) l.Add(t.name + (t.critical ? " (critical: still run, without its perf row)" : ""));
            }
            return l;
        }

        static bool PerfDisabled(PlayTick t)
        {
            var s = t.sub as Perf.Sub;
            return s != null && s.disabled;
        }
    }

    // when a registered tick runs
    public enum TickWhen { Always, OnDemand, InUse }

    // one per-frame tick of a Play module (PlayRegistry.OnTick). Active switches an OnDemand tick: any thread.
    public sealed class PlayTick
    {
        internal string name, perfName;
        internal Action fn;
        internal TickWhen when;
        internal object sub;
        internal volatile bool active, off;
        internal bool critical;
        internal int fails;
        internal long lastLog;

        public string Name { get { return name; } }
        public bool Active { get { return active; } set { active = value; } }
        // the control lane depends on this tick: it keeps running when the perf fail-safe switched its row off
        public PlayTick Critical() { critical = true; return this; }
    }
}
