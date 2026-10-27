using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Replay mode CORE_A (docs/REPLAY_DESIGN.md 2.1, 2.2, 2.3.4, 2.4, 3.4, 4.1, 5, 6.1, 8.3): the replay half of
    // MultiCamRunner: RecordBody/RecordLoop (own preroll, 60 Hz pacing: vsync checked in the preroll, else the own frame
    // limiter; its per-frame assert, cues bound before sample 0, background scans paused, GC and record set prebuilt before
    // sample 0, runner phase assert, own hold with audio.onHold), RecordMarkers on SceneHooks.preUpdate, handover, ReplayBody, PlanPass, CameraPass (BeginPass,
    // warmup along the shot's poses with the TAA reset on the first render, jitter padding counted on Time.frameCount of the
    // first warmup render, EndPass), GoldenPass with noise frames, replay rows, run.json blocks and passes.json. Built for P0
    // (look game, order camera; cams and range work too). Spec events with log false still get their fire time recorded.
    // Output: record -> <out>/record (run artefacts there too); replay/take -> <out> in the v2 take layout; golden ->
    // <out>/golden/<sample:D5>.jpg plus golden/noise/n000|n001|n016|n120.jpg (renders after the 12-render settle) and its
    // own run.json/log.txt; passes -> <out>/passes/ (passes.json, run.json, meta/poses/events/tracks/rig CSVs, no frames).
    // The pure planning math (timeline 5.1 with ramps, sub-frames, warmup times, jitter padding, pass actions, blur 5.3,
    // frame-time stats, limiter grid, pacing check, hitch loss) is MultiCamReplayMath at the end of this file; it uses System types only and is unit-tested
    // offline under .NET 9.
    //
    // CORE_A / CORE_B contract (ARCHITECT, revision 3). Signatures of the contract members in this file are frozen;
    // CORE_A owns their bodies and everything else in this file. CORE_B owns MultiCam.cs and Remote.cs.
    // 1. Commands (CORE_B). "mcam record|replay|take|passes|golden <spec> <out> ..." parse into a ReplayArgs
    //    (replay: cams=a,b and range=t0:t1; golden: optional third positional argument = every). CORE_B loads the plan with
    //    the replay lint of 3.2 (no v2 whole-R error for segments, ReplayConst.CheckRemoved on the parsed JSON, the replay
    //    errors and warnings), refuses on errors, creates the runner exactly as MultiCam.Start does (host GameObject,
    //    DontDestroyOnLoad, AddComponent<MultiCamRunner>, MultiCam.runner), sets runner.replayArgs = args and then calls
    //    runner.Init(plan, mode, outDir, double.NaN, core) with mode = the verb (ReplayModes constant). CORE_A may assume a
    //    lint-clean plan: every ReplayConst enumeration valid, plan.report.ok; spec.replay null = all defaults (CORE_A uses
    //    `spec.replay ?? new ReplaySpec()`).
    // 2. Entry (CORE_B wires, CORE_A implements). MultiCamRunner.Run() pushes ReplayMain() instead of Body() when
    //    ReplayModes.Is(mode). ReplayMain owns the whole run from OpenOutput to the last pass: session push and pop timing,
    //    resolution, pacing, setup, seed, preroll, recorder, replayer, passes. It honours stopRequested like Loop and
    //    reports failures by throwing (Run catches) or by setting `failure`.
    // 3. Status (CORE_B formats, CORE_A fills). CORE_A keeps `replay` current and sets the existing fields `state`, `step`,
    //    `frame`, `capturedSteps`, `currentCam`, `renders`; CORE_B's StatusLine and ProgressJson read `replay` when
    //    ReplayModes.Is(mode) (3.4: recording t, samples, objects, MB, fps, drift ms, audio; replay pass i/n <camera>
    //    step j/m eta).
    // 4. run.json (CORE_B calls, CORE_A fills). WriteRun builds the v2 object and, when ReplayModes.Is(mode), calls
    //    ReplayRunJson(o, final) as its last step before writing; CORE_A sets mode, segments (replay timeline), recording
    //    (ReplayRecordingInfo), passes (ReplayPassInfo list) and its own extra keys.
    // 5. Finish (CORE_B calls, CORE_A implements). Finish calls ReplayFinish(how, kill) inside its own try/catch as its
    //    first action when ReplayModes.Is(mode), before events.Disarm, rig End, MultiCamTargets.End and
    //    MultiCamSession.Pop. ReplayFinish is idempotent and runs on stop, failure, abort, runner destruction and quit:
    //    AudioCapture.Stop first (ForceLevel(0) with readback inside), then SceneReplayer End (game camera put back before
    //    the culler re-hook), SceneRecorder End (flush, footer, record.json), RecordPrep Restore (or the handover release),
    //    hooks off. When `replay.summary` is set, CORE_B uses it as the MultiCam.status text after `how`.
    // 6. Busy gate (CORE_B). During every replay mode MultiCam.Blocks lets through mcam status|stop|abort, srec status,
    //    acap status, game_state, plus the v2 whitelist.
    // 7. Session (CORE_B). MultiCamSession.Push calls AudioCapture.Ensure() when spec.replay != null (6.0 G5). CORE_A sets
    //    the record pacing of 2.3.4 itself after Push (Push saves and Pop restores captureFramerate, fixedDeltaTime,
    //    timeScale, vSyncCount, FramerateController state, targetFrameRate) and the record resolution through
    //    MultiCamSession.SetResolution. TAA reset on warmup and director-run starts: CORE_A writes
    //    MultiCamSession.taa.feedbackMin/feedbackMax = 0 in the coroutine step before that render and restores
    //    MultiCamSession.fbMin/fbMax in the next step (the v2 TaaFrame pattern); MultiCamLook gets no new member and keeps
    //    the v2 _RealTime write in OnPreRender (CORE_A sets MultiCamLook.realTime and MultiCamLook.clock = t0Real + r(T)).
    // 8. Frozen existing API. Every member of MultiCamRunner, MultiCamSession, MultiCamLook, MultiCamGrab,
    //    MultiCamFrameWriter, MultiCamCsv, MultiCamEvents, MultiCamPlan, MultiCamSeg and MultiCam present in
    //    qa/stubs_r3/MultiCam.cs keeps its name and signature: CORE_B may change bodies and add members to every type
    //    except MultiCamRunner, never rename, remove or re-sign. CORE_A may use all of them (e.g. OpenOutput, OpenWriters,
    //    Setup, WaitFor, Wire, ResolveHides, EvaluateAll, ApplyPose, SetHidden, Enqueue, CopySource, Running1, Frozen,
    //    TickFrozen, TickFree, RunCmd, RunCues, Absorb, Log, Warn, Error, Mark, RateError, EventRow, FlushCsv, Progress,
    //    WriteRun, Sees, Frustum and the fields plan, spec, mode, outDir, core, ctx, cam, gameNear, rigs, poses, evaluated,
    //    events, meta, posesCsv, eventsCsv, tracksCsv, rigCsv, writers, grab, tracked, cueDone, step, frame,
    //    capturedSteps, renders, fixedCount, seg, segIndex, t0, loopRealStart, runRealStart, stopRequested, aborted, state,
    //    failure, endReason, currentCam, camDirs, hides, warnings, errors).
    // 9. Names. CORE_B adds no member to MultiCamRunner (it edits bodies of Run, StatusLine, ProgressJson, WriteRun,
    //    Finish only). CORE_A's new runner members live in this file; CORE_A's helper types are nested in MultiCamRunner or
    //    top-level with names starting "MultiCamReplay"; CORE_B's new top-level types never start with "MultiCamReplay".
    //    The types below (ReplayModes, ReplayArgs, ReplayProgress) are part of the contract: frozen shapes.
    // 10. Other owners' contracts CORE_A uses (their stub headers): SceneRecorder (Prepare, Begin with the header CORE_A
    //    fills, SetVirtual, Event, extra, End, RecordPrep.HandOver), SceneReplayer (Load, Begin, BeginPass/EndPass with
    //    `pass`, T, ApplyNow, Info, End), ReplayHub.source for ctx.game (GameCam) and event times, SceneHooks (preUpdate for
    //    RecordMarkers, lateFrame for the runner phase assert), AudioCapture (Preflight, Start, clockBase = header.t0Real,
    //    Marker(label, sample, gameTime, lag), ForceLevel, Stop).

    public static class ReplayModes
    {
        public const string Record = "record", Replay = "replay", Take = "take", Passes = "passes", Golden = "golden";
        public static readonly string[] All = { Record, Replay, Take, Passes, Golden };

        public static bool Is(string mode) { return Array.IndexOf(All, mode) >= 0; }
    }

    internal sealed class ReplayArgs
    {
        public readonly List<string> cams = new List<string>();      // cams=a,b (empty = play.cams, then every enabled camera)
        public bool hasRange;                                        // range=t0:t1 given
        public double rangeFrom, rangeTo;                            // record seconds
        public int every;                                            // golden: grab every n-th sample (0 = record.captureEvery)
    }

    internal sealed class ReplayProgress
    {
        public string phase = "";                                    // setup | record | load | plan | pass | golden | noise | passes | end
        public double t, duration;                                   // record: game seconds since t0 and spec length; replay: T and recording duration
        public int samples, objects;                                 // record pass: SceneRecorder counts
        public long bytes;                                           // record pass: scene.srec bytes so far
        public double fps, driftMs;                                  // record pass: rendered fps over the last second, |r(t) - t| in ms
        public string audio = "";                                    // AudioCapture state ("" = no capture)
        public int pass = -1, passes;                                // replay: current pass (0-based) and pass count
        public string camera = "";                                   // replay: camera of the current pass
        public int stepIndex, steps;                                 // replay: captured step j of m in the current pass (warmups excluded)
        public double eta = double.NaN;                              // seconds left for the whole run, NaN = unknown
        public string summary = "";                                  // one line set by CORE_A when the run ends
    }

    public sealed partial class MultiCamRunner
    {
        internal ReplayArgs replayArgs;                              // set by CORE_B between AddComponent and Init
        internal readonly ReplayProgress replay = new ReplayProgress();

        struct ReplayMarkerJob { public string label; public int sample, frame; public double time; }

        const string ReplayMetaHeader = "step,game_time,rate,group,frame,sub,captured,speed,real_time,fixed_steps,segment";
        const string ReplayEventsHeader = "step,game_time,frame,kind,name,source,detail";
        const string ReplayPosesHeader = "step,camera,x,y,z,pitch,yaw,roll,fov,qx,qy,qz,qw,aim_x,aim_y,aim_z,near,flags,copy_of,subj_x,subj_y,subj_z";
        const string ReplayTracksHeader = "step,game_time,target,ok,x,y,z,vx,vy,vz,count,bmin_x,bmin_y,bmin_z,bmax_x,bmax_y,bmax_z";
        const string ReplayViewsHeader = "step,game_time,camera,source,visible,blocker,block_m,inside,inside_by,cam_x,cam_y,cam_z,subject_x,subject_y,subject_z,path_hit";
        MultiCamCsv viewsCsv;
        double[] viewNext, viewFirstBlocked, viewFirstInside;
        int[] viewChecks, viewBlocked, viewInside, pathHits;
        string[] viewFirstBlocker, viewFirstInsideBy, pathPending, pathFirstBy;
        double[] pathFirst;
        Vector3[] pathPrev;
        bool[] pathHas;
        int[] tlHide = new int[0];
        int planIdx;
        readonly List<HideSet> rpHideSets = new List<HideSet>();
        readonly Dictionary<string, int> rpHideIndex = new Dictionary<string, int>();
        const string ReplayRigHeader = "step,game_time,box,cam_x,cam_y,cam_z,aim_x,aim_y,aim_z,up_x,up_y,up_z,x,y,z,qx,qy,qz,qw,fov,clean_x,clean_y,clean_z,clean_qx,clean_qy,clean_qz,clean_qw,vel_x,vel_y,vel_z,probe_x,probe_y,probe_z,extrap,hand_x,hand_y,hand_z,shake_x,shake_y,shake_z,cut";
        const string ReplayPassesFolder = "passes";
        const int GoldenSettle = 12;
        static readonly int[] GoldenNoise = { 0, 1, 16, 120 };

        readonly WaitForEndOfFrame replayEof = new WaitForEndOfFrame();
        MultiCamAsyncGrab agrab;
        Json.Obj rbJson;
        long rpFrameT0;
        readonly List<double> rpFrameMs = new List<double>();

        string FrameTimeText()
        {
            if (rpFrameMs.Count == 0) return "frame time: no data";
            var f = new List<double>(rpFrameMs);
            f.Sort();
            return "frame time EOF to EOF: median " + f[f.Count / 2].ToString("0.0", IC) + " ms, p90 " + f[f.Count * 9 / 10].ToString("0.0", IC) + " ms, max " + f[f.Count - 1].ToString("0.0", IC) + " ms over " + f.Count + " render(s)";
        }
        ReplaySpec rspec;
        PlaySpec rplay;
        string takeDir = "", recordDir = "", rpSource = "";
        bool rpFresh;

        SceneRecorder recorder;
        RecordPrep recPrep;
        RecordHeader recHeader;
        readonly List<SubsceneCuller> recKept = new List<SubsceneCuller>();
        readonly List<string> recKeptNames = new List<string>();
        bool recHooks, recEnded, recPrepDone, recAudioStarted, recHolding, replayFinished;
        int recWantCapture, recWantVsync, recWantTarget = -1, recRefresh, recPacingChanges, recHitches, recFpsFrames, recGameFrames;
        int recMarkerEvery = 30, recMarkers, recLastMarker = -1, curSample;
        double recLastReal, recFpsT0, recDriftMax, recHoldStart, recDuration;
        string recHoldReason = "", recRunnerPhase = "", recClock = "real", recEndReason = "";
        readonly List<float> recFrameMs = new List<float>(4096), recDtMs = new List<float>(4096), recFrameMsNoGrab = new List<float>(4096), recDtMsNoGrab = new List<float>(4096), recCalMs = new List<float>(256);
        readonly Dictionary<string, Func<string>> recBound = new Dictionary<string, Func<string>>();
        string recPaceMode = "vsync", recCalVerdict = "", recGrade = "pacing";
        double recLimitPeriod = 1.0 / 60.0, recLimitTarget = double.NaN, recCalMedian = double.NaN, recCalWithin = double.NaN, recHitchLoss, recDriftSigned, recCueMsMax, recGcMs = -1;
        int recLimitWaits, recLimitLate, recLimitReanchors, recCuesBound, recCuesUnbound, recCalSkip = 5;
        bool recTimer, recTimerUsed, recQuiet, recPrevShot;
        const double LimiterSlack = 0.0015;
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
        readonly List<object> recHolds = new List<object>();
        readonly List<ReplayMarkerJob> recMarkerQueue = new List<ReplayMarkerJob>();
        readonly HashSet<string> recFired = new HashSet<string>();
        Action<string, string> recOnPosted;
        MultiCamFrameWriter recGameWriter, rpGoldenWriter;
        MultiCamReplayJpegPipe recGamePipe, rpGoldenPipe;
        string recGameEncoder = "", rpGoldenEncoder = "";

        SceneReplayer rp;
        bool rpBegun, rpEnded, rpTaaPending;
        ReplayRecordingInfo recInfo;
        readonly List<ReplayPassInfo> passInfos = new List<ReplayPassInfo>();
        readonly List<object> passStats = new List<object>();
        List<MultiCamReplayEntry> tl;
        CamPose[] tlPoses = new CamPose[0];
        int nWarm, nSteps, nFrames, nCams, outW, outH, rpEtaRenders0;
        long rpEtaTotal;
        float rpEtaStart;
        double tlFrom, tlTo;
        double[] segFrom = new double[0];
        readonly List<int> passCams = new List<int>();
        int[] passOf = new int[0];
        Json.Obj rplanJson, goldenJson;

        // ---------------------------------------------------------------- entry

        IEnumerator ReplayMain()
        {
            rspec = spec.replay ?? new ReplaySpec();
            rplay = rspec.play ?? new PlaySpec();
            if (replayArgs == null) replayArgs = new ReplayArgs();
            takeDir = outDir;
            recordDir = Path.Combine(takeDir, ReplayConst.RecordFolder);
            if (mode == ReplayModes.Record) outDir = recordDir;
            else if (mode == ReplayModes.Golden) outDir = Path.Combine(takeDir, ReplayConst.GoldenFolder);
            else if (mode == ReplayModes.Passes) outDir = Path.Combine(takeDir, ReplayPassesFolder);
            replay.phase = "setup";
            state = "setup";
            ReplayOpenRun();
            MultiCamSession.Push(spec, core);
            WriteRun(false);
            Log(mode + " '" + spec.name + "' from " + plan.path + " -> " + takeDir + (outDir != takeDir ? " (run files in " + outDir + ")" : ""));
            foreach (var w in plan.report.warnings) Log("spec warn: " + w);
            if (mode == ReplayModes.Record || mode == ReplayModes.Take) yield return RecordBody(mode == ReplayModes.Take);
            if (mode == ReplayModes.Golden) yield return GoldenPass();
            else if (mode != ReplayModes.Record && !stopRequested) yield return ReplayBody(mode == ReplayModes.Take, mode == ReplayModes.Passes);
            replay.phase = "end";
        }

        void ReplayOpenRun()
        {
            Directory.CreateDirectory(outDir);
            if (File.Exists(Path.Combine(outDir, "run.json"))) DevLog.Write("[mcam] note: " + outDir + " already holds a run; files are overwritten");
            File.WriteAllText(Path.Combine(outDir, "spec.json"), spec.text);
            logW = new StreamWriter(Path.Combine(outDir, "log.txt"), false, new UTF8Encoding(false));
            logW.AutoFlush = true;
            camDirs = new string[spec.cameras.Count];
            for (int i = 0; i < spec.cameras.Count; i++) camDirs[i] = Path.Combine(takeDir, spec.cameras[i].name);
        }

        IEnumerator ReplayResolution(int w, int h)
        {
            if (Screen.width == w && Screen.height == h) yield break;
            MultiCamSession.SetResolution(w, h);
            float until = Time.realtimeSinceStartup + 6f;
            while ((Screen.width != w || Screen.height != h) && Time.realtimeSinceStartup < until) { yield return null; TickFree(); }
            if (Screen.width != w || Screen.height != h) throw new Exception("resolution " + w + "x" + h + " not applied (screen is " + Screen.width + "x" + Screen.height + ")");
            for (int i = 0; i < 3; i++) { yield return null; TickFree(); }
            Log("resolution " + w + "x" + h);
        }

        void ReplayWire(bool replaying)
        {
            ctx.spec = spec;
            ctx.camera = cam;
            int w = Screen.width, h = Screen.height;
            if (replaying && spec.session.res.x > 0 && spec.session.res.y > 0) { w = (int)spec.session.res.x; h = (int)spec.session.res.y; }
            ctx.screenW = w; ctx.screenH = h;
            ctx.aspect = h > 0 ? w / (float)h : 16f / 9f;
            ctx.fps = spec.timing.fps;
            ctx.target = MultiCamTargets.Sample;
            ctx.poseOf = PoseOf;
            if (replaying || events == null) ctx.eventTime = ReplayEventTime; else ctx.eventTime = events.TimeOf;
            ctx.log = RigLog;
            ctx.preview = false;
            ctx.rehearse = false;
            ctx.planning = false;
            ctx.rate = 60; ctx.speed = 1f;
        }

        double ReplayEventTime(string name)
        {
            var s = rp;
            if (s == null) return double.NaN;
            try { return s.EventTime(name); } catch { return double.NaN; }
        }

        void ReplayRigs()
        {
            foreach (var r in rigs) { try { if (r != null) r.End(); } catch (Exception e) { DevLog.Error("mcam replay rig end", e); } }
            int n = spec.cameras.Count;
            rigs = new ICamRig[n]; poses = new CamPose[n]; evaluated = new bool[n]; visited = new bool[n];
            frameThisStep = new object[n]; lastCopy = new string[n]; hides = new List<Renderer>[n];
            if (cueDone == null || cueDone.Length != spec.cues.Count) cueDone = new bool[spec.cues.Count];
            for (int i = 0; i < n; i++) rigs[i] = MultiCamRig.Create(spec.cameras[i]);
        }

        void ReplayResetCtx()
        {
            ctx.step = -1; ctx.t = 0; ctx.dt = 0; ctx.frame = -1; ctx.sub = -1; ctx.captured = false; ctx.outputTime = 0; ctx.outputDt = 0;
        }

        void ReplayBeginRigs()
        {
            for (int i = 0; i < rigs.Length; i++)
            {
                try { rigs[i].Begin(ctx); }
                catch (Exception e) { RateError("rig begin " + spec.cameras[i].name, e); }
            }
        }

        List<CamPose> ReplayPlanned()
        {
            var planned = new List<CamPose>();
            for (int i = 0; i < poses.Length; i++) if (evaluated[i]) planned.Add(poses[i]);
            ctx.planning = true;
            for (int i = 0; i < rigs.Length; i++)
            {
                try { rigs[i].Plan(ctx, planned); }
                catch (Exception e) { RateError("plan " + spec.cameras[i].name, e); }
            }
            ctx.planning = false;
            return planned;
        }

        // ---------------------------------------------------------------- record pass (2.3, 2.3.4, 6.1)

        IEnumerator RecordBody(bool take)
        {
            var rec = rspec.record ?? new RecordSpec();
            var au = rspec.audio ?? new AudioCapSpec();
            bool sound = au.capture == "record";
            recClock = rec.clock;
            recMarkerEvery = Math.Max(1, au.markerEvery);
            state = "setup"; replay.phase = "setup";
            replay.duration = spec.seconds;
            RecordOpenFiles(take);
            if (sound)
            {
                string why = AudioCapture.Preflight(au);
                if (why != null) throw new Exception("audio capture refused by preflight: " + why);
            }
            Running1();
            yield return Setup();
            if (stopRequested) { endReason = "stop"; yield break; }
            if (spec.seed >= 0) { MultiCam.ApplySeed(spec.seed); EventRow("setup", "seed", "", spec.seed.ToString(IC)); }
            if (rec.res.x > 0 && rec.res.y > 0) yield return ReplayResolution((int)rec.res.x, (int)rec.res.y);
            outW = Screen.width; outH = Screen.height;
            RecordPacing(rec);
            SceneHooks.preUpdate += RecordMarkers;
            recHooks = true;
            SceneHooks.Ensure();
            if (spec.preroll > 0) yield return RecordPreroll();
            if (stopRequested) { endReason = "stop"; yield break; }
            yield return RecordPacingCheck();
            if (stopRequested) { endReason = "stop"; yield break; }
            state = "prepare";
            cam = RenderHost.WorldCam();
            if (cam == null) throw new Exception("no gameplay camera");
            gameNear = cam.nearClipPlane > 0 ? cam.nearClipPlane : MultiCamConst.GameNear;
            events = new MultiCamEvents(spec, plan);
            events.row = RecordEventRow;
            var rep = new SpecReport();
            MultiCamTargets.Begin(spec, rep);
            targetsBegun = true;
            Absorb(rep, "targets");
            if (!rep.ok) throw new Exception("target resolution failed:\n" + rep.Summary(20));
            ReplayWire(false);
            ctx.rate = rec.clock == "fixed" ? Math.Max(1, rec.rate) : 60;
            var rep2 = new SpecReport();
            events.Arm(ctx, rep2, null);
            Absorb(rep2, "events");
            if (!rep2.ok) throw new Exception("event setup failed:\n" + rep2.Summary(20));
            var rep3 = new SpecReport();
            Interpolate(rep3);
            Absorb(rep3, "objects");
            ReplayRigs();
            step = -1;
            ReplayResetCtx();
            MultiCamRig.SampleGame(cam, ctx);
            MultiCamTargets.Step(ctx);
            ReplayBeginRigs();
            EvaluateAll();
            try { tracked = MultiCamTargets.Tracked() ?? new List<TargetRef>(); } catch (Exception e) { Warn("tracked targets: " + e.Message); tracked = new List<TargetRef>(); }
            var planned = ReplayPlanned();
            yield return RecordKeepLoaded(planned, rec);
            if (stopRequested) { endReason = "stop"; yield break; }
            RecordBindCues();
            DevCore.quietBackground = true;
            recQuiet = true;
            var rep4 = new SpecReport();
            recPrep = SceneRecorder.Prepare(rec, planned, ctx.aspect, rep4);
            Absorb(rep4, "record prep");
            if (recPrep == null || !rep4.ok) throw new Exception("record preparation failed:\n" + rep4.Summary(20));
            if (sound)
            {
                string why = AudioCapture.Preflight(au);
                if (why != null) throw new Exception("audio capture refused by preflight: " + why);
            }
            recHeader = RecordMakeHeader(rec);
            if (rec.captureGame)
            {
                yield return RecordGameWriter();
                recGrade = "golden";
                Log("note: record.captureGame grabs every " + Math.Max(1, rec.captureEvery) + "th sample (" + recGameEncoder + "): a golden-reference record, not pacing grade (record.json pacing.grade golden; the nograb_* stats leave the frames after a grab out)");
            }
            RecordCollect();
            var rep7 = new SpecReport();
            SceneRecorder.Prebuild(rec, recordDir, rep7);
            Absorb(rep7, "record set");
            for (int i = 0; i < 3; i++)
            {
                yield return null;
                TickFree();
                RecordPacingAssert(false);
            }
            RecordPhaseCheck();
            recHeader.t0 = Time.timeDbl;
            recHeader.t0Real = Time.realtimeSinceStartupDbl;
            recHeader.stepAtT0 = LogicManager.stepCount;
            var rep5 = new SpecReport();
            recorder = SceneRecorder.Begin(rec, recordDir, recHeader, rep5);
            Absorb(rep5, "recorder");
            if (recorder == null || !rep5.ok) throw new Exception("recorder did not start:\n" + rep5.Summary(20));
            recOnPosted = RecordPosted;
            SceneRecorder.posted += recOnPosted;
            t0 = recHeader.t0;
            loopRealStart = Time.realtimeSinceStartup;
            recLastReal = recHeader.t0Real;
            recFpsT0 = recHeader.t0Real;
            AudioCapture.clockBase = recHeader.t0Real;
            if (sound)
            {
                var rep6 = new SpecReport();
                bool ok = AudioCapture.Start(au, recordDir, rep6);
                Absorb(rep6, "audio");
                if (!ok) throw new Exception("audio capture did not start:\n" + rep6.Summary(10));
                recAudioStarted = true;
            }
            state = "recording"; replay.phase = "record";
            Log("record start: t0 " + recHeader.t0.ToString("0.###", IC) + ", clock " + rec.clock + ", res " + Screen.width + "x" + Screen.height + ", " + recorder.objects + " object(s), " + recKeptNames.Count + " kept culler(s)" + (sound ? ", audio capture on" : ""));
            WriteRun(false);
            yield return RecordLoop(rec, au);
            RecordFinishPass(take);
            if (recGamePipe != null)
            {
                if (agrab != null) yield return agrab.Drain(30f);
                RecordCloseGameWriter(false);
                WriteRun(false);
            }
        }

        IEnumerator RecordLoop(RecordSpec rec, AudioCapSpec au)
        {
            int every = Math.Max(1, rec.captureEvery);
            RecordStep(au, true);
            while (true)
            {
                bool shot = recGameWriter != null && curSample % every == 0;
                if (shot) RecordEvent("game", curSample.ToString("D5", IC), ReplayConst.GameFolder + "/" + curSample.ToString("D5", IC) + ".jpg");
                yield return replayEof;
                if (shot) RecordGameFrame();
                recPrevShot = shot;
                if (stopRequested) { endReason = "stop"; break; }
                if (ctx.t >= spec.seconds - 1e-6) { endReason = "seconds"; break; }
                if (spec.until != null && events.ReachedFired(spec.until, ctx.t)) { endReason = "until " + spec.until; break; }
                if (recAudioStarted && !AudioCapture.capturing) throw new Exception("audio capture stopped during the record (acap watchdog or readback, see log.txt): take aborted");
                RecordPacingAssert(true);
                RecordCues();
                RecordLimit();
                yield return null;
                RecordStep(au, false);
            }
            recEndReason = endReason;
            Log("record loop end (" + endReason + "): " + recorder.samples + " sample(s), game " + ctx.t.ToString("0.###", IC) + " s, drift " + replay.driftMs.ToString("0.#", IC) + " ms (max " + recDriftMax.ToString("0.#", IC) + ", hitch loss " + (recHitchLoss * 1000.0).ToString("0.#", IC) + " ms), " + recPacingChanges + " pacing change(s), " + recHitches + " hitch(es), pacing " + recPaceMode + (recPaceMode == "limiter" ? " (" + recLimitWaits + " waits, " + recLimitLate + " late, " + recLimitReanchors + " re-anchored)" : ""));
        }

        void RecordStep(AudioCapSpec au, bool first)
        {
            double now = Time.realtimeSinceStartupDbl;
            curSample = recorder.samples;
            step = curSample;
            double t = Time.timeDbl - t0;
            if (!first)
            {
                float ms = (float)((now - recLastReal) * 1000.0);
                float dtMs = Time.unscaledDeltaTime * 1000f;
                recFrameMs.Add(ms);
                recDtMs.Add(dtMs);
                if (!recPrevShot) { recFrameMsNoGrab.Add(ms); recDtMsNoGrab.Add(dtMs); }
                if (ms > 100f) recHitches++;
                recFpsFrames++;
                double lost = recClock == "fixed" ? 0.0 : MultiCamReplayMath.HitchLoss(now - recLastReal, t - ctx.t, Time.maximumDeltaTime);
                if (lost > 0)
                {
                    recHitchLoss += lost;
                    RecordEvent("hitch", curSample.ToString(IC), "frame " + ms.ToString("0.#", IC) + " ms, game time lost " + (lost * 1000.0).ToString("0.#", IC) + " ms (maximumDeltaTime clamp)");
                }
            }
            recLastReal = now;
            if (now - recFpsT0 >= 1.0) { replay.fps = recFpsFrames / (now - recFpsT0); recFpsT0 = now; recFpsFrames = 0; }
            ctx.step = curSample;
            ctx.dt = first ? 0f : (float)(t - ctx.t);
            ctx.t = t;
            ctx.frame = -1; ctx.sub = -1; ctx.captured = false; ctx.speed = 1f;
            ctx.outputTime = t; ctx.outputDt = ctx.dt;
            ctx.realTime = now - recHeader.t0Real;
            try { MultiCamRig.SampleGame(cam, ctx); } catch (Exception e) { RateError("SampleGame", e); }
            try { MultiCamTargets.Step(ctx); } catch (Exception e) { RateError("targets step", e); }
            try { events.PollFrame(ctx); events.PollStep(ctx); } catch (Exception e) { RateError("events", e); }
            RecordUnloggedFires();
            try { recorder.SetVirtual(ctx); } catch (Exception e) { RateError("record virtual", e); }
            ReplayRigRow();
            ReplayTrackRows();
            RecordHold(au);
            recDriftSigned = (ctx.realTime - t) * 1000.0;
            double drift = Math.Abs(recDriftSigned);
            replay.driftMs = drift;
            if (drift > recDriftMax) recDriftMax = drift;
            replay.t = t;
            recDuration = t;
            replay.samples = recorder.samples; replay.objects = recorder.objects; replay.bytes = recorder.bytes;
            replay.audio = AudioCapture.capturing ? "capturing" : recAudioStarted ? "stopped" : "";
            replay.eta = Math.Max(0, spec.seconds - t);
            TickFree();
            if ((curSample + 1) % Math.Max(1, spec.output.flushEvery) == 0) FlushCsv();
            Progress(false);
        }

        void RecordGameFrame()
        {
            float r0 = Time.realtimeSinceStartup;
            string path = Path.Combine(Path.Combine(recordDir, ReplayConst.GameFolder), curSample.ToString("D5", IC) + ".jpg");
            if (recGamePipe != null && agrab != null) recGamePipe.Grab(agrab, path);
            else
            {
                object data = grab != null ? grab.Grab() : null;
                if (data != null) recGameWriter.Add(path, data);
            }
            msRead += (Time.realtimeSinceStartup - r0) * 1000.0; nRead++;
            recGameFrames++;
            if (recGameWriter.error != null) throw new Exception("game frame writer: " + recGameWriter.error);
            if (recGamePipe != null && agrab != null && agrab.error != null) throw new Exception("game frame readback: " + agrab.error);
        }

        // record.captureGame grabs (task d): the native async readback plus an ffmpeg JPEG pipe keep ReadPixels and EncodeToJPG
        // (34 ms per grab at 1280x720 in R1) off the game thread; output.readback readpixels, or a missing plugin with auto,
        // keeps the old path.
        IEnumerator RecordGameWriter()
        {
            string dir = Path.Combine(recordDir, ReplayConst.GameFolder);
            Directory.CreateDirectory(dir);
            if (grab != null) { try { grab.Dispose(); } catch { } grab = null; }
            recGamePipe = null;
            var o = spec.output;
            if (o.readback != "readpixels")
            {
                var g = new MultiCamAsyncGrab();
                yield return g.Open(Screen.width, Screen.height);
                string why = g.ready ? null : g.error;
                if (g.ready)
                {
                    var jp = MultiCamReplayJpegPipe.Open("game", dir, g.w, g.h, g.pixFmt, o, out why);
                    if (jp != null) { agrab = g; recGamePipe = jp; recGameWriter = jp.writer; recGameEncoder = jp.encoder; Log("game frames: " + jp.encoder + ": " + o.ffmpeg + " " + jp.args); yield break; }
                    g.Close();
                }
                if (o.readback == "native") throw new Exception("record.captureGame with output.readback native: " + why);
                Warn("record.captureGame: native readback not used (" + why + "), ReadPixels + EncodeToJPG on the game thread");
            }
            grab = new MultiCamGrab(Screen.width, Screen.height, true, Mathf.Clamp(o.jpeg, 1, 100));
            recGameWriter = new MultiCamFrameWriter("game", Math.Max(1, o.queue));
            recGameEncoder = "Unity EncodeToJPG " + Mathf.Clamp(o.jpeg, 1, 100) + " (ReadPixels on the game thread)";
        }

        void RecordCloseGameWriter(bool kill)
        {
            var w = recGameWriter;
            if (w == null) return;
            recGameWriter = null;
            w.Close(kill, 30000);
            if (w.error != null) errors.Add("game frame writer: " + w.error);
            var p = recGamePipe;
            if (p == null) { Log("game frames: " + w.written + " written"); return; }
            recGamePipe = null;
            if (agrab != null)
            {
                if (agrab.error != null) errors.Add("game frame readback: " + agrab.error);
                try { Log("game frames: " + agrab.StatsLine()); } catch { }
                agrab.Close();
                agrab = null;
            }
            string err;
            int n = p.Finish(out err);
            if (err != null) errors.Add("game frames: " + err);
            Log("game frames: " + n + " of " + p.targets.Count + " JPEG(s) written by ffmpeg" + (err != null ? " (" + err + ")" : ""));
        }

        void RecordHold(AudioCapSpec au)
        {
            bool paused = false, loading = false;
            try { paused = GameManager.IsPaused; loading = spec.session.holdWhileLoading && GameManager.IsLoading(); } catch { }
            bool held = paused || loading;
            double now = Time.realtimeSinceStartupDbl;
            if (held && !recHolding)
            {
                recHolding = true;
                recHoldStart = now;
                recHoldReason = paused ? "game paused" : "game loading";
                Mark("hold", recHoldReason + " at sample " + curSample);
                RecordEvent("hold", "start", recHoldReason);
                if (AudioCapture.capturing)
                {
                    if (au.onHold != "mark") throw new Exception("hold during the audio capture (" + recHoldReason + "): take aborted (audio.onHold abort)");
                    RecordQueueMarker("hold" + recHolds.Count.ToString(IC));
                }
            }
            else if (!held && recHolding)
            {
                recHolding = false;
                var h = new Json.Obj();
                h["start"] = recHoldStart - recHeader.t0Real;
                h["duration"] = now - recHoldStart;
                h["reason"] = recHoldReason;
                h["sample"] = (double)curSample;
                recHolds.Add(h);
                Mark("hold", "released after " + (now - recHoldStart).ToString("0.00", IC) + " s");
                RecordEvent("hold", "end", recHoldReason);
                if (AudioCapture.capturing) RecordQueueMarker("resume" + (recHolds.Count - 1).ToString(IC));
            }
            if (RenderHost.focused != focusWas) { focusWas = RenderHost.focused; Mark("focus", focusWas ? "regained" : "lost (record continues)"); }
        }

        void RecordCues()
        {
            List<int> due = null;
            for (int i = 0; i < spec.cues.Count; i++)
            {
                if (cueDone[i]) continue;
                double at = ctx.TimeOf(spec.cues[i].at, 0.0);
                if (double.IsNaN(at) || ctx.t < at - 1e-6) continue;
                if (due == null) due = new List<int>();
                due.Add(i);
            }
            if (due == null) return;
            due.Sort((x, y) => ctx.TimeOf(spec.cues[x].at, 0.0).CompareTo(ctx.TimeOf(spec.cues[y].at, 0.0)));
            foreach (int i in due)
            {
                cueDone[i] = true;
                var q = spec.cues[i];
                foreach (var cmd in q.Commands())
                {
                    long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    Func<string> bound;
                    string res = recBound.TryGetValue(cmd, out bound) ? RecordInvoke(bound) : null;
                    bool direct = res != null;
                    if (!direct) res = RunCmd(cmd);
                    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - c0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (ms > recCueMsMax) recCueMsMax = ms;
                    string how = (direct ? "bound" : "bridge") + ", " + ms.ToString("0.##", IC) + " ms";
                    EventRow("cue", q.label, cmd, res + " (" + how + ")");
                    RecordEvent("cue", q.label, cmd + " -> " + res + " (" + how + ")");
                    Log("cue sample " + curSample + " t " + ctx.t.ToString("0.###", IC) + (q.label.Length > 0 ? " '" + q.label + "'" : "") + ": " + cmd + " -> " + res + " (" + how + ")");
                    if (ms > 4.0) Warn("cue '" + cmd + "' took " + ms.ToString("0.#", IC) + " ms inside a recorded frame (" + (direct ? "the bound call itself" : "bridge path, object resolve") + ")");
                }
            }
        }

        // Cue targets are resolved once before sample 0 (Remote.Bind: object, component and method of a `call`), so the
        // bridge's scene walk and its synchronous object database refresh never run inside a recorded frame (R1: 0.3-1.5 s).
        // Unbound commands (other verbs, objects that do not exist yet) still run through the bridge, with a warning.
        void RecordBindCues()
        {
            recBound.Clear();
            recCuesBound = 0; recCuesUnbound = 0;
            foreach (var q in spec.cues)
                foreach (var cmd in q.Commands())
                {
                    if (string.IsNullOrEmpty(cmd) || recBound.ContainsKey(cmd)) continue;
                    string how;
                    Func<string> f = null;
                    try { f = Remote.Bind(cmd, out how); }
                    catch (Exception e) { how = "bind failed: " + e.Message; }
                    if (f != null) { recBound[cmd] = f; recCuesBound++; Log("cue bound before sample 0: " + cmd + " (" + how + ")"); }
                    else { recCuesUnbound++; Warn("cue '" + cmd + "' is not pre-bound (" + how + "): it runs through the bridge inside a recorded frame and may stall it"); }
                }
        }

        static string RecordInvoke(Func<string> f)
        {
            try { return f(); }
            catch (TargetInvocationException e) { var x = e.InnerException ?? e; return "ERROR " + x.GetType().Name + ": " + x.Message; }
            catch (Exception e) { return "ERROR " + e.GetType().Name + ": " + e.Message; }
        }

        void RecordCollect()
        {
            long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int g0 = GC.CollectionCount(0);
            try { GC.Collect(); } catch (Exception e) { Warn("GC before sample 0: " + e.Message); return; }
            recGcMs = (System.Diagnostics.Stopwatch.GetTimestamp() - c0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Log("GC before sample 0: " + recGcMs.ToString("0", IC) + " ms (collections " + g0 + " -> " + GC.CollectionCount(0) + ", heap " + (GC.GetTotalMemory(false) / 1048576.0).ToString("0", IC) + " MB); background scanners paused until the record ends");
        }

        void RecordEventRow(string kind, string name, string source, string detail)
        {
            EventRow(kind, name, source, detail);
            if (kind == "signal" || kind == "fsm" || kind == "sound") return;
            if (kind == "event" && name != null && !recFired.Add(name)) return;
            RecordEvent(kind, name, string.IsNullOrEmpty(source) ? detail : source + ": " + detail);
            if (kind == "event" && AudioCapture.capturing) RecordQueueMarker("e" + name);
        }

        // SceneRecorder.PostEvent already wrote the kind "event" row of a post: a spec event of type "posted" with the same name
        // must not write a second one; the post gets the audio marker of an event.
        void RecordPosted(string name, string detail)
        {
            if (recorder == null || recEnded || string.IsNullOrEmpty(name) || !recFired.Add(name)) return;
            if (AudioCapture.capturing) RecordQueueMarker("e" + name);
        }

        void RecordUnloggedFires()
        {
            foreach (var e in spec.events)
            {
                if (e.log || string.IsNullOrEmpty(e.name) || recFired.Contains(e.name)) continue;
                bool fired;
                try { fired = events.HasFired(e.name); } catch { fired = false; }
                if (!fired) continue;
                recFired.Add(e.name);
                RecordEvent("event", e.name, "fired (log off)");
                if (AudioCapture.capturing) RecordQueueMarker("e" + e.name);
            }
        }

        void RecordEvent(string kind, string name, string detail)
        {
            if (recorder == null || recEnded) return;
            try { recorder.Event(kind ?? "", name ?? "", detail ?? ""); } catch (Exception e) { RateError("record event", e); }
        }

        void RecordQueueMarker(string label)
        {
            recMarkerQueue.Add(new ReplayMarkerJob { label = label, sample = curSample, frame = Time.frameCount, time = ctx.t });
        }

        void RecordMarkers()
        {
            var r = recorder;
            if (r == null || recEnded || !AudioCapture.capturing) { recMarkerQueue.Clear(); return; }
            int n = r.samples;
            if (n != recLastMarker && (recMarkers == 0 || n % recMarkerEvery == 0))
            {
                string label = "s" + n.ToString(IC);
                AudioCapture.Marker(label, n, Time.timeDbl - t0, 0);
                RecordEvent("marker", label, "lag 0");
                recLastMarker = n;
                recMarkers++;
            }
            for (int i = 0; i < recMarkerQueue.Count; i++)
            {
                var m = recMarkerQueue[i];
                int lag = Math.Max(1, Time.frameCount - m.frame);
                AudioCapture.Marker(m.label, m.sample, m.time, lag);
                RecordEvent("marker", m.label, "sample " + m.sample.ToString(IC) + " lag " + lag.ToString(IC));
                recMarkers++;
            }
            recMarkerQueue.Clear();
        }

        void RecordPacing(RecordSpec rec)
        {
            recRefresh = 0;
            try { recRefresh = Screen.currentResolution.refreshRate; } catch { }
            try { FramerateController.automatic = false; } catch { }
            if (rec.clock == "fixed")
            {
                int r = rec.rate > 0 ? rec.rate : 60;
                recWantCapture = r;
                RecordUseLimiter(r, "clock fixed");
            }
            else
            {
                recWantCapture = 0;
                if (recRefresh == 60) { recPaceMode = "vsync"; recWantVsync = 1; recWantTarget = -1; }
                else RecordUseLimiter(60, "display reports " + recRefresh + " Hz");
            }
            RecordApplyPacing();
            Log("record pacing: clock " + rec.clock + ", " + recPaceMode + ", captureFramerate " + recWantCapture + ", vSyncCount " + recWantVsync + ", targetFrameRate " + recWantTarget + ", display " + recRefresh + " Hz");
        }

        // 2.3.4 as measured in R1: vSyncCount 1 followed the 75 Hz monitor of a hybrid-GPU laptop (median 13-14 ms) although
        // Screen.currentResolution said 60 Hz, and targetFrameRate 60 ran at 18.1 ms. The limiter (vSyncCount 0,
        // targetFrameRate -1, timeBeginPeriod(1)) waits at the end of every record frame for the next 1/rate boundary of
        // a fixed grid: Sleep(1) while more than 2.5 ms are left, then a Stopwatch spin. A frame later than the boundary by up
        // to 1.5 ms keeps the grid (the next wait is shorter), a later one re-anchors the grid at once.
        void RecordUseLimiter(int rate, string why)
        {
            recPaceMode = "limiter";
            recWantVsync = 0; recWantTarget = -1;
            recLimitPeriod = 1.0 / Math.Max(1, rate);
            recLimitTarget = double.NaN;
            if (!recTimer) { try { recTimer = timeBeginPeriod(1) == 0; } catch { recTimer = false; } }
            if (recTimer) recTimerUsed = true;
            Log("record pacing: own frame limiter at " + rate + " Hz (" + why + "), vSyncCount 0, targetFrameRate -1, timeBeginPeriod(1) " + (recTimer ? "on" : "failed"));
        }

        static double RecordNow() { return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency; }

        void RecordLimit()
        {
            if (recPaceMode != "limiter") return;
            int kind;
            recLimitTarget = MultiCamReplayMath.LimiterTarget(recLimitTarget, RecordNow(), recLimitPeriod, LimiterSlack, out kind);
            if (kind == MultiCamReplayMath.LimitLate) { recLimitLate++; return; }
            if (kind == MultiCamReplayMath.LimitAnchor) { recLimitReanchors++; return; }
            recLimitWaits++;
            double target = recLimitTarget;
            while (true)
            {
                double left = target - RecordNow();
                if (left <= 0) break;
                if (left > 0.0025) System.Threading.Thread.Sleep(1);
            }
        }

        void RecordReleaseTimer()
        {
            if (!recTimer) return;
            recTimer = false;
            try { timeEndPeriod(1); } catch { }
        }

        // Checks during the real-time preroll that vSyncCount 1 really paces at 60 Hz (engine frame interval median within
        // 0.5 ms of 16.67 ms and 90 % of the frames in 15-18.5 ms, over 40 frames after the first 5); else the record uses
        // the limiter. Extra frames run only when the preroll was too short for 40 intervals.
        IEnumerator RecordPacingCheck()
        {
            if (recPaceMode != "vsync") yield break;
            int n = 0;
            while (recCalMs.Count < 40 && n < 240 && !stopRequested)
            {
                yield return null;
                TickFree();
                RecordPacingAssert(false);
                RecordCalibrate();
                n++;
            }
            double med, within;
            bool ok = MultiCamReplayMath.PacingHolds(recCalMs, 1000.0 / 60.0, 15.0, 18.5, out med, out within);
            recCalMedian = med; recCalWithin = within;
            recCalVerdict = ok ? "vsync holds 60 Hz" : "vsync does not hold 60 Hz";
            Log("pacing check: " + recCalMs.Count + " frame(s), engine interval median " + med.ToString("0.00", IC) + " ms, " + (within * 100).ToString("0", IC) + " % in 15-18.5 ms" + (n > 0 ? " (" + n + " extra preroll frame(s))" : "") + ": " + recCalVerdict);
            if (ok) yield break;
            RecordUseLimiter(60, "vSyncCount 1 measured " + med.ToString("0.0", IC) + " ms");
            RecordApplyPacing();
        }

        void RecordCalibrate()
        {
            if (recPaceMode != "vsync" || Time.captureFramerate != 0) return;
            if (recCalSkip > 0) { recCalSkip--; return; }
            if (recCalMs.Count < 240) recCalMs.Add(Time.unscaledDeltaTime * 1000f);
        }

        void RecordApplyPacing()
        {
            Time.captureFramerate = recWantCapture;
            try { FramerateController.SetVSyncCount(recWantVsync); } catch { }
            if (QualitySettings.vSyncCount != recWantVsync) QualitySettings.vSyncCount = recWantVsync;
            if (Application.targetFrameRate != recWantTarget) Application.targetFrameRate = recWantTarget;
        }

        void RecordPacingAssert(bool count)
        {
            int cf = Time.captureFramerate, vs = QualitySettings.vSyncCount, tf = Application.targetFrameRate;
            if (cf == recWantCapture && vs == recWantVsync && tf == recWantTarget) return;
            RecordApplyPacing();
            if (!count) return;
            recPacingChanges++;
            string d = "captureFramerate " + cf + ", vSyncCount " + vs + ", targetFrameRate " + tf + " at sample " + curSample + " -> restored " + recWantCapture + "/" + recWantVsync + "/" + recWantTarget;
            EventRow("pacing", "", "", d);
            RecordEvent("pacing", "", d);
            if (recPacingChanges <= 5 || recPacingChanges % 100 == 0) Log("pacing changed (" + recPacingChanges + "): " + d);
        }

        IEnumerator RecordPreroll()
        {
            state = "preroll";
            double start = Time.timeDbl;
            int n = 0, max = (int)(spec.preroll * 240) + 240;
            while (Time.timeDbl - start < spec.preroll - 1e-6 && !stopRequested && n < max)
            {
                yield return null;
                TickFree();
                RecordPacingAssert(false);
                RecordCalibrate();
                n++;
            }
            Log("preroll (real time, not recorded) " + (Time.timeDbl - start).ToString("0.###", IC) + " s in " + n + " frames");
        }

        IEnumerator RecordKeepLoaded(List<CamPose> planned, RecordSpec rec)
        {
            var st = spec.stream;
            recKeptNames.Clear();
            bool recKeep = rec != null && rec.keep != null && rec.keep.Count > 0;
            if (st.keepLoaded == "none" && !recKeep) yield break;
            state = "keep-loaded";
            var mgr = SubsceneCullingManager.instance;
            var want = new List<SubsceneCuller>();
            if (st.keepLoaded != "none")
                foreach (var name in st.cullers)
                {
                    var c = mgr.GetSubsceneCullerByName(name);
                    if (c == null) Warn("keep-loaded: no culler named " + name); else if (!want.Contains(c)) want.Add(c);
                }
            if (recKeep)
                foreach (var name in rec.keep)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    var c = SceneRecorder.FindCuller(name);
                    if (c == null) Warn("replay.record.keep: no subscene culler named " + name); else if (!want.Contains(c)) want.Add(c);
                }
            if (st.keepLoaded == "auto" && st.prepass)
            {
                foreach (var c in mgr.GetAllSubsceneCullers())
                {
                    if (c == null || want.Contains(c)) continue;
                    var b = c.CullingBounds;
                    foreach (var p in planned) if (Sees(p, b)) { want.Add(c); break; }
                }
            }
            foreach (var c in want)
            {
                try { recKeptNames.Add(c.SceneName); } catch { }
                if (MultiCamSession.IsKept(c)) continue;
                mgr.AddAlwaysLoaded(c);
                recKept.Add(c);
            }
            if (want.Count == 0) yield break;
            Log("keep-loaded (record): " + want.Count + " culler(s) in view of " + planned.Count + " planned pose(s), " + recKept.Count + " handle(s) owned by the record");
            float until = Time.realtimeSinceStartup + Math.Max(1f, st.timeout);
            while (Time.realtimeSinceStartup < until && !stopRequested)
            {
                bool done = mgr.AllCullersFinished();
                if (done) foreach (var c in want) if (c != null && !c.IsActive) { done = false; break; }
                if (done) break;
                yield return null;
                TickFree();
                RecordPacingAssert(false);
            }
            int missing = 0;
            foreach (var c in want) if (c != null && !c.IsActive) missing++;
            if (missing > 0) Warn("keep-loaded: " + missing + " culler(s) not active after " + st.timeout.ToString("0", IC) + " s; pop-in possible");
            int settle = rec != null ? rec.settleFrames : 0;
            for (int i = 0; i < settle && !stopRequested; i++)
            {
                yield return null;
                TickFree();
                RecordPacingAssert(false);
            }
            if (settle > 0) Log("keep-loaded (record): settled " + settle + " frame(s) after the kept subscenes were active (record.settleFrames, late-awake content)");
        }

        RecordHeader RecordMakeHeader(RecordSpec rec)
        {
            var h = new RecordHeader();
            h.created = DateTime.Now.ToString("s", IC);
            h.spec = plan.path;
            h.clock = rec.clock;
            int sub, sp;
            if (G.CurrentSavepoint(out sub, out sp)) { h.subscene = sub; h.savepoint = sp; h.level = G.SubsceneLabel(sub); }
            else { try { h.level = Application.loadedLevelName; } catch { } }
            h.captureFramerate = Time.captureFramerate;
            h.streamVersion = Levels.StreamVersion;
            h.screenW = Screen.width; h.screenH = Screen.height;
            h.fixedDeltaTime = Time.fixedDeltaTime;
            h.gameFov = cam != null ? cam.fieldOfView : MultiCamConst.GameFov;
            try { foreach (var c in SubsceneCullingManager.instance.GetAllSubsceneCullers()) if (c != null && c.IsActive) h.subscenes.Add(c.SceneName); } catch (Exception e) { Warn("header subscenes: " + e.Message); }
            h.keptCullers.AddRange(recKeptNames);
            h.posEps = rec.posEps; h.rotEps = rec.rotEps; h.cut = rec.cut;
            h.vSyncCount = QualitySettings.vSyncCount; h.targetFrameRate = Application.targetFrameRate; h.refreshRate = recRefresh;
            return h;
        }

        void RecordPhaseCheck()
        {
            int lf = SceneHooks.lateFrame, fc = Time.frameCount;
            recRunnerPhase = lf == fc ? "after LateUpdate" : "lateFrame " + lf + " at frameCount " + fc + " (not after LateUpdate)";
            Log("runner phase: " + recRunnerPhase);
            if (lf != fc) throw new Exception("runner phase check failed: the record step resumed with " + recRunnerPhase + "; it must run after LateUpdate (REPLAY_DESIGN 2.2)");
        }

        void RecordOpenFiles(bool take)
        {
            Directory.CreateDirectory(recordDir);
            if (take) File.WriteAllText(Path.Combine(recordDir, "spec.json"), spec.text);
            eventsCsv = new MultiCamCsv(Path.Combine(recordDir, "events.csv"), ReplayEventsHeader);
            rigCsv = new MultiCamCsv(Path.Combine(recordDir, "rig.csv"), ReplayRigHeader);
            if (spec.output.tracks) tracksCsv = new MultiCamCsv(Path.Combine(recordDir, "tracks.csv"), ReplayTracksHeader);
        }

        void RecordCloseFiles(bool take)
        {
            FlushCsv();
            if (!take) return;
            foreach (var c in new[] { eventsCsv, rigCsv, tracksCsv }) { try { if (c != null) c.Close(); } catch { } }
            eventsCsv = null; rigCsv = null; tracksCsv = null;
            try
            {
                using (var src = new FileStream(Path.Combine(outDir, "log.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dst = new FileStream(Path.Combine(recordDir, "log.txt"), FileMode.Create, FileAccess.Write))
                {
                    var buf = new byte[65536];
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
                }
            }
            catch (Exception e) { Warn("record log copy: " + e.Message); }
        }

        void RecordFinishPass(bool take)
        {
            state = "record end";
            if (recAudioStarted) AudioCapture.Stop("mcam " + mode + ": record end");
            replay.audio = recAudioStarted ? "stopped" : "";
            Frozen();
            if (events != null) { try { events.Disarm(); } catch (Exception e) { DevLog.Error("mcam record events", e); } }
            RecordUnhook();
            if (recGamePipe == null) RecordCloseGameWriter(false);
            RecordEndRecorder();
            if (!take) RecordRestore();
            RecordCloseFiles(take);
            WriteRun(false);
        }

        void RecordEndRecorder()
        {
            var r = recorder;
            if (r == null || recEnded) return;
            recEnded = true;
            var x = r.extra;
            x["pacing"] = RecordPacingJson();
            x["runnerPhase"] = recRunnerPhase;
            x["holds"] = new List<object>(recHolds);
            x["audio"] = RecordAudioJson();
            x["drift"] = RecordDriftJson();
            x["cues"] = RecordCuesJson();
            x["gcBeforeSample0Ms"] = recGcMs;
            x["end_reason"] = recEndReason;
            x["game_frames"] = (double)recGameFrames;
            x["game_frames_encoder"] = recGameEncoder;
            x["take"] = takeDir;
            var rep = new SpecReport();
            try { r.End(rep); }
            finally { Absorb(rep, "record end"); }
            replay.samples = r.samples; replay.objects = r.objects; replay.bytes = r.bytes;
            Log("recording written to " + recordDir + ": " + r.samples + " sample(s), " + r.objects + " object(s), " + (r.bytes / 1e6).ToString("0.##", IC) + " MB");
        }

        void RecordRestore()
        {
            if (recPrep != null && !recPrepDone)
            {
                recPrepDone = true;
                try { recPrep.Restore(); } catch (Exception e) { DevLog.Error("mcam record prep restore", e); }
            }
            if (recKept.Count == 0) return;
            try
            {
                var mgr = SubsceneCullingManager.instance;
                foreach (var c in recKept) if (c != null) mgr.RemoveAlwaysLoaded(c);
            }
            catch (Exception e) { DevLog.Error("mcam record keep-loaded release", e); }
            Log("keep-loaded (record): " + recKept.Count + " handle(s) released");
            recKept.Clear();
        }

        void RecordUnhook()
        {
            RecordReleaseTimer();
            if (recOnPosted != null) { SceneRecorder.posted -= recOnPosted; recOnPosted = null; }
            if (recQuiet) { recQuiet = false; DevCore.quietBackground = false; }
            if (!recHooks) return;
            recHooks = false;
            SceneHooks.preUpdate -= RecordMarkers;
            try { SceneHooks.Release(); } catch (Exception e) { DevLog.Error("mcam record hooks", e); }
        }

        Json.Obj RecordPacingJson()
        {
            var p = new Json.Obj();
            p["clock"] = recClock;
            p["captureFramerate"] = (double)recWantCapture; p["vSyncCount"] = (double)recWantVsync; p["targetFrameRate"] = (double)recWantTarget; p["refreshRate"] = (double)recRefresh;
            p["changes"] = (double)recPacingChanges;
            var s = MultiCamReplayMath.FrameStats(recFrameMs, 15.0, 18.5);
            p["frames"] = s[0];
            p["frame_ms_mean"] = MultiCamPlan.Num(s[1]); p["frame_ms_p99"] = MultiCamPlan.Num(s[2]); p["frame_ms_max"] = MultiCamPlan.Num(s[3]);
            p["within_15_18_5"] = MultiCamPlan.Num(s[4]);
            p["hitches"] = (double)recHitches;
            p["mode"] = recPaceMode;
            p["grade"] = recGrade;
            var d = MultiCamReplayMath.FrameStats(recDtMs, 15.0, 18.5);
            p["dt_frames"] = d[0]; p["dt_ms_mean"] = MultiCamPlan.Num(d[1]); p["dt_ms_p99"] = MultiCamPlan.Num(d[2]); p["dt_ms_max"] = MultiCamPlan.Num(d[3]);
            p["dt_within_15_18_5"] = MultiCamPlan.Num(d[4]);
            var g = MultiCamReplayMath.FrameStats(recFrameMsNoGrab, 15.0, 18.5);
            var gd = MultiCamReplayMath.FrameStats(recDtMsNoGrab, 15.0, 18.5);
            p["nograb_frames"] = g[0]; p["nograb_ms_mean"] = MultiCamPlan.Num(g[1]); p["nograb_within_15_18_5"] = MultiCamPlan.Num(g[4]);
            p["nograb_dt_ms_mean"] = MultiCamPlan.Num(gd[1]); p["nograb_dt_within_15_18_5"] = MultiCamPlan.Num(gd[4]);
            p["note"] = "frame_ms: record step to record step (phase 9); dt_ms: Time.unscaledDeltaTime, the engine frame interval game logic sees; nograb_*: without the frames after a captureGame grab";
            var lim = new Json.Obj();
            lim["period_ms"] = recLimitPeriod * 1000.0; lim["waits"] = (double)recLimitWaits; lim["late"] = (double)recLimitLate; lim["reanchors"] = (double)recLimitReanchors;
            lim["slack_ms"] = LimiterSlack * 1000.0; lim["timeBeginPeriod1"] = recTimerUsed;
            p["limiter"] = lim;
            var cal = new Json.Obj();
            cal["frames"] = (double)recCalMs.Count; cal["median_ms"] = MultiCamPlan.Num(recCalMedian); cal["within_15_18_5"] = MultiCamPlan.Num(recCalWithin); cal["verdict"] = recCalVerdict;
            p["calibration"] = cal;
            return p;
        }

        Json.Obj RecordDriftJson()
        {
            var d = new Json.Obj();
            d["final_ms"] = replay.driftMs; d["max_ms"] = recDriftMax;
            d["final_signed_ms"] = recDriftSigned;
            d["hitch_loss_ms"] = recHitchLoss * 1000.0;
            d["residual_ms"] = recDriftSigned - recHitchLoss * 1000.0;
            d["note"] = "final_signed = r(t) - t at the end; hitch_loss = game time lost to the maximumDeltaTime clamp (hitch events; mcam_audio track cuts these gaps); residual = final_signed - hitch_loss";
            return d;
        }

        Json.Obj RecordCuesJson()
        {
            var c = new Json.Obj();
            c["bound"] = (double)recCuesBound; c["unbound"] = (double)recCuesUnbound; c["max_ms"] = recCueMsMax;
            return c;
        }

        Json.Obj RecordAudioJson()
        {
            var au = rspec != null && rspec.audio != null ? rspec.audio : new AudioCapSpec();
            var a = new Json.Obj();
            a["capture"] = au.capture; a["started"] = recAudioStarted; a["markers"] = (double)recMarkers; a["markerEvery"] = (double)recMarkerEvery;
            a["onHold"] = au.onHold; a["suspend"] = au.suspend; a["level"] = (double)au.level;
            a["usable"] = null;
            return a;
        }

        Json.Obj RecordJson()
        {
            var r = new Json.Obj();
            r["dir"] = recordDir;
            r["clock"] = recClock;
            if (recorder != null) { r["samples"] = (double)recorder.samples; r["objects"] = (double)recorder.objects; r["bytes"] = (double)recorder.bytes; r["event_times"] = recorder.EventTimesJson(); }
            r["duration"] = recDuration;
            r["end_reason"] = recEndReason;
            r["ended"] = recEnded;
            r["runnerPhase"] = recRunnerPhase;
            r["pacing"] = RecordPacingJson();
            r["holds"] = new List<object>(recHolds);
            r["audio"] = RecordAudioJson();
            r["drift_ms"] = replay.driftMs; r["drift_max_ms"] = recDriftMax;
            r["drift"] = RecordDriftJson();
            r["cues"] = RecordCuesJson();
            r["kept_cullers"] = MultiCamPlan.Strs(recKeptNames);
            r["game_frames"] = (double)recGameFrames;
            r["game_frames_encoder"] = recGameEncoder;
            if (recHeader != null) r["header"] = ReplayJson(recHeader);
            return r;
        }

        // ---------------------------------------------------------------- replay passes (2.4, 5)

        IEnumerator ReplayBody(bool afterRecord, bool planOnly)
        {
            state = "load"; replay.phase = "load";
            string src = afterRecord ? recordDir : ReplaySource();
            rpSource = src;
            ReplayOpenFiles();
            bool same = ReplaySameSession(src);
            rpFresh = !same;
            Log("replay source " + src + (same ? " (recorded in this session)" : " (fresh session)"));
            if (!same) yield return ReplayFreshSetup(src);
            if (stopRequested) { endReason = "stop"; yield break; }
            Frozen();
            ReplayLint();
            var lrep = new SpecReport();
            rp = ReplayLoad(src, rplay, lrep);
            Absorb(lrep, "replay load");
            if (rp == null || !lrep.ok) throw new Exception("recording not loaded from " + src + ":\n" + lrep.Summary(20));
            ReplayInfo();
            outW = Screen.width; outH = Screen.height;
            if (spec.session.res.x > 0 && spec.session.res.y > 0)
            {
                outW = (int)spec.session.res.x; outH = (int)spec.session.res.y;
                if (!planOnly) yield return ReplayResolution(outW, outH);
            }
            cam = RenderHost.WorldCam();
            if (cam == null) throw new Exception("no gameplay camera");
            gameNear = cam.nearClipPlane > 0 ? cam.nearClipPlane : MultiCamConst.GameNear;
            if (!afterRecord && !planOnly) yield return ReplayKeepLoaded();
            if (stopRequested) { endReason = "stop"; yield break; }
            if (!afterRecord && !planOnly) yield return ReplayKeepRecorded();
            if (stopRequested) { endReason = "stop"; yield break; }
            yield return ReplayFreeze();
            ReplayHandover ho = null;
            if (afterRecord && recPrep != null && !recPrepDone)
            {
                recPrepDone = true;
                ho = recPrep.HandOver() ?? new ReplayHandover();
                foreach (var c in recKept) if (c != null && !ho.keptLoaded.Contains(c)) ho.keptLoaded.Add(c);
                recKept.Clear();
                Log("handover: " + ho.forced.Count + " forced culler(s), " + ho.keptLoaded.Count + " kept subscene(s) stay until replay end");
            }
            var brep = new SpecReport();
            rpBegun = true;
            rp.Begin(ho, brep);
            Absorb(brep, "replay begin");
            if (!brep.ok) throw new Exception("replay did not begin:\n" + brep.Summary(20));
            ReplayPacing();
            yield return ReplayStreamingSettle();
            if (targetsBegun) { try { MultiCamTargets.End(); } catch (Exception e) { DevLog.Error("mcam replay targets re-begin", e); } targetsBegun = false; }
            var trep = new SpecReport();
            MultiCamTargets.Begin(spec, trep);
            targetsBegun = true;
            Absorb(trep, "targets");
            if (!trep.ok) throw new Exception("target resolution failed:\n" + trep.Summary(20));
            ReplayWire(true);
            ReplayRigs();
            try { tracked = MultiCamTargets.Tracked() ?? new List<TargetRef>(); } catch (Exception e) { Warn("tracked targets: " + e.Message); tracked = new List<TargetRef>(); }
            ReplayPassList(planOnly);
            ReplayHideBegin();
            ReplayTimeline();
            if (nSteps == 0) throw new Exception("empty replay timeline (range " + tlFrom.ToString("0.###", IC) + ".." + tlTo.ToString("0.###", IC) + " s)");
            ReplayEventsCsv();
            WriteRun(false);
            state = "plan"; replay.phase = "plan";
            yield return PlanPass();
            FlushCsv();
            if (stopRequested) { endReason = "stop"; ReplayEndReplayer(); yield break; }
            ReplayEstimate();
            if (planOnly)
            {
                File.WriteAllText(Path.Combine(outDir, ReplayConst.PassesFile), Json.Write(rplanJson));
                Log("passes: " + ReplayConst.PassesFile + " written (" + nSteps + " steps, " + nFrames + " frames, " + passCams.Count + " pass(es), ~" + rplanJson.Num("est_minutes").ToString("0.#", IC) + " min)");
                endReason = "plan";
                replay.phase = "passes"; state = "passes done";
                ReplayEndReplayer();
                yield break;
            }
            if (grab != null) { try { grab.Dispose(); } catch { } }
            grab = new MultiCamGrab(Screen.width, Screen.height, spec.output.format == "jpg", Mathf.Clamp(spec.output.jpeg, 1, 100));
            yield return ReplayReadback();
            writers = new MultiCamFrameWriter[spec.cameras.Count];
            ReplayPrestart(0);
            outW = Screen.width; outH = Screen.height;
            rpEtaStart = Time.realtimeSinceStartup;
            rpEtaRenders0 = renders;
            loopRealStart = Time.realtimeSinceStartup;
            for (int k = 0; k < passCams.Count; k++)
            {
                if (stopRequested) break;
                yield return CameraPass(k, passCams[k]);
            }
            endReason = stopRequested ? "stop" : "done";
            ReplayEndReplayer();
            state = "replay done"; replay.phase = "end";
        }

        IEnumerator ReplayReadback()
        {
            agrab = null;
            var o = spec.output;
            if (o.format != "pipe" || o.readback == "readpixels")
            {
                Log("readback: ReadPixels on the game thread (" + (o.format != "pipe" ? "output.format " + o.format : "output.readback readpixels") + ")");
                yield break;
            }
            var g = new MultiCamAsyncGrab();
            yield return g.Open(Screen.width, Screen.height);
            if (g.ready)
            {
                agrab = g;
                Log("readback: native async (InsideReadback " + g.w + "x" + g.h + " " + g.pixFmt + ", " + MultiCamAsyncGrab.Stages + " staging textures, lag " + MultiCamAsyncGrab.Lag + ", " + MultiCamAsyncGrab.CpuFrames + " CPU frames)");
                yield break;
            }
            if (o.readback == "native") throw new Exception("output.readback native: " + g.error);
            Warn("readback: native plugin not used (" + g.error + "), ReadPixels on the game thread");
        }

        IEnumerator ReplayFreeze()
        {
            state = "freeze";
            Frozen();
            if (CameraControl.free) { CameraControl.SetFree(false); yield return null; TickFrozen(); }
            cam = RenderHost.WorldCam();
            if (cam == null) throw new Exception("no gameplay camera");
            gameNear = cam.nearClipPlane > 0 ? cam.nearClipPlane : MultiCamConst.GameNear;
            MultiCamSession.Attach(cam, spec);
        }

        IEnumerator ReplayStreamingSettle()
        {
            var mgr = SubsceneCullingManager.instance;
            float start = Time.realtimeSinceStartup, until = start + Math.Max(1f, spec.stream.timeout);
            int frames = 0;
            while ((!mgr.AllCullersFinished() || (rp != null && rp.StreamingPending)) && Time.realtimeSinceStartup < until && !stopRequested) { yield return null; TickFrozen(); frames++; }
            bool pending = rp != null && rp.StreamingPending;
            if (frames > 0) Log("replay begin: streaming settled after " + frames + " frame(s), " + (Time.realtimeSinceStartup - start).ToString("0.0", IC) + " s" + (mgr.AllCullersFinished() && !pending ? "" : " (still loading: pop-in possible)"));
            if (pending) Warn("replay begin: kept subscenes of the recording still loading after " + spec.stream.timeout.ToString("0", IC) + " s; the first pass may see pop-in and live culling");
        }

        // The baseline diff of a fresh session (2.4.1) goes into this run's output folder, never into the source take.
        SceneReplayer ReplayLoad(string src, PlaySpec ps, SpecReport rep)
        {
            SceneReplayer.baselineDir = outDir;
            try { return SceneReplayer.Load(src, ps, rep); }
            finally { SceneReplayer.baselineDir = null; }
        }

        // The recording's kept subscenes (header.keptCullers) are kept loaded and waited for before the replayer Begin, in
        // golden and replay alike: Begin then forces the cullers of every kept subscene and freezes culling with all of them
        // active (R8: a fresh-session golden began 181 frames before they had loaded and counted 52 culler SetActive and 24
        // subscene activations in its pass). Handles are released at the session pop.
        IEnumerator ReplayKeepRecorded()
        {
            var hd = recInfo != null ? recInfo.header : null;
            if (hd == null || hd.keptCullers == null || hd.keptCullers.Count == 0) yield break;
            var mgr = SubsceneCullingManager.instance;
            var want = new List<SubsceneCuller>();
            var missing = new List<string>();
            int added = 0;
            foreach (var name in hd.keptCullers)
            {
                var c = ReplayFindSubscene(mgr, name);
                if (c == null) { missing.Add(name); continue; }
                if (want.Contains(c)) continue;
                want.Add(c);
                if (MultiCamSession.Keep(c)) added++;
            }
            if (missing.Count > 0) Warn("kept subscenes of the recording not found: " + string.Join(", ", missing.ToArray()));
            state = "kept subscenes";
            float start = Time.realtimeSinceStartup, until = start + Math.Max(1f, spec.stream.timeout);
            int frames = 0, settle = 0;
            while (!stopRequested && Time.realtimeSinceStartup < until)
            {
                bool done = mgr.AllCullersFinished();
                if (done) foreach (var c in want) if (c != null && (!c.IsLoaded || !c.IsActive || c.InProgress)) { done = false; break; }
                if (done && ++settle > 2) break;
                if (!done) settle = 0;
                yield return null;
                TickFrozen();
                frames++;
            }
            int notReady = 0;
            foreach (var c in want) if (c != null && (!c.IsLoaded || !c.IsActive || c.InProgress)) notReady++;
            Log("kept subscenes of the recording: " + want.Count + " (" + added + " newly kept), " + (notReady == 0 ? "all loaded and active" : notReady + " not ready") + " after " + frames + " frame(s), " + (Time.realtimeSinceStartup - start).ToString("0.0", IC) + " s");
            if (notReady > 0) Warn("kept subscenes: " + notReady + " of " + want.Count + " not loaded and active after " + spec.stream.timeout.ToString("0", IC) + " s; the replay may see pop-in");
        }

        static SubsceneCuller ReplayFindSubscene(SubsceneCullingManager mgr, string name)
        {
            SubsceneCuller c = null;
            try { c = mgr.GetSubsceneCullerByName(name); } catch { }
            if (c != null) return c;
            foreach (var s in mgr.GetAllSubsceneCullers()) if (s != null && (s.name == name || s.SceneName == name)) return s;
            return null;
        }

        void ReplayPacing()
        {
            if (!spec.session.vsync)
            {
                try { FramerateController.automatic = false; FramerateController.SetVSyncCount(0); } catch { }
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
            Time.captureFramerate = Mathf.Clamp(Mathf.RoundToInt(spec.timing.fps), 1, MultiCamConst.MaxRate);
        }

        string ReplaySource()
        {
            string s = rspec.source;
            if (string.IsNullOrEmpty(s)) return recordDir;
            try { return Path.IsPathRooted(s) ? Path.GetFullPath(s) : Path.GetFullPath(Path.Combine(plan.dir, s)); }
            catch (Exception e) { throw new Exception("replay.source: bad path '" + s + "': " + e.Message); }
        }

        static string ReplayNormPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            try { p = Path.GetFullPath(p); } catch { }
            return p.Replace('/', '\\').TrimEnd('\\');
        }

        static bool ReplaySameSession(string src)
        {
            return SceneRecorder.SessionRecording(src) != null;
        }

        IEnumerator ReplayFreshSetup(string src)
        {
            state = "fresh setup";
            List<string> lines = rplay.setup;
            string from = "play.setup";
            if (lines == null)
            {
                from = "the record spec's setup (" + Path.Combine(src, "spec.json") + ")";
                try
                {
                    var rep = new SpecReport();
                    var s = MultiCamSpec.Parse(File.ReadAllText(Path.Combine(src, "spec.json")), Path.Combine(src, "spec.json"), rep);
                    if (s != null) lines = s.setup;
                }
                catch (Exception e) { Warn("fresh session: record spec.json unreadable: " + e.Message); }
            }
            if (lines == null || lines.Count == 0)
            {
                from = "the recording header (load + waitfor streaming)";
                lines = new List<string>();
                SceneRecording r = null;
                var rep = new SpecReport();
                try { r = SrecReader.Read(Path.Combine(src, ReplayConst.RecordFile), rep); } catch (Exception e) { Warn("fresh session: header read failed: " + e.Message); }
                if (r == null || r.header == null || r.header.subscene < 0) throw new Exception("fresh session: no setup and no savepoint in the recording header of " + src);
                lines.Add("load " + r.header.subscene.ToString(IC) + " " + r.header.savepoint.ToString(IC));
                lines.Add("waitfor streaming");
            }
            Log("fresh session: " + lines.Count + " setup line(s) from " + from + " (cues are not run)");
            Running1();
            yield return ReplaySetupLines(lines);
        }

        IEnumerator ReplaySetupLines(List<string> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (stopRequested) yield break;
                string line = (lines[i] ?? "").Trim();
                if (line.Length == 0) continue;
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string c0 = p[0].ToLowerInvariant();
                state = "replay setup " + (i + 1) + "/" + lines.Count + ": " + line;
                if (c0 == "mcam") { Warn("replay setup: skipped '" + line + "' (mcam commands cannot be setup commands)"); continue; }
                if (c0 == "wait")
                {
                    double s = 0;
                    double.TryParse(p.Length > 1 ? p[1] : "0", NumberStyles.Float, IC, out s);
                    float until = Time.realtimeSinceStartup + (float)s;
                    while (Time.realtimeSinceStartup < until && !stopRequested) { yield return null; TickFree(); }
                    EventRow("setup", "wait", line, "ok");
                    continue;
                }
                if (c0 == "waitfor") { yield return WaitFor(p, line); continue; }
                string res = RunCmd(line);
                EventRow("setup", "", line, res);
                Log("replay setup: " + line + " -> " + res);
                yield return null;
                TickFree();
            }
        }

        IEnumerator ReplayKeepLoaded()
        {
            var st = spec.stream;
            if (st.keepLoaded == "none") yield break;
            state = "keep-loaded";
            var mgr = SubsceneCullingManager.instance;
            var want = new List<SubsceneCuller>();
            foreach (var name in st.cullers)
            {
                var c = mgr.GetSubsceneCullerByName(name);
                if (c == null) Warn("keep-loaded: no culler named " + name); else if (!want.Contains(c)) want.Add(c);
            }
            int planned = 0;
            if (st.keepLoaded == "auto" && st.prepass)
            {
                var rep = new SpecReport();
                MultiCamTargets.Begin(spec, rep);
                targetsBegun = true;
                Absorb(rep, "keep-loaded targets");
                ReplayWire(true);
                ReplayRigs();
                step = -1;
                ReplayResetCtx();
                try { MultiCamRig.SampleGame(cam, ctx); MultiCamTargets.Step(ctx); } catch (Exception e) { RateError("keep-loaded sample", e); }
                ReplayBeginRigs();
                EvaluateAll();
                var list = ReplayPlanned();
                planned = list.Count;
                foreach (var r in rigs) { try { if (r != null) r.End(); } catch { } }
                rigs = new ICamRig[0];
                try { MultiCamTargets.End(); } catch (Exception e) { DevLog.Error("mcam replay keep-loaded targets", e); }
                targetsBegun = false;
                foreach (var c in mgr.GetAllSubsceneCullers())
                {
                    if (c == null || want.Contains(c)) continue;
                    var b = c.CullingBounds;
                    foreach (var p in list) if (Sees(p, b)) { want.Add(c); break; }
                }
            }
            int added = 0;
            foreach (var c in want) if (MultiCamSession.Keep(c)) added++;
            if (want.Count == 0) yield break;
            Log("keep-loaded (replay cameras): " + want.Count + " culler(s) in view of " + planned + " planned pose(s), " + added + " newly added (released at session pop)");
            float until = Time.realtimeSinceStartup + Math.Max(1f, st.timeout);
            while (Time.realtimeSinceStartup < until && !stopRequested)
            {
                bool done = mgr.AllCullersFinished();
                if (done) foreach (var c in want) if (c != null && !c.IsActive) { done = false; break; }
                if (done) break;
                yield return null;
                TickFrozen();
            }
            int missing = 0;
            foreach (var c in want) if (c != null && !c.IsActive) missing++;
            if (missing > 0) Warn("keep-loaded: " + missing + " culler(s) not active after " + st.timeout.ToString("0", IC) + " s; pop-in possible");
        }

        void ReplayOpenFiles()
        {
            meta = new MultiCamCsv(Path.Combine(outDir, "meta.csv"), ReplayMetaHeader);
            eventsCsv = new MultiCamCsv(Path.Combine(outDir, "events.csv"), ReplayEventsHeader);
            if (spec.output.poses) posesCsv = new MultiCamCsv(Path.Combine(outDir, "poses.csv"), ReplayPosesHeader);
            if (spec.output.tracks) tracksCsv = new MultiCamCsv(Path.Combine(outDir, "tracks.csv"), ReplayTracksHeader);
            if (spec.output.rig) rigCsv = new MultiCamCsv(Path.Combine(outDir, "rig.csv"), ReplayRigHeader);
            viewsCsv = new MultiCamCsv(Path.Combine(outDir, "views.csv"), ReplayViewsHeader);
            for (int i = 0; i < spec.cameras.Count; i++) camDirs[i] = Path.Combine(outDir, spec.cameras[i].name);
        }

        void ReplayLint()
        {
            if (rplay.look != "game") Warn("play.look '" + rplay.look + "' is not built yet (P1): replayed with look 'game'");
            if (rplay.order != "camera") Warn("play.order '" + rplay.order + "' is not built yet (P1): replayed with order 'camera'");
            for (int i = 0; i < spec.cameras.Count; i++)
                if (spec.cameras[i].enabled && MultiCamShots.AnyHide(spec, i)) Log("camera " + spec.cameras[i].name + ": hide in its passes through a spare layer and the camera's cullingMask (MultiCamHide)");
            if (spec.output.format == "pipe" && rplay.copyHolds) Log("output pipe: director holds are rendered, not copied");
        }

        void ReplayInfo()
        {
            if (rp == null) return;
            try { recInfo = rp.Info(); } catch (Exception e) { RateError("replay info", e); }
        }

        void ReplayPassList(bool planOnly)
        {
            var want = replayArgs.cams.Count > 0 ? replayArgs.cams : rplay.cams;
            int n = spec.cameras.Count;
            passCams.Clear();
            passOf = new int[n];
            for (int i = 0; i < n; i++) passOf[i] = -1;
            foreach (int i in plan.order)
            {
                var c = spec.cameras[i];
                if (!c.enabled) continue;
                if (want != null && want.Count > 0 && !want.Contains(c.name)) continue;
                passOf[i] = passCams.Count;
                passCams.Add(i);
            }
            if (want != null)
                foreach (var name in want)
                {
                    int i;
                    if (!plan.camIndex.TryGetValue(name, out i)) Warn("cams: unknown camera '" + name + "'");
                    else if (!spec.cameras[i].enabled) Warn("cams: camera '" + name + "' is disabled");
                }
            replay.passes = passCams.Count;
            if (passCams.Count == 0 && !planOnly) throw new Exception("no camera to film (cams filter or every camera disabled)");
        }

        void ReplayTimeline()
        {
            double dur = 0;
            try { dur = rp.duration; } catch (Exception e) { RateError("replay duration", e); }
            if (double.IsNaN(dur) || dur < 0) dur = 0;
            double from = 0, to = dur;
            if (replayArgs.hasRange) { from = replayArgs.rangeFrom; to = replayArgs.rangeTo; }
            else if (rplay.Has("range")) { from = rplay.range.x; to = rplay.range.y; }
            from = Math.Max(0, Math.Min(from, dur));
            to = Math.Max(from, Math.Min(to, dur));
            tlFrom = from; tlTo = to;
            int ns = plan.segs.Count;
            segFrom = new double[ns];
            var speed = new float[ns];
            var samples = new int[ns];
            var shutter = new float[ns];
            for (int i = 0; i < ns; i++)
            {
                var g = plan.segs[i];
                segFrom[i] = i == 0 ? double.NegativeInfinity : ctx.TimeOf(g.spec.from, double.NaN);
                if (i > 0 && double.IsNaN(segFrom[i])) Warn("timing.segments[" + i + "].from " + g.spec.from + ": not in the recording (event never fired); segment unused");
                speed[i] = g.speed > 0 ? g.speed : 1f;
                samples[i] = Math.Max(1, g.captured);
                shutter[i] = g.spec.shutter;
            }
            float fps = spec.timing.fps > 0 ? spec.timing.fps : 30f;
            tl = MultiCamReplayMath.Timeline(from, to, fps, Math.Max(0.0, rplay.ramp), segFrom, speed, samples, shutter, Math.Max(0, rplay.warmup), out nWarm, out nSteps, out nFrames);
            long prevLogic = 0;
            for (int idx = 0; idx < tl.Count; idx++)
            {
                var e = tl[idx];
                try { e.real = rp.RealTimeAt(e.T); } catch (Exception ex) { e.real = e.T; RateError("replay real time", ex); }
                try { e.logic = rp.LogicStepAt(e.T); } catch (Exception ex) { e.logic = 0; RateError("replay logic step", ex); }
                if (e.step > 0) e.fixedSteps = (int)Math.Max(0, e.logic - prevLogic);
                if (e.step >= 0) prevLogic = e.logic;
            }
            replay.duration = dur;
            replay.steps = nSteps;
            Log("timeline: " + nFrames + " frame(s), " + nSteps + " step(s) at " + fps.ToString("0.##", IC) + " fps over record " + from.ToString("0.###", IC) + ".." + to.ToString("0.###", IC) + " s of " + dur.ToString("0.###", IC) + " s, ramp " + rplay.ramp.ToString("0.##", IC) + " s, " + nWarm + " warmup slot(s)");
        }

        void ReplayEventsCsv()
        {
            if (eventsCsv == null || rp == null || rp.tracks == null) return;
            foreach (var ev in rp.tracks.events)
            {
                if (ev == null) continue;
                int s = -1, f = -1;
                for (int idx = nWarm; idx < tl.Count; idx++) if (tl[idx].T >= ev.time - 1e-9) { s = tl[idx].step; f = tl[idx].frame; break; }
                eventsCsv.I(s).D(ev.time, "0.######").I(f).S(ev.kind).S(ev.name).S("record").S(ev.detail).End();
            }
        }

        bool ReplayGameCam(double t)
        {
            bool ok = false;
            try { ok = rp.GameCam(t, ctx.game); } catch (Exception e) { RateError("replay game camera", e); }
            if (!ok) { try { MultiCamRig.SampleGame(cam, ctx); } catch (Exception e) { RateError("SampleGame", e); } }
            ctx.game.step = ctx.step;
            return ok;
        }

        IEnumerator PlanPass()
        {
            nCams = spec.cameras.Count;
            tlPoses = new CamPose[tl.Count * nCams];
            float chunk = Time.realtimeSinceStartup;
            int lastSeg = -1;
            double prevT = tl.Count > 0 ? tl[0].T : 0, prevO = tl.Count > 0 ? tl[0].o : 0;
            for (int idx = 0; idx < tl.Count; idx++)
            {
                if (stopRequested) yield break;
                var e = tl[idx];
                rp.T = e.T;
                try { rp.ApplyNow(e.T); } catch (Exception ex) { RateError("replay apply", ex); }
                step = e.step; frame = e.frame;
                ctx.step = idx - nWarm;
                ctx.t = e.T;
                ctx.dt = idx == 0 ? 0f : (float)(e.T - prevT);
                ctx.frame = e.frame; ctx.sub = e.sub; ctx.captured = e.step >= 0;
                ctx.rate = e.rate; ctx.speed = e.speed;
                ctx.outputTime = e.o; ctx.outputDt = idx == 0 ? 0f : (float)(e.o - prevO);
                ctx.realTime = e.real;
                ReplayGameCam(e.T);
                try { MultiCamTargets.Step(ctx); } catch (Exception ex) { RateError("targets step", ex); }
                if (idx == 0) ReplayBeginRigs();
                if (e.step >= 0 && e.seg != lastSeg)
                {
                    lastSeg = e.seg;
                    var g = plan.segs[e.seg];
                    EventRow("segment", g.label, "segments[" + e.seg + "]", "speed " + e.speed.ToString("0.####", IC) + " samples " + e.samples + " rate " + e.rate);
                }
                EvaluateAll();
                Array.Copy(poses, 0, tlPoses, idx * nCams, nCams);
                planIdx = idx;
                ReplayHidePlan(idx, e);
                e.aim = ctx.game.aimDistance;
                if (e.step >= 0) ReplayRows(e);
                ReplayPaths(idx, e);
                if (e.step >= 0) ReplayViews(e);
                prevT = e.T; prevO = e.o;
                replay.t = e.T;
                replay.stepIndex = Math.Max(0, e.step + 1);
                if (Time.realtimeSinceStartup - chunk > 0.05f)
                {
                    Progress(false);
                    yield return null;
                    TickFrozen();
                    chunk = Time.realtimeSinceStartup;
                }
            }
            step = nSteps - 1;
            frame = nFrames;
            Log("plan pass: " + tl.Count + " evaluation(s) (" + nWarm + " warmup slots + " + nSteps + " steps) x " + nCams + " camera(s)");
            ReplayViewWarnings();
        }

        void ReplayRows(MultiCamReplayEntry e)
        {
            meta.I(e.step).D(e.T, "0.######").I(e.rate).I(e.samples).I(e.frame).I(e.sub).I(1).D(e.speed, "0.######").D(e.real, "0.######").I(e.fixedSteps).I(e.seg).End();
            ReplayPoseRows();
            ReplayRigRow();
            ReplayTrackRows();
        }

        void ReplayPoseRows()
        {
            if (posesCsv == null) return;
            for (int i = 0; i < poses.Length; i++)
            {
                var p = poses[i];
                var e = p.r.eulerAngles;
                posesCsv.I(step).S(spec.cameras[i].name).V(p.p, "0.#####").D(e.x, "0.####").D(e.y, "0.####").D(e.z, "0.####").D(p.fov, "0.####").Q(p.r)
                    .V(p.aim, "0.####").D(p.near > 0 ? p.near : gameNear, "0.###").S(PoseFlags.Letters(p.flags)).S(p.copyOf ?? "").V(p.hasSubj ? p.subj : new Vector3(float.NaN, float.NaN, float.NaN), "0.####").End();
            }
        }

        // View checks of every enabled camera's pose every view.every record seconds in the plan pass (scene applied at T):
        // views.csv rows and run.json `views` (per camera: checks, blocked, inside, first times). MultiCamView, MULTICAM.md 6.2.
        void ReplayViews(MultiCamReplayEntry e)
        {
            if (viewsCsv == null) return;
            int n = spec.cameras.Count;
            if (viewNext == null || viewNext.Length != n)
            {
                viewNext = new double[n]; viewChecks = new int[n]; viewBlocked = new int[n]; viewInside = new int[n];
                viewFirstBlocked = new double[n]; viewFirstInside = new double[n]; viewFirstBlocker = new string[n]; viewFirstInsideBy = new string[n];
                for (int i = 0; i < n; i++) { viewNext[i] = double.NegativeInfinity; viewFirstBlocked[i] = viewFirstInside[i] = double.NaN; }
            }
            List<Bounds> avoid = null;
            ViewSpec avoidFor = null;
            for (int i = 0; i < n; i++)
            {
                var cs = spec.cameras[i];
                if (!cs.enabled) continue;
                var v = MultiCamView.Spec(cs);
                if (v.every <= 0f || e.T < viewNext[i] - 1e-9) continue;
                viewNext[i] = e.T + v.every;
                var p = poses[i];
                if (!p.ok) continue;
                var src = !string.IsNullOrEmpty(p.copyOf) ? spec.FindCamera(p.copyOf) : null;
                ViewResult r;
                try
                {
                    if (avoid == null || !ReferenceEquals(avoidFor, v)) { avoid = MultiCamView.Avoid(ctx, v); avoidFor = v; }
                    r = MultiCamView.Check(p, p.aim, v, avoid, ctx.aspect, ViewOwn(src ?? cs), ReplayHideRoots(planIdx, i));
                }
                catch (Exception ex) { RateError("view check " + cs.name, ex); continue; }
                viewChecks[i]++;
                if (!r.visible) { viewBlocked[i]++; if (double.IsNaN(viewFirstBlocked[i])) { viewFirstBlocked[i] = e.T; viewFirstBlocker[i] = r.blocker; } }
                if (r.inside) { viewInside[i]++; if (double.IsNaN(viewFirstInside[i])) { viewFirstInside[i] = e.T; viewFirstInsideBy[i] = r.insideBy; } }
                string ph = pathPending != null && i < pathPending.Length ? pathPending[i] ?? "" : "";
                if (pathPending != null && i < pathPending.Length) pathPending[i] = "";
                viewsCsv.I(e.step).D(e.T, "0.######").S(cs.name).S(src != null ? src.name : (p.flags & PoseFlags.Blending) != 0 ? "blend" : "").I(r.visible ? 1 : 0).S(r.blocker)
                    .D(r.visible ? double.NaN : r.blockAt, "0.###").I(r.inside ? 1 : 0).S(r.insideBy).V(p.p, "0.###").V(r.subject, "0.###").S(ph).End();
            }
        }

        Transform ViewOwn(CamSpec cs)
        {
            var r = cs.look != null && !cs.look.isSelf && !cs.look.isNone ? cs.look : cs.target;
            if (r == null || r.fixedPoint) return null;
            TargetSample s;
            try { return ctx.Target(r, out s) && s.ok ? MultiCamView.Own(r, s.go) : null; } catch { return null; }
        }

        // ---------------------------------------------------------------- hide sets and camera paths (MULTICAM.md 6.4, 6.2)

        void ReplayHideBegin()
        {
            MultiCamHide.Restore();
            MultiCamHide.ClearCache();
            rpHideSets.Clear();
            rpHideIndex.Clear();
            bool any = false;
            for (int i = 0; i < spec.cameras.Count; i++) if (spec.cameras[i].enabled && MultiCamShots.AnyHide(spec, i)) any = true;
            if (!any) return;
            MultiCamHide.PickLayer();
            Log("hide: " + MultiCamHide.layerNote + "; game camera cullingMask 0x" + (cam != null ? cam.cullingMask.ToString("X8", IC) : "?"));
        }

        double ReplayTimeOf(TimeRef r) { return ctx.TimeOf(r, 0.0); }

        void ReplayHidePlan(int idx, MultiCamReplayEntry e)
        {
            if (tlHide.Length != tl.Count * nCams) { tlHide = new int[tl.Count * nCams]; for (int k = 0; k < tlHide.Length; k++) tlHide[k] = -1; }
            for (int i = 0; i < nCams; i++)
            {
                var cs = spec.cameras[i];
                if (!cs.enabled || !MultiCamShots.AnyHide(spec, i)) continue;
                var names = MultiCamShots.HideNames(spec, i, e.T, ReplayTimeOf, poses[i].flags);
                if (names.Count == 0) continue;
                string key = MultiCamShots.HideKey(names);
                int id;
                if (!rpHideIndex.TryGetValue(key, out id))
                {
                    var rep = new SpecReport();
                    var set = MultiCamHide.Resolve(names, cs.where + ".hide", rep);
                    Absorb(rep, "hide");
                    id = rpHideSets.Count;
                    rpHideSets.Add(set);
                    rpHideIndex[key] = id;
                    Log("hide set " + id + " (first for " + cs.name + " at " + e.T.ToString("0.###", IC) + " s): " + MultiCamHide.Text(set));
                }
                tlHide[idx * nCams + i] = id;
            }
        }

        int ReplayHideAt(int idx, int i)
        {
            int k = idx * nCams + i;
            return k >= 0 && k < tlHide.Length ? tlHide[k] : -1;
        }

        List<Transform> ReplayHideRoots(int idx, int i)
        {
            int id = ReplayHideAt(idx, i);
            return id >= 0 ? rpHideSets[id].roots : null;
        }

        // Camera path check of every enabled camera at every evaluation: the straight segment from its previous position
        // crossing a collider (MultiCamView.PathHit) counts at captured steps; views.csv `path_hit` carries the first crossing
        // since the camera's previous row, run.json views pathHits / firstPathHit / firstPathBy.
        void ReplayPaths(int idx, MultiCamReplayEntry e)
        {
            int n = spec.cameras.Count;
            if (pathPrev == null || pathPrev.Length != n)
            {
                pathPrev = new Vector3[n]; pathHas = new bool[n]; pathHits = new int[n]; pathPending = new string[n]; pathFirstBy = new string[n]; pathFirst = new double[n];
                for (int i = 0; i < n; i++) pathFirst[i] = double.NaN;
            }
            for (int i = 0; i < n; i++)
            {
                var cs = spec.cameras[i];
                var p = poses[i];
                if (!cs.enabled || !p.ok) { pathHas[i] = false; continue; }
                if (pathHas[i] && e.step >= 0 && (p.flags & PoseFlags.Cut) == 0)
                {
                    string hit = "";
                    try
                    {
                        var src = !string.IsNullOrEmpty(p.copyOf) ? spec.FindCamera(p.copyOf) : null;
                        hit = MultiCamView.PathHit(pathPrev[i], p.p, MultiCamView.Spec(cs), ViewOwn(src ?? cs), ReplayHideRoots(idx, i));
                    }
                    catch (Exception ex) { RateError("path check " + cs.name, ex); }
                    if (hit.Length > 0)
                    {
                        pathHits[i]++;
                        if (double.IsNaN(pathFirst[i])) { pathFirst[i] = e.T; pathFirstBy[i] = hit; }
                        if (string.IsNullOrEmpty(pathPending[i])) pathPending[i] = hit;
                    }
                }
                pathPrev[i] = p.p;
                pathHas[i] = true;
            }
        }

        void ReplayViewWarnings()
        {
            for (int i = 0; i < spec.cameras.Count; i++)
            {
                var name = spec.cameras[i].name;
                if (pathHits != null && i < pathHits.Length && pathHits[i] > 0)
                    Warn("camera " + name + ": its path passes through level geometry " + pathHits[i] + " time(s), first at " + pathFirst[i].ToString("0.###", IC) + " s (" + pathFirstBy[i] + "); views.csv path_hit");
                if (viewInside != null && i < viewInside.Length && viewInside[i] > 0)
                    Warn("camera " + name + ": camera or near plane inside geometry in " + viewInside[i] + " of " + viewChecks[i] + " view check(s), first at " + viewFirstInside[i].ToString("0.###", IC) + " s (" + viewFirstInsideBy[i] + "); views.csv inside");
            }
        }

        Json.Obj ReplayViewsJson()
        {
            var o = new Json.Obj();
            if (viewNext == null) return o;
            for (int i = 0; i < viewNext.Length && i < spec.cameras.Count; i++)
            {
                if (viewChecks[i] == 0 && (pathHits == null || i >= pathHits.Length || pathHits[i] == 0)) continue;
                var c = new Json.Obj();
                c["checks"] = (double)viewChecks[i]; c["blocked"] = (double)viewBlocked[i]; c["inside"] = (double)viewInside[i];
                c["firstBlocked"] = MultiCamPlan.Num(viewFirstBlocked[i]); c["firstBlocker"] = viewFirstBlocker[i];
                c["firstInside"] = MultiCamPlan.Num(viewFirstInside[i]); c["firstInsideBy"] = viewFirstInsideBy[i];
                if (pathHits != null && i < pathHits.Length)
                {
                    c["pathHits"] = (double)pathHits[i]; c["firstPathHit"] = MultiCamPlan.Num(pathFirst[i]); c["firstPathBy"] = pathFirstBy[i] ?? "";
                }
                o[spec.cameras[i].name] = c;
            }
            return o;
        }

        void ReplayRigRow()
        {
            if (rigCsv == null) return;
            var g = ctx.game;
            rigCsv.I(step).D(ctx.t, "0.######").S(g.box ?? "").V(g.positionCamera, "0.#####").V(g.positionAim, "0.#####").V(g.positionUp, "0.######")
                .V(g.rendered.p, "0.#####").Q(g.rendered.r).D(g.rendered.fov, "0.####").V(g.clean.p, "0.#####").Q(g.clean.r).V(g.velocity, "0.####")
                .V(g.probe, "0.#####").D(g.extrapolation, "0.#####").V(g.handOffset, "0.######").V(g.shakeOffset, "0.######").I(g.cut ? 1 : 0).End();
        }

        void ReplayTrackRows()
        {
            if (tracksCsv == null) return;
            foreach (var r in tracked)
            {
                TargetSample s = default(TargetSample);
                bool ok;
                try { ok = MultiCamTargets.Sample(r, out s) && s.ok; } catch { ok = false; }
                tracksCsv.I(step).D(ctx.t, "0.######").S(r.text).I(ok ? 1 : 0);
                if (ok) tracksCsv.V(s.pos, "0.#####").V(s.vel, "0.####").I(s.count); else tracksCsv.E().E().E().E().E().E().E();
                if (ok && s.hasBounds) tracksCsv.V(s.bounds.min, "0.####").V(s.bounds.max, "0.####"); else tracksCsv.E().E().E().E().E().E();
                tracksCsv.End();
            }
        }

        bool ReplayKeepBlur(int i)
        {
            var r = spec.cameras[i].render;
            return r != null && r.Has("motionBlur") && r.motionBlur < 0f;
        }

        float ReplayBlur(int i, MultiCamReplayEntry e)
        {
            return MultiCamReplayMath.BlurStrength(rplay.motionBlur, rplay.shutterAngle, MultiCamSession.mbStrength, e.speed, spec.timing.fps, e.samples, ReplayKeepBlur(i)) * MultiCamShots.BlurScale(spec, i, e.T, ReplayTimeOf);
        }

        int ReplayCopyAt(int i, int idx, bool files)
        {
            if (!rplay.copyHolds || spec.output.format == "pipe") return -1;
            var e = tl[idx];
            if (e.step < 0) return -1;
            var p = tlPoses[idx * nCams + i];
            if (string.IsNullOrEmpty(p.copyOf)) return -1;
            int s;
            if (!plan.camIndex.TryGetValue(p.copyOf, out s) || s == i) return -1;
            if (s >= passOf.Length || passOf[s] < 0 || passOf[s] >= passOf[i]) return -1;
            if (!string.IsNullOrEmpty(tlPoses[idx * nCams + s].copyOf)) return -1;
            if (!Same(spec.cameras[i].post, spec.cameras[s].post) || ReplayKeepBlur(i) != ReplayKeepBlur(s)) return -1;
            if (ReplayHideAt(idx, i) != ReplayHideAt(idx, s) || ReplayBlur(i, e) != ReplayBlur(s, e)) return -1;
            if (files && !File.Exists(ReplayFramePath(s, e.step))) return -1;
            return s;
        }

        List<MultiCamReplayAct> ReplayActions(int i, int startRenders, bool files, out int warm, out int copies, out int runs)
        {
            return MultiCamReplayMath.Actions(nWarm, tl.Count, Math.Max(0, rplay.warmup), startRenders, idx => ReplayCopyAt(i, idx, files), out warm, out copies, out runs);
        }

        void ReplayEstimate()
        {
            int r = renders;
            long totalRenders = 0;
            int totalCopies = 0, totalWarm = 0;
            double px = (double)Math.Max(1, outW) * Math.Max(1, outH), scale = px / (2560.0 * 1440.0);
            double totalSec = 0, totalGb = 0;
            var cl = new List<object>();
            foreach (int i in passCams)
            {
                int warm, copies, runs;
                var acts = ReplayActions(i, r, false, out warm, out copies, out runs);
                int rend = 0, own = 0;
                foreach (var a in acts) { if (a.kind != MultiCamReplayMath.ActCopy) rend++; if (a.kind == MultiCamReplayMath.ActOwn) own++; }
                r += rend;
                totalRenders += rend; totalCopies += copies; totalWarm += warm;
                double sec = (own * 0.05 + (rend - own) * 0.03) * Math.Max(0.25, scale);
                double gb = spec.output.format == "raw" ? nSteps * px * 4 / 1e9 : spec.output.format == "pipe" ? nSteps * px * 0.05 / 1e9 : nSteps * 1.0e6 * scale / 1e9;
                totalSec += sec; totalGb += gb;
                var c = new Json.Obj();
                var cs = spec.cameras[i];
                c["name"] = cs.name; c["mode"] = cs.mode; c["pass"] = (double)passOf[i];
                c["renders"] = (double)rend; c["captured"] = (double)own; c["warmups"] = (double)warm; c["copies"] = (double)copies; c["runs"] = (double)runs;
                c["est_seconds"] = Math.Round(sec, 1); c["est_gb"] = Math.Round(gb, 3);
                cl.Add(c);
            }
            rpEtaTotal = totalRenders;
            var o = new Json.Obj();
            o["spec"] = plan.path;
            o["recording"] = rpSource;
            o["fresh_session"] = rpFresh;
            o["duration"] = replay.duration;
            o["range"] = new List<object> { tlFrom, tlTo };
            o["fps"] = (double)spec.timing.fps;
            o["ramp"] = (double)rplay.ramp;
            o["warmup"] = (double)rplay.warmup;
            o["screen"] = new List<object> { (double)outW, (double)outH };
            o["events"] = ReplayEventTimesJson();
            o["segments"] = ReplaySegmentsJson();
            o["steps"] = (double)nSteps;
            o["frames"] = (double)nFrames;
            o["output_seconds"] = nFrames / (double)Math.Max(1e-6f, spec.timing.fps);
            o["cameras"] = cl;
            o["renders"] = (double)totalRenders;
            o["warmups"] = (double)totalWarm;
            o["copies"] = (double)totalCopies;
            o["est_seconds"] = Math.Round(totalSec, 1);
            o["est_minutes"] = Math.Round(totalSec / 60.0, 1);
            o["est_gb"] = Math.Round(totalGb, 2);
            rplanJson = o;
            Log("passes estimate: " + passCams.Count + " pass(es), " + totalRenders + " render(s) (" + totalWarm + " warmup), " + totalCopies + " copied step(s), ~" + (totalSec / 60.0).ToString("0.#", IC) + " min, ~" + totalGb.ToString("0.##", IC) + " GB");
        }

        // The ffmpeg pipe of a pass is started on a worker thread one pass ahead (ReplayPrestart), so Process.Start and ffmpeg's
        // own start-up overlap the previous pass instead of stalling the first frames of this one (0.4-0.8 s per pass).
        sealed class ReplayPipeStart
        {
            public string cam, ffmpeg, args; public int queue; public MultiCamFrameWriter writer; public string error; public System.Threading.Thread thread;
            public void Run()
            {
                try { writer = MultiCamFrameWriter.Pipe(cam, queue, ffmpeg, args); }
                catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }
            }
        }

        readonly Dictionary<int, ReplayPipeStart> rpStarting = new Dictionary<int, ReplayPipeStart>();

        string ReplayPipeArgs(int i)
        {
            var o = spec.output;
            string file = Path.Combine(outDir, spec.cameras[i].name + ".mkv");
            return agrab != null ? PipeArgs(Screen.width, Screen.height, o, file, agrab.pixFmt, false) : PipeArgs(Screen.width, Screen.height, o, file);
        }

        void ReplayPrestart(int k)
        {
            if (spec.output.format != "pipe" || k < 0 || k >= passCams.Count) return;
            int i = passCams[k];
            if (writers == null) writers = new MultiCamFrameWriter[spec.cameras.Count];
            if (writers[i] != null || rpStarting.ContainsKey(i)) return;
            var job = new ReplayPipeStart { cam = spec.cameras[i].name, ffmpeg = spec.output.ffmpeg, args = ReplayPipeArgs(i), queue = Math.Max(1, spec.output.queue) };
            job.thread = new System.Threading.Thread(job.Run);
            job.thread.IsBackground = true;
            job.thread.Name = "mcam pipe start " + job.cam;
            rpStarting[i] = job;
            job.thread.Start();
        }

        MultiCamFrameWriter ReplayTakeStarted(int i)
        {
            ReplayPipeStart job;
            if (!rpStarting.TryGetValue(i, out job)) return null;
            rpStarting.Remove(i);
            if (!job.thread.Join(15000)) { Warn("pipe " + job.cam + ": the prestart did not finish within 15 s; started again on the game thread"); return null; }
            if (job.writer == null) { Warn("pipe " + job.cam + ": prestart failed (" + job.error + "); started again on the game thread"); return null; }
            Log("pipe " + job.cam + " (started ahead of the pass): " + job.ffmpeg + " " + job.args);
            return job.writer;
        }

        void ReplayDropPrestarted()
        {
            foreach (var kv in rpStarting)
            {
                var job = kv.Value;
                try
                {
                    if (job.thread != null && !job.thread.Join(15000)) continue;
                    if (job.writer == null) continue;
                    job.writer.Close(true, 2000);
                    string file = Path.Combine(outDir, job.cam + ".mkv");
                    if (job.writer.written == 0 && File.Exists(file) && new FileInfo(file).Length < 4096) File.Delete(file);
                    Log("pipe " + job.cam + ": started ahead but never used (run ended), closed");
                }
                catch (Exception e) { DevLog.Error("mcam prestarted pipe " + job.cam, e); }
            }
            rpStarting.Clear();
        }

        MultiCamFrameWriter ReplayWriter(int i)
        {
            if (writers == null) writers = new MultiCamFrameWriter[spec.cameras.Count];
            if (writers[i] != null) return writers[i];
            var o = spec.output;
            var c = spec.cameras[i];
            if (o.format == "pipe")
            {
                var pre = ReplayTakeStarted(i);
                if (pre != null) { writers[i] = pre; return pre; }
                string file = Path.Combine(outDir, c.name + ".mkv");
                string args = agrab != null ? PipeArgs(Screen.width, Screen.height, o, file, agrab.pixFmt, false) : PipeArgs(Screen.width, Screen.height, o, file);
                writers[i] = MultiCamFrameWriter.Pipe(c.name, Math.Max(1, o.queue), o.ffmpeg, args);
                Log("pipe " + c.name + ": " + o.ffmpeg + " " + args);
            }
            else
            {
                Directory.CreateDirectory(camDirs[i]);
                writers[i] = new MultiCamFrameWriter(c.name, Math.Max(1, o.queue));
            }
            return writers[i];
        }

        string ReplayFramePath(int i, int stepIndex)
        {
            if (spec.output.format == "pipe") return null;
            return Path.Combine(camDirs[i], stepIndex.ToString("D5", IC) + (spec.output.format == "raw" ? ".rgba" : ".jpg"));
        }

        bool ReplayCopy(int src, int dst, int stepIndex)
        {
            try { File.Copy(ReplayFramePath(src, stepIndex), ReplayFramePath(dst, stepIndex), true); return true; }
            catch (Exception e) { RateError("copy " + spec.cameras[src].name + " -> " + spec.cameras[dst].name, e); return false; }
        }

        void ReplayWriterCheck(MultiCamFrameWriter w)
        {
            if (w != null && w.error != null) throw new Exception("frame writer " + w.name + ": " + w.error);
        }

        double ReplayT0Real()
        {
            if (recInfo != null && recInfo.header != null) return recInfo.header.t0Real;
            if (recHeader != null) return recHeader.t0Real;
            return 0;
        }

        void ReplayTaa(bool reset, float blur, ReplayPassInfo info)
        {
            var taa = MultiCamSession.taa;
            if (taa == null) return;
            if (reset)
            {
                taa.feedbackMin = 0f; taa.feedbackMax = 0f;
                rpTaaPending = true;
                if (info != null) info.taaResets++;
            }
            else if (rpTaaPending)
            {
                taa.feedbackMin = MultiCamSession.fbMin; taa.feedbackMax = MultiCamSession.fbMax;
                rpTaaPending = false;
            }
            if (taa.motionBlurStrength != blur) taa.motionBlurStrength = blur;
        }

        void ReplayTaaRestore()
        {
            rpTaaPending = false;
            var taa = MultiCamSession.taa;
            if (taa == null) return;
            taa.feedbackMin = MultiCamSession.fbMin; taa.feedbackMax = MultiCamSession.fbMax;
            taa.motionBlurStrength = MultiCamSession.mbStrength;
        }

        IEnumerator ReplayRender(double t, double real, CamPose pose, PostSpec post, float fog, bool reset, float blur, MultiCamFrameWriter w, string path, ReplayPassInfo info)
        {
            rp.T = t;
            ApplyPose(pose);
            MultiCamLook.post = post;
            MultiCamLook.fogDelta = fog;
            MultiCamLook.realTime = true;
            MultiCamLook.clock = rplay.realTime == "capture" ? loopRealStart + t : ReplayT0Real() + real;
            ReplayTaa(reset, blur, info);
            yield return null;
            TickFrozen();
            yield return replayEof;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (rpFrameT0 != 0) rpFrameMs.Add((now - rpFrameT0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            rpFrameT0 = now;
            renders++;
            if (info != null) info.renders++;
            if (w != null && agrab != null)
            {
                float r0 = Time.realtimeSinceStartup;
                if (rpGoldenPipe != null && w == rpGoldenPipe.writer) rpGoldenPipe.Grab(agrab, path);
                else agrab.Grab(w, path);
                msRead += (Time.realtimeSinceStartup - r0) * 1000.0; nRead++;
            }
            else if (w != null && grab != null)
            {
                float r0 = Time.realtimeSinceStartup;
                object data = grab.Grab();
                msRead += (Time.realtimeSinceStartup - r0) * 1000.0; nRead++;
                if (data != null) w.Add(path, data);
            }
        }

        Json.Obj ReplayHideSpan(int id, double from, double to)
        {
            var o = new Json.Obj();
            var set = rpHideSets[id];
            o["set"] = (double)id; o["from"] = from; o["to"] = to;
            o["objects"] = MultiCamPlan.Strs(set.paths); o["renderers"] = (double)set.renderers.Count;
            o["backfaces"] = MultiCamPlan.Strs(MultiCamHide.FlipPaths(set));
            return o;
        }

        Json.Obj ReplayHideJson(HashSet<int> used, List<object> spans)
        {
            var o = new Json.Obj();
            var objects = new List<string>();
            var unresolved = new List<string>();
            var flips = new List<string>();
            int rend = 0;
            foreach (int id in used)
            {
                var set = rpHideSets[id];
                foreach (var pth in set.paths) if (!objects.Contains(pth)) objects.Add(pth);
                foreach (var u in set.unresolved) if (!unresolved.Contains(u)) unresolved.Add(u);
                foreach (var f in MultiCamHide.FlipPaths(set)) if (!flips.Contains(f)) flips.Add(f);
                rend += set.renderers.Count;
            }
            o["layer"] = (double)MultiCamHide.Layer;
            o["objects"] = MultiCamPlan.Strs(objects);
            o["backfaces"] = MultiCamPlan.Strs(flips);
            o["unresolved"] = MultiCamPlan.Strs(unresolved);
            o["renderers"] = (double)rend;
            o["spans"] = spans;
            return o;
        }

        void ReplayEta()
        {
            int done = renders - rpEtaRenders0;
            if (done <= 0 || rpEtaTotal <= 0) { replay.eta = double.NaN; return; }
            double per = (Time.realtimeSinceStartup - rpEtaStart) / done;
            replay.eta = Math.Max(0, (rpEtaTotal - done) * per);
        }

        IEnumerator CameraPass(int k, int i)
        {
            var cs = spec.cameras[i];
            replay.phase = "pass"; replay.pass = k; replay.passes = passCams.Count; replay.camera = cs.name; replay.stepIndex = 0; replay.steps = nSteps;
            currentCam = i; capturedSteps = 0;
            state = "pass " + (k + 1) + "/" + passCams.Count + " " + cs.name;
            float start = Time.realtimeSinceStartup;
            int renders0 = renders;
            var w = ReplayWriter(i);
            ReplayPrestart(k + 1);
            yield return replayEof;
            int warm, copies, runs;
            var acts = ReplayActions(i, Time.frameCount + 1, true, out warm, out copies, out runs);
            Log(state + ": " + acts.Count + " action(s), " + warm + " warmup render(s), " + copies + " copied step(s), " + runs + " director run(s), first render at frame " + (Time.frameCount + 1));
            int first = acts.Count > 0 ? acts[0].idx : nWarm;
            rp.T = tl[first].T;
            rp.BeginPass(cs.name);
            rpFrameMs.Clear(); rpFrameT0 = 0;
            var info = rp.pass ?? new ReplayPassInfo();
            info.camera = cs.name;
            passInfos.Add(info);
            WriteRun(false);
            rpTaaPending = false;
            var post = cs.post;
            int done = 0, copied = 0;
            var hideSpans = new List<object>();
            int hideCur = -2;
            double hideFrom = double.NaN, hideLast = double.NaN;
            var hideUsed = new HashSet<int>();
            for (int a = 0; a < acts.Count; a++)
            {
                if (stopRequested) break;
                var act = acts[a];
                var e = tl[act.idx];
                if (act.kind == MultiCamReplayMath.ActCopy)
                {
                    if (ReplayCopy(act.src, i, e.step)) copied++;
                    done++;
                    step = e.step; frame = e.frame; capturedSteps = done; replay.stepIndex = done;
                    continue;
                }
                var pose = tlPoses[act.idx * nCams + i];
                float fog = post == null ? 0f : post.fogOffset + (post.fogMatch ? (pose.aim - pose.p).magnitude - e.aim : 0f);
                bool own = act.kind == MultiCamReplayMath.ActOwn;
                int hid = ReplayHideAt(act.idx, i);
                if (hid != hideCur)
                {
                    if (hideCur >= 0) hideSpans.Add(ReplayHideSpan(hideCur, hideFrom, hideLast));
                    hideCur = hid; hideFrom = e.T;
                    if (hid >= 0) hideUsed.Add(hid);
                }
                hideLast = e.T;
                if (hid >= 0) MultiCamHide.Apply(cam, rpHideSets[hid], "pass " + cs.name); else MultiCamHide.Restore();
                if (own) { step = e.step; frame = e.frame; }
                yield return ReplayRender(e.T, e.real, pose, post, fog, act.reset, ReplayBlur(i, e), own ? w : null, own ? ReplayFramePath(i, e.step) : null, info);
                if (own) { done++; capturedSteps = done; replay.stepIndex = done; }
                else info.warmups++;
                replay.t = e.T;
                ReplayEta();
                ReplayWriterCheck(w);
                Progress(false);
            }
            ReplayTaaRestore();
            MultiCamHide.Restore();
            if (hideCur >= 0) hideSpans.Add(ReplayHideSpan(hideCur, hideFrom, hideLast));
            MultiCamLook.post = null;
            MultiCamLook.fogDelta = 0f;
            Log("pass " + (k + 1) + "/" + passCams.Count + " " + cs.name + ": " + FrameTimeText() + "; " + rp.PhaseText());
            try { rp.EndPass(cs.name); } catch (Exception ex) { Error("end pass " + cs.name + ": " + ex.GetType().Name + ": " + ex.Message); }
            var endInfo = rp.pass;
            if (endInfo != null && endInfo != info)
            {
                info.endHash = endInfo.endHash; info.rngEnd = endInfo.rngEnd;
                if (string.IsNullOrEmpty(info.startHash)) { info.startHash = endInfo.startHash; info.rngStart = endInfo.rngStart; }
            }
            if (passInfos.Count > 1 && !string.IsNullOrEmpty(passInfos[0].startHash) && info.startHash != passInfos[0].startHash)
                Warn("pass " + cs.name + ": start hash " + info.startHash + " differs from pass 0 (" + passInfos[0].startHash + ")");
            if (agrab != null && w != null)
            {
                yield return agrab.Drain(60f);
                if (agrab.error != null) Error("pass " + cs.name + ": " + agrab.error);
                rbJson = agrab.StatsJson();
                Log("pass " + (k + 1) + "/" + passCams.Count + " " + cs.name + ": " + agrab.StatsLine());
            }
            int written = 0;
            if (w != null)
            {
                w.Close(false, 120000);
                written = w.written;
                ReplayWriterCheck(w);
            }
            float secs = Time.realtimeSinceStartup - start;
            int rend = renders - renders0;
            var st = new Json.Obj();
            st["camera"] = cs.name; st["seconds"] = Math.Round(secs, 2); st["renders"] = (double)rend; st["steps"] = (double)done;
            st["copied"] = (double)copied; st["runs"] = (double)runs; st["frames_written"] = (double)written;
            st["ms_per_render"] = MultiCamPlan.Num(rend > 0 ? secs * 1000.0 / rend : double.NaN);
            st["stopped"] = stopRequested;
            if (hideUsed.Count > 0) st["hide"] = ReplayHideJson(hideUsed, hideSpans);
            passStats.Add(st);
            Log("pass " + (k + 1) + "/" + passCams.Count + " " + cs.name + " done: " + done + "/" + nSteps + " step(s), " + rend + " render(s), " + copied + " copied, " + written + " frame(s) written, " + secs.ToString("0.0", IC) + " s, start hash " + info.startHash + ", end hash " + info.endHash);
            WriteRun(false);
        }

        // ---------------------------------------------------------------- golden test (3.4, R3)

        IEnumerator GoldenPass()
        {
            state = "load"; replay.phase = "load";
            string src = ReplaySource();
            rpSource = src;
            eventsCsv = new MultiCamCsv(Path.Combine(outDir, "events.csv"), ReplayEventsHeader);
            bool same = ReplaySameSession(src);
            rpFresh = !same;
            Log("golden source " + src + (same ? " (recorded in this session)" : " (fresh session)"));
            if (!same) yield return ReplayFreshSetup(src);
            if (stopRequested) { endReason = "stop"; yield break; }
            Frozen();
            var gp = SpecBind.Overlay<PlaySpec>(rplay, null) ?? new PlaySpec();
            gp.cullers = "recorded";
            if (!gp.given.Contains("cullers")) gp.given.Add("cullers");
            var lrep = new SpecReport();
            rp = ReplayLoad(src, gp, lrep);
            Absorb(lrep, "replay load");
            if (rp == null || !lrep.ok) throw new Exception("recording not loaded from " + src + ":\n" + lrep.Summary(20));
            ReplayInfo();
            var rec = rspec.record ?? new RecordSpec();
            int w = (int)rec.res.x, h = (int)rec.res.y;
            var hd = recInfo != null ? recInfo.header : null;
            if (hd != null && hd.screenW > 0 && hd.screenH > 0) { w = hd.screenW; h = hd.screenH; }
            if (w > 0 && h > 0) yield return ReplayResolution(w, h);
            outW = Screen.width; outH = Screen.height;
            yield return ReplayKeepRecorded();
            if (stopRequested) { endReason = "stop"; yield break; }
            yield return ReplayFreeze();
            var brep = new SpecReport();
            rpBegun = true;
            rp.Begin(null, brep);
            Absorb(brep, "replay begin");
            if (!brep.ok) throw new Exception("replay did not begin:\n" + brep.Summary(20));
            ReplayPacing();
            yield return ReplayStreamingSettle();
            yield return GoldenWriter();
            var gw = rpGoldenWriter;
            string noiseDir = Path.Combine(outDir, ReplayConst.NoiseFolder);
            Directory.CreateDirectory(noiseDir);
            int n = 0;
            try { n = rp.sampleCount; } catch (Exception e) { RateError("golden samples", e); }
            int every = replayArgs.every > 0 ? replayArgs.every : Math.Max(1, rec.captureEvery);
            var grabAt = MultiCamReplayMath.NoiseGrabAt(GoldenSettle, GoldenNoise);
            int noiseRenders = MultiCamReplayMath.NoiseRenders(GoldenSettle, GoldenNoise);
            rpEtaTotal = noiseRenders + n;
            rpEtaStart = Time.realtimeSinceStartup;
            rpEtaRenders0 = renders;
            loopRealStart = Time.realtimeSinceStartup;
            replay.phase = "noise"; replay.passes = 1; replay.pass = 0; replay.camera = "game"; replay.steps = n; replay.stepIndex = 0;
            state = "golden noise";
            var g = new GameCamState();
            double tA = n > 0 ? rp.SampleTime(0) : 0;
            float mb = MultiCamSession.mbStrength;
            yield return replayEof;
            rp.T = tA;
            rp.BeginPass("golden");
            var info = rp.pass ?? new ReplayPassInfo();
            info.camera = "golden";
            passInfos.Add(info);
            var noise = new List<object>();
            var pose = GoldenPose(tA, g);
            double realA = 0;
            try { realA = rp.RealTimeAt(tA); } catch { }
            for (int r = 0; r < noiseRenders; r++)
            {
                if (stopRequested) break;
                int k = Array.IndexOf(grabAt, r);
                string path = k >= 0 ? Path.Combine(noiseDir, "n" + GoldenNoise[k].ToString("D3", IC) + ".jpg") : null;
                yield return ReplayRender(tA, realA, pose, null, 0f, r == 0, mb, k >= 0 ? gw : null, path, info);
                info.warmups++;
                if (k >= 0)
                {
                    var nj = new Json.Obj();
                    nj["file"] = ReplayConst.NoiseFolder + "/n" + GoldenNoise[k].ToString("D3", IC) + ".jpg";
                    nj["after_settle"] = (double)GoldenNoise[k]; nj["render"] = (double)(r + 1); nj["sample"] = 0.0; nj["t"] = tA;
                    noise.Add(nj);
                }
                ReplayEta();
                Progress(false);
            }
            replay.phase = "golden"; state = "golden";
            int grabbed = 0;
            for (int s = 0; s < n && !stopRequested; s++)
            {
                double t = rp.SampleTime(s);
                pose = GoldenPose(t, g);
                double real = t;
                try { real = rp.RealTimeAt(t); } catch { }
                bool take = s % every == 0;
                step = s;
                yield return ReplayRender(t, real, pose, null, 0f, false, mb, take ? gw : null, take ? Path.Combine(outDir, s.ToString("D5", IC) + ".jpg") : null, info);
                if (take) grabbed++;
                replay.stepIndex = s + 1; replay.t = t; capturedSteps = grabbed;
                ReplayEta();
                ReplayWriterCheck(gw);
                Progress(false);
            }
            ReplayTaaRestore();
            try { rp.EndPass("golden"); } catch (Exception ex) { Error("end pass golden: " + ex.GetType().Name + ": " + ex.Message); }
            var endInfo = rp.pass;
            if (endInfo != null && endInfo != info) { info.endHash = endInfo.endHash; info.rngEnd = endInfo.rngEnd; }
            if (rpGoldenPipe != null && agrab != null) yield return agrab.Drain(60f);
            string genc = rpGoldenEncoder;
            int gwritten = GoldenCloseWriter(false);
            ReplayWriterCheck(gw);
            var gj = new Json.Obj();
            gj["source"] = src; gj["samples"] = (double)n; gj["every"] = (double)every; gj["grabbed"] = (double)grabbed; gj["written"] = (double)gwritten;
            gj["encoder"] = genc;
            gj["settle"] = (double)GoldenSettle; gj["noise"] = noise; gj["cullers"] = "recorded";
            gj["screen"] = new List<object> { (double)outW, (double)outH };
            gj["frames"] = "<sample:D5>.jpg (compare with record/game/<sample:D5>.jpg)";
            goldenJson = gj;
            Log("golden: " + n + " sample(s) rendered, " + grabbed + " grabbed (every " + every + "), " + noise.Count + " noise frame(s)");
            endReason = stopRequested ? "stop" : "done";
            ReplayEndReplayer();
        }

        // Golden frames use the same JPEG path as the record's game frames (native readback + ffmpeg when available), so the
        // record and golden sides of replaydiff come from one encoder; GoldenCloseWriter renames the ffmpeg sequence.
        IEnumerator GoldenWriter()
        {
            if (grab != null) { try { grab.Dispose(); } catch { } grab = null; }
            rpGoldenPipe = null;
            var o = spec.output;
            if (o.readback != "readpixels")
            {
                var g = new MultiCamAsyncGrab();
                yield return g.Open(Screen.width, Screen.height);
                string why = g.ready ? null : g.error;
                if (g.ready)
                {
                    var jp = MultiCamReplayJpegPipe.Open("golden", outDir, g.w, g.h, g.pixFmt, o, out why);
                    if (jp != null) { agrab = g; rpGoldenPipe = jp; rpGoldenWriter = jp.writer; rpGoldenEncoder = jp.encoder; Log("golden frames: " + jp.encoder + ": " + o.ffmpeg + " " + jp.args); yield break; }
                    g.Close();
                }
                if (o.readback == "native") throw new Exception("golden with output.readback native: " + why);
                Warn("golden: native readback not used (" + why + "), ReadPixels + EncodeToJPG on the game thread");
            }
            grab = new MultiCamGrab(Screen.width, Screen.height, true, Mathf.Clamp(o.jpeg, 1, 100));
            rpGoldenWriter = new MultiCamFrameWriter("golden", Math.Max(1, o.queue));
            rpGoldenEncoder = "Unity EncodeToJPG " + Mathf.Clamp(o.jpeg, 1, 100) + " (ReadPixels on the game thread)";
        }

        int GoldenCloseWriter(bool kill)
        {
            var w = rpGoldenWriter;
            if (w == null) return 0;
            rpGoldenWriter = null;
            w.Close(kill, 120000);
            var p = rpGoldenPipe;
            if (p == null) return w.written;
            rpGoldenPipe = null;
            if (agrab != null)
            {
                if (agrab.error != null) errors.Add("golden readback: " + agrab.error);
                try { Log("golden frames: " + agrab.StatsLine()); } catch { }
                agrab.Close();
                agrab = null;
            }
            string err;
            int n = p.Finish(out err);
            if (err != null) errors.Add("golden frames: " + err);
            Log("golden frames: " + n + " of " + p.targets.Count + " JPEG(s) written by ffmpeg" + (err != null ? " (" + err + ")" : ""));
            return n;
        }

        CamPose GoldenPose(double t, GameCamState g)
        {
            bool ok = false;
            try { ok = rp.GameCam(t, g); } catch (Exception e) { RateError("golden camera", e); }
            if (!ok || !g.ok)
            {
                var tr = cam.transform;
                return CamPose.Make(tr.position, tr.rotation, cam.fieldOfView, MultiCamConst.GameAimDistance);
            }
            var p = g.rendered;
            if (p.fov <= 0) p.fov = recInfo != null && recInfo.header != null && recInfo.header.gameFov > 0 ? recInfo.header.gameFov : MultiCamSession.camFov;
            p.ok = true;
            return p;
        }

        void ReplayEndReplayer()
        {
            var r = rp;
            if (r == null || !rpBegun || rpEnded) return;
            rpEnded = true;
            ReplayInfo();
            var rep = new SpecReport();
            try { r.End(rep); }
            finally { Absorb(rep, "replay end"); }
            string st;
            try { st = r.Status(); } catch { st = ""; }
            Log("replay end: restored" + (st.Length > 0 ? " (" + st + ")" : ""));
        }

        // ---------------------------------------------------------------- run.json, finish

        List<object> ReplaySegmentsJson()
        {
            var sl = new List<object>();
            int ns = plan.segs.Count;
            for (int i = 0; i < ns; i++)
            {
                var g = plan.segs[i];
                var s = new Json.Obj();
                s["index"] = (double)i;
                s["from"] = g.spec.from != null ? g.spec.from.ToString() : "0";
                s["from_resolved"] = i == 0 ? (object)0.0 : MultiCamPlan.Num(i < segFrom.Length ? segFrom[i] : double.NaN);
                s["speed"] = (double)g.speed; s["samples"] = (double)g.captured; s["shutter"] = (double)g.spec.shutter;
                s["steps_per_frame"] = (double)Math.Max(1, g.captured); s["label"] = g.label;
                int steps = 0, rate = 0;
                var frames = new HashSet<int>();
                double ta = double.NaN, tb = double.NaN, oa = double.NaN, ob = double.NaN;
                if (tl != null)
                    for (int idx = nWarm; idx < tl.Count; idx++)
                    {
                        var e = tl[idx];
                        if (e.seg != i) continue;
                        steps++; frames.Add(e.frame);
                        if (rate == 0) rate = e.rate;
                        if (double.IsNaN(ta)) { ta = e.T; oa = e.o; }
                        tb = e.T; ob = e.o;
                    }
                s["rate"] = (double)rate;
                s["steps"] = (double)steps; s["frames"] = (double)frames.Count;
                s["t_from"] = MultiCamPlan.Num(ta); s["t_to"] = MultiCamPlan.Num(tb); s["out_from"] = MultiCamPlan.Num(oa); s["out_to"] = MultiCamPlan.Num(ob);
                sl.Add(s);
            }
            return sl;
        }

        Json.Obj ReplayEventTimesJson()
        {
            var o = new Json.Obj();
            foreach (var e in spec.events) if (!string.IsNullOrEmpty(e.name)) o[e.name] = MultiCamPlan.Num(ReplayEventTime(e.name));
            foreach (var n in ReplayRecordedOnly()) if (!o.ContainsKey(n)) o[n] = MultiCamPlan.Num(ReplayEventTime(n));
            return o;
        }

        // Names of kind "event" rows of the recording that no spec event declares (SceneRecorder.PostEvent, srec event), in
        // first-fire order.
        List<string> ReplayRecordedOnly()
        {
            var r = new List<string>();
            if (rp == null || rp.tracks == null) return r;
            foreach (var ev in rp.tracks.events)
            {
                if (ev == null || ev.kind != "event" || string.IsNullOrEmpty(ev.name) || r.Contains(ev.name)) continue;
                if (spec.FindEvent(ev.name) != null) continue;
                r.Add(ev.name);
            }
            return r;
        }

        List<object> ReplayEventsJson()
        {
            var r = new List<object>();
            foreach (var e in spec.events)
            {
                var o = new Json.Obj();
                o["name"] = e.name; o["type"] = e.type;
                var f = new List<object>();
                if (rp != null && rp.tracks != null)
                    foreach (var ev in rp.tracks.events)
                        if (ev != null && ev.name == e.name && (ev.kind == "event" || ev.kind == "event+")) f.Add(ev.time);
                double t = ReplayEventTime(e.name);
                if (f.Count == 0 && !double.IsNaN(t)) f.Add(t);
                o["fired"] = f;
                o["time"] = MultiCamPlan.Num(t);
                o["predicted"] = null;
                o["armed"] = true;
                o["source"] = "recording";
                r.Add(o);
            }
            foreach (var n in ReplayRecordedOnly())
            {
                var o = new Json.Obj();
                o["name"] = n; o["type"] = "posted";
                var f = new List<object>();
                string detail = "";
                foreach (var ev in rp.tracks.events)
                    if (ev != null && ev.kind == "event" && ev.name == n) { f.Add(ev.time); if (detail.Length == 0) detail = ev.detail ?? ""; }
                o["fired"] = f;
                o["time"] = MultiCamPlan.Num(ReplayEventTime(n));
                o["predicted"] = null;
                o["armed"] = false;
                o["source"] = "recording (posted, not a spec event)";
                o["detail"] = detail;
                r.Add(o);
            }
            return r;
        }

        object ReplayMonitorJson()
        {
            var r = rp;
            if (r == null) return null;
            try
            {
                var m = r.GetType().GetMethod("MonitorJson", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                return m != null ? m.Invoke(r, null) as Json.Obj : null;
            }
            catch { return null; }
        }

        static object ReplayJson(object x)
        {
            if (x == null) return null;
            if (x is string || x is bool) return x;
            if (x is int) return (double)(int)x;
            if (x is long) return (double)(long)x;
            if (x is float) return MultiCamPlan.Num((float)x);
            if (x is double) return MultiCamPlan.Num((double)x);
            var ls = x as List<string>;
            if (ls != null) return MultiCamPlan.Strs(ls);
            var type = x.GetType();
            if (type.IsPrimitive || type.IsEnum || type.IsValueType) return Convert.ToString(x, CultureInfo.InvariantCulture);
            var o = new Json.Obj();
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) o[f.Name] = ReplayJson(f.GetValue(x));
            return o;
        }

        void ReplayRunJson(Json.Obj o, bool final)
        {
            try
            {
                o["mode"] = mode;
                o["take"] = takeDir;
                o["replay_format"] = 1.0;
                var rj = new Json.Obj();
                rj["phase"] = replay.phase; rj["source"] = rpSource; rj["fresh_session"] = rpFresh; rj["summary"] = replay.summary;
                if (rplay != null)
                {
                    rj["look"] = rplay.look; rj["order"] = rplay.order; rj["interp"] = rplay.interp; rj["particles"] = rplay.particles;
                    rj["warmup"] = (double)rplay.warmup; rj["motionBlur"] = rplay.motionBlur; rj["shutterAngle"] = (double)rplay.shutterAngle;
                    rj["cullers"] = mode == ReplayModes.Golden ? "recorded" : rplay.cullers; rj["realTime"] = rplay.realTime; rj["ramp"] = (double)rplay.ramp; rj["copyHolds"] = rplay.copyHolds;
                }
                if (passCams.Count > 0)
                {
                    var pc = new List<object>();
                    foreach (int i in passCams) pc.Add(spec.cameras[i].name);
                    rj["cams"] = pc;
                }
                o["replay"] = rj;
                if (outW > 0 && outH > 0) o["screen"] = new List<object> { (double)outW, (double)outH };
                if (recorder != null || recHeader != null) { o["record"] = RecordJson(); o["pacing"] = RecordPacingJson(); }
                if (rp != null) ReplayInfo();
                if (recInfo != null) o["recording"] = ReplayJson(recInfo);
                if (viewNext != null) o["views"] = ReplayViewsJson();
                var mon = ReplayMonitorJson();
                if (mon != null) o["monitor"] = mon;
                if (tl != null)
                {
                    o["segments"] = ReplaySegmentsJson();
                    var tj = new Json.Obj();
                    tj["from"] = tlFrom; tj["to"] = tlTo; tj["steps"] = (double)nSteps; tj["frames"] = (double)nFrames; tj["warmup_slots"] = (double)nWarm;
                    tj["fps"] = (double)spec.timing.fps; tj["ramp"] = (double)rplay.ramp; tj["duration"] = replay.duration;
                    o["timeline"] = tj;
                    o["steps"] = (double)nSteps;
                    o["frames"] = (double)nFrames;
                    o["captured_steps"] = (double)nSteps;
                    if (rp != null) o["events"] = ReplayEventsJson();
                }
                var pl = new List<object>();
                foreach (var p in passInfos) pl.Add(ReplayJson(p));
                o["passes"] = pl;
                o["pass_stats"] = new List<object>(passStats);
                if (rbJson != null) o["readback"] = rbJson;
                if (rplanJson != null) o["plan"] = rplanJson;
                if (goldenJson != null) o["golden"] = goldenJson;
                o["renders"] = (double)renders;
            }
            catch (Exception e) { DevLog.Error("mcam replay run.json", e); }
        }

        // Finish keeps the game frozen (MultiCamFreeze) when the run left it frozen (record end, replay or golden freeze), unless
        // the replayer reloads the savepoint at End (play.restore reload).
        internal bool ReplayEndsFrozen()
        {
            if (Time.timeScale != 0f) return false;
            return !(rpBegun && rplay != null && rplay.restore == "reload");
        }

        void ReplayFinish(string how, bool kill)
        {
            if (replayFinished) return;
            replayFinished = true;
            try { AudioCapture.Stop("mcam " + mode + ": " + how); } catch (Exception e) { DevLog.Error("mcam replay finish audio", e); }
            try { RecordCloseGameWriter(kill || aborted); } catch (Exception e) { DevLog.Error("mcam replay finish game writer", e); }
            try { GoldenCloseWriter(kill || aborted); } catch (Exception e) { DevLog.Error("mcam replay finish golden writer", e); }
            try { ReplayTaaRestore(); } catch (Exception e) { DevLog.Error("mcam replay finish taa", e); }
            try { MultiCamHide.Restore(); } catch (Exception e) { DevLog.Error("mcam replay finish hide", e); }
            try { ReplayDropPrestarted(); } catch (Exception e) { DevLog.Error("mcam replay finish prestarted pipes", e); }
            try { ReplayEndReplayer(); } catch (Exception e) { DevLog.Error("mcam replay finish replayer", e); }
            try { if (recorder != null && !recEnded) RecordEndRecorder(); } catch (Exception e) { DevLog.Error("mcam replay finish recorder", e); }
            try { RecordRestore(); } catch (Exception e) { DevLog.Error("mcam replay finish record prep", e); }
            try { RecordUnhook(); } catch (Exception e) { DevLog.Error("mcam replay finish hooks", e); }
            replay.phase = "end";
            try { replay.summary = ReplaySummary(); } catch (Exception e) { replay.summary = mode + ": summary failed: " + e.Message; }
        }

        string ReplaySummary()
        {
            var sb = new StringBuilder();
            sb.Append(mode);
            if (recorder != null)
                sb.Append(": recorded ").Append(recorder.samples).Append(" sample(s), ").Append(recorder.objects).Append(" object(s), ").Append((recorder.bytes / 1e6).ToString("0.#", IC))
                  .Append(" MB, ").Append(recDuration.ToString("0.##", IC)).Append(" s (").Append(recEndReason).Append("), drift max ").Append(recDriftMax.ToString("0.#", IC)).Append(" ms");
            if (nSteps > 0) sb.Append(recorder != null ? "; " : ": ").Append("timeline ").Append(nSteps).Append(" step(s) / ").Append(nFrames).Append(" frame(s)");
            if (passInfos.Count > 0) sb.Append(", ").Append(passInfos.Count).Append(" pass(es)");
            if (renders > 0) sb.Append(", ").Append(renders).Append(" render(s)");
            if (goldenJson != null) sb.Append(", golden ").Append(goldenJson.Num("grabbed").ToString("0", IC)).Append(" frame(s)");
            if (rplanJson != null && mode == ReplayModes.Passes) sb.Append(", est ").Append(rplanJson.Num("est_minutes").ToString("0.#", IC)).Append(" min");
            if (recInfo != null) sb.Append(", conflicts ").Append(recInfo.conflicts).Append(", unresolved ").Append(recInfo.unresolved);
            return sb.ToString();
        }
    }

    // JPEG frames through the native readback and one ffmpeg image2 pipe (record.captureGame grabs and golden frames): the
    // game thread only queues the GPU copy, ffmpeg (mjpeg, full-range 4:4:4) writes <dir>/_seq_<n>.jpg in grab order, and
    // Finish renames frame n to the target path given at its grab (record/game/<sample:D5>.jpg, golden/<sample:D5>.jpg,
    // golden/noise/n<k:D3>.jpg), after the writer has closed and ffmpeg has exited.
    internal sealed class MultiCamReplayJpegPipe
    {
        const string Prefix = "_seq_";
        public MultiCamFrameWriter writer;
        public readonly List<string> targets = new List<string>();
        public string dir = "", args = "", encoder = "";

        public static int Quality(int jpeg) { return jpeg >= 98 ? 1 : jpeg >= 90 ? 2 : jpeg >= 80 ? 3 : jpeg >= 60 ? 5 : 8; }

        public static MultiCamReplayJpegPipe Open(string name, string dir, int w, int h, string pixFmt, OutputSpec o, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(dir);
                foreach (var f in Directory.GetFiles(dir, Prefix + "*.jpg")) { try { File.Delete(f); } catch { } }
                int q = Quality(o.jpeg);
                var p = new MultiCamReplayJpegPipe { dir = dir };
                p.args = "-hide_banner -loglevel error -y -f rawvideo -pix_fmt " + pixFmt + " -s " + w + "x" + h + " -framerate 30 -i - -an -c:v mjpeg -q:v " + q +
                         " -qmin 1 -pix_fmt yuvj444p -f image2 -start_number 0 \"" + Path.Combine(dir, Prefix + "%05d.jpg") + "\"";
                p.writer = MultiCamFrameWriter.Pipe(name, Math.Max(2, o.queue), o.ffmpeg, p.args);
                p.encoder = "native readback + ffmpeg mjpeg q" + q + " yuvj444p off the game thread";
                return p;
            }
            catch (Exception e) { error = "ffmpeg JPEG pipe: " + e.GetType().Name + ": " + e.Message; return null; }
        }

        public void Grab(MultiCamAsyncGrab g, string target)
        {
            targets.Add(target ?? "");
            g.Grab(writer, null);
        }

        public int Finish(out string error)
        {
            error = null;
            int n = 0, missing = 0;
            for (int k = 0; k < targets.Count; k++)
            {
                string src = Path.Combine(dir, Prefix + k.ToString("D5", CultureInfo.InvariantCulture) + ".jpg");
                string dst = targets[k];
                if (!File.Exists(src)) { missing++; continue; }
                if (string.IsNullOrEmpty(dst)) { try { File.Delete(src); } catch { } continue; }
                try
                {
                    string d = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(src, dst);
                    n++;
                }
                catch (Exception e) { if (error == null) error = "rename " + src + " -> " + dst + ": " + e.Message; }
            }
            if (missing > 0 && error == null) error = missing + " of " + targets.Count + " frame(s) missing from ffmpeg's output";
            return n;
        }
    }

    // ------------------------------------------------------------------ MultiCamReplay pure math (System types only; unit-tested offline under .NET 9)

    internal sealed class MultiCamReplayEntry
    {
        public double T, o, real;                                    // record time, output time, recorded wall seconds since t0Real
        public long logic;
        public int step = -1, frame = -1, sub = -1, seg, samples = 1, rate = 30, fixedSteps;
        public float speed = 1f, aim = 11f;
    }

    internal struct MultiCamReplayAct
    {
        public int idx, src;
        public byte kind;
        public bool reset;
    }

    internal static class MultiCamReplayMath
    {
        public const byte ActWarm = 0, ActOwn = 1, ActCopy = 2;
        public const int JitterCycle = 16;

        internal sealed class Piece { public double a, b, o, d; public float s; public int seg; }

        public static int SegAt(double[] start, double t)
        {
            int pick = 0;
            for (int i = 1; i < start.Length; i++) if (!double.IsNaN(start[i]) && t >= start[i] - 1e-6) pick = i;
            return pick;
        }

        public static List<Piece> Pieces(double[] start, float[] speed, double from, double to)
        {
            var cuts = new List<double> { from, to };
            for (int i = 1; i < start.Length; i++) if (!double.IsNaN(start[i]) && start[i] > from + 1e-9 && start[i] < to - 1e-9) cuts.Add(start[i]);
            cuts.Sort();
            var r = new List<Piece>();
            for (int k = 0; k + 1 < cuts.Count; k++)
            {
                double a = cuts[k], b = cuts[k + 1];
                if (b - a < 1e-9) continue;
                int si = SegAt(start, a);
                float s = Math.Max(1e-6f, speed[si]);
                if (r.Count > 0 && r[r.Count - 1].s == s) { r[r.Count - 1].b = b; continue; }
                r.Add(new Piece { a = a, b = b, s = s, seg = si });
            }
            double o = 0;
            foreach (var p in r) { p.o = o; p.d = (p.b - p.a) / p.s; o += p.d; }
            return r;
        }

        public static double RampSpeed(List<Piece> pieces, double ramp, double o)
        {
            if (pieces.Count == 0) return 1.0;
            int k = pieces.Count - 1;
            for (int j = 0; j < pieces.Count; j++) if (o < pieces[j].o + pieces[j].d) { k = j; break; }
            var p = pieces[k];
            double s = p.s;
            if (ramp <= 0) return s;
            if (k > 0)
            {
                var q = pieces[k - 1];
                double w = Math.Min(ramp, Math.Min(q.d, p.d));
                if (w > 1e-9 && o < p.o + w / 2) s = q.s + (p.s - q.s) * Clamp01((o - (p.o - w / 2)) / w);
            }
            if (k + 1 < pieces.Count)
            {
                var q = pieces[k + 1];
                double w = Math.Min(ramp, Math.Min(p.d, q.d));
                if (w > 1e-9 && o > q.o - w / 2) s = p.s + (q.s - p.s) * Clamp01((o - (q.o - w / 2)) / w);
            }
            return s;
        }

        static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        public static int RateOf(double fps, double speed, int samples, float shutter)
        {
            double s = Math.Max(1e-6, speed);
            if (samples <= 1) return Math.Max(1, (int)Math.Round(fps / s));
            double ex = Math.Max(1e-9, shutter / 360.0) * s / fps;
            return Math.Max(1, (int)Math.Round(samples / ex));
        }

        public static List<MultiCamReplayEntry> Timeline(double from, double to, double fps, double ramp, double[] start, float[] speed, int[] samples, float[] shutter, int warmup,
                                                         out int nWarm, out int nSteps, out int nFrames)
        {
            if (fps <= 0) fps = 30;
            var pieces = Pieces(start, speed, from, to);
            var steps = new List<MultiCamReplayEntry>();
            double t = from;
            int f = 0;
            while (t <= to + 1e-9 && f < 1000000)
            {
                int si = SegAt(start, t);
                double o = f / fps;
                double s = ramp > 0 && pieces.Count > 1 ? RampSpeed(pieces, ramp, o + 0.5 / fps) : speed[si];
                if (s < 1e-6) s = 1e-6;
                int m = Math.Max(1, samples[si]);
                double ex = m > 1 ? Math.Max(1e-9, shutter[si] / 360.0) * s / fps : s / fps;
                int rate = RateOf(fps, s, m, shutter[si]);
                for (int i = 0; i < m; i++)
                {
                    var e = new MultiCamReplayEntry();
                    e.T = m > 1 ? Math.Max(0, t - ex + (i + 1) * ex / m) : t;
                    e.o = Math.Max(0, o - (t - e.T) / s);
                    e.step = steps.Count; e.frame = f; e.sub = i; e.seg = si; e.samples = m; e.speed = (float)s; e.rate = rate;
                    steps.Add(e);
                }
                t += s / fps;
                f++;
            }
            nSteps = steps.Count;
            nFrames = f;
            nWarm = Math.Max(0, warmup) + JitterCycle - 1;
            var r = new List<MultiCamReplayEntry>(nWarm + nSteps);
            var first = nSteps > 0 ? steps[0] : null;
            int m0 = first != null ? first.samples : 1;
            double d0 = first != null ? first.speed / (fps * m0) : 1.0 / fps;
            for (int k = 0; k < nWarm; k++)
            {
                int back = nWarm - k;
                var e = new MultiCamReplayEntry();
                e.T = Math.Max(0, (first != null ? first.T : from) - back * d0);
                e.o = Math.Max(0, (first != null ? first.o : 0) - back / (fps * m0));
                e.step = -1; e.frame = -1; e.sub = -1;
                if (first != null) { e.seg = first.seg; e.samples = first.samples; e.speed = first.speed; e.rate = first.rate; }
                r.Add(e);
            }
            r.AddRange(steps);
            return r;
        }

        public static int JitterPad(int renders, int warmup)
        {
            int v = (renders + Math.Max(0, warmup)) % JitterCycle;
            if (v < 0) v += JitterCycle;
            return (JitterCycle - v) % JitterCycle;
        }

        public static List<MultiCamReplayAct> Actions(int nWarm, int count, int warmup, int startRenders, Func<int, int> copyAt, out int warm, out int copies, out int runs)
        {
            var acts = new List<MultiCamReplayAct>();
            warm = 0; copies = 0; runs = 0;
            if (count <= nWarm) return acts;
            int w = Math.Max(0, warmup);
            int wp = Math.Min(nWarm, w + JitterPad(startRenders, w));
            bool live = copyAt(nWarm) < 0;
            if (live)
                for (int k = nWarm - wp; k < nWarm; k++) { acts.Add(new MultiCamReplayAct { idx = k, kind = ActWarm, reset = k == nWarm - wp, src = -1 }); warm++; }
            bool first = true;
            for (int idx = nWarm; idx < count; idx++)
            {
                int s = copyAt(idx);
                if (s >= 0)
                {
                    acts.Add(new MultiCamReplayAct { idx = idx, kind = ActCopy, src = s });
                    copies++; live = false; first = false;
                    continue;
                }
                bool reset = false;
                if (!live)
                {
                    int k0 = Math.Max(0, idx - w);
                    for (int k = k0; k < idx; k++) { acts.Add(new MultiCamReplayAct { idx = k, kind = ActWarm, reset = k == k0, src = -1 }); warm++; }
                    reset = k0 == idx;
                    runs++;
                    live = true;
                }
                else if (first && wp == 0) reset = true;
                acts.Add(new MultiCamReplayAct { idx = idx, kind = ActOwn, reset = reset, src = -1 });
                first = false;
            }
            return acts;
        }

        public static float BlurStrength(string mode, float shutterAngle, float game, double speed, double fps, int samples, bool keepGame)
        {
            if (samples > 1) return 0f;
            if (keepGame) return game;
            string m = mode ?? "game";
            if (m == "off") return 0f;
            if (m == "angle") return Math.Max(0f, shutterAngle / 360f);
            float v;
            if (m != "game" && float.TryParse(m, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return Math.Max(0f, v);
            double d = speed / (Math.Max(1e-6, fps) * Math.Max(1, samples));
            if (d <= 1e-9) return game;
            return (float)Math.Min(16.0, game * (1.0 / 60.0) / d);
        }

        public static int[] NoiseGrabAt(int settle, int[] offsets)
        {
            var r = new int[offsets.Length];
            for (int i = 0; i < offsets.Length; i++) r[i] = Math.Max(0, settle - 1 + offsets[i]);
            return r;
        }

        public static int NoiseRenders(int settle, int[] offsets)
        {
            int max = 0;
            foreach (int o in offsets) if (o > max) max = o;
            return Math.Max(1, settle + max);
        }

        public const int LimitWait = 0, LimitLate = 1, LimitAnchor = 2;

        // Frame limiter grid (2.3.4): the target of this frame is prev + period; a frame up to `slack` past it keeps the grid
        // (no wait, the next frame waits less), a later one or the first call re-anchors the grid at `now`.
        public static double LimiterTarget(double prev, double now, double period, double slack, out int kind)
        {
            if (double.IsNaN(prev) || double.IsInfinity(prev)) { kind = LimitAnchor; return now; }
            double next = prev + period;
            if (now <= next) { kind = LimitWait; return next; }
            if (now - next <= slack) { kind = LimitLate; return next; }
            kind = LimitAnchor;
            return now;
        }

        public static bool PacingHolds(List<float> ms, double periodMs, double lo, double hi, out double median, out double within)
        {
            median = double.NaN; within = double.NaN;
            int n = ms.Count;
            if (n == 0) return false;
            var a = ms.ToArray();
            Array.Sort(a);
            median = n % 2 == 1 ? a[n / 2] : 0.5 * (a[n / 2 - 1] + a[n / 2]);
            int w = 0;
            foreach (var v in a) if (v >= lo && v <= hi) w++;
            within = w / (double)n;
            return n >= 20 && Math.Abs(median - periodMs) <= 0.5 && within >= 0.9;
        }

        // Game time lost in one frame to the maximumDeltaTime clamp: the real interval minus the game interval when the game
        // interval is the clamp (a hitch); 0 otherwise.
        public static double HitchLoss(double realDt, double gameDt, double maxDt)
        {
            if (maxDt <= 0 || gameDt < maxDt - 1e-4 || realDt <= gameDt) return 0.0;
            return realDt - gameDt;
        }

        public static double[] FrameStats(List<float> ms, double lo, double hi)
        {
            int n = ms.Count;
            if (n == 0) return new[] { 0.0, double.NaN, double.NaN, double.NaN, double.NaN };
            var a = ms.ToArray();
            Array.Sort(a);
            double sum = 0;
            int within = 0;
            foreach (var v in a) { sum += v; if (v >= lo && v <= hi) within++; }
            int p = Math.Min(n - 1, Math.Max(0, (int)Math.Ceiling(0.99 * n) - 1));
            return new[] { (double)n, sum / n, (double)a[p], (double)a[n - 1], within / (double)n };
        }
    }
}
