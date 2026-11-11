using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Savepoint info for a subscene only exists after its Gameplay scene has streamed in once (count = -1 before).
    // We drive Playdead's own SavepointManager.SavepointTask.WaitForSubscene(index, unload) to load areas on demand,
    // then optionally spawn / teleport. Results are cached to _mod/spawns.cache so the list is complete across sessions.
    public static class Discovery
    {
        public enum Action { Discover, Spawn, Teleport, Catalog }
        class Job { public int sub, sp; public Action action; public bool wasUnloaded; }

        static readonly Queue<Job> queue = new Queue<Job>();
        static Job cur;
        static IEnumerator it;
        static MethodInfo waitMethod;
        static float jobStart;
        public static int batchTotal, batchDone;
        public static string status = "";

        public static bool Busy { get { return cur != null || queue.Count > 0; } }

        public static void Enqueue(int sub, int sp, Action action)
        {
            queue.Enqueue(new Job { sub = sub, sp = sp, action = action });
            batchTotal++;
        }

        public static void DiscoverAll()
        {
            if (!G.SavepointsReady) return;
            int n = 0;
            for (int s = 0; s < SavepointManager.SubsceneCount; s++)
                if (SavepointManager.GetSubsceneSavepointCount(s) < 0) { Enqueue(s, 0, Action.Discover); n++; }
            DevLog.Write("discover: queued " + n + " unloaded area(s)");
        }

        public static void Cancel()
        {
            queue.Clear();
            DevLog.Write("discover: cancelled (" + batchDone + "/" + batchTotal + " done)");
            batchTotal = batchDone = 0;
            // let the current load finish so the culling manager ends in a consistent state
        }

        public static void TeleportToAreaCenter(int sub)
        {
            Bounds b;
            var ch = G.MainCharacter;
            if (!G.AreaBounds(sub, out b) || ch == null) { DevLog.Write("tp: no bounds for " + G.SubsceneLabel(sub)); return; }
            var p = b.center; p.z = ch.pos3.z;
            DevLog.Write("tp: " + G.SubsceneLabel(sub) + " has no savepoints - moving to area centre (may be inside geometry)");
            G.Teleport(p);
        }

        public static void Update()
        {
            if (!G.SavepointsReady) return;
            try { if (GameManager.IsLoading()) return; } catch { }
            if (cur == null)
            {
                if (queue.Count == 0) { status = ""; batchTotal = batchDone = 0; return; }
                cur = queue.Dequeue();
                if (cur.sub < 0 || cur.sub >= SavepointManager.SubsceneCount)
                {
                    DevLog.Write("discover: no area " + cur.sub + " (areas are 0.." + (SavepointManager.SubsceneCount - 1) + ")");
                    cur = null; batchDone++;
                    return;
                }
                if (waitMethod == null)
                {
                    waitMethod = typeof(SavepointManager.SavepointTask).GetMethod("WaitForSubscene", BindingFlags.NonPublic | BindingFlags.Static);
                    if (waitMethod == null) { DevLog.Write("discover: WaitForSubscene not found"); queue.Clear(); cur = null; return; }
                }
                // an area that is loaded now (the boy stands in it, or the game streams it) is never unloaded behind the game's
                // back; when its state cannot be read, Discover unloads as it always did
                bool stateKnown = false;
                try
                {
                    var cul = SavepointManager.GetCuller(cur.sub);
                    if (cul != null) { stateKnown = true; cur.wasUnloaded = cul.LoadedState == SubsceneCuller.ELoadedState.Unloaded; }
                }
                catch { }
                bool unload = cur.action == Action.Discover && (!stateKnown || cur.wasUnloaded);
                try { it = (IEnumerator)waitMethod.Invoke(null, new object[] { cur.sub, unload }); }
                catch (Exception e) { DevLog.Error("discover start " + cur.sub, e); cur = null; return; }
                jobStart = Time.realtimeSinceStartup;
            }
            status = (cur.action == Action.Discover ? "Discovering " : cur.action == Action.Catalog ? "Cataloguing " : "Loading ") + G.SubsceneLabel(cur.sub) +
                     (batchTotal > 1 ? "   (" + (batchDone + 1) + "/" + batchTotal + ")" : "");
            bool more;
            try { more = it.MoveNext(); }
            catch (Exception e) { DevLog.Error("discover " + cur.sub, e); more = false; }
            if (more && Time.realtimeSinceStartup - jobStart < 60f) return;
            Finish();
        }

        static void Finish()
        {
            var j = cur; cur = null; it = null; batchDone++;
            if (j.action == Action.Catalog)
            {
                try
                {
                    AudioTimed.Scan(true);
                    AudioLinks.Scan(true);
                    AudioCatalog.RecordLoaded();
                }
                catch (Exception e) { DevLog.Error("catalog scan " + j.sub, e); }
                if (j.wasUnloaded)
                {
                    try
                    {
                        var cul = SavepointManager.GetCuller(j.sub);
                        if (cul != null) { cul.DeactivateSubsceneImmediate(); cul.UnloadSubsceneImmediate(); }
                    }
                    catch (Exception e) { DevLog.Error("catalog unload " + j.sub, e); }
                }
                SpawnCache.Record(j.sub);
                SpawnCache.SaveIfDirty();
                DevLog.Write("catalog: " + G.SubsceneLabel(j.sub) + " done");
                return;
            }
            int count = SavepointManager.GetSubsceneSavepointCount(j.sub);
            SpawnCache.Record(j.sub);
            SpawnCache.SaveIfDirty();
            DevLog.Write("discover: " + G.SubsceneLabel(j.sub) + " -> " + (count < 0 ? "no savepoint data" : count + " savepoint(s)"));
            if (count == 0 && j.action == Action.Teleport) { TeleportToAreaCenter(j.sub); return; }
            if (count == 0 && j.action == Action.Spawn) { G.LoadSavepoint(j.sub, 0); return; } // falls back to nearest savepoint
            if (count <= 0) return;
            int sp = Mathf.Clamp(j.sp, 0, count - 1);
            if (j.action == Action.Spawn) G.LoadSavepoint(j.sub, sp);
            else if (j.action == Action.Teleport && DevCore.Instance != null) DevCore.Instance.TeleportToSpawn(j.sub, sp);
        }
    }

    public static class SpawnCache
    {
        public class Entry
        {
            public string name;
            public int count;
            public Vector3[] pos;
            public string[] info;
            public bool atlas;      // from the level atlas (area not discovered yet), never written to spawns.cache
        }

        static readonly Dictionary<string, Entry> byName = new Dictionary<string, Entry>();
        static bool loaded, dirty, loadFailed, loadFailLogged;
        static float nextScan;
        static int[] seenCount;
        static int seenStream = -1;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        // more savepoints than any area has: a count above it in the cache file is a damaged line
        const int MaxSavepoints = 4096;

        static string PathFile { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "spawns.cache"); } }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            string text;
            bool fromBackup;
            try { text = SafeFile.ReadAllTextOrBackup(PathFile, out fromBackup); }
            catch (Exception ex)
            {
                // the file is there but could not be read: never write a shorter list over it
                loadFailed = true;
                DevLog.Error("spawn cache load", ex);
                return;
            }
            if (text == null) return;
            int bad = 0;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                var e = ParseLine(line);
                if (e == null) { bad++; continue; }
                byName[e.name] = e;
            }
            if (fromBackup) dirty = true;
            DevLog.Write("spawn cache: " + byName.Count + " area(s) loaded from disk" + (bad > 0 ? ", " + bad + " unreadable line(s) skipped" : "") +
                         (fromBackup ? " (spawns.cache was unreadable, its .bak copy was used)" : ""));
        }

        // name|count|x,y,z;x,y,z;...|info;info;...   null for a line that is not one (a bad position alone reads as unknown)
        internal static Entry ParseLine(string line)
        {
            var f = line.Split('|');
            if (f.Length < 4 || f[0].Length == 0) return null;
            int count;
            if (!Fmt.TryI(f[1], out count) || count < 0 || count > MaxSavepoints) return null;
            var e = new Entry { name = f[0], count = count, pos = new Vector3[count], info = new string[count] };
            var ps = f[2].Split(';'); var inf = f[3].Split(';');
            for (int i = 0; i < count; i++)
            {
                if (i < ps.Length)
                {
                    var c = ps[i].Split(',');
                    float x, y, z;
                    if (c.Length == 3 && Fmt.TryF(c[0], out x) && Fmt.TryF(c[1], out y) && Fmt.TryF(c[2], out z)) e.pos[i] = new Vector3(x, y, z);
                }
                e.info[i] = i < inf.Length ? inf[i] : "";
            }
            return e;
        }

        public static void SaveIfDirty()
        {
            if (!dirty) return;
            if (loadFailed)
            {
                if (!loadFailLogged) { loadFailLogged = true; DevLog.Write("spawn cache: not saved, spawns.cache could not be read at start (the file is left as it is)"); }
                return;
            }
            dirty = false;
            try
            {
                var sb = new StringBuilder();
                foreach (var e in byName.Values)
                {
                    sb.Append(e.name).Append('|').Append(e.count.ToString(IC)).Append('|');
                    for (int i = 0; i < e.count; i++)
                    {
                        if (i > 0) sb.Append(';');
                        sb.Append(e.pos[i].x.ToString("R", IC)).Append(',').Append(e.pos[i].y.ToString("R", IC)).Append(',').Append(e.pos[i].z.ToString("R", IC));
                    }
                    sb.Append('|');
                    for (int i = 0; i < e.count; i++) { if (i > 0) sb.Append(';'); sb.Append((e.info[i] ?? "").Replace("|", "/").Replace(";", ",").Replace("\n", " ").Replace("\r", " ")); }
                    sb.Append('\n');
                }
                SafeFile.WriteAllText(PathFile, sb.ToString(), true);
            }
            catch (Exception ex) { DevLog.Error("spawn cache save", ex); }
        }

        public static void Record(int sub)
        {
            Load();
            int n;
            try { n = SavepointManager.GetSubsceneSavepointCount(sub); } catch { return; }
            if (n < 0 || n > MaxSavepoints) return;
            string name = SavepointManager.GetSubsceneName(sub);
            Entry old;
            byName.TryGetValue(name, out old);
            if (old != null && old.count == n)
            {
                // nothing new is the usual case: compare in place and allocate nothing
                bool same = true;
                for (int i = 0; i < n && same; i++)
                {
                    Vector3 p = Vector3.zero;
                    try { p = SavepointManager.GetSavepointPosition(sub, i); } catch { }
                    if (p == Vector3.zero) continue;   // not known to the game right now: the cached position stays
                    if ((p - old.pos[i]).sqrMagnitude > 0.0001f) same = false;
                }
                if (same) return;
            }
            var e = new Entry { name = name, count = n, pos = new Vector3[n], info = new string[n] };
            for (int i = 0; i < n; i++)
            {
                try { e.pos[i] = SavepointManager.GetSavepointPosition(sub, i); } catch { }
                if (e.pos[i] == Vector3.zero && old != null && i < old.count) e.pos[i] = old.pos[i];
                string inf = "";
                try
                {
                    inf = SavepointManager.GetSavepointCharacterType(sub, i).ToString();
                    if (SavepointManager.IsSavepointCheckpointOnly(sub, i)) inf += ", checkpoint-only";
                }
                catch { }
                e.info[i] = inf;
            }
            byName[name] = e;
            dirty = true;
        }

        // periodic: record anything the game has learned on its own (normal play streaming). An area is read again only
        // when its savepoint count changed, and every known area once after a streaming change (positions arrive then).
        public static void Scan()
        {
            if (Time.realtimeSinceStartup < nextScan || !G.SavepointsReady) return;
            nextScan = Time.realtimeSinceStartup + 5f;
            Load();
            try
            {
                int subs = SavepointManager.SubsceneCount;
                if (seenCount == null || seenCount.Length != subs)
                {
                    seenCount = new int[subs];
                    for (int s = 0; s < subs; s++) seenCount[s] = int.MinValue;
                }
                bool all = Levels.Changed(ref seenStream);
                for (int s = 0; s < subs; s++)
                {
                    int n = SavepointManager.GetSubsceneSavepointCount(s);
                    if (n < 0 || (!all && n == seenCount[s])) continue;
                    seenCount[s] = n;
                    Record(s);
                }
                SaveIfDirty();
            }
            catch (Exception e) { DevLog.Error("spawn cache scan", e); nextScan = Time.realtimeSinceStartup + 60f; }
        }

        public static Entry Get(int sub)
        {
            Load();
            Entry e;
            try { string name = SavepointManager.GetSubsceneName(sub); return byName.TryGetValue(name, out e) ? e : FromAtlas(name); } catch { return null; }
        }

        static readonly Dictionary<string, Entry> fromAtlas = new Dictionary<string, Entry>();
        static Entry FromAtlas(string name)
        {
            Entry e;
            if (fromAtlas.TryGetValue(name, out e)) return e;
            Atlas.EnsureLoaded();
            var a = Atlas.Ready ? Atlas.AreaNamed(name) : null;
            if (a != null && a.savepoints >= 0)
            {
                e = new Entry { name = name, count = a.savepoints, pos = new Vector3[a.savepoints], info = new string[a.savepoints], atlas = true };
                for (int i = 0; i < e.count; i++) e.info[i] = "";
                var sp = Atlas.SpawnsOf(name);
                if (sp != null) foreach (var x in sp) if (x.i >= 0 && x.i < e.count) { e.pos[x.i] = x.pos; e.info[x.i] = x.info; }
            }
            fromAtlas[name] = e;
            return e;
        }

        public static int KnownAreas { get { Load(); return byName.Count; } }
    }
}
