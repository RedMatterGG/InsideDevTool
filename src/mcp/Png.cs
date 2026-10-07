using System;
using System.IO;

namespace InsideDev.McpExt
{
    // Minimal PNG decode / downscale / encode, Unity-free, so screenshots are scaled on a worker thread instead of the
    // game's main thread. Decodes 8-bit, non-interlaced truecolour (RGB / RGBA) - what Application.CaptureScreenshot writes.
    public static class Png
    {
        static readonly byte[] Sig = { 137, 80, 78, 71, 13, 10, 26, 10 };

        static uint BE(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }

        // returns RGB pixels (3 bytes per pixel, top row first)
        public static byte[] DecodeRgb(byte[] png, out int w, out int h)
        {
            w = h = 0;
            for (int i = 0; i < 8; i++) if (png[i] != Sig[i]) throw new FormatException("not a PNG");
            int pos = 8, bpp = 0; var idat = new MemoryStream();
            while (pos + 8 <= png.Length)
            {
                int len = (int)BE(png, pos); string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4); int d = pos + 8;
                if (type == "IHDR")
                {
                    w = (int)BE(png, d); h = (int)BE(png, d + 4);
                    int depth = png[d + 8], ct = png[d + 9], inter = png[d + 12];
                    if (depth != 8 || inter != 0 || (ct != 2 && ct != 6)) throw new FormatException("unsupported PNG (depth " + depth + ", colour type " + ct + ", interlace " + inter + ")");
                    bpp = ct == 6 ? 4 : 3;
                }
                else if (type == "IDAT") idat.Write(png, d, len);
                else if (type == "IEND") break;
                pos = d + len + 4;
            }
            if (bpp == 0) throw new FormatException("no IHDR");
            var z = idat.ToArray();
            int stride = w * bpp;
            var raw = Zlib.Inflate(z, 2, (stride + 1) * h);
            if (raw.Length < (stride + 1) * h) throw new FormatException("truncated image data");
            var outp = new byte[w * h * 3];
            var prev = new byte[stride]; var cur = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                int f = raw[y * (stride + 1)];
                Buffer.BlockCopy(raw, y * (stride + 1) + 1, cur, 0, stride);
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? cur[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0, v = cur[x];
                    switch (f)
                    {
                        case 1: v += a; break;
                        case 2: v += b; break;
                        case 3: v += (a + b) >> 1; break;
                        case 4: { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c; break; }
                    }
                    cur[x] = (byte)v;
                }
                int o = y * w * 3;
                if (bpp == 3) Buffer.BlockCopy(cur, 0, outp, o, stride);
                else for (int x = 0; x < w; x++) { outp[o + x * 3] = cur[x * 4]; outp[o + x * 3 + 1] = cur[x * 4 + 1]; outp[o + x * 3 + 2] = cur[x * 4 + 2]; }
                var t = prev; prev = cur; cur = t;
            }
            return outp;
        }

        // area-average downscale of RGB pixels
        public static byte[] Scale(byte[] src, int w, int h, int nw, int nh)
        {
            var dst = new byte[nw * nh * 3];
            for (int y = 0; y < nh; y++)
            {
                int y0 = y * h / nh, y1 = Math.Max(y0 + 1, (y + 1) * h / nh);
                for (int x = 0; x < nw; x++)
                {
                    int x0 = x * w / nw, x1 = Math.Max(x0 + 1, (x + 1) * w / nw);
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int yy = y0; yy < y1; yy++) { int row = yy * w * 3; for (int xx = x0; xx < x1; xx++) { int i = row + xx * 3; r += src[i]; g += src[i + 1]; b += src[i + 2]; n++; } }
                    int o = (y * nw + x) * 3; dst[o] = (byte)(r / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(b / n);
                }
            }
            return dst;
        }

        static readonly uint[] crcTable = MakeCrc();
        static uint[] MakeCrc() { var t = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; } return t; }
        static uint Crc(byte[] b, int o, int n) { uint c = 0xFFFFFFFFu; for (int i = o; i < o + n; i++) c = crcTable[(c ^ b[i]) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFFu; }
        static void PutBE(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        static void Chunk(Stream s, string type, byte[] data)
        {
            PutBE(s, (uint)data.Length);
            var td = new byte[4 + data.Length]; System.Text.Encoding.ASCII.GetBytes(type, 0, 4, td, 0); Buffer.BlockCopy(data, 0, td, 4, data.Length);
            s.Write(td, 0, td.Length); PutBE(s, Crc(td, 0, td.Length));
        }

        public static byte[] EncodeRgb(byte[] px, int w, int h)
        {
            int stride = w * 3;
            var raw = new byte[(stride + 1) * h];
            for (int y = 0; y < h; y++)
            {
                int ro = y * (stride + 1), po = y * stride;
                raw[ro] = 1;   // Sub filter: compresses rendered images well and is cheap
                for (int x = 0; x < stride; x++) raw[ro + 1 + x] = (byte)(px[po + x] - (x >= 3 ? px[po + x - 3] : 0));
            }
            var zdata = Zlib.Compress(raw);
            var s = new MemoryStream();
            s.Write(Sig, 0, 8);
            var hdr = new MemoryStream(); PutBE(hdr, (uint)w); PutBE(hdr, (uint)h); hdr.WriteByte(8); hdr.WriteByte(2); hdr.WriteByte(0); hdr.WriteByte(0); hdr.WriteByte(0);
            Chunk(s, "IHDR", hdr.ToArray());
            Chunk(s, "IDAT", zdata);
            Chunk(s, "IEND", new byte[0]);
            return s.ToArray();
        }

        // decode + downscale + encode; returns null when no scaling is needed
        public static byte[] Downscale(byte[] png, int maxW, out string note)
        {
            int w, h; var px = DecodeRgb(png, out w, out h);
            if (w <= maxW) { note = w + "x" + h; return null; }
            int nw = maxW, nh = Math.Max(1, (int)Math.Round(h * (maxW / (double)w)));
            note = w + "x" + h + " scaled to " + nw + "x" + nh;
            return EncodeRgb(Scale(px, w, h, nw, nh), nw, nh);
        }
    }
}
