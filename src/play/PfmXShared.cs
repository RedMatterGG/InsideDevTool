using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace InsideDev.Play
{
    // PfmXShared.cs (owner TIME): what the three recorders (grec, rec, pano) share. FilmFiles: one rule for output files
    // (no name -> <_mod>/recordings/<kind>_<yyyyMMdd_HHmmss>.<ext>, a bare name -> the same folder, a path with a folder ->
    // as given) and the check that a finished mkv carries a duration. FilmEnc: the video encoder for ffmpeg, chosen by a
    // real test encode (the encoder list of ffmpeg says what was compiled in, not what this GPU can do) and cached per
    // ffmpeg path; order hevc_nvenc, hevc_amf, hevc_qsv, libx265, libx264, or the one named by enc= / editor.cfg
    // film.encoder. FilmShared: small helpers (game quit, a bounded read of a child's stderr).
    internal static class FilmFiles
    {
        public const string Folder = "recordings";

        // any thread. dir = <_mod>/recordings (the caller reads it on the main thread). arg = what the user typed (null or
        // "" = no name). bareOnly refuses names with a folder (callers that must stay inside dir). Returns the full path
        // with forward slashes, or null with error. The folder of the file is not created here.
        public static string Resolve(string dir, string kind, string ext, string arg, bool bareOnly, out string error)
        {
            error = null;
            try
            {
                string a = (arg ?? "").Trim().Trim('"');
                string path;
                if (a.Length == 0)
                {
                    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", Fmt.IC);
                    path = Path.Combine(dir, kind + "_" + stamp + ext);
                    for (int i = 2; File.Exists(path) && i < 100; i++) path = Path.Combine(dir, kind + "_" + stamp + "_" + Fmt.I(i) + ext);
                }
                else if (a.IndexOfAny(new[] { '/', '\\', ':' }) < 0)
                {
                    if (a == "." || a == "..") { error = "bad file name " + a; return null; }
                    path = Path.Combine(dir, a);
                }
                else
                {
                    if (bareOnly) { error = "only a file name is accepted here (it goes to " + dir.Replace('\\', '/') + "), got " + a; return null; }
                    path = Path.GetFullPath(a);
                }
                if (Path.GetFileName(path).Length == 0) { error = "no file name in " + a; return null; }
                if (Path.GetExtension(path).Length == 0) path += ext;
                return Path.GetFullPath(path).Replace('\\', '/');
            }
            catch (Exception e) { error = "bad file " + arg + ": " + e.Message; return null; }
        }

        // 1 = the Matroska header carries a Duration (ffmpeg wrote its trailer), 0 = it does not (ffmpeg was cut off: the
        // file plays but has no length until it is remuxed), -1 = not a Matroska file or unreadable. Any thread.
        public static int MkvDuration(string path)
        {
            try
            {
                var b = new byte[16384];
                int n;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) n = fs.Read(b, 0, b.Length);
                if (n < 64 || b[0] != 0x1A || b[1] != 0x45 || b[2] != 0xDF || b[3] != 0xA3) return -1;
                // the Info element itself is the last place its id stands in the header (the first one is its SeekHead entry)
                int info = -1;
                for (int at = Find(b, n, 0, 0x15, 0x49, 0xA9, 0x66); at >= 0; at = Find(b, n, at + 4, 0x15, 0x49, 0xA9, 0x66)) info = at;
                if (info < 0) return -1;
                int end = Find(b, n, info + 4, 0x16, 0x54, 0xAE, 0x6B);
                if (end < 0) end = n;
                for (int i = info + 4; i + 2 < end; i++)
                    if (b[i] == 0x44 && b[i + 1] == 0x89 && (b[i + 2] == 0x84 || b[i + 2] == 0x88)) return 1;
                return 0;
            }
            catch { return -1; }
        }

        static int Find(byte[] b, int n, int from, byte b0, byte b1, byte b2, byte b3)
        {
            for (int i = from; i + 3 < n; i++) if (b[i] == b0 && b[i + 1] == b1 && b[i + 2] == b2 && b[i + 3] == b3) return i;
            return -1;
        }

        public static double Mb(string path)
        {
            try { return File.Exists(path) ? Math.Round(new FileInfo(path).Length / 1e6, 1) : 0; } catch { return 0; }
        }
    }

    internal static class FilmEnc
    {
        public const string ConfigKey = "film.encoder", Auto = "auto";
        public static readonly string[] Order = { "hevc_nvenc", "hevc_amf", "hevc_qsv", "libx265", "libx264" };
        static readonly string[] Known = { "hevc_nvenc", "h264_nvenc", "hevc_amf", "h264_amf", "hevc_qsv", "h264_qsv", "libx265", "libx264" };

        // "<ffmpeg>|<encoder>" -> null when the test encode worked, else why not
        static readonly Dictionary<string, string> tested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> picked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string List { get { return Auto + ", " + string.Join(", ", Known); } }

        public static bool IsKnown(string enc)
        {
            return enc == Auto || Array.IndexOf(Known, enc) >= 0;
        }

        // the options after "-c:v <encoder>": constant quality q (0..51, lower is better) and a 4:2:0 picture every player
        // reads for the software encoders
        public static string Args(string enc, int q)
        {
            string n = Fmt.I(q);
            if (enc.EndsWith("_nvenc", StringComparison.Ordinal)) return "-preset p6 -tune hq -rc vbr -cq " + n + " -b:v 0";
            if (enc.EndsWith("_amf", StringComparison.Ordinal)) return "-quality quality -rc cqp -qp_i " + n + " -qp_p " + n + " -qp_b " + n;
            if (enc.EndsWith("_qsv", StringComparison.Ordinal)) return "-preset slow -global_quality " + n;
            if (enc == "libx265") return "-preset fast -crf " + n + " -pix_fmt yuv420p -x265-params log-level=error";
            return "-preset veryfast -crf " + n + " -pix_fmt yuv420p";
        }

        // any thread, no process start: the encoder a finished Pick chose for this ffmpeg and wish, or null
        public static string Cached(string ffmpeg, string wanted)
        {
            lock (picked) { string e; return picked.TryGetValue(ffmpeg + "|" + (wanted ?? Auto), out e) ? e : null; }
        }

        // Worker thread (starts ffmpeg for a five frame test encode per candidate, up to 10 s each, once per session; an
        // encoder passes when ffmpeg ends with code 0 and without a message: some builds end with 0 and "nothing was written").
        // wanted = auto or a known encoder. null with why when nothing works.
        public static string Pick(string ffmpeg, string wanted, out string why)
        {
            why = null;
            wanted = string.IsNullOrEmpty(wanted) ? Auto : wanted.Trim().ToLowerInvariant();
            if (!IsKnown(wanted)) { why = "unknown encoder " + wanted + " (" + List + ")"; return null; }
            string hit = Cached(ffmpeg, wanted);
            if (hit != null) return hit;
            var fails = new List<string>();
            foreach (var enc in wanted == Auto ? Order : new[] { wanted })
            {
                string no = Test(ffmpeg, enc);
                if (no == null)
                {
                    lock (picked) picked[ffmpeg + "|" + wanted] = enc;
                    if (fails.Count > 0) DevLog.Write("[play] video encoder " + enc + " (" + string.Join("; ", fails.ToArray()) + ")");
                    return enc;
                }
                fails.Add(enc + ": " + no);
            }
            why = wanted == Auto ? "no video encoder works with " + ffmpeg + " (" + string.Join("; ", fails.ToArray()) + ")"
                                 : "encoder " + fails[0] + "; pass enc=auto or set editor.cfg " + ConfigKey;
            return null;
        }

        static string Test(string ffmpeg, string enc)
        {
            string key = ffmpeg + "|" + enc, no;
            lock (tested) if (tested.TryGetValue(key, out no)) return no;
            no = null;
            try
            {
                var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error -f lavfi -i color=c=black:s=256x256:r=30 -frames:v 5 -c:v " + enc + " " + Args(enc, 30) + " -f null -")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    ChildJob.Add(p);
                    string err = FilmShared.ReadAll(p, 10000);
                    if (err == null) no = "the test encode did not end in 10 s";
                    else if (p.ExitCode != 0 || err.Trim().Length > 0) no = FilmShared.LastLine(err);
                }
            }
            catch (Exception e) { no = "cannot run ffmpeg: " + e.Message; }
            lock (tested) tested[key] = no;
            return no;
        }
    }

    internal static class FilmShared
    {
        // main thread: the game is quitting (the extensions are being disposed), so coroutines will not run again and
        // children in the job object end with the process
        public static bool Quitting { get { return Extensions.Quitting; } }

        // Reads the child's stderr to its end on a helper thread and waits for the exit; null when it did not end within
        // timeoutMs (the child is killed then). Any thread except the main one.
        public static string ReadAll(Process p, int timeoutMs)
        {
            var sb = new StringBuilder();
            var t = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = p.StandardError.ReadLine()) != null) lock (sb) { if (sb.Length < 20000) sb.Append(line).Append('\n'); }
                }
                catch { }
            }) { IsBackground = true, Name = "film stderr" };
            t.Start();
            bool ended = false;
            try { ended = p.WaitForExit(timeoutMs); } catch { }
            if (!ended) { try { p.Kill(); } catch { } return null; }
            t.Join(2000);
            lock (sb) return sb.ToString();
        }

        public static string LastLine(string text)
        {
            string last = "";
            foreach (var l in (text ?? "").Split('\n')) { string s = l.Trim(); if (s.Length > 0) last = s; }
            if (last.Length == 0) return "no message";
            return last.Length > 300 ? last.Substring(0, 300) + "..." : last;
        }

        // true when the last word of the command is word (case does not matter); the word is removed
        public static bool TakeLast(List<string> a, string word, int from)
        {
            int i = a.Count - 1;
            if (i < from || !string.Equals(a[i], word, StringComparison.OrdinalIgnoreCase)) return false;
            a.RemoveAt(i);
            return true;
        }
    }
}
