using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Walking a legacy Animation's states (foreach AnimationState / GetClipCount / this[name]) makes the engine build
    // its state list (Animation::RebuildStateForEverythingIncremental). On an Animation whose GameObject has never been
    // active - a level loaded but not yet activated by streaming or by the level sweep, or a prefab asset - that native
    // state does not exist and Unity 5.0 dereferences null (hard crash, 2026-09-28). Only walk states on active objects;
    // otherwise read nothing from the Animation at all.
    public static class AnimSafe
    {
        public static bool CanWalkStates(Animation an) { return an != null && an.gameObject.activeInHierarchy; }

        public static void Clips(Animation an, List<AnimationClip> into)
        {
            if (an == null) return;
            // NO Animation call of any kind on an inactive object: even the default-clip getter rebuilds the state
            // list in this engine build and crashed the second time (2026-09-28 23:51)
            if (CanWalkStates(an)) { foreach (AnimationState st in an) if (st != null && st.clip != null) into.Add(st.clip); }
        }
    }
}
