using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace InsideDev.McpExt
{
    // DevToolsCalls.cs (owner HOST): game-side handlers of the 13 dev tools and screenshot (worker thread; every game
    // access goes through ctx.Main). Bridge commands run through Remote.RunCommand with the DevLog lines they wrote.
    // Fixes over v0.5: structured {text, truncated, total_chars, offset, next_offset}; inspect pages with offset, command
    // and the others cap at max_chars / 20000; isError from the reply's first line (HostText.Classify); fsm_graph
    // reads the graph without touching selection or panels unless open_panel; find_objects and fsm_find honour limit
    // (fsm_find builds its index in chunks over frames instead of one long frame); set_active re-reads after 2 frames
    // with a verdict; observe ids (o12, h3, b1) resolve strictly through InsideDev.Play's Observer.Resolve; command
    // refuses OS input and server-killing lines.
    internal sealed class DevToolsCalls
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        readonly DevCore core;
        volatile bool panelByMcp;

        internal static readonly string[] Panels = { "spawns", "levels", "triggers", "hidden", "world", "audio", "inspector", "cheats", "console", "diag", "settings",
            "objects", "explorer", "graph", "events", "sounds", "atimeline", "wwiseapi", "history", "audiodb", "logic", "modified", "fsmgraph", "fsmfind" };

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
                        string spec = Target(ctx, HostText.Need(a, "object"));
                        bool deep = HostText.Bool(a, "deep", false);
                        return RunText(ctx, "inspect " + spec + (deep ? " deep" : ""), HostText.Int(a, "offset", 0), HostText.Int(a, "max_chars", HostText.DefaultCap), true);
                    }
                case "select": return RunText(ctx, "select " + Target(ctx, HostText.Need(a, "object")), 0, HostText.DefaultCap, false);
                case "set_active": return SetActive(a, ctx);
                case "fsm_graph": return FsmGraphTool(a, ctx);
                case "fsm_find": return FsmFindTool(a, ctx);
                case "fsm":
                    {
                        string spec = Target(ctx, HostText.Need(a, "object"));
                        string act = HostText.Need(a, "action").ToLowerInvariant();
                        if (act != "event" && act != "state" && act != "var") return ToolResult.Fail(McpErr.BadArgs, "action must be event, state or var");
                        string fs = HostText.Str(a, "fsm", "");
                        string val = HostText.Str(a, "value", null);
                        return RunText(ctx, "fsm " + Q(spec) + (fs.Length > 0 ? " " + Q(fs) : "") + " " + act + " " + Q(HostText.Need(a, "name")) + (val != null ? " " + Q(val) : ""), 0, HostText.DefaultCap, false);
                    }
                case "signal": return RunText(ctx, "signal " + Q(Target(ctx, HostText.Need(a, "object"))) + " " + Q(HostText.Need(a, "output")), 0, HostText.DefaultCap, false);
                case "show_panel": return ShowPanel(a, ctx);
                case "log": return LogTool(a, ctx);
                case "command":
                    {
                        string c = HostText.Need(a, "command").Trim();
                        string why = HostText.Refusal(c);
                        if (why != null) return ToolResult.Fail(McpErr.Refused, why, "use the typed tools (act, time, menu) or the in-game console");
                        return RunText(ctx, c, 0, HostText.Int(a, "max_chars", HostText.DefaultCap), false);
                    }
            }
            return ToolResult.Fail(McpErr.NotFound, "unknown dev tool " + name);
        }

        static string Q(string s) { s = (s ?? "").Trim(); return s.IndexOf(' ') >= 0 && !s.StartsWith("\"") ? "\"" + s.Replace("\"", "") + "\"" : s; }

        // ---------------------------------------------------------------- bridge commands
        sealed class RunOut { public string res, log; }

        // main thread: the command's reply plus the DevLog lines it wrote; a line that drives a secret / achievement
        // object (HostText.SecretTypes, on the object, or for signal on a receiver up to 3 wires away) is refused
        RunOut Exec(string line)
        {
            string secret = SecretRefusal(line);
            if (secret != null) return new RunOut { res = "refused: " + secret + "; " + HostText.SecretHint, log = "" };
            var ls = DevLog.Lines;
            string last = null;
            try { if (ls.Count > 0) last = ls[ls.Count - 1]; } catch { }
            string res = Remote.RunCommand(line, Core);
            var sb = new StringBuilder();
            try
            {
                int start = 0;
                if (last != null) for (int i = ls.Count - 1; i >= 0; i--) if (ReferenceEquals(ls[i], last)) { start = i + 1; break; }
                for (int i = start; i < ls.Count; i++) sb.Append(ls[i]).Append('\n');
            }
            catch (Exception e) { sb.Append("(log lines not read: ").Append(e.Message).Append(")\n"); }
            return new RunOut { res = res, log = sb.ToString() };
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

        static ToolResult Build(RunOut o, int offset, int max, bool pageable)
        {
            string text = Join(o);
            string code = HostText.Classify(o.res);
            string msg = code != null ? HostText.FirstLine(o.res) : null;
            if (code == null && o.res == null && o.log.IndexOf("unknown command", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                code = McpErr.BadArgs;
                foreach (var l in o.log.Split('\n')) if (l.IndexOf("unknown command", StringComparison.OrdinalIgnoreCase) >= 0) { msg = l.Trim(); break; }
            }
            return HostText.Result(HostText.Slice(text, offset, max), code, msg, pageable);
        }

        ToolResult RunText(CallContext ctx, string line, int offset, int max, bool pageable)
        {
            var o = ctx.Main(() => Exec(line), 30000);
            return Build(o, offset, max, pageable);
        }

        // ---------------------------------------------------------------- ids from observe
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

        // "o12" -> "#<instanceID>" through Observer.Resolve (stale_id / not_found pass through); anything else unchanged
        string Target(CallContext ctx, string spec)
        {
            spec = (spec ?? "").Trim();
            if (!IsId(spec)) return spec;
            var m = ObserverResolve();
            if (m == null) return spec;
            return ctx.Main(() =>
            {
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
            });
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
            string spec = Target(ctx, HostText.Need(a, "object"));
            bool want = HostText.Bool(a, "active", true);
            GameObject go = null;
            bool before = false, now = false;
            var o = ctx.Main(() =>
            {
                go = Remote.Resolve(spec);
                if (go == null) return new RunOut { res = "not found", log = "" };
                before = go.activeSelf;
                var r = Exec("setactive #" + go.GetInstanceID().ToString(IC) + (want ? " on" : " off"));
                now = go != null && go.activeSelf;
                return r;
            }, 30000);
            if (go == null || HostText.Classify(o.res) != null) return Build(o, 0, HostText.DefaultCap, false);
            ctx.WaitFrames(2, 3000);
            bool gone = false, later = now, inHier = false;
            ctx.MainDo(() =>
            {
                if (go == null) { gone = true; return; }
                later = go.activeSelf; inHier = go.activeInHierarchy;
            });
            string verdict = gone ? "unreadable" : later == want ? "confirmed" : now == want ? "reverted_by_game" : "not_applied";
            var o2 = new RunOut { res = o.res, log = o.log + "after 2 frames: activeSelf=" + later + " inHierarchy=" + inHier + " (" + verdict + ")\n" };
            var res = Build(o2, 0, HostText.DefaultCap, false);
            var s = res.structured;
            s["verdict"] = verdict;
            s["before"] = before; s["after"] = now;
            if (!gone) { s["after_k"] = later; s["active_in_hierarchy"] = inHier; }
            return res;
        }

        ToolResult FsmGraphTool(Json.Obj a, CallContext ctx)
        {
            string spec = Target(ctx, HostText.Need(a, "object"));
            string fsmName = HostText.Str(a, "fsm", "").Trim();
            bool open = HostText.Bool(a, "open_panel", false);
            string text = ctx.Main(() =>
            {
                var go = Remote.Resolve(spec);
                if (go == null) return "not found";
                var fsms = go.GetComponents<PlayMakerFSM>();
                if (fsms == null || fsms.Length == 0) return "no FSM on " + go.name;
                PlayMakerFSM pick = null;
                if (fsmName.Length > 0)
                {
                    foreach (var f in fsms) if (f != null && f.FsmName == fsmName) { pick = f; break; }
                    if (pick == null) foreach (var f in fsms) if (f != null && string.Equals(f.FsmName, fsmName, StringComparison.OrdinalIgnoreCase)) { pick = f; break; }
                    if (pick == null) return "not found: no FSM named '" + fsmName + "' on " + go.name + "; FSMs: " + Names(fsms);
                }
                else pick = fsms[0];
                if (open) { Selection.Set(go, "mcp"); FsmGraph.Open(pick); }
                string dump = FsmGraph.Dump(pick);
                if (fsmName.Length == 0 && fsms.Length > 1) dump += "\n(" + fsms.Length + " FSMs on this object: " + Names(fsms) + "; pick one with fsm)";
                return dump;
            }, 30000);
            return HostText.Text(text, 0, HostText.DefaultCap, false);
        }

        static string Names(PlayMakerFSM[] fsms)
        {
            var l = new List<string>();
            foreach (var f in fsms) if (f != null) l.Add(f.FsmName);
            return string.Join(", ", l.ToArray());
        }

        // FsmFind internals (private in the core): index, building, Start, Pump, Search, query, lastQuery, lastCount, hits, head
        static bool fsmLooked, fsmOk;
        static FieldInfo fIndex, fBuilding, fQuery, fLastQuery, fLastCount, fHits, fHead;
        static MethodInfo mStart, mPump, mSearch;

        static bool FsmReflect()
        {
            if (fsmLooked) return fsmOk;
            fsmLooked = true;
            try
            {
                var t = typeof(FsmFind);
                const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                fIndex = t.GetField("index", S); fBuilding = t.GetField("building", S); fQuery = t.GetField("query", S);
                fLastQuery = t.GetField("lastQuery", S); fLastCount = t.GetField("lastCount", S); fHits = t.GetField("hits", S); fHead = t.GetField("head", S);
                mStart = t.GetMethod("Start", S, null, Type.EmptyTypes, null); mPump = t.GetMethod("Pump", S, null, Type.EmptyTypes, null); mSearch = t.GetMethod("Search", S, null, Type.EmptyTypes, null);
                fsmOk = fIndex != null && fBuilding != null && fQuery != null && fLastQuery != null && fLastCount != null && fHits != null && fHead != null &&
                        mStart != null && mPump != null && mSearch != null && fLastCount.FieldType == typeof(int);
            }
            catch { fsmOk = false; }
            if (!fsmOk) HostLog.Write("fsm_find: FsmFind internals not found; using FsmFind.Report (index built in one frame, at most 30 hits)");
            return fsmOk;
        }

        ToolResult FsmFindTool(Json.Obj a, CallContext ctx)
        {
            string q = HostText.Need(a, "text");
            int limit = Math.Max(1, HostText.Int(a, "limit", 30));
            bool refl = FsmReflect();
            if (refl)
            {
                for (int i = 0; i < 5000; i++)
                {
                    string st = ctx.Main(() =>
                    {
                        if (!ObjectDatabase.Ready) return "notready";
                        var index = (IList)fIndex.GetValue(null);
                        if (index.Count == 0 && fBuilding.GetValue(null) == null) mStart.Invoke(null, null);
                        if (fBuilding.GetValue(null) == null) return "done";
                        mPump.Invoke(null, null);
                        return fBuilding.GetValue(null) == null ? "done" : "more";
                    }, 10000);
                    if (st != "more") break;
                    if (ctx.RemainingMs < 8000 || ctx.Cancelled) break;
                }
            }
            string text = ctx.Main(() =>
            {
                if (!refl || limit <= 30) return CutRows(FsmFind.Report(q), limit);
                if (!ObjectDatabase.Ready) return "object scan not finished yet";
                var index = (IList)fIndex.GetValue(null);
                fQuery.SetValue(null, q); fLastQuery.SetValue(null, q); fLastCount.SetValue(null, index.Count);
                mSearch.Invoke(null, null);
                var hits = (IList)fHits.GetValue(null);
                var sb = new StringBuilder();
                sb.Append((string)fHead.GetValue(null)).Append('\n');
                for (int i = 0; i < hits.Count && i < limit; i++)
                {
                    var h = hits[i];
                    var ht = h.GetType();
                    var fs = ht.GetField("score"); var fl = ht.GetField("label");
                    sb.Append("  ").Append(fs != null ? Convert.ToString(fs.GetValue(h), IC) : "?").Append("  ").Append(fl != null ? (string)fl.GetValue(h) : h.ToString()).Append('\n');
                }
                return sb.ToString();
            }, 30000);
            if (!refl && limit > 30) text += "(this InsideDev build reports at most 30 hits)\n";
            return HostText.Text(text, 0, HostText.DefaultCap, false);
        }

        static string CutRows(string report, int limit)
        {
            if (report == null) return "";
            var lines = report.Split('\n');
            if (lines.Length <= limit + 1) return report;
            var sb = new StringBuilder();
            for (int i = 0; i <= limit && i < lines.Length; i++) sb.Append(lines[i]).Append('\n');
            return sb.ToString();
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

        ToolResult LogTool(Json.Obj a, CallContext ctx)
        {
            int n = Math.Max(1, Math.Min(DevLog.MaxLines, HostText.Int(a, "lines", 60)));
            string text = ctx.Main(() =>
            {
                string[] copy = null;
                for (int t = 0; t < 3 && copy == null; t++) { try { copy = DevLog.Lines.ToArray(); } catch { copy = null; } }
                if (copy == null) return "(log busy, try again)";
                int from = Math.Max(0, copy.Length - n);
                var part = new string[copy.Length - from];
                Array.Copy(copy, from, part, 0, part.Length);
                return string.Join("\n", part);
            });
            return HostText.Result(HostText.Tail(text, HostText.DefaultCap), null, null, false);
        }

        ToolResult Screenshot(Json.Obj a, CallContext ctx)
        {
            var req = new ShotRequest
            {
                mode = HostText.Str(a, "mode", "preview"),
                maxWidth = Math.Max(0, HostText.Int(a, "max_width", 960)),
                clean = HostText.Bool(a, "clean", true)
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
            if (r.png != null) res.AddImage(r.png, "image/png");
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
