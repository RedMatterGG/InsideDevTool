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
        public bool alive, active, on;
        public int checkAt;
    }

    // World overlay: trigger/collider wireframes, labels, savepoint markers.
    // Drawn at the end of the frame in screen space so it works regardless of Playdead's custom render pipeline.
    // Cost: it is rebuilt every frame, so nothing per collider may ask the engine every frame. Each entry keeps its
    // object, shape, world corners and bounds and asks the engine (alive, active, enabled, transform) only every
    // CheckEvery frames, staggered; frustum culling and the projection run in C# from one view-projection matrix per
    // frame (WorldToScreenPoint per line end was 24 engine calls per box). A rescan classifies new colliders only.
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
        public static readonly string[] LabelLevels = { "OFF", "SELECTED", "NEARBY", "ALL" };
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

        public readonly List<ColliderEntry> entries = new List<ColliderEntry>();
        readonly Dictionary<int, ColliderEntry> known = new Dictionary<int, ColliderEntry>();
        float nextRefresh; int seenStream = -1;
        Plane[] planes;
        Camera cam;
        Matrix4x4 vp;
        Rect pr;
        float sh;
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

        public void Update()
        {
            // Phase 12: FindObjectsOfType(Collider) every second only while the editor is open; with it closed the
            // list is refreshed when streaming changes, or every 10 s
            if (Time.realtimeSinceStartup >= nextRefresh || Levels.Changed(ref seenStream))
            {
                nextRefresh = Time.realtimeSinceStartup + (Perf.editorOpen ? 1f : 10f);
                try { Refresh(); } catch (Exception e) { DevLog.Error("Overlay.Refresh", e); }
            }
        }

        public void ForceRefresh() { nextRefresh = 0f; }

        void Refresh()
        {
            entries.Clear();
            if (!showTriggers && !showSolids) { known.Clear(); return; }
            var all = UObj.FindObjectsOfType(typeof(Collider));
            var sb = new StringBuilder();
            var seen = new HashSet<int>();
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
                    Classify(e, typeNames);
                    Shape(e);
                    e.checkAt = 0;
                    known[id] = e;
                }
                entries.Add(e);
            }
            var gone = new List<int>();
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
            var t = e.t;
            if (e.shape == 0)
            {
                Vector3 h = e.ls * 0.5f;
                for (int i = 0; i < 8; i++)
                    e.w[i] = t.TransformPoint(e.lc + new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z));
                var bb = new Bounds(e.w[0], Vector3.zero);
                for (int i = 1; i < 8; i++) bb.Encapsulate(e.w[i]);
                e.b = bb;
                return;
            }
            if (e.shape == 1)
            {
                Vector3 s = t.lossyScale;
                e.wr = e.lr * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                e.w[0] = t.TransformPoint(e.lc);
                e.b = new Bounds(e.w[0], Vector3.one * e.wr * 2f);
                return;
            }
            Bounds b;
            try { b = c.bounds; } catch { e.alive = false; return; }
            if (!e.on && b.size == Vector3.zero) b = new Bounds(t.position, t.lossyScale);
            e.b = b;
            for (int i = 0; i < 8; i++) e.w[i] = new Vector3((i & 1) != 0 ? b.max.x : b.min.x, (i & 2) != 0 ? b.max.y : b.min.y, (i & 4) != 0 ? b.max.z : b.min.z);
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
        Draw d;
        public void Emit(Draw draw)
        {
            d = draw;
            visibleTriggers = visibleSolids = 0; inScopeCount = 0;
            RebuildHops();
            if (!showTriggers && !showSolids && !showSavepoints) return;
            cam = G.Cam();
            if (cam == null) return;
            planes = GeometryUtility.CalculateFrustumPlanes(cam);
            vp = cam.projectionMatrix * cam.worldToCameraMatrix;
            pr = cam.pixelRect;
            sh = Screen.height;
            FrustumPlanes();

            var labels = new List<KeyValuePair<Vector3, ColliderEntry>>();
            Vector3 camPos = cam.transform.position;
            int f = Time.frameCount;
            foreach (var e in entries)
            {
                if (f >= e.checkAt) Check(e, f);
                if (!e.alive || !e.active) continue;
                if (!e.on && !showDisabled) continue;
                if (e.trigger && triggerFilter != 0 && e.kind != triggerFilterKinds[triggerFilter]) continue;
                var b = e.b;
                if (!InFrustum(b)) continue;
                if ((b.center - camPos).magnitude > maxDistance + b.extents.magnitude) continue;
                if (e.trigger) visibleTriggers++; else visibleSolids++;
                int hop = ScopeHop(e.go, b);
                float a = e.on ? e.color.a : 0.3f;
                if (hop < 0) a *= 0.18f; else inScopeCount++;
                curColor = new Color(e.color.r, e.color.g, e.color.b, a);
                DrawEntry(e);
                if (showLabels && e.trigger && LabelWanted(e.col, b, hop)) labels.Add(new KeyValuePair<Vector3, ColliderEntry>(b.center, e));
            }
            if (showLinks) DrawLinks();
            DrawAudioEmitters();
            if (showSavepoints) DrawSavepoints();
            if (showHidden && hiddenEntries != null) DrawHidden(camPos);
            var ch = G.MainCharacter;
            if (ch != null)
            {
                curColor = Color.white;
                var p = ch.pos3;
                Line(p + new Vector3(-0.4f, 0, 0), p + new Vector3(0.4f, 0, 0));
                Line(p + new Vector3(0, -0.4f, 0), p + new Vector3(0, 0.4f, 0));
            }
            if (labels.Count > 0) DrawLabels(labels);
        }

        // ---------------------------------------------------------------- relationship scope
        // BFS over the reference index (both directions, noise edges skipped) from the selection.
        void RebuildHops()
        {
            var sel = Selection.Current;
            int want = scope == 1 ? 1 : scope == 2 ? 2 : 0;
            if (hopVersion == Selection.Version && hopScope == scope && (hopEdges == ReferenceIndex.edges || Time.realtimeSinceStartup - hopAt < 2f)) return;
            hopVersion = Selection.Version; hopScope = scope; hopEdges = ReferenceIndex.edges; hopAt = Time.realtimeSinceStartup;
            hopOf.Clear();
            if (sel == null) return;
            int start = sel.GetInstanceID();
            hopOf[start] = 0;
            var frontier = new List<int> { start };
            for (int h = 1; h <= want && frontier.Count > 0 && hopOf.Count < 400; h++)
            {
                var next = new List<int>();
                foreach (var id in frontier)
                {
                    foreach (var e in ReferenceIndex.References(id)) if (!e.noise && e.dstGo != 0 && !hopOf.ContainsKey(e.dstGo)) { hopOf[e.dstGo] = h; next.Add(e.dstGo); }
                    foreach (var e in ReferenceIndex.Referencers(id)) if (!e.noise && e.srcGo != 0 && !hopOf.ContainsKey(e.srcGo)) { hopOf[e.srcGo] = h; next.Add(e.srcGo); }
                }
                frontier = next;
            }
        }

        // hop distance of a collider's object from the selection (-1 = out of scope). The collider may sit on a
        // child of the related object (common for trigger volumes), so the parent chain is checked up to 3 levels.
        public int ScopeHop(GameObject g, Bounds b)
        {
            var sel = Selection.Current;
            if (scope == 4 || sel == null) return 0;
            if (scope == 3) { var ch = G.MainCharacter; return ch != null && (b.ClosestPoint(ch.pos3) - ch.pos3).sqrMagnitude < 15f * 15f ? 0 : -1; }
            var t = g.transform;
            for (int i = 0; i < 4 && t != null; i++, t = t.parent)
            {
                int h;
                if (hopOf.TryGetValue(t.gameObject.GetInstanceID(), out h)) return h;
            }
            return -1;
        }

        // lines from the selection to the objects one/two hops away (world <-> graph sync)
        void DrawLinks()
        {
            var sel = Selection.Current;
            if (sel == null || hopOf.Count <= 1 || scope > 2) return;
            Vector3 from = CenterOf(sel);
            int n = 0;
            foreach (var kv in hopOf)
            {
                if (kv.Value == 0 || n > 80) continue;
                var r = ObjectDatabase.Get(kv.Key);
                if (r == null || r.go == null || !r.go.activeInHierarchy) continue;
                n++;
                bool fired = EventMonitor.FiredAge(sel.GetInstanceID(), kv.Key) < 2.5f || EventMonitor.FiredAge(kv.Key, sel.GetInstanceID()) < 2.5f;
                curColor = fired ? new Color(0.3f, 1f, 0.4f, 0.95f) : kv.Value == 1 ? new Color(0.35f, 0.9f, 1f, 0.7f) : new Color(0.35f, 0.9f, 1f, 0.3f);
                Line(from, CenterOf(r.go));
            }
        }

        // §89: the Wwise event selected in the Audio DB -> its emitters in the world (game references that are loaded
        // + every object that posted it this session); flashes green while it is playing
        // §88: the selection's own audio - what it posted this session and what its game data references
        void DrawSelectionAudio()
        {
            var sel = Selection.Current;
            if (sel == null) return;
            int id = sel.GetInstanceID();
            if (selAudioFor != id || Time.realtimeSinceStartup - selAudioAt > 1f)
            {
                selAudioFor = id; selAudioAt = Time.realtimeSinceStartup; selAudioLast = -1f;
                var names = new List<string>();
                foreach (var p in AudioTrace.plays.Values)
                    if ((int)p.gameObj == id) { string n = AudioTrace.Name(p.eventId); if (!names.Contains(n) && names.Count < 4) names.Add(n); if (p.t > selAudioLast) selAudioLast = p.t; }
                string path = Inspector.PathOf(sel.transform);
                int refs = 0;
                foreach (var r in AudioDb.refs.Values) if (r.path == path && r.prov != "OBSERVED") { refs++; if (!names.Contains(r.eventName) && names.Count < 6) names.Add(r.eventName); }
                selAudioText = names.Count > 0 ? "audio: " + string.Join(", ", names.ToArray()) + (refs > 0 ? "  (" + refs + " in its data)" : "") : null;
            }
            if (selAudioText == null) return;
            bool live = selAudioLast >= 0 && Time.realtimeSinceStartup - selAudioLast < 2f;
            Vector3 sp = RenderScale.W2S(cam, CenterOf(sel));
            if (sp.z > 0) d.ShadowText(sp.x + 8, Screen.height - sp.y + 8, selAudioText, live ? new Color(0.3f, 1f, 0.45f, 1f) : new Color(0.45f, 0.85f, 1f, 1f), 12);
        }
        int selAudioFor; float selAudioAt, selAudioLast = -1f; string selAudioText;

        void DrawAudioEmitters()
        {
            DrawGraphHover();
            DrawSelectionAudio();
            var rec = AudioDbPanel.Current;
            if (rec == null || (rec.kind != AudioDb.Kind.Event && rec.kind != AudioDb.Kind.Bank)) return;
            bool bank = rec.kind == AudioDb.Kind.Bank;
            if (emitFor != rec.id || Time.realtimeSinceStartup - emitAt > 1f)
            {
                emitFor = rec.id; emitAt = Time.realtimeSinceStartup; emitters.Clear(); emitterEvent.Clear();
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
            }
            AudioTrace.EvStat st; float age = !bank && AudioTrace.evStats.TryGetValue(rec.id, out st) ? Time.realtimeSinceStartup - st.lastT : 1e9f;
            bool live = age < 2f;
            var ch = G.MainCharacter;
            for (int ei = 0; ei < emitters.Count; ei++)
            {
                var g = emitters[ei];
                if (g == null) continue;
                Vector3 c = CenterOf(g);
                curColor = live ? new Color(0.3f, 1f, 0.45f, 1f - age * 0.4f) : new Color(0.45f, 0.85f, 1f, 0.9f);
                float r = 0.35f;
                Line(c + Vector3.left * r, c + Vector3.right * r); Line(c + Vector3.up * r, c + Vector3.down * r);
                Line(c + new Vector3(-r, -r, 0) * 0.7f, c + new Vector3(r, r, 0) * 0.7f); Line(c + new Vector3(-r, r, 0) * 0.7f, c + new Vector3(r, -r, 0) * 0.7f);
                if (ch != null) { curColor = new Color(curColor.r, curColor.g, curColor.b, 0.25f); Line(ch.pos3, c); }
                Vector3 sp = RenderScale.W2S(cam, c);
                if (sp.z > 0) d.ShadowText(sp.x + 8, Screen.height - sp.y - 7, "audio " + emitterEvent[ei] + "  on " + g.name + (bank ? "  [" + rec.Name + "]" : ""), live ? new Color(0.3f, 1f, 0.45f, 1f) : new Color(0.45f, 0.85f, 1f, 1f), 12);
            }
        }
        readonly List<GameObject> emitters = new List<GameObject>(); readonly List<string> emitterEvent = new List<string>(); uint emitFor; float emitAt;

        // §89: the graph edge under the mouse, drawn between the two objects in the world
        void DrawGraphHover()
        {
            if (Time.realtimeSinceStartup - GraphView.HoverAt > 0.15f) return;
            var a = ObjectDatabase.Get(GraphView.HoverA); var b = ObjectDatabase.Get(GraphView.HoverB);
            if (a == null || b == null || a.go == null || b.go == null) return;
            Vector3 pa = CenterOf(a.go), pb = CenterOf(b.go);
            curColor = new Color(1f, 0.85f, 0.3f, 1f);
            Line(pa, pb); Line(pa + Vector3.up * 0.03f, pb + Vector3.up * 0.03f);
            float r = 0.25f;
            foreach (var c in new[] { pa, pb }) { Line(c + Vector3.left * r, c + Vector3.right * r); Line(c + Vector3.up * r, c + Vector3.down * r); }
        }

        static Vector3 CenterOf(GameObject g)
        {
            var c = g.GetComponent<Collider>();
            if (c != null) { try { return c.bounds.center; } catch { } }
            var rd = g.GetComponent<Renderer>();
            if (rd != null) { try { return rd.bounds.center; } catch { } }
            return g.transform.position;
        }

        bool LabelWanted(Collider c, Bounds b, int hop)
        {
            if (labelLevel <= 0) return false;
            if (labelLevel == 1) return Selection.Current != null && (c.gameObject == Selection.Current || (hop >= 0 && scope <= 2));
            if (labelLevel == 2) { var ch = G.MainCharacter; return ch != null && (b.ClosestPoint(ch.pos3) - ch.pos3).sqrMagnitude < 15f * 15f; }
            return true;
        }

        Color curColor;
        static readonly Color cHidden = new Color(1f, 0.35f, 0.9f, 0.95f);

        void DrawHidden(Vector3 camPos)
        {
            int labels = 0;
            foreach (var h in hiddenEntries)
            {
                if (h.go == null) continue;
                var p = h.go.transform.position;
                if ((p - camPos).magnitude > maxDistance) continue;
                if (!GeometryUtility.TestPlanesAABB(planes, new Bounds(p, Vector3.one * 0.5f))) continue;
                curColor = cHidden;
                float s = 0.3f;
                Line(p + new Vector3(-s, -s, 0), p + new Vector3(s, s, 0));
                Line(p + new Vector3(-s, s, 0), p + new Vector3(s, -s, 0));
                if (showLabels && labels < 50)
                {
                    Vector3 sp = RenderScale.W2S(cam, p);
                    if (sp.z > 0) { d.ShadowText(sp.x + 6, Screen.height - sp.y - 6, h.go.name + " [" + h.kind + "]", cHidden, 11); labels++; }
                }
            }
        }

        void DrawLabels(List<KeyValuePair<Vector3, ColliderEntry>> labels)
        {
            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            var scr = new List<KeyValuePair<float, KeyValuePair<Vector2, ColliderEntry>>>();
            foreach (var kv in labels)
            {
                Vector3 sp = RenderScale.W2S(cam, kv.Key);
                if (sp.z <= 0) continue;
                var gp = new Vector2(sp.x, Screen.height - sp.y);
                if (gp.x < -50 || gp.y < -20 || gp.x > Screen.width + 50 || gp.y > Screen.height + 20) continue;
                scr.Add(new KeyValuePair<float, KeyValuePair<Vector2, ColliderEntry>>((gp - center).sqrMagnitude, new KeyValuePair<Vector2, ColliderEntry>(gp, kv.Value)));
            }
            scr.Sort((a, b) => a.Key.CompareTo(b.Key));
            int n = Math.Min(scr.Count, 60);
            for (int i = 0; i < n; i++)
            {
                var gp = scr[i].Value.Key;
                var e = scr[i].Value.Value;
                string text = e.name + (e.col != null && !e.col.enabled ? "  (off)" : "") + (e.types.Length > 0 ? "  <" + e.types + ">" : "");
                d.ShadowText(gp.x + 4, gp.y - 7, text, e.color, 12);
            }
        }

        void DrawSavepoints()
        {
            if (!G.SavepointsReady) return;
            int curSub, curSp;
            G.CurrentSavepoint(out curSub, out curSp);
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
                    if (!GeometryUtility.TestPlanesAABB(planes, new Bounds(p, Vector3.one))) continue;
                    curColor = (s == curSub && i == curSp) ? cSpawn : cSave;
                    float dd = 0.5f;
                    Line(p + new Vector3(-dd, 0, 0), p + new Vector3(0, dd, 0));
                    Line(p + new Vector3(0, dd, 0), p + new Vector3(dd, 0, 0));
                    Line(p + new Vector3(dd, 0, 0), p + new Vector3(0, -dd, 0));
                    Line(p + new Vector3(0, -dd, 0), p + new Vector3(-dd, 0, 0));
                    Line(p, p + new Vector3(0, 1.5f, 0));
                    if (showLabels)
                    {
                        Vector3 sp = RenderScale.W2S(cam, p + new Vector3(0, 1.6f, 0));
                        if (sp.z > 0) d.ShadowText(sp.x - 20, Screen.height - sp.y - 14, "SP " + s + "/" + i, curColor, 12);
                    }
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

        bool InFrustum(Bounds b)
        {
            Vector3 mn = b.min, mx = b.max;
            for (int i = 0; i < 6; i++)
            {
                var pl = fpl[i];
                float px = pl.x >= 0 ? mx.x : mn.x, py = pl.y >= 0 ? mx.y : mn.y, pz = pl.z >= 0 ? mx.z : mn.z;
                if (pl.x * px + pl.y * py + pl.z * pz + pl.w < 0) return false;
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
