using System;
using System.Collections.Generic;
using System.Globalization;

namespace InsideDev.Play
{
    // InteractMacros.cs (owner INPUT): Unity-free verb macros of interact, compiled into the offline tests too. A macro is
    // a list of phases; each phase feeds its frames in order (the last one repeats) for at most maxSteps logic steps and
    // ends early when done holds (after minSteps) or fail holds. skipUnless skips a phase that does not apply to the state
    // the previous phases left. needDone: running out of steps without done fails the verb with failText. Thresholds
    // come from the decompiled game (report 02 sections 1.2, 2.2, 4):
    //   GameInput: jump = rising edge kept 0.25 s; on a plateau action held = grab. BoyInput: deadzone 0.3, looseDir
    //   axis cone 63 deg, strictDir needs 0.6, drop = jump (Digital: action) + looseDir.y < -0.5, extra deadzone 0.6 after
    //   a strong deflection (so macros use magnitude 1 on one axis). BoyGrabPlateauState: climb looseDir.y > 0.5 after
    //   0.15 s, let go strictDir.y < -0.5 after 0.2 s. BoyRunState: ladder needs looseDir.y > 0.5 and |stick| > 0.9,
    //   climb down stick.y < -0.5 within 0.8 m of an edge. BoyLadderState: drop after 0.35 s, side jump |x| > 0.5 after
    //   0.5 s. BoyRopeState: jump with |x| > 0.5 and stick.y < 0.8. MindHat: stick.y < -0.8 for 1/3 s, then action.
    //   SubmarineChargeBoost: hold rawJump (Digital: action), release after at least 0.5 s with a direction.
    //   SubmarineBoyInteraction: exit on rawAction after a release (Digital: stick up flick), at the surface.
    //   Lever: grab, wait 0.21 s, stick against Lever.dir (|v| > 0.3). SwitchValve: grab, wait 0.15 s, the handle
    //   follows a turning stick.
    //   Turning (BoyStick, FP/BoyInput.cs:58-131): after a stick with |stick|^2 > 0.7 the boy keeps an extra deadzone of
    //   0.6 toward that side that decays 0.05 per step; the opposite direction then needs a projection above 0.3 + it.
    //   The turn phase feeds the 0.4 walk stick (no run) until the game's own rule lets it register, then 6 steps.
    //   Devices (Device subclasses, AC/): grab = action held facing the handle (Device.GrabCheck); then the stick
    //   along the device's axis with action held (Hatch y up, VentHatch / PullOffBoards / BoardBreak x away,
    //   DoorBoarded y up, UnderwaterLock y down, ...) until a done state (Success, Complete, PullOff ...).
    //   SwitchButtonNew: a rising rawAction within reach facing it (AC/SwitchButtonNew.cs:773), held keeps it in.
    //   OpenElevatorDoors(2): action held facing transform.right (InitialState), stick y > 0 lifts (LiftDoorState)
    //   until OpenDoor; letting go drops the door. SubmarineClimbController.TestClimbIn: on top of the sub within
    //   0.7 m of the hatch, looseDir straight down. MindHat.HangOnTriggerEnter: the boy's body enters the hat's
    //   trigger (walk or jump into it). HuddleGrabState.CreateThrow: the release of action with |stick.x| > 0.5 (the
    //   last such stick) or strictDir.y > 0.5 throws the held object.
    internal enum MacroCondKind
    {
        None, StateIn, StateNotIn, Holding, NotHolding, NotMindControlling, NotInSub, AtSurface, SubOnSurface, Boosting,
        DeviceGrabbed, DeviceMoved, RunGrounded, LeftWater, Any, MindControlling, InSub, DeviceIn
    }

    internal sealed class MacroCond
    {
        public MacroCondKind kind;
        public string[] states = new string[0];
        public MacroCond[] any = new MacroCond[0];

        public static MacroCond Of(MacroCondKind k) { return new MacroCond { kind = k }; }
        public static MacroCond In(params string[] s) { return new MacroCond { kind = MacroCondKind.StateIn, states = s }; }
        public static MacroCond NotIn(params string[] s) { return new MacroCond { kind = MacroCondKind.StateNotIn, states = s }; }
        public static MacroCond Or(params MacroCond[] c) { return new MacroCond { kind = MacroCondKind.Any, any = c }; }
        public static MacroCond DeviceIn(params string[] s) { return new MacroCond { kind = MacroCondKind.DeviceIn, states = s }; }

        public bool Eval(ActBody now, ActBody start)
        {
            if (now == null) return false;
            switch (kind)
            {
                case MacroCondKind.None: return false;
                case MacroCondKind.StateIn: return now.has && Array.IndexOf(states, now.state) >= 0;
                case MacroCondKind.StateNotIn: return now.has && Array.IndexOf(states, now.state) < 0;
                case MacroCondKind.Holding: return now.holding;
                case MacroCondKind.NotHolding: return !now.holding;
                case MacroCondKind.NotMindControlling: return !now.mindControlling;
                case MacroCondKind.NotInSub: return now.special != "InsideSub";
                case MacroCondKind.AtSurface: return now.atSurface || !now.inWater;
                case MacroCondKind.SubOnSurface: return now.subOnSurface;
                case MacroCondKind.Boosting: return now.subBoosting;
                case MacroCondKind.DeviceGrabbed: return now.device != "" && now.device != "None" && now.device != "Idle";
                case MacroCondKind.DeviceMoved:
                    if (now.device != "" && now.device != "None" && now.device != "Idle" && now.device != "Grab") return true;
                    return start != null && !float.IsNaN(now.deviceValue) && !float.IsNaN(start.deviceValue) && Math.Abs(now.deviceValue - start.deviceValue) > 0.02f;
                case MacroCondKind.RunGrounded: return now.has && now.state == "Run" && now.grounded;
                case MacroCondKind.LeftWater: return !now.inWater;
                case MacroCondKind.MindControlling: return now.mindControlling;
                case MacroCondKind.InSub: return now.special == "InsideSub";
                case MacroCondKind.DeviceIn: return now.device != "" && Array.IndexOf(states, now.device) >= 0;
                case MacroCondKind.Any:
                    foreach (var c in any) if (c.Eval(now, start)) return true;
                    return false;
            }
            return false;
        }

