using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace InsideDev.McpExt
{
    // HostContext.cs (owner HOST, signatures frozen): CallContext implementation and the main-thread queue it uses.
    // MainQueue.Pump runs from McpExtension.Tick (DevRoot.Update, after FixedUpdate of the frame): it updates Frame, runs
    // queued main-thread items (budget about 4 ms per frame, at least one item), and evaluates WaitUntil checks.
    // An item whose caller gave up (timeout or cancel) is removed and never runs later: no zombie side effects.
    // Unity-free: the frame number comes from MainQueue.FrameSource (Time.frameCount, set by McpExtension.Start), so the
    // queue, waits and jobs run in the offline tests (qa/mcp/HOST) with a fake pump thread.
    public sealed class HostCallContext : CallContext
    {
        readonly CallInfo info;
        readonly object pgate = new object();
        long lastSentTicks;
        bool hasPending;
        double pendP, pendT;
        string pendM;
        volatile bool wireCut;
        internal HostJob job;

        const long ProgressGapTicks = 250 * TimeSpan.TicksPerMillisecond;
        const int Slice = 20;

        public HostCallContext(CallInfo info) { this.info = info ?? new CallInfo(); }

        public override CallInfo Info { get { return info; } }

        public override void Progress(double progress, double total, string message)
        {
            var j = job;
            if (j != null) j.SetProgress(progress, total, message);
            Emit(progress, total, message);
        }

        // rate-limited forward to the server's sink: at most 4 per second, the last one is kept and flushed later
        internal void Emit(double progress, double total, string message)
        {
            if (wireCut || info.progress == null) return;
            bool send = false;
            lock (pgate)
            {
                long now = DateTime.UtcNow.Ticks;
                if (now - lastSentTicks >= ProgressGapTicks) { lastSentTicks = now; hasPending = false; send = true; }
                else { hasPending = true; pendP = progress; pendT = total; pendM = message; }
            }
            if (send) Sink(progress, total, message);
        }

        internal void FlushProgress()
        {
            double p, t; string m;
            lock (pgate)
            {
                if (!hasPending) return;
                hasPending = false; p = pendP; t = pendT; m = pendM; lastSentTicks = DateTime.UtcNow.Ticks;
            }
            Sink(p, t, m);
        }

        void MaybeFlush()
        {
            if (!hasPending || wireCut) return;
            if (DateTime.UtcNow.Ticks - lastSentTicks >= ProgressGapTicks) FlushProgress();
        }

        void Sink(double p, double t, string m)
        {
            var s = info.progress;
            if (s == null || wireCut) return;
            try { s(p, t, m); } catch (Exception e) { HostLog.Write("progress sink of " + info.tool + ": " + e.Message); info.progress = null; }
        }

        // the call has returned (or the host answered for it): later progress must not reach the response stream
        internal void CutWire() { wireCut = true; }

        int Effective(int timeoutMs)
        {
            if (timeoutMs <= 0) timeoutMs = 10000;
            return Math.Min(timeoutMs, Math.Max(info.RemainingMs, 2000));
        }

        public override T Main<T>(Func<T> fn, int timeoutMs = 10000)
        {
            if (fn == null) throw new ArgumentNullException("fn");
            if (MainQueue.OnMainThread)
            {
                try { return fn(); }
                catch (McpException) { throw; }
                catch (Exception e) { throw GameError(e); }
            }
            ThrowIfCancelled();
            int limit = Effective(timeoutMs);
            var it = MainQueue.Enqueue(() => fn(), false);
            var sw = Stopwatch.StartNew();
            while (true)
            {
                int st = it.state;
                if (st == MainItem.Done) break;
                if (st == MainItem.Pending)
                {
                    if (info.Cancelled && MainQueue.TryDrop(it)) throw new McpException(McpErr.Cancelled, "cancelled: " + info.CancelReason);
                    if (sw.ElapsedMilliseconds >= limit && MainQueue.TryDrop(it)) throw NotReady(limit);
                }
                MainQueue.WaitItem(it, Slice);
                MaybeFlush();
            }
            if (it.error != null)
            {
                var me = it.error as McpException;
                if (me != null) throw me;
                throw GameError(it.error);
            }
            return it.result is T ? (T)it.result : default(T);
        }

        public override void MainDo(Action fn, int timeoutMs = 10000)
        {
            if (fn == null) throw new ArgumentNullException("fn");
            Main<bool>(() => { fn(); return true; }, timeoutMs);
        }

        internal static McpException GameError(Exception e)
        {
            while (e is System.Reflection.TargetInvocationException && e.InnerException != null) e = e.InnerException;
            var me = e as McpException;
            if (me != null) return me;
            var x = new McpException(McpErr.GameError, e.GetType().Name + ": " + e.Message, "the game threw while running the request; see log for the stack");
            return x;
        }

        static McpException NotReady(int limit)
        {
            string why = MainQueue.Alive
                ? "the main thread is busy (a long load or a heavy frame)"
                : "the main thread is not running (window unfocused without runInBackground, a long load, or the game is paused)";
            return new McpException(McpErr.NotReady, "the main thread did not run the request within " + (limit / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s: " + why,
                "nothing was run; try again, or focus the game window / wait for the load");
        }

        public override bool WaitFrames(int frames, int timeoutMs)
        {
            if (frames <= 0) return true;
            if (MainQueue.OnMainThread) return false;
            var w = MainQueue.AddWaiter(null, MainQueue.Frame + frames, 1);
            return WaitWaiter(w, timeoutMs);
        }

        public override bool WaitUntil(Func<bool> check, int timeoutMs, int everyFrames = 1)
        {
            if (check == null) throw new ArgumentNullException("check");
            if (MainQueue.OnMainThread)
            {
                try { return check(); } catch (McpException) { throw; } catch (Exception e) { throw GameError(e); }
            }
            var w = MainQueue.AddWaiter(check, 0, Math.Max(1, everyFrames));
            return WaitWaiter(w, timeoutMs);
        }

        bool WaitWaiter(Waiter w, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    if (MainQueue.WaitWaiter(w, Slice)) break;
                    MaybeFlush();
                    if (info.Cancelled) return false;
                    long el = sw.ElapsedMilliseconds;
                    if (el >= timeoutMs || info.RemainingMs <= 0) return false;
                }
            }
            finally { MainQueue.RemoveWaiter(w); }
            if (w.error != null)
            {
                var me = w.error as McpException;
                if (me != null) throw me;
                throw GameError(w.error);
            }
            return true;
        }

        public override bool Sleep(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                if (info.Cancelled) return false;
                long left = ms - sw.ElapsedMilliseconds;
                if (left <= 0) return true;
                if (info.RemainingMs <= 0) return false;
                Thread.Sleep((int)Math.Min(Slice, left));
                MaybeFlush();
            }
        }

        public override long Frame { get { return MainQueue.Frame; } }

        public override McpJob BeginJob(string lane, out ToolResult busy)
        {
            var j = HostJobs.Begin(lane, info, out busy);
            var hj = j as HostJob;
            if (hj != null) { hj.ctx = this; job = hj; }
            return j;
        }

        public override McpJob FindJob(string id) { return HostJobs.Find(id); }
        public override McpJob RunningJob(string lane) { return HostJobs.Running(lane); }
        public override void Log(string msg) { HostLog.Write("[" + info.tool + "] " + msg); }
        public override void Audit(string what, Json.Obj detail) { AuditLog.Write(info, what, detail); }
    }

    internal sealed class MainItem
    {
        public const int Pending = 0, Running = 1, Done = 2, Dropped = 3;
        public Func<object> fn;
        public object result;
        public Exception error;
        public volatile int state;
        public bool post;
        public string label;
    }

    internal sealed class Waiter
    {
        public Func<bool> check;
        public long next;
        public int every = 1;
        public volatile bool done;
        public Exception error;
    }

    public static class MainQueue
    {
        static readonly object gate = new object();
        static readonly List<MainItem> items = new List<MainItem>();
        static readonly List<Waiter> waiters = new List<Waiter>();
        static long frame, lastPumpTicks;
        static volatile int mainThread = -1;
        internal static Func<long> FrameSource;
        internal static double BudgetMs = 4.0;
        internal static long Pumps;

        // main thread, once per frame
        public static void Pump()
        {
            long f;
            var src = FrameSource;
            try { f = src != null ? src() : Interlocked.Read(ref frame) + 1; } catch { f = Interlocked.Read(ref frame) + 1; }
            PumpAt(f);
        }

        internal static void PumpAt(long f)
        {
            mainThread = Thread.CurrentThread.ManagedThreadId;
            Interlocked.Exchange(ref frame, f);
            Interlocked.Exchange(ref lastPumpTicks, DateTime.UtcNow.Ticks);
            Interlocked.Increment(ref Pumps);
            var sw = Stopwatch.StartNew();
            int ran = 0;
            while (true)
            {
                MainItem it = null;
                lock (gate)
                {
                    if (items.Count > 0 && (ran == 0 || sw.Elapsed.TotalMilliseconds < BudgetMs))
                    {
                        it = items[0]; items.RemoveAt(0); it.state = MainItem.Running;
                    }
                }
                if (it == null) break;
                try { it.result = it.fn(); }
                catch (Exception e) { it.error = e; if (it.post) HostLog.Write("main-thread task " + (it.label ?? "") + " failed: " + e.GetType().Name + ": " + e.Message); }
                lock (gate) { it.state = MainItem.Done; Monitor.PulseAll(gate); }
                ran++;
            }
            List<Waiter> due = null;
            lock (gate)
            {
                foreach (var w in waiters) if (!w.done && f >= w.next) { if (due == null) due = new List<Waiter>(); due.Add(w); }
            }
            if (due != null)
                foreach (var w in due)
                {
                    if (w.check == null) { w.done = true; continue; }
                    try { if (w.check()) w.done = true; else w.next = f + w.every; }
                    catch (Exception e) { w.error = e; w.done = true; }
                }
            lock (gate)
            {
                for (int i = waiters.Count - 1; i >= 0; i--) if (waiters[i].done) waiters.RemoveAt(i);
                Monitor.PulseAll(gate);
            }
        }

        // Time.frameCount of the last Pump (any thread)
        public static long Frame { get { return Interlocked.Read(ref frame); } }
        // true when Pump ran within the last 2 s (the main thread is alive)
        public static bool Alive
        {
            get { long t = Interlocked.Read(ref lastPumpTicks); return t != 0 && DateTime.UtcNow.Ticks - t < 2 * TimeSpan.TicksPerSecond; }
        }
        public static int Pending { get { lock (gate) return items.Count; } }

        internal static bool OnMainThread { get { return mainThread == Thread.CurrentThread.ManagedThreadId; } }

        internal static MainItem Enqueue(Func<object> fn, bool post)
        {
            var it = new MainItem { fn = fn, post = post };
            lock (gate) items.Add(it);
            return it;
        }

        // fire and forget: runs on the next pump even when the caller is cancelled (cleanup that must happen)
        internal static void Post(string label, Action a)
        {
            if (a == null) return;
            if (OnMainThread) { try { a(); } catch (Exception e) { HostLog.Write("main-thread task " + label + " failed: " + e.Message); } return; }
            var it = new MainItem { fn = () => { a(); return null; }, post = true, label = label };
            lock (gate) items.Add(it);
        }

        // true when the item was still pending and is now removed for good
        internal static bool TryDrop(MainItem it)
        {
            lock (gate)
            {
                if (it.state != MainItem.Pending) return false;
                items.Remove(it);
                it.state = MainItem.Dropped;
                return true;
            }
        }

        internal static void WaitItem(MainItem it, int ms)
        {
            lock (gate) { if (it.state == MainItem.Pending || it.state == MainItem.Running) Monitor.Wait(gate, ms); }
        }

        internal static Waiter AddWaiter(Func<bool> check, long next, int every)
        {
            var w = new Waiter { check = check, next = next, every = every };
            lock (gate) waiters.Add(w);
            return w;
        }

        internal static bool WaitWaiter(Waiter w, int ms)
        {
            lock (gate)
            {
                if (!w.done) Monitor.Wait(gate, ms);
                return w.done;
            }
        }

        internal static void RemoveWaiter(Waiter w) { lock (gate) waiters.Remove(w); }

        internal static int WaiterCount { get { lock (gate) return waiters.Count; } }

        // offline tests only: forget every queued item and waiter
        internal static void ResetForTests()
        {
            lock (gate) { items.Clear(); waiters.Clear(); Monitor.PulseAll(gate); }
            mainThread = -1;
            Interlocked.Exchange(ref lastPumpTicks, 0);
        }
    }

    // log lines from worker threads, written to DevLog by McpExtension.Tick on the main thread
    internal static class HostLog
    {
        static readonly Queue<string> q = new Queue<string>();
        const int Max = 500;

        public static void Write(string msg)
        {
            lock (q) { q.Enqueue(msg ?? ""); while (q.Count > Max) q.Dequeue(); }
        }

        public static List<string> Drain()
        {
            var l = new List<string>();
            lock (q) while (q.Count > 0) l.Add(q.Dequeue());
            return l;
        }
    }

    // _mod/mcp_audit.jsonl: one JSON line per non-read-only call and per CallContext.Audit
    public static class AuditLog
    {
        internal static string FilePath;
        internal const long MaxBytes = 5L * 1024 * 1024;
        static readonly object gate = new object();

        public static void Write(CallInfo call, string what, Json.Obj detail)
        {
            string path = FilePath;
            if (string.IsNullOrEmpty(path)) return;
            var o = new Json.Obj();
            o["t"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            if (call != null)
            {
                o["profile"] = call.profile; o["tool"] = call.tool;
                if (!string.IsNullOrEmpty(call.sessionId)) o["session"] = call.sessionId.Length > 8 ? call.sessionId.Substring(0, 8) : call.sessionId;
                if (!string.IsNullOrEmpty(call.client)) o["client"] = call.client.Length > 60 ? call.client.Substring(0, 60) : call.client;
            }
            o["what"] = what ?? "";
            if (detail != null) o["detail"] = detail;
            string line;
            try { line = JsonOut.Write(o); } catch (Exception e) { line = "{\"what\":\"audit encode failed: " + e.Message.Replace("\"", "'") + "\"}"; }
            lock (gate)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string old = path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, line + "\n");
                }
                catch { }
            }
        }
    }
}
