using System;
using System.Collections.Generic;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // Perception.cs (owner OBSERVE, public API frozen): read-only game queries behind observe, interact, move_to and
    // the hazard tool (reports 02 sections 2.3, 5.1, 5.2; 03 section 7.2). All main thread. Pure queries only: never call
    // Find*State / GetClimbDownState / FindGrabBasedClimbup (their BoyState constructors move the boy); use
    // BoyGrabState.FindGrabCollider, BoyLadderState.FindLadder(boy, null, false), BoyRopeState.FindRope,
    // GrabSearcher.FindNearbyGrabVertices, status.edges / obstruction fields. Release pooled Plateau / ColInfo results.
    // Private game fields (SentryGun state, ChaserBase.guardMode, WaterGirl) are read by cached reflection.
    // This file: the controlled body (boy, huddle, a hat dude while the boy wears a mind-control hat, the submarine
    // while the boy drives it), its JSON, the body list, death cause and the music-clock check. PerceptionScan.cs:
    // component caches, objects, hazards, affordances. PerceptionInput.cs: input forced by the game (body.stick_forced,
    // forced_by; ForcedInput for input tracks) and the huddle's grab (body.grab, holding). FindGrabCollider / FindLadder are only used to mark "in_reach";
    // the plateaus read from status.edges / obstructionPlateau belong to the boy's status and are never acquired here,
    // so nothing pooled needs releasing. FindRope is not used (its verticalJump branch adds velocity).
    public static partial class Perception
    {
        public const string Owner = "OBSERVE";

        public static void Register()
        {
            PlayRegistry.OnTick("perception", Tick);
        }

        // the controlled character now (ScriptGlobals.mainCharacter: huddle when huddleActive, else boy); null while loading
        public static Character ActiveCharacter()
        {
            Character ch = ScriptGlobals.mainCharacter;
            if (ch == null || !ch.isCharacterActive) return null;
            return ch;
        }

        // position of the controlled body (the submarine while the boy drives it)
        public static bool BodyPos(out Vector3 pos)
        {
            var c = Controlled();
            pos = c != null ? c.pos : Vector3.zero;
            return c != null;
        }

        // the body section of observe (Observer.BodySchema); full adds anim, obstructed, edges
        public static Json.Obj Body(bool full)
        {
            var c = Controlled();
            if (c == null) return null;
            var o = new Json.Obj();
            o["kind"] = c.kind;
            o["id"] = Observer.IdFor(c.go, "b");
            if (full) o["name"] = c.go.name;
            o["x"] = PlayShared.R(c.pos.x);
            o["y"] = PlayShared.R(c.pos.y);
            o["z"] = PlayShared.R(c.pos.z);
            o["vx"] = PlayShared.R(c.vel.x);
            o["vy"] = PlayShared.R(c.vel.y);
            o["facing"] = (double)c.facing;
            o["state"] = c.state;
            if (full && c.anim != null) o["anim"] = c.anim;
            o["grounded"] = c.grounded;
            o["in_water"] = c.inWater;
            if (c.inWater) o["submerged"] = PlayShared.R(c.submerged);
            if (c.airS >= 0) o["air_s"] = PlayShared.R(c.airS, 1);
            if (c.holding != null)
            {
                o["holding"] = Observer.IdFor(c.holding, "o");
                o["holding_name"] = StateNames.Clip(c.holding.name, 40);
            }
            if (c.grab != null) o["grab"] = c.grab;
            if (ObserverLinks.GripHeld()) o["grip_held"] = true;
            if (c.inputChecked) o["stick_forced"] = c.stickForced;
            if (c.forcedBy != null) o["forced_by"] = c.forcedBy;
            o["special"] = c.special;
            o["dead"] = c.dead;
            o["controllable"] = c.controllable;
            if (full && c.boy != null)
            {
                var st = c.boy.status;
                o["obstructed"] = st.isObstructed;
                o["edge_left"] = EdgeDist(st, 0);
                o["edge_right"] = EdgeDist(st, 1);
                o["depth"] = PlayShared.R(c.boy.plane != null ? c.boy.plane.posz : c.pos.z);
            }
            return o;
        }

        public static List<BodyInfo> Bodies()
        {
            var l = new List<BodyInfo>();
            var c = Controlled();
            var boy = ScriptGlobals.boy;
            if (boy != null)
                l.Add(BodyOf("boy", boy.gameObject, boy.pos3, c != null && c.kind == "boy", boy.isCharacterActive));
            var hud = ScriptGlobals.huddle;
            if (hud != null)
                l.Add(BodyOf("huddle", hud.gameObject, hud.pos3, c != null && c.kind == "huddle", ScriptGlobals.huddleActive));
            foreach (var x in Items(typeof(MindHatReceiver)))
            {
                var rcv = x as MindHatReceiver;
                if (rcv == null) continue;
                var d = rcv.GetComponent<Boy>();
                if (d == null || d == boy) continue;
                bool ctl = c != null && c.kind == "dude" && c.boy == d;
                l.Add(BodyOf("dude", d.gameObject, d.pos3, ctl, d.isCharacterActive));
                if (l.Count >= 15) break;
            }
            foreach (var x in Items(typeof(Submarine)))
            {
                var s = x as Submarine;
                if (s == null) continue;
                l.Add(BodyOf("submarine", s.gameObject, s.pos3, c != null && c.kind == "submarine" && c.sub == s, s.isActiveAndEnabled));
                if (l.Count >= 16) break;
            }
            return l;
        }

        // interactables near center, nearest first, ids from Observer.IdFor(go, "o"), dx/dy from the body
        public static List<ObjInfo> Objects(Vector3 center, float radius, int limit)
        {
            int total;
            return ObjectsCounted(center, radius, limit, out total);
        }

        // hazards near center, most urgent first (threat, then eta_s, then distance), ids "h"
        public static List<HazardInfo> Hazards(Vector3 center, float radius, int limit)
        {
            int total;
            return HazardsCounted(center, radius, limit, out total);
        }

        // interact verbs the controlled body can try on go (Interact.Verbs); kind = one of Observer.ObjectKinds
        public static List<string> Affordances(GameObject go, out string kind)
        {
            kind = "other";
            if (go == null) return new List<string>();
            var seen = Observer.LatestObject(go);
            if (seen != null) { kind = seen.kind; return new List<string>(seen.actions); }
            var c = Controlled();
            string state;
            Component comp;
            kind = Classify(go, out comp);
            if (kind == "box" || kind == "pushable" || kind == "item")
            {
                var rb = go.GetComponentInParent<Rigidbody>();
                return BoxVerbs(kind, c, rb != null && c != null && c.holding == rb.gameObject, out state);
            }
            return Verbs(kind, comp, c, out state);
        }

        // a music-clock hazard is active near the body (ForcePushManager shockwave, lineup ZombieMonitorDetection, lab
        // elevator fall, water lock): stepping / slow motion would desync it
        public static bool MusicTimedNear(out string what)
        {
            int f = Time.frameCount;
            if (f != mtFrame)
            {
                mtFrame = f;
                mtWhat = null;
                try { mtWhat = ComputeMusicTimed(); } catch { mtWhat = null; }
            }
            what = mtWhat;
            return mtWhat != null;
        }

        // ---------------------------------------------------------------- controlled body
        internal sealed class Ctl
        {
            public string kind, state, anim, special = "none", grab, forcedBy;
            public bool stickForced, inputChecked;
            public Character ch;
            public Boy boy;
            public Huddle huddle;
            public Submarine sub;
            public GameObject go, holding;
            public Vector3 pos, vel;
            public int facing = 1;
            public bool grounded, inWater, dead, controllable, scripted;
            public float submerged, airS = -1f;
        }

        static int ctlFrame = -1, ctlEpoch = -1;
        static Ctl ctlCache;
        static int mtFrame = -1;
        static string mtWhat;

        internal static Ctl Controlled()
        {
            int f = Time.frameCount;
            if (f == ctlFrame && ctlEpoch == Observer.Epoch && (ctlCache == null || ctlCache.go != null)) return ctlCache;
            ctlFrame = f;
            ctlEpoch = Observer.Epoch;
            ctlCache = BuildCtl();
            return ctlCache;
        }

        internal static void ForgetBody() { ctlFrame = -1; ctlCache = null; }

        static Ctl BuildCtl()
        {
            var ch = ActiveCharacter();
            if (ch == null) return null;
            var c = new Ctl { ch = ch, go = ch.gameObject, dead = ch.isDead };
            var hud = ch as Huddle;
            var boy = ch as Boy;
            if (hud != null) FillHuddle(c, hud);
            else if (boy != null)
            {
                Submarine sub = null;
                if (boy.specialBoyMode == Boy.SpecialBoyMode.InsideSub) sub = BoySubmarine();
                Boy dude = boy.isMindControlling ? ControlledDude() : null;
                if (sub != null) FillSub(c, boy, sub);
                else if (dude != null) { FillBoy(c, dude); c.kind = "dude"; c.go = dude.gameObject; c.dead = boy.isDead || dude.isDead; c.special = "MindControlling"; }
                else FillBoy(c, boy);
            }
            else
            {
                c.kind = "boy";
                c.pos = ch.pos3;
                c.vel = ch.velocity3;
                c.state = StateNames.Short(ch.GetType().Name);
            }
            try { c.controllable = !c.dead && !c.scripted && GameManager.IsPlaying() && GameInput.IsEnabled() && !Observer.MenuOpen(); }
            catch { c.controllable = false; }
            return c;
        }

        static void FillBoy(Ctl c, Boy boy)
        {
            c.kind = "boy";
            c.boy = boy;
            c.go = boy.gameObject;
            c.pos = boy.pos3;
            var v = boy.velocity;
            c.vel = new Vector3(v.x, v.y, 0f);
            c.facing = boy.facing < 0f ? -1 : 1;
            var bs = boy.state;
            c.state = bs != null ? StateNames.Short(bs.GetType().Name) : "";
            c.scripted = bs is BoyCustomAnimState;
            c.anim = AnimOf(boy);
            var st = boy.status;
            c.grounded = st.groundPlateau != null;
            c.inWater = st.water != null;
            c.submerged = st.submerged;
            if (st.water != null && boy.divingTimeElapsed > 0f && !BoyUtils.HasInfiniteAir(boy))
                c.airS = Mathf.Max(0f, st.water.maxDivingTime - boy.divingTimeElapsed);
            var rb = BoyUtils.GetCurrentGrabBody(boy);
            if (rb != null) c.holding = rb.gameObject;
            if (boy == ScriptGlobals.boy)
            {
                c.inputChecked = true;
                FillForced(c, boy);
            }
            if (boy.specialBoyMode != Boy.SpecialBoyMode.None) c.special = boy.specialBoyMode.ToString();
            else if (boy.isMindControlling) c.special = "MindControlling";
            else if (boy.isMindControlled) c.special = "MindControlled";
        }

        static void FillHuddle(Ctl c, Huddle hud)
        {
            c.kind = "huddle";
            c.huddle = hud;
            c.pos = hud.pos3;
            c.vel = hud.velocity3;
            c.facing = c.vel.x < -0.05f ? -1 : 1;
            var hs = hud.state;
            c.state = hs != null ? StateNames.Short(hs.GetType().Name) : "";
            HuddleStatus st = null;
            try { st = hud.status; } catch { st = null; }
            if (st != null)
            {
                c.grounded = st.hasFoothold;
                c.inWater = st.water != null;
                c.submerged = Mathf.Clamp01(st.waterFraction);
            }
            FillGrab(c, hud);
            c.inputChecked = true;
            FillForced(c, hud);
            if (hud.isMindControlled) c.special = "MindControlled";
        }

        static void FillSub(Ctl c, Boy boy, Submarine sub)
        {
            c.kind = "submarine";
            c.boy = null;
            c.sub = sub;
            c.go = sub.gameObject;
            c.pos = sub.pos3;
            c.vel = sub.velocity3;
            c.facing = c.vel.x < -0.05f ? -1 : 1;
            c.state = "Drive";
            c.inWater = true;
            c.submerged = 1f;
            c.special = "InsideSub";
        }

        static string AnimOf(Boy boy)
        {
            try
            {
                var vb = boy.visualBoy;
                if (vb == null || vb.state == null || vb.state.logicAnim == null || vb.state.logicAnim.anim == null) return null;
                var an = vb.state.logicAnim.anim;
                var uo = an as UnityEngine.Object;
                string n = uo != null ? uo.name : Convert.ToString(an);
                return StateNames.Short(vb.state.GetType().Name) + ":" + n;
            }
            catch { return null; }
        }

        static double EdgeDist(Boy.Status st, int side)
        {
            if (st.edges == null || st.edges.Length <= side || st.edges[side] == null || st.edges[side].plateau == null) return -1;
            return PlayShared.R(Mathf.Min(st.edges[side].distanceToBoy, 999f));
        }

        static Submarine BoySubmarine()
        {
            foreach (var x in Items(typeof(SubmarineBoyInteraction)))
            {
                var sbi = x as SubmarineBoyInteraction;
                if (sbi == null || sbi.submarine == null) continue;
                if (sbi.GetBoyIsInsideSubmarine()) return sbi.submarine;
            }
            foreach (var x in Items(typeof(Submarine)))
            {
                var s = x as Submarine;
                if (s != null && s.isActiveAndEnabled) return s;
            }
            return null;
        }

        static Boy ControlledDude()
        {
            foreach (var x in Items(typeof(MindHatReceiver)))
            {
                var rcv = x as MindHatReceiver;
                if (rcv == null || !rcv.ControlIsActive()) continue;
                var d = rcv.GetBoyControlledByHat();
                if (d != null && d != ScriptGlobals.boy && d.isCharacterActive) return d;
            }
            return null;
        }

        static BodyInfo BodyOf(string kind, GameObject go, Vector3 pos, bool controlled, bool active)
        {
            return new BodyInfo { id = Observer.IdFor(go, "b"), kind = kind, name = go.name, go = go, pos = pos, controlled = controlled, active = active };
        }

        // ---------------------------------------------------------------- death cause, music clock
        static long fallThreatStep = -100000;

        internal static string DeathCause()
        {
            var ch = ScriptGlobals.mainCharacter;
            var boy = ch as Boy;
            if (boy == null) return ch != null ? "huddle" : "unknown";
            if (boy.drowningDeath) return "drowned";
            if (LogicManager.stepCount - fallThreatStep < 90) return "fall";
            string near = NearestThreat(boy.pos3);
            if (near != null) return near;
            var bs = boy.state;
            string s = bs != null ? StateNames.Short(bs.GetType().Name) : "?";
            string an = AnimOf(boy);
            return "state " + (an ?? s);
        }

        static void TrackFall()
        {
            var c = Controlled();
            if (c == null || c.boy == null || c.grounded || c.dead) return;
            float lim = c.boy.properties != null && c.boy.properties.death != null ? c.boy.properties.death.fallSpeedLimit : 12.4f;
            if (-c.vel.y > lim * 0.85f) fallThreatStep = LogicManager.stepCount;
        }

        static string ComputeMusicTimed()
        {
            var fpm = LevelGlobals.forcePushManager;
            if (fpm != null && fpm.isActiveAndEnabled) return "shockwave (mines, ForcePushManager)";
            Vector3 p;
            bool hb = BodyPos(out p);
            var wft = LevelGlobals.waterFlushTunnel;
            if (wft != null && wft.isActiveAndEnabled && (!hb || (wft.transform.position - p).magnitude < 120f)) return "lab water lock (WaterFlushTunnel)";
            EnsureFresh(typeof(ElevatorFall));
            foreach (var x in Items(typeof(ElevatorFall)))
            {
                var ef = x as ElevatorFall;
                if (ef != null && ef.isActiveAndEnabled && (!hb || (ef.transform.position - p).magnitude < 80f)) return "lab elevator fall (ElevatorFall)";
            }
            EnsureFresh(typeof(ZombieMonitorDetection));
            foreach (var x in Items(typeof(ZombieMonitorDetection)))
            {
                var z = x as ZombieMonitorDetection;
                if (z != null && z.isActiveAndEnabled && (!hb || (z.transform.position - p).magnitude < 100f)) return "lineup (ZombieMonitorDetection)";
            }
            return null;
        }

        static void Tick()
        {
            if (!ChangeLog.Active) return;
            try
            {
                if (GameManager.IsLoading()) return;
                TrackFall();
                TickRefresh();
            }
            catch (Exception e)
            {
                if (!tickFailed) { tickFailed = true; DevLog.Error("[play] perception tick", e); }
            }
        }
        static bool tickFailed;
    }
}
