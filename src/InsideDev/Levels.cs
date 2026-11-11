using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
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
        // Phase 12: bumps whenever streaming changes (subscenes loaded / activated) or a level loads. Periodic
        // FindObjectsOfType scans elsewhere key off this instead of running on a timer.
        public static int StreamVersion;
        public static float StreamChangedAt;
        public static void Bump(string why) { StreamVersion++; StreamChangedAt = Time.realtimeSinceStartup; if (why == "level load") { relist = true; registry = null; nextRegistryTry = 0f; } }
        // true once when the version moved past 'seen' and streaming has been quiet for BackgroundSettle seconds (so a level
        // load triggers one scan at its end instead of one between every subscene), and never while mcam captures: the scans
        // are 30-170 ms main-thread hitches that break 60 Hz record pacing; they run after it. 'settle' can only ask for a
        // longer quiet time than BackgroundSettle, never a shorter one.
        public const float BackgroundSettle = 3f;
        public static bool Changed(ref int seen, float settle = 0.5f)
        {
            if (seen == StreamVersion || Time.realtimeSinceStartup - StreamChangedAt < Math.Max(settle, BackgroundSettle) || MultiCam.Running) return false;
            seen = StreamVersion; return true;
        }
        public static readonly Dictionary<string, string> state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static int loadedCount, activeCount;

        // The cullers come from the game's own registry (SubsceneCullingManager keeps every enabled culler in one list), so
        // no scene search is needed. The registry is reached through its private singleton field: asking the public
        // property would create the manager before the game does. Without that field the list is found with
        // FindObjectsOfType after a level load, when a culler is gone, or every RefindEvery seconds.
        // One sweep over all cullers is spread over SweepSeconds: a few cullers per frame, no per-frame garbage. The state
        // of each culler is kept, so the dictionary is written and the version bumped only when something changed.
        const float SweepSeconds = 1f, RefindEvery = 120f;
        const byte StUnloaded = 0, StStreaming = 1, StLoaded = 2, StActive = 3;
        static readonly string[] stateNames = { "unloaded", "streaming", "loaded", "active" };
        static SubsceneCuller[] cullers = new SubsceneCuller[0];
        static string[] cullerKey = new string[0];
        static byte[] cullerState = new byte[0];
        static int cullerCount, sweepAt, registrySize = -1;
        static float sweepAcc, lastScanAt, listedAt, nextScan, nextRegistryTry;
        static bool relist = true, relistNow, sweepChanged, registryTried;
        static FieldInfo registryField;
        static ReadOnlyCollection<SubsceneCuller> registry;

        static void FindRegistry(float now)
        {
            if (registry != null || now < nextRegistryTry) return;
            nextRegistryTry = now + 1f;
            if (!registryTried)
            {
                registryTried = true;
                try { registryField = typeof(SubsceneCullingManager).GetField("singletonInstance", BindingFlags.Static | BindingFlags.NonPublic); } catch { registryField = null; }
            }
            if (registryField == null) return;
            var m = registryField.GetValue(null) as SubsceneCullingManager;
            if (m != null) { registry = m.GetAllSubsceneCullers(); relist = true; }
        }

        static byte ReadState(SubsceneCuller c) { return c.IsActive ? StActive : c.IsLoaded ? StLoaded : c.InProgress ? StStreaming : StUnloaded; }

        static void Relist(float now)
        {
            relist = relistNow = false; listedAt = now;
            int n = 0;
            if (registry != null)
            {
                int rc = registry.Count;
                registrySize = rc;
                if (cullers.Length < rc) Grow(rc);
                for (int i = 0; i < rc; i++) { var c = registry[i]; if (c != null) cullers[n++] = c; }
            }
            else
            {
                var found = UObj.FindObjectsOfType(typeof(SubsceneCuller));
                if (cullers.Length < found.Length) Grow(found.Length);
                foreach (var o in found) { var c = o as SubsceneCuller; if (c != null) cullers[n++] = c; }
            }
            for (int i = n; i < cullerCount; i++) cullers[i] = null;
            cullerCount = n;
            state.Clear(); loadedCount = activeCount = 0;
            for (int i = 0; i < n; i++)
            {
                var c = cullers[i];
                string name = null;
                try { name = c.SceneName; } catch { }
                if (string.IsNullOrEmpty(name)) { cullerKey[i] = null; cullerState[i] = StUnloaded; continue; }
                string key = name.Substring(name.LastIndexOf('/') + 1);
                if (key.EndsWith(".unity")) key = key.Substring(0, key.Length - 6);
                cullerKey[i] = key;
                byte st = ReadState(c);
                cullerState[i] = st;
                if (st >= StLoaded) loadedCount++;
                if (st == StActive) activeCount++;
                state[key] = stateNames[st];
            }
            sweepAt = 0; sweepAcc = 0f;
        }

        static void Grow(int n)
        {
            n = Math.Max(n, 64);
            cullers = new SubsceneCuller[n]; cullerKey = new string[n]; cullerState = new byte[n];
            cullerCount = 0;
        }

        public static void ScanStreaming()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextScan) return;
            float dt = Mathf.Clamp(now - lastScanAt, 0f, SweepSeconds);
            lastScanAt = now;
            try
            {
                FindRegistry(now);
                // the registry is a live view of the game's list; a scene search (no registry) waits for mcam runs unless a
                // culler is gone
                bool mustRelist = registry != null ? relist || registry.Count != registrySize
                                                   : relistNow || ((relist || now - listedAt > RefindEvery) && !MultiCam.Running);
                if (mustRelist)
                {
                    int prevLoaded = loadedCount, prevActive = activeCount;
                    Relist(now);
                    sweepChanged = false;
                    if (loadedCount != prevLoaded || activeCount != prevActive) Signal();
                    return;
                }
                if (cullerCount == 0) return;
                sweepAcc += cullerCount * dt / SweepSeconds;
                int todo = (int)sweepAcc;
                if (todo <= 0) return;
                sweepAcc -= todo;
                if (todo > cullerCount) todo = cullerCount;
                for (; todo > 0; todo--)
                {
                    int i = sweepAt++;
                    var c = cullers[i];
                    if (c == null) relist = relistNow = true;
                    else if (cullerKey[i] != null)
                    {
                        byte st = ReadState(c), old = cullerState[i];
                        if (st != old)
                        {
                            cullerState[i] = st;
                            if ((st >= StLoaded) != (old >= StLoaded)) { loadedCount += st >= StLoaded ? 1 : -1; sweepChanged = true; }
                            if ((st == StActive) != (old == StActive)) { activeCount += st == StActive ? 1 : -1; sweepChanged = true; }
                            state[cullerKey[i]] = stateNames[st];
                        }
                    }
                    if (sweepAt >= cullerCount)
                    {
                        sweepAt = 0;
                        if (sweepChanged) { sweepChanged = false; Signal(); }
                    }
                }
            }
            catch (Exception e) { DevLog.Error("ScanStreaming", e); nextScan = now + 30f; relist = relistNow = true; }
        }

        static void Signal()
        {
            ObjectDatabase.MarkDirty("streaming " + loadedCount + " loaded / " + activeCount + " active");
            Bump("streaming");
        }

        // one line for the doctor: where the culler list comes from and what it holds
        public static string Status()
        {
            return Fmt.I(cullerCount) + " cullers from " + (registry != null ? "the game's registry" : "a scene search") + ", " + Fmt.I(activeCount) + " active / " + Fmt.I(loadedCount) + " loaded, version " + Fmt.I(StreamVersion);
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
