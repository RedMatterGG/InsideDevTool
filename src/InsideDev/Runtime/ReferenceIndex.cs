using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    public enum Provenance { SerializedField, ReflectionField, SignalConnection, StateMachine, Adapter, Observed, Inferred, Code }

    // source object/component --field--> target object/component
    public sealed class RefEdge
    {
        public int ownerGo;               // object whose indexing produced this edge (lifecycle)
        public int srcGo;                 // logical source GameObject (session id); differs from owner for connector wiring
        public Component srcComp;
        public string srcType;
        public string field;              // e.g. "moveTarget", "targets[2]", "FSM 'PM' > State 3 > PDMoveTowards.gameObject"
        public UObj dst;                  // referenced object (GameObject, Component or asset)
        public int dstObj;                // instance id of dst
        public int dstGo;                 // GameObject id of dst if it lives in the scene, else 0 (asset)
        public string dstType;
        public string dstName;
        public Provenance prov;
        public bool noise;                // self-reference or engine bookkeeping; hidden unless asked for
        public bool DstAlive { get { return dst != null; } }
    }

    // Forward + reverse reference index over the RuntimeObjectDatabase. Built incrementally within a frame
    // budget; only fields are read (no property getters except whitelisted PlayMaker value holders).
    public static class ReferenceIndex
    {
        static readonly Dictionary<int, List<RefEdge>> fwd = new Dictionary<int, List<RefEdge>>(16384);     // srcGo -> edges
        static readonly Dictionary<int, List<RefEdge>> rev = new Dictionary<int, List<RefEdge>>(16384);     // dstGo (or asset id) -> edges
        static readonly Dictionary<int, List<RefEdge>> owned = new Dictionary<int, List<RefEdge>>(16384);   // ownerGo -> edges it produced
        static readonly Dictionary<int, int> indexedPass = new Dictionary<int, int>(32768);                  // goId -> db pass when indexed
        static Dictionary<int, List<SignalConnection>> signalsByOut = new Dictionary<int, List<SignalConnection>>();
        static readonly Queue<int> queue = new Queue<int>();
        static readonly HashSet<int> queued = new HashSet<int>();
        public static int edges, indexedObjects, lastDbPass = -1, fullRuns;
        public static float budgetMs = 1.5f, lastFullMs;
        static float runMs;
        static readonly Stopwatch sw = new Stopwatch();
        public static bool Busy { get { return queue.Count > 0; } }
        public static int Pending { get { return queue.Count; } }

        // ---------------------------------------------------------------- scheduling
        public static void Tick()
        {
            try
            {
                if (!ObjectDatabase.Ready) return;
                if (ObjectDatabase.completedPasses != lastDbPass)
                {
                    lastDbPass = ObjectDatabase.completedPasses;
                    Sync();
                }
                if (queue.Count > 0) Work(budgetMs);
            }
            catch (Exception e) { DevLog.Error("reference index", e); }
        }

        // enqueue new objects, drop edges of objects that left the database
        static void Sync()
        {
            BuildSignalMap();
            var gone = new List<int>();
            foreach (var id in indexedPass.Keys) if (ObjectDatabase.Get(id) == null) gone.Add(id);
            foreach (var id in gone) Remove(id);
            int before = queue.Count;
            foreach (var r in ObjectDatabase.all)
                if (r.go != null && !indexedPass.ContainsKey(r.id) && queued.Add(r.id)) queue.Enqueue(r.id);
            if (before == 0 && queue.Count > 0) { runMs = 0; }
        }

        static void Work(float budget)
        {
            sw.Reset(); sw.Start();
            int n = 0;
            while (queue.Count > 0)
            {
                int id = queue.Dequeue();
                queued.Remove(id);
                var r = ObjectDatabase.Get(id);
                if (r != null) Index(r);
                if ((++n & 7) == 0 && sw.Elapsed.TotalMilliseconds > budget) break;
            }
            sw.Stop();
            runMs += (float)sw.Elapsed.TotalMilliseconds;
            if (queue.Count == 0)
            {
                fullRuns++; lastFullMs = runMs;
                DevLog.Write("[refs] index up to date: " + indexedObjects + " objects, " + edges + " edges, " + runMs.ToString("0") + " ms total work");
            }
        }

        // synchronous re-index of one object (fresh values for the inspector)
        public static void Refresh(GameObject g)
        {
            var r = ObjectDatabase.Get(g);
            if (r != null) Index(r);
        }

        // make sure the whole index is complete now (explicit user queries such as "find referencers")
        static void BuildSignalMap()
        {
            var map = new Dictionary<int, List<SignalConnection>>();
            SignalManager sm = null;
            try { sm = PersistentBehaviour<SignalManager>.instance; } catch { }
            if (sm != null && sm.connections != null)
                foreach (var c in sm.connections)
                {
                    if (c == null || c.signalOutGameObject == null || c.signalInGameObject == null) continue;
                    int k = c.signalOutGameObject.GetInstanceID();
                    List<SignalConnection> l;
                    if (!map.TryGetValue(k, out l)) map[k] = l = new List<SignalConnection>(2);
                    l.Add(c);
                }
            signalsByOut = map;
        }

        public static void EnsureComplete()
        {
            ObjectDatabase.EnsureFresh(5f);
            if (ObjectDatabase.completedPasses != lastDbPass) { lastDbPass = ObjectDatabase.completedPasses; Sync(); }
            if (queue.Count > 0) Work(100000f);
        }

        static void Remove(int ownerId)
        {
            List<RefEdge> list;
            if (owned.TryGetValue(ownerId, out list))
            {
                foreach (var e in list)
                {
                    List<RefEdge> l;
                    if (fwd.TryGetValue(e.srcGo, out l)) { l.Remove(e); if (l.Count == 0) fwd.Remove(e.srcGo); }
                    int key = e.dstGo != 0 ? e.dstGo : e.dstObj;
                    if (rev.TryGetValue(key, out l)) { l.Remove(e); if (l.Count == 0) rev.Remove(key); }
                }
                edges -= list.Count;
                owned.Remove(ownerId);
            }
            if (indexedPass.Remove(ownerId)) indexedObjects--;
        }

        // ---------------------------------------------------------------- indexing one object
        static List<RefEdge> cur;
        static int curGo;
        static Component curComp;
        static string curType;
        static Provenance curProv;

        static void Index(ObjRecord r)
        {
            Remove(r.id);
            cur = new List<RefEdge>();
            curGo = r.id;
            work = 0; truncated = false;
            foreach (var c in r.go.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                curComp = c; curType = c.GetType().Name;
                try
                {
                    var fsm = c as PlayMakerFSM;
                    if (fsm != null) { IndexFsm(fsm); continue; }
                    var sc = c as SignalConnector;
                    if (sc != null)
                    {
                        curProv = Provenance.SignalConnection;
                        if (sc.sender != null && sc.receiver != null)
                            AddEdgeFrom(sc.sender, sc, "signal " + sc.signalOutName + " -> " + sc.signalInName, sc.receiver);
                    }
                    var mb = c as MonoBehaviour;
                    if (mb != null) { curProv = Provenance.SerializedField; Walk(mb, "", 0); IndexCode(mb); }
                }
                catch (Exception e) { DevLog.Write("[refs] " + r.path + " " + curType + ": " + e.GetType().Name + " " + e.Message); }
            }
            IndexRuntimeSignals(r.go);
            if (truncated) truncatedObjects++;
            owned[r.id] = cur;
            foreach (var e in cur)
            {
                List<RefEdge> l;
                if (!fwd.TryGetValue(e.srcGo, out l)) fwd[e.srcGo] = l = new List<RefEdge>(4);
                l.Add(e);
                int key = e.dstGo != 0 ? e.dstGo : e.dstObj;
                if (!rev.TryGetValue(key, out l)) rev[key] = l = new List<RefEdge>(2);
                l.Add(e);
            }
            edges += cur.Count;
            indexedPass[r.id] = ObjectDatabase.completedPasses;
            indexedObjects++;
            cur = null;
        }

        static void Add(string field, UObj target) { AddEdgeFrom(null, null, field, target); }

        // CODE edges: static singleton / FindObjectOfType dependencies of this script, resolved to the live object
        static void IndexCode(MonoBehaviour mb)
        {
            var deps = CodeDeps.For(mb.GetType());
            if (deps == null) return;
            var groups = new Dictionary<string, List<CodeDeps.Dep>>();
            foreach (var d in deps)
            {
                string k = d.via + "|" + d.target.FullName;
                List<CodeDeps.Dep> l; if (!groups.TryGetValue(k, out l)) { l = new List<CodeDeps.Dep>(); groups[k] = l; } l.Add(d);
            }
            foreach (var g in groups.Values)
            {
                var target = CodeDeps.Resolve(g[0]);
                if (target == null) continue;
                var members = new List<string>(); var methods = new List<string>();
                foreach (var d in g)
                {
                    string m = d.access + (d.member != null ? " " + d.member : "");
                    if (!members.Contains(m)) members.Add(m);
                    if (!methods.Contains(d.method)) methods.Add(d.method);
                }
                var prev = curProv; curProv = Provenance.Code;
                int before = cur.Count;
                AddEdgeFrom(null, null, "code: " + g[0].via + " → " + string.Join(", ", members.ToArray()) + "  (in " + string.Join(", ", methods.ToArray()) + ")", target);
                // global hubs (ScriptGlobals.boy, camera ...) are referenced by hundreds of scripts: keep, but as noise
                if (cur.Count > before && g[0].via.StartsWith("ScriptGlobals.")) cur[cur.Count - 1].noise = true;
                curProv = prev;
            }
        }

        // re-index objects carrying scripts of these types (used when the code dependency scan completes)
        public static void RequeueTypes(HashSet<Type> types)
        {
            var names = new HashSet<string>(); foreach (var t in types) names.Add(t.Name);
            foreach (var r in ObjectDatabase.all)
            {
                if (r.go == null || !indexedPass.ContainsKey(r.id)) continue;
                foreach (var c in r.comps) if (names.Contains(c)) { if (queued.Add(r.id)) queue.Enqueue(r.id); break; }
            }
        }

        // an edge whose logical source is another object (signal connector wiring: sender -> receiver)
        static void AddEdgeFrom(GameObject srcOverride, Component via, string field, UObj target)
        {
            if (target == null) return;
            if (++work > MaxWork) { truncated = true; return; }
            var e = new RefEdge { ownerGo = curGo, srcGo = curGo, srcComp = curComp, srcType = curType, field = field, dst = target, dstObj = target.GetInstanceID(), dstType = target.GetType().Name, dstName = target.name, prov = curProv };
            var go = target as GameObject;
            var comp = target as Component;
            if (go == null && comp != null) go = comp.gameObject;
            if (go != null && ObjectDatabase.Get(go) != null) e.dstGo = go.GetInstanceID();
            // connector wiring: logical source is the sender, the connector object owns the edge (lifecycle)
            if (srcOverride != null) e.srcGo = srcOverride.GetInstanceID();
            e.noise = (e.dstGo != 0 && e.dstGo == e.srcGo) || IsBookkeeping(curType, field);
            cur.Add(e);
        }

        // ---------------------------------------------------------------- reflection walk (fields only)
        sealed class Plan { public FieldInfo[] fields; }
        static readonly Dictionary<Type, Plan> plans = new Dictionary<Type, Plan>();
        const int MaxDepth = 3, MaxElems = 512, MaxWork = 20000;
        static int work; static bool truncated;
        public static int truncatedObjects;

        static Plan PlanFor(Type t)
        {
            Plan p;
            if (plans.TryGetValue(t, out p)) return p;
            var list = new List<FieldInfo>();
            for (var k = t; k != null && k != typeof(MonoBehaviour) && k != typeof(Behaviour) && k != typeof(Component) && k != typeof(object) && k != typeof(FsmStateAction); k = k.BaseType)
                foreach (var f in k.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (Interesting(f.FieldType)) list.Add(f);
            p = new Plan { fields = list.ToArray() };
            plans[t] = p;
            return p;
        }

        static bool Interesting(Type t)
        {
            if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t.IsPointer) return false;
            if (typeof(Delegate).IsAssignableFrom(t)) return false;
            if (t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4) || t == typeof(Quaternion) || t == typeof(Color) || t == typeof(Rect) || t == typeof(Matrix4x4) || t == typeof(Bounds)) return false;
            return true;
        }

        static void Walk(object o, string prefix, int depth)
        {
            if (o == null || depth > MaxDepth) return;
            var plan = PlanFor(o.GetType());
            foreach (var f in plan.fields)
            {
                if (++work > MaxWork) { truncated = true; return; }
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                if (v == null) continue;
                Value(v, prefix + f.Name, depth);
            }
        }

        static void Value(object v, string label, int depth)
        {
            var uo = v as UObj;
            if (uo != null) { Add(label, uo); return; }
            if (v is UObj) return;   // destroyed Unity object (== null)
            // PlayMaker value holders (whitelisted getters: plain field reads inside)
            var fg = v as FsmGameObject; if (fg != null) { if (fg.Value != null) Add(label, fg.Value); return; }
            var fob = v as FsmObject; if (fob != null) { if (fob.Value != null) Add(label, fob.Value); return; }
            var fod = v as FsmOwnerDefault;
            if (fod != null)
            {
                if (fod.OwnerOption == OwnerDefaultOption.SpecifyGameObject && fod.GameObject != null && fod.GameObject.Value != null) Add(label, fod.GameObject.Value);
                return;
            }
            if (v is NamedVariable || v is FsmEvent) return;
            var t = v.GetType();
            if (t.IsArray || v is IList)
            {
                var list = (IList)v;
                int n = Math.Min(list.Count, MaxElems);
                for (int i = 0; i < n; i++)
                {
                    if (++work > MaxWork) { truncated = true; return; }
                    object e;
                    try { e = list[i]; } catch { break; }
                    if (e != null) Value(e, label + "[" + i + "]", depth);
                }
                return;
            }
            if (v is IDictionary || v is IEnumerable) return;   // don't enumerate arbitrary collections
            if (t.IsValueType || t.IsSerializable || (t.IsClass && t.Namespace == null))
                Walk(v, label + ".", depth + 1);
        }

        // ---------------------------------------------------------------- PlayMaker state machines
        static void IndexFsm(PlayMakerFSM fsm)
        {
            curProv = Provenance.StateMachine;
            var f = fsm.Fsm;
            if (f == null) return;
            string fn = "FSM '" + fsm.FsmName + "'";
            try
            {
                var vars = fsm.FsmVariables;
                if (vars != null)
                {
                    foreach (var gv in vars.GameObjectVariables) if (gv != null && gv.Value != null) Add(fn + " var " + gv.Name, gv.Value);
                    foreach (var ov in vars.ObjectVariables) if (ov != null && ov.Value != null) Add(fn + " var " + ov.Name, ov.Value);
                }
            }
            catch { }
            var states = f.States;
            if (states == null) return;
            foreach (var st in states)
            {
                if (st == null) continue;
                FsmStateAction[] acts;
                try { acts = st.Actions; } catch { continue; }
                if (acts == null) continue;
                foreach (var a in acts)
                {
                    if (a == null) continue;
                    string label = fn + " > " + st.Name + " > " + a.GetType().Name + ".";
                    // owner-targeted actions act on the FSM's own object
                    Walk(a, label, 0);
                }
            }
        }

        // runtime signal connections registered in Playdead's SignalManager
        static void IndexRuntimeSignals(GameObject go)
        {
            List<SignalConnection> conns;
            if (!signalsByOut.TryGetValue(go.GetInstanceID(), out conns)) return;
            curProv = Provenance.SignalConnection; curComp = null; curType = "SignalManager";
            foreach (var c in conns)
            {
                if (c == null || c.signalInGameObject == null) continue;
                Add("signal " + c.signalOutName + " -> " + c.signalInName + (c.IsInputFsm ? " (FSM event)" : "") + (c.isActive ? "" : " [inactive]"), c.signalInGameObject);
            }
        }

        // engine bookkeeping fields that reference half the scene and explain nothing
        static bool IsBookkeeping(string type, string field)
        {
            // SignalConnector's own fields duplicate the [SIGNAL] edge that already describes the wiring
            return type == "SubsceneNode" || type == "SubsceneCuller" || type == "LateAwakeNode" || type == "SignalConnector" || type == "PlayMakerFixedUpdate" ||
                   field == "_cachedRenderer" || field.StartsWith("_cached");
        }

        public static List<RefEdge> Filter(List<RefEdge> l, bool includeNoise)
        {
            if (includeNoise) return l;
            var r = new List<RefEdge>(l.Count);
            foreach (var e in l) if (!e.noise) r.Add(e);
            return r;
        }

        // ---------------------------------------------------------------- queries
        static readonly List<RefEdge> none = new List<RefEdge>();
        public static List<RefEdge> References(int goId) { List<RefEdge> l; return fwd.TryGetValue(goId, out l) ? l : none; }
        public static List<RefEdge> Referencers(int goId) { List<RefEdge> l; return rev.TryGetValue(goId, out l) ? l : none; }
        public static List<RefEdge> References(GameObject g) { return g == null ? none : References(g.GetInstanceID()); }
        public static List<RefEdge> Referencers(GameObject g) { return g == null ? none : Referencers(g.GetInstanceID()); }
        public static List<RefEdge> ReferencersOfAsset(UObj asset) { return asset == null ? none : Referencers(asset.GetInstanceID()); }

        // scene-object neighbours (edges to assets are not traversed)
        static IEnumerable<KeyValuePair<int, RefEdge>> Next(int id, bool forward)
        {
            foreach (var e in forward ? References(id) : Referencers(id))
            {
                if (e.noise) continue;
                int n = forward ? e.dstGo : e.srcGo;
                if (n != 0 && n != id) yield return new KeyValuePair<int, RefEdge>(n, e);
            }
        }

        // breadth-first dependency chain (what this object leads to), levels up to maxDepth
        public static List<KeyValuePair<int, RefEdge>> Chain(int start, bool forward, int maxDepth, int maxNodes)
        {
            var res = new List<KeyValuePair<int, RefEdge>>();
            var seen = new HashSet<int> { start };
            var q = new Queue<KeyValuePair<int, int>>();
            q.Enqueue(new KeyValuePair<int, int>(start, 0));
            while (q.Count > 0 && res.Count < maxNodes)
            {
                var it = q.Dequeue();
                if (it.Value >= maxDepth) continue;
                foreach (var nb in Next(it.Key, forward))
                {
                    if (!seen.Add(nb.Key)) continue;
                    res.Add(nb);
                    q.Enqueue(new KeyValuePair<int, int>(nb.Key, it.Value + 1));
                }
            }
            return res;
        }

        // shortest path a -> b following references (optionally in either direction)
        public static List<RefEdge> ShortestPath(int a, int b, bool undirected, int maxDepth = 12)
        {
            var prev = new Dictionary<int, RefEdge>();
            var from = new Dictionary<int, int>();
            var q = new Queue<int>(); q.Enqueue(a);
            var depth = new Dictionary<int, int> { { a, 0 } };
            while (q.Count > 0)
            {
                int n = q.Dequeue();
                if (n == b) break;
                if (depth[n] >= maxDepth) continue;
                foreach (bool fw in undirected ? new[] { true, false } : new[] { true })
                    foreach (var nb in Next(n, fw))
                    {
                        if (depth.ContainsKey(nb.Key)) continue;
                        depth[nb.Key] = depth[n] + 1; prev[nb.Key] = nb.Value; from[nb.Key] = n;
                        q.Enqueue(nb.Key);
                    }
            }
            if (!prev.ContainsKey(b)) return null;
            var path = new List<RefEdge>();
            for (int n = b; n != a; n = from[n]) path.Add(prev[n]);
            path.Reverse();
            return path;
        }

        // all simple paths a -> b (forward), bounded
        public static List<List<RefEdge>> AllPaths(int a, int b, int maxDepth, int maxPaths)
        {
            var res = new List<List<RefEdge>>();
            var stack = new List<RefEdge>();
            var onPath = new HashSet<int> { a };
            Dfs(a, b, maxDepth, maxPaths, stack, onPath, res);
            return res;
        }

        static void Dfs(int n, int b, int maxDepth, int maxPaths, List<RefEdge> stack, HashSet<int> onPath, List<List<RefEdge>> res)
        {
            if (res.Count >= maxPaths || stack.Count >= maxDepth) return;
            foreach (var nb in Next(n, true))
            {
                if (onPath.Contains(nb.Key)) continue;
                stack.Add(nb.Value);
                if (nb.Key == b) res.Add(new List<RefEdge>(stack));
                else { onPath.Add(nb.Key); Dfs(nb.Key, b, maxDepth, maxPaths, stack, onPath, res); onPath.Remove(nb.Key); }
                stack.RemoveAt(stack.Count - 1);
                if (res.Count >= maxPaths) return;
            }
        }

        public static string Stats()
        {
            return "indexed " + indexedObjects + " objects, " + edges + " edges, " + rev.Count + " referenced targets, " + truncatedObjects + " truncated, pending " + queue.Count +
                   ", full runs " + fullRuns + " (last " + lastFullMs.ToString("0") + " ms), budget " + budgetMs.ToString("0.0") + " ms/frame";
        }

        public static string ProvLabel(Provenance p)
        {
            switch (p)
            {
                case Provenance.SerializedField: return "FIELD";
                case Provenance.SignalConnection: return "SIGNAL";
                case Provenance.StateMachine: return "FSM";
                case Provenance.Adapter: return "ADAPTER";
                case Provenance.Observed: return "OBSERVED";
                case Provenance.Inferred: return "INFERRED";
                case Provenance.Code: return "CODE";
                default: return "REFLECTION";
            }
        }

        public static string Describe(RefEdge e, bool outgoing)
        {
            string src = NameOf(e.srcGo);
            string dst = e.dstName + (e.dstType != "GameObject" ? " (" + e.dstType + ")" : "") + (e.dstGo == 0 ? " [asset]" : "") + (e.DstAlive ? "" : " [destroyed]");
            return "[" + ProvLabel(e.prov) + "] " + (outgoing ? e.srcType + "." + e.field + "  ->  " + dst : src + "  " + e.srcType + "." + e.field);
        }

        public static string NameOf(int goId) { var r = ObjectDatabase.Get(goId); return r != null ? r.name : "#" + goId; }
        public static string PathOf(int goId) { var r = ObjectDatabase.Get(goId); return r != null ? r.path : "#" + goId; }
    }
}
