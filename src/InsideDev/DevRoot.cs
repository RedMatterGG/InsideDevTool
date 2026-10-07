using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    public class DevCore
    {
        public static DevCore Instance;
        readonly Overlay overlay = new Overlay();
        readonly Inspector inspector = new Inspector();
        readonly Draw draw = RenderHost.draw;   // command list; GPU resources live in RenderHost.renderer
        readonly UI ui;

        public bool panel;
        bool hud = true;
        string hudKeys, hudKeysFor; int hudKeysFilter = -1;
        static readonly string[] tabs = { "Spawn points", "Levels", "Triggers", "Hidden", "World state", "Audio", "Inspector", "Cheats", "Console", "Diagnostics", "Settings", "Objects", "Explorer", "Graph", "Events", "Sounds", "Audio Timeline", "Wwise API", "History", "Audio DB", "Logic", "Mods", "FSM Graph", "FSM Find" };
        static readonly string[] tabIds = { "spawns", "levels", "triggers", "hidden", "world", "audio", "inspector", "cheats", "console", "diag", "settings", "objects", "explorer", "graph", "events", "sounds", "atimeline", "wwiseapi", "history", "audiodb", "logic", "modified", "fsmgraph", "fsmfind" };
        public readonly Workspace workspace = new Workspace();
        public float uiScale = 1f;
        public const int TabSpawnsI = 0, TabLevelsI = 1, TabTriggersI = 2, TabHiddenI = 3, TabWorldI = 4, TabAudioI = 5, TabInspectorI = 6, TabCheatsI = 7, TabConsoleI = 8, TabDiagI = 9, TabSettingsI = 10, TabObjectsI = 11, TabExplorerI = 12, TabGraphI = 13, TabEventsI = 14, TabSoundsI = 15;
        string spFilter = "", trFilter = "", conInput = "";
        readonly HashSet<int> openSubs = new HashSet<int>();
        bool showFlags;
        public bool captureGameLog;
        float fps, fpsAcc; int fpsFrames;
        float nextBeat;
        public float unityUpdateRT = -100f; public bool fallbackUpdLogged;

        public DevCore()
        {
            Instance = this;
            ui = new UI(draw);
            RenderHost.buildWorld = BuildWorld;
            EditorState.Load();
            uiScale = EditorState.Get("ui.scale", 1f);
            hud = EditorState.Get("hud", true);
            overlay.labelLevel = Mathf.Clamp(EditorState.Get("overlay.labels", 3), 0, 3); overlay.showLabels = overlay.labelLevel > 0;
            overlay.scope = Mathf.Clamp(EditorState.Get("overlay.scope", 1), 0, 4);
            RenderDiag.board = EditorState.Get("diag.board", false);
            TransformGizmo.Load();
            try { GameCode.Boot(); } catch (Exception e) { DevLog.Error("gamecode", e); }
            RenderHost.SetRenderer(EditorState.Get("render.renderer", "gl"));
            Action[] fns = { TabSpawns, TabLevels, TabTriggers, TabHidden, TabWorld, TabAudio, () => inspector.Draw(ui), TabCheats, TabConsole, () => RenderDiag.DrawTab(ui, this), TabSettings, TabObjects, () => SceneExplorer.Draw(ui), () => GraphView.Draw(ui), () => EventsPanel.Draw(ui), () => SoundsPanel.Draw(ui), () => AudioTimelinePanel.Draw(ui), () => WwiseApiPanel.Draw(ui), () => HistoryPanel.Draw(ui), () => AudioDbPanel.Draw(ui), () => LogicPanel.Draw(ui), () => ModifiedPanel.Draw(ui, inspector), () => FsmGraph.Draw(ui), () => FsmFind.Draw(ui) };
            for (int i = 0; i < tabIds.Length; i++) workspace.Register(tabIds[i], tabs[i], fns[i]);
            // Phase 11: history, mods, bookmarks, search
            HistoryPanel.authorMode = EditorState.Get("mode.author", false);
            try { Bookmarks.Load(); Mods.LoadAll(); } catch (Exception e) { DevLog.Error("phase 11 load", e); }
            GlobalSearch.RegisterDefaults(this);
            RegisterCommands();
            try { Application.logMessageReceived += OnUnityLog; } catch (Exception e) { DevLog.Error("log hook", e); }
        }

        void OnUnityLog(string msg, string stack, LogType type)
        {
            if (!captureGameLog && !(type == LogType.Exception && stack != null && stack.Contains("InsideDev"))) return;
            DevLog.Write("[" + type + "] " + msg + (type == LogType.Exception ? "\n" + stack : ""));
        }

        // ---------------------------------------------------------------- per-frame logic
        public void LateUpdate()
        {
            Perf.Run("Shockwave", Shockwave.LateUpdate);
            Perf.Run("AudioTimed", AudioTimed.LateUpdate);
            Perf.Run("AudioCatalog", AudioCatalog.AutoRecord);
            CameraControl.Apply();
            Perf.Run("RenderScale", RenderScale.Tick);
        }

        public void Update()
        {
            try
            {
                fpsAcc += Time.unscaledDeltaTime; fpsFrames++;
                if (fpsAcc >= 0.5f) { fps = fpsFrames / fpsAcc; fpsAcc = 0; fpsFrames = 0; }
                if (Time.realtimeSinceStartup > nextBeat)
                {
                    nextBeat = Time.realtimeSinceStartup + 30f;
                    var st = RenderHost.lastStats;
                    DevLog.Write("heartbeat: frame " + Time.frameCount + " backend=" + RenderHost.active + " submits=" + RenderHost.submitCount + " fails=" + RenderHost.submitFails +
                                 " batches=" + st.batches + " prims=" + st.Primitives + " panel=" + panel + " probe=" + (RenderHost.probeOk ? "ok" : "no"));
                }
                EditorInput.Poll();
                EditorState.Tick();
                if (EditorInput.Key(KeyCode.F1)) { panel = !panel; ui.focus = null; }
                if (EditorInput.ctrl && (Input.GetKeyDown(KeyCode.P) || Input.GetKeyDown(KeyCode.K)) && (ui.focus == null || ui.focus == "palette_q")) { CommandPalette.Toggle(ui); if (CommandPalette.open) panel = true; }
                if (EditorInput.ctrl && !InputCapture.keyboard && EditorInput.Key(KeyCode.Z)) DevLog.Write("[history] " + (EditorInput.shift ? ChangeRecorder.Redo() : ChangeRecorder.Undo()));
                if (EditorInput.ctrl && !InputCapture.keyboard && EditorInput.Key(KeyCode.Y)) DevLog.Write("[history] " + ChangeRecorder.Redo());
                if (EditorInput.Key(KeyCode.BackQuote)) { ShowTab(TabConsoleI); ui.focus = "con"; }
                if (!InputCapture.keyboard)
                {
                    if (EditorInput.Key(KeyCode.F2)) { overlay.CycleTriggers(); DevLog.Write("triggers: " + overlay.TriggerModeLabel); }
                    if (EditorInput.Key(KeyCode.F3)) { overlay.showSolids = !overlay.showSolids; overlay.ForceRefresh(); }
                    if (EditorInput.Key(KeyCode.F4) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) { overlay.scope = (overlay.scope + 1) % 5; EditorState.Set("overlay.scope", overlay.scope); DevLog.Write("[overlay] scope " + Overlay.Scopes[overlay.scope]); }
                    else if (EditorInput.Key(KeyCode.F4)) { overlay.labelLevel = (overlay.labelLevel + 1) % 4; overlay.showLabels = overlay.labelLevel > 0; EditorState.Set("overlay.labels", overlay.labelLevel); }
                    if (EditorInput.Key(KeyCode.F5)) { hud = !hud; EditorState.Set("hud", hud); }
                    if (EditorInput.Key(KeyCode.F6)) overlay.showSavepoints = !overlay.showSavepoints;
                    if (EditorInput.Key(KeyCode.F7)) { G.God = !G.God; DevLog.Write("god " + (G.God ? "ON" : "OFF")); }
                    if (EditorInput.Key(KeyCode.F8)) G.Kill();
                    if (EditorInput.Key(KeyCode.F9)) { draw.flipY = !draw.flipY; DevLog.Write("flipY " + draw.flipY); }
                    if (EditorInput.Key(KeyCode.F10)) { overlay.showHidden = !overlay.showHidden; if (overlay.showHidden) ScanHidden(); DevLog.Write("hidden markers " + overlay.showHidden); }
                    if (EditorInput.Key(KeyCode.F11)) { RenderDiag.board = !RenderDiag.board; EditorState.Set("diag.board", RenderDiag.board); }
                    if (EditorInput.Key(KeyCode.LeftBracket)) G.TimeScale = Step(G.TimeScale, false);
                    if (EditorInput.Key(KeyCode.RightBracket)) G.TimeScale = Step(G.TimeScale, true);
                    if (EditorInput.Key(KeyCode.Backslash)) G.TimeScale = 1f;
                }
                ui.BeginFrame(uiScale);
                if (panel)
                {
                    Cursor.visible = true;
                    Cursor.lockState = CursorLockMode.None;
                    Perf.Run("EditorUI", () => { try { workspace.Build(ui, hud ? 54f / uiScale : 0f, "INSIDE Dev " + Boot.Version); } catch (Exception e) { DevLog.Error("workspace", e); ui.EndArea(); } });
                    try { CommandPalette.Draw(ui); } catch (Exception e) { DevLog.Error("palette", e); ui.EndArea(); CommandPalette.open = false; }
                    try { ui.DrawTooltip(); } catch (Exception e) { DevLog.Error("tooltip", e); }
                }
                InputCapture.Update(panel && ui.focus != null, CameraControl.free);
                CameraControl.Update(ui.mouseOverUI);
                RenderHost.MarkUiBuild(panel);
                Perf.editorOpen = panel;
                Perf.Run("ObjectDatabase", () => ObjectDatabase.Tick(panel));
                Perf.Run("Selection", Selection.Tick);
                // browser-style history: Alt+Left / Alt+Right, or the mouse back / forward side buttons
                if ((EditorInput.alt && EditorInput.Key(KeyCode.LeftArrow)) || Input.GetMouseButtonDown(3)) Selection.Back();
                if ((EditorInput.alt && EditorInput.Key(KeyCode.RightArrow)) || Input.GetMouseButtonDown(4)) Selection.Forward();
                Perf.Run("EventMonitor", EventMonitor.Tick);
                Perf.Run("SoundLibrary", SoundLibrary.Tick);
                Perf.Run("ChangeRecorder", ChangeRecorder.Tick);
                Perf.Run("Mods", Mods.Tick);
                Perf.Run("AudioTrace", AudioTrace.Tick);
                Perf.Run("AudioDb", AudioDb.Tick);
                Perf.Run("AudioRules", AudioRules.BootTick);
                Perf.Run("HiddenScan", PassiveHidden.Tick);
                Perf.Run("AudioRefScan", AudioRefScan.Tick);
                Perf.Run("CodeDeps", CodeDeps.Tick);
                Perf.Run("Logic", LogicPanel.Tick);
                Perf.Run("ModifiedPanel", ModifiedPanel.Tick);
                Perf.Run("ReferenceIndex", ReferenceIndex.Tick);
                Perf.Run("CustomSpawns", CustomSpawns.Tick);
                Perf.Run("Gizmo", () => TransformGizmo.Update(ui.mouseOverUI));
                Perf.Run("WorldPick", () => WorldPick.Update(ui.mouseOverUI, panel));
                if (EditorInput.down[2] && !ui.mouseOverUI) TeleportToMouse();
                Perf.Run("Overlay", overlay.Update);
                Perf.Run("Levels", Levels.ScanStreaming);
                Perf.Run("LiveSavepoints", RefreshLiveSavepoints);
                Perf.Run("Discovery", Discovery.Update);
                Perf.Run("Bridge", () => Remote.Update(this));
                Perf.Run("Extensions", () => { Extensions.LoadAll(this); Extensions.Tick(this); });
                Perf.Run("AudioSpy", () => { if (AudioSpy.Instance == null || !(SoundEngine.GetImplementationDebug() is AudioSpy)) { if (Time.realtimeSinceStartup > nextAudioTry) { nextAudioTry = Time.realtimeSinceStartup + 2f; AudioSpy.Install(); } } });
                Perf.Run("HiddenScan", TickHidden);
                Perf.Run("SpawnCache", SpawnCache.Scan);
            }
            catch (Exception e) { DevLog.Error("Update", e); }
        }

        static float Step(float v, bool up)
        {
            float[] steps = { 0f, 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 4f, 8f };
            if (up) { foreach (var s in steps) if (s > v + 0.001f) return s; return 8f; }
            for (int i = steps.Length - 1; i >= 0; i--) if (steps[i] < v - 0.001f) return steps[i];
            return 0f;
        }

        void TeleportToMouse()
        {
            var cam = G.Cam();
            var ch = G.MainCharacter;
            if (cam == null || ch == null) return;
            Ray r = RenderScale.Ray(cam, Input.mousePosition);
            float z = ch.pos3.z;
            if (Mathf.Abs(r.direction.z) < 1e-4f) return;
            float t = (z - r.origin.z) / r.direction.z;
            if (t < 0) return;
            G.Teleport(r.origin + r.direction * t);
        }

        // ---------------------------------------------------------------- world layer (built by RenderHost once per submitted frame)
        public bool Panel { get { return panel; } }

        void BuildWorld(Draw d)
        {
            try { overlay.Emit(d); } catch (Exception e) { DevLog.Error("Overlay.Emit", e); overlay.showTriggers = overlay.showSolids = overlay.showSavepoints = false; }
            try { WorldPick.Emit(d); } catch (Exception e) { DevLog.Error("pick overlay", e); }
            try { SelectionOverlay.Emit(d); } catch (Exception e) { DevLog.Error("selection overlay", e); SelectionOverlay.enabled = false; }
            try { TransformGizmo.Emit(d); } catch (Exception e) { DevLog.Error("gizmo", e); TransformGizmo.SetGlobal(false); }
            try { if (hud) DrawHud(); } catch (Exception e) { DevLog.Error("HUD", e); hud = false; }
            try { RenderDiag.DrawBoard(d, panel ? workspace.GameViewRect(uiScale) : new Rect(0, 0, Screen.width, Screen.height)); } catch (Exception e) { DevLog.Error("diag board", e); RenderDiag.board = false; }
        }

        void DrawHud()
        {
            var ch = G.MainCharacter;
            string pos = ch != null ? ch.pos3.ToString("F2") : "-";
            int sub, sp;
            string where = G.CurrentSavepoint(out sub, out sp) ? G.SubsceneLabel(sub) + " #" + sp : "-";
            string s = string.Format("INSIDE Dev {8}  |  {0} {1}  |  pos {2}  |  sp {3}  |  ts {4:0.##}{5}  |  triggers {6}  |  {7:0} fps",
                ch != null ? ch.name : "no char", ch != null && ch.isDead ? "(dead)" : "", pos, where, G.TimeScale, G.God ? "  |  GOD" : "",
                overlay.visibleTriggers, fps, Boot.Version);
            string lvl = "level: ?";
            if (sub >= 0)
            {
                string subName = SafeSubName(sub);
                var parts = Levels.ForSubscene(subName);
                var gp = parts.Find(x => x.part.StartsWith("Game", StringComparison.OrdinalIgnoreCase)) ?? (parts.Count > 0 ? parts[0] : null);
                lvl = "level: " + (gp != null ? gp.name + " = " + gp.File + " (scene " + gp.index + ", " + gp.area + ")" : subName + " (no build scene match)");
                if (parts.Count > 1)
                {
                    var others = new List<string>();
                    foreach (var p in parts) if (p != gp) others.Add(p.part + " " + p.File);
                    lvl += "   +  " + string.Join(", ", others.ToArray());
                }
            }
            lvl += "   |   streaming: " + Levels.activeCount + " active / " + Levels.loadedCount + " loaded scenes";
            draw.Fill(new Rect(0, 0, Screen.width, 54), new Color(0, 0, 0, 0.45f));
            draw.ShadowText(10, 4, s, Color.white, 13);
            draw.ShadowText(10, 20, lvl, new Color(0.6f, 1f, 0.7f, 1f), 13);
            if (hudKeysFor != overlay.TriggerModeLabel || hudKeysFilter != WorldPick.filter) { hudKeysFor = overlay.TriggerModeLabel; hudKeysFilter = WorldPick.filter; hudKeys = "F1 panel  F2 triggers: " + hudKeysFor + "  F3 solids  F4 labels  P pick (Shift+P filter: " + WorldPick.Filters[WorldPick.filter] + ")  F6 savepoints  F7 god  F8 kill  F9 flip  F10 hidden  F11 diagnostics  Ins freecam  +/-/0 zoom  [ ] \\ time  MMB teleport  ` console"; }
            draw.ShadowText(10, 36, hudKeys, new Color(0.75f, 0.8f, 0.9f, 1f), 12);
        }

        // ---------------------------------------------------------------- spawn points
        readonly Dictionary<int, Savepoint> liveSp = new Dictionary<int, Savepoint>();
        float nextSpScan; int spStream = -1;
        bool spOnlyLoaded;

        void RefreshLiveSavepoints()
        {
            if (Time.realtimeSinceStartup < nextSpScan && !Levels.Changed(ref spStream)) return;
            nextSpScan = Time.realtimeSinceStartup + (panel ? 2f : 30f);
            liveSp.Clear();
            try
            {
                foreach (var o in UObj.FindObjectsOfType(typeof(Savepoint)))
                {
                    var sp = o as Savepoint;
                    if (sp == null || sp.subsceneIndex < 0 || sp.index < 0) continue;
                    liveSp[sp.subsceneIndex * 1000 + sp.index] = sp;
                }
            }
            catch (Exception e) { DevLog.Error("live savepoints", e); nextSpScan = Time.realtimeSinceStartup + 30f; }
        }

        static string SafeSubName(int s) { try { return SavepointManager.GetSubsceneName(s); } catch { return "?"; } }
        static string SafeAreaName(int s, int i) { try { return SavepointManager.GetSavepointFullAreaName(s, i); } catch { return ""; } }

        bool SpawnPos(int s, int i, out Vector3 p)
        {
            Savepoint live;
            if (liveSp.TryGetValue(s * 1000 + i, out live) && live != null)
            {
                p = live.spawnLocation != null ? live.spawnLocation.position : live.transform.position;
                return true;
            }
            try { p = SavepointManager.GetSavepointPosition(s, i); if (p != Vector3.zero) return true; }
            catch { }
            var ce = SpawnCache.Get(s);
            p = ce != null && i < ce.count ? ce.pos[i] : Vector3.zero;
            return p != Vector3.zero;
        }

        public void TeleportToSpawn(int s, int i)
        {
            Vector3 p;
            bool areaLoaded = liveSp.ContainsKey(s * 1000 + i);
            if (!areaLoaded)
            {
                // teleporting into an unloaded area drops the character into the void: load it first
                var cul = SavepointManager.GetCuller(s);
                areaLoaded = cul != null && cul.IsActive;
            }
            if (SavepointManager.GetSubsceneSavepointCount(s) == 0)
            {
                if (areaLoaded) Discovery.TeleportToAreaCenter(s);
                else Discovery.Enqueue(s, 0, Discovery.Action.Teleport);
                return;
            }
            if (areaLoaded && SpawnPos(s, i, out p)) { G.Teleport(p); return; }
            if (Discovery.Busy) { DevLog.Write("tp: busy loading another area, try again in a moment"); return; }
            DevLog.Write("tp: loading " + G.SubsceneLabel(s) + " first");
            Discovery.Enqueue(s, i, Discovery.Action.Teleport);
        }

        string csName = "", csRename = ""; int csRenameIdx = -1;

        void CustomSpawnsUI()
        {
            ui.Label("CUSTOM SPAWNS  (saved in _mod\\custom_spawns.cfg; launching/continuing the game still uses the game's own savepoint)", UI.Accent);
            ui.BeginRow();
            ui.Label("Name:", null, 40);
            bool enter = ui.TextField("csname", ref csName, Mathf.Max(120, ui.Width - 170));
            if (ui.Button("Save spawn here", 150) || enter) { CustomSpawns.SaveHere(csName.Trim()); csName = ""; }
            ui.EndRow();
            var es = CustomSpawns.entries;
            for (int i = 0; i < es.Count; i++)
            {
                var e = es[i];
                bool rsp = CustomSpawns.respawnEntry == i;
                ui.BeginRow();
                if (ui.Button("Go", 34)) CustomSpawns.Go(i);
                if (ui.Button(rsp ? "Respawn: ON" : "Respawn here", 100, rsp ? UI.TabSel : (Color?)null)) CustomSpawns.SetRespawn(i);
                if (csRenameIdx == i)
                {
                    bool ok = ui.TextField("csren", ref csRename, 160);
                    if (ui.Button("OK", 34) || ok) { CustomSpawns.Rename(i, csRename); csRenameIdx = -1; ui.focus = null; }
                }
                else if (ui.Button("Rename", 60)) { csRenameIdx = i; csRename = e.name; ui.focus = "csren"; }
                if (ui.Button("Delete", 56)) { CustomSpawns.Delete(i); ui.EndRow(); break; }
                ui.Label(e.name + "   " + e.subscene.TrimStart('#') + " #" + e.savepoint + "   @" + e.pos.ToString("F1"), rsp ? UI.Accent : UI.Txt);
                ui.EndRow();
            }
            if (es.Count == 0) ui.Label("   none yet", UI.Dim);
            if (CustomSpawns.lastStatus.Length > 0) ui.Label("   " + CustomSpawns.lastStatus, UI.Dim);
            ui.Label("Respawn here = after a death/respawn in that same area you are moved to this spot. Other areas and game launch are unaffected.", UI.Dim);
            ui.Space(6);
        }

        readonly Dictionary<int, KeyValuePair<int, string>> spHeads = new Dictionary<int, KeyValuePair<int, string>>();
        void TabSpawns()
        {
            if (!G.SavepointsReady) { ui.Label("Savepoint manager not loaded yet — start or continue a game.", UI.Dim); return; }
            CustomSpawnsUI();
            int curSub, curSp;
            G.CurrentSavepoint(out curSub, out curSp);
            int subs = SavepointManager.SubsceneCount;
            int unknown = 0;
            for (int k = 0; k < subs; k++) if (SavepointManager.GetSubsceneSavepointCount(k) < 0 && SpawnCache.Get(k) == null) unknown++;
            ui.BeginRow();
            ui.Label("Current: " + (curSub >= 0 ? G.SubsceneLabel(curSub) + " #" + curSp : "?"), UI.Accent, 330);
            if (ui.Button("Respawn")) GameManager.Respawn();
            if (ui.Button("Reload")) GameManager.ReloadScene();
            if (ui.Button("Expand all")) for (int k = 0; k < subs; k++) openSubs.Add(k);
            if (ui.Button("Collapse")) openSubs.Clear();
            ui.EndRow();
            ui.BeginRow();
            if (Discovery.Busy)
            {
                ui.Label(Discovery.status, new Color(1f, 0.85f, 0.4f, 1f), ui.Width - 80);
                if (ui.Button("Cancel", 70)) Discovery.Cancel();
            }
            else
            {
                ui.Label(unknown > 0 ? unknown + " area(s) never loaded yet - their savepoints are unknown (?)." : "All areas discovered (" + SpawnCache.KnownAreas + " cached).", unknown > 0 ? new Color(1f, 0.85f, 0.4f, 1f) : UI.Dim, ui.Width - 130);
                if (unknown > 0 && ui.Button("Discover all", 120)) Discovery.DiscoverAll();
            }
            ui.EndRow();
            ui.BeginRow();
            ui.Label("Filter:", null, 44);
            ui.TextField("spf", ref spFilter, 260);
            spOnlyLoaded = ui.Toggle(spOnlyLoaded, "only loaded areas");
            ui.EndRow();
            ui.Label("Spawn = respawn there (loads the area; also sets your save's spawn point).  TP = move the character (loads the area first if needed).", UI.Dim);
            string f = spFilter.ToLowerInvariant();
            ui.BeginScroll("sp", ui.Remaining);
            for (int s = 0; s < subs; s++)
            {
                string label = G.SubsceneLabel(s);
                int gameN = SavepointManager.GetSubsceneSavepointCount(s);
                var ce = SpawnCache.Get(s);
                int n = gameN >= 0 ? gameN : (ce != null ? ce.count : -1);
                bool anyLive = false;
                for (int i = 0; i < n && !anyLive; i++) anyLive = liveSp.ContainsKey(s * 1000 + i);
                if (spOnlyLoaded && !anyLive) continue;
                if (f.Length > 0 && !label.ToLowerInvariant().Contains(f)) continue;
                bool open = n > 0 && (openSubs.Contains(s) || f.Length > 0 || spOnlyLoaded);
                // row text cached per area (rebuilt when any input changes): building ~100 rows of strings plus a
                // level lookup every frame was a steady source of garbage
                int hk = (n * 31 + gameN) * 8 + (open ? 1 : 0) + (anyLive ? 2 : 0) + (s == curSub ? 4 : 0);
                KeyValuePair<int, string> hc0; string head;
                if (spHeads.TryGetValue(s, out hc0) && hc0.Key == hk) head = hc0.Value;
                else
                {
                    var gp = Levels.ForSubscene(SafeSubName(s)).Find(x => x.part.StartsWith("Game", StringComparison.OrdinalIgnoreCase));
                    string cnt = gameN > 0 ? gameN.ToString() : gameN == 0 ? "0 - no savepoint, secret/overlay area" : (ce != null ? ce.count + ", cached" : "?");
                    head = (n > 0 ? (open ? "v " : "> ") : "  ") + s.ToString("D3") + "  " + label + "  (" + cnt + ")" + (gp != null ? "   " + gp.File : "") + (anyLive ? "   [loaded]" : "") + (s == curSub ? "   <- here" : "");
                    spHeads[s] = new KeyValuePair<int, string>(hk, head);
                }
                ui.BeginRow();
                Color hc = s == curSub ? UI.Accent : anyLive ? new Color(0.6f, 1f, 0.7f, 1f) : n < 0 ? new Color(1f, 0.85f, 0.4f, 1f) : UI.Txt;
                if (ui.Item(head, hc, ui.Width - (gameN < 0 ? 170 : gameN == 0 ? 145 : 70)))
                {
                    if (openSubs.Contains(s)) openSubs.Remove(s); else openSubs.Add(s);
                }
                if (gameN < 0 && ui.Button("Discover", 95)) Discovery.Enqueue(s, 0, Discovery.Action.Discover);
                if (gameN == 0)
                {
                    if (ui.Button("TP", 40)) TeleportToSpawn(s, 0);
                    if (ui.Button("Spawn near", 95)) G.LoadSavepoint(s, 0);
                }
                else if (ui.Button("Spawn", 65)) G.LoadSavepoint(s, 0);
                ui.EndRow();
                if (!open) continue;
                for (int i = 0; i < n; i++)
                {
                    Vector3 p;
                    bool known = SpawnPos(s, i, out p);
                    bool live = liveSp.ContainsKey(s * 1000 + i);
                    string type = "";
                    if (gameN >= 0) { try { type = SavepointManager.GetSavepointCharacterType(s, i).ToString(); if (SavepointManager.IsSavepointCheckpointOnly(s, i)) type += ", checkpoint-only"; } catch { } }
                    else if (ce != null && i < ce.count) type = ce.info[i];
                    ui.BeginRow();
                    ui.Space(22);
                    bool here = s == curSub && i == curSp;
                    string area = gameN >= 0 ? SafeAreaName(s, i) : SafeSubName(s) + " #" + i;
                    ui.Label((here ? "> " : "  ") + "#" + i + "   " + area + "   " + type + (known ? "   @" + p.ToString("F1") : ""), here ? UI.Accent : (live ? UI.Txt : UI.Dim), ui.Width - 124);
                    if (ui.Button("Spawn", 60)) G.LoadSavepoint(s, i);
                    if (ui.Button("TP", 40)) TeleportToSpawn(s, i);
                    ui.EndRow();
                }
            }
            ui.EndScroll();
        }

        // ---------------------------------------------------------------- hidden objects
        public class HiddenEntry { public GameObject go; public string kind; public string path; }
        public readonly List<HiddenEntry> hidden = new List<HiddenEntry>();
        float nextHiddenScan;
        string hdFilter = "";
        bool hdOffObjects = true, hdNoRender = true, hdOffColliders = true;

        // Phase 12: background hidden-marker refresh is time-sliced over the object database snapshot (no forced
        // full pass): it restarts when the database finished a new pass (streaming triggers one) or the user changed
        // something, or every 30 s (5 s with the editor open); 3 ms per frame at most.
        readonly List<HiddenEntry> hiddenNext = new List<HiddenEntry>();
        int hiddenIdx = -1, hiddenPass = -1, hiddenHist = -1, hiddenStream = -1;
        void TickHidden()
        {
            if (!overlay.showHidden) { hiddenIdx = -1; return; }
            if (Levels.Changed(ref hiddenStream)) ObjectDatabase.Demand(20f);   // get a fresh database pass after streaming
            if (hiddenIdx < 0)
            {
                bool due = Time.realtimeSinceStartup > nextHiddenScan || ObjectDatabase.completedPasses != hiddenPass || ChangeRecorder.Version != hiddenHist;
                if (!due || !ObjectDatabase.Ready) return;
                hiddenPass = ObjectDatabase.completedPasses; hiddenHist = ChangeRecorder.Version;
                nextHiddenScan = Time.realtimeSinceStartup + (panel ? 5f : 30f);
                hiddenNext.Clear(); hiddenIdx = 0;
            }
            var all = ObjectDatabase.all;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), budget = System.Diagnostics.Stopwatch.Frequency * 3 / 1000;
            while (hiddenIdx < all.Count)
            {
                var r = all[hiddenIdx++];
                if (r.t != null) CheckHidden(r.t, hiddenNext);
                if ((hiddenIdx & 63) == 0 && System.Diagnostics.Stopwatch.GetTimestamp() - t0 > budget) return;
            }
            hidden.Clear(); hidden.AddRange(hiddenNext); hiddenNext.Clear();
            overlay.hiddenEntries = hidden;
            hiddenIdx = -1;
        }

        static void CheckHidden(Transform t, List<HiddenEntry> into)
        {
            var g = t.gameObject;
            if (!g.activeSelf)
            {
                if (t.parent == null || t.parent.gameObject.activeInHierarchy) into.Add(new HiddenEntry { go = g, kind = "off" });
                return;
            }
            if (!g.activeInHierarchy) return;
            var rs = g.GetComponents<Renderer>();
            if (rs.Length > 0)
            {
                bool any = false;
                foreach (var r in rs) if (r != null && r.enabled) { any = true; break; }
                if (!any) into.Add(new HiddenEntry { go = g, kind = "renderer off" });
            }
            foreach (var c in g.GetComponents<Collider>())
                if (c != null && !c.enabled) { into.Add(new HiddenEntry { go = g, kind = c.isTrigger ? "trigger off" : "collider off" }); break; }
        }

        // full synchronous scan (Hidden tab "Scan" button, bridge, enabling the markers)
        public void ScanHidden()
        {
            nextHiddenScan = Time.realtimeSinceStartup + 3f;
            hidden.Clear();
            try
            {
                foreach (var t in Inspector.AllSceneTransforms())
                {
                    if (t == null) continue;
                    var g = t.gameObject;
                    if (!g.activeSelf)
                    {
                        // report only the object that is switched off, not every child under it
                        if (t.parent == null || t.parent.gameObject.activeInHierarchy)
                            hidden.Add(new HiddenEntry { go = g, kind = "off" });
                        continue;
                    }
                    if (!g.activeInHierarchy) continue;
                    var rs = g.GetComponents<Renderer>();
                    if (rs.Length > 0)
                    {
                        bool any = false;
                        foreach (var r in rs) if (r != null && r.enabled) { any = true; break; }
                        if (!any) hidden.Add(new HiddenEntry { go = g, kind = "renderer off" });
                    }
                    foreach (var c in g.GetComponents<Collider>())
                        if (c != null && !c.enabled) { hidden.Add(new HiddenEntry { go = g, kind = c.isTrigger ? "trigger off" : "collider off" }); break; }
                }
            }
            catch (Exception e) { DevLog.Error("ScanHidden", e); }
            overlay.hiddenEntries = hidden;
        }

        void TabHidden()
        {
            ui.BeginRow();
            if (ui.Button("Scan loaded scenes")) ScanHidden();
            ui.Label("Filter:", null, 44);
            ui.TextField("hdf", ref hdFilter, 200);
            bool m = ui.Toggle(overlay.showHidden, "markers in world [F10]");
            if (m != overlay.showHidden) { overlay.showHidden = m; if (m) ScanHidden(); }
            ui.EndRow();
            ui.BeginRow();
            hdOffObjects = ui.Toggle(hdOffObjects, "objects switched off");
            hdNoRender = ui.Toggle(hdNoRender, "invisible (renderers off)");
            hdOffColliders = ui.Toggle(hdOffColliders, "disabled triggers/colliders");
            ui.EndRow();
            ui.Label("Only what is in the currently loaded scenes exists in memory - other areas' objects appear once they stream in.", UI.Dim);
            var ch = G.MainCharacter;
            Vector3 origin = ch != null ? ch.pos3 : Vector3.zero;
            var list = new List<KeyValuePair<float, HiddenEntry>>();
            string f = hdFilter.ToLowerInvariant();
            foreach (var h in hidden)
            {
                if (h.go == null) continue;
                bool isOff = h.kind == "off", isRen = h.kind == "renderer off";
                if ((isOff && !hdOffObjects) || (isRen && !hdNoRender) || (!isOff && !isRen && !hdOffColliders)) continue;
                if (f.Length > 0 && !h.go.name.ToLowerInvariant().Contains(f)) continue;
                list.Add(new KeyValuePair<float, HiddenEntry>((h.go.transform.position - origin).magnitude, h));
            }
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            ui.Label(list.Count + " hidden item(s), nearest first" + (Changes.list.Count > 0 ? "   |   " + Changes.list.Count + " changed by you (purple, top)" : ""), UI.Dim);
            if (Changes.list.Count > 0)
            {
                ui.BeginRow();
                ui.Label("CHANGED BY YOU", Changes.Purple, ui.Width - 100);
                if (ui.Button("Revert all", 90)) Changes.RevertAll();
                ui.EndRow();
                var chs = Changes.list.ToArray();
                ui.VirtualList("hdch", chs.Length, UI.RowPitch, Mathf.Min(chs.Length * UI.RowPitch + 4, ui.Remaining * 0.35f), k =>
                {
                    var chg = chs[k];
                    if (chg.go == null) { Changes.list.Remove(chg); return; }
                    bool cur = Changes.Current(chg);
                    ui.BeginRow();
                    if (ui.Button(cur ? "Hide" : "Show", 50)) Changes.Set(chg, !cur);
                    if (ui.Button("Revert", 60)) Changes.Revert(chg);
                    if (ui.Button("Go", 30)) G.Teleport(chg.go.transform.position);
                    if (ui.Item(chg.go.name + "   [" + chg.what + ": " + (chg.original ? "on" : "off") + " -> " + (cur ? "on" : "off") + "]   " + Parent(chg.go), Changes.Purple))
                        SelectInInspector(chg.go);
                    ui.EndRow();
                });
                ui.Space(4);
            }
            list.RemoveAll(kv => kv.Value.go == null || Changes.IsChanged(kv.Value.go));
            ui.VirtualList("hd", list.Count, UI.RowPitch, ui.Remaining, i =>
            {
                var h = list[i].Value;
                var g = h.go;
                if (g == null) return;
                ui.BeginRow();
                bool visibleNow = h.kind == "off" ? g.activeSelf : h.kind == "renderer off" ? AnyRenderer(g) : AnyCollider(g);
                if (ui.Button(visibleNow ? "Hide" : "Show", 50)) SetShown(h, !visibleNow);
                if (ui.Button("Go", 30)) G.Teleport(g.transform.position);
                if (ui.Item(list[i].Key.ToString("F1", CultureInfo.InvariantCulture) + "m   " + g.name + "   [" + h.kind + "]   " + Parent(g), visibleNow ? new Color(0.6f, 1f, 0.7f, 1f) : new Color(1f, 0.45f, 0.9f, 1f)))
                    SelectInInspector(g);
                ui.EndRow();
            });
        }

        static string Parent(GameObject g) { var p = g.transform.parent; return p == null ? "" : "in " + p.name; }
        static bool AnyRenderer(GameObject g) { foreach (var r in g.GetComponents<Renderer>()) if (r != null && r.enabled) return true; return false; }
        static bool AnyCollider(GameObject g) { foreach (var c in g.GetComponents<Collider>()) if (c != null && c.enabled) return true; return false; }

        static void SetShown(HiddenEntry h, bool on)
        {
            var g = h.go;
            if (h.kind == "off") { Changes.Record(g, null, "active", g.activeSelf); g.SetActive(on); }
            else if (h.kind == "renderer off") { foreach (var r in g.GetComponents<Renderer>()) if (r != null) { Changes.Record(g, r, "renderer", r.enabled); r.enabled = on; } }
            else { foreach (var c in g.GetComponents<Collider>()) if (c != null) { Changes.Record(g, c, c.isTrigger ? "trigger" : "collider", c.enabled); c.enabled = on; } }
            DevLog.Write((on ? "show " : "hide ") + g.name + " [" + h.kind + "]");
        }

        // ---------------------------------------------------------------- world state (persistent flags)
        string wsFilter = "";
        readonly List<PersistentBool> pbools = new List<PersistentBool>();
        float nextPbScan;

        void TabWorld()
        {
            if (Time.realtimeSinceStartup > nextPbScan)
            {
                nextPbScan = Time.realtimeSinceStartup + 2f;
                pbools.Clear();
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(PersistentBool)))
                {
                    var pb = o as PersistentBool;
                    if (pb != null && pb.gameObject.hideFlags == HideFlags.None) pbools.Add(pb);
                }
                pbools.Sort((a, b) => string.CompareOrdinal(a.booleanName, b.booleanName));
            }
            int ps, pp;
            try { Savegame.GetCurrentProgression(out ps, out pp); } catch { ps = pp = -1; }
            ui.Label("Progression: " + (ps >= 0 ? G.SubsceneLabel(ps) + " #" + pp : "?") + "     save hash " + Savegame.SaveHash, UI.Accent);
            ui.Label("PersistentBool = Playdead's named world flags (secrets taken, doors, events). Saved into your savegame at the next savepoint.", UI.Dim);
            ui.BeginRow();
            ui.Label("Filter:", null, 44);
            ui.TextField("wsf", ref wsFilter, 260);
            ui.EndRow();
            string f = wsFilter.ToLowerInvariant();
            ui.BeginScroll("ws", ui.Remaining);
            int shown = 0;
            foreach (var pb in pbools)
            {
                if (pb == null) continue;
                string name = pb.booleanName ?? "?";
                if (f.Length > 0 && !(name + " " + pb.gameObject.name).ToLowerInvariant().Contains(f)) continue;
                shown++;
                ui.BeginRow();
                bool v = ui.Toggle(pb.current, "", 22);
                if (v != pb.current) { pb.current = v; DevLog.Write("persistent '" + name + "' = " + v); }
                if (ui.Item(name + "   (" + pb.gameObject.name + (pb.gameObject.activeInHierarchy ? "" : ", hidden") + ")", pb.current ? new Color(0.6f, 1f, 0.7f, 1f) : UI.Txt))
                { inspector.Select(pb.gameObject); ShowTab(TabInspectorI); }
                ui.EndRow();
            }
            if (shown == 0) ui.Label("No persistent flags in the loaded scenes.", UI.Dim);
            ui.EndScroll();
        }

        // ---------------------------------------------------------------- audio
        float nextAudioTry;
        int audioSub;
        string auFilter = "", auPost = "", auCue = "";
        bool auPaused;
        List<AudioSpy.Entry> auFrozen;
        static readonly string[] audioSubs = { "Live log", "Audio-driven systems (all)", "Game -> audio (event posts in FSMs)" };

        void TabAudio()
        {
            AudioLinks.Scan();
            ui.BeginRow();
            ui.Label(AudioSpy.Instance != null ? "spy active   posts " + AudioSpy.totalPosts + "   callbacks " + AudioSpy.totalCallbacks : "spy not installed yet (sound engine not ready)", AudioSpy.Instance != null ? UI.Dim : Color.red, 330);
            try
            {
                var mm = PersistentBehaviour<GlobalAudio>.instance.music;
                ui.Label(string.Format(CultureInfo.InvariantCulture, "music position {0:0.00}s / loop {1:0.00}s   beat {2}", mm.GetMusicPosition_s(), mm.GetLoopLength_s(), mm.LastBeat), UI.Dim, 330);
            }
            catch { }
            ui.EndRow();
            audioSub = ui.Tabs(audioSub, audioSubs);
            if (audioSub == 0) AudioLog();
            else if (audioSub == 1) AudioTimed.Draw(ui, this);
            else AudioPosts();
        }

        readonly List<AudioSpy.Entry> auRows = new List<AudioSpy.Entry>(); readonly List<string> auTexts = new List<string>();
        List<AudioSpy.Entry> auSrc; int auCount = -1; float auLastT; string auFilterUsed;
        void AudioLog()
        {
            ui.BeginRow();
            ui.Label("Filter:", null, 44);
            ui.TextField("auf", ref auFilter, 180);
            AudioSpy.logStates = ui.Toggle(AudioSpy.logStates, "states");
            AudioSpy.logSwitches = ui.Toggle(AudioSpy.logSwitches, "switches");
            AudioSpy.logRtpc = ui.Toggle(AudioSpy.logRtpc, "rtpc+beats");
            AudioSpy.logVoice = ui.Toggle(AudioSpy.logVoice, "boy voice/foley");
            bool p = ui.Toggle(auPaused, "pause");
            if (p != auPaused) { auPaused = p; auFrozen = p ? new List<AudioSpy.Entry>(AudioSpy.log) : null; }
            if (ui.Button("Clear", 50)) AudioSpy.log.Clear();
            ui.EndRow();
            ui.BeginRow();
            ui.Label("Play event:", null, 76);
            bool e1 = ui.TextField("aupost", ref auPost, 240);
            if ((ui.Button("Post (global)") || e1) && auPost.Trim().Length > 0) AudioSpy.Post(auPost.Trim(), null);
            if (ui.Button("Post on boy") && auPost.Trim().Length > 0) { var ch = G.MainCharacter; AudioSpy.Post(auPost.Trim(), ch != null ? ch.gameObject : null); }
            ui.Label("Inject cue:", null, 72);
            bool e2 = ui.TextField("aucue", ref auCue, 160);
            if ((ui.Button("Inject") || e2) && auCue.Trim().Length > 0) AudioSpy.InjectCue(auCue.Trim());
            if (ui.Button("Beat")) AudioSpy.InjectBeat();
            ui.EndRow();
            ui.Label("CUE = marker/user cue reached the game (-> receiver, waiting FSM).  TIMED = a music-position trigger fired (-> its signals).  * = injected by you.", UI.Dim);
            var src = auFrozen ?? AudioSpy.log;
            // rebuilt only when the log or the filter changes (building every line each frame made ~0.5 MB of
            // garbage per frame -> a 140 ms garbage collection every couple of seconds)
            float lastT = src.Count > 0 ? src[src.Count - 1].time : 0f;
            if (src != auSrc || src.Count != auCount || lastT != auLastT || auFilter != auFilterUsed)
            {
                auSrc = src; auCount = src.Count; auLastT = lastT; auFilterUsed = auFilter;
                string f = auFilter.ToLowerInvariant();
                auRows.Clear(); auTexts.Clear();
                for (int i = src.Count - 1; i >= 0; i--)
                {
                    var e = src[i];
                    string line = e.frame + "  " + e.kind.PadRight(7) + " " + e.name + "   @" + e.source + (string.IsNullOrEmpty(e.detail) ? "" : "   " + e.detail);
                    if (f.Length > 0 && !line.ToLowerInvariant().Contains(f)) continue;
                    auRows.Add(e); auTexts.Add(line);
                }
            }
            var rows = auRows; var texts = auTexts;
            ui.VirtualList("aulog", rows.Count, UI.RowPitch, ui.Remaining, i =>
            {
                var e = rows[i];
                Color c = e.kind == "TIMED" ? new Color(1f, 0.5f, 0.3f, 1f) : e.kind.StartsWith("CUE") ? new Color(1f, 0.85f, 0.3f, 1f) : e.kind == "post" ? UI.Txt : e.kind.StartsWith("BEAT") || e.kind == "beat" ? UI.Accent : UI.Dim;
                ui.BeginRow();
                if (ui.Item(texts[i], c)) { auPost = e.kind == "post" ? e.name : auPost; if (e.kind.StartsWith("CUE")) auCue = e.name; }
                ui.EndRow();
            });
        }

        void AudioCues()
        {
            ui.BeginRow();
            if (ui.Button("Rescan")) AudioLinks.Scan(true);
            ui.Label(AudioLinks.waits.Count + " state(s) in loaded FSMs wait for a music cue / marker / beat. Green = waiting RIGHT NOW (inject its cue to advance it).", UI.Dim);
            ui.EndRow();
            ui.BeginScroll("aucues", ui.Remaining);
            var sorted = new List<AudioLinks.Wait>(AudioLinks.waits);
            sorted.Sort((a, b) => (IsWaiting(b) ? 1 : 0).CompareTo(IsWaiting(a) ? 1 : 0));
            foreach (var w in sorted)
            {
                if (w.fsm == null) continue;
                bool live = IsWaiting(w);
                ui.BeginRow();
                if (w.isCue) { if (ui.Button("Inject cue", 90)) { if (w.any || string.IsNullOrEmpty(w.cue)) AudioSpy.InjectCue("__any__"); else AudioSpy.InjectCue(w.cue); } }
                else if (ui.Button("Inject beat", 90)) AudioSpy.InjectBeat();
                if (ui.Button("Send event", 90)) { DevLog.Write("fsm " + w.fsm.gameObject.name + " <- " + w.finishEvent); ChangeRecorder.Action(w.fsm.gameObject, "fsmEvent", w.fsm.FsmName, w.finishEvent, "audio"); w.fsm.SendEvent(w.finishEvent); }
                if (ui.Item((w.isCue ? "cue '" + (w.any ? "*any*" : w.cue) + "'" : "beat") + "  ->  " + w.fsm.gameObject.name + "/" + w.fsm.FsmName + "  state [" + w.state + "]  fires '" + w.finishEvent + "'" + (live ? "   <- WAITING NOW" : "") + (w.fsm.gameObject.activeInHierarchy ? "" : "  (object hidden)"),
                    live ? new Color(0.5f, 1f, 0.6f, 1f) : UI.Txt))
                { inspector.Select(w.fsm.gameObject); ShowTab(TabInspectorI); }
                ui.EndRow();
            }
            if (AudioLinks.waits.Count == 0) ui.Label("No audio-driven states in the loaded scenes (they appear as areas stream in).", UI.Dim);
            ui.EndScroll();
        }

        static bool IsWaiting(AudioLinks.Wait w) { try { return w.fsm != null && w.fsm.ActiveStateName == w.state && w.fsm.enabled && w.fsm.gameObject.activeInHierarchy; } catch { return false; } }

        void AudioPosts()
        {
            ui.BeginRow();
            if (ui.Button("Rescan")) AudioLinks.Scan(true);
            ui.Label(AudioLinks.posts.Count + " FSM actions that post a Wwise event in the loaded scenes.  " + AudioSpy.knownEvents.Count + " event names known.", UI.Dim);
            ui.EndRow();
            ui.BeginScroll("auposts", ui.Remaining);
            foreach (var pr in AudioLinks.posts)
            {
                if (pr.fsm == null) continue;
                ui.BeginRow();
                if (ui.Button("Play", 50)) AudioSpy.Post(pr.eventName, pr.fsm.gameObject);
                if (ui.Item("'" + pr.eventName + "'   by " + pr.action + " in " + pr.fsm.gameObject.name + "/" + pr.fsm.FsmName + " [" + pr.state + "]", UI.Txt))
                { inspector.Select(pr.fsm.gameObject); ShowTab(TabInspectorI); }
                ui.EndRow();
            }
            ui.EndScroll();
        }

        // ---------------------------------------------------------------- levels (build scenes)
        string lvFilter = "";
        bool lvOnlyLoaded;
        void TabLevels()
        {
            Levels.EnsureLoaded();
            ui.BeginRow();
            ui.Label("Filter:", null, 44);
            ui.TextField("lvf", ref lvFilter, 260);
            lvOnlyLoaded = ui.Toggle(lvOnlyLoaded, "only loaded");
            ui.EndRow();
            int lvHead = Levels.scenes.Count * 1000000 + Levels.activeCount * 1000 + Levels.loadedCount;
            if (lvHead != lvHeadKey) { lvHeadKey = lvHead; lvHeadText = Levels.scenes.Count + " build scenes.  File levelN = scene N+1 (scene 0 is in mainData).  " + Levels.activeCount + " active / " + Levels.loadedCount + " loaded right now."; }
            ui.Label(lvHeadText, UI.Dim);
            string f = lvFilter.ToLowerInvariant();
            var subIndex = SubsceneIndexByBase();
            // rows: text and lookups cached per scene (rebuilt when its state changes); 253 rows of string building
            // per frame were this panel's garbage
            if (lvRows.Count != Levels.scenes.Count && lvRows.Count > 0 || subIndex.Count != lvSubCount) { lvRows.Clear(); lvSubCount = subIndex.Count; }
            lvVisible.Clear();
            for (int i = 0; i < Levels.scenes.Count; i++)
            {
                var sc = Levels.scenes[i];
                string st = Levels.StateOf(sc);
                if (lvOnlyLoaded && (st == "" || st == "unloaded")) continue;
                LvRow row;
                if (i >= lvRows.Count) { int sub; row = new LvRow { key = (sc.path + " " + sc.File).ToLowerInvariant(), sub = subIndex.TryGetValue(sc.baseName.ToLowerInvariant(), out sub) ? sub : -1 }; lvRows.Add(row); }
                else row = lvRows[i];
                if (f.Length > 0 && !row.key.Contains(f)) continue;
                if (row.state != st || row.text == null) { row.state = st; row.text = sc.index.ToString("D3") + "  " + sc.File.PadRight(9) + "  " + sc.name + "   " + sc.area + (st.Length > 0 ? "   [" + st + "]" : ""); }
                lvVisible.Add(i);
            }
            var vis = lvVisible;
            ui.VirtualList("lv", vis.Count, UI.RowPitch, ui.Remaining, k =>
            {
                int i = vis[k]; var row = lvRows[i]; string st = row.state;
                Color c = st == "active" ? new Color(0.6f, 1f, 0.7f, 1f) : st == "loaded" ? new Color(0.9f, 0.9f, 0.5f, 1f) : st == "streaming" ? UI.Accent : UI.Dim;
                ui.BeginRow();
                ui.Label(row.text, c, ui.Width - 70);
                if (row.sub >= 0 && SavepointManager.GetSubsceneSavepointCount(row.sub) > 0)
                {
                    if (ui.Button("Spawn", 60)) G.LoadSavepoint(row.sub, 0);
                }
                ui.EndRow();
            });
        }

        sealed class LvRow { public string key, text, state; public int sub; }
        readonly List<LvRow> lvRows = new List<LvRow>(); readonly List<int> lvVisible = new List<int>();
        int lvHeadKey = -1, lvSubCount = -1; string lvHeadText = "";
        Dictionary<string, int> subIdxCache;
        Dictionary<string, int> SubsceneIndexByBase()
        {
            if (subIdxCache != null && subIdxCache.Count > 0) return subIdxCache;
            subIdxCache = new Dictionary<string, int>();
            if (!G.SavepointsReady) return subIdxCache;
            for (int s = 0; s < SavepointManager.SubsceneCount; s++)
            {
                string b = SafeSubName(s).TrimStart('#').ToLowerInvariant();
                if (!subIdxCache.ContainsKey(b)) subIdxCache[b] = s;
            }
            return subIdxCache;
        }

        void TabTriggers()
        {
            ui.BeginRow();
            ui.Label("Filter:", null, 44);
            ui.TextField("trf", ref trFilter, 300);
            if (ui.Button("Refresh")) overlay.ForceRefresh();
            ui.EndRow();
            // nearest-first list rebuilt 4x per second (distances change as the boy moves), not every frame
            if (Time.realtimeSinceStartup - trAt > 0.25f || trFilter != trFilterUsed)
            {
                trAt = Time.realtimeSinceStartup; trFilterUsed = trFilter;
                var ch = G.MainCharacter;
                var cam = G.Cam();
                Vector3 origin = ch != null ? ch.pos3 : (cam != null ? cam.transform.position : Vector3.zero);
                trList.Clear();
                string f = trFilter.ToLowerInvariant();
                foreach (var e in overlay.entries)
                {
                    if (!e.trigger || e.col == null || !e.col.gameObject.activeInHierarchy) continue;
                    if (f.Length > 0 && !(e.name + " " + e.types).ToLowerInvariant().Contains(f)) continue;
                    trList.Add(new KeyValuePair<float, ColliderEntry>((e.col.bounds.center - origin).magnitude, e));
                }
                trList.Sort((x, y) => x.Key.CompareTo(y.Key));
                trText.Clear();
                foreach (var kv in trList) trText.Add(kv.Key.ToString("F1", CultureInfo.InvariantCulture) + "m   " + kv.Value.name + "   <" + kv.Value.types + ">");
                trHead = trList.Count + " active triggers, nearest first" + (overlay.showTriggers ? "" : "   (trigger scan OFF — press F2)");
            }
            var list = trList; var texts = trText;
            ui.Label(trHead, UI.Dim);
            ui.VirtualList("tr", list.Count, UI.RowPitch, ui.Remaining, i =>
            {
                var e = list[i].Value;
                ui.BeginRow();
                if (ui.Button("Go", 30) && e.col != null) G.Teleport(e.col.bounds.center);
                if (ui.Item(texts[i], e.color) && e.col != null)
                    SelectInInspector(e.col.gameObject);
                ui.EndRow();
            });
        }

        float trAt = -9; string trFilterUsed, trHead = "";
        readonly List<KeyValuePair<float, ColliderEntry>> trList = new List<KeyValuePair<float, ColliderEntry>>();
        readonly List<string> trText = new List<string>();

        void TabCheats()
        {
            ui.BeginRow();
            bool god = ui.Toggle(G.God, "God mode (Playdead InvincibleBoy flag)  [F7]");
            if (god != G.God) G.God = god;
            if (ui.Button("Kill [F8]")) G.Kill();
            ui.EndRow();

            ui.Label("Time scale: " + G.TimeScale.ToString("0.00") + "     [ / ] step,  \\ reset");
            float ts = ui.Slider("ts", G.TimeScale, 0f, 4f);
            if (Mathf.Abs(ts - G.TimeScale) > 0.001f) G.TimeScale = ts;
            ui.BeginRow();
            foreach (var v in new[] { 0f, 0.1f, 0.25f, 0.5f, 1f, 2f, 4f })
                if (ui.Button(v.ToString("0.##"), 50)) G.TimeScale = v;
            ui.EndRow();

            ui.Space();
            ui.Label("Camera   " + CameraControl.Status(), UI.Dim);
            ui.BeginRow();
            if (ui.Button(CameraControl.free ? "Exit free camera [Ins]" : "Free camera [Ins]", -2, CameraControl.free ? UI.TabSel : (Color?)null)) CameraControl.ToggleFree();
            if (ui.Button("Zoom in [+]")) CameraControl.ZoomBy(1f / 1.15f);
            if (ui.Button("Zoom out [-]")) CameraControl.ZoomBy(1.15f);
            if (ui.Button("Reset zoom [0]")) CameraControl.ResetZoom();
            if (ui.Button("Restore camera defaults")) CameraControl.ResetAll();
            ui.EndRow();
            if (CameraControl.free) ui.Label("Free camera: W/A/S/D or arrows move, Q/E down/up, hold right mouse to look, wheel = speed, Shift fast, Ctrl slow. The boy is frozen while flying.", UI.Dim);
            ui.BeginRow();
            ui.Label("Zoom", null, 40);
            float z = ui.Slider("zoom", 1f / CameraControl.zoom, 0.33f, 6f);
            if (Mathf.Abs(z - 1f / CameraControl.zoom) > 0.001f) CameraControl.zoom = 1f / z;
            ui.EndRow();

            ui.Space();
            ui.Label("Overlay", UI.Dim);
            ui.BeginRow();
            bool a = ui.Toggle(overlay.showTriggers, "triggers [F2]");
            bool b = ui.Toggle(overlay.showSolids, "solid colliders [F3]");
            if (a != overlay.showTriggers || b != overlay.showSolids) { overlay.showTriggers = a; overlay.showSolids = b; overlay.ForceRefresh(); }
            if (ui.Button("show: " + overlay.TriggerModeLabel + " [F2]")) overlay.CycleTriggers();
            if (ui.Button("labels: " + Overlay.LabelLevels[overlay.labelLevel] + " [F4]")) { overlay.labelLevel = (overlay.labelLevel + 1) % 4; overlay.showLabels = overlay.labelLevel > 0; EditorState.Set("overlay.labels", overlay.labelLevel); }
            if (ui.Button("scope: " + Overlay.Scopes[overlay.scope] + " [Shift+F4]")) { overlay.scope = (overlay.scope + 1) % 5; EditorState.Set("overlay.scope", overlay.scope); }
            overlay.showLinks = ui.Toggle(overlay.showLinks, "relation lines");
            overlay.showSavepoints = ui.Toggle(overlay.showSavepoints, "savepoints [F6]");
            ui.EndRow();
            ui.BeginRow();
            hud = ui.Toggle(hud, "HUD [F5]");
            draw.flipY = ui.Toggle(draw.flipY, "flip Y [F9] (if everything is upside down)");
            captureGameLog = ui.Toggle(captureGameLog, "mirror game log to console");
            ui.EndRow();
            ui.BeginRow();
            ui.Label("Draw distance " + overlay.maxDistance.ToString("0") + "m", null, 150);
            overlay.maxDistance = ui.Slider("dist", overlay.maxDistance, 5f, 300f);
            ui.EndRow();
            ui.Label("green = savepoint   red = kill/death   cyan = camera   purple = audio   yellow = other trigger   grey = solid", UI.Dim);
            ui.Label("Middle-click in the world teleports the boy/huddle to the cursor (on its current depth plane).", UI.Dim);

            ui.Space();
            ui.Label("Picking and moving", UI.Dim);
            ui.BeginRow();
            bool pm = ui.Toggle(WorldPick.mode, "pick mode [P]");
            if (pm != WorldPick.mode) WorldPick.mode = pm;
            if (ui.Button("pick filter: " + WorldPick.Filters[WorldPick.filter] + " [Shift+P]")) WorldPick.filter = (WorldPick.filter + 1) % WorldPick.Filters.Length;
            bool gz = ui.Toggle(TransformGizmo.globalOn, "Move gizmo on every selection (overrides the per-object button)");
            if (gz != TransformGizmo.globalOn) TransformGizmo.SetGlobal(gz);
            ui.EndRow();
            ui.Label("Gizmo: drag the X/Y/Z arrows along an axis or the centre square in the camera plane. Ctrl = snap 0.1 m, Shift = fine, right mouse = cancel.", UI.Dim);

            ui.Space();
            showFlags = ui.Toggle(showFlags, "Playdead EditorMode debug flags (experimental)");
            if (showFlags)
            {
                ui.BeginScroll("flags", Mathf.Max(60, ui.Remaining));
                foreach (EditorMode.EDebugFlags fl in Enum.GetValues(typeof(EditorMode.EDebugFlags)))
                {
                    if (fl == EditorMode.EDebugFlags.None) continue;
                    bool on = G.GetFlag(fl);
                    bool nv = ui.Toggle(on, fl.ToString());
                    if (nv != on) { G.SetFlag(fl, nv); DevLog.Write("flag " + fl + " = " + nv); }
                }
                ui.EndScroll();
            }
        }

        int lastLogCount;
        List<string> conLines;
        void TabConsole()
        {
            float h = ui.Remaining - UI.LineH - 6;
            if (DevLog.Lines.Count != lastLogCount || conLines == null)
            {
                lastLogCount = DevLog.Lines.Count;
                conLines = new List<string>();
                lock (DevLog.Lines) foreach (var l in DevLog.Lines) conLines.AddRange(l.Split('\n'));
                ui.ScrollToEnd("con");
            }
            var cl = conLines;
            ui.VirtualList("con", cl.Count, UI.ItemPitch, h, i => ui.Label(cl[i]));
            ui.BeginRow();
            bool enter = ui.TextField("con", ref conInput, ui.Width - 60);
            bool run = ui.Button("Run", 50);
            ui.EndRow();
            if ((enter || run) && conInput.Trim().Length > 0)
            {
                string cmd = conInput.Trim();
                conInput = "";
                DevLog.Write("> " + cmd);
                try { Commands.Run(cmd, this); } catch (Exception e) { DevLog.Write("error: " + e.Message); }
            }
        }

        // ---------------------------------------------------------------- hooks for commands
        public Overlay Overlay { get { return overlay; } }
        public UI UIKit { get { return ui; } }
        public Inspector Inspector { get { return inspector; } }
        public void ShowPanel(string id) { panel = true; workspace.Show(id); }

        // Phase 11: command registry (palette, bridge "cmd <id>")
        void RegisterCommands()
        {
            EditorCommands.Register("undo", "Undo", "history", () => ChangeRecorder.Undo(), "Ctrl+Z");
            EditorCommands.Register("redo", "Redo", "history", () => ChangeRecorder.Redo(), "Ctrl+Y");
            EditorCommands.Register("revert-all", "Revert all my changes", "history", () => ChangeRecorder.RevertAll());
            EditorCommands.Register("mods-reload", "Reload mods from disk", "mods", () => { Mods.LoadAll(); return Mods.Summary(); });
            EditorCommands.Register("author-mode", "Toggle Mod Author mode", "mode", () => { HistoryPanel.authorMode = !HistoryPanel.authorMode; EditorState.Set("mode.author", HistoryPanel.authorMode); return HistoryPanel.authorMode ? "Mod Author mode" : "Explore mode"; });
            EditorCommands.Register("bookmark", "Bookmark selected object", "selection", () => { var m = Bookmarks.Add(Selection.Current); return m != null ? "bookmarked " + m.name : "nothing selected"; }, null, () => Selection.Current != null);
            EditorCommands.Register("sel-show", "Show selected object (set active)", "selection", () => { ChangeRecorder.SetActive(Selection.Current, true, "command"); return "shown"; }, null, () => Selection.Current != null && !Selection.Current.activeSelf);
            EditorCommands.Register("sel-hide", "Hide selected object (set inactive)", "selection", () => { ChangeRecorder.SetActive(Selection.Current, false, "command"); return "hidden"; }, null, () => Selection.Current != null && Selection.Current.activeSelf);
            EditorCommands.Register("sel-boy", "Teleport the boy to the selected object", "selection", () => { G.Teleport(Selection.Current.transform.position); return "teleported"; }, null, () => Selection.Current != null);
            EditorCommands.Register("sel-clear", "Clear selection", "selection", () => { Selection.Clear("command"); return ""; }, null, () => Selection.Current != null);
            EditorCommands.Register("pick", "Toggle world pick mode", "world", () => { WorldPick.mode = !WorldPick.mode; return "pick mode " + (WorldPick.mode ? "on" : "off"); }, "P");
            EditorCommands.Register("scope", "Cycle overlay scope (selected / 1 hop / 2 hops / nearby / all)", "world", () => { overlay.scope = (overlay.scope + 1) % 5; EditorState.Set("overlay.scope", overlay.scope); return "scope " + Overlay.Scopes[overlay.scope]; }, "Shift+F4");
            EditorCommands.Register("labels", "Cycle overlay labels", "world", () => { overlay.labelLevel = (overlay.labelLevel + 1) % 4; overlay.showLabels = overlay.labelLevel > 0; EditorState.Set("overlay.labels", overlay.labelLevel); return "labels " + Overlay.LabelLevels[overlay.labelLevel]; }, "F4");
            EditorCommands.Register("triggers", "Cycle trigger overlay: all / general / savepoints / cameras / kill / audio / off", "world", () => { overlay.CycleTriggers(); return "triggers: " + overlay.TriggerModeLabel; }, "F2");
            EditorCommands.Register("god", "Toggle god mode", "cheats", () => { G.God = !G.God; return "god " + G.God; }, "F7");
            EditorCommands.Register("kill", "Kill the boy", "cheats", () => { G.Kill(); return "killed"; }, "F8");
            for (int i = 0; i < tabIds.Length; i++) { string id = tabIds[i]; EditorCommands.Register("open-" + id, "Open panel: " + tabs[i], "panels", () => { ShowPanel(id); return ""; }); }
        }

        public void ShowTab(int t) { panel = true; if (t >= 0 && t < tabIds.Length) workspace.Show(tabIds[t]); }
        public void SetPanel(bool on, string tabIndex) { panel = on; int t; if (tabIndex != null && int.TryParse(tabIndex, out t)) ShowTab(t); }
        public void SelectInInspector(GameObject g) { Selection.Set(g, "command"); ShowTab(TabInspectorI); }

        // ---------------------------------------------------------------- objects (RuntimeObjectDatabase browser; Scene Explorer comes in Phase 6)
        string objQuery = "";
        int objKind = 0;
        List<ObjRecord> objResults = new List<ObjRecord>();
        string objLastQuery = null; int objLastKind = -1, objLastPass = -1;
        static readonly string[] objKinds = { "All", "Triggers", "State machines", "Signals", "Audio", "Cameras", "Animation", "Hidden" };
        static readonly string[] objKindQ = { "", "k:trigger", "k:statemachine", "k:signal", "k:audio", "k:camera", "k:animation", "hidden" };

        float objStatsAt = -9; string objStats1 = "", objStats2 = "", objCountLine = ""; int objCountFor = -1;
        readonly Dictionary<ObjRecord, KeyValuePair<bool, string>> objLabels = new Dictionary<ObjRecord, KeyValuePair<bool, string>>();
        void TabObjects()
        {
            if (Time.realtimeSinceStartup - objStatsAt > 1f) { objStatsAt = Time.realtimeSinceStartup; objStats1 = ObjectDatabase.Stats(); objStats2 = "references: " + ReferenceIndex.Stats(); }
            ui.Label(objStats1, UI.Dim);
            ui.Label(objStats2, UI.Dim);
            ui.BeginRow();
            ui.Label("Search:", null, 52);
            bool enter = ui.TextField("objq", ref objQuery, Mathf.Max(120, ui.Width - 60));
            ui.EndRow();
            int k = ui.Tabs(objKind, objKinds);
            if (k != objKind) objKind = k;
            string q = (objQuery + " " + objKindQ[objKind]).Trim();
            if (q != objLastQuery || objKind != objLastKind || ObjectDatabase.completedPasses != objLastPass || enter)
            {
                objLastQuery = q; objLastKind = objKind; objLastPass = ObjectDatabase.completedPasses;
                objResults = ObjectDatabase.Search(q, 20000); objLabels.Clear(); objCountFor = -1;
            }
            if (objCountFor != objResults.Count) { objCountFor = objResults.Count; objCountLine = objResults.Count + " result(s).  Words match name or path;  t:Type = component;  k:kind;  hidden / active."; objLabels.Clear(); }
            ui.Label(objCountLine, UI.Dim);
            var res = objResults;
            ui.VirtualList("objs", res.Count, UI.ItemPitch, ui.Remaining, i =>
            {
                var r = res[i];
                if (r.go == null) { ui.Item("(destroyed) " + r.name, UI.Dim); return; }
                Color c = !r.activeInHierarchy ? new Color(1f, 0.45f, 0.9f, 1f) : (r.kind & ObjKind.Trigger) != 0 ? new Color(1f, 0.9f, 0.2f, 1f) : (r.kind & ObjKind.StateMachine) != 0 ? UI.Accent : UI.Txt;
                if (r.go == Selection.Current) ui.RowHighlight(new Color(0.22f, 0.37f, 0.6f, 0.75f));
                KeyValuePair<bool, string> lab;
                if (!objLabels.TryGetValue(r, out lab) || lab.Key != r.activeInHierarchy) objLabels[r] = lab = new KeyValuePair<bool, string>(r.activeInHierarchy, (r.activeInHierarchy ? "" : "[off] ") + r.name + "   <" + r.KindLabel + ">   " + r.path);
                bool ck = ui.Item(lab.Value, c);
                if (ui.LastHover) Selection.SetHover(r.go);
                if (ck) { Selection.Set(r.go, "objects"); ShowTab(TabInspectorI); }
            });
        }

        // ---------------------------------------------------------------- settings
        void TabSettings()
        {
            ui.Label("UI scale", UI.Dim);
            ui.BeginRow();
            foreach (var sc in new[] { 0.75f, 1f, 1.25f, 1.5f })
                if (ui.Button((sc * 100).ToString("0") + "%", 60, Mathf.Approximately(uiScale, sc) ? UI.TabSel : (Color?)null)) { uiScale = sc; EditorState.Set("ui.scale", sc); }
            ui.EndRow();
            ui.Space();
            ui.Label("Layout", UI.Dim);
            ui.BeginRow();
            if (ui.Button("Docked workspace", -2, workspace.docked ? UI.TabSel : (Color?)null)) workspace.SetDocked(true);
            if (ui.Button("Single window", -2, !workspace.docked ? UI.TabSel : (Color?)null)) workspace.SetDocked(false);
            if (ui.Button("Reset layout")) workspace.ResetLayout();
            ui.EndRow();
            ui.Label("Docks: drag the splitters to resize.  ▸ moves the current panel to the next dock, – hides a dock.", UI.Dim);
            ui.Space();
            ui.Label("Renderer", UI.Dim);
            ui.BeginRow();
            if (ui.Button("Unity GL (immediate)", -2, RenderHost.renderer == RenderHost.glRenderer ? UI.TabSel : (Color?)null)) { RenderHost.SetRenderer("gl"); EditorState.Set("render.renderer", "gl"); }
            if (ui.Button("Unity Mesh (batched)", -2, RenderHost.renderer == RenderHost.meshRenderer ? UI.TabSel : (Color?)null)) { RenderHost.SetRenderer("mesh"); EditorState.Set("render.renderer", "mesh"); }
            ui.EndRow();
            ui.Space();
            ui.BeginRow();
            bool h = ui.Toggle(hud, "HUD [F5]"); if (h != hud) { hud = h; EditorState.Set("hud", hud); }
            bool b = ui.Toggle(RenderDiag.board, "diagnostics board [F11]"); if (b != RenderDiag.board) { RenderDiag.board = b; EditorState.Set("diag.board", b); }
            ui.EndRow();
            ui.Space();
            RenderSettingsPanel.Draw(ui);
            ui.Space();
            GameCode.DrawSettings(ui);
            ui.Space();
            ui.Label("Extensions (_mod\\InsideDev.*.dll)", UI.Dim);
            if (Extensions.loaded.Count == 0) ui.Label("   none loaded", UI.Dim);
            foreach (var x in Extensions.loaded) { string xn; try { xn = x.Name; } catch { xn = x.GetType().Name; } ui.Label("   " + xn, UI.Dim); }
            foreach (var pr in Extensions.problems) ui.Label("   problem: " + pr, Color.red);
            ui.Space();
            ui.Label("Cheat Engine", UI.Dim);
            ui.BeginRow();
            bool m = ui.Toggle(MemAddr.Enabled, "Memory addresses in the Inspector"); if (m != MemAddr.Enabled) MemAddr.Enabled = m;
            ui.EndRow();
            if (MemAddr.Enabled) ui.Label("   " + (MemAddr.Ready ? "" : "") + MemAddr.status + (MemLink.Linked ? "   |   CE script linked (_mod\\ce)" : "   |   CE script not linked (load tools\\cheatengine\\InsideDev.CT in Cheat Engine)"), MemAddr.Ready ? UI.Dim : Color.red);
            ui.Space();
            ui.Label("Keyboard: " + InputCapture.Status + ".  While a text field is focused the boy does not receive keys.", UI.Dim);
            ui.Label("Settings are saved to _mod\\editor.cfg.", UI.Dim);
        }
    }

    public static class Commands
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static void Run(string line, DevCore root)
        {
            var a = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string c = a[0].ToLowerInvariant();
            switch (c)
            {
                case "help":
                    DevLog.Write("tp <x> <y> [z] | spawn|load <sub> <sp> | tpsp <sub> <sp> | discover all|<sub> | post <event> | cue <name> | shockwave nokill|fire | catalog | subs [filter] | levels [filter] | where | kill | god [on|off] | ts <scale>\n" +
                                 "find <name> | sel <name> | respawn | reload | triggers|solids|labels|savepoints [on|off]\n" +
                                 "flag <name> [on|off] | flags | clear");
                    break;
                case "tp":
                    {
                        var ch = G.MainCharacter;
                        if (a.Length < 3) { DevLog.Write("usage: tp <x> <y> [z]"); break; }
                        float z = a.Length > 3 ? float.Parse(a[3], IC) : (ch != null ? ch.pos3.z : 0f);
                        G.Teleport(new Vector3(float.Parse(a[1], IC), float.Parse(a[2], IC), z));
                        break;
                    }
                case "levels":
                    {
                        Levels.EnsureLoaded();
                        string f = a.Length > 1 ? a[1].ToLowerInvariant() : "";
                        int shown = 0;
                        foreach (var sc in Levels.scenes)
                        {
                            if (f.Length > 0 && !(sc.path + " " + sc.File).ToLowerInvariant().Contains(f)) continue;
                            string st = Levels.StateOf(sc);
                            DevLog.Write(sc.index.ToString("D3") + "  " + sc.File + "  " + sc.name + "  " + sc.area + (st.Length > 0 ? "  [" + st + "]" : ""));
                            if (++shown >= 80) { DevLog.Write("... refine filter"); break; }
                        }
                        break;
                    }
                case "discover":
                    if (a.Length > 1 && a[1] == "all") Discovery.DiscoverAll();
                    else if (a.Length > 1) Discovery.Enqueue(int.Parse(a[1]), 0, Discovery.Action.Discover);
                    else DevLog.Write("usage: discover all | discover <subsceneIndex>");
                    break;
                case "post":
                    if (a.Length < 2) { DevLog.Write("usage: post <wwise event name>"); break; }
                    AudioSpy.Post(a[1], null); break;
                case "cue":
                    if (a.Length < 2) { DevLog.Write("usage: cue <marker/cue name>"); break; }
                    AudioSpy.InjectCue(a[1]); break;
                case "catalog":
                    AudioCatalog.CatalogAll(); break;
                case "shockwave":
                    if (a.Length > 1 && a[1] == "nokill") { Shockwave.noKill = !Shockwave.noKill; DevLog.Write("shockwave kill disabled: " + Shockwave.noKill); }
                    else if (a.Length > 1 && a[1] == "fire") Shockwave.FireNow();
                    else DevLog.Write("usage: shockwave nokill | shockwave fire");
                    break;
                case "tpsp":
                    if (a.Length < 3) { DevLog.Write("usage: tpsp <subsceneIndex> <savepointIndex>"); break; }
                    root.TeleportToSpawn(int.Parse(a[1]), int.Parse(a[2]));
                    break;
                case "spawn":
                case "load":
                    if (a.Length < 3) { DevLog.Write("usage: load <subsceneIndex> <savepointIndex>"); break; }
                    G.LoadSavepoint(int.Parse(a[1]), int.Parse(a[2]));
                    break;
                case "subs":
                    {
                        if (!G.SavepointsReady) { DevLog.Write("savepoints not ready"); break; }
                        string f = a.Length > 1 ? a[1].ToLowerInvariant() : "";
                        for (int s = 0; s < SavepointManager.SubsceneCount; s++)
                        {
                            string l = G.SubsceneLabel(s);
                            if (f.Length == 0 || l.ToLowerInvariant().Contains(f))
                                DevLog.Write(s.ToString("D3") + "  " + l + "  (" + SavepointManager.GetSubsceneSavepointCount(s) + " savepoints)");
                        }
                        break;
                    }
                case "where":
                    {
                        var ch = G.MainCharacter;
                        int sub, sp;
                        G.CurrentSavepoint(out sub, out sp);
                        DevLog.Write((ch != null ? ch.name + " at " + ch.pos3.ToString("F3") : "no character") + "   savepoint " + (sub >= 0 ? G.SubsceneLabel(sub) + " #" + sp + " (load " + sub + " " + sp + ")" : "?"));
                        break;
                    }
                case "kill": G.Kill(); break;
                case "god":
                    G.God = a.Length > 1 ? On(a[1]) : !G.God;
                    DevLog.Write("god " + (G.God ? "ON" : "OFF"));
                    break;
                case "ts":
                    if (a.Length > 1) G.TimeScale = float.Parse(a[1], IC);
                    DevLog.Write("timescale " + G.TimeScale);
                    break;
                case "find":
                case "sel":
                    {
                        if (a.Length < 2) { DevLog.Write("usage: " + c + " <name>"); break; }
                        string q = line.Substring(line.IndexOf(' ') + 1).Trim();
                        root.Inspector.Search(q);
                        ObjectDatabase.EnsureFresh(3f);
                        var res = new List<Transform>();
                        foreach (var r in ObjectDatabase.Search(q, 5000)) if (r.t != null && r.activeInHierarchy) res.Add(r.t);
                        if (c == "sel")
                        {
                            if (res.Count == 0) { DevLog.Write("no match"); break; }
                            root.Inspector.Select(res[0].gameObject);
                            root.ShowTab(DevCore.TabInspectorI);
                            DevLog.Write("selected " + Inspector.PathOf(res[0]));
                        }
                        else
                        {
                            DevLog.Write(res.Count + " match(es)");
                            for (int i = 0; i < Math.Min(40, res.Count); i++)
                                DevLog.Write("  " + Inspector.PathOf(res[i]) + "  @" + res[i].position.ToString("F2"));
                        }
                        break;
                    }
                case "respawn": GameManager.Respawn(); break;
                case "reload": GameManager.ReloadScene(); break;
                case "triggers": root.Overlay.showTriggers = a.Length > 1 ? On(a[1]) : !root.Overlay.showTriggers; root.Overlay.ForceRefresh(); break;
                case "solids": root.Overlay.showSolids = a.Length > 1 ? On(a[1]) : !root.Overlay.showSolids; root.Overlay.ForceRefresh(); break;
                case "labels": root.Overlay.showLabels = a.Length > 1 ? On(a[1]) : !root.Overlay.showLabels; break;
                case "savepoints": root.Overlay.showSavepoints = a.Length > 1 ? On(a[1]) : !root.Overlay.showSavepoints; break;
                case "flags":
                    foreach (EditorMode.EDebugFlags fl in Enum.GetValues(typeof(EditorMode.EDebugFlags)))
                        if (fl != EditorMode.EDebugFlags.None) DevLog.Write("  " + fl + " = " + G.GetFlag(fl));
                    break;
                case "flag":
                    {
                        if (a.Length < 2) { DevLog.Write("usage: flag <name> [on|off]"); break; }
                        var fl = (EditorMode.EDebugFlags)Enum.Parse(typeof(EditorMode.EDebugFlags), a[1], true);
                        G.SetFlag(fl, a.Length > 2 ? On(a[2]) : !G.GetFlag(fl));
                        DevLog.Write("flag " + fl + " = " + G.GetFlag(fl));
                        break;
                    }
                case "clear": lock (DevLog.Lines) DevLog.Lines.Clear(); break;
                default: DevLog.Write("unknown command '" + c + "' — try help"); break;
            }
        }

        static bool On(string s)
        {
            s = s.ToLowerInvariant();
            return s == "on" || s == "1" || s == "true" || s == "yes";
        }
    }

    // Unity-driven host. Rendering lifecycle lives in RenderHost; this only forwards engine messages.
    public class DevRoot : MonoBehaviour
    {
        public DevCore core;
        int eofGen;

        void Awake() { if (core == null) core = DevCore.Instance ?? new DevCore(); DevLog.Write("DevRoot.Awake"); RenderHost.Init(); }
        void Start() { StartEof("start"); }
        void LateUpdate() { if (core != null) core.LateUpdate(); }
        void Update()
        {
            try { RenderHost.OnUpdate(); } catch (Exception e) { DevLog.Error("RenderHost.OnUpdate", e); }
            if (RenderHost.wantEofRestart) { RenderHost.wantEofRestart = false; StartEof("watchdog"); }
            if (core == null) return;
            core.unityUpdateRT = Time.realtimeSinceStartup;
            core.Update();
        }

        void StartEof(string why)
        {
            eofGen++;
            StartCoroutine(EndOfFrame(eofGen));
            if (why != "start") RenderHost.Event("end-of-frame coroutine (re)started: " + why + " gen " + eofGen);
        }

        IEnumerator EndOfFrame(int gen)
        {
            var wait = new WaitForEndOfFrame();
            while (gen == eofGen)
            {
                yield return wait;
                if (gen != eofGen) yield break;
                EofTick();
            }
        }

        static void EofTick()
        {
            try { RenderHost.OnEndOfFrame(); } catch (Exception e) { DevLog.Error("RenderHost.OnEndOfFrame", e); }
        }

        void OnApplicationFocus(bool focus) { try { RenderHost.OnFocus(focus); } catch (Exception e) { DevLog.Error("OnApplicationFocus", e); } }
        void OnApplicationPause(bool pause) { try { RenderHost.OnPause(pause); } catch (Exception e) { DevLog.Error("OnApplicationPause", e); } }
        void OnLevelWasLoaded(int level) { try { RenderHost.OnLevelLoaded(level); } catch (Exception e) { DevLog.Error("OnLevelWasLoaded", e); } }

        void OnApplicationQuit() { try { PassiveHidden.SaveIfDirty(); } catch (Exception e) { DevLog.Error("OnApplicationQuit", e); } }

        void OnDestroy()
        {
            try { PassiveHidden.SaveIfDirty(); } catch { }
            RenderHost.Snapshot("DevRoot destroyed");
            RenderHost.Shutdown();
            DevLog.Write("DevRoot destroyed");
        }
    }
}
