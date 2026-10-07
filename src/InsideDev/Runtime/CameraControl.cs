using System;
using UnityEngine;

namespace InsideDev
{
    // Free camera + zoom for the gameplay camera.
    //   Free camera: sets CameraScript.paused (the game's own switch; its transform writer MoveCamera() stops), then
    //                drives the camera transform from input. Leaving restores paused=false, so the game camera
    //                blends back from wherever it was. The boy's input is gated while flying.
    //   Zoom:        multiplies the camera's field of view; the game's own FOV changes (FSM SetCameraFOV etc.) are
    //                picked up as the new base, so zoom never compounds. Reset restores the base FOV.
    public static class CameraControl
    {
        public static bool free;
        public static float zoom = 1f;           // 1 = game default, <1 = zoom in, >1 = zoom out
        public static float speed = 6f;          // m/s
        static Vector3 camPos; static float yaw, pitch;
        static CameraScript script; static bool pausedBefore;
        static float baseFov = -1f, lastApplied = -1f;
        static Camera cam;
        static Vector2 lastMouse; static bool lookActive;

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
                yaw = e.y; pitch = e.x > 180 ? e.x - 360 : e.x;
                free = true;
                DevLog.Write("[cam] free camera ON at " + camPos.ToString("F2"));
            }
            else
            {
                free = false;
                if (script != null) script.paused = pausedBefore;
                DevLog.Write("[cam] free camera OFF (game camera resumed)");
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
                if (EditorInput.Key(KeyCode.Insert)) ToggleFree();
                if (EditorInput.Key(KeyCode.KeypadPlus) || EditorInput.Key(KeyCode.Equals)) ZoomBy(1f / 1.15f);
                if (EditorInput.Key(KeyCode.KeypadMinus) || EditorInput.Key(KeyCode.Minus)) ZoomBy(1.15f);
                if (EditorInput.Key(KeyCode.Alpha0) || EditorInput.Key(KeyCode.Keypad0)) ResetZoom();
                if (!free) return;
                if (cam == null || !cam.enabled) { SetFree(false); return; }
                if (InputCapture.keyboard) return;   // typing in a text field
                float dt = Time.unscaledDeltaTime;
                float k = speed * (EditorInput.shift ? 4f : 1f) * (EditorInput.ctrl ? 0.25f : 1f);
                // mouse look while the right button is held over the game view
                // (mouse delta from positions: INSIDE's input manager may not define the "Mouse X/Y" axes)
                var m = EditorInput.mouse;
                if (EditorInput.held[1] && !uiHot && lookActive)
                {
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
            catch (Exception e) { DevLog.Error("camera control", e); SetFree(false); }
        }

        // LateUpdate (after game scripts): apply transform + FOV
        public static void Apply()
        {
            try
            {
                var c = free ? cam : RenderHost.WorldCam();
                if (c == null) return;
                if (free)
                {
                    c.transform.position = camPos;
                    c.transform.rotation = Quaternion.Euler(pitch, yaw, 0);
                }
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
                   (baseFov > 0 ? "   fov " + lastApplied.ToString("0.#") + " (game " + baseFov.ToString("0.#") + ")" : "");
        }
    }
}
