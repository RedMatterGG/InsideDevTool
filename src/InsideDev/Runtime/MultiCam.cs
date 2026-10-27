using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HutongGames.PlayMaker;
using UnityEngine;

namespace InsideDev
{
    // MultiCam v2 CORE (docs/MULTICAM_DESIGN.md sections 3, 5, 6): spec load + lint + legacy mapping, the capture
    // runner (step frames, frozen visits with a TAA render budget, measured game time, segments, cues), session push/pop,
    // events and time references, frame writers and CSV/JSON outputs, the per-render look component, keep-loaded
    // subscenes and the authoring probe. Camera behaviours and target resolution live in MultiCamRig / MultiCamTargets.
    public static class MultiCam
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public static string status = "idle";
        internal static MultiCamPlan last;
        internal static MultiCamRunner runner;
        static bool replayLock;
        static float gammaOriginal = float.NaN;
        public const string BusyText = "busy: mcam capturing (allowed now: mcam status|stop|abort|check|list|pose|schema, srec status|objects|event, acap status, state, game_state, ping, audio)";
        public const string Help = "mcam check <spec> [json] [replay|v2] | start <spec> <out> | rehearse <spec> <out> | preview <spec> <out> [t] | record <spec> <out> | replay <spec> <out> [cams=a,b] [range=t0:t1] | take <spec> <out> [cams=a,b] [range=t0:t1] | passes <spec> <out> [cams=a,b] [range=t0:t1] | golden <spec> <out> [every] | stop | abort | status [json] | show <cam> [t] | pose <cam> [t] | views <spec> [shots|t,t,...] [cams=a,b] [out=<json>] | list | probe <obj> | probe <x> <y> <z> <r> | schema";

        public static bool Running { get { return runner != null; } }
        public static bool Capturing { get { return MultiCamSession.active; } }
        internal static bool ReplayRunning { get { return runner != null && runner.replayArgs != null; } }

        public static bool Blocks(List<string> a)
        {
            if (a == null || a.Count == 0) return false;
            if (!(MultiCamSession.active && MultiCamSession.locked) && !(ReplayRunning && replayLock)) return false;
            return !MultiCamGate.Passes(a);
        }

        public static string Command(List<string> a, DevCore core)
        {
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            try
            {
                switch (sub)
                {
                    case "check": return Check(a);
                    case "start": return Start(a, core, "capture");
                    case "rehearse": return Start(a, core, "rehearse");
                    case "preview": return Start(a, core, "preview");
                    case ReplayModes.Record:
                    case ReplayModes.Replay:
                    case ReplayModes.Take:
                    case ReplayModes.Passes:
                    case ReplayModes.Golden: return StartReplay(a, core, sub);
                    case "stop":
                        if (runner == null) return "multicam not running (" + status + ")";
                        runner.RequestStop();
                        return "stopping after the current step";
                    case "abort": return Abort("mcam abort");
                    case "status":
                        if (a.Count > 2 && a[2].ToLowerInvariant() == "json") return runner != null ? runner.ProgressJson() : "{\"state\": \"" + (MultiCamSession.active ? "session active without runner" : "idle") + "\", \"frozen\": " + (MultiCamFreeze.holding ? "true" : "false") + ", \"status\": \"" + status.Replace("\\", "/").Replace("\"", "'").Replace("\n", " ") + "\"}";
                        return runner != null ? runner.StatusLine() : "multicam " + status + (MultiCamSession.active ? "  [session still active: run mcam abort]" : "") + (MultiCamFreeze.holding ? "  [game " + MultiCamFreeze.Status() + "]" : "");
                    case "show": return ShowPose(true, a);
                    case "pose": return ShowPose(false, a);
                    case "views": return Views(a);
                    case "list": return ListCameras();
                    case "probe": return MultiCamProbe.Run(a);
                    case "readback": return MultiCamReadbackTest.Command(a);
                    case "schema": return SpecBind.Schema(typeof(MultiCamSpec));
                    case "help": return Help;
                }
            }
            catch (Exception e) { DevLog.Error("mcam " + sub, e); return "mcam " + sub + " failed: " + e.GetType().Name + ": " + e.Message; }
            return "usage: " + Help;
        }

        static string Check(List<string> a)
        {
            if (a.Count < 3) return "usage: mcam check <spec.json> [json] [replay|v2]";
            bool json = false, forceReplay = false, forceV2 = false;
            for (int i = 3; i < a.Count; i++)
            {
                string k = a[i].ToLowerInvariant();
                if (k == "json") json = true;
                else if (k == "replay") forceReplay = true;
                else if (k == "v2") forceV2 = true;
                else return "usage: mcam check <spec.json> [json] [replay|v2]";
            }
            var plan = MultiCamPlan.Load(a[2], forceReplay);
            if (!forceReplay && !forceV2 && plan.spec != null && plan.spec.replay != null) plan = MultiCamPlan.Load(a[2], true);
            if (plan.spec != null)
            {
                plan.ResolveTargets(runner != null || MultiCamSession.active);
                if (runner == null) last = plan;
            }
            return json ? plan.ReportJson() : plan.ReportText();
        }

        static string Start(List<string> a, DevCore core, string mode)
        {
            string verb = mode == "capture" ? "start" : mode;
            if (runner != null) return "already running: " + runner.StatusLine();
            if (MultiCamSession.active) return "the previous capture session was not restored: run 'mcam abort' first";
            string owner = SceneOwner();
            if (owner != null) return owner;
            if (a.Count < 4) return "usage: mcam " + verb + " <spec.json> <out dir>" + (mode == "preview" ? " [t]" : "");
            double at = double.NaN;
            if (mode == "preview" && a.Count > 4 && !double.TryParse(a[4], NumberStyles.Float, IC, out at)) return "preview time must be game seconds";
            string outDir;
            try { outDir = Path.GetFullPath(a[3]); } catch (Exception e) { return "bad out dir: " + e.Message; }
            var plan = MultiCamPlan.Load(a[2]);
            if (plan.spec == null || !plan.report.ok) return "spec error(s), not started:\n" + plan.report.Summary(40);
            last = plan;
            var host = new GameObject("InsideDev MultiCam");
            UnityEngine.Object.DontDestroyOnLoad(host);
            status = mode + " starting -> " + outDir;
            runner = host.AddComponent<MultiCamRunner>();
            runner.Init(plan, mode, outDir, at, core);
            if (runner == null) return "multicam " + mode + " did not start: " + status;
            var sb = new StringBuilder();
            sb.Append("multicam ").Append(mode).Append(" started: ").Append(plan.spec.cameras.Count).Append(" camera(s), ").Append(plan.segs.Count).Append(" segment(s), ")
              .Append(plan.spec.seconds.ToString("0.##", IC)).Append(" game s max");
            if (mode == "capture") sb.Append(", estimate ").Append(plan.estCaptured.ToString("0", IC)).Append(" captured steps, ~").Append(plan.estMinutes.ToString("0.#", IC)).Append(" min");
            sb.Append(" -> ").Append(outDir);
            if (plan.report.warnings.Count > 0) sb.Append('\n').Append(plan.report.Summary(12));
            return sb.ToString();
        }

        static string StartReplay(List<string> a, DevCore core, string verb)
        {
            string usage = "usage: " + MultiCamLint.Usage(verb);
            if (runner != null) return "already running: " + runner.StatusLine();
            if (MultiCamSession.active) return "the previous capture session was not restored: run 'mcam abort' first";
            string owner = SceneOwner();
            if (owner != null) return owner;
            if (AudioCapture.capturing) return "an acap capture is running: run 'acap stop' first";
            if (a.Count < 4) return usage;
            string outDir;
            try { outDir = Path.GetFullPath(a[3]); } catch (Exception e) { return "bad out dir: " + e.Message; }
            var plan = MultiCamPlan.Load(a[2], true);
            if (plan.spec == null || !plan.report.ok) return "spec error(s), not started:\n" + plan.report.Summary(40);
            var args = new ReplayArgs();
            string err = MultiCamLint.ParseArgs(verb, a, 4, plan.spec, args);
            if (err != null) return err + "\n" + usage;
            string missing = MultiCamLint.MissingRecording(verb, plan.spec, plan.dir, outDir);
            if (missing != null) return missing;
            last = plan;
            replayLock = plan.spec.session.lockInput;
            var host = new GameObject("InsideDev MultiCam");
            UnityEngine.Object.DontDestroyOnLoad(host);
            status = verb + " starting -> " + outDir;
            runner = host.AddComponent<MultiCamRunner>();
            runner.replayArgs = args;
            runner.Init(plan, verb, outDir, double.NaN, core);
            if (runner == null) return "multicam " + verb + " did not start: " + status;
            var sb = new StringBuilder();
            sb.Append("multicam ").Append(verb).Append(" started: ").Append(MultiCamLint.Describe(verb, plan, args)).Append(" -> ").Append(outDir);
            if (plan.report.warnings.Count > 0) sb.Append('\n').Append(plan.report.Summary(12));
            return sb.ToString();
        }

        static string SceneOwner()
        {
            if (SceneReplayer.current != null) return "a recording is loaded (srec viewer or replay): run 'srec end' first";
            if (SceneRecorder.active != null) return "a manual recording is running: run 'srec record stop' first";
            return null;
        }

        public static string Abort(string why)
        {
            string r;
            if (runner != null) { runner.AbortNow(why); r = "aborted: session restored"; }
            else if (MultiCamSession.active) { MultiCamSession.Pop(); r = "no runner was alive; session restored"; status = "aborted (" + why + ")"; }
            else r = "nothing to abort (" + status + ")";
            if (AudioCapture.capturing && MultiCamSafety.AudioOff(why, null)) r += "; audio capture stopped (rtpc 0 first)";
            if (runner == null && (SceneReplayer.current != null || SceneRecorder.active != null)) r += "; an srec session is still active (srec end | srec record stop)";
            return r;
        }

        static string ShowPose(bool show, List<string> a)
        {
            if (a.Count < 3) return "usage: mcam " + (show ? "show" : "pose") + " <camera> [t]";
            string name = a[2];
            CamPose p;
            if (runner != null)
            {
                if (show) return BusyText;
                if (!runner.PoseNow(name, out p)) return "unknown camera '" + name + "' or no pose yet";
                return PoseText(name, p);
            }
            if (last == null || last.spec == null) return "no spec loaded: run 'mcam check <spec>' first";
            double t = 0;
            if (a.Count > 3 && !double.TryParse(a[3], NumberStyles.Float, IC, out t)) return "t must be game seconds";
            int idx;
            if (!last.camIndex.TryGetValue(name, out idx)) return "unknown camera '" + name + "'";
            string err, view = "";
            CamPose[] ps;
            var plan = last;
            if (!MultiCamRunner.EvaluateAt(plan, t, (c, all) => view = ViewLine(plan, plan.spec.cameras[idx], c, all[idx]).Text(), out ps, out err)) return err;
            p = ps[idx];
            string hidden = "";
            if (show)
            {
                CameraControl.Set(p.p, p.r, p.fov, p.near > 0 ? p.near : MultiCamSession.camNear);
                hidden = ShowHide(plan, idx, t, p);
            }
            return (show ? "free camera -> " : "") + PoseText(name, p) + "\nview: " + view + (show ? "\nhide: " + hidden : "");
        }

        // mcam show: the camera's hide set at t on the free camera (spare layer + cullingMask, MultiCamHide), kept until the
        // next show, `cam off` or an mcam run.
        static string ShowHide(MultiCamPlan plan, int idx, double t, CamPose p)
        {
            try
            {
                var viewer = SceneReplayer.current;
                bool live = viewer == null || !viewer.active;
                Func<TimeRef, double> timeOf = r => r == null ? 0.0 : r.absolute ? r.offset : (live ? plan.Predict(r) : viewer.EventTime(r.ev) + r.offset);
                var names = MultiCamShots.HideNames(plan.spec, idx, t, timeOf, p.flags);
                if (names.Count == 0) { MultiCamHide.EndPreview(); return "none"; }
                var rep = new SpecReport();
                var set = MultiCamHide.Resolve(names, plan.spec.cameras[idx].where + ".hide", rep);
                var c = CameraControl.Current;
                if (c == null) return "no camera";
                if (MultiCamHide.Layer < 0) MultiCamHide.PickLayer();
                MultiCamHide.Apply(c, set, "show " + plan.spec.cameras[idx].name);
                return MultiCamHide.Text(set) + " (" + MultiCamHide.layerNote + ")";
            }
            catch (Exception e) { DevLog.Error("mcam show hide", e); return "failed: " + e.GetType().Name + ": " + e.Message; }
        }

        internal static ViewResult ViewLine(MultiCamPlan plan, CamSpec cs, MultiCamContext c, CamPose p)
        {
            try
            {
                var src = !string.IsNullOrEmpty(p.copyOf) ? plan.spec.FindCamera(p.copyOf) ?? cs : cs;
                var v = MultiCamView.Spec(cs);
                Transform own = null;
                var r = src.look != null && !src.look.isSelf && !src.look.isNone ? src.look : src.target;
                TargetSample s;
                if (r != null && !r.fixedPoint && c.Target(r, out s) && s.ok) own = MultiCamView.Own(r, s.go);
                List<Transform> hidden = null;
                if (cs.index >= 0 && MultiCamShots.AnyHide(plan.spec, cs.index))
                {
                    var names = MultiCamShots.HideNames(plan.spec, cs.index, c.t, x => c.TimeOf(x, 0.0), p.flags);
                    if (names.Count > 0) hidden = MultiCamHide.Resolve(names, cs.where + ".hide", null).roots;
                }
                return MultiCamView.Check(p, p.aim, v, MultiCamView.Avoid(c, v), c.aspect, own, hidden);
            }
            catch (Exception e) { var f = new ViewResult(); f.visible = false; f.blocker = "view check failed: " + e.GetType().Name + ": " + e.Message; return f; }
        }

