using System;
using System.Collections.Generic;
using System.IO;

namespace InsideDev.McpExt
{
    // ShotDraw.cs (owner HOST): the Unity-free half of McpShot (Shot.cs has the capture). Types of the screenshot
    // service, Describe / Attach, the worker-side processing (decode, black check, scale, set-of-mark labels, encode)
    // with a built-in 5x7 font, the shots folder (_mod/shots, newest 200 kept) and the inside://shots/<file> resource.
    public sealed class ShotMark
    {
        public string id, label;
        // screen pixels of the full-size capture, origin top-left
        public float x, y;
    }

    public sealed class ShotRequest
    {
        public string mode = "preview";
        public int maxWidth = 960;
        // hide InsideDev panels, HUD and overlays for this capture
        public bool clean = true;
        // marks to draw (mode marked); null = ask McpShot.MarkSource
        public List<ShotMark> marks;
    }

    public sealed class ShotResult
    {
        public byte[] png;
        public string path, mode, error;
        public int width, height, srcWidth, srcHeight;
        public bool black;
        public long frame;
        public List<ShotMark> marks;
        internal string code, note, file;
        internal Json.Obj camera;
        internal bool hasBoy, boyOn;
        internal float boyX, boyY;
        internal double luma = -1;
    }

    public static partial class McpShot
    {
        // set by InsideDev.Play (Observer.ScreenMarks): main thread, returns at most max marks of the ids of the latest
        // observation that are on screen
        public static Func<int, List<ShotMark>> MarkSource;

        internal static string ShotsDir;
        internal const string UriPrefix = "inside://shots/";
        internal static int Keep = 200;
        internal const int MaxMarks = 64;

        // structured fields {mode, width, height, src_width, src_height, path, black, frame, marks[{id, label, x, y}]}
        // with x / y scaled to the returned image
        public static Json.Obj Describe(ShotResult r)
        {
            var o = new Json.Obj();
            if (r == null) return o;
            o["mode"] = r.mode ?? "preview";
            o["width"] = (double)r.width; o["height"] = (double)r.height;
            o["src_width"] = (double)r.srcWidth; o["src_height"] = (double)r.srcHeight;
            if (r.path != null) o["path"] = r.path;
            if (r.file != null) o["uri"] = UriPrefix + r.file;
            o["black"] = r.black;
            o["frame"] = (double)r.frame;
            double sx = r.srcWidth > 0 && r.width > 0 ? r.width / (double)r.srcWidth : 1.0;
            double sy = r.srcHeight > 0 && r.height > 0 ? r.height / (double)r.srcHeight : 1.0;
            if (r.marks != null)
            {
                var l = new List<object>();
                foreach (var m in r.marks)
                {
                    if (m == null) continue;
                    var mo = new Json.Obj();
                    mo["id"] = m.id ?? ""; mo["label"] = m.label ?? m.id ?? "";
                    mo["x"] = Math.Round(m.x * sx, 1); mo["y"] = Math.Round(m.y * sy, 1);
                    l.Add(mo);
                    if (l.Count >= MaxMarks) break;
                }
                o["marks"] = l;
            }
            if (r.camera != null) o["camera"] = r.camera;
            if (r.hasBoy)
            {
                var b = new Json.Obj();
                b["x"] = Math.Round(r.boyX * sx, 1); b["y"] = Math.Round(r.boyY * sy, 1); b["on_screen"] = r.boyOn;
                o["boy"] = b;
            }
            if (r.note != null) o["note"] = r.note;
            if (r.error != null) o["problem"] = r.error;
            return o;
        }

        // attach r.png (when present) to result as an image block and Describe(r) as structured["image"]
        public static void Attach(ToolResult result, ShotResult r)
        {
            if (result == null || r == null) return;
            if (r.png != null) result.AddImage(r.png, "image/png");
            if (result.structured == null)
            {
                result.structured = new Json.Obj();
                result.structured["ok"] = !result.isError;
            }
            result.structured["image"] = Describe(r);
        }

        internal static string NormalizeMode(string mode)
        {
            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "marked": return "marked";
                case "full": return "full";
                case "path": case "path_only": return "path";
                default: return "preview";
            }
        }

