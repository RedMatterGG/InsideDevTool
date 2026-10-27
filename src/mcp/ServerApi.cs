using System;
using System.Collections.Generic;

namespace InsideDev.McpExt
{
    // ServerApi.cs (owner SERVER, signatures frozen, docs/MCP_PLAY_DESIGN.md section 4): the MCP contract shared by the
    // server, the in-game host (HOST) and tool providers such as InsideDev.Play.dll. Unity-free and InsideDev-free except
    // Json: compiled into InsideDev.Mcp.dll and InsideDev.McpStdio.exe. The ToJson methods give the wire shapes of the
    // MCP 2025-11-25 schema (tools/list entries, CallToolResult, resources, prompts).

    // Which tool sets a definition belongs to. "all" serves every definition.
    [Flags]
    public enum McpProfiles { None = 0, Play = 1, Dev = 2, Both = 3 }

    public static class McpProfile
    {
        public const string Play = "play", Dev = "dev", All = "all";

        // true when a definition tagged with set is served on profile (play, dev, all)
        public static bool Includes(string profile, McpProfiles set)
        {
            if (profile == All) return set != McpProfiles.None;
            if (profile == Play) return (set & McpProfiles.Play) != 0;
            if (profile == Dev) return (set & McpProfiles.Dev) != 0;
            return false;
        }

        // HTTP path -> profile: /mcp = dev (backward compatible), /mcp/dev, /mcp/play, /mcp/all; null = not an MCP path
        public static string FromPath(string path)
        {
            if (path == null) return null;
            string p = path.TrimEnd('/');
            if (p == "/mcp" || p == "/mcp/dev") return Dev;
            if (p == "/mcp/play") return Play;
            if (p == "/mcp/all") return All;
            return null;
        }
    }

    // Error codes of the structured error envelope {ok:false, error:{code, message, hint}}.
    public static class McpErr
    {
        public const string BadArgs = "bad_args", Busy = "busy", NotReady = "not_ready", StaleId = "stale_id", NotFound = "not_found",
            Ambiguous = "ambiguous", Refused = "refused", Unsafe = "unsafe", ConfirmRequired = "confirm_required", Timeout = "timeout",
            Cancelled = "cancelled", GameError = "game_error", NotBuilt = "not_built", Internal = "internal", Unauthorized = "unauthorized";
    }

    // Thrown by providers (any thread) to return a structured error; the host turns it into ToolResult.Fail.
    public sealed class McpException : Exception
    {
        public readonly string code, hint;
        public Json.Obj data;
        public McpException(string code, string message) : this(code, message, null) { }
        public McpException(string code, string message, string hint) : base(message) { this.code = code; this.hint = hint; }
    }

    // One tool. Annotations are always emitted explicitly (the spec default for a missing destructiveHint is true).
    public sealed class ToolDef
    {
        public string name, title, description;
        public Json.Obj inputSchema, outputSchema;
        public Json.Obj meta;
        public bool readOnly, destructive, idempotent, openWorld;
        public McpProfiles profiles = McpProfiles.Dev;
        public string owner = "";
        public int timeoutMs = 55000;

        public ToolDef ReadOnly() { readOnly = true; idempotent = true; destructive = false; return this; }
        public ToolDef Destructive() { destructive = true; return this; }
        public ToolDef Idempotent() { idempotent = true; return this; }
        public ToolDef Owner(string o) { owner = o ?? ""; return this; }
        public ToolDef Timeout(int ms) { timeoutMs = ms; return this; }
        public ToolDef Meta(string key, object value) { if (meta == null) meta = new Json.Obj(); meta[key] = value; return this; }
        public ToolDef AlwaysLoad() { return Meta("anthropic/alwaysLoad", true); }
        public ToolDef RequiresUser() { return Meta("anthropic/requiresUserInteraction", true); }
        public ToolDef MaxChars(int n) { return Meta("anthropic/maxResultSizeChars", (double)n); }

