using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    // "What is this object wired to / what does it want": Playdead signal connections + PlayMaker state machines.
    public static class Links
    {
        static readonly HashSet<string> open = new HashSet<string>();
        static readonly Dictionary<string, string> edits = new Dictionary<string, string>();
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly Color cOut = new Color(1f, 0.75f, 0.35f, 1f);
        static readonly Color cIn = new Color(0.45f, 0.85f, 1f, 1f);

        static UObj[] connCache;
        static float nextConnScan;

        static string N(GameObject g) { return g == null ? "(null)" : g.name; }

        public static void Draw(UI ui, GameObject go, Inspector insp)
        {
            DrawSignals(ui, go, insp);
            DrawConnectors(ui, go, insp);
            foreach (var fsm in go.GetComponents<PlayMakerFSM>()) if (fsm != null) DrawFsm(ui, fsm, insp);
        }

        // collapsible section header; the open/closed state is saved in editor.cfg (kept across restarts)
        public static bool Section(UI ui, string key, string title, int count, Color c)
        {
            string k = "insp.fold." + key;
            bool open = EditorState.Get(k, true);
            if (ui.Item((open ? "v " : "> ") + title + "   (" + count + ")", c)) { open = !open; EditorState.Set(k, open); }
            return open;
        }

        // ---------------------------------------------------------------- signals
        static void DrawSignals(UI ui, GameObject go, Inspector insp)
        {
            SignalManager sm = null;
            try { sm = PersistentBehaviour<SignalManager>.instance; } catch { }
            if (sm == null || sm.connections == null) return;
            int total = 0;
            foreach (var c in sm.connections) if (c != null && c.isActive && (c.signalOutGameObject == go || c.signalInGameObject == go)) total++;
            if (sm.signalOuts != null) foreach (var so in sm.signalOuts) if (so != null && so.isActive && so.gameObject == go) total++;
            if (sm.signalIns != null) foreach (var si in sm.signalIns) if (si != null && si.isActive && si.gameObject == go) total++;
            if (total == 0) return;
            if (!Section(ui, "signals", "Signal connections (Playdead SignalManager)", total, UI.Dim)) return;
            foreach (var c in sm.connections)
            {
                if (c == null || !c.isActive) continue;
                bool isOut = c.signalOutGameObject == go, isIn = c.signalInGameObject == go;
                if (!isOut && !isIn) continue;
                ui.BeginRow();
                if (isOut)
                {
                    ui.Label("  OUT  " + c.signalOutName + "  ->  " + N(c.signalInGameObject) + "." + c.signalInName + (c.IsInputFsm ? " (FSM event)" : "") + (c.isConnected ? "" : "  [not connected]"), cOut, ui.Width - 130);
                    if (c.signalInGameObject != null && ui.Button("select", 60)) { ui.EndRow(); insp.Select(c.signalInGameObject); return; }
                }
                else
                {
                    ui.Label("  IN   " + N(c.signalOutGameObject) + "." + c.signalOutName + "  ->  " + c.signalInName + (c.isConnected ? "" : "  [not connected]"), cIn, ui.Width - 130);
                    if (c.signalOutGameObject != null && ui.Button("select", 60)) { ui.EndRow(); insp.Select(c.signalOutGameObject); return; }
                }
                if (ui.Button("fire", 50)) Fire(c);
                ui.EndRow();
            }
            // signals declared on this object (even if nothing is connected to them)
            if (sm.signalOuts != null)
                foreach (var so in sm.signalOuts)
                {
                    if (so == null || !so.isActive || so.gameObject != go) continue;
                    ui.BeginRow();
                    ui.Label("  signal out: " + (so.debugName ?? so.signalHash.ToString()), cOut, ui.Width - 60);
                    if (ui.Button("fire", 50)) { DevLog.Write("signal out " + go.name + "." + so.debugName); ChangeRecorder.Action(go, "signal", null, so.debugName, "inspector"); so.Signal(); }
                    ui.EndRow();
                }
            if (sm.signalIns != null)
                foreach (var si in sm.signalIns)
                {
                    if (si == null || !si.isActive || si.gameObject != go) continue;
                    ui.BeginRow();
                    ui.Label("  signal in:  " + (si.debugName ?? si.signalHash.ToString()), cIn, ui.Width - 60);
                    if (si.action != null && ui.Button("fire", 50)) { DevLog.Write("signal in " + go.name + "." + si.debugName); try { si.action(); } catch (Exception e) { DevLog.Error("signal in", e); } }
                    ui.EndRow();
                }
        }

        static void Fire(SignalConnection c)
        {
            DevLog.Write("fire " + N(c.signalOutGameObject) + "." + c.signalOutName + " -> " + N(c.signalInGameObject) + "." + c.signalInName);
            if (c.signalIn == null && c.fsmIn != null) ChangeRecorder.Action(c.signalInGameObject, "fsmEvent", c.fsmIn.Name, c.signalInName, "inspector");
            else ChangeRecorder.Action(c.signalOutGameObject, "signal", null, c.signalOutName, "inspector");
            try
            {
                if (c.signalIn != null && c.signalIn.action != null) c.signalIn.action();
                else if (c.fsmIn != null) c.fsmIn.Event(c.signalInName);
                else if (c.signalOut != null) c.signalOut.Signal();
            }
            catch (Exception e) { DevLog.Error("fire", e); }
        }

        // SignalConnector components that reference this object (serialized wiring in the scene)
        static void DrawConnectors(UI ui, GameObject go, Inspector insp)
        {
            if (Time.realtimeSinceStartup > nextConnScan || connCache == null)
            {
                nextConnScan = Time.realtimeSinceStartup + 2f;
                connCache = Resources.FindObjectsOfTypeAll(typeof(SignalConnector));
            }
            int total = 0;
            foreach (var o in connCache) { var k = o as SignalConnector; if (k != null && (k.sender == go || k.receiver == go || k.gameObject == go)) total++; }
            if (total == 0) return;
            if (!Section(ui, "connectors", "SignalConnector wiring", total, UI.Dim)) return;
            foreach (var o in connCache)
            {
                var sc = o as SignalConnector;
                if (sc == null || (sc.sender != go && sc.receiver != go && sc.gameObject != go)) continue;
                ui.BeginRow();
                ui.Label("  " + N(sc.sender) + "." + sc.signalOutName + "  ->  " + N(sc.receiver) + "." + sc.signalInName + (sc.enabled ? "" : "  [disabled]"), UI.Txt, ui.Width - 70);
                var other = sc.receiver == go ? sc.sender : sc.receiver;
                if (other != null && other != go && ui.Button("select", 60)) { ui.EndRow(); insp.Select(other); return; }
                ui.EndRow();
            }
        }

        // ---------------------------------------------------------------- PlayMaker
        static void DrawFsm(UI ui, PlayMakerFSM fsm, Inspector insp)
        {
            string key = "fsm" + fsm.GetInstanceID();
            bool isOpen = open.Contains(key);
            ui.BeginRow();
            if (ui.Item((isOpen ? "v " : "> ") + "FSM '" + fsm.FsmName + "'   state: " + fsm.ActiveStateName + (fsm.enabled ? "" : "  [disabled]"), new Color(0.8f, 0.7f, 1f, 1f), ui.Width - 70))
            { if (isOpen) open.Remove(key); else open.Add(key); }
            if (ui.Button("graph", 56)) FsmGraph.Open(fsm);
            ui.EndRow();
            if (!isOpen) return;
            if (!string.IsNullOrEmpty(fsm.FsmDescription)) ui.Label("    " + fsm.FsmDescription, UI.Dim);
            ui.Label("    fire = send the event as the game would (only acts if the current state listens for it)   force = jump straight to a state, skipping conditions", UI.Dim);
            ui.Label("    rewire: 'change' moves an exit to another state, 'x' removes it, '+ exit' adds 'on <event> -> <state>' (History records it; Record mod keeps it)", UI.Dim);
            if (FsmEdit.IsEdited(fsm))
            {
                ui.BeginRow();
                ui.Label("    wiring changed live", new Color(1f, 0.6f, 0.3f, 1f), ui.Width - 130);
                if (ui.Button("reset wiring", 110)) { FsmEdit.Reset(fsm); wire = null; }
                ui.EndRow();
            }

            // events this FSM listens for = what the trigger "wants"
            var globals = fsm.FsmGlobalTransitions;
            ui.BeginRow();
            ui.Label("    global events (from any state):" + (globals == null || globals.Length == 0 ? " none" : ""), UI.Dim, ui.Width - 90);
            if (ui.Button("+ exit", 70)) OpenWire(fsm, "*", null, true);
            ui.EndRow();
            WireEditor(ui, fsm, "*", null);
            if (globals != null)
                foreach (var t in globals) { EventRow(ui, fsm, t, "      ", "*"); }
            FsmState[] states = fsm.FsmStates;
            if (states != null)
                foreach (var st in states)
                {
                    if (st == null) continue;
                    bool active = st.Name == fsm.ActiveStateName;
                    string sk = key + "/" + st.Name;
                    bool so = open.Contains(sk);
                    ui.BeginRow();
                    if (ui.Item("    " + (so ? "v " : "> ") + (active ? "[*] " : "") + st.Name + "   (" + (st.Transitions != null ? st.Transitions.Length : 0) + " exits)", active ? UI.Accent : UI.Txt, ui.Width - 60))
                    { if (so) open.Remove(sk); else open.Add(sk); }
                    if (ui.Button("force", 50)) { DevLog.Write("fsm " + fsm.gameObject.name + "/" + fsm.FsmName + " -> state " + st.Name); ChangeRecorder.Action(fsm.gameObject, "fsmState", fsm.FsmName, st.Name, "inspector"); fsm.SetState(st.Name); }
                    ui.EndRow();
                    if (!so) continue;
                    if (st.Actions != null)
                        foreach (var a in st.Actions)
                            if (a != null)
                            {
                                string tn = a.GetType().Name;
                                if (tn == "WaitForMusicCueOrMarker")
                                {
                                    var cf = a.GetType().GetField("cueOrMarkerName");
                                    var cv = cf != null ? cf.GetValue(a) as FsmString : null;
                                    string cue = cv != null ? cv.Value : "";
                                    ui.BeginRow();
                                    ui.Label("          AUDIO: waits for music cue '" + cue + "'", new Color(1f, 0.85f, 0.3f, 1f), ui.Width - 100);
                                    if (ui.Button("inject cue", 90)) AudioSpy.InjectCue(string.IsNullOrEmpty(cue) ? "__any__" : cue);
                                    ui.EndRow();
                                }
                                else if (tn == "WaitForBeat")
                                {
                                    ui.BeginRow();
                                    ui.Label("          AUDIO: waits for music beat", new Color(1f, 0.85f, 0.3f, 1f), ui.Width - 100);
                                    if (ui.Button("inject beat", 90)) AudioSpy.InjectBeat();
                                    ui.EndRow();
                                }
                                else ui.Label("          action: " + tn + (string.IsNullOrEmpty(a.Name) ? "" : " '" + a.Name + "'") + (a.Enabled ? "" : " [off]") + ActionSummary(a), UI.Dim);
                            }
                    ui.BeginRow();
                    ui.Label("          exits:", UI.Dim, ui.Width - 90);
                    if (ui.Button("+ exit", 70)) OpenWire(fsm, st.Name, null, true);
                    ui.EndRow();
                    WireEditor(ui, fsm, st.Name, null);
                    if (st.Transitions != null)
                        foreach (var t in st.Transitions) EventRow(ui, fsm, t, "          ", st.Name);
                }

            // variables (editable)
            FsmVariables vars = null;
            try { vars = fsm.FsmVariables; } catch { }
            if (vars == null) return;
            NamedVariable[] all = null;
            try { all = vars.GetAllNamedVariables(); } catch { }
            if (all == null || all.Length == 0) return;
            ui.Label("    variables:", UI.Dim);
            foreach (var v in all) VarRow(ui, fsm, v, insp);
        }

        // key parameters of a PlayMaker action, incl. nested game data (e.g. BoyVoiceConfig -> config={...})
        public static string ActionSummary(FsmStateAction a)
        {
            var sb = new System.Text.StringBuilder();
            int k = 0;
            foreach (var f in ValueDump.Fields(a.GetType(), false))
            {
                if (k > 10 || f.DeclaringType == typeof(FsmStateAction) || !ValueDump.IsSerialized(f)) continue;
                object v; try { v = f.GetValue(a); } catch { continue; }
                string d = Remote.ActionParam(v);
                if (d == null) continue;
                sb.Append(k++ == 0 ? "   { " : ", ").Append(f.Name).Append('=').Append(d);
            }
            if (k > 0) sb.Append(" }");
            return sb.ToString();
        }

        static void EventRow(UI ui, PlayMakerFSM fsm, FsmTransition t, string indent, string state)
        {
            if (t == null) return;
            ui.BeginRow();
            ui.Label(indent + "on '" + t.EventName + "'  ->  " + t.ToState, new Color(1f, 0.9f, 0.55f, 1f), ui.Width - 180);
            if (!string.IsNullOrEmpty(t.EventName) && ui.Button("fire", 50))
            {
                DevLog.Write("fsm " + fsm.gameObject.name + "/" + fsm.FsmName + " <- event " + t.EventName);
                ChangeRecorder.Action(fsm.gameObject, "fsmEvent", fsm.FsmName, t.EventName, "inspector");
                fsm.SendEvent(t.EventName);
            }
            if (ui.Button("change", 70)) OpenWire(fsm, state, t.EventName, false);
            if (ui.Button("x", 30)) { FsmEdit.Unwire(fsm, state, t.EventName, "inspector"); ui.EndRow(); return; }
            ui.EndRow();
            WireEditor(ui, fsm, state, t.EventName);
        }

        // ---- rewiring editor (one open at a time)
        sealed class WireEdit { public int fsm; public string state, ev; public bool add; public string text = ""; }
        static WireEdit wire;
        static void OpenWire(PlayMakerFSM fsm, string state, string ev, bool add)
        {
            if (wire != null && wire.fsm == fsm.GetInstanceID() && wire.state == state && wire.ev == ev && wire.add == add) { wire = null; return; }   // toggle
            wire = new WireEdit { fsm = fsm.GetInstanceID(), state = state, ev = ev, add = add };
        }

        static void WireEditor(UI ui, PlayMakerFSM fsm, string state, string ev)
        {
            var w = wire;
            if (w == null || w.fsm != fsm.GetInstanceID() || w.state != state || w.ev != ev) return;
            var hi = new Color(1f, 0.6f, 0.3f, 1f);
            string evName = w.add ? w.text.Trim() : ev;
            if (w.add)
            {
                ui.BeginRow();
                ui.Label("            event:", hi, 110);
                ui.TextField("wire-ev", ref w.text, 220);
                if (ui.Button("cancel", 70)) { wire = null; ui.EndRow(); return; }
                ui.EndRow();
                ui.Label("            pick an event this FSM knows, or type a new name (signals arrive as events, e.g. '>GoNow'):", UI.Dim);
                ButtonFlow(ui, FsmEdit.EventNames(fsm), "            ", n => { w.text = n; });
            }
            else
            {
                ui.BeginRow();
                ui.Label("            move exit '" + ev + "' to:", hi, ui.Width - 90);
                if (ui.Button("cancel", 70)) { wire = null; ui.EndRow(); return; }
                ui.EndRow();
            }
            if (string.IsNullOrEmpty(evName)) { ui.Label("            (choose an event first)", UI.Dim); return; }
            if (w.add) ui.Label("            on '" + evName + "' go to state:", hi);
            var names = new List<string>();
            foreach (var st in fsm.FsmStates) if (st != null) names.Add(st.Name);
            ButtonFlow(ui, names, "            ", n => { FsmEdit.Wire(fsm, state, evName, n, "inspector"); wire = null; });
        }

        // a wrapping row of buttons
        static void ButtonFlow(UI ui, List<string> items, string indent, Action<string> pick)
        {
            float x = 0, max = ui.Width - 20, lead = indent.Length * 6f;
            bool row = false;
            foreach (var it in items)
            {
                float bw = Mathf.Min(max - lead, 16f + it.Length * 7f);
                if (!row || x + bw > max) { if (row) ui.EndRow(); ui.BeginRow(); ui.Label("", null, lead); x = lead; row = true; }
                if (ui.Button(it, bw)) { pick(it); }
                x += bw + 4;
            }
            if (row) ui.EndRow();
        }

        static void VarRow(UI ui, PlayMakerFSM fsm, NamedVariable v, Inspector insp)
        {
            if (v == null) return;
            string key = fsm.GetInstanceID() + ":" + v.Name;
            ui.BeginRow();
            ui.Label("      " + v.Name, UI.Dim, 200);
            var fb = v as FsmBool; var ff = v as FsmFloat; var fi = v as FsmInt; var fs = v as FsmString; var fg = v as FsmGameObject;
            if (fb != null)
            {
                bool nb = ui.Toggle(fb.Value, fb.Value ? "true" : "false");
                if (nb != fb.Value) { fb.Value = nb; ModifiedPanel.NoteExternal(fsm.gameObject, "FSM var " + v.Name + " = " + nb); }
            }
            else if (ff != null || fi != null || fs != null)
            {
                string cur;
                if (!edits.TryGetValue(key, out cur)) cur = ff != null ? ff.Value.ToString(IC) : fi != null ? fi.Value.ToString(IC) : (fs.Value ?? "");
                string nv = cur;
                bool enter = ui.TextField("fv:" + key, ref nv, 160);
                if (nv != cur) edits[key] = nv;
                if (edits.ContainsKey(key) && (ui.Button("set", 40) || enter))
                {
                    try
                    {
                        if (ff != null) ff.Value = float.Parse(nv, IC);
                        else if (fi != null) fi.Value = int.Parse(nv, IC);
                        else fs.Value = nv;
                        edits.Remove(key);
                        ModifiedPanel.NoteExternal(fsm.gameObject, "FSM var " + v.Name + " = " + nv);
                    }
                    catch (Exception e) { DevLog.Write("fsm var " + v.Name + ": " + e.Message); }
                }
            }
            else if (fg != null)
            {
                var g = fg.Value;
                if (g != null) { if (ui.Item(g.name, UI.Accent)) { ui.EndRow(); insp.Select(g); return; } }
                else ui.Label("null");
            }
            else
            {
                object raw = null;
                try { raw = v.RawValue; } catch { }
                ui.Label(raw == null ? "null" : raw.ToString());
            }
            ui.EndRow();
        }
    }
}
