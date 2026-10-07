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
                if (waitMethod == null)
                {
                    waitMethod = typeof(SavepointManager.SavepointTask).GetMethod("WaitForSubscene", BindingFlags.NonPublic | BindingFlags.Static);
                    if (waitMethod == null) { DevLog.Write("discover: WaitForSubscene not found"); queue.Clear(); cur = null; return; }
                }
                bool unload = cur.action == Action.Discover;
                if (cur.action == Action.Catalog)
                {
                    try { var cul = SavepointManager.GetCuller(cur.sub); cur.wasUnloaded = cul != null && cul.LoadedState == SubsceneCuller.ELoadedState.Unloaded; } catch { }
                }
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
        }

        static readonly Dictionary<string, Entry> byName = new Dictionary<string, Entry>();
        static bool loaded, dirty;
        static float nextScan;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        static string PathFile { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "spawns.cache"); } }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(PathFile)) return;
                foreach (var line in File.ReadAllLines(PathFile))
                {
                    var f = line.Split('|');
                    if (f.Length < 4) continue;
                    var e = new Entry { name = f[0], count = int.Parse(f[1], IC) };
                    var ps = f[2].Split(';'); var inf = f[3].Split(';');
                    e.pos = new Vector3[e.count]; e.info = new string[e.count];
                    for (int i = 0; i < e.count; i++)
                    {
                        if (i < ps.Length)
                        {
                            var c = ps[i].Split(',');
                            if (c.Length == 3) e.pos[i] = new Vector3(float.Parse(c[0], IC), float.Parse(c[1], IC), float.Parse(c[2], IC));
                        }
                        e.info[i] = i < inf.Length ? inf[i] : "";
                    }
                    byName[e.name] = e;
                }
                DevLog.Write("spawn cache: " + byName.Count + " area(s) loaded from disk");
            }
            catch (Exception ex) { DevLog.Error("spawn cache load", ex); }
        }

        public static void SaveIfDirty()
        {
            if (!dirty) return;
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
                    for (int i = 0; i < e.count; i++) { if (i > 0) sb.Append(';'); sb.Append((e.info[i] ?? "").Replace("|", "/").Replace(";", ",")); }
                    sb.Append('\n');
                }
                File.WriteAllText(PathFile, sb.ToString());
            }
            catch (Exception ex) { DevLog.Error("spawn cache save", ex); }
        }

        public static void Record(int sub)
        {
            Load();
            int n;
            try { n = SavepointManager.GetSubsceneSavepointCount(sub); } catch { return; }
            if (n < 0) return;
            string name = SavepointManager.GetSubsceneName(sub);
            Entry old;
            byName.TryGetValue(name, out old);
            var e = new Entry { name = name, count = n, pos = new Vector3[n], info = new string[n] };
            bool anyPos = false;
            for (int i = 0; i < n; i++)
            {
                try { e.pos[i] = SavepointManager.GetSavepointPosition(sub, i); anyPos |= e.pos[i] != Vector3.zero; } catch { }
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
            if (old != null && old.count == n && SamePos(old, e)) return;
            byName[name] = e;
            dirty = true;
        }

        static bool SamePos(Entry a, Entry b)
        {
            for (int i = 0; i < a.count; i++) if ((a.pos[i] - b.pos[i]).sqrMagnitude > 0.0001f) return false;
            return true;
        }

        // periodic: record anything the game has learned on its own (normal play streaming)
        public static void Scan()
        {
            if (Time.realtimeSinceStartup < nextScan || !G.SavepointsReady) return;
            nextScan = Time.realtimeSinceStartup + 5f;
            Load();
            try
            {
                for (int s = 0; s < SavepointManager.SubsceneCount; s++)
                    if (SavepointManager.GetSubsceneSavepointCount(s) >= 0) Record(s);
                SaveIfDirty();
            }
            catch (Exception e) { DevLog.Error("spawn cache scan", e); nextScan = Time.realtimeSinceStartup + 60f; }
        }

        public static Entry Get(int sub)
        {
            Load();
            Entry e;
            try { return byName.TryGetValue(SavepointManager.GetSubsceneName(sub), out e) ? e : null; } catch { return null; }
        }

        public static int KnownAreas { get { Load(); return byName.Count; } }
    }
}
