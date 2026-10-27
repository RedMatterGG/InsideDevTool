using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace InsideDev
{
    // Line-of-sight and near-plane checks of camera poses (MULTICAM.md 6.2 "View checks"): events-camera candidate choice,
    // replay plan passes (views.csv and run.json `views`), `mcam views` and `mcam show`. Visibility: the camera -> subject
    // segment, stopped ViewSpec.subjectRadius short of the subject, against non-trigger colliders on the view layers (Unity
    // 5.0 raycasts hit triggers, so they are filtered here) and against the bounds of the avoid targets plus the huddle cloth
    // bounds, grown by ViewSpec.margin (a box that holds the subject is the subject and is skipped). Colliders under the
    // subject's own object (for boy / huddle / main: its parent, where the character's bodies live) never count. Inside:
    // the camera point and the four near-plane corners, each inside a collider (overlap) or behind a surface that faces the
    // subject but not the camera (a closed collider around the camera, or a one-sided set wall seen from its back: Unity 5.0
    // rays do not hit back faces or start inside a convex collider), or inside an avoid box. Colliders under objects the camera
    // hides at that moment (`hide`, MultiCamHide) never count. PathHit: the camera's own path between two poses crossing a
    // collider (replay plan passes, views.csv `path_hit`).
    public sealed class ViewResult
    {
        public bool visible = true, inside;
        public string blocker = "", insideBy = "";
        public float blockAt = -1f;
        public Vector3 subject;
        public int hits;

        public string Text()
        {
            var ic = CultureInfo.InvariantCulture;
            return "target visible " + (visible ? "yes" : "no (" + blocker + " at " + blockAt.ToString("0.##", ic) + " m)") +
                   ", near plane inside geometry " + (inside ? "yes (" + insideBy + ")" : "no");
        }
    }

    public static class MultiCamView
    {
        static readonly ViewSpec Defaults = new ViewSpec();
        static TargetRef huddleBounds;

        public static ViewSpec Spec(CamSpec c) { return c != null && c.view != null ? c.view : Defaults; }

        public static int Mask(ViewSpec v)
        {
            if (v == null || v.layers == null || v.layers.Count == 0) return ~(1 << 2);
            int m = 0;
            foreach (int l in v.layers) if (l >= 0 && l < 32) m |= 1 << l;
            return m;
        }

        public static List<Bounds> Avoid(MultiCamContext ctx, ViewSpec v)
        {
            var r = new List<Bounds>();
            if (v == null) return r;
            TargetSample s;
            if (v.huddle)
            {
                if (huddleBounds == null) { huddleBounds = TargetRef.Of("huddle"); huddleBounds.point = "bounds"; huddleBounds.given.Add("point"); }
                if (Sample(ctx, huddleBounds, out s) && s.hasBounds) r.Add(s.bounds);
            }
            if (v.avoid != null)
                foreach (var a in v.avoid)
                    if (a != null && Sample(ctx, a, out s)) r.Add(s.hasBounds ? s.bounds : new Bounds(s.pos, Vector3.one * 0.5f));
            return r;
        }

        static bool Sample(MultiCamContext ctx, TargetRef r, out TargetSample s)
        {
            s = default(TargetSample);
            try { return (ctx != null && ctx.target != null ? ctx.Target(r, out s) : MultiCamTargets.Sample(r, out s)) && s.ok; }
            catch { return false; }
        }

        // The subject's own object: colliders below it never block (a character's bodies sit under its parent).
        public static Transform Own(TargetRef r, GameObject go)
        {
            if (go == null) return null;
            string of = r != null ? (r.of ?? "").Trim() : "";
            bool character = of == "boy" || of == "huddle" || of == "main";
            return character && go.transform.parent != null ? go.transform.parent : go.transform;
        }

        public static ViewResult Check(CamPose p, Vector3 subject, ViewSpec v, List<Bounds> avoid, float aspect, Transform own)
        {
            return Check(p, subject, v, avoid, aspect, own, null);
        }

        public static ViewResult Check(CamPose p, Vector3 subject, ViewSpec v, List<Bounds> avoid, float aspect, Transform own, List<Transform> hidden)
        {
            if (v == null) v = Defaults;
            var res = new ViewResult();
            res.subject = subject;
            int mask = Mask(v);
            var d = subject - p.p;
            float dist = d.magnitude;
            if (dist < 1e-3f) return res;
            var dir = d / dist;
            float reach = Math.Max(0f, dist - Math.Max(0f, v.subjectRadius));
            if (reach > 0f)
            {
                float best = float.MaxValue;
                Collider bc = null;
                foreach (var h in Physics.RaycastAll(p.p, dir, reach, mask))
                {
                    if (!Counts(h.collider, v, own, hidden)) continue;
                    res.hits++;
                    if (h.distance < best) { best = h.distance; bc = h.collider; }
                }
                if (bc != null) { res.visible = false; res.blocker = Name(bc); res.blockAt = best; }
            }
            if (avoid != null)
            {
                var end = p.p + dir * reach;
                foreach (var b in avoid)
                {
                    var e = b.extents + Vector3.one * Math.Max(0f, v.margin);
                    if (MultiCamGeom.InsideBox(p.p, b.center, b.extents) && !res.inside) { res.inside = true; res.insideBy = "bounds " + BoxText(b); }
                    if (MultiCamGeom.InsideBox(subject, b.center, e)) continue;
                    float t;
                    if (!MultiCamGeom.SegmentHitsBox(p.p, end, b.center, e, out t)) continue;
                    float at = t * reach;
                    if (res.visible || at < res.blockAt) { res.visible = false; res.blocker = "bounds " + BoxText(b); res.blockAt = at; }
                }
            }
            if (v.near && !res.inside) Inside(p, subject, v, mask, aspect, own, hidden, res);
            return res;
        }

        public static string PathHit(Vector3 a, Vector3 b, ViewSpec v, Transform own, List<Transform> hidden)
        {
            if (v == null) v = Defaults;
            var d = b - a;
            float len = d.magnitude;
            if (len < 1e-3f) return "";
            int mask = Mask(v);
            var dir = d / len;
            foreach (var h in Physics.RaycastAll(a, dir, len, mask)) if (Counts(h.collider, v, own, hidden)) return Name(h.collider);
            foreach (var h in Physics.RaycastAll(b, -dir, len, mask)) if (Counts(h.collider, v, own, hidden)) return Name(h.collider) + " (from its back)";
            return "";
        }

        static void Inside(CamPose p, Vector3 subject, ViewSpec v, int mask, float aspect, Transform own, List<Transform> hidden, ViewResult res)
        {
            float near = p.near > 0f ? p.near : MultiCamConst.GameNear;
            float tv = Mathf.Tan(Mathf.Clamp(p.fov > 0f ? p.fov : MultiCamConst.GameFov, 1f, 170f) * 0.5f * Mathf.Deg2Rad);
            var f = p.r * Vector3.forward;
            var u = p.r * Vector3.up;
            var r = p.r * Vector3.right;
            float hh = near * tv, hw = hh * (aspect > 0.1f ? aspect : 16f / 9f);
            var c = p.p + f * near;
            var pts = new[] { p.p, c + r * hw + u * hh, c - r * hw + u * hh, c + r * hw - u * hh, c - r * hw - u * hh };
            foreach (var q in pts)
            {
                foreach (var col in Physics.OverlapSphere(q, 0.02f, mask))
                    if (Counts(col, v, own, hidden)) { res.inside = true; res.insideBy = Name(col); return; }
                var d = q - subject;
                float len = d.magnitude;
                if (len < 1e-3f) continue;
                var fromSubject = Physics.RaycastAll(subject, d / len, len, mask);
                if (fromSubject.Length == 0) continue;
                var back = Physics.RaycastAll(q, -d / len, len, mask);
                foreach (var h in fromSubject)
                {
                    if (!Counts(h.collider, v, own, hidden)) continue;
                    bool seen = false;
                    foreach (var b in back) if (b.collider == h.collider) { seen = true; break; }
                    if (!seen) { res.inside = true; res.insideBy = Name(h.collider) + " (its surface faces the subject, not the camera)"; return; }
                }
            }
        }

        static bool Counts(Collider c, ViewSpec v, Transform own, List<Transform> hidden)
        {
            if (c == null || c.isTrigger) return false;
            if (own != null && c.transform.IsChildOf(own)) return false;
            if (hidden != null) foreach (var h in hidden) if (h != null && c.transform.IsChildOf(h)) return false;
            if (v.ignore != null && v.ignore.Count > 0)
            {
                string path = Name(c);
                foreach (var s in v.ignore) if (!string.IsNullOrEmpty(s) && path.IndexOf(s, StringComparison.Ordinal) >= 0) return false;
            }
            return true;
        }

        static string Name(Collider c)
        {
            try { return Inspector.PathOf(c.transform); } catch { return c != null ? c.name : "(collider)"; }
        }

        static string BoxText(Bounds b)
        {
            var ic = CultureInfo.InvariantCulture;
            return "(" + b.center.x.ToString("0.##", ic) + ", " + b.center.y.ToString("0.##", ic) + ", " + b.center.z.ToString("0.##", ic) + ") +-(" +
                   b.extents.x.ToString("0.##", ic) + ", " + b.extents.y.ToString("0.##", ic) + ", " + b.extents.z.ToString("0.##", ic) + ")";
        }
    }
}
