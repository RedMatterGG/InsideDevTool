using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace InsideDev.McpExt
{
    // DevToolsCalls.cs (owner HOST): game-side handlers of the 12 dev text tools and screenshot (worker thread; every
    // game access goes through ctx.Main). Bridge commands run through Remote.RunCommand with the DevLog lines they wrote.
    // Output is structured {text, truncated, total_chars, offset, next_offset}; inspect pages with offset (later pages
    // come from the text kept for 30 s, see Pages); command and the others cap at max_chars / 20000. isError comes from
    // the first line of the command's reply, or of what it logged when it returned nothing (HostText.Classify).
    // A call takes one main-thread item: an observe id (o12, h3, b1) is resolved through InsideDev.Play's
    // Observer.Resolve in the same item that runs the command. set_active re-reads after 2 frames with a verdict.
    // command refuses OS input and lines that change the server itself; it also runs several commands in one call
    // (commands) and can wait for a status command to report the end of long work (wait / until).
    internal sealed class DevToolsCalls
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        readonly DevCore core;
        volatile bool panelByMcp;

        // main thread: above 0 while a line that came through the command tool or POST /cmd runs. Commands that change
        // who may use the server (mcp token, mcp tools, mcp hosts, mcp off, mcp port) refuse themselves while it is set,
        // also when another command runs them on the caller's behalf.
        internal static int InMcp;

        internal static readonly string[] Panels = { "spawns", "levels", "triggers", "hidden", "world", "audio", "inspector", "cheats", "console", "diag", "settings",
            "objects", "explorer", "graph", "events", "sounds", "atimeline", "wwiseapi", "history", "audiodb", "logic", "modified", "fsmgraph", "fsmfind" };

        const double BatchSliceMs = 4.0;

        internal DevToolsCalls(DevCore core) { this.core = core; }

        DevCore Core { get { return core ?? DevCore.Instance; } }

        internal ToolResult Call(string name, Json.Obj a, CallContext ctx)
        {
            switch (name)
            {
                case "game_state": return RunText(ctx, "state", 0, HostText.DefaultCap, false);
                case "screenshot": return Screenshot(a, ctx);
                case "find_objects": return FindObjects(a, ctx);
                case "inspect":
                    {
                        string obj = HostText.Need(a, "object");
                        bool deep = HostText.Bool(a, "deep", false);
                        return Inspect(ctx, obj, deep, HostText.Int(a, "offset", 0), HostText.Int(a, "max_chars", HostText.DefaultCap));
                    }
                case "select": return RunOn(ctx, HostText.Need(a, "object"), false, false, spec => "select " + spec, HostText.DefaultCap);
                case "set_active": return SetActive(a, ctx);
                case "fsm_graph": return FsmGraphTool(a, ctx);
                case "fsm_find": return FsmFindTool(a, ctx);
                case "fsm":
                    {
                        string obj = HostText.Need(a, "object");
                        string act = HostText.Need(a, "action").ToLowerInvariant();
                        if (act != "event" && act != "state" && act != "var") return ToolResult.Fail(McpErr.BadArgs, "action must be event, state or var");
                        string fs = HostText.Str(a, "fsm", "");
                        string val = HostText.Str(a, "value", null);
                        string nm = HostText.Need(a, "name");
                        return RunOn(ctx, obj, true, HostText.Bool(a, "strict", false),
                            spec => "fsm " + Q(spec) + (fs.Length > 0 ? " " + Q(fs) : "") + " " + act + " " + Q(nm) + (val != null ? " " + Q(val) : ""), HostText.DefaultCap);
                    }
                case "signal":
                    {
                        string obj = HostText.Need(a, "object");
                        string output = HostText.Need(a, "output");
                        return RunOn(ctx, obj, true, HostText.Bool(a, "strict", false), spec => "signal " + Q(spec) + " " + Q(output), HostText.DefaultCap);
                    }
                case "show_panel": return ShowPanel(a, ctx);
                case "log": return LogTool(a);
                case "command": return Command(a, ctx);
            }
            return ToolResult.Fail(McpErr.NotFound, "unknown dev tool " + name);
        }

        static string Q(string s) { s = (s ?? "").Trim(); return s.IndexOf(' ') >= 0 && !s.StartsWith("\"") ? "\"" + s.Replace("\"", "") + "\"" : s; }

        // ---------------------------------------------------------------- bridge commands
        sealed class RunOut
        {
            public string res, log, line;
            public Names names;
        }

        // main thread: the command's reply plus the DevLog lines it wrote; a line that drives a secret / achievement
        // object (HostText.SecretTypes, on the object, or for signal on a receiver up to 3 wires away) is refused
        RunOut Exec(string line)
        {
            int before = DevLog.Total;
            string res;
            InMcp++;
            try { res = Remote.RunCommand(line, Core); }
            finally { InMcp--; }
            var sb = new StringBuilder();
            try
            {
                var lines = new List<string>();
                DevLog.Since(before, lines);
                foreach (var l in lines) sb.Append(l).Append('\n');
            }
            catch (Exception e) { sb.Append("(log lines not read: ").Append(e.Message).Append(")\n"); }
            return new RunOut { res = res, log = sb.ToString(), line = line };
        }

        // Remote.ObjectRefusal: every untrusted Remote entry (this tool, POST /cmd, scenario steps, det cues, spec lines)
        internal static string ObjectRefusal(string line)
        {
            string secret = SecretRefusal(line);
            return secret == null ? null : secret + "; " + HostText.SecretHint;
        }

        static string SecretRefusal(string line)
        {
            var a = HostText.Words(line ?? "");
            if (a.Count < 2) return null;
            string verb = a[0].ToLowerInvariant();
            int at;
            bool follow, byComponent;
            if (!HostText.DrivesObject(verb, out at, out follow, out byComponent)) return null;
            string spec;
            if (at < 0)
            {
                string rest = line.Trim();
                rest = rest.Length > a[0].Length ? rest.Substring(a[0].Length) : "";
                spec = rest.Split('|')[0].Trim().Trim('"');
            }
            else spec = a[at];
            GameObject go = null;
            try { go = Remote.Resolve(spec); } catch { }
            if (go == null) return null;
            string hit = SecretOn(go, byComponent && a.Count > 2 ? a[2] : null);
            if (hit == null && follow) hit = SecretDownstream(go, 3);
            return hit == null ? null : verb + " reaches " + hit + ", which calls User.Achievement.Acquire (Steam) or clears the orb progress";
        }

        static string SecretOn(GameObject go, string component)
        {
            if (go == null) return null;
            foreach (var mb in go.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                for (var t = mb.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    if (!HostText.SecretType(t.Name)) continue;
                    if (component != null && !mb.GetType().Name.EndsWith(component, StringComparison.OrdinalIgnoreCase) && !t.Name.Equals(component, StringComparison.OrdinalIgnoreCase)) continue;
                    return go.name + " (" + mb.GetType().Name + ")";
                }
            }
            return null;
        }

        // receivers of the object's signal wires, breadth first
        static string SecretDownstream(GameObject go, int depth)
        {
            SignalManager sm = null;
            try { sm = PersistentBehaviour<SignalManager>.instance; } catch { }
            if (sm == null || sm.connections == null) return null;
            var seen = new HashSet<int> { go.GetInstanceID() };
            var front = new List<GameObject> { go };
            for (int d = 0; d < depth && front.Count > 0 && seen.Count < 300; d++)
            {
                var next = new List<GameObject>();
                foreach (var cn in sm.connections)
                {
                    if (cn == null || !cn.isActive || cn.signalInGameObject == null || cn.signalOutGameObject == null) continue;
                    if (!front.Contains(cn.signalOutGameObject)) continue;
                    var to = cn.signalInGameObject;
                    if (!seen.Add(to.GetInstanceID())) continue;
                    string hit = SecretOn(to, null);
                    if (hit != null) return hit;
                    next.Add(to);
                }
                front = next;
            }
            return null;
        }

        static string Join(RunOut o)
        {
            string res = o.res ?? "";
            string text = res + (o.log.Length > 0 ? (res.Length > 0 ? "\n--- log ---\n" : "") + o.log : "");
            return text.Trim().Length == 0 ? "done (no output)" : text;
        }

        // error code of a finished command: from its reply, or from the first line it logged when it returned nothing
        // (console commands answer through the log only)
        static string Code(RunOut o, out string message)
        {
            string basis = string.IsNullOrEmpty(o.res) ? HostText.FirstLine(o.log) : o.res;
            string code = HostText.Classify(basis);
            message = code != null ? HostText.FirstLine(basis) : null;
            if (code == null && string.IsNullOrEmpty(o.res) && o.log.IndexOf("unknown command", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                code = McpErr.BadArgs;
                foreach (var l in o.log.Split('\n')) if (l.IndexOf("unknown command", StringComparison.OrdinalIgnoreCase) >= 0) { message = l.Trim(); break; }
            }
            return code;
        }

        static ToolResult Build(RunOut o, int offset, int max, bool pageable)
        {
            string msg;
            string code = Code(o, out msg);
            var r = HostText.Result(HostText.Slice(Join(o), offset, max), code, msg, pageable);
            if (o.names != null) o.names.Describe(r.structured);
            return r;
        }

        // the command tool's own fields: command = the line that ran, from = where text comes from (reply = what the
        // command returned, log = what it wrote to the log, both, none); reply and log are given apart only when both
        // exist, so an answer can be told from log lines without sending everything twice
        static void Parts(Json.Obj s, RunOut o, int max)
        {
            bool hasRes = !string.IsNullOrEmpty(o.res), hasLog = o.log.Length > 0;
            s["command"] = o.line ?? "";
            s["from"] = hasRes && hasLog ? "both" : hasRes ? "reply" : hasLog ? "log" : "none";
            if (hasRes && hasLog) { s["reply"] = HostText.Cut(o.res, max); s["log"] = HostText.Cut(o.log, max); }
        }

        ToolResult RunText(CallContext ctx, string line, int offset, int max, bool pageable)
        {
            var o = ctx.Main(() => Exec(line), 30000);
            FollowWaitLine(ctx, o);
            return Build(o, offset, max, pageable);
        }

        // console commands run on the main thread and cannot wait; a reply whose last line is "wait: pano wait" (grec, rec
        // the same) asks the caller to poll that command until it stops answering "pending:". The tool's worker thread
        // does that here, within the call's time, and puts the final status in place of the wait line.
        void FollowWaitLine(CallContext ctx, RunOut o)
        {
            int at = o.res != null ? o.res.LastIndexOf("\nwait: ", StringComparison.Ordinal) : -1;
            if (at < 0) return;
            string poll = o.res.Substring(at + 7).Trim();
            if (poll != "pano wait" && poll != "grec wait" && poll != "rec wait") return;
            string head = o.res.Substring(0, at);
            while (ctx.RemainingMs > 3000 && ctx.Sleep(100))
            {
                var p = ctx.Main(() => Exec(poll), 10000);
                if (p.res != null && p.res.StartsWith("pending:", StringComparison.Ordinal)) continue;
                o.res = head + "\n" + p.res;
                o.log += p.log;
                return;
            }
        }

        // "ctl wait [jN] [<seconds>]" blocks until a ctl job ends, which the game's main thread must never do: it runs here,
        // on the tool's worker thread, with the seconds capped to the call's time. null when the line is not ctl wait or
        // the Ctl extension is not loaded (the line then runs as usual and says why)
        RunOut CtlWait(CallContext ctx, string line)
        {
            var w = HostText.Words(line);
            if (w.Count < 2 || !w[0].Equals("ctl", StringComparison.OrdinalIgnoreCase) || !w[1].Equals("wait", StringComparison.OrdinalIgnoreCase)) return null;
            var ext = ctx.Main(() =>
            {
                foreach (var x in Extensions.loaded)
                    if (x != null && x.GetType().FullName == "InsideDev.Play.CtlExtension") return x;
                return null;
            }, 5000);
            if (ext == null) return null;
            double budget = Math.Max(1.0, Math.Floor((ctx.RemainingMs - 3000) / 1000.0));
            int i = 2;
            double s;
            if (i < w.Count && !double.TryParse(w[i], NumberStyles.Float, IC, out s)) i++;
            if (i < w.Count && double.TryParse(w[i], NumberStyles.Float, IC, out s)) { if (s > budget) w[i] = budget.ToString("0", IC); }
            else if (i == w.Count) w.Add(Math.Min(budget, 30.0).ToString("0", IC));
            string res;
            try { res = ext.Command(w, null); }
            catch (Exception e) { res = "failed: " + e.GetType().Name + ": " + e.Message; }
            return new RunOut { res = res, log = "", line = line };
        }

        // one main-thread item: resolve the object word, build the command line with it, run it
        ToolResult RunOn(CallContext ctx, string obj, bool countNames, bool strict, Func<string, string> lineFor, int max)
        {
            var o = ctx.Main(() =>
            {
                Names names;
                string spec = Target(obj, countNames, strict, out names);
                var r = Exec(lineFor(spec));
                r.names = names;
                return r;
            }, 30000);
            return Build(o, 0, max, false);
        }

        // ---------------------------------------------------------------- pages of long text
        // inspect page 2..N: the full text of the first page is kept per MCP session for 30 s, so a later page is a slice
        // of the same dump (no second reflection walk on the main thread, and the pages fit together). page_of = the
        // frame the text was made in.
        sealed class PageText { public string line, text, code, message; public long frame; public DateTime at; }
        static readonly Dictionary<string, PageText> pages = new Dictionary<string, PageText>();
        const int PageKeepS = 30, PageSessions = 32;

        static PageText CachedPage(string session, string line)
        {
            lock (pages)
            {
                PageText p;
                if (!pages.TryGetValue(session, out p)) return null;
                if (p.line != line || (DateTime.UtcNow - p.at).TotalSeconds > PageKeepS) return null;
                return p;
            }
        }

        static void KeepPage(string session, PageText p)
        {
            lock (pages)
            {
                var now = DateTime.UtcNow;
                var old = new List<string>();
                foreach (var kv in pages) if ((now - kv.Value.at).TotalSeconds > PageKeepS) old.Add(kv.Key);
                foreach (var k in old) pages.Remove(k);
                if (pages.Count >= PageSessions && !pages.ContainsKey(session)) return;
                pages[session] = p;
            }
        }

        ToolResult Inspect(CallContext ctx, string obj, bool deep, int offset, int max)
        {
            string session = ctx.Info.sessionId ?? "";
            string key = obj.Trim() + (deep ? "\ndeep" : "\n");
            PageText page = offset > 0 ? CachedPage(session, key) : null;
            if (page == null)
            {
                var o = ctx.Main(() =>
                {
                    Names names;
                    string spec = Target(obj, false, false, out names);
                    return Exec("inspect " + spec + (deep ? " deep" : ""));
                }, 30000);
                string msg;
                string code = Code(o, out msg);
                page = new PageText { line = key, text = Join(o), code = code, message = msg, frame = ctx.Frame, at = DateTime.UtcNow };
                if (page.text.Length > max) KeepPage(session, page);
            }
            var r = HostText.Result(HostText.Slice(page.text, offset, max), page.code, page.message, true);
            r.structured["page_of"] = (double)page.frame;
            return r;
        }

        // ---------------------------------------------------------------- the object word
        static MethodInfo observerResolve;
        static DateTime observerLooked = DateTime.MinValue;

        static bool IsId(string s)
        {
            if (s.Length < 2 || (s[0] != 'o' && s[0] != 'h' && s[0] != 'b')) return false;
            for (int i = 1; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        static MethodInfo ObserverResolve()
        {
            if (observerResolve != null) return observerResolve;
            if ((DateTime.UtcNow - observerLooked).TotalSeconds < 5) return null;
            observerLooked = DateTime.UtcNow;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n;
                try { n = asm.GetName().Name; } catch { continue; }
                if (n != "InsideDev.Play") continue;
                var t = asm.GetType("InsideDev.Play.Observer");
                if (t == null) continue;
                observerResolve = t.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            }
            return observerResolve;
        }

        // what a plain name matched: the bridge takes the object with that name (else with that text in its name) that
        // is nearest to the boy, whatever else carries the name
        sealed class Names
        {
            public string query;
            public int matches;
            public bool exact;
            public readonly List<string> candidates = new List<string>();

            public void Describe(Json.Obj s)
            {
                if (matches < 2) return;
                s["matches"] = (double)matches;
                var l = new List<object>();
                foreach (var c in candidates) l.Add(c);
                s["candidates"] = l;
                var w = s["warnings"] as List<object>;
                if (w == null) { w = new List<object>(); s["warnings"] = w; }
                w.Add(matches.ToString(IC) + " objects " + (exact ? "are named" : "have") + " '" + query + "'" + (exact ? "" : " in their name") +
                      "; this call used the one nearest to the boy (candidates: nearest first). Pass a hierarchy path or #instanceID, or strict:true to refuse such a name.");
            }
        }

        const int MaxCandidates = 10;

        // main thread. "o12" -> "#<instanceID>" through Observer.Resolve (stale_id / not_found pass through); a path or
        // #id stays as it is; a plain name stays too, and with countNames its matches are counted (strict: more than one
        // is an error instead of "the nearest")
        static string Target(string spec, bool countNames, bool strict, out Names names)
        {
            names = null;
            spec = (spec ?? "").Trim();
            if (IsId(spec))
            {
                var m = ObserverResolve();
                if (m == null) return spec;
                object o;
                try { o = m.Invoke(null, new object[] { spec }); }
                catch (TargetInvocationException e)
                {
                    var inner = e.InnerException;
                    if (inner is NotImplementedException) return spec;
                    var me = inner as McpException;
                    if (me != null) throw me;
                    throw HostCallContext.GameError(inner ?? e);
                }
                var go = o as GameObject;
                if (go == null) { var c = o as Component; if (c != null) go = c.gameObject; }
                if (go == null) throw new McpException(McpErr.NotFound, spec + " is not a scene object", "use a hierarchy path or #instanceID");
                return "#" + go.GetInstanceID().ToString(IC);
            }
            if (!countNames) return spec;
            string q = spec.Trim('"');
            if (q.Length == 0 || q[0] == '#' || q.IndexOf('/') >= 0) return spec;
            names = Count(q);
            if (strict && names.matches > 1)
            {
                var ex = new McpException(McpErr.Ambiguous, names.matches.ToString(IC) + " objects " + (names.exact ? "are named" : "have") + " '" + q + "'" + (names.exact ? "" : " in their name") + "; nothing was done",
                    "pass a hierarchy path or #instanceID (candidates lists the nearest ones)");
                var d = new Json.Obj();
                d["matches"] = (double)names.matches;
                var l = new List<object>();
                foreach (var c in names.candidates) l.Add(c);
                d["candidates"] = l;
                ex.data = d;
                throw ex;
            }
            return spec;
        }

        // the name rule of Remote.Resolve: objects named exactly q, else objects with q inside their name; the
        // candidates are the nearest ones to the boy, nearest first, as "<path> #<instanceID>"
        static Names Count(string q)
        {
            var n = new Names { query = q };
            var all = Inspector.AllSceneTransforms();
            var ch = G.MainCharacter;
            var origin = ch != null ? ch.pos3 : Vector3.zero;
            var hits = new List<Transform>();
            foreach (var t in all) if (t != null && t.name == q) hits.Add(t);
            n.exact = hits.Count > 0;
            if (hits.Count == 0) foreach (var t in all) if (t != null && t.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(t);
            n.matches = hits.Count;
            if (hits.Count < 2) return n;
            var dist = new float[hits.Count];
            var arr = hits.ToArray();
            for (int i = 0; i < arr.Length; i++) dist[i] = (arr[i].position - origin).sqrMagnitude;
            Array.Sort(dist, arr);
            for (int i = 0; i < arr.Length && i < MaxCandidates; i++)
            {
                string path;
                try { path = Inspector.PathOf(arr[i]); } catch (Exception) { path = arr[i].name; }
                n.candidates.Add(path + " #" + arr[i].gameObject.GetInstanceID().ToString(IC));
            }
            return n;
        }

        // ---------------------------------------------------------------- tools
        ToolResult FindObjects(Json.Obj a, CallContext ctx)
        {
            string q = HostText.Need(a, "query");
            string cat = HostText.Str(a, "category", "");
            int limit = Math.Max(1, HostText.Int(a, "limit", 25));
            int count = 0;
            string text = ctx.Main(() =>
            {
                var c = SmartFind.Cat.All;
                if (cat.Length > 0)
                {
                    int ci = Array.IndexOf(SmartFind.CatNames, cat == "fsm" ? "state machines" : cat);
                    if (ci >= 0) c = (SmartFind.Cat)ci;
                }
                int hc, hp;
                var hs = SmartFind.Run(q, c, false, false, out hc, out hp);
                count = hs.Count;
                var sb = new StringBuilder();
                if (!ObjectDatabase.Ready) sb.Append("object scan not finished yet; results may be incomplete\n");
                sb.Append(hs.Count).Append(" result(s); clutter hidden ").Append(hc).Append(", path-only hidden ").Append(hp);
                if (SmartFind.Truncated > 0) sb.Append(", ").Append(SmartFind.Truncated).Append(" over the cap");
                if (hs.Count > limit) sb.Append("; showing ").Append(limit);
                sb.Append('\n');
                for (int i = 0; i < hs.Count && i < limit; i++) sb.Append("  ").Append(hs[i].score).Append("  ").Append(SmartFind.Describe(hs[i])).Append('\n');
                return sb.ToString();
            }, 30000);
            var r = HostText.Result(HostText.Slice(text, 0, HostText.DefaultCap), null, null, false);
            r.structured["count"] = (double)count;
            return r;
        }

        ToolResult SetActive(Json.Obj a, CallContext ctx)
        {
            string obj = HostText.Need(a, "object");
            bool want = HostText.Bool(a, "active", true);
            bool strict = HostText.Bool(a, "strict", false);
            GameObject go = null;
            bool found = false, before = false, now = false;
            var o = ctx.Main(() =>
            {
                Names names;
                string spec = Target(obj, true, strict, out names);
                go = Remote.Resolve(spec);
                if (go == null) return new RunOut { res = "not found", log = "", names = names };
                found = true;
                before = go.activeSelf;
                var r = Exec("setactive #" + go.GetInstanceID().ToString(IC) + (want ? " on" : " off"));
                r.names = names;
                now = go != null && go.activeSelf;
                return r;
            }, 30000);
            string msg;
            if (!found || Code(o, out msg) != null) return Build(o, 0, HostText.DefaultCap, false);
            ctx.WaitFrames(2, 3000);
            bool gone = false, later = now, inHier = false;
            ctx.MainDo(() =>
            {
                if (go == null) { gone = true; return; }
                later = go.activeSelf; inHier = go.activeInHierarchy;
            });
            string verdict = gone ? "unreadable" : later == want ? "confirmed" : now == want ? "reverted_by_game" : "not_applied";
            var o2 = new RunOut { res = o.res, log = o.log + "after 2 frames: activeSelf=" + later + " inHierarchy=" + inHier + " (" + verdict + ")\n", names = o.names };
            var res = Build(o2, 0, HostText.DefaultCap, false);
            var s = res.structured;
            s["verdict"] = verdict;
            s["before"] = before; s["after"] = now;
            if (!gone) { s["after_k"] = later; s["active_in_hierarchy"] = inHier; }
            return res;
        }

        ToolResult FsmGraphTool(Json.Obj a, CallContext ctx)
        {
            string obj = HostText.Need(a, "object");
            string fsmName = HostText.Str(a, "fsm", "").Trim();
            bool open = HostText.Bool(a, "open_panel", false);
            string text = ctx.Main(() =>
            {
                Names names;
                var go = Remote.Resolve(Target(obj, false, false, out names));
                if (go == null) return "not found";
                var fsms = go.GetComponents<PlayMakerFSM>();
                if (fsms == null || fsms.Length == 0) return "no FSM on " + go.name;
                PlayMakerFSM pick = null;
                if (fsmName.Length > 0)
                {
                    foreach (var f in fsms) if (f != null && f.FsmName == fsmName) { pick = f; break; }
                    if (pick == null) foreach (var f in fsms) if (f != null && string.Equals(f.FsmName, fsmName, StringComparison.OrdinalIgnoreCase)) { pick = f; break; }
                    if (pick == null) return "not found: no FSM named '" + fsmName + "' on " + go.name + "; FSMs: " + FsmNames(fsms);
                }
                else pick = fsms[0];
                if (open) { Selection.Set(go, "mcp"); FsmGraph.Open(pick); }
                string dump = FsmGraph.Dump(pick);
                if (fsmName.Length == 0 && fsms.Length > 1) dump += "\n(" + fsms.Length + " FSMs on this object: " + FsmNames(fsms) + "; pick one with fsm)";
                return dump;
            }, 30000);
            return HostText.Text(text, 0, HostText.DefaultCap, false);
        }

        static string FsmNames(PlayMakerFSM[] fsms)
        {
            var l = new List<string>();
            foreach (var f in fsms) if (f != null) l.Add(f.FsmName);
            return string.Join(", ", l.ToArray());
        }

        // the core's FsmFind index, pumped one slice per main-thread item until it is complete, then queried without
        // touching the panel's Find box
        ToolResult FsmFindTool(Json.Obj a, CallContext ctx)
        {
            string q = HostText.Need(a, "text");
            int limit = Math.Max(1, HostText.Int(a, "limit", 30));
            for (int i = 0; i < 5000; i++)
            {
                bool more = ctx.Main(() => FsmFind.PumpIndex(), 10000);
                if (!more || ctx.RemainingMs < 8000 || ctx.Cancelled) break;
            }
            string text = ctx.Main(() =>
            {
                if (!ObjectDatabase.Ready) return "object scan not finished yet";
                int total;
                string header;
                var rows = FsmFind.Query(q, limit, out total, out header);
                var sb = new StringBuilder();
                sb.Append(header).Append('\n');
                foreach (var r in rows) sb.Append("  ").Append(Fmt.I(r.score)).Append("  ").Append(r.label).Append('\n');
                if (FsmFind.Indexing) sb.Append("(the state machine index was still being built when the call ended: ask again for every match)\n");
                return sb.ToString();
            }, 30000);
            return HostText.Text(text, 0, HostText.DefaultCap, false);
        }

        ToolResult ShowPanel(Json.Obj a, CallContext ctx)
        {
            string pn = HostText.Need(a, "panel").Trim();
            string dock = HostText.Str(a, "dock", "").Trim().ToLowerInvariant();
            object size = a["size"];
            if (pn.ToLowerInvariant() == "none") { panelByMcp = false; return RunText(ctx, "panel off", 0, HostText.DefaultCap, false); }
            var o = ctx.Main(() =>
            {
                var c = Core;
                bool before = c != null && c.Panel;
                RunOut r;
                if (dock.Length > 0)
                {
                    Exec("ui layout dock");
                    r = Exec("ui put " + pn + " " + dock);
                    if (size is double && HostText.Classify(r.res) == null) r = Exec("ui dock " + dock + " " + ((double)size).ToString(IC));
                }
                else r = Exec("ui show " + pn);
                if (!before && c != null && c.Panel) panelByMcp = true;
                return r;
            }, 30000);
            if (Array.IndexOf(Panels, pn.ToLowerInvariant()) < 0) o.log += "warning: '" + pn + "' is not one of the known panel ids (" + string.Join(", ", Panels) + ")\n";
            return Build(o, 0, HostText.DefaultCap, false);
        }

        // the log is read under its own lock, so this tool does not wait for the game's main thread
        static ToolResult LogTool(Json.Obj a)
        {
            int n = Math.Max(1, Math.Min(DevLog.MaxLines, HostText.Int(a, "lines", 60)));
            string text = string.Join("\n", DevLog.Tail(n));
            return HostText.Result(HostText.Tail(text, HostText.DefaultCap), null, null, false);
        }

        // ---------------------------------------------------------------- command: one line, several lines, wait
        ToolResult Command(Json.Obj a, CallContext ctx)
        {
            int max = HostText.Int(a, "max_chars", HostText.DefaultCap);
            var many = a["commands"] as List<object>;
            string one = HostText.Str(a, "command", null);
            if (many != null && many.Count > 0)
            {
                if (one != null && one.Trim().Length > 0) return ToolResult.Fail(McpErr.BadArgs, "give command or commands, not both");
                if (HostText.Str(a, "wait", null) != null) return ToolResult.Fail(McpErr.BadArgs, "wait works with one command, not with commands");
                var lines = new List<string>();
                foreach (var x in many)
                {
                    var s = x as string;
                    if (s == null) return ToolResult.Fail(McpErr.BadArgs, "commands: expected an array of strings");
                    if (s.Trim().Length > 0) lines.Add(s.Trim());
                }
                if (lines.Count == 0) return ToolResult.Fail(McpErr.BadArgs, "commands: no command in the list");
                return Batch(ctx, lines, HostText.Bool(a, "stop_on_error", false), HostText.Bool(a, "dry_run", false), max);
            }
            if (one == null || one.Trim().Length == 0) return ToolResult.Fail(McpErr.BadArgs, "command: required (or commands: [...] for several)", "see the tool's inputSchema");
            string c = one.Trim();
            string why = HostText.Refusal(c);
            if (why != null) return ToolResult.Fail(McpErr.Refused, why, "use the typed tools (act, time, menu) or the in-game console");
            string wait = (HostText.Str(a, "wait", "") ?? "").Trim();
            if (wait.Length > 0)
            {
                string whyWait = HostText.Refusal(wait);
                if (whyWait != null) return ToolResult.Fail(McpErr.Refused, "wait: " + whyWait);
            }
            if (HostText.Bool(a, "dry_run", false))
            {
                var d = ToolResult.Ok(new Json.Obj());
                d.structured["text"] = "would run: " + c;
                d.structured["command"] = c;
                d.structured["dry_run"] = true;
                return d;
            }
            var o = wait.Length == 0 ? CtlWait(ctx, c) : null;
            if (o == null) o = ctx.Main(() => Exec(c), 30000);
            if (wait.Length == 0)
            {
                FollowWaitLine(ctx, o);
                var r = Build(o, 0, max, false);
                Parts(r.structured, o, max);
                return r;
            }
            return Wait(ctx, o, wait, HostText.Str(a, "until", HostText.DefaultUntil), HostText.Num(a, "wait_timeout_s", 45), max);
        }

        // after the command ran: ask the status command again and again until its reply has one of the until texts
        ToolResult Wait(CallContext ctx, RunOut first, string waitCmd, string until, double timeoutS, int max)
        {
            string msg;
            string code = Code(first, out msg);
            if (code != null)
            {
                var failed = Build(first, 0, max, false);
                Parts(failed.structured, first, max);
                return failed;
            }
            var alts = new List<string>();
            foreach (var part in (string.IsNullOrEmpty(until) ? HostText.DefaultUntil : until).Split('|')) if (part.Trim().Length > 0) alts.Add(part.Trim());
            int limitMs = (int)Math.Round(Math.Max(0, timeoutS) * 1000);
            limitMs = Math.Max(0, Math.Min(limitMs, ctx.RemainingMs - 4000));
            var sw = Stopwatch.StartNew();
            RunOut last = null;
            string hit = null;
            while (true)
            {
                if (!ctx.Sleep(last == null ? 60 : 200)) break;
                last = ctx.Main(() => Exec(waitCmd), 10000);
                string reply = Join(last);
                foreach (var alt in alts) if (reply.IndexOf(alt, StringComparison.OrdinalIgnoreCase) >= 0) { hit = alt; break; }
                if (hit != null || sw.ElapsedMilliseconds >= limitMs) break;
                ctx.Progress(Math.Round(sw.ElapsedMilliseconds / 1000.0, 1), Math.Round(limitMs / 1000.0, 1), "waiting for '" + waitCmd + "'");
            }
            bool done = hit != null;
            string waited = last != null ? Join(last) : "(not asked: cancelled)";
            string text = Join(first) + "\n--- " + waitCmd + " ---\n" + waited + (done ? "" : "\n(" + (ctx.Cancelled ? "cancelled" : "not finished after " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.#", IC) + " s") + "; ask '" + waitCmd + "' again)");
            string wcode = null, wmsg = null;
            if (done && last != null) wcode = Code(last, out wmsg);
            var r = HostText.Result(HostText.Slice(text, 0, max), wcode, wmsg, false);
            var s = r.structured;
            Parts(s, first, max);
            s["wait"] = waitCmd;
            s["wait_done"] = done;
            if (done) s["wait_hit"] = hit;
            s["waited_s"] = Math.Round(sw.ElapsedMilliseconds / 1000.0, 2);
            s["wait_reply"] = HostText.Cut(waited, max);
            return r;
        }

        // several commands in one call: as many as fit in about 4 ms run in one main-thread item, the rest in the next
        // frames. results = [{command, ok, code, text}]; the call itself is an error when a command failed.
        ToolResult Batch(CallContext ctx, List<string> lines, bool stopOnError, bool dryRun, int max)
        {
            var results = new List<object>();
            var text = new StringBuilder();
            int failed = 0;
            string firstCode = null, firstMsg = null;
            int each = Math.Max(1000, max / Math.Max(1, lines.Count));
            if (dryRun)
            {
                foreach (var line in lines)
                {
                    string why = HostText.Refusal(line);
                    var e = new Json.Obj();
                    e["command"] = line; e["ok"] = why == null; e["code"] = why == null ? null : McpErr.Refused; e["text"] = why == null ? "would run" : "refused: " + why;
                    results.Add(e);
                    if (why != null) failed++;
                    text.Append("> ").Append(line).Append('\n').Append(e.Str("text")).Append('\n');
                }
                var dr = HostText.Result(HostText.Slice(text.ToString(), 0, max), null, null, false);
                dr.structured["results"] = results;
                dr.structured["count"] = (double)lines.Count;
                dr.structured["failed"] = (double)failed;
                dr.structured["dry_run"] = true;
                return dr;
            }
            int i = 0;
            bool stopped = false;
            while (i < lines.Count && !stopped)
            {
                ctx.ThrowIfCancelled();
                int start = i;
                var part = ctx.Main(() =>
                {
                    var outs = new List<RunOut>();
                    long t0 = Stopwatch.GetTimestamp();
                    for (int k = start; k < lines.Count; k++)
                    {
                        string why = HostText.Refusal(lines[k]);
                        var o = why != null ? new RunOut { res = "refused: " + why, log = "", line = lines[k] } : Exec(lines[k]);
                        outs.Add(o);
                        string m;
                        if (stopOnError && Code(o, out m) != null) break;
                        if ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency >= BatchSliceMs) break;
                    }
                    return outs;
                }, 30000);
                foreach (var o in part)
                {
                    string msg;
                    string code = Code(o, out msg);
                    string body = Join(o);
                    var e = new Json.Obj();
                    e["command"] = o.line; e["ok"] = code == null; e["code"] = code; e["text"] = HostText.Cut(body, each);
                    results.Add(e);
                    text.Append("> ").Append(o.line).Append('\n').Append(HostText.Cut(body, each)).Append('\n');
                    if (code != null)
                    {
                        failed++;
                        if (firstCode == null) { firstCode = code; firstMsg = o.line + ": " + msg; }
                        if (stopOnError) stopped = true;
                    }
                }
                i += part.Count;
            }
            for (; i < lines.Count; i++)
            {
                var e = new Json.Obj();
                e["command"] = lines[i]; e["ok"] = false; e["code"] = "skipped"; e["text"] = "not run: an earlier command failed (stop_on_error)";
                results.Add(e);
            }
            var r = HostText.Result(HostText.Slice(text.ToString(), 0, max), firstCode, firstCode != null ? failed.ToString(IC) + " of " + lines.Count.ToString(IC) + " command(s) failed; first: " + firstMsg : null, false);
            r.structured["results"] = results;
            r.structured["count"] = (double)lines.Count;
            r.structured["failed"] = (double)failed;
            return r;
        }

        ToolResult Screenshot(Json.Obj a, CallContext ctx)
        {
            var req = new ShotRequest
            {
                mode = HostText.Str(a, "mode", "preview"),
                maxWidth = Math.Max(0, HostText.Int(a, "max_width", 960)),
                clean = HostText.Bool(a, "clean", true),
                fast = HostText.Bool(a, "fast", false),
                format = HostText.Str(a, "format", "png"),
                quality = HostText.Int(a, "quality", 80)
            };
            var r = McpShot.Capture(ctx, req);
            if (r.error != null)
            {
                var f = ToolResult.Fail(r.code ?? McpErr.GameError, r.error, r.code == McpErr.NotReady ? "focus or restore the game window; the game must be rendering" : null);
                var d = McpShot.Describe(r);
                foreach (var k in d.order) if (k != "problem") f.structured[k] = d[k];
                return f;
            }
            var res = ToolResult.Ok(McpShot.Describe(r));
            if (r.png != null) res.AddImage(r.png, r.Mime);
            if (r.mode == "full" && r.file != null) res.AddResourceLink(McpShot.UriPrefix + r.file, r.file, "image/png");
            return res;
        }

        // main thread (McpRegistry.RestoreAll)
        internal void Restore(List<string> report)
        {
            if (!panelByMcp) return;
            panelByMcp = false;
            var c = Core;
            if (c == null || !c.Panel) return;
            Remote.RunCommand("panel off", c);
            report.Add("closed the InsideDev editor opened by show_panel");
        }
    }
}
