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
    // Commands: pano <file.png> [size=4096] [x y z] [noVignette] ... (Help), pano status. Restore (key pano) ends a capture and puts the camera back.
    // Approach after yasirkula/Unity360ScreenshotCapture (MIT, Copyright (c) 2018 Suleyman Yasir KULA: cube faces to
    // equirect, GPano XMP in the PNG) and SoylentGraham's RenderToCubemap + equirect blit gist; no code copied.
    public static class Pano
    {
        public const string Help = "pano <file.png> [size=4096 | <size>] [x y z | eye=x,y,z] [noVignette] [lens] [near=m] [warm=0..30] [samples=1..16] [faces=<dir>] [compress] [delay=frames] | pano status | pano help";
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
        internal static bool Capturing;

        static Cap cap;
        static bool hooked;
        static FieldInfo fCurrent, fClean;
        static PropertyInfo pActive, pT;
        static bool looked;

        sealed class Job
        {
            public int id, size = 4096, samples = 1, warm = 1, delay = 2, n, m, frame0, frames;
            public string file = "", facesDir = "", eyeFrom = "head", vignetteName = "";
            public bool hasEye, vignette = true, lens, compress, cleaned;
            public float near = -1f, usedNear, usedFov;
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
            public bool jitter, jitterOn, lens, vignette, wasFree;
            public Texture vig;
            public Vector3 prevPos;
            public Quaternion prevRot;
            public Texture2D tex;
        }

        public static string Command(List<string> a, DevCore core)
        {
            string sub = a.Count > 1 ? a[1] : "status";
            string low = sub.ToLowerInvariant();
            if (low == "status") return Status();
            if (low == "help") return "usage: " + Help;
            lock (gate)
            {
                if (job != null && (job.phase == "queued" || job.phase == "capturing") && job.total.ElapsedMilliseconds > 30000)
                {
                    last = "pano #" + job.id + " dropped: its capture stalled (level load?)";
                    RestorePose(job);
                    job = null;
                    End();
                }
                if (job != null) return "pano: busy with #" + job.id + " (" + job.phase + "); pano status says when it is done";
            }
            if (MultiCam.Capturing || CameraControl.captureLock) return "pano: mcam is capturing";
            var j = new Job();
            try { j.file = Path.GetFullPath(sub).Replace('\\', '/'); } catch (Exception e) { return "pano: bad file " + sub + ": " + e.Message; }
            if (!j.file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) j.file += ".png";
            var nums = new List<float>();
            for (int i = 2; i < a.Count; i++)
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
                    else if (key == "faces") { try { j.facesDir = Path.GetFullPath(val).Replace('\\', '/'); } catch (Exception e) { return "pano: bad faces dir " + val + ": " + e.Message; } }
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
            var root = UnityEngine.Object.FindObjectOfType<DevRoot>();
            if (root == null) return "pano: no DevRoot to run on";
            lock (gate) { j.id = ++lastId; job = j; }
            j.total.Start();
            root.StartCoroutine(Run(j, core));
            return "pano #" + j.id + " queued: " + j.file + " " + j.size + "x" + (j.size / 2) + ", eye " + (j.hasEye ? V(j.eye) : "head + " + F(EyeUp) + " m") +
                   (j.vignette ? "" : ", no vignette") + (j.lens ? ", lens kept" : "") + ", " + (j.warm + j.samples) + " frame(s) per face" + (j.samples > 1 ? " (" + j.samples + " samples)" : "") +
                   (j.near > 0f ? ", near " + F(j.near) : "") + (j.facesDir.Length > 0 ? ", faces -> " + j.facesDir : "") + (j.compress ? ", compressed PNG" : ", stored PNG") + "; pano status";
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

        static void Finish(Job j, string text)
        {
            lock (gate) { last = text; if (job == j) job = null; }
            DevLog.Write("[play] " + text);
        }

        static IEnumerator Run(Job j, DevCore core)
        {
            j.cleaned = Clean(core, true, j);
            for (int i = 0; i < j.delay; i++) yield return null;
            yield return eof;
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
            }
            End();
            j.renderMs = sw.Elapsed.TotalMilliseconds;
            j.frames = Time.frameCount - j.frame0;
            if (j.cleaned) Clean(core, false, j);
            if (err != null) { j.raw = null; Finish(j, "pano #" + j.id + " failed: " + err); yield break; }
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
                j.n = Math.Min(w, h);
                if (j.n < 64) return "camera view " + w + "x" + h + " too small";
                c.tex = new Texture2D(j.n, j.n, TextureFormat.RGB24, false);
                cap = c;
                Capturing = true;
                if (!hooked) { Camera.onPreRender += PreRender; hooked = true; }
                Restorer.Register(RestoreKey, "panorama capture (pano)", () => { RestorePose(j); End(); return "pano capture ended"; });
                return Pose(j, 0);
            }
            catch (Exception e) { return "begin: " + e.GetType().Name + ": " + e.Message; }
        }

        static string Pose(Job j, int f)
        {
            try
            {
                var c = cap;
                if (c == null) return "capture state lost";
                int w = c.cam.pixelWidth, h = c.cam.pixelHeight;
                if (Math.Min(w, h) != j.n) return "view size changed during the capture";
                float fov = 2f * Mathf.Atan(h / (float)j.n) * Mathf.Rad2Deg;
                if (!CameraControl.Set(j.eye, Quaternion.Euler(Pitch[f], Yaw[f], 0f), fov, j.near)) return "free camera unavailable";
                j.usedFov = c.cam.fieldOfView; j.usedNear = c.cam.nearClipPlane;
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

        static void Work(Job j)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                int n = j.n, target = Math.Max(64, j.size / 4), k = Math.Max(1, n / target), m = n / k, o = (n - m * k) / 2;
                j.m = m;
                var faces = new byte[6][];
                byte[][] full = j.facesDir.Length > 0 ? new byte[6][] : null;
                Par(6, f =>
                {
                    faces[f] = Box(j.raw[f], n, k, m, o);
                    if (full != null) full[f] = k == 1 ? faces[f] : Box(j.raw[f], n, 1, n, 0);
                    j.raw[f] = null;
                });
                int W = j.size, H = j.size / 2;
                var img = new byte[W * H * 3];
                int parts = Math.Max(1, Math.Min(16, Environment.ProcessorCount));
                Par(parts, p => Equirect(faces, m, img, W, H, H * p / parts, H * (p + 1) / parts));
                j.convertMs = sw.Elapsed.TotalMilliseconds;
                j.phase = "writing";
                sw = Stopwatch.StartNew();
                var dir = Path.GetDirectoryName(j.file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (full != null) Directory.CreateDirectory(j.facesDir);
                Par(full != null ? 7 : 1, i =>
                {
                    if (i == 0) Write(j.file, j.compress ? WithXmp(Png.EncodeRgb(img, W, H), W, H) : StoredPng(img, W, H, Xmp(W, H)));
                    else Write(Path.Combine(j.facesDir, Names[i - 1] + ".png"), j.compress ? Png.EncodeRgb(full[i - 1], n, n) : StoredPng(full[i - 1], n, n, null));
                });
                j.encodeMs = sw.Elapsed.TotalMilliseconds;
                j.total.Stop();
                Write(Path.ChangeExtension(j.file, ".json"), Encoding.UTF8.GetBytes(Sidecar(j, W, H)));
                Finish(j, "pano #" + j.id + " done " + j.file + " " + W + "x" + H + " from 6 x " + n + " px faces in " + j.frames + " frame(s): capture " + Ms(j.renderMs) + " ms, convert " + Ms(j.convertMs) +
                          " ms, write " + Ms(j.encodeMs) + " ms, total " + j.total.ElapsedMilliseconds + " ms" + (j.warn.Count > 0 ? "; warnings: " + string.Join("; ", j.warn.ToArray()) : ""));
            }
            catch (Exception e) { j.raw = null; Finish(j, "pano #" + j.id + " failed: " + e.GetType().Name + ": " + e.Message); }
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

        static void Equirect(byte[][] faces, int m, byte[] img, int W, int H, int y0, int y1)
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
                int row = y * W * 3;
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
                    Sample(faces[f], m, (u + 1f) * hm - 0.5f, (v + 1f) * hm - 0.5f, img, row + x * 3);
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

        static void Write(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        static byte[] StoredPng(byte[] px, int w, int h, byte[] itxt)
        {
            int stride = w * 3, rawLen = (stride + 1) * h;
            const int Block = 65535;
            int blocks = Math.Max(1, (rawLen + Block - 1) / Block);
            int zlen = 2 + blocks * 5 + rawLen + 4;
            var z = new byte[zlen];
            z[0] = 0x78; z[1] = 0x01;
            int zp = 2, row = 0, col = -1;
            uint s1 = 1, s2 = 0;
            for (int b = 0; b < blocks; b++)
            {
                int len = Math.Min(Block, rawLen - b * Block);
                z[zp] = (byte)(b == blocks - 1 ? 1 : 0);
                z[zp + 1] = (byte)len; z[zp + 2] = (byte)(len >> 8);
                z[zp + 3] = (byte)~len; z[zp + 4] = (byte)(~len >> 8);
                zp += 5;
                for (int i = 0; i < len; i++)
                {
                    byte v;
                    if (col < 0) { v = 0; col = 0; }
                    else
                    {
                        v = px[row * stride + col];
                        if (++col == stride) { col = -1; row++; }
                    }
                    z[zp++] = v;
                    s1 += v; if (s1 >= 65521) s1 -= 65521;
                    s2 += s1; if (s2 >= 65521) s2 -= 65521;
                }
            }
            uint ad = s2 << 16 | s1;
            z[zp] = (byte)(ad >> 24); z[zp + 1] = (byte)(ad >> 16); z[zp + 2] = (byte)(ad >> 8); z[zp + 3] = (byte)ad;
            var s = new MemoryStream(zlen + 1024);
            s.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            var hdr = new MemoryStream();
            PutBE(hdr, (uint)w); PutBE(hdr, (uint)h); hdr.WriteByte(8); hdr.WriteByte(2); hdr.WriteByte(0); hdr.WriteByte(0); hdr.WriteByte(0);
            Chunk(s, "IHDR", hdr.ToArray(), 0, 13);
            if (itxt != null) Chunk(s, "iTXt", itxt, 0, itxt.Length);
            const int Part = 1 << 20;
            for (int p = 0; p < zlen; p += Part) Chunk(s, "IDAT", z, p, Math.Min(Part, zlen - p));
            Chunk(s, "IEND", new byte[0], 0, 0);
            return s.ToArray();
        }

        static void Chunk(Stream s, string type, byte[] data, int off, int len)
        {
            var t = Encoding.ASCII.GetBytes(type);
            PutBE(s, (uint)len);
            s.Write(t, 0, 4);
            s.Write(data, off, len);
            uint c = Crc(t, 0, 4, 0xFFFFFFFFu);
            c = Crc(data, off, len, c);
            PutBE(s, c ^ 0xFFFFFFFFu);
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

        static byte[] WithXmp(byte[] png, int w, int h)
        {
            var data = Xmp(w, h);
            const int at = 8 + 25;
            var outp = new MemoryStream(png.Length + data.Length + 12);
            outp.Write(png, 0, at);
            Chunk(outp, "iTXt", data, 0, data.Length);
            outp.Write(png, at, png.Length - at);
            return outp.ToArray();
        }

        static void PutBE(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }

        static uint[] crcTable;

        static uint Crc(byte[] b, int off, int len, uint r)
        {
            if (crcTable == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                crcTable = t;
            }
            var tb = crcTable;
            for (int i = off, e = off + len; i < e; i++) r = tb[(r ^ b[i]) & 0xFF] ^ (r >> 8);
            return r;
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
            s.Append(" \"game_time\": ").Append(N(j.gameTime)).Append(",\n");
            s.Append(" \"srec_t\": ").Append(double.IsNaN(j.srecT) ? "null" : N(j.srecT)).Append(",\n");
            s.Append(" \"frame\": ").Append(j.frame0).Append(", \"frames\": ").Append(j.frames).Append(",\n");
            s.Append(" \"camera\": {\"pos\": ").Append(Arr(j.camPos)).Append(", \"euler\": ").Append(Arr(j.camEuler)).Append(", \"fov\": ").Append(F(j.camFov)).Append(", \"near\": ").Append(F(j.camNear)).Append("},\n");
            s.Append(" \"capture\": {\"method\": \"one game frame per face (free camera)\", \"face_px\": ").Append(j.n).Append(", \"face_used_px\": ").Append(j.m).Append(", \"fov\": ").Append(F(j.usedFov)).Append(", \"near\": ").Append(F(j.usedNear))
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
