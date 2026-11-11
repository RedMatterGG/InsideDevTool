using System;
using System.Collections.Generic;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // PlayLevelsStream.cs (owner WORLD_A): stream {op:"load"} and the freeze keeper. A load is a copy of Playdead's
    // SavepointManager.SavepointTask.WaitForSubscene (FP/SavepointManager.cs:277-317): CullingActive off, wait while the
    // culler is InProgress, LoadSubscene(StreamManager.LoadStrategy.Synchronous, timesliced true) when Unloaded, wait while
    // Loading / QueuedForLoad, ActivateSubsceneImmediate when asked, CullingActive back. It runs as a state machine in
    // the main-thread tick, so a cancelled or abandoned call never leaves culling switched off; a game load that starts
    // meanwhile ends it (the load owns culling then). The freeze keeper switches culling off again after a load that
    // turned it back on (GameManager.ReloadOnlySubscenes sets CullingActive true, FP/GameManager.cs:474-503).
    // CullingActive is held off through one shared record (Hold / Release): the first holder remembers the value, the
    // last one puts it back, so an op that outlives its call (timeout, cancel) and a freeze or a second op that arrive
    // meanwhile cannot remember "off" as the original. One stream load at a time: a second one answers busy.
    internal static class PlayLevelsStream
    {
        sealed class Op
        {
            public int sub, phase;
            public bool activate, touched;
            public SubsceneCuller culler;
            public double startMs;
            public volatile bool done;
            public string outcome = "done";
            public readonly List<string> warnings = new List<string>();
        }

        static readonly List<Op> ops = new List<Op>();
        static volatile bool frozen;
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        const double TimeoutMs = 30000;

        public static bool Frozen { get { return frozen; } set { frozen = value; if (value) PlayLevels.TickOn(); } }
        public static bool Busy { get { lock (ops) return ops.Count > 0; } }

        // main thread: the shared hold on CullingActive
        static int holders;
        static bool cullingBefore = true;

        static void Hold(SubsceneCullingManager mgr)
        {
            if (holders++ == 0) cullingBefore = mgr.CullingActive;
            mgr.CullingActive = false;
        }

        // apply false = a game load took streaming over: it sets CullingActive itself
        static void Release(bool apply)
        {
            if (holders <= 0) return;
            if (--holders > 0 || !apply) return;
            try { SubsceneCullingManager.instance.CullingActive = frozen ? false : cullingBefore; } catch { }
        }

        // main thread: the CullingActive value that counts as "before" for a freeze that starts now (live = the
        // manager's value): while a stream load holds culling off it is the value from before that load
        internal static bool Original(bool live) { return holders > 0 ? cullingBefore : live; }

        // main thread, end of a freeze: true = the caller sets CullingActive to value now; false = a stream load still
        // holds culling off and will put value back when it ends
        internal static bool ThawNow(bool value)
        {
            if (holders <= 0) return true;
            cullingBefore = value;
            return false;
        }

        // worker thread; the caller holds the control lane
        public static string Load(CallContext ctx, int sub, bool activate, List<string> warnings)
        {
            var op = new Op { sub = sub, activate = activate, startMs = clock.Elapsed.TotalMilliseconds };
            ctx.MainDo(() =>
            {
                if (PlayLevels.Loading) throw new McpException(McpErr.Busy, "busy: a load is in progress", "wait_until {any:[{ready:true}]}, then retry");
                if (Busy) throw new McpException(McpErr.Busy, "busy: a stream load is still running (an earlier call ended before it did)", "it finishes or gives up within 30 s; stream {op:\"status\"} shows the state, then retry");
                op.culler = SavepointManager.GetCuller(sub);
                if (op.culler == null) throw new McpException(McpErr.NotFound, "no culler for subscene " + sub);
                lock (ops) ops.Add(op);
                PlayLevels.TickOn();
            });
            int ms = (int)Math.Min(TimeoutMs + 2000, ctx.RemainingMs - 1500);
            bool ok = ms > 0 && ctx.WaitUntil(() => op.done, ms);
            lock (op.warnings) warnings.AddRange(op.warnings);
            if (!ok)
            {
                warnings.Add("load: still running when the call ended; it finishes or times out by itself in the game tick");
                return "timeout";
            }
            if (op.outcome == "done" && !PlayLevels.IsPinned(sub)) warnings.Add("not pinned: the culling manager unloads it again when it is not visible; stream {op:\"pin\"} keeps it");
            return op.outcome;
        }

        // main thread, every frame (from PlayLevels.Tick)
        public static void Tick(bool gameLoading)
        {
            if (frozen && !gameLoading)
            {
                try { var m = SubsceneCullingManager.instance; if (m.CullingActive) m.CullingActive = false; } catch { }
            }
            Op[] arr;
            lock (ops) { if (ops.Count == 0) return; arr = ops.ToArray(); }
            foreach (var op in arr)
            {
                bool end;
                try { end = Step(op, gameLoading); }
                catch (Exception e) { op.outcome = "refused"; Note(op, "load: " + e.GetType().Name + ": " + e.Message); Restore(op); end = true; }
                if (!end) continue;
                lock (ops) ops.Remove(op);
                op.done = true;
            }
        }

        static bool Step(Op op, bool gameLoading)
        {
            var c = op.culler;
            if (c == null) { op.outcome = "refused"; Note(op, "load: the culler is gone"); Restore(op); return true; }
            if (gameLoading && op.phase > 0)
            {
                op.outcome = "refused";
                Note(op, "load: a game load started and took over streaming");
                if (op.touched) { op.touched = false; Release(false); }
                return true;
            }
            if (clock.Elapsed.TotalMilliseconds - op.startMs > TimeoutMs) { op.outcome = "timeout"; Note(op, "load: gave up after 30 s (" + c.LoadedState + ")"); Restore(op); return true; }
            var mgr = SubsceneCullingManager.instance;
            switch (op.phase)
            {
                case 0:
                    Hold(mgr);
                    op.touched = true;
                    op.phase = 1;
                    return false;
                case 1:
                    if (c.InProgress) return false;
                    if (c.LoadedState == SubsceneCuller.ELoadedState.Unloaded)
                    {
                        c.LoadSubscene(StreamManager.LoadStrategy.Synchronous, true);
                        Note(op, "load: " + c.SceneName + " was unloaded, loading it");
                    }
                    op.phase = 2;
                    return false;
                case 2:
                    if (c.LoadedState == SubsceneCuller.ELoadedState.Loading || c.LoadedState == SubsceneCuller.ELoadedState.QueuedForLoad) return false;
                    if (op.activate && !c.IsActive) c.ActivateSubsceneImmediate();
                    Note(op, "load: " + c.SceneName + " is " + c.LoadedState);
                    Restore(op);
                    return true;
            }
            return true;
        }

        static void Restore(Op op)
        {
            if (!op.touched) return;
            op.touched = false;
            Release(true);
        }

        static void Note(Op op, string s) { lock (op.warnings) op.warnings.Add(s); }
    }
}