        public override string ToString()
        {
            if (kind == MacroCondKind.StateIn) return "state in " + string.Join("|", states);
            if (kind == MacroCondKind.StateNotIn) return "state not in " + string.Join("|", states);
            if (kind == MacroCondKind.DeviceIn) return "device in " + string.Join("|", states);
            if (kind == MacroCondKind.Any)
            {
                var p = new List<string>();
                foreach (var c in any) p.Add(c.ToString());
                return string.Join(" or ", p.ToArray());
            }
            return kind.ToString();
        }
    }

    internal sealed class MacroPhase
    {
        public string name;
        public readonly List<InputFrame> frames = new List<InputFrame>();
        public int minSteps, maxSteps;
        public MacroCond done, fail, skipUnless;
        public bool needDone;
        public string failText = "";
        public string fallback = "";

        public int FrameSteps
        {
            get
            {
                int n = 0;
                foreach (var f in frames) n += f.steps;
                return n;
            }
        }
    }

    internal sealed class MacroEnv
    {
        public string verb = "", dir = "toward";
        public float seconds = 1f;
        public string kind = "boy", state = "Run", special = "None";
        public bool grounded = true, inWater, atSurface, holding, mindControlling, digital, obstructed, subOnSurface, dead;
        public float facing = 1f;
        public bool hasTarget;
        public float targetDx, targetDy;
        public string device = "";
        public int leverDir, deviceFace;
        public float valveAngle = -90f;
        public float edgeLeft = 1e6f, edgeRight = 1e6f;
        // BoyInput.GetExtraDeadZone of the controlled boy (signed: + after a strong right stick)
        public float extraDeadZone;

        public int TargetSide { get { return !hasTarget || Math.Abs(targetDx) < 0.05f ? 0 : (targetDx > 0 ? 1 : -1); } }
        public int Facing { get { return facing < 0 ? -1 : 1; } }
    }

    internal sealed class InteractMacro
    {
        public string verb;
        public readonly List<MacroPhase> phases = new List<MacroPhase>();
        public string notAvailable;
        public bool latchOnSuccess;
        public InputFrame latch;
        public string note = "";

        public int MaxSteps
        {
            get
            {
                int n = 0;
                foreach (var p in phases) n += p.maxSteps;
                return n;
            }
        }
    }

    internal static class InteractMacros
    {
        public const int MaxTotalSteps = 1800;
        public const int GrabSteps = 30, LeverWaitSteps = 15, ValveWaitSteps = 12, HatPullSteps = 24, SubChargeMinSteps = 33;
        public const float DetachStickY = -1f, ClimbStickY = 1f, DropStickY = -1f, ValveTurnsPerSecond = 0.8f;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static readonly string[] TargetVerbs = { "grab", "push", "pull", "lever", "valve", "button", "lift", "sub_enter", "hat_wear" };
        public static readonly string[] HuddleVerbs = { "grab", "release", "push", "pull", "throw" };
        public static readonly string[] SubVerbs = { "sub_boost", "sub_exit" };
        // verbs that keep a grab grip (AgentCore.Grip): they hold action themselves; every other verb lets go first
        public static readonly string[] GripVerbs = { "grab", "push", "pull" };

        static readonly string[] DeviceDone = { "Success", "Succes", "AboveSuccess", "BelowSuccess", "Complete", "AfterPulledOff", "PullOff", "Open", "Opened", "FullyOpened", "Fallen", "Raised" };
        static readonly string[] ButtonDown = { "Push", "RunningPush", "Toggle", "Jump", "Rope" };
        static readonly string[] LiftGrabbed = { "WaitForGrabFinish", "LiftDoor" };
        static readonly string[] LiftOpen = { "OpenDoor", "Complete", "OpeningTheDoor", "DoorOpening" };

        public static bool WantsTarget(string verb) { return Array.IndexOf(TargetVerbs, verb) >= 0 || verb == "climb" || verb == "climb_down" || verb == "ladder_up" || verb == "ladder_down"; }

        // metres: how close the body must be (horizontal distance to the target's collider) before the macro runs
        public static float Reach(string verb)
        {
            switch (verb)
            {
                case "grab": case "push": case "pull": return 0.8f;
                case "lever": case "valve": return 1.0f;
                case "button": return 0.9f;
                case "lift": return 0.8f;
                case "sub_enter": return 0.6f;
                case "hat_wear": return 0.4f;
                case "climb": return 1.5f;
                case "climb_down": return 0.8f;
                case "ladder_up": case "ladder_down": return 0.7f;
            }
            return -1f;
        }

        // the interact device kind of a game class (ActProbe.AttachDevice): lever_h / lever_v (stick x / y), valve,
        // button, lift, dev_x / dev_y / dev_down / dev_free (grab devices: stick along x, up, down, or away from the
        // target), dev_grab (hold only), huddle_dev; "" = not a device
        public static string DeviceAxis(string typeName)
        {
            switch (typeName ?? "")
            {
                case "Lever": case "LeverAnalog": case "LeverAnalogWater": return "lever";
                case "LeverOneTime": case "SuctionLever": case "ChickenGunSwitch": return "lever_h";
                case "LeverElevatorStuck": case "SwitchTallRod": return "lever_v";
                case "SwitchValve": return "valve";
                case "SwitchButtonNew": return "button";
                case "OpenElevatorDoors": case "OpenElevatorDoors2": return "lift";
                case "Hatch": case "DoorBoarded": case "WindowInteractive": case "RopeLadder": return "dev_y";
                case "UnderwaterLock": return "dev_down";
                case "VentHatch": case "PullOffBoards": case "BoardBreak": case "AlbinoMultiPullBoards": case "MinesGate": case "SuperPillar": case "SuperLadder": return "dev_x";
                case "UnderwaterHatch": case "UnderwaterCoil": case "TorchPickup": return "dev_free";
                case "WaterStreamGrabHook": return "dev_grab";
                case "HuddleOvenGrab": case "PushCoverLogic": return "huddle_dev";
            }
            return "";
        }

