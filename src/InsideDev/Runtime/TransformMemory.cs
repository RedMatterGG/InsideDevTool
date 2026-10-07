using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Remembers an object's original transform the first time the editor changes it, so it can be restored.
    public static class TransformMemory
    {
        public struct Orig { public Transform t; public Vector3 pos, euler, scale; }
        static readonly Dictionary<int, Orig> orig = new Dictionary<int, Orig>();

        public static void Remember(Transform t)
        {
            if (t == null) return;
            int id = t.GetInstanceID();
            if (!orig.ContainsKey(id)) orig[id] = new Orig { t = t, pos = t.position, euler = t.localEulerAngles, scale = t.localScale };
        }

        public static bool Has(Transform t) { return t != null && orig.ContainsKey(t.GetInstanceID()); }

        public static bool Restore(Transform t)
        {
            Orig o;
            if (t == null || !orig.TryGetValue(t.GetInstanceID(), out o)) return false;
            RecordAll(t);
            t.position = o.pos; t.localEulerAngles = o.euler; t.localScale = o.scale;
            orig.Remove(t.GetInstanceID());
            DevLog.Write("transform restored: " + t.name);
            return true;
        }

        public static int RestoreAll()
        {
            int n = 0;
            foreach (var o in new List<Orig>(orig.Values)) if (o.t != null) { RecordAll(o.t); o.t.position = o.pos; o.t.localEulerAngles = o.euler; o.t.localScale = o.scale; n++; }
            orig.Clear();
            DevLog.Write("transforms restored: " + n);
            return n;
        }

        static void RecordAll(Transform t)
        {
            ChangeRecorder.Before(t.gameObject, ChangeRecorder.PropKind.Position, "restore");
            ChangeRecorder.Before(t.gameObject, ChangeRecorder.PropKind.LocalEuler, "restore");
            ChangeRecorder.Before(t.gameObject, ChangeRecorder.PropKind.LocalScale, "restore");
        }

        public static int Count { get { return orig.Count; } }
    }
}
