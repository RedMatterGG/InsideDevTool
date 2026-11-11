using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace InsideDev
{
    // What MultiCam asks of ffmpeg, in one place: which program a spec's output.ffmpeg means, which encoder its
    // output.pipeCodec means, and whether both work before a take touches the game.
    //   output.ffmpeg     "ffmpeg" (the default) = the tool's own search (Ffmpeg.Find: editor.cfg ffmpeg.path, INSIDEDEV_FFMPEG,
    //                     <game>\_mod\ffmpeg, PATH, C:\ffmpeg\bin). Anything else must pass Ffmpeg.SafeExe: a full local path
    //                     to a file named ffmpeg.exe. A spec file is data that may come from anywhere, so it never chooses
    //                     another program to start.
    //   output.pipeCodec  one name of Codecs, or "auto" (hevc_nvenc when a test encode works, else libx265). The name goes on
    //                     the command line as it is, so nothing else is accepted.
    // Resolve runs on the main thread (Ffmpeg.Find reads editor.cfg) and remembers its answer; Cached gives that answer to
    // writer start threads.
    internal static class McaXFfmpeg
    {
        public const string Auto = "auto", Nvenc = "hevc_nvenc", X265 = "libx265";
        public static readonly string[] Codecs = { Nvenc, X265 };
        public const string CodecList = "hevc_nvenc, libx265, auto";
        const int ProbeW = 640, ProbeH = 360, ProbeMs = 8000, AskMs = 10000;

        static readonly object gate = new object();
        static readonly Dictionary<string, string> exeOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> whyOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> autoOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> probed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> silent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static bool IsDefault(string given)
        {
            string g = (given ?? "").Trim().Trim('"');
            return g.Length == 0 || string.Equals(g, "ffmpeg", StringComparison.OrdinalIgnoreCase) || string.Equals(g, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase);
        }

        // Main thread. The program for a spec's output.ffmpeg, or null and the reason.
        public static string Resolve(string given, out string why)
        {
            string exe;
            if (IsDefault(given))
            {
                string tried;
                exe = Ffmpeg.Find(out tried);
                why = exe != null ? null : "ffmpeg.exe not found (looked at: " + tried + "); set ffmpeg.path in editor.cfg, put it in <game>\\_mod\\ffmpeg\\ or on PATH";
            }
            else exe = Given(given, out why);
            lock (gate) { exeOf[Key(given)] = exe; whyOf[Key(given)] = why; }
            return exe;
        }

        // Any thread. The answer of the last Resolve for this value; without one, what can be found off the main thread
        // (an explicit path, INSIDEDEV_FFMPEG, PATH, C:\ffmpeg\bin).
        public static string Cached(string given, out string why)
        {
            string key = Key(given), exe;
            lock (gate)
            {
                if (exeOf.TryGetValue(key, out exe)) { whyOf.TryGetValue(key, out why); return exe; }
            }
            if (!IsDefault(given)) return Given(given, out why);
            why = null;
            string env = null;
            try { env = Environment.GetEnvironmentVariable("INSIDEDEV_FFMPEG"); } catch { }
            string w;
            if (!string.IsNullOrEmpty(env) && Ffmpeg.SafeExe(env, out w)) return env.Trim().Trim('"');
            exe = Ffmpeg.OnPath("ffmpeg.exe");
            if (exe != null) return exe;
            const string fixedPlace = "C:\\ffmpeg\\bin\\ffmpeg.exe";
            if (File.Exists(fixedPlace)) return fixedPlace;
            why = "ffmpeg.exe not found (INSIDEDEV_FFMPEG, PATH, " + fixedPlace + ")";
            return null;
        }

        static string Given(string given, out string why)
        {
            string p = (given ?? "").Trim().Trim('"');
            string reason;
            if (Ffmpeg.SafeExe(p, out reason)) { why = null; return p; }
            why = "'" + given + "' is not accepted: " + reason + " (a spec may give the full local path of an ffmpeg.exe, or \"ffmpeg\" for the tool's own search)";
            return null;
        }

        static string Key(string given) { return IsDefault(given) ? "" : given.Trim().Trim('"'); }

        // The list name for a spec's output.pipeCodec ("auto" included), or null when it is not on the list.
        public static string CodecName(string given)
        {
            string g = (given ?? "").Trim();
            if (string.Equals(g, Auto, StringComparison.OrdinalIgnoreCase)) return Auto;
            foreach (var c in Codecs) if (string.Equals(g, c, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        // Any thread. The encoder name for the command line; "auto" gives what the last Preflight chose for this ffmpeg.
        public static string Codec(OutputSpec o)
        {
            string name = CodecName(o.pipeCodec);
            if (name == null) throw new Exception("output.pipeCodec '" + o.pipeCodec + "' is not one of " + CodecList);
            if (name != Auto) return name;
            string why;
            string exe = Cached(o.ffmpeg, out why);
            if (exe == null) throw new Exception("output.pipeCodec auto: " + why);
            lock (gate)
            {
                string pick;
                if (autoOf.TryGetValue(exe, out pick)) return pick;
            }
            throw new Exception("output.pipeCodec auto: no encoder chosen yet for " + exe + " (mcam check <spec> chooses it)");
        }

        public static bool IsNvenc(string codec) { return codec == Nvenc; }

        // rate control of the pipe encoders: constant QP for NVENC, CRF for x265, both from output.pipeQp
        public static string QualityArgs(string codec, int qp)
        {
            return IsNvenc(codec) ? "-preset p5 -rc constqp -qp " + Fmt.I(qp) : "-preset fast -crf " + Fmt.I(qp);
        }

        public static string PixelFormat(string codec) { return IsNvenc(codec) ? "p010le" : "yuv420p10le"; }

        // Main thread, from a check or start command only: the first call for an ffmpeg starts it ("-encoders", and one test
        // encode of a single frame for hevc_nvenc) and may take some seconds; later calls answer from memory.
        // blockers = what makes a pipe take impossible on this machine; notes and warnings go to the report.
        public static void Preflight(OutputSpec o, List<string> blockers, SpecReport report)
        {
            string why;
            string exe = Resolve(o.ffmpeg, out why);
            if (exe == null) { blockers.Add("output.ffmpeg: " + why); return; }
            string name = CodecName(o.pipeCodec);
            if (name == null) { blockers.Add("output.pipeCodec: '" + o.pipeCodec + "' is not one of " + CodecList); return; }
            string err, perr;
            if (name == Auto)
            {
                string pick = null, nv = null;
                bool hasNv = HasEncoder(exe, Nvenc, out err);
                if (err != null) { blockers.Add("output.ffmpeg: " + err); return; }
                if (!hasNv) nv = "hevc_nvenc is not in this ffmpeg";
                else if (Probe(exe, Nvenc, o.pipeQp, out perr)) pick = Nvenc;
                else nv = "hevc_nvenc test encode failed: " + perr;
                if (pick == null && HasEncoder(exe, X265, out err)) pick = X265;
                if (pick == null) { blockers.Add("output.pipeCodec auto: " + exe + " has no usable H.265 encoder (" + nv + "; libx265 is not in this ffmpeg)"); return; }
                lock (gate) autoOf[exe] = pick;
                if (report != null) report.Note("output.pipeCodec", "auto -> " + pick + (nv != null ? " (" + nv + ")" : "") + ", " + exe);
                return;
            }
            if (!HasEncoder(exe, name, out err))
            {
                blockers.Add(err != null ? "output.ffmpeg: " + err : "output.pipeCodec: " + name + " is not in this ffmpeg (" + exe + "); use " + (name == Nvenc ? X265 : Nvenc) + " or auto");
                return;
            }
            if (name == Nvenc && !Probe(exe, name, o.pipeQp, out perr) && report != null)
                report.Warn("output.pipeCodec", "hevc_nvenc is in this ffmpeg but a test encode of one frame failed (" + perr + "): a take would fail at its first frame; use libx265 or auto");
        }

        // Ffmpeg.HasEncoder reads ffmpeg's answer to its end. An ffmpeg that never answers must not hold the game thread, so
        // the question is asked on a worker and this thread waits AskMs at most; such an ffmpeg is not asked again.
        static bool HasEncoder(string exe, string codec, out string error)
        {
            lock (gate)
            {
                if (silent.TryGetValue(exe, out error)) return false;
            }
            bool has = false;
            string err = null;
            var t = new Thread(() =>
            {
                try { string e; has = Ffmpeg.HasEncoder(exe, codec, out e); err = e; }
                catch (Exception x) { err = x.GetType().Name + ": " + x.Message; }
            });
            t.IsBackground = true;
            t.Name = "mcam encoder list";
            t.Start();
            if (!t.Join(AskMs))
            {
                error = exe + " did not answer '-hide_banner -encoders' in " + Fmt.I(AskMs / 1000) + " s";
                lock (gate) silent[exe] = error;
                return false;
            }
            error = err;
            return has;
        }

        // One raw frame through the same input and encoder options as a pipe take, to the null muxer. Remembered per ffmpeg
        // and encoder. The child runs on its own thread; this thread waits ProbeMs at most.
        static bool Probe(string exe, string codec, int qp, out string error)
        {
            string key = exe + "|" + codec, known;
            lock (gate)
            {
                if (probed.TryGetValue(key, out known)) { error = known.Length > 0 ? known : null; return known.Length == 0; }
            }
            string args = "-hide_banner -loglevel error -f rawvideo -pix_fmt rgba -s " + Fmt.I(ProbeW) + "x" + Fmt.I(ProbeH) + " -framerate 30 -i - -an -frames:v 1 -vf format=" + PixelFormat(codec) +
                          " -c:v " + codec + " " + QualityArgs(codec, qp) + " -f null -";
            string result = "";
            Process proc = null;
            var err = new StringBuilder();
            int exit = -1;
            bool done = false;
            var t = new Thread(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
                    var p = Process.Start(psi);
                    lock (err) proc = p;
                    ChildJob.Add(p);
                    var reader = new Thread(() =>
                    {
                        try { string line; while ((line = p.StandardError.ReadLine()) != null) lock (err) { if (err.Length < 600) err.Append(line).Append(" | "); } }
                        catch { }
                    });
                    reader.IsBackground = true;
                    reader.Start();
                    try
                    {
                        var zero = new byte[ProbeW * ProbeH * 4];
                        var s = p.StandardInput.BaseStream;
                        s.Write(zero, 0, zero.Length);
                        s.Flush();
                        s.Close();
                    }
                    catch { }
                    p.WaitForExit();
                    reader.Join(1000);
                    exit = p.ExitCode;
                    done = true;
                }
                catch (Exception e) { lock (err) err.Append(e.GetType().Name).Append(": ").Append(e.Message); }
            });
            t.IsBackground = true;
            t.Name = "mcam encoder probe";
            t.Start();
            if (!t.Join(ProbeMs))
            {
                Process p;
                lock (err) p = proc;
                try { if (p != null && !p.HasExited) p.Kill(); } catch { }
                result = "no answer in " + Fmt.I(ProbeMs / 1000) + " s";
            }
            else if (!done || exit != 0)
            {
                string tail;
                lock (err) tail = err.ToString().Trim().TrimEnd('|').Trim();
                result = tail.Length > 0 ? tail : "ffmpeg exit code " + Fmt.I(exit);
            }
            lock (gate) probed[key] = result;
            error = result.Length > 0 ? result : null;
            return result.Length == 0;
        }
    }
}
