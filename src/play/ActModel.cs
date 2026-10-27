using System;
using System.Collections.Generic;
using System.Globalization;

namespace InsideDev.Play
{
    // ActModel.cs (owner INPUT): Unity-free logic of act and interact, compiled into the offline tests too
    // (qa/mcp/INPUT). ActBody is one main-thread reading of the controlled body (ActProbe fills it from the game);
    // ActInputs parses and lints the act inputs; ActStops decides the stop_on / watch / hazard stops from two
    // consecutive snapshots; ActStateNames shortens game class names the way observe does (BoyGrabPlateauState -> GrabPlateau).
    internal sealed class ActBody
    {
        public bool has, dead, grounded, inWater, atSurface, holding, mindControlling, obstructed;
        public bool loading, playing, inputEnabled, subOnSurface, subBoosting;
        public string kind = "none", state = "", special = "None", device = "";
        public float x, y, z, vx, vy, facing = 1f, deviceValue = float.NaN, extraDeadZone;
        public float edgeLeft = 1e6f, edgeRight = 1e6f;
        public int sub = -1, sp = -1;
        public long step;

        public string StateKey { get { return special != "None" && special != "" ? special + "/" + state : state; } }

        public bool Airborne
        {
            get
            {
                if (!has || grounded || inWater) return false;
                if (kind == "boy" || kind == "dude") return state == "Jump";
                return kind == "huddle";
            }
        }

        public float NearestEdge { get { return Math.Min(edgeLeft, edgeRight); } }
    }

    internal sealed class ActWatch
    {
        public string id = "";
        public bool exists, active;
        public float x, y, z, angle;
        public string fsm = "";
    }

    internal sealed class ActHazard
    {
        public string id = "", kind = "";
        public bool threat;
        public float eta = -1f, dist;
    }

    internal static class ActStateNames
    {
        public static string Short(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return "";
            string s = typeName;
            int dot = s.LastIndexOf('.');
            if (dot >= 0) s = s.Substring(dot + 1);
            if (s.StartsWith("Boy", StringComparison.Ordinal) && s.Length > 3) s = s.Substring(3);
            else if (s.StartsWith("Huddle", StringComparison.Ordinal) && s.Length > 6) s = s.Substring(6);
            if (s.EndsWith("State", StringComparison.Ordinal) && s.Length > 5) s = s.Substring(0, s.Length - 5);
            return s;
        }
    }

    internal static class ActInputs
    {
        public const int MaxSteps = 600;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static List<InputFrame> Parse(List<object> inputs, out int totalSteps, out string error)
        {
            var frames = new List<InputFrame>();
            totalSteps = 0;
            error = null;
            if (inputs == null) return frames;
            long total = 0;
            for (int i = 0; i < inputs.Count; i++)
            {
                string at = "inputs[" + i + "]";
                var o = inputs[i] as Json.Obj;
                if (o == null) { error = at + ": expected an object {stick, jump, action, steps}"; return frames; }
                float x = 0f, y = 0f;
                if (o.ContainsKey("stick") && o["stick"] != null)
                {
                    var l = o["stick"] as List<object>;
                    if (l == null || l.Count != 2 || !(l[0] is double) || !(l[1] is double)) { error = at + ".stick: expected 2 numbers [x, y]"; return frames; }
                    double dx = (double)l[0], dy = (double)l[1];
                    if (double.IsNaN(dx) || double.IsNaN(dy) || dx < -1 || dx > 1 || dy < -1 || dy > 1) { error = at + ".stick: each value must be in -1..1"; return frames; }
                    x = (float)dx; y = (float)dy;
                    if (AgentQueue.InDeadzoneBand(x, y))
                    {
                        error = at + ".stick: magnitude " + AgentQueue.Magnitude(x, y).ToString("0.##", IC) + " is inside the 0.3 deadzone (the game reads 0): send 0, or 0.3..1 (above 0.8 runs)";
                        return frames;
                    }
                }
                bool jump, action;
                if (!Flag(o, "jump", out jump)) { error = at + ".jump: expected true or false"; return frames; }
                if (!Flag(o, "action", out action)) { error = at + ".action: expected true or false"; return frames; }
                int steps = 1;
                if (o.ContainsKey("steps") && o["steps"] != null)
                {
                    if (!(o["steps"] is double)) { error = at + ".steps: expected an integer 1..600"; return frames; }
                    double d = (double)o["steps"];
                    if (d != Math.Floor(d) || d < 1 || d > MaxSteps) { error = at + ".steps: expected an integer 1..600"; return frames; }
                    steps = (int)d;
                }
                total += steps;
                frames.Add(AgentQueue.Shape(new InputFrame { x = x, y = y, jump = jump, action = action, steps = steps }));
            }
            if (total > MaxSteps)
            {
                error = "inputs: " + total + " steps in total, at most " + MaxSteps + " per call (10 s): split into several act calls, or use hold";
                return frames;
            }
            totalSteps = (int)total;
            return frames;
        }

