// ReplayLab image toolkit: end-of-frame region grabs, JPEG output, PSNR, difference masks, centroids, silhouette distance.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace InsideDev.ReplayLab
{
    public struct RectI
    {
        public int x, y, w, h;
        public bool Empty { get { return w <= 0 || h <= 0; } }
        public int Count { get { return Empty ? 0 : w * h; } }
        public RectI(int x, int y, int w, int h) { this.x = x; this.y = y; this.w = w; this.h = h; }
        public static RectI Screen() { return new RectI(0, 0, UnityEngine.Screen.width, UnityEngine.Screen.height); }

        public RectI Clamp()
        {
            int sw = UnityEngine.Screen.width, sh = UnityEngine.Screen.height;
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(sw, x + w), y1 = Math.Min(sh, y + h);
            return new RectI(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }

        public RectI Pad(int p) { return new RectI(x - p, y - p, w + 2 * p, h + 2 * p).Clamp(); }

        public static RectI Union(RectI a, RectI b)
        {
            if (a.Empty) return b;
            if (b.Empty) return a;
            int x0 = Math.Min(a.x, b.x), y0 = Math.Min(a.y, b.y), x1 = Math.Max(a.x + a.w, b.x + b.w), y1 = Math.Max(a.y + a.h, b.y + b.h);
            return new RectI(x0, y0, x1 - x0, y1 - y0);
        }

        public bool Contains(int px, int py) { return px >= x && py >= y && px < x + w && py < y + h; }

        public List<object> ToJson() { return new List<object> { x, y, w, h }; }
    }

    public sealed class Frame
    {
        public RectI r;
        public Color32[] px;
        public int frame;
        public double t;
    }

    public static class LabImage
    {
        static readonly Dictionary<long, Texture2D> texs = new Dictionary<long, Texture2D>();
        public static int quality = 92;
        public static int pending;

        static Texture2D Tex(int w, int h)
        {
            long k = ((long)w << 32) | (uint)h;
            Texture2D t;
            if (texs.TryGetValue(k, out t) && t != null) return t;
            t = new Texture2D(w, h, TextureFormat.RGB24, false);
            t.hideFlags = HideFlags.HideAndDontSave;
            texs[k] = t;
            return t;
        }

        public static void Release()
        {
            foreach (var t in texs.Values) if (t != null) UnityEngine.Object.Destroy(t);
            texs.Clear();
        }

        public static Frame Grab(RectI r, string savePath)
        {
            r = r.Clamp();
            if (r.Empty) return null;
            var tex = Tex(r.w, r.h);
            var prev = RenderTexture.active;
            RenderTexture.active = null;
            try { tex.ReadPixels(new Rect(r.x, r.y, r.w, r.h), 0, 0, false); }
            finally { RenderTexture.active = prev; }
            var f = new Frame { r = r, px = tex.GetPixels32(), frame = Time.frameCount, t = Time.realtimeSinceStartup };
            if (savePath != null) Write(savePath, tex.EncodeToJPG(quality));
            return f;
        }

        public static void Save(Frame f, string path)
        {
            if (f == null || path == null) return;
            var tex = Tex(f.r.w, f.r.h);
            tex.SetPixels32(f.px);
            Write(path, tex.EncodeToJPG(quality));
        }

        public static void SaveMask(bool[] m, RectI r, string path)
        {
            if (m == null || path == null || r.Empty) return;
            var px = new Color32[m.Length];
            for (int i = 0; i < m.Length; i++) px[i] = m[i] ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255);
            Save(new Frame { r = r, px = px }, path);
        }

        static void Write(string path, byte[] bytes)
        {
            Interlocked.Increment(ref pending);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); }
                catch (Exception e) { DevLog.Write("[lab] write " + path + ": " + e.Message); }
                finally { Interlocked.Decrement(ref pending); }
            });
        }

        public static RectI Project(Camera cam, IEnumerable<Vector3> pts, int pad)
        {
            if (cam == null) return new RectI();
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            int n = 0;
            foreach (var p in pts)
            {
                var s = cam.WorldToScreenPoint(p);
                if (s.z <= 0.01f) continue;
                n++;
                if (s.x < x0) x0 = s.x; if (s.y < y0) y0 = s.y; if (s.x > x1) x1 = s.x; if (s.y > y1) y1 = s.y;
            }
            if (n == 0) return new RectI();
            var r = new RectI((int)Math.Floor(x0) - pad, (int)Math.Floor(y0) - pad, (int)Math.Ceiling(x1 - x0) + 2 * pad + 1, (int)Math.Ceiling(y1 - y0) + 2 * pad + 1);
            return r.Clamp();
        }

        public static IEnumerable<Vector3> Corners(Bounds b)
        {
            var c = b.center; var e = b.extents;
            for (int i = 0; i < 8; i++) yield return c + new Vector3((i & 1) != 0 ? e.x : -e.x, (i & 2) != 0 ? e.y : -e.y, (i & 4) != 0 ? e.z : -e.z);
        }

        public static RectI ProjectBounds(Camera cam, Bounds b, int pad) { return Project(cam, Corners(b), pad); }

        public static Vector2 Screen(Camera cam, Vector3 p) { var s = cam.WorldToScreenPoint(p); return new Vector2(s.x, s.y); }

        public static double Psnr(Frame a, Frame b) { return a == null ? double.NaN : PsnrIn(a, b, a.r); }

        public static double PsnrIn(Frame a, Frame b, RectI sub)
        {
            if (a == null || b == null || a.r.w != b.r.w || a.r.h != b.r.h) return double.NaN;
            int x0 = Math.Max(0, sub.x - a.r.x), y0 = Math.Max(0, sub.y - a.r.y);
            int x1 = Math.Min(a.r.w, sub.x + sub.w - a.r.x), y1 = Math.Min(a.r.h, sub.y + sub.h - a.r.y);
            if (x1 <= x0 || y1 <= y0) return double.NaN;
            double se = 0; long n = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * a.r.w;
                for (int x = x0; x < x1; x++)
                {
                    var p = a.px[row + x]; var q = b.px[row + x];
                    int dr = p.r - q.r, dg = p.g - q.g, db = p.b - q.b;
                    se += dr * dr + dg * dg + db * db;
                    n += 3;
                }
            }
            if (n == 0) return double.NaN;
            double mse = se / n;
            return mse <= 1e-12 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
        }

        public static int Luma(Color32 c) { return (c.r * 54 + c.g * 183 + c.b * 19) >> 8; }

        public static bool[] Diff(Frame a, Frame b, int thr)
        {
            if (a == null || b == null || a.px.Length != b.px.Length) return null;
            var m = new bool[a.px.Length];
            for (int i = 0; i < m.Length; i++)
            {
                var p = a.px[i]; var q = b.px[i];
                int d = Math.Max(Math.Abs(p.r - q.r), Math.Max(Math.Abs(p.g - q.g), Math.Abs(p.b - q.b)));
                m[i] = d > thr;
            }
            return m;
        }

        public static bool[] Darker(Frame plate, Frame f, int thr)
        {
            if (plate == null || f == null || plate.px.Length != f.px.Length) return null;
            var m = new bool[f.px.Length];
            for (int i = 0; i < m.Length; i++) m[i] = Luma(plate.px[i]) - Luma(f.px[i]) > thr;
            return m;
        }

        public static void Exclude(bool[] m, RectI r, RectI ex)
        {
            if (m == null || ex.Empty) return;
            for (int y = Math.Max(ex.y, r.y); y < Math.Min(ex.y + ex.h, r.y + r.h); y++)
                for (int x = Math.Max(ex.x, r.x); x < Math.Min(ex.x + ex.w, r.x + r.w); x++)
                    m[(y - r.y) * r.w + (x - r.x)] = false;
        }

        public static void Despeckle(bool[] m, int w, int h)
        {
            if (m == null) return;
            var keep = new bool[m.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!m[i]) continue;
                    int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if ((dx != 0 || dy != 0) && xx >= 0 && yy >= 0 && xx < w && yy < h && m[yy * w + xx]) n++;
                        }
                    keep[i] = n >= 2;
                }
            Array.Copy(keep, m, m.Length);
        }

        public static int Count(bool[] m)
        {
            if (m == null) return 0;
            int n = 0;
            foreach (var b in m) if (b) n++;
            return n;
        }

        public static Vector2 Centroid(bool[] m, RectI r, out int n)
        {
            n = 0;
            if (m == null) return new Vector2(float.NaN, float.NaN);
            double sx = 0, sy = 0;
            for (int y = 0; y < r.h; y++)
                for (int x = 0; x < r.w; x++)
                    if (m[y * r.w + x]) { sx += x; sy += y; n++; }
            if (n == 0) return new Vector2(float.NaN, float.NaN);
            return new Vector2((float)(r.x + sx / n + 0.5), (float)(r.y + sy / n + 0.5));
        }

        public static double RowExtent(bool[] m, RectI r)
        {
            if (m == null) return double.NaN;
            var widths = new List<double>();
            for (int y = 0; y < r.h; y++)
            {
                int x0 = -1, x1 = -1, n = 0;
                for (int x = 0; x < r.w; x++) if (m[y * r.w + x]) { if (x0 < 0) x0 = x; x1 = x; n++; }
                if (n >= 3) widths.Add(x1 - x0 + 1);
            }
            return LabUtil.Median(widths);
        }

        static int[] Integral(bool[] m, int w, int h)
        {
            var s = new int[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                int row = 0;
                for (int x = 0; x < w; x++)
                {
                    row += m[y * w + x] ? 1 : 0;
                    s[(y + 1) * (w + 1) + x + 1] = s[y * (w + 1) + x + 1] + row;
                }
            }
            return s;
        }

        static double Coverage(bool[] a, int[] sb, int w, int h, int r)
        {
            int n = 0, hit = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!a[y * w + x]) continue;
                    n++;
                    int x0 = Math.Max(0, x - r), y0 = Math.Max(0, y - r), x1 = Math.Min(w, x + r + 1), y1 = Math.Min(h, y + r + 1);
                    int s = sb[y1 * (w + 1) + x1] - sb[y0 * (w + 1) + x1] - sb[y1 * (w + 1) + x0] + sb[y0 * (w + 1) + x0];
                    if (s > 0) hit++;
                }
            return n == 0 ? 1.0 : (double)hit / n;
        }

        public static int SilhouetteDistance(bool[] a, bool[] b, int w, int h, int maxR, double cover)
        {
            if (a == null || b == null) return -1;
            var sa = Integral(a, w, h); var sb = Integral(b, w, h);
            for (int r = 0; r <= maxR; r++)
                if (Coverage(a, sb, w, h, r) >= cover && Coverage(b, sa, w, h, r) >= cover) return r;
            return maxR + 1;
        }
    }
}
