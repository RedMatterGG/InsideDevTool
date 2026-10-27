using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InsideDev.Play
{
    // GuideScenarioCore.cs (owner GUIDE): the Unity-free part of run_scenario (GuideScenario.cs): parsing and validating a
    // scenario (inline steps or a file {name, stop_on_fail, steps}), observe path lookup and the expect comparisons.
    // Report 05 item 14 (debuff scenario JSON). Tested offline in qa/mcp/GUIDE.
    internal sealed class ScenarioStep
    {
        public int index;
        public string kind = "", label = "";
        public float x, y;
        public bool hasStick, jump, action, stopOnDeath = true;
        public int steps = 60;
        public Json.Obj until;
        public double withinS = 10, tol = 0.01;
        public string path = "", cmp = "eq", mode = "respawn", shot = "full", cmd = "", text = "";
        public object value;
        public bool hasValue, confirm;
        public int subscene = -1, savepoint = 0;
    }

    internal sealed class ScenarioDoc
    {
        public string name = "";
        public bool stopOnFail = true;
        public readonly List<ScenarioStep> steps = new List<ScenarioStep>();
        public readonly List<string> warnings = new List<string>();
    }

    internal static class ScenarioPlan
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public const int MaxSteps = 200, MaxPressSteps = 3600;
        public static readonly string[] Kinds = { "load", "press", "hold", "release", "wait", "until", "expect", "capture", "command", "observe", "note" };
        public static readonly string[] Cmps = { "eq", "ne", "lt", "le", "gt", "ge", "exists", "missing", "contains" };
        public static readonly string[] LoadModes = { "respawn", "quiet", "chapter", "reload_respawn", "reload_chapter", "newgame" };
        public static readonly string[] ShotModes = { "preview", "full", "marked" };
        // console commands that drive the OS, kill the server or quit (always refused), and game loads that skip the save
        // backup and confirm (refused only with play.save_guard on, same rule as the dev command tool)
        public static readonly string[] RefusedCommands = { "keyhold", "focustest", "remote off", "mcp off", "mcp port", "quit", "exit" };
        public static readonly string[] LoadCommands = { "load", "spawn", "reload", "respawn", "cspawn go", "cspawn respawn" };
        static readonly string[] Keys = { "do", "label", "stick", "jump", "action", "steps", "seconds", "stop_on_death", "until", "within_s", "path", "cmp", "value",
            "tol", "mode", "subscene", "savepoint", "confirm", "shot", "cmd", "text" };

        // doc = a list of steps or {name, stop_on_fail, steps}; error = "steps[2].stick: ..." on the first problem;
        // the default play.save_guard off (command steps may load)
        public static ScenarioDoc Parse(object doc, out string error)
        {
            return Parse(doc, false, out error);
        }

        // saveGuard = Saves.Guard: on refuses the load commands in command steps
        public static ScenarioDoc Parse(object doc, bool saveGuard, out string error)
        {
            error = null;
            var d = new ScenarioDoc();
            List<object> list = doc as List<object>;
            var o = doc as Json.Obj;
            if (o != null)
            {
                foreach (var k in o.order)
                    if (k != "name" && k != "stop_on_fail" && k != "steps" && k != "description") { error = "unknown key '" + k + "' (allowed: name, description, stop_on_fail, steps)"; return null; }
                d.name = o.Str("name", "");
                if (o["stop_on_fail"] is bool) d.stopOnFail = (bool)o["stop_on_fail"];
                else if (o["stop_on_fail"] != null) { error = "stop_on_fail: expected a boolean"; return null; }
                list = o["steps"] as List<object>;
            }
            if (list == null) { error = "steps: expected an array of step objects"; return null; }
            if (list.Count == 0) { error = "steps: empty"; return null; }
            if (list.Count > MaxSteps) { error = "steps: at most " + MaxSteps + " steps (got " + list.Count + ")"; return null; }
            for (int i = 0; i < list.Count; i++)
            {
                var s = ParseStep(list[i] as Json.Obj, i, d.warnings, saveGuard, out error);
                if (s == null) { if (error == null) error = "steps[" + i + "]: expected an object"; return null; }
                d.steps.Add(s);
            }
            return d;
        }

        static ScenarioStep ParseStep(Json.Obj o, int i, List<string> warnings, bool saveGuard, out string error)
        {
            error = null;
            string at = "steps[" + i + "]";
            if (o == null) { error = at + ": expected an object"; return null; }
            foreach (var k in o.order) if (Array.IndexOf(Keys, k) < 0) { error = at + ": unknown key '" + k + "'"; return null; }
            var s = new ScenarioStep { index = i };
            s.kind = o.Str("do", "");
            if (Array.IndexOf(Kinds, s.kind) < 0) { error = at + ".do: one of " + string.Join(", ", Kinds); return null; }
            if (!Text(o, "label", 200, ref s.label, at, out error)) return null;
            if (!Bool(o, "jump", ref s.jump, at, out error) || !Bool(o, "action", ref s.action, at, out error)
                || !Bool(o, "stop_on_death", ref s.stopOnDeath, at, out error) || !Bool(o, "confirm", ref s.confirm, at, out error)) return null;
            if (o["stick"] != null)
            {
                var l = o["stick"] as List<object>;
                if (l == null || l.Count != 2 || !(l[0] is double) || !(l[1] is double) || Math.Abs((double)l[0]) > 1 || Math.Abs((double)l[1]) > 1)
                { error = at + ".stick: expected 2 numbers in -1..1"; return null; }
                s.x = (float)(double)l[0]; s.y = (float)(double)l[1]; s.hasStick = true;
                double mag = Math.Sqrt(s.x * s.x + s.y * s.y);
                if (mag > 0 && mag < 0.3) warnings.Add(at + ".stick: magnitude below the 0.3 deadzone reads as 0");
            }
            int steps = -1;
            if (o["steps"] != null && !Int(o, "steps", 1, MaxPressSteps, ref steps, at, out error)) return null;
            double seconds = -1;
            if (o["seconds"] != null && !Num(o, "seconds", 0.02, 60, ref seconds, at, out error)) return null;
            if (steps > 0 && seconds > 0) { error = at + ": give steps or seconds, not both"; return null; }
            if (seconds > 0) steps = Math.Max(1, (int)Math.Round(seconds * 60));
            if (!Num(o, "within_s", 0.1, 50, ref s.withinS, at, out error) || !Num(o, "tol", 0, 1000, ref s.tol, at, out error)) return null;
            if (!Text(o, "path", 200, ref s.path, at, out error) || !Text(o, "cmd", 300, ref s.cmd, at, out error) || !Text(o, "text", 300, ref s.text, at, out error)) return null;
            if (!Choice(o, "cmp", Cmps, ref s.cmp, at, out error) || !Choice(o, "mode", LoadModes, ref s.mode, at, out error) || !Choice(o, "shot", ShotModes, ref s.shot, at, out error)) return null;
            if (!Int(o, "subscene", 0, 999, ref s.subscene, at, out error) || !Int(o, "savepoint", 0, 999, ref s.savepoint, at, out error)) return null;
            if (o.ContainsKey("value")) { s.value = o["value"]; s.hasValue = true; }
            if (o["until"] != null)
            {
                s.until = o["until"] as Json.Obj;
                if (s.until == null || s.until.Count != 1) { error = at + ".until: an object with exactly one condition key (as in wait_until any[])"; return null; }
            }
            switch (s.kind)
            {
                case "press":
                    s.steps = steps > 0 ? steps : 1;
                    if (!s.hasStick && !s.jump && !s.action) warnings.Add(at + ": press without stick, jump or action is a neutral wait");
                    break;
                case "hold":
                    if (!s.hasStick && !s.jump && !s.action) { error = at + ": hold needs stick, jump or action"; return null; }
                    break;
                case "wait":
                    s.steps = steps > 0 ? steps : 60;
                    break;
                case "until":
                    if (s.until == null) { error = at + ".until: required for do until"; return null; }
                    break;
                case "expect":
                    if (s.path.Length == 0) { error = at + ".path: required for do expect (an observe path such as body.state)"; return null; }
                    if (o["cmp"] == null) s.cmp = s.hasValue ? "eq" : "exists";
                    if (s.cmp != "exists" && s.cmp != "missing" && !s.hasValue) { error = at + ".value: required for cmp " + s.cmp; return null; }
                    break;
                case "load":
                    bool needsTarget = s.mode == "respawn" || s.mode == "quiet" || s.mode == "chapter";
                    if (needsTarget && o["subscene"] == null) { error = at + ".subscene: required for load mode " + s.mode; return null; }
                    break;
                case "command":
                    string why;
                    if (s.cmd.Trim().Length == 0) { error = at + ".cmd: required for do command"; return null; }
                    if (CommandRefused(s.cmd, saveGuard, out why)) { error = at + ".cmd: refused: " + why; return null; }
                    break;
            }
            return s;
        }

        static bool Bool(Json.Obj o, string k, ref bool v, string at, out string error)
        {
            error = null;
            if (o[k] == null) return true;
            if (!(o[k] is bool)) { error = at + "." + k + ": expected a boolean"; return false; }
            v = (bool)o[k];
            return true;
        }

        static bool Int(Json.Obj o, string k, int min, int max, ref int v, string at, out string error)
        {
            error = null;
            if (o[k] == null) return true;
            if (!(o[k] is double)) { error = at + "." + k + ": expected an integer"; return false; }
            double d = (double)o[k];
            if (d != Math.Floor(d) || d < min || d > max) { error = at + "." + k + ": expected an integer in " + min + ".." + max; return false; }
            v = (int)d;
            return true;
        }

        static bool Num(Json.Obj o, string k, double min, double max, ref double v, string at, out string error)
        {
            error = null;
            if (o[k] == null) return true;
            if (!(o[k] is double)) { error = at + "." + k + ": expected a number"; return false; }
            double d = (double)o[k];
            if (d < min || d > max) { error = at + "." + k + ": expected " + min.ToString(IC) + ".." + max.ToString(IC); return false; }
            v = d;
            return true;
        }

        static bool Text(Json.Obj o, string k, int maxLen, ref string v, string at, out string error)
        {
            error = null;
            if (o[k] == null) return true;
            var s = o[k] as string;
            if (s == null) { error = at + "." + k + ": expected a string"; return false; }
            if (s.Length > maxLen) { error = at + "." + k + ": at most " + maxLen + " characters"; return false; }
            v = s;
            return true;
        }

        static bool Choice(Json.Obj o, string k, string[] values, ref string v, string at, out string error)
        {
            error = null;
            if (o[k] == null) return true;
            var s = o[k] as string;
            if (s == null || Array.IndexOf(values, s) < 0) { error = at + "." + k + ": one of " + string.Join(", ", values); return false; }
            v = s;
            return true;
        }

        public static bool CommandRefused(string cmd, bool saveGuard, out string why)
        {
            why = null;
            string c = " " + string.Join(" ", (cmd ?? "").Trim().ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)) + " ";
            foreach (var r in RefusedCommands)
                if (c.StartsWith(" " + r + " ")) { why = "'" + r + "' is not allowed through MCP (OS input, server stop or quit); use act / restore instead"; return true; }
            if (saveGuard)
                foreach (var r in LoadCommands)
                    if (c.StartsWith(" " + r + " ")) { why = "'" + r + "' loads without the save backup and confirm (chapter loads write INSIDE.sav and unlock chapters); use a load step {do:\"load\", mode, subscene, savepoint, confirm}"; return true; }
            return false;
        }

        // dotted path into an observation: body.state, objects.0.kind, objects[0].kind, warnings.length
        public static object Lookup(Json.Obj root, string path, out bool found)
        {
            found = false;
            object cur = root;
            if (root == null || string.IsNullOrEmpty(path)) return null;
            string p = path.Replace("[", ".").Replace("]", "");
            foreach (var part in p.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var o = cur as Json.Obj;
                var l = cur as List<object>;
                if (o != null)
                {
                    if (!o.ContainsKey(part)) return null;
                    cur = o[part];
                    continue;
                }
                if (l != null)
                {
                    if (part == "length" || part == "count") { cur = (double)l.Count; continue; }
                    int i;
                    if (!int.TryParse(part, NumberStyles.Integer, IC, out i)) return null;
                    if (i < 0) i += l.Count;
                    if (i < 0 || i >= l.Count) return null;
                    cur = l[i];
                    continue;
                }
                return null;
            }
            found = true;
            return cur;
        }

        public static bool Compare(object actual, bool found, string cmp, object expected, double tol, out string detail)
        {
            string a = Render(actual), e = Render(expected);
            bool ok;
            switch (cmp)
            {
                case "exists": ok = found && actual != null; detail = found ? "= " + a : "missing"; return ok;
                case "missing": ok = !found || actual == null; detail = found ? "= " + a : "missing"; return ok;
                case "eq": ok = found && Same(actual, expected, tol); break;
                case "ne": ok = !found || !Same(actual, expected, tol); break;
                case "contains": ok = found && Contains(actual, expected, tol); break;
                default:
                    if (!(actual is double) || !(expected is double)) { detail = "= " + a + " (" + cmp + " needs numbers, want " + e + ")"; return false; }
                    double x = (double)actual, y = (double)expected;
                    ok = cmp == "lt" ? x < y : cmp == "le" ? x <= y + tol : cmp == "gt" ? x > y : x >= y - tol;
                    break;
            }
            detail = (found ? "= " + a : "missing") + " (want " + cmp + " " + e + ")";
            return ok;
        }

        static bool Same(object a, object b, double tol)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a is double && b is double) return Math.Abs((double)a - (double)b) <= tol;
            if (a is string && b is string) return (string)a == (string)b;
            if (a is bool && b is bool) return (bool)a == (bool)b;
            return Render(a) == Render(b);
        }

        static bool Contains(object a, object b, double tol)
        {
            var s = a as string;
            if (s != null) return b is string && s.IndexOf((string)b, StringComparison.OrdinalIgnoreCase) >= 0;
            var l = a as List<object>;
            if (l != null) { foreach (var x in l) if (Same(x, b, tol)) return true; return false; }
            var o = a as Json.Obj;
            return o != null && b is string && o.ContainsKey((string)b);
        }

        public static string Render(object v)
        {
            if (v == null) return "null";
            if (v is double) return ((double)v).ToString("0.###", IC);
            if (v is bool) return (bool)v ? "true" : "false";
            var s = v as string;
            if (s != null) return s.Length > 80 ? s.Substring(0, 80) + "..." : s;
            string j = JsonLine.Compact(v);
            return j.Length > 120 ? j.Substring(0, 120) + "..." : j;
        }

        // game steps the scenario asks for at most (press / wait lengths, until limits), for the budget hint
        public static int MaxGameSteps(ScenarioDoc d, int from)
        {
            long n = 0;
            for (int i = Math.Max(0, from); i < d.steps.Count; i++)
            {
                var s = d.steps[i];
                if (s.kind == "press" || s.kind == "wait") n += s.steps;
                else if (s.kind == "until") n += (long)Math.Ceiling(s.withinS * 60);
            }
            return (int)Math.Min(int.MaxValue, n);
        }
    }
}
