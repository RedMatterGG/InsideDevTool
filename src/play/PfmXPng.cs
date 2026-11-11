using System;
using System.IO;
using System.Text;

namespace InsideDev.Play
{
    // PfmXPng.cs (owner TIME): a PNG written straight to its file, for pictures too large to build in memory first (a
    // 16384 x 8192 panorama is 400 MB of pixels). 8-bit RGB, signature, IHDR, an optional iTXt chunk, then IDAT chunks of
    // up to 1 MiB that together carry one zlib stream of stored (uncompressed) deflate blocks, then IEND. The caller hands
    // in the scanlines top to bottom; only one chunk is held at a time. No dependency on Unity or the core.
    internal sealed class StoredPng : IDisposable
    {
        const int Part = 1 << 20, Block = 65535;
        static readonly byte[] Zero = { 0 };
        static readonly byte[] None = new byte[0];
        static uint[] crcTable;

        readonly FileStream fs;
        readonly byte[] part = new byte[Part];
        readonly int stride;
        int fill, blockLeft, rowsLeft;
        long rawLeft;
        uint s1 = 1, s2;

        public StoredPng(string path, int width, int height, byte[] itxt)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("empty picture");
            stride = width * 3;
            rowsLeft = height;
            rawLeft = (long)(stride + 1) * height;
            fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            var hdr = new MemoryStream();
            PutBE(hdr, (uint)width); PutBE(hdr, (uint)height); hdr.WriteByte(8); hdr.WriteByte(2); hdr.WriteByte(0); hdr.WriteByte(0); hdr.WriteByte(0);
            Chunk(fs, "IHDR", hdr.ToArray(), 0, 13);
            if (itxt != null) Chunk(fs, "iTXt", itxt, 0, itxt.Length);
            Put(0x78); Put(0x01);
        }

        // rows scanlines of RGB bytes, the first one at src[off], each stride bytes after the one before
        public void Rows(byte[] src, int off, int rows)
        {
            if (rows > rowsLeft) throw new InvalidOperationException("more rows than the picture has");
            for (int y = 0; y < rows; y++)
            {
                Raw(Zero, 0, 1);
                Raw(src, off + y * stride, stride);
            }
            rowsLeft -= rows;
        }

        // the last row is in: the zlib checksum, the last IDAT, IEND; closes the file
        public void End()
        {
            if (rowsLeft != 0 || rawLeft != 0) throw new InvalidOperationException(rowsLeft + " row(s) of the picture were not written");
            uint ad = s2 << 16 | s1;
            Put((byte)(ad >> 24)); Put((byte)(ad >> 16)); Put((byte)(ad >> 8)); Put((byte)ad);
            Flush();
            Chunk(fs, "IEND", None, 0, 0);
            fs.Flush();
            fs.Close();
        }

        public void Dispose() { try { fs.Close(); } catch { } }

        void Raw(byte[] src, int off, int len)
        {
            while (len > 0)
            {
                if (blockLeft == 0)
                {
                    int bl = (int)Math.Min(Block, rawLeft);
                    Put((byte)(rawLeft == bl ? 1 : 0));
                    Put((byte)bl); Put((byte)(bl >> 8)); Put((byte)~bl); Put((byte)(~bl >> 8));
                    blockLeft = bl;
                }
                int n = Math.Min(len, blockLeft);
                Put(src, off, n);
                Adler(src, off, n);
                off += n; len -= n; blockLeft -= n; rawLeft -= n;
            }
        }

        void Adler(byte[] b, int off, int len)
        {
            uint a = s1, c = s2;
            while (len > 0)
            {
                int n = Math.Min(len, 5552);
                for (int e = off + n; off < e; off++) { a += b[off]; c += a; }
                a %= 65521; c %= 65521;
                len -= n;
            }
            s1 = a; s2 = c;
        }

        void Put(byte v)
        {
            if (fill == Part) Flush();
            part[fill++] = v;
        }

        void Put(byte[] src, int off, int len)
        {
            while (len > 0)
            {
                if (fill == Part) Flush();
                int n = Math.Min(len, Part - fill);
                Buffer.BlockCopy(src, off, part, fill, n);
                fill += n; off += n; len -= n;
            }
        }

        void Flush()
        {
            if (fill == 0) return;
            Chunk(fs, "IDAT", part, 0, fill);
            fill = 0;
        }

        public static void Chunk(Stream s, string type, byte[] data, int off, int len)
        {
            var t = Encoding.ASCII.GetBytes(type);
            PutBE(s, (uint)len);
            s.Write(t, 0, 4);
            s.Write(data, off, len);
            uint c = Crc(t, 0, 4, 0xFFFFFFFFu);
            c = Crc(data, off, len, c);
            PutBE(s, c ^ 0xFFFFFFFFu);
        }

        public static void PutBE(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }

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
    }
}
