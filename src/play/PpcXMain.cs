using System;
using System.Collections.Generic;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // PpcXMain.cs (owner WORLD_B): main-thread work that must happen even when the call that needs it was cancelled or
    // ran out of time. CallContext.MainDo throws before it queues anything for a cancelled call and drops an item the
    // main thread did not pick up in time, so an undo sent that way (unfreeze after a teleport) can be lost. Run tries
    // the normal way first and, when the item never ran, leaves it here; the "levels" tick runs the queue on the next
    // frame. With nothing queued the tick reads one field.
    internal static class MainCleanup
    {
        sealed class Job { public string what; public Action run; }

        static readonly List<Job> queue = new List<Job>();
        static volatile int pending;

        // any thread
        public static void Post(string what, Action run)
        {
            if (run == null) return;
            lock (queue) { queue.Add(new Job { what = what ?? "", run = run }); pending = queue.Count; }
            PlayLevels.TickOn();
        }

        // any thread: items wait for the next Pump
        public static bool Pending { get { return pending != 0; } }

        // worker thread: run on the main thread now; queued for the next frame when the call was cancelled or the main
        // thread did not take the item (an item that ran and threw is not run again)
        public static void Run(CallContext ctx, string what, Action run, List<string> warnings)
        {
            try { ctx.MainDo(run); }
            catch (McpException e)
            {
                if (e.code == McpErr.Cancelled || e.code == McpErr.NotReady)
                {
                    Post(what, run);
                    if (warnings != null) warnings.Add(what + ": runs on the next frame (" + e.code + ")");
                }
                else if (warnings != null) warnings.Add(what + ": " + e.Message);
            }
            catch (Exception e) { if (warnings != null) warnings.Add(what + ": " + e.Message); }
        }

        // main thread, every frame
        public static void Pump()
        {
            if (pending == 0) return;
            Job[] jobs;
            lock (queue) { jobs = queue.ToArray(); queue.Clear(); pending = 0; }
            foreach (var j in jobs)
            {
                try { j.run(); }
                catch (Exception e) { DevLog.Error("[play] cleanup " + j.what, e); }
            }
        }
    }
}
