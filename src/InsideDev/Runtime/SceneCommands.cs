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
    // Replies: a command that did its job keeps its first words ("srec loaded", "T ...", "playing", "paused:", "srec end:",
    // "recording ->", "recording stopped ->"); one that could not starts with refused: / busy: / usage: / not found: /
    // failed: / cannot. `srec end`, `srec record stop` and `srec probe stop` with nothing to stop are not failures.
    // status, record status, list, events and load ... dry take a last word "json" (one JsonLine object).
    // Take folders: a relative <dir> is a folder under <game>\_mod\srec (RplXTakes); `srec record start` without a folder
    // makes take_<yyyyMMdd_HHmmss> there; `srec load last` takes the newest. Every reply names the full path.
    // Level load: the viewer's replayer ends itself (SceneReplayer, RplXLoad) and the viewer is dropped with a note that
    // status and the next viewer command repeat; a probe stops, a pending record handover is released, the freeze hold lets
    // go. seek and step apply their time at once (SceneReplayer.ApplyNow), so the reply and a screenshot taken in the same
    // frame show the same T. The host object lives only while a viewer, recording, probe or handover exists.
    public static class SceneCommands
    {
        internal const string WriteProbeFile = "write_probe.tsv";
        internal const string WriteProbeHeader = "path\tframes\tmaxPos\tmaxRotDeg\twriters";
        internal const string CameraProbeHeader = "path\tcomponents\texpected\trigid";
        internal const string NoViewer = "no recording loaded (srec load <dir>)";
        internal const string LoadUsage = "usage: srec load <dir|last> [dry] [replace] [play [speed] [loop|once]] [t=<s>|@event] [cam=game|keep] [<play key>=value ...] [json with dry]  (a relative dir is a folder under <game>\\_mod\\srec)";
        internal const string SeekUsage = "usage: srec seek <seconds|start|end|@event[+-s]|rel <seconds>|sample:<n>|<percent>%>";
        internal const string RecordUsage = "usage: srec record start [<dir>] [roots=a,b] [keep=subscene,...] [spec=<file>] [clock=real|fixed:<rate>] [audio=on] [overwrite=on] [<record key>=value ...] | srec record stop [keep|load] | srec record status [json]  (no dir: a new take_<date_time> under <game>\\_mod\\srec; a relative dir is a folder there)";
        internal const string EventUsage = "usage: srec event <name> [detail ...] (name: letters, digits, '_' and '.'; referenced as @name)";
        internal const string Usage =
            "srec status [json] | srec objects [filter] [max=n] | srec list [max=n] [json] | srec events [json]\n" +
            "srec load <dir|last> [dry] [replace] [play [speed] [loop]] [t=<s>|@event] [cam=game|keep] [<play key>=value ...] | srec reload | srec end\n" +
            "srec play [speed] [loop] | srec pause | srec seek <s|start|end|@event[+-s]|rel <s>|sample:<n>|<p>%> | srec step [dt|next|back]\n" +
            "srec probe [n] [eps=m] [rot=deg] [out=dir] | srec probe camera [n] [dx=m] [yaw=deg] [eps=m] [rot=deg] [out=dir] [roots=a,b] | srec probe stop | srec probe result\n" +
            "srec record start [<dir>] [roots=a,b] [keep=subscene,...] [spec=<file>] [clock=real|fixed:<rate>] [audio=on] [overwrite=on] [<record key>=value ...] | srec record stop [keep|load] | srec record status [json]\n" +
            "srec event <name> [detail ...]\n" +
            "a relative <dir> is a folder under <game>\\_mod\\srec; record start without <dir> makes take_<date_time> there";

        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly string[] RecordOwnKeys = { "clock", "audio", "overwrite", "spec" };
        const float KeepTimeout = 30f;
        const int HostIdleFrames = 300;
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
        static int pendingLoads;
        static ProbeRun probe;
        static string lastProbe = "", lastProbeFull = "", lastRecord = "", closedNote = "", handoverNote = "";
        static List<string> lastLoad;
        static SceneCommandsHost host;
        static int errorCount, idleFrames;

        sealed class ProbeRun
        {
            public string kind = "", phase = "", dir = "";
            public int frame, frames, tick, loads;
            public bool freeze, gone;
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
            public bool audioOn, audioLost, audioSilent, hooked, paced;
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
                RplXSetup.Register();
                RplXSetup.KeepAlive();
                switch (sub)
                {
                    case "status": return TakeJson(a, 2) ? JsonLine.Write(StatusJson()) : Status();
                    case "help": return Usage;
                    case "objects": return Objects(a);
                    case "list": return ListTakes(a);
                    case "events": return Events(a);
                    case "load": return Load(a);
                    case "reload": return Reload();
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
            catch (Exception e) { DevLog.Error("srec " + sub, e); return "failed: srec " + sub + ": " + e.GetType().Name + ": " + e.Message; }
            return "usage:\n" + Usage;
        }

        // Removes a last word "json" (at index from or later) and says whether it was there.
        static bool TakeJson(List<string> a, int from)
        {
            int last = a.Count - 1;
            if (last < from || !string.Equals(a[last], "json", StringComparison.OrdinalIgnoreCase)) return false;
            a.RemoveAt(last);
            return true;
        }

        // null while a viewer is loaded, else the refusal for a command that needs one: why the last viewer went away
        // (level load, ended outside srec) when that is known.
        static string NeedViewer()
        {
            if (viewer != null) return null;
            return "refused: " + (closedNote.Length > 0 ? closedNote + "; srec reload loads it again" : NoViewer);
        }

        static string FirstError(SpecReport r)
        {
            if (r == null) return "";
            return r.errors.Count > 0 ? r.errors[0] : r.warnings.Count > 0 ? r.warnings[0] : "";
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
            bool busy = sb.Length > 0;
            if (!busy) sb.Append("srec idle (srec help)\n");
            if (viewer == null && closedNote.Length > 0) sb.Append("last ").Append(closedNote).Append('\n');
            if (pending == null && handoverNote.Length > 0) sb.Append("last handover: ").Append(handoverNote).Append('\n');
            if (lastProbe.Length > 0) sb.Append("last probe: ").Append(lastProbe).Append('\n');
            if (lastRecord.Length > 0 && manual == null) sb.Append("last record: ").Append(lastRecord).Append('\n');
            if (viewer != null || manual != null || probe != null || SceneReplayer.current != null || SceneRecorder.active != null) sb.Append(HooksLine()).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        // `srec status json`: state is the first that applies of waiting, recording, viewer, probe, replay (mcam), recording
        // (mcam), idle. The viewer's fields are at the top level, the replayer's own counters under "replay".
        static Json.Obj StatusJson()
        {
            var o = new Json.Obj();
            var m = manual;
            var r = viewer;
            var p = probe;
            o["state"] = m != null ? (m.waiting ? "waiting" : "recording") : r != null ? "viewer" : p != null ? "probe" : SceneReplayer.current != null ? "replay (mcam)" : SceneRecorder.active != null ? "recording (mcam)" : "idle";
            o["frame"] = Time.frameCount;
            o["timeScale"] = (double)Time.timeScale;
            o["loading"] = RplXLoad.Loading();
            o["frozenBy"] = r != null ? "viewer" : p != null && p.freeze ? "probe" : SceneReplayer.current != null ? "replay (mcam)" : MultiCamFreeze.holding ? "hold: " + MultiCamFreeze.reason : "";
            if (r != null)
            {
                o["dir"] = viewerDir;
                o["T"] = r.T;
                o["appliedT"] = r.AppliedT;
                o["appliedFrame"] = r.AppliedFrame;
                o["duration"] = Duration(r);
                o["sample"] = SafeSampleAt(r, r.AppliedT);
                o["sampleCount"] = SampleCount(r);
                o["playing"] = playing;
                o["speed"] = speed;
                o["loop"] = looping;
                o["timeScaleReforced"] = freezeLeaks;
                try { o["replay"] = r.StatusJson(); } catch (Exception e) { o["replayError"] = e.Message; }
            }
            else if (SceneReplayer.current != null)
            {
                try { o["replay"] = SceneReplayer.current.StatusJson(); } catch (Exception e) { o["replayError"] = e.Message; }
            }
            if (m != null) o["recording"] = RecordJson(m);
            else if (SceneRecorder.active != null) o["recorderText"] = RecorderStatus(SceneRecorder.active);
            if (pending != null)
            {
                var h = new Json.Obj();
                h["from"] = pendingFrom;
                h["forcedCullers"] = pending.forced != null ? pending.forced.Count : 0;
                h["keptSubscenes"] = pending.keptLoaded != null ? pending.keptLoaded.Count : 0;
                o["handover"] = h;
            }
            if (p != null)
            {
                var pj = new Json.Obj();
                pj["kind"] = p.kind; pj["phase"] = p.phase; pj["frame"] = p.frame; pj["frames"] = p.frames;
                o["probe"] = pj;
            }
            o["lastViewer"] = closedNote;
            o["lastHandover"] = handoverNote;
            o["lastProbe"] = lastProbe;
            o["lastRecord"] = lastRecord;
            o["takes"] = RplXTakes.Root();
            o["tickErrors"] = errorCount;
            return o;
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

        // For the MCP server's automatic restores (RplXSetup.KeepAlive): why they must wait now, or null. Main thread.
        internal static string RecordBusy()
        {
            var m = manual;
            return m == null ? null : m.waiting ? "srec record start is waiting for its subscenes" : "srec recording " + m.dir;
        }

        internal static string ReplayBusy()
        {
            return viewer != null && playing ? "srec replay playing " + viewerDir : null;
        }

        static string RecorderStatus(SceneRecorder r)
        {
            try { return r.Status(); } catch (Exception e) { return "status failed: " + e.Message; }
        }

        static string Objects(List<string> a)
        {
            var r = viewer ?? SceneReplayer.current;
            if (r == null) return NeedViewer();
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

        // Words of `srec load` after the folder: key=value pairs go to PlayArgs, the bare words are options.
        sealed class LoadWords
        {
            public bool dry, replace, play, loop, json;
            public double speed = 1.0;
            public readonly List<string> pairs = new List<string>();
        }

        internal static bool PlayWord(string v, ref double sp, ref bool lp)
        {
            double x;
            if (v == "loop") lp = true;
            else if (v == "once") lp = false;
            else if (double.TryParse(v.TrimEnd('x'), NumberStyles.Float, IC, out x) && x != 0.0 && Math.Abs(x) <= 100.0) sp = x;
            else return false;
            return true;
        }

        static string ParseLoadWords(List<string> a, int from, LoadWords w)
        {
            for (int i = from; i < a.Count; i++)
            {
                string s = a[i] ?? "";
                if (s.IndexOf('=') > 0) { w.pairs.Add(s); continue; }
                string v = s.ToLowerInvariant();
                if (v == "dry") w.dry = true;
                else if (v == "replace") w.replace = true;
                else if (v == "play") w.play = true;
                else if (v == "json") w.json = true;
                else if (w.play && PlayWord(v, ref w.speed, ref w.loop)) { }
                else return "unknown word '" + s + "'";
            }
            if (w.json && !w.dry) return "json belongs to dry";
            return null;
        }

        // A relative folder is a take under <game>\_mod\srec; one that is not there but exists relative to the game's
        // working folder (where relative paths went before) is still found.
        static string LoadDir(string arg, out string note)
        {
            note = "";
            if (Path.IsPathRooted(arg)) return Path.GetFullPath(arg);
            string under = RplXTakes.Under(RplXTakes.Root(), arg);
            if (RecordingDir(under) != null) return under;
            string legacy = Path.GetFullPath(arg);
            if (RecordingDir(legacy) == null) return under;
            note = "found relative to the game's working folder, not under " + RplXTakes.Root();
            return legacy;
        }

        static string Load(List<string> a)
        {
            if (a.Count < 3) return LoadUsage;
            var w = new LoadWords();
            string bad = ParseLoadWords(a, 3, w);
            if (bad != null) return LoadUsage + ": " + bad;
            string busy = Busy();
            if (busy != null) return busy;
            if (manual != null) return "busy: recording in progress: srec record stop first";
            if (!w.dry)
            {
                if (RplXLoad.Loading()) return "busy: the game is loading a level: srec load again when it has finished";
                if (viewer != null && !w.replace) return "busy: a recording is loaded (" + viewerDir + "): srec end first, or add the word replace";
                if (probe != null) return "busy: a probe is running: srec probe stop first";
            }
            string full, dirNote = "";
            try
            {
                if (a[2].ToLowerInvariant() == "last")
                {
                    full = RplXTakes.Last(RplXTakes.Root());
                    if (full == null) return "not found: no take with a " + ReplayConst.RecordFile + " under " + RplXTakes.Root();
                }
                else full = LoadDir(a[2], out dirNote);
            }
            catch (Exception e) { return "refused: bad dir '" + a[2] + "': " + e.Message; }
            string dir = RecordingDir(full);
            if (dir == null) return "not found: no " + ReplayConst.RecordFile + " in " + full + " or in its " + ReplayConst.RecordFolder + " folder";
            var report = new SpecReport();
            string tArg;
            bool gameCam;
            var spec = PlayArgs(w.pairs, 0, report, out tArg, out gameCam);
            if (!report.ok) return "refused: srec load: " + FirstError(report) + "\n" + report.Summary(20);
            if (dirNote.Length > 0) report.Note("srec.dir", dirNote);
            string replaced = "";
            if (viewer != null && !w.dry) replaced = End().Split('\n')[0];
            SceneReplayer r = null;
            try { r = SceneReplayer.Load(dir, spec, report, w.dry); }
            catch (Exception e) { report.Error("srec load", e.GetType().Name + ": " + e.Message); r = null; }
            if (r == null || !report.ok) return "refused: srec load " + dir + ": " + FirstError(report) + (replaced.Length > 0 ? " (the recording loaded before was released)" : "") + "\n" + report.Summary(30);
            if (w.dry)
            {
                string text;
                try { text = w.json ? JsonLine.Write(r.DryJson()) : "srec load dry " + dir + ": " + r.DryText() + (report.warnings.Count > 0 ? "\n" + report.Summary(10) : ""); }
                finally { try { r.End(null); } catch (Exception e) { Fail("dry end", e); } }
                return text;
            }
            var hand = pending;
            r.KeepFreeCamera = true;
            try { r.Begin(hand, report); }
            catch (Exception e)
            {
                report.Error("srec load", "Begin: " + e.GetType().Name + ": " + e.Message);
                try { r.End(report); } catch (Exception e2) { report.Error("srec load", "End: " + e2.GetType().Name + ": " + e2.Message); }
                if (hand != null) { report.Note("srec load", "record handover released (" + ReleaseHandover(hand) + ")"); pending = null; pendingFrom = ""; }
                return "failed: srec load " + dir + ": Begin threw, restored: " + e.GetType().Name + ": " + e.Message + "\n" + report.Summary(30);
            }
            if (!r.active)
            {
                try { r.End(report); } catch (Exception e2) { report.Error("srec load", "End: " + e2.GetType().Name + ": " + e2.Message); }
                return "refused: srec load " + dir + ": " + FirstError(report) + "\n" + report.Summary(30);
            }
            if (hand != null) { pending = null; pendingFrom = ""; }
            int beginErrors = report.errors.Count;
            viewer = r;
            viewerDir = dir;
            playing = false;
            looping = false;
            speed = 1.0;
            freezeLeaks = 0;
            closedNote = "";
            lastLoad = new List<string>();
            for (int i = 0; i < a.Count; i++)
                if (i < 3 || !string.Equals(a[i], "replace", StringComparison.OrdinalIgnoreCase)) lastLoad.Add(i == 2 ? full : a[i]);
            double dur = Duration(r), t = 0.0;
            string err;
            if (tArg != null && !ParseTime(tArg, 0.0, dur, EventTimeOf(r), out t, out err)) { report.Warn("srec.t", err + "; T = 0"); t = 0.0; }
            r.T = Clamp(t, 0.0, dur);
            EnsureHost();
            string camText = ViewerCamera(r, r.T, gameCam);
            if (w.play)
            {
                speed = w.speed;
                looping = w.loop;
                if (!looping && speed < 0.0 && r.T <= 0.0) r.T = dur;
                playing = true;
            }
            var sb = new StringBuilder();
            sb.Append("srec loaded ").Append(dir).Append(": ").Append(SampleCount(r)).Append(" samples, ").Append(F3(dur)).Append(" s");
            if (r.tracks != null && r.tracks.rec != null) sb.Append(", ").Append(r.tracks.rec.objects.Count).Append(" objects");
            if (hand != null) sb.Append(", record handover taken (").Append(HandoverText(hand)).Append(")");
            if (playing) sb.Append("; playing x").Append(F(speed)).Append(looping ? " loop" : "").Append(" from T ").Append(F3(r.T));
            else sb.Append("; frozen at T ").Append(F3(r.T));
            sb.Append("; ").Append(camText).Append(" (Insert toggles, WASD/QE + right mouse fly); srec play [speed] [loop], srec end restores");
            if (replaced.Length > 0) sb.Append("\nreplaced the loaded recording: ").Append(replaced);
            if (beginErrors > 0) sb.Append("\nBegin reported ").Append(beginErrors).Append(" error(s): the viewer runs with those parts missing (srec end restores what was set up)");
            if (report.errors.Count + report.warnings.Count + report.notes.Count > 0) sb.Append('\n').Append(report.Summary(20));
            return sb.ToString();
        }

        // Ends the viewer and loads the same folder again with the arguments of the last `srec load` (a re-recorded take,
        // or a viewer that a level load closed).
        static string Reload()
        {
            if (lastLoad == null) return "refused: nothing was loaded in this session (srec load <dir>)";
            var a = new List<string>(lastLoad);
            if (viewer != null) a.Add("replace");
            return Load(a);
        }

        static string ListTakes(List<string> a)
        {
            bool json = TakeJson(a, 2);
            int max = 30;
            for (int i = 2; i < a.Count; i++)
            {
                string s = a[i];
                if (!s.StartsWith("max=", StringComparison.OrdinalIgnoreCase) || !int.TryParse(s.Substring(4), NumberStyles.Integer, IC, out max) || max < 1) return "usage: srec list [max=n] [json]";
            }
            string root = RplXTakes.Root();
            int total;
            var takes = RplXTakes.List(root, max, out total);
            return json ? JsonLine.Write(RplXTakes.ToJson(root, takes, total)) : RplXTakes.Text(root, takes, total);
        }

        static string Events(List<string> a)
        {
            bool json = TakeJson(a, 2);
            var r = viewer ?? SceneReplayer.current;
            if (r == null) return NeedViewer();
            var evs = r.Events();
            if (json)
            {
                var l = new List<object>();
                foreach (var e in evs) { var j = new Json.Obj(); j["name"] = e.Key; j["t"] = Math.Round(e.Value, 4); l.Add(j); }
                return JsonLine.Obj("dir", viewer != null ? viewerDir : "", "duration", Duration(r), "events", l);
            }
            var sb = new StringBuilder();
            sb.Append("events ").Append(evs.Count).Append(" (srec seek @name[+-s]), duration ").Append(F3(Duration(r))).Append(" s");
            foreach (var e in evs) sb.Append("\n  ").Append(F3(e.Value)).Append("  ").Append(e.Key);
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

        // The first line tells what End left behind: a clean end keeps the words "released, scene restored" and names the
        // state the scene is in; a restore step that failed makes the reply a failure.
        static string End()
        {
            string pre = "";
            if (probe != null)
            {
                StopProbe("srec end");
                pre = "probe stopped (" + lastProbe + "); ";
            }
            if (viewer == null)
            {
                string was = closedNote;
                closedNote = "";
                if (pending == null) return pre + (was.Length > 0 ? "no recording loaded (" + was + ")" : NoViewer);
                string rel = ReleaseHandover(pending);
                pending = null;
                pendingFrom = "";
                return pre + "no recording loaded; released the pending record handover (" + rel + ")";
            }
            var r = viewer;
            string dir = viewerDir;
            playing = false;
            viewer = null;
            viewerDir = "";
            var report = new SpecReport();
            bool threw = false;
            try { r.End(report); }
            catch (Exception e) { threw = true; report.Error("srec end", e.GetType().Name + ": " + e.Message); }
            string facts;
            try { facts = r.EndFacts(); } catch { facts = ""; }
            int failed = r.RestoreFailures + (threw ? 1 : 0);
            string tail = report.errors.Count + report.warnings.Count + report.notes.Count > 0 ? "\n" + report.Summary(30) : "";
            if (failed > 0) return "failed: srec end " + dir + ": released, but " + failed + " restore step(s) failed (" + FirstError(report) + "); " + facts + tail;
            return pre + "srec end: " + dir + " released, " + facts + tail;
        }

        static string Play(List<string> a)
        {
            string need = NeedViewer();
            if (need != null) return need;
            if (probe != null && probe.kind == "camera") return "busy: camera probe running: srec probe stop first";
            if (a.Count > 2)
            {
                double sp = speed;
                bool lp = false;
                for (int i = 2; i < a.Count; i++)
                    if (!PlayWord(a[i].ToLowerInvariant(), ref sp, ref lp)) return "usage: srec play [speed (non-zero, |speed| <= 100)] [loop|once]";
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
            string need = NeedViewer();
            if (need != null) return need;
            playing = false;
            return "paused: " + ViewerLine();
        }

        // Sets T and applies it in this frame, so the reply and a screenshot of the same frame show the same time. null
        // while the viewer lives, else the refusal (a level load was seen by the apply and closed the viewer).
        static string ApplyAt(double t)
        {
            var r = viewer;
            r.T = Clamp(t, 0.0, Duration(r));
            try { r.ApplyNow(r.T); } catch (Exception e) { Fail("apply", e); }
            if (r.active) return null;
            Orphans();
            return NeedViewer();
        }

        static string Seek(List<string> a)
        {
            string need = NeedViewer();
            if (need != null) return need;
            if (a.Count < 3) return SeekUsage;
            double dur = Duration(viewer), t;
            string err;
            var r = viewer;
            if (!ParseSeek(a, 2, r.T, dur, SampleCount(r), i => SafeSampleTime(r, i), EventTimeOf(r), out t, out err))
                return err.StartsWith("event ", StringComparison.Ordinal) ? "not found: " + err + " (srec events lists them)" : SeekUsage + ": " + err;
            return ApplyAt(t) ?? ViewerLine();
        }

        static string Step(List<string> a)
        {
            string need = NeedViewer();
            if (need != null) return need;
            if (a.Count > 3) return "usage: srec step [dt seconds | next | back]";
            double dur = Duration(viewer), t = viewer.T;
            string v = a.Count > 2 ? a[2].ToLowerInvariant() : "next";
            if (v == "next" || v == "back" || v == "prev")
            {
                int n = SampleCount(viewer);
                if (n <= 0) return "refused: the recording has no samples";
                int at = SafeSampleAt(viewer, t);
                int i = StepSample(at, SafeSampleTime(viewer, at), t, v != "next", n);
                t = SafeSampleTime(viewer, i);
            }
            else
            {
                double dt;
                if (!double.TryParse(v, NumberStyles.Float, IC, out dt) || double.IsNaN(dt) || double.IsInfinity(dt)) return "usage: srec step [dt seconds | next | back]";
                t += dt;
            }
            playing = false;
            return ApplyAt(t) ?? ViewerLine();
        }

        // "T <requested> / <duration> s  sample i/n  paused|playing ...  applied T <t> frame <f>": applied is the time the
        // scene shows now and the frame it was applied in (while playing it trails the requested T by one frame).
        static string ViewerLine()
        {
            var r = viewer;
            if (r == null) return NoViewer;
            double t = r.T;
            int n = SampleCount(r);
            return "T " + F3(t) + " / " + F3(Duration(r)) + " s  sample " + (n > 0 ? SafeSampleAt(r, t).ToString(IC) : "-") + "/" + n + "  " +
                   (playing ? "playing x" + F(speed) + (looping ? " loop" : "") : "paused") +
                   (r.AppliedFrame >= 0 ? "  applied T " + F3(r.AppliedT) + " frame " + r.AppliedFrame.ToString(IC) : "  not applied yet");
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
            if (RplXLoad.Loading()) return;
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
                    string how = a.Count > 3 ? a[3].ToLowerInvariant() : "";
                    if (a.Count > 4 || (how != "" && how != "keep" && how != "load")) return "usage: srec record stop [keep|load] (nothing was stopped; keep hands the record preparation to the next srec load, load also loads the take)";
                    if (manual == null) return SceneRecorder.active != null ? "busy: the active recording belongs to mcam (mcam stop)" : "not recording";
                    if (how != "load" || manual.waiting) return RecordStop(how == "keep", "srec record stop");
                    string take = manual.dir;
                    string stopped = RecordStop(true, "srec record stop load");
                    if (stopped.StartsWith("failed:", StringComparison.Ordinal)) return stopped;
                    string loaded = Load(new List<string> { "srec", "load", take });
                    return loaded.StartsWith("srec loaded", StringComparison.Ordinal) ? loaded + "\n" + stopped : loaded + "\nthe recording itself was stopped: " + stopped;
                case "status":
                    bool json = TakeJson(a, 3);
                    if (json) return JsonLine.Write(manual != null ? RecordJson(manual) : RecordIdleJson());
                    if (manual != null) return RecordLine(manual);
                    if (SceneRecorder.active != null) return "recording (mcam): " + RecorderStatus(SceneRecorder.active);
                    return "not recording" + (lastRecord.Length > 0 ? "; last record: " + lastRecord : "");
            }
            return RecordUsage;
        }

        // A word is key=value when the part before the first '=' is a plain key; a folder with '=' in its name still works
        // when it has a drive or a separator before it.
        static bool IsPair(string s)
        {
            int eq = s != null ? s.IndexOf('=') : -1;
            if (eq <= 0) return false;
            for (int i = 0; i < eq; i++) if (!char.IsLetterOrDigit(s[i]) && s[i] != '_' && s[i] != '.') return false;
            return true;
        }

        static string RecordStart(List<string> a)
        {
            if (a.Count < 3) return RecordUsage;
            string busy = Busy();
            if (busy != null) return busy;
            if (manual != null) return "busy: already recording -> " + manual.dir + " (srec record stop first)";
            if (viewer != null) return "busy: a recording is loaded in the viewer: srec end first";
            if (probe != null) return "busy: a probe is running: srec probe stop first";
            bool named = a.Count > 3 && !IsPair(a[3]);
            int from = named ? 4 : 3;
            string dir;
            try { dir = named ? RplXTakes.Under(RplXTakes.Root(), a[3]) : RplXTakes.NewTake(RplXTakes.Root(), DateTime.Now, p => Directory.Exists(p) || File.Exists(p)); }
            catch (Exception e) { return "refused: bad dir '" + (named ? a[3] : "") + "': " + e.Message; }
            if (MultiCamHide.Active) { MultiCamHide.Restore(); DevLog.Write("[srec] an mcam hide / backfaces set was still applied: restored before the record scan"); }
            var report = new SpecReport();
            MultiCamPlan plan = null;
            RecordSpec baseSpec = null;
            string specPath = OwnArg(a, from, "spec");
            if (specPath != null)
            {
                plan = LoadSpec(specPath, report);
                if (plan != null && plan.spec != null && plan.spec.replay != null && plan.spec.replay.record != null) baseSpec = plan.spec.replay.record;
            }
            bool audioOn, overwrite;
            var spec = RecordArgsOn(baseSpec ?? new RecordSpec(), a, from, report, out audioOn, out overwrite);
            if (File.Exists(Path.Combine(dir, ReplayConst.RecordFile)) && !overwrite) report.Error("srec.dir", dir + " already holds a recording (use a new folder or overwrite=on)");
            var keep = KeepCullers(spec, report);
            if (!report.ok) return "refused: srec record start: " + FirstError(report) + "\n" + report.Summary(20);
            AudioCapSpec aspec = null;
            if (audioOn)
            {
                aspec = new AudioCapSpec();
                aspec.capture = "record";
                string why;
                try { why = AudioCapture.Preflight(aspec); } catch (Exception e) { why = e.GetType().Name + ": " + e.Message; }
                if (why != null) return "refused: srec record start: audio preflight: " + why;
            }
            lastRecord = "";
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
                   " (srec record status shows it, srec record stop cancels); folder " + m.dir +
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
                return (fail.StartsWith("refused", StringComparison.Ordinal) ? "refused: " : "failed: ") + "srec record start " + fail + ", restored: " + FirstError(report) + "\n" + report.Summary(20);
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
            sb.Append(m.audioOn ? ", audio capture on" + (m.audioSilent ? " (SILENT: the capture level is 0)" : "") : ", no audio").Append("; srec event <name> marks a moment, srec record stop [keep|load] ends it");
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
            lastRecord = res.StartsWith("recording -> ", StringComparison.Ordinal) ? "" : res;
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
            if (!SceneRecorder.EventName(name)) return "usage: bad event name '" + name + "': " + EventUsage.Substring(7);
            string detail = a.Count > 3 ? string.Join(" ", a.GetRange(3, a.Count - 3).ToArray()) : "srec event";
            var r = SceneRecorder.active;
            int sample = r != null ? r.samples : -1;
            if (!SceneRecorder.PostEvent(name, detail)) return "refused: no active recording: event '" + name + "' not stored (srec record start or mcam record first)";
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
                AudioCapture.gameBase = m.header.t0;
                started = AudioCapture.Start(m.audio, m.dir, report);
            }
            catch (Exception e) { report.Error("audio", e.GetType().Name + ": " + e.Message); started = false; }
            if (!started) return "refused by the audio capture";
            m.audioSilent = AudioCapture.SilentNow;
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
                lastRecord = "srec record start cancelled (" + why + "): it was waiting for " + (m.keep.Count - KeepReadyCount(m)) + " of " + m.keep.Count + " kept subscene(s)";
                return lastRecord;
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
            if (pending != null && keep) { pendingLoads = RplXLoad.Count(); handoverNote = ""; }
            string head = report.errors.Count > 0 ? "failed: recording stopped with " + report.errors.Count + " error(s) (" + report.errors[0] + ") -> " : "recording stopped -> ";
            string line = head + m.dir + ": " + m.rec.samples + " samples, " + m.rec.objects + " objects, " + (m.rec.bytes / 1048576.0).ToString("0.0", IC) + " MB, " +
                   seconds.ToString("0.00", IC) + " s, pacing changes " + m.changes +
                   (m.audioOn ? (m.audioLost ? ", audio window closed early" : m.audioSilent ? ", audio captured at level 0 (silent)" : ", audio captured") + " (acap result shows the WAV check once it is finalized)" : "") + "; " + hand;
            lastRecord = line;
            return line + (report.errors.Count + report.warnings.Count + report.notes.Count > 0 ? "\n" + report.Summary(20) : "");
        }

        // The recorder stops sampling for good when its writer fails or 30 samples in a row had errors (Failed). A clean early
        // stop (limit, disk space, level load) is handled by Orphans.
        static bool RecorderFailed(ManualRecord m, out string why)
        {
            why = m.rec.Failed ? m.rec.StopReason : null;
            return why != null;
        }

        static Json.Obj RecordJson(ManualRecord m)
        {
            var o = new Json.Obj();
            o["state"] = m.waiting ? "waiting" : "recording";
            o["dir"] = m.dir;
            if (m.waiting)
            {
                o["keptReady"] = KeepReadyCount(m);
                o["kept"] = m.keep.Count;
                o["secondsLeft"] = Math.Round(Math.Max(0f, m.waitUntil - Time.realtimeSinceStartup), 1);
                return o;
            }
            o["samples"] = m.rec.samples;
            o["objects"] = m.rec.objects;
            o["bytes"] = m.rec.bytes;
            o["seconds"] = Math.Round(m.header != null ? Time.timeDbl - m.header.t0 : 0.0, 3);
            o["fps"] = Math.Round(RecentFps(m.frameTimes), 1);
            o["driftMs"] = Math.Round(m.driftMs, 2);
            o["pacingChanges"] = m.changes;
            o["audio"] = m.audioOn ? (m.audioLost ? "closed" : m.audioSilent ? "silent" : "on") : "off";
            o["specEventsFired"] = m.events != null ? m.eventRows : 0;
            o["recorder"] = Json.Parse(m.rec.StatusJson());
            return o;
        }

        static Json.Obj RecordIdleJson()
        {
            var o = new Json.Obj();
            o["state"] = SceneRecorder.active != null ? "recording (mcam)" : "idle";
            if (SceneRecorder.active != null) o["recorder"] = Json.Parse(SceneRecorder.ActiveStatusJson());
            o["lastRecord"] = lastRecord;
            o["failed"] = lastRecord.StartsWith("failed:", StringComparison.Ordinal) || lastRecord.StartsWith("refused:", StringComparison.Ordinal) || lastRecord.StartsWith("cannot ", StringComparison.Ordinal);
            return o;
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
            string failed;
            if (RecorderFailed(m, out failed))
            {
                string stop = RecordStop(false, "recorder failed: " + failed);
                if (!lastRecord.StartsWith("failed:", StringComparison.Ordinal)) lastRecord = "failed: " + failed + "; " + lastRecord;
                DevLog.Write("ERROR [srec] the recorder failed, the manual recording was stopped: " + stop);
                return;
            }
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
            if (probe != null) return "busy: a probe is running (" + ProbeLine(probe) + "): srec probe stop first";
            if (manual != null) return "busy: recording in progress: srec record stop first";
            if (RplXLoad.Loading()) return "busy: the game is loading a level: srec probe again when it has finished";
            if (v == "camera") return CameraProbeStart(a);
            int n = 60, from = 2;
            if (v.Length > 0 && v.IndexOf('=') < 0)
            {
                if (!int.TryParse(v, NumberStyles.Integer, IC, out n) || n < 1 || n > 10000) return "usage: srec probe [n] [eps=m] [rot=deg] [out=dir] | srec probe camera [n] ...";
                from = 3;
            }
            if (viewer == null) return "refused: srec probe needs a loaded recording (srec load <dir>): it compares the replayed transforms at lateWrite with the last component OnPreCull" + (closedNote.Length > 0 ? " (" + closedNote + ")" : "");
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
            if (!report.ok) return "refused: srec probe: " + FirstError(report) + "\n" + report.Summary(10);
            var cam = RenderHost.WorldCam();
            if (cam == null) return "refused: no gameplay camera";
            var rec = viewer.tracks != null ? viewer.tracks.rec : null;
            if (rec == null) return "refused: the replayer has no object table";
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
            if (list.Count == 0) return "refused: no resolved transform in the recording";
            var p = new ProbeRun();
            p.kind = "write";
            p.phase = "starting";
            p.frames = n;
            p.dir = outDir;
            p.tick = Time.frameCount;
            p.loads = RplXLoad.Count();
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
            RefuseUnknown(o, typeof(RecordSpec), CameraProbeOwnKeys, report);
            var spec = new RecordSpec();
            SpecBind.Fill(spec, o, "srec", report);
            float dx = OwnFloat(own, "dx", 1.5f, report), yaw = OwnFloat(own, "yaw", 10f, report), eps = OwnFloat(own, "eps", 1e-5f, report), rot = OwnFloat(own, "rot", 1e-3f, report);
            string outDir = OwnDir(own, ModDir(), report);
            if (!report.ok) return "refused: srec probe camera: " + FirstError(report) + "\n" + report.Summary(10);
            var cam = RenderHost.WorldCam();
            if (cam == null) return "refused: no gameplay camera";
            var p = new ProbeRun();
            p.kind = "camera";
            p.phase = "starting";
            p.frames = 3 + 2 * n + 2;
            p.dir = outDir;
            p.freeze = viewer == null;
            p.tick = Time.frameCount;
            p.loads = RplXLoad.Count();
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
                if (p.gone)
                {
                    if (!wasFree) CameraControl.EndCapture();
                }
                else if (wasFree) CameraControl.Set(aP, aR, fov);
                else
                {
                    CameraControl.EndCapture();
                    if (cam != null) { cam.fieldOfView = fov; cam.nearClipPlane = near; }
                }
                CameraControl.captureLock = wasLock;
                CameraControl.quiet = wasQuiet;
                if (p.freeze)
                {
                    if (p.gone) DevLog.Write("[srec] probe: " + RplXLoad.TimeToGame());
                    else
                    {
                        try { GameManager.TimeScale = gmBefore; } catch { }
                        Time.timeScale = tsBefore;
                    }
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
                SafeFile.WriteAllText(file, MergeCameraDriven(old, scanned, rows, out kept), true);
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
            idleFrames = 0;
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
            try { LevelLoad(); } catch (Exception e) { Fail("level load", e); }
            try { if (viewer != null || (probe != null && probe.freeze)) HoldFreeze(); } catch (Exception e) { Fail("freeze", e); }
            var m = manual;
            if (m != null && m.waiting) { try { WaitTick(m); } catch (Exception e) { Fail("record wait", e); manual = null; ReleaseKept(m); } }
            else if (m != null) { try { RecordTick(m); } catch (Exception e) { Fail("record tick", e); } }
            var p = probe;
            if (p != null && Time.frameCount - p.tick > 300) StopProbe("stalled (no progress for 300 frames)");
            if (viewer != null || manual != null || probe != null || pending != null) idleFrames = 0;
            else if (++idleFrames > HostIdleFrames) DropHost();
        }

        // A probe and a pending record handover do not survive a level load: the probe stops without writing its saved
        // camera pose or time scale into the load, the handover's cullers and kept subscenes go back to the game.
        static void LevelLoad()
        {
            var p = probe;
            if (p != null && RplXLoad.Seen(p.loads))
            {
                p.gone = true;
                StopProbe("level load at frame " + Time.frameCount + " (nothing of the probe was written back)");
            }
            if (pending != null && RplXLoad.Seen(pendingLoads))
            {
                handoverNote = "released by a level load at frame " + Time.frameCount + " (" + ReleaseHandover(pending) + ")";
                pending = null;
                pendingFrom = "";
                DevLog.Write("[srec] record handover " + handoverNote);
            }
        }

        // Nothing to drive: the host and its two per-frame coroutines go away until the next load, record or probe.
        static void DropHost()
        {
            var h = host;
            host = null;
            idleFrames = 0;
            if (h != null) UObj.Destroy(h.gameObject);
        }

        static void Orphans()
        {
            var r = viewer;
            if (r != null && !r.active)
            {
                bool load = r.EndedByLoad;
                if (probe != null) { probe.gone = load; StopProbe(load ? "level load" : "the replay ended outside srec"); }
                viewer = null;
                viewerDir = "";
                playing = false;
                closedNote = load ? "viewer closed: level load at frame " + r.EndFrame + " (scene state not restored, the load replaced it)"
                                  : "viewer closed: the replay was ended outside srec at frame " + r.EndFrame + " (" + r.EndFacts() + ")";
                DevLog.Write("[srec] " + closedNote);
            }
            var m = manual;
            if (m != null && m.rec != null && SceneRecorder.active != m.rec)
            {
                string s = RecordStop(false, "recorder ended outside srec");
                DevLog.Write("[srec] " + s);
            }
            else if (m != null && !m.waiting && m.rec != null && m.rec.Stopped && !m.rec.Failed)
            {
                // a limit, low disk space or a level load ended the sampling; a failure is RecordTick's (lastRecord "failed: ...")
                string why = "recording ended early: " + m.rec.StopReason;
                string s = RecordStop(false, why);
                lastRecord = why + " | " + s.Split('\n')[0];
                DevLog.Write("[srec] " + why + "; " + s);
            }
        }

        // Quick, read-only rows for the doctor command.
        internal static List<Doctor.Row> DoctorRows()
        {
            var rows = new List<Doctor.Row>();
            string root = RplXTakes.Root();
            rows.Add(new Doctor.Row("srec viewer", "take folder", Doctor.Info, root + (Directory.Exists(root) ? "" : " (made by the first srec record start)")));
            var r = viewer;
            if (r != null)
                rows.Add(new Doctor.Row("srec viewer", "viewer", Doctor.Info, "loaded: " + viewerDir + ", the game is frozen by it (Time.timeScale " + Time.timeScale.ToString("0.###", IC) + ")", "srec end"));
            else if (closedNote.Length > 0) rows.Add(new Doctor.Row("srec viewer", "viewer", Doctor.Info, closedNote, "srec reload"));
            else rows.Add(new Doctor.Row("srec viewer", "viewer", Doctor.Ok, "nothing loaded"));
            if (manual != null) rows.Add(new Doctor.Row("srec viewer", "recording", Doctor.Info, manual.waiting ? "waiting to record " + manual.dir : "recording " + manual.dir, "srec record stop"));
            else if (lastRecord.StartsWith("failed:", StringComparison.Ordinal)) rows.Add(new Doctor.Row("srec viewer", "recording", Doctor.Warn, lastRecord.Split('\n')[0], "srec record start again; the log names the failed sample"));
            if (pending != null) rows.Add(new Doctor.Row("srec viewer", "record handover", Doctor.Warn, "pending from " + pendingFrom + ": " + HandoverText(pending) + " stay forced / loaded", "srec load (takes it) or srec end (releases it)"));
            if (errorCount > 0) rows.Add(new Doctor.Row("srec viewer", "frame phases", Doctor.Warn, errorCount + " error(s) in the srec frame phases since the game started (log)"));
            return rows;
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

        // A key that is neither one of the command's own keys nor a field of the spec node is an error that names the valid
        // ones (a typo must not run the command with a default); the key is taken out so it is reported once.
        internal static void RefuseUnknown(Json.Obj o, Type node, string[] ownKeys, SpecReport report)
        {
            List<string> bad = null;
            foreach (var k in o.order) if (SpecBind.Field(node, k) == null) (bad ?? (bad = new List<string>())).Add(k);
            if (bad == null) return;
            var valid = new List<string>();
            if (ownKeys != null) valid.AddRange(ownKeys);
            foreach (var f in SpecBind.Fields(node)) if (!valid.Contains(f.Name)) valid.Add(f.Name);
            string list = string.Join(", ", valid.ToArray());
            foreach (var k in bad)
            {
                o.Remove(k);
                o.order.Remove(k);
                report.Error("srec." + k, "unknown argument '" + k + "' (valid: " + list + ")");
            }
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
            RefuseUnknown(o, typeof(RecordSpec), RecordOwnKeys, report);
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
            if (s.post) r.Warn("srec.post", "not built: post-processing values are not recorded");
            if (s.maxMB < 0 || s.maxSeconds < 0f || s.minFreeMB < 0) r.Error("srec", "maxMB, maxSeconds and minFreeMB must not be negative");
            if (Array.IndexOf(ReplayConst.OnLoads, s.onLoad) < 0) r.Error("srec.onLoad", "continue or stop expected");
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
            var root = new Json.Obj();
            var rp = new Json.Obj();
            rp["play"] = o;
            root["replay"] = rp;
            int errorsBefore = report.errors.Count;
            ReplayConst.CheckRemoved(root, report);
            if (report.errors.Count == errorsBefore) RefuseUnknown(o, typeof(PlaySpec), LoadOwnKeys, report);
            SpecBind.Fill(spec, o, "srec", report);
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

        // The forms of `srec seek`: words[from] alone is a ParseTime form, "sample:<n>" (the time of that sample) or "<p>%"
        // of the duration; "rel <seconds>" moves from now. Anything left over is an error.
        internal static bool ParseSeek(List<string> words, int from, double now, double duration, int sampleCount, Func<int, double> sampleTime, Func<string, double> eventTime, out double t, out string error)
        {
            t = now;
            error = null;
            if (words == null || from >= words.Count || string.IsNullOrEmpty(words[from])) { error = "time expected"; return false; }
            string s = words[from], v = s.ToLowerInvariant();
            int used = 1;
            double x;
            if (v == "rel")
            {
                if (from + 1 >= words.Count || !double.TryParse(words[from + 1], NumberStyles.Float, IC, out x) || double.IsNaN(x) || double.IsInfinity(x)) { error = "rel needs seconds (rel -0.5)"; return false; }
                t = now + x;
                used = 2;
            }
            else if (v.StartsWith("sample:", StringComparison.Ordinal))
            {
                int n;
                if (!int.TryParse(v.Substring(7), NumberStyles.Integer, IC, out n) || n < 0 || n >= sampleCount || sampleTime == null) { error = "sample:<n> needs 0.." + (sampleCount - 1) + ", got '" + s + "'"; return false; }
                t = sampleTime(n);
            }
            else if (v.Length > 1 && v[v.Length - 1] == '%')
            {
                if (!double.TryParse(v.Substring(0, v.Length - 1), NumberStyles.Float, IC, out x) || double.IsNaN(x) || x < 0.0 || x > 100.0) { error = "a percentage 0..100 expected, got '" + s + "'"; return false; }
                t = duration * x / 100.0;
            }
            else if (!ParseTime(s, now, duration, eventTime, out t, out error)) return false;
            if (from + used < words.Count) { t = now; error = "unexpected '" + words[from + used] + "'"; return false; }
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