        public static bool GrabDevice(string device) { return device == "dev_x" || device == "dev_y" || device == "dev_down" || device == "dev_free" || device == "dev_grab"; }

        public static int StepsOf(float seconds)
        {
            int n = (int)Math.Round(seconds * 60f);
            return Math.Max(1, Math.Min(600, n));
        }

        static InputFrame F(float x, float y, bool jump, bool action, int steps)
        {
            return new InputFrame { x = x, y = y, jump = jump, action = action, steps = steps };
        }

        static MacroPhase P(string name, int min, int max, MacroCond done, bool needDone, string failText, params InputFrame[] frames)
        {
            var p = new MacroPhase { name = name, minSteps = min, maxSteps = max, done = done, needDone = needDone, failText = failText ?? "" };
            p.frames.AddRange(frames);
            return p;
        }

        // -1 / +1 stick x for a direction; toward / away use the target side (or facing without a target); 0 for up / down
        public static int DirX(string dir, int targetSide, int facing)
        {
            int side = targetSide != 0 ? targetSide : facing;
            switch (dir)
            {
                case "left": return -1;
                case "right": return 1;
                case "toward": return side;
                case "away": return -side;
            }
            return 0;
        }

        public static void DirVec(string dir, MacroEnv e, out float x, out float y)
        {
            x = 0f; y = 0f;
            switch (dir)
            {
                case "left": x = -1f; return;
                case "right": x = 1f; return;
                case "up": y = 1f; return;
                case "down": y = -1f; return;
            }
            float sx = e.Facing, sy = 0f;
            if (e.hasTarget && (Math.Abs(e.targetDx) > 0.05f || Math.Abs(e.targetDy) > 0.05f))
            {
                float m = (float)Math.Sqrt(e.targetDx * e.targetDx + e.targetDy * e.targetDy);
                sx = e.targetDx / m; sy = e.targetDy / m;
            }
            if (dir == "away") { sx = -sx; sy = -sy; }
            x = sx; y = sy;
        }

        static InteractMacro NA(InteractMacro m, string why) { m.notAvailable = why; return m; }

        public const float TurnStick = 0.4f;
        public const int TurnSteps = 6, TurnWaitMax = 20;

        static MacroPhase Turn(MacroEnv e) { return TurnTo(e, e.TargetSide); }

        // walk-stick turn toward side; the lead-in covers the steps BoyInput's extra deadzone still hides the stick
        static MacroPhase TurnTo(MacroEnv e, int side)
        {
            if (side == 0 || side == e.Facing || e.kind == "huddle") return null;
            int k = BoyStick.StepsUntilLooseX(e.extraDeadZone, TurnStick * side, 0f, side, TurnWaitMax);
            if (k < 0) k = TurnWaitMax;
            int n = k - 1 + TurnSteps;
            return P("turn", n, n, null, false, null, F(TurnStick * side, 0f, false, false, n));
        }

        public static InteractMacro Build(MacroEnv e)
        {
            var m = new InteractMacro { verb = e.verb };
            if (e.dead) return NA(m, "the body is dead: wait_until {alive:true}");
            bool inSub = e.special == "InsideSub";
            if (inSub && Array.IndexOf(SubVerbs, e.verb) < 0) return NA(m, "the boy is driving the submarine: only sub_boost and sub_exit work (steer with act)");
            if (!inSub && Array.IndexOf(SubVerbs, e.verb) >= 0) return NA(m, "not in the submarine (state " + e.state + ")");
            if (e.kind == "huddle" && Array.IndexOf(HuddleVerbs, e.verb) < 0)
                return NA(m, "the huddle has no " + e.verb + " move: use act (action held = grab, jump held = leap, the huddle climbs by itself)");
            if (e.verb == "hat_detach" && !e.mindControlling) return NA(m, "not wearing a mind-control hat");
            if (e.mindControlling && e.verb != "hat_detach" && e.kind != "dude") return NA(m, "the boy is in a hat but no dude is under control: act steers the dude, hat_detach leaves the hat");
            switch (e.verb)
            {
                case "grab": return Grab(m, e);
                case "release": return ReleaseVerb(m, e);
                case "push": return PushPull(m, e, true);
                case "pull": return PushPull(m, e, false);
                case "climb": return Climb(m, e);
                case "climb_down": return ClimbDown(m, e);
                case "drop": return Drop(m, e);
                case "ladder_up": return LadderUpDown(m, e, true);
                case "ladder_down": return LadderUpDown(m, e, false);
                case "ladder_off": return LadderOff(m, e);
                case "rope_jump": return RopeJump(m, e);
                case "swim_down": return Swim(m, e, false);
                case "swim_up": return Swim(m, e, true);
                case "surface": return Surface(m, e);
                case "hat_detach": return HatDetach(m, e);
                case "sub_boost": return SubBoost(m, e);
                case "sub_exit": return SubExit(m, e);
                case "lever": return Lever(m, e);
                case "valve": return Valve(m, e);
                case "button": return ButtonPress(m, e);
                case "lift": return Lift(m, e);
                case "sub_enter": return SubEnter(m, e);
                case "hat_wear": return HatWear(m, e);
                case "throw": return Throw(m, e);
            }
            return NA(m, "unknown verb " + e.verb);
        }

        static MacroCond GrabbedCond() { return MacroCond.Or(MacroCond.Of(MacroCondKind.Holding), MacroCond.In("Grab", "CustomAnim"), MacroCond.Of(MacroCondKind.DeviceGrabbed)); }

        static InteractMacro Grab(InteractMacro m, MacroEnv e)
        {
            if (e.kind == "huddle")
            {
                if (!e.holding) m.phases.Add(P("grab", 1, GrabSteps, MacroCond.Of(MacroCondKind.Holding), true, "the huddle grabbed nothing: walk it into the object first", F(0f, 0f, false, true, 1)));
                m.latchOnSuccess = true;
                m.latch = F(0f, 0f, false, true, 1);
                m.note = "holding: action stays pressed until interact release or the next act";
                return m;
            }
            if (e.holding || e.state == "Grab")
            {
                m.latchOnSuccess = true;
                m.latch = F(0f, 0f, false, true, 1);
                m.note = "already holding: action stays pressed";
                return m;
            }
            if (e.state != "Run" && e.state != "Swim") return NA(m, "grab works standing or swimming (state " + e.state + ")");
            var t = Turn(e);
            if (t != null) m.phases.Add(t);
            m.phases.Add(P("grab", 1, GrabSteps, GrabbedCond(), true, "nothing to grab in reach: stand next to it facing it (move_to the target first)", F(0f, 0f, false, true, 1)));
            m.latchOnSuccess = true;
            m.latch = F(0f, 0f, false, true, 1);
            m.note = "holding: action stays pressed until interact release or the next act";
            return m;
        }