        // the tools/list entry {name, title, description, inputSchema, outputSchema?, annotations {title, readOnlyHint,
        // destructiveHint, idempotentHint, openWorldHint}, _meta?}. Every hint is explicit: clients read a missing
        // destructiveHint as destructive.
        public Json.Obj ToJson()
        {
            var o = new Json.Obj();
            string t = string.IsNullOrEmpty(title) ? name : title;
            o["name"] = name;
            o["title"] = t;
            o["description"] = description ?? "";
            o["inputSchema"] = inputSchema ?? Schema.Obj().Build();
            if (outputSchema != null) o["outputSchema"] = outputSchema;
            var an = new Json.Obj();
            an["title"] = t; an["readOnlyHint"] = readOnly; an["destructiveHint"] = destructive; an["idempotentHint"] = idempotent; an["openWorldHint"] = openWorld;
            o["annotations"] = an;
            if (meta != null && meta.Count > 0) o["_meta"] = meta;
            return o;
        }
    }

    // Result of tools/call. New tools always set structured (Claude Code drops text blocks when structuredContent is
    // present, so everything the model must read lives in structured); the server adds a compact JSON text copy.
    public sealed class ToolResult
    {
        public readonly List<Json.Obj> content = new List<Json.Obj>();
        public Json.Obj structured;
        public bool isError;

        // legacy text result (the 13 dev tools); the server wraps it as structured {ok, text}
        public static ToolResult Text(string s, bool error = false) { var r = new ToolResult { isError = error }; r.AddText(s); return r; }

        // success: structured gets ok:true as its first key unless it already has ok
        public static ToolResult Ok(Json.Obj structured)
        {
            var s = structured ?? new Json.Obj();
            if (!s.ContainsKey("ok")) { s["ok"] = true; s.order.Remove("ok"); s.order.Insert(0, "ok"); }
            return new ToolResult { structured = s, isError = !(s["ok"] is bool) || !(bool)s["ok"] };
        }

        // failure envelope {ok:false, error:{code, message, hint}}; extra fields may be added to structured afterwards
        public static ToolResult Fail(string code, string message, string hint = null)
        {
            var e = new Json.Obj(); e["code"] = code ?? McpErr.Internal; e["message"] = message ?? ""; if (hint != null) e["hint"] = hint;
            var s = new Json.Obj(); s["ok"] = false; s["error"] = e;
            return new ToolResult { structured = s, isError = true };
        }

        public static ToolResult Fail(McpException e)
        {
            var r = Fail(e.code, e.Message, e.hint);
            if (e.data != null) foreach (var k in e.data.order) r.structured[k] = e.data[k];
            return r;
        }

        // busy envelope: error.code busy plus busy{job, tool, lane, running_s, hint}
        public static ToolResult Busy(McpJob running)
        {
            var r = Fail(McpErr.Busy, "busy: " + (running != null ? running.Tool + " is running (job " + running.Id + ")" : "another action is running"),
                running != null ? "wait for it with wait_until {any:[{job:\"" + running.Id + "\"}]}, or cancel the running call" : "wait a moment and retry, or cancel the running call");
            var b = new Json.Obj();
            if (running != null) { b["job"] = running.Id; b["tool"] = running.Tool; b["lane"] = running.Lane; b["running_s"] = Math.Round(running.ElapsedMs / 1000.0, 1); }
            r.structured["busy"] = b;
            return r;
        }

        public ToolResult AddText(string s) { var c = new Json.Obj(); c["type"] = "text"; c["text"] = s ?? ""; content.Add(c); return this; }
        public ToolResult AddImage(byte[] png, string mime) { var c = new Json.Obj(); c["type"] = "image"; c["data"] = Convert.ToBase64String(png); c["mimeType"] = mime; content.Add(c); return this; }
        public ToolResult AddResourceLink(string uri, string name, string mimeType)
        {
            var c = new Json.Obj(); c["type"] = "resource_link"; c["uri"] = uri; c["name"] = name ?? uri; if (mimeType != null) c["mimeType"] = mimeType; content.Add(c); return this;
        }

        // the compact JSON copy of structured in the first text block is cut at this many chars
        internal const int TextCopyCap = 100000;

