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
    }

    // World overlay: trigger/collider wireframes, labels, savepoint markers.
    // Drawn from OnGUI (Repaint) in screen space so it works regardless of Playdead's custom render pipeline.
    public class Overlay
    {
        public bool showTriggers = true;
        // F2 cycles: all triggers, then one category at a time: general (yellow), savepoints (green), cameras (blue),
        // kill (red), audio (purple)
        public int triggerFilter;
        public static readonly string[] triggerFilterNames = { "all", "general (yellow)", "savepoints (green)", "cameras (blue)", "kill (red)", "audio (purple)" };
        static readonly string[] triggerFilterKinds = { null, "trigger", "save", "camera", "kill", "audio" };
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
        float nextRefresh; int seenStream = -1;
        Plane[] planes;
        Camera cam;

        public int visibleTriggers, visibleSolids;

        static readonly Color cSave = new Color(0.2f, 1f, 0.3f, 0.95f);
        static readonly Color cKill = new Color(1f, 0.2f, 0.2f, 0.95f);
        static readonly Color cCam = new Color(0.3f, 0.85f, 1f, 0.9f);
        static readonly Color cAudio = new Color(0.8f, 0.45f, 1f, 0.85f);
        static readonly Color cTrig = new Color(1f, 0.9f, 0.2f, 0.9f);
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
            if (!showTriggers && !showSolids) return;
            var all = UObj.FindObjectsOfType(typeof(Collider));
            var sb = new StringBuilder();
            foreach (var o in all)
            {
                var c = o as Collider;
                if (c == null) continue;
                bool trig = c.isTrigger;
                if (trig ? !showTriggers : !showSolids) continue;
                var e = new ColliderEntry { col = c, name = c.gameObject.name, trigger = trig };
                sb.Length = 0;
                foreach (var mb in c.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(mb.GetType().Name);
                }
                e.types = sb.ToString();
                Classify(e);
                entries.Add(e);
            }
        }

        // category of one trigger collider, same rules as the overlay colours (for picking)
        public static string TriggerKindOf(Collider c)
        {
            var e = new ColliderEntry { col = c, name = c.gameObject.name, trigger = true };
            var sb = new StringBuilder();
            foreach (var mb in c.GetComponents<MonoBehaviour>()) if (mb != null) sb.Append(mb.GetType().Name).Append(' ');
            e.types = sb.ToString();
            Classify(e);
            return e.kind == "save" ? "savepoint" : e.kind == "trigger" ? "general" : e.kind;
        }

        static void Classify(ColliderEntry e)
        {
            if (!e.trigger) { e.color = cSolid; e.kind = "solid"; return; }
            string s = (e.types + " " + e.name).ToLowerInvariant();
            if (s.Contains("savepoint") || s.Contains("checkpoint")) { e.color = cSave; e.kind = "save"; }
            else if (s.Contains("kill") || s.Contains("death") || s.Contains("die") || s.Contains("drown")) { e.color = cKill; e.kind = "kill"; }
            else if (s.Contains("camera") || s.Contains("cam")) { e.color = cCam; e.kind = "camera"; }
            else if (s.Contains("audio") || s.Contains("sound") || s.Contains("akevent") || s.Contains("music")) { e.color = cAudio; e.kind = "audio"; }
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

            var labels = new List<KeyValuePair<Vector3, ColliderEntry>>();
            Vector3 camPos = cam.transform.position;
            foreach (var e in entries)
            {
                var c = e.col;
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                if (!c.enabled && !showDisabled) continue;
                if (e.trigger && triggerFilter != 0 && e.kind != triggerFilterKinds[triggerFilter]) continue;
                Bounds b;
                try { b = c.bounds; } catch { continue; }
                if (!c.enabled && b.size == Vector3.zero) { var tt = c.transform; b = new Bounds(tt.position, tt.lossyScale); }
                if (!GeometryUtility.TestPlanesAABB(planes, b)) continue;
                if ((b.center - camPos).magnitude > maxDistance + b.extents.magnitude) continue;
                if (e.trigger) visibleTriggers++; else visibleSolids++;
                int hop = ScopeHop(c.gameObject, b);
                float a = c.enabled ? e.color.a : 0.3f;
                if (hop < 0) a *= 0.18f; else inScopeCount++;
                curColor = new Color(e.color.r, e.color.g, e.color.b, a);
                DrawCollider(c, b);
                if (showLabels && e.trigger && LabelWanted(c, b, hop)) labels.Add(new KeyValuePair<Vector3, ColliderEntry>(b.center, e));
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

        void DrawCollider(Collider c, Bounds b)
        {
            var t = c.transform;
            var box = c as BoxCollider;
            if (box != null) { LocalBox(t, box.center, box.size); return; }
            var sph = c as SphereCollider;
            if (sph != null)
            {
                Vector3 s = t.lossyScale;
                float r = sph.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                Vector3 ctr = t.TransformPoint(sph.center);
                Circle(ctr, r, Vector3.right, Vector3.up);
                Circle(ctr, r, Vector3.right, Vector3.forward);
                Circle(ctr, r, Vector3.up, Vector3.forward);
                return;
            }
            var cap = c as CapsuleCollider;
            if (cap != null)
            {
                Vector3 size = new Vector3(cap.radius * 2, cap.radius * 2, cap.radius * 2);
                size[cap.direction] = Mathf.Max(cap.height, cap.radius * 2);
                LocalBox(t, cap.center, size);
                return;
            }
            WorldBox(b.min, b.max);
        }

        // ---------------------------------------------------------------- primitives
        void Line(Vector3 a, Vector3 b)
        {
            Vector3 sa = RenderScale.W2S(cam, a);
            Vector3 sb = RenderScale.W2S(cam, b);
            if (sa.z <= 0 || sb.z <= 0) return;
            d.Line(new Vector2(sa.x, Screen.height - sa.y), new Vector2(sb.x, Screen.height - sb.y), curColor);
        }

        readonly Vector3[] corners = new Vector3[8];
        void LocalBox(Transform t, Vector3 center, Vector3 size)
        {
            Vector3 h = size * 0.5f;
            for (int i = 0; i < 8; i++)
            {
                var l = new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z);
                corners[i] = t.TransformPoint(center + l);
            }
            Edges();
        }

        void WorldBox(Vector3 min, Vector3 max)
        {
            for (int i = 0; i < 8; i++)
                corners[i] = new Vector3((i & 1) != 0 ? max.x : min.x, (i & 2) != 0 ? max.y : min.y, (i & 4) != 0 ? max.z : min.z);
            Edges();
        }

        void Edges()
        {
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit < 8; bit <<= 1)
                    if ((i & bit) == 0) Line(corners[i], corners[i | bit]);
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
