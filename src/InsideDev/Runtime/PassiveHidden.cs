using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using HutongGames.PlayMaker;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    // Passive hidden-object scanner. Loads nothing: it only looks at levels the game itself has ACTIVATED while you play,
    // plus the master / persistent objects, and keeps a database in the game folder (_mod\hidden\).
    //
    //   hidden   = switched off (also inside a switched-off parent), or renderer / trigger / collider disabled
    //   excluded = anything the game can switch on during play:
    //              - a gameplay reference to it (script field, state-machine action, signal wiring) from a source that can
    //                run (on, or inside switched-off parents that can themselves be switched on - iterated to a fixpoint)
    //              - an Animator with a controller or any Animation component on it or a parent
    //              - it was seen switched on / rendering / colliding at any time while scanning
    //
    // Output: hidden_db.tsv / seen_on.txt hold the raw state between sessions (written 20 s after something new was
    // found); hidden_names.txt (one name per line) and hidden_items.txt (level | path | how it is hidden), sorted by
    // level, are written with "hidden save", when the game quits and every two minutes while new things are found.
    //
    // Off unless asked for: editor.cfg hidden.passive = 1 ("hidden on") keeps it on, Background.Want(Background.Hidden)
    // or "hidden scan" runs it for a while. A level is scanned in slices of about a millisecond per frame.
    //
    // Replaces the forced level sweep / save-point tour (removed 0.30.0): loading levels outside the game's own streaming
    // crashed the engine in the game's AnimationPreAwake (4 crashes, 2026-09-28).
    public static class PassiveHidden
    {
        public const string ConfigKey = "hidden.passive";
        const float LevelRescan = 20f, MasterRescan = 30f, SaveEvery = 20f, ReportEvery = 120f;
        const double SliceMs = 1.0;
        const string MasterKey = "(master / persistent)";

        struct Item { public string scene, path, name, kind, toggle; }
        struct Edge { public int src, dst; public string via; }

        static readonly Dictionary<string, Item> db = new Dictionary<string, Item>();          // scene|path|kind -> item
        static readonly HashSet<string> seenOn = new HashSet<string>();                         // scene|path|kind
        static readonly Dictionary<string, HashSet<string>> knownPaths = new Dictionary<string, HashSet<string>>();   // scene -> paths
        static readonly Dictionary<string, float> lastScan = new Dictionary<string, float>();
        static readonly HashSet<string> analysedThisSession = new HashSet<string>();
        static readonly List<SubsceneCuller> cullers = new List<SubsceneCuller>();
        static float nextTick, cullersAt = -100f, masterAt = -100f, savedAt, reportAt, dirtySince = -1f;
        static bool loaded, dirty, reportDirty, loadFailed;
        static int rr, scans, levelsSeen;
        static string lastMsg = "";

        static string Dir { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "hidden"); } }

        // ---------------------------------------------------------------- on / off
        // The switch (a field since 0.10.0: other modules read and write it). It follows editor.cfg hidden.passive both
        // ways: the key is read at the first tick and once a second, a change of the field is written to the key.
        public static bool enabled;
        static bool synced, fieldSeen, keySeen; static float keyReadAt = -100f;

        static void Sync()
        {
            if (!synced)
            {
                synced = true;
                try { keySeen = EditorState.Get(ConfigKey, false); } catch { }
                enabled = fieldSeen = keySeen || enabled;
                if (!enabled) try { if (File.Exists(Path.Combine(Dir, "hidden_db.tsv"))) DevLog.Write("[hidden] the passive hidden scanner is off since 0.11.0 unless asked for: " + ConfigKey + "=1 (hidden on) keeps it on as before, hidden scan runs it for a while"); } catch { }
                return;
            }
            if (enabled != fieldSeen)
            {
                fieldSeen = keySeen = enabled;
                try { EditorState.Set(ConfigKey, enabled); } catch { }
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now - keyReadAt < 1f) return;
            keyReadAt = now;
            bool k = keySeen;
            try { k = EditorState.Get(ConfigKey, false); } catch { }
            if (k != keySeen) { keySeen = k; enabled = fieldSeen = k; }
        }

        // scanning right now (a level is being looked at in slices)
        public static bool Scanning { get { return job != null; } }
        // the scanner runs: switched on for good, or asked for with Background.Want(Background.Hidden, seconds)
        public static bool Running { get { return !loadFailed && (enabled || Background.Wanted(Background.Hidden)); } }

        // ---------------------------------------------------------------- driver (one level at a time, a slice per frame)
        public static void Tick()
        {
            Sync();
            if (!Running)
            {
                if (job != null) Abort();
                return;
            }
            if (job != null || pending.Count > 0)
            {
                // one budget per frame; the many small master roots share it
                sw.Reset(); sw.Start();
                if (job != null) Slice();
                while (job == null && pending.Count > 0 && sw.Elapsed.TotalMilliseconds < SliceMs)
                {
                    var n = pending[pending.Count - 1]; pending.RemoveAt(pending.Count - 1);
                    Begin(MasterKey, n);
                    if (job != null) Slice();
                }
                if (job != null || pending.Count > 0) return;
            }
            float t = Time.realtimeSinceStartup;
            if (t < nextTick) return;
            nextTick = t + 0.5f;
            if (!loaded) Load();
            if (loadFailed) return;
            if (!G.SavepointsReady) return;
            var ch = G.MainCharacter; if (ch == null || !ch.isCharacterActive) return;

            if (t - cullersAt > 5f || Levels.Changed(ref seenStream))
            {
                cullersAt = t; cullers.Clear();
                foreach (var o in UObj.FindObjectsOfType(typeof(SubsceneCuller))) { var c = o as SubsceneCuller; if (c != null && c.IsValid) cullers.Add(c); }
                DropUnusedPathCaches(t);
            }
            // next ACTIVE level that is due
            for (int n = 0; n < cullers.Count; n++)
            {
                var c = cullers[(rr + n) % Math.Max(1, cullers.Count)];
                if (c == null || !c.IsActive || c.InProgress || c.SubsceneNode == null) continue;
                string key = Key(c); float last;
                if (lastScan.TryGetValue(key, out last) && t - last < LevelRescan) continue;
                rr = (rr + n + 1) % Math.Max(1, cullers.Count);
                lastScan[key] = t;
                Begin(key, c.SubsceneNode.transform);
                if (job != null) return;
                continue;
            }
            if (t - masterAt > MasterRescan)
            {
                masterAt = t;
                foreach (var tr in Inspector.AllSceneTransforms())
                    if (tr != null && tr.parent == null && tr.GetComponent<SubsceneNode>() == null) pending.Add(tr);
                if (pending.Count > 0) return;
            }
            MaybeSave(t);
        }
        static int seenStream;
        static readonly List<Transform> pending = new List<Transform>();   // master roots waiting for their scan

        static string Key(SubsceneCuller c)
        {
            string n = c.SceneName; n = n.Substring(n.LastIndexOf('/') + 1);
            return n.EndsWith(".unity", StringComparison.Ordinal) ? n.Substring(0, n.Length - 6) : n;
        }

        // ---------------------------------------------------------------- scanning one active level, in slices
        struct SibKey { public int parent; public string name; }
        sealed class SibCmp : IEqualityComparer<SibKey>
        {
            public bool Equals(SibKey a, SibKey b) { return a.parent == b.parent && a.name == b.name; }
            public int GetHashCode(SibKey k) { return k.parent * 397 ^ k.name.GetHashCode(); }
        }

        sealed class PathCache { public Dictionary<int, string> paths; public float usedAt; }
        static readonly Dictionary<int, PathCache> pathCaches = new Dictionary<int, PathCache>();   // root transform id -> paths of its objects

        sealed class Job
        {
            public string key; public Transform root; public int rootId;
            public Transform[] all; public int i, phase;
            public Dictionary<int, string> paths; public bool rebuild;
            public Dictionary<SibKey, int> sib;
            public HashSet<string> known;
            public readonly List<Item> found = new List<Item>(); public readonly List<GameObject> foundGo = new List<GameObject>();
            public bool unseen;
            public Dictionary<int, List<int>> offAnc; public Dictionary<int, string> toggle;
        }
        static Job job;
        static readonly System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
        static readonly List<Component> rsTmp = new List<Component>(8), colTmp = new List<Component>(8), compTmp = new List<Component>(16);

        static void Begin(string key, Transform root)
        {
            if (root == null) return;
            try
            {
                var j = new Job { key = key, root = root, rootId = root.GetInstanceID() };
                j.all = root.GetComponentsInChildren<Transform>(true);
                PathCache pc;
                if (pathCaches.TryGetValue(j.rootId, out pc) && pc.paths.Count == j.all.Length) j.paths = pc.paths;
                else { j.rebuild = true; j.paths = new Dictionary<int, string>(j.all.Length); j.sib = new Dictionary<SibKey, int>(new SibCmp()); }
                if (!knownPaths.TryGetValue(key, out j.known)) knownPaths[key] = j.known = new HashSet<string>();
                job = j;
            }
            catch (Exception e) { job = null; DevLog.Error("hidden scan " + key, e); }
        }

        static void Abort()
        {
            job = null; pending.Clear();
            tgt.Clear(); edges.Clear();
        }

        // runs the current scan until the frame's budget (the caller started the stopwatch) is used up
        static void Slice()
        {
            var j = job;
            try
            {
                while (job == j && sw.Elapsed.TotalMilliseconds < SliceMs)
                {
                    switch (j.phase)
                    {
                        case 0: Objects(j); break;
                        case 1: OffAncestors(j); break;
                        case 2: Animated(j); break;
                        case 3: References(j); break;
                        default: Finish(j); job = null; MaybeSave(Time.realtimeSinceStartup); break;
                    }
                }
            }
            catch (Exception e) { DevLog.Error("hidden scan " + j.key, e); Abort(); }
        }

        static bool Structural(Job j, Transform t, GameObject g) { return t == j.root || g.GetComponent<SubsceneNode>() != null || g.GetComponent<SubsceneContentRoot>() != null; }

        // phase 0: paths and the hidden state of every object
        static void Objects(Job j)
        {
            var all = j.all;
            int n = 0;
            while (j.i < all.Length)
            {
                if ((++n & 15) == 0 && sw.Elapsed.TotalMilliseconds >= SliceMs) return;
                var t = all[j.i++];
                if (t == null) continue;
                int tid = t.GetInstanceID();
                string p;
                if (j.rebuild)
                {
                    // same-named siblings would share one path (and one database entry): number the 2nd, 3rd ... as
                    // "name #2", "name #3" in sibling order, which comes from the level data and is the same every session
                    string nm = t.name;
                    var par = t.parent; string pp;
                    p = par != null && j.paths.TryGetValue(par.GetInstanceID(), out pp) ? pp + "/" + nm : nm;
                    if (t != j.root)
                    {
                        var sk = new SibKey { parent = par != null ? par.GetInstanceID() : 0, name = nm }; int nth;
                        j.sib.TryGetValue(sk, out nth); j.sib[sk] = ++nth;
                        if (nth > 1) p += " #" + nth;
                    }
                    j.paths[tid] = p;
                }
                else if (!j.paths.TryGetValue(tid, out p))
                {
                    // the level changed since its paths were built: start this scan again from the level data
                    j.rebuild = true; j.paths = new Dictionary<int, string>(all.Length); j.sib = new Dictionary<SibKey, int>(new SibCmp());
                    j.i = 0; j.found.Clear(); j.foundGo.Clear(); j.unseen = false;
                    return;
                }
                // objects spawned at runtime (pools, effects: "(Clone)") are new instances every session and not
                // level content; recording them would pile up a new entry per spawn / per reboot
                if (p.IndexOf("(Clone)", StringComparison.Ordinal) >= 0) continue;
                var g = t.gameObject;
                if (Structural(j, t, g)) continue;
                rsTmp.Clear(); colTmp.Clear();
                g.GetComponents(typeof(Renderer), rsTmp); g.GetComponents(typeof(Collider), colTmp);
                bool active = g.activeSelf;
                if (j.known.Contains(p) && !Changes.IsChanged(g))   // your own edits are not the game switching it on
                {
                    // a known hidden object seen on right now -> the game does switch it on
                    string pk = null;
                    if (active) Mark((pk = j.key + "|" + p) + "|off");
                    for (int k = 0; k < rsTmp.Count; k++) { var r = rsTmp[k] as Renderer; if (r != null && r.enabled) { Mark((pk ?? (pk = j.key + "|" + p)) + "|renderer off"); break; } }
                    for (int k = 0; k < colTmp.Count; k++) { var c = colTmp[k] as Collider; if (c != null && c.enabled) { Mark((pk ?? (pk = j.key + "|" + p)) + "|" + (c.isTrigger ? "trigger off" : "collider off")); break; } }
                }
                string kind = null;
                if (!active) kind = "off";
                else
                {
                    if (rsTmp.Count > 0) { bool any = false; for (int k = 0; k < rsTmp.Count; k++) { var r = rsTmp[k] as Renderer; if (r != null && r.enabled) { any = true; break; } } if (!any) kind = "renderer off"; }
                    if (kind == null) for (int k = 0; k < colTmp.Count; k++) { var c = colTmp[k] as Collider; if (c != null && !c.enabled) { kind = c.isTrigger ? "trigger off" : "collider off"; break; } }
                }
                if (kind == null) continue;
                j.found.Add(new Item { scene = j.key, path = p, name = t.name, kind = kind }); j.foundGo.Add(g);
                if (!j.unseen && !db.ContainsKey(j.key + "|" + p + "|" + kind)) j.unseen = true;
            }
            rsTmp.Clear(); colTmp.Clear();
            j.sib = null;
            PathCache pc;
            if (!pathCaches.TryGetValue(j.rootId, out pc)) pathCaches[j.rootId] = pc = new PathCache();
            pc.paths = j.paths; pc.usedAt = Time.realtimeSinceStartup;
            // reference / animation analysis: first time a level is seen this session, or when it shows something new
            if (j.found.Count > 0 && (j.unseen || !analysedThisSession.Contains(j.key)))
            {
                j.offAnc = new Dictionary<int, List<int>>(); j.toggle = new Dictionary<int, string>();
                j.i = 0; j.phase = 1;
            }
            else j.phase = 4;
        }

        static void DropUnusedPathCaches(float now)
        {
            if (pathCaches.Count == 0) return;
            List<int> drop = null;
            foreach (var kv in pathCaches) if (now - kv.Value.usedAt > 90f) { if (drop == null) drop = new List<int>(); drop.Add(kv.Key); }
            if (drop != null) foreach (var k in drop) pathCaches.Remove(k);
            if (goKey.Count > 20000) goKey.Clear();
        }

        // phase 1: for every object the switched-off objects above it (and itself)
        static void OffAncestors(Job j)
        {
            var all = j.all; int n = 0;
            while (j.i < all.Length)
            {
                if ((++n & 15) == 0 && sw.Elapsed.TotalMilliseconds >= SliceMs) return;
                var t = all[j.i++];
                if (t == null) continue;
                string p;
                if (!j.paths.TryGetValue(t.GetInstanceID(), out p) || p.IndexOf("(Clone)", StringComparison.Ordinal) >= 0) continue;
                var g = t.gameObject; int id = g.GetInstanceID();
                List<int> pa = null; if (t != j.root && t.parent != null) j.offAnc.TryGetValue(t.parent.gameObject.GetInstanceID(), out pa);
                if (Structural(j, t, g)) { j.offAnc[id] = pa; continue; }
                List<int> mine = pa; if (!g.activeSelf) { mine = pa != null ? new List<int>(pa) : new List<int>(); mine.Add(id); }
                j.offAnc[id] = mine;
            }
            j.i = 0; j.phase = 2;
        }

        // phase 2: animation on the object or a parent (component presence only: no Animation call on inactive objects)
        static void Animated(Job j)
        {
            int n = 0;
            while (j.i < j.foundGo.Count)
            {
                if ((++n & 7) == 0 && sw.Elapsed.TotalMilliseconds >= SliceMs) return;
                var g = j.foundGo[j.i++];
                if (g == null) continue;
                for (var u = g.transform; u != null && u != j.root; u = u.parent)
                {
                    var am = u.GetComponent<Animator>(); var an = u.GetComponent<Animation>();
                    if ((am != null && am.runtimeAnimatorController != null) || an != null) { j.toggle[g.GetInstanceID()] = "animated"; break; }
                }
            }
            tgt.Clear(); edges.Clear();
            for (int i = 0; i < j.foundGo.Count; i++) tgt[j.foundGo[i].GetInstanceID()] = j.found[i];
            j.i = 0; j.phase = 3;
        }

        // phase 3: what the scripts and state machines of the level reference, then who can run
        static void References(Job j)
        {
            var all = j.all;
            while (j.i < all.Length)
            {
                if (sw.Elapsed.TotalMilliseconds >= SliceMs) return;
                var t = all[j.i++];
                if (t == null) continue;
                var go = t.gameObject; srcGo = go.GetInstanceID();
                compTmp.Clear();
                go.GetComponents(typeof(MonoBehaviour), compTmp);
                for (int k = 0; k < compTmp.Count; k++)
                {
                    var mb = compTmp[k] as MonoBehaviour; if (mb == null) continue;
                    srcComp = mb.GetType().Name; if (Bookkeeping(srcComp)) continue;
                    work = 0;
                    try { var fsm = mb as PlayMakerFSM; if (fsm != null) WalkFsm(fsm); else Walk(mb, "", 0); } catch { }
                }
            }
            compTmp.Clear();
            tgt.Clear();
            var toggle = j.toggle; var offAnc = j.offAnc;
            bool changed = true; int guard = 0;
            while (changed && guard++ < 50)
            {
                changed = false;
                foreach (var e in edges)
                {
                    if (toggle.ContainsKey(e.dst)) continue;
                    List<int> oa; offAnc.TryGetValue(e.src, out oa);
                    bool canRun = true;
                    if (oa != null) foreach (var x in oa) if (!toggle.ContainsKey(x)) { canRun = false; break; }
                    if (canRun) { toggle[e.dst] = "referenced by " + e.via; changed = true; }
                }
            }
            edges.Clear();
            j.offAnc = null;
            analysedThisSession.Add(j.key);
            j.phase = 4;
        }

        // phase 4: into the database
        static void Finish(Job j)
        {
            var toggle = j.toggle;
            for (int i = 0; i < j.found.Count; i++)
            {
                var h = j.found[i]; string k = h.scene + "|" + h.path + "|" + h.kind; Item prev;
                int gid = j.foundGo[i].GetInstanceID();
                string had0; if (!goKey.TryGetValue(gid, out had0) || had0 != k) goKey[gid] = k;
                bool had = db.TryGetValue(k, out prev);
                string verdict; if (toggle != null && toggle.TryGetValue(gid, out verdict)) h.toggle = verdict; else h.toggle = had ? prev.toggle : "";
                if (had && !string.IsNullOrEmpty(prev.toggle)) h.toggle = prev.toggle;   // a "switchable" verdict sticks
                if (!had || h.toggle != prev.toggle) { db[k] = h; j.known.Add(h.path); Dirty(); }
            }
            scans++;
            if (lastScanLevels.Add(j.key)) levelsSeen = lastScanLevels.Count;
        }
        static readonly HashSet<string> lastScanLevels = new HashSet<string>();
        static readonly Dictionary<int, string> goKey = new Dictionary<int, string>();   // live object -> db key (this session's scans)

        // what the scanner knows about one object: null if it never saw it hidden
        public static string Verdict(GameObject go)
        {
            if (go == null) return null;
            string k; if (!goKey.TryGetValue(go.GetInstanceID(), out k)) return null;
            Item it; if (!db.TryGetValue(k, out it)) return null;
            bool seen = seenOn.Contains(k);
            string t = string.IsNullOrEmpty(it.toggle) ? "nothing in its level references it and it is not animated" : it.toggle;
            return "hidden-item scanner: " + it.kind + " in the level; " + t + (seen ? "; seen switched on in an earlier session (sessions before 0.36 also counted your own edits)" : "; never seen switched on in any scanned session");
        }

        static bool Mark(string k) { if (seenOn.Add(k)) { Dirty(); return true; } return false; }
        static void Dirty() { if (!dirty) dirtySince = Time.realtimeSinceStartup; dirty = true; reportDirty = true; }

        // ---------------------------------------------------------------- "can the game switch this on?"
        static readonly Dictionary<int, Item> tgt = new Dictionary<int, Item>();
        static readonly List<Edge> edges = new List<Edge>();
        static int srcGo; static string srcComp; static int work;

        static bool Bookkeeping(string t) { return t == "SubsceneNode" || t == "SubsceneCuller" || t == "LateAwakeNode" || t == "PlayMakerFixedUpdate" || t == "BoundsCullerRuntime" || t == "SubsceneContentRoot"; }

        static void Hit(UObj o, string label)
        {
            if (o == null) return;
            var go = o as GameObject; if (go == null) { var cp = o as Component; if (cp != null) go = cp.gameObject; }
            if (go == null) return;
            int id = go.GetInstanceID();
            if (id == srcGo || !tgt.ContainsKey(id) || label.StartsWith("_cached", StringComparison.Ordinal)) return;
            edges.Add(new Edge { src = srcGo, dst = id, via = srcComp + "." + label });
        }

        static void WalkFsm(PlayMakerFSM fsm)
        {
            var f = fsm.Fsm; if (f == null) return;
            try
            {
                var vars = fsm.FsmVariables;
                foreach (var v in vars.GameObjectVariables) if (v != null) Hit(v.Value, "FSM var " + v.Name);
                foreach (var v in vars.ObjectVariables) if (v != null) Hit(v.Value, "FSM var " + v.Name);
            }
            catch { }
            if (f.States == null) return;
            foreach (var st in f.States)
            {
                if (st == null) continue;
                FsmStateAction[] acts; try { acts = st.Actions; } catch { continue; }
                if (acts == null) continue;
                foreach (var ac in acts) if (ac != null) { work = 0; Walk(ac, "FSM '" + fsm.FsmName + "' > " + st.Name + " > " + ac.GetType().Name + ".", 0); }
            }
        }

        static readonly Dictionary<Type, FieldInfo[]> plans = new Dictionary<Type, FieldInfo[]>();
        static FieldInfo[] PlanFor(Type t)
        {
            FieldInfo[] p; if (plans.TryGetValue(t, out p)) return p;
            var l = new List<FieldInfo>();
            for (var k = t; k != null && k != typeof(MonoBehaviour) && k != typeof(Behaviour) && k != typeof(Component) && k != typeof(object) && k != typeof(FsmStateAction); k = k.BaseType)
                foreach (var f in k.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var ft = f.FieldType;
                    if (!ReferenceIndex.Interesting(ft)) continue;
                    l.Add(f);
                }
            p = l.ToArray(); plans[t] = p; return p;
        }

        static void Walk(object o, string prefix, int depth)
        {
            if (o == null || depth > 3) return;
            foreach (var f in PlanFor(o.GetType()))
            {
                if (++work > 20000) return;
                object v; try { v = f.GetValue(o); } catch { continue; }
                if (v != null) Value(v, prefix + f.Name, depth);
            }
        }

        static void Value(object v, string label, int depth)
        {
            var uo = v as UObj; if (uo != null) { Hit(uo, label); return; }
            if (v is UObj) return;
            var fg = v as FsmGameObject; if (fg != null) { Hit(fg.Value, label); return; }
            var fob = v as FsmObject; if (fob != null) { Hit(fob.Value, label); return; }
            var fod = v as FsmOwnerDefault;
            if (fod != null) { if (fod.OwnerOption == OwnerDefaultOption.SpecifyGameObject && fod.GameObject != null) Hit(fod.GameObject.Value, label); return; }
            if (v is NamedVariable || v is FsmEvent) return;
            var t = v.GetType();
            if (t.IsArray || v is IList)
            {
                var list = (IList)v; int n = Math.Min(list.Count, 512);
                for (int i = 0; i < n; i++) { if (++work > 20000) return; object e; try { e = list[i]; } catch { break; } if (e != null) Value(e, label + "[" + i + "]", depth); }
                return;
            }
            if (v is IDictionary || v is IEnumerable) return;
            if (t.IsValueType || t.IsSerializable || (t.IsClass && t.Namespace == null)) Walk(v, label + ".", depth + 1);
        }

        // ---------------------------------------------------------------- persistence (game folder: _mod\hidden\)
        static string Tsv(string x) { return (x ?? "").Replace('\t', ' ').Replace('\n', ' '); }

        static void Load()
        {
            loaded = true;
            try
            {
                if (!Directory.Exists(Dir)) return;   // nothing saved yet; the folder is created by the first save
                foreach (var f in new[] { "hidden_db.tsv", "seen_on.txt" })
                {
                    string main = Path.Combine(Dir, f), tmp = main + ".tmp";
                    if (!File.Exists(main) && File.Exists(tmp)) File.Move(tmp, main);
                }
                string p = Path.Combine(Dir, "hidden_db.tsv");
                if (File.Exists(p))
                {
                    bool first = true;
                    foreach (var l in File.ReadAllLines(p))
                    {
                        if (first) { first = false; continue; }
                        var f = l.Split('\t'); if (f.Length < 5) continue;
                        var it = new Item { scene = f[0], path = f[1], name = f[2], kind = f[3], toggle = f[4] };
                        db[it.scene + "|" + it.path + "|" + it.kind] = it;
                        HashSet<string> kp; if (!knownPaths.TryGetValue(it.scene, out kp)) knownPaths[it.scene] = kp = new HashSet<string>();
                        kp.Add(it.path);
                    }
                }
                string s = Path.Combine(Dir, "seen_on.txt");
                if (File.Exists(s)) foreach (var l in File.ReadAllLines(s)) if (l.Length > 0) seenOn.Add(l);
                DevLog.Write("[hidden] passive scanner: " + db.Count + " known hidden objects, " + seenOn.Count + " seen switched on (" + Dir + ")");
            }
            catch (Exception e)
            {
                // keep the existing files untouched: saving now would replace the history with this session only
                loadFailed = true;
                DevLog.Error("hidden load (scanner stopped, files left as they are)", e);
            }
        }

        static void MaybeSave(float t)
        {
            if (!dirty || t - savedAt < SaveEvery) return;
            Save(reportDirty && t - reportAt >= ReportEvery, false);
        }

        // game quit: the raw files and the two lists, written before the call returns
        public static void SaveIfDirty() { if (dirty || reportDirty) Save(true, true); }

        public static void Save() { Save(true, true); }

        static readonly object fileGate = new object();
        static volatile bool writing;
        // every snapshot gets a number; a write never replaces the files with an older snapshot (a background save that
        // was queued before "hidden save", the quit save or "hidden reset")
        static int snapSeq, writtenSeq;

        // The texts are built and written on a worker thread from a snapshot (plain strings); a save that must be on
        // disk when it returns (quit, "hidden save") writes on the caller's thread and waits for a running worker.
        static void Save(bool reports, bool wait)
        {
            if (loadFailed || !loaded) return;
            if (writing && !wait) return;
            savedAt = Time.realtimeSinceStartup; dirty = false;
            if (reports) { reportAt = savedAt; reportDirty = false; }
            var items = new Item[db.Count]; db.Values.CopyTo(items, 0);
            var keys = new string[db.Count]; db.Keys.CopyTo(keys, 0);
            var seen = new string[seenOn.Count]; seenOn.CopyTo(seen);
            string dir = Dir;
            int seq = ++snapSeq;
            if (wait)
            {
                try { int n = Write(seq, dir, items, keys, seen, reports); lastMsg = "saved: " + n + " listed of " + items.Length + " hidden (" + DateTime.Now.ToString("HH:mm:ss") + ")"; }
                catch (Exception e) { dirty = true; DevLog.Error("hidden save", e); }
                return;
            }
            writing = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Write(seq, dir, items, keys, seen, reports); lastMsg = "saved " + items.Length + " hidden (" + DateTime.Now.ToString("HH:mm:ss") + ")"; }
                catch (Exception e) { dirty = true; DevLog.Error("hidden save", e); }
                finally { writing = false; }
            });
        }

        // any thread; returns the number of listed items (0 when the lists were not written)
        static int Write(int seq, string dir, Item[] items, string[] keys, string[] seen, bool reports)
        {
            lock (fileGate)
            {
                if (seq <= writtenSeq) return 0;   // a newer snapshot is on disk already
                writtenSeq = seq;
                Directory.CreateDirectory(dir);
                var sb = new StringBuilder("level\tpath\tname\tkind\ttoggle\n", 64 + items.Length * 96);
                foreach (var h in items) sb.Append(Tsv(h.scene)).Append('\t').Append(Tsv(h.path)).Append('\t').Append(Tsv(h.name)).Append('\t').Append(h.kind).Append('\t').Append(Tsv(h.toggle)).Append('\n');
                SafeFile.WriteAllText(Path.Combine(dir, "hidden_db.tsv"), sb.ToString());
                var so = new StringBuilder(seen.Length * 64); foreach (var x in seen) so.Append(x).Append('\n');
                SafeFile.WriteAllText(Path.Combine(dir, "seen_on.txt"), so.ToString());
                return reports ? WriteFinal(dir, items, keys, seen) : 0;
            }
        }

        // hidden objects nothing in the game switches on, sorted by level name ('#' ignored, master last)
        static int WriteFinal(string dir, Item[] items, string[] keys, string[] seen)
        {
            var seenSet = new HashSet<string>(seen);
            var keep = new List<Item>();
            for (int i = 0; i < items.Length; i++) if (string.IsNullOrEmpty(items[i].toggle) && !seenSet.Contains(keys[i])) keep.Add(items[i]);
            // one sort key per level name, not per comparison
            var levelKey = new Dictionary<string, string>();
            foreach (var h in keep) if (!levelKey.ContainsKey(h.scene)) levelKey[h.scene] = (h.scene.StartsWith("(", StringComparison.Ordinal) ? "~" : "") + h.scene.TrimStart('#').ToLowerInvariant();
            var arr = keep.ToArray(); var sortKeys = new string[arr.Length];
            for (int i = 0; i < arr.Length; i++) sortKeys[i] = levelKey[arr[i].scene] + "\n" + arr[i].path;
            Array.Sort(sortKeys, arr, StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(); foreach (var h in arr) { int n; counts.TryGetValue(h.scene, out n); counts[h.scene] = n + 1; }
            var names = new StringBuilder(); var full = new StringBuilder();
            full.Append("# hidden objects that nothing in the game switches on: ").Append(arr.Length).Append(" items in ").Append(counts.Count).Append(" levels visited so far, sorted by level\n# level | path | how it is hidden\n");
            string cur = null;
            foreach (var h in arr)
            {
                if (h.scene != cur) { cur = h.scene; full.Append("\n=== ").Append(cur).Append("  (").Append(counts[cur]).Append(") ===\n"); }
                names.Append(h.name).Append('\n'); full.Append(h.scene).Append(" | ").Append(h.path).Append(" | ").Append(h.kind).Append('\n');
            }
            SafeFile.WriteAllText(Path.Combine(dir, "hidden_names.txt"), names.ToString());
            SafeFile.WriteAllText(Path.Combine(dir, "hidden_items.txt"), full.ToString());
            return arr.Length;
        }

        // ---------------------------------------------------------------- bridge: hidden [status|save|on|off|scan [seconds]|reset] [json]
        public static string Command(List<string> a)
        {
            Sync();
            bool json = a.Count > 1 && a[a.Count - 1] == "json";
            int argc = json ? a.Count - 1 : a.Count;
            string op = argc > 1 ? a[1].ToLowerInvariant() : "status";
            if (op == "save")
            {
                if (!loaded) return json ? JsonLine.Obj("ok", false, "error", "nothing to save: the scanner has not run in this session") : "failed: nothing to save, the scanner has not run in this session (hidden on | hidden scan)";
                if (loadFailed) return json ? JsonLine.Obj("ok", false, "error", "the database could not be read at start; files left as they are") : "failed: the database could not be read at start, files left as they are (see the log)";
                Save();
                return json ? StatusJson() : lastMsg;
            }
            if (op == "on" || op == "off")
            {
                enabled = op == "on"; Sync();
                if (json) return StatusJson();
                return "passive hidden scanner " + (op == "on" ? "on" : "off") + (op == "on" ? " (kept on: " + ConfigKey + "=1)" : Background.Wanted(Background.Hidden) ? " (still wanted for a moment by a panel or command)" : "");
            }
            if (op == "scan")
            {
                float sec = 120f;
                if (argc > 2 && (!Fmt.TryF(a[2], out sec) || sec <= 0 || sec > 3600)) return "usage: hidden scan [seconds 1..3600]";
                Background.Want(Background.Hidden, sec);
                if (json) return StatusJson();
                return "passive hidden scanner runs for " + Fmt.F(sec, "0") + " s (hidden on keeps it on)";
            }
            if (op == "reset")
            {
                Abort();
                db.Clear(); seenOn.Clear(); knownPaths.Clear(); lastScan.Clear(); analysedThisSession.Clear(); lastScanLevels.Clear(); goKey.Clear(); pathCaches.Clear();
                dirty = reportDirty = false; levelsSeen = 0;
                try { lock (fileGate) { writtenSeq = ++snapSeq; } } catch { }
                try { lock (fileGate) foreach (var f in new[] { "hidden_db.tsv", "seen_on.txt", "hidden_names.txt", "hidden_items.txt" }) { var p = Path.Combine(Dir, f); if (File.Exists(p)) File.Delete(p); } } catch { }
                return json ? StatusJson() : "hidden database cleared";
            }
            if (!loaded) Load();
            if (json) return StatusJson();
            int sw = 0; foreach (var h in db.Values) if (!string.IsNullOrEmpty(h.toggle)) sw++;
            bool on = enabled;
            return "passive hidden scanner " + (on ? "on" : "off") + ": " + db.Count + " hidden known, " + sw + " switchable, " + seenOn.Count + " seen on, " + levelsSeen + " levels scanned this session, " + scans + " scans  | " + lastMsg +
                   (loadFailed ? "\n  the database could not be read at start: scanner stopped, files left as they are" : "") +
                   (!on && Background.Wanted(Background.Hidden) ? "\n  running for a while (asked for by a panel or hidden scan)" : "") +
                   (!on && !loadFailed && !Background.Wanted(Background.Hidden) ? "\n  off: hidden on keeps it on, hidden scan runs it for two minutes" : "") +
                   "\n  files: " + Dir;
        }

        public static string StatusJson()
        {
            int sw = 0; foreach (var h in db.Values) if (!string.IsNullOrEmpty(h.toggle)) sw++;
            return JsonLine.Obj("ok", !loadFailed, "on", enabled, "running", Running, "scanning", Scanning, "loaded", loaded, "loadFailed", loadFailed,
                "known", db.Count, "switchable", sw, "seenOn", seenOn.Count, "levelsScanned", levelsSeen, "scans", scans, "unsaved", dirty, "lastSave", lastMsg, "dir", Dir);
        }
    }
}
