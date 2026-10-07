using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
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
    // Output (rewritten whenever something new is found): hidden_names.txt (one name per line) and hidden_items.txt
    // (level | path | how it is hidden), sorted by level. hidden_db.tsv / seen_on.txt hold the raw state between sessions.
    //
    // Replaces the forced level sweep / save-point tour (removed 0.30.0): loading levels outside the game's own streaming
    // crashed the engine in the game's AnimationPreAwake (4 crashes, 2026-09-28).
    public static class PassiveHidden
    {
        public static bool enabled = true;
        const float LevelRescan = 20f, MasterRescan = 30f, SaveEvery = 20f;

        struct Item { public string scene, path, name, kind, toggle; }
        struct Edge { public int src, dst; public string via; }

        static readonly Dictionary<string, Item> db = new Dictionary<string, Item>();          // scene|path|kind -> item
        static readonly HashSet<string> seenOn = new HashSet<string>();                         // scene|path|kind
        static readonly HashSet<string> knownPaths = new HashSet<string>();                     // scene|path
        static readonly Dictionary<string, float> lastScan = new Dictionary<string, float>();
        static readonly HashSet<string> analysedThisSession = new HashSet<string>();
        static readonly List<SubsceneCuller> cullers = new List<SubsceneCuller>();
        static float nextTick, cullersAt = -100f, masterAt = -100f, savedAt, dirtySince = -1f;
        static bool loaded, dirty, loadFailed;
        static int rr, scans, levelsSeen;
        static string lastMsg = "";

        static string Dir { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "hidden"); } }

        // ---------------------------------------------------------------- driver (one level per step, spread over frames)
        public static void Tick()
        {
            if (!enabled) return;
            float t = Time.realtimeSinceStartup;
            if (t < nextTick) return;
            nextTick = t + 0.5f;
            if (!loaded) Load();
            if (!G.SavepointsReady) return;
            var ch = G.MainCharacter; if (ch == null || !ch.isCharacterActive) return;

            if (t - cullersAt > 5f || Levels.Changed(ref seenStream))
            {
                cullersAt = t; cullers.Clear();
                foreach (var o in UObj.FindObjectsOfType(typeof(SubsceneCuller))) { var c = o as SubsceneCuller; if (c != null && c.IsValid) cullers.Add(c); }
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
                ScanLevel(key, c.SubsceneNode.transform);
                MaybeSave(t);
                return;
            }
            if (t - masterAt > MasterRescan)
            {
                masterAt = t;
                foreach (var tr in Inspector.AllSceneTransforms())
                    if (tr != null && tr.parent == null && tr.GetComponent<SubsceneNode>() == null) ScanLevel("(master / persistent)", tr);
            }
            MaybeSave(t);
        }
        static int seenStream;

        static string Key(SubsceneCuller c)
        {
            string n = c.SceneName; n = n.Substring(n.LastIndexOf('/') + 1);
            return n.EndsWith(".unity") ? n.Substring(0, n.Length - 6) : n;
        }

        // ---------------------------------------------------------------- scanning one active level
        static void ScanLevel(string key, Transform root)
        {
            try
            {
                var all = root.GetComponentsInChildren<Transform>(true);
                var paths = new Dictionary<int, string>(all.Length);
                var sib = new Dictionary<string, int>();
                var offAnc = new Dictionary<int, List<int>>();
                var found = new List<Item>(); var foundGo = new List<GameObject>();
                bool unseen = false;
                foreach (var t in all)
                {
                    if (t == null) continue;
                    var g = t.gameObject; int id = g.GetInstanceID();
                    // same-named siblings would share one path (and one database entry): number the 2nd, 3rd ... as
                    // "name #2", "name #3" in sibling order, which comes from the level data and is the same every session
                    string pp; string p = t.parent != null && paths.TryGetValue(t.parent.GetInstanceID(), out pp) ? pp + "/" + t.name : t.name;
                    if (t != root)
                    {
                        string sk = (t.parent != null ? t.parent.GetInstanceID() : 0) + "/" + t.name; int nth; sib.TryGetValue(sk, out nth); sib[sk] = ++nth;
                        if (nth > 1) p += " #" + nth;
                    }
                    paths[t.GetInstanceID()] = p;
                    // objects spawned at runtime (pools, effects: "(Clone)") are new instances every session and not
                    // level content; recording them would pile up a new entry per spawn / per reboot
                    if (p.IndexOf("(Clone)", StringComparison.Ordinal) >= 0) continue;
                    List<int> pa = null; if (t != root && t.parent != null) offAnc.TryGetValue(t.parent.gameObject.GetInstanceID(), out pa);
                    bool structural = t == root || g.GetComponent<SubsceneNode>() != null || g.GetComponent<SubsceneContentRoot>() != null;
                    if (structural) { offAnc[id] = pa; continue; }
                    List<int> mine = pa; if (!g.activeSelf) { mine = pa != null ? new List<int>(pa) : new List<int>(); mine.Add(id); }
                    offAnc[id] = mine;
                    var rs = g.GetComponents<Renderer>(); var cols = g.GetComponents<Collider>();
                    string pk = key + "|" + p;
                    if (knownPaths.Contains(pk) && !Changes.IsChanged(g))   // your own edits are not the game switching it on
                    {
                        // a known hidden object seen on right now -> the game does switch it on
                        if (g.activeSelf && Mark(pk + "|off")) { }
                        foreach (var r in rs) if (r != null && r.enabled) { Mark(pk + "|renderer off"); break; }
                        foreach (var c in cols) if (c != null && c.enabled) { Mark(pk + "|" + (c.isTrigger ? "trigger off" : "collider off")); break; }
                    }
                    string kind = null;
                    if (!g.activeSelf) kind = "off";
                    else
                    {
                        if (rs.Length > 0) { bool any = false; foreach (var r in rs) if (r != null && r.enabled) { any = true; break; } if (!any) kind = "renderer off"; }
                        if (kind == null) foreach (var c in cols) if (c != null && !c.enabled) { kind = c.isTrigger ? "trigger off" : "collider off"; break; }
                    }
                    if (kind == null) continue;
                    found.Add(new Item { scene = key, path = p, name = t.name, kind = kind }); foundGo.Add(g);
                    if (!db.ContainsKey(key + "|" + p + "|" + kind)) unseen = true;
                }
                // reference / animation analysis: first time a level is seen this session, or when it shows something new
                Dictionary<int, string> toggle = null;
                if (found.Count > 0 && (unseen || !analysedThisSession.Contains(key)))
                {
                    toggle = Analyse(root, all, paths, offAnc, found, foundGo);
                    analysedThisSession.Add(key);
                }
                for (int i = 0; i < found.Count; i++)
                {
                    var h = found[i]; string k = h.scene + "|" + h.path + "|" + h.kind; Item prev;
                    goKey[foundGo[i].GetInstanceID()] = k;
                    bool had = db.TryGetValue(k, out prev);
                    string verdict; if (toggle != null && toggle.TryGetValue(foundGo[i].GetInstanceID(), out verdict)) h.toggle = verdict; else h.toggle = had ? prev.toggle : "";
                    if (had && !string.IsNullOrEmpty(prev.toggle)) h.toggle = prev.toggle;   // a "switchable" verdict sticks
                    if (!had || h.toggle != prev.toggle) { db[k] = h; knownPaths.Add(h.scene + "|" + h.path); Dirty(); }
                }
                scans++;
                if (!lastScanLevels.Contains(key)) { lastScanLevels.Add(key); levelsSeen = lastScanLevels.Count; }
            }
            catch (Exception e) { DevLog.Error("hidden scan " + key, e); }
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
        static void Dirty() { if (!dirty) dirtySince = Time.realtimeSinceStartup; dirty = true; }

        // ---------------------------------------------------------------- "can the game switch this on?"
        static Dictionary<int, Item> tgt = new Dictionary<int, Item>();
        static readonly List<Edge> edges = new List<Edge>();
        static int srcGo; static string srcComp; static int work;

        static bool Bookkeeping(string t) { return t == "SubsceneNode" || t == "SubsceneCuller" || t == "LateAwakeNode" || t == "PlayMakerFixedUpdate" || t == "BoundsCullerRuntime" || t == "SubsceneContentRoot"; }

        static Dictionary<int, string> Analyse(Transform root, Transform[] all, Dictionary<int, string> paths, Dictionary<int, List<int>> offAnc, List<Item> found, List<GameObject> foundGo)
        {
            var toggle = new Dictionary<int, string>();
            // animation on the object or a parent (component presence only: no Animation call on inactive objects)
            foreach (var g in foundGo)
                for (var u = g.transform; u != null && u != root; u = u.parent)
                {
                    var am = u.GetComponent<Animator>(); var an = u.GetComponent<Animation>();
                    if ((am != null && am.runtimeAnimatorController != null) || an != null) { toggle[g.GetInstanceID()] = "animated"; break; }
                }
            tgt.Clear(); edges.Clear();
            for (int i = 0; i < foundGo.Count; i++) tgt[foundGo[i].GetInstanceID()] = found[i];
            foreach (var t in all)
            {
                if (t == null) continue;
                var go = t.gameObject; srcGo = go.GetInstanceID();
                foreach (var cc in go.GetComponents<Component>())
                {
                    var mb = cc as MonoBehaviour; if (mb == null) continue;
                    srcComp = cc.GetType().Name; if (Bookkeeping(srcComp)) continue;
                    work = 0;
                    try { var fsm = mb as PlayMakerFSM; if (fsm != null) WalkFsm(fsm); else Walk(mb, "", 0); } catch { }
                }
            }
            tgt.Clear();
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
            return toggle;
        }

        static void Hit(UObj o, string label)
        {
            if (o == null) return;
            var go = o as GameObject; if (go == null) { var cp = o as Component; if (cp != null) go = cp.gameObject; }
            if (go == null) return;
            int id = go.GetInstanceID();
            if (id == srcGo || !tgt.ContainsKey(id) || label.StartsWith("_cached")) return;
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
                    if (ft.IsPrimitive || ft.IsEnum || ft.IsPointer || ft == typeof(string) || typeof(Delegate).IsAssignableFrom(ft)) continue;
                    if (ft == typeof(Vector2) || ft == typeof(Vector3) || ft == typeof(Vector4) || ft == typeof(Quaternion) || ft == typeof(Color) || ft == typeof(Rect) || ft == typeof(Matrix4x4) || ft == typeof(Bounds)) continue;
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
                Directory.CreateDirectory(Dir);
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
                        db[it.scene + "|" + it.path + "|" + it.kind] = it; knownPaths.Add(it.scene + "|" + it.path);
                    }
                }
                string s = Path.Combine(Dir, "seen_on.txt");
                if (File.Exists(s)) foreach (var l in File.ReadAllLines(s)) if (l.Length > 0) seenOn.Add(l);
                DevLog.Write("[hidden] passive scanner: " + db.Count + " known hidden objects, " + seenOn.Count + " seen switched on (" + Dir + ")");
            }
            catch (Exception e)
            {
                // keep the existing files untouched: saving now would replace the history with this session only
                loadFailed = true; enabled = false;
                DevLog.Error("hidden load (scanner stopped, files left as they are)", e);
            }
        }

        static void MaybeSave(float t)
        {
            if (!dirty || t - savedAt < SaveEvery) return;
            Save();
        }

        public static void SaveIfDirty() { if (dirty) Save(); }

        // write to a temp file, then swap: a crash or power loss mid-write never leaves a half-written database
        static void WriteAtomic(string name, string text)
        {
            string p = Path.Combine(Dir, name), tmp = p + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(p)) File.Delete(p);
            File.Move(tmp, p);
        }

        public static void Save()
        {
            if (loadFailed || !loaded) return;
            savedAt = Time.realtimeSinceStartup; dirty = false;
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder("level\tpath\tname\tkind\ttoggle\n");
                foreach (var h in db.Values) sb.Append(Tsv(h.scene)).Append('\t').Append(Tsv(h.path)).Append('\t').Append(Tsv(h.name)).Append('\t').Append(h.kind).Append('\t').Append(Tsv(h.toggle)).Append('\n');
                WriteAtomic("hidden_db.tsv", sb.ToString());
                var so = new StringBuilder(); foreach (var x in seenOn) so.Append(x).Append('\n');
                WriteAtomic("seen_on.txt", so.ToString());
                int n = WriteFinal();
                lastMsg = "saved: " + n + " listed of " + db.Count + " hidden (" + DateTime.Now.ToString("HH:mm:ss") + ")";
            }
            catch (Exception e) { DevLog.Error("hidden save", e); }
        }

        // hidden objects nothing in the game switches on, sorted by level name ('#' ignored, master last)
        static int WriteFinal()
        {
            var keep = new List<Item>();
            foreach (var kv in db) if (string.IsNullOrEmpty(kv.Value.toggle) && !seenOn.Contains(kv.Key)) keep.Add(kv.Value);
            Func<string, string> lk = sc => (sc.StartsWith("(") ? "~" : "") + sc.TrimStart('#').ToLowerInvariant();
            keep.Sort((x, y) => { int c = string.CompareOrdinal(lk(x.scene), lk(y.scene)); return c != 0 ? c : string.CompareOrdinal(x.path, y.path); });
            var counts = new Dictionary<string, int>(); foreach (var h in keep) { int n; counts.TryGetValue(h.scene, out n); counts[h.scene] = n + 1; }
            var names = new StringBuilder(); var full = new StringBuilder();
            full.Append("# hidden objects that nothing in the game switches on: ").Append(keep.Count).Append(" items in ").Append(counts.Count).Append(" levels visited so far, sorted by level\n# level | path | how it is hidden\n");
            string cur = null;
            foreach (var h in keep)
            {
                if (h.scene != cur) { cur = h.scene; full.Append("\n=== ").Append(cur).Append("  (").Append(counts[cur]).Append(") ===\n"); }
                names.Append(h.name).Append('\n'); full.Append(h.scene).Append(" | ").Append(h.path).Append(" | ").Append(h.kind).Append('\n');
            }
            WriteAtomic("hidden_names.txt", names.ToString());
            WriteAtomic("hidden_items.txt", full.ToString());
            return keep.Count;
        }

        // ---------------------------------------------------------------- bridge: hidden [status|save|on|off|reset]
        public static string Command(List<string> a)
        {
            string op = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            if (op == "save") { Save(); return lastMsg; }
            if (op == "on" || op == "off") { enabled = op == "on"; return "passive hidden scanner " + (enabled ? "on" : "off"); }
            if (op == "reset")
            {
                db.Clear(); seenOn.Clear(); knownPaths.Clear(); lastScan.Clear(); analysedThisSession.Clear(); lastScanLevels.Clear();
                try { foreach (var f in new[] { "hidden_db.tsv", "seen_on.txt", "hidden_names.txt", "hidden_items.txt" }) { var p = Path.Combine(Dir, f); if (File.Exists(p)) File.Delete(p); } } catch { }
                return "hidden database cleared";
            }
            int sw = 0; foreach (var h in db.Values) if (!string.IsNullOrEmpty(h.toggle)) sw++;
            return "passive hidden scanner " + (enabled ? "on" : "off") + ": " + db.Count + " hidden known, " + sw + " switchable, " + seenOn.Count + " seen on, " + levelsSeen + " levels scanned this session, " + scans + " scans  | " + lastMsg + "\n  files: " + Dir;
        }
    }
}