        static InteractMacro ReleaseVerb(InteractMacro m, MacroEnv e)
        {
            if (e.kind != "huddle")
            {
                if (e.state == "GrabPlateau") return NA(m, "hanging on a ledge: use drop to let go or climb to climb up");
                if (e.state == "Ladder" || e.state == "Rope") return NA(m, "on a " + e.state.ToLowerInvariant() + ": use ladder_off, rope_jump or drop");
                if (!e.holding && e.state != "Grab" && e.state != "CustomAnim") return NA(m, "not holding anything (state " + e.state + ")");
            }
            else if (!e.holding) return NA(m, "the huddle is not holding anything");
            var done = e.kind == "huddle" ? MacroCond.Of(MacroCondKind.NotHolding) : e.state == "CustomAnim" ? MacroCond.NotIn("CustomAnim") : e.state == "Grab" ? MacroCond.NotIn("Grab") : MacroCond.Of(MacroCondKind.NotHolding);
            m.phases.Add(P("let go", 1, 40, done, true, "still holding after letting go of the button", F(0f, 0f, false, false, 1)));
            return m;
        }

        static InteractMacro PushPull(InteractMacro m, MacroEnv e, bool push)
        {
            if (GrabDevice(e.device) && e.kind != "huddle") return DeviceMove(m, e, push);
            if (e.dir == "up" || e.dir == "down") return NA(m, (push ? "push" : "pull") + " moves sideways: dir left, right, toward or away");
            int side = e.TargetSide != 0 ? e.TargetSide : e.Facing;
            int sx;
            if (e.dir == "left") sx = -1;
            else if (e.dir == "right") sx = 1;
            else if (push) sx = e.dir == "away" ? -side : side;
            else sx = -side;
            int n = StepsOf(e.seconds);
            if (e.kind == "huddle")
            {
                if (!e.holding) m.phases.Add(P("grab", 1, GrabSteps, MacroCond.Of(MacroCondKind.Holding), true, "the huddle grabbed nothing", F(0f, 0f, false, true, 1)));
                var hp = P(push ? "push" : "pull", n, n, null, false, null, F(sx, 0f, false, true, n));
                hp.fail = MacroCond.Of(MacroCondKind.NotHolding);
                hp.failText = "the huddle lost its grip";
                m.phases.Add(hp);
                return m;
            }
            if (!e.holding && e.state != "Grab")
            {
                if (e.state != "Run" && e.state != "Swim") return NA(m, (push ? "push" : "pull") + " works standing next to the object (state " + e.state + ")");
                var t = Turn(e);
                if (t != null) m.phases.Add(t);
                m.phases.Add(P("grab", 1, GrabSteps, MacroCond.Or(MacroCond.Of(MacroCondKind.Holding), MacroCond.In("Grab")), true, "nothing to grab in reach: stand next to it facing it (move_to the target first)", F(0f, 0f, false, true, 1)));
            }
            var p = P(push ? "push" : "pull", n, n, null, false, null, F(sx, 0f, false, true, n));
            p.fail = MacroCond.Of(MacroCondKind.NotHolding);
            p.failText = "lost the grip (too heavy, blocked, or the object is not movable this way)";
            m.phases.Add(p);
            return m;
        }

        static void ClimbFinish(InteractMacro m)
        {
            var up = P("climb up", 1, 60, MacroCond.NotIn("GrabPlateau"), true, "cannot climb up here (no room above): drop, or try another ledge", F(0f, ClimbStickY, false, false, 1));
            up.skipUnless = MacroCond.In("GrabPlateau");
            m.phases.Add(up);
            var fin = P("finish", 0, 120, MacroCond.Of(MacroCondKind.RunGrounded), false, null, F(0f, 0f, false, false, 1));
            fin.skipUnless = MacroCond.In("ReachPlateau", "ClimbDown");
            m.phases.Add(fin);
        }

        static InteractMacro Climb(InteractMacro m, MacroEnv e)
        {
            if (e.state == "Ladder") return LadderUpDown(m, e, true);
            if (e.state == "Rope")
            {
                int n = StepsOf(e.seconds);
                var r = P("climb rope", n, n, null, false, null, F(0f, ClimbStickY, false, false, n));
                r.fail = MacroCond.NotIn("Rope");
                r.failText = "fell off the rope";
                m.phases.Add(r);
                return m;
            }
            if (e.state == "GrabPlateau" || e.state == "ReachPlateau")
            {
                ClimbFinish(m);
                return m;
            }
            if (e.state == "Jump")
            {
                m.phases.Add(P("catch", 1, 45, MacroCond.In("GrabPlateau", "ReachPlateau", "Ladder", "Rope"), true, "landed without catching a ledge", F(DirX(e.dir, e.TargetSide, e.Facing), 0f, false, false, 1)));
                ClimbFinish(m);
                return m;
            }
            if (e.state != "Run") return NA(m, "climb works standing, hanging on a ledge, on a ladder or rope (state " + e.state + ")");
            if (e.obstructed && (e.TargetSide == 0 || e.TargetSide == e.Facing))
            {
                m.phases.Add(P("push into", 1, 30, MacroCond.In("ReachPlateau", "GrabPlateau"), false, null, F(e.Facing, 0f, false, false, 1)));
            }
            int jx = 0;
            if (e.hasTarget && Math.Abs(e.targetDx) > 0.4f) jx = e.TargetSide;
            else if (e.dir == "left" || e.dir == "right") jx = DirX(e.dir, 0, e.Facing);
            var jump = P("jump", 1, 70, MacroCond.In("GrabPlateau", "ReachPlateau", "Ladder", "Rope"), true, "jumped but caught no ledge: stand right under it or next to it (move_to), or it is too high",
                F(jx, 0f, false, false, 1), F(jx, 0f, true, false, 18), F(jx, 0f, false, false, 1));
            jump.skipUnless = MacroCond.In("Run");
            m.phases.Add(jump);
            ClimbFinish(m);
            return m;
        }

