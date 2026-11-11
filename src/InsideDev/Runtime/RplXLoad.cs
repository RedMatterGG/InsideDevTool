using System;
using System.Reflection;
using UnityEngine;

namespace InsideDev
{
    // Level loads seen from the replay side (SceneReplayer, the srec viewer, the srec probes). A load (respawn, chapter,
    // savepoint command) replaces the subscenes and takes the time scale back: GameManager.FadeAndReload sets its own
    // timeScale to 1, freezes the game while the scenes are swapped and then waits for a FixedUpdate. Whoever holds
    // Time.timeScale at 0 for a replay, a viewer or a probe has to let go at once, or that wait never ends.
    // Main thread only.
    internal static class RplXLoad
    {
        static FieldInfo fLatch;
        static bool latchLooked;

        // GameManager.LoadCount: one more for every load the game starts; 0 while the game manager is not there.
        public static int Count()
        {
            try { return GameManager.LoadCount; } catch { return 0; }
        }

        // From the first frame of the fade before a load to the frame its scenes are ready.
        public static bool Loading()
        {
            try { return GameManager.IsLoading(); } catch { return false; }
        }

        // countAtStart = Count() taken by the caller while no load ran. True from the first frame of a load on, and for good
        // once the load counted.
        public static bool Seen(int countAtStart) { return Count() != countAtStart || Loading(); }

        // Gives the time scale back to a running load. The load computes Time.timeScale itself (0 while it swaps the scenes,
        // 1 after) but writes it only when its own last value differs, so that latch (GameManager.oldTimeScale) is cleared
        // and the next GameManager.LateUpdate writes the value the load wants. Returns what was done, for a report line.
        public static string TimeToGame()
        {
            try { GameManager.TimeScale = 1f; } catch { }
            bool latched = false;
            try
            {
                if (!latchLooked)
                {
                    latchLooked = true;
                    fLatch = typeof(GameManager).GetField("oldTimeScale", BindingFlags.Instance | BindingFlags.NonPublic);
                }
                var gm = PersistentBehaviour<GameManager>.instance;
                if (gm != null && fLatch != null) { fLatch.SetValue(gm, -1f); latched = true; }
            }
            catch (Exception e) { DevLog.Error("replay time hand-back", e); }
            if (!latched) Time.timeScale = 1f;
            return latched ? "time scale handed back to the level load (GameManager.TimeScale 1, the game writes Time.timeScale itself)"
                           : "Time.timeScale and GameManager.TimeScale set to 1 for the level load (the game's time latch was not found)";
        }
    }
}
