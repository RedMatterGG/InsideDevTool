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
            public string parent;                                      // the subsystem a nested one ran inside
            public double lastMs;                                      // the last call
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
        public static void Run(string name, Action a) { Run(Get(name), a, false); }
        // the same for a caller that keeps its Sub (no name lookup per call)
        public static void Run(Sub s, Action a) { Run(s, a, false); }
        // a part of another subsystem (editor panels, extension modules): measured the same way, not added to the frame total again
        public static void RunNested(string name, Action a) { Run(Get(name), a, true); }
        // the same for a caller that keeps its Sub (no name lookup per call)
        public static void RunNested(Sub s, Action a) { Run(s, a, true); }

        static Sub current;   // the top-level subsystem that is running (main thread)

        static void Run(Sub s, Action a, bool nested)
        {
            if (nested != s.nested || (nested && s.parent == null)) { s.nested = nested; s.parent = nested && current != null ? current.name : null; }
            if (s.disabled) return;
            string name = s.name;
            Sub outer = current;
            if (!nested) current = s;
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
            if (!nested) current = outer;
            s.calls++; s.totalMs += ms; s.lastMs = ms;
            int gc1 = GC.CollectionCount(0);
            if (gc1 != gc0)
            {
                // a garbage collection ran inside this subsystem's call: its pause is charged here but is caused by
                // every allocation in the process (game + mod)
                gcPauses++; if (ms > gcMaxMs) gcMaxMs = ms; if (ms > gcMaxSinceReset) gcMaxSinceReset = ms;
                if (ms > 30 && gcLogs++ < 30) DevLog.Write("[perf] " + Fmt.F(ms, "0.0") + " ms in " + name + " included a garbage collection (GC #" + gc1 + ", heap " + (GC.GetTotalMemory(false) >> 20) + " MB)");
            }
            else
            {
                long d = GC.GetTotalMemory(false) - mem0; if (d > 0) s.allocBytes += d; s.allocCalls++;
                if (ms > 30 && slowLogs++ < 30) DevLog.Write("[perf] slow call: " + name + " " + Fmt.F(ms, "0.0") + " ms (no GC)");
            }
            if (ms > s.maxMs) { s.maxMs = ms; s.maxAt = Time.realtimeSinceStartup; }
            s.win[s.wi] = ms; s.wi = (s.wi + 1) % s.win.Length; if (s.winCount < s.win.Length) s.winCount++;
            if (editorOpen) { s.openMs += ms; s.openCalls++; } else { s.closedMs += ms; s.closedCalls++; }
            if (nested) return;
            int frame = Time.frameCount;
            if (frameAccFrame != frame && editorOpen) Background.PanelOpen();   // once per frame: the open editor is the tool in use
            if (frameAccFrame != frame) { if (frameAccFrame >= 0) { frameMs = frameAcc; frames[fi] = frameAcc; fi = (fi + 1) % frames.Length; if (fc < frames.Length) fc++; framesSinceReset++; msSinceReset += frameAcc; } frameAcc = 0; frameAccFrame = frame; }
            frameAcc += ms;
        }
        static long framesSinceReset; static double msSinceReset;

        public static bool Enable(string name, bool on)
        {
            Sub s;
            if (!subs.TryGetValue(name, out s)) return false;
            s.disabled = !on; s.consecutiveFails = 0;
            return true;
        }

        static readonly List<Renderer> hiddenRenderers = new List<Renderer>();

        public static string Hide(string roots, string part)
        {
            bool byMesh = part.StartsWith("mesh:", StringComparison.Ordinal);
            string want = byMesh ? part.Substring(5) : part;
            int off = 0, kept = 0, shadows = 0, found = 0;
            long verts = 0;
            var meshes = new HashSet<int>();
            var mats = new HashSet<int>();
            var missing = new List<string>();
            foreach (var raw in roots.Split(','))
            {
                string rn = raw.Trim();
                if (rn.Length == 0) continue;
                var go = GameObject.Find(rn);
                if (go == null) { missing.Add(rn); continue; }
                found++;
                foreach (var r in go.GetComponentsInChildren<Renderer>(false))
                {
                    if (!r.enabled) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    var mesh = mf != null ? mf.sharedMesh : null;
                    if (want.Length > 0)
                    {
                        bool match = byMesh ? mesh != null && mesh.name.StartsWith(want, StringComparison.Ordinal) : r.gameObject.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!match) { kept++; continue; }
                    }
                    r.enabled = false;
                    hiddenRenderers.Add(r);
                    off++;
                    if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) shadows++;
                    if (mesh != null && meshes.Add(mesh.GetInstanceID())) verts += mesh.vertexCount;
                    foreach (var m in r.sharedMaterials) if (m != null) mats.Add(m.GetInstanceID());
                }
            }
            return "perf hide: " + off + " renderer(s) off under " + found + " root(s)" + (missing.Count > 0 ? " (not found: " + string.Join(", ", missing.ToArray()) + ")" : "") +
                   (want.Length > 0 ? ", " + kept + " not matching '" + part + "' kept" : "") + "; " + shadows + " shadow caster(s), " + meshes.Count + " mesh(es) with " + verts + " vertices, " +
                   mats.Count + " material(s); lights untouched; " + hiddenRenderers.Count + " hidden in total (perf show restores)";
        }

        public static string Show()
        {
            int n = 0;
            foreach (var r in hiddenRenderers) if (r != null) { r.enabled = true; n++; }
            hiddenRenderers.Clear();
            return "perf show: " + n + " renderer(s) back on";
        }

        static int gcAtReset = -1, gcPausesAtReset; static float resetAt; static double gcMaxSinceReset;
        public static void Reset() { foreach (var s in order) { s.allocBytes = s.allocCalls = 0; s.calls = 0; s.totalMs = s.maxMs = 0; s.winCount = s.wi = 0; s.openMs = s.closedMs = 0; s.openCalls = s.closedCalls = 0; } fc = fi = 0; framesSinceReset = 0; msSinceReset = 0; gcAtReset = GC.CollectionCount(0); gcPausesAtReset = gcPauses; gcMaxSinceReset = 0; resetAt = Time.realtimeSinceStartup; }

        public static double FrameAvg { get { double s = 0; for (int i = 0; i < fc; i++) s += frames[i]; return fc > 0 ? s / fc : 0; } }
        public static double FrameMax { get { double m = 0; for (int i = 0; i < fc; i++) if (frames[i] > m) m = frames[i]; return m; } }

        public static string Report()
        {
            var sb = new StringBuilder();
            int gcs = GC.CollectionCount(0);
            float now = Time.realtimeSinceStartup;
            sb.Append("InsideDev cost per frame (last ").Append(fc).Append(" frames): avg ").AppendF((float)FrameAvg, "0.000").Append(" ms, max ").AppendF((float)FrameMax, "0.00").Append(" ms   editor ").Append(editorOpen ? "OPEN" : "closed").Append('\n');
            sb.Append("  garbage collections: ").Append(gcs).Append(" since start (").AppendF(gcs / Math.Max(1f, now / 60f), "0.0").Append("/min), ").Append(gcPauses).Append(" ran inside mod calls (max ").AppendF((float)gcMaxMs, "0.0").Append(" ms), heap ").Append(GC.GetTotalMemory(false) >> 20).Append(" MB\n");
            if (gcAtReset >= 0) sb.Append("  since perf reset ").AppendF(now - resetAt, "0.0").Append(" s ago: ").Append(gcs - gcAtReset).Append(" garbage collection(s), ").Append(gcPauses - gcPausesAtReset).Append(" inside mod calls (max ").AppendF((float)gcMaxSinceReset, "0.0").Append(" ms)")
                .Append(framesSinceReset > 0 ? ", avg " + Fmt.F(msSinceReset / framesSinceReset, "0.000") + " ms per frame over " + framesSinceReset + " frames" : "").Append('\n');
            sb.Append("  background ").Append(Background.Mode).Append(Background.InUse ? ", tool in use" : ", tool idle (scanners asleep)").Append('\n');
            var l = new List<Sub>(order);
            l.Sort((a, b) => b.Avg.CompareTo(a.Avg));
            sb.Append("  subsystem                avg ms    max(win)   max(all)  closed avg  open avg   fails  alloc KB/call\n");
            foreach (var s in l)
            {
                sb.Append("  ").Append(s.name.PadRight(22))
                  .Append(Fmt.F(s.Avg, "0.0000").PadLeft(9)).Append(Fmt.F(s.WinMax, "0.000").PadLeft(11)).Append(Fmt.F(s.maxMs, "0.00").PadLeft(11))
                  .Append((s.closedCalls > 0 ? Fmt.F(s.closedMs / s.closedCalls, "0.0000") : "-").PadLeft(12))
                  .Append((s.openCalls > 0 ? Fmt.F(s.openMs / s.openCalls, "0.0000") : "-").PadLeft(10))
                  .Append(Fmt.I(s.totalFails).PadLeft(8)).Append((s.allocCalls > 0 ? Fmt.F(s.allocBytes / 1024.0 / s.allocCalls, "0.0") : "-").PadLeft(15)).Append(s.nested ? "  (part of " + (s.parent ?? "EditorUI") + ")" : "").Append(s.disabled ? "  OFF (fail-safe)" : "").Append('\n');
            }
            return sb.ToString();
        }

        // The same numbers as one JSON line ("perf json"). "frame" is the rolling window of the last frames, "sinceReset"
        // covers everything since "perf reset" (reset, wait, read: a measurement a script can repeat). Milliseconds and
        // bytes as numbers; rows are sorted by avgMs, nested rows (parts of another row) are not in the frame totals.
        public static string Json()
        {
            int gcs = GC.CollectionCount(0);
            float now = Time.realtimeSinceStartup;
            var o = new Json.Obj();
            o["ok"] = true; o["version"] = Boot.Version; o["editorOpen"] = editorOpen; o["background"] = Background.Mode; o["inUse"] = Background.InUse;
            var fr = new Json.Obj(); fr["frames"] = fc; fr["avgMs"] = FrameAvg; fr["maxMs"] = FrameMax;
            o["frame"] = fr;
            var gc = new Json.Obj(); gc["count"] = gcs; gc["perMinute"] = gcs / Math.Max(1f, now / 60f); gc["insideModCalls"] = gcPauses; gc["maxMs"] = gcMaxMs; gc["heapBytes"] = GC.GetTotalMemory(false);
            o["gc"] = gc;
            double allocFrame = 0;
            if (gcAtReset >= 0)
            {
                float sec = now - resetAt;
                var sr = new Json.Obj();
                sr["seconds"] = sec; sr["frames"] = framesSinceReset; sr["avgMs"] = framesSinceReset > 0 ? msSinceReset / framesSinceReset : 0;
                sr["gc"] = gcs - gcAtReset; sr["gcPerMinute"] = sec > 0.5f ? (gcs - gcAtReset) * 60.0 / sec : 0; sr["gcInsideModCalls"] = gcPauses - gcPausesAtReset; sr["gcMaxMs"] = gcMaxSinceReset;
                o["sinceReset"] = sr;
            }
            var l = new List<Sub>(order);
            l.Sort((a, b) => b.Avg.CompareTo(a.Avg));
            var rows = new List<object>();
            foreach (var s in l)
            {
                var r = new Json.Obj();
                r["name"] = s.name; r["calls"] = s.calls; r["avgMs"] = s.Avg; r["maxWindowMs"] = s.WinMax; r["maxMs"] = s.maxMs;
                r["totalAvgMs"] = s.calls > 0 ? s.totalMs / s.calls : 0;
                r["closedAvgMs"] = s.closedCalls > 0 ? (object)(s.closedMs / s.closedCalls) : null;
                r["openAvgMs"] = s.openCalls > 0 ? (object)(s.openMs / s.openCalls) : null;
                r["fails"] = s.totalFails;
                double alloc = s.allocCalls > 0 ? (double)s.allocBytes / s.allocCalls : 0;
                r["allocBytesPerCall"] = s.allocCalls > 0 ? (object)alloc : null;
                if (s.nested) { r["nested"] = true; r["parent"] = s.parent ?? "EditorUI"; }
                if (s.disabled) r["disabled"] = true;
                if (!s.nested) allocFrame += alloc;
                rows.Add(r);
            }
            // sum of the per-call averages of the top-level rows: what one frame of the tool allocates (an estimate:
            // each row runs at most once per frame, and calls interrupted by a collection are not counted)
            o["allocBytesPerFrame"] = allocFrame;
            o["rows"] = rows;
            return JsonLine.Write(o);
        }

        // bridge: perf [json] | reset | enable <subsystem> | hide <root>[,<root>...] [part | mesh:<prefix>] | show
        public static string Command(List<string> a)
        {
            string op = a.Count > 1 ? a[1].ToLowerInvariant() : "";
            if (op == "json") return Json();
            if (op == "reset") { Reset(); return a.Count > 2 && a[2] == "json" ? JsonLine.Obj("ok", true, "reset", true) : "perf stats reset"; }
            if (op == "enable") return a.Count > 2 ? (Enable(a[2], true) ? a[2] + " re-enabled" : "no subsystem " + a[2]) : "usage: perf enable <subsystem>";
            if (op == "hide") return a.Count > 2 ? Hide(a[2], a.Count > 3 ? a[3] : "") : "perf hide <root>[,<root>...] [name part | mesh:<mesh name prefix>]";
            if (op == "show") return Show();
            return Report();
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
                for (int i = 0; i < cacheLines.Length; i++) cacheColors[i] = cacheLines[i].Contains("OFF (fail-safe)") ? new Color(1f, 0.45f, 0.4f, 1f) : cacheLines[i].StartsWith("InsideDev") ? UI.Accent : UI.Txt;
            }
            for (int i = 0; i < cacheLines.Length; i++) ui.Label(cacheLines[i], cacheColors[i]);
            ui.BeginRow();
            if (ui.Button("Reset stats")) { Reset(); cacheAt = 0; }
            foreach (var s in order) if (s.disabled && ui.Button("Re-enable " + s.name)) Enable(s.name, true);
            ui.EndRow();
        }

        public static void Draw(UI ui) { DrawCached(ui); }
    }
}
