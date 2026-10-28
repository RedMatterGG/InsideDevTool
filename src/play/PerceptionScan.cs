using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev.Play
{
    // PerceptionScan.cs (owner OBSERVE): what is around the body.
    //   Component caches: ONE Object.FindObjectsOfType(MonoBehaviour) pass fills every type at once (active objects
    //   only; each concrete class is matched to the tracked types once and remembered), a separate FindObjectsOfType only
    //   for a tracked type that is not a MonoBehaviour. A per-type pass walked all ~25 000 scene objects for each of ~40
    //   types, one every other frame for 90 s after any agent call: ~6.5 ms of every frame and a GC every few seconds.
    //   No background refresh by default (one pass is ~20 ms at 25 000 objects, a dropped frame each time): observe
    //   rescans inline what is older than 120 steps or from another epoch, while the game waits for the agent anyway.
    //   editor.cfg play.perception_bg_s = N > 0 refreshes stale caches in the background every N s of real time (never
    //   while the real-time recorder films). The pass cost is kept for observe full (perf).
    //   Objects: cached interactables + grabbable rigidbodies from one Physics.OverlapSphere (BoyGrabState.IsGrabCollider)
    //   + ledges from the boy's status (edges below the boy = drops, obstructionPlateau = a climbable top). Every Device
    //   subclass comes from one Device scan plus the non-Device classes of DeviceKinds.ScanTypes (PerceptionKinds.cs):
    //   levers, hatches, boards, lift doors and the other grab devices, each with its interact verbs; a device's
    //   position is the nearest point of its grab trigger (Device.triggerGrab). The cut to max_objects keeps actionable
    //   rows first (ObjectPick).
    //   Hazards: dogs, guards, sentries, spotlights, rope guns, the mermaid, kill zones, crushers, the shockwave, the
    //   lineup, the KillerKinds classes (darts, the cargo hall guard, the capsule run-over, the flush gate, the lineup
    //   camera, the moving cover, the boy explosion, the pig), active PlayMaker FSMs with a kill action (PD_KillBoy,
    //   RopeGunShootAndKillMainCharacter), plus drowning (air left) and fall (predicted landing speed vs
    //   properties.death.fallSpeedLimit).
    public static partial class Perception
    {
        sealed class TypeCache
        {
            public Type type;
            public UObj[] items = new UObj[0];
            public int epoch = -1, frame = -100000;
            public long step = -1;
            public double ms;
            public bool invalid = true;
        }

        internal static readonly List<string> MissingTypes = new List<string>();

        static readonly Type[] ObjectTypes = WithNamed(new[]
        {
            typeof(Ladder), typeof(Rope), typeof(Device), typeof(SwitchValve), typeof(SwitchButtonNew), typeof(Gate),
            typeof(Elevator), typeof(MindHat), typeof(Submarine), typeof(BoyWater), typeof(Savepoint),
        }, DeviceKinds.ScanTypes);

        static readonly Type[] HazardTypes = WithNamed(new[]
        {
            typeof(Hulv), typeof(ChaserBase), typeof(SentryGun), typeof(SentryGunAimAndShoot), typeof(SpotLightDetector), typeof(RopeGun), typeof(WaterGirl),
            typeof(Killer), typeof(DelayedKiller), typeof(WaterFlushKiller), typeof(HeadlessKillTrigger), typeof(GateCrush), typeof(CityGateCrush),
            typeof(WagonCrush), typeof(ZombieMonitorDetection),
        }, KillerKinds.Types);

        // the listed types plus game classes found by name in Assembly-CSharp (a missing name is noted, not fatal)
        static Type[] WithNamed(Type[] fixedTypes, string[] names)
        {
            var l = new List<Type>(fixedTypes);
            foreach (var n in names)
            {
                Type t = null;
                try { t = typeof(Device).Assembly.GetType(n, false); } catch { t = null; }
                if (t != null && !l.Contains(t)) l.Add(t);
                else if (t == null && MissingTypes.Count < 20) MissingTypes.Add(n);
            }
            return l.ToArray();
        }

        static readonly Type[] OtherTypes = { typeof(SubmarineBoyInteraction), typeof(MindHatReceiver), typeof(ElevatorFall) };

        static readonly Dictionary<Type, TypeCache> caches = new Dictionary<Type, TypeCache>();
        static readonly List<TypeCache> order = new List<TypeCache>();
        static int lastTickScan = -100000;
        static float lastFullAt = -100f;
        static double lastFullMs;
        const int StaleSteps = 30;
        static int bgCheck;
        static float bgSeconds;
        static readonly Dictionary<Type, int[]> matches = new Dictionary<Type, int[]>();
        static readonly List<int> loose = new List<int>();

        static void InitCaches()
        {
            if (order.Count > 0) return;
            foreach (var t in ObjectTypes) AddCache(t);
            foreach (var t in HazardTypes) AddCache(t);
            foreach (var t in OtherTypes) AddCache(t);
        }

        static void AddCache(Type t)
        {
            if (caches.ContainsKey(t)) return;
            var c = new TypeCache { type = t };
            caches[t] = c;
            order.Add(c);
            if (!typeof(MonoBehaviour).IsAssignableFrom(t)) loose.Add(order.Count - 1);
        }

        static bool Stale(TypeCache c) { return Stale(c, StaleSteps); }

        static bool Stale(TypeCache c, int steps)
        {
            if (c.invalid || c.epoch != Observer.Epoch) return true;
            return LogicManager.stepCount - c.step >= steps && Time.frameCount - c.frame >= steps;
        }

        static void Scan(TypeCache c)
        {
            ScanAll();
        }

        static void ScanAll()
        {
            var sw = Stopwatch.StartNew();
            UObj[] all;
            try { all = UObj.FindObjectsOfType(typeof(MonoBehaviour)); }
            catch { all = null; }
            var lists = new List<UObj>[order.Count];
            if (all != null)
            {
                foreach (var o in all)
                {
                    if (o == null) continue;
                    var ct = o.GetType();
                    int[] idx;
                    if (!matches.TryGetValue(ct, out idx)) { idx = Match(ct); matches[ct] = idx; }
                    for (int k = 0; k < idx.Length; k++)
                    {
                        int i = idx[k];
                        if (lists[i] == null) lists[i] = new List<UObj>();
                        lists[i].Add(o);
                    }
                }
            }
            foreach (int i in loose)
            {
                UObj[] found;
                try { found = UObj.FindObjectsOfType(order[i].type); }
                catch { found = null; }
                if (found != null) lists[i] = new List<UObj>(found);
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            for (int i = 0; i < order.Count; i++)
            {
                var c = order[i];
                c.items = lists[i] != null ? lists[i].ToArray() : new UObj[0];
                c.epoch = Observer.Epoch;
                c.step = LogicManager.stepCount;
                c.frame = Time.frameCount;
                c.invalid = false;
                c.ms = ms / Math.Max(1, order.Count);
            }
            lastFullMs = ms;
            lastFullAt = Time.realtimeSinceStartup;
        }

        static int[] Match(Type ct)
        {
            var l = new List<int>();
            for (int i = 0; i < order.Count; i++) if (order[i].type.IsAssignableFrom(ct)) l.Add(i);
            return l.ToArray();
        }

        // a cache from an older epoch is rescanned inline while this frame's inline budget (8 ms) lasts; beyond it the
        // old list is returned (objects destroyed by the load drop out through the Unity null checks) and observe warns
        // scan_pending
        const double InlineBudgetMs = 8;
        static int inlineFrame = -1, scanPendingFrame = -1;
        static double inlineMs;

        internal static bool ScanPending { get { return scanPendingFrame == Time.frameCount; } }

        internal static UObj[] Items(Type t)
        {
            InitCaches();
            TypeCache c;
            if (!caches.TryGetValue(t, out c)) return new UObj[0];
            if (c.invalid || c.epoch != Observer.Epoch)
            {
                int f = Time.frameCount;
                if (f != inlineFrame) { inlineFrame = f; inlineMs = 0; }
                if (inlineMs < InlineBudgetMs) { ScanAll(); inlineMs += lastFullMs; }
                else scanPendingFrame = f;
            }
            return c.items;
        }

        static void EnsureFresh(Type t)
        {
            InitCaches();
            TypeCache c;
            if (caches.TryGetValue(t, out c) && Stale(c)) Scan(c);
        }

        // main thread (observe, before it builds): rescan up to n types from another epoch or older than 120 steps (the
        // tick keeps every type under about 62 steps while the game runs, so this normally only runs after a load);
        // true when more remain
        const int ObserveStaleSteps = 120;

        internal static bool RefreshSome(int n)
        {
            InitCaches();
            foreach (var c in order)
            {
                if (!Stale(c, ObserveStaleSteps)) continue;
                ScanAll();
                return false;
            }
            return false;
        }

        internal static void InvalidateAll()
        {
            foreach (var c in order) c.invalid = true;
        }

        static void TickRefresh()
        {
            InitCaches();
            if (--bgCheck <= 0) { bgCheck = 120; try { bgSeconds = EditorState.Get("play.perception_bg_s", 0f); } catch { bgSeconds = 0f; } }
            if (bgSeconds <= 0f || Time.realtimeSinceStartup - lastFullAt < bgSeconds || Recorder.On) return;
            foreach (var c in order)
            {
                if (!Stale(c)) continue;
                ScanAll();
                lastTickScan = Time.frameCount;
                return;
            }
        }

        internal static Json.Obj Perf()
        {
            InitCaches();
            var o = new Json.Obj();
            double total = 0, worst = 0;
            int stale = 0;
            string worstName = "";
            foreach (var c in order)
            {
                total += c.ms;
                if (c.ms > worst) { worst = c.ms; worstName = c.type.Name; }
                if (Stale(c)) stale++;
            }
            o["scan_ms_total"] = PlayShared.R(lastFullMs, 2);
            o["scan_pass"] = "one FindObjectsOfType(MonoBehaviour) for all types";
            o["scan_ms_worst"] = PlayShared.R(worst, 2);
            o["scan_worst_type"] = worstName;
            o["stale_types"] = (double)stale;
            o["types"] = (double)order.Count;
            return o;
        }

        // ---------------------------------------------------------------- reflection on private game members
        static readonly Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();
        static readonly Dictionary<string, MethodInfo> methods = new Dictionary<string, MethodInfo>();
        internal static readonly List<string> MissingMembers = new List<string>();
        // first failure per part, logged once (an in-game test reads them from the log and from observe warnings)
        internal static readonly List<string> Failures = new List<string>();

        static void NoteFailure(string part, Exception e)
        {
            foreach (var f in Failures) if (f.StartsWith(part + ":", StringComparison.Ordinal)) return;
            if (Failures.Count >= 20) return;
            Failures.Add(part + ": " + e.GetType().Name + ": " + e.Message);
            try { DevLog.Error("[play] perception " + part, e); } catch { }
        }
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static FieldInfo Field(Type t, string name)
        {
            string key = t.FullName + "." + name;
            FieldInfo f;
            if (fields.TryGetValue(key, out f)) return f;
            for (var x = t; x != null && f == null; x = x.BaseType) f = x.GetField(name, Any | BindingFlags.DeclaredOnly);
            fields[key] = f;
            if (f == null && MissingMembers.Count < 20) MissingMembers.Add(key);
            return f;
        }

        static object Read(object target, string name)
        {
            if (target == null) return null;
            var f = Field(target.GetType(), name);
            if (f == null) return null;
            try { return f.GetValue(f.IsStatic ? null : target); } catch { return null; }
        }

        // a state enum that only some classes have (state, else mState): no reflection_missing note when absent
        static string DeviceState(object target)
        {
            if (target == null) return null;
            foreach (var name in new[] { "state", "mState" })
            {
                string key = "?" + target.GetType().FullName + "." + name;
                FieldInfo f;
                if (!fields.TryGetValue(key, out f))
                {
                    for (var x = target.GetType(); x != null && f == null; x = x.BaseType) f = x.GetField(name, Any | BindingFlags.DeclaredOnly);
                    fields[key] = f;
                }
                if (f == null) continue;
                object v;
                try { v = f.GetValue(f.IsStatic ? null : target); } catch { v = null; }
                return v != null ? StateNames.Lower(v.ToString()) : null;
            }
            return null;
        }

        static bool ReadBool(object target, string name, bool def)
        {
            var v = Read(target, name);
            return v is bool ? (bool)v : def;
        }

        static string ReadEnum(object target, string name)
        {
            var v = Read(target, name);
            return v != null ? StateNames.Lower(v.ToString()) : null;
        }

        static object Call(object target, Type t, string name)
        {
            string key = t.FullName + "." + name + "()";
            MethodInfo m;
            if (!methods.TryGetValue(key, out m))
            {
                m = t.GetMethod(name, Any, null, Type.EmptyTypes, null);
                methods[key] = m;
                if (m == null && MissingMembers.Count < 20) MissingMembers.Add(key);
            }
            if (m == null) return null;
            try { return m.Invoke(m.IsStatic ? null : target, null); } catch { return null; }
        }

        // ---------------------------------------------------------------- objects

        internal static List<ObjInfo> ObjectsCounted(Vector3 center, float radius, int limit, out int total)
        {
            total = 0;
            var all = new List<ObjInfo>();
            var c = Controlled();
            Vector3 body = c != null ? c.pos : center;
            float r2 = radius * radius;
            var typed = new HashSet<int>();
            Collider grabReach = null;
            Ladder ladderReach = null;
            if (c != null && c.boy != null && (c.boy.isPlayer || c.boy.isMindControlled))
            {
                try { grabReach = BoyGrabState.FindGrabCollider(c.boy); } catch { grabReach = null; }
                try { ladderReach = BoyLadderState.FindLadder(c.boy, null, false); } catch { ladderReach = null; }
            }
            foreach (var t in ObjectTypes)
            {
                foreach (var x in Items(t))
                {
                    try
                    {
                        var comp = x as Component;
                        if (comp == null) continue;
                        var go = comp.gameObject;
                        if (!go.activeInHierarchy) continue;
                        var rope = comp as Rope;
                        if (rope != null && rope.visualOnly) continue;
                        string kind = KindOf(comp);
                        if (kind == null) continue;
                        Vector3 p = PosFor(comp, kind, body);
                        if ((p - center).sqrMagnitude > r2) continue;
                        typed.Add(go.GetInstanceID());
                        string state;
                        var verbs = Verbs(kind, comp, c, out state);
                        if (comp == ladderReach) state = Join(state, "in_reach");
                        all.Add(Info(go, kind, p, body, center, verbs, state));
                    }
                    catch (Exception e) { NoteFailure("object " + t.Name, e); }
                }
            }
            try { AddGrabbables(all, typed, c, body, center, radius, grabReach); } catch (Exception e) { NoteFailure("grabbables", e); }
            try { if (c != null && c.boy != null) AddLedges(all, c, body, center, r2); } catch (Exception e) { NoteFailure("ledges", e); }
            all.Sort((a, b) => a.dist.CompareTo(b.dist));
            total = all.Count;
            if (limit >= 0 && all.Count > limit)
            {
                var actionable = new List<bool>(all.Count);
                foreach (var o in all) actionable.Add(o.actions.Count > 0);
                var kept = new List<ObjInfo>();
                foreach (int i in ObjectPick.Keep(actionable, limit)) kept.Add(all[i]);
                all = kept;
            }
            foreach (var o in all)
            {
                if (o.id == null) o.id = Observer.IdFor(o.go, "o");
                o.path = PlayShared.PathOf(o.go.transform);
                Vector3 d = o.pos - body;
                o.dist = d.magnitude;
            }
            return all;
        }

        static ObjInfo Info(GameObject go, string kind, Vector3 p, Vector3 body, Vector3 center, List<string> verbs, string state)
        {
            var o = new ObjInfo { kind = kind, name = go.name, go = go, pos = p, state = state ?? "" };
            Vector3 d = p - body;
            o.dx = d.x;
            o.dy = d.y;
            o.dz = d.z;
            o.dist = (p - center).magnitude;
            if (verbs != null) o.actions.AddRange(verbs);
            return o;
        }

        static string Join(string a, string b) { return string.IsNullOrEmpty(a) ? b : a + " " + b; }

        static void AddGrabbables(List<ObjInfo> all, HashSet<int> typed, Ctl c, Vector3 body, Vector3 center, float radius, Collider reach)
        {
            Collider[] cols;
            try { cols = Physics.OverlapSphere(center, Mathf.Min(radius, 30f), CollisionUtils.Layers.playerInteractionMask); }
            catch { return; }
            if (cols == null) return;
            var seen = new HashSet<int>();
            Rigidbody reachBody = reach != null ? reach.attachedRigidbody : null;
            Rigidbody held = c != null && c.holding != null ? c.holding.GetComponent<Rigidbody>() : null;
            int n = 0;
            foreach (var col in cols)
            {
                if (++n > 600) break;
                if (col == null) continue;
                var rb = col.attachedRigidbody;
                if (rb == null) continue;
                if (!(col.isTrigger || !rb.isKinematic)) continue;
                if (!BoyGrabState.IsGrabCollider(col)) continue;
                var go = rb.gameObject;
                int gid = go.GetInstanceID();
                if (!seen.Add(gid)) continue;
                if (UnderTyped(go.transform, typed)) continue;
                if (go.GetComponentInParent<Character>() != null || go.GetComponentInParent<Rope>() != null || go.GetComponentInParent<Hulv>() != null || go.GetComponentInParent<Submarine>() != null) continue;
                bool item = col.isTrigger;
                string kind = item ? "item" : rb.mass >= 40f ? "pushable" : "box";
                Vector3 p = col.bounds.center;
                string state;
                var verbs = BoxVerbs(kind, c, rb == held, out state);
                if (rb == reachBody) state = Join(state, "in_reach");
                all.Add(Info(go, kind, p, body, center, verbs, state));
            }
        }

        static bool UnderTyped(Transform t, HashSet<int> typed)
        {
            for (int i = 0; t != null && i < 5; i++, t = t.parent) if (typed.Contains(t.gameObject.GetInstanceID())) return true;
            return false;
        }

        static void AddLedges(List<ObjInfo> all, Ctl c, Vector3 body, Vector3 center, float r2)
        {
            var st = c.boy.status;
            if (st.edges != null)
                for (int side = 0; side < st.edges.Length && side < 2; side++)
                {
                    var e = st.edges[side];
                    if (e == null || e.plateau == null || !e.belowBoy) continue;
                    var col = e.plateau.collider;
                    if (col == null) continue;
                    Vector3 p = e.plateau.pos;
                    if ((p - center).sqrMagnitude > r2) continue;
                    var verbs = new List<string>();
                    if (c.grounded && e.distanceToBoy < 1.2f) verbs.Add("climb_down");
                    var o = Info(col.gameObject, "ledge", p, body, center, verbs, side == 0 ? "drop left" : "drop right");
                    o.id = Observer.IdFor(col.gameObject, "o", 1 + side);
                    all.Add(o);
                }
            var top = st.obstructionPlateau;
            if (top != null && top.collider != null)
            {
                Vector3 p = top.pos;
                if ((p - center).sqrMagnitude <= r2)
                {
                    var verbs = new List<string> { "climb" };
                    var o = Info(top.collider.gameObject, "ledge", p, body, center, verbs, "climb up");
                    o.id = Observer.IdFor(top.collider.gameObject, "o", 3);
                    all.Add(o);
                }
            }
        }

        // observe kind of a cached component; null = not listed (secret devices, WagonCrush, FenceField, ...)
        static string KindOf(Component comp)
        {
            if (comp is Ladder) return "ladder";
            if (comp is Rope) return "rope";
            if (comp is SwitchValve) return "valve";
            if (comp is SwitchButtonNew) return "button";
            if (comp is Gate) return "door";
            if (comp is Elevator) return "elevator";
            if (comp is MindHat) return "hat";
            if (comp is Submarine) return "submarine";
            if (comp is BoyWater) return "water";
            if (comp is Savepoint) return "savepoint";
            return DeviceKinds.KindOf(comp.GetType().Name);
        }

        static bool IsDeviceKind(string kind)
        {
            return kind == DeviceKinds.Hatch || kind == DeviceKinds.Boards || kind == DeviceKinds.Device || kind == DeviceKinds.LiftDoor || kind == "door" || kind == "lever" || kind == "button";
        }

        static Vector3 PosFor(Component comp, string kind, Vector3 body)
        {
            var rope = comp as Rope;
            if (rope != null)
            {
                var bodies = rope.bodies;
                Vector3 best = comp.transform.position;
                float bd = float.MaxValue;
                if (bodies != null)
                    foreach (var rb in bodies)
                    {
                        if (rb == null) continue;
                        float d = (rb.position - body).sqrMagnitude;
                        if (d < bd) { bd = d; best = rb.position; }
                    }
                return best;
            }
            var dev = comp as Device;
            if (dev != null && dev.triggerGrab != null)
            {
                var tc = dev.triggerGrab.GetComponent<Collider>();
                if (tc != null) return Closest(tc.bounds, body);
            }
            if (kind == "ladder" || kind == "water" || kind == "savepoint" || kind == "elevator" || (IsDeviceKind(kind) && kind != "lever"))
            {
                var col = comp.GetComponent<Collider>();
                if (col == null && kind != "ladder" && kind != "water" && kind != "savepoint" && kind != "elevator") col = comp.GetComponentInChildren<Collider>();
                if (col != null) return Closest(col.bounds, body);
            }
            if (kind == "submarine") return ((Submarine)comp).pos3;
            return comp.transform.position;
        }

        static Vector3 Closest(Bounds b, Vector3 p)
        {
            Vector3 mn = b.min, mx = b.max;
            return new Vector3(Mathf.Clamp(p.x, mn.x, mx.x), Mathf.Clamp(p.y, mn.y, mx.y), Mathf.Clamp(p.z, mn.z, mx.z));
        }

        static List<string> BoxVerbs(string kind, Ctl c, bool held, out string state)
        {
            state = held ? "held" : "";
            var l = new List<string>();
            if (c == null) return l;
            if (held) { l.Add("release"); l.Add("push"); l.Add("pull"); if (c.kind == "huddle") l.Add("throw"); return l; }
            l.Add("grab");
            if (kind != "item") { l.Add("push"); l.Add("pull"); }
            return l;
        }

        // verbs and short state for a cached object kind
        internal static List<string> Verbs(string kind, Component comp, Ctl c, out string state)
        {
            state = "";
            var l = new List<string>();
            string bodyState = c != null ? c.state : "";
            bool boyLike = c != null && (c.kind == "boy" || c.kind == "dude");
            switch (kind)
            {
                case "ladder":
                    if (bodyState == "Ladder") { l.Add("ladder_up"); l.Add("ladder_down"); l.Add("ladder_off"); state = "on"; }
                    else if (boyLike) { l.Add("ladder_up"); l.Add("ladder_down"); }
                    break;
                case "rope":
                    if (bodyState == "Rope") { l.Add("rope_jump"); state = "on"; }
                    break;
                case "lever":
                    l.Add("lever");
                    state = DeviceState(comp) ?? "";
                    break;
                case "valve":
                    l.Add("valve");
                    state = ReadEnum(comp, "state") ?? "";
                    break;
                case "button":
                    if (boyLike) l.Add("button");
                    state = ReadEnum(comp, "state") ?? "";
                    if (ReadBool(comp, "buttonOn", false)) state = Join(state, "on");
                    break;
                case "door":
                case "hatch":
                case "boards":
                case "device":
                case "lift_door":
                {
                    string tn = comp.GetType().Name;
                    if (c != null) l.AddRange(DeviceKinds.Verbs(tn, c.kind));
                    state = DeviceState(comp) ?? "";
                    if (DeviceKinds.NeedsFollowers(tn)) state = Join(state, "needs_followers");
                    break;
                }
                case "elevator":
                    state = ReadBool(comp, "isMoving", false) ? "moving" : ReadBool(comp, "isDown", true) ? "down" : "up";
                    break;
                case "hat":
                {
                    var hat = comp as MindHat;
                    if (hat != null)
                    {
                        Boy wearer = null;
                        try { wearer = hat.GetBoyInHat(); } catch { }
                        bool off = false;
                        try { off = hat.IsMindHatTurnedOff(); } catch { }
                        if (wearer != null && wearer == ScriptGlobals.boy) { l.Add("hat_detach"); state = "worn"; }
                        else state = off ? "off" : wearer != null ? "worn by other" : "free";
                        if (state == "free" && c != null && c.kind == "boy") l.Add("hat_wear");
                    }
                    break;
                }
                case "submarine":
                    if (c != null && c.kind == "submarine" && c.sub == comp) { l.Add("sub_boost"); l.Add("sub_exit"); state = "driving"; }
                    else
                    {
                        state = "empty";
                        if (boyLike) l.Add("sub_enter");
                    }
                    break;
                case "water":
                {
                    var w = comp as BoyWater;
                    bool inThis = c != null && c.boy != null && c.boy.status.water == w;
                    if (inThis || (c != null && c.huddle != null && c.huddle.status != null && c.huddle.status.water == w))
                    {
                        state = "in";
                        l.Add("swim_down"); l.Add("swim_up"); l.Add("surface");
                    }
                    if (w != null && w.infiniteAir) state = Join(state, "infinite_air");
                    break;
                }
                case "savepoint":
                {
                    var sp = comp as Savepoint;
                    if (sp != null && PersistentBehaviour<Savegame>.instance != null)
                    {
                        var cur = Savegame.GetCurrentSavepoint();
                        if (cur.subscene == sp.subsceneIndex && cur.savepoint == sp.index) state = "current";
                    }
                    break;
                }
            }
            return l;
        }

        // the kind of a GameObject from its components (for Affordances on objects not in the latest observation)
        static string Classify(GameObject go, out Component comp)
        {
            comp = null;
            foreach (var t in ObjectTypes)
            {
                var x = go.GetComponentInParent(t);
                if (x == null) continue;
                string k = KindOf(x);
                if (k != null) { comp = x; return k; }
            }
            foreach (var col in go.GetComponentsInChildren<Collider>())
            {
                if (col == null || !BoyGrabState.IsGrabCollider(col)) continue;
                var rb = col.attachedRigidbody;
                return col.isTrigger ? "item" : rb != null && rb.mass >= 40f ? "pushable" : "box";
            }
            return "other";
        }

        // ---------------------------------------------------------------- hazards
        static readonly List<HazardInfo> lastHazards = new List<HazardInfo>();

        internal static List<HazardInfo> HazardsCounted(Vector3 center, float radius, int limit, out int total)
        {
            var l = new List<HazardInfo>();
            var c = Controlled();
            Vector3 body = c != null ? c.pos : center;
            Vector3 bvel = c != null ? c.vel : Vector3.zero;
            float r2 = radius * radius;
            foreach (var t in HazardTypes)
            {
                foreach (var x in Items(t))
                {
                    var comp = x as Behaviour;
                    if (comp == null || !comp.isActiveAndEnabled) continue;
                    HazardInfo h;
                    try { h = HazardOf(comp, body, bvel); }
                    catch (Exception e) { NoteFailure("hazard " + t.Name, e); continue; }
                    if (h == null) continue;
                    float near = h.kind == "other" ? Mathf.Max(radius, 60f) : radius;
                    if ((h.pos - center).sqrMagnitude > near * near && !(h.threat && (h.pos - center).sqrMagnitude <= 4f * r2)) continue;
                    l.Add(h);
                }
            }
            try { AddKillFsms(l, body, center, radius); } catch (Exception e) { NoteFailure("kill fsm", e); }
            try { var sw = Shockwave(body); if (sw != null) l.Add(sw); } catch (Exception e) { NoteFailure("shockwave", e); }
            if (c != null)
            {
                try { var dr = Drowning(c, body); if (dr != null) l.Add(dr); } catch (Exception e) { NoteFailure("drowning", e); }
                try { var fa = Fall(c, body); if (fa != null) l.Add(fa); } catch (Exception e) { NoteFailure("fall", e); }
            }
            foreach (var h in l)
            {
                Vector3 d = h.pos - body;
                h.dx = d.x;
                h.dy = d.y;
                if (h.dist < 0) h.dist = d.magnitude;
            }
            l.Sort((a, b) => HazardRank.Compare(a.threat, a.etaS, a.dist, b.threat, b.etaS, b.dist));
            total = l.Count;
            if (limit >= 0 && l.Count > limit) l.RemoveRange(limit, l.Count - limit);
            foreach (var h in l) if (h.id == null) h.id = Observer.IdFor(h.go, "h");
            lastHazards.Clear();
            lastHazards.AddRange(l);
            return l;
        }

        static HazardInfo New(string kind, Component comp, Vector3 pos)
        {
            return new HazardInfo { kind = kind, name = comp.gameObject.name, go = comp.gameObject, pos = pos, state = "", dist = -1 };
        }

        static HazardInfo HazardOf(Behaviour comp, Vector3 body, Vector3 bvel)
        {
            var dog = comp as Hulv;
            if (dog != null)
            {
                var h = New("dog", comp, comp.transform.position);
                var ai = dog.hulvAI;
                bool chasing = false;
                if (ai != null)
                {
                    chasing = ai.GetCurrentMode() == HulvAI.AIMode.ChaseBoy;
                    var cm = ai.GetChaseMode();
                    h.state = chasing ? StateNames.Lower(cm.ToString()) : "patrol";
                    h.threat = chasing && cm != HulvAI.ChaseMode.RunAway && cm != HulvAI.ChaseMode.Idle;
                }
                Vector3 to = body - h.pos;
                float dist = to.magnitude;
                h.dist = dist;
                if (dist > 0.01f)
                {
                    float closing = Vector3.Dot(dog.velocity - bvel, to / dist);
                    h.etaS = (float)HazardRank.Eta(dist, closing);
                }
                if (h.threat && h.etaS < 0 && dist < 3f) h.etaS = 0f;
                return h;
            }
            var guard = comp as ChaserBase;
            if (guard != null)
            {
                var h = New("guard", comp, comp.transform.position);
                var gm = Read(guard, "guardMode");
                string s = gm != null ? gm.ToString() : null;
                h.state = s != null ? StateNames.Lower(s) : "unreadable";
                h.threat = s == "ChasingBoy" || s == "StranglingBoy";
                return h;
            }
            if (comp is SentryGun)
            {
                var h = New("sentry", comp, comp.transform.position);
                string s = ReadEnum(comp, "state");
                bool awake = ReadBool(comp, "isAwake", true);
                h.state = (s ?? "unreadable") + (awake ? "" : " asleep");
                h.threat = awake && (s == "rotation" || s == "kill_boy");
                return h;
            }
            if (comp is SentryGunAimAndShoot)
            {
                var h = New("sentry", comp, comp.transform.position);
                h.state = "aim_and_shoot";
                return h;
            }
            var spot = comp as SpotLightDetector;
            if (spot != null)
            {
                var h = New("spotlight", comp, comp.transform.position);
                bool on = ReadBool(spot, "scriptActive", true);
                bool sees = spot.BoyIsVisible;
                h.state = !on ? "off" : sees ? "sees_body" : "searching";
                h.threat = on && sees;
                h.safe = on && !sees;
                return h;
            }
            var gun = comp as RopeGun;
            if (gun != null)
            {
                var h = New("rope_gun", comp, comp.transform.position);
                bool shooting = false;
                try { shooting = gun.IsShooting(); } catch { }
                string s = ReadEnum(gun, "mShootingState");
                h.state = s ?? (shooting ? "shooting" : "idle");
                h.threat = shooting;
                return h;
            }
            if (comp is WaterGirl)
            {
                var h = New("mermaid", comp, comp.transform.position);
                bool moving = ReadBool(comp, "moving", false);
                bool caught = ReadBool(comp, "boyCaught", false);
                h.state = caught ? "caught" : moving ? "chase" : "idle";
                h.threat = moving || caught;
                return h;
            }
            if (comp is Killer || comp is DelayedKiller || comp is WaterFlushKiller || comp is HeadlessKillTrigger)
            {
                Vector3 p = ClosestOn(comp, body);
                var h = New("kill_zone", comp, p);
                bool on = true;
                var k = comp as Killer;
                if (k != null) { try { on = k.getActiveState(); } catch { } }
                else if (comp is DelayedKiller) on = ReadBool(comp, "isEnabled", true);
                h.state = (on ? "on " : "off ") + StateNames.Lower(comp.GetType().Name);
                float d = (p - body).magnitude;
                h.dist = d;
                h.threat = on && d < 1.0f;
                return h.threat || on ? h : null;
            }
            if (comp is GateCrush || comp is CityGateCrush || comp is WagonCrush)
            {
                Vector3 p = ClosestOn(comp, body);
                var h = New("crusher", comp, p);
                h.state = StateNames.Lower(comp.GetType().Name);
                return h;
            }
            if (comp is ZombieMonitorDetection)
            {
                var h = New("other", comp, comp.transform.position);
                bool det = ReadBool(comp, "detectionActive", false);
                bool walking = ReadBool(comp, "walking", false);
                h.state = "lineup " + (det ? "detecting" : "idle") + (walking ? " marching" : "");
                h.threat = det;
                h.musicTimed = true;
                return h;
            }
            string tn = comp.GetType().Name;
            if (KillerKinds.Known(tn))
            {
                Vector3 p = ClosestOn(comp, body);
                var h = New(KillerKinds.KindOf(tn), comp, p);
                float d = (p - body).magnitude;
                h.dist = d;
                string field = KillerKinds.StateField(tn);
                string s = field != null ? ReadEnum(comp, field) : null;
                h.state = StateNames.Lower(tn) + (string.IsNullOrEmpty(s) ? "" : " " + s);
                h.threat = KillerKinds.Positional(tn) ? d < 1f : KillerKinds.Threat(tn, s);
                h.musicTimed = KillerKinds.MusicTimed(tn);
                return h;
            }
            return null;
        }

        // ---------------------------------------------------------------- kill FSMs
        static readonly Dictionary<int, bool> killFsms = new Dictionary<int, bool>();

        static bool IsKillFsm(PlayMakerFSM f)
        {
            int id = f.GetInstanceID();
            bool k;
            if (killFsms.TryGetValue(id, out k)) return k;
            var fsm = f.Fsm;
            if (fsm == null || !fsm.Initialized || fsm.States == null) return false;
            k = false;
            foreach (var st in fsm.States)
            {
                if (st == null || k) continue;
                FsmStateAction[] acts;
                try { acts = st.Actions; } catch { continue; }
                if (acts == null) continue;
                foreach (var a in acts) if (a != null && KillerKinds.IsKillAction(a.GetType().Name)) { k = true; break; }
            }
            if (killFsms.Count > 4000) killFsms.Clear();
            killFsms[id] = k;
            return k;
        }

        static void AddKillFsms(List<HazardInfo> l, Vector3 body, Vector3 center, float radius)
        {
            var list = PlayMakerFSM.FsmList;
            if (list == null) return;
            float far = radius + 30f;
            float r2 = radius * radius;
            var seen = new HashSet<int>();
            foreach (var h in l) if (h.go != null) seen.Add(h.go.GetInstanceID());
            for (int i = 0; i < list.Count; i++)
            {
                var f = list[i];
                if (f == null || !f.enabled) continue;
                var go = f.gameObject;
                if (!go.activeInHierarchy || (go.transform.position - center).sqrMagnitude > far * far) continue;
                if (seen.Contains(go.GetInstanceID()) || !IsKillFsm(f)) continue;
                Vector3 p = ClosestOn(f, body);
                if ((p - center).sqrMagnitude > r2) continue;
                seen.Add(go.GetInstanceID());
                var h = New(KillerKinds.FsmKind(go.name), f, p);
                float d = (p - body).magnitude;
                h.dist = d;
                string st = null;
                try { st = f.Fsm != null ? f.Fsm.ActiveStateName : null; } catch { st = null; }
                h.state = "kill fsm " + f.FsmName + (string.IsNullOrEmpty(st) ? "" : ": " + st);
                h.threat = d < 0.3f;
                l.Add(h);
            }
        }

        static Vector3 ClosestOn(Component comp, Vector3 body)
        {
            var col = comp.GetComponent<Collider>();
            if (col == null) col = comp.GetComponentInChildren<Collider>();
            return col != null ? Closest(col.bounds, body) : comp.transform.position;
        }

        static HazardInfo Shockwave(Vector3 body)
        {
            var fpm = LevelGlobals.forcePushManager;
            if (fpm == null || !fpm.isActiveAndEnabled) return null;
            var h = New("shockwave", fpm, fpm.transform.position);
            float t = fpm.musicTime, cycle = fpm.musicCycleTime;
            double eta = HazardRank.CycleEta(t, 5.9, cycle);
            bool cover = false;
            try { cover = ForcePushManager.GetInCover(); } catch { }
            object deadly = Call(fpm, typeof(ForcePushManager), "IsInsideDeadly");
            bool inside = deadly is bool ? (bool)deadly : true;
            bool armed = ReadBool(fpm, "killEnabled", true);
            h.state = "cycle " + PlayShared.F(PlayShared.R(t, 1)) + "/" + PlayShared.F(cycle) + " s" + (cover ? " in cover" : "") + (armed ? "" : " unarmed");
            h.etaS = (float)eta;
            h.safe = cover;
            h.threat = armed && inside && !cover && eta < 2.5;
            h.musicTimed = true;
            h.dist = 0;
            h.pos = body;
            return h;
        }

        static HazardInfo Drowning(Ctl c, Vector3 body)
        {
            if (c.airS < 0 || c.boy == null) return null;
            var w = c.boy.status.water;
            if (w == null) return null;
            float surf = body.y;
            try { surf = w.GetSurfaceHeight(c.boy); } catch { }
            var h = new HazardInfo { kind = "drowning", name = w.gameObject.name, go = w.gameObject, pos = new Vector3(body.x, surf, body.z), dist = -1 };
            h.state = "air " + PlayShared.F(PlayShared.R(c.airS, 1)) + "/" + PlayShared.F(w.maxDivingTime) + " s";
            h.etaS = c.airS;
            h.threat = c.airS < 4f;
            h.id = Observer.IdFor(w.gameObject, "h", 2);
            return h;
        }

        static HazardInfo Fall(Ctl c, Vector3 body)
        {
            if (c.boy == null || c.grounded || c.inWater || c.dead || c.vel.y > -3f) return null;
            var props = c.boy.properties;
            float lim = props != null && props.death != null ? props.death.fallSpeedLimit : 12.4f;
            float h = 60f;
            try
            {
                var hits = Physics.RaycastAll(body + Vector3.up * 0.1f, Vector3.down, 60f, CollisionUtils.Layers.defaultCollisionMask);
                float best = float.MaxValue;
                if (hits != null) foreach (var hit in hits) if (hit.collider != null && !hit.collider.isTrigger && hit.distance < best) best = hit.distance;
                if (best < float.MaxValue) h = Mathf.Max(0f, best - 0.1f);
            }
            catch { }
            var gv = c.boy.gravity;
            double g = Math.Sqrt(gv.x * gv.x + gv.y * gv.y);
            double secs;
            double land = HazardRank.LandingSpeed(c.vel.y, h, g, out secs);
            var hz = new HazardInfo { kind = "fall", name = "fall", go = c.go, pos = new Vector3(body.x, body.y - h, body.z), dist = h };
            hz.state = "falling " + PlayShared.F(PlayShared.R(-c.vel.y, 1)) + " m/s, landing " + PlayShared.F(PlayShared.R(land, 1)) + " of " + PlayShared.F(lim) + " m/s" + (h >= 59f ? " (no ground in 60 m)" : "");
            hz.etaS = (float)secs;
            hz.threat = land > lim * 0.95;
            hz.id = Observer.IdFor(c.go, "h", 1);
            return hz;
        }

        static string NearestThreat(Vector3 p)
        {
            string best = null;
            float bd = 6f;
            foreach (var h in lastHazards)
            {
                if (h.go == null || h.kind == "fall" || h.kind == "drowning") continue;
                float d = (h.go.transform.position - p).magnitude;
                if (h.kind == "shockwave") { if (h.threat) return "shockwave"; continue; }
                if ((h.threat && d < bd) || d < 2f) { best = h.kind; bd = d; }
            }
            return best;
        }
    }
}
