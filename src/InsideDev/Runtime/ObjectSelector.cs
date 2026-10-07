using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace InsideDev
{
    public enum Resolution { Exact, UniqueFallback, Ambiguous, NotFound }

    // Persistent reference to a GameObject (and optionally one of its components) that survives reloads.
    // Never contains instance ids. Resolution never silently picks one of several candidates.
    //   scene      root object name (INSIDE streams each subscene under its own root)
    //   path       full hierarchy path including the root
    //   component  optional component type name
    //   pos        optional approximate world position, used only to break ties
    public sealed class ObjectSelector
    {
        public string scene, path, name, component;
        public bool hasPos;
        public Vector3 pos;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static ObjectSelector From(ObjRecord r, string component = null)
        {
            return new ObjectSelector { scene = r.scene, path = r.path, name = r.name, component = component, hasPos = true, pos = r.pos };
        }

        public static ObjectSelector From(GameObject g, string component = null)
        {
            var r = ObjectDatabase.Get(g);
            if (r != null && r.path != null) return From(r, component);
            var t = g.transform;
            return new ObjectSelector { scene = t.root.name, path = Inspector.PathOf(t), name = g.name, component = component, hasPos = true, pos = t.position };
        }

        public override string ToString()
        {
            return path + (component != null ? "::" + component : "") +
                   (hasPos ? string.Format(IC, " @{0:0.##},{1:0.##},{2:0.##}", pos.x, pos.y, pos.z) : "");
        }

        // inverse of ToString: "root/a/b[::Component][ @x,y,z]"
        public static ObjectSelector Parse(string s)
        {
            s = s.Trim();
            var sel = new ObjectSelector();
            int at = s.LastIndexOf(" @", StringComparison.Ordinal);
            if (at > 0)
            {
                var p = s.Substring(at + 2).Split(',');
                float x, y, z;
                if (p.Length == 3 && float.TryParse(p[0], NumberStyles.Float, IC, out x) && float.TryParse(p[1], NumberStyles.Float, IC, out y) && float.TryParse(p[2], NumberStyles.Float, IC, out z))
                { sel.hasPos = true; sel.pos = new Vector3(x, y, z); }
                s = s.Substring(0, at);
            }
            int cc = s.IndexOf("::", StringComparison.Ordinal);
            if (cc > 0) { sel.component = s.Substring(cc + 2); s = s.Substring(0, cc); }
            sel.path = s;
            int slash = s.IndexOf('/');
            sel.scene = slash > 0 ? s.Substring(0, slash) : s;
            sel.name = s.Substring(s.LastIndexOf('/') + 1);
            return sel;
        }

        bool CompOk(ObjRecord r) { return component == null || r.Has(component); }

        public Resolution Resolve(out ObjRecord match, out List<ObjRecord> candidates)
        {
            match = null;
            candidates = new List<ObjRecord>();
            ObjectDatabase.EnsureFresh(5f);
            // 1) exact path (+ component)
            foreach (var r in ObjectDatabase.ByPath(path)) if (CompOk(r)) candidates.Add(r);
            if (candidates.Count == 1) { match = candidates[0]; return Resolution.Exact; }
            if (candidates.Count > 1) return TieBreak(candidates, out match);
            // 2) fallback: same name (+ component) in the same scene root, then anywhere
            foreach (var r in ObjectDatabase.all) if (r.go != null && r.name == name && r.scene == scene && CompOk(r)) candidates.Add(r);
            if (candidates.Count == 0) foreach (var r in ObjectDatabase.all) if (r.go != null && r.name == name && CompOk(r)) candidates.Add(r);
            if (candidates.Count == 0) return Resolution.NotFound;
            if (candidates.Count == 1) { match = candidates[0]; return Resolution.UniqueFallback; }
            var res = TieBreak(candidates, out match);
            return res == Resolution.Exact ? Resolution.UniqueFallback : res;
        }

        // several candidates: accept only if exactly one lies within 0.5 m of the recorded position
        Resolution TieBreak(List<ObjRecord> c, out ObjRecord match)
        {
            match = null;
            if (!hasPos) return Resolution.Ambiguous;
            ObjRecord best = null; int near = 0;
            foreach (var r in c) if ((r.pos - pos).sqrMagnitude < 0.25f) { near++; best = r; }
            if (near == 1) { match = best; return Resolution.UniqueFallback; }
            return Resolution.Ambiguous;
        }

        public GameObject ResolveObject(out Resolution res)
        {
            ObjRecord m; List<ObjRecord> c;
            res = Resolve(out m, out c);
            return m != null ? m.go : null;
        }
    }
}
