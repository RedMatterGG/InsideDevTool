using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Persisted editor preferences and layout (_mod\editor.cfg, key=value lines). Lives outside the renderer
    // and the UI toolkit so neither a renderer reset nor a scene change can lose it.
    // The file is replaced in one step (SafeFile), the first save of a session keeps the previous content as
    // editor.cfg.bak, and a file that could not be read is never replaced by the few values of the running session.
    public static class EditorState
    {
        static readonly Dictionary<string, string> kv = new Dictionary<string, string>();
        // keys set or removed while editor.cfg could not be read: what the file says about the others wins once it can be read
        static readonly HashSet<string> touched = new HashSet<string>();
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static string path;
        static bool loaded, dirty, readFailed, readWarned, backedUp;
        static int loadedCount;
        static float saveAt;

        static string Path_
        {
            get
            {
                if (path == null) path = Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "editor.cfg");
                return path;
            }
        }

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            DescribeKeys();
            bool exists = false, fromBackup = false; string text = null;
            try
            {
                exists = File.Exists(Path_);
                text = SafeFile.ReadAllTextOrBackup(Path_, out fromBackup);
            }
            catch (Exception e) { DevLog.Error("editor state load", e); }
            // the file is there but only its backup (or nothing) could be read: the backup's values are used for now, and
            // the file is not written before it could be read and merged
            readFailed = exists && (text == null || fromBackup);
            if (text != null)
            {
                Parse(text, kv);
                loadedCount = kv.Count;
                DevLog.Write("editor state: " + kv.Count + " value(s) from editor.cfg" + (fromBackup ? ".bak (editor.cfg was missing or unreadable)" : ""));
            }
            if (readFailed) DevLog.Write("editor state: editor.cfg exists but cannot be read; it is left untouched until it can be");
        }

        static void Parse(string text, Dictionary<string, string> into)
        {
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int i = line.IndexOf('=');
                if (i <= 0 || line[0] == '#') continue;
                string k = line.Substring(0, i).Trim();
                into[k] = line.Substring(i + 1).Trim();
            }
        }

        public static void Tick()
        {
            if (dirty && Time.realtimeSinceStartup >= saveAt) Save();
        }

        // writes pending changes now (quit, unload)
        public static void Flush() { if (dirty) Save(); }

        public static void Save()
        {
            dirty = false;
            try
            {
                if (readFailed)
                {
                    string old;
                    try { old = File.ReadAllText(Path_); }
                    catch (Exception e)
                    {
                        if (!readWarned) { readWarned = true; DevLog.Write("editor state: not saved, editor.cfg is still unreadable (" + e.Message + "); the settings of this session stay in memory and the save is tried again"); }
                        dirty = true; saveAt = Time.realtimeSinceStartup + 10f;
                        return;
                    }
                    // the file as it is now, plus what was set or removed in this session
                    var main = new Dictionary<string, string>();
                    Parse(old, main);
                    foreach (var k in touched) { string v; if (kv.TryGetValue(k, out v)) main[k] = v; else main.Remove(k); }
                    kv.Clear();
                    foreach (var e in main) kv[e.Key] = e.Value;
                    loadedCount = kv.Count;
                    readFailed = false; touched.Clear();
                }
                var sb = new StringBuilder("# InsideDev editor layout and preferences\n");
                var keys = new List<string>(kv.Keys); keys.Sort(StringComparer.Ordinal);
                foreach (var k in keys) sb.Append(k).Append('=').Append(kv[k]).Append('\n');
                SafeFile.WriteAllText(Path_, sb.ToString(), !backedUp && loadedCount > 0);
                backedUp = true;
            }
            catch (Exception e) { DevLog.Error("editor state save", e); }
        }

        // one line for the doctor: where the file is and whether it can be kept up to date
        public static string FileStatus(out bool ok)
        {
            Load();
            ok = !readFailed;
            return (readFailed ? "editor.cfg cannot be read, changes are not saved: " : kv.Count + " value(s), " + (dirty ? "save pending: " : "saved: ")) + Path_;
        }

        static void Mark() { if (!dirty) { dirty = true; saveAt = Time.realtimeSinceStartup + 1.5f; } }

        // a key with a blank, '=', '#' or a control character, or a value with a line break, would write a line of its own
        // into editor.cfg: refused for every caller (logged once)
        static bool badWarned;
        static bool Storable(string k, string v)
        {
            if (RemotePolicy.StoreKeyOk(k) && RemotePolicy.StoreValueOk(v)) return true;
            if (!badWarned) { badWarned = true; DevLog.Write("editor state: refused to store a key with a blank, '=', '#' or a control character, or a value with a line break (" + (RemotePolicy.StoreKeyOk(k) ? "value of " + k : "a key of " + (k ?? "").Length + " characters") + "); further refusals are not logged"); }
            return false;
        }

        public static void Set(string k, string v) { if (!Storable(k, v)) return; Load(); string old; if (kv.TryGetValue(k, out old) && old == v) return; kv[k] = v; if (readFailed) touched.Add(k); Mark(); }
        public static void Set(string k, float v) { Set(k, v.ToString("0.###", IC)); }
        public static void Set(string k, int v) { Set(k, v.ToString(IC)); }
        public static void Set(string k, bool v) { Set(k, v ? "1" : "0"); }

        public static void Remove(string k) { Load(); if (kv.Remove(k)) { if (readFailed) touched.Add(k); Mark(); } }
        public static bool Has(string k) { Load(); return kv.ContainsKey(k); }

        // Copy of every stored key and value, sorted by key (the cfg command).
        public static List<KeyValuePair<string, string>> All()
        {
            Load();
            var l = new List<KeyValuePair<string, string>>(kv);
            l.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return l;
        }

        // Known keys: every part describes the keys it reads (default as text, one line of help) once at start, so "cfg"
        // can list keys that are not stored yet and say what each one does. Main thread.
        public struct KeyInfo { public string key, def, text, part; }
        static readonly Dictionary<string, KeyInfo> known = new Dictionary<string, KeyInfo>();
        public static void Describe(string part, string key, string def, string text) { known[key] = new KeyInfo { key = key, def = def, text = text, part = part }; }
        public static bool Known(string key, out KeyInfo info) { return known.TryGetValue(key, out info); }
        public static List<KeyInfo> Described()
        {
            var l = new List<KeyInfo>(known.Values);
            l.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
            return l;
        }
        public static string Get(string k, string def) { Load(); string v; return kv.TryGetValue(k, out v) ? v : def; }
        public static float Get(string k, float def) { float f; return float.TryParse(Get(k, (string)null), NumberStyles.Float, IC, out f) ? f : def; }
        public static int Get(string k, int def) { int i; return int.TryParse(Get(k, (string)null), NumberStyles.Integer, IC, out i) ? i : def; }
        public static bool Get(string k, bool def) { var s = Get(k, (string)null); return s == null ? def : s == "1" || s == "true"; }

        public static void Reset(string prefix)
        {
            Load();
            var rm = new List<string>();
            foreach (var k in kv.Keys) if (k.StartsWith(prefix, StringComparison.Ordinal)) rm.Add(k);
            foreach (var k in rm) { kv.Remove(k); if (readFailed) touched.Add(k); }
            Mark();
        }

        // every stored key that starts with prefix (named layouts)
        public static List<string> Keys(string prefix)
        {
            Load();
            var l = new List<string>();
            foreach (var k in kv.Keys) if (k.StartsWith(prefix, StringComparison.Ordinal)) l.Add(k);
            l.Sort(StringComparer.Ordinal);
            return l;
        }

        // the keys read by the editor panels and the render host
        static void DescribeKeys()
        {
            const string P = "editor";
            Describe(P, "layout.mode", "dock", "editor shell: dock (three docks around the game view) or window (one floating window)");
            Describe(P, "dock.left.w", "0.3", "width of the left dock as a fraction of the screen, 0.12 to 0.6");
            Describe(P, "dock.right.w", "0.3", "width of the right dock as a fraction of the screen, 0.12 to 0.6");
            Describe(P, "dock.bottom.h", "0.3", "height of the bottom dock as a fraction of the screen, 0.1 to 0.7");
            foreach (var d in new[] { "left", "bottom", "right" })
            {
                Describe(P, "dock." + d + ".panels", "", "panel ids shown in the " + d + " dock, comma separated (empty: the built-in set)");
                Describe(P, "dock." + d + ".active", "0", "index of the tab shown in the " + d + " dock");
                Describe(P, "dock." + d + ".visible", "1", "1: the " + d + " dock is shown, 0: hidden");
            }
            Describe(P, "window.x", "30", "single window mode: left edge in UI pixels");
            Describe(P, "window.y", "70", "single window mode: top edge in UI pixels");
            Describe(P, "window.w", "760", "single window mode: width in UI pixels");
            Describe(P, "window.h", "580", "single window mode: height in UI pixels");
            Describe(P, "window.active", "0", "single window mode: index of the tab shown");
            Describe(P, "layouts", "", "names of the saved layouts, comma separated; each is stored under layout.<name>.* (ui layout save <name>)");
            Describe(P, "explorer.cat", "0", "Explorer search category: 0 all, 1 animation, 2 sounds, 3 scripts, 4 triggers, 5 state machines, 6 characters");
            Describe(P, "explorer.clutter", "0", "1: the Explorer search also lists static meshes, decals, bones and spawned FX clones");
            Describe(P, "explorer.path", "0", "1: the Explorer search also lists objects whose parent or level name matches");
            Describe(P, "graph.mode", "0", "Graph panel: 0 GAME, 1 WWISE, 2 COMBINED, 3 LOGIC");
            Describe(P, "graph.zoom", "1", "Graph panel zoom, 0.3 to 2.5 (used when graph.userzoom is 1)");
            Describe(P, "graph.userzoom", "0", "1: the Graph panel keeps the zoom the user chose instead of fitting each new graph");
            Describe(P, "graph.live.s", "1", "1: signals light up the Graph panel and fill its event strip");
            Describe(P, "graph.live.f", "1", "1: state machine changes light up the Graph panel and fill its event strip");
            Describe(P, "graph.live.a", "1", "1: sounds light up the Graph panel and fill its event strip");
            Describe(P, "graph.live.n", "1", "1: animation starts light up the Graph panel and fill its event strip");
            Describe(P, "gizmo.global", "0", "1: the move gizmo is shown for every selected object");
            Describe(P, "render.glyphcache", "1", "1: glyph positions of the editor font are kept between frames; 0: read from the font for every glyph of every frame (use it if editor text ever shows wrong letters)");
            Describe(P, "mode.author", "0", "1: Mod Author mode (selectors, Record as mod, the mod manager in the History panel)");
            foreach (var s in new[] { "activity", "materials", "levelfile", "atlas", "memory", "recent" })
                Describe(P, "insp.fold." + s, "1", "1: the Inspector section " + s.ToUpperInvariant() + " is unfolded, 0: folded (a folded section is not recomputed)");
        }
    }
}
