using System;
using System.Collections.Generic;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // Act.cs (owner INPUT, the act tool schema frozen): the raw, step-exact input layer. Takes the control lane, installs
    // AgentCore (agent mode unless input_mode says otherwise), queues the inputs, runs Stepper.Run for their total length
    // (at most 600 steps = 10 s) with the stop_on / watch checks, then releases (unless hold) and returns outcome,
    // steps run, what is still held, changes and a concise observation. Inputs are always released on error or cancel.
    // input_mode human releases everything and puts the game's own input core back (the inputs are not run).
    // hold: after a full run the last segment stays latched; after a stop_on / watch stop the input being fed at that
    // step stays latched; after death, load, cancel, timeout or stall nothing is held.
    // stop_on: death and load come from Stepper; state_change, grounded, airborne, checkpoint compare BodySnaps every
    // step; hazard asks Perception.Hazards every 6 steps; event = any new ChangeLog entry. watch: position (10 cm),
    // rotation (10 deg), active flag, first FSM state of each watched object. Not ready / died / loading results carry
    // next (PlayShared.NextFor: the call to make). changes is compact (near the body) unless changes:"full".
    // A grab from interact grab (AgentCore.Grip) stays held through act: action is pressed in every step and stays
    // pressed after the call (held.grip true, warning grip_held when a segment has action false); release:true,
    // input_mode human or a cancel let go. when = one wait_until condition (Waits.When): the game runs until it holds,
    // then the inputs start in the same call (music / hazard phases); a when that does not hold within when_timeout_s
    // ends the call with outcome timeout and no inputs run.
    public static class Act
    {
        public const string Owner = "INPUT";
        public static readonly string[] StopOn = { "death", "load", "state_change", "grounded", "airborne", "checkpoint", "hazard", "event" };
        public static readonly string[] Outcomes = { "done", "stopped", "died", "loading", "cancelled", "timeout", "not_controllable", "stalled" };

        public static void Register()
        {
            var item = Schema.Obj().Vec("stick", "Left stick x (right +), y (up +). 0 or magnitude 0.3..1; |x| > 0.8 runs.", 2, -1, 1).Default("stick", new List<object> { 0.0, 0.0 })
                .Bool("jump", "Jump button held (a press needs a release before the next one).", false)
                .Bool("action", "Action / grab button held (held = grab, short tap under 0.3 s = action).", false)
                .Int("steps", "Logic steps to hold this input (1/60 s each).", 1, 1, 600);
            var stopOn = Schema.Arr(Schema.Enum(StopOn), 0, 8); stopOn["default"] = new List<object> { "death", "load" };
            PlayRegistry.Add(PlayShared.Def("act", "Act (input steps)", PlayShared.Play, Owner,
                "Press the game's controls for exact logic steps (60 per second): a list of {stick, jump, action, steps}, at most 600 steps per call. The game runs only for those steps (step mode) and stops early on stop_on. hold keeps the last input pressed after the call; a grab from interact grab stays held (release:true lets go). when waits for a condition first, then acts in the same call. Returns outcome, steps run, changes and a fresh observation.",
                Schema.Obj().Arr("inputs", "Input segments in order.", Schema.ObjOf(item), 0, 64)
                    .Bool("hold", "Keep the last segment pressed after the call (until release or the next act).", false)
                    .Bool("release", "Release held input (also a grab) before the inputs run.", false)
                    .Enum("input_mode", "agent (default on first use), mix (agent + physical pad) or human (give control back).", null, "agent", "mix", "human")
                    .Prop("stop_on", "Stop early when one of these happens.", stopOn)
                    .Arr("watch", "Object ids: stop when the state of one changes.", Schema.Str(), 0, 8)
                    .Obj("when", Waits.WhenText, Waits.WhenSchema())
                    .Num("when_timeout_s", "Longest wait for when (wall clock s).", Waits.WhenDefaultS, 0.1, Waits.WhenMaxS)
                    .Enum("then", "step mode: freeze after the call (default) or keep running.", "freeze", "freeze", "run")
                    .Enum("changes", "compact (near the body, plus deaths, checkpoints, loads, errors) or full (every signal and fsm change).", "compact", "compact", "full")
                    .Enum("observe", "Observation in the result.", "concise", "concise", "none").Build(),
                Schema.Obj().Enum("outcome", "How it ended.", null, Outcomes).Str("stop_reason", "Which stop_on / watch fired.")
                    .Str("next", "When nothing ran or the body died: the call to make.")
                    .Int("steps", "Logic steps run.", 0, 100000).Int("requested", "Steps asked for.", 0, 100000).Int("frames", "Rendered frames.", 0, 100000)
                    .OutObj("held", "Input still held after the call (null = none).", Schema.Obj().Vec("stick", "", 2, -1, 1).Bool("jump", "").Bool("action", ""))
                    .Enum("input_mode", "Input mode now.", null, "agent", "mix", "human")
                    .Arr("warnings", "music_timed, controller_digital, input_hijacked ...", Schema.Str(), 0, 20)
                    .Prop("changes", null, ChangeLog.Ref())
                    .Prop("observe", null, Observer.ConciseRef()).BuildOutput()).AlwaysLoad(), Handle);
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            int total;
            string err;
            var frames = ParseInputs(PlayShared.List(a, "inputs"), out total, out err);
            if (err != null) return ToolResult.Fail(McpErr.BadArgs, err, "a segment is {stick:[x, y], jump, action, steps}; stick magnitude 0 or 0.3..1; at most 600 steps per call");
            bool hold = PlayShared.Bool(a, "hold", false);
            bool release = PlayShared.Bool(a, "release", false);
            string modeArg = PlayShared.Str(a, "input_mode", null);
            var stopOn = new HashSet<string>(PlayShared.Has(a, "stop_on") ? PlayShared.Strings(a, "stop_on") : new List<string> { "death", "load" });
            var watch = PlayShared.Strings(a, "watch");
            bool leaveRunning = PlayShared.Str(a, "then", "freeze") == "run";
            bool wantObs = PlayShared.Str(a, "observe", "concise") != "none";
            bool fullChanges = PlayShared.Str(a, "changes", "compact") == "full";
            var when = PlayShared.Obj(a, "when");
            double whenTimeout = PlayShared.Num(a, "when_timeout_s", Waits.WhenDefaultS);
            string screenNow = null;

            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return busy;

            var warnings = new List<string>();
            var r = PlayShared.Env();
            long mark = SafeMark();
            var scope = new ActScope();
            ActBody start = null;
            string notReady = null, readyReason = "";
            bool human = modeArg == "human";
            bool keepLatch = false, keepGrip = false;
            RunResult rr = null;
            try
            {
                ctx.MainDo(() =>
                {
                    if (watch.Count > 0) ActProbe.Watch(scope, watch);
                    if (human)
                    {
                        AgentCore.Release();
                        AgentCore.Uninstall();
                        return;
                    }
                    InputMode want;
                    string w = AgentQueue.TryMode(modeArg, out want) ? AgentCore.Install(want) : ActProbe.EnsureAgent();
                    if (w != null) warnings.Add(w);
                    bool jumpHeld = AgentCore.LastFed.jump;
                    if (release) AgentCore.Release();
                    warnings.AddRange(ActInputs.Lint(frames, jumpHeld));
                    if (AgentCore.Grip && ActInputs.AnyActionOff(frames)) warnings.Add(GripWarning);
                    if (frames.Count == 0) return;
                    if (!ActProbe.Ready(out readyReason))
                    {
                        if (ActStops.SoftNotReady(readyReason)) warnings.Add("not_ready: " + readyReason + " (inputs may be ignored)");
                        else
                        {
                            notReady = ActStops.NotReadyOutcome(readyReason);
                            try { screenNow = Observer.Screen(); } catch (NotImplementedException) { }
                            return;
                        }
                    }
                    start = ActProbe.Take(scope);
                    if (stopOn.Contains("hazard")) foreach (var h in ActProbe.Hazards(scope, start)) if (h.threat) scope.threatsAtStart.Add(h.id);
                });

                if (human)
                {
                    if (frames.Count > 0) warnings.Add("inputs_ignored: input_mode human gives control back to the player; nothing was run");
                    r["outcome"] = "done";
                    r["steps"] = 0.0;
                    r["requested"] = (double)total;
                    r["frames"] = 0.0;
                }
                else if (notReady != null || frames.Count == 0)
                {
                    r["outcome"] = notReady ?? "done";
                    if (notReady != null)
                    {
                        r["stop_reason"] = readyReason;
                        string next = PlayShared.NextFor(readyReason, screenNow);
                        if (next != null) r["next"] = next;
                    }
                    r["steps"] = 0.0;
                    r["requested"] = (double)total;
                    r["frames"] = 0.0;
                    keepLatch = notReady == null && hold;
                    keepGrip = notReady != "loading";
                }
                else if (when != null && !RunWhenAct(ctx, r, when, whenTimeout, total))
                {
                    r["steps"] = 0.0;
                    r["requested"] = (double)total;
                    r["frames"] = 0.0;
                    keepGrip = r.Str("outcome", "") == "timeout" || r.Str("outcome", "") == "stalled";
                }
                else
                {
                    if (when != null)
                        ctx.MainDo(() =>
                        {
                            start = ActProbe.Take(scope);
                            scope.threatsAtStart.Clear();
                            if (stopOn.Contains("hazard")) foreach (var h in ActProbe.Hazards(scope, start)) if (h.threat) scope.threatsAtStart.Add(h.id);
                        });
                    long gated0 = AgentCore.GatedSteps;
                    AgentCore.Replace(frames, hold);
                    var req = new RunRequest
                    {
                        maxSteps = total,
                        timeoutMs = Math.Max(1000, Math.Min(50000, ctx.RemainingMs - 2500)),
                        stopOnDeath = stopOn.Contains("death"),
                        stopOnLoad = stopOn.Contains("load"),
                        leaveRunning = leaveRunning,
                        label = "act",
                        stopCheck = StopCheck(scope, start, stopOn, mark)
                    };
                    rr = Stepper.Run(ctx, req);
                    long gated = AgentCore.GatedSteps - gated0;
                    bool allGated = rr.steps > 0 && gated >= rr.steps;
                    string outcome = ActStops.Outcome(rr.stop, allGated);
                    r["outcome"] = outcome;
                    if (rr.stop == RunStop.Condition) r["stop_reason"] = rr.detail;
                    else if (rr.stop == RunStop.Died) { r["stop_reason"] = "death"; r["next"] = PlayShared.NextFor("dead", null); }
                    else if (rr.stop == RunStop.Loading) { r["stop_reason"] = "load"; r["next"] = PlayShared.NextFor("loading", null); }
                    else if (rr.stop != RunStop.Steps && !string.IsNullOrEmpty(rr.detail)) r["stop_reason"] = rr.detail;
                    else if (allGated) r["stop_reason"] = "input_gated";
                    r["steps"] = (double)rr.steps;
                    r["requested"] = (double)total;
                    r["frames"] = (double)rr.frames;
                    if (gated > 0 && !allGated) warnings.Add("input_gated: " + gated + " of " + rr.steps + " steps had no effect (input disabled during a fade or load)");
                    foreach (var w in rr.warnings) if (!warnings.Contains(w)) warnings.Add(w);
                    if (hold && rr.stop == RunStop.Steps) keepLatch = true;
                    else if (hold && rr.stop == RunStop.Condition)
                    {
                        AgentCore.ClearQueue();
                        AgentCore.Latch(AgentCore.LastFed);
                        keepLatch = true;
                    }
                    else if (hold) warnings.Add("hold dropped: the run ended with " + outcome);
                    keepGrip = rr.stop != RunStop.Cancelled && rr.stop != RunStop.Died && rr.stop != RunStop.Loading;
                }
            }
            finally
            {
                if (keepLatch) AgentCore.ClearQueue();
                else if (keepGrip) AgentCore.ReleaseKeepGrip();
                else AgentCore.Release();
            }

            r["held"] = AgentCore.HeldNow();
            r["input_mode"] = AgentQueue.ModeName(AgentCore.Mode);
            if (AgentCore.Hijacked && !warnings.Contains("input_hijacked")) warnings.Add("input_hijacked");
            Tail(r, ctx, mark, wantObs, "act", warnings, fullChanges);
            return ToolResult.Ok(r);
        }

        internal const string GripWarning = "grip_held: the grab from interact grab stays held (action is pressed in every step and after the call); act {release:true} or interact release lets go";

        // worker: the when wait before an action; true = it holds, run the action. stop = the RunStop that ended the
        // wait (condition when it holds); r gets the when block
        internal static bool RunWhen(CallContext ctx, Json.Obj r, Json.Obj when, double timeoutS, int actionSteps, string tool, out string stop)
        {
            var wb = Waits.When(ctx, when, timeoutS, ActInputs.ReserveMs(actionSteps), tool, out stop);
            r["when"] = wb;
            return stop == RunStop.Condition;
        }

        static bool RunWhenAct(CallContext ctx, Json.Obj r, Json.Obj when, double timeoutS, int total)
        {
            string stop;
            if (RunWhen(ctx, r, when, timeoutS, total, "act", out stop)) return true;
            var wb = r["when"] as Json.Obj;
            r["outcome"] = stop == RunStop.Steps ? "timeout" : ActStops.Outcome(stop, false);
            r["stop_reason"] = "when: " + (wb != null ? wb.Str("detail", "") : stop);
            if (stop == RunStop.Died) r["next"] = PlayShared.NextFor("dead", null);
            else if (stop == RunStop.Loading) r["next"] = PlayShared.NextFor("loading", null);
            else if (stop == RunStop.Timeout || stop == RunStop.Steps) r["next"] = "the condition did not hold in when_timeout_s: observe, then call again (inputs did not run)";
            return false;
        }

        static Func<string> StopCheck(ActScope scope, ActBody start, HashSet<string> stopOn, long mark)
        {
            bool snapStops = false;
            foreach (var k in ActStops.SnapStops) if (stopOn.Contains(k)) snapStops = true;
            bool hazard = stopOn.Contains("hazard"), ev = stopOn.Contains("event"), watch = scope.watchGo.Count > 0;
            if (!snapStops && !hazard && !ev && !watch) return null;
            var prev = start;
            int n = 0;
            return () =>
            {
                n++;
                var now = ActProbe.Take(scope);
                string why = snapStops ? ActStops.Check(stopOn, start, prev, now) : null;
                prev = now;
                if (why == null && watch) why = ActProbe.CheckWatch(scope);
                if (why == null && hazard && n % 6 == 0) why = ActStops.Hazard(scope.threatsAtStart, ActProbe.Hazards(scope, now));
                if (why == null && ev) why = EventSince(mark);
                return why;
            };
        }

        static string EventSince(long mark)
        {
            try
            {
                if (ChangeLog.Mark() <= mark) return null;
                var l = ChangeLog.Events(mark, 1);
                if (l == null || l.Count == 0) return null;
                return "event: " + l[0].Str("type", "?") + " " + l[0].Str("detail", "");
            }
            catch (Exception) { return null; }
        }

        internal static long SafeMark()
        {
            try { return ChangeLog.Mark(); } catch (Exception) { return -1; }
        }

        // the common end of an action result: warnings, changes since mark, concise observation
        internal static void Tail(Json.Obj r, CallContext ctx, long mark, bool wantObs, string tool, List<string> warnings)
        {
            Tail(r, ctx, mark, wantObs, tool, warnings, false);
        }

        internal static void Tail(Json.Obj r, CallContext ctx, long mark, bool wantObs, string tool, List<string> warnings, bool fullChanges)
        {
            if (mark >= 0)
            {
                try { r["changes"] = fullChanges ? ChangeLog.SinceFull(mark, 20) : ChangeLog.Since(mark, 20); }
                catch (Exception) { warnings.Add("changes_unavailable"); }
            }
            if (wantObs)
            {
                try { r["observe"] = ctx.Main(() => Observer.Concise(tool)); }
                catch (NotImplementedException) { warnings.Add("observe_unavailable"); }
                catch (McpException e) { warnings.Add("observe_failed: " + e.code + " " + e.Message); }
            }
            if (warnings.Count > 20) warnings.RemoveRange(20, warnings.Count - 20);
            r["warnings"] = PlayShared.L(warnings);
        }

        // worker thread: validate and convert the inputs array (already schema-checked) to frames; error = "inputs[i]: ..."
        public static List<InputFrame> ParseInputs(List<object> inputs, out int totalSteps, out string error)
        {
            return ActInputs.Parse(inputs, out totalSteps, out error);
        }
    }
}
