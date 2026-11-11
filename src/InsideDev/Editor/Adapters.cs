using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace InsideDev
{
    // Phase 8: game-specific adapters shown at the top of the Inspector. Each explains what the object is for
    // in game terms and offers the actions that make sense for it, clearly labelled by how "real" they are:
    //   Fire (as the game would)  - raises the same signal/event the game raises; downstream logic runs normally
    //   Force                      - sets state directly, skipping the game's own conditions (may leave things inconsistent)
    // What is shown is read from the game a few times per second and kept in between (the connection table, the
    // animation states and the spawn position are not walked or computed on every frame).
    public static class Adapters
    {
        static readonly Color cHead = new Color(0.55f, 0.9f, 0.75f, 1f);
        static readonly Color cWarn = new Color(1f, 0.7f, 0.4f, 1f);
        static readonly Color cTarget = new Color(1f, 0.72f, 0.25f, 1f);
        const float Period = 0.5f;

        // the components of the selection the adapters are for, looked up when the selection changes and once per second
        static int compsFor; static float compsAt;
        static IsTriggeredByProbe[] triggers; static Savepoint[] savepoints; static Animator[] animators; static Animation[] animations;

        public static void Draw(UI ui, GameObject go)
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                int id = go.GetInstanceID();
                if (id != compsFor || now - compsAt > 1f || triggers == null)
                {
                    compsFor = id; compsAt = now;
                    triggers = go.GetComponents<IsTriggeredByProbe>(); savepoints = go.GetComponents<Savepoint>();
                    animators = go.GetComponents<Animator>(); animations = go.GetComponents<Animation>();
                }
                foreach (var t in triggers) if (t != null) Trigger(ui, t, now);
                foreach (var sp in savepoints) if (sp != null) SavepointA(ui, sp, now);
                foreach (var an in animators) if (an != null) AnimatorA(ui, an, now);
                foreach (var an in animations) if (an != null) AnimationA(ui, an, now);
                Recent(ui, go, now);
            }
            catch (Exception e) { ui.Label("adapter error: " + e.Message, Color.red); }
        }

        // ---------------------------------------------------------------- trigger volume (IsTriggeredByProbe)
        static readonly FieldInfo fProbesInside = typeof(IsTriggeredByProbe).GetField("noOfCharacterProbesInside", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        struct TargetRow { public string text; public GameObject go; }
        static int trigFor; static float trigAt; static string trigHead, trigState; static bool trigInside; static Collider trigCol;
        static readonly List<TargetRow> trigEnter = new List<TargetRow>(), trigExit = new List<TargetRow>();

        static void Trigger(UI ui, IsTriggeredByProbe t, float now)
        {
            int id = t.GetInstanceID();
            if (id != trigFor || now - trigAt > Period)
            {
                trigFor = id; trigAt = now;
                trigHead = "TRIGGER  —  fires when " + Who(t) + " enters / leaves this volume";
                int inside = 0;
                try { if (fProbesInside != null) inside = (int)fProbesInside.GetValue(t); } catch { }
                trigCol = t.GetComponent<Collider>();
                trigInside = inside > 0;
                trigState = "   " + (inside > 0 ? "character INSIDE now (" + inside + ")" : "nobody inside") + (trigCol != null && !trigCol.enabled ? "   [collider disabled - cannot fire]" : "") + (!t.gameObject.activeInHierarchy ? "   [object inactive - cannot fire]" : "") + (!t.enabled ? "   [script disabled]" : "");
                Targets(trigEnter, "   on enter ->", t.enterSignal);
                Targets(trigExit, "   on exit  ->", t.exitSignal);
            }
            ui.Label(trigHead, cHead);
            ui.Label(trigState, trigInside ? UI.Accent : UI.Dim);
            if (TargetRows(ui, trigEnter) || TargetRows(ui, trigExit)) return;
            ui.BeginRow();
            ui.Space(12);
            if (ui.Button("Fire enter (as the game would)") && t.enterSignal != null) { DevLog.Write("[adapter] fire enterSignal on " + t.name); ChangeRecorder.Action(t.gameObject, "signal", null, t.enterSignal.debugName, "adapter"); t.enterSignal.Signal(); }
            if (ui.Button("Fire exit") && t.exitSignal != null) { DevLog.Write("[adapter] fire exitSignal on " + t.name); ChangeRecorder.Action(t.gameObject, "signal", null, t.exitSignal.debugName, "adapter"); t.exitSignal.Signal(); }
            if (ui.Button("Boy here")) G.Teleport(trigCol != null ? trigCol.bounds.center : t.transform.position);
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

        // who receives the signal: one walk over the game's connection table per refresh
        static void Targets(List<TargetRow> into, string prefix, SignalOut so)
        {
            into.Clear();
            if (so == null) return;
            SignalManager sm = null;
            try { sm = PersistentBehaviour<SignalManager>.instance; } catch { }
            if (sm != null && sm.connections != null)
                foreach (var c in sm.connections)
                {
                    if (c == null || !c.isActive || c.signalOut != so) continue;
                    var rg = c.signalInGameObject;
                    into.Add(new TargetRow { go = rg, text = prefix + "  " + (rg != null ? rg.name : "?") + "." + c.signalInName + (c.IsInputFsm ? "  (state machine event)" : "") });
                }
            if (into.Count == 0) into.Add(new TargetRow { text = prefix + "  (nothing connected)" });
        }

        // true: a row was clicked and the selection changed (the caller stops drawing this object)
        static bool TargetRows(UI ui, List<TargetRow> rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.go == null && rows.Count == 1 && r.text.EndsWith("(nothing connected)", StringComparison.Ordinal)) { ui.Label(r.text, UI.Dim); continue; }
                ui.BeginRow();
                bool ck = ui.Item(r.text, cTarget);
                if (ui.LastHover && r.go != null) Selection.SetHover(r.go);
                ui.EndRow();
                if (ck && r.go != null) { Selection.Set(r.go, "adapter"); return true; }
            }
            return false;
        }

        // ---------------------------------------------------------------- savepoint
        static int spFor; static float spAt; static string spHead, spLine;
        static void SavepointA(UI ui, Savepoint sp, float now)
        {
            int id = sp.GetInstanceID();
            if (id != spFor || now - spAt > Period)
            {
                spFor = id; spAt = now;
                spHead = "SAVEPOINT  —  checkpoint #" + sp.index + " of area " + SafeSub(sp.subsceneIndex) + (sp.isChapter ? "  (chapter start)" : "");
                spLine = "   spawn " + sp.GetGroundPosition().ToString("F2") + "   facing " + sp.facing + "   " + (sp.isCheckpointOnly ? "checkpoint only" : "saves the game") + (sp.deactivateAfterSave ? ", turns off after saving" : "");
            }
            ui.Label(spHead, cHead);
            ui.Label(spLine, UI.Dim);
            ui.BeginRow();
            ui.Space(12);
            if (ui.Button("Load this savepoint (game load)")) G.LoadSavepoint(sp.subsceneIndex, sp.index);
            if (ui.Button("Boy to spawn")) G.Teleport(sp.GetGroundPosition());
            ui.EndRow();
        }

        static string SafeSub(int i) { try { return SavepointManager.GetSubsceneName(i); } catch { return "#" + i; } }

        // ---------------------------------------------------------------- who plays an animation (cached per object)
        static int playedFor; static float playedAt; static readonly List<string> playedBy = new List<string>(), clipEvents = new List<string>();
        static readonly List<Color> playedColor = new List<Color>();
        static void PlayedBy(GameObject go, Animation an)
        {
            int id = go.GetInstanceID();
            if (id == playedFor && Time.realtimeSinceStartup - playedAt < 5f) return;
            playedFor = id; playedAt = Time.realtimeSinceStartup; playedBy.Clear(); clipEvents.Clear(); playedColor.Clear();
            if (an != null && an.playAutomatically && an.clip != null) playedBy.Add("    [UNITY] plays '" + an.clip.name + "' by itself when the object turns on (Play Automatically)");
            ReferenceIndex.Refresh(go);
            // the code graph says which game code plays it; it is asked for when a script is what references the animation
            bool code = LogicPanel.Scanned;
            foreach (var e in ReferenceIndex.Filter(ReferenceIndex.Referencers(go), false))
            {
                if (playedBy.Count > 14) { playedBy.Add("    …"); break; }
                bool animRef = e.dstType == "Animation" || e.dstType == "Animator" || e.field.IndexOf("Anim", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!animRef) continue;
                string src = ReferenceIndex.PathOf(e.srcGo);
                if (e.prov == Provenance.StateMachine) { playedBy.Add("    [FSM] " + src + "  " + e.field); continue; }
                if (e.srcComp != null)
                {
                    if (!code) LogicPanel.Request();
                    else if (CodeLookup.CodeFor(e.srcComp.GetType(), e.field, playedBy, src, CodeLookup.AnimKeys)) continue;
                }
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
            foreach (var l in playedBy) playedColor.Add(l.TrimStart().StartsWith("[CODE]", StringComparison.Ordinal) ? Color.white : UI.Dim);
        }

        static void PlayedLines(UI ui) { for (int i = 0; i < playedBy.Count; i++) ui.Label(playedBy[i], i < playedColor.Count ? playedColor[i] : UI.Dim); }

        // ---------------------------------------------------------------- Animation (legacy, most INSIDE props)
        // the states of the selected Animation: names, labels and slider ids are read once per second, what is playing
        // ten times per second
        sealed class StateRow { public AnimationState st; public string name, label, sliderId; public bool playing; }
        static readonly List<StateRow> stateRows = new List<StateRow>();
        static int statesFor; static float statesAt, animHeadAt; static string animHead;

        static void AnimationA(UI ui, Animation an, float now)
        {
            var go = an.gameObject;
            if (!AnimSafe.CanWalkStates(an)) { ui.Label("ANIMATION  —  clips are listed once this object is on (reading them earlier crashes Unity 5.0)", cHead); return; }
            int id = an.GetInstanceID();
            bool stale = id != statesFor || now - statesAt > 1f;
            if (!stale) foreach (var r in stateRows) if (r.st == null) { stale = true; break; }
            if (stale)
            {
                statesFor = id; statesAt = now; stateRows.Clear(); animHeadAt = 0f;
                int gid = go.GetInstanceID();
                foreach (AnimationState st in an)
                {
                    if (st == null) continue;
                    string n = st.name;
                    stateRows.Add(new StateRow { st = st, name = n, label = n + "  " + st.length.ToString("0.00") + " s  " + st.wrapMode, sliderId = "pose_" + gid + n });
                }
            }
            if (now - animHeadAt > 0.1f)
            {
                animHeadAt = now;
                string playing = null; float t = 0f;
                foreach (var r in stateRows) { r.playing = an.IsPlaying(r.name); if (r.playing && playing == null) { playing = r.name; t = r.st.normalizedTime; } }
                int clips = an.GetClipCount();
                animHead = "ANIMATION  —  " + (clips == 0 ? "no clips (it cannot play anything)   " : clips + " clip(s)   ") + (playing != null ? "PLAYING " + playing + " " + (t % 1f * 100f).ToString("0") + "%" : "idle") + (an.enabled ? "" : "   [disabled]");
            }
            ui.Label(animHead, cHead);
            PlayedBy(go, an);
            ui.Label("   played by:", UI.Dim);
            PlayedLines(ui);
            if (clipEvents.Count > 0) { ui.Label("   animation events (functions the clips call):", UI.Dim); foreach (var l in clipEvents) ui.Label(l, UI.Dim); }
            ui.Label("   Play = run as the game would   pose slider = preview a frame without playing (not recorded; the game may override it)", UI.Dim);
            var poses = PosesOf(go.GetInstanceID());
            foreach (var r in stateRows)
            {
                var st = r.st;
                if (st == null) continue;
                ui.BeginRow(); ui.Space(12);
                if (ui.Button("Play", 44)) { an.Stop(); an.Play(r.name); ChangeRecorder.Action(go, "anim", null, r.name, "adapter"); animHeadAt = 0f; }
                if (ui.Button("Stop", 44)) { an.Stop(r.name); animHeadAt = 0f; }
                ui.Label(r.label, r.playing ? UI.Accent : UI.Txt, 260);
                float pose; poses.TryGetValue(r.name, out pose);
                float cur = r.playing ? st.normalizedTime % 1f : pose;
                float nv = ui.Slider(r.sliderId, cur, 0f, 1f, Mathf.Max(60, ui.Width - 4));
                if (Mathf.Abs(nv - cur) > 0.0005f && !an.IsPlaying(r.name))
                {
                    poses[r.name] = nv;
                    bool was = st.enabled; st.enabled = true; st.weight = 1f; st.normalizedTime = nv; an.Sample(); st.enabled = was;
                }
                ui.EndRow();
            }
        }

        // remembered pose slider positions, per object and clip (two objects with a clip of the same name do not share one)
        static readonly Dictionary<int, Dictionary<string, float>> poseByObject = new Dictionary<int, Dictionary<string, float>>();
        static Dictionary<string, float> PosesOf(int goId)
        {
            Dictionary<string, float> d;
            if (poseByObject.TryGetValue(goId, out d)) return d;
            if (poseByObject.Count > 200) poseByObject.Clear();
            poseByObject[goId] = d = new Dictionary<string, float>();
            return d;
        }

        // ---------------------------------------------------------------- Animator (Mecanim)
        static int paramsFor; static float paramsAt, animatorAt; static string animatorHead;
        static AnimatorControllerParameter[] animParams; static string[] paramLabels, paramValues;

        static void AnimatorA(UI ui, Animator an, float now)
        {
            int id = an.GetInstanceID();
            if (id != paramsFor || now - paramsAt > 1f)
            {
                paramsFor = id; paramsAt = now; animatorAt = 0f;
                animParams = null;
                try { animParams = an.parameters; } catch { }
                int n = animParams != null ? animParams.Length : 0;
                paramLabels = new string[n]; paramValues = new string[n];
                for (int i = 0; i < n; i++) paramLabels[i] = "   " + animParams[i].name;
            }
            if (now - animatorAt > 0.1f)
            {
                animatorAt = now;
                string clip = null; float nt = 0f;
                try { if (an.runtimeAnimatorController != null && an.isActiveAndEnabled) { var ci = an.GetCurrentAnimatorClipInfo(0); if (ci != null && ci.Length > 0 && ci[0].clip != null) clip = ci[0].clip.name; nt = an.GetCurrentAnimatorStateInfo(0).normalizedTime; } } catch { }
                animatorHead = "ANIMATOR  —  " + (an.runtimeAnimatorController != null ? an.runtimeAnimatorController.name : "no controller (it cannot animate anything)") + "   speed " + an.speed.ToString("0.##") + (an.enabled ? "" : "   [disabled]") + (clip != null ? "   now: " + clip + " " + (nt % 1f * 100f).ToString("0") + "%" : "");
                for (int i = 0; animParams != null && i < animParams.Length; i++)
                {
                    var p = animParams[i];
                    try { paramValues[i] = p.type == AnimatorControllerParameterType.Float ? an.GetFloat(p.name).ToString("0.###") : p.type == AnimatorControllerParameterType.Int ? an.GetInteger(p.name).ToString() : null; }
                    catch { paramValues[i] = "?"; }
                }
            }
            ui.Label(animatorHead, cHead);
            PlayedBy(an.gameObject, an.GetComponent<Animation>());
            ui.Label("   driven by:", UI.Dim);
            PlayedLines(ui);
            if (animParams == null || animParams.Length == 0) { ui.Label("   (no parameters)", UI.Dim); return; }
            for (int i = 0; i < animParams.Length; i++)
            {
                var p = animParams[i];
                ui.BeginRow();
                ui.Label(paramLabels[i], UI.Dim, 200);
                try
                {
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Bool:
                            bool b = an.GetBool(p.name); bool nb = ui.Toggle(b, b ? "true" : "false"); if (nb != b) an.SetBool(p.name, nb); break;
                        case AnimatorControllerParameterType.Trigger:
                            if (ui.Button("Set trigger")) an.SetTrigger(p.name); break;
                        default:
                            ui.Label(paramValues[i] ?? "?"); break;
                    }
                }
                catch { ui.Label("?"); }
                ui.EndRow();
            }
        }

        // ---------------------------------------------------------------- recent live events for this object
        struct RecentRow { public string text; public bool fresh; }
        static readonly List<RecentRow> recent = new List<RecentRow>();
        static int recentFor; static float recentAt; static long recentTotal = -1;

        static void Recent(UI ui, GameObject go, float now)
        {
            int id = go.GetInstanceID();
            // the event ring is searched when an event arrived, and four times per second for the "s ago" texts
            if (id != recentFor || recentTotal != EventMonitor.total || (recent.Count > 0 && now - recentAt > 0.25f))
            {
                recentFor = id; recentTotal = EventMonitor.total; recentAt = now; recent.Clear();
                int n = EventMonitor.Count;
                for (int i = 0; i < n && recent.Count < 6; i++)
                {
                    var e = EventMonitor.Get(i);
                    if (e.goId != id) continue;
                    recent.Add(new RecentRow { fresh = now - e.t < 2f, text = "   " + (now - e.t).ToString("0.0") + " s ago   " + (e.kind == 'S' ? "signal  " : "state   ") + e.text });
                }
            }
            if (recent.Count == 0) return;
            if (!Links.Section(ui, "recent", "RECENT EVENTS (live)", recent.Count, cHead)) return;
            for (int i = 0; i < recent.Count; i++) ui.Label(recent[i].text, recent[i].fresh ? Color.white : UI.Dim);
        }
    }
}
