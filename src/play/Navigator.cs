using System;
using System.Collections.Generic;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // Navigator.cs (owner NAV, MoveRequest / MoveTo and the move_to schema frozen): "go to X" for the controlled body.
    // There is no navmesh (report 02 section 6): closed loop on the body position through AgentCore + Stepper, using the
    // 2D collision slice at the body's depth plane (NavGeometry), status.isObstructed / edges, ladders and ropes, run
    // 2.72 m/s, jump height BoyUtils.GetJumpHeightEstimate. Small steps and obstacles are climbed by the game itself;
    // jump_gaps jumps gaps and low walls the slice says are passable. Outcomes: arrived, blocked (reason wall / pit /
    // obstacle / ledge_up / ledge_down / water), fell, died, timeout, cancelled, unreachable, loading. Takes the control lane.
    // Loop (worker): slice around body -> goal (SliceJob chunks), NavPlan.Plan, then Stepper.Run segments of 30 steps
    // whose stopCheck reads the body (NavBody) and lets NavLoop pick the next step's input, applied as one latched
    // AgentCore frame; a stop code ends the segment (arrived, blocked, fell, replan ...). Re-plans after every landing or
    // climb, when the route ends in front of the body, or on an unexpected edge / wall. Input is always released at the
    // end (also on error and cancel), except a grab grip (interact grab, AgentCore.Grip): it stays held on arrived /
    // blocked / fell / timeout / unreachable (the box comes along; warning grip_held) and is let go on cancel, death and
    // loads. when (Waits.When) waits for one wait_until condition first, inside the same call. y and the result y use the
    // body centre (boy.pos.y; feet = y - height / 2).
    public sealed class MoveRequest
    {
        // exactly one of target / x / dx
        public string target;
        public bool hasX, hasDx, hasY;
        public float x, dx, y;
        public string gait = "run";
        public float tolerance = 0.3f;
        // adjacent = stop facing the target within grab reach; exact = stop at x within tolerance
        public string stop = "adjacent";
        public int maxSteps = 600;
        public bool jumpGaps = true, climb = true;
    }

    public static class Navigator
    {
        public const string Owner = "NAV";
        public static readonly string[] Outcomes = { "arrived", "blocked", "fell", "died", "timeout", "cancelled", "unreachable", "loading" };
        const int SegmentSteps = 30, MaxReplans = 60, Horizon = 18;

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("move_to", "Move to", PlayShared.Play, Owner,
                "Walk or run the controlled body to an object id (target), a world x, or a relative dx (metres, + right). Climbs small steps, jumps small gaps (jump_gaps) and stops at walls, pits and ledges it cannot pass with outcome blocked and a reason. Runs the game until arrived / blocked / fell / died / timeout.",
                Schema.Obj().Str("target", "Object id from observe (o12).").Num("x", "World x (m).", -1e5, 1e5).Num("dx", "Metres from the body (+ right, - left).", -500, 500)
                    .Num("y", "World y for swimming or climbing targets (m).", -1e5, 1e5)
                    .Enum("gait", "walk or run.", "run", "walk", "run").Num("tolerance", "Arrival tolerance (m).", 0.3, 0.05, 3)
                    .Enum("stop", "adjacent = within grab reach facing the target, exact = at the point.", "adjacent", "adjacent", "exact")
                    .Int("max_steps", "Logic-step limit (60 per second).", 600, 1, 1800)
                    .Bool("jump_gaps", "Jump gaps and low walls on the way.", true).Bool("climb", "Climb onto ledges in the way.", true)
                    .Obj("when", Waits.WhenText, Waits.WhenSchema())
                    .Num("when_timeout_s", "Longest wait for when (wall clock s).", Waits.WhenDefaultS, 0.1, Waits.WhenMaxS)
                    .Enum("observe", "Observation in the result.", "concise", "concise", "none").Build(),
                Schema.Obj().Enum("outcome", "How it ended.", null, Outcomes).Str("reason", "Why blocked / unreachable (wall, pit, obstacle, ledge_up, ledge_down, water ...).")
                    .Int("steps", "Logic steps run.", 0, 100000).Num("distance_left", "Metres to the goal.", 0, 1e5)
                    .Num("x", "Body x at the end.", -1e5, 1e5).Num("y", "Body y at the end.", -1e5, 1e5)
                    .Arr("events", "Jumps, climbs and stops on the way.", Schema.Str(), 0, 30).Arr("warnings", "", Schema.Str(), 0, 20)
                    .Prop("changes", null, ChangeLog.Ref())
                    .Prop("observe", null, Observer.ConciseRef()).BuildOutput()).AlwaysLoad(), Handle);
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            var req = Parse(a);
            Validate(req);
            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return busy ?? ToolResult.Fail(McpErr.Busy, "busy: another action is running");
            var when = PlayShared.Obj(a, "when");
            Json.Obj wb = null;
            if (when != null)
            {
                string ws;
                wb = Waits.When(ctx, when, PlayShared.Num(a, "when_timeout_s", Waits.WhenDefaultS), WhenReserveMs(req.maxSteps), "move_to", out ws);
                if (ws != RunStop.Condition)
                {
                    var f = PlayShared.Env();
                    f["outcome"] = ws == RunStop.Died ? "died" : ws == RunStop.Loading ? "loading" : ws == RunStop.Cancelled ? "cancelled" : "timeout";
                    f["reason"] = "when: " + wb.Str("detail", ws);
                    f["steps"] = 0.0;
                    f["when"] = wb;
                    f["warnings"] = new List<object>();
                    if (PlayShared.Str(a, "observe", "concise") != "none" && !ctx.Cancelled)
                    {
                        var o0 = ctx.Main(() => { try { return Observer.Concise("move_to"); } catch (NotImplementedException) { return null; } });
                        if (o0 != null) f["observe"] = o0;
                    }
                    return ToolResult.Ok(f);
                }
            }
            var r = MoveTo(ctx, req);
            if (wb != null) r["when"] = wb;
            if (PlayShared.Str(a, "observe", "concise") != "none" && !ctx.Cancelled)
            {
                var o = ctx.Main(() => { try { return Observer.Concise("move_to"); } catch (NotImplementedException) { return null; } });
                if (o != null) r["observe"] = o;
            }
            return ToolResult.Ok(r);
        }

        internal static MoveRequest Parse(Json.Obj a)
        {
            var q = new MoveRequest();
            q.target = PlayShared.Str(a, "target", null);
            q.hasX = PlayShared.Has(a, "x"); q.x = (float)PlayShared.Num(a, "x", 0);
            q.hasDx = PlayShared.Has(a, "dx"); q.dx = (float)PlayShared.Num(a, "dx", 0);
            q.hasY = PlayShared.Has(a, "y"); q.y = (float)PlayShared.Num(a, "y", 0);
            q.gait = PlayShared.Str(a, "gait", "run");
            q.tolerance = (float)PlayShared.Num(a, "tolerance", 0.3);
            q.stop = PlayShared.Str(a, "stop", "adjacent");
            q.maxSteps = PlayShared.Int(a, "max_steps", 600);
            q.jumpGaps = PlayShared.Bool(a, "jump_gaps", true);
            q.climb = PlayShared.Bool(a, "climb", true);
            return q;
        }

        // wall clock a move of max_steps needs after its when wait (real-time pacing; at most 25 s, the rest stays for the wait)
        public static int WhenReserveMs(int maxSteps) { return 3000 + (int)Math.Min(22000, Math.Ceiling(Math.Max(0, maxSteps) * 1000.0 / 60.0)); }

        internal static void Validate(MoveRequest req)
        {
            if (req == null) throw new McpException(McpErr.BadArgs, "no move request");
            int n = (!string.IsNullOrEmpty(req.target) ? 1 : 0) + (req.hasX ? 1 : 0) + (req.hasDx ? 1 : 0);
            if (n != 1) throw new McpException(McpErr.BadArgs, "give exactly one of target, x, dx (got " + n + ")", "target = object id from observe, x = world x, dx = metres from the body");
            if (req.maxSteps < 1) throw new McpException(McpErr.BadArgs, "max_steps must be >= 1");
        }

        // state of one move_to call; the GameObject / colliders are touched only on the main thread
        sealed class MoveState
        {
            public MoveRequest req;
            public NavGoal goal = new NavGoal();
            public NavCaps caps = new NavCaps();
            public NavLoop loop;
            public GameObject target;
            public readonly List<Collider> targetColliders = new List<Collider>();
            public float halfHeight = 0.665f;
            public NavBody last;
            public NavCmd applied;
            public bool hasApplied;
            public int rays;
            public List<object> route = new List<object>();
            public readonly List<string> warnings = new List<string>();
            public string error;
        }

        sealed class PlanStart
        {
            public SliceJob job;
            public float x, y;
            public bool ok;
        }

        // worker thread; the caller holds the control lane. Returns the structured result of move_to without observe
        public static Json.Obj MoveTo(CallContext ctx, MoveRequest req)
        {
            Validate(req);
            var st = new MoveState { req = req };
            long mark = Mark();
            var run = new RunInfo();
            try
            {
                string early = ctx.Main(() => Setup(st));
                if (early != null) Split(early, out run.outcome, out run.reason);
                else Drive(ctx, st, run);
            }
            catch (McpException e)
            {
                if (e.code != McpErr.Cancelled || st.loop == null) throw;
                run.outcome = "cancelled"; run.reason = e.Message;
            }
            finally
            {
                string o = run.outcome;
                bool keep = o == "arrived" || o == "blocked" || o == "fell" || o == "timeout" || o == "unreachable";
                try { if (keep) AgentCore.ReleaseKeepGrip(); else AgentCore.Release(); } catch (Exception) { }
            }
            bool grip = false;
            try { grip = AgentCore.Grip; } catch (Exception) { }
            if (grip) AddWarning(st, "grip_held: the grab from interact grab stays held (the object comes along; no jumps or climbs while holding); interact release lets go");
            var fb = ctx.Cancelled ? st.last : ctx.Main(() => Body(st));
            return Result(st, run.outcome, run.reason, run.total, fb, mark);
        }

        sealed class RunInfo
        {
            public string outcome, reason;
            public int total;
        }

        // worker: plan, decide, run segments until an outcome
        static void Drive(CallContext ctx, MoveState st, RunInfo run)
        {
            var req = st.req;
            int left = req.maxSteps, zeroRuns = 0, idlePlans = 0;
            bool decide = true;
            while (run.outcome == null)
            {
                if (ctx.Cancelled) { run.outcome = "cancelled"; run.reason = ctx.Info.CancelReason; return; }
                if (ctx.RemainingMs < 3000) { run.outcome = "timeout"; run.reason = "time_budget"; return; }
                if (st.loop.needPlan && st.loop.CanReplan)
                {
                    if (st.loop.replans >= MaxReplans) { run.outcome = "timeout"; run.reason = "replan_limit"; return; }
                    string pend = Plan(ctx, st);
                    if (pend != null) { Split(pend, out run.outcome, out run.reason); return; }
                    decide = true;
                }
                if (decide)
                {
                    decide = false;
                    string code = ctx.Main(() => Decide(st));
                    if (code == "replan")
                    {
                        st.loop.needPlan = true;
                        if (++idlePlans > 3) { run.outcome = "unreachable"; run.reason = "no_route"; return; }
                        continue;
                    }
                    if (code != null) { Split(code, out run.outcome, out run.reason); return; }
                }
                if (left <= 0) { run.outcome = "timeout"; run.reason = "max_steps"; return; }
                var rr = Stepper.Run(ctx, new RunRequest
                {
                    maxSteps = Math.Min(left, SegmentSteps), timeoutMs = Math.Max(1000, ctx.RemainingMs - 2500),
                    stopCheck = () => Check(st), stopOnDeath = true, stopOnLoad = true, label = "move_to"
                });
                run.total += rr.steps; left -= rr.steps;
                if (rr.steps > 0) idlePlans = 0;
                foreach (var w in rr.warnings) AddWarning(st, w);
                ctx.Progress(req.maxSteps - left, req.maxSteps, "move_to x=" + PlayShared.F(st.last.x) + " goal " + PlayShared.F(st.goal.x));
                switch (rr.stop)
                {
                    case RunStop.Died: run.outcome = "died"; break;
                    case RunStop.Loading: run.outcome = "loading"; break;
                    case RunStop.Cancelled: run.outcome = "cancelled"; run.reason = ctx.Info.CancelReason; break;
                    case RunStop.Timeout: run.outcome = "timeout"; run.reason = "time_budget"; break;
                    case RunStop.Stalled: run.outcome = "timeout"; run.reason = "stalled" + (string.IsNullOrEmpty(rr.detail) ? "" : ": " + rr.detail); break;
                    case RunStop.Condition:
                        if (rr.detail == "replan") { st.loop.needPlan = true; decide = true; }
                        else if (rr.detail == "error") throw new McpException(McpErr.GameError, "move_to: " + st.error);
                        else Split(rr.detail, out run.outcome, out run.reason);
                        break;
                    default:
                        if (rr.steps == 0 && ++zeroRuns >= 3) { run.outcome = "timeout"; run.reason = "no steps ran"; }
                        break;
                }
            }
        }

        static void Split(string code, out string outcome, out string reason)
        {
            reason = null;
            int i = code.IndexOf(':');
            outcome = i < 0 ? code : code.Substring(0, i);
            if (i >= 0) reason = code.Substring(i + 1);
            if (outcome == "lost") { outcome = "loading"; reason = "no controlled body"; }
            if (Array.IndexOf(Outcomes, outcome) < 0) { reason = code; outcome = "blocked"; }
        }

        static Json.Obj Result(MoveState st, string outcome, string reason, int total, NavBody fb, long mark)
        {
            var r = PlayShared.Env();
            r["outcome"] = outcome;
            if (!string.IsNullOrEmpty(reason)) r["reason"] = reason;
            r["steps"] = (double)total;
            float dx = st.goal.x - fb.x, dy = st.goal.hasY ? st.goal.y - fb.y : 0f;
            bool twoD = st.goal.hasY && (fb.swimming || fb.sub || fb.onLadder);
            r["distance_left"] = PlayShared.R(fb.valid ? (twoD ? Math.Sqrt(dx * dx + dy * dy) : Math.Abs(dx)) : 0);
            if (fb.valid) { r["x"] = PlayShared.R(fb.x); r["y"] = PlayShared.R(fb.y + st.halfHeight); }
            r["events"] = PlayShared.L(st.loop != null ? st.loop.events : new List<string>());
            r["warnings"] = PlayShared.L(st.warnings);
            var ch = Since(mark);
            if (ch != null) r["changes"] = ch;
            var g = new Json.Obj();
            g["x"] = PlayShared.R(st.goal.x);
            if (st.goal.hasY) g["y"] = PlayShared.R(st.goal.y + st.halfHeight);
            if (st.goal.face != 0f) g["face"] = (double)st.goal.face;
            r["goal"] = g;
            if (fb.valid) r["state"] = fb.state ?? "";
            if (st.route.Count > 0) r["route"] = st.route;
            if (st.loop != null) r["replans"] = (double)st.loop.replans;
            r["rays"] = (double)st.rays;
            return r;
        }

        static void AddWarning(MoveState st, string w)
        {
            if (string.IsNullOrEmpty(w) || st.warnings.Contains(w) || st.warnings.Count >= 20) return;
            st.warnings.Add(w);
        }

        static long Mark()
        {
            try { return ChangeLog.Mark(); } catch (NotImplementedException) { return 0; }
        }

        static Json.Obj Since(long mark)
        {
            try { return ChangeLog.Since(mark, 20); } catch (NotImplementedException) { return null; }
        }

        // main thread: readiness, goal, planner numbers, agent input
        static string Setup(MoveState st)
        {
            string why;
            if (!Ready(out why))
            {
                string w = (why ?? "").ToLowerInvariant();
                if (w.Contains("load")) return "loading:" + why;
                if (w.Contains("dead")) return "died:" + why;
                throw new McpException(McpErr.NotReady, "move_to needs a controllable body: " + why, "observe, or wait_until {ready:true}");
            }
            var ch = NavGame.Active();
            if (ch == null) return "loading:no controlled body";
            var req = st.req;
            st.halfHeight = NavGame.HalfHeight(ch);
            st.caps = NavGame.Caps(ch);
            st.caps.jumpGaps = req.jumpGaps; st.caps.climb = req.climb;
            if (ch is Huddle)
            {
                st.caps.canJump = false; st.caps.height = st.halfHeight * 2f; st.caps.headroom = Math.Max(1.0f, st.halfHeight * 1.4f); st.caps.climbAuto = 1.0f;
                AddWarning(st, "huddle: no jump planning, walls and edges are found by stalls");
            }
            var b = Read(st);
            if (!b.valid) return "loading:no controlled body";
            var goal = st.goal;
            goal.tolerance = req.tolerance;
            goal.gait = req.gait == "walk" ? NavLoop.WalkMag : 1f;
            goal.hasY = req.hasY;
            goal.y = req.y - st.halfHeight;
            if (!string.IsNullOrEmpty(req.target))
            {
                var go = Observer.ResolveTarget(req.target);
                st.target = go;
                var bd = NavGame.TargetBounds(go, st.targetColliders);
                if (req.stop == "exact") goal.x = bd.center.x;
                else
                {
                    float reach = st.caps.radius + 0.3f;
                    if (b.x < bd.min.x) { goal.x = bd.min.x - reach; goal.face = 1f; }
                    else if (b.x > bd.max.x) { goal.x = bd.max.x + reach; goal.face = -1f; }
                    else goal.x = b.x;
                }
            }
            else if (req.hasX) goal.x = req.x;
            else goal.x = b.x + req.dx;
            if (!AgentCore.Installed || AgentCore.Mode == InputMode.Human) AddWarning(st, AgentCore.Install(InputMode.Agent));
            if (!st.caps.canJump && ch is Boy) AddWarning(st, "jumping is disabled here (BoyProperties.jump.allow)");
            st.loop = new NavLoop(goal, st.caps);
            st.last = b;
            return null;
        }

        static bool Ready(out string reason)
        {
            try { return Observer.Ready(out reason); }
            catch (NotImplementedException) { }
            reason = "";
            if (GameManager.IsLoading()) { reason = "loading"; return false; }
            var ch = NavGame.Active();
            if (ch == null) { reason = "no_body"; return false; }
            if (ch.isDead) { reason = "dead"; return false; }
            return true;
        }

        // worker: slice around body -> goal in main-thread chunks, then plan (pure)
        static string Plan(CallContext ctx, MoveState st)
        {
            var ps = ctx.Main(() => StartPlan(st));
            if (!ps.ok) return "lost";
            while (!ctx.Main(() => ps.job.Step(NavGeometry.ChunkMs))) ctx.ThrowIfCancelled();
            var g = ps.job.grid;
            st.rays += ps.job.rays;
            var route = NavPlan.Plan(g, ps.x, ps.y, st.goal.x, st.goal.hasY, st.goal.y, st.caps);
            st.loop.SetRoute(route);
            st.route = route.Describe(10);
            if (route.yIgnored) AddWarning(st, "y ignored: no ladder or water at the goal");
            foreach (var n in ps.job.notes) AddWarning(st, "slice: " + n);
            if (route.end == "open" && route.reason == "not_loaded" && Math.Abs(route.endX - ps.x) < 2.6f) return "unreachable:not_loaded";
            return null;
        }

        static PlanStart StartPlan(MoveState st)
        {
            var ps = new PlanStart();
            var b = Read(st);
            if (!b.valid) return ps;
            float z;
            if (!NavGame.BodyPlane(out z)) return ps;
            float hz = Mathf.Clamp(st.goal.x, b.x - Horizon, b.x + Horizon);
            float x0 = Math.Min(b.x, hz) - 3f, x1 = Math.Max(b.x, hz) + 3f;
            float lo = b.y - 7f, hi = b.y + 5f;
            if (st.goal.hasY) { lo = Math.Min(lo, st.goal.y - 2f); hi = Math.Max(hi, st.goal.y + 3f); }
            hi = Math.Min(hi, lo + 32f);
            ps.job = new SliceJob((x0 + x1) * 0.5f, (lo + hi) * 0.5f, z, x1 - x0, hi - lo, 0.25f, NavGeometry.RayBudget);
            ps.x = b.x; ps.y = b.y; ps.ok = true;
            st.last = b;
            return ps;
        }

        // main thread: decide for the current state (no step) and apply the input
        static string Decide(MoveState st)
        {
            var b = Read(st);
            st.last = b;
            var code = st.loop.Step(b);
            Apply(st);
            return code;
        }

        // main thread after each logic step (Stepper stopCheck)
        static string Check(MoveState st)
        {
            try
            {
                var b = Read(st);
                st.last = b;
                var code = st.loop.Step(b);
                Apply(st);
                return code;
            }
            catch (Exception e)
            {
                st.error = e.GetType().Name + ": " + e.Message;
                try { AgentCore.Release(); } catch (Exception) { }
                return "error";
            }
        }

        static void Apply(MoveState st)
        {
            var c = st.loop.cmd;
            if (st.hasApplied && c.Same(st.applied)) return;
            AgentCore.ReleaseKeepGrip();
            if (!c.Neutral) AgentCore.Enqueue(new List<InputFrame> { new InputFrame { x = c.x, y = c.y, jump = c.jump, action = false, steps = 1 } }, true);
            st.applied = c; st.hasApplied = true;
        }

        static NavBody Body(MoveState st)
        {
            try { return Read(st); } catch (Exception) { return st.last; }
        }

        // main thread: the controlled body now (read fresh every step: the Boy is re-created on respawn)
        static NavBody Read(MoveState st)
        {
            var b = new NavBody();
            var ch = NavGame.Active();
            if (ch == null || !ch.isCharacterActive) return b;
            b.valid = true;
            b.dead = ch.isDead;
            float hh = NavGame.HalfHeight(ch);
            var boy = ch as Boy;
            if (boy == null)
            {
                var p3 = ch.pos3; var v3 = ch.velocity3;
                b.x = p3.x; b.y = p3.y - hh; b.vx = v3.x; b.vy = v3.y; b.facing = v3.x < 0f ? -1f : 1f;
                b.grounded = true;
                var hud = ch as Huddle;
                if (hud != null)
                {
                    b.huddle = true;
                    b.swimming = hud.isSwimming;
                    b.state = hud.state != null ? hud.state.GetType().Name : "";
                }
                return b;
            }
            var pos = boy.pos; var vel = boy.velocity;
            b.x = pos.x; b.y = pos.y - hh; b.vx = vel.x; b.vy = vel.y; b.facing = boy.facing;
            var state = boy.state;
            b.state = state != null ? state.GetType().Name : "";
            b.swimming = state is BoySwimState;
            b.onLadder = state is BoyLadderState;
            b.onRope = state is BoyRopeState;
            b.hanging = state is BoyGrabPlateauState;
            b.climbing = state is BoyReachPlateauState || state is BoyClimbDownState;
            b.sub = boy.specialBoyMode == Boy.SpecialBoyMode.InsideSub;
            b.special = boy.specialBoyMode == Boy.SpecialBoyMode.ClimbingSub || boy.specialBoyMode == Boy.SpecialBoyMode.ClimbingCliff;
            if (b.sub)
            {
                Vector3 sp;
                bool got = false;
                try { got = Perception.BodyPos(out sp); } catch (NotImplementedException) { sp = Vector3.zero; }
                if (got) { b.x = sp.x; b.y = sp.y - hh; }
            }
            var s = boy.status;
            if (s == null) return b;
            b.grounded = s.groundPlateau != null;
            b.obstructed = s.obstructionStatus != Boy.Status.ObstructionStatus.None;
            b.climbable = s.obstructionPlateau != null;
            b.touching = s.touchingObstruction;
            b.obstructionDist = s.obstructionDistance;
            b.inWater = s.water != null;
            if (s.edges != null && s.edges.Length >= 2)
            {
                var el = s.edges[0]; var er = s.edges[1];
                if (el != null) { b.edgeL = el.plateau != null; b.edgeDistL = el.distanceToBoy; b.edgeDropL = el.distanceToBottom; }
                if (er != null) { b.edgeR = er.plateau != null; b.edgeDistR = er.distanceToBoy; b.edgeDropR = er.distanceToBottom; }
            }
            if (st != null && st.targetColliders.Count > 0 && s.touchingObstruction && s.obstructionCollider != null) b.touchingTarget = st.targetColliders.Contains(s.obstructionCollider);
            return b;
        }
    }
}
