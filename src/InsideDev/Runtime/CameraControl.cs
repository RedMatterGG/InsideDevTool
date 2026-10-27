using System;
using System.Globalization;
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
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static string Pose(Vector3 p, float yawDeg, float pitchDeg, float fov, float seconds, float rollDeg = 0f)
        {
            if (!free) SetFree(true);
            if (!free) return "no gameplay camera";
            stepMode = false;
            fromPos = camPos; fromYaw = yaw; fromPitch = pitch; fromRoll = roll; fromFov = shotFov > 0 ? shotFov : cam.fieldOfView;
            toPos = p; toYaw = yawDeg; toPitch = pitchDeg; toRoll = rollDeg; toFov = fov > 0 ? fov : fromFov;
            exactRot = false;
            moveT0 = Time.unscaledTime; moveDur = Mathf.Max(0f, seconds); moving = true;
            if (moveDur <= 0f) Step(1f);
            return "cam -> " + p.ToString("F2") + " yaw " + yawDeg.ToString("0.#") + " pitch " + pitchDeg.ToString("0.#") + (rollDeg != 0f ? " roll " + rollDeg.ToString("0.#") : "") + " fov " + toFov.ToString("0.#") + (seconds > 0 ? " over " + seconds.ToString("0.#") + " s" : "");
        }

        // exact pose for scripted capture (no easing)
        public static bool Set(Vector3 p, Quaternion r, float fov) { return Set(p, r, fov, -1f); }

        public static bool Set(Vector3 p, Quaternion r, float fov, float near)
        {
            if (!free) SetFree(true);
            if (!free) return false;
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
            if (!free || cam == null) return;
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

        static void Store(Vector3 p, Quaternion r, float fov, float near)
        {
            camPos = p; camRot = r; exactRot = true;
            var e = r.eulerAngles;
            yaw = e.y; pitch = e.x > 180 ? e.x - 360 : e.x; roll = e.z > 180 ? e.z - 360 : e.z;
            shotFov = fov;
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
            return (free ? "free" : "game") + " cam at " + c.transform.position.ToString("F2") + " yaw " + e.y.ToString("0.#") + " pitch " + Signed(e.x).ToString("0.#") +
                   (Mathf.Abs(Signed(e.z)) > 0.05f ? " roll " + Signed(e.z).ToString("0.#") : "") + " fov " + c.fieldOfView.ToString("0.#") + " near " + c.nearClipPlane.ToString("0.##") +
                   (captureLock ? "  [capture lock]" : "");
        }

        static float Signed(float a) { return a > 180f ? a - 360f : a; }

        public static string SetFov(float fov)
        {
            if (!free) SetFree(true);
            if (!free) return "no gameplay camera";
            moving = false;
            shotFov = Mathf.Clamp(fov, 5f, 140f);
            return Describe();
        }

        public static string SetRoll(float deg)
        {
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

        static string N(float v) { return v.ToString("0.###", IC); }

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
                if (!quiet) DevLog.Write("[cam] free camera ON at " + camPos.ToString("F2"));
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

        // Update: input (keys only when the editor keyboard isn't in use by a text field)
        public static void Update(bool uiHot)
        {
            try
            {
                if (captureLock) return;
                if (EditorInput.Key(KeyCode.Insert)) ToggleFree();
                if (EditorInput.Key(KeyCode.KeypadPlus) || EditorInput.Key(KeyCode.Equals)) ZoomBy(1f / 1.15f);
                if (EditorInput.Key(KeyCode.KeypadMinus) || EditorInput.Key(KeyCode.Minus)) ZoomBy(1.15f);
                if (EditorInput.Key(KeyCode.Alpha0) || EditorInput.Key(KeyCode.Keypad0)) ResetZoom();
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
                    yaw += (m.x - lastMouse.x) * 0.25f;
                    pitch = Mathf.Clamp(pitch + (m.y - lastMouse.y) * 0.25f, -89f, 89f);
                }
                lookActive = EditorInput.held[1] && !uiHot;
                lastMouse = m;
                if (EditorInput.wheel != 0 && !uiHot) speed = Mathf.Clamp(speed * (EditorInput.wheel > 0 ? 1.25f : 0.8f), 0.5f, 100f);
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
            return (free ? "FREE CAM (Insert to exit)  speed " + speed.ToString("0.#") + " m/s" : "game camera") + "   zoom " + (1f / zoom).ToString("0.00") + "x" +
                   (baseFov > 0 ? "   fov " + lastApplied.ToString("0.#") + " (game " + baseFov.ToString("0.#") + ")" : "") + (captureLock ? "   [mcam capture lock]" : "");
        }
    }
}
