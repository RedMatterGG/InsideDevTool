using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    // Replay mode PLAY_C (docs/REPLAY_DESIGN.md 3.4): the "srec ..." bridge commands. Viewer over SceneReplayer (load,
    // play, pause, seek, step, end) with the free camera, status and the object table; write-order probe (driven transforms
    // changed between SceneHooks.lateWrite and the last component OnPreCull, with candidate writers -> write_probe.tsv) and
    // camera probe (camera_driven.tsv with the expected column of 2.3.1, merged by default into <game>/_mod/, where every later
    // record reads it); manual record start/stop through SceneRecorder with the 2.3.4 pacing and its per-frame assert, the
    // virtual channel every frame, audio markers (Update phase, mirrored as "marker" events) and the keep handover.
    public static class SceneCommands
    {
        internal const string WriteProbeFile = "write_probe.tsv";
        internal const string WriteProbeHeader = "path\tframes\tmaxPos\tmaxRotDeg\twriters";
        internal const string CameraProbeHeader = "path\tcomponents\texpected\trigid";
        internal const string NoViewer = "no recording loaded (srec load <dir>)";
        internal const string RecordUsage = "usage: srec record start <dir> [roots=a,b] [keep=subscene,...] [spec=<file>] [clock=real|fixed:<rate>] [audio=on] [overwrite=on] [<record key>=value ...] | srec record stop [keep] | srec record status";
        internal const string EventUsage = "usage: srec event <name> [detail ...] (name: letters, digits, '_' and '.'; referenced as @name)";
        internal const string Usage =
            "srec status | srec objects [filter] [max=n]\n" +
            "srec load <dir> [t=<s>|@event] [cam=game|keep] [<play key>=value ...] | srec end\n" +
            "srec play [speed] [loop] | srec pause | srec seek <s|start|end|@event[+-s]> | srec step [dt|next|back]\n" +
            "srec probe [n] [eps=m] [rot=deg] [out=dir] | srec probe camera [n] [dx=m] [yaw=deg] [eps=m] [rot=deg] [out=dir] [roots=a,b] | srec probe stop | srec probe result\n" +
            "srec record start <dir> [roots=a,b] [keep=subscene,...] [spec=<file>] [clock=real|fixed:<rate>] [audio=on] [overwrite=on] [<record key>=value ...] | srec record stop [keep] | srec record status\n" +
            "srec event <name> [detail ...]";

        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly string[] RecordOwnKeys = { "clock", "audio", "overwrite", "spec" };
        const float KeepTimeout = 30f;
        static readonly string[] LoadOwnKeys = { "t", "cam" };
        static readonly string[] WriteProbeOwnKeys = { "eps", "rot", "out" };
        static readonly string[] CameraProbeOwnKeys = { "dx", "yaw", "eps", "rot", "out" };
        static readonly Dictionary<Type, string> writerKinds = new Dictionary<Type, string>();

        static SceneReplayer viewer;
        static string viewerDir = "";
        static bool playing, looping;
        static double speed = 1.0;
        static int freezeLeaks;
        static ManualRecord manual;
        static ReplayHandover pending;
        static string pendingFrom = "";
        static ProbeRun probe;
        static string lastProbe = "", lastProbeFull = "", lastRecord = "";
        static SceneCommandsHost host;
        static int errorCount;

        sealed class ProbeRun
        {
            public string kind = "", phase = "", dir = "";
            public int frame, frames, tick;
            public bool freeze;
            public Action cleanup;

            public void Cleanup()
            {
                var c = cleanup;
                cleanup = null;
                if (c != null) c();
            }
        }

        sealed class ManualRecord
        {
            public SceneRecorder rec;
            public RecordPrep prep;
            public RecordSpec spec;
            public RecordHeader header;
            public AudioCapSpec audio;
            public bool audioOn, audioLost, hooked, paced;
            public string dir = "", line = "";
            public readonly MultiCamContext ctx = new MultiCamContext();
            public readonly List<float> frameTimes = new List<float>(4096);
            public int lastMarker = -1, frames, changes;
            public double driftMs, driftMaxMs;
            public int captureBefore, vsyncBefore, vsyncVirtualBefore, targetBefore;
            public bool autoBefore;
            public int wantCapture, wantVsync, wantTarget, refresh;
            public MultiCamPlan plan;
            public MultiCamEvents events;
            public bool targets, waiting;
            public float waitUntil;
            public int settleFrom = -1, settled = -1;
            public SpecReport startReport;
            public readonly List<SubsceneCuller> keep = new List<SubsceneCuller>(), kept = new List<SubsceneCuller>();
            public readonly List<string> keptNames = new List<string>();
            public readonly HashSet<string> fired = new HashSet<string>();
            public Action<string, string> onPosted;
            public int eventRows;
        }

        // ------------------------------------------------------------------ dispatch

        public static string Command(List<string> a, DevCore core)
        {
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            try
            {
                switch (sub)
                {
                    case "status": return Status();
                    case "help": return Usage;
                    case "objects": return Objects(a);
                    case "load": return Load(a);
                    case "end": return End();
                    case "play": return Play(a);
                    case "pause": return Pause();
                    case "seek": return Seek(a);
                    case "step": return Step(a);
                    case "probe": return Probe(a);
                    case "record": return Record(a);
                    case "event": return PostEvent(a);
                }
            }
            catch (Exception e) { DevLog.Error("srec " + sub, e); return "srec " + sub + " failed: " + e.GetType().Name + ": " + e.Message; }
            return "usage:\n" + Usage;
        }

        static string Busy()
        {
            if (MultiCam.runner != null) return "busy: an mcam run owns the scene (srec status and srec objects stay available)";
            if (MultiCamSession.active) return "busy: an mcam session is active (mcam abort first)";
            if (SceneReplayer.current != null && SceneReplayer.current != viewer) return "busy: a replay owned by mcam is active";
            if (SceneRecorder.active != null && (manual == null || SceneRecorder.active != manual.rec)) return "busy: a recording owned by mcam is active";
            return null;
        }

        // ------------------------------------------------------------------ status and objects

        static string Status()
        {
            var sb = new StringBuilder();
            if (viewer != null)
            {
                sb.Append("viewer ").Append(viewerDir).Append("  ").Append(ViewerLine());
                if (freezeLeaks > 0) sb.Append("  timeScale re-forced ").Append(freezeLeaks).Append("x");
                sb.Append("\n  ").Append(ReplayerStatus(viewer)).Append('\n');
            }
            else if (SceneReplayer.current != null) sb.Append("replay (mcam): ").Append(ReplayerStatus(SceneReplayer.current)).Append('\n');
            if (manual != null) sb.Append(RecordLine(manual)).Append('\n');
            else if (SceneRecorder.active != null) sb.Append("recording (mcam): ").Append(RecorderStatus(SceneRecorder.active)).Append('\n');
            if (pending != null) sb.Append("handover pending from ").Append(pendingFrom).Append(": ").Append(HandoverText(pending)).Append(" (the next srec load takes it, srec end releases it)\n");
            if (probe != null) sb.Append(ProbeLine(probe)).Append('\n');
            if (lastProbe.Length > 0) sb.Append("last probe: ").Append(lastProbe).Append('\n');
            if (lastRecord.Length > 0 && manual == null) sb.Append("last deferred record start: ").Append(lastRecord).Append('\n');
            if (sb.Length == 0) return "srec idle (srec help)";
            if (viewer != null || manual != null || probe != null || SceneReplayer.current != null || SceneRecorder.active != null) sb.Append(HooksLine()).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        static string HooksLine()
        {
            int f = Time.frameCount, late = SceneHooks.lateFrame;
            return "hooks: lateFrame " + late + " at frame " + f + (late == f ? " (lateWrite fired this frame)" : "");
        }

        static string ReplayerStatus(SceneReplayer r)
        {
            try { return r.Status(); } catch (Exception e) { return "status failed: " + e.Message; }
        }

        static string RecorderStatus(SceneRecorder r)
        {
            try { return r.Status(); } catch (Exception e) { return "status failed: " + e.Message; }
        }

        static string Objects(List<string> a)
        {
            var r = viewer ?? SceneReplayer.current;
            if (r == null) return NoViewer;
            string filter = "";
            int max = 200;
            for (int i = 2; i < a.Count; i++)
            {
                string s = a[i];
                if (s.StartsWith("max=", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(s.Substring(4), NumberStyles.Integer, IC, out max) || max < 1) return "usage: srec objects [filter] [max=n]";
                }
                else filter = filter.Length == 0 ? s : filter + " " + s;
            }
            return r.ObjectsText(filter, max);
        }

        // ------------------------------------------------------------------ viewer

        static string Load(List<string> a)
        {
            if (a.Count < 3) return "usage: srec load <dir> [t=<s>|@event] [cam=game|keep] [<play key>=value ...]";
            string busy = Busy();
            if (busy != null) return busy;
            if (viewer != null) return "a recording is loaded (" + viewerDir + "): srec end first";
            if (manual != null) return "recording in progress: srec record stop first";
            if (probe != null) return "a probe is running: srec probe stop first";
            string full;
            try { full = Path.GetFullPath(a[2]); } catch (Exception e) { return "bad dir: " + e.Message; }
            string dir = RecordingDir(full);
            if (dir == null) return "no " + ReplayConst.RecordFile + " in " + full + " or in its " + ReplayConst.RecordFolder + " folder";
            var report = new SpecReport();
            string tArg;
            bool gameCam;
            var spec = PlayArgs(a, 3, report, out tArg, out gameCam);
            if (!report.ok) return "srec load refused:\n" + report.Summary(20);
            SceneReplayer r = null;
            try { r = SceneReplayer.Load(dir, spec, report); }
            catch (Exception e) { report.Error("srec load", e.GetType().Name + ": " + e.Message); r = null; }
            if (r == null || !report.ok) return "srec load refused: " + dir + "\n" + report.Summary(30);
            var hand = pending;
            try { r.Begin(hand, report); }
            catch (Exception e)
            {
                report.Error("srec load", "Begin: " + e.GetType().Name + ": " + e.Message);
                try { r.End(report); } catch (Exception e2) { report.Error("srec load", "End: " + e2.GetType().Name + ": " + e2.Message); }
                if (hand != null) { report.Note("srec load", "record handover released (" + ReleaseHandover(hand) + ")"); pending = null; pendingFrom = ""; }
                return "srec load failed in Begin, restored: " + dir + "\n" + report.Summary(30);
            }
            if (hand != null) { pending = null; pendingFrom = ""; }
            int beginErrors = report.errors.Count;
            viewer = r;
            viewerDir = dir;
            playing = false;
            looping = false;
            speed = 1.0;
            freezeLeaks = 0;
            double dur = Duration(r), t = 0.0;
            string err;
            if (tArg != null && !ParseTime(tArg, 0.0, dur, EventTimeOf(r), out t, out err)) { report.Warn("srec.t", err + "; T = 0"); t = 0.0; }
            r.T = Clamp(t, 0.0, dur);
            EnsureHost();
            string camText = ViewerCamera(r, r.T, gameCam);
            var sb = new StringBuilder();
            sb.Append("srec loaded ").Append(dir).Append(": ").Append(SampleCount(r)).Append(" samples, ").Append(F3(dur)).Append(" s");
            if (r.tracks != null && r.tracks.rec != null) sb.Append(", ").Append(r.tracks.rec.objects.Count).Append(" objects");
            if (hand != null) sb.Append(", record handover taken (").Append(HandoverText(hand)).Append(")");
            sb.Append("; frozen at T ").Append(F3(r.T)).Append("; ").Append(camText).Append(" (Insert toggles, WASD/QE + right mouse fly); srec play [speed] [loop], srec end restores");
            if (beginErrors > 0) sb.Append("\nBegin reported ").Append(beginErrors).Append(" error(s): the viewer runs with those parts missing (srec end restores what was set up)");
            if (report.errors.Count + report.warnings.Count + report.notes.Count > 0) sb.Append('\n').Append(report.Summary(20));
            return sb.ToString();
        }

        static string ViewerCamera(SceneReplayer r, double t, bool gameCam)
        {
            CameraControl.captureLock = false;
            if (gameCam)
            {
                var g = new GameCamState();
                bool ok;
                try { ok = r.GameCam(t, g) && g.ok; } catch { ok = false; }
                var p = g.rendered.ok ? g.rendered : g.clean;
                var cam = RenderHost.WorldCam();
                float fov = p.fov > 0f ? p.fov : cam != null ? cam.fieldOfView : 50f;
                if (ok && p.ok && CameraControl.Set(p.p, p.r, fov)) return "free camera at the recorded game camera";
            }
            if (!CameraControl.free) CameraControl.SetFree(true);
            return CameraControl.free ? "free camera at the current pose" : "no gameplay camera";
        }

        static string End()
        {
            if (probe != null) StopProbe("srec end");
            if (viewer == null)
            {
                if (pending == null) return NoViewer;
                string rel = ReleaseHandover(pending);
                pending = null;
                pendingFrom = "";
                return "no recording loaded; released the pending record handover (" + rel + ")";
            }
            var r = viewer;
            string dir = viewerDir;
            playing = false;
            viewer = null;
            viewerDir = "";
            var report = new SpecReport();
            try { r.End(report); }
            catch (Exception e) { report.Error("srec end", e.GetType().Name + ": " + e.Message); }
            return "srec end: " + dir + " released, scene restored" + (report.errors.Count + report.warnings.Count + report.notes.Count > 0 ? "\n" + report.Summary(30) : "");
        }

        static string Play(List<string> a)
        {
            if (viewer == null) return NoViewer;
            if (probe != null && probe.kind == "camera") return "camera probe running: srec probe stop first";
            if (a.Count > 2)
            {
                double sp = speed;
                bool lp = false;
                for (int i = 2; i < a.Count; i++)
                {
                    string v = a[i].ToLowerInvariant();
                    double x;
                    if (v == "loop") lp = true;
                    else if (v == "once") lp = false;
                    else if (double.TryParse(v.TrimEnd('x'), NumberStyles.Float, IC, out x) && x != 0.0 && Math.Abs(x) <= 100.0) sp = x;
                    else return "usage: srec play [speed (non-zero, |speed| <= 100)] [loop|once]";
                }
                speed = sp;
                looping = lp;
            }
            double dur = Duration(viewer), t = viewer.T;
            if (!looping && speed > 0.0 && t >= dur) viewer.T = 0.0;
            if (!looping && speed < 0.0 && t <= 0.0) viewer.T = dur;
            playing = true;
            return "playing x" + F(speed) + (looping ? " loop" : "") + " from T " + F3(viewer.T) + " / " + F3(dur) + " s";
        }

        static string Pause()
        {
            if (viewer == null) return NoViewer;
            playing = false;
            return "paused: " + ViewerLine();
        }

        static string Seek(List<string> a)
        {
            if (viewer == null) return NoViewer;
            if (a.Count < 3) return "usage: srec seek <seconds|start|end|@event[+-s]>";
            double dur = Duration(viewer), t;
            string err;
            if (!ParseTime(a[2], viewer.T, dur, EventTimeOf(viewer), out t, out err)) return err;
            viewer.T = Clamp(t, 0.0, dur);
            return ViewerLine();
        }

        static string Step(List<string> a)
        {
            if (viewer == null) return NoViewer;
            playing = false;
            double dur = Duration(viewer), t = viewer.T;
            string v = a.Count > 2 ? a[2].ToLowerInvariant() : "next";
            if (v == "next" || v == "back" || v == "prev")
            {
                int n = SampleCount(viewer);
                if (n <= 0) return "the recording has no samples";
                int at = SafeSampleAt(viewer, t);
                int i = StepSample(at, SafeSampleTime(viewer, at), t, v != "next", n);
                t = SafeSampleTime(viewer, i);
            }
            else
            {
                double dt;
                if (!double.TryParse(v, NumberStyles.Float, IC, out dt)) return "usage: srec step [dt seconds | next | back]";
                t += dt;
            }
            viewer.T = Clamp(t, 0.0, dur);
            return ViewerLine();
        }

        static string ViewerLine()
        {
            var r = viewer;
            if (r == null) return NoViewer;
            double t = r.T;
            int n = SampleCount(r);
            return "T " + F3(t) + " / " + F3(Duration(r)) + " s  sample " + (n > 0 ? SafeSampleAt(r, t).ToString(IC) : "-") + "/" + n + "  " +
                   (playing ? "playing x" + F(speed) + (looping ? " loop" : "") : "paused");
        }

        static void ViewerTick()
        {
            var r = viewer;
            if (r == null || !playing) return;
            bool stopped;
            r.T = Advance(r.T, Time.unscaledDeltaTime * speed, Duration(r), looping, out stopped);
            if (stopped) playing = false;
        }

        static void HoldFreeze()
        {
            float gm = 0f;
            try { gm = GameManager.TimeScale; } catch { gm = 0f; }
            if (Time.timeScale == 0f && gm == 0f) return;
            freezeLeaks++;
            try { GameManager.TimeScale = 0f; } catch { }
            Time.timeScale = 0f;
        }

        static double Duration(SceneReplayer r)
        {
            try { return r.duration; } catch { return 0.0; }
        }

        static int SampleCount(SceneReplayer r)
        {
            try { return r.sampleCount; } catch { return 0; }
        }

        static int SafeSampleAt(SceneReplayer r, double t)
        {
            try { return r.SampleAt(t); } catch { return 0; }
        }

        static double SafeSampleTime(SceneReplayer r, int i)
        {
            try { return r.SampleTime(i); } catch { return 0.0; }
        }

        static Func<string, double> EventTimeOf(SceneReplayer r)
        {
            return name =>
            {
                try { return r.EventTime(name); } catch { return double.NaN; }
            };
        }

        static string ReleaseHandover(ReplayHandover h)
        {
            int cullers = 0, kept = 0;
            if (h.forced != null)
                foreach (var b in h.forced)
                {
                    if (b == null) continue;
                    try { b.valid = true; b.RefreshRuntimeState(); cullers++; } catch (Exception e) { Fail("handover culler", e); }
                }
            if (h.keptLoaded != null)
            {
                var m = SubsceneCullingManager.instance;
                foreach (var c in h.keptLoaded)
                {
                    if (c == null) continue;
                    try { m.RemoveAlwaysLoaded(c); kept++; } catch (Exception e) { Fail("handover subscene", e); }
                }
            }
            return cullers + " culler(s) valid again, " + kept + " subscene(s) no longer kept loaded";
        }

        static string HandoverText(ReplayHandover h)
        {
            return (h.forced != null ? h.forced.Count : 0) + " forced culler(s), " + (h.keptLoaded != null ? h.keptLoaded.Count : 0) + " kept subscene(s)";
        }

        // ------------------------------------------------------------------ manual record

        static string Record(List<string> a)
        {
            string v = a.Count > 2 ? a[2].ToLowerInvariant() : "status";
            switch (v)
            {
                case "start": return RecordStart(a);
                case "stop":
                    if (manual == null) return SceneRecorder.active != null ? "the active recording belongs to mcam (mcam stop)" : "not recording";
                    return RecordStop(a.Count > 3 && a[3].ToLowerInvariant() == "keep", "srec record stop");
                case "status":
                    if (manual != null) return RecordLine(manual);
                    return SceneRecorder.active != null ? "recording (mcam): " + RecorderStatus(SceneRecorder.active) : "not recording";
            }
            return RecordUsage;
        }

        static string RecordStart(List<string> a)
        {
            if (a.Count < 4) return RecordUsage;
            string busy = Busy();
            if (busy != null) return busy;
            if (manual != null) return "already recording -> " + manual.dir + " (srec record stop first)";
            if (viewer != null) return "a recording is loaded in the viewer: srec end first";
            if (probe != null) return "a probe is running: srec probe stop first";
            if (MultiCamHide.Active) { MultiCamHide.Restore(); DevLog.Write("[srec] an mcam hide / backfaces set was still applied: restored before the record scan"); }
            string dir;
            try { dir = Path.GetFullPath(a[3]); } catch (Exception e) { return "bad dir: " + e.Message; }
            var report = new SpecReport();
            MultiCamPlan plan = null;
            RecordSpec baseSpec = null;
            string specPath = OwnArg(a, 4, "spec");
            if (specPath != null)
            {
                plan = LoadSpec(specPath, report);
                if (plan != null && plan.spec != null && plan.spec.replay != null && plan.spec.replay.record != null) baseSpec = plan.spec.replay.record;
            }
            bool audioOn, overwrite;
            var spec = RecordArgsOn(baseSpec ?? new RecordSpec(), a, 4, report, out audioOn, out overwrite);
            if (File.Exists(Path.Combine(dir, ReplayConst.RecordFile)) && !overwrite) report.Error("srec.dir", dir + " already holds a recording (use a new folder or overwrite=on)");
            var keep = KeepCullers(spec, report);
            if (!report.ok) return "srec record start refused:\n" + report.Summary(20);
            AudioCapSpec aspec = null;
            if (audioOn)
            {
                aspec = new AudioCapSpec();
                aspec.capture = "record";
                string why;
                try { why = AudioCapture.Preflight(aspec); } catch (Exception e) { why = e.GetType().Name + ": " + e.Message; }
                if (why != null) return "srec record start refused: audio preflight: " + why;
            }
            var m = new ManualRecord();
            m.spec = spec;
            m.dir = dir;
            m.audioOn = audioOn;
            m.audio = aspec;
            m.line = string.Join(" ", a.ToArray());
            m.plan = plan;
            KeepLoaded(m, keep);
            if (KeepReady(m) && !Settles(m)) return RecordBegin(m, report);
            return StartWaiting(m, report);
        }

        static bool Settles(ManualRecord m) { return m.keep.Count > 0 && m.spec.settleFrames > 0; }

        static string StartWaiting(ManualRecord m, SpecReport report)
        {
            m.waiting = true;
            m.waitUntil = Time.realtimeSinceStartup + KeepTimeout;
            m.startReport = report;
            manual = m;
            EnsureHost();
            return "srec record start: waiting for " + m.keep.Count + " kept subscene(s) to load and activate (up to " + KeepTimeout.ToString("0", IC) + " s): " +
                   string.Join(", ", m.keptNames.ToArray()) + "; the recording starts " + (Settles(m) ? m.spec.settleFrames + " frame(s) after that (record.settleFrames, late-awake content)" : "then") +
                   " (srec record status shows it, srec record stop cancels)" +
                   (report.warnings.Count + report.notes.Count > 0 ? "\n" + report.Summary(20) : "");
        }

        static string RecordBegin(ManualRecord m, SpecReport report)
        {
            var spec = m.spec;
            string dir = m.dir;
            if (Time.timeScale != 1f) report.Warn("srec", "timeScale is " + Time.timeScale.ToString("0.###", IC) + ": game time runs at that rate while recording");
            if (pending != null)
            {
                report.Note("srec", "released the pending handover from " + pendingFrom + " (" + ReleaseHandover(pending) + ")");
                pending = null;
                pendingFrom = "";
            }
            try { Directory.CreateDirectory(dir); } catch (Exception e) { ReleaseKept(m); return "cannot create " + dir + ": " + e.Message; }
            SavePacing(m);
            ApplyPacing(m);
            string fail = null;
            try
            {
                if (m.plan != null) ArmEvents(m, report);
                float aspect = Screen.height > 0 ? Screen.width / (float)Screen.height : 16f / 9f;
                m.prep = SceneRecorder.Prepare(spec, new List<CamPose>(), aspect, report);
                if (report.ok)
                {
                    m.header = Header(spec);
                    m.header.keptCullers.AddRange(m.keptNames);
                    m.rec = SceneRecorder.Begin(spec, dir, m.header, report);
                }
                if (m.rec == null || !report.ok) fail = "refused by the recorder";
            }
            catch (Exception e) { report.Error("srec record", e.GetType().Name + ": " + e.Message); fail = "failed"; }
            if (fail == null && m.audioOn) fail = StartAudio(m, report);
            if (fail != null)
            {
                Abandon(m, report);
                return "srec record start " + fail + ", restored:\n" + report.Summary(20);
            }
            manual = m;
            try { m.rec.Event("srec", "record start", m.line); } catch (Exception e) { report.Warn("srec.event", e.Message); }
            try { Virtual(m); } catch (Exception e) { report.Warn("srec.virtual", e.GetType().Name + ": " + e.Message); }
            EnsureHost();
            var sb = new StringBuilder();
            sb.Append("recording -> ").Append(dir).Append("  clock ").Append(spec.clock == "fixed" ? "fixed:" + spec.rate : "real")
              .Append(", pacing captureFramerate ").Append(m.wantCapture).Append(" vSyncCount ").Append(m.wantVsync).Append(" targetFrameRate ").Append(m.wantTarget)
              .Append(" (display ").Append(m.refresh).Append(" Hz), roots ").Append(string.Join(",", spec.roots.ToArray()));
            if (m.keptNames.Count > 0) sb.Append(", kept ").Append(string.Join(",", m.keptNames.ToArray()));
            if (m.events != null) sb.Append(", ").Append(m.plan.spec.events.Count).Append(" spec event(s) armed");
            sb.Append(m.audioOn ? ", audio capture on" : ", no audio").Append("; srec event <name> marks a moment, srec record stop [keep] ends it");
            if (report.warnings.Count + report.notes.Count > 0) sb.Append('\n').Append(report.Summary(20));
            return sb.ToString();
        }

        static MultiCamPlan LoadSpec(string path, SpecReport report)
        {
            string full;
            try { full = Path.GetFullPath(path); } catch (Exception e) { report.Error("srec.spec", "bad path '" + path + "': " + e.Message); return null; }
            if (!File.Exists(full)) { report.Error("srec.spec", "no spec file " + full); return null; }
            MultiCamPlan p;
            try
            {
                p = MultiCamPlan.Load(full, false);
                if (p.spec != null && p.spec.replay != null) p = MultiCamPlan.Load(full, true);
            }
            catch (Exception e) { report.Error("srec.spec", full + ": " + e.GetType().Name + ": " + e.Message); return null; }
            if (p.spec == null || !p.report.ok) { report.Error("srec.spec", full + " has errors:\n" + p.report.Summary(10)); return null; }
            report.Note("srec.spec", full + ": " + p.spec.events.Count + " event(s)" + (p.spec.replay != null ? ", replay.record as the record defaults" : ""));
            return p;
        }

        static List<SubsceneCuller> KeepCullers(RecordSpec spec, SpecReport report)
        {
            var r = new List<SubsceneCuller>();
            if (spec.keep == null) return r;
            foreach (var raw in spec.keep)
            {
                string k = (raw ?? "").Trim();
                if (k.Length == 0) continue;
                SubsceneCuller c = null;
                try { c = SceneRecorder.FindCuller(k); } catch (Exception e) { report.Error("srec.keep", k + ": " + e.Message); continue; }
                if (c == null) report.Error("srec.keep", "no subscene culler '" + k + "'");
                else if (!r.Contains(c)) r.Add(c);
            }
            return r;
        }

        static void KeepLoaded(ManualRecord m, List<SubsceneCuller> keep)
        {
            foreach (var c in keep)
            {
                m.keep.Add(c);
                try { m.keptNames.Add(c.SceneName); } catch { m.keptNames.Add(c.name); }
                if (MultiCamSession.IsKept(c)) continue;
                SubsceneCullingManager.instance.AddAlwaysLoaded(c);
                m.kept.Add(c);
            }
        }

        static bool KeepReady(ManualRecord m)
        {
            foreach (var c in m.keep)
            {
                try { if (c != null && (!c.IsLoaded || !c.IsActive || c.InProgress)) return false; } catch { return false; }
            }
            return true;
        }

        static int KeepReadyCount(ManualRecord m)
        {
            int n = 0;
            foreach (var c in m.keep) { try { if (c != null && c.IsLoaded && c.IsActive && !c.InProgress) n++; } catch { } }
            return n;
        }

        static void ReleaseKept(ManualRecord m)
        {
            if (m.kept.Count == 0) return;
            try { var mgr = SubsceneCullingManager.instance; foreach (var c in m.kept) if (c != null) mgr.RemoveAlwaysLoaded(c); }
            catch (Exception e) { Fail("keep release", e); }
            m.kept.Clear();
        }

        static void WaitTick(ManualRecord m)
        {
            bool ready = KeepReady(m), late = Time.realtimeSinceStartup > m.waitUntil;
            if (!ready) m.settleFrom = -1;
            else if (Settles(m))
            {
                if (m.settleFrom < 0) m.settleFrom = Time.frameCount;
                m.settled = Time.frameCount - m.settleFrom;
                if (m.settled < m.spec.settleFrames && !late) return;
            }
            if (!ready && !late) return;
            m.waiting = false;
            manual = null;
            var report = m.startReport ?? new SpecReport();
            if (!ready) report.Warn("srec.keep", (m.keep.Count - KeepReadyCount(m)) + " of " + m.keep.Count + " kept subscene(s) not loaded and active after " + KeepTimeout.ToString("0", IC) + " s: they join the recording when they load");
            else if (m.settled >= 0) report.Note("srec.settle", "sample 0 " + m.settled + " frame(s) after the kept subscenes were loaded and active (record.settleFrames " + m.spec.settleFrames + ")");
            string res = RecordBegin(m, report);
            lastRecord = res.Split('\n')[0];
            DevLog.Write("[srec] record start after the kept subscenes: " + res);
        }

        // Spec events of `srec record start ... spec=<file>` (research C8): armed like the mcam record pass, polled in the record
        // step, the first fire of each name stored as a kind "event" row (a post of the same name already counts as its fire).
        // Arm problems are warnings: an event that cannot arm never fires, the recording still runs.
        static void ArmEvents(ManualRecord m, SpecReport report)
        {
            var sp = m.plan.spec;
            if (sp == null || sp.events == null || sp.events.Count == 0) return;
            var c = m.ctx;
            c.spec = sp;
            c.rate = m.spec.clock == "fixed" ? Math.Max(1, m.spec.rate) : 60;
            c.target = MultiCamTargets.Sample;
            c.log = s => DevLog.Write("[srec] " + s);
            foreach (var e in sp.events) if (e.type == "impact" || e.type == "box" || e.type == "cond") { m.targets = true; break; }
            var arm = new SpecReport();
            if (m.targets) MultiCamTargets.Begin(sp, arm);
            m.events = new MultiCamEvents(sp, m.plan);
            m.events.row = (kind, name, source, detail) => ManualEventRow(m, kind, name, source, detail);
            c.eventTime = m.events.TimeOf;
            m.onPosted = (name, detail) => m.fired.Add(name);
            SceneRecorder.posted += m.onPosted;
            m.events.Arm(c, arm, null);
            foreach (var e in arm.errors) report.Warn("srec.spec.events", e + " (it will not fire)");
            foreach (var w in arm.warnings) report.Warn("srec.spec.events", w);
        }

        static void ManualEventRow(ManualRecord m, string kind, string name, string source, string detail)
        {
            if (kind == "error" || kind == "warn") { DevLog.Write("[srec] event " + name + " " + kind + ": " + detail); return; }
            if (kind != "event" || string.IsNullOrEmpty(name) || !m.fired.Add(name) || m.rec == null) return;
            m.eventRows++;
            try { m.rec.Event("event", name, string.IsNullOrEmpty(source) ? detail : source + ": " + detail); } catch (Exception e) { Fail("event row", e); }
        }

        static void ManualEvents(ManualRecord m)
        {
            if (m.events == null) return;
            if (m.targets) MultiCamTargets.Step(m.ctx);
            m.events.PollFrame(m.ctx);
            m.events.PollStep(m.ctx);
        }

        static void DisarmEvents(ManualRecord m)
        {
            if (m.onPosted != null) { SceneRecorder.posted -= m.onPosted; m.onPosted = null; }
            if (m.events != null) { try { m.events.Disarm(); } catch (Exception e) { Fail("events disarm", e); } }
            if (m.targets)
            {
                m.targets = false;
                if (MultiCam.runner == null) { try { MultiCamTargets.End(); } catch (Exception e) { Fail("targets end", e); } }
            }
        }

        static string PostEvent(List<string> a)
        {
            if (a.Count < 3) return EventUsage;
            string name = a[2];
            if (!SceneRecorder.EventName(name)) return "bad event name '" + name + "': " + EventUsage.Substring(7);
            string detail = a.Count > 3 ? string.Join(" ", a.GetRange(3, a.Count - 3).ToArray()) : "srec event";
            var r = SceneRecorder.active;
            int sample = r != null ? r.samples : -1;
            if (!SceneRecorder.PostEvent(name, detail)) return "no active recording: event '" + name + "' not stored (srec record start or mcam record first)";
            string owner = manual != null && manual.rec == r ? "manual recording " + manual.dir : "mcam recording";
            return "event '" + name + "' stored with sample " + sample + " of the " + owner;
        }

        static string StartAudio(ManualRecord m, SpecReport report)
        {
            bool started = false;
            try
            {
                AudioCapture.Ensure();
                AudioCapture.clockBase = m.header.t0Real;
                started = AudioCapture.Start(m.audio, m.dir, report);
            }
            catch (Exception e) { report.Error("audio", e.GetType().Name + ": " + e.Message); started = false; }
            if (!started) return "refused by the audio capture";
            try
            {
                AudioCapture.Marker("s0", 0, 0.0, 0);
                m.rec.Event("marker", "s0", "lag 0");
                m.lastMarker = 0;
                SceneHooks.preUpdate += OnPreUpdate;
                m.hooked = true;
                SceneHooks.Ensure();
            }
            catch (Exception e) { report.Error("audio", "markers: " + e.GetType().Name + ": " + e.Message); return "failed (audio markers)"; }
            return null;
        }

        static void Abandon(ManualRecord m, SpecReport report)
        {
            if (m.audioOn) AudioOff("srec record start failed", report);
            Unhook(m);
            DisarmEvents(m);
            if (m.rec != null) { try { m.rec.End(report); } catch (Exception e) { report.Error("srec record", "End: " + e.GetType().Name + ": " + e.Message); } }
            if (m.prep != null) { try { m.prep.Restore(); } catch (Exception e) { report.Error("srec record", "Restore: " + e.GetType().Name + ": " + e.Message); } }
            ReleaseKept(m);
            RestorePacing(m);
        }

        static void Unhook(ManualRecord m)
        {
            if (!m.hooked) return;
            m.hooked = false;
            SceneHooks.preUpdate -= OnPreUpdate;
            try { SceneHooks.Release(); } catch (Exception e) { Fail("hooks release", e); }
        }

        static string RecordStop(bool keep, string why)
        {
            var m = manual;
            if (m == null) return "not recording";
            manual = null;
            if (m.waiting)
            {
                m.waiting = false;
                ReleaseKept(m);
                return "srec record start cancelled (" + why + "): it was waiting for " + (m.keep.Count - KeepReadyCount(m)) + " of " + m.keep.Count + " kept subscene(s)";
            }
            var report = new SpecReport();
            if (m.audioOn) AudioOff(why, report);
            Unhook(m);
            DisarmEvents(m);
            double seconds = m.header != null ? Time.timeDbl - m.header.t0 : 0.0;
            try { m.rec.Event("srec", "record stop", keep ? "keep" : ""); } catch { }
            try { FillExtra(m); } catch (Exception e) { report.Warn("record.json", e.GetType().Name + ": " + e.Message); }
            try { m.rec.End(report); } catch (Exception e) { report.Error("srec record stop", "End: " + e.GetType().Name + ": " + e.Message); }
            string hand;
            try
            {
                if (keep)
                {
                    pending = m.prep.HandOver();
                    pendingFrom = m.dir;
                    if (pending != null) { foreach (var c in m.kept) if (c != null && !pending.keptLoaded.Contains(c)) pending.keptLoaded.Add(c); m.kept.Clear(); }
                    hand = pending != null ? "handover kept for the next srec load (" + HandoverText(pending) + ")" : "no handover returned";
                }
                else
                {
                    m.prep.Restore();
                    hand = "record preparation restored" + (m.kept.Count > 0 ? ", " + m.kept.Count + " kept subscene(s) released" : "");
                }
            }
            catch (Exception e) { report.Error("srec record stop", (keep ? "HandOver: " : "Restore: ") + e.GetType().Name + ": " + e.Message); hand = "restore failed"; }
            ReleaseKept(m);
            RestorePacing(m);
            return "recording stopped -> " + m.dir + ": " + m.rec.samples + " samples, " + m.rec.objects + " objects, " + (m.rec.bytes / 1048576.0).ToString("0.0", IC) + " MB, " +
                   seconds.ToString("0.00", IC) + " s, pacing changes " + m.changes + (m.audioLost ? ", audio window closed early" : "") + "; " + hand +
                   (report.errors.Count + report.warnings.Count + report.notes.Count > 0 ? "\n" + report.Summary(20) : "");
        }

        static string RecordLine(ManualRecord m)
        {
            if (m.waiting)
                return "waiting to record " + m.dir + ": " + KeepReadyCount(m) + " of " + m.keep.Count + " kept subscene(s) loaded and active" +
                       (m.settleFrom >= 0 ? ", settling " + Math.Max(0, m.settled) + "/" + m.spec.settleFrames + " frame(s)" : "") + ", " +
                       Math.Max(0f, m.waitUntil - Time.realtimeSinceStartup).ToString("0", IC) + " s left (srec record stop cancels)";
            double t = m.header != null ? Time.timeDbl - m.header.t0 : 0.0;
            return "recording " + m.dir + "  " + RecorderStatus(m.rec) + "  t " + t.ToString("0.00", IC) + " s  fps " + RecentFps(m.frameTimes).ToString("0.0", IC) +
                   "  drift " + m.driftMs.ToString("0.0", IC) + " ms  pacing changes " + m.changes + "  audio " + (m.audioOn ? (m.audioLost ? "closed" : "on") : "off") +
                   (m.events != null ? "  spec events fired " + m.eventRows : "");
        }

        static double RecentFps(List<float> dts)
        {
            int n = Math.Min(60, dts.Count);
            if (n == 0) return 0.0;
            double s = 0.0;
            for (int i = dts.Count - n; i < dts.Count; i++) s += dts[i];
            return s > 0.0 ? n / s : 0.0;
        }

        static void OnPreUpdate()
        {
            var m = manual;
            if (m == null || !m.audioOn || m.rec == null || m.audioLost) return;
            int n = m.rec.samples, every = Math.Max(1, m.audio.markerEvery);
            if (n == m.lastMarker || n % every != 0 || !AudioCapture.capturing) return;
            m.lastMarker = n;
            string label = "s" + n.ToString(IC);
            AudioCapture.Marker(label, n, Time.timeDbl - m.header.t0, 0);
            m.rec.Event("marker", label, "lag 0");
        }

        static void AudioOff(string why, SpecReport report)
        {
            bool zero = false;
            for (int i = 0; i < 2 && !zero && AudioCapture.capturing; i++)
            {
                try { zero = AudioCapture.ForceLevel(0f); } catch (Exception e) { report.Error("audio", "ForceLevel(0): " + e.GetType().Name + ": " + e.Message); }
            }
            if (AudioCapture.capturing && !zero) report.Error("audio", "rtpc 0 readback mismatch after a retry (" + why + ")");
            try { AudioCapture.Stop(why); } catch (Exception e) { report.Error("audio", "Stop: " + e.GetType().Name + ": " + e.Message); }
        }

        static void RecordTick(ManualRecord m)
        {
            if (m.rec == null) return;
            m.frames++;
            if (m.frames > 1) m.frameTimes.Add(Time.unscaledDeltaTime);
            if (Time.captureFramerate != m.wantCapture || QualitySettings.vSyncCount != m.wantVsync || Application.targetFrameRate != m.wantTarget)
            {
                m.changes++;
                string was = "captureFramerate " + Time.captureFramerate + ", vSyncCount " + QualitySettings.vSyncCount + ", targetFrameRate " + Application.targetFrameRate;
                ApplyPacing(m);
                try { m.rec.Event("pacing", "restored", was); } catch { }
                if (m.changes <= 3) DevLog.Write("[srec] record pacing changed (" + was + "), restored");
            }
            if (m.audioOn && !m.audioLost && m.frames > 120 && !AudioCapture.capturing)
            {
                m.audioLost = true;
                try { m.rec.Event("audio", "capture closed", "AudioCapture stopped during the record"); } catch { }
                DevLog.Write("[srec] audio capture closed during the record (watchdog or stop); the record continues without sound");
            }
            double drift = Math.Abs((Time.realtimeSinceStartupDbl - m.header.t0Real) - (Time.timeDbl - m.header.t0)) * 1000.0;
            m.driftMs = drift;
            if (drift > m.driftMaxMs) m.driftMaxMs = drift;
            Virtual(m);
            try { ManualEvents(m); } catch (Exception e) { Fail("spec events", e); }
        }

        static void Virtual(ManualRecord m)
        {
            var cam = RenderHost.WorldCam();
            var c = m.ctx;
            c.step = m.rec.samples;
            c.frame = Time.frameCount;
            c.t = Time.timeDbl - m.header.t0;
            c.dt = Time.deltaTime;
            c.realTime = Time.realtimeSinceStartupDbl - m.header.t0Real;
            c.camera = cam;
            c.screenW = Screen.width;
            c.screenH = Screen.height;
            c.aspect = Screen.height > 0 ? Screen.width / (float)Screen.height : c.aspect;
            MultiCamRig.SampleGame(cam, c);
            m.rec.SetVirtual(c);
        }

        static RecordHeader Header(RecordSpec spec)
        {
            var h = new RecordHeader();
            h.created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", IC);
            h.spec = "srec record";
            try { h.level = Application.loadedLevelName; } catch { h.level = ""; }
            h.recorderVersion = Boot.Version;
            h.clock = spec.clock;
            h.captureFramerate = Time.captureFramerate;
            int sub, sp;
            if (G.CurrentSavepoint(out sub, out sp)) { h.subscene = sub; h.savepoint = sp; }
            h.streamVersion = Levels.StreamVersion;
            h.screenW = Screen.width;
            h.screenH = Screen.height;
            h.fixedDeltaTime = Time.fixedDeltaTime;
            h.t0 = Time.timeDbl;
            h.t0Real = Time.realtimeSinceStartupDbl;
            try { h.stepAtT0 = LogicManager.stepCount; } catch { h.stepAtT0 = 0; }
            var cam = RenderHost.WorldCam();
            h.gameFov = cam != null ? cam.fieldOfView : 0f;
            try
            {
                foreach (var c in SubsceneCullingManager.instance.GetAllSubsceneCullers())
                    if (c != null && c.IsLoaded && !h.subscenes.Contains(c.SceneName)) h.subscenes.Add(c.SceneName);
            }
            catch (Exception e) { Fail("header subscenes", e); }
            h.posEps = spec.posEps;
            h.rotEps = spec.rotEps;
            h.cut = spec.cut;
            h.vSyncCount = QualitySettings.vSyncCount;
            h.targetFrameRate = Application.targetFrameRate;
            h.refreshRate = Screen.currentResolution.refreshRate;
            return h;
        }

        static void FillExtra(ManualRecord m)
        {
            var p = PacingStats(m.frameTimes);
            p["clock"] = m.spec.clock;
            if (m.spec.clock == "fixed") p["rate"] = m.spec.rate;
            p["captureFramerate"] = m.wantCapture;
            p["vSyncCount"] = m.wantVsync;
            p["targetFrameRate"] = m.wantTarget;
            p["refreshRate"] = m.refresh;
            p["changes"] = m.changes;
            p["driftMsEnd"] = Math.Round(m.driftMs, 3);
            p["driftMsMax"] = Math.Round(m.driftMaxMs, 3);
            m.rec.extra["source"] = "srec record";
            m.rec.extra["command"] = m.line;
            m.rec.extra["pacing"] = p;
            if (m.keptNames.Count > 0) m.rec.extra["keptSubscenes"] = new List<object>(m.keptNames.ToArray());
            var settle = new Json.Obj();
            settle["frames"] = m.spec.settleFrames;
            settle["waited"] = m.settled;
            settle["applied"] = Settles(m);
            m.rec.extra["settle"] = settle;
            if (m.plan != null)
            {
                m.rec.extra["spec"] = m.plan.path;
                if (m.events != null) m.rec.extra["specEvents"] = m.events.Summary();
            }
            var au = new Json.Obj();
            au["capture"] = m.audioOn ? "record" : "none";
            if (m.audioOn) au["closedEarly"] = m.audioLost;
            m.rec.extra["audio"] = au;
        }

        static void SavePacing(ManualRecord m)
        {
            m.captureBefore = Time.captureFramerate;
            m.vsyncBefore = QualitySettings.vSyncCount;
            m.targetBefore = Application.targetFrameRate;
            try { m.vsyncVirtualBefore = FramerateController.GetVirtualVSyncCount(); m.autoBefore = FramerateController.automatic; }
            catch { m.vsyncVirtualBefore = m.vsyncBefore; m.autoBefore = false; }
            m.refresh = Screen.currentResolution.refreshRate;
            PacingFor(m.spec.clock, m.spec.rate, m.refresh, out m.wantCapture, out m.wantVsync, out m.wantTarget);
            m.paced = true;
        }

        static void ApplyPacing(ManualRecord m)
        {
            try { FramerateController.automatic = false; FramerateController.SetVSyncCount(m.wantVsync); } catch { }
            QualitySettings.vSyncCount = m.wantVsync;
            Application.targetFrameRate = m.wantTarget;
            Time.captureFramerate = m.wantCapture;
        }

        static void RestorePacing(ManualRecord m)
        {
            if (!m.paced) return;
            m.paced = false;
            Time.captureFramerate = m.captureBefore;
            try { FramerateController.SetVSyncCount(m.vsyncVirtualBefore); FramerateController.automatic = m.autoBefore; } catch { }
            QualitySettings.vSyncCount = m.vsyncBefore;
            Application.targetFrameRate = m.targetBefore;
        }

        // ------------------------------------------------------------------ probes

        static string Probe(List<string> a)
        {
            string v = a.Count > 2 ? a[2].ToLowerInvariant() : "";
            if (v == "stop")
            {
                if (probe == null) return "no probe running";
                StopProbe("srec probe stop");
                return lastProbe;
            }
            if (v == "result") return lastProbeFull.Length > 0 ? lastProbeFull : "no probe result yet";
            string busy = Busy();
            if (busy != null) return busy;
            if (probe != null) return "a probe is running (" + ProbeLine(probe) + "): srec probe stop first";
            if (manual != null) return "recording in progress: srec record stop first";
            if (v == "camera") return CameraProbeStart(a);
            int n = 60, from = 2;
            if (v.Length > 0 && v.IndexOf('=') < 0)
            {
                if (!int.TryParse(v, NumberStyles.Integer, IC, out n) || n < 1 || n > 10000) return "usage: srec probe [n] [eps=m] [rot=deg] [out=dir] | srec probe camera [n] ...";
                from = 3;
            }
            if (viewer == null) return "srec probe needs a loaded recording (srec load <dir>): it compares the replayed transforms at lateWrite with the last component OnPreCull";
            return WriteProbeStart(a, from, n);
        }

        static string ProbeLine(ProbeRun p)
        {
            return p.kind + " probe running: " + p.phase + " frame " + p.frame + "/" + p.frames;
        }

        static string WriteProbeStart(List<string> a, int from, int n)
        {
            var report = new SpecReport();
            var own = new Dictionary<string, string>();
            var extra = KeyValues(a, from, null, WriteProbeOwnKeys, own, report);
            foreach (var k in extra.order) report.Error("srec.probe", "unknown key '" + k + "' (eps, rot, out)");
            float eps = OwnFloat(own, "eps", 1e-5f, report), rot = OwnFloat(own, "rot", 1e-3f, report);
            string outDir = OwnDir(own, viewerDir, report);
            if (!report.ok) return "srec probe refused:\n" + report.Summary(10);
            var cam = RenderHost.WorldCam();
            if (cam == null) return "no gameplay camera";
            var rec = viewer.tracks != null ? viewer.tracks.rec : null;
            if (rec == null) return "the replayer has no object table";
            var list = new List<Transform>();
            for (int id = 0; id < rec.objects.Count; id++)
            {
                var o = rec.objects[id];
                if (o == null || (o.flags & SrecConst.ObjCamXf) != 0) continue;
                if (o.kind != SrecConst.KindTransform && o.kind != SrecConst.KindBoyBone && o.kind != SrecConst.KindClothBone && o.kind != SrecConst.KindSpawned) continue;
                Transform t = null;
                try { t = viewer.Resolved(id) as Transform; } catch { t = null; }
                if (t != null) list.Add(t);
            }
            if (list.Count == 0) return "no resolved transform in the recording";
            var p = new ProbeRun();
            p.kind = "write";
            p.phase = "starting";
            p.frames = n;
            p.dir = outDir;
            p.tick = Time.frameCount;
            probe = p;
            EnsureHost();
            host.StartCoroutine(WriteProbe(p, list, cam, eps, rot));
            return "write-order probe started: " + list.Count + " driven transforms, " + n + " frames (eps " + eps.ToString("R", IC) + " m, rot " + rot.ToString("R", IC) +
                   " deg); srec status shows progress, srec probe result the list, " + WriteProbeFile + " -> " + outDir;
        }

        static IEnumerator WriteProbe(ProbeRun p, List<Transform> list, Camera cam, float eps, float rotEps)
        {
            int count = list.Count;
            var lp = new Vector3[count];
            var lr = new Quaternion[count];
            var ls = new Vector3[count];
            var hits = new int[count];
            var maxPos = new double[count];
            var maxRot = new double[count];
            int lateAt = -1, cullAt = -1, compared = 0;
            float e2 = eps * eps;
            Action onLate = () =>
            {
                for (int i = 0; i < count; i++)
                {
                    var t = list[i];
                    if (t == null) continue;
                    lp[i] = t.localPosition; lr[i] = t.localRotation; ls[i] = t.localScale;
                }
                lateAt = Time.frameCount;
            };
            Action<Camera> onCull = c =>
            {
                int f = Time.frameCount;
                if (lateAt != f || cullAt == f || compared >= p.frames) return;
                cullAt = f;
                compared++;
                for (int i = 0; i < count; i++)
                {
                    var t = list[i];
                    if (t == null) continue;
                    double dp = (t.localPosition - lp[i]).magnitude, dr = RotDeg(t.localRotation, lr[i]);
                    bool ds = (t.localScale - ls[i]).sqrMagnitude > e2;
                    if (dp * dp <= e2 && dr <= rotEps && !ds) continue;
                    hits[i]++;
                    if (dp > maxPos[i]) maxPos[i] = dp;
                    if (dr > maxRot[i]) maxRot[i] = dr;
                }
            };
            SceneCommandsTap tap = null;
            p.cleanup = () =>
            {
                SceneHooks.lateWrite -= onLate;
                try { SceneHooks.Release(); } catch (Exception e) { Fail("probe hooks release", e); }
                if (tap != null) { tap.onPreCull = null; UObj.Destroy(tap); }
            };
            string startError = null;
            try
            {
                SceneHooks.lateWrite += onLate;
                SceneHooks.Ensure();
                tap = cam.gameObject.AddComponent<SceneCommandsTap>();
                tap.onPreCull = onCull;
            }
            catch (Exception e) { startError = e.GetType().Name + ": " + e.Message; }
            if (startError != null)
            {
                p.Cleanup();
                Finish(p, "write-order probe failed to start: " + startError, "");
                yield break;
            }
            p.phase = "probing";
            int guard = 0;
            while (compared < p.frames && guard++ < p.frames * 4 + 120)
            {
                p.frame = compared;
                p.tick = Time.frameCount;
                yield return null;
                if (probe != p) yield break;
            }
            p.Cleanup();
            p.frame = compared;
            string summary, full;
            WriteProbeResult(p, list, hits, maxPos, maxRot, compared, CameraPreCullers(cam), out summary, out full);
            Finish(p, summary, full);
        }

        static void WriteProbeResult(ProbeRun p, List<Transform> list, int[] hits, double[] maxPos, double[] maxRot, int compared, List<string> preCullers, out string summary, out string full)
        {
            var idx = new List<int>();
            for (int i = 0; i < list.Count; i++) if (hits[i] > 0 && list[i] != null) idx.Add(i);
            idx.Sort((x, y) => hits[y] != hits[x] ? hits[y].CompareTo(hits[x]) : maxRot[y] != maxRot[x] ? maxRot[y].CompareTo(maxRot[x]) : maxPos[y].CompareTo(maxPos[x]));
            var tsv = new StringBuilder(WriteProbeHeader).Append('\n');
            var lines = new StringBuilder();
            int shown = 0;
            foreach (var i in idx)
            {
                var t = list[i];
                string path = Inspector.PathOf(t);
                string writers = string.Join("; ", Writers(t).ToArray());
                tsv.Append(path).Append('\t').Append(hits[i]).Append('\t').Append(maxPos[i].ToString("0.######", IC)).Append('\t').Append(maxRot[i].ToString("0.####", IC)).Append('\t').Append(writers).Append('\n');
                if (shown++ < 200)
                    lines.Append("\n  ").Append(path).Append("  frames ").Append(hits[i]).Append("  pos ").Append(maxPos[i].ToString("0.######", IC)).Append(" m  rot ")
                         .Append(maxRot[i].ToString("0.####", IC)).Append(" deg  writers: ").Append(writers.Length > 0 ? writers : "none found");
            }
            string file = Path.Combine(p.dir, WriteProbeFile);
            string written;
            try { Directory.CreateDirectory(p.dir); File.WriteAllText(file, tsv.ToString()); written = file; }
            catch (Exception e) { written = "not written (" + e.Message + ")"; }
            summary = "write-order probe: " + compared + " frames, " + list.Count + " driven transforms, " + idx.Count + " changed between lateWrite and the last component OnPreCull; " + written;
            full = summary + lines.ToString() + (idx.Count > 200 ? "\n  ... " + (idx.Count - 200) + " more in " + WriteProbeFile : "") +
                   (idx.Count > 0 ? "\n  world camera OnPreCull components (candidate writers of every row): " + (preCullers.Count > 0 ? string.Join(", ", preCullers.ToArray()) : "none") : "");
        }

        static string CameraProbeStart(List<string> a)
        {
            int n = 30, from = 3;
            if (a.Count > 3 && a[3].IndexOf('=') < 0)
            {
                if (!int.TryParse(a[3], NumberStyles.Integer, IC, out n) || n < 4 || n > 2000) return "usage: srec probe camera [n (4..2000)] [dx=m] [yaw=deg] [eps=m] [rot=deg] [out=dir] [<record key>=value ...]";
                from = 4;
            }
            var report = new SpecReport();
            var own = new Dictionary<string, string>();
            var o = KeyValues(a, from, typeof(RecordSpec), CameraProbeOwnKeys, own, report);
            var spec = new RecordSpec();
            SpecBind.Fill(spec, o, "srec", report);
            float dx = OwnFloat(own, "dx", 1.5f, report), yaw = OwnFloat(own, "yaw", 10f, report), eps = OwnFloat(own, "eps", 1e-5f, report), rot = OwnFloat(own, "rot", 1e-3f, report);
            string outDir = OwnDir(own, ModDir(), report);
            if (!report.ok) return "srec probe camera refused:\n" + report.Summary(10);
            var cam = RenderHost.WorldCam();
            if (cam == null) return "no gameplay camera";
            var p = new ProbeRun();
            p.kind = "camera";
            p.phase = "starting";
            p.frames = 3 + 2 * n + 2;
            p.dir = outDir;
            p.freeze = viewer == null;
            p.tick = Time.frameCount;
            probe = p;
            EnsureHost();
            host.StartCoroutine(CameraProbe(p, n, dx, yaw, eps, rot, spec, report));
            return "camera probe started: " + (p.freeze ? "frozen by the probe" : "viewer frozen") + ", " + n + " frames still then " + n + " frames alternating with a pose " + F(dx) +
                   " m right and " + F(yaw) + " deg yaw; " + ReplayConst.CameraDrivenFile + " -> " + outDir + " (merged: rows of transforms this probe does not scan are kept)";
        }

        static IEnumerator CameraProbe(ProbeRun p, int n, float dx, float dyaw, float eps, float rotEps, RecordSpec spec, SpecReport report)
        {
            var eof = new WaitForEndOfFrame();
            var cam = RenderHost.WorldCam();
            var camT = cam.transform;
            bool wasFree = CameraControl.free, wasLock = CameraControl.captureLock, wasQuiet = CameraControl.quiet, wasPlaying = playing;
            float fov = cam.fieldOfView, near = cam.nearClipPlane, tsBefore = Time.timeScale, gmBefore = Time.timeScale;
            try { gmBefore = GameManager.TimeScale; } catch { gmBefore = tsBefore; }
            Vector3 aP = camT.position;
            Quaternion aR = camT.rotation;
            playing = false;
            p.cleanup = () =>
            {
                if (wasFree) CameraControl.Set(aP, aR, fov);
                else
                {
                    CameraControl.EndCapture();
                    if (cam != null) { cam.fieldOfView = fov; cam.nearClipPlane = near; }
                }
                CameraControl.captureLock = wasLock;
                CameraControl.quiet = wasQuiet;
                if (p.freeze)
                {
                    try { GameManager.TimeScale = gmBefore; } catch { }
                    Time.timeScale = tsBefore;
                }
                playing = wasPlaying && viewer != null;
            };
            if (p.freeze) HoldFreeze();
            CameraControl.quiet = true;
            CameraControl.captureLock = true;
            if (!CameraControl.Set(aP, aR, fov))
            {
                p.Cleanup();
                Finish(p, "camera probe: no gameplay camera", "");
                yield break;
            }
            var bP = aP + camT.right * dx;
            var bR = Quaternion.AngleAxis(dyaw, Vector3.up) * aR;
            p.phase = "settle";
            for (int i = 0; i < 3; i++)
            {
                p.tick = Time.frameCount;
                yield return eof;
                if (probe != p) yield break;
                p.frame++;
            }
            string how;
            var list = ProbeSet(spec, report, camT, out how);
            int count = list.Count;
            var pos = new Vector3[count];
            var rot = new Quaternion[count];
            var scl = new Vector3[count];
            for (int i = 0; i < count; i++) { var t = list[i]; if (t == null) continue; pos[i] = t.position; rot[i] = t.rotation; scl[i] = t.localScale; }
            var control = new HashSet<int>();
            var moved = new HashSet<int>();
            p.phase = "control (camera still)";
            for (int f = 0; f < n; f++)
            {
                p.tick = Time.frameCount;
                yield return eof;
                if (probe != p) yield break;
                Scan(list, pos, rot, scl, control, eps, rotEps);
                p.frame++;
            }
            p.phase = "camera moving";
            for (int f = 0; f < n; f++)
            {
                if (f % 2 == 0) CameraControl.Set(bP, bR, fov); else CameraControl.Set(aP, aR, fov);
                p.tick = Time.frameCount;
                yield return eof;
                if (probe != p) yield break;
                Scan(list, pos, rot, scl, moved, eps, rotEps);
                p.frame++;
            }
            CameraControl.Set(aP, aR, fov);
            p.phase = "restore";
            for (int i = 0; i < 2; i++)
            {
                p.tick = Time.frameCount;
                yield return eof;
                if (probe != p) yield break;
                p.frame++;
            }
            p.Cleanup();
            string summary, full;
            CameraProbeResult(p, list, control, moved, how, out summary, out full);
            if (report.warnings.Count + report.notes.Count > 0) full += "\n" + report.Summary(10);
            Finish(p, summary, full);
        }

        static List<Transform> ProbeSet(RecordSpec spec, SpecReport report, Transform camT, out string how)
        {
            how = "record set";
            List<Transform> set = null;
            try { set = SceneRecorder.RecordSet(spec, true, report); }
            catch (Exception e) { set = null; how = "whole scene (record set unavailable: " + e.GetType().Name + ")"; }
            if (set != null && set.Count > 0) return set;
            if (set != null) how = "whole scene (empty record set)";
            set = new List<Transform>();
            foreach (var t in Inspector.AllSceneTransforms())
            {
                if (t == null || t == camT || t.IsChildOf(camT)) continue;
                if (t.root.name.StartsWith("InsideDev", StringComparison.Ordinal)) continue;
                set.Add(t);
            }
            return set;
        }

        static void Scan(List<Transform> list, Vector3[] pos, Quaternion[] rot, Vector3[] scl, HashSet<int> set, float eps, float rotEps)
        {
            float e2 = eps * eps;
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (t == null) continue;
                Vector3 p = t.position, s = t.localScale;
                Quaternion r = t.rotation;
                if ((p - pos[i]).sqrMagnitude > e2 || RotDeg(r, rot[i]) > rotEps || (s - scl[i]).sqrMagnitude > e2) set.Add(i);
                pos[i] = p; rot[i] = r; scl[i] = s;
            }
        }

        static void CameraProbeResult(ProbeRun p, List<Transform> list, HashSet<int> control, HashSet<int> moved, string how, out string summary, out string full)
        {
            var expected = BuiltInCameraDriven();
            var driven = new List<int>();
            foreach (var i in moved) if (!control.Contains(i) && list[i] != null) driven.Add(i);
            var paths = new Dictionary<int, string>();
            foreach (var i in driven) paths[i] = Inspector.PathOf(list[i]);
            driven.Sort((x, y) => string.CompareOrdinal(paths[x], paths[y]));
            var rows = new List<string>();
            var lines = new StringBuilder();
            var listed = new HashSet<Transform>();
            int expectedCount = 0, rigidCount = 0;
            foreach (var i in driven)
            {
                var t = list[i];
                listed.Add(t);
                var comps = new List<string>();
                foreach (var c in t.GetComponents<Component>()) if (c != null && !(c is Transform)) comps.Add(c.GetType().Name);
                bool exp = expected.Contains(t);
                bool rigid = !exp && RigidBody(t);
                if (exp) expectedCount++;
                if (rigid) rigidCount++;
                rows.Add(CameraRow(paths[i], comps, exp, rigid));
                lines.Append("\n  ").Append(paths[i]).Append("  [").Append(string.Join(",", comps.ToArray())).Append("]").Append(exp ? " expected" : "").Append(rigid ? " RIGID" : "");
            }
            var missing = MissingExpected(listed);
            var ctrl = new List<string>();
            foreach (var i in control) if (list[i] != null && ctrl.Count < 20) ctrl.Add(Inspector.PathOf(list[i]));
            var scanned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in list) if (t != null) scanned.Add(Inspector.PathOf(t));
            string file = Path.Combine(p.dir, ReplayConst.CameraDrivenFile);
            string written;
            int kept = 0;
            try
            {
                Directory.CreateDirectory(p.dir);
                string[] old = File.Exists(file) ? File.ReadAllLines(file) : new string[0];
                File.WriteAllText(file, MergeCameraDriven(old, scanned, rows, out kept));
                written = file + (kept > 0 ? " (" + kept + " row(s) of other transforms kept)" : "");
            }
            catch (Exception e) { written = "not written (" + e.Message + ")"; }
            bool ok = missing.Count == 0 && rigidCount == 0;
            summary = "camera probe " + (ok ? "ok" : "check") + ": " + list.Count + " transforms scanned (" + how + "), " + control.Count + " changed without camera motion, " + driven.Count +
                      " camera-driven (" + expectedCount + " on the built-in list, " + rigidCount + " rigid), " + missing.Count + " built-in writer(s) not listed; " + written;
            var sb = new StringBuilder(summary).Append(lines.ToString());
            foreach (var m in missing) sb.Append("\n  missing: ").Append(m);
            foreach (var c in ctrl) sb.Append("\n  changed without camera motion: ").Append(c);
            full = sb.ToString();
        }

        static HashSet<Transform> BuiltInCameraDriven()
        {
            var e = new HashSet<Transform>();
            foreach (var lf in UObj.FindObjectsOfType<LensFlareSimple>()) if (lf != null) AddTree(e, lf.transform);
            foreach (var ap in UObj.FindObjectsOfType<AudioCameraProbe>()) if (ap != null) AddTree(e, ap.transform);
            foreach (var we in UObj.FindObjectsOfType<WaterEdge>())
            {
                if (we == null) continue;
                if (we.edge != null) AddTree(e, we.edge);
                if (we.inverseTransform != null) AddTree(e, we.inverseTransform);
            }
            foreach (var wd in UObj.FindObjectsOfType<WaterDust>())
            {
                if (wd == null || wd.dustObject == null) continue;
                var t = wd.dustObject.transform;
                e.Add(t);
                if (t.parent != null) e.Add(t.parent);
            }
            return e;
        }

        static void AddTree(HashSet<Transform> set, Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t != null) set.Add(t);
        }

        static List<string> MissingExpected(HashSet<Transform> listed)
        {
            var missing = new List<string>();
            foreach (var lf in UObj.FindObjectsOfType<LensFlareSimple>())
            {
                if (lf == null || !lf.enabled || !lf.gameObject.activeInHierarchy) continue;
                bool any = false;
                foreach (var t in lf.GetComponentsInChildren<Transform>(true)) if (listed.Contains(t)) { any = true; break; }
                if (!any && Rendered(lf.transform)) missing.Add("LensFlareSimple " + Inspector.PathOf(lf.transform));
            }
            foreach (var wd in UObj.FindObjectsOfType<WaterDust>())
            {
                if (wd == null || !wd.enabled || wd.dustObject == null) continue;
                var t = wd.dustObject.transform;
                if (!t.gameObject.activeInHierarchy || listed.Contains(t)) continue;
                if (Rendered(t)) missing.Add("WaterDust dust " + Inspector.PathOf(t));
            }
            return missing;
        }

        static bool RigidBody(Transform t)
        {
            if (t.GetComponent<Rigidbody>() != null) return true;
            foreach (var c in t.GetComponents<Collider>()) if (c != null && !c.isTrigger) return true;
            return false;
        }

        static bool Rendered(Transform t)
        {
            foreach (var r in t.GetComponentsInChildren<Renderer>(true)) if (r != null && r.isVisible) return true;
            return false;
        }

        internal static List<string> Writers(Transform t)
        {
            var l = new List<string>();
            for (var x = t; x != null; x = x.parent)
            {
                foreach (var c in x.GetComponents<Component>())
                {
                    if (c == null) continue;
                    string why = WriterKind(c);
                    if (why == null) continue;
                    string s = c.GetType().Name + " (" + why + ") on " + x.name;
                    if (!l.Contains(s)) l.Add(s);
                }
            }
            return l;
        }

        static string WriterKind(Component c)
        {
            if (!(c is MonoBehaviour)) return null;
            var ty = c.GetType();
            string why;
            if (writerKinds.TryGetValue(ty, out why)) return why;
            why = HasMethod(ty, "LateUpdate") ? "LateUpdate" : HasMethod(ty, "UmbrellaLateUpdate") ? "UmbrellaLateUpdate" : null;
            writerKinds[ty] = why;
            return why;
        }

        static List<string> CameraPreCullers(Camera cam)
        {
            var l = new List<string>();
            if (cam == null) return l;
            foreach (var c in cam.GetComponents<MonoBehaviour>())
            {
                if (c == null || c is SceneCommandsTap || !c.enabled) continue;
                string n = c.GetType().Name;
                if (HasMethod(c.GetType(), "OnPreCull") && !l.Contains(n)) l.Add(n);
            }
            return l;
        }

        static bool HasMethod(Type t, string name)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var ty = t; ty != null && ty != typeof(MonoBehaviour) && ty != typeof(object); ty = ty.BaseType)
                if (ty.GetMethod(name, all) != null) return true;
            return false;
        }

        static void Finish(ProbeRun p, string summary, string full)
        {
            if (probe == p) probe = null;
            lastProbe = summary;
            lastProbeFull = full.Length > 0 ? full : summary;
            DevLog.Write("[srec] " + summary);
        }

        static void StopProbe(string why)
        {
            var p = probe;
            if (p == null) return;
            try { p.Cleanup(); } catch (Exception e) { Fail("probe cleanup", e); }
            Finish(p, p.kind + " probe stopped at frame " + p.frame + "/" + p.frames + ": " + why, "");
        }

        // ------------------------------------------------------------------ host and frame phases

        static void EnsureHost()
        {
            if (host != null) return;
            var go = new GameObject("InsideDev SceneCommands");
            UObj.DontDestroyOnLoad(go);
            host = go.AddComponent<SceneCommandsHost>();
            host.StartCoroutine(PhaseLoop());
            host.StartCoroutine(EofLoop());
        }

        static IEnumerator PhaseLoop()
        {
            while (true)
            {
                yield return null;
                PhaseTick();
            }
        }

        static IEnumerator EofLoop()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                try { ViewerTick(); } catch (Exception e) { Fail("viewer tick", e); }
            }
        }

        static void PhaseTick()
        {
            try { Orphans(); } catch (Exception e) { Fail("orphans", e); }
            try { if (viewer != null || (probe != null && probe.freeze)) HoldFreeze(); } catch (Exception e) { Fail("freeze", e); }
            var m = manual;
            if (m != null && m.waiting) { try { WaitTick(m); } catch (Exception e) { Fail("record wait", e); manual = null; ReleaseKept(m); } }
            else if (m != null) { try { RecordTick(m); } catch (Exception e) { Fail("record tick", e); } }
            var p = probe;
            if (p != null && Time.frameCount - p.tick > 300) StopProbe("stalled (no progress for 300 frames)");
        }

        static void Orphans()
        {
            var r = viewer;
            if (r != null && !r.active)
            {
                if (probe != null) StopProbe("the replay ended outside srec");
                viewer = null;
                viewerDir = "";
                playing = false;
                lastProbe = "viewer closed: the replay was ended outside srec (scene restored by its End)";
                DevLog.Write("[srec] " + lastProbe);
            }
            var m = manual;
            if (m != null && m.rec != null && SceneRecorder.active != m.rec)
            {
                string s = RecordStop(false, "recorder ended outside srec");
                DevLog.Write("[srec] " + s);
            }
        }

        internal static void HostGone(SceneCommandsHost h)
        {
            if (host != h) return;
            host = null;
            if (probe != null) StopProbe("host destroyed");
        }

        internal static void Quit()
        {
            try { if (probe != null) StopProbe("application quit"); } catch { }
            try { if (manual != null) RecordStop(false, "application quit"); } catch { }
            try { if (viewer != null) End(); } catch { }
        }

        static void Fail(string where, Exception e)
        {
            errorCount++;
            if (errorCount <= 20) DevLog.Error("srec " + where, e);
        }

        // ------------------------------------------------------------------ arguments (pure; tested offline)

        internal static Json.Obj KeyValues(List<string> a, int from, Type node, string[] ownKeys, Dictionary<string, string> own, SpecReport report)
        {
            var o = new Json.Obj();
            for (int i = from; i < a.Count; i++)
            {
                string s = a[i];
                int eq = s.IndexOf('=');
                if (eq <= 0) { report.Error("srec", "key=value expected, got '" + s + "'"); continue; }
                string k = s.Substring(0, eq), v = s.Substring(eq + 1);
                if (ownKeys != null && Array.IndexOf(ownKeys, k) >= 0) { own[k] = v; continue; }
                var f = node != null ? SpecBind.Field(node, k) : null;
                o[k] = f != null ? ArgValue(f.FieldType, v) : v;
            }
            return o;
        }

        internal static object ArgValue(Type t, string v)
        {
            double d;
            bool b;
            if (t == typeof(string)) return v;
            if (t == typeof(bool)) return TryBool(v, out b) ? (object)b : v;
            if (t == typeof(int) || t == typeof(float) || t == typeof(double)) return double.TryParse(v, NumberStyles.Float, IC, out d) ? (object)d : v;
            if (t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(float[]))
            {
                var parts = v.Split(new[] { ',', ';', 'x', 'X' }, StringSplitOptions.RemoveEmptyEntries);
                var l = new List<object>();
                foreach (var p in parts) { if (!double.TryParse(p, NumberStyles.Float, IC, out d)) return v; l.Add(d); }
                return l;
            }
            if (t == typeof(List<string>))
            {
                var l = new List<object>();
                foreach (var p in v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) l.Add(p);
                return l;
            }
            if (v.Length > 0 && (v[0] == '{' || v[0] == '['))
            {
                try { return Json.Parse(v); } catch { return v; }
            }
            return v;
        }

        internal static bool TryBool(string v, out bool b)
        {
            string s = (v ?? "").ToLowerInvariant();
            b = s == "on" || s == "true" || s == "1" || s == "yes";
            return b || s == "off" || s == "false" || s == "0" || s == "no";
        }

        internal static RecordSpec RecordArgs(List<string> a, int from, SpecReport report, out bool audioOn, out bool overwrite)
        {
            return RecordArgsOn(new RecordSpec(), a, from, report, out audioOn, out overwrite);
        }

        internal static string OwnArg(List<string> a, int from, string key)
        {
            string p = key + "=";
            for (int i = from; i < a.Count; i++) if (a[i] != null && a[i].StartsWith(p, StringComparison.Ordinal)) return a[i].Substring(p.Length);
            return null;
        }

        internal static RecordSpec RecordArgsOn(RecordSpec spec, List<string> a, int from, SpecReport report, out bool audioOn, out bool overwrite)
        {
            var own = new Dictionary<string, string>();
            var o = KeyValues(a, from, typeof(RecordSpec), RecordOwnKeys, own, report);
            audioOn = false;
            overwrite = false;
            string v;
            if (own.TryGetValue("clock", out v))
            {
                string c = v.ToLowerInvariant();
                int colon = c.IndexOf(':');
                if (colon >= 0)
                {
                    int rate;
                    if (int.TryParse(c.Substring(colon + 1), NumberStyles.Integer, IC, out rate)) o["rate"] = (double)rate;
                    else report.Error("srec.clock", "fixed:<rate> expected, got '" + v + "'");
                    c = c.Substring(0, colon);
                }
                o["clock"] = c;
            }
            if (own.TryGetValue("audio", out v) && !TryBool(v, out audioOn)) report.Error("srec.audio", "on or off expected, got '" + v + "'");
            if (own.TryGetValue("overwrite", out v) && !TryBool(v, out overwrite)) report.Error("srec.overwrite", "on or off expected, got '" + v + "'");
            if (spec == null) spec = new RecordSpec();
            SpecBind.Fill(spec, o, "srec", report);
            LintRecord(spec, audioOn, report);
            return spec;
        }

        static void LintRecord(RecordSpec s, bool audioOn, SpecReport r)
        {
            if (!In(s.clock, ReplayConst.Clocks)) r.Error("srec.clock", "real or fixed:<rate> expected, got '" + s.clock + "'");
            else if (s.clock == "fixed" && Array.IndexOf(ReplayConst.FixedRates, s.rate) < 0) r.Error("srec.rate", "the fixed clock rate must be 60, 120 or 240, got " + s.rate);
            Allowed(r, "particles", s.particles, ReplayConst.RecordParticles);
            Allowed(r, "forceCullers", s.forceCullers, ReplayConst.ForceCullers);
            if (s.roots == null || s.roots.Count == 0) r.Error("srec.roots", "at least one root expected (auto, boy, huddle, subscene:<name> or an object path)");
            if (s.forceCullers == "list" && s.cullers.Count == 0) r.Warn("srec.cullers", "forceCullers list without cullers: nothing is forced");
            if (s.forceCullers == "planned") r.Warn("srec.forceCullers", "planned needs spec cameras: a manual record plans none, so nothing is forced");
            if (s.particles == "list" && s.particleSystems.Count == 0) r.Warn("srec.particleSystems", "particles list without particleSystems: no particles recorded");
            if (audioOn && s.clock == "fixed" && s.rate != 60) r.Error("srec.audio", "audio capture needs clock real or fixed:60 (2.3.4)");
            if (s.Has("res")) r.Warn("srec.res", "ignored: a manual record keeps the current resolution");
            if (s.Has("captureGame") && s.captureGame) r.Warn("srec.captureGame", "ignored: game frames are grabbed by mcam record only");
            if (s.keepSpawned) r.Warn("srec.keepSpawned", "P2, not built");
            if (s.rescanEvery < 1) r.Error("srec.rescanEvery", "must be at least 1");
            if (s.posEps <= 0f || s.rotEps <= 0f || s.cut <= 0f) r.Error("srec", "posEps, rotEps and cut must be positive");
        }

        internal static PlaySpec PlayArgs(List<string> a, int from, SpecReport report, out string tArg, out bool gameCam)
        {
            var own = new Dictionary<string, string>();
            var o = KeyValues(a, from, typeof(PlaySpec), LoadOwnKeys, own, report);
            tArg = null;
            gameCam = true;
            string v;
            if (own.TryGetValue("t", out v)) tArg = v;
            if (own.TryGetValue("cam", out v))
            {
                v = v.ToLowerInvariant();
                if (v == "keep") gameCam = false;
                else if (v != "game") report.Error("srec.cam", "game or keep expected, got '" + v + "'");
            }
            var spec = new PlaySpec();
            SpecBind.Fill(spec, o, "srec", report);
            var root = new Json.Obj();
            var rp = new Json.Obj();
            rp["play"] = o;
            root["replay"] = rp;
            ReplayConst.CheckRemoved(root, report);
            Allowed(report, "look", spec.look, ReplayConst.Looks);
            Allowed(report, "order", spec.order, ReplayConst.Orders);
            Allowed(report, "interp", spec.interp, ReplayConst.Interps);
            Allowed(report, "particles", spec.particles, ReplayConst.PlayParticles);
            Allowed(report, "cullers", spec.cullers, ReplayConst.Cullers);
            Allowed(report, "shadows", spec.shadows, ReplayConst.Shadows);
            Allowed(report, "activate", spec.activate, ReplayConst.Activates);
            Allowed(report, "realTime", spec.realTime, ReplayConst.RealTimes);
            Allowed(report, "restore", spec.restore, ReplayConst.Restores);
            if (spec.cullers == "live") report.Warn("srec.cullers", "live is a diagnostic: culler groups toggle with SetActive per camera");
            if (spec.maxUnresolved < 0f || spec.maxUnresolved > 1f) report.Error("srec.maxUnresolved", "a fraction in [0, 1] expected");
            if (spec.activateList != null)
                foreach (var k in spec.activateList.order)
                    if (!In(spec.activateList.Str(k, ""), ReplayConst.ActivateListModes)) report.Error("srec.activateList." + k, "quiet or normal expected");
            return spec;
        }

        static void Allowed(SpecReport r, string key, string value, string[] allowed)
        {
            if (!In(value, allowed)) r.Error("srec." + key, "unknown value '" + value + "' (allowed: " + string.Join(", ", allowed) + ")");
        }

        static bool In(string v, string[] allowed) { return v != null && Array.IndexOf(allowed, v) >= 0; }

        static float OwnFloat(Dictionary<string, string> own, string key, float def, SpecReport report)
        {
            string v;
            if (!own.TryGetValue(key, out v)) return def;
            float f;
            if (float.TryParse(v, NumberStyles.Float, IC, out f) && !float.IsNaN(f) && !float.IsInfinity(f)) return f;
            report.Error("srec." + key, "number expected, got '" + v + "'");
            return def;
        }

        static string OwnDir(Dictionary<string, string> own, string def, SpecReport report)
        {
            string v;
            if (!own.TryGetValue("out", out v)) return def;
            try { return Path.GetFullPath(v); }
            catch (Exception e) { report.Error("srec.out", e.Message); return def; }
        }

        static string ModDir() { return Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"); }

        // ------------------------------------------------------------------ math and formatting (pure; tested offline)

        internal static string RecordingDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            if (File.Exists(Path.Combine(dir, ReplayConst.RecordFile))) return dir;
            string sub = Path.Combine(dir, ReplayConst.RecordFolder);
            return File.Exists(Path.Combine(sub, ReplayConst.RecordFile)) ? sub : null;
        }

        internal static double Advance(double t, double delta, double duration, bool loop, out bool stopped)
        {
            stopped = false;
            if (!(duration > 0.0)) { stopped = true; return 0.0; }
            double n = t + delta;
            if (n >= 0.0 && n <= duration) return n;
            if (loop) return n - duration * Math.Floor(n / duration);
            stopped = true;
            return n < 0.0 ? 0.0 : duration;
        }

        internal static int StepSample(int at, double atTime, double t, bool back, int count)
        {
            int i = back ? (atTime >= t - 1e-9 ? at - 1 : at) : at + 1;
            return Math.Max(0, Math.Min(count - 1, i));
        }

        internal static bool ParseTime(string s, double now, double duration, Func<string, double> eventTime, out double t, out string error)
        {
            t = now;
            error = null;
            if (string.IsNullOrEmpty(s)) { error = "time expected"; return false; }
            string v = s.ToLowerInvariant();
            if (v == "start") { t = 0.0; return true; }
            if (v == "end") { t = duration; return true; }
            if (s[0] == '@')
            {
                int k = s.IndexOfAny(new[] { '+', '-' }, 1);
                string name = k > 0 ? s.Substring(1, k - 1) : s.Substring(1);
                double off = 0.0;
                if (k > 0 && !double.TryParse(s.Substring(k), NumberStyles.Float, IC, out off)) { error = "bad offset in '" + s + "'"; return false; }
                double e = eventTime != null ? eventTime(name) : double.NaN;
                if (double.IsNaN(e)) { error = "event '" + name + "' did not fire in the recording"; return false; }
                t = e + off;
                return true;
            }
            if (!double.TryParse(s, NumberStyles.Float, IC, out t) || double.IsNaN(t) || double.IsInfinity(t)) { t = now; error = "seconds, start, end or @event[+-s] expected, got '" + s + "'"; return false; }
            return true;
        }

        internal static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }

        internal static double RotDeg(Quaternion a, Quaternion b)
        {
            double dot = (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w;
            double s = dot < 0.0 ? -1.0 : 1.0;
            double dx = a.x - s * b.x, dy = a.y - s * b.y, dz = a.z - s * b.z, dw = a.w - s * b.w;
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
            return 4.0 * Math.Asin(Math.Min(1.0, 0.5 * d)) * (180.0 / Math.PI);
        }

        internal static void PacingFor(string clock, int rate, int refresh, out int capture, out int vsync, out int target)
        {
            bool fixedClock = clock == "fixed";
            capture = fixedClock ? rate : 0;
            vsync = !fixedClock && refresh == 60 ? 1 : 0;
            target = fixedClock ? rate : vsync == 1 ? -1 : 60;
        }

        internal static Json.Obj PacingStats(List<float> dts)
        {
            var o = new Json.Obj();
            int n = dts.Count;
            o["frames"] = n;
            if (n == 0) return o;
            var s = new List<float>(dts);
            s.Sort();
            double sum = 0.0;
            int band = 0, hitches = 0;
            foreach (var d in dts)
            {
                sum += d;
                if (d >= 0.015f && d <= 0.0185f) band++;
                if (d > 0.1f) hitches++;
            }
            o["frameMeanMs"] = Math.Round(sum / n * 1000.0, 3);
            o["frameP99Ms"] = Math.Round(s[Math.Max(0, Math.Min(n - 1, (int)Math.Ceiling(0.99 * n) - 1))] * 1000.0, 3);
            o["within15to18_5ms"] = Math.Round(band / (double)n, 4);
            o["hitches"] = hitches;
            return o;
        }

        internal static string CameraRow(string path, List<string> comps, bool expected, bool rigid)
        {
            return path + "\t" + string.Join(",", comps.ToArray()) + "\t" + (expected ? "1" : "0") + "\t" + (rigid ? "1" : "0");
        }

        internal static string MergeCameraDriven(string[] oldLines, HashSet<string> scanned, List<string> rows, out int kept)
        {
            var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
            kept = 0;
            if (oldLines != null)
                for (int i = 1; i < oldLines.Length; i++)
                {
                    string l = oldLines[i];
                    if (string.IsNullOrEmpty(l)) continue;
                    int tab = l.IndexOf('\t');
                    string path = tab >= 0 ? l.Substring(0, tab) : l;
                    if (path.Length == 0 || scanned.Contains(path) || byPath.ContainsKey(path)) continue;
                    byPath[path] = l;
                    kept++;
                }
            foreach (var r in rows)
            {
                int tab = r.IndexOf('\t');
                string path = tab >= 0 ? r.Substring(0, tab) : r;
                if (byPath.ContainsKey(path) && !scanned.Contains(path)) kept--;
                byPath[path] = r;
            }
            var keys = new List<string>(byPath.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder(CameraProbeHeader).Append('\n');
            foreach (var k in keys) sb.Append(byPath[k]).Append('\n');
            return sb.ToString();
        }

        static string F(double v) { return v.ToString("0.###", IC); }

        static string F3(double v) { return v.ToString("0.000", IC); }
    }

    // Host of the srec frame phases (yield-null step after LateUpdate: freeze hold, record pacing and virtual channel;
    // end of frame: viewer T) and of the probe coroutines. Named InsideDev* so recordings and probes skip it.
    public sealed class SceneCommandsHost : MonoBehaviour
    {
        void OnApplicationQuit() { SceneCommands.Quit(); }

        void OnDestroy() { SceneCommands.HostGone(this); }
    }

    // Added last to the world camera by `srec probe`: its OnPreCull runs after every other component OnPreCull and before the
    // Camera.onPreCull delegate (L1), i.e. after every LateUpdate writer and before SceneReplayer's preCull re-assert.
    public sealed class SceneCommandsTap : MonoBehaviour
    {
        internal Action<Camera> onPreCull;
        Camera cam;

        void OnPreCull()
        {
            var h = onPreCull;
            if (h == null) return;
            if (cam == null) cam = GetComponent<Camera>();
            try { h(cam); }
            catch (Exception e) { DevLog.Error("srec probe tap", e); onPreCull = null; }
        }
    }
}