        // worker thread: decode, black check, scale and label the captured PNG into r (mode decides what comes back inline)
        internal static void Process(ShotResult r, byte[] png, ShotRequest req)
        {
            string mode = NormalizeMode(r.mode ?? (req != null ? req.mode : null));
            r.mode = mode;
            byte[] px = null;
            int w = 0, h = 0;
            try
            {
                px = Png.DecodeRgb(png, out w, out h);
                r.srcWidth = w; r.srcHeight = h;
                double mean;
                r.black = ShotDraw.IsBlack(px, w, h, out mean);
                r.luma = mean;
            }
            catch (Exception e) { r.note = "not decoded: " + e.Message; px = null; }
            if (mode == "full" || mode == "path")
            {
                r.width = r.srcWidth; r.height = r.srcHeight; r.png = null;
                return;
            }
            if (px == null) { r.png = png; r.width = r.srcWidth; r.height = r.srcHeight; return; }
            int maxW = req != null ? req.maxWidth : 960;
            int nw = w, nh = h;
            byte[] outPx = px;
            if (maxW > 0 && w > maxW)
            {
                nw = maxW; nh = Math.Max(1, (int)Math.Round(h * (maxW / (double)w)));
                outPx = Png.Scale(px, w, h, nw, nh);
            }
            r.width = nw; r.height = nh;
            bool drew = false;
            if (mode == "marked" && r.marks != null && r.marks.Count > 0)
            {
                double sx = nw / (double)w, sy = nh / (double)h;
                var scaled = new List<ShotMark>();
                foreach (var m in r.marks) if (m != null && scaled.Count < MaxMarks) scaled.Add(new ShotMark { id = m.id, label = m.label ?? m.id, x = (float)(m.x * sx), y = (float)(m.y * sy) });
                ShotDraw.Marks(outPx, nw, nh, scaled);
                drew = true;
            }
            r.png = ReferenceEquals(outPx, px) && !drew ? png : Png.EncodeRgb(outPx, nw, nh);
        }

        // newest keep files stay
        internal static void Prune(string dir, int keep)
        {
            try
            {
                var files = Directory.GetFiles(dir, "*.png");
                if (files.Length <= keep) return;
                var times = new DateTime[files.Length];
                for (int i = 0; i < files.Length; i++) { try { times[i] = File.GetLastWriteTimeUtc(files[i]); } catch { times[i] = DateTime.MinValue; } }
                Array.Sort(times, files);
                for (int i = 0; i < files.Length - keep; i++) { try { File.Delete(files[i]); } catch { } }
            }
            catch { }
        }

        // inside://shots/<file>: the full-size PNG of a capture (blob), null when the uri is not a shot or the file is gone
        internal static ResourceContents ReadShot(string uri)
        {
            if (uri == null || !uri.StartsWith(UriPrefix, StringComparison.Ordinal) || ShotsDir == null) return null;
            string name = uri.Substring(UriPrefix.Length);
            if (!SafeName(name)) throw new McpException(McpErr.BadArgs, "bad screenshot name " + name, "use the uri from a screenshot result");
            string p = Path.Combine(ShotsDir, name);
            if (!File.Exists(p)) return null;
            return new ResourceContents { uri = uri, mimeType = "image/png", blob = File.ReadAllBytes(p) };
        }

