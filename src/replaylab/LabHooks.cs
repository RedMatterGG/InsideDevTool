// ReplayLab frame hooks: RuntimeCallbacks update/lateUpdate (-400), an order-0 LateUpdate host, Camera.onPreCull/onPreRender/
// onPostRender delegates and a component probe on the world camera. Each phase runs once per frame, every call guarded.
using System;
using System.Collections.Generic;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev.ReplayLab
{
    public sealed class LabHost : MonoBehaviour
    {
        void LateUpdate() { LabHooks.Late0(); }
    }

    public sealed class LabCamProbe : MonoBehaviour
    {
        Camera cam;
        void Awake() { cam = GetComponent<Camera>(); }
        void OnPreCull() { LabHooks.ProbePreCull(cam); }
        void OnPreRender() { LabHooks.ProbePreRender(cam); }
        void OnPostRender() { LabHooks.ProbePostRender(cam); }
    }

    public static class LabHooks
    {
        public static LabContext ctx;
        public static int rawUpdate, rawLate, rawLate0, rawPreCull, rawPreRender, rawPostRender;
        static int fUpd = -1, fLate = -1, fLate0 = -1, fPreCull = -1, fPreRender = -1, fPostRender = -1, fpPreCull = -1, fpPreRender = -1, fpPostRender = -1;
        static bool installed;
        static GameObject hostGo;
        static LabHost host;

        public static MonoBehaviour Host(out bool own)
        {
            own = false;
            if (host == null)
            {
                try
                {
                    if (hostGo == null) { hostGo = new GameObject("InsideDev_ReplayLab"); UObj.DontDestroyOnLoad(hostGo); }
                    host = hostGo.GetComponent<LabHost>();
                    if (host == null) host = hostGo.AddComponent<LabHost>();
                }
                catch (Exception e) { DevLog.Write("[lab] LabHost unavailable: " + e.Message); host = null; }
            }
            if (host != null) { own = true; return host; }
            return UObj.FindObjectOfType<DevRoot>();
        }

        public static void Install(LabContext c)
        {
            ctx = c;
            fUpd = fLate = fLate0 = fPreCull = fPreRender = fPostRender = fpPreCull = fpPreRender = fpPostRender = -1;
            if (installed) return;
            RuntimeCallbacks.update += OnUpdate;
            RuntimeCallbacks.lateUpdate += OnLate;
            Camera.onPreCull += OnPreCull;
            Camera.onPreRender += OnPreRender;
            Camera.onPostRender += OnPostRender;
            installed = true;
        }

        public static void Uninstall()
        {
            ctx = null;
            if (!installed) return;
            RuntimeCallbacks.update -= OnUpdate;
            RuntimeCallbacks.lateUpdate -= OnLate;
            Camera.onPreCull -= OnPreCull;
            Camera.onPreRender -= OnPreRender;
            Camera.onPostRender -= OnPostRender;
            installed = false;
        }

        static bool Once(ref int guard)
        {
            int f = Time.frameCount;
            if (guard == f) return false;
            guard = f;
            return true;
        }

        static void Run(LabContext c, string what, Action a)
        {
            if (a == null) return;
            try { a(); } catch (Exception e) { c.HookError(what, e); }
        }

        static void Run(LabContext c, string what, Action<Camera> a, Camera cam)
        {
            if (a == null) return;
            try { a(cam); } catch (Exception e) { c.HookError(what, e); }
        }

        static void OnUpdate()
        {
            var c = ctx;
            rawUpdate++;
            if (c == null) return;
            c.Trace("rc.update");
            if (!Once(ref fUpd)) return;
            Run(c, "update", c.onUpdate);
        }

        static void OnLate()
        {
            var c = ctx;
            rawLate++;
            if (c == null) return;
            c.Trace("rc.lateUpdate");
            if (!Once(ref fLate)) return;
            c.ReassertTime();
            Run(c, "lateUpdate", c.onLate);
        }

        public static void Late0()
        {
            var c = ctx;
            rawLate0++;
            if (c == null) return;
            c.Trace("lab.lateUpdate0");
            if (!Once(ref fLate0)) return;
            Run(c, "lateUpdate0", c.onLate0);
        }

        static void OnPreCull(Camera cam)
        {
            var c = ctx;
            if (c == null || cam == null || cam != c.cam) return;
            rawPreCull++;
            c.Trace("cam.onPreCull");
            if (!Once(ref fPreCull)) return;
            Run(c, "onPreCull", c.onPreCull, cam);
        }

        static void OnPreRender(Camera cam)
        {
            var c = ctx;
            if (c == null || cam == null || cam != c.cam) return;
            rawPreRender++;
            c.Trace("cam.onPreRender");
            if (!Once(ref fPreRender)) return;
            Run(c, "onPreRender", c.onPreRender, cam);
        }

        static void OnPostRender(Camera cam)
        {
            var c = ctx;
            if (c == null || cam == null || cam != c.cam) return;
            rawPostRender++;
            c.Trace("cam.onPostRender");
            if (!Once(ref fPostRender)) return;
            Run(c, "onPostRender", c.onPostRender, cam);
        }

        public static void ProbePreCull(Camera cam)
        {
            var c = ctx;
            if (c == null || cam != c.cam) return;
            c.Trace("probe.OnPreCull");
            if (!Once(ref fpPreCull)) return;
            Run(c, "probe.OnPreCull", c.onProbePreCull, cam);
        }

        public static void ProbePreRender(Camera cam)
        {
            var c = ctx;
            if (c == null || cam != c.cam) return;
            c.Trace("probe.OnPreRender");
            if (!Once(ref fpPreRender)) return;
            Run(c, "probe.OnPreRender", c.onProbePreRender, cam);
        }

        public static void ProbePostRender(Camera cam)
        {
            var c = ctx;
            if (c == null || cam != c.cam) return;
            c.Trace("probe.OnPostRender");
            if (!Once(ref fpPostRender)) return;
            Run(c, "probe.OnPostRender", c.onProbePostRender, cam);
        }
    }
}
