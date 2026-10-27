using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // GuideScenario.cs (owner GUIDE): the dev tool run_scenario (report 05 item 14, debuff scenario JSON / DayZ
    // playbook_run): a scripted list of steps run in order under one control-lane job through the same contracts the play
    // tools use: load -> PlayLevels.StartLoad, press / hold / release -> AgentCore (queue, latch, release), wait / press
    // -> Stepper.Run, until -> Waits.Compile + Stepper.Run, expect -> Observer.Observe + a path comparison, capture ->
    // McpShot.Capture, command -> Remote.RunCommand (OS-input / server-stop / quit commands refused), observe ->
    // Observer.Concise, note -> Notes.Add. Parsing, validation, paths and comparisons: GuideScenarioCore.cs. Files:
    // <_mod>/scenarios/*.json and <guide root>/scenarios/*.json ({name, description, stop_on_fail, steps}). Input is always
    // released at the end. Registered from GuideBook.Register (PlayExtension calls only the 22 module Registers).
    public static class ScenarioRunner
    {
        public const string Owner = "GUIDE", Tool = "run_scenario";
        public static readonly string[] Outcomes = { "passed", "failed", "stopped", "cancelled", "timeout", "valid", "listed" };

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def(Tool, "Run a scenario", PlayShared.Dev, Owner,
                "Run a scripted test: steps in order through the agent input and the step clock. do = load (checkpoint), press / hold / release (stick, jump, action), wait (steps or seconds), until (one wait_until condition within within_s), expect (observe path cmp value), capture (screenshot), command (console), observe, note. Stops at the first failed step unless stop_on_fail is false. A run longer than the call budget ends with outcome timeout and next = the step to pass as start_at. op check only validates; op list shows scenario files. Loads need no confirm unless the user set EditorState play.save_guard = 1 (then loads that write the save need confirm:true in the step).",
                Schema.Obj().Enum("op", "run, check (validate only) or list (scenario files).", "run", "run", "check", "list")
                    .Str("file", "Scenario file name in _mod/scenarios or <guide>/scenarios (without .json), instead of steps.")
                    .Arr("steps", "Inline steps (instead of file).", Schema.ObjOf(StepSchema()), 1, ScenarioPlan.MaxSteps)
                    .Int("start_at", "First step index to run (continue after a timeout).", 0, 0, ScenarioPlan.MaxSteps - 1)
                    .Bool("stop_on_fail", "Stop at the first failed step (default true; when not given a file's own stop_on_fail applies).")
                    .Enum("observe", "Observation at the end.", "concise", "concise", "none").Build(),
                Schema.Obj().Enum("op", "", null, "run", "check", "list").Enum("outcome", "", null, Outcomes).Str("name", "Scenario name.")
                    .Int("total", "Steps in the scenario.", 0, ScenarioPlan.MaxSteps).Int("ran", "Steps run in this call.", 0, ScenarioPlan.MaxSteps)
                    .Int("next", "Index to continue with (start_at), -1 when finished.", -1, ScenarioPlan.MaxSteps).Int("failures", "", 0, ScenarioPlan.MaxSteps)
                    .Arr("results", "One row per step run.", Schema.OutOf(Schema.Obj().Int("i", "Step index.", 0, ScenarioPlan.MaxSteps).Str("do", "").Str("label", "").Bool("ok", "")
                        .Str("detail", "What happened.").Int("steps", "Logic steps run.", 0, 1000000).Str("stop", "Stepper stop reason.").Any("value", "observe / capture / command output.")), 0, ScenarioPlan.MaxSteps)
                    .Arr("scenarios", "op list.", Schema.OutOf(Schema.Obj().Str("file", "").Str("name", "").Str("description", "").Int("steps", "", 0, ScenarioPlan.MaxSteps).Str("error", "")), 0, 200)
                    .Int("game_steps", "Logic steps the remaining steps may take at most.", 0, int.MaxValue)
                    .Arr("warnings", "", Schema.Str(), 0, 40)
                    .Prop("changes", null, ChangeLog.Ref())
                    .Prop("observe", null, Observer.ConciseRef()).BuildOutput()).Destructive(), Handle);
            PlayRegistry.Instructions(McpProfiles.Dev, 60, "run_scenario replays a scripted step list (op check first; steps run under the control lane like act).");
        }

        public static SchemaObj StepSchema()
        {
            return Schema.Obj()
                .Enum("do", "Step kind.", null, ScenarioPlan.Kinds)
                .StrMax("label", "Name shown in results.", 200)
                .Vec("stick", "press / hold: stick [x, y], each -1..1 (0 or magnitude 0.3..1; above 0.8 runs).", 2, -1, 1)
                .Bool("jump", "press / hold: jump button (rising edge, buffered 0.25 s).").Bool("action", "press / hold: action button (tap under 0.3 s or hold = grab).")
                .Int("steps", "press / wait: logic steps (60 = 1 s).", 1, ScenarioPlan.MaxPressSteps).Num("seconds", "wait: game seconds instead of steps.", 0.02, 60)
                .Bool("stop_on_death", "press / wait / until: a death fails the step (false runs through it).", true)
                .Any("until", "until: one wait_until condition object, e.g. {\"grounded\":true} or {\"near\":{\"target\":\"o3\"}}.")
                .Num("within_s", "until: game-time limit in seconds.", 10, 0.1, 50)
                .StrMax("path", "expect: observe path (body.state, body.x, screen, checkpoint.savepoint, objects.0.kind, hazards.length).", 200)
                .Enum("cmp", "expect: comparison (default eq with value, exists without).", null, ScenarioPlan.Cmps)
                .Any("value", "expect: value to compare with.")
                .Num("tol", "expect: number tolerance.", 0.01, 0, 1000)
                .Enum("mode", "load: respawn, quiet, chapter (writes the save), reload_respawn, reload_chapter or newgame.", "respawn", ScenarioPlan.LoadModes)
                .Int("subscene", "load: savepoint subscene index.", 0, 999).Int("savepoint", "load: savepoint index.", 0, 0, 999)
                .Bool("confirm", "load: allow a load that writes INSIDE.sav (needed only with play.save_guard on).", false)
                .Enum("shot", "capture: preview, full or marked (the PNG stays on disk; only the path is returned).", "full", ScenarioPlan.ShotModes)
                .StrMax("cmd", "command: console command line.", 300).StrMax("text", "note: text of the note.", 300)
                .Required("do");
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "run");
            var r = PlayShared.Env();
            r["op"] = op;
            var warnings = new List<string>();
            if (op == "list")
            {
                r["outcome"] = "listed";
                r["scenarios"] = ListFiles();
                r["warnings"] = PlayShared.L(warnings);
                return ToolResult.Ok(r);
            }
            object src;
            string from;
            if (PlayShared.Has(a, "file"))
            {
                if (PlayShared.Has(a, "steps")) throw new McpException(McpErr.BadArgs, "give file or steps, not both");
                src = LoadFile(PlayShared.Str(a, "file", ""), out from);
            }
            else if (PlayShared.Has(a, "steps")) { src = PlayShared.List(a, "steps"); from = "inline"; }
            else throw new McpException(McpErr.BadArgs, "give steps or file", "run_scenario {op:\"list\"} shows the files");
            string error;
            var doc = ScenarioPlan.Parse(src, Saves.Guard, out error);
            if (doc == null) throw new McpException(McpErr.BadArgs, error, "fix the step and run op check again");
            if (doc.name.Length == 0) doc.name = from;
            if (a["stop_on_fail"] is bool) doc.stopOnFail = (bool)a["stop_on_fail"];
            int startAt = PlayShared.Int(a, "start_at", 0);
            if (startAt >= doc.steps.Count) throw new McpException(McpErr.BadArgs, "start_at " + startAt + " is past the last step (" + (doc.steps.Count - 1) + ")");
            warnings.AddRange(doc.warnings);
            r["name"] = doc.name;
            r["total"] = (double)doc.steps.Count;
            r["game_steps"] = (double)ScenarioPlan.MaxGameSteps(doc, startAt);
            if (op == "check")
            {
                r["outcome"] = "valid";
                r["ran"] = 0.0;
                r["next"] = (double)startAt;
                r["failures"] = 0.0;
                if (ScenarioPlan.MaxGameSteps(doc, startAt) > 2400) warnings.Add("the steps may take more than one call (about 40 s of game time per call): continue with start_at = next");
                r["warnings"] = PlayShared.L(warnings);
                return ToolResult.Ok(r);
            }
            return Run(doc, startAt, PlayShared.Str(a, "observe", "concise") != "none", ctx, r, warnings);
        }

        static ToolResult Run(ScenarioDoc doc, int startAt, bool wantObs, CallContext ctx, Json.Obj r, List<string> warnings)
        {
            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return busy;
            long mark = SafeMark();
            var results = new List<object>();
            int failures = 0, ran = 0, next = -1;
            string outcome = "passed";
            try
            {
                for (int i = startAt; i < doc.steps.Count; i++)
                {
                    if (ctx.Cancelled || job.Cancelled) { outcome = "cancelled"; next = i; break; }
                    if (ctx.RemainingMs < 4000) { outcome = "timeout"; next = i; break; }
                    var s = doc.steps[i];
                    job.Progress(i - startAt, doc.steps.Count - startAt, Tool + " " + i + " " + s.kind);
                    var row = new Json.Obj();
                    row["i"] = (double)i;
                    row["do"] = s.kind;
                    if (s.label.Length > 0) row["label"] = s.label;
                    bool ok;
                    string stop = null;
                    try { ok = Exec(s, ctx, job, row, warnings, out stop); }
                    catch (McpException e) { ok = false; row["detail"] = e.code + ": " + e.Message; if (e.code == McpErr.Cancelled) stop = "cancelled"; }
                    catch (NotImplementedException e) { ok = false; row["detail"] = "not_built: " + e.Message; }
                    catch (Exception e) { ok = false; row["detail"] = e.GetType().Name + ": " + e.Message; }
                    row["ok"] = ok;
                    results.Add(row);
                    ran++;
                    if (stop == "cancelled") { outcome = "cancelled"; next = i + 1 < doc.steps.Count ? i + 1 : -1; break; }
                    if (stop == "loading") { outcome = "stopped"; next = i + 1 < doc.steps.Count ? i + 1 : -1; warnings.Add("a load is still running (see the step detail); wait_until {any:[{job:...}]} and continue with start_at"); break; }
                    if (!ok)
                    {
                        failures++;
                        if (doc.stopOnFail) { outcome = "failed"; next = i + 1 < doc.steps.Count ? i + 1 : -1; break; }
                    }
                }
                if (outcome == "passed" && failures > 0) outcome = "failed";
            }
            finally
            {
                try { AgentCore.Release(); } catch (Exception) { }
            }
            r["outcome"] = outcome;
            r["ran"] = (double)ran;
            r["next"] = (double)next;
            r["failures"] = (double)failures;
            r["results"] = results;
            if (mark >= 0)
            {
                try { r["changes"] = ChangeLog.Since(mark, 20); } catch (Exception) { warnings.Add("changes_unavailable"); }
            }
            if (wantObs)
            {
                try { r["observe"] = ctx.Main(() => Observer.Concise(Tool)); }
                catch (NotImplementedException) { warnings.Add("observe_unavailable"); }
                catch (McpException e) { warnings.Add("observe_failed: " + e.code + " " + e.Message); }
            }
            if (warnings.Count > 40) warnings.RemoveRange(40, warnings.Count - 40);
            r["warnings"] = PlayShared.L(warnings);
            return ToolResult.Ok(r);
        }

        static bool Exec(ScenarioStep s, CallContext ctx, McpJob job, Json.Obj row, List<string> warnings, out string stop)
        {
            stop = null;
            switch (s.kind)
            {
                case "release":
                    AgentCore.Release();
                    row["detail"] = "input released";
                    return true;
                case "hold":
                    EnsureAgent(ctx, warnings);
                    AgentCore.Enqueue(new List<InputFrame> { Frame(s, 1) }, true);
                    row["detail"] = "held " + Describe(s);
                    return true;
                case "press":
                {
                    EnsureAgent(ctx, warnings);
                    InputFrame held;
                    bool hadHold = AgentCore.Latched(out held);
                    AgentCore.Enqueue(new List<InputFrame> { Frame(s, s.steps) }, false);
                    bool ok = RunSteps(s, s.steps, null, ctx, row, warnings, "press " + Describe(s), out stop);
                    if (AgentCore.QueuedSteps > 0)
                    {
                        AgentCore.Release();
                        if (hadHold) AgentCore.Enqueue(new List<InputFrame> { held }, true);
                    }
                    return ok;
                }
                case "wait":
                    return RunSteps(s, s.steps, null, ctx, row, warnings, "wait", out stop);
                case "until":
                {
                    string err;
                    var check = Waits.Compile(s.until, ctx, out err);
                    if (check == null) { row["detail"] = "until: " + (err ?? "invalid condition"); return false; }
                    return RunSteps(s, Math.Max(1, (int)Math.Ceiling(s.withinS * 60)), check, ctx, row, warnings, "until", out stop);
                }
                case "expect":
                {
                    var obs = ctx.Main(() => Observer.Observe(new ObserveOptions { detail = "full" }));
                    bool found;
                    object v = ScenarioPlan.Lookup(obs, s.path, out found);
                    string detail;
                    bool ok = ScenarioPlan.Compare(v, found, s.cmp, s.value, s.tol, out detail);
                    row["detail"] = s.path + " " + detail;
                    return ok;
                }
                case "observe":
                {
                    var obs = ctx.Main(() => Observer.Concise(Tool));
                    row["value"] = Summary(obs);
                    row["detail"] = "observed";
                    return true;
                }
                case "capture":
                {
                    var shot = McpShot.Capture(ctx, new ShotRequest { mode = s.shot == "marked" ? "marked" : s.shot, maxWidth = 960, clean = true });
                    if (shot == null || !string.IsNullOrEmpty(shot.error)) { row["detail"] = "capture failed: " + (shot != null ? shot.error : "no result"); return false; }
                    row["value"] = shot.path ?? "";
                    row["detail"] = "captured " + shot.srcWidth + "x" + shot.srcHeight + (shot.black ? " (black frame)" : "");
                    if (shot.black) warnings.Add("steps[" + s.index + "]: the capture is a black frame");
                    return true;
                }
                case "command":
                {
                    string why;
                    if (ScenarioPlan.CommandRefused(s.cmd, Saves.Guard, out why)) { row["detail"] = "refused: " + why; return false; }
                    string line = s.cmd;
                    string output = ctx.Main(() => Remote.RunCommand(line, DevCore.Instance), 10000) ?? "";
                    row["value"] = output.Length > 600 ? output.Substring(0, 600) + "..." : output;
                    string fail = CommandFailure(output);
                    row["detail"] = fail == null ? "ok" : "failed: " + fail;
                    return fail == null;
                }
                case "note":
                {
                    string text = s.text.Length > 0 ? s.text : s.label;
                    int id = ctx.Main(() => Notes.Add(text), 5000);
                    row["value"] = (double)id;
                    row["detail"] = "note " + id;
                    return true;
                }
                case "load":
                    return Load(s, ctx, job, row, out stop);
            }
            row["detail"] = "unknown step";
            return false;
        }

        static bool RunSteps(ScenarioStep s, int steps, Func<string> check, CallContext ctx, Json.Obj row, List<string> warnings, string what, out string stop)
        {
            bool deathWanted = s.until != null && (s.until.ContainsKey("dead") || s.until.ContainsKey("alive"));
            var req = new RunRequest
            {
                maxSteps = steps,
                timeoutMs = Math.Max(1000, Math.Min(50000, ctx.RemainingMs - 3000)),
                stopCheck = check,
                stopOnDeath = s.stopOnDeath && !deathWanted,
                stopOnLoad = true,
                label = Tool + " " + s.index
            };
            var rr = Stepper.Run(ctx, req);
            stop = rr.stop == RunStop.Cancelled ? "cancelled" : null;
            row["steps"] = (double)rr.steps;
            row["stop"] = rr.stop;
            foreach (var w in rr.warnings) if (!warnings.Contains(w)) warnings.Add(w);
            bool ok = check != null ? rr.stop == RunStop.Condition : rr.stop == RunStop.Steps || (!req.stopOnDeath && rr.stop == RunStop.Died);
            string tail = string.IsNullOrEmpty(rr.detail) ? "" : " (" + rr.detail + ")";
            row["detail"] = what + ": " + rr.steps + " steps, stop " + rr.stop + tail;
            return ok;
        }

        static bool Load(ScenarioStep s, CallContext ctx, McpJob job, Json.Obj row, out string stop)
        {
            stop = null;
            var req = new LoadRequest
            {
                mode = s.mode,
                subscene = s.subscene,
                savepoint = s.savepoint,
                wait = true,
                timeoutMs = Math.Max(2000, Math.Min(45000, ctx.RemainingMs - 4000)),
                settleFrames = 20,
                confirm = s.confirm
            };
            var res = PlayLevels.StartLoad(ctx, job, req);
            string outcome = res != null ? res.Str("outcome", "") : "";
            var cur = res != null ? res["current"] as Json.Obj : null;
            row["value"] = cur;
            string backup = res != null ? res.Str("backup", null) : null;
            row["detail"] = "load " + s.mode + (s.subscene >= 0 ? " " + s.subscene + ":" + s.savepoint : "") + ": " + outcome + (backup != null ? " (save backup " + backup + ")" : "");
            if (outcome == "loading") { stop = "loading"; return true; }
            return outcome == "done";
        }

        static void EnsureAgent(CallContext ctx, List<string> warnings)
        {
            string w = ctx.Main(() => AgentCore.Installed && AgentCore.Mode != InputMode.Human ? null : AgentCore.Install(InputMode.Agent));
            if (w != null && !warnings.Contains(w)) warnings.Add(w);
        }

        static InputFrame Frame(ScenarioStep s, int steps)
        {
            return new InputFrame { x = s.hasStick ? s.x : 0f, y = s.hasStick ? s.y : 0f, jump = s.jump, action = s.action, steps = Math.Max(1, steps) };
        }

        static string Describe(ScenarioStep s)
        {
            var parts = new List<string>();
            if (s.hasStick) parts.Add("stick " + PlayShared.F(s.x) + "," + PlayShared.F(s.y));
            if (s.jump) parts.Add("jump");
            if (s.action) parts.Add("action");
            return parts.Count == 0 ? "neutral" : string.Join(" + ", parts.ToArray());
        }

        static Json.Obj Summary(Json.Obj obs)
        {
            var o = new Json.Obj();
            if (obs == null) return o;
            foreach (var k in new[] { "screen", "ready", "reason" }) if (obs.ContainsKey(k)) o[k] = obs[k];
            var b = obs["body"] as Json.Obj;
            if (b != null)
            {
                var bo = new Json.Obj();
                foreach (var k in new[] { "kind", "state", "x", "y", "grounded", "dead" }) if (b.ContainsKey(k)) bo[k] = b[k];
                o["body"] = bo;
            }
            if (obs.ContainsKey("checkpoint")) o["checkpoint"] = obs["checkpoint"];
            return o;
        }

        static readonly string[] FailMarks = { "not found", "usage", "unknown", "busy", "no fsm", "component not found", "field not found", "property not found",
            "no parameterless method", "no active character", "error in", "failed", "not built" };

        static string CommandFailure(string output)
        {
            string o = (output ?? "").ToLowerInvariant();
            foreach (var m in FailMarks) if (o.Contains(m)) return m;
            return null;
        }

        static long SafeMark()
        {
            try { return ChangeLog.Mark(); } catch (Exception) { return -1; }
        }

        static List<string> Dirs()
        {
            var l = new List<string>();
            string mod = GuideBook.ModDir();
            if (mod != null) l.Add(Path.Combine(mod, "scenarios"));
            l.Add(Path.Combine(GuideBook.Root, "scenarios"));
            return l;
        }

        static object LoadFile(string name, out string from)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0 || n.IndexOf("..", StringComparison.Ordinal) >= 0 || n.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                throw new McpException(McpErr.BadArgs, "file: a plain scenario name (no folders)", "run_scenario {op:\"list\"}");
            if (!n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) n += ".json";
            foreach (var d in Dirs())
            {
                string p = Path.Combine(d, n);
                if (!File.Exists(p)) continue;
                from = Path.GetFileNameWithoutExtension(n);
                try { return Json.Parse(File.ReadAllText(p, Encoding.UTF8)); }
                catch (Exception e) { throw new McpException(McpErr.BadArgs, n + ": not valid JSON: " + e.Message); }
            }
            throw new McpException(McpErr.NotFound, "no scenario file " + n, "run_scenario {op:\"list\"} shows the files");
        }

        static List<object> ListFiles()
        {
            var l = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in Dirs())
            {
                string[] files;
                try { if (!Directory.Exists(d)) continue; files = Directory.GetFiles(d, "*.json"); } catch (Exception) { continue; }
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    string file = Path.GetFileNameWithoutExtension(f);
                    if (!seen.Add(file) || l.Count >= 200) continue;
                    var o = new Json.Obj();
                    o["file"] = file;
                    try
                    {
                        var src = Json.Parse(File.ReadAllText(f, Encoding.UTF8));
                        string err;
                        var doc = ScenarioPlan.Parse(src, Saves.Guard, out err);
                        var jo = src as Json.Obj;
                        o["name"] = doc != null && doc.name.Length > 0 ? doc.name : file;
                        if (jo != null && jo["description"] is string) o["description"] = jo["description"];
                        o["steps"] = (double)(doc != null ? doc.steps.Count : 0);
                        if (err != null) o["error"] = err;
                    }
                    catch (Exception e) { o["error"] = e.Message; }
                    l.Add(o);
                }
            }
            return l;
        }
    }
}
