using System;
using System.Collections.Generic;
using System.Threading;

namespace InsideDev.McpExt
{
    // HostDispatch.cs (owner HOST): the call path behind IToolHost.Call / ReadResource / GetPrompt, Unity-free so the
    // offline tests drive it with fake providers. A call: profile and allowlist checks, owner lookup, a second strict
    // Schema.Validate (fills defaults; HostText.CheckArgs checks the top level when Validate is not built), then the provider runs on its own thread while the server's worker waits at
    // most the tool budget (CallInfo.budgetMs, 55 s) plus a 2.5 s grace. On time: HostJobs.EndOfCall, progress flush,
    // audit. Over time: the reply is error timeout; a call that holds a job keeps running as that job (the client awaits
    // it with jobs / wait_until {job}) and its late result lands in the job; a call without a job is cancelled, so its
    // pending main-thread items are dropped and never run. Results are normalized: structured has ok first and
    // isError == !ok.
    internal static class HostDispatch
    {
        internal static int GraceMs = 2500;
        internal const int AuditArgsChars = 2000;
        static long lastCallTicks;
        static volatile bool callsSinceRestore;
        static int active;

        internal static int Active { get { return Interlocked.CompareExchange(ref active, 0, 0); } }
        internal static DateTime LastCallUtc { get { long t = Interlocked.Read(ref lastCallTicks); return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc); } }

        // true once: no call for idleS seconds after at least one call since the last restore, and nothing running
        internal static bool IdleRestoreDue(int idleS)
        {
            if (idleS <= 0 || !callsSinceRestore || Active > 0) return false;
            if (HostJobs.Running(McpLanes.Control) != null) return false;
            if ((DateTime.UtcNow - LastCallUtc).TotalSeconds < idleS) return false;
            callsSinceRestore = false;
            return true;
        }

        internal static void MarkRestored() { callsSinceRestore = false; }

        sealed class Runner
        {
            public readonly object gate = new object();
            public bool done;
            public ToolResult result;
            public volatile bool answered;
        }

