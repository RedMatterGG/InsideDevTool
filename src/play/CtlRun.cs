using System;
using System.Collections.Generic;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // CtlRun.cs: the job side of the ctl commands (Ctl.cs). CtlJobs.Run is the worker thread of one timed command, inside
    // the detached control-lane job that Ctl.cs took on the main thread. Input commands: CtlGame.Prepare (main thread)
    // checks that the game takes play input, installs AgentCore (agent mode unless mix was chosen), keeps the clock as it
    // is (step mode stays step mode; an untouched or running realtime clock is chosen as realtime, so ctl does not freeze
    // a game that a person plays) and queues the frames with the held input (ctl hold) merged in and kept after them,
    // without a gap; Stepper.Run then advances (step mode) or watches (realtime) the game for exactly those steps and
    // stops on death and load. Any end other than done releases all input (like act). stopat runs toward x with plain
    // stick input and drops the stick in the step that crosses x (its stop check, main thread). Tool-backed commands
    // (goto = move_to, time step, track play) call the Play tool handler with the job's own CallContext, so the tool's
    // BeginJob gets the same job and shares the lane; a track playback is finished by ActTrack's tick, not here.
    // CtlJobs.Tick (main thread) logs "ctl jN <outcome>: ..." when a watched job ends.
    internal static class CtlJobs
    {
        sealed class Watched { public McpJob job; public string what; }

        static readonly object gate = new object();
        static readonly List<Watched> watched = new List<Watched>();
        static readonly Dictionary<string, string> whats = new Dictionary<string, string>();
        static readonly Queue<string> order = new Queue<string>();
        static volatile string last;
        const int KeepWhats = 200;

        internal static string LastId { get { return last; } }

        internal static string WhatOf(string id)
        {
            if (id == null) return null;
            lock (gate) { string w; return whats.TryGetValue(id, out w) ? w : null; }
        }

        internal static void Watch(McpJob job, string what)
        {
            lock (gate)
            {
                watched.Add(new Watched { job = job, what = what });
                whats[job.Id] = what;
                order.Enqueue(job.Id);
                while (order.Count > KeepWhats) whats.Remove(order.Dequeue());
            }
            last = job.Id;
        }

        // main thread, every frame
        internal static void Tick()
        {
            List<Watched> ended = null;
            lock (gate)
            {
                for (int i = 0; i < watched.Count; i++)
                {
                    if (watched[i].job.State == McpJobState.Running) continue;
                    if (ended == null) ended = new List<Watched>();
                    ended.Add(watched[i]);
                }
                if (ended != null) foreach (var w in ended) watched.Remove(w);
            }
            if (ended == null) return;
            foreach (var w in ended) DevLog.Write("ctl " + w.job.Id + " " + CtlCore.JobText(w.job.State, w.what, w.job.Result));
        }

        // worker thread
        internal static void Run(CtlPlan p, HostCallContext ctx, McpJob job)
        {
            Json.Obj r;
            try { r = p.kind == CtlKind.Tool ? RunTool(p, ctx) : RunInput(p, ctx, job); }
            catch (McpException e) { Release(); r = Failed(e.code, e.Message, e.hint); }
            catch (Exception e) { Release(); r = Failed(McpErr.Internal, e.GetType().Name + ": " + e.Message, null); }
            if (r == null) return;
            if (!r.Bool("ok", false))
            {
                job.Fail(r.Str("code", McpErr.Internal), r.Str("message", "failed"));
                return;
            }
            try { r["body"] = ctx.Main(() => CtlGame.BodyLine(), 5000); } catch (Exception) { }
            job.Finish(r);
        }

        static void Release() { try { AgentCore.Release(); } catch (Exception) { } }

        static Json.Obj Failed(string code, string message, string hint)
        {
            var r = new Json.Obj();
            r["ok"] = false;
            r["code"] = code ?? McpErr.Internal;
            r["message"] = (message ?? "") + (string.IsNullOrEmpty(hint) ? "" : " (" + hint + ")");
            return r;
        }

        static Json.Obj RunTool(CtlPlan p, HostCallContext ctx)
        {
            var tr = CtlCommands.CallTool(p.tool, p.args, ctx);
            var s = tr.structured ?? new Json.Obj();
            if (tr.isError)
            {
                var e = s["error"] as Json.Obj;
                return Failed(e != null ? e.Str("code", McpErr.Internal) : McpErr.Internal, e != null ? e.Str("message", "") : "failed", e != null ? e.Str("hint", null) : null);
            }
            if (p.tool == "input_track" && s.Str("outcome", "") == "playing") return null;
            var r = PlayShared.Env();
            r["outcome"] = s.Str("outcome", "done");
            r["line"] = CtlCore.ToolLine(p.tool, p.op, s, false);
            r["result"] = s;
            return r;
        }

        static Json.Obj RunInput(CtlPlan p, HostCallContext ctx, McpJob job)
        {
            var prep = ctx.Main(() => CtlGame.Prepare(p));
            if (prep.error != null) return Failed(prep.code, prep.error, prep.hint);
            var r = PlayShared.Env();
            if (prep.done != null)
            {
                r["outcome"] = "done";
                r["steps"] = 0.0;
                r["line"] = prep.done;
                return r;
            }
            RunResult rr;
            try
            {
                rr = Stepper.Run(ctx, new RunRequest
                {
                    maxSteps = prep.steps,
                    timeoutMs = prep.timeoutMs,
                    stopOnDeath = true,
                    stopOnLoad = true,
                    label = "ctl " + job.Id,
                    stopCheck = prep.stopCheck
                });
            }
            catch (Exception)
            {
                Release();
                throw;
            }
            string outcome = CtlCore.Outcome(rr.stop, p.kind == CtlKind.StopAt);
            if (outcome == "not_reached") { try { ctx.MainDo(() => CtlGame.Rest(prep)); } catch (Exception) { Release(); } }
            else if (outcome != "done") Release();
            var w = new List<string>(prep.warnings);
            foreach (var x in rr.warnings) if (!w.Contains(x)) w.Add(x);
            r["outcome"] = outcome;
            r["steps"] = (double)rr.steps;
            r["requested"] = (double)prep.steps;
            r["line"] = CtlCore.RunLine(outcome, p.what, rr.steps, prep.steps, rr.stop == RunStop.Steps && outcome == "done" ? "" : rr.detail, w);
            r["warnings"] = PlayShared.L(w);
            return r;
        }
    }

    // what Prepare decided (main thread) for the worker
    internal sealed class CtlPrep
    {
        public string error, code, hint, done;
        public readonly List<string> warnings = new List<string>();
        public int steps, timeoutMs;
        public Func<string> stopCheck;
        public InputFrame after;
        public bool hasAfter;
    }

    // game side of ctl, main thread only
    internal static class CtlGame
    {
        static string EnsureAgent()
        {
            var m = AgentCore.Installed && AgentCore.Mode != InputMode.Human ? AgentCore.Mode : InputMode.Agent;
            return AgentCore.Install(m);
        }

        static void ChooseClock()
        {
            if (Stepper.Mode == StepMode.Realtime && !Stepper.Frozen) Stepper.SetMode(StepMode.Realtime);
        }

        static string Screen()
        {
            try { return Observer.Screen(); } catch (Exception) { return null; }
        }

        static string NotReadyHint(string reason, string screen)
        {
            if (screen == "message") return "a storage message waits for a button";
            switch (reason ?? "")
            {
                case "title": return "the title waits for a button: start the game first";
                case "paused": return "the pause menu is open: close it";
                case "dead": return "the body respawns at the checkpoint: retry in a moment";
            }
            return "retry when the game is playable (ctl status shows the body)";
        }

        internal static CtlPrep Prepare(CtlPlan p)
        {
            var r = new CtlPrep { steps = p.steps };
            r.warnings.AddRange(p.warnings);
            string reason;
            if (!Observer.Ready(out reason))
            {
                if (!CtlCore.SoftNotReady(reason))
                {
                    r.error = "the game does not take play input now (" + reason + ")";
                    r.code = McpErr.NotReady;
                    r.hint = NotReadyHint(reason, Screen());
                    return r;
                }
                r.warnings.Add("not_ready: " + reason + " (inputs may be ignored)");
            }
            string w = EnsureAgent();
            if (w != null) r.warnings.Add(w);
            ChooseClock();
            InputFrame held;
            bool hasHeld = AgentCore.Latched(out held) && !CtlCore.IsNeutral(held);
            List<InputFrame> frames;
            if (p.kind == CtlKind.StopAt)
            {
                Vector3 pos;
                if (!Perception.BodyPos(out pos))
                {
                    r.error = "no controlled body";
                    r.code = McpErr.NotReady;
                    return r;
                }
                r.after = CtlCore.WithoutStick(held);
                r.hasAfter = hasHeld && !CtlCore.IsNeutral(r.after);
                double dx = p.target - pos.x;
                if (Math.Abs(dx) <= CtlCore.StopAtTolerance)
                {
                    Rest(r);
                    r.done = "done: " + p.what + ": already there (x=" + CtlCore.F(pos.x) + ")";
                    return r;
                }
                int dir = dx > 0 ? 1 : -1;
                float mag = p.gait == "walk" ? CtlCore.WalkStick : 1f;
                var run = new List<CtlFrame> { new CtlFrame { stick = true, x = dir * mag, steps = p.steps } };
                frames = CtlCore.Merge(run, held, hasHeld, false);
                double target = p.target;
                var prep = r;
                r.stopCheck = () =>
                {
                    Vector3 q;
                    if (!Perception.BodyPos(out q)) return null;
                    if ((dir > 0 && q.x < target) || (dir < 0 && q.x > target)) return null;
                    Rest(prep);
                    return "reached x=" + CtlCore.F(q.x);
                };
            }
            else frames = CtlCore.Merge(p.frames, held, hasHeld, true);
            AgentCore.Release();
            AgentCore.Enqueue(frames, p.kind != CtlKind.StopAt && hasHeld);
            r.timeoutMs = CtlCore.TimeoutMs(p.steps, Stepper.Mode == StepMode.Step, Stepper.Speed);
            return r;
        }

        // stopat end: the stick goes neutral, held buttons stay
        internal static void Rest(CtlPrep r)
        {
            AgentCore.Release();
            if (!r.hasAfter) return;
            var f = r.after;
            f.steps = 1;
            AgentCore.Enqueue(new List<InputFrame> { f }, true);
        }

        internal static string Apply(CtlPlan p)
        {
            switch (p.kind)
            {
                case CtlKind.Hold: return Hold(p);
                case CtlKind.Release: return Release(p);
                case CtlKind.Mode: return Mode(p);
            }
            return "failed: nothing to apply for " + p.kind;
        }

        static string Note(string w) { return string.IsNullOrEmpty(w) ? "" : " [" + CtlCore.Clip(w, 140) + "]"; }

        static string Frozen()
        {
            return Stepper.Mode == StepMode.Step ? " (step mode: the game is frozen; ctl time run, ctl time step <n> or a timed command moves it)" : "";
        }

        static string Hold(CtlPlan p)
        {
            string w = EnsureAgent();
            InputFrame held;
            bool has = AgentCore.Latched(out held);
            var now = CtlCore.ApplyHold(held, has, p.set);
            AgentCore.Release();
            AgentCore.Enqueue(new List<InputFrame> { now }, true);
            return "ok: holding " + CtlCore.HeldText(now) + Frozen() + Note(w);
        }

        static string Release(CtlPlan p)
        {
            InputFrame held;
            bool has = AgentCore.Latched(out held) && !CtlCore.IsNeutral(held);
            bool queued = AgentCore.QueuedSteps > 0;
            if (p.releaseAll || !has)
            {
                AgentCore.Release();
                return has || queued ? "ok: released everything (neutral input)" : "ok: nothing was held";
            }
            var now = CtlCore.ReleaseParts(held, p.relStick, p.relJump, p.relAction);
            AgentCore.Release();
            if (!CtlCore.IsNeutral(now)) AgentCore.Enqueue(new List<InputFrame> { now }, true);
            return "ok: " + p.what + "; holding " + CtlCore.HeldText(now);
        }

        static string Mode(CtlPlan p)
        {
            if (p.mode == "human")
            {
                AgentCore.Release();
                AgentCore.Uninstall();
                return "ok: human control: the pad and keyboard drive the game again (the next ctl input command takes over)";
            }
            var m = p.mode == "mix" ? InputMode.Mix : InputMode.Agent;
            string w = AgentCore.Install(m);
            return "ok: input mode " + p.mode + (m == InputMode.Mix ? " (ctl input and the pad together)" : " (only ctl and MCP input reach the game)") + Note(w);
        }

        internal static string StatusJson() { return JsonOut.Write(Observer.Concise(null)); }

        static string ModeName(InputMode m) { return m == InputMode.Agent ? "agent" : m == InputMode.Mix ? "mix" : "human"; }

        internal static string BodyLine()
        {
            Json.Obj b = null;
            string reason = null;
            try { b = Perception.Body(false); }
            catch (Exception e) { reason = "unreadable: " + e.GetType().Name + ": " + e.Message; }
            if (b == null && reason == null)
            {
                try { string why; Observer.Ready(out why); reason = why; }
                catch (Exception) { }
            }
            InputFrame held;
            bool has = AgentCore.Latched(out held);
            string clock = CtlCore.Clock(Stepper.Mode == StepMode.Step, Stepper.Frozen, Stepper.Speed);
            string input = CtlCore.Input(ModeName(AgentCore.Installed ? AgentCore.Mode : InputMode.Human), has, held, AgentCore.QueuedSteps);
            return CtlCore.Body(b, reason, clock, input);
        }
    }
}
