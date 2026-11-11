using System;
using System.Collections.Generic;
using System.IO;

namespace InsideDev
{
    // The out dir of "mcam start | rehearse | preview | record | replay | take | passes | golden":
    //   - it may be left away: <spec dir>/takes/<spec name>_<yyyyMMdd_HHmmss> (replay, passes and golden, which need a
    //     recording, take the newest such folder that holds one);
    //   - a folder that already holds a take is not written into unless overwrite=on is among the arguments. A take can be
    //     hours of rendering, and a record pass is a performance that cannot be repeated. A folder that holds only a
    //     preview or a rehearsal (run.json says so) is not a take: those are made to be repeated and to be followed by the
    //     real run.
    internal static class McaXTake
    {
        const string TakesFolder = "takes";
        static readonly string[] Reserved = { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9" };

        // Windows opens these names as devices in every folder, with or without an extension.
        public static bool ReservedName(string name)
        {
            string n = (name ?? "").Trim();
            int dot = n.IndexOf('.');
            if (dot >= 0) n = n.Substring(0, dot);
            return Array.IndexOf(Reserved, n.ToLowerInvariant()) >= 0;
        }

        // The words of a[from..] without overwrite=<on|off>; overwrite = what the last of them said.
        public static List<string> Words(List<string> a, int from, out bool overwrite, out string error)
        {
            overwrite = false; error = null;
            var r = new List<string>();
            for (int i = from; i < a.Count; i++)
            {
                string s = a[i] ?? "";
                if (s.StartsWith("overwrite=", StringComparison.OrdinalIgnoreCase))
                {
                    string v = s.Substring(10).ToLowerInvariant();
                    if (v == "on" || v == "1" || v == "true" || v == "yes") overwrite = true;
                    else if (v == "off" || v == "0" || v == "false" || v == "no") overwrite = false;
                    else error = "usage: overwrite=on or overwrite=off (got '" + s + "')";
                    continue;
                }
                if (string.Equals(s, "overwrite", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "force", StringComparison.OrdinalIgnoreCase))
                {
                    error = "usage: write overwrite=on to replace an existing take (got '" + s + "')";
                    continue;
                }
                r.Add(s);
            }
            return r;
        }

        public static bool ReplayOption(string s)
        {
            s = s ?? "";
            return s.StartsWith("cams=", StringComparison.OrdinalIgnoreCase) || s.StartsWith("range=", StringComparison.OrdinalIgnoreCase) || s.StartsWith("every=", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "resume", StringComparison.OrdinalIgnoreCase);
        }

        static string TakesDir(MultiCamPlan plan) { return Path.Combine(string.IsNullOrEmpty(plan.dir) ? "." : plan.dir, TakesFolder); }

        // The spec's name (or its file name) as a folder name.
        public static string TakeName(MultiCamPlan plan)
        {
            string n = plan.spec != null ? (plan.spec.name ?? "").Trim() : "";
            if (n.Length == 0) { try { n = Path.GetFileNameWithoutExtension(plan.path) ?? ""; } catch { n = ""; } }
            var bad = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(n.Length);
            foreach (char c in n) sb.Append(c < 32 || c == ' ' || Array.IndexOf(bad, c) >= 0 ? '_' : c);
            n = sb.ToString().Trim('.', '_');
            if (n.Length == 0) n = "take";
            if (n.Length > 60) n = n.Substring(0, 60);
            return ReservedName(n) ? "_" + n : n;
        }

        // <spec dir>/takes/<name>_<yyyyMMdd_HHmmss>, with _2, _3 ... when that second is taken.
        public static string DefaultOut(MultiCamPlan plan)
        {
            string stem = Path.Combine(TakesDir(plan), TakeName(plan) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", Fmt.IC));
            string dir = stem;
            for (int k = 2; k < 100 && (Directory.Exists(dir) || File.Exists(dir)); k++) dir = stem + "_" + Fmt.I(k);
            return Path.GetFullPath(dir);
        }

        // The newest default folder of this spec that holds a recording; null when there is none.
        public static string LatestRecorded(MultiCamPlan plan)
        {
            string takes = TakesDir(plan), best = null;
            if (!Directory.Exists(takes)) return null;
            foreach (var d in Directory.GetDirectories(takes, TakeName(plan) + "_*"))
            {
                if (!File.Exists(Path.Combine(Path.Combine(d, ReplayConst.RecordFolder), ReplayConst.RecordFile))) continue;
                if (best == null || string.CompareOrdinal(Path.GetFileName(d), Path.GetFileName(best)) > 0) best = d;
            }
            return best != null ? Path.GetFullPath(best) : null;
        }

        public static string DefaultPattern(MultiCamPlan plan) { return Path.Combine(TakesDir(plan), TakeName(plan) + "_<yyyyMMdd_HHmmss>"); }

        // The "mode" of the run whose run.json lies in dir: null without a run.json, "?" when it cannot be read.
        public static string RunMode(string dir)
        {
            string f = Path.Combine(dir, "run.json");
            if (!File.Exists(f)) return null;
            try
            {
                var o = Json.Parse(File.ReadAllText(f)) as Json.Obj;
                string m = o != null ? o.Str("mode", "") : "";
                return string.IsNullOrEmpty(m) ? "?" : m;
            }
            catch { return "?"; }
        }

        // True when dir holds the run.json of a run worth keeping (anything but a preview or a rehearsal).
        public static bool HoldsTake(string dir, out string mode)
        {
            mode = RunMode(dir);
            return mode != null && mode != "preview" && mode != "rehearse";
        }

        // The refusal for a run that would write over a take, or null. verb = start | rehearse | preview | record | take.
        public static string Holds(string verb, string outDir)
        {
            string rec = Path.Combine(outDir, ReplayConst.RecordFolder), mode;
            bool recording = File.Exists(Path.Combine(rec, ReplayConst.RecordFile));
            if (verb == ReplayModes.Record || verb == ReplayModes.Take)
            {
                if (recording)
                    return "refused: " + rec + " already holds a recording (" + ReplayConst.RecordFile + "). Use a new folder, 'mcam replay <spec> <out dir>' to film that recording again, or add overwrite=on to replace it";
                string runDir = verb == ReplayModes.Record ? rec : outDir;
                if (HoldsTake(runDir, out mode))
                    return "refused: " + runDir + " already holds a run (run.json, mode " + mode + "). Use a new folder, or add overwrite=on to replace it";
                return null;
            }
            if (HoldsTake(outDir, out mode))
                return "refused: " + outDir + " already holds a take (run.json, mode " + mode + "). Use a new folder (without an out dir the take goes to <spec dir>\\takes\\<name>_<time>), or add overwrite=on to replace it";
            return null;
        }

        // Frame files of an older take in a camera folder (<5 digits or more>.jpg | .rgba). A shorter new take would leave
        // the higher numbers behind, and the folder would read as one take.
        public static int ClearFrames(string camDir)
        {
            int n = 0;
            if (!Directory.Exists(camDir)) return 0;
            foreach (var f in Directory.GetFiles(camDir))
            {
                string ext = (Path.GetExtension(f) ?? "").ToLowerInvariant();
                if (ext != ".jpg" && ext != ".rgba") continue;
                string name = Path.GetFileNameWithoutExtension(f);
                if (name.Length < 5) continue;
                bool digits = true;
                foreach (char c in name) if (c < '0' || c > '9') { digits = false; break; }
                if (!digits) continue;
                try { File.Delete(f); n++; } catch { }
            }
            return n;
        }
    }
}
