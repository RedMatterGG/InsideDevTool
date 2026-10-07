using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace InsideDev
{
    // Read-only access to the game's own serialized files on disk (levelN, sharedassetsN.assets, resources.assets):
    // Unity 5.0.4 serialized file v15, little-endian data, no type trees. Used to answer "what does the level file
    // say" (authored state) next to the live state, to resolve material names, and to find TextAssets such as the
    // Wwise SoundbanksInfo. Only headers and the requested objects are read; nothing is written or copied out.
    // No UnityEngine types here: the parsing runs on a worker thread.
    public sealed class SFile
    {
        public struct Obj { public long start; public uint size; public int cls; }
        public readonly string path;
        public int version; public long dataOffset; public string unity;
        public readonly Dictionary<long, Obj> objs = new Dictionary<long, Obj>();
        public readonly List<string> externals = new List<string>();

        public SFile(string path)
        {
            this.path = path;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var h = new byte[20]; Fill(fs, h, 20);
                uint meta = BE(h, 0); version = (int)BE(h, 8); dataOffset = BE(h, 12);
                if (version < 14 || version > 16) throw new Exception("serialized file version " + version + " not supported");
                var b = new byte[meta]; Fill(fs, b, (int)meta);
                int p = 0;
                int e = Array.IndexOf(b, (byte)0, p); unity = Encoding.ASCII.GetString(b, p, e - p); p = e + 1;
                p += 4;                                    // platform
                if (version >= 13) p += 1;                 // type tree flag (0 in the shipped game)
                int n = I32(b, p); p += 4;
                for (int i = 0; i < n; i++) { int cid = I32(b, p); p += 4; if (version >= 16) p += 3; if (cid < 0) p += 16; p += 16; }
                n = I32(b, p); p += 4;
                for (int i = 0; i < n; i++)
                {
                    p = (p + 3) & ~3;
                    long pid = I64(b, p); uint bs = (uint)I32(b, p + 8), sz = (uint)I32(b, p + 12); int cls = (ushort)(b[p + 20] | b[p + 21] << 8);
                    if (version >= 16) cls = I32(b, p + 16);   // (type index in v16+; the game is v15)
                    objs[pid] = new Obj { start = bs, size = sz, cls = cls };
                    p += 24 + (version >= 15 ? 1 : 0);
                }
                if (version >= 11) { n = I32(b, p); p += 4; for (int i = 0; i < n; i++) { p += 4; p = (p + 3) & ~3; p += 8; } }
                n = I32(b, p); p += 4;
                for (int i = 0; i < n; i++)
                {
                    e = Array.IndexOf(b, (byte)0, p); p = e + 1; p += 16 + 4;
                    e = Array.IndexOf(b, (byte)0, p); externals.Add(Encoding.UTF8.GetString(b, p, e - p)); p = e + 1;
                }
            }
        }

        FileStream batch;
        public void BeginBatch() { if (batch == null) batch = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536); }
        public void EndBatch() { if (batch != null) { batch.Dispose(); batch = null; } }
        public byte[] Read(long pid)
        {
            Obj o; if (!objs.TryGetValue(pid, out o)) return null;
            var d = new byte[o.size];
            lock (this)
            {
                if (batch != null) { batch.Position = dataOffset + o.start; Fill(batch, d, (int)o.size); return d; }
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { fs.Position = dataOffset + o.start; Fill(fs, d, (int)o.size); }
            }
            return d;
        }
        public int ClassOf(long pid) { Obj o; return objs.TryGetValue(pid, out o) ? o.cls : -1; }

        static void Fill(Stream s, byte[] b, int n) { int got = 0; while (got < n) { int r = s.Read(b, got, n - got); if (r <= 0) throw new EndOfStreamException(); got += r; } }
        static uint BE(byte[] b, int p) { return (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]); }
        public static int I32(byte[] b, int p) { return b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24; }
        public static long I64(byte[] b, int p) { return (uint)I32(b, p) | (long)I32(b, p + 4) << 32; }
        public static float F32(byte[] b, int p) { return BitConverter.ToSingle(b, p); }
        public static string Str(byte[] b, ref int p)
        {
            int n = I32(b, p); if (n < 0 || p + 4 + n > b.Length) { p = b.Length; return ""; }
            string s = Encoding.UTF8.GetString(b, p + 4, n); p = (p + 4 + n + 3) & ~3; return s;
        }
    }

    public static class LevelFile
    {
        // class ids of the Unity 5.0 component types compared here
        public const int GameObjectCls = 1, TransformCls = 4, RectTransformCls = 224, MonoBehaviourCls = 114, MaterialCls = 21, TextAssetCls = 49;
        static readonly HashSet<int> enabledAt12 = new HashSet<int> { 114, 108, 111, 95, 82, 20, 23, 137, 199, 81, 92, 124, 604, 212 };
        static readonly HashSet<int> colliders = new HashSet<int> { 64, 65, 135, 136 };

        public sealed class Comp { public int cls; public int fid; public long pid; public int enabled = -1; public List<string> materials; }
        public sealed class Go
        {
            public long pid; public string name, path; public bool active; public int layer; public ushort tag;
            public readonly List<Comp> comps = new List<Comp>();
            public float[] localPos;
            public readonly List<Go> children = new List<Go>();
        }
        public sealed class Level { public string file, error; public SFile sf; public readonly Dictionary<string, Go> byPath = new Dictionary<string, Go>(); public Go root; public double ms; }

        static readonly Dictionary<string, Level> levels = new Dictionary<string, Level>();
        static readonly Dictionary<string, SFile> files = new Dictionary<string, SFile>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> loading = new HashSet<string>();
        static readonly object gate = new object();
        public static string DataDir;   // set from the main thread (Application.dataPath)

        public static SFile Open(string name)
        {
            lock (gate)
            {
                SFile f; if (files.TryGetValue(name, out f)) return f;
                f = new SFile(Path.Combine(DataDir, name));
                files[name] = f;
                return f;
            }
        }

        // the parsed level (null while it is being read on the worker thread; the caller asks again next frame)
        public static Level Get(string levelFile)
        {
            lock (gate)
            {
                Level l; if (levels.TryGetValue(levelFile, out l)) return l;
                if (loading.Add(levelFile)) ThreadPool.QueueUserWorkItem(_ => Load(levelFile));
                return null;
            }
        }
        public static Level GetNow(string levelFile)
        {
            lock (gate) { Level l; if (levels.TryGetValue(levelFile, out l)) return l; }
            Load(levelFile);
            lock (gate) { return levels[levelFile]; }
        }

        static void Load(string levelFile)
        {
            var lv = new Level { file = levelFile };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var sf = Open(levelFile); lv.sf = sf;
                sf.BeginBatch();
                var gos = new Dictionary<long, Go>();
                var tr = new Dictionary<long, long[]>();          // transform pid -> [go pid, father pid]
                var kids = new Dictionary<long, List<long>>();    // transform pid -> child transform pids (level order)
                var trOfGo = new Dictionary<long, long>();
                foreach (var kv in sf.objs)
                {
                    if (kv.Value.cls == GameObjectCls)
                    {
                        var d = sf.Read(kv.Key); int p = 0;
                        var g = new Go { pid = kv.Key };
                        int n = SFile.I32(d, p); p += 4;
                        for (int i = 0; i < n; i++) { g.comps.Add(new Comp { cls = SFile.I32(d, p), fid = SFile.I32(d, p + 4), pid = SFile.I64(d, p + 8) }); p += 16; }
                        g.layer = SFile.I32(d, p); p += 4;
                        g.name = SFile.Str(d, ref p);
                        g.tag = (ushort)(d[p] | d[p + 1] << 8); g.active = d[p + 2] != 0;
                        gos[kv.Key] = g;
                    }
                    else if (kv.Value.cls == TransformCls || kv.Value.cls == RectTransformCls)
                    {
                        var d = sf.Read(kv.Key);
                        long go = SFile.I64(d, 4);
                        int p = 12 + 16 + 12 + 12; int n = SFile.I32(d, p); p += 4;
                        var l = new List<long>(n);
                        for (int i = 0; i < n; i++) l.Add(SFile.I64(d, p + i * 12 + 4));
                        p += n * 12;
                        tr[kv.Key] = new[] { go, SFile.I64(d, p + 4) };
                        kids[kv.Key] = l;
                        trOfGo[go] = kv.Key;
                    }
                }
                // component states the comparison needs (enabled flags, renderer materials)
                foreach (var g in gos.Values)
                {
                    foreach (var c in g.comps)
                    {
                        if (c.fid != 0) continue;
                        if (c.cls == TransformCls)
                        {
                            var d = sf.Read(c.pid); if (d != null && d.Length >= 40) g.localPos = new[] { SFile.F32(d, 28), SFile.F32(d, 32), SFile.F32(d, 36) };
                            continue;
                        }
                        if (!enabledAt12.Contains(c.cls) && !colliders.Contains(c.cls)) continue;
                        var b = sf.Read(c.pid); if (b == null) continue;
                        if (enabledAt12.Contains(c.cls) && b.Length > 12) c.enabled = b[12];
                        else if (b.Length > 25) c.enabled = b[25];
                        if ((c.cls == 23 || c.cls == 137 || c.cls == 199) && b.Length > 60)
                        {
                            int p = 56; int n = SFile.I32(b, p); p += 4;
                            if (n >= 0 && n < 64) { c.materials = new List<string>(); for (int i = 0; i < n; i++) { c.materials.Add(MaterialName(sf, SFile.I32(b, p), SFile.I64(b, p + 4))); p += 12; } }
                        }
                    }
                }
                // paths: root transforms in level order, children in their transform order; same-named siblings
                // get " #2", " #3" like the passive hidden scanner
                foreach (var kv in tr)
                {
                    if (kv.Value[1] != 0 && tr.ContainsKey(kv.Value[1])) continue;
                    Go g; if (!gos.TryGetValue(kv.Value[0], out g)) continue;
                    g.path = g.name; if (lv.root == null) lv.root = g;
                    lv.byPath[g.path] = g;
                    Walk(g, kv.Key, gos, tr, kids, lv);
                }
            }
            catch (Exception e) { lv.error = e.GetType().Name + ": " + e.Message; }
            finally { if (lv.sf != null) lv.sf.EndBatch(); }
            lv.ms = sw.Elapsed.TotalMilliseconds;
            lock (gate) { levels[levelFile] = lv; loading.Remove(levelFile); }
        }

        static void Walk(Go g, long tpid, Dictionary<long, Go> gos, Dictionary<long, long[]> tr, Dictionary<long, List<long>> kids, Level lv)
        {
            List<long> ks; if (!kids.TryGetValue(tpid, out ks)) return;
            var count = new Dictionary<string, int>();
            foreach (var k in ks)
            {
                long[] t; Go c; if (!tr.TryGetValue(k, out t) || !gos.TryGetValue(t[0], out c)) continue;
                int nth; count.TryGetValue(c.name, out nth); count[c.name] = ++nth;
                c.path = g.path + "/" + c.name + (nth > 1 ? " #" + nth : "");
                g.children.Add(c);
                lv.byPath[c.path] = c;
                Walk(c, k, gos, tr, kids, lv);
            }
        }

        public static string MaterialName(SFile sf, int fid, long pid)
        {
            if (pid == 0) return "(none)";
            try
            {
                SFile f = sf;
                if (fid != 0)
                {
                    if (fid - 1 >= sf.externals.Count) return "?";
                    string ext = sf.externals[fid - 1];
                    if (ext.IndexOf("unity default resources", StringComparison.OrdinalIgnoreCase) >= 0 || ext.IndexOf("builtin", StringComparison.OrdinalIgnoreCase) >= 0) return "(built-in #" + pid + ")";
                    f = Open(Path.GetFileName(ext));
                }
                var d = f.Read(pid); if (d == null) return "? #" + pid;
                int p = 0; return SFile.Str(d, ref p);
            }
            catch { return "? #" + pid; }
        }

        public static string ClassName(int cls)
        {
            switch (cls)
            {
                case 4: return "Transform"; case 20: return "Camera"; case 23: return "MeshRenderer"; case 33: return "MeshFilter"; case 54: return "Rigidbody";
                case 64: return "MeshCollider"; case 65: return "BoxCollider"; case 82: return "AudioSource"; case 95: return "Animator"; case 108: return "Light";
                case 111: return "Animation"; case 114: return "MonoBehaviour"; case 135: return "SphereCollider"; case 136: return "CapsuleCollider";
                case 137: return "SkinnedMeshRenderer"; case 198: return "ParticleSystem"; case 199: return "ParticleSystemRenderer"; case 224: return "RectTransform";
            }
            return "class " + cls;
        }

        // TextAssets in a resource file chosen by name and the start of their text (want(name, first ~400 chars));
        // only the matching ones are read in full. Returns (name, text) pairs.
        public static List<KeyValuePair<string, string>> TextAssets(string file, Func<string, string, bool> want)
        {
            var res = new List<KeyValuePair<string, string>>();
            var sf = Open(file);
            using (var fs = new FileStream(sf.path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var head = new byte[640];
                foreach (var kv in sf.objs)
                {
                    if (kv.Value.cls != TextAssetCls) continue;
                    fs.Position = sf.dataOffset + kv.Value.start;
                    int got = fs.Read(head, 0, (int)Math.Min(head.Length, kv.Value.size)); if (got < 8) continue;
                    int nl = SFile.I32(head, 0); if (nl <= 0 || nl > 200 || 4 + nl > got) continue;
                    string name = Encoding.UTF8.GetString(head, 4, nl);
                    int p = (4 + nl + 3) & ~3; if (p + 4 > got) continue;
                    int tl = SFile.I32(head, p);
                    string start = Encoding.UTF8.GetString(head, p + 4, Math.Max(0, Math.Min(tl, got - p - 4)));
                    if (!want(name, start)) continue;
                    var d = sf.Read(kv.Key); int q = 0; SFile.Str(d, ref q);
                    res.Add(new KeyValuePair<string, string>(name, SFile.Str(d, ref q)));
                }
            }
            return res;
        }
    }
}
