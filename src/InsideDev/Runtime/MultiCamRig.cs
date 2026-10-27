using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace InsideDev
{
    // MultiCam v2 camera behaviours (RIG). Design: docs/MULTICAM_DESIGN.md sections 3.8 and 8.
    // Modes: game, static, follow, keys, rail, orbit, gamerel, gamelike, frame, director. Every rig is evaluated once per step
    // (captured or not) in EvalOrder, works on world-space poses only (never touches the real camera), handles dt = 0 and
    // unresolved event times (hold + Waiting), and returns ok = false only when it has no pose at all. Shared layers: look-at
    // with critically damped smoothing and look-ahead, player-side clamp, shake (live game handshake / impact shake, or a
    // replica sampled from the game's own noise clips on game or output time), optional screen-space framing ("screen") for
    // look-at modes. SampleGame snapshots CameraScript per step.
    // Replay (REPLAY_DESIGN 8.3 RIG, while ReplayHub.replaying): SampleGame never reads the live camera or CameraScript, it
    // fills ctx.game from IReplaySource.GameCam (CORE normally does that itself and skips SampleGame); every rig reads game
    // state only through ctx.game and the targets (CameraScript is read for static config only: smoothing curves, probe
    // defaults, noise clips); gamelike anchored on the game probe takes its box from the recorded ctx.game.box. Rig Begin
    // restarts the shake replica's clip probe, so a plan pass evaluated twice gives the same poses.
    public static class MultiCamRig
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly string[] Common = { "name", "mode", "label", "enabled", "fov", "near", "focus", "roll", "playerSide", "zMax", "render", "post", "shake", "hide", "clamp", "view", "motion" };
        static readonly Dictionary<string, string[]> ModeKeys = new Dictionary<string, string[]>
        {
            { "game", new[] { "offset", "fovScale", "clean" } },
            { "static", new[] { "pos", "yaw", "pitch", "look", "lookOffset", "lookSmooth", "target", "point", "screen" } },
            { "follow", new[] { "target", "point", "offset", "look", "lookOffset", "local", "smooth", "lookSmooth", "lag", "lead", "planar", "start", "yaw", "pitch", "screen" } },
            { "keys", new[] { "keys", "interp", "ease", "start", "look", "lookOffset", "lookSmooth", "target", "point", "screen" } },
            { "rail", new[] { "path", "duration", "interp", "ease", "start", "look", "lookOffset", "lookSmooth", "yaw", "pitch", "target", "point", "screen" } },
            { "orbit", new[] { "target", "point", "offset", "radius", "height", "azimuth", "duration", "ease", "start", "look", "lookOffset", "lookSmooth", "yaw", "pitch", "screen" } },
            { "gamerel", new[] { "orbitYaw", "orbitPitch", "dist", "distScale", "shift", "keepSize", "clean" } },
            { "gamelike", new[] { "target", "point", "box", "camOffset", "aimOffset", "extrapolate", "lag", "smooth", "planar", "lookOffset" } },
            { "frame", new[] { "target", "point", "screen", "size", "subjectHeight", "crop", "smooth", "lead", "yaw", "pitch" } },
            { "director", new[] { "shots", "cutAngle", "cutDistance", "minHold" } },
            { "overview", new[] { "target", "point", "pos", "yaw", "pitch", "offset", "smooth", "lead", "pans", "start" } },
            { "events", new[] { "beats", "target", "point", "offset", "offsets", "lookOffset", "lookSmooth", "smooth", "hold", "move", "late", "ease", "start" } },
        };
        static readonly string[] BeatEases = { "ends", "smooth", "smoother", "linear", "sine" };
        static readonly string[] Lags = { "damp", "game", "none" };
        static readonly string[] Interps = { "catmull", "linear" };
        static readonly string[] KeyEases = { "ends", "keys", "each", "linear" };
        static readonly string[] PathEases = { "ends", "linear" };
        static readonly string[] ShotEases = { "smooth", "smoother", "linear", "sine" };
        static readonly string[] Transitions = { "orbit", "linear", "cut" };
        static readonly string[] Clocks = { "game", "output" };
        static readonly string[] FramedModes = { "static", "follow", "keys", "rail", "orbit" };
        const float EndsFraction = 0.2f;
        static readonly MotionSpec DefaultMotion = new MotionSpec();
        static readonly MotionSpec NoMotion = new MotionSpec { maxSpeed = 0f, maxTurn = 0f, ease = 0f };

        static MultiCamContext sampledCtx;
        static int sampledStep = int.MinValue;
        static Vector3 sampledCamera;
        static Camera cachedCam;
        static CameraScript cachedScript;

        public static ICamRig Create(CamSpec spec)
        {
            switch (spec != null ? spec.mode : "")
            {
                case "game": return new GameRig(spec);
                case "static": return new StaticRig(spec);
                case "follow": return new FollowRig(spec);
                case "keys": return new KeysRig(spec);
                case "rail": return new RailRig(spec);
                case "orbit": return new OrbitRig(spec);
                case "gamerel": return new GameRelRig(spec);
                case "gamelike": return new GameLikeRig(spec);
                case "frame": return new FrameRig(spec);
                case "director": return new DirectorRig(spec);
                case "overview": return new OverviewRig(spec);
                case "events": return new EventsRig(spec);
            }
            return new NullRig(spec ?? new CamSpec());
        }

        // ------------------------------------------------------------------ game camera snapshot

        public static void SampleGame(Camera cam, MultiCamContext ctx)
        {
            var g = ctx.game;
            if (ReplayHub.replaying)
            {
                bool rok;
                try { rok = ReplayHub.source.GameCam(ctx.t, g) && g.ok; } catch { rok = false; }
                if (!rok) { g.ok = false; g.cut = false; }
                g.step = ctx.step;
                return;
            }
            g.step = ctx.step;
            g.ok = cam != null;
            g.cut = false;
            if (cam == null) return;
            var tr = cam.transform;
            float fov = cam.fieldOfView, near = cam.nearClipPlane;
            Vector3 pc = tr.position;
            Quaternion rc = tr.rotation;
            CameraScript cs = null;
            try
            {
                if (cam != cachedCam || cachedScript == null) { cachedCam = cam; cachedScript = cam.GetComponent<CameraScript>(); }
                cs = cachedScript != null ? cachedScript : CameraScript.main;
            }
            catch { cs = null; }
            Vector3 cleanP = pc;
            Quaternion cleanR = rc;
            g.handOffset = Vector3.zero; g.handRot = Quaternion.identity;
            g.shakeOffset = Vector3.zero; g.shakeRot = Quaternion.identity;
            g.box = "";
            if (cs != null)
            {
                try
                {
                    g.positionCamera = cs.positionCamera;
                    g.positionAim = cs.positionAim;
                    g.positionUp = cs.positionUp.sqrMagnitude > 1e-6f ? cs.positionUp : Vector3.up;
                    g.velocity = cs.cameraVelocity;
                    float ad = (g.positionAim - g.positionCamera).magnitude;
                    g.aimDistance = ad > 0.05f ? ad : MultiCamConst.GameAimDistance;
                    cleanP = cs.interpolatedPosition;
                    cleanR = cs.interpolatedRotation;
                    if (cleanR.x == 0f && cleanR.y == 0f && cleanR.z == 0f && cleanR.w == 0f) cleanR = rc;
                }
                catch { cleanP = pc; cleanR = rc; }
                try
                {
                    var box = cs.GetActiveCameraModifier() as CameraBlendBox;
                    g.box = box != null ? box.name : "";
                }
                catch { g.box = ""; }
                try
                {
                    var pr = cs.cameraBlendProbe;
                    if (pr != null) { g.probe = pr.worldPosition; g.extrapolation = pr.extrapolationValue; }
                }
                catch { }
                try
                {
                    if (cs.bHandshakeEffect && cs.cameraHandshake != null)
                    {
                        g.handOffset = Quaternion.Inverse(cleanR) * cs.cameraHandshake.GetMovement();
                        g.handRot = cs.cameraHandshake.GetRotation();
                    }
                }
                catch { g.handOffset = Vector3.zero; g.handRot = Quaternion.identity; }
                try
                {
                    if (cs.bCustomEffects && cs.cameraShake != null)
                    {
                        g.shakeOffset = Quaternion.Inverse(cleanR) * cs.cameraShake.GetMovement();
                        g.shakeRot = cs.cameraShake.GetRotation();
                    }
                }
                catch { g.shakeOffset = Vector3.zero; g.shakeRot = Quaternion.identity; }
            }
            else
            {
                g.positionCamera = pc;
                g.positionAim = pc + rc * Vector3.forward * MultiCamConst.GameAimDistance;
                g.positionUp = rc * Vector3.up;
                g.velocity = Vector3.zero;
                g.aimDistance = MultiCamConst.GameAimDistance;
            }
            g.rendered = CamPose.Make(pc, rc, fov, g.aimDistance);
            g.rendered.near = near;
            g.clean = CamPose.Make(cleanP, cleanR, fov, g.aimDistance);
            g.clean.near = near;
            if (sampledCtx == ctx && ctx.step > sampledStep && (g.positionCamera - sampledCamera).magnitude > 2f) g.cut = true;
            sampledCtx = ctx;
            sampledStep = ctx.step;
            sampledCamera = g.positionCamera;
        }

        // ------------------------------------------------------------------ evaluation order

        public static List<int> EvalOrder(MultiCamSpec spec, SpecReport report)
        {
            var order = new List<int>();
            int n = spec.cameras.Count;
            var placed = new bool[n];
            var index = new Dictionary<string, int>();
            for (int i = 0; i < n; i++) { var c = spec.cameras[i]; if (c != null && !string.IsNullOrEmpty(c.name) && !index.ContainsKey(c.name)) index[c.name] = i; }
            bool progress = true;
            while (order.Count < n && progress)
            {
                progress = false;
                for (int i = 0; i < n; i++)
                {
                    if (placed[i]) continue;
                    var c = spec.cameras[i];
                    bool ready = true;
                    if (c != null && c.mode == "director")
                        foreach (var sh in c.shots)
                        {
                            int j;
                            if (sh != null && index.TryGetValue(sh.cam ?? "", out j) && j != i && !placed[j]) { ready = false; break; }
                        }
                    if (!ready) continue;
                    placed[i] = true;
                    order.Add(i);
                    progress = true;
                }
            }
            if (order.Count < n)
            {
                var rest = new List<string>();
                for (int i = 0; i < n; i++) if (!placed[i]) { order.Add(i); rest.Add(spec.cameras[i] != null ? spec.cameras[i].name : "#" + i.ToString(IC)); }
                if (report != null) report.Warn("cameras", "director cycle among " + string.Join(", ", rest.ToArray()) + "; evaluated in spec order (their sources may lag one step)");
            }
            return order;
        }

        // ------------------------------------------------------------------ validation (4.13 mode and range lint)

        public static void Validate(MultiCamSpec spec, SpecReport report)
        {
            if (spec == null || report == null) return;
            var names = new HashSet<string>();
            foreach (var c in spec.cameras) if (c != null && !string.IsNullOrEmpty(c.name)) names.Add(c.name);
            foreach (var c in spec.cameras)
            {
                if (c == null) continue;
                try { ValidateCamera(spec, c, names, report); }
                catch (Exception e) { report.Error(Where(c), "validation failed: " + e.GetType().Name + ": " + e.Message); }
            }
            Cycles(spec, report);
        }

        static string Where(CamSpec c) { return !string.IsNullOrEmpty(c.where) ? c.where : "cameras." + c.name; }

        static string F(float v) { return v.ToString("0.##", IC); }

        static void OneOf(string where, string key, string value, string[] allowed, SpecReport report)
        {
            if (Array.IndexOf(allowed, value) < 0) report.Error(where + "." + key, "unknown value '" + value + "' (" + string.Join(", ", allowed) + ")");
        }

        static void ValidateCamera(MultiCamSpec spec, CamSpec c, HashSet<string> names, SpecReport report)
        {
            string w = Where(c);
            if (Array.IndexOf(MultiCamConst.Modes, c.mode) < 0) { report.Error(w + ".mode", "unknown mode '" + c.mode + "' (" + string.Join(", ", MultiCamConst.Modes) + ")"); return; }
            string[] mk = ModeKeys[c.mode];
            foreach (var k in c.given)
            {
                if (Array.IndexOf(Common, k) >= 0 || Array.IndexOf(mk, k) >= 0) continue;
                if (spec.defaults != null && spec.defaults.Has(k)) continue;
                report.Warn(w + "." + k, "ignored by mode '" + c.mode + "'");
            }
            if (c.fov > 60f) report.Warn(w + ".fov", "fov " + F(c.fov) + " > 60 (the game uses 50.99)");
            else if (c.fov > 51.5f) report.Note(w + ".fov", "fov " + F(c.fov) + " is wider than the game's 50.99");
            if (c.near < 0f) report.Error(w + ".near", "near must be >= 0");
            if (c.playerSide && c.zMax > 0f) report.Warn(w + ".zMax", "zMax " + F(c.zMax) + " > 0 lets the camera behind the set front");
            if (c.mode == "director" && c.shake != null && c.Has("shake")) report.Warn(w + ".shake", "a director inherits shake from its sources; ignored");
            if (c.shake != null) OneOf(w + ".shake", "clock", c.shake.clock, Clocks, report);
            if (c.clamp != null) ValidateClamp(c, w, report);
            if (c.view != null && (c.view.margin < 0f || c.view.subjectRadius < 0f || c.view.every < 0f)) report.Error(w + ".view", "margin, subjectRadius and every must be >= 0");
            if (c.motion != null)
            {
                var m = c.motion;
                if (m.maxSpeed < 0f || m.maxTurn < 0f || m.ease < 0f || m.blurScale < 0f) report.Error(w + ".motion", "maxSpeed, maxTurn, ease and blurScale must be >= 0");
                if (c.mode == "game") report.Warn(w + ".motion", "the game camera is never limited (only blurScale applies)");
                if (m.blurScale > 2f) report.Warn(w + ".motion.blurScale", "blurScale " + F(m.blurScale) + " > 2 smears more than the game's own blur");
            }
            switch (c.mode)
            {
                case "game":
                    if (c.fovScale <= 0f) report.Error(w + ".fovScale", "fovScale must be > 0");
                    break;
                case "static": ValidateStatic(c, w, report); break;
                case "follow":
                    if (c.target == null) report.Error(w + ".target", "follow needs a target");
                    OneOf(w, "lag", c.lag, Lags, report);
                    if (c.smooth < 0f || c.lookSmooth < 0f) report.Error(w, "smooth and lookSmooth must be >= 0");
                    if (c.playerSide && c.offset.z > c.zMax && !c.local) report.Warn(w + ".offset", "offset z " + F(c.offset.z) + " > zMax: with a subject near z = 0 the camera is clamped to the player-side line");
                    if (c.look != null && c.look.isNone) Euler(w, c.yaw, c.pitch, c.playerSide, report);
                    break;
                case "keys": ValidateKeys(c, w, report); break;
                case "rail":
                    if (c.path.Count < 2) report.Error(w + ".path", "rail needs at least 2 points");
                    if (c.duration <= 0f) report.Error(w + ".duration", "rail needs duration > 0");
                    OneOf(w, "interp", c.interp, Interps, report);
                    OneOf(w, "ease", c.ease, PathEases, report);
                    for (int i = 0; i < c.path.Count; i++)
                        if (c.playerSide && c.path[i].z > c.zMax) report.Warn(w + ".path[" + i.ToString(IC) + "]", "point z " + F(c.path[i].z) + " is behind the player-side line (clamped)");
                    if (!HasLook(c)) Euler(w, c.yaw, c.pitch, c.playerSide, report);
                    break;
                case "orbit":
                    if (c.target == null) report.Error(w + ".target", "orbit needs a target (centre)");
                    if (c.radius <= 0f) report.Error(w + ".radius", "radius must be > 0");
                    if (c.duration < 0f) report.Error(w + ".duration", "duration must be >= 0");
                    OneOf(w, "ease", c.ease, PathEases, report);
                    if (Mathf.Abs(c.azimuth.x) > 60f || Mathf.Abs(c.azimuth.y) > 60f) report.Warn(w + ".azimuth", "azimuth beyond +-60 deg swings toward the side of the set");
                    if (c.target != null && c.target.fixedPoint && c.playerSide)
                        for (int i = 0; i <= 4; i++)
                        {
                            float az = Mathf.Lerp(c.azimuth.x, c.azimuth.y, i / 4f) * Mathf.Deg2Rad;
                            var p = c.target.pos + c.target.offset + c.offset + new Vector3(Mathf.Sin(az) * c.radius, c.height, -Mathf.Cos(az) * c.radius);
                            if (p.z > c.zMax) { report.Warn(w, "orbit passes behind the player-side line (clamped)"); break; }
                        }
                    break;
                case "gamerel":
                    if (Mathf.Abs(c.orbitYaw) > 60f) report.Warn(w + ".orbitYaw", "orbitYaw beyond +-60 deg swings toward the side of the set");
                    if (c.distScale <= 0f) report.Error(w + ".distScale", "distScale must be > 0");
                    if (c.dist < 0f) report.Error(w + ".dist", "dist must be >= 0");
                    break;
                case "gamelike":
                    OneOf(w, "lag", c.Has("lag") ? c.lag : "game", Lags, report);
                    if (c.target == null) report.Note(w + ".target", "no target: anchored on the game's own probe (main character)");
                    break;
                case "frame":
                    if (c.target == null) report.Error(w + ".target", "frame needs a target");
                    if (c.crop != null && (c.crop.Length != 4 || c.crop[2] <= 0f || c.crop[3] <= 0f)) report.Error(w + ".crop", "crop must be [x, y, w, h] with w, h > 0");
                    if (c.size <= 0f) report.Error(w + ".size", "size must be > 0");
                    else if (c.size > 1f) report.Warn(w + ".size", "size > 1: the subject is taller than the frame");
                    if (c.screen.x < 0f || c.screen.x > 1f || c.screen.y < 0f || c.screen.y > 1f) report.Warn(w + ".screen", "screen point outside [0, 1]");
                    Euler(w, c.yaw, c.pitch, c.playerSide, report);
                    break;
                case "director": ValidateDirector(c, w, names, report); break;
                case "overview":
                    if (!c.Has("pos")) report.Error(w + ".pos", "overview needs pos (camera y and z; x is the start)");
                    if (c.target == null) report.Note(w + ".target", "no target: the frame stays at pos.x until a pan moves it");
                    if (c.smooth < 0f) report.Error(w + ".smooth", "smooth must be >= 0");
                    if (c.playerSide && c.pos.z > c.zMax && c.pitch < 45f) report.Error(w + ".pos", "position z " + F(c.pos.z) + " is behind the player-side line z = " + F(c.zMax));
                    Euler(w, c.yaw, c.pitch, c.playerSide, report);
                    for (int i = 0; i < c.pans.Count; i++)
                    {
                        var pn = c.pans[i];
                        if (pn == null) continue;
                        string pw = w + ".pans[" + i.ToString(IC) + "]";
                        if (pn.duration < 0f) report.Error(pw + ".duration", "duration must be >= 0");
                        OneOf(pw, "ease", pn.ease, ShotEases, report);
                    }
                    break;
                case "events":
                    if (c.beats.Count == 0) report.Error(w + ".beats", "events needs at least one beat");
                    if (c.hold < 0f || c.move < 0f || c.late < 0f) report.Error(w, "hold, move and late must be >= 0");
                    if (c.smooth < 0f) report.Error(w + ".smooth", "smooth must be >= 0");
                    OneOf(w, "ease", c.ease, BeatEases, report);
                    for (int i = 0; i < c.beats.Count; i++)
                    {
                        var b = c.beats[i];
                        if (b == null) continue;
                        string bw = w + ".beats[" + i.ToString(IC) + "]";
                        if (b.target == null && c.target == null) report.Error(bw + ".target", "beat needs a target");
                        if (b.hold < 0f || b.move < 0f) report.Error(bw, "hold and move must be >= 0");
                        var offs = b.offsets.Count > 0 ? b.offsets : b.Has("offset") ? new List<Vector3> { b.offset } : c.offsets.Count > 0 ? c.offsets : new List<Vector3> { c.offset };
                        bool front = false;
                        foreach (var o in offs) if (o.z < -1f) front = true;
                        if (c.playerSide && !front) report.Warn(bw + ".offsets", "no candidate offset has z < -1: with a subject near z = 0 the camera ends on the player-side line (clamped)");
                    }
                    break;
            }
        }

        static void ValidateClamp(CamSpec c, string w, SpecReport report)
        {
            var k = c.clamp;
            if (!k.Has("left") && !k.Has("right")) report.Warn(w + ".clamp", "neither left nor right given: no edge is clamped");
            if (k.Has("left") && k.Has("right") && k.left >= k.right) report.Error(w + ".clamp", "left " + F(k.left) + " must be below right " + F(k.right));
            if (k.aspect < 0f) report.Error(w + ".clamp.aspect", "aspect must be >= 0 (0 = the frame's own)");
        }

        static bool HasLook(CamSpec c) { return c.look != null && !c.look.isNone && !(c.look.isSelf && c.target == null); }

        static Vector3 Forward(float yaw, float pitch)
        {
            float y = yaw * Mathf.Deg2Rad, p = pitch * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(y) * Mathf.Cos(p), -Mathf.Sin(p), Mathf.Cos(y) * Mathf.Cos(p));
        }

        static void Euler(string w, float yaw, float pitch, bool playerSide, SpecReport report)
        {
            var f = Forward(yaw, pitch);
            if (playerSide && f.z < -0.05f && pitch < 60f) report.Error(w, "looks back toward the screen (yaw " + F(yaw) + ", pitch " + F(pitch) + "); the sets have no back side");
            float y = Mathf.DeltaAngle(0f, yaw);
            if (Mathf.Abs(y) > 40f) report.Warn(w + ".yaw", "yaw " + F(y) + " beyond +-40 deg");
            if (pitch < -20f || pitch > 35f) report.Warn(w + ".pitch", "pitch " + F(pitch) + " outside [-20, 35] (positive looks down)");
        }

        static void ValidateStatic(CamSpec c, string w, SpecReport report)
        {
            if (!HasLook(c))
            {
                Euler(w, c.yaw, c.pitch, c.playerSide, report);
                if (c.playerSide && c.pos.z > c.zMax && c.pitch < 45f) report.Error(w + ".pos", "position z " + F(c.pos.z) + " is behind the player-side line z = " + F(c.zMax) + " (only overhead shots, pitch >= 45, may be there)");
                return;
            }
            var lr = c.look.isSelf ? c.target : c.look;
            if (lr != null && lr.fixedPoint)
            {
                var a = lr.pos + lr.offset + c.lookOffset;
                var d = a - c.pos;
                if (d.magnitude < 1.5f && c.near <= 0f) report.Warn(w + ".near", "look point " + F(d.magnitude) + " m away with the game near clip 1.0; set near (>= 0.3)");
                if (d.sqrMagnitude > 1e-6f)
                {
                    var f = d.normalized;
                    float pitch = -Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
                    if (c.playerSide && f.z < -0.05f && pitch < 60f) report.Error(w + ".look", "looks back toward the screen; the sets have no back side");
                    if (c.playerSide && c.pos.z > c.zMax && pitch < 45f) report.Error(w + ".pos", "position z " + F(c.pos.z) + " is behind the player-side line z = " + F(c.zMax));
                }
                return;
            }
            if (c.playerSide && c.pos.z > c.zMax) report.Warn(w + ".pos", "position z " + F(c.pos.z) + " is behind the player-side line; allowed only for overhead shots (pitch >= 45), otherwise clamped");
        }

        static void ValidateKeys(CamSpec c, string w, SpecReport report)
        {
            OneOf(w, "interp", c.interp, Interps, report);
            OneOf(w, "ease", c.ease, KeyEases, report);
            if (c.keys.Count == 0) { report.Error(w + ".keys", "keys mode needs at least one key"); return; }
            bool camLook = HasLook(c);
            for (int i = 0; i < c.keys.Count; i++)
            {
                var k = c.keys[i];
                if (k == null) continue;
                string kw = w + ".keys[" + i.ToString(IC) + "]";
                if (i > 0 && c.keys[i - 1] != null)
                {
                    var p = c.keys[i - 1];
                    if (k.t < p.t) report.Error(kw + ".t", "keys are not sorted by t (" + F(p.t) + " then " + F(k.t) + ")");
                    else if (k.t == p.t) report.Warn(kw + ".t", "two keys at t " + F(k.t));
                    if (c.ease == "ends" && (k.pos - p.pos).sqrMagnitude < 1e-6f && (k.yaw != p.yaw || k.pitch != p.pitch || k.look != null || p.look != null))
                        report.Warn(kw, "same position as the previous key: with ease 'ends' the turn between them is instant (use ease 'keys')");
                }
                if (c.playerSide && k.pos.z > c.zMax) report.Warn(kw + ".pos", "key z " + F(k.pos.z) + " is behind the player-side line (clamped unless overhead)");
                bool keyLook = k.look != null ? !k.look.isNone : camLook;
                if (!keyLook) Euler(kw, k.yaw, k.pitch, c.playerSide, report);
                if (k.fov > 60f) report.Warn(kw + ".fov", "fov " + F(k.fov) + " > 60");
            }
        }

        static void ValidateDirector(CamSpec c, string w, HashSet<string> names, SpecReport report)
        {
            if (c.shots.Count == 0) { report.Error(w + ".shots", "director needs at least one shot"); return; }
            for (int i = 0; i < c.shots.Count; i++)
            {
                var sh = c.shots[i];
                if (sh == null) continue;
                string sw = w + ".shots[" + i.ToString(IC) + "]";
                if (string.IsNullOrEmpty(sh.cam)) report.Error(sw + ".cam", "shot needs a camera");
                else if (sh.cam == c.name) report.Error(sw + ".cam", "director references itself");
                else if (!names.Contains(sh.cam)) report.Error(sw + ".cam", "unknown camera '" + sh.cam + "'");
                OneOf(sw, "ease", sh.ease, ShotEases, report);
                OneOf(sw, "transition", sh.transition, Transitions, report);
                OneOf(sw, "clock", sh.clock, Clocks, report);
                if (sh.blend < 0f) report.Error(sw + ".blend", "blend must be >= 0");
                if (sh.motionBlur < 0f) report.Error(sw + ".motionBlur", "motionBlur must be >= 0 (a scale of the pass blur)");
                foreach (var h in sh.hide) if (!string.IsNullOrEmpty(h) && h.StartsWith("#", StringComparison.Ordinal)) report.Warn(sw + ".hide", "instance ids are session-local: " + h);
            }
            for (int i = 0; i + 1 < c.shots.Count; i++)
            {
                var a = c.shots[i]; var b = c.shots[i + 1];
                if (a == null || b == null || a.from == null || b.from == null) continue;
                if (a.from.absolute != b.from.absolute || (!a.from.absolute && a.from.ev != b.from.ev)) continue;
                if (b.from.offset < a.from.offset) report.Warn(w + ".shots[" + (i + 1).ToString(IC) + "].from", "shots are not in time order");
            }
        }

        static void Cycles(MultiCamSpec spec, SpecReport report)
        {
            var dirs = new Dictionary<string, CamSpec>();
            foreach (var c in spec.cameras) if (c != null && c.mode == "director" && !string.IsNullOrEmpty(c.name) && !dirs.ContainsKey(c.name)) dirs[c.name] = c;
            var state = new Dictionary<string, int>();
            var stack = new List<string>();
            foreach (var name in dirs.Keys)
                if (Visit(name, dirs, state, stack, report)) return;
        }

        static bool Visit(string name, Dictionary<string, CamSpec> dirs, Dictionary<string, int> state, List<string> stack, SpecReport report)
        {
            int st;
            state.TryGetValue(name, out st);
            if (st == 2) return false;
            if (st == 1)
            {
                int k = stack.IndexOf(name);
                var cyc = new List<string>();
                for (int i = Math.Max(0, k); i < stack.Count; i++) cyc.Add(stack[i]);
                cyc.Add(name);
                report.Error(Where(dirs[name]) + ".shots", "director cycle: " + string.Join(" -> ", cyc.ToArray()));
                return true;
            }
            state[name] = 1;
            stack.Add(name);
            foreach (var sh in dirs[name].shots)
                if (sh != null && !string.IsNullOrEmpty(sh.cam) && sh.cam != name && dirs.ContainsKey(sh.cam) && Visit(sh.cam, dirs, state, stack, report)) return true;
            stack.RemoveAt(stack.Count - 1);
            state[name] = 2;
            return false;
        }

        // ------------------------------------------------------------------ math helpers

        static float Pitch(Quaternion r)
        {
            var f = r * Vector3.forward;
            return -Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        static Quaternion LookRot(Vector3 dir, Vector3 up)
        {
            if (dir.sqrMagnitude < 1e-10f) dir = Vector3.forward;
            if (up.sqrMagnitude < 1e-10f) up = Vector3.up;
            if (Mathf.Abs(Vector3.Dot(dir.normalized, up.normalized)) > 0.9995f) up = Vector3.forward;
            return Quaternion.LookRotation(dir, up);
        }

        static CamPose LookPose(Vector3 p, Vector3 aim, Vector3 up, float roll, float fov)
        {
            var c = new CamPose();
            c.p = p; c.aim = aim; c.fov = fov; c.ok = true;
            c.r = LookRot(aim - p, up) * Quaternion.Euler(0f, 0f, roll);
            return c;
        }

        static Quaternion Framed(Vector3 dir, Vector3 up, float roll, Vector2 screen, float fov, float aspect)
        {
            float tv = Mathf.Tan(Mathf.Clamp(fov, 1f, 170f) * 0.5f * Mathf.Deg2Rad), th = tv * aspect;
            var local = new Vector3((2f * screen.x - 1f) * th, (1f - 2f * screen.y) * tv, 1f);
            return LookRot(dir, up) * Quaternion.Euler(0f, 0f, roll) * Quaternion.Inverse(Quaternion.LookRotation(local, Vector3.up));
        }

        static float Aspect(MultiCamContext ctx) { return ctx != null && ctx.aspect > 0.1f ? ctx.aspect : 16f / 9f; }

        static Quaternion Scale(Quaternion q, float k)
        {
            if (k == 1f) return q;
            float angle; Vector3 axis;
            q.ToAngleAxis(out angle, out axis);
            if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-12f) return Quaternion.identity;
            if (angle > 180f) angle -= 360f;
            return Quaternion.AngleAxis(angle * k, axis);
        }

        static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        static float Ease(string ease, float x)
        {
            x = Mathf.Clamp01(x);
            switch (ease)
            {
                case "linear": return x;
                case "smoother": return x * x * x * (x * (x * 6f - 15f) + 10f);
                case "sine": return 0.5f - 0.5f * Mathf.Cos(Mathf.PI * x);
            }
            return x * x * (3f - 2f * x);
        }

        static float Trapezoid(float x, float a)
        {
            x = Mathf.Clamp01(x);
            float v = 1f / (1f - a);
            if (x < a) return v * x * x / (2f * a);
            if (x <= 1f - a) return v * (x - a * 0.5f);
            float r = 1f - x;
            return 1f - v * r * r / (2f * a);
        }

        static Vector3 Damp(Vector3 cur, Vector3 target, ref Vector3 vel, float time, float dt)
        {
            if (time <= 0f) { vel = Vector3.zero; return target; }
            if (dt <= 0f) return cur;
            return Vector3.SmoothDamp(cur, target, ref vel, time, Mathf.Infinity, dt);
        }

        static float CurveAt(AnimationCurve c, float d)
        {
            if (c == null || c.length == 0) return 1f;
            return c.EvaluateExtend(d);
        }

        static Vector3 GameLaw(Vector3 cur, Vector3 want, float dt, AnimationCurve cx, AnimationCurve cy, AnimationCurve cz)
        {
            if (dt <= 0f) return cur;
            int n = Mathf.Max(1, Mathf.CeilToInt(dt * 60f - 1e-4f));
            float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                float dx = Mathf.Abs(want.x - cur.x), dy = Mathf.Abs(want.y - cur.y), dz = Mathf.Abs(want.z - cur.z);
                cur.x = Mathf.MoveTowards(cur.x, want.x, Mathf.Max(0f, dx * CurveAt(cx, dx)) * h);
                cur.y = Mathf.MoveTowards(cur.y, want.y, Mathf.Max(0f, dy * CurveAt(cy, dy)) * h);
                cur.z = Mathf.MoveTowards(cur.z, want.z, Mathf.Max(0f, dz * CurveAt(cz, dz)) * h);
            }
            return cur;
        }

        static AnimationCurve fallbackXZ, fallbackY;

        static void GameCurves(out AnimationCurve cx, out AnimationCurve cy, out AnimationCurve cz)
        {
            cx = cy = cz = null;
            try
            {
                var cs = CameraScript.main;
                if (cs != null) { cx = cs.smoothCurveX; cy = cs.smoothCurveY; cz = cs.smoothCurveZ; }
            }
            catch { }
            if (fallbackXZ == null)
            {
                fallbackXZ = new AnimationCurve(new Keyframe(0f, 0f, 0f, 0f), new Keyframe(0.1f, 0f, 0f, 0f), new Keyframe(0.2f, 0.75f, 5f, 5f), new Keyframe(2f, 10f, 15f, 15f));
                fallbackY = new AnimationCurve(new Keyframe(0f, 0f, 0f, 10f), new Keyframe(0.1f, 0.5f, 0.2149f, 0.2149f), new Keyframe(1.7f, 5f, 6.16f, 6.16f));
            }
            if (cx == null || cx.length == 0) cx = fallbackXZ;
            if (cy == null || cy.length == 0) cy = fallbackY;
            if (cz == null || cz.length == 0) cz = fallbackXZ;
        }

        static Vector3 Centripetal(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
        {
            float d12 = (p2 - p1).magnitude;
            if (d12 < 1e-5f) return Vector3.Lerp(p1, p2, u);
            float t1 = Mathf.Max(Mathf.Sqrt((p1 - p0).magnitude), 1e-3f);
            float t2 = t1 + Mathf.Sqrt(d12);
            float t3 = t2 + Mathf.Max(Mathf.Sqrt((p3 - p2).magnitude), 1e-3f);
            float t = t1 + u * (t2 - t1);
            var a1 = (t1 - t) / t1 * p0 + t / t1 * p1;
            var a2 = (t2 - t) / (t2 - t1) * p1 + (t - t1) / (t2 - t1) * p2;
            var a3 = (t3 - t) / (t3 - t2) * p2 + (t - t2) / (t3 - t2) * p3;
            var b1 = (t2 - t) / t2 * a1 + t / t2 * a2;
            var b2 = (t3 - t) / (t3 - t1) * a2 + (t - t1) / (t3 - t1) * a3;
            return (t2 - t) / (t2 - t1) * b1 + (t - t1) / (t2 - t1) * b2;
        }

        static Vector3 Uniform(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
        {
            float u2 = u * u, u3 = u2 * u;
            return 0.5f * (2f * p1 + (p2 - p0) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u2 + (3f * p1 - p0 - 3f * p2 + p3) * u3);
        }

        static Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float h, float u)
        {
            float u2 = u * u, u3 = u2 * u;
            return (2f * u3 - 3f * u2 + 1f) * p0 + (u3 - 2f * u2 + u) * h * m0 + (-2f * u3 + 3f * u2) * p1 + (u3 - u2) * h * m1;
        }

        static Vector3 SegUniform(Vector3[] v, int i, float u)
        {
            int n = v.Length;
            if (n == 1) return v[0];
            var p1 = v[i]; var p2 = v[i + 1];
            var p0 = i > 0 ? v[i - 1] : p1 + (p1 - p2);
            var p3 = i + 2 < n ? v[i + 2] : p2 + (p2 - p1);
            return Uniform(p0, p1, p2, p3, u);
        }

        static Vector3 SegHermite(Vector3[] v, float[] t, int i, float u)
        {
            int n = v.Length;
            if (n == 1) return v[0];
            float h = t[i + 1] - t[i];
            if (h <= 1e-6f) return v[i + 1];
            return Hermite(v[i], Tangent(v, t, i), v[i + 1], Tangent(v, t, i + 1), h, u);
        }

        static Vector3 Tangent(Vector3[] v, float[] t, int k)
        {
            if (k <= 0 || k >= v.Length - 1) return Vector3.zero;
            float h = t[k + 1] - t[k - 1];
            return h > 1e-6f ? (v[k + 1] - v[k - 1]) / h : Vector3.zero;
        }

        static bool Sample(MultiCamContext ctx, TargetRef r, out TargetSample s)
        {
            s = default(TargetSample);
            if (r == null || r.isNone || r.isSelf) return false;
            if (ctx != null && ctx.target != null) return ctx.Target(r, out s);
            return MultiCamTargets.Sample(r, out s);
        }

        static bool IsBase(string of)
        {
            return of == "boy" || of == "huddle" || of == "main" || of == "game" || of == "gameaim" || of == "probe" || of == "none" || of == "self";
        }

        static TargetRef WithPoint(TargetRef r, string point)
        {
            var c = new TargetRef();
            c.of = r.of; c.point = point; c.offset = r.offset; c.where = r.where;
            c.given.AddRange(r.given);
            if (!c.given.Contains("point")) c.given.Add("point");
            return c;
        }

        static TargetRef TargetOf(CamSpec s)
        {
            var t = s.target;
            if (t == null || t.fixedPoint || t.isGroup || !string.IsNullOrEmpty(t.point) || string.IsNullOrEmpty(s.point) || s.point == "pivot") return t;
            return WithPoint(t, s.point);
        }

        static TargetRef LookRef(CamSpec s, TargetRef target, bool v1, bool defaultSelf)
        {
            var l = s.look;
            if (l == null) return defaultSelf ? target : null;
            if (l.isNone) return null;
            if (l.isSelf) return target;
            return V1Look(l, v1);
        }

        static TargetRef V1Look(TargetRef l, bool v1)
        {
            if (!v1 || l == null || l.fixedPoint || l.isGroup || !string.IsNullOrEmpty(l.point) || IsBase((l.of ?? "").Trim())) return l;
            return WithPoint(l, "bounds");
        }

        // ------------------------------------------------------------------ shared layers

        sealed class LookTrack
        {
            readonly RigBase owner;
            readonly TargetRef target;
            readonly Vector3 offset, extra;
            readonly float smooth, lead;
            readonly bool feed;
            Vector3 cur, vel, raw, rawVel, ffV;
            bool init, hasRaw, ffInit;

            public LookTrack(RigBase owner, TargetRef target, Vector3 offset, Vector3 extra, float smooth, float lead)
            {
                this.owner = owner; this.target = target; this.offset = offset; this.extra = extra; this.smooth = smooth; this.lead = lead;
                feed = owner.spec.feedForward;
            }

            public bool configured { get { return target != null; } }

            public bool Raw(MultiCamContext ctx, out Vector3 point, ref int flags)
            {
                point = Vector3.zero;
                if (target == null) return false;
                TargetSample ts;
                if (Sample(ctx, target, out ts))
                {
                    var la = lead > 0f ? Vector3.ClampMagnitude(new Vector3(ts.vel.x, ts.vel.y, 0f) * lead, 4f) : Vector3.zero;
                    raw = ts.pos + extra + offset + la;
                    rawVel = ts.vel;
                    hasRaw = true;
                    owner.SetSubject(ts.pos, ts.vel);
                }
                else { flags |= PoseFlags.NoTarget; rawVel = Vector3.zero; }
                if (!hasRaw) return false;
                point = raw;
                return true;
            }

            public bool Point(MultiCamContext ctx, out Vector3 point, ref int flags)
            {
                if (!Raw(ctx, out point, ref flags)) return false;
                var ahead = feed ? MultiCamGeom.FeedLead(rawVel, ref ffV, ref ffInit, smooth, ctx.dt) : Vector3.zero;
                if (!init || smooth <= 0f) { cur = point; vel = smooth > 0f ? ahead / smooth : Vector3.zero; init = true; }
                else cur = Damp(cur, point + ahead, ref vel, smooth, ctx.dt);
                point = cur;
                return true;
            }
        }

        sealed class Shaker
        {
            readonly ShakeSpec s;
            public readonly bool active;
            GameObject dummy;
            AnimationClip soft, hard;
            bool tried, broken, logged;
            int probes;
            float probeMax;
            double probeFirst = double.NaN;

            public Shaker(ShakeSpec spec)
            {
                s = spec;
                active = spec != null && (spec.hand != 0f || spec.fx != 0f || spec.replica != 0f || spec.hard != 0f);
            }

            public void Apply(MultiCamContext ctx, ref CamPose p)
            {
                if (!active) return;
                var off = Vector3.zero;
                var rot = Quaternion.identity;
                var g = ctx.game;
                if (s.hand != 0f && g.ok)
                {
                    var h = g.handOffset;
                    if (!s.pullback) h.z = 0f;
                    off += h * s.hand;
                    rot = rot * Scale(g.handRot, s.hand);
                }
                if (s.fx != 0f && g.ok)
                {
                    off += g.shakeOffset * s.fx;
                    rot = rot * Scale(g.shakeRot, s.fx);
                }
                if (s.replica != 0f || s.hard != 0f)
                {
                    double time = (s.clock == "output" ? ctx.outputTime : ctx.t) * s.rate + s.phase;
                    Vector3 lp; Quaternion lr;
                    if (s.replica != 0f && Noise(ctx, true, time, out lp, out lr)) { off += lp * s.replica; rot = rot * Scale(lr, s.replica); }
                    if (s.hard != 0f && Noise(ctx, false, time, out lp, out lr)) { off += lp * s.hard; rot = rot * Scale(lr, s.hard); }
                }
                p.p += p.r * off;
                p.r = p.r * rot;
                p.flags |= PoseFlags.Shake;
            }

            void Clips()
            {
                tried = true;
                try
                {
                    var cs = CameraScript.main;
                    var hs = cs != null ? cs.cameraHandshake : null;
                    if (hs != null) { soft = hs.softNoiseClip; hard = hs.hardNoiseClip; }
                }
                catch { soft = hard = null; }
                if (soft == null && hard == null) { broken = true; return; }
                dummy = new GameObject("InsideDev MultiCam shake");
                dummy.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(dummy);
            }

            bool Noise(MultiCamContext ctx, bool isSoft, double time, out Vector3 pos, out Quaternion rot)
            {
                pos = Vector3.zero; rot = Quaternion.identity;
                if (!tried) Clips();
                var clip = isSoft ? soft : hard;
                if (!broken && clip != null && dummy != null)
                {
                    try
                    {
                        var tr = dummy.transform;
                        tr.localPosition = Vector3.zero;
                        tr.localRotation = Quaternion.identity;
                        float len = clip.length;
                        float tt = len > 0f ? (float)(time - Math.Floor(time / len) * len) : 0f;
                        clip.SampleAnimation(dummy, tt);
                        pos = tr.localPosition;
                        rot = tr.localRotation;
                        Probe(pos, rot, time);
                        if (!broken) return true;
                        if (!logged) { logged = true; ctx.Log("warn shake replica: sampling the noise clips gives no motion; using procedural noise"); }
                    }
                    catch (Exception e) { broken = true; if (!logged) { logged = true; ctx.Log("warn shake replica: clip sampling failed (" + e.Message + "); using procedural noise"); } }
                }
                if (!logged) { logged = true; ctx.Log("warn shake replica: noise clip " + (isSoft ? "noiseSoft" : "noiseHard") + " unavailable; using procedural noise"); }
                Procedural(isSoft, time, out pos, out rot);
                return true;
            }

            void Probe(Vector3 pos, Quaternion rot, double time)
            {
                if (probes < 0) return;
                probeMax = Mathf.Max(probeMax, pos.magnitude + Quaternion.Angle(Quaternion.identity, rot));
                if (double.IsNaN(probeFirst)) probeFirst = time;
                probes++;
                if (probeMax > 0f) { probes = -1; return; }
                if (probes >= 12 && Math.Abs(time - probeFirst) > 0.3) broken = true;
            }

            static void Procedural(bool isSoft, double time, out Vector3 pos, out Quaternion rot)
            {
                float a = isSoft ? 1f : 3f;
                float f = isSoft ? 0.35f : 0.9f;
                float t = (float)(time % 10000.0) * f;
                float nx = Mathf.PerlinNoise(t, 0.13f) - 0.5f, ny = Mathf.PerlinNoise(t, 3.71f) - 0.5f;
                float rx = Mathf.PerlinNoise(t, 7.29f) - 0.5f, ry = Mathf.PerlinNoise(t, 11.83f) - 0.5f, rz = Mathf.PerlinNoise(t, 17.41f) - 0.5f;
                pos = new Vector3(nx, ny, 0f) * 0.04f * a;
                rot = Quaternion.Euler(rx * 0.8f * a, ry * 0.8f * a, rz * 0.4f * a);
            }

            public void Restart()
            {
                probes = 0;
                probeMax = 0f;
                probeFirst = double.NaN;
                logged = false;
                broken = tried && (dummy == null || (soft == null && hard == null));
            }

            public void End()
            {
                if (dummy != null) UnityEngine.Object.Destroy(dummy);
                dummy = null;
                tried = false;
                broken = false;
                probes = 0;
                probeMax = 0f;
                probeFirst = double.NaN;
            }
        }

        sealed class Path
        {
            public readonly Vector3[] pts;
            readonly bool linear;
            const int Per = 64;
            float[] cum;
            public float length;

            public Path(Vector3[] points, bool linearInterp)
            {
                pts = points;
                linear = linearInterp;
                int n = pts.Length;
                if (n < 2) return;
                cum = new float[(n - 1) * Per + 1];
                var prev = pts[0];
                for (int j = 1; j < cum.Length; j++)
                {
                    int i = Math.Min(j / Per, n - 2);
                    float u = (j - i * Per) / (float)Per;
                    var p = At(i, u);
                    cum[j] = cum[j - 1] + (p - prev).magnitude;
                    prev = p;
                }
                length = cum[cum.Length - 1];
            }

            public Vector3 At(int i, float u)
            {
                int n = pts.Length;
                if (n == 0) return Vector3.zero;
                if (n == 1) return pts[0];
                var p1 = pts[i]; var p2 = pts[i + 1];
                if (linear) return Vector3.Lerp(p1, p2, u);
                float d = (p2 - p1).magnitude * 0.05f;
                var p0 = i > 0 && (pts[i - 1] - p1).magnitude > d ? pts[i - 1] : p1 + (p1 - p2);
                var p3 = i + 2 < n && (pts[i + 2] - p2).magnitude > d ? pts[i + 2] : p2 + (p2 - p1);
                return Centripetal(p0, p1, p2, p3, u);
            }

            public void ParamAt(float s01, out int i, out float u)
            {
                int n = pts.Length;
                i = 0; u = 0f;
                if (n < 2) return;
                float g;
                if (length < 1e-4f) g = Mathf.Clamp01(s01) * (n - 1);
                else
                {
                    float target = Mathf.Clamp01(s01) * length;
                    int lo = 1, hi = cum.Length - 1;
                    while (lo < hi) { int mid = (lo + hi) >> 1; if (cum[mid] < target) lo = mid + 1; else hi = mid; }
                    float seg = cum[lo] - cum[lo - 1];
                    float frac = seg > 1e-7f ? (target - cum[lo - 1]) / seg : 1f;
                    g = (lo - 1 + frac) / Per;
                }
                i = Mathf.Clamp((int)Mathf.Floor(g), 0, n - 2);
                u = Mathf.Clamp01(g - i);
            }
        }

        // ------------------------------------------------------------------ rigs

        abstract class RigBase : ICamRig
        {
            protected readonly CamSpec s;
            protected readonly Shaker shaker;
            protected bool v1, aimed, framed;
            protected CamPose lastRaw;
            protected bool hasLast;
            bool failLogged, limInit, subjSet, moveSet;
            Vector3 limP, subjPos, subjVel, moveVel;
            Quaternion limR;
            float limV, limW;

            protected RigBase(CamSpec spec) { s = spec; shaker = new Shaker(spec.shake); }

            public CamSpec spec { get { return s; } }

            public void Begin(MultiCamContext ctx)
            {
                v1 = ctx != null && ctx.spec != null && (!ctx.spec.Has("version") || ctx.spec.version < 2);
                framed = s.Has("screen") && Array.IndexOf(FramedModes, s.mode) >= 0;
                hasLast = false;
                failLogged = false;
                limInit = false;
                shaker.Restart();
                Reset(ctx);
            }

            protected abstract void Reset(MultiCamContext ctx);
            protected abstract CamPose Pose(MultiCamContext ctx);
            protected virtual bool ClampAllowed { get { return true; } }
            protected virtual bool ShakeAllowed { get { return true; } }
            protected virtual bool LimitAllowed { get { return s.motion != null && s.mode != "game"; } }

            // The subject this step (MULTICAM.md 6.3, 6.6): the tracked point before damping and its velocity; stamped on the pose
            // (poses.csv subj_*) and carried by the limiter. A pose copied from another camera keeps that camera's subject.
            // SetMove: the velocity of what the camera position follows (follow / frame target, the overview's x).
            public void SetSubject(Vector3 pos, Vector3 vel) { subjSet = true; subjPos = pos; subjVel = vel; }

            protected void SetMove(Vector3 vel) { moveSet = true; moveVel = vel; }
            protected MotionSpec Motion { get { return s.motion ?? (v1 ? NoMotion : DefaultMotion); } }

            public CamPose Evaluate(MultiCamContext ctx)
            {
                CamPose p;
                aimed = false;
                subjSet = false;
                moveSet = false;
                try { p = Pose(ctx); }
                catch (Exception e)
                {
                    if (!failLogged) { failLogged = true; ctx.Log("error camera " + s.name + ": " + e.GetType().Name + ": " + e.Message); }
                    p = hasLast ? Hold(PoseFlags.NoTarget) : NoPose(0);
                }
                if (!p.ok) return p;
                if (subjSet) { p.hasSubj = true; p.subj = subjPos; p.subjVel = subjVel; p.attached = moveSet; p.moveVel = moveSet ? moveVel : Vector3.zero; }
                if (p.near <= 0f && s.near > 0f) p.near = s.near;
                lastRaw = p;
                hasLast = true;
                Finish(ctx, ref p);
                return p;
            }

            void Finish(MultiCamContext ctx, ref CamPose p)
            {
                Clamps(ctx, ref p);
                if (LimitAllowed && Limit(ctx, ref p)) Clamps(ctx, ref p);
                if (ShakeAllowed && shaker.active)
                {
                    try { shaker.Apply(ctx, ref p); }
                    catch (Exception e) { if (!failLogged) { failLogged = true; ctx.Log("error camera " + s.name + " shake: " + e.Message); } }
                }
            }

            void Clamps(MultiCamContext ctx, ref CamPose p)
            {
                if (ClampAllowed && s.playerSide && p.p.z > s.zMax && Pitch(p.r) < 45f)
                {
                    p.p.z = s.zMax;
                    if (aimed) p.r = framed ? Framed(p.aim - p.p, Vector3.up, s.roll, s.screen, p.fov, Aspect(ctx)) : LookRot(p.aim - p.p, p.r * Vector3.up);
                    p.flags |= PoseFlags.Clamped;
                }
                if (s.clamp != null && EdgeClamp(ctx, ref p))
                {
                    p.flags = (p.flags | PoseFlags.Clamped) & ~PoseFlags.Copy;
                    p.copyOf = null;
                }
            }

            // Motion limiter (MotionSpec, output clock): the pose follows the wanted one with bounded speed, turn rate and
            // acceleration; true when it changed the pose (then it is no copy of a source frame any more). Subject-relative: the
            // state first moves with the tracked subject (MultiCamGeom.Carry: the turn when the camera aims at it, the
            // displacement when its position follows it), so only reframing and switches are capped.
            bool Limit(MultiCamContext ctx, ref CamPose p)
            {
                var m = s.motion;
                float dt = ctx.outputDt > 0f ? ctx.outputDt : 0f;
                if (!limInit || (p.p - limP).magnitude > MultiCamGeom.MotionSnap) { limP = p.p; limR = p.r; limV = 0f; limW = 0f; limInit = true; return false; }
                if (p.hasSubj && dt > 0f) MultiCamGeom.Carry(ref limP, ref limR, p.subj, p.subjVel, p.attached ? p.moveVel : Vector3.zero, aimed, ctx.dt > 0f ? ctx.dt : dt);
                var np = MultiCamGeom.LimitMove(limP, p.p, ref limV, m.maxSpeed, m.ease, dt);
                var nr = MultiCamGeom.LimitTurn(limR, p.r, ref limW, m.maxTurn, m.ease, dt);
                limP = np; limR = nr;
                if ((np - p.p).sqrMagnitude < 1e-8f && MultiCamGeom.AngleDeg(nr, p.r) < 0.01f) return false;
                float d = (p.aim - p.p).magnitude;
                p.p = np;
                p.r = nr;
                p.aim = np + nr * Vector3.forward * (d > 0.05f ? d : MultiCamConst.GameAimDistance);
                p.flags &= ~PoseFlags.Copy;
                p.copyOf = null;
                return true;
            }

            public virtual void Plan(MultiCamContext ctx, List<CamPose> poses) { }

            public virtual void End() { shaker.End(); }

            protected bool Edges(MultiCamContext ctx, Vector3 pos, Quaternion rot, float fov, Vector3 aim, out float left, out float right)
            {
                var k = s.clamp;
                var fwd = rot * Vector3.forward;
                bool plane = k.Has("z");
                float depth = plane ? k.z : Vector3.Dot(aim - pos, fwd);
                if (!plane && depth <= 0.05f) depth = s.focus > 0f ? s.focus : MultiCamConst.GameAimDistance;
                float tv = Mathf.Tan(Mathf.Clamp(fov > 0f ? fov : GameFov(ctx), 1f, 170f) * 0.5f * Mathf.Deg2Rad);
                float aspect = k.aspect > 0.1f ? k.aspect : Aspect(ctx);
                return MultiCamGeom.EdgeXs(pos, rot * Vector3.right, rot * Vector3.up, fwd, tv, aspect, plane, depth, k.corners, out left, out right);
            }

            bool EdgeClamp(MultiCamContext ctx, ref CamPose p)
            {
                float l, r, shift;
                if (!Edges(ctx, p.p, p.r, p.fov, p.aim, out l, out r)) return false;
                if (!MultiCamGeom.EdgeShift(l, r, s.clamp.Has("left"), s.clamp.left, s.clamp.Has("right"), s.clamp.right, out shift)) return false;
                p.p.x += shift;
                p.aim.x += shift;
                return true;
            }

            protected CamPose NoPose(int flags)
            {
                var c = new CamPose();
                c.ok = false;
                c.flags = PoseFlags.Fallback | flags;
                return c;
            }

            protected CamPose Hold(int flags)
            {
                if (!hasLast) return NoPose(flags);
                var c = lastRaw;
                c.flags = (c.flags & ~(PoseFlags.Clamped | PoseFlags.Shake | PoseFlags.Cut | PoseFlags.Copy)) | flags;
                c.copyOf = null;
                return c;
            }

            protected float Fov(MultiCamContext ctx) { return s.fov > 0f ? s.fov : GameFov(ctx); }

            protected static float GameFov(MultiCamContext ctx)
            {
                return ctx != null && ctx.game.ok && ctx.game.rendered.fov > 1f ? ctx.game.rendered.fov : MultiCamConst.GameFov;
            }

            protected CamPose Euler(Vector3 p, float pitch, float yaw, float roll, float fov)
            {
                return CamPose.Make(p, Quaternion.Euler(pitch, yaw, roll), fov, s.focus > 0f ? s.focus : MultiCamConst.GameAimDistance);
            }

            protected CamPose Look(MultiCamContext ctx, Vector3 p, Vector3 aim, float fov, int flags)
            {
                aimed = true;
                var c = LookPose(p, aim, Vector3.up, s.roll, fov);
                if (framed) c.r = Framed(aim - p, Vector3.up, s.roll, s.screen, fov, Aspect(ctx));
                c.flags |= flags;
                return c;
            }
        }

        sealed class NullRig : RigBase
        {
            public NullRig(CamSpec spec) : base(spec) { }
            protected override void Reset(MultiCamContext ctx) { }
            protected override CamPose Pose(MultiCamContext ctx) { return NoPose(0); }
        }

        sealed class GameRig : RigBase
        {
            public GameRig(CamSpec spec) : base(spec) { }
            protected override bool ClampAllowed { get { return false; } }
            protected override void Reset(MultiCamContext ctx) { }

            protected override CamPose Pose(MultiCamContext ctx)
            {
                var g = ctx.game;
                if (!g.ok) return hasLast ? Hold(PoseFlags.NoTarget) : NoPose(PoseFlags.NoTarget);
                var b = s.clean ? g.clean : g.rendered;
                var c = new CamPose();
                c.ok = true;
                c.r = s.roll != 0f ? b.r * Quaternion.Euler(0f, 0f, s.roll) : b.r;
                c.p = b.p + b.r * s.offset;
                float fov = s.fov > 0f && !v1 ? s.fov : b.fov;
                c.fov = fov * (s.fovScale > 0f ? s.fovScale : 1f);
                c.aim = c.p + c.r * Vector3.forward * g.aimDistance;
                c.near = s.near;
                return c;
            }
        }

        sealed class StaticRig : RigBase
        {
            LookTrack look;
            public StaticRig(CamSpec spec) : base(spec) { }

            protected override void Reset(MultiCamContext ctx)
            {
                var t = TargetOf(s);
                look = new LookTrack(this, LookRef(s, t, v1, false), s.lookOffset, Vector3.zero, s.lookSmooth, 0f);
            }

            protected override CamPose Pose(MultiCamContext ctx)
            {
                int flags = 0;
                Vector3 a;
                if (look.Point(ctx, out a, ref flags)) return Look(ctx, s.pos, a, Fov(ctx), flags);
                var e = Euler(s.pos, s.pitch, s.yaw, s.roll, Fov(ctx));
                e.flags |= flags;
                return e;
            }

            public override void Plan(MultiCamContext ctx, List<CamPose> poses)
            {
                int flags = 0;
                Vector3 a;
                poses.Add(look.Raw(ctx, out a, ref flags) ? LookPose(s.pos, a, Vector3.up, s.roll, Fov(ctx)) : Euler(s.pos, s.pitch, s.yaw, s.roll, Fov(ctx)));
            }
        }

        sealed class FollowRig : RigBase
        {
            TargetRef target;
            LookTrack look;
            bool lookNone, init, ffInit;
            Vector3 cur, vel, hold, ffV;
            float z0;
            AnimationCurve cx, cy, cz;
            public FollowRig(CamSpec spec) : base(spec) { }

            protected override void Reset(MultiCamContext ctx)
            {
                target = TargetOf(s);
                lookNone = s.look != null && s.look.isNone;
                look = new LookTrack(this, LookRef(s, target, v1, true), s.lookOffset, Vector3.zero, s.lookSmooth, s.lead);
                init = false;
                GameCurves(out cx, out cy, out cz);
            }

            protected override CamPose Pose(MultiCamContext ctx)
            {
                TargetSample ts;
                if (!Sample(ctx, target, out ts)) return hasLast ? Hold(PoseFlags.NoTarget) : NoPose(PoseFlags.NoTarget);
                int flags = 0;
                var off = s.offset;
                if (s.local && ts.go != null) off = Quaternion.Euler(0f, ts.go.transform.eulerAngles.y, 0f) * off;
                var want = ts.pos + off;
                SetSubject(ts.pos, ts.vel);
                var ahead = s.feedForward && s.lag == "damp" ? MultiCamGeom.FeedLead(ts.vel, ref ffV, ref ffInit, s.smooth, ctx.dt) : Vector3.zero;
                if (s.planar) ahead.z = 0f;
                if (!init) { cur = want; vel = s.smooth > 0f ? ahead / s.smooth : Vector3.zero; z0 = want.z; hold = want; init = true; }
                if (s.planar) want.z = z0;
                bool waiting = s.start != null && !ctx.Reached(s.start);
                if (waiting) { cur = hold; vel = Vector3.zero; flags |= PoseFlags.Waiting; }
                else if (s.lag == "none") cur = want;
                else if (s.lag == "game") cur = GameLaw(cur, want, ctx.dt, cx, cy, cz);
                else cur = Damp(cur, want + ahead, ref vel, s.smooth, ctx.dt);
                if (!waiting) SetMove(s.planar ? new Vector3(ts.vel.x, ts.vel.y, 0f) : ts.vel);
                float fov = Fov(ctx);
                if (lookNone) { var e = Euler(cur, s.pitch, s.yaw, s.roll, fov); e.flags |= flags; return e; }
                Vector3 a;
                if (!look.Point(ctx, out a, ref flags)) a = ts.pos + s.lookOffset;
                return Look(ctx, cur, a, fov, flags);
            }
        }

        sealed class KeysRig : RigBase
        {
            int n;
            float[] kt, kf;
            Vector3[] kp, ka, aims, klo, kv;
            TargetRef[] kl;
            bool[] lookOk;
            Path path;
            bool linearInterp;
            string ease;
            Vector3 aimS, aimV, ffV;
            bool aimInit, ffInit;
            public KeysRig(CamSpec spec) : base(spec) { }

            protected override void Reset(MultiCamContext ctx)
            {
                var keys = new List<KeySpec>();
                foreach (var k in s.keys) if (k != null) keys.Add(k);
                for (int i = 1; i < keys.Count; i++)
                {
                    var k = keys[i];
                    int j = i - 1;
                    while (j >= 0 && keys[j].t > k.t) { keys[j + 1] = keys[j]; j--; }
                    keys[j + 1] = k;
                }
                n = keys.Count;
                kt = new float[n]; kf = new float[n];
                kp = new Vector3[n]; ka = new Vector3[n]; aims = new Vector3[n]; klo = new Vector3[n]; kv = new Vector3[n];
                kl = new TargetRef[n]; lookOk = new bool[n];
                var target = TargetOf(s);
                var camLook = LookRef(s, target, v1, false);
                for (int i = 0; i < n; i++)
                {
                    var k = keys[i];
                    kt[i] = k.t; kf[i] = k.fov; kp[i] = k.pos;
                    var a = new Vector3(k.pitch, k.yaw, k.roll);
                    if (i > 0)
                    {
                        var p = ka[i - 1];
                        a = new Vector3(p.x + Mathf.DeltaAngle(p.x, a.x), p.y + Mathf.DeltaAngle(p.y, a.y), p.z + Mathf.DeltaAngle(p.z, a.z));
                    }
                    ka[i] = a;
                    if (k.look != null) { kl[i] = k.look.isNone ? null : k.look.isSelf ? target : V1Look(k.look, v1); klo[i] = k.lookOffset; }
                    else { kl[i] = camLook; klo[i] = s.lookOffset; }
                }
                linearInterp = s.interp == "linear";
                ease = v1 && !s.Has("ease") ? "each" : s.ease;
                path = new Path(kp, linearInterp);
                aimInit = false;
            }

            Vector3 KeyAim(MultiCamContext ctx, int j, out bool ok, out bool fresh)
            {
                ok = false; fresh = false;
                TargetSample ts;
                if (kl[j] != null && Sample(ctx, kl[j], out ts)) { ok = true; fresh = true; kv[j] = ts.vel; return ts.pos + klo[j]; }
                kv[j] = Vector3.zero;
                if (kl[j] != null && lookOk[j]) { ok = true; return aims[j]; }
                return kp[j] + Quaternion.Euler(ka[j].x, ka[j].y, ka[j].z) * Vector3.forward * (s.focus > 0f ? s.focus : MultiCamConst.GameAimDistance);
            }

            void TimeSeg(float lt, out int i, out float u)
            {
                i = 0;
                while (i < n - 2 && lt >= kt[i + 1]) i++;
                float h = kt[i + 1] - kt[i];
                u = h > 1e-6f ? Mathf.Clamp01((lt - kt[i]) / h) : 1f;
            }

            void Locate(float lt, out int i, out float u, out Vector3 p)
            {
                i = 0; u = 0f;
                if (n == 1) { p = kp[0]; return; }
                if (lt <= kt[0]) { p = kp[0]; return; }
                if (lt >= kt[n - 1]) { i = n - 2; u = 1f; p = kp[n - 1]; return; }
                switch (ease)
                {
                    case "keys":
                        TimeSeg(lt, out i, out u);
                        p = linearInterp ? Vector3.Lerp(kp[i], kp[i + 1], u) : SegHermite(kp, kt, i, u);
                        return;
                    case "each":
                        TimeSeg(lt, out i, out u);
                        u = Smooth(u);
                        p = path.At(i, u);
                        return;
                    case "linear":
                        TimeSeg(lt, out i, out u);
                        p = path.At(i, u);
                        return;
                }
                float d = kt[n - 1] - kt[0];
                float sn = Trapezoid((lt - kt[0]) / d, EndsFraction);
                if (path.length > 1e-3f) path.ParamAt(sn, out i, out u);
                else TimeSeg(kt[0] + sn * d, out i, out u);
                p = path.At(i, u);
            }

            void Orient(int i, float u, Vector3 p, out Quaternion r, out Vector3 aim)
            {
                float focus = s.focus > 0f ? s.focus : MultiCamConst.GameAimDistance;
                int j = Math.Min(i + 1, n - 1);
                bool la = lookOk[i], lb = lookOk[j];
                bool smoothPath = ease == "ends" || ease == "keys";
                if (la && lb)
                {
                    aim = n == 1 ? aims[0] : ease == "keys" ? SegHermite(aims, kt, i, u) : ease == "ends" ? SegUniform(aims, i, u) : Vector3.Lerp(aims[i], aims[j], u);
                    r = LookRot(aim - p, Vector3.up) * Quaternion.Euler(0f, 0f, Mathf.Lerp(ka[i].z, ka[j].z, u));
                    return;
                }
                if (!la && !lb)
                {
                    if (smoothPath && n > 1)
                    {
                        var ang = ease == "keys" ? SegHermite(ka, kt, i, u) : SegUniform(ka, i, u);
                        r = Quaternion.Euler(ang.x, ang.y, ang.z);
                    }
                    else r = Quaternion.Slerp(Quaternion.Euler(ka[i].x, ka[i].y, ka[i].z), Quaternion.Euler(ka[j].x, ka[j].y, ka[j].z), u);
                    aim = p + r * Vector3.forward * focus;
                    return;
                }
                var ra = la ? LookRot(aims[i] - p, Vector3.up) * Quaternion.Euler(0f, 0f, ka[i].z) : Quaternion.Euler(ka[i].x, ka[i].y, ka[i].z);
                var rb = lb ? LookRot(aims[j] - p, Vector3.up) * Quaternion.Euler(0f, 0f, ka[j].z) : Quaternion.Euler(ka[j].x, ka[j].y, ka[j].z);
                float w = Smooth(u);
                r = Quaternion.Slerp(ra, rb, w);
                float da = la ? (aims[i] - p).magnitude : focus, db = lb ? (aims[j] - p).magnitude : focus;
                aim = p + r * Vector3.forward * Mathf.Lerp(da, db, w);
            }

            protected override CamPose Pose(MultiCamContext ctx)
            {
                if (n == 0) return NoPose(0);
                int flags = 0;
                for (int j = 0; j < n; j++)
                {
                    bool ok, fresh;
                    var a = KeyAim(ctx, j, out ok, out fresh);
                    if (kl[j] != null && !fresh) flags |= PoseFlags.NoTarget;
                    aims[j] = a;
                    lookOk[j] = ok;
                }
                double local = ctx.LocalTime(s.start);
                bool waiting = double.IsNaN(local) || local < kt[0] - 1e-6;
                if (waiting) flags |= PoseFlags.Waiting;
                float lt = waiting ? kt[0] : (float)local;
                int i; float u; Vector3 p;
                Locate(lt, out i, out u, out p);
                Quaternion r; Vector3 aim;
                Orient(i, u, p, out r, out aim);
                int jj = Math.Min(i + 1, n - 1);
                float camFov = Fov(ctx);
                float fa = kf[i] > 0f ? kf[i] : camFov, fb = kf[jj] > 0f ? kf[jj] : camFov;
                bool lookI = lookOk[i] && kl[i] != null, lookJ = lookOk[jj] && kl[jj] != null;
                var av = lookI && lookJ ? Vector3.Lerp(kv[i], kv[jj], u) : lookI ? kv[i] : lookJ ? kv[jj] : Vector3.zero;
                if (lookI || lookJ) SetSubject(lookI && lookJ ? aim : lookI ? aims[i] : aims[jj], av);
                if (s.lookSmooth > 0f)
                {
                    var ahead = s.feedForward && (lookI || lookJ) ? MultiCamGeom.FeedLead(av, ref ffV, ref ffInit, s.lookSmooth, ctx.dt) : Vector3.zero;
                    if (!aimInit) { aimS = aim; aimV = ahead / s.lookSmooth; aimInit = true; }
                    else aimS = Damp(aimS, aim + ahead, ref aimV, s.lookSmooth, ctx.dt);
                    r = LookRot(aimS - p, r * Vector3.up);
                    aim = aimS;
                }
                var c = new CamPose();
                c.ok = true; c.p = p; c.r = r; c.aim = aim; c.fov = Mathf.Lerp(fa, fb, u); c.flags = flags;
                aimed = lookOk[i] || lookOk[jj];
                if (framed && aimed) c.r = Framed(aim - p, Vector3.up, Mathf.Lerp(ka[i].z, ka[jj].z, u), s.screen, c.fov, Aspect(ctx));
                return c;
            }

            public override void Plan(MultiCamContext ctx, List<CamPose> poses)
            {
                if (n == 0) return;
                float camFov = Fov(ctx);
                for (int j = 0; j < n; j++)
                {
                    bool ok, fresh;
                    aims[j] = KeyAim(ctx, j, out ok, out fresh);
                    lookOk[j] = ok;
                }
                for (int j = 0; j < n; j++)
                {
                    Quaternion r; Vector3 aim;
                    Orient(Math.Min(j, Math.Max(0, n - 2)), j == n - 1 && n > 1 ? 1f : 0f, kp[j], out r, out aim);
                    var c = CamPose.Make(kp[j], r, kf[j] > 0f ? kf[j] : camFov, 1f);
                    c.aim = aim;
                    poses.Add(c);
                    if (j + 1 < n)
                    {
                        var m = path.At(j, 0.5f);
                        Orient(j, 0.5f, m, out r, out aim);
                        var mc = CamPose.Make(m, r, kf[j] > 0f ? kf[j] : camFov, 1f);
                        mc.aim = aim;
                        poses.Add(mc);
                    }
                }
            }
        }

        sealed class RailRig : RigBase
        {
            Path path;
            LookTrack look;
            public RailRig(CamSpec spec) : base(spec) { }

            protected override void Reset(MultiCamContext ctx)
            {
                path = new Path(s.path.ToArray(), s.interp == "linear");
                look = new LookTrack(this, LookRef(s, TargetOf(s), v1, false), s.lookOffset, Vector3.zero, s.lookSmooth, 0f);
            }

            Vector3 At(float sn)
            {
                if (path.pts.Length == 1) return path.pts[0];
                int i; float u;
                path.ParamAt(sn, out i, out u);
                return path.At(i, u);
            }

            protected override CamPose Pose(MultiCamContext ctx)
            {
                if (path.pts.Length == 0) return NoPose(0);
                int flags = 0;
                double local = ctx.LocalTime(s.start);
                bool waiting = double.IsNaN(local) || local < 0;
                if (waiting) flags |= PoseFlags.Waiting;
                float x = !waiting && s.duration > 0f ? Mathf.Clamp01((float)(local / s.duration)) : 0f;
                var p = At(s.ease == "linear" ? x : Trapezoid(x, EndsFraction));
                Vector3 a;
                if (look.Point(ctx, out a, ref flags)) return Look(ctx, p, a, Fov(ctx), flags);
                var e = Euler(p, s.pitch, s.yaw, s.roll, Fov(ctx));
                e.flags |= flags;
                return e;
            }

            public override void Plan(MultiCamContext ctx, List<CamPose> poses)
            {
                if (path.pts.Length == 0) return;
                int flags = 0;
                Vector3 a;
                bool hasLook = look.Raw(ctx, out a, ref flags);
                for (int k = 0; k <= 8; k++)
                {
                    var p = At(k / 8f);
                    poses.Add(hasLook ? LookPose(p, a, Vector3.up, s.roll, Fov(ctx)) : Euler(p, s.pitch, s.yaw, s.roll, Fov(ctx)));
                }
            }
        }

        sealed class OrbitRig : RigBase
        {
            TargetRef target;
            LookTrack look;
            bool lookNone;
            public OrbitRig(CamSpec spec) : base(spec) { }

            protected override void Reset(MultiCamContext ctx)
            {
                target = TargetOf(s);
                lookNone = s.look != null && s.look.isNone;
                bool selfLook = s.look == null || s.look.isSelf;
                look = new LookTrack(this, LookRef(s, target, v1, true), s.lookOffset, selfLook ? s.offset : Vector3.zero, s.lookSmooth, 0f);
            }

            Vector3 Place(Vector3 c, float w)
            {
                float az = Mathf.Lerp(s.azimuth.x, s.azimuth.y, w) * Mathf.Deg2Rad;
                return c + new Vector3(Mathf.Sin(az) * s.radius, s.height, -Mathf.Cos(az) * s.radius);
            }

            protected override CamPose Pose(MultiCamContext ctx)
            {
                TargetSample ts;
                if (!Sample(ctx, target, out ts)) return hasLast ? Hold(PoseFlags.NoTarget) : NoPose(PoseFlags.NoTarget);
                int flags = 0;
                var c = ts.pos + s.offset;
                double local = ctx.LocalTime(s.start);
                bool waiting = double.IsNaN(local) || local < 0;
                if (waiting) flags |= PoseFlags.Waiting;
                float x = !waiting && s.duration > 0f ? Mathf.Clamp01((float)(local / s.duration)) : 0f;
                var p = Place(c, s.ease == "linear" ? x : Smooth(x));
                float fov = Fov(ctx);
                if (lookNone) { var e = Euler(p, s.pitch, s.yaw, s.roll, fov); e.flags |= flags; return e; }
                Vector3 a;
                if (!look.Point(ctx, out a, ref flags)) a = c + s.lookOffset;
                return Look(ctx, p, a, fov, flags);
            }

            public override void Plan(MultiCamContext ctx, List<CamPose> poses)
            {
                TargetSample ts;
                if (!Sample(ctx, target, out ts)) return;
                var c = ts.pos + s.offset;
                for (int k = 0; k <= 6; k++)
                {
                    var p = Place(c, k / 6f);
                    poses.Add(lookNone ? Euler(p, s.pitch, s.yaw, s.roll, Fov(ctx)) : LookPose(p, c + s.lookOffset, Vector3.up, s.roll, Fov(ctx)));
                }
            }
        }

        sealed class GameRelRig : RigBase
        {
            public GameRelRig(CamSpec spec) : base(spec) { }
            protected override void Reset(MultiCamContext ctx) { }

            protected override CamPose Pose(MultiCamContext ctx)
            {
                var g = ctx.game;
                if (!g.ok) return hasLast ? Hold(PoseFlags.NoTarget) : NoPose(PoseFlags.NoTarget);
                var cl = g.clean;
                var bp = s.clean ? g.clean : g.rendered;
                var pivot = cl.p + cl.forward * g.aimDistance;
                var v = bp.p - pivot;
                if (v.sqrMagnitude < 1e-6f) v = -cl.forward * g.aimDistance;
                var up = g.positionUp.sqrMagnitude > 1e-4f ? g.positionUp.normalized : Vector3.up;
                v = Quaternion.AngleAxis(s.orbitYaw, Vector3.up) * v;
                var right = Vector3.Cross(up, -v.normalized);
                if (right.sqrMagnitude > 1e-8f) v = Quaternion.AngleAxis(s.orbitPitch, right.normalized) * v;
                float len = s.dist > 0f ? s.dist : v.magnitude * s.distScale;
                float gameFov = bp.fov > 1f ? bp.fov : MultiCamConst.GameFov;
                float fov = s.fov > 0f ? s.fov : gameFov;
                if (s.fov > 0f && s.keepSize) len *= Mathf.Tan(gameFov * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
                var p = pivot + v.normalized * len;
                var aim = pivot;
                if (s.shift != Vector2.zero)
                {
                    var rot = LookRot(aim - p, up);
                    var sh = rot * new Vector3(s.shift.x, s.shift.y, 0f);
                    p += sh; aim += sh;
                }
                aimed = true;
                var c = LookPose(p, aim, up, s.roll, fov);
                return c;
            }
        }

        sealed class GameLikeRig : RigBase
        {
            TargetRef target;
            CameraBlendBox fixedBox, curBox, prevBox;
            bool boxNone, init, hasCur, blending, useGameExt;
            string lag;
            bool planar;
            CameraBlendProbe probe;
            AnimationCurve cx, cy, cz, extCurve;
            float extSpeed, ext, blendT, blendDur;
            Vector3 prevP, cam, aim, velC, velA;
            public GameLikeRig(CamSpec spec) : base(spec) { }

            protected override void Reset(MultiCamContext ctx)
            {
                target = TargetOf(s);
                lag = s.Has("lag") ? s.lag : "game";
                planar = s.Has("planar") ? s.planar : true;
                probe = null;
                try { var cs = CameraScript.main; probe = cs != null ? cs.cameraBlendProbe : null; } catch { probe = null; }
                GameCurves(out cx, out cy, out cz);
                extCurve = probe != null && probe.extrapolationCurve != null && probe.extrapolationCurve.length > 0 ? probe.extrapolationCurve : new AnimationCurve(new Keyframe(0f, 0.3f), new Keyframe(0.7f, 1f));
                extSpeed = probe != null ? probe.extrapolationSpeed : 0.166f;
                useGameExt = target == null || (target.of == "probe" && !target.fixedPoint && !target.isGroup);
                boxNone = s.box == "none";
                fixedBox = null;
                if (!boxNone && !string.IsNullOrEmpty(s.box) && s.box != "auto")
                {
                    var rep = new SpecReport();
                    var go = MultiCamTargets.ResolveObject(s.box, Where(s) + ".box", rep);
                    fixedBox = go != null ? go.GetComponent<CameraBlendBox>() : null;
                    if (fixedBox == null && ctx != null) ctx.Log("warn camera " + s.name + ": camera box '" + s.box + "' not found" + (rep.errors.Count > 0 ? " (" + rep.errors[0] + ")" : "") + "; using auto");
                }
                init = false; hasCur = false; blending = false; ext = 0f;
                curBox = prevBox = null;
            }

            bool Anchor(MultiCamContext ctx, out Vector3 p)
            {
                p = Vector3.zero;
                if (target == null)
                {
                    if (!ctx.game.ok) return false;
                    p = ctx.game.probe;
                    return true;
                }
                TargetSample ts;
                if (!Sample(ctx, target, out ts)) return false;
                p = ts.pos;
                if (planar) p.z = probe != null ? probe.zPlayground : 0f;
                return true;
            }

            CameraBlendBox FindBox(Vector3 p)
            {
                var um = Umbrella<BlendBox>.instance;
                if (um == null || um.targets == null) return null;
                CameraBlendBox best = null;
                int bestPri = int.MinValue;
                var layer = probe != null ? probe.cameraLayer : CameraLayer.Any;
                for (int i = 0; i < um.size && i < um.targets.Length; i++)
                {
                    var cb = um.targets[i] as CameraBlendBox;
                    if (cb == null || !cb.isActive || !cb.enabled) continue;
                    if (cb.cameraLayer != CameraLayer.Any && layer != CameraLayer.Any && cb.cameraLayer != layer) continue;
                    var bc = cb.boxCollider;
                    if (bc == null || !bc.Contains(p)) continue;
                    if (cb.priority >= bestPri) { best = cb; bestPri = cb.priority; }
                }
                return best;
            }

            CameraBlendBox RecordedBox(MultiCamContext ctx, Vector3 p)
            {
                string name = ctx.game.ok ? ctx.game.box : "";
                if (string.IsNullOrEmpty(name)) return FindBox(p);
                var um = Umbrella<BlendBox>.instance;
                if (um == null || um.targets == null) return FindBox(p);
                CameraBlendBox best = null;
                bool bestIn = false;
                int bestPri = int.MinValue;
                for (int i = 0; i < um.size && i < um.targets.Length; i++)
                {
                    var cb = um.targets[i] as CameraBlendBox;
                    if (cb == null || cb.name != name) continue;
                    var bc = cb.boxCollider;
                    bool inside = bc != null && bc.Contains(p);
                    if (best != null && (bestIn && !inside || bestIn == inside && cb.priority <= bestPri)) continue;
                    best = cb; bestIn = inside; bestPri = cb.priority;
                }
                return best != null ? best : FindBox(p);
            }

            CameraPivot Pivot(CameraBlendBox box, Vector3 p)
            {
                var pv = new CameraPivot();
                if (box != null)
                {
                    try
                    {
                        box.UpdateCameraPivot(ref pv, p, probe);
                        if (pv.up.sqrMagnitude < 1e-6f) pv.up = Vector3.up;
                        return pv;
                    }
                    catch { pv = new CameraPivot(); }
                }
                bool own = s.Has("camOffset") || probe == null;
                pv.camera = p + (own ? s.camOffset : probe.defaultCamera);
                pv.aim = p + (s.Has("aimOffset") || probe == null ? s.aimOffset : probe.defaultAim);
                pv.up = probe != null && probe.defaultUp.sqrMagnitude > 1e-6f ? probe.defaultUp : Vector3.up;
                pv.extrapolateCamera = probe != null ? probe.defaultExtrapolateCamera : 3f;
                pv.extrapolateAim = probe != null ? probe.defaultExtrapolateAim : 3f;
                return pv;
            }

            protected override CamPose Pose(MultiCamContext ctx)
            {
                Vector3 p;
                if (!Anchor(ctx, out p)) return hasLast ? Hold(PoseFlags.NoTarget) : NoPose(PoseFlags.NoTarget);
                float dt = ctx.dt;
                if (!init) { prevP = p; ext = useGameExt && ctx.game.ok ? ctx.game.extrapolation : 0f; }
                else
                {
                    float dx = p.x - prevP.x;
                    prevP = p;
                    float w = extCurve.Evaluate(Mathf.InverseLerp(0f, 2f, Mathf.Abs(Mathf.Sign(dx) - ext)));
                    ext = Mathf.Clamp(ext + dx * extSpeed * w, -1f, 1f);
                }
                var box = boxNone ? null : fixedBox != null ? fixedBox : useGameExt && ReplayHub.replaying ? RecordedBox(ctx, p) : FindBox(p);
                int flags = 0;
                if (!hasCur) { curBox = box; hasCur = true; }
                else if (box != curBox)
                {
                    prevBox = curBox;
                    curBox = box;
                    blendT = 0f;
                    blendDur = box != null ? box.timeIn : prevBox != null ? prevBox.timeOut : 0f;
                    blending = blendDur > 1e-4f && init;
                }
                var pv = Pivot(curBox, p);
                if (blending)
                {
                    float w = Mathf.Clamp01(blendT / blendDur);
                    blendT += dt;
                    if (w >= 1f) blending = false;
                    else
                    {
                        var pp = Pivot(prevBox, p);
                        var res = new CameraPivot();
                        CameraPivot.Lerp(ref res, ref pp, ref pv, Smooth(w));
                        pv = res;
                        flags |= PoseFlags.Blending;
                    }
                }
                float ec = s.extrapolate >= 0f ? s.extrapolate : pv.extrapolateCamera;
                float ea = s.extrapolate >= 0f ? s.extrapolate : pv.extrapolateAim;
                var wantC = pv.camera + Vector3.right * ext * ec;
                var wantA = pv.aim + Vector3.right * ext * ea + s.lookOffset;
                if (!init) { cam = wantC; aim = wantA; velC = velA = Vector3.zero; init = true; }
                else if (lag == "none") { cam = wantC; aim = wantA; }
                else if (lag == "damp") { cam = Damp(cam, wantC, ref velC, s.smooth, dt); aim = Damp(aim, wantA, ref velA, s.smooth, dt); }
                else { cam = GameLaw(cam, wantC, dt, cx, cy, cz); aim = GameLaw(aim, wantA, dt, cx, cy, cz); }
                aimed = true;
                var c = LookPose(cam, aim, pv.up, s.roll, Fov(ctx));
                c.flags |= flags;
                return c;
            }
        }

        sealed class FrameRig : RigBase
        {
            TargetRef target;
            Vector3 cur, vel, ffV;
            bool init, ffInit;
            public FrameRig(CamSpec spec) : base(spec) { }

            protected override void Reset(MultiCamContext ctx) { target = TargetOf(s); init = false; ffInit = false; }

            protected override CamPose Pose(MultiCamContext ctx)
            {
                TargetSample ts;
                if (!Sample(ctx, target, out ts)) return hasLast ? Hold(PoseFlags.NoTarget) : NoPose(PoseFlags.NoTarget);
                var subject = ts.pos + (s.lead > 0f ? Vector3.ClampMagnitude(ts.vel * s.lead, 4f) : Vector3.zero);
                float h = s.subjectHeight > 0f ? s.subjectHeight : ts.height > 0.05f ? ts.height : MultiCamConst.BoyHeight;
                var r = Quaternion.Euler(s.pitch, s.yaw, s.roll);
                float fov = Fov(ctx);
                float tv = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
                float aspect = ctx.aspect > 0.1f ? ctx.aspect : 16f / 9f;
                float th = tv * aspect;
                float u = s.screen.x, v = s.screen.y, size = s.size;
                if (s.crop != null && s.crop.Length == 4 && s.crop[2] > 0f && s.crop[3] > 0f && ctx.screenW > 0 && ctx.screenH > 0)
                {
                    u = (s.crop[0] + u * s.crop[2]) / ctx.screenW;
                    v = (s.crop[1] + v * s.crop[3]) / ctx.screenH;
                    size = size * s.crop[3] / ctx.screenH;
                }
                size = Mathf.Max(size, 0.01f);
                float d = h / (2f * size * tv);
                float xn = 2f * u - 1f, yn = 1f - 2f * v;
                var want = subject - r * new Vector3(xn * th * d, yn * tv * d, d);
                SetSubject(ts.pos, ts.vel);
                SetMove(ts.vel);
                var ahead = s.feedForward ? MultiCamGeom.FeedLead(ts.vel, ref ffV, ref ffInit, s.smooth, ctx.dt) : Vector3.zero;
                if (!init) { cur = want; vel = s.smooth > 0f ? ahead / s.smooth : Vector3.zero; init = true; }
                else cur = Damp(cur, want + ahead, ref vel, s.smooth, ctx.dt);
                var c = new CamPose();
                c.ok = true; c.p = cur; c.r = r; c.fov = fov; c.aim = subject;
                return c;
            }
        }

        sealed class DirectorRig : RigBase
        {
            int cur = -1, from = -1;
            bool blending, fromLive, canCopy;
            double blendClock;
            float blendLen;
            CamPose fromSnap;
            public DirectorRig(CamSpec spec) : base(spec) { }
            protected override bool ClampAllowed { get { return blending; } }
            protected override bool ShakeAllowed { get { return false; } }
            protected override bool LimitAllowed { get { return false; } }

            // Blend length in the shot's clock: at least `blend`, and long enough for the motion limits over the blend path
            // (sampled length of the interpolated camera path, turn between the end orientations).
            float Stretch(MultiCamContext ctx, ShotSpec shot, CamPose a, CamPose b, string transition)
            {
                var m = Motion;
                float len = 0f;
                var prev = a.p;
                for (int k = 1; k <= 16; k++)
                {
                    var q = Blend(a, b, Ease(shot.ease, k / 16f), transition);
                    len += (q.p - prev).magnitude;
                    prev = q.p;
                }
                float turn = Vector3.Angle(a.forward, b.forward);
                float need = MultiCamGeom.MoveSeconds(len, turn, shot.ease, m.maxSpeed, m.maxTurn, m.ease);
                float inClock = shot.clock == "output" ? need : need * Math.Max(ctx.speed, 1e-3f);
                if (inClock > shot.blend + 1e-3f)
                    ctx.Log("shot " + s.name + " -> " + shot.cam + ": blend " + shot.blend.ToString("0.##", IC) + " s stretched to " + inClock.ToString("0.##", IC) + " s by the motion limits (" +
                            len.ToString("0.#", IC) + " m, " + turn.ToString("0", IC) + " deg; maxSpeed " + m.maxSpeed.ToString("0.##", IC) + " m/s, maxTurn " + m.maxTurn.ToString("0.#", IC) + " deg/s, ease " + m.ease.ToString("0.##", IC) + " s)");
                return Math.Max(shot.blend, inClock);
            }

            protected override void Reset(MultiCamContext ctx)
            {
                cur = -1; from = -1; blending = false; blendLen = 0f;
                canCopy = !(s.Has("post") || s.Has("render") || s.hide.Count > 0);
            }

            int Current(MultiCamContext ctx, out double at)
            {
                int idx = -1;
                at = double.NaN;
                double best = double.NegativeInfinity;
                for (int i = 0; i < s.shots.Count; i++)
                {
                    var sh = s.shots[i];
                    if (sh == null) continue;
                    double tf = ctx.TimeOf(sh.from, 0.0);
                    if (double.IsNaN(tf) || ctx.t < tf - 1e-6 || tf < best) continue;
                    best = tf; idx = i; at = tf;
                }
                return idx;
            }

            int First(MultiCamContext ctx)
            {
                int idx = -1;
                double best = double.PositiveInfinity;
                for (int i = 0; i < s.shots.Count; i++)
                {
                    var sh = s.shots[i];
                    if (sh == null) continue;
                    double tf = ctx.TimeOf(sh.from, 0.0);
                    if (!double.IsNaN(tf) && tf < best) { best = tf; idx = i; }
                }
                if (idx >= 0) return idx;
                for (int i = 0; i < s.shots.Count; i++) if (s.shots[i] != null) return i;
                return -1;
            }

            string Transition(ShotSpec shot) { return v1 && !shot.Has("transition") ? "linear" : shot.transition; }

            bool AutoCut(CamPose a, CamPose b)
            {
                if (Vector3.Angle(a.forward, b.forward) > s.cutAngle) return true;
                return s.cutDistance > 0f && (a.aim - b.aim).magnitude > s.cutDistance;
            }

            static CamPose Blend(CamPose a, CamPose b, float w, string transition)
            {
                var c = new CamPose();
                c.ok = true;
                float na = a.near > 0f ? a.near : MultiCamConst.GameNear, nb = b.near > 0f ? b.near : MultiCamConst.GameNear;
                c.near = Mathf.Lerp(na, nb, w);
                c.fov = Mathf.Lerp(a.fov, b.fov, w);
                float da = (a.p - a.aim).magnitude, db = (b.p - b.aim).magnitude;
                if (transition == "linear" || da < 1e-3f || db < 1e-3f)
                {
                    c.p = Vector3.Lerp(a.p, b.p, w);
                    c.aim = Vector3.Lerp(a.aim, b.aim, w);
                    c.r = Quaternion.Slerp(a.r, b.r, w);
                    return c;
                }
                c.p = CameraBlendProbe.InterpolateCamera(a.aim, b.aim, a.p, b.p, w);
                c.aim = CameraBlendProbe.InterpolateAim(a.p, b.p, a.aim, b.aim, w);
                var up = Vector3.Slerp(a.r * Vector3.up, b.r * Vector3.up, w);
                c.r = LookRot(c.aim - c.p, up);
                return c;
            }

            bool Enabled(MultiCamContext ctx, string cam)
            {
                var c = ctx.spec != null ? ctx.spec.FindCamera(cam) : null;
                return c != null && c.enabled;
            }

            protected override CamPose Pose(MultiCamContext ctx)
            {
                if (s.shots.Count == 0) return NoPose(0);
                int flags = 0;
                double at;
                int idx = Current(ctx, out at);
                if (idx < 0) { idx = First(ctx); flags |= PoseFlags.Waiting; }
                if (idx < 0) return NoPose(0);
                var shot = s.shots[idx];
                CamPose target;
                bool haveTarget = ctx.PoseOf(shot.cam, out target) && target.ok;
                if (idx != cur)
                {
                    int prev = cur;
                    cur = idx;
                    if (prev < 0 || !hasLast) blending = false;
                    else
                    {
                        CamPose src;
                        bool wasBlending = blending;
                        if (wasBlending) { fromSnap = lastRaw; fromLive = false; }
                        else
                        {
                            fromLive = ctx.PoseOf(s.shots[prev].cam, out src) && src.ok;
                            fromSnap = fromLive ? src : lastRaw;
                        }
                        from = prev;
                        string transition = Transition(shot);
                        bool cut = transition == "cut" || shot.blend <= 0f || !haveTarget || AutoCut(fromSnap, target);
                        if (cut) { blending = false; flags |= PoseFlags.Cut; }
                        else
                        {
                            blending = true;
                            double since = double.IsNaN(at) ? 0.0 : Math.Max(0.0, ctx.t - at);
                            blendClock = shot.clock == "output" ? since / Math.Max(ctx.speed, 1e-3f) : since;
                        }
                        ctx.Log("shot " + s.name + " -> " + shot.cam + (cut ? " (cut)" : " (" + transition + " " + shot.blend.ToString("0.##", IC) + " s)"));
                        if (blending) blendLen = Stretch(ctx, shot, fromSnap, target, transition);
                    }
                }
                else if (blending) blendClock += shot.clock == "output" ? ctx.outputDt : ctx.dt;
                if (!haveTarget) return hasLast ? Hold(PoseFlags.NoTarget | flags) : NoPose(PoseFlags.NoTarget);
                if (blending)
                {
                    float bl = Math.Max(shot.blend, blendLen);
                    float w = bl > 0f ? Mathf.Clamp01((float)(blendClock / bl)) : 1f;
                    if (w >= 1f) blending = false;
                    else
                    {
                        var a = fromSnap;
                        if (fromLive && from >= 0)
                        {
                            CamPose live;
                            if (ctx.PoseOf(s.shots[from].cam, out live) && live.ok) { a = live; fromSnap = live; }
                        }
                        aimed = true;
                        var b = Blend(a, target, Ease(shot.ease, w), Transition(shot));
                        b.flags |= flags | PoseFlags.Blending;
                        return b;
                    }
                }
                var hp = target;
                hp.flags = (target.flags & ~PoseFlags.Copy) | flags;
                hp.copyOf = string.IsNullOrEmpty(target.copyOf) ? shot.cam : target.copyOf;
                if (canCopy && Enabled(ctx, hp.copyOf)) hp.flags |= PoseFlags.Copy;
                return hp;
            }
        }

        // overview: fixed height, depth, orientation and fov (pos.y, pos.z, pitch, yaw, fov); the frame centre x follows the
        // target's x (+ offset.x, + lead) with `smooth` damping, limited before the damping so the frame stops softly at the
        // clamp edges (clamp.left / clamp.right = world x of the frame edges at the subject depth). pans[]: from each `from`
        // the centre moves to `to` over `duration` and holds there (the ending pan back over the route).
        sealed class OverviewRig : RigBase
        {
            TargetRef target;
            bool init, hasOut, ffInit;
            float cur, vel, panFrom, lastOut;
            Vector3 ffV;
            int panAt;
            public OverviewRig(CamSpec spec) : base(spec) { }

            protected override void Reset(MultiCamContext ctx) { target = TargetOf(s); init = false; hasOut = false; panAt = -1; ffInit = false; }

            float Focus { get { return s.focus > 0f ? s.focus : 20f; } }

            void Limits(MultiCamContext ctx, Quaternion rot, float fov, out float lo, out float hi)
            {
                lo = float.NegativeInfinity; hi = float.PositiveInfinity;
                if (s.clamp == null) return;
                var p0 = new Vector3(0f, s.pos.y, s.pos.z);
                float l, r;
                if (!Edges(ctx, p0, rot, fov, p0 + rot * Vector3.forward * Focus, out l, out r)) return;
                if (s.clamp.Has("left")) lo = s.clamp.left - l;
                if (s.clamp.Has("right")) hi = s.clamp.right - r;
                if (lo > hi) { float m = (lo + hi) * 0.5f; lo = m; hi = m; }
            }

            int PanIndex(MultiCamContext ctx, out double from)
            {
                int k = -1;
                from = double.NaN;
                for (int i = 0; i < s.pans.Count; i++)
                {
                    var pn = s.pans[i];
                    if (pn == null) continue;
                    double f = ctx.TimeOf(pn.from, double.NaN);
                    if (double.IsNaN(f) || ctx.t < f - 1e-9 || (k >= 0 && f < from)) continue;
                    k = i; from = f;
                }
                return k;
            }

            protected override CamPose Pose(MultiCamContext ctx)
            {
                int flags = 0;
                float fov = Fov(ctx);
                var rot = Quaternion.Euler(s.pitch, s.yaw, s.roll);
                float lo, hi;
                Limits(ctx, rot, fov, out lo, out hi);
                float want = s.pos.x;
                TargetSample ts = default(TargetSample);
                bool sampled = target != null && Sample(ctx, target, out ts);
                if (sampled) { want = ts.pos.x + s.offset.x + (s.lead > 0f ? Mathf.Clamp(ts.vel.x * s.lead, -4f, 4f) : 0f); SetSubject(ts.pos, ts.vel); }
                else if (target != null) flags |= PoseFlags.NoTarget;
                bool waiting = s.start != null && !ctx.Reached(s.start);
                if (waiting) { flags |= PoseFlags.Waiting; want = s.pos.x; }
                if (want < lo || want > hi) flags |= PoseFlags.Clamped;
                float x = Mathf.Clamp(want, lo, hi);
                float ahead = sampled && !waiting && s.feedForward ? MultiCamGeom.FeedLead(new Vector3(ts.vel.x, 0f, 0f), ref ffV, ref ffInit, s.smooth, ctx.dt).x : 0f;
                if (sampled && !waiting) SetMove(new Vector3(ts.vel.x, 0f, 0f));
                if (!init || s.smooth <= 0f) { cur = x; vel = s.smooth > 0f ? ahead / s.smooth : 0f; init = true; }
                else if (ctx.dt > 0f) cur = Mathf.SmoothDamp(cur, Mathf.Clamp(x + ahead, lo, hi), ref vel, s.smooth, Mathf.Infinity, ctx.dt);
                cur = Mathf.Clamp(cur, lo, hi);
                double from;
                int k = PanIndex(ctx, out from);
                float outX = cur;
                if (k >= 0)
                {
                    if (k != panAt) { panFrom = hasOut ? lastOut : cur; panAt = k; }
                    var pn = s.pans[k];
                    outX = Mathf.Clamp(MultiCamGeom.Pan(panFrom, pn.to, from, pn.duration, ctx.t, pn.ease), lo, hi);
                }
                lastOut = outX;
                hasOut = true;
                var c = CamPose.Make(new Vector3(outX, s.pos.y, s.pos.z), rot, fov, Focus);
                c.flags |= flags;
                return c;
            }

            public override void Plan(MultiCamContext ctx, List<CamPose> poses)
            {
                float fov = Fov(ctx), lo, hi;
                var rot = Quaternion.Euler(s.pitch, s.yaw, s.roll);
                Limits(ctx, rot, fov, out lo, out hi);
                float a = float.IsInfinity(lo) ? s.pos.x : lo, b = float.IsInfinity(hi) ? s.pos.x : hi;
                for (int i = 0; i <= 4; i++) poses.Add(CamPose.Make(new Vector3(Mathf.Lerp(a, b, i / 4f), s.pos.y, s.pos.z), rot, fov, Focus));
            }
        }

        // events: beats on spec / recorded events (at), each held `hold` s on its target (+ offset, looking at target +
        // lookOffset), scheduled one after another (MultiCamGeom.Schedule; a beat later than `late` after its event is
        // dropped). The camera moves from the previous beat's live pose to the next over `move` s (ease). Each beat picks
        // the first of its candidate offsets whose view is clear (MultiCamView: no collider or huddle / avoid bounds on the
        // camera -> subject ray, near plane outside geometry, player side) at its first evaluation, else the least bad one;
        // the choice is logged. Before the first beat (or before `start`): the camera target (idle) or the first beat's pose.
        sealed class EventsRig : RigBase
        {
            sealed class Beat
            {
                public BeatSpec b;
                public TargetRef target;
                public Vector3 lookOffset, cur, vel, ffV, subj, subjVel;
                public readonly List<Vector3> offsets = new List<Vector3>();
                public float hold, move, late, fov;
                public int chosen = -1;
                public bool init, ffInit;
                public double lastT;
                public string label = "";
            }

            readonly List<Beat> beats = new List<Beat>();
            Beat idle;
            int lastCur = -2;
            float moveLen = -1f;
            bool moving, useSnap;
            CamPose moveSnap;
            double[] at = new double[0];
            float[] holds = new float[0], lates = new float[0];
            public EventsRig(CamSpec spec) : base(spec) { }
            protected override bool LimitAllowed { get { return false; } }

            protected override void Reset(MultiCamContext ctx)
            {
                beats.Clear();
                moveLen = -1f;
                moving = false; useSnap = false;
                for (int i = 0; i < s.beats.Count; i++)
                {
                    var b = s.beats[i];
                    if (b == null) continue;
                    var k = Make(b, b.target ?? s.target, b.target != null ? b.point : s.point);
                    k.label = b.label.Length > 0 ? b.label : (b.at != null ? b.at.ToString() : "#" + i.ToString(IC));
                    beats.Add(k);
                }
                var t = TargetOf(s);
                idle = t != null ? Make(null, t, "") : null;
                if (idle != null) idle.label = "idle";
                at = new double[beats.Count];
                holds = new float[beats.Count];
                lates = new float[beats.Count];
                for (int i = 0; i < beats.Count; i++) { holds[i] = beats[i].hold; lates[i] = beats[i].late; }
                lastCur = -2;
            }

            Beat Make(BeatSpec b, TargetRef t, string point)
            {
                var k = new Beat { b = b };
                k.target = t != null && !string.IsNullOrEmpty(point) && point != "pivot" && !t.fixedPoint && !t.isGroup && string.IsNullOrEmpty(t.point) ? WithPoint(t, point) : t;
                k.hold = b != null && b.hold > 0f ? b.hold : s.hold;
                k.move = b != null && b.move > 0f ? b.move : s.move;
                k.late = b != null && b.late >= 0f ? b.late : s.late;
                k.fov = b != null && b.fov > 0f ? b.fov : 0f;
                k.lookOffset = b != null && b.Has("lookOffset") ? b.lookOffset : s.lookOffset;
                if (b != null && b.offsets.Count > 0) k.offsets.AddRange(b.offsets);
                else if (b != null && b.Has("offset")) k.offsets.Add(b.offset);
                else if (s.offsets.Count > 0) k.offsets.AddRange(s.offsets);
                else k.offsets.Add(s.offset);
                return k;
            }

            string EaseName { get { return s.ease == "ends" ? "smooth" : s.ease; } }

            bool BeatPose(MultiCamContext ctx, Beat k, out CamPose pose, ref int flags)
            {
                pose = default(CamPose);
                TargetSample ts;
                if (k.target == null || !Sample(ctx, k.target, out ts)) { flags |= PoseFlags.NoTarget; return false; }
                bool restart = !k.init || ctx.t < k.lastT - 1e-9 || ctx.t - k.lastT > 0.25 || s.smooth <= 0f;
                if (restart) k.ffInit = false;
                var ahead = s.feedForward ? MultiCamGeom.FeedLead(ts.vel, ref k.ffV, ref k.ffInit, s.smooth, ctx.dt) : Vector3.zero;
                if (restart) { k.cur = ts.pos; k.vel = s.smooth > 0f ? ahead / s.smooth : Vector3.zero; k.init = true; }
                else k.cur = Damp(k.cur, ts.pos + ahead, ref k.vel, s.smooth, ctx.dt);
                k.subj = ts.pos; k.subjVel = ts.vel;
                k.lastT = ctx.t;
                if (k.chosen < 0) Choose(ctx, k, ts.pos, ts.go);
                var off = k.offsets[Mathf.Clamp(k.chosen, 0, k.offsets.Count - 1)];
                pose = LookPose(k.cur + off, k.cur + k.lookOffset, Vector3.up, s.roll, k.fov > 0f ? k.fov : Fov(ctx));
                return true;
            }

            void Choose(MultiCamContext ctx, Beat k, Vector3 subject, GameObject go)
            {
                var v = MultiCamView.Spec(s);
                List<Bounds> avoid = null;
                try { avoid = MultiCamView.Avoid(ctx, v); } catch (Exception e) { ctx.Log("warn camera " + s.name + " view avoid: " + e.Message); }
                var own = MultiCamView.Own(k.target, go);
                int best = 0, bestScore = int.MaxValue;
                string why = "";
                for (int i = 0; i < k.offsets.Count; i++)
                {
                    var p = LookPose(subject + k.offsets[i], subject + k.lookOffset, Vector3.up, s.roll, k.fov > 0f ? k.fov : Fov(ctx));
                    p.near = s.near;
                    int score = s.playerSide && p.p.z > s.zMax ? 8 : 0;
                    string text = "";
                    try
                    {
                        var r = MultiCamView.Check(p, subject, v, avoid, Aspect(ctx), own);
                        if (!r.visible) score += 2;
                        if (r.inside) score += 3;
                        text = r.Text();
                    }
                    catch (Exception e) { text = "view check failed: " + e.Message; score += 1; }
                    if (score < bestScore) { best = i; bestScore = score; why = text; }
                    if (score == 0) break;
                }
                k.chosen = best;
                ctx.Log("camera " + s.name + " beat " + k.label + ": offset " + best + " of " + k.offsets.Count + (bestScore == 0 ? " (clear)" : " (no clear candidate, least bad: " + why + ")"));
            }

            protected override CamPose Pose(MultiCamContext ctx)
            {
                int flags = 0;
                bool wasMoving = moving;
                moving = false;
                for (int i = 0; i < beats.Count; i++) at[i] = ctx.TimeOf(beats[i].b.at, double.NaN);
                var start = MultiCamGeom.Schedule(at, holds, lates);
                int c = MultiCamGeom.Current(start, ctx.t);
                bool waiting = s.start != null && !ctx.Reached(s.start);
                CamPose to;
                if (c < 0 || waiting)
                {
                    flags |= PoseFlags.Waiting;
                    if (idle != null && BeatPose(ctx, idle, out to, ref flags)) { SetSubject(idle.subj, idle.subjVel); to.flags |= flags; return to; }
                    int first = -1;
                    for (int i = 0; i < start.Length; i++) if (!double.IsNaN(start[i]) && (first < 0 || start[i] < start[first])) first = i;
                    if (first >= 0 && BeatPose(ctx, beats[first], out to, ref flags)) { SetSubject(beats[first].subj, beats[first].subjVel); to.flags |= flags; return to; }
                    return hasLast ? Hold(flags | PoseFlags.NoTarget) : NoPose(PoseFlags.NoTarget);
                }
                if (c != lastCur)
                {
                    lastCur = c;
                    moveLen = -1f;
                    useSnap = wasMoving && hasLast;
                    moveSnap = lastRaw;
                    ctx.Log("camera " + s.name + " -> beat " + beats[c].label + " at " + start[c].ToString("0.###", IC) + " (event " + at[c].ToString("0.###", IC) + ")");
                }
                if (!BeatPose(ctx, beats[c], out to, ref flags)) return hasLast ? Hold(flags) : NoPose(flags);
                SetSubject(beats[c].subj, beats[c].subjVel);
                float mv = Math.Max(beats[c].move, moveLen);
                float w = mv > 0f ? (float)((ctx.t - start[c]) / mv) : 1f;
                if (w < 1f)
                {
                    int pv = MultiCamGeom.Previous(start, c);
                    CamPose from = default(CamPose);
                    int ff = 0;
                    bool has = useSnap ? true : pv >= 0 ? BeatPose(ctx, beats[pv], out from, ref ff) : (idle != null && BeatPose(ctx, idle, out from, ref ff));
                    if (useSnap) from = moveSnap;
                    if (has && moveLen < 0f && beats[c].move > 0f)
                    {
                        moveLen = MoveStretch(ctx, beats[c], from, to);
                        mv = Math.Max(beats[c].move, moveLen);
                        w = (float)((ctx.t - start[c]) / mv);
                    }
                    if (has && w < 1f)
                    {
                        float e = MultiCamGeom.Ease(EaseName, w);
                        var p = Vector3.Lerp(from.p, to.p, e);
                        var a = Vector3.Lerp(from.aim, to.aim, e);
                        var b = LookPose(p, a, Vector3.up, s.roll, Mathf.Lerp(from.fov, to.fov, e));
                        aimed = true;
                        b.flags |= flags | PoseFlags.Blending;
                        moving = true;
                        return b;
                    }
                }
                aimed = true;
                to.flags |= flags;
                return to;
            }

            // Move length (game seconds) of a beat move: at least `move`, and long enough for the motion limits over the straight
            // camera path and the turn between the two beat poses.
            float MoveStretch(MultiCamContext ctx, Beat k, CamPose a, CamPose b)
            {
                var m = Motion;
                float dist = (b.p - a.p).magnitude, turn = Vector3.Angle(a.forward, b.forward);
                float need = MultiCamGeom.MoveSeconds(dist, turn, EaseName, m.maxSpeed, m.maxTurn, m.ease) * Math.Max(ctx.speed, 1e-3f);
                if (need > k.move + 1e-3f)
                    ctx.Log("camera " + s.name + " beat " + k.label + ": move " + k.move.ToString("0.##", IC) + " s stretched to " + need.ToString("0.##", IC) + " s by the motion limits (" +
                            dist.ToString("0.#", IC) + " m, " + turn.ToString("0", IC) + " deg)" + (need > k.hold + 1e-3f ? "; longer than its hold " + k.hold.ToString("0.##", IC) + " s" : ""));
                return need;
            }

            public override void Plan(MultiCamContext ctx, List<CamPose> poses)
            {
                foreach (var k in beats)
                {
                    TargetSample ts;
                    if (k.target == null || !Sample(ctx, k.target, out ts)) continue;
                    foreach (var o in k.offsets) poses.Add(LookPose(ts.pos + o, ts.pos + k.lookOffset, Vector3.up, s.roll, k.fov > 0f ? k.fov : Fov(ctx)));
                }
            }
        }
    }
}
