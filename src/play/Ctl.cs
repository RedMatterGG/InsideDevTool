using System;
using System.Collections.Generic;
using System.Threading;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // Ctl.cs: the "ctl" console / bridge commands (docs/MCP_PLAY_DESIGN.md section 18): plain-text control of the boy and
    // the huddle for a person or a script, without an MCP client. Reachable from the file bridge (_mod/cmd), the MCP
    // command tool, POST /cmd (ServerPlain in src/mcp/McpServer.cs) and the in-game console (the core asks the extensions
    // for console words it does not know, DevRoot Commands.Run, since 2026-10-25). Input goes only through
    // AgentCore (no OS input, no window focus), time only through the Stepper. Every command that injects input or runs
    // the clock takes the MCP control lane (HostJobs) like an MCP action, so ctl and MCP actions never run at once: the
    // second one gets busy. Instant commands (hold, release, mode, time freeze / run / speed, track rec / stop / save /
    // list ...) change state on the main thread and answer at once; timed ones (stick / press / run / walk / swim with a
    // duration, tap, jump, goto, stopat, seq, track play, time step) start a detached control-lane job on a worker
    // thread (CtlRun.cs): the reply says "started: ... job=jN", ctl job jN shows it, ctl stop, MCP restore and jobs cancel
    // stop it. Every reply is one result line plus the body line (CtlCore.Body); the first word of the result line is
    // what the MCP command tool reads as the outcome (busy:, usage:, refused:, failed: are errors).
    // A second extension class of InsideDev.Play.dll next to PlayExtension: it owns only the ctl word.
    public sealed class CtlExtension : IInsideDevExtension, IDisposable
    {
        public string Name { get { return "Ctl (console control of the boy and the huddle: ctl help)"; } }

        public void Start(DevCore core) { }

        public void Tick(DevCore core)
        {
            CtlJobs.Tick();
        }

        public string Command(List<string> a, DevCore core)
        {
            if (a == null || a.Count == 0 || !string.Equals(a[0], CtlCommands.Word, StringComparison.OrdinalIgnoreCase)) return null;
            return CtlCommands.Run(a);
        }

        public void Dispose() { CtlCommands.CancelOwn("game quit"); }
    }

    public static class CtlCommands
    {
        public const string Word = "ctl", Tool = "ctl";
        public const int InstantBudgetMs = 30000, JobBudgetMs = 660000;

        // main thread (bridge, console, MCP command tool): the reply to one ctl command
        public static string Run(List<string> a)
        {
            var p = CtlCore.Parse(a);
            string line;
            try { line = p.error ?? Exec(p); }
            catch (McpException e) { line = CtlCore.ErrorLine(e.code, e.Message, e.hint); }
            catch (Exception e) { line = "failed: " + e.GetType().Name + ": " + e.Message; }
            if (p.error == null && (p.kind == CtlKind.Help || p.kind == CtlKind.StatusJson)) return line;
            return line + "\n" + SafeBody();
        }

        static string Exec(CtlPlan p)
        {
            switch (p.kind)
            {
                case CtlKind.Help: return CtlCore.Help;
                case CtlKind.Status: return Status();
                case CtlKind.StatusJson: return CtlGame.StatusJson();
                case CtlKind.Job: return JobLine(p.jobId);
                case CtlKind.Stop: return Stop();
                case CtlKind.Hold:
                case CtlKind.Release:
                case CtlKind.Mode:
                    return Instant(p);
                case CtlKind.Input:
                case CtlKind.StopAt:
                    return Start(p);
                case CtlKind.Tool:
                    return p.async ? Start(p) : ToolNow(p);
            }
            return "failed: nothing to do for " + p.kind;
        }

        static string SafeBody()
        {
            try { return CtlGame.BodyLine(); }
            catch (Exception e) { return "body: unavailable (" + e.GetType().Name + ")"; }
        }

        static CallInfo Info(int budgetMs)
        {
            return new CallInfo { profile = McpProfile.All, tool = Tool, client = Word, budgetMs = budgetMs };
        }

        static string BusyLine(ToolResult busy)
        {
            var s = busy != null ? busy.structured : null;
            var e = s != null ? s["error"] as Json.Obj : null;
            var b = s != null ? s["busy"] as Json.Obj : null;
            string code = e != null ? e.Str("code", McpErr.Busy) : McpErr.Busy;
            string msg = e != null ? e.Str("message", "the control lane is busy") : "the control lane is busy";
            if (code != McpErr.Busy) return CtlCore.ErrorLine(code, msg, e != null ? e.Str("hint", null) : null);
            string owner = b != null ? b.Str("tool", "") : "";
            string id = b != null ? b.Str("job", "") : "";
            string hint = owner == Tool ? "ctl job " + id + " shows it, ctl stop cancels it"
                : owner == "mcam" ? "a MultiCam capture holds the game; mcam stop ends it"
                : "an MCP action holds the control lane; wait for it, or MCP restore / jobs cancel stops it";
            return CtlCore.ErrorLine(McpErr.Busy, msg, hint);
        }

        internal static bool IsBusy(ToolResult r)
        {
            var e = r != null && r.structured != null ? r.structured["error"] as Json.Obj : null;
            return e != null && e.Str("code", "") == McpErr.Busy;
        }

        static string Instant(CtlPlan p)
        {
            var info = Info(InstantBudgetMs);
            var ctx = new HostCallContext(info);
            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return BusyLine(busy);
            string line;
            try { line = CtlGame.Apply(p); }
            catch (Exception e)
            {
                job.Fail(McpErr.GameError, e.GetType().Name + ": " + e.Message);
                throw;
            }
            var r = PlayShared.Env();
            r["line"] = line;
            job.Finish(r);
            return line;
        }

        static string Start(CtlPlan p)
        {
            var info = Info(JobBudgetMs);
            var ctx = new HostCallContext(info);
            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return BusyLine(busy);
            job.Detach();
            CtlJobs.Watch(job, p.what);
            var th = new Thread(() => CtlJobs.Run(p, ctx, job)) { IsBackground = true, Name = "InsideDev ctl " + job.Id };
            try { th.Start(); }
            catch (Exception e)
            {
                job.Fail(McpErr.Internal, "could not start the ctl thread: " + e.Message);
                return "failed: could not start the job thread: " + e.Message;
            }
            return "started: " + p.what + " job=" + job.Id + "; ctl job " + job.Id + " shows it, ctl stop cancels it";
        }

        // main thread: a Play tool that answers at once (track ops, time freeze / run / speed / state)
        static string ToolNow(CtlPlan p)
        {
            if (p.tool == "input_track" && p.op == "stop")
            {
                string c = CancelPlayback();
                if (c != null) return c;
            }
            if (p.tool == "time" && p.op == "realtime" && !p.args.ContainsKey("speed")) p.args["speed"] = Stepper.Speed;
            var info = Info(InstantBudgetMs);
            var ctx = new HostCallContext(info);
            var r = CallTool(p.tool, p.args, ctx);
            try { HostJobs.EndOfCall(info, r); } catch (Exception) { }
            if (r.isError && IsBusy(r)) return BusyLine(r);
            return CtlCore.ToolLine(p.tool, p.op, r.structured, r.isError);
        }

        // a playing track is stopped by cancelling its job (the input_track stop op would wait for the main thread)
        static string CancelPlayback()
        {
            var st = new Json.Obj();
            st["op"] = "status";
            var r = CallTool("input_track", st, new HostCallContext(Info(InstantBudgetMs)));
            var s = r.structured;
            if (r.isError || s == null || !s.Bool("playing")) return null;
            var jo = s["job"] as Json.Obj;
            var j = jo != null ? HostJobs.Find(jo.Str("id", "")) : null;
            if (j == null) return null;
            j.Cancel("ctl track stop");
            return "ok: playback cancelled (job " + j.Id + "); it stops at the next frame and the input goes neutral";
        }

        // any thread: a Play tool through its own handler with the given context (defaults filled like the server does)
        internal static ToolResult CallTool(string name, Json.Obj args, CallContext ctx)
        {
            var t = PlayRegistry.Find(name);
            if (t == null) return ToolResult.Fail(McpErr.NotFound, "no play tool " + name, "play status lists the problems of InsideDev.Play");
            string err;
            if (!Schema.Validate(t.def.inputSchema, args, out err)) return ToolResult.Fail(McpErr.BadArgs, err, "ctl help");
            ToolResult r;
            try { r = t.handler(args, ctx); }
            catch (McpException e) { r = ToolResult.Fail(e); }
            catch (NotImplementedException e) { r = ToolResult.Fail(McpErr.NotBuilt, name + ": " + e.Message); }
            catch (Exception e) { r = ToolResult.Fail(McpErr.Internal, name + ": " + e.GetType().Name + ": " + e.Message); }
            return r ?? ToolResult.Fail(McpErr.Internal, name + " returned nothing");
        }

        static string LaneText()
        {
            var j = HostJobs.Running(McpLanes.Control);
            if (j == null) return "free";
            string what = j.Tool == Tool ? CtlJobs.WhatOf(j.Id) : null;
            return "held by " + j.Tool + " job " + j.Id + (what != null ? " (" + what + ")" : "");
        }

        static string Status() { return "ok: control lane " + LaneText(); }

        static string JobLine(string id)
        {
            if (string.IsNullOrEmpty(id)) id = CtlJobs.LastId;
            if (string.IsNullOrEmpty(id)) return "not found: no ctl job yet (timed ctl commands start one)";
            var j = HostJobs.Find(id);
            if (j == null) return "not found: no job " + id + " (finished jobs are kept 10 minutes)";
            string what = CtlJobs.WhatOf(j.Id) ?? j.Tool;
            if (j.State == McpJobState.Running)
            {
                var d = j.Describe();
                string prog = d.ContainsKey("progress") ? ", step " + CtlCore.F(d.Num("progress")) + (d.Num("total") > 0 ? "/" + CtlCore.F(d.Num("total")) : "") : "";
                return "running: " + what + " job=" + j.Id + " for " + CtlCore.F(Math.Round(j.ElapsedMs / 1000.0, 1)) + " s" + prog + (j.Cancelled ? ", cancelling" : "");
            }
            return CtlCore.JobText(j.State, what, j.Result);
        }

        static string Stop()
        {
            var j = HostJobs.Running(McpLanes.Control);
            if (j != null && j.Tool != Tool)
                return "busy: " + j.Tool + " is running (job " + j.Id + "); ctl stop cancels only ctl jobs (MCP restore or jobs cancel stops it)";
            if (j != null) j.Cancel("ctl stop");
            AgentCore.Release();
            return "ok: " + (j != null ? "cancelled job " + j.Id + " (" + (CtlJobs.WhatOf(j.Id) ?? j.Tool) + "), " : "") + "input released";
        }

        // the running ctl job, if any, is cancelled (game quit)
        internal static void CancelOwn(string why)
        {
            try
            {
                var j = HostJobs.Running(McpLanes.Control);
                if (j != null && j.Tool == Tool) j.Cancel(why);
            }
            catch (Exception) { }
        }
    }
}
