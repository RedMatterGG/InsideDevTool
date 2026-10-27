using System;
using System.Collections.Generic;
using System.Reflection;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // ActProbe.cs (owner INPUT): main-thread readings for act and interact. Take() fills a ActBody from the game every
    // logic step (cheap reads only, no Find*State): ScriptGlobals.mainCharacter each time (never cached across calls),
    // the dude under a mind-control hat (MindHatReceiver.ControlIsActive), the submarine while InsideSub, the savepoint,
    // and the state of one interact target device (the classes of InteractMacros.DeviceAxis: private "state" enum, or
    // "mState" for OpenElevatorDoors2, by cached reflection), plus BoyInput.GetExtraDeadZone for the turn rule.
    // A Scope lives for one tool call; its cached dude / submarine / target references are null-checked on every read.
    internal sealed class ActScope
    {
        public GameObject target;
        public Component device;
        public string deviceKind = "";
        public int deviceFace;
        // throw: the grab grip is dropped in the same queue operation that starts the first phase
        public bool dropGrip;
        public Boy dude;
        public Submarine submarine;
        public bool subLooked;
        public int dudeLookStep = int.MinValue;
        public readonly List<GameObject> watchGo = new List<GameObject>();
        public readonly List<ActWatch> watchState = new List<ActWatch>();
        public readonly List<string> devicesSeen = new List<string>();
        public readonly HashSet<string> threatsAtStart = new HashSet<string>();
        public bool hazardsBroken;
        public int reads;
    }

    internal static class ActProbe
    {
        static readonly Dictionary<Type, FieldInfo> stateFields = new Dictionary<Type, FieldInfo>();
        static readonly Dictionary<Type, FieldInfo> valueFields = new Dictionary<Type, FieldInfo>();
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        public static ActBody Take(ActScope s)
        {
            var b = new ActBody();
            if (s != null) s.reads++;
            try
            {
                b.step = LogicManager.stepCount;
                b.loading = GameManager.IsLoading();
                b.playing = GameManager.IsPlaying();
                b.inputEnabled = GameInput.IsEnabled();
            }
            catch (Exception) { }
            try { ReadBody(b, s); } catch (Exception) { b.has = false; }
            try { ReadSavepoint(b); } catch (Exception) { }
            if (s != null && s.device != null) { try { ReadDevice(b, s); } catch (Exception) { } }
            return b;
        }

        static void ReadSavepoint(ActBody b)
        {
            if (PersistentBehaviour<Savegame>.instance == null) return;
            int sub, sp;
            Savegame.GetCurrentSavepoint(out sub, out sp);
            b.sub = sub; b.sp = sp;
        }

        static void ReadBody(ActBody b, ActScope s)
        {
            if (ScriptGlobals.huddleActive)
            {
                var h = ScriptGlobals.huddle;
                b.has = true;
                b.kind = "huddle";
                b.state = h.state != null ? ActStateNames.Short(h.state.GetType().Name) : "";
                b.dead = h.isDead;
                var st = h.status;
                b.grounded = st != null && st.hasFoothold;
                b.inWater = (st != null && st.water != null) || h.isSwimming;
                b.holding = h.grabState != null && h.grabState.IsGrabbing();
                SetPos(b, h.pos3, h.velocity3);
                return;
            }
            var boy = ScriptGlobals.boy;
            if (boy == null || !boy.isCharacterActive) return;
            b.mindControlling = boy.isMindControlling;
            if (boy.isMindControlling)
            {
                b.special = "MindControlling";
                var d = s != null ? Dude(s, b.step) : FindDude();
                if (d != null)
                {
                    ReadBoy(b, d);
                    b.kind = "dude";
                    b.special = d.specialBoyMode == Boy.SpecialBoyMode.InsideSub ? "InsideSub" : "MindControlling";
                    b.mindControlling = true;
                    return;
                }
                ReadBoy(b, boy);
                b.special = "MindControlling";
                b.mindControlling = true;
                return;
            }
            ReadBoy(b, boy);
            if (boy.specialBoyMode != Boy.SpecialBoyMode.None) b.special = boy.specialBoyMode.ToString();
            if (boy.specialBoyMode == Boy.SpecialBoyMode.InsideSub)
            {
                b.kind = "submarine";
                var sub = s != null ? Sub(s) : (Submarine)UnityEngine.Object.FindObjectOfType(typeof(Submarine));
                if (sub != null)
                {
                    var rb = sub.GetComponent<Rigidbody>();
                    SetPos(b, sub.transform.position, rb != null ? rb.velocity : Vector3.zero);
                    b.subOnSurface = sub.SubOnSurface();
                    b.subBoosting = sub.booster != null && sub.booster.IsBoosting;
                }
            }
        }

        static void ReadBoy(ActBody b, Boy boy)
        {
            b.has = true;
            b.kind = "boy";
            b.dead = boy.isDead;
            var state = boy.state;
            b.state = state != null ? ActStateNames.Short(state.GetType().Name) : "";
            b.facing = boy.facing < 0f ? -1f : 1f;
            SetPos(b, boy.pos3, boy.velocity3);
            var st = boy.status;
            if (st != null)
            {
                b.grounded = st.groundCollider != null;
                b.inWater = st.water != null;
                b.obstructed = st.isObstructed && st.obstructionPlateau != null;
                if (st.edges != null && st.edges.Length >= 2)
                {
                    if (st.edges[0] != null && st.edges[0].plateau != null) b.edgeLeft = st.edges[0].distanceToBoy;
                    if (st.edges[1] != null && st.edges[1].plateau != null) b.edgeRight = st.edges[1].distanceToBoy;
                }
            }
            var swim = state as BoySwimState;
            if (swim != null) b.atSurface = swim.atSurface;
            b.holding = BoyUtils.GetCurrentGrabBody(boy) != null;
            try { var bi = boy.input; if (bi != null) b.extraDeadZone = bi.GetExtraDeadZone(); } catch (Exception) { }
        }

        static void SetPos(ActBody b, Vector3 p, Vector3 v)
        {
            b.x = p.x; b.y = p.y; b.z = p.z; b.vx = v.x; b.vy = v.y;
        }

        static Submarine Sub(ActScope s)
        {
            if (s.submarine != null) return s.submarine;
            if (s.subLooked) return null;
            s.subLooked = true;
            s.submarine = (Submarine)UnityEngine.Object.FindObjectOfType(typeof(Submarine));
            return s.submarine;
        }

        static Boy Dude(ActScope s, long step)
        {
            if (s.dude != null && s.dude.isMindControlled) return s.dude;
            s.dude = null;
            if (s.dudeLookStep != int.MinValue && step - s.dudeLookStep < 30) return null;
            s.dudeLookStep = (int)step;
            s.dude = FindDude();
            return s.dude;
        }

        internal static Boy FindDude()
        {
            foreach (var o in UnityEngine.Object.FindObjectsOfType(typeof(MindHatReceiver)))
            {
                var r = o as MindHatReceiver;
                if (r == null || !r.ControlIsActive()) continue;
                var d = r.GetBoyControlledByHat();
                if (d != null && d.isCharacterActive) return d;
            }
            return null;
        }

        // ---------------------------------------------------------------- devices (interact lever / valve)
        public static void AttachDevice(ActScope s, GameObject go)
        {
            if (go == null) return;
            var c = FindDevice(go.GetComponents<MonoBehaviour>()) ?? FindDevice(go.GetComponentsInParent<MonoBehaviour>()) ?? FindDevice(go.GetComponentsInChildren<MonoBehaviour>());
            if (c == null) return;
            s.device = c;
            string kind = InteractMacros.DeviceAxis(c.GetType().Name);
            if (kind == "lever") kind = Horizontal(c) ? "lever_h" : "lever_v";
            s.deviceKind = kind;
            float rx = c.transform.right.x;
            s.deviceFace = Math.Abs(rx) < 1e-3f ? 0 : rx > 0f ? 1 : -1;
        }

        static Component FindDevice(MonoBehaviour[] list)
        {
            if (list == null) return null;
            foreach (var m in list) if (m != null && InteractMacros.DeviceAxis(m.GetType().Name) != "") return m;
            return null;
        }

        static bool Horizontal(Component c)
        {
            var l = c as Lever;
            if (l != null) return l.horizontalLever;
            var la = c as LeverAnalog;
            if (la != null) return la.horizontalLever;
            var lw = c as LeverAnalogWater;
            if (lw != null) return lw.horizontalLever;
            return true;
        }

        public static int LeverDir(ActScope s)
        {
            var l = s.device as Lever;
            return l != null ? l.dir : 0;
        }

        public static float ValveAngle(ActScope s)
        {
            var v = s.device as SwitchValve;
            if (v == null || v.physicsHandle == null) return -90f;
            var d = -v.physicsHandle.transform.up;
            return Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        }

        static void ReadDevice(ActBody b, ActScope s)
        {
            var c = s.device;
            if (c == null) return;
            var t = c.GetType();
            FieldInfo f;
            if (!stateFields.TryGetValue(t, out f)) { f = t.GetField("state", Inst) ?? t.GetField("mState", Inst); stateFields[t] = f; }
            if (f != null)
            {
                var v = f.GetValue(c);
                b.device = v != null ? v.ToString() : "";
                if (b.device != "" && (s.devicesSeen.Count == 0 || s.devicesSeen[s.devicesSeen.Count - 1] != b.device) && s.devicesSeen.Count < 24) s.devicesSeen.Add(b.device);
            }
            var valve = c as SwitchValve;
            if (valve != null) { b.deviceValue = valve.rotationPercentage; return; }
            FieldInfo vf;
            if (!valueFields.TryGetValue(t, out vf)) { vf = t.GetField("currentHandleFraction", Inst); valueFields[t] = vf; }
            if (vf != null && vf.FieldType == typeof(float)) b.deviceValue = (float)vf.GetValue(c);
        }

        // ---------------------------------------------------------------- watch (act watch ids)
        public static void Watch(ActScope s, List<string> specs)
        {
            foreach (var spec in specs)
            {
                var go = Interact.Resolve(spec);
                s.watchGo.Add(go);
                var w = WatchOf(go);
                w.id = spec;
                s.watchState.Add(w);
            }
        }

        static ActWatch WatchOf(GameObject go)
        {
            var w = new ActWatch();
            if (go == null) return w;
            w.exists = true;
            w.active = go.activeInHierarchy;
            var p = go.transform.position;
            w.x = p.x; w.y = p.y; w.z = p.z;
            w.angle = go.transform.eulerAngles.z;
            var fsm = go.GetComponent<PlayMakerFSM>();
            if (fsm != null) { try { w.fsm = fsm.ActiveStateName ?? ""; } catch (Exception) { } }
            return w;
        }

        public static string CheckWatch(ActScope s)
        {
            for (int i = 0; i < s.watchGo.Count; i++)
            {
                var now = WatchOf(s.watchGo[i]);
                now.id = s.watchState[i].id;
                string why = ActStops.Watch(s.watchState[i], now);
                if (why != null) return why;
            }
            return null;
        }

        // ---------------------------------------------------------------- hazards (act stop_on hazard)
        public static List<ActHazard> Hazards(ActScope s, ActBody b)
        {
            var l = new List<ActHazard>();
            if (s.hazardsBroken || b == null || !b.has) return l;
            try
            {
                foreach (var h in Perception.Hazards(new Vector3(b.x, b.y, b.z), 14f, 6))
                    l.Add(new ActHazard { id = h.id ?? "", kind = h.kind ?? "", threat = h.threat && !h.safe, eta = h.etaS, dist = h.dist });
            }
            catch (Exception) { s.hazardsBroken = true; }
            return l;
        }

        // ---------------------------------------------------------------- readiness and input mode
        // main thread: Observer.Ready when OBSERVE is built, else the game's own gates
        public static bool Ready(out string reason)
        {
            try { return Observer.Ready(out reason); }
            catch (NotImplementedException) { }
            reason = "";
            if (GameManager.IsLoading()) { reason = "loading"; return false; }
            if (GameManager.IsPaused) { reason = "paused"; return false; }
            var c = ScriptGlobals.mainCharacter;
            if (c == null || !c.isCharacterActive) { reason = "no_body"; return false; }
            if (c.isDead) { reason = "dead"; return false; }
            if (!GameInput.IsEnabled()) { reason = "input_disabled"; return false; }
            return true;
        }

        // main thread: agent mode unless the agent (or input_mode) chose mix; returns a warning or null
        public static string EnsureAgent()
        {
            var m = AgentCore.Installed && AgentCore.Mode != InputMode.Human ? AgentCore.Mode : InputMode.Agent;
            return AgentCore.Install(m);
        }

        public static GameObject ObjOf(UnityEngine.Object o)
        {
            var go = o as GameObject;
            if (go != null) return go;
            var c = o as Component;
            return c != null ? c.gameObject : null;
        }
    }
}
