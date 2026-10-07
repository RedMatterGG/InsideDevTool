using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace InsideDev
{
    // Phase 10: canonical audio identities.
    //   Definition - one Wwise event name (listed once, however many objects use it)
    //   Binding    - a place in the scene that posts it: a component field (AudioEventSimple / AudioEventWithCallback /
    //                string "...event..." field) or a PlayMaker PostAKEvent action; provenance FIELD / FSM
    //   Observed   - (object, event) pairs actually posted while you play, from the AudioSpy hook; provenance OBSERVED
    //   Instance   - a playing id returned by a post (auditions are tracked so they can be stopped)
    // Actions (each click = exactly one post):
    //   Audition          - posts the event on the editor's own neutral emitter at the listener: hear it, game logic untouched
    //   Post from binding - posts on the binding's object (positioned like the game would); still no game logic
    //   Trigger original  - runs the game's own path: forces the FSM state that posts it / fires the owner's trigger
    //   Override          - makes a binding post a different event (reversible; original kept)
    public static class SoundLibrary
    {
        public enum Prov { Field, Fsm, Observed }

        public sealed class Binding
        {
            public Prov prov;
            public GameObject go;
            public Component comp;           // component or PlayMakerFSM
            public string where;             // "ForcePushManager.audioCoverDeath" / "FSM 'x' > State 2 > PostAKEvent"
            public object holder;            // AudioEventSimple / AudioEventWithCallback / PostAKEvent / component (string field)
            public FieldInfo field;          // string field (for plain string bindings)
            public string fsmState;
            public string original;          // event name before override (null = not overridden)
            public GameObject postTarget;    // object the sound is actually posted on (FSM PostAKEvent target), if different
            public int observedPosts;
            public float lastPost = -1;
        }

        public sealed class Def
        {
            public string name;
            public readonly List<Binding> bindings = new List<Binding>();
            public int posts, auditions;
            public float lastPost = -1;
            public string lastSource = "";
            public int UsedBy { get { var s = new HashSet<int>(); foreach (var b in bindings) if (b.go != null) s.Add(b.go.GetInstanceID()); return s.Count; } }
        }

        public static readonly Dictionary<string, Def> defs = new Dictionary<string, Def>(StringComparer.Ordinal);
        static readonly Dictionary<Type, FieldInfo[]> typeFields = new Dictionary<Type, FieldInfo[]>();
        static int scannedPass = -1, cursor;
        static bool scanning;
        public static float lastScanMs; static readonly System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
        public static float budgetMs = 1f;
        public static int scannedObjects;

        static readonly List<uint> auditionIds = new List<uint>();
        static GameObject emitter;

        public static Def Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Def d;
            if (!defs.TryGetValue(name, out d)) defs[name] = d = new Def { name = name };
            return d;
        }

        // ---------------------------------------------------------------- scanning (incremental, frame budgeted)
        public static void Tick()
        {
            if (!ObjectDatabase.Ready) return;
            if (!scanning && ObjectDatabase.completedPasses != scannedPass)
            {
                scannedPass = ObjectDatabase.completedPasses;
                scanning = true; cursor = 0;
                // drop static bindings of destroyed objects; observed ones stay as history
                foreach (var d in defs.Values) d.bindings.RemoveAll(b => b.go == null || (b.prov != Prov.Observed && b.comp == null));
            }
            if (!scanning) return;
            sw.Reset(); sw.Start();
            var all = ObjectDatabase.all;
            while (cursor < all.Count && sw.Elapsed.TotalMilliseconds < budgetMs)
            {
                var r = all[cursor++];
                if (r.go == null) continue;
                try { ScanObject(r.go); } catch { }
                scannedObjects = cursor;
            }
            if (cursor >= all.Count) { scanning = false; lastScanMs = (float)sw.Elapsed.TotalMilliseconds; }
        }

        public static FieldInfo[] AudioFields(Type t)
        {
            FieldInfo[] res;
            if (typeFields.TryGetValue(t, out res)) return res;
            var l = new List<FieldInfo>();
            foreach (var f in ValueDump.Fields(t, false))
            {
                var ft = f.FieldType;
                if (ft == typeof(AudioEventSimple) || ft == typeof(AudioEventWithCallback) ||
                    ft == typeof(AudioEventSimple[]) || ft == typeof(List<AudioEventSimple>) ||
                    (ft == typeof(string) && f.Name.IndexOf("event", StringComparison.OrdinalIgnoreCase) >= 0 && ValueDump.IsSerialized(f)))
                    l.Add(f);
            }
            typeFields[t] = res = l.ToArray();
            return res;
        }

        static void ScanObject(GameObject go)
        {
            foreach (var c in go.GetComponents<MonoBehaviour>())
            {
                if (c == null) continue;
                var fsm = c as PlayMakerFSM;
                if (fsm != null) { ScanFsm(fsm); continue; }
                foreach (var f in AudioFields(c.GetType()))
                {
                    object v; try { v = f.GetValue(c); } catch { continue; }
                    if (v == null) continue;
                    var se = v as AudioEventSimple;
                    if (se != null) { Bind(se.eventName, Prov.Field, go, c, c.GetType().Name + "." + f.Name, se, null, null); continue; }
                    var we = v as AudioEventWithCallback;
                    if (we != null) { Bind(we.eventName, Prov.Field, go, c, c.GetType().Name + "." + f.Name, we, null, null); continue; }
                    var arr = v as System.Collections.IEnumerable;
                    if (!(v is string) && arr != null)
                    {
                        int i = 0;
                        foreach (var x in arr) { var sx = x as AudioEventSimple; if (sx != null) Bind(sx.eventName, Prov.Field, go, c, c.GetType().Name + "." + f.Name + "[" + i + "]", sx, null, null); i++; }
                        continue;
                    }
                    var s = v as string;
                    if (!string.IsNullOrEmpty(s) && s.IndexOf(' ') < 0 && s.Length > 3) Bind(s, Prov.Field, go, c, c.GetType().Name + "." + f.Name, c, f, null);
                }
            }
        }

        static void ScanFsm(PlayMakerFSM fsm)
        {
            FsmState[] states = null;
            try { states = fsm.FsmStates; } catch { }
            if (states == null) return;
            foreach (var st in states)
            {
                if (st == null || st.Actions == null) continue;
                foreach (var a in st.Actions)
                {
                    var p = a as PostAKEvent;
                    if (p == null || p.eventID == null || string.IsNullOrEmpty(p.eventID.Value)) continue;
                    string name = p.eventID.Value;
                    if (p.useAudioProperties) { var ap = fsm.GetComponent<AudioProperties>(); if (ap != null) name = ap.properties.prefix + "_" + name; }
                    Bind(name, Prov.Fsm, fsm.gameObject, fsm, "FSM '" + fsm.FsmName + "' > " + st.Name + " > PostAKEvent", p, null, st.Name);
                    try
                    {
                        GameObject custom = null;
                        try { custom = fsm.Fsm.GetOwnerDefaultTarget(p.gameObject); } catch { }
                        var tgt = AudioKeyObject.GetObject(p.keyObject, fsm.gameObject, custom);
                        if (tgt != null && tgt != fsm.gameObject)
                            foreach (var b in Get(name).bindings) if (b.comp == fsm && b.fsmState == st.Name) b.postTarget = tgt;
                    }
                    catch { }
                }
            }
        }

        static void Bind(string ev, Prov prov, GameObject go, Component comp, string where, object holder, FieldInfo field, string state)
        {
            if (string.IsNullOrEmpty(ev)) return;
            var d = Get(ev);
            foreach (var b in d.bindings) if (b.comp == comp && b.where == where) return;
            d.bindings.Add(new Binding { prov = prov, go = go, comp = comp, where = where, holder = holder, field = field, fsmState = state });
        }

        // ---------------------------------------------------------------- observed posts (from AudioSpy)
        public static void OnPosted(string ev, GameObject source)
        {
            if (string.IsNullOrEmpty(ev)) return;
            var d = Get(ev);
            float now = Time.realtimeSinceStartup;
            d.posts++; d.lastPost = now; d.lastSource = source != null ? source.name : "global";
            if (source == null || source == emitter) return;
            foreach (var b in d.bindings)
                if (b.go == source || b.postTarget == source) { b.observedPosts++; b.lastPost = now; return; }
            bool global = source.name == "Global" || source.name == "Protagonist" || (source.transform.parent != null && source.transform.parent.name == "Persistent");
            d.bindings.Add(new Binding { prov = Prov.Observed, go = source, where = global ? "played through the game's shared emitter '" + source.name + "' (the code that posts it is one of the FIELD bindings)" : "posted at runtime (not found in fields/FSMs)", observedPosts = 1, lastPost = now });
        }

        // ---------------------------------------------------------------- actions (one call = one post)
        static AkGameObj Emitter()
        {
            if (emitter == null)
            {
                emitter = new GameObject("InsideDev.AuditionEmitter");
                UnityEngine.Object.DontDestroyOnLoad(emitter);
                emitter.hideFlags = HideFlags.HideAndDontSave;
                emitter.AddComponent<AkGameObj>();
            }
            var cam = G.Cam();
            if (cam != null) emitter.transform.position = cam.transform.position;
            return emitter.GetComponent<AkGameObj>();
        }

        public static string Audition(string ev)
        {
            try
            {
                var ak = Emitter();
                uint id = SoundEngine.PostEventByIDFast(SoundEngine.Name2ID(ev), ev, ak);
                Get(ev).auditions++;
                if (id != 0) auditionIds.Add(id);
                return id != 0 ? "audition '" + ev + "' playing id " + id : "audition '" + ev + "' FAILED (event not in a loaded bank)";
            }
            catch (Exception e) { return "audition failed: " + e.Message; }
        }

        public static void StopAuditions()
        {
            foreach (var id in auditionIds) { try { SoundEngine.StopPlayingID(id, 0.1f); } catch { } }
            auditionIds.Clear();
        }

        public static string PostFrom(Binding b, string ev)
        {
            if (b.go == null) return "object gone";
            var ak = b.go.GetComponent<AkGameObj>() ?? AudioKeyObject.AkGameObjs.global;
            uint id = SoundEngine.PostEventByIDFast(SoundEngine.Name2ID(ev), ev, ak);
            return "posted '" + ev + "' on " + (ak != null ? ak.gameObject.name : "global") + (id == 0 ? " FAILED" : " id " + id);
        }

        public static bool CanTriggerOriginal(Binding b)
        {
            if (b.go == null) return false;
            if (b.prov == Prov.Fsm && b.fsmState != null) return true;
            return b.go.GetComponent<IsTriggeredByProbe>() != null;
        }

        public static string TriggerOriginal(Binding b)
        {
            if (b.prov == Prov.Fsm && b.comp is PlayMakerFSM)
            {
                var fsm = (PlayMakerFSM)b.comp;
                fsm.SetState(b.fsmState);
                return "forced " + fsm.gameObject.name + " / " + fsm.FsmName + " into state '" + b.fsmState + "' (runs all its actions, not only the sound)";
            }
            var t = b.go.GetComponent<IsTriggeredByProbe>();
            if (t != null && t.enterSignal != null) { t.enterSignal.Signal(); return "fired " + b.go.name + ".enterSignal (as if the boy entered)"; }
            return "no game path known for this binding";
        }

        // make a binding post another event; remembers the original for Revert
        public static string Override(Binding b, string newEvent)
        {
            string cur = CurrentName(b);
            if (b.original == null) b.original = cur;
            if (!SetName(b, newEvent)) return "this binding can't be overridden";
            DevLog.Write("[audio] override " + b.go.name + " " + b.where + ": '" + cur + "' -> '" + newEvent + "'");
            return "now posts '" + newEvent + "' (was '" + b.original + "')";
        }

        public static string Revert(Binding b)
        {
            if (b.original == null) return "not overridden";
            SetName(b, b.original);
            string o = b.original; b.original = null;
            return "reverted to '" + o + "'";
        }

        public static string CurrentName(Binding b)
        {
            var se = b.holder as AudioEventSimple; if (se != null) return se.eventName;
            var we = b.holder as AudioEventWithCallback; if (we != null) return we.eventName;
            var p = b.holder as PostAKEvent; if (p != null) return p.eventID.Value;
            if (b.field != null) return b.field.GetValue(b.holder) as string;
            return null;
        }

        static readonly BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static bool SetName(Binding b, string ev)
        {
            var se = b.holder as AudioEventSimple;
            if (se != null) { se.eventName = ev; se.eventID = SoundEngine.Name2ID(ev); se.isInitialized = true; return true; }
            var we = b.holder as AudioEventWithCallback;
            if (we != null)
            {
                we.eventName = ev;
                var fid = typeof(AudioEventWithCallback).GetField("eventID", BF); if (fid != null) fid.SetValue(we, SoundEngine.Name2ID(ev));
                return true;
            }
            var p = b.holder as PostAKEvent;
            if (p != null)
            {
                p.eventID.Value = ev; p.useAudioProperties = false;
                var li = typeof(PostAKEvent).GetField("isLateInitialized", BF); if (li != null) li.SetValue(p, false);   // rebuild its cached event on next enter
                return true;
            }
            if (b.field != null) { b.field.SetValue(b.holder, ev); return true; }
            return false;
        }

        public static string Status()
        {
            int bindings = 0, nf = 0, ns = 0, no = 0;
            foreach (var d in defs.Values) foreach (var b in d.bindings) { bindings++; if (b.prov == Prov.Field) nf++; else if (b.prov == Prov.Fsm) ns++; else no++; }
            return defs.Count + " sound definitions, " + bindings + " bindings (" + nf + " field, " + ns + " state machine, " + no + " observed), scanned " + scannedObjects + " objects" + (scanning ? " (scanning " + cursor + "/" + ObjectDatabase.all.Count + ")" : "");
        }
    }
}
