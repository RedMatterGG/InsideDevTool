using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InsideDev.Play
{
    // CtlCore.cs: the Unity-free half of the ctl console commands (Ctl.cs; docs/MCP_PLAY_DESIGN.md section 18): the
    // duration and seq script grammar, the word parser that turns "ctl ..." into a CtlPlan, the held-input arithmetic and
    // the reply texts. Compiled into InsideDev.Play.dll and, as is, into the offline tests (qa/mcp/CTL/CtlTest.cs).
    // Durations: a plain number is seconds (1.2), a whole number followed by s is logic steps (72s = 72 steps = 1.2 s).
    public enum CtlKind { Help, Status, StatusJson, Input, Hold, Release, StopAt, Tool, Mode, Job, Stop }

    // one stretch of timed input; stick false keeps the held stick (ctl hold) for these steps
    public struct CtlFrame
    {
        public float x, y;
        public bool stick, jump, action;
        public int steps;
    }

    public sealed class CtlPlan
    {
        public CtlKind kind;
        public string error, what = "", tool, op, mode, jobId, gait = "run";
        public readonly List<CtlFrame> frames = new List<CtlFrame>();
        public readonly List<string> warnings = new List<string>();
        public int steps;
        public CtlFrame set;
        public bool releaseAll, relStick, relJump, relAction, async;
        public double target;
        public Json.Obj args;
    }

    public static class CtlCore
    {
        public const int StepsPerSecond = 60, TapSteps = 6, TapPress = 5, JumpRunSteps = 30, MaxSteps = 36000, MaxSegments = 500, StopAtSteps = 3600, GotoSteps = 1800, MaxTimeStep = 600;
        public const float WalkStick = 0.5f, Deadzone = 0.3f;
        public const double StopAtTolerance = 0.05;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public const string Help =
            "ctl: drive the boy / huddle through AgentCore (no OS input, no window focus). <t> = seconds (1.2) or logic steps (72s; 60 steps = 1 s).\n" +
            "  ctl status [json] | ctl job [jN] | ctl stop | ctl help\n" +
            "  ctl stick <x> <y> [for <t>]   ctl press <jump|action|grab ...> [for <t>]   ctl tap <jump|action|grab ...> [<t>]\n" +
            "  ctl hold <left|right|up|down|stick <x> <y>|jump|action|grab ...>   ctl release [all | stick|jump|action|grab ...]\n" +
            "  ctl run left|right [<t>]   ctl walk left|right [<t>]   ctl jump [left|right] [<t>]   ctl grab on|off   ctl swim <dir> [<t>]\n" +
            "  ctl goto <x> [<y>] [walk]  (move_to navigator)   ctl stopat <x> [walk]  (plain stick, stops when the body crosses x)\n" +
            "  ctl seq \"<script>\"  R L U D <t>[@0.3..1] stick, J [<t>] jump, A [<t>] action (no <t>: a 0.1 s tap), G <t> grab, W <t> wait, + = together\n" +
            "                      e.g. ctl seq \"R1.2 J R0.4+J G1 W0.5 L0.3\"\n" +
            "  ctl track rec [name] | stop | save <name> [overwrite] | play [name] [speed <k>] [freeze] [correct|nocorrect] [free] | list | info [name] | status | load <name> | trim <from> [<to>]\n" +
            "  ctl time [state] | freeze | run [<k>] | step <n> | speed <k>   ctl mode agent|mix|human\n" +
            "Without <t>, stick / press / run / walk / swim hold until ctl release. Timed commands run as a job: the reply says job=jN, ctl job jN shows it.";

        // ---------------------------------------------------------------- durations
        public static bool TryDuration(string s, out int steps, out string error)
        {
            steps = 0;
            error = null;
            string t = (s ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) { error = "empty duration"; return false; }
            if (t.EndsWith("s", StringComparison.Ordinal))
            {
                int k;
                if (!int.TryParse(t.Substring(0, t.Length - 1), NumberStyles.None, IC, out k))
                {
                    error = "'" + s + "': logic steps are a whole number followed by s (72s = 72 steps = 1.2 s); seconds have no suffix (1.2)";
                    return false;
                }
                if (k < 1 || k > MaxSteps) { error = "'" + s + "': 1.." + MaxSteps + " steps"; return false; }
                steps = k;
                return true;
            }
            double sec;
            if (!double.TryParse(t, NumberStyles.AllowDecimalPoint, IC, out sec))
            {
                error = "'" + s + "' is not a duration (seconds like 1.2, or logic steps like 72s)";
                return false;
            }
            int n = (int)Math.Round(sec * StepsPerSecond);
            if (sec <= 0 || n > MaxSteps) { error = "'" + s + "': more than 0 and at most " + MaxSteps / StepsPerSecond + " s"; return false; }
            steps = Math.Max(1, n);
            return true;
        }

        static bool LooksLikeDuration(string s) { return !string.IsNullOrEmpty(s) && (char.IsDigit(s[0]) || s[0] == '.'); }

        static bool OptDuration(List<string> a, ref int i, out int steps, out bool has, out string error)
        {
            steps = 0;
            has = false;
            error = null;
            if (i >= a.Count) return true;
            if (a[i].ToLowerInvariant() == "for")
            {
                i++;
                if (i >= a.Count) { error = "for needs a duration"; return false; }
            }
            else if (!LooksLikeDuration(a[i])) return true;
            if (!TryDuration(a[i], out steps, out error)) return false;
            i++;
            has = true;
            return true;
        }

        public static string StepsText(int n)
        {
            return n + (n == 1 ? " step" : " steps") + " (" + F(n / (double)StepsPerSecond) + " s)";
        }

        public static int TimeoutMs(int steps, bool stepMode, double speed)
        {
            double s = steps / (double)StepsPerSecond;
            double ms = stepMode ? s * 6000 : s * 1500 / Math.Max(0.05, speed);
            return (int)Math.Min(600000, ms + 10000);
        }

        // ---------------------------------------------------------------- seq scripts
        struct SeqAtom
        {
            public int steps, press;
            public double x, y;
            public bool stick, jump, action;
        }

        // script = segments separated by spaces or commas; a segment = atoms joined by +, started together, each lasting
        // its own duration (the segment lasts as long as its longest atom); stick atoms of one segment add up
        public static bool ParseSeq(string script, List<CtlFrame> frames, List<string> warnings, out int total, out int segments, out string error)
        {
            total = 0;
            segments = 0;
            error = null;
            var tokens = (script ?? "").Split(new[] { ' ', '\t', ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) { error = "empty script"; return false; }
            if (tokens.Length > MaxSegments) { error = "more than " + MaxSegments + " segments"; return false; }
            bool prevJump = false;
            foreach (var tok in tokens)
            {
                var atoms = new List<SeqAtom>();
                foreach (var part in tok.Split('+'))
                {
                    if (part.Length == 0) { error = "'" + tok + "': empty part next to +"; return false; }
                    SeqAtom x;
                    if (!Atom(part, out x, out error)) { error = "'" + tok + "': " + error; return false; }
                    atoms.Add(x);
                }
                var seg = SegmentFrames(atoms);
                segments++;
                if (prevJump && seg.Count > 0 && seg[0].jump)
                    warnings.Add("segment " + segments + " (" + tok + "): the jump is still pressed from the segment before, so this is no new press; put W0.1 before it");
                foreach (var f in seg) { Append(frames, f); total += f.steps; }
                if (total > MaxSteps) { error = "the script is longer than " + MaxSteps + " steps (" + MaxSteps / StepsPerSecond + " s)"; return false; }
                prevJump = seg.Count > 0 && seg[seg.Count - 1].jump;
            }
            return true;
        }

        static bool Atom(string s, out SeqAtom a, out string error)
        {
            a = new SeqAtom();
            error = null;
            char c = char.ToUpperInvariant(s[0]);
            string rest = s.Substring(1);
            double mag = 1;
            int at = rest.IndexOf('@');
            if (at >= 0)
            {
                if ("RLUD".IndexOf(c) < 0) { error = "@ (stick strength) goes only with R L U D"; return false; }
                if (!Num(rest.Substring(at + 1), out mag) || mag < Deadzone || mag > 1) { error = "the strength after @ is 0.3..1"; return false; }
                rest = rest.Substring(0, at);
            }
            switch (c)
            {
                case 'R':
                case 'L':
                case 'U':
                case 'D':
                    if (rest.Length == 0) { error = c + " needs a duration (" + c + "1.2 or " + c + "72s)"; return false; }
                    if (!TryDuration(rest, out a.steps, out error)) return false;
                    a.stick = true;
                    a.x = c == 'R' ? mag : c == 'L' ? -mag : 0;
                    a.y = c == 'U' ? mag : c == 'D' ? -mag : 0;
                    return true;
                case 'J':
                case 'A':
                    if (rest.Length == 0) { a.steps = TapSteps; a.press = TapPress; }
                    else if (!TryDuration(rest, out a.steps, out error)) return false;
                    if (c == 'J') a.jump = true; else a.action = true;
                    return true;
                case 'G':
                    if (rest.Length == 0) { error = "G needs a duration (G1 = grab held 1 s)"; return false; }
                    if (!TryDuration(rest, out a.steps, out error)) return false;
                    a.action = true;
                    return true;
                case 'W':
                    if (rest.Length == 0) { error = "W needs a duration (W0.5)"; return false; }
                    return TryDuration(rest, out a.steps, out error);
            }
            error = "unknown part '" + s[0] + "' (R L U D J A G W)";
            return false;
        }

        static List<CtlFrame> SegmentFrames(List<SeqAtom> atoms)
        {
            var cuts = new List<int> { 0 };
            foreach (var x in atoms)
            {
                if (!cuts.Contains(x.steps)) cuts.Add(x.steps);
                if (x.press > 0 && !cuts.Contains(x.press)) cuts.Add(x.press);
            }
            cuts.Sort();
            var l = new List<CtlFrame>();
            for (int k = 0; k + 1 < cuts.Count; k++)
            {
                int from = cuts[k];
                var f = new CtlFrame { steps = cuts[k + 1] - from };
                double sx = 0, sy = 0;
                foreach (var x in atoms)
                {
                    if (x.steps <= from) continue;
                    if (x.stick) { f.stick = true; sx += x.x; sy += x.y; }
                    bool down = x.press <= 0 || x.press > from;
                    if (x.jump && down) f.jump = true;
                    if (x.action && down) f.action = true;
                }
                if (f.stick)
                {
                    Clamp(ref sx, ref sy);
                    f.x = (float)sx;
                    f.y = (float)sy;
                }
                l.Add(f);
            }
            return l;
        }

        static void Append(List<CtlFrame> frames, CtlFrame f)
        {
            int n = frames.Count;
            if (n > 0)
            {
                var last = frames[n - 1];
                if (last.stick == f.stick && last.jump == f.jump && last.action == f.action && last.x == f.x && last.y == f.y)
                {
                    last.steps += f.steps;
                    frames[n - 1] = last;
                    return;
                }
            }
            frames.Add(f);
        }

        static void Clamp(ref double x, ref double y)
        {
            double m = Math.Sqrt(x * x + y * y);
            if (m > 1) { x /= m; y /= m; }
        }

        // ---------------------------------------------------------------- held input
        public static bool IsNeutral(InputFrame f) { return f.x == 0 && f.y == 0 && !f.jump && !f.action; }

        public static InputFrame ApplyHold(InputFrame held, bool hasHeld, CtlFrame set)
        {
            var o = hasHeld ? held : new InputFrame();
            if (set.stick) { o.x = set.x; o.y = set.y; }
            if (set.jump) o.jump = true;
            if (set.action) o.action = true;
            o.steps = 1;
            return o;
        }

        public static InputFrame ReleaseParts(InputFrame held, bool stick, bool jump, bool action)
        {
            var o = held;
            if (stick) { o.x = 0; o.y = 0; }
            if (jump) o.jump = false;
            if (action) o.action = false;
            o.steps = 1;
            return o;
        }

        public static InputFrame WithoutStick(InputFrame held) { return ReleaseParts(held, true, false, false); }

        // the frames to queue: each stretch with the held buttons added and the held stick where it gives none; keep adds
        // the held input as the last frame so it stays latched after the stretch, without a gap
        public static List<InputFrame> Merge(List<CtlFrame> frames, InputFrame held, bool hasHeld, bool keep)
        {
            var l = new List<InputFrame>();
            foreach (var f in frames)
            {
                if (f.steps <= 0) continue;
                var o = new InputFrame { steps = f.steps };
                if (f.stick) { o.x = f.x; o.y = f.y; }
                else if (hasHeld) { o.x = held.x; o.y = held.y; }
                o.jump = f.jump || (hasHeld && held.jump);
                o.action = f.action || (hasHeld && held.action);
                l.Add(o);
            }
            if (keep && hasHeld) l.Add(new InputFrame { x = held.x, y = held.y, jump = held.jump, action = held.action, steps = 1 });
            return l;
        }

        public static string HeldText(InputFrame f)
        {
            var parts = new List<string>();
            if (f.x != 0 || f.y != 0) parts.Add("stick " + F(f.x) + "," + F(f.y));
            if (f.jump) parts.Add("jump");
            if (f.action) parts.Add("action");
            return parts.Count == 0 ? "nothing" : string.Join(" + ", parts.ToArray());
        }

        public static bool SoftNotReady(string reason)
        {
            string r = (reason ?? "").ToLowerInvariant();
            return r.Contains("cutscene") || r.Contains("scripted") || r.Contains("custom_anim") || r.Contains("customanim");
        }

        // ---------------------------------------------------------------- the words of a command
        // the core's bridge splitting rules (Remote.Split): spaces and tabs separate, double quotes group and are dropped
        public static List<string> Words(string line)
        {
            var r = new List<string>();
            var sb = new StringBuilder();
            bool q = false;
            foreach (char ch in line ?? "")
            {
                if (ch == '"') { q = !q; continue; }
                if (!q && (ch == ' ' || ch == '\t')) { if (sb.Length > 0) { r.Add(sb.ToString()); sb.Length = 0; } continue; }
                sb.Append(ch);
            }
            if (sb.Length > 0) r.Add(sb.ToString());
            return r;
        }

        public static CtlPlan Parse(List<string> a)
        {
            var p = new CtlPlan();
            if (a == null || a.Count < 2) { p.kind = CtlKind.Status; return p; }
            switch (a[1].ToLowerInvariant())
            {
                case "help":
                case "?":
                    p.kind = CtlKind.Help;
                    return p;
                case "status":
                    bool json = a.Count > 2 && a[2].ToLowerInvariant() == "json";
                    p.kind = json ? CtlKind.StatusJson : CtlKind.Status;
                    return Extra(a, p, json ? 3 : 2, "ctl status [json]");
                case "state":
                    p.kind = CtlKind.StatusJson;
                    return Extra(a, p, 2, "ctl state");
                case "stick": return Stick(a, p);
                case "press": return Press(a, p, false);
                case "tap": return Press(a, p, true);
                case "hold": return Hold(a, p);
                case "release": return Release(a, p);
                case "run": return Move(a, p, 1f, "run");
                case "walk": return Move(a, p, WalkStick, "walk");
                case "jump": return Jump(a, p);
                case "grab": return Grab(a, p);
                case "swim": return Swim(a, p);
                case "goto": return Goto(a, p);
                case "stopat": return StopAt(a, p);
                case "seq": return Seq(a, p);
                case "track": return Track(a, p);
                case "time": return Time(a, p);
                case "mode": return Mode(a, p);
                case "job": return Job(a, p);
                case "stop":
                    p.kind = CtlKind.Stop;
                    return Extra(a, p, 2, "ctl stop");
            }
            p.error = "unknown ctl command '" + a[1] + "'; ctl help lists them";
            return p;
        }

        static CtlPlan Usage(CtlPlan p, string usage, string why)
        {
            p.error = "usage: " + usage + (string.IsNullOrEmpty(why) ? "" : " (" + why + ")");
            return p;
        }

        static CtlPlan Extra(List<string> a, CtlPlan p, int used, string usage)
        {
            return a.Count > used ? Usage(p, usage, "unexpected '" + a[used] + "'") : p;
        }

        static bool Num(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, IC, out v) && !double.IsNaN(v) && !double.IsInfinity(v);
        }

        static string StickError(double x, double y)
        {
            if (x < -1 || x > 1 || y < -1 || y > 1) return "x and y are -1..1";
            double m = Math.Sqrt(x * x + y * y);
            if (m > 0 && m < Deadzone) return "the stick strength must be 0 or 0.3..1; below 0.3 the game reads 0";
            return null;
        }

        static CtlPlan Timed(CtlPlan p, string desc, CtlFrame f, int steps)
        {
            f.steps = steps;
            p.kind = CtlKind.Input;
            p.frames.Add(f);
            p.steps = steps;
            p.what = desc + " for " + StepsText(steps);
            return p;
        }

        // a tap: pressed for TapPress steps, released in the last of TapSteps, so taps in a row are separate presses
        static CtlPlan Tap(CtlPlan p, string desc, CtlFrame f)
        {
            f.steps = TapPress;
            p.kind = CtlKind.Input;
            p.frames.Add(f);
            p.frames.Add(new CtlFrame { steps = TapSteps - TapPress });
            p.steps = TapSteps;
            p.what = desc + " for " + StepsText(TapSteps);
            return p;
        }

        static CtlPlan Held(CtlPlan p, string desc, CtlFrame f)
        {
            p.kind = CtlKind.Hold;
            p.set = f;
            p.what = "hold " + desc;
            return p;
        }

        static CtlPlan Rest(List<string> a, CtlPlan p, int i, string usage, string desc, CtlFrame f)
        {
            int steps;
            bool has;
            string err;
            if (!OptDuration(a, ref i, out steps, out has, out err)) return Usage(p, usage, err);
            if (i < a.Count) return Usage(p, usage, "unexpected '" + a[i] + "'");
            return has ? Timed(p, desc, f, steps) : Held(p, desc, f);
        }

        const string UStick = "ctl stick <x> <y> [for <seconds>|<steps>s]";

        static CtlPlan Stick(List<string> a, CtlPlan p)
        {
            double x, y;
            if (a.Count < 4 || !Num(a[2], out x) || !Num(a[3], out y)) return Usage(p, UStick, null);
            string bad = StickError(x, y);
            if (bad != null) return Usage(p, UStick, bad);
            return Rest(a, p, 4, UStick, "stick " + F(x) + "," + F(y), new CtlFrame { stick = true, x = (float)x, y = (float)y });
        }

        static string Button(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "jump":
                case "j":
                    return "jump";
                case "action":
                case "a":
                case "grab":
                case "g":
                    return "action";
            }
            return null;
        }

        static CtlPlan Press(List<string> a, CtlPlan p, bool tap)
        {
            string usage = tap ? "ctl tap <jump|action|grab ...> [<seconds>|<steps>s]" : "ctl press <jump|action|grab ...> [for <seconds>|<steps>s]";
            var f = new CtlFrame();
            var names = new List<string>();
            int i = 2;
            for (; i < a.Count; i++)
            {
                string b = Button(a[i]);
                if (b == null) break;
                if (b == "jump") f.jump = true; else f.action = true;
                names.Add(a[i].ToLowerInvariant());
            }
            if (names.Count == 0) return Usage(p, usage, i < a.Count ? "'" + a[i] + "' is not a button" : "name a button");
            string desc = string.Join("+", names.ToArray());
            if (!tap) return Rest(a, p, i, usage, "press " + desc, f);
            int steps;
            bool has;
            string err;
            if (!OptDuration(a, ref i, out steps, out has, out err)) return Usage(p, usage, err);
            if (i < a.Count) return Usage(p, usage, "unexpected '" + a[i] + "'");
            if (has) return Timed(p, "tap " + desc, f, steps);
            return Tap(p, "tap " + desc, f);
        }

        public static bool Direction(string w, out double x, out double y)
        {
            x = 0;
            y = 0;
            switch (w)
            {
                case "ul": case "upleft": w = "up-left"; break;
                case "ur": case "upright": w = "up-right"; break;
                case "dl": case "downleft": w = "down-left"; break;
                case "dr": case "downright": w = "down-right"; break;
            }
            var parts = (w ?? "").Split('-');
            if (parts.Length > 2) return false;
            bool h = false, v = false;
            foreach (var s in parts)
            {
                switch (s)
                {
                    case "left": if (h) return false; h = true; x = -1; break;
                    case "right": if (h) return false; h = true; x = 1; break;
                    case "up": if (v) return false; v = true; y = 1; break;
                    case "down": if (v) return false; v = true; y = -1; break;
                    default: return false;
                }
            }
            Clamp(ref x, ref y);
            return true;
        }

        const string UHold = "ctl hold <left|right|up|down|stick <x> <y>|jump|action|grab ...>";

        static CtlPlan Hold(List<string> a, CtlPlan p)
        {
            var f = new CtlFrame();
            var parts = new List<string>();
            double sx = 0, sy = 0;
            bool dir = false;
            for (int i = 2; i < a.Count; i++)
            {
                string w = a[i].ToLowerInvariant();
                double dx, dy;
                if (w == "stick")
                {
                    double x, y;
                    if (i + 2 >= a.Count || !Num(a[i + 1], out x) || !Num(a[i + 2], out y)) return Usage(p, UHold, "stick needs x and y");
                    string bad = StickError(x, y);
                    if (bad != null) return Usage(p, UHold, bad);
                    f.stick = true;
                    f.x = (float)x;
                    f.y = (float)y;
                    parts.Add("stick " + F(x) + "," + F(y));
                    i += 2;
                    continue;
                }
                if (Direction(w, out dx, out dy)) { sx += dx; sy += dy; dir = true; parts.Add(w); continue; }
                string b = Button(w);
                if (b == null) return Usage(p, UHold, "'" + a[i] + "' is not a direction or a button");
                if (b == "jump") f.jump = true; else f.action = true;
                parts.Add(w);
            }
            if (parts.Count == 0) return Usage(p, UHold, "name what to hold");
            if (dir)
            {
                if (f.stick) return Usage(p, UHold, "give either stick <x> <y> or directions");
                Clamp(ref sx, ref sy);
                f.stick = true;
                f.x = (float)sx;
                f.y = (float)sy;
            }
            return Held(p, string.Join(" ", parts.ToArray()), f);
        }

        const string URelease = "ctl release [all | stick|jump|action|grab ...]";

        static CtlPlan Release(List<string> a, CtlPlan p)
        {
            p.kind = CtlKind.Release;
            if (a.Count == 2 || (a.Count == 3 && a[2].ToLowerInvariant() == "all"))
            {
                p.releaseAll = true;
                p.what = "release all";
                return p;
            }
            var parts = new List<string>();
            for (int i = 2; i < a.Count; i++)
            {
                string w = a[i].ToLowerInvariant();
                double dx, dy;
                if (w == "stick" || Direction(w, out dx, out dy)) { p.relStick = true; if (!parts.Contains("stick")) parts.Add("stick"); continue; }
                string b = Button(w);
                if (b == null) return Usage(p, URelease, "'" + a[i] + "' is not stick or a button");
                if (b == "jump") p.relJump = true; else p.relAction = true;
                if (!parts.Contains(b)) parts.Add(b);
            }
            p.what = "release " + string.Join(" ", parts.ToArray());
            return p;
        }

        static CtlPlan Move(List<string> a, CtlPlan p, float mag, string verb)
        {
            string usage = "ctl " + verb + " left|right [<seconds>|<steps>s]";
            if (a.Count < 3) return Usage(p, usage, null);
            string d = a[2].ToLowerInvariant();
            if (d != "left" && d != "right") return Usage(p, usage, "'" + a[2] + "' is not left or right");
            return Rest(a, p, 3, usage, verb + " " + d, new CtlFrame { stick = true, x = d == "left" ? -mag : mag });
        }

        const string UJump = "ctl jump [left|right] [<seconds>|<steps>s]";

        static CtlPlan Jump(List<string> a, CtlPlan p)
        {
            int i = 2;
            float dir = 0;
            if (i < a.Count)
            {
                string d = a[i].ToLowerInvariant();
                if (d == "left") { dir = -1; i++; }
                else if (d == "right") { dir = 1; i++; }
            }
            int steps;
            bool has;
            string err;
            if (!OptDuration(a, ref i, out steps, out has, out err)) return Usage(p, UJump, err);
            if (i < a.Count) return Usage(p, UJump, "unexpected '" + a[i] + "'");
            p.kind = CtlKind.Input;
            if (dir == 0) return has ? Timed(p, "jump", new CtlFrame { jump = true }, steps) : Tap(p, "jump", new CtlFrame { jump = true });
            int total = has ? steps : JumpRunSteps;
            int press = Math.Min(TapPress, total);
            p.frames.Add(new CtlFrame { stick = true, x = dir, jump = true, steps = press });
            if (total > press) p.frames.Add(new CtlFrame { stick = true, x = dir, steps = total - press });
            p.steps = total;
            p.what = "jump " + (dir < 0 ? "left" : "right") + " for " + StepsText(total);
            return p;
        }

        static CtlPlan Grab(List<string> a, CtlPlan p)
        {
            string w = a.Count == 3 ? a[2].ToLowerInvariant() : "";
            if (w == "on") return Held(p, "grab", new CtlFrame { action = true });
            if (w == "off")
            {
                p.kind = CtlKind.Release;
                p.relAction = true;
                p.what = "release grab";
                return p;
            }
            return Usage(p, "ctl grab on|off", null);
        }

        const string USwim = "ctl swim <left|right|up|down|up-left|up-right|down-left|down-right> [<seconds>|<steps>s]";

        static CtlPlan Swim(List<string> a, CtlPlan p)
        {
            double x, y;
            if (a.Count < 3) return Usage(p, USwim, null);
            string d = a[2].ToLowerInvariant();
            if (!Direction(d, out x, out y)) return Usage(p, USwim, "'" + a[2] + "' is not a direction");
            return Rest(a, p, 3, USwim, "swim " + d, new CtlFrame { stick = true, x = (float)x, y = (float)y });
        }

        static bool Gait(List<string> a, ref int i, ref string gait)
        {
            if (i >= a.Count) return false;
            string g = a[i].ToLowerInvariant();
            if (g != "walk" && g != "run") return false;
            gait = g;
            i++;
            return true;
        }

        const string UGoto = "ctl goto <x> [<y>] [walk|run]";

        static CtlPlan Goto(List<string> a, CtlPlan p)
        {
            double x, y = 0;
            if (a.Count < 3 || !Num(a[2], out x)) return Usage(p, UGoto, null);
            int i = 3;
            bool hasY = i < a.Count && Num(a[i], out y);
            if (hasY) i++;
            string gait = "run";
            Gait(a, ref i, ref gait);
            if (i < a.Count) return Usage(p, UGoto, "unexpected '" + a[i] + "'");
            var o = new Json.Obj();
            o["x"] = x;
            if (hasY) o["y"] = y;
            o["gait"] = gait;
            o["stop"] = "exact";
            o["max_steps"] = (double)GotoSteps;
            o["observe"] = "none";
            p.kind = CtlKind.Tool;
            p.tool = "move_to";
            p.op = "goto";
            p.async = true;
            p.args = o;
            p.what = "goto x=" + F(x) + (hasY ? " y=" + F(y) : "") + (gait == "walk" ? " walking" : "");
            return p;
        }

        const string UStopAt = "ctl stopat <x> [walk|run]";

        static CtlPlan StopAt(List<string> a, CtlPlan p)
        {
            double x;
            if (a.Count < 3 || !Num(a[2], out x)) return Usage(p, UStopAt, null);
            int i = 3;
            string gait = "run";
            Gait(a, ref i, ref gait);
            if (i < a.Count) return Usage(p, UStopAt, "unexpected '" + a[i] + "'");
            p.kind = CtlKind.StopAt;
            p.target = x;
            p.gait = gait;
            p.steps = StopAtSteps;
            p.what = gait + " to x=" + F(x) + " and stop";
            return p;
        }

        static CtlPlan Seq(List<string> a, CtlPlan p)
        {
            if (a.Count < 3) return Usage(p, "ctl seq \"<script>\"", "e.g. ctl seq \"R1.2 J R0.4+J G1 W0.5 L0.3\"");
            string script = string.Join(" ", a.GetRange(2, a.Count - 2).ToArray());
            int total, segments;
            string err;
            if (!ParseSeq(script, p.frames, p.warnings, out total, out segments, out err))
            {
                p.frames.Clear();
                p.warnings.Clear();
                return Usage(p, "ctl seq \"<script>\"", err);
            }
            p.kind = CtlKind.Input;
            p.steps = total;
            p.what = "seq \"" + Clip(script, 60) + "\" (" + segments + (segments == 1 ? " segment, " : " segments, ") + StepsText(total) + ")";
            return p;
        }

        const string UTrack = "ctl track rec [name] | stop | save <name> [overwrite] | play [name] [speed <k>] [freeze] [correct|nocorrect] [free] | list | info [name] | status | load <name> | trim <from> [<to>]";

        static CtlPlan Track(List<string> a, CtlPlan p)
        {
            string op = a.Count > 2 ? a[2].ToLowerInvariant() : "status";
            var o = new Json.Obj();
            int i = 3;
            switch (op)
            {
                case "rec":
                case "record":
                case "start":
                    o["op"] = "start";
                    if (i < a.Count) o["name"] = a[i++];
                    break;
                case "stop":
                case "list":
                case "status":
                    o["op"] = op;
                    break;
                case "info":
                    o["op"] = "info";
                    if (i < a.Count) o["name"] = a[i++];
                    break;
                case "save":
                case "load":
                    if (i >= a.Count) return Usage(p, "ctl track " + op + " <name>" + (op == "save" ? " [overwrite]" : ""), null);
                    o["op"] = op;
                    o["name"] = a[i++];
                    if (op == "save" && i < a.Count && a[i].ToLowerInvariant() == "overwrite") { o["overwrite"] = true; i++; }
                    break;
                case "trim":
                    {
                        double from, to = -1;
                        if (i >= a.Count || !Num(a[i], out from) || from < 0 || from != Math.Floor(from)) return Usage(p, "ctl track trim <from> [<to>]", "steps are whole numbers");
                        i++;
                        if (i < a.Count && Num(a[i], out to)) i++;
                        if (to != Math.Floor(to) || to < -1) return Usage(p, "ctl track trim <from> [<to>]", "to is a step or -1 (the end)");
                        o["op"] = "trim";
                        o["from"] = from;
                        o["to"] = to;
                        break;
                    }
                case "play":
                    o["op"] = "play";
                    if (i < a.Count && !PlayWord(a[i])) o["name"] = a[i++];
                    while (i < a.Count && PlayWord(a[i]))
                    {
                        string w = a[i++].ToLowerInvariant();
                        if (w == "freeze") o["then"] = "freeze";
                        else if (w == "nocorrect") o["correct"] = false;
                        else if (w == "correct") o["correct"] = true;
                        else if (w == "free") o["lockstep"] = false;
                        else
                        {
                            double k;
                            if (i >= a.Count || !Num(a[i], out k) || k < 0.05 || k > 4) return Usage(p, "ctl track play [name] [speed <0.05..4>] [freeze] [correct|nocorrect]", null);
                            o["speed"] = k;
                            i++;
                        }
                    }
                    o["wait"] = false;
                    o["observe"] = "none";
                    p.async = true;
                    break;
                default:
                    return Usage(p, UTrack, "unknown track op '" + a[2] + "'");
            }
            if (i < a.Count) return Usage(p, UTrack, "unexpected '" + a[i] + "'");
            p.kind = CtlKind.Tool;
            p.tool = "input_track";
            p.op = o.Str("op");
            p.args = o;
            string name = o.Str("name", null);
            p.what = "track " + (op == "start" || op == "record" ? "rec" : op) + (name != null ? " " + name : "");
            return p;
        }

        static bool PlayWord(string s)
        {
            string w = s.ToLowerInvariant();
            return w == "speed" || w == "freeze" || w == "nocorrect" || w == "correct" || w == "free";
        }

        const string UTime = "ctl time [state] | freeze | run [<speed>] | step <n> | speed <k>";

        static CtlPlan Time(List<string> a, CtlPlan p)
        {
            string op = a.Count > 2 ? a[2].ToLowerInvariant() : "state";
            var o = new Json.Obj();
            int i = 3;
            double k;
            switch (op)
            {
                case "state":
                    o["op"] = "state";
                    break;
                case "freeze":
                case "step_mode":
                    o["op"] = "step_mode";
                    break;
                case "run":
                case "realtime":
                    o["op"] = "realtime";
                    if (i < a.Count && Num(a[i], out k))
                    {
                        if (k < 0.05 || k > 4) return Usage(p, UTime, "speed is 0.05..4");
                        o["speed"] = k;
                        i++;
                    }
                    break;
                case "step":
                    {
                        int n = 1;
                        if (i < a.Count)
                        {
                            if (!int.TryParse(a[i], NumberStyles.None, IC, out n) || n < 1 || n > MaxTimeStep) return Usage(p, "ctl time step <1.." + MaxTimeStep + ">", "logic steps, 60 per second");
                            i++;
                        }
                        o["op"] = "step";
                        o["steps"] = (double)n;
                        o["observe"] = "none";
                        p.async = true;
                        break;
                    }
                case "speed":
                    if (i >= a.Count || !Num(a[i], out k) || k < 0.05 || k > 4) return Usage(p, "ctl time speed <0.05..4>", null);
                    o["op"] = "speed";
                    o["speed"] = k;
                    i++;
                    break;
                default:
                    return Usage(p, UTime, "unknown time op '" + a[2] + "'");
            }
            if (i < a.Count) return Usage(p, UTime, "unexpected '" + a[i] + "'");
            p.kind = CtlKind.Tool;
            p.tool = "time";
            p.op = o.Str("op");
            p.args = o;
            p.what = "time " + op + (p.op == "step" ? " " + F(o.Num("steps")) : p.op == "speed" ? " " + F(o.Num("speed")) : "");
            return p;
        }

        static CtlPlan Mode(List<string> a, CtlPlan p)
        {
            string m = a.Count == 3 ? a[2].ToLowerInvariant() : "";
            if (m != "agent" && m != "mix" && m != "human") return Usage(p, "ctl mode agent|mix|human", "human gives the pad and keyboard back");
            p.kind = CtlKind.Mode;
            p.mode = m;
            p.what = "mode " + m;
            return p;
        }

        static CtlPlan Job(List<string> a, CtlPlan p)
        {
            p.kind = CtlKind.Job;
            if (a.Count > 2) p.jobId = a[2];
            return Extra(a, p, 3, "ctl job [jN]");
        }

        // ---------------------------------------------------------------- reply texts
        public static string F(double v) { return v.ToString("0.##", IC); }

        public static string Clip(string s, int n)
        {
            s = s ?? "";
            return s.Length > n ? s.Substring(0, n - 3) + "..." : s;
        }

        static string OneLine(string s) { return (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); }

        // the first word of an error reply is what the MCP command tool reads as the error code (HostText.Classify)
        public static string ErrorLine(string code, string message, string hint)
        {
            string prefix;
            switch (code ?? "")
            {
                case "busy": prefix = "busy: "; break;
                case "bad_args": prefix = "usage: "; break;
                case "not_found":
                case "stale_id": prefix = "not found: "; break;
                case "refused":
                case "unsafe":
                case "confirm_required":
                case "unauthorized": prefix = "refused: "; break;
                case "cancelled": prefix = "cancelled: "; break;
                case "not_ready": prefix = "failed: not ready: "; break;
                default: prefix = "failed: " + (string.IsNullOrEmpty(code) ? "" : code + ": "); break;
            }
            string m = OneLine(message);
            string bare = prefix.Substring(0, prefix.IndexOf(':') + 1);
            if (m.StartsWith(bare, StringComparison.OrdinalIgnoreCase)) m = m.Substring(bare.Length).TrimStart();
            string h = OneLine(hint);
            return prefix + m + (h.Length > 0 ? " (" + h + ")" : "");
        }

        public static string Outcome(string stop, bool stopAt)
        {
            switch (stop ?? "")
            {
                case "steps": return stopAt ? "not_reached" : "done";
                case "condition":
                case "": return "done";
            }
            return stop == "died" || stop == "loading" || stop == "cancelled" || stop == "timeout" || stop == "stalled" ? stop : "done";
        }

        public static string RunLine(string outcome, string what, int steps, int requested, string detail, List<string> warnings)
        {
            var sb = new StringBuilder();
            sb.Append(outcome).Append(": ").Append(what);
            bool full = outcome == "done" && steps >= requested;
            if (!full) sb.Append(": ").Append(steps).Append(" of ").Append(requested).Append(" steps run");
            string d = OneLine(detail);
            if (d.Length > 0 && d != steps + " steps") sb.Append(" (").Append(d).Append(')');
            if (warnings != null && warnings.Count > 0) sb.Append(" [").Append(Clip(OneLine(warnings[0]), 140)).Append(']');
            return sb.ToString();
        }

        public static string Clock(bool stepMode, bool frozen, double speed)
        {
            if (stepMode) return frozen ? "step frozen" : "step running";
            if (frozen) return "realtime paused";
            return "realtime" + (Math.Abs(speed - 1) > 1e-6 ? " x" + F(speed) : "");
        }

        public static string Input(string mode, bool hasHeld, InputFrame held, int queued)
        {
            var sb = new StringBuilder(string.IsNullOrEmpty(mode) ? "human" : mode);
            if (hasHeld && !IsNeutral(held)) sb.Append(" held=").Append(HeldText(held).Replace(" + ", "+").Replace("stick ", "stick:"));
            if (queued > 0) sb.Append(" queued=").Append(queued);
            return sb.ToString();
        }

        // the second line of every reply: the controlled body (Perception.Body, concise), the clock and the input
        public static string Body(Json.Obj b, string reason, string clock, string input)
        {
            var sb = new StringBuilder("body: ");
            if (b == null) sb.Append("none (").Append(string.IsNullOrEmpty(reason) ? "no controlled body" : reason).Append(')');
            else
            {
                sb.Append(b.Str("kind", "?"));
                string id = b.Str("id", "");
                if (id.Length > 0) sb.Append(' ').Append(id);
                sb.Append(" x=").Append(F(b.Num("x"))).Append(" y=").Append(F(b.Num("y")));
                sb.Append(" vx=").Append(F(b.Num("vx"))).Append(" vy=").Append(F(b.Num("vy")));
                sb.Append(" state=").Append(b.Str("state", "?"));
                sb.Append(b.Bool("grounded") ? " grounded" : " airborne");
                if (b.Bool("in_water")) sb.Append(" in_water");
                string grab = b.Str("grab", null);
                if (string.IsNullOrEmpty(grab) || grab == "none") grab = b.Str("holding_name", null);
                sb.Append(" grab=").Append(string.IsNullOrEmpty(grab) || grab == "none" ? "-" : Clip(grab, 40));
                sb.Append(" stick_forced=").Append(b.ContainsKey("stick_forced") ? (b.Bool("stick_forced") ? "yes" : "no") : "?");
                string fb = b.Str("forced_by", null);
                if (!string.IsNullOrEmpty(fb)) sb.Append(" forced_by=\"").Append(Clip(OneLine(fb), 60)).Append('"');
                if (b.Bool("dead")) sb.Append(" DEAD");
                else if (b.ContainsKey("controllable") && !b.Bool("controllable")) sb.Append(" not_controllable");
            }
            if (!string.IsNullOrEmpty(clock)) sb.Append(" | clock=").Append(clock);
            if (!string.IsNullOrEmpty(input)) sb.Append(" | input=").Append(input);
            return sb.ToString();
        }

        static string FirstWarning(Json.Obj s)
        {
            var w = s != null ? s["warnings"] as List<object> : null;
            if (w == null) return "";
            foreach (var x in w) { var t = x as string; if (!string.IsNullOrEmpty(t)) return " [" + Clip(OneLine(t), 140) + "]"; }
            return "";
        }

        public static string ToolLine(string tool, string op, Json.Obj s, bool isError)
        {
            if (s == null) return "failed: " + tool + " returned nothing";
            if (isError || !s.Bool("ok", true))
            {
                var e = s["error"] as Json.Obj;
                return ErrorLine(e != null ? e.Str("code", "internal") : "internal", e != null ? e.Str("message", "") : "failed", e != null ? e.Str("hint", null) : null);
            }
            switch (tool)
            {
                case "input_track": return TrackLine(op, s) + FirstWarning(s);
                case "time": return TimeLine(op, s) + FirstWarning(s);
                case "move_to": return MoveLine(s) + FirstWarning(s);
            }
            return "ok: " + tool;
        }

        static string TrackBrief(Json.Obj t)
        {
            if (t == null) return "?";
            return t.Str("name", "?") + " (" + F(t.Num("steps")) + " steps, " + F(t.Num("seconds", t.Num("steps") / StepsPerSecond)) + " s" +
                   (t.Str("body", "").Length > 0 ? ", " + t.Str("body", "") : "") + (t.ContainsKey("subscene") ? ", " + F(t.Num("subscene", -1)) + ":" + F(t.Num("savepoint", -1)) : "") + ")";
        }

        static string TrackLine(string op, Json.Obj s)
        {
            var t = s["track"] as Json.Obj;
            switch (op)
            {
                case "start": return "ok: recording input track " + (t != null ? t.Str("name", "?") : "?") + " from the next logic step; ctl track stop ends it";
                case "stop":
                    if (s.Str("outcome", "") == "stopped") return "ok: recording stopped: " + TrackBrief(t) + "; ctl track save <name> keeps it";
                    return "ok: nothing to stop (no recording or playback runs)";
                case "save": return "ok: saved " + TrackBrief(t) + " to " + s.Str("file", "?");
                case "load": return "ok: loaded " + TrackBrief(t);
                case "trim": return "ok: trimmed to " + TrackBrief(t) + "; ctl track save <name> keeps it";
                case "info":
                    return "ok: " + TrackBrief(t) + (t != null && t.Num("forced_steps") > 0 ? ", forced " + F(t.Num("forced_steps")) + " steps" : "") +
                           (t != null && t.ContainsKey("saved") && !t.Bool("saved") ? ", not saved" : "");
                case "list":
                    {
                        var rows = s["tracks"] as List<object> ?? new List<object>();
                        var names = new List<string>();
                        foreach (var r in rows) { if (names.Count >= 8) { names.Add("..."); break; } names.Add(TrackBrief(r as Json.Obj)); }
                        return "ok: " + rows.Count + (rows.Count == 1 ? " track" : " tracks") + (names.Count > 0 ? ": " + string.Join(", ", names.ToArray()) : "") + " in " + s.Str("file", "?");
                    }
                case "status":
                    {
                        if (s.Bool("recording")) return "ok: recording " + TrackBrief(t);
                        var pb = s["playback"] as Json.Obj;
                        if (s.Bool("playing") && pb != null) return "ok: playing " + pb.Str("name", "?") + " step " + F(pb.Num("step")) + "/" + F(pb.Num("of")) + (s["job"] is Json.Obj ? " job=" + ((Json.Obj)s["job"]).Str("id", "?") : "");
                        return "ok: no recording or playback" + (t != null ? "; in memory " + TrackBrief(t) : "");
                    }
            }
            return "ok: track " + op + ": " + s.Str("outcome", "done");
        }

        static string TimeLine(string op, Json.Obj s)
        {
            string clock = Clock(s.Str("mode", "") == "step", s.Bool("frozen"), s.Num("speed", 1));
            switch (op)
            {
                case "step_mode": return "ok: step mode (" + clock + "): the game is frozen between commands; timed ctl commands, ctl time step <n> and MCP actions advance it";
                case "realtime": return "ok: realtime at speed " + F(s.Num("speed", 1)) + " (" + clock + ")";
                case "speed": return "ok: speed " + F(s.Num("speed", 1)) + (s.Str("mode", "") == "step" ? " (stored; it applies in realtime: ctl time run)" : "");
                case "step":
                    return "done: time step: " + F(s.Num("steps_run")) + " steps (" + s.Str("stop", "steps") + (s.Str("detail", "").Length > 0 ? ": " + OneLine(s.Str("detail", "")) : "") + "), " + clock;
            }
            return "ok: clock " + clock + " step " + F(s.Num("step")) + (s.Bool("loading") ? " loading" : "") + (s.Bool("paused") ? " paused" : "") + (s.Bool("recording") ? " (a scene recorder owns the frame pacing)" : "");
        }

        static string MoveLine(Json.Obj s)
        {
            string outcome = s.Str("outcome", "done");
            var sb = new StringBuilder();
            sb.Append(outcome).Append(": goto after ").Append(F(s.Num("steps"))).Append(" steps, at x=").Append(F(s.Num("x"))).Append(" y=").Append(F(s.Num("y")));
            if (s.ContainsKey("distance_left")) sb.Append(", ").Append(F(s.Num("distance_left"))).Append(" m left");
            string reason = s.Str("reason", "");
            if (reason.Length > 0) sb.Append(" (").Append(OneLine(reason)).Append(')');
            return sb.ToString();
        }

        // the reply of ctl job for a finished job: the job's own line, or a summary of a tool result (track play)
        public static string JobText(string state, string what, Json.Obj r)
        {
            if (r != null && r["line"] is string) return (string)r["line"];
            var e = r != null ? r["error"] as Json.Obj : null;
            if (e != null) return ErrorLine(state == "cancelled" ? "cancelled" : e.Str("code", "internal"), e.Str("message", ""), e.Str("hint", null));
            string outcome = r != null ? r.Str("outcome", state) : state;
            var sb = new StringBuilder();
            sb.Append(outcome).Append(": ").Append(string.IsNullOrEmpty(what) ? "job" : what);
            if (r != null)
            {
                string sr = OneLine(r.Str("stop_reason", ""));
                if (sr.Length > 0) sb.Append(" (").Append(sr).Append(')');
                var pb = r["playback"] as Json.Obj;
                if (pb != null) sb.Append(": step ").Append(F(pb.Num("step"))).Append('/').Append(F(pb.Num("of"))).Append(", max drift ").Append(F(pb.Num("max_drift_m"))).Append(" m");
            }
            return sb.ToString();
        }
    }
}
