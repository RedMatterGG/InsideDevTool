using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Move an object to another parent, keeping where it is in the world. Useful when the game's hierarchy was changed
    // after an animation was made: legacy animation clips address objects by path (e.g. "CurtainA" under the object
    // that plays the clip), so an object that was later wrapped in an extra group stops following its animation.
    // The original parent is remembered for "restore parent". Recorded in History as action "reparent"
    // (arg = selector of the new parent, "(root)" for none), so it can be kept as a mod that re-applies it on load.
    public static class Reparent
    {
        static readonly Dictionary<int, Transform> original = new Dictionary<int, Transform>();
        static readonly Dictionary<int, string> originalSel = new Dictionary<int, string>();   // where the game has it, for mods

        // the selector of the object at its ORIGINAL place (a mod must find it there when the level loads)
        public static string OriginalSelector(GameObject go) { string s; return go != null && originalSel.TryGetValue(go.GetInstanceID(), out s) ? s : null; }
        public static int pickFor;   // instance id of the object waiting for "pick new parent" (0 = none)

        public static bool Moved(GameObject go) { return go != null && original.ContainsKey(go.GetInstanceID()); }

        public static string Do(GameObject go, Transform parent, string source)
        {
            if (go == null) return "no object";
            var t = go.transform;
            for (var p = parent; p != null; p = p.parent) if (p == t) return "can't put " + go.name + " under its own child";
            if (t.parent == parent) return go.name + " is already there";
            if (!original.ContainsKey(go.GetInstanceID()))
            {
                original[go.GetInstanceID()] = t.parent;
                originalSel[go.GetInstanceID()] = new ObjectSelector { path = Inspector.PathOf(t), name = go.name, hasPos = true, pos = t.position }.ToString();
            }
            t.SetParent(parent, true);
            Rebind(t);
            string sel = parent != null ? ObjectSelector.From(parent.gameObject).ToString() : "(root)";
            ChangeRecorder.Action(go, "reparent", null, sel, source);
            string msg = go.name + " now under " + (parent != null ? parent.name : "(scene root)");
            DevLog.Write("[reparent] " + msg);
            return msg;
        }

        public static string Restore(GameObject go)
        {
            Transform p;
            if (go == null || !original.TryGetValue(go.GetInstanceID(), out p)) return "not moved";
            original.Remove(go.GetInstanceID()); originalSel.Remove(go.GetInstanceID());
            go.transform.SetParent(p, true);
            Rebind(go.transform);
            ChangeRecorder.Action(go, "reparent", null, p != null ? ObjectSelector.From(p.gameObject).ToString() : "(root)", "inspector");
            return go.name + " back under " + (p != null ? p.name : "(scene root)");
        }

        // Legacy Animation components bind their clip paths once (INSIDE pre-builds them at load through
        // Animation.RebuildStateForEverythingIncremental, see AnimationPreAwake). After a move they still point at the
        // old place, so every Animation above the object rebuilds its bindings here, the same way the game does it.
        public static int Rebind(Transform t)
        {
            int n = 0;
            for (var p = t.parent; p != null; p = p.parent)
                foreach (var an in p.GetComponents<Animation>())
                    if (an != null) { RebindAnimation(an); n++; }
            return n;
        }

        // Rebuilding alone keeps the old bindings (measured: the curtains stayed on frame 0 while the clip played).
        // Removing and re-adding each clip makes Unity create fresh states that resolve the paths again; the
        // playing state (time / weight / enabled) is carried over.
        public static void RebindAnimation(Animation an)
        {
            try
            {
                var states = new List<AnimationState>();
                foreach (AnimationState st in an) states.Add(st);
                var def = an.clip;
                foreach (var st in states)
                {
                    var clip = st.clip; string name = st.name;
                    if (clip == null) continue;
                    bool en = st.enabled; float time = st.time, w = st.weight, sp = st.speed; var wm = st.wrapMode;
                    an.RemoveClip(name);
                    an.AddClip(clip, name);
                    var ns = an[name];
                    if (ns != null) { ns.wrapMode = wm; ns.speed = sp; ns.time = time; ns.weight = w; ns.enabled = en; }
                }
                if (def != null) an.clip = def;
                for (int i = 0; i < 100000; i++) if (an.RebuildStateForEverythingIncremental(i)) break;
            }
            catch (System.Exception e) { DevLog.Write("[reparent] rebuild " + an.name + ": " + e.Message); }
        }

        // mod form: arg = selector of the new parent or "(root)"
        public static string Apply(GameObject go, string arg, string source)
        {
            if (arg == "(root)") return Do(go, null, source);
            var l = Mods.ResolveAll(arg);
            if (l.Count == 0) return "new parent not loaded: " + arg;
            return Do(go, l[0].transform, source);
        }
    }
}
