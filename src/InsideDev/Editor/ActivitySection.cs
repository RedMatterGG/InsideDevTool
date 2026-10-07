using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace InsideDev
{
    // Inspector section ACTIVITY: everything that moves or makes noise in the selected object AND everything below it,
    // so selecting "the pig" (or its root) answers "what animations / sounds / events does it have" in one place:
    //   animations     Animation / Animator components below, clip count and what is playing (click = select it)
    //   sounds         sound events referenced by objects below (sound library) and posts heard from them this
    //                  session (click = open in Audio DB)
    //   state machines PlayMaker FSMs below with their current state
    //   signals        signal outputs below
    //   live events    the last events raised by objects below, filtered by type (signals / FSM / sounds / animation)
    // Rebuilt on selection change and every 3 s; the live list every 0.25 s. Objects below are also watched for
    // animation clips starting (event type "animation").
    public static class ActivitySection
    {
        struct Line { public string text; public Color c; public GameObject go; public string sound; }
        static readonly Color cHead = new Color(0.55f, 1f, 0.55f, 1f), cSnd = new Color(0.3f, 0.9f, 0.85f, 1f), cFsm = new Color(0.72f, 0.55f, 1f, 1f), cSig = new Color(1f, 0.72f, 0.25f, 1f);

        static int forId; static float at, liveAt; static string title;
        static readonly List<Line> lines = new List<Line>();
        static readonly HashSet<int> ids = new HashSet<int>();
        static readonly List<Line> live = new List<Line>();
        static bool lS = true, lF = true, lA = true, lN = true;
        static long liveTotal = -1;

        static void Rebuild(GameObject go)
        {
            lines.Clear(); ids.Clear();
            var all = go.GetComponentsInChildren<Transform>(true);
            var gos = new List<GameObject>();
            for (int i = 0; i < all.Length && i < 4000; i++) { ids.Add(all[i].gameObject.GetInstanceID()); gos.Add(all[i].gameObject); }
            EventMonitor.WatchExtra(gos);

            // animations
            int na = 0; var anims = new List<Line>();
            foreach (var g in gos)
            {
                var an = g.GetComponent<Animation>(); var am = g.GetComponent<Animator>();
                if (an == null && am == null) continue;
                na++; if (anims.Count >= 25) continue;
                string d;
                if (an != null)
                {
                    if (!AnimSafe.CanWalkStates(an)) d = "clips listed once it is on";
                    else
                    {
                        int n = an.GetClipCount(); string playing = null;
                        if (an.isPlaying) foreach (AnimationState st in an) if (st != null && an.IsPlaying(st.name)) { playing = st.name; break; }
                        d = n == 0 ? "no clips" : n + " clip" + (n == 1 ? "" : "s") + (playing != null ? ", playing " + playing : ", idle");
                    }
                }
                else d = am.runtimeAnimatorController != null ? "animator " + am.runtimeAnimatorController.name : "animator without controller";
                anims.Add(new Line { text = "      " + Rel(go, g) + "  —  " + d, c = g.activeInHierarchy ? Color.white : UI.Dim, go = g });
            }
            // sounds: references below + posts heard from objects below
            var snd = new Dictionary<string, int>();
            foreach (var d in SoundLibrary.defs.Values)
                foreach (var b in d.bindings) if (b.go != null && ids.Contains(b.go.GetInstanceID()) && !snd.ContainsKey(d.name)) snd[d.name] = 0;
            for (int i = 0; i < EventMonitor.Count; i++)
            {
                var e = EventMonitor.Get(i); if (e.kind != 'A' || !ids.Contains(e.goId)) continue;
                int k = e.text.IndexOf("  posts  "); if (k < 0) continue;
                string ev = e.text.Substring(k + 9); int c; snd.TryGetValue(ev, out c); snd[ev] = c + 1;
            }
            // state machines and signals
            var fsms = new List<Line>(); var sigs = new List<Line>();
            foreach (var g in gos)
                foreach (var f in g.GetComponents<PlayMakerFSM>())
                    if (f != null && fsms.Count < 20) fsms.Add(new Line { text = "      " + Rel(go, g) + " / " + f.FsmName + "   state: " + (f.ActiveStateName ?? "-"), c = cFsm, go = g });
            SignalManager sm = null; try { sm = PersistentBehaviour<SignalManager>.instance; } catch { }
            if (sm != null && sm.signalOuts != null)
                foreach (var so in sm.signalOuts)
                    if (so != null && so.gameObject != null && ids.Contains(so.gameObject.GetInstanceID()) && sigs.Count < 20)
                        sigs.Add(new Line { text = "      " + Rel(go, so.gameObject) + " . " + so.debugName, c = cSig, go = so.gameObject });

            title = "ACTIVITY (this object and " + (gos.Count - 1) + " below):  " + na + " animation(s), " + snd.Count + " sound(s), " + fsms.Count + " state machine(s), " + sigs.Count + " signal(s)";
            if (anims.Count > 0) { lines.Add(new Line { text = "   animations" + (na > anims.Count ? " (first " + anims.Count + " of " + na + ")" : ""), c = UI.Dim }); lines.AddRange(anims); }
            if (snd.Count > 0)
            {
                lines.Add(new Line { text = "   sounds  (click = open in Audio DB)", c = UI.Dim });
                var keys = new List<string>(snd.Keys); keys.Sort((a, b) => snd[b] != snd[a] ? snd[b].CompareTo(snd[a]) : string.CompareOrdinal(a, b));
                int n = 0; foreach (var k in keys) { if (n++ >= 25) break; lines.Add(new Line { text = "      " + k + (snd[k] > 0 ? "   heard " + snd[k] + "x" : ""), c = cSnd, sound = k }); }
            }
            if (fsms.Count > 0) { lines.Add(new Line { text = "   state machines", c = UI.Dim }); lines.AddRange(fsms); }
            if (sigs.Count > 0) { lines.Add(new Line { text = "   signals", c = UI.Dim }); lines.AddRange(sigs); }
            if (lines.Count == 0) lines.Add(new Line { text = "   no animation, sound, state machine or signal on this object or below it", c = UI.Dim });
        }

        static string Rel(GameObject root, GameObject g)
        {
            if (g == root) return g.name + " (this)";
            var parts = new List<string>();
            for (var t = g.transform; t != null && t != root.transform; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return parts.Count > 3 ? parts[0] + "/…/" + parts[parts.Count - 1] : string.Join("/", parts.ToArray());
        }

        static void RebuildLive()
        {
            live.Clear();
            for (int i = 0; i < EventMonitor.Count && live.Count < 10; i++)
            {
                var e = EventMonitor.Get(i);
                if (!ids.Contains(e.goId)) continue;
                if ((e.kind == 'S' && !lS) || (e.kind == 'F' && !lF) || (e.kind == 'A' && !lA) || (e.kind == 'N' && !lN)) continue;
                live.Add(new Line { text = "      " + (Time.realtimeSinceStartup - e.t).ToString("0.0") + " s ago   " + EventMonitor.KindName(e.kind) + " " + e.text, c = EventsPanel.KindColor(e.kind) });
            }
        }

        public static void Draw(UI ui, GameObject go)
        {
            float now = Time.realtimeSinceStartup;
            int id = go.GetInstanceID();
            if (id != forId || now - at > 3f) { forId = id; at = now; try { Rebuild(go); } catch (Exception e) { DevLog.Error("activity", e); } liveTotal = -1; }
            if (!Links.Section(ui, "activity", title ?? "ACTIVITY", lines.Count, cHead)) return;
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (l.go != null || l.sound != null)
                {
                    if (ui.Item(l.text, l.c))
                    {
                        if (l.go != null) { Selection.Set(l.go, "activity"); return; }
                        var rec = AudioDb.Find(l.sound); if (rec != null) { AudioDbPanel.Select(rec); DevCore.Instance.ShowPanel("audiodb"); }
                    }
                    if (ui.LastHover && l.go != null) Selection.SetHover(l.go);
                }
                else ui.Label(l.text, l.c);
            }
            ui.BeginRow();
            ui.Label("   live events:", UI.Dim, 100);
            bool s = ui.Toggle(lS, "signals"), f = ui.Toggle(lF, "state machines"), a = ui.Toggle(lA, "sounds"), n = ui.Toggle(lN, "animation");
            if (s != lS || f != lF || a != lA || n != lN) { lS = s; lF = f; lA = a; lN = n; liveTotal = -1; }
            ui.EndRow();
            if (liveTotal != EventMonitor.total || now - liveAt > 1f) { liveTotal = EventMonitor.total; liveAt = now; RebuildLive(); }
            if (live.Count == 0) ui.Label("      nothing yet - events appear here as the game raises them", UI.Dim);
            for (int i = 0; i < live.Count; i++) ui.Label(live[i].text, live[i].c);
        }
    }
}