        static InteractMacro ClimbDown(InteractMacro m, MacroEnv e)
        {
            if (e.state == "GrabPlateau") { m.note = "already hanging from the ledge"; return m; }
            if (e.state == "Ladder") return LadderUpDown(m, e, false);
            if (e.state != "Run") return NA(m, "climb_down works standing at a ledge edge (state " + e.state + ")");
            if (Math.Min(e.edgeLeft, e.edgeRight) > 0.8f)
                return NA(m, "no ledge edge within 0.8 m (left " + Dist(e.edgeLeft) + ", right " + Dist(e.edgeRight) + "): move_to the edge first");
            m.phases.Add(P("down", 1, 40, MacroCond.In("ClimbDown", "GrabPlateau", "Ladder"), true, "did not climb down: this edge cannot be hung from (or it is a wall top the boy steps off)", F(0f, DropStickY, false, false, 1)));
            var settle = P("settle", 0, 60, MacroCond.In("GrabPlateau", "Ladder"), false, null, F(0f, 0f, false, false, 1));
            settle.skipUnless = MacroCond.In("ClimbDown");
            m.phases.Add(settle);
            return m;
        }

        static string Dist(float d) { return d >= 1e5f ? "none" : d.ToString("0.##", IC) + " m"; }

        static InputFrame DropFrame(MacroEnv e) { return F(0f, DropStickY, !e.digital, e.digital, 1); }

        static InteractMacro Drop(InteractMacro m, MacroEnv e)
        {
            if (e.state == "GrabPlateau")
            {
                m.phases.Add(P("let go", 1, 40, MacroCond.NotIn("GrabPlateau"), true, "still hanging", F(0f, DropStickY, false, false, 1)));
                return m;
            }
            if (e.state == "Ladder" || e.state == "Rope")
            {
                m.phases.Add(P("drop", 1, 45, MacroCond.NotIn(e.state), true, "still on the " + e.state.ToLowerInvariant(), F(0f, DropStickY, false, false, 2), DropFrame(e)));
                return m;
            }
            if (e.holding || e.state == "Grab") return ReleaseVerb(m, e);
            return NA(m, "nothing to drop from (state " + e.state + ")");
        }

        static InteractMacro LadderUpDown(InteractMacro m, MacroEnv e, bool up)
        {
            float sy = up ? 1f : -1f;
            int n = StepsOf(e.seconds);
            if (e.state == "Run")
            {
                m.phases.Add(P("mount", 1, up ? 30 : 40, MacroCond.In("Ladder"), true,
                    up ? "no ladder here: stand in front of it (move_to the ladder first)" : "no ladder going down here: stand at its top edge",
                    F(0f, sy, false, false, 1)));
            }
            else if (e.state != "Ladder") return NA(m, "not on a ladder and not standing at one (state " + e.state + ")");
            m.phases.Add(P(up ? "climb up" : "climb down", 1, n, MacroCond.NotIn("Ladder"), false, null, F(0f, sy, false, false, n)));
            var fin = P("finish", 0, 90, MacroCond.Of(MacroCondKind.RunGrounded), false, null, F(0f, 0f, false, false, 1));
            fin.skipUnless = MacroCond.In("ReachPlateau");
            m.phases.Add(fin);
            return m;
        }

        static InteractMacro LadderOff(InteractMacro m, MacroEnv e)
        {
            if (e.state != "Ladder") return NA(m, "not on a ladder (state " + e.state + ")");
            int sx = 0;
            if (e.dir == "left" || e.dir == "right") sx = DirX(e.dir, 0, e.Facing);
            else if ((e.dir == "toward" || e.dir == "away") && e.TargetSide != 0) sx = DirX(e.dir, e.TargetSide, e.Facing);
            if (sx == 0)
            {
                m.phases.Add(P("drop", 1, 45, MacroCond.NotIn("Ladder"), true, "still on the ladder", F(0f, DropStickY, false, false, 2), DropFrame(e)));
                return m;
            }
            m.phases.Add(P("jump off", 1, 50, MacroCond.NotIn("Ladder"), true, "did not jump off (the boy must hold the ladder 0.5 s first)", F(sx, 0f, false, false, 4), F(sx, 0f, true, false, 1)));
            var fly = P("fly", 0, 90, MacroCond.NotIn("Jump"), false, null, F(sx, 0f, true, false, 12), F(sx, 0f, false, false, 1));
            m.phases.Add(fly);
            return m;
        }

        static InteractMacro RopeJump(InteractMacro m, MacroEnv e)
        {
            if (e.state != "Rope") return NA(m, "not on a rope (state " + e.state + ")");
            if (e.dir == "up" || e.dir == "down") return NA(m, "rope_jump jumps sideways: dir left, right, toward or away");
            int sx = DirX(e.dir, e.TargetSide, e.Facing);
            m.phases.Add(P("jump", 1, 50, MacroCond.NotIn("Rope"), true, "did not let go of the rope", F(sx, 0f, false, false, 10), F(sx, 0f, true, false, 1)));
            m.phases.Add(P("fly", 0, 120, MacroCond.NotIn("Jump"), false, null, F(sx, 0f, true, false, 12), F(sx, 0f, false, false, 1)));
            return m;
        }

        static InteractMacro Swim(InteractMacro m, MacroEnv e, bool up)
        {
            if (e.state != "Swim" && !e.inWater) return NA(m, "not in water (state " + e.state + ")");
            int n = StepsOf(e.seconds);
            float sx = 0f;
            if (e.dir == "left" || e.dir == "right") sx = DirX(e.dir, 0, e.Facing) * 0.6f;
            float sy = up ? 1f : -1f;
            if (sx != 0f) sy *= 0.8f;
            var p = P(up ? "swim up" : "dive", n, n, null, false, null, F(sx, sy, false, false, n));
            p.fail = MacroCond.Of(MacroCondKind.LeftWater);
            p.failText = "left the water";
            m.phases.Add(p);
            if (!up && e.seconds > 8f) m.note = "diving longer than 8 s: watch air_s (about 11 s of air)";
            return m;
        }

