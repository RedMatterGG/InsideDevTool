using System;
using System.Collections.Generic;
using System.Reflection;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // Stepper.cs (owner TIME, public API and the time tool schema frozen): the game clock for agents.
    //   step mode (default once an agent acts): the game is frozen (GameManager.TimeScale and Time.timeScale 0) between
    //   calls and advances only inside Run. Run sets vSyncCount 0 and Time.captureFramerate 60, so each rendered frame
    //   is one FixedUpdate = one logic step (INFERRED, measured per frame from LogicManager.stepCount and reported in
    //   RunResult.steps), and re-freezes in the frame the stop fires (checks run in Tick, after the frame's step).
    //   realtime mode: the game runs at speed (0.05..4, GameManager.TimeScale + Time.timeScale) and Run just watches.
    // Never freezes while GameManager.IsLoading or before "ready" after a load (title hang, 03 section 4.3); a freeze
    // asked for during a load is applied after it. Every load resets the game's time scale, so Tick re-applies the
    // mode after loadFinished. Slow motion and stepping desync music-timed hazards: State and Run add the warning
    // "music_timed: <what>" when Perception.MusicTimedNear is true. Restore entry "time" (Restorer): realtime, speed 1,
    // capture and vSync back, runInBackground back. Clock ownership while a SceneRecorder samples (srec record start or
    // an mcam record pass; InsideDev.SceneRecorder.active, read by reflection because the pin has no recorder): the
    // recorder owns the frame pacing (captureFramerate, vSyncCount, targetFrameRate; srec re-asserts it every frame), the
    // stepper owns only the time scale (freeze, speed) and skips BeginStepping; an EndStepping during a recording is
    // deferred until it ends, so neither restores the other's values. A MultiCam capture owns everything (External).
    // Pacing (2026-10-25 audit): a step-mode run in a music-timed place (Perception.MusicTimedNear) and an input_track
    // playback (HoldLockstep) hold each captured frame to a 60 Hz wall-clock grid (FramePacer, targetFrameRate -1 and
    // timeBeginPeriod(1) while it runs), so game time keeps pace with the Wwise music, which pauses while the game is
    // frozen; playback gets captureFramerate 60 like the rehearsal (one logic step per frame). Focus: once an agent
    // touched the clock, a GameEventManager Focus layer that a focus loss switched off is switched back on (with
    // runInBackground), and FocusState tells observe.
    // The clock logic itself is StepClock (StepperCore.cs, Unity-free);
    // GameClockHost below is the game behind it. Game APIs (decompiled FP/ = Assembly-CSharp-firstpass): GameManager
    // TimeScale / IsLoading / IsPaused / LoadCount / loadStarted / loadFinished / fader (FP/GameManager.cs:95,115-142,
    // 202-220), UpdateTimeScale writes Time.timeScale only on change (:545-566), FadeAndReload resets timeScale and the
    // fade-in runs on scaled time (:386-431, 303); LogicManager.stepCount (FP/LogicManager.cs:25,32); CameraFader.IsDone
    // (FP/CameraFader.cs:19); GameInput.IsEnabled (FP/GameInput.cs:314); TitleManager.IsShowing / IsPressStartShowing
    // (FP/TitleManager.cs:349,370); GameEventManager.IsTitleMenu (FP/GameEventManager.cs:135); FramerateController
    // SetVSyncCount / GetVirtualVSyncCount / automatic (FP/FramerateController.cs:5,19-36); PDRandom.seed
    // (FP/PDRandom.cs:51-62); RandomGenerator.ResetAll (FP/RandomGenerator.cs:78); MusicManager.GetMusicPosition_s
    // (FP/MusicManager.cs:60, GlobalAudio.music FP/GlobalAudio.cs:16); ForcePushManager.getActiveZoneCount
    // (AC/ForcePushManager.cs:428); ScriptGlobals.mainCharacter, Character.isDead (FP/ScriptGlobals.cs:17,
    // FP/Character.cs:29).
    public static class Stepper
    {
        public const string Owner = "TIME";

        static readonly GameClockHost host = new GameClockHost();
        static readonly StepClock clock = new StepClock(host);

        internal static StepClock Clock { get { return clock; } }
        internal static GameClockHost Host { get { return host; } }

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("time", "Game clock", PlayShared.Both, Owner,
                "Game clock. The game runs in realtime until the first action call; from then on it is in step mode: frozen between calls, running only inside act, move_to, interact, wait_until and time step. op realtime lets it run (speed scales it). Mines shockwave, lineup and lab lift / water lock run on the music clock: step-mode runs there are paced to real time (the music pauses while frozen), slow motion desyncs them; act / move_to / interact take when to wait for a safe phase inside the call.",
                Schema.Obj().Enum("op", "state, step_mode, realtime, pause, resume, step, speed or rng_seed.", "state",
                        "state", "step_mode", "realtime", "pause", "resume", "step", "speed", "rng_seed")
                    .Int("steps", "op step: logic steps to run (1/60 s each), then freeze.", 1, 1, 600)
                    .Num("speed", "op speed / realtime: game speed while running.", 1, 0.05, 4)
                    .Int("seed", "op rng_seed: PDRandom.seed, RandomGenerator.ResetAll, UnityEngine.Random.seed.", 0, 0, 2147483647)
                    .Enum("observe", "op step: observation in the result.", "concise", "concise", "none").Build(),
                Schema.Obj().Enum("mode", "Clock mode.", null, "step", "realtime").Bool("frozen", "The game is held at time scale 0 now.")
                    .Num("speed", "Realtime speed.", 0, 4).Num("scale", "Time.timeScale.", 0, 100).Num("game_scale", "GameManager.TimeScale.", 0, 100)
                    .Int("capture_fps", "Time.captureFramerate (60 while stepping).", 0, 1000).Int("frame", "Time.frameCount.", 0, long.MaxValue)
                    .Int("step", "LogicManager.stepCount.", 0, long.MaxValue).Num("music_s", "Music position (s).", -1, 1e6)
                    .Bool("loading", "A load is in progress.").Bool("paused", "The game itself is paused (menu, focus).")
                    .Int("steps_run", "op step: steps actually run.", 0, 100000).Arr("warnings", "music_timed, focus, loading ...", Schema.Str(), 0, 20)
                    .Prop("observe", null, Observer.ConciseRef())
                    .Prop("changes", null, ChangeLog.Ref()).BuildOutput()), Handle);
            PlayRegistry.OnTick("stepper", Tick);
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "state");
            if (op == "state") return ToolResult.Ok(ctx.Main(() => State()));
            if (op == "rng_seed")
            {
                int seed = PlayShared.Int(a, "seed", 0);
                return ToolResult.Ok(ctx.Main(() =>
                {
                    Seed(seed);
                    var s = State();
                    s["seed"] = (double)seed;
                    return s;
                }));
            }
            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return busy ?? ToolResult.Fail(McpErr.Busy, "the control lane is busy", "wait for the running action, or call restore");
            if (op == "step") return StepOp(a, ctx);
            double speed = PlayShared.Num(a, "speed", 1);
            return ToolResult.Ok(ctx.Main(() =>
            {
                var w = new List<string>();
                switch (op)
                {
                    case "step_mode": w.AddRange(clock.SetMode(StepMode.Step)); break;
                    case "realtime": w.AddRange(clock.SetMode(StepMode.Realtime)); w.AddRange(clock.SetSpeed(speed)); break;
                    case "pause": w.AddRange(clock.Freeze()); break;
                    case "resume": w.AddRange(clock.Unfreeze()); break;
                    case "speed": w.AddRange(clock.SetSpeed(speed)); break;
                    default: throw new McpException(McpErr.BadArgs, "op: unknown " + op);
                }
                var s = State();
                Merge(s, w);
                return s;
            }));
        }

        static ToolResult StepOp(Json.Obj a, CallContext ctx)
        {
            int steps = PlayShared.Int(a, "steps", 1);
            bool wantObserve = PlayShared.Str(a, "observe", "concise") != "none";
            long mark = TryMark();
            var pre = new List<string>();
            if (clock.ModeChosen && clock.Mode == StepMode.Realtime)
            {
                ctx.MainDo(() => pre.AddRange(clock.SetMode(StepMode.Step)));
                pre.Insert(0, "mode: switched to step mode (time {op:\"realtime\"} goes back)");
            }
            var rr = Run(ctx, new RunRequest { maxSteps = steps, timeoutMs = 50000, stopOnDeath = true, stopOnLoad = true, label = "time step" });
            object obs = null;
            var o = ctx.Main(() =>
            {
                var s = State();
                if (wantObserve) obs = TryConcise("time");
                return s;
            });
            o["steps_run"] = (double)rr.steps;
            o["frames_run"] = (double)rr.frames;
            o["stop"] = rr.stop;
            o["detail"] = rr.detail;
            pre.AddRange(rr.warnings);
            if (wantObserve && obs == null) pre.Add("observe: not available (observe is not built)");
            Merge(o, pre);
            var ch = TrySince(mark);
            if (ch != null) o["changes"] = ch;
            if (obs != null) o["observe"] = obs;
            return ToolResult.Ok(o);
        }

        static void Merge(Json.Obj o, List<string> extra)
        {
            var w = o["warnings"] as List<object>;
            if (w == null) { w = new List<object>(); o["warnings"] = w; }
            foreach (var s in extra) if (s != null && !w.Contains(s)) w.Insert(0, s);
            while (w.Count > 20) w.RemoveAt(w.Count - 1);
        }

        static string Join(List<string> w) { return w == null || w.Count == 0 ? null : string.Join("; ", w.ToArray()); }

        public static StepMode Mode { get { return clock.Mode; } }
        public static double Speed { get { return clock.Speed; } }
        // any thread: held at time scale 0 by the stepper right now
        public static bool Frozen { get { return clock.Frozen; } }
        // any thread: a Run is advancing the game
        public static bool Running { get { return clock.Running; } }
        // any thread: LogicManager.stepCount / Time.frameCount at the last Tick
        public static long Step { get { return clock.Step; } }
        public static long Frame { get { return clock.Frame; } }

        // main thread; returns a warning or null
        public static string SetMode(StepMode mode) { return Join(clock.SetMode(mode)); }
        public static string SetSpeed(double speed) { return Join(clock.SetSpeed(speed)); }
        // main thread: freeze now / drop the freeze (keeps the mode)
        public static void Freeze() { clock.Freeze(); }
        public static void Unfreeze() { clock.Unfreeze(); }

        // worker thread: advance the game until req stops it. Switches to step mode first if no mode was chosen yet in
        // this session. Takes no lane itself: the calling tool holds the control lane (BeginJob) around it.
        public static RunResult Run(CallContext ctx, RunRequest req)
        {
            var rr = StepperCore.RunOn(ctx, clock, req);
            if (host.Recording && clock.Mode == StepMode.Step && !rr.warnings.Contains(RecorderWarning)) rr.warnings.Add(RecorderWarning);
            string paced = host.TakePacedNote();
            if (paced != null && !rr.warnings.Contains(paced)) rr.warnings.Add(paced);
            return rr;
        }

        internal const string RecorderWarning = "recording: a scene recorder owns the frame pacing (captureFramerate / vSync), so step-mode runs follow its clock and can end a step or two late; the time scale is still the stepper's";

        // main thread: input_track playback holds captureFramerate 60 with real-time pacing (a scene recorder, when one
        // samples, owns the pacing instead)
        public static void HoldLockstep(bool on) { host.HoldLockstep(on); }

        // any thread: null, or a focus_lost / focus_recovered note for observe (recoveries are reported for 60 s)
        public static string FocusState() { return FocusGuard.State(); }

        // main thread: the state fields of the time tool
        public static Json.Obj State()
        {
            var o = new Json.Obj();
            o["mode"] = clock.Mode == StepMode.Step ? "step" : "realtime";
            o["frozen"] = clock.Frozen;
            o["speed"] = PlayShared.R(clock.Speed);
            o["scale"] = PlayShared.R(Time.timeScale, 3);
            o["game_scale"] = PlayShared.R(host.GameScale, 3);
            o["capture_fps"] = (double)Time.captureFramerate;
            o["frame"] = (double)Math.Max(0, Time.frameCount);
            o["step"] = (double)Math.Max(0L, LogicManager.stepCount);
            o["music_s"] = MusicPosition();
            bool loading = host.Loading, paused = host.Paused;
            o["loading"] = loading;
            o["paused"] = paused;
            o["running"] = clock.Running;
            o["freeze_pending"] = clock.WantFrozen && !clock.Frozen && !clock.Running;
            o["vsync"] = (double)QualitySettings.vSyncCount;
            o["run_in_background"] = Application.runInBackground;
            o["recording"] = host.Recording;
            o["pacing"] = host.PacingNow ? "real_time_60" : "none";
            var w = new List<object>();
            string reason;
            if (clock.WantFrozen && !clock.Frozen && !clock.Running && !host.CanControl(out reason)) w.Add("freeze_deferred: " + reason + ": the game freezes once it is ready");
            string what = host.MusicTimed();
            if (what != null)
            {
                if (clock.Mode == StepMode.Step || Math.Abs(clock.Speed - 1) > 1e-6) w.Add(StepClock.MusicWarning(what));
                else w.Add("music_timed: " + what + " (realtime at speed 1 keeps it in sync)");
            }
            if (paused) w.Add("paused: the game is paused (menu, message or window focus); no logic steps run");
            string focus = FocusGuard.State();
            if (focus != null) w.Add(focus);
            if (!Application.runInBackground) w.Add("run_in_background: off, the game stops when its window loses focus (the first action turns it on)");
            if (host.External) w.Add("external: a MultiCam capture owns the clock");
            else if (host.Recording) w.Add(RecorderWarning);
            o["warnings"] = w;
            return o;
        }

        // main thread, every frame (registered with OnTick in Register)
        public static void Tick()
        {
            host.Hook();
            host.PollRecorder();
            clock.Tick();
            FocusGuard.Tick(clock.Touched && !host.External);
            host.Pace();
        }

        internal static void Seed(int seed)
        {
            PDRandom.seed = seed;
            RandomGenerator.ResetAll();
            UnityEngine.Random.seed = seed;
        }

        static double MusicPosition()
        {
            try
            {
                var ga = PersistentBehaviour<GlobalAudio>.instance;
                if (ga == null || ga.music == null) return -1;
                return PlayShared.R(ga.music.GetMusicPosition_s());
            }
            catch { return -1; }
        }

        // main thread: Observer.Concise, or null while observe is not built
        internal static object TryConcise(string tool)
        {
            try { return Observer.Concise(tool); }
            catch (NotImplementedException) { return null; }
        }

        // any thread: ChangeLog mark / changes, or -1 / null while ChangeLog is not built
        internal static long TryMark()
        {
            try { return ChangeLog.Mark(); }
            catch (NotImplementedException) { return -1; }
        }

        internal static Json.Obj TrySince(long mark)
        {
            if (mark < 0) return null;
            try { return ChangeLog.Since(mark, 20); }
            catch (NotImplementedException) { return null; }
        }
    }

    // The game behind StepClock (main thread except NowMs). Load phases: loadStarted (or GameManager.IsLoading seen)
    // -> loading; loadFinished -> fading in until the fader is done and GameInput is enabled again (15 s cap), because
    // FadeAndReload's fade-in and the startup title run on scaled time after state = Playing and hang at timeScale 0.
    internal sealed class GameClockHost : IClockHost
    {
        public const int StepFps = 60, LateFadeMs = 15000;

        static readonly System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        static FieldInfo recorderField;
        static bool recorderLooked;
        volatile bool recording;
        bool restoreAfterRecorder;
        volatile int loadPhase;
        double loadFinishedMs;
        bool hooked, steppingSaved, touchSaved, prevAuto, origRunInBg;
        int prevCapture, prevVsyncReal, prevVsyncVirtual, prevTarget = -1;
        bool levelsBuilt = true, perceptionBuilt = true;
        bool runStepping, trackHold, pacedRun, periodSet;
        volatile bool pacing;
        readonly FramePacer pacer = new FramePacer();
        volatile string pacedNote;

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        static extern uint timeBeginPeriod(uint period);
        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        static extern uint timeEndPeriod(uint period);

        public long StepCount { get { return LogicManager.stepCount; } }
        public long FrameCount { get { return Time.frameCount; } }
        public double NowMs { get { return watch.Elapsed.TotalMilliseconds; } }
        public bool Loading { get { try { return GameManager.IsLoading(); } catch { return false; } } }
        public bool Paused { get { try { return GameManager.IsPaused; } catch { return false; } } }
        public bool External { get { try { return MultiCam.Capturing; } catch { return false; } } }
        public float GameScale { get { try { return GameManager.TimeScale; } catch { return Time.timeScale; } } }
        // no load and no post-load fade-in in progress
        public bool Settled { get { string r; CanControl(out r); return loadPhase == 0 && !Loading; } }
        public int LoadPhase { get { return loadPhase; } }

        public bool Dead
        {
            get
            {
                try { var c = ScriptGlobals.mainCharacter; return c != null && c.isDead; }
                catch { return false; }
            }
        }

        public void Hook()
        {
            if (hooked) return;
            hooked = true;
            try
            {
                GameManager.loadStarted += OnLoadStarted;
                GameManager.loadFinished += OnLoadFinished;
            }
            catch (Exception e) { DevLog.Error("[play] stepper load hooks", e); }
        }

        void OnLoadStarted(GameManager.Load load, Savepoint.CharacterType character) { loadPhase = 1; }

        void OnLoadFinished(GameManager.Load load, Savepoint.CharacterType character)
        {
            loadFinishedMs = NowMs;
            loadPhase = 2;
        }

        public bool CanControl(out string reason)
        {
            reason = "";
            GameManager gm = null;
            try { gm = PersistentBehaviour<GameManager>.instance; } catch { }
            if (gm == null) { reason = "no GameManager yet"; return false; }
            if (GameManager.IsLoading()) { if (loadPhase == 0) loadPhase = 1; reason = "loading"; return false; }
            var fader = gm.fader;
            bool faded = fader == null || fader.IsDone;
            if (loadPhase != 0)
            {
                bool late = loadPhase == 2 && NowMs - loadFinishedMs > LateFadeMs;
                if (faded && (InputEnabled() || late)) loadPhase = 0;
                else { reason = "load fading in"; return false; }
            }
            if (!faded) { reason = "fading"; return false; }
            if (TitleShowing()) { reason = "title"; return false; }
            if (levelsBuilt)
            {
                try { if (PlayLevels.Loading) { reason = "loading"; return false; } }
                catch (NotImplementedException) { levelsBuilt = false; }
                catch { }
            }
            return true;
        }

        static bool InputEnabled()
        {
            try { return GameInput.IsEnabled(); } catch { return true; }
        }

        static bool TitleShowing()
        {
            try
            {
                if (TitleManager.IsShowing() || TitleManager.IsPressStartShowing()) return true;
                return PersistentBehaviour<GameEventManager>.instance != null && GameEventManager.IsTitleMenu();
            }
            catch { return false; }
        }

        public void ApplyScale(float scale)
        {
            try { GameManager.TimeScale = scale; } catch { }
            if (scale != 0f && (Paused || Loading)) return;
            Time.timeScale = scale;
        }

        // a SceneRecorder samples now (srec record start, or an mcam record pass); main thread
        public bool Recording { get { return recording; } }

        public void PollRecorder()
        {
            recording = RecorderActive();
            if (restoreAfterRecorder && !recording)
            {
                restoreAfterRecorder = false;
                ApplyPacing();
            }
            else if (trackHold && !recording && !steppingSaved) ApplyPacing();
        }

        static bool RecorderActive()
        {
            if (!recorderLooked)
            {
                recorderLooked = true;
                try
                {
                    var t = typeof(DevLog).Assembly.GetType("InsideDev.SceneRecorder");
                    recorderField = t != null ? t.GetField("active", BindingFlags.Public | BindingFlags.Static) : null;
                }
                catch { recorderField = null; }
            }
            if (recorderField == null) return false;
            try { return recorderField.GetValue(null) != null; } catch { return false; }
        }

        public void BeginStepping()
        {
            if (RecorderActive()) { recording = true; return; }
            runStepping = true;
            pacedRun = MusicTimed() != null;
            ApplyPacing();
        }

        public void EndStepping()
        {
            runStepping = false;
            pacedRun = false;
            if (!steppingSaved) return;
            if (RecorderActive()) { recording = true; restoreAfterRecorder = true; return; }
            ApplyPacing();
        }

        // input_track playback: captureFramerate 60 + real-time pacing until off (a recorder owns it while it samples)
        public void HoldLockstep(bool on)
        {
            trackHold = on;
            if (RecorderActive()) { recording = true; if (!on && steppingSaved) restoreAfterRecorder = true; return; }
            ApplyPacing();
            if (!on && !runStepping) pacedNote = null;
        }

        public bool PacingNow { get { return pacing; } }

        // main thread: the step-mode capture values for the holders (a run, a playback), saved once and put back when
        // neither holds them; pacing for a music-timed run or a playback
        void ApplyPacing()
        {
            if (!runStepping && !trackHold) { RestorePacing(); return; }
            if (!steppingSaved)
            {
                steppingSaved = true;
                prevCapture = Time.captureFramerate;
                prevVsyncReal = QualitySettings.vSyncCount;
                prevVsyncVirtual = prevVsyncReal;
                prevTarget = Application.targetFrameRate;
                prevAuto = false;
                try
                {
                    prevVsyncVirtual = FramerateController.GetVirtualVSyncCount();
                    prevAuto = FramerateController.automatic;
                }
                catch { }
            }
            try
            {
                FramerateController.automatic = false;
                FramerateController.SetVSyncCount(0);
            }
            catch { }
            QualitySettings.vSyncCount = 0;
            Time.captureFramerate = StepFps;
            SetPacing(pacedRun || trackHold);
        }

        void SetPacing(bool on)
        {
            if (on == pacing) return;
            pacing = on;
            pacer.Reset();
            if (on)
            {
                Application.targetFrameRate = -1;
                try { periodSet = timeBeginPeriod(1) == 0; } catch { periodSet = false; }
            }
            else
            {
                Application.targetFrameRate = prevTarget;
                if (periodSet) { try { timeEndPeriod(1); } catch { } periodSet = false; }
            }
        }

        void RestorePacing()
        {
            if (!steppingSaved) return;
            if (pacing && pacer.frames > 1) pacedNote = PacedNote(pacer.frames, pacer.late);
            SetPacing(false);
            steppingSaved = false;
            Time.captureFramerate = prevCapture;
            try
            {
                FramerateController.SetVSyncCount(prevVsyncVirtual);
                FramerateController.automatic = prevAuto;
            }
            catch { }
            QualitySettings.vSyncCount = prevVsyncReal;
            Application.targetFrameRate = prevTarget;
        }

        static string PacedNote(int frames, int late)
        {
            return "paced: music-timed place, the run followed real time at 60 fps (" + frames + " frames" + (late > 0 ? ", " + late + " late: the game rendered below 60 fps, game time fell behind the music" : "") + ")";
        }

        // main thread, every frame (Stepper.Tick): hold the frame to the 60 Hz grid while pacing
        public void Pace()
        {
            if (!pacing || recording || External) return;
            double until = NowMs + pacer.Wait(NowMs);
            while (true)
            {
                double left = until - NowMs;
                if (left <= 0) break;
                if (left > 2.0) System.Threading.Thread.Sleep(1);
                else System.Threading.Thread.SpinWait(64);
            }
        }

        // the pacing note of the last paced run, once (a playback's own result reports its pacing)
        public string TakePacedNote()
        {
            string n = pacedNote;
            pacedNote = null;
            if (n == null && pacing && runStepping) n = PacedNote(pacer.frames, pacer.late);
            return n;
        }

        public void Touch()
        {
            if (!touchSaved)
            {
                touchSaved = true;
                origRunInBg = Application.runInBackground;
            }
            Application.runInBackground = true;
            Restorer.Register("time", "game clock (step mode, pause, speed, capture / vSync, runInBackground)", Stepper.Clock.Reset);
        }

        public string Untouch()
        {
            EndStepping();
            string r = "capture " + Time.captureFramerate + ", vSync " + QualitySettings.vSyncCount;
            if (touchSaved)
            {
                Application.runInBackground = origRunInBg;
                touchSaved = false;
                r += ", runInBackground " + (origRunInBg ? "on" : "off");
            }
            return r;
        }

        public string MusicTimed()
        {
            if (perceptionBuilt)
            {
                try
                {
                    string what;
                    if (Perception.MusicTimedNear(out what)) return string.IsNullOrEmpty(what) ? "music-timed hazard" : what;
                    return null;
                }
                catch (NotImplementedException) { perceptionBuilt = false; }
                catch { return null; }
            }
            try { if (ForcePushManager.getActiveZoneCount() > 0) return "mines shockwave (ForcePushManager)"; } catch { }
            return null;
        }
    }

    // A focus loss before runInBackground was on makes Unity send OnApplicationPause(true): GameEventManager drops its
    // Focus layer and pauses the game (FP/GameEventManager.cs:165-175, 319-333) until the window gets focus back. Once
    // an agent touched the clock, every 10 frames: Focus layer off -> runInBackground on and the private EnableLayer
    // (Layer.Focus) by reflection, which resumes the game unless a menu / message / system layer holds it. State() is the
    // observe note (focus_lost while it cannot recover, focus_recovered for 60 s after a recovery).
    internal static class FocusGuard
    {
        const int Every = 10;
        const double ShowMs = 60000;
        static MethodInfo enableLayer;
        static bool looked, failed;
        static int frames;
        static volatile string lost, recovered;
        static double recoveredAt = -1e9;
        static readonly System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        public static string State()
        {
            if (lost != null) return lost;
            var r = recovered;
            return r != null && watch.Elapsed.TotalMilliseconds - recoveredAt < ShowMs ? r : null;
        }

        public static void Tick(bool agent)
        {
            if (++frames % Every != 0) return;
            try
            {
                if (PersistentBehaviour<GameEventManager>.instance == null) { lost = null; return; }
                bool focus = (GameEventManager.Layers & GameEventManager.Layer.Focus) != 0;
                if (focus) { lost = null; return; }
                if (!agent || failed)
                {
                    lost = "focus_lost: the game paused itself when its window lost focus (GameEventManager Focus layer off)" + (failed ? "; the automatic recovery failed, click the game window once" : "; the next action call recovers it");
                    return;
                }
                if (!Application.runInBackground) Application.runInBackground = true;
                if (!looked)
                {
                    looked = true;
                    enableLayer = typeof(GameEventManager).GetMethod("EnableLayer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(GameEventManager.Layer) }, null);
                }
                if (enableLayer == null) { failed = true; return; }
                enableLayer.Invoke(PersistentBehaviour<GameEventManager>.instance, new object[] { GameEventManager.Layer.Focus });
                lost = null;
                recovered = "focus_recovered: the game had paused itself on focus loss; Play set runInBackground and switched GameEventManager's Focus layer back on";
                recoveredAt = watch.Elapsed.TotalMilliseconds;
                try { ChangeLog.Note("note", recovered); } catch (Exception) { }
            }
            catch (Exception e)
            {
                if (!failed) { failed = true; DevLog.Error("[play] focus recovery", e); }
            }
        }
    }
}
