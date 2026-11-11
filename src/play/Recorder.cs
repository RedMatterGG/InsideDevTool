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
    // Recorder.cs (owner TIME): real-time video of the game, for reviews and test passes without the per-frame
    // screenshot cost (a 2560x1440 screenshot is a PNG written and read back, 2.4 to 3 s each). The default source is the
    // game's own frames: the start is handed to GameRec (grec, the back buffer read by the native plugin), so windows over
    // the game never reach the file; rec stop / record_stop / rec status follow that recording. The desktop capture
    // below is opt-in (time {op:"record", source:"desktop"}, rec start ... desktop) and says that windows over the game
    // are recorded too. Desktop source: ffmpeg's ddagrab reads the
    // screen through DXGI Desktop Duplication (the GPU copies the desktop; the game's main thread does nothing) and
    // the encoder (FilmEnc: hevc_nvenc first, others when it does not work here) writes the file. The capture rectangle
    // is the game window's own client area, found from inside the process: EnumWindows (this process, visible, largest
    // client area), ClientToScreen and the window's monitor (MonitorFromWindow / GetMonitorInfo) under a per-monitor
    // DPI-aware thread context, so the numbers are physical pixels. ddagrab offsets are relative to its output; the output
    // index is the DXGI output of the default adapter that IS the window's monitor (Dxgi.Outputs, matched by monitor
    // handle, else by desktop rectangle), so two monitors of one size are told apart; output=<n> overrides it. Desktop
    // duplication records what is on screen, so a window on top of the game is recorded too: raise lifts the game window
    // to the top of the z-order without taking focus, and the state notes when another window covers the centre of the
    // picture at the start. Opt-in: nothing runs until time {op:"record"} or the console "rec start".
    // Stops on time {op:"record_stop"}, "rec stop", restore and when the game quits; ffmpeg sits in the core's job object,
    // so it also ends when the game process dies, and -t caps every recording (editor.cfg film.rec_max_s).
    // ffmpeg is the one the core finds (Ffmpeg.Find: editor.cfg ffmpeg.path, INSIDEDEV_FFMPEG, <game>/_mod/ffmpeg,
    // PATH, C:/ffmpeg/bin); a path named by the caller of the tool is never started.
    public static class Recorder
    {
        public const string RestoreKey = "record", MaxKey = "film.rec_max_s";
        public const int MaxDefault = 14400;
        public const string Usage = "rec start [fps 5..60] [cq 0..51] [file] [every=<n>] [enc=auto|<encoder>] [desktop [raise] [output=<n>]] | rec stop [review_fps] [wait] | rec status [json] | rec wait";
        public const string DesktopNote = "desktop capture: windows over the game will be in the video (the default source, the game's own frames, keeps them out)";

        sealed class Options
        {
            public int fps = 30, cq = 16, output = -1;
            public bool raise;
            public string file, enc;
        }

        // what a start needs from the main thread
        sealed class Setup
        {
            public string ffmpeg, tried, enc, dir, error;
            public int maxSeconds;
        }

        static readonly object gate = new object(), askGate = new object();
        static Process proc;
        static string file, ffmpegUsed, lastProblem, framesDir, encUsed, note;
        static readonly StringBuilder errTail = new StringBuilder();
        static readonly Stopwatch clock = new Stopwatch();
        static int outputIdx = -1, rectX, rectY, rectW, rectH, maxUsed;
        static volatile bool starting, cutting, wantSetup;
        // stops at work (a stop asked from the console counts from the command on, before its thread runs)
        static int stops;
        static Setup setupReply;
        static readonly ManualResetEvent setupDone = new ManualResetEvent(false);
        static int mainThread;

        // the last start went to grec (the game's own frames); gameOpen: that recording is not stopped through rec yet
        static volatile bool viaGame, gameOpen;
        static string gameExe;

        public static bool On
        {
            get
            {
                if (GameOn) return true;
                lock (gate) return proc != null && !SafeExited(proc);
            }
        }

        // any thread: the grec recording that rec started still runs (ended by itself or by grec stop: no longer ours)
        static bool GameOn
        {
            get
            {
                if (!gameOpen) return false;
                if (GameRec.Running) return true;
                gameOpen = false;
                return false;
            }
        }

        // a recording was started and not stopped yet (its ffmpeg may have ended by itself: the cap, an error)
        static bool Open { get { if (GameOn) return true; lock (gate) return proc != null; } }

        // any thread: a start, a stop or the frame cutting is still at work (the file is not complete)
        public static bool Pending { get { return starting || stops > 0 || cutting || (GameOn && GameRec.Phase != "recording"); } }

        // any thread: the record block of time {op:"state"}
        public static Json.Obj StateObj()
        {
            if (viaGame) return GameStateObj();
            var o = new Json.Obj();
            o["source"] = "desktop";
            lock (gate)
            {
                bool on = proc != null && !SafeExited(proc);
                o["on"] = on;
                o["starting"] = starting;
                if (stops > 0) o["stopping"] = true;
                if (file != null) o["file"] = file;
                if (on || clock.ElapsedMilliseconds > 0) o["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
                if (rectW > 0) o["rect"] = Fmt.I(rectW) + "x" + Fmt.I(rectH) + " at " + Fmt.I(rectX) + "," + Fmt.I(rectY) + " on output " + Fmt.I(outputIdx);
                if (encUsed != null) o["encoder"] = encUsed;
                if (on && maxUsed > 0) o["max_seconds"] = (double)maxUsed;
                if (framesDir != null) o["frames_dir"] = framesDir;
                if (cutting) o["cutting_frames"] = true;
                if (note != null) o["note"] = note;
                if (proc != null && SafeExited(proc))
                    o["problem"] = maxUsed > 0 && clock.Elapsed.TotalSeconds >= maxUsed - 2 ? "the recording reached its cap of " + Fmt.I(maxUsed) + " s (editor.cfg " + MaxKey + ") and ended; rec stop closes it"
                                                                                           : "ffmpeg exited: " + Tail();
                else if (lastProblem != null) o["problem"] = lastProblem;
            }
            return o;
        }

        // the record block while the source is the game's own frames (grec's state in the same field names)
        static Json.Obj GameStateObj()
        {
            var g = GameRec.StatusObj();
            var o = new Json.Obj();
            o["source"] = "game";
            string phase = g.Str("phase", "idle");
            bool mine = GameOn;
            o["on"] = mine && phase == "recording";
            o["starting"] = mine && (phase == "opening" || phase == "testing the encoder");
            if (stops > 0 || (mine && (phase == "draining" || phase == "closing"))) o["stopping"] = true;
            o["phase"] = phase;
            foreach (var k in new[] { "file", "seconds", "encoder", "fps", "every", "video_seconds", "has_duration", "mb" })
                if (g.ContainsKey(k)) o[k] = g[k];
            lock (gate)
            {
                if (framesDir != null) o["frames_dir"] = framesDir;
                if (cutting) o["cutting_frames"] = true;
                if (note != null) o["note"] = note;
                string problem = g.Str("error", null) ?? g.Str("warning", null) ?? lastProblem;
                if (problem != null) o["problem"] = problem;
            }
            return o;
        }

        // worker thread (the MCP call), the default source: the game's own frames through grec. Waits until frames are
        // written or the start failed; throws with grec's refusal (busy names the holder of the readback)
        public static void StartGame(Json.Obj a, CallContext ctx)
        {
            var opt = new Options
            {
                fps = Math.Max(5, Math.Min(60, PlayShared.Int(a, "record_fps", 30))),
                cq = Math.Max(0, Math.Min(51, PlayShared.Int(a, "cq", 16))),
                raise = PlayShared.Bool(a, "raise", false),
                file = PlayShared.Str(a, "file", null),
                enc = PlayShared.Str(a, "enc", null)
            };
            int every = PlayShared.Int(a, "every", 0);
            string why = ctx.Main(() => GameStart(opt, every));
            if (why != null)
            {
                var code = why.StartsWith("busy", StringComparison.Ordinal) ? McpErr.Busy : why.StartsWith("usage", StringComparison.Ordinal) ? McpErr.BadArgs : McpErr.GameError;
                throw new McpException(code, why, code == McpErr.Busy ? "stop the other capture first (grec stop, film {op:\"grec_stop\"} or the mcam run), or time {op:\"record\", source:\"desktop\"}" : null);
            }
            while (GameRec.Running && GameRec.Phase != "recording" && ctx.RemainingMs > 2000 && !ctx.Cancelled) ctx.Sleep(40);
            if (GameRec.Phase == "failed")
            {
                string s = GameRec.Status();
                int nl = s.IndexOf('\n');
                throw new McpException(McpErr.GameError, "record failed: " + (nl >= 0 ? s.Substring(0, nl) : s));
            }
        }

        // main thread: hand the start to grec; null when it started, else the reply (busy / failed: / usage:)
        static string GameStart(Options opt, int every)
        {
            lock (gate) { if (proc != null) return "busy: a desktop recording is running (" + file + "); rec stop first"; }
            if (Pending) return "busy: the recorder is still starting or closing a file; rec status";
            if (every < 0 || every > 600) return "usage: every=<1..600>, got " + Fmt.I(every);
            int ev = every > 0 ? every : EveryFor(opt.fps);
            var line = new List<string> { "grec", "start" };
            if (!string.IsNullOrEmpty(opt.file)) line.Add(Path.GetFileName(opt.file.Trim().Trim('"')));
            line.Add(Fmt.I(ev));
            line.Add(Fmt.I(opt.cq));
            if (!string.IsNullOrEmpty(opt.enc)) line.Add("enc=" + opt.enc);
            if (every <= 0) line.Add("fps=" + Fmt.I(opt.fps));   // record_fps is the rate of the file; every only thins the grabs
            string r = GameRec.Command(line);
            if (r.StartsWith("grec: opening", StringComparison.Ordinal))
            {
                string tried;
                try { gameExe = Ffmpeg.Find(out tried); } catch (Exception) { gameExe = null; }
                lock (gate) { lastProblem = null; framesDir = null; note = opt.raise ? "raise is only used by the desktop source" : null; }
                viaGame = true;
                gameOpen = true;
                DevLog.Write("[play] rec start: the game's own frames through grec, every " + Fmt.I(ev) + " frame(s)");
                return null;
            }
            if (r.StartsWith("grec: already running", StringComparison.Ordinal)) return "busy: the frame readback is held by grec (" + r.Substring(6) + "); grec stop first";
            if (r.StartsWith("grec: failed: ", StringComparison.Ordinal)) return "failed: " + r.Substring(14);
            return r;
        }

        // main thread: every n-th rendered frame that gives about fps frames per second of game time
        static int EveryFor(int fps)
        {
            double rate = UnityEngine.Time.captureFramerate;
            if (rate <= 0)
            {
                bool stepping = false;
                try { stepping = Stepper.Clock.Touched; } catch (Exception) { }
                if (stepping) rate = 60;
            }
            if (rate <= 0)
            {
                try { var s = Stepper.State(); if (s["fps"] is double) rate = (double)s["fps"]; } catch (Exception) { }
            }
            if (rate < 5 || rate > 1000) rate = 60;
            return Math.Max(1, Math.Min(600, (int)Math.Round(rate / Math.Max(1, fps))));
        }

        // worker thread (the MCP call), source desktop: start ffmpeg; returns the state or throws
        public static Json.Obj Start(Json.Obj a)
        {
            var opt = new Options
            {
                fps = Math.Max(5, Math.Min(60, PlayShared.Int(a, "record_fps", 30))),
                cq = Math.Max(0, Math.Min(51, PlayShared.Int(a, "cq", 16))),
                raise = PlayShared.Bool(a, "raise", false),
                file = PlayShared.Str(a, "file", null),
                output = PlayShared.Int(a, "output", -1),
                enc = PlayShared.Str(a, "enc", null)
            };
            string extra = string.IsNullOrEmpty(PlayShared.Str(a, "ffmpeg", null)) ? null
                : "the ffmpeg argument is not used (a client cannot name the program to start); ffmpeg comes from editor.cfg ffmpeg.path, INSIDEDEV_FFMPEG, <game>/_mod/ffmpeg or PATH";
            StartWith(opt, AskMain(), extra);
            return StateObj();
        }

        // main thread: the settings a start reads once
        static Setup MainSetup()
        {
            var s = new Setup();
            try
            {
                s.ffmpeg = Ffmpeg.Find(out s.tried);
                s.enc = EditorState.Get(FilmEnc.ConfigKey, FilmEnc.Auto).Trim().ToLowerInvariant();
                s.maxSeconds = Math.Max(0, Math.Min(86400, EditorState.Get(MaxKey, MaxDefault)));
                s.dir = Path.Combine(Extensions.Dir, FilmFiles.Folder);
            }
            catch (Exception e) { s.error = e.GetType().Name + ": " + e.Message; }
            return s;
        }

        // main thread (Stepper start and wake, and the film tick while a start waits for its settings)
        public static void Prime()
        {
            if (mainThread == 0) mainThread = Thread.CurrentThread.ManagedThreadId;
            var t = PrimeTick;
            if (t != null)
            {
                t.Active = false;
                if (wantSetup) t.Active = true;
            }
            if (!wantSetup) return;
            wantSetup = false;
            setupReply = MainSetup();
            setupDone.Set();
        }

        // the "film" tick (PfmXFilm): runs Prime only while a worker waits for the main thread's settings
        internal static PlayTick PrimeTick;

        static Setup AskMain()
        {
            if (mainThread != 0 && Thread.CurrentThread.ManagedThreadId == mainThread) return MainSetup();
            lock (askGate)
            {
                setupDone.Reset();
                wantSetup = true;
                var t = PrimeTick;
                if (t != null) t.Active = true;
                if (!setupDone.WaitOne(5000, false))
                {
                    wantSetup = false;
                    throw new McpException(McpErr.NotReady, "the game is not running frames now (minimized, loading or stopped), so the recorder could not read its settings", "try again when the game runs");
                }
                return setupReply;
            }
        }

        static void StartWith(Options opt, Setup setup, string extraNote)
        {
            lock (gate)
            {
                if (proc != null && !SafeExited(proc)) throw new McpException(McpErr.Busy, "a recording is running (" + file + "); stop it first (time {op:\"record_stop\"} or rec stop)");
                if (starting) throw new McpException(McpErr.Busy, "a recording is starting");
                if (stops > 0) throw new McpException(McpErr.Busy, "the last recording is still being closed");
                if (GameOn) throw new McpException(McpErr.Busy, "a recording of the game's own frames is running (grec); stop it first (time {op:\"record_stop\"} or rec stop)");
                starting = true;
                viaGame = false;
                lastProblem = null;
                note = null;
            }
            try
            {
                if (setup == null || setup.error != null) throw new McpException(McpErr.NotReady, "the recorder settings could not be read: " + (setup != null ? setup.error : "no reply"));
                string exe = setup.ffmpeg;
                if (exe == null) throw new McpException(McpErr.NotReady, "ffmpeg.exe not found (tried " + setup.tried + ")", "set editor.cfg ffmpeg.path (cfg ffmpeg.path <full path>), INSIDEDEV_FFMPEG, or put ffmpeg.exe in <game>/_mod/ffmpeg");
                Win.Rect win, mon;
                IntPtr hmon;
                IntPtr hwnd = Win.GameWindow(out win, out mon, out hmon);
                if (hwnd == IntPtr.Zero) throw new McpException(McpErr.NotReady, "the game window was not found (minimized or not created yet)");
                int w = win.right - win.left, h = win.bottom - win.top;
                int x = win.left - mon.left, y = win.top - mon.top;
                if (x < 0 || y < 0 || x + w > mon.right - mon.left || y + h > mon.bottom - mon.top)
                    throw new McpException(McpErr.Refused, "the game window (" + Fmt.I(w) + "x" + Fmt.I(h) + " at " + Fmt.I(win.left) + "," + Fmt.I(win.top) + ") is not inside one monitor (" + Fmt.I(mon.right - mon.left) + "x" + Fmt.I(mon.bottom - mon.top) + " at " + Fmt.I(mon.left) + "," + Fmt.I(mon.top) + "); move it fully onto one screen");
                w &= ~1; h &= ~1;
                if (w < 16 || h < 16) throw new McpException(McpErr.NotReady, "the game window is too small to record (" + Fmt.I(w) + "x" + Fmt.I(h) + ")");
                int idx = opt.output >= 0 ? opt.output : FindOutput(exe, hmon, mon);
                if (idx > 15) throw new McpException(McpErr.BadArgs, "output " + Fmt.I(idx) + ": DXGI outputs are numbered from 0");
                string why;
                string enc = FilmEnc.Pick(exe, string.IsNullOrEmpty(opt.enc) ? setup.enc : opt.enc, out why);
                if (enc == null) throw new McpException(McpErr.GameError, why, "rec start enc=<encoder>, or cfg " + FilmEnc.ConfigKey + " <encoder>");
                if (opt.raise) Win.Raise(hwnd);
                string path = FilmFiles.Resolve(setup.dir, "rec", ".mkv", string.IsNullOrEmpty(opt.file) ? null : Path.GetFileName(opt.file), true, out why);
                if (path == null) throw new McpException(McpErr.BadArgs, why);
                Directory.CreateDirectory(setup.dir);
                bool nv = enc.EndsWith("_nvenc", StringComparison.Ordinal);
                string args = "-hide_banner -loglevel error -y -f lavfi -i \"ddagrab=output_idx=" + Fmt.I(idx) + ":framerate=" + Fmt.I(opt.fps) + ":draw_mouse=0:video_size=" + Fmt.I(w) + "x" + Fmt.I(h) + ":offset_x=" + Fmt.I(x) + ":offset_y=" + Fmt.I(y) +
                    (nv ? "" : ",hwdownload,format=bgra") + "\"" +
                    " -c:v " + enc + " " + FilmEnc.Args(enc, opt.cq) + " -g " + Fmt.I(opt.fps * 2) + " -an" + (setup.maxSeconds > 0 ? " -t " + Fmt.I(setup.maxSeconds) : "") + " " + Ffmpeg.Quote(path);
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = false, CreateNoWindow = true };
                Process p;
                try { p = Process.Start(psi); }
                catch (Exception e) { throw new McpException(McpErr.NotReady, "cannot start ffmpeg (" + exe + "): " + e.Message, "set editor.cfg ffmpeg.path or INSIDEDEV_FFMPEG"); }
                bool inJob = ChildJob.Add(p);
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
                var notes = new List<string>();
                if (extraNote != null) notes.Add(extraNote);
                if (Win.Covered(hwnd, win)) notes.Add("another window covers the centre of the game picture: desktop capture records what is on top (raise lifts the game window; grec records the game's own frames)");
                if (!inJob) notes.Add("ffmpeg could not be tied to the game process: if the game crashes, the recording runs on until the " + (setup.maxSeconds > 0 ? Fmt.I(setup.maxSeconds) + " s cap" : "disk is full"));
                lock (gate)
                {
                    proc = p; file = path; ffmpegUsed = exe; framesDir = null; encUsed = enc; maxUsed = setup.maxSeconds;
                    outputIdx = idx; rectX = x; rectY = y; rectW = w; rectH = h;
                    note = notes.Count > 0 ? string.Join("; ", notes.ToArray()) : null;
                    clock.Reset(); clock.Start();
                }
                Restorer.Register(RestoreKey, "real-time recording (ffmpeg ddagrab)", RestoreNow);
                DevLog.Write("[play] rec start " + Fmt.I(w) + "x" + Fmt.I(h) + " at " + Fmt.I(x) + "," + Fmt.I(y) + " on output " + Fmt.I(idx) + ", " + Fmt.I(opt.fps) + " fps, " + enc + " cq " + Fmt.I(opt.cq) + " -> " + path);
            }
            finally { starting = false; }
        }

        // main thread (restore): never waits for ffmpeg, except at game quit, where the file would otherwise be cut off
        static string RestoreNow()
        {
            if (!Open) { try { Restorer.Forget(RestoreKey); } catch { } return "no recording was running"; }
            if (FilmShared.Quitting || !On) return Stop(0, 3000, false).Str("file", "stopped");
            string path = file;
            new Thread(() => { try { Stop(0); } catch (Exception e) { lock (gate) lastProblem = e.Message; } }) { IsBackground = true, Name = "rec stop" }.Start();
            return "recording stopping: " + path;
        }

        // any thread: stop the running recording (q on stdin, then a kill after 15 s); reviewFps > 0 also cuts JPG frames
        public static Json.Obj Stop(double reviewFps) { return Stop(reviewFps, 15000, false); }

        static Json.Obj Stop(double reviewFps, int waitMs, bool counted)
        {
            if (!counted) Interlocked.Increment(ref stops);
            if (viaGame) return StopGame(reviewFps, waitMs);
            Process p;
            string path, exe;
            lock (gate) { p = proc; path = file; exe = ffmpegUsed; proc = null; clock.Stop(); }
            var o = new Json.Obj();
            try
            {
                if (p == null) { o["stopped"] = false; o["note"] = "no recording was running"; return o; }
                bool clean = false;
                try
                {
                    if (!SafeExited(p))
                    {
                        p.StandardInput.Write("q");
                        p.StandardInput.Flush();
                        p.StandardInput.Close();
                        clean = p.WaitForExit(waitMs);
                        if (!clean) { try { p.Kill(); } catch { } }
                    }
                    else clean = true;
                }
                catch { try { p.Kill(); } catch { } }
                o["stopped"] = true;
                o["file"] = path;
                o["clean"] = clean;
                try { if (File.Exists(path)) o["mb"] = Math.Round(new FileInfo(path).Length / 1e6, 1); } catch { }
                int d = FilmFiles.MkvDuration(path);
                if (d >= 0) o["has_duration"] = d == 1;
                o["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
                if (!clean || d == 0) lock (gate) lastProblem = "ffmpeg was cut off before it finished the file (it plays, but has no duration; remux: ffmpeg -i <file> -c copy <new file>)";
                DevLog.Write("[play] rec stop " + path + (clean ? "" : " (killed)"));
                try { Restorer.Forget(RestoreKey); } catch { }
                if (reviewFps > 0 && File.Exists(path)) o["frames_dir"] = CutFrames(exe, path, reviewFps);
            }
            finally { Interlocked.Decrement(ref stops); }
            return o;
        }

        // any thread, source game: stop grec (a flag) and wait for its file to close; the caller counted stops
        static Json.Obj StopGame(double reviewFps, int waitMs)
        {
            var o = new Json.Obj();
            o["source"] = "game";
            try
            {
                if (!GameOn) { o["stopped"] = false; o["note"] = "no recording was running"; return o; }
                gameOpen = false;
                GameRec.Command(new List<string> { "grec", "stop" });
                var wait = Stopwatch.StartNew();
                while (GameRec.Running && wait.ElapsedMilliseconds < waitMs) Thread.Sleep(40);
                var g = GameRec.StatusObj();
                string path = g.Str("file", null);
                o["stopped"] = true;
                if (path != null) o["file"] = path;
                bool closed = !GameRec.Running;
                o["clean"] = closed && g.Str("phase", "") == "done";
                foreach (var k in new[] { "mb", "has_duration", "seconds", "video_seconds", "finish", "error" })
                    if (g.ContainsKey(k)) o[k] = g[k];
                if (!closed) o["note"] = "the file is still being closed (rec status / grec status)";
                DevLog.Write("[play] rec stop (grec) " + (path ?? ""));
                if (reviewFps > 0 && closed && path != null && gameExe != null && File.Exists(path)) o["frames_dir"] = CutFrames(gameExe, path, reviewFps);
            }
            finally { Interlocked.Decrement(ref stops); }
            return o;
        }

        // JPG review frames at reviewFps into <video>_frames; returns the folder
        static string CutFrames(string exe, string video, double reviewFps)
        {
            string dir = Path.Combine(Path.GetDirectoryName(video), Path.GetFileNameWithoutExtension(video) + "_frames").Replace('\\', '/');
            Directory.CreateDirectory(dir);
            string args = "-hide_banner -loglevel error -y -i " + Ffmpeg.Quote(video) + " -vf fps=" + Fmt.F(reviewFps) + " -q:v 2 " + Ffmpeg.Quote(dir + "/f%04d.jpg");
            cutting = true;
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
                ChildJob.Add(p);
                if (!p.WaitForExit(600000)) { try { p.Kill(); } catch { } }
            }
            catch (Exception e) { lock (gate) lastProblem = "frames: " + e.Message; }
            finally { cutting = false; }
            lock (gate) framesDir = dir;
            return dir;
        }

        // console (main thread): rec start [fps] [cq] [raise] [file] [output=<n>] [enc=<name>] | rec stop [review_fps] [wait] |
        // rec status [json] | rec wait
        public static string Command(List<string> args)
        {
            var a = new List<string>(args);
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            if (sub == "status") return FilmShared.TakeLast(a, "json", 2) ? JsonLine.Write(StateObj()) : JsonOut.Write(StateObj());
            if (sub == "wait") return (Pending ? "pending: " : "") + JsonLine.Write(StateObj());
            if (sub == "start")
            {
                var opt = new Options();
                int numbers = 0, every = 0;
                bool desktop = false;
                for (int i = 2; i < a.Count; i++)
                {
                    int n;
                    string k = a[i].ToLowerInvariant();
                    if (k == "raise") opt.raise = true;
                    else if (k == "desktop") desktop = true;
                    else if (k.StartsWith("every=", StringComparison.Ordinal))
                    {
                        if (!Fmt.TryI(k.Substring(6), out every) || every < 1 || every > 600) return "usage: every=<1..600>, got " + a[i] + "\n" + Usage;
                    }
                    else if (k.StartsWith("output=", StringComparison.Ordinal))
                    {
                        if (!Fmt.TryI(k.Substring(7), out opt.output) || opt.output < 0 || opt.output > 15) return "usage: output=<0..15>, got " + a[i] + "\n" + Usage;
                    }
                    else if (k.StartsWith("enc=", StringComparison.Ordinal))
                    {
                        opt.enc = k.Substring(4);
                        if (!FilmEnc.IsKnown(opt.enc)) return "usage: enc=" + opt.enc + " is not one of " + FilmEnc.List;
                    }
                    else if (Fmt.TryI(a[i], out n)) { if (numbers++ == 0) opt.fps = Math.Max(5, Math.Min(60, n)); else opt.cq = Math.Max(0, Math.Min(51, n)); }
                    else opt.file = a[i];
                }
                if (On) return "busy: a recording is running (" + (GameOn ? GameRec.StatusObj().Str("file", "grec") : file) + "); rec stop first";
                if (Pending) return "busy: the recorder is still starting or closing a file; rec status";
                if (!desktop)
                {
                    if (opt.output >= 0) return "usage: output=<n> is for the desktop source (rec start ... desktop)\n" + Usage;
                    string why = GameStart(opt, every);
                    return why ?? "rec: starting, source game: the game's own frames through grec, windows over the game stay out (rec status shows the file, or the problem)";
                }
                if (every > 0) return "usage: every=<n> is for the game source (rec start without desktop)\n" + Usage;
                var setup = MainSetup();
                if (setup.ffmpeg == null && setup.error == null) return "failed: ffmpeg.exe not found (tried " + setup.tried + "); set editor.cfg ffmpeg.path, INSIDEDEV_FFMPEG or put it in <game>/_mod/ffmpeg";
                var t = new Thread(() =>
                {
                    try { StartWith(opt, setup, null); }
                    catch (Exception e) { lock (gate) lastProblem = e.Message; DevLog.Write("[play] rec start failed: " + e.Message); }
                }) { IsBackground = true, Name = "rec start" };
                t.Start();
                return "rec: starting, source desktop: " + DesktopNote + " (rec status shows the file, or the problem)";
            }
            if (sub == "stop")
            {
                bool wait = FilmShared.TakeLast(a, "wait", 2);
                double fps = 0;
                if (a.Count > 2 && (!Fmt.TryD(a[2], out fps) || fps < 0 || fps > 30)) return "usage: rec stop [review_fps 0..30] [wait]";
                if (!Open) return "rec: no recording is running";
                var t = new Thread(() => { try { Stop(fps, 15000, true); } catch (Exception e) { lock (gate) lastProblem = e.Message; } }) { IsBackground = true, Name = "rec stop" };
                Interlocked.Increment(ref stops);
                t.Start();
                return "rec: stopping" + (fps > 0 ? ", then cutting review frames at " + Fmt.F(fps) + " fps" : "") + (wait ? "\nwait: rec wait" : "");
            }
            return "usage: " + Usage;
        }

        static string Tail() { lock (errTail) return errTail.Length == 0 ? "(no message)" : errTail.ToString(); }

        static bool SafeExited(Process p) { try { return p.HasExited; } catch { return true; } }

        // the ddagrab output that shows the game's monitor: the DXGI output with that monitor handle (or desktop
        // rectangle); when DXGI cannot be asked, the only output of that size according to ffmpeg, never a guess
        static int FindOutput(string exe, IntPtr hmon, Win.Rect mon)
        {
            int mw = mon.right - mon.left, mh = mon.bottom - mon.top;
            string size = Fmt.I(mw) + "x" + Fmt.I(mh);
            string dxgiError;
            var outs = Dxgi.Outputs(out dxgiError);
            if (outs != null)
            {
                foreach (var o in outs) if (o.monitor == hmon && hmon != IntPtr.Zero) return o.index;
                foreach (var o in outs) if (o.left == mon.left && o.top == mon.top && o.right == mon.right && o.bottom == mon.bottom) return o.index;
                throw new McpException(McpErr.Refused, "the game's monitor (" + size + " at " + Fmt.I(mon.left) + "," + Fmt.I(mon.top) + ") is not one of the " + Fmt.I(outs.Count) + " output(s) of the default graphics adapter: it is driven by another adapter, which ddagrab cannot read from here",
                    "move the game to a monitor of the default adapter, or record the game's own frames with grec");
            }
            var hits = new List<int>();
            for (int i = 0; i < 6; i++)
            {
                string line = Probe(exe, i);
                if (line == null) break;
                if (line.Contains("dimensions " + size)) hits.Add(i);
            }
            if (hits.Count == 1) return hits[0];
            if (hits.Count == 0) throw new McpException(McpErr.Refused, "no DXGI output of the default graphics adapter is " + size + ": the game's monitor is driven by another adapter, which ddagrab cannot read from here");
            throw new McpException(McpErr.Refused, Fmt.I(hits.Count) + " outputs are " + size + " and DXGI could not be asked which one shows the game (" + dxgiError + ")", "say which: rec start output=<n> (0 is the first monitor of the adapter)");
        }

        static string Probe(string exe, int idx)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, "-hide_banner -v verbose -f lavfi -i ddagrab=output_idx=" + Fmt.I(idx) + ":framerate=1:draw_mouse=0 -frames:v 1 -f null -")
                    { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true }))
                {
                    ChildJob.Add(p);
                    string all = FilmShared.ReadAll(p, 10000);
                    if (all == null) return null;
                    foreach (var l in all.Split('\n')) if (l.Contains("Opened dxgi output")) return l;
                    return null;
                }
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
            [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
            [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
            [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();

            public static IntPtr GameWindow(out Rect win, out Rect mon) { IntPtr h; return GameWindow(out win, out mon, out h); }

            // the visible window of this process with the largest client area; win = client rect in screen pixels,
            // mon / hmon = the monitor it is on
            public static IntPtr GameWindow(out Rect win, out Rect mon, out IntPtr hmon)
            {
                win = new Rect(); mon = new Rect(); hmon = IntPtr.Zero;
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
                    hmon = MonitorFromWindow(best, 2);
                    GetMonitorInfo(hmon, ref mi);
                    mon = mi.rcMonitor;
                    return best;
                }
                finally { if (ctx) { try { SetThreadDpiAwarenessContext(old); } catch { } } }
            }

            // true when the top window at the centre of win (screen pixels) is not the game window
            public static bool Covered(IntPtr hwnd, Rect win)
            {
                IntPtr old = IntPtr.Zero;
                bool ctx = false;
                try { old = SetThreadDpiAwarenessContext(new IntPtr(-4)); ctx = old != IntPtr.Zero; } catch { }
                try
                {
                    IntPtr top = WindowFromPoint(new Point { x = (win.left + win.right) / 2, y = (win.top + win.bottom) / 2 });
                    if (top == IntPtr.Zero) return false;
                    IntPtr root = GetAncestor(top, 2);
                    return root != hwnd && top != hwnd;
                }
                catch { return false; }
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
