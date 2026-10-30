using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // GameRec.cs (owner TIME): video of the game's own picture, independent of the desktop: windows over the game, focus and
    // other programs never reach the file, unlike desktop duplication (the real-time recorder). At the end of every
    // n-th frame the back buffer is copied on the GPU by the native InsideReadback plugin (INSIDE_Data/Plugins, the same path
    // as MultiCam's replay pipes: an event queued with rb_push and a blit into a 1x1 marker render texture that the plugin's
    // OMSetRenderTargets hook sees on the render thread), mapped a few frames later and handed by a pump thread to ffmpeg's
    // stdin as raw frames, encoded with hevc_nvenc. The coroutine runs on the core's DevRoot. Commands: grec start <file>
    // [every] [cq], grec stop, grec status. Restore and a game quit stop it.
    public static class GameRec
    {
        public const string RestoreKey = "grec";
        const int Event = 0x52420000, OpGrab = 1, OpFlush = 2, OpProbe = 3, Stages = 4, Lag = 2, CpuFrames = 6;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

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

        [DllImport("InsideReadback")] static extern int rb_version();
        [DllImport("InsideReadback")] static extern int rb_open(int stages, int lag, int cpu);
        [DllImport("InsideReadback")] static extern void rb_close();
        [DllImport("InsideReadback")] static extern int rb_take(int timeoutMs, ref Frame f);
        [DllImport("InsideReadback")] static extern void rb_release(int slot);
        [DllImport("InsideReadback")] static extern int rb_stat_get(ref Stat s);
        [DllImport("InsideReadback")] static extern int rb_push(int eventID);
        [DllImport("InsideReadback")] static extern void rb_set_marker(IntPtr tex, IntPtr tex2);

        static readonly object gate = new object();
        static readonly WaitForEndOfFrame eof = new WaitForEndOfFrame();
        static RenderTexture marker, marker2;
        static Process ffmpeg;
        static Stream stdin;
        static Thread pump;
        static volatile bool stopping, stopAsked, running;
        static volatile string error;
        static string file = "", phase = "idle", pixFmt = "";
        static int session, issued, delivered, dropped, emitted, every = 2, cq = 16, w, h;
        static readonly Stopwatch clock = new Stopwatch();


        public static string Command(List<string> a)
        {
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            if (sub == "start")
            {
                if (running) return "grec: already running into " + file;
                if (a.Count < 3) return "grec start <file.mkv> [every n-th frame, default 2] [cq, default 16]";
                file = a[2].Replace('\\', '/');
                every = a.Count > 3 ? Math.Max(1, ParseInt(a[3], 2)) : 2;
                cq = a.Count > 4 ? ParseInt(a[4], 16) : 16;
                var root = UnityEngine.Object.FindObjectOfType<DevRoot>();
                if (root == null) return "grec: no DevRoot to run on";
                error = null;
                stopAsked = false;
                running = true;
                phase = "opening";
                root.StartCoroutine(Run());
                Restorer.Register(RestoreKey, "game video (grec)", () => { stopAsked = true; return "grec stopping"; });
                return "grec: opening the readback, then recording every " + every + " frame(s) into " + file;
            }
            if (sub == "stop") { if (!running) return "grec: not running"; stopAsked = true; return "grec: stopping (grec status shows when the file is closed)"; }
            return Status();
        }

        static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, IC, out v) ? v : fallback;
        }

        static string Status()
        {
            lock (gate)
                return "grec: " + phase + (file.Length > 0 ? ", " + file : "") + ", " + issued + " grab(s), " + delivered + " written, " + dropped + " dropped" +
                       (w > 0 ? ", " + w + "x" + h + " " + pixFmt : "") + (clock.IsRunning ? ", " + clock.Elapsed.TotalSeconds.ToString("0.0", IC) + " s" : "") + (error != null ? ", error: " + error : "");
        }

        static IEnumerator Run()
        {
            string why = null;
            try { if (rb_version() < 1) why = "InsideReadback too old"; }
            catch (Exception e) { why = "InsideReadback: " + e.Message; }
            if (why == null)
            {
                session = rb_open(Stages, Lag, CpuFrames);
                if (session == 0) why = "InsideReadback busy (a MultiCam run holds it)";
            }
            if (why != null) { Fail(why); yield break; }
            marker = NewMarker();
            marker2 = NewMarker();
            rb_set_marker(marker.GetNativeTexturePtr(), marker2.GetNativeTexturePtr());
            var st = new Stat();
            for (int i = 0; i < 120; i++)
            {
                yield return eof;
                Emit(Event | (OpProbe << 12));
                yield return null;
                rb_stat_get(ref st);
                if (st.source == 1 && st.width > 0) break;
            }
            if (st.source != 1 || st.width <= 0) { Fail("the plugin did not find the swap chain (device " + st.hasDevice + ", source " + st.source + ")"); Close(); yield break; }
            pixFmt = st.dxgiFormat == 28 || st.dxgiFormat == 29 ? "rgba" : st.dxgiFormat == 87 || st.dxgiFormat == 91 ? "bgra" : st.dxgiFormat == 88 || st.dxgiFormat == 93 ? "bgr0" : "";
            if (pixFmt.Length == 0) { Fail("unsupported back buffer format " + st.dxgiFormat); Close(); yield break; }
            w = st.width;
            h = st.height;
            try { StartFfmpeg(); }
            catch (Exception e) { Fail("ffmpeg: " + e.Message); Close(); yield break; }
            stopping = false;
            pump = new Thread(Pump) { IsBackground = true, Name = "grec pump" };
            pump.Start();
            lock (gate) { issued = delivered = dropped = 0; phase = "recording"; }
            clock.Reset();
            clock.Start();
            DevLog.Write("[play] grec: recording " + w + "x" + h + " " + pixFmt + " every " + every + " frame(s) into " + file);
            while (!stopAsked)
            {
                yield return eof;
                if (Time.frameCount % every != 0) continue;
                lock (gate) issued++;
                Emit(Event | (OpGrab << 12) | ((session << 8) | (issued & 0xFF)));
            }
            phase = "draining";
            float until = Time.realtimeSinceStartup + 8f;
            while (Time.realtimeSinceStartup < until)
            {
                int d;
                lock (gate) d = delivered + dropped;
                if (d >= issued) break;
                Emit(Event | (OpFlush << 12) | (session << 8));
                yield return null;
            }
            clock.Stop();
            Close();
        }

        static RenderTexture NewMarker()
        {
            var rt = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave };
            rt.Create();
            return rt;
        }

        static void Emit(int ev)
        {
            if (rb_push(ev) == 0 && error == null) error = "readback event queue full";
            emitted++;
            var prev = RenderTexture.active;
            Graphics.Blit(Texture2D.blackTexture, (emitted & 1) == 0 ? marker : marker2);
            RenderTexture.active = prev;
        }

        static void StartFfmpeg()
        {
            string exe = Recorder.FindFfmpeg(null);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            string fps = (60.0 / every).ToString("0.###", IC);
            var psi = new ProcessStartInfo(exe,
                "-hide_banner -loglevel error -y -f rawvideo -pix_fmt " + pixFmt + " -s " + w + "x" + h + " -framerate " + fps + " -i - -an " +
                "-c:v hevc_nvenc -preset p6 -tune hq -rc vbr -cq " + cq + " -b:v 0 -g 60 \"" + file + "\"")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            ffmpeg = Process.Start(psi);
            ffmpeg.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) DevLog.Write("[play] grec ffmpeg: " + e.Data); };
            ffmpeg.BeginErrorReadLine();
            stdin = ffmpeg.StandardInput.BaseStream;
        }

        static void Pump()
        {
            var f = new Frame();
            byte[] buf = null;
            while (true)
            {
                int done;
                lock (gate) done = delivered + dropped;
                if (stopping && done >= issued) return;
                int got;
                try { got = rb_take(50, ref f); }
                catch (Exception e) { if (error == null) error = "rb_take: " + e.Message; return; }
                if (got == 0) { if (stopping) return; continue; }
                if (f.error != 0 || f.data == IntPtr.Zero)
                {
                    if (f.slot >= 0) rb_release(f.slot);
                    lock (gate) dropped++;
                    continue;
                }
                try
                {
                    int row = w * 4;
                    if (buf == null || buf.Length != row * h) buf = new byte[row * h];
                    if (f.pitch == row || f.pitch <= 0) Marshal.Copy(f.data, buf, 0, Math.Min(buf.Length, (int)f.bytes));
                    else for (int y = 0; y < h; y++) Marshal.Copy(new IntPtr(f.data.ToInt64() + (long)y * f.pitch), buf, y * row, row);
                    rb_release(f.slot);
                    stdin.Write(buf, 0, buf.Length);
                    lock (gate) delivered++;
                }
                catch (Exception e)
                {
                    if (error == null) error = "write: " + e.Message;
                    lock (gate) dropped++;
                }
            }
        }

        static void Fail(string why)
        {
            error = why;
            phase = "failed";
            running = false;
            DevLog.Write("[play] grec: " + why);
        }

        static void Close()
        {
            stopping = true;
            if (pump != null && !pump.Join(10000) && error == null) error = "pump did not stop";
            pump = null;
            try { rb_close(); } catch (Exception) { }
            try { var f = new Frame(); while (rb_take(0, ref f) == 1) if (f.slot >= 0) rb_release(f.slot); } catch (Exception) { }
            try { rb_set_marker(IntPtr.Zero, IntPtr.Zero); } catch (Exception) { }
            if (marker != null) { marker.Release(); UnityEngine.Object.Destroy(marker); marker = null; }
            if (marker2 != null) { marker2.Release(); UnityEngine.Object.Destroy(marker2); marker2 = null; }
            var s = stdin;
            var p = ffmpeg;
            stdin = null;
            ffmpeg = null;
            phase = phase == "failed" ? "failed" : "closing";
            new Thread(() =>
            {
                try { if (s != null) s.Close(); } catch (Exception) { }
                try { if (p != null && !p.WaitForExit(60000)) p.Kill(); } catch (Exception) { }
                if (phase != "failed") phase = "done";
                running = false;
                DevLog.Write("[play] grec: " + Status());
            }) { IsBackground = true, Name = "grec close" }.Start();
            try { Restorer.Forget(RestoreKey); } catch (Exception) { }
        }
    }
}
