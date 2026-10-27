using System;
using System.Collections.Generic;
using System.Globalization;

namespace InsideDev.McpExt
{
    // HostText.cs (owner HOST): Unity-free helpers of the dev tools and the host policy. Output caps and paging of
    // legacy text, error classification of bridge replies ("not found", "usage", "busy: mcam ...", "ERROR in ..."),
    // refusals of the command tool (OS input, server stop, and, only with EditorState play.save_guard on, game loads that
    // skip the save backup and confirm: load, spawn, reload, respawn, cspawn go / respawn), the secret / achievement type
    // names the dev tools refuse to drive, strict argument readers (a string never becomes 0) and the cached EditorState
    // settings the worker threads read (mcp.tools allowlist, mcp.token, mcp.idle_restore_s, mcp.restore_on_close_s,
    // play.save_guard).
    internal static class HostText
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        internal const int DefaultCap = 20000;

        internal sealed class Page
        {
            public string text = "";
            public int offset, total, next = -1;
            public bool truncated;
        }

        // the slice [offset, offset + max) of text, cut after a line break near the end when there is one
        internal static Page Slice(string text, int offset, int max)
        {
            var p = new Page();
            text = text ?? "";
            p.total = text.Length;
            if (max <= 0) max = DefaultCap;
            if (offset < 0) offset = 0;
            if (offset > p.total) offset = p.total;
            p.offset = offset;
            int len = Math.Min(max, p.total - offset);
            if (offset + len < p.total)
            {
                int nl = text.LastIndexOf('\n', offset + len - 1, len);
                if (nl >= 0 && nl + 1 - offset >= len * 9 / 10) len = nl + 1 - offset;
                if (len > 0 && char.IsHighSurrogate(text[offset + len - 1])) len--;
                p.truncated = true;
                p.next = offset + len;
            }
            p.text = text.Substring(offset, len);
            return p;
        }

        // the last max characters (log tails), cut at a line start
        internal static Page Tail(string text, int max)
        {
            var p = new Page();
            text = text ?? "";
            p.total = text.Length;
            if (max <= 0) max = DefaultCap;
            if (p.total <= max) { p.text = text; return p; }
            int from = p.total - max;
            int nl = text.IndexOf('\n', from);
            if (nl >= 0 && nl - from < max / 10) from = nl + 1;
            p.offset = from; p.truncated = true;
            p.text = text.Substring(from);
            return p;
        }

        // structured result {ok, text, truncated, total_chars, offset, next_offset}; code != null makes it an error
        internal static ToolResult Result(Page p, string code, string message, bool pageable)
        {
            string text = p.text;
            if (p.truncated)
            {
                if (p.next >= 0)
                    text += "\n... [truncated: characters " + p.offset + "-" + p.next + " of " + p.total +
                            (pageable ? "; call again with offset " + p.next + "]" : "; narrow the request or raise max_chars]");
                else text = "... [first " + p.offset + " of " + p.total + " characters left out]\n" + text;
            }
            ToolResult r = code != null ? ToolResult.Fail(code, message ?? FirstLine(p.text), HintFor(code)) : ToolResult.Ok(new Json.Obj());
            var s = r.structured;
            s["text"] = text;
            s["truncated"] = p.truncated;
            s["total_chars"] = (double)p.total;
            s["offset"] = (double)p.offset;
            if (p.next >= 0) s["next_offset"] = (double)p.next;
            return r;
        }

        internal static ToolResult Text(string text, int offset, int max, bool pageable)
        {
            var p = Slice(text, offset, max);
            string code = Classify(text);
            return Result(p, code, code != null ? FirstLine(text) : null, pageable);
        }

        internal static string HintFor(string code)
        {
            switch (code)
            {
                case McpErr.NotFound: return "check the name with find_objects; use an id from observe, a full hierarchy path or #instanceID";
                case McpErr.BadArgs: return "command 'help' lists the bridge commands and their arguments";
                case McpErr.Busy: return "a MultiCam capture or another job holds the game; wait for it (jobs) or stop it";
                case McpErr.NotReady: return "no playable character now (title, loading or dead); try again after the load";
                case McpErr.Refused: return "not available through MCP";
                case McpErr.NotBuilt: return "this feature is not in the loaded InsideDev build";
                case McpErr.GameError: return "see the text and the log tool for details";
            }
            return null;
        }

