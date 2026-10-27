using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // Waits.cs (owner TIME, the wait_until schema frozen): run the game (step mode) or watch it (realtime) until one of
    // the conditions holds. A timeout is not an error: ok true, satisfied false, timed_out true. Conditions are compiled
    // to main-thread checks (Compile) and evaluated after every step. The call never holds the main thread: it loops on
    // the worker through Stepper.Run / CallContext.WaitUntil. Condition job waits on another job (e.g. a detached load)
    // without taking the control lane.
    // Rules: every item of any has exactly one key (WaitPlan.KeyOf); ids and paths resolve strictly through
    // Observer.ResolveTarget before the game runs (stale_id / not_found / ambiguous come back as errors); a wait with
    // any non-job condition takes the control lane (busy otherwise, unless a job condition lets it watch without the
    // lane); dead / alive / ready / loaded / screen / checkpoint_changed / controllable / job waits run through deaths and
    // loads, the others stop there (stop died / loading, satisfied false). steps / seconds raise max_steps to their own
    // length. A condition that already holds before the game runs ends the wait at once (waited_steps 0); ready during
    // a run must hold on two consecutive steps. Distances are in the x/y plane from the body (Perception.BodyPos) to the target's transform. Body facts
    // come from Perception.Body(false) (OBSERVE's definitions); while it is not built, a fallback reads the boy directly.
    // signal installs a tap connection at the front of that SignalOut's private connection list (the core EventMonitor
    // recipe, named "InsideDev.tap" so EventMonitor's listing skips it), removed when the wait ends or, failing that,
    // 5 s after its last check (tick "waits").
    // Game APIs checked against the decompiled code: SignalManager.signalOuts (FP/SignalManager.cs:29), SignalOut
    // isActive / gameObject / debugName / private connections (FP/SignalOut.cs:5-16, set FP/SignalManager.cs:135-139),
    // SignalConnection fields and RaiseEvent (FP/SignalConnection.cs:16-85), SignalIn (FP/SignalIn.cs:6-16), FastList
    // Insert / Remove (FP/FastList.cs:123,137); PlayMakerFSM FsmName / ActiveStateName / FsmStates (PM/PlayMakerFSM.cs:50,
    // 76,88), FsmState.Name (PM/HutongGames.PlayMaker/FsmState.cs:95); Savegame.GetCurrentSavepoint (FP/Savegame.cs:377);
    // ScriptGlobals.mainCharacter (FP/ScriptGlobals.cs:17); Character isDead / isCharacterActive / pos3
    // (FP/Character.cs:29,33,35); Boy.state / status.groundPlateau (FP/Boy.cs:355,357,39); Huddle.state (FP/Huddle.cs:291);
    // GameManager.IsPlaying / IsLoading / IsPaused (FP/GameManager.cs:115,202,217) and fader (:95), CameraFader.IsDone,
    // GameInput.IsEnabled, TitleManager.IsShowing (see Stepper.cs); SubsceneCullingManager.instance /
    // AllCullersFinished (FP/SubsceneCullingManager.cs:76,259); PreAwakeManager.PendingPreawake (FP/PreAwakeManager.cs:38);
    // LogicManager.stepCount (FP/LogicManager.cs:25).
    public static class Waits
    {
        public const string Owner = "TIME";
        public const string TapName = "InsideDev.tap";
        public const int TapExpireMs = 5000, HazardEvery = 4, ReadyFrames = 2;
        public static readonly string[] Keys = WaitPlan.AllKeys;

        static readonly string[] Attached = { "Ladder", "Rope", "GrabPlateau", "ReachPlateau", "ClimbDown", "Swim", "SwimGrab", "Grab", "CustomAnim", "Ragdoll", "Slide" };

        public static SchemaObj ConditionSchema()
        {
            return Schema.Obj()
                .Int("steps", "After this many logic steps.", 1, 36000).Num("seconds", "After this much game time.", 0.05, 600)
                .Bool("grounded", "Body on the ground.").Bool("airborne", "Body in the air.").Bool("controllable", "Input reaches the body.")
                .Bool("ready", "Load finished and playable (PlayLevels.Ready).").Bool("loaded", "No load in progress.")
                .Bool("checkpoint_changed", "A new savepoint was reached.").Bool("dead", "Body died.").Bool("alive", "Body alive again.")
                .Str("state", "Body state name (Run, Jump, Ladder, Swim, ...).").Enum("screen", "Screen value.", null, Observer.Screens)
                .Obj("near", "Body within distance of an object.", Schema.Obj().Str("target", "Object id or path.").Num("within", "Metres.", 1, 0.1, 100).Required("target"))
                .Obj("far_from", "Body farther than distance from an object or hazard.", Schema.Obj().Str("target", "Id or path.").Num("beyond", "Metres.", 5, 0.1, 200).Required("target"))
                .Str("hazard_safe", "Hazard id: its threat is over or the body is in cover.")
                .Obj("hazard_eta", "A hazard's eta_s is at least min_s (the shockwave: that many seconds before the next blow).", Schema.Obj().Str("target", "Hazard id (h3).").Num("min_s", "Seconds.", 4, 0, 600).Required("target"))
                .Obj("music_time", "Music position in [from, to) s, folded into cycle (0 = the running shockwave's cycle, else not folded); from > to wraps.", Schema.Obj().Num("from", "s.", 0, 1e5).Num("to", "s.", 0, 1e5).Num("cycle", "Cycle length (s), 0 = auto.", 0, 0, 1e4).Required("from", "to"))
                .Obj("fsm_state", "An FSM enters a state.", Schema.Obj().Str("object", "Object id or path.").Str("fsm", "FSM name.").Str("state", "State name.").Required("object", "state"))
                .Obj("signal", "A signal output fires.", Schema.Obj().Str("object", "Object id or path.").Str("output", "Signal output name.").Required("object", "output"))
                .Obj("object_active", "An object becomes active / inactive.", Schema.Obj().Str("target", "Id or path.").Bool("active", "Wanted state.", true).Required("target"))
                .Str("job", "Job id (j3): it finished.");
        }

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("wait_until", "Wait until", PlayShared.Both, Owner,
                "Let the game run until one condition in any holds (steps, seconds, grounded, controllable, ready, checkpoint_changed, dead, alive, state, screen, near, far_from, hazard_safe, hazard_eta, music_time, fsm_state, signal, object_active, job), then freeze. A timeout is not an error: check satisfied. Use after loads, respawns and timed hazards; to act right after a phase use act / move_to / interact {when}.",
                Schema.Obj().Arr("any", "Conditions; the first that holds ends the wait. Each item has exactly one key.", Schema.ObjOf(ConditionSchema()), 1, 8)
                    .Num("timeout_s", "Wall-clock limit.", 20, 0.1, 50).Int("max_steps", "Game-step limit.", 3600, 1, 36000)
                    .Enum("then", "step mode: freeze after (default) or keep running.", "freeze", "freeze", "run")
                    .Enum("observe", "Observation in the result.", "concise", "concise", "none").Required("any").Build(),
                Schema.Obj().Bool("satisfied", "A condition held.").Int("which", "Index in any of the condition that held (-1 none).", -1, 7)
                    .OutObj("condition", "That condition.", Schema.Obj().Open()).Bool("timed_out", "timeout_s or max_steps ran out.")
                    .Int("waited_steps", "Logic steps run.", 0, 1000000).Num("elapsed_ms", "Wall-clock ms.", 0, 1e7)
                    .OutObj("observed", "The watched values at the end.", Schema.Obj().Open())
                    .OutObj("job", "Job state (condition job).", McpExt.DevToolsProvider.JobSchema())
                    .Arr("warnings", "", Schema.Str(), 0, 20)
                    .Prop("changes", null, ChangeLog.Ref())
                    .Prop("observe", null, Observer.ConciseRef()).BuildOutput()).AlwaysLoad(), Handle);
            PlayRegistry.OnTick("waits", TickTaps);
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            var any = PlayShared.List(a, "any");
            string err;
            var keys = WaitPlan.KeysOf(any, out err);
            if (keys == null) return ToolResult.Fail(McpErr.BadArgs, err, "each item of any is one condition, e.g. {grounded:true} or {near:{target:\"o12\", within:1}}");
            double timeoutS = PlayShared.Num(a, "timeout_s", 20);
            int maxSteps = PlayShared.Int(a, "max_steps", 3600);
            bool leave = PlayShared.Str(a, "then", "freeze") == "run";
            bool wantObserve = PlayShared.Str(a, "observe", "concise") != "none";
            var warnings = new List<string>();
            int ownSteps = 0;
            for (int i = 0; i < keys.Count; i++)
            {
                var c = any[i] as Json.Obj;
                if (keys[i] == "steps") ownSteps = Math.Max(ownSteps, PlayShared.Int(c, "steps", 0));
                if (keys[i] == "seconds") ownSteps = Math.Max(ownSteps, WaitPlan.StepsFor(PlayShared.Num(c, "seconds", 0)));
            }
            if (ownSteps > maxSteps) { warnings.Add("max_steps: raised from " + maxSteps + " to " + ownSteps + " to cover the steps / seconds condition"); maxSteps = ownSteps; }
            bool watch = !WaitPlan.NeedsLane(keys);
            if (!watch)
            {
                ToolResult busy;
                var job = ctx.BeginJob(McpLanes.Control, out busy);
                if (job == null)
                {
                    if (!keys.Contains("job")) return busy ?? ToolResult.Fail(McpErr.Busy, "the control lane is busy", "wait for the running action, or call restore");
                    watch = true;
                    warnings.Add("watch_only: the control lane is busy, so this wait does not run the game; it only watches while that job runs it");
                }
            }
            var set = new WaitSet();
            try
            {
                for (int i = 0; i < any.Count; i++) set.conds.Add(CompileOne(any[i] as Json.Obj, keys[i], i, ctx));
                long mark = Stepper.TryMark();
                string stop, detail;
                int steps;
                double elapsed;
                string already = ctx.Main(() => set.Check());
                if (already != null)
                {
                    stop = RunStop.Condition; detail = already; steps = 0; elapsed = 0;
                    if (leave) ctx.MainDo(() => { if (Stepper.Mode == StepMode.Step) Stepper.Unfreeze(); });
                }
                else if (!watch)
                {
                    bool aware = WaitPlan.LoadAwareAny(keys);
                    var rr = Stepper.Run(ctx, new RunRequest
                    {
                        maxSteps = maxSteps, timeoutMs = (int)Math.Round(timeoutS * 1000), stopCheck = set.Check, stopOnDeath = !aware, stopOnLoad = !aware,
                        leaveRunning = leave, label = "wait_until"
                    });
                    stop = rr.stop; detail = rr.detail; steps = rr.steps; elapsed = rr.elapsedMs;
                    warnings.AddRange(rr.warnings);
                }
                else Watch(ctx, set, (int)Math.Round(timeoutS * 1000), maxSteps, out stop, out detail, out steps, out elapsed);
                object obs = null;
                Json.Obj observed = null;
                try
                {
                    observed = ctx.Main(() =>
                    {
                        var ob = set.Observed();
                        if (wantObserve) obs = Stepper.TryConcise("wait_until");
                        return ob;
                    });
                }
                catch (McpException) { }
                if (wantObserve && obs == null) warnings.Add("observe: not available");
                var o = WaitPlan.Result(set, any, stop, detail, steps, elapsed, observed, warnings);
                for (int i = 0; i < keys.Count; i++)
                {
                    if (keys[i] != "job") continue;
                    var j = ctx.FindJob(PlayShared.Str(any[i] as Json.Obj, "job", ""));
                    if (j != null) o["job"] = j.Describe();
                    break;
                }
                var ch = Stepper.TrySince(mark);
                if (ch != null) o["changes"] = ch;
                if (obs != null) o["observe"] = obs;
                return ToolResult.Ok(o);
            }
            finally
            {
                if (set.conds.Count > 0)
                {
                    try { ctx.MainDo(set.Cleanup, 2000); } catch { }
                }
            }
        }

        static void Watch(CallContext ctx, WaitSet set, int timeoutMs, int maxSteps, out string stop, out string detail, out int steps, out double elapsed)
        {
            var t0 = DateTime.UtcNow;
            long start = Stepper.Step;
            int budget = Math.Max(50, Math.Min(timeoutMs, ctx.RemainingMs - StepperCore.ReplyReserveMs));
            stop = RunStop.Timeout;
            detail = "wall-clock limit";
            while (true)
            {
                var slice = DateTime.UtcNow;
                bool done = ctx.WaitUntil(() => set.Holds() || Stepper.Step - start >= maxSteps, StepperCore.SliceMs, 1);
                if (!done && (DateTime.UtcNow - slice).TotalMilliseconds < 5 && !ctx.Cancelled) System.Threading.Thread.Sleep(5);
                elapsed = (DateTime.UtcNow - t0).TotalMilliseconds;
                long ran = Stepper.Step - start;
                if (set.Which >= 0) { stop = RunStop.Condition; detail = set.What ?? ""; break; }
                if (ran >= maxSteps) { stop = RunStop.Steps; detail = ran + " steps"; break; }
                if (ctx.Cancelled) { stop = RunStop.Cancelled; detail = "cancelled"; break; }
                if (elapsed >= budget) { stop = RunStop.Timeout; detail = "wall-clock limit"; break; }
                try { ctx.Progress(elapsed, budget, "wait_until: watching (" + ran + " steps)"); } catch { }
            }
            steps = (int)Math.Max(0, Math.Min(int.MaxValue, Stepper.Step - start));
            elapsed = (DateTime.UtcNow - t0).TotalMilliseconds;
        }

        // worker thread: one condition object -> a main-thread check returning null (not yet) or a short description of
        // what held; error = why the condition is invalid (unknown id, two keys, ...)
        public static Func<string> Compile(Json.Obj condition, CallContext ctx, out string error)
        {
            string key = WaitPlan.KeyOf(condition, 0, out error);
            if (key == null) return null;
            try { return CompileOne(condition, key, 0, ctx).check; }
            catch (McpException e) { error = e.code + ": " + e.Message + (string.IsNullOrEmpty(e.hint) ? "" : " (" + e.hint + ")"); return null; }
        }

        // worker thread
        internal static WaitCond CompileOne(Json.Obj cond, string key, int index, CallContext ctx)
        {
            if (key == "job") return JobCond(cond, index, ctx);
            return ctx.Main(() => CompileMain(cond, key, index));
        }

        static WaitCond JobCond(Json.Obj cond, int index, CallContext ctx)
        {
            string id = PlayShared.Str(cond, "job", "");
            var j = ctx.FindJob(id);
            if (j == null) throw new McpException(McpErr.NotFound, "any[" + index + "].job: no job " + id, "list jobs with the jobs tool (dev) or use the id from the result that started it");
            var c = New(cond, "job", index);
            c.check = () => j.State != McpJobState.Running ? "job " + j.Id + " " + j.State : null;
            c.observe = () => j.State;
            return c;
        }

        static WaitCond New(Json.Obj cond, string key, int index) { return new WaitCond { index = index, key = key, source = cond }; }

        // main thread
        static WaitCond CompileMain(Json.Obj cond, string key, int index)
        {
            var c = New(cond, key, index);
            object v = cond[key];
            string at = "any[" + index + "]." + key;
            switch (key)
            {
                case "steps":
                case "seconds":
                    {
                        int n = key == "steps" ? PlayShared.Int(cond, "steps", 1) : WaitPlan.StepsFor(PlayShared.Num(cond, "seconds", 0));
                        long start = LogicManager.stepCount;
                        string label = key == "steps" ? n + " steps" : PlayShared.F(PlayShared.Num(cond, "seconds", 0)) + " s (" + n + " steps)";
                        c.check = () => LogicManager.stepCount - start >= n ? "after " + label : null;
                        c.observe = () => key == "steps" ? (object)(double)(LogicManager.stepCount - start) : PlayShared.R((LogicManager.stepCount - start) / WaitPlan.StepsPerSecond);
                        break;
                    }
                case "grounded":
                case "airborne":
                    {
                        bool want = v is bool && (bool)v;
                        bool air = key == "airborne";
                        c.check = () =>
                        {
                            var b = Probe(true);
                            if (!b.has || !b.groundedKnown) return null;
                            bool now = air ? Airborne(b) : b.grounded;
                            return now == want ? key + " " + (want ? "true" : "false") + " (" + b.state + ")" : null;
                        };
                        c.observe = () => { var b = Probe(true); return b.groundedKnown ? (object)(air ? Airborne(b) : b.grounded) : "unknown"; };
                        break;
                    }
                case "controllable":
                    {
                        bool want = v is bool && (bool)v;
                        c.check = () => Probe(true).controllable == want ? "controllable " + (want ? "true" : "false") : null;
                        c.observe = () => Probe(true).controllable;
                        break;
                    }
                case "dead":
                    {
                        bool want = v is bool && (bool)v;
                        c.check = () => { var b = Probe(false); return b.has && b.dead == want ? (want ? "the body is dead" : "the body is not dead") : null; };
                        c.observe = () => Probe(false).dead;
                        break;
                    }
                case "alive":
                    {
                        bool want = v is bool && (bool)v;
                        c.check = () => Alive() == want ? (want ? "alive and playing" : "not alive") : null;
                        c.observe = () => Alive();
                        break;
                    }
                case "ready":
                    {
                        bool want = v is bool && (bool)v;
                        int run = 0, calls = 0;
                        c.check = () =>
                        {
                            string why;
                            bool first = calls++ == 0;
                            bool r = Ready(out why);
                            run = r ? run + 1 : 0;
                            if (want) return run >= ReadyFrames || (r && first) ? "ready" : null;
                            return r ? null : "not ready: " + why;
                        };
                        c.observe = () => { string why; bool r = Ready(out why); return r ? (object)true : "no: " + why; };
                        break;
                    }
                case "loaded":
                    {
                        bool want = v is bool && (bool)v;
                        c.check = () => !LoadingNow() == want ? (want ? "no load in progress" : "a load is in progress") : null;
                        c.observe = () => !LoadingNow();
                        break;
                    }
                case "checkpoint_changed":
                    {
                        bool want = v is bool && (bool)v;
                        string start = SavepointNow();
                        c.check = () => { string now = SavepointNow(); return (now != start) == want ? "savepoint " + start + " -> " + now : null; };
                        c.observe = () => SavepointNow();
                        break;
                    }
                case "state":
                    {
                        string want = (v as string ?? "").Trim();
                        if (want.Length == 0) throw new McpException(McpErr.BadArgs, at + ": empty state name");
                        c.check = () => { var b = Probe(true); return b.has && SameState(b, want) ? "state " + b.state : null; };
                        c.observe = () => Probe(true).state;
                        break;
                    }
                case "screen":
                    {
                        string want = v as string ?? "";
                        c.check = () => { string s = ScreenNow(); return s == want ? "screen " + s : null; };
                        c.observe = () => ScreenNow();
                        break;
                    }
                case "near":
                case "far_from":
                    {
                        var o = v as Json.Obj;
                        string spec = PlayShared.Str(o, "target", "");
                        bool near = key == "near";
                        double lim = near ? PlayShared.Num(o, "within", 1) : PlayShared.Num(o, "beyond", 5);
                        var go = Target(spec, at + ".target");
                        c.check = () =>
                        {
                            if (go == null) return near ? null : spec + " is gone";
                            double d = Dist(go);
                            if (d < 0) return null;
                            if (near) return d <= lim ? "within " + PlayShared.F(d) + " m of " + spec : null;
                            return d > lim ? PlayShared.F(d) + " m from " + spec : null;
                        };
                        c.observe = () => { if (go == null) return "gone"; double d = Dist(go); return d < 0 ? (object)"no body" : PlayShared.R(d); };
                        break;
                    }
                case "hazard_safe":
                    {
                        string spec = v as string ?? "";
                        var go = Target(spec, at);
                        if (!PerceptionBuilt()) throw new McpException(McpErr.NotBuilt, at + ": needs the hazard view of observe, which is not built yet");
                        int calls = 0;
                        string last = null;
                        c.check = () =>
                        {
                            if (calls++ % HazardEvery != 0) return last;
                            last = HazardSafe(go, spec);
                            return last;
                        };
                        c.observe = () => HazardSafe(go, spec) ?? "threat";
                        break;
                    }
                case "hazard_eta":
                    {
                        var o = v as Json.Obj;
                        string spec = PlayShared.Str(o, "target", "");
                        double min = PlayShared.Num(o, "min_s", 4);
                        var go = Target(spec, at + ".target");
                        if (!PerceptionBuilt()) throw new McpException(McpErr.NotBuilt, at + ": needs the hazard view of observe, which is not built yet");
                        int calls = 0;
                        string last = null;
                        c.check = () =>
                        {
                            if (calls++ % 2 != 0) return last;
                            last = HazardEta(go, spec, min);
                            return last;
                        };
                        c.observe = () => { double e = EtaNow(go); return e < -1.5 ? (object)"gone" : PlayShared.R(e, 1); };
                        break;
                    }
                case "music_time":
                    {
                        var o = v as Json.Obj;
                        double from = PlayShared.Num(o, "from", 0), to = PlayShared.Num(o, "to", 0), cycle = PlayShared.Num(o, "cycle", 0);
                        c.check = () =>
                        {
                            double ph = MusicPhaseNow(cycle);
                            return WaitPlan.InWindow(ph, from, to) ? "music " + PlayShared.F(PlayShared.R(ph)) + " s in [" + PlayShared.F(from) + ", " + PlayShared.F(to) + ")" : null;
                        };
                        c.observe = () => PlayShared.R(MusicPhaseNow(cycle));
                        break;
                    }
                case "fsm_state":
                    FsmCond(c, v as Json.Obj, at);
                    break;
                case "signal":
                    SignalCond(c, v as Json.Obj, at);
                    break;
                case "object_active":
                    {
                        var o = v as Json.Obj;
                        string spec = PlayShared.Str(o, "target", "");
                        bool want = PlayShared.Bool(o, "active", true);
                        var go = Target(spec, at + ".target");
                        c.check = () =>
                        {
                            if (go == null) return want ? null : spec + " was destroyed";
                            return go.activeInHierarchy == want ? spec + (want ? " is active" : " is inactive") : null;
                        };
                        c.observe = () => go == null ? (object)"destroyed" : go.activeInHierarchy;
                        break;
                    }
                default:
                    throw new McpException(McpErr.BadArgs, at + ": unknown condition");
            }
            return c;
        }

        // ---------------------------------------------------------------- body, readiness, screen

        sealed class BodyNow
        {
            public int frame = int.MinValue;
            public bool full, has, active, dead, grounded, groundedKnown, inWater, controllable;
            public string state = "";
            public Vector3 pos;
        }

        static readonly BodyNow body = new BodyNow();
        static bool perceptionBuilt = true, observerBuilt = true, levelsBuilt = true;

        static bool PerceptionBuilt()
        {
            if (!perceptionBuilt) return false;
            try { Perception.ActiveCharacter(); return true; }
            catch (NotImplementedException) { perceptionBuilt = false; return false; }
            catch { return true; }
        }

        // main thread; full = also state, grounded and controllable (Perception.Body, cached per frame)
        static BodyNow Probe(bool full)
        {
            int f = Time.frameCount;
            if (body.frame == f && (body.full || !full)) return body;
            var b = body;
            b.frame = f;
            b.full = full;
            b.has = b.active = b.dead = b.grounded = b.groundedKnown = b.inWater = b.controllable = false;
            b.state = "";
            Character ch = null;
            try { ch = ScriptGlobals.mainCharacter; } catch { }
            b.has = ch != null;
            if (b.has)
            {
                b.active = ch.isCharacterActive;
                b.dead = ch.isDead;
                b.pos = ch.pos3;
            }
            if (perceptionBuilt)
            {
                try { Vector3 p; if (Perception.BodyPos(out p)) b.pos = p; }
                catch (NotImplementedException) { perceptionBuilt = false; }
                catch { }
            }
            if (!full || !b.has) return b;
            Json.Obj j = null;
            if (perceptionBuilt)
            {
                try { j = Perception.Body(false); }
                catch (NotImplementedException) { perceptionBuilt = false; }
                catch { }
            }
            if (j != null)
            {
                b.groundedKnown = j["grounded"] is bool;
                b.grounded = j.Bool("grounded");
                b.inWater = j.Bool("in_water");
                b.state = j.Str("state", "") ?? "";
                if (j["dead"] is bool) b.dead = j.Bool("dead");
                b.controllable = j["controllable"] is bool ? j.Bool("controllable") : FallbackControllable(ch);
                return b;
            }
            var boy = ch as Boy;
            if (boy != null)
            {
                try { b.state = ShortState(boy.state); } catch { }
                try { b.grounded = (object)boy.status.groundPlateau != null; b.groundedKnown = true; } catch { }
                b.inWater = b.state == "Swim" || b.state == "SwimGrab";
            }
            var hd = ch as Huddle;
            if (hd != null)
            {
                try { b.state = ShortState(hd.state); } catch { }
                b.inWater = b.state == "Swim";
            }
            b.controllable = FallbackControllable(ch);
            return b;
        }

        static bool FallbackControllable(Character ch)
        {
            try { return ch != null && ch.isCharacterActive && !ch.isDead && GameManager.IsPlaying() && GameInput.IsEnabled() && Stepper.Host.Settled; }
            catch { return false; }
        }

        static bool Airborne(BodyNow b)
        {
            if (b.grounded || b.inWater || b.dead) return false;
            return Array.IndexOf(Attached, b.state) < 0;
        }

        static string ShortState(object state)
        {
            if (state == null) return "";
            string n = state.GetType().Name;
            if (n.StartsWith("Boy") && n.Length > 3) n = n.Substring(3);
            else if (n.StartsWith("Huddle") && n.Length > 6) n = n.Substring(6);
            if (n.EndsWith("State") && n.Length > 5) n = n.Substring(0, n.Length - 5);
            return n;
        }

        static bool SameState(BodyNow b, string want)
        {
            string s = b.state ?? "";
            if (string.Equals(s, want, StringComparison.OrdinalIgnoreCase)) return true;
            string w = want;
            if (w.StartsWith("Boy", StringComparison.OrdinalIgnoreCase) && w.Length > 3) w = w.Substring(3);
            else if (w.StartsWith("Huddle", StringComparison.OrdinalIgnoreCase) && w.Length > 6) w = w.Substring(6);
            if (w.EndsWith("State", StringComparison.OrdinalIgnoreCase) && w.Length > 5) w = w.Substring(0, w.Length - 5);
            return string.Equals(s, w, StringComparison.OrdinalIgnoreCase);
        }

        static bool Alive()
        {
            var b = Probe(false);
            if (!b.has || !b.active || b.dead) return false;
            try { return GameManager.IsPlaying() && Stepper.Host.Settled; } catch { return false; }
        }

        static bool LoadingNow()
        {
            try { if (GameManager.IsLoading()) return true; } catch { }
            if (levelsBuilt)
            {
                try { return PlayLevels.Loading; }
                catch (NotImplementedException) { levelsBuilt = false; }
                catch { }
            }
            return false;
        }

        // PlayLevels.Ready, or report 03 section 1.4 while it is not built
        static bool Ready(out string reason)
        {
            reason = "";
            if (levelsBuilt)
            {
                try { return PlayLevels.Ready(out reason); }
                catch (NotImplementedException) { levelsBuilt = false; }
            }
            try
            {
                if (!GameManager.IsPlaying()) { reason = "loading"; return false; }
                if (!GameInput.IsEnabled()) { reason = "input disabled"; return false; }
                var gm = PersistentBehaviour<GameManager>.instance;
                if (gm != null && gm.fader != null && !gm.fader.IsDone) { reason = "fading"; return false; }
                if (TitleManager.IsShowing()) { reason = "title"; return false; }
                if (!SubsceneCullingManager.instance.AllCullersFinished()) { reason = "streaming"; return false; }
                if (PreAwakeManager.PendingPreawake()) { reason = "preawake pending"; return false; }
                if (!ScriptGlobals.mainCharacterActive) { reason = "no body"; return false; }
                if (!Stepper.Host.Settled) { reason = "load fading in"; return false; }
                return true;
            }
            catch (Exception e) { reason = "unreadable: " + e.GetType().Name; return false; }
        }

        static string ScreenNow()
        {
            if (observerBuilt)
            {
                try { return Observer.Screen(); }
                catch (NotImplementedException) { observerBuilt = false; }
            }
            try
            {
                if (GameManager.IsLoading()) return "loading";
                if (TitleManager.IsShowing() || TitleManager.IsPressStartShowing()) return "title";
                var ch = ScriptGlobals.mainCharacter;
                if (ch != null && ch.isDead) return "dead";
                if (GameManager.IsPaused) return "pause_menu";
                return "in_world";
            }
            catch { return "unknown"; }
        }

        static string SavepointNow()
        {
            try { int sub, sp; Savegame.GetCurrentSavepoint(out sub, out sp); return sub + ":" + sp; }
            catch { return "?"; }
        }

        // ---------------------------------------------------------------- targets, distance, hazards

        static GameObject Target(string spec, string at)
        {
            if (string.IsNullOrEmpty(spec)) throw new McpException(McpErr.BadArgs, at + ": empty target");
            if (observerBuilt)
            {
                try { return Observer.ResolveTarget(spec); }
                catch (NotImplementedException) { observerBuilt = false; }
            }
            var go = GameObject.Find(spec);
            if (go == null) throw new McpException(McpErr.NotFound, at + ": no object " + spec, "observe is not built yet: only exact paths of active objects resolve");
            return go;
        }

        // x/y distance from the body to go (-1 without a body)
        static double Dist(GameObject go)
        {
            var b = Probe(false);
            if (!b.has) return -1;
            var p = go.transform.position;
            double dx = p.x - b.pos.x, dy = p.y - b.pos.y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        static string HazardSafe(GameObject go, string spec)
        {
            if (go == null) return spec + " is gone";
            var b = Probe(false);
            if (!b.has) return null;
            List<HazardInfo> hz;
            try { hz = Perception.Hazards(b.pos, 80f, 40); }
            catch { return null; }
            if (hz == null) return null;
            foreach (var h in hz)
            {
                if (h == null || h.go != go) continue;
                if (h.safe) return spec + ": the body is in cover";
                if (!h.threat) return spec + ": no threat now (" + (h.state ?? "") + ")";
                return null;
            }
            return spec + ": not near the body any more";
        }

        // the hazard's eta_s now; -1 unknown, -2 not listed near the body
        static double EtaNow(GameObject go)
        {
            if (go == null) return -2;
            var b = Probe(false);
            if (!b.has) return -1;
            List<HazardInfo> hz;
            try { hz = Perception.Hazards(b.pos, 80f, 40); }
            catch { return -1; }
            if (hz != null) foreach (var h in hz) if (h != null && h.go == go) return h.etaS;
            return -2;
        }

        static string HazardEta(GameObject go, string spec, double min)
        {
            if (go == null) return spec + " is gone";
            double e = EtaNow(go);
            if (e < -1.5) return spec + ": not near the body any more";
            if (e >= min) return spec + ": eta " + PlayShared.F(PlayShared.R(e, 1)) + " s >= " + PlayShared.F(min) + " s";
            return null;
        }

        // the music position folded into cycle; cycle 0 = the running shockwave's own folded clock (ForcePushManager
        // musicTime), else the raw position; -1 when no music plays
        static double MusicPhaseNow(double cycle)
        {
            try
            {
                if (cycle <= 0)
                {
                    var fpm = LevelGlobals.forcePushManager;
                    if (fpm != null && fpm.isActiveAndEnabled) return fpm.musicTime;
                }
                var ga = PersistentBehaviour<GlobalAudio>.instance;
                if (ga == null || ga.music == null) return -1;
                return WaitPlan.MusicPhase(ga.music.GetMusicPosition_s(), cycle);
            }
            catch { return -1; }
        }

        // ---------------------------------------------------------------- when (act / move_to / interact)
        public const string WhenText = "Wait first, then act in the same call: one wait_until condition, e.g. {hazard_eta:{target:\"h3\", min_s:4}} (4+ s to the next shockwave blow) or {music_time:{from:0.2, to:1.5}}; not met in when_timeout_s = outcome timeout, no input.";
        public const double WhenDefaultS = 15, WhenMaxS = 45;

        // the when parameter: the wait_until conditions that make sense before an action (same names and fields; the
        // load / death / screen ones belong to wait_until), terse descriptions (tools/list size)
        public static SchemaObj WhenSchema()
        {
            return Schema.Obj()
                .Num("seconds", "Game s.", 0.05, 600).Bool("grounded", "On ground.").Bool("airborne", "In air.").Str("state", "Body state.")
                .Obj("near", "Within m.", Schema.Obj().Str("target", "Id.").Num("within", "m.", 1, 0.1, 100).Required("target"))
                .Obj("far_from", "Beyond m.", Schema.Obj().Str("target", "Id.").Num("beyond", "m.", 5, 0.1, 200).Required("target"))
                .Str("hazard_safe", "Hazard id.")
                .Obj("hazard_eta", "eta_s >= min_s.", Schema.Obj().Str("target", "Hazard id.").Num("min_s", "s.", 4, 0, 600).Required("target"))
                .Obj("music_time", "Music s in [from, to).", Schema.Obj().Num("from", "s.", 0, 1e5).Num("to", "s.", 0, 1e5).Num("cycle", "s, 0 auto.", 0, 0, 1e4).Required("from", "to"))
                .Obj("fsm_state", "FSM state.", Schema.Obj().Str("object", "Id.").Str("fsm", "FSM.").Str("state", "State.").Required("object", "state"))
                .Obj("signal", "Signal fired.", Schema.Obj().Str("object", "Id.").Str("output", "Output.").Required("object", "output"))
                .Obj("object_active", "Object active.", Schema.Obj().Str("target", "Id.").Bool("active", "Wanted.", true).Required("target"));
        }

        // worker thread, the caller holds the control lane: run the game (or watch it in realtime) until condition holds,
        // at most timeoutS of wall clock and never into reserveMs (what the action itself needs of the call). stop is a
        // RunStop value: condition = go on with the action; died, loading, cancelled, timeout, steps, stalled = do not act.
        // The result block: {condition (key), satisfied, detail, waited_steps, elapsed_ms, warnings?}.
        public static Json.Obj When(CallContext ctx, Json.Obj condition, double timeoutS, int reserveMs, string label, out string stop)
        {
            string err;
            string key = WaitPlan.KeyOf(condition, 0, out err);
            if (key == null) throw new McpException(McpErr.BadArgs, "when: " + err.Replace("any[0]", "when").Replace(" (put several conditions as separate items)", ""), "when is one wait_until condition, e.g. {hazard_eta:{target:\"h3\", min_s:4}}");
            if (key == "job") throw new McpException(McpErr.BadArgs, "when: a job condition cannot gate an action", "wait_until {any:[{job:\"<id>\"}]} first, then act");
            var set = new WaitSet();
            var o = new Json.Obj();
            o["condition"] = key;
            try
            {
                set.conds.Add(CompileOne(condition, key, 0, ctx));
                string already = ctx.Main(() => set.Check());
                int steps = 0;
                double elapsed = 0;
                string detail;
                var warnings = new List<string>();
                if (already != null) { stop = RunStop.Condition; detail = set.What ?? already; }
                else
                {
                    int budget = WaitPlan.WhenBudgetMs(timeoutS, ctx.RemainingMs, reserveMs);
                    if (budget < 100) { stop = RunStop.Timeout; detail = "no time left in this call for the wait (lower the action's length or when_timeout_s)"; }
                    else
                    {
                        int maxSteps = 36000;
                        if (key == "steps") maxSteps = Math.Max(maxSteps, PlayShared.Int(condition, "steps", 1));
                        bool aware = WaitPlan.LoadAwareAny(new List<string> { key });
                        var rr = Stepper.Run(ctx, new RunRequest { maxSteps = maxSteps, timeoutMs = budget, stopCheck = set.Check, stopOnDeath = !aware, stopOnLoad = !aware, label = label + " when" });
                        stop = rr.stop;
                        detail = rr.stop == RunStop.Condition ? (set.What ?? rr.detail) : rr.detail;
                        steps = rr.steps;
                        elapsed = rr.elapsedMs;
                        warnings.AddRange(rr.warnings);
                    }
                }
                o["satisfied"] = stop == RunStop.Condition;
                o["detail"] = detail ?? "";
                o["waited_steps"] = (double)steps;
                o["elapsed_ms"] = Math.Round(elapsed, 1);
                if (warnings.Count > 0) o["warnings"] = PlayShared.L(warnings);
                return o;
            }
            finally
            {
                try { ctx.MainDo(set.Cleanup, 2000); } catch { }
            }
        }

        // ---------------------------------------------------------------- FSM states

        static void FsmCond(WaitCond c, Json.Obj o, string at)
        {
            string spec = PlayShared.Str(o, "object", "");
            string fsmName = PlayShared.Str(o, "fsm", null);
            string state = PlayShared.Str(o, "state", "");
            var go = Target(spec, at + ".object");
            var fsms = new List<PlayMakerFSM>();
            var names = new List<string>();
            foreach (var f in go.GetComponents<PlayMakerFSM>())
            {
                if (f == null) continue;
                if (!names.Contains(f.FsmName)) names.Add(f.FsmName);
                if (string.IsNullOrEmpty(fsmName) || f.FsmName == fsmName) fsms.Add(f);
            }
            if (fsms.Count == 0)
                throw new McpException(McpErr.NotFound, at + ": no FSM" + (string.IsNullOrEmpty(fsmName) ? "" : " " + fsmName) + " on " + PlayShared.PathOf(go.transform),
                    names.Count > 0 ? "FSMs here: " + string.Join(", ", names.ToArray()) : "this object has no PlayMakerFSM");
            var states = new List<string>();
            bool known = false;
            foreach (var f in fsms)
            {
                FsmState[] all = null;
                try { all = f.FsmStates; } catch { }
                if (all == null) continue;
                foreach (var s in all)
                {
                    if (s == null) continue;
                    if (s.Name == state) known = true;
                    if (states.Count < 25 && !states.Contains(s.Name)) states.Add(s.Name);
                }
            }
            if (!known) throw new McpException(McpErr.BadArgs, at + ".state: no state " + state + " in " + string.Join(", ", NamesOf(fsms)), "states: " + string.Join(", ", states.ToArray()));
            c.check = () =>
            {
                foreach (var f in fsms)
                {
                    if (f == null) continue;
                    string st;
                    try { st = f.ActiveStateName; } catch { continue; }
                    if (st == state) return f.FsmName + " in " + state;
                }
                return null;
            };
            c.observe = () =>
            {
                var l = new List<object>();
                foreach (var f in fsms) { if (f == null) continue; string st; try { st = f.ActiveStateName; } catch { st = "?"; } l.Add(f.FsmName + ": " + st); }
                return l;
            };
        }

        static string[] NamesOf(List<PlayMakerFSM> fsms)
        {
            var l = new List<string>();
            foreach (var f in fsms) if (f != null) l.Add(f.FsmName);
            return l.ToArray();
        }

        // ---------------------------------------------------------------- signal taps

        sealed class Tap
        {
            public SignalOut so;
            public SignalConnection con;
            public int fired;
            public double lastUsedMs;
        }

        static readonly List<Tap> taps = new List<Tap>();
        static FieldInfo fConns;
        static readonly System.Diagnostics.Stopwatch tapClock = System.Diagnostics.Stopwatch.StartNew();

        static double Now { get { return tapClock.Elapsed.TotalMilliseconds; } }

        static void SignalCond(WaitCond c, Json.Obj o, string at)
        {
            string spec = PlayShared.Str(o, "object", "");
            string output = PlayShared.Str(o, "output", "");
            var go = Target(spec, at + ".object");
            var t = AddTap(go, output, at);
            int start = t.fired;
            c.check = () =>
            {
                t.lastUsedMs = Now;
                return t.fired > start ? output + " fired on " + spec + (t.fired - start > 1 ? " (" + (t.fired - start) + " times)" : "") : null;
            };
            c.observe = () => (double)(t.fired - start);
            c.cleanup = () => RemoveTap(t);
        }

        static Tap AddTap(GameObject go, string output, string at)
        {
            SignalManager sm = null;
            try { sm = PersistentBehaviour<SignalManager>.instance; } catch { }
            if (sm == null || sm.signalOuts == null) throw new McpException(McpErr.NotReady, at + ": the signal manager is not up yet");
            SignalOut found = null;
            var names = new List<string>();
            foreach (var so in sm.signalOuts)
            {
                if (so == null || !so.isActive || so.gameObject != go) continue;
                if (so.debugName == output) { found = so; break; }
                if (names.Count < 20 && so.debugName != null && !names.Contains(so.debugName)) names.Add(so.debugName);
            }
            if (found == null)
                throw new McpException(McpErr.NotFound, at + ": no signal output " + output + " on " + PlayShared.PathOf(go.transform),
                    names.Count > 0 ? "outputs here: " + string.Join(", ", names.ToArray()) : "this object has no active signal outputs");
            if (fConns == null) fConns = typeof(SignalOut).GetField("connections", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var list = fConns != null ? fConns.GetValue(found) as FastList<SignalConnection> : null;
            if (list == null) throw new McpException(McpErr.GameError, at + ": SignalOut.connections is not readable");
            var t = new Tap { so = found, lastUsedMs = Now };
            var sin = new SignalIn { isActive = true, debugName = TapName, action = () => t.fired++ };
            t.con = new SignalConnection { isActive = true, signalIn = sin, signalOut = found, signalOutGameObject = go, signalOutName = found.debugName, signalInName = TapName };
            list.Insert(0, t.con);
            taps.Add(t);
            return t;
        }

        static void RemoveTap(Tap t)
        {
            if (!taps.Remove(t)) return;
            try
            {
                var list = fConns != null && t.so != null ? fConns.GetValue(t.so) as FastList<SignalConnection> : null;
                if (list != null) list.Remove(t.con);
            }
            catch { }
        }

        // main thread, every frame: drop taps whose wait ended without clean-up
        static void TickTaps()
        {
            if (taps.Count == 0) return;
            double now = Now;
            for (int i = taps.Count - 1; i >= 0; i--)
                if (now - taps[i].lastUsedMs > TapExpireMs) RemoveTap(taps[i]);
        }
    }
}
