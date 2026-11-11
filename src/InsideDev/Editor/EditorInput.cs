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
            // typed characters are only read by a focused text field; Input.inputString builds a new string on every call
            var core = DevCore.Instance;
            chars = core != null && core.UIKit.focus != null ? (Input.inputString ?? "") : "";
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

        // Editor hotkeys are ignored while a text field owns the keyboard. F1 is the exception: it types nothing, so it
        // always closes the panel and with it ends the typing (not while a capture has locked the editor).
        public static bool Key(KeyCode k)
        {
            if (InputCapture.keyboard && !(k == KeyCode.F1 && InputCapture.Typing && !(MultiCamSession.active && MultiCamSession.locked))) return false;
            return Input.GetKeyDown(k);
        }

        // Text field of an editor panel: ui.TextField, and InputCapture learns which field owns the keyboard, so the focus
        // is given back by itself once that field is no longer drawn (its panel, tab or row went away).
        public static bool Field(UI ui, string id, ref string value, float w = -1)
        {
            bool enter = ui.TextField(id, ref value, w);
            if (ui.focus == id) InputCapture.FieldSeen(id);
            return enter;
        }
    }

    // Keeps editor typing away from the boy: while a text field is focused the game's own input gate
    // (GameInput.Disable/Enable, the same switch GameManager uses during loads) is closed.
    // Release is delayed one frame so the Escape/Enter that ends typing can't reach the game menu,
    // and never re-opens the gate while the game itself is loading.
    public static class InputCapture
    {
        public static bool keyboard;        // editor owns the keyboard this frame
        static bool weDisabled, typing;
        static int releaseFrames;
        static string seenId; static int seenFrame;

        // a text field has the keyboard (not: the free camera or a capture lock holds the gate)
        public static bool Typing { get { return typing; } }

        internal static void FieldSeen(string id) { seenId = id; seenFrame = Time.frameCount; }

        // The focused field went through EditorInput.Field and was not laid out for more than two frames: it is gone from
        // the screen, the keyboard goes back to the game and the hotkeys. Two frames of grace cover a focus that is set
        // one frame before its field is drawn. Fields this tracker never saw are left alone.
        static bool DropOrphan()
        {
            var core = DevCore.Instance;
            var ui = core != null ? core.UIKit : null;
            if (ui == null || ui.focus != seenId) { seenId = null; return false; }
            if (Time.frameCount - seenFrame <= 2) return false;
            ui.focus = null; seenId = null;
            return true;
        }

        // wantKeyboard: a text field owns the keyboard (editor hotkeys off too).
        // gateGame: the game must not receive input (free camera), editor hotkeys stay on.
        public static void Update(bool wantKeyboard, bool gateGame = false)
        {
            try
            {
                if (seenId != null && DropOrphan()) wantKeyboard = false;
                typing = wantKeyboard;
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
            catch (Exception e) { DevLog.Error("input capture", e); keyboard = false; typing = false; }
        }

        public static string Status { get { return keyboard ? "editor has keyboard" + (weDisabled ? " (game input gated)" : "") : "game has keyboard"; } }

        // World layer, top of the game view: says why the boy does not move while a text field has the keyboard.
        static readonly Color cTagBack = new Color(0f, 0f, 0f, 0.62f), cTag = new Color(1f, 0.85f, 0.45f, 1f);
        const string TagText = "typing in the editor: game input paused   (Esc or F1 ends it)";
        public static void EmitTag(Draw d, Rect view)
        {
            if (!typing) return;
            float w = d.Measure(TagText, 12) + 16;
            if (view.width < w + 8) return;
            var r = new Rect(view.x + (view.width - w) * 0.5f, view.y + 58, w, 20);
            d.Fill(r, cTagBack);
            d.Text(r.x + 8, r.y + 3, TagText, cTag, 12);
        }
    }
}
