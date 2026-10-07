using System;
using System.IO;

namespace InsideDev.McpExt
{
    // Pure managed inflate / deflate. Unity 5.0's Mono has no MonoPosixHelper, so System.IO.Compression.DeflateStream
    // throws in game. Deflate side: LZ77 (32 KB window, hash chains) + fixed Huffman codes - good enough for PNG screenshots.
    public static class Zlib
    {
        static readonly int[] LBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        static readonly int[] LExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        static readonly int[] DBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        static readonly int[] DExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        static readonly int[] CLOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        // ---------------------------------------------------------------- inflate (raw deflate data from data[off..])
        sealed class Huff
        {
            public readonly short[] count = new short[16], symbol;
            public Huff(int n) { symbol = new short[n]; }
            public static Huff Build(byte[] lens, int n)
            {
                var h = new Huff(n);
                for (int s = 0; s < n; s++) h.count[lens[s]]++;
                h.count[0] = 0;
                var offs = new short[16];
                for (int l = 1; l < 16; l++) offs[l] = (short)(offs[l - 1] + h.count[l - 1]);
                for (int s = 0; s < n; s++) if (lens[s] != 0) h.symbol[offs[lens[s]]++] = (short)s;
                return h;
            }
        }

        sealed class BitIn
        {
            readonly byte[] d; int pos; uint buf; int cnt;
            public BitIn(byte[] d, int pos) { this.d = d; this.pos = pos; }
            public int Bits(int n)
            {
                while (cnt < n) { if (pos >= d.Length) throw new FormatException("deflate data ends early"); buf |= (uint)d[pos++] << cnt; cnt += 8; }
                int v = (int)(buf & ((1u << n) - 1)); buf >>= n; cnt -= n; return v;
            }
            public void Align() { buf = 0; cnt = 0; }
            public int Decode(Huff h)
            {
                int code = 0, first = 0, index = 0;
                for (int len = 1; len < 16; len++)
                {
                    code |= Bits(1); int c = h.count[len];
                    if (code - c < first) return h.symbol[index + (code - first)];
                    index += c; first += c; first <<= 1; code <<= 1;
                }
                throw new FormatException("bad huffman code");
            }
            public int Pos { get { return pos; } }
            public byte Byte() { if (pos >= d.Length) throw new FormatException("deflate data ends early"); return d[pos++]; }
        }

