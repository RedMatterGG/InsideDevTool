using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    // Build scene table parsed from INSIDE_Data/mainData (BuildSettings.levels).
    // Scene index 0 lives in mainData itself; scene index N (N>=1) is the file "level{N-1}".
    public static class Levels
    {
        public class Scene
        {
            public int index;
            public string path;      // Assets/Scenes/03_City/lineup/#lineup_Gameplay.unity
            public string name;      // #lineup_Gameplay
            public string area;      // 03_City
            public string baseName;  // lineup   (subscene name without '#' and suffix)
            public string part;      // Gameplay / Environment / Backdrop / other
            public string File { get { return index == 0 ? "mainData" : "level" + (index - 1); } }
        }

        public static readonly List<Scene> scenes = new List<Scene>();
        static readonly Dictionary<string, Scene> byName = new Dictionary<string, Scene>(StringComparer.OrdinalIgnoreCase);
        static bool loaded;

        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                byte[] d = File.ReadAllBytes(Path.Combine(Application.dataPath, "mainData"));
                int start = FindTable(d);
                if (start < 0) { DevLog.Write("levels: scene table not found in mainData"); return; }
                int count = BitConverter.ToInt32(d, start);
                int p = start + 4;
                for (int i = 0; i < count && p + 4 <= d.Length; i++)
                {
                    int len = BitConverter.ToInt32(d, p); p += 4;
                    string s = Encoding.UTF8.GetString(d, p, len); p += len; p = (p + 3) & ~3;
                    Add(i, s);
                }
                DevLog.Write("levels: " + scenes.Count + " build scenes (level0.." + "level" + (scenes.Count - 2) + " + mainData)");
            }
            catch (Exception e) { DevLog.Error("levels", e); }
        }

        static int FindTable(byte[] d)
        {
            byte[] pre = Encoding.ASCII.GetBytes("Assets/");
            for (int p = 4; p + 8 < d.Length; p += 4)
            {
                int len = BitConverter.ToInt32(d, p);
                if (len < 12 || len > 400 || p + 4 + len > d.Length) continue;
                bool ok = true;
                for (int k = 0; k < pre.Length; k++) if (d[p + 4 + k] != pre[k]) { ok = false; break; }
                if (!ok) continue;
                if (Encoding.ASCII.GetString(d, p + 4 + len - 6, 6) != ".unity") continue;
                int count = BitConverter.ToInt32(d, p - 4);
                if (count > 0 && count < 5000) return p - 4;
            }
            return -1;
        }

        static void Add(int i, string path)
        {
            var sc = new Scene { index = i, path = path };
            string file = path.Substring(path.LastIndexOf('/') + 1);
            if (file.EndsWith(".unity")) file = file.Substring(0, file.Length - 6);
            sc.name = file;
            var parts = path.Split('/');
            sc.area = parts.Length > 3 ? parts[2] : "";
            string b = file.TrimStart('#');
            sc.part = "";
            foreach (var suf in new[] { "_Gameplay", "_GamePlay", "_Gamelogic", "_Environment", "_Backdrop", "_Logic" })
                if (b.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) { sc.part = suf.Substring(1); b = b.Substring(0, b.Length - suf.Length); break; }
            sc.baseName = b;
            scenes.Add(sc);
            byName[file] = sc;
            if (!byName.ContainsKey(file.TrimStart('#'))) byName[file.TrimStart('#')] = sc;
        }

        public static Scene ByName(string name)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(name)) return null;
            Scene s;
            return byName.TryGetValue(name, out s) ? s : null;
        }

        // All build scenes that belong to a subscene / puzzle name (e.g. "lineup" -> Backdrop/Environment/Gameplay)
        public static List<Scene> ForSubscene(string subsceneName)
        {
            EnsureLoaded();
            var res = new List<Scene>();
            if (string.IsNullOrEmpty(subsceneName)) return res;
            string b = subsceneName.TrimStart('#');
            foreach (var suf in new[] { "_Gameplay", "_GamePlay", "_Environment", "_Backdrop" })
                if (b.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) { b = b.Substring(0, b.Length - suf.Length); break; }
            foreach (var s in scenes) if (string.Equals(s.baseName, b, StringComparison.OrdinalIgnoreCase)) res.Add(s);
            return res;
        }

        public static string Describe(Scene s) { return s == null ? "?" : s.name + " = " + s.File + " (scene " + s.index + ")"; }

        // ---------------------------------------------------------------- runtime streaming state
        static float nextScan, cullersAt = -100f;
        static readonly List<SubsceneCuller> cullers = new List<SubsceneCuller>();
        // Phase 12: bumps whenever streaming changes (subscenes loaded / activated) or a level loads. Periodic
        // FindObjectsOfType scans elsewhere key off this instead of running on a timer.
        public static int StreamVersion;
        public static float StreamChangedAt;
        public static void Bump(string why) { StreamVersion++; StreamChangedAt = Time.realtimeSinceStartup; if (why == "level load") cullersAt = -100f; }
        // true once when the version moved past 'seen' and streaming has been quiet for 'settle' seconds
        public static bool Changed(ref int seen, float settle = 0.5f)
        {
            if (seen == StreamVersion || Time.realtimeSinceStartup - StreamChangedAt < settle) return false;
            seen = StreamVersion; return true;
        }
        public static readonly Dictionary<string, string> state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static int loadedCount, activeCount;

        public static void ScanStreaming()
        {
            if (Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + 1f;
            int prevLoaded = loadedCount, prevActive = activeCount;
            state.Clear(); loadedCount = activeCount = 0;
            try
            {
                // the culler list itself only changes with level loads; re-find it every 30 s or when one is gone
                bool stale = Time.realtimeSinceStartup - cullersAt > 120f;   // level loads bump cullersAt via Bump()
                foreach (var c in cullers) if (c == null) stale = true;
                if (stale)
                {
                    cullers.Clear(); cullersAt = Time.realtimeSinceStartup;
                    foreach (var o in UObj.FindObjectsOfType(typeof(SubsceneCuller))) { var sc = o as SubsceneCuller; if (sc != null) cullers.Add(sc); }
                }
                foreach (var c in cullers)
                {
                    if (c == null) continue;
                    string n;
                    try { n = c.SceneName; } catch { continue; }
                    if (string.IsNullOrEmpty(n)) continue;
                    string st = c.IsActive ? "active" : c.IsLoaded ? "loaded" : c.InProgress ? "streaming" : "unloaded";
                    if (c.IsLoaded) loadedCount++;
                    if (c.IsActive) activeCount++;
                    string key = n.Substring(n.LastIndexOf('/') + 1);
                    if (key.EndsWith(".unity")) key = key.Substring(0, key.Length - 6);
                    state[key] = st;
                }
            }
            catch (Exception e) { DevLog.Error("ScanStreaming", e); nextScan = Time.realtimeSinceStartup + 30f; }
            if (loadedCount != prevLoaded || activeCount != prevActive) { ObjectDatabase.MarkDirty("streaming " + loadedCount + " loaded / " + activeCount + " active"); Bump("streaming"); }
        }

        public static string StateOf(Scene s)
        {
            string st;
            if (state.TryGetValue(s.name, out st)) return st;
            if (state.TryGetValue(s.name.TrimStart('#'), out st)) return st;
            return "";
        }
    }
}
