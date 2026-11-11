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

        // Which cameras belong to the loaded scenes (not assets, not editor helpers). The object database answers that per
        // camera while it is fresh; the scene scan below (every active transform plus every loaded GameObject, 10 ms and
        // more) is the fallback and is kept for 10 s and until the next level load.
        static HashSet<int> rootIds;
        static float rootIdsAt = -100f;
        const float RootsKeep = 10f, DatabaseFresh = 15f;

        public static void OnLevelLoaded() { rootIds = null; nextRows = 0f; }

        static HashSet<int> SceneRootIds()
        {
            if (rootIds != null && Time.realtimeSinceStartup - rootIdsAt < RootsKeep) return rootIds;
            var ids = new HashSet<int>();
            foreach (var o in UObj.FindObjectsOfType(typeof(Transform))) { var t = o as Transform; if (t != null) ids.Add(t.root.GetInstanceID()); }
            foreach (var o in Resources.FindObjectsOfTypeAll(typeof(GameObject)))
            {
                var g = o as GameObject;
                if (g != null && g.hideFlags == HideFlags.None && g.transform.parent == null && !g.activeSelf) ids.Add(g.transform.GetInstanceID());
            }
            rootIds = ids; rootIdsAt = Time.realtimeSinceStartup;
            return ids;
        }

        public static List<CamRow> Cameras(bool force)
        {
            RenderHost.WantCameraOrder();
            if (!force && Time.realtimeSinceStartup < nextRows) return rows;
            nextRows = Time.realtimeSinceStartup + 1f;
            var res = new List<CamRow>();
            try
            {
                RenderHost.RefreshCameras();
                ObjectDatabase.Demand(5f);
                bool byDatabase = ObjectDatabase.Ready && ObjectDatabase.Age < DatabaseFresh;
                HashSet<int> roots = null;
                var world = RenderHost.WorldCam();
                Camera main = null; try { main = Camera.main; } catch { }
                var seen = new HashSet<int>();
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(Camera)))
                {
                    var c = o as Camera;
                    if (c == null || !seen.Add(c.GetInstanceID())) continue;
                    bool ours = c == RenderHost.overlayCam;
                    if (!ours)
                    {
                        if (c.gameObject.hideFlags != HideFlags.None) continue;
                        if (byDatabase)
                        {
                            // not in the database and not active: a prefab or asset camera (or one created switched off
                            // since the last pass, which then shows up after the next one)
                            if (ObjectDatabase.Get(c.gameObject) == null && !c.gameObject.activeInHierarchy) continue;
                        }
                        else
                        {
                            if (roots == null) roots = SceneRootIds();
                            if (!roots.Contains(c.transform.root.GetInstanceID())) continue;
                        }
                    }
                    var r = new CamRow { cam = c, name = c.name, path = Inspector.PathOf(c.transform), depth = c.depth, enabled = c.enabled, active = c.gameObject.activeInHierarchy };
                    var tt = c.targetTexture;
                    r.target = tt == null ? "screen" : "RT " + tt.name + " " + tt.width + "x" + tt.height;
                    var pr = c.pixelRect;
                    r.viewport = Fmt.S("{0:0},{1:0} {2:0}x{3:0}", pr.x, pr.y, pr.width, pr.height);
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
                        if ((comp is MonoBehaviour && Effect(ct)) || (ct.Namespace == "UnityEngine" && ct.Name.StartsWith("D11", StringComparison.Ordinal))) fx.Add(ct.Name);
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

        const string OrderNote = "render order is being collected (the camera callbacks were off): ask again in a moment";

        public static string CameraReport() { return CameraReport(false); }

        // json: one line { ok, orderKnown, rendered, cameras: [ { order, name, path, roles[], depth, enabled, active, target, viewport, mask, clear, renderPath, effects } ] }
        public static string CameraReport(bool json)
        {
            var list = Cameras(true);
            bool known = RenderHost.CameraOrderKnown;
            if (json)
            {
                var cams = new List<object>();
                foreach (var r in list)
                {
                    var o = new Json.Obj();
                    o["order"] = r.order; o["name"] = r.name; o["path"] = r.path;
                    o["roles"] = new List<object>(r.roles.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                    o["depth"] = r.depth; o["enabled"] = r.enabled; o["active"] = r.active; o["target"] = r.target; o["viewport"] = r.viewport;
                    o["mask"] = r.mask; o["clear"] = r.clear; o["renderPath"] = r.renderPath; o["effects"] = r.effects;
                    cams.Add(o);
                }
                var root = new Json.Obj();
                root["ok"] = true; root["orderKnown"] = known; root["rendered"] = RenderHost.ObservedCount; root["cameras"] = cams;
                return JsonLine.Write(root);
            }
            var sb = new StringBuilder();
            sb.Append(list.Count).Append(" camera(s); ").Append(RenderHost.ObservedCount).Append(" rendered last frame (order = observed Camera.onPostRender order; camera type n/a in Unity 5.0)\n");
            if (!known) sb.Append(OrderNote).Append('\n');
            foreach (var r in list)
            {
                sb.Append(r.order >= 0 ? "#" + r.order : "--").Append("  ").Append(r.name).Append("  [").Append(r.roles).Append("]\n")
                  .Append("     path ").Append(r.path).Append('\n')
                  .Append("     depth ").Append(Fmt.F(r.depth, "0.##")).Append("  enabled ").Append(r.enabled).Append("  activeInHierarchy ").Append(r.active)
                  .Append("  target ").Append(r.target).Append("  viewport ").Append(r.viewport).Append('\n')
                  .Append("     mask ").Append(r.mask).Append("  clear ").Append(r.clear).Append("  path ").Append(r.renderPath).Append("  image effects: ").Append(r.effects).Append('\n');
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- on-screen board (world layer)
        static readonly Color ok = new Color(0.45f, 1f, 0.55f, 1f), bad = new Color(1f, 0.4f, 0.35f, 1f), dim = new Color(0.75f, 0.8f, 0.9f, 1f);

        // the board text is rebuilt 4x per second like the tab's status lines; the frame counter in its first line is
        // as old as that
        struct BoardLine { public string key, detail; public Color c; }
        static readonly List<BoardLine> boardLines = new List<BoardLine>();
        static float boardAt;

        public static void DrawBoard(Draw d, Rect view)
        {
            InputCapture.EmitTag(d, view);
            if (!board) return;
            float w = Mathf.Min(760, view.width - 20), x = view.xMax - w - 10, y = Mathf.Max(60, view.y + 6), lh = 15;
            if (w < 200) return;
            if (Time.realtimeSinceStartup >= boardAt || boardLines.Count == 0)
            {
                boardAt = Time.realtimeSinceStartup + 0.25f;
                boardLines.Clear();
                boardLines.Add(new BoardLine { key = "DIAGNOSTICS [F11]   frame " + Time.frameCount + "   " + Screen.width + "x" + Screen.height + (Screen.fullScreen ? " fullscreen" : " windowed") +
                       "   backend " + RenderHost.requested + " -> " + RenderHost.active + "   focus lost/regained " + RenderHost.focusLostCount + "/" + RenderHost.focusRegainCount, c = dim });
                foreach (var l in RenderHost.LivenessLines()) boardLines.Add(new BoardLine { key = (l.Value ? "● " : "○ ") + l.Key, detail = l.Detail, c = l.Value ? ok : bad });
                string res; bool rok = RenderHost.renderer.ResourcesOk(out res);
                boardLines.Add(new BoardLine { key = (rok ? "● " : "○ ") + "RESOURCES", detail = res, c = rok ? ok : bad });
                boardLines.Add(new BoardLine { key = (RenderHost.probeOk ? "● " : "○ ") + "PIXELS", detail = RenderHost.probeResult, c = RenderHost.probeOk ? ok : bad });
                boardLines.Add(new BoardLine { key = "cameras " + Camera.allCamerasCount + "   world " + RenderHost.CamName(RenderHost.WorldCam()) + "   final " + RenderHost.CamName(RenderHost.finalCam) +
                       (RenderHost.lastError != null ? "   last error: " + RenderHost.lastError : ""), c = dim });
            }
            d.Fill(new Rect(x, y, w, lh * boardLines.Count + 10), new Color(0, 0, 0, 0.72f));
            float cy = y + 5;
            for (int i = 0; i < boardLines.Count; i++)
            {
                var l = boardLines[i];
                d.Text(x + 8, cy, l.key, l.c, 12);
                if (l.detail != null) d.Text(x + 150, cy, l.detail, dim, 12);
                cy += lh;
            }
        }

        // ---------------------------------------------------------------- Diagnostics tab
        static bool showPerf = true;
        static readonly Backend[] backends = { Backend.Auto, Backend.EndOfFrame, Backend.OverlayCamera, Backend.GameCameraPost };
        static readonly string[] backendNames = { "Auto", "End of frame", "Overlay camera", "Game camera (0.9 bug)" };
        static readonly Color cChosen = new Color(0.25f, 0.42f, 0.7f, 1f);

        public static void DrawTab(UI ui, DevCore core)
        {
            RenderHost.WantCameraOrder();
            showPerf = ui.Toggle(showPerf, "InsideDev cost per subsystem (Phase 12)");
            if (showPerf) Perf.DrawCached(ui);
            ui.BeginRow();
            ui.Label("Render backend:", null, 110);
            for (int i = 0; i < backends.Length; i++)
                if (ui.Button(backendNames[i], -2, RenderHost.requested == backends[i] ? cChosen : (Color?)null)) RenderHost.Request(backends[i]);
            ui.EndRow();
            ui.BeginRow();
            if (ui.Button("Verify pixels now")) RenderHost.RequestProbe("manual", 1, true);
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
                         "   text metrics: " + RenderResources.MetricsStatus() + (RenderHost.lastError != null ? "   last error: " + RenderHost.lastError : ""), UI.Dim));
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
                    camText.Add(new[] { (r.order >= 0 ? "#" + r.order : "--") + "  " + r.name + "   depth " + Fmt.F(r.depth, "0.##") + "   " + r.target + "   " + (r.enabled ? "enabled" : "disabled") + (r.active ? "" : ", inactive") + "   [" + r.roles + "]",
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