        internal static ToolResult Call(ToolDef def, Json.Obj args, CallInfo info)
        {
            if (info == null) info = new CallInfo { tool = def != null ? def.name : "" };
            if (def == null) return ToolResult.Fail(McpErr.NotFound, "unknown tool");
            if (string.IsNullOrEmpty(info.tool)) info.tool = def.name;
            if (info.budgetMs <= 0) info.budgetMs = def.timeoutMs > 0 ? def.timeoutMs : 55000;
            Interlocked.Exchange(ref lastCallTicks, DateTime.UtcNow.Ticks);
            callsSinceRestore = true;
            HostJobs.Sweep();
            if (!McpProfile.Includes(info.profile, def.profiles))
                return ToolResult.Fail(McpErr.NotFound, def.name + " is not served on the " + info.profile + " profile", "use /mcp/all or the profile that lists it");
            if (!HostSettings.Allowed(info.profile, def.name))
                return Audited(def, info, args, ToolResult.Fail(McpErr.Refused, def.name + " is not in the tool allowlist (EditorState mcp.tools)", "ask the user to allow it"), 0);
            var provider = McpRegistry.OwnerOf(def.name);
            if (provider == null) return ToolResult.Fail(McpErr.NotFound, "no provider serves " + def.name, "the extension that owns it is not loaded");
            if (args == null) args = new Json.Obj();
            string verr = null;
            bool valid = true;
            try { valid = Schema.Validate(def.inputSchema, args, out verr); }
            catch (NotImplementedException) { valid = HostText.CheckArgs(def.inputSchema, args, out verr); }
            if (!valid) return Audited(def, info, args, ToolResult.Fail(McpErr.BadArgs, verr, "see the tool's inputSchema"), 0);

            var ctx = new HostCallContext(info);
            var run = new Runner();
            var started = DateTime.UtcNow;
            string argText = AuditArgs(args);
            Interlocked.Increment(ref active);
            var th = new Thread(() => Execute(provider, def, args, ctx, run, started, argText)) { IsBackground = true, Name = "InsideDev MCP " + def.name };
            try { th.Start(); }
            catch (Exception e)
            {
                Interlocked.Decrement(ref active);
                return ToolResult.Fail(McpErr.Internal, "could not start the call thread: " + e.Message);
            }
            int wait = info.budgetMs + GraceMs;
            lock (run.gate)
            {
                var until = started.AddMilliseconds(wait);
                while (!run.done)
                {
                    int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) break;
                    Monitor.Wait(run.gate, Math.Min(left, 500));
                }
                if (run.done) { run.answered = true; return run.result; }
                run.answered = true;
            }
            ctx.CutWire();
            var jobs = HostJobs.OfCall(info);
            if (jobs.Count == 0)
                lock (run.gate)
                {
                    if (!run.done) Monitor.Wait(run.gate, 200);
                    if (run.done) return run.result;
                }
            ToolResult tr;
            if (jobs.Count > 0)
            {
                var j = jobs[0];
                j.autoDetached = true;
                j.Detach();
                tr = ToolResult.Fail(McpErr.Timeout, def.name + " is still running after " + (wait / 1000) + " s; it continues as job " + j.Id,
                    info.profile == McpProfile.Play
                        ? "wait for it with wait_until {any:[{job:\"" + j.Id + "\"}]}; restore stops it"
                        : "wait for it with jobs {op:\"await\", id:\"" + j.Id + "\"} or wait_until {any:[{job:\"" + j.Id + "\"}]}; restore or jobs {op:\"cancel\"} stops it");
                tr.structured["job"] = j.Describe();
            }
            else
            {
                info.Cancel("timeout");
                tr = ToolResult.Fail(McpErr.Timeout, def.name + " did not finish within " + (wait / 1000) + " s and was cancelled",
                    "nothing it queued for the game runs later; retry with a smaller request");
            }
            if (!def.readOnly) AuditLog.Write(info, "timeout", AuditDetail(argText, tr, (DateTime.UtcNow - started).TotalMilliseconds));
            return tr;
        }

        static void Execute(IMcpToolProvider provider, ToolDef def, Json.Obj args, HostCallContext ctx, Runner run, DateTime started, string argText)
        {
            ToolResult r = null;
            try
            {
                try { r = provider.Call(def.name, args, ctx); }
                catch (McpException e) { r = ToolResult.Fail(e); }
                catch (NotImplementedException e) { r = ToolResult.Fail(McpErr.NotBuilt, def.name + ": " + e.Message, "owner " + def.owner + " has not implemented it"); }
                catch (ThreadAbortException) { r = ToolResult.Fail(McpErr.Internal, def.name + " was aborted"); }
                catch (Exception e)
                {
                    var inner = e;
                    while (inner is System.Reflection.TargetInvocationException && inner.InnerException != null) inner = inner.InnerException;
                    var me = inner as McpException;
                    r = me != null ? ToolResult.Fail(me) : ToolResult.Fail(McpErr.Internal, def.name + ": " + inner.GetType().Name + ": " + inner.Message);
                }
                if (r == null) r = ToolResult.Fail(McpErr.Internal, def.name + " returned nothing");
                Normalize(r);
                try { HostJobs.EndOfCall(ctx.Info, r); } catch (Exception e) { HostLog.Write("EndOfCall " + def.name + ": " + e.Message); }
                ctx.FlushProgress();
                ctx.CutWire();
            }
            finally
            {
                Interlocked.Decrement(ref active);
                bool late;
                lock (run.gate) late = run.answered;
                if (r != null && !def.readOnly) AuditLog.Write(ctx.Info, late ? "late_result" : "call", AuditDetail(argText, r, (DateTime.UtcNow - started).TotalMilliseconds));
                lock (run.gate) { run.result = r; run.done = true; Monitor.PulseAll(run.gate); }
            }
        }

        // structured gets ok first; isError follows ok
        internal static void Normalize(ToolResult r)
        {
            var s = r.structured;
            if (s == null) return;
            if (!s.ContainsKey("ok") || !(s["ok"] is bool))
            {
                s["ok"] = !r.isError;
                s.order.Remove("ok"); s.order.Insert(0, "ok");
            }
            else if (s.order.Count > 0 && s.order[0] != "ok") { s.order.Remove("ok"); s.order.Insert(0, "ok"); }
            r.isError = !(bool)s["ok"];
            if (r.isError && !(s["error"] is Json.Obj))
            {
                var e = new Json.Obj(); e["code"] = McpErr.Internal; e["message"] = s.Str("text", "failed");
                s["error"] = e;
            }
        }

        static ToolResult Audited(ToolDef def, CallInfo info, Json.Obj args, ToolResult r, double ms)
        {
            if (!def.readOnly) AuditLog.Write(info, "call", AuditDetail(AuditArgs(args), r, ms));
            return r;
        }

        static string AuditArgs(Json.Obj args)
        {
            try { return HostText.Cut(JsonOut.Write(args ?? new Json.Obj()), AuditArgsChars); } catch (Exception e) { return "<" + e.Message + ">"; }
        }

        static Json.Obj AuditDetail(string args, ToolResult r, double ms)
        {
            var d = new Json.Obj();
            d["args"] = args;
            string outcome = "ok";
            if (r != null && r.isError)
            {
                var e = r.structured != null ? r.structured["error"] as Json.Obj : null;
                outcome = e != null ? e.Str("code", "error") : "error";
                if (e != null) d["message"] = HostText.Cut(e.Str("message", ""), 300);
            }
            else if (r != null && r.structured != null && r.structured["outcome"] is string) outcome = "ok:" + r.structured.Str("outcome");
            d["outcome"] = outcome;
            d["ms"] = Math.Round(ms);
            return d;
        }

        internal static ResourceContents ReadResource(string uri, CallInfo info)
        {
            if (info == null) info = new CallInfo();
            Interlocked.Exchange(ref lastCallTicks, DateTime.UtcNow.Ticks);
            var ctx = new HostCallContext(info);
            foreach (var p in McpRegistry.Providers())
            {
                ResourceContents c;
                try { c = p.ReadResource(uri, ctx); }
                catch (McpException) { throw; }
                catch (NotImplementedException) { continue; }
                catch (Exception e) { throw new McpException(McpErr.Internal, Name(p) + ": " + e.GetType().Name + ": " + e.Message); }
                if (c != null) { if (c.uri == null) c.uri = uri; return c; }
            }
            return null;
        }

        internal static PromptResult GetPrompt(string name, Json.Obj args, CallInfo info)
        {
            if (info == null) info = new CallInfo();
            Interlocked.Exchange(ref lastCallTicks, DateTime.UtcNow.Ticks);
            var ctx = new HostCallContext(info);
            foreach (var p in McpRegistry.Providers())
            {
                PromptResult r;
                try { r = p.GetPrompt(name, args ?? new Json.Obj(), ctx); }
                catch (McpException) { throw; }
                catch (NotImplementedException) { continue; }
                catch (Exception e) { throw new McpException(McpErr.Internal, Name(p) + ": " + e.GetType().Name + ": " + e.Message); }
                if (r != null) return r;
            }
            return null;
        }

        static string Name(IMcpToolProvider p) { try { return p.ProviderName; } catch { return p.GetType().Name; } }
    }
}
