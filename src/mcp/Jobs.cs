using System;
using System.Collections.Generic;
using System.Threading;

namespace InsideDev.McpExt
{
    // Jobs.cs (owner HOST, signatures frozen): the job runner behind CallContext.BeginJob. One job per lane; ids are
    // "j1", "j2", ... for the game session; finished jobs are kept for 10 minutes (last 50) so wait_until {job} and the
    // dev tool jobs can read their result. A MultiCam capture holds the control lane (busy, tool "mcam"). Cancel sets
    // the job's flag (and cancels the call that owns it); the owning loop sees it within one frame and ends the job
    // (inputs released, game re-frozen). Unity-free; HostJobs.CaptureBusy is set by McpExtension (MultiCam.Capturing,
    // cached on the main thread).
    public sealed class HostJob : McpJob
    {
        readonly string id, tool, lane;
        internal readonly CallInfo call;
        readonly DateTime started = DateTime.UtcNow;
        DateTime ended;
        readonly object gate = new object();
        volatile string state = McpJobState.Running;
        volatile bool cancelled, detached, finished;
        string cancelReason = "", error, message = "";
        double progress, total;
        bool hasProgress;
        Json.Obj result;
        internal volatile bool autoDetached;
        internal HostCallContext ctx;
        internal DateTime cancelledAt;

        public HostJob(string id, string tool, string lane, CallInfo call)
        {
            this.id = id ?? ""; this.tool = tool ?? ""; this.lane = lane ?? McpLanes.Control; this.call = call;
        }

        public override string Id { get { return id; } }
        public override string Tool { get { return tool; } }
        public override string Lane { get { return lane; } }
        public override string State { get { return state; } }
        public override double ElapsedMs { get { var end = finished ? ended : DateTime.UtcNow; return (end - started).TotalMilliseconds; } }
        public override bool Cancelled { get { return cancelled; } }
        public override string CancelReason { get { lock (gate) return cancelReason; } }
        internal bool Ended { get { return finished; } }
        internal DateTime EndedUtc { get { return ended; } }
        internal DateTime StartedUtc { get { return started; } }

        public override void Cancel(string reason)
        {
            lock (gate)
            {
                if (finished || cancelled) return;
                cancelled = true; cancelReason = reason ?? "cancelled"; cancelledAt = DateTime.UtcNow;
            }
            if (call != null) call.Cancel(reason ?? "cancelled");
        }

        public override void Progress(double progress, double total, string message)
        {
            SetProgress(progress, total, message);
            var c = ctx;
            if (c != null) c.Emit(progress, total, message);
        }

        internal void SetProgress(double p, double t, string m)
        {
            lock (gate) { progress = p; total = t; if (m != null) message = m; hasProgress = true; }
        }

        public override void Detach() { detached = true; }

        // true once Detach was called; the host does not auto-finish detached jobs when the call returns
        public bool Detached { get { return detached; } }

        public override void Finish(Json.Obj result) { End(cancelled ? McpJobState.Cancelled : McpJobState.Done, result, null); }

        public override void Fail(string code, string message)
        {
            var e = new Json.Obj(); e["code"] = code ?? McpErr.Internal; e["message"] = message ?? "";
            var s = new Json.Obj(); s["ok"] = false; s["error"] = e;
            End(cancelled || code == McpErr.Cancelled ? McpJobState.Cancelled : McpJobState.Failed, s, (code ?? McpErr.Internal) + ": " + (message ?? ""));
        }

        // end with a tool result: failed when the result is an error, else done (cancelled when the job was cancelled)
        internal void EndWith(ToolResult r)
        {
            if (r == null) { Fail(McpErr.Internal, tool + " returned nothing"); return; }
            var s = r.structured;
            if (r.isError)
            {
                var e = s != null ? s["error"] as Json.Obj : null;
                string code = e != null ? e.Str("code", McpErr.Internal) : McpErr.Internal;
                string msg = e != null ? e.Str("message", "") : FirstText(r);
                End(cancelled || code == McpErr.Cancelled ? McpJobState.Cancelled : McpJobState.Failed, s ?? Envelope(code, msg), code + ": " + msg);
            }
            else End(cancelled ? McpJobState.Cancelled : McpJobState.Done, s ?? TextEnvelope(r), null);
        }

        static string FirstText(ToolResult r)
        {
            foreach (var c in r.content) if (c.Str("type") == "text") return c.Str("text", "");
            return "";
        }

