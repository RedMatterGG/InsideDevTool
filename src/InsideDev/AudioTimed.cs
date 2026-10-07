using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    // Every live system that is timed or driven by audio:
    //  * components holding MusicEventChecker timers (fire when the music play position crosses eventTime_s)
    //  * components that read the music position directly (rotating covers, elevator, voice reactions...)
    //  * PlayMaker WaitForMusicTime / WaitForMusicCueOrMarker / WaitForBeat (via AudioLinks)
    // For each timer: live countdown, fire log, Fire now (retime just ahead), Mute (never fires), retime slider.
    public static class AudioTimed
    {
        public class Timer
        {
            public string field;
            public MusicEventChecker chk;
            public float original;
            public bool muted;
            public float forcedUntil = -1;
            public float lastFired = -1;
            public int fireCount;
        }
        public class Sys
        {
            public MonoBehaviour comp;
            public string typeName;
            public string desc;
            public readonly List<Timer> timers = new List<Timer>();
        }

        public static readonly List<Sys> systems = new List<Sys>();
        static readonly Dictionary<Type, FieldInfo[]> checkerFields = new Dictionary<Type, FieldInfo[]>();
        static float nextScan; static int scannedStream = -1;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // types that read the music position without a MusicEventChecker (found in the decompiled code)
        static readonly Dictionary<string, string> known = new Dictionary<string, string>
        {
            { "ForcePushManager", "Shockwave: blow (kill check) / incoming wall / reset, locked to the music loop" },
            { "WaterFlushTunnel", "Flush tunnel water stream on/off -> signals startStream / stopStream" },
            { "AudioManagerFlushTunnel", "Flush tunnel: water open/close + door open/close timed to the music" },
            { "MusicLoopPulseListener", "Generic on/off pulse following the music loop" },
            { "MinesCraneShield", "Crane shield position follows the music position" },
            { "ShieldMusicRotater", "Rotating cover: rotation angle = music position (cycle / cycles per rotation)" },
            { "ShieldMusicRotaterDynamic", "Rotating cover (dynamic) driven by music position" },
            { "MinesRotater", "Rotating structure phase-locked to the music (catches up with boy velocity)" },
            { "ElevatorFall", "Elevator drop sequence timed to the music -> readyDoor / hitGround / prepareVoice" },
            { "ForcePushSurviveSound", "Voice reaction timing vs shockwave -> firstSurvive / thirdSurvive / sureDeathLand" },
            { "ForcePushRotaterVoiceLogic", "Voice logic around rotating cover -> enterCover / exitCover / closeCall" },
            { "ForcePushGetWagonVoiceLogic", "Voice logic in wagon shockwave area -> closeCall / enterCover / exitCover ..." },
            { "BoyVoiceSequencer", "Boy breathing: each breath callback drives the additive breathing animation" },
            { "HuddleSequencer", "Huddle voice/foley sequencer: audio markers drive huddle reactions" },
            { "AudioSequenceStopSelector", "Stops a sequence on the next sound/marker boundary" },
            { "TestMusicTime", "Dev test: music time checker" },
            { "MusicTimeTest", "Dev test: music time" },
        };

        public static void Scan(bool force = false)
        {
            // Phase 12: FindObjectsOfTypeAll(MonoBehaviour) costs 10-20 ms, so the background scan runs only when
            // streaming changed (new areas bring new music-timed systems) or every 60 s; the Audio tab forces it
            if (!force && Time.realtimeSinceStartup < nextScan && !Levels.Changed(ref scannedStream, 1f)) return;
            nextScan = Time.realtimeSinceStartup + (force ? 1f : 60f);
            // keep existing Timer objects (their mute/force state) for components that still exist
            var old = new Dictionary<int, Sys>();
            foreach (var s in systems) if (s.comp != null) old[s.comp.GetInstanceID()] = s;
            systems.Clear();
            try
            {
                // FindObjectsOfTypeAll also returns components on inactive (hidden) objects and disabled components
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour)))
                {
                    var mb = o as MonoBehaviour;
                    if (mb == null || mb.hideFlags != HideFlags.None || mb.gameObject.hideFlags != HideFlags.None) continue;
                    var t = mb.GetType();
                    var fields = CheckerFields(t);
                    string desc;
                    bool isKnown = known.TryGetValue(t.Name, out desc);
                    if (fields.Length == 0 && !isKnown) continue;
                    Sys prev;
                    if (old.TryGetValue(mb.GetInstanceID(), out prev)) { systems.Add(prev); continue; }
                    var sys = new Sys { comp = mb, typeName = t.Name, desc = desc ?? "Has music-time triggers" };
                    foreach (var f in fields)
                    {
                        var c = f.GetValue(mb) as MusicEventChecker;
                        if (c != null) sys.timers.Add(new Timer { field = f.Name, chk = c, original = c.eventTime_s });
                    }
                    systems.Add(sys);
                }
                systems.Sort((a, b) => string.CompareOrdinal(a.typeName, b.typeName));
            }
            catch (Exception e) { DevLog.Error("AudioTimed.Scan", e); nextScan = Time.realtimeSinceStartup + 30f; }
        }

        static FieldInfo[] CheckerFields(Type t)
        {
            FieldInfo[] r;
            if (checkerFields.TryGetValue(t, out r)) return r;
            var list = new List<FieldInfo>();
            for (var ty = t; ty != null && ty != typeof(MonoBehaviour); ty = ty.BaseType)
                foreach (var f in ty.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (f.FieldType == typeof(MusicEventChecker)) list.Add(f);
            r = list.ToArray();
            checkerFields[t] = r;
            return r;
        }

        static MusicManager Music { get { try { return PersistentBehaviour<GlobalAudio>.instance.music; } catch { return null; } } }

        // LateUpdate: owners call checker.Update() in their Update, so the flag is visible now
        public static void LateUpdate()
        {
            Scan();   // background refresh (every 3s) so TIMED firings are logged even when the tab is closed
            float now = Time.realtimeSinceStartup;
            foreach (var s in systems)
            {
                if (s.comp == null) continue;
                foreach (var tm in s.timers)
                {
                    bool fired = false;
                    try { fired = tm.chk.didEventOccurThisUpdate; } catch { }
                    if (fired)
                    {
                        tm.lastFired = now; tm.fireCount++;
                        AudioSpy.AddTimed(s.typeName + "." + tm.field + " @" + tm.original.ToString("0.00", IC) + "s", s.comp.gameObject.name, Outgoing(s.comp.gameObject));
                        if (tm.forcedUntil > 0) { tm.chk.SetEventTime(tm.muted ? float.NaN : tm.original); tm.forcedUntil = -1; }
                    }
                    else if (tm.forcedUntil > 0 && now > tm.forcedUntil)
                    {
                        tm.chk.SetEventTime(tm.muted ? float.NaN : tm.original); tm.forcedUntil = -1;
                    }
                }
            }
        }

        public static void FireNow(Timer tm)
        {
            var m = Music; if (m == null) return;
            float pos = m.GetMusicPosition_s();
            float loop = m.GetLoopLength_s();
            float t = pos + 0.12f;
            if (loop > 0.2f) t = Mathf.Repeat(t, loop);
            tm.chk.SetEventTime(t);
            tm.forcedUntil = Time.realtimeSinceStartup + 1.5f;
            DevLog.Write("audio-timed: forcing " + tm.field + " (was @" + tm.original.ToString("0.00", IC) + "s)");
        }

        public static void SetMuted(Timer tm, bool mute)
        {
            tm.muted = mute;
            tm.chk.SetEventTime(mute ? float.NaN : tm.original);
            DevLog.Write("audio-timed: " + tm.field + (mute ? " muted (will never fire)" : " restored"));
        }

        public static string Outgoing(GameObject go)
        {
            SignalManager sm = null;
            try { sm = PersistentBehaviour<SignalManager>.instance; } catch { }
            if (sm == null || sm.connections == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var c in sm.connections)
            {
                if (c == null || !c.isActive || c.signalOutGameObject != go) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(c.signalOutName).Append(" -> ").Append(c.signalInGameObject != null ? c.signalInGameObject.name : "?").Append('.').Append(c.signalInName);
                if (sb.Length > 300) { sb.Append(" ..."); break; }
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- UI
        static readonly HashSet<int> open = new HashSet<int>();
        static readonly Dictionary<string, string> exact = new Dictionary<string, string>();
        static readonly HashSet<string> openAreas = new HashSet<string>();
        static string filter = "";

        public static void Draw(UI ui, DevCore core)
        {
            Scan();
            AudioLinks.Scan();
            var m = Music;
            float pos = m != null ? m.GetMusicPosition_s() : -1f;
            float loop = m != null ? m.GetLoopLength_s() : 0f;
            ui.BeginRow();
            if (ui.Button("Rescan")) { Scan(true); AudioLinks.Scan(true); }
            ui.Label("Filter:", null, 44);
            ui.TextField("atf", ref filter, 160);
            ui.Label(string.Format(IC, "   music {0:0.00}s / loop {1:0.00}s     {2} audio-driven components, {3} FSM audio waits", pos, loop, systems.Count, AudioLinks.waits.Count), UI.Dim);
            ui.EndRow();
            ui.Label("Fire now = retime the trigger to fire ~0.1s from now (then restores).  Mute = it never fires (restorable).  Every firing is logged (TIMED) in the Live log.", UI.Dim);
            string f = filter.ToLowerInvariant();
            ui.BeginScroll("atimed", ui.Remaining);

            // shockwave gets its dedicated panel on top when present
            if (Shockwave.Mgr != null && (f.Length == 0 || "shockwave forcepush".Contains(f)))
            {
                Shockwave.Draw(ui);
                ui.Space(10);
            }

            foreach (var s in systems)
            {
                if (s.comp == null) continue;
                string head = s.typeName + "  on  " + s.comp.gameObject.name;
                if (f.Length > 0 && !(head + " " + s.desc).ToLowerInvariant().Contains(f)) continue;
                int id = s.comp.GetInstanceID();
                bool isOpen = open.Contains(id) || f.Length > 0;
                bool anyRecent = false;
                foreach (var tm in s.timers) if (tm.lastFired > 0 && Time.realtimeSinceStartup - tm.lastFired < 0.6f) anyRecent = true;
                ui.BeginRow();
                bool en = ui.Toggle(s.comp.enabled, "", 20);
                if (en != s.comp.enabled) { Changes.Record(s.comp.gameObject, s.comp, s.typeName, s.comp.enabled); s.comp.enabled = en; }
                if (ui.Item((isOpen ? "v " : "> ") + head + (s.comp.gameObject.activeInHierarchy ? (s.comp.enabled ? "" : "  (disabled)") : "  (hidden / not active)") + "   [" + AudioCatalog.AreaOf(s.comp.gameObject) + "]", anyRecent ? new Color(1f, 0.5f, 0.3f, 1f) : new Color(1f, 0.85f, 0.3f, 1f), ui.Width - 90))
                { if (open.Contains(id)) open.Remove(id); else open.Add(id); }
                if (ui.Button("inspect", 70)) core.SelectInInspector(s.comp.gameObject);
                ui.EndRow();
                if (!isOpen) continue;
                ui.Label("      " + s.desc, UI.Dim);
                foreach (var tm in s.timers)
                {
                    float at = tm.muted ? float.NaN : tm.original;
                    float next = (loop > 0.2f && !tm.muted) ? Mathf.Repeat(at - pos, loop) : -1f;
                    bool recent = tm.lastFired > 0 && Time.realtimeSinceStartup - tm.lastFired < 0.6f;
                    ui.BeginRow();
                    ui.Label(string.Format(IC, "      {0} @ {1}   {2}   fired {3}x{4}", tm.field, tm.muted ? "MUTED" : tm.original.ToString("0.00", IC) + "s",
                        next >= 0 ? "next in " + next.ToString("0.00", IC) + "s" : "", tm.fireCount, recent ? "   <<< FIRED" : ""),
                        recent ? new Color(1f, 0.5f, 0.3f, 1f) : tm.muted ? UI.Dim : UI.Txt, ui.Width - 250);
                    if (ui.Button("Fire now", 80)) FireNow(tm);
                    if (ui.Button(tm.muted ? "Unmute" : "Mute", 70)) SetMuted(tm, !tm.muted);
                    ui.EndRow();
                    if (!tm.muted)
                    {
                        ui.BeginRow();
                        ui.Space(40);
                        string key = "at:" + id + tm.field;
                        string txt;
                        if (!exact.TryGetValue(key, out txt)) txt = tm.original.ToString("0.###", IC);
                        ui.Label("exact s:", UI.Dim, 60);
                        bool enter = ui.TextField(key, ref txt, 90);
                        exact[key] = txt;
                        if (ui.Button("set", 40) || enter)
                        {
                            float v;
                            if (float.TryParse(txt, NumberStyles.Float, IC, out v)) { tm.original = v; tm.chk.SetEventTime(v); exact.Remove(key); DevLog.Write("audio-timed: " + tm.field + " -> " + v.ToString("0.###", IC) + "s"); }
                        }
                        if (loop > 0.2f)
                        {
                            float nv = ui.Slider(key + "s", Mathf.Clamp(tm.original, 0f, loop), 0f, loop, ui.Width - 10);
                            if (Mathf.Abs(nv - Mathf.Clamp(tm.original, 0f, loop)) > 0.01f) { tm.original = nv; tm.chk.SetEventTime(nv); exact.Remove(key); }
                        }
                        ui.EndRow();
                    }
                }
                string outs = Outgoing(s.comp.gameObject);
                if (outs.Length > 0) ui.Label("      signals: " + outs, new Color(1f, 0.75f, 0.35f, 1f));
                // tunable public floats (cycle lengths, offsets, speeds)
                foreach (var fi in s.comp.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (fi.FieldType != typeof(float) && fi.FieldType != typeof(int)) continue;
                    string n = fi.Name.ToLowerInvariant();
                    if (!(n.Contains("cycle") || n.Contains("offset") || n.Contains("time") || n.Contains("speed") || n.Contains("strength"))) continue;
                    ui.BeginRow();
                    ui.Label("      " + fi.Name + " = " + Convert.ToString(fi.GetValue(s.comp), IC), UI.Dim, 280);
                    if (ui.Button("-", 24)) Nudge(s.comp, fi, -1);
                    if (ui.Button("+", 24)) Nudge(s.comp, fi, +1);
                    ui.EndRow();
                }
            }

            // PlayMaker audio waits (cue / beat / music time)
            ui.Space(8);
            ui.Label("PlayMaker states waiting on audio (green = waiting right now)", UI.Dim);
            foreach (var w in AudioLinks.waits)
            {
                if (w.fsm == null) continue;
                string head = w.kind + "  ->  " + w.fsm.gameObject.name + "/" + w.fsm.FsmName + " [" + w.state + "] fires '" + w.finishEvent + "'";
                if (f.Length > 0 && !head.ToLowerInvariant().Contains(f)) continue;
                bool live = false;
                try { live = w.fsm.ActiveStateName == w.state && w.fsm.enabled && w.fsm.gameObject.activeInHierarchy; } catch { }
                ui.BeginRow();
                if (w.isCue) { if (ui.Button("Inject cue", 90)) AudioSpy.InjectCue(w.any || string.IsNullOrEmpty(w.cue) ? "__any__" : w.cue); }
                else if (w.isBeat) { if (ui.Button("Inject beat", 90)) AudioSpy.InjectBeat(); }
                else ui.Space(94);
                if (ui.Button("Send event", 90)) { DevLog.Write("fsm " + w.fsm.gameObject.name + " <- " + w.finishEvent); w.fsm.SendEvent(w.finishEvent); }
                if (ui.Item(head + (live ? "   <- WAITING NOW" : "") + "   [" + AudioCatalog.AreaOf(w.fsm.gameObject) + "]", live ? new Color(0.5f, 1f, 0.6f, 1f) : UI.Txt)) core.SelectInInspector(w.fsm.gameObject);
                ui.EndRow();
            }

            // ---- whole game (catalog)
            AudioCatalog.Load();
            ui.Space(12);
            ui.BeginRow();
            int total = 0; foreach (var kv in AudioCatalog.byArea) total += kv.Value.Count;
            ui.Label("ALL AREAS (catalog): " + AudioCatalog.byArea.Count + " area(s), " + total + " audio trigger(s) recorded", new Color(1f, 0.85f, 0.3f, 1f), ui.Width - 330);
            if (Discovery.Busy) { ui.Label(Discovery.status, new Color(1f, 0.85f, 0.4f, 1f), 250); if (ui.Button("Cancel", 60)) Discovery.Cancel(); }
            else if (ui.Button("Catalog all areas", 150)) AudioCatalog.CatalogAll();
            ui.EndRow();
            ui.Label("Areas get recorded automatically as you play; 'Catalog all areas' loads each area once (takes a few minutes) so the list covers the whole game.", UI.Dim);
            var subIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (G.SavepointsReady) for (int i = 0; i < SavepointManager.SubsceneCount; i++) { string b = SavepointManager.GetSubsceneName(i).TrimStart('#'); if (!subIdx.ContainsKey(b)) subIdx[b] = i; }
            var areas = new List<string>(AudioCatalog.byArea.Keys);
            areas.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var area in areas)
            {
                var list = AudioCatalog.byArea[area];
                bool areaMatch = f.Length == 0 || area.ToLowerInvariant().Contains(f);
                var shown = new List<AudioCatalog.Entry>();
                foreach (var e in list) if (areaMatch || (e.obj + " " + e.detail).ToLowerInvariant().Contains(f)) shown.Add(e);
                if (shown.Count == 0) continue;
                bool isOpen = openAreas.Contains(area) || f.Length > 0;
                int sub;
                bool hasSub = subIdx.TryGetValue(area, out sub);
                ui.BeginRow();
                if (ui.Item((isOpen ? "v " : "> ") + area + "   (" + shown.Count + ")", UI.Accent, ui.Width - 75))
                { if (openAreas.Contains(area)) openAreas.Remove(area); else openAreas.Add(area); }
                if (hasSub && ui.Button("Spawn", 65)) G.LoadSavepoint(sub, 0);
                ui.EndRow();
                if (!isOpen) continue;
                foreach (var e in shown)
                {
                    Color c = e.kind == "timer" ? new Color(1f, 0.6f, 0.35f, 1f) : e.kind == "cue" ? new Color(1f, 0.85f, 0.3f, 1f) : e.kind == "system" ? UI.Txt : UI.Accent;
                    ui.Label("      " + e.kind.PadRight(9) + " " + e.detail + "    (" + e.obj + ")", c);
                }
            }
            ui.EndScroll();
        }

        static void Nudge(object o, FieldInfo fi, int dir)
        {
            if (fi.FieldType == typeof(int)) fi.SetValue(o, (int)fi.GetValue(o) + dir);
            else { float v = (float)fi.GetValue(o); float step = Mathf.Max(0.05f, Mathf.Abs(v) * 0.1f); fi.SetValue(o, v + dir * step); }
            DevLog.Write("set " + fi.DeclaringType.Name + "." + fi.Name + " = " + fi.GetValue(o));
        }
    }
}
