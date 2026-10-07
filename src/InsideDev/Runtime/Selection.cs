using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Phase 5: one selection for the whole editor. Inspector, Scene Explorer, Objects list, reference lists,
    // world overlay and the bridge all read and write this; nobody keeps a private "selected" any more.
    //   current  - selected GameObject (+ optional component focus)
    //   hover    - object under the mouse in any list (world overlay highlights it, never changes selection)
    //   history  - back / forward like a browser (instance ids are session-only; destroyed entries are skipped)
    //   version  - bumps on every change so views can react once (e.g. Explorer reveals the new selection)
    public static class Selection
    {
        public static GameObject Current { get { return current; } }
        public static Component Focus { get { return focus; } }
        public static string Source { get; private set; }
        public static int Version { get; private set; }

        static GameObject current;
        static Component focus;
        static readonly List<GameObject> back = new List<GameObject>(), fwd = new List<GameObject>();
        const int MaxHistory = 64;

        // hover: set every frame by whoever draws the row under the mouse; expires after 2 frames
        static GameObject hover; static int hoverFrame = -10;
        public static GameObject Hover { get { return Time.frameCount - hoverFrame <= 2 ? hover : null; } }
        public static void SetHover(GameObject g) { if (g != null) { hover = g; hoverFrame = Time.frameCount; } }

        public static event Action<GameObject, string> Changed;

        public static void Set(GameObject go, string source, Component comp = null)
        {
            if (go == current && comp == focus) return;
            if (current != null && go != current)
            {
                back.Add(current);
                if (back.Count > MaxHistory) back.RemoveAt(0);
                fwd.Clear();
            }
            Apply(go, comp, source);
        }

        public static void Clear(string source) { Set(null, source); }

        static void Apply(GameObject go, Component comp, string source)
        {
            current = go; focus = comp; Source = source; Version++;
            if (go != null) { try { ReferenceIndex.Refresh(go); } catch (Exception e) { DevLog.Error("selection refs", e); } }
            var h = Changed;
            if (h != null) { try { h(go, source); } catch (Exception e) { DevLog.Error("selection listener", e); } }
        }

        public static bool CanBack { get { Prune(back); return back.Count > 0; } }
        public static bool CanForward { get { Prune(fwd); return fwd.Count > 0; } }

        public static void Back()
        {
            Prune(back);
            if (back.Count == 0) return;
            var g = back[back.Count - 1]; back.RemoveAt(back.Count - 1);
            if (current != null) fwd.Add(current);
            Apply(g, null, "history");
        }

        public static void Forward()
        {
            Prune(fwd);
            if (fwd.Count == 0) return;
            var g = fwd[fwd.Count - 1]; fwd.RemoveAt(fwd.Count - 1);
            if (current != null) back.Add(current);
            Apply(g, null, "history");
        }

        static void Prune(List<GameObject> l) { l.RemoveAll(g => g == null); }

        public static int HistoryCount { get { return back.Count + fwd.Count; } }

        // the selected object was destroyed (scene unload): drop it so views stop showing a dead object
        public static void Tick()
        {
            if (!ReferenceEquals(current, null) && current == null) { current = null; focus = null; Source = "destroyed"; Version++; }
        }

        public static string Describe()
        {
            if (current == null) return "nothing selected";
            return Inspector.PathOf(current.transform) + "  #" + current.GetInstanceID() + (focus != null ? "  [" + focus.GetType().Name + "]" : "") + "  (via " + Source + ")";
        }
    }
}

namespace InsideDev
{
    // World-space marker for the selection (cyan) and the hovered row (white): bounds of renderers + colliders,
    // or a small cross at the transform when the object has neither (or is inactive).
    public static class SelectionOverlay
    {
        public static bool enabled = true;
        static readonly Color cSel = new Color(0.2f, 0.95f, 1f, 1f), cHover = new Color(1f, 1f, 1f, 0.85f);

        public static void Emit(Draw d)
        {
            if (!enabled) return;
            var cam = G.Cam();
            if (cam == null) return;
            d.SetWorldCamera(cam);
            var sel = Selection.Current;
            if (sel != null) Mark(d, sel, cSel, true);
            var h = Selection.Hover;
            if (h != null && h != sel) Mark(d, h, cHover, false);
        }

        // bounds are recomputed at most every 0.25 s per object (GetComponentsInChildren on big hierarchies is not free)
        sealed class Cached { public int id; public float at; public Bounds b; public bool has; }
        static readonly Cached[] cache = { new Cached(), new Cached() };

        static void Measure(GameObject go, Cached k)
        {
            k.id = go.GetInstanceID(); k.at = Time.realtimeSinceStartup; k.has = false;
            k.b = new Bounds(go.transform.position, Vector3.zero);
            int n = 0;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.gameObject.activeInHierarchy || ++n > 2000) continue;
                if (!k.has) { k.b = r.bounds; k.has = true; } else k.b.Encapsulate(r.bounds);
            }
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
            {
                if (col == null || !col.gameObject.activeInHierarchy || !col.enabled || ++n > 4000) continue;
                if (!k.has) { k.b = col.bounds; k.has = true; } else k.b.Encapsulate(col.bounds);
            }
        }

        static void Mark(Draw d, GameObject go, Color c, bool label)
        {
            var k = label ? cache[0] : cache[1];
            if (k.id != go.GetInstanceID() || Time.realtimeSinceStartup - k.at > 0.25f) Measure(go, k);
            Bounds b = k.b; bool has = k.has;
            var p = go.transform.position;
            if (!has || b.size.sqrMagnitude < 1e-4f || b.size.magnitude > 400f)
            {
                // inactive / empty object: cross + small box at its position
                d.WorldLine(p + Vector3.left * 0.5f, p + Vector3.right * 0.5f, c);
                d.WorldLine(p + Vector3.down * 0.5f, p + Vector3.up * 0.5f, c);
                d.WorldBox(p, Vector3.one * 0.3f, Quaternion.identity, c);
            }
            else d.WorldBox(b.center, b.size, Quaternion.identity, c);
            if (label)
            {
                Vector2 s;
                if (d.Project(has ? new Vector3(b.center.x, b.max.y, b.center.z) : p, out s))
                    d.ShadowText(s.x + 4, s.y - 16, go.name + (go.activeInHierarchy ? "" : "  [off]"), c, 13);
            }
        }
    }
}
