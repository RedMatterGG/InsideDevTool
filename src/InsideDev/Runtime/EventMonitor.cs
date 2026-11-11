using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HutongGames.PlayMaker;
using UnityEngine;

namespace InsideDev
{
    // Phase 8: live event history.
    //   signals - every active Playdead SignalOut gets one extra "tap" connection at the front of its connection list,
    //             so the tap runs whenever the game fires the signal (cause is logged before its effects).
    //             Taps are ordinary SignalConnections with only a SignalIn action; removing them restores the lists.
    //   FSMs    - PlayMaker state changes are detected by polling ActiveStateName of the known state machines.
    //   graph   - fired (sender, receiver) pairs are remembered so the graph can animate those edges (OBSERVED).
    // Nothing here changes what the game does; it only observes.
    //
    // The monitor sleeps (no taps in the game's lists, no polling, nothing recorded) until somebody wants it:
    //   - editor.cfg events.monitor = 1 ("events always"): always on
    //   - Background.Want(Background.Events, seconds) or EventMonitor.Want(seconds): on for that long
    //   - while the editor panel is open, and while the tool is in use (Background.InUse) and something read the ring
    //     (Count, Status, Dump, FiredAge, LastAge, WatchAnimations, WatchExtra) in the last seconds
    // enabled = false ("events off", the monitor toggle of the Events panel) keeps it off for this session whatever
    // asks; enabled = true ("events on") lets it run again. When it wakes up it hooks at once and asks the object
    // database for a pass; events from the time it slept are not in the ring.
    public static class EventMonitor
    {
        public struct Ev
        {
            public float t; public int frame; public char kind;   // 'S' signal, 'F' fsm transition, 'A' sound posted, 'N' animation clip started
            public int goId; public string text;
        }

        public const int Capacity = 4000;
        public const string ConfigKey = "events.monitor";
        const float ReadKeepsAwake = 10f, WakeForCommand = 30f;

        static readonly Ev[] ring = new Ev[Capacity];
        static int head, count;
        public static int Count { get { NoteRead(); return count; } }
        // events recorded since the game started (a field since 0.10.0: other modules read it)
        public static long total;
        public static bool paused;

        // ---------------------------------------------------------------- who wants it
        // False = switched off for this session ("events off", the panel toggle, a failure). A field since 0.10.0:
        // other modules read and write it, the monitor reads it every frame.
        public static bool enabled = true;
        static bool keyOn, keyRead;
        static float keyReadAt = -100f;
        static bool awake;               // taps installed and polling
        static long readAt = long.MinValue;
        static float wokeAt = -100f, sleptAt = -100f;
        public static int wakeUps;

        // taps installed and polling right now
        public static bool Awake { get { return awake; } }
        public static bool Always { get { ReadKey(); return keyOn && enabled; } }

        // on for good: editor.cfg events.monitor = 1 (or back to 0)
        public static void SetAlways(bool on)
        {
            keyOn = on; keyRead = true;
            if (on) enabled = true;
            try { EditorState.Set(ConfigKey, on); } catch { }
        }

        // main thread: keep the monitor running for the next seconds (recordings and tools that read the ring later)
        public static void Want(float seconds) { Background.Want(Background.Events, seconds); }

        // back to the default: on while somebody wants it, off otherwise
        public static void Auto() { enabled = true; SetAlways(false); }

        static void NoteRead() { Interlocked.Exchange(ref readAt, Stopwatch.GetTimestamp()); }

        static bool ReadLately()
        {
            long t = Interlocked.Read(ref readAt);
            return t != long.MinValue && Stopwatch.GetTimestamp() - t < (long)(ReadKeepsAwake * Stopwatch.Frequency);
        }

        static void ReadKey()
        {
            float now = Time.realtimeSinceStartup;
            if (keyRead && now - keyReadAt < 1f) return;
            keyRead = true; keyReadAt = now;
            try { keyOn = EditorState.Get(ConfigKey, false); } catch { }
        }

        static bool ShouldRun()
        {
            if (!enabled) return false;
            ReadKey();
            if (keyOn || Background.Wanted(Background.Events) || Perf.editorOpen) return true;
            return ReadLately() && Background.InUse;
        }
        static float nextSelfWant;

