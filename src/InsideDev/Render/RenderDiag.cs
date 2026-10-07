using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    // Diagnostics mode: on-screen liveness board (F11), the Diagnostics tab (backend control, cameras, events),
    // and text reports for the remote bridge. Everything here reads RenderHost; nothing here owns state
    // the renderer depends on.
    public static class RenderDiag
    {
        public static bool board;
        static int page;
        static readonly string[] pages = { "Cameras", "Render events" };

        // ---------------------------------------------------------------- camera table
        public class CamRow
        {
            public Camera cam;
            public string name, path, target, viewport, mask, clear, effects, roles, renderPath;
            public float depth;
            public bool enabled, active;
            public int order;
        }

        static List<CamRow> rows = new List<CamRow>();
        static float nextRows;
        static readonly Dictionary<Type, bool> hasOnRenderImage = new Dictionary<Type, bool>();

        static bool Effect(Type t)
        {
            bool v;
            if (hasOnRenderImage.TryGetValue(t, out v)) return v;
            v = false;
            for (var k = t; k != null && k != typeof(MonoBehaviour) && !v; k = k.BaseType)
                v = k.GetMethod("OnRenderImage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) != null;
            hasOnRenderImage[t] = v;
            return v;
        }

        static HashSet<int> SceneRootIds()
        {
            var ids = new HashSet<int>();
            foreach (var o in UObj.FindObjectsOfType(typeof(Transform))) { var t = o as Transform; if (t != null) ids.Add(t.root.GetInstanceID()); }
            foreach (var o in Resources.FindObjectsOfTypeAll(typeof(GameObject)))
            {
                var g = o as GameObject;
                if (g != null && g.hideFlags == HideFlags.None && g.transform.parent == null && !g.activeSelf) ids.Add(g.transform.GetInstanceID());
            }
            return ids;
        }

        public static List<CamRow> Cameras(bool force)
        {
            if (!force && Time.realtimeSinceStartup < nextRows) return rows;
            nextRows = Time.realtimeSinceStartup + 1f;
            var res = new List<CamRow>();
            try
            {
                RenderHost.RefreshCameras();
                var roots = SceneRootIds();
                var world = RenderHost.WorldCam();
                Camera main = null; try { main = Camera.main; } catch { }
                var seen = new HashSet<int>();
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(Camera)))
                {
                    var c = o as Camera;
                    if (c == null || !seen.Add(c.GetInstanceID())) continue;
                    bool ours = c == RenderHost.overlayCam;
                    if (!ours && (c.gameObject.hideFlags != HideFlags.None || !roots.Contains(c.transform.root.GetInstanceID()))) continue;
                    var r = new CamRow { cam = c, name = c.name, path = Inspector.PathOf(c.transform), depth = c.depth, enabled = c.enabled, active = c.gameObject.activeInHierarchy };
                    var tt = c.targetTexture;
                    r.target = tt == null ? "screen" : "RT " + tt.name + " " + tt.width + "x" + tt.height;
                    var pr = c.pixelRect;
                    r.viewport = string.Format("{0:0},{1:0} {2:0}x{3:0}", pr.x, pr.y, pr.width, pr.height);
                    r.mask = c.cullingMask == -1 ? "Everything" : c.cullingMask == 0 ? "Nothing" : "0x" + c.cullingMask.ToString("X8");
                    r.clear = c.clearFlags.ToString();
                    r.renderPath = c.renderingPath.ToString() + (c.hdr ? " HDR" : "");
                    var fx = new List<string>();
                    foreach (var comp in c.GetComponents<Component>())
                    {
                        if (comp == null) continue;
                        var ct = comp.GetType();
                        var beh = comp as Behaviour;
                        if (beh != null && !beh.enabled) continue;
                        // C# image effects (OnRenderImage) and Playdead's native engine-side post passes (UnityEngine.D11*)
                        if ((comp is MonoBehaviour && Effect(ct)) || (ct.Namespace == "UnityEngine" && ct.Name.StartsWith("D11"))) fx.Add(ct.Name);
                    }
                    r.effects = fx.Count == 0 ? "-" : string.Join(", ", fx.ToArray());
                    r.order = RenderHost.ObservedOrder(c);
                    var roles = new List<string>();
                    if (c == RenderHost.finalCam) roles.Add("FINAL");
                    if (c == world) roles.Add("WORLD");
                    if (c == main) roles.Add("MAIN");
                    if (ours) roles.Add("OURS");
                    r.roles = string.Join(" ", roles.ToArray());
                    res.Add(r);
                }
                res.Sort((a, b) =>
                {
                    int oa = a.order < 0 ? 999 : a.order, ob = b.order < 0 ? 999 : b.order;
                    return oa != ob ? oa.CompareTo(ob) : a.depth.CompareTo(b.depth);
                });
            }
            catch (Exception e) { DevLog.Error("camera table", e); }
            rows = res;
            return rows;
        }

        public static string CameraReport()
        {
            var sb = new StringBuilder();
            var list = Cameras(true);
            sb.Append(list.Count).Append(" camera(s); ").Append(RenderHost.ObservedCount).Append(" rendered last frame (order = observed Camera.onPostRender order; camera type n/a in Unity 5.0)\n");
            foreach (var r in list)
            {
                sb.Append(r.order >= 0 ? "#" + r.order : "--").Append("  ").Append(r.name).Append("  [").Append(r.roles).Append("]\n")
                  .Append("     path ").Append(r.path).Append('\n')
                  .Append("     depth ").Append(r.depth).Append("  enabled ").Append(r.enabled).Append("  activeInHierarchy ").Append(r.active)
                  .Append("  target ").Append(r.target).Append("  viewport ").Append(r.viewport).Append('\n')
                  .Append("     mask ").Append(r.mask).Append("  clear ").Append(r.clear).Append("  path ").Append(r.renderPath).Append("  image effects: ").Append(r.effects).Append('\n');
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- on-screen board (world layer)
        static readonly Color ok = new Color(0.45f, 1f, 0.55f, 1f), bad = new Color(1f, 0.4f, 0.35f, 1f), dim = new Color(0.75f, 0.8f, 0.9f, 1f);

        public static void DrawBoard(Draw d, Rect view)
        {
            if (!board) return;
            var lines = RenderHost.LivenessLines();
            string res; bool rok = RenderHost.renderer.ResourcesOk(out res);
            float w = Mathf.Min(760, view.width - 20), x = view.xMax - w - 10, y = Mathf.Max(60, view.y + 6), lh = 15;
            if (w < 200) return;
            d.Fill(new Rect(x, y, w, lh * (lines.Count + 5) + 10), new Color(0, 0, 0, 0.72f));
            float cy = y + 5;
            d.Text(x + 8, cy, "DIAGNOSTICS [F11]   frame " + Time.frameCount + "   " + Screen.width + "x" + Screen.height + (Screen.fullScreen ? " fullscreen" : " windowed") +
                   "   backend " + RenderHost.requested + " -> " + RenderHost.active + "   focus lost/regained " + RenderHost.focusLostCount + "/" + RenderHost.focusRegainCount, dim, 12);
            cy += lh;
            foreach (var l in lines)
            {
                d.Text(x + 8, cy, (l.Value ? "● " : "○ ") + l.Key, l.Value ? ok : bad, 12);
                d.Text(x + 150, cy, l.Detail, dim, 12);
                cy += lh;
            }
            d.Text(x + 8, cy, (rok ? "● " : "○ ") + "RESOURCES", rok ? ok : bad, 12); d.Text(x + 150, cy, res, dim, 12); cy += lh;
            d.Text(x + 8, cy, (RenderHost.probeOk ? "● " : "○ ") + "PIXELS", RenderHost.probeOk ? ok : bad, 12); d.Text(x + 150, cy, RenderHost.probeResult, dim, 12); cy += lh;
            d.Text(x + 8, cy, "cameras " + Camera.allCamerasCount + "   world " + RenderHost.CamName(RenderHost.WorldCam()) + "   final " + RenderHost.CamName(RenderHost.finalCam) +
                   (RenderHost.lastError != null ? "   last error: " + RenderHost.lastError : ""), dim, 12);
        }

        // ---------------------------------------------------------------- Diagnostics tab
        static bool showPerf = true;
        public static void DrawTab(UI ui, DevCore core)
        {
            showPerf = ui.Toggle(showPerf, "InsideDev cost per subsystem (Phase 12)");
            if (showPerf) Perf.DrawCached(ui);
            ui.BeginRow();
            ui.Label("Render backend:", null, 110);
            Backend[] bs = { Backend.Auto, Backend.EndOfFrame, Backend.OverlayCamera, Backend.GameCameraPost };
            string[] names = { "Auto", "End of frame", "Overlay camera", "Game camera (0.9 bug)" };
            for (int i = 0; i < bs.Length; i++)
                if (ui.Button(names[i], -2, RenderHost.requested == bs[i] ? new Color(0.25f, 0.42f, 0.7f, 1f) : (Color?)null)) RenderHost.Request(bs[i]);
            ui.EndRow();
            ui.BeginRow();
            if (ui.Button("Verify pixels now")) RenderHost.RequestProbe("manual", 1);
            if (ui.Button("Reset renderer resources")) { RenderHost.renderer.DestroyResources(); RenderHost.Event("renderer resources destroyed by user"); }
            if (ui.Button("Log snapshot")) RenderHost.Snapshot("manual snapshot");
            board = ui.Toggle(board, "on-screen board [F11]");
            ui.EndRow();
            // status lines are rebuilt 4x per second, not every frame (they were ~100 KB/frame of strings)
            if (Time.realtimeSinceStartup >= statusAt)
            {
                statusAt = Time.realtimeSinceStartup + 0.25f;
                status.Clear();
                status.Add(new KeyValuePair<string, Color>("Active: " + RenderHost.active + "   (Auto = end of frame, falls back to our overlay camera if that callback stops or fails the pixel check)", UI.Dim));
                foreach (var l in RenderHost.LivenessLines())
                    status.Add(new KeyValuePair<string, Color>((l.Value ? "● " : "○ ") + l.Key + "  " + l.Detail, l.Value ? ok : bad));
                string res; bool rok = RenderHost.renderer.ResourcesOk(out res);
                status.Add(new KeyValuePair<string, Color>((rok ? "● " : "○ ") + "RESOURCES        " + res, rok ? ok : bad));
                status.Add(new KeyValuePair<string, Color>((RenderHost.probeOk ? "● " : "○ ") + "PIXEL PROBE      " + RenderHost.probeResult, RenderHost.probeOk ? ok : bad));
                status.Add(new KeyValuePair<string, Color>("focus lost " + RenderHost.focusLostCount + "   regained " + RenderHost.focusRegainCount + "   submits " + RenderHost.submitCount + "   failures " + RenderHost.submitFails +
                         (RenderHost.lastError != null ? "   last error: " + RenderHost.lastError : ""), UI.Dim));
            }
            for (int i = 0; i < status.Count; i++) ui.Label(status[i].Key, status[i].Value);
            page = ui.Tabs(page, pages);
            if (page == 0) CamerasPage(ui, core); else EventsPage(ui);
        }

        static float statusAt;
        static readonly List<KeyValuePair<string, Color>> status = new List<KeyValuePair<string, Color>>();
        static object camTextFor; static string camHeader = "";
        static readonly List<string[]> camText = new List<string[]>();

        static void CamerasPage(UI ui, DevCore core)
        {
            var list = Cameras(false);
            if (!ReferenceEquals(list, camTextFor))
            {
                camTextFor = list; camText.Clear();
                camHeader = list.Count + " cameras.  Order = observed render order last frame.  FINAL = last camera drawing to the screen.  WORLD = camera used for overlay projection.";
                foreach (var r in list)
                    camText.Add(new[] { (r.order >= 0 ? "#" + r.order : "--") + "  " + r.name + "   depth " + r.depth + "   " + r.target + "   " + (r.enabled ? "enabled" : "disabled") + (r.active ? "" : ", inactive") + "   [" + r.roles + "]",
                                        "      vp " + r.viewport + "   mask " + r.mask + "   clear " + r.clear + "   " + r.renderPath + "   effects: " + r.effects,
                                        "      " + r.path });
            }
            ui.Label(camHeader, UI.Dim);
            ui.BeginScroll("diagcams", ui.Remaining);
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (r.cam == null) continue;
                Color c = !r.enabled || !r.active ? UI.Dim : r.roles.Contains("FINAL") ? ok : UI.Txt;
                if (ui.Item(camText[i][0], c)) core.SelectInInspector(r.cam.gameObject);
                ui.Label(camText[i][1], UI.Dim);
                ui.Label(camText[i][2], UI.Dim);
            }
            ui.EndScroll();
        }

        static int lastEventCount;
        static void EventsPage(UI ui)
        {
            if (RenderHost.events.Count != lastEventCount) { lastEventCount = RenderHost.events.Count; ui.ScrollToEnd("diagev"); }
            ui.BeginScroll("diagev", ui.Remaining);
            foreach (var e in RenderHost.events) ui.Label(e, e.Contains("FAILED") || e.Contains("LOST") ? bad : e.Contains("VERIFIED") || e.Contains("first successful") ? ok : UI.Txt);
            ui.EndScroll();
        }
    }
}
