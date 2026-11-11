using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Commands of the bridge that concern the tool itself: quit, doctor, cfg, background, and the JSON forms of state and
    // where. Reached from the switch in Remote.cs, so they work through the file bridge, POST /cmd, the MCP command
    // tool and the in-game console alike. Also the core's rows of the doctor and the descriptions of the core's settings.
    public static class CoreBCommands
    {
        // ---------------------------------------------------------------- start-up (first frame of the bridge)
        public static void Init()
        {
            EditorState.Describe("core", "background", "auto", "when the background scanners run: auto (only while the tool is in use), always (as before 0.11.0), off (only on request)");
            EditorState.Describe("core", "ffmpeg.path", "", "full path of ffmpeg.exe; empty = _mod\\ffmpeg\\ffmpeg.exe, the PATH, C:\\ffmpeg\\bin");
            EditorState.Describe("core", "bridge.enabled", "1", "file bridge _mod\\cmd on (1) or off (0), read at start; `remote on|off` switches it for the session");
            EditorState.Describe("core", "bridge.state", "auto", "state.txt snapshot: auto (only while command files arrive), always (every 5 s whether anybody reads it or not)");
            EditorState.Describe("core", "audio.spy", "1", "0 leaves the wrapper around the game's sound engine out: no audio log, no audio rules, no cue injection (read at start)");
            EditorState.Describe("core", "audio.log", "auto", "audio log, callback log and caller stacks: auto (only while the tool is in use), always");
            EditorState.Describe("core", "audio.stacks", "important", "caller stacks of sound posts while the audio log is awake: off, important (first posts of each event), full; `wwise stacks` changes it for the session only");
            EditorState.Describe("core", "audio.timed", "auto", "scan and firing log of music-timed components: auto (only while the tool is in use), always");
            EditorState.Describe("core", "audio.catalog", "auto", "audio_catalog.txt grows with the areas you play through: auto (only while the tool is in use), always, off");
            Doctor.Register("core", CoreRows);
        }

        // settings a remote client must not change or read: they decide what the network side may do
        static bool Protected(string key)
        {
            return key.StartsWith("mcp.", StringComparison.Ordinal) || key == "play.cheats" || key == "play.save_guard" || key == "play.guide_dir" || key == "ffmpeg.path" || Secret(key);
        }
        static bool Secret(string key) { return key.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 || key.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0; }

        static void ConfigChanged(string key)
        {
            if (key == "background") Background.Reload();
            else if (key == "audio.log") AudioSpy.ReloadConfig();
            else if (key == "audio.stacks") AudioSpy.ReloadStacks();
            else if (key == "audio.timed") AudioTimed.ReloadConfig();
            else if (key == "audio.catalog") AudioCatalog.ReloadConfig();
            else if (key == "bridge.state") Remote.ReloadConfig();
        }

        // ---------------------------------------------------------------- per frame (two tests)
        static int logSeen;
        static string lastError;
        static readonly List<string> logScratch = new List<string>();
        static float quitAt = -1f;

        public static void Tick()
        {
            if (DevLog.Total != logSeen) CountErrors();
            if (quitAt > 0f && Time.realtimeSinceStartup >= quitAt)
            {
                quitAt = -1f;
                try { EditorState.Save(); } catch { }
                DevLog.Write("quit: closing the game (quit command)");
                Application.Quit();
            }
        }

        // ERROR lines of insidedev.log since start, counted as they are written (the log keeps only its last 400 lines)
        static void CountErrors()
        {
            logScratch.Clear();
            logSeen = DevLog.Since(logSeen, logScratch);
            for (int i = 0; i < logScratch.Count; i++)
            {
                string l = logScratch[i];
                if (l == null || !(l.StartsWith("ERROR", StringComparison.Ordinal) || l.StartsWith("[Exception]", StringComparison.Ordinal))) continue;
                int nl = l.IndexOf('\n');
                lastError = nl < 0 ? l : l.Substring(0, nl);
                if (lastError.Length > 200) lastError = lastError.Substring(0, 200) + "...";
            }
            logScratch.Clear();
        }

        public static int ErrorLines { get { return DevLog.Errors; } }

        // ---------------------------------------------------------------- commands
        public static bool IsJson(List<string> a) { return a.Count > 1 && string.Equals(a[a.Count - 1], "json", StringComparison.OrdinalIgnoreCase); }

        public static string Run(string c, List<string> a, DevCore core)
        {
            bool json = IsJson(a);
            int n = json ? a.Count - 1 : a.Count;   // words without the trailing json
            switch (c)
            {
                case "quit":
                    quitAt = Time.realtimeSinceStartup + 0.5f;
                    return "quitting: the game closes in half a second";
                case "doctor":
                    {
                        var rows = Doctor.Run();
                        return json ? Doctor.Json(rows) : Doctor.Text(rows);
                    }
                case "background":
                    {
                        string m = n > 1 ? a[1].ToLowerInvariant() : "status";
                        if (m != "status" && !Background.SetMode(m)) return "usage: background [status|auto|always|off] [json]";
                        if (!json) return Background.Status() + "\n" + Owners.Status();
                        var o = new Json.Obj();
                        o["ok"] = true; o["mode"] = Background.Mode; o["in_use"] = Background.InUse; o["panel_opened"] = Background.EverOpened;
                        var w = new List<object>();
                        foreach (var f in new[] { Background.Objects, Background.Audio, Background.Hidden, Background.Events, Background.Code, Background.Catalog, Background.Timed })
                            if (Background.Wanted(f)) w.Add(f);
                        o["wanted"] = w;
                        o["owners"] = Owners.Status();
                        return JsonLine.Write(o);
                    }
                case "cfg": return Cfg(a, n, json);
            }
            return null;
        }

        static string Shown(string key, string value) { return Secret(key) ? (string.IsNullOrEmpty(value) ? "(empty)" : "(set, not shown)") : value; }
        static string Def(EditorState.KeyInfo info) { return string.IsNullOrEmpty(info.def) ? "(empty)" : info.def; }

        const string BadKey = "refused: bad key (a key is letters, digits, '_', '.' and '-')";

        static string Cfg(List<string> a, int n, bool json)
        {
            string sub = n > 1 ? a[1].ToLowerInvariant() : "list";
            if (sub == "set" || sub == "reset")
            {
                if (n < 3 || (sub == "set" && n < 4)) return "usage: cfg set <key> <value> | cfg reset <key>";
                string key = a[2].Trim();
                if (!RemotePolicy.CfgKeyOk(key)) return BadKey;
                if (Protected(key.ToLowerInvariant()) && !Remote.Trusted)
                    return "refused: " + key + " is a protected setting and is not changed through MCP or the console; edit _mod\\editor.cfg, or send the command through the file bridge (_mod\\cmd)";
                EditorState.KeyInfo info;
                bool known = EditorState.Known(key, out info);
                string old = EditorState.Has(key) ? EditorState.Get(key, "") : null;
                string note = known ? "" : "   warning: no part describes this key (a typo is saved like any other key)";
                if (known && info.text != null && info.text.IndexOf("read at start", StringComparison.OrdinalIgnoreCase) >= 0) note = "   (read at start: takes effect when the game is started again)";
                if (sub == "reset")
                {
                    EditorState.Remove(key);
                    ConfigChanged(key);
                    return key + " reset" + (known ? " to its default " + Def(info) : "") + (old == null ? " (it was not stored)" : "") + note;
                }
                string value = string.Join(" ", a.GetRange(3, n - 3).ToArray());
                if (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0) return "refused: a value is one line";
                if (key == "background")
                {
                    if (!Background.SetMode(value)) return "refused: background is auto, always or off";
                }
                else
                {
                    EditorState.Set(key, value);
                    ConfigChanged(key);
                }
                return key + " = " + Shown(key, value) + (old != null && old != value ? "   (was " + Shown(key, old) + ")" : "") + note;
            }
            if (sub == "get")
            {
                if (n < 3) return "usage: cfg get <key> [json]";
                string key = a[2].Trim();
                if (!RemotePolicy.CfgKeyOk(key)) return BadKey;
                EditorState.KeyInfo info;
                bool known = EditorState.Known(key, out info);
                bool stored = EditorState.Has(key);
                if (!known && !stored) return "not found: no setting '" + key + "' (cfg lists them)";
                string value = stored ? EditorState.Get(key, "") : info.def ?? "";
                if (json) return JsonLine.Write(Row(key, value, known, info, stored));
                return key + " = " + Shown(key, value) + (stored ? "" : "   (default)") + (known ? "   [default " + Def(info) + "]  (" + info.part + ") " + info.text : "   (no part describes this key)");
            }
            if (sub != "list" && n > 2) return "usage: " + CoreBHelp.Usage(CoreBHelp.Find("cfg"));
            // list: every described key and every stored key; one optional word filters the keys
            string filter = sub == "list" ? (n > 2 ? a[2] : null) : (n > 1 ? a[1] : null);
            var stored2 = new Dictionary<string, string>();
            foreach (var kv in EditorState.All()) stored2[kv.Key] = kv.Value;
            var keys = new List<string>(stored2.Keys);
            var described = new Dictionary<string, EditorState.KeyInfo>();
            foreach (var d in EditorState.Described()) { described[d.key] = d; if (!stored2.ContainsKey(d.key)) keys.Add(d.key); }
            keys.Sort(StringComparer.Ordinal);
            var rows = new List<object>();
            var sb = new StringBuilder();
            int shown = 0, undescribed = 0;
            foreach (var key in keys)
            {
                if (filter != null && key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                EditorState.KeyInfo info;
                bool known = described.TryGetValue(key, out info);
                string value;
                bool isStored = stored2.TryGetValue(key, out value);
                if (!isStored) value = info.def ?? "";
                if (!known) undescribed++;
                shown++;
                if (json) { rows.Add(Row(key, value, known, info, isStored)); continue; }
                sb.Append("  ").Append(key).Append(" = ").Append(Shown(key, value));
                if (known) sb.Append(isStored ? "   [default " + Def(info) + "]" : "   (default)").Append("  (").Append(info.part).Append(") ").Append(info.text);
                sb.Append('\n');
            }
            if (json)
            {
                var o = new Json.Obj();
                o["ok"] = true; o["count"] = shown; o["keys"] = rows;
                return JsonLine.Write(o);
            }
            return "cfg: " + shown + " setting(s)" + (filter != null ? " with '" + filter + "'" : "") + ", " + undescribed + " stored without a description (layout values, keys of other versions, typos)\n" + sb +
                   "  cfg set <key> <value> saves; cfg reset <key> returns to the default";
        }

        static Json.Obj Row(string key, string value, bool known, EditorState.KeyInfo info, bool stored)
        {
            var o = new Json.Obj();
            o["key"] = key;
            o["value"] = Shown(key, value);
            o["stored"] = stored;
            if (known) { o["default"] = info.def ?? ""; o["part"] = info.part; o["text"] = info.text; }
            if (Protected(key)) o["protected"] = true;
            return o;
        }

        // ---------------------------------------------------------------- state json / where json
        static List<object> Vec(Vector3 v)
        {
            return new List<object> { Math.Round((double)v.x, 3), Math.Round((double)v.y, 3), Math.Round((double)v.z, 3) };
        }

        static void Boy(Json.Obj o)
        {
            var ch = G.MainCharacter;
            if (ch == null) { o["boy"] = null; }
            else
            {
                var b = new Json.Obj();
                b["name"] = ch.name; b["pos"] = Vec(ch.pos3); b["dead"] = ch.isDead;
                o["boy"] = b;
            }
            int sub, sp;
            if (G.CurrentSavepoint(out sub, out sp) && sub >= 0)
            {
                var s = new Json.Obj();
                s["sub"] = sub; s["sp"] = sp; s["label"] = G.SubsceneLabel(sub);
                o["savepoint"] = s;
            }
            else o["savepoint"] = null;
        }

        public static string WhereJson()
        {
            var o = new Json.Obj();
            o["ok"] = true;
            Boy(o);
            return JsonLine.Write(o);
        }

        public static string StateJson(DevCore core)
        {
            var o = new Json.Obj();
            o["ok"] = true;
            o["version"] = Boot.Version;
            o["frame"] = Time.frameCount;
            o["time"] = Math.Round((double)Time.realtimeSinceStartup, 2);
            Boy(o);
            o["god"] = G.God;
            o["timescale"] = Math.Round((double)G.TimeScale, 3);
            var st = new Json.Obj();
            st["active"] = Levels.activeCount; st["loaded"] = Levels.loadedCount;
            o["streaming"] = st;
            var active = new List<object>();
            foreach (var kv in Levels.state) if (kv.Value == "active") active.Add(kv.Key);
            o["active_scenes"] = active;
            try
            {
                var m = PersistentBehaviour<GlobalAudio>.instance.music;
                var mu = new Json.Obj();
                mu["pos"] = Math.Round((double)m.GetMusicPosition_s(), 3); mu["loop"] = Math.Round((double)m.GetLoopLength_s(), 3);
                o["music"] = mu;
            }
            catch { }
            var sw = Shockwave.Mgr;
            if (sw != null)
            {
                var s = new Json.Obj();
                s["in_cover"] = ForcePushManager.GetInCover(); s["zones"] = ForcePushManager.getActiveZoneCount(); s["nokill"] = Shockwave.noKill;
                s["blow_at"] = Math.Round((double)Shockwave.CheckerTime("blow"), 3); s["cycle"] = Math.Round((double)sw.musicCycleTime, 3);
                o["shockwave"] = s;
            }
            o["panel"] = core != null && core.Panel;
            o["bridge"] = Remote.enabled;
            o["run_in_background"] = Application.runInBackground;
            o["background"] = Background.Mode;
            o["in_use"] = Background.InUse;
            o["render_backend"] = RenderHost.active.ToString();
            o["changes"] = Changes.list.Count;
            o["errors"] = DevLog.Errors;
            return JsonLine.Write(o);
        }

        // timed json: the music-timed components that are loaded now, with their timers
        public static string TimedJson()
        {
            var list = new List<object>();
            foreach (var s in AudioTimed.systems)
            {
                if (s.comp == null) continue;
                var x = new Json.Obj();
                x["type"] = s.typeName;
                x["object"] = Inspector.PathOf(s.comp.transform);
                x["id"] = s.comp.gameObject.GetInstanceID();
                x["enabled"] = s.comp.enabled;
                x["active"] = s.comp.gameObject.activeInHierarchy;
                var timers = new List<object>();
                foreach (var t in s.timers)
                {
                    var tm = new Json.Obj();
                    tm["field"] = t.field;
                    tm["muted"] = t.muted;
                    tm["time"] = t.muted ? null : (object)Math.Round((double)t.original, 3);
                    tm["game_time"] = float.IsNaN(t.game) ? null : (object)Math.Round((double)t.game, 3);
                    tm["fired"] = t.fireCount;
                    timers.Add(tm);
                }
                x["timers"] = timers;
                list.Add(x);
            }
            var o = new Json.Obj();
            o["ok"] = true; o["count"] = list.Count; o["systems"] = list;
            return JsonLine.Write(o);
        }

        // ---------------------------------------------------------------- doctor: the core's rows
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetDiskFreeSpaceExW(string dir, out ulong freeForCaller, out ulong total, out ulong free);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")]
        static extern uint GetCurrentProcessId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr wnd, out uint pid);
        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr wnd);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr wnd);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LoaderInt();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LoaderInfo(IntPtr buf, int n);

        static IntPtr gameWindow;

        // The game's own top-level window (class UnityWndClass of this process). Zero when it cannot be found.
        public static IntPtr GameWindow()
        {
            try
            {
                uint pid;
                if (gameWindow != IntPtr.Zero && GetWindowThreadProcessId(gameWindow, out pid) != 0 && pid == GetCurrentProcessId()) return gameWindow;
                gameWindow = IntPtr.Zero;
                uint me = GetCurrentProcessId();
                IntPtr w = IntPtr.Zero;
                for (int i = 0; i < 64; i++)
                {
                    w = FindWindowExW(IntPtr.Zero, w, "UnityWndClass", null);
                    if (w == IntPtr.Zero) break;
                    if (GetWindowThreadProcessId(w, out pid) != 0 && pid == me) { gameWindow = w; break; }
                }
            }
            catch { gameWindow = IntPtr.Zero; }
            return gameWindow;
        }

        static string GameDir { get { return Path.GetDirectoryName(Application.dataPath); } }
        static string ModDir { get { return Path.Combine(GameDir, "_mod"); } }

        static string Gb(ulong bytes) { return Fmt.F(bytes / 1073741824.0, "0.0") + " GB"; }

        static readonly Dictionary<Assembly, string> moduleVersions = new Dictionary<Assembly, string>();
        // "Version" constant of a module, when one of its public types has one (the modules carry no assembly version)
        static string ModuleVersion(Assembly asm)
        {
            string v;
            if (moduleVersions.TryGetValue(asm, out v)) return v;
            v = "";
            try
            {
                foreach (var t in asm.GetTypes())
                {
                    var f = t.GetField("Version", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (f == null || f.FieldType != typeof(string) || !f.IsLiteral) continue;
                    v = f.GetValue(null) as string ?? "";
                    if (v.Length > 0 && v.Length < 24 && char.IsDigit(v[0])) break;
                    v = "";
                }
            }
            catch { }
            moduleVersions[asm] = v;
            return v;
        }

        static List<Doctor.Row> CoreRows()
        {
            var rows = new List<Doctor.Row>();
            const string P = "core";
            rows.Add(new Doctor.Row(P, "version", Doctor.Info, "InsideDev " + Boot.Version + ", Unity " + Application.unityVersion));

            // extension modules
            var seen = new List<Assembly>();
            foreach (var x in Extensions.loaded)
            {
                var asm = x.GetType().Assembly;
                if (seen.Contains(asm)) continue;
                seen.Add(asm);
                string file = "?";
                try { file = Path.GetFileName(asm.Location); } catch { }
                if (string.IsNullOrEmpty(file)) file = asm.GetName().Name + ".dll";
                string v = ModuleVersion(asm);
                rows.Add(new Doctor.Row(P, "module " + file, Doctor.Info, v.Length > 0 ? "version " + v : "loaded (the module names no version)"));
            }
            if (Extensions.loaded.Count == 0) rows.Add(new Doctor.Row(P, "modules", Doctor.Info, "no extension module loaded (InsideDev.Mcp.dll, InsideDev.Play.dll ... go into _mod; `ext load` picks them up)"));
            foreach (var p in Extensions.problems) rows.Add(new Doctor.Row(P, "module problem", Doctor.Warn, p, "replace the file with the one of this release, or remove it from _mod"));

            // loader
            try
            {
                IntPtr m = GetModuleHandleW(Path.Combine(GameDir, "version.dll"));
                IntPtr ver = m != IntPtr.Zero ? GetProcAddress(m, "WT_Version") : IntPtr.Zero;
                if (m == IntPtr.Zero || ver == IntPtr.Zero)
                    rows.Add(new Doctor.Row(P, "loader", Doctor.Warn, m == IntPtr.Zero ? "version.dll of the game folder is not the loaded one (the tool was started another way)" : "version.dll has no Wwise tracer (an old or foreign loader)", "copy bin\\version.dll of this release next to INSIDE.exe"));
                else
                {
                    int tracer = ((LoaderInt)Marshal.GetDelegateForFunctionPointer(ver, typeof(LoaderInt)))();
                    IntPtr infoFn = GetProcAddress(m, "WT_LoaderInfo");
                    if (infoFn == IntPtr.Zero)
                        rows.Add(new Doctor.Row(P, "loader", Doctor.Warn, "version.dll is older than 0.11.0 (tracer v" + Fmt.I(tracer) + "): it starts Mono's debugger agent on 127.0.0.1:55555 unless _mod\\nodebugger.flag exists", "copy bin\\version.dll of this release next to INSIDE.exe"));
                    else
                    {
                        IntPtr buf = Marshal.AllocHGlobal(64);
                        try
                        {
                            for (int i = 0; i < 8; i++) Marshal.WriteInt64(buf, i * 8, 0);
                            ((LoaderInfo)Marshal.GetDelegateForFunctionPointer(infoFn, typeof(LoaderInfo)))(buf, 8);
                            long dbg = Marshal.ReadInt64(buf, 32), port = Marshal.ReadInt64(buf, 40), mods = Marshal.ReadInt64(buf, 24);
                            rows.Add(new Doctor.Row(P, "loader", Doctor.Ok, "version.dll present, tracer v" + Fmt.I(tracer) + ", " + Fmt.I(mods) + " module(s) booted (log: _mod\\hook.log)"));
                            if (dbg != 0) rows.Add(new Doctor.Row(P, "debugger", Doctor.Warn, "Mono's debugger agent listens on 127.0.0.1:" + Fmt.I(port) + " (_mod\\debugger.flag): any local program can attach, and the game's code runs unoptimised", "delete _mod\\debugger.flag and restart when you do not debug"));
                            else rows.Add(new Doctor.Row(P, "debugger", Doctor.Ok, "Mono's debugger agent is off" + (File.Exists(Path.Combine(ModDir, "debugger.flag")) ? " (debugger.flag exists: it was created after the start, or nodebugger.flag overrides it)" : "")));
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                }
            }
            catch (Exception e) { rows.Add(new Doctor.Row(P, "loader", Doctor.Info, "could not be asked: " + e.GetType().Name + ": " + e.Message)); }

            // _mod folder and its drive
            try
            {
                string probe = Path.Combine(ModDir, "doctor_write_test.tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                rows.Add(new Doctor.Row(P, "_mod folder", Doctor.Ok, ModDir + " is writable"));
            }
            catch (Exception e) { rows.Add(new Doctor.Row(P, "_mod folder", Doctor.Fail, ModDir + " is not writable: " + e.Message, "move the game out of a protected folder, or give your user write access to _mod")); }
            try
            {
                ulong avail, total, free;
                if (GetDiskFreeSpaceExW(ModDir, out avail, out total, out free))
                    rows.Add(new Doctor.Row(P, "disk", avail < 2147483648UL ? Doctor.Warn : Doctor.Ok, Gb(avail) + " free of " + Gb(total) + " on the drive of _mod", "recordings need room: free some space or record to another drive"));
                else rows.Add(new Doctor.Row(P, "disk", Doctor.Info, "free space could not be read (error " + Fmt.I(Marshal.GetLastWin32Error()) + ")"));
            }
            catch (Exception e) { rows.Add(new Doctor.Row(P, "disk", Doctor.Info, "free space could not be read: " + e.Message)); }

            // ffmpeg
            try
            {
                string tried;
                string ff = Ffmpeg.Find(out tried);
                if (ff != null) rows.Add(new Doctor.Row(P, "ffmpeg", Doctor.Ok, ff));
                else rows.Add(new Doctor.Row(P, "ffmpeg", Doctor.Warn, "not found (tried: " + tried + "); only recording and encoding need it", "put ffmpeg.exe into _mod\\ffmpeg, or set ffmpeg.path in _mod\\editor.cfg"));
            }
            catch (Exception e) { rows.Add(new Doctor.Row(P, "ffmpeg", Doctor.Info, "search failed: " + e.Message)); }

            // window, focus, run in background
            bool runBg = Application.runInBackground;
            try
            {
                IntPtr w = GameWindow();
                if (w == IntPtr.Zero) rows.Add(new Doctor.Row(P, "window", Doctor.Info, "the game window was not found"));
                else if (IsIconic(w)) rows.Add(new Doctor.Row(P, "window", Doctor.Warn, "the game window is minimized: nothing is drawn (screenshots and recordings stand still)" + (runBg ? "" : " and the game is paused"), "restore the window"));
                else rows.Add(new Doctor.Row(P, "window", Doctor.Ok, (IsWindowVisible(w) ? "visible" : "hidden") + ", " + (GetForegroundWindow() == w ? "in front" : "not the focused window")));
            }
            catch (Exception e) { rows.Add(new Doctor.Row(P, "window", Doctor.Info, "could not be asked: " + e.Message)); }
            rows.Add(new Doctor.Row(P, "run in background", runBg ? Doctor.Ok : Doctor.Warn, runBg ? "on: the game keeps running while its window is not focused" : "off: the game, and every command sent to it, stops while its window is not focused", "bg on"));
            rows.Add(new Doctor.Row(P, "screen", Doctor.Info, Fmt.I(Screen.width) + "x" + Fmt.I(Screen.height) + (Screen.fullScreen ? " full screen" : " window") + ", vSync " + Fmt.I(QualitySettings.vSyncCount) + ", target frame rate " + Fmt.I(Application.targetFrameRate)));
            float ts = Time.timeScale;
            rows.Add(new Doctor.Row(P, "time scale", Mathf.Abs(ts - 1f) < 0.0001f ? Doctor.Ok : Doctor.Warn, Fmt.F(ts) + (ts == 0f ? " (the game is paused, or a step mode holds it)" : Mathf.Abs(ts - 1f) < 0.0001f ? "" : " (not the game's own speed)"), "ts 1"));

            rows.Add(new Doctor.Row(P, "background", Doctor.Info, Background.Status() + "; " + Owners.Status()));
            rows.Add(new Doctor.Row(P, "bridge", Doctor.Info, Remote.BridgeStatus()));
            int errorLines = DevLog.Errors;
            if (errorLines == 0) rows.Add(new Doctor.Row(P, "errors", Doctor.Ok, "no ERROR line in _mod\\insidedev.log since the start"));
            else rows.Add(new Doctor.Row(P, "errors", Doctor.Warn, Fmt.I(errorLines) + " ERROR line(s) in _mod\\insidedev.log since the start; the last: " + lastError, "read the log around it (the log tool, or the file)"));
            return rows;
        }
    }
}
