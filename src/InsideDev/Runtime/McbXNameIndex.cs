using System;
using System.Collections.Generic;

namespace InsideDev
{
    // Object name -> records of the object database snapshot, for the strict target lookup of MultiCamTargets: an exact
    // name or a path suffix is answered from the handful of objects with that name instead of a walk over every record.
    // The key is the last segment of the record's path (its name, unless the name itself holds a '/'). Rebuilt on the first
    // lookup after a database pass completed; the lists are kept between rebuilds, so a rebuild allocates next to nothing.
    // Main thread.
    internal static class McbXNameIndex
    {
        static readonly Dictionary<string, List<ObjRecord>> bySegment = new Dictionary<string, List<ObjRecord>>();
        static readonly List<ObjRecord> none = new List<ObjRecord>(0);
        static int builtPass = -1;

        // Records whose path ends with this segment. The caller still tests the full name or path and that the object lives.
        public static List<ObjRecord> Named(string segment)
        {
            if (builtPass != ObjectDatabase.completedPasses) Build();
            List<ObjRecord> l;
            return segment != null && bySegment.TryGetValue(segment, out l) ? l : none;
        }

        static void Build()
        {
            builtPass = ObjectDatabase.completedPasses;
            foreach (var l in bySegment.Values) l.Clear();
            var all = ObjectDatabase.all;
            for (int i = 0; i < all.Count; i++)
            {
                var rec = all[i];
                string key = Segment(rec);
                if (key == null) continue;
                List<ObjRecord> l;
                if (!bySegment.TryGetValue(key, out l)) bySegment[key] = l = new List<ObjRecord>(1);
                l.Add(rec);
            }
        }

        static string Segment(ObjRecord rec)
        {
            string name = rec.name, path = rec.path;
            if (path == null) return name;
            if (name != null && name.IndexOf('/') < 0)
            {
                int at = path.Length - name.Length;
                if (at == 0 ? path == name : at > 0 && path[at - 1] == '/' && path.EndsWith(name, StringComparison.Ordinal)) return name;
            }
            int k = path.LastIndexOf('/');
            return k < 0 ? path : path.Substring(k + 1);
        }
    }
}
