using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace InsideDev
{
    // Replay mode PLAY (docs/REPLAY_DESIGN.md 2.2): the one owner of the RuntimeCallbacks.update / lateUpdate and
    // Camera.onPreCull subscriptions used by SceneRecorder, SceneReplayer, SceneCommands and CORE (RecordMarkers on preUpdate).
    // Client rule (REC_A, PLAY_B, PLAY_C, CORE_A): add handlers, then Ensure(); remove handlers, then Release(), which
    // unsubscribes once preUpdate, lateWrite and preCullMain are all empty and no culler hold is left.
    // Handlers of one event run in subscription order, each in its own try/catch (PDUtilities.SafeExecute wraps a whole
    // multicast in one try, and Camera.onPreCull has no try at all); every phase fires at most once per Time.frameCount;
    // preCullMain fires only for RenderHost.WorldCam(). preUpdate runs in the public RuntimeCallbacks.update event, before
    // RuntimeCallbacks.ExecuteInEditModeIncluded.update of the same frame; lateWrite in RuntimeCallbacks.lateUpdate (-400).
    // Internal culler hold (SceneReplayer): while cullerHolds > 0, every preUpdate repeats
    // `ExecuteInEditModeIncluded.update -= BoundsCullerRuntime.UpdateBoundsCullers` (AddBoundsCuller re-hooks it when the
    // culler count was 0, FP/BoundsCullerRuntime.cs:169-173), a no-op when absent.
    public static class SceneHooks
    {
        static readonly List<Action> updates = new List<Action>(), lates = new List<Action>();
        static readonly List<Action<Camera>> culls = new List<Action<Camera>>();
        static Action[] updateRun = new Action[0], lateRun = new Action[0];
        static Action<Camera>[] cullRun = new Action<Camera>[0];
        static readonly Action cullerUpdate = BoundsCullerRuntime.UpdateBoundsCullers;
        static readonly Dictionary<string, int> failures = new Dictionary<string, int>();
        static bool subscribed;
        static Camera mainCamera;
        static int lastUpdate = -1, lastLate = -1, lastCull = -1;
        static FieldInfo fEditUpdate;

        internal static int cullerHolds;
        internal static int errors, cullerUnhooks;

        public static event Action preUpdate
        {
            add { if (value == null) return; updates.Add(value); updateRun = updates.ToArray(); }
            remove { if (value != null && updates.Remove(value)) updateRun = updates.ToArray(); }
        }

        public static event Action lateWrite
        {
            add { if (value == null) return; lates.Add(value); lateRun = lates.ToArray(); }
            remove { if (value != null && lates.Remove(value)) lateRun = lates.ToArray(); }
        }

        public static event Action<Camera> preCullMain                  // Camera.onPreCull: after every component OnPreCull (L1)
        {
            add { if (value == null) return; culls.Add(value); cullRun = culls.ToArray(); }
            remove { if (value != null && culls.Remove(value)) cullRun = culls.ToArray(); }
        }

        public static int lateFrame { get { return lastLate; } }        // Time.frameCount of the last RuntimeCallbacks.lateUpdate

        public static Camera main { get { return mainCamera; } }

        internal static bool subscribedNow { get { return subscribed; } }

        public static void Ensure()
        {
            try { mainCamera = RenderHost.WorldCam(); } catch { mainCamera = null; }
            if (subscribed) return;
            RuntimeCallbacks.update += OnUpdate;
            RuntimeCallbacks.lateUpdate += OnLate;
            Camera.onPreCull += OnPreCull;
            subscribed = true;
            DevLog.Write("[scene] hooks on (RuntimeCallbacks.update, RuntimeCallbacks.lateUpdate, Camera.onPreCull)");
        }

        public static void Release()
        {
            if (!subscribed || updates.Count + lates.Count + culls.Count > 0 || cullerHolds > 0) return;
            RuntimeCallbacks.update -= OnUpdate;
            RuntimeCallbacks.lateUpdate -= OnLate;
            Camera.onPreCull -= OnPreCull;
            subscribed = false;
            lastUpdate = lastCull = -1;
            DevLog.Write("[scene] hooks off");
        }

        internal static void UnhookCullers()
        {
            RuntimeCallbacks.ExecuteInEditModeIncluded.update -= cullerUpdate;
        }

        internal static void RehookCullers()
        {
            if (!CullerUpdateHooked()) RuntimeCallbacks.ExecuteInEditModeIncluded.update += cullerUpdate;
        }

        internal static bool CullerUpdateHooked()
        {
            try
            {
                if (fEditUpdate == null) fEditUpdate = typeof(RuntimeCallbacks.ExecuteInEditModeIncluded).GetField("update", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                var d = fEditUpdate != null ? fEditUpdate.GetValue(null) as Delegate : null;
                if (d == null) return false;
                foreach (var x in d.GetInvocationList())
                    if (x.Method.DeclaringType == typeof(BoundsCullerRuntime) && x.Method.Name == "UpdateBoundsCullers" && x.Method.GetParameters().Length == 0) return true;
            }
            catch (Exception e) { DevLog.Error("scene hooks: culler hook check", e); }
            return false;
        }

        internal static string Status()
        {
            return "hooks " + (subscribed ? "on" : "off") + ": preUpdate " + updates.Count + ", lateWrite " + lates.Count + ", preCullMain " + culls.Count +
                   ", culler holds " + cullerHolds + ", lateFrame " + lastLate + ", errors " + errors;
        }

        static void OnUpdate()
        {
            int f = Time.frameCount;
            if (f == lastUpdate) return;
            lastUpdate = f;
            try { mainCamera = RenderHost.WorldCam(); } catch { mainCamera = null; }
            if (cullerHolds > 0)
            {
                try { UnhookCullers(); cullerUnhooks++; } catch (Exception e) { Fail("culler unhook", null, e); }
            }
            var run = updateRun;
            for (int i = 0; i < run.Length; i++)
            {
                try { run[i](); } catch (Exception e) { Fail("preUpdate", run[i], e); }
            }
        }

        static void OnLate()
        {
            int f = Time.frameCount;
            if (f == lastLate) return;
            lastLate = f;
            var run = lateRun;
            for (int i = 0; i < run.Length; i++)
            {
                try { run[i](); } catch (Exception e) { Fail("lateWrite", run[i], e); }
            }
        }

        static void OnPreCull(Camera cam)
        {
            try
            {
                if (cam == null) return;
                var m = RenderHost.WorldCam();
                if (m == null || cam != m) return;
                int f = Time.frameCount;
                if (f == lastCull) return;
                lastCull = f;
                mainCamera = m;
                var run = cullRun;
                for (int i = 0; i < run.Length; i++)
                {
                    try { run[i](cam); } catch (Exception e) { Fail("preCullMain", run[i], e); }
                }
            }
            catch (Exception e) { Fail("preCullMain", null, e); }
        }

        static void Fail(string phase, Delegate d, Exception e)
        {
            errors++;
            string who = phase;
            try { if (d != null) who = phase + " " + d.Method.DeclaringType.Name + "." + d.Method.Name; } catch { }
            int n;
            failures.TryGetValue(who, out n);
            failures[who] = ++n;
            if (n <= 5 || n % 600 == 0) DevLog.Write("ERROR in scene hook " + who + " (" + n + "x): " + e.GetType().Name + ": " + e.Message + (n <= 2 ? "\n" + e.StackTrace : ""));
        }
    }
}
