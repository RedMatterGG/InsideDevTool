using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace InsideDev
{
    // Asynchronous frame readback through the native InsideReadback plugin (src/readback/inside_readback.c, installed as
    // INSIDE_Data/Plugins/InsideReadback.dll). At the end of the frame the game thread queues the event (rb_push) and blits
    // into one of two 1x1 marker render textures; INSIDE's engine never forwards GL.IssuePluginEvent to plugins, and the
    // D3D11 runtime rewrites the Clear/Update slots of the context's dispatch table, so the plugin runs the queued event from
    // its OMSetRenderTargets hook when Unity binds a marker on the render thread (in command order). The render thread copies the back buffer on the GPU and maps the copy `lag` grabs later; a pump thread hands the CPU
    // frames, in grab order, to the frame writers (ffmpeg pipes) which free them after writing. Frames are top row first
    // (no vflip) in the back buffer's byte order (rgba or bgra). output.readback "auto" falls back to MultiCamGrab
    // (ReadPixels on the game thread) when the plugin is missing or does not find the swap chain.
    internal static class ReadbackNative
    {
        public const int Event = 0x52420000, OpGrab = 1, OpFlush = 2, OpProbe = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct Stat
        {
            public int version, deviceType, hasDevice, renderThread, source, width, height, dxgiFormat, samples;
            public int stageCount, lag, cpuCount, pending, lastError, session, ignored;
            public long grabs, flushes, probes, frames, errors, dropped, produced, taken, released;
            public double mapWaitMs, mapWaitMaxMs, slotWaitMs, slotWaitMaxMs, copyMs, eventMs, eventMaxMs;
            public long calls, deviceCalls, markerHits, queued, hookCalls;
            public int lastEvent, lastDeviceEvent, hooked, queueDepth;

            public string Text()
            {
                return "device type " + deviceType + " (2 = D3D11), device " + hasDevice + ", device calls " + deviceCalls + " (last event " + lastDeviceEvent + "), render events " + calls + " (last 0x" + lastEvent.ToString("X8") + "), probes " + probes + ", grabs " + grabs + ", flushes " + flushes + ", frames " + frames + ", produced " + produced + ", taken " + taken + ", released " + released + ", dropped " + dropped + ", pending " + pending + ", source " + source + ", " + width + "x" + height + " DXGI " + dxgiFormat + ", render thread " + renderThread + ", errors " + errors + " (last: " + ErrorText(lastError) + "), session " + session + ", ignored " + ignored + ", hooks " + hooked + " (OMSetRenderTargets checks " + hookCalls + "), marker hits " + markerHits + ", queued " + queued + " (depth " + queueDepth + ")";
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Frame
        {
            public long seq, bytes;
            public IntPtr data;
            public int slot, tag, width, height, pitch, dxgiFormat, error, reserved;
        }

        [DllImport("InsideReadback")] public static extern int rb_version();
        [DllImport("InsideReadback")] public static extern int rb_open(int stages, int lag, int cpu);
        [DllImport("InsideReadback")] public static extern void rb_close();
        [DllImport("InsideReadback")] public static extern void rb_free_buffers();
        [DllImport("InsideReadback")] public static extern int rb_take(int timeoutMs, ref Frame f);
        [DllImport("InsideReadback")] public static extern void rb_release(int slot);
        [DllImport("InsideReadback")] public static extern int rb_stat_get(ref Stat s);
        [DllImport("InsideReadback")] public static extern int rb_push(int eventID);
        [DllImport("InsideReadback")] public static extern void rb_set_marker(IntPtr tex, IntPtr tex2);

        static readonly string[] Errors = { "none", "no D3D11 device", "no render target bound", "bound target is not the swap chain", "unsupported back buffer format", "staging texture creation failed", "Map failed", "no free CPU frame (writer too slow)", "out of memory", "closed", "Map timed out (GPU copy not finished in 2 s)" };

        public static string ErrorText(int code) { return code >= 0 && code < Errors.Length ? Errors[code] : "error " + code; }

        public static string PixFmt(int dxgi)
        {
            if (dxgi == 28 || dxgi == 29) return "rgba";
            if (dxgi == 87 || dxgi == 91) return "bgra";
            if (dxgi == 88 || dxgi == 93) return "bgr0";
            return null;
        }

        public static Stat Get() { var s = new Stat(); rb_stat_get(ref s); return s; }

        public static string Unavailable()
        {
            try { int v = rb_version(); return v >= 1 ? null : "InsideReadback version " + v; }
            catch (DllNotFoundException) { return "INSIDE_Data/Plugins/InsideReadback.dll is not installed"; }
            catch (EntryPointNotFoundException e) { return "InsideReadback: " + e.Message; }
            catch (Exception e) { return "InsideReadback: " + e.GetType().Name + ": " + e.Message; }
        }
    }

    internal sealed class MultiCamAsyncGrab
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public const int Stages = 4, Lag = 2, CpuFrames = 6;

        sealed class Target { public MultiCamFrameWriter w; public string path; public int tag; public byte[] capture; public bool discard; public bool done; }

        readonly Queue<Target> fifo = new Queue<Target>();
        readonly object gate = new object();
        readonly WaitForEndOfFrame eof = new WaitForEndOfFrame();
        Thread pump;
        RenderTexture marker, marker2;
        int emitted;
        volatile bool stopping;
        int session, issued, delivered;
        public volatile string error;
        public string pixFmt;
        public int w, h, dxgiFormat;
        public bool ready;
        public double issueMs;
        public int dropped;

        public int Issued { get { return issued; } }
        public int Delivered { get { lock (gate) return delivered; } }

        public IEnumerator Open(int expectW, int expectH)
        {
            ready = false;
            string why = ReadbackNative.Unavailable();
            if (why != null) { error = why; yield break; }
            session = ReadbackNative.rb_open(Stages, Lag, CpuFrames);
            if (session == 0) { error = "InsideReadback busy: CPU frames are still held by a writer"; yield break; }
            marker = NewMarker();
            marker2 = NewMarker();
            IntPtr mp = marker.GetNativeTexturePtr(), mp2 = marker2.GetNativeTexturePtr();
            if (mp == IntPtr.Zero || mp2 == IntPtr.Zero) { error = "marker render texture has no native texture"; Close(); yield break; }
            ReadbackNative.rb_set_marker(mp, mp2);
            var st = new ReadbackNative.Stat();
            for (int i = 0; i < 120; i++)
            {
                yield return eof;
                Emit(ReadbackNative.Event | (ReadbackNative.OpProbe << 12));
                yield return null;
                st = ReadbackNative.Get();
                if (st.source == 1 && st.width > 0) break;
            }
            if (st.hasDevice == 0) { error = "InsideReadback has no D3D11 device: " + st.Text(); Close(); yield break; }
            if (st.source != 1) { error = "InsideReadback did not find the swap chain: " + st.Text(); Close(); yield break; }
            if (st.width != expectW || st.height != expectH) { error = "back buffer " + st.width + "x" + st.height + " differs from the screen " + expectW + "x" + expectH; Close(); yield break; }
            if (st.samples > 1) { error = "multisampled back buffer (" + st.samples + "x) not supported"; Close(); yield break; }
            pixFmt = ReadbackNative.PixFmt(st.dxgiFormat);
            if (pixFmt == null) { error = "unsupported back buffer format " + st.dxgiFormat; Close(); yield break; }
            w = st.width; h = st.height; dxgiFormat = st.dxgiFormat;
            stopping = false;
            pump = new Thread(Pump);
            pump.IsBackground = true;
            pump.Name = "mcam readback pump";
            pump.Start();
            ready = true;
        }

        public void Grab(MultiCamFrameWriter writer, string path) { Issue(new Target { w = writer, path = path }); }

        void Issue(Target t)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            t.tag = (session << 8) | (issued & 0xFF);
            lock (gate) fifo.Enqueue(t);
            issued++;
            Emit(ReadbackNative.Event | (ReadbackNative.OpGrab << 12) | t.tag);
            issueMs += sw.Elapsed.TotalMilliseconds;
        }

        static RenderTexture NewMarker()
        {
            var rt = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32);
            rt.hideFlags = HideFlags.HideAndDontSave;
            rt.Create();
            return rt;
        }

        void Emit(int ev)
        {
            if (ReadbackNative.rb_push(ev) == 0 && error == null) error = "readback event queue full";
            emitted++;
            var prev = RenderTexture.active;
            Graphics.Blit(Texture2D.blackTexture, (emitted & 1) == 0 ? marker : marker2);
            RenderTexture.active = prev;
        }

        void Flush() { Emit(ReadbackNative.Event | (ReadbackNative.OpFlush << 12) | (session << 8)); }

        public IEnumerator Drain(float timeout)
        {
            if (!ready) yield break;
            float until = Time.realtimeSinceStartup + timeout;
            while (Delivered < issued && error == null && Time.realtimeSinceStartup < until)
            {
                Flush();
                yield return null;
            }
            if (Delivered < issued && error == null) error = "readback drain timed out: " + Delivered + "/" + issued + " frame(s) delivered";
        }

        void Pump()
        {
            var f = new ReadbackNative.Frame();
            while (true)
            {
                if (stopping && Delivered >= issued) return;
                int got;
                try { got = ReadbackNative.rb_take(50, ref f); }
                catch (Exception e) { if (error == null) error = "rb_take: " + e.Message; return; }
                if (got == 0) { if (stopping) return; continue; }
                Target t = null;
                lock (gate) { if (fifo.Count > 0) t = fifo.Dequeue(); }
                if (t == null)
                {
                    if (f.slot >= 0) ReadbackNative.rb_release(f.slot);
                    if (error == null) error = "readback frame " + f.seq + " without a grab";
                    continue;
                }
                if ((f.tag & 0xFFF) != t.tag && error == null) error = "readback order: frame tag " + (f.tag & 0xFFF) + ", expected " + t.tag;
                if (f.error != 0 || f.data == IntPtr.Zero)
                {
                    if (f.slot >= 0) ReadbackNative.rb_release(f.slot);
                    Interlocked.Increment(ref dropped);
                    if (error == null) error = "readback frame " + f.seq + ": " + ReadbackNative.ErrorText(f.error);
                }
                else if (t.capture != null || t.discard)
                {
                    if (t.capture != null) Marshal.Copy(f.data, t.capture, 0, (int)Math.Min(f.bytes, t.capture.Length));
                    ReadbackNative.rb_release(f.slot);
                }
                else t.w.AddNative(t.path, f.data, (int)f.bytes, f.slot);
                lock (gate) { t.done = true; delivered++; }
            }
        }

        public void Close()
        {
            stopping = true;
            try { ReadbackNative.rb_close(); } catch { }
            if (pump != null && !pump.Join(5000) && error == null) error = "readback pump did not stop";
            pump = null;
            try
            {
                var f = new ReadbackNative.Frame();
                while (ReadbackNative.rb_take(0, ref f) == 1) if (f.slot >= 0) ReadbackNative.rb_release(f.slot);
            }
            catch { }
            lock (gate) fifo.Clear();
            try { ReadbackNative.rb_set_marker(IntPtr.Zero, IntPtr.Zero); } catch { }
            if (marker != null) { marker.Release(); UnityEngine.Object.Destroy(marker); marker = null; }
            if (marker2 != null) { marker2.Release(); UnityEngine.Object.Destroy(marker2); marker2 = null; }
            ready = false;
        }

        public Json.Obj StatsJson()
        {
            var o = new Json.Obj();
            o["mode"] = "native";
            o["pix_fmt"] = pixFmt ?? "";
            o["dxgi_format"] = (double)dxgiFormat;
            o["issued"] = (double)issued;
            o["issue_ms_avg"] = MultiCamPlan.Num(issued > 0 ? issueMs / issued : double.NaN);
            o["error"] = error ?? "";
            try
            {
                var s = ReadbackNative.Get();
                double n = Math.Max(1, s.grabs);
                o["grabs"] = (double)s.grabs; o["frames"] = (double)s.frames; o["errors"] = (double)s.errors; o["dropped"] = (double)s.dropped;
                o["render_thread"] = (double)s.renderThread;
                o["map_wait_ms_avg"] = Math.Round(s.mapWaitMs / n, 3); o["map_wait_ms_max"] = Math.Round(s.mapWaitMaxMs, 3);
                o["slot_wait_ms_avg"] = Math.Round(s.slotWaitMs / n, 3); o["slot_wait_ms_max"] = Math.Round(s.slotWaitMaxMs, 3);
                o["copy_ms_avg"] = Math.Round(s.copyMs / n, 3);
                o["event_ms_avg"] = Math.Round(s.eventMs / n, 3); o["event_ms_max"] = Math.Round(s.eventMaxMs, 3);
                o["ignored_events"] = (double)s.ignored;
            }
            catch (Exception e) { o["stat_error"] = e.Message; }
            return o;
        }

        public string StatsLine()
        {
            var o = StatsJson();
            return "readback native " + pixFmt + ": " + o.Num("grabs").ToString("0", IC) + " grab(s), issue " + o.Num("issue_ms_avg").ToString("0.###", IC) + " ms, GPU wait avg " + o.Num("map_wait_ms_avg").ToString("0.###", IC) + " / max " + o.Num("map_wait_ms_max").ToString("0.##", IC) + " ms, copy " + o.Num("copy_ms_avg").ToString("0.##", IC) + " ms, writer wait max " + o.Num("slot_wait_ms_max").ToString("0.##", IC) + " ms, " + o.Num("dropped").ToString("0", IC) + " dropped" + (error != null ? ", error: " + error : "");
        }

        internal byte[] CaptureNext() { var b = new byte[w * h * 4]; Issue(new Target { capture = b }); return b; }

        internal void DiscardNext() { Issue(new Target { discard = true }); }
    }

    // mcam readback test: on the current scene, compares one frame grabbed by the plugin with ReadPixels of the same frame
    // (pixel exact expected) and measures the game-thread frame time over 120 frames each with no grab, the plugin,
    // ReadPixels + GetPixels32 (the old pipe path) and ReadPixels + EncodeToJPG (the old jpg path).
    internal sealed class MultiCamReadbackTest : MonoBehaviour
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public static string result = "not run";
        public static bool running;
        const int Frames = 120;
        int vsync0, target0;
        bool fcAuto0, paced;

        public static string Command(List<string> a)
        {
            string s = a.Count > 2 ? a[2].ToLowerInvariant() : "status";
            if (s == "status") return (running ? "running: " : "") + result;
            if (s == "stat") { string why = ReadbackNative.Unavailable(); return why ?? ReadbackNative.Get().Text(); }
            if (s != "test") return "usage: mcam readback test|status|stat";
            if (running) return "readback test already running";
            if (MultiCam.Running) return "mcam is capturing; run the readback test when it is idle";
            var go = new GameObject("InsideDev ReadbackTest");
            DontDestroyOnLoad(go);
            go.AddComponent<MultiCamReadbackTest>();
            running = true;
            result = "started";
            return "readback test started (about " + (Frames * 4 / 60 + 4) + " s); poll 'mcam readback status'";
        }

        void Start() { StartCoroutine(Run()); }

        IEnumerator Run()
        {
            var sb = new StringBuilder();
            var eof = new WaitForEndOfFrame();
            var grab = new MultiCamAsyncGrab();
            int sw = Screen.width, sh = Screen.height;
            yield return StartCoroutine(grab.Open(sw, sh));
            if (!grab.ready) { Finish("FAIL open: " + grab.error); yield break; }
            sb.Append("plugin: back buffer ").Append(grab.w).Append('x').Append(grab.h).Append(' ').Append(grab.pixFmt).Append(" (DXGI ").Append(grab.dxgiFormat).Append("), render thread ").Append(ReadbackNative.Get().renderThread).Append(", game thread ").Append(Thread.CurrentThread.ManagedThreadId).Append(" (managed id)\n");
            var tex = new Texture2D(sw, sh, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            int worst = -1; long diffPx = 0;
            for (int k = 0; k < 3; k++)
            {
                yield return eof;
                var prev = RenderTexture.active;
                RenderTexture.active = null;
                tex.ReadPixels(new Rect(0, 0, sw, sh), 0, 0, false);
                RenderTexture.active = prev;
                byte[] nat = grab.CaptureNext();
                yield return StartCoroutine(grab.Drain(5f));
                if (grab.error != null) break;
                Color32[] px = tex.GetPixels32();
                bool bgra = grab.pixFmt != "rgba";
                if (k == 0) Dump(px, nat, sw, sh, bgra);
                for (int y = 0; y < sh; y++)
                {
                    int ny = (sh - 1 - y) * sw * 4, uy = y * sw;
                    for (int x = 0; x < sw; x++)
                    {
                        var c = px[uy + x];
                        int o = ny + x * 4;
                        int r = nat[o + (bgra ? 2 : 0)], g = nat[o + 1], b = nat[o + (bgra ? 0 : 2)];
                        int d = Math.Max(Math.Abs(r - c.r), Math.Max(Math.Abs(g - c.g), Math.Abs(b - c.b)));
                        if (d > 0) diffPx++;
                        if (d > worst) worst = d;
                    }
                }
            }
            Destroy(tex);
            if (grab.error != null) { string stat = ReadbackNative.Get().Text(); grab.Close(); Finish(sb + "FAIL compare: " + grab.error + " | " + stat); yield break; }
            sb.Append("compare: 3 frame(s), max channel difference ").Append(worst).Append(", ").Append(diffPx).Append(" differing pixel(s) -> ").Append(worst == 0 ? "PASS (pixel exact)" : worst <= 2 ? "PASS (rounding)" : "FAIL").Append('\n');
            vsync0 = QualitySettings.vSyncCount; target0 = Application.targetFrameRate;
            try { fcAuto0 = FramerateController.automatic; FramerateController.automatic = false; FramerateController.SetVSyncCount(0); } catch { }
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            paced = true;
            sb.Append("timing with vsync off, no frame limit\n");
            var times = new double[4];
            string[] names = { "no grab", "plugin", "ReadPixels+GetPixels32", "ReadPixels+EncodeToJPG" };
            var raw = new MultiCamGrab(sw, sh, false, 95);
            var jpg = new MultiCamGrab(sw, sh, true, 95);
            for (int mode = 0; mode < 4; mode++)
            {
                var ft = new List<double>();
                double last = Time.realtimeSinceStartup;
                for (int i = 0; i < Frames + 10; i++)
                {
                    yield return eof;
                    if (mode == 1) grab.DiscardNext();
                    else if (mode == 2) raw.Grab();
                    else if (mode == 3) jpg.Grab();
                    double now = Time.realtimeSinceStartup;
                    if (i >= 10) ft.Add((now - last) * 1000.0);
                    last = now;
                }
                if (mode == 1) yield return StartCoroutine(grab.Drain(5f));
                ft.Sort();
                times[mode] = ft[ft.Count / 2];
                sb.Append(names[mode]).Append(": median frame ").Append(times[mode].ToString("0.0", IC)).Append(" ms (p90 ").Append(ft[ft.Count * 9 / 10].ToString("0.0", IC)).Append(" ms, ").Append((1000.0 / times[mode]).ToString("0", IC)).Append(" fps)\n");
            }
            raw.Dispose(); jpg.Dispose();
            sb.Append(grab.StatsLine());
            grab.Close();
            Finish(sb.ToString());
        }

        static void Dump(Color32[] unity, byte[] nat, int w, int h, bool bgra)
        {
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "_mod/readback_test");
                System.IO.Directory.CreateDirectory(dir);
                var t = new Texture2D(w, h, TextureFormat.RGB24, false);
                t.SetPixels32(unity);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "readpixels.jpg"), t.EncodeToJPG(90));
                var conv = new Color32[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int o = ((h - 1 - y) * w + x) * 4;
                        conv[y * w + x] = new Color32(nat[o + (bgra ? 2 : 0)], nat[o + 1], nat[o + (bgra ? 0 : 2)], 255);
                    }
                t.SetPixels32(conv);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "native.jpg"), t.EncodeToJPG(90));
                Destroy(t);
            }
            catch (Exception e) { DevLog.Error("readback dump", e); }
        }

        void Finish(string text)
        {
            if (paced)
            {
                QualitySettings.vSyncCount = vsync0;
                Application.targetFrameRate = target0;
                try { FramerateController.SetVSyncCount(vsync0); FramerateController.automatic = fcAuto0; } catch { }
                paced = false;
            }
            result = text;
            running = false;
            DevLog.Write("mcam readback test:\n" + text);
            Destroy(gameObject);
        }
    }
}
