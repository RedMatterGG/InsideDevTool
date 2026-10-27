using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Per-camera and per-shot `hide` that also works in replay passes (MULTICAM.md 6.4). The replayer owns Renderer.enabled
    // (per-camera culling emulation), so hiding never touches it: the GameObjects of the hidden renderers move to a spare layer
    // that no game object uses (31 unless something uses it) and the camera's cullingMask drops that layer. The game's
    // velocity camera copies the main camera's mask (PDVelocityBuffer.OnPreCull CopyFrom), the explicit velocity buffer draws
    // VelocityBufferTag meshes regardless of layers. Layers and the mask go back exactly (the mask: the saved value when nothing
    // else changed it, else only the spare bit is put back) when the set changes, at the pass end, on abort and at the session
    // pop; `mcam show` applies the camera's set to the free camera until the next show, `cam off` or an mcam run.
    // Sets: the camera's own `hide`; a director adds the shot on air (its `hide` and its source camera's `hide`) and, inside a
    // blend (pose flag B), the previous shot's.
    // A name `<path> box=x0,y0,z0,x1,y1,z1[;x0,y0,z0,x1,y1,z1 ...]` hides only the triangles of the object's meshes whose three
    // vertices lie inside one of those world boxes (a prop baked into a level batch mesh, e.g. the CEO office floor lamp): the
    // MeshFilter shows a cut copy of its mesh while the set is applied and gets the game's mesh back on restore. All box entries
    // of a set that hit the same MeshFilter make one cut copy with all their boxes (HideBoxes).
    // `backfaces` names (MultiCamShots.BackfaceMark prefix, MULTICAM.md 6.5) swap the meshes of the object's MeshFilters and
    // SkinnedMeshRenderers (box: only the triangles inside it, tested at the rest pose through the renderer's transform) to a
    // two-sided copy (MeshFlip) the same way, so single-sided set meshes and hollow slabs also draw from behind. Same renderer,
    // materials, layer and property blocks; no new objects; copies are cached per renderer and box; a box hide on the same
    // MeshFilter wins. Nothing of this runs in the live game: live visits use only `renderers`.
    public sealed class HideSet
    {
        public string key = "";
        public readonly List<string> names = new List<string>();
        public readonly List<string> paths = new List<string>();
        public readonly List<Renderer> renderers = new List<Renderer>();
        public readonly List<Transform> roots = new List<Transform>();
        public readonly List<string> unresolved = new List<string>();
        public readonly List<MeshCut> cuts = new List<MeshCut>();
        public readonly List<MeshCut> flips = new List<MeshCut>();

        public bool Empty { get { return names.Count == 0; } }
    }

    public sealed class MeshCut
    {
        public MeshFilter filter;
        public SkinnedMeshRenderer skin;
        public Mesh original;
        public Mesh cut;
        public int removed;
        public int total;
        public bool flip;
        public string path = "";
        public string box = "";
        public readonly List<string> boxes = new List<string>();

        public bool Alive { get { return (filter != null || skin != null) && cut != null; } }

        public Mesh Current { get { return filter != null ? filter.sharedMesh : skin != null ? skin.sharedMesh : null; } }

        public void Set(Mesh m)
        {
            if (filter != null) filter.sharedMesh = m;
            else if (skin != null) skin.sharedMesh = m;
        }
    }

    public static class MultiCamHide
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly int[] Spare = { 31, 26, 25, 24, 19, 9, 7, 6, 3 };
        sealed class Entry { public GameObject go; public int layer; }
        static readonly List<Entry> applied = new List<Entry>();
        static readonly List<MeshCut> cutApplied = new List<MeshCut>();
        static readonly Dictionary<string, HideSet> cache = new Dictionary<string, HideSet>();
        static readonly Dictionary<string, MeshCut> cutCache = new Dictionary<string, MeshCut>();
        static Camera maskCam;
        static int maskBefore, maskOurs, layer = -1, cacheStream = -1;
        static string appliedKey = "";
        public static string owner = "";
        public static string layerNote = "";
        public static int restores;

        public static int Layer { get { return layer; } }
        public static bool Active { get { return applied.Count > 0 || cutApplied.Count > 0 || maskCam != null; } }
        public static string AppliedKey { get { return appliedKey; } }

        public static void ClearCache() { cache.Clear(); cacheStream = -1; }

        // `<path> box=x0,y0,z0,x1,y1,z1[;...]` -> the path and its world boxes (HideBoxes.Split); false for a plain path or a bad box.
        public static bool SplitBox(string name, out string path, out List<Vector3> mins, out List<Vector3> maxs, out string error)
        {
            return HideBoxes.Split(name, out path, out mins, out maxs, out error);
        }

        sealed class BoxGroup
        {
            public Component owner;
            public bool all;
            public readonly List<Vector3> mins = new List<Vector3>(), maxs = new List<Vector3>();
            public readonly List<string> names = new List<string>();
            public readonly List<int[]> at = new List<int[]>();
        }

        // Resolves the names (strict object paths, MultiCamTargets.ResolveObject) to their renderers, once per name list and
        // stream version; unresolved names are listed, never fatal. Box entries that hit the same MeshFilter (hide) or renderer
        // (backfaces) are merged into one copy with all their boxes.
        public static HideSet Resolve(List<string> names, string where, SpecReport report)
        {
            if (cacheStream != Levels.StreamVersion) { cache.Clear(); cacheStream = Levels.StreamVersion; }
            string key = MultiCamShots.HideKey(names);
            HideSet set;
            if (cache.TryGetValue(key, out set)) return set;
            set = new HideSet { key = key };
            var seen = new HashSet<Renderer>();
            var cutGroups = new List<BoxGroup>();
            var flipGroups = new List<BoxGroup>();
            foreach (var n in names)
            {
                if (string.IsNullOrEmpty(n) || set.names.Contains(n)) continue;
                set.names.Add(n);
                bool flip = n.StartsWith(MultiCamShots.BackfaceMark, StringComparison.Ordinal);
                string name = flip ? n.Substring(MultiCamShots.BackfaceMark.Length) : n;
                string what = flip ? "backfaces: " : "hide: ";
                string path, boxError;
                List<Vector3> mins, maxs;
                bool box = SplitBox(name, out path, out mins, out maxs, out boxError);
                if (!box && boxError.Length > 0)
                {
                    set.unresolved.Add(n);
                    if (report != null) report.Warn(where, what + boxError + ": " + name);
                    continue;
                }
                var rep = new SpecReport();
                GameObject go = null;
                try { go = MultiCamTargets.ResolveObject(path, where, rep); } catch (Exception e) { rep.Error(where, e.GetType().Name + ": " + e.Message); }
                if (go == null)
                {
                    set.unresolved.Add(n);
                    if (report != null) report.Warn(where, what + "not resolved: " + name + (rep.errors.Count > 0 ? " (" + rep.errors[0] + ")" : ""));
                    continue;
                }
                if (flip)
                {
                    int owners = 0;
                    foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true)) owners += Group(flipGroups, mf, n, box, mins, maxs);
                    foreach (var sk in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) owners += Group(flipGroups, sk, n, box, mins, maxs);
                    if (owners == 0) { set.unresolved.Add(n); if (report != null) report.Warn(where, "backfaces: nothing to flip: " + name + " (no MeshFilter or SkinnedMeshRenderer)"); }
                    continue;
                }
                if (box)
                {
                    int owners = 0;
                    foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true)) owners += Group(cutGroups, mf, n, true, mins, maxs);
                    if (owners == 0) { set.unresolved.Add(n); if (report != null) report.Warn(where, "hide: box cut nothing: " + name + " (no MeshFilter)"); }
                    continue;
                }
                set.roots.Add(go.transform);
                set.paths.Add(Inspector.PathOf(go.transform));
                foreach (var r in go.GetComponentsInChildren<Renderer>(true)) if (r != null && seen.Add(r)) set.renderers.Add(r);
            }
            Build(set, cutGroups, false, where, report);
            Build(set, flipGroups, true, where, report);
            foreach (var f in set.flips)
                foreach (var c in set.cuts)
                    if (f.filter != null && f.filter == c.filter && report != null) report.Warn(where, "backfaces: " + f.path + " is box-hidden in the same set; the hide wins");
            cache[key] = set;
            return set;
        }

        static int Group(List<BoxGroup> groups, Component owner, string name, bool box, List<Vector3> mins, List<Vector3> maxs)
        {
            if (owner == null) return 0;
            BoxGroup g = null;
            foreach (var x in groups) if (x.owner == owner) { g = x; break; }
            if (g == null) { g = new BoxGroup { owner = owner }; groups.Add(g); }
            g.names.Add(name);
            if (!box) { g.all = true; g.at.Add(new int[0]); return 1; }
            g.at.Add(HideBoxes.Merge(g.mins, g.maxs, mins, maxs));
            return 1;
        }

        // One cut / two-sided copy per group; a name none of whose boxes changed a triangle on any of its renderers is listed
        // as unresolved with a warning.
        static void Build(HideSet set, List<BoxGroup> groups, bool flip, string where, SpecReport report)
        {
            var hit = new Dictionary<string, bool>();
            var why = new Dictionary<string, string>();
            foreach (var g in groups)
            {
                string w = "";
                int[] perBox;
                var c = flip ? Flip(g.owner as MeshFilter, g.owner as SkinnedMeshRenderer, g.all ? null : g.mins, g.maxs, out perBox, ref w)
                             : Cut(g.owner as MeshFilter, g.mins, g.maxs, out perBox, ref w);
                if (c != null && !(flip ? set.flips : set.cuts).Contains(c)) (flip ? set.flips : set.cuts).Add(c);
                for (int i = 0; i < g.names.Count; i++)
                {
                    string n = g.names[i];
                    bool any = c != null && (g.at[i].Length == 0 || g.all);
                    if (c != null && perBox != null) foreach (int k in g.at[i]) if (k >= 0 && k < perBox.Length && perBox[k] > 0) any = true;
                    bool before;
                    hit[n] = (hit.TryGetValue(n, out before) && before) || any;
                    if (!any && w.Length > 0) why[n] = w;
                }
            }
            foreach (var kv in hit)
            {
                if (kv.Value) continue;
                set.unresolved.Add(kv.Key);
                string w;
                why.TryGetValue(kv.Key, out w);
                string shown = flip && kv.Key.StartsWith(MultiCamShots.BackfaceMark, StringComparison.Ordinal) ? kv.Key.Substring(MultiCamShots.BackfaceMark.Length) : kv.Key;
                if (report != null) report.Warn(where, (flip ? "backfaces: nothing to flip: " : "hide: box cut nothing: ") + shown + (!string.IsNullOrEmpty(w) ? " (" + w + ")" : ""));
            }
        }

        public static string BoxText(Vector3 min, Vector3 max) { return HideBoxes.Text(min, max); }

        public static string BoxText(List<Vector3> mins, List<Vector3> maxs) { return HideBoxes.Text(mins, maxs); }

        // A copy of the filter's mesh without the triangles whose three vertices lie inside one of the world boxes (cached per
        // filter and sorted boxes); perBox = the triangles inside each box; null when the mesh is missing, not readable or has no
        // triangle in any box.
        static MeshCut Cut(MeshFilter mf, List<Vector3> mins, List<Vector3> maxs, out int[] perBox, ref string why)
        {
            perBox = null;
            if (mf == null) return null;
            var m = mf.sharedMesh;
            string boxText = BoxText(mins, maxs);
            string ck = mf.GetInstanceID().ToString(IC) + "|" + HideBoxes.Key(mins, maxs);
            MeshCut c;
            if (cutCache.TryGetValue(ck, out c) && c.Alive && (m == c.original || m == c.cut)) { perBox = PerBox(c, mins, maxs); return c; }
            if (m == null) { why = "no mesh"; return null; }
            Vector3[] v;
            try { v = m.vertices; } catch (Exception e) { why = "mesh not readable: " + e.GetType().Name; return null; }
            if (v == null || v.Length == 0) { why = "mesh " + m.name + " not readable (no vertices)"; return null; }
            var tr = mf.transform;
            var mask = new int[v.Length];
            for (int i = 0; i < v.Length; i++) mask[i] = HideBoxes.Mask(tr.TransformPoint(v[i]), mins, maxs);
            perBox = new int[mins.Count];
            var copy = (Mesh)UnityEngine.Object.Instantiate(m);
            copy.name = m.name + " (mcam hide box)";
            int removed = 0, total = 0;
            for (int s = 0; s < m.subMeshCount; s++) copy.SetTriangles(HideBoxes.Keep(m.GetTriangles(s), mask, perBox, ref removed, ref total), s);
            if (removed == 0)
            {
                UnityEngine.Object.Destroy(copy);
                why = "no triangle of " + m.name + " (" + total.ToString(IC) + ") inside " + boxText;
                return null;
            }
            c = new MeshCut { filter = mf, original = m, cut = copy, removed = removed, total = total, path = Inspector.PathOf(mf.transform), box = boxText };
            c.boxes.AddRange(BoxTexts(mins, maxs, perBox));
            cutCache[ck] = c;
            return c;
        }

        static List<string> BoxTexts(List<Vector3> mins, List<Vector3> maxs, int[] perBox)
        {
            var l = new List<string>();
            for (int i = 0; i < mins.Count; i++) l.Add(BoxText(mins[i], maxs[i]) + "=" + (perBox != null && i < perBox.Length ? perBox[i] : 0).ToString(IC));
            return l;
        }

        static int[] PerBox(MeshCut c, List<Vector3> mins, List<Vector3> maxs)
        {
            var r = new int[mins.Count];
            for (int i = 0; i < mins.Count; i++)
            {
                string s = BoxText(mins[i], maxs[i]) + "=";
                foreach (var b in c.boxes)
                {
                    int k;
                    if (b.StartsWith(s, StringComparison.Ordinal) && int.TryParse(b.Substring(s.Length), NumberStyles.Integer, IC, out k)) r[i] = k;
                }
            }
            return r;
        }

        // A two-sided copy of the renderer's mesh (MeshFlip; mins null = every triangle, else only those inside one of the
        // boxes), cached per renderer and sorted boxes; null when the mesh is missing, not readable, has no triangle to flip or
        // would pass MeshFlip.MaxVertices.
        static MeshCut Flip(MeshFilter mf, SkinnedMeshRenderer sk, List<Vector3> mins, List<Vector3> maxs, out int[] perBox, ref string why)
        {
            perBox = null;
            Component owner = mf != null ? (Component)mf : sk;
            if (owner == null) return null;
            var m = mf != null ? mf.sharedMesh : sk.sharedMesh;
            bool box = mins != null && mins.Count > 0;
            string boxText = box ? BoxText(mins, maxs) : "";
            string ck = "flip|" + owner.GetInstanceID().ToString(IC) + "|" + (box ? HideBoxes.Key(mins, maxs) : "");
            MeshCut c;
            if (cutCache.TryGetValue(ck, out c) && c.Alive && (m == c.original || m == c.cut)) { perBox = box ? PerBox(c, mins, maxs) : null; return c; }
            if (m == null) { why = "no mesh"; return null; }
            Vector3[] v;
            try { v = m.vertices; } catch (Exception e) { why = "mesh not readable: " + e.GetType().Name; return null; }
            if (v == null || v.Length == 0) { why = "mesh " + m.name + " not readable (no vertices)"; return null; }
            int[] mask = null;
            var sub = new int[m.subMeshCount][];
            int total = 0;
            for (int s = 0; s < sub.Length; s++) { sub[s] = m.GetTriangles(s); total += sub[s].Length / 3; }
            if (box)
            {
                var tr = owner.transform;
                mask = new int[v.Length];
                for (int i = 0; i < v.Length; i++) mask[i] = HideBoxes.Mask(tr.TransformPoint(v[i]), mins, maxs);
                perBox = new int[mins.Count];
                int r = 0, t = 0;
                foreach (var st in sub) HideBoxes.Keep(st, mask, perBox, ref r, ref t);
            }
            int[] dupOf;
            int[][] tris;
            int added = MeshFlip.Build(sub, mask, v.Length, out dupOf, out tris);
            if (added == 0) { why = "no triangle of " + m.name + " (" + total.ToString(IC) + ")" + (box ? " inside " + boxText : ""); return null; }
            int n = v.Length + dupOf.Length;
            if (n > MeshFlip.MaxVertices) { why = m.name + " would get " + n.ToString(IC) + " vertices (max " + MeshFlip.MaxVertices.ToString(IC) + "); narrow it with a box"; return null; }
            var copy = (Mesh)UnityEngine.Object.Instantiate(m);
            copy.name = m.name + " (mcam backfaces)";
            try
            {
                var normals = MeshFlip.BackNormals(MeshFlip.Extend(m.normals, dupOf, v.Length), v.Length);
                var tangents = MeshFlip.BackTangents(MeshFlip.Extend(m.tangents, dupOf, v.Length), v.Length);
                var uv = MeshFlip.Extend(m.uv, dupOf, v.Length);
                var uv2 = MeshFlip.Extend(m.uv2, dupOf, v.Length);
                var uv3 = MeshFlip.Extend(m.uv3, dupOf, v.Length);
                var uv4 = MeshFlip.Extend(m.uv4, dupOf, v.Length);
                var colors = MeshFlip.Extend(m.colors32, dupOf, v.Length);
                var weights = MeshFlip.Extend(m.boneWeights, dupOf, v.Length);
                var bind = m.bindposes;
                var bounds = m.bounds;
                copy.Clear(true);
                copy.vertices = MeshFlip.Extend(v, dupOf, v.Length);
                if (normals != null && normals.Length == n) copy.normals = normals;
                if (tangents != null && tangents.Length == n) copy.tangents = tangents;
                if (uv != null && uv.Length == n) copy.uv = uv;
                if (uv2 != null && uv2.Length == n) copy.uv2 = uv2;
                if (uv3 != null && uv3.Length == n) copy.uv3 = uv3;
                if (uv4 != null && uv4.Length == n) copy.uv4 = uv4;
                if (colors != null && colors.Length == n) copy.colors32 = colors;
                if (weights != null && weights.Length == n) copy.boneWeights = weights;
                if (bind != null && bind.Length > 0) copy.bindposes = bind;
                copy.subMeshCount = tris.Length;
                for (int s = 0; s < tris.Length; s++) copy.SetTriangles(tris[s], s);
                copy.bounds = bounds;
            }
            catch (Exception e)
            {
                UnityEngine.Object.Destroy(copy);
                why = "copy of " + m.name + " failed: " + e.GetType().Name + ": " + e.Message;
                return null;
            }
            c = new MeshCut { filter = mf, skin = sk, original = m, cut = copy, removed = added, total = total, flip = true, path = Inspector.PathOf(owner.transform), box = boxText };
            if (box) c.boxes.AddRange(BoxTexts(mins, maxs, perBox));
            cutCache[ck] = c;
            return c;
        }

        // A spare layer: unnamed and on no active renderer; 31 first.
        public static int PickLayer()
        {
            var used = new HashSet<int>();
            try { foreach (var o in UnityEngine.Object.FindObjectsOfType(typeof(Renderer))) { var r = o as Renderer; if (r != null) used.Add(r.gameObject.layer); } }
            catch (Exception e) { DevLog.Error("mcam hide layer scan", e); }
            foreach (int l in Spare)
            {
                string name = "";
                try { name = LayerMask.LayerToName(l); } catch { name = ""; }
                if (!string.IsNullOrEmpty(name) || used.Contains(l)) continue;
                layer = l;
                layerNote = "layer " + l.ToString(IC) + " (unnamed, no active renderer)";
                return l;
            }
            layer = 31;
            layerNote = "layer 31 (no free layer found: renderers already on it are hidden too)";
            return layer;
        }

        // Applies the set to the camera (restores the previous set first when it differs); the same set on the same camera only
        // re-asserts the mask bit and the cut meshes.
        public static void Apply(Camera cam, HideSet set, string who)
        {
            if (set == null || set.Empty || cam == null) { Restore(); return; }
            if (Active && set.key == appliedKey && cam == maskCam)
            {
                if (layer >= 0 && (cam.cullingMask & (1 << layer)) != 0) { cam.cullingMask &= ~(1 << layer); maskOurs = cam.cullingMask; }
                foreach (var c in cutApplied) if (c.Alive && c.Current == c.original) c.Set(c.cut);
                return;
            }
            Restore();
            if (layer < 0) PickLayer();
            owner = who ?? "";
            appliedKey = set.key;
            maskCam = cam;
            maskBefore = cam.cullingMask;
            cam.cullingMask = maskBefore & ~(1 << layer);
            maskOurs = cam.cullingMask;
            var done = new HashSet<GameObject>();
            foreach (var r in set.renderers)
            {
                if (r == null) continue;
                var go = r.gameObject;
                if (go == null || !done.Add(go) || go.layer == layer) continue;
                applied.Add(new Entry { go = go, layer = go.layer });
                go.layer = layer;
            }
            foreach (var l in new[] { set.cuts, set.flips })
                foreach (var c in l)
                {
                    if (!c.Alive || c.Current != c.original) continue;
                    c.Set(c.cut);
                    cutApplied.Add(c);
                }
        }

        public static void Restore()
        {
            if (!Active) return;
            for (int i = applied.Count - 1; i >= 0; i--)
            {
                var e = applied[i];
                try { if (e.go != null && e.go.layer == layer) e.go.layer = e.layer; } catch { }
            }
            applied.Clear();
            for (int i = cutApplied.Count - 1; i >= 0; i--)
            {
                var c = cutApplied[i];
                try { if (c.Alive && c.Current == c.cut) c.Set(c.original); } catch { }
            }
            cutApplied.Clear();
            try
            {
                if (maskCam != null && layer >= 0)
                {
                    int cur = maskCam.cullingMask;
                    maskCam.cullingMask = cur == maskOurs ? maskBefore : cur | (maskBefore & (1 << layer));
                }
            }
            catch { }
            maskCam = null;
            appliedKey = "";
            owner = "";
            restores++;
        }

        public static void EndPreview()
        {
            if (Active && owner.StartsWith("show", StringComparison.Ordinal)) Restore();
        }

        public static List<string> FlipPaths(HideSet set)
        {
            var r = new List<string>();
            if (set == null) return r;
            foreach (var c in set.flips) if (!r.Contains(c.path)) r.Add(c.path);
            return r;
        }

        static string BoxesText(MeshCut c)
        {
            return c.boxes.Count > 1 ? string.Join(" + ", c.boxes.ToArray()) + " (triangles per box)" : c.box;
        }

        public static string Text(HideSet set)
        {
            if (set == null || set.Empty) return "none";
            var sb = new StringBuilder();
            sb.Append(set.renderers.Count).Append(" renderer(s) of ").Append(string.Join(", ", set.paths.ToArray()));
            foreach (var c in set.cuts)
                sb.Append("; box cut ").Append(c.removed.ToString(IC)).Append(" of ").Append(c.total.ToString(IC)).Append(" triangle(s) of ").Append(c.path).Append(" in ").Append(BoxesText(c));
            foreach (var c in set.flips)
                sb.Append("; backfaces ").Append(c.removed.ToString(IC)).Append(" of ").Append(c.total.ToString(IC)).Append(" triangle(s) of ").Append(c.path).Append(c.box.Length > 0 ? " in " + BoxesText(c) : "");
            if (set.unresolved.Count > 0) sb.Append("; not resolved: ").Append(string.Join(", ", set.unresolved.ToArray()));
            return sb.ToString();
        }
    }
}
