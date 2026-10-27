using System;
using System.Collections.Generic;
using System.Threading;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // StepperCore.cs (owner TIME): the Unity-free half of the game clock, compiled into InsideDev.Play.dll and, as is,
    // into the offline tests (qa/mcp/TIME). StepClock keeps mode / speed / freeze and advances every Run frame by frame
    // in Tick (main thread): it counts logic steps from IClockHost.StepCount, evaluates the stops in a fixed order
    // (abandoned, cancelled, stop check, load started, died, max steps, wall clock, paused / stalled; so a condition that
    // holds in the frame of a death still counts) and re-freezes in the same Tick that saw the stop. Frames while
    // loading or not controllable never count as stalled. The worker side (StepperCore.RunOn) only starts the run
    // through ctx.Main, waits and sends progress; Tick owns all clean-up, so a cancelled or abandoned call never leaves
    // the game stepping. IClockHost is the game (GameClockHost in Stepper.cs) or a fake clock in the tests.
    public enum StepMode { Step, Realtime }

    public sealed class RunRequest
    {
        public int maxSteps = 1;
        public int timeoutMs = 50000;
        // main thread after each step; a non-null return stops the run with stop = condition and detail = the string
        public Func<string> stopCheck;
        public bool stopOnDeath = true, stopOnLoad = true;
        // step mode: leave the game running at the end (then:"run") instead of re-freezing
        public bool leaveRunning;
        // progress message prefix (notifications/progress every 15 steps or 0.25 s)
        public string label = "";
    }

    public static class RunStop
    {
        public const string Steps = "steps", Condition = "condition", Died = "died", Loading = "loading", Cancelled = "cancelled",
            Timeout = "timeout", Stalled = "stalled";
    }

    public sealed class RunResult
    {
        public int steps, frames;
        // RunStop value
        public string stop = RunStop.Steps;
        public string detail = "";
        public long fromStep, toStep;
        public double elapsedMs;
        public readonly List<string> warnings = new List<string>();

        // {steps, frames, stop, detail, from_step, to_step, elapsed_ms, warnings[]}
        public Json.Obj ToJson()
        {
            var o = new Json.Obj();
            o["steps"] = (double)steps;
            o["frames"] = (double)frames;
            o["stop"] = stop ?? "";
            o["detail"] = detail ?? "";
            o["from_step"] = (double)fromStep;
            o["to_step"] = (double)toStep;
            o["elapsed_ms"] = Math.Round(elapsedMs, 1);
            var w = new List<object>();
            foreach (var s in warnings) w.Add(s);
            o["warnings"] = w;
            return o;
        }
    }

    // The game behind the clock. Everything is main thread except NowMs (any thread).
    internal interface IClockHost
    {
        long StepCount { get; }
        long FrameCount { get; }
        double NowMs { get; }
        // GameManager.IsLoading (a load is running, its fade-out included)
        bool Loading { get; }
        // GameManager.IsPaused (menu, message, focus)
        bool Paused { get; }
        // the controlled character is dead
        bool Dead { get; }
        // a MultiCam capture owns the clock: hands off
        bool External { get; }
        // safe to freeze and to step deterministically: not loading, the load's fade-in done and input back, no title
        bool CanControl(out string reason);
        float GameScale { get; }
        // GameManager.TimeScale = scale; Time.timeScale = scale unless that would unpause a paused or loading game
        void ApplyScale(float scale);
        // vSync 0 + captureFramerate 60 (previous values kept) / put them back
        void BeginStepping();
        void EndStepping();
        // first change of a session (Restorer key "time", runInBackground on) / the undo of it, returns what was put back
        void Touch();
        string Untouch();
        // a music-clock hazard near the body, or null
        string MusicTimed();
    }

    internal sealed class StepRun
    {
        public RunRequest req;
        public Func<bool> cancelled;
        public long startStep, lastStep, startFrame, toStep;
        public double startMs, deadlineMs, elapsedMs;
        public volatile int steps;
        public int frames, lastProgressFrame, pausedFrames;
        public bool wasLoading, wasDead, stepping, refreeze;
        public volatile bool finished, abandoned;
        public string stop = RunStop.Steps, detail = "";
        public readonly List<string> warnings = new List<string>();
        public readonly object gate = new object();

        public void Warn(string w)
        {
            lock (gate) { if (w != null && !warnings.Contains(w)) warnings.Add(w); }
        }

        public RunResult Result(double nowMs, string fallbackStop, string fallbackDetail)
        {
            lock (gate)
            {
                var r = new RunResult { steps = steps, frames = frames, fromStep = startStep };
                if (finished) { r.stop = stop; r.detail = detail; r.toStep = toStep; r.elapsedMs = elapsedMs; }
                else { r.stop = fallbackStop; r.detail = fallbackDetail; r.toStep = lastStep; r.elapsedMs = nowMs - startMs; }
                r.warnings.AddRange(warnings);
                return r;
            }
        }
    }

    internal sealed class StepClock
    {
        public const int StallFramesStep = 30, StallFramesRealtime = 120, PausedFramesLimit = 10;
        public const double MinSpeed = 0.05, MaxSpeed = 4;

        readonly IClockHost h;
        StepMode mode = StepMode.Realtime;
        bool modeChosen, wantFrozen, touched;
        volatile bool frozenApplied;
        double speed = 1;
        volatile StepRun run;
        long step = -1, frame = -1;

        public StepClock(IClockHost host) { h = host; }

        public IClockHost Host { get { return h; } }
        public StepMode Mode { get { return mode; } }
        public double Speed { get { return speed; } }
        public bool Frozen { get { return frozenApplied; } }
        public bool WantFrozen { get { return wantFrozen; } }
        public bool ModeChosen { get { return modeChosen; } }
        public bool Touched { get { return touched; } }
        public bool Running { get { return run != null; } }
        public StepRun Current { get { return run; } }
        public long Step { get { return Interlocked.Read(ref step); } }
        public long Frame { get { return Interlocked.Read(ref frame); } }

        public static double ClampSpeed(double s)
        {
            if (double.IsNaN(s)) return 1;
            return Math.Max(MinSpeed, Math.Min(MaxSpeed, s));
        }

        public static string MusicWarning(string what)
        {
            return "music_timed: " + what + ": step-mode runs are paced to real time here (the music pauses while frozen), slow motion desyncs it; act / move_to / interact {when} wait for a phase in the same call";
        }

        void TouchOnce()
        {
            if (touched) return;
            touched = true;
            h.Touch();
        }

        public List<string> SetMode(StepMode m)
        {
            TouchOnce();
            modeChosen = true;
            mode = m;
            wantFrozen = m == StepMode.Step;
            var w = new List<string>();
            if (run == null) ApplyNow(w);
            MusicNote(w);
            return w;
        }

        public List<string> SetSpeed(double s)
        {
            TouchOnce();
            speed = ClampSpeed(s);
            var w = new List<string>();
            if (mode == StepMode.Step) w.Add("speed: stored, it applies in realtime mode (time {op:\"realtime\"})");
            else if (!wantFrozen && run == null) ApplyNow(w);
            MusicNote(w);
            return w;
        }

        public List<string> Freeze()
        {
            TouchOnce();
            wantFrozen = true;
            var w = new List<string>();
            if (run == null) ApplyNow(w);
            return w;
        }

        public List<string> Unfreeze()
        {
            TouchOnce();
            wantFrozen = false;
            var w = new List<string>();
            if (run == null) ApplyNow(w);
            MusicNote(w);
            return w;
        }

        void MusicNote(List<string> w)
        {
            if (mode == StepMode.Realtime && Math.Abs(speed - 1) < 1e-6) return;
            string what = h.MusicTimed();
            if (what != null) w.Add(MusicWarning(what));
        }

        float TargetScale() { return mode == StepMode.Realtime ? (float)speed : 1f; }

        void ApplyNow(List<string> w)
        {
            if (h.External) { frozenApplied = false; if (w != null) w.Add("external: a MultiCam capture owns the clock"); return; }
            string reason;
            bool can = h.CanControl(out reason);
            if (wantFrozen)
            {
                if (can) { h.ApplyScale(0f); frozenApplied = true; }
                else { frozenApplied = false; if (w != null) w.Add("freeze_deferred: " + reason + ": the game freezes once it is ready"); }
            }
            else
            {
                frozenApplied = false;
                h.ApplyScale(TargetScale());
            }
        }

        // main thread, every frame
        public void Tick()
        {
            Interlocked.Exchange(ref step, h.StepCount);
            Interlocked.Exchange(ref frame, h.FrameCount);
            var r = run;
            if (r != null) { TickRun(r); return; }
            if (!touched) return;
            if (h.External) { frozenApplied = false; return; }
            string reason;
            bool can = h.CanControl(out reason);
            if (wantFrozen)
            {
                if (can) { h.ApplyScale(0f); frozenApplied = true; }
                else frozenApplied = false;
                return;
            }
            frozenApplied = false;
            if (!can) return;
            if (mode == StepMode.Realtime) { if (Math.Abs(h.GameScale - speed) > 1e-4) h.ApplyScale((float)speed); }
            else if (h.GameScale == 0f) h.ApplyScale(1f);
        }

        // main thread: start a run (the caller holds the control lane)
        public StepRun Begin(RunRequest req, Func<bool> cancelled, double budgetMs)
        {
            if (run != null) throw new McpException(McpErr.Busy, "a run is already active (" + run.req.label + ")", "wait for it, or call restore");
            TouchOnce();
            if (!modeChosen) { modeChosen = true; mode = StepMode.Step; wantFrozen = true; }
            var r = new StepRun { req = req ?? new RunRequest(), cancelled = cancelled, startStep = h.StepCount, startFrame = h.FrameCount, startMs = h.NowMs };
            r.lastStep = r.startStep;
            r.toStep = r.startStep;
            r.deadlineMs = r.startMs + Math.Max(1, budgetMs);
            r.wasLoading = h.Loading;
            r.wasDead = h.Dead;
            frozenApplied = false;
            if (h.External) r.Warn("external: a MultiCam capture owns the clock");
            else if (mode == StepMode.Step)
            {
                h.BeginStepping();
                r.stepping = true;
                string reason;
                if (h.CanControl(out reason)) { if (!h.Paused) h.ApplyScale(1f); }
                else r.Warn((h.Loading ? "loading" : "not_ready") + ": " + reason + ": the game runs in realtime until it is ready, then steps");
            }
            else
            {
                if (wantFrozen) { r.refreeze = true; wantFrozen = false; }
                h.ApplyScale((float)speed);
            }
            MusicRun(r);
            run = r;
            return r;
        }

        void MusicRun(StepRun r)
        {
            if (mode == StepMode.Realtime && Math.Abs(speed - 1) < 1e-6) return;
            string what = h.MusicTimed();
            if (what != null) r.Warn(MusicWarning(what));
        }

        static bool Probe(Func<bool> f)
        {
            if (f == null) return false;
            try { return f(); } catch { return false; }
        }

        void TickRun(StepRun r)
        {
            if (h.FrameCount == r.startFrame && !r.abandoned) return;
            long sc = h.StepCount;
            r.frames++;
            if (sc > r.lastStep) { r.steps += (int)Math.Min(int.MaxValue, sc - r.lastStep); r.lastProgressFrame = r.frames; }
            r.lastStep = sc;
            bool loading = h.Loading, dead = h.Dead, paused = h.Paused;
            string reason;
            bool can = h.CanControl(out reason);
            string stop = null, detail = "";
            if (r.abandoned) { stop = RunStop.Cancelled; detail = "abandoned: the caller stopped waiting"; }
            else if (Probe(r.cancelled)) { stop = RunStop.Cancelled; detail = "cancelled"; }
            else if (r.req.stopCheck != null)
            {
                string c = null;
                try { c = r.req.stopCheck(); }
                catch (Exception e) { stop = RunStop.Stalled; detail = "stop check failed: " + e.GetType().Name + ": " + e.Message; }
                if (c != null) { stop = RunStop.Condition; detail = c; }
            }
            if (stop == null && r.req.stopOnLoad && loading && !r.wasLoading) { stop = RunStop.Loading; detail = "a load started"; }
            if (stop == null && r.req.stopOnDeath && dead && !r.wasDead) { stop = RunStop.Died; detail = "the body died"; }
            if (stop == null && r.req.maxSteps > 0 && r.steps >= r.req.maxSteps) { stop = RunStop.Steps; detail = r.steps + " steps"; }
            if (stop == null && h.NowMs > r.deadlineMs) { stop = RunStop.Timeout; detail = "wall-clock limit"; }
            if (loading || !can) { r.lastProgressFrame = r.frames; r.pausedFrames = 0; }
            else if (stop == null)
            {
                r.pausedFrames = paused ? r.pausedFrames + 1 : 0;
                int idle = r.frames - r.lastProgressFrame;
                int limit = mode == StepMode.Step ? StallFramesStep : (int)Math.Ceiling(StallFramesRealtime / Math.Min(1.0, speed));
                if (r.pausedFrames >= PausedFramesLimit) { stop = RunStop.Stalled; detail = "the game is paused (menu, message or window focus)"; }
                else if (!paused && idle >= limit) { stop = RunStop.Stalled; detail = "no logic step for " + idle + " frames"; }
            }
            r.wasLoading = loading;
            r.wasDead = dead;
            if (stop != null) { End(r, stop, detail); return; }
            if (h.External) return;
            if (mode == StepMode.Step) { if (can && !paused) h.ApplyScale(1f); }
            else if (can && Math.Abs(h.GameScale - speed) > 1e-4) h.ApplyScale((float)speed);
        }

        // main thread: end r now with stop / detail and freeze or keep running as asked
        public void End(StepRun r, string stop, string detail)
        {
            lock (r.gate)
            {
                if (r.finished) return;
                r.stop = stop;
                r.detail = detail ?? "";
                r.toStep = h.StepCount;
                r.elapsedMs = h.NowMs - r.startMs;
            }
            if (r.stepping) { h.EndStepping(); r.stepping = false; }
            if (mode == StepMode.Step) wantFrozen = !r.req.leaveRunning;
            else if (r.req.leaveRunning) wantFrozen = false;
            else if (r.refreeze) wantFrozen = true;
            if (run == r) run = null;
            var w = new List<string>();
            ApplyNow(w);
            foreach (var x in w) if (!x.StartsWith("external")) r.Warn(x);
            MusicRun(r);
            lock (r.gate) { r.finished = true; }
        }

        public void AbortRun(string why)
        {
            var r = run;
            if (r != null) End(r, RunStop.Cancelled, why);
        }

        // main thread: the Restorer "time" undo
        public string Reset()
        {
            AbortRun("restore");
            mode = StepMode.Realtime;
            speed = 1;
            wantFrozen = false;
            modeChosen = false;
            frozenApplied = false;
            string what = "realtime, speed 1";
            if (!h.External) h.ApplyScale(1f);
            else what += " (MultiCam capture owns the scale)";
            if (touched) { touched = false; string u = h.Untouch(); if (!string.IsNullOrEmpty(u)) what += ", " + u; }
            return what;
        }
    }

    // Real-time pacing of captured frames (step-mode runs in music-timed places, input_track playback): with
    // captureFramerate 60 every frame is one logic step, so holding each frame to a 60 Hz wall-clock grid keeps game
    // time with the Wwise music. Wait(now) is called once per frame and returns how long that frame must still wait;
    // a frame later than two periods restarts the grid (and counts in late) instead of rushing to catch up.
    internal sealed class FramePacer
    {
        public double periodMs = 1000.0 / 60.0;
        double next = -1;
        public int frames, late;

        public void Reset() { next = -1; frames = 0; late = 0; }

        public double Wait(double nowMs)
        {
            frames++;
            if (next < 0) { next = nowMs + periodMs; return 0; }
            double w = next - nowMs;
            if (w < -2 * periodMs) { late++; next = nowMs + periodMs; return 0; }
            next += periodMs;
            return w > 0 ? w : 0;
        }
    }

    internal static class StepperCore
    {
        public const int SliceMs = 250, ReplyReserveMs = 1500, StallGraceMs = 3000, CancelGraceMs = 2000;

        // worker thread: run req on clock and wait for its end (see Stepper.Run)
        public static RunResult RunOn(CallContext ctx, StepClock clock, RunRequest req)
        {
            if (req == null) req = new RunRequest();
            ctx.ThrowIfCancelled();
            McpJob job = null;
            try { job = ctx.RunningJob(McpLanes.Control); } catch (NotImplementedException) { }
            var j = job;
            Func<bool> cancelled = () => ctx.Cancelled || (j != null && j.Cancelled);
            int want = req.timeoutMs > 0 ? req.timeoutMs : 50000;
            int budget = Math.Max(50, Math.Min(want, ctx.RemainingMs - ReplyReserveMs));
            var r = ctx.Main(() => clock.Begin(req, cancelled, budget));
            int lastSteps = -1;
            string prefix = string.IsNullOrEmpty(req.label) ? "" : req.label + ": ";
            var spin = System.Diagnostics.Stopwatch.StartNew();
            while (!r.finished)
            {
                spin.Reset();
                spin.Start();
                bool done = ctx.WaitUntil(() => r.finished, SliceMs, 1);
                if (r.finished) break;
                if (!done && spin.ElapsedMilliseconds < 5 && !cancelled()) Thread.Sleep(5);
                int s = r.steps;
                if (s != lastSteps)
                {
                    lastSteps = s;
                    string msg = prefix + "step " + s + (req.maxSteps > 0 ? "/" + req.maxSteps : "");
                    try { ctx.Progress(s, req.maxSteps > 0 ? req.maxSteps : 0, msg); } catch { }
                    try { if (j != null) j.Progress(s, req.maxSteps > 0 ? req.maxSteps : 0, msg); } catch { }
                }
                if (cancelled()) { WaitFinished(ctx, r, CancelGraceMs); break; }
                if (clock.Host.NowMs > r.deadlineMs + StallGraceMs) { r.abandoned = true; WaitFinished(ctx, r, 500); break; }
            }
            if (!r.finished) r.abandoned = true;
            return r.Result(clock.Host.NowMs, cancelled() ? RunStop.Cancelled : RunStop.Timeout, "the main thread did not finish the run in time; it stops at its next frame");
        }

        static void WaitFinished(CallContext ctx, StepRun r, int ms)
        {
            for (int i = 0; i < ms / 10 && !r.finished; i++)
                if (!ctx.Sleep(10)) Thread.Sleep(10);
        }
    }
}
