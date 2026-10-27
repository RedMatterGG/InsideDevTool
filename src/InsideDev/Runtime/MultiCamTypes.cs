using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // MultiCam v2 shared contract (architect-owned; implementers must not edit). Design: docs/MULTICAM_DESIGN.md.
    // Spec classes: one public field per JSON key (same name, default = initializer), filled by SpecBind from Json.Obj.
    // Runtime types: CamPose, GameCamState, TargetSample, MultiCamContext, ICamRig, ITarget and the lookup delegates
    // that connect MultiCam.cs (CORE) with MultiCamRig.cs and MultiCamTargets.cs (RIG).

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SpecIgnoreAttribute : Attribute { }

    public interface IJsonValue
    {
        bool Read(object value, string where, SpecReport report);
    }

    public abstract class SpecNode
    {
        [SpecIgnore] public List<string> given = new List<string>();
        [SpecIgnore] public string where = "";
        public bool Has(string key) { return given.Contains(key); }
    }

    public sealed class SpecReport
    {
        public readonly List<string> errors = new List<string>();
        public readonly List<string> warnings = new List<string>();
        public readonly List<string> notes = new List<string>();
        public bool ok { get { return errors.Count == 0; } }
        public void Error(string where, string message) { errors.Add(Line(where, message)); }
        public void Warn(string where, string message) { warnings.Add(Line(where, message)); }
        public void Note(string where, string message) { notes.Add(Line(where, message)); }

        public string Summary(int max)
        {
            var sb = new StringBuilder();
            sb.Append(errors.Count).Append(" error(s), ").Append(warnings.Count).Append(" warning(s)");
            Append(sb, "error", errors, max);
            Append(sb, "warn", warnings, max);
            Append(sb, "note", notes, max);
            return sb.ToString();
        }

        static void Append(StringBuilder sb, string tag, List<string> lines, int max)
        {
            for (int i = 0; i < lines.Count && i < max; i++) sb.Append('\n').Append(tag).Append(": ").Append(lines[i]);
            if (lines.Count > max) sb.Append('\n').Append(tag).Append(": ... ").Append(lines.Count - max).Append(" more");
        }

        static string Line(string where, string message) { return string.IsNullOrEmpty(where) ? message : where + ": " + message; }
    }

    // ------------------------------------------------------------------ time and target references

    public sealed class TimeRef : IJsonValue
    {
        public double offset;
        public string ev;

        public TimeRef() { }
        public TimeRef(double seconds) { offset = seconds; }
        public TimeRef(string eventName, double offsetSeconds) { ev = eventName; offset = offsetSeconds; }

        public bool absolute { get { return string.IsNullOrEmpty(ev); } }

        public bool Read(object value, string where, SpecReport report)
        {
            if (value is double) { offset = (double)value; ev = null; return true; }
            var s = value as string;
            string e; double o;
            if (s != null && TryParse(s, out e, out o)) { ev = e; offset = o; return true; }
            report.Error(where, "time expected: game seconds or \"@event\", \"@event+1.5\", \"@event-0.5\"");
            return false;
        }

        public static bool TryParse(string s, out string eventName, out double offsetSeconds)
        {
            eventName = null; offsetSeconds = 0;
            if (s == null) return false;
            s = s.Trim();
            if (s.Length == 0) return false;
            if (s[0] != '@') return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out offsetSeconds);
            int i = 1;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.')) i++;
            if (i == 1) return false;
            string name = s.Substring(1, i - 1);
            string rest = s.Substring(i).Replace(" ", "");
            if (rest.Length == 0) { eventName = name; return true; }
            if (rest[0] != '+' && rest[0] != '-') return false;
            double o;
            if (!double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out o)) return false;
            eventName = name; offsetSeconds = o;
            return true;
        }

        public override string ToString()
        {
            var ic = CultureInfo.InvariantCulture;
            if (absolute) return offset.ToString("0.######", ic);
            if (offset == 0) return "@" + ev;
            return "@" + ev + (offset > 0 ? "+" : "") + offset.ToString("0.######", ic);
        }
    }

    public sealed class TargetRef : SpecNode, IJsonValue
    {
        public string of;
        public string point;
        public Vector3 offset;
        public Vector3 pos;
        public GroupSpec group;
        [SpecIgnore] public object cache;
        [SpecIgnore] public int cacheVersion = -1;

        public bool fixedPoint { get { return Has("pos"); } }
        public bool isGroup { get { return group != null; } }
        public bool isNone { get { return of == "none"; } }
        public bool isSelf { get { return of == "self"; } }

        public static TargetRef Of(string text) { var r = new TargetRef(); r.of = text; r.given.Add("of"); return r; }

        public static TargetRef At(Vector3 p) { var r = new TargetRef(); r.pos = p; r.given.Add("pos"); return r; }

        public bool Read(object value, string where, SpecReport report)
        {
            this.where = where;
            var s = value as string;
            if (s != null)
            {
                of = s.Trim();
                if (!given.Contains("of")) given.Add("of");
                if (of.Length > 0) return true;
                report.Error(where, "empty target");
                return false;
            }
            var l = value as List<object>;
            if (l != null)
            {
                Vector3 v;
                if (!SpecBind.Vec3(l, out v)) { report.Error(where, "target point [x, y, z] expected"); return false; }
                pos = v;
                if (!given.Contains("pos")) given.Add("pos");
                return true;
            }
            var o = value as Json.Obj;
            if (o != null)
            {
                SpecBind.Fill(this, o, where, report);
                if (!string.IsNullOrEmpty(of) || fixedPoint || group != null) return true;
                report.Error(where, "target object needs 'of', 'pos' or 'group'");
                return false;
            }
            report.Error(where, "target expected: \"boy\" | \"<path>\" | [x, y, z] | {of, point, offset} | {group}");
            return false;
        }

        public string text
        {
            get
            {
                var ic = CultureInfo.InvariantCulture;
                string b;
                if (group != null) b = "group(" + (group.find.Length > 0 ? group.find : group.list.Length > 0 ? group.list : group.of.Count + " members") + ")";
                else if (fixedPoint) b = "[" + pos.x.ToString("0.###", ic) + "," + pos.y.ToString("0.###", ic) + "," + pos.z.ToString("0.###", ic) + "]";
                else b = of ?? "";
                if (!string.IsNullOrEmpty(point)) b += " ." + point;
                if (offset != Vector3.zero) b += " +(" + offset.x.ToString("0.###", ic) + "," + offset.y.ToString("0.###", ic) + "," + offset.z.ToString("0.###", ic) + ")";
                return b;
            }
        }

        public override string ToString() { return text; }
    }

    public sealed class GroupSpec : SpecNode
    {
        public List<TargetRef> of = new List<TargetRef>();
        public string find = "";
        public string list = "";
        public GroupFilter filter = new GroupFilter();
        public string mode = "centroid";
        public string point = "";
        public float smooth = 0.3f;
        public float hold = 0.5f;
        public int max = 0;
    }

    public sealed class GroupFilter : SpecNode
    {
        public bool active = true;
        public bool alive = true;
        public bool notKinematic = false;
        public float minSpeed = 0f;
        public Vector3 boxCenter;
        public Vector3 boxSize;
        public TargetRef near;
        public float maxDist = 0f;
    }

    // ------------------------------------------------------------------ spec: root

    public sealed class MultiCamSpec : SpecNode
    {
        public int version = 2;
        public string name = "";
        public double seconds = 10.0;
        public TimeRef until;
        public double preroll = 0.0;
        public int seed = -1;
        public string rehearsal = "";
        public List<string> setup = new List<string>();
        public List<CueSpec> cues = new List<CueSpec>();
        public List<EventSpec> events = new List<EventSpec>();
        public TimingSpec timing = new TimingSpec();
        public List<RateSpec> rates = new List<RateSpec>();
        public int settle = -1;
        public int jpeg = -1;
        public RenderSpec render = new RenderSpec();
        public OutputSpec output = new OutputSpec();
        public SessionSpec session = new SessionSpec();
        public PostSpec post = new PostSpec();
        public StreamSpec stream = new StreamSpec();
        public List<TargetRef> interpolate = new List<TargetRef>();
        public List<TargetRef> tracks = new List<TargetRef>();
        public CamSpec defaults;
        public List<CamSpec> cameras = new List<CamSpec>();
        public Json.Obj compose;
        public ReplaySpec replay;
        [SpecIgnore] public string text = "";
        [SpecIgnore] public string path = "";

        public CamSpec FindCamera(string camName)
        {
            foreach (var c in cameras) if (c.name == camName) return c;
            return null;
        }

        public EventSpec FindEvent(string eventName)
        {
            foreach (var e in events) if (e.name == eventName) return e;
            return null;
        }

        public static MultiCamSpec Parse(string json, string sourcePath, SpecReport report)
        {
            object root;
            try { root = Json.Parse(json); }
            catch (Exception e) { report.Error(sourcePath, "JSON: " + e.Message); return null; }
            var o = root as Json.Obj;
            if (o == null) { report.Error(sourcePath, "spec must be a JSON object"); return null; }
            var s = new MultiCamSpec();
            s.text = json; s.path = sourcePath ?? "";
            SpecBind.Fill(s, o, "", report);
            return s;
        }
    }

    // ------------------------------------------------------------------ spec: timing

    public sealed class TimingSpec : SpecNode
    {
        public float fps = 30f;
        public string physics = "game";
        public List<SegmentSpec> segments = new List<SegmentSpec>();
    }

    public sealed class SegmentSpec : SpecNode
    {
        public TimeRef from = new TimeRef(0);
        public float speed = 1f;
        public int samples = 1;
        public float shutter = 180f;
        public int rate = 0;
        public string label = "";
    }

    public sealed class RateSpec : SpecNode, IJsonValue
    {
        public TimeRef from = new TimeRef(0);
        public float rate = 30f;
        public int group = 1;

        public bool Read(object value, string where, SpecReport report)
        {
            this.where = where;
            var l = value as List<object>;
            if (l != null)
            {
                if (l.Count < 2 || !(l[1] is double)) { report.Error(where, "rate segment [fromGameSecond, stepsPerGameSecond, stepsPerOutputFrame] expected"); return false; }
                var f = new TimeRef();
                if (!f.Read(l[0], where + "[0]", report)) return false;
                from = f; given.Add("from");
                rate = (float)(double)l[1]; given.Add("rate");
                if (l.Count > 2 && l[2] != null)
                {
                    object g;
                    if (!SpecBind.Convert(l[2], typeof(int), where + "[2]", report, out g)) return false;
                    group = (int)g; given.Add("group");
                }
                return true;
            }
            var o = value as Json.Obj;
            if (o != null) { SpecBind.Fill(this, o, where, report); return true; }
            report.Error(where, "rate segment expected: [from, rate, group] or {from, rate, group}");
            return false;
        }
    }

    // ------------------------------------------------------------------ spec: cues and events

    public sealed class CueSpec : SpecNode, IJsonValue
    {
        public TimeRef at = new TimeRef(0);
        public string cmd = "";
        public List<string> cmds = new List<string>();
        public string label = "";

        public bool Read(object value, string where, SpecReport report)
        {
            this.where = where;
            var l = value as List<object>;
            if (l != null)
            {
                if (l.Count < 2) { report.Error(where, "cue [time, \"command\", ...] expected"); return false; }
                var a = new TimeRef();
                if (!a.Read(l[0], where + "[0]", report)) return false;
                at = a; given.Add("at");
                for (int i = 1; i < l.Count; i++)
                {
                    var c = l[i] as string;
                    if (c == null) { report.Error(where + "[" + i + "]", "command string expected"); return false; }
                    if (i == 1) { cmd = c; given.Add("cmd"); } else cmds.Add(c);
                }
                if (cmds.Count > 0) given.Add("cmds");
                return true;
            }
            var o = value as Json.Obj;
            if (o != null)
            {
                SpecBind.Fill(this, o, where, report);
                if (cmd.Length > 0 || cmds.Count > 0) return true;
                report.Error(where, "cue needs 'cmd' or 'cmds'");
                return false;
            }
            report.Error(where, "cue expected: [time, \"command\"] or {at, cmd | cmds}");
            return false;
        }

        public List<string> Commands()
        {
            var r = new List<string>();
            if (cmd.Length > 0) r.Add(cmd);
            r.AddRange(cmds);
            return r;
        }
    }

    public sealed class EventSpec : SpecNode
    {
        public string name = "";
        public string type = "";
        public string obj = "";
        public string comp = "";
        public string field = "";
        public string signal = "";
        public string fsm = "";
        public string state = "";
        public string fsmEvent = "";
        public string sound = "";
        public string edge = "enter";
        public TargetRef target;
        public TargetRef to;
        public string prop = "";
        public string op = "";
        public double value = 0.0;
        public string match = "";
        public Vector3 center;
        public Vector3 size;
        public float min = 0f;
        public bool once = true;
        public int count = 1;
        public TimeRef after;
        public float cooldown = 0f;
        public double expect = double.NaN;
        public bool log = true;
    }

    // ------------------------------------------------------------------ spec: capture settings

    public sealed class RenderSpec : SpecNode
    {
        public int warmup = 1;
        public int samples = 4;
        public string taa = "reset";
        public int jitter = 0;
        public float motionBlur = 0f;
    }

    public sealed class OutputSpec : SpecNode
    {
        public string format = "jpg";
        public int jpeg = 95;
        public int queue = 3;
        public bool poses = true;
        public bool rig = true;
        public bool tracks = true;
        public int flushEvery = 30;
        public string ffmpeg = "ffmpeg";
        public string pipeCodec = "hevc_nvenc";
        public int pipeQp = 14;
        public string readback = "auto";
    }

    public sealed class SessionSpec : SpecNode
    {
        public bool clean = true;
        public bool vsync = false;
        public bool runInBackground = true;
        public bool lockInput = true;
        public bool handshake = true;
        public bool shake = true;
        public Vector2 res;
        public string realTime = "game";
        public bool holdWhileLoading = true;
        public bool quietLog = true;
        public int gcSteps = 0;
    }

    public sealed class PostSpec : SpecNode
    {
        public float brightness = float.NaN;
        public float blacklift = 0f;
        public float postGamma = 0f;
        public float inWhiteScale = 1f;
        public float saturation = 0f;
        public float bloomScale = 1f;
        public float distortionScale = 1f;
        public bool fogMatch = false;
        public float fogOffset = 0f;
        public bool noWater = false;
    }

    public sealed class StreamSpec : SpecNode
    {
        public string keepLoaded = "auto";
        public List<string> cullers = new List<string>();
        public bool prepass = true;
        public int visitFrames = 0;
        public float timeout = 20f;
        public bool freeze = false;
        public bool addDuringTake = true;
    }

    // ------------------------------------------------------------------ spec: cameras

    public sealed class ShakeSpec : SpecNode
    {
        public float hand = 0f;
        public float fx = 0f;
        public bool pullback = false;
        public float replica = 0f;
        public float hard = 0f;
        public string clock = "game";
        public float rate = 1f;
        public float phase = 0f;
    }

    public sealed class KeySpec : SpecNode
    {
        public float t = 0f;
        public Vector3 pos;
        public float yaw = 0f;
        public float pitch = 0f;
        public float roll = 0f;
        public float fov = 0f;
        public TargetRef look;
        public Vector3 lookOffset;
    }

    public sealed class ShotSpec : SpecNode
    {
        public string cam = "";
        public TimeRef from = new TimeRef(0);
        public float blend = 0f;
        public string ease = "smooth";
        public string transition = "orbit";
        public string clock = "game";
        public string label = "";
        public List<string> hide = new List<string>();
        public float motionBlur = 1f;
    }

    // Camera motion limits on the output clock (MULTICAM.md 6.3): peak camera speed (m/s) and turn rate (deg/s) and the ease
    // in / ease out time of a move (s). Directors stretch a blend and events cameras a beat move to the time the limits need
    // for its distance and turn (defaults apply when `motion` is not given); the other modes, except `game`, follow their pose
    // through the limiter only when `motion` is given. 0 turns a limit off. blurScale scales this camera's replay blur.
    public sealed class MotionSpec : SpecNode
    {
        public float maxSpeed = 6f;
        public float maxTurn = 35f;
        public float ease = 0.6f;
        public float blurScale = 1f;
    }

    // Frame-edge clamp (any mode): world x of the left / right frame edge at the subject depth; only the given sides apply.
    // Depth = the pose's aim point along the view axis, or the world plane z when `z` is given. aspect 0 = the frame's own.
    public sealed class ClampSpec : SpecNode
    {
        public float left = 0f;
        public float right = 0f;
        public float z = 0f;
        public float aspect = 0f;
        public bool corners = true;
    }

    // overview: one pan segment (record seconds): from `from`, the frame centre x moves to `to` over `duration`, then holds.
    public sealed class PanSpec : SpecNode
    {
        public TimeRef from = new TimeRef(0);
        public float duration = 4f;
        public float to = 0f;
        public string ease = "smooth";
    }

    // events: one beat; 0 / unset fields take the camera's value (hold, move, late, fov, offset, offsets, lookOffset).
    public sealed class BeatSpec : SpecNode
    {
        public TimeRef at = new TimeRef(0);
        public TargetRef target;
        public string point = "";
        public float hold = 0f;
        public float move = 0f;
        public float late = -1f;
        public float fov = 0f;
        public Vector3 offset;
        public List<Vector3> offsets = new List<Vector3>();
        public Vector3 lookOffset;
        public string label = "";
    }

    // Line-of-sight and near-plane checks (events candidates, replay plan passes every `every` record seconds, mcam views/show):
    // colliders on `layers` ([] = all but Ignore Raycast; triggers and paths containing an `ignore` entry skipped) and the
    // bounds of `avoid` targets (+ the huddle cloth bounds when `huddle`), grown by `margin`; the ray stops `subjectRadius`
    // short of the subject point.
    public sealed class ViewSpec : SpecNode
    {
        public List<TargetRef> avoid = new List<TargetRef>();
        public bool huddle = true;
        public List<int> layers = new List<int>();
        public List<string> ignore = new List<string>();
        public float margin = 0.25f;
        public float subjectRadius = 0.3f;
        public bool near = true;
        public float every = 0.5f;
    }

    public sealed class CamSpec : SpecNode
    {
        public string name = "";
        public string mode = "static";
        public string label = "";
        public bool enabled = true;
        public float fov = 0f;
        public float near = 0f;
        public float focus = 10f;
        public Vector3 pos;
        public float yaw = 0f;
        public float pitch = 0f;
        public float roll = 0f;
        public TargetRef target;
        public TargetRef look;
        public string point = "";
        public Vector3 offset;
        public Vector3 lookOffset;
        public bool local = false;
        public float smooth = 0f;
        public float lookSmooth = 0f;
        public float fovScale = 1f;
        public string lag = "damp";
        public float lead = 0f;
        public bool feedForward = true;
        public bool planar = false;
        public TimeRef start;
        public List<KeySpec> keys = new List<KeySpec>();
        public string interp = "catmull";
        public string ease = "ends";
        public float duration = 0f;
        public List<Vector3> path = new List<Vector3>();
        public float radius = 8f;
        public float height = 1.5f;
        public Vector2 azimuth;
        public float orbitYaw = 0f;
        public float orbitPitch = 0f;
        public float dist = 0f;
        public float distScale = 1f;
        public Vector2 shift;
        public bool keepSize = true;
        public bool clean = false;
        public string box = "auto";
        public Vector3 camOffset = new Vector3(0f, 2f, -11f);
        public Vector3 aimOffset = new Vector3(0f, 2f, 0f);
        public float extrapolate = -1f;
        public Vector2 screen = new Vector2(0.48f, 0.6f);
        public float size = 0.12f;
        public float subjectHeight = 0f;
        public float[] crop;
        public List<ShotSpec> shots = new List<ShotSpec>();
        public float cutAngle = 90f;
        public float cutDistance = 0f;
        public float minHold = 4f;
        public List<PanSpec> pans = new List<PanSpec>();
        public List<BeatSpec> beats = new List<BeatSpec>();
        public List<Vector3> offsets = new List<Vector3>();
        public float hold = 2f;
        public float move = 1f;
        public float late = 2.5f;
        public ClampSpec clamp;
        public ViewSpec view;
        public bool playerSide = true;
        public float zMax = -2f;
        public RenderSpec render;
        public PostSpec post;
        public ShakeSpec shake;
        public List<string> hide = new List<string>();
        public List<string> backfaces = new List<string>();
        public MotionSpec motion;
        [SpecIgnore] public int index = -1;
    }

    // ------------------------------------------------------------------ runtime types

    public static class MultiCamConst
    {
        public const float GameFov = 50.987f;
        public const float GameNear = 1f;
        public const float GameFar = 600f;
        public const float GameAimDistance = 11f;
        public const float BoyHeight = 1.33f;
        public const float PlayerSideZ = -2f;
        public const int MinRate = 10;
        public const int MaxRate = 960;
        public static readonly string[] Modes = { "game", "static", "follow", "keys", "rail", "orbit", "gamerel", "gamelike", "frame", "director", "overview", "events" };
        public static readonly string[] EventTypes = { "time", "signal", "signalIn", "fsm", "sound", "probe", "impact", "box", "cond", "field", "boystate", "visual", "death", "loaded", "camcut", "posted" };
    }

    public static class PoseFlags
    {
        public const int Fallback = 1;
        public const int Clamped = 2;
        public const int Waiting = 4;
        public const int Blending = 8;
        public const int Cut = 16;
        public const int NoTarget = 32;
        public const int Copy = 64;
        public const int Shake = 128;

        public static string Letters(int f)
        {
            var sb = new StringBuilder();
            if ((f & Fallback) != 0) sb.Append('F');
            if ((f & Clamped) != 0) sb.Append('C');
            if ((f & Waiting) != 0) sb.Append('W');
            if ((f & Blending) != 0) sb.Append('B');
            if ((f & Cut) != 0) sb.Append('X');
            if ((f & NoTarget) != 0) sb.Append('T');
            if ((f & Copy) != 0) sb.Append('P');
            if ((f & Shake) != 0) sb.Append('S');
            return sb.ToString();
        }
    }

    public struct CamPose
    {
        public Vector3 p;
        public Quaternion r;
        public float fov;
        public bool ok;
        public Vector3 aim;
        public float near;
        public string copyOf;
        public int flags;
        public bool hasSubj;
        public Vector3 subj;
        public Vector3 subjVel;
        public bool attached;
        public Vector3 moveVel;

        public Vector3 forward { get { return r * Vector3.forward; } }

        public static CamPose Make(Vector3 position, Quaternion rotation, float fov, float focus)
        {
            var c = new CamPose();
            c.p = position; c.r = rotation; c.fov = fov; c.ok = true;
            c.aim = position + rotation * Vector3.forward * focus;
            return c;
        }

        public static CamPose LookAt(Vector3 position, Vector3 aim, Vector3 up, float roll, float fov)
        {
            var d = aim - position;
            if (d.sqrMagnitude < 1e-8f) d = Vector3.forward;
            var c = new CamPose();
            c.p = position; c.aim = aim; c.fov = fov; c.ok = true;
            c.r = Quaternion.LookRotation(d, up) * Quaternion.Euler(0f, 0f, roll);
            return c;
        }
    }

    public sealed class GameCamState
    {
        public bool ok;
        public int step = -1;
        public CamPose rendered;
        public CamPose clean;
        public Vector3 positionCamera;
        public Vector3 positionAim;
        public Vector3 positionUp = Vector3.up;
        public Vector3 velocity;
        public float aimDistance = MultiCamConst.GameAimDistance;
        public string box = "";
        public Vector3 probe;
        public float extrapolation;
        public Vector3 handOffset;
        public Quaternion handRot = Quaternion.identity;
        public Vector3 shakeOffset;
        public Quaternion shakeRot = Quaternion.identity;
        public bool cut;
    }

    public struct TargetSample
    {
        public bool ok;
        public Vector3 pos;
        public Vector3 vel;
        public bool hasBounds;
        public Bounds bounds;
        public float height;
        public int count;
        public GameObject go;
    }

    public delegate bool TargetLookup(TargetRef target, out TargetSample sample);
    public delegate bool PoseLookup(string camera, out CamPose pose);
    public delegate double EventTimeLookup(string eventName);

    public sealed class MultiCamContext
    {
        public MultiCamSpec spec;
        public int step = -1;
        public double t;
        public float dt;
        public int frame = -1;
        public int sub = -1;
        public bool captured;
        public int rate = 30;
        public float speed = 1f;
        public float fps = 30f;
        public double outputTime;
        public float outputDt;
        public double realTime;
        public int screenW = 2560;
        public int screenH = 1440;
        public float aspect = 16f / 9f;
        public bool preview;
        public bool rehearse;
        public bool planning;
        public Camera camera;
        public readonly GameCamState game = new GameCamState();
        public TargetLookup target;
        public PoseLookup poseOf;
        public EventTimeLookup eventTime;
        public Action<string> log;

        public bool Target(TargetRef r, out TargetSample s)
        {
            s = default(TargetSample);
            if (r == null || target == null) return false;
            return target(r, out s);
        }

        public bool PoseOf(string cameraName, out CamPose p)
        {
            p = default(CamPose);
            if (poseOf == null || string.IsNullOrEmpty(cameraName)) return false;
            return poseOf(cameraName, out p);
        }

        public double TimeOf(TimeRef r, double fallback)
        {
            if (r == null) return fallback;
            if (r.absolute) return r.offset;
            if (eventTime == null) return double.NaN;
            double te = eventTime(r.ev);
            return double.IsNaN(te) ? double.NaN : te + r.offset;
        }

        public double LocalTime(TimeRef start)
        {
            double s = TimeOf(start, 0.0);
            return double.IsNaN(s) ? double.NaN : t - s;
        }

        public bool Reached(TimeRef r)
        {
            double s = TimeOf(r, 0.0);
            return !double.IsNaN(s) && t >= s - 1e-6;
        }

        public void Log(string message) { if (log != null) log(message); }
    }

    public interface ICamRig
    {
        CamSpec spec { get; }
        void Begin(MultiCamContext ctx);
        CamPose Evaluate(MultiCamContext ctx);
        void Plan(MultiCamContext ctx, List<CamPose> poses);
        void End();
    }

    public interface ITarget
    {
        string text { get; }
        string resolved { get; }
        bool Sample(MultiCamContext ctx, out TargetSample sample);
    }

    // Pure camera geometry and scheduling of the overview / events modes and the frame-edge clamp (no engine calls, so the
    // offline harness qa/replay CamPlanTests.cs runs it under .NET 9).
    public static class MultiCamGeom
    {
        // World x of the frame's left and right edges: camera at p with basis ax/up/fwd (unit), tanV = tan(vfov / 2). plane =
        // where the edge rays meet the world plane z = depth, else at camera-space depth `depth`. corners adds the frame
        // corners to the mid-edge rays (the outermost x wins).
        public static bool EdgeXs(Vector3 p, Vector3 ax, Vector3 up, Vector3 fwd, float tanV, float aspect, bool plane, float depth, bool corners, out float left, out float right)
        {
            left = float.PositiveInfinity; right = float.NegativeInfinity;
            float tanH = tanV * aspect;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = corners ? -1 : 0; sy <= (corners ? 1 : 0); sy++)
                {
                    var d = fwd + ax * (sx * tanH) + up * (sy * tanV);
                    float x;
                    if (plane)
                    {
                        if (d.z <= 1e-5f) continue;
                        float t = (depth - p.z) / d.z;
                        if (t <= 0f) continue;
                        x = p.x + t * d.x;
                    }
                    else x = p.x + depth * d.x;
                    if (sx < 0) left = Math.Min(left, x); else right = Math.Max(right, x);
                }
            return !float.IsInfinity(left) && !float.IsInfinity(right);
        }

        // Truck shift in x that keeps the frame inside [minX, maxX] on the given sides; a frame wider than both limits is
        // centred between them. False when nothing moves (overshoots below EdgeTol, 1 mm, are float noise).
        public const float EdgeTol = 0.001f;

        public static bool EdgeShift(float left, float right, bool hasMin, float minX, bool hasMax, float maxX, out float shift)
        {
            shift = 0f;
            bool lo = hasMin && left < minX - EdgeTol, hi = hasMax && right > maxX + EdgeTol;
            if (!lo && !hi) return false;
            if (hasMin && hasMax && right - left > maxX - minX) shift = (minX + maxX) * 0.5f - (left + right) * 0.5f;
            else if (hi) shift = maxX - right;
            else shift = minX - left;
            return Math.Abs(shift) > 0f;
        }

        // events: beats in event-time order; a beat starts at its time or when the previous kept beat's hold ends, whichever
        // is later, and is dropped (NaN) when that is more than `late` after its time. NaN times (event never fired) drop.
        public static double[] Schedule(double[] at, float[] hold, float[] late)
        {
            int n = at.Length;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                bool na = double.IsNaN(at[a]), nb = double.IsNaN(at[b]);
                if (na != nb) return na ? 1 : -1;
                int c = na ? 0 : at[a].CompareTo(at[b]);
                return c != 0 ? c : a.CompareTo(b);
            });
            var start = new double[n];
            for (int i = 0; i < n; i++) start[i] = double.NaN;
            double free = double.NegativeInfinity;
            foreach (int i in order)
            {
                if (double.IsNaN(at[i])) continue;
                double s = Math.Max(at[i], free);
                if (s - at[i] > Math.Max(0f, late[i]) + 1e-9) continue;
                start[i] = s;
                free = s + Math.Max(0f, hold[i]);
            }
            return start;
        }

        // The beat on air at t (latest start <= t; equal starts: the higher index), -1 before the first.
        public static int Current(double[] start, double t)
        {
            int best = -1;
            for (int i = 0; i < start.Length; i++)
                if (!double.IsNaN(start[i]) && start[i] <= t + 1e-9 && (best < 0 || start[i] >= start[best])) best = i;
            return best;
        }

        // The kept beat scheduled right before beat c, -1 when c is the first.
        public static int Previous(double[] start, int c)
        {
            if (c < 0) return -1;
            int best = -1;
            for (int i = 0; i < start.Length; i++)
            {
                if (i == c || double.IsNaN(start[i])) continue;
                bool before = start[i] < start[c] || (start[i] == start[c] && i < c);
                if (before && (best < 0 || start[i] > start[best] || (start[i] == start[best] && i > best))) best = i;
            }
            return best;
        }

        public static float Ease(string ease, float x)
        {
            x = x < 0f ? 0f : x > 1f ? 1f : x;
            switch (ease)
            {
                case "linear": return x;
                case "smoother": return x * x * x * (x * (x * 6f - 15f) + 10f);
                case "sine": return 0.5f - 0.5f * (float)Math.Cos(Math.PI * x);
            }
            return x * x * (3f - 2f * x);
        }

        // overview pan: x moves from x0 (the frame centre when the pan started) to `to` over `duration` from `from`.
        public static float Pan(float x0, float to, double from, float duration, double t, string ease)
        {
            if (t <= from) return x0;
            if (duration <= 0f) return to;
            return x0 + (to - x0) * Ease(ease, (float)((t - from) / duration));
        }

        // Segment a -> b against an axis-aligned box (centre c, half size e); enter = segment parameter of the first contact.
        public static bool SegmentHitsBox(Vector3 a, Vector3 b, Vector3 c, Vector3 e, out float enter)
        {
            enter = 0f;
            float t0 = 0f, t1 = 1f;
            for (int k = 0; k < 3; k++)
            {
                float o = a[k], d = b[k] - a[k], lo = c[k] - e[k], hi = c[k] + e[k];
                if (Math.Abs(d) < 1e-9f)
                {
                    if (o < lo || o > hi) return false;
                    continue;
                }
                float ta = (lo - o) / d, tb = (hi - o) / d;
                if (ta > tb) { float tmp = ta; ta = tb; tb = tmp; }
                if (ta > t0) t0 = ta;
                if (tb < t1) t1 = tb;
                if (t0 > t1) return false;
            }
            enter = t0;
            return true;
        }

        public static bool InsideBox(Vector3 p, Vector3 c, Vector3 e)
        {
            return Math.Abs(p.x - c.x) <= e.x && Math.Abs(p.y - c.y) <= e.y && Math.Abs(p.z - c.z) <= e.z;
        }

        // ---- camera motion limits (MotionSpec, MULTICAM.md 6.3), output clock

        // A jump of more than this many metres between two poses is a cut: the limiter takes it at once.
        public const float MotionSnap = 25f;

        // Velocity feed-forward of damped target tracking (MULTICAM.md 6.6): smoothing time of the subject velocity, its cap, and
        // the speed above which a sample is a snap (teleport, cut) that keeps the previous value.
        public const float FeedVelTau = 0.1f, FeedMaxSpeed = 30f, FeedSnapSpeed = 60f;

        // The lead to add to the subject before a critically damped SmoothDamp of smoothTime: the smoothed, capped subject velocity
        // times smoothTime, so a steadily moving subject has no steady lag. state = the smoothed velocity (init false: the first
        // sample starts it; a tracker starting at the subject also starts its damper velocity at `state`).
        public static Vector3 FeedLead(Vector3 vel, ref Vector3 state, ref bool init, float smoothTime, float dt)
        {
            if (smoothTime <= 0f) return Vector3.zero;
            float sp = vel.magnitude;
            if (sp <= FeedSnapSpeed)
            {
                var v = sp > FeedMaxSpeed ? vel * (FeedMaxSpeed / sp) : vel;
                if (!init) { state = v; init = true; }
                else if (dt > 0f) state += (v - state) * (1f - (float)Math.Exp(-dt / FeedVelTau));
            }
            else if (!init) { state = Vector3.zero; init = true; }
            return state * smoothTime;
        }

        // Turn rate (deg/s) the subject's velocity causes seen from `from`: its part across the view ray over the distance.
        public static float SubjectTurn(Vector3 from, Vector3 subj, Vector3 subjVel)
        {
            var d = subj - from;
            float dist = d.magnitude;
            if (dist < 0.05f) return 0f;
            var n = d / dist;
            var across = subjVel - n * Vector3.Dot(subjVel, n);
            return (float)(across.magnitude / dist * 180.0 / Math.PI);
        }

        // Moves the limiter state with the subject for one step before the limit (MULTICAM.md 6.3): the position by the velocity
        // of what the camera position follows (move: the follow / frame target, the overview's x; zero for a camera that does not
        // follow), then, when the camera aims at the subject, the rotation by the turn that keeps the subject on the same view ray.
        // What is left (reframing, a switch to another subject) stays under maxSpeed / maxTurn.
        public static void Carry(ref Vector3 pos, ref Quaternion rot, Vector3 subj, Vector3 subjVel, Vector3 move, bool aimed, float dt)
        {
            if (dt <= 0f) return;
            var step = subjVel * dt;
            var own = move * dt;
            if (step.magnitude > MotionSnap || own.magnitude > MotionSnap) return;
            var d0 = subj - step - pos;
            pos += own;
            var d1 = subj - pos;
            if (!aimed || d0.sqrMagnitude < 1e-6f || d1.sqrMagnitude < 1e-6f) return;
            rot = FromTo(d0, d1) * rot;
        }

        public static Quaternion FromTo(Vector3 a, Vector3 b)
        {
            float la = a.magnitude, lb = b.magnitude;
            if (la < 1e-9f || lb < 1e-9f) return new Quaternion(0f, 0f, 0f, 1f);
            var an = a / la;
            var bn = b / lb;
            float d = Vector3.Dot(an, bn);
            if (d > 0.9999999f) return new Quaternion(0f, 0f, 0f, 1f);
            if (d < -0.9999f)
            {
                var axis = Vector3.Cross(an, Math.Abs(an.x) < 0.9f ? new Vector3(1f, 0f, 0f) : new Vector3(0f, 1f, 0f));
                axis = axis / axis.magnitude;
                return new Quaternion(axis.x, axis.y, axis.z, 0f);
            }
            var c = Vector3.Cross(an, bn);
            float s = (float)Math.Sqrt((1.0 + d) * 2.0);
            return new Quaternion(c.x / s, c.y / s, c.z / s, s * 0.5f);
        }

        // Peak of d(ease)/dx: how much faster than its average speed a move with this ease is at its fastest.
        public static float EasePeak(string ease)
        {
            switch (ease)
            {
                case "linear": return 1f;
                case "smoother": return 1.875f;
                case "sine": return (float)(Math.PI / 2.0);
            }
            return 1.5f;
        }

        // Output seconds a move of `dist` metres and `turn` degrees needs so that its peak speed stays within maxSpeed and its
        // peak turn rate within maxTurn (0 = no limit), and so that it has `ease` seconds to speed up and as many to slow down
        // (not for linear, which has no ease).
        public static float MoveSeconds(float dist, float turn, string ease, float maxSpeed, float maxTurn, float easeSeconds)
        {
            float k = EasePeak(ease), t = 0f;
            if (maxSpeed > 0f && dist > 0f) t = Math.Max(t, k * dist / maxSpeed);
            if (maxTurn > 0f && turn > 0f) t = Math.Max(t, k * turn / maxTurn);
            if (ease != "linear" && easeSeconds > 0f && (dist > 1e-3f || turn > 0.05f)) t = Math.Max(t, 2f * easeSeconds);
            return t;
        }

        public static float AngleDeg(Quaternion a, Quaternion b)
        {
            double d = Math.Abs((double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w);
            double na = Math.Sqrt((double)a.x * a.x + (double)a.y * a.y + (double)a.z * a.z + (double)a.w * a.w);
            double nb = Math.Sqrt((double)b.x * b.x + (double)b.y * b.y + (double)b.z * b.z + (double)b.w * b.w);
            if (na < 1e-12 || nb < 1e-12) return 0f;
            return (float)(2.0 * Math.Acos(Math.Min(1.0, d / (na * nb))) * 180.0 / Math.PI);
        }

        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            double d = (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w;
            double s = 1.0;
            if (d < 0.0) { d = -d; s = -1.0; }
            double wa, wb;
            if (d > 0.9995) { wa = 1.0 - t; wb = t; }
            else
            {
                double th = Math.Acos(Math.Min(1.0, d)), sn = Math.Sin(th);
                wa = Math.Sin((1.0 - t) * th) / sn; wb = Math.Sin(t * th) / sn;
            }
            wb *= s;
            double x = wa * a.x + wb * b.x, y = wa * a.y + wb * b.y, z = wa * a.z + wb * b.z, w = wa * a.w + wb * b.w;
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (n < 1e-12) return b;
            return new Quaternion((float)(x / n), (float)(y / n), (float)(z / n), (float)(w / n));
        }

        // One limiter step of a scalar rate (m/s or deg/s) for `dist` left to go: accelerate and brake by vmax / ease per second
        // (ease 0 = at once) toward min(vmax, the speed that still stops at the goal); never faster than that stopping speed.
        public static float LimitRate(float dist, float v, float vmax, float easeSeconds, float dt)
        {
            if (vmax <= 0f) return float.PositiveInfinity;
            if (dt <= 0f) return v;
            float a = easeSeconds > 0f ? vmax / easeSeconds : float.PositiveInfinity;
            float stop = float.IsInfinity(a) ? vmax : (float)Math.Sqrt(2.0 * a * Math.Max(0f, dist));
            float want = Math.Min(vmax, stop);
            float nv = float.IsInfinity(a) ? want : Math.Max(v - a * dt, Math.Min(v + a * dt, want));
            return Math.Max(0f, Math.Min(nv, stop));
        }

        // The camera position after one limiter step toward `want` (speed kept in `speed`); jumps above MotionSnap are cuts.
        public static Vector3 LimitMove(Vector3 cur, Vector3 want, ref float speed, float maxSpeed, float easeSeconds, float dt)
        {
            var d = want - cur;
            float dist = d.magnitude;
            if (maxSpeed <= 0f || dist > MotionSnap) { speed = 0f; return want; }
            if (dt <= 0f) return cur;
            speed = LimitRate(dist, speed, maxSpeed, easeSeconds, dt);
            float step = speed * dt;
            if (step >= dist || dist < 1e-6f) { if (dist < 1e-6f) speed = 0f; return want; }
            return cur + d * (step / dist);
        }

        // The camera rotation after one limiter step toward `want` (turn rate in deg/s kept in `rate`).
        public static Quaternion LimitTurn(Quaternion cur, Quaternion want, ref float rate, float maxTurn, float easeSeconds, float dt)
        {
            if (maxTurn <= 0f) { rate = 0f; return want; }
            float ang = AngleDeg(cur, want);
            if (dt <= 0f) return cur;
            rate = LimitRate(ang, rate, maxTurn, easeSeconds, dt);
            float step = rate * dt;
            if (step >= ang || ang < 1e-4f) { if (ang < 1e-4f) rate = 0f; return want; }
            return Slerp(cur, want, step / ang);
        }
    }

    // Director shot timing shared by hide sets and blur scales (pure, tested offline in qa/replay CamPlanTests.cs): the same
    // rule as DirectorRig.Current (the latest `from` <= t, equal times: the later shot); `prev` = the shot on air before it
    // (-1 = none). HideNames: the camera's own `hide`; a director adds the shot on air (its `hide` and its source camera's) and,
    // inside a blend (pose flag B), the previous shot's (MULTICAM.md 6.4). `backfaces` entries (MULTICAM.md 6.5) travel in the
    // same list with the BackfaceMark prefix: the camera's own, and for a director the shot's source camera's.
    public static class MultiCamShots
    {
        public const string BackfaceMark = "backfaces: ";

        public static int At(CamSpec dir, double t, Func<TimeRef, double> timeOf, out int prev)
        {
            prev = -1;
            int cur = -1;
            if (dir == null || timeOf == null) return -1;
            int n = dir.shots.Count;
            var tf = new double[n];
            double best = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                var sh = dir.shots[i];
                tf[i] = sh == null ? double.NaN : (sh.from == null ? 0.0 : timeOf(sh.from));
                if (double.IsNaN(tf[i]) || t < tf[i] - 1e-6 || tf[i] < best) continue;
                best = tf[i]; cur = i;
            }
            if (cur < 0) return -1;
            double pb = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                if (i == cur || double.IsNaN(tf[i])) continue;
                bool before = tf[i] < tf[cur] - 1e-9 || (Math.Abs(tf[i] - tf[cur]) <= 1e-9 && i < cur);
                if (before && tf[i] >= pb) { pb = tf[i]; prev = i; }
            }
            return cur;
        }

        // Motion blur scale of camera i at t: motion.blurScale times the shot on air's motionBlur (directors).
        public static float BlurScale(MultiCamSpec spec, int i, double t, Func<TimeRef, double> timeOf)
        {
            if (spec == null || i < 0 || i >= spec.cameras.Count) return 1f;
            var c = spec.cameras[i];
            float k = c.motion != null ? c.motion.blurScale : 1f;
            if (c.mode == "director")
            {
                int prev;
                int s = At(c, t, timeOf, out prev);
                if (s >= 0 && c.shots[s] != null) k *= c.shots[s].motionBlur;
            }
            return Math.Max(0f, k);
        }

        public static string HideKey(List<string> names)
        {
            var l = new List<string>(names);
            l.Sort(StringComparer.Ordinal);
            return string.Join("\n", l.ToArray());
        }

        // Names hidden for camera i at time t (see the header); poseFlags of the camera's pose at t (blend = flag B).
        public static List<string> HideNames(MultiCamSpec spec, int i, double t, Func<TimeRef, double> timeOf, int poseFlags)
        {
            var r = new List<string>();
            if (spec == null || i < 0 || i >= spec.cameras.Count) return r;
            var c = spec.cameras[i];
            Add(r, c.hide);
            AddMarked(r, c.backfaces);
            if (c.mode != "director") return r;
            int prev;
            int k = At(c, t, timeOf, out prev);
            if (k >= 0) AddShot(spec, c, r, c.shots[k]);
            if (prev >= 0 && (poseFlags & PoseFlags.Blending) != 0) AddShot(spec, c, r, c.shots[prev]);
            return r;
        }

        public static bool AnyHide(MultiCamSpec spec, int i)
        {
            var c = spec.cameras[i];
            if (c.hide.Count > 0 || c.backfaces.Count > 0) return true;
            if (c.mode != "director") return false;
            foreach (var sh in c.shots)
            {
                if (sh == null) continue;
                if (sh.hide.Count > 0) return true;
                var src = spec.FindCamera(sh.cam);
                if (src != null && src != c && (src.hide.Count > 0 || src.backfaces.Count > 0)) return true;
            }
            return false;
        }

        static void AddShot(MultiCamSpec spec, CamSpec dir, List<string> r, ShotSpec sh)
        {
            if (sh == null) return;
            Add(r, sh.hide);
            var src = spec.FindCamera(sh.cam);
            if (src != null && src != dir) { Add(r, src.hide); AddMarked(r, src.backfaces); }
        }

        static void Add(List<string> r, List<string> l)
        {
            if (l == null) return;
            foreach (var n in l) if (!string.IsNullOrEmpty(n) && !r.Contains(n)) r.Add(n);
        }

        static void AddMarked(List<string> r, List<string> l)
        {
            if (l == null) return;
            foreach (var n in l) if (!string.IsNullOrEmpty(n) && !r.Contains(BackfaceMark + n)) r.Add(BackfaceMark + n);
        }
    }

    // Two-sided mesh copy for `backfaces` (pure, tested offline in qa/replay CamPlanTests.cs): every triangle whose three
    // vertices share a box bit (mask null = all; HideBoxes.Mask) gets a reversed twin (a, c, b) on duplicated vertices, each
    // source vertex duplicated once. dupOf[k] = the source vertex of new vertex vertexCount + k; tris[s] = submesh s with its twins appended. The copy
    // negates the normals of the duplicates and the w of their tangents (same bitangent on the back side).
    public static class MeshFlip
    {
        public const int MaxVertices = 65000;

        public static int Build(int[][] subTris, int[] mask, int vertexCount, out int[] dupOf, out int[][] tris)
        {
            var map = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++) map[i] = -1;
            var dup = new List<int>();
            tris = new int[subTris.Length][];
            int added = 0;
            for (int s = 0; s < subTris.Length; s++)
            {
                var src = subTris[s] ?? new int[0];
                var l = new List<int>(src.Length * 2);
                l.AddRange(src);
                for (int j = 0; j + 2 < src.Length; j += 3)
                {
                    int a = src[j], b = src[j + 1], c = src[j + 2];
                    if (mask != null && (mask[a] & mask[b] & mask[c]) == 0) continue;
                    l.Add(Dup(a, map, dup, vertexCount));
                    l.Add(Dup(c, map, dup, vertexCount));
                    l.Add(Dup(b, map, dup, vertexCount));
                    added++;
                }
                tris[s] = l.ToArray();
            }
            dupOf = dup.ToArray();
            return added;
        }

        static int Dup(int v, int[] map, List<int> dup, int vertexCount)
        {
            if (map[v] < 0) { map[v] = vertexCount + dup.Count; dup.Add(v); }
            return map[v];
        }

        public static T[] Extend<T>(T[] src, int[] dupOf, int vertexCount)
        {
            if (src == null || src.Length != vertexCount || dupOf == null || dupOf.Length == 0) return src;
            var r = new T[vertexCount + dupOf.Length];
            Array.Copy(src, r, vertexCount);
            for (int k = 0; k < dupOf.Length; k++) r[vertexCount + k] = src[dupOf[k]];
            return r;
        }

        public static Vector3[] BackNormals(Vector3[] n, int vertexCount)
        {
            if (n == null) return null;
            for (int k = vertexCount; k < n.Length; k++) n[k] = -n[k];
            return n;
        }

        public static Vector4[] BackTangents(Vector4[] t, int vertexCount)
        {
            if (t == null) return null;
            for (int k = vertexCount; k < t.Length; k++) t[k] = new Vector4(t[k].x, t[k].y, t[k].z, -t[k].w);
            return t;
        }
    }

    // Box part of `hide` / `backfaces` names (MULTICAM.md 6.4; pure, tested offline in qa/replay CamPlanTests.cs):
    // `<path> box=x0,y0,z0,x1,y1,z1[;x0,y0,z0,x1,y1,z1 ...]`, one to MaxBoxes world boxes. A triangle is cut (or flipped) when its
    // three vertices lie inside the same box: Mask gives each vertex one bit per box containing it, Keep drops the triangles whose
    // three masks share a bit. Key = the sorted distinct box texts, so the same boxes in any order (also from several entries on
    // one MeshFilter, merged by MultiCamHide.Resolve) share one cached copy.
    public static class HideBoxes
    {
        public const string Marker = " box=";
        public const int MaxBoxes = 31;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static bool Split(string name, out string path, out List<Vector3> mins, out List<Vector3> maxs, out string error)
        {
            path = name; mins = new List<Vector3>(); maxs = new List<Vector3>(); error = "";
            int k = name == null ? -1 : name.LastIndexOf(Marker, StringComparison.Ordinal);
            if (k < 0) return false;
            path = name.Substring(0, k).Trim();
            var boxes = name.Substring(k + Marker.Length).Split(';');
            if (boxes.Length > MaxBoxes) { error = "at most " + MaxBoxes.ToString(IC) + " boxes in one name"; return false; }
            foreach (var box in boxes)
            {
                var parts = box.Split(',');
                if (parts.Length != 6) { error = "box needs 6 numbers x0,y0,z0,x1,y1,z1 (several boxes: separate them with ';')"; return false; }
                var v = new float[6];
                for (int i = 0; i < 6; i++)
                {
                    if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, IC, out v[i])) { error = "box value '" + parts[i] + "' is not a number"; return false; }
                }
                mins.Add(new Vector3(Math.Min(v[0], v[3]), Math.Min(v[1], v[4]), Math.Min(v[2], v[5])));
                maxs.Add(new Vector3(Math.Max(v[0], v[3]), Math.Max(v[1], v[4]), Math.Max(v[2], v[5])));
            }
            return true;
        }

        public static string Text(Vector3 min, Vector3 max)
        {
            return "[" + min.x.ToString("0.###", IC) + ", " + min.y.ToString("0.###", IC) + ", " + min.z.ToString("0.###", IC) + " .. "
                + max.x.ToString("0.###", IC) + ", " + max.y.ToString("0.###", IC) + ", " + max.z.ToString("0.###", IC) + "]";
        }

        public static string Text(List<Vector3> mins, List<Vector3> maxs)
        {
            var l = new List<string>();
            for (int i = 0; i < mins.Count; i++) l.Add(Text(mins[i], maxs[i]));
            return string.Join(" + ", l.ToArray());
        }

        public static string Key(List<Vector3> mins, List<Vector3> maxs)
        {
            var l = new List<string>();
            for (int i = 0; i < mins.Count; i++) { var s = Text(mins[i], maxs[i]); if (!l.Contains(s)) l.Add(s); }
            l.Sort(StringComparer.Ordinal);
            return string.Join(";", l.ToArray());
        }

        // Adds the boxes not yet in the lists (same text = same box); returns the list index of each given box.
        public static int[] Merge(List<Vector3> mins, List<Vector3> maxs, List<Vector3> addMins, List<Vector3> addMaxs)
        {
            var at = new int[addMins.Count];
            for (int i = 0; i < addMins.Count; i++)
            {
                string s = Text(addMins[i], addMaxs[i]);
                int k = -1;
                for (int j = 0; j < mins.Count && k < 0; j++) if (Text(mins[j], maxs[j]) == s) k = j;
                if (k < 0 && mins.Count < MaxBoxes) { k = mins.Count; mins.Add(addMins[i]); maxs.Add(addMaxs[i]); }
                at[i] = k;
            }
            return at;
        }

        public static int Mask(Vector3 p, List<Vector3> mins, List<Vector3> maxs)
        {
            int m = 0;
            for (int b = 0; b < mins.Count && b < MaxBoxes; b++)
            {
                var lo = mins[b];
                var hi = maxs[b];
                if (p.x >= lo.x && p.x <= hi.x && p.y >= lo.y && p.y <= hi.y && p.z >= lo.z && p.z <= hi.z) m |= 1 << b;
            }
            return m;
        }

        // The triangles of one submesh a cut keeps; removed += the cut ones, perBox[b] += those inside box b.
        public static int[] Keep(int[] tris, int[] mask, int[] perBox, ref int removed, ref int total)
        {
            var keep = new List<int>(tris.Length);
            for (int j = 0; j + 2 < tris.Length; j += 3)
            {
                total++;
                int m = mask[tris[j]] & mask[tris[j + 1]] & mask[tris[j + 2]];
                if (m != 0)
                {
                    removed++;
                    if (perBox != null) for (int b = 0; b < perBox.Length; b++) if ((m & (1 << b)) != 0) perBox[b]++;
                    continue;
                }
                keep.Add(tris[j]); keep.Add(tris[j + 1]); keep.Add(tris[j + 2]);
            }
            return keep.ToArray();
        }
    }

    // ------------------------------------------------------------------ JSON -> spec binding

    public static class SpecBind
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();

        public static FieldInfo[] Fields(Type t)
        {
            FieldInfo[] r;
            if (fieldCache.TryGetValue(t, out r)) return r;
            var list = new List<FieldInfo>();
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (!f.IsInitOnly && f.GetCustomAttributes(typeof(SpecIgnoreAttribute), false).Length == 0) list.Add(f);
            r = list.ToArray();
            fieldCache[t] = r;
            return r;
        }

        public static FieldInfo Field(Type t, string key)
        {
            foreach (var f in Fields(t)) if (f.Name == key) return f;
            return null;
        }

        public static T Read<T>(object value, string where, SpecReport report) where T : SpecNode, new()
        {
            object r;
            return ConvertNode(value, typeof(T), where, report, out r) ? (T)r : null;
        }

        public static void Fill(SpecNode node, Json.Obj o, string where, SpecReport report)
        {
            node.where = where ?? "";
            var t = node.GetType();
            foreach (var k in o.order)
            {
                if (k.Length == 0 || k[0] == '_' || k == "comment") continue;
                string at = string.IsNullOrEmpty(where) ? k : where + "." + k;
                var f = Field(t, k);
                if (f == null)
                {
                    string near = Nearest(t, k);
                    report.Warn(at, "unknown key, ignored" + (near != null ? " (did you mean '" + near + "'?)" : ""));
                    continue;
                }
                object v;
                if (!Convert(o[k], f.FieldType, at, report, out v)) continue;
                f.SetValue(node, v);
                if (!node.given.Contains(k)) node.given.Add(k);
            }
        }

        public static bool Convert(object v, Type t, string where, SpecReport report, out object result)
        {
            result = null;
            if (v == null) return false;
            if (t == typeof(object)) { result = v; return true; }
            if (t == typeof(Json.Obj))
            {
                result = v as Json.Obj;
                if (result != null) return true;
                report.Error(where, "object expected");
                return false;
            }
            if (t == typeof(string))
            {
                if (v is string) { result = v; return true; }
                if (v is double || v is bool) { result = System.Convert.ToString(v, IC); return true; }
                report.Error(where, "string expected");
                return false;
            }
            if (t == typeof(bool))
            {
                if (v is bool) { result = v; return true; }
                report.Error(where, "true or false expected");
                return false;
            }
            if (t == typeof(double) || t == typeof(float) || t == typeof(int))
            {
                if (!(v is double)) { report.Error(where, "number expected"); return false; }
                double d = (double)v;
                if (t == typeof(double)) { result = d; return true; }
                if (t == typeof(float)) { result = (float)d; return true; }
                if (Math.Abs(d - Math.Round(d)) > 1e-9 || Math.Abs(d) > int.MaxValue) { report.Error(where, "integer expected"); return false; }
                result = (int)Math.Round(d);
                return true;
            }
            if (t == typeof(Vector3))
            {
                Vector3 p;
                if (Vec3(v as List<object>, out p)) { result = p; return true; }
                report.Error(where, "[x, y, z] expected");
                return false;
            }
            if (t == typeof(Vector2))
            {
                Vector2 p;
                if (Vec2(v as List<object>, out p)) { result = p; return true; }
                report.Error(where, "[a, b] expected");
                return false;
            }
            if (t == typeof(float[]))
            {
                var l = v as List<object>;
                if (l == null) { report.Error(where, "list of numbers expected"); return false; }
                var a = new float[l.Count];
                for (int i = 0; i < l.Count; i++)
                {
                    if (!(l[i] is double)) { report.Error(where + "[" + i + "]", "number expected"); return false; }
                    a[i] = (float)(double)l[i];
                }
                result = a;
                return true;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return ConvertList(v, t, where, report, out result);
            if (typeof(SpecNode).IsAssignableFrom(t) || typeof(IJsonValue).IsAssignableFrom(t)) return ConvertNode(v, t, where, report, out result);
            report.Error(where, "unsupported field type " + t.Name);
            return false;
        }

        static bool ConvertNode(object v, Type t, string where, SpecReport report, out object result)
        {
            result = null;
            var inst = Activator.CreateInstance(t);
            var jv = inst as IJsonValue;
            if (jv != null)
            {
                if (!jv.Read(v, where, report)) return false;
                result = inst;
                return true;
            }
            var o = v as Json.Obj;
            if (o == null) { report.Error(where, "object expected"); return false; }
            Fill((SpecNode)inst, o, where, report);
            result = inst;
            return true;
        }

        static bool ConvertList(object v, Type t, string where, SpecReport report, out object result)
        {
            result = null;
            var et = t.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(t);
            var src = v as List<object>;
            var map = v as Json.Obj;
            var nameField = typeof(SpecNode).IsAssignableFrom(et) && !typeof(IJsonValue).IsAssignableFrom(et) ? Field(et, "name") : null;
            if (src == null && map != null && nameField != null)
            {
                foreach (var k in map.order)
                {
                    if (k.Length == 0 || k[0] == '_' || k == "comment") continue;
                    object e;
                    if (!ConvertNode(map[k], et, where + "." + k, report, out e)) continue;
                    var sn = (SpecNode)e;
                    if (!sn.Has("name")) { nameField.SetValue(e, k); sn.given.Add("name"); }
                    list.Add(e);
                }
                result = list;
                return true;
            }
            if (src == null)
            {
                if (et == typeof(string) || et == typeof(TargetRef))
                {
                    object one;
                    if (!Convert(v, et, where, report, out one)) return false;
                    list.Add(one);
                    result = list;
                    return true;
                }
                report.Error(where, "list expected");
                return false;
            }
            for (int i = 0; i < src.Count; i++)
            {
                object e;
                if (Convert(src[i], et, where + "[" + i + "]", report, out e)) list.Add(e);
            }
            result = list;
            return true;
        }

        public static bool Vec3(List<object> l, out Vector3 v)
        {
            v = new Vector3(0f, 0f, 0f);
            if (l == null || l.Count != 3) return false;
            for (int i = 0; i < 3; i++) if (!(l[i] is double)) return false;
            v = new Vector3((float)(double)l[0], (float)(double)l[1], (float)(double)l[2]);
            return true;
        }

        public static bool Vec2(List<object> l, out Vector2 v)
        {
            v = new Vector2(0f, 0f);
            if (l == null || l.Count != 2) return false;
            for (int i = 0; i < 2; i++) if (!(l[i] is double)) return false;
            v = new Vector2((float)(double)l[0], (float)(double)l[1]);
            return true;
        }

        public static T Overlay<T>(T under, T over) where T : SpecNode, new()
        {
            if (under == null && over == null) return null;
            var r = new T();
            var fields = Fields(typeof(T));
            if (under != null)
            {
                foreach (var f in fields) f.SetValue(r, f.GetValue(under));
                r.given.AddRange(under.given);
                r.where = under.where;
            }
            if (over != null)
            {
                foreach (var k in over.given)
                {
                    var f = Field(typeof(T), k);
                    if (f == null) continue;
                    f.SetValue(r, f.GetValue(over));
                    if (!r.given.Contains(k)) r.given.Add(k);
                }
                r.where = over.where;
            }
            return r;
        }

        public static void Walk(object node, string where, Action<object, string> visit)
        {
            if (node == null) return;
            visit(node, where);
            if (!(node is SpecNode)) return;
            foreach (var f in Fields(node.GetType()))
            {
                var v = f.GetValue(node);
                if (v == null) continue;
                string at = string.IsNullOrEmpty(where) ? f.Name : where + "." + f.Name;
                if (v is SpecNode || v is IJsonValue) { Walk(v, at, visit); continue; }
                if (v is string || v is float[]) continue;
                var l = v as IList;
                if (l == null) continue;
                for (int i = 0; i < l.Count; i++)
                {
                    var e = l[i];
                    if (e is SpecNode || e is IJsonValue) Walk(e, at + "[" + i + "]", visit);
                }
            }
        }

        public static string Schema(Type t)
        {
            var sb = new StringBuilder();
            Schema(sb, t, "", 0, new List<Type>());
            return sb.ToString();
        }

        static void Schema(StringBuilder sb, Type t, string prefix, int depth, List<Type> seen)
        {
            object inst = Activator.CreateInstance(t);
            foreach (var f in Fields(t))
            {
                sb.Append(prefix).Append(f.Name).Append(" : ").Append(TypeName(f.FieldType)).Append(" = ").Append(DefaultText(f.GetValue(inst))).Append('\n');
                bool isList = f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(List<>);
                var nt = isList ? f.FieldType.GetGenericArguments()[0] : f.FieldType;
                if (depth < 4 && typeof(SpecNode).IsAssignableFrom(nt) && !seen.Contains(nt))
                {
                    seen.Add(nt);
                    Schema(sb, nt, prefix + f.Name + (isList ? "[]." : "."), depth + 1, seen);
                }
            }
        }

        static string TypeName(Type t)
        {
            if (t == typeof(float) || t == typeof(double)) return "number";
            if (t == typeof(int)) return "int";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string";
            if (t == typeof(Vector3)) return "[x,y,z]";
            if (t == typeof(Vector2)) return "[a,b]";
            if (t == typeof(float[])) return "number[]";
            if (t == typeof(TimeRef)) return "time";
            if (t == typeof(TargetRef)) return "target";
            if (t == typeof(Json.Obj)) return "object";
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return TypeName(t.GetGenericArguments()[0]) + "[]";
            return t.Name;
        }

        static string DefaultText(object v)
        {
            if (v == null) return "null";
            if (v is string) return "\"" + v + "\"";
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is float) return float.IsNaN((float)v) ? "unset" : ((float)v).ToString("0.###", IC);
            if (v is double) return double.IsNaN((double)v) ? "unset" : ((double)v).ToString("0.###", IC);
            if (v is int) return ((int)v).ToString(IC);
            if (v is Vector3) { var p = (Vector3)v; return "[" + p.x.ToString("0.###", IC) + "," + p.y.ToString("0.###", IC) + "," + p.z.ToString("0.###", IC) + "]"; }
            if (v is Vector2) { var p = (Vector2)v; return "[" + p.x.ToString("0.###", IC) + "," + p.y.ToString("0.###", IC) + "]"; }
            if (v is TimeRef) return v.ToString();
            var l = v as IList;
            if (l != null) return l.Count == 0 ? "[]" : "[" + l.Count + " items]";
            return "{}";
        }

        static string Nearest(Type t, string key)
        {
            string best = null; int bd = 3;
            foreach (var f in Fields(t))
            {
                if (string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase)) return f.Name;
                int d = Distance(f.Name.ToLowerInvariant(), key.ToLowerInvariant());
                if (d < bd) { bd = d; best = f.Name; }
            }
            return best;
        }

        static int Distance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var tmp = prev; prev = cur; cur = tmp;
            }
            return prev[b.Length];
        }
    }
}