        // CallToolResult {content, structuredContent, isError}. content = the compact JSON of structured (cut at TextCopyCap
        // with ..."truncated"), then image / resource_link blocks, then text blocks whose text is not already a top-level
        // value of structured. A legacy result without structured gets structured {ok: !isError, text: its text blocks}.
        // isError is also true when structured.ok is false.
        public Json.Obj ToJson()
        {
            var s = structured;
            bool legacy = s == null;
            if (legacy)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var c in content)
                    if (c != null && c.Str("type") == "text") { if (sb.Length > 0) sb.Append('\n'); sb.Append(c.Str("text", "")); }
                s = new Json.Obj(); s["ok"] = !isError; s["text"] = sb.ToString();
            }
            bool err = isError || (s["ok"] is bool && !(bool)s["ok"]);
            string json = JsonOut.Write(s);
            if (json.Length > TextCopyCap) json = json.Substring(0, TextCopyCap) + "...\"truncated\"";
            var blocks = new List<object>();
            var first = new Json.Obj(); first["type"] = "text"; first["text"] = json;
            blocks.Add(first);
            foreach (var c in content)
            {
                if (c == null) continue;
                if (c.Str("type") != "text") { blocks.Add(c); continue; }
                if (legacy || HasValue(s, c.Str("text", ""))) continue;
                blocks.Add(c);
            }
            var o = new Json.Obj();
            o["content"] = blocks;
            o["structuredContent"] = s;
            o["isError"] = err;
            return o;
        }

        static bool HasValue(Json.Obj s, string text)
        {
            foreach (var k in s.order) { var v = s[k] as string; if (v != null && v == text) return true; }
            return false;
        }
    }

    public sealed class ResourceDef
    {
        public string uri, name, title, description, mimeType = "text/markdown";
        public McpProfiles profiles = McpProfiles.Both;
        // resources/list entry {uri, name, title?, description?, mimeType?}
        public Json.Obj ToJson()
        {
            var o = new Json.Obj(); o["uri"] = uri ?? ""; o["name"] = string.IsNullOrEmpty(name) ? uri ?? "" : name;
            if (!string.IsNullOrEmpty(title)) o["title"] = title;
            if (!string.IsNullOrEmpty(description)) o["description"] = description;
            if (!string.IsNullOrEmpty(mimeType)) o["mimeType"] = mimeType;
            return o;
        }
    }

    public sealed class ResourceTemplateDef
    {
        public string uriTemplate, name, title, description, mimeType = "text/markdown";
        public McpProfiles profiles = McpProfiles.Both;
        // resources/templates/list entry {uriTemplate, name, title?, description?, mimeType?}
        public Json.Obj ToJson()
        {
            var o = new Json.Obj(); o["uriTemplate"] = uriTemplate ?? ""; o["name"] = string.IsNullOrEmpty(name) ? uriTemplate ?? "" : name;
            if (!string.IsNullOrEmpty(title)) o["title"] = title;
            if (!string.IsNullOrEmpty(description)) o["description"] = description;
            if (!string.IsNullOrEmpty(mimeType)) o["mimeType"] = mimeType;
            return o;
        }
    }

    // resources/read result for one uri (text or blob)
    public sealed class ResourceContents
    {
        public string uri, mimeType = "text/markdown", text;
        public byte[] blob;
        // one entry of resources/read contents: {uri, mimeType?, text} or {uri, mimeType?, blob (base64)}
        public Json.Obj ToJson()
        {
            var o = new Json.Obj(); o["uri"] = uri ?? "";
            if (!string.IsNullOrEmpty(mimeType)) o["mimeType"] = mimeType;
            if (blob != null) o["blob"] = Convert.ToBase64String(blob); else o["text"] = text ?? "";
            return o;
        }
    }

    public sealed class PromptArg
    {
        public string name, description;
        public bool required;
    }

    public sealed class PromptDef
    {
        public string name, title, description;
        public readonly List<PromptArg> arguments = new List<PromptArg>();
        public McpProfiles profiles = McpProfiles.Both;
        // prompts/list entry {name, title?, description?, arguments [{name, description?, required}]}
        public Json.Obj ToJson()
        {
            var o = new Json.Obj(); o["name"] = name ?? "";
            if (!string.IsNullOrEmpty(title)) o["title"] = title;
            if (!string.IsNullOrEmpty(description)) o["description"] = description;
            var l = new List<object>();
            foreach (var a in arguments)
            {
                if (a == null) continue;
                var x = new Json.Obj(); x["name"] = a.name ?? "";
                if (!string.IsNullOrEmpty(a.description)) x["description"] = a.description;
                x["required"] = a.required;
                l.Add(x);
            }
            o["arguments"] = l;
            return o;
        }
    }

    public sealed class PromptMessage
    {
        public string role = "user", text;
    }

    public sealed class PromptResult
    {
        public string description;
        public readonly List<PromptMessage> messages = new List<PromptMessage>();
        // prompts/get result {description?, messages [{role, content {type:text, text}}]}
        public Json.Obj ToJson()
        {
            var o = new Json.Obj();
            if (!string.IsNullOrEmpty(description)) o["description"] = description;
            var l = new List<object>();
            foreach (var m in messages)
            {
                if (m == null) continue;
                var c = new Json.Obj(); c["type"] = "text"; c["text"] = m.text ?? "";
                var x = new Json.Obj(); x["role"] = m.role == "assistant" ? "assistant" : "user"; x["content"] = c;
                l.Add(x);
            }
            o["messages"] = l;
            return o;
        }
    }

    // Created by the server for every tools/call, resources/read and prompts/get. The server keeps (session, requestId)
    // -> CallInfo so notifications/cancelled reaches it; progress is null unless the request carried a progressToken and
    // the reply is an SSE stream.
    public sealed class CallInfo
    {
        public string profile = McpProfile.Dev, tool = "", sessionId = "", client = "";
        public object requestId, progressToken;
        public readonly DateTime startedUtc = DateTime.UtcNow;
        public int budgetMs = 55000;
        public Action<double, double, string> progress;
        volatile bool cancelled;
        string cancelReason = "";

        public bool Cancelled { get { return cancelled; } }
        public string CancelReason { get { return cancelReason; } }
        public void Cancel(string reason) { cancelReason = reason ?? "cancelled"; cancelled = true; }
        public double ElapsedMs { get { return (DateTime.UtcNow - startedUtc).TotalMilliseconds; } }
        public int RemainingMs { get { return Math.Max(0, budgetMs - (int)ElapsedMs); } }
    }

    // Lanes of exclusive work. control = anything that advances game time, injects input, loads, or holds the camera
    // over several frames. One job per lane; a second request gets ToolResult.Busy.
    public static class McpLanes
    {
        public const string Control = "control";
    }

    public static class McpJobState
    {
        public const string Running = "running", Done = "done", Failed = "failed", Cancelled = "cancelled";
    }

    // A multi-frame job (HOST implements, Jobs.cs). Created by CallContext.BeginJob; a job ends with Finish or Fail. A
    // job that is not Detached is finished automatically when its tool call returns.
    public abstract class McpJob
    {
        public abstract string Id { get; }
        public abstract string Tool { get; }
        public abstract string Lane { get; }
        public abstract string State { get; }
        public abstract double ElapsedMs { get; }
        public abstract bool Cancelled { get; }
        public abstract string CancelReason { get; }
        public abstract void Cancel(string reason);
        public abstract void Progress(double progress, double total, string message);
        // keep the job (and its lane) alive after the tool call returns, e.g. a load that is still fading in
        public abstract void Detach();
        public abstract void Finish(Json.Obj result);
        public abstract void Fail(string code, string message);
        // worker thread: true when the job ended within timeoutMs
        public abstract bool Await(int timeoutMs);
        public abstract Json.Obj Result { get; }
        // {id, tool, lane, state, elapsed_s, progress, total, message, error}
        public abstract Json.Obj Describe();
    }

    // Handed to providers on the worker thread that runs the call (HOST implements, HostContext.cs).
    public abstract class CallContext
    {
        public abstract CallInfo Info { get; }
        public string Tool { get { return Info.tool; } }
        public string Profile { get { return Info.profile; } }
        public bool Cancelled { get { return Info.Cancelled; } }
        public int RemainingMs { get { return Info.RemainingMs; } }

        // notifications/progress when the client asked for it; rate-limited by the host; no-op otherwise
        public abstract void Progress(double progress, double total, string message);

        // run fn on the Unity main thread and wait for it. Throws McpException not_ready when the main thread did not
        // pick it up within timeoutMs (the job is then dropped, never run later) and cancelled when the call is cancelled
        // before it ran. Exceptions inside fn come back as McpException game_error (McpException passes through).
        public abstract T Main<T>(Func<T> fn, int timeoutMs = 10000);
        public abstract void MainDo(Action fn, int timeoutMs = 10000);

        // worker thread: wait for n rendered frames; false on timeout or cancel
        public abstract bool WaitFrames(int frames, int timeoutMs);
        // worker thread: evaluate check on the main thread every everyFrames frames until it returns true; false on
        // timeout or cancel. check must be cheap (well under 1 ms).
        public abstract bool WaitUntil(Func<bool> check, int timeoutMs, int everyFrames = 1);
        // cancellable sleep on the worker thread; false when cancelled
        public abstract bool Sleep(int ms);
        // Time.frameCount as last seen by the host tick (any thread)
        public abstract long Frame { get; }

        // take the lane for this call. null plus busy when another job holds it (or a MultiCam capture runs)
        public abstract McpJob BeginJob(string lane, out ToolResult busy);
        public abstract McpJob FindJob(string id);
        public abstract McpJob RunningJob(string lane);

        public abstract void Log(string msg);
        // one line in _mod/mcp_audit.jsonl (the host also audits every non-read-only call by itself)
        public abstract void Audit(string what, Json.Obj detail);

        public void ThrowIfCancelled() { if (Info.Cancelled) throw new McpException(McpErr.Cancelled, "cancelled: " + Info.CancelReason); }
    }

    // Anything that contributes tools, resources and prompts (the host's own dev tools, InsideDev.Play.dll, later
    // extensions). Registered with McpRegistry.Add; the lists are re-read after McpRegistry.Changed().
    public interface IMcpToolProvider
    {
        string ProviderName { get; }
        List<ToolDef> Tools();
        // worker thread; args are already validated and defaults filled by the server
        ToolResult Call(string tool, Json.Obj args, CallContext ctx);
        List<ResourceDef> Resources();
        List<ResourceTemplateDef> ResourceTemplates();
        // null = not mine
        ResourceContents ReadResource(string uri, CallContext ctx);
        List<PromptDef> Prompts();
        // null = not mine
        PromptResult GetPrompt(string name, Json.Obj args, CallContext ctx);
        // a paragraph for InitializeResult.instructions of that profile, or null
        string Instructions(string profile);
        // main thread: return the game to normal gameplay (release input, unfreeze, camera back, ...); append what was reset
        void Restore(List<string> report);
    }

    // What the server needs from the in-game host (McpExtension). The server validates arguments against
    // ToolDef.inputSchema (Schema.Validate) before Call; Call runs on the connection's worker thread.
    public interface IToolHost
    {
        string ServerVersion { get; }
        string Instructions(string profile);
        List<ToolDef> Tools(string profile);
        ToolResult Call(ToolDef tool, Json.Obj args, CallInfo call);
        List<ResourceDef> Resources(string profile);
        List<ResourceTemplateDef> ResourceTemplates(string profile);
        ResourceContents ReadResource(string uri, CallInfo call);
        List<PromptDef> Prompts(string profile);
        PromptResult GetPrompt(string name, Json.Obj args, CallInfo call);
        // bumps whenever tools, resources or prompts change (-> notifications/tools/list_changed)
        long ListVersion { get; }
        // empty = no auth; otherwise HTTP requests need "Authorization: Bearer <token>"
        string AuthToken { get; }
        // tool allowlist (EditorState mcp.tools); false = hidden and refused
        bool Allowed(string profile, string tool);
        void Log(string msg);
    }
}
