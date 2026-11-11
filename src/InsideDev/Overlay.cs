using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    public class ColliderEntry
    {
        public Collider col;
        public string name;
        public string types;     // game MonoBehaviours on the same object
        public Color color;
        public bool trigger;
        public string kind;      // short category label
        // cached per entry, refreshed every few frames (staggered) instead of asking the engine every frame
        public GameObject go;
        public Transform t;
        public int shape;        // 0 local box (box / capsule), 1 sphere, 2 world bounds
        public Vector3 lc, ls;   // local centre and size of the box
        public float lr;         // sphere radius (local)
        public readonly Vector3[] w = new Vector3[8];   // world corners, or w[0] = sphere centre
        public float wr;         // sphere radius (world)
        public Bounds b;
        public Vector3 bc, be;   // centre and extents of b
        public float br;         // length of be
        public bool alive, active, on;
        public int checkAt;
        public string label;     // overlay label text, built when it is first drawn and again when 'on' changes
        public bool labelOn;
        public string[] typeList;   // the component type names behind types, for short and full labels
        public string labelFull;    // label with every component, wrapped, for the selected object
        public int labelTypes = -2; // overlay.label.types value the label was built with
        public float labelW;        // width of label in pixels at the label size
        public int hop, hopStamp = -1;   // hop distance from the selection, valid while hopStamp matches the overlay's
    }

    // World overlay: trigger/collider wireframes, labels, savepoint markers.
    // Drawn at the end of the frame in screen space so it works regardless of Playdead's custom render pipeline.
    // Cost: it is rebuilt every frame, so nothing per collider may ask the engine every frame. Each entry keeps its
    // object, shape, world corners and bounds and asks the engine (alive, active, enabled, transform) only every
    // CheckEvery frames, staggered; frustum culling and the projection run in C# from one view-projection matrix per
    // frame (WorldToScreenPoint per line end was 24 engine calls per box). A rescan classifies new colliders only.
    // The choices (what is shown, filter, distance) are kept in editor.cfg: LoadChoices at start, SaveChoices whenever
    // the user changes one. Code that hides the overlay for a while (clean frames) sets the fields and does not save.
    public class Overlay
    {
        public bool showTriggers = true;
        // F2 cycles: all triggers, then one category at a time: general (yellow), savepoints (green), cameras (blue),
        // kill (red), audio (purple), logic (orange), water (deep blue), fx (teal), streaming (white)
        public int triggerFilter;
        public static readonly string[] triggerFilterNames = { "all", "general (yellow)", "savepoints (green)", "cameras (blue)", "kill (red)", "audio (purple)", "logic (orange)", "water (deep blue)", "fx (teal)", "streaming (white)" };
        static readonly string[] triggerFilterKinds = { null, "trigger", "save", "camera", "kill", "audio", "logic", "water", "fx", "stream" };
        public string TriggerModeLabel { get { return showTriggers ? triggerFilterNames[triggerFilter] : "off"; } }
        // F2: all -> each category -> off -> all
        public void CycleTriggers()
        {
            if (!showTriggers) { showTriggers = true; triggerFilter = 0; }
            else if (triggerFilter == triggerFilterNames.Length - 1) { showTriggers = false; triggerFilter = 0; }
            else triggerFilter++;
            ForceRefresh();
        }
        public bool showSolids = false;
        public bool showLabels = true;
        // label level: 0 off, 1 selected object only, 2 near the boy (15 m), 3 all visible
        public int labelLevel = 3;
        int lastLabelLevel = 3;
        public static readonly string[] LabelLevels = { "OFF", "SELECTED", "NEARBY", "ALL" };
        public void SetLabelLevel(int level)
        {
            labelLevel = Mathf.Clamp(level, 0, 3); showLabels = labelLevel > 0;
            if (labelLevel > 0) lastLabelLevel = labelLevel;
        }
        // labels on = the level used last (ALL at first), off = OFF
        public void SetLabels(bool on) { SetLabelLevel(on ? (labelLevel > 0 ? labelLevel : lastLabelLevel) : 0); }
        // Phase B: relationship scope around the selection. In-scope objects are drawn normally, everything else faint.
        // 0 SELECTED, 1 SELECTED + 1 HOP (default), 2 SELECTED + 2 HOPS, 3 NEARBY (15 m of the boy), 4 ALL
        public int scope = 1;
        public static readonly string[] Scopes = { "SELECTED", "1 HOP", "2 HOPS", "NEARBY", "ALL" };
        public bool showLinks = true;          // lines from the selection to its related objects
        readonly Dictionary<int, int> hopOf = new Dictionary<int, int>();   // goId -> hop distance from selection
        int hopVersion = -1, hopScope = -1, hopEdges = -1; float hopAt;
        public int inScopeCount;
        public bool showSavepoints = true;
        public float maxDistance = 60f;   // world units from camera
        public bool showHidden = false;
        public bool showDisabled = true;
        public List<DevCore.HiddenEntry> hiddenEntries;

        // ---------------------------------------------------------------- persisted choices (editor.cfg)
        public static void DescribeKeys()
        {
            EditorState.Describe("core", "overlay.triggers", "1", "world overlay: trigger volumes drawn (F2 cycles the categories, then off)");
            EditorState.Describe("core", "overlay.filter", "all", "world overlay: trigger category shown: all, trigger, save, camera, kill, audio, logic, water, fx or stream");
            EditorState.Describe("core", "overlay.solids", "0", "world overlay: solid colliders drawn (F3)");
            EditorState.Describe("core", "overlay.labels", "3", "world overlay: label level 0 off, 1 selected, 2 near the boy, 3 all (F4)");
            EditorState.Describe("core", "overlay.label.types", "3", "world overlay: component names in a label, the rest as +N (0 only the count, -1 all); the selected object always shows all");
            EditorState.Describe("core", "overlay.scope", "1", "world overlay: scope around the selection 0 selected, 1 one hop, 2 two hops, 3 near the boy, 4 all (Shift+F4)");
            EditorState.Describe("core", "overlay.savepoints", "1", "world overlay: savepoint markers drawn (F6)");
            EditorState.Describe("core", "overlay.links", "1", "world overlay: lines from the selection to its related objects");
            EditorState.Describe("core", "overlay.distance", "60", "world overlay: draw distance from the camera in metres (5 to 300)");
            EditorState.Describe("core", "overlay.hidden", "0", "world overlay: markers on switched-off objects (F10); keeps the object scan running");
        }

        public void LoadChoices()
        {
            SetLabelLevel(EditorState.Get("overlay.labels", 3));
            labelTypes = Mathf.Clamp(EditorState.Get("overlay.label.types", 3), -1, 50);
            scope = Mathf.Clamp(EditorState.Get("overlay.scope", 1), 0, 4);
            showTriggers = EditorState.Get("overlay.triggers", true);
            string kind = EditorState.Get("overlay.filter", "all");
            triggerFilter = 0;
            for (int i = 1; i < triggerFilterKinds.Length; i++) if (triggerFilterKinds[i] == kind) triggerFilter = i;
            showSolids = EditorState.Get("overlay.solids", false);
            showSavepoints = EditorState.Get("overlay.savepoints", true);
            showLinks = EditorState.Get("overlay.links", true);
            maxDistance = Mathf.Clamp(EditorState.Get("overlay.distance", 60f), 5f, 300f);
            showHidden = EditorState.Get("overlay.hidden", false);
        }

        // call after the user changed a choice; a key whose value did not change is not written. While clean frames are
        // on the fields hold their temporary "everything hidden" state, which must not reach the file.
        public void SaveChoices()
        {
            if (MultiCamClean.on) return;
            EditorState.Set("overlay.labels", labelLevel);
            EditorState.Set("overlay.scope", scope);
            EditorState.Set("overlay.triggers", showTriggers);
            EditorState.Set("overlay.filter", triggerFilterKinds[triggerFilter] ?? "all");
            EditorState.Set("overlay.solids", showSolids);
            EditorState.Set("overlay.savepoints", showSavepoints);
            EditorState.Set("overlay.links", showLinks);
            EditorState.Set("overlay.distance", Mathf.Round(maxDistance));
            EditorState.Set("overlay.hidden", showHidden);
        }

        public readonly List<ColliderEntry> entries = new List<ColliderEntry>();
        readonly Dictionary<int, ColliderEntry> known = new Dictionary<int, ColliderEntry>();
        readonly HashSet<int> seenIds = new HashSet<int>();
        readonly List<int> goneIds = new List<int>();
        readonly StringBuilder typesSb = new StringBuilder();
        float nextRefresh, refreshedAt = -1e9f; int seenStream = -1;
        bool wasInUse;
        Camera cam;
        Matrix4x4 vp;
        Rect pr;
        float sh, sw;
        readonly Vector4[] fpl = new Vector4[6];
        readonly Vector3[] proj = new Vector3[8];
        const int CheckEvery = 8;

        public int visibleTriggers, visibleSolids;

        static readonly Color cSave = new Color(0.2f, 1f, 0.3f, 0.95f);
        static readonly Color cKill = new Color(1f, 0.2f, 0.2f, 0.95f);
        static readonly Color cCam = new Color(0.3f, 0.85f, 1f, 0.9f);
        static readonly Color cAudio = new Color(0.8f, 0.45f, 1f, 0.85f);
        static readonly Color cTrig = new Color(1f, 0.9f, 0.2f, 0.9f);
        static readonly Color cLogic = new Color(1f, 0.58f, 0.15f, 0.9f);
        static readonly Color cWater = new Color(0.25f, 0.45f, 1f, 0.9f);
        static readonly Color cFx = new Color(0.35f, 0.9f, 0.75f, 0.85f);
        static readonly Color cStream = new Color(0.92f, 0.92f, 0.92f, 0.8f);
        static readonly Color cSolid = new Color(0.7f, 0.7f, 0.7f, 0.35f);
        static readonly Color cSpawn = new Color(0.2f, 1f, 1f, 1f);

        // FindObjectsOfType(Collider) costs milliseconds. With no boxes to draw it does not run at all. Otherwise the list
        // is refreshed when streaming changed, every second while the editor is open and every 10 s while the tool is in
        // use; with nobody using the tool the timer is only a 30 s safety net for colliders the game switched on itself.
        public void Update()
        {
            if (!showTriggers && !showSolids)
            {
                if (entries.Count > 0 || known.Count > 0) { entries.Clear(); known.Clear(); }
                nextRefresh = 0f;
                return;
            }
            float now = Time.realtimeSinceStartup;
            bool inUse = Perf.editorOpen || Background.InUse;
            if (inUse && !wasInUse && now - refreshedAt > 10f) nextRefresh = 0f;   // somebody came back: bring the list up to date
            wasInUse = inUse;
            if (now >= nextRefresh || Levels.Changed(ref seenStream))
            {
                nextRefresh = now + (Perf.editorOpen ? 1f : inUse ? 10f : 30f);
                refreshedAt = now;
                try { Refresh(); } catch (Exception e) { DevLog.Error("Overlay.Refresh", e); }
            }
        }

        public void ForceRefresh() { nextRefresh = 0f; }

        void Refresh()
        {
            entries.Clear();
            if (!showTriggers && !showSolids) { known.Clear(); return; }
            var all = UObj.FindObjectsOfType(typeof(Collider));
            var sb = typesSb;
            var seen = seenIds; seen.Clear();
            foreach (var o in all)
            {
                var c = o as Collider;
                if (c == null) continue;
                bool trig = c.isTrigger;
                if (trig ? !showTriggers : !showSolids) continue;
                int id = c.GetInstanceID();
                seen.Add(id);
                ColliderEntry e;
                if (!known.TryGetValue(id, out e) || e.col != c || e.trigger != trig)
                {
                    e = new ColliderEntry { col = c, go = c.gameObject, t = c.transform, trigger = trig };
                    e.name = e.go.name;
                    sb.Length = 0; typeNames.Clear();
                    foreach (var mb in c.GetComponents<MonoBehaviour>())
                    {
                        if (mb == null) continue;
                        string tn = mb.GetType().Name;
                        typeNames.Add(tn);
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(tn);
                    }
                    e.types = sb.ToString();
                    e.typeList = typeNames.ToArray();
                    Classify(e, typeNames);
                    Shape(e);
                    e.checkAt = 0;
                    known[id] = e;
                }
                entries.Add(e);
            }
            var gone = goneIds; gone.Clear();
            foreach (var k in known.Keys) if (!seen.Contains(k)) gone.Add(k);
            foreach (var k in gone) known.Remove(k);
        }

        static void Shape(ColliderEntry e)
        {
            var box = e.col as BoxCollider;
            if (box != null) { e.shape = 0; e.lc = box.center; e.ls = box.size; return; }
            var sph = e.col as SphereCollider;
            if (sph != null) { e.shape = 1; e.lc = sph.center; e.lr = sph.radius; return; }
            var cap = e.col as CapsuleCollider;
            if (cap != null)
            {
                var size = new Vector3(cap.radius * 2, cap.radius * 2, cap.radius * 2);
                size[cap.direction] = Mathf.Max(cap.height, cap.radius * 2);
                e.shape = 0; e.lc = cap.center; e.ls = size;
                return;
            }
            e.shape = 2;
        }

        // engine reads for one entry: alive, active, enabled, world geometry and bounds
        void Check(ColliderEntry e, int f)
        {
            e.checkAt = f + CheckEvery + (e.GetHashCode() & 3);
            var c = e.col;
            e.alive = c != null;
            if (!e.alive) return;
            e.active = e.go.activeInHierarchy;
            if (!e.active) return;
            e.on = c.enabled;
            e.hopStamp = -1;   // the parent chain may have changed since the last check
            var t = e.t;
            Bounds b;
            if (e.shape == 0)
            {
                Vector3 h = e.ls * 0.5f;
                for (int i = 0; i < 8; i++)
                    e.w[i] = t.TransformPoint(e.lc + new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z));
                b = new Bounds(e.w[0], Vector3.zero);
                for (int i = 1; i < 8; i++) b.Encapsulate(e.w[i]);
            }
            else if (e.shape == 1)
            {
                Vector3 s = t.lossyScale;
                e.wr = e.lr * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                e.w[0] = t.TransformPoint(e.lc);
                b = new Bounds(e.w[0], Vector3.one * e.wr * 2f);
            }
            else
            {
                try { b = c.bounds; } catch { e.alive = false; return; }
                if (!e.on && b.size == Vector3.zero) b = new Bounds(t.position, t.lossyScale);
                Vector3 mn = b.min, mx = b.max;
                for (int i = 0; i < 8; i++) e.w[i] = new Vector3((i & 1) != 0 ? mx.x : mn.x, (i & 2) != 0 ? mx.y : mn.y, (i & 4) != 0 ? mx.z : mn.z);
            }
            e.b = b; e.bc = b.center; e.be = b.extents; e.br = e.be.magnitude;
        }

        // category of one trigger collider, same rules as the overlay colours (for picking)
        public static string TriggerKindOf(Collider c)
        {
            var e = new ColliderEntry { col = c, name = c.gameObject.name, trigger = true };
            var names = new List<string>();
            foreach (var mb in c.GetComponents<MonoBehaviour>()) if (mb != null) names.Add(mb.GetType().Name);
            e.types = string.Join(" ", names.ToArray());
            Classify(e, names);
            return e.kind == "save" ? "savepoint" : e.kind == "trigger" ? "general" : e.kind;
        }

        // Component types decide first (Playdead's scripts say what a volume does); the object name only when they do not.
        // Names alone filed VfxConfig_HardImpact_Dust_Gradient as kill ("die" in Gradient) and fog boxes named Camera... as camera.
        static readonly List<string> typeNames = new List<string>();
        static readonly HashSet<string> tSave = new HashSet<string> { "Savepoint", "SavepointEventHandler", "SavepointAudio" };
        static readonly HashSet<string> tCam = new HashSet<string> { "CameraBlendBox", "CameraBorder", "CameraBlendProbe", "CameraZoneOverride" };
        static readonly HashSet<string> tWater = new HashSet<string> { "BoyWater", "FluidVolume", "WaterQueueMaster", "WaterEdge", "WaterMesh", "WaterDust", "WaterSplashHandler", "WaterTrigger", "WaterTrigger2" };
        static readonly HashSet<string> tStream = new HashSet<string> { "UnloadInactiveSubscenes", "SubsceneLoadTrigger", "LoadTrigger" };
        static readonly HashSet<string> tAudio = new HashSet<string> { "RTPCBox", "RTPCCapsule", "RTPCSphere", "AkGameObj", "AudioProbe", "AkAmbient", "AkEnvironment", "AudioProperties" };
        static readonly HashSet<string> tFx = new HashSet<string> { "EnvironmentBox", "FogAmbientBlendBox", "PostEffectTransitionBox", "MaterialInstanceBlendBox", "ColorBlendProbe", "VfxConfig", "VfxProbe", "EnvironmentSlave", "EffectMaterial", "EffectMaterialSwitcher", "LadderEffectMaterial", "BoyDepthObstruction" };
        static readonly HashSet<string> tPlumbing = new HashSet<string> { "PlayMakerFixedUpdate", "PlayMakerTriggerEnter", "PlayMakerTriggerExit", "PlayMakerTriggerStay", "PlayMakerCollisionEnter", "PlayMakerCollisionExit", "PlayMakerCollisionStay", "PlayMakerMouseEvents" };

        static void Classify(ColliderEntry e, List<string> types)
        {
            if (!e.trigger) { e.color = cSolid; e.kind = "solid"; return; }
            string n = e.name.ToLowerInvariant();
            bool save = false, kill = false, cam = false, water = false, stream = false, audio = false, fx = false, logic = false;
            foreach (var t in types)
            {
                if (tSave.Contains(t)) save = true;
                else if (t.Contains("Kill") || t.Contains("Death")) kill = true;
                else if (tCam.Contains(t) || t.StartsWith("Camera")) cam = true;
                else if (tWater.Contains(t)) water = true;
                else if (tStream.Contains(t)) stream = true;
                else if (tAudio.Contains(t) || t.StartsWith("Ak") || t.StartsWith("RTPC") || t.StartsWith("Audio")) audio = true;
                else if (tFx.Contains(t) || t.EndsWith("ColorTrigger") || t.EndsWith("BlendBox") || (t.EndsWith("Probe") && t != "IsTriggeredByProbe")) fx = true;
                else if (!tPlumbing.Contains(t)) logic = true;
            }
            if (save || n.StartsWith("savepoint") || n.Contains("checkpoint")) { e.color = cSave; e.kind = "save"; }
            else if (kill) { e.color = cKill; e.kind = "kill"; }
            else if (cam) { e.color = cCam; e.kind = "camera"; }
            else if (water) { e.color = cWater; e.kind = "water"; }
            else if (stream) { e.color = cStream; e.kind = "stream"; }
            else if (audio) { e.color = cAudio; e.kind = "audio"; }
            else if (fx) { e.color = cFx; e.kind = "fx"; }
            else if (n.Contains("kill") || n.Contains("death") || n.Contains("drown") || n.Contains("squash") || n.Contains("crush") || n.StartsWith("die") || n.Contains("_die") || n.Contains(" die")) { e.color = cKill; e.kind = "kill"; }
            else if (logic) { e.color = cLogic; e.kind = "logic"; }
            else if (n.Contains("cam")) { e.color = cCam; e.kind = "camera"; }
            else if (n.Contains("audio") || n.Contains("sound") || n.Contains("akevent") || n.Contains("music")) { e.color = cAudio; e.kind = "audio"; }
            else { e.color = cTrig; e.kind = "trigger"; }
        }

        // ---------------------------------------------------------------- drawing (called at end of frame)
        // This runs every submitted frame with the default settings and the editor closed, so nothing in it allocates or
        // asks the engine per collider: lists are reused, label and savepoint texts are kept, the scope hop is kept per
        // entry, culling and projection run in C#.
        Draw d;
        struct LabelAt { public float d; public Vector2 p; public ColliderEntry e; }
        readonly List<LabelAt> labels = new List<LabelAt>(256);
        static readonly Comparison<LabelAt> nearestCentreFirst = (a, b) => a.d.CompareTo(b.d);
        const int MaxLabels = 60;
        const float NearBoy = 15f;
        bool boyKnown; Vector3 boyPos;

        public void Emit(Draw draw)
        {
            d = draw;
            visibleTriggers = visibleSolids = 0; inScopeCount = 0;
            RebuildHops();
            var sel = Selection.Current;
            bool hasSel = sel != null;
            bool boxes = showTriggers || showSolids;
            bool hiddenMarks = showHidden && hiddenEntries != null;
            float now = Time.realtimeSinceStartup;
            // hidden markers, the selection's lines and labels, Audio DB emitters and the graph hover line do not depend on
            // the box toggles; with nothing at all to draw the camera is not even looked up
            if (!boxes && !showSavepoints && !hiddenMarks && !hasSel && AudioDbPanel.Current == null && now - GraphView.HoverAt > 0.15f) return;
            cam = G.Cam();
            if (cam == null) return;
            vp = cam.projectionMatrix * cam.worldToCameraMatrix;
            pr = cam.pixelRect;
            sh = Screen.height; sw = Screen.width;
            FrustumPlanes();
            Vector3 camPos = cam.transform.position;
            int f = Time.frameCount;
            var ch = G.MainCharacter;
            boyKnown = (object)ch != null;
            if (boyKnown) boyPos = ch.pos3;

            labels.Clear();
            if (boxes)
            {
                string kindFilter = triggerFilter != 0 ? triggerFilterKinds[triggerFilter] : null;
                bool scoped = hasSel && scope != 4;
                bool wantLabels = showLabels && labelLevel > 0;
                for (int k = 0; k < entries.Count; k++)
                {
                    var e = entries[k];
                    bool checkedNow = f >= e.checkAt;
                    if (checkedNow) Check(e, f);
                    if (!e.alive || !e.active) continue;
                    if (!e.on && !showDisabled) continue;
                    if (e.trigger && kindFilter != null && e.kind != kindFilter) continue;
                    if (!InFrustum(e.bc, e.be)) continue;
                    float reach = maxDistance + e.br;
                    if ((e.bc - camPos).sqrMagnitude > reach * reach) continue;
                    int hop = 0;
                    if (scoped)
                    {
                        if (scope == 3) hop = boyKnown && Near(boyPos, e.bc, e.be) ? 0 : -1;
                        else
                        {
                            if (e.hopStamp != hopStamp)
                            {
                                // the object may have been destroyed since its last check
                                if (!checkedNow && e.col == null) { e.alive = false; continue; }
                                e.hop = HopOf(e.t); e.hopStamp = hopStamp;
                            }
                            hop = e.hop;
                        }
                    }
                    if (e.trigger) visibleTriggers++; else visibleSolids++;
                    float a = e.on ? e.color.a : 0.3f;
                    if (hop < 0) a *= 0.18f; else inScopeCount++;
                    curColor = new Color(e.color.r, e.color.g, e.color.b, a);
                    DrawEntry(e);
                    if (wantLabels && e.trigger)
                    {
                        bool want = labelLevel == 3
                                    || (labelLevel == 1 ? hasSel && (ReferenceEquals(e.go, sel) || (hop >= 0 && scope <= 2))
                                                        : boyKnown && Near(boyPos, e.bc, e.be));
                        if (want) AddLabel(e);
                    }
                }
            }
            if (hasSel && showLinks) DrawLinks(sel);
            DrawGraphHover(now);
            if (hasSel) DrawSelectionAudio(sel, now);
            DrawAudioEmitters(now);
            if (showSavepoints) DrawSavepoints(now);
            if (hiddenMarks) DrawHidden(camPos, f);
            if (boyKnown && (boxes || showSavepoints))
            {
                curColor = Color.white;
                var p = boyPos;
                Line(p + new Vector3(-0.4f, 0, 0), p + new Vector3(0.4f, 0, 0));
                Line(p + new Vector3(0, -0.4f, 0), p + new Vector3(0, 0.4f, 0));
            }
            if (labels.Count > 0) DrawLabels();
        }

        // ---------------------------------------------------------------- relationship scope
        // BFS over the reference index (both directions, noise edges skipped) from the selection.
        readonly List<int> hopFrontier = new List<int>(), hopNext = new List<int>();
        int hopStamp;
        void RebuildHops()
        {
            var sel = Selection.Current;
            int want = scope == 1 ? 1 : scope == 2 ? 2 : 0;
            if (hopVersion == Selection.Version && hopScope == scope && (hopEdges == ReferenceIndex.edges || Time.realtimeSinceStartup - hopAt < 2f)) return;
            hopVersion = Selection.Version; hopScope = scope; hopEdges = ReferenceIndex.edges; hopAt = Time.realtimeSinceStartup;
            hopOf.Clear(); hopStamp++;
            if (sel == null) return;
            int start = sel.GetInstanceID();
            hopOf[start] = 0;
            var frontier = hopFrontier; var next = hopNext;
            frontier.Clear(); frontier.Add(start);
            for (int h = 1; h <= want && frontier.Count > 0 && hopOf.Count < 400; h++)
            {
                next.Clear();
                foreach (var id in frontier)
                {
                    foreach (var e in ReferenceIndex.References(id)) if (!e.noise && e.dstGo != 0 && !hopOf.ContainsKey(e.dstGo)) { hopOf[e.dstGo] = h; next.Add(e.dstGo); }
                    foreach (var e in ReferenceIndex.Referencers(id)) if (!e.noise && e.srcGo != 0 && !hopOf.ContainsKey(e.srcGo)) { hopOf[e.srcGo] = h; next.Add(e.srcGo); }
                }
                var swap = frontier; frontier = next; next = swap;
            }
        }

        // hop distance of a collider's object from the selection (-1 = out of scope). The collider may sit on a
        // child of the related object (common for trigger volumes), so the parent chain is checked up to 3 levels.
        public int ScopeHop(GameObject g, Bounds b)
        {
            var sel = Selection.Current;
            if (scope == 4 || sel == null) return 0;
            if (scope == 3) { var ch = G.MainCharacter; return ch != null && Near(ch.pos3, b.center, b.extents) ? 0 : -1; }
            return g != null ? HopOf(g.transform) : -1;
        }

        int HopOf(Transform t)
        {
            for (int i = 0; i < 4 && t != null; i++, t = t.parent)
            {
                int h;
                if (hopOf.TryGetValue(t.gameObject.GetInstanceID(), out h)) return h;
            }
            return -1;
        }

        // a box (centre, extents) comes within NearBoy metres of the point
        static bool Near(Vector3 p, Vector3 c, Vector3 ext)
        {
            float dx = Mathf.Max(0f, Mathf.Abs(p.x - c.x) - ext.x), dy = Mathf.Max(0f, Mathf.Abs(p.y - c.y) - ext.y), dz = Mathf.Max(0f, Mathf.Abs(p.z - c.z) - ext.z);
            return dx * dx + dy * dy + dz * dz < NearBoy * NearBoy;
        }

        // lines from the selection to the objects one/two hops away (world <-> graph sync)
        void DrawLinks(GameObject sel)
        {
            if (hopOf.Count <= 1 || scope > 2) return;
            Vector3 from = CenterOf(sel);
            int selId = sel.GetInstanceID();
            int n = 0;
            foreach (var kv in hopOf)
            {
                if (kv.Value == 0 || n > 80) continue;
                var r = ObjectDatabase.Get(kv.Key);
                if (r == null || r.go == null || !r.go.activeInHierarchy) continue;
                n++;
                bool fired = EventMonitor.FiredAge(selId, kv.Key) < 2.5f || EventMonitor.FiredAge(kv.Key, selId) < 2.5f;
                curColor = fired ? new Color(0.3f, 1f, 0.4f, 0.95f) : kv.Value == 1 ? new Color(0.35f, 0.9f, 1f, 0.7f) : new Color(0.35f, 0.9f, 1f, 0.3f);
                Line(from, CenterOf(r.go));
            }
        }

        // §89: the Wwise event selected in the Audio DB -> its emitters in the world (game references that are loaded
        // + every object that posted it this session); flashes green while it is playing
        // §88: the selection's own audio - what it posted this session and what its game data references
        readonly List<string> selAudioNames = new List<string>();
        void DrawSelectionAudio(GameObject sel, float now)
        {
            int id = sel.GetInstanceID();
            if (selAudioFor != id || now - selAudioAt > 1f)
            {
                selAudioFor = id; selAudioAt = now; selAudioLast = -1f;
                var names = selAudioNames; names.Clear();
                foreach (var p in AudioTrace.plays.Values)
                    if ((int)p.gameObj == id) { string n = AudioTrace.Name(p.eventId); if (!names.Contains(n) && names.Count < 4) names.Add(n); if (p.t > selAudioLast) selAudioLast = p.t; }
                int refs = 0;
                if (AudioDb.refs.Count > 0)
                {
                    string path = Inspector.PathOf(sel.transform);
                    foreach (var r in AudioDb.refs.Values) if (r.path == path && r.prov != "OBSERVED") { refs++; if (!names.Contains(r.eventName) && names.Count < 6) names.Add(r.eventName); }
                }
                selAudioText = names.Count > 0 ? "audio: " + string.Join(", ", names.ToArray()) + (refs > 0 ? "  (" + refs + " in its data)" : "") : null;
            }
            if (selAudioText == null) return;
            bool live = selAudioLast >= 0 && now - selAudioLast < 2f;
            Vector3 sp = Project(CenterOf(sel));
            if (sp.z > 0) d.ShadowText(sp.x + 8, sh - sp.y + 8, selAudioText, live ? new Color(0.3f, 1f, 0.45f, 1f) : new Color(0.45f, 0.85f, 1f, 1f), 12);
        }
        int selAudioFor; float selAudioAt, selAudioLast = -1f; string selAudioText;

        void DrawAudioEmitters(float now)
        {
            var rec = AudioDbPanel.Current;
            if (rec == null || (rec.kind != AudioDb.Kind.Event && rec.kind != AudioDb.Kind.Bank)) return;
            bool bank = rec.kind == AudioDb.Kind.Bank;
            if (emitFor != rec.id || now - emitAt > 1f)
            {
                emitFor = rec.id; emitAt = now; emitters.Clear(); emitterEvent.Clear(); emitterText.Clear();
                if (bank)
                {
                    // a SoundBank: loaded objects that reference any event of that bank
                    foreach (var r in AudioDb.recs)
                    {
                        if (r.kind != AudioDb.Kind.Event || r.bank != rec.name) continue;
                        foreach (var x in r.refs) if (x.live != null && x.live.go != null && !emitters.Contains(x.live.go) && emitters.Count < 80) { emitters.Add(x.live.go); emitterEvent.Add(r.Name); }
                    }
                }
                else
                {
                    foreach (var x in rec.refs) if (x.live != null && x.live.go != null && !emitters.Contains(x.live.go)) { emitters.Add(x.live.go); emitterEvent.Add(rec.Name); }
                    foreach (var p in AudioTrace.plays.Values)
                        if (p.eventId == rec.id) { var u = AudioTrace.Unity(p.gameObj); if (u != null && !emitters.Contains(u)) { emitters.Add(u); emitterEvent.Add(rec.Name); } }
                }
                // the label of each emitter, built here once a second instead of every frame
                for (int i = 0; i < emitters.Count; i++) emitterText.Add("audio " + emitterEvent[i] + "  on " + emitters[i].name + (bank ? "  [" + rec.Name + "]" : ""));
            }
            AudioTrace.EvStat st; float age = !bank && AudioTrace.evStats.TryGetValue(rec.id, out st) ? now - st.lastT : 1e9f;
            bool live = age < 2f;
            for (int ei = 0; ei < emitters.Count; ei++)
            {
                var g = emitters[ei];
                if (g == null) continue;
                Vector3 c = CenterOf(g);
                curColor = live ? new Color(0.3f, 1f, 0.45f, 1f - age * 0.4f) : new Color(0.45f, 0.85f, 1f, 0.9f);
                float r = 0.35f;
                Line(c + Vector3.left * r, c + Vector3.right * r); Line(c + Vector3.up * r, c + Vector3.down * r);
                Line(c + new Vector3(-r, -r, 0) * 0.7f, c + new Vector3(r, r, 0) * 0.7f); Line(c + new Vector3(-r, r, 0) * 0.7f, c + new Vector3(r, -r, 0) * 0.7f);
                if (boyKnown) { curColor = new Color(curColor.r, curColor.g, curColor.b, 0.25f); Line(boyPos, c); }
                Vector3 sp = Project(c);
                if (sp.z > 0) d.ShadowText(sp.x + 8, sh - sp.y - 7, emitterText[ei], live ? new Color(0.3f, 1f, 0.45f, 1f) : new Color(0.45f, 0.85f, 1f, 1f), 12);
            }
        }
        readonly List<GameObject> emitters = new List<GameObject>(); readonly List<string> emitterEvent = new List<string>(), emitterText = new List<string>(); uint emitFor; float emitAt;

        // §89: the graph edge under the mouse, drawn between the two objects in the world
        void DrawGraphHover(float now)
        {
            if (now - GraphView.HoverAt > 0.15f) return;
            var a = ObjectDatabase.Get(GraphView.HoverA); var b = ObjectDatabase.Get(GraphView.HoverB);
            if (a == null || b == null || a.go == null || b.go == null) return;
            Vector3 pa = CenterOf(a.go), pb = CenterOf(b.go);
            curColor = new Color(1f, 0.85f, 0.3f, 1f);
            Line(pa, pb); Line(pa + Vector3.up * 0.03f, pb + Vector3.up * 0.03f);
            Cross(pa, 0.25f); Cross(pb, 0.25f);
        }

        void Cross(Vector3 c, float r) { Line(c + Vector3.left * r, c + Vector3.right * r); Line(c + Vector3.up * r, c + Vector3.down * r); }

        static Vector3 CenterOf(GameObject g)
        {
            var c = g.GetComponent<Collider>();
            if (c != null) { try { return c.bounds.center; } catch { } }
            var rd = g.GetComponent<Renderer>();
            if (rd != null) { try { return rd.bounds.center; } catch { } }
            return g.transform.position;
        }

        Color curColor;
        static readonly Color cHidden = new Color(1f, 0.35f, 0.9f, 0.95f);

        // markers on switched-off objects: position and "alive" are read every CheckEvery frames (staggered), the label
        // text once
        void DrawHidden(Vector3 camPos, int f)
        {
            int shown = 0;
            float maxSq = maxDistance * maxDistance;
            var list = hiddenEntries;
            for (int k = 0; k < list.Count; k++)
            {
                var h = list[k];
                if (f >= h.checkAt)
                {
                    h.checkAt = f + CheckEvery + (k & 3);
                    h.alive = h.go != null;
                    if (h.alive) h.pos = h.go.transform.position;
                }
                if (!h.alive) continue;
                var p = h.pos;
                if ((p - camPos).sqrMagnitude > maxSq) continue;
                if (!InFrustum(p, new Vector3(0.25f, 0.25f, 0.25f))) continue;
                curColor = cHidden;
                float s = 0.3f;
                Line(p + new Vector3(-s, -s, 0), p + new Vector3(s, s, 0));
                Line(p + new Vector3(-s, s, 0), p + new Vector3(s, -s, 0));
                if (showLabels && shown < 50)
                {
                    Vector3 sp = Project(p);
                    if (sp.z <= 0) continue;
                    if (h.label == null)
                    {
                        if (h.go == null) { h.alive = false; continue; }
                        h.label = h.go.name + " [" + h.kind + "]";
                    }
                    d.ShadowText(sp.x + 6, sh - sp.y - 6, h.label, cHidden, 11);
                    shown++;
                }
            }
        }

        // screen position of a wanted label; behind the camera or off screen = no label
        void AddLabel(ColliderEntry e)
        {
            Vector3 sp = Project(e.bc);
            if (sp.z <= 0) return;
            float x = sp.x, y = sh - sp.y;
            if (x < -50 || y < -20 || x > sw + 50 || y > sh + 20) return;
            float dx = x - sw * 0.5f, dy = y - sh * 0.5f;
            labels.Add(new LabelAt { d = dx * dx + dy * dy, p = new Vector2(x, y), e = e });
        }

        // At most MaxLabels labels, the ones nearest to the screen centre. A label names the object and its first
        // overlay.label.types components ("+N" says how many more); the selected object gets every component, wrapped.
        // Labels never overlap: a label that would cover one already placed moves down or up a line (a thin line then
        // joins it to its object); one that finds no free place is left out and counted in a corner note.
        const int LabelSize = 12;
        const float LabelLine = 15f;
        readonly List<Rect> placed = new List<Rect>(64);
        public int labelTypes = 3;
        int labelsHidden;
        string hiddenNote;
        int hiddenNoteFor = -1;

        void DrawLabels()
        {
            if (labels.Count > MaxLabels) labels.Sort(nearestCentreFirst);
            int n = Math.Min(labels.Count, MaxLabels);
            var sel = Selection.Current;
            placed.Clear();
            labelsHidden = labels.Count - n;
            if (sel != null && selAudioText != null)
            {
                // the selection's audio line is drawn next to it by DrawSelectionAudio: keep its place free
                Vector3 asp = Project(CenterOf(sel));
                if (asp.z > 0) placed.Add(new Rect(asp.x + 8, sh - asp.y + 8, d.Measure(selAudioText, LabelSize), LabelLine));
            }
            for (int i = 0; i < n; i++)
            {
                var l = labels[i];
                var e = l.e;
                bool selected = sel != null && ReferenceEquals(e.go, sel);
                if (e.label == null || e.labelOn != e.on || e.labelTypes != labelTypes)
                {
                    e.labelOn = e.on;
                    e.labelTypes = labelTypes;
                    e.label = LabelText(e, labelTypes);
                    e.labelFull = null;
                    e.labelW = d.Measure(e.label, LabelSize);
                }
                string text = e.label;
                float w = e.labelW, h = LabelLine;
                if (selected)
                {
                    if (e.labelFull == null) e.labelFull = WrapFull(e, Mathf.Max(320f, sw * 0.4f));
                    text = e.labelFull;
                    w = 0f;
                    int lines = 1, start = 0;
                    for (int k = 0; k <= text.Length; k++)
                    {
                        if (k < text.Length && text[k] != '\n') continue;
                        w = Mathf.Max(w, d.Measure(text.Substring(start, k - start), LabelSize));
                        start = k + 1;
                        if (k < text.Length) lines++;
                    }
                    h = LabelLine * lines;
                }
                float x = l.p.x + 4, y = l.p.y - 7;
                if (x + w > sw - 4) x = Mathf.Max(4f, sw - 4 - w);
                if (x < 4) x = 4;
                var r = new Rect(x, y, w, h);
                bool ok = Free(r);
                for (int step = 1; step <= 4 && !ok; step++)
                {
                    var down = new Rect(x, y + step * LabelLine, w, h);
                    if (Free(down)) { r = down; ok = true; break; }
                    var up = new Rect(x, y - step * LabelLine, w, h);
                    if (Free(up)) { r = up; ok = true; break; }
                }
                if (!ok && !selected) { labelsHidden++; continue; }
                placed.Add(r);
                if (Mathf.Abs(r.y - y) > 1f) d.Line(new Vector2(l.p.x, l.p.y), new Vector2(r.x, r.y + LabelLine * 0.5f), new Color(e.color.r, e.color.g, e.color.b, 0.45f));
                d.ShadowText(r.x, r.y, text, e.color, LabelSize);
            }
            if (labelsHidden > 0)
            {
                if (hiddenNoteFor != labelsHidden) { hiddenNoteFor = labelsHidden; hiddenNote = "+" + Fmt.I(labelsHidden) + " label(s) hidden (overlap or limit): select an object to see its full label, F4 for fewer labels"; }
                d.ShadowText(8, sh - 22, hiddenNote, new Color(0.8f, 0.8f, 0.8f, 0.8f), 11);
            }
        }

        bool Free(Rect r)
        {
            for (int i = 0; i < placed.Count; i++) if (placed[i].Overlaps(r)) return false;
            return true;
        }

        // "name  (off)  <A, B, C +4>"; types < 0 = every component, 0 = only the count
        static string LabelText(ColliderEntry e, int types)
        {
            var sb = new System.Text.StringBuilder(e.name);
            if (!e.on) sb.Append("  (off)");
            var list = e.typeList;
            int count = list != null ? list.Length : 0;
            if (count == 0)
            {
                if (!string.IsNullOrEmpty(e.types)) sb.Append("  <").Append(e.types).Append('>');
                return sb.ToString();
            }
            if (types == 0) return sb.Append("  <").Append(Fmt.I(count)).Append(" component(s)>").ToString();
            int show = types < 0 ? count : Math.Min(types, count);
            sb.Append("  <");
            for (int i = 0; i < show; i++) { if (i > 0) sb.Append(", "); sb.Append(list[i]); }
            if (count > show) sb.Append(" +").Append(Fmt.I(count - show));
            sb.Append('>');
            return sb.ToString();
        }

        // the name line, then the components in lines no wider than maxW
        string WrapFull(ColliderEntry e, float maxW)
        {
            var sb = new System.Text.StringBuilder(e.name);
            if (!e.on) sb.Append("  (off)");
            var list = e.typeList;
            if (list == null || list.Length == 0) return sb.ToString();
            sb.Append('\n');
            var line = new System.Text.StringBuilder("  ");
            for (int i = 0; i < list.Length; i++)
            {
                string part = list[i] + (i < list.Length - 1 ? ", " : "");
                if (line.Length > 2 && d.Measure(line.ToString() + part, LabelSize) > maxW)
                {
                    sb.Append(line.ToString().TrimEnd()).Append('\n');
                    line.Length = 0;
                    line.Append("  ");
                }
                line.Append(part);
            }
            sb.Append(line.ToString().TrimEnd());
            return sb.ToString();
        }

        // Savepoint markers. The positions and "SP s/i" texts are kept; the game is asked again when streaming changed
        // (the savepoints of an area become known when it loads) and every SpRefreshEvery seconds as a safety net.
        struct SpMark { public Vector3 p; public int sub, sp; public string text; }
        SpMark[] spMarks = new SpMark[0];
        int spCount, spStream = -1;
        float spAt = -1e9f;
        readonly Dictionary<int, string> spTexts = new Dictionary<int, string>();
        const float SpRefreshEvery = 5f;

        void RefreshSavepoints(float now)
        {
            spStream = Levels.StreamVersion; spAt = now;
            spCount = 0;
            if (!G.SavepointsReady) return;
            int subs;
            try { subs = SavepointManager.SubsceneCount; } catch { return; }
            for (int s = 0; s < subs; s++)
            {
                int n = SavepointManager.GetSubsceneSavepointCount(s);
                for (int i = 0; i < n; i++)
                {
                    Vector3 p;
                    try { p = SavepointManager.GetSavepointPosition(s, i); } catch { continue; }
                    if (p == Vector3.zero) continue;
                    if (spCount == spMarks.Length) Array.Resize(ref spMarks, Math.Max(64, spMarks.Length * 2));
                    int key = s * 100000 + i;
                    string text;
                    if (!spTexts.TryGetValue(key, out text)) spTexts[key] = text = "SP " + s + "/" + i;
                    spMarks[spCount++] = new SpMark { p = p, sub = s, sp = i, text = text };
                }
            }
        }

        void DrawSavepoints(float now)
        {
            if (spStream != Levels.StreamVersion || now - spAt > SpRefreshEvery) RefreshSavepoints(now);
            if (spCount == 0) return;
            int curSub, curSp;
            G.CurrentSavepoint(out curSub, out curSp);
            var half = new Vector3(0.5f, 0.5f, 0.5f);
            for (int k = 0; k < spCount; k++)
            {
                Vector3 p = spMarks[k].p;
                if (!InFrustum(p, half)) continue;
                curColor = (spMarks[k].sub == curSub && spMarks[k].sp == curSp) ? cSpawn : cSave;
                float dd = 0.5f;
                Line(p + new Vector3(-dd, 0, 0), p + new Vector3(0, dd, 0));
                Line(p + new Vector3(0, dd, 0), p + new Vector3(dd, 0, 0));
                Line(p + new Vector3(dd, 0, 0), p + new Vector3(0, -dd, 0));
                Line(p + new Vector3(0, -dd, 0), p + new Vector3(-dd, 0, 0));
                Line(p, p + new Vector3(0, 1.5f, 0));
                if (showLabels)
                {
                    Vector3 sp = Project(p + new Vector3(0, 1.6f, 0));
                    if (sp.z > 0) d.ShadowText(sp.x - 20, sh - sp.y - 14, spMarks[k].text, curColor, 12);
                }
            }
        }

        void DrawEntry(ColliderEntry e)
        {
            if (e.shape == 1)
            {
                Circle(e.w[0], e.wr, Vector3.right, Vector3.up);
                Circle(e.w[0], e.wr, Vector3.right, Vector3.forward);
                Circle(e.w[0], e.wr, Vector3.up, Vector3.forward);
                return;
            }
            for (int i = 0; i < 8; i++) proj[i] = Project(e.w[i]);
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit < 8; bit <<= 1)
                    if ((i & bit) == 0) Seg(proj[i], proj[i | bit]);
        }

        // world -> screen like Camera.WorldToScreenPoint (z = depth in front of the camera; <= 0 behind it)
        Vector3 Project(Vector3 p)
        {
            float x = vp.m00 * p.x + vp.m01 * p.y + vp.m02 * p.z + vp.m03;
            float y = vp.m10 * p.x + vp.m11 * p.y + vp.m12 * p.z + vp.m13;
            float w = vp.m30 * p.x + vp.m31 * p.y + vp.m32 * p.z + vp.m33;
            if (w <= 1e-5f) return new Vector3(0f, 0f, -1f);
            return new Vector3((x / w * 0.5f + 0.5f) * pr.width + pr.x, (y / w * 0.5f + 0.5f) * pr.height + pr.y, w);
        }

        void Seg(Vector3 sa, Vector3 sb)
        {
            if (sa.z <= 0 || sb.z <= 0) return;
            d.Line(new Vector2(sa.x, sh - sa.y), new Vector2(sb.x, sh - sb.y), curColor);
        }

        // view-projection frustum planes (left, right, bottom, top, near, far) as (normal, d)
        void FrustumPlanes()
        {
            var r0 = new Vector4(vp.m00, vp.m01, vp.m02, vp.m03);
            var r1 = new Vector4(vp.m10, vp.m11, vp.m12, vp.m13);
            var r2 = new Vector4(vp.m20, vp.m21, vp.m22, vp.m23);
            var r3 = new Vector4(vp.m30, vp.m31, vp.m32, vp.m33);
            fpl[0] = r3 + r0; fpl[1] = r3 - r0; fpl[2] = r3 + r1; fpl[3] = r3 - r1; fpl[4] = r3 + r2; fpl[5] = r3 - r2;
        }

        // box (centre, extents) against the six planes: the corner farthest along each plane normal decides
        bool InFrustum(Vector3 c, Vector3 ext)
        {
            for (int i = 0; i < 6; i++)
            {
                var pl = fpl[i];
                float reach = ext.x * (pl.x >= 0 ? pl.x : -pl.x) + ext.y * (pl.y >= 0 ? pl.y : -pl.y) + ext.z * (pl.z >= 0 ? pl.z : -pl.z);
                if (pl.x * c.x + pl.y * c.y + pl.z * c.z + pl.w + reach < 0) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- primitives
        void Line(Vector3 a, Vector3 b)
        {
            Seg(Project(a), Project(b));
        }

        void Circle(Vector3 c, float r, Vector3 ax, Vector3 ay)
        {
            const int seg = 20;
            Vector3 prev = c + ax * r;
            for (int i = 1; i <= seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                Vector3 p = c + (ax * Mathf.Cos(a) + ay * Mathf.Sin(a)) * r;
                Line(prev, p);
                prev = p;
            }
        }
    }
}
