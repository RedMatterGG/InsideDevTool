using System;
using System.Collections.Generic;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // WorldCamera.cs (owner WORLD_B, public API and the camera schema frozen): the gameplay camera (report 03 section
    // 5). CameraScript.main: positions, target, paused, GetActiveCameraModifier, cameraBlendProbe.GetWeights;
    // CameraBlendBox.ActivateBox / DeactivateBox (622 boxes); SetTarget, AddPointOfInterest / ClearPointOfInterest,
    // FastBlend, bCustomEffects / bHandshakeEffect; pose through the core CameraControl (free camera). Streaming follows
    // Camera.main: while any override is active the body's subscene is pinned (PlayLevels.Pin, owner "camera") unless
    // pin is false; Restorer key "camera" puts target, POIs, effects, boxes and the free camera back and unpins.
    // Overrides are: follow (another target), poi, box_set (originals kept per box), pose (the free camera, when this
    // tool turned it on, or moved it), shot_route apply (render tool). fx alone only registers the undo. A tick every
    // 30 frames moves the pin when the body walks into another subscene and retries a pin that failed (pin_unknown);
    // the core free camera turned on outside this tool (hotkey, command freecam) gets the same pin (owner "freecam")
    // while it is on. The pin keeps the subscene, but BoundsCullerRuntime still switches the _CULL gameplay containers
    // on the Camera.main frustum (FP/BoundsCullerRuntime.cs:243, 439-466): observe warns camera_away meanwhile.
    // Writes are refused during a MultiCam capture.
    // Verified against FP/CameraScript.cs:35-41, 73, 122-221, 262-273, 358-380; FP/CameraBlendBox.cs:136-198, 242-250;
    // FP/CameraBlendProbe.cs:150-153; core CameraControl.Pose / SetFree / free (pin).
    public static class WorldCamera
    {
        public const string Owner = "WORLD_B";
        public const string Key = "camera";
        public const string PinOwner = "camera";
        public const string FreePinOwner = "freecam";

        sealed class BoxOrig
        {
            public CameraBlendBox box;
            public bool active;
            public Vector3 pc, pa;
        }

        static readonly object gate = new object();
        static readonly Dictionary<int, BoxOrig> boxes = new Dictionary<int, BoxOrig>();
        static bool follow, poi, fx, posed, freeOwned, shakeBefore, handshakeBefore;
        static Transform prevTarget;
        static volatile bool overridden;
        static volatile int pinned = -1, freePinned = -1;
        static bool pinWanted = true;
        static int tickCount;

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("camera", "Camera", PlayShared.Dev, Owner,
                "The gameplay camera. state and boxes are read-only. box_set switches a CameraBlendBox (framing zone) on / off or moves its points; follow targets another object; poi adds or clears a point of interest; fx turns shake / handshake off or on; pose flies the free camera to a pose; release gives the camera back. While overridden the body's area is pinned so it does not unload. Writes are re-read after 2 frames (verdict) and undone by restore.",
                Schema.Obj().Enum("op", "state, boxes, box_set, follow, poi, fx, pose or release.", "state", "state", "boxes", "box_set", "follow", "poi", "fx", "pose", "release")
                    .Str("box", "op box_set: box id or path.").Bool("active", "op box_set.").Vec("position_camera", "op box_set: camera point.", 3, -1e5, 1e5)
                    .Vec("position_aim", "op box_set: aim point.", 3, -1e5, 1e5).Str("object", "op follow / poi: object id or path; empty poi clears.")
                    .Num("blend_s", "op follow: blend seconds.", 1, 0, 10).Num("radius", "op boxes (m).", 30, 1, 500)
                    .Bool("shake", "op fx.").Bool("handshake", "op fx.")
                    .Obj("pose", "op pose.", Schema.Obj().Num("x", "World x.", -1e5, 1e5).Num("y", "World y.", -1e5, 1e5).Num("z", "World z.", -1e5, 1e5)
                        .Num("yaw", "Degrees.", -720, 720).Num("pitch", "Degrees.", -90, 90).Num("fov", "Degrees.", 5, 140).Num("roll", "Degrees.", -180, 180)
                        .Num("seconds", "Ease time.", 0, 0, 30).Required("x", "y", "z"))
                    .Bool("pin", "Pin the body's area while overridden.", true).Int("limit", "op boxes: rows.", 30, 1, 100).Build(),
                Schema.Obj().Enum("outcome", "", null, "done", "refused")
                    .OutObj("camera", "Camera state.", Schema.Obj().Vec("pos", "", 3, -1e5, 1e5).Vec("aim", "", 3, -1e5, 1e5).Vec("up", "", 3, -1, 1).Num("fov", "", 0, 180)
                        .Bool("paused", "CameraScript.paused.").Str("target", "Followed object path.").Bool("overridden", "A tool holds the camera.").Bool("free", "Free camera on.")
                        .OutObj("active_box", "", Schema.Obj().Str("id", "").Str("path", "").Num("priority", "", -1e6, 1e6))
                        .Arr("weights", "Blend stack.", Schema.OutOf(Schema.Obj().Str("box", "").Num("value", "", 0, 1)), 0, 16))
                    .Arr("boxes", "op boxes.", Schema.OutOf(Schema.Obj().Str("id", "").Str("path", "").Bool("active", "").Num("priority", "", -1e6, 1e6)
                        .Num("time_in", "", 0, 1e3).Num("time_out", "", 0, 1e3).Vec("position_camera", "", 3, -1e5, 1e5).Vec("position_aim", "", 3, -1e5, 1e5).Num("dist", "", 0, 1e5)), 0, 100)
                    .Int("pinned_subscene", "Area pinned while overridden (-1 none).", -1, 999)
                    .Enum("verdict", "Re-read after 2 frames.", null, Reflect.Verdicts).Arr("warnings", "", Schema.Str(), 0, 20).BuildOutput()), Handle);
            PlayRegistry.OnTick("camera_pin", Tick);
        }

        // any thread
        public static bool Overridden { get { return overridden; } }
        public static int PinnedSubscene { get { return pinned; } }

        // main thread: back to the game camera (the Restorer "camera" undo)
        public static string Release()
        {
            var done = new List<string>();
            lock (gate)
            {
                int n = 0;
                foreach (var b in boxes.Values)
                {
                    if (b.box == null) continue;
                    b.box.isActive = b.active;
                    b.box.positionCamera = b.pc;
                    b.box.positionAim = b.pa;
                    n++;
                }
                if (n > 0) done.Add(n + (n == 1 ? " box" : " boxes"));
                boxes.Clear();
                var cs = CameraScript.main;
                if (follow && cs != null)
                {
                    var t = prevTarget != null ? prevTarget : MainTransform();
                    try { cs.SetTarget(t, 0f); done.Add("target"); } catch (Exception e) { done.Add("target failed: " + e.Message); }
                }
                if (poi && cs != null) { cs.ClearPointOfInterest(); done.Add("poi"); }
                if (fx && cs != null)
                {
                    cs.bCustomEffects = shakeBefore;
                    cs.bHandshakeEffect = handshakeBefore;
                    done.Add("fx");
                }
                if (freeOwned && CameraControl.free)
                {
                    bool cap = false;
                    try { cap = MultiCam.Capturing; } catch { }
                    if (!cap) { CameraControl.SetFree(false); done.Add("free camera off"); }
                    else done.Add("free camera kept (MultiCam capture)");
                }
                follow = poi = fx = posed = freeOwned = false;
                prevTarget = null;
                Unpin(done);
                overridden = false;
            }
            World.Forget(Key);
            return done.Count == 0 ? "nothing to release" : string.Join(", ", done.ToArray());
        }

        static Transform MainTransform()
        {
            var c = ScriptGlobals.mainCharacter;
            return c != null ? c.transform : null;
        }

        // ---------------------------------------------------------------- pinning (main thread)
        static void Unpin(List<string> done)
        {
            int p = pinned;
            if (p < 0) return;
            try { PlayLevels.Pin(p, false, PinOwner); if (done != null) done.Add("unpinned " + p); }
            catch (NotImplementedException) { }
            catch (Exception e) { if (done != null) done.Add("unpin " + p + " failed: " + e.Message); }
            pinned = -1;
        }

        static void PinBody(List<string> warnings)
        {
            if (!pinWanted) return;
            int sub;
            try { sub = PlayLevels.BodySubscene(); }
            catch (NotImplementedException)
            {
                warnings.Add("pin_unavailable: stream pinning is not built yet; the body's area may unload while the camera is away (about 4 s of game time)");
                return;
            }
            if (sub < 0) { warnings.Add("pin_unknown: the body's subscene is unknown, nothing pinned"); return; }
            if (sub == pinned) return;
            try
            {
                if (PlayLevels.Pin(sub, true, PinOwner))
                {
                    if (pinned >= 0) PlayLevels.Pin(pinned, false, PinOwner);
                    pinned = sub;
                }
                else warnings.Add("pin_failed: subscene " + sub + " could not be pinned");
            }
            catch (NotImplementedException) { warnings.Add("pin_unavailable: stream pinning is not built yet"); }
        }

        static void Tick()
        {
            if (++tickCount % 30 != 0) return;
            var w = new List<string>();
            try
            {
                lock (gate)
                {
                    if (overridden && posed && !CameraControl.free)
                    {
                        posed = false;
                        freeOwned = false;
                        Mark(pinWanted, w);
                    }
                }
                if (overridden)
                {
                    if (pinWanted) PinBody(w);
                    UnpinFree();
                    return;
                }
                bool free = false;
                try { free = CameraControl.free && !MultiCam.Capturing; } catch { }
                if (free) PinFree(); else UnpinFree();
            }
            catch { }
        }

        // the core free camera on without this tool: keep the body's subscene loaded while it is on
        static void PinFree()
        {
            int sub;
            try { sub = PlayLevels.BodySubscene(); } catch (NotImplementedException) { return; }
            if (sub < 0 || sub == freePinned) return;
            try
            {
                if (PlayLevels.Pin(sub, true, FreePinOwner))
                {
                    if (freePinned >= 0) PlayLevels.Pin(freePinned, false, FreePinOwner);
                    freePinned = sub;
                }
            }
            catch (NotImplementedException) { }
        }

        static void UnpinFree()
        {
            int p = freePinned;
            if (p < 0) return;
            try { PlayLevels.Pin(p, false, FreePinOwner); } catch (NotImplementedException) { }
            freePinned = -1;
        }

        static void Mark(bool pin, List<string> warnings)
        {
            pinWanted = pin;
            bool wasOverridden = overridden;
            overridden = follow || poi || posed || boxes.Count > 0;
            if (overridden || fx) World.Restore(Key, "camera override (target, boxes, poi, fx, free camera, pin)", Release);
            else World.Forget(Key);
            if (overridden && pin) PinBody(warnings);
            if (!overridden || !pin) Unpin(null);
            if (overridden && !pin && !wasOverridden) warnings.Add("not_pinned: the body's area can unload while the camera looks elsewhere");
        }

        // render shot_route apply: the free camera to an exact pose (main thread)
        internal static string ApplyPose(Vector3 p, Quaternion q, bool pin, List<string> warnings)
        {
            World.NotCapturing("the camera");
            lock (gate)
            {
                bool wasFree = CameraControl.free;
                float fov = -1f;
                var cam = G.Cam();
                if (cam != null) fov = cam.fieldOfView;
                if (!CameraControl.Set(p, q, fov)) throw new McpException(McpErr.NotReady, "no gameplay camera", "wait until the game is ready");
                if (!wasFree) freeOwned = true;
                posed = true;
                Mark(pin, warnings);
            }
            return "free camera at " + PlayShared.F(p.x) + ", " + PlayShared.F(p.y) + ", " + PlayShared.F(p.z);
        }

        // ---------------------------------------------------------------- tool
        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "state");
            bool pin = PlayShared.Bool(a, "pin", true);
            var warnings = new List<string>();
            var r = PlayShared.Env();
            switch (op)
            {
                case "state":
                    ctx.MainDo(() => r["camera"] = State());
                    break;
                case "boxes":
                    ctx.MainDo(() => Boxes(r, (float)PlayShared.Num(a, "radius", 30), PlayShared.Int(a, "limit", 30)));
                    break;
                case "release":
                    ctx.MainDo(() =>
                    {
                        World.NotCapturing("the camera");
                        warnings.Add(Release());
                        r["camera"] = State();
                    });
                    break;
                case "box_set": return BoxSet(a, ctx, pin);
                case "follow": return Follow(a, ctx, pin);
                case "poi":
                    ctx.MainDo(() =>
                    {
                        World.NotCapturing("the camera");
                        var cs = Script();
                        string spec = PlayShared.Str(a, "object", "");
                        lock (gate)
                        {
                            if (string.IsNullOrEmpty(spec))
                            {
                                cs.ClearPointOfInterest();
                                poi = false;
                                warnings.Add("poi cleared");
                            }
                            else
                            {
                                var go = World.Target(spec, "object");
                                cs.AddPointOfInterest(go.transform);
                                poi = true;
                                warnings.Add("poi " + World.Path(go));
                                if (go.GetComponent<CameraPointOfInterest>() == null) warnings.Add("no CameraPointOfInterest on the object: the camera's default poi tuning is used");
                            }
                            Mark(pin, warnings);
                        }
                        r["camera"] = State();
                    });
                    break;
                case "fx":
                    ctx.MainDo(() =>
                    {
                        World.NotCapturing("the camera");
                        var cs = Script();
                        if (!PlayShared.Has(a, "shake") && !PlayShared.Has(a, "handshake")) throw new McpException(McpErr.BadArgs, "op fx needs shake and / or handshake");
                        lock (gate)
                        {
                            if (!fx) { shakeBefore = cs.bCustomEffects; handshakeBefore = cs.bHandshakeEffect; fx = true; }
                            if (PlayShared.Has(a, "shake"))
                            {
                                cs.bCustomEffects = PlayShared.Bool(a, "shake", true);
                                if (!cs.bCustomEffects && cs.cameraShake != null) cs.cameraShake.CancelCustomEffects();
                            }
                            if (PlayShared.Has(a, "handshake")) cs.bHandshakeEffect = PlayShared.Bool(a, "handshake", true);
                            Mark(pin, warnings);
                        }
                        r["camera"] = State();
                    });
                    break;
                case "pose": return Pose(a, ctx, pin);
                default: return ToolResult.Fail(McpErr.BadArgs, "unknown op " + op);
            }
            r["outcome"] = "done";
            r["pinned_subscene"] = (double)pinned;
            if (warnings.Count > 0) r["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(r);
        }

        static CameraScript Script()
        {
            var cs = CameraScript.main;
            if (cs == null) throw new McpException(McpErr.NotReady, "no gameplay camera (CameraScript.main)", "wait until the game is ready");
            return cs;
        }

        // main thread
        static Json.Obj State()
        {
            var o = new Json.Obj();
            var cs = CameraScript.main;
            var cam = G.Cam();
            if (cam != null)
            {
                o["pos"] = PlayShared.V(cam.transform.position);
                o["up"] = PlayShared.V(cam.transform.up);
                o["fov"] = PlayShared.R(cam.fieldOfView);
            }
            if (cs != null)
            {
                o["aim"] = CameraControl.free && cam != null ? PlayShared.V(cam.transform.position + cam.transform.forward * 10f) : PlayShared.V(cs.positionAim);
                o["paused"] = cs.paused;
                var t = cs.cameraTarget;
                o["target"] = t != null ? PlayShared.PathOf(t) : "";
                var probe = cs.cameraBlendProbe;
                if (probe != null)
                {
                    var ws = probe.GetWeights();
                    var l = new List<object>();
                    ICameraWeight top = null;
                    foreach (var w in ws)
                    {
                        if (w == null) continue;
                        top = w;
                        if (l.Count >= 16) continue;
                        var e = new Json.Obj();
                        var m = w.modifier;
                        var owner = m != null ? m.owner : null;
                        e["box"] = owner != null ? World.Path(owner) : "default";
                        e["value"] = PlayShared.R(Mathf.Clamp01(w.value), 3);
                        l.Add(e);
                    }
                    o["weights"] = l;
                    if (top != null && top.modifier != null && top.modifier.owner != null)
                    {
                        var ab = new Json.Obj();
                        ab["id"] = World.Id(top.modifier.owner, "o");
                        ab["path"] = World.Path(top.modifier.owner);
                        ab["priority"] = (double)top.modifier.priority;
                        o["active_box"] = ab;
                    }
                }
            }
            o["overridden"] = overridden;
            o["free"] = CameraControl.free;
            return o;
        }

        static void Boxes(Json.Obj r, float radius, int limit)
        {
            Vector3 center;
            if (!World.BodyPos(out center))
            {
                var cam = G.Cam();
                if (cam == null) throw new McpException(McpErr.NotReady, "no body and no camera", "wait until the game is ready");
                center = cam.transform.position;
            }
            var all = UnityEngine.Object.FindObjectsOfType(typeof(CameraBlendBox)) as CameraBlendBox[];
            var rows = new List<KeyValuePair<float, CameraBlendBox>>();
            if (all != null)
                foreach (var b in all)
                {
                    if (b == null) continue;
                    var col = b.GetComponent<Collider>();
                    float d = col != null ? Vector3.Distance(col.ClosestPointOnBounds(center), center) : Vector3.Distance(b.transform.position, center);
                    if (d <= radius) rows.Add(new KeyValuePair<float, CameraBlendBox>(d, b));
                }
            rows.Sort((x, y) => x.Key.CompareTo(y.Key));
            var l = new List<object>();
            foreach (var kv in rows)
            {
                if (l.Count >= limit) break;
                var b = kv.Value;
                var o = new Json.Obj();
                o["id"] = World.Id(b.gameObject, "o");
                o["path"] = World.Path(b);
                o["active"] = b.isActive;
                o["priority"] = (double)b.priority;
                o["time_in"] = PlayShared.R(b.timeIn);
                o["time_out"] = PlayShared.R(b.timeOut);
                o["position_camera"] = PlayShared.V(b.positionCamera);
                o["position_aim"] = PlayShared.V(b.positionAim);
                o["dist"] = PlayShared.R(kv.Key);
                l.Add(o);
            }
            r["boxes"] = l;
            r["camera"] = State();
            if (rows.Count > l.Count) r["warnings"] = new List<object> { "showing " + l.Count + " of " + rows.Count + " boxes within " + PlayShared.F(radius) + " m" };
        }

        // ---------------------------------------------------------------- verified writes
        static ToolResult BoxSet(Json.Obj a, CallContext ctx, bool pin)
        {
            var warnings = new List<string>();
            string spec = PlayShared.Str(a, "box", null);
            bool hasActive = PlayShared.Has(a, "active");
            bool wantActive = PlayShared.Bool(a, "active", true);
            Vector3 pc, pa;
            bool hasPc = PlayShared.Vec3(a, "position_camera", out pc), hasPa = PlayShared.Vec3(a, "position_aim", out pa);
            if (!hasActive && !hasPc && !hasPa) return ToolResult.Fail(McpErr.BadArgs, "op box_set needs active, position_camera or position_aim");
            Func<CameraBlendBox, bool> holds = b => b != null && (!hasActive || b.isActive == wantActive) && (!hasPc || (b.positionCamera - pc).sqrMagnitude < 1e-4f) && (!hasPa || (b.positionAim - pa).sqrMagnitude < 1e-4f);
            CameraBlendBox box = null;
            bool after = false;
            var r = PlayShared.Env();
            ctx.MainDo(() =>
            {
                World.NotCapturing("the camera");
                var go = World.Target(spec, "box");
                box = go.GetComponent<CameraBlendBox>();
                if (box == null) throw new McpException(McpErr.NotFound, World.Path(go) + " is not a CameraBlendBox", "camera {op:\"boxes\"} lists the framing boxes");
                lock (gate)
                {
                    int id = box.GetInstanceID();
                    if (!boxes.ContainsKey(id)) boxes[id] = new BoxOrig { box = box, active = box.isActive, pc = box.positionCamera, pa = box.positionAim };
                    if (hasActive) { if (wantActive) box.ActivateBox(); else box.DeactivateBox(); }
                    if (hasPc) box.positionCamera = pc;
                    if (hasPa) box.positionAim = pa;
                    Mark(pin, warnings);
                }
                after = holds(box);
            });
            bool later = World.WaitVerify(ctx, 2, warnings);
            bool readK = false, sameK = false;
            ctx.MainDo(() =>
            {
                if (later && box != null) { readK = true; sameK = holds(box); }
                var o = new Json.Obj();
                if (box != null)
                {
                    o["id"] = World.Id(box.gameObject, "o");
                    o["path"] = World.Path(box);
                    o["active"] = box.isActive;
                    o["priority"] = (double)box.priority;
                    o["time_in"] = PlayShared.R(box.timeIn);
                    o["time_out"] = PlayShared.R(box.timeOut);
                    o["position_camera"] = PlayShared.V(box.positionCamera);
                    o["position_aim"] = PlayShared.V(box.positionAim);
                }
                r["boxes"] = new List<object> { o };
                r["camera"] = State();
            });
            if (later && !sameK) warnings.Add("the game changed the box back (an FSM or signal drives it)");
            r["outcome"] = "done";
            r["verdict"] = ReflectValues.Verdict(box != null, after, true, readK, sameK);
            r["pinned_subscene"] = (double)pinned;
            if (warnings.Count > 0) r["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(r);
        }

        static ToolResult Follow(Json.Obj a, CallContext ctx, bool pin)
        {
            var warnings = new List<string>();
            string spec = PlayShared.Str(a, "object", null);
            float blend = (float)PlayShared.Num(a, "blend_s", 1);
            Transform target = null;
            bool after = false;
            var r = PlayShared.Env();
            ctx.MainDo(() =>
            {
                World.NotCapturing("the camera");
                var cs = Script();
                string low = (spec ?? "").Trim().ToLowerInvariant();
                lock (gate)
                {
                    if (low.Length == 0)
                    {
                        target = follow && prevTarget != null ? prevTarget : MainTransform();
                        cs.SetTarget(target, blend);
                        follow = false;
                        prevTarget = null;
                        warnings.Add("follow back to " + (target != null ? PlayShared.PathOf(target) : "the default target"));
                        Mark(pin, warnings);
                        after = target == null || cs.cameraTarget == target;
                        return;
                    }
                    var go = World.Target(spec, "object");
                    target = go.transform;
                    if (!follow) { prevTarget = cs.cameraTarget; follow = true; }
                    cs.SetTarget(target, blend);
                    if (target.GetComponentInChildren<CameraBlendProbe>() == null && target.GetComponent<Huddle>() == null)
                        warnings.Add("no CameraBlendProbe under the target: the camera uses the default offset (0, 2, -12) and ignores framing boxes");
                    Mark(pin, warnings);
                    after = cs.cameraTarget == target;
                }
            });
            bool later = World.WaitVerify(ctx, 2, warnings);
            bool readK = false, sameK = false;
            ctx.MainDo(() =>
            {
                var cs = CameraScript.main;
                if (later && cs != null) { readK = true; sameK = cs.cameraTarget == target; }
                r["camera"] = State();
            });
            r["outcome"] = "done";
            r["verdict"] = ReflectValues.Verdict(target != null, after, true, readK, sameK);
            r["pinned_subscene"] = (double)pinned;
            if (warnings.Count > 0) r["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(r);
        }

        static ToolResult Pose(Json.Obj a, CallContext ctx, bool pin)
        {
            var p = PlayShared.Obj(a, "pose");
            if (p == null) return ToolResult.Fail(McpErr.BadArgs, "op pose needs pose {x, y, z, yaw?, pitch?, fov?, roll?, seconds?}");
            var warnings = new List<string>();
            var pos = new Vector3((float)p.Num("x"), (float)p.Num("y"), (float)p.Num("z"));
            float seconds = (float)PlayShared.Num(p, "seconds", 0);
            string msg = null;
            bool readAfter = false, sameAfter = false;
            var r = PlayShared.Env();
            ctx.MainDo(() =>
            {
                World.NotCapturing("the camera");
                lock (gate)
                {
                    bool wasFree = CameraControl.free;
                    var cam = G.Cam();
                    float yaw = 0f, pitch = 0f, fov = 0f, roll = 0f;
                    if (cam != null)
                    {
                        var e = cam.transform.eulerAngles;
                        yaw = e.y;
                        pitch = e.x > 180f ? e.x - 360f : e.x;
                        roll = e.z > 180f ? e.z - 360f : e.z;
                    }
                    if (PlayShared.Has(p, "yaw")) yaw = (float)p.Num("yaw");
                    if (PlayShared.Has(p, "pitch")) pitch = (float)p.Num("pitch");
                    if (PlayShared.Has(p, "roll")) roll = (float)p.Num("roll");
                    if (PlayShared.Has(p, "fov")) fov = (float)p.Num("fov");
                    msg = CameraControl.Pose(pos, yaw, pitch, fov, seconds, roll);
                    if (!CameraControl.free) throw new McpException(McpErr.NotReady, msg ?? "no gameplay camera", "wait until the game is ready");
                    if (!wasFree) freeOwned = true;
                    posed = true;
                    Mark(pin, warnings);
                    var cur = CameraControl.Current;
                    readAfter = cur != null;
                    sameAfter = cur != null && (cur.transform.position - pos).sqrMagnitude < 1e-3f;
                }
            });
            if (msg != null) warnings.Insert(0, msg);
            bool later = seconds <= 0f && World.WaitVerify(ctx, 2, warnings);
            bool readK = false, sameK = false;
            ctx.MainDo(() =>
            {
                var cam = CameraControl.Current;
                if (cam != null && later)
                {
                    readK = true;
                    sameK = (cam.transform.position - pos).sqrMagnitude < 1e-3f;
                }
                r["camera"] = State();
            });
            r["outcome"] = "done";
            if (seconds <= 0f) r["verdict"] = ReflectValues.Verdict(readAfter, sameAfter, true, readK, sameK);
            else warnings.Add("easing over " + PlayShared.F(seconds) + " s (unscaled time): no verdict; camera state shows the pose once the move ends");
            r["pinned_subscene"] = (double)pinned;
            if (warnings.Count > 0) r["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(r);
        }
    }
}
