using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace InsideDev
{
    // Live rewiring of any PlayMaker FSM: change where a transition leads, add a new "on <event> -> <state>" exit to a
    // state (or a global one), or remove an exit. Works on every PlayMakerFSM in the game.
    //   state "*" = the FSM's global transitions (taken from any state).
    // The FSM's original wiring is remembered on the first edit, so "reset wiring" puts it back. Every edit is logged
    // in History (ChangeRecorder action "fsmWire"), so it can be recorded into a mod that re-applies it whenever the
    // object is loaded (op action "fsmWire", arg "<state>|<event>|<toState>"; an empty toState removes the exit).
    public static class FsmEdit
    {
        sealed class Orig { public Dictionary<string, FsmTransition[]> states = new Dictionary<string, FsmTransition[]>(); public FsmTransition[] globals; }
        static readonly Dictionary<int, Orig> originals = new Dictionary<int, Orig>();

        static FsmTransition[] Copy(FsmTransition[] a)
        {
            if (a == null) return new FsmTransition[0];
            var r = new FsmTransition[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = a[i] != null ? new FsmTransition(a[i]) : null;
            return r;
        }

        static void Remember(PlayMakerFSM f)
        {
            int id = f.GetInstanceID();
            if (originals.ContainsKey(id)) return;
            var o = new Orig { globals = Copy(f.FsmGlobalTransitions) };
            foreach (var st in f.FsmStates) if (st != null && !o.states.ContainsKey(st.Name)) o.states[st.Name] = Copy(st.Transitions);
            originals[id] = o;
        }

        public static bool IsEdited(PlayMakerFSM f) { return f != null && originals.ContainsKey(f.GetInstanceID()); }

        // every event name this FSM knows (declared events + names used in its transitions), for pickers
        public static List<string> EventNames(PlayMakerFSM f)
        {
            var l = new List<string>();
            try { foreach (var e in f.FsmEvents) if (e != null && !string.IsNullOrEmpty(e.Name) && !l.Contains(e.Name)) l.Add(e.Name); } catch { }
            foreach (var st in f.FsmStates) if (st != null && st.Transitions != null) foreach (var t in st.Transitions) if (t != null && !string.IsNullOrEmpty(t.EventName) && !l.Contains(t.EventName)) l.Add(t.EventName);
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l;
        }

        // the event object transitions must hold: PlayMaker matches incoming events by reference
        public static FsmEvent EventFor(PlayMakerFSM f, string name)
        {
            try { foreach (var e in f.FsmEvents) if (e != null && e.Name == name) return e; } catch { }
            foreach (var st in f.FsmStates) if (st != null && st.Transitions != null) foreach (var t in st.Transitions) if (t != null && t.EventName == name && t.FsmEvent != null) return t.FsmEvent;
            var ev = FsmEvent.GetFsmEvent(name);
            try { var list = new List<FsmEvent>(f.Fsm.Events ?? new FsmEvent[0]); list.Add(ev); f.Fsm.Events = list.ToArray(); } catch { }
            return ev;
        }

        static FsmState State(PlayMakerFSM f, string name) { foreach (var st in f.FsmStates) if (st != null && st.Name == name) return st; return null; }
        static string Label(PlayMakerFSM f) { return f.gameObject.name + "/" + f.FsmName; }

        // add the exit, or move it if the state already has one for this event
        public static string Wire(PlayMakerFSM f, string state, string ev, string toState, string source)
        {
            if (f == null) return "no FSM";
            if (string.IsNullOrEmpty(ev)) return "no event name";
            if (State(f, toState) == null) return "FSM " + Label(f) + " has no state '" + toState + "'";
            bool global = state == "*";
            FsmState st = null;
            if (!global) { st = State(f, state); if (st == null) return "FSM " + Label(f) + " has no state '" + state + "'"; }
            Remember(f);
            var arr = global ? f.FsmGlobalTransitions : st.Transitions;
            var list = new List<FsmTransition>(arr ?? new FsmTransition[0]);
            string what;
            var existing = list.Find(t => t != null && t.EventName == ev);
            if (existing != null) { what = "moved '" + ev + "' from [" + existing.ToState + "] to [" + toState + "]"; existing.ToState = toState; }
            else
            {
                var t = new FsmTransition(); t.FsmEvent = EventFor(f, ev); t.ToState = toState;
                list.Add(t); what = "added on '" + ev + "' -> [" + toState + "]";
            }
            if (global) f.Fsm.GlobalTransitions = list.ToArray(); else st.Transitions = list.ToArray();
            string msg = Label(f) + (global ? " global" : " [" + state + "]") + ": " + what;
            DevLog.Write("[fsm] " + msg);
            ChangeRecorder.Action(f.gameObject, "fsmWire", f.FsmName, state + "|" + ev + "|" + toState, source);
            return msg;
        }

        public static string Unwire(PlayMakerFSM f, string state, string ev, string source)
        {
            if (f == null) return "no FSM";
            bool global = state == "*";
            FsmState st = null;
            if (!global) { st = State(f, state); if (st == null) return "FSM " + Label(f) + " has no state '" + state + "'"; }
            var arr = global ? f.FsmGlobalTransitions : st.Transitions;
            if (arr == null) return "nothing to remove";
            var list = new List<FsmTransition>(arr);
            int n = list.RemoveAll(t => t != null && t.EventName == ev);
            if (n == 0) return (global ? "global" : "[" + state + "]") + " has no exit on '" + ev + "'";
            Remember(f);
            if (global) f.Fsm.GlobalTransitions = list.ToArray(); else st.Transitions = list.ToArray();
            string msg = Label(f) + (global ? " global" : " [" + state + "]") + ": removed exit on '" + ev + "'";
            DevLog.Write("[fsm] " + msg);
            ChangeRecorder.Action(f.gameObject, "fsmWire", f.FsmName, state + "|" + ev + "|", source);
            return msg;
        }

        public static string Reset(PlayMakerFSM f)
        {
            Orig o; if (f == null || !originals.TryGetValue(f.GetInstanceID(), out o)) return "not edited";
            f.Fsm.GlobalTransitions = Copy(o.globals);
            foreach (var st in f.FsmStates) { FsmTransition[] a; if (st != null && o.states.TryGetValue(st.Name, out a)) st.Transitions = Copy(a); }
            originals.Remove(f.GetInstanceID());
            DevLog.Write("[fsm] " + Label(f) + ": wiring reset to the original");
            return Label(f) + ": wiring reset to the original";
        }

        // mod / bridge form: "<state>|<event>|<toState>" (empty toState = remove)
        public static string Apply(PlayMakerFSM f, string arg, string source)
        {
            var p = (arg ?? "").Split('|');
            if (p.Length < 3) return "bad wiring '" + arg + "' (want state|event|toState)";
            return p[2].Length == 0 ? Unwire(f, p[0], p[1], source) : Wire(f, p[0], p[1], p[2], source);
        }
    }
}
