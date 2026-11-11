using System;
using System.Collections.Generic;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // Restore.cs (owner TIME, Restorer API and the restore schema frozen): the restore registry. Every module that
    // changes something the player would notice registers an undo the first time (Register is idempotent per key: the
    // first undo, holding the original state, wins) and Forgets the key when it put the state back itself. Key
    // convention: "input", "time", "camera", "stream_pin:<sub>", "stream_freeze", "god", "air", "hazard:<id>",
    // "menu_lock", "menu_open", "title_hide", "overlay", "render:<what>", "audio:<what>", "debug_flag:<flag>", "vsync",
    // "save_lock".
    // RunAll (main thread) runs undos in reverse registration order, each in try/catch, the clock's ("time") after all
    // others; save_lock is kept unless asked for (unlocking writes the in-memory save). The restore tool and
    // IMcpToolProvider.Restore (session end, game quit, "mcp off") call it; it also cancels the running control job and
    // releases input first.
    // The restore tool (worker): cancels the running control job (never a MultiCam capture, tool "mcam") and waits up to
    // 2 s for it to end, then on the main thread releases input, ends a run that is still active, runs the registry,
    // gives control back to the player (AgentCore.Uninstall unless keep_agent_input), switches the core free camera off
    // and the MCP camera back (WorldCamera.Release when still overridden), and runs every other provider's Restore (dev
    // panels). The registry itself is RestoreBook (RestoreCore.cs, Unity-free).
    // The clock is the last thing a full restore touches: when something that ran after the clock's own undo took the
    // clock again, that undo runs once more, and the result's clock field is read from the game after everything.
    // dry_run (tool) and "play restore list" (console) show what a restore would cancel, reset and keep without doing
    // it. Things restore does not put back (the master volume after a mute, an RNG seed) register a note with Keep and
    // show in kept.
    public static class Restorer
    {
        public const string Owner = "TIME";
        public const int CancelWaitMs = 2000;
        public const string MultiCamTool = "mcam";

        static readonly RestoreBook book = new RestoreBook();
        static volatile string why = "";

        // any thread: who ran the restore that is running or ran last ("" before the first one)
        public static string Why { get { return why; } }

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("restore", "Restore normal play", PlayShared.Both, Owner,
                "Put the game back to normal: cancel the running action, release held input, give control back to the player, realtime at speed 1, camera back to the game, god / hazards / air / stream pins / menu lock / render and audio changes made through MCP undone. Lists what it reset. The save lock is kept unless include_save_lock. dry_run only lists what it would do.",
                Schema.Obj().Arr("only", "Only these registry keys or key prefixes (camera, time, input, hazard:, ...). Default all.", Schema.Str(), 0, 32)
                    .Bool("include_save_lock", "Also release the save lock (the game then writes INSIDE.sav).", false)
                    .Bool("keep_agent_input", "Keep AgentCore installed (neutral input) instead of human control.", false)
                    .Bool("dry_run", "Change nothing: list what a restore would cancel (would_cancel), reset (would_reset) and keep.", false)
                    .Enum("observe", "Observation in the result.", "concise", "concise", "none").Build(),
                Schema.Obj().Arr("reset", "What was put back.", Schema.OutOf(Schema.Obj().Str("key", "Registry key.").Str("what", "Description.").Str("result", "Outcome text.")), 0, 64)
                    .Arr("kept", "Keys left as they are, and things restore never puts back.", Schema.Str(), 0, 64).Arr("errors", "Undos that failed.", Schema.Str(), 0, 64)
                    .Bool("dry_run", "Nothing was changed.")
                    .Arr("would_reset", "dry_run: what a restore would put back, in order.", Schema.OutOf(Schema.Obj().Str("key", "Registry key.").Str("what", "Description.")), 0, 80)
                    .Str("would_cancel", "dry_run: the running action a restore would cancel.")
                    .OutObj("clock", "The game clock after the restore (dry_run: now).", Schema.Obj().Enum("mode", "", null, "step", "realtime").Bool("frozen", "")
                        .Num("speed", "", 0, 4).Num("scale", "Time.timeScale.", 0, 100).Bool("held", "Play still holds the clock."))
                    .Prop("observe", null, Observer.ConciseRef()).BuildOutput()).Idempotent(), Handle);
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            var only = PlayShared.Strings(a, "only");
            bool includeSaveLock = PlayShared.Bool(a, "include_save_lock", false);
            bool keepAgent = PlayShared.Bool(a, "keep_agent_input", false);
            bool wantObserve = PlayShared.Str(a, "observe", "concise") != "none";
            if (PlayShared.Bool(a, "dry_run", false)) return ToolResult.Ok(ctx.Main(() => Plan(only, includeSaveLock, keepAgent)));
            bool all = only.Count == 0;
            string cancelled = null, mcam = null;
            bool jobEnded = true;
            if (all || RestoreBook.Matches("input", only) || RestoreBook.Matches("time", only))
            {
                McpJob job = null;
                try { job = ctx.RunningJob(McpLanes.Control); } catch (NotImplementedException) { }
                if (job != null && job.Tool == MultiCamTool) mcam = job.Id;
                else if (job != null)
                {
                    cancelled = job.Id + " (" + job.Tool + ")";
                    why = "the restore tool";
                    job.Cancel("restore");
                    try { jobEnded = job.Await(CancelWaitMs); } catch (NotImplementedException) { jobEnded = false; }
                }
            }
            object obs = null;
            var o = ctx.Main(() =>
            {
                var r = Restore(only, includeSaveLock, keepAgent, true);
                if (wantObserve) obs = Stepper.TryConcise("restore", ctx.Info.sessionId);
                return r;
            });
            if (all) ChangeLog.Idle();
            var reset = o["reset"] as List<object>;
            var errors = o["errors"] as List<object>;
            if (cancelled != null && reset != null) reset.Insert(0, RestoreBook.Row("job", "running action " + cancelled, jobEnded ? "cancelled" : "cancel requested (still ending)"));
            if (cancelled != null && !jobEnded && errors != null) errors.Add("job " + cancelled + ": did not end within " + (CancelWaitMs / 1000) + " s");
            if (mcam != null) (o["kept"] as List<object>).Insert(0, "job:" + mcam + " (a MultiCam capture is not cancelled by restore)");
            if (obs != null) o["observe"] = obs;
            return ToolResult.Ok(o);
        }

        // main thread: the whole restore. tool = the restore tool (also the core free camera and the other providers'
        // Restore); false = IMcpToolProvider.Restore (session end, idle, quit), where the host runs the other providers.
        internal static Json.Obj Restore(List<string> only, bool includeSaveLock, bool keepAgent, bool tool)
        {
            bool all = only == null || only.Count == 0;
            bool input = all || RestoreBook.Matches("input", only);
            bool time = all || RestoreBook.Matches("time", only);
            bool camera = all || RestoreBook.Matches("camera", only);
            why = tool ? "the restore tool" : "the host: idle, session end, mcp off, play restore or quit";
            if (input || time) CancelControlJob();
            if (input) Try(() => AgentCore.Release());
            if (time) Stepper.Clock.AbortRun("restore");
            var exclude = keepAgent ? new List<string> { "input" } : null;
            var r = book.RunAll(only, includeSaveLock, exclude);
            var reset = r["reset"] as List<object>;
            var errors = r["errors"] as List<object>;
            if (input && !keepAgent)
            {
                try
                {
                    if (AgentCore.Installed)
                    {
                        AgentCore.Uninstall();
                        reset.Add(RestoreBook.Row("input", "agent input core", AgentCore.Installed
                            ? "human control; the core stays in place as the tap of the running input track recording"
                            : "uninstalled: the player has control"));
                    }
                }
                catch (NotImplementedException) { }
                catch (Exception e) { errors.Add("input: " + e.GetType().Name + ": " + e.Message); }
            }
            if (camera)
            {
                try
                {
                    if (WorldCamera.Overridden) reset.Add(RestoreBook.Row("camera", "camera override", WorldCamera.Release() ?? "released"));
                }
                catch (NotImplementedException) { }
                catch (Exception e) { errors.Add("camera: " + e.GetType().Name + ": " + e.Message); }
                try
                {
                    if (tool && CameraControl.free && !MultiCam.Capturing) { CameraControl.SetFree(false); reset.Add(RestoreBook.Row("free_camera", "core free camera", "off")); }
                }
                catch (Exception e) { errors.Add("free_camera: " + e.GetType().Name + ": " + e.Message); }
            }
            if (tool && all)
            {
                List<IMcpToolProvider> list = null;
                try { list = McpRegistry.Providers(); } catch (NotImplementedException) { }
                if (list != null)
                    foreach (var p in list)
                    {
                        if (p == null || p.ProviderName == "play") continue;
                        var lines = new List<string>();
                        try { p.Restore(lines); }
                        catch (NotImplementedException) { continue; }
                        catch (Exception e) { errors.Add("provider:" + p.ProviderName + ": " + e.GetType().Name + ": " + e.Message); continue; }
                        foreach (var line in lines) reset.Add(RestoreBook.Row("provider:" + p.ProviderName, p.ProviderName + " tools", line));
                    }
            }
            if (time)
            {
                // the clock is the last word: something that ran after its undo took it again
                try
                {
                    if (book.IsSet("time") && RestoreBook.Matches("time", only))
                    {
                        var again = book.RunAll(new List<string> { "time" }, false, null);
                        var rows = again["reset"] as List<object>;
                        if (rows != null) foreach (var x in rows) { var row = x as Json.Obj; if (row != null) { row["result"] = row.Str("result", "") + " (again: a later undo had taken the clock)"; reset.Add(row); } }
                        var errs = again["errors"] as List<object>;
                        if (errs != null) errors.AddRange(errs);
                    }
                }
                catch (Exception e) { errors.Add("time: " + e.GetType().Name + ": " + e.Message); }
            }
            try { r["clock"] = ClockNow(); } catch (Exception) { }
            Cap(reset, 64);
            Cap(errors, 64);
            Cap(r["kept"] as List<object>, 64);
            return r;
        }

        // main thread: the clock as the game has it now
        static Json.Obj ClockNow()
        {
            var s = Stepper.State();
            var c = new Json.Obj();
            c["mode"] = s["mode"];
            c["frozen"] = s["frozen"];
            c["speed"] = s["speed"];
            c["scale"] = s["scale"];
            c["held"] = Stepper.Clock.Touched;
            return c;
        }

        // main thread: what Restore with the same arguments would do, without doing it (restore {dry_run:true},
        // play restore list): {ok, dry_run, would_cancel?, would_reset[{key, what}], kept[], reset[], errors[], clock}
        internal static Json.Obj Plan(List<string> only, bool includeSaveLock, bool keepAgent)
        {
            bool all = only == null || only.Count == 0;
            bool input = all || RestoreBook.Matches("input", only);
            bool time = all || RestoreBook.Matches("time", only);
            bool camera = all || RestoreBook.Matches("camera", only);
            var o = PlayShared.Env();
            o["dry_run"] = true;
            if (input || time)
            {
                try
                {
                    var j = HostJobs.Running(McpLanes.Control);
                    if (j != null && j.Tool != MultiCamTool) o["would_cancel"] = j.Id + " (" + j.Tool + ")";
                }
                catch (Exception) { }
            }
            var p = book.Plan(only, includeSaveLock, keepAgent ? new List<string> { "input" } : null);
            var would = p["would_reset"] as List<object>;
            if (time && Stepper.Running) would.Insert(0, PlanRow("run", "the active clock run (ends as cancelled)"));
            if (input)
            {
                var held = AgentCore.HeldNow();
                if (held != null || AgentCore.QueuedSteps > 0) would.Insert(0, PlanRow("input", "held or queued agent input (released)"));
                if (!keepAgent && AgentCore.Installed && !book.IsSet("input")) would.Add(PlanRow("input", "agent input core (uninstalled: the player has control)"));
            }
            if (camera)
            {
                try { if (WorldCamera.Overridden) would.Add(PlanRow("camera", "camera override (released)")); } catch (Exception) { }
                try { if (CameraControl.free && !MultiCam.Capturing) would.Add(PlanRow("free_camera", "core free camera (off; the restore tool only)")); } catch (Exception) { }
            }
            o["would_reset"] = would;
            o["kept"] = p["kept"];
            o["reset"] = new List<object>();
            o["errors"] = new List<object>();
            try { o["clock"] = ClockNow(); } catch (Exception) { }
            Cap(would, 80);
            Cap(o["kept"] as List<object>, 64);
            return o;
        }

        static Json.Obj PlanRow(string key, string what)
        {
            var o = new Json.Obj();
            o["key"] = key;
            o["what"] = what;
            return o;
        }

        // the console form of Plan
        internal static string PlanText(Json.Obj plan)
        {
            var sb = new System.Text.StringBuilder();
            var would = plan["would_reset"] as List<object>;
            var kept = plan["kept"] as List<object>;
            int n = would != null ? would.Count : 0;
            sb.Append(n == 0 ? "nothing to restore" : "a restore would reset " + n + (n == 1 ? " thing" : " things") + " (nothing was changed)");
            string cancel = plan.Str("would_cancel", null);
            if (cancel != null) sb.Append("\n  cancel: the running action ").Append(cancel);
            if (would != null) foreach (var x in would) { var row = x as Json.Obj; if (row != null) sb.Append("\n  ").Append(row.Str("key", "?")).Append(": ").Append(row.Str("what", "")); }
            if (kept != null) foreach (var k in kept) sb.Append("\n  kept: ").Append(Convert.ToString(k));
            return sb.ToString();
        }

        static void Cap(List<object> l, int n)
        {
            if (l != null && l.Count > n) l.RemoveRange(n, l.Count - n);
        }

        static void CancelControlJob()
        {
            try
            {
                var j = HostJobs.Running(McpLanes.Control);
                if (j != null && !j.Cancelled && j.Tool != MultiCamTool) j.Cancel("restore");
            }
            catch { }
        }

        static void Try(Action f)
        {
            try { f(); } catch { }
        }

        // any thread (the undo runs on the main thread); what = human text for the report; undo returns a result text
        public static void Register(string key, string what, Func<string> undo) { book.Register(key, what, undo); }
        public static void Forget(string key) { book.Forget(key); }
        public static bool IsSet(string key) { return book.IsSet(key); }
        public static List<string> Keys() { return book.Keys(); }
        // any thread: something restore does not put back. note runs on the main thread with every restore and returns
        // the text for kept ("" or null = no longer true, the note is dropped). One note per key.
        public static void Keep(string key, Func<string> note) { book.Keep(key, note); }
        public static void Unkeep(string key) { book.Unkeep(key); }
        // main thread: {reset[{key, what, result}], kept[], errors[], clock}; only = key prefixes (null = all)
        public static Json.Obj RunAll(List<string> only, bool includeSaveLock) { return Restore(only, includeSaveLock, false, false); }
    }
}
