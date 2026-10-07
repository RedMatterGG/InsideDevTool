using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace InsideDev
{
    // Live editing of Playdead signal wiring into a PlayMaker FSM: connect any object's signal output to one of the
    // FSM's signal inputs (the events whose name starts with '>', e.g. '>EndNow'), or disconnect an existing one.
    // Uses the game's own SignalManager.CreateConnection, so a connection to an object that is still switched off
    // (its signal output not created yet) is made "waiting" and links itself when that object wakes up.
    // Recorded in History as action "sigWire", arg "+|<signalIn>|<signalOut>|<sender selector>" ('-' = disconnect),
    // so the Mods tab can keep it (re-applied whenever the FSM object loads).
    public static class SignalEdit
    {
        static FieldInfo fOutConns;
        // per FSM: the edits made this session, so "reset" can undo them (newest first)
        sealed class Change { public bool added; public GameObject sender; public string outName, inName; }
        static readonly Dictionary<int, List<Change>> changes = new Dictionary<int, List<Change>>();
        static bool resetting;
        static void Note(PlayMakerFSM f, bool added, GameObject sender, string outName, string inName)
        {
            if (resetting || f == null) return;
            List<Change> l; if (!changes.TryGetValue(f.GetInstanceID(), out l)) changes[f.GetInstanceID()] = l = new List<Change>();
            l.Add(new Change { added = added, sender = sender, outName = outName, inName = inName });
        }
        public static bool IsEdited(PlayMakerFSM f) { List<Change> l; return f != null && changes.TryGetValue(f.GetInstanceID(), out l) && l.Count > 0; }

        // undo this session's signal edits on the FSM, newest first
        public static string Reset(PlayMakerFSM f)
        {
            List<Change> l; if (f == null || !changes.TryGetValue(f.GetInstanceID(), out l) || l.Count == 0) return "no signal edits";
            resetting = true; int n = 0;
            try
            {
                for (int i = l.Count - 1; i >= 0; i--)
                {
                    var c = l[i]; if (c.sender == null) continue;
                    if (c.added) { foreach (var x in Incoming(f)) if (x.signalOutGameObject == c.sender && x.signalOutName == c.outName && x.signalInName == c.inName) { Disconnect(f, x, "reset"); n++; break; } }
                    else { Connect(f, c.sender, c.outName, c.inName, "reset"); n++; }
                }
            }
            finally { resetting = false; l.Clear(); }
            return "signal wiring reset (" + n + " change(s) undone)";
        }
        static SignalManager SM { get { try { return PersistentBehaviour<SignalManager>.instance; } catch { return null; } } }

        // the FSM's signal inputs: '>' events it has exits for (plus any already connected)
        public static List<string> Inputs(PlayMakerFSM f)
        {
            var l = new List<string>();
            if (f == null) return l;
            var g = f.FsmGlobalTransitions;
            if (g != null) foreach (var t in g) if (t != null && t.EventName != null && t.EventName.StartsWith(">") && !l.Contains(t.EventName)) l.Add(t.EventName);
            if (f.FsmStates != null) foreach (var st in f.FsmStates) if (st != null && st.Transitions != null) foreach (var t in st.Transitions) if (t != null && t.EventName != null && t.EventName.StartsWith(">") && !l.Contains(t.EventName)) l.Add(t.EventName);
            foreach (var c in Incoming(f)) if (!l.Contains(c.signalInName)) l.Add(c.signalInName);
            return l;
        }

        // active connections that end in this FSM
        public static List<SignalConnection> Incoming(PlayMakerFSM f)
        {
            var l = new List<SignalConnection>();
            var sm = SM; if (sm == null || sm.connections == null || f == null) return l;
            var go = f.gameObject;
            foreach (var c in sm.connections)
                if (c != null && c.isActive && c.signalInGameObject == go && (c.fsmIn == null || c.fsmIn == f.Fsm)) l.Add(c);
            return l;
        }

        // signal outputs that exist right now (objects that have woken up at least once), nearest first
        public static List<SignalOut> Outputs(Vector3 near, string filter, int max)
        {
            var l = new List<SignalOut>();
            var sm = SM; if (sm == null || sm.signalOuts == null) return l;
            string f = (filter ?? "").Trim().ToLowerInvariant();
            foreach (var s in sm.signalOuts)
            {
                if (s == null || !s.isActive || s.gameObject == null) continue;
                if (f.Length > 0 && (s.gameObject.name + "." + s.debugName).ToLowerInvariant().IndexOf(f, StringComparison.Ordinal) < 0) continue;
                l.Add(s);
            }
            l.Sort((a, b) => (a.gameObject.transform.position - near).sqrMagnitude.CompareTo((b.gameObject.transform.position - near).sqrMagnitude));
            if (l.Count > max) l.RemoveRange(max, l.Count - max);
            return l;
        }

        public static string Connect(PlayMakerFSM f, GameObject sender, string outName, string inName, string source)
        {
            var sm = SM; if (sm == null) return "no SignalManager";
            if (f == null || sender == null) return "missing FSM or sender";
            if (string.IsNullOrEmpty(outName) || string.IsNullOrEmpty(inName)) return "missing signal name";
            foreach (var c in Incoming(f)) if (c.signalOutGameObject == sender && c.signalOutName == outName && c.signalInName == inName) return "already connected: " + sender.name + "." + outName + " -> " + inName;
            var con = sm.CreateConnection(sender, outName, f.gameObject, inName, f);
            if (con == null) return "the game refused the connection (connection table full?)";
            string msg = sender.name + "." + outName + " -> " + f.gameObject.name + "/" + f.FsmName + "." + inName + (con.isConnected ? "" : "  (waiting: " + sender.name + " has not created that signal yet - it links when the object wakes up)");
            DevLog.Write("[signal] connected " + msg);
            Note(f, true, sender, outName, inName);
            ChangeRecorder.Action(f.gameObject, "sigWire", f.FsmName, "+|" + inName + "|" + outName + "|" + ObjectSelector.From(sender), source);
            return "connected " + msg;
        }

        public static string Disconnect(PlayMakerFSM f, SignalConnection c, string source)
        {
            var sm = SM; if (sm == null || c == null || !c.isActive) return "not connected";
            var sender = c.signalOutGameObject; string outName = c.signalOutName, inName = c.signalInName;
            // SignalOut.Signal() raises every connection in its list whether active or not: take it out of that list first
            if (c.signalOut != null)
            {
                try
                {
                    if (fOutConns == null) fOutConns = typeof(SignalOut).GetField("connections", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var list = fOutConns != null ? fOutConns.GetValue(c.signalOut) as FastList<SignalConnection> : null;
                    if (list != null) list.Remove(c);
                }
                catch (Exception e) { DevLog.Error("signal disconnect", e); }
            }
            sm.HandleSignalConnectionDestroyed(c);
            string msg = (sender != null ? sender.name : "?") + "." + outName + " -/-> " + (f != null ? f.gameObject.name + "/" + f.FsmName : "?") + "." + inName;
            DevLog.Write("[signal] disconnected " + msg);
            Note(f, false, sender, outName, inName);
            if (f != null && sender != null) ChangeRecorder.Action(f.gameObject, "sigWire", f.FsmName, "-|" + inName + "|" + outName + "|" + ObjectSelector.From(sender), source);
            return "disconnected " + msg;
        }

        // mod / bridge form
        public static string Apply(PlayMakerFSM f, string arg, string source)
        {
            var p = (arg ?? "").Split(new[] { '|' }, 4);
            if (p.Length < 4) return "bad signal wiring '" + arg + "' (want +|in|out|sender)";
            var senders = Mods.ResolveAll(p[3]);
            if (senders.Count == 0) return "sender not loaded: " + p[3];
            if (p[0] == "+") return Connect(f, senders[0], p[2], p[1], source);
            foreach (var c in Incoming(f)) if (c.signalOutGameObject == senders[0] && c.signalOutName == p[2] && c.signalInName == p[1]) return Disconnect(f, c, source);
            return "no such connection";
        }

        public static string Label(string arg)
        {
            var p = (arg ?? "").Split(new[] { '|' }, 4);
            if (p.Length < 4) return arg;
            string who = p[3]; int at = who.IndexOf(" @"); if (at > 0) who = who.Substring(0, at); who = who.Substring(who.LastIndexOf('/') + 1);
            return (p[0] == "+" ? "connect " : "disconnect ") + who + "." + p[2] + " -> " + p[1];
        }

        public static string Describe(PlayMakerFSM f)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var i in Inputs(f))
            {
                sb.Append("  in ").Append(i).Append(":");
                int n = 0;
                foreach (var c in Incoming(f)) if (c.signalInName == i) { n++; sb.Append("\n     <- ").Append(c.signalOutGameObject != null ? Inspector.PathOf(c.signalOutGameObject.transform) : "?").Append('.').Append(c.signalOutName).Append(c.isConnected ? "" : "  (waiting)"); }
                if (n == 0) sb.Append(" nothing connected");
                sb.Append('\n');
            }
            return sb.Length == 0 ? "  no signal inputs ('>' events)\n" : sb.ToString();
        }
    }
}
