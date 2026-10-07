using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Compatibility view over ChangeRecorder for the older on/off call sites and the "changed by you" list in the
    // Hidden tab. Every change still lands in the History (undo/redo/revert, Record as mod).
    public static class Changes
    {
        public class Change
        {
            public GameObject go;
            public Component comp;      // null = GameObject active flag
            public string what;
            public bool original;
            public ChangeRecorder.Net net;
        }

        public static readonly Color Purple = new Color(0.78f, 0.45f, 1f, 1f);

        // net on/off changes (active / enabled), rebuilt from the history when it changes
        static List<Change> cache = new List<Change>();
        static int cacheVersion = -1; static float cacheAt;
        public static List<Change> list
        {
            get
            {
                if (cacheVersion != ChangeRecorder.Version || Time.realtimeSinceStartup - cacheAt > 0.5f)
                {
                    cacheVersion = ChangeRecorder.Version; cacheAt = Time.realtimeSinceStartup;
                    var l = new List<Change>();
                    foreach (var n in ChangeRecorder.NetChanges())
                    {
                        if (n.prop.kind != ChangeRecorder.PropKind.Active && n.prop.kind != ChangeRecorder.PropKind.Enabled) continue;
                        string what = n.prop.kind == ChangeRecorder.PropKind.Active ? "active" : n.prop.comp is Collider ? (((Collider)n.prop.comp).isTrigger ? "trigger" : "collider") : n.prop.comp is Renderer ? "renderer" : n.prop.compType;
                        l.Insert(0, new Change { go = n.prop.go, comp = n.prop.comp, what = what, original = n.original is bool && (bool)n.original, net = n });
                    }
                    cache = l;
                }
                return cache;
            }
        }

        // call BEFORE applying the new value (the new value is read back when the history entry commits)
        public static void Record(GameObject go, Component c, string what, bool currentValue)
        {
            if (go == null) return;
            if (c == null) ChangeRecorder.Before(go, ChangeRecorder.PropKind.Active, "editor");
            else ChangeRecorder.BeforeEnabled(c, "editor");
        }

        public static bool Current(Change ch)
        {
            if (ch.go == null) return false;
            if (ch.comp == null) return ch.go.activeSelf;
            var v = ChangeRecorder.GetEnabled(ch.comp);
            return v is bool && (bool)v;
        }

        public static void Set(Change ch, bool v)
        {
            if (ch.go == null) return;
            if (ch.comp == null) ChangeRecorder.SetActive(ch.go, v, "editor");
            else ChangeRecorder.SetEnabledRecorded(ch.comp, v, "editor");
        }

        public static void Revert(Change ch) { DevLog.Write(ChangeRecorder.Revert(ch.net)); cacheVersion = -1; }
        public static void RevertAll() { DevLog.Write(ChangeRecorder.RevertAll()); cacheVersion = -1; }

        public static bool IsChanged(GameObject go)
        {
            foreach (var x in list) if (x.go == go) return true;
            return false;
        }
    }
}
