using System;
using System.Collections.Generic;
using System.Reflection;
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
    public static class EventMonitor
    {
        public struct Ev
        {
            public float t; public int frame; public char kind;   // 'S' signal, 'F' fsm transition, 'A' sound posted, 'N' animation clip started
            public int goId; public string text;
        }

        public const int Capacity = 4000;
        static readonly Ev[] ring = new Ev[Capacity];
        static int head, count;
        public static int Count { get { return count; } }
        public static long total;
        public static bool enabled = true, paused;

        static readonly Dictionary<SignalOut, SignalConnection> taps = new Dictionary<SignalOut, SignalConnection>();
        static readonly Dictionary<long, float> firedPairs = new Dictionary<long, float>();
        static readonly Dictionary<int, string> fsmStates = new Dictionary<int, string>();
        static readonly List<PlayMakerFSM> fsms = new List<PlayMakerFSM>();
        static FieldInfo fConns;
        static int tappedPass = -1, fsmPass = -1;
        static float nextTapScan;
        public static int TapCount { get { return taps.Count; } }
        public static int FsmCount { get { return fsms.Count; } }

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
            float t; return lastByGoKind.TryGetValue(((long)goId << 8) | kind, out t) ? Time.realtimeSinceStartup - t : 1e9f;
        }
        public static string KindName(char k) { return k == 'S' ? "SIGNAL " : k == 'F' ? "FSM    " : k == 'A' ? "SOUND  " : k == 'N' ? "ANIM   " : "?      "; }

        // sound posts (from the managed SoundEngine wrapper)
        public static void AudioPost(GameObject go, string name)
        {
            if (!enabled || go == null) return;
            Add('A', go.GetInstanceID(), go.name + "  posts  " + name);
        }

        // animation clips starting on watched objects (graph nodes + selection): polled, no hooks
        static readonly Dictionary<int, string> animPlaying = new Dictionary<int, string>();
        static readonly List<GameObject> animWatch = new List<GameObject>(), animWatchExtra = new List<GameObject>();
        // objects below the Inspector selection (ACTIVITY section)
        public static void WatchExtra(IEnumerable<GameObject> gos)
        {
            animWatchExtra.Clear();
            foreach (var g in gos) if (g != null && (g.GetComponent<Animation>() != null || g.GetComponent<Animator>() != null) && animWatchExtra.Count < 200) animWatchExtra.Add(g);
        }
        static float nextAnimPoll;
        public static void WatchAnimations(IEnumerable<GameObject> gos)
        {
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
            lastByGoKind[((long)goId << 8) | kind] = Time.realtimeSinceStartup;
            ring[head] = new Ev { t = Time.realtimeSinceStartup, frame = Time.frameCount, kind = kind, goId = goId, text = text };
            head = (head + 1) % Capacity;
            if (count < Capacity) count++;
            total++;
        }

        static long Key(int a, int b) { return ((long)a << 32) ^ (uint)b; }

        // seconds since the (src -> dst) signal pair last fired; large if never
        public static float FiredAge(int srcGo, int dstGo)
        {
            float t;
            return firedPairs.TryGetValue(Key(srcGo, dstGo), out t) ? Time.realtimeSinceStartup - t : 1e9f;
        }

        public static void Tick()
        {
            if (!enabled) { if (taps.Count > 0) Unhook(); return; }
            try
            {
                if (ObjectDatabase.completedPasses != tappedPass || Time.realtimeSinceStartup > nextTapScan)
                {
                    tappedPass = ObjectDatabase.completedPasses;
                    nextTapScan = Time.realtimeSinceStartup + 3f;
                    Hook();
                }
                PollFsms();
                PollAnimations();
            }
            catch (Exception e) { DevLog.Error("event monitor", e); enabled = false; Unhook(); }
        }

        // ---------------------------------------------------------------- signal taps
        static void Hook()
        {
            SignalManager sm = null;
            try { sm = PersistentBehaviour<SignalManager>.instance; } catch { }
            if (sm == null || sm.signalOuts == null) return;
            if (fConns == null) fConns = typeof(SignalOut).GetField("connections", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fConns == null) { DevLog.Write("[events] SignalOut.connections not found - signal taps disabled"); return; }
            // drop taps of signals that went away
            var dead = new List<SignalOut>();
            foreach (var kv in taps) if (!kv.Key.isActive || kv.Key.gameObject == null) dead.Add(kv.Key);
            foreach (var so in dead) taps.Remove(so);
            foreach (var so in sm.signalOuts)
            {
                if (so == null || !so.isActive || so.gameObject == null || taps.ContainsKey(so)) continue;
                var list = fConns.GetValue(so) as FastList<SignalConnection>;
                if (list == null) continue;
                var captured = so;
                var tapIn = new SignalIn { isActive = true, debugName = "InsideDev.tap", action = () => OnSignal(captured) };
                var tap = new SignalConnection { isActive = true, signalIn = tapIn, signalOut = so, signalOutGameObject = so.gameObject, signalOutName = so.debugName, signalInName = "InsideDev.tap" };
                list.Insert(0, tap);
                taps[so] = tap;
            }
        }

        public static void Unhook()
        {
            if (fConns == null) { taps.Clear(); return; }
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
            DevLog.Write("[events] signal taps removed");
        }

        static void OnSignal(SignalOut so)
        {
            try
            {
                var go = so.gameObject;
                int src = go != null ? go.GetInstanceID() : 0;
                var list = fConns.GetValue(so) as FastList<SignalConnection>;
                var sb = new System.Text.StringBuilder();
                sb.Append(go != null ? go.name : "?").Append('.').Append(so.debugName);
                int n = 0;
                float now = Time.realtimeSinceStartup;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var c = list[i];
                        if (c == null || c.signalInName == "InsideDev.tap") continue;
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
        static void PollFsms()
        {
            if (ObjectDatabase.completedPasses != fsmPass)
            {
                fsmPass = ObjectDatabase.completedPasses;
                fsms.Clear();
                foreach (var r in ObjectDatabase.all)
                    if (r.go != null && (r.kind & ObjKind.StateMachine) != 0)
                        foreach (var f in r.go.GetComponents<PlayMakerFSM>()) if (f != null) fsms.Add(f);
            }
            for (int i = 0; i < fsms.Count; i++)
            {
                var f = fsms[i];
                if (f == null) continue;
                string st;
                try { st = f.ActiveStateName; } catch { continue; }
                int id = f.GetInstanceID();
                string old;
                if (!fsmStates.TryGetValue(id, out old)) { fsmStates[id] = st; continue; }
                if (old == st) continue;
                fsmStates[id] = st;
                if (!f.gameObject.activeInHierarchy && string.IsNullOrEmpty(st)) continue;
                Add('F', f.gameObject.GetInstanceID(), f.gameObject.name + " / " + f.FsmName + ":  " + (string.IsNullOrEmpty(old) ? "(none)" : old) + "  ->  " + st);
            }
        }

        public static string Status()
        {
            return (enabled ? "on" : "off") + (paused ? " (paused)" : "") + ", " + taps.Count + " signals tapped, " + fsms.Count + " state machines watched, " + count + " events kept (" + total + " total)";
        }

        public static string Dump(int n, string filter)
        {
            var sb = new System.Text.StringBuilder(Status() + "\n");
            int shown = 0;
            for (int i = 0; i < count && shown < n; i++)
            {
                var e = Get(i);
                if (filter != null && e.text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                sb.Append(e.t.ToString("0.00")).Append("s  f").Append(e.frame).Append("  ").Append(KindName(e.kind)).Append(e.text).Append('\n');
                shown++;
            }
            return sb.ToString();
        }
    }
}
