using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // ReflectWorld.cs (owner WORLD_B): game-side helpers shared by the WORLD_B tools (camera, logic, body, hazard,
    // audio, render, reflect, state_diff). Cross-module calls go only through the frozen public APIs and degrade when a
    // module is not built yet (ids fall back to #instanceID, the concise observation and changes are left out). Signal
    // ports come from SignalManager's public pools (FP/SignalManager.cs:26-32). Private game fields are read through a
    // cached FieldInfo / MethodInfo table; a missing member is reported, never a NullReferenceException.
    internal static class World
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly object gate = new object();
        static readonly Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();
        static readonly Dictionary<string, MethodInfo> methods = new Dictionary<string, MethodInfo>();

        // ---------------------------------------------------------------- ids, targets, body (main thread)
        public static string Id(UnityEngine.Object o, string prefix)
        {
            if (o == null) return "";
            try { return Observer.IdFor(o, prefix); }
            catch (NotImplementedException) { return "#" + o.GetInstanceID().ToString(IC); }
        }

        public static GameObject Target(string spec, string what)
        {
            if (string.IsNullOrEmpty(spec)) throw new McpException(McpErr.BadArgs, what + " is required", "pass an id from observe (o12) or a hierarchy path");
            return Observer.ResolveTarget(spec);
        }

        public static Character Active()
        {
            try { return Perception.ActiveCharacter(); }
            catch (NotImplementedException) { return G.MainCharacter; }
        }

        public static bool BodyPos(out Vector3 p)
        {
            try { return Perception.BodyPos(out p); }
            catch (NotImplementedException)
            {
                var c = G.MainCharacter;
                p = c != null ? c.pos3 : Vector3.zero;
                return c != null;
            }
        }

        public static Vector3 Center(string spec)
        {
            if (!string.IsNullOrEmpty(spec)) return Target(spec, "object").transform.position;
            Vector3 p;
            if (!BodyPos(out p)) throw new McpException(McpErr.NotReady, "no controlled body", "wait until the game is ready, or pass object");
            return p;
        }

        // Unity 5.0 has no GetComponentInChildren(includeInactive)
        public static T Child<T>(Component c) where T : Component
        {
            if (c == null) return null;
            var a = c.GetComponentsInChildren<T>(true);
            return a != null && a.Length > 0 ? a[0] : null;
        }

        public static T Child<T>(GameObject go) where T : Component { return go != null ? Child<T>(go.transform) : null; }

        public static string Path(GameObject go) { return go != null ? PlayShared.PathOf(go.transform) : ""; }
        public static string Path(Component c) { return c != null ? PlayShared.PathOf(c.transform) : ""; }

        public static object ObjRender(UnityEngine.Object o)
        {
            if (o == null) return "null (destroyed)";
            var r = new Json.Obj();
            var c = o as Component;
            var g = o as GameObject;
            r["ref"] = c != null ? Path(c) : g != null ? Path(g) : o.name;
            r["type"] = o.GetType().Name;
            r["id"] = "#" + o.GetInstanceID().ToString(IC);
            return r;
        }

        // reflect / logic: a string spec as a UnityEngine.Object of type t
        public static object RefFor(string spec, Type t)
        {
            var go = Target(spec, "object reference");
            if (t == typeof(GameObject) || t == typeof(UnityEngine.Object)) return go;
            if (t == typeof(Transform)) return go.transform;
            if (typeof(Component).IsAssignableFrom(t))
            {
                var c = go.GetComponent(t);
                if (c == null) c = go.GetComponentInChildren(t);
                if (c == null) throw new McpException(McpErr.NotFound, Path(go) + " has no " + t.Name, "pass an object that carries the component");
                return c;
            }
            throw new McpException(McpErr.BadArgs, "cannot pass a " + t.Name + " by path");
        }

        // ---------------------------------------------------------------- other modules, guarded
        public static bool Frozen()
        {
            try { return Stepper.Frozen; }
            catch (NotImplementedException) { return Time.timeScale == 0f; }
        }

        public static long Step()
        {
            try { return Stepper.Step; }
            catch (NotImplementedException) { return LogicManager.stepCount; }
        }

        public static long Mark()
        {
            try { return ChangeLog.Mark(); }
            catch (NotImplementedException) { return -1; }
        }

        public static object Since(long mark)
        {
            if (mark < 0) return null;
            try { return ChangeLog.SinceFull(mark, 20); }
            catch (NotImplementedException) { return null; }
        }

        // main thread: a savepoint fired or entered under the save lock (Saves.MarkCheat: quit keeps the lock)
        public static void SaveCheat(string what)
        {
            try { Saves.MarkCheat(what); }
            catch (NotImplementedException) { }
        }

        public static object Concise(string tool)
        {
            try { return Observer.Concise(tool); }
            catch (Exception) { return null; }
        }

        public static bool SaveLocked()
        {
            try { return Saves.Locked; }
            catch (NotImplementedException) { return false; }
        }

        public static void NotCapturing(string what)
        {
            bool cap = false;
            try { cap = MultiCam.Capturing; } catch { }
            if (cap) throw new McpException(McpErr.Refused, what + " is blocked while a MultiCam capture runs", "wait for the capture to end (jobs)");
        }

        public static void Restore(string key, string what, Func<string> undo)
        {
            try { Restorer.Register(key, what, undo); }
            catch (NotImplementedException) { }
        }

        public static void Forget(string key)
        {
            try { Restorer.Forget(key); }
            catch (NotImplementedException) { }
        }

        public static void Note(string type, string detail)
        {
            try { ChangeLog.Note(type, detail); }
            catch (Exception) { }
        }

        // the verify wait: frames rendered (the game's FixedUpdate logic does not run while frozen)
        public static bool WaitVerify(CallContext ctx, int frames, List<string> warnings)
        {
            if (frames <= 0) return false;
            bool frozen = false;
            try { frozen = ctx.Main(() => Frozen(), 3000); } catch (McpException) { }
            if (frozen) warnings.Add("frozen: re-read after " + frames + " rendered frames; the game's logic steps did not run (time step or act to see the game react)");
            if (!ctx.WaitFrames(frames, 3000 + frames * 200))
            {
                warnings.Add("verify_wait: the frames did not come in time (cancelled or the main thread is stalled)");
                return false;
            }
            return true;
        }

        public static List<object> Strs(IEnumerable<string> l) { return PlayShared.L(l); }

        // ---------------------------------------------------------------- reflection on game members
        public static FieldInfo Field(Type t, string name)
        {
            string key = t.FullName + ":" + name;
            lock (gate)
            {
                FieldInfo f;
                if (fields.TryGetValue(key, out f)) return f;
                for (var x = t; x != null && f == null; x = x.BaseType) f = x.GetField(name, Inst | BindingFlags.DeclaredOnly);
                fields[key] = f;
                return f;
            }
        }

        public static MethodInfo Method(Type t, string name, Type[] args)
        {
            string key = t.FullName + ":" + name + ":" + (args == null ? "*" : args.Length.ToString(IC));
            lock (gate)
            {
                MethodInfo m;
                if (methods.TryGetValue(key, out m)) return m;
                for (var x = t; x != null && m == null; x = x.BaseType)
                {
                    try { m = args == null ? x.GetMethod(name, Inst | BindingFlags.DeclaredOnly) : x.GetMethod(name, Inst | BindingFlags.DeclaredOnly, null, args, null); }
                    catch (AmbiguousMatchException) { m = null; }
                }
                methods[key] = m;
                return m;
            }
        }

        public static object Get(object o, string field)
        {
            var f = Field(o.GetType(), field);
            if (f == null) throw new McpException(McpErr.GameError, o.GetType().Name + "." + field + " not found (game version differs?)");
            return f.GetValue(o);
        }

        public static bool TryGet(object o, string field, out object v)
        {
            v = null;
            if (o == null) return false;
            var f = Field(o.GetType(), field);
            if (f == null) return false;
            try { v = f.GetValue(o); return true; } catch { return false; }
        }

        public static void Set(object o, string field, object v)
        {
            var f = Field(o.GetType(), field);
            if (f == null) throw new McpException(McpErr.GameError, o.GetType().Name + "." + field + " not found (game version differs?)");
            f.SetValue(o, v);
        }

        public static object Call(object o, string method, params object[] args)
        {
            var m = Method(o.GetType(), method, null);
            if (m == null) throw new McpException(McpErr.GameError, o.GetType().Name + "." + method + "() not found (game version differs?)");
            try { return m.Invoke(o, args); }
            catch (TargetInvocationException e) { throw new McpException(McpErr.GameError, o.GetType().Name + "." + method + ": " + ReflectValues.Inner(e).Message); }
        }

        static readonly string[] StateFields = { "state", "mState", "currentState", "open", "isOpen", "locked", "isActive", "isMoving", "isDown", "isAwake", "activated" };

        // "state=Idle open=false" from the usual private fields of a logic component
        public static string ShortState(Component c)
        {
            if (c == null) return "";
            var parts = new List<string>();
            foreach (var n in StateFields)
            {
                object v;
                if (TryGet(c, n, out v) && v != null && (v is bool || v is Enum || v is int || v is string)) parts.Add(n + "=" + Convert.ToString(v, IC).ToLowerInvariant());
                if (parts.Count >= 3) break;
            }
            var b = c as Behaviour;
            if (b != null && !b.enabled) parts.Add("disabled");
            return string.Join(" ", parts.ToArray());
        }

        // ---------------------------------------------------------------- signal ports
        public sealed class Ports
        {
            public readonly Dictionary<int, List<SignalIn>> ins = new Dictionary<int, List<SignalIn>>();
            public readonly Dictionary<int, List<SignalOut>> outs = new Dictionary<int, List<SignalOut>>();
            public readonly List<SignalConnection> conns = new List<SignalConnection>();

            public List<SignalIn> In(GameObject go) { List<SignalIn> l; return go != null && ins.TryGetValue(go.GetInstanceID(), out l) ? l : new List<SignalIn>(); }
            public List<SignalOut> Out(GameObject go) { List<SignalOut> l; return go != null && outs.TryGetValue(go.GetInstanceID(), out l) ? l : new List<SignalOut>(); }

            public List<SignalConnection> From(GameObject go)
            {
                var r = new List<SignalConnection>();
                foreach (var c in conns) if (c.signalOutGameObject == go) r.Add(c);
                return r;
            }

            public List<SignalConnection> To(GameObject go)
            {
                var r = new List<SignalConnection>();
                foreach (var c in conns) if (c.signalInGameObject == go) r.Add(c);
                return r;
            }
        }

        public static SignalManager Signals()
        {
            var sm = PersistentBehaviour<SignalManager>.instance;
            if (sm == null) throw new McpException(McpErr.NotReady, "no SignalManager (the master scene is not loaded)", "wait until the game is ready");
            return sm;
        }

        public static Ports ScanPorts()
        {
            var sm = Signals();
            var p = new Ports();
            if (sm.signalIns != null)
                foreach (var s in sm.signalIns)
                {
                    if (s == null || !s.isActive || s.gameObject == null) continue;
                    List<SignalIn> l;
                    if (!p.ins.TryGetValue(s.gameObjectID, out l)) p.ins[s.gameObjectID] = l = new List<SignalIn>();
                    l.Add(s);
                }
            if (sm.signalOuts != null)
                foreach (var s in sm.signalOuts)
                {
                    if (s == null || !s.isActive || s.gameObject == null) continue;
                    List<SignalOut> l;
                    if (!p.outs.TryGetValue(s.gameObjectID, out l)) p.outs[s.gameObjectID] = l = new List<SignalOut>();
                    l.Add(s);
                }
            if (sm.connections != null)
                foreach (var c in sm.connections)
                    if (c != null && c.isActive && !c.IsDead) p.conns.Add(c);
            return p;
        }

        public static SignalIn FindIn(GameObject go, string name)
        {
            var sm = Signals();
            if (sm.signalIns == null) return null;
            int id = go.GetInstanceID();
            foreach (var s in sm.signalIns)
                if (s != null && s.isActive && s.gameObjectID == id && s.debugName == name && s.gameObject == go) return s;
            return null;
        }

        public static SignalOut FindOut(GameObject go, string name)
        {
            var sm = Signals();
            if (sm.signalOuts == null) return null;
            int id = go.GetInstanceID();
            foreach (var s in sm.signalOuts)
                if (s != null && s.isActive && s.gameObjectID == id && s.debugName == name && s.gameObject == go) return s;
            return null;
        }

        public static List<string> FsmEvents(GameObject go)
        {
            var r = new List<string>();
            foreach (var f in go.GetComponents<PlayMakerFSM>())
            {
                FsmEventsOf(f, r);
            }
            return r;
        }

        static void FsmEventsOf(PlayMakerFSM f, List<string> r)
        {
            HutongGames.PlayMaker.FsmEvent[] evs;
            try { evs = f.FsmEvents; } catch { return; }
            if (evs == null) return;
            foreach (var e in evs) if (e != null && !string.IsNullOrEmpty(e.Name) && !r.Contains(e.Name) && !e.Name.StartsWith("FINISHED", StringComparison.Ordinal)) r.Add(e.Name);
        }

        public static List<object> PortNames(IEnumerable<SignalIn> l)
        {
            var r = new List<object>();
            var seen = new HashSet<string>();
            foreach (var s in l) if (s.debugName != null && seen.Add(s.debugName)) r.Add(s.debugName);
            return r;
        }

        public static List<object> PortNames(IEnumerable<SignalOut> l)
        {
            var r = new List<object>();
            var seen = new HashSet<string>();
            foreach (var s in l) if (s.debugName != null && seen.Add(s.debugName)) r.Add(s.debugName);
            return r;
        }

        // ---------------------------------------------------------------- results
        public static Json.Obj Row() { return new Json.Obj(); }

        public static void Dist(Json.Obj row, Vector3 at, Vector3 from)
        {
            var d = at - from;
            row["dx"] = PlayShared.R(d.x);
            row["dy"] = PlayShared.R(d.y);
            if (Mathf.Abs(d.z) > 0.005f) row["dz"] = PlayShared.R(d.z);
            row["dist"] = PlayShared.R(d.magnitude);
        }

        public static void Page(Json.Obj r, List<Json.Obj> rows, int offset, int limit)
        {
            var items = new List<object>();
            for (int i = offset; i < rows.Count && items.Count < limit; i++) items.Add(rows[i]);
            r["items"] = items;
            r["total"] = (double)rows.Count;
        }
    }
}
