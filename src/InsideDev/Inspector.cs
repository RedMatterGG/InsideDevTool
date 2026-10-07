using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    // Scene browser + component/field inspector (reflection based, edits bool/number/string fields).
    public class Inspector
    {
        // Phase 5: the selection lives in Selection; the inspector is one view of it.
        public GameObject selected { get { return Selection.Current; } }
        public enum Mode { Normal, Advanced, Raw }
        public Mode mode = Mode.Normal;
        static readonly string[] ModeNames = { "Normal", "Advanced", "Raw" };
        public string filter = "";
        readonly List<Transform> results = new List<Transform>();
        readonly HashSet<string> expanded = new HashSet<string>();
        readonly Dictionary<string, string> edits = new Dictionary<string, string>();
        string posX = "", posY = "", posZ = "";
        readonly string[] rotS = { "", "", "" }, sclS = { "", "", "" };
        static readonly string[] Axis = { "X", "Y", "Z" };
        int posFor, seenVersion = -1;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public Inspector() { mode = (Mode)Mathf.Clamp(EditorState.Get("inspector.mode", 0), 0, 2); }

        public void Select(GameObject go) { Selection.Set(go, "inspector"); }

        void SyncSelection()
        {
            if (Selection.Version == seenVersion) return;
            seenVersion = Selection.Version;
            expanded.Clear();
            edits.Clear();
            posFor = 0;
            if (Selection.Focus != null) expanded.Add("c" + Selection.Focus.GetInstanceID());
        }

        // All scene transforms, including inactive (hidden) ones. FindObjectsOfType skips inactive objects,
        // so walk down from every root instead (GetChild returns inactive children too).
        public static List<Transform> AllSceneTransforms()
        {
            // served by the RuntimeObjectDatabase (incremental scan); refreshed synchronously only if stale
            try
            {
                ObjectDatabase.EnsureFresh(3f);
                if (ObjectDatabase.Ready)
                {
                    var list = new List<Transform>(ObjectDatabase.all.Count);
                    foreach (var r in ObjectDatabase.all) if (r.t != null) list.Add(r.t);
                    return list;
                }
            }
            catch (Exception e) { DevLog.Error("AllSceneTransforms via database", e); }
            return WalkSceneTransforms();
        }

        public static List<Transform> WalkSceneTransforms()
        {
            var roots = new HashSet<Transform>();
            foreach (var o in UObj.FindObjectsOfType(typeof(Transform)))
            {
                var t = o as Transform;
                if (t != null) roots.Add(t.root);
            }
            // inactive roots: only visible through FindObjectsOfTypeAll (which also returns prefab assets;
            // assets are normally activeSelf=true, so keep only inactive, non-hidden ones)
            foreach (var o in Resources.FindObjectsOfTypeAll(typeof(GameObject)))
            {
                var g = o as GameObject;
                if (g == null || g.hideFlags != HideFlags.None || g.transform.parent != null || g.activeSelf) continue;
                roots.Add(g.transform);
            }
            var res = new List<Transform>(20000);
            var stack = new Stack<Transform>();
            foreach (var r in roots) stack.Push(r);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                res.Add(t);
                for (int i = t.childCount - 1; i >= 0; i--) stack.Push(t.GetChild(i));
            }
            return res;
        }

        // query: plain text = name contains; "t:Type" = has a component whose type name contains Type;
        // add " hidden" to only list inactive objects.  Empty = scene roots.
        List<SmartFind.Hit> smart; string smartHead;
        public void Search(string text)
        {
            filter = text ?? "";
            results.Clear(); smart = null;
            string q = filter.Trim();
            // plain words: ranked + described results (SmartFind), clutter hidden; t:Type / hidden keep the old list
            if (q.Length > 0 && !q.StartsWith("t:", StringComparison.OrdinalIgnoreCase) && !q.EndsWith(" hidden", StringComparison.OrdinalIgnoreCase) && !q.Equals("hidden", StringComparison.OrdinalIgnoreCase) && ObjectDatabase.Ready)
            {
                int hc, hp;
                smart = SmartFind.Run(q, SmartFind.Cat.All, false, false, out hc, out hp);
                foreach (var h in smart) h.text = SmartFind.Describe(h);
                smartHead = smart.Count + " result(s), best first" + (hc > 0 ? "  (" + hc + " clutter hidden - use the Explorer to show it)" : "");
                DevLog.Write("inspector: " + smart.Count + " ranked result(s) for '" + filter + "'");
                return;
            }
            bool onlyHidden = false;
            if (q.EndsWith(" hidden", StringComparison.OrdinalIgnoreCase) || q.Equals("hidden", StringComparison.OrdinalIgnoreCase))
            { onlyHidden = true; q = q.Length > 6 ? q.Substring(0, q.Length - 7).Trim() : ""; }
            string typeQ = null;
            if (q.StartsWith("t:", StringComparison.OrdinalIgnoreCase)) { typeQ = q.Substring(2).Trim().ToLowerInvariant(); q = ""; }
            string f = q.ToLowerInvariant();
            foreach (var t in AllSceneTransforms())
            {
                if (t == null) continue;
                if (onlyHidden && t.gameObject.activeInHierarchy) continue;
                if (f.Length == 0 && typeQ == null && !onlyHidden) { if (t.parent == null) results.Add(t); continue; }
                if (f.Length > 0 && !t.name.ToLowerInvariant().Contains(f)) continue;
                if (typeQ != null)
                {
                    bool hit = false;
                    foreach (var c in t.GetComponents<Component>())
                        if (c != null && c.GetType().Name.ToLowerInvariant().Contains(typeQ)) { hit = true; break; }
                    if (!hit) continue;
                }
                results.Add(t);
            }
            results.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            DevLog.Write("inspector: " + results.Count + " result(s) for '" + filter + "'");
        }

        public static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        public void Draw(UI ui)
        {
            SyncSelection();
            ui.BeginRow();
            if (ui.Button("<", 22, Selection.CanBack ? (Color?)null : UI.BgAlt) && Selection.CanBack) Selection.Back();
            if (ui.Button(">", 22, Selection.CanForward ? (Color?)null : UI.BgAlt) && Selection.CanForward) Selection.Forward();
            ui.Label("Find:", null, 34);
            bool enter = ui.TextField("insp_find", ref filter, Mathf.Max(80, ui.Width - 250));
            if (ui.Button("Search") || enter) { Search(filter); Selection.Clear("inspector"); }
            if (ui.Button("Roots")) { Search(""); Selection.Clear("inspector"); }
            if (selected != null && ui.Button("Deselect")) Selection.Clear("inspector");
            ui.EndRow();
            int m = ui.Tabs((int)mode, ModeNames);
            if (m != (int)mode) { mode = (Mode)m; EditorState.Set("inspector.mode", m); }
            SyncSelection();

            float h = ui.Remaining;
            if (selected == null)
            {
                if (smart != null)
                {
                    ui.Label(smartHead, UI.Dim);
                    var l = smart;
                    ui.VirtualList("insp_smart", l.Count, UI.ItemPitch, ui.Remaining, i =>
                    {
                        var hh = l[i]; if (hh.r.go == null) { ui.Item("(destroyed) " + hh.r.name, UI.Dim); return; }
                        if (ui.Item(hh.text, !hh.r.activeSelf ? new Color(1f, 0.45f, 0.9f, 1f) : hh.r.activeInHierarchy ? UI.Txt : UI.Dim)) Select(hh.r.go);
                        if (ui.LastHover) Selection.SetHover(hh.r.go);
                    });
                    return;
                }
                ui.BeginScroll("insp_list", h);
                int shown = 0;
                foreach (var t in results)
                {
                    if (t == null) continue;
                    if (++shown > 500) { ui.Label("… " + (results.Count - 500) + " more — refine the search", UI.Dim); break; }
                    bool act = t.gameObject.activeInHierarchy;
                    string label = (t.parent == null ? t.name : PathOf(t)) + (t.childCount > 0 ? "   (" + t.childCount + ")" : "") + (!t.gameObject.activeSelf ? "   [HIDDEN - off]" : !act ? "   [hidden by parent]" : "");
                    if (ui.Item(label, !t.gameObject.activeSelf ? new Color(1f, 0.45f, 0.9f, 1f) : act ? UI.Txt : UI.Dim)) Select(t.gameObject);
                }
                if (results.Count == 0) ui.Label("Press Roots, or search:  name   |   t:ComponentType   |   append ' hidden' for inactive only (e.g. 't:Secret hidden')", UI.Dim);
                ui.EndScroll();
                return;
            }
            ui.BeginScroll("insp_detail", h);
            try { DrawSelected(ui); }
            catch (Exception e) { ui.Label("inspector error: " + e.Message, Color.red); }
            ui.EndScroll();
        }

        GameObject pickChild;
        void DrawSelected(UI ui)
        {
            if (Reparent.pickFor != 0 && pickChild != null && selected != null && selected != pickChild)
            {
                var child = pickChild; Reparent.pickFor = 0; pickChild = null;
                DevLog.Write(Reparent.Do(child, selected.transform, "inspector"));
                Select(child);
            }
            var go = selected;
            if (go == null) { ui.Label("(destroyed)"); return; }
            var t = go.transform;
            ui.Label(PathOf(t), UI.Accent);
            if (mode != Mode.Normal)
            {
                string selStr = "";
                try { selStr = ObjectSelector.From(go).ToString(); } catch { }
                ui.Label("selector: " + selStr + (mode == Mode.Raw ? "   instance #" + go.GetInstanceID() + " (session only)" : ""), UI.Dim);
            }
            ui.BeginRow();
            bool act = ui.Toggle(go.activeSelf, go.activeSelf ? (go.activeInHierarchy ? "active" : "active (but a parent is off)") : "active  <- HIDDEN, tick to show");
            if (act != go.activeSelf) { Changes.Record(go, null, "active", go.activeSelf); go.SetActive(act); }
            ui.Label("layer " + go.layer + (go.tag != "Untagged" ? "   tag " + go.tag : ""), UI.Dim, 160);
            if (t.parent != null && ui.Button("Parent")) { Select(t.parent.gameObject); ui.EndRow(); return; }
            if (ui.Button("Boy here")) G.Teleport(t.position);
            ui.EndRow();
            // re-parenting (keeps the world position)
            ui.BeginRow();
            ui.Label("parent: " + (t.parent != null ? t.parent.name : "(scene root)"), UI.Dim, 220);
            if (t.parent != null && ui.Button("move up a level")) Reparent.Do(go, t.parent.parent, "inspector");
            if (Reparent.pickFor == go.GetInstanceID()) { if (ui.Button("cancel pick")) Reparent.pickFor = 0; }
            else if (ui.Button("pick new parent")) { Reparent.pickFor = go.GetInstanceID(); pickChild = go; }
            if (Reparent.Moved(go) && ui.Button("restore parent")) Reparent.Restore(go);
            ui.EndRow();
            if (Reparent.pickFor == go.GetInstanceID()) ui.Label("   now select the new parent (Explorer, P pick, or the Parent/child buttons); it moves when you select it", new Color(1f, 0.6f, 0.3f, 1f));

            bool refresh = posFor != go.GetInstanceID();
            posFor = go.GetInstanceID();
            // position (world), rotation (local euler), scale (local): drag the X/Y/Z tabs or type + Enter
            var ps = new[] { posX, posY, posZ };
            var np = VecRow(ui, "pos", "p", t.position, ps, 0.01f, refresh, go);
            posX = ps[0]; posY = ps[1]; posZ = ps[2];
            if (np != t.position) { TransformMemory.Remember(t); ChangeRecorder.Before(go, ChangeRecorder.PropKind.Position, "inspector"); t.position = np; }
            var nr = VecRow(ui, "rot", "r", t.localEulerAngles, rotS, 0.5f, refresh);
            if (nr != t.localEulerAngles) { TransformMemory.Remember(t); ChangeRecorder.Before(go, ChangeRecorder.PropKind.LocalEuler, "inspector"); t.localEulerAngles = nr; }
            var nsc = VecRow(ui, "scale", "s", t.localScale, sclS, 0.005f, refresh);
            if (nsc != t.localScale) { TransformMemory.Remember(t); ChangeRecorder.Before(go, ChangeRecorder.PropKind.LocalScale, "inspector"); t.localScale = nsc; }
            if (TransformMemory.Has(t) || TransformMemory.Count > 0)
            {
                ui.BeginRow();
                if (TransformMemory.Has(t) && ui.Button("Reset to original")) TransformMemory.Restore(t);
                if (TransformMemory.Count > 0 && ui.Button("Reset all moved objects (" + TransformMemory.Count + ")")) TransformMemory.RestoreAll();
                ui.EndRow();
            }
            ui.Label("Drag X/Y/Z left/right to change (Shift = fine, Ctrl = fast) or type a value and press Enter.", UI.Dim);

            if (t.childCount > 0)
            {
                ui.Label("Children (" + t.childCount + ")", UI.Dim);
                int n = Math.Min(t.childCount, 80);
                for (int i = 0; i < n; i++)
                {
                    var c = t.GetChild(i);
                    bool ck = ui.Item("    " + c.name + (c.childCount > 0 ? "   (" + c.childCount + ")" : ""), c.gameObject.activeSelf ? UI.Txt : UI.Dim);
                    if (ui.LastHover) Selection.SetHover(c.gameObject);
                    if (ck) { Select(c.gameObject); return; }
                }
                if (t.childCount > n) ui.Label("    … " + (t.childCount - n) + " more", UI.Dim);
            }

            try { Guide.Draw(ui, go); } catch (Exception e) { ui.Label("guide error: " + e.Message, Color.red); }
            try { Adapters.Draw(ui, go); } catch (Exception e) { ui.Label("adapters error: " + e.Message, Color.red); }
            if (go != selected) return;
            try { ActivitySection.Draw(ui, go); } catch (Exception e) { ui.Label("activity error: " + e.Message, Color.red); }
            if (go != selected) return;
            try { FactsSections.Draw(ui, go); } catch (Exception e) { ui.Label("facts error: " + e.Message, Color.red); }
            if (go != selected) return;
            try { MemorySection.Draw(ui, go); } catch (Exception e) { ui.Label("memory error: " + e.Message, Color.red); }
            if (go != selected) return;
            try { Links.Draw(ui, go, this); } catch (Exception e) { ui.Label("links error: " + e.Message, Color.red); }
            if (go != selected) return;
            try { DrawRefs(ui, go); } catch (Exception e) { ui.Label("references error: " + e.Message, Color.red); }
            if (go != selected) return;
            ui.Label("Components", UI.Dim);
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) { ui.Label("   (missing script)", UI.Dim); continue; }
                string id = "c" + comp.GetInstanceID();
                bool open = expanded.Contains(id);
                ui.BeginRow();
                var beh = comp as Behaviour; var col = comp as Collider; var ren = comp as Renderer;
                if (beh != null) { bool en = ui.Toggle(beh.enabled, "", 20); if (en != beh.enabled) { Changes.Record(go, beh, beh.GetType().Name, beh.enabled); beh.enabled = en; } }
                else if (col != null) { bool en = ui.Toggle(col.enabled, "", 20); if (en != col.enabled) { Changes.Record(go, col, col.isTrigger ? "trigger" : "collider", col.enabled); col.enabled = en; } }
                else if (ren != null) { bool en = ui.Toggle(ren.enabled, "", 20); if (en != ren.enabled) { Changes.Record(go, ren, "renderer", ren.enabled); ren.enabled = en; } }
                else ui.Space(24);
                if (ui.Item((open ? "v " : "> ") + (mode == Mode.Normal ? comp.GetType().Name : comp.GetType().FullName) + (mode == Mode.Raw ? "   #" + comp.GetInstanceID() : ""), UI.Accent))
                {
                    if (open) expanded.Remove(id); else expanded.Add(id);
                }
                ui.EndRow();
                if (open) DrawFields(ui, comp);
            }
        }

        // ---------------------------------------------------------------- references (reverse-reference index)
        bool refsOpen = true, refByOpen = true, chainOpen;
        static readonly Color cRef = new Color(1f, 0.75f, 0.35f, 1f), cRefBy = new Color(0.45f, 0.85f, 1f, 1f);

        int refsGo; float refsAt; bool refsChainBuilt; string refsHeadOut = "", refsHeadIn = "";
        List<RefEdge> refsOut = new List<RefEdge>(), refsIn = new List<RefEdge>();
        readonly List<string> refsOutText = new List<string>(), refsInText = new List<string>(), refsChainText = new List<string>();
        readonly List<ObjRecord> refsChain = new List<ObjRecord>();
        void DrawRefs(UI ui, GameObject go)
        {
            // lists and texts are rebuilt once a second (or on a new selection), not every frame: Describe() per
            // edge per frame was a large share of the editor's garbage
            int gid = go.GetInstanceID();
            if (gid != refsGo || Time.realtimeSinceStartup - refsAt > 1f || refsChainBuilt != chainOpen)
            {
                refsGo = gid; refsAt = Time.realtimeSinceStartup; refsChainBuilt = chainOpen;
                refsOut = ReferenceIndex.Filter(ReferenceIndex.References(go), false);
                refsIn = ReferenceIndex.Filter(ReferenceIndex.Referencers(go), false);
                refsOutText.Clear(); foreach (var e in refsOut) refsOutText.Add("   " + ReferenceIndex.Describe(e, true));
                refsInText.Clear(); foreach (var e in refsIn) refsInText.Add("   " + ReferenceIndex.Describe(e, false));
                refsHeadOut = "References (" + refsOut.Count + ")"; refsHeadIn = "Referenced by (" + refsIn.Count + ")";
                refsChain.Clear(); refsChainText.Clear();
                if (chainOpen)
                    foreach (var kv in ReferenceIndex.Chain(gid, true, 4, 60))
                    {
                        var r = ObjectDatabase.Get(kv.Key);
                        refsChain.Add(r); refsChainText.Add("   -> " + (r != null ? r.name : "#" + kv.Key) + "   via " + ReferenceIndex.NameOf(kv.Value.srcGo) + " " + kv.Value.srcType + "." + kv.Value.field);
                    }
            }
            var outs = refsOut; var ins = refsIn;
            ui.BeginRow();
            if (ui.Item((refsOpen ? "v " : "> ") + refsHeadOut, cRef, 180)) refsOpen = !refsOpen;
            if (ui.Item((refByOpen ? "v " : "> ") + refsHeadIn, cRefBy, 190)) refByOpen = !refByOpen;
            if (ui.Item((chainOpen ? "v " : "> ") + "Dependency chain", UI.Dim, 150)) chainOpen = !chainOpen;
            ui.EndRow();
            if (ReferenceIndex.Pending > 0) ui.Label("   index still building (" + ReferenceIndex.Pending + " objects queued) - referencers may be incomplete", UI.Dim);
            if (refsOpen)
            {
                if (outs.Count == 0) ui.Label("   no outgoing references found", UI.Dim);
                for (int i = 0; i < outs.Count; i++)
                {
                    var e = outs[i];
                    GameObject target = e.dst as GameObject ?? (e.dst is Component ? ((Component)e.dst).gameObject : null);
                    bool ck = ui.Item(refsOutText[i], e.DstAlive ? cRef : UI.Dim);
                    if (ui.LastHover && target != null) Selection.SetHover(target);
                    if (ck && target != null && e.dstGo != 0) { Select(target); return; }
                }
            }
            if (refByOpen)
            {
                if (ins.Count == 0) ui.Label("   No incoming reference found in the current loaded scenes / reference index.", UI.Dim);
                for (int i = 0; i < ins.Count; i++)
                {
                    var e = ins[i];
                    var src = ObjectDatabase.Get(e.srcGo);
                    bool ck = ui.Item(refsInText[i], cRefBy);
                    if (ui.LastHover && src != null) Selection.SetHover(src.go);
                    if (ck && src != null) { Select(src.go); return; }
                }
            }
            if (chainOpen)
            {
                if (refsChain.Count == 0) ui.Label("   (leads nowhere within 4 steps)", UI.Dim);
                for (int i = 0; i < refsChain.Count; i++)
                {
                    var r = refsChain[i];
                    if (ui.Item(refsChainText[i], UI.Txt) && r != null) { Select(r.go); return; }
                }
            }
        }

        // gizmoFor != null: the row also gets the move-gizmo button for that object (greyed while the global
        // "gizmo on every selection" cheat is on)
        Vector3 VecRow(UI ui, string label, string id, Vector3 v, string[] s, float speed, bool refresh, GameObject gizmoFor = null)
        {
            ui.BeginRow();
            ui.Label(label, UI.Dim, 38);
            var res = v;
            for (int i = 0; i < 3; i++)
            {
                bool dragging = ui.IsDragging(id + i);
                float nv = ui.DragHandle(id + i, Axis[i], v[i], speed);
                if (nv != v[i]) res[i] = nv;
                if (refresh || dragging || nv != v[i] || !ui.FocusIs(id + "t" + i)) s[i] = res[i].ToString("F3", IC);
                if (ui.TextField(id + "t" + i, ref s[i], 78))
                {
                    float f;
                    if (float.TryParse(s[i], NumberStyles.Float, IC, out f)) res[i] = f;
                    ui.focus = null;
                }
            }
            if (gizmoFor != null)
            {
                if (TransformGizmo.globalOn) ui.ButtonDisabled("gizmo (global on)", "turned on for every selection in Cheats");
                else
                {
                    bool on = TransformGizmo.IsOnFor(gizmoFor);
                    if (ui.Button(on ? "gizmo ON" : "gizmo", -2, on ? UI.TabSel : (Color?)null)) TransformGizmo.SetFor(gizmoFor, !on);
                }
            }
            ui.EndRow();
            return res;
        }

        void DrawFields(UI ui, Component comp)
        {
            var col = comp as Collider;
            if (col != null)
            {
                bool tr = ui.Toggle(col.isTrigger, "isTrigger");
                if (tr != col.isTrigger) col.isTrigger = tr;
                ui.Label("      bounds " + col.bounds.center.ToString("F2") + "  size " + col.bounds.size.ToString("F2"), UI.Dim);
            }
            fieldRoot = comp;
            int count = DrawObjectFields(ui, comp, "c" + comp.GetInstanceID(), 0);
            fieldRoot = null;
            if (count == 0 && col == null) ui.Label("      (no " + (mode == Mode.Normal ? "serialized " : "") + "script fields" + (mode == Mode.Normal ? " - Advanced shows private state" : "") + ")", UI.Dim);
            var an = comp as Animation;
            if (an != null) DrawAnimation(ui, an);
            if (comp is MonoBehaviour) DrawMethods(ui, comp);
        }

        // ---------------------------------------------------------------- animation clips (legacy Animation)
        void DrawAnimation(UI ui, Animation an)
        {
            if (!AnimSafe.CanWalkStates(an))
            {
                ui.Label("      Clips: listed once this object is active (reading an Animation on a never-activated object crashes Unity 5.0).", UI.Dim);
                return;
            }
            ui.Label("      Clips (Play = runs the clip once on this object; the game may override it next frame)", UI.Dim);
            foreach (AnimationState st in an)
            {
                if (st == null) continue;
                ui.BeginRow();
                ui.Space(24);
                if (ui.Button("Play", 44)) { an.Stop(); an.Play(st.name); DevLog.Write("[anim] " + an.name + " play " + st.name); }
                if (ui.Button("Stop", 44)) an.Stop(st.name);
                ui.Label(st.name + "   " + st.length.ToString("0.00", IC) + " s   " + st.wrapMode + (an.IsPlaying(st.name) ? "   PLAYING " + (st.normalizedTime * 100f).ToString("0") + "%" : ""), an.IsPlaying(st.name) ? UI.Accent : UI.Txt);
                ui.EndRow();
            }
        }

        // ---------------------------------------------------------------- parameterless methods of game scripts
        static readonly Dictionary<Type, MethodInfo[]> methodCache = new Dictionary<Type, MethodInfo[]>();
        static readonly HashSet<string> unityMessages = new HashSet<string> { "Awake", "Start", "Update", "LateUpdate", "FixedUpdate", "OnEnable", "OnDisable", "OnDestroy", "OnGUI", "Reset", "OnValidate", "OnDrawGizmos", "OnDrawGizmosSelected", "OnPreProcess", "OnApplicationQuit" };

        static MethodInfo[] MethodsOf(Type t)
        {
            MethodInfo[] m;
            if (methodCache.TryGetValue(t, out m)) return m;
            var list = new List<MethodInfo>();
            for (var k = t; k != null && k != typeof(MonoBehaviour) && k != typeof(object); k = k.BaseType)
                foreach (var mi in k.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (mi.GetParameters().Length != 0 || mi.IsSpecialName || mi.IsGenericMethod || mi.ContainsGenericParameters) continue;
                    if (unityMessages.Contains(mi.Name)) continue;
                    list.Add(mi);
                }
            m = list.ToArray();
            methodCache[t] = m;
            return m;
        }

        // Methods are collapsed per script type (click the row to open). Methods the game itself never calls
        // (found by scanning the game code: no caller, no delegate, no string reference in code or scenes) are
        // listed first and marked.
        static readonly HashSet<Type> methodsOpen = new HashSet<Type>();
        static readonly HashSet<string> neverCalled = new HashSet<string> {
            "FarmPigFight.SetSlaughterBoyState", "FarmPigFight.SetWalkState", "FarmPigFight.RecoverState",
            "GirlDrownLogic.SetCatchState", "MindHat.SetFailSelfAttachState", "LineupEndChase.SetHulvAttentionState",
            "ZombieMonitorCameraGameplay.SetLeaveState", "SecretJoystick.SetLoweredState", "Secret.DisableSecret",
            "ElevatorMines.SetShakeState", "StartSequence.SetInputWaitState", "GateCrush.IdleState",
        };
        static bool NeverCalled(MethodInfo mi) { return mi.DeclaringType != null && neverCalled.Contains(mi.DeclaringType.Name + "." + mi.Name); }

        void DrawMethods(UI ui, Component comp)
        {
            var ms = MethodsOf(comp.GetType());
            if (ms.Length == 0) return;
            var t = comp.GetType();
            bool open = methodsOpen.Contains(t);
            int nc = 0; foreach (var mi in ms) if (NeverCalled(mi)) nc++;
            if (ui.Item("      " + (open ? "v " : "> ") + "Methods (" + ms.Length + ")" + (nc > 0 ? "   " + nc + " never called by the game" : ""), nc > 0 ? new Color(1f, 0.75f, 0.3f, 1f) : UI.Accent))
            { if (open) methodsOpen.Remove(t); else methodsOpen.Add(t); open = !open; }
            if (!open)
            {
                // bookmarked methods stay reachable while the list is collapsed
                var mk = Bookmarks.MarkedMethods(comp.gameObject, t.Name);
                if (mk.Count == 0) return;
                foreach (var mi in ms)
                {
                    if (!mk.Contains(mi.Name)) continue;
                    ui.BeginRow(); ui.Space(24);
                    if (ui.Button("Call", 44)) { try { mi.Invoke(comp, null); DevLog.Write("[call] " + t.Name + "." + mi.Name + "() on " + comp.name); } catch (Exception e) { DevLog.Write("[call] " + mi.Name + " failed: " + (e.InnerException ?? e).Message); } }
                    ui.Label("* " + mi.Name + "()", new Color(1f, 0.85f, 0.4f, 1f));
                    ui.EndRow();
                }
                return;
            }
            ui.Label("      Call = runs the game's own code once; side effects are NOT undoable.   + / * = bookmark (kept in History > Bookmarks)", UI.Dim);
            var markedSet = Bookmarks.MarkedMethods(comp.gameObject, t.Name);
            // order: bookmarked, never called by the game, the rest
            for (int pass = 0; pass < 3; pass++)
                foreach (var mi in ms)
                {
                    bool never = NeverCalled(mi);
                    bool marked = markedSet.Contains(mi.Name);
                    int group = marked ? 0 : never ? 1 : 2;
                    if (group != pass) continue;
                    ui.BeginRow();
                    ui.Space(4);
                    if (ui.Button(marked ? "*" : "+", 20, marked ? UI.TabSel : (Color?)null)) Bookmarks.ToggleMethod(comp.gameObject, t.Name, mi.Name);
                    if (ui.Button("Call", 44))
                    {
                        try
                        {
                            var r = mi.Invoke(comp, null);
                            DevLog.Write("[call] " + t.Name + "." + mi.Name + "()" + (mi.ReturnType != typeof(void) ? " -> " + r : "") + " on " + comp.name);
                        }
                        catch (Exception e) { DevLog.Write("[call] " + mi.Name + " failed: " + (e.InnerException ?? e).Message); }
                    }
                    ui.Label(mi.Name + "()" + (mi.ReturnType != typeof(void) ? " : " + mi.ReturnType.Name : "") + (mi.IsPublic ? "" : "   private") + (never ? "   <- never called by the game" : ""),
                        never ? new Color(1f, 0.75f, 0.3f, 1f) : mi.IsPublic ? UI.Txt : UI.Dim);
                    ui.EndRow();
                }
        }

        // fields of a component or nested game-data object; returns how many were drawn
        int DrawObjectFields(UI ui, object owner, string path, int level)
        {
            int count = 0;
            bool raw = mode == Mode.Raw;
            foreach (var f in ValueDump.Fields(owner.GetType(), raw))
            {
                if (mode == Mode.Normal && !ValueDump.IsSerialized(f)) continue;
                if (++count > 200) { ui.Label(Indent(level) + "… more fields", UI.Dim); break; }
                DrawField(ui, owner, f, path + "." + f.Name, level);
            }
            return count;
        }

        // Phase 11: field edits are recorded against the component being drawn, by field path ("a.b" inside it)
        Component fieldRoot;
        void RecordField(string key)
        {
            if (fieldRoot == null) return;
            int dot = key.IndexOf('.');
            if (dot > 0) ChangeRecorder.BeforeField(fieldRoot, key.Substring(dot + 1), "inspector");
        }

        static string Indent(int level) { return new string(' ', 6 + level * 4); }

        void DrawField(UI ui, object owner, FieldInfo f, string path, int level)
        {
            object v;
            try { v = f.GetValue(owner); } catch (Exception e) { ui.Label(Indent(level) + f.Name + " = <" + e.GetType().Name + ">", UI.Dim); return; }
            var ft = f.FieldType;
            string key = path;
            if (ValueDump.IsNested(v))
            {
                bool op = expanded.Contains(key);
                if (ui.Item(Indent(level) + (op ? "v " : "> ") + f.Name + " : " + v.GetType().Name + (op ? "" : "   " + ValueDump.Compact(v, 1)), new Color(0.8f, 0.85f, 1f, 1f)))
                { if (op) expanded.Remove(key); else expanded.Add(key); }
                if (op)
                {
                    if (level >= 5) ui.Label(Indent(level + 1) + "(too deep)", UI.Dim);
                    else if (v.GetType().IsValueType) { ui.Label(Indent(level + 1) + "(struct - read only)", UI.Dim); DrawReadOnly(ui, v, level + 1); }
                    else DrawObjectFields(ui, v, key, level + 1);
                }
                return;
            }
            bool readOnly = owner.GetType().IsValueType;
            ui.BeginRow();
            ui.Label(Indent(level) + f.Name + (mode == Mode.Raw ? "  : " + ft.Name + (f.IsPublic ? "" : " (private)") : ""), UI.Dim, 220 + level * 16);
            if (readOnly) { ui.Label(ValueDump.Short(v)); ui.EndRow(); return; }
            if (ft == typeof(bool))
            {
                bool b = (bool)v;
                bool nb = ui.Toggle(b, b ? "true" : "false");
                if (nb != b) { RecordField(key); f.SetValue(owner, nb); }
            }
            else if (ft == typeof(float) || ft == typeof(int) || ft == typeof(string) || ft == typeof(double))
            {
                string cur;
                if (!edits.TryGetValue(key, out cur)) cur = v == null ? "" : Convert.ToString(v, IC);
                string nv = cur;
                bool enter = ui.TextField("f:" + key, ref nv, 160);
                if (nv != cur) edits[key] = nv;
                if (edits.ContainsKey(key) && (ui.Button("set") || enter))
                {
                    try
                    {
                        object parsed = ft == typeof(string) ? (object)nv : Convert.ChangeType(nv, ft, IC);
                        RecordField(key);
                        f.SetValue(owner, parsed);
                        edits.Remove(key);
                        DevLog.Write("set " + f.DeclaringType.Name + "." + f.Name + " = " + nv);
                    }
                    catch (Exception e) { DevLog.Write("set " + f.Name + " failed: " + e.Message); }
                }
            }
            else if (ft.IsEnum)
            {
                var names = Enum.GetNames(ft);
                if (ui.Button(v + "  (click = next)", -2))
                {
                    int idx = Array.IndexOf(names, v.ToString());
                    object nv2 = Enum.Parse(ft, names[(idx + 1) % names.Length]);
                    RecordField(key);
                    f.SetValue(owner, nv2);
                    DevLog.Write("set " + f.DeclaringType.Name + "." + f.Name + " = " + nv2);
                }
            }
            else if (ft == typeof(Vector3))
            {
                string cur;
                var vv = (Vector3)v;
                if (!edits.TryGetValue(key, out cur)) cur = vv.x.ToString("0.###", IC) + " " + vv.y.ToString("0.###", IC) + " " + vv.z.ToString("0.###", IC);
                string nv = cur;
                bool enter = ui.TextField("f:" + key, ref nv, 200);
                if (nv != cur) edits[key] = nv;
                if (edits.ContainsKey(key) && (ui.Button("set") || enter))
                {
                    var p = nv.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    float x, y, z;
                    if (p.Length == 3 && float.TryParse(p[0], NumberStyles.Float, IC, out x) && float.TryParse(p[1], NumberStyles.Float, IC, out y) && float.TryParse(p[2], NumberStyles.Float, IC, out z))
                    { RecordField(key); f.SetValue(owner, new Vector3(x, y, z)); edits.Remove(key); }
                }
            }
            else if (v is UObj)
            {
                var uo = (UObj)v;
                GameObject target = null;
                if (uo is GameObject) target = (GameObject)uo;
                else if (uo is Component) target = ((Component)uo).gameObject;
                string txt = uo == null ? "null" : uo.name + " (" + uo.GetType().Name + ")";
                if (target != null) { if (ui.Item(txt, UI.Accent)) { ui.EndRow(); Select(target); return; } }
                else ui.Label(txt);
            }
            else ui.Label(ValueDump.Short(v));
            ui.EndRow();
        }

        void DrawReadOnly(UI ui, object v, int level)
        {
            foreach (var f in ValueDump.Fields(v.GetType(), mode == Mode.Raw))
            {
                object x; try { x = f.GetValue(v); } catch { continue; }
                ui.Label(Indent(level) + f.Name + " = " + (ValueDump.IsNested(x) ? ValueDump.Compact(x, 2) : ValueDump.Short(x)), UI.Dim);
            }
        }

        static string Describe(object v)
        {
            if (v == null) return "null";
            var col = v as ICollection;
            if (col != null) return v.GetType().Name + " [" + col.Count + "]";
            string s;
            try { s = v.ToString(); } catch { s = "?"; }
            if (s.Length > 80) s = s.Substring(0, 80) + "...";
            return s;
        }
    }
}
