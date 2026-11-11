using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace InsideDev.McpExt
{
    // Shot.cs (owner HOST, signatures frozen): screenshots for the screenshot tool and for observe {image}.
    // preview / marked: one main-thread hop starts a coroutine on DevRoot; at the end of the frame it reads the back
    // buffer once (ReadPixels into a kept Texture2D), scales it down on the GPU (Graphics.Blit through halving render
    // textures, then one bilinear step), reads the small result back, draws the id labels of MarkSource (marked) and
    // encodes it once with Unity's own encoder (EncodeToPNG / EncodeToJPG). The worker thread only waits, then writes
    // the encoded bytes to _mod/shots/<frame>_<n>.png|jpg; it never touches a Unity object.
    // Why the end of the frame from a coroutine started in this frame's Update: WaitForEndOfFrame callbacks run in
    // the order they were registered, and DevRoot registered its own (the editor's panel and overlays are drawn there)
    // one frame earlier, so this read comes after every camera, image effect, OnGUI and the editor's own drawing. That
    // is the image Application.CaptureScreenshot saves, which Unity takes right after the end-of-frame callbacks.
    // full / path: Application.CaptureScreenshot writes the full-size PNG as before; the black check comes from the same
    // end-of-frame read (64 x 36 GPU copy), not from decoding the file. black = mean luma below 2/255 (flag it, never
    // fail). clean hides InsideDev panels, HUD and overlays through the core's "clean on" from the next frame on and
    // puts them back with "clean off" when the last capture that asked for it is done (only when a capture switched it
    // on). Metadata: frame, camera pose (G.Cam), boy screen position (G.MainCharacter.pos3 through WorldToScreenPoint).
    public static partial class McpShot
    {
        static int counter;
        static FieldInfo cleanField;
        static bool cleanLooked;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly WaitForEndOfFrame Eof = new WaitForEndOfFrame();

        // main thread only
        static DevRoot root;
        static Texture2D full, small, tiny;
        static bool haveTextures;
        static long lastShotTicks;
        static int cleanUsers;
        static byte[] markBuf;
        const int BlackW = 64, BlackH = 36, WaitMs = 10000, ReleaseAfterS = 60;
        // an image above this many bytes is not returned inline (base64 in one JSON line; clients refuse images over 5 MB); the file has it
        internal const int InlineMaxBytes = 3584 * 1024;

        sealed class Grab
        {
            public ShotResult r;
            public ShotRequest req;
            public string dir, format;
            public int n, quality;
            public bool file, marked, cleaned;
            public volatile bool abandoned;
            public bool done;
            public byte[] data;

            public void Fail(string code, string message) { r.code = code; r.error = message; }
            public void Done() { lock (this) { done = true; Monitor.PulseAll(this); } }
        }

        // worker thread
        public static ShotResult Capture(CallContext ctx, ShotRequest req)
        {
            if (req == null) req = new ShotRequest();
            var clock = Stopwatch.StartNew();
            var r = new ShotResult { mode = NormalizeMode(req.mode) };
            if (MainQueue.OnMainThread) { r.error = "a screenshot cannot be taken from the game's main thread (it needs the end of a frame)"; r.code = McpErr.Internal; return r; }
            string dir = ShotsDir;
            if (string.IsNullOrEmpty(dir)) { r.error = "the screenshot folder is not set (the MCP extension did not start)"; r.code = McpErr.NotReady; return r; }
            try { Directory.CreateDirectory(dir); }
            catch (Exception e) { r.error = "cannot create " + dir + ": " + e.Message; r.code = McpErr.GameError; return r; }
            var g = new Grab
            {
                r = r, req = req, dir = dir, n = Interlocked.Increment(ref counter),
                file = r.mode == "full" || r.mode == "path", marked = r.mode == "marked",
                format = NormalizeFormat(req.format), quality = Math.Max(1, Math.Min(100, req.quality))
            };
            r.format = g.file ? "png" : g.format;
            try
            {
                ctx.MainDo(() => Begin(g), WaitMs);
                if (!WaitDone(ctx, g))
                {
                    Abandon(g);
                    r.error = ctx.Cancelled ? "cancelled before the screenshot was taken" : "the screenshot was not taken within 10 s (is the game window minimized or not rendering?)";
                    r.code = ctx.Cancelled ? McpErr.Cancelled : McpErr.NotReady;
                    return r;
                }
            }
            catch (McpException e) { Abandon(g); r.error = e.Message; r.code = e.code; return r; }
            if (r.error != null) return r;
            if (g.file)
            {
                if (!WaitFile(ctx, r.path))
                {
                    r.error = ctx.Cancelled ? "cancelled before the screenshot was written" : "the screenshot was not written within 10 s (is the game window minimized or not rendering?)";
                    r.code = ctx.Cancelled ? McpErr.Cancelled : McpErr.NotReady;
                    return r;
                }
            }
            else if (g.data != null)
            {
                r.bytes = g.data.Length;
                try { File.WriteAllBytes(r.path, g.data); }
                catch (Exception e) { r.note = Join(r.note, "the image was not saved as a file: " + e.Message); r.path = null; r.file = null; }
                if (g.data.Length <= InlineMaxBytes) r.png = g.data;
                else r.note = Join(r.note, "the image is " + (g.data.Length / (1024 * 1024)).ToString(IC) + " MB, too large to return inline; it is in the file at path (use a smaller max_width or format jpeg)");
            }
            Prune(dir, Keep);
            r.ms = clock.ElapsedMilliseconds;
            return r;
        }

        static string Join(string a, string b) { return string.IsNullOrEmpty(a) ? b : a + "; " + b; }

        static bool WaitDone(CallContext ctx, Grab g)
        {
            var sw = Stopwatch.StartNew();
            lock (g)
            {
                while (!g.done)
                {
                    if (ctx.Cancelled || sw.ElapsedMilliseconds >= WaitMs || ctx.RemainingMs <= 0) return false;
                    Monitor.Wait(g, 20);
                }
            }
            return true;
        }

        // worker thread: the capture is given up; a coroutine that comes back later does nothing but its clean-up
        static void Abandon(Grab g)
        {
            g.abandoned = true;
            MainQueue.Post("screenshot clean off", () => Release(g));
        }

        // main thread: this capture's hold on clean frames ends
        static void Release(Grab g)
        {
            if (!g.cleaned) return;
            g.cleaned = false;
            CleanOff();
        }

        // main thread (Update phase): clean on, then the coroutine that reads the frame at its end
        static void Begin(Grab g)
        {
            if (root == null) root = UnityEngine.Object.FindObjectOfType<DevRoot>();
            if (root == null) throw new McpException(McpErr.NotReady, "no InsideDev root object to run the capture on", "the editor has not started yet; try again in a moment");
            if (g.req.clean) g.cleaned = CleanOn();
            try { root.StartCoroutine(Run(g)); }
            catch (Exception e)
            {
                Release(g);
                throw new McpException(McpErr.GameError, "could not start the capture: " + e.GetType().Name + ": " + e.Message);
            }
        }

        static IEnumerator Run(Grab g)
        {
            // the editor's panel of this frame is already built: clean frames start with the next one
            if (g.cleaned) yield return null;
            if (g.file && !g.abandoned)
            {
                try { FileCapture(g); }
                catch (Exception e) { g.Fail(McpErr.GameError, "screenshot file: " + e.GetType().Name + ": " + e.Message); }
            }
            yield return Eof;
            try { if (!g.abandoned && g.r.error == null) Read(g); }
            catch (Exception e)
            {
                if (g.file) g.r.note = Join(g.r.note, "black not checked: " + e.GetType().Name + ": " + e.Message);
                else g.Fail(McpErr.GameError, "screenshot: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                Release(g);
                g.Done();
            }
        }

        static void Name(Grab g, string ext)
        {
            var r = g.r;
            r.frame = Time.frameCount;
            r.file = r.frame.ToString(IC) + "_" + g.n.ToString(IC) + ext;
            r.path = Path.Combine(g.dir, r.file);
        }

        // main thread, before the end of the frame: Unity writes the file when the frame is finished
        static void FileCapture(Grab g)
        {
            Name(g, ".png");
            Application.CaptureScreenshot(g.r.path);
        }

        // main thread, end of frame
        static void Read(Grab g)
        {
            var r = g.r;
            long t0 = Stopwatch.GetTimestamp();
            int sw = Screen.width, sh = Screen.height;
            if (!g.file) Name(g, g.format == "jpeg" ? ".jpg" : ".png");
            r.srcWidth = sw; r.srcHeight = sh;
            Meta(r);
            if (g.marked) Marks(g);
            if (g.file && g.req.fast)
            {
                r.width = sw; r.height = sh;
                r.note = "fast: black not checked";
                return;
            }
            if (sw <= 0 || sh <= 0) { g.Fail(McpErr.NotReady, "the game has no screen to read (" + sw + "x" + sh + ")"); return; }
            Textures(sw, sh);
            var prev = RenderTexture.active;
            var temps = new List<RenderTexture>(4);
            try
            {
                RenderTexture.active = null;
                full.ReadPixels(new Rect(0, 0, sw, sh), 0, 0, false);
                int nw = sw, nh = sh;
                int maxW = g.file ? 0 : g.req.maxWidth;
                if (maxW > 0 && sw > maxW) { nw = maxW; nh = Math.Max(1, (int)Math.Round(sh * (maxW / (double)sw))); }
                r.width = nw; r.height = nh;
                full.Apply(false, false);
                Texture src = full;
                int cw = sw, ch = sh;
                // exact 2 x 2 averages while the image is at least twice the target, then one bilinear step
                while (cw >= nw * 2 && ch >= nh * 2 && cw > 1 && ch > 1)
                {
                    cw = (cw + 1) / 2; ch = (ch + 1) / 2;
                    src = Step(src, cw, ch, temps);
                }
                if (cw != nw || ch != nh) src = Step(src, nw, nh, temps);
                Texture2D outTex = full;
                if (!ReferenceEquals(src, full))
                {
                    if (small.width != nw || small.height != nh) small.Resize(nw, nh, TextureFormat.RGB24, false);
                    RenderTexture.active = (RenderTexture)src;
                    small.ReadPixels(new Rect(0, 0, nw, nh), 0, 0, false);
                    outTex = small;
                }
                var dot = Step(src, BlackW, BlackH, temps);
                RenderTexture.active = dot;
                tiny.ReadPixels(new Rect(0, 0, BlackW, BlackH), 0, 0, false);
                RenderTexture.active = prev;
                double mean;
                r.black = Black(tiny.GetPixels32(), out mean);
                r.luma = mean;
                if (g.file) return;
                if (g.marked && r.marks != null && r.marks.Count > 0) DrawMarks(outTex, nw, nh, r.marks, sw, sh);
                g.data = g.format == "jpeg" ? outTex.EncodeToJPG(g.quality) : outTex.EncodeToPNG();
                if (g.data == null || g.data.Length == 0) { g.data = null; g.Fail(McpErr.GameError, "the engine returned no image data"); }
            }
            finally
            {
                RenderTexture.active = prev;
                foreach (var t in temps) RenderTexture.ReleaseTemporary(t);
                lastShotTicks = DateTime.UtcNow.Ticks;
                r.mainMs = Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 1);
            }
        }

        static RenderTexture Step(Texture src, int w, int h, List<RenderTexture> temps)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            temps.Add(rt);
            Graphics.Blit(src, rt);
            return rt;
        }

        static Texture2D NewTexture(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGB24, false, true);
            t.hideFlags = HideFlags.HideAndDontSave;
            t.filterMode = FilterMode.Bilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            return t;
        }

        // the kept textures: full = one screen (remade when the screen size changes), small = the scaled result,
        // tiny = the black check
        static void Textures(int sw, int sh)
        {
            if (full != null && (full.width != sw || full.height != sh)) { UnityEngine.Object.Destroy(full); full = null; }
            if (full == null) full = NewTexture(sw, sh);
            if (small == null) small = NewTexture(16, 16);
            if (tiny == null) tiny = NewTexture(BlackW, BlackH);
            haveTextures = true;
        }

        // main thread, about once a second: the textures (one screen of pixels) are freed a minute after the last shot
        internal static void Trim()
        {
            if (!haveTextures) return;
            if (DateTime.UtcNow.Ticks - lastShotTicks < ReleaseAfterS * TimeSpan.TicksPerSecond) return;
            FreeTextures();
        }

        // main thread
        internal static void FreeTextures()
        {
            haveTextures = false;
            if (full != null) UnityEngine.Object.Destroy(full);
            if (small != null) UnityEngine.Object.Destroy(small);
            if (tiny != null) UnityEngine.Object.Destroy(tiny);
            full = null; small = null; tiny = null;
            markBuf = null;
        }

        // mean luma (0..255) of the 64 x 36 copy below 2 = black
        static bool Black(Color32[] px, out double mean)
        {
            double sum = 0;
            for (int i = 0; i < px.Length; i++) sum += 0.299 * px[i].r + 0.587 * px[i].g + 0.114 * px[i].b;
            mean = px.Length > 0 ? sum / px.Length : 0;
            return mean < 2.0;
        }

        // the labels are drawn by ShotDraw on RGB bytes, top row first; a texture's pixels start at the bottom row
        static void DrawMarks(Texture2D tex, int w, int h, List<ShotMark> marks, int srcW, int srcH)
        {
            var px = tex.GetPixels32();
            int need = w * h * 3;
            if (markBuf == null || markBuf.Length < need) markBuf = new byte[need];
            var b = markBuf;
            for (int y = 0; y < h; y++)
            {
                int o = (h - 1 - y) * w * 3, i = y * w;
                for (int x = 0; x < w; x++, i++, o += 3) { var c = px[i]; b[o] = c.r; b[o + 1] = c.g; b[o + 2] = c.b; }
            }
            double sx = w / (double)srcW, sy = h / (double)srcH;
            var scaled = new List<ShotMark>();
            foreach (var m in marks) if (m != null && scaled.Count < MaxMarks) scaled.Add(new ShotMark { id = m.id, label = m.label ?? m.id, x = (float)(m.x * sx), y = (float)(m.y * sy) });
            ShotDraw.Marks(b, w, h, scaled);
            for (int y = 0; y < h; y++)
            {
                int o = (h - 1 - y) * w * 3, i = y * w;
                for (int x = 0; x < w; x++, i++, o += 3) px[i] = new Color32(b[o], b[o + 1], b[o + 2], 255);
            }
            tex.SetPixels32(px);
        }

        static void Marks(Grab g)
        {
            var r = g.r;
            if (g.req.marks != null) { r.marks = new List<ShotMark>(g.req.marks); return; }
            var src = MarkSource;
            if (src == null) { r.marks = new List<ShotMark>(); r.note = "no mark source (InsideDev.Play is not loaded)"; return; }
            try { r.marks = src(MaxMarks) ?? new List<ShotMark>(); }
            catch (Exception e) { r.marks = new List<ShotMark>(); r.note = "marks unavailable: " + e.GetType().Name + ": " + e.Message; }
            if (r.marks.Count == 0 && r.note == null) r.note = "no observed ids on screen (call observe first)";
        }

        static void Meta(ShotResult r)
        {
            Camera cam = null;
            try { cam = G.Cam(); } catch { }
            if (cam == null) return;
            var t = cam.transform; var p = t.position; var e = t.eulerAngles;
            var c = new Json.Obj();
            c["name"] = cam.name;
            c["x"] = R(p.x); c["y"] = R(p.y); c["z"] = R(p.z);
            c["pitch"] = R(e.x); c["yaw"] = R(e.y); c["roll"] = R(e.z);
            c["fov"] = R(cam.fieldOfView);
            r.camera = c;
            try
            {
                var ch = G.MainCharacter;
                if (ch != null)
                {
                    var sp = cam.WorldToScreenPoint(ch.pos3);
                    r.hasBoy = true;
                    r.boyX = sp.x; r.boyY = Screen.height - sp.y;
                    r.boyOn = sp.z > 0 && sp.x >= 0 && sp.x <= Screen.width && r.boyY >= 0 && r.boyY <= Screen.height;
                }
            }
            catch { }
        }

        static double R(float v) { return Math.Round(v, 2); }

        // worker thread, full / path: the file is complete when it has bytes and nobody holds it open any more
        static bool WaitFile(CallContext ctx, string path)
        {
            ctx.WaitFrames(1, 2000);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < WaitMs)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 0)
                        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) return f.Length > 0;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (!ctx.Sleep(15)) return false;
            }
            return false;
        }

        // main thread: true when this capture holds clean frames (and must give them back with CleanOff). Captures can
        // overlap (two clients, observe {image} next to screenshot): clean goes off with the last one.
        static bool CleanOn()
        {
            var core = DevCore.Instance;
            var f = CleanField();
            if (core == null || f == null) return false;
            if (cleanUsers > 0) { cleanUsers++; return true; }
            if ((bool)f.GetValue(null)) return false;
            Remote.RunCommand("clean on", core);
            if (!(bool)f.GetValue(null)) return false;
            cleanUsers = 1;
            return true;
        }

        static void CleanOff()
        {
            if (cleanUsers <= 0) return;
            if (--cleanUsers > 0) return;
            var core = DevCore.Instance;
            var f = CleanField();
            if (core == null || f == null || !(bool)f.GetValue(null)) return;
            Remote.RunCommand("clean off", core);
        }

        static FieldInfo CleanField()
        {
            if (cleanLooked) return cleanField;
            cleanLooked = true;
            try
            {
                var t = typeof(DevCore).Assembly.GetType("InsideDev.MultiCamClean");
                cleanField = t != null ? t.GetField("on", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
                if (cleanField != null && cleanField.FieldType != typeof(bool)) cleanField = null;
            }
            catch { cleanField = null; }
            if (cleanField == null) HostLog.Write("screenshot clean: InsideDev.MultiCamClean.on not found; shots include the editor UI");
            return cleanField;
        }
    }
}
