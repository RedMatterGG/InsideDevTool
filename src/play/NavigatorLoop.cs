using System;
using System.Collections.Generic;

namespace InsideDev.Play
{
    // NavigatorLoop.cs (owner NAV): the pure per-step controller of move_to, tested offline in qa/mcp/NAV. Navigator reads
    // the body into a NavBody after every logic step (Stepper stopCheck, main thread), NavLoop.Step decides the input for
    // the next step (cmd: stick + jump level) from the body and the planned NavRoute, and returns a stop code or null:
    //   arrived | blocked:<reason> | fell | died | lost | replan (the worker slices and plans again, then continues)
    // Game rules it follows (report 02 section 2.2): stick |x| > 0.8 runs; jump is a rising edge (one released step
    // before every press) and holding it gives the full height; at a ledge hang stick up (looseDir.y > 0.5) climbs; an
    // obstruction with an obstruction plateau is climbed by pushing into it; walking into a pushable grabs it, so
    // adjacent stops leave room in front of the target.
    internal struct NavBody
    {
        public bool valid, dead, grounded, inWater, swimming, onLadder, onRope, hanging, climbing, special, sub, huddle, touchingTarget;
        public float x, y, vx, vy, facing;
        public string state;
        public bool obstructed, climbable, touching;
        public float obstructionDist;
        public bool edgeL, edgeR;
        public float edgeDistL, edgeDistR, edgeDropL, edgeDropR;

        public bool Edge(float dir) { return dir > 0 ? edgeR : edgeL; }
        public float EdgeDist(float dir) { return dir > 0 ? edgeDistR : edgeDistL; }
        public float EdgeDrop(float dir) { return dir > 0 ? edgeDropR : edgeDropL; }
    }

    internal struct NavCmd
    {
        public float x, y;
        public bool jump;

        public bool Same(NavCmd o) { return Math.Abs(x - o.x) < 1e-3f && Math.Abs(y - o.y) < 1e-3f && jump == o.jump; }
        public bool Neutral { get { return Math.Abs(x) < 1e-3f && Math.Abs(y) < 1e-3f && !jump; } }
    }

    internal sealed class NavGoal
    {
        // feet x / feet y of the goal; face = the direction to face at the end (adjacent stop), 0 = any
        public float x, y, tolerance = 0.3f, gait = 1f, face;
        public bool hasY;
    }

    internal sealed class NavLoop
    {
        public const int JumpHold = 20, SettleMax = 30, StuckSteps = 50, HangMax = 90, ClimbMax = 150, AirMax = 300, LadderEnterMax = 45;
        public const float WalkMag = 0.6f, SlowMag = 0.45f, TurnMag = 0.35f;

        readonly NavGoal goal;
        readonly NavCaps caps;
        public NavRoute route;
        public NavCmd cmd;
        public readonly List<string> events = new List<string>();
        public bool needPlan = true;
        public int steps, replans;
        public string phase = "walk";
        int phaseSteps, jumpTimer, retries, settle, faceSteps, edgeReplans, stuckReplans, noProgress;
        float lastGroundY, airStartY, expectedDrop, jumpStartY, jumpLandY, progressX, progressY;
        bool haveGround, haveProgress, pressedThisJump;
        string jumpKind;

        public NavLoop(NavGoal goal, NavCaps caps) { this.goal = goal; this.caps = caps; }

        // grounded walking with no maneuver in progress: the worker may slice and plan again
        public bool CanReplan { get { return phase == "walk" || phase == "settle"; } }

        public void SetRoute(NavRoute r)
        {
            route = r;
            needPlan = false;
            replans++;
        }

        void Note(string e) { if (events.Count < 30) events.Add(e); }

        void Set(float x, float y, bool jump) { cmd.x = Clamp(x); cmd.y = Clamp(y); cmd.jump = jump; }

        static float Clamp(float v) { return v > 1f ? 1f : (v < -1f ? -1f : v); }

        void Enter(string p) { if (phase != p) { phase = p; phaseSteps = 0; noProgress = 0; } }

        string Stop(string code) { Set(0f, 0f, false); return code; }

        void TrackProgress(NavBody b)
        {
            if (!haveProgress || Math.Abs(b.x - progressX) > 0.1f || Math.Abs(b.y - progressY) > 0.15f)
            {
                progressX = b.x; progressY = b.y; haveProgress = true; noProgress = 0;
            }
            else noProgress++;
        }

        // one logic step; returns a stop code or null (cmd holds the input for the next step)
        public string Step(NavBody b)
        {
            steps++; phaseSteps++;
            if (!b.valid) return Stop("lost");
            if (b.dead) return Stop("died");
            TrackProgress(b);
            if (b.special) return Stop("blocked:special_mode");
            if (b.sub || b.swimming) return Swim(b);
            if (b.onLadder) return Ladder(b);
            if (b.onRope) return Stop("blocked:on_rope");
            if (b.hanging) return Hang(b);
            if (b.climbing)
            {
                Enter("climb");
                if (phaseSteps > ClimbMax) return Stop("blocked:climb_stuck");
                Set(Dir(b) * WalkMag, 0f, false);
                return null;
            }
            if (!b.grounded) return Air(b);
            return Ground(b);
        }