        static readonly Dictionary<SignalOut, SignalConnection> taps = new Dictionary<SignalOut, SignalConnection>();
        static readonly Dictionary<long, float> firedPairs = new Dictionary<long, float>();
        static FieldInfo fConns;
        static int tappedPass = -1, fsmPass = -1, streamSeen = -1;
        static float nextTapScan, nextDbDemand;
        public static int TapCount { get { return taps.Count; } }
        public static int FsmCount { get { return fsmN; } }

        public static Ev Get(int i)   // 0 = newest
        {
            int idx = (head - 1 - i + Capacity * 2) % Capacity;
            return ring[idx];
        }

        public static void Clear() { head = count = 0; firedPairs.Clear(); lastByGoKind.Clear(); }

        static readonly Dictionary<long, float> lastByGoKind = new Dictionary<long, float>();
        // seconds since this object last raised an event of this kind
        public static float LastAge(int goId, char kind)
        {
            NoteRead();
            float t; return lastByGoKind.TryGetValue(((long)goId << 8) | kind, out t) ? Time.realtimeSinceStartup - t : 1e9f;
        }
        public static string KindName(char k) { return k == 'S' ? "SIGNAL " : k == 'F' ? "FSM    " : k == 'A' ? "SOUND  " : k == 'N' ? "ANIM   " : "?      "; }
        static string KindWord(char k) { return k == 'S' ? "signal" : k == 'F' ? "fsm" : k == 'A' ? "sound" : k == 'N' ? "anim" : "other"; }

        // sound posts (from the managed SoundEngine wrapper)
        public static void AudioPost(GameObject go, string name)
        {
            if (!awake || paused || go == null) return;
            Add('A', go.GetInstanceID(), go.name + "  posts  " + name);
        }

        // animation clips starting on watched objects (graph nodes + selection): polled, no hooks
        static readonly Dictionary<int, string> animPlaying = new Dictionary<int, string>();
        static readonly List<GameObject> animWatch = new List<GameObject>(), animWatchExtra = new List<GameObject>();
        // objects below the Inspector selection (ACTIVITY section)
        public static void WatchExtra(IEnumerable<GameObject> gos)
        {
            NoteRead();
            animWatchExtra.Clear();
            foreach (var g in gos) if (g != null && (g.GetComponent<Animation>() != null || g.GetComponent<Animator>() != null) && animWatchExtra.Count < 200) animWatchExtra.Add(g);
        }
        static float nextAnimPoll;
        public static void WatchAnimations(IEnumerable<GameObject> gos)
        {
            NoteRead();
            animWatch.Clear();
            foreach (var g in gos) if (g != null && (g.GetComponent<Animation>() != null || g.GetComponent<Animator>() != null) && animWatch.Count < 200) animWatch.Add(g);
        }
        static void PollAnimations()
        {
            float now = Time.realtimeSinceStartup; if (now < nextAnimPoll) return; nextAnimPoll = now + 0.1f;
            var sel = Selection.Current;
            int nw = animWatch.Count;
            for (int i = -1; i < nw + animWatchExtra.Count; i++)
            {
                var g = i < 0 ? sel : i < nw ? animWatch[i] : animWatchExtra[i - nw];
                if (g == null || !g.activeInHierarchy) continue;
                string playing = null;
                var an = g.GetComponent<Animation>();
                if (an != null && an.enabled && an.isPlaying && AnimSafe.CanWalkStates(an))
                    foreach (AnimationState st in an) if (st != null && an.IsPlaying(st.name)) { playing = st.name; break; }
                if (playing == null)
                {
                    var am = g.GetComponent<Animator>();
                    if (am != null && am.enabled && am.runtimeAnimatorController != null)
                        try { var ci = am.GetCurrentAnimatorClipInfo(0); if (ci != null && ci.Length > 0 && ci[0].clip != null) playing = ci[0].clip.name; } catch { }
                }
                int id = g.GetInstanceID(); string was;
                animPlaying.TryGetValue(id, out was);
                if (playing != null && playing != was) Add('N', id, g.name + "  plays clip  " + playing);
                animPlaying[id] = playing;
            }
        }

