using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // Pano.cs (owner TIME): one console command, one 360-degree equirectangular PNG (+ GPano XMP and a JSON sidecar).
    // The six cube faces are six ordinary game frames: the free camera (core CameraControl.Set, as the camera tool's pose)
    // is put at the eye with fov 90 over the shorter side at the end of a frame, the next frame runs the whole game path at
    // that pose (Update-phase streaming / bounds culling from Camera.main, the srec viewer's camera-mode culler emulation in
    // preCullMain, PrePass lighting, fog, water, the native D11PD post chain) and its back buffer centre square is read at
    // the end of that frame. No Camera.Render and no render texture: an earlier version rendered all faces in one frame with
    // Camera.Render and lost parts of the level in the srec viewer; a normal frame per face is by construction what a
    // screenshot at that pose shows. warm=n frames per face settle first, samples=n
    // accumulate n jittered frames (TAA feedback (i-1)/i); TAA feedback is 0 on the first frame of a face, motion blur 0,
    // barrel distortion and chromatic aberration 0 (they bend the face edges; "lens" keeps them), vignette kept unless
    // noVignette (vignetteTex = white); these are written in Camera.onPreRender so per-frame writers before it do not undo
    // them. campath's onPreCull pose is suspended. Afterwards the free camera / its pose, TAA and post are put back after
    // one more frame with TAA feedback 0 at the original pose (no ghost). A worker thread box-filters the faces, maps them
    // to the equirect and writes an uncompressed (stored deflate) PNG, fast; "compress" deflates it in the game instead.
    // The stored PNG is streamed to the file in bands of at most 32 MiB of pixels (StoredPng), so a 16384 px panorama
    // needs no more memory than a 4096 px one. For the six faces the game is held (Stepper.Freeze, undone at the end)
    // unless it is frozen already or "live" is given, and the free camera's keys are locked (CameraControl.captureLock),
    // so all faces show one moment from one point. overscan=<deg> renders each face about that much wider than 90 degrees
    // and reads only the centre square that spans exactly 90, which keeps screen-edge effects (vignette, bloom falloff,
    // TAA edge clamps) off the cube seams at the price of smaller faces.
    // File: no name -> <_mod>/recordings/pano_<date>_<time>.png, a bare name -> that folder, a path -> as given.
    // Commands: pano [file.png] [size=4096] [x y z] [noVignette] ... (Help), pano status [json], pano wait. Restore (key
    // pano) ends a capture and puts the camera back.
    // Approach after yasirkula/Unity360ScreenshotCapture (MIT, Copyright (c) 2018 Suleyman Yasir KULA: cube faces to
    // equirect, GPano XMP in the PNG) and SoylentGraham's RenderToCubemap + equirect blit gist; no code copied.
    public static class Pano
    {
        public const string Help = "pano [file.png | take] [size=4096 | <size>] [x y z | eye=x,y,z] [noVignette] [lens] [near=m] [warm=0..30] [samples=1..16] [faces=<dir>] [compress] [delay=frames] [live | hold] [overscan=0..45 deg] [wait] | pano status [json] | pano wait | pano help";
        public const string RestoreKey = "pano";
        const string HeadPath = "_Boy/_BoyPreprocessed/visual/Boy/Head";
        const float EyeUp = 0.10f;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly string[] Names = { "front", "right", "back", "left", "up", "down" };
        static readonly float[] Yaw = { 0f, 90f, 180f, 270f, 0f, 0f };
        static readonly float[] Pitch = { 0f, 0f, 0f, 0f, -90f, 90f };
        static readonly WaitForEndOfFrame eof = new WaitForEndOfFrame();
        static readonly object gate = new object();
        static Job job;
        static int lastId;
        static string last = "pano: idle";
        static Json.Obj lastObj;
        internal static bool Capturing;

        static Cap cap;
        static bool hooked;
        static FieldInfo fCurrent, fClean;
        static PropertyInfo pActive, pT;
        static bool looked;

        sealed class Job
        {
            public int id, size = 4096, samples = 1, warm = 1, delay = 2, n, full, m, frame0, frames, lastFrame;
            public string file = "", facesDir = "", eyeFrom = "head", vignetteName = "", sidecar = "";
            public bool hasEye, vignette = true, lens, compress, cleaned, hold = true, held, replaced;
            public volatile bool dropped;
            public float near = -1f, usedNear, usedFov, overscan;
            public Vector3 eye, camPos, camEuler;
            public float camFov, camNear;
            public double gameTime, srecT = double.NaN, renderMs, convertMs, encodeMs;
            public Color32[][] raw = new Color32[6][];
            public readonly List<string> warn = new List<string>();
            public readonly Stopwatch total = new Stopwatch();
            public volatile string phase = "queued";
        }

        sealed class Cap
        {
            public Camera cam;
            public D11PDTemporalReprojection3 taa;
            public D11PDPostCombined pc;
            public float fbMin, fbMax, blur, barrel, chroma, fb, prevFov, prevNear;
            public bool jitter, jitterOn, lens, vignette, wasFree, lockBefore, froze, scaleHeld;
            public float scaleBefore;
            public Texture vig;
            public Vector3 prevPos;
            public Quaternion prevRot;
            public Texture2D tex;
        }

        public static string Command(List<string> args, DevCore core)
        {
            var a = new List<string>(args);
            string sub = a.Count > 1 ? a[1] : "status";
            string low = sub.ToLowerInvariant();
            if (low == "status") return FilmShared.TakeLast(a, "json", 2) ? JsonLine.Write(StatusObj()) : Status();
            if (low == "help") return "usage: " + Help;
            if (low == "wait") { lock (gate) return job != null ? "pending: " + Status() : last; }
            bool wait = FilmShared.TakeLast(a, "wait", 1);
            string text = Start(a, core, false);
            return wait && text.StartsWith("pano #", StringComparison.Ordinal) ? text + "\nwait: pano wait" : text;
        }

        // main thread: a new job from the words after "pano"; the reply starts with "pano #<id> queued" when it runs.
        // bareOnly keeps the files inside <_mod>/recordings (the film tool on the play profile).
        internal static string Start(List<string> a, DevCore core, bool bareOnly)
        {
            lock (gate)
            {
                if (job != null && (job.phase == "queued" || job.phase == "capturing") && Time.frameCount - job.lastFrame > 120)
                {
                    var old = job;
                    old.dropped = true;
                    last = "pano #" + old.id + " failed: dropped, its capture stalled (level load?)";
                    lastObj = Result(old, false, "dropped, its capture stalled (level load?)", 0, 0);
                    RestorePose(old);
                    job = null;
                    End();
                    if (old.cleaned) { old.cleaned = false; Clean(core, false, old); }
                }
                if (job != null) return "pano: busy with #" + job.id + " (" + job.phase + "); pano status says when it is done";
            }
            if (MultiCam.Capturing) return "pano: mcam is capturing";
            if (CameraControl.captureLock) return "pano: busy: another capture holds the camera (an mcam pass, det or lab)";
            var j = new Job();
            int first = 1;
            string name = null;
            if (a.Count > 1 && a[1].ToLowerInvariant() == "take") first = 2;
            else if (a.Count > 1 && !IsOption(a[1])) { name = a[1]; first = 2; }
            string why;
            j.file = FilmFiles.Resolve(Path.Combine(Extensions.Dir, FilmFiles.Folder), "pano", ".png", name, bareOnly, out why);
            if (j.file == null) return "pano: " + why;
            if (!j.file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) j.file += ".png";
            var nums = new List<float>();
            for (int i = first; i < a.Count; i++)
            {
                string s = a[i], k = s.ToLowerInvariant();
                int eq = s.IndexOf('=');
                float v;
                if (eq > 0)
                {
                    string key = k.Substring(0, eq), val = s.Substring(eq + 1);
                    if (key == "size") j.size = (int)Num(val, -1f);
                    else if (key == "near") j.near = Num(val, -1f);
                    else if (key == "samples") j.samples = (int)Num(val, -1f);
                    else if (key == "warm") j.warm = (int)Num(val, -1f);
                    else if (key == "delay") j.delay = (int)Num(val, -1f);
                    else if (key == "overscan") j.overscan = Num(val, -1f);
                    else if (key == "faces")
                    {
                        if (bareOnly) return "pano: refused: faces=<dir> is not available here";
                        try { j.facesDir = Path.GetFullPath(val).Replace('\\', '/'); } catch (Exception e) { return "pano: bad faces dir " + val + ": " + e.Message; }
                    }
                    else if (key == "vignette") j.vignette = val != "0" && val.ToLowerInvariant() != "off";
                    else if (key == "eye")
                    {
                        var p = val.Split(',');
                        if (p.Length != 3) return "pano: eye=x,y,z expected, got " + val;
                        j.eye = new Vector3(Num(p[0], float.NaN), Num(p[1], float.NaN), Num(p[2], float.NaN));
                        if (float.IsNaN(j.eye.x) || float.IsNaN(j.eye.y) || float.IsNaN(j.eye.z)) return "pano: eye=x,y,z expected, got " + val;
                        j.hasEye = true; j.eyeFrom = "point";
                    }
                    else return "pano: unknown option " + s + "\nusage: " + Help;
                }
                else if (float.TryParse(s, NumberStyles.Float, IC, out v)) nums.Add(v);
                else if (k == "novignette") j.vignette = false;
                else if (k == "vignette") j.vignette = true;
                else if (k == "lens") j.lens = true;
                else if (k == "compress") j.compress = true;
                else if (k == "live") j.hold = false;
                else if (k == "hold") j.hold = true;
                else return "pano: unknown option " + s + "\nusage: " + Help;
            }
            if (nums.Count == 1 || nums.Count == 4) j.size = (int)nums[0];
            if (nums.Count == 3 || nums.Count == 4)
            {
                int o = nums.Count - 3;
                j.eye = new Vector3(nums[o], nums[o + 1], nums[o + 2]);
                j.hasEye = true; j.eyeFrom = "point";
            }
            else if (nums.Count > 1) return "pano: give <size>, <x y z> or <size x y z>\nusage: " + Help;
            if (j.size < 256 || j.size > 16384) return "pano: size " + j.size + " outside 256..16384";
            j.size -= j.size % 4;
            if (j.samples < 1 || j.samples > 16) return "pano: samples 1..16";
            if (j.warm < 0 || j.warm > 30) return "pano: warm 0..30 frames";
            if (j.delay < 0 || j.delay > 120) return "pano: delay 0..120 frames";
            if (j.near != -1f && (j.near <= 0f || j.near > 10f)) return "pano: near must be in (0, 10] m";
            if (!(j.overscan >= 0f && j.overscan <= 45f)) return "pano: overscan 0..45 degrees";
            var root = UnityEngine.Object.FindObjectOfType<DevRoot>();
            if (root == null) return "pano: no DevRoot to run on";
            j.replaced = File.Exists(j.file);
            j.lastFrame = Time.frameCount;
            lock (gate) { j.id = ++lastId; job = j; }
            j.total.Start();
            root.StartCoroutine(Run(j, core));
            return "pano #" + j.id + " queued: " + j.file + (j.replaced ? " (replacing the existing file)" : "") + " " + j.size + "x" + (j.size / 2) + ", eye " + (j.hasEye ? V(j.eye) : "head + " + F(EyeUp) + " m") +
                   (j.vignette ? "" : ", no vignette") + (j.lens ? ", lens kept" : "") + ", " + (j.warm + j.samples) + " frame(s) per face" + (j.samples > 1 ? " (" + j.samples + " samples)" : "") +
                   (j.near > 0f ? ", near " + F(j.near) : "") + (j.overscan > 0f ? ", overscan " + F(j.overscan) + " deg" : "") + (j.hold ? ", game held for the faces" : ", live") +
                   (j.facesDir.Length > 0 ? ", faces -> " + j.facesDir : "") + (j.compress ? ", compressed PNG" : ", stored PNG") + "; pano status";
        }

        static readonly string[] Words = { "novignette", "vignette", "lens", "compress", "live", "hold", "wait" };
        static readonly string[] Keys = { "size", "near", "samples", "warm", "delay", "overscan", "faces", "vignette", "eye" };

        // true when the first word after "pano" is an option, so no file name was given
        static bool IsOption(string s)
        {
            string k = s.ToLowerInvariant();
            float v;
            if (Array.IndexOf(Words, k) >= 0 || float.TryParse(s, NumberStyles.Float, IC, out v)) return true;
            int eq = k.IndexOf('=');
            return eq > 0 && Array.IndexOf(Keys, k.Substring(0, eq)) >= 0;
        }

        // any thread: no capture is queued, running or being written
        internal static bool Idle { get { lock (gate) return job == null; } }
        internal static int LastId { get { lock (gate) return lastId; } }

        // any thread: pano status json. busy with the running job, and "last" = the result of the last finished one
        internal static Json.Obj StatusObj()
        {
            var o = new Json.Obj();
            lock (gate)
            {
                var j = job;
                o["busy"] = j != null;
                if (j != null)
                {
                    o["id"] = (double)j.id; o["phase"] = j.phase; o["file"] = j.file; o["ms"] = (double)j.total.ElapsedMilliseconds;
                }
                if (lastObj != null) o["last"] = lastObj;
                o["text"] = j != null ? "pano #" + j.id + " " + j.phase + " " + j.file : last;
            }
            return o;
        }

        static Json.Obj Result(Job j, bool ok, string error, int W, int H)
        {
            var o = new Json.Obj();
            o["id"] = (double)j.id;
            o["ok"] = ok;
            o["file"] = j.file;
            if (ok)
            {
                o["sidecar"] = j.sidecar;
                o["width"] = (double)W; o["height"] = (double)H;
                o["face_px"] = (double)j.n;
                o["frames"] = (double)j.frames;
                o["held"] = j.held;
                o["game_time"] = Math.Round(j.gameTime, 6);
                if (j.facesDir.Length > 0) o["faces_dir"] = j.facesDir;
                var ms = new Json.Obj();
                ms["capture"] = Math.Round(j.renderMs); ms["convert"] = Math.Round(j.convertMs); ms["write"] = Math.Round(j.encodeMs); ms["total"] = (double)j.total.ElapsedMilliseconds;
                o["ms"] = ms;
            }
            else o["error"] = error ?? "";
            var w = new List<object>();
            lock (j.warn) foreach (var x in j.warn) w.Add(x);
            o["warnings"] = w;
            return o;
        }

        static string Status()
        {
            lock (gate)
            {
                var j = job;
                if (j != null) return "pano #" + j.id + " " + j.phase + " " + j.file + " (" + j.total.ElapsedMilliseconds + " ms)";
                return last;
            }
        }

        static void Finish(Job j, string text, Json.Obj result)
        {
            lock (gate)
            {
                if (j.dropped) return;
                last = text; lastObj = result;
                if (job == j) job = null;
            }
            DevLog.Write("[play] " + text);
        }

        static IEnumerator Run(Job j, DevCore core)
        {
            j.cleaned = Clean(core, true, j);
            for (int i = 0; i < j.delay; i++)
            {
                yield return null;
                if (j.dropped) yield break;
                j.lastFrame = Time.frameCount;
            }
            yield return eof;
            if (j.dropped) yield break;
            j.lastFrame = Time.frameCount;
            j.phase = "capturing";
            j.frame0 = Time.frameCount;
            var sw = Stopwatch.StartNew();
            string err = Begin(j);
            int steps = j.warm + j.samples;
            for (int f = 0; f < 6 && err == null; f++)
            {
                for (int s = 0; s < steps; s++)
                {
                    int i = s - j.warm + 1;
                    cap.fb = i <= 1 ? 0f : (i - 1) / (float)i;
                    cap.jitterOn = cap.jitter && j.samples > 1 && i >= 1;
                    yield return eof;
                    if (j.dropped) yield break;
                    j.lastFrame = Time.frameCount;
                    if (cap == null) { err = "capture state lost (restore or level load)"; break; }
                }
                if (err == null) err = Read(j, f);
                if (err == null && f < 5) err = Pose(j, f + 1);
            }
            if (cap != null)
            {
                RestorePose(j);
                cap.fb = 0f;
                cap.jitterOn = cap.jitter;
                yield return eof;
                if (j.dropped) yield break;
            }
            End();
            j.renderMs = sw.Elapsed.TotalMilliseconds;
            j.frames = Time.frameCount - j.frame0;
            if (j.cleaned) { j.cleaned = false; Clean(core, false, j); }
            if (err != null) { j.raw = null; Finish(j, "pano #" + j.id + " failed: " + err, Result(j, false, err, 0, 0)); yield break; }
            j.phase = "converting";
            var t = new Thread(() => Work(j));
            t.IsBackground = true;
            t.Name = "InsideDev pano";
            t.Start();
        }

        static bool Clean(DevCore core, bool on, Job j)
        {
            try
            {
                if (on)
                {
                    if (fClean == null)
                    {
                        var t = typeof(DevCore).Assembly.GetType("InsideDev.MultiCamClean");
                        fClean = t != null ? t.GetField("on", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
                    }
                    if (fClean != null && fClean.FieldType == typeof(bool) && (bool)fClean.GetValue(null)) return false;
                }
                Remote.RunCommand(on ? "clean on" : "clean off", core);
                return on;
            }
            catch (Exception e) { Warn(j, "clean " + (on ? "on" : "off") + ": " + e.Message); return false; }
        }

        static string Begin(Job j)
        {
            try
            {
                var cam = Camera.main;
                if (cam == null) cam = CameraControl.Current;
                if (cam == null) return "no main camera";
                var c = new Cap { cam = cam, taa = cam.GetComponent<D11PDTemporalReprojection3>(), pc = cam.GetComponent<D11PDPostCombined>(), lens = j.lens, vignette = j.vignette };
                var tr = cam.transform;
                c.prevPos = tr.position; c.prevRot = tr.rotation; c.prevFov = cam.fieldOfView; c.prevNear = cam.nearClipPlane;
                c.wasFree = CameraControl.free;
                if (c.taa != null) { c.fbMin = c.taa.feedbackMin; c.fbMax = c.taa.feedbackMax; c.blur = c.taa.motionBlurStrength; c.jitter = c.taa.frustumJitter; }
                if (c.pc != null) { c.barrel = c.pc.barrelValue; c.chroma = c.pc.colorDistortionPx720p; c.vig = c.pc.vignetteTex; }
                j.camPos = tr.position; j.camEuler = tr.eulerAngles; j.camFov = cam.fieldOfView; j.camNear = cam.nearClipPlane;
                j.gameTime = Time.timeDbl;
                j.srecT = ViewerT();
                j.vignetteName = c.pc == null ? "no D11PDPostCombined" : c.vig != null ? c.vig.name : "none";
                if (!j.hasEye && !HeadEye(out j.eye)) return "no head bone (" + HeadPath + "); give x y z";
                if (c.taa == null) Warn(j, "no D11PDTemporalReprojection3 on " + cam.name);
                if (c.pc == null) Warn(j, "no D11PDPostCombined on " + cam.name);
                if (cam != Camera.main) Warn(j, "camera " + cam.name + " is not Camera.main");
                int w = cam.pixelWidth, h = cam.pixelHeight;
                j.full = Math.Min(w, h);
                j.n = j.overscan > 0f ? (int)(j.full / Math.Tan((45.0 + j.overscan * 0.5) * Math.PI / 180.0)) & ~1 : j.full;
                if (j.n < 64) return "camera view " + w + "x" + h + (j.overscan > 0f ? " too small for overscan " + F(j.overscan) : " too small");
                c.tex = new Texture2D(j.n, j.n, TextureFormat.RGB24, false);
                c.lockBefore = CameraControl.captureLock;
                CameraControl.captureLock = true;
                cap = c;
                Capturing = true;
                if (j.hold) Hold(j, c);
                if (!hooked) { Camera.onPreRender += PreRender; hooked = true; }
                Restorer.Register(RestoreKey, "panorama capture (pano)", () => { RestorePose(j); End(); return "pano capture ended"; });
                return Pose(j, 0);
            }
            catch (Exception e) { return "begin: " + e.GetType().Name + ": " + e.Message; }
        }

        // The game stands still for the six faces unless something else already holds or runs the clock. While no agent
        // or ctl command has touched the stepper, only Time.timeScale is held (taking the stepper would switch on its focus
        // and background handling until the next restore); otherwise the stepper freezes and unfreezes.
        static void Hold(Job j, Cap c)
        {
            try
            {
                if (Time.timeScale == 0f || Stepper.Frozen) { j.held = true; return; }
                if (Stepper.Running) { Warn(j, "not held: a clock run is active, the faces show different moments"); return; }
                if (!Stepper.Clock.Touched)
                {
                    c.scaleBefore = Time.timeScale;
                    Time.timeScale = 0f;
                    c.scaleHeld = true; j.held = true;
                    return;
                }
                Stepper.Freeze();
                if (Stepper.Frozen) { c.froze = true; j.held = true; }
                else { Stepper.Unfreeze(); Warn(j, "not held: the game cannot be frozen now (loading, or a capture owns the clock)"); }
            }
            catch (Exception e) { Warn(j, "hold: " + e.Message); }
        }

        static string Pose(Job j, int f)
        {
            try
            {
                var c = cap;
                if (c == null) return "capture state lost";
                int w = c.cam.pixelWidth, h = c.cam.pixelHeight;
                if (Math.Min(w, h) != j.full) return "view size changed during the capture";
                // the centre j.n pixels are the 90 degree face: with overscan j.n is smaller than the view, so the view is wider
                float fov = 2f * Mathf.Atan(h / (float)j.n) * Mathf.Rad2Deg;
                if (!CameraControl.Set(j.eye, Quaternion.Euler(Pitch[f], Yaw[f], 0f), fov, j.near)) return "free camera unavailable";
                j.usedFov = c.cam.fieldOfView; j.usedNear = c.cam.nearClipPlane;
                if (Mathf.Abs(j.usedFov - fov) > 0.05f) return "the camera did not take the field of view " + F(fov) + " (it has " + F(j.usedFov) + "); use a smaller overscan";
                return null;
            }
            catch (Exception e) { return "pose " + Names[f] + ": " + e.GetType().Name + ": " + e.Message; }
        }

        static string Read(Job j, int f)
        {
            try
            {
                var c = cap;
                if (c == null) return "capture state lost";
                var tr = c.cam.transform;
                if (c.scaleHeld && Time.timeScale != 0f) Warn(j, Names[f] + ": the game ran during the capture (something set the time scale to " + F(Time.timeScale) + ")");
                if ((tr.position - j.eye).sqrMagnitude > 1e-4f || Quaternion.Angle(tr.rotation, Quaternion.Euler(Pitch[f], Yaw[f], 0f)) > 0.05f)
                    Warn(j, Names[f] + ": camera moved off the face pose before the read (" + V(tr.position) + ")");
                var pr = c.cam.pixelRect;
                int w = c.cam.pixelWidth, h = c.cam.pixelHeight, n = j.n;
                RenderTexture.active = null;
                c.tex.ReadPixels(new Rect((int)pr.x + (w - n) / 2, (int)pr.y + (h - n) / 2, n, n), 0, 0, false);
                j.raw[f] = c.tex.GetPixels32();
                return null;
            }
            catch (Exception e) { return "read " + Names[f] + ": " + e.GetType().Name + ": " + e.Message; }
        }

        static void RestorePose(Job j)
        {
            var c = cap;
            if (c == null) return;
            try
            {
                if (c.wasFree) CameraControl.Set(c.prevPos, c.prevRot, c.prevFov, c.prevNear);
                else
                {
                    CameraControl.SetFree(false);
                    c.cam.fieldOfView = c.prevFov;
                    c.cam.nearClipPlane = c.prevNear;
                }
            }
            catch (Exception e) { Warn(j, "camera restore: " + e.Message); }
        }

        static void PreRender(Camera cam)
        {
            var c = cap;
            if (c == null || cam != c.cam) return;
            try
            {
                if (c.pc != null)
                {
                    if (!c.lens) { c.pc.barrelValue = 0f; c.pc.colorDistortionPx720p = 0f; }
                    if (!c.vignette) c.pc.vignetteTex = Texture2D.whiteTexture;
                }
                if (c.taa != null)
                {
                    c.taa.feedbackMin = c.fb; c.taa.feedbackMax = c.fb;
                    c.taa.motionBlurStrength = 0f;
                    c.taa.frustumJitter = c.jitterOn;
                }
            }
            catch { }
        }

        static void End()
        {
            var c = cap;
            cap = null;
            Capturing = false;
            if (hooked) { Camera.onPreRender -= PreRender; hooked = false; }
            try { Restorer.Forget(RestoreKey); } catch { }
            if (c == null) return;
            CameraControl.captureLock = c.lockBefore;
            if (c.froze) { c.froze = false; try { Stepper.Unfreeze(); } catch { } }
            if (c.scaleHeld) { c.scaleHeld = false; try { if (Time.timeScale == 0f && !Stepper.Host.Paused && !Stepper.Host.Loading) Time.timeScale = c.scaleBefore; } catch { } }
            try { if (c.pc != null) { c.pc.barrelValue = c.barrel; c.pc.colorDistortionPx720p = c.chroma; c.pc.vignetteTex = c.vig; } } catch { }
            try { if (c.taa != null) { c.taa.feedbackMin = c.fbMin; c.taa.feedbackMax = c.fbMax; c.taa.motionBlurStrength = c.blur; c.taa.frustumJitter = c.jitter; } } catch { }
            if (c.tex != null) UnityEngine.Object.Destroy(c.tex);
        }

        static void Warn(Job j, string text)
        {
            lock (j.warn) if (!j.warn.Contains(text)) j.warn.Add(text);
        }

        static double ViewerT()
        {
            try
            {
                if (!looked)
                {
                    looked = true;
                    var t = typeof(DevCore).Assembly.GetType("InsideDev.SceneReplayer");
                    if (t != null)
                    {
                        const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                        fCurrent = t.GetField("current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        pActive = t.GetProperty("active", I);
                        pT = t.GetProperty("T", I);
                    }
                }
                if (fCurrent == null || pActive == null || pT == null) return double.NaN;
                var r = fCurrent.GetValue(null);
                if (r == null) return double.NaN;
                var on = pActive.GetValue(r, null);
                if (!(on is bool) || !(bool)on) return double.NaN;
                return Convert.ToDouble(pT.GetValue(r, null), IC);
            }
            catch { return double.NaN; }
        }

        static bool HeadEye(out Vector3 eye)
        {
            eye = Vector3.zero;
            Transform t = null;
            var go = GameObject.Find(HeadPath);
            if (go != null) t = go.transform;
            if (t == null)
            {
                var boy = ScriptGlobals.boy;
                if (boy != null) t = Deep(boy.transform.root, "Head");
            }
            if (t == null) return false;
            eye = t.position + Vector3.up * EyeUp;
            return true;
        }

        static Transform Deep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = Deep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        const long BandBytes = 32L << 20;

        static void Work(Job j)
        {
            string tmp = j.file + ".tmp";
            try
            {
                var sw = Stopwatch.StartNew();
                int n = j.n, target = Math.Max(64, j.size / 4), k = Math.Max(1, n / target), m = n / k, o = (n - m * k) / 2;
                j.m = m;
                float sc = n / (float)(m * k);
                var faces = new byte[6][];
                byte[][] full = j.facesDir.Length > 0 ? new byte[6][] : null;
                Par(6, f =>
                {
                    faces[f] = Box(j.raw[f], n, k, m, o);
                    if (full != null) full[f] = k == 1 ? faces[f] : Box(j.raw[f], n, 1, n, 0);
                    j.raw[f] = null;
                });
                j.raw = null;
                int W = j.size, H = j.size / 2, stride = W * 3;
                int parts = Math.Max(1, Math.Min(16, Environment.ProcessorCount));
                var dir = Path.GetDirectoryName(j.file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var write = new Stopwatch();
                j.phase = "writing";
                if (j.compress)
                {
                    var img = new byte[(long)stride * H];
                    Par(parts, p => Equirect(faces, m, sc, img, W, 0, H, H * p / parts, H * (p + 1) / parts));
                    sw.Stop();
                    write.Start();
                    var png = Png.EncodeRgb(img, W, H);
                    img = null;
                    var xmp = Xmp(W, H);
                    const int at = 8 + 25;
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        fs.Write(png, 0, at);
                        StoredPng.Chunk(fs, "iTXt", xmp, 0, xmp.Length);
                        fs.Write(png, at, png.Length - at);
                    }
                }
                else
                {
                    int bandRows = (int)Math.Max(1, Math.Min(H, BandBytes / stride));
                    var band = new byte[(long)bandRows * stride];
                    using (var png = new StoredPng(tmp, W, H, Xmp(W, H)))
                    {
                        for (int y0 = 0; y0 < H; y0 += bandRows)
                        {
                            int rows = Math.Min(bandRows, H - y0), top = y0;
                            sw.Start();
                            Par(Math.Min(parts, rows), p => Equirect(faces, m, sc, band, W, top, H, top + rows * p / Math.Min(parts, rows), top + rows * (p + 1) / Math.Min(parts, rows)));
                            sw.Stop();
                            write.Start();
                            png.Rows(band, 0, rows);
                            write.Stop();
                        }
                        write.Start();
                        png.End();
                    }
                }
                SafeFile.Replace(tmp, j.file);
                if (full != null)
                {
                    Directory.CreateDirectory(j.facesDir);
                    Par(6, i =>
                    {
                        string fp = Path.Combine(j.facesDir, Names[i] + ".png"), ft = fp + ".tmp";
                        if (j.compress) File.WriteAllBytes(ft, Png.EncodeRgb(full[i], n, n));
                        else using (var png = new StoredPng(ft, n, n, null)) { png.Rows(full[i], 0, n); png.End(); }
                        SafeFile.Replace(ft, fp);
                    });
                }
                write.Stop();
                j.convertMs = sw.Elapsed.TotalMilliseconds;
                j.encodeMs = write.Elapsed.TotalMilliseconds;
                j.total.Stop();
                j.sidecar = SidecarPath(j);
                SafeFile.WriteAllText(j.sidecar, Sidecar(j, W, H));
                string warn;
                lock (j.warn) warn = j.warn.Count > 0 ? "; warnings: " + string.Join("; ", j.warn.ToArray()) : "";
                Finish(j, "pano #" + j.id + " done " + j.file + " " + W + "x" + H + " from 6 x " + n + " px faces in " + j.frames + " frame(s): capture " + Ms(j.renderMs) + " ms, convert " + Ms(j.convertMs) +
                          " ms, write " + Ms(j.encodeMs) + " ms, total " + j.total.ElapsedMilliseconds + " ms" + warn, Result(j, true, null, W, H));
            }
            catch (Exception e)
            {
                j.raw = null;
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                string err = e.GetType().Name + ": " + e.Message;
                Finish(j, "pano #" + j.id + " failed: " + err, Result(j, false, err, 0, 0));
            }
        }

        // <name>.json next to the picture; when a file of that name is there and is not a panorama sidecar (a scenario,
        // a spec, a package.json) it is left alone and the sidecar becomes <name>.pano.json
        static string SidecarPath(Job j)
        {
            string plain = Path.ChangeExtension(j.file, ".json");
            try
            {
                if (File.Exists(plain) && File.ReadAllText(plain).IndexOf("\"projection\": \"equirectangular\"", StringComparison.Ordinal) < 0)
                {
                    string other = Path.ChangeExtension(j.file, ".pano.json");
                    Warn(j, Path.GetFileName(plain) + " exists and is not a panorama sidecar: the sidecar is " + Path.GetFileName(other));
                    return other;
                }
            }
            catch { }
            return plain;
        }

        static void Par(int count, Action<int> body)
        {
            var threads = new Thread[count];
            Exception err = null;
            for (int i = 0; i < count; i++)
            {
                int idx = i;
                threads[i] = new Thread(() => { try { body(idx); } catch (Exception e) { err = e; } });
                threads[i].IsBackground = true;
                threads[i].Start();
            }
            foreach (var t in threads) t.Join();
            if (err != null) throw new InvalidOperationException(err.Message, err);
        }

        static byte[] Box(Color32[] src, int n, int k, int m, int o)
        {
            var d = new byte[m * m * 3];
            int kk = k * k, half = kk / 2;
            for (int y = 0; y < m; y++)
            {
                for (int x = 0; x < m; x++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < k; dy++)
                    {
                        int bi = (n - 1 - (o + y * k + dy)) * n + o + x * k;
                        for (int dx = 0; dx < k; dx++) { var c = src[bi + dx]; r += c.r; g += c.g; b += c.b; }
                    }
                    int di = (y * m + x) * 3;
                    d[di] = (byte)((r + half) / kk); d[di + 1] = (byte)((g + half) / kk); d[di + 2] = (byte)((b + half) / kk);
                }
            }
            return d;
        }

        // rows y0..y1 of the W x H picture into img, whose first row is picture row top. sc = full face / the part Box used
        // (over 1 when the face size is not a multiple of the box factor), so 90 degrees map onto the whole face.
        static void Equirect(byte[][] faces, int m, float sc, byte[] img, int W, int top, int H, int y0, int y1)
        {
            var sinLon = new float[W];
            var cosLon = new float[W];
            for (int x = 0; x < W; x++)
            {
                double lon = (x + 0.5) / W * 2.0 * Math.PI - Math.PI;
                sinLon[x] = (float)Math.Sin(lon); cosLon[x] = (float)Math.Cos(lon);
            }
            float hm = 0.5f * m;
            for (int y = y0; y < y1; y++)
            {
                double lat = Math.PI / 2.0 - (y + 0.5) / H * Math.PI;
                float cl = (float)Math.Cos(lat), sl = (float)Math.Sin(lat);
                int row = (y - top) * W * 3;
                for (int x = 0; x < W; x++)
                {
                    float dx = cl * sinLon[x], dy = sl, dz = cl * cosLon[x];
                    float ax = Math.Abs(dx), ay = Math.Abs(dy), az = Math.Abs(dz);
                    int f;
                    float u, v;
                    if (az >= ax && az >= ay)
                    {
                        if (dz > 0f) { f = 0; u = dx / az; v = -dy / az; }
                        else { f = 2; u = -dx / az; v = -dy / az; }
                    }
                    else if (ax > az && ax >= ay)
                    {
                        if (dx > 0f) { f = 1; u = -dz / ax; v = -dy / ax; }
                        else { f = 3; u = dz / ax; v = -dy / ax; }
                    }
                    else if (dy > 0f) { f = 4; u = dx / ay; v = dz / ay; }
                    else { f = 5; u = dx / ay; v = -dz / ay; }
                    Sample(faces[f], m, (u * sc + 1f) * hm - 0.5f, (v * sc + 1f) * hm - 0.5f, img, row + x * 3);
                }
            }
        }

        static void Sample(byte[] p, int m, float fx, float fy, byte[] dst, int di)
        {
            float lim = m - 1;
            if (fx < 0f) fx = 0f; else if (fx > lim) fx = lim;
            if (fy < 0f) fy = 0f; else if (fy > lim) fy = lim;
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = x0 < m - 1 ? x0 + 1 : x0, y1 = y0 < m - 1 ? y0 + 1 : y0;
            float tx = fx - x0, ty = fy - y0;
            int a = (y0 * m + x0) * 3, b = (y0 * m + x1) * 3, c = (y1 * m + x0) * 3, d = (y1 * m + x1) * 3;
            for (int i = 0; i < 3; i++)
            {
                float top = p[a + i] + (p[b + i] - p[a + i]) * tx;
                float bot = p[c + i] + (p[d + i] - p[c + i]) * tx;
                dst[di + i] = (byte)(top + (bot - top) * ty + 0.5f);
            }
        }

        static byte[] Xmp(int w, int h)
        {
            string xmp = "<?xpacket begin=\"" + (char)0xFEFF + "\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                         "<rdf:Description rdf:about=\"\" xmlns:GPano=\"http://ns.google.com/photos/1.0/panorama/\" GPano:UsePanoramaViewer=\"True\" GPano:ProjectionType=\"equirectangular\"" +
                         " GPano:CroppedAreaImageWidthPixels=\"" + w + "\" GPano:CroppedAreaImageHeightPixels=\"" + h + "\" GPano:FullPanoWidthPixels=\"" + w + "\" GPano:FullPanoHeightPixels=\"" + h + "\"" +
                         " GPano:CroppedAreaLeftPixels=\"0\" GPano:CroppedAreaTopPixels=\"0\" GPano:PoseHeadingDegrees=\"0\" GPano:InitialViewHeadingDegrees=\"0\"/></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
            var body = new MemoryStream();
            var key = Encoding.ASCII.GetBytes("XML:com.adobe.xmp");
            body.Write(key, 0, key.Length);
            body.WriteByte(0); body.WriteByte(0); body.WriteByte(0); body.WriteByte(0); body.WriteByte(0);
            var text = Encoding.UTF8.GetBytes(xmp);
            body.Write(text, 0, text.Length);
            return body.ToArray();
        }

        static string Sidecar(Job j, int W, int H)
        {
            var s = new StringBuilder();
            s.Append("{\n");
            s.Append(" \"file\": ").Append(Q(j.file)).Append(",\n");
            s.Append(" \"projection\": \"equirectangular\",\n");
            s.Append(" \"size\": [").Append(W).Append(", ").Append(H).Append("],\n");
            s.Append(" \"eye\": ").Append(Arr(j.eye)).Append(",\n");
            s.Append(" \"eye_from\": ").Append(Q(j.eyeFrom == "head" ? "head bone " + HeadPath + " + " + F(EyeUp) + " m up" : "point")).Append(",\n");
            s.Append(" \"rotation\": {\"centre\": \"world +z (yaw 0, pitch 0)\", \"columns\": \"yaw -180 at the left edge, 0 at the centre, +90 (world +x) at 3/4, +180 at the right edge\", \"rows\": \"pitch +90 (world +y) at the top, -90 at the bottom\", \"roll\": 0},\n");
            s.Append(" \"faces\": [");
            for (int f = 0; f < 6; f++) s.Append(f > 0 ? ", " : "").Append("{\"name\": ").Append(Q(Names[f])).Append(", \"yaw\": ").Append(F(Yaw[f])).Append(", \"pitch\": ").Append(F(Pitch[f])).Append("}");
            s.Append("],\n");
            s.Append(" \"game_time\": ").Append(N(j.gameTime)).Append(", \"held\": ").Append(j.held ? "true" : "false").Append(",\n");
            s.Append(" \"srec_t\": ").Append(double.IsNaN(j.srecT) ? "null" : N(j.srecT)).Append(",\n");
            s.Append(" \"frame\": ").Append(j.frame0).Append(", \"frames\": ").Append(j.frames).Append(",\n");
            s.Append(" \"camera\": {\"pos\": ").Append(Arr(j.camPos)).Append(", \"euler\": ").Append(Arr(j.camEuler)).Append(", \"fov\": ").Append(F(j.camFov)).Append(", \"near\": ").Append(F(j.camNear)).Append("},\n");
            s.Append(" \"capture\": {\"method\": \"one game frame per face (free camera)\", \"face_px\": ").Append(j.n).Append(", \"face_used_px\": ").Append(j.m).Append(", \"fov\": ").Append(F(j.usedFov)).Append(", \"near\": ").Append(F(j.usedNear))
             .Append(", \"overscan_deg\": ").Append(F(j.overscan)).Append(", \"view_px\": ").Append(j.full)
             .Append(", \"vignette\": ").Append(j.vignette ? "true" : "false").Append(", \"vignette_tex\": ").Append(Q(j.vignetteName)).Append(", \"lens\": ").Append(j.lens ? "true" : "false")
             .Append(", \"warm\": ").Append(j.warm).Append(", \"samples\": ").Append(j.samples).Append(", \"png\": ").Append(j.compress ? "\"deflate\"" : "\"stored\"").Append(", \"faces_dir\": ").Append(j.facesDir.Length > 0 ? Q(j.facesDir) : "null").Append("},\n");
            s.Append(" \"ms\": {\"capture\": ").Append(Ms(j.renderMs)).Append(", \"convert\": ").Append(Ms(j.convertMs)).Append(", \"write\": ").Append(Ms(j.encodeMs)).Append(", \"total\": ").Append(j.total.ElapsedMilliseconds).Append("},\n");
            s.Append(" \"warnings\": [");
            lock (j.warn) for (int i = 0; i < j.warn.Count; i++) s.Append(i > 0 ? ", " : "").Append(Q(j.warn[i]));
            s.Append("]\n}\n");
            return s.ToString();
        }

        static string Q(string v)
        {
            var s = new StringBuilder("\"");
            foreach (var c in v ?? "")
            {
                if (c == '"' || c == '\\') s.Append('\\').Append(c);
                else if (c < ' ') s.Append("\\u").Append(((int)c).ToString("x4", IC));
                else s.Append(c);
            }
            return s.Append('"').ToString();
        }

        static string Arr(Vector3 v) { return "[" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + "]"; }
        static string V(Vector3 v) { return F(v.x) + " " + F(v.y) + " " + F(v.z); }
        static string F(float v) { return v.ToString("0.####", IC); }
        static string N(double v) { return v.ToString("0.######", IC); }
        static string Ms(double v) { return v.ToString("0", IC); }

        static float Num(string s, float fallback)
        {
            float v;
            return float.TryParse(s, NumberStyles.Float, IC, out v) ? v : fallback;
        }
    }
}
