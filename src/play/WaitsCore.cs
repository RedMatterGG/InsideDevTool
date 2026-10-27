using System;
using System.Collections.Generic;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // WaitsCore.cs (owner TIME): the Unity-free part of wait_until, compiled into InsideDev.Play.dll and into the offline
    // tests (qa/mcp/TIME): the condition keys, the one-key rule, which conditions need the control lane or must not stop
    // on death / loads, seconds -> steps, the "first that holds" combination and the result shape (a timeout is ok:true,
    // satisfied:false, timed_out:true, which:-1).
    internal sealed class WaitCond
    {
        public int index;
        public string key;
        public Json.Obj source;
        // main thread: null = not yet, else a short description of what held
        public Func<string> check;
        // main thread: the watched value for "observed"
        public Func<object> observe;
        // main thread: remove anything the condition installed (signal taps)
        public Action cleanup;
    }

    internal sealed class WaitSet
    {
        public readonly List<WaitCond> conds = new List<WaitCond>();
        volatile int which = -1;
        volatile string what;

        public int Which { get { return which; } }
        public string What { get { return what; } }

        // main thread: the first condition (in order) that holds now
        public string Check()
        {
            for (int i = 0; i < conds.Count; i++)
            {
                var c = conds[i];
                string s = c.check != null ? c.check() : null;
                if (s == null) continue;
                what = s;
                which = i;
                return "any[" + i + "] " + c.key + ": " + s;
            }
            return null;
        }

        public bool Holds() { return Check() != null; }

        // main thread
        public Json.Obj Observed()
        {
            var o = new Json.Obj();
            foreach (var c in conds)
            {
                if (c.observe == null) continue;
                string k = o.ContainsKey(c.key) ? c.key + "#" + c.index : c.key;
                object v;
                try { v = c.observe(); } catch (Exception e) { v = "unreadable: " + e.GetType().Name; }
                o[k] = v;
            }
            return o;
        }

        public void Cleanup()
        {
            foreach (var c in conds)
            {
                if (c.cleanup == null) continue;
                try { c.cleanup(); } catch { }
            }
        }
    }

    internal static class WaitPlan
    {
        public static readonly string[] AllKeys = { "steps", "seconds", "grounded", "airborne", "controllable", "ready", "loaded", "checkpoint_changed", "dead",
            "alive", "state", "screen", "near", "far_from", "hazard_safe", "hazard_eta", "music_time", "fsm_state", "signal", "object_active", "job" };

        // keys whose wait must not stop when the body dies or a load starts (they are about death, loads or respawns)
        static readonly string[] LoadAware = { "dead", "alive", "ready", "loaded", "screen", "checkpoint_changed", "job", "controllable" };

        public const double StepsPerSecond = 60;

        // the single key of a condition; error = why it is not exactly one known key
        public static string KeyOf(Json.Obj cond, int index, out string error)
        {
            error = null;
            if (cond == null) { error = "any[" + index + "]: expected an object with exactly one key"; return null; }
            var keys = new List<string>();
            foreach (var k in cond.order) if (cond.ContainsKey(k) && cond[k] != null) keys.Add(k);
            if (keys.Count != 1)
            {
                error = "any[" + index + "]: exactly one key per condition, got " + (keys.Count == 0 ? "none" : string.Join(", ", keys.ToArray())) +
                    " (put several conditions as separate items)";
                return null;
            }
            if (Array.IndexOf(AllKeys, keys[0]) < 0) { error = "any[" + index + "]: unknown condition " + keys[0] + " (known: " + string.Join(", ", AllKeys) + ")"; return null; }
            return keys[0];
        }

        public static List<string> KeysOf(List<object> any, out string error)
        {
            error = null;
            var l = new List<string>();
            if (any == null || any.Count == 0) { error = "any: at least one condition"; return null; }
            for (int i = 0; i < any.Count; i++)
            {
                string k = KeyOf(any[i] as Json.Obj, i, out error);
                if (k == null) return null;
                l.Add(k);
            }
            return l;
        }

        public static bool NeedsLane(List<string> keys)
        {
            foreach (var k in keys) if (k != "job") return true;
            return false;
        }

        public static bool LoadAwareAny(List<string> keys)
        {
            foreach (var k in keys) if (Array.IndexOf(LoadAware, k) >= 0) return true;
            return false;
        }

        public static int StepsFor(double seconds)
        {
            if (seconds <= 0) return 1;
            return Math.Max(1, (int)Math.Ceiling(seconds * StepsPerSecond - 1e-6));
        }

        // timed_out: the wall-clock limit or max_steps ran out
        public static bool TimedOut(string stop) { return stop == RunStop.Timeout || stop == RunStop.Steps; }

        // music_time: the music position (s), folded into [0, cycle) when cycle > 0, lies in [from, to); from > to wraps
        // around the cycle end (from 5.9 to 0.5 of a 6 s cycle)
        public static double MusicPhase(double musicS, double cycle)
        {
            if (cycle <= 0 || double.IsNaN(musicS)) return musicS;
            double p = musicS % cycle;
            return p < 0 ? p + cycle : p;
        }

        public static bool InWindow(double phase, double from, double to)
        {
            if (double.IsNaN(phase) || phase < 0) return false;
            if (from <= to) return phase >= from && phase < to;
            return phase >= from || phase < to;
        }

        // when (act / move_to / interact): the wall-clock ms the wait may use so the action itself still fits the call
        public static int WhenBudgetMs(double whenTimeoutS, int remainingMs, int actionReserveMs)
        {
            int want = (int)Math.Round(Math.Max(0.1, whenTimeoutS) * 1000);
            int left = remainingMs - actionReserveMs - StepperCore.ReplyReserveMs;
            return Math.Max(0, Math.Min(want, left));
        }

        // the wait_until result fields (ok first); changes / observe / job are added by the caller
        public static Json.Obj Result(WaitSet set, List<object> any, string stop, string detail, int waitedSteps, double elapsedMs, Json.Obj observed, List<string> warnings)
        {
            var o = new Json.Obj();
            o["ok"] = true;
            bool sat = stop == RunStop.Condition && set != null && set.Which >= 0;
            o["satisfied"] = sat;
            o["which"] = sat ? (double)set.Which : -1.0;
            if (sat && any != null && set.Which < any.Count && any[set.Which] is Json.Obj) o["condition"] = any[set.Which];
            o["timed_out"] = !sat && TimedOut(stop);
            o["waited_steps"] = (double)Math.Max(0, waitedSteps);
            o["elapsed_ms"] = Math.Round(Math.Max(0, elapsedMs), 1);
            o["observed"] = observed ?? new Json.Obj();
            o["stop"] = stop ?? "";
            o["detail"] = sat ? (set.What ?? detail ?? "") : (detail ?? "");
            var w = new List<object>();
            if (warnings != null) foreach (var s in warnings) if (!w.Contains(s)) w.Add(s);
            if (!sat && stop == RunStop.Died) w.Add("died: the body died before a condition held (add {dead:true} or {alive:true} to wait through it)");
            if (!sat && stop == RunStop.Loading) w.Add("loading: a load started before a condition held (wait for {ready:true})");
            if (!sat && stop == RunStop.Stalled) w.Add("stalled: " + detail);
            if (!sat && stop == RunStop.Cancelled) w.Add("cancelled: " + detail);
            o["warnings"] = w;
            return o;
        }
    }
}
