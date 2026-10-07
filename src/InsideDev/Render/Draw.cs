using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    public enum CmdKind : byte { Rect, Line, Text, Triangle }

    // One renderer-independent screen-space draw command. Coordinates are top-left-origin physical pixels.
    public struct DrawCmd
    {
        public CmdKind kind;
        public Rect r;             // Rect
        public Vector2 a, b, c2;   // Line (a,b), Triangle (a,b,c2)
        public float x, y;         // Text
        public string s;           // Text
        public int size;           // Text (physical px)
        public Rect clip;          // physical px; commands are clipped on the CPU by the tessellator
        public Color c;
    }

    // Command list recorded by the editor (overlay, HUD, UI widgets) and consumed by an IEditorRenderer.
    // Owns no GPU resources: a renderer reset never touches what is recorded here or any editor state.
    //
    // Structure: two layers (world below UI). Each layer is a list of segments drawn in order (z-order);
    // inside a segment the tessellator emits solids, then lines, then text. Windows/panels open their own
    // segment so they stack correctly. Every command carries the clip rect active when it was recorded.
    //
    // UI scale: while `uiLayer` is set, coordinates and text sizes are logical and multiplied by `uiScale`.
    public class Draw
    {
        public sealed class Segment
        {
            public readonly List<DrawCmd> solids = new List<DrawCmd>(256);
            public readonly List<DrawCmd> lines = new List<DrawCmd>(512);
            public readonly List<DrawCmd> texts = new List<DrawCmd>(256);
            public void Clear() { solids.Clear(); lines.Clear(); texts.Clear(); }
            public int Count { get { return solids.Count + lines.Count + texts.Count; } }
        }

        public sealed class Layer
        {
            public readonly List<Segment> segments = new List<Segment>();
            public int used;   // segments in use this frame
            readonly List<Segment> pool = new List<Segment>();
            public Segment Current { get { if (used == 0) Next(); return segments[used - 1]; } }
            public void Next()
            {
                if (used < segments.Count) { segments[used].Clear(); used++; return; }
                var s = pool.Count > 0 ? pool[pool.Count - 1] : new Segment();
                if (pool.Count > 0) pool.RemoveAt(pool.Count - 1);
                s.Clear(); segments.Add(s); used++;
            }
            public void Clear() { for (int i = 0; i < used; i++) segments[i].Clear(); used = 0; }
            public int Count { get { int n = 0; for (int i = 0; i < used; i++) n += segments[i].Count; return n; } }
        }

        public readonly Layer world = new Layer();
        public readonly Layer ui = new Layer();

        public bool flipY;          // F9: legacy switch for setups where everything renders upside down
        public bool uiLayer;        // true while UI widgets record
        public float uiScale = 1f;  // 0.75 / 1 / 1.25 / 1.5
        public const int DefaultSize = 13;

        public static IEditorRenderer Metrics;   // text metrics come from the render resources (font)

        static readonly Rect NoClip = new Rect(-100000f, -100000f, 200000f, 200000f);
        // clip state is per layer: the world pass (built at render time) can never inherit a UI clip
        readonly List<Rect> uiClipStack = new List<Rect>(), worldClipStack = new List<Rect>();
        Rect uiClip = NoClip, worldClip = NoClip;
        List<Rect> clipStack { get { return uiLayer ? uiClipStack : worldClipStack; } }
        Rect clip { get { return uiLayer ? uiClip : worldClip; } set { if (uiLayer) uiClip = value; else worldClip = value; } }

        Layer L { get { return uiLayer ? ui : world; } }
        float S { get { return uiLayer ? uiScale : 1f; } }

        public void Clear() { world.Clear(); worldClipStack.Clear(); worldClip = NoClip; }
        public void ClearUI() { ui.Clear(); uiClipStack.Clear(); uiClip = NoClip; }

        // new z-order group on the current layer (call when a window/panel begins)
        public void BeginSegment() { L.Next(); }

        // clip rects are in the current layer's units (logical for UI) and intersect with the enclosing clip
        public void PushClip(Rect r)
        {
            clipStack.Add(clip);
            float s = S;
            var p = new Rect(r.x * s, r.y * s, r.width * s, r.height * s);
            float x0 = Mathf.Max(p.xMin, clip.xMin), y0 = Mathf.Max(p.yMin, clip.yMin);
            float x1 = Mathf.Min(p.xMax, clip.xMax), y1 = Mathf.Min(p.yMax, clip.yMax);
            clip = new Rect(x0, y0, Mathf.Max(0, x1 - x0), Mathf.Max(0, y1 - y0));
        }
        public int ClipDepth { get { return clipStack.Count; } }
        public void PopClipTo(int depth) { while (clipStack.Count > depth) PopClip(); }
        public void PopClip() { if (clipStack.Count > 0) { clip = clipStack[clipStack.Count - 1]; clipStack.RemoveAt(clipStack.Count - 1); } }
        public Rect CurrentClipLogical { get { float s = S; return new Rect(clip.x / s, clip.y / s, clip.width / s, clip.height / s); } }

        // ---------------------------------------------------------------- primitives
        public void Line(Vector2 a, Vector2 b, Color c)
        {
            float s = S;
            L.Current.lines.Add(new DrawCmd { kind = CmdKind.Line, a = a * s, b = b * s, c = c, clip = clip });
        }
        public void Fill(Rect r, Color c)
        {
            float s = S;
            L.Current.solids.Add(new DrawCmd { kind = CmdKind.Rect, r = new Rect(r.x * s, r.y * s, r.width * s, r.height * s), c = c, clip = clip });
        }
        public void Triangle(Vector2 a, Vector2 b, Vector2 c, Color col)
        {
            float s = S;
            L.Current.solids.Add(new DrawCmd { kind = CmdKind.Triangle, a = a * s, b = b * s, c2 = c * s, c = col, clip = clip });
        }
        public void Frame(Rect r, Color c)
        {
            Line(new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), c);
            Line(new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), c);
            Line(new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax), c);
            Line(new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMin), c);
        }
        public void Text(float x, float y, string s, Color c, int size = DefaultSize, float clipTop = -1e9f, float clipBottom = 1e9f)
        {
            if (string.IsNullOrEmpty(s)) return;
            float k = S;
            var cl = clip;
            if (clipTop > -1e8f || clipBottom < 1e8f)
            {
                float t = Mathf.Max(cl.yMin, clipTop * k), b = Mathf.Min(cl.yMax, clipBottom * k);
                cl = new Rect(cl.x, t, cl.width, Mathf.Max(0, b - t));
            }
            L.Current.texts.Add(new DrawCmd { kind = CmdKind.Text, x = x * k, y = y * k, s = s, c = c, size = Mathf.Max(6, Mathf.RoundToInt(size * k)), clip = cl });
        }
        public void ShadowText(float x, float y, string s, Color c, int size = DefaultSize)
        {
            Text(x + 1, y + 1, s, new Color(0, 0, 0, c.a * 0.9f), size);
            Text(x, y, s, c, size);
        }

        // ---------------------------------------------------------------- world-space primitives (projected at record time)
        Camera worldCam;
        public void SetWorldCamera(Camera cam) { worldCam = cam; }

        public bool Project(Vector3 p, out Vector2 screen)
        {
            screen = Vector2.zero;
            if (worldCam == null) return false;
            var sp = RenderScale.W2S(worldCam, p);
            if (sp.z <= 0) return false;
            screen = new Vector2(sp.x, Screen.height - sp.y);
            return true;
        }

        public void WorldLine(Vector3 a, Vector3 b, Color c)
        {
            Vector2 sa, sb;
            if (Project(a, out sa) && Project(b, out sb)) Line(sa, sb, c);
        }

        readonly Vector3[] corners = new Vector3[8];
        public void WorldBox(Vector3 center, Vector3 size, Quaternion rot, Color c)
        {
            Vector3 h = size * 0.5f;
            for (int i = 0; i < 8; i++)
                corners[i] = center + rot * new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z);
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit < 8; bit <<= 1)
                    if ((i & bit) == 0) WorldLine(corners[i], corners[i | bit], c);
        }

        // logical width of a string at a logical size (UI layer) or physical (world layer)
        public float Measure(string s, int size = DefaultSize)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            float k = S;
            int ps = Mathf.Max(6, Mathf.RoundToInt(size * k));
            var m = Metrics;
            if (m == null) return s.Length * size * 0.55f;
            try { return m.Measure(s, ps) / k; } catch { return s.Length * size * 0.55f; }
        }
    }
}
