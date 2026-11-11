using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Free camera + zoom for the gameplay camera.
    //   Free camera: sets CameraScript.paused (the game's own switch; its transform writer MoveCamera() stops), then
    //                drives the camera transform from input. Leaving restores paused=false, so the game camera
    //                blends back from wherever it was. The boy's input is gated while flying.
    //   Zoom:        multiplies the camera's field of view; the game's own FOV changes (FSM SetCameraFOV etc.) are
    //                picked up as the new base, so zoom never compounds. Reset restores the base FOV.
    //   Capture:     MultiCam drives the camera through Set (exact pose written immediately, roll and near included),
    //                GameStep (CameraScript runs one step frame, nothing is written) and captureLock (input ignored).
    //   Commands:    Command handles cam status [json] | cam look <target> ... | cam back | cam pose (numbers as a reply
    //                prints them); every number of a reply has a decimal point on every Windows language.
    //   editor.cfg:  cam.hotkeys always | panel | off (Insert, + - 0), cam.speed (remembered fly speed, m/s),
    //                cam.lookSensitivity (degrees per pixel), cam.invertY. Read once per second.
    public static class CameraControl
    {
        public static bool free;
        public static float zoom = 1f;           // 1 = game default, <1 = zoom in, >1 = zoom out
        public static float speed = 6f;          // m/s
        public static bool captureLock;          // MultiCam capture: keyboard / mouse / hotkeys ignored
        public static bool quiet;                // MultiCam capture: no ON/OFF log lines
        static Vector3 camPos; static float yaw, pitch, roll;
        static Quaternion camRot = Quaternion.identity; static bool exactRot, stepMode;
        static CameraScript script; static bool pausedBefore;
        static float baseFov = -1f, lastApplied = -1f;
        static float shotNear = -1f, nearBefore = -1f;
        static Camera cam;
        static Vector2 lastMouse; static bool lookActive;
        // scripted shots (bridge "cam pose"): the free camera eases from where it is to a pose over a time
        static Vector3 fromPos, toPos; static float fromYaw, toYaw, fromPitch, toPitch, fromRoll, toRoll, fromFov, toFov, moveT0, moveDur;
        static bool moving; static float shotFov = -1f;
        // where the camera was before the last cam pose / cam look ("cam back")
        static bool backSet, backFree; static Vector3 backPos; static float backYaw, backPitch, backRoll, backFov;
        // editor.cfg
        const string KeyHotkeys = "cam.hotkeys", KeySpeed = "cam.speed", KeySens = "cam.lookSensitivity", KeyInvert = "cam.invertY";
        const float DefaultSpeed = 6f, DefaultSens = 0.25f, MinFov = 5f, MaxFov = 140f, LookSize = 0.4f;
        const int KeysAlways = 0, KeysPanel = 1, KeysOff = 2;
        static int hotkeys = KeysAlways;
        static float lookSens = DefaultSens, cfgSpeed = DefaultSpeed, cfgNext;
        static bool invertY, cfgReady, zoomSaid;

        static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
        static bool Finite(Vector3 v) { return Finite(v.x) && Finite(v.y) && Finite(v.z); }

        public static string Pose(Vector3 p, float yawDeg, float pitchDeg, float fov, float seconds, float rollDeg = 0f)
        {
            if (!Finite(p) || !Finite(yawDeg) || !Finite(pitchDeg) || !Finite(rollDeg) || !Finite(fov) || !Finite(seconds)) return "refused: cam pose needs finite numbers";
            KeepBack();
            if (!free) SetFree(true);
            if (!free) return "no gameplay camera";
            stepMode = false;
            fromPos = camPos; fromYaw = yaw; fromPitch = pitch; fromRoll = roll; fromFov = shotFov > 0 ? shotFov : cam.fieldOfView;
            toPos = p; toYaw = yawDeg; toPitch = pitchDeg; toRoll = rollDeg; toFov = fov > 0 ? Mathf.Clamp(fov, MinFov, MaxFov) : fromFov;
            exactRot = false;
            moveT0 = Time.unscaledTime; moveDur = Mathf.Max(0f, seconds); moving = true;
            if (moveDur <= 0f) Step(1f);
            return "cam -> " + Fmt.V3(p, "F2") + " yaw " + Fmt.F(yawDeg, "0.#") + " pitch " + Fmt.F(pitchDeg, "0.#") + (rollDeg != 0f ? " roll " + Fmt.F(rollDeg, "0.#") : "") + " fov " + Fmt.F(toFov, "0.#") +
                   (seconds > 0 ? " over " + Fmt.F(seconds, "0.#") + " s" : "");
        }

        // exact pose for scripted capture (no easing)
        public static bool Set(Vector3 p, Quaternion r, float fov) { return Set(p, r, fov, -1f); }

        public static bool Set(Vector3 p, Quaternion r, float fov, float near)
        {
            if (free && cam == null) Retake();
            if (!free) SetFree(true);
            if (!free) return false;
            if (!Usable(p, r)) return false;
            moving = false;
            stepMode = false;
            if (script != null && !script.paused) script.paused = true;
            Store(p, r, fov, near);
            Write();
            return true;
        }

        // capture: the game pose is put back on the camera before a step frame without leaving step mode
        public static void WriteGamePose(Vector3 p, Quaternion r, float fov, float near)
        {
            if (!free) return;
            if (cam == null) { Retake(); if (!free) return; }
            if (!Usable(p, r)) return;
            Store(p, r, fov, near);
            Write();
        }

        // capture: run (true) or freeze (false) the game camera for a step frame; Apply writes nothing meanwhile
        public static void GameStep(bool run)
        {
            if (!free) return;
            stepMode = true;
            moving = false;
            if (script != null) script.paused = !run;
        }

        public static bool BeginCapture()
        {
            if (!free) SetFree(true);
            if (!free) return false;
            captureLock = true;
            moving = false;
            return true;
        }

        public static void EndCapture()
        {
            captureLock = false;
            stepMode = false;
            SetFree(false);
        }

        public static Camera Current { get { return free ? cam : RenderHost.WorldCam(); } }

        // A pose that can be written to a transform: finite position, a rotation that is one.
        static bool Usable(Vector3 p, Quaternion r)
        {
            if (!Finite(p) || !Finite(r.x) || !Finite(r.y) || !Finite(r.z) || !Finite(r.w)) return false;
            return r.x * r.x + r.y * r.y + r.z * r.z + r.w * r.w >= 1e-6f;
        }

        // The camera object of the free camera was destroyed (a load while a capture holds the lock, so Update never saw
        // it): let go of it and take the gameplay camera that exists now. free stays false when there is none.
        static void Retake()
        {
            bool q = quiet;
            quiet = true;
            try { SetFree(false); SetFree(true); }
            finally { quiet = q; }
            if (!q) DevLog.Write(free ? "[cam] the gameplay camera was replaced: free camera moved to the new one" : "[cam] the gameplay camera is gone: free camera off");
        }

        static void Store(Vector3 p, Quaternion r, float fov, float near)
        {
            camPos = p; camRot = r; exactRot = true;
            var e = r.eulerAngles;
            yaw = e.y; pitch = e.x > 180 ? e.x - 360 : e.x; roll = e.z > 180 ? e.z - 360 : e.z;
            shotFov = fov > 0 ? Mathf.Clamp(fov, 1f, 179f) : -1f;
            if (near > 0f) shotNear = near;
        }

        static void Write()
        {
            if (cam == null) return;
            var t = cam.transform;
            t.position = camPos;
            t.rotation = exactRot ? camRot : Quaternion.Euler(pitch, yaw, roll);
            if (shotFov > 0) { cam.fieldOfView = shotFov; lastApplied = shotFov; }
            if (shotNear > 0 && !Mathf.Approximately(cam.nearClipPlane, shotNear)) cam.nearClipPlane = shotNear;
        }

        static void Step(float t)
        {
            float s = t * t * (3f - 2f * t);
            camPos = Vector3.Lerp(fromPos, toPos, s);
            yaw = Mathf.LerpAngle(fromYaw, toYaw, s); pitch = Mathf.Lerp(fromPitch, toPitch, s); roll = Mathf.LerpAngle(fromRoll, toRoll, s);
            shotFov = Mathf.Lerp(fromFov, toFov, s);
            if (t >= 1f) moving = false;
        }

        public static string Describe()
        {
            var c = free ? cam : RenderHost.WorldCam();
            if (c == null) return "no gameplay camera";
            var e = c.transform.eulerAngles;
            return (free ? "free" : "game") + " cam at " + Fmt.V3(c.transform.position, "F2") + " yaw " + Fmt.F(e.y, "0.#") + " pitch " + Fmt.F(Signed(e.x), "0.#") +
                   (Mathf.Abs(Signed(e.z)) > 0.05f ? " roll " + Fmt.F(Signed(e.z), "0.#") : "") + " fov " + Fmt.F(c.fieldOfView, "0.#") + " near " + Fmt.F(c.nearClipPlane, "0.##") +
                   (captureLock ? "  [capture lock]" : "");
        }

        static float Signed(float a) { return a > 180f ? a - 360f : a; }

        public static string SetFov(float fov)
        {
            if (!Finite(fov)) return "refused: cam fov needs a number";
            if (!free) SetFree(true);
            if (!free) return "no gameplay camera";
            moving = false;
            shotFov = Mathf.Clamp(fov, MinFov, MaxFov);
            return Describe();
        }

        public static string SetRoll(float deg)
        {
            if (!Finite(deg)) return "refused: cam roll needs a number";
            if (!free) SetFree(true);
            if (!free) return "no gameplay camera";
            moving = false;
            if (exactRot) { var e = camRot.eulerAngles; yaw = e.y; pitch = Signed(e.x); }
            exactRot = false;
            roll = deg;
            return Describe();
        }

        // current pose as a MultiCam spec camera object
        public static string Json(string name)
        {
            var c = free ? cam : RenderHost.WorldCam();
            if (c == null) return "no gameplay camera";
            var p = c.transform.position; var e = c.transform.eulerAngles;
            return "{\"name\": \"" + (string.IsNullOrEmpty(name) ? "cam1" : name) + "\", \"mode\": \"static\", \"pos\": [" + N(p.x) + ", " + N(p.y) + ", " + N(p.z) + "], \"yaw\": " + N(Signed(e.y)) +
                   ", \"pitch\": " + N(Signed(e.x)) + ", \"roll\": " + N(Signed(e.z)) + ", \"fov\": " + N(c.fieldOfView) + "}";
        }

        static string N(float v) { return Fmt.F(v); }

        // ---------------------------------------------------------------- commands of this file

        // cam status [json] | cam look <target> ... | cam back | cam pose x y z yaw pitch [fov] [seconds] [roll].
        // Returns null for every other word (the caller keeps its own handling).
        public static string Command(List<string> a)
        {
            if (a == null || a.Count < 2) return null;
            switch (a[1].ToLowerInvariant())
            {
                case "status": return a.Count > 2 && a[a.Count - 1].ToLowerInvariant() == "json" ? StatusJson() : Describe();
                case "back": return Back();
                case "look": return Look(a);
                case "pose": return PoseCommand(a);
            }
            return null;
        }

        // One line of JSON: the pose with full precision (it goes back into "cam pose" unchanged) and the camera state.
        public static string StatusJson()
        {
            var c = free ? cam : RenderHost.WorldCam();
            if (c == null) return JsonLine.Obj("ok", false, "error", "no gameplay camera", "free", free, "captureLock", captureLock);
            var p = c.transform.position; var e = c.transform.eulerAngles;
            return JsonLine.Obj("ok", true, "free", free, "pos", new List<object> { p.x, p.y, p.z }, "yaw", Signed(e.y), "pitch", Signed(e.x), "roll", Signed(e.z),
                                "fov", c.fieldOfView, "near", c.nearClipPlane, "zoom", 1f / zoom, "gameFov", baseFov > 0 ? (object)baseFov : null, "speed", speed,
                                "moving", moving, "captureLock", captureLock, "hotkeys", HotkeyName());
        }

        const string PoseUsage = "usage: cam pose x y z yaw pitch [fov] [seconds] [roll]";

        // The numbers may come as a reply printed them: "(1.23, 4.56, 7.89)" is three numbers.
        static string PoseCommand(List<string> a)
        {
            var n = new List<float>();
            for (int i = 2; i < a.Count; i++)
                foreach (var part in a[i].Split(new[] { '(', ')', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    float v;
                    if (!Fmt.TryF(part.Trim(), out v)) return PoseUsage;
                    n.Add(v);
                }
            if (n.Count < 5 || n.Count > 8) return PoseUsage;
            return Pose(new Vector3(n[0], n[1], n[2]), n[3], n[4], n.Count > 5 ? n[5] : -1f, n.Count > 6 ? n[6] : 0f, n.Count > 7 ? n[7] : 0f);
        }

        const string LookUsage = "usage: cam look <target> [dist] [yaw] [pitch] [fov] [seconds] [point=<name>] [size=<part of the frame height>]";

        // Places the free camera in front of a target (the strict target syntax of mcam specs: boy, huddle, an object path
        // ...): dist metres away along -forward of yaw / pitch, or as far as makes the subject `size` of the frame height.
        static string Look(List<string> a)
        {
            if (a.Count < 3) return LookUsage;
            string point = "";
            float dist = -1f, yawDeg = 0f, pitchDeg = 8f, fov = -1f, seconds = 0f, size = LookSize;
            int positional = 0;
            for (int i = 3; i < a.Count; i++)
            {
                string s = a[i], k = "";
                int eq = s.IndexOf('=');
                if (eq > 0) { k = s.Substring(0, eq).ToLowerInvariant(); s = s.Substring(eq + 1); }
                if (k == "point") { point = s; continue; }
                float v;
                if (!Fmt.TryF(s, out v) || !Finite(v)) return LookUsage;
                if (k.Length == 0) k = positional == 0 ? "dist" : positional == 1 ? "yaw" : positional == 2 ? "pitch" : positional == 3 ? "fov" : positional == 4 ? "seconds" : "?";
                if (eq <= 0) positional++;
                switch (k)
                {
                    case "dist": dist = v; break;
                    case "yaw": yawDeg = v; break;
                    case "pitch": pitchDeg = v; break;
                    case "fov": fov = v; break;
                    case "seconds": seconds = v; break;
                    case "size": size = v; break;
                    default: return LookUsage;
                }
            }
            var c = free ? cam : RenderHost.WorldCam();
            if (c == null) return "no gameplay camera";
            TargetSample ts;
            string resolved, error;
            if (!MultiCamTargets.SampleOnce(a[2], point, out ts, out resolved, out error)) return "not found: " + error;
            if (fov <= 0f) fov = free && shotFov > 0 ? shotFov : c.fieldOfView;
            fov = Mathf.Clamp(fov, MinFov, MaxFov);
            if (dist <= 0f)
            {
                float h = ts.height > 0.05f ? ts.height : MultiCamConst.BoyHeight;
                dist = h / (2f * Mathf.Clamp(size, 0.02f, 1f) * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad));
            }
            dist = Mathf.Clamp(dist, 0.3f, 2000f);
            var p = ts.pos - Quaternion.Euler(pitchDeg, yawDeg, 0f) * Vector3.forward * dist;
            string r = Pose(p, yawDeg, pitchDeg, fov, seconds);
            return r.StartsWith("cam -> ", StringComparison.Ordinal) ? r + "  looking at " + resolved + " from " + Fmt.F(dist, "0.##") + " m" : r;
        }

        static void KeepBack()
        {
            backSet = true;
            backFree = free && cam != null;
            if (!backFree) return;
            backPos = camPos;
            if (exactRot) { var e = camRot.eulerAngles; backYaw = e.y; backPitch = Signed(e.x); backRoll = Signed(e.z); }
            else { backYaw = yaw; backPitch = pitch; backRoll = roll; }
            backFov = shotFov > 0 ? shotFov : cam.fieldOfView;
        }

        // Back to where the camera was before the last cam pose / cam look (the game camera, or the earlier free pose).
        // A second "cam back" returns to the pose it left.
        public static string Back()
        {
            if (!backSet) return "cannot: there is no earlier pose (cam back undoes the last cam pose or cam look)";
            if (!backFree)
            {
                KeepBack();
                SetFree(false);
                return Describe();
            }
            return Pose(backPos, backYaw, backPitch, backFov, 0f, backRoll);
        }

        // ---------------------------------------------------------------- free camera

        public static void ToggleFree() { SetFree(!free); }

        public static void SetFree(bool on)
        {
            var c = RenderHost.WorldCam();
            if (on == free) return;
            if (on)
            {
                if (c == null) { DevLog.Write("[cam] no gameplay camera"); return; }
                cam = c;
                script = c.GetComponent<CameraScript>();
                pausedBefore = script != null && script.paused;
                if (script != null) script.paused = true;
                camPos = c.transform.position;
                var e = c.transform.eulerAngles;
                yaw = e.y; pitch = e.x > 180 ? e.x - 360 : e.x; roll = e.z > 180 ? e.z - 360 : e.z;
                camRot = c.transform.rotation; exactRot = false; stepMode = false;
                nearBefore = c.nearClipPlane; shotNear = -1f;
                free = true;
                if (!quiet) DevLog.Write("[cam] free camera ON at " + Fmt.V3(camPos, "F2"));
            }
            else
            {
                free = false; moving = false; shotFov = -1f; stepMode = false; exactRot = false;
                MultiCamHide.EndPreview();
                if (cam != null && shotNear > 0 && nearBefore > 0) { try { cam.nearClipPlane = nearBefore; } catch { } }
                shotNear = -1f;
                if (script != null) script.paused = pausedBefore;
                if (!quiet) DevLog.Write("[cam] free camera OFF (game camera resumed)");
            }
        }

        public static void ZoomBy(float factor) { zoom = Mathf.Clamp(zoom * factor, 0.15f, 3f); }
        public static void ResetZoom() { zoom = 1f; }

        public static void ResetAll()
        {
            SetFree(false);
            ResetZoom();
        }

        // ---------------------------------------------------------------- editor.cfg

        static string HotkeyName() { return hotkeys == KeysPanel ? "panel" : hotkeys == KeysOff ? "off" : "always"; }

        static void ReadConfig()
        {
            if (!cfgReady)
            {
                cfgReady = true;
                EditorState.Describe("cam", KeyHotkeys, "always", "when Insert (free camera) and + - 0 (zoom) work: always, panel (only while the editor panel is open), off");
                EditorState.Describe("cam", KeySpeed, Fmt.F(DefaultSpeed), "fly speed of the free camera in m/s (the mouse wheel changes and stores it)");
                EditorState.Describe("cam", KeySens, Fmt.F(DefaultSens), "mouse look of the free camera in degrees per pixel");
                EditorState.Describe("cam", KeyInvert, "0", "1: mouse up looks down in the free camera");
                MultiCamRunner.ReplayDescribeKeys();
                Doctor.Register("cam", Check);
            }
            string h = EditorState.Get(KeyHotkeys, "always");
            hotkeys = h == "panel" ? KeysPanel : h == "off" || h == "0" ? KeysOff : KeysAlways;
            float s = EditorState.Get(KeySpeed, DefaultSpeed);
            if (s != cfgSpeed && Finite(s)) { cfgSpeed = s; speed = Mathf.Clamp(s, 0.5f, 100f); }
            float k = EditorState.Get(KeySens, DefaultSens);
            lookSens = Finite(k) ? Mathf.Clamp(k, 0.01f, 5f) : DefaultSens;
            invertY = EditorState.Get(KeyInvert, false);
        }

        static bool HotkeysLive()
        {
            if (hotkeys == KeysAlways) return true;
            if (hotkeys == KeysOff) return false;
            var core = DevCore.Instance;
            return (object)core != null && core.panel;
        }

        static List<Doctor.Row> Check()
        {
            var rows = new List<Doctor.Row>();
            var c = RenderHost.WorldCam();
            if (c == null) rows.Add(new Doctor.Row("cam", "gameplay camera", Doctor.Info, "none right now (title menu or a load)"));
            else rows.Add(new Doctor.Row("cam", "gameplay camera", Doctor.Ok, c.name + ", fov " + Fmt.F(c.fieldOfView, "0.#") + ", hotkeys " + HotkeyName()));
            if (free) rows.Add(new Doctor.Row("cam", "free camera", Doctor.Info, captureLock ? "on, held by a capture" : "on: the game camera is paused", captureLock ? null : "cam off"));
            bool zoomed = Mathf.Abs(zoom - 1f) > 0.001f;
            rows.Add(new Doctor.Row("cam", "zoom", zoomed ? Doctor.Warn : Doctor.Ok, zoomed ? Fmt.F(1f / zoom, "0.00") + "x: the game's field of view is changed" : "1x", zoomed ? "zoom reset" : null));
            return rows;
        }

        // Update: input (keys only when the editor keyboard isn't in use by a text field)
        public static void Update(bool uiHot)
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now >= cfgNext)
                {
                    cfgNext = now + 1f;
                    try { ReadConfig(); }
                    catch (Exception e) { cfgNext = now + 30f; DevLog.Error("camera config", e); }
                }
                if (captureLock) return;
                if (HotkeysLive())
                {
                    if (EditorInput.Key(KeyCode.Insert)) ToggleFree();
                    if (EditorInput.Key(KeyCode.KeypadPlus) || EditorInput.Key(KeyCode.Equals)) ZoomBy(1f / 1.15f);
                    if (EditorInput.Key(KeyCode.KeypadMinus) || EditorInput.Key(KeyCode.Minus)) ZoomBy(1.15f);
                    if (EditorInput.Key(KeyCode.Alpha0) || EditorInput.Key(KeyCode.Keypad0)) ResetZoom();
                }
                if (!free) return;
                if (cam == null || !cam.enabled) { SetFree(false); return; }
                if (moving) { Step(moveDur <= 0f ? 1f : Mathf.Clamp01((Time.unscaledTime - moveT0) / moveDur)); return; }
                if (InputCapture.keyboard) return;   // typing in a text field
                float dt = Time.unscaledDeltaTime;
                float k = speed * (EditorInput.shift ? 4f : 1f) * (EditorInput.ctrl ? 0.25f : 1f);
                // mouse look while the right button is held over the game view
                // (mouse delta from positions: INSIDE's input manager may not define the "Mouse X/Y" axes)
                var m = EditorInput.mouse;
                if (EditorInput.held[1] && !uiHot && lookActive)
                {
                    exactRot = false;
                    yaw += (m.x - lastMouse.x) * lookSens;
                    pitch = Mathf.Clamp(pitch + (m.y - lastMouse.y) * (invertY ? -lookSens : lookSens), -89f, 89f);
                }
                lookActive = EditorInput.held[1] && !uiHot;
                lastMouse = m;
                if (EditorInput.wheel != 0 && !uiHot)
                {
                    speed = Mathf.Clamp(speed * (EditorInput.wheel > 0 ? 1.25f : 0.8f), 0.5f, 100f);
                    cfgSpeed = speed;
                    EditorState.Set(KeySpeed, speed);
                }
                var rot = Quaternion.Euler(pitch, yaw, 0);
                Vector3 mv = Vector3.zero;
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) mv += Vector3.forward;
                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) mv += Vector3.back;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) mv += Vector3.left;
                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) mv += Vector3.right;
                camPos += rot * mv * k * dt;
                if (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.PageUp)) camPos += Vector3.up * k * dt;
                if (Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.PageDown)) camPos += Vector3.down * k * dt;
            }
            catch (Exception e) { DevLog.Error("camera control", e); if (!captureLock) SetFree(false); }
        }

        // LateUpdate (after game scripts): apply transform + FOV
        public static void Apply()
        {
            try
            {
                bool zoomed = Mathf.Abs(zoom - 1f) > 0.001f;
                if (zoomed != zoomSaid)
                {
                    // a changed zoom is never silent: one line when it leaves 1x and one when it is back
                    zoomSaid = zoomed;
                    if (!quiet && !captureLock) DevLog.Write(zoomed ? "[cam] zoom " + Fmt.F(1f / zoom, "0.00") + "x (key 0 or 'zoom reset' puts the game's field of view back)" : "[cam] zoom back to 1x");
                }
                var c = free ? cam : RenderHost.WorldCam();
                if (c == null) return;
                if (free && stepMode) return;
                if (free)
                {
                    c.transform.position = camPos;
                    c.transform.rotation = exactRot ? camRot : Quaternion.Euler(pitch, yaw, roll);
                    if (shotNear > 0 && !Mathf.Approximately(c.nearClipPlane, shotNear)) c.nearClipPlane = shotNear;
                }
                if (free && shotFov > 0) { c.fieldOfView = shotFov; lastApplied = shotFov; return; }
                float f = c.fieldOfView;
                if (baseFov < 0 || !Mathf.Approximately(f, lastApplied)) baseFov = f;   // game changed FOV (or first frame)
                float want = Mathf.Clamp(baseFov * zoom, 5f, 140f);
                if (!Mathf.Approximately(f, want)) c.fieldOfView = want;
                lastApplied = c.fieldOfView;
            }
            catch (Exception e) { DevLog.Error("camera apply", e); }
        }

        public static string Status()
        {
            return (free ? "FREE CAM (Insert to exit)  speed " + Fmt.F(speed, "0.#") + " m/s" : "game camera") + "   zoom " + Fmt.F(1f / zoom, "0.00") + "x" +
                   (baseFov > 0 ? "   fov " + Fmt.F(lastApplied, "0.#") + " (game " + Fmt.F(baseFov, "0.#") + ")" : "") + (captureLock ? "   [mcam capture lock]" : "");
        }
    }
}
