using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // GameRec.cs (owner TIME): video of the game's own picture, independent of the desktop: windows over the game, focus and
    // other programs never reach the file, unlike desktop duplication (the real-time recorder). At the end of every
    // n-th frame the back buffer is copied on the GPU by the native InsideReadback plugin (INSIDE_Data/Plugins, the same path
    // as MultiCam's replay pipes: an event queued with rb_push and a blit into a 1x1 marker render texture that the plugin's
    // OMSetRenderTargets hook sees on the render thread) and mapped a few frames later. The plugin has one session, so grec
    // holds Owners.Readback as "grec" from start to the end of the close.
    // Threads: the coroutine (DevRoot, end of frame) only queues grab events, and only for frames whose game time moved
    // (GrecGate: a frozen game, the stepper between agent calls or a pause, renders the same moment over and over; those
    // frames are counted as still and never grabbed). Every grab frame it also reads the plugin's state (rb_stat_get),
    // which puts the OMSetRenderTargets hook back when its dispatch slot was rewritten (2026-11-11: once an agent run
    // started, the plugin saw no marker again for the rest of the session and its event queue filled to 256; until then
    // grec read the state only while probing); if the plugin still does not run its events it opens a new session.
    // The markers are in fact not seen while the stepper runs logic steps (captureFramerate 60, live test 2026-11-11);
    // plugin version 3 then runs the waiting grab at the swap chain's Present (at most one frame late, counted in status). The take thread copies every finished frame out of the plugin into a native buffer and
    // frees the plugin's slot at once, so the render thread never waits for the encoder; GrecLedger pairs each picture
    // with its grab by tag, so a grab whose picture never came counts as lost once. The write thread hands the buffers
    // to ffmpeg's stdin. Between them is a bounded queue: when the encoder is too slow the take thread drops the picture.
    // Timeline (fixed at grec start and reported once): the file is constant frame rate. Count mode, only while a capture
    // rate is set by something other than the agent's stepper (srec, a user): one file frame per grab at capture rate /
    // every (fps= overrides the number), a lost picture is written as a copy of the previous frame. Time mode otherwise
    // (real-time play, the stepper; nominal fps=, else 60 / every): GrecPlacer puts file frame k on the picture nearest
    // to t0 + k / fps by game time, so video time = game time (= wall time and the audio length in real-time play) and
    // frozen time is left out. <file>.times.csv (on by default, times=off) has one row per file frame: index, the shown
    // grab's frame / game_time / real_time, repeated, video_time (index / fps) and slot_game_time (the game time the
    // frame stands for); game time is UnityEngine.Time.timeDbl at the end of the grabbed frame.
    // End: ffmpeg leaving by itself (no such encoder, bad size, disk full) ends the capture in that frame and shows in
    // grec status. A stop drains for at most 3 s on the coroutine, then a close thread waits for the writer, closes stdin
    // and waits for ffmpeg, so the container is finished (an mkv then carries its duration; status says when it does not).
    // Commands: grec start [file] [every] [cq] [enc=] [fps=] [times=off], grec stop [wait], grec status [json], grec wait.
    // Restore and a game quit stop it.
    public static class GameRec
    {
        public const string RestoreKey = "grec", Owner = "grec";
        public const string Usage = "grec start [file.mkv] [every n-th frame, default 2] [cq 0..51, default 16] [enc=auto|<encoder>] [fps=<rate of the file>] [times=off] | grec stop [wait] | grec status [json] | grec wait";
        const int Event = 0x52420000, OpGrab = 1, OpFlush = 2, OpProbe = 3, Stages = 4, Lag = 2, CpuFrames = 6;
        const long QueueBytes = 256L << 20;
        const int QueueMin = 4, QueueMax = 24;
        // frames per game second of the stepper's runs and of track playback
        const int StepRate = 60;

        [StructLayout(LayoutKind.Sequential)]
        struct Stat
        {
            public int version, deviceType, hasDevice, renderThread, source, width, height, dxgiFormat, samples;
            public int stageCount, lag, cpuCount, pending, lastError, session, ignored;
            public long grabs, flushes, probes, frames, errors, dropped, produced, taken, released;
            public double mapWaitMs, mapWaitMaxMs, slotWaitMs, slotWaitMaxMs, copyMs, eventMs, eventMaxMs;
            public long calls, deviceCalls, markerHits, queued, hookCalls;
            public int lastEvent, lastDeviceEvent, hooked, queueDepth;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Frame
        {
            public long seq, bytes;
            public IntPtr data;
            public int slot, tag, width, height, pitch, dxgiFormat, error, reserved;
        }

        // one grab in grab order for the writer: its picture (b, has) or none (lost / dropped)
        struct Entry
        {
            public IntPtr b;
            public bool has;
            public GrecStamp s;
        }

        [DllImport("InsideReadback")] static extern int rb_version();
        [DllImport("InsideReadback")] static extern int rb_open(int stages, int lag, int cpu);
        [DllImport("InsideReadback")] static extern void rb_close();
        [DllImport("InsideReadback")] static extern int rb_take(int timeoutMs, ref Frame f);
        [DllImport("InsideReadback")] static extern void rb_release(int slot);
        [DllImport("InsideReadback")] static extern int rb_stat_get(ref Stat s);
        [DllImport("InsideReadback")] static extern int rb_push(int eventID);
        [DllImport("InsideReadback")] static extern void rb_set_marker(IntPtr tex, IntPtr tex2);
        [DllImport("InsideReadback")] static extern void rb_free_buffers();
        [DllImport("InsideReadback")] static extern long rb_info(int what);

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(IntPtr file, IntPtr buffer, uint count, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll")] static extern void RtlMoveMemory(IntPtr dst, IntPtr src, UIntPtr count);
        [DllImport("kernel32.dll")] static extern void RtlZeroMemory(IntPtr dst, UIntPtr count);

        static readonly object gate = new object();
        static readonly WaitForEndOfFrame eof = new WaitForEndOfFrame();
        static RenderTexture marker, marker2;
        static Process ffmpeg;
        static Stream stdin;
        static IntPtr stdinHandle;
        static Thread taker, writer, closer;
        static volatile bool stopAsked, running, closing, takeStop, writeStop, pipeDead, ffExited, quick, owned, cut;
        static volatile string error, phase = "idle", warning;
        static string file = "", pixFmt = "", exe = "", encoder = "", encWanted = "", rateFrom = "", finish = "";
        static int session, emitted, every = 2, cq = 16, w, h, rateAtStart;
        static double fps, fpsAsked;
        static bool timesOn, replaced;
        static int ffExit = int.MinValue;
        static readonly Stopwatch clock = new Stopwatch();
        static readonly StringBuilder errTail = new StringBuilder();

        // under gate
        static int issued, dropReadback, dropLate, dropLost, queuedNow, queueCap, buffersMade, stray, reopened, reopenFails;
        static volatile bool byTime, sessionOpen;
        static long frameBytes;
        static readonly Queue<Entry> queue = new Queue<Entry>();
        static readonly Stack<IntPtr> pool = new Stack<IntPtr>();
        static GrecLedger ledger = new GrecLedger();
        static volatile GrecPlacer<IntPtr> placer;
        static volatile GrecGate frameGate;
        static volatile string stallNote;
        static float reopenAt;
        // the plugin's Present fallback (version 3): grabs it ran at Present because their marker never came, and the
        // older ones it dropped next to them; counted from this recording's start
        static long presentGrabs, presentDrops, presentGrabs0, presentDrops0, swapSwitches, swapSwitches0;
        const int PresentFallbackVersion = 3;
        static int pluginVersion, pushRefused;
        // the first failure is error; the ones after it are kept here (grec status shows both)
        static readonly List<string> laterErrors = new List<string>();

        // any thread: a recording was asked for and its file is not closed yet
        public static bool Running { get { return running; } }
        public static string Phase { get { return phase; } }

        public static string Command(List<string> args)
        {
            var a = new List<string>(args);
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            if (sub == "start") return Start(a);
            if (sub == "stop")
            {
                bool wait = FilmShared.TakeLast(a, "wait", 2);
                if (!running) return "grec: not running";
                stopAsked = true;
                return "grec: stopping (grec status shows when the file is closed)" + (wait ? "\nwait: grec wait" : "");
            }
            if (sub == "wait") return running ? "pending: " + Status() : Status();
            if (sub == "status") return FilmShared.TakeLast(a, "json", 2) ? JsonLine.Write(StatusObj()) : Status();
            if (sub == "help") return "usage: " + Usage;
            return Status();
        }

        static string Start(List<string> a)
        {
            if (running) return "grec: already running into " + file;
            int at = 2, n;
            string name = null;
            if (a.Count > at && !IsOption(a[at])) name = a[at++];
            int ev = 2, q = 16, pos = 0;
            double rate = 0;
            string enc = null;
            bool times = true;
            for (; at < a.Count; at++)
            {
                string s = a[at], k = s.ToLowerInvariant();
                if (k == "times" || k == "times=on") { times = true; continue; }
                if (k == "times=off") { times = false; continue; }
                if (k.StartsWith("enc=", StringComparison.Ordinal)) { enc = k.Substring(4); continue; }
                if (k.StartsWith("fps=", StringComparison.Ordinal))
                {
                    if (!Fmt.TryD(s.Substring(4), out rate) || rate < 1 || rate > 1000) return "usage: fps=<1..1000>, got " + s + "\n" + Usage;
                    continue;
                }
                if (!Fmt.TryI(s, out n)) return "usage: unknown option " + s + "\n" + Usage;
                if (pos == 0) ev = n; else if (pos == 1) q = n; else return "usage: too many numbers\n" + Usage;
                pos++;
            }
            if (ev < 1 || ev > 600) return "usage: every n-th frame 1..600, got " + Fmt.I(ev);
            if (q < 0 || q > 51) return "usage: cq 0..51, got " + Fmt.I(q);
            string wantEnc = enc ?? EditorState.Get(FilmEnc.ConfigKey, FilmEnc.Auto).Trim().ToLowerInvariant();
            if (!FilmEnc.IsKnown(wantEnc)) return "usage: enc=" + wantEnc + " is not one of " + FilmEnc.List;
            string why, path = FilmFiles.Resolve(Path.Combine(Extensions.Dir, FilmFiles.Folder), "grec", ".mkv", name, false, out why);
            if (path == null) return "grec: failed: " + why;
            var root = UnityEngine.Object.FindObjectOfType<DevRoot>();
            if (root == null) return "grec: failed: no DevRoot to run on";
            string tried, found = Ffmpeg.Find(out tried);
            if (found == null) return "grec: failed: ffmpeg.exe not found (tried " + tried + "); set editor.cfg ffmpeg.path, INSIDEDEV_FFMPEG or put it in <game>/_mod/ffmpeg";
            pluginVersion = 0;
            try { pluginVersion = rb_version(); if (pluginVersion < 1) return "grec: failed: InsideReadback too old"; }
            catch (Exception e) { return "grec: failed: the InsideReadback plugin did not load (" + e.Message + "); it belongs in INSIDE_Data/Plugins"; }
            string holder;
            if (!Owners.TryTake(Owners.Readback, Owner, out holder)) return "busy: the frame readback is held by " + holder + " (it has one session); grec starts when that ends";
            owned = true;
            int s0 = 0;
            why = null;
            try { s0 = rb_open(Stages, Lag, CpuFrames); } catch (Exception e) { why = e.Message; }
            if (s0 == 0)
            {
                ReleaseOwner();
                return why != null ? "grec: failed: rb_open: " + why : "busy: InsideReadback did not open (another capture holds it or frames are still held)";
            }
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); }
            catch (Exception e) { try { rb_close(); } catch { } ReleaseOwner(); return "grec: failed: cannot create the folder of " + path + ": " + e.Message; }
            replaced = File.Exists(path);
            session = s0;
            sessionOpen = true;
            file = path; exe = found; every = ev; cq = q; fpsAsked = rate; encWanted = wantEnc; timesOn = times;
            encoder = ""; pixFmt = ""; finish = ""; w = h = 0;
            string from;
            bool timeMode;
            fps = RateFor(every, fpsAsked, out from, out timeMode);
            rateFrom = from;
            byTime = timeMode;
            error = null; stallNote = null;
            warning = pluginVersion < PresentFallbackVersion ? "InsideReadback version " + Fmt.I(pluginVersion) + " has no Present fallback: frames of agent runs (captureFramerate 60) are lost; install the new InsideReadback.dll into INSIDE_Data/Plugins" : null;
            presentGrabs = presentDrops = swapSwitches = 0;
            presentGrabs0 = presentDrops0 = swapSwitches0 = 0;
            pushRefused = 0;
            lock (laterErrors) laterErrors.Clear();
            if (pluginVersion >= PresentFallbackVersion) { try { presentGrabs0 = rb_info(4); presentDrops0 = rb_info(5); swapSwitches0 = rb_info(6); } catch (Exception) { } }
            stopAsked = closing = takeStop = writeStop = pipeDead = ffExited = quick = cut = false;
            ffExit = int.MinValue;
            lock (gate) { issued = dropReadback = dropLate = dropLost = queuedNow = stray = reopened = reopenFails = 0; ledger = new GrecLedger(); placer = null; frameGate = null; reopenAt = 0; }
            lock (errTail) errTail.Length = 0;
            clock.Reset();
            running = true;
            phase = "opening";
            Restorer.Register(RestoreKey, "game video (grec)", RestoreNow);
            root.StartCoroutine(Drive(Run()));
            return "grec: opening the readback, then recording every " + Fmt.I(every) + " frame(s) into " + file + " at " + Fmt.F(fps, "0.###") + " fps (" + rateFrom + ")" + (replaced ? " (replacing the existing file)" : "");
        }

        static bool IsOption(string s)
        {
            int n;
            string k = s.ToLowerInvariant();
            return k == "times" || k.StartsWith("times=", StringComparison.Ordinal) || k.StartsWith("enc=", StringComparison.Ordinal) || k.StartsWith("fps=", StringComparison.Ordinal) || Fmt.TryI(s, out n);
        }

        static string RestoreNow()
        {
            if (!running) return "grec not running";
            stopAsked = true;
            if (!FilmShared.Quitting) return "grec stopping";
            quick = true;
            Close();
            var c = closer;
            bool ended = c == null || c.Join(4000);
            return ended ? "grec closed " + file : "grec: the file was not finished before the game ended";
        }

        static void ReleaseOwner()
        {
            if (!owned) return;
            owned = false;
            Owners.Release(Owners.Readback, Owner);
        }

        public static string Status()
        {
            lock (gate)
            {
                var sb = new StringBuilder(200);
                bool failed = phase == "failed";
                var pl = placer;
                var fg = frameGate;
                int written = pl != null ? pl.written : 0, repeated = pl != null ? pl.repeated : 0, skipped = pl != null ? pl.skipped : 0, still = fg != null ? fg.still : 0;
                double t0Game = pl != null ? pl.t0 : 0, tLastGame = pl != null ? pl.tLast : 0;
                sb.Append("grec: ").Append(phase);
                if (failed) sb.Append(": ").Append(error ?? "no reason recorded").Append(Later()).Append(';');
                if (file.Length > 0) sb.Append(failed ? " " : ", ").Append(file);
                sb.Append(", ").Append(Fmt.I(issued)).Append(" grab(s), ").Append(Fmt.I(written)).Append(" written, ").Append(Fmt.I(byTime ? dropReadback + dropLate + dropLost : repeated)).Append(" dropped");
                if (byTime) sb.Append(" (placed by game time: ").Append(Fmt.I(repeated)).Append(" repeated, ").Append(Fmt.I(skipped)).Append(" skipped, grabs lost: readback ").Append(Fmt.I(dropReadback)).Append(", encoder too slow ").Append(Fmt.I(dropLate)).Append(", lost ").Append(Fmt.I(dropLost)).Append(')');
                else if (repeated > 0) sb.Append(" (readback ").Append(Fmt.I(dropReadback)).Append(", encoder too slow ").Append(Fmt.I(dropLate)).Append(", lost ").Append(Fmt.I(dropLost)).Append("; each is the previous frame again)");
                if (still > 0) sb.Append(", ").Append(Fmt.I(still)).Append(" still frame(s) left out (game time did not move)");
                if (presentGrabs > 0) sb.Append(", ").Append(Fmt.I(presentGrabs)).Append(" grab(s) taken at Present (marker not seen, up to one frame late; times.csv at_present)").Append(presentDrops > 0 ? ", " + Fmt.I(presentDrops) + " dropped there" : "");
                if (swapSwitches > 0) sb.Append(", swap chain replaced ").Append(Fmt.I(swapSwitches)).Append(" time(s) (the plugin followed the new one)");
                if (pushRefused > 0) sb.Append(", ").Append(Fmt.I(pushRefused)).Append(" plugin event(s) refused (queue full)");
                if (reopened > 0 || reopenFails > 0) sb.Append(", readback reopened ").Append(Fmt.I(reopened)).Append(" time(s)").Append(stallNote != null ? " (" + stallNote + ")" : "");
                if (w > 0) sb.Append(", ").Append(Fmt.I(w)).Append('x').Append(Fmt.I(h)).Append(' ').Append(pixFmt);
                if (clock.ElapsedMilliseconds > 0) sb.Append(", ").Append(Fmt.F(clock.Elapsed.TotalSeconds, "0.0")).Append(" s");
                if (fps > 0) sb.Append(", ").Append(Fmt.F(fps)).Append(" fps (").Append(rateFrom).Append(')');
                if (encoder.Length > 0) sb.Append(", ").Append(encoder);
                if (fps > 0 && written > 0) sb.Append(", video ").Append(Fmt.F(written / fps, "0.00")).Append(" s");
                if (tLastGame > t0Game) sb.Append(", game time ").Append(Fmt.F(tLastGame - t0Game, "0.00")).Append(" s");
                if (finish.Length > 0) sb.Append(", ").Append(finish);
                if (warning != null) sb.Append(", warning: ").Append(warning);
                if (error != null && !failed) sb.Append(", error: ").Append(error).Append(Later());
                return sb.ToString();
            }
        }

        // any thread: the same as Status for machines (grec status json, the film tool)
        public static Json.Obj StatusObj()
        {
            var o = new Json.Obj();
            lock (gate)
            {
                var pl = placer;
                var fg = frameGate;
                int written = pl != null ? pl.written : 0;
                double t0Game = pl != null ? pl.t0 : 0, tLastGame = pl != null ? pl.tLast : 0;
                o["phase"] = phase;
                o["recording"] = phase == "recording";
                o["busy"] = running;
                if (file.Length > 0) o["file"] = file;
                o["frames_issued"] = (double)issued;
                o["frames_written"] = (double)written;
                o["frames_repeated"] = (double)(pl != null ? pl.repeated : 0);
                o["frames_skipped"] = (double)(pl != null ? pl.skipped : 0);
                o["frames_still"] = (double)(fg != null ? fg.still : 0);
                if (presentGrabs > 0 || presentDrops > 0) { o["present_grabs"] = (double)presentGrabs; o["present_dropped"] = (double)presentDrops; }
                if (swapSwitches > 0) o["swap_chain_replaced"] = (double)swapSwitches;
                if (pushRefused > 0) o["events_refused"] = (double)pushRefused;
                string later = Later();
                if (later.Length > 0) o["errors_later"] = later.Substring(2);
                o["timeline"] = byTime ? "game_time" : "per_grab";
                o["game_time_span"] = Math.Round(Math.Max(0, tLastGame - t0Game), 3);
                if (byTime) o["max_gap_ms"] = Math.Round(pl != null ? pl.maxGapMs : 0, 1);
                if (reopened > 0 || reopenFails > 0) { o["readback_reopened"] = (double)reopened; if (stallNote != null) o["readback_stall"] = stallNote; }
                o["game_time_start"] = Math.Round(t0Game, 6);
                o["dropped_readback"] = (double)dropReadback;
                o["dropped_encoder_slow"] = (double)dropLate;
                o["dropped_lost"] = (double)dropLost;
                o["queue"] = (double)queuedNow;
                o["queue_max"] = (double)queueCap;
                if (w > 0) { o["width"] = (double)w; o["height"] = (double)h; o["pix_fmt"] = pixFmt; }
                o["every"] = (double)every;
                if (fps > 0) { o["fps"] = Math.Round(fps, 3); o["fps_from"] = rateFrom; o["video_seconds"] = Math.Round(written / fps, 3); o["video_time"] = Math.Round(written / fps, 3); }
                if (encoder.Length > 0) o["encoder"] = encoder;
                o["cq"] = (double)cq;
                o["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
                if (finish.Length > 0) o["finish"] = finish;
                if (phase == "done" || phase == "failed")
                {
                    if (ffExit != int.MinValue) o["ffmpeg_exit"] = (double)ffExit;
                    if (file.Length > 0) { o["mb"] = FilmFiles.Mb(file); int d = FilmFiles.MkvDuration(file); if (d >= 0) o["has_duration"] = d == 1; }
                }
                if (timesOn && file.Length > 0) o["times_file"] = file + ".times.csv";
                if (warning != null) o["warning"] = warning;
                if (error != null) o["error"] = error;
            }
            return o;
        }

        static IEnumerator Run()
        {
            marker = NewMarker();
            marker2 = NewMarker();
            rb_set_marker(marker.GetNativeTexturePtr(), marker2.GetNativeTexturePtr());
            var st = new Stat();
            for (int i = 0; i < 120 && !stopAsked; i++)
            {
                yield return eof;
                Emit(Event | (OpProbe << 12));
                yield return null;
                rb_stat_get(ref st);
                if (st.source == 1 && st.width > 0) break;
            }
            if (stopAsked) { Abort("stopped before the first frame"); yield break; }
            if (st.source != 1 || st.width <= 0) { Abort("the plugin did not find the swap chain (device " + Fmt.I(st.hasDevice) + ", source " + Fmt.I(st.source) + ")"); yield break; }
            pixFmt = st.dxgiFormat == 28 || st.dxgiFormat == 29 ? "rgba" : st.dxgiFormat == 87 || st.dxgiFormat == 91 ? "bgra" : st.dxgiFormat == 88 || st.dxgiFormat == 93 ? "bgr0" : "";
            if (pixFmt.Length == 0) { Abort("unsupported back buffer format " + Fmt.I(st.dxgiFormat)); yield break; }
            w = st.width;
            h = st.height;
            string enc = FilmEnc.Cached(exe, encWanted);
            if (enc == null)
            {
                phase = "testing the encoder";
                string encWhy = null;
                bool done = false;
                var t = new Thread(() => { try { enc = FilmEnc.Pick(exe, encWanted, out encWhy); } catch (Exception e) { encWhy = e.Message; } done = true; }) { IsBackground = true, Name = "grec encoder" };
                t.Start();
                while (!done && !stopAsked) yield return null;
                if (stopAsked) { Abort("stopped before the first frame"); yield break; }
                if (enc == null) { Abort(encWhy ?? "no encoder"); yield break; }
            }
            encoder = enc;
            frameBytes = (long)w * h * 4;
            lock (gate) { queueCap = (int)Math.Max(QueueMin, Math.Min(QueueMax, QueueBytes / frameBytes)); buffersMade = 0; queuedNow = 0; }
            try { StartFfmpeg(); }
            catch (Exception e) { Abort("ffmpeg: " + e.Message); yield break; }
            taker = new Thread(Take) { IsBackground = true, Name = "grec take" };
            writer = new Thread(Write) { IsBackground = true, Name = "grec write" };
            taker.Start();
            writer.Start();
            rateAtStart = Time.captureFramerate;
            phase = "recording";
            clock.Reset();
            clock.Start();
            DevLog.Write("[play] grec: recording " + Fmt.I(w) + "x" + Fmt.I(h) + " " + pixFmt + " every " + Fmt.I(every) + " frame(s) at " + Fmt.F(fps) + " fps (" + rateFrom + ") with " + encoder + " into " + file);
            var fg = new GrecGate(every, byTime ? GrecCore.MinGapSlots / fps : 0);
            frameGate = fg;
            var led = ledger;
            int sent = 0;
            while (!stopAsked)
            {
                yield return eof;
                if (pipeDead || ffExited) break;
                if (!taker.IsAlive) { Note("the take thread ended"); cut = true; break; }
                int fc = Time.frameCount;
                double game = Time.timeDbl;
                if (!fg.Wants(fc, game)) continue;
                int capNow = Time.captureFramerate;
                if (capNow != rateAtStart)
                {
                    // a capture rate that gives another file rate than the one the file has (idle frames at rate 0 are stills)
                    rateAtStart = capNow;
                    if (warning == null && !byTime && fpsAsked <= 0 && capNow > 0 && Math.Abs(capNow / (double)every - fps) > 0.01)
                        warning = "the game now runs at capture rate " + Fmt.I(capNow) + " (" + Fmt.F(capNow / (double)every) + " recorded frames per game second) but the file has " + Fmt.F(fps) + " fps: its speed is off from there (start grec when the clock mode is set, or pass fps=)";
                    else if (warning == null && !byTime && capNow == 0)
                        warning = "the capture rate was removed: the file still takes one frame per grab at " + Fmt.F(fps) + " fps while the game now runs in real time, so its speed is off from there (start grec after the capture rate is set back, or without one for frames placed by game time)";
                }
                rb_stat_get(ref st);
                Watch(ref st);
                if (pluginVersion >= PresentFallbackVersion) { try { presentGrabs = rb_info(4) - presentGrabs0; presentDrops = rb_info(5) - presentDrops0; swapSwitches = rb_info(6) - swapSwitches0; } catch (Exception) { } }
                var stamp = new GrecStamp { frame = fc, tag = -1, game = game, real = Time.realtimeSinceStartupDbl };
                bool send = sessionOpen && led.Outstanding < GrecCore.MaxInFlight;
                if (send) stamp.tag = (session << 8) | (sent++ & 0xFF);
                lock (gate) issued++;
                led.Issue(stamp);
                if (send) Emit(Event | (OpGrab << 12) | stamp.tag);
            }
            clock.Stop();
            if (pipeDead || ffExited)
            {
                if (ffExited) Note("ffmpeg stopped: " + Tail());
                else Note("the writer stopped");
                phase = "failed";
                DevLog.Write("[play] grec: " + error);
            }
            else
            {
                phase = "draining";
                float until = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < until && !pipeDead && sessionOpen)
                {
                    if (led.Count == 0) break;
                    rb_stat_get(ref st);
                    if (st.queueDepth >= GrecCore.StallEvents) break;
                    Emit(Event | (OpFlush << 12) | (session << 8));
                    yield return null;
                }
            }
            Close();
        }

        // main thread, once at grec start (the mode and the rate hold for the whole file). Count mode while a capture rate
        // makes every rendered frame a fixed step of game time and the agent's stepper is not involved: the capture rate /
        // every (fps= overrides the number). Time mode otherwise, frames placed by game time (Write): real-time play (the
        // frame rate wanders; nominal fps= or 60 / every) and the stepper touched by an agent or ctl (60 / every: its runs
        // set captureFramerate 60 only while they run, the game is frozen between calls, and it may go back to real time).
        static double RateFor(int ev, double asked, out string from, out bool timeMode)
        {
            int cap = Time.captureFramerate;
            bool stepping = false;
            try { stepping = Stepper.Clock.Touched || Stepper.Running; } catch { }
            timeMode = cap <= 0 || stepping;
            if (asked > 0) { from = timeMode ? "fps=, frames placed by game time" : "fps="; return asked; }
            if (stepping) { from = "stepper clock " + Fmt.I(StepRate) + " / " + Fmt.I(ev) + ", frames placed by game time (the frozen time between agent calls is left out)"; return StepRate / (double)ev; }
            if (cap > 0) { from = "capture rate " + Fmt.I(cap) + " / " + Fmt.I(ev); return cap / (double)ev; }
            from = "nominal 60 / " + Fmt.I(ev) + ", frames placed by game time (real-time play)";
            return 60.0 / ev;
        }

        static RenderTexture NewMarker()
        {
            var rt = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave };
            rt.Create();
            return rt;
        }

        // main thread, every grab frame, after rb_stat_get (which already hooks a rewritten OMSetRenderTargets slot again).
        // Events piling up means the render thread no longer sees the markers: close the session and open a new one, which
        // hooks the context's current dispatch table; the grabs in flight are lost. While the plugin refuses (a frame is
        // still held) every grab frame tries again and the moments in between are counted lost.
        static void Watch(ref Stat st)
        {
            if (sessionOpen)
            {
                if (st.queueDepth < GrecCore.StallEvents) return;
                float now = Time.realtimeSinceStartup;
                if (now < reopenAt) return;
                reopenAt = now + 1f;
                stallNote = "the plugin stopped running its events: queue " + Fmt.I(st.queueDepth) + ", hooked " + Fmt.I(st.hooked) + ", OMSetRenderTargets calls " + Fmt.I(st.hookCalls) + ", marker hits " + Fmt.I(st.markerHits);
                DevLog.Write("[play] grec: " + stallNote + "; opening a new readback session");
                sessionOpen = false;
                try { rb_close(); } catch (Exception) { }
            }
            int s = 0;
            try { s = rb_open(Stages, Lag, CpuFrames); } catch (Exception) { }
            if (s == 0) { lock (gate) reopenFails++; return; }
            session = s;
            ledger.KillAll();
            lock (gate) reopened++;
            sessionOpen = true;
        }

        static void Emit(int ev)
        {
            if (rb_push(ev) == 0) pushRefused++;
            emitted++;
            var prev = RenderTexture.active;
            Graphics.Blit(Texture2D.blackTexture, (emitted & 1) == 0 ? marker : marker2);
            RenderTexture.active = prev;
        }

        static void StartFfmpeg()
        {
            string crop = (w & 1) != 0 || (h & 1) != 0 ? "-vf crop=trunc(iw/2)*2:trunc(ih/2)*2 " : "";
            var psi = new ProcessStartInfo(exe,
                "-hide_banner -loglevel error -y -f rawvideo -pix_fmt " + pixFmt + " -s " + Fmt.I(w) + "x" + Fmt.I(h) + " -framerate " + Fmt.F(fps, "0.######") + " -i - -an " + crop +
                "-c:v " + encoder + " " + FilmEnc.Args(encoder, cq) + " -g 60 " + Ffmpeg.Quote(file))
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var p = Process.Start(psi);
            ChildJob.Add(p);
            ffmpeg = p;
            stdin = p.StandardInput.BaseStream;
            var fs = stdin as FileStream;
            stdinHandle = IntPtr.Zero;
            try { if (fs != null) stdinHandle = fs.Handle; } catch { stdinHandle = IntPtr.Zero; }
            new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = p.StandardError.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        lock (errTail) { errTail.Append(line).Append(" | "); if (errTail.Length > 1500) errTail.Remove(0, errTail.Length - 1500); }
                        DevLog.Write("[play] grec ffmpeg: " + line);
                    }
                }
                catch { }
                try { if (p.WaitForExit(5000)) ffExit = p.ExitCode; } catch { }
                ffExited = true;
            }) { IsBackground = true, Name = "grec ffmpeg" }.Start();
        }

        static string Tail()
        {
            lock (errTail) return errTail.Length == 0 ? "no message from ffmpeg" + (ffExit != int.MinValue ? " (exit code " + Fmt.I(ffExit) + ")" : "") : errTail.ToString().TrimEnd(' ', '|');
        }

        // take thread
        static IntPtr Rent()
        {
            lock (gate)
            {
                if (pool.Count > 0) return pool.Pop();
                if (buffersMade >= queueCap + 2) return IntPtr.Zero;
                buffersMade++;
            }
            try { return Marshal.AllocHGlobal(new IntPtr(frameBytes)); }
            catch (OutOfMemoryException) { lock (gate) buffersMade--; return IntPtr.Zero; }
        }

        static void Return(IntPtr b)
        {
            if (b != IntPtr.Zero) lock (gate) pool.Push(b);
        }

        static void Enqueue(IntPtr b, GrecStamp s)
        {
            lock (gate) { queue.Enqueue(new Entry { b = b, has = b != IntPtr.Zero, s = s }); queuedNow++; Monitor.Pulse(gate); }
        }

        // take thread and close thread: a grab whose picture never came (never sent, its event or frame lost in the
        // plugin, its session closed)
        static void Lost(GrecStamp s)
        {
            lock (gate) dropLost++;
            Enqueue(IntPtr.Zero, s);
        }

        static void Take()
        {
            var f = new Frame();
            var led = ledger;
            var lost = new List<GrecStamp>();
            while (true)
            {
                bool last = takeStop;
                GrecStamp ls;
                while (led.NextLost(out ls)) Lost(ls);
                int got;
                try { got = rb_take(last ? 0 : 50, ref f); }
                catch (Exception e) { Note("rb_take: " + e.Message); return; }
                if (got == 0) { if (last) return; continue; }
                GrecStamp s;
                lost.Clear();
                if (!led.Match(f.tag & 0xFFF, lost, out s))
                {
                    if (f.slot >= 0) rb_release(f.slot);
                    lock (gate) stray++;
                    continue;
                }
                for (int i = 0; i < lost.Count; i++) Lost(lost[i]);
                s.present = f.reserved == 1;
                if (f.error != 0 || f.data == IntPtr.Zero)
                {
                    if (f.slot >= 0) rb_release(f.slot);
                    lock (gate) dropReadback++;
                    Enqueue(IntPtr.Zero, s);
                    continue;
                }
                if (f.width != w || f.height != h || f.bytes != frameBytes)
                {
                    rb_release(f.slot);
                    Note("the back buffer changed from " + Fmt.I(w) + "x" + Fmt.I(h) + " to " + Fmt.I(f.width) + "x" + Fmt.I(f.height) + ": recording stopped (start grec again for the new size)");
                    lock (gate) dropReadback++;
                    Enqueue(IntPtr.Zero, s);
                    cut = true;
                    stopAsked = true;
                    continue;
                }
                bool room;
                lock (gate) room = queuedNow < queueCap;
                IntPtr b = room && !pipeDead ? Rent() : IntPtr.Zero;
                if (b != IntPtr.Zero) RtlMoveMemory(b, f.data, new UIntPtr((ulong)frameBytes));
                rb_release(f.slot);
                if (b == IntPtr.Zero) lock (gate) dropLate++;
                Enqueue(b, s);
            }
        }

        // write thread: GrecPlacer decides the file frames (count mode: one per grab; time mode: by game time, see
        // PfmXGrecCore.cs), this sends them to ffmpeg and writes the times rows
        sealed class Sink : IGrecSink<IntPtr>
        {
            public StreamWriter times;
            byte[] managed;

            public bool Frame(IntPtr picture, GrecStamp shown, int index, bool repeated, double slotGame)
            {
                if (pipeDead || picture == IntPtr.Zero || !Send(picture, ref managed)) return false;
                if (times != null)
                {
                    try { times.Write(Fmt.I(index) + "," + Fmt.I(shown.frame) + "," + Fmt.F(shown.game, "0.######") + "," + Fmt.F(shown.real, "0.######") + "," + (repeated ? "1" : "0") + "," + Fmt.F(index / fps, "0.######") + "," + Fmt.F(slotGame, "0.######") + "," + (shown.present ? "1" : "0") + "\n"); }
                    catch (Exception e)
                    {
                        try { times.Close(); } catch { }
                        times = null;
                        warning = (warning != null ? warning + "; " : "") + "times file stopped at frame " + Fmt.I(index) + ": " + e.Message;
                    }
                }
                return true;
            }

            public void Recycle(IntPtr picture) { Return(picture); }

            public IntPtr Blank()
            {
                IntPtr b;
                try { b = Marshal.AllocHGlobal(new IntPtr(frameBytes)); }
                catch (OutOfMemoryException) { return IntPtr.Zero; }
                RtlZeroMemory(b, new UIntPtr((ulong)frameBytes));
                return b;
            }
        }

        static void Write()
        {
            var sink = new Sink();
            if (timesOn)
            {
                try { sink.times = new StreamWriter(file + ".times.csv", false, new UTF8Encoding(false)); sink.times.Write("index,frame,game_time,real_time,repeated,video_time,slot_game_time,at_present\n"); }
                catch (Exception e) { sink.times = null; warning = (warning != null ? warning + "; " : "") + "times file: " + e.Message; }
            }
            var pl = new GrecPlacer<IntPtr>(fps, !byTime, sink);
            placer = pl;
            try
            {
                while (true)
                {
                    Entry e;
                    bool have = false;
                    lock (gate)
                    {
                        if (queue.Count == 0 && !writeStop) Monitor.Wait(gate, 100);
                        if (queue.Count > 0) { e = queue.Dequeue(); have = true; }
                        else { e = new Entry(); if (writeStop) break; }
                    }
                    if (!have) continue;
                    bool ok = pl.Add(e.b, e.has, e.s);
                    lock (gate) queuedNow--;
                    if (!ok && !pipeDead) PipeLost();
                }
                if (!pipeDead && !pl.Finish() && !pipeDead) PipeLost();
            }
            catch (Exception e) { pipeDead = true; Note("write: " + e.GetType().Name + ": " + e.Message); }
            finally
            {
                if (pl.HasPicture) Return(pl.Picture);
                if (sink.times != null) { try { sink.times.Close(); } catch { } }
            }
        }

        static void PipeLost()
        {
            pipeDead = true;
            for (int i = 0; i < 50 && !ffExited; i++) Thread.Sleep(10);
            Note("ffmpeg stopped: " + Tail());
        }

        static bool Send(IntPtr b, ref byte[] managed)
        {
            try
            {
                if (stdinHandle != IntPtr.Zero)
                {
                    long left = frameBytes, at = 0;
                    while (left > 0)
                    {
                        uint n;
                        if (!WriteFile(stdinHandle, new IntPtr(b.ToInt64() + at), (uint)Math.Min(left, 1 << 24), out n, IntPtr.Zero) || n == 0) return false;
                        at += n; left -= n;
                    }
                    return true;
                }
                if (managed == null) managed = new byte[frameBytes];
                Marshal.Copy(b, managed, 0, managed.Length);
                stdin.Write(managed, 0, managed.Length);
                return true;
            }
            catch { return false; }
        }

        // any thread: the first failure becomes error, later ones are kept (at most 4) for grec status
        static void Note(string why)
        {
            lock (laterErrors)
            {
                if (error == null) { error = why; return; }
                if (why == error || laterErrors.Contains(why) || laterErrors.Count >= 4) return;
                laterErrors.Add(why);
            }
        }

        static string Later()
        {
            lock (laterErrors) return laterErrors.Count == 0 ? "" : "; later: " + string.Join("; ", laterErrors.ToArray());
        }

        // main thread: runs Run so that an exception in it closes the recording instead of leaving the owner, the threads
        // and ffmpeg behind with running still true
        static IEnumerator Drive(IEnumerator inner)
        {
            while (true)
            {
                object cur;
                try
                {
                    if (!inner.MoveNext()) yield break;
                    cur = inner.Current;
                }
                catch (Exception e)
                {
                    Note("grec: " + e.GetType().Name + ": " + e.Message);
                    DevLog.Error("[play] grec", e);
                    phase = "failed";
                    if (!closing) Close();
                    yield break;
                }
                yield return cur;
            }
        }

        // close thread: Join / WaitForExit in slices, so a game quit that sets quick while a normal stop already waits
        // shortens the wait
        static bool Wait(Func<int, bool> step, int normalMs, int quickMs)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                if (step(100)) return true;
                if (sw.ElapsedMilliseconds >= (quick ? quickMs : normalMs)) return false;
            }
        }

        // main thread: a start that never reached the frame loop
        static void Abort(string why)
        {
            Note(why);
            phase = "failed";
            DevLog.Write("[play] grec: " + why);
            Close();
        }

        // Main thread, never waits: stops the grabs and the plugin session, frees the Unity objects and leaves the rest
        // (frames still in flight, the writer, ffmpeg's end, the owner) to the close thread.
        static void Close()
        {
            if (closing) return;
            closing = true;
            clock.Stop();
            try { rb_close(); } catch (Exception) { }
            takeStop = true;
            try { rb_set_marker(IntPtr.Zero, IntPtr.Zero); } catch (Exception) { }
            if (marker != null) { marker.Release(); UnityEngine.Object.Destroy(marker); marker = null; }
            if (marker2 != null) { marker2.Release(); UnityEngine.Object.Destroy(marker2); marker2 = null; }
            bool failed = phase == "failed";
            if (!failed) phase = "closing";
            try { Restorer.Forget(RestoreKey); } catch (Exception) { }
            var t = new Thread(() => Finish(failed)) { IsBackground = true, Name = "grec close" };
            closer = t;
            t.Start();
        }

        static void Finish(bool failed)
        {
            var tk = taker; var wr = writer; var p = ffmpeg; var s = stdin;
            try
            {
                if (tk != null && !tk.Join(3000)) Note("the take thread did not stop");
                try { var f = new Frame(); while (rb_take(0, ref f) == 1) if (f.slot >= 0) rb_release(f.slot); } catch (Exception) { }
                try { rb_free_buffers(); } catch (Exception) { }
                ReleaseOwner();
                if (wr != null)
                {
                    var rest = new List<GrecStamp>();
                    ledger.TakeAll(rest);
                    if (!failed && !pipeDead) for (int i = 0; i < rest.Count; i++) Lost(rest[i]);
                    lock (gate) { writeStop = true; Monitor.PulseAll(gate); }
                    if (!Wait(ms => wr.Join(ms), 60000, 1500))
                    {
                        pipeDead = true;
                        Note("the encoder did not take the last frames in time");
                        try { if (p != null && !p.HasExited) p.Kill(); } catch (Exception) { }
                        wr.Join(2000);
                    }
                }
                try { if (s != null) s.Close(); } catch (Exception) { }
                bool ended = true;
                if (p != null)
                {
                    try { ended = Wait(ms => p.WaitForExit(ms), 30000, 2500); } catch (Exception) { }
                    if (!ended) { try { p.Kill(); } catch (Exception) { } }
                    try { if (p.HasExited) ffExit = p.ExitCode; } catch (Exception) { }
                }
                if (p != null)
                {
                    int d = FilmFiles.MkvDuration(file);
                    if (!ended) { finish = "not finished: ffmpeg did not end and was stopped (remux: ffmpeg -i <file> -c copy <new file>)"; failed = true; Note("ffmpeg did not end after its input closed"); }
                    else if (ffExit != 0) { failed = true; Note("ffmpeg ended with code " + Fmt.I(ffExit) + ": " + Tail()); }
                    else if (d == 0) finish = "file closed without a duration (remux: ffmpeg -i <file> -c copy <new file>)";
                    else finish = "file complete" + (d == 1 ? " with duration" : "") + ", " + Fmt.F(FilmFiles.Mb(file), "0.0") + " MB";
                    if (cut) failed = true;
                }
            }
            catch (Exception e) { failed = true; Note("close: " + e.Message); }
            finally
            {
                ReleaseOwner();
                FreeBuffers();
                stdin = null; stdinHandle = IntPtr.Zero; ffmpeg = null; taker = null; writer = null;
                phase = failed ? "failed" : "done";
                running = false;
                DevLog.Write("[play] " + Status());
            }
        }

        static void FreeBuffers()
        {
            lock (gate)
            {
                while (queue.Count > 0) { var e = queue.Dequeue(); if (e.b != IntPtr.Zero) pool.Push(e.b); }
                queuedNow = 0;
                while (pool.Count > 0) { try { Marshal.FreeHGlobal(pool.Pop()); } catch { } }
                buffersMade = 0;
            }
        }

        // main thread, quick: for the doctor
        internal static string PluginState()
        {
            try { return "version " + Fmt.I(rb_version()); }
            catch (Exception e) { return "not loaded: " + e.Message; }
        }
    }
}
