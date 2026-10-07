using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Unity-style move gizmo in the game view for the current selection.
    //   Per object: the "gizmo" button next to the position row in the Inspector (remembered per object).
    //   Global: Cheats -> "Move gizmo on every selection" shows it for whatever is selected; the per-object
    //   button is then greyed out.
    // Handles: X (red), Y (green), Z (blue) arrows move along that world axis; the centre square moves in the
    // camera plane. Ctrl snaps to 0.1 m, Shift moves 10x finer. Right mouse while dragging cancels (Esc would
    // also reach the game's pause menu).
    // Every drag is recorded (TransformMemory + ChangeRecorder) so "Reset to original" / History work.
    public static class TransformGizmo
    {
        public static bool globalOn;
        static readonly HashSet<int> perObject = new HashSet<int>();

        static int hot = -1;          // hovered handle: 0 x, 1 y, 2 z, 3 centre
        static int drag = -1;         // handle being dragged
        static Transform dragT;
        static Rigidbody dragRb;
        static Vector3 startPos, grab;   // grab: axis param (float in .x) or plane point at drag start

        public static bool Busy { get { return Visible && (hot >= 0 || drag >= 0); } }
        public static bool Visible { get { var s = Selection.Current; return s != null && (globalOn || perObject.Contains(s.GetInstanceID())); } }
        public static bool IsOnFor(GameObject g) { return g != null && perObject.Contains(g.GetInstanceID()); }
        public static void SetFor(GameObject g, bool on) { if (g == null) return; if (on) perObject.Add(g.GetInstanceID()); else perObject.Remove(g.GetInstanceID()); }
        public static void Load() { globalOn = EditorState.Get("gizmo.global", false); }
        public static void SetGlobal(bool on) { globalOn = on; EditorState.Set("gizmo.global", on); }

        static readonly Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
        static readonly Color[] colors = { new Color(1f, 0.25f, 0.25f, 1f), new Color(0.35f, 1f, 0.3f, 1f), new Color(0.3f, 0.55f, 1f, 1f) };
        static readonly Color cHot = new Color(1f, 0.92f, 0.2f, 1f), cCentre = new Color(1f, 1f, 1f, 0.9f);
        const float ScreenLen = 90f, HitPx = 9f;

        static float WorldLen(Camera cam, Vector3 p)
        {
            if (cam.orthographic) return cam.orthographicSize * 2f * ScreenLen / Screen.height;
            float d = Vector3.Dot(p - cam.transform.position, cam.transform.forward);
            return Mathf.Max(0.01f, d) * 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * ScreenLen / Screen.height;
        }

        static bool Proj(Camera cam, Vector3 p, out Vector2 s)
        {
            var sp = RenderScale.W2S(cam, p);
            s = new Vector2(sp.x, Screen.height - sp.y);
            return sp.z > 0;
        }

        static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a; float l2 = ab.sqrMagnitude;
            float t = l2 < 1e-4f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2);
            return (a + ab * t - p).magnitude;
        }

        static Ray MouseRay(Camera cam) { var m = EditorInput.mouse; return RenderScale.Ray(cam, new Vector3(m.x, Screen.height - m.y, 0)); }

        // parameter along the axis line (origin o, dir a) of the point closest to the ray
        static float AxisParam(Ray r, Vector3 o, Vector3 a)
        {
            Vector3 w = o - r.origin;
            float b = Vector3.Dot(a, r.direction), d = Vector3.Dot(a, w), e = Vector3.Dot(r.direction, w);
            float den = 1f - b * b;
            if (den < 1e-5f) return 0f;             // axis points at the camera
            return (b * e - d) / den;
        }

        static bool PlaneHit(Ray r, Vector3 o, Vector3 n, out Vector3 hit)
        {
            hit = o;
            float den = Vector3.Dot(n, r.direction);
            if (Mathf.Abs(den) < 1e-5f) return false;
            float t = Vector3.Dot(o - r.origin, n) / den;
            if (t < 0) return false;
            hit = r.origin + r.direction * t; return true;
        }

        // called every frame before WorldPick (so a click on a handle is not a pick)
        public static void Update(bool overUI)
        {
            var sel = Selection.Current;
            var cam = G.Cam();
            if (!Visible || cam == null) { hot = -1; if (drag >= 0) EndDrag(false); return; }
            var t = sel.transform;
            if (drag >= 0)
            {
                if (dragT != t || EditorInput.down[1]) { EndDrag(true); return; }
                if (!EditorInput.held[0]) { EndDrag(false); return; }
                var ray = MouseRay(cam);
                Vector3 np = startPos;
                if (drag < 3)
                {
                    float d = AxisParam(ray, startPos, axes[drag]) - grab.x;
                    if (EditorInput.shift) d *= 0.1f;
                    np = startPos + axes[drag] * d;
                }
                else
                {
                    Vector3 h;
                    if (PlaneHit(ray, startPos, -cam.transform.forward, out h)) { var delta = h - grab; if (EditorInput.shift) delta *= 0.1f; np = startPos + delta; }
                }
                if (EditorInput.ctrl) np = new Vector3(Mathf.Round(np.x * 10f) / 10f, Mathf.Round(np.y * 10f) / 10f, Mathf.Round(np.z * 10f) / 10f);
                Apply(np);
                return;
            }
            hot = -1;
            if (overUI) return;
            var p = t.position;
            Vector2 o;
            if (!Proj(cam, p, out o)) return;
            float len = WorldLen(cam, p);
            var m = EditorInput.mouse;
            if (Mathf.Abs(m.x - o.x) <= 7f && Mathf.Abs(m.y - o.y) <= 7f) hot = 3;
            else
            {
                float best = HitPx;
                for (int i = 0; i < 3; i++)
                {
                    Vector2 e;
                    if (!Proj(cam, p + axes[i] * len, out e)) continue;
                    if ((e - o).sqrMagnitude < 16f) continue;   // axis points at the camera
                    float dd = SegDist(m, o, e);
                    if (dd < best) { best = dd; hot = i; }
                }
            }
            if (hot >= 0 && EditorInput.down[0]) BeginDrag(cam, t, hot);
        }

        static void BeginDrag(Camera cam, Transform t, int h)
        {
            drag = h; dragT = t; startPos = t.position;
            dragRb = t.GetComponent<Rigidbody>();
            var ray = MouseRay(cam);
            if (h < 3) grab = new Vector3(AxisParam(ray, startPos, axes[h]), 0, 0);
            else { Vector3 hit; grab = PlaneHit(ray, startPos, -cam.transform.forward, out hit) ? hit : startPos; }
            TransformMemory.Remember(t);
            ChangeRecorder.Before(t.gameObject, ChangeRecorder.PropKind.Position, "gizmo");
        }

        static void Apply(Vector3 p)
        {
            if (dragT == null) return;
            // record every step of the drag (History merges consecutive moves of the same object into one entry),
            // so gizmo moves show up in History / the Mods tab and can be saved into a mod
            if ((dragT.position - p).sqrMagnitude > 1e-10f) ChangeRecorder.Before(dragT.gameObject, ChangeRecorder.PropKind.Position, "gizmo");
            dragT.position = p;
            if (dragRb != null) { dragRb.position = p; if (!dragRb.isKinematic) { dragRb.velocity = Vector3.zero; dragRb.angularVelocity = Vector3.zero; } }
        }

        static void EndDrag(bool cancel)
        {
            if (cancel && dragT != null) Apply(startPos);
            if (dragT != null) DevLog.Write("[gizmo] " + (cancel ? "cancelled " : "moved ") + dragT.name + " " + startPos.ToString("F2") + " -> " + dragT.position.ToString("F2"));
            drag = -1; dragT = null; dragRb = null;
        }

        // world layer (physical px)
        public static void Emit(Draw d)
        {
            if (!Visible) return;
            var cam = G.Cam();
            if (cam == null) return;
            var t = Selection.Current.transform;
            var p = t.position;
            Vector2 o;
            if (!Proj(cam, p, out o)) return;
            float len = WorldLen(cam, p);
            for (int i = 0; i < 3; i++)
            {
                Vector2 e;
                if (!Proj(cam, p + axes[i] * len, out e)) continue;
                var dir = e - o;
                if (dir.sqrMagnitude < 16f) { d.Fill(new Rect(o.x - 3, o.y - 3, 6, 6), colors[i]); continue; }
                bool on = hot == i || drag == i;
                var c = on ? cHot : colors[i];
                var nrm = new Vector2(-dir.y, dir.x).normalized;
                for (int k = -1; k <= 1; k++) d.Line(o + nrm * k, e + nrm * k, c);          // 3 px shaft
                var u = dir.normalized;
                d.Triangle(e + u * 12f, e + nrm * 6f, e - nrm * 6f, c);                      // arrow head
                d.ShadowText(e.x + u.x * 16f - 4, e.y + u.y * 16f - 7, i == 0 ? "X" : i == 1 ? "Y" : "Z", c, 12);
            }
            var cc = hot == 3 || drag == 3 ? cHot : cCentre;
            d.Frame(new Rect(o.x - 6, o.y - 6, 12, 12), cc);
            d.Fill(new Rect(o.x - 3, o.y - 3, 6, 6), cc);
            if (drag >= 0)
            {
                var delta = t.position - startPos;
                d.ShadowText(o.x + 14, o.y + 10, "moved " + delta.ToString("F2") + "   right mouse = cancel", cHot, 12);
                Vector2 s0;
                if (Proj(cam, startPos, out s0)) d.Line(s0, o, new Color(1f, 1f, 1f, 0.5f));
            }
            else if (hot >= 0) d.ShadowText(o.x + 14, o.y + 10, hot == 3 ? "drag: move in camera plane" : "drag: move along " + "XYZ"[hot] + "   (Ctrl snap 0.1, Shift fine)", cHot, 11);
        }
    }
}