        static void Add(char kind, int goId, string text)
        {
            if (paused) return;
            float now = Time.realtimeSinceStartup;
            lastByGoKind[((long)goId << 8) | kind] = now;
            ring[head] = new Ev { t = now, frame = Time.frameCount, kind = kind, goId = goId, text = text };
            head = (head + 1) % Capacity;
            if (count < Capacity) count++;
            total++;
        }

        static long Key(int a, int b) { return ((long)a << 32) ^ (uint)b; }

        // seconds since the (src -> dst) signal pair last fired; large if never
        public static float FiredAge(int srcGo, int dstGo)
        {
            NoteRead();
            float t;
            return firedPairs.TryGetValue(Key(srcGo, dstGo), out t) ? Time.realtimeSinceStartup - t : 1e9f;
        }

        public static void Tick()
        {
            if (!ShouldRun()) { if (awake) Sleep(); return; }
            try
            {
                float now = Time.realtimeSinceStartup;
                if (!awake) WakeUp(now);
                if (keyOn && now >= nextSelfWant) { nextSelfWant = now + 1f; Background.Want(Background.Events, 2f); }
                // the state machine list comes from the object database: one pass after streaming changes and one
                // a minute for objects the game creates at run time
                if (Levels.Changed(ref streamSeen) || now >= nextDbDemand) { nextDbDemand = now + 60f; ObjectDatabase.DemandQuiet(3f); }
                if (ObjectDatabase.completedPasses != tappedPass || now > nextTapScan)
                {
                    tappedPass = ObjectDatabase.completedPasses;
                    nextTapScan = now + 3f;
                    Hook();
                }
                PollFsms();
                PollAnimations();
            }
            catch (Exception e) { DevLog.Error("event monitor", e); enabled = false; Sleep(); }
        }

        static void WakeUp(float now)
        {
            awake = true; wokeAt = now; wakeUps++;
            tappedPass = -1; fsmPass = -1; nextTapScan = 0; nextDbDemand = 0;
            streamSeen = Levels.StreamVersion;
            // old states would report a transition for everything that changed while nobody looked
            fsmN = 0; animPlaying.Clear();
        }

        static void Sleep()
        {
            awake = false; sleptAt = Time.realtimeSinceStartup;
            Unhook();
            for (int i = 0; i < fsmN; i++) { fsmArr[i] = null; fsmState[i] = null; }
            fsmN = 0;
            animPlaying.Clear(); animWatch.Clear(); animWatchExtra.Clear();
            // session ids die with every load: the age tables are only useful while somebody watches
            if (firedPairs.Count > 4096) firedPairs.Clear();
            if (lastByGoKind.Count > 4096) lastByGoKind.Clear();
        }

        // ---------------------------------------------------------------- signal taps
        const string TapName = "InsideDev.tap";
        static readonly List<SignalOut> deadTmp = new List<SignalOut>();

        static void Hook()
        {
            SignalManager sm = null;
            try { sm = PersistentBehaviour<SignalManager>.instance; } catch { }
            if (sm == null || sm.signalOuts == null) return;
            if (fConns == null) fConns = typeof(SignalOut).GetField("connections", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fConns == null) { if (!noConnsLogged) { noConnsLogged = true; DevLog.Write("[events] SignalOut.connections not found - signal taps disabled"); } return; }
            // drop taps of signals that went away; a signal that is only switched off loses its tap as well, so the
            // game switching it on again cannot end up with two
            var dead = deadTmp; dead.Clear();
            foreach (var kv in taps) if (!kv.Key.isActive || kv.Key.gameObject == null) dead.Add(kv.Key);
            foreach (var so in dead)
            {
                try { var list = fConns.GetValue(so) as FastList<SignalConnection>; if (list != null) list.Remove(taps[so]); } catch { }
                taps.Remove(so);
            }
            dead.Clear();
            foreach (var so in sm.signalOuts)
            {
                if (so == null || !so.isActive || so.gameObject == null || taps.ContainsKey(so)) continue;
                var list = fConns.GetValue(so) as FastList<SignalConnection>;
                if (list == null) continue;
                // a tap left behind by an earlier hook of this signal is taken over, never doubled
                SignalConnection old = null;
                for (int i = 0; i < list.Count; i++) { var c = list[i]; if (c != null && c.signalInName == TapName) { old = c; break; } }
                if (old != null) { taps[so] = old; continue; }
                var captured = so;
                var tapIn = new SignalIn { isActive = true, debugName = TapName, action = () => OnSignal(captured) };
                var tap = new SignalConnection { isActive = true, signalIn = tapIn, signalOut = so, signalOutGameObject = so.gameObject, signalOutName = so.debugName, signalInName = TapName };
                list.Insert(0, tap);
                taps[so] = tap;
            }
        }
        static bool noConnsLogged;

