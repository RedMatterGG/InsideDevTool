using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace InsideDev
{
    // Phase 8: game-specific adapters shown at the top of the Inspector. Each explains what the object is for
    // in game terms and offers the actions that make sense for it, clearly labelled by how "real" they are:
    //   Fire (as the game would)  - raises the same signal/event the game raises; downstream logic runs normally
    //   Force                      - sets state directly, skipping the game's own conditions (may leave things inconsistent)
    public static class Adapters
    {
        static readonly Color cHead = new Color(0.55f, 0.9f, 0.75f, 1f);
        static readonly Color cWarn = new Color(1f, 0.7f, 0.4f, 1f);

        public static void Draw(UI ui, GameObject go)
        {
            try
            {
                foreach (var t in go.GetComponents<IsTriggeredByProbe>()) if (t != null) Trigger(ui, t);
                foreach (var sp in go.GetComponents<Savepoint>()) if (sp != null) SavepointA(ui, sp);
                foreach (var an in go.GetComponents<Animator>()) if (an != null) AnimatorA(ui, an);
                foreach (var an in go.GetComponents<Animation>()) if (an != null) AnimationA(ui, an);
                Recent(ui, go);
            }
            catch (Exception e) { ui.Label("adapter error: " + e.Message, Color.red); }
        }

        // ---------------------------------------------------------------- trigger volume (IsTriggeredByProbe)
        static void Trigger(UI ui, IsTriggeredByProbe t)
        {
            ui.Label("TRIGGER  —  fires when " + Who(t) + " enters / leaves this volume", cHead);
            int inside = 0;
            try { inside = (int)typeof(IsTriggeredByProbe).GetField("noOfCharacterProbesInside", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).GetValue(t); } catch { }
            var col = t.GetComponent<Collider>();
            ui.Label("   " + (inside > 0 ? "character INSIDE now (" + inside + ")" : "nobody inside") + (col != null && !col.enabled ? "   [collider disabled - cannot fire]" : "") + (!t.gameObject.activeInHierarchy ? "   [object inactive - cannot fire]" : "") + (!t.enabled ? "   [script disabled]" : ""), inside > 0 ? UI.Accent : UI.Dim);
            Targets(ui, "   on enter ->", t.enterSignal);
            Targets(ui, "   on exit  ->", t.exitSignal);
            ui.BeginRow();
            ui.Space(12);
            if (ui.Button("Fire enter (as the game would)") && t.enterSignal != null) { DevLog.Write("[adapter] fire enterSignal on " + t.name); ChangeRecorder.Action(t.gameObject, "signal", null, t.enterSignal.debugName, "adapter"); t.enterSignal.Signal(); }
            if (ui.Button("Fire exit") && t.exitSignal != null) { DevLog.Write("[adapter] fire exitSignal on " + t.name); ChangeRecorder.Action(t.gameObject, "signal", null, t.exitSignal.debugName, "adapter"); t.exitSignal.Signal(); }
            if (ui.Button("Boy here")) G.Teleport(col != null ? col.bounds.center : t.transform.position);
            ui.EndRow();
        }

        static string Who(IsTriggeredByProbe t)
        {
            var l = new List<string>();
            if (t.useBoyProbe) l.Add("the boy");
            if (t.useHuddleProbe) l.Add("the Huddle");
            if (t.probe != null) l.Add("probe '" + t.probe.name + "'");
            if (t.anyNonTriggerProbe) l.Add("any probe");
            return l.Count == 0 ? "a probe" : string.Join(" or ", l.ToArray());
        }

        static void Targets(UI ui, string prefix, SignalOut so)
        {
            if (so == null) return;
            SignalManager sm = null;
            try { sm = PersistentBehaviour<SignalManager>.instance; } catch { }
            int n = 0;
            if (sm != null && sm.connections != null)
                foreach (var c in sm.connections)
                {
                    if (c == null || !c.isActive || c.signalOut != so) continue;
                    n++;
                    var rg = c.signalInGameObject;
                    ui.BeginRow();
                    bool ck = ui.Item(prefix + "  " + (rg != null ? rg.name : "?") + "." + c.signalInName + (c.IsInputFsm ? "  (state machine event)" : ""), new Color(1f, 0.72f, 0.25f, 1f));
                    if (ui.LastHover && rg != null) Selection.SetHover(rg);
                    ui.EndRow();
                    if (ck && rg != null) { Selection.Set(rg, "adapter"); return; }
                }
            if (n == 0) ui.Label(prefix + "  (nothing connected)", UI.Dim);
        }

        // ---------------------------------------------------------------- savepoint
        static void SavepointA(UI ui, Savepoint sp)
        {
            ui.Label("SAVEPOINT  —  checkpoint #" + sp.index + " of area " + SafeSub(sp.subsceneIndex) + (sp.isChapter ? "  (chapter start)" : ""), cHead);
            ui.Label("   spawn " + sp.GetGroundPosition().ToString("F2") + "   facing " + sp.facing + "   " + (sp.isCheckpointOnly ? "checkpoint only" : "saves the game") + (sp.deactivateAfterSave ? ", turns off after saving" : ""), UI.Dim);
            ui.BeginRow();
            ui.Space(12);
            if (ui.Button("Load this savepoint (game load)")) G.LoadSavepoint(sp.subsceneIndex, sp.index);
            if (ui.Button("Boy to spawn")) G.Teleport(sp.GetGroundPosition());
            ui.EndRow();
        }

        static string SafeSub(int i) { try { return SavepointManager.GetSubsceneName(i); } catch { return "#" + i; } }

        // ---------------------------------------------------------------- who plays an animation (cached per object)
        static int playedFor; static float playedAt; static readonly List<string> playedBy = new List<string>(), clipEvents = new List<string>();
        static void PlayedBy(GameObject go, Animation an)
        {
            int id = go.GetInstanceID();
            if (id == playedFor && Time.realtimeSinceStartup - playedAt < 5f) return;
            playedFor = id; playedAt = Time.realtimeSinceStartup; playedBy.Clear(); clipEvents.Clear();
            if (an != null && an.playAutomatically && an.clip != null) playedBy.Add("    [UNITY] plays '" + an.clip.name + "' by itself when the object turns on (Play Automatically)");
            ReferenceIndex.Refresh(go);
            foreach (var e in ReferenceIndex.Filter(ReferenceIndex.Referencers(go), false))
            {
                if (playedBy.Count > 14) { playedBy.Add("    …"); break; }
                bool animRef = e.dstType == "Animation" || e.dstType == "Animator" || e.field.IndexOf("Anim", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!animRef) continue;
                string src = ReferenceIndex.PathOf(e.srcGo);
                if (e.prov == Provenance.StateMachine) { playedBy.Add("    [FSM] " + src + "  " + e.field); continue; }
                if (e.srcComp != null && CodeGraph.done && CodeLookup.CodeFor(e.srcComp.GetType(), e.field, playedBy, src, CodeLookup.AnimKeys)) continue;
                playedBy.Add("    [" + ReferenceIndex.ProvLabel(e.prov) + "] " + src + "  " + e.srcType + "." + e.field);
            }
            if (an != null && AnimSafe.CanWalkStates(an))
                foreach (AnimationState st in an)
                {
                    if (st == null || st.clip == null) continue;
                    AnimationEvent[] evs; try { evs = st.clip.events; } catch { continue; }
                    foreach (var ev in evs) if (clipEvents.Count < 20) clipEvents.Add("    clip '" + st.clip.name + "' at " + ev.time.ToString("0.00") + " s calls " + ev.functionName + "(" + (ev.stringParameter ?? "") + (ev.objectReferenceParameter != null ? " " + ev.objectReferenceParameter.name : "") + ")");
                }
            if (playedBy.Count == 0) playedBy.Add("    nothing found that plays it (no script, state machine or Play Automatically) - it may never play");
        }

        // ---------------------------------------------------------------- Animation (legacy, most INSIDE props)
        static void AnimationA(UI ui, Animation an)
        {
            var go = an.gameObject;
            if (!AnimSafe.CanWalkStates(an)) { ui.Label("ANIMATION  —  clips are listed once this object is on (reading them earlier crashes Unity 5.0)", cHead); return; }
            string playing = null; float t = 0f;
            foreach (AnimationState st in an) if (st != null && an.IsPlaying(st.name)) { playing = st.name; t = st.normalizedTime; break; }
            ui.Label("ANIMATION  —  " + (an.GetClipCount() == 0 ? "no clips (it cannot play anything)   " : an.GetClipCount() + " clip(s)   ") + (playing != null ? "PLAYING " + playing + " " + (t % 1f * 100f).ToString("0") + "%" : "idle") + (an.enabled ? "" : "   [disabled]"), cHead);
            PlayedBy(go, an);
            ui.Label("   played by:", UI.Dim);
            foreach (var l in playedBy) ui.Label(l, l.TrimStart().StartsWith("[CODE]") ? Color.white : UI.Dim);
            if (clipEvents.Count > 0) { ui.Label("   animation events (functions the clips call):", UI.Dim); foreach (var l in clipEvents) ui.Label(l, UI.Dim); }
            ui.Label("   Play = run as the game would   pose slider = preview a frame without playing (not recorded; the game may override it)", UI.Dim);
            foreach (AnimationState st in an)
            {
                if (st == null) continue;
                ui.BeginRow(); ui.Space(12);
                if (ui.Button("Play", 44)) { an.Stop(); an.Play(st.name); ChangeRecorder.Action(go, "anim", null, st.name, "adapter"); }
                if (ui.Button("Stop", 44)) an.Stop(st.name);
                ui.Label(st.name + "  " + st.length.ToString("0.00") + " s  " + st.wrapMode, an.IsPlaying(st.name) ? UI.Accent : UI.Txt, 260);
                float cur = an.IsPlaying(st.name) ? st.normalizedTime % 1f : poseT(st.name);
                float nv = ui.Slider("pose_" + go.GetInstanceID() + st.name, cur, 0f, 1f, Mathf.Max(60, ui.Width - 4));
                if (Mathf.Abs(nv - cur) > 0.0005f && !an.IsPlaying(st.name))
                {
                    poses[st.name] = nv;
                    bool was = st.enabled; st.enabled = true; st.weight = 1f; st.normalizedTime = nv; an.Sample(); st.enabled = was;
                }
                ui.EndRow();
            }
        }
        static readonly Dictionary<string, float> poses = new Dictionary<string, float>();
        static float poseT(string clip) { float v; return poses.TryGetValue(clip, out v) ? v : 0f; }

        // ---------------------------------------------------------------- Animator (Mecanim)
        static void AnimatorA(UI ui, Animator an)
        {
            string clip = null; float nt = 0f;
            try { if (an.runtimeAnimatorController != null && an.isActiveAndEnabled) { var ci = an.GetCurrentAnimatorClipInfo(0); if (ci != null && ci.Length > 0 && ci[0].clip != null) clip = ci[0].clip.name; nt = an.GetCurrentAnimatorStateInfo(0).normalizedTime; } } catch { }
            ui.Label("ANIMATOR  —  " + (an.runtimeAnimatorController != null ? an.runtimeAnimatorController.name : "no controller (it cannot animate anything)") + "   speed " + an.speed.ToString("0.##") + (an.enabled ? "" : "   [disabled]") + (clip != null ? "   now: " + clip + " " + (nt % 1f * 100f).ToString("0") + "%" : ""), cHead);
            PlayedBy(an.gameObject, an.GetComponent<Animation>());
            ui.Label("   driven by:", UI.Dim);
            foreach (var l in playedBy) ui.Label(l, l.TrimStart().StartsWith("[CODE]") ? Color.white : UI.Dim);
            AnimatorControllerParameter[] ps = null;
            try { ps = an.parameters; } catch { }
            if (ps == null || ps.Length == 0) { ui.Label("   (no parameters)", UI.Dim); return; }
            foreach (var p in ps)
            {
                ui.BeginRow();
                ui.Label("   " + p.name, UI.Dim, 200);
                try
                {
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Bool:
                            bool b = an.GetBool(p.name); bool nb = ui.Toggle(b, b ? "true" : "false"); if (nb != b) an.SetBool(p.name, nb); break;
                        case AnimatorControllerParameterType.Trigger:
                            if (ui.Button("Set trigger")) an.SetTrigger(p.name); break;
                        case AnimatorControllerParameterType.Float:
                            ui.Label(an.GetFloat(p.name).ToString("0.###")); break;
                        case AnimatorControllerParameterType.Int:
                            ui.Label(an.GetInteger(p.name).ToString()); break;
                    }
                }
                catch { ui.Label("?"); }
                ui.EndRow();
            }
        }

        // ---------------------------------------------------------------- recent live events for this object
        static void Recent(UI ui, GameObject go)
        {
            int id = go.GetInstanceID(), shown = 0, total = 0;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < EventMonitor.Count && total < 6; i++) if (EventMonitor.Get(i).goId == id) total++;
            if (total == 0) return;
            if (!Links.Section(ui, "recent", "RECENT EVENTS (live)", total, cHead)) return;
            for (int i = 0; i < EventMonitor.Count && shown < 6; i++)
            {
                var e = EventMonitor.Get(i);
                if (e.goId != id) continue;
                shown++;
                ui.Label("   " + (now - e.t).ToString("0.0") + " s ago   " + (e.kind == 'S' ? "signal  " : "state   ") + e.text, now - e.t < 2f ? Color.white : UI.Dim);
            }
        }
    }
}
