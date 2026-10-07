using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace InsideDev
{
    // Which sound FILES an event ends up playing: wem id -> source file name (Wwise SoundbanksInfo shipped in
    // resources.assets), Wwise object path (bank report text assets), where it is stored (inside a bank, or a
    // streamed .wem in StreamingAssets\Audio), its size, and codec / channels / rate / length from the wem header.
    // Metadata only: at most the first 512 bytes (the RIFF header) of a wem are read; audio is never decoded,
    // extracted or copied.
    public static class AudioMedia
    {
        public sealed class Info
        {
            public uint id; public string shortName, wwisePath, bank; public bool streamed, prefetch, inBank;
            public long size = -1; public bool headerRead; public string codec, error; public int channels, rate; public double seconds = -1;
            public bool fileMissing;
            public string File { get { if (shortName == null) return "#" + id + ".wem"; int k = shortName.LastIndexOf('\\'); return k >= 0 ? shortName.Substring(k + 1) : shortName; } }
        }

        static readonly Dictionary<uint, Info> infos = new Dictionary<uint, Info>();
        static int state;   // 0 idle, 1 loading, 2 ready, 3 failed
        public static string status = "not loaded";
        static string dataDir, streamDir, bankDir;

        public static bool Ready { get { return state == 2; } }

        public static void EnsureStarted()
        {
            if (state != 0) return;
            state = 1; status = "reading SoundbanksInfo…";
            dataDir = Application.dataPath; streamDir = Path.Combine(Application.streamingAssetsPath, "Audio"); bankDir = streamDir;
            if (LevelFile.DataDir == null) LevelFile.DataDir = dataDir;
            WwiseBanks.MediaLabel = Label;
            ThreadPool.QueueUserWorkItem(_ => Load());
        }

        static readonly Regex rxFile = new Regex(@"<File Id=""(\d+)""[^>]*>\s*<ShortName>([^<]*)</ShortName>", RegexOptions.Compiled);

        static void Load()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var map = new Dictionary<uint, Info>();
                // SoundbanksInfo: three platform copies ship; the Windows one matches the PC banks
                var si = LevelFile.TextAssets("resources.assets", (n, head) => n == "SoundbanksInfo" && head.Contains("Platform=\"Windows\""));
                foreach (var kv in si)
                {
                    string x = kv.Value;
                    int streamedAt = x.IndexOf("<StreamedFiles>", StringComparison.Ordinal), streamedEnd = x.IndexOf("</StreamedFiles>", StringComparison.Ordinal);
                    foreach (Match m in rxFile.Matches(x))
                    {
                        uint id; if (!uint.TryParse(m.Groups[1].Value, out id)) continue;
                        Info i; if (!map.TryGetValue(id, out i)) map[id] = i = new Info { id = id };
                        i.shortName = m.Groups[2].Value;
                        if (m.Index > streamedAt && m.Index < streamedEnd) i.streamed = true;
                    }
                }
                // bank reports (Game2 / Boy / Huddle / Init): media rows carry the Wwise object path
                var taken = new HashSet<string>();   // the platform copies share ids and paths: read one of each
                var reports = LevelFile.TextAssets("resources.assets", (n, head) => (n == "Game2" || n == "Boy" || n == "Huddle" || n == "Init") && head.StartsWith("Event\tID") && taken.Add(n));
                var doneBanks = new HashSet<string>();
                foreach (var kv in reports)
                {
                    if (!doneBanks.Add(kv.Key)) continue;   // the platform copies share ids and paths
                    bool media = false;
                    foreach (var line in kv.Value.Split('\n'))
                    {
                        if (line.Length > 0 && line[0] != '\t') { media = line.StartsWith("In Memory Audio") || line.StartsWith("Streamed Audio"); continue; }
                        if (!media) continue;
                        var c = line.TrimEnd('\r').Split('\t');
                        uint id; if (c.Length < 3 || !uint.TryParse(c[1], out id)) continue;
                        Info i; if (!map.TryGetValue(id, out i)) map[id] = i = new Info { id = id };
                        foreach (var col in c) if (col.StartsWith("\\Actor-Mixer") || col.StartsWith("\\Interactive Music")) { i.wwisePath = col; break; }
                    }
                }
                lock (infos) { foreach (var kv in map) infos[kv.Key] = kv.Value; }
                status = map.Count + " sound files named (" + sw.Elapsed.TotalMilliseconds.ToString("0") + " ms)";
                state = 2;
            }
            catch (Exception e) { status = "failed: " + e.Message; state = 3; }
        }

        public static Info Get(uint id)
        {
            EnsureStarted();
            Info i;
            lock (infos) { if (!infos.TryGetValue(id, out i)) infos[id] = i = new Info { id = id }; }
            if (!i.headerRead) ReadHeader(i);
            return i;
        }

        // where the media is and its RIFF header (first 512 bytes)
        static void ReadHeader(Info i)
        {
            i.headerRead = true;
            try
            {
                string bank; uint[] loc;
                byte[] h = null;
                if (WwiseBanks.mediaBank.TryGetValue(i.id, out bank) && WwiseBanks.mediaLoc.TryGetValue(i.id, out loc))
                {
                    i.bank = bank; i.inBank = true; i.size = loc[1];
                    WwiseBanks.Bank bk = null; foreach (var b in WwiseBanks.banks) if (b.name == bank) bk = b;
                    if (bk != null && bk.dataPos > 0)
                        using (var fs = new FileStream(bk.file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        { fs.Position = bk.dataPos + loc[0]; h = new byte[Math.Min(512, (int)loc[1])]; fs.Read(h, 0, h.Length); }
                }
                string f = Path.Combine(streamDir, i.id + ".wem");
                if (File.Exists(f))
                {
                    i.streamed = true; if (i.inBank) i.prefetch = true;
                    var fi = new FileInfo(f); if (!i.inBank) i.size = fi.Length;
                    if (h == null || i.prefetch) using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { h = new byte[Math.Min(512, (int)fi.Length)]; fs.Read(h, 0, h.Length); i.size = fi.Length; }
                }
                else if (!i.inBank) { i.fileMissing = true; return; }
                if (h != null) ParseRiff(i, h);
            }
            catch (Exception e) { i.error = e.Message; }
        }

        static int I32(byte[] b, int p) { return b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24; }
        static int U16(byte[] b, int p) { return b[p] | b[p + 1] << 8; }

        static void ParseRiff(Info i, byte[] h)
        {
            if (h.Length < 12 || Encoding.ASCII.GetString(h, 0, 4) != "RIFF") { i.codec = "not RIFF"; return; }
            int p = 12, fmtAt = -1, fmtSize = 0, vorbAt = -1; long dataSize = -1;
            while (p + 8 <= h.Length)
            {
                string tag = Encoding.ASCII.GetString(h, p, 4); int sz = I32(h, p + 4);
                if (tag == "fmt ") { fmtAt = p + 8; fmtSize = sz; }
                else if (tag == "vorb") vorbAt = p + 8;
                else if (tag == "data") { dataSize = (uint)sz; break; }
                p += 8 + sz + (sz & 1);
                if (sz < 0) break;
            }
            if (fmtAt < 0 || fmtAt + 16 > h.Length) { i.codec = "?"; return; }
            int tagFmt = U16(h, fmtAt); i.channels = U16(h, fmtAt + 2); i.rate = I32(h, fmtAt + 4);
            int blockAlign = U16(h, fmtAt + 12), bits = U16(h, fmtAt + 14);
            switch (tagFmt)
            {
                case 0xFFFF:
                    i.codec = "Vorbis";
                    if (vorbAt < 0 && fmtSize == 0x42) vorbAt = fmtAt + 0x18;
                    if (vorbAt >= 0 && vorbAt + 4 <= h.Length && i.rate > 0) i.seconds = (uint)I32(h, vorbAt) / (double)i.rate;
                    break;
                case 0x0001: case 0xFFFE:
                    i.codec = "PCM " + bits + "-bit";
                    if (dataSize > 0 && blockAlign > 0 && i.rate > 0) i.seconds = dataSize / (double)blockAlign / i.rate;
                    break;
                case 0x0002: case 0x0011:
                    i.codec = "ADPCM";
                    if (dataSize > 0 && blockAlign > 0 && i.rate > 0 && i.channels > 0) i.seconds = dataSize / (double)blockAlign * 64 / i.rate;
                    break;
                default: i.codec = "format 0x" + tagFmt.ToString("X4"); break;
            }
        }

        public static string Size(long b) { return b < 0 ? "?" : b < 1024 ? b + " B" : b < 1048576 ? (b / 1024.0).ToString("0") + " KB" : (b / 1048576.0).ToString("0.0") + " MB"; }

        public static string Where(Info i)
        {
            if (i.fileMissing) return "MISSING: not in any bank and no streamed file " + i.id + ".wem (cannot play)";
            if (i.inBank && i.prefetch) return "start in bank " + i.bank + ", rest streamed from " + i.id + ".wem";
            if (i.inBank) return "inside bank " + i.bank + ".bnk";
            if (i.streamed) return "streamed from StreamingAssets\\Audio\\" + i.id + ".wem";
            return "?";
        }

        // one-line label used by the Wwise tree, Audio DB and graph
        public static string Label(uint id)
        {
            var i = Get(id);
            var sb = new StringBuilder(i.File);
            if (i.seconds >= 0) sb.Append(", ").Append(i.seconds.ToString("0.0")).Append(" s");
            sb.Append(" [").Append(i.fileMissing ? "MISSING" : i.inBank ? (i.prefetch ? "prefetch " : "bank ") + i.bank : "streamed").Append(", ").Append(Size(i.size)).Append(']');
            return sb.ToString();
        }

        public static List<string> Details(uint id)
        {
            var i = Get(id); var l = new List<string>();
            l.Add(i.File + "   (media id " + id + ")");
            if (i.shortName != null) l.Add("    source file: " + i.shortName);
            if (i.wwisePath != null) l.Add("    Wwise object: " + i.wwisePath);
            l.Add("    " + Where(i) + ",  " + Size(i.size));
            if (i.codec != null) l.Add("    " + i.codec + ", " + (i.channels == 1 ? "mono" : i.channels == 2 ? "stereo" : i.channels + " ch") + ", " + (i.rate / 1000.0).ToString("0.#") + " kHz" + (i.seconds >= 0 ? ", " + i.seconds.ToString("0.00") + " s" : ""));
            if (i.error != null) l.Add("    header: " + i.error);
            return l;
        }

        // every media id reachable from a node (event -> play actions -> containers -> sounds / tracks)
        public static List<uint> MediaUnder(uint id, int max = 200)
        {
            var res = new List<uint>(); var seen = new HashSet<uint>();
            Collect(WwiseBanks.Get(id), res, seen, 0, max);
            return res;
        }
        static void Collect(WwiseBanks.Node n, List<uint> res, HashSet<uint> seen, int d, int max)
        {
            if (n == null || d > 16 || !seen.Add(n.id) || res.Count >= max) return;
            foreach (var m in n.media) if (!res.Contains(m)) res.Add(m);
            if (n.type == WwiseBanks.HType.Event) { foreach (var a in WwiseBanks.ActionsOf(n)) if ((a.actionType >> 8) == 0x04) Collect(WwiseBanks.Get(a.target), res, seen, d + 1, max); return; }
            foreach (var c in n.children) Collect(WwiseBanks.Get(c), res, seen, d + 1, max);
            foreach (var l in n.links) if (l.prov == WwiseBanks.Prov.Inferred) Collect(WwiseBanks.Get(l.to), res, seen, d + 1, max);
        }

        public static string Report(uint id)
        {
            var sb = new StringBuilder();
            var ms = MediaUnder(id);
            sb.Append("SOUND FILES (").Append(ms.Count).Append(")   ").Append(status).Append('\n');
            foreach (var m in ms) foreach (var l in Details(m)) sb.Append("  ").Append(l).Append('\n');
            return sb.ToString();
        }
    }
}
