using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Phase 12: per-subsystem cost accounting + fail-safe.
    // Every per-frame subsystem runs through Perf.Run(name, action):
    //   - time is measured (Stopwatch ticks) and kept as a rolling window, split by editor open / closed
    //   - an exception is logged once per second at most; a subsystem that throws on 30 consecutive calls is
    //     switched off (fail-safe) instead of spamming the log every frame; "perf enable <name>" turns it back on
    public static class Perf
    {
        public sealed class Sub
        {
            public string name; public bool disabled; public int consecutiveFails, totalFails; public float lastErrLog;
            public long calls; public double totalMs, maxMs; public float maxAt;
            public readonly double[] win = new double[300]; public int wi; public int winCount;
            public double openMs, closedMs; public long openCalls, closedCalls;
            public long allocBytes, allocCalls; public bool nested;   // bytes allocated (calls without a GC in between); nested = inside another subsystem
            public double Avg { get { double s = 0; for (int i = 0; i < winCount; i++) s += win[i]; return winCount > 0 ? s / winCount : 0; } }
            public double WinMax { get { double m = 0; for (int i = 0; i < winCount; i++) if (win[i] > m) m = win[i]; return m; } }
        }

        static readonly Dictionary<string, Sub> subs = new Dictionary<string, Sub>();
        static readonly List<Sub> order = new List<Sub>();
        static readonly System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
        static readonly double tickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        public static bool editorOpen;
        public static double frameMs;            // sum of all subsystems this frame
        static double frameAcc; static int frameAccFrame = -1;
        public static readonly double[] frames = new double[300]; static int fi, fc;

        public static Sub Get(string name)
        {
            Sub s;
            if (!subs.TryGetValue(name, out s)) { s = new Sub { name = name }; subs[name] = s; order.Add(s); }
            return s;
        }

        public static int gcPauses, gcLogs, slowLogs; public static double gcMaxMs;
        public static void Run(string name, Action a) { Run(name, a, false); }
        // a part of another subsystem (editor panels): measured the same way, not added to the frame total again
        public static void RunNested(string name, Action a) { Run(name, a, true); }
        static void Run(string name, Action a, bool nested)
        {
            var s = Get(name);
            s.nested = nested;
            if (s.disabled) return;
            long mem0 = GC.GetTotalMemory(false);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int gc0 = GC.CollectionCount(0);
            try { a(); s.consecutiveFails = 0; }
            catch (Exception e)
            {
                s.consecutiveFails++; s.totalFails++;
                if (Time.realtimeSinceStartup - s.lastErrLog > 1f) { s.lastErrLog = Time.realtimeSinceStartup; DevLog.Error(name, e); }
                if (s.consecutiveFails >= 30) { s.disabled = true; DevLog.Write("[perf] FAIL-SAFE: " + name + " threw " + s.consecutiveFails + " times in a row and is now off (perf enable " + name + ")"); }
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * tickMs;
            s.calls++; s.totalMs += ms;
            if (GC.CollectionCount(0) != gc0)
            {
                // a garbage collection ran inside this subsystem's call: its pause is charged here but is caused by
                // every allocation in the process (game + mod)
                gcPauses++; if (ms > gcMaxMs) gcMaxMs = ms;
                if (ms > 30 && gcLogs++ < 30) DevLog.Write("[perf] " + ms.ToString("0.0") + " ms in " + name + " included a garbage collection (GC #" + GC.CollectionCount(0) + ", heap " + (GC.GetTotalMemory(false) >> 20) + " MB)");
            }
            else { long d = GC.GetTotalMemory(false) - mem0; if (d > 0) s.allocBytes += d; s.allocCalls++; }
            if (GC.CollectionCount(0) == gc0 && ms > 30 && slowLogs++ < 30) DevLog.Write("[perf] slow call: " + name + " " + ms.ToString("0.0") + " ms (no GC)");
            if (ms > s.maxMs) { s.maxMs = ms; s.maxAt = Time.realtimeSinceStartup; }
            s.win[s.wi] = ms; s.wi = (s.wi + 1) % s.win.Length; if (s.winCount < s.win.Length) s.winCount++;
            if (editorOpen) { s.openMs += ms; s.openCalls++; } else { s.closedMs += ms; s.closedCalls++; }
            if (nested) return;
            if (frameAccFrame != Time.frameCount) { if (frameAccFrame >= 0) { frameMs = frameAcc; frames[fi] = frameAcc; fi = (fi + 1) % frames.Length; if (fc < frames.Length) fc++; } frameAcc = 0; frameAccFrame = Time.frameCount; }
            frameAcc += ms;
        }

        public static bool Enable(string name, bool on)
        {
            Sub s;
            if (!subs.TryGetValue(name, out s)) return false;
            s.disabled = !on; s.consecutiveFails = 0;
            return true;
        }

        public static void Reset() { foreach (var s in order) { s.allocBytes = s.allocCalls = 0; s.calls = 0; s.totalMs = s.maxMs = 0; s.winCount = s.wi = 0; s.openMs = s.closedMs = 0; s.openCalls = s.closedCalls = 0; } fc = fi = 0; }

        public static double FrameAvg { get { double s = 0; for (int i = 0; i < fc; i++) s += frames[i]; return fc > 0 ? s / fc : 0; } }
        public static double FrameMax { get { double m = 0; for (int i = 0; i < fc; i++) if (frames[i] > m) m = frames[i]; return m; } }

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.Append("InsideDev cost per frame (last ").Append(fc).Append(" frames): avg ").Append(FrameAvg.ToString("0.000")).Append(" ms, max ").Append(FrameMax.ToString("0.00")).Append(" ms   editor ").Append(editorOpen ? "OPEN" : "closed").Append('\n');
            sb.Append("  garbage collections: ").Append(GC.CollectionCount(0)).Append(" since start (").Append((GC.CollectionCount(0) / Math.Max(1f, Time.realtimeSinceStartup / 60f)).ToString("0.0")).Append("/min), ").Append(gcPauses).Append(" ran inside mod calls (max ").Append(gcMaxMs.ToString("0.0")).Append(" ms), heap ").Append(GC.GetTotalMemory(false) >> 20).Append(" MB\n");
            var l = new List<Sub>(order);
            l.Sort((a, b) => b.Avg.CompareTo(a.Avg));
            sb.Append("  subsystem                avg ms    max(win)   max(all)  closed avg  open avg   fails  alloc KB/call\n");
            foreach (var s in l)
            {
                sb.Append("  ").Append(s.name.PadRight(22))
                  .Append(s.Avg.ToString("0.0000").PadLeft(9)).Append(s.WinMax.ToString("0.000").PadLeft(11)).Append(s.maxMs.ToString("0.00").PadLeft(11))
                  .Append((s.closedCalls > 0 ? (s.closedMs / s.closedCalls).ToString("0.0000") : "-").PadLeft(12))
                  .Append((s.openCalls > 0 ? (s.openMs / s.openCalls).ToString("0.0000") : "-").PadLeft(10))
                  .Append(s.totalFails.ToString().PadLeft(8)).Append((s.allocCalls > 0 ? (s.allocBytes / 1024.0 / s.allocCalls).ToString("0.0") : "-").PadLeft(15)).Append(s.nested ? "  (part of EditorUI)" : "").Append(s.disabled ? "  OFF (fail-safe)" : "").Append('\n');
            }
            return sb.ToString();
        }

        // cached variant for the Diagnostics panel: the report text is rebuilt twice a second, not every frame
        static float cacheAt; static string[] cacheLines = new string[0]; static Color[] cacheColors = new Color[0];
        public static void DrawCached(UI ui)
        {
            if (Time.realtimeSinceStartup >= cacheAt)
            {
                cacheAt = Time.realtimeSinceStartup + 0.5f;
                var ls = new List<string>(); foreach (var line in Report().Split('\n')) if (line.Length > 0) ls.Add(line);
                cacheLines = ls.ToArray(); cacheColors = new Color[cacheLines.Length];
                for (int i = 0; i < cacheLines.Length; i++) cacheColors[i] = cacheLines[i].Contains("OFF") ? new Color(1f, 0.45f, 0.4f, 1f) : cacheLines[i].StartsWith("InsideDev") ? UI.Accent : UI.Txt;
            }
            for (int i = 0; i < cacheLines.Length; i++) ui.Label(cacheLines[i], cacheColors[i]);
            ui.BeginRow();
            if (ui.Button("Reset stats")) { Reset(); cacheAt = 0; }
            foreach (var s in order) if (s.disabled && ui.Button("Re-enable " + s.name)) Enable(s.name, true);
            ui.EndRow();
        }

        public static void Draw(UI ui)
        {
            foreach (var line in Report().Split('\n'))
                if (line.Length > 0) ui.Label(line, line.Contains("OFF") ? new Color(1f, 0.45f, 0.4f, 1f) : line.StartsWith("InsideDev") ? UI.Accent : UI.Txt);
            ui.BeginRow();
            if (ui.Button("Reset stats")) Reset();
            foreach (var s in order) if (s.disabled && ui.Button("Re-enable " + s.name)) Enable(s.name, true);
            ui.EndRow();
        }
    }
}
