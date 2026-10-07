using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    // Where the editor submits its frame.
    //   EndOfFrame     WaitForEndOfFrame coroutine: after every camera and every image effect. Default.
    //   OverlayCamera  our own camera (clear none, culling mask 0, highest depth) via the engine-owned static
    //                  Camera.onPostRender: renders after all game cameras and their post effects. Fallback.
    //   GameCameraPost Camera.onPostRender of the gameplay camera. This is what 0.9 fell back to after focus
    //                  loss; INSIDE's post stack (PDPostCombined.OnRenderImage) runs AFTER onPostRender and
    //                  overwrites the whole screen, so anything drawn there is invisible. Kept only to reproduce.
    public enum Backend { Auto, EndOfFrame, OverlayCamera, GameCameraPost }

    public static class RenderHost
    {
        public static readonly IEditorRenderer glRenderer = new UnityGLRenderer();
        public static readonly IEditorRenderer meshRenderer = new UnityMeshRenderer();
        public static IEditorRenderer renderer = glRenderer;

        public static void SetRenderer(string name)
        {
            var r = name == "mesh" || name == "UnityMesh" ? meshRenderer : glRenderer;
            if (r == renderer) return;
            Event("renderer " + renderer.Name + " -> " + r.Name);
            renderer = r;
            Draw.Metrics = r;
            r.Invalidate("renderer switch");
            // Graphics.DrawMeshNow only draws inside a camera render (verified: end-of-frame probe fails),
            // so the mesh renderer prefers the overlay-camera backend
            if (requested == Backend.Auto) Switch(PreferredBackend, "renderer " + r.Name + " prefers " + PreferredBackend);
            RequestProbe("renderer switch", 3);
        }
        public static readonly Draw draw = new Draw();
        public static Action<Draw> buildWorld;          // editor fills the world layer (overlay, HUD, diag board)

        public static Backend requested = Backend.Auto;
        public static Backend active = Backend.EndOfFrame;
        static bool inited;

        // ---------------------------------------------------------------- liveness (frame numbers, -1 = never)
        public static int updateFrame = -1, inputPollFrame = -1, inputEventFrame = -1;
        public static int eofFrame = -1, ovlFrame = -1, gameCamFrame = -1, anyCamFrame = -1;
        public static int uiBuildFrame = -1, worldBuildFrame = -1, submitFrame = -1, submitOkFrame = -1;
        public static long eofCount, ovlCount, gameCamCount, camPostCount, submitCount, submitFails, loaderGuiCount;
        public static RenderStats lastStats;
        public static string lastError;
        public static int uiCmds;
        public static bool uiPanelOpen;
        static float lastErrorLog;

        // ---------------------------------------------------------------- focus / screen / scene
        public static bool focused = true, paused;
        public static int focusLostCount, focusRegainCount;
        public static int firstSubmitAfterRegainCount, maxRegainFrames, probeVerifiedCount, probeFailedCount;
        static int regainFrame = -1;
        static bool awaitFirstAfterRegain;
        static int sw, sh; static bool sfull; static string levelName;
        static int eofGraceUntil;
        static int eofStreak;

        // ---------------------------------------------------------------- events (ring buffer shown in the diagnostics tab)
        public static readonly List<string> events = new List<string>();

        public static void Event(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " f" + Time.frameCount + "  " + msg;
            events.Add(line);
            if (events.Count > 80) events.RemoveRange(0, events.Count - 80);
            DevLog.Write("[render] " + msg);
        }

        // ---------------------------------------------------------------- lifecycle hooks (called by DevRoot / loader)
        public static void Init()
        {
            if (inited) return;
            inited = true;
            Draw.Metrics = renderer;
            Camera.onPostRender += OnCamPost;
            Camera.onPreCull += OnCamPreCull;
            try { Font.textureRebuilt += OnFontRebuilt; } catch (Exception e) { DevLog.Error("font rebuild hook", e); }
            sw = Screen.width; sh = Screen.height; sfull = Screen.fullScreen;
            try { levelName = Application.loadedLevelName; } catch { levelName = "?"; }
            RequestProbe("startup", 90);
            Event("render host up: backend " + requested + " -> " + active + ", " + sw + "x" + sh + (sfull ? " fullscreen" : " windowed"));
        }

        public static void Shutdown()
        {
            Camera.onPostRender -= OnCamPost;
            Camera.onPreCull -= OnCamPreCull;
            try { Font.textureRebuilt -= OnFontRebuilt; } catch { }
            inited = false;
        }

        static void OnFontRebuilt(Font f)
        {
            Event("font atlas rebuilt: " + (f != null ? f.name : "?"));
        }

        public static void OnFocus(bool f)
        {
            if (f == focused) { Snapshot("focus " + (f ? "gained (dup)" : "lost (dup)")); return; }
            focused = f;
            if (!f) { focusLostCount++; Snapshot("FOCUS LOST #" + focusLostCount); return; }
            focusRegainCount++;
            regainFrame = Time.frameCount;
            awaitFirstAfterRegain = true;
            eofGraceUntil = Time.frameCount + 10;
            renderer.Invalidate("focus regained");
            RefreshCameras();
            RequestProbe("focus regained #" + focusRegainCount, 3);
            Snapshot("FOCUS REGAINED #" + focusRegainCount);
        }

        public static void OnPause(bool p)
        {
            paused = p;
            Snapshot(p ? "application paused" : "application resumed");
            if (!p) renderer.Invalidate("application resumed");
        }

        public static void OnLevelLoaded(int level)
        {
            renderer.Invalidate("scene load " + level);
            ObjectDatabase.MarkDirty("scene load " + level);
            Levels.Bump("level load");
            RefreshCameras();
        }

        public static void OnLoaderGui() { loaderGuiCount++; }

        // Called first thing in DevRoot.Update.
        public static void OnUpdate()
        {
            int f = Time.frameCount;
            updateFrame = f;
            if (firstUpdateFrame < 0) firstUpdateFrame = f;
            if (!inited) Init();

            // resolution / fullscreen change
            int w = Screen.width, h = Screen.height; bool full = Screen.fullScreen;
            if (w != sw || h != sh || full != sfull)
            {
                Event("screen changed " + sw + "x" + sh + (sfull ? " full" : " win") + " -> " + w + "x" + h + (full ? " full" : " win"));
                sw = w; sh = h; sfull = full;
                renderer.Invalidate("screen change");
                RefreshCameras();
                RequestProbe("screen change", 5);
            }
            // scene change (INSIDE streams additively; loadedLevelName changes on real level loads)
            string ln = null;
            if (f % 30 == 0) { try { ln = Application.loadedLevelName; } catch { } }
            if (ln != null && ln != levelName) { levelName = ln; renderer.Invalidate("level " + ln); RefreshCameras(); }

            Watchdog(f);
            FocusTest.Tick();
            if (active == Backend.OverlayCamera) MaintainOverlayCamera();
            else if (overlayCam != null && overlayCam.enabled) overlayCam.enabled = false;
            if (camScanDue < Time.realtimeSinceStartup) RefreshCameras();
        }

        // Frame-based liveness: a pause (focus loss) stops frames, so it can never look like a dead callback.
        // 0.9 used wall-clock time here: any Alt-Tab longer than 2 s tripped it and moved rendering to the
        // gameplay camera's onPostRender, where INSIDE's post effects erase it -> invisible UI.
        static void Watchdog(int f)
        {
            if (eofFrame == f - 1) eofStreak++; else eofStreak = 0;
            if (requested != Backend.Auto) { if (active != requested) Switch(requested, "requested"); return; }
            if (f < eofGraceUntil) return;
            if (active == Backend.EndOfFrame)
            {
                int since = eofFrame < 0 ? f - firstUpdateFrame : f - eofFrame;
                if (since > 30)
                {
                    Switch(Backend.OverlayCamera, "end-of-frame callback silent for " + since + " frames");
                    wantEofRestart = true;
                }
            }
            else if (active == Backend.OverlayCamera && PreferredBackend == Backend.EndOfFrame && eofStreak >= 120 && (!eofFailedProbe || f - eofFailedFrame > 1800))
                Switch(Backend.EndOfFrame, "end-of-frame callback alive again (" + eofStreak + " frames)");
        }

        static int firstUpdateFrame = -1;
        public static bool wantEofRestart;
        static bool eofFailedProbe;
        static int eofFailedFrame;

        public static Backend PreferredBackend { get { return renderer == meshRenderer ? Backend.OverlayCamera : Backend.EndOfFrame; } }

        public static void Switch(Backend b, string why)
        {
            if (b == Backend.Auto) b = PreferredBackend;
            if (b == active) return;
            Event("backend " + active + " -> " + b + ": " + why);
            active = b;
            if (b == Backend.OverlayCamera) MaintainOverlayCamera();
            renderer.Invalidate("backend switch");
            RequestProbe("backend switch", 3);
        }

        public static void Request(Backend b)
        {
            requested = b;
            eofFailedProbe = false;
            Event("backend requested: " + b);
            Switch(b == Backend.Auto ? PreferredBackend : b, "user request");
        }

        // ---------------------------------------------------------------- render callbacks
        public static void OnEndOfFrame()
        {
            eofFrame = Time.frameCount; eofCount++;
            if (active == Backend.EndOfFrame) Submit("endOfFrame");
            ProbeReadback("end-of-frame");
        }

        static void OnCamPreCull(Camera c)
        {
            if (frameOrderFrame != Time.frameCount) { frameOrderFrame = Time.frameCount; lastOrderCount = orderCount; Array.Copy(order, lastOrder, orderCount); orderCount = 0; }
        }

        static void OnCamPost(Camera c)
        {
            try
            {
                camPostCount++; anyCamFrame = Time.frameCount;
                if (c == null) return;
                if (orderCount < order.Length) order[orderCount++] = c;
                if (c == overlayCam)
                {
                    ovlFrame = Time.frameCount; ovlCount++;
                    if (active == Backend.OverlayCamera)
                    {
                        Submit("overlayCamera");
                        if (eofStreak == 0) ProbeReadback("overlay-camera post (end-of-frame silent)");
                    }
                    return;
                }
                if (c == WorldCam())
                {
                    gameCamFrame = Time.frameCount; gameCamCount++;
                    if (active == Backend.GameCameraPost) Submit("gameCameraPost");
                }
            }
            catch (Exception e) { RateLimitedError("camera post", e); }
        }

        // ---------------------------------------------------------------- submission (exactly once per frame)
        static void Submit(string via)
        {
            int f = Time.frameCount;
            if (submitFrame == f) return;
            submitFrame = f;
            submitCount++;
            try
            {
                draw.Clear();
                if (buildWorld != null)
                {
                    try { buildWorld(draw); worldBuildFrame = f; }
                    catch (Exception e) { RateLimitedError("build world layer", e); }
                }
                Color? probe = null;
                if (probePhase >= 0 && probePhase < 2 && f >= probeAtFrame) { probe = ProbeColors[probePhase]; probeDrawnFrame = f; probeDrawnVia = via; }
                var st = new RenderStats();
                bool ok = renderer.Submit(draw, probe, ref st);
                lastStats = st;
                if (ok)
                {
                    submitOkFrame = f;
                    if (awaitFirstAfterRegain)
                    {
                        awaitFirstAfterRegain = false;
                        firstSubmitAfterRegainCount++;
                        maxRegainFrames = Math.Max(maxRegainFrames, f - regainFrame);
                        Event("first successful submit after focus regain #" + focusRegainCount + ": frame " + f + " (+" + (f - regainFrame) + " frames) via " + via +
                              "  batches " + st.batches + " prims " + st.Primitives);
                    }
                }
                else
                {
                    submitFails++;
                    lastError = st.error;
                    if (probe.HasValue) probeDrawnFrame = -1;
                    if (Time.realtimeSinceStartup - lastErrorLog > 5f) { lastErrorLog = Time.realtimeSinceStartup; Event("SUBMIT FAILED via " + via + ": " + st.error); }
                    renderer.Invalidate("submit failed");
                }
            }
            catch (Exception e) { submitFails++; RateLimitedError("submit", e); renderer.Invalidate("submit exception"); }
        }

        static void RateLimitedError(string where, Exception e)
        {
            lastError = where + ": " + e.GetType().Name + ": " + e.Message;
            if (Time.realtimeSinceStartup - lastErrorLog < 5f) return;
            lastErrorLog = Time.realtimeSinceStartup;
            DevLog.Error("render/" + where, e);
        }

        // ---------------------------------------------------------------- pixel verification
        // Proves pixels reached the final image: draw a marker, read it back from the backbuffer after
        // everything else rendered (end of frame). Two frames, two colours, so a coincidence can't pass.
        public const int ProbeInset = 5;
        static readonly Color[] ProbeColors = { new Color(1f, 0f, 1f, 1f), new Color(0f, 1f, 1f, 1f) };
        static int probePhase = -1, probeAtFrame, probeDrawnFrame = -1, probeHits;
        static string probeReason = "", probeDrawnVia = "";
        static readonly Color[] probeRead = new Color[4];
        static Texture2D probeTex;
        public static string probeResult = "not run";
        public static int probeResultFrame = -1;
        public static bool probeOk;
        static int autoSwitchesForProbe;

        public static void RequestProbe(string reason, int delayFrames)
        {
            probeReason = reason;
            probePhase = 0;
            probeHits = 0;
            probeAtFrame = Time.frameCount + Math.Max(1, delayFrames);
            probeDrawnFrame = -1;
        }

        static void ProbeReadback(string where)
        {
            if (probePhase < 0 || probeDrawnFrame != Time.frameCount) return;
            probeDrawnFrame = -1;
            int w = Screen.width, h = Screen.height;
            var prev = RenderTexture.active;
            try
            {
                if (probeTex == null) { probeTex = new Texture2D(1, 1, TextureFormat.RGB24, false); probeTex.hideFlags = HideFlags.HideAndDontSave; }
                RenderTexture.active = null;
                probeTex.ReadPixels(new Rect(ProbeInset, ProbeInset, 1, 1), 0, 0, false);
                probeRead[probePhase * 2] = probeTex.GetPixel(0, 0);
                probeTex.ReadPixels(new Rect(ProbeInset, h - ProbeInset - 1, 1, 1), 0, 0, false);
                probeRead[probePhase * 2 + 1] = probeTex.GetPixel(0, 0);
            }
            catch (Exception e) { RateLimitedError("probe readback", e); probePhase = -1; probeResult = "readback error " + e.Message; return; }
            finally { RenderTexture.active = prev; }

            var want = ProbeColors[probePhase];
            bool hit = Near(probeRead[probePhase * 2], want) || Near(probeRead[probePhase * 2 + 1], want);
            if (hit) probeHits++;
            probePhase++;
            if (probePhase < 2) { probeAtFrame = Time.frameCount + 1; return; }

            probePhase = -1;
            probeOk = probeHits == 2;
            if (probeOk) probeVerifiedCount++; else probeFailedCount++;
            probeResultFrame = Time.frameCount;
            probeResult = (probeOk ? "VERIFIED" : "FAILED") + " via " + probeDrawnVia + ", read at " + where + " (" + probeReason + ")  read " +
                          C(probeRead[0]) + "/" + C(probeRead[1]) + " then " + C(probeRead[2]) + "/" + C(probeRead[3]) + "  " + w + "x" + h;
            Event("pixel probe " + probeResult);
            if (probeOk) { autoSwitchesForProbe = 0; return; }
            if (active == Backend.EndOfFrame) { eofFailedProbe = true; eofFailedFrame = Time.frameCount; }
            // self-heal in Auto mode: try the other backend once per failure chain
            if (requested == Backend.Auto && autoSwitchesForProbe < 2)
            {
                autoSwitchesForProbe++;
                Switch(active == Backend.EndOfFrame ? Backend.OverlayCamera : Backend.EndOfFrame, "pixel probe failed");
            }
        }

        static bool Near(Color a, Color b) { return Mathf.Abs(a.r - b.r) < 0.15f && Mathf.Abs(a.g - b.g) < 0.15f && Mathf.Abs(a.b - b.b) < 0.15f; }
        static string C(Color c) { return "#" + ((int)(c.r * 255)).ToString("X2") + ((int)(c.g * 255)).ToString("X2") + ((int)(c.b * 255)).ToString("X2"); }

        // ---------------------------------------------------------------- overlay camera
        public static Camera overlayCam;
        static float nextDepthCheck;

        static void MaintainOverlayCamera()
        {
            try
            {
                if (overlayCam == null)
                {
                    var go = new GameObject("InsideDev_OverlayCamera");
                    UObj.DontDestroyOnLoad(go);
                    go.hideFlags = HideFlags.DontSave;
                    overlayCam = go.AddComponent<Camera>();
                    overlayCam.clearFlags = CameraClearFlags.Nothing;
                    overlayCam.cullingMask = 0;
                    overlayCam.eventMask = 0;
                    overlayCam.renderingPath = RenderingPath.Forward;
                    overlayCam.hdr = false;
                    overlayCam.useOcclusionCulling = false;
                    overlayCam.orthographic = true;
                    overlayCam.targetTexture = null;
                    overlayCam.rect = new Rect(0, 0, 1, 1);
                    nextDepthCheck = 0;
                    Event("overlay camera created");
                }
                if (!overlayCam.enabled) overlayCam.enabled = true;
                if (Time.realtimeSinceStartup >= nextDepthCheck)
                {
                    nextDepthCheck = Time.realtimeSinceStartup + 1f;
                    float max = -100f;
                    foreach (var c in Camera.allCameras) if (c != null && c != overlayCam) max = Mathf.Max(max, c.depth);
                    if (overlayCam.depth <= max) overlayCam.depth = max + 10f;
                }
            }
            catch (Exception e) { RateLimitedError("overlay camera", e); }
        }

        // gameplay camera (projection for overlays), cached per frame: onPostRender runs for every camera
        static Camera worldCam; static int worldCamFrame = -1;
        public static Camera WorldCam()
        {
            if (worldCamFrame != Time.frameCount) { worldCamFrame = Time.frameCount; try { worldCam = G.Cam(); } catch { worldCam = null; } }
            return worldCam;
        }

        // ---------------------------------------------------------------- cameras (diagnostics)
        static readonly Camera[] order = new Camera[64], lastOrder = new Camera[64];
        static int orderCount, lastOrderCount, frameOrderFrame = -1;
        static float camScanDue;
        public static int cameraCount, enabledCameraCount;
        public static Camera finalCam;

        public static void RefreshCameras()
        {
            camScanDue = Time.realtimeSinceStartup + 2f;
            try
            {
                cameraCount = Camera.allCamerasCount;
                enabledCameraCount = cameraCount;
                finalCam = null;
                for (int i = lastOrderCount - 1; i >= 0; i--)
                {
                    var c = lastOrder[i];
                    if (c != null && c != overlayCam && c.targetTexture == null) { finalCam = c; break; }
                }
            }
            catch { }
        }

        // index of a camera in the last complete frame's observed render order (-1 = did not render)
        public static int ObservedOrder(Camera c)
        {
            for (int i = 0; i < lastOrderCount; i++) if (lastOrder[i] == c) return i;
            return -1;
        }
        public static int ObservedCount { get { return lastOrderCount; } }

        // ---------------------------------------------------------------- input/UI marks (from DevCore.Update)
        public static void MarkInput(bool anyEvent)
        {
            inputPollFrame = Time.frameCount;
            if (anyEvent) inputEventFrame = Time.frameCount;
        }

        public static void MarkUiBuild(bool panelOpen)
        {
            uiBuildFrame = Time.frameCount;
            uiPanelOpen = panelOpen;
            uiCmds = draw.ui.Count;
        }

        // ---------------------------------------------------------------- reporting
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        static string Ago(int frame) { return frame < 0 ? "never" : (Time.frameCount - frame) + "f ago"; }

        public static string CamName(Camera c) { return c == null ? "-" : c.name + (c == overlayCam ? " (ours)" : ""); }

        // One dense log line with everything needed to diagnose a render failure.
        public static void Snapshot(string reason)
        {
            string res;
            bool rok = renderer.ResourcesOk(out res);
            Camera world = null;
            try { world = G.Cam(); } catch { }
            DevLog.Write("[render] " + reason + " | frame " + Time.frameCount + "  screen " + Screen.width + "x" + Screen.height + (Screen.fullScreen ? " fullscreen" : " windowed") +
                         " | backend " + requested + "->" + active + " | worldCam " + CamName(world) + "  finalCam " + CamName(finalCam) + "  cameras " + Camera.allCamerasCount +
                         " | callbacks eof " + eofCount + " (" + Ago(eofFrame) + ")  ovl " + ovlCount + "  gameCam " + gameCamCount + "  allCamPost " + camPostCount +
                         " | submits " + submitCount + " fails " + submitFails + " last ok " + Ago(submitOkFrame) +
                         " | last draw: batches " + lastStats.batches + " passes " + lastStats.passes + " quads " + lastStats.quads + " lines " + lastStats.lines + " glyphs " + lastStats.glyphs +
                         " | resources " + (rok ? "OK" : "BAD") + ": " + res + " | probe " + probeResult);
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            int f = Time.frameCount;
            string res; bool rok = renderer.ResourcesOk(out res);
            sb.Append("frame ").Append(f).Append("  screen ").Append(Screen.width).Append('x').Append(Screen.height).Append(Screen.fullScreen ? " fullscreen" : " windowed")
              .Append("  focused ").Append(focused).Append("  paused ").Append(paused).Append("  runInBackground ").Append(Application.runInBackground).Append('\n');
            sb.Append("backend requested ").Append(requested).Append("  active ").Append(active).Append("  renderer ").Append(renderer.Name).Append('\n');
            foreach (var l in LivenessLines()) sb.Append(l.Key).Append(l.Value ? "  [OK]  " : "  [--]  ").Append(l.Detail).Append('\n');
            sb.Append("RESOURCES        ").Append(rok ? "[OK]  " : "[BAD] ").Append(res).Append('\n');
            sb.Append("PIXEL PROBE      ").Append(probeOk ? "[OK]  " : "[--]  ").Append(probeResult).Append(probeResultFrame >= 0 ? " (" + Ago(probeResultFrame) + ")" : "").Append('\n');
            sb.Append("focus lost ").Append(focusLostCount).Append("  regained ").Append(focusRegainCount).Append("  submits ").Append(submitCount).Append("  fails ").Append(submitFails)
              .Append(lastError != null ? "  last error: " + lastError : "").Append('\n');
            sb.Append("recent render events:\n");
            for (int i = Math.Max(0, events.Count - 25); i < events.Count; i++) sb.Append("  ").Append(events[i]).Append('\n');
            return sb.ToString();
        }

        public struct Live { public string Key; public bool Value; public string Detail; }

        public static List<Live> LivenessLines()
        {
            int f = Time.frameCount;
            var l = new List<Live>();
            l.Add(new Live { Key = "UPDATE ALIVE    ", Value = updateFrame >= f - 1, Detail = "last Update " + Ago(updateFrame) });
            l.Add(new Live { Key = "INPUT ALIVE     ", Value = inputPollFrame >= f - 1, Detail = "polled " + Ago(inputPollFrame) + ", last input event " + Ago(inputEventFrame) });
            l.Add(new Live { Key = "RENDER CALLBACK ", Value = eofFrame >= f - 2 || ovlFrame >= f - 2, Detail = "eof " + Ago(eofFrame) + " (" + eofCount + ")  overlayCam " + Ago(ovlFrame) + " (" + ovlCount + ")  gameCam " + Ago(gameCamFrame) + " (" + gameCamCount + ")" });
            l.Add(new Live { Key = "UI BUILD ALIVE  ", Value = uiBuildFrame >= f - 1 && worldBuildFrame >= f - 2, Detail = "ui " + Ago(uiBuildFrame) + (uiPanelOpen ? " (panel open, " + uiCmds + " cmds)" : " (panel closed)") + "  world/HUD " + Ago(worldBuildFrame) });
            l.Add(new Live { Key = "UI SUBMIT ALIVE ", Value = submitOkFrame >= f - 2, Detail = "last ok " + Ago(submitOkFrame) + " via " + active + "  batches " + lastStats.batches + " passes " + lastStats.passes + " prims " + lastStats.Primitives + " (q" + lastStats.quads + " l" + lastStats.lines + " g" + lastStats.glyphs + ")" });
            return l;
        }
    }
}
