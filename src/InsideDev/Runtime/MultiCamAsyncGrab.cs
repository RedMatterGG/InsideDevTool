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
    // The plugin has one session. Whoever opens it takes Owners.Readback first ("mcam", "readback test" here, "grec" in the
    // Play module) and gives it back when the session is closed, so a second opener gets a refusal that names the holder
    // instead of taking the session away from under the first one.
    internal static class ReadbackNative
    {
        public const int Event = 0x52420000, OpGrab = 1, OpFlush = 2, OpProbe = 3;
        public const int InfoCpuBytes = 0, InfoStagingBytes = 1, InfoState = 2;
        public const int StateOpen = 1, StateFrameHeld = 2, StateHooked = 4, StateReleasePending = 8;

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
                return "device type " + deviceType + " (2 = D3D11), device " + hasDevice + ", device calls " + deviceCalls + " (last event " + lastDeviceEvent + "), render events " + calls + " (last 0x" + lastEvent.ToString("X8", CultureInfo.InvariantCulture) + "), probes " + probes + ", grabs " + grabs + ", flushes " + flushes + ", frames " + frames + ", produced " + produced + ", taken " + taken + ", released " + released + ", dropped " + dropped + ", pending " + pending + ", source " + source + ", " + width + "x" + height + " DXGI " + dxgiFormat + ", render thread " + renderThread + ", errors " + errors + " (last: " + ErrorText(lastError) + "), session " + session + ", ignored " + ignored + ", hooks " + hooked + " (OMSetRenderTargets checks " + hookCalls + "), marker hits " + markerHits + ", queued " + queued + " (depth " + queueDepth + ")";
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
        [DllImport("InsideReadback")] static extern long rb_info(int what);

        // rb_version of the loaded plugin; 0 until Unavailable() has answered null once (nothing here loads the plugin
        // before somebody asks for it)
        public static int Version;

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

        // InfoCpuBytes / InfoStagingBytes / InfoState of the plugin; -1 when the installed plugin is older than version 2.
        public static long Info(int what)
        {
            if (Version < 2) return -1;
            try { return rb_info(what); } catch { return -1; }
        }

        // Why rb_open answered 0 although Owners.Readback was free.
        public static string BusyText()
        {
            long st = Info(InfoState);
            if (st >= 0 && (st & StateOpen) != 0) return "InsideReadback busy: a session is open that no owner registered (stop the recording that uses it; 'mcam readback free force' closes a session that was left behind)";
            return "InsideReadback busy: CPU frames are still held by a writer";
        }

        public static string Unavailable()
        {
            try { int v = rb_version(); if (v >= 1) Version = v; return v >= 1 ? null : "InsideReadback version " + v; }
            catch (DllNotFoundException) { return "INSIDE_Data/Plugins/InsideReadback.dll is not installed"; }
            catch (EntryPointNotFoundException e) { return "InsideReadback: " + e.Message; }
            catch (Exception e) { return "InsideReadback: " + e.GetType().Name + ": " + e.Message; }
        }
    }

    internal sealed class MultiCamAsyncGrab
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public const int Stages = 4, Lag = 2, CpuFrames = 6;
        public const string OwnerMcam = "mcam", OwnerTest = "readback test";

        // the object whose native session is open (main thread)
        static MultiCamAsyncGrab current;
        string owner;

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

        public IEnumerator Open(int expectW, int expectH) { return Open(expectW, expectH, OwnerMcam); }

        // who = the Owners.Readback name of the caller. Open takes it and Close gives it back; a refusal names the holder.
        public IEnumerator Open(int expectW, int expectH, string who)
        {
            ready = false;
            string why = ReadbackNative.Unavailable();
            if (why != null) { error = why; yield break; }
            // an Open of the same owner that was dropped half way (a run aborted during the probe frames) still holds the session
            if (current != null && current != this && current.owner == who) current.Close();
            string holder;
            if (!Owners.TryTake(Owners.Readback, who, out holder)) { error = "InsideReadback busy: held by " + holder + (holder == "grec" ? " (grec stop first)" : holder == OwnerMcam ? " (an mcam run)" : ""); yield break; }
            owner = who;
            session = ReadbackNative.rb_open(Stages, Lag, CpuFrames);
            if (session == 0) { error = ReadbackNative.BusyText(); Owners.Release(Owners.Readback, who); owner = null; yield break; }
            current = this;
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
                if (f.error != 0 || f.data == IntPtr.Zero)
                {
                    if (f.slot >= 0) ReadbackNative.rb_release(f.slot);
                    Interlocked.Increment(ref dropped);
                    if (error == null) error = "readback frame " + f.seq + ": " + ReadbackNative.ErrorText(f.error);
                }
                else if ((f.tag & 0xFFF) != t.tag)
                {
                    ReadbackNative.rb_release(f.slot);
                    Interlocked.Increment(ref dropped);
                    if (error == null) error = "readback order: frame tag " + (f.tag & 0xFFF) + ", expected " + t.tag;
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

        // Main thread. Safe on an object that never opened and safe to call twice: the native session, the markers and
        // Owners.Readback are touched only by the object that holds them.
        public void Close()
        {
            bool had = session != 0;
            session = 0;
            stopping = true;
            if (had) { try { ReadbackNative.rb_close(); } catch { } }
            if (pump != null && !pump.Join(5000) && error == null) error = "readback pump did not stop";
            pump = null;
            if (had)
            {
                try
                {
                    var f = new ReadbackNative.Frame();
                    while (ReadbackNative.rb_take(0, ref f) == 1) if (f.slot >= 0) ReadbackNative.rb_release(f.slot);
                }
                catch { }
                try { ReadbackNative.rb_set_marker(IntPtr.Zero, IntPtr.Zero); } catch { }
                // frames a writer is still sending stay; FreeBuffers after the writers have closed takes the rest
                try { ReadbackNative.rb_free_buffers(); } catch { }
            }
            lock (gate) fifo.Clear();
            if (marker != null) { marker.Release(); UnityEngine.Object.Destroy(marker); marker = null; }
            if (marker2 != null) { marker2.Release(); UnityEngine.Object.Destroy(marker2); marker2 = null; }
            ready = false;
            if (current == this) current = null;
            if (owner != null) { Owners.Release(Owners.Readback, owner); owner = null; }
        }

        // Main thread. Closes the session of an Open that its caller lost (the run ended while Open was still probing).
        public static void CloseOrphan(string who)
        {
            var c = current;
            if (c != null && c.owner == who) c.Close();
        }

        // Main thread. Gives the plugin's CPU frames back to the system when nobody holds the plugin. Does nothing when
        // the plugin was never used in this process.
        public static void FreeBuffers()
        {
            if (ReadbackNative.Version < 1 || Owners.Holder(Owners.Readback) != null) return;
            try { ReadbackNative.rb_free_buffers(); } catch { }
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
        const string Usage = "usage: mcam readback test | status [json] | stat [json] | free [force]";
        int vsync0, target0;
        bool fcAuto0, paced, finished;
        MultiCamAsyncGrab grab;
        Texture2D tex;
        MultiCamGrab raw, jpg;

        public static string Command(List<string> a)
        {
            string s = a.Count > 2 ? a[2].ToLowerInvariant() : "status";
            bool json = a.Count > 3 && a[a.Count - 1].ToLowerInvariant() == "json";
            if (s == "status") return json ? JsonLine.Obj("running", running, "result", result, "holder", Owners.Holder(Owners.Readback) ?? "") : (running ? "running: " : "") + result;
            if (s == "stat") return Stat(json);
            if (s == "free") return Free(a.Count > 3 && a[3].ToLowerInvariant() == "force");
            if (s != "test") return Usage;
            if (running) return "readback test already running";
            if (MultiCam.Running) return "mcam is capturing; run the readback test when it is idle";
            string held = Owners.Holder(Owners.Readback);
            if (held != null) return "busy: the readback plugin is held by " + held;
            var go = new GameObject("InsideDev ReadbackTest");
            DontDestroyOnLoad(go);
            go.AddComponent<MultiCamReadbackTest>();
            running = true;
            result = "started";
            return "readback test started (about " + (Frames * 4 / 60 + 4) + " s); poll 'mcam readback status'";
        }

        static double Mb(long bytes) { return bytes < 0 ? -1.0 : Math.Round(bytes / 1048576.0, 1); }

        static string Stat(bool json)
        {
            string why = ReadbackNative.Unavailable();
            string holder = Owners.Holder(Owners.Readback) ?? "";
            if (why != null) return json ? JsonLine.Obj("installed", false, "reason", why, "holder", holder) : why;
            var s = ReadbackNative.Get();
            long cpu = ReadbackNative.Info(ReadbackNative.InfoCpuBytes), staging = ReadbackNative.Info(ReadbackNative.InfoStagingBytes), state = ReadbackNative.Info(ReadbackNative.InfoState);
            if (!json)
                return s.Text() + ", plugin version " + Fmt.I(s.version) + ", holder " + (holder.Length > 0 ? holder : "none") +
                       (state >= 0 ? ", session " + ((state & ReadbackNative.StateOpen) != 0 ? "open" : "closed") + ", memory held: CPU frames " + Fmt.F(Mb(cpu), "0.#") + " MB, staging textures " + Fmt.F(Mb(staging), "0.#") + " MB" : "");
            var o = new Json.Obj();
            o["installed"] = true; o["version"] = s.version; o["holder"] = holder;
            o["open"] = state >= 0 ? (object)((state & ReadbackNative.StateOpen) != 0) : null;
            o["frame_held"] = state >= 0 ? (object)((state & ReadbackNative.StateFrameHeld) != 0) : null;
            o["hooked"] = s.hooked != 0;
            o["cpu_mb"] = cpu >= 0 ? (object)Mb(cpu) : null; o["staging_mb"] = staging >= 0 ? (object)Mb(staging) : null;
            o["device"] = s.hasDevice != 0; o["source"] = s.source; o["width"] = s.width; o["height"] = s.height; o["dxgi_format"] = s.dxgiFormat;
            o["session"] = s.session; o["grabs"] = s.grabs; o["frames"] = s.frames; o["produced"] = s.produced; o["taken"] = s.taken; o["released"] = s.released;
            o["dropped"] = s.dropped; o["errors"] = s.errors; o["last_error"] = ReadbackNative.ErrorText(s.lastError); o["pending"] = s.pending;
            o["ignored_events"] = s.ignored; o["marker_hits"] = s.markerHits; o["hook_calls"] = s.hookCalls; o["queue_depth"] = s.queueDepth;
            return JsonLine.Write(o);
        }

        // Gives the memory of the plugin back between runs. With `force` it also closes a native session that no owner
        // registered (left open by a recorder that died).
        static string Free(bool force)
        {
            string why = ReadbackNative.Unavailable();
            if (why != null) return "cannot free: " + why;
            string holder = Owners.Holder(Owners.Readback);
            if (holder != null) return "busy: the readback plugin is held by " + holder + " (nothing freed)";
            long state = ReadbackNative.Info(ReadbackNative.InfoState), before = ReadbackNative.Info(ReadbackNative.InfoCpuBytes);
            string closed = "";
            if (state >= 0 && (state & ReadbackNative.StateOpen) != 0)
            {
                if (!force) return "busy: a native readback session is open that no owner registered (stop the recording that uses it; 'mcam readback free force' closes a session that was left behind)";
                try
                {
                    ReadbackNative.rb_close();
                    var f = new ReadbackNative.Frame();
                    while (ReadbackNative.rb_take(0, ref f) == 1) if (f.slot >= 0) ReadbackNative.rb_release(f.slot);
                    ReadbackNative.rb_set_marker(IntPtr.Zero, IntPtr.Zero);
                }
                catch (Exception e) { return "failed: " + e.GetType().Name + ": " + e.Message; }
                closed = "the open session was closed; ";
            }
            try { ReadbackNative.rb_free_buffers(); } catch (Exception e) { return "failed: " + e.GetType().Name + ": " + e.Message; }
            long after = ReadbackNative.Info(ReadbackNative.InfoCpuBytes), staging = ReadbackNative.Info(ReadbackNative.InfoStagingBytes);
            if (before < 0) return "readback: " + closed + "CPU frames freed";
            return "readback: " + closed + "CPU frames " + Fmt.F(Mb(before), "0.#") + " -> " + Fmt.F(Mb(after), "0.#") + " MB, staging textures " + Fmt.F(Mb(staging), "0.#") +
                   " MB (the render thread releases them at its first frame after a session is closed)";
        }

        void Start() { StartCoroutine(Drive()); }

        // Run and the enumerators it yields, on one stack: an exception anywhere in them ends in Finish, which closes the
        // plugin and puts vsync back.
        IEnumerator Drive()
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(Run());
            while (stack.Count > 0)
            {
                object cur = null;
                bool moved;
                try
                {
                    moved = stack.Peek().MoveNext();
                    if (moved) cur = stack.Peek().Current;
                }
                catch (Exception e)
                {
                    DevLog.Error("mcam readback test", e);
                    Finish("FAIL: " + e.GetType().Name + ": " + e.Message);
                    yield break;
                }
                if (!moved) { stack.Pop(); continue; }
                var nested = cur as IEnumerator;
                if (nested != null) { stack.Push(nested); continue; }
                yield return cur;
            }
            if (!finished) Finish("FAIL: the test ended without a result");
        }

        void OnDestroy()
        {
            if (finished) return;
            finished = true;
            Cleanup();
            result = "FAIL: the test object was destroyed before the test ended";
            running = false;
        }

        IEnumerator Run()
        {
            var sb = new StringBuilder();
            var eof = new WaitForEndOfFrame();
            grab = new MultiCamAsyncGrab();
            int sw = Screen.width, sh = Screen.height;
            yield return grab.Open(sw, sh, MultiCamAsyncGrab.OwnerTest);
            if (!grab.ready) { Finish("FAIL open: " + grab.error); yield break; }
            sb.Append("plugin: back buffer ").Append(grab.w).Append('x').Append(grab.h).Append(' ').Append(grab.pixFmt).Append(" (DXGI ").Append(grab.dxgiFormat).Append("), render thread ").Append(ReadbackNative.Get().renderThread).Append(", game thread ").Append(Thread.CurrentThread.ManagedThreadId).Append(" (managed id)\n");
            tex = new Texture2D(sw, sh, TextureFormat.RGBA32, false);
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
                yield return grab.Drain(5f);
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
            tex = null;
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
            raw = new MultiCamGrab(sw, sh, false, 95);
            jpg = new MultiCamGrab(sw, sh, true, 95);
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
                if (mode == 1) yield return grab.Drain(5f);
                ft.Sort();
                times[mode] = ft[ft.Count / 2];
                sb.Append(names[mode]).Append(": median frame ").Append(times[mode].ToString("0.0", IC)).Append(" ms (p90 ").Append(ft[ft.Count * 9 / 10].ToString("0.0", IC)).Append(" ms, ").Append((1000.0 / times[mode]).ToString("0", IC)).Append(" fps)\n");
            }
            sb.Append(grab.StatsLine());
            Finish(sb.ToString());
        }

        // Everything the test holds: the plugin session (and with it Owners.Readback), its textures, the vsync setting.
        void Cleanup()
        {
            try { if (grab != null) grab.Close(); } catch (Exception e) { DevLog.Error("readback test close", e); }
            grab = null;
            try { if (tex != null) Destroy(tex); } catch { }
            tex = null;
            try { if (raw != null) raw.Dispose(); if (jpg != null) jpg.Dispose(); } catch { }
            raw = null; jpg = null;
            if (paced)
            {
                paced = false;
                try { QualitySettings.vSyncCount = vsync0; Application.targetFrameRate = target0; } catch { }
                try { FramerateController.SetVSyncCount(vsync0); FramerateController.automatic = fcAuto0; } catch { }
            }
            MultiCamAsyncGrab.FreeBuffers();
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
            if (finished) return;
            finished = true;
            Cleanup();
            result = text;
            running = false;
            DevLog.Write("mcam readback test:\n" + text);
            Destroy(gameObject);
        }
    }
}