        internal static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            foreach (var raw in s.Split('\n'))
            {
                var l = raw.Trim();
                if (l.Length > 0) return l.Length > 300 ? l.Substring(0, 300) + "..." : l;
            }
            return "";
        }

        static readonly string[][] Starts =
        {
            new[] { "not found", McpErr.NotFound }, new[] { "usage:", McpErr.BadArgs }, new[] { "usage ", McpErr.BadArgs },
            new[] { "unknown ", McpErr.BadArgs }, new[] { "unknown:", McpErr.BadArgs }, new[] { "busy:", McpErr.Busy }, new[] { "busy ", McpErr.Busy },
            new[] { "no fsm", McpErr.NotFound }, new[] { "refused", McpErr.Refused }, new[] { "failed:", McpErr.GameError }, new[] { "failed to ", McpErr.GameError },
            new[] { "error:", McpErr.GameError }, new[] { "error in ", McpErr.GameError }, new[] { "error -", McpErr.GameError },
            new[] { "cannot ", McpErr.GameError }, new[] { "can't ", McpErr.GameError }, new[] { "could not ", McpErr.GameError }, new[] { "couldn't ", McpErr.GameError },
            new[] { "no active ", McpErr.NotReady }, new[] { "not built", McpErr.NotBuilt }, new[] { "object scan not finished", McpErr.NotReady },
        };

        static readonly string[][] Contains =
        {
            new[] { "component not found", McpErr.NotFound }, new[] { "field/property not found", McpErr.NotFound },
            new[] { "no parameterless method", McpErr.NotFound }, new[] { "no active character", McpErr.NotReady },
            new[] { " failed:", McpErr.GameError }, new[] { "not built", McpErr.NotBuilt },
        };

        // error code of a bridge reply, judged on its first line only (dumps further down may contain any text); null = ok
        internal static string Classify(string reply)
        {
            if (reply == null) return null;
            string first = FirstLine(reply);
            if (first.Length == 0) return null;
            if (first.IndexOf("ERROR in ", StringComparison.Ordinal) >= 0) return McpErr.GameError;
            string low = first.ToLowerInvariant();
            if (low == "not found") return McpErr.NotFound;
            foreach (var s in Starts) if (low.StartsWith(s[0], StringComparison.Ordinal)) return s[1];
            foreach (var s in Contains) if (low.IndexOf(s[0], StringComparison.Ordinal) >= 0) return s[1];
            if (low.EndsWith(" failed", StringComparison.Ordinal)) return McpErr.GameError;
            return null;
        }

        // reason text when the command tool must not run line, else null
        internal static string Refusal(string line)
        {
            if (line == null) return "empty command";
            if (line.IndexOf('\n') >= 0 || line.IndexOf('\r') >= 0) return "one command per call (the line contains a line break)";
            var a = Words(line);
            if (a.Count == 0) return "empty command";
            string c = a[0].ToLowerInvariant();
            string s1 = a.Count > 1 ? a[1].ToLowerInvariant() : "";
            if (c == "keyhold" || c == "focustest") return c + " sends real OS keyboard input; use the act tool (input through AgentCore, no window focus needed)";
            if (c == "remote" && s1 == "off") return "remote off cannot be undone without a game restart";
            if (c == "mcp" && (s1 == "off" || s1 == "port")) return "mcp " + s1 + " would stop this MCP server from inside a call; use the in-game console";
            if (!HostSettings.SaveGuard) return null;
            if (c == "load" || c == "spawn" || c == "reload" || c == "respawn")
                return c + " loads through the game without the save backup or confirm (a chapter load writes INSIDE.sav and unlocks chapters forever); use level {op:\"load_checkpoint\" | \"reload\"} or checkpoint {op:\"reload\"}";
            if (c == "cspawn" && (s1 == "go" || s1 == "respawn"))
                return "cspawn " + s1 + " loads a savepoint through the game without the save backup or confirm; use level {op:\"load_checkpoint\"}, then body {op:\"teleport\"}";
            return null;
        }

        // game classes whose code calls User.Achievement.Acquire (a Steam call) or ResetProgression (clears every orb
        // bit): AC/FinalSecretFade.cs:100-106, AC/SecretPod.cs:443-479, SecretPodCeiling.cs:190, SecretPodLarge.cs:281,
        // Secret.cs:366, SecretSuitcase.cs:148, AchiementTrigger.cs:9 (Playdead's spelling), FP/StartNewGameMenuItem.cs:23
        internal static readonly string[] SecretTypes = { "FinalSecretFade", "SecretPod", "SecretPodCeiling", "SecretPodLarge", "Secret", "SecretSuitcase", "AchiementTrigger", "AchievementTrigger", "StartNewGameMenuItem" };

        internal static bool SecretType(string typeName)
        {
            return typeName != null && Array.IndexOf(SecretTypes, typeName) >= 0;
        }

        // dev command verbs that drive an object: the object word (index into Words, or -1 = first '|' field of the rest)
        internal static bool DrivesObject(string verb, out int at, out bool follow, out bool byComponent)
        {
            at = 1; follow = false; byComponent = false;
            switch (verb)
            {
                case "signal": follow = true; return true;
                case "signalin": case "fsm": return true;
                case "call": case "set": byComponent = true; return true;
                case "fsmset": case "sigwire": at = -1; return true;
            }
            return false;
        }

        internal const string SecretHint = "orb bits: save {op:\"orbs\", confirm:true}; the ending: save {op:\"ending\", kind:\"secret\", confirm:true} (no Steam call)";

        internal static List<string> Words(string line)
        {
            var r = new List<string>();
            var sb = new System.Text.StringBuilder();
            bool q = false;
            foreach (char ch in line)
            {
                if (ch == '"') { q = !q; continue; }
                if (!q && (ch == ' ' || ch == '\t')) { if (sb.Length > 0) { r.Add(sb.ToString()); sb.Length = 0; } continue; }
                sb.Append(ch);
            }
            if (sb.Length > 0) r.Add(sb.ToString());
            return r;
        }

        // ---------------------------------------------------------------- strict argument readers
        internal static int Int(Json.Obj a, string k, int def)
        {
            var v = a != null ? a[k] : null;
            if (v == null) return def;
            if (v is double)
            {
                double d = (double)v;
                if (d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue) return (int)d;
            }
            if (v is int) return (int)v;
            if (v is long && (long)v >= int.MinValue && (long)v <= int.MaxValue) return (int)(long)v;
            throw new McpException(McpErr.BadArgs, k + ": expected an integer, got " + Show(v), "see the tool's inputSchema");
        }

        // fallback when Schema.Validate is not built: top-level keys only (unknown keys, required, type, enum, range,
        // array size); null for an optional key counts as absent; fills top-level defaults
        internal static bool CheckArgs(Json.Obj schema, Json.Obj args, out string error)
        {
            error = null;
            if (schema == null || args == null) return true;
            var props = schema["properties"] as Json.Obj ?? new Json.Obj();
            var req = schema["required"] as List<object> ?? new List<object>();
            bool closed = schema["additionalProperties"] is bool && !(bool)schema["additionalProperties"];
            foreach (var k in new List<string>(args.order))
            {
                if (!args.ContainsKey(k)) continue;
                var ps = props[k] as Json.Obj;
                if (ps == null)
                {
                    if (closed && !props.ContainsKey(k)) { error = k + ": unknown property; allowed: " + string.Join(", ", props.order.ToArray()); return false; }
                    continue;
                }
                var v = args[k];
                if (v == null) { if (!req.Contains(k)) { args.Remove(k); args.order.Remove(k); } continue; }
                string type = ps["type"] as string;
                bool ok = true;
                switch (type)
                {
                    case "string": ok = v is string; break;
                    case "boolean": ok = v is bool; break;
                    case "number": ok = v is double; break;
                    case "integer": ok = v is double && (double)v == Math.Floor((double)v) && !double.IsInfinity((double)v); break;
                    case "array": ok = v is List<object>; break;
                    case "object": ok = v is Json.Obj; break;
                }
                if (!ok) { error = k + ": expected " + (type == "integer" ? "an integer" : type) + ", got " + Show(v); return false; }
                var en = ps["enum"] as List<object>;
                if (en != null && !en.Contains(v)) { error = k + ": " + Show(v) + " is not one of " + string.Join(", ", ToStrings(en)); return false; }
                if (v is double)
                {
                    double d = (double)v;
                    if (ps["minimum"] is double && d < (double)ps["minimum"]) { error = k + ": " + d.ToString("R", IC) + " is below the minimum " + ((double)ps["minimum"]).ToString("R", IC); return false; }
                    if (ps["maximum"] is double && d > (double)ps["maximum"]) { error = k + ": " + d.ToString("R", IC) + " is above the maximum " + ((double)ps["maximum"]).ToString("R", IC); return false; }
                }
                var l = v as List<object>;
                if (l != null)
                {
                    if (ps["minItems"] is double && l.Count < (double)ps["minItems"]) { error = k + ": expected at least " + ps["minItems"] + " items"; return false; }
                    if (ps["maxItems"] is double && l.Count > (double)ps["maxItems"]) { error = k + ": expected at most " + ps["maxItems"] + " items"; return false; }
                }
                var str = v as string;
                if (str != null && ps["maxLength"] is double && str.Length > (double)ps["maxLength"]) { error = k + ": longer than " + ps["maxLength"] + " characters"; return false; }
            }
            foreach (var r in req)
            {
                var k = r as string;
                if (k != null && (!args.ContainsKey(k) || args[k] == null)) { error = k + ": required"; return false; }
            }
            foreach (var k in props.order)
            {
                var ps = props[k] as Json.Obj;
                if (ps != null && ps.ContainsKey("default") && !args.ContainsKey(k)) { var dv = ps["default"]; args[k] = dv is List<object> ? new List<object>((List<object>)dv) : dv; }
            }
            return true;
        }

        static string[] ToStrings(List<object> l)
        {
            var r = new string[l.Count];
            for (int i = 0; i < l.Count; i++) r[i] = Convert.ToString(l[i], IC);
            return r;
        }

        internal static double Num(Json.Obj a, string k, double def)
        {
            var v = a != null ? a[k] : null;
            if (v == null) return def;
            if (v is double) return (double)v;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            throw new McpException(McpErr.BadArgs, k + ": expected a number, got " + Show(v), "see the tool's inputSchema");
        }

        internal static bool Bool(Json.Obj a, string k, bool def)
        {
            var v = a != null ? a[k] : null;
            if (v == null) return def;
            if (v is bool) return (bool)v;
            throw new McpException(McpErr.BadArgs, k + ": expected true or false, got " + Show(v), "see the tool's inputSchema");
        }

        internal static string Str(Json.Obj a, string k, string def)
        {
            var v = a != null ? a[k] : null;
            if (v == null) return def;
            var s = v as string;
            if (s != null) return s;
            throw new McpException(McpErr.BadArgs, k + ": expected a string, got " + Show(v), "see the tool's inputSchema");
        }

        internal static string Need(Json.Obj a, string k)
        {
            var s = Str(a, k, null);
            if (s == null || s.Trim().Length == 0) throw new McpException(McpErr.BadArgs, k + ": required", "see the tool's inputSchema");
            return s;
        }

        static string Show(object v)
        {
            if (v is string) return "string \"" + ((string)v).Replace("\"", "'") + "\"";
            if (v is bool) return "boolean";
            if (v is double) return "number " + ((double)v).ToString("R", IC);
            if (v is List<object>) return "array";
            if (v is Json.Obj) return "object";
            return v.GetType().Name;
        }

        internal static string Cut(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }

    // EditorState values cached on the main thread (EditorState is not thread-safe), read by worker threads
    internal static class HostSettings
    {
        static volatile string tools = "", token = "";
        static volatile int idleRestoreS = 600, restoreOnCloseS = 60;
        static volatile bool saveGuard;
        static string parsedFrom;
        static List<string> entries = new List<string>();
        static readonly object gate = new object();

        internal static string Tools { get { return tools; } set { tools = value ?? ""; } }
        internal static string Token { get { return token; } set { token = value ?? ""; } }
        internal static int IdleRestoreS { get { return idleRestoreS; } set { idleRestoreS = value; } }
        // mcp.restore_on_close_s: seconds after the last session's DELETE before the game is restored (0 = never on
        // DELETE; the idle restore still applies)
        internal static int RestoreOnCloseS { get { return restoreOnCloseS; } set { restoreOnCloseS = value; } }
        // play.save_guard (default off): on = the command tool also refuses the game loads that skip the save backup
        internal static bool SaveGuard { get { return saveGuard; } set { saveGuard = value; } }

        // mcp.tools: comma / space list of tool names, "<profile>:*", "<profile>:<tool>" or "*"; empty = everything
        internal static bool Allowed(string profile, string tool)
        {
            string raw = tools;
            if (raw.Trim().Length == 0) return true;
            List<string> l;
            lock (gate)
            {
                if (raw != parsedFrom)
                {
                    var n = new List<string>();
                    foreach (var part in raw.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)) n.Add(part.Trim().ToLowerInvariant());
                    entries = n; parsedFrom = raw;
                }
                l = entries;
            }
            string p = (profile ?? "").ToLowerInvariant(), t = (tool ?? "").ToLowerInvariant();
            foreach (var e in l)
            {
                if (e == "*" || e == t) return true;
                int c = e.IndexOf(':');
                if (c <= 0) continue;
                string ep = e.Substring(0, c), et = e.Substring(c + 1);
                if (ep == p && (et == "*" || et == t)) return true;
            }
            return false;
        }
    }
}
