using System;
using System.Collections.Generic;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // Restore.cs (owner TIME, Restorer API and the restore schema frozen): the restore registry. Every module that
    // changes something the player would notice registers an undo the first time (Register is idempotent per key: the
    // first undo, holding the original state, wins) and Forgets the key when it put the state back itself. Key
    // convention: "input", "time", "camera", "stream_pin:<sub>", "stream_freeze", "god", "air", "hazard:<id>",
    // "menu_lock", "title_hide", "overlay", "render:<what>", "audio:<what>", "debug_flag:<flag>", "save_lock".
    // RunAll (main thread) runs undos in reverse registration order, each in try/catch; save_lock is kept unless asked
    // for (unlocking writes the in-memory save). The restore tool and IMcpToolProvider.Restore (session end, game quit,
    // "mcp off") call it; it also cancels the running control job and releases input first.
    // The restore tool (worker): cancels the running control job (never a MultiCam capture, tool "mcam") and waits up to
    // 2 s for it to end, then on the main thread releases input, ends a run that is still active, runs the registry,
    // gives control back to the player (AgentCore.Uninstall unless keep_agent_input), switches the core free camera off
    // and the MCP camera back (WorldCamera.Release when still overridden), and runs every other provider's Restore (dev
    // panels). The registry itself is RestoreBook (RestoreCore.cs, Unity-free).
    public static class Restorer
    {
        public const string Owner = "TIME";
        public const int CancelWaitMs = 2000;
        public const string MultiCamTool = "mcam";

        static readonly RestoreBook book = new RestoreBook();

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("restore", "Restore normal play", PlayShared.Both, Owner,
                "Put the game back to normal: cancel the running action, release held input, give control back to the player, realtime at speed 1, camera back to the game, god / hazards / air / stream pins / menu lock / render and audio changes made through MCP undone. Lists what it reset. The save lock is kept unless include_save_lock.",
                Schema.Obj().Arr("only", "Only these registry keys or key prefixes (camera, time, input, hazard:, ...). Default all.", Schema.Str(), 0, 32)
                    .Bool("include_save_lock", "Also release the save lock (the game then writes INSIDE.sav).", false)
                    .Bool("keep_agent_input", "Keep AgentCore installed (neutral input) instead of human control.", false)
                    .Enum("observe", "Observation in the result.", "concise", "concise", "none").Build(),
                Schema.Obj().Arr("reset", "What was put back.", Schema.OutOf(Schema.Obj().Str("key", "Registry key.").Str("what", "Description.").Str("result", "Outcome text.")), 0, 64)
                    .Arr("kept", "Keys left as they are.", Schema.Str(), 0, 64).Arr("errors", "Undos that failed.", Schema.Str(), 0, 64)
                    .Prop("observe", null, Observer.ConciseRef()).BuildOutput()).Idempotent(), Handle);
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            var only = PlayShared.Strings(a, "only");
            bool includeSaveLock = PlayShared.Bool(a, "include_save_lock", false);
            bool keepAgent = PlayShared.Bool(a, "keep_agent_input", false);
            bool wantObserve = PlayShared.Str(a, "observe", "concise") != "none";
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
                    job.Cancel("restore");
                    try { jobEnded = job.Await(CancelWaitMs); } catch (NotImplementedException) { jobEnded = false; }
                }
            }
            object obs = null;
            var o = ctx.Main(() =>
            {
                var r = Restore(only, includeSaveLock, keepAgent, true);
                if (wantObserve) obs = Stepper.TryConcise("restore");
                return r;
            });
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
                    if (AgentCore.Installed) { AgentCore.Uninstall(); reset.Add(RestoreBook.Row("input", "agent input core", "uninstalled: the player has control")); }
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
            Cap(reset, 64);
            Cap(errors, 64);
            Cap(r["kept"] as List<object>, 64);
            return r;
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
        // main thread: {reset[{key, what, result}], kept[], errors[]}; only = key prefixes (null = all)
        public static Json.Obj RunAll(List<string> only, bool includeSaveLock) { return Restore(only, includeSaveLock, false, false); }
    }
}
