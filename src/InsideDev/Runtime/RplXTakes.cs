using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Where manual recordings live: <game>\_mod\srec\<take>. A relative folder given to `srec record start`, `srec load`
    // or `srec load ... dry` is a take there, an absolute path is used as it is; `srec record start` without a folder
    // makes take_<yyyyMMdd_HHmmss>. `srec list` shows the takes (newest first), `srec load last` loads the newest.
    // A take is a direct subfolder that holds scene.srec, or record\scene.srec as mcam writes it.
    internal static class RplXTakes
    {
        public const string FolderName = "srec";
        const long MaxJsonBytes = 4L * 1024 * 1024;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public sealed class Take
        {
            public string name = "", folder = "", record = "";
            public DateTime written;
            public long bytes;
            public bool audio;
            public int samples = -1;
            public double seconds = -1.0;
        }

        // main thread (Application.dataPath)
        public static string Root()
        {
            return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), FolderName);
        }

        // Full path of a take argument: absolute as given, relative under root. Pure.
        public static string Under(string root, string arg)
        {
            return Path.GetFullPath(Path.IsPathRooted(arg) ? arg : Path.Combine(root, arg));
        }

        public static string NewName(DateTime now) { return "take_" + now.ToString("yyyyMMdd_HHmmss", IC); }

        // A folder name that is not taken yet: take_<stamp>, then take_<stamp>_2 ... Pure apart from `taken`.
        public static string NewTake(string root, DateTime now, Func<string, bool> taken)
        {
            string name = NewName(now), dir = Path.Combine(root, name);
            for (int k = 2; taken != null && taken(dir) && k < 1000; k++) dir = Path.Combine(root, name + "_" + k.ToString(IC));
            return Path.GetFullPath(dir);
        }

        // The takes under root, newest first, at most max of them with their details; total = how many there are.
        public static List<Take> List(string root, int max, out int total)
        {
            var all = new List<Take>();
            total = 0;
            string[] dirs;
            try { dirs = Directory.Exists(root) ? Directory.GetDirectories(root) : new string[0]; }
            catch (Exception e) { DevLog.Error("srec list " + root, e); dirs = new string[0]; }
            foreach (var d in dirs)
            {
                try
                {
                    string rec = SceneCommands.RecordingDir(d);
                    if (rec == null) continue;
                    var fi = new FileInfo(Path.Combine(rec, ReplayConst.RecordFile));
                    var t = new Take();
                    t.name = Path.GetFileName(d);
                    t.folder = d;
                    t.record = rec;
                    t.written = fi.LastWriteTime;
                    t.bytes = fi.Length;
                    all.Add(t);
                }
                catch (Exception e) { DevLog.Error("srec list " + d, e); }
            }
            all.Sort((a, b) => b.written.CompareTo(a.written));
            total = all.Count;
            if (max > 0 && all.Count > max) all.RemoveRange(max, all.Count - max);
            foreach (var t in all) Details(t);
            return all;
        }

        // The folder of the newest take under root (what `srec load` takes), null when there is none.
        public static string Last(string root)
        {
            string best = null;
            DateTime at = DateTime.MinValue;
            string[] dirs;
            try { dirs = Directory.Exists(root) ? Directory.GetDirectories(root) : new string[0]; }
            catch { return null; }
            foreach (var d in dirs)
            {
                try
                {
                    string rec = SceneCommands.RecordingDir(d);
                    if (rec == null) continue;
                    var w = File.GetLastWriteTime(Path.Combine(rec, ReplayConst.RecordFile));
                    if (best == null || w > at) { best = d; at = w; }
                }
                catch { }
            }
            return best;
        }

        static void Details(Take t)
        {
            try
            {
                t.audio = File.Exists(Path.Combine(t.record, ReplayConst.AudioFile)) || File.Exists(Path.Combine(t.folder, ReplayConst.AudioFile));
                string js = Path.Combine(t.record, ReplayConst.RecordJson);
                var fi = new FileInfo(js);
                if (!fi.Exists || fi.Length > MaxJsonBytes) return;
                var o = Json.Parse(File.ReadAllText(js)) as Json.Obj;
                if (o == null) return;
                t.samples = (int)o.Num("samples", -1);
                t.seconds = o.Num("duration", -1.0);
            }
            catch (Exception) { }
        }

        public static string Text(string root, List<Take> takes, int total)
        {
            var sb = new StringBuilder();
            sb.Append("takes ").Append(takes.Count).Append(" of ").Append(total).Append(" under ").Append(root).Append(" (newest first; srec load <name> | srec load last)");
            foreach (var t in takes)
            {
                sb.Append("\n  ").Append(t.name).Append("  ").Append(t.written.ToString("yyyy-MM-dd HH:mm:ss", IC)).Append("  ")
                  .Append(t.samples >= 0 ? t.samples.ToString(IC) + " samples" : "? samples").Append("  ")
                  .Append(t.seconds >= 0 ? t.seconds.ToString("0.00", IC) + " s" : "? s").Append("  ")
                  .Append((t.bytes / 1048576.0).ToString("0.0", IC)).Append(" MB  audio ").Append(t.audio ? "yes" : "no");
            }
            return sb.ToString();
        }

        public static Json.Obj ToJson(string root, List<Take> takes, int total)
        {
            var o = new Json.Obj();
            o["root"] = root;
            o["total"] = total;
            var l = new List<object>();
            foreach (var t in takes)
            {
                var j = new Json.Obj();
                j["name"] = t.name;
                j["dir"] = t.folder;
                j["written"] = t.written.ToString("yyyy-MM-dd HH:mm:ss", IC);
                j["samples"] = t.samples;
                j["seconds"] = t.seconds;
                j["bytes"] = t.bytes;
                j["audio"] = t.audio;
                l.Add(j);
            }
            o["takes"] = l;
            return o;
        }
    }
}
