using System;
using UnityEngine;

namespace InsideDev
{
    // Per-frame snapshot of Unity input for the editor. Widgets and hotkeys read this, never Input directly,
    // so there is one place that decides what the editor consumes.
    public static class EditorInput
    {
        public static Vector2 mouse;                    // physical px, top-left origin
        public static readonly bool[] down = new bool[3], held = new bool[3], up = new bool[3];
        public static float wheel;
        public static string chars = "";
        public static bool anyEvent;
        public static bool ctrl, shift, alt;
        static Vector3 lastMouse;

        public static void Poll()
        {
            var mp = Input.mousePosition;
            mouse = new Vector2(mp.x, Screen.height - mp.y);
            for (int i = 0; i < 3; i++) { down[i] = Input.GetMouseButtonDown(i); held[i] = Input.GetMouseButton(i); up[i] = Input.GetMouseButtonUp(i); }
            wheel = Input.mouseScrollDelta.y;
            chars = Input.inputString ?? "";
            ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            anyEvent = Input.anyKey || mp != lastMouse || wheel != 0;
            lastMouse = mp;
            // synthetic input from the bridge (automated UI tests): physical px, top-left origin
            if (injectFrames > 0)
            {
                mouse = injectPos;
                for (int i = 0; i < 3; i++) { down[i] = held[i] = up[i] = false; }
                if (injectButton >= 0)
                {
                    if (injectFrames == 3) { down[injectButton] = true; held[injectButton] = true; }
                    else if (injectFrames == 2) { up[injectButton] = true; }
                }
                injectFrames--;
                anyEvent = true;
            }
            RenderHost.MarkInput(anyEvent);
        }

        static Vector2 injectPos; static int injectFrames, injectButton = -1;
        // button -1 = hover only. Sequence over 3 frames: down, up, settle (mouse held at the position).
        public static void Inject(Vector2 physicalPos, int button) { injectPos = physicalPos; injectButton = button; injectFrames = 3; }

        // editor hotkeys are ignored while a text field owns the keyboard
        public static bool Key(KeyCode k) { return !InputCapture.keyboard && Input.GetKeyDown(k); }
    }

    // Keeps editor typing away from the boy: while a text field is focused the game's own input gate
    // (GameInput.Disable/Enable, the same switch GameManager uses during loads) is closed.
    // Release is delayed one frame so the Escape/Enter that ends typing can't reach the game menu,
    // and never re-opens the gate while the game itself is loading.
    public static class InputCapture
    {
        public static bool keyboard;        // editor owns the keyboard this frame
        static bool weDisabled;
        static int releaseFrames;

        // wantKeyboard: a text field owns the keyboard (editor hotkeys off too).
        // gateGame: the game must not receive input (free camera), editor hotkeys stay on.
        public static void Update(bool wantKeyboard, bool gateGame = false)
        {
            try
            {
                if (wantKeyboard || gateGame)
                {
                    keyboard = wantKeyboard; releaseFrames = 2;
                    if (GameInput.IsEnabled()) { GameInput.Disable(); weDisabled = true; }
                    return;
                }
                if (releaseFrames > 0) { releaseFrames--; return; }
                keyboard = false;
                if (weDisabled)
                {
                    weDisabled = false;
                    bool loading = false;
                    try { loading = GameManager.IsLoading(); } catch { }
                    if (!loading && !GameInput.IsEnabled()) GameInput.Enable();
                }
            }
            catch (Exception e) { DevLog.Error("input capture", e); keyboard = false; }
        }

        public static string Status { get { return keyboard ? "editor has keyboard" + (weDisabled ? " (game input gated)" : "") : "game has keyboard"; } }
    }
}