        static InteractMacro Surface(InteractMacro m, MacroEnv e)
        {
            if (e.state != "Swim" && !e.inWater) return NA(m, "not in water (state " + e.state + ")");
            if (e.atSurface) { m.note = "already at the surface"; return m; }
            int n = Math.Max(StepsOf(e.seconds), 300);
            m.phases.Add(P("surface", 1, n, MacroCond.Of(MacroCondKind.AtSurface), true, "did not reach the surface (a ceiling above? swim sideways first)", F(0f, 1f, false, false, 1)));
            return m;
        }

        static InteractMacro HatDetach(InteractMacro m, MacroEnv e)
        {
            m.phases.Add(P("pull down", HatPullSteps, HatPullSteps, MacroCond.Of(MacroCondKind.NotMindControlling), false, null, F(0f, DetachStickY, false, false, HatPullSteps)));
            m.phases.Add(P("detach", 1, 30, MacroCond.Of(MacroCondKind.NotMindControlling), false, null, F(0f, DetachStickY, false, true, 1)));
            var rel = P("release", 1, 60, MacroCond.Of(MacroCondKind.NotMindControlling), true, "the detach gesture did not take the hat off", F(0f, 0f, false, false, 1));
            rel.fallback = "force_detach";
            m.phases.Add(rel);
            return m;
        }

        static InteractMacro SubBoost(InteractMacro m, MacroEnv e)
        {
            float x, y;
            DirVec(e.dir, e, out x, out y);
            int charge = Math.Max(SubChargeMinSteps, Math.Min(180, StepsOf(e.seconds)));
            bool dg = e.digital;
            m.phases.Add(P("charge", charge, charge, null, false, null, F(x, y, !dg, dg, charge)));
            m.phases.Add(P("fire", 1, 20, MacroCond.Of(MacroCondKind.Boosting), true, "the boost did not fire (cooldown 4 s after a boost, lights out, or boost not allowed here)", F(x, y, false, false, 1)));
            m.phases.Add(P("coast", 20, 20, null, false, null, F(x, y, false, false, 20)));
            return m;
        }

        static InteractMacro SubExit(InteractMacro m, MacroEnv e)
        {
            if (!e.subOnSurface)
                m.phases.Add(P("surface", 1, Math.Max(StepsOf(e.seconds), 360), MacroCond.Of(MacroCondKind.SubOnSurface), true, "the submarine did not reach the surface", F(0f, 1f, false, false, 1)));
            m.phases.Add(P("settle", 6, 6, null, false, null, F(0f, 0f, false, false, 6)));
            if (e.digital)
                m.phases.Add(P("exit", 1, 240, MacroCond.Of(MacroCondKind.NotInSub), true, "did not leave the submarine: it must be at the surface, upright, slow and not boosting", F(0f, 0f, false, false, 3), F(0f, 1f, false, false, 5), F(0f, 0f, false, false, 1)));
            else
                m.phases.Add(P("exit", 1, 240, MacroCond.Of(MacroCondKind.NotInSub), true, "did not leave the submarine: it must be at the surface, upright, slow and not boosting", F(0f, 0f, false, true, 8), F(0f, 0f, false, false, 1)));
            return m;
        }

        public static void LeverStick(MacroEnv e, out float x, out float y)
        {
            x = 0f; y = 0f;
            bool vertical = e.device == "lever_v";
            switch (e.dir)
            {
                case "left": x = -1f; return;
                case "right": x = 1f; return;
                case "up": y = 1f; return;
                case "down": y = -1f; return;
            }
            int auto;
            if (vertical) auto = e.leverDir != 0 ? -Math.Sign(e.leverDir) : -1;
            else auto = e.leverDir != 0 ? -Math.Sign(e.leverDir) : (e.TargetSide != 0 ? -e.TargetSide : -e.Facing);
            if (e.dir == "away") auto = -auto;
            if (vertical) y = auto; else x = auto;
        }

        static InteractMacro Lever(InteractMacro m, MacroEnv e)
        {
            if (e.state != "Run" && e.state != "CustomAnim") return NA(m, "lever works standing at the lever (state " + e.state + ")");
            float x, y;
            LeverStick(e, out x, out y);
            int n = StepsOf(e.seconds);
            if (e.state == "Run")
            {
                var t = Turn(e);
                if (t != null) m.phases.Add(t);
                m.phases.Add(P("grab", 1, 40, MacroCond.Or(MacroCond.In("CustomAnim"), MacroCond.Of(MacroCondKind.DeviceGrabbed)), true, "could not grab the lever: stand at it (move_to the lever first)", F(0f, 0f, false, true, 1)));
            }
            m.phases.Add(P("wait", LeverWaitSteps, LeverWaitSteps, null, false, null, F(0f, 0f, false, true, LeverWaitSteps)));
            m.phases.Add(P("pull", n, n, null, false, null, F(x, y, false, true, n)));
            m.phases.Add(P("let go", 1, 40, MacroCond.NotIn("CustomAnim"), false, null, F(0f, 0f, false, false, 1)));
            return m;
        }

        public static List<InputFrame> ValveCircle(float startDeg, int sign, int steps, float turnsPerSecond)
        {
            var l = new List<InputFrame>();
            double a = (startDeg + sign * 30.0) * Math.PI / 180.0;
            double w = sign * turnsPerSecond * 2.0 * Math.PI / 60.0;
            for (int i = 0; i < steps; i++)
            {
                double t = a + w * i;
                l.Add(new InputFrame { x = (float)Math.Cos(t), y = (float)Math.Sin(t), action = true, steps = 1 });
            }
            return l;
        }

        public static int ValveSign(string dir) { return dir == "left" || dir == "up" || dir == "away" ? 1 : -1; }

