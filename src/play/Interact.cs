using System;
using System.Collections.Generic;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // Interact.cs (owner INPUT, the interact tool schema and Verbs frozen): one verb = one input macro built on the
    // game's own thresholds (report 02 sections 1.2, 2.2, 4): e.g. grab = action held facing a grabbable; climb = stick up
    // (looseDir.y > 0.5) at a ledge; drop = jump + stick down (y < -0.5); hat_detach = stick y < -0.8 then action within
    // 0.2 s; sub_boost = hold rawJump, release to fire. Each phase checks the body state and stops when it is wrong.
    // The target is an id from observe (default: the nearest object that offers the verb, Perception.Affordances);
    // approach walks there first with Navigator. Takes the control lane; inputs released at the end, except after a
    // successful grab, which keeps action pressed (the boy keeps holding) until interact release or the next act.
    // Macros and conditions: InteractMacros.cs (pure); body readings: ActProbe.cs. hat_detach plays the gesture; only
    // with allow_direct:true does a failed gesture fall back to MindHat.ForceDetach (a direct game call, never in a final
    // take; refused when the hat has disableDetach), and the result then lists it in direct_calls. The result adds
    // phases[] {name, steps, result} and, for lever / valve / button / lift / push / pull, what the target did in detail.
    // Grip: a successful grab sets AgentCore.Grip instead of a latch (act and move_to keep it); grab, push and pull keep
    // it, every other verb lets go first and warns grip_released. when waits for one wait_until condition before the
    // verb runs (Act.RunWhen).
    public static class Interact
    {
        public const string Owner = "INPUT";
        public static readonly string[] Verbs = { "grab", "release", "push", "pull", "climb", "climb_down", "drop", "ladder_up", "ladder_down", "ladder_off",
            "rope_jump", "swim_down", "swim_up", "surface", "hat_detach", "sub_boost", "sub_exit", "lever", "valve", "button", "lift", "sub_enter", "hat_wear", "throw" };
        public static readonly string[] Outcomes = { "done", "failed", "not_available", "out_of_reach", "died", "loading", "timeout", "cancelled" };

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("interact", "Interact", PlayShared.Play, Owner,
                "Do one thing with an object or the current body state: grab, release, push, pull, climb, climb_down, drop, ladder_up / ladder_down / ladder_off, rope_jump, swim_down / swim_up, surface, hat_detach, hat_wear, sub_enter, sub_boost, sub_exit, lever, valve, button, lift (lift doors), throw (huddle). target = id from observe (default nearest that offers the verb). Runs the game until the move completes or fails; returns state before / after, changes and an observation.",
                Schema.Obj().Enum("verb", "What to do (see objects[].actions in observe). grab keeps holding across later act / move_to calls; release lets go.", null, Verbs)
                    .Str("target", "Object id from observe (o12). Default: nearest object offering the verb.")
                    .Enum("dir", "Direction for push (toward = stick toward the target), pull (always pulls the target toward the body unless left / right; hatches and boards: along their axis), throw, rope_jump, ladder_off (left / right jumps off, else drops), sub_boost, lever (default: the way it can move), valve (right / down = clockwise).", "toward", "left", "right", "up", "down", "toward", "away")
                    .Num("seconds", "Duration for push, pull, button hold, lift, throw swing, swim_*, ladder_up / ladder_down, sub_boost charge, valve turning.", 1, 0.05, 10)
                    .Bool("approach", "Walk to the target first when it is out of reach.", true)
                    .Bool("allow_direct", "hat_detach: if the gesture fails, take the hat off with MindHat.ForceDetach (a direct game call; off keeps takes input-only).", false)
                    .Obj("when", Waits.WhenText, Waits.WhenSchema())
                    .Num("when_timeout_s", "Longest wait for when (wall clock s).", Waits.WhenDefaultS, 0.1, Waits.WhenMaxS)
                    .Enum("observe", "Observation in the result.", "concise", "concise", "none").Required("verb").Build(),
                Schema.Obj().Enum("outcome", "How it ended.", null, Outcomes).Str("verb", "Verb run.").Str("target", "Target id.")
                    .Str("state_before", "Body state before.").Str("state_after", "Body state after.").Int("steps", "Logic steps run.", 0, 100000)
                    .Str("detail", "Why it failed, or what happened.").Arr("direct_calls", "Direct game calls used instead of input (allow_direct).", Schema.Str(), 0, 4)
                    .Arr("warnings", "", Schema.Str(), 0, 20)
                    .Prop("changes", null, ChangeLog.Ref())
                    .Prop("observe", null, Observer.ConciseRef()).BuildOutput()).AlwaysLoad(), Handle);
        }

        sealed class Prep
        {
            public string notReady, reason = "", targetId, navTarget;
            public GameObject target;
            public float dx, dy, cdx, cdy;
            public bool needApproach;
            public Vector3 targetStart;
            public ActBody snap;
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            string verb = PlayShared.Str(a, "verb", null);
            if (verb == null || Array.IndexOf(Verbs, verb) < 0) return ToolResult.Fail(McpErr.BadArgs, "verb: one of " + string.Join(", ", Verbs));
            string spec = PlayShared.Str(a, "target", null);
            if (spec != null && spec.Trim().Length == 0) spec = null;
            string dir = PlayShared.Str(a, "dir", "toward");
            float seconds = (float)PlayShared.Num(a, "seconds", 1.0);
            bool approach = PlayShared.Bool(a, "approach", true);
            bool allowDirect = PlayShared.Bool(a, "allow_direct", false);
            bool wantObs = PlayShared.Str(a, "observe", "concise") != "none";
            var when = PlayShared.Obj(a, "when");
            double whenTimeout = PlayShared.Num(a, "when_timeout_s", Waits.WhenDefaultS);

            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return busy;

            var warnings = new List<string>();
            var phasesOut = new List<object>();
            var directCalls = new List<string>();
            var r = PlayShared.Env();
            long mark = Act.SafeMark();
            var scope = new ActScope();
            int steps = 0;
            string outcome = null, detail = null;
            bool latched = false;
            Prep p = null;
            ActBody start = null, end = null;
            InteractMacro macro = null;
            try
            {
                p = ctx.Main(() => Prepare(scope, verb, spec, warnings));
                start = p.snap;
                if (p.notReady != null)
                {
                    outcome = p.notReady == "loading" ? "loading" : "not_available";
                    detail = "not ready: " + p.reason;
                }
                if (outcome == null && when != null)
                {
                    string ws;
                    if (!Act.RunWhen(ctx, r, when, whenTimeout, 600, "interact", out ws))
                    {
                        var wb = r["when"] as Json.Obj;
                        outcome = ws == RunStop.Died ? "died" : ws == RunStop.Loading ? "loading" : ws == RunStop.Cancelled ? "cancelled" : "timeout";
                        detail = "when: " + (wb != null ? wb.Str("detail", ws) : ws);
                    }
                    else
                    {
                        var scope2 = new ActScope();
                        var p2 = ctx.Main(() => Prepare(scope2, verb, spec, warnings));
                        scope = scope2;
                        p = p2;
                        start = p.snap;
                        if (p.notReady != null) { outcome = p.notReady == "loading" ? "loading" : "not_available"; detail = "not ready after when: " + p.reason; }
                    }
                }
                if (outcome == null && AgentCore.Grip && Array.IndexOf(InteractMacros.GripVerbs, verb) < 0)
                {
                    if (verb == "throw") scope.dropGrip = true;
                    else
                    {
                        ctx.MainDo(() => AgentCore.SetGrip(false));
                        if (verb != "release" && verb != "drop") warnings.Add("grip_released: " + verb + " needs the action button free, so the grab from interact grab was let go");
                    }
                }
                if (outcome == null && p.needApproach)
                {
                    if (!approach)
                    {
                        outcome = "out_of_reach";
                        detail = p.targetId + " is " + PlayShared.F(Math.Abs(p.dx)) + " m away (reach " + PlayShared.F(InteractMacros.Reach(verb)) + " m): approach:true or move_to first";
                    }
                    else
                    {
                        Json.Obj mv = null;
                        try { mv = Navigator.MoveTo(ctx, new MoveRequest { target = p.navTarget, stop = "adjacent", gait = Math.Abs(p.dx) < 2.5f ? "walk" : "run", maxSteps = 900 }); }
                        catch (NotImplementedException) { warnings.Add("approach_unavailable: move_to is not built yet"); }
                        int ms = mv != null ? (int)mv.Num("steps", 0) : 0;
                        steps += ms;
                        string mo = mv != null ? mv.Str("outcome", "") : "";
                        phasesOut.Add(PhaseJson("approach", ms, mo));
                        if (mo == "died" || mo == "loading" || mo == "cancelled" || mo == "timeout") { outcome = mo; detail = "approach: " + mo; }
                        else if (mo != "arrived") { outcome = "out_of_reach"; detail = "approach " + mo + (mv != null && mv.Str("reason", "") != "" ? ": " + mv.Str("reason", "") : ""); }
                        else
                        {
                            var p2 = ctx.Main(() => Prepare(scope, verb, p.navTarget, warnings));
                            p2.targetId = p.targetId;
                            p2.targetStart = p.targetStart;
                            p = p2;
                        }
                    }
                }
                if (outcome == null)
                {
                    var env = ctx.Main(() => EnvOf(scope, verb, dir, seconds, p));
                    macro = InteractMacros.Build(env);
                    if (macro.notAvailable != null) { outcome = "not_available"; detail = macro.notAvailable; }
                }
                if (outcome == null)
                {
                    MacroPhase failed = null;
                    foreach (var ph in macro.phases)
                    {
                        string res = RunPhase(ctx, scope, ph, start, ref steps, warnings, phasesOut, verb);
                        if (res == "ok") continue;
                        if (res == "fail") { failed = ph; break; }
                        outcome = res;
                        detail = "during " + ph.name;
                        break;
                    }
                    if (outcome == null && failed != null && failed.fallback == "force_detach" && !allowDirect)
                    {
                        outcome = "failed";
                        detail = failed.failText + "; allow_direct:true would take the hat off with MindHat.ForceDetach (a direct game call, not for final takes)";
                    }
                    else if (outcome == null && failed != null && failed.fallback == "force_detach")
                    {
                        string fb = ForceDetach(ctx, scope, start, ref steps, warnings, phasesOut);
                        if (fb == "ok") { outcome = "done"; detail = "the detach gesture did not work, MindHat.ForceDetach took the hat off (allow_direct)"; directCalls.Add("MindHat.ForceDetach"); }
                        else if (fb == "fail") { outcome = "failed"; detail = failed.failText; directCalls.Add("MindHat.ForceDetach"); }
                        else if (fb.StartsWith("refused:", StringComparison.Ordinal)) { outcome = "failed"; detail = failed.failText + "; " + fb.Substring(8); }
                        else { outcome = fb; directCalls.Add("MindHat.ForceDetach"); }
                    }
                    else if (outcome == null && failed != null) { outcome = "failed"; detail = failed.failText; }
                    if (outcome == null)
                    {
                        outcome = "done";
                        if (macro.latchOnSuccess)
                        {
                            ctx.MainDo(() => AgentCore.SetGrip(true));
                            latched = true;
                        }
                    }
                }
                end = SafeTake(ctx, scope);
            }
            finally
            {
                if (outcome == null || outcome == "cancelled" || outcome == "died" || outcome == "loading") AgentCore.Release();
                else AgentCore.ReleaseKeepGrip();
            }

            string d2 = TargetDetail(ctx, scope, verb, p, start, end, outcome);
            if (outcome == "done" && d2 != null && d2.StartsWith("failed:", StringComparison.Ordinal)) { outcome = "failed"; d2 = d2.Substring(7); }
            if (macro != null && macro.note != "" && outcome == "done" && detail == null) detail = macro.note;
            if (d2 != null) detail = detail == null ? d2 : detail + "; " + d2;
            if (latched && macro != null && macro.note != "" && !warnings.Contains(macro.note)) warnings.Add(macro.note);

            r["outcome"] = outcome;
            r["verb"] = verb;
            if (p != null && p.targetId != null) r["target"] = p.targetId;
            if (start != null) r["state_before"] = start.has ? start.StateKey : "none";
            if (end != null) r["state_after"] = end.has ? end.StateKey : "none";
            r["steps"] = (double)steps;
            if (detail != null) r["detail"] = detail;
            if (directCalls.Count > 0) r["direct_calls"] = PlayShared.L(directCalls);
            r["phases"] = phasesOut;
            if (AgentCore.Hijacked && !warnings.Contains("input_hijacked")) warnings.Add("input_hijacked");
            Act.Tail(r, ctx, mark, wantObs, "interact", warnings);
            return ToolResult.Ok(r);
        }

        static ActBody SafeTake(CallContext ctx, ActScope scope)
        {
            try { return ctx.Main(() => ActProbe.Take(scope)); }
            catch (McpException) { return null; }
        }

        static Json.Obj PhaseJson(string name, int steps, string result)
        {
            var o = new Json.Obj();
            o["name"] = name;
            o["steps"] = (double)steps;
            o["result"] = result;
            return o;
        }

        // main thread
        static Prep Prepare(ActScope scope, string verb, string spec, List<string> warnings)
        {
            var p = new Prep();
            string w = ActProbe.EnsureAgent();
            if (w != null && !warnings.Contains(w)) warnings.Add(w);
            string reason;
            if (!ActProbe.Ready(out reason))
            {
                if (ActStops.SoftNotReady(reason)) warnings.Add("not_ready: " + reason);
                else { p.notReady = ActStops.NotReadyOutcome(reason); p.reason = reason; }
            }
            p.snap = ActProbe.Take(scope);
            if (p.notReady != null) return p;
            GameObject go = null;
            if (spec != null)
            {
                go = Resolve(spec);
                p.targetId = IdOf(go, spec);
                p.navTarget = spec;
            }
            else if (InteractMacros.WantsTarget(verb) && p.snap.has)
            {
                try
                {
                    foreach (var o in Perception.Objects(new Vector3(p.snap.x, p.snap.y, p.snap.z), 6f, 16))
                    {
                        if (o.go == null || !o.actions.Contains(verb)) continue;
                        go = o.go;
                        p.targetId = o.id ?? IdOf(go, null);
                        p.navTarget = o.id ?? ("#" + go.GetInstanceID());
                        break;
                    }
                }
                catch (Exception) { }
                if (go == null && (verb == "lever" || verb == "valve" || verb == "button" || verb == "lift")) warnings.Add("no " + verb + " target found within 6 m: trying in place");
            }
            if (go == null) return p;
            scope.target = go;
            ActProbe.AttachDevice(scope, go);
            p.target = go;
            p.targetStart = go.transform.position;
            Geometry(go, p);
            float reach = InteractMacros.Reach(verb);
            p.needApproach = reach > 0f && Math.Abs(p.dx) > reach;
            p.snap = ActProbe.Take(scope);
            return p;
        }

        // main thread: Observer.ResolveTarget, or an exact hierarchy path while OBSERVE is not built
        internal static GameObject Resolve(string spec)
        {
            try { return Observer.ResolveTarget(spec); }
            catch (NotImplementedException)
            {
                var go = GameObject.Find(spec);
                if (go == null) throw new McpException(McpErr.NotFound, "target " + spec + " not found", "observe is not built yet: give an exact hierarchy path");
                return go;
            }
        }

        static string IdOf(GameObject go, string spec)
        {
            try { return Observer.IdFor(go, "o"); }
            catch (Exception) { return spec ?? ("#" + go.GetInstanceID()); }
        }

        static void Geometry(GameObject go, Prep p)
        {
            var body = new Vector3(p.snap.x, p.snap.y, p.snap.z);
            var center = go.transform.position;
            float best = float.MaxValue, bdx = center.x - body.x, bdy = center.y - body.y;
            bool any = false;
            var bounds = new Bounds(center, Vector3.zero);
            foreach (var c in go.GetComponentsInChildren<Collider>())
            {
                if (c == null || !c.enabled) continue;
                var q = c.ClosestPointOnBounds(body);
                float dx = q.x - body.x, dy = q.y - body.y;
                if (Math.Abs(dx) < best) { best = Math.Abs(dx); bdx = dx; bdy = dy; }
                if (!any) { bounds = c.bounds; any = true; } else bounds.Encapsulate(c.bounds);
            }
            if (any) center = bounds.center;
            p.dx = bdx; p.dy = bdy;
            p.cdx = center.x - body.x; p.cdy = center.y - body.y;
        }

        // main thread
        static MacroEnv EnvOf(ActScope scope, string verb, string dir, float seconds, Prep p)
        {
            var s = ActProbe.Take(scope);
            var e = new MacroEnv
            {
                verb = verb, dir = dir, seconds = seconds, kind = s.kind, state = s.state, special = s.special,
                grounded = s.grounded, inWater = s.inWater, atSurface = s.atSurface, holding = s.holding, mindControlling = s.mindControlling,
                digital = AgentCore.Digital, obstructed = s.obstructed, subOnSurface = s.subOnSurface, dead = s.dead || !s.has,
                facing = s.facing, edgeLeft = s.edgeLeft, edgeRight = s.edgeRight
            };
            if (p.target != null)
            {
                p.snap = s;
                Geometry(p.target, p);
                e.hasTarget = true;
                e.targetDx = Math.Abs(p.cdx) > 0.05f ? p.cdx : p.dx;
                e.targetDy = p.cdy;
            }
            e.device = scope.deviceKind;
            e.deviceFace = scope.deviceFace;
            e.extraDeadZone = s.extraDeadZone;
            e.leverDir = ActProbe.LeverDir(scope);
            e.valveAngle = ActProbe.ValveAngle(scope);
            return e;
        }

        // worker thread: "ok", "fail" or an outcome (died, loading, cancelled, timeout, failed)
        static string RunPhase(CallContext ctx, ActScope scope, MacroPhase ph, ActBody start, ref int steps, List<string> warnings, List<object> phasesOut, string verb)
        {
            if (ctx.Cancelled) return "cancelled";
            int left = InteractMacros.MaxTotalSteps - steps;
            if (left <= 0) return "timeout";
            var pre = ctx.Main(() => ActProbe.Take(scope));
            if (ph.skipUnless != null && !ph.skipUnless.Eval(pre, start)) { phasesOut.Add(PhaseJson(ph.name, 0, "skipped")); return "ok"; }
            if (ph.minSteps == 0 && ph.done != null && ph.done.Eval(pre, start)) { phasesOut.Add(PhaseJson(ph.name, 0, "already")); return "ok"; }
            if (ph.fail != null && ph.fail.Eval(pre, start)) { phasesOut.Add(PhaseJson(ph.name, 0, "fail")); return "fail"; }
            if (scope.dropGrip) { AgentCore.ReplaceDropGrip(ph.frames, true); scope.dropGrip = false; }
            else AgentCore.Replace(ph.frames, true);
            long s0 = pre.step;
            var req = new RunRequest
            {
                maxSteps = Math.Min(ph.maxSteps, left),
                timeoutMs = Math.Max(1000, Math.Min(50000, ctx.RemainingMs - 2500)),
                stopOnDeath = true,
                stopOnLoad = true,
                label = "interact " + verb + ": " + ph.name,
                stopCheck = () =>
                {
                    var now = ActProbe.Take(scope);
                    if (ph.fail != null && now.step > s0 && ph.fail.Eval(now, start)) return "fail";
                    if (ph.done != null && now.step - s0 >= Math.Max(1, ph.minSteps) && ph.done.Eval(now, start)) return "done";
                    return null;
                }
            };
            var rr = Stepper.Run(ctx, req);
            steps += rr.steps;
            foreach (var w in rr.warnings) if (!warnings.Contains(w)) warnings.Add(w);
            string result;
            switch (rr.stop)
            {
                case RunStop.Died: result = "died"; break;
                case RunStop.Loading: result = "loading"; break;
                case RunStop.Cancelled: result = "cancelled"; break;
                case RunStop.Timeout: result = "timeout"; break;
                case RunStop.Stalled: result = "failed"; warnings.Add("stalled: the game did not advance during " + ph.name + " (paused or a menu open?)"); break;
                case RunStop.Condition: result = rr.detail == "fail" ? "fail" : "ok"; break;
                default: result = ph.needDone ? "fail" : "ok"; break;
            }
            phasesOut.Add(PhaseJson(ph.name, rr.steps, result == "ok" ? (rr.stop == RunStop.Condition ? "done" : "time") : result));
            return result;
        }

        static string ForceDetach(CallContext ctx, ActScope scope, ActBody start, ref int steps, List<string> warnings, List<object> phasesOut)
        {
            string refused = ctx.Main(() =>
            {
                var boy = ScriptGlobals.boy;
                if (boy == null) return "refused: no boy";
                MindHat hat = null;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(typeof(MindHat)))
                {
                    var h = o as MindHat;
                    if (h != null && h.GetBoyInHat() == boy) { hat = h; break; }
                }
                if (hat == null) return "refused: the hat the boy wears was not found";
                if (hat.disableDetach) return "refused: this hat cannot be taken off here (disableDetach, scripted)";
                hat.ForceDetach(false);
                return null;
            });
            if (refused != null) { phasesOut.Add(PhaseJson("force_detach", 0, "refused")); return refused; }
            warnings.Add("hat_force_detach: MindHat.ForceDetach was used");
            var wait = new MacroPhase { name = "force_detach", minSteps = 1, maxSteps = 120, done = MacroCond.Of(MacroCondKind.NotMindControlling), needDone = true, failText = "still in the hat after ForceDetach" };
            wait.frames.Add(new InputFrame { steps = 1 });
            string res = RunPhase(ctx, scope, wait, start, ref steps, warnings, phasesOut, "hat_detach");
            return res;
        }

        static readonly string[] IdleDeviceStates = { "None", "Idle", "Grab" };

        // worker thread: what the target did ("failed:..." turns a done lever into failed)
        static string TargetDetail(CallContext ctx, ActScope scope, string verb, Prep p, ActBody start, ActBody end, string outcome)
        {
            if (p == null || p.target == null) return null;
            if (verb == "push" || verb == "pull")
            {
                float moved;
                try
                {
                    var go = p.target;
                    var s0 = p.targetStart;
                    moved = ctx.Main(() => go != null ? Vector3.Distance(go.transform.position, s0) : -1f);
                }
                catch (McpException) { return null; }
                if (moved < 0f) return "target destroyed";
                return "target moved " + PlayShared.F(moved) + " m";
            }
            if (scope.device == null || (verb != "lever" && verb != "valve")) return null;
            bool moved2 = false;
            foreach (var s in scope.devicesSeen) if (Array.IndexOf(IdleDeviceStates, s) < 0) moved2 = true;
            string states = scope.devicesSeen.Count > 0 ? string.Join(" -> ", scope.devicesSeen.ToArray()) : "unknown";
            string value = "";
            if (start != null && end != null && !float.IsNaN(start.deviceValue) && !float.IsNaN(end.deviceValue))
            {
                value = ", " + (verb == "valve" ? "rotation " : "handle ") + PlayShared.F(start.deviceValue) + " -> " + PlayShared.F(end.deviceValue);
                if (Math.Abs(end.deviceValue - start.deviceValue) > 0.02f) moved2 = true;
            }
            string text = verb + " states " + states + value;
            if (outcome == "done" && !moved2 && scope.devicesSeen.Count > 0) return "failed:" + text + ": it did not move, try the other dir";
            return text;
        }
    }
}