        // mcam views: view checks (target visible, near plane inside geometry) of camera poses at chosen record times, with the
        // srec viewer moved to each time (srec load first; the scene goes back to the viewer's time afterwards). `shots`
        // checks every director shot (blend done, mid-hold) and every events beat (move done, mid-hold).
        static string Views(List<string> a)
        {
            const string usage = "usage: mcam views <spec.json> [shots | <t>[,<t>...]] [cams=a,b] [out=<file.json>]";
            if (runner != null) return BusyText;
            if (a.Count < 3) return usage;
            var plan = MultiCamPlan.Load(a[2], true);
            if (plan.spec == null || !plan.report.ok) return "spec error(s):\n" + plan.report.Summary(20);
            last = plan;
            var spec = plan.spec;
            var times = new List<double>();
            var labels = new List<string>();
            var only = new List<int>();
            HashSet<string> cams = null;
            string outFile = null;
            bool shots = true;
            for (int i = 3; i < a.Count; i++)
            {
                string k = a[i];
                if (k.StartsWith("cams=", StringComparison.OrdinalIgnoreCase)) { cams = new HashSet<string>(k.Substring(5).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)); continue; }
                if (k.StartsWith("out=", StringComparison.OrdinalIgnoreCase)) { outFile = k.Substring(4); continue; }
                if (k.ToLowerInvariant() == "shots") { shots = true; continue; }
                shots = false;
                foreach (var part in k.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    double v;
                    if (!double.TryParse(part, NumberStyles.Float, IC, out v)) return "bad time '" + part + "'\n" + usage;
                    times.Add(v); labels.Add(""); only.Add(-1);
                }
            }
            if (cams != null) foreach (var n in cams) if (!plan.camIndex.ContainsKey(n)) return "unknown camera '" + n + "'";
            var viewer = SceneReplayer.current;
            bool live = viewer == null || !viewer.active;
            Func<string, double> evt = n => { double v = live ? double.NaN : viewer.EventTime(n); return double.IsNaN(v) ? plan.Predict(new TimeRef(n, 0)) : v; };
            if (shots) ViewTimes(spec, evt, cams, times, labels, only);
            if (times.Count == 0) return "nothing to check (no director shots or events beats" + (cams != null ? " among cams=" : "") + "); give times: " + usage;
            if (times.Count > 400) return times.Count + " checks: more than 400, narrow cams= or the times";
            double back = live ? 0.0 : viewer.T;
            var rows = new List<object>();
            var sb = new StringBuilder();
            int bad = 0, n2 = 0;
            try
            {
                for (int k = 0; k < times.Count; k++)
                {
                    double t = times[k];
                    if (!live) viewer.ApplyNow(t);
                    CamPose[] ps;
                    string err;
                    int kk = k;
                    var lines = new List<string>();
                    bool ok = MultiCamRunner.EvaluateAt(plan, t, (c, all) =>
                    {
                        for (int i = 0; i < spec.cameras.Count; i++)
                        {
                            var cs = spec.cameras[i];
                            if (only[kk] >= 0 ? i != only[kk] : (cams != null ? !cams.Contains(cs.name) : !cs.enabled)) continue;
                            var r = ViewLine(plan, cs, c, all[i]);
                            n2++;
                            if (!r.visible || r.inside) bad++;
                            var row = new Json.Obj();
                            row["t"] = t; row["camera"] = cs.name; row["label"] = labels[kk]; row["visible"] = r.visible; row["blocker"] = r.blocker;
                            row["inside"] = r.inside; row["insideBy"] = r.insideBy;
                            row["pos"] = new List<object> { (double)all[i].p.x, (double)all[i].p.y, (double)all[i].p.z };
                            row["aim"] = new List<object> { (double)all[i].aim.x, (double)all[i].aim.y, (double)all[i].aim.z };
                            row["fov"] = (double)all[i].fov; row["flags"] = PoseFlags.Letters(all[i].flags); row["source"] = all[i].copyOf ?? "";
                            rows.Add(row);
                            lines.Add("t " + t.ToString("0.000", IC) + "  " + cs.name + (labels[kk].Length > 0 ? " [" + labels[kk] + "]" : "") + (string.IsNullOrEmpty(all[i].copyOf) ? "" : " <- " + all[i].copyOf) + ": " + r.Text());
                        }
                    }, out ps, out err);
                    if (!ok) { sb.Append("t ").Append(t.ToString("0.000", IC)).Append(": ").Append(err).Append('\n'); continue; }
                    foreach (var l in lines) sb.Append(l).Append('\n');
                }
            }
            finally { if (!live) { try { viewer.ApplyNow(back); } catch (Exception e) { DevLog.Error("mcam views restore", e); } } }
            if (outFile != null)
            {
                try
                {
                    var o = new Json.Obj();
                    o["spec"] = plan.path; o["live"] = live; o["checks"] = (double)n2; o["problems"] = (double)bad; o["rows"] = rows;
                    File.WriteAllText(Path.GetFullPath(outFile), Json.Write(o));
                }
                catch (Exception e) { sb.Append("out: ").Append(e.Message).Append('\n'); }
            }
            return "mcam views: " + n2 + " check(s), " + bad + " with the target hidden or the near plane inside geometry" +
                   (live ? " (no recording loaded: the live scene as it is now)" : "") + "\n" + sb.ToString().TrimEnd();
        }

        static void ViewTimes(MultiCamSpec spec, Func<string, double> evt, HashSet<string> cams, List<double> times, List<string> labels, List<int> only)
        {
            Func<TimeRef, double> at = r => r == null ? double.NaN : r.absolute ? r.offset : evt(r.ev) + r.offset;
            for (int i = 0; i < spec.cameras.Count; i++)
            {
                var c = spec.cameras[i];
                if (cams != null && !cams.Contains(c.name)) continue;
                if (c.mode == "director")
                {
                    var ts = new List<double>();
                    foreach (var sh in c.shots) ts.Add(sh != null ? at(sh.from) : double.NaN);
                    for (int k = 0; k < c.shots.Count; k++)
                    {
                        var sh = c.shots[k];
                        if (sh == null || double.IsNaN(ts[k])) continue;
                        double next = double.PositiveInfinity;
                        for (int j = 0; j < ts.Count; j++) if (!double.IsNaN(ts[j]) && ts[j] > ts[k] && ts[j] < next) next = ts[j];
                        double inT = ts[k] + Math.Max(0f, sh.blend) + 0.05;
                        double mid = double.IsInfinity(next) ? inT + 1.0 : (inT + next) * 0.5;
                        times.Add(inT); labels.Add("shot " + k + " " + sh.cam + " in"); only.Add(i);
                        if (mid > inT + 0.1) { times.Add(mid); labels.Add("shot " + k + " " + sh.cam + " mid"); only.Add(i); }
                    }
                }
                else if (c.mode == "events")
                {
                    int n = c.beats.Count;
                    var bt = new double[n]; var hold = new float[n]; var late = new float[n]; var move = new float[n];
                    for (int k = 0; k < n; k++)
                    {
                        var b = c.beats[k];
                        bt[k] = b != null ? at(b.at) : double.NaN;
                        hold[k] = b != null && b.hold > 0f ? b.hold : c.hold;
                        move[k] = b != null && b.move > 0f ? b.move : c.move;
                        late[k] = b != null && b.late >= 0f ? b.late : c.late;
                    }
                    var start = MultiCamGeom.Schedule(bt, hold, late);
                    for (int k = 0; k < n; k++)
                    {
                        if (double.IsNaN(start[k])) continue;
                        string lb = "beat " + (c.beats[k].label.Length > 0 ? c.beats[k].label : k.ToString(IC));
                        times.Add(start[k] + move[k] + 0.05); labels.Add(lb + " in"); only.Add(i);
                        if (hold[k] > move[k] + 0.2f) { times.Add(start[k] + (move[k] + hold[k]) * 0.5); labels.Add(lb + " mid"); only.Add(i); }
                    }
                }
            }
        }

        internal static string PoseText(string name, CamPose p)
        {
            var e = p.r.eulerAngles;
            return name + "  " + F(p.p.x) + " " + F(p.p.y) + " " + F(p.p.z) + "  yaw " + F(Signed(e.y)) + " pitch " + F(Signed(e.x)) + " roll " + F(Signed(e.z)) + " fov " + F(p.fov) +
                   "  aim " + F(p.aim.x) + " " + F(p.aim.y) + " " + F(p.aim.z) + (p.near > 0 ? "  near " + F(p.near) : "") + (p.flags != 0 ? "  flags " + PoseFlags.Letters(p.flags) : "") +
                   (!string.IsNullOrEmpty(p.copyOf) ? "  copyOf " + p.copyOf : "") + "\ncam pose " + F(p.p.x) + " " + F(p.p.y) + " " + F(p.p.z) + " " + F(Signed(e.y)) + " " + F(Signed(e.x)) + " " + F(p.fov) + " 0 " + F(Signed(e.z));
        }

        static string F(float v) { return v.ToString("0.###", IC); }
        internal static float Signed(float a) { return a > 180f ? a - 360f : a; }

        static string ListCameras()
        {
            if (last == null || last.spec == null) return "no spec loaded: run 'mcam check <spec>' first";
            var sb = new StringBuilder();
            sb.Append(last.path).Append(": ").Append(last.spec.cameras.Count).Append(" camera(s)\n");
            foreach (var c in last.spec.cameras)
            {
                sb.Append("  ").Append(c.name).Append("  ").Append(c.mode).Append(c.enabled ? "" : "  [disabled]");
                if (c.render != null) sb.Append("  W").Append(c.render.warmup).Append("+N").Append(c.render.samples).Append(' ').Append(c.render.taa);
                if (c.target != null) sb.Append("  target ").Append(c.target.text);
                if (c.look != null) sb.Append("  look ").Append(c.look.text);
                if (c.mode == "static") sb.Append("  pos ").Append(F(c.pos.x)).Append(',').Append(F(c.pos.y)).Append(',').Append(F(c.pos.z));
                if (c.shots.Count > 0) sb.Append("  shots ").Append(c.shots.Count);
                if (c.hide.Count > 0) sb.Append("  hide ").Append(c.hide.Count);
                if (!string.IsNullOrEmpty(c.label)) sb.Append("  '").Append(c.label).Append('\'');
                sb.Append('\n');
            }
            return sb.ToString();
        }

        public static string Hud(List<string> a, DevCore core)
        {
            if (core == null) core = DevCore.Instance;
            if (core == null) return "no editor core";
            bool cur = MultiCamClean.GetHud(core);
            bool v = a.Count > 1 ? On(a[1]) : !cur;
            MultiCamClean.SetHud(core, v);
            return "hud " + (v ? "on" : "off") + " (not saved)";
        }

        public static string Clean(List<string> a, DevCore core)
        {
            if (core == null) core = DevCore.Instance;
            bool on = a.Count > 1 ? On(a[1]) : !MultiCamClean.on;
            if (on) MultiCamClean.Enable(core);
            else if (MultiCamSession.active && MultiCamSession.cleanedBySession) return "clean frames are held by the running mcam session";
            else MultiCamClean.Restore(core);
            return "clean " + (MultiCamClean.on ? "on (editor overlays off, not saved)" : "off (overlays restored)");
        }

        public static string Gamma(List<string> a)
        {
            var cam = CameraControl.Current;
            if (cam == null) return "no gameplay camera";
            var pc = cam.GetComponent<D11PDPostCombined>();
            if (pc == null) return "no D11PDPostCombined on the gameplay camera";
            if (a.Count < 2) return "gamma_gamma " + pc.gamma_gamma.ToString("0.####", IC) + (float.IsNaN(gammaOriginal) ? "" : " (original " + gammaOriginal.ToString("0.####", IC) + ")");
            if (a[1].ToLowerInvariant() == "reset")
            {
                if (float.IsNaN(gammaOriginal)) return "gamma untouched: gamma_gamma " + pc.gamma_gamma.ToString("0.####", IC);
                pc.gamma_gamma = gammaOriginal;
                gammaOriginal = float.NaN;
                return "gamma reset: gamma_gamma " + pc.gamma_gamma.ToString("0.####", IC);
            }
            float v;
            if (!float.TryParse(a[1], NumberStyles.Float, IC, out v)) return "usage: gamma <0..1> | gamma reset";
            if (float.IsNaN(gammaOriginal)) gammaOriginal = pc.gamma_gamma;
            pc.SetGamma(Mathf.Clamp01(v));
            return "gamma " + Mathf.Clamp01(v).ToString("0.###", IC) + " -> gamma_gamma " + pc.gamma_gamma.ToString("0.####", IC) + " (not saved; gamma reset restores " + gammaOriginal.ToString("0.####", IC) + ")";
        }

        public static string Seed(List<string> a)
        {
            int n;
            if (a.Count < 2 || !int.TryParse(a[1], NumberStyles.Integer, IC, out n)) return "usage: seed <n>";
            ApplySeed(n);
            return "seeded " + n + " (UnityEngine.Random, RandomGenerator.ResetAll, PDRandom)";
        }

        public static void ApplySeed(int n)
        {
            UnityEngine.Random.seed = n;
            try { RandomGenerator.ResetAll(); } catch (Exception e) { DevLog.Error("seed RandomGenerator", e); }
            try { PDRandom.seed = n; } catch (Exception e) { DevLog.Error("seed PDRandom", e); }
        }

        static bool On(string s) { s = s.ToLowerInvariant(); return s == "on" || s == "1" || s == "true" || s == "yes"; }
    }

    // ------------------------------------------------------------------ loaded spec: legacy mapping, overlays, lint, segments

    internal sealed class MultiCamSeg
    {
        public int index;
        public SegmentSpec spec;
        public int rate = 30, steps = 1, captured = 1;
        public float speed = 1f;
        public string label = "";
        public string Describe() { return "R " + rate + " N " + steps + " M " + captured + " speed " + speed.ToString("0.####", CultureInfo.InvariantCulture); }
    }

    internal sealed class MultiCamPlan
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly string[] Ops = { "<", "<=", ">", ">=", "==", "!=" };
        static readonly string[] CondProps = { "x", "y", "z", "vx", "vy", "vz", "speed", "dist" };
        public MultiCamSpec spec;
        public readonly SpecReport report = new SpecReport();
        public string path = "", dir = "", rehearsalDir = "";
        public bool legacy, replayLint;
        public readonly List<MultiCamSeg> segs = new List<MultiCamSeg>();
        public List<int> order = new List<int>();
        public readonly Dictionary<string, int> camIndex = new Dictionary<string, int>();
        public readonly List<string> targetLines = new List<string>();
        public readonly HashSet<string> referenced = new HashSet<string>();
        public double estSteps, estCaptured, estRenders, estMinutes, estGB, estGameSeconds;
        Dictionary<string, double> rehearsal;

        public static MultiCamPlan Load(string file) { return Load(file, false); }

        public static MultiCamPlan Load(string file, bool replay)
        {
            var p = new MultiCamPlan();
            p.replayLint = replay;
            p.path = file ?? "";
            string text;
            try { p.path = Path.GetFullPath(file); text = File.ReadAllText(p.path); }
            catch (Exception e) { p.report.Error(file, "cannot read spec: " + e.Message); return p; }
            try { p.dir = Path.GetDirectoryName(p.path); } catch { p.dir = ""; }
            p.spec = MultiCamSpec.Parse(text, p.path, p.report);
            if (p.spec == null) return p;
            try { p.Prepare(); }
            catch (Exception e) { p.report.Error("", "spec preparation failed: " + e.GetType().Name + ": " + e.Message); }
            return p;
        }

        void Prepare()
        {
            Legacy();
            Effective();
            BuildSegments();
            Lint();
            try { MultiCamRig.Validate(spec, report); }
            catch (Exception e) { report.Error("cameras", "rig validation failed: " + e.GetType().Name + ": " + e.Message); }
            try { order = MultiCamRig.EvalOrder(spec, report) ?? new List<int>(); }
            catch (Exception e) { report.Error("cameras", "evaluation order failed: " + e.Message); order = new List<int>(); }
            var seen = new HashSet<int>();
            foreach (int i in order) if (i < 0 || i >= spec.cameras.Count || !seen.Add(i)) report.Error("cameras", "evaluation order is invalid (index " + i + ")");
            if (seen.Count != spec.cameras.Count) report.Error("cameras", "evaluation order covers " + seen.Count + " of " + spec.cameras.Count + " cameras");
            Estimate();
            LintHolds();
        }

        void LintHolds()
        {
            foreach (var c in spec.cameras)
            {
                if (c.mode != "director" || c.shots == null) continue;
                string w = string.IsNullOrEmpty(c.where) ? "cameras[" + c.index + "]" : c.where;
                var times = new List<KeyValuePair<double, int>>();
                for (int i = 0; i < c.shots.Count; i++)
                {
                    if (c.shots[i] == null) continue;
                    double t = Predict(c.shots[i].from ?? new TimeRef(0));
                    if (!double.IsNaN(t)) times.Add(new KeyValuePair<double, int>(t, i));
                }
                times.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Value.CompareTo(b.Value));
                for (int k = 0; k < times.Count; k++)
                {
                    double t = times[k].Key;
                    bool last = k + 1 >= times.Count;
                    double next = last ? estGameSeconds : times[k + 1].Key;
                    if (!last && next <= t + 1e-9) { report.Error(w + ".shots[" + times[k + 1].Value + "].from", "two shots start at " + t.ToString("0.###", IC)); continue; }
                    double hold = OutTime(next) - OutTime(t);
                    if (hold < c.minHold - 1e-6 && (!last || hold > 0))
                        report.Warn(w + ".shots[" + times[k].Value + "]", "'" + c.shots[times[k].Value].cam + "' holds " + hold.ToString("0.##", IC) + " output s, less than minHold " + c.minHold.ToString("0.##", IC));
                }
            }
        }

        double OutTime(double t)
        {
            var starts = new List<double>();
            for (int i = 0; i < segs.Count; i++) starts.Add(i == 0 ? 0 : Predict(segs[i].spec.from));
            var cuts = new List<double> { 0, estGameSeconds };
            foreach (var s in starts) if (!double.IsNaN(s) && s > 0 && s < estGameSeconds) cuts.Add(s);
            cuts.Sort();
            double o = 0, speed = 1;
            for (int b = 0; b + 1 < cuts.Count; b++)
            {
                double a0 = cuts[b], a1 = cuts[b + 1];
                if (a1 <= a0) continue;
                int act = 0;
                for (int i = 0; i < segs.Count; i++) if (i == 0 || (!double.IsNaN(starts[i]) && starts[i] <= a0 + 1e-9)) act = i;
                speed = Math.Max(1e-6, segs[act].speed);
                if (t <= a0) return o;
                o += (Math.Min(t, a1) - a0) / speed;
                if (t <= a1) return o;
            }
            return o + Math.Max(0, t - cuts[cuts.Count - 1]) / speed;
        }

        void Legacy()
        {
            legacy = !spec.Has("version") || spec.version < 2;
            if (legacy)
            {
                if (!spec.Has("settle")) spec.settle = 3;
                if (!spec.Has("jpeg") && !spec.output.Has("jpeg")) spec.jpeg = 92;
                report.Note("version", "no version key: v1 legacy defaults (fov 40, settle 3, jpeg 92, looks at bounds)");
            }
            if (spec.rates.Count > 0 && spec.timing.segments.Count == 0)
            {
                for (int i = 0; i < spec.rates.Count; i++)
                {
                    var r = spec.rates[i];
                    string w = "rates[" + i + "]";
                    int ri = (int)Math.Round(r.rate);
                    if (Math.Abs(r.rate - ri) > 1e-4) report.Error(w, "stepsPerGameSecond " + r.rate.ToString("0.###", IC) + " must be a whole number");
                    var s = new SegmentSpec();
                    s.from = r.from ?? new TimeRef(0);
                    s.rate = ri;
                    s.samples = Math.Max(1, r.group);
                    s.shutter = 360f;
                    s.label = "rate" + i;
                    s.where = w;
                    s.given.AddRange(new[] { "from", "rate", "samples", "shutter", "label" });
                    spec.timing.segments.Add(s);
                }
                if (!spec.timing.Has("fps") && spec.rates[0].rate > 0) spec.timing.fps = spec.rates[0].rate / Math.Max(1, spec.rates[0].group);
            }
            else if (spec.rates.Count > 0) report.Warn("rates", "ignored: timing.segments is given");
            if (spec.settle >= 0)
            {
                if (spec.Has("render")) { if (spec.Has("settle")) report.Warn("settle", "ignored: render is given"); }
                else
                {
                    if (spec.settle == 0) report.Warn("settle", "settle 0 maps to render.warmup 1 (warmup must be >= 1)");
                    var r = new RenderSpec();
                    r.warmup = Math.Max(1, spec.settle); r.samples = 1; r.taa = "keep"; r.where = "settle";
                    r.given.AddRange(new[] { "warmup", "samples", "taa" });
                    spec.render = r;
                }
            }
            if (spec.jpeg >= 0) { spec.output.jpeg = spec.jpeg; if (!spec.output.given.Contains("jpeg")) spec.output.given.Add("jpeg"); }
            if (!string.IsNullOrEmpty(spec.rehearsal))
            {
                try { rehearsalDir = Path.IsPathRooted(spec.rehearsal) ? spec.rehearsal : Path.GetFullPath(Path.Combine(dir, spec.rehearsal)); }
                catch (Exception e) { report.Error("rehearsal", "bad path: " + e.Message); }
            }
        }

        void Effective()
        {
            var d = spec.defaults;
            var list = new List<CamSpec>();
            for (int i = 0; i < spec.cameras.Count; i++)
            {
                var c0 = spec.cameras[i];
                var c = d != null ? SpecBind.Overlay(d, c0) : c0;
                c.render = SpecBind.Overlay(SpecBind.Overlay(spec.render, d != null ? d.render : null), c0.render);
                c.post = SpecBind.Overlay(SpecBind.Overlay(spec.post, d != null ? d.post : null), c0.post);
                c.shake = SpecBind.Overlay(d != null ? d.shake : null, c0.shake);
                c.motion = SpecBind.Overlay(d != null ? d.motion : null, c0.motion);
                c.index = i;
                if (legacy && !c.Has("fov") && c.mode != "game" && c.mode != "director") c.fov = 40f;
                if (!string.IsNullOrEmpty(c.point) && c.target != null && string.IsNullOrEmpty(c.target.point) && !c.target.isGroup && !c.target.fixedPoint)
                {
                    var t = SpecBind.Overlay<TargetRef>(c.target, null);
                    t.point = c.point;
                    if (!t.given.Contains("point")) t.given.Add("point");
                    c.target = t;
                }
                if (legacy)
                {
                    c.target = LegacyPoint(c.target, null);
                    c.look = LegacyPoint(c.look, "bounds");
                    if (c.keys != null) for (int k = 0; k < c.keys.Count; k++) if (c.keys[k].look != null) c.keys[k].look = LegacyPoint(c.keys[k].look, "bounds");
                }
                list.Add(c);
            }
            spec.cameras.Clear();
            spec.cameras.AddRange(list);
            camIndex.Clear();
            for (int i = 0; i < spec.cameras.Count; i++) if (!string.IsNullOrEmpty(spec.cameras[i].name) && !camIndex.ContainsKey(spec.cameras[i].name)) camIndex[spec.cameras[i].name] = i;
        }

        static TargetRef LegacyPoint(TargetRef r, string objectPoint)
        {
            if (r == null || r.isNone || r.isSelf || r.isGroup || r.fixedPoint || !string.IsNullOrEmpty(r.point)) return r;
            string b = (r.of ?? "").Trim();
            string p = b == "boy" || b == "huddle" || b == "main" ? "pos3" : b == "game" || b == "gameaim" || b == "probe" ? null : objectPoint;
            if (string.IsNullOrEmpty(p)) return r;
            var t = SpecBind.Overlay<TargetRef>(r, null);
            t.point = p;
            if (!t.given.Contains("point")) t.given.Add("point");
            return t;
        }

        void BuildSegments()
        {
            var tm = spec.timing;
            if (tm.fps <= 0f) { report.Error("timing.fps", "must be > 0"); tm.fps = 30f; }
            if (tm.physics != "game" && tm.physics != "fine") { report.Error("timing.physics", "'game' or 'fine' expected"); tm.physics = "game"; }
            if (tm.physics == "fine") report.Warn("timing.physics", "'fine' changes fixedDeltaTime: trajectories, CameraScript smoothing (/60 per fixed step) and LogicManager interpolation differ from normal play (experimental)");
            var list = tm.segments;
            if (list.Count == 0) list.Add(new SegmentSpec());
            double fps = tm.fps;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                string w = "timing.segments[" + i + "]";
                var g = new MultiCamSeg { index = i, spec = s, label = s.label ?? "" };
                int m = s.samples;
                if (m < 1) { report.Error(w + ".samples", ">= 1 required"); m = 1; }
                double r; int n;
                if (s.rate > 0)
                {
                    n = m; r = s.rate;
                    g.speed = (float)(n * fps / r);
                    if (s.Has("speed") && Math.Abs(s.speed - g.speed) > 1e-4) report.Warn(w + ".speed", "ignored: explicit rate " + s.rate + " with " + m + " sample(s) per frame gives speed " + g.speed.ToString("0.####", IC));
                }
                else
                {
                    if (s.speed <= 0f) { report.Error(w + ".speed", "must be > 0"); s.speed = 1f; }
                    g.speed = s.speed;
                    if (m == 1) { n = 1; r = fps / s.speed; }
                    else
                    {
                        float sh = s.shutter;
                        if (sh <= 0f || sh > 360f) { report.Error(w + ".shutter", "must be in (0, 360] degrees"); sh = 180f; }
                        n = Math.Max(m, (int)Math.Round(m * 360.0 / sh));
                        r = n * fps / s.speed;
                    }
                }
                int ri = r < int.MaxValue ? (int)Math.Round(r) : int.MaxValue;
                if (!replayLint && Math.Abs(r - ri) > 1e-3) report.Error(w, "steps per game second R = " + r.ToString("0.###", IC) + " is not whole: choose fps / speed / samples / shutter so that fps * N / speed is an integer (N = " + n + ")");
                if (!replayLint && (ri < MultiCamConst.MinRate || ri > MultiCamConst.MaxRate)) report.Error(w, "steps per game second R = " + ri + " outside [" + MultiCamConst.MinRate + ", " + MultiCamConst.MaxRate + "]");
                g.rate = Mathf.Clamp(ri, MultiCamConst.MinRate, MultiCamConst.MaxRate);
                g.steps = n; g.captured = m;
                if (!replayLint && tm.physics == "game" && ((g.rate < 60 && 60 % g.rate != 0) || (g.rate > 60 && g.rate % 60 != 0)))
                    report.Warn(w, "R = " + g.rate + " gives an uneven physics cadence with 60 Hz fixed steps (prefer divisors or multiples of 60)");
                segs.Add(g);
            }
            double prev = double.NegativeInfinity;
            for (int i = 0; i < list.Count; i++)
            {
                var f = list[i].from;
                if (f == null || !f.absolute) continue;
                if (f.offset < prev - 1e-9) report.Warn("timing.segments[" + i + "].from", "segments should be listed in time order");
                prev = f.offset;
            }
            if (list[0].from != null && !(list[0].from.absolute && list[0].from.offset <= 1e-9)) report.Note("timing.segments[0].from", "segment 0 is used until a later segment is reached");
        }

        void Lint()
        {
            if (spec.seconds <= 0) report.Error("seconds", "must be > 0");
            if (spec.preroll < 0) report.Error("preroll", "must be >= 0");
            var names = new HashSet<string>();
            int enabled = 0;
            for (int i = 0; i < spec.cameras.Count; i++)
            {
                var c = spec.cameras[i];
                string w = string.IsNullOrEmpty(c.where) ? "cameras[" + i + "]" : c.where;
                if (string.IsNullOrEmpty(c.name)) report.Error(w, "camera name required");
                else if (!ValidName(c.name, "-_")) report.Error(w + ".name", "'" + c.name + "': only letters, digits, '_' and '-' (it is a folder name)");
                else if (!names.Add(c.name)) report.Error(w + ".name", "duplicate camera name '" + c.name + "'");
                if (c.enabled) enabled++;
                var r = c.render;
                if (r != null)
                {
                    if (r.warmup < 1) report.Error(w + ".render.warmup", ">= 1 required (the first render after a pose change is culled and reprojected from the previous camera)");
                    if (r.samples < 1) report.Error(w + ".render.samples", ">= 1 required");
                    if (r.taa != "reset" && r.taa != "keep" && r.taa != "toggle") report.Error(w + ".render.taa", "'reset', 'keep' or 'toggle' expected");
                    if (r.taa == "toggle") report.Warn(w + ".render.taa", "'toggle' is an experimental fallback");
                    if (r.jitter < 0 || r.jitter > 15) report.Error(w + ".render.jitter", "jitter pattern ids are 1..15 (0 = auto from samples)");
                    if (r.warmup + r.samples > 64) report.Warn(w + ".render", (r.warmup + r.samples) + " renders per visit is very slow");
                }
            }
            if (spec.cameras.Count == 0) report.Error("cameras", "no cameras");
            else if (enabled == 0) report.Error("cameras", "no enabled camera");
            int budget = -1; bool unequal = false;
            foreach (var c in spec.cameras)
            {
                if (!c.enabled || c.render == null) continue;
                int b = Math.Max(1, c.render.warmup) + Math.Max(1, c.render.samples);
                if (budget < 0) budget = b; else if (b != budget) unequal = true;
            }
            bool gameLook = replayLint && (spec.replay == null || spec.replay.play == null || spec.replay.play.look == "game");
            if (unequal && !gameLook) report.Warn("render", "render budgets differ between cameras: frozen frames run Update logic once per render (PlayMaker, Actor), so cameras see slightly different frozen-frame counts");
            var o = spec.output;
            if (o.format != "jpg" && o.format != "raw" && o.format != "pipe") report.Error("output.format", "'jpg', 'raw' or 'pipe' expected");
            if (o.jpeg < 1 || o.jpeg > 100) report.Error("output.jpeg", "1..100 expected");
            if (o.queue < 1) report.Error("output.queue", ">= 1 required");
            if (o.flushEvery < 1) report.Error("output.flushEvery", ">= 1 required");
            if (o.readback != "auto" && o.readback != "native" && o.readback != "readpixels") report.Error("output.readback", "'auto', 'native' or 'readpixels' expected");
            if (o.format == "pipe")
            {
                if (enabled > 8) report.Error("output.format", "pipe mode: " + enabled + " enabled cameras need more NVENC sessions than allowed (8)");
                string pc = o.pipeCodec.ToLowerInvariant();
                if (pc.IndexOf("hevc") < 0 && pc.IndexOf("265") < 0) report.Error("output.pipeCodec", "H.265 only (hevc_nvenc or libx265)");
                if (!File.Exists(MultiCamFrameWriter.ResolveExe(o.ffmpeg))) report.Warn("output.ffmpeg", "not found: " + o.ffmpeg + " (give the full path to ffmpeg.exe or put its folder on PATH)");
            }
            if (o.format == "raw") report.Note("output.format", "raw frames are about " + (ScreenPixels() * 4 / 1e6).ToString("0.#", IC) + " MB each");
            var ss = spec.session;
            if (ss.realTime != "game" && ss.realTime != "wall") report.Error("session.realTime", "'game' or 'wall' expected");
            if ((ss.res.x > 0) != (ss.res.y > 0) || ss.res.x < 0 || ss.res.y < 0) report.Error("session.res", "[width, height] or [0, 0] expected");
            if (ss.gcSteps < 0) report.Error("session.gcSteps", ">= 0 required");
            var st = spec.stream;
            if (st.keepLoaded != "auto" && st.keepLoaded != "none" && st.keepLoaded != "list") report.Error("stream.keepLoaded", "'auto', 'none' or 'list' expected");
            if (st.keepLoaded == "list" && st.cullers.Count == 0) report.Warn("stream.cullers", "keepLoaded 'list' without cullers");
            if (st.freeze) report.Warn("stream.freeze", "CullingActive = false during the take is experimental");
            LintEvents();
            LintRefs();
            LintSetup();
            for (int i = 0; i < spec.cues.Count; i++)
            {
                var q = spec.cues[i];
                string w = "cues[" + i + "]";
                if (q.Commands().Count == 0) report.Error(w, "no command");
                if (q.at != null && q.at.absolute && q.at.offset > spec.seconds) report.Warn(w + ".at", "after the take end (" + spec.seconds.ToString("0.##", IC) + " s): never runs");
                foreach (var cmd in q.Commands())
                {
                    string c0 = cmd.Trim().Split(' ', '\t')[0].ToLowerInvariant();
                    if (c0 == "mcam") report.Error(w, "mcam commands cannot be cues");
                    else if (c0 == "cam" || c0 == "freecam" || c0 == "zoom" || c0 == "res" || c0 == "fullscreen") report.Error(w, "'" + c0 + "' moves or resizes the capture camera; not allowed as a cue");
                }
            }
            if (spec.until != null && spec.until.absolute && spec.until.offset > spec.seconds) report.Warn("until", "later than seconds; seconds caps the take");
            foreach (var e in spec.events) if (!string.IsNullOrEmpty(e.name) && !referenced.Contains(e.name)) report.Warn("events." + e.name, "never referenced by a time (\"@" + e.name + "\")");
            if (rehearsalDir.Length > 0 && !File.Exists(Path.Combine(rehearsalDir, "events.csv"))) report.Warn("rehearsal", "no events.csv in " + rehearsalDir);
            if (replayLint)
            {
                Json.Obj root = null;
                try { root = Json.Parse(spec.text) as Json.Obj; } catch { root = null; }
                MultiCamLint.Replay(spec, root, report);
            }
        }

        void LintEvents()
        {
            var names = new HashSet<string>();
            for (int i = 0; i < spec.events.Count; i++)
            {
                var e = spec.events[i];
                string w = string.IsNullOrEmpty(e.where) ? "events[" + i + "]" : e.where;
                if (string.IsNullOrEmpty(e.name)) report.Error(w, "event name required");
                else if (!ValidName(e.name, "_.")) report.Error(w + ".name", "'" + e.name + "': only letters, digits, '_' and '.'");
                else if (!names.Add(e.name)) report.Error(w + ".name", "duplicate event name '" + e.name + "'");
                if (Array.IndexOf(MultiCamConst.EventTypes, e.type) < 0) { report.Error(w + ".type", "unknown event type '" + e.type + "' (" + string.Join(", ", MultiCamConst.EventTypes) + ")"); continue; }
                if (e.edge != "enter" && e.edge != "exit" && e.edge != "event") report.Error(w + ".edge", "'enter', 'exit' or 'event' expected");
                if (e.op.Length > 0 && Array.IndexOf(Ops, e.op) < 0) report.Error(w + ".op", "one of " + string.Join(" ", Ops));
                if (e.count < 1) report.Error(w + ".count", ">= 1 required");
                if (e.cooldown < 0) report.Error(w + ".cooldown", ">= 0 required");
                switch (e.type)
                {
                    case "time": if (!e.Has("value")) report.Warn(w + ".value", "time event without value fires at t = 0"); break;
                    case "signal":
                    case "signalIn":
                        if (e.obj.Length == 0) report.Error(w + ".obj", "required (" + (e.type == "signal" ? "sender" : "receiver") + " object path)");
                        if (e.signal.Length == 0) report.Error(w + ".signal", "required (" + (e.type == "signal" ? "SignalOut debug name" : "SignalIn name, FSM inputs start with '>'") + ")");
                        break;
                    case "fsm":
                        if (e.obj.Length == 0) report.Error(w + ".obj", "required");
                        if (e.edge == "event" && e.fsmEvent.Length == 0) report.Error(w + ".fsmEvent", "required for edge 'event'");
                        if (e.edge != "event" && e.state.Length == 0) report.Error(w + ".state", "required for edge 'enter' / 'exit'");
                        break;
                    case "sound": if (e.sound.Length == 0) report.Error(w + ".sound", "required"); break;
                    case "probe": if (e.obj.Length == 0) report.Error(w + ".obj", "required (object with IsTriggeredByProbe)"); break;
                    case "impact": if (e.target == null) report.Error(w + ".target", "required"); break;
                    case "box":
                        if (e.target == null) report.Error(w + ".target", "required");
                        if (e.size == Vector3.zero) report.Error(w + ".size", "required");
                        break;
                    case "cond":
                        if (e.target == null) report.Error(w + ".target", "required");
                        if (Array.IndexOf(CondProps, e.prop) < 0) report.Error(w + ".prop", "one of " + string.Join(" ", CondProps));
                        if (e.op.Length == 0) report.Error(w + ".op", "required");
                        if (e.prop == "dist" && e.to == null) report.Error(w + ".to", "required for prop 'dist'");
                        break;
                    case "field":
                        if (e.obj.Length == 0) report.Error(w + ".obj", "required");
                        if (e.comp.Length == 0) report.Error(w + ".comp", "required");
                        if (e.field.Length == 0) report.Error(w + ".field", "required");
                        if (e.op.Length == 0 && e.match.Length == 0) report.Error(w, "'op' + 'value' or 'match' required");
                        break;
                    case "boystate":
                    case "visual": if (e.match.Length == 0) report.Error(w + ".match", "required (class name)"); break;
                }
            }
        }

        void LintRefs()
        {
            var sp = spec;
            SpecBind.Walk(spec, "", (node, where) =>
            {
                var tr = node as TimeRef;
                if (tr == null || tr.absolute) return;
                referenced.Add(tr.ev);
                if (sp.FindEvent(tr.ev) == null) report.Error(where, "unknown event '" + tr.ev + "' in \"" + tr + "\"");
            });
        }

        void LintSetup()
        {
            for (int i = 0; i < spec.setup.Count; i++)
            {
                string line = spec.setup[i].Trim();
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;
                string c = p[0].ToLowerInvariant();
                string w = "setup[" + i + "]";
                if (c == "wait") { double s; if (p.Length < 2 || !double.TryParse(p[1], NumberStyles.Float, IC, out s) || s < 0) report.Error(w, "wait <real seconds>"); }
                else if (c == "waitfor")
                {
                    string k = p.Length > 1 ? p[1].ToLowerInvariant() : "";
                    if (k != "loaded" && k != "streaming" && k != "savepoint" && k != "event") report.Error(w, "waitfor loaded | streaming | savepoint <name> | event <name>");
                    if (k == "event")
                    {
                        if (p.Length < 3 || spec.FindEvent(p[2]) == null) report.Error(w, "waitfor event: unknown event");
                        else referenced.Add(p[2]);
                    }
                }
                else if (c == "mcam") report.Error(w, "mcam commands cannot be setup commands");
            }
        }

        static bool ValidName(string n, string extra)
        {
            foreach (char ch in n) if (!(ch < 128 && (char.IsLetterOrDigit(ch) || extra.IndexOf(ch) >= 0))) return false;
            return n.Length > 0;
        }

        public Dictionary<string, double> Rehearsal()
        {
            if (rehearsal != null) return rehearsal;
            rehearsal = new Dictionary<string, double>();
            if (rehearsalDir.Length == 0) return rehearsal;
            try
            {
                string f = Path.Combine(rehearsalDir, "events.csv");
                if (!File.Exists(f)) return rehearsal;
                var lines = File.ReadAllLines(f);
                if (lines.Length < 2) return rehearsal;
                var head = MultiCamCsv.Split(lines[0]);
                int it = head.IndexOf("game_time"), ik = head.IndexOf("kind"), iname = head.IndexOf("name");
                if (it < 0 || ik < 0 || iname < 0) return rehearsal;
                for (int i = 1; i < lines.Length; i++)
                {
                    var c = MultiCamCsv.Split(lines[i]);
                    if (c.Count <= Math.Max(it, Math.Max(ik, iname)) || c[ik] != "event" || rehearsal.ContainsKey(c[iname])) continue;
                    double v;
                    if (double.TryParse(c[it], NumberStyles.Float, IC, out v)) rehearsal[c[iname]] = v;
                }
            }
            catch (Exception e) { report.Warn("rehearsal", "events.csv unreadable: " + e.Message); }
            return rehearsal;
        }

        public double Predict(TimeRef r)
        {
            if (r == null) return 0;
            if (r.absolute) return r.offset;
            var e = spec.FindEvent(r.ev);
            if (e == null) return double.NaN;
            if (!double.IsNaN(e.expect)) return e.expect + r.offset;
            double v;
            if (Rehearsal().TryGetValue(r.ev, out v)) return v + r.offset;
            return double.NaN;
        }

        void Estimate()
        {
            double end = spec.seconds;
            if (spec.until != null) { double u = Predict(spec.until); if (!double.IsNaN(u) && u < end) end = u; }
            if (end < 0) end = 0;
            estGameSeconds = end;
            var starts = new List<double>();
            for (int i = 0; i < segs.Count; i++) starts.Add(i == 0 ? 0 : Predict(segs[i].spec.from));
            var bounds = new List<double> { 0, end };
            foreach (var s in starts) if (!double.IsNaN(s) && s > 0 && s < end) bounds.Add(s);
            bounds.Sort();
            if (replayLint) { EstimateReplay(starts, bounds); return; }
            int perStep = 0, cams = 0;
            foreach (var c in spec.cameras)
            {
                if (!c.enabled || c.render == null) continue;
                cams++;
                perStep += Math.Max(1, c.render.warmup) + Math.Max(1, c.render.samples);
            }
            estSteps = 0; estCaptured = 0;
            for (int b = 0; b + 1 < bounds.Count; b++)
            {
                double a0 = bounds[b], a1 = bounds[b + 1];
                if (a1 <= a0) continue;
                int act = 0;
                for (int i = 0; i < segs.Count; i++) if (i == 0 || (!double.IsNaN(starts[i]) && starts[i] <= a0 + 1e-9)) act = i;
                var g = segs[act];
                estSteps += (a1 - a0) * g.rate;
                estCaptured += (a1 - a0) * g.rate * g.captured / Math.Max(1, g.steps);
            }
            estRenders = estSteps + estCaptured * perStep;
            estMinutes = estRenders * 0.032 / 60.0;
            double px = ScreenPixels();
            double frameBytes = spec.output.format == "raw" ? px * 4 : spec.output.format == "pipe" ? px * 0.05 : 1.0e6 * px / (2560.0 * 1440.0);
            estGB = estCaptured * cams * frameBytes / 1e9;
            report.Note("estimate", estSteps.ToString("0", IC) + " steps, " + estCaptured.ToString("0", IC) + " captured, " + cams + " camera(s) x " + (cams > 0 ? (perStep / cams).ToString(IC) : "0") +
                        " renders -> ~" + estRenders.ToString("0", IC) + " renders, ~" + estMinutes.ToString("0.#", IC) + " min at ~32 ms per render (1440p, vsync off), ~" + estGB.ToString("0.##", IC) + " GB");
        }

        void EstimateReplay(List<double> starts, List<double> bounds)
        {
            var play = spec.replay != null && spec.replay.play != null ? spec.replay.play : new PlaySpec();
            int cams = 0;
            foreach (var c in spec.cameras) if (c.enabled) cams++;
            double frames = 0, subRenders = 0;
            for (int b = 0; b + 1 < bounds.Count; b++)
            {
                double a0 = bounds[b], a1 = bounds[b + 1];
                if (a1 <= a0) continue;
                int act = 0;
                for (int i = 0; i < segs.Count; i++) if (i == 0 || (!double.IsNaN(starts[i]) && starts[i] <= a0 + 1e-9)) act = i;
                var g = segs[act];
                double f = (a1 - a0) / Math.Max(1e-6, g.speed) * spec.timing.fps;
                frames += f;
                subRenders += f * Math.Max(1, g.captured);
            }
            int warm = Math.Max(0, play.warmup);
            double perSub = 1;
            if (play.look == "clean")
            {
                double sum = 0; int n = 0;
                foreach (var c in spec.cameras) if (c.enabled && c.render != null) { sum += Math.Max(1, c.render.warmup) + Math.Max(1, c.render.samples); n++; }
                perSub = n > 0 ? sum / n : 1;
            }
            estSteps = frames;
            estCaptured = frames;
            estRenders = cams * (subRenders * perSub + warm);
            estMinutes = estRenders * 0.05 / 60.0 + (estGameSeconds + spec.preroll) / 60.0;
            double px = ScreenPixels();
            double frameBytes = spec.output.format == "raw" ? px * 4 : spec.output.format == "pipe" ? px * 0.05 : 1.0e6 * px / (2560.0 * 1440.0);
            estGB = estCaptured * cams * frameBytes / 1e9;
            report.Note("estimate", "replay: " + estGameSeconds.ToString("0.##", IC) + " record s, " + frames.ToString("0", IC) + " output frames per camera, " + cams + " camera(s) -> ~" + estRenders.ToString("0", IC) +
                        " renders (look " + play.look + ", warmup " + warm + " per pass), ~" + estMinutes.ToString("0.#", IC) + " min incl. the record pass at ~50 ms per render (1440p), ~" + estGB.ToString("0.##", IC) + " GB; director runs add warmups");
        }

        public void ResolveTargets(bool passive)
        {
            targetLines.Clear();
            var rep = report;
            bool begun = false, end = true;
            var run = passive ? MultiCam.runner : null;
            if (run != null && run.TargetsBegun)
            {
                if (!run.TargetsLive) { rep.Note("targets", "in-game target report skipped: the running take is resolving its own targets right now (try again after its first step)"); return; }
                end = false;
            }
            try
            {
                MultiCamTargets.Begin(spec, rep);
                begun = end;
                var refs = new List<KeyValuePair<string, TargetRef>>();
                SpecBind.Walk(spec, "", (node, where) =>
                {
                    var r = node as TargetRef;
                    if (r == null || r.isNone || r.isSelf) return;
                    if (where.IndexOf(".group.") >= 0 || where.IndexOf(".filter.") >= 0) return;
                    refs.Add(new KeyValuePair<string, TargetRef>(where, r));
                });
                foreach (var kv in refs)
                {
                    string d;
                    try { d = MultiCamTargets.Describe(kv.Value); } catch (Exception e) { d = kv.Value.text + " -> describe failed: " + e.Message; }
                    targetLines.Add(kv.Key + ": " + d);
                }
                foreach (var e in spec.events)
                {
                    if (e.obj.Length == 0) continue;
                    var go = MultiCamTargets.ResolveObject(e.obj, "events." + e.name + ".obj", rep);
                    targetLines.Add("events." + e.name + ".obj: " + e.obj + " -> " + (go != null ? Inspector.PathOf(go.transform) + " #" + go.GetInstanceID() + (go.activeInHierarchy ? " active" : " inactive") : "NOT RESOLVED"));
                }
                foreach (var c in spec.cameras)
                    foreach (var kind in new[] { "hide", "backfaces" })
                        foreach (var h in kind == "hide" ? c.hide : c.backfaces)
                        {
                            string path, boxError;
                            List<Vector3> min, max;
                            bool box = MultiCamHide.SplitBox(h, out path, out min, out max, out boxError);
                            if (!box && boxError.Length > 0)
                            {
                                rep.Error(c.where + "." + kind, boxError + ": " + h);
                                targetLines.Add(c.name + "." + kind + ": " + h + " -> BAD BOX (" + boxError + ")");
                                continue;
                            }
                            var go = MultiCamTargets.ResolveObject(path, c.where + "." + kind, rep);
                            targetLines.Add(c.name + "." + kind + ": " + h + " -> " + (go != null ? Inspector.PathOf(go.transform) + (box ? " box " + MultiCamHide.BoxText(min, max) : "") : "NOT RESOLVED"));
                        }
            }
            catch (Exception e) { rep.Error("targets", "target report failed: " + e.GetType().Name + ": " + e.Message); }
            finally { if (begun) { try { MultiCamTargets.End(); } catch { } } }
        }

        public string ReportText()
        {
            var sb = new StringBuilder();
            sb.Append("mcam check ").Append(path).Append(": ").Append(spec != null && report.ok ? "OK" : "FAILED").Append('\n');
            if (spec != null)
            {
                sb.Append("spec ").Append(legacy ? "v1 (legacy mapping)" : "v" + spec.version).Append("  name '").Append(spec.name).Append("'  seconds ").Append(spec.seconds.ToString("0.##", IC));
                if (spec.until != null) sb.Append("  until ").Append(spec.until);
                if (spec.preroll > 0) sb.Append("  preroll ").Append(spec.preroll.ToString("0.##", IC));
                if (spec.seed >= 0) sb.Append("  seed ").Append(spec.seed);
                sb.Append("  fps ").Append(spec.timing.fps.ToString("0.##", IC)).Append("  physics ").Append(spec.timing.physics).Append("  output ").Append(spec.output.format).Append('\n');
                if (replayLint) sb.Append(MultiCamLint.ReplayLine(spec)).Append('\n');
                sb.Append("segments:\n");
                foreach (var g in segs)
                {
                    sb.Append("  #").Append(g.index).Append(" from ").Append(g.spec.from != null ? g.spec.from.ToString() : "0");
                    double p = g.index == 0 ? 0 : Predict(g.spec.from);
                    if (g.spec.from != null && !g.spec.from.absolute) sb.Append(" (~").Append(double.IsNaN(p) ? "?" : p.ToString("0.##", IC)).Append(')');
                    sb.Append("  ").Append(replayLint ? "speed " + g.speed.ToString("0.####", IC) + " M " + g.captured : g.Describe()).Append(g.label.Length > 0 ? "  '" + g.label + "'" : "").Append('\n');
                }
                sb.Append("cameras (").Append(spec.cameras.Count).Append("):\n");
                foreach (var c in spec.cameras)
                {
                    sb.Append("  ").Append(c.name).Append("  ").Append(c.mode).Append(c.enabled ? "" : "  [disabled]");
                    if (c.render != null) sb.Append("  W").Append(c.render.warmup).Append("+N").Append(c.render.samples).Append(' ').Append(c.render.taa);
                    if (c.post != null && c.post.given.Count > 0) sb.Append("  post ").Append(string.Join(",", c.post.given.ToArray()));
                    sb.Append('\n');
                }
                if (spec.events.Count > 0)
                {
                    sb.Append("events:\n");
                    foreach (var e in spec.events)
                    {
                        sb.Append("  ").Append(e.name).Append("  ").Append(e.type);
                        if (e.obj.Length > 0) sb.Append("  ").Append(e.obj);
                        if (e.signal.Length > 0) sb.Append(" .").Append(e.signal);
                        if (e.target != null) sb.Append("  target ").Append(e.target.text);
                        double pr = Predict(new TimeRef(e.name, 0));
                        if (!double.IsNaN(pr)) sb.Append("  ~").Append(pr.ToString("0.##", IC)).Append(" s");
                        sb.Append('\n');
                    }
                }
                if (targetLines.Count > 0)
                {
                    sb.Append("targets (resolved in the current scene):\n");
                    for (int i = 0; i < targetLines.Count && i < 60; i++) sb.Append("  ").Append(targetLines[i]).Append('\n');
                    if (targetLines.Count > 60) sb.Append("  ... ").Append(targetLines.Count - 60).Append(" more\n");
                }
            }
            sb.Append(report.Summary(80));
            return sb.ToString();
        }

        public string ReportJson()
        {
            var o = new Json.Obj();
            o["path"] = path;
            o["ok"] = spec != null && report.ok;
            o["errors"] = Strs(report.errors);
            o["warnings"] = Strs(report.warnings);
            o["notes"] = Strs(report.notes);
            o["replay_lint"] = replayLint;
            if (spec != null)
            {
                o["legacy"] = legacy;
                o["name"] = spec.name;
                o["seconds"] = spec.seconds;
                o["fps"] = (double)spec.timing.fps;
                var sl = new List<object>();
                foreach (var g in segs)
                {
                    var s = new Json.Obj();
                    s["index"] = (double)g.index; s["from"] = g.spec.from != null ? g.spec.from.ToString() : "0";
                    s["from_predicted"] = Num(g.index == 0 ? 0 : Predict(g.spec.from));
                    s["rate"] = (double)g.rate; s["steps_per_frame"] = (double)g.steps; s["captured_per_frame"] = (double)g.captured; s["speed"] = (double)g.speed; s["label"] = g.label;
                    sl.Add(s);
                }
                o["segments"] = sl;
                var cl = new List<object>();
                foreach (var c in spec.cameras)
                {
                    var s = new Json.Obj();
                    s["name"] = c.name; s["mode"] = c.mode; s["enabled"] = c.enabled;
                    if (c.render != null) { s["warmup"] = (double)c.render.warmup; s["samples"] = (double)c.render.samples; s["taa"] = c.render.taa; }
                    cl.Add(s);
                }
                o["cameras"] = cl;
                o["targets"] = Strs(targetLines);
                var est = new Json.Obj();
                est["game_seconds"] = estGameSeconds; est["steps"] = Math.Round(estSteps); est["captured_steps"] = Math.Round(estCaptured);
                est["renders"] = Math.Round(estRenders); est["minutes"] = Math.Round(estMinutes, 1); est["gigabytes"] = Math.Round(estGB, 2);
                o["estimate"] = est;
            }
            return Json.Write(o);
        }

        static double ScreenPixels()
        {
            try { return LivePixels(); }
            catch { return 2560.0 * 1440.0; }
        }

        static double LivePixels() { return Math.Max(1, Screen.width) * (double)Math.Max(1, Screen.height); }

        internal static List<object> Strs(IEnumerable<string> l) { var r = new List<object>(); foreach (var s in l) r.Add(s); return r; }
        internal static object Num(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? null : (object)v; }
    }

    // ------------------------------------------------------------------ replay lint and command arguments (REPLAY_DESIGN 3.2, 3.4)

    // Errors and warnings of the replay lint of 3.2 on top of the v2 lint (the v2 whole-R and rate-range errors are skipped
    // by MultiCamPlan when replayLint is set), the player-side 65 deg rule for replay cameras, and the argument parsing of
    // "mcam record|replay|take|passes|golden". Reads spec types only, so it builds for the offline .NET test as well.
    internal static class MultiCamLint
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public const float SideAngle = 65f, TopDownPitch = 60f;
        public const int MaxRecordW = 1920, MaxRecordH = 1080, CostlySamples = 8;
        public const float BlurClamp = 2f;
        const string R = "replay.", RR = "replay.record.", RP = "replay.play.", RA = "replay.audio.";

        public static string Usage(string verb)
        {
            switch (verb)
            {
                case ReplayModes.Record: return "mcam record <spec.json> <out dir>";
                case ReplayModes.Golden: return "mcam golden <spec.json> <out dir> [every]";
                default: return "mcam " + verb + " <spec.json> <out dir> [cams=a,b] [range=t0:t1]";
            }
        }

        public static void Replay(MultiCamSpec spec, Json.Obj root, SpecReport report)
        {
            if (spec == null || report == null) return;
            ReplayConst.CheckRemoved(root, report);
            var rs = spec.replay ?? new ReplaySpec();
            var rec = rs.record ?? new RecordSpec();
            var play = rs.play ?? new PlaySpec();
            var au = rs.audio ?? new AudioCapSpec();
            if (!spec.Has("version") || spec.version < 2) report.Warn("version", "replay specs are version 2 (the v1 legacy defaults apply)");
            Timing(spec, play, report);
            Record(rec, report);
            Play(spec, play, report);
            Audio(rec, au, report);
            V2Fields(spec, play, report);
            Sides(spec, play, report);
        }

        static void OneOf(string where, string value, string[] allowed, SpecReport report)
        {
            if (Array.IndexOf(allowed, value) < 0) report.Error(where, "unknown value '" + value + "' (" + string.Join(", ", allowed) + ")");
        }

        static void Timing(MultiCamSpec spec, PlaySpec play, SpecReport report)
        {
            if (spec.rates.Count > 0) report.Error("rates", "legacy rates are not allowed in a replay spec: use timing.segments with speed (any float speed works, no whole step rate)");
            var segs = spec.timing.segments;
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                if (s == null) continue;
                string w = "timing.segments[" + i + "]";
                if (s.Has("rate") && !(s.where ?? "").StartsWith("rates[", StringComparison.Ordinal)) report.Error(w + ".rate", "not allowed in a replay spec: give speed instead (any float speed works)");
                if (s.samples > CostlySamples) report.Warn(w + ".samples", s.samples + " sub-frames per output frame: " + s.samples + " renders per frame and camera (cost)");
            }
            if (spec.Has("settle")) report.Error("settle", "v1 key, not allowed in a replay spec (replay.play.warmup sets the pass warmup)");
            if (spec.timing.physics == "fine") report.Error("timing.physics", "'fine' is not allowed in a replay spec: the record pass runs game physics");
        }

        static void Record(RecordSpec rec, SpecReport report)
        {
            OneOf(RR + "clock", rec.clock, ReplayConst.Clocks, report);
            if (rec.clock == "fixed" && Array.IndexOf(ReplayConst.FixedRates, rec.rate) < 0) report.Error(RR + "rate", "clock 'fixed' needs rate 60, 120 or 240 (got " + rec.rate + ")");
            if (rec.clock == "real" && rec.Has("rate")) report.Note(RR + "rate", "ignored with clock 'real' (one sample per rendered frame at 60 Hz)");
            var res = rec.res;
            if ((res.x > 0) != (res.y > 0) || res.x < 0 || res.y < 0) report.Error(RR + "res", "[width, height] or [0, 0] expected");
            else if (res.x > MaxRecordW || res.y > MaxRecordH) report.Warn(RR + "res", Fmt(res.x) + "x" + Fmt(res.y) + " is above " + MaxRecordW + "x" + MaxRecordH + ": the record pass may fall below real time");
            if (rec.roots == null || rec.roots.Count == 0) report.Error(RR + "roots", "at least one root (default [\"auto\"])");
            OneOf(RR + "particles", rec.particles, ReplayConst.RecordParticles, report);
            if (rec.particles == "list" && (rec.particleSystems == null || rec.particleSystems.Count == 0)) report.Warn(RR + "particleSystems", "particles 'list' without particleSystems: no particles recorded");
            OneOf(RR + "forceCullers", rec.forceCullers, ReplayConst.ForceCullers, report);
            if (rec.forceCullers == "list" && (rec.cullers == null || rec.cullers.Count == 0)) report.Warn(RR + "cullers", "forceCullers 'list' without cullers: no culler forced");
            if (!(rec.posEps > 0f)) report.Error(RR + "posEps", "must be > 0");
            if (!(rec.rotEps > 0f)) report.Error(RR + "rotEps", "must be > 0");
            if (!(rec.cut > 0f)) report.Error(RR + "cut", "must be > 0");
            if (rec.rescanEvery < 1) report.Error(RR + "rescanEvery", ">= 1 required");
            if (rec.captureEvery < 1) report.Error(RR + "captureEvery", ">= 1 required");
            if (rec.box != null && (rec.box.size.x <= 0f || rec.box.size.y <= 0f || rec.box.size.z <= 0f)) report.Error(RR + "box.size", "every size component must be > 0");
            if (rec.settleFrames < 0) report.Error(RR + "settleFrames", ">= 0 required");
            else if (rec.settleFrames > 600) report.Warn(RR + "settleFrames", rec.settleFrames + " frames is a long wait before sample 0");
            if (rec.blocks != null)
                for (int i = 0; i < rec.blocks.Count; i++)
                    if (ShaderMirror.BlockEntry(rec.blocks[i]) == null) report.Error(RR + "blocks[" + i + "]", "'" + rec.blocks[i] + "' is not <path>|<property>[:float],...");
            if (rec.checks != null)
                for (int i = 0; i < rec.checks.Count; i++)
                {
                    string c = rec.checks[i] ?? "";
                    int k = c.IndexOf(" @", StringComparison.Ordinal);
                    if (c.Trim().Length == 0) report.Error(RR + "checks[" + i + "]", "empty check");
                    else if (k >= 0 && c.IndexOf("..", k, StringComparison.Ordinal) < 0) report.Error(RR + "checks[" + i + "]", "the window is ' @<from>..<to>' (seconds or event[+-offset])");
                }
        }

        static void Play(MultiCamSpec spec, PlaySpec play, SpecReport report)
        {
            OneOf(RP + "look", play.look, ReplayConst.Looks, report);
            OneOf(RP + "order", play.order, ReplayConst.Orders, report);
            OneOf(RP + "interp", play.interp, ReplayConst.Interps, report);
            OneOf(RP + "particles", play.particles, ReplayConst.PlayParticles, report);
            OneOf(RP + "cullers", play.cullers, ReplayConst.Cullers, report);
            OneOf(RP + "shadows", play.shadows, ReplayConst.Shadows, report);
            OneOf(RP + "activate", play.activate, ReplayConst.Activates, report);
            OneOf(RP + "realTime", play.realTime, ReplayConst.RealTimes, report);
            OneOf(RP + "restore", play.restore, ReplayConst.Restores, report);
            string mb = play.motionBlur ?? "";
            if (Array.IndexOf(ReplayConst.MotionBlurModes, mb) < 0)
            {
                double v;
                if (!double.TryParse(mb, NumberStyles.Float, IC, out v) || double.IsNaN(v) || double.IsInfinity(v)) report.Error(RP + "motionBlur", "'game', 'angle', 'off' or a strength number expected (got '" + mb + "')");
                else if (v < 0) report.Error(RP + "motionBlur", "strength must be >= 0");
                else if (v > BlurClamp) report.Warn(RP + "motionBlur", "strength " + v.ToString("0.###", IC) + " > " + Fmt(BlurClamp) + ": the shader clamp is unknown (R6)");
            }
            else if (mb == "game")
            {
                foreach (var g in spec.timing.segments)
                {
                    if (g == null || !(g.speed > 0f) || g.samples > 1) continue;
                    double s = 0.8 * (1.0 / 60.0) * spec.timing.fps / g.speed;
                    if (s > BlurClamp + 1e-6) { report.Warn(RP + "motionBlur", "'game' gives strength " + s.ToString("0.##", IC) + " at speed " + g.speed.ToString("0.###", IC) + " (above " + Fmt(BlurClamp) + ": the shader clamp is unknown, R6)"); break; }
                }
            }
            if (!(play.shutterAngle > 0f) || play.shutterAngle > 360f) report.Error(RP + "shutterAngle", "must be in (0, 360] degrees");
            if (play.warmup < 0) report.Error(RP + "warmup", ">= 0 required");
            else if (play.warmup == 0) report.Warn(RP + "warmup", "0: no TAA reset at pass start, the previous pass's history leaks into the first frames");
            if (play.ramp < 0f) report.Error(RP + "ramp", ">= 0 required");
            if (play.maxUnresolved < 0f || play.maxUnresolved > 1f) report.Error(RP + "maxUnresolved", "fraction in [0, 1] expected");
            if (play.activateList != null)
                foreach (var kv in play.activateList)
                {
                    var v = kv.Value as string;
                    if (v == null || Array.IndexOf(ReplayConst.ActivateListModes, v) < 0) report.Error(RP + "activateList." + kv.Key, "'quiet' or 'normal' expected");
                }
            if (play.Has("range"))
            {
                double a = play.range.x, b = play.range.y;
                if (a < 0 || b > spec.seconds + 1e-6 || b <= a) report.Error(RP + "range", "[from, to] with 0 <= from < to <= seconds (" + spec.seconds.ToString("0.##", IC) + ") expected");
            }
            for (int i = 0; play.cams != null && i < play.cams.Count; i++)
            {
                var c = spec.FindCamera(play.cams[i]);
                if (c == null) report.Error(RP + "cams[" + i + "]", "unknown camera '" + play.cams[i] + "'");
                else if (!c.enabled) report.Warn(RP + "cams[" + i + "]", "camera '" + play.cams[i] + "' is disabled");
            }
            for (int i = 0; play.coverageOk != null && i < play.coverageOk.Count; i++)
                if (spec.FindCamera(play.coverageOk[i]) == null) report.Warn(RP + "coverageOk[" + i + "]", "unknown camera '" + play.coverageOk[i] + "'");
            if (play.cullers == "live") report.Warn(RP + "cullers", "'live' is a diagnostic: cullers SetActive per camera and contaminate the passes (R4, R10 fail)");
            if (play.cullers == "recorded")
                foreach (var c in spec.cameras)
                    if (c.enabled && c.mode != "game") { report.Warn(RP + "cullers", "'recorded' applies the game camera's culling to every camera: groups the game camera culled stay hidden in '" + c.name + "' (use it for the golden test)"); break; }
            if (play.look == "clean")
                foreach (var c in spec.cameras)
                    if (c.enabled && c.render != null && c.render.samples > CostlySamples) { report.Warn((string.IsNullOrEmpty(c.where) ? "cameras." + c.name : c.where) + ".render.samples", "look 'clean' with " + c.render.samples + " TAA samples per sub-frame (cost: warmup + samples renders per sub-frame)"); break; }
            if (play.setup != null) Setup(play.setup, report);
        }

        static void Setup(List<string> setup, SpecReport report)
        {
            for (int i = 0; i < setup.Count; i++)
            {
                var p = (setup[i] ?? "").Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;
                string c = p[0].ToLowerInvariant(), w = RP + "setup[" + i + "]";
                double s;
                if (c == "mcam") report.Error(w, "mcam commands cannot be setup commands");
                else if (c == "wait" && (p.Length < 2 || !double.TryParse(p[1], NumberStyles.Float, IC, out s) || s < 0)) report.Error(w, "wait <real seconds>");
                else if (c == "waitfor")
                {
                    string k = p.Length > 1 ? p[1].ToLowerInvariant() : "";
                    if (k != "loaded" && k != "streaming" && k != "savepoint") report.Error(w, "waitfor loaded | streaming | savepoint <name> (no events in a replay setup)");
                }
            }
        }

        static void Audio(RecordSpec rec, AudioCapSpec au, SpecReport report)
        {
            OneOf(RA + "capture", au.capture, ReplayConst.AudioCaptures, report);
            OneOf(RA + "suspend", au.suspend, ReplayConst.Suspends, report);
            OneOf(RA + "onHold", au.onHold, ReplayConst.OnHolds, report);
            bool cap = au.capture == "record";
            if (cap && rec.clock == "fixed" && rec.rate != 60) report.Error(RA + "capture", "'record' with clock 'fixed' needs rate 60 (got " + rec.rate + ")");
            if (cap && au.suspend == "off" && !au.allowMuterOnly) report.Error(RA + "capture", "'record' with suspend 'off' needs allowMuterOnly true (and the S2 flag): no guarded capture path otherwise");
            if (cap) report.Warn(RA + "capture", "'record' is refused by AudioCapture.Preflight until a capture path has passed (only with the muted output, acap.output muted: S3b or S2, user consent first; REPLAY_DESIGN 6.0 G3); the default audible output needs no flag");
            if (cap && rec.clock == "fixed") report.Note(RA + "capture", "clock 'fixed' 60: the take's audio is usable only if the drift check passes (6.2)");
            if (au.suspend == "on") report.Warn(RA + "suspend", "'on' is a diagnostic: suspended capture renders at 0.919x real time (L5), the slope check refuses that audio");
            if (au.onHold == "mark") report.Warn(RA + "onHold", "'mark' continues through holds: the wall-time gap goes to audio_sync.csv and is cut by mcam_audio.py track");
            if (au.level < 0f || au.level > 100f) report.Error(RA + "level", "0..100 expected");
            if (au.markerEvery < 1) report.Error(RA + "markerEvery", ">= 1 required");
            if (!(au.maxHeartbeatAge > 0f)) report.Error(RA + "maxHeartbeatAge", "must be > 0");
            if (cap && string.IsNullOrEmpty(au.heartbeat)) report.Error(RA + "heartbeat", "InsideMuter heartbeat file required for a capture");
        }

        static void V2Fields(MultiCamSpec spec, PlaySpec play, SpecReport report)
        {
            if (spec.session.Has("vsync")) report.Note("session.vsync", "ignored in replay modes: the record pass sets its own 60 Hz pacing (2.3.4)");
            if (spec.stream.Has("addDuringTake")) report.Note("stream.addDuringTake", "no effect in replay passes: streaming is frozen (2.4.2)");
            if (spec.interpolate.Count > 0) report.Note("interpolate", "record pass only; replay passes use interpolation None");
            if (!string.IsNullOrEmpty(spec.rehearsal)) report.Note("rehearsal", "ignored in replay passes: event times come from the recording");
            foreach (var e in spec.events) if (e != null && e.Has("expect")) { report.Note("events." + e.name + ".expect", "ignored in replay passes: event times come from the recording"); }
            if (play.look == "game")
                foreach (var c in spec.cameras)
                    if (c.enabled && c.render != null && (c.render.Has("warmup") || c.render.Has("samples") || c.render.Has("taa")))
                    { report.Note(R + "play.look", "'game': one render per step, render warmup/samples/taa of the cameras are not used (only render.motionBlur < 0 keeps the game value)"); break; }
        }

        // ---------------------------------------------------------------- player-side 65 deg rule (3.2)

        static void Sides(MultiCamSpec spec, PlaySpec play, SpecReport report)
        {
            foreach (var c in spec.cameras)
            {
                if (c == null || !c.playerSide) continue;
                string w = !string.IsNullOrEmpty(c.where) ? c.where : "cameras." + c.name;
                bool ok = play.coverageOk != null && play.coverageOk.Contains(c.name);
                switch (c.mode)
                {
                    case "static":
                        if (!HasLook(c)) Side(w, Forward(c.yaw, c.pitch), ok, report);
                        else { Vector3 a; if (FixedLook(c.look.isSelf ? c.target : c.look, c.lookOffset, out a)) Side(w + ".look", a - c.pos, ok, report); }
                        break;
                    case "keys":
                        for (int i = 0; i < c.keys.Count; i++)
                        {
                            var k = c.keys[i];
                            if (k == null) continue;
                            string kw = w + ".keys[" + i + "]";
                            bool keyLook = k.look != null ? !k.look.isNone : HasLook(c);
                            Vector3 a;
                            if (!keyLook) Side(kw, Forward(k.yaw, k.pitch), ok, report);
                            else if (k.look != null ? FixedLook(k.look.isSelf ? c.target : k.look, k.lookOffset, out a) : FixedLook(c.look.isSelf ? c.target : c.look, c.lookOffset, out a)) Side(kw, a - k.pos, ok, report);
                        }
                        break;
                    case "rail":
                        if (!HasLook(c)) Side(w, Forward(c.yaw, c.pitch), ok, report);
                        else
                        {
                            Vector3 a;
                            if (FixedLook(c.look.isSelf ? c.target : c.look, c.lookOffset, out a))
                                for (int i = 0; i < c.path.Count; i++) if (Side(w + ".path[" + i + "]", a - c.path[i], ok, report)) break;
                        }
                        break;
                    case "orbit":
                        if (c.look != null && c.look.isNone) { Side(w, Forward(c.yaw, c.pitch), ok, report); break; }
                        for (int i = 0; i <= 4; i++)
                        {
                            float az = Mathf.Lerp(c.azimuth.x, c.azimuth.y, i / 4f) * Mathf.Deg2Rad;
                            var ring = new Vector3(Mathf.Sin(az) * c.radius, c.height, -Mathf.Cos(az) * c.radius);
                            Vector3 d;
                            if (c.look == null || c.look.isSelf) d = c.lookOffset - ring;
                            else
                            {
                                Vector3 a;
                                if (!FixedLook(c.look, c.lookOffset, out a) || c.target == null || !c.target.fixedPoint) break;
                                d = a - (c.target.pos + c.target.offset + c.offset + ring);
                            }
                            if (Side(w + ".azimuth", d, ok, report)) break;
                        }
                        break;
                }
            }
        }

        static bool HasLook(CamSpec c) { return c.look != null && !c.look.isNone && !(c.look.isSelf && c.target == null); }

        static bool FixedLook(TargetRef r, Vector3 lookOffset, out Vector3 a)
        {
            a = Vector3.zero;
            if (r == null || !r.fixedPoint) return false;
            a = r.pos + r.offset + lookOffset;
            return true;
        }

        public static Vector3 Forward(float yaw, float pitch)
        {
            double y = yaw * Math.PI / 180.0, p = pitch * Math.PI / 180.0;
            return new Vector3((float)(Math.Sin(y) * Math.Cos(p)), (float)-Math.Sin(p), (float)(Math.Cos(y) * Math.Cos(p)));
        }

        public static double AngleFromZ(Vector3 d, out double pitch)
        {
            double len = Math.Sqrt((double)d.x * d.x + (double)d.y * d.y + (double)d.z * d.z);
            pitch = 0;
            if (len < 1e-9) return 0;
            pitch = -Math.Asin(Math.Max(-1.0, Math.Min(1.0, d.y / len))) * 180.0 / Math.PI;
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, d.z / len))) * 180.0 / Math.PI;
        }

        static bool Side(string where, Vector3 d, bool coverageOk, SpecReport report)
        {
            double pitch;
            double ang = AngleFromZ(d, out pitch);
            if (ang <= SideAngle + 1e-6) return false;
            bool top = pitch > TopDownPitch;
            if (top && coverageOk) return false;
            if (top) report.Warn(where, "top-down view " + ang.ToString("0.#", IC) + " deg from +z (pitch " + pitch.ToString("0.#", IC) + "): check the floor coverage with a still, then list the camera in replay.play.coverageOk");
            else report.Warn(where, "forward " + ang.ToString("0.#", IC) + " deg from +z (more than " + Fmt(SideAngle) + "): the side of the set shows (sets have no back side; cullers 'force' makes groups visible but creates no geometry)");
            return true;
        }

        static string Fmt(float v) { return v.ToString("0.##", IC); }

        // ---------------------------------------------------------------- arguments, recording check, start line

        public static string ParseArgs(string verb, List<string> a, int from, MultiCamSpec spec, ReplayArgs args)
        {
            bool filters = verb == ReplayModes.Replay || verb == ReplayModes.Take || verb == ReplayModes.Passes;
            for (int i = from; i < a.Count; i++)
            {
                string s = a[i] ?? "";
                int eq = s.IndexOf('=');
                string k = eq > 0 ? s.Substring(0, eq).ToLowerInvariant() : "", v = eq > 0 ? s.Substring(eq + 1) : s;
                if (filters && k == "cams")
                {
                    foreach (var part in v.Split(','))
                    {
                        string n = part.Trim();
                        if (n.Length == 0) continue;
                        var c = spec.FindCamera(n);
                        if (c == null) return "cams: unknown camera '" + n + "'";
                        if (!c.enabled) return "cams: camera '" + n + "' is disabled";
                        if (!args.cams.Contains(n)) args.cams.Add(n);
                    }
                    if (args.cams.Count == 0) return "cams: no camera named";
                }
                else if (filters && k == "range")
                {
                    var p = v.Split(':');
                    double t0, t1;
                    if (p.Length != 2 || !double.TryParse(p[0], NumberStyles.Float, IC, out t0) || !double.TryParse(p[1], NumberStyles.Float, IC, out t1)) return "range: t0:t1 in record seconds expected (got '" + v + "')";
                    if (t0 < 0 || t1 <= t0 || t1 > spec.seconds + 1e-6) return "range: 0 <= t0 < t1 <= seconds (" + spec.seconds.ToString("0.##", IC) + ") expected";
                    args.hasRange = true; args.rangeFrom = t0; args.rangeTo = t1;
                }
                else if (verb == ReplayModes.Golden && (k == "every" || (eq < 0 && args.every == 0)))
                {
                    int n;
                    if (!int.TryParse(v, NumberStyles.Integer, IC, out n) || n < 1) return "every: whole number >= 1 expected (got '" + v + "')";
                    args.every = n;
                }
                else return "unknown argument '" + s + "' for mcam " + verb;
            }
            return null;
        }

        public static string SourceDir(MultiCamSpec spec, string specDir, string outDir)
        {
            string s = spec.replay != null ? spec.replay.source : "";
            if (string.IsNullOrEmpty(s)) return Path.Combine(outDir, ReplayConst.RecordFolder);
            try { return Path.IsPathRooted(s) ? Path.GetFullPath(s) : Path.GetFullPath(Path.Combine(specDir ?? "", s)); }
            catch { return null; }
        }

        public static string MissingRecording(string verb, MultiCamSpec spec, string specDir, string outDir)
        {
            if (verb != ReplayModes.Replay && verb != ReplayModes.Passes && verb != ReplayModes.Golden) return null;
            string dir = SourceDir(spec, specDir, outDir);
            if (dir == null) return "replay.source: bad path '" + spec.replay.source + "'";
            if (File.Exists(Path.Combine(dir, ReplayConst.RecordFile))) return null;
            return "no recording: " + Path.Combine(dir, ReplayConst.RecordFile) + " does not exist (run 'mcam record <spec> <out>' first, or set replay.source)";
        }

        public static string Describe(string verb, MultiCamPlan plan, ReplayArgs args)
        {
            var spec = plan.spec;
            var sb = new StringBuilder();
            int enabled = 0;
            foreach (var c in spec.cameras) if (c.enabled) enabled++;
            if (verb == ReplayModes.Record) sb.Append("record pass, ").Append(spec.seconds.ToString("0.##", IC)).Append(" game s max");
            else
            {
                sb.Append(args.cams.Count > 0 ? args.cams.Count + " of " + enabled + " camera(s) (" + string.Join(",", args.cams.ToArray()) + ")" : enabled + " camera(s)");
                sb.Append(", ").Append(plan.segs.Count).Append(" segment(s), ").Append(spec.seconds.ToString("0.##", IC)).Append(" game s max");
                if (args.hasRange) sb.Append(", range ").Append(args.rangeFrom.ToString("0.###", IC)).Append(':').Append(args.rangeTo.ToString("0.###", IC));
            }
            if (verb == ReplayModes.Golden) sb.Append(", grab every ").Append(args.every > 0 ? args.every.ToString(IC) : "captureEvery");
            if (verb == ReplayModes.Replay || verb == ReplayModes.Take) sb.Append(", estimate ~").Append(plan.estMinutes.ToString("0.#", IC)).Append(" min");
            return sb.ToString();
        }

        public static string ReplayLine(MultiCamSpec spec)
        {
            var rs = spec.replay ?? new ReplaySpec();
            var rec = rs.record ?? new RecordSpec();
            var play = rs.play ?? new PlaySpec();
            var au = rs.audio ?? new AudioCapSpec();
            var sb = new StringBuilder();
            sb.Append("replay: source ").Append(string.IsNullOrEmpty(rs.source) ? "<out>/record" : rs.source)
              .Append("  record ").Append(rec.clock).Append(rec.clock == "fixed" ? " " + rec.rate : "").Append(' ').Append(Fmt(rec.res.x)).Append('x').Append(Fmt(rec.res.y))
              .Append(" cullers ").Append(rec.forceCullers).Append(rec.captureGame ? " captureGame/" + rec.captureEvery : "")
              .Append("  play ").Append(play.look).Append(' ').Append(play.order).Append(" warmup ").Append(play.warmup).Append(" blur ").Append(play.motionBlur).Append(" cullers ").Append(play.cullers)
              .Append(play.ramp > 0f ? " ramp " + Fmt(play.ramp) : "")
              .Append("  audio ").Append(au.capture).Append(au.capture == "record" ? " suspend " + au.suspend + " level " + Fmt(au.level) : "");
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ busy gate whitelist (MULTICAM_DESIGN 3.6, REPLAY_DESIGN 3.4)

    internal static class MultiCamGate
    {
        public static bool Passes(List<string> a)
        {
            if (a == null || a.Count == 0) return true;
            string c = (a[0] ?? "").ToLowerInvariant();
            if (c == "state" || c == "game_state" || c == "ping" || c == "audio") return true;
            string s = a.Count > 1 ? (a[1] ?? "").ToLowerInvariant() : "status";
            if (c == "srec") return s == "status" || s == "objects" || s == "event";
            if (c == "acap") return a.Count > 1 && s == "status";
            if (c != "mcam") return false;
            return s == "status" || s == "stop" || s == "abort" || s == "check" || s == "list" || s == "pose" || s == "schema" || s == "help";
        }
    }

    // ------------------------------------------------------------------ replay status text (CORE_A fills ReplayProgress)

    internal static class MultiCamProgressText
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static void Line(StringBuilder sb, ReplayProgress r, string state, double seconds)
        {
            string phase = r.phase ?? "", cam = r.camera ?? "", audio = r.audio ?? "";
            switch (phase)
            {
                case "record":
                    sb.Append("recording: t ").Append(r.t.ToString("0.000", IC)).Append('/').Append((r.duration > 0 ? r.duration : seconds).ToString("0.##", IC)).Append(" s, ")
                      .Append(r.samples).Append(" samples, ").Append(r.objects).Append(" objects, ").Append((r.bytes / 1048576.0).ToString("0.0", IC)).Append(" MB, ")
                      .Append(r.fps.ToString("0.0", IC)).Append(" fps, drift ").Append(r.driftMs.ToString("0.0", IC)).Append(" ms, audio ").Append(audio.Length > 0 ? audio : "off");
                    break;
                case "pass":
                    sb.Append("replay pass ").Append(r.pass + 1).Append('/').Append(r.passes).Append(' ').Append(cam.Length > 0 ? cam : "?").Append(" step ").Append(r.stepIndex).Append('/').Append(r.steps);
                    if (r.duration > 0) sb.Append(", T ").Append(r.t.ToString("0.000", IC)).Append('/').Append(r.duration.ToString("0.##", IC)).Append(" s");
                    break;
                case "golden":
                case "noise":
                    sb.Append(phase).Append(' ').Append(cam.Length > 0 ? cam + " " : "").Append("step ").Append(r.stepIndex).Append('/').Append(r.steps);
                    if (r.duration > 0) sb.Append(", T ").Append(r.t.ToString("0.000", IC)).Append('/').Append(r.duration.ToString("0.##", IC)).Append(" s");
                    break;
                default:
                    sb.Append(state ?? "");
                    if (phase.Length > 0 && (state ?? "").IndexOf(phase, StringComparison.Ordinal) < 0) sb.Append(" (").Append(phase).Append(')');
                    break;
            }
            if (!double.IsNaN(r.eta) && !double.IsInfinity(r.eta)) sb.Append(", eta ").Append((r.eta / 60.0).ToString("0.0", IC)).Append(" min");
        }

        public static Json.Obj ToJson(ReplayProgress r)
        {
            var o = new Json.Obj();
            o["phase"] = r.phase ?? ""; o["t"] = Num(r.t); o["duration"] = Num(r.duration);
            o["samples"] = (double)r.samples; o["objects"] = (double)r.objects; o["bytes"] = (double)r.bytes; o["mb"] = Math.Round(r.bytes / 1048576.0, 2);
            o["fps"] = Num(r.fps); o["drift_ms"] = Num(r.driftMs); o["audio"] = r.audio ?? "";
            o["pass"] = (double)r.pass; o["passes"] = (double)r.passes; o["camera"] = r.camera ?? "";
            o["step_index"] = (double)r.stepIndex; o["steps"] = (double)r.steps; o["eta_s"] = Num(r.eta); o["summary"] = r.summary ?? "";
            return o;
        }

        static object Num(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? null : (object)v; }
    }

    // ------------------------------------------------------------------ replay abort path (REPLAY_DESIGN 3.4, 6.0 G4)

    // Safety net around MultiCamRunner.ReplayFinish: the rtpc goes to 0 (GetRTPCValue readback, one retry) before the
    // capture stops, and whatever the replay run left behind (replayer, recorder with its scene.srec writer) is ended.
    internal static class MultiCamSafety
    {
        public static bool AudioOff(string why, Action<string> log)
        {
            if (!AudioCapture.capturing) return false;
            bool ok = false;
            for (int i = 0; i < 2 && !ok; i++)
            {
                try { ok = AudioCapture.ForceLevel(0f); }
                catch (Exception e) { DevLog.Error("mcam audio level 0", e); }
            }
            Say(log, ok ? "audio: rtpc forced to 0, readback ok" : "error: audio: rtpc 0 readback mismatch after a retry (" + why + ")");
            try { AudioCapture.Stop(why); } catch (Exception e) { DevLog.Error("mcam audio stop", e); }
            Say(log, "audio: capture stopped (" + why + ")");
            return true;
        }

        public static void EndLeftovers(string why, Action<string> log)
        {
            AudioOff(why, log);
            var rp = SceneReplayer.current;
            if (rp != null)
            {
                var rep = new SpecReport();
                try { rp.End(rep); Say(log, "replay ended by the abort path: " + rep.Summary(5).Replace('\n', ' ')); }
                catch (Exception e) { DevLog.Error("mcam replay end", e); Say(log, "error: replay end failed: " + e.GetType().Name + ": " + e.Message); }
            }
            var rc = SceneRecorder.active;
            if (rc != null)
            {
                var rep = new SpecReport();
                try { rc.End(rep); Say(log, "recording closed by the abort path: " + rep.Summary(5).Replace('\n', ' ')); }
                catch (Exception e) { DevLog.Error("mcam recorder end", e); Say(log, "error: recorder end failed: " + e.GetType().Name + ": " + e.Message); }
            }
        }

        static void Say(Action<string> log, string m)
        {
            if (log != null) { try { log(m); return; } catch { } }
            DevLog.Write("[mcam] " + m);
        }
    }

    // ------------------------------------------------------------------ the capture runner

    public sealed partial class MultiCamRunner : MonoBehaviour
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        MultiCamPlan plan;
        MultiCamSpec spec;
        string mode = "capture", outDir = "", started = "", endReason = "";
        double previewAtT = double.NaN;
        bool rehearse, preview, previewNow;
        DevCore core;
        readonly MultiCamContext ctx = new MultiCamContext();
        Camera cam;
        float gameNear = MultiCamConst.GameNear;
        ICamRig[] rigs = new ICamRig[0];
        CamPose[] poses = new CamPose[0];
        bool[] evaluated = new bool[0], visited = new bool[0];
        object[] frameThisStep = new object[0];
        string[] lastCopy = new string[0], camDirs = new string[0];
        List<Renderer>[] hides = new List<Renderer>[0];
        MultiCamEvents events;
        MultiCamCsv meta, posesCsv, eventsCsv, tracksCsv, rigCsv;
        StreamWriter logW;
        MultiCamFrameWriter[] writers;
        MultiCamGrab grab;
        List<TargetRef> tracked = new List<TargetRef>();
        bool[] cueDone = new bool[0];
        readonly List<string> warnings = new List<string>(), errors = new List<string>(), lateLoads = new List<string>();
        readonly Dictionary<string, int> errorCounts = new Dictionary<string, int>();
        int step = -1, frame, posInFrame, segIndex = -1, capturedSteps, fixedCount, leaks, currentCam = -1, renders;
        MultiCamSeg seg;
        double t0, tPrev, outputClock;
        float runRealStart, loopRealStart = -1f, nextProgress;
        double msStep, msVisit, msRead, nStep, nVisit, nRead;
        bool stopRequested, finished, targetsBegun, aborted, focusWas = true;
        string state = "starting", failure;

        internal void Init(MultiCamPlan p, string m, string dir, double at, DevCore c)
        {
            plan = p; spec = p.spec; mode = m; outDir = dir; previewAtT = at; core = c;
            rehearse = m == "rehearse"; preview = m == "preview"; previewNow = preview && double.IsNaN(at);
            seg = plan.segs[0];
            started = DateTime.Now.ToString("s", IC);
            runRealStart = Time.realtimeSinceStartup;
            StartCoroutine(Run());
        }

        public void RequestStop() { stopRequested = true; Log("stop requested"); }

        void FixedUpdate() { fixedCount++; }

        void OnDestroy() { if (!finished) Finish("stopped: runner destroyed", true); }

        void OnApplicationQuit() { if (!finished) Finish("stopped: application quit", true); }

        internal void AbortNow(string why)
        {
            aborted = true;
            try { StopAllCoroutines(); } catch { }
            Finish("aborted (" + why + ")", true);
        }

        IEnumerator Run()
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(ReplayModes.Is(mode) ? ReplayMain() : Body());
            while (stack.Count > 0)
            {
                object cur = null;
                bool moved;
                try
                {
                    moved = stack.Peek().MoveNext();
                    if (moved) cur = stack.Peek().Current;
                }
                catch (Exception e)
                {
                    failure = e.GetType().Name + ": " + e.Message;
                    DevLog.Error("mcam runner", e);
                    try { Log("FAILED: " + e + ""); } catch { }
                    break;
                }
                if (!moved) { stack.Pop(); continue; }
                var nested = cur as IEnumerator;
                if (nested != null) { stack.Push(nested); continue; }
                yield return cur;
            }
            Finish(failure != null ? "failed: " + failure : stopRequested ? "stopped" : "done", false);
        }

        IEnumerator Body()
        {
            state = "setup";
            OpenOutput();
            MultiCamSession.Push(spec, core);
            WriteRun(false);
            Log(mode + " '" + spec.name + "' from " + plan.path + " -> " + outDir);
            foreach (var w in plan.report.warnings) Log("spec warn: " + w);
            Running1();
            if (!previewNow)
            {
                yield return Setup();
                if (spec.seed >= 0) { MultiCam.ApplySeed(spec.seed); EventRow("setup", "seed", "", spec.seed.ToString(IC)); }
            }
            if (spec.session.res.x > 0 && spec.session.res.y > 0) yield return ApplyResolution();
            events = new MultiCamEvents(spec, plan);
            events.row = EventRow;
            if (!previewNow && spec.preroll > 0) yield return Preroll();
            yield return Freeze();
            yield return KeepLoaded();
            OpenWriters();
            WriteRun(false);
            state = mode == "capture" ? "capturing" : mode;
            if (previewNow) yield return PreviewNow();
            else yield return Loop();
        }

        // ---------------------------------------------------------------- setup, seed, resolution, preroll

        IEnumerator Setup()
        {
            for (int i = 0; i < spec.setup.Count; i++)
            {
                if (stopRequested) yield break;
                string line = spec.setup[i].Trim();
                if (line.Length == 0) continue;
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string c0 = p[0].ToLowerInvariant();
                state = "setup " + (i + 1) + "/" + spec.setup.Count + ": " + line;
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
                Log("setup: " + line + " -> " + res);
                yield return null;
                TickFree();
            }
        }

        IEnumerator WaitFor(string[] p, string line)
        {
            string kind = p.Length > 1 ? p[1].ToLowerInvariant() : "";
            string arg = p.Length > 2 ? string.Join(" ", p, 2, p.Length - 2) : "";
            float start = Time.realtimeSinceStartup, until = start + 60f;
            MultiCamEvents tmp = null;
            MultiCamContext tctx = null;
            if (kind == "event")
            {
                tmp = new MultiCamEvents(spec, plan);
                tmp.logMonitor = false;
                tmp.row = (k, n, s, d) => { if (k == "event") EventRow("setup", n, "waitfor event", d); };
                tctx = new MultiCamContext();
                tctx.spec = spec; tctx.eventTime = tmp.TimeOf; tctx.target = MultiCamTargets.Sample; tctx.rate = 60; tctx.fps = spec.timing.fps;
                var rep = new SpecReport();
                tmp.Arm(tctx, rep, arg);
                if (!rep.ok) { tmp.Disarm(); throw new Exception(line + ": " + rep.Summary(5)); }
            }
            while (true)
            {
                bool ok = false;
                if (kind == "loaded") ok = !GameManager.IsLoading();
                else if (kind == "streaming") ok = !GameManager.IsLoading() && Levels.activeCount > 0 && SubsceneCullingManager.instance.AllCullersFinished();
                else if (kind == "savepoint") { int sub, sp; ok = !GameManager.IsLoading() && G.CurrentSavepoint(out sub, out sp) && SavepointMatches(arg, sub, sp); }
                else if (kind == "event") ok = tmp != null && tmp.HasFired(arg);
                else throw new Exception("unknown setup primitive: " + line);
                if (ok || stopRequested) break;
                if (Time.realtimeSinceStartup > until)
                {
                    if (tmp != null) tmp.Disarm();
                    throw new Exception("setup timeout after 60 s: " + line);
                }
                yield return null;
                TickFree();
                if (tmp != null) { tctx.t = Time.realtimeSinceStartup - start; tmp.PollFrame(tctx); tmp.PollStep(tctx); }
            }
            if (tmp != null) tmp.Disarm();
            string took = (Time.realtimeSinceStartup - start).ToString("0.0", IC) + " s";
            EventRow("setup", kind, line, "ok after " + took);
            Log("setup: " + line + " ok after " + took);
        }

        static bool SavepointMatches(string arg, int sub, int sp)
        {
            string n = arg.Trim().TrimStart('#').Trim();
            if (n.Length == 0) return true;
            var parts = n.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int a, b;
            if (parts.Length >= 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out a) && (parts.Length == 1 || int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out b)))
            {
                if (a != sub) return false;
                return parts.Length == 1 || int.Parse(parts[1], CultureInfo.InvariantCulture) == sp;
            }
            string label = G.SubsceneLabel(sub) + " #" + sp;
            return label.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        IEnumerator ApplyResolution()
        {
            int w = (int)spec.session.res.x, h = (int)spec.session.res.y;
            if (Screen.width == w && Screen.height == h) yield break;
            MultiCamSession.SetResolution(w, h);
            float until = Time.realtimeSinceStartup + 6f;
            while ((Screen.width != w || Screen.height != h) && Time.realtimeSinceStartup < until) { yield return null; TickFree(); }
            if (Screen.width != w || Screen.height != h) throw new Exception("resolution " + w + "x" + h + " not applied (screen is " + Screen.width + "x" + Screen.height + ")");
            for (int i = 0; i < 3; i++) { yield return null; TickFree(); }
            Log("resolution " + w + "x" + h);
        }

        IEnumerator Preroll()
        {
            state = "preroll";
            Time.captureFramerate = plan.segs[0].rate;
            Running1();
            double start = Time.timeDbl;
            int n = 0, max = (int)(spec.preroll * plan.segs[0].rate * 2) + 120;
            while (Time.timeDbl - start < spec.preroll - 1e-6 && !stopRequested && n < max) { yield return null; TickFree(); n++; }
            Log("preroll " + (Time.timeDbl - start).ToString("0.###", IC) + " s in " + n + " frames");
        }

        // ---------------------------------------------------------------- freeze: camera, targets, events, rigs

        IEnumerator Freeze()
        {
            state = "freeze";
            Time.captureFramerate = plan.segs[0].rate;
            Frozen();
            if (CameraControl.free) { CameraControl.SetFree(false); yield return null; TickFrozen(); }
            cam = RenderHost.WorldCam();
            if (cam == null) throw new Exception("no gameplay camera");
            gameNear = cam.nearClipPlane > 0 ? cam.nearClipPlane : MultiCamConst.GameNear;
            var rep = new SpecReport();
            MultiCamTargets.Begin(spec, rep);
            targetsBegun = true;
            Absorb(rep, "targets");
            if (!rep.ok) throw new Exception("target resolution failed:\n" + rep.Summary(20));
            MultiCamSession.Attach(cam, spec);
            Wire();
            var rep2 = new SpecReport();
            events.Arm(ctx, rep2, null);
            Absorb(rep2, "events");
            if (!rep2.ok) throw new Exception("event setup failed:\n" + rep2.Summary(20));
            int n = spec.cameras.Count;
            rigs = new ICamRig[n]; poses = new CamPose[n]; evaluated = new bool[n]; visited = new bool[n];
            frameThisStep = new object[n]; lastCopy = new string[n]; hides = new List<Renderer>[n];
            cueDone = new bool[spec.cues.Count];
            for (int i = 0; i < n; i++) rigs[i] = MultiCamRig.Create(spec.cameras[i]);
            ctx.step = -1; ctx.t = 0; ctx.dt = 0; ctx.frame = -1; ctx.sub = -1; ctx.captured = false; ctx.outputTime = 0; ctx.outputDt = 0;
            MultiCamRig.SampleGame(cam, ctx);
            MultiCamTargets.Step(ctx);
            var rep3 = new SpecReport();
            Interpolate(rep3);
            ResolveHides(rep3);
            Absorb(rep3, "objects");
            for (int i = 0; i < n; i++) rigs[i].Begin(ctx);
            EvaluateAll();
            try { tracked = MultiCamTargets.Tracked() ?? new List<TargetRef>(); } catch (Exception e) { Warn("tracked targets: " + e.Message); tracked = new List<TargetRef>(); }
            Log("frozen: " + n + " camera(s), " + tracked.Count + " tracked target(s), camera " + cam.name + " fov " + cam.fieldOfView.ToString("0.###", IC) + " near " + gameNear.ToString("0.###", IC));
        }

        void Wire()
        {
            ctx.spec = spec;
            ctx.camera = cam;
            ctx.screenW = Screen.width; ctx.screenH = Screen.height;
            ctx.aspect = Screen.height > 0 ? Screen.width / (float)Screen.height : 16f / 9f;
            ctx.fps = spec.timing.fps;
            ctx.target = MultiCamTargets.Sample;
            ctx.poseOf = PoseOf;
            ctx.eventTime = events.TimeOf;
            ctx.log = RigLog;
            ctx.preview = preview;
            ctx.rehearse = rehearse;
            ctx.planning = false;
            ctx.rate = seg.rate; ctx.speed = seg.speed;
        }

        bool PoseOf(string name, out CamPose p)
        {
            p = default(CamPose);
            int i;
            if (name == null || !plan.camIndex.TryGetValue(name, out i) || i >= evaluated.Length || !evaluated[i]) return false;
            p = poses[i];
            return true;
        }

        internal bool PoseNow(string name, out CamPose p) { return PoseOf(name, out p); }

        internal bool TargetsBegun { get { return targetsBegun && !finished; } }

        internal bool TargetsLive { get { return targetsBegun && !finished && step >= 0; } }

        void Interpolate(SpecReport rep)
        {
            foreach (var r in spec.interpolate)
            {
                if (r == null) continue;
                GameObject go = null;
                TargetSample s;
                try { if (MultiCamTargets.Sample(r, out s) && s.go != null) go = s.go; } catch { }
                if (go == null && !string.IsNullOrEmpty(r.of)) go = MultiCamTargets.ResolveObject(r.of, r.where, rep);
                if (go == null) { rep.Warn(r.where, "interpolate: not resolved: " + r.text); continue; }
                var rb = go.GetComponent<Rigidbody>();
                if (rb == null) rb = go.GetComponentInChildren<Rigidbody>();
                if (rb == null) { rep.Warn(r.where, "interpolate: no Rigidbody on " + Inspector.PathOf(go.transform)); continue; }
                MultiCamSession.SetInterpolation(rb);
                Log("interpolate " + Inspector.PathOf(rb.transform));
            }
        }

        void ResolveHides(SpecReport rep)
        {
            for (int i = 0; i < spec.cameras.Count; i++)
            {
                var c = spec.cameras[i];
                if (c.hide == null || c.hide.Count == 0) continue;
                var list = new List<Renderer>();
                foreach (var q in c.hide)
                {
                    string path, boxError;
                    List<Vector3> min, max;
                    bool box = MultiCamHide.SplitBox(q, out path, out min, out max, out boxError);
                    if (!box && boxError.Length > 0) { rep.Warn(c.where + ".hide", boxError + ": " + q); continue; }
                    var go = MultiCamTargets.ResolveObject(path, c.where + ".hide", rep);
                    if (go == null) { rep.Warn(c.where + ".hide", "not resolved: " + q); continue; }
                    if (box) { rep.Note(c.where + ".hide", "box " + MultiCamHide.BoxText(min, max) + " of " + Inspector.PathOf(go.transform) + " is cut in replay passes and mcam show only; live visits draw it whole"); continue; }
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true)) if (r != null) list.Add(r);
                }
                hides[i] = list;
            }
        }

        // ---------------------------------------------------------------- keep-loaded subscenes

        IEnumerator KeepLoaded()
        {
            var st = spec.stream;
            if (st.keepLoaded == "none") yield break;
            state = "keep-loaded";
            var mgr = SubsceneCullingManager.instance;
            var add = new List<SubsceneCuller>();
            foreach (var name in st.cullers)
            {
                var c = mgr.GetSubsceneCullerByName(name);
                if (c == null) Warn("keep-loaded: no culler named " + name); else add.Add(c);
            }
            var planned = new List<CamPose>();
            if (st.keepLoaded == "auto" && st.prepass)
            {
                for (int i = 0; i < poses.Length; i++) planned.Add(poses[i]);
                ctx.planning = true;
                for (int i = 0; i < rigs.Length; i++)
                {
                    try { rigs[i].Plan(ctx, planned); }
                    catch (Exception e) { RateError("plan " + spec.cameras[i].name, e); }
                }
                ctx.planning = false;
                foreach (var c in mgr.GetAllSubsceneCullers())
                {
                    if (c == null || add.Contains(c)) continue;
                    var b = c.CullingBounds;
                    foreach (var p in planned) if (Sees(p, b)) { add.Add(c); break; }
                }
            }
            int added = 0;
            foreach (var c in add) if (MultiCamSession.Keep(c)) added++;
            if (add.Count == 0) yield break;
            Log("keep-loaded: " + add.Count + " culler(s) in view of " + planned.Count + " planned pose(s), " + added + " newly added");
            float until = Time.realtimeSinceStartup + Math.Max(1f, st.timeout);
            while (Time.realtimeSinceStartup < until && !stopRequested)
            {
                bool done = mgr.AllCullersFinished();
                if (done) foreach (var c in add) if (c != null && !c.IsActive) { done = false; break; }
                if (done) break;
                yield return null;
                TickFrozen();
            }
            int missing = 0;
            foreach (var c in add) if (c != null && !c.IsActive) missing++;
            if (missing > 0) Warn("keep-loaded: " + missing + " culler(s) not active after " + st.timeout.ToString("0", IC) + " s (frozen time may stall streaming); pop-in possible");
            if (st.visitFrames > 0)
            {
                foreach (var p in planned)
                {
                    ApplyPose(p);
                    for (int k = 0; k < st.visitFrames; k++) { yield return null; TickFrozen(); }
                }
                RestoreGame();
                CameraControl.GameStep(false);
            }
            if (st.freeze) MultiCamSession.SetCulling(false);
        }

        bool Sees(CamPose p, Bounds b)
        {
            if (b.size == Vector3.zero) return false;
            var planes = Frustum(p, ctx.aspect, p.near > 0 ? p.near : gameNear);
            return GeometryUtility.TestPlanesAABB(planes, b);
        }

        static Plane[] Frustum(CamPose p, float aspect, float near)
        {
            var proj = Matrix4x4.Perspective(p.fov > 0 ? p.fov : MultiCamConst.GameFov, aspect, Mathf.Max(0.05f, near), MultiCamConst.GameFar);
            var w2c = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(p.p, p.r, Vector3.one).inverse;
            return GeometryUtility.CalculateFrustumPlanes(proj * w2c);
        }

        void LateLoads()
        {
            var st = spec.stream;
            if (!st.addDuringTake || st.keepLoaded == "none") return;
            var mgr = SubsceneCullingManager.instance;
            var all = mgr.GetAllSubsceneCullers();
            for (int i = 0; i < poses.Length; i++)
            {
                if (!spec.cameras[i].enabled) continue;
                Plane[] planes = null;
                foreach (var c in all)
                {
                    if (c == null || c.IsLoaded || MultiCamSession.IsKept(c)) continue;
                    var b = c.CullingBounds;
                    if (b.size == Vector3.zero) continue;
                    if (planes == null) planes = Frustum(poses[i], ctx.aspect, poses[i].near > 0 ? poses[i].near : gameNear);
                    if (!GeometryUtility.TestPlanesAABB(planes, b)) continue;
                    if (!MultiCamSession.Keep(c)) continue;
                    string n = c.name;
                    lateLoads.Add(n);
                    Mark("late load", n + " (camera " + spec.cameras[i].name + ")");
                }
            }
        }

        // ---------------------------------------------------------------- step loop

        IEnumerator Loop()
        {
            yield return new WaitForEndOfFrame();
            t0 = Time.timeDbl; tPrev = 0; outputClock = 0; frame = 0; posInFrame = 0; step = -1;
            ctx.t = 0;
            loopRealStart = Time.realtimeSinceStartup;
            MultiCamLook.realTime = spec.session.realTime != "wall";
            MultiCamLook.clock = loopRealStart;
            bool previewAt = preview && !double.IsNaN(previewAtT);
            bool previewShot = false;
            while (true)
            {
                if (posInFrame == 0)
                {
                    if (stopRequested) { endReason = "stop"; break; }
                    if (ctx.t >= spec.seconds - 1e-6) { endReason = "seconds"; break; }
                    if (spec.until != null && events.ReachedFired(spec.until, ctx.t)) { endReason = "until " + spec.until; break; }
                    if (previewAt && ctx.t >= previewAtT - 1e-6) previewShot = true;
                    PickSegment();
                    yield return Hold();
                    if (stopRequested) { endReason = "stop"; break; }
                }
                RunCues();
                RestoreGame();
                if (spec.timing.physics == "fine") { double dt = 1.0 / seg.rate; Time.fixedDeltaTime = (float)(dt / Math.Max(1.0, Math.Ceiling(dt * 60.0 - 1e-6))); }
                Time.captureFramerate = seg.rate;
                fixedCount = 0;
                Running1();
                float rs = Time.realtimeSinceStartup;
                yield return null;
                Frozen();
                CameraControl.GameStep(false);
                if (MultiCamSession.locked) InputCapture.keyboard = true;
                msStep += (Time.realtimeSinceStartup - rs) * 1000.0; nStep++;
                double t = Time.timeDbl - t0;
                step++;
                ctx.step = step;
                ctx.dt = (float)(t - tPrev);
                tPrev = t;
                ctx.t = t;
                bool cap = previewAt ? previewShot : posInFrame >= seg.steps - seg.captured;
                ctx.captured = cap && !rehearse;
                ctx.frame = cap ? (previewAt ? 0 : frame) : -1;
                ctx.sub = cap ? (previewAt ? 0 : posInFrame - (seg.steps - seg.captured)) : -1;
                ctx.rate = seg.rate; ctx.speed = seg.speed;
                outputClock += ctx.dt / Math.Max(1e-6f, seg.speed);
                ctx.outputTime = cap && !previewAt ? frame / (double)spec.timing.fps : outputClock;
                ctx.outputDt = ctx.dt / Math.Max(1e-6f, seg.speed);
                ctx.realTime = Time.realtimeSinceStartup - runRealStart;
                MultiCamLook.clock = loopRealStart + t;
                SampleAndEvaluate();
                Rows(cap);
                if (ctx.captured) LateLoads();
                yield return new WaitForEndOfFrame();
                if (ctx.captured) { yield return VisitAll(); capturedSteps++; }
                events.PollFrame(ctx);
                if (previewShot) { frame = 1; endReason = "preview"; break; }
                posInFrame++;
                if (posInFrame >= seg.steps) { posInFrame = 0; frame++; outputClock = frame / (double)spec.timing.fps; }
                AfterStep();
                if (stopRequested) { endReason = "stop"; break; }
            }
            Log("loop end (" + endReason + "): " + (step + 1) + " steps, " + frame + " frames, " + capturedSteps + " captured, game " + ctx.t.ToString("0.###", IC) + " s");
        }

        IEnumerator PreviewNow()
        {
            step = 0; ctx.step = 0; ctx.captured = true; ctx.frame = 0; ctx.sub = 0;
            loopRealStart = Time.realtimeSinceStartup;
            MultiCamLook.realTime = false;
            Rows(true);
            yield return new WaitForEndOfFrame();
            yield return VisitAll();
            capturedSteps = 1; frame = 1;
            endReason = "preview";
        }

        void PickSegment()
        {
            int pick = 0;
            for (int i = 0; i < plan.segs.Count; i++) if (i == 0 || ctx.Reached(plan.segs[i].spec.from)) pick = i;
            if (pick == segIndex) return;
            seg = plan.segs[pick];
            segIndex = pick;
            EventRow("segment", seg.label, "segments[" + pick + "]", seg.Describe());
            Log("segment " + pick + (seg.label.Length > 0 ? " '" + seg.label + "'" : "") + " at t " + ctx.t.ToString("0.###", IC) + ": " + seg.Describe());
        }

        IEnumerator Hold()
        {
            float since = Time.realtimeSinceStartup;
            bool held = false;
            while (!stopRequested)
            {
                bool paused = false, loading = false;
                try { paused = GameManager.IsPaused; loading = spec.session.holdWhileLoading && GameManager.IsLoading(); } catch { }
                if (!paused && !loading) break;
                if (!held) { held = true; Mark("hold", paused ? "game paused" : "game loading"); state = "hold (" + (paused ? "paused" : "loading") + ")"; }
                yield return null;
                TickFrozen();
            }
            if (held) { Mark("hold", "released after " + (Time.realtimeSinceStartup - since).ToString("0.0", IC) + " s"); state = mode == "capture" ? "capturing" : mode; }
            if (RenderHost.focused != focusWas) { focusWas = RenderHost.focused; Mark("focus", focusWas ? "regained" : "lost (capture continues)"); }
        }

        void RunCues()
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
                    string res = RunCmd(cmd);
                    EventRow("cue", q.label, cmd, res);
                    Log("cue t " + ctx.t.ToString("0.###", IC) + (q.label.Length > 0 ? " '" + q.label + "'" : "") + ": " + cmd + " -> " + res);
                }
            }
        }

        void RestoreGame()
        {
            var g = ctx.game;
            if (g.ok) CameraControl.WriteGamePose(g.rendered.p, g.rendered.r, g.rendered.fov > 0 ? g.rendered.fov : MultiCamSession.camFov, gameNear);
            CameraControl.GameStep(true);
            MultiCamLook.post = null;
            MultiCamLook.fogDelta = 0f;
            currentCam = -1;
        }

        void SampleAndEvaluate()
        {
            try { MultiCamRig.SampleGame(cam, ctx); } catch (Exception e) { RateError("SampleGame", e); }
            try { MultiCamTargets.Step(ctx); } catch (Exception e) { RateError("targets step", e); }
            try { events.PollFrame(ctx); events.PollStep(ctx); } catch (Exception e) { RateError("events", e); }
            EvaluateAll();
        }

        void EvaluateAll()
        {
            for (int i = 0; i < evaluated.Length; i++) evaluated[i] = false;
            foreach (int i in plan.order)
            {
                CamPose p;
                try { p = rigs[i].Evaluate(ctx); }
                catch (Exception e) { RateError("evaluate " + spec.cameras[i].name, e); p = default(CamPose); }
                if (!p.ok || float.IsNaN(p.p.x) || float.IsNaN(p.r.w))
                {
                    int fl = p.flags;
                    p = ctx.game.rendered;
                    p.ok = true; p.copyOf = null;
                    p.flags = fl | PoseFlags.Fallback;
                }
                if (p.fov <= 0) p.fov = ctx.game.rendered.fov > 0 ? ctx.game.rendered.fov : MultiCamSession.camFov;
                poses[i] = p;
                evaluated[i] = true;
                var cs = spec.cameras[i];
                if (cs.mode == "director" && step >= 0)
                {
                    string c = p.copyOf ?? "";
                    if (c != (lastCopy[i] ?? "") || (p.flags & PoseFlags.Cut) != 0)
                    {
                        EventRow("shot", c.Length > 0 ? c : "(blend)", cs.name, PoseFlags.Letters(p.flags));
                        lastCopy[i] = c;
                    }
                }
            }
        }

        void ApplyPose(CamPose pose)
        {
            float fov = pose.fov > 0 ? pose.fov : (ctx.game.rendered.fov > 0 ? ctx.game.rendered.fov : MultiCamSession.camFov);
            float near = pose.near > 0 ? pose.near : gameNear;
            CameraControl.Set(pose.p, pose.r, fov, near);
        }

        // ---------------------------------------------------------------- visits

        IEnumerator VisitAll()
        {
            for (int i = 0; i < frameThisStep.Length; i++) { frameThisStep[i] = null; visited[i] = false; }
            List<int> copies = null;
            for (int i = 0; i < spec.cameras.Count; i++)
            {
                if (!spec.cameras[i].enabled) continue;
                if (CopySource(i) >= 0) { if (copies == null) copies = new List<int>(); copies.Add(i); continue; }
                yield return Visit(i, poses[i]);
            }
            if (copies == null) yield break;
            foreach (int i in copies)
            {
                int s = CopySource(i);
                if (s >= 0 && visited[s] && frameThisStep[s] != null)
                {
                    frameThisStep[i] = frameThisStep[s];
                    visited[i] = true;
                    Enqueue(i, frameThisStep[s]);
                }
                else yield return Visit(i, poses[i]);
            }
        }

        int CopySource(int i)
        {
            var p = poses[i];
            if (string.IsNullOrEmpty(p.copyOf)) return -1;
            int s;
            if (!plan.camIndex.TryGetValue(p.copyOf, out s) || s == i) return -1;
            var a = spec.cameras[i];
            var b = spec.cameras[s];
            if (!b.enabled || !string.IsNullOrEmpty(poses[s].copyOf)) return -1;
            if (!Same(a.render, b.render) || !Same(a.post, b.post)) return -1;
            if ((hides[i] != null && hides[i].Count > 0) || (hides[s] != null && hides[s].Count > 0)) return -1;
            if (MultiCamShots.AnyHide(spec, i) || MultiCamShots.AnyHide(spec, s)) return -1;
            return s;
        }

        static bool Same(SpecNode a, SpecNode b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.GetType() != b.GetType()) return false;
            foreach (var f in SpecBind.Fields(a.GetType())) if (!Equals(f.GetValue(a), f.GetValue(b))) return false;
            return true;
        }

        IEnumerator Visit(int i, CamPose pose)
        {
            var cs = spec.cameras[i];
            var rs = cs.render ?? spec.render;
            currentCam = i;
            float v0 = Time.realtimeSinceStartup;
            ApplyPose(pose);
            SetHidden(i, true);
            MultiCamLook.post = cs.post;
            MultiCamLook.fogDelta = cs.post == null ? 0f : cs.post.fogOffset + (cs.post.fogMatch ? (pose.aim - pose.p).magnitude - ctx.game.aimDistance : 0f);
            int w = Math.Max(1, rs.warmup), n = Math.Max(1, rs.samples);
            MultiCamSession.TaaBegin(rs, n);
            for (int r = 0; r < w + n; r++)
            {
                yield return null;
                TickFrozen();
                MultiCamSession.TaaFrame(rs, r, w);
                renders++;
            }
            yield return new WaitForEndOfFrame();
            float r0 = Time.realtimeSinceStartup;
            object data = grab != null ? grab.Grab() : null;
            msRead += (Time.realtimeSinceStartup - r0) * 1000.0; nRead++;
            frameThisStep[i] = data;
            visited[i] = true;
            if (data != null) Enqueue(i, data);
            SetHidden(i, false);
            MultiCamSession.TaaEnd();
            MultiCamLook.post = null;
            MultiCamLook.fogDelta = 0f;
            msVisit += (Time.realtimeSinceStartup - v0) * 1000.0; nVisit++;
        }

        void SetHidden(int i, bool hide)
        {
            var l = hides.Length > i ? hides[i] : null;
            if (hide && i < spec.cameras.Count && spec.cameras[i].mode == "director" && MultiCamShots.AnyHide(spec, i))
            {
                var names = MultiCamShots.HideNames(spec, i, ctx.t, r => ctx.TimeOf(r, 0.0), poses.Length > i ? poses[i].flags : 0);
                names.RemoveAll(n => n.StartsWith(MultiCamShots.BackfaceMark, StringComparison.Ordinal));
                l = names.Count > 0 ? MultiCamHide.Resolve(names, spec.cameras[i].where + ".hide", null).renderers : null;
            }
            if (l == null) { if (!hide) MultiCamSession.ShowHidden(); return; }
            if (hide) { foreach (var r in l) if (r != null && r.enabled) { r.enabled = false; MultiCamSession.hidden.Add(r); } }
            else MultiCamSession.ShowHidden();
        }

        void Enqueue(int i, object data)
        {
            var w = writers != null && i < writers.Length ? writers[i] : null;
            if (w == null) return;
            string fmt = spec.output.format;
            string path = fmt == "pipe" ? null : Path.Combine(camDirs[i], step.ToString("D5", IC) + (fmt == "raw" ? ".rgba" : ".jpg"));
            w.Add(path, data);
        }

        // ---------------------------------------------------------------- frame helpers

        void Running1()
        {
            Time.timeScale = 1f;
            try { GameManager.TimeScale = 1f; } catch { }
        }

        void Frozen()
        {
            Time.timeScale = 0f;
            try { GameManager.TimeScale = 0f; } catch { }
        }

        void TickFrozen()
        {
            if (Time.timeScale != 0f)
            {
                leaks++;
                if (leaks <= 5 || leaks % 100 == 0) Mark("timeScale leak", "timeScale " + Time.timeScale.ToString("0.###", IC) + " during a frozen frame (" + leaks + ")");
            }
            Frozen();
            if (MultiCamSession.locked) InputCapture.keyboard = true;
            if (events != null) { try { events.PollFrame(ctx); } catch (Exception e) { RateError("events frame", e); } }
        }

        void TickFree()
        {
            if (MultiCamSession.locked) InputCapture.keyboard = true;
        }

        string RunCmd(string line)
        {
            var ls = DevLog.Lines;
            string lastLine = ls.Count > 0 ? ls[ls.Count - 1] : null;
            string res;
            try { res = Remote.RunInternal(line, core); }
            catch (Exception e) { res = "ERROR " + e.GetType().Name + ": " + e.Message; }
            var extra = new StringBuilder();
            int start = 0;
            if (lastLine != null) { start = ls.Count; for (int i = ls.Count - 1; i >= 0; i--) if (ReferenceEquals(ls[i], lastLine)) { start = i + 1; break; } }
            for (int i = start; i < ls.Count; i++) { if (extra.Length > 0) extra.Append(" | "); extra.Append(ls[i]); }
            string r = (res ?? "").Trim();
            if (extra.Length > 0) r = r.Length > 0 ? r + " | " + extra : extra.ToString();
            r = r.Replace('\n', ' ').Replace('\r', ' ');
            return r.Length > 400 ? r.Substring(0, 400) + "..." : r;
        }

        void Absorb(SpecReport rep, string what)
        {
            foreach (var w in rep.warnings) Warn(what + ": " + w);
            foreach (var n in rep.notes) Log(what + ": " + n);
            foreach (var e in rep.errors) Error(what + ": " + e);
        }

        // ---------------------------------------------------------------- outputs

        void OpenOutput()
        {
            Directory.CreateDirectory(outDir);
            if (File.Exists(Path.Combine(outDir, "run.json"))) DevLog.Write("[mcam] note: " + outDir + " already holds a take; files are overwritten");
            File.WriteAllText(Path.Combine(outDir, "spec.json"), spec.text);
            logW = new StreamWriter(Path.Combine(outDir, "log.txt"), false, new UTF8Encoding(false));
            logW.AutoFlush = true;
            meta = new MultiCamCsv(Path.Combine(outDir, "meta.csv"), "step,game_time,rate,group,frame,sub,captured,speed,real_time,fixed_steps,segment");
            eventsCsv = new MultiCamCsv(Path.Combine(outDir, "events.csv"), "step,game_time,frame,kind,name,source,detail");
            if (spec.output.poses) posesCsv = new MultiCamCsv(Path.Combine(outDir, "poses.csv"), "step,camera,x,y,z,pitch,yaw,roll,fov,qx,qy,qz,qw,aim_x,aim_y,aim_z,near,flags,copy_of,subj_x,subj_y,subj_z");
            if (spec.output.tracks) tracksCsv = new MultiCamCsv(Path.Combine(outDir, "tracks.csv"), "step,game_time,target,ok,x,y,z,vx,vy,vz,count,bmin_x,bmin_y,bmin_z,bmax_x,bmax_y,bmax_z");
            if (spec.output.rig) rigCsv = new MultiCamCsv(Path.Combine(outDir, "rig.csv"), "step,game_time,box,cam_x,cam_y,cam_z,aim_x,aim_y,aim_z,up_x,up_y,up_z,x,y,z,qx,qy,qz,qw,fov,clean_x,clean_y,clean_z,clean_qx,clean_qy,clean_qz,clean_qw,vel_x,vel_y,vel_z,probe_x,probe_y,probe_z,extrap,hand_x,hand_y,hand_z,shake_x,shake_y,shake_z,cut");
            camDirs = new string[spec.cameras.Count];
            for (int i = 0; i < spec.cameras.Count; i++)
            {
                camDirs[i] = Path.Combine(outDir, spec.cameras[i].name);
                if (!rehearse && spec.cameras[i].enabled && spec.output.format != "pipe") Directory.CreateDirectory(camDirs[i]);
            }
        }

        void OpenWriters()
        {
            if (rehearse) return;
            var o = spec.output;
            int w = Screen.width, h = Screen.height;
            grab = new MultiCamGrab(w, h, o.format == "jpg", Mathf.Clamp(o.jpeg, 1, 100));
            writers = new MultiCamFrameWriter[spec.cameras.Count];
            for (int i = 0; i < spec.cameras.Count; i++)
            {
                var c = spec.cameras[i];
                if (!c.enabled) continue;
                if (o.format == "pipe")
                {
                    string file = Path.Combine(outDir, c.name + ".mkv");
                    writers[i] = MultiCamFrameWriter.Pipe(c.name, Math.Max(1, o.queue), o.ffmpeg, PipeArgs(w, h, o, file));
                    Log("pipe " + c.name + ": " + o.ffmpeg + " " + PipeArgs(w, h, o, file));
                }
                else writers[i] = new MultiCamFrameWriter(c.name, Math.Max(1, o.queue));
            }
        }

        string PipeArgs(int w, int h, OutputSpec o, string file) { return PipeArgs(w, h, o, file, "rgba", true); }

        string PipeArgs(int w, int h, OutputSpec o, string file, string pixFmt, bool flip)
        {
            bool nv = o.pipeCodec.ToLowerInvariant().IndexOf("nvenc") >= 0;
            string q = nv ? "-preset p5 -rc constqp -qp " + o.pipeQp : "-preset fast -crf " + o.pipeQp;
            return "-hide_banner -loglevel error -y -f rawvideo -pix_fmt " + pixFmt + " -s " + w + "x" + h + " -framerate " + spec.timing.fps.ToString("0.###", IC) +
                   " -i - -an -vf \"" + (flip ? "vflip," : "") + "scale=out_color_matrix=bt709:out_range=tv,format=" + (nv ? "p010le" : "yuv420p10le") + "\" -c:v " + o.pipeCodec + " " + q +
                   " -color_primaries bt709 -color_trc bt709 -colorspace bt709 \"" + file + "\"";
        }

        void Rows(bool cap)
        {
            bool capturedRow = cap && !rehearse;
            meta.I(step).D(ctx.t, "0.######").I(seg.rate).I(seg.captured).I(ctx.frame).I(ctx.sub).I(capturedRow ? 1 : 0).D(seg.speed, "0.######")
                .D(Time.realtimeSinceStartup - runRealStart, "0.###").I(fixedCount).I(seg.index).End();
            if (posesCsv != null)
            {
                for (int i = 0; i < poses.Length; i++)
                {
                    var p = poses[i];
                    var e = p.r.eulerAngles;
                    posesCsv.I(step).S(spec.cameras[i].name).V(p.p, "0.#####").D(e.x, "0.####").D(e.y, "0.####").D(e.z, "0.####").D(p.fov, "0.####").Q(p.r)
                        .V(p.aim, "0.####").D(p.near > 0 ? p.near : gameNear, "0.###").S(PoseFlags.Letters(p.flags)).S(p.copyOf ?? "").V(p.hasSubj ? p.subj : new Vector3(float.NaN, float.NaN, float.NaN), "0.####").End();
                }
            }
            if (rigCsv != null)
            {
                var g = ctx.game;
                rigCsv.I(step).D(ctx.t, "0.######").S(g.box ?? "").V(g.positionCamera, "0.#####").V(g.positionAim, "0.#####").V(g.positionUp, "0.######")
                    .V(g.rendered.p, "0.#####").Q(g.rendered.r).D(g.rendered.fov, "0.####").V(g.clean.p, "0.#####").Q(g.clean.r).V(g.velocity, "0.####")
                    .V(g.probe, "0.#####").D(g.extrapolation, "0.#####").V(g.handOffset, "0.######").V(g.shakeOffset, "0.######").I(g.cut ? 1 : 0).End();
            }
            if (tracksCsv != null)
            {
                foreach (var r in tracked)
                {
                    TargetSample s = default(TargetSample);
                    bool ok = false;
                    try { ok = MultiCamTargets.Sample(r, out s) && s.ok; } catch { ok = false; }
                    tracksCsv.I(step).D(ctx.t, "0.######").S(r.text).I(ok ? 1 : 0);
                    if (ok) tracksCsv.V(s.pos, "0.#####").V(s.vel, "0.####").I(s.count); else tracksCsv.E().E().E().E().E().E().E();
                    if (ok && s.hasBounds) tracksCsv.V(s.bounds.min, "0.####").V(s.bounds.max, "0.####"); else tracksCsv.E().E().E().E().E().E();
                    tracksCsv.End();
                }
            }
        }

        void AfterStep()
        {
            int fe = Math.Max(1, spec.output.flushEvery);
            if ((step + 1) % fe == 0) FlushCsv();
            if (spec.session.gcSteps > 0 && (step + 1) % spec.session.gcSteps == 0) GC.Collect();
            if (writers != null)
                foreach (var w in writers)
                    if (w != null && w.error != null) throw new Exception("frame writer " + w.name + ": " + w.error);
            Progress(false);
        }

        void FlushCsv()
        {
            if (meta != null) meta.Flush();
            if (posesCsv != null) posesCsv.Flush();
            if (eventsCsv != null) eventsCsv.Flush();
            if (tracksCsv != null) tracksCsv.Flush();
            if (rigCsv != null) rigCsv.Flush();
            if (viewsCsv != null) viewsCsv.Flush();
        }

        void EventRow(string kind, string name, string source, string detail)
        {
            if (eventsCsv == null) return;
            eventsCsv.I(step).D(ctx.t, "0.######").I(ctx.frame).S(kind).S(name ?? "").S(source ?? "").S(detail ?? "").End();
        }

        void Log(string m)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff", IC) + "  step " + step + "  t " + ctx.t.ToString("0.###", IC) + "  " + m;
            try { if (logW != null) logW.Write(line + "\n"); } catch { }
            DevLog.Write("[mcam] " + m);
        }

        void RigLog(string m)
        {
            if (m == null) return;
            if (m.StartsWith("warn ", StringComparison.Ordinal)) Warn(m.Substring(5));
            else if (m.StartsWith("error ", StringComparison.Ordinal)) Error(m.Substring(6));
            else Log(m);
        }

        void Warn(string m) { warnings.Add(m); EventRow("warn", "", "", m); Log("warn: " + m); }

        void Error(string m) { errors.Add(m); EventRow("error", "", "", m); Log("error: " + m); }

        void Mark(string name, string detail) { EventRow("mark", name, "", detail); Log(name + ": " + detail); }

        void RateError(string key, Exception e)
        {
            int n;
            errorCounts.TryGetValue(key, out n);
            errorCounts[key] = ++n;
            if (n <= 3 || n % 200 == 0) Error(key + " (" + n + "x): " + e.GetType().Name + ": " + e.Message);
        }

        void Progress(bool force)
        {
            float now = Time.realtimeSinceStartup;
            if (!force && now < nextProgress) return;
            nextProgress = now + 0.5f;
            MultiCam.status = StatusLine();
            try { File.WriteAllText(Path.Combine(outDir, "progress.json"), ProgressJson()); } catch { }
        }

        double EtaSeconds()
        {
            if (loopRealStart < 0 || ctx.t <= 1e-3) return double.NaN;
            double end = spec.seconds;
            if (spec.until != null)
            {
                double u = events != null ? ctx.TimeOf(spec.until, double.NaN) : double.NaN;
                if (!double.IsNaN(u) && u < end) end = u;
            }
            double perGame = (Time.realtimeSinceStartup - loopRealStart) / ctx.t;
            return Math.Max(0, (end - ctx.t) * perGame);
        }

        double SecondsPerStep()
        {
            if (ReplayModes.Is(mode) && replay.phase == "pass")
            {
                int done = renders - rpEtaRenders0;
                return done > 0 ? (Time.realtimeSinceStartup - rpEtaStart) / done : double.NaN;
            }
            return loopRealStart >= 0 && step >= 0 ? (Time.realtimeSinceStartup - loopRealStart) / Math.Max(1, step + 1) : double.NaN;
        }

        internal string StatusLine()
        {
            var sb = new StringBuilder();
            sb.Append(mode).Append(" '").Append(spec.name).Append("' ");
            if (ReplayModes.Is(mode)) MultiCamProgressText.Line(sb, replay, state, spec.seconds);
            else sb.Append(state);
            if (step >= 0 && !ReplayModes.Is(mode))
            {
                sb.Append(": step ").Append(step).Append(", frame ").Append(frame).Append(", captured ").Append(capturedSteps)
                  .Append(", t ").Append(ctx.t.ToString("0.000", IC)).Append('/').Append(spec.seconds.ToString("0.##", IC)).Append(" s");
                if (seg != null) sb.Append(", seg ").Append(seg.index).Append(seg.label.Length > 0 ? " '" + seg.label + "'" : "").Append(" (").Append(seg.Describe()).Append(')');
                if (currentCam >= 0 && currentCam < spec.cameras.Count) sb.Append(", cam ").Append(spec.cameras[currentCam].name);
                double sps = (Time.realtimeSinceStartup - loopRealStart) / Math.Max(1, step + 1);
                sb.Append(", ").Append(sps.ToString("0.00", IC)).Append(" s/step");
                double eta = EtaSeconds();
                if (!double.IsNaN(eta)) sb.Append(", eta ").Append((eta / 60.0).ToString("0.0", IC)).Append(" min");
            }
            if (warnings.Count > 0) sb.Append(", ").Append(warnings.Count).Append(" warn");
            if (errors.Count > 0) sb.Append(", ").Append(errors.Count).Append(" err");
            sb.Append(" -> ").Append(outDir);
            return sb.ToString();
        }

        internal string ProgressJson()
        {
            var o = new Json.Obj();
            o["state"] = state; o["mode"] = mode; o["name"] = spec.name; o["out"] = outDir;
            o["step"] = (double)step; o["frame"] = (double)frame; o["captured_steps"] = (double)capturedSteps;
            o["game_time"] = ctx.t; o["seconds"] = spec.seconds;
            o["segment"] = seg != null ? (object)(double)seg.index : null; o["segment_label"] = seg != null ? seg.label : "";
            o["camera"] = currentCam >= 0 && currentCam < spec.cameras.Count ? spec.cameras[currentCam].name : "";
            double sps = SecondsPerStep();
            o["s_per_step"] = double.IsNaN(sps) ? null : (object)MultiCamPlan.Num(sps);
            o["eta_s"] = MultiCamPlan.Num(EtaSeconds());
            int depth = 0; double wait = 0;
            if (writers != null) foreach (var w in writers) if (w != null) { depth = Math.Max(depth, w.Depth); wait += w.waitMs; }
            o["queue"] = (double)depth;
            var ph = new Json.Obj();
            ph["step_frame"] = MultiCamPlan.Num(nStep > 0 ? msStep / nStep : double.NaN);
            ph["visit"] = MultiCamPlan.Num(nVisit > 0 ? msVisit / nVisit : double.NaN);
            ph["readback_encode"] = MultiCamPlan.Num(nRead > 0 ? msRead / nRead : double.NaN);
            ph["queue_wait_total"] = wait;
            o["phase_ms"] = ph;
            o["renders"] = (double)renders;
            o["timescale_leaks"] = (double)leaks;
            o["gc"] = (double)GC.CollectionCount(0);
            o["warnings"] = (double)warnings.Count; o["errors"] = (double)errors.Count;
            o["last_error"] = errors.Count > 0 ? errors[errors.Count - 1] : "";
            if (ReplayModes.Is(mode))
            {
                o["status"] = StatusLine();
                o["game_time"] = replay.t;
                o["eta_s"] = MultiCamPlan.Num(replay.eta);
                if (!string.IsNullOrEmpty(replay.camera)) o["camera"] = replay.camera;
                o["replay"] = MultiCamProgressText.ToJson(replay);
            }
            return Json.Write(o);
        }

        void WriteRun(bool final)
        {
            try
            {
                var o = new Json.Obj();
                o["version"] = 2.0;
                o["spec_version"] = (double)spec.version;
                o["legacy"] = plan.legacy;
                o["name"] = spec.name;
                o["mode"] = mode;
                o["out"] = outDir;
                o["spec"] = plan.path;
                o["started"] = started;
                o["finished"] = final ? DateTime.Now.ToString("s", IC) : null;
                o["status"] = final ? MultiCam.status : state;
                o["end_reason"] = endReason;
                o["screen"] = new List<object> { (double)Screen.width, (double)Screen.height };
                o["fps"] = (double)spec.timing.fps;
                o["physics"] = spec.timing.physics;
                o["format"] = spec.output.format;
                var sl = new List<object>();
                foreach (var g in plan.segs)
                {
                    var s = new Json.Obj();
                    s["index"] = (double)g.index;
                    s["from"] = g.spec.from != null ? g.spec.from.ToString() : "0";
                    s["from_resolved"] = MultiCamPlan.Num(g.index == 0 ? 0 : events != null ? ctx.TimeOf(g.spec.from, 0.0) : plan.Predict(g.spec.from));
                    s["speed"] = (double)g.speed; s["samples"] = (double)g.captured; s["shutter"] = (double)g.spec.shutter;
                    s["rate"] = (double)g.rate; s["steps_per_frame"] = (double)g.steps; s["label"] = g.label;
                    sl.Add(s);
                }
                o["segments"] = sl;
                var cl = new List<object>();
                foreach (var c in spec.cameras)
                {
                    var s = new Json.Obj();
                    s["name"] = c.name; s["mode"] = c.mode; s["label"] = c.label; s["enabled"] = c.enabled;
                    s["folder"] = c.enabled && !rehearse ? (spec.output.format == "pipe" ? c.name + ".mkv" : c.name) : null;
                    var r = c.render ?? spec.render;
                    var rj = new Json.Obj();
                    rj["warmup"] = (double)r.warmup; rj["samples"] = (double)r.samples; rj["taa"] = r.taa; rj["jitter"] = (double)(r.jitter > 0 ? r.jitter : r.taa == "keep" ? MultiCamSession.jitter : MultiCamSession.JitterFor(r.samples)); rj["motionBlur"] = (double)r.motionBlur;
                    s["render"] = rj;
                    var pj = new Json.Obj();
                    if (c.post != null) foreach (var k in c.post.given) { var f = SpecBind.Field(typeof(PostSpec), k); if (f != null) { var v = f.GetValue(c.post); pj[k] = v is float ? (object)(double)(float)v : v; } }
                    s["post"] = pj;
                    cl.Add(s);
                }
                o["cameras"] = cl;
                var tl = new List<object>();
                foreach (var r in tracked)
                {
                    var s = new Json.Obj();
                    s["ref"] = r.text;
                    string d;
                    try { d = MultiCamTargets.Describe(r); } catch (Exception e) { d = "describe failed: " + e.Message; }
                    s["resolved"] = d;
                    tl.Add(s);
                }
                o["targets"] = tl;
                o["events"] = events != null ? events.Summary() : new List<object>();
                o["keep_loaded"] = MultiCamPlan.Strs(MultiCamSession.KeptNames());
                o["late_loads"] = MultiCamPlan.Strs(lateLoads);
                var gj = new Json.Obj();
                gj["fov"] = (double)MultiCamSession.camFov; gj["near"] = (double)gameNear;
                var tj = new Json.Obj();
                tj["feedbackMin"] = (double)MultiCamSession.fbMin; tj["feedbackMax"] = (double)MultiCamSession.fbMax; tj["motionBlurStrength"] = (double)MultiCamSession.mbStrength; tj["frustumJitterPattern"] = (double)MultiCamSession.jitter;
                gj["taa"] = MultiCamSession.taa != null ? (object)tj : null;
                var pb = new Json.Obj();
                foreach (var kv in MultiCamSession.postBase) pb[kv.Key] = (double)kv.Value;
                gj["post"] = pb;
                o["game"] = gj;
                if (spec.output.format == "raw" && grab != null)
                {
                    var rw = new Json.Obj();
                    rw["w"] = (double)grab.w; rw["h"] = (double)grab.h; rw["format"] = "rgba"; rw["rowOrder"] = "bottomUp";
                    o["raw"] = rw;
                }
                o["steps"] = (double)(step + 1);
                o["frames"] = (double)frame;
                o["captured_steps"] = (double)capturedSteps;
                o["renders"] = (double)renders;
                o["real_seconds"] = (double)(Time.realtimeSinceStartup - runRealStart);
                o["timescale_leaks"] = (double)leaks;
                o["warnings"] = MultiCamPlan.Strs(warnings);
                o["errors"] = MultiCamPlan.Strs(errors);
                if (ReplayModes.Is(mode))
                {
                    try { ReplayRunJson(o, final); }
                    catch (Exception e) { DevLog.Error("mcam run.json replay", e); o["replay_error"] = e.GetType().Name + ": " + e.Message; }
                }
                File.WriteAllText(Path.Combine(outDir, "run.json"), Json.Write(o));
            }
            catch (Exception e) { DevLog.Error("mcam run.json", e); }
        }

        // ---------------------------------------------------------------- finish

        void Finish(string how, bool kill)
        {
            if (finished) return;
            finished = true;
            state = how;
            bool replayMode = ReplayModes.Is(mode);
            bool endFrozen = replayMode && ReplayEndsFrozen();
            if (replayMode)
            {
                string why = "mcam " + mode + ": " + how;
                MultiCamSafety.AudioOff(why, Log);
                try { ReplayFinish(how, kill); }
                catch (Exception e) { DevLog.Error("mcam finish replay", e); try { Log("replay finish failed: " + e.GetType().Name + ": " + e.Message); } catch { } }
                MultiCamSafety.EndLeftovers(why, Log);
            }
            try { if (events != null) events.Disarm(); } catch (Exception e) { DevLog.Error("mcam finish events", e); }
            foreach (var r in rigs) { try { if (r != null) r.End(); } catch (Exception e) { DevLog.Error("mcam finish rig", e); } }
            try { if (targetsBegun) MultiCamTargets.End(); } catch (Exception e) { DevLog.Error("mcam finish targets", e); }
            try { MultiCamSession.Pop(); } catch (Exception e) { DevLog.Error("mcam finish session", e); }
            if (endFrozen)
            {
                try { MultiCamFreeze.Hold("mcam " + mode + " " + how + ": final sample"); } catch (Exception e) { DevLog.Error("mcam finish freeze", e); }
                try { Log("the game stays frozen at the final sample (REPLAY_DESIGN 2.4.8, 3.4): Time.timeScale 0 is held until 'ts 1'"); } catch { }
            }
            try { if (agrab != null) { agrab.Close(); if (agrab.error != null) errors.Add("readback: " + agrab.error); agrab = null; } } catch (Exception e) { DevLog.Error("mcam finish readback", e); }
            int written = 0;
            if (writers != null)
                foreach (var w in writers)
                {
                    if (w == null) continue;
                    try { w.Close(kill || aborted, 30000); written += w.written; if (w.error != null) errors.Add("writer " + w.name + ": " + w.error); if (w.stderr.Length > 0) Log("ffmpeg " + w.name + ": " + w.stderr); }
                    catch (Exception e) { DevLog.Error("mcam finish writer", e); }
                }
            try { if (grab != null) grab.Dispose(); } catch { }
            if (spec != null && cueDone != null)
                for (int i = 0; i < spec.cues.Count && i < cueDone.Length; i++)
                    if (!cueDone[i]) Log("cue never ran: " + spec.cues[i].at + " " + string.Join("; ", spec.cues[i].Commands().ToArray()));
            if (events != null) foreach (var n in events.NeverFired()) Log("event never fired: " + n);
            string tail = (warnings.Count > 0 ? ", " + warnings.Count + " warning(s)" : "") + (errors.Count > 0 ? ", " + errors.Count + " error(s)" : "") + " -> " + outDir;
            if (replayMode && !string.IsNullOrEmpty(replay.summary)) MultiCam.status = how + ": " + replay.summary + tail;
            else MultiCam.status = how + ": " + (step + 1) + " steps, " + frame + " frames, " + capturedSteps + " captured steps, " + written + " frames written, game " + ctx.t.ToString("0.###", IC) + " s, " +
                                   (Time.realtimeSinceStartup - runRealStart).ToString("0", IC) + " s real" + tail;
            try { Log(MultiCam.status); } catch { }
            try { FlushCsv(); } catch { }
            foreach (var c in new[] { meta, posesCsv, eventsCsv, tracksCsv, rigCsv, viewsCsv }) { try { if (c != null) c.Close(); } catch { } }
            WriteRun(true);
            Progress(true);
            try { if (logW != null) logW.Close(); } catch { }
            logW = null;
            if (MultiCam.runner == this) MultiCam.runner = null;
            try { if (this != null && gameObject != null) Destroy(gameObject); } catch { }
        }

        // ---------------------------------------------------------------- evaluation without a run (mcam show / pose)

        internal static bool EvaluateNow(MultiCamPlan plan, string name, double t, out CamPose pose, out string error)
        {
            pose = default(CamPose); error = null;
            int idx;
            if (!plan.camIndex.TryGetValue(name, out idx)) { error = "unknown camera '" + name + "'"; return false; }
            CamPose[] all;
            if (!EvaluateAt(plan, t, null, out all, out error)) return false;
            pose = all[idx];
            return true;
        }

        // One-shot evaluation of every camera at time t (no smoothing history); `during` runs while the targets are begun.
        // With an srec viewer loaded, event times come from the recording (the spec's predictions fill events it never had).
        internal static bool EvaluateAt(MultiCamPlan plan, double t, Action<MultiCamContext, CamPose[]> during, out CamPose[] poses, out string error)
        {
            poses = null; error = null;
            var spec = plan.spec;
            var cam = CameraControl.Current;
            if (cam == null) { error = "no gameplay camera"; return false; }
            int n = spec.cameras.Count;
            var rigs = new ICamRig[n];
            var ps = new CamPose[n];
            var done = new bool[n];
            var ev = new MultiCamEvents(spec, plan);
            var ctx = new MultiCamContext();
            ctx.spec = spec; ctx.camera = cam; ctx.screenW = Screen.width; ctx.screenH = Screen.height;
            ctx.aspect = Screen.height > 0 ? Screen.width / (float)Screen.height : 16f / 9f;
            ctx.fps = spec.timing.fps; ctx.target = MultiCamTargets.Sample; ctx.eventTime = ev.TimeOf; ctx.preview = true;
            var viewer = SceneReplayer.current;
            if (viewer != null && viewer.active) ctx.eventTime = en => { double v = viewer.EventTime(en); return double.IsNaN(v) ? ev.TimeOf(en) : v; };
            ctx.rate = plan.segs[0].rate; ctx.speed = plan.segs[0].speed;
            ctx.poseOf = (string nm, out CamPose p) =>
            {
                p = default(CamPose);
                int i;
                if (nm == null || !plan.camIndex.TryGetValue(nm, out i) || !done[i]) return false;
                p = ps[i];
                return true;
            };
            ctx.log = m => DevLog.Write("[mcam] " + m);
            var rep = new SpecReport();
            bool begun = false;
            try
            {
                MultiCamTargets.Begin(spec, rep);
                begun = true;
                ctx.step = 0; ctx.t = t; ctx.dt = 0; ctx.frame = 0; ctx.sub = 0; ctx.captured = true;
                MultiCamRig.SampleGame(cam, ctx);
                MultiCamTargets.Step(ctx);
                for (int i = 0; i < n; i++) { rigs[i] = MultiCamRig.Create(spec.cameras[i]); rigs[i].Begin(ctx); }
                foreach (int i in plan.order)
                {
                    var p = rigs[i].Evaluate(ctx);
                    if (!p.ok) { int fl = p.flags; p = ctx.game.rendered; p.ok = true; p.flags = fl | PoseFlags.Fallback; }
                    if (p.fov <= 0) p.fov = cam.fieldOfView;
                    ps[i] = p; done[i] = true;
                }
                poses = ps;
                if (during != null) during(ctx, ps);
                if (!rep.ok) error = rep.Summary(5);
                return true;
            }
            catch (Exception e) { error = "evaluation failed: " + e.GetType().Name + ": " + e.Message; return false; }
            finally
            {
                foreach (var r in rigs) { try { if (r != null) r.End(); } catch { } }
                if (begun) { try { MultiCamTargets.End(); } catch { } }
            }
        }
    }

    // ------------------------------------------------------------------ session push / pop

    internal static class MultiCamSession
    {
        public static bool active, locked, attached, cleanedBySession;
        static float timeScale = 1f, gmTimeScale = 1f, fixedDt = 0.02f, zoomBefore = 1f;
        static int captureFps, vsyncReal, vsyncVirtual = 1, targetFps = -1, resW, resH;
        static bool runInBg, fcAuto, quietBefore, renderSwapped, requestedSet, fsmLoggingSet, fsmLoggingBefore, cullingSet, cullingBefore, resSet, resFull, heldBefore;
        static IEditorRenderer rendererBefore;
        static Backend requestedBefore;
        static DevCore core;
        public static Camera cam;
        static CameraScript script;
        static bool scriptPaused, handshake = true, customFx = true;
        public static float camFov = MultiCamConst.GameFov, camNear = MultiCamConst.GameNear;
        public static D11PDTemporalReprojection3 taa;
        public static float fbMin, fbMax, mbStrength;
        public static int jitter;
        static bool taaEnabled = true;
        public static readonly Dictionary<string, float> postBase = new Dictionary<string, float>();
        public static MultiCamLook look;
        static readonly List<KeyValuePair<Rigidbody, RigidbodyInterpolation>> interp = new List<KeyValuePair<Rigidbody, RigidbodyInterpolation>>();
        static readonly List<SubsceneCuller> kept = new List<SubsceneCuller>();
        public static readonly List<Renderer> hidden = new List<Renderer>();
        static FieldInfo fAlways;

        public static void Push(MultiCamSpec spec, DevCore c)
        {
            if (active) Pop();
            Try("hide", MultiCamHide.Restore);
            core = c ?? DevCore.Instance;
            if (spec.replay != null) Try("audio guard", AudioCapture.Ensure);
            active = true;
            locked = spec.session.lockInput;
            heldBefore = MultiCamFreeze.holding;
            MultiCamFreeze.Release("mcam session");
            timeScale = Time.timeScale;
            try { gmTimeScale = GameManager.TimeScale; } catch { gmTimeScale = timeScale; }
            fixedDt = Time.fixedDeltaTime;
            captureFps = Time.captureFramerate;
            runInBg = Application.runInBackground;
            vsyncReal = QualitySettings.vSyncCount;
            try { vsyncVirtual = FramerateController.GetVirtualVSyncCount(); fcAuto = FramerateController.automatic; } catch { vsyncVirtual = vsyncReal; }
            targetFps = Application.targetFrameRate;
            quietBefore = CameraControl.quiet;
            zoomBefore = CameraControl.zoom;
            resSet = false;
            if (spec.session.runInBackground) Application.runInBackground = true;
            if (!spec.session.vsync)
            {
                try { FramerateController.automatic = false; FramerateController.SetVSyncCount(0); } catch { }
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
            CameraControl.quiet = spec.session.quietLog;
            CameraControl.zoom = 1f;
            if (CameraControl.free) CameraControl.SetFree(false);
            if (spec.session.clean)
            {
                if (!MultiCamClean.on) { MultiCamClean.Enable(core); cleanedBySession = true; }
                rendererBefore = RenderHost.renderer;
                RenderHost.renderer = new MultiCamNullRenderer { inner = rendererBefore };
                renderSwapped = true;
                requestedBefore = RenderHost.requested;
                if (RenderHost.requested == Backend.Auto) RenderHost.requested = RenderHost.active;
                requestedSet = true;
            }
        }

        public static void Attach(Camera c, MultiCamSpec spec)
        {
            cam = c;
            attached = true;
            script = c.GetComponent<CameraScript>();
            if (script != null)
            {
                scriptPaused = script.paused; handshake = script.bHandshakeEffect; customFx = script.bCustomEffects;
                script.bHandshakeEffect = spec.session.handshake;
                script.bCustomEffects = spec.session.shake;
            }
            camFov = c.fieldOfView;
            camNear = c.nearClipPlane;
            taa = c.GetComponent<D11PDTemporalReprojection3>();
            if (taa != null) { fbMin = taa.feedbackMin; fbMax = taa.feedbackMax; mbStrength = taa.motionBlurStrength; jitter = taa.frustumJitterPattern; taaEnabled = taa.enabled; }
            postBase.Clear();
            var pc = c.GetComponent<D11PDPostCombined>();
            if (pc != null)
            {
                postBase["gamma_gamma"] = pc.gamma_gamma; postBase["post_inBlack"] = pc.post_inBlack; postBase["post_inWhite"] = pc.post_inWhite;
                postBase["post_gamma"] = pc.post_gamma; postBase["post_blacklift"] = pc.post_blacklift; postBase["saturationAdjustment"] = pc.saturationAdjustment;
            }
            look = c.gameObject.AddComponent<MultiCamLook>();
            if (!CameraControl.BeginCapture()) throw new Exception("free camera could not take the gameplay camera");
        }

        public static int JitterFor(int n) { return n <= 4 ? 6 : n <= 8 ? 11 : n <= 16 ? 12 : 13; }

        public static void TaaBegin(RenderSpec rs, int n)
        {
            if (taa == null) return;
            int j = rs.jitter > 0 ? rs.jitter : rs.taa == "keep" ? jitter : JitterFor(n);
            if (taa.frustumJitterPattern != j) taa.frustumJitterPattern = j;
            float mb = rs.motionBlur >= 0f ? rs.motionBlur : mbStrength;
            if (taa.motionBlurStrength != mb) taa.motionBlurStrength = mb;
        }

        public static void TaaFrame(RenderSpec rs, int r, int w)
        {
            if (taa == null) return;
            if (rs.taa == "reset" && r >= w)
            {
                int k = r - w + 1;
                float f = (k - 1f) / k;
                taa.feedbackMin = f; taa.feedbackMax = f;
                return;
            }
            taa.feedbackMin = fbMin; taa.feedbackMax = fbMax;
            if (rs.taa == "toggle")
            {
                if (r == w) taa.enabled = false;
                else if (r == w + 1 && taaEnabled) taa.enabled = true;
            }
        }

        public static void TaaEnd()
        {
            if (taa == null) return;
            taa.feedbackMin = fbMin; taa.feedbackMax = fbMax;
            if (taaEnabled && !taa.enabled) taa.enabled = true;
        }

        public static void SetInterpolation(Rigidbody rb)
        {
            foreach (var kv in interp) if (kv.Key == rb) return;
            interp.Add(new KeyValuePair<Rigidbody, RigidbodyInterpolation>(rb, rb.interpolation));
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        static FastList<SubsceneCuller> AlwaysList()
        {
            try
            {
                if (fAlways == null) fAlways = typeof(SubsceneCullingManager).GetField("alwaysLoaded", BindingFlags.Instance | BindingFlags.NonPublic);
                return fAlways != null ? fAlways.GetValue(SubsceneCullingManager.instance) as FastList<SubsceneCuller> : null;
            }
            catch { return null; }
        }

        public static bool IsKept(SubsceneCuller c)
        {
            if (kept.Contains(c)) return true;
            if (c.AlwaysLoaded) return true;
            var l = AlwaysList();
            return l != null && l.Contains(c);
        }

        public static bool Keep(SubsceneCuller c)
        {
            if (c == null || IsKept(c)) return false;
            SubsceneCullingManager.instance.AddAlwaysLoaded(c);
            kept.Add(c);
            return true;
        }

        public static List<string> KeptNames()
        {
            var r = new List<string>();
            foreach (var c in kept) { try { r.Add(c != null ? c.name : "(destroyed)"); } catch { r.Add("?"); } }
            return r;
        }

        public static void SetCulling(bool on)
        {
            var m = SubsceneCullingManager.instance;
            if (!cullingSet) { cullingBefore = m.CullingActive; cullingSet = true; }
            m.CullingActive = on;
        }

        public static void EnableFsmLogging()
        {
            if (!fsmLoggingSet) { fsmLoggingBefore = FsmLog.LoggingEnabled; fsmLoggingSet = true; }
            FsmLog.LoggingEnabled = true;
        }

        public static void SetResolution(int w, int h)
        {
            if (!resSet) { resW = Screen.width; resH = Screen.height; resFull = Screen.fullScreen; resSet = true; }
            Screen.SetResolution(w, h, Screen.fullScreen);
        }

        public static void ShowHidden()
        {
            foreach (var r in hidden) { try { if (r != null) r.enabled = true; } catch { } }
            hidden.Clear();
        }

        public static void Pop()
        {
            if (!active) return;
            Try("hidden", ShowHidden);
            Try("hide", MultiCamHide.Restore);
            Try("camera control", CameraControl.EndCapture);
            Try("camera script", () =>
            {
                if (script != null) { script.paused = scriptPaused; script.bHandshakeEffect = handshake; script.bCustomEffects = customFx; }
                if (cam != null && attached) { cam.fieldOfView = camFov; cam.nearClipPlane = camNear; }
            });
            Try("taa", () =>
            {
                if (taa != null) { taa.feedbackMin = fbMin; taa.feedbackMax = fbMax; taa.motionBlurStrength = mbStrength; taa.frustumJitterPattern = jitter; taa.enabled = taaEnabled; }
            });
            Try("look", () =>
            {
                MultiCamLook.post = null; MultiCamLook.fogDelta = 0f; MultiCamLook.realTime = false; MultiCamLook.clock = 0.0;
                if (look != null) { look.RestoreBase(); UnityEngine.Object.Destroy(look); }
            });
            Try("interpolation", () =>
            {
                foreach (var kv in interp) if (kv.Key != null) kv.Key.interpolation = kv.Value;
                interp.Clear();
            });
            Try("keep-loaded", () =>
            {
                var m = SubsceneCullingManager.instance;
                foreach (var c in kept) if (c != null) m.RemoveAlwaysLoaded(c);
                kept.Clear();
                if (cullingSet) { m.CullingActive = cullingBefore; cullingSet = false; }
            });
            Try("fsm log", () => { if (fsmLoggingSet) { FsmLog.LoggingEnabled = fsmLoggingBefore; fsmLoggingSet = false; } });
            Try("time", () =>
            {
                Time.captureFramerate = captureFps;
                Time.fixedDeltaTime = fixedDt;
                MultiCamFreeze.Restore(timeScale, gmTimeScale, heldBefore, "session pop: the game was frozen when the mcam run started");
                heldBefore = false;
            });
            Try("vsync", () =>
            {
                try { FramerateController.SetVSyncCount(vsyncVirtual); FramerateController.automatic = fcAuto; } catch { }
                QualitySettings.vSyncCount = vsyncReal;
                Application.targetFrameRate = targetFps;
                Application.runInBackground = runInBg;
            });
            Try("resolution", () => { if (resSet) { Screen.SetResolution(resW, resH, resFull); resSet = false; } });
            Try("renderer", () =>
            {
                if (renderSwapped) { RenderHost.renderer = rendererBefore ?? RenderHost.glRenderer; renderSwapped = false; }
                if (requestedSet) { RenderHost.requested = requestedBefore; requestedSet = false; }
                RenderHost.RequestProbe("mcam session end", 3);
            });
            Try("clean", () => { if (cleanedBySession) { MultiCamClean.Restore(core); cleanedBySession = false; } });
            Try("input", () => { InputCapture.keyboard = false; CameraControl.quiet = quietBefore; CameraControl.zoom = zoomBefore; });
            active = false; locked = false; attached = false;
            cam = null; script = null; taa = null; look = null;
            DevLog.Write("[mcam] session restored");
        }

        static void Try(string what, Action a)
        {
            try { a(); } catch (Exception e) { DevLog.Error("mcam restore " + what, e); }
        }
    }

    // ------------------------------------------------------------------ freeze hold (REPLAY_DESIGN 2.4.8, 3.4)
    // After a record, replay, take, golden or passes run the game stays frozen at the final sample. A one-time
    // Time.timeScale = 0 does not hold: TimeScaleController.Update on _Globals (FP/TimeScaleController.cs) sets
    // GameManager.TimeScale to 1 every frame while captureFramerate is 0, and GameManager.UpdateTimeScale (LateUpdate -615,
    // FP/GameManager.cs:545-566) writes Time.timeScale whenever its computed value differs from its private latch
    // oldTimeScale, which the frozen run left at 0. Hold therefore sets the latch to the value the game computes next (1,
    // as for a console 'ts 0') and re-asserts Time.timeScale 0 in RuntimeCallbacks.lateUpdate (-400, after the game's
    // write) every frame. Released by G.TimeScale (ts, the cheats slider and hotkeys), a level load or the next mcam session.
    internal static class MultiCamFreeze
    {
        public static bool holding;
        public static string reason = "";
        public static int reforced;
        static float since;
        static bool hooked;
        static FieldInfo fLatch;

        public static void Hold(string why)
        {
            bool was = holding;
            holding = true;
            reason = why ?? "";
            if (!was) { since = Time.realtimeSinceStartup; reforced = 0; }
            Time.timeScale = 0f;
            try { GameManager.TimeScale = 0f; } catch { }
            Latch(1f);
            if (!hooked) { RuntimeCallbacks.lateUpdate += Tick; hooked = true; }
            if (!was) DevLog.Write("[mcam] game frozen (" + reason + "): Time.timeScale 0 held every frame until 'ts <value>', a level load or the next mcam run");
        }

        public static void Release(string why)
        {
            if (hooked) { RuntimeCallbacks.lateUpdate -= Tick; hooked = false; }
            if (!holding) return;
            holding = false;
            DevLog.Write("[mcam] freeze hold released (" + why + ") after " + (Time.realtimeSinceStartup - since).ToString("0.#", CultureInfo.InvariantCulture) + " s, " + reforced + " re-force(s) of a foreign timeScale write");
        }

        public static void Restore(float timeScale, float gmTimeScale, bool held, string why)
        {
            if (held || timeScale == 0f) { Hold(why); return; }
            Time.timeScale = timeScale;
            try { GameManager.TimeScale = gmTimeScale == timeScale ? gmTimeScale : timeScale; } catch { }
        }

        public static string Status() { return holding ? "frozen (" + reason + "; ts 1 resumes)" : ""; }

        static void Latch(float v)
        {
            try
            {
                var gm = PersistentBehaviour<GameManager>.instance;
                if (gm == null) return;
                if (fLatch == null) fLatch = typeof(GameManager).GetField("oldTimeScale", BindingFlags.Instance | BindingFlags.NonPublic);
                if (fLatch != null) fLatch.SetValue(gm, v);
            }
            catch (Exception e) { DevLog.Error("mcam freeze latch", e); }
        }

        static void Tick()
        {
            try
            {
                if (!holding) { Release("not holding"); return; }
                bool loading = false;
                try { loading = GameManager.IsLoading(); } catch { }
                if (loading) { Release("level load"); return; }
                if (Time.timeScale == 0f) return;
                reforced++;
                if (reforced <= 5 || reforced % 100 == 0) DevLog.Write("[mcam] freeze hold: timeScale " + Time.timeScale.ToString("0.###", CultureInfo.InvariantCulture) + " written by the game (pause menu or time scale script), re-forced 0 (" + reforced + ")");
                Time.timeScale = 0f;
            }
            catch (Exception e) { DevLog.Error("mcam freeze hold", e); }
        }
    }

    // ------------------------------------------------------------------ clean frames (no editor.cfg writes)

    internal static class MultiCamClean
    {
        public static bool on;
        static bool panel, hud, triggers, solids, savepoints, labels, hiddenMarks, pick, gizmo, selection, board;
        static FieldInfo fHud;

        static FieldInfo HudField()
        {
            if (fHud == null) fHud = typeof(DevCore).GetField("hud", BindingFlags.Instance | BindingFlags.NonPublic);
            return fHud;
        }

        public static bool GetHud(DevCore core)
        {
            var f = HudField();
            return f != null && core != null && (bool)f.GetValue(core);
        }

        public static void SetHud(DevCore core, bool v)
        {
            var f = HudField();
            if (f != null && core != null) f.SetValue(core, v);
        }

        public static void Enable(DevCore core)
        {
            if (on) return;
            if (core == null) core = DevCore.Instance;
            if (core == null) return;
            var ov = core.Overlay;
            panel = core.panel; hud = GetHud(core);
            triggers = ov.showTriggers; solids = ov.showSolids; savepoints = ov.showSavepoints; labels = ov.showLabels; hiddenMarks = ov.showHidden;
            pick = WorldPick.mode; gizmo = TransformGizmo.globalOn; selection = SelectionOverlay.enabled; board = RenderDiag.board;
            core.panel = false;
            SetHud(core, false);
            ov.showTriggers = false; ov.showSolids = false; ov.showSavepoints = false; ov.showLabels = false; ov.showHidden = false;
            WorldPick.mode = false; TransformGizmo.globalOn = false; SelectionOverlay.enabled = false; RenderDiag.board = false;
            ov.ForceRefresh();
            on = true;
        }

        public static void Restore(DevCore core)
        {
            if (!on) return;
            if (core == null) core = DevCore.Instance;
            on = false;
            if (core == null) return;
            var ov = core.Overlay;
            core.panel = panel;
            SetHud(core, hud);
            ov.showTriggers = triggers; ov.showSolids = solids; ov.showSavepoints = savepoints; ov.showLabels = labels; ov.showHidden = hiddenMarks;
            WorldPick.mode = pick; TransformGizmo.globalOn = gizmo; SelectionOverlay.enabled = selection; RenderDiag.board = board;
            ov.ForceRefresh();
        }
    }

    internal sealed class MultiCamNullRenderer : IEditorRenderer
    {
        public IEditorRenderer inner;
        public string Name { get { return "suppressed by mcam"; } }
        public void Invalidate(string reason) { }
        public void DestroyResources() { }
        public bool Submit(Draw list, Color? probe, ref RenderStats stats) { return true; }
        public float Measure(string s, int size) { return inner != null ? inner.Measure(s, size) : (s ?? "").Length * size * 0.55f; }
        public bool ResourcesOk(out string report) { report = "suppressed by mcam"; return true; }
    }

    // ------------------------------------------------------------------ per-render look (post overrides, _RealTime)

    public sealed class MultiCamLook : MonoBehaviour
    {
        public static PostSpec post;
        public static float fogDelta;
        public static bool realTime;
        public static double clock;

        sealed class Tr
        {
            public float b, w;
            public bool has;
            public float Base(float cur) { if (!has || cur != w) { b = cur; has = true; } return b; }
        }

        D11PDPostCombined pc;
        readonly Tr gamma = new Tr(), black = new Tr(), pgamma = new Tr(), inWhite = new Tr(), sat = new Tr(), wide = new Tr(), chroma = new Tr(), barrel = new Tr(), water = new Tr(), fogS = new Tr(), fogE = new Tr();
        float[] bloomBase, bloomWritten;
        int realTimeId = -1;
        bool failed, fogChecked;

        void OnPreRender()
        {
            try
            {
                if (realTime)
                {
                    if (realTimeId < 0) realTimeId = Shader.PropertyToID("_RealTime");
                    double t = clock;
                    Shader.SetGlobalVector(realTimeId, new Vector4((float)t, (float)(1000.0 * t), (float)Math.Sin(t), (float)(t - Math.Floor(t))));
                }
                var p = post;
                if (pc == null) pc = GetComponent<D11PDPostCombined>();
                if (pc != null && pc.enabled) ApplyPost(p);
                ApplyFog(p != null ? fogDelta : 0f);
            }
            catch (Exception e) { if (!failed) { failed = true; DevLog.Error("mcam look", e); } }
        }

        void OnPostRender()
        {
            if (fogChecked || !fogS.has || fogDelta == 0f) return;
            fogChecked = true;
            if (RenderSettings.fogStartDistance != fogS.w) DevLog.Write("[mcam] look order: fog start was rewritten after MultiCamLook.OnPreRender (fogMatch/fogOffset ineffective)");
        }

        static bool Delta(float v) { return v != 0f; }

        void ApplyPost(PostSpec p)
        {
            float bright = p != null ? p.brightness : float.NaN;
            if (!float.IsNaN(bright) || gamma.has) { float cur = pc.gamma_gamma, b = gamma.Base(cur), v = float.IsNaN(bright) ? b : bright; if (v != cur) pc.gamma_gamma = v; gamma.w = v; }
            float d = p != null ? p.blacklift : 0f;
            if (Delta(d) || black.has) { float cur = pc.post_blacklift, b = black.Base(cur), v = b + d; if (v != cur) pc.post_blacklift = v; black.w = v; }
            d = p != null ? p.postGamma : 0f;
            if (Delta(d) || pgamma.has) { float cur = pc.post_gamma, b = pgamma.Base(cur), v = b + d; if (v != cur) pc.post_gamma = v; pgamma.w = v; }
            float s = p != null ? p.inWhiteScale : 1f;
            if (s != 1f || inWhite.has) { float cur = pc.post_inWhite, b = inWhite.Base(cur), v = b * s; if (v != cur) pc.post_inWhite = v; inWhite.w = v; }
            d = p != null ? p.saturation : 0f;
            if (Delta(d) || sat.has) { float cur = pc.saturationAdjustment, b = sat.Base(cur), v = b + d; if (v != cur) pc.saturationAdjustment = v; sat.w = v; }
            s = p != null ? p.bloomScale : 1f;
            if (s != 1f || wide.has) { float cur = pc.wideparm_bloomIntensity, b = wide.Base(cur), v = b * s; if (v != cur) pc.wideparm_bloomIntensity = v; wide.w = v; }
            if (s != 1f || bloomWritten != null)
            {
                var cur = pc.hdrparm_bloomIntensity;
                if (cur != null)
                {
                    if (bloomBase == null || !SameArr(cur, bloomWritten)) bloomBase = (float[])cur.Clone();
                    var v = new float[bloomBase.Length];
                    for (int i = 0; i < v.Length; i++) v[i] = bloomBase[i] * s;
                    if (!SameArr(v, cur)) pc.hdrparm_bloomIntensity = v;
                    bloomWritten = v;
                }
            }
            s = p != null ? p.distortionScale : 1f;
            if (s != 1f || chroma.has) { float cur = pc.colorDistortionPx720p, b = chroma.Base(cur), v = b * s; if (v != cur) pc.colorDistortionPx720p = v; chroma.w = v; }
            if (s != 1f || barrel.has) { float cur = pc.barrelValue, b = barrel.Base(cur), v = b * s; if (v != cur) pc.barrelValue = v; barrel.w = v; }
            bool nw = p != null && p.noWater;
            if (nw || water.has) { float cur = pc.waterDist, b = water.Base(cur), v = nw ? 0f : b; if (v != cur) pc.waterDist = v; water.w = v; }
        }

        void ApplyFog(float d)
        {
            if (Delta(d) || fogS.has) { float cur = RenderSettings.fogStartDistance, b = fogS.Base(cur), v = b + d; if (v != cur) RenderSettings.fogStartDistance = v; fogS.w = v; }
            if (Delta(d) || fogE.has) { float cur = RenderSettings.fogEndDistance, b = fogE.Base(cur), v = b + d; if (v != cur) RenderSettings.fogEndDistance = v; fogE.w = v; }
        }

        static bool SameArr(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public void RestoreBase()
        {
            try
            {
                if (pc == null) pc = GetComponent<D11PDPostCombined>();
                if (pc != null)
                {
                    if (gamma.has && pc.gamma_gamma == gamma.w) pc.gamma_gamma = gamma.b;
                    if (black.has && pc.post_blacklift == black.w) pc.post_blacklift = black.b;
                    if (pgamma.has && pc.post_gamma == pgamma.w) pc.post_gamma = pgamma.b;
                    if (inWhite.has && pc.post_inWhite == inWhite.w) pc.post_inWhite = inWhite.b;
                    if (sat.has && pc.saturationAdjustment == sat.w) pc.saturationAdjustment = sat.b;
                    if (wide.has && pc.wideparm_bloomIntensity == wide.w) pc.wideparm_bloomIntensity = wide.b;
                    if (bloomWritten != null && bloomBase != null && SameArr(pc.hdrparm_bloomIntensity, bloomWritten)) pc.hdrparm_bloomIntensity = bloomBase;
                    if (chroma.has && pc.colorDistortionPx720p == chroma.w) pc.colorDistortionPx720p = chroma.b;
                    if (barrel.has && pc.barrelValue == barrel.w) pc.barrelValue = barrel.b;
                    if (water.has && pc.waterDist == water.w) pc.waterDist = water.b;
                }
                if (fogS.has && RenderSettings.fogStartDistance == fogS.w) RenderSettings.fogStartDistance = fogS.b;
                if (fogE.has && RenderSettings.fogEndDistance == fogE.w) RenderSettings.fogEndDistance = fogE.b;
            }
            catch (Exception e) { DevLog.Error("mcam look restore", e); }
        }
    }

    // ------------------------------------------------------------------ readback, frame writers, CSV

    internal sealed class MultiCamGrab
    {
        Texture2D tex;
        public int w, h;
        readonly bool jpg;
        readonly int quality;

        public MultiCamGrab(int width, int height, bool jpeg, int q)
        {
            w = width; h = height; jpg = jpeg; quality = q;
            tex = new Texture2D(w, h, jpg ? TextureFormat.RGB24 : TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
        }

        public object Grab()
        {
            var prev = RenderTexture.active;
            RenderTexture.active = null;
            try { tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false); }
            finally { RenderTexture.active = prev; }
            return jpg ? (object)tex.EncodeToJPG(quality) : tex.GetPixels32();
        }

        public void Dispose() { if (tex != null) UnityEngine.Object.Destroy(tex); tex = null; }
    }

    internal sealed class MultiCamFrameWriter
    {
        struct Job { public string path; public byte[] bytes; public Color32[] px; public IntPtr native; public int len, slot; }
        readonly Queue<Job> q = new Queue<Job>();
        readonly object gate = new object();
        readonly int limit;
        public readonly string name;
        Thread thread;
        bool closing;
        public volatile string error;
        public int written;
        public double waitMs;
        System.Diagnostics.Process proc;
        Stream pipe;
        readonly StringBuilder err = new StringBuilder();
        byte[] chunk;

        public MultiCamFrameWriter(string camName, int queueLimit) : this(camName, queueLimit, true) { }

        MultiCamFrameWriter(string camName, int queueLimit, bool start)
        {
            name = camName; limit = Math.Max(1, queueLimit);
            if (start) StartThread();
        }

        void StartThread()
        {
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "mcam writer " + name;
            thread.Start();
        }

        // output.ffmpeg: a full path, or a program name looked up in the folders of PATH (name and name.exe)
        public static string ResolveExe(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return exe;
            try
            {
                if (Path.IsPathRooted(exe) || exe.IndexOf('/') >= 0 || exe.IndexOf('\\') >= 0) return exe;
                string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in path.Split(Path.PathSeparator))
                {
                    string d = dir.Trim().Trim('"');
                    if (d.Length == 0) continue;
                    foreach (var name in new[] { exe, exe + ".exe" })
                    {
                        string f = Path.Combine(d, name);
                        if (File.Exists(f)) return f;
                    }
                }
            }
            catch (Exception) { }
            return exe;
        }

        public static MultiCamFrameWriter Pipe(string camName, int queueLimit, string ffmpeg, string args)
        {
            var w = new MultiCamFrameWriter(camName, queueLimit, false);
            var psi = new System.Diagnostics.ProcessStartInfo(ResolveExe(ffmpeg), args);
            psi.UseShellExecute = false; psi.RedirectStandardInput = true; psi.RedirectStandardError = true; psi.RedirectStandardOutput = false; psi.CreateNoWindow = true;
            w.proc = System.Diagnostics.Process.Start(psi);
            w.pipe = w.proc.StandardInput.BaseStream;
            var et = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = w.proc.StandardError.ReadLine()) != null) lock (w.err) { if (w.err.Length < 8000) w.err.Append(line).Append(" | "); }
                }
                catch { }
            });
            et.IsBackground = true;
            et.Start();
            w.StartThread();
            return w;
        }

        public string stderr { get { lock (err) return err.ToString(); } }

        public int Depth { get { lock (gate) return q.Count; } }

        public void Add(string path, object data)
        {
            Enqueue(new Job { path = path, bytes = data as byte[], px = data as Color32[], slot = -1 });
        }

        public void AddNative(string path, IntPtr data, int len, int slot)
        {
            Enqueue(new Job { path = path, native = data, len = len, slot = slot });
        }

        void Enqueue(Job j)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            lock (gate)
            {
                if (closing && j.slot >= 0) { ReadbackNative.rb_release(j.slot); return; }
                while (q.Count >= limit && error == null && !closing) Monitor.Wait(gate, 50);
                q.Enqueue(j);
                Monitor.PulseAll(gate);
            }
            waitMs += sw.Elapsed.TotalMilliseconds;
        }

        void Loop()
        {
            while (true)
            {
                Job j;
                lock (gate)
                {
                    while (q.Count == 0 && !closing) Monitor.Wait(gate);
                    if (q.Count == 0) return;
                    j = q.Dequeue();
                    Monitor.PulseAll(gate);
                }
                try { Write(j); written++; }
                catch (Exception e) { if (error == null) error = e.GetType().Name + ": " + e.Message; }
                finally { if (j.slot >= 0) ReadbackNative.rb_release(j.slot); }
            }
        }

        void Write(Job j)
        {
            if (j.native != IntPtr.Zero)
            {
                if (pipe != null) WriteNative(pipe, j.native, j.len);
                else using (var fs = new FileStream(j.path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20)) WriteNative(fs, j.native, j.len);
                return;
            }
            if (pipe != null)
            {
                if (j.px != null) WritePixels(pipe, j.px);
                return;
            }
            if (j.bytes != null) { File.WriteAllBytes(j.path, j.bytes); return; }
            if (j.px != null)
            {
                using (var fs = new FileStream(j.path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20)) WritePixels(fs, j.px);
            }
        }

        void WriteNative(Stream s, IntPtr data, int total)
        {
            if (chunk == null) chunk = new byte[1 << 20];
            int off = 0;
            while (off < total)
            {
                int n = Math.Min(chunk.Length, total - off);
                Marshal.Copy(new IntPtr(data.ToInt64() + off), chunk, 0, n);
                s.Write(chunk, 0, n);
                off += n;
            }
        }

        unsafe void WritePixels(Stream s, Color32[] px)
        {
            int total = px.Length * 4;
            if (chunk == null) chunk = new byte[1 << 20];
            fixed (Color32* p = px)
            {
                byte* b = (byte*)p;
                int off = 0;
                while (off < total)
                {
                    int n = Math.Min(chunk.Length, total - off);
                    Marshal.Copy((IntPtr)(b + off), chunk, 0, n);
                    s.Write(chunk, 0, n);
                    off += n;
                }
            }
        }

        public void Close(bool kill, int timeoutMs)
        {
            lock (gate)
            {
                closing = true;
                if (kill)
                {
                    foreach (var j in q) if (j.slot >= 0) ReadbackNative.rb_release(j.slot);
                    q.Clear();
                }
                Monitor.PulseAll(gate);
            }
            if (kill && proc != null) { try { if (!proc.HasExited) proc.Kill(); } catch { } }
            if (thread != null && !thread.Join(timeoutMs)) { if (error == null) error = "writer did not finish within " + timeoutMs + " ms"; }
            if (proc != null)
            {
                try { if (pipe != null) pipe.Close(); } catch { }
                try { if (!proc.WaitForExit(kill ? 2000 : 60000)) { proc.Kill(); if (error == null) error = "ffmpeg did not exit; killed"; } } catch { }
                try { if (proc.HasExited && proc.ExitCode != 0 && error == null && !kill) error = "ffmpeg exit code " + proc.ExitCode; } catch { }
            }
        }
    }

    internal sealed class MultiCamCsv
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly char[] Special = { ',', '"', '\n', '\r' };
        readonly StreamWriter w;
        readonly StringBuilder sb = new StringBuilder(512);
        bool first = true;

        public MultiCamCsv(string path, string header)
        {
            w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16);
            w.Write(header);
            w.Write('\n');
        }

        void Sep() { if (!first) sb.Append(','); first = false; }

        public MultiCamCsv S(string v)
        {
            Sep();
            if (string.IsNullOrEmpty(v)) return this;
            if (v.IndexOfAny(Special) >= 0) sb.Append('"').Append(v.Replace("\"", "\"\"").Replace('\n', ' ').Replace('\r', ' ')).Append('"');
            else sb.Append(v);
            return this;
        }

        public MultiCamCsv I(int v) { Sep(); sb.Append(v.ToString(IC)); return this; }

        public MultiCamCsv D(double v, string f) { Sep(); if (!double.IsNaN(v) && !double.IsInfinity(v)) sb.Append(v.ToString(f, IC)); return this; }

        public MultiCamCsv V(Vector3 v, string f) { return D(v.x, f).D(v.y, f).D(v.z, f); }

        public MultiCamCsv Q(Quaternion q) { return D(q.x, "0.#######").D(q.y, "0.#######").D(q.z, "0.#######").D(q.w, "0.#######"); }

        public MultiCamCsv E() { Sep(); return this; }

        public void End() { sb.Append('\n'); w.Write(sb.ToString()); sb.Length = 0; first = true; }

        public void Flush() { try { w.Flush(); } catch { } }

        public void Close() { try { w.Flush(); w.Close(); } catch { } }

        public static List<string> Split(string line)
        {
            var r = new List<string>();
            var cur = new StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (q)
                {
                    if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else q = false; }
                    else cur.Append(c);
                }
                else if (c == '"') q = true;
                else if (c == ',') { r.Add(cur.ToString()); cur.Length = 0; }
                else cur.Append(c);
            }
            r.Add(cur.ToString());
            return r;
        }
    }

    // ------------------------------------------------------------------ events: trigger sources and time references

    internal sealed class MultiCamEvents
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static FieldInfo fConns;
        const string TapName = "InsideDev.tap";

        sealed class Src
        {
            public EventSpec spec;
            public double fired = double.NaN, predicted = double.NaN, lastMatch = double.NegativeInfinity;
            public int matches;
            public bool armed, prev, prevKnown, useLog, warned;
            public GameObject go;
            public readonly List<double> fires = new List<double>();
            public readonly List<KeyValuePair<SignalOut, SignalConnection>> taps = new List<KeyValuePair<SignalOut, SignalConnection>>();
            public int tapVersion = -1;
            public readonly List<PlayMakerFSM> fsms = new List<PlayMakerFSM>();
            public readonly Dictionary<int, string> fsmState = new Dictionary<int, string>();
            public readonly Dictionary<int, int> logIndex = new Dictionary<int, int>();
            public IsTriggeredByProbe probe;
            public Action<IsTriggeredByProbe> onProbe;
            public Component comp;
            public FieldInfo field;
            public PropertyInfo prop;
            public bool countOf;
            public Vector3 lastVel, lastCam;
            public bool hasVel, hasCam;
            public string lastName;
        }

        struct Pending { public Src src; public string source, detail; }

        readonly List<Src> srcs = new List<Src>();
        readonly Dictionary<string, Src> byName = new Dictionary<string, Src>();
        readonly List<Pending> pending = new List<Pending>();
        public Action<string, string, string, string> row;
        public bool logMonitor = true;
        MultiCamContext ctx;
        SpecReport report;
        bool armed, soundOn, deathOn, loadOn, postedOn;
        Action<string, string> onPosted;
        long monitorSeen = -1;

        public MultiCamEvents(MultiCamSpec spec, MultiCamPlan plan)
        {
            var reh = plan != null ? plan.Rehearsal() : null;
            foreach (var e in spec.events)
            {
                var s = new Src { spec = e };
                if (!double.IsNaN(e.expect)) s.predicted = e.expect;
                else if (reh != null) { double v; if (reh.TryGetValue(e.name, out v)) s.predicted = v; }
                srcs.Add(s);
                if (!string.IsNullOrEmpty(e.name) && !byName.ContainsKey(e.name)) byName[e.name] = s;
            }
        }

        public double TimeOf(string name)
        {
            Src s;
            if (name == null || !byName.TryGetValue(name, out s)) return double.NaN;
            return !double.IsNaN(s.fired) ? s.fired : s.predicted;
        }

        public bool HasFired(string name)
        {
            Src s;
            return name != null && byName.TryGetValue(name, out s) && !double.IsNaN(s.fired);
        }

        public bool ReachedFired(TimeRef r, double t)
        {
            if (r == null) return false;
            if (r.absolute) return t >= r.offset - 1e-6;
            Src s;
            if (!byName.TryGetValue(r.ev, out s) || double.IsNaN(s.fired)) return false;
            return t >= s.fired + r.offset - 1e-6;
        }

        public List<string> NeverFired()
        {
            var r = new List<string>();
            foreach (var s in srcs) if (s.armed && double.IsNaN(s.fired)) r.Add(s.spec.name + " (" + s.spec.type + ")");
            return r;
        }

        public List<object> Summary()
        {
            var r = new List<object>();
            foreach (var s in srcs)
            {
                var o = new Json.Obj();
                o["name"] = s.spec.name; o["type"] = s.spec.type;
                var f = new List<object>();
                foreach (var t in s.fires) f.Add(t);
                o["fired"] = f;
                o["time"] = MultiCamPlan.Num(s.fired);
                o["predicted"] = MultiCamPlan.Num(s.predicted);
                o["armed"] = s.armed;
                r.Add(o);
            }
            return r;
        }

        // ---------------------------------------------------------------- arm / disarm

        public void Arm(MultiCamContext c, SpecReport rep, string only)
        {
            ctx = c; report = rep;
            foreach (var s in srcs)
            {
                if (only != null && s.spec.name != only) continue;
                try { ArmOne(s, rep); }
                catch (Exception e) { rep.Error("events." + s.spec.name, "arm failed: " + e.GetType().Name + ": " + e.Message); }
            }
            armed = true;
            if (EventMonitor.enabled) monitorSeen = EventMonitor.total;
        }

        static bool NeedsObject(string type) { return type == "signal" || type == "signalIn" || type == "fsm" || type == "sound" || type == "probe" || type == "field"; }

        void ArmOne(Src s, SpecReport rep)
        {
            var e = s.spec;
            string w = "events." + e.name;
            if (NeedsObject(e.type) && e.obj.Length > 0)
            {
                s.go = MultiCamTargets.ResolveObject(e.obj, w + ".obj", rep);
                if (s.go == null) { rep.Error(w + ".obj", "object not resolved: " + e.obj); return; }
            }
            switch (e.type)
            {
                case "signal":
                case "signalIn":
                    HookSignal(s);
                    if (s.taps.Count == 0) rep.Warn(w, "no matching SignalOut yet (" + e.obj + " ." + e.signal + "); re-scanned on stream changes");
                    break;
                case "fsm": HookFsm(s, rep); break;
                case "sound": if (!soundOn) { SoundEngine.eventPosted += OnSound; soundOn = true; } break;
                case "probe": HookProbe(s, rep); break;
                case "death": if (!deathOn) { DeathManager.died += OnDeath; deathOn = true; } break;
                case "loaded": if (!loadOn) { GameManager.loadFinished += OnLoaded; loadOn = true; } break;
                case "field": BindField(s, rep); break;
                case "posted": if (!postedOn) { onPosted = OnPosted; SceneRecorder.posted += onPosted; postedOn = true; } break;
            }
            s.armed = true;
        }

        // Type "posted": SceneRecorder.PostEvent / `srec event` with the event's own name, or with `match` (a trailing '*'
        // matches a prefix).
        internal static bool PostedMatch(string eventName, string match, string posted)
        {
            if (string.IsNullOrEmpty(posted)) return false;
            string m = string.IsNullOrEmpty(match) ? eventName ?? "" : match;
            if (m.EndsWith("*", StringComparison.Ordinal)) return posted.StartsWith(m.Substring(0, m.Length - 1), StringComparison.Ordinal);
            return posted == m;
        }

        void OnPosted(string name, string detail)
        {
            if (!armed) return;
            foreach (var s in srcs)
                if (s.armed && s.spec.type == "posted" && PostedMatch(s.spec.name, s.spec.match, name))
                    pending.Add(new Pending { src = s, source = "posted", detail = name + (string.IsNullOrEmpty(detail) ? "" : ": " + detail) });
        }

        public void Disarm()
        {
            armed = false;
            foreach (var s in srcs)
            {
                Unhook(s);
                if (s.probe != null && s.onProbe != null)
                {
                    try { if (s.spec.edge == "exit") s.probe.probeExit.Event -= s.onProbe; else s.probe.probeEnter.Event -= s.onProbe; } catch { }
                    s.onProbe = null;
                }
            }
            if (soundOn) { try { SoundEngine.eventPosted -= OnSound; } catch { } soundOn = false; }
            if (deathOn) { try { DeathManager.died -= OnDeath; } catch { } deathOn = false; }
            if (loadOn) { try { GameManager.loadFinished -= OnLoaded; } catch { } loadOn = false; }
            if (postedOn) { try { SceneRecorder.posted -= onPosted; } catch { } postedOn = false; }
            pending.Clear();
        }

        // ---------------------------------------------------------------- signals (own taps; the reused-signal case is re-validated every step)

        internal static FastList<SignalConnection> Connections(SignalOut so)
        {
            if (fConns == null) fConns = typeof(SignalOut).GetField("connections", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return fConns != null ? fConns.GetValue(so) as FastList<SignalConnection> : null;
        }

        static bool InName(string have, string want)
        {
            if (have == null) return false;
            return have == want || have == SignalIn.prefixFsmSignal + want || SignalIn.prefixFsmSignal + have == want;
        }

        void HookSignal(Src s)
        {
            Unhook(s);
            s.tapVersion = Levels.StreamVersion;
            if (s.go == null) return;
            SignalManager sm = null;
            try { sm = PersistentBehaviour<SignalManager>.instance; } catch { }
            if (sm == null || sm.signalOuts == null) return;
            bool outgoing = s.spec.type == "signal";
            foreach (var so in sm.signalOuts)
            {
                if (so == null || !so.isActive || so.gameObject == null) continue;
                var list = Connections(so);
                if (list == null) continue;
                bool match = false;
                if (outgoing) match = so.gameObject == s.go && so.debugName == s.spec.signal;
                else for (int i = 0; i < list.Count; i++) { var c = list[i]; if (c != null && c.signalInGameObject == s.go && InName(c.signalInName, s.spec.signal)) { match = true; break; } }
                if (!match) continue;
                var src = s; var sender = so;
                var tapIn = new SignalIn { isActive = true, debugName = TapName, action = () => OnSignal(src, sender) };
                var tap = new SignalConnection { isActive = true, signalIn = tapIn, signalOut = so, signalOutGameObject = so.gameObject, signalOutName = so.debugName, signalInName = TapName };
                list.Insert(0, tap);
                s.taps.Add(new KeyValuePair<SignalOut, SignalConnection>(so, tap));
            }
        }

        bool TapsValid(Src s)
        {
            if (s.go == null) return false;
            foreach (var kv in s.taps)
            {
                var so = kv.Key;
                if (!so.isActive || so.gameObject == null) return false;
                if (s.spec.type == "signal" && (so.gameObject != s.go || so.debugName != s.spec.signal)) return false;
                var l = Connections(so);
                if (l == null || !l.Contains(kv.Value)) return false;
            }
            return true;
        }

        void Rehook(Src s)
        {
            if (s.go == null)
            {
                var rep = new SpecReport();
                s.go = MultiCamTargets.ResolveObject(s.spec.obj, "events." + s.spec.name + ".obj", rep);
                if (s.go != null && row != null) row("warn", s.spec.name, "events", "object re-resolved after a stream change: " + Inspector.PathOf(s.go.transform));
            }
            int before = s.taps.Count;
            HookSignal(s);
            if (s.taps.Count != before && row != null) row("mark", s.spec.name, "events", "signal taps " + before + " -> " + s.taps.Count);
        }

        void Unhook(Src s)
        {
            foreach (var kv in s.taps)
            {
                try { var l = Connections(kv.Key); if (l != null) l.Remove(kv.Value); } catch { }
            }
            s.taps.Clear();
        }

        void OnSignal(Src s, SignalOut so)
        {
            if (!armed || !s.armed) return;
            string d;
            try { d = (so.gameObject != null ? so.gameObject.name : "?") + "." + so.debugName; } catch { d = "?"; }
            pending.Add(new Pending { src = s, source = s.spec.type, detail = d });
        }

        // ---------------------------------------------------------------- FSMs

        void HookFsm(Src s, SpecReport rep)
        {
            var e = s.spec;
            s.fsms.Clear();
            if (s.go == null) return;
            foreach (var f in s.go.GetComponents<PlayMakerFSM>()) if (f != null && (e.fsm.Length == 0 || f.FsmName == e.fsm)) s.fsms.Add(f);
            if (s.fsms.Count == 0) { rep.Error("events." + e.name + ".fsm", "no PlayMakerFSM" + (e.fsm.Length > 0 ? " named '" + e.fsm + "'" : "") + " on " + e.obj); return; }
            if (e.edge == "event" && !FsmLog.LoggingEnabled) { MultiCamSession.EnableFsmLogging(); rep.Note("events." + e.name, "FsmLog.LoggingEnabled switched on for FSM event edges"); }
            s.useLog = FsmLog.LoggingEnabled;
            if (!s.useLog) rep.Note("events." + e.name, "FsmLog logging is off: polling ActiveStateName every frame (very short states may be missed)");
            foreach (var f in s.fsms)
            {
                int id = f.GetInstanceID();
                try { s.fsmState[id] = f.ActiveStateName; } catch { s.fsmState[id] = null; }
                try { s.logIndex[id] = f.Fsm.MyLog.Entries.Count; } catch { s.logIndex[id] = 0; }
            }
        }

        void PollFsm(Src s)
        {
            var e = s.spec;
            foreach (var f in s.fsms)
            {
                if (f == null) continue;
                int id = f.GetInstanceID();
                if (s.useLog)
                {
                    List<FsmLogEntry> en = null;
                    try { en = f.Fsm.MyLog.Entries; } catch { }
                    if (en != null)
                    {
                        int from;
                        s.logIndex.TryGetValue(id, out from);
                        if (from > en.Count) from = en.Count;
                        for (int k = from; k < en.Count; k++)
                        {
                            var x = en[k];
                            if (x == null) continue;
                            if (e.edge == "enter" && x.LogType == FsmLogType.EnterState && x.State != null && x.State.Name == e.state) Match(s, "fsm", f.FsmName + " enter " + e.state);
                            else if (e.edge == "exit" && x.LogType == FsmLogType.ExitState && x.State != null && x.State.Name == e.state) Match(s, "fsm", f.FsmName + " exit " + e.state);
                            else if (e.edge == "event" && x.LogType == FsmLogType.Event && x.Event != null && x.Event.Name == e.fsmEvent) Match(s, "fsm", f.FsmName + " event " + e.fsmEvent);
                        }
                        s.logIndex[id] = en.Count;
                        continue;
                    }
                }
                string st;
                try { st = f.ActiveStateName; } catch { continue; }
                string old;
                s.fsmState.TryGetValue(id, out old);
                if (st == old) continue;
                s.fsmState[id] = st;
                if (e.edge == "enter" && st == e.state) Match(s, "fsm", f.FsmName + " " + (old ?? "(none)") + " -> " + st);
                else if (e.edge == "exit" && old == e.state) Match(s, "fsm", f.FsmName + " " + old + " -> " + (st ?? "(none)"));
            }
        }

        // ---------------------------------------------------------------- probes, sounds, death, load

        void HookProbe(Src s, SpecReport rep)
        {
            if (s.go == null) return;
            s.probe = s.go.GetComponent<IsTriggeredByProbe>();
            if (s.probe == null) s.probe = s.go.GetComponentInChildren<IsTriggeredByProbe>();
            if (s.probe == null) { rep.Error("events." + s.spec.name + ".obj", "no IsTriggeredByProbe on " + s.spec.obj); return; }
            var src = s;
            s.onProbe = p => { if (armed && src.armed) pending.Add(new Pending { src = src, source = "probe", detail = src.spec.edge }); };
            if (s.spec.edge == "exit") s.probe.probeExit.Event += s.onProbe; else s.probe.probeEnter.Event += s.onProbe;
        }

        void OnSound(uint id, string name, GameObject go, bool result)
        {
            if (!armed) return;
            foreach (var s in srcs)
            {
                if (!s.armed || s.spec.type != "sound") continue;
                if (!string.Equals(name, s.spec.sound, StringComparison.OrdinalIgnoreCase)) continue;
                if (s.go != null && (go == null || (go != s.go && !go.transform.IsChildOf(s.go.transform)))) continue;
                pending.Add(new Pending { src = s, source = "sound", detail = name + (go != null ? " @" + go.name : "") });
            }
        }

        void OnDeath()
        {
            if (!armed) return;
            foreach (var s in srcs) if (s.armed && s.spec.type == "death") pending.Add(new Pending { src = s, source = "death", detail = "" });
        }

        void OnLoaded(GameManager.Load load, Savepoint.CharacterType ct)
        {
            if (!armed) return;
            foreach (var s in srcs) if (s.armed && s.spec.type == "loaded") pending.Add(new Pending { src = s, source = "loaded", detail = load.ToString() });
        }

        // ---------------------------------------------------------------- polling

        public void PollFrame(MultiCamContext c)
        {
            if (!armed) return;
            ctx = c;
            foreach (var s in srcs)
            {
                if (!s.armed) continue;
                var ty = s.spec.type;
                try
                {
                    if (ty == "fsm") PollFsm(s);
                    else if ((ty == "boystate" || ty == "visual") && !Gated(s, c.t)) PollState(s);
                }
                catch (Exception e) { Fail(s, e); }
            }
            if (pending.Count == 0) return;
            var list = pending.ToArray();
            pending.Clear();
            foreach (var p in list) Match(p.src, p.source, p.detail);
        }

        public void PollStep(MultiCamContext c)
        {
            if (!armed) return;
            ctx = c;
            foreach (var s in srcs)
            {
                if (!s.armed) continue;
                if (Gated(s, c.t)) continue;
                try
                {
                    switch (s.spec.type)
                    {
                        case "time": if (double.IsNaN(s.fired) && c.t >= s.spec.value - 1e-9) Match(s, "time", "t " + c.t.ToString("0.###", IC)); break;
                        case "signal":
                        case "signalIn": if (Levels.StreamVersion != s.tapVersion || !TapsValid(s)) Rehook(s); break;
                        case "impact": PollImpact(s); break;
                        case "box": PollBox(s); break;
                        case "cond": PollCond(s); break;
                        case "field": PollField(s); break;
                        case "camcut": PollCamcut(s); break;
                    }
                }
                catch (Exception e) { Fail(s, e); }
            }
            if (logMonitor && EventMonitor.enabled) RawRows();
        }

        bool Gated(Src s, double t)
        {
            var ty = s.spec.type;
            if (s.spec.after == null || ty == "signal" || ty == "signalIn" || ty == "fsm" || ReachedFired(s.spec.after, t)) return false;
            s.prevKnown = false; s.hasVel = false; s.hasCam = false; s.lastName = null;
            return true;
        }

        void Fail(Src s, Exception e)
        {
            if (s.warned) return;
            s.warned = true;
            if (row != null) row("error", s.spec.name, "events", e.GetType().Name + ": " + e.Message);
        }

        void Edge(Src s, bool cur, string source, string detail, bool fireIfInitial)
        {
            if (!s.prevKnown) { s.prevKnown = true; s.prev = cur; if (cur && fireIfInitial) Match(s, source, detail); return; }
            bool rising = cur && !s.prev;
            s.prev = cur;
            if (rising) Match(s, source, detail);
        }

        void PollState(Src s)
        {
            var boy = ScriptGlobals.boy;
            string n = null;
            if (boy != null)
            {
                if (s.spec.type == "boystate") { var st = boy.state; n = st != null ? st.GetType().Name : null; }
                else { var vb = boy.visualBoy; var st = vb != null ? vb.state : null; n = st != null ? st.GetType().Name : null; }
            }
            bool isMatch = n != null && string.Equals(n, s.spec.match, StringComparison.Ordinal);
            Edge(s, s.spec.edge == "exit" ? !isMatch : isMatch, s.spec.type, (s.lastName ?? "-") + " -> " + (n ?? "-"), false);
            s.lastName = n;
        }

        bool Sample(TargetRef r, out TargetSample ts)
        {
            ts = default(TargetSample);
            return r != null && ctx != null && ctx.Target(r, out ts) && ts.ok;
        }

        void PollImpact(Src s)
        {
            TargetSample ts;
            if (!Sample(s.spec.target, out ts)) { s.hasVel = false; return; }
            Vector3 v = ts.vel;
            var rb = ts.go != null ? ts.go.GetComponent<Rigidbody>() : null;
            if (rb != null) v = rb.velocity;
            if (s.hasVel)
            {
                float dv = (s.lastVel - v).magnitude;
                float th = Math.Max(s.spec.min, 0.25f);
                if (dv >= th && v.magnitude < s.lastVel.magnitude) Match(s, "impact", "dv " + dv.ToString("0.##", IC) + " m/s, speed " + s.lastVel.magnitude.ToString("0.##", IC) + " -> " + v.magnitude.ToString("0.##", IC));
            }
            s.lastVel = v;
            s.hasVel = true;
        }

        void PollBox(Src s)
        {
            TargetSample ts;
            if (!Sample(s.spec.target, out ts)) return;
            bool inside = new Bounds(s.spec.center, s.spec.size).Contains(ts.pos);
            Edge(s, s.spec.edge == "exit" ? !inside : inside, "box", (inside ? "inside " : "outside ") + ts.pos.ToString("F2"), false);
        }

        void PollCond(Src s)
        {
            TargetSample ts;
            if (!Sample(s.spec.target, out ts)) return;
            double v;
            switch (s.spec.prop)
            {
                case "x": v = ts.pos.x; break;
                case "y": v = ts.pos.y; break;
                case "z": v = ts.pos.z; break;
                case "vx": v = ts.vel.x; break;
                case "vy": v = ts.vel.y; break;
                case "vz": v = ts.vel.z; break;
                case "speed": v = ts.vel.magnitude; break;
                case "dist":
                    {
                        TargetSample to;
                        if (!Sample(s.spec.to, out to)) return;
                        v = (ts.pos - to.pos).magnitude;
                        break;
                    }
                default: return;
            }
            Edge(s, Compare(v, s.spec.op, s.spec.value), "cond", s.spec.prop + " " + v.ToString("0.###", IC), false);
        }

        static bool Compare(double v, string op, double x)
        {
            switch (op)
            {
                case "<": return v < x;
                case "<=": return v <= x;
                case ">": return v > x;
                case ">=": return v >= x;
                case "==": return Math.Abs(v - x) < 1e-6;
                case "!=": return Math.Abs(v - x) >= 1e-6;
            }
            return false;
        }

        void BindField(Src s, SpecReport rep)
        {
            var e = s.spec;
            string w = "events." + e.name;
            if (s.go == null) return;
            foreach (var c in s.go.GetComponents<Component>()) if (c != null && string.Equals(c.GetType().Name, e.comp, StringComparison.OrdinalIgnoreCase)) { s.comp = c; break; }
            if (s.comp == null) { rep.Error(w + ".comp", "no component '" + e.comp + "' on " + e.obj); return; }
            string name = e.field;
            if (name.EndsWith(".Count")) { s.countOf = true; name = name.Substring(0, name.Length - 6); }
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var t = s.comp.GetType(); t != null && s.field == null && s.prop == null; t = t.BaseType)
            {
                s.field = t.GetField(name, bf | BindingFlags.DeclaredOnly);
                if (s.field == null) { var p = t.GetProperty(name, bf | BindingFlags.DeclaredOnly); if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) s.prop = p; }
            }
            if (s.field == null && s.prop == null) rep.Error(w + ".field", "no field or property '" + name + "' on " + s.comp.GetType().Name);
        }

        void PollField(Src s)
        {
            if (s.comp == null || (s.field == null && s.prop == null)) return;
            object v = s.field != null ? s.field.GetValue(s.comp) : s.prop.GetValue(s.comp, null);
            if (s.countOf) { var col = v as ICollection; v = col != null ? (object)col.Count : null; }
            bool cur;
            string text = v == null ? "null" : Convert.ToString(v, IC);
            if (s.spec.match.Length > 0) cur = string.Equals(text, s.spec.match, StringComparison.OrdinalIgnoreCase);
            else
            {
                double d;
                if (v is bool) d = (bool)v ? 1 : 0;
                else if (v is Enum) d = Convert.ToDouble(Convert.ToInt64(v, IC), IC);
                else if (v is IConvertible) { try { d = Convert.ToDouble(v, IC); } catch { return; } }
                else return;
                cur = Compare(d, s.spec.op, s.spec.value);
            }
            Edge(s, cur, "field", s.spec.comp + "." + s.spec.field + " = " + text, false);
        }

        void PollCamcut(Src s)
        {
            var g = ctx.game;
            if (s.spec.match == "box")
            {
                string n = g.box ?? "";
                if (s.lastName != null && n != s.lastName) Match(s, "camcut", s.lastName + " -> " + n);
                s.lastName = n;
                return;
            }
            if (s.spec.min > 0f)
            {
                if (s.hasCam && (g.positionCamera - s.lastCam).magnitude > s.spec.min) Match(s, "camcut", "jump " + (g.positionCamera - s.lastCam).magnitude.ToString("0.##", IC) + " m");
                s.lastCam = g.positionCamera;
                s.hasCam = true;
                return;
            }
            if (g.cut) Match(s, "camcut", "game camera cut");
        }

        void RawRows()
        {
            long total = EventMonitor.total;
            if (monitorSeen < 0) { monitorSeen = total; return; }
            long n = total - monitorSeen;
            monitorSeen = total;
            if (n <= 0 || row == null) return;
            if (n > EventMonitor.Count) n = EventMonitor.Count;
            if (n > 200) { row("mark", "monitor", "", (n - 200) + " raw monitor rows skipped"); n = 200; }
            for (long i = n - 1; i >= 0; i--)
            {
                var ev = EventMonitor.Get((int)i);
                string k = ev.kind == 'S' ? "signal" : ev.kind == 'F' ? "fsm" : ev.kind == 'A' ? "sound" : null;
                if (k != null) row(k, "", "", ev.text);
            }
        }

        void Match(Src s, string source, string detail)
        {
            if (!armed || ctx == null) return;
            var e = s.spec;
            if (e.after != null && !ReachedFired(e.after, ctx.t)) return;
            if (ctx.t - s.lastMatch < e.cooldown - 1e-9) return;
            s.lastMatch = ctx.t;
            s.matches++;
            if (s.matches < e.count)
            {
                if (e.log && row != null) row("event+", e.name, source, "match " + s.matches + "/" + e.count + (detail.Length > 0 ? ": " + detail : ""));
                return;
            }
            bool first = double.IsNaN(s.fired);
            s.fires.Add(ctx.t);
            if (first && !double.IsNaN(s.predicted) && row != null && Math.Abs(s.predicted - ctx.t) > 1.0 / Math.Max(1, ctx.rate) + 1e-6)
                row("mark", e.name, "prediction", "event " + e.name + " fired at " + ctx.t.ToString("0.###", IC) + ", predicted " + s.predicted.ToString("0.###", IC));
            if (first || !e.once) s.fired = ctx.t;
            if (e.log && row != null) row(first ? "event" : "event+", e.name, source, detail);
            if (first && ctx.log != null) ctx.log("event " + e.name + " at t " + ctx.t.ToString("0.###", IC) + (detail.Length > 0 ? " (" + detail + ")" : ""));
        }
    }

    // ------------------------------------------------------------------ authoring probe (mcam probe)

    internal static class MultiCamProbe
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static string Run(List<string> a)
        {
            if (a.Count < 3) return "usage: mcam probe <obj> | mcam probe <x> <y> <z> <radius>";
            float x, y, z, r;
            if (a.Count >= 6 && TryF(a[2], out x) && TryF(a[3], out y) && TryF(a[4], out z) && TryF(a[5], out r)) return Area(new Vector3(x, y, z), r);
            string q = string.Join(" ", a.GetRange(2, a.Count - 2).ToArray());
            var rep = new SpecReport();
            GameObject go = null;
            try { go = MultiCamTargets.ResolveObject(q, "probe", rep); } catch { }
            string how = "strict";
            if (go == null) { go = Remote.Resolve(q); how = "loose match (not strict: use the printed path in specs)"; }
            if (go == null) return "not found: " + q + (rep.errors.Count + rep.warnings.Count > 0 ? "\n" + rep.Summary(10) : "");
            return Describe(go, how);
        }

        static bool TryF(string s, out float v) { return float.TryParse(s, NumberStyles.Float, IC, out v); }

        static bool Under(GameObject g, GameObject root) { return g != null && (g == root || g.transform.IsChildOf(root.transform)); }

        static string J(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        static string Describe(GameObject go, string how)
        {
            var sb = new StringBuilder();
            string path = Inspector.PathOf(go.transform);
            sb.Append(path).Append("  #").Append(go.GetInstanceID()).Append(go.activeInHierarchy ? "  active" : "  inactive").Append("  pos ").Append(go.transform.position.ToString("F2")).Append("  [").Append(how).Append("]\n");
            var snippets = new List<string>();
            SignalManager sm = null;
            try { sm = PersistentBehaviour<SignalManager>.instance; } catch { }
            if (sm != null && sm.signalOuts != null)
            {
                int outs = 0, ins = 0;
                foreach (var so in sm.signalOuts)
                {
                    if (so == null || !so.isActive || so.gameObject == null) continue;
                    var list = MultiCamEvents.Connections(so);
                    if (Under(so.gameObject, go) && outs < 40)
                    {
                        outs++;
                        string sp = Inspector.PathOf(so.gameObject.transform);
                        sb.Append("  signal out  ").Append(sp).Append(" .").Append(so.debugName);
                        int n = 0;
                        if (list != null) for (int i = 0; i < list.Count; i++) { var c = list[i]; if (c == null || c.signalInName == "InsideDev.tap") continue; sb.Append(n++ == 0 ? "  ->  " : ", ").Append(c.signalInGameObject != null ? c.signalInGameObject.name : "?").Append('.').Append(c.signalInName); }
                        sb.Append('\n');
                        snippets.Add("{\"type\": \"signal\", \"obj\": " + J(sp) + ", \"signal\": " + J(so.debugName) + "}");
                    }
                    if (list == null) continue;
                    for (int i = 0; i < list.Count && ins < 40; i++)
                    {
                        var c = list[i];
                        if (c == null || c.signalInName == "InsideDev.tap" || !Under(c.signalInGameObject, go)) continue;
                        ins++;
                        string rp = Inspector.PathOf(c.signalInGameObject.transform);
                        sb.Append("  signal in   ").Append(rp).Append(" .").Append(c.signalInName).Append("  <-  ").Append(so.gameObject.name).Append('.').Append(so.debugName).Append('\n');
                        snippets.Add("{\"type\": \"signalIn\", \"obj\": " + J(rp) + ", \"signal\": " + J(c.signalInName) + "}");
                    }
                }
            }
            foreach (var f in go.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (f == null) continue;
                string fp = Inspector.PathOf(f.transform);
                sb.Append("  fsm         ").Append(fp).Append(" '").Append(f.FsmName).Append("'  active ").Append(f.ActiveStateName ?? "-");
                try
                {
                    var st = f.FsmStates; var names = new List<string>();
                    if (st != null) foreach (var s in st) if (s != null && names.Count < 30) names.Add(s.Name);
                    sb.Append("\n              states: ").Append(string.Join(", ", names.ToArray()));
                    var ev = f.FsmEvents; var en = new List<string>();
                    if (ev != null) foreach (var e in ev) if (e != null && !e.IsSystemEvent && en.Count < 30) en.Add(e.Name);
                    if (en.Count > 0) sb.Append("\n              events: ").Append(string.Join(", ", en.ToArray()));
                    if (names.Count > 0) snippets.Add("{\"type\": \"fsm\", \"obj\": " + J(fp) + ", \"fsm\": " + J(f.FsmName) + ", \"state\": " + J(names[names.Count > 1 ? 1 : 0]) + ", \"edge\": \"enter\"}");
                }
                catch { }
                sb.Append('\n');
            }
            foreach (var p in go.GetComponentsInChildren<IsTriggeredByProbe>(true))
            {
                if (p == null) continue;
                string pp = Inspector.PathOf(p.transform);
                sb.Append("  probe       ").Append(pp).Append(" (IsTriggeredByProbe)\n");
                snippets.Add("{\"type\": \"probe\", \"obj\": " + J(pp) + ", \"edge\": \"enter\"}");
            }
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb == null) continue;
                string rp = Inspector.PathOf(rb.transform);
                sb.Append("  rigidbody   ").Append(rp).Append("  mass ").Append(rb.mass.ToString("0.###", IC)).Append("  ").Append(rb.interpolation).Append(rb.isKinematic ? "  kinematic" : "").Append(rb.IsSleeping() ? "  sleeping" : "").Append('\n');
                snippets.Add("{\"type\": \"impact\", \"target\": " + J(rp) + ", \"min\": 1.0}");
            }
            int ca = go.GetComponentsInChildren<CollisionAnalyzer>(true).Length;
            if (ca > 0) sb.Append("  collision analyzers: ").Append(ca).Append('\n');
            var ids = new HashSet<int>();
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) ids.Add(t.gameObject.GetInstanceID());
            int shown = 0;
            for (int i = 0; i < EventMonitor.Count && i < 2000 && shown < 15; i++)
            {
                var ev = EventMonitor.Get(i);
                if (ev.kind != 'A' || !ids.Contains(ev.goId)) continue;
                shown++;
                sb.Append("  sound       ").Append(ev.text).Append('\n');
                int k = ev.text.IndexOf("  posts  ");
                if (k >= 0) snippets.Add("{\"type\": \"sound\", \"sound\": " + J(ev.text.Substring(k + 9).Trim()) + ", \"obj\": " + J(path) + "}");
            }
            if (snippets.Count > 0)
            {
                sb.Append("events snippets (add a \"name\"):\n");
                var seen = new HashSet<string>();
                foreach (var s in snippets) if (seen.Add(s) && seen.Count <= 40) sb.Append("  ").Append(s).Append('\n');
            }
            return sb.ToString();
        }

        static string Area(Vector3 c, float r)
        {
            var withSignals = new HashSet<int>();
            SignalManager sm = null;
            try { sm = PersistentBehaviour<SignalManager>.instance; } catch { }
            if (sm != null && sm.signalOuts != null) foreach (var so in sm.signalOuts) if (so != null && so.isActive && so.gameObject != null) withSignals.Add(so.gameObject.GetInstanceID());
            var sb = new StringBuilder();
            int n = 0;
            foreach (var t in Inspector.AllSceneTransforms())
            {
                if (t == null || (t.position - c).magnitude > r) continue;
                var go = t.gameObject;
                var kinds = new List<string>();
                if (withSignals.Contains(go.GetInstanceID())) kinds.Add("signals");
                if (go.GetComponent<PlayMakerFSM>() != null) kinds.Add("fsm");
                if (go.GetComponent<IsTriggeredByProbe>() != null) kinds.Add("probe");
                var rb = go.GetComponent<Rigidbody>();
                if (rb != null && !rb.isKinematic) kinds.Add("rigidbody " + rb.mass.ToString("0.##", IC) + " kg");
                if (kinds.Count == 0) continue;
                if (++n > 80) { sb.Append("  ... more (narrow the radius)\n"); break; }
                sb.Append("  ").Append(((t.position - c).magnitude).ToString("0.0", IC)).Append(" m  ").Append(Inspector.PathOf(t)).Append("  [").Append(string.Join(", ", kinds.ToArray())).Append("]\n");
            }
            return n == 0 ? "nothing with signals / FSMs / probes / rigidbodies within " + r + " m" : "within " + r.ToString("0.#", IC) + " m of " + c.ToString("F2") + " (then: mcam probe <path>):\n" + sb;
        }
    }
}