        static Huff fixedL, fixedD;
        static void Fixed()
        {
            if (fixedL != null) return;
            var l = new byte[288]; for (int i = 0; i < 288; i++) l[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
            var d = new byte[30]; for (int i = 0; i < 30; i++) d[i] = 5;
            fixedL = Huff.Build(l, 288); fixedD = Huff.Build(d, 30);
        }

        public static byte[] Inflate(byte[] data, int off, int expected)
        {
            Fixed();
            var o = new byte[Math.Max(expected, 1024)]; int op = 0;
            var br = new BitIn(data, off);
            int last;
            do
            {
                last = br.Bits(1); int type = br.Bits(2);
                if (type == 0)
                {
                    br.Align(); int len = br.Byte() | br.Byte() << 8; br.Byte(); br.Byte();
                    Ensure(ref o, op + len); for (int i = 0; i < len; i++) o[op++] = br.Byte();
                    continue;
                }
                Huff hl, hd;
                if (type == 1) { hl = fixedL; hd = fixedD; }
                else if (type == 2)
                {
                    int nlen = br.Bits(5) + 257, ndist = br.Bits(5) + 1, ncode = br.Bits(4) + 4;
                    var cl = new byte[19]; for (int i = 0; i < ncode; i++) cl[CLOrder[i]] = (byte)br.Bits(3);
                    var hc = Huff.Build(cl, 19);
                    var lens = new byte[nlen + ndist]; int k = 0;
                    while (k < nlen + ndist)
                    {
                        int sym = br.Decode(hc);
                        if (sym < 16) lens[k++] = (byte)sym;
                        else
                        {
                            byte v = 0; int rep;
                            if (sym == 16) { if (k == 0) throw new FormatException("repeat with no length"); v = lens[k - 1]; rep = 3 + br.Bits(2); }
                            else if (sym == 17) rep = 3 + br.Bits(3); else rep = 11 + br.Bits(7);
                            while (rep-- > 0) lens[k++] = v;
                        }
                    }
                    var ll = new byte[nlen]; Buffer.BlockCopy(lens, 0, ll, 0, nlen);
                    var dl = new byte[ndist]; Buffer.BlockCopy(lens, nlen, dl, 0, ndist);
                    hl = Huff.Build(ll, nlen); hd = Huff.Build(dl, ndist);
                }
                else throw new FormatException("bad block type");
                while (true)
                {
                    int sym = br.Decode(hl);
                    if (sym < 256) { Ensure(ref o, op + 1); o[op++] = (byte)sym; }
                    else if (sym == 256) break;
                    else
                    {
                        sym -= 257; if (sym >= 29) throw new FormatException("bad length");
                        int len = LBase[sym] + br.Bits(LExtra[sym]);
                        int ds = br.Decode(hd); if (ds >= 30) throw new FormatException("bad distance");
                        int dist = DBase[ds] + br.Bits(DExtra[ds]);
                        if (dist > op) throw new FormatException("distance too far");
                        Ensure(ref o, op + len);
                        for (int i = 0; i < len; i++) { o[op] = o[op - dist]; op++; }
                    }
                }
            } while (last == 0);
            if (op != o.Length) Array.Resize(ref o, op);
            return o;
        }
        static void Ensure(ref byte[] o, int n) { if (n > o.Length) Array.Resize(ref o, Math.Max(n, o.Length * 2)); }

        // ---------------------------------------------------------------- deflate (zlib wrapper, fixed Huffman)
        sealed class BitOut
        {
            public readonly MemoryStream s = new MemoryStream(); uint buf; int cnt;
            public void Put(int v, int n) { buf |= (uint)v << cnt; cnt += n; while (cnt >= 8) { s.WriteByte((byte)buf); buf >>= 8; cnt -= 8; } }
            public void PutRev(int code, int n) { int r = 0; for (int i = 0; i < n; i++) { r = (r << 1) | (code & 1); code >>= 1; } Put(r, n); }
            public void Flush() { if (cnt > 0) s.WriteByte((byte)buf); buf = 0; cnt = 0; }
        }

        static void Lit(BitOut b, int sym)
        {
            if (sym < 144) b.PutRev(0x30 + sym, 8);
            else if (sym < 256) b.PutRev(0x190 + sym - 144, 9);
            else if (sym < 280) b.PutRev(sym - 256, 7);
            else b.PutRev(0xC0 + sym - 280, 8);
        }

        public static byte[] Compress(byte[] d)
        {
            var b = new BitOut();
            b.s.WriteByte(0x78); b.s.WriteByte(0x01);
            b.Put(1, 1); b.Put(1, 2);              // final block, fixed Huffman
            const int HB = 15, HS = 1 << HB, W = 32768, MaxChain = 24;
            var head = new int[HS]; for (int i = 0; i < HS; i++) head[i] = -1;
            var prev = new int[W];
            int n = d.Length, p = 0;
            while (p < n)
            {
                int bestLen = 0, bestDist = 0;
                if (p + 2 < n)
                {
                    int h = ((d[p] << 10) ^ (d[p + 1] << 5) ^ d[p + 2]) & (HS - 1);
                    int c = head[h], chain = MaxChain, max = Math.Min(258, n - p);
                    while (c >= 0 && p - c <= W && chain-- > 0)
                    {
                        if (d[c + bestLen] == d[p + bestLen] && d[c] == d[p])
                        {
                            int l = 0; while (l < max && d[c + l] == d[p + l]) l++;
                            if (l > bestLen) { bestLen = l; bestDist = p - c; if (l == max) break; }
                        }
                        int pc = prev[c & (W - 1)]; if (pc >= c) break; c = pc;
                    }
                    prev[p & (W - 1)] = head[h]; head[h] = p;
                }
                if (bestLen >= 3)
                {
                    int li = 28; while (LBase[li] > bestLen) li--;
                    Lit(b, 257 + li); if (LExtra[li] > 0) b.Put(bestLen - LBase[li], LExtra[li]);
                    int di = 29; while (DBase[di] > bestDist) di--;
                    b.PutRev(di, 5); if (DExtra[di] > 0) b.Put(bestDist - DBase[di], DExtra[di]);
                    // index the skipped positions too
                    for (int k = 1; k < bestLen; k++)
                    {
                        int q = p + k; if (q + 2 >= n) break;
                        int h2 = ((d[q] << 10) ^ (d[q + 1] << 5) ^ d[q + 2]) & (HS - 1);
                        prev[q & (W - 1)] = head[h2]; head[h2] = q;
                    }
                    p += bestLen;
                }
                else { Lit(b, d[p]); p++; }
            }
            Lit(b, 256);
            b.Flush();
            uint a = 1, s2 = 0; foreach (var x in d) { a = (a + x) % 65521; s2 = (s2 + a) % 65521; }
            uint ad = (s2 << 16) | a;
            b.s.WriteByte((byte)(ad >> 24)); b.s.WriteByte((byte)(ad >> 16)); b.s.WriteByte((byte)(ad >> 8)); b.s.WriteByte((byte)ad);
            return b.s.ToArray();
        }
    }
}
