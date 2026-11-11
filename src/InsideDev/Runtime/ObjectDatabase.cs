using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    [Flags]
    public enum ObjKind
    {
        None = 0,
        Trigger = 1 << 0, Collider = 1 << 1, Camera = 1 << 2, Light = 1 << 3, Audio = 1 << 4,
        StateMachine = 1 << 5, Signal = 1 << 6, Animation = 1 << 7, Savepoint = 1 << 8, Character = 1 << 9,
        Renderer = 1 << 10, Script = 1 << 11, Group = 1 << 12,
    }

    // Editor-side record of one live GameObject. Instance ids are runtime identities only (never persisted);
    // persistent references use ObjectSelector.
    public sealed class ObjRecord
    {
        public int id;                  // Unity instance id of the GameObject (session only)
        public GameObject go;
        public Transform t;
        public string name, nameLower, path, pathLower, scene;
        public int depth, parentId;
        public bool activeSelf, activeInHierarchy;
        public int layer;
        public string tag;
        public Vector3 pos;
        public string[] comps = Empty;  // component type names
        public ObjKind kind;
        public float discoveredAt;      // realtimeSinceStartup
        public int discoveredFrame, seenPass, childCount, compCount;
        public string parentPathSeen;   // the parent's path object this record's path was built from (walk bookkeeping)
        public static readonly string[] Empty = new string[0];

        public bool Alive { get { return go != null; } }
        public string KindLabel { get { return kind == ObjKind.None ? "object" : kind.ToString().Replace(", ", "|"); } }
        public bool Has(string componentType) { foreach (var c in comps) if (c == componentType) return true; return false; }
    }

    // RuntimeObjectDatabase: incremental, frame-budgeted scan of every loaded GameObject (active and inactive).
    // Unity APIs are main-thread only, so work is chunked across frames instead of threads. Scanning runs only
    // while something wants it (editor open, bridge activity, explicit request) — zero cost otherwise. Nothing demands
    // it on its own: after a sleep the first demanded pass starts at once and refreshes the root set, so the snapshot
    // catches up with whatever streamed in or out meanwhile. Readers that must not see an old snapshot use EnsureFresh.
    public static class ObjectDatabase
    {
        public static readonly Dictionary<int, ObjRecord> byId = new Dictionary<int, ObjRecord>(32768);
        static readonly Dictionary<string, List<ObjRecord>> byPath = new Dictionary<string, List<ObjRecord>>(32768);
        public static readonly List<ObjRecord> all = new List<ObjRecord>(32768);   // stable snapshot of the last completed pass
        static readonly Dictionary<string, List<ObjRecord>> byName = new Dictionary<string, List<ObjRecord>>(8192);
        static int byNameStamp = -1, changeStamp;   // changeStamp moves when a pass added, removed or renamed something

        public static int pass, completedPasses, removedLastPass, addedLastPass;
        public static float lastPassMs, lastPassEnd = -1f, lastRootMs;
        public static int lastPassFrames, roots;
        public static float budgetMs = 1.5f;
        public static string dirtyReason = "startup";
        static bool dirty = true;
        static float demandUntil = -1f;
        static float nextPassAt;

        // walk state
        struct Item { public Transform t; public int depth, parentId; public string scene, parentPath; }
        static readonly Stack<Item> stack = new Stack<Item>(4096);
        static bool walking;
        static int passStartFrame;
        static float passMs;
        static readonly Stopwatch sw = new Stopwatch();

        public static bool Ready { get { return completedPasses > 0; } }
        public static float Age { get { return lastPassEnd < 0 ? 1e9f : Time.realtimeSinceStartup - lastPassEnd; } }
        public static bool Scanning { get { return walking; } }
        // somebody asked for the database in the last seconds (it sleeps otherwise)
        public static bool Demanded { get { return Time.realtimeSinceStartup < demandUntil; } }

        // ---------------------------------------------------------------- demand / invalidation
        // main thread; whoever asks for the database is using the tool, so the scanners that feed on it wake up too
        public static void Demand(float seconds) { demandUntil = Mathf.Max(demandUntil, Time.realtimeSinceStartup + seconds); Background.Touch(); }
        // the same for a scanner or monitor of the tool itself: its need for a pass is not somebody using the tool
        public static void DemandQuiet(float seconds) { demandUntil = Mathf.Max(demandUntil, Time.realtimeSinceStartup + seconds); }
        // moves when a completed pass added, removed or renamed objects (consumers skip passes that changed nothing)
        public static int ChangeStamp { get { return changeStamp; } }
        public static void MarkDirty(string reason) { dirty = true; rootsDirty = true; dirtyReason = reason; nextPassAt = 0; }

        // root discovery (FindObjectsOfType + FindObjectsOfTypeAll) costs ~10 ms, so the root set is cached and
        // refreshed only after a streaming change (Levels.StreamVersion) or every db.rootRefresh seconds (default 60) as a
        // safety net for objects spawned at the root; passes in between walk the cached roots. Every 10 s made a 20-30 ms
        // hitch every 10 s while an agent or the editor kept the database awake.
        static readonly List<Transform> rootCache = new List<Transform>();
        static bool rootsDirty = true;
        static float rootRefreshAt;
        static int rootStreamSeen = -1;
        public static int rootRefreshes;

        public static void Tick(bool editorOpen)
        {
            try
            {
                if (editorOpen) Demand(2f);
                bool wanted = Time.realtimeSinceStartup < demandUntil;
                if (!walking)
                {
                    if (!wanted) return;
                    if (Time.realtimeSinceStartup < nextPassAt && !dirty) return;
                    BeginPass();
                }
                Step(budgetMs);
            }
            catch (Exception e) { DevLog.Error("object database", e); walking = false; nextPassAt = Time.realtimeSinceStartup + 5f; }
        }

        // synchronous: make sure the snapshot is no older than maxAge seconds (used by explicit user searches)
        public static void EnsureFresh(float maxAge)
        {
            Demand(10f);
            if (Ready && Age <= maxAge && !dirty) return;
            if (!walking) BeginPass();
            Step(10000f);
        }

        // ---------------------------------------------------------------- scanning
        static void BeginPass()
        {
            pass++;
            walking = true;
            dirty = false;
            passStartFrame = Time.frameCount;
            passMs = 0;
            addedLastPass = 0;
            stack.Clear();
            sw.Reset(); sw.Start();
            rootCache.RemoveAll(t => t == null || t.parent != null);
            if (Levels.StreamVersion != rootStreamSeen) { rootStreamSeen = Levels.StreamVersion; rootsDirty = true; }
            if (rootsDirty || rootCache.Count == 0 || Time.realtimeSinceStartup >= rootRefreshAt)
            {
                var rootSet = rootSetTmp; rootSet.Clear();
                foreach (var o in UObj.FindObjectsOfType(typeof(Transform)))
                {
                    var t = o as Transform;
                    if (t != null) rootSet.Add(t.root);
                }
                // inactive roots are invisible to FindObjectsOfType; FindObjectsOfTypeAll also returns assets,
                // which are normally activeSelf=true, so keep only inactive, non-hidden roots
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(GameObject)))
                {
                    var g = o as GameObject;
                    if (g == null || g.hideFlags != HideFlags.None || g.transform.parent != null || g.activeSelf) continue;
                    rootSet.Add(g.transform);
                }
                rootCache.Clear(); rootCache.AddRange(rootSet);
                rootSet.Clear();
                rootsDirty = false;
                rootRefreshAt = Time.realtimeSinceStartup + Mathf.Max(10f, EditorState.Get("db.rootRefresh", 60f));
                rootRefreshes++;
                sw.Stop();
                lastRootMs = (float)sw.Elapsed.TotalMilliseconds;
            }
            else sw.Stop();
            roots = rootCache.Count;
            foreach (var r in rootCache) stack.Push(new Item { t = r, depth = 0, parentId = 0, scene = r.name });
            passMs += (float)sw.Elapsed.TotalMilliseconds;
        }

        static readonly HashSet<Transform> rootSetTmp = new HashSet<Transform>();

        static void Step(float budget)
        {
            sw.Reset(); sw.Start();
            int n = 0;
            float now = Time.realtimeSinceStartup;
            while (stack.Count > 0)
            {
                var it = stack.Pop();
                var t = it.t;
                if (t == null) continue;
                var rec = Visit(t, it, now);
                for (int i = rec.childCount - 1; i >= 0; i--)
                {
                    var c = t.GetChild(i);
                    stack.Push(new Item { t = c, depth = it.depth + 1, parentId = rec.id, scene = it.scene, parentPath = rec.path });
                }
                if ((++n & 63) == 0 && sw.Elapsed.TotalMilliseconds > budget) break;
            }
            sw.Stop();
            passMs += (float)sw.Elapsed.TotalMilliseconds;
            if (stack.Count == 0) EndPass();
        }

        static ObjRecord Visit(Transform t, Item it, float now)
        {
            var go = t.gameObject;
            int id = go.GetInstanceID();
            ObjRecord r;
            bool isNew = !byId.TryGetValue(id, out r) || r.go == null;
            if (isNew)
            {
                r = new ObjRecord { id = id, go = go, t = t, discoveredAt = now, discoveredFrame = Time.frameCount };
                byId[id] = r;
                addedLastPass++;
            }
            // cheap per-pass refresh
            r.activeSelf = go.activeSelf;
            r.activeInHierarchy = go.activeInHierarchy;
            r.pos = t.position;
            r.depth = it.depth;
            bool reparented = !isNew && r.parentId != it.parentId;
            r.parentId = it.parentId;
            r.scene = it.scene;
            int cc = t.childCount;
            r.childCount = cc;
            // an ancestor was renamed or moved: its path is a new string object, so every path below it is rebuilt
            bool parentMoved = !ReferenceEquals(r.parentPathSeen, it.parentPath);
            // names / components: when new, when the parent moved it, and for one object in eight per pass (a rename or
            // an added component is seen within eight passes without re-reading every object in one frame)
            bool refresh = isNew || reparented || ((pass + id) & 7) == 0;
            if (refresh || parentMoved || r.path == null)
            {
                string nm = refresh || r.name == null ? go.name : r.name;
                if (r.name != nm || r.path == null || reparented || parentMoved)
                {
                    if (r.name != nm) { r.name = nm; r.nameLower = nm.ToLowerInvariant(); }
                    if (!isNew) renamedThisPass++;   // renamed, moved, or below something that was: consumers re-read it
                    r.parentPathSeen = it.parentPath;
                    SetPath(r, it.parentPath == null ? nm : it.parentPath + "/" + nm);
                }
            }
            if (refresh)
            {
                r.layer = go.layer;
                bool sameTag = false;
                if (r.tag != null && r.tag.Length > 0) { try { sameTag = go.CompareTag(r.tag); } catch { } }
                if (!sameTag) { try { r.tag = go.tag; } catch { r.tag = ""; } }
                Components(r);
            }
            r.seenPass = pass;
            return r;
        }
        static int renamedThisPass;

        static void SetPath(ObjRecord r, string p)
        {
            if (r.path != null) { List<ObjRecord> l; if (byPath.TryGetValue(r.path, out l)) { l.Remove(r); if (l.Count == 0) byPath.Remove(r.path); } }
            r.path = p; r.pathLower = p.ToLowerInvariant();
            List<ObjRecord> list;
            if (!byPath.TryGetValue(p, out list)) byPath[p] = list = new List<ObjRecord>(1);
            list.Add(r);
        }

        // Type.Name builds a new string on every call in this Mono: one copy per type, with what the type says
        // about the object's kind
        static readonly Dictionary<Type, string> typeNames = new Dictionary<Type, string>(512);
        static readonly Dictionary<Type, ObjKind> typeKinds = new Dictionary<Type, ObjKind>(512);
        static readonly List<Component> compTmp = new List<Component>(32);
        const ObjKind ScriptBit = (ObjKind)(1 << 30), ColliderBit = (ObjKind)(1 << 29);   // per-type notes, never stored in a record

        static string TypeName(Type ty, out ObjKind k)
        {
            string n;
            if (typeNames.TryGetValue(ty, out n)) { k = typeKinds[ty]; return n; }
            n = ty.Name;
            k = ObjKind.None;
            if (typeof(Collider).IsAssignableFrom(ty)) k = ColliderBit;
            else if (typeof(Camera).IsAssignableFrom(ty)) k = ObjKind.Camera;
            else if (typeof(Light).IsAssignableFrom(ty)) k = ObjKind.Light;
            else if (typeof(Renderer).IsAssignableFrom(ty)) k = ObjKind.Renderer;
            else if (typeof(Animation).IsAssignableFrom(ty) || typeof(Animator).IsAssignableFrom(ty)) k = ObjKind.Animation;
            else if (typeof(AudioSource).IsAssignableFrom(ty) || n.StartsWith("Ak", StringComparison.Ordinal) || n.Contains("Audio") || n.Contains("Sound") || n.Contains("Music")) k = ObjKind.Audio;
            else if (n == "PlayMakerFSM") k = ObjKind.StateMachine;
            else if (n.StartsWith("Signal", StringComparison.Ordinal) || n.EndsWith("SignalConnector", StringComparison.Ordinal)) k = ObjKind.Signal;
            else if (n == "Savepoint" || n.Contains("Checkpoint")) k = ObjKind.Savepoint;
            else if (typeof(Character).IsAssignableFrom(ty) || n == "Boy" || n == "Huddle") k = ObjKind.Character;
            if (typeof(MonoBehaviour).IsAssignableFrom(ty)) k |= ScriptBit;
            typeNames[ty] = n; typeKinds[ty] = k;
            return n;
        }

        static void Components(ObjRecord r)
        {
            var cs = compTmp;
            cs.Clear();
            r.go.GetComponents(typeof(Component), cs);
            int count = cs.Count;
            r.compCount = count;
            // most refreshes find the same components: the old names array is kept then
            if (r.comps != null && r.comps.Length > 0 && r.comps.Length != count) renamedThisPass++;   // components added or removed
            var names = r.comps != null && r.comps.Length == count ? r.comps : new string[count];
            bool fresh = !ReferenceEquals(names, r.comps);
            ObjKind k = ObjKind.None;
            int scripts = 0;
            for (int i = 0; i < count; i++)
            {
                var c = cs[i];
                string n; ObjKind tk = ObjKind.None;
                if (c == null) n = "(missing)";
                else
                {
                    n = TypeName(c.GetType(), out tk);
                    if ((tk & ColliderBit) != 0) { var col = c as Collider; k |= col != null && col.isTrigger ? ObjKind.Trigger : ObjKind.Collider; }
                    else k |= tk & ~ScriptBit;
                    if ((tk & ScriptBit) != 0) scripts++;
                }
                if (!fresh && !ReferenceEquals(names[i], n))
                {
                    // a component changed: a new array, so a reader holding the old one keeps a consistent view
                    var copy = new string[count];
                    Array.Copy(names, copy, i);
                    names = copy; fresh = true;
                    renamedThisPass++;   // a component of a known object changed
                }
                names[i] = n;
            }
            cs.Clear();
            if (scripts > 0 && (k & (ObjKind.StateMachine | ObjKind.Signal | ObjKind.Audio | ObjKind.Savepoint | ObjKind.Character)) == 0) k |= ObjKind.Script;
            if (k == ObjKind.None && count <= 1 && r.childCount > 0) k = ObjKind.Group;
            r.comps = names;
            r.kind = k;
        }

        static readonly List<int> deadTmp = new List<int>();

        static void EndPass()
        {
            walking = false;
            // drop records not seen this pass (destroyed or unloaded); never hand out stale references
            var dead = deadTmp; dead.Clear();
            foreach (var kv in byId) if (kv.Value.seenPass != pass || kv.Value.go == null) dead.Add(kv.Key);
            foreach (var id in dead)
            {
                var r = byId[id];
                if (r.path != null) { List<ObjRecord> l; if (byPath.TryGetValue(r.path, out l)) { l.Remove(r); if (l.Count == 0) byPath.Remove(r.path); } }
                r.go = null; r.t = null;
                byId.Remove(id);
            }
            removedLastPass = dead.Count;
            dead.Clear();
            all.Clear();
            foreach (var kv in byId) all.Add(kv.Value);
            if (addedLastPass > 0 || removedLastPass > 0 || renamedThisPass > 0) changeStamp++;
            renamedThisPass = 0;
            completedPasses++;
            lastPassMs = passMs;
            lastPassFrames = Time.frameCount - passStartFrame + 1;
            lastPassEnd = Time.realtimeSinceStartup;
            nextPassAt = lastPassEnd + 1.0f;
            if (completedPasses == 1 || removedLastPass > 0 || addedLastPass > 200)
                DevLog.Write("[db] pass " + pass + ": " + all.Count + " objects, " + roots + " roots, +" + addedLastPass + " -" + removedLastPass +
                             ", " + Fmt.F(lastPassMs, "0.0") + " ms over " + lastPassFrames + " frame(s) (" + dirtyReason + ")");
            dirtyReason = "periodic";
        }

        // ---------------------------------------------------------------- queries
        public static ObjRecord Get(int id) { ObjRecord r; return byId.TryGetValue(id, out r) && r.go != null ? r : null; }
        public static ObjRecord Get(GameObject g) { return g == null ? null : Get(g.GetInstanceID()); }

        public static List<ObjRecord> ByPath(string path)
        {
            List<ObjRecord> l;
            var res = new List<ObjRecord>();
            if (byPath.TryGetValue(path, out l)) foreach (var r in l) if (r.go != null) res.Add(r);
            return res;
        }

        // Records of the last completed pass whose name is exactly this (compared in lower case). The index is rebuilt
        // on the first call after a pass that added, removed or renamed objects, so an unused index costs nothing.
        // The returned list belongs to the index: read it at once, do not keep or change it; an entry is dead
        // (r.go == null) when its object went away since the pass. Main thread.
        static readonly List<ObjRecord> noRecords = new List<ObjRecord>(0);
        public static List<ObjRecord> ByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return noRecords;
            if (byNameStamp != changeStamp) RebuildNames();
            List<ObjRecord> l;
            return byName.TryGetValue(name.ToLowerInvariant(), out l) ? l : noRecords;
        }

        // every distinct lower-case object name of the last completed pass (for prefix / substring lookups)
        public static ICollection<string> Names
        {
            get { if (byNameStamp != changeStamp) RebuildNames(); return byName.Keys; }
        }

        static readonly List<string> emptyNames = new List<string>();
        static void RebuildNames()
        {
            byNameStamp = changeStamp;
            foreach (var kv in byName) kv.Value.Clear();
            foreach (var r in all)
            {
                if (r.nameLower == null) continue;
                List<ObjRecord> l;
                if (!byName.TryGetValue(r.nameLower, out l)) byName[r.nameLower] = l = new List<ObjRecord>(1);
                l.Add(r);
            }
            emptyNames.Clear();
            foreach (var kv in byName) if (kv.Value.Count == 0) emptyNames.Add(kv.Key);
            foreach (var k in emptyNames) byName.Remove(k);
            emptyNames.Clear();
        }

        // query: words must all match name or path; "t:Type" = has component; "k:kind" = classification;
        // "hidden" = inactive only; "active" = active only
        public static List<ObjRecord> Search(string query, int max)
        {
            var res = new List<ObjRecord>();
            var words = new List<string>();
            string typeQ = null, kindQ = null; bool onlyHidden = false, onlyActive = false;
            foreach (var w in (query ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (w.StartsWith("t:")) typeQ = w.Substring(2);
                else if (w.StartsWith("k:")) kindQ = w.Substring(2);
                else if (w == "hidden") onlyHidden = true;
                else if (w == "active") onlyActive = true;
                else words.Add(w);
            }
            foreach (var r in all)
            {
                if (r.go == null) continue;
                if (onlyHidden && r.activeInHierarchy) continue;
                if (onlyActive && !r.activeInHierarchy) continue;
                bool ok = true;
                foreach (var w in words) if (r.nameLower.IndexOf(w, StringComparison.Ordinal) < 0 && r.pathLower.IndexOf(w, StringComparison.Ordinal) < 0) { ok = false; break; }
                if (!ok) continue;
                if (typeQ != null) { bool hit = false; foreach (var c in r.comps) if (c.IndexOf(typeQ, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; } if (!hit) continue; }
                if (kindQ != null && r.KindLabel.IndexOf(kindQ, StringComparison.OrdinalIgnoreCase) < 0) continue;
                res.Add(r);
                if (res.Count >= max) break;
            }
            return res;
        }

        public static string Stats()
        {
            int active = 0, scenes = 0;
            var sc = new HashSet<string>();
            foreach (var r in all) { if (r.activeInHierarchy) active++; sc.Add(r.scene); }
            scenes = sc.Count;
            return "objects " + all.Count + " (" + active + " active, " + (all.Count - active) + " inactive)  roots " + roots + "  scenes/roots " + scenes +
                   "  passes " + completedPasses + "  last pass " + Fmt.F(lastPassMs, "0.0") + " ms / " + lastPassFrames + " frames (last root refresh " + Fmt.F(lastRootMs, "0.0") + " ms, " + rootRefreshes + " refreshes)" +
                   "  +" + addedLastPass + " -" + removedLastPass + "  age " + (Ready ? Fmt.F(Age, "0.0") + " s" : "-") + (walking ? "  [scanning]" : "") +
                   "  budget " + Fmt.F(budgetMs, "0.0") + " ms/frame" + (Demanded ? "" : "  [asleep: nobody asked for it]");
        }

        // the same numbers as one JSON line ("db stats json")
        public static string StatsJson()
        {
            int active = 0;
            foreach (var r in all) if (r.activeInHierarchy) active++;
            return JsonLine.Obj("ok", true, "objects", all.Count, "active", active, "inactive", all.Count - active, "roots", roots, "passes", completedPasses,
                "ready", Ready, "scanning", walking, "demanded", Demanded, "ageSeconds", Ready ? (object)Age : null, "lastPassMs", lastPassMs, "lastPassFrames", lastPassFrames,
                "lastRootMs", lastRootMs, "rootRefreshes", rootRefreshes, "added", addedLastPass, "removed", removedLastPass, "budgetMs", budgetMs);
        }
    }
}
