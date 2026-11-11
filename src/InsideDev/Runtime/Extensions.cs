using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace InsideDev
{
    // Optional add-ons in separate DLLs, so the game tool does not carry features that only some people use.
    // Every _mod\InsideDev.*.dll is loaded at boot; each public class implementing IInsideDevExtension is created,
    // started once, ticked every frame, and asked for bridge / console commands it does not recognise in the core.
    // Example: InsideDev.Mcp.dll (the MCP server for AI assistants).
    public interface IInsideDevExtension
    {
        string Name { get; }
        void Start(DevCore core);
        void Tick(DevCore core);
        // return null when the command is not for this extension
        string Command(List<string> args, DevCore core);
    }

    // Optional, next to IInsideDevExtension: an extension whose content lives in some levels only. Levels are keys of
    // Levels.state (scene file names such as "#fields_Gameplay"; read once when the extension is loaded). The core then
    // calls Tick only while at least one of them is loaded ("loaded" or "active"), starting within about a second after
    // the first one is loaded, and one last time right after the last one went away so the extension can release what
    // it bound. While
    // none is loaded the extension is not called at all. Start, Command and Dispose are not affected. For the settle
    // wait and the level's root use a LevelGate per level inside Tick.
    public interface IInsideDevLevelScoped
    {
        string[] Levels { get; }
    }

    public static class Extensions
    {
        public static readonly List<IInsideDevExtension> loaded = new List<IInsideDevExtension>();
        public static readonly List<string> problems = new List<string>();
        static bool done;

        // One entry per ticked extension: its own row in "perf" ("ext.<name>", part of Extensions) and a delegate made
        // once at load, so the per-frame dispatch allocates nothing. A level-scoped extension whose levels are not loaded
        // costs one comparison per frame.
        // Errors as in 0.10.0: an exception of Tick is logged ("[ext] <name>", at most once a second with the count of
        // the ones in between) and the extension keeps being ticked; an extension whose Start threw is still loaded,
        // ticked and asked for commands. editor.cfg ext.autoDisable = 1 switches off an extension whose Tick threw 30
        // times in a row ("perf enable ext.<name>" switches it back on) and does not tick one whose Start threw.
        public const string AutoDisableKey = "ext.autoDisable";
        const int AutoDisableAfter = 30;
        sealed class Slot
        {
            public IInsideDevExtension ext; public string name; public Perf.Sub sub; public Action tick; public bool reported;
            public string[] levels, levelsAlt;      // null = ticked every frame
            public bool loaded, release; public float nextCheck; public int wakeUps;
            public int slow; public float slowLogAt = -1000f; public int slowLogs;
            public int fails, failsInRow, unlogged; public float failLogAt = -1000f; public bool startFailed;
        }
        static Slot[] slots = new Slot[0];
        static bool autoDisable, autoRead;

        static bool AutoDisable
        {
            get
            {
                if (!autoRead) { autoRead = true; try { autoDisable = EditorState.Get(AutoDisableKey, false); EditorState.Describe("data", AutoDisableKey, "0", "1 = an extension module whose Tick throws 30 times in a row is switched off, one whose Start threw is not ticked (0 = as in 0.10.0: logged, kept running)"); } catch { } }
                return autoDisable;
            }
        }

        static void Failed(Slot s, Exception e)
        {
            s.fails++; s.failsInRow++; s.sub.totalFails++;
            float now = Time.realtimeSinceStartup;
            if (now - s.failLogAt >= 1f)
            {
                s.failLogAt = now;
                if (s.unlogged > 0) DevLog.Write("[ext] " + s.name + ": " + s.unlogged + " more exception(s) in Tick since the last one logged");
                s.unlogged = 0;
                DevLog.Error("[ext] " + s.name, e);
            }
            else s.unlogged++;
            if (AutoDisable && s.failsInRow >= AutoDisableAfter && !s.sub.disabled)
            {
                s.sub.disabled = true; s.sub.consecutiveFails = s.failsInRow;
                DevLog.Write("[ext] " + s.name + " threw " + s.failsInRow + " times in a row and is switched off (" + AutoDisableKey + "=1; perf enable " + s.sub.name + ")");
            }
        }
        const float LevelCheckEvery = 0.25f;
        const double SlowMs = 2.0; const int SlowFrames = 5; const float SlowLogEvery = 30f;

        public static string Dir { get { return Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"); } }

        public static void LoadAll(DevCore core)
        {
            if (done) return;
            done = true;
            // extensions reference "InsideDev": hand them the copy that is already loaded
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var want = new AssemblyName(e.Name).Name;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == want) return a;
                return null;
            };
            LoadNew(core);
        }

        static readonly HashSet<string> loadedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string RowName(string extName)
        {
            var sb = new System.Text.StringBuilder("ext.");
            foreach (char c in extName ?? "") sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            return sb.ToString();
        }

        static void AddSlot(IInsideDevExtension ext, string name, DevCore core)
        {
            var s = new Slot { ext = ext, name = name, sub = Perf.Get(RowName(name)) };
            // the exception is caught here, not by Perf: Perf's own fail-safe would switch the extension off
            s.tick = () => { try { ext.Tick(core); s.failsInRow = 0; } catch (Exception e) { Failed(s, e); } };
            var scoped = ext as IInsideDevLevelScoped;
            if (scoped != null)
            {
                string[] lv = null;
                try { lv = scoped.Levels; } catch (Exception e) { problems.Add(name + ": Levels failed (" + e.Message + "), ticked every frame"); }
                if (lv != null && lv.Length > 0)
                {
                    var keep = new List<string>();
                    foreach (var x in lv) if (!string.IsNullOrEmpty(x)) keep.Add(x);
                    if (keep.Count > 0)
                    {
                        s.levels = keep.ToArray(); s.levelsAlt = new string[s.levels.Length];
                        for (int i = 0; i < s.levels.Length; i++) s.levelsAlt[i] = LevelGate.AltName(s.levels[i]);
                    }
                }
            }
            var n = new Slot[slots.Length + 1];
            Array.Copy(slots, n, slots.Length);
            n[slots.Length] = s;
            slots = n;
        }

        // loads every _mod\InsideDev.*.dll not loaded yet (at boot, and with the "ext load" command without restarting)
        public static string LoadNew(DevCore core)
        {
            string[] files;
            try { files = Directory.GetFiles(Dir, "InsideDev.*.dll"); } catch (Exception e) { problems.Add("extension folder: " + e.Message); return "extension folder: " + e.Message; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            int before = loaded.Count;
            foreach (var f in files)
            {
                string file = Path.GetFileName(f);
                if (!loadedFiles.Add(file)) continue;
                try
                {
                    var asm = Assembly.LoadFrom(f);
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e)
                    {
                        // the loader exception names the missing dependency; the outer message never does
                        string why = e.LoaderExceptions != null && e.LoaderExceptions.Length > 0 && e.LoaderExceptions[0] != null ? e.LoaderExceptions[0].Message : e.Message;
                        problems.Add(file + ": " + why); DevLog.Write("[ext] " + file + ": " + why);
                        continue;
                    }
                    int n = 0;
                    foreach (var t in types)
                    {
                        if (t == null || t.IsAbstract || !typeof(IInsideDevExtension).IsAssignableFrom(t)) continue;
                        n++;
                        var ext = (IInsideDevExtension)Activator.CreateInstance(t);
                        string name; try { name = ext.Name; } catch { name = null; }
                        if (string.IsNullOrEmpty(name)) name = t.Name;
                        bool startFailed = false;
                        try { ext.Start(core); }
                        catch (Exception e)
                        {
                            startFailed = true;
                            DevLog.Error("[ext] start " + name, e);
                            if (AutoDisable)
                            {
                                // half started: never ticked, and stopped again if it can be
                                problems.Add(name + " (" + file + "): start failed: " + e.Message + " (not ticked: " + AutoDisableKey + "=1)");
                                var d = ext as IDisposable;
                                if (d != null) { try { d.Dispose(); } catch { } }
                                continue;
                            }
                            problems.Add(name + " (" + file + "): start failed: " + e.Message + " (still ticked, as in 0.10.0)");
                        }
                        loaded.Add(ext);
                        AddSlot(ext, name, core);
                        slots[slots.Length - 1].startFailed = startFailed;
                        DevLog.Write("[ext] loaded " + name + " from " + file);
                    }
                    if (n == 0) problems.Add(file + ": no extension class");
                }
                catch (Exception e) { problems.Add(file + ": " + e.Message); DevLog.Error("[ext] " + file, e); }
            }
            return (loaded.Count - before) + " new extension(s) loaded\n" + Summary();
        }

        // Extensions that hold threads or sockets implement IDisposable and are disposed when the game quits: a thread
        // still blocked in a socket call keeps Unity 5.0's Mono from shutting down and the game hangs on exit.
        static bool quit;
        public static bool Quitting { get { return quit; } }
        public static void Quit()
        {
            if (quit) return;
            quit = true;
            foreach (var ext in loaded)
            {
                var d = ext as IDisposable;
                if (d == null) continue;
                try { d.Dispose(); } catch (Exception e) { DevLog.Error("[ext] quit", e); }
            }
        }

        public static void Tick(DevCore core)
        {
            var l = slots;
            if (l.Length == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < l.Length; i++)
            {
                var s = l[i];
                if (s.sub.disabled)
                {
                    if (!s.reported) { s.reported = true; problems.Add(s.name + ": Tick failed " + s.sub.consecutiveFails + " times in a row, switched off (perf enable " + s.sub.name + ")"); }
                    continue;
                }
                if (s.reported) { s.reported = false; s.failsInRow = 0; }
                if (s.levels != null)
                {
                    if (now >= s.nextCheck)
                    {
                        s.nextCheck = now + LevelCheckEvery;
                        bool on = false;
                        for (int k = 0; k < s.levels.Length; k++) if (LevelGate.IsLoaded(s.levels[k], s.levelsAlt[k])) { on = true; break; }
                        if (on != s.loaded) { s.loaded = on; if (on) s.wakeUps++; else s.release = true; }
                    }
                    if (!s.loaded)
                    {
                        if (!s.release) continue;
                        s.release = false;   // one call after its last level went away
                    }
                }
                Perf.RunNested(s.sub, s.tick);
                if (s.sub.lastMs <= SlowMs) { s.slow = 0; continue; }
                if (++s.slow >= SlowFrames && now - s.slowLogAt >= SlowLogEvery)
                {
                    s.slowLogAt = now; s.slowLogs++;
                    DevLog.Write("[ext] SLOW: " + s.name + " took over " + Fmt.F(SlowMs, "0.#") + " ms per frame for " + s.slow + " frames in a row (last " + Fmt.F(s.sub.lastMs, "0.00") + " ms, average " + Fmt.F(s.sub.Avg, "0.000") + " ms): this extension is what slows the game down (details: ext, perf)");
                }
            }
        }

        public static string Command(List<string> a, DevCore core)
        {
            foreach (var x in loaded)
            {
                string r;
                try { r = x.Command(a, core); } catch (Exception e) { r = "ERROR in " + SafeName(x) + ": " + e.Message; }
                if (r != null) return r;
            }
            return null;
        }

        static string SafeName(IInsideDevExtension x) { try { return x.Name; } catch { return x.GetType().Name; } }

        static string LevelsText(Slot s)
        {
            if (s.levels == null) return "";
            var sb = new System.Text.StringBuilder("   levels: ");
            for (int i = 0; i < s.levels.Length; i++) sb.Append(i > 0 ? ", " : "").Append(s.levels[i]).Append(" (").Append(LevelGate.StateOf(s.levels[i])).Append(')');
            sb.Append(s.loaded ? "   [ticking]" : "   [asleep: none of its levels is loaded]");
            return sb.ToString();
        }

        // first line "<n> extension(s)", then one line per extension with its tick cost, then the problems
        public static string Summary()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(loaded.Count).Append(" extension(s)");
            foreach (var s in slots)
            {
                sb.Append("\n  ").Append(s.name);
                if (s.sub.calls > 0) sb.Append("   tick avg ").Append(Fmt.F(s.sub.Avg, "0.000")).Append(" ms, max ").Append(Fmt.F(s.sub.maxMs, "0.00")).Append(" ms");
                sb.Append(LevelsText(s));
                if (s.slowLogs > 0) sb.Append("   [slow: over ").Append(Fmt.F(SlowMs, "0.#")).Append(" ms per frame ").Append(s.slowLogs).Append(" time(s)]");
                if (s.fails > 0) sb.Append("   [").Append(s.fails).Append(" exception(s) in Tick]");
                if (s.startFailed) sb.Append("   [Start threw]");
                if (s.sub.disabled) sb.Append("   [switched off after errors: perf enable ").Append(s.sub.name).Append(']');
            }
            foreach (var p in problems) sb.Append("\n  problem: ").Append(p);
            return sb.ToString();
        }

        // the same as one JSON line ("ext json")
        public static string SummaryJson()
        {
            var o = new Json.Obj();
            o["ok"] = true; o["count"] = loaded.Count;
            var l = new List<object>();
            foreach (var s in slots)
            {
                var x = new Json.Obj();
                x["name"] = s.name; x["perfRow"] = s.sub.name; x["calls"] = s.sub.calls;
                x["avgMs"] = s.sub.Avg; x["maxMs"] = s.sub.maxMs; x["lastMs"] = s.sub.lastMs;
                x["fails"] = s.fails; x["disabled"] = s.sub.disabled; x["slowWarnings"] = s.slowLogs; x["startFailed"] = s.startFailed;
                x["levelScoped"] = s.levels != null;
                if (s.levels != null)
                {
                    var lv = new List<object>();
                    foreach (var name in s.levels) { var j = new Json.Obj(); j["level"] = name; j["state"] = LevelGate.StateOf(name); lv.Add(j); }
                    x["levels"] = lv; x["ticking"] = s.loaded; x["wakeUps"] = s.wakeUps;
                }
                l.Add(x);
            }
            o["extensions"] = l;
            o["problems"] = new List<object>(problems.ToArray());
            return JsonLine.Write(o);
        }

        // bridge: ext | ext load | ext json
        public static string CommandExt(List<string> a, DevCore core)
        {
            string op = a.Count > 1 ? a[1].ToLowerInvariant() : "";
            if (op == "load") { string r = LoadNew(core); return a.Count > 2 && a[2] == "json" ? SummaryJson() : r; }
            if (op == "json") return SummaryJson();
            return Summary();
        }

        // extensions that were loaded and are ticked / were switched off after errors (for the doctor check)
        public static int Ticked { get { int n = 0; foreach (var s in slots) if (!s.sub.disabled) n++; return n; } }
        public static int SwitchedOff { get { int n = 0; foreach (var s in slots) if (s.sub.disabled) n++; return n; } }
        public static int Asleep { get { int n = 0; foreach (var s in slots) if (s.levels != null && !s.loaded) n++; return n; } }
        public static int SlowWarnings { get { int n = 0; foreach (var s in slots) n += s.slowLogs; return n; } }
    }
}
