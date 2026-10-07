using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace InsideDev
{
    // Audio DB phase M: game-side references that the SoundLibrary (top-level fields + PlayMaker actions) does not
    // see. Walks serialized data of every loaded game script, loaded ScriptableObjects and animation clip events,
    // looking for strings whose FNV hash is a Wwise event of the scanned banks (the match is by exact id, so a hit
    // means that event name is written in the game's data). Examples: the boy's footsteps / landings live in
    // UnityAnimEventDatabaseManager.clip2AnimEventsValues[clip].events[i].audioEvent.eventName.
    //   prov DATA  - nested serialized data of a scene script   (area / object path known)
    //   prov ASSET - a loaded ScriptableObject
    //   prov ANIM  - an animation clip event parameter / function name
    // Runs after streaming changes, time-sliced (1 ms per frame), read-only.
    public static class AudioRefScan
    {
        sealed class Root { public object obj; public GameObject go; public string area, path, prov; public Type type; public int iid; }
        // lazy path of a value: strings are only built for the few values that turn out to be references (GC)
        sealed class PNode
        {
            public PNode parent; public string seg; public int idx = -1; public object item;
            public override string ToString()
            {
                var sb = new System.Text.StringBuilder(); Build(this, sb); return sb.ToString();
            }
            static void Build(PNode n, System.Text.StringBuilder sb)
            {
                if (n.parent != null) Build(n.parent, sb);
                if (n.idx >= 0) { sb.Append('[').Append(n.idx); string lab = n.item != null ? ClipLabel(n.item) : null; if (lab != null) sb.Append(" '").Append(lab).Append('\''); sb.Append(']'); }
                else { if (n.parent != null) sb.Append('.'); sb.Append(n.seg); }
            }
            public string RootField() { var n = this; while (n.parent != null && n.parent.parent != null) n = n.parent; return n.seg ?? ""; }
        }
        struct Frame { public object o; public PNode path; public int depth; public IList list; public int idx, kind; }
        struct Hit { public string where, ev; public uint id; }
        sealed class Cached { public int pass; public Hit[] hits; }

        static readonly List<Root> roots = new List<Root>();
        static readonly List<KeyValuePair<GameObject, AnimationClip>> clips = new List<KeyValuePair<GameObject, AnimationClip>>();
        static readonly Stack<Frame> stack = new Stack<Frame>();
        static readonly HashSet<string> notedThisPass = new HashSet<string>();
        static readonly Dictionary<int, Cached> rootCache = new Dictionary<int, Cached>();      // component / asset instance -> hits
        static readonly Dictionary<int, Hit[]> clipCache = new Dictionary<int, Hit[]>();       // clip instance -> hits (clip data is immutable)
        static readonly Dictionary<int, AnimationClip[]> ownerClips = new Dictionary<int, AnimationClip[]>();
        static readonly List<Hit> curHits = new List<Hit>();
        static int rootIdx = -1, clipIdx, nodes, goIdx; static Root cur; static bool curWalking;
        static readonly List<GameObject> gos = new List<GameObject>();
        static bool running; static int streamSeen = -1, dbPassAt = -1; static float pendingSince = -1f;
        public static int passes, lastRefs, lastRoots, lastClips, lastWalked; public static float lastMs; static float passMs;
        public static string status = "waiting"; static string where; static int slowLogs;

        const int MaxDepth = 7, MaxNodesPerRoot = 300000, RewalkAfterPasses = 6;

        public static void Tick()
        {
            if (!AudioDb.Built || !ObjectDatabase.Ready) return;
            if (!running)
            {
                if (Levels.Changed(ref streamSeen, 3f) || passes == 0) { if (pendingSince < 0) { pendingSince = Time.realtimeSinceStartup; dbPassAt = ObjectDatabase.completedPasses; } }
                // start once the object database has caught up with the new objects (or after 20 s regardless)
                if (pendingSince >= 0 && (ObjectDatabase.completedPasses > dbPassAt || passes == 0 || Time.realtimeSinceStartup - pendingSince > 20f)) Begin();
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            where = "?";
            try { Step(sw); } catch (Exception e) { DevLog.Error("audio ref scan", e); End(); }
            float ms = (float)sw.Elapsed.TotalMilliseconds; passMs += ms;
            if (ms > 8f && slowLogs++ < 20) DevLog.Write("[audio refs] slow step " + ms.ToString("0.0") + " ms in " + where + (cur != null && cur.type != null ? " (root " + cur.type.Name + ")" : ""));
        }

        static void Begin()
        {
            pendingSince = -1f; running = true; passMs = 0; lastRefs = 0; lastWalked = 0;
            roots.Clear(); clips.Clear(); stack.Clear(); notedThisPass.Clear(); rootIdx = -1; clipIdx = 0; cur = null; curWalking = false;
            AudioDb.BeginRefScan();
            gos.Clear(); goIdx = 0;
            foreach (var rec in ObjectDatabase.all) if (rec.go != null) gos.Add(rec.go);
            stage = 0;
            status = "scanning " + gos.Count + " objects";
        }

        static int stage;
        static void CollectAssets()
        {
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(ScriptableObject)))
                {
                    var so = o as ScriptableObject; if (so == null) continue;
                    var t = so.GetType();
                    if (!IsGame(t) || Walk(t).Length == 0) continue;
                    roots.Add(new Root { obj = so, prov = "ASSET", type = t, iid = so.GetInstanceID() });
                }
            }
            catch { }
        }

        static readonly List<Component> animators = new List<Component>(); static int animIdx; static readonly HashSet<int> clipSeen = new HashSet<int>();
        static void CollectClips()
        {
            animators.Clear(); animIdx = 0; clipSeen.Clear();
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(Animation))) { var c = o as Component; if (c != null && c.gameObject.hideFlags == HideFlags.None) animators.Add(c); }
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(Animator))) { var c = o as Component; if (c != null && c.gameObject.hideFlags == HideFlags.None) animators.Add(c); }
            }
            catch { }
        }
        static void CollectClipsOf(Component c)
        {
            if (c == null) return;
            try
            {
                AnimationClip[] cl;
                int id = c.GetInstanceID();
                if (!ownerClips.TryGetValue(id, out cl))
                {
                    var l = new List<AnimationClip>();
                    var an = c as Animation;
                    if (an != null) AnimSafe.Clips(an, l);
                    var am = c as Animator;
                    if (am != null && am.runtimeAnimatorController != null) foreach (var x in am.runtimeAnimatorController.animationClips) if (x != null) l.Add(x);
                    cl = l.ToArray();
                    if (an == null || AnimSafe.CanWalkStates(an)) ownerClips[id] = cl;
                }
                var go = c.gameObject;
                int gid = go.GetInstanceID();
                foreach (var x in cl) if (x != null && clipSeen.Add(gid ^ x.GetInstanceID() * 31)) clips.Add(new KeyValuePair<GameObject, AnimationClip>(go, x));
            }
            catch { }
            lastClips = clips.Count;
        }

        static void CollectRoots(GameObject go)
        {
            if (go == null) return;
            MonoBehaviour[] mbs; try { mbs = go.GetComponents<MonoBehaviour>(); } catch { return; }
            foreach (var mb in mbs)
            {
                if (mb == null || mb is PlayMakerFSM) continue;
                var t = mb.GetType();
                if (!IsGame(t) || Walk(t).Length == 0) continue;
                roots.Add(new Root { obj = mb, go = go, prov = "DATA", type = t, iid = mb.GetInstanceID() });
            }
            if (goIdx == gos.Count) { lastRoots = roots.Count; gos.Clear(); goIdx = 0; }
        }

        static void End()
        {
            FinishRoot();
            running = false; passes++; lastMs = passMs;
            AudioDb.EndRefScan();
            status = "pass " + passes + ": " + lastRefs + " references from " + lastRoots + " scripts / assets (" + lastWalked + " walked, rest cached) and " + lastClips + " clips (" + passMs.ToString("0") + " ms total, sliced)";
            DevLog.Write("[audio refs] " + status);
        }

        static void FinishRoot()
        {
            if (cur != null && curWalking) rootCache[cur.iid] = new Cached { pass = passes, hits = curHits.ToArray() };
            curWalking = false; curHits.Clear();
        }

        static void Step(System.Diagnostics.Stopwatch sw)
        {
            if (stage < 2) { if (stage++ == 0) { where = "assets"; CollectAssets(); } else { where = "clip owners"; CollectClips(); } return; }   // one Find* per frame
            while (sw.Elapsed.TotalMilliseconds < 1.0)
            {
                if (goIdx < gos.Count) { where = "roots"; CollectRoots(gos[goIdx++]); continue; }
                if (stack.Count > 0 && nodes < MaxNodesPerRoot) { var f = stack.Pop(); nodes++; where = "walk"; if (f.list != null) VisitList(f); else Visit(f); continue; }
                if (animIdx < animators.Count) { where = "clips"; CollectClipsOf(animators[animIdx++]); continue; }
                stack.Clear();
                FinishRoot();
                if (rootIdx + 1 < roots.Count)
                {
                    cur = roots[++rootIdx]; nodes = 0;
                    var uo = cur.obj as UnityEngine.Object;
                    if (uo == null) continue;                               // destroyed since the pass started
                    Cached c;
                    if (rootCache.TryGetValue(cur.iid, out c) && passes - c.pass < RewalkAfterPasses) { foreach (var h in c.hits) Note(h, false); continue; }
                    curWalking = true; lastWalked++;
                    stack.Push(new Frame { o = cur.obj, path = new PNode { seg = cur.type.Name }, depth = 0 });
                    continue;
                }
                if (clipIdx < clips.Count) { where = "clip events"; ScanClip(clips[clipIdx++]); continue; }
                End(); return;
            }
        }

        // ------------------------------------------------------------------ walking serialized data
        static bool IsGame(Type t) { string a = t.Assembly.GetName().Name; return a == "Assembly-CSharp" || a == "Assembly-CSharp-firstpass"; }

        sealed class WF { public FieldInfo f; public int kind; }   // 1 string, 2 object, 3 list/array, 4 id list
        static readonly Dictionary<Type, WF[]> walkCache = new Dictionary<Type, WF[]>();
        static readonly Dictionary<Type, FieldInfo> clipField = new Dictionary<Type, FieldInfo>();

        static bool Nested(Type t)
        {
            if (t == null || t.IsPrimitive || t.IsEnum || t == typeof(string) || typeof(UnityEngine.Object).IsAssignableFrom(t)) return false;
            if (!IsGame(t)) return false;
            return (t.Attributes & TypeAttributes.Serializable) != 0;
        }

        static Type ElemType(Type t)
        {
            if (t.IsArray) return t.GetElementType();
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return t.GetGenericArguments()[0];
            return null;
        }

        static WF[] Walk(Type t)
        {
            WF[] res;
            if (walkCache.TryGetValue(t, out res)) return res;
            walkCache[t] = res = new WF[0];                                   // guard against recursive types
            var l = new List<WF>();
            FieldInfo[] direct = typeof(MonoBehaviour).IsAssignableFrom(t) ? SoundLibrary.AudioFields(t) : null;   // already FIELD refs
            foreach (var f in ValueDump.Fields(t, false))
            {
                if (!ValueDump.IsSerialized(f)) continue;
                if (direct != null && Array.IndexOf(direct, f) >= 0) continue;
                var ft = f.FieldType;
                if (ft == typeof(string)) { l.Add(new WF { f = f, kind = 1 }); continue; }
                if (Nested(ft)) { l.Add(new WF { f = f, kind = 2 }); continue; }
                var et = ElemType(ft);
                if (et == null) continue;
                if (et == typeof(string) || Nested(et)) l.Add(new WF { f = f, kind = 3 });
                else if ((et == typeof(int) || et == typeof(uint)) && f.Name.IndexOf("event", StringComparison.OrdinalIgnoreCase) >= 0 && f.Name.IndexOf("id", StringComparison.OrdinalIgnoreCase) >= 0) l.Add(new WF { f = f, kind = 4 });
            }
            walkCache[t] = res = l.ToArray();
            return res;
        }

        // lists are walked 64 elements per step so a huge list never runs inside one frame
        static void VisitList(Frame fr)
        {
            var list = fr.list; int end = Math.Min(list.Count, fr.idx + 64);
            if (end < list.Count) { var rest = fr; rest.idx = end; stack.Push(rest); }
            for (int i = end - 1; i >= fr.idx; i--)
            {
                object x; try { x = list[i]; } catch { break; }
                if (x == null) continue;
                if (fr.kind == 4)
                {
                    uint id = x is int ? (uint)(int)x : x is uint ? (uint)x : 0u;
                    var node = id != 0 ? WwiseBanks.Get(id) : null;
                    if (node != null && node.type == WwiseBanks.HType.Event) Found(AudioTrace.Name(id), new PNode { parent = fr.path, idx = i }, " (event id)", id);
                    continue;
                }
                var s = x as string;
                if (s != null) { Check(s, fr.path, null, i); continue; }
                if (fr.depth >= MaxDepth) break;
                stack.Push(new Frame { o = x, path = new PNode { parent = fr.path, idx = i, item = x }, depth = fr.depth + 1 });
            }
        }

        static string ClipLabel(object o)
        {
            var t = o.GetType();
            FieldInfo cf;
            if (!clipField.TryGetValue(t, out cf))
            {
                cf = null;
                foreach (var f in ValueDump.Fields(t, false)) if (f.FieldType == typeof(AnimationClip)) { cf = f; break; }
                clipField[t] = cf;
            }
            if (cf == null) return null;
            try { var c = cf.GetValue(o) as AnimationClip; return c != null ? c.name : null; } catch { return null; }
        }

        static void Visit(Frame fr)
        {
            foreach (var w in Walk(fr.o.GetType()))
            {
                object v; try { v = w.f.GetValue(fr.o); } catch { continue; }
                if (v == null) continue;
                switch (w.kind)
                {
                    case 1: Check((string)v, fr.path, w.f.Name, -1); break;
                    case 2: if (fr.depth < MaxDepth) stack.Push(new Frame { o = v, path = new PNode { parent = fr.path, seg = w.f.Name }, depth = fr.depth + 1 }); break;
                    case 3: case 4:
                        {
                            var list = v as IList; if (list == null || list.Count == 0) break;
                            stack.Push(new Frame { list = list, idx = 0, kind = w.kind, path = new PNode { parent = fr.path, seg = w.f.Name }, depth = fr.depth });
                            break;
                        }
                }
            }
        }

        static void Check(string s, PNode parent, string field, int idx)
        {
            if (s.Length < 3 || s.Length > 120 || s.IndexOf(' ') >= 0) return;
            uint h = AudioTrace.Hash(s);
            WwiseBanks.Node n;
            if (WwiseBanks.nodes.TryGetValue(h, out n))
            {
                AudioTrace.Learn(h, s, AudioTrace.IdSource.GameReference);
                if (n.type == WwiseBanks.HType.Event) Found(s, field != null ? new PNode { parent = parent, seg = field } : new PNode { parent = parent, idx = idx }, "", h);
            }
            else if (WwiseBanks.stateGroups.Contains(h) || WwiseBanks.switchGroups.Contains(h) || WwiseBanks.rtpcs.Contains(h))
                AudioTrace.Learn(h, s, AudioTrace.IdSource.GameReference);
        }

        // one reference per (script / asset, top-level field, event) and pass: the first location found is shown
        static void Found(string ev, PNode path, string suffix, uint id)
        {
            string rootPrefix = cur.type.Name + "." + path.RootField();
            foreach (var h in curHits) if (h.id == id && h.where.StartsWith(rootPrefix)) return;
            var hit = new Hit { where = path.ToString() + suffix, ev = ev, id = id };
            curHits.Add(hit);
            Note(hit, true);
        }

        static void Note(Hit h, bool fresh)
        {
            if (cur.area == null)
            {
                if (cur.go != null) { cur.area = AudioCatalog.AreaOf(cur.go); cur.path = Inspector.PathOf(cur.go.transform); }
                else { var uo = cur.obj as UnityEngine.Object; cur.area = "(asset)"; cur.path = (uo != null ? uo.name : "?") + " [" + cur.type.Name + "]"; }
            }
            if (!notedThisPass.Add(cur.path + "|" + h.where + "|" + h.id)) return;
            lastRefs++;
            AudioDb.Note(cur.area, cur.path, (cur.prov == "ASSET" ? "asset data " : "data ") + h.where, cur.prov, h.ev);
        }

        // ------------------------------------------------------------------ animation clip events
        static readonly List<Hit> clipHits = new List<Hit>();
        static void ScanClip(KeyValuePair<GameObject, AnimationClip> kv)
        {
            var go = kv.Key; var clip = kv.Value;
            if (go == null || clip == null) return;
            Hit[] hits;
            int cid = clip.GetInstanceID();
            if (!clipCache.TryGetValue(cid, out hits))
            {
                clipHits.Clear();
                AnimationEvent[] evs = null; try { evs = clip.events; } catch { }
                if (evs != null)
                    foreach (var e in evs)
                    {
                        string[] cands = { e.stringParameter, e.functionName, e.objectReferenceParameter != null ? e.objectReferenceParameter.name : null };
                        foreach (var s in cands)
                        {
                            if (string.IsNullOrEmpty(s) || s.IndexOf(' ') >= 0) continue;
                            uint h = AudioTrace.Hash(s);
                            var n = WwiseBanks.Get(h);
                            if (n == null || n.type != WwiseBanks.HType.Event) continue;
                            AudioTrace.Learn(h, s, AudioTrace.IdSource.GameReference);
                            clipHits.Add(new Hit { id = h, ev = s, where = "animation event: clip '" + clip.name + "' @" + e.time.ToString("0.00") + "s " + e.functionName + "(" + (s == e.functionName ? "" : s) + ")" });
                        }
                    }
                clipCache[cid] = hits = clipHits.ToArray();
            }
            if (hits.Length == 0) return;
            string area = AudioCatalog.AreaOf(go), path = Inspector.PathOf(go.transform);
            foreach (var h in hits)
            {
                if (!notedThisPass.Add(path + "|clip|" + h.id)) continue;
                lastRefs++;
                AudioDb.Note(area, path, h.where, "ANIM", h.ev);
            }
        }
    }
}