        static Json.Obj Envelope(string code, string msg)
        {
            var e = new Json.Obj(); e["code"] = code; e["message"] = msg;
            var s = new Json.Obj(); s["ok"] = false; s["error"] = e; return s;
        }

        static Json.Obj TextEnvelope(ToolResult r) { var s = new Json.Obj(); s["ok"] = true; s["text"] = FirstText(r); return s; }

        bool End(string st, Json.Obj res, string err)
        {
            lock (gate)
            {
                if (finished) return false;
                state = st; result = res; error = err; ended = DateTime.UtcNow; finished = true;
                Monitor.PulseAll(gate);
            }
            HostJobs.Ended(this);
            return true;
        }

        // worker thread: true when the job ended within timeoutMs
        public override bool Await(int timeoutMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
            lock (gate)
            {
                while (!finished)
                {
                    int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) return false;
                    Monitor.Wait(gate, Math.Min(left, 250));
                }
                return true;
            }
        }

        public override Json.Obj Result { get { lock (gate) return result; } }

        public override Json.Obj Describe()
        {
            var o = new Json.Obj();
            lock (gate)
            {
                o["id"] = id; o["tool"] = tool; o["lane"] = lane; o["state"] = state;
                o["elapsed_s"] = Math.Round(ElapsedMs / 1000.0, 1);
                if (hasProgress) { o["progress"] = progress; o["total"] = total; }
                if (message.Length > 0) o["message"] = message;
                if (error != null) o["error"] = error;
                if (detached) o["detached"] = true;
                if (cancelled) o["cancel_reason"] = cancelReason;
            }
            return o;
        }
    }

    // the control lane while a MultiCam capture runs ("mcam" in busy results, wait_until {job:"mcam"}, jobs list)
    internal sealed class CaptureJob : McpJob
    {
        DateTime since = DateTime.UtcNow;
        bool wasOn;

        internal bool On { get { bool on = HostJobs.CaptureOn(); if (on && !wasOn) since = DateTime.UtcNow; wasOn = on; return on; } }
        public override string Id { get { return "mcam"; } }
        public override string Tool { get { return "mcam"; } }
        public override string Lane { get { return McpLanes.Control; } }
        public override string State { get { return On ? McpJobState.Running : McpJobState.Done; } }
        public override double ElapsedMs { get { return On ? (DateTime.UtcNow - since).TotalMilliseconds : 0; } }
        public override bool Cancelled { get { return false; } }
        public override string CancelReason { get { return ""; } }
        public override void Cancel(string reason) { }
        public override void Progress(double progress, double total, string message) { }
        public override void Detach() { }
        public override void Finish(Json.Obj result) { }
        public override void Fail(string code, string message) { }

        public override bool Await(int timeoutMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
            while (On)
            {
                if (DateTime.UtcNow >= until) return false;
                Thread.Sleep(50);
            }
            return true;
        }

        public override Json.Obj Result { get { return null; } }

        public override Json.Obj Describe()
        {
            var o = new Json.Obj();
            o["id"] = Id; o["tool"] = Tool; o["lane"] = Lane; o["state"] = State;
            o["elapsed_s"] = Math.Round(ElapsedMs / 1000.0, 1);
            o["message"] = "MultiCam capture; stop it with command 'mcam stop'";
            return o;
        }
    }

    public static class HostJobs
    {
        static readonly object gate = new object();
        static readonly Dictionary<string, HostJob> lanes = new Dictionary<string, HostJob>();
        static readonly List<HostJob> finished = new List<HostJob>();
        static readonly CaptureJob capture = new CaptureJob();
        static int counter;
        internal static Func<bool> CaptureBusy;
        internal static int KeepFinished = 50;
        internal static TimeSpan KeepFor = TimeSpan.FromMinutes(10);
        internal static TimeSpan MaxDetached = TimeSpan.FromMinutes(10);

        internal static bool CaptureOn()
        {
            var f = CaptureBusy;
            if (f == null) return false;
            try { return f(); } catch { return false; }
        }

        // null + busy result when the lane is held (or a MultiCam capture runs and lane is control)
        public static McpJob Begin(string lane, CallInfo call, out ToolResult busy)
        {
            busy = null;
            if (string.IsNullOrEmpty(lane)) lane = McpLanes.Control;
            if (call != null && call.Cancelled)
            {
                busy = ToolResult.Fail(McpErr.Cancelled, "cancelled: " + call.CancelReason, "the call was cancelled before it started");
                return null;
            }
            lock (gate)
            {
                HostJob cur;
                if (lanes.TryGetValue(lane, out cur) && !cur.Ended)
                {
                    if (call != null && ReferenceEquals(cur.call, call)) return cur;
                    busy = ToolResult.Busy(cur);
                    return null;
                }
                if (lane == McpLanes.Control && CaptureOn())
                {
                    busy = ToolResult.Busy(capture);
                    var e = busy.structured["error"] as Json.Obj;
                    if (e != null) e["hint"] = "a MultiCam capture holds the game; wait for it with wait_until {any:[{job:\"mcam\"}]} or stop it with command 'mcam stop'";
                    return null;
                }
                var j = new HostJob("j" + (++counter), call != null ? call.tool : "", lane, call);
                lanes[lane] = j;
                return j;
            }
        }

        public static McpJob Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id == "mcam") return capture;
            lock (gate)
            {
                foreach (var j in lanes.Values) if (j.Id == id) return j;
                for (int i = finished.Count - 1; i >= 0; i--) if (finished[i].Id == id) return finished[i];
            }
            return null;
        }

        public static McpJob Running(string lane)
        {
            if (string.IsNullOrEmpty(lane)) lane = McpLanes.Control;
            lock (gate)
            {
                HostJob j;
                return lanes.TryGetValue(lane, out j) && !j.Ended ? j : null;
            }
        }

        // running first, then recent finished ones
        public static List<McpJob> All()
        {
            var l = new List<McpJob>();
            bool cap = CaptureOn();
            lock (gate)
            {
                Prune();
                foreach (var j in lanes.Values) if (!j.Ended) l.Add(j);
                if (cap) l.Add(capture);
                for (int i = finished.Count - 1; i >= 0; i--) l.Add(finished[i]);
            }
            return l;
        }

        public static int CancelAll(string reason)
        {
            var l = new List<HostJob>();
            lock (gate) foreach (var j in lanes.Values) if (!j.Ended) l.Add(j);
            foreach (var j in l) j.Cancel(reason);
            return l.Count;
        }

        // called when a tool call returns: finishes the call's job unless it was detached
        public static void EndOfCall(CallInfo call, ToolResult result)
        {
            if (call == null) return;
            var mine = new List<HostJob>();
            lock (gate) foreach (var j in lanes.Values) if (ReferenceEquals(j.call, call) && !j.Ended) mine.Add(j);
            foreach (var j in mine)
            {
                if (j.Detached && !j.autoDetached) continue;
                j.EndWith(result);
            }
        }

        internal static List<HostJob> OfCall(CallInfo call)
        {
            var mine = new List<HostJob>();
            if (call == null) return mine;
            lock (gate) foreach (var j in lanes.Values) if (ReferenceEquals(j.call, call) && !j.Ended) mine.Add(j);
            return mine;
        }

        internal static void Ended(HostJob j)
        {
            lock (gate)
            {
                HostJob cur;
                if (lanes.TryGetValue(j.Lane, out cur) && ReferenceEquals(cur, j)) lanes.Remove(j.Lane);
                if (!finished.Contains(j)) finished.Add(j);
                Prune();
            }
        }

        static void Prune()
        {
            var now = DateTime.UtcNow;
            finished.RemoveAll(j => now - j.EndedUtc > KeepFor);
            if (finished.Count > KeepFinished) finished.RemoveRange(0, finished.Count - KeepFinished);
        }

        // detached jobs that outlive MaxDetached are cancelled; 5 s later, if still running, failed (frees the lane)
        internal static void Sweep()
        {
            var now = DateTime.UtcNow;
            var l = new List<HostJob>();
            lock (gate) foreach (var j in lanes.Values) if (!j.Ended && (j.Detached || j.autoDetached)) l.Add(j);
            foreach (var j in l)
            {
                if (!j.Cancelled && now - j.StartedUtc > MaxDetached) j.Cancel("job ran longer than " + (int)MaxDetached.TotalSeconds + " s");
                else if (j.Cancelled && now - j.cancelledAt > TimeSpan.FromSeconds(5)) j.Fail(McpErr.Timeout, "the job did not end after it was cancelled (" + j.CancelReason + ")");
            }
        }

        // offline tests only
        internal static void ResetForTests()
        {
            lock (gate) { lanes.Clear(); finished.Clear(); counter = 0; }
            CaptureBusy = null;
        }
    }
}
