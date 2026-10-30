using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // Recorder.cs (owner TIME): real-time video of the game window, for reviews and test passes without the per-frame
    // screenshot cost (a 2560x1440 screenshot is a PNG written and read back, 2.4 to 3 s each). ffmpeg's ddagrab reads the
    // screen through DXGI Desktop Duplication (the GPU copies the desktop; the game's main thread does nothing) and
    // hevc_nvenc encodes it on the NVENC unit. The capture rectangle is the game window's own client area, found from
    // inside the process: EnumWindows (this process, visible, largest client area), ClientToScreen and the window's
    // monitor (MonitorFromWindow / GetMonitorInfo) under a per-monitor DPI-aware thread context, so the numbers are
    // physical pixels. ddagrab offsets are relative to its output; the output index is the first DXGI output of the
    // default adapter whose size matches that monitor (one short ffmpeg probe per output at start). Desktop duplication
    // records what is on screen, so a window on top of the game is recorded too: raise lifts the game window to the top
    // of the z-order without taking focus. Opt-in: nothing runs until time {op:"record"} or the console "rec start".
    // Stops on time {op:"record_stop"}, "rec stop", restore and when the game quits. ffmpeg: the ffmpeg argument, then
    // the INSIDEDEV_FFMPEG environment variable, then _mod/ffmpeg/ffmpeg.exe, C:/ffmpeg/bin/ffmpeg.exe, then PATH.
    public static class Recorder
    {
        public const string RestoreKey = "record";
        static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;

        sealed class Options
        {
            public int fps = 30, cq = 16;
            public bool raise;
            public string file, ffmpeg, codec = "hevc_nvenc", preset = "p6";
        }

        static readonly object gate = new object();
        static Process proc;
        static string file, ffmpegUsed, lastProblem, framesDir;
        static readonly StringBuilder errTail = new StringBuilder();
        static readonly Stopwatch clock = new Stopwatch();
        static int outputIdx = -1, rectX, rectY, rectW, rectH;
        static volatile bool starting, cutting;

        public static bool On { get { lock (gate) return proc != null && !SafeExited(proc); } }

        // any thread: the record block of time {op:"state"}
        public static Json.Obj StateObj()
        {
            var o = new Json.Obj();
            lock (gate)
            {
                bool on = proc != null && !SafeExited(proc);
                o["on"] = on;
                o["starting"] = starting;
                if (file != null) o["file"] = file;
                if (on || clock.ElapsedMilliseconds > 0) o["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
                if (rectW > 0) o["rect"] = rectW + "x" + rectH + " at " + rectX + "," + rectY + " on output " + outputIdx;
                if (framesDir != null) o["frames_dir"] = framesDir;
                if (cutting) o["cutting_frames"] = true;
                if (proc != null && SafeExited(proc)) o["problem"] = "ffmpeg exited: " + Tail();
                else if (lastProblem != null) o["problem"] = lastProblem;
            }
            return o;
        }

        // worker thread (the MCP call) or a background thread (the console): start ffmpeg; returns the state or throws
        public static Json.Obj Start(Json.Obj a)
        {
            var opt = new Options
            {
                fps = Math.Max(5, Math.Min(60, PlayShared.Int(a, "record_fps", 30))),
                cq = Math.Max(0, Math.Min(51, PlayShared.Int(a, "cq", 16))),
                raise = PlayShared.Bool(a, "raise", false),
                file = PlayShared.Str(a, "file", null),
                ffmpeg = PlayShared.Str(a, "ffmpeg", null)
            };
            StartWith(opt);
            return StateObj();
        }

        static void StartWith(Options opt)
        {
            lock (gate)
            {
                if (proc != null && !SafeExited(proc)) throw new McpException(McpErr.Busy, "a recording is running (" + file + "); stop it first (time {op:\"record_stop\"} or rec stop)");
                if (starting) throw new McpException(McpErr.Busy, "a recording is starting");
                starting = true;
                lastProblem = null;
            }
            try
            {
                string exe = FindFfmpeg(opt.ffmpeg);
                Win.Rect win, mon;
                IntPtr hwnd = Win.GameWindow(out win, out mon);
                if (hwnd == IntPtr.Zero) throw new McpException(McpErr.NotReady, "the game window was not found (minimized or not created yet)");
                int w = win.right - win.left, h = win.bottom - win.top;
                int x = win.left - mon.left, y = win.top - mon.top;
                if (x < 0 || y < 0 || x + w > mon.right - mon.left || y + h > mon.bottom - mon.top)
                    throw new McpException(McpErr.Refused, "the game window (" + w + "x" + h + " at " + win.left + "," + win.top + ") is not inside one monitor (" + (mon.right - mon.left) + "x" + (mon.bottom - mon.top) + " at " + mon.left + "," + mon.top + "); move it fully onto one screen");
                w &= ~1; h &= ~1;
                int idx = FindOutput(exe, mon.right - mon.left, mon.bottom - mon.top);
                if (idx < 0) throw new McpException(McpErr.Refused, "no DXGI output of the default graphics adapter is " + (mon.right - mon.left) + "x" + (mon.bottom - mon.top) + ": the game's monitor is driven by another adapter, which ddagrab cannot read from here");
                if (opt.raise) Win.Raise(hwnd);
                string dir = Path.Combine(ModDir(), "recordings");
                Directory.CreateDirectory(dir);
                string name = string.IsNullOrEmpty(opt.file) ? DateTime.Now.ToString("yyyyMMdd_HHmmss", IC) + ".mkv" : Path.GetFileName(opt.file);
                if (Path.GetExtension(name) == "") name += ".mkv";
                string path = Path.Combine(dir, name);
                string args = "-hide_banner -loglevel error -y -f lavfi -i \"ddagrab=output_idx=" + idx + ":framerate=" + opt.fps + ":draw_mouse=0:video_size=" + w + "x" + h + ":offset_x=" + x + ":offset_y=" + y + "\"" +
                    " -c:v " + opt.codec + " -preset " + opt.preset + " -tune hq -rc vbr -cq " + opt.cq + " -b:v 0 -g " + (opt.fps * 2) + " -an \"" + path + "\"";
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = false, CreateNoWindow = true };
                Process p;
                try { p = Process.Start(psi); }
                catch (Exception e) { throw new McpException(McpErr.NotReady, "cannot start ffmpeg (" + exe + "): " + e.Message + "; pass ffmpeg or set INSIDEDEV_FFMPEG"); }
                lock (errTail) errTail.Length = 0;
                var et = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while ((line = p.StandardError.ReadLine()) != null) lock (errTail) { errTail.Append(line).Append(" | "); if (errTail.Length > 4000) errTail.Remove(0, errTail.Length - 4000); }
                    }
                    catch { }
                }) { IsBackground = true, Name = "rec stderr" };
                et.Start();
                Thread.Sleep(600);
                if (SafeExited(p)) throw new McpException(McpErr.GameError, "ffmpeg stopped at once: " + Tail());
                lock (gate)
                {
                    proc = p; file = path; ffmpegUsed = exe; framesDir = null;
                    outputIdx = idx; rectX = x; rectY = y; rectW = w; rectH = h;
                    clock.Reset(); clock.Start();
                }
                Restorer.Register(RestoreKey, "real-time recording (ffmpeg ddagrab)", () => Stop(0).Str("file", "stopped"));
                DevLog.Write("[play] rec start " + w + "x" + h + " at " + x + "," + y + " on output " + idx + ", " + opt.fps + " fps, " + opt.codec + " cq " + opt.cq + " -> " + path);
            }
            finally { starting = false; }
        }

        // any thread: stop the running recording (q on stdin, then a kill after 15 s); reviewFps > 0 also cuts JPG frames
        public static Json.Obj Stop(double reviewFps)
        {
            Process p;
            string path, exe;
            lock (gate) { p = proc; path = file; exe = ffmpegUsed; proc = null; clock.Stop(); }
            var o = new Json.Obj();
            if (p == null) { o["stopped"] = false; o["note"] = "no recording was running"; return o; }
            bool clean = false;
            try
            {
                if (!SafeExited(p))
                {
                    p.StandardInput.Write("q");
                    p.StandardInput.Flush();
                    p.StandardInput.Close();
                    clean = p.WaitForExit(15000);
                    if (!clean) { try { p.Kill(); } catch { } }
                }
                else clean = true;
            }
            catch { try { p.Kill(); } catch { } }
            o["stopped"] = true;
            o["file"] = path;
            o["clean"] = clean;
            try { if (File.Exists(path)) o["mb"] = Math.Round(new FileInfo(path).Length / 1e6, 1); } catch { }
            o["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
            DevLog.Write("[play] rec stop " + path + (clean ? "" : " (killed)"));
            if (reviewFps > 0 && File.Exists(path)) o["frames_dir"] = CutFrames(exe, path, reviewFps);
            return o;
        }

        // JPG review frames at reviewFps into <video>_frames; returns the folder
        static string CutFrames(string exe, string video, double reviewFps)
        {
            string dir = Path.Combine(Path.GetDirectoryName(video), Path.GetFileNameWithoutExtension(video) + "_frames");
            Directory.CreateDirectory(dir);
            string args = "-hide_banner -loglevel error -y -i \"" + video + "\" -vf fps=" + reviewFps.ToString("0.###", IC) + " -q:v 2 \"" + Path.Combine(dir, "f%04d.jpg") + "\"";
            cutting = true;
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
                if (!p.WaitForExit(600000)) { try { p.Kill(); } catch { } }
            }
            catch (Exception e) { lock (gate) lastProblem = "frames: " + e.Message; }
            finally { cutting = false; }
            lock (gate) framesDir = dir;
            return dir;
        }

        // console: rec start [fps] [cq] [raise] | rec stop [review_fps] | rec status
        public static string Command(List<string> a)
        {
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            if (sub == "status") return JsonOut.Write(StateObj());
            if (sub == "start")
            {
                var args = new Json.Obj();
                for (int i = 2; i < a.Count; i++)
                {
                    int n;
                    if (a[i].ToLowerInvariant() == "raise") args["raise"] = true;
                    else if (int.TryParse(a[i], out n)) { if (!args.ContainsKey("record_fps")) args["record_fps"] = (double)n; else args["cq"] = (double)n; }
                    else args["file"] = a[i];
                }
                var t = new Thread(() =>
                {
                    try { StartWith(new Options { fps = Math.Max(5, Math.Min(60, PlayShared.Int(args, "record_fps", 30))), cq = PlayShared.Int(args, "cq", 16), raise = PlayShared.Bool(args, "raise", false), file = PlayShared.Str(args, "file", null) }); }
                    catch (Exception e) { lock (gate) lastProblem = e.Message; DevLog.Write("[play] rec start failed: " + e.Message); }
                }) { IsBackground = true, Name = "rec start" };
                t.Start();
                return "rec: starting (rec status shows the file, or the problem)";
            }
            if (sub == "stop")
            {
                double fps = 0;
                if (a.Count > 2) double.TryParse(a[2], System.Globalization.NumberStyles.Float, IC, out fps);
                var t = new Thread(() => { try { Stop(fps); } catch (Exception e) { lock (gate) lastProblem = e.Message; } }) { IsBackground = true, Name = "rec stop" };
                t.Start();
                return "rec: stopping" + (fps > 0 ? ", then cutting review frames at " + fps + " fps" : "");
            }
            return "rec start [fps] [cq] [raise] [file] | rec stop [review_fps] | rec status";
        }

        static string Tail() { lock (errTail) return errTail.Length == 0 ? "(no message)" : errTail.ToString(); }

        static bool SafeExited(Process p) { try { return p.HasExited; } catch { return true; } }

        static volatile string modDir;

        // main thread (Stepper.Tick): Application.dataPath is main-thread only, the recorder runs on workers
        public static void Prime()
        {
            if (modDir != null) return;
            try { modDir = Extensions.Dir; } catch { }
        }

        static string ModDir()
        {
            string d = modDir;
            if (string.IsNullOrEmpty(d)) throw new McpException(McpErr.NotReady, "the _mod folder is not known yet (the play extension has not ticked)");
            return d;
        }

        internal static string FindFfmpeg(string given)
        {
            var tried = new List<string>();
            foreach (var c in new[] { given, Environment.GetEnvironmentVariable("INSIDEDEV_FFMPEG"), Path.Combine(ModDir(), "ffmpeg/ffmpeg.exe"), "C:/ffmpeg/bin/ffmpeg.exe" })
            {
                if (string.IsNullOrEmpty(c)) continue;
                if (File.Exists(c)) return c;
                tried.Add(c);
            }
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { string c = Path.Combine(d.Trim(), "ffmpeg.exe"); if (File.Exists(c)) return c; } catch { }
            }
            throw new McpException(McpErr.NotReady, "ffmpeg.exe not found (tried " + string.Join(", ", tried.ToArray()) + " and PATH); pass ffmpeg, set INSIDEDEV_FFMPEG or put it in _mod/ffmpeg");
        }

        // the first output of the default adapter with the monitor's size; a cache per size
        static readonly Dictionary<string, int> outputBySize = new Dictionary<string, int>();

        static int FindOutput(string exe, int w, int h)
        {
            string key = w + "x" + h;
            lock (outputBySize) { int c; if (outputBySize.TryGetValue(key, out c)) return c; }
            for (int i = 0; i < 6; i++)
            {
                string line = Probe(exe, i);
                if (line == null) break;
                if (line.Contains("dimensions " + key)) { lock (outputBySize) outputBySize[key] = i; return i; }
            }
            return -1;
        }

        static string Probe(string exe, int idx)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, "-hide_banner -v verbose -f lavfi -i ddagrab=output_idx=" + idx + ":framerate=1:draw_mouse=0 -frames:v 1 -f null -")
                    { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true });
                string all = p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                foreach (var l in all.Split('\n')) if (l.Contains("Opened dxgi output")) return l;
                return null;
            }
            catch { return null; }
        }

        // Win32 from inside the process
        internal static class Win
        {
            [StructLayout(LayoutKind.Sequential)] public struct Rect { public int left, top, right, bottom; }
            [StructLayout(LayoutKind.Sequential)] struct Point { public int x, y; }
            [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int cbSize; public Rect rcMonitor, rcWork; public uint dwFlags; }
            delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

            [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
            [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
            [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
            [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
            [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Rect r);
            [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref Point p);
            [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
            [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MonitorInfo mi);
            [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
            [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
            [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();

            // the visible window of this process with the largest client area; win = client rect in screen pixels
            public static IntPtr GameWindow(out Rect win, out Rect mon)
            {
                win = new Rect(); mon = new Rect();
                IntPtr old = IntPtr.Zero;
                bool ctx = false;
                try { old = SetThreadDpiAwarenessContext(new IntPtr(-4)); ctx = old != IntPtr.Zero; } catch { }
                try
                {
                    uint me = GetCurrentProcessId();
                    IntPtr best = IntPtr.Zero;
                    long area = 0;
                    EnumWindows((h, lp) =>
                    {
                        uint pid;
                        GetWindowThreadProcessId(h, out pid);
                        if (pid != me || !IsWindowVisible(h) || IsIconic(h)) return true;
                        Rect r;
                        if (!GetClientRect(h, out r)) return true;
                        long a = (long)(r.right - r.left) * (r.bottom - r.top);
                        if (a > area) { area = a; best = h; }
                        return true;
                    }, IntPtr.Zero);
                    if (best == IntPtr.Zero) return IntPtr.Zero;
                    Rect c;
                    GetClientRect(best, out c);
                    var p = new Point();
                    ClientToScreen(best, ref p);
                    win = new Rect { left = p.x, top = p.y, right = p.x + c.right, bottom = p.y + c.bottom };
                    var mi = new MonitorInfo { cbSize = Marshal.SizeOf(typeof(MonitorInfo)) };
                    GetMonitorInfo(MonitorFromWindow(best, 2), ref mi);
                    mon = mi.rcMonitor;
                    return best;
                }
                finally { if (ctx) { try { SetThreadDpiAwarenessContext(old); } catch { } } }
            }

            // to the top of the z-order without activating it (no focus change)
            public static void Raise(IntPtr hwnd)
            {
                try { SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); } catch { }
            }
        }
    }
}