        static bool Flag(Json.Obj o, string k, out bool v)
        {
            v = false;
            if (!o.ContainsKey(k) || o[k] == null) return true;
            if (!(o[k] is bool)) return false;
            v = (bool)o[k];
            return true;
        }

        public static bool AnyActionOff(List<InputFrame> frames)
        {
            if (frames == null) return false;
            foreach (var f in frames) if (!f.action) return true;
            return false;
        }

        // wall-clock ms an action of n logic steps needs after a when wait (real-time pacing: 60 steps per second, plus
        // a quarter, plus 3 s for the reply and the observation)
        public static int ReserveMs(int steps) { return 3000 + (int)Math.Ceiling(Math.Max(0, steps) * 1000.0 / 60.0 * 1.25); }

        public static List<string> Lint(List<InputFrame> frames, bool jumpHeldBefore)
        {
            var w = new List<string>();
            bool prevJump = jumpHeldBefore;
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].jump && prevJump)
                    w.Add("inputs[" + i + "]: jump is still held from " + (i == 0 ? "the last step before this call" : "inputs[" + (i - 1) + "]") + ", so it is not a new press: put a segment with jump:false (1 step) in between");
                prevJump = frames[i].jump;
            }
            return w;
        }
    }

    internal static class ActStops
    {
        public static readonly string[] SnapStops = { "state_change", "grounded", "airborne", "checkpoint" };

        public static string Check(ICollection<string> stopOn, ActBody start, ActBody prev, ActBody now)
        {
            if (stopOn == null || now == null || prev == null || start == null) return null;
            if (stopOn.Contains("state_change") && prev.has && now.has && prev.StateKey != now.StateKey)
                return "state_change: " + prev.StateKey + " -> " + now.StateKey;
            if (stopOn.Contains("grounded") && prev.has && now.has && !prev.grounded && now.grounded)
                return "grounded";
            if (stopOn.Contains("airborne") && prev.has && now.has && !prev.Airborne && now.Airborne)
                return "airborne";
            if (stopOn.Contains("checkpoint") && now.sub >= 0 && (now.sub != start.sub || now.sp != start.sp))
                return "checkpoint: " + now.sub + ":" + now.sp;
            return null;
        }

        public static string Watch(ActWatch a, ActWatch b)
        {
            if (a == null || b == null) return null;
            if (a.exists != b.exists) return "watch " + a.id + ": " + (b.exists ? "appeared" : "destroyed");
            if (!b.exists) return null;
            if (a.active != b.active) return "watch " + a.id + ": " + (b.active ? "activated" : "deactivated");
            float dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z;
            if (dx * dx + dy * dy + dz * dz > 0.01f) return "watch " + a.id + ": moved " + Math.Sqrt(dx * dx + dy * dy + dz * dz).ToString("0.##", CultureInfo.InvariantCulture) + " m";
            float da = Math.Abs(b.angle - a.angle);
            if (da > 180f) da = 360f - da;
            if (da > 10f) return "watch " + a.id + ": rotated " + da.ToString("0", CultureInfo.InvariantCulture) + " deg";
            if (a.fsm != b.fsm) return "watch " + a.id + ": fsm " + a.fsm + " -> " + b.fsm;
            return null;
        }

        public static string Hazard(ICollection<string> threatsAtStart, List<ActHazard> now)
        {
            if (now == null) return null;
            foreach (var h in now)
            {
                if (h.threat && (threatsAtStart == null || !threatsAtStart.Contains(h.id))) return "hazard: " + h.id + " " + h.kind + " is a threat";
                if (h.eta >= 0f && h.eta <= 1f && (threatsAtStart == null || !threatsAtStart.Contains(h.id))) return "hazard: " + h.id + " " + h.kind + " can kill in " + h.eta.ToString("0.#", CultureInfo.InvariantCulture) + " s";
            }
            return null;
        }

        public static string Outcome(string runStop, bool allGated)
        {
            switch (runStop)
            {
                case "steps": return allGated ? "not_controllable" : "done";
                case "condition": return "stopped";
                case "died": return "died";
                case "loading": return "loading";
                case "cancelled": return "cancelled";
                case "timeout": return "timeout";
                case "stalled": return "stalled";
            }
            return "done";
        }

        public static string NotReadyOutcome(string reason)
        {
            string r = (reason ?? "").ToLowerInvariant();
            return r.StartsWith("load", StringComparison.Ordinal) ? "loading" : "not_controllable";
        }

        public static bool SoftNotReady(string reason)
        {
            string r = (reason ?? "").ToLowerInvariant();
            return r.Contains("cutscene") || r.Contains("scripted") || r.Contains("custom_anim") || r.Contains("customanim");
        }
    }
}