        static InteractMacro Valve(InteractMacro m, MacroEnv e)
        {
            if (e.state != "Run" && e.state != "CustomAnim") return NA(m, "valve works standing at the valve (state " + e.state + ")");
            int n = StepsOf(e.seconds);
            if (e.state == "Run")
            {
                var t = Turn(e);
                if (t != null) m.phases.Add(t);
                m.phases.Add(P("grab", 1, 40, MacroCond.Or(MacroCond.In("CustomAnim"), MacroCond.Of(MacroCondKind.DeviceGrabbed)), true, "could not grab the valve: stand at it (move_to the valve first)", F(0f, 0f, false, true, 1)));
            }
            m.phases.Add(P("wait", ValveWaitSteps, ValveWaitSteps, null, false, null, F(0f, 0f, false, true, ValveWaitSteps)));
            var turn = new MacroPhase { name = "turn", minSteps = n, maxSteps = n };
            turn.frames.AddRange(ValveCircle(e.valveAngle, ValveSign(e.dir), n, ValveTurnsPerSecond));
            turn.fail = MacroCond.NotIn("CustomAnim");
            turn.failText = "let go of the valve";
            m.phases.Add(turn);
            m.phases.Add(P("let go", 1, 40, MacroCond.NotIn("CustomAnim"), false, null, F(0f, 0f, false, false, 1)));
            m.note = (ValveSign(e.dir) < 0 ? "clockwise" : "counter-clockwise") + " about " + (e.seconds * ValveTurnsPerSecond).ToString("0.#", IC) + " turns";
            return m;
        }

        // the stick of a grab device move: an explicit dir wins; else dev_x pulls away from the target (push toward),
        // dev_y up, dev_down pulls down (push up), dev_free away from / toward the target
        public static void DeviceStick(MacroEnv e, bool push, out float x, out float y)
        {
            x = 0f; y = 0f;
            switch (e.dir)
            {
                case "left": x = -1f; return;
                case "right": x = 1f; return;
                case "up": y = 1f; return;
                case "down": y = -1f; return;
            }
            int side = e.TargetSide != 0 ? e.TargetSide : e.Facing;
            switch (e.device)
            {
                case "dev_y": y = 1f; return;
                case "dev_down": y = push ? 1f : -1f; return;
                case "dev_free":
                    DirVec(push ? (e.dir == "away" ? "away" : "toward") : "away", e, out x, out y);
                    return;
            }
            x = push ? (e.dir == "away" ? -side : side) : -side;
        }

        static InteractMacro DeviceMove(InteractMacro m, MacroEnv e, bool push)
        {
            string what = push ? "push" : "pull";
            bool ladder = e.state == "Ladder";
            if (e.state != "Run" && e.state != "Swim" && e.state != "CustomAnim" && !ladder) return NA(m, what + " on a device works standing (or swimming) at its handle (state " + e.state + ")");
            float x, y;
            DeviceStick(e, push, out x, out y);
            int n = StepsOf(e.seconds);
            if (e.state == "Run" || e.state == "Swim")
            {
                var t = Turn(e);
                if (t != null) m.phases.Add(t);
                m.phases.Add(P("grab", 1, 40, MacroCond.Or(MacroCond.In("CustomAnim"), MacroCond.Of(MacroCondKind.DeviceGrabbed)), true, "could not grab it: stand at its handle facing it (move_to the target first)", F(0f, 0f, false, true, 1)));
                m.phases.Add(P("wait", LeverWaitSteps, LeverWaitSteps, null, false, null, F(0f, 0f, false, true, LeverWaitSteps)));
            }
            if (e.device == "dev_grab")
            {
                m.phases.Add(P("hold", n, n, null, false, null, F(0f, 0f, false, true, n)));
                m.note = "held the hook for " + e.seconds.ToString("0.##", IC) + " s";
            }
            else m.phases.Add(P(what, 1, n, MacroCond.DeviceIn(DeviceDone), false, null, F(x, y, false, true, n)));
            m.phases.Add(P("let go", 1, 40, MacroCond.NotIn("CustomAnim"), false, null, F(0f, 0f, false, false, 1)));
            if (ladder) m.note = "from the ladder: stick " + (y > 0 ? "up" : y < 0 ? "down" : x > 0 ? "right" : "left") + " with action held (a hatch above opens from below)";
            return m;
        }

        static InteractMacro ButtonPress(InteractMacro m, MacroEnv e)
        {
            if (e.device != "button") return NA(m, "no pressure button at the target (button needs a SwitchButtonNew; step-on plates are pressed by standing or putting a box on them with move_to / push)");
            if (e.state != "Run" && e.state != "Jump" && e.state != "Rope" && e.state != "Swim") return NA(m, "button works standing, jumping, on a rope or swimming at it (state " + e.state + ")");
            if (e.state == "Run")
            {
                var t = Turn(e);
                if (t != null) m.phases.Add(t);
            }
            int n = StepsOf(e.seconds);
            m.phases.Add(P("release first", 2, 2, null, false, null, F(0f, 0f, false, false, 2)));
            m.phases.Add(P("press", 1, 30, MacroCond.DeviceIn(ButtonDown), true, "the button did not go in: stand right at it facing it, within about 1 m (move_to the button first)", F(0f, 0f, false, true, 1)));
            m.phases.Add(P("hold", n, n, null, false, null, F(0f, 0f, false, true, n)));
            m.phases.Add(P("let go", 1, 1, null, false, null, F(0f, 0f, false, false, 1)));
            m.note = "pressed and held " + e.seconds.ToString("0.##", IC) + " s (a hold button stays in only while action is held)";
            return m;
        }

