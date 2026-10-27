using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // StateDiff.cs (owner WORLD_B, Snapshot / Diff and the state_diff schema frozen): snapshot the serialized state of
    // the components around a point (or under a root) and diff two snapshots (report 05 item 15), for puzzles and the
    // ARG. Field values are flattened to "object path|component|field" -> short value text (Vector3 to 0.01, floats to
    // 4 digits, refs as path). Snapshots are taken in main-thread chunks of at most 3 ms and kept in memory (at most 8).
    // Snapshot.Diff is pure (offline-testable).
    // What is captured per object: GameObject active, Transform position / euler / scale, Behaviour / Collider / Renderer
    // enabled, Rigidbody velocity / kinematic / sleeping, Animation playing clips, Light intensity / colour, PlayMakerFSM
    // state, active flag and bool / int / float / string variables, and every instance field (public and private) of
    // game MonoBehaviours: leaves as text, collections as Type[count], game classes / structs one level deep ("a.b").
    // Radius mode walks the active objects within the radius plus the inactive subtrees under them and skips the
    // controlled characters and the camera (they change every frame); root mode walks everything under the root,
    // inactive included. Paths carry "[n]" for same-named siblings so keys stay stable between snapshots.
    public sealed class Snapshot
    {
        public string name;
        public long frame, step;
        public int objects, components;
        public readonly Dictionary<string, string> fields = new Dictionary<string, string>();

        internal bool hasRoot, truncated;
        internal string root;
        internal Vector3 center;
        internal float radius;
        internal List<string> comps = new List<string>();
        internal int maxFields;

        // keys changed, added (before null) or removed (after null), sorted by key, at most cap; total = all differences
        public static List<DiffRow> Diff(Snapshot a, Snapshot b, int cap, out int total)
        {
            var keys = new List<string>();
            if (a != null) keys.AddRange(a.fields.Keys);
            if (b != null) foreach (var k in b.fields.Keys) if (a == null || !a.fields.ContainsKey(k)) keys.Add(k);
            keys.Sort(StringComparer.Ordinal);
            var rows = new List<DiffRow>();
            total = 0;
            foreach (var k in keys)
            {
                string x = null, y = null;
                if (a != null) a.fields.TryGetValue(k, out x);
                if (b != null) b.fields.TryGetValue(k, out y);
                if (x != null && y != null && string.Equals(x, y, StringComparison.Ordinal)) continue;
                total++;
                if (rows.Count < cap) rows.Add(new DiffRow { key = k, before = x, after = y });
            }
            return rows;
        }
    }

    public sealed class DiffRow
    {
        public string key, before, after;
    }

    public static class StateDiff
    {
        public const string Owner = "WORLD_B";
        public const int MaxSnapshots = 8;
        public const double ChunkMs = 3.0;

        static readonly object gate = new object();
        static readonly List<Snapshot> store = new List<Snapshot>();

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("state_diff", "State diff", PlayShared.Dev, Owner,
                "Snapshot every component field around the body (or a point / root) and diff later: snapshot name, act or wait, then diff name (against a new snapshot or another named one). Shows exactly which fields a lever, trigger or action changed. Covers active flags, transforms, enabled flags, rigidbodies, FSM states and variables, and the public and private fields of game scripts.",
                Schema.Obj().Enum("op", "snapshot, diff, list or drop.", "snapshot", "snapshot", "diff", "list", "drop")
                    .Str("name", "Snapshot name.", "a").Str("against", "op diff: compare with this snapshot instead of a new one.")
                    .Str("center", "Object id or path (default the body).").Num("radius", "Metres.", 8, 1, 50).Str("root", "Hierarchy path: everything under it instead of a radius.")
                    .Arr("components", "Only these component types.", Schema.Str(), 0, 20).Int("max_fields", "Field cap per snapshot.", 5000, 100, 50000)
                    .Int("limit", "op diff: rows.", 100, 1, 500).Build(),
                Schema.Obj().OutObj("snapshot", "", Schema.Obj().Str("name", "").Int("objects", "", 0, 1000000).Int("components", "", 0, 1000000).Int("fields", "", 0, 10000000)
                        .Int("frame", "", 0, long.MaxValue).Int("step", "", 0, long.MaxValue))
                    .Arr("snapshots", "op list.", Schema.OutOf(Schema.Obj().Str("name", "").Int("fields", "", 0, 10000000).Int("frame", "", 0, long.MaxValue)), 0, 8)
                    .Arr("diff", "op diff.", Schema.OutOf(Schema.Obj().Str("object", "").Str("component", "").Str("field", "").Str("before", "").Str("after", "")
                        .Enum("change", "changed, added (no before) or removed (no after).", null, "changed", "added", "removed")), 0, 500)
                    .Int("total", "Differences before the limit.", 0, 10000000).Bool("truncated", "max_fields was hit.")
                    .Arr("warnings", "", Schema.Str(), 0, 20).BuildOutput()).Idempotent(), Handle);
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "snapshot");
            string name = PlayShared.Str(a, "name", "a");
            if (string.IsNullOrEmpty(name)) name = "a";
            var r = PlayShared.Env();
            if (op == "list") { r["snapshots"] = List(); return ToolResult.Ok(r); }
            if (op == "drop")
            {
                lock (gate)
                {
                    if (name == "*") store.Clear();
                    else if (store.RemoveAll(s => s.name == name) == 0) return ToolResult.Fail(McpErr.NotFound, "no snapshot '" + name + "'", "list shows the snapshots");
                }
                r["snapshots"] = List();
                return ToolResult.Ok(r);
            }
            if (op == "snapshot")
            {
                var p = new Snapshot { name = name, root = PlayShared.Str(a, "root", null), radius = (float)PlayShared.Num(a, "radius", 8), maxFields = PlayShared.Int(a, "max_fields", 5000) };
                p.hasRoot = !string.IsNullOrEmpty(p.root);
                p.comps = PlayShared.Strings(a, "components");
                var s = Take(ctx, p, PlayShared.Str(a, "center", null), true);
                Keep(s);
                r["snapshot"] = Summary(s);
                r["truncated"] = s.truncated;
                return ToolResult.Ok(r);
            }
            if (op == "diff")
            {
                var before = Find(name);
                if (before == null) return ToolResult.Fail(McpErr.NotFound, "no snapshot '" + name + "'", "state_diff {op:\"snapshot\", name:\"" + name + "\"} first");
                string against = PlayShared.Str(a, "against", null);
                Snapshot after;
                if (!string.IsNullOrEmpty(against))
                {
                    after = Find(against);
                    if (after == null) return ToolResult.Fail(McpErr.NotFound, "no snapshot '" + against + "'", "list shows the snapshots");
                }
                else
                {
                    var p = new Snapshot { name = name + "_now", root = before.root, hasRoot = before.hasRoot, radius = before.radius, center = before.center, maxFields = before.maxFields };
                    p.comps = new List<string>(before.comps);
                    after = Take(ctx, p, null, false);
                }
                int total;
                var rows = Snapshot.Diff(before, after, PlayShared.Int(a, "limit", 100), out total);
                var l = new List<object>();
                foreach (var d in rows)
                {
                    string path, comp, field;
                    StateDiffText.Split(d.key, out path, out comp, out field);
                    var o = new Json.Obj();
                    o["object"] = path;
                    o["component"] = comp;
                    o["field"] = field;
                    if (d.before != null) o["before"] = d.before;
                    if (d.after != null) o["after"] = d.after;
                    o["change"] = d.before == null ? "added" : d.after == null ? "removed" : "changed";
                    l.Add(o);
                }
                r["snapshot"] = Summary(after);
                r["diff"] = l;
                r["total"] = (double)total;
                r["truncated"] = before.truncated || after.truncated;
                var w = new List<object>();
                if (before.truncated || after.truncated) w.Add("max_fields was hit: raise it or narrow with components / radius, or the diff may miss fields");
                if (total > l.Count) w.Add("showing " + l.Count + " of " + total + " differences (limit)");
                if (w.Count > 0) r["warnings"] = w;
                return ToolResult.Ok(r);
            }
            return ToolResult.Fail(McpErr.BadArgs, "unknown op " + op);
        }

        static Snapshot Find(string name)
        {
            lock (gate) { foreach (var s in store) if (s.name == name) return s; }
            return null;
        }

        static void Keep(Snapshot s)
        {
            lock (gate)
            {
                store.RemoveAll(x => x.name == s.name);
                store.Add(s);
                while (store.Count > MaxSnapshots) store.RemoveAt(0);
            }
        }

        static List<object> List()
        {
            var l = new List<object>();
            lock (gate)
                foreach (var s in store)
                {
                    var o = new Json.Obj();
                    o["name"] = s.name;
                    o["fields"] = (double)s.fields.Count;
                    o["frame"] = (double)s.frame;
                    l.Add(o);
                }
            return l;
        }

        static Json.Obj Summary(Snapshot s)
        {
            var o = new Json.Obj();
            o["name"] = s.name;
            o["objects"] = (double)s.objects;
            o["components"] = (double)s.components;
            o["fields"] = (double)s.fields.Count;
            o["frame"] = (double)s.frame;
            o["step"] = (double)Math.Max(0, s.step);
            return o;
        }

        // worker thread: the capture in main-thread chunks
        static Snapshot Take(CallContext ctx, Snapshot s, string centerSpec, bool resolveCenter)
        {
            var cap = new Capture(s);
            ctx.MainDo(() => cap.Begin(centerSpec, resolveCenter));
            while (!cap.done)
            {
                ctx.ThrowIfCancelled();
                if (ctx.RemainingMs < 2500) { s.truncated = true; break; }
                ctx.MainDo(() => cap.Step(ChunkMs));
            }
            return s;
        }

        // ---------------------------------------------------------------- capture (main thread)
        sealed class FieldPlan
        {
            public FieldInfo field;
            public string name;
            public List<FieldInfo> sub;
        }

        static readonly Dictionary<Type, List<FieldPlan>> plans = new Dictionary<Type, List<FieldPlan>>();

        static List<FieldPlan> PlanFor(Type t)
        {
            lock (plans)
            {
                List<FieldPlan> l;
                if (plans.TryGetValue(t, out l)) return l;
                l = new List<FieldPlan>();
                foreach (var f in ReflectValues.InstanceFields(t))
                {
                    var ft = f.FieldType;
                    if (Skip(ft)) continue;
                    var p = new FieldPlan { field = f, name = StateDiffText.FieldName(f.Name) };
                    if (!StateDiffText.IsLeaf(ft) && !typeof(System.Collections.ICollection).IsAssignableFrom(ft))
                    {
                        if (!GameType(ft)) continue;
                        p.sub = new List<FieldInfo>();
                        foreach (var sf in ReflectValues.InstanceFields(ft))
                            if (!Skip(sf.FieldType) && StateDiffText.IsLeaf(sf.FieldType) && p.sub.Count < 12) p.sub.Add(sf);
                        if (p.sub.Count == 0) continue;
                    }
                    l.Add(p);
                }
                plans[t] = l;
                return l;
            }
        }

        static bool Skip(Type t)
        {
            return typeof(Delegate).IsAssignableFrom(t) || t == typeof(SignalIn) || t == typeof(SignalOut) || t.IsPointer || t == typeof(IntPtr)
                || (t.IsGenericType && t.Name.StartsWith("SimpleEvent", StringComparison.Ordinal)) || t.Name.StartsWith("FastEvent", StringComparison.Ordinal);
        }

        static bool GameType(Type t)
        {
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return false;
            var an = t.Assembly.GetName().Name;
            return an == "Assembly-CSharp" || an == "Assembly-CSharp-firstpass";
        }

        static string RefPath(UnityEngine.Object o)
        {
            if (o == null) return "null";
            var c = o as Component;
            if (c != null) return PlayShared.PathOf(c.transform) + ":" + c.GetType().Name;
            var g = o as GameObject;
            if (g != null) return PlayShared.PathOf(g.transform);
            return o.name + ":" + o.GetType().Name;
        }

        sealed class Capture
        {
            readonly Snapshot s;
            readonly Stack<Transform> stack = new Stack<Transform>();
            readonly Dictionary<int, string> pathCache = new Dictionary<int, string>();
            readonly Dictionary<string, int> pathOwner = new Dictionary<string, int>();
            readonly List<Transform> skip = new List<Transform>();
            readonly HashSet<string> filter;
            Transform[] all;
            int cursor;
            float r2;
            public bool done;

            public Capture(Snapshot snap)
            {
                s = snap;
                filter = snap.comps != null && snap.comps.Count > 0 ? new HashSet<string>(snap.comps) : null;
            }

            public void Begin(string centerSpec, bool resolveCenter)
            {
                s.frame = Time.frameCount;
                s.step = World.Step();
                if (s.hasRoot)
                {
                    var go = World.Target(s.root, "root");
                    stack.Push(go.transform);
                    return;
                }
                if (resolveCenter) s.center = World.Center(centerSpec);
                r2 = s.radius * s.radius;
                all = UnityEngine.Object.FindObjectsOfType(typeof(Transform)) as Transform[];
                if (all == null) all = new Transform[0];
                var boy = ScriptGlobals.boy;
                if (boy != null)
                {
                    skip.Add(boy.transform);
                    if (boy.visualBoy != null) skip.Add(boy.visualBoy.transform);
                }
                var hud = ScriptGlobals.huddle;
                if (hud != null) skip.Add(hud.transform);
                var cam = CameraScript.main;
                if (cam != null) skip.Add(cam.transform);
            }

            bool Skipped(Transform t)
            {
                foreach (var x in skip) if (x != null && (t == x || t.IsChildOf(x))) return true;
                return false;
            }

            public void Step(double budgetMs)
            {
                var sw = Stopwatch.StartNew();
                while (!done)
                {
                    if (sw.Elapsed.TotalMilliseconds > budgetMs) return;
                    if (s.fields.Count >= s.maxFields) { s.truncated = true; done = true; return; }
                    if (stack.Count > 0)
                    {
                        var t = stack.Pop();
                        if (t == null) continue;
                        Take(t);
                        for (int i = t.childCount - 1; i >= 0; i--) stack.Push(t.GetChild(i));
                        continue;
                    }
                    if (s.hasRoot || all == null || cursor >= all.Length) { done = true; return; }
                    var x = all[cursor++];
                    if (x == null || (x.position - s.center).sqrMagnitude > r2 || Skipped(x)) continue;
                    Take(x);
                    for (int i = x.childCount - 1; i >= 0; i--)
                    {
                        var c = x.GetChild(i);
                        if (!c.gameObject.activeSelf) stack.Push(c);
                    }
                }
            }

            string PathKey(Transform t)
            {
                string p;
                int id = t.GetInstanceID();
                if (pathCache.TryGetValue(id, out p)) return p;
                var parent = t.parent;
                int before = 0;
                if (parent != null)
                {
                    int idx = t.GetSiblingIndex();
                    for (int i = 0; i < idx && i < parent.childCount; i++) if (parent.GetChild(i).name == t.name) before++;
                }
                var seg = StateDiffText.Segment(t.name, before);
                p = parent == null ? seg : PathKey(parent) + "/" + seg;
                pathCache[id] = p;
                return p;
            }

            void Put(string path, string comp, string field, string value)
            {
                if (s.fields.Count >= s.maxFields) { s.truncated = true; return; }
                s.fields[StateDiffText.Key(path, comp, field)] = value;
            }

            bool Want(string typeName, Type t)
            {
                return filter == null || filter.Contains(typeName) || (t != null && filter.Contains(t.FullName));
            }

            void Take(Transform t)
            {
                var go = t.gameObject;
                if ((go.hideFlags & HideFlags.HideInHierarchy) != 0) return;
                string path = PathKey(t);
                int owner;
                int iid = go.GetInstanceID();
                if (pathOwner.TryGetValue(path, out owner) && owner != iid) path = path + "#" + iid;
                else pathOwner[path] = iid;
                s.objects++;
                if (Want("GameObject", null)) Put(path, "GameObject", "active", go.activeSelf ? "true" : "false");
                var seen = new Dictionary<Type, int>();
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var ct = c.GetType();
                    if (ct.Namespace != null && ct.Namespace.StartsWith("InsideDev", StringComparison.Ordinal)) continue;
                    if (!Want(ct.Name, ct)) continue;
                    int n;
                    seen.TryGetValue(ct, out n);
                    seen[ct] = n + 1;
                    s.components++;
                    try { Component(path, StateDiffText.ComponentLabel(ct.Name, n), c); }
                    catch (Exception e) { Put(path, StateDiffText.ComponentLabel(ct.Name, n), "error", e.GetType().Name); }
                }
            }

            void Component(string path, string label, Component c)
            {
                var tr = c as Transform;
                if (tr != null)
                {
                    Put(path, label, "position", StateDiffText.Value(tr.position, null));
                    Put(path, label, "euler", StateDiffText.Value(tr.localEulerAngles, null));
                    Put(path, label, "scale", StateDiffText.Value(tr.localScale, null));
                    return;
                }
                var rb = c as Rigidbody;
                if (rb != null)
                {
                    Put(path, label, "velocity", StateDiffText.Value(rb.velocity, null));
                    Put(path, label, "angular", StateDiffText.Value(rb.angularVelocity, null));
                    Put(path, label, "kinematic", rb.isKinematic ? "true" : "false");
                    Put(path, label, "sleeping", rb.IsSleeping() ? "true" : "false");
                    return;
                }
                var col = c as Collider;
                if (col != null)
                {
                    Put(path, label, "enabled", col.enabled ? "true" : "false");
                    Put(path, label, "trigger", col.isTrigger ? "true" : "false");
                    return;
                }
                var ren = c as Renderer;
                if (ren != null) { Put(path, label, "enabled", ren.enabled ? "true" : "false"); return; }
                var anim = c as Animation;
                if (anim != null)
                {
                    Put(path, label, "enabled", anim.enabled ? "true" : "false");
                    var playing = new List<string>();
                    foreach (AnimationState st in anim)
                        if (st != null && anim.IsPlaying(st.name))
                        {
                            playing.Add(st.name);
                            Put(path, label, "time." + st.name, StateDiffText.Num(st.time));
                        }
                    Put(path, label, "playing", playing.Count == 0 ? "none" : string.Join(",", playing.ToArray()));
                    return;
                }
                var light = c as Light;
                if (light != null)
                {
                    Put(path, label, "enabled", light.enabled ? "true" : "false");
                    Put(path, label, "intensity", StateDiffText.Num(light.intensity));
                    Put(path, label, "color", StateDiffText.Value(light.color, null));
                    return;
                }
                var fsm = c as PlayMakerFSM;
                if (fsm != null) { Fsm(path, label, fsm); return; }
                var b = c as Behaviour;
                if (b != null) Put(path, label, "enabled", b.enabled ? "true" : "false");
                var mb = c as MonoBehaviour;
                if (mb == null) return;
                foreach (var p in PlanFor(c.GetType()))
                {
                    object v;
                    try { v = p.field.GetValue(c); } catch { continue; }
                    if (p.sub == null) { Put(path, label, p.name, StateDiffText.Value(v, RefPath)); continue; }
                    if (v == null) { Put(path, label, p.name, "null"); continue; }
                    foreach (var sf in p.sub)
                    {
                        object sv;
                        try { sv = sf.GetValue(v); } catch { continue; }
                        Put(path, label, p.name + "." + StateDiffText.FieldName(sf.Name), StateDiffText.Value(sv, RefPath));
                    }
                }
            }

            void Fsm(string path, string label, PlayMakerFSM f)
            {
                Put(path, label, "enabled", f.enabled ? "true" : "false");
                var fsm = f.Fsm;
                if (fsm == null) return;
                Put(path, label, "fsm", StateDiffText.Cut(f.FsmName));
                Put(path, label, "state", StateDiffText.Cut(fsm.ActiveStateName ?? ""));
                Put(path, label, "active", fsm.Active ? "true" : "false");
                var vars = fsm.Variables;
                if (vars == null) return;
                int n = 0;
                if (vars.BoolVariables != null) foreach (var v in vars.BoolVariables) if (v != null && n++ < 30) Put(path, label, "var." + v.Name, v.Value ? "true" : "false");
                if (vars.IntVariables != null) foreach (var v in vars.IntVariables) if (v != null && n++ < 30) Put(path, label, "var." + v.Name, StateDiffText.Num(v.Value));
                if (vars.FloatVariables != null) foreach (var v in vars.FloatVariables) if (v != null && n++ < 30) Put(path, label, "var." + v.Name, StateDiffText.Num(v.Value));
                if (vars.StringVariables != null) foreach (var v in vars.StringVariables) if (v != null && n++ < 30) Put(path, label, "var." + v.Name, StateDiffText.Cut(v.Value));
            }
        }
    }
}
