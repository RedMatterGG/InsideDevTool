using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace InsideDev
{
    // FSM Find panel: searches INSIDE state machines and signal wiring (what the Explorer's name search can't see):
    //   events and signal inputs ('>EndNow'), state names, state machine names, action settings (animation names,
    //   signal names, targets...), signal outputs and signal connections between objects.
    // Every loaded PlayMakerFSM is indexed, including ones on switched-off objects. The index is built a few FSMs per
    // frame (no hitch) and refreshed when the loaded scenes change. Click a result = select the object and open the
    // FSM Graph with that state / exit / signal input selected.
    public static class FsmFind
    {
        enum K { Machine, State, Event, SignalIn, Action, SignalOut, Connection }
        sealed class Entry { public K kind; public GameObject go; public PlayMakerFSM fsm; public string text, lower, detail, state, ev, from; }
        sealed class Hit { public Entry e; public int score; public float dist; public string label; public Color color; }

        static List<Entry> index = new List<Entry>(), building;
        static readonly Queue<ObjRecord> todo = new Queue<ObjRecord>();
        static int indexedPass = -1, fsmCount, buildFsms; static float indexedAt = -999f;
        static string query = "", lastQuery; static int lastCount = -1;
        static bool fEvents = true, fStates = true, fActions = true, fSignals = true, fOff = true;
        static readonly List<Hit> hits = new List<Hit>();
        static string head = "";
        const int Cap = 2000;

        static readonly Color cEv = new Color(1f, 0.9f, 0.55f, 1f), cSig = new Color(1f, 0.62f, 0.2f, 1f), cState = new Color(0.6f, 0.8f, 1f, 1f), cAct = new Color(0.75f, 0.75f, 0.8f, 1f), cMach = new Color(0.8f, 0.7f, 1f, 1f), cOffObj = new Color(1f, 0.45f, 0.9f, 1f);

        // ------------------------------------------------------------------ index (incremental)
        static void Pump()
        {
            if (!ObjectDatabase.Ready) return;
            float now = Time.realtimeSinceStartup;
            if (building == null && ObjectDatabase.completedPasses != indexedPass && now - indexedAt > 20f) Start();
            if (building == null) return;
            float until = now + 0.006f;
            while (todo.Count > 0 && Time.realtimeSinceStartup < until)
            {
                var r = todo.Dequeue();
                if (r.go == null) continue;
                try { foreach (var f in r.go.GetComponents<PlayMakerFSM>()) if (f != null) { IndexFsm(building, f); buildFsms++; } } catch { }
            }
            if (todo.Count == 0)
            {
                try { IndexSignals(building); } catch (Exception e) { DevLog.Error("fsm find signals", e); }
                index = building; building = null; fsmCount = buildFsms; lastQuery = null;
            }
        }

        static void Start()
        {
            building = new List<Entry>(index.Count + 256); todo.Clear(); buildFsms = 0;
            indexedPass = ObjectDatabase.completedPasses; indexedAt = Time.realtimeSinceStartup;
            foreach (var r in ObjectDatabase.all) if (r.go != null && r.Has("PlayMakerFSM")) todo.Enqueue(r);
        }

        static void Add(List<Entry> l, K k, GameObject go, PlayMakerFSM f, string text, string detail, string state = null, string ev = null, string from = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            l.Add(new Entry { kind = k, go = go, fsm = f, text = text, lower = text.ToLowerInvariant(), detail = detail, state = state, ev = ev, from = from });
        }

        static void IndexFsm(List<Entry> l, PlayMakerFSM f)
        {
            var go = f.gameObject;
            Add(l, K.Machine, go, f, f.FsmName + " " + go.name, "state machine '" + f.FsmName + "'");
            var g = f.FsmGlobalTransitions;
            if (g != null) foreach (var t in g) if (t != null && !string.IsNullOrEmpty(t.EventName))
                    Add(l, t.EventName.StartsWith(">") ? K.SignalIn : K.Event, go, f, t.EventName, (t.EventName.StartsWith(">") ? "signal input '" : "global event '") + t.EventName + "' -> [" + t.ToState + "]", null, t.EventName, "*");
            var states = f.FsmStates; if (states == null) return;
            foreach (var st in states)
            {
                if (st == null) continue;
                Add(l, K.State, go, f, st.Name, "state [" + st.Name + "]", st.Name);
                if (st.Transitions != null)
                    foreach (var t in st.Transitions) if (t != null && !string.IsNullOrEmpty(t.EventName))
                            Add(l, t.EventName.StartsWith(">") ? K.SignalIn : K.Event, go, f, t.EventName, (t.EventName.StartsWith(">") ? "signal input '" : "event '") + t.EventName + "' in [" + st.Name + "] -> [" + t.ToState + "]", null, t.EventName, st.Name);
                if (st.Actions != null)
                    foreach (var a in st.Actions)
                    {
                        if (a == null) continue;
                        string tn = a.GetType().Name, sum = "";
                        try { sum = Links.ActionSummary(a); } catch { }
                        string d = tn + sum; if (d.Length > 160) d = d.Substring(0, 159) + "…";
                        Add(l, tn.IndexOf("Signal", StringComparison.Ordinal) >= 0 ? K.SignalOut : K.Action, go, f, tn + " " + (a.Name ?? "") + sum, "action " + d + "  in [" + st.Name + "]", st.Name);
                    }
            }
        }

        static void IndexSignals(List<Entry> l)
        {
            SignalManager sm = null; try { sm = PersistentBehaviour<SignalManager>.instance; } catch { }
            if (sm == null) return;
            if (sm.connections != null)
                foreach (var c in sm.connections)
                {
                    if (c == null || !c.isActive) continue;
                    string s = (c.signalOutGameObject != null ? c.signalOutGameObject.name : "?") + "." + c.signalOutName + " -> " + (c.signalInGameObject != null ? c.signalInGameObject.name : "?") + "." + c.signalInName;
                    var rf = c.signalInGameObject != null ? c.signalInGameObject.GetComponent<PlayMakerFSM>() : null;
                    var go = c.signalInGameObject ?? c.signalOutGameObject; if (go == null) continue;
                    Add(l, K.Connection, go, rf, s, "connection " + s + (c.isConnected ? "" : "  (waiting)"), null, c.signalInName != null && c.signalInName.StartsWith(">") ? c.signalInName : null);
                }
            if (sm.signalOuts != null)
                foreach (var so in sm.signalOuts)
                    if (so != null && so.isActive && so.gameObject != null)
                        Add(l, K.SignalOut, so.gameObject, so.gameObject.GetComponent<PlayMakerFSM>(), so.debugName, "signal output '" + so.debugName + "'");
        }

        // ------------------------------------------------------------------ search
        static bool Wanted(K k)
        {
            switch (k)
            {
                case K.Event: return fEvents;
                case K.SignalIn: return fEvents || fSignals;
                case K.State: case K.Machine: return fStates;
                case K.Action: return fActions;
                default: return fSignals;
            }
        }

        static void Search()
        {
            hits.Clear();
            var words = query.Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) { head = index.Count + " entries indexed from " + fsmCount + " state machines + signal wiring. Type an event, state, animation or signal name."; return; }
            var ch = G.MainCharacter; Vector3 o = ch != null ? ch.pos3 : Vector3.zero;
            var seen = new HashSet<string>();
            int over = 0;
            foreach (var e in index)
            {
                if (e.go == null || !Wanted(e.kind)) continue;
                if (!fOff && !e.go.activeInHierarchy) continue;
                int score = 0; bool all = true;
                foreach (var w in words)
                {
                    int i = e.lower.IndexOf(w, StringComparison.Ordinal);
                    if (i < 0) { all = false; break; }
                    score += e.lower.Length == w.Length ? 100 : i == 0 || !char.IsLetterOrDigit(e.lower[i - 1]) || (i < e.text.Length && char.IsUpper(e.text[i])) ? 60 : 30;
                }
                if (!all) continue;
                score += e.kind == K.SignalIn ? 30 : e.kind == K.Event ? 25 : e.kind == K.Connection ? 22 : e.kind == K.SignalOut ? 20 : e.kind == K.State ? 15 : e.kind == K.Machine ? 10 : 0;
                string key = e.go.GetInstanceID() + "|" + e.detail;   // same entry twice (e.g. two identical exits) = one row
                if (!seen.Add(key)) continue;
                if (hits.Count >= Cap) { over++; continue; }
                hits.Add(new Hit { e = e, score = score, dist = (e.go.transform.position - o).magnitude });
            }
            hits.Sort((a, b) => a.score != b.score ? b.score.CompareTo(a.score) : a.dist.CompareTo(b.dist));
            foreach (var h in hits)
            {
                var e = h.e;
                string where = e.go.name + (e.fsm != null && e.kind != K.SignalOut && e.kind != K.Connection ? " / " + e.fsm.FsmName : "");
                h.label = (e.go.activeInHierarchy ? "" : "[off] ") + where + "   · " + e.detail + "   " + h.dist.ToString("0") + " m";
                h.color = !e.go.activeInHierarchy ? (e.go.activeSelf ? UI.Dim : cOffObj) : e.kind == K.SignalIn || e.kind == K.SignalOut || e.kind == K.Connection ? cSig : e.kind == K.Event ? cEv : e.kind == K.State ? cState : e.kind == K.Machine ? cMach : cAct;
            }
            head = hits.Count + " match(es)" + (over > 0 ? "  (" + over + " more - add a word)" : "") + " in " + fsmCount + " state machines.  Click = select + open in FSM Graph.";
        }

        // ------------------------------------------------------------------ UI
        public static void Draw(UI ui)
        {
            ObjectDatabase.Demand(5f);
            Pump();
            ui.BeginRow();
            ui.Label("Find:", null, 34);
            ui.TextField("fsmfind", ref query, Mathf.Max(80, ui.Width - 130));
            if (ui.Button("Rescan")) { indexedAt = -999f; indexedPass = -1; }
            ui.EndRow();
            ui.BeginRow();
            bool a = ui.Toggle(fEvents, "events"), b = ui.Toggle(fStates, "states"), c = ui.Toggle(fActions, "actions"), d = ui.Toggle(fSignals, "signals"), e = ui.Toggle(fOff, "switched-off objects");
            if (a != fEvents || b != fStates || c != fActions || d != fSignals || e != fOff) { fEvents = a; fStates = b; fActions = c; fSignals = d; fOff = e; lastQuery = null; }
            ui.EndRow();
            if (!ObjectDatabase.Ready) { ui.Label("scanning scene… (" + ObjectDatabase.Stats() + ")", UI.Dim); return; }
            if (building != null) ui.Label("indexing state machines… " + todo.Count + " objects left" + (index.Count > 0 ? " (searching the previous index meanwhile)" : ""), UI.Dim);
            if (query != lastQuery || index.Count != lastCount) { lastQuery = query; lastCount = index.Count; Search(); }
            ui.Label(head, UI.Dim);
            var sel = Selection.Current; int selId = sel != null ? sel.GetInstanceID() : 0;
            var list = hits;
            ui.VirtualList("fsmfind_list", list.Count, UI.ItemPitch, ui.Remaining, i =>
            {
                var h = list[i];
                if (h.e.go == null) { ui.Item("(destroyed)", UI.Dim); return; }
                if (h.e.go.GetInstanceID() == selId) ui.RowHighlight(new Color(0.22f, 0.37f, 0.6f, 0.75f));
                bool ck = ui.Item(h.label, h.color);
                if (ui.LastHover) Selection.SetHover(h.e.go);
                if (ck) Open(h.e);
            });
        }

        static void Open(Entry e)
        {
            Selection.Set(e.go, "fsm find");
            var f = e.fsm;
            if (f == null) return;
            if (e.kind == K.SignalIn || (e.kind == K.Connection && e.ev != null)) FsmGraph.Focus(f, null, null, null, e.ev);
            else if (e.kind == K.Event) FsmGraph.Focus(f, null, e.from, e.ev, null);
            else if (e.kind == K.State || e.kind == K.Action || (e.kind == K.SignalOut && e.state != null)) FsmGraph.Focus(f, e.state, null, null, null);
            else FsmGraph.Focus(f, null, null, null, null);
        }

        // bridge: same search as text
        public static string Report(string q)
        {
            if (!ObjectDatabase.Ready) return "object scan not finished yet";
            if (index.Count == 0 && building == null) Start();
            int guard = 0; while (building != null && guard++ < 100000) Pump();
            query = q; lastQuery = q; lastCount = index.Count; Search();
            var sb = new System.Text.StringBuilder(head + "\n");
            for (int i = 0; i < hits.Count && i < 30; i++) sb.Append("  ").Append(hits[i].score).Append("  ").Append(hits[i].label).Append('\n');
            return sb.ToString();
        }
    }
}
