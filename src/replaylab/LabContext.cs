// ReplayLab run context: arguments, result.json/verdict.txt/log.txt output, the undo stack that restores every change in
// a finally block, timeScale forcing, camera hold, clean frames (editor overlay off), TAA control and the hook targets.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev.ReplayLab
{
    sealed class LabNullRenderer : IEditorRenderer
    {
        public IEditorRenderer inner;
        public string Name { get { return "suppressed by lab"; } }
        public void Invalidate(string reason) { }
        public void DestroyResources() { }
        public bool Submit(Draw list, Color? probe, ref RenderStats stats) { return true; }
        public float Measure(string s, int size) { return inner != null ? inner.Measure(s, size) : (s ?? "").Length * size * 0.55f; }
        public bool ResourcesOk(out string report) { report = "suppressed by lab"; return true; }
    }

    public sealed class LabContext
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public readonly string id, dir, test;
        public readonly Dictionary<string, string> args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> positional = new List<string>();
        public readonly Json.Obj result = new Json.Obj();
        public readonly List<string> log = new List<string>();
        public readonly List<object> grabs = new List<object>();
        public readonly List<string> restoreErrors = new List<string>();
        public readonly WaitForEndOfFrame eof = new WaitForEndOfFrame();
        readonly List<KeyValuePair<string, Action>> undo = new List<KeyValuePair<string, Action>>();

        public string verdict, summary, phase = "start", abortReason;
        public bool abort, finished, ownHost;
        public int startFrame, steps, lastStepFrame, hookErrors;
        public float startReal, timeout = 600f;
        public DateTime started;
        public Camera cam;

        public Action onUpdate, onLate, onLate0;
        public Action<Camera> onPreCull, onPreRender, onPostRender, onProbePreCull, onProbePreRender, onProbePostRender;

        public int traceUntil = -1;
        public readonly List<string> trace = new List<string>();

        bool timeSaved, timeForced;
        float timeBefore, gmBefore, timeTarget;
        public int timeLeaks;

        bool camSaved;
        public LabPose camBefore;

        public D11PDTemporalReprojection3 taa;
        bool taaSaved, taaEnabled, taaJitter;
        float taaMin, taaMax, taaBlur;

        public LabContext(string test, string id, string dir, List<string> a)
        {
            this.test = test; this.id = id; this.dir = dir;
            for (int i = 2; i < a.Count; i++)
            {
                int eq = a[i].IndexOf('=');
                if (eq > 0) args[a[i].Substring(0, eq)] = a[i].Substring(eq + 1);
                else positional.Add(a[i]);
            }
            started = DateTime.Now;
            startFrame = Time.frameCount;
            startReal = Time.realtimeSinceStartup;
            cam = RenderHost.WorldCam();
            float t;
            if (args.ContainsKey("timeout") && float.TryParse(args["timeout"], NumberStyles.Float, IC, out t)) timeout = t;
        }

        public string Arg(string k, string def) { string v; return args.TryGetValue(k, out v) && v.Length > 0 ? v : def; }
        public float F(string k, float def) { float v; return float.TryParse(Arg(k, null), NumberStyles.Float, IC, out v) ? v : def; }
        public int I(string k, int def) { int v; return int.TryParse(Arg(k, null), NumberStyles.Integer, IC, out v) ? v : def; }
        public bool B(string k, bool def)
        {
            var s = Arg(k, null);
            if (s == null) return def;
            s = s.ToLowerInvariant();
            return s == "on" || s == "1" || s == "true" || s == "yes";
        }
        public bool Has(string word) { foreach (var p in positional) if (p.Equals(word, StringComparison.OrdinalIgnoreCase)) return true; return false; }
        public List<string> List(string k)
        {
            var l = new List<string>();
            var s = Arg(k, null);
            if (s == null) return l;
            foreach (var x in s.Split(',')) if (x.Trim().Length > 0) l.Add(x.Trim());
            return l;
        }

        public void Log(string s)
        {
            string line = "[" + (Time.frameCount - startFrame).ToString(IC) + "] " + s;
            log.Add(line);
            DevLog.Write("[lab " + id + "] " + s);
        }

        public void Prep(string msg) { throw new LabPrep(msg); }

        public void Pass(string s) { verdict = "PASS"; summary = s; }
        public void Fail(string s) { verdict = "FAIL"; summary = s; }
        public void Info(string s) { verdict = "INFO"; summary = s; }
        public void Verdict(bool ok, string s) { verdict = ok ? "PASS" : "FAIL"; summary = s; }

        public void Undo(string what, Action a) { undo.Add(new KeyValuePair<string, Action>(what, a)); }

        public void HookError(string what, Exception e)
        {
            hookErrors++;
            if (hookErrors <= 10) Log("hook " + what + ": " + e.GetType().Name + ": " + e.Message);
        }

        public void Trace(string what)
        {
            if (Time.frameCount > traceUntil) return;
            trace.Add(Time.frameCount.ToString(IC) + " " + what);
        }

        public string File(string name) { return Path.Combine(dir, name); }

        public void SaveGrab(Frame f, string name, string note)
        {
            if (f == null) return;
            LabImage.Save(f, File(name));
            AddGrab(name, f.r, note);
        }

        public void AddGrab(string name, RectI r, string note)
        {
            var o = new Json.Obj();
            o["file"] = name; o["rect"] = r.ToJson(); o["frame"] = Time.frameCount - startFrame;
            if (note != null) o["note"] = note;
            grabs.Add(o);
        }

        public Frame Grab(RectI r, string save, string note)
        {
            var f = LabImage.Grab(r, save != null ? File(save) : null);
            if (save != null && f != null) AddGrab(save, f.r, note);
            return f;
        }

        public IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }
        public IEnumerator Render(int n) { for (int i = 0; i < n; i++) yield return eof; }
        public IEnumerator Settle() { return Render(Math.Max(1, I("settle", 12))); }

        public void Step()
        {
            steps++;
            lastStepFrame = Time.frameCount;
            ReassertTime();
        }

        public void TimeScale(float v)
        {
            if (!timeSaved)
            {
                timeSaved = true;
                timeBefore = Time.timeScale;
                try { gmBefore = GameManager.TimeScale; } catch { gmBefore = timeBefore; }
                Undo("timeScale", () =>
                {
                    timeForced = false;
                    try { GameManager.TimeScale = gmBefore; } catch { }
                    Time.timeScale = timeBefore;
                });
            }
            timeForced = true;
            timeTarget = v;
            Apply(v);
        }

        public void Freeze() { TimeScale(0f); }

        static void Apply(float v)
        {
            try { GameManager.TimeScale = v; } catch { }
            Time.timeScale = v;
        }

        public void ReassertTime()
        {
            if (!timeForced || finished) return;
            if (Time.timeScale != timeTarget)
            {
                timeLeaks++;
                if (timeLeaks <= 3) Log("timeScale leak: " + Time.timeScale.ToString("0.###", IC) + " (forced " + timeTarget.ToString("0.###", IC) + ")");
                Apply(timeTarget);
            }
        }

        public bool HoldCamera(LabPose p)
        {
            if (cam == null) return false;
            if (!camSaved)
            {
                camSaved = true;
                camBefore = LabPose.Of(cam);
                bool wasFree = CameraControl.free, wasLock = CameraControl.captureLock, wasQuiet = CameraControl.quiet;
                float near = cam.nearClipPlane;
                var c = cam;
                Undo("camera", () =>
                {
                    if (wasFree) { CameraControl.Set(camBefore.p, camBefore.r, camBefore.fov); }
                    else
                    {
                        CameraControl.EndCapture();
                        if (c != null) { c.fieldOfView = camBefore.fov; c.nearClipPlane = near; }
                    }
                    CameraControl.captureLock = wasLock;
                    CameraControl.quiet = wasQuiet;
                });
                CameraControl.quiet = true;
                CameraControl.captureLock = true;
            }
            return CameraControl.Set(p.p, p.r, p.fov > 0 ? p.fov : cam.fieldOfView);
        }

        public LabPose StartPose()
        {
            LabPose p;
            if (LabUtil.TryPose(Arg("cam", null), out p)) { if (p.fov <= 0 && cam != null) p.fov = cam.fieldOfView; return p; }
            return LabPose.Of(cam);
        }

        public void HoldStartCamera()
        {
            var p = StartPose();
            if (!HoldCamera(p)) Prep("no gameplay camera");
            result["camera"] = p.ToJson();
        }

        public void CleanFrames()
        {
            if (!B("clean", true)) return;
            if (RenderHost.renderer is LabNullRenderer) return;
            var before = RenderHost.renderer;
            var req = RenderHost.requested;
            RenderHost.renderer = new LabNullRenderer { inner = before };
            if (RenderHost.requested == Backend.Auto) RenderHost.requested = RenderHost.active;
            Undo("clean frames", () =>
            {
                if (RenderHost.renderer is LabNullRenderer) RenderHost.renderer = before ?? RenderHost.glRenderer;
                RenderHost.requested = req;
            });
        }

        public D11PDTemporalReprojection3 Taa()
        {
            if (taa == null && cam != null) taa = cam.GetComponent<D11PDTemporalReprojection3>();
            if (taa != null && !taaSaved)
            {
                taaSaved = true;
                taaMin = taa.feedbackMin; taaMax = taa.feedbackMax; taaJitter = taa.frustumJitter; taaBlur = taa.motionBlurStrength; taaEnabled = taa.enabled;
                var t = taa;
                Undo("taa", () =>
                {
                    if (t == null) return;
                    t.feedbackMin = taaMin; t.feedbackMax = taaMax; t.frustumJitter = taaJitter; t.motionBlurStrength = taaBlur;
                    if (t.enabled != taaEnabled) t.enabled = taaEnabled;
                });
                var o = new Json.Obj();
                o["feedbackMin"] = LabUtil.Num(taaMin); o["feedbackMax"] = LabUtil.Num(taaMax); o["frustumJitter"] = taaJitter; o["motionBlurStrength"] = LabUtil.Num(taaBlur);
                result["taaGame"] = o;
            }
            return taa;
        }

        public void TaaClean(bool noBlur)
        {
            var t = Taa();
            if (t == null) return;
            t.feedbackMin = 0f; t.feedbackMax = 0f; t.frustumJitter = false;
            t.motionBlurStrength = noBlur ? 0f : taaBlur;
        }

        public void TaaGame(float blur)
        {
            var t = Taa();
            if (t == null) return;
            t.feedbackMin = taaMin; t.feedbackMax = taaMax; t.frustumJitter = taaJitter;
            t.motionBlurStrength = blur < 0f ? taaBlur : blur;
        }

        public float TaaGameBlur { get { Taa(); return taaBlur; } }

        public void SaveTransform(Transform t)
        {
            if (t == null) return;
            var lp = t.localPosition; var lr = t.localRotation; var ls = t.localScale; var par = t.parent;
            Undo("transform " + t.name, () => { if (t != null && t.parent == par) { t.localPosition = lp; t.localRotation = lr; t.localScale = ls; } });
        }

        public void SaveRigidbody(Rigidbody rb)
        {
            if (rb == null) return;
            bool k = rb.isKinematic; var ip = rb.interpolation; var v = rb.velocity; var w = rb.angularVelocity; bool sleep = rb.IsSleeping();
            Undo("rigidbody " + rb.name, () =>
            {
                if (rb == null) return;
                rb.interpolation = ip;
                rb.isKinematic = k;
                if (!k) { rb.velocity = v; rb.angularVelocity = w; if (sleep) rb.Sleep(); }
            });
        }

        public void SaveEnabled(Behaviour b)
        {
            if (b == null) return;
            bool e = b.enabled;
            Undo("enabled " + b.GetType().Name + " " + b.name, () => { if (b != null && b.enabled != e) b.enabled = e; });
        }

        public void SaveRenderer(Renderer r)
        {
            if (r == null) return;
            bool e = r.enabled;
            Undo("renderer " + r.name, () => { if (r != null && r.enabled != e) r.enabled = e; });
        }

        public void SaveActive(GameObject g)
        {
            if (g == null) return;
            bool a = g.activeSelf;
            Undo("active " + g.name, () => { if (g != null && g.activeSelf != a) g.SetActive(a); });
        }

        public LabCamProbe AddProbe()
        {
            if (cam == null) return null;
            LabCamProbe p = null;
            try { p = cam.gameObject.AddComponent<LabCamProbe>(); }
            catch (Exception e) { Log("component probe unavailable: " + e.Message); }
            if (p == null) return null;
            var pp = p;
            Undo("camera probe", () => { if (pp != null) UObj.Destroy(pp); });
            return p;
        }

        public void RunUndo()
        {
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                try { undo[i].Value(); }
                catch (Exception e) { restoreErrors.Add(undo[i].Key + ": " + e.GetType().Name + ": " + e.Message); }
            }
            undo.Clear();
        }

        public string VerdictLine()
        {
            string v = verdict ?? "FAIL";
            string s = summary ?? "no verdict";
            if (restoreErrors.Count > 0) s += " (restore errors: " + restoreErrors.Count + ")";
            return v + " " + s.Replace('\n', ' ').Replace('\r', ' ');
        }

        public void WriteOutputs()
        {
            result["test"] = test; result["id"] = id; result["verdict"] = verdict ?? "FAIL"; result["summary"] = summary ?? "";
            result["started"] = started.ToString("yyyy-MM-dd HH:mm:ss", IC); result["finished"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", IC);
            result["frames"] = Time.frameCount - startFrame; result["seconds"] = LabUtil.Num(Time.realtimeSinceStartup - startReal);
            var a = new Json.Obj();
            foreach (var kv in args) a[kv.Key] = kv.Value;
            if (positional.Count > 0) a["words"] = LabUtil.Strs(positional);
            result["args"] = a;
            result["screen"] = new List<object> { Screen.width, Screen.height };
            result["timeScaleLeaks"] = timeLeaks;
            result["hookErrors"] = hookErrors;
            if (abortReason != null) result["aborted"] = abortReason;
            var r = new Json.Obj();
            r["ok"] = restoreErrors.Count == 0;
            r["errors"] = LabUtil.Strs(restoreErrors);
            result["restore"] = r;
            result["grabs"] = grabs;
            try
            {
                Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(File("result.json"), Json.Write(result) + "\n");
                System.IO.File.WriteAllText(File("verdict.txt"), VerdictLine() + "\n");
                var sb = new StringBuilder();
                foreach (var l in log) sb.Append(l).Append('\n');
                System.IO.File.WriteAllText(File("log.txt"), sb.ToString());
            }
            catch (Exception e) { DevLog.Write("[lab] write outputs " + dir + ": " + e.Message); }
        }
    }
}
