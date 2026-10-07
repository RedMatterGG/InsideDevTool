using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Pick objects in the game view (P toggles pick mode, Shift+P cycles the category filter).
    // One ray from the cursor collects everything it touches, each tagged with a category:
    //   physics hits      -> Collider, Trigger/<savepoint|kill|camera|audio|general>
    //   renderer bounds   -> Mesh (ray passes through the object's visible bounds)
    //   near the cursor   -> Light, Camera, Audio, Savepoint, Character, Animation, Logic (FSM/signal/script),
    //                        Object (empty/group), Inactive  (pivot within a few pixels; finds things with no
    //                        collider or renderer and switched-off objects)
    // Hovering lists the candidates next to the cursor; click selects the highlighted one, clicking again at the
    // same spot moves down the list.
    // Cost: the ray is cast at most 10x per second and only when the mouse or camera moved. Screen positions and
    // renderer bounds of the object database are refreshed incrementally (a slice per frame), so a hover query is a
    // few thousand float compares instead of 25k WorldToScreenPoint + GetComponent calls.
    public static class WorldPick
    {
        public static bool mode;
        public static int filter;   // index into Filters
        public static readonly string[] Filters = { "all", "objects", "meshes", "colliders", "triggers", "lights", "cameras", "audio", "savepoints", "logic", "inactive" };

        public sealed class Cand
        {
            public GameObject go; public string cat, detail; public float order;
        }
        static readonly List<Cand> cands = new List<Cand>(64);
        static readonly List<Cand> pool = new List<Cand>(64);
        static int poolUsed;
        static readonly HashSet<int> seen = new HashSet<int>();

        static Vector2 lastClickPos = new Vector2(-999, -999);
        static int cycle;
        static float hoverAt = -1f;
        static Vector2 hoverMouse;
        static Vector3 hoverCamPos; static Quaternion hoverCamRot;
        public static string lastResult = "";
        public static int queries;       // diagnostics: ray queries issued

        const float PixelRadius = 14f;
        const float QueryInterval = 0.1f;   // max 10 queries per second
        const int SlicePerFrame = 2500;

        // ------------------------------------------------------------ incremental screen/bounds cache
        struct Slot { public float sx, sy, depth; public bool onScreen; public Bounds rb; public bool hasR; }
        static Slot[] slots = new Slot[0];
        static int sliceAt, cacheFor = -1;
        static readonly Dictionary<int, Renderer> rendOf = new Dictionary<int, Renderer>(8192);

        static void RefreshSlice(Camera cam)
        {
            var all = ObjectDatabase.all;
            int n = all.Count;
            if (slots.Length < n) { slots = new Slot[n + 1024]; sliceAt = 0; }
            if (cacheFor != ObjectDatabase.completedPasses) { cacheFor = ObjectDatabase.completedPasses; sliceAt = 0; }
            if (n == 0) return;
            var cp = cam.transform.position;
            int end = Math.Min(n, sliceAt + SlicePerFrame);
            for (int i = sliceAt; i < end; i++)
            {
                var r = all[i]; var s = new Slot();
                if (r.go != null && r.t != null)
                {
                    var wp = r.t.position;
                    if ((wp - cp).sqrMagnitude < 150f * 150f)
                    {
                        var sp = RenderScale.W2S(cam, wp);
                        s.sx = sp.x; s.sy = Screen.height - sp.y; s.depth = sp.z; s.onScreen = sp.z > 0;
                    }
                    if ((r.kind & ObjKind.Renderer) != 0 && r.activeInHierarchy)
                    {
                        Renderer rend;
                        if (!rendOf.TryGetValue(r.id, out rend)) { rend = r.go.GetComponent<Renderer>(); rendOf[r.id] = rend; }
                        if (rend != null && rend.enabled) { s.rb = rend.bounds; s.hasR = true; }
                    }
                }
                slots[i] = s;
            }
            sliceAt = end >= n ? 0 : end;
            if (rendOf.Count > 60000) rendOf.Clear();
        }

        // ------------------------------------------------------------ categories
        static string CategoryOf(ObjRecord r)
        {
            var k = r.kind;
            if (!r.activeInHierarchy) return "Inactive";
            if ((k & ObjKind.Savepoint) != 0) return "Savepoint";
            if ((k & ObjKind.Light) != 0) return "Light";
            if ((k & ObjKind.Camera) != 0) return "Camera";
            if ((k & ObjKind.Audio) != 0) return "Audio";
            if ((k & ObjKind.Character) != 0) return "Character";
            if ((k & ObjKind.Animation) != 0) return "Animation";
            if ((k & (ObjKind.StateMachine | ObjKind.Signal | ObjKind.Script)) != 0) return "Logic";
            if ((k & ObjKind.Trigger) != 0) return "Trigger";
            if ((k & ObjKind.Collider) != 0) return "Collider";
            if ((k & ObjKind.Renderer) != 0) return "Mesh";
            return "Object";
        }

        static bool Allowed(string cat)
        {
            switch (Filters[filter])
            {
                case "all": return true;
                case "objects": return cat == "Object" || cat == "Mesh" || cat == "Character" || cat == "Animation";
                case "meshes": return cat == "Mesh";
                case "colliders": return cat == "Collider";
                case "triggers": return cat.StartsWith("Trigger");
                case "lights": return cat == "Light";
                case "cameras": return cat == "Camera" || cat == "Trigger/camera";
                case "audio": return cat == "Audio" || cat == "Trigger/audio";
                case "savepoints": return cat == "Savepoint" || cat == "Trigger/savepoint";
                case "logic": return cat == "Logic";
                case "inactive": return cat == "Inactive";
            }
            return true;
        }

        static void Add(GameObject go, string cat, string detail, float order)
        {
            if (cands.Count >= 300 || !Allowed(cat) || !seen.Add(go.GetInstanceID())) return;
            if (poolUsed == pool.Count) pool.Add(new Cand());
            var c = pool[poolUsed++];
            c.go = go; c.cat = cat; c.detail = detail; c.order = order;
            cands.Add(c);
        }

        static bool IsBoy(ObjRecord r) { return r.path == "_Boy" || r.path.StartsWith("_Boy/"); }

        static void Collect(Vector2 mouse, bool exact)
        {
            cands.Clear(); seen.Clear(); poolUsed = 0; queries++;
            var cam = G.Cam();
            if (cam == null) return;
            var ray = RenderScale.Ray(cam, new Vector3(mouse.x, Screen.height - mouse.y, 0));
            // 1) physics: solid colliders and trigger volumes
            try
            {
                var hits = Physics.RaycastAll(ray, 400f, ~0);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (var h in hits)
                {
                    var col = h.collider;
                    if (col == null) continue;
                    var g = col.gameObject;
                    if (g.transform.root.name.StartsWith("_Boy")) continue;
                    string cat = col.isTrigger ? "Trigger/" + Overlay.TriggerKindOf(col) : "Collider";
                    Add(g, cat, col.GetType().Name + ", " + h.distance.ToString("0.0") + " m", h.distance);
                }
            }
            catch (Exception e) { DevLog.Error("pick raycast", e); }
            // 2) renderer bounds the ray passes through, 3) pivots near the cursor
            var all = ObjectDatabase.all;
            int n = Math.Min(all.Count, slots.Length);
            var cp = cam.transform.position;
            for (int i = 0; i < n; i++)
            {
                var s = slots[i];
                var r = all[i];
                if (s.hasR)
                {
                    float dist;
                    if (s.rb.IntersectRay(ray, out dist) && r.go != null && !IsBoy(r))
                    {
                        if (exact) { Renderer rend; if (rendOf.TryGetValue(r.id, out rend) && rend != null && !rend.bounds.IntersectRay(ray)) continue; }
                        // bigger boxes (floors, backdrops) sort after small ones at the same depth
                        Add(r.go, "Mesh", "bounds " + s.rb.size.x.ToString("0.#") + "x" + s.rb.size.y.ToString("0.#") + "x" + s.rb.size.z.ToString("0.#") + ", " + dist.ToString("0.0") + " m",
                            dist + 0.02f * s.rb.size.magnitude + 0.5f);
                        continue;
                    }
                }
                if (!s.onScreen) continue;
                float dx = s.sx - mouse.x, dy = s.sy - mouse.y;
                if (dx * dx + dy * dy > PixelRadius * PixelRadius) continue;
                if (r.go == null || r.kind == ObjKind.None && r.childCount == 0 || IsBoy(r)) continue;
                float px = Mathf.Sqrt(dx * dx + dy * dy);
                if (exact && r.t != null)
                {
                    var sp = RenderScale.W2S(cam, r.t.position);
                    if (sp.z <= 0) continue;
                    float ex = sp.x - mouse.x, ey = Screen.height - sp.y - mouse.y;
                    px = Mathf.Sqrt(ex * ex + ey * ey);
                    if (px > PixelRadius) continue;
                }
                Add(r.go, CategoryOf(r), "pivot " + px.ToString("0") + " px, " + s.depth.ToString("0.0") + " m", 1000f + px + s.depth * 0.01f);
            }
            cands.Sort((a, b) => a.order.CompareTo(b.order));
            if (cands.Count > 40) cands.RemoveRange(40, cands.Count - 40);
        }

        // ------------------------------------------------------------ per frame
        public static void Update(bool overUI, bool panelOpen)
        {
            if (EditorInput.Key(KeyCode.P))
            {
                if (EditorInput.shift) { filter = (filter + 1) % Filters.Length; mode = true; hoverAt = -1f; DevLog.Write("[pick] filter " + Filters[filter]); }
                else { mode = !mode; DevLog.Write("[pick] mode " + (mode ? "ON" : "off")); }
            }
            if (!mode) { if (cands.Count > 0) { cands.Clear(); queries++; } return; }
            var cam = G.Cam();
            if (cam == null) return;
            RefreshSlice(cam);
            var m = EditorInput.mouse;
            if (overUI || TransformGizmo.Busy) { if (cands.Count > 0) { cands.Clear(); queries++; } return; }
            float now = Time.realtimeSinceStartup;
            bool moved = (m - hoverMouse).sqrMagnitude > 9f;
            bool camMoved = (cam.transform.position - hoverCamPos).sqrMagnitude > 0.0004f || Quaternion.Angle(cam.transform.rotation, hoverCamRot) > 0.2f;
            if (now - hoverAt >= QueryInterval && (moved || camMoved || now - hoverAt > 1f))
            {
                hoverAt = now; hoverCamPos = cam.transform.position; hoverCamRot = cam.transform.rotation;
                if (moved) cycle = 0;
                hoverMouse = m;
                Collect(m, false);
                if (cycle >= cands.Count) cycle = 0;
            }
            if (cands.Count > 0 && cands[cycle].go != null) Selection.SetHover(cands[cycle].go);
            if (!EditorInput.down[0]) return;
            Collect(m, true);
            if (cands.Count == 0) { lastResult = "nothing under the cursor (" + Filters[filter] + ")"; return; }
            if ((m - lastClickPos).sqrMagnitude < 36 && Selection.Current == (cycle < cands.Count ? cands[cycle].go : null)) cycle = (cycle + 1) % cands.Count;
            else if (cycle >= cands.Count) cycle = 0;
            lastClickPos = m; hoverMouse = m; hoverAt = now;
            var c = cands[cycle];
            Selection.Set(c.go, "pick");
            lastResult = "picked [" + c.cat + "] " + c.go.name + " (" + (cycle + 1) + "/" + cands.Count + ", " + c.detail + ")";
            DevLog.Write("[pick] " + lastResult);
        }

        // cursor list (world layer, physical px)
        static readonly Color cHead = new Color(0.7f, 0.8f, 0.9f, 1f), cRow = new Color(0.85f, 0.87f, 0.9f, 1f), cCur = new Color(1f, 0.9f, 0.3f, 1f);
        // the text is rebuilt only when the candidate list, the highlighted row or the filter changes
        static readonly List<string> lines = new List<string>(12);
        static readonly List<bool> lineCur = new List<bool>(12);
        static int linesQuery = -1, linesCycle = -1, linesFilter = -1;
        public static void Emit(Draw d)
        {
            if (!mode) return;
            if (linesQuery != queries || linesCycle != cycle || linesFilter != filter)
            {
                linesQuery = queries; linesCycle = cycle; linesFilter = filter;
                lines.Clear(); lineCur.Clear();
                if (cands.Count == 0) { lines.Add("pick: " + Filters[filter] + " - nothing here   (Shift+P = filter)"); lineCur.Add(false); }
                else
                {
                    lines.Add("pick: " + Filters[filter] + "   " + cands.Count + " found   click = select, click again = next, Shift+P = filter"); lineCur.Add(false);
                    int first = Mathf.Clamp(cycle - 4, 0, Math.Max(0, cands.Count - 10));
                    for (int i = first; i < cands.Count && i < first + 10; i++)
                    {
                        var c = cands[i];
                        if (c.go == null) continue;
                        lines.Add((i == cycle ? "> " : "  ") + "[" + c.cat + "] " + c.go.name + "   " + c.detail); lineCur.Add(i == cycle);
                    }
                    if (cands.Count > first + 10) { lines.Add("  ... " + (cands.Count - first - 10) + " more"); lineCur.Add(false); }
                }
            }
            var m = EditorInput.mouse;
            float y = m.y + 10;
            for (int i = 0; i < lines.Count; i++)
            {
                bool cur = lineCur[i];
                d.ShadowText(m.x + 16, y, lines[i], i == 0 ? cHead : cur ? cCur : cRow, cur ? 13 : 11);
                y += cur ? 17 : i == 0 ? 15 : 14;
            }
        }

        // bridge test: pick at a GUI-space pixel (top-left origin), same rules as a click
        public static string PickAt(Vector2 p)
        {
            var cam = G.Cam();
            if (cam != null) { int guard = 0; do RefreshSlice(cam); while (sliceAt != 0 && ++guard < 100); }
            Collect(p, true);
            if (cands.Count == 0) return "nothing at " + p + " (filter " + Filters[filter] + ")";
            var sb = new System.Text.StringBuilder(cands.Count + " candidate(s) at " + p + " (filter " + Filters[filter] + ")\n");
            for (int i = 0; i < cands.Count; i++) sb.Append(i == 0 ? " -> " : "    ").Append('[').Append(cands[i].cat).Append("] ").Append(Inspector.PathOf(cands[i].go.transform)).Append("   ").Append(cands[i].detail).Append('\n');
            Selection.Set(cands[0].go, "pick");
            return sb.ToString();
        }
    }
}
