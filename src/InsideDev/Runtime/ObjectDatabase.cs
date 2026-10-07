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
        public static readonly string[] Empty = new string[0];

        public bool Alive { get { return go != null; } }
        public string KindLabel { get { return kind == ObjKind.None ? "object" : kind.ToString().Replace(", ", "|"); } }
        public bool Has(string componentType) { foreach (var c in comps) if (c == componentType) return true; return false; }
    }

    // RuntimeObjectDatabase: incremental, frame-budgeted scan of every loaded GameObject (active and inactive).
    // Unity APIs are main-thread only, so work is chunked across frames instead of threads. Scanning runs only
    // while something wants it (editor open, bridge activity, explicit request) — zero cost otherwise.
    public static class ObjectDatabase
    {
        public static readonly Dictionary<int, ObjRecord> byId = new Dictionary<int, ObjRecord>(32768);
        static readonly Dictionary<string, List<ObjRecord>> byPath = new Dictionary<string, List<ObjRecord>>(32768);
        public static readonly List<ObjRecord> all = new List<ObjRecord>(32768);   // stable snapshot of the last completed pass

        public static int pass, completedPasses, removedLastPass, addedLastPass;
        public static float lastPassMs, lastPassEnd = -1f, lastRootMs;
        public static int lastPassFrames, roots;
        public static float budgetMs = 1.5f;
        public static string dirtyReason = "startup";
        static bool dirty = true;
        static float demandUntil = -1f;
        static float nextPassAt;

        // walk state
        struct Item { public Transform t; public int depth, parentId; public string scene; }
        static readonly Stack<Item> stack = new Stack<Item>(4096);
        static bool walking;
        static int passStartFrame;
        static float passMs;
        static readonly Stopwatch sw = new Stopwatch();

        public static bool Ready { get { return completedPasses > 0; } }
        public static float Age { get { return lastPassEnd < 0 ? 1e9f : Time.realtimeSinceStartup - lastPassEnd; } }
        public static bool Scanning { get { return walking; } }

        // ---------------------------------------------------------------- demand / invalidation
        public static void Demand(float seconds) { demandUntil = Mathf.Max(demandUntil, Time.realtimeSinceStartup + seconds); }
        public static void MarkDirty(string reason) { dirty = true; rootsDirty = true; dirtyReason = reason; nextPassAt = 0; }

        // root discovery (FindObjectsOfType + FindObjectsOfTypeAll) costs ~10 ms, so the root set is cached and
        // refreshed only after a scene/streaming change or every 10 s; passes in between walk the cached roots
        static readonly List<Transform> rootCache = new List<Transform>();
        static bool rootsDirty = true;
        static float rootRefreshAt;
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
            if (rootsDirty || rootCache.Count == 0 || Time.realtimeSinceStartup >= rootRefreshAt)
            {
                var rootSet = new HashSet<Transform>();
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
                rootsDirty = false;
                rootRefreshAt = Time.realtimeSinceStartup + 10f;
                rootRefreshes++;
                sw.Stop();
                lastRootMs = (float)sw.Elapsed.TotalMilliseconds;
            }
            else sw.Stop();
            roots = rootCache.Count;
            foreach (var r in rootCache) stack.Push(new Item { t = r, depth = 0, parentId = 0, scene = r.name });
            passMs += (float)sw.Elapsed.TotalMilliseconds;
        }

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
                Visit(t, it, now);
                for (int i = t.childCount - 1; i >= 0; i--)
                {
                    var c = t.GetChild(i);
                    stack.Push(new Item { t = c, depth = it.depth + 1, parentId = t.gameObject.GetInstanceID(), scene = it.scene });
                }
                if ((++n & 63) == 0 && sw.Elapsed.TotalMilliseconds > budget) break;
            }
            sw.Stop();
            passMs += (float)sw.Elapsed.TotalMilliseconds;
            if (stack.Count == 0) EndPass();
        }

        static void Visit(Transform t, Item it, float now)
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
            // names/paths/components only when new, periodically, or when the parent moved it
            if (isNew || reparented || (pass % 8) == 0)
            {
                string nm = go.name;
                if (r.name != nm || r.path == null || reparented)
                {
                    r.name = nm; r.nameLower = nm.ToLowerInvariant();
                    var parent = t.parent;
                    ObjRecord pr;
                    string pp = parent != null && byId.TryGetValue(it.parentId, out pr) && pr.path != null ? pr.path : (parent != null ? Inspector.PathOf(parent) : null);
                    SetPath(r, pp == null ? nm : pp + "/" + nm);
                }
                r.layer = go.layer;
                try { r.tag = go.tag; } catch { r.tag = ""; }
                Components(r);
            }
            else if (r.path == null) { SetPath(r, Inspector.PathOf(t)); }
            r.seenPass = pass;
        }

        static void SetPath(ObjRecord r, string p)
        {
            if (r.path != null) { List<ObjRecord> l; if (byPath.TryGetValue(r.path, out l)) l.Remove(r); }
            r.path = p; r.pathLower = p.ToLowerInvariant();
            List<ObjRecord> list;
            if (!byPath.TryGetValue(p, out list)) byPath[p] = list = new List<ObjRecord>(1);
            list.Add(r);
        }

        static void Components(ObjRecord r)
        {
            var cs = r.go.GetComponents<Component>();
            r.compCount = cs.Length;
            var names = new string[cs.Length];
            ObjKind k = ObjKind.None;
            int scripts = 0;
            for (int i = 0; i < cs.Length; i++)
            {
                var c = cs[i];
                if (c == null) { names[i] = "(missing)"; continue; }
                var ty = c.GetType();
                string n = ty.Name;
                names[i] = n;
                var col = c as Collider;
                if (col != null) k |= col.isTrigger ? ObjKind.Trigger : ObjKind.Collider;
                else if (c is Camera) k |= ObjKind.Camera;
                else if (c is Light) k |= ObjKind.Light;
                else if (c is Renderer) k |= ObjKind.Renderer;
                else if (c is Animation || c is Animator) k |= ObjKind.Animation;
                else if (c is AudioSource || n.StartsWith("Ak") || n.Contains("Audio") || n.Contains("Sound") || n.Contains("Music")) k |= ObjKind.Audio;
                else if (n == "PlayMakerFSM") k |= ObjKind.StateMachine;
                else if (n.StartsWith("Signal") || n.EndsWith("SignalConnector")) k |= ObjKind.Signal;
                else if (n == "Savepoint" || n.Contains("Checkpoint")) k |= ObjKind.Savepoint;
                else if (c is Character || n == "Boy" || n == "Huddle") k |= ObjKind.Character;
                if (c is MonoBehaviour) scripts++;
            }
            if (scripts > 0 && (k & (ObjKind.StateMachine | ObjKind.Signal | ObjKind.Audio | ObjKind.Savepoint | ObjKind.Character)) == 0) k |= ObjKind.Script;
            if (k == ObjKind.None && cs.Length <= 1 && r.childCount > 0) k = ObjKind.Group;
            r.comps = names;
            r.kind = k;
        }

        static void EndPass()
        {
            walking = false;
            // drop records not seen this pass (destroyed or unloaded); never hand out stale references
            var dead = new List<int>();
            foreach (var kv in byId) if (kv.Value.seenPass != pass || kv.Value.go == null) dead.Add(kv.Key);
            foreach (var id in dead)
            {
                var r = byId[id];
                if (r.path != null) { List<ObjRecord> l; if (byPath.TryGetValue(r.path, out l)) { l.Remove(r); if (l.Count == 0) byPath.Remove(r.path); } }
                r.go = null; r.t = null;
                byId.Remove(id);
            }
            removedLastPass = dead.Count;
            all.Clear();
            foreach (var kv in byId) all.Add(kv.Value);
            completedPasses++;
            lastPassMs = passMs;
            lastPassFrames = Time.frameCount - passStartFrame + 1;
            lastPassEnd = Time.realtimeSinceStartup;
            nextPassAt = lastPassEnd + 1.0f;
            if (completedPasses == 1 || removedLastPass > 0 || addedLastPass > 200)
                DevLog.Write("[db] pass " + pass + ": " + all.Count + " objects, " + roots + " roots, +" + addedLastPass + " -" + removedLastPass +
                             ", " + lastPassMs.ToString("0.0") + " ms over " + lastPassFrames + " frame(s) (" + dirtyReason + ")");
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
                if (typeQ != null) { bool hit = false; foreach (var c in r.comps) if (c.ToLowerInvariant().Contains(typeQ)) { hit = true; break; } if (!hit) continue; }
                if (kindQ != null && r.KindLabel.ToLowerInvariant().IndexOf(kindQ, StringComparison.Ordinal) < 0) continue;
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
                   "  passes " + completedPasses + "  last pass " + lastPassMs.ToString("0.0") + " ms / " + lastPassFrames + " frames (last root refresh " + lastRootMs.ToString("0.0") + " ms, " + rootRefreshes + " refreshes)" +
                   "  +" + addedLastPass + " -" + removedLastPass + "  age " + (Ready ? Age.ToString("0.0") + " s" : "-") + (walking ? "  [scanning]" : "") +
                   "  budget " + budgetMs.ToString("0.0") + " ms/frame";
        }
    }
}
