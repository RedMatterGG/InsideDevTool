using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace InsideDev.Det
{
    // DetCommands.cs: "det" bridge / console commands and the run driver of the input replay determinism test
    // (REPLAY_DESIGN Section 7). Loaded from _mod\InsideDev.Det.dll; never touches Playback.SessionManager.
    // A run: lock frames, install FilmInput, load 7 0; at prepareLoad and allScenesLoaded reset the RNGs; k0 = stepCount at
    // allScenesLoaded, then prep, step-indexed cues from RuntimeCallbacks.fixedUpdate, one log row per logic step, stop at
    // kmax or baleLand + 210 (a replay runs exactly as many steps as its source unless kmax= is given), restore everything.
    public sealed class DetExtension : IInsideDevExtension, IDisposable
    {
        public string Name { get { return "Det (input replay determinism test, " + DetRun.state + ")"; } }

        public void Start(DevCore core) { DetRun.core = core; }

        public void Tick(DevCore core)
        {
            DetRun.core = core;
            try { DetRun.Tick(); } catch (Exception e) { DevLog.Error("[det] tick", e); }
        }

        public string Command(List<string> a, DevCore core)
        {
            if (a == null || a.Count == 0 || a[0].ToLowerInvariant() != "det") return null;
            DetRun.core = core;
            return DetCommands.Run(a);
        }

        public void Dispose() { DetRun.Finish("game quit"); }
    }

    public static class DetCommands
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public const string Help =
            "det prep [anim=default|always] [cull=live|freeze] [stream=live|keep] [drip=on|off] | det prep full | det prep default\n" +
            "det cue <k> <command> | det cues [clear]\n" +
            "det run <name> <out> [kmax] [kmax=n] [fps=60] [cam=...]\n" +
            "det replay <source> <out> [cam=x,y,z,yaw,pitch,fov | cam=x y z yaw pitch fov | cam=<run>] [kmax=n] [fps=60]\n" +
            "det status | det zone | det abort\n" +
            "<out>: absolute folder, or relative to _mod/det; <source> / cam=<run>: a folder path, or a run folder next to <out>";
        static readonly List<DetCue> track = new List<DetCue>();

        public static List<DetCue> Track { get { return track; } }

        public static string Run(List<string> a)
        {
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "prep": return DetPrep.Set(a);
                case "cue": return AddCue(a);
                case "cues":
                    if (a.Count > 2 && a[2].ToLowerInvariant() == "clear")
                    {
                        if (DetRun.Busy) return "busy: det " + DetRun.state + " (the running track is a copy; clear after the run)";
                        track.Clear();
                        return "cue track cleared";
                    }
                    return CueText(track);
                case "run": return DetRun.Start(false, a);
                case "replay": return DetRun.Start(true, a);
                case "status": return DetRun.Status();
                case "zone": return DetPrep.ZoneReport();
                case "abort":
                case "stop":
                    return DetRun.Busy ? DetRun.Finish("abort (det " + sub + ")") : "det not running (" + DetRun.state + ")";
                case "help": return Help;
            }
            return "unknown det command '" + sub + "'\n" + Help;
        }

        static string AddCue(List<string> a)
        {
            long k;
            if (a.Count < 4 || !long.TryParse(a[2], NumberStyles.Integer, IC, out k) || k < 0) return "usage: det cue <k >= 0> <command>";
            if (DetRun.Busy) return "busy: det " + DetRun.state + " (cues are copied at run start)";
            var cmd = string.Join(" ", a.GetRange(3, a.Count - 3).ToArray());
            int at = track.Count;
            while (at > 0 && track[at - 1].k > k) at--;
            track.Insert(at, new DetCue { k = k, cmd = cmd });
            return "cue k " + k + ": " + cmd + "\n" + CueText(track);
        }

        public static string CueText(List<DetCue> list)
        {
            if (list.Count == 0) return "cue track empty";
            var sb = new StringBuilder(list.Count + " cue(s):");
            foreach (var c in list) sb.Append("\n  k ").Append(c.k.ToString(IC)).Append("  ").Append(c.cmd);
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ run driver

    public static class DetRun
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public const int LoadSub = 7, LoadSp = 0, DefaultKmax = 1200, AfterLand = 210;
        public static DevCore core;
        public static string state = "idle";
        public static string name = "", outDir = "", sourceDir = "", mode = "", lastResult = "";
        public static long k0 = long.MinValue, lastK = -1;
        public static int frame0 = -1, kmax = DefaultKmax, fps = 60;
        public static DetLog log;

        static long lastStep = long.MinValue;
        static int lastUpdateFrame = -1, lastLateFrame = -1, fixedThisFrame;
        static bool hooked, sawPrepare, stopAtLand = true;
        static string stopReason, abortReason;
        static float startedAt, lastStepAt;
        static string created = "";
        static LoadListener listener;
        static int loadHandle = -1;
        static readonly List<DetCue> cues = new List<DetCue>();
        static int cuesFired;
        static string camMode = "game", camSource = "";
        static Vector3 camPos;
        static float camYaw, camPitch, camFov = 45f;
        static List<DetRigRow> rigRows;
        static bool camSet, lockBefore;
        static int sCapture, sTarget, sVsyncReal, sVsyncVirtual;
        static bool sAuto, sBg, locked;
        static float sFixed;
        static string prepAtRun = "", prepCounts = "";

        public static bool Busy { get { return state == "loading" || state == "running" || state == "stopping"; } }

        sealed class LoadListener : IFastEventExListener<GameManager.Load>
        {
            public void OnFastEventEx(FastEventEx<GameManager.Load> sender, ref GameManager.Load arg) { OnPrepareLoad(); }
        }

        // ---------------------------------------------------------------- start

        public static string Start(bool replay, List<string> a)
        {
            if (Busy) return "busy: det " + state + " " + name + " (det abort stops it)";
            string usage = replay ? "usage: det replay <source> <out> [cam=...] [kmax=n] [fps=n]" : "usage: det run <name> <out> [kmax] [kmax=n] [fps=n] [cam=...]";
            if (a.Count < 4) return usage;
            try { if (MultiCam.Running) return "busy: mcam is running"; } catch { }
            if (!G.SavepointsReady) return "savepoints not ready: start or continue a game first";
            string outFull;
            try { outFull = FullPath(a[3]); } catch (Exception e) { return "bad <out>: " + e.Message; }
            int km = DefaultKmax, f = 60;
            bool kmaxSet = false;
            string cam = null;
            for (int i = 4; i < a.Count; i++)
            {
                string t = a[i];
                string tl = t.ToLowerInvariant();
                int v;
                if (tl.StartsWith("kmax=")) { if (!int.TryParse(t.Substring(5), NumberStyles.Integer, IC, out km) || km < 1) return "bad kmax: " + t; kmaxSet = true; }
                else if (tl.StartsWith("fps=")) { if (!int.TryParse(t.Substring(4), NumberStyles.Integer, IC, out f) || f < 30 || f > 600 || f % 60 != 0) return "bad fps (a multiple of 60): " + t; }
                else if (tl.StartsWith("cam="))
                {
                    cam = t.Substring(4);
                    float dummy;
                    if (cam.IndexOf(',') < 0 && float.TryParse(cam, NumberStyles.Float, IC, out dummy))
                    {
                        if (i + 5 >= a.Count) return "cam= needs x y z yaw pitch fov";
                        for (int j = 1; j <= 5; j++) cam += "," + a[i + j];
                        i += 5;
                    }
                }
                else if (!replay && int.TryParse(t, NumberStyles.Integer, IC, out v) && v > 0) { km = v; kmaxSet = true; }
                else return "unknown argument '" + t + "'\n" + usage;
            }
            string src = null;
            List<DetInput> inputs = null;
            List<DetCue> runCues;
            string err;
            if (replay)
            {
                src = SourceDir(a[2], outFull);
                if (!Directory.Exists(src)) return "source run not found: " + src;
                if (SamePath(src, outFull)) return "<out> is the source run folder: " + outFull;
                inputs = DetLog.ReadInputs(src, out err);
                if (inputs == null) return err;
                runCues = DetLog.ReadCues(src, out err);
                if (runCues == null) return err;
                if (inputs.Count == 0) return "source run has no inputs: " + src;
                if (!kmaxSet) km = Math.Max(1, inputs.Count - 1);
            }
            else
            {
                runCues = new List<DetCue>();
                foreach (var c in DetCommands.Track) runCues.Add(new DetCue { k = c.k, cmd = c.cmd });
            }
            camMode = "game"; camSource = ""; rigRows = null;
            if (cam != null)
            {
                var p = cam.Split(',');
                if (p.Length == 6)
                {
                    float[] n = new float[6];
                    for (int i = 0; i < 6; i++) if (!float.TryParse(p[i], NumberStyles.Float, IC, out n[i])) return "bad cam pose value '" + p[i] + "' (cam=x,y,z,yaw,pitch,fov)";
                    camMode = "pose"; camPos = new Vector3(n[0], n[1], n[2]); camYaw = n[3]; camPitch = n[4]; camFov = n[5];
                }
                else if (p.Length == 1 && cam.Length > 0)
                {
                    string rsrc = SourceDir(cam, outFull);
                    rigRows = DetLog.ReadRig(rsrc, out err);
                    if (rigRows == null || err != null) return err ?? ("no rig rows in " + rsrc);
                    camMode = "rig"; camSource = rsrc;
                }
                else return "bad cam= (cam=x,y,z,yaw,pitch,fov | cam=x y z yaw pitch fov | cam=<run>)";
            }
            try { Directory.CreateDirectory(outFull); } catch (Exception e) { return "cannot create " + outFull + ": " + e.Message; }
            bool overwrite = File.Exists(Path.Combine(outFull, "steps.csv"));
            mode = replay ? "replay" : "record";
            name = replay ? Path.GetFileName(outFull) : a[2];
            outDir = outFull; sourceDir = src ?? ""; kmax = km; fps = f; stopAtLand = !replay;
            cues.Clear(); cues.AddRange(runCues); cuesFired = 0;
            k0 = long.MinValue; lastK = -1; frame0 = -1; lastStep = long.MinValue; fixedThisFrame = 0;
            sawPrepare = false; stopReason = null; abortReason = null; camSet = false;
            created = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", IC);
            prepAtRun = DetPrep.Text(); prepCounts = "";
            try
            {
                log = new DetLog(outFull);
                if (replay) log.LoadReference(src);
                DetLog.WriteCues(outFull, cues);
                Lock(fps);
                lockBefore = CameraControl.captureLock;
                CameraControl.captureLock = true;
                FilmInput.Install(replay ? FilmInput.Replay : FilmInput.Record, inputs);
                Hook();
                state = "loading";
                startedAt = Time.realtimeSinceStartup;
                WriteMeta("loading");
                log.Event(-1, "start", mode, name);
                G.LoadSavepoint(LoadSub, LoadSp);
            }
            catch (Exception e)
            {
                state = "loading";
                Finish("start failed: " + e.GetType().Name + ": " + e.Message);
                return "det start failed: " + e.Message;
            }
            return "det " + mode + " " + name + ": loading " + LoadSub + " " + LoadSp + " -> " + outFull + (overwrite ? " (overwriting the previous run there)" : "") +
                   "\n  prep " + prepAtRun + ", fps " + fps + ", kmax " + kmax + ", cues " + cues.Count + ", cam " + CamText() + (replay ? ", inputs " + inputs.Count + " steps from " + src : "");
        }

        static string FullPath(string p)
        {
            string full = Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(Path.Combine(Extensions.Dir, "det"), p));
            return full.Length > 3 ? full.TrimEnd('/', '\\') : full;
        }

        static string SourceDir(string nameOrPath, string outFull)
        {
            if (Path.IsPathRooted(nameOrPath)) return FullPath(nameOrPath);
            if (nameOrPath.IndexOf('/') >= 0 || nameOrPath.IndexOf('\\') >= 0) return FullPath(nameOrPath);
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(outFull), nameOrPath));
        }

        static bool SamePath(string x, string y)
        {
            return string.Equals(x.TrimEnd('/', '\\').Replace('\\', '/'), y.TrimEnd('/', '\\').Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        static string CamText()
        {
            if (camMode == "pose") return "pose " + V(camPos) + " yaw " + F(camYaw) + " pitch " + F(camPitch) + " fov " + F(camFov);
            if (camMode == "rig") return "rig of " + camSource + " (" + rigRows.Count + " frames)";
            return "game";
        }

        // ---------------------------------------------------------------- frame lock and hooks

        static void Lock(int rate)
        {
            sCapture = Time.captureFramerate; sTarget = Application.targetFrameRate; sVsyncReal = QualitySettings.vSyncCount;
            sFixed = Time.fixedDeltaTime; sBg = Application.runInBackground;
            try { sVsyncVirtual = FramerateController.GetVirtualVSyncCount(); sAuto = FramerateController.automatic; } catch { sVsyncVirtual = sVsyncReal; sAuto = false; }
            try { FramerateController.automatic = false; FramerateController.SetVSyncCount(0); } catch { }
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = rate;
            Time.captureFramerate = rate;
            Time.fixedDeltaTime = 1f / 60f;
            Application.runInBackground = true;
            locked = true;
        }

        static void Unlock()
        {
            if (!locked) return;
            Time.captureFramerate = sCapture;
            Time.fixedDeltaTime = sFixed;
            try { FramerateController.SetVSyncCount(sVsyncVirtual); FramerateController.automatic = sAuto; } catch { }
            QualitySettings.vSyncCount = sVsyncReal;
            Application.targetFrameRate = sTarget;
            Application.runInBackground = sBg;
            locked = false;
        }

        static void Hook()
        {
            if (hooked) return;
            RuntimeCallbacks.fixedUpdate += OnFixed;
            RuntimeCallbacks.update += OnUpdate;
            RuntimeCallbacks.lateUpdate += OnLate;
            GameManager.allScenesLoaded += OnAllLoaded;
            SubsceneCuller.StaticEvents.postActivate += OnPostActivate;
            SubsceneCuller.StaticEvents.postDeactivate += OnPostDeactivate;
            if (listener == null) listener = new LoadListener();
            GameManager.prepareLoad.Register(listener, ref loadHandle);
            hooked = true;
        }

        static void Unhook()
        {
            if (!hooked) return;
            RuntimeCallbacks.fixedUpdate -= OnFixed;
            RuntimeCallbacks.update -= OnUpdate;
            RuntimeCallbacks.lateUpdate -= OnLate;
            GameManager.allScenesLoaded -= OnAllLoaded;
            SubsceneCuller.StaticEvents.postActivate -= OnPostActivate;
            SubsceneCuller.StaticEvents.postDeactivate -= OnPostDeactivate;
            GameManager.prepareLoad.Unregister(ref loadHandle);
            hooked = false;
        }

        static void ResetRng()
        {
            RandomGenerator.ResetAll();
            PDRandom.seed = 0;
            UnityEngine.Random.seed = 0;
        }

        // ---------------------------------------------------------------- game callbacks

        static void OnPrepareLoad()
        {
            try
            {
                if (state == "loading")
                {
                    ResetRng();
                    sawPrepare = true;
                    DetPrep.ComputeZone(LoadSub);
                    DetPrep.OnPrepareLoad();
                    log.Event(-1, "prepareLoad", "zone " + DetPrep.zone.Count + " culler(s)", "kept " + DetPrep.KeptCount);
                }
                else if (state == "running" || state == "stopping")
                {
                    log.Event(lastK, "load", "unexpected load", "");
                    if (abortReason == null) abortReason = "unexpected load during the run (k " + lastK + ")";
                }
            }
            catch (Exception e) { DevLog.Error("[det] prepareLoad", e); }
        }

        static void OnAllLoaded()
        {
            try
            {
                if (state != "loading") return;
                if (!sawPrepare) log.warnings.Add("allScenesLoaded without a prepareLoad of this run");
                ResetRng();
                k0 = LogicManager.stepCount;
                frame0 = Time.frameCount;
                if (FilmInput.current != null) FilmInput.current.k0 = k0;
                DetPrep.OnLoaded();
                log.Resolve(DetPrep.ZoneGroups());
                SetupCamera();
                state = "running";
                lastStepAt = Time.realtimeSinceStartup;
                log.Event(-1, "allScenesLoaded", "k0 " + k0.ToString(IC), "frame " + frame0.ToString(IC));
                WriteMeta("running");
            }
            catch (Exception e)
            {
                DevLog.Error("[det] allScenesLoaded", e);
                if (abortReason == null) abortReason = "allScenesLoaded handler failed: " + e.Message;
            }
        }

        static void SetupCamera()
        {
            if (camMode == "pose") camSet = CameraControl.Set(camPos, Quaternion.Euler(camPitch, camYaw, 0f), camFov);
            else if (camMode == "rig" && rigRows != null && rigRows.Count > 0) camSet = CameraControl.Set(rigRows[0].pos, rigRows[0].rot, rigRows[0].fov);
            else return;
            if (!camSet) log.warnings.Add("free camera could not be set (no gameplay camera)");
        }

        static void OnPostActivate(SubsceneCuller c)
        {
            try
            {
                if (state != "running") return;
                log.Event(lastK, "activate", c != null ? c.name : "?", DetPrep.zone.Contains(c) ? "zone" : "");
                DetPrep.OnActivated(c);
            }
            catch (Exception e) { DevLog.Error("[det] postActivate", e); }
        }

        static void OnPostDeactivate(SubsceneCuller c)
        {
            try { if (state == "running") log.Event(lastK, "deactivate", c != null ? c.name : "?", DetPrep.zone.Contains(c) ? "zone" : ""); }
            catch (Exception e) { DevLog.Error("[det] postDeactivate", e); }
        }

        static void OnFixed()
        {
            try
            {
                long step = LogicManager.stepCount;
                if (step == lastStep) return;
                lastStep = step;
                fixedThisFrame++;
                if (state != "running") return;
                long k = step - k0;
                if (k < 0) return;
                lastK = k;
                var fi = FilmInput.current;
                bool has = fi != null && fi.lastK == k;
                log.Step(k, step, has, has ? fi.last : default(DetInput));
                foreach (var c in cues)
                {
                    if (c.k != k || c.lastFiredStep == step) continue;
                    c.lastFiredStep = step;
                    string r;
                    try { r = Remote.RunCommand(c.cmd, core ?? DevCore.Instance); } catch (Exception e) { r = "ERROR " + e.GetType().Name + ": " + e.Message; }
                    cuesFired++;
                    log.Event(k, "cue", c.cmd, Short(r));
                }
                lastStepAt = Time.realtimeSinceStartup;
                if (k >= kmax) stopReason = "kmax " + kmax.ToString(IC);
                else if (stopAtLand && log.kBaleLand >= 0 && k >= log.kBaleLand + AfterLand) stopReason = "baleLand + " + AfterLand.ToString(IC) + " (k " + k.ToString(IC) + ")";
                if (stopReason != null) state = "stopping";
            }
            catch (Exception e)
            {
                DevLog.Error("[det] fixedUpdate", e);
                if (abortReason == null) abortReason = "fixedUpdate hook failed: " + e.Message;
            }
        }

        static void OnUpdate()
        {
            try
            {
                int fc = Time.frameCount;
                if (fc == lastUpdateFrame) return;
                lastUpdateFrame = fc;
                if (state != "running" && state != "stopping") return;
                log.MarkUpdate(UnityEngine.Random.seed);
                if (state != "running") return;
                DetPrep.Tick();
                log.Rig(fc - frame0, lastK, Camera.main, CameraControl.free);
            }
            catch (Exception e) { DevLog.Error("[det] update", e); }
        }

        static void OnLate()
        {
            try
            {
                int fc = Time.frameCount;
                if (fc == lastLateFrame) return;
                lastLateFrame = fc;
                int n = fixedThisFrame;
                fixedThisFrame = 0;
                if (state != "running" && state != "stopping") return;
                log.Late(UnityEngine.Random.seed, n);
                if (state == "running" && camMode == "rig" && rigRows != null)
                {
                    int next = fc - frame0 + 1;
                    if (next >= 0 && next < rigRows.Count) CameraControl.Set(rigRows[next].pos, rigRows[next].rot, rigRows[next].fov);
                }
                if (state == "stopping") Finish(stopReason);
            }
            catch (Exception e)
            {
                DevLog.Error("[det] lateUpdate", e);
                if (abortReason == null) abortReason = "lateUpdate hook failed: " + e.Message;
            }
        }

        public static void Tick()
        {
            if (!Busy) return;
            float now = Time.realtimeSinceStartup;
            if (abortReason != null) { Finish(abortReason); return; }
            if (state == "loading")
            {
                if (!sawPrepare && now - startedAt > 20f) Finish("load did not start within 20 s");
                else if (now - startedAt > 300f) Finish("load did not finish within 300 s");
            }
            else if (state == "running" && now - lastStepAt > 30f) Finish("no logic step for 30 s (game paused or frozen)");
        }

        // ---------------------------------------------------------------- finish

        public static string Finish(string reason)
        {
            if (!Busy) return "det not running (" + state + ")";
            state = "finishing";
            bool ok = reason != null && (reason.StartsWith("kmax") || reason.StartsWith("baleLand"));
            List<DetInput> fed = FilmInput.current != null ? FilmInput.current.fed : null;
            int missing = FilmInput.current != null ? FilmInput.current.missing : 0;
            Try("hooks", Unhook);
            Try("input", FilmInput.Uninstall);
            Try("camera", () => { if (camSet) CameraControl.SetFree(false); CameraControl.captureLock = lockBefore; camSet = false; });
            prepCounts = DetPrep.Counts();
            Try("prep", DetPrep.Restore);
            Try("frames", Unlock);
            Try("log", () =>
            {
                if (log == null) return;
                log.Event(lastK, ok ? "end" : "abort", reason ?? "", "");
                if (fed != null) DetLog.WriteInputs(outDir, fed);
                if (missing > 0) log.warnings.Add(missing + " replay step(s) beyond the source inputs (neutral input fed)");
                log.Close();
            });
            state = ok ? "done" : "aborted";
            Try("meta", () => WriteMeta(reason ?? ""));
            lastResult = "det " + mode + " " + name + " " + state + ": " + reason + "; k " + lastK + ", rows " + (log != null ? log.rows : 0) +
                         (log != null ? ", baleFall " + log.kBaleFall + ", baleLand " + log.kBaleLand + ", shots " + log.shots + ", kills " + log.kills : "") +
                         (log != null && sourceDir.Length > 0 ? "\n  " + log.LiveText() : "") + "\n  -> " + outDir;
            DevLog.Write("[det] " + lastResult);
            return lastResult;
        }

        static void Try(string what, Action a)
        {
            try { a(); } catch (Exception e) { DevLog.Error("[det] finish " + what, e); if (log != null) log.warnings.Add("finish " + what + ": " + e.Message); }
        }

        // ---------------------------------------------------------------- status and meta

        public static string Status()
        {
            var sb = new StringBuilder("det " + state);
            if (name.Length > 0) sb.Append("  ").Append(mode).Append(' ').Append(name).Append(" -> ").Append(outDir);
            if (Busy && log != null)
            {
                sb.Append("\n  k ").Append(lastK).Append(" / ").Append(kmax).Append("  step ").Append(LogicManager.stepCount).Append("  k0 ").Append(k0 == long.MinValue ? "-" : k0.ToString(IC));
                sb.Append("  rows ").Append(log.rows).Append("  events ").Append(log.eventCount).Append("  cues ").Append(cuesFired).Append('/').Append(cues.Count);
                sb.Append("\n  baleFall ").Append(log.kBaleFall).Append("  baleLand ").Append(log.kBaleLand).Append("  shots ").Append(log.shots).Append("  hits ").Append(log.hits).Append("  kills ").Append(log.kills);
                if (FilmInput.current != null && FilmInput.current.missing > 0) sb.Append("  input missing ").Append(FilmInput.current.missing);
                if (sourceDir.Length > 0) sb.Append("\n  ").Append(log.LiveText());
                foreach (var w in log.warnings) sb.Append("\n  warning: ").Append(w);
            }
            else if (lastResult.Length > 0) sb.Append("\n  last: ").Append(lastResult);
            sb.Append("\n  prep ").Append(DetPrep.Text()).Append("  cue track ").Append(DetCommands.Track.Count);
            return sb.ToString();
        }

        static void WriteMeta(string end)
        {
            if (log == null) return;
            var sb = new StringBuilder("{\n");
            sb.Append("  \"tool\": \"InsideDev.Det\",\n  \"version\": 1,\n");
            sb.Append("  \"name\": ").Append(DetLog.J(name)).Append(",\n  \"mode\": ").Append(DetLog.J(mode)).Append(",\n");
            sb.Append("  \"source\": ").Append(DetLog.J(sourceDir)).Append(",\n  \"created\": ").Append(DetLog.J(created)).Append(",\n");
            sb.Append("  \"state\": ").Append(DetLog.J(state)).Append(",\n  \"end\": ").Append(DetLog.J(end)).Append(",\n");
            sb.Append("  \"load\": [").Append(LoadSub).Append(", ").Append(LoadSp).Append("],\n");
            sb.Append("  \"prep\": {\"anim\": ").Append(DetLog.J(DetPrep.anim)).Append(", \"cull\": ").Append(DetLog.J(DetPrep.cull)).Append(", \"stream\": ").Append(DetLog.J(DetPrep.stream)).Append(", \"drip\": ").Append(DetLog.J(DetPrep.drip)).Append("},\n");
            sb.Append("  \"prepText\": ").Append(DetLog.J(prepAtRun)).Append(",\n  \"prepCounts\": ").Append(DetLog.J(prepCounts)).Append(",\n");
            sb.Append("  \"fps\": ").Append(fps).Append(",\n  \"kmax\": ").Append(kmax).Append(",\n  \"afterLand\": ").Append(stopAtLand ? AfterLand : -1).Append(",\n");
            sb.Append("  \"fixedDeltaTimeBefore\": ").Append(DetLog.F(sFixed)).Append(",\n  \"captureFramerateBefore\": ").Append(sCapture).Append(",\n");
            sb.Append("  \"k0\": ").Append(k0 == long.MinValue ? "null" : k0.ToString(IC)).Append(",\n  \"frame0\": ").Append(frame0).Append(",\n");
            sb.Append("  \"kEnd\": ").Append(lastK).Append(",\n  \"rows\": ").Append(log.rows).Append(",\n  \"rigRows\": ").Append(log.rigRows).Append(",\n");
            sb.Append("  \"kBaleFall\": ").Append(log.kBaleFall).Append(",\n  \"kBaleLand\": ").Append(log.kBaleLand).Append(",\n");
            sb.Append("  \"shots\": ").Append(log.shots).Append(",\n  \"hits\": ").Append(log.hits).Append(",\n  \"kills\": ").Append(log.kills).Append(",\n  \"killed\": ").Append(DetLog.J(log.killed)).Append(",\n");
            sb.Append("  \"chicks\": ").Append(log.chickCount).Append(",\n  \"groups\": ").Append(log.groupCount).Append(",\n");
            sb.Append("  \"cam\": {\"mode\": ").Append(DetLog.J(camMode)).Append(", \"text\": ").Append(DetLog.J(CamText())).Append("},\n");
            sb.Append("  \"cues\": ").Append(cues.Count).Append(",\n  \"cuesFired\": ").Append(cuesFired).Append(",\n");
            sb.Append("  \"zone\": [");
            for (int i = 0; i < DetPrep.zone.Count; i++) sb.Append(i > 0 ? ", " : "").Append(DetLog.J(DetPrep.zone[i] != null ? DetPrep.zone[i].name : "?"));
            sb.Append("],\n  \"live\": ").Append(DetLog.J(sourceDir.Length > 0 ? log.LiveText() : "")).Append(",\n");
            sb.Append("  \"warnings\": [");
            for (int i = 0; i < log.warnings.Count; i++) sb.Append(i > 0 ? ", " : "").Append(DetLog.J(log.warnings[i]));
            sb.Append("]\n}\n");
            File.WriteAllText(Path.Combine(outDir, "meta.json"), sb.ToString(), new UTF8Encoding(false));
        }

        static string Short(string s)
        {
            if (s == null) return "";
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length > 160 ? s.Substring(0, 160) : s;
        }

        static string F(float v) { return v.ToString("0.###", IC); }
        static string V(Vector3 v) { return "(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ")"; }
    }

    // ------------------------------------------------------------------ prep switches (7.2)

    public static class DetPrep
    {
        public static string anim = "default", cull = "live", stream = "live", drip = "on";
        public const float Margin = 5f;
        public const string DripName = "FX_WaterDripDown2";
        public static readonly List<SubsceneCuller> zone = new List<SubsceneCuller>();
        public static Bounds zoneBounds;
        public static bool zoneOk;
        static readonly List<SubsceneCuller> kept = new List<SubsceneCuller>();
        static readonly List<Animation> animPending = new List<Animation>();
        static readonly Dictionary<Animation, AnimationCullingType> animBefore = new Dictionary<Animation, AnimationCullingType>();
        static readonly List<BoundsCullerRuntime> forced = new List<BoundsCullerRuntime>();
        static readonly List<ParticleSound> drips = new List<ParticleSound>();
        static bool unhooked;
        static int hookIndex = -1, animRestoreSkipped;
        static FieldInfo fAlways, fEditUpdate;

        public static int KeptCount { get { return kept.Count; } }

        public static string Text() { return "anim=" + anim + " cull=" + cull + " stream=" + stream + " drip=" + drip; }

        public static string Set(List<string> a)
        {
            if (a.Count <= 2) return "prep " + Text();
            if (DetRun.Busy) return "busy: det " + DetRun.state + " (prep is read at load time; change it between runs)";
            string na = anim, nc = cull, ns = stream, nd = drip;
            for (int i = 2; i < a.Count; i++)
            {
                string t = a[i].ToLowerInvariant();
                if (t == "full") { na = "always"; nc = "freeze"; ns = "keep"; nd = "off"; continue; }
                if (t == "default") { na = "default"; nc = "live"; ns = "live"; nd = "on"; continue; }
                int eq = t.IndexOf('=');
                if (eq <= 0) return "bad prep switch '" + a[i] + "'";
                string k = t.Substring(0, eq), v = t.Substring(eq + 1);
                if (k == "anim" && (v == "default" || v == "always")) na = v;
                else if (k == "cull" && (v == "live" || v == "freeze")) nc = v;
                else if (k == "stream" && (v == "live" || v == "keep")) ns = v;
                else if (k == "drip" && (v == "on" || v == "off")) nd = v;
                else return "bad prep switch '" + a[i] + "' (anim=default|always cull=live|freeze stream=live|keep drip=on|off)";
            }
            anim = na; cull = nc; stream = ns; drip = nd;
            return "prep " + Text();
        }

        public static void ComputeZone(int sub)
        {
            zone.Clear();
            zoneOk = false;
            Bounds b;
            if (G.AreaBounds(sub, out b)) zoneOk = true;
            else
            {
                try
                {
                    var p = SavepointManager.GetSavepointPosition(sub, DetRun.LoadSp);
                    b = new Bounds(p, new Vector3(60f, 60f, 60f));
                    zoneOk = p != Vector3.zero;
                }
                catch { }
            }
            b.Expand(2f * Margin);
            zoneBounds = b;
            if (!zoneOk) return;
            foreach (var c in SubsceneCullingManager.instance.GetAllSubsceneCullers())
                if (c != null && c.CullingBounds.Intersects(b)) zone.Add(c);
        }

        public static string ZoneReport()
        {
            if (!G.SavepointsReady) return "savepoints not ready";
            if (!DetRun.Busy) ComputeZone(DetRun.LoadSub);
            var sb = new StringBuilder("zone of load " + DetRun.LoadSub + " " + DetRun.LoadSp + ": " + (zoneOk ? "bounds " + zoneBounds.center.ToString("F1") + " size " + zoneBounds.size.ToString("F1") : "no area bounds") + ", " + zone.Count + " subscene culler(s)");
            int groups = 0, anims = 0, dripCount = 0;
            foreach (var c in zone)
            {
                sb.Append("\n  ").Append(c.name).Append("  ").Append(c.LoadedState).Append(Kept(c) ? "  kept" : "");
                Transform root = null;
                try { if (c.IsLoaded) root = c.ContentRoot; } catch { }
                if (root == null) continue;
                groups += root.GetComponentsInChildren<BoundsCullerRuntime>(true).Length;
                anims += root.GetComponentsInChildren<Animation>(true).Length;
                foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == DripName) dripCount += t.GetComponentsInChildren<ParticleSound>(true).Length;
            }
            sb.Append("\n  loaded content: ").Append(groups).Append(" culler group(s), ").Append(anims).Append(" Animation(s), ").Append(dripCount).Append(" ParticleSound on ").Append(DripName);
            sb.Append("\n  prep ").Append(Text());
            return sb.ToString();
        }

        static FastList<SubsceneCuller> AlwaysList()
        {
            try
            {
                if (fAlways == null) fAlways = typeof(SubsceneCullingManager).GetField("alwaysLoaded", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                return fAlways != null ? fAlways.GetValue(SubsceneCullingManager.instance) as FastList<SubsceneCuller> : null;
            }
            catch { return null; }
        }

        static bool Kept(SubsceneCuller c)
        {
            if (kept.Contains(c) || c.AlwaysLoaded) return true;
            var l = AlwaysList();
            return l != null && l.Contains(c);
        }

        public static void OnPrepareLoad()
        {
            if (stream != "keep") return;
            var m = SubsceneCullingManager.instance;
            foreach (var c in zone)
            {
                if (c == null || Kept(c)) continue;
                m.AddAlwaysLoaded(c);
                kept.Add(c);
            }
        }

        public static void OnLoaded()
        {
            foreach (var c in zone) if (c != null && c.IsLoaded) Apply(c);
            Tick();
        }

        public static void OnActivated(SubsceneCuller c)
        {
            if (c != null && zone.Contains(c)) Apply(c);
        }

        static void Apply(SubsceneCuller c)
        {
            Transform root;
            try { root = c.ContentRoot; } catch { return; }
            if (root == null) return;
            if (anim == "always")
                foreach (var an in root.GetComponentsInChildren<Animation>(true))
                    if (an != null && !animBefore.ContainsKey(an) && !animPending.Contains(an)) animPending.Add(an);
            if (cull == "freeze")
                foreach (var g in root.GetComponentsInChildren<BoundsCullerRuntime>(true))
                {
                    if (g == null || forced.Contains(g)) continue;
                    forced.Add(g);
                    g.previousVisible = true;
                    if (!g.gameObject.activeSelf) g.gameObject.SetActive(true);
                }
            if (drip == "off")
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name != DripName) continue;
                    foreach (var ps in t.GetComponentsInChildren<ParticleSound>(true))
                        if (ps != null && ps.enabled && !drips.Contains(ps)) { ps.enabled = false; drips.Add(ps); }
                }
        }

        public static void Tick()
        {
            if (cull == "freeze") UnhookCullers();
            for (int i = animPending.Count - 1; i >= 0; i--)
            {
                var an = animPending[i];
                if (an == null) { animPending.RemoveAt(i); continue; }
                if (!an.gameObject.activeInHierarchy) continue;
                animBefore[an] = an.cullingType;
                an.cullingType = AnimationCullingType.AlwaysAnimate;
                animPending.RemoveAt(i);
            }
        }

        static FieldInfo EditUpdateField()
        {
            if (fEditUpdate == null) fEditUpdate = typeof(RuntimeCallbacks.ExecuteInEditModeIncluded).GetField("update", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return fEditUpdate;
        }

        static int CullerHookIndex()
        {
            try
            {
                var f = EditUpdateField();
                var d = f != null ? f.GetValue(null) as Delegate : null;
                if (d == null) return -1;
                var list = d.GetInvocationList();
                for (int i = 0; i < list.Length; i++) if (list[i].Method.DeclaringType == typeof(BoundsCullerRuntime) && list[i].Method.Name == "UpdateBoundsCullers") return i;
            }
            catch { }
            return -1;
        }

        static void UnhookCullers()
        {
            if (!unhooked) hookIndex = CullerHookIndex();
            RuntimeCallbacks.ExecuteInEditModeIncluded.update -= BoundsCullerRuntime.UpdateBoundsCullers;
            unhooked = true;
        }

        static void RehookCullers()
        {
            if (BoundsCullerRuntime.boundsCullers.Count == 0 || CullerHookIndex() >= 0) return;
            Action add = BoundsCullerRuntime.UpdateBoundsCullers;
            var f = EditUpdateField();
            var d = f != null ? f.GetValue(null) as Delegate : null;
            if (f == null || d == null || hookIndex < 0) { RuntimeCallbacks.ExecuteInEditModeIncluded.update += add; return; }
            var list = new List<Delegate>(d.GetInvocationList());
            list.Insert(Math.Min(hookIndex, list.Count), add);
            f.SetValue(null, Delegate.Combine(list.ToArray()));
        }

        public static List<BoundsCullerRuntime> ZoneGroups()
        {
            var r = new List<BoundsCullerRuntime>();
            foreach (var c in zone)
            {
                Transform root = null;
                try { if (c != null && c.IsLoaded) root = c.ContentRoot; } catch { }
                if (root == null) continue;
                foreach (var g in root.GetComponentsInChildren<BoundsCullerRuntime>(true)) if (g != null && !r.Contains(g)) r.Add(g);
            }
            if (r.Count == 0) foreach (var g in BoundsCullerRuntime.boundsCullers) if (g != null && !r.Contains(g)) r.Add(g);
            return r;
        }

        public static string Counts()
        {
            return "zone " + zone.Count + ", kept " + kept.Count + ", groups forced " + forced.Count + ", animations always " + animBefore.Count + " (" + animPending.Count + " never active), drips off " + drips.Count +
                   (unhooked ? ", culler hook removed (was at " + hookIndex + ")" : "");
        }

        public static void Restore()
        {
            if (unhooked)
            {
                try { RehookCullers(); } catch (Exception e) { DevLog.Error("[det] culler hook", e); }
                unhooked = false;
                hookIndex = -1;
            }
            foreach (var g in forced) { try { if (g != null) g.RefreshRuntimeState(); } catch { } }
            forced.Clear();
            animRestoreSkipped = 0;
            foreach (var kv in animBefore)
            {
                try { if (kv.Key != null && kv.Key.gameObject.activeInHierarchy) kv.Key.cullingType = kv.Value; else animRestoreSkipped++; } catch { animRestoreSkipped++; }
            }
            if (animRestoreSkipped > 0) DevLog.Write("[det] " + animRestoreSkipped + " Animation(s) inactive at restore keep AlwaysAnimate until the next load");
            animBefore.Clear();
            animPending.Clear();
            foreach (var ps in drips) { try { if (ps != null) ps.enabled = true; } catch { } }
            drips.Clear();
            var m = SubsceneCullingManager.instance;
            foreach (var c in kept) { try { if (c != null) m.RemoveAlwaysLoaded(c); } catch { } }
            kept.Clear();
        }
    }
}