        internal static bool SafeName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64 || !name.EndsWith(".png", StringComparison.Ordinal)) return false;
            for (int i = 0; i < name.Length - 4; i++) { char c = name[i]; if (!(c >= '0' && c <= '9') && c != '_') return false; }
            return true;
        }
    }

    // set-of-mark drawing on RGB pixels (3 bytes per pixel, top row first) with a 5x7 font
    internal static class ShotDraw
    {
        const string Chars = "0123456789abcdefghijklmnopqrstuvwxyz -_:./#()?+,";
        static readonly string[] Glyphs =
        {
            ".###.#...##..###.#.###..##...#.###.", "..#...##....#....#....#....#...###.", ".###.#...#....#...#...#...#...#####", "#####...#...#.....#.....##...#.###.",
            "...#...##..#.#.#..#.#####...#....#.", "######....####.....#....##...#.###.", "..##..#...#....####.#...##...#.###.", "#####....#...#...#...#....#....#...",
            ".###.#...##...#.###.#...##...#.###.", ".###.#...##...#.####....#...#..##..", "...........###.....#.#####...#.####", "#....#....#.##.##..##...##...#####.",
            "...........###.#....#....#...#.###.", "....#....#.##.##..###...##...#.####", "...........###.#...#######.....###.", "..##..#..#.#...###...#....#....#...",
            "......#####...##...#.####....#.###.", "#....#....#.##.##..##...##...##...#", "..#........##....#....#....#...###.", "...#........##....#....#.#..#..##..",
            "#....#....#..#.#.#..##...#.#..#..#.", ".##....#....#....#....#....#...###.", "..........##.#.#.#.##.#.##...##...#", "..........#.##.##..##...##...##...#",
            "...........###.#...##...##...#.###.", ".....####.#...##...#####.#....#....", "......#####...##...#.####....#....#", "..........#.##.##..##....#....#....",
            "...........#####.....###.....#####.", ".#....#...###...#....#....#..#..##.", "..........#...##...##...##..##.##.#", "..........#...##...##...#.#.#...#..",
            "..........#...##...##.#.##.#.#.#.#.", "..........#...#.#.#...#...#.#.#...#", ".....#...##...##...#.####....#.###.", "..........#####...#...#...#...#####",
            "...................................", "...............#####...............", "..............................#####", "......##...##........##...##.......",
            "..........................##...##..", ".........#...#...#...#...#.........", ".#.#..#.#.#####.#.#.#####.#.#..#.#.", "...#...#...#....#....#.....#.....#.",
            ".#.....#.....#....#....#...#...#...", ".###.#...#....#...#...#.........#..", ".......#....#..#####..#....#.......", ".....................##....#...#..."
        };

        internal static bool Glyph(char c, int col, int row)
        {
            int i = Chars.IndexOf(char.ToLowerInvariant(c));
            if (i < 0) i = Chars.IndexOf('?');
            var g = Glyphs[i];
            int k = row * 5 + col;
            return k < g.Length && g[k] == '#';
        }

        internal static int TextWidth(string s, int scale) { return string.IsNullOrEmpty(s) ? 0 : s.Length * 6 * scale - scale; }

        internal static void Put(byte[] px, int w, int h, int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int o = (y * w + x) * 3;
            px[o] = r; px[o + 1] = g; px[o + 2] = b;
        }

        internal static void Fill(byte[] px, int w, int h, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
        {
            for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++) Put(px, w, h, x, y, r, g, b);
        }

        internal static void Text(byte[] px, int w, int h, int x, int y, string s, int scale, byte r, byte g, byte b)
        {
            if (string.IsNullOrEmpty(s)) return;
            for (int i = 0; i < s.Length; i++)
            {
                int cx = x + i * 6 * scale;
                for (int row = 0; row < 7; row++)
                    for (int col = 0; col < 5; col++)
                        if (Glyph(s[i], col, row)) Fill(px, w, h, cx + col * scale, y + row * scale, cx + (col + 1) * scale, y + (row + 1) * scale, r, g, b);
            }
        }

        internal static void Dot(byte[] px, int w, int h, int cx, int cy, int rad, byte r, byte g, byte b)
        {
            for (int y = -rad; y <= rad; y++)
                for (int x = -rad; x <= rad; x++)
                    if (x * x + y * y <= rad * rad) Put(px, w, h, cx + x, cy + y, r, g, b);
        }

        // labels with a dot at the object; boxes are pushed down when they would overlap an earlier label
        internal static void Marks(byte[] px, int w, int h, List<ShotMark> marks)
        {
            int scale = w >= 1400 ? 3 : w >= 600 ? 2 : 1;
            var boxes = new List<int[]>();
            foreach (var m in marks)
            {
                int cx = (int)Math.Round(m.x), cy = (int)Math.Round(m.y);
                if (cx < -8 || cy < -8 || cx > w + 8 || cy > h + 8) continue;
                Dot(px, w, h, cx, cy, 2 * scale + 1, 0, 0, 0);
                Dot(px, w, h, cx, cy, 2 * scale, 255, 214, 0);
                string label = Clip(m.label ?? m.id ?? "?", 24);
                int pad = scale + 1, bw = TextWidth(label, scale) + 2 * pad, bh = 7 * scale + 2 * pad;
                int bx = cx + 3 * scale + 2, by = cy - bh - 2 * scale;
                if (bx + bw > w) bx = cx - 3 * scale - 2 - bw;
                if (bx < 0) bx = 0;
                if (by < 0) by = cy + 3 * scale + 2;
                if (by + bh > h) by = Math.Max(0, h - bh);
                for (int tries = 0; tries < 6 && Overlaps(boxes, bx, by, bw, bh); tries++)
                {
                    by += bh + 1;
                    if (by + bh > h) by = Math.Max(0, cy - (bh + 1) * (tries + 2));
                }
                Fill(px, w, h, bx, by, bx + bw, by + bh, 255, 214, 0);
                Fill(px, w, h, bx + 1, by + 1, bx + bw - 1, by + bh - 1, 16, 16, 16);
                Text(px, w, h, bx + pad, by + pad, label, scale, 255, 255, 255);
                boxes.Add(new[] { bx, by, bw, bh });
            }
        }

        static bool Overlaps(List<int[]> boxes, int x, int y, int bw, int bh)
        {
            foreach (var b in boxes) if (x < b[0] + b[2] && b[0] < x + bw && y < b[1] + b[3] && b[1] < y + bh) return true;
            return false;
        }

        static string Clip(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "+"; }

        // mean luma (0..255) over a sparse grid below 2 = black
        internal static bool IsBlack(byte[] px, int w, int h, out double mean)
        {
            mean = 0;
            if (px == null || w <= 0 || h <= 0) return false;
            int gx = Math.Min(w, 64), gy = Math.Min(h, 36);
            double sum = 0; int n = 0;
            for (int j = 0; j < gy; j++)
            {
                int y = (int)((j * 2L + 1) * h / (2L * gy));
                for (int i = 0; i < gx; i++)
                {
                    int x = (int)((i * 2L + 1) * w / (2L * gx));
                    int o = (y * w + x) * 3;
                    sum += 0.299 * px[o] + 0.587 * px[o + 1] + 0.114 * px[o + 2];
                    n++;
                }
            }
            mean = n > 0 ? sum / n : 0;
            return mean < 2.0;
        }
    }
}