        public static void Unhook()
        {
            if (taps.Count == 0) return;
            int n = taps.Count;
            if (fConns != null)
                foreach (var kv in taps)
                {
                    try
                    {
                        var list = fConns.GetValue(kv.Key) as FastList<SignalConnection>;
                        if (list != null) list.Remove(kv.Value);
                    }
                    catch { }
                }
            taps.Clear();
            DevLog.Write("[events] " + n + " signal taps removed");
        }

        static readonly System.Text.StringBuilder sigText = new System.Text.StringBuilder(256);

        static void OnSignal(SignalOut so)
        {
            if (paused || !awake) return;
            try
            {
                var go = so.gameObject;
                int src = go != null ? go.GetInstanceID() : 0;
                var list = fConns.GetValue(so) as FastList<SignalConnection>;
                var sb = sigText; sb.Length = 0;
                sb.Append(go != null ? go.name : "?").Append('.').Append(so.debugName);
                int n = 0;
                float now = Time.realtimeSinceStartup;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var c = list[i];
                        if (c == null || c.signalInName == TapName) continue;
                        var rg = c.signalInGameObject;
                        if (rg != null) firedPairs[Key(src, rg.GetInstanceID())] = now;
                        sb.Append(n++ == 0 ? "  ->  " : ", ").Append(rg != null ? rg.name : "?").Append('.').Append(c.signalInName);
                    }
                if (n == 0) sb.Append("  (no receivers)");
                Add('S', src, sb.ToString());
            }
            catch { }
        }

        // ---------------------------------------------------------------- FSM transitions
        static PlayMakerFSM[] fsmArr = new PlayMakerFSM[256];
        static string[] fsmState = new string[256];
        static int fsmN;
        static readonly Dictionary<int, string> carry = new Dictionary<int, string>();
        static readonly List<Component> compTmp = new List<Component>(8);

        static void RebuildFsms()
        {
            // states of machines that are still there are carried over, so a rebuild never invents a transition
            carry.Clear();
            for (int i = 0; i < fsmN; i++) { var f = fsmArr[i]; if (f != null) carry[f.GetInstanceID()] = fsmState[i]; fsmArr[i] = null; fsmState[i] = null; }
            fsmN = 0;
            foreach (var r in ObjectDatabase.all)
            {
                if (r.go == null || (r.kind & ObjKind.StateMachine) == 0) continue;
                compTmp.Clear();
                r.go.GetComponents(typeof(PlayMakerFSM), compTmp);
                for (int k = 0; k < compTmp.Count; k++)
                {
                    var f = compTmp[k] as PlayMakerFSM; if (f == null) continue;
                    if (fsmN == fsmArr.Length) { Array.Resize(ref fsmArr, fsmN * 2); Array.Resize(ref fsmState, fsmN * 2); }
                    string st;
                    if (!carry.TryGetValue(f.GetInstanceID(), out st)) { try { st = f.ActiveStateName; } catch { st = null; } }
                    fsmArr[fsmN] = f; fsmState[fsmN] = st; fsmN++;
                }
            }
            compTmp.Clear(); carry.Clear();
        }

        static void DropFsm(int i)
        {
            fsmN--;
            fsmArr[i] = fsmArr[fsmN]; fsmState[i] = fsmState[fsmN];
            fsmArr[fsmN] = null; fsmState[fsmN] = null;
        }