        float Dir(NavBody b) { return goal.x >= b.x ? 1f : -1f; }

        string Swim(NavBody b)
        {
            Enter("swim");
            float tx = goal.x, ty = goal.hasY ? goal.y : b.y;
            float dx = tx - b.x, dy = ty - b.y;
            float d = goal.hasY ? (float)Math.Sqrt(dx * dx + dy * dy) : Math.Abs(dx);
            if (d <= goal.tolerance) return Stop("arrived");
            if (noProgress > StuckSteps * 2) return Stop("blocked:wall");
            if (!goal.hasY) { Set(dx > 0 ? 1f : -1f, 0f, false); return null; }
            float n = (float)Math.Sqrt(dx * dx + dy * dy);
            Set(dx / n, dy / n, false);
            return null;
        }

        string Ladder(NavBody b)
        {
            Enter("ladder");
            if (!goal.hasY)
            {
                var ls = route != null ? route.Find("ladder") : null;
                if (ls == null) return Stop("blocked:on_ladder");
                goal.hasY = true; goal.y = ls.toY;
            }
            float dy = goal.y - b.y;
            if (Math.Abs(dy) <= Math.Max(goal.tolerance, 0.25f)) return Stop("arrived");
            if (noProgress > StuckSteps) return Stop(dy > 0 ? "blocked:ladder_top" : "blocked:ladder_bottom");
            Set(0f, dy > 0 ? 1f : -1f, false);
            return null;
        }

        string Hang(NavBody b)
        {
            if (phase != "hang") { Enter("hang"); Note("hang x=" + PlayShared.F(b.x) + " y=" + PlayShared.F(b.y)); }
            if (!caps.climb) return Stop("blocked:ledge_up");
            if (phaseSteps > HangMax) return Stop("blocked:ledge_up");
            Set(0f, 1f, false);
            return null;
        }

        string Air(NavBody b)
        {
            if (phase == "jump")
            {
                jumpTimer++;
                Set(Dir(b), 0f, jumpTimer < JumpHold);
                if (jumpTimer > AirMax) return Stop("fell");
                return null;
            }
            if (phase != "air")
            {
                airStartY = haveGround ? lastGroundY : b.y;
                var d = route != null ? route.Next(b.x, 1.2f) : null;
                expectedDrop = d != null && d.kind == "drop" && Math.Abs(d.x - b.x) < 1.5f ? d.h : 0f;
                Enter("air");
            }
            Set(Dir(b) * (expectedDrop > 0f ? WalkMag : 0f), 0f, false);
            if (phaseSteps > AirMax) return Stop("fell");
            return null;
        }

        string Landed(NavBody b)
        {
            string was = phase;
            Enter("walk");
            needPlan = true;
            if (was == "air")
            {
                float fall = airStartY - b.y;
                if (fall > expectedDrop + 0.8f && fall > caps.stepDown + 0.3f) { Note("fell " + PlayShared.F(fall) + " m at x=" + PlayShared.F(b.x)); return Stop("fell"); }
                if (fall > caps.stepDown) Note("dropped " + PlayShared.F(fall) + " m");
                return "replan";
            }
            if (was == "jump")
            {
                if (jumpKind == "jump_climb" && b.y < jumpStartY + 0.3f)
                {
                    retries++;
                    Note("jump_climb missed (try " + retries + ")");
                    if (retries > 1) return Stop("blocked:ledge_up");
                    return "replan";
                }
                if (b.y < jumpLandY - 1.0f && b.y < jumpStartY - caps.stepDown - 0.5f) { Note("jump fell short at x=" + PlayShared.F(b.x)); return Stop("fell"); }
                Note("landed x=" + PlayShared.F(b.x) + " y=" + PlayShared.F(b.y));
                return "replan";
            }
            Note("climbed to y=" + PlayShared.F(b.y));
            return "replan";
        }

        void StartJump(NavStep m, NavBody b, float dir)
        {
            Enter("jump");
            jumpKind = m.kind; jumpStartY = b.y; jumpLandY = m.toY; jumpTimer = 0; pressedThisJump = false;
            Note(m.kind + " at x=" + PlayShared.F(b.x));
        }

