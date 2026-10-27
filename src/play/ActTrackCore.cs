using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // ActTrackCore.cs (owner INPUT): the Unity-free half of input tracks (ActTrack.cs), compiled into the offline tests
    // too (qa/mcp/TRACK). TrackFrame is one logic step: relative step k, game time t, the stick and buttons that reached
    // the game, the controlled body's position and velocity before that step's logic, and flags (input gated, dead, input
    // forced by the game, huddle grabbing, no body). InputTrack appends frames with a strictly rising k (a repeated or
    // older LogicManager.stepCount still advances k by one; a jump leaves a gap), trims to a step range and sums itself
    // up. TrackFile is the file format of _mod/input_tracks/<name>.json: line 1 {"header":{...}, (op list reads only this
    // line), line 2 the column names, then one JSON array per frame. TrackCursor schedules playback the way Playdead's
    // Playback.InputRecorder.Replayer does: relative step j plays the last frame with k <= j (gaps hold the previous
    // input), and a correction target exists only on steps that were recorded (k == j). TrackCorrect is the per-step
    // blend of the InputRecorder.AdjustPosition pattern (lerp position and velocity toward the recorded body by gain),
    // in the x / y plane only, with a deadband and a per-step cap; DriftStats sums the drift up for the result.
    internal struct TrackFrame
    {
        public const int Gated = 1, Dead = 2, Forced = 4, Grabbing = 8, NoBody = 16;
        public int k, flags;
        public float t, sx, sy, px, py, pz, vx, vy, vz;
        public bool jump, action;

        public bool Has(int flag) { return (flags & flag) != 0; }
        public int Buttons { get { return (jump ? 1 : 0) | (action ? 2 : 0); } }
        public InputFrame Input { get { return new InputFrame { x = sx, y = sy, jump = jump, action = action, steps = 1 }; } }
    }

    internal sealed class TrackHeader
    {
        public const string Format = "insidedev.input_track";
        public const int Version = 1;
        public string name = "", created = "", body = "", checkpoint = "", clock = "", source = "recorded";
        public int subscene = -1, savepoint = -1, loadCount = -1, trimFrom = -1, trimTo = -1, trimOf = -1;
        public long startStep = -1;
        public double startTime;

        public TrackHeader Copy() { return (TrackHeader)MemberwiseClone(); }
    }

    internal sealed class InputTrack
    {
        public const int MaxFrames = 72000, MaxGap = 36000;
        public const double StepSeconds = 1.0 / 60.0;
        public TrackHeader header = new TrackHeader();
        public readonly List<TrackFrame> frames = new List<TrackFrame>();

        public int Count { get { return frames.Count; } }
        public int Steps { get { return frames.Count == 0 ? 0 : frames[frames.Count - 1].k + 1; } }
        public double Seconds { get { return Steps * StepSeconds; } }
        public bool Full { get { return frames.Count >= MaxFrames; } }

        // one fed step: abs = LogicManager.stepCount of the step, time = game time (s); false when the track is full
        public bool Append(long abs, double time, TrackFrame f)
        {
            if (Full) return false;
            if (frames.Count == 0)
            {
                header.startStep = abs;
                header.startTime = time;
                f.k = 0;
            }
            else
            {
                int prev = frames[frames.Count - 1].k;
                long rel = abs - header.startStep;
                f.k = rel > prev && rel - prev <= MaxGap ? (int)rel : prev + 1;
            }
            f.t = (float)(time - header.startTime);
            frames.Add(f);
            return true;
        }

        // the frames with from <= k <= to (to < 0 = the end), re-keyed so that the first kept frame is step 0
        public InputTrack Trim(int from, int to, out string error)
        {
            error = null;
            int last = Steps - 1;
            if (frames.Count == 0) { error = "trim: the track is empty"; return null; }
            if (from < 0) from = 0;
            if (to < 0 || to > last) to = last;
            if (from > to) { error = "trim: from " + from + " is after to " + to + " (the track has steps 0.." + last + ")"; return null; }
            var t = new InputTrack();
            t.header = header.Copy();
            int first = -1;
            float t0 = 0f;
            foreach (var f in frames)
            {
                if (f.k < from || f.k > to) continue;
                var g = f;
                if (first < 0) { first = f.k; t0 = f.t; }
                g.k -= first;
                g.t -= t0;
                t.frames.Add(g);
            }
            if (t.frames.Count == 0) { error = "trim: no recorded step in " + from + ".." + to + " (the track has steps 0.." + last + ")"; return null; }
            int baseK = header.trimFrom >= 0 ? header.trimFrom : 0;
            t.header.trimFrom = baseK + first;
            t.header.trimTo = baseK + first + t.Steps - 1;
            t.header.trimOf = header.trimOf >= 0 ? header.trimOf : Steps;
            t.header.startStep = header.startStep >= 0 ? header.startStep + first : -1;
            t.header.startTime = header.startTime + t0;
            t.header.source = "trimmed";
            return t;
        }

        // the info / status summary: counts, spans where the game forced the input, start / end, and up to maxSamples
        // rows [k, x, y, vx, vy, flags] spread evenly over the track (for choosing trim points)
        public Json.Obj Summary(int maxSamples)
        {
            var o = new Json.Obj();
            o["name"] = header.name;
            o["body"] = header.body;
            o["subscene"] = (double)header.subscene;
            o["savepoint"] = (double)header.savepoint;
            if (header.checkpoint.Length > 0) o["checkpoint"] = header.checkpoint;
            o["steps"] = (double)Steps;
            o["frames"] = (double)Count;
            o["seconds"] = Math.Round(Seconds, 2);
            if (header.created.Length > 0) o["created"] = header.created;
            o["source"] = header.source;
            if (header.clock.Length > 0) o["clock"] = header.clock;
            int gaps = 0, forced = 0, gated = 0, dead = 0, grab = 0, spanFrom = -1, prevK = -1;
            double path = 0, maxSpeed = 0;
            var spans = new List<object>();
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                if (i > 0)
                {
                    var p = frames[i - 1];
                    if (f.k != p.k + 1) gaps++;
                    double dx = f.px - p.px, dy = f.py - p.py;
                    path += Math.Sqrt(dx * dx + dy * dy);
                }
                maxSpeed = Math.Max(maxSpeed, Math.Sqrt((double)f.vx * f.vx + (double)f.vy * f.vy));
                if (f.Has(TrackFrame.Gated)) gated++;
                if (f.Has(TrackFrame.Dead)) dead++;
                if (f.Has(TrackFrame.Grabbing)) grab++;
                bool fo = f.Has(TrackFrame.Forced);
                if (fo) forced++;
                if (fo && spanFrom < 0) spanFrom = f.k;
                if (!fo && spanFrom >= 0) { AddSpan(spans, spanFrom, prevK); spanFrom = -1; }
                prevK = f.k;
            }
            if (spanFrom >= 0) AddSpan(spans, spanFrom, prevK);
            o["gaps"] = (double)gaps;
            o["forced_steps"] = (double)forced;
            o["gated_steps"] = (double)gated;
            o["dead_steps"] = (double)dead;
            o["grab_steps"] = (double)grab;
            if (spans.Count > 0) o["forced_spans"] = spans;
            if (frames.Count > 0)
            {
                o["start"] = Pos(frames[0]);
                o["end"] = Pos(frames[frames.Count - 1]);
            }
            o["path_m"] = Math.Round(path, 2);
            o["max_speed"] = Math.Round(maxSpeed, 2);
            if (header.trimOf >= 0)
            {
                var tr = new Json.Obj();
                tr["from"] = (double)header.trimFrom;
                tr["to"] = (double)header.trimTo;
                tr["of"] = (double)header.trimOf;
                o["trimmed"] = tr;
            }
            if (maxSamples > 0 && frames.Count > 0)
            {
                int every = Math.Max(1, (int)Math.Ceiling(Steps / (double)maxSamples));
                var rows = new List<object>();
                int nextK = 0;
                for (int i = 0; i < frames.Count && rows.Count < maxSamples; i++)
                {
                    var f = frames[i];
                    if (f.k < nextK) continue;
                    rows.Add(Row(f));
                    nextK = f.k + every;
                }
                o["sample_columns"] = new List<object> { "k", "x", "y", "vx", "vy", "flags" };
                o["samples"] = rows;
            }
            return o;
        }

        static void AddSpan(List<object> spans, int from, int to)
        {
            if (spans.Count < 8) spans.Add(new List<object> { (double)from, (double)to });
        }

        static List<object> Pos(TrackFrame f)
        {
            return new List<object> { Math.Round(f.px, 2), Math.Round(f.py, 2), Math.Round(f.pz, 2) };
        }

        static List<object> Row(TrackFrame f)
        {
            return new List<object> { (double)f.k, Math.Round(f.px, 2), Math.Round(f.py, 2), Math.Round(f.vx, 2), Math.Round(f.vy, 2), (double)f.flags };
        }
    }

    internal static class TrackFile
    {
        public const string Extension = ".json";
        public const int MaxName = 64;
        public static readonly string[] Columns = { "k", "t", "sx", "sy", "b", "px", "py", "pz", "vx", "vy", "vz", "f" };
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        const string HeaderStart = "{\"header\":";

        // null when name can be a file name: 1..64 of letters, digits, '_', '-', '.', not starting with '.'
        public static string NameError(string name)
        {
            if (string.IsNullOrEmpty(name)) return "name: empty";
            if (name.Length > MaxName) return "name: longer than " + MaxName + " characters";
            if (name[0] == '.') return "name: must not start with '.'";
            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
                if (!ok) return "name: only letters, digits, '_', '-' and '.' (got '" + c + "')";
            }
            return null;
        }

        public static Json.Obj HeaderJson(InputTrack t)
        {
            var h = t.header;
            var o = new Json.Obj();
            o["format"] = TrackHeader.Format;
            o["version"] = (double)TrackHeader.Version;
            o["name"] = h.name;
            o["created"] = h.created;
            o["body"] = h.body;
            o["subscene"] = (double)h.subscene;
            o["savepoint"] = (double)h.savepoint;
            o["checkpoint"] = h.checkpoint;
            o["clock"] = h.clock;
            o["source"] = h.source;
            o["load_count"] = (double)h.loadCount;
            o["start_step"] = (double)h.startStep;
            o["start_time"] = Math.Round(h.startTime, 4);
            o["step_s"] = Math.Round(InputTrack.StepSeconds, 6);
            o["steps"] = (double)t.Steps;
            o["frames"] = (double)t.Count;
            o["seconds"] = Math.Round(t.Seconds, 3);
            if (h.trimOf >= 0)
            {
                var tr = new Json.Obj();
                tr["from"] = (double)h.trimFrom;
                tr["to"] = (double)h.trimTo;
                tr["of"] = (double)h.trimOf;
                o["trimmed"] = tr;
            }
            return o;
        }

        public static string Write(InputTrack t)
        {
            var sb = new StringBuilder(64 + t.Count * 80);
            sb.Append(HeaderStart).Append(JsonOut.Write(HeaderJson(t))).Append(",\n");
            sb.Append("\"columns\":").Append(JsonOut.Write(new List<object>(Columns))).Append(",\n");
            sb.Append("\"frames\":[\n");
            for (int i = 0; i < t.Count; i++)
            {
                var f = t.frames[i];
                sb.Append('[').Append(f.k.ToString(IC)).Append(',').Append(D(f.t, "0.###")).Append(',')
                  .Append(f.sx.ToString("R", IC)).Append(',').Append(f.sy.ToString("R", IC)).Append(',').Append(f.Buttons.ToString(IC)).Append(',')
                  .Append(D(f.px, "0.###")).Append(',').Append(D(f.py, "0.###")).Append(',').Append(D(f.pz, "0.###")).Append(',')
                  .Append(D(f.vx, "0.###")).Append(',').Append(D(f.vy, "0.###")).Append(',').Append(D(f.vz, "0.###")).Append(',')
                  .Append(f.flags.ToString(IC)).Append(']');
                sb.Append(i < t.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("]}\n");
            return sb.ToString();
        }

        static string D(float v, string fmt) { return ((double)v).ToString(fmt, IC); }

        // the header of a track file from its first line, or null when the line is not one
        public static Json.Obj HeaderOfLine(string line)
        {
            if (line == null) return null;
            line = line.Trim();
            if (!line.StartsWith(HeaderStart, StringComparison.Ordinal) || !line.EndsWith(",", StringComparison.Ordinal)) return null;
            try
            {
                var o = Json.Parse(line.Substring(0, line.Length - 1) + "}") as Json.Obj;
                var h = o != null ? o["header"] as Json.Obj : null;
                return h != null && h.Str("format") == TrackHeader.Format ? h : null;
            }
            catch (Exception) { return null; }
        }

        public static InputTrack Read(string text, out string error)
        {
            error = null;
            Json.Obj root;
            try { root = Json.Parse(text ?? "") as Json.Obj; }
            catch (Exception e) { error = "not JSON: " + e.Message; return null; }
            var h = root != null ? root["header"] as Json.Obj : null;
            if (h == null || h.Str("format") != TrackHeader.Format) { error = "not an input track (header.format is not " + TrackHeader.Format + ")"; return null; }
            if (h.Num("version", 0) > TrackHeader.Version) { error = "track version " + h.Num("version", 0) + " is newer than " + TrackHeader.Version; return null; }
            var cols = root["columns"] as List<object>;
            var rows = root["frames"] as List<object>;
            if (cols == null || rows == null) { error = "columns or frames missing"; return null; }
            var at = new int[Columns.Length];
            for (int c = 0; c < Columns.Length; c++)
            {
                at[c] = cols.IndexOf(Columns[c]);
                if (at[c] < 0) { error = "column " + Columns[c] + " missing"; return null; }
            }
            var t = new InputTrack();
            t.header = HeaderFrom(h);
            int prev = -1;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i] as List<object>;
                double[] v;
                if (r == null || !Numbers(r, at, out v)) { error = "frames[" + i + "]: expected " + Columns.Length + " numbers"; return null; }
                int k = (int)v[0];
                if (k != v[0] || k < 0 || (i == 0 && k != 0) || (i > 0 && k <= prev)) { error = "frames[" + i + "]: k " + v[0] + " must start at 0 and rise"; return null; }
                prev = k;
                int b = (int)v[4];
                t.frames.Add(new TrackFrame
                {
                    k = k, t = (float)v[1], sx = (float)v[2], sy = (float)v[3], jump = (b & 1) != 0, action = (b & 2) != 0,
                    px = (float)v[5], py = (float)v[6], pz = (float)v[7], vx = (float)v[8], vy = (float)v[9], vz = (float)v[10], flags = (int)v[11]
                });
                if (t.Count > InputTrack.MaxFrames) { error = "more than " + InputTrack.MaxFrames + " frames"; return null; }
            }
            return t;
        }

        static bool Numbers(List<object> r, int[] at, out double[] v)
        {
            v = new double[at.Length];
            for (int c = 0; c < at.Length; c++)
            {
                if (at[c] >= r.Count || !(r[at[c]] is double)) return false;
                double d = (double)r[at[c]];
                if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                v[c] = d;
            }
            return true;
        }

        static TrackHeader HeaderFrom(Json.Obj o)
        {
            var h = new TrackHeader
            {
                name = o.Str("name", ""), created = o.Str("created", ""), body = o.Str("body", ""), checkpoint = o.Str("checkpoint", ""),
                clock = o.Str("clock", ""), source = o.Str("source", "recorded"), subscene = (int)o.Num("subscene", -1),
                savepoint = (int)o.Num("savepoint", -1), loadCount = (int)o.Num("load_count", -1), startStep = (long)o.Num("start_step", -1),
                startTime = o.Num("start_time", 0)
            };
            var tr = o["trimmed"] as Json.Obj;
            if (tr != null)
            {
                h.trimFrom = (int)tr.Num("from", -1);
                h.trimTo = (int)tr.Num("to", -1);
                h.trimOf = (int)tr.Num("of", -1);
            }
            return h;
        }
    }

    internal sealed class TrackCursor
    {
        readonly TrackFrame[] f;
        int i = -1, j;

        public TrackCursor(TrackFrame[] frames) { f = frames ?? new TrackFrame[0]; }

        public int Length { get { return f.Length == 0 ? 0 : f[f.Length - 1].k + 1; } }
        // steps fed so far (the relative step of the next Next)
        public int Step { get { return j; } }
        public bool Done { get { return j >= Length; } }
        public TrackFrame First { get { return f.Length > 0 ? f[0] : default(TrackFrame); } }

        // the frame for the next step; exact = that step was recorded, so its body is a correction target
        public bool Next(out TrackFrame frame, out bool exact)
        {
            if (Done) { frame = default(TrackFrame); exact = false; return false; }
            while (i + 1 < f.Length && f[i + 1].k <= j) i++;
            frame = f[i];
            exact = frame.k == j;
            j++;
            return true;
        }
    }

    internal sealed class CorrectParams
    {
        public bool on = true;
        public double gain = 0.05, deadband = 0.01, maxStep = 0.02, maxDv = 0.5, stopDrift;
    }

    internal struct CorrectStep
    {
        public bool valid, apply, stop;
        public double drift, dx, dy, dvx, dvy;
        public double Shift { get { return Math.Sqrt(dx * dx + dy * dy); } }
    }

    internal static class TrackCorrect
    {
        // one step: live body (x, y, vx, vy) against the recorded one (rx, ry, rvx, rvy). Outside the deadband the
        // position moves gain of the way to the recorded one (at most maxStep m) and the velocity gain of the way (at
        // most maxDv m/s); stop when stopDrift > 0 and the drift is larger
        public static CorrectStep Step(CorrectParams p, double x, double y, double vx, double vy, double rx, double ry, double rvx, double rvy)
        {
            var s = new CorrectStep();
            if (!Finite(x) || !Finite(y) || !Finite(vx) || !Finite(vy) || !Finite(rx) || !Finite(ry) || !Finite(rvx) || !Finite(rvy)) return s;
            s.valid = true;
            double ex = rx - x, ey = ry - y;
            s.drift = Math.Sqrt(ex * ex + ey * ey);
            if (p.stopDrift > 0 && s.drift > p.stopDrift) { s.stop = true; return s; }
            if (!p.on || s.drift <= p.deadband) return s;
            double g = Math.Max(0, Math.Min(1, p.gain));
            s.dx = ex * g;
            s.dy = ey * g;
            double len = s.Shift;
            if (p.maxStep > 0 && len > p.maxStep) { double k = p.maxStep / len; s.dx *= k; s.dy *= k; }
            s.dvx = (rvx - vx) * g;
            s.dvy = (rvy - vy) * g;
            double dv = Math.Sqrt(s.dvx * s.dvx + s.dvy * s.dvy);
            if (p.maxDv > 0 && dv > p.maxDv) { double k = p.maxDv / dv; s.dvx *= k; s.dvy *= k; }
            s.apply = s.dx != 0 || s.dy != 0 || s.dvx != 0 || s.dvy != 0;
            return s;
        }

        static bool Finite(double d) { return !double.IsNaN(d) && !double.IsInfinity(d); }
    }

    internal sealed class DriftStats
    {
        public const int PerSecondCap = 1200, StepsPerSecond = 60;
        public int steps, measured, corrected, skipped, forced, gated, maxAt = -1;
        public double max, sum, start = -1, end, maxShift, totalShift;
        readonly List<object> perSecond = new List<object>();
        double secMax;
        int secSteps;

        // one fed step (whether or not it had a correction target)
        public void Fed(bool forcedStep, bool gatedStep)
        {
            steps++;
            if (forcedStep) forced++;
            if (gatedStep) gated++;
            if (++secSteps >= StepsPerSecond) Flush();
        }

        // a step with a correction target; applied = the correction was written to the body
        public void Measured(int j, CorrectStep c, bool applied)
        {
            if (!c.valid) { skipped++; return; }
            measured++;
            if (start < 0) start = c.drift;
            end = c.drift;
            sum += c.drift;
            if (c.drift > max) { max = c.drift; maxAt = j; }
            if (c.drift > secMax) secMax = c.drift;
            if (!applied) return;
            corrected++;
            double sh = c.Shift;
            totalShift += sh;
            if (sh > maxShift) maxShift = sh;
        }

        public void Skipped() { skipped++; }

        void Flush()
        {
            if (perSecond.Count < PerSecondCap) perSecond.Add(Math.Round(secMax, 3));
            secMax = 0;
            secSteps = 0;
        }

        public Json.Obj ToJson()
        {
            var o = new Json.Obj();
            o["steps"] = (double)steps;
            o["measured"] = (double)measured;
            o["corrected"] = (double)corrected;
            o["skipped"] = (double)skipped;
            o["forced_steps"] = (double)forced;
            o["gated_steps"] = (double)gated;
            o["start_offset_m"] = Math.Round(Math.Max(0, start), 3);
            o["max_drift_m"] = Math.Round(max, 3);
            o["max_drift_step"] = (double)maxAt;
            o["mean_drift_m"] = Math.Round(measured > 0 ? sum / measured : 0, 3);
            o["end_drift_m"] = Math.Round(end, 3);
            o["max_shift_m"] = Math.Round(maxShift, 4);
            o["total_shift_m"] = Math.Round(totalShift, 3);
            var ps = new List<object>(perSecond);
            if (secSteps > 0 && ps.Count < PerSecondCap) ps.Add(Math.Round(secMax, 3));
            o["drift_per_s"] = ps;
            return o;
        }
    }

    internal static class TrackPlan
    {
        // distance in the x / y plane between the body and the track's first frame
        public static double StartOffset(TrackFrame first, double x, double y)
        {
            double dx = first.px - x, dy = first.py - y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // why play must not start, or null
        public static string StartRefusal(TrackHeader h, int frames, string liveKind, double startOffset, double maxStart)
        {
            if (frames == 0) return "the track is empty";
            if (!string.IsNullOrEmpty(h.body) && !string.IsNullOrEmpty(liveKind) && h.body != liveKind)
                return "body: the track was recorded with the " + h.body + ", the controlled body is the " + liveKind;
            if (maxStart > 0 && startOffset > maxStart)
                return "start: the body is " + startOffset.ToString("0.##", CultureInfo.InvariantCulture) + " m from the track's first frame (max_start " +
                       maxStart.ToString("0.##", CultureInfo.InvariantCulture) + ")";
            return null;
        }

        // the checkpoint load that puts the body where the track starts
        public static string LoadHint(TrackHeader h)
        {
            if (h.subscene < 0) return "load the checkpoint the track was recorded from, then play right after it is ready";
            return "level {op:\"load_checkpoint\", subscene:" + h.subscene + ", savepoint:" + Math.Max(0, h.savepoint) + "} (or checkpoint goto), wait_until {ready:true}, then play";
        }
    }
}