        static void PollFsms()
        {
            if (ObjectDatabase.completedPasses != fsmPass)
            {
                bool first = fsmPass < 0;
                fsmPass = ObjectDatabase.completedPasses;
                if (first || fsmN == 0 || ObjectDatabase.addedLastPass > 0 || ObjectDatabase.removedLastPass > 0) RebuildFsms();
            }
            for (int i = 0; i < fsmN; i++)
            {
                var f = fsmArr[i];
                string st;
                try { st = f.ActiveStateName; } catch { DropFsm(i--); continue; }
                string old = fsmState[i];
                if (ReferenceEquals(old, st) || old == st) continue;
                fsmState[i] = st;
                if (f == null) { DropFsm(i--); continue; }   // destroyed since the last database pass
                var go = f.gameObject;
                if (!go.activeInHierarchy && string.IsNullOrEmpty(st)) continue;
                Add('F', go.GetInstanceID(), go.name + " / " + f.FsmName + ":  " + (string.IsNullOrEmpty(old) ? "(none)" : old) + "  ->  " + st);
            }
        }

        // ---------------------------------------------------------------- text
        static string Mode { get { return !enabled ? "off" : Always ? "always" : "demand"; } }

        // "on" / "off" first, as before; the tail says how it is switched and whether it only just woke up
        public static string Status()
        {
            bool wasAwake = awake;
            NoteRead();
            if (enabled && !wasAwake) Want(WakeForCommand);
            string s = (!enabled ? "off" : "on") + (paused ? " (paused)" : "") + ", " + taps.Count + " signals tapped, " + fsmN + " state machines watched, " + count + " events kept (" + total + " total)";
            if (!enabled) return s + "; switched off for this session (events on)";
            s += Always ? "; always on (" + ConfigKey + "=1)" : "; on while in use (events always keeps it on)";
            if (!wasAwake) s += "; was asleep: collecting from now, try again in a moment";
            return s;
        }

        public static string Dump(int n, string filter)
        {
            var sb = new System.Text.StringBuilder(Status() + "\n");
            int shown = 0;
            for (int i = 0; i < count && shown < n; i++)
            {
                var e = Get(i);
                if (filter != null && (e.text == null || e.text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                sb.Append(Fmt.F(e.t, "0.00")).Append("s  f").Append(e.frame).Append("  ").Append(KindName(e.kind)).Append(e.text).Append('\n');
                shown++;
            }
            return sb.ToString();
        }

        // one JSON line: state of the monitor and the newest n events (newest first)
        public static string DumpJson(int n, string filter)
        {
            bool wasAwake = awake;
            NoteRead();
            if (enabled && !wasAwake) Want(WakeForCommand);
            var o = new Json.Obj();
            o["ok"] = true; o["on"] = enabled; o["mode"] = Mode; o["awake"] = wasAwake; o["paused"] = paused;
            o["collecting"] = enabled && !wasAwake;
            o["taps"] = taps.Count; o["fsms"] = fsmN; o["kept"] = count; o["total"] = total; o["now"] = Time.realtimeSinceStartup; o["frame"] = Time.frameCount;
            var l = new List<object>();
            for (int i = 0; i < count && l.Count < n; i++)
            {
                var e = Get(i);
                if (filter != null && (e.text == null || e.text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                var x = new Json.Obj();
                x["t"] = e.t; x["frame"] = e.frame; x["kind"] = KindWord(e.kind); x["id"] = e.goId; x["text"] = e.text;
                l.Add(x);
            }
            o["events"] = l;
            return JsonLine.Write(o);
        }

        // bridge: events [<n> [filter]] | on | off | always | auto | clear | status   (last word "json" for one JSON line)
        //   on = may run again this session (as 0.10.0's "events on"), off = off for this session, always = on for good
        //   (events.monitor = 1), auto = events.monitor = 0 and on
        public static string Command(List<string> a)
        {
            bool json = a.Count > 1 && a[a.Count - 1] == "json";
            int argc = json ? a.Count - 1 : a.Count;
            string op = argc > 1 ? a[1] : "";
            if (op == "on") { enabled = true; Want(60f); }
            else if (op == "off") enabled = false;
            else if (op == "always") SetAlways(true);
            else if (op == "auto") Auto();
            else if (op == "clear") Clear();
            if (op == "on" || op == "off" || op == "always" || op == "auto" || op == "clear" || op == "status") return json ? DumpJson(0, null) : Status();
            int n = 40; string flt = null;
            if (argc > 1 && !Fmt.TryI(a[1], out n)) { n = 40; flt = a[1]; }
            if (argc > 2) flt = a[2];
            return json ? DumpJson(n, flt) : Dump(n, flt);
        }
    }
}
