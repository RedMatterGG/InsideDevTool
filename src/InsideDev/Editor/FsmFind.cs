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
    // frame (no hitch) and built again only when it is out of date: objects were loaded or unloaded, or a state machine
    // was rewired or an action setting changed in the editor ("Rescan" forces it). Click a result = select the object
    // and open the FSM Graph with that state / exit / signal input selected.
    // Other code searches through PumpIndex and Query, which leave the panel alone.
    public static class FsmFind
    {
        enum K { Machine, State, Event, SignalIn, Action, SignalOut, Connection }
        static readonly string[] KindNames = { "machine", "state", "event", "signal-in", "action", "signal-out", "connection" };
        sealed class Entry { public K kind; public GameObject go; public PlayMakerFSM fsm; public string text, lower, detail, state, ev, from; public int goId, detailHash; }
        sealed class Hit { public Entry e; public int score; public float dist; public string label; public Color color; }

        // one result of Query
        public sealed class Row
        {
            public int score, id;                   // id: instance id of the object (dies on every load)
            public string kind, label, detail, objectName, fsm, state, ev;
            public bool active; public float dist;
            public GameObject go;
        }

        static List<Entry> index = new List<Entry>(), building;
        static readonly Queue<ObjRecord> todo = new Queue<ObjRecord>();
        static int fsmCount, buildFsms; static float indexedAt = -999f;
        static bool stale = true, force = true;
        static long builtSig;
        static int seenStamp = -1, seenLevels = -1, seenVersion = -1, seenEntryId;
        static string query = "", lastQuery; static int lastCount = -1;
        static bool fEvents = true, fStates = true, fActions = true, fSignals = true, fOff = true;
        static readonly List<Hit> hits = new List<Hit>();
        static string head = "";
        static SearchDelay queryDelay;
        const int Cap = 2000;
        const float MinRebuildGap = 5f;

        static readonly Color cEv = new Color(1f, 0.9f, 0.55f, 1f), cSig = new Color(1f, 0.62f, 0.2f, 1f), cState = new Color(0.6f, 0.8f, 1f, 1f), cAct = new Color(0.75f, 0.75f, 0.8f, 1f), cMach = new Color(0.8f, 0.7f, 1f, 1f), cOffObj = new Color(1f, 0.45f, 0.9f, 1f),
            cSelRow = new Color(0.22f, 0.37f, 0.6f, 0.75f);

        // ------------------------------------------------------------------ index (incremental)
        // out of date: the set of loaded objects changed, or an edit touched a state machine or its signal wiring
        static void CheckStale()
        {
            int st = SceneStamp.Value;
            if (st != seenStamp) { seenStamp = st; stale = true; }
            if (Levels.Changed(ref seenLevels)) stale = true;
            if (ChangeRecorder.Version == seenVersion) return;
            seenVersion = ChangeRecorder.Version;
            var h = ChangeRecorder.Entries;
            int newest = seenEntryId;
            for (int i = h.Count - 1; i >= 0 && i >= h.Count - 40; i--)
            {
                var e = h[i];
                if (e == null || e.id <= seenEntryId) break;
                if (e.id > newest) newest = e.id;
                if (e.action == null) continue;
                string a = e.action.action;
                if (a == "fsmWire" || a == "fsmAction" || a == "sigWire") stale = force = true;
            }
            seenEntryId = newest;
        }

        // which objects carry state machines: when that set is the same as at the last build, objects that came and
        // went since (spawned effects, streamed scenery) do not make the index out of date
        static long FsmSignature()
        {
            long n = 0, x = 0;
            foreach (var r in ObjectDatabase.all) if (r.go != null && r.Has("PlayMakerFSM")) { n++; x ^= (uint)r.id * 2654435761L; }
            return (n << 40) ^ x;
        }

        static void Pump()
        {
            if (!ObjectDatabase.Ready) return;
            float now = Time.realtimeSinceStartup;
            if (building == null)
            {
                CheckStale();
                if (stale && now - indexedAt > MinRebuildGap)
                {
                    if (force || FsmSignature() != builtSig) Start();
                    else { stale = false; indexedAt = now; }
                }
            }
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
            indexedAt = Time.realtimeSinceStartup; stale = force = false;
            foreach (var r in ObjectDatabase.all) if (r.go != null && r.Has("PlayMakerFSM")) todo.Enqueue(r);
            builtSig = FsmSignature();
        }

        static void Add(List<Entry> l, K k, GameObject go, PlayMakerFSM f, string text, string detail, string state = null, string ev = null, string from = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            l.Add(new Entry { kind = k, go = go, goId = go.GetInstanceID(), fsm = f, text = text, lower = text.ToLowerInvariant(), detail = detail, detailHash = detail.GetHashCode(), state = state, ev = ev, from = from });
        }

        static bool IsInput(string eventName) { return eventName.Length > 0 && eventName[0] == '>'; }

        static void IndexFsm(List<Entry> l, PlayMakerFSM f)
        {
            var go = f.gameObject;
            Add(l, K.Machine, go, f, f.FsmName + " " + go.name, "state machine '" + f.FsmName + "'");
            var g = f.FsmGlobalTransitions;
            if (g != null) foreach (var t in g) if (t != null && !string.IsNullOrEmpty(t.EventName))
                    Add(l, IsInput(t.EventName) ? K.SignalIn : K.Event, go, f, t.EventName, (IsInput(t.EventName) ? "signal input '" : "global event '") + t.EventName + "' -> [" + t.ToState + "]", null, t.EventName, "*");
            var states = f.FsmStates; if (states == null) return;
            foreach (var st in states)
            {
                if (st == null) continue;
                Add(l, K.State, go, f, st.Name, "state [" + st.Name + "]", st.Name);
                if (st.Transitions != null)
                    foreach (var t in st.Transitions) if (t != null && !string.IsNullOrEmpty(t.EventName))
                            Add(l, IsInput(t.EventName) ? K.SignalIn : K.Event, go, f, t.EventName, (IsInput(t.EventName) ? "signal input '" : "event '") + t.EventName + "' in [" + st.Name + "] -> [" + t.ToState + "]", null, t.EventName, st.Name);
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
                    Add(l, K.Connection, go, rf, s, "connection " + s + (c.isConnected ? "" : "  (waiting)"), null, c.signalInName != null && IsInput(c.signalInName) ? c.signalInName : null);
                }
            if (sm.signalOuts != null)
                foreach (var so in sm.signalOuts)
                    if (so != null && so.isActive && so.gameObject != null)
                        Add(l, K.SignalOut, so.gameObject, so.gameObject.GetComponent<PlayMakerFSM>(), so.debugName, "signal output '" + so.debugName + "'");
        }

        // ------------------------------------------------------------------ search
        const int AllKinds = 127;
        static int KindMask() { return (fEvents ? 1 << (int)K.Event : 0) | (fEvents || fSignals ? 1 << (int)K.SignalIn : 0) | (fStates ? (1 << (int)K.State) | (1 << (int)K.Machine) : 0) | (fActions ? 1 << (int)K.Action : 0) | (fSignals ? (1 << (int)K.SignalOut) | (1 << (int)K.Connection) : 0); }

        static readonly HashSet<long> seen = new HashSet<long>();

        // the panel's search: the text of the Find box, the panel's toggles
        static void Search() { head = Collect(query, hits, Cap, KindMask(), fOff); }

        // ranked matches of q in the current index; returns the header line
        static string Collect(string q, List<Hit> into, int cap, int kinds, bool withOff)
        {
            into.Clear();
            var words = (q ?? "").Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return index.Count + " entries indexed from " + fsmCount + " state machines + signal wiring. Type an event, state, animation or signal name.";
            var ch = G.MainCharacter; Vector3 o = ch != null ? ch.pos3 : Vector3.zero;
            seen.Clear();
            int over = 0;
            foreach (var e in index)
            {
                if ((kinds & (1 << (int)e.kind)) == 0) continue;
                int score = 0; bool all = true;
                foreach (var w in words)
                {
                    int i = e.lower.IndexOf(w, StringComparison.Ordinal);
                    if (i < 0) { all = false; break; }
                    score += e.lower.Length == w.Length ? 100 : i == 0 || !char.IsLetterOrDigit(e.lower[i - 1]) || (i < e.text.Length && char.IsUpper(e.text[i])) ? 60 : 30;
                }
                if (!all) continue;
                if (e.go == null) continue;
                if (!withOff && !e.go.activeInHierarchy) continue;
                score += e.kind == K.SignalIn ? 30 : e.kind == K.Event ? 25 : e.kind == K.Connection ? 22 : e.kind == K.SignalOut ? 20 : e.kind == K.State ? 15 : e.kind == K.Machine ? 10 : 0;
                // same entry twice (e.g. two identical exits) = one row
                if (!seen.Add(((long)e.goId << 32) | (uint)e.detailHash)) continue;
                if (into.Count >= cap) { over++; continue; }
                into.Add(new Hit { e = e, score = score, dist = (e.go.transform.position - o).magnitude });
            }
            seen.Clear();
            into.Sort((a, b) => a.score != b.score ? b.score.CompareTo(a.score) : a.dist.CompareTo(b.dist));
            foreach (var h in into)
            {
                var e = h.e;
                bool on = e.go.activeInHierarchy;
                string where = e.go.name + (e.fsm != null && e.kind != K.SignalOut && e.kind != K.Connection ? " / " + e.fsm.FsmName : "");
                h.label = (on ? "" : "[off] ") + where + "   · " + e.detail + "   " + Fmt.F(h.dist, "0") + " m";
                h.color = !on ? (e.go.activeSelf ? UI.Dim : cOffObj) : e.kind == K.SignalIn || e.kind == K.SignalOut || e.kind == K.Connection ? cSig : e.kind == K.Event ? cEv : e.kind == K.State ? cState : e.kind == K.Machine ? cMach : cAct;
            }
            return into.Count + " match(es)" + (over > 0 ? "  (" + over + " more - add a word)" : "") + " in " + fsmCount + " state machines.  Click = select + open in FSM Graph.";
        }

        // ------------------------------------------------------------------ for other code (MCP fsm_find, scripts)
        // Call once per frame (main thread) until it returns false: the index is then complete and current.
        public static bool PumpIndex()
        {
            ObjectDatabase.Demand(5f);
            if (!ObjectDatabase.Ready) return true;
            Pump();
            return building != null;
        }

        public static int IndexedEntries { get { return index.Count; } }
        public static bool Indexing { get { return building != null; } }

        // The matches of q, best first, at most limit rows; total = how many matched (up to 2000). Every kind of entry and
        // switched-off objects are included. Reads the index as it is: pump it first. Does not touch the panel.
        static readonly List<Hit> queryHits = new List<Hit>();
        public static List<Row> Query(string q, int limit, out int total, out string header)
        {
            header = Collect(q, queryHits, Cap, AllKinds, true);
            total = queryHits.Count;
            var rows = new List<Row>(Math.Min(Math.Max(limit, 0), total));
            for (int i = 0; i < queryHits.Count && i < limit; i++)
            {
                var h = queryHits[i]; var e = h.e;
                rows.Add(new Row { score = h.score, id = e.goId, kind = KindNames[(int)e.kind], label = h.label, detail = e.detail, objectName = e.go.name, fsm = e.fsm != null ? e.fsm.FsmName : null, state = e.state, ev = e.ev, active = e.go.activeInHierarchy, dist = h.dist, go = e.go });
            }
            queryHits.Clear();
            return rows;
        }

        // ------------------------------------------------------------------ UI
        static UI curUi; static int curSel;
        static readonly Action<int> drawRow = DrawRow;

        public static void Draw(UI ui)
        {
            ObjectDatabase.Demand(5f);
            Background.Want(Background.Objects, 5f);
            Pump();
            ui.BeginRow();
            ui.Label("Find:", null, 34);
            EditorInput.Field(ui, "fsmfind", ref query, Mathf.Max(80, ui.Width - 130));
            if (ui.Button("Rescan")) { stale = force = true; indexedAt = -999f; }
            ui.EndRow();
            ui.BeginRow();
            bool a = ui.Toggle(fEvents, "events"), b = ui.Toggle(fStates, "states"), c = ui.Toggle(fActions, "actions"), d = ui.Toggle(fSignals, "signals"), e = ui.Toggle(fOff, "switched-off objects");
            if (a != fEvents || b != fStates || c != fActions || d != fSignals || e != fOff) { fEvents = a; fStates = b; fActions = c; fSignals = d; fOff = e; lastQuery = null; }
            ui.EndRow();
            if (!ObjectDatabase.Ready) { ui.Label("scanning scene… (" + ObjectDatabase.Stats() + ")", UI.Dim); return; }
            if (building != null) ui.Label("indexing state machines… " + todo.Count + " objects left" + (index.Count > 0 ? " (searching the previous index meanwhile)" : ""), UI.Dim);
            string q = queryDelay.Value(query, 0.12f);
            if (q != lastQuery || index.Count != lastCount) { lastQuery = q; lastCount = index.Count; head = Collect(q, hits, Cap, KindMask(), fOff); }
            ui.Label(head, UI.Dim);
            var sel = Selection.Current;
            curSel = sel != null ? sel.GetInstanceID() : 0; curUi = ui;
            ui.VirtualList("fsmfind_list", hits.Count, UI.ItemPitch, ui.Remaining, drawRow);
        }

        static void DrawRow(int i)
        {
            if (i >= hits.Count) return;
            var ui = curUi;
            var h = hits[i];
            if (h.e.go == null) { ui.Item("(destroyed)", UI.Dim); return; }
            if (h.e.goId == curSel) ui.RowHighlight(cSelRow);
            bool ck = ui.Item(h.label, h.color);
            if (ui.LastHover) Selection.SetHover(h.e.go);
            if (ck) Open(h.e);
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

        // bridge: "fsmfind <text>" prints the best 30 matches; "fsmfind <text> json" answers with one JSON line
        // { ok, query, total, indexed, hits: [ { score, kind, object, id, fsm, state, event, detail, active, dist } ] } (60 hits).
        // The Find box of the panel keeps its own text.
        public static string Report(string q)
        {
            q = (q ?? "").Trim();
            bool json = q == "json" || q.EndsWith(" json", StringComparison.Ordinal);
            if (json) q = q.Substring(0, q.Length - 4).Trim();
            ObjectDatabase.Demand(5f);
            if (!ObjectDatabase.Ready) return json ? JsonLine.Obj("ok", false, "error", "object scan not finished yet") : "object scan not finished yet";
            if (index.Count == 0 && building == null) Start();
            // finish a running build now, but never hold the frame for more than two seconds
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (building != null && sw.ElapsedMilliseconds < 2000) Pump();
            if (building != null && index.Count == 0)
            {
                string busy = "busy: indexing state machines (" + todo.Count + " objects left), ask again in a moment";
                return json ? JsonLine.Obj("ok", false, "error", busy) : busy;
            }
            int total; string header;
            var rows = Query(q, json ? 60 : 30, out total, out header);
            if (json)
            {
                var list = new List<object>();
                foreach (var r in rows)
                {
                    var o = new Json.Obj();
                    o["score"] = r.score; o["kind"] = r.kind; o["object"] = r.objectName; o["id"] = r.id; o["fsm"] = r.fsm; o["state"] = r.state; o["event"] = r.ev;
                    o["detail"] = r.detail; o["active"] = r.active; o["dist"] = r.dist;
                    list.Add(o);
                }
                var root = new Json.Obj(); root["ok"] = true; root["query"] = q; root["total"] = total; root["indexed"] = index.Count; root["hits"] = list;
                return JsonLine.Write(root);
            }
            var sb = new System.Text.StringBuilder(header + "\n");
            foreach (var r in rows) sb.Append("  ").Append(r.score).Append("  ").Append(r.label).Append('\n');
            return sb.ToString();
        }
    }
}
