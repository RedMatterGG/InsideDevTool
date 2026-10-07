using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace InsideDev
{
    // FSM Graph panel: one PlayMaker state machine drawn as boxes and arrows, editable in place.
    //   boxes      states, at the positions the designers placed them in PlayMaker (fallback: layered layout)
    //   arrows     exits ("on <event> -> <state>"), labelled with the event; ANY STATE = global exits; START marks the
    //              start state. The current state is filled; the exit just taken flashes green.
    //   edit       drag from a state's right-edge square onto another state = new exit (then pick / type the event)
    //              click an arrow (or its label) = select it: fire / delete / drag its orange end onto another state = move it
    //              click a state = select it (actions, force); drag a state = move the box (view only, remembered)
    //   view       wheel = zoom, drag background = pan, Fit
    // All edits go through FsmEdit, so History records them and "reset wiring" restores the original.
    public static class FsmGraph
    {
        static PlayMakerFSM fsm;
        static int fsmId;
        static string selState, selEvFrom, selEv, selSig;   // selected state, exit (from state, event) or signal input
        static string sigFilter = "";
        static Vector2 pan; static float zoom = 1f; static bool needFit = true;
        // per FSM: user-moved boxes
        static readonly Dictionary<string, Vector2> moved = new Dictionary<string, Vector2>();
        // drag state
        enum Drag { None, Pan, Node, NewExit, MoveExit }
        static Drag drag; static Vector2 dragStart, dragOrigin; static string dragState;
        // pending new exit (from, to) waiting for an event name
        static string pendFrom, pendTo, pendText = "";
        // live: last observed state change
        static string lastActive, flashFrom, flashEv, flashTo; static float flashAt = -9f;
        static readonly Color cState = new Color(0.12f, 0.13f, 0.16f, 0.97f), cActive = new Color(0.16f, 0.32f, 0.52f, 0.98f), cEdge = new Color(1f, 0.85f, 0.45f, 0.85f),
            cGlobal = new Color(0.75f, 0.55f, 1f, 0.9f), cSel = new Color(1f, 0.55f, 0.25f, 1f), cFlash = new Color(0.3f, 1f, 0.45f, 1f), cNew = new Color(0.4f, 0.9f, 1f, 1f), cSig = new Color(1f, 0.62f, 0.2f, 0.95f), cBad = new Color(1f, 0.35f, 0.3f, 1f);
        const float NodeH = 34f;

        public static void Open(PlayMakerFSM f)
        {
            Set(f);
            try { DevCore.Instance.ShowPanel("fsmgraph"); } catch { }
        }

        // open with something selected (FSM Find)
        public static void Focus(PlayMakerFSM f, string state, string evFrom, string ev, string sig)
        {
            Open(f);
            selState = state; selEvFrom = evFrom; selEv = ev; selSig = sig; pendFrom = null;
            if (ev != null && evFrom == null) selEv = null;
        }

        static void Set(PlayMakerFSM f)
        {
            if (f == fsm) return;
            fsm = f; fsmId = f != null ? f.GetInstanceID() : 0;
            selState = selEvFrom = selEv = selSig = pendFrom = pendTo = null; sigFilter = ""; pendText = ""; lastActive = null; flashAt = -9f;
            needFit = true; drag = Drag.None;
        }

        // ------------------------------------------------------------------ model (rebuilt every frame: FSMs are small)
        sealed class N { public string key, name, sub; public Vector2 pos; public float w; public int actions; public bool start, global, sig, unconnected; }
        sealed class E { public string from, ev, to; public bool global, sig; public int idx, count; public bool flash; }
        static readonly Dictionary<string, N> nodes = new Dictionary<string, N>();
        static readonly List<E> edges = new List<E>();
        const string Any = "*";

        static void Build(Draw d)
        {
            nodes.Clear(); edges.Clear();
            var states = fsm.FsmStates; if (states == null) return;
            bool authored = false; Vector2 first = Vector2.zero; bool haveFirst = false;
            foreach (var st in states)
            {
                if (st == null) continue;
                var c = st.Position.center;
                if (!haveFirst) { first = c; haveFirst = true; } else if ((c - first).sqrMagnitude > 1f) authored = true;
            }
            string start = null; try { start = fsm.Fsm.StartState; } catch { }
            foreach (var st in states)
            {
                if (st == null || nodes.ContainsKey(st.Name)) continue;
                var n = new N { key = st.Name, name = st.Name, actions = st.Actions != null ? st.Actions.Length : 0, start = st.Name == start };
                n.w = Mathf.Max(110f, (d != null ? d.Measure(st.Name, 13) : st.Name.Length * 7f) + 26f);
                n.pos = authored ? st.Position.center * 1.35f : Vector2.zero;
                nodes[st.Name] = n;
            }
            if (!authored) Layered(start);
            // exits
            foreach (var st in states)
                if (st != null && st.Transitions != null)
                    foreach (var t in st.Transitions) if (t != null && !string.IsNullOrEmpty(t.ToState)) edges.Add(new E { from = st.Name, ev = t.EventName ?? "", to = t.ToState });
            var g = fsm.FsmGlobalTransitions;
            if (g != null && g.Length > 0)
            {
                float minX = 1e9f, minY = 1e9f;
                foreach (var n in nodes.Values) { minX = Mathf.Min(minX, n.pos.x - n.w * 0.5f); minY = Mathf.Min(minY, n.pos.y); }
                if (nodes.Count == 0) { minX = 0; minY = 0; }
                nodes[Any] = new N { key = Any, name = "ANY STATE", pos = new Vector2(minX - 170f, minY - 60f), w = 110f, global = true };
                foreach (var t in g) if (t != null && !string.IsNullOrEmpty(t.ToState)) edges.Add(new E { from = Any, ev = t.EventName ?? "", to = t.ToState, global = true });
            }
            // signal inputs ('>' events): a box left of the state that listens for it, listing who is connected
            var perTarget = new Dictionary<string, int>();
            var incoming = SignalEdit.Incoming(fsm);
            foreach (var inName in SignalEdit.Inputs(fsm))
            {
                string target = null;
                foreach (var e in edges) if (e.ev == inName) { target = e.global ? Any : e.from; break; }
                N tn = null; if (target == null || !nodes.TryGetValue(target, out tn)) { if (nodes.Count == 0) continue; foreach (var x in nodes.Values) { tn = x; break; } target = tn.key; }
                int k; perTarget.TryGetValue(target, out k); perTarget[target] = k + 1;
                var senders = new List<string>();
                foreach (var c in incoming) if (c.signalInName == inName) senders.Add((c.signalOutGameObject != null ? c.signalOutGameObject.name : "?") + "." + c.signalOutName + (c.isConnected ? "" : " (waiting)"));
                string sub = senders.Count == 0 ? "nothing connected" : senders[0] + (senders.Count > 1 ? "  +" + (senders.Count - 1) + " more" : "");
                string key = "sig:" + inName;
                float w = Mathf.Max(130f, Mathf.Max(d != null ? d.Measure(inName, 13) : inName.Length * 7f, d != null ? d.Measure(sub, 11) : sub.Length * 6f) + 22f);
                nodes[key] = new N { key = key, name = "signal " + inName, sub = sub, sig = true, unconnected = senders.Count == 0, w = w, pos = tn.pos + new Vector2(-tn.w * 0.5f - w * 0.5f - 90f, 52f + k * 48f) };
                edges.Add(new E { from = key, ev = inName, to = target, sig = true });
            }
            // user-moved boxes
            foreach (var n in nodes.Values) { Vector2 p; if (moved.TryGetValue(fsmId + "/" + n.key, out p)) n.pos = p; }
            // parallel exits between the same two states get spread apart
            var groups = new Dictionary<string, List<E>>();
            foreach (var e in edges)
            {
                string k = string.CompareOrdinal(e.from, e.to) <= 0 ? e.from + "\n" + e.to : e.to + "\n" + e.from;
                List<E> l; if (!groups.TryGetValue(k, out l)) groups[k] = l = new List<E>(); l.Add(e);
            }
            foreach (var l in groups.Values) for (int i = 0; i < l.Count; i++) { l[i].idx = i; l[i].count = l.Count; }
            bool fl = Time.realtimeSinceStartup - flashAt < 2.5f;
            if (fl) foreach (var e in edges) e.flash = e.ev == flashEv && e.to == flashTo && (e.from == flashFrom || e.global);
        }

        // no authored positions: columns by distance from the start state
        static void Layered(string start)
        {
            var depth = new Dictionary<string, int>();
            var q = new Queue<string>();
            if (start != null && nodes.ContainsKey(start)) { depth[start] = 0; q.Enqueue(start); }
            while (true)
            {
                while (q.Count > 0)
                {
                    var s = q.Dequeue(); FsmState st = Find(s); if (st == null || st.Transitions == null) continue;
                    foreach (var t in st.Transitions) if (t != null && t.ToState != null && nodes.ContainsKey(t.ToState) && !depth.ContainsKey(t.ToState)) { depth[t.ToState] = depth[s] + 1; q.Enqueue(t.ToState); }
                }
                string un = null; foreach (var k in nodes.Keys) if (!depth.ContainsKey(k)) { un = k; break; }
                if (un == null) break;
                int max = 0; foreach (var v in depth.Values) max = Mathf.Max(max, v);
                depth[un] = depth.Count == 0 ? 0 : max + 1; q.Enqueue(un);
            }
            var rows = new Dictionary<int, int>();
            foreach (var n in nodes.Values)
            {
                int c = depth[n.name]; int r; rows.TryGetValue(c, out r); rows[c] = r + 1;
                n.pos = new Vector2(c * 230f, r * 80f);
            }
        }

        static FsmState Find(string name) { if (fsm == null || fsm.FsmStates == null) return null; foreach (var s in fsm.FsmStates) if (s != null && s.Name == name) return s; return null; }

        // ------------------------------------------------------------------ geometry
        static Vector2 Scr(Rect area, Vector2 p) { return area.center + pan + p * zoom; }
        static Vector2 World(Rect area, Vector2 s) { return (s - area.center - pan) / zoom; }
        static Rect NodeRect(Rect area, N n) { var c = Scr(area, n.pos); float w = n.w * zoom, h = NodeH * zoom; return new Rect(c.x - w * 0.5f, c.y - h * 0.5f, w, h); }
        static Vector2 Handle(Rect r) { return new Vector2(r.xMax, r.center.y); }

        // where the line from the box centre towards p leaves the box
        static Vector2 Border(Rect r, Vector2 p)
        {
            var c = r.center; var dir = p - c; if (dir.sqrMagnitude < 0.01f) return c;
            float tx = dir.x != 0 ? (r.width * 0.5f) / Mathf.Abs(dir.x) : 1e9f, ty = dir.y != 0 ? (r.height * 0.5f) / Mathf.Abs(dir.y) : 1e9f;
            return c + dir * Mathf.Min(tx, ty);
        }

        // quadratic curve for an exit: a -> control -> b; parallel exits bend apart
        static void Curve(Rect ra, Rect rb, E e, out Vector2 a, out Vector2 ctl, out Vector2 b)
        {
            Vector2 ca = ra.center, cb = rb.center;
            var dir = (cb - ca); float len = dir.magnitude; if (len < 1) len = 1; dir /= len;
            var perp = new Vector2(-dir.y, dir.x);
            float off = (e.idx - (e.count - 1) * 0.5f) * 26f * zoom;
            if (string.CompareOrdinal(e.from, e.to) > 0) off = -off;   // the group is keyed by the sorted pair: keep the side stable
            if (e.count == 1) off = 0;
            var mid = (ca + cb) * 0.5f + perp * off * 2f;
            a = Border(ra, mid); b = Border(rb, mid); ctl = mid;
        }
        static Vector2 Bez(Vector2 a, Vector2 c, Vector2 b, float t) { float u = 1 - t; return u * u * a + 2 * u * t * c + t * t * b; }

        static float DistToCurve(Vector2 p, Vector2 a, Vector2 c, Vector2 b)
        {
            float best = 1e9f; var prev = a;
            for (int i = 1; i <= 12; i++) { var q = Bez(a, c, b, i / 12f); best = Mathf.Min(best, DistSeg(p, prev, q)); prev = q; }
            return best;
        }
        static float DistSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a; float l = ab.sqrMagnitude; if (l < 1e-4f) return (p - a).magnitude;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / l); return (p - (a + ab * t)).magnitude;
        }

        // ------------------------------------------------------------------ draw
        public static void Draw(UI ui)
        {
            PickFsm(ui);
            if (fsm == null) { ui.Label("Select an object with a PlayMaker state machine (or press 'graph' on an FSM in the Inspector).", UI.Dim); return; }
            Live();
            var d = ui.D;
            Build(d);
            Details(ui);
            if (fsm == null) return;
            ui.Label("drag a state's right-edge square onto a state = new exit   click arrow = select (drag its orange end = move, Delete = remove)   click state = details   drag state = move box   wheel = zoom   drag background = pan", UI.Dim);
            var area = ui.Canvas(ui.Remaining);
            d.Fill(area, new Color(0, 0, 0, 0.25f));
            if (needFit) Fit(area);
            d.PushClip(area);
            try { Canvas(ui, d, area); } finally { d.PopClip(); }
        }

        static void PickFsm(UI ui)
        {
            var go = Selection.Current;
            PlayMakerFSM[] here = go != null ? go.GetComponents<PlayMakerFSM>() : new PlayMakerFSM[0];
            if (fsm == null && here.Length > 0) Set(here[0]);
            else if (fsm != null && here.Length > 0 && Array.IndexOf(here, fsm) < 0) Set(here[0]);
            if ((object)fsm != null && fsm == null) Set(null);   // destroyed
            ui.BeginRow();
            ui.Label(fsm != null ? fsm.gameObject.name + " /" : "FSM Graph", UI.Txt, fsm != null ? Mathf.Min(220, ui.D.Measure(fsm.gameObject.name + " /", 13) + 10) : 90);
            foreach (var f in here) if (f != null && ui.Button(f.FsmName, -2, f == fsm ? UI.TabSel : (Color?)null)) Set(f);
            if (fsm != null && Array.IndexOf(here, fsm) < 0 && ui.Button(fsm.FsmName, -2, UI.TabSel)) { }
            if (fsm != null)
            {
                bool running = fsm.gameObject.activeInHierarchy && fsm.enabled;
                ui.Label("  state: " + (running ? fsm.ActiveStateName : "(not running)"), running ? UI.Accent : UI.Dim, 200);
                if (ui.Button("Fit")) needFit = true;
                if (ui.Button("Select object")) Selection.Set(fsm.gameObject, "fsm graph");
                bool edited = FsmEdit.IsEdited(fsm) || SignalEdit.IsEdited(fsm) || ActionEdit.IsEdited(fsm);
                if (ui.Button("reset edits", -2, edited ? new Color(0.45f, 0.25f, 0.1f, 1f) : UI.BgAlt))
                {
                    if (edited)
                    {
                        var parts = new List<string>();
                        if (FsmEdit.IsEdited(fsm)) { FsmEdit.Reset(fsm); parts.Add("exits back to the original"); }
                        if (SignalEdit.IsEdited(fsm)) parts.Add(SignalEdit.Reset(fsm));
                        if (ActionEdit.IsEdited(fsm)) parts.Add(ActionEdit.Reset(fsm));
                        note = "reset: " + string.Join(", ", parts.ToArray());
                        selEv = null; selSig = null; pendFrom = null;
                    }
                    else note = "nothing to reset: no exits, signal wiring or action settings changed on this state machine this session";
                    noteAt = Time.realtimeSinceStartup;
                }
            }
            ui.EndRow();
            if (fsm != null && !fsm.gameObject.activeInHierarchy)
            {
                ui.BeginRow();
                ui.Label(OffReason(fsm.gameObject) + " - the state machine is not running", cBad, Mathf.Max(200, ui.Width - 110));
                if (ui.Button("turn on", -2, new Color(0.15f, 0.4f, 0.2f, 1f))) { note = TurnOn(fsm.gameObject); noteAt = Time.realtimeSinceStartup; }
                ui.EndRow();
            }
            else if (fsm != null && !fsm.enabled)
            {
                ui.BeginRow();
                ui.Label("the state machine component is disabled", cBad, Mathf.Max(200, ui.Width - 110));
                if (ui.Button("enable", -2, new Color(0.15f, 0.4f, 0.2f, 1f))) { ChangeRecorder.SetEnabledRecorded(fsm, true, "fsm graph"); }
                ui.EndRow();
            }
            if (note != null && Time.realtimeSinceStartup - noteAt < 6f) ui.Label(note, new Color(0.5f, 1f, 0.6f, 1f));
        }

        static string note; static float noteAt;

        // why an object is not active: itself off, or the first switched-off parent
        public static string OffReason(GameObject go)
        {
            if (!go.activeSelf) return go.name + " is switched off";
            for (var t = go.transform.parent; t != null; t = t.parent) if (!t.gameObject.activeSelf) return "parent '" + t.name + "' is switched off";
            return go.name + " is off";
        }

        // switch on the object and every switched-off parent (one History step, undoable)
        public static string TurnOn(GameObject go)
        {
            if (go == null) return "";
            var names = new List<string>();
            ChangeRecorder.Begin("turn on " + go.name, "fsm graph");
            try { for (var t = go.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) { ChangeRecorder.SetActive(t.gameObject, true, "fsm graph"); names.Add(t.name); } }
            finally { ChangeRecorder.End(); }
            return names.Count == 0 ? go.name + " was already on" : "turned on " + string.Join(", ", names.ToArray()) + (go.activeInHierarchy ? "" : "  (still off: the game may switch it back)") + "  - undo in History";
        }

        static void Live()
        {
            string a = fsm.ActiveStateName;
            if (lastActive != null && a != lastActive)
            {
                flashFrom = lastActive; flashTo = a; flashEv = null; flashAt = Time.realtimeSinceStartup;
                try { var lt = fsm.Fsm.LastTransition; if (lt != null && lt.ToState == a) flashEv = lt.EventName; } catch { }
            }
            lastActive = a;
        }

        static void Details(UI ui)
        {
            var hi = new Color(1f, 0.6f, 0.3f, 1f);
            if (pendFrom != null)
            {
                ui.BeginRow();
                ui.Label("new exit  [" + (pendFrom == Any ? "ANY STATE" : pendFrom) + "] -> [" + pendTo + "]   on event:", cNew, 330);
                bool enter = ui.TextField("fsmg-ev", ref pendText, 180);
                bool ok = ui.Button("add") || enter;
                if (ui.Button("cancel")) { pendFrom = null; ui.EndRow(); return; }
                ui.EndRow();
                if (ok && pendText.Trim().Length > 0) { FsmEdit.Wire(fsm, pendFrom, pendText.Trim(), pendTo, "fsm graph"); selEvFrom = pendFrom; selEv = pendText.Trim(); selState = null; pendFrom = null; return; }
                Flow(ui, FsmEdit.EventNames(fsm), n => pendText = n);
                return;
            }
            if (selSig != null) { SignalDetails(ui, hi); return; }
            if (selEv != null)
            {
                E e = null; foreach (var x in edges) if (x.from == selEvFrom && x.ev == selEv) e = x;
                if (e == null) { selEv = null; return; }
                ui.BeginRow();
                ui.Label("exit  [" + (e.global ? "ANY STATE" : e.from) + "]  on '" + e.ev + "'  ->  [" + e.to + "]", hi, Mathf.Max(200, ui.Width - 330));
                if (e.ev.Length > 0 && ui.Button("fire event")) { ChangeRecorder.Action(fsm.gameObject, "fsmEvent", fsm.FsmName, e.ev, "fsm graph"); fsm.SendEvent(e.ev); }
                if (ui.Button("go to target")) { ChangeRecorder.Action(fsm.gameObject, "fsmState", fsm.FsmName, e.to, "fsm graph"); fsm.SetState(e.to); }
                if (ui.Button("delete exit", -2, new Color(0.45f, 0.15f, 0.12f, 1f))) { FsmEdit.Unwire(fsm, e.from, e.ev, "fsm graph"); selEv = null; }
                ui.EndRow();
                ui.Label("   fire = send the event (acts only if the current state listens for it)   drag the orange square at its arrow end onto another state to move this exit", UI.Dim);
                return;
            }
            if (selState != null)
            {
                var st = Find(selState); if (st == null) { selState = null; return; }
                bool active = st.Name == fsm.ActiveStateName;
                ui.BeginRow();
                ui.Label("state  [" + st.Name + "]" + (active ? "  (current)" : "") + "   " + (st.Transitions != null ? st.Transitions.Length : 0) + " exits", hi, Mathf.Max(200, ui.Width - 120));
                if (ui.Button("force here")) { ChangeRecorder.Action(fsm.gameObject, "fsmState", fsm.FsmName, st.Name, "fsm graph"); fsm.SetState(st.Name); }
                ui.EndRow();
                ActionList(ui, st);
            }
        }

        // ---- action settings (editable)
        static string openAction;                                                   // "<fsm id>|<state>|<index>"
        static readonly Dictionary<string, string> editBuf = new Dictionary<string, string>();
        static string enumOpen;
        static readonly Color cEdited = new Color(1f, 0.6f, 0.3f, 1f);

        static void ActionList(UI ui, FsmState st)
        {
            if (st.Actions == null || st.Actions.Length == 0) { ui.Label("   no actions", UI.Dim); return; }
            ui.Label("   click an action to edit its settings  (most are read when the state starts: 'force here' re-runs it)", UI.Dim);
            for (int i = 0; i < st.Actions.Length; i++)
            {
                var a = st.Actions[i]; if (a == null) continue;
                string key = fsmId + "|" + st.Name + "|" + i;
                bool open = openAction == key;
                bool anyEdit = ActionEdit.IsEdited(fsm, a, ActionEdit.EnabledField);
                if (!anyEdit) foreach (var f in ActionEdit.Fields(a)) if (ActionEdit.IsEdited(fsm, a, f.Name)) { anyEdit = true; break; }
                string s = (open ? "v " : "> ") + "#" + i + " " + a.GetType().Name + (string.IsNullOrEmpty(a.Name) ? "" : " '" + a.Name + "'") + (open ? "" : Links.ActionSummary(a));
                if (s.Length > 200) s = s.Substring(0, 199) + "…";
                ui.BeginRow();
                ui.Label("", null, 8);
                if (ui.Item(s, !a.Enabled ? UI.Dim : anyEdit ? cEdited : UI.Txt, Mathf.Max(120, ui.Width - 90))) { openAction = open ? null : key; enumOpen = null; }
                bool en = ui.Toggle(a.Enabled, a.Enabled ? "on" : "off");
                if (en != a.Enabled) Note(ActionEdit.Set(fsm, st.Name, i, ActionEdit.EnabledField, en ? "true" : "false", "fsm graph"));
                ui.EndRow();
                if (open) Settings(ui, st, i, a, key);
            }
        }

        static void Note(string s) { note = s; noteAt = Time.realtimeSinceStartup; }

        static void Settings(UI ui, FsmState st, int i, FsmStateAction a, string akey)
        {
            var fields = ActionEdit.Fields(a);
            if (fields.Count == 0) ui.Label("        (no settings)", UI.Dim);
            foreach (var f in fields)
            {
                object v; try { v = f.GetValue(a); } catch { continue; }
                bool edited = ActionEdit.IsEdited(fsm, a, f.Name);
                bool editable = ActionEdit.IsEditable(f.FieldType);
                string k = akey + "|" + f.Name;
                ui.BeginRow();
                ui.Label("        " + f.Name, edited ? cEdited : UI.Dim, 190);
                if (!editable)
                {
                    string d = Remote.ActionParam(v) ?? (v == null ? "(none)" : v.GetType().Name);
                    ui.Label(d + "   (" + f.FieldType.Name + ", not editable here)", UI.Dim);
                    ui.EndRow(); continue;
                }
                var t = f.FieldType;
                if (t == typeof(bool) || t == typeof(FsmBool))
                {
                    bool cur = t == typeof(bool) ? (bool)v : v != null && ((FsmBool)v).Value;
                    var nv = v as NamedVariable; if (nv != null && nv.UseVariable && !string.IsNullOrEmpty(nv.Name)) ui.Label("{" + nv.Name + "}", UI.Dim, 90);
                    bool nb = ui.Toggle(cur, cur ? "true" : "false");
                    if (nb != cur) Note(ActionEdit.Set(fsm, st.Name, i, f.Name, nb ? "true" : "false", "fsm graph"));
                }
                else if (t.IsEnum)
                {
                    ui.Label(v != null ? v.ToString() : "?", edited ? cEdited : UI.Txt, 160);
                    if (ui.Button(enumOpen == k ? "close" : "change")) enumOpen = enumOpen == k ? null : k;
                }
                else
                {
                    ui.Label(ActionEdit.Show(v), edited ? cEdited : UI.Txt, 170);
                    string buf; if (!editBuf.TryGetValue(k, out buf)) buf = ActionEdit.EditText(v);
                    string nb = buf;
                    bool enter = ui.TextField("ae:" + k, ref nb, 150);
                    if (nb != buf) editBuf[k] = nb;
                    if (ui.Button("set") || enter) { Note(ActionEdit.Set(fsm, st.Name, i, f.Name, nb, "fsm graph")); editBuf.Remove(k); }
                }
                if (edited && ui.Button("undo")) { Note(ActionEdit.Revert(fsm, a, f.Name)); editBuf.Remove(k); }
                ui.EndRow();
                if (t.IsEnum && enumOpen == k)
                {
                    var names = new List<string>(Enum.GetNames(t));
                    Flow(ui, names, n => { Note(ActionEdit.Set(fsm, st.Name, i, f.Name, n, "fsm graph")); enumOpen = null; });
                }
                if (t == typeof(FsmEvent) && editBuf.ContainsKey(k)) { ui.Label("        events this state machine knows:", UI.Dim); Flow(ui, FsmEdit.EventNames(fsm), n => { editBuf[k] = n; }); }
                var nvar = v as NamedVariable;
                if (nvar != null && nvar.UseVariable && !string.IsNullOrEmpty(nvar.Name) && t != typeof(FsmBool))
                    ui.Label("          reads FSM variable '" + nvar.Name + "' - 'set' gives this action its own constant instead (the variable is unchanged)", UI.Dim);
            }
        }

        static void SignalDetails(UI ui, Color hi)
        {
            string inName = selSig;
            ui.BeginRow();
            ui.Label("signal input  '" + inName + "'  - other objects' signals arrive here as this event", hi, Mathf.Max(200, ui.Width - 120));
            if (ui.Button("fire event")) { ChangeRecorder.Action(fsm.gameObject, "fsmEvent", fsm.FsmName, inName, "fsm graph"); fsm.SendEvent(inName); }
            ui.EndRow();
            int n = 0;
            foreach (var c in SignalEdit.Incoming(fsm))
            {
                if (c.signalInName != inName) continue; n++;
                ui.BeginRow();
                string from = c.signalOutGameObject != null ? c.signalOutGameObject.name : "?";
                ui.Label("   <- " + from + "." + c.signalOutName + (c.isConnected ? "" : "   (waiting: links when " + from + " wakes up)") + (c.signalOutGameObject != null && !c.signalOutGameObject.activeInHierarchy ? "   [off: " + OffReason(c.signalOutGameObject) + "]" : ""), c.isConnected ? cSig : UI.Dim, Mathf.Max(200, ui.Width - 250));
                if (c.signalOutGameObject != null && !c.signalOutGameObject.activeInHierarchy && ui.Button("turn on", -2, new Color(0.15f, 0.4f, 0.2f, 1f))) { note = TurnOn(c.signalOutGameObject); noteAt = Time.realtimeSinceStartup; }
                if (c.signalOutGameObject != null && ui.Button("select")) Selection.Set(c.signalOutGameObject, "fsm graph");
                if (ui.Button("disconnect", -2, new Color(0.45f, 0.15f, 0.12f, 1f))) { SignalEdit.Disconnect(fsm, c, "fsm graph"); ui.EndRow(); return; }
                ui.EndRow();
            }
            if (n == 0) ui.Label("   nothing is connected to this input", cBad);
            ui.BeginRow();
            ui.Label("   connect a signal:", UI.Dim, 130);
            bool enter = ui.TextField("fsmg-sig", ref sigFilter, 240);
            ui.Label("  (search object or signal name; nearest first.  'Object.signal' + Enter = connect by name, also to objects that are switched off)", UI.Dim);
            ui.EndRow();
            var outs = SignalEdit.Outputs(fsm.transform.position, sigFilter, 14);
            float x = 0, max = ui.Width - 20; bool row = false;
            foreach (var so in outs)
            {
                float dist = (so.gameObject.transform.position - fsm.transform.position).magnitude;
                string label = so.gameObject.name + "." + so.debugName + "  " + dist.ToString("0") + " m" + (so.gameObject.activeInHierarchy ? "" : " [off]");
                float bw = Mathf.Min(max, 16f + ui.D.Measure(label, 13));
                if (!row || x + bw > max) { if (row) ui.EndRow(); ui.BeginRow(); x = 0; row = true; }
                if (ui.Button(label, bw)) { SignalEdit.Connect(fsm, so.gameObject, so.debugName, inName, "fsm graph"); sigFilter = ""; }
                x += bw + 4;
            }
            if (row) ui.EndRow();
            if (outs.Count == 0) ui.Label("   no existing signal output matches - type 'Object.signal' and press Enter to connect by name", UI.Dim);
            if (enter)
            {
                int dot = sigFilter.LastIndexOf('.');
                if (dot > 0)
                {
                    string on = sigFilter.Substring(0, dot).Trim(), sn = sigFilter.Substring(dot + 1).Trim();
                    GameObject best = null; float bd = float.MaxValue;
                    foreach (var r in ObjectDatabase.all)
                        if (r.go != null && r.name == on) { float dd = (r.go.transform.position - fsm.transform.position).sqrMagnitude; if (dd < bd) { bd = dd; best = r.go; } }
                    DevLog.Write(best == null ? "[signal] no loaded object named '" + on + "'" : SignalEdit.Connect(fsm, best, sn, inName, "fsm graph"));
                    if (best != null) sigFilter = "";
                }
            }
        }

        static void Flow(UI ui, List<string> items, Action<string> pick)
        {
            float x = 0, max = ui.Width - 20; bool row = false; int n = 0;
            foreach (var it in items)
            {
                if (n++ > 60) break;
                float bw = Mathf.Min(max, 16f + ui.D.Measure(it, 13));
                if (!row || x + bw > max) { if (row) ui.EndRow(); ui.BeginRow(); x = 0; row = true; }
                if (ui.Button(it, bw)) pick(it);
                x += bw + 4;
            }
            if (row) ui.EndRow();
        }

        static void Fit(Rect area)
        {
            needFit = false;
            if (nodes.Count == 0) { pan = Vector2.zero; zoom = 1; return; }
            float minX = 1e9f, minY = 1e9f, maxX = -1e9f, maxY = -1e9f;
            foreach (var n in nodes.Values) { minX = Mathf.Min(minX, n.pos.x - n.w * 0.5f); maxX = Mathf.Max(maxX, n.pos.x + n.w * 0.5f); minY = Mathf.Min(minY, n.pos.y - NodeH); maxY = Mathf.Max(maxY, n.pos.y + NodeH); }
            minX -= 70f;   // START marker on the left
            float w = maxX - minX + 80, h = maxY - minY + 80;
            zoom = Mathf.Clamp(Mathf.Min(area.width / w, area.height / h), 0.25f, 1.4f);
            pan = -new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f) * zoom;
        }

        static void Canvas(UI ui, Draw d, Rect area)
        {
            bool hot = ui.Hot(area);
            if (hot && ui.Wheel != 0)
            {
                float old = zoom;
                zoom = Mathf.Clamp(zoom * (ui.Wheel > 0 ? 1.15f : 1f / 1.15f), 0.25f, 2.5f);
                var m = ui.mouse - area.center - pan; pan -= m * (zoom / old - 1f);
            }
            int fs = Mathf.RoundToInt(12 * Mathf.Clamp(zoom, 0.75f, 1.3f));

            // hit tests
            N hoverNode = null; bool hoverHandle = false;
            foreach (var n in nodes.Values)
            {
                var r = NodeRect(area, n);
                if (!hot) continue;
                if (!n.sig && (ui.mouse - Handle(r)).sqrMagnitude < 81f) { hoverNode = n; hoverHandle = true; }
                else if (r.Contains(ui.mouse) && !hoverHandle) hoverNode = n;
            }
            E hoverEdge = null; float best = 7f;
            var labelRects = new List<KeyValuePair<Rect, E>>();
            // edges
            foreach (var e in edges)
            {
                N na, nb; if (!nodes.TryGetValue(e.from, out na) || !nodes.TryGetValue(e.to, out nb)) continue;
                Rect ra = NodeRect(area, na), rb = NodeRect(area, nb);
                bool sel = e.sig ? e.ev == selSig : e.from == selEvFrom && e.ev == selEv;
                Color c = e.flash ? Color.Lerp(cFlash, e.global ? cGlobal : cEdge, (Time.realtimeSinceStartup - flashAt) / 2.5f) : sel ? cSel : e.sig ? cSig : e.global ? cGlobal : cEdge;
                Vector2 a, ctl, b, lab;
                if (e.from == e.to)
                {   // loop above the box
                    var p0 = new Vector2(ra.xMax - 14 * zoom, ra.yMin); var p1 = new Vector2(ra.xMax - 14 * zoom, ra.yMin - 26 * zoom);
                    var p2 = new Vector2(ra.xMax + 14 * zoom, ra.yMin - 26 * zoom); var p3 = new Vector2(ra.xMax + 14 * zoom, ra.center.y - 6 * zoom);
                    d.Line(p0, p1, c); d.Line(p1, p2, c); d.Line(p2, new Vector2(p2.x, p3.y), c); d.Line(new Vector2(p2.x, p3.y), new Vector2(ra.xMax + 2, p3.y), c);
                    Arrow(d, new Vector2(ra.xMax + 2, p3.y), new Vector2(-1, 0), c);
                    a = p0; ctl = (p1 + p2) * 0.5f; b = p3; lab = (p1 + p2) * 0.5f + new Vector2(0, -8 * zoom);
                    if (hot && hoverNode == null) { float dd = Mathf.Min(DistSeg(ui.mouse, p0, p1), Mathf.Min(DistSeg(ui.mouse, p1, p2), DistSeg(ui.mouse, p2, p3))); if (dd < best) { best = dd; hoverEdge = e; } }
                }
                else
                {
                    Curve(ra, rb, e, out a, out ctl, out b);
                    var prev = a;
                    for (int i = 1; i <= 14; i++) { var q = Bez(a, ctl, b, i / 14f); d.Line(prev, q, c); if (sel || e.flash) d.Line(prev + Vector2.up, q + Vector2.up, c); prev = q; }
                    Arrow(d, b, (b - Bez(a, ctl, b, 0.9f)).normalized, c);
                    lab = Bez(a, ctl, b, 0.5f);
                    if (hot && hoverNode == null) { float dd = DistToCurve(ui.mouse, a, ctl, b); if (dd < best) { best = dd; hoverEdge = e; } }
                }
                if (zoom >= 0.45f)
                {
                    string txt = e.ev.Length > 0 ? e.ev : "(no event)";
                    float w = d.Measure(txt, fs - 1) + 8, h = fs + 4;
                    var lr = new Rect(lab.x - w * 0.5f, lab.y - h * 0.5f, w, h);
                    d.Fill(lr, new Color(0.04f, 0.05f, 0.07f, sel ? 0.95f : 0.82f));
                    if (sel) d.Frame(lr, cSel);
                    d.Text(lr.x + 4, lr.y + 1, txt, c, fs - 1);
                    labelRects.Add(new KeyValuePair<Rect, E>(lr, e));
                }
                if (sel && e.from != e.to && !e.sig)
                {   // move handle on the arrow end
                    var hr = new Rect(b.x - 6, b.y - 6, 12, 12);
                    d.Fill(hr, cSel);
                    if (hot && ui.click && hr.Contains(ui.mouse) && drag == Drag.None) { drag = Drag.MoveExit; dragStart = ui.mouse; }
                }
            }
            if (hot && hoverNode == null) foreach (var kv in labelRects) if (kv.Key.Contains(ui.mouse)) { hoverEdge = kv.Value; best = 0; }

            // START marker
            foreach (var n in nodes.Values)
                if (n.start)
                {
                    var r = NodeRect(area, n); var s = new Vector2(r.xMin - 46 * zoom, r.center.y);
                    d.Line(s, new Vector2(r.xMin - 2, r.center.y), cFlash); Arrow(d, new Vector2(r.xMin - 2, r.center.y), Vector2.right, cFlash);
                    if (zoom >= 0.45f) d.Text(s.x - 4, s.y - fs - 3, "START", cFlash, fs - 2);
                }

            // boxes
            string active = fsm.ActiveStateName;
            foreach (var n in nodes.Values)
            {
                var r = NodeRect(area, n);
                if (!r.Overlaps(area)) continue;
                bool hov = n == hoverNode;
                if (n.sig)
                {
                    bool ss = selSig != null && n.key == "sig:" + selSig;
                    d.Fill(r, hov ? new Color(0.22f, 0.16f, 0.08f, 0.98f) : new Color(0.15f, 0.11f, 0.06f, 0.97f));
                    d.Frame(r, ss ? cSel : n.unconnected ? cBad : cSig);
                    if (zoom >= 0.4f)
                    {
                        d.PushClip(r);
                        d.Text(r.x + 7, r.y + 3 * zoom, n.name, cSig, fs);
                        if (zoom >= 0.6f) d.Text(r.x + 7, r.y + 3 * zoom + fs + 1, n.sub, n.unconnected ? cBad : UI.Dim, fs - 2);
                        d.PopClip();
                    }
                    continue;
                }
                bool isAct = n.name == active, sel = n.name == selState && selEv == null && selSig == null;
                if (isAct) { var g = new Rect(r.x - 3, r.y - 3, r.width + 6, r.height + 6); d.Frame(g, new Color(0.4f, 0.7f, 1f, 0.9f)); }
                if (!n.global && flashTo == n.name && Time.realtimeSinceStartup - flashAt < 1.5f) { var g = new Rect(r.x - 5, r.y - 5, r.width + 10, r.height + 10); d.Frame(g, cFlash); }
                d.Fill(r, n.global ? new Color(0.18f, 0.12f, 0.25f, 0.97f) : isAct ? cActive : hov ? new Color(0.2f, 0.22f, 0.28f, 0.98f) : cState);
                d.Frame(r, sel ? cSel : n.global ? cGlobal : isAct ? new Color(0.5f, 0.8f, 1f, 1f) : new Color(0.45f, 0.48f, 0.56f, 1f));
                if (zoom >= 0.4f)
                {
                    d.PushClip(r);
                    d.Text(r.x + 7, r.y + 3 * zoom, n.name, isAct ? Color.white : n.global ? cGlobal : UI.Txt, fs);
                    if (!n.global && zoom >= 0.6f) d.Text(r.x + 7, r.y + 3 * zoom + fs + 1, n.actions + " action" + (n.actions == 1 ? "" : "s") + (isAct ? "  · current" : ""), UI.Dim, fs - 2);
                    d.PopClip();
                }
                // new-exit handle
                var h = Handle(r);
                d.Fill(new Rect(h.x - 4, h.y - 4, 8, 8), (hov && hoverHandle) ? cNew : new Color(0.55f, 0.6f, 0.7f, 0.9f));
            }

            // input
            if (drag == Drag.None && hot && ui.click)
            {
                if (hoverNode != null && hoverHandle) { drag = Drag.NewExit; dragState = hoverNode.key; dragStart = ui.mouse; }
                else if (hoverNode != null && hoverNode.sig) { drag = Drag.Node; dragState = hoverNode.key; dragStart = ui.mouse; dragOrigin = hoverNode.pos; selSig = hoverNode.key.Substring(4); selState = null; selEv = null; pendFrom = null; }
                else if (hoverNode != null && !hoverNode.global) { drag = Drag.Node; dragState = hoverNode.key; dragStart = ui.mouse; dragOrigin = hoverNode.pos; selState = hoverNode.name; selEv = null; selSig = null; pendFrom = null; }
                else if (hoverEdge != null && hoverEdge.sig) { selSig = hoverEdge.ev; selState = null; selEv = null; pendFrom = null; }
                else if (hoverEdge != null) { selEvFrom = hoverEdge.from; selEv = hoverEdge.ev; selState = null; selSig = null; pendFrom = null; }
                else { drag = Drag.Pan; dragStart = ui.mouse; dragOrigin = pan; }
            }
            switch (drag)
            {
                case Drag.Pan:
                    if (ui.held) pan = dragOrigin + (ui.mouse - dragStart);
                    else { if ((ui.mouse - dragStart).sqrMagnitude < 9) { selState = null; selEv = null; selSig = null; } drag = Drag.None; }
                    break;
                case Drag.Node:
                    {
                        N n; if (!nodes.TryGetValue(dragState, out n)) { drag = Drag.None; break; }
                        if (ui.held) { var p = dragOrigin + (ui.mouse - dragStart) / zoom; if ((ui.mouse - dragStart).sqrMagnitude > 9) moved[fsmId + "/" + n.key] = p; }
                        else drag = Drag.None;
                        break;
                    }
                case Drag.NewExit:
                case Drag.MoveExit:
                    {
                        Vector2 from;
                        if (drag == Drag.NewExit) { N n; from = nodes.TryGetValue(dragState, out n) ? Handle(NodeRect(area, n)) : dragStart; }
                        else from = dragStart;
                        var col = drag == Drag.NewExit ? cNew : cSel;
                        d.Line(from, ui.mouse, col); d.Line(from + Vector2.up, ui.mouse + Vector2.up, col);
                        N target = null;
                        foreach (var n in nodes.Values) if (!n.global && !n.sig && NodeRect(area, n).Contains(ui.mouse)) target = n;
                        if (target != null) d.Frame(NodeRect(area, target), col);
                        if (!ui.held)
                        {
                            if (target != null)
                            {
                                if (drag == Drag.NewExit) { pendFrom = dragState; pendTo = target.name; pendText = ""; selState = null; selEv = null; }
                                else if (selEv != null) FsmEdit.Wire(fsm, selEvFrom, selEv, target.name, "fsm graph");
                            }
                            drag = Drag.None;
                        }
                        break;
                    }
            }
            if (selEv != null && EditorInput.Key(KeyCode.Delete)) { FsmEdit.Unwire(fsm, selEvFrom, selEv, "fsm graph"); selEv = null; }

            // tooltip
            if (drag == Drag.None && hoverNode != null && hoverNode.sig)
                Tip(d, ui.mouse, hoverNode.name + "   <- " + hoverNode.sub, "click = connect / disconnect signals into this input");
            else if (drag == Drag.None && hoverEdge != null && hoverEdge.sig && hoverNode == null)
                Tip(d, ui.mouse, "signal " + hoverEdge.ev + " arrives here as an event: [" + hoverEdge.to + "] takes this exit", "click = connect / disconnect signals");
            else if (drag == Drag.None && hoverEdge != null && hoverNode == null)
                Tip(d, ui.mouse, (hoverEdge.global ? "from ANY STATE" : "[" + hoverEdge.from + "]") + "  on '" + hoverEdge.ev + "'  ->  [" + hoverEdge.to + "]", "click = select this exit");
            else if (drag == Drag.None && hoverNode != null)
                Tip(d, ui.mouse, hoverHandle ? "drag onto a state = new exit from " + hoverNode.name : hoverNode.name + (hoverNode.name == active ? "  (current state)" : ""), hoverHandle ? "" : "click = details   drag = move the box");
        }

        static void Arrow(Draw d, Vector2 tip, Vector2 dir, Color c)
        {
            if (dir == Vector2.zero) dir = Vector2.right;
            var perp = new Vector2(-dir.y, dir.x);
            d.Triangle(tip, tip - dir * 9 + perp * 4.5f, tip - dir * 9 - perp * 4.5f, c);
        }

        static void Tip(Draw d, Vector2 m, string a, string b)
        {
            float w = Mathf.Max(d.Measure(a, 12), d.Measure(b, 12)) + 12; float h = b.Length > 0 ? 36 : 20;
            var r = new Rect(m.x + 14, m.y + 14, w, h);
            d.Fill(r, new Color(0.05f, 0.06f, 0.08f, 0.96f)); d.Frame(r, UI.Border);
            d.Text(r.x + 6, r.y + 3, a, UI.Txt, 12); if (b.Length > 0) d.Text(r.x + 6, r.y + 19, b, UI.Dim, 12);
        }

        // bridge: text form of what the panel shows
        public static string Dump(PlayMakerFSM f)
        {
            Set(f);
            var sb = new System.Text.StringBuilder();
            Build(null);
            sb.Append(f.gameObject.name).Append(" / ").Append(f.FsmName).Append("   current [").Append(f.ActiveStateName).Append("]   ").Append(nodes.Count).Append(" boxes, ").Append(edges.Count).Append(" exits\n");
            foreach (var n in nodes.Values) sb.Append("  box ").Append(n.name).Append(n.start ? " (start)" : "").Append(" at ").Append(n.pos.ToString("F0")).Append('\n');
            foreach (var e in edges) sb.Append("  exit [").Append(e.global ? "ANY" : e.from).Append("] on '").Append(e.ev).Append("' -> [").Append(e.to).Append("]\n");
            return sb.ToString();
        }
    }
}