        static InteractMacro Lift(InteractMacro m, MacroEnv e)
        {
            if (e.device != "lift") return NA(m, "no lift door at the target (lift works on OpenElevatorDoors)");
            if (e.state != "Run" && e.state != "Swim" && e.state != "CustomAnim") return NA(m, "lift works standing in front of the door (state " + e.state + ")");
            if (e.state != "CustomAnim")
            {
                int face = e.deviceFace != 0 ? e.deviceFace : e.TargetSide;
                var t = TurnTo(e, face);
                if (t != null) m.phases.Add(t);
                m.phases.Add(P("grab", 1, 45, MacroCond.DeviceIn(LiftGrabbed), true, "could not grab the door: stand in front of it, inside its trigger, facing the way it opens (move_to the door first)", F(0f, 0f, false, true, 1)));
            }
            m.phases.Add(P("settle", 1, 120, MacroCond.DeviceIn("LiftDoor", "OpenDoor", "OpeningTheDoor", "DoorOpening", "Complete"), true, "the grab did not finish (the door state did not reach LiftDoor)", F(0f, 0f, false, true, 1)));
            int n = Math.Min(600, Math.Max(StepsOf(e.seconds), 360));
            m.phases.Add(P("lift", 1, n, MacroCond.DeviceIn(LiftOpen), true, "the door is not open yet (it falls back when the boy lets go): lift again with more seconds", F(0f, 1f, false, true, 1)));
            m.phases.Add(P("let go", 1, 60, MacroCond.NotIn("CustomAnim"), false, null, F(0f, 0f, false, false, 1)));
            return m;
        }

        static InteractMacro SubEnter(InteractMacro m, MacroEnv e)
        {
            if (e.kind != "boy" && e.kind != "dude") return NA(m, "only the boy or a hat dude climbs into the submarine");
            if (e.state != "Run") return NA(m, "sub_enter works standing on top of the submarine at its hatch, not jumping (state " + e.state + ")");
            m.phases.Add(P("climb in", 1, 90, MacroCond.Of(MacroCondKind.InSub), true, "did not climb in: stand on top of the submarine within 0.7 m of its hatch, not jumping (the stick goes straight down)", F(0f, -1f, false, false, 1)));
            if (e.kind == "dude") m.note = "realtime_window: a dude climbs in after about 0.2 s of real time (SubmarineClimbController uses Time.realtimeSinceStartup)";
            return m;
        }

        static InteractMacro HatWear(InteractMacro m, MacroEnv e)
        {
            if (e.mindControlling) return NA(m, "already in a mind-control hat: hat_detach first");
            if (e.kind != "boy") return NA(m, "only the boy puts on a mind-control hat");
            if (e.state != "Run" && e.state != "Jump" && e.state != "Ladder" && e.state != "Rope" && e.state != "Swim") return NA(m, "hat_wear works under the hat (state " + e.state + ")");
            var jump = P("jump", 1, 70, MacroCond.Of(MacroCondKind.MindControlling), false, null, F(0f, 0f, false, false, 1), F(0f, 0f, true, false, 18), F(0f, 0f, false, false, 1));
            jump.skipUnless = MacroCond.In("Run");
            m.phases.Add(jump);
            m.phases.Add(P("catch", 0, 60, MacroCond.Of(MacroCondKind.MindControlling), true, "did not reach the hat: it hangs too high to jump into from here (reach it from a ledge, a box or a ladder below it)", F(0f, 0f, false, false, 1)));
            return m;
        }

        static InteractMacro Throw(InteractMacro m, MacroEnv e)
        {
            if (e.kind != "huddle") return NA(m, "only the huddle throws (the boy lets go with release)");
            if (!e.holding) return NA(m, "the huddle is not holding anything: grab first");
            float x = 0f, y = 0f;
            if (e.dir == "up") y = 1f;
            else if (e.dir == "down") return NA(m, "throw goes left, right, up, toward or away");
            else x = DirX(e.dir, e.TargetSide, e.Facing);
            int n = Math.Max(10, Math.Min(StepsOf(e.seconds), 120));
            var swing = P("swing", n, n, null, false, null, F(x, y, false, true, n));
            swing.fail = MacroCond.Of(MacroCondKind.NotHolding);
            swing.failText = "the huddle lost its grip before the throw";
            m.phases.Add(swing);
            m.phases.Add(P("throw", 1, 20, MacroCond.Of(MacroCondKind.NotHolding), true, "the huddle did not let go", F(x, y, false, false, 1)));
            m.phases.Add(P("follow", 10, 10, null, false, null, F(0f, 0f, false, false, 10)));
            m.note = "thrown " + (y > 0 ? "up" : x > 0 ? "right" : "left") + " (action released with the stick held past 0.5)";
            return m;
        }
    }

    // BoyInput's stick rules (FP/BoyInput.cs:58-131), float for float: extraDeadZone follows the side of the last strong
    // stick (|stick|^2 > 0.7) and decays 3 * deltaTime (0.05 at the 60 Hz logic step) per step; a direction registers
    // when its projection beats 0.3 plus that deadzone (0 when it points the same way) inside the 63 degree cone.
    internal static class BoyStick
    {
        public const float Deadzone = 0.3f, StrongSqr = 0.7f, Extra = 0.6f, ConeRad = 1.0995574f;
        public static readonly float DecayPerStep = (1f / 60f) * 3f;

        static float Sign(float v) { return v >= 0f ? 1f : -1f; }

        public static float NextExtra(float extra, float x, float y)
        {
            if (x * x + y * y > StrongSqr) return Extra * Sign(x);
            if (extra > 0f) return Math.Max(extra - DecayPerStep, 0f);
            return Math.Min(extra + DecayPerStep, 0f);
        }

        public static bool Facing(float x, float y, float dx, float dy, float extra)
        {
            float num = Math.Abs(extra);
            if (x * extra > 0f) num = 0f;
            float proj = x * dx + y * dy;
            if (!(Deadzone + num < proj)) return false;
            float m = (float)Math.Sqrt(x * x + y * y);
            return m > 0f && (float)Math.Cos(ConeRad) < proj / m;
        }

        public static int LooseX(float x, float y, float extra)
        {
            if (Facing(x, y, 1f, 0f, extra)) return 1;
            if (Facing(x, y, -1f, 0f, extra)) return -1;
            return 0;
        }

        // the 1-based step at which a constant stick (x, y) first gives looseDir.x == dirX, starting from extra; -1 when
        // not within max steps
        public static int StepsUntilLooseX(float extra, float x, float y, int dirX, int max)
        {
            float e = extra;
            for (int i = 1; i <= max; i++)
            {
                e = NextExtra(e, x, y);
                if (LooseX(x, y, e) == dirX) return i;
            }
            return -1;
        }
    }
}
