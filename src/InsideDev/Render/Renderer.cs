using System;
using System.Collections.Generic;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    public struct RenderStats
    {
        public int passes;      // Material.SetPass calls
        public int batches;     // draw calls (GL.Begin/End pairs or DrawMeshNow)
        public int quads, lines, glyphs, texts, clippedTexts, segments;
        public int width, height;
        public string error;
        public int Primitives { get { return quads + lines + glyphs; } }
    }

    // Consumes a Draw command list. Implementations own nothing but transient GPU objects;
    // shared materials/font live in RenderResources; all editor state lives outside the renderer.
    public interface IEditorRenderer
    {
        string Name { get; }
        void Invalidate(string reason);            // mark transient resources dirty; rebuilt lazily on next submit
        void DestroyResources();                   // hard reset (debug)
        bool Submit(Draw list, Color? probe, ref RenderStats stats);
        float Measure(string s, int size);         // physical px
        bool ResourcesOk(out string report);
    }

    // ------------------------------------------------------------------ shared resources
    public static class RenderResources
    {
        public static Material lineMat;
        public static Font font;
        static bool dirty = true;
        public static bool stateDirty = true;
        public static int rebuilds, invalidations;
        public static string lastReason = "startup";

        public static void Invalidate(string reason) { dirty = true; stateDirty = true; invalidations++; lastReason = reason; }

        public static void Destroy()
        {
            try { if (lineMat != null) UObj.Destroy(lineMat); } catch { }
            lineMat = null; font = null;
            Invalidate("hard reset");
        }

        public static bool MatOk(Material m) { return m != null && m.shader != null && m.shader.isSupported; }

        public static void Validate()
        {
            if (!dirty && MatOk(lineMat) && font != null) return;
            if (!MatOk(lineMat))
            {
                try { if (lineMat != null) UObj.Destroy(lineMat); } catch { }
                lineMat = null;
                try
                {
                    lineMat = new Material("Shader \"InsideDev/Lines\" { SubShader { Tags { \"Queue\"=\"Overlay\" } Pass { Blend SrcAlpha OneMinusSrcAlpha ZWrite Off ZTest Always Cull Off Fog { Mode Off } BindChannels { Bind \"vertex\", vertex Bind \"color\", color } } } }");
                }
                catch (Exception e) { DevLog.Error("renderer: line material", e); }
                if (!MatOk(lineMat))
                {
                    var sh = Shader.Find("Hidden/Internal-Colored");
                    if (sh != null) lineMat = new Material(sh);
                    DevLog.Write("[render] line material fallback Hidden/Internal-Colored: " + (sh != null));
                }
                if (lineMat != null) lineMat.hideFlags = HideFlags.HideAndDontSave;
                rebuilds++;
                RenderHost.Event("renderer: line material (re)built, ok=" + MatOk(lineMat) + " (" + lastReason + ")");
            }
            if (font == null)
            {
                try { font = (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf"); } catch (Exception e) { DevLog.Error("renderer: font", e); }
                if (font == null) { try { font = Font.CreateDynamicFontFromOSFont("Arial", Draw.DefaultSize); } catch { } }
                RenderHost.Event("renderer: font " + (font != null ? font.name : "NONE"));
            }
            dirty = false;
        }

        public static bool Ok(out string report)
        {
            bool mat = lineMat != null, sh = mat && lineMat.shader != null, shOk = sh && lineMat.shader.isSupported;
            bool f = font != null;
            Material fm = null; Texture ft = null;
            try { if (f) fm = font.material; if (fm != null) ft = fm.mainTexture; } catch { }
            report = "mat " + (mat ? "OK" : "MISSING") + "  shader " + (!sh ? "MISSING" : shOk ? "OK" : "UNSUPPORTED") +
                     "  font " + (f ? font.name : "MISSING") + "  fontMat " + (fm != null ? "OK" : "MISSING") +
                     "  atlas " + (ft != null ? ft.width + "x" + ft.height : "none") +
                     "  rebuilds " + rebuilds + "  invalidations " + invalidations;
            return mat && shOk && f && fm != null && ft != null;
        }

        public static float Measure(string s, int size)
        {
            Validate();
            var f = font;
            if (f == null || string.IsNullOrEmpty(s)) return (s ?? "").Length * size * 0.55f;
            f.RequestCharactersInTexture(s, size);
            float w = 0, best = 0;
            CharacterInfo ci;
            foreach (char ch in s)
            {
                if (ch == '\n') { best = Mathf.Max(best, w); w = 0; continue; }
                if (f.GetCharacterInfo(ch, out ci, size)) w += ci.advance;
                else w += size * 0.5f;
            }
            return Mathf.Max(best, w);
        }
    }

    // ------------------------------------------------------------------ tessellation
    // Turns commands into clipped, renderer-ready geometry in GL pixel space (bottom-left origin).
    // Solids and glyphs are quads (4 verts), lines are pairs. One Geometry per segment.
    public sealed class Geometry
    {
        public readonly List<Vector3> sv = new List<Vector3>(4096);   // solid quads
        public readonly List<Color> sc = new List<Color>(4096);
        public readonly List<Vector3> lv = new List<Vector3>(4096);   // line pairs
        public readonly List<Color> lc = new List<Color>(4096);
        public readonly List<Vector3> tv = new List<Vector3>(16384);  // glyph quads
        public readonly List<Vector2> tu = new List<Vector2>(16384);
        public readonly List<Color> tc = new List<Color>(16384);
        public void Clear() { sv.Clear(); sc.Clear(); lv.Clear(); lc.Clear(); tv.Clear(); tu.Clear(); tc.Clear(); }
    }

    public static class Tessellator
    {
        static float H; static bool flip;
        static float Y(float y) { return flip ? y : H - y; }

        public static void Build(Draw.Segment seg, Geometry g, Font font, int h, bool flipY, ref RenderStats st)
        {
            g.Clear();
            H = h; flip = flipY;
            // solids
            for (int i = 0; i < seg.solids.Count; i++)
            {
                var d = seg.solids[i];
                if (d.kind == CmdKind.Rect)
                {
                    var r = Intersect(d.r, d.clip);
                    if (r.width <= 0 || r.height <= 0) continue;
                    Quad(g.sv, g.sc, r.xMin, r.yMin, r.xMax, r.yMax, d.c);
                }
                else
                {
                    // triangles are drawn only when fully inside their clip (UI icons/arrows are small)
                    if (!Inside(d.a, d.clip) || !Inside(d.b, d.clip) || !Inside(d.c2, d.clip)) continue;
                    g.sv.Add(new Vector3(d.a.x, Y(d.a.y), 0)); g.sv.Add(new Vector3(d.b.x, Y(d.b.y), 0));
                    g.sv.Add(new Vector3(d.c2.x, Y(d.c2.y), 0)); g.sv.Add(new Vector3(d.c2.x, Y(d.c2.y), 0));
                    for (int k = 0; k < 4; k++) g.sc.Add(d.c);
                }
                st.quads++;
            }
            // lines (Liang-Barsky)
            for (int i = 0; i < seg.lines.Count; i++)
            {
                var d = seg.lines[i];
                Vector2 a = d.a, b = d.b;
                if (!ClipLine(ref a, ref b, d.clip)) continue;
                g.lv.Add(new Vector3(a.x, Y(a.y), 0)); g.lv.Add(new Vector3(b.x, Y(b.y), 0));
                g.lc.Add(d.c); g.lc.Add(d.c);
                st.lines++;
            }
            // text
            if (seg.texts.Count > 0 && font != null) Texts(seg, g, font, ref st);
        }

        static void Texts(Draw.Segment seg, Geometry g, Font f, ref RenderStats st)
        {
            var ts = seg.texts;
            // request everything first: a request can rebuild the atlas and invalidate earlier UVs
            for (int i = 0; i < ts.Count; i++) f.RequestCharactersInTexture(ts[i].s, ts[i].size);
            CharacterInfo ci;
            for (int i = 0; i < ts.Count; i++)
            {
                var t = ts[i];
                var cl = t.clip;
                if (t.y > cl.yMax || t.y + t.size * 1.3f < cl.yMin || t.x > cl.xMax) { st.clippedTexts++; continue; }
                st.texts++;
                float x = Mathf.Round(t.x);
                float lineTop = Mathf.Round(t.y);
                float baseline = lineTop + Mathf.Round(t.size * 0.95f);
                foreach (char ch in t.s)
                {
                    if (ch == '\n') { x = Mathf.Round(t.x); lineTop += t.size + 3; baseline = lineTop + Mathf.Round(t.size * 0.95f); continue; }
                    if (!f.GetCharacterInfo(ch, out ci, t.size)) { x += t.size * 0.5f; continue; }
                    // glyph box in top-left space
                    float x0 = x + ci.minX, x1 = x + ci.maxX;
                    float yTop = baseline - ci.maxY, yBot = baseline - ci.minY;
                    x += ci.advance;
                    if (x1 <= x0 || yBot <= yTop) continue;
                    float cx0 = Mathf.Max(x0, cl.xMin), cx1 = Mathf.Min(x1, cl.xMax);
                    float cy0 = Mathf.Max(yTop, cl.yMin), cy1 = Mathf.Min(yBot, cl.yMax);
                    if (cx1 <= cx0 || cy1 <= cy0) continue;
                    // fractions of the glyph that remain (u: left->right, v: bottom->top)
                    float u0 = (cx0 - x0) / (x1 - x0), u1 = (cx1 - x0) / (x1 - x0);
                    float v0 = (yBot - cy1) / (yBot - yTop), v1 = (yBot - cy0) / (yBot - yTop);
                    g.tv.Add(new Vector3(cx0, Y(cy1), 0)); g.tu.Add(UV(ci, u0, v0));
                    g.tv.Add(new Vector3(cx1, Y(cy1), 0)); g.tu.Add(UV(ci, u1, v0));
                    g.tv.Add(new Vector3(cx1, Y(cy0), 0)); g.tu.Add(UV(ci, u1, v1));
                    g.tv.Add(new Vector3(cx0, Y(cy0), 0)); g.tu.Add(UV(ci, u0, v1));
                    g.tc.Add(t.c); g.tc.Add(t.c); g.tc.Add(t.c); g.tc.Add(t.c);
                    st.glyphs++;
                }
            }
            if (flip)
            {
                // legacy flip mode: glyphs were authored for bottom-left space; swap vertical winding of UVs
                for (int i = 0; i < g.tu.Count; i += 4) { var a = g.tu[i]; var b = g.tu[i + 1]; g.tu[i] = g.tu[i + 3]; g.tu[i + 1] = g.tu[i + 2]; g.tu[i + 3] = a; g.tu[i + 2] = b; }
            }
        }

        // bilinear over the four glyph UV corners (handles flipped/rotated atlas glyphs)
        static Vector2 UV(CharacterInfo ci, float u, float v)
        {
            Vector2 bottom = Vector2.Lerp(ci.uvBottomLeft, ci.uvBottomRight, u);
            Vector2 top = Vector2.Lerp(ci.uvTopLeft, ci.uvTopRight, u);
            return Vector2.Lerp(bottom, top, v);
        }

        static void Quad(List<Vector3> v, List<Color> c, float x0, float y0, float x1, float y1, Color col)
        {
            v.Add(new Vector3(x0, Y(y0), 0)); v.Add(new Vector3(x1, Y(y0), 0));
            v.Add(new Vector3(x1, Y(y1), 0)); v.Add(new Vector3(x0, Y(y1), 0));
            c.Add(col); c.Add(col); c.Add(col); c.Add(col);
        }

        static Rect Intersect(Rect a, Rect b)
        {
            float x0 = Mathf.Max(a.xMin, b.xMin), y0 = Mathf.Max(a.yMin, b.yMin), x1 = Mathf.Min(a.xMax, b.xMax), y1 = Mathf.Min(a.yMax, b.yMax);
            return new Rect(x0, y0, x1 - x0, y1 - y0);
        }
        static bool Inside(Vector2 p, Rect r) { return p.x >= r.xMin && p.x <= r.xMax && p.y >= r.yMin && p.y <= r.yMax; }

        static bool ClipLine(ref Vector2 a, ref Vector2 b, Rect r)
        {
            float t0 = 0, t1 = 1, dx = b.x - a.x, dy = b.y - a.y;
            if (!Clip(-dx, a.x - r.xMin, ref t0, ref t1)) return false;
            if (!Clip(dx, r.xMax - a.x, ref t0, ref t1)) return false;
            if (!Clip(-dy, a.y - r.yMin, ref t0, ref t1)) return false;
            if (!Clip(dy, r.yMax - a.y, ref t0, ref t1)) return false;
            var na = new Vector2(a.x + t0 * dx, a.y + t0 * dy);
            var nb = new Vector2(a.x + t1 * dx, a.y + t1 * dy);
            a = na; b = nb;
            return true;
        }
        static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (Mathf.Abs(p) < 1e-6f) return q >= 0;
            float t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
    }

    // ------------------------------------------------------------------ shared submission frame
    public abstract class RendererBase : IEditorRenderer
    {
        public abstract string Name { get; }
        protected readonly Geometry geo = new Geometry();

        public void Invalidate(string reason) { RenderResources.Invalidate(reason); }
        public virtual void DestroyResources() { RenderResources.Destroy(); }
        public float Measure(string s, int size) { return RenderResources.Measure(s, size); }
        public bool ResourcesOk(out string report) { return RenderResources.Ok(out report); }

        // Every submission establishes all state it needs and always balances Push/Pop.
        public bool Submit(Draw list, Color? probe, ref RenderStats st)
        {
            st = new RenderStats();
            RenderResources.Validate();
            var mat = RenderResources.lineMat;
            if (!RenderResources.MatOk(mat)) { st.error = "no usable line material"; return false; }
            int w = Screen.width, h = Screen.height;
            if (w <= 0 || h <= 0) { st.error = "screen size " + w + "x" + h; return false; }
            st.width = w; st.height = h;
            var font = RenderResources.font;
            Material fmat = null;
            var prevRT = RenderTexture.active;
            bool pushed = false;
            try
            {
                if (RenderResources.stateDirty) { GL.InvalidateState(); RenderResources.stateDirty = false; }
                RenderTexture.active = null;           // backbuffer, explicitly
                GL.PushMatrix(); pushed = true;
                GL.Viewport(new Rect(0, 0, w, h));     // never inherit a camera's pixelRect
                GL.LoadPixelMatrix(0, w, 0, h);        // explicit pixel projection for the current size
                BeginFrame();
                foreach (var layer in new[] { list.world, list.ui })
                {
                    for (int i = 0; i < layer.used; i++)
                    {
                        var seg = layer.segments[i];
                        if (seg.Count == 0) continue;
                        Tessellator.Build(seg, geo, font, h, list.flipY, ref st);
                        if (geo.tv.Count > 0 && fmat == null) { fmat = font.material; if (fmat == null) throw new Exception("font material missing"); }
                        DrawGeometry(geo, mat, fmat, ref st);
                        st.segments++;
                    }
                }
                if (probe.HasValue)
                {
                    geo.Clear();
                    float a = RenderHost.ProbeInset - 3;
                    ProbeQuad(a, a, probe.Value);
                    ProbeQuad(a, h - RenderHost.ProbeInset - 3, probe.Value);
                    DrawGeometry(geo, mat, null, ref st);
                }
                return true;
            }
            catch (Exception e)
            {
                st.error = e.GetType().Name + ": " + e.Message;
                RenderResources.Invalidate("submit exception");
                return false;
            }
            finally
            {
                EndFrame();
                if (pushed) { try { GL.PopMatrix(); } catch { } }
                RenderTexture.active = prevRT;
            }
        }

        void ProbeQuad(float x, float y, Color c)
        {
            geo.sv.Add(new Vector3(x, y, 0)); geo.sv.Add(new Vector3(x + 6, y, 0)); geo.sv.Add(new Vector3(x + 6, y + 6, 0)); geo.sv.Add(new Vector3(x, y + 6, 0));
            for (int k = 0; k < 4; k++) geo.sc.Add(c);
        }

        protected static void Pass(Material m, ref RenderStats st) { if (!m.SetPass(0)) throw new Exception("SetPass failed on " + m.name); st.passes++; }
        protected virtual void BeginFrame() { }
        protected virtual void EndFrame() { }
        protected abstract void DrawGeometry(Geometry g, Material solid, Material text, ref RenderStats st);
    }

    // Immediate-mode GL backend.
    public sealed class UnityGLRenderer : RendererBase
    {
        public override string Name { get { return "UnityGL"; } }
        bool begun;

        protected override void EndFrame() { if (begun) { try { GL.End(); } catch { } begun = false; } }

        protected override void DrawGeometry(Geometry g, Material solid, Material text, ref RenderStats st)
        {
            if (g.sv.Count > 0)
            {
                Pass(solid, ref st);
                GL.Begin(GL.QUADS); begun = true;
                for (int i = 0; i < g.sv.Count; i++) { GL.Color(g.sc[i]); GL.Vertex(g.sv[i]); }
                GL.End(); begun = false; st.batches++;
            }
            if (g.lv.Count > 0)
            {
                Pass(solid, ref st);
                GL.Begin(GL.LINES); begun = true;
                for (int i = 0; i < g.lv.Count; i++) { GL.Color(g.lc[i]); GL.Vertex(g.lv[i]); }
                GL.End(); begun = false; st.batches++;
            }
            if (g.tv.Count > 0 && text != null)
            {
                Pass(text, ref st);
                GL.Begin(GL.QUADS); begun = true;
                for (int i = 0; i < g.tv.Count; i++) { GL.Color(g.tc[i]); GL.TexCoord(g.tu[i]); GL.Vertex(g.tv[i]); }
                GL.End(); begun = false; st.batches++;
            }
        }
    }

    // Unity-native mesh backend (prototype): same geometry uploaded into pooled Meshes and drawn with
    // Graphics.DrawMeshNow. Arrays are padded to power-of-two capacity (Unity 5.0 has no List setters),
    // unused tail vertices are degenerate.
    public sealed class UnityMeshRenderer : RendererBase
    {
        public override string Name { get { return "UnityMesh"; } }

        sealed class Slot
        {
            public Mesh mesh;
            public Vector3[] v; public Color[] c; public Vector2[] uv; public int[] idx;
        }
        readonly List<Slot> pool = new List<Slot>();
        int used;

        protected override void BeginFrame() { used = 0; }

        public override void DestroyResources()
        {
            foreach (var s in pool) { try { if (s.mesh != null) UObj.Destroy(s.mesh); } catch { } }
            pool.Clear();
            base.DestroyResources();
        }

        Slot Take()
        {
            if (used == pool.Count)
            {
                var m = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                m.MarkDynamic();
                pool.Add(new Slot { mesh = m });
            }
            var s = pool[used++];
            if (s.mesh == null) { s.mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave }; s.mesh.MarkDynamic(); s.v = null; }
            return s;
        }

        static int Cap(int n) { int c = 64; while (c < n) c <<= 1; return c; }

        void Upload(Slot s, List<Vector3> v, List<Color> c, List<Vector2> uv, MeshTopology topo)
        {
            int n = Math.Min(v.Count, 32768), cap = Cap(n);   // Unity 5.0 meshes are limited to 65000 vertices
            bool realloc = s.v == null || s.v.Length != cap;
            if (realloc) { s.v = new Vector3[cap]; s.c = new Color[cap]; s.uv = new Vector2[cap]; }
            for (int i = 0; i < n; i++) { s.v[i] = v[i]; s.c[i] = c[i]; if (uv != null) s.uv[i] = uv[i]; }
            for (int i = n; i < cap; i++) { s.v[i] = Vector3.zero; s.c[i] = Color.clear; }
            int icount = topo == MeshTopology.Lines ? cap : cap / 4 * 6;
            if (s.idx == null || s.idx.Length != icount) s.idx = new int[icount];
            if (topo == MeshTopology.Lines) { for (int i = 0; i < cap; i++) s.idx[i] = i < n ? i : 0; }
            else
            {
                int q = 0;
                for (int i = 0; i < cap; i += 4)
                {
                    if (i < n) { s.idx[q++] = i; s.idx[q++] = i + 1; s.idx[q++] = i + 2; s.idx[q++] = i; s.idx[q++] = i + 2; s.idx[q++] = i + 3; }
                    else { for (int k = 0; k < 6; k++) s.idx[q++] = 0; }
                }
            }
            var m = s.mesh;
            if (realloc) m.Clear();
            m.vertices = s.v; m.colors = s.c; m.uv = s.uv;
            m.SetIndices(s.idx, topo, 0);
            m.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
        }

        protected override void DrawGeometry(Geometry g, Material solid, Material text, ref RenderStats st)
        {
            if (g.sv.Count > 0) { var s = Take(); Upload(s, g.sv, g.sc, null, MeshTopology.Triangles); Pass(solid, ref st); Graphics.DrawMeshNow(s.mesh, Matrix4x4.identity); st.batches++; }
            if (g.lv.Count > 0) { var s = Take(); Upload(s, g.lv, g.lc, null, MeshTopology.Lines); Pass(solid, ref st); Graphics.DrawMeshNow(s.mesh, Matrix4x4.identity); st.batches++; }
            if (g.tv.Count > 0 && text != null) { var s = Take(); Upload(s, g.tv, g.tc, g.tu, MeshTopology.Triangles); Pass(text, ref st); Graphics.DrawMeshNow(s.mesh, Matrix4x4.identity); st.batches++; }
        }
    }
}