        string Ground(NavBody b)
        {
            if (phase == "jump")
            {
                if (!pressedThisJump)
                {
                    if (cmd.jump) { Set(Dir(b), 0f, false); return null; }
                    pressedThisJump = true;
                    jumpTimer = 0;
                    Set(Dir(b), 0f, true);
                    return null;
                }
                jumpTimer++;
                if (jumpTimer < 6) { Set(Dir(b), 0f, true); return null; }
                if (jumpTimer < 12) { Set(Dir(b), 0f, false); return null; }
                return Landed(b);
            }
            if (phase == "air" || phase == "climb" || phase == "hang") return Landed(b);
            lastGroundY = b.y; haveGround = true;
            if (phase == "ladder_enter")
            {
                if (phaseSteps > LadderEnterMax) return Stop("blocked:ladder");
                return null;
            }
            float dg = goal.x - b.x, adg = Math.Abs(dg), dir = dg >= 0 ? 1f : -1f;
            if (adg <= goal.tolerance || b.touchingTarget) return Settle(b);
            if (phase == "settle") Enter("walk");
            if (needPlan || route == null) return "replan";
            if ((route.dir > 0) != (dir > 0)) { needPlan = true; return "replan"; }
            float mag = goal.gait;
            if (adg < 0.8f) mag = Math.Min(mag, SlowMag);
            var m = route.Next(b.x, 0.05f);
            if (m != null && (m.x - goal.x) * dir > 0) m = null;
            if (m == null && route.end == "open" && (route.endX - b.x) * dir < 2.5f) { needPlan = true; return "replan"; }
            if (m != null)
            {
                float dm = (m.x - b.x) * dir;
                switch (m.kind)
                {
                    case "block":
                        float stopAt = caps.radius + 0.2f;
                        if (dm <= stopAt + 0.1f || (b.touching && b.obstructed))
                        {
                            Set(0f, 0f, false);
                            if (Math.Abs(b.vx) < 0.3f) { Note("stopped before " + m.reason + " at x=" + PlayShared.F(m.x)); return "blocked:" + m.reason; }
                            return null;
                        }
                        if (dm < 1.0f) mag = Math.Min(mag, SlowMag);
                        break;
                    case "climb":
                        if (dm < 1.0f) mag = Math.Max(mag, WalkMag);
                        if (b.touching && b.obstructed && !b.climbable && noProgress > 20)
                        {
                            StartJump(new NavStep { kind = "jump_climb", x = m.x, y = m.y, toY = m.toY, h = m.h }, b, dir);
                            Set(dir, 0f, false);
                            return null;
                        }
                        break;
                    case "jump_climb":
                        if (dm <= 0.8f || (b.touching && b.obstructed)) { StartJump(m, b, dir); Set(dir, 0f, false); return null; }
                        break;
                    case "jump_gap":
                        mag = Math.Max(mag, 1f);
                        if (dm <= 0.3f || (b.Edge(dir) && b.EdgeDist(dir) < 0.3f)) { StartJump(m, b, dir); Set(dir, 0f, false); return null; }
                        break;
                    case "ladder":
                        if (Math.Abs(m.x - b.x) < 0.25f)
                        {
                            Enter("ladder_enter");
                            Note("ladder at x=" + PlayShared.F(m.x));
                            Set(0f, m.toY > b.y ? 1f : -1f, false);
                            return null;
                        }
                        mag = Math.Min(mag, WalkMag);
                        Set((m.x > b.x ? 1f : -1f) * mag, 0f, false);
                        return null;
                }
            }
            if (b.Edge(dir) && b.EdgeDist(dir) < 0.35f && b.EdgeDrop(dir) >= 2.4f && !Expected(m, b, dir, 1.0f))
            {
                Set(0f, 0f, false);
                if (edgeReplans++ == 0) { needPlan = true; return "replan"; }
                Note("edge with a deep drop at x=" + PlayShared.F(b.x + dir * b.EdgeDist(dir)));
                return "blocked:" + (caps.jumpGaps ? "ledge_down" : "pit");
            }
            if (b.touching && b.obstructed && noProgress > StuckSteps && (m == null || (m.kind != "climb" && m.kind != "jump_climb")))
            {
                if (b.climbable && !caps.climb) return Stop("blocked:obstacle");
                if (stuckReplans++ == 0) { needPlan = true; noProgress = 0; return "replan"; }
                return Stop(b.climbable ? "blocked:obstacle" : "blocked:wall");
            }
            if (noProgress > StuckSteps * 2) return Stop("blocked:obstacle");
            Set(dir * mag, 0f, false);
            return null;
        }

        static bool Expected(NavStep m, NavBody b, float dir, float within)
        {
            return m != null && (m.kind == "drop" || m.kind == "jump_gap") && Math.Abs(m.x - b.x) <= within;
        }

        string Settle(NavBody b)
        {
            if (phase != "settle") { Enter("settle"); settle = 0; faceSteps = 0; }
            settle++;
            if (Math.Abs(b.vx) > 0.4f && settle < SettleMax) { Set(0f, 0f, false); return null; }
            if (goal.face != 0f && b.facing * goal.face < 0f && faceSteps < 6 && !b.touchingTarget)
            {
                faceSteps++;
                Set(goal.face * TurnMag, 0f, false);
                return null;
            }
            if (goal.hasY && Math.Abs(goal.y - b.y) > 0.6f && route != null)
            {
                var ls = route.Find("ladder");
                if (ls != null && Math.Abs(ls.x - b.x) < 0.4f)
                {
                    Enter("ladder_enter");
                    Set(0f, goal.y > b.y ? 1f : -1f, false);
                    return null;
                }
            }
            return Stop("arrived");
        }
    }
}
