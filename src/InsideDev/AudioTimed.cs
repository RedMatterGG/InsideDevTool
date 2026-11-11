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
    // The list is kept up to date and firings are logged only while somebody uses the tool (Background.Timed, the Audio
    // tab, a command) or with editor.cfg audio.timed=always; asleep it costs one test per frame.
    public static class AudioTimed
    {
        public class Timer
        {
            public string field;
            public MusicEventChecker chk;
            public MonoBehaviour owner;     // the component that holds the checker
            public float original;          // the time it fires at (follows every edit, also undo and the Inspector)
            public float game;              // the time the game gave it when it was first seen
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
        // number of completed scans (the catalog records after each one)
        public static int ScanCount;
        static readonly Dictionary<Type, FieldInfo[]> checkerFields = new Dictionary<Type, FieldInfo[]>();
        static readonly Dictionary<Type, FieldInfo[]> tunableFields = new Dictionary<Type, FieldInfo[]>();
        static readonly Dictionary<int, Sys> oldById = new Dictionary<int, Sys>();
        static readonly List<Timer> forcedTimers = new List<Timer>();
        static HashSet<string> timedTypeNames;
        static int scannedStream = -1, needPass;
        static bool everScanned, pending;
        static float pendingSince, retryAt;
        static int mode = -1;   // 0 auto, 1 always
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        // seconds the background scan waits for a fresh object database pass before it searches the engine itself
        const float DatabaseWait = 10f;

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

        public static void ReloadConfig() { mode = -1; }

        // somebody looks at the timed systems (panel, command, client, the catalog), or the user asked for it for good
        static bool Awake
        {
            get
            {
                if (mode < 0) mode = EditorState.Get("audio.timed", "auto") == "always" ? 1 : 0;
                return mode == 1 || AudioCatalog.Always || Background.WantedOrInUse(Background.Timed) || Background.Wanted(Background.Catalog);
            }
        }

        // force: search the engine now (a command, the Rescan button, a catalog job). Otherwise the background refresh: it runs
        // once after a streaming change (new areas bring new music-timed systems) and takes its candidates from the object
        // database, because Resources.FindObjectsOfTypeAll(MonoBehaviour) is a 10-20 ms search that leaves a large array behind.
        public static void Scan(bool force = false)
        {
            if (force) { FullScan(); return; }
            float now = Time.realtimeSinceStartup;
            if (now < retryAt) return;
            if (!DevCore.BackgroundOk) { if (pending) pendingSince = now; return; }
            if (Levels.Changed(ref scannedStream, 1f) || (!everScanned && !pending))
            {
                if (!pending) pendingSince = now;
                pending = true;
                needPass = ObjectDatabase.completedPasses + (ObjectDatabase.Scanning ? 2 : 1);
            }
            if (!pending) return;
            if (ObjectDatabase.completedPasses >= needPass) { ScanRecords(); return; }
            if (now - pendingSince > DatabaseWait) { FullScan(); return; }
            ObjectDatabase.Demand(2f);
            Background.Want(Background.Objects, 2f);
        }

        static void BeginScan()
        {
            // keep existing Timer objects (their fire counts and forced state) for components that still exist
            oldById.Clear();
            foreach (var s in systems) if (s.comp != null) oldById[s.comp.GetInstanceID()] = s;
            systems.Clear();
        }

        static void EndScan()
        {
            oldById.Clear();
            systems.Sort((a, b) => string.CompareOrdinal(a.typeName, b.typeName));
            everScanned = true; pending = false;
            scannedStream = Levels.StreamVersion;
            ScanCount++;
            foreach (var s in systems) foreach (var tm in s.timers) Sync(tm);
        }

        static void FullScan()
        {
            BeginScan();
            try
            {
                // FindObjectsOfTypeAll also returns components on inactive (hidden) objects and disabled components
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour))) Consider(o as MonoBehaviour);
                EndScan();
            }
            catch (Exception e) { DevLog.Error("AudioTimed.Scan", e); pending = false; retryAt = Time.realtimeSinceStartup + 30f; }
        }

        static void ScanRecords()
        {
            BeginScan();
            try
            {
                var names = TimedTypeNames();
                foreach (var r in ObjectDatabase.all)
                {
                    var comps = r.comps;
                    bool hit = false;
                    for (int i = 0; i < comps.Length; i++) if (names.Contains(comps[i])) { hit = true; break; }
                    if (!hit || r.go == null) continue;
                    foreach (var mb in r.go.GetComponents<MonoBehaviour>()) Consider(mb);
                }
                EndScan();
            }
            catch (Exception e) { DevLog.Error("AudioTimed.Scan", e); pending = false; retryAt = Time.realtimeSinceStartup + 30f; }
        }

        static void Consider(MonoBehaviour mb)
        {
            if (mb == null || mb.hideFlags != HideFlags.None || mb.gameObject.hideFlags != HideFlags.None) return;
            var t = mb.GetType();
            var fields = CheckerFields(t);
            string desc;
            bool isKnown = known.TryGetValue(t.Name, out desc);
            if (fields.Length == 0 && !isKnown) return;
            Sys prev;
            if (oldById.TryGetValue(mb.GetInstanceID(), out prev)) { systems.Add(prev); return; }
            var sys = new Sys { comp = mb, typeName = t.Name, desc = desc ?? "Has music-time triggers" };
            foreach (var f in fields)
            {
                var c = f.GetValue(mb) as MusicEventChecker;
                if (c != null) sys.timers.Add(new Timer { field = f.Name, chk = c, owner = mb, original = c.eventTime_s, game = c.eventTime_s });
            }
            systems.Add(sys);
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

        // Names of the component types a scan looks for: the known ones and every game script that holds (or inherits) a
        // MusicEventChecker field. Built once from the game's assemblies; the object database stores type names per object.
        static HashSet<string> TimedTypeNames()
        {
            if (timedTypeNames != null) return timedTypeNames;
            var set = new HashSet<string>(known.Keys);
            var declares = new Dictionary<Type, bool>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    if (!asm.GetName().Name.StartsWith("Assembly-CSharp", StringComparison.Ordinal)) continue;
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t == null || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    for (var ty = t; ty != null && ty != typeof(MonoBehaviour); ty = ty.BaseType)
                    {
                        bool d;
                        if (!declares.TryGetValue(ty, out d))
                        {
                            try
                            {
                                foreach (var f in ty.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                                    if (f.FieldType == typeof(MusicEventChecker)) { d = true; break; }
                            }
                            catch { }
                            declares[ty] = d;
                        }
                        if (d) { set.Add(t.Name); break; }
                    }
                }
            }
            timedTypeNames = set;
            return set;
        }

        static MusicManager Music { get { try { return PersistentBehaviour<GlobalAudio>.instance.music; } catch { return null; } } }

        // The game's own value is the truth: an undo, a mod or the Inspector may have written the time meanwhile.
        static void Sync(Timer tm)
        {
            if (tm.forcedUntil > 0) return;
            float cur = tm.chk.eventTime_s;
            if (float.IsNaN(cur)) tm.muted = true;
            else { tm.muted = false; tm.original = cur; }
        }

        // LateUpdate: owners call checker.Update() in their Update, so the flag is visible now
        public static void LateUpdate()
        {
            if (forcedTimers.Count > 0) RestoreForced();
            if (!Awake) return;
            Scan();   // background refresh after a streaming change, so TIMED firings are logged while the tab is closed
            float now = Time.realtimeSinceStartup;
            for (int si = 0; si < systems.Count; si++)
            {
                var s = systems[si];
                if (s.comp == null) continue;
                for (int ti = 0; ti < s.timers.Count; ti++)
                {
                    var tm = s.timers[ti];
                    bool fired = false;
                    try { fired = tm.chk.didEventOccurThisUpdate; } catch { }
                    if (fired)
                    {
                        tm.lastFired = now; tm.fireCount++;
                        if (AudioSpy.Listening) AudioSpy.AddTimed(s.typeName + "." + tm.field + " @" + tm.original.ToString("0.00", IC) + "s", s.comp.gameObject.name, Outgoing(s.comp.gameObject));
                    }
                    Sync(tm);
                }
            }
        }

        // A timer retimed by "Fire now" goes back to its own time once it fired or after 1.5 s, also while nobody watches.
        static void RestoreForced()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = forcedTimers.Count - 1; i >= 0; i--)
            {
                var tm = forcedTimers[i];
                bool gone = tm.owner == null, fired = false;
                if (!gone) { try { fired = tm.chk.didEventOccurThisUpdate; } catch { } }
                if (!gone && !fired && now <= tm.forcedUntil) continue;
                if (!gone) tm.chk.SetEventTime(tm.muted ? float.NaN : tm.original);
                tm.forcedUntil = -1;
                forcedTimers.RemoveAt(i);
            }
        }

        public static void FireNow(Timer tm)
        {
            var m = Music; if (m == null) return;
            Sync(tm);
            float pos = m.GetMusicPosition_s();
            float loop = m.GetLoopLength_s();
            float t = pos + 0.12f;
            if (loop > 0.2f) t = Mathf.Repeat(t, loop);
            tm.chk.SetEventTime(t);
            tm.forcedUntil = Time.realtimeSinceStartup + 1.5f;
            if (!forcedTimers.Contains(tm)) forcedTimers.Add(tm);
            DevLog.Write("audio-timed: forcing " + tm.field + " (was @" + (tm.muted ? "MUTED" : tm.original.ToString("0.00", IC) + "s") + ")");
        }

        // the change goes into the history (undo, revert all, record as mod) as a field edit of the owner component
        static void BeforeEdit(Timer tm, string source)
        {
            if (tm.owner != null) ChangeRecorder.BeforeField(tm.owner, tm.field + ".eventTime_s", source);
        }

        public static void SetMuted(Timer tm, bool mute) { SetMuted(tm, mute, "audio"); }
        public static void SetMuted(Timer tm, bool mute, string source)
        {
            Sync(tm);
            if (tm.forcedUntil <= 0) { BeforeEdit(tm, source); tm.chk.SetEventTime(mute ? float.NaN : tm.original); }
            tm.muted = mute;
            DevLog.Write("audio-timed: " + tm.field + (mute ? " muted (will never fire)" : " restored"));
        }

        // new time in seconds; also ends a mute
        public static void SetTime(Timer tm, float seconds, string source)
        {
            if (tm.forcedUntil <= 0) { BeforeEdit(tm, source); tm.chk.SetEventTime(seconds); }
            tm.muted = false;
            tm.original = seconds;
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
        // lists of the catalog part, rebuilt when the catalog or the filter changes
        static readonly Dictionary<string, int> subIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static int subIdxCount = -1;
        static readonly List<string> catAreas = new List<string>();
        static readonly Dictionary<string, List<AudioCatalog.Entry>> catShown = new Dictionary<string, List<AudioCatalog.Entry>>();
        static int catVersion = -1, catTotal;
        static string catFilter;

        static FieldInfo[] Tunable(Type t)
        {
            FieldInfo[] r;
            if (tunableFields.TryGetValue(t, out r)) return r;
            var list = new List<FieldInfo>();
            foreach (var fi in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (fi.FieldType != typeof(float) && fi.FieldType != typeof(int)) continue;
                string n = fi.Name.ToLowerInvariant();
                if (n.Contains("cycle") || n.Contains("offset") || n.Contains("time") || n.Contains("speed") || n.Contains("strength")) list.Add(fi);
            }
            r = list.ToArray();
            tunableFields[t] = r;
            return r;
        }

        static void BuildCatalogLists(string f)
        {
            if (G.SavepointsReady && SavepointManager.SubsceneCount != subIdxCount)
            {
                subIdxCount = SavepointManager.SubsceneCount;
                subIdx.Clear();
                for (int i = 0; i < subIdxCount; i++) { string b = SavepointManager.GetSubsceneName(i).TrimStart('#'); if (!subIdx.ContainsKey(b)) subIdx[b] = i; }
            }
            if (catVersion == AudioCatalog.Version && catFilter == f) return;
            catVersion = AudioCatalog.Version; catFilter = f;
            catAreas.Clear(); catShown.Clear(); catTotal = 0;
            foreach (var kv in AudioCatalog.byArea)
            {
                catTotal += kv.Value.Count;
                List<AudioCatalog.Entry> shown = kv.Value;
                if (f.Length > 0 && !kv.Key.ToLowerInvariant().Contains(f))
                {
                    shown = new List<AudioCatalog.Entry>();
                    foreach (var e in kv.Value) if ((e.obj + " " + e.detail).ToLowerInvariant().Contains(f)) shown.Add(e);
                }
                if (shown.Count == 0) continue;
                catAreas.Add(kv.Key);
                catShown[kv.Key] = shown;
            }
            catAreas.Sort(StringComparer.OrdinalIgnoreCase);
        }

        public static void Draw(UI ui, DevCore core)
        {
            Background.Want(Background.Timed, 5f);
            Background.Want(Background.Catalog, 5f);
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
                            if (Fmt.TryF(txt, out v)) { SetTime(tm, v, "audio"); exact.Remove(key); DevLog.Write("audio-timed: " + tm.field + " -> " + v.ToString("0.###", IC) + "s"); }
                        }
                        if (loop > 0.2f)
                        {
                            float nv = ui.Slider(key + "s", Mathf.Clamp(tm.original, 0f, loop), 0f, loop, ui.Width - 10);
                            if (Mathf.Abs(nv - Mathf.Clamp(tm.original, 0f, loop)) > 0.01f) { SetTime(tm, nv, "audio"); exact.Remove(key); }
                        }
                        ui.EndRow();
                    }
                }
                string outs = Outgoing(s.comp.gameObject);
                if (outs.Length > 0) ui.Label("      signals: " + outs, new Color(1f, 0.75f, 0.35f, 1f));
                // tunable public floats (cycle lengths, offsets, speeds)
                foreach (var fi in Tunable(s.comp.GetType()))
                {
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
                if (ui.Button("Send event", 90)) { DevLog.Write("fsm " + w.fsm.gameObject.name + " <- " + w.finishEvent); ChangeRecorder.Action(w.fsm.gameObject, "fsmEvent", w.fsm.FsmName, w.finishEvent, "audio"); w.fsm.SendEvent(w.finishEvent); }
                if (ui.Item(head + (live ? "   <- WAITING NOW" : "") + "   [" + AudioCatalog.AreaOf(w.fsm.gameObject) + "]", live ? new Color(0.5f, 1f, 0.6f, 1f) : UI.Txt)) core.SelectInInspector(w.fsm.gameObject);
                ui.EndRow();
            }

            // ---- whole game (catalog)
            AudioCatalog.Load();
            BuildCatalogLists(f);
            ui.Space(12);
            ui.BeginRow();
            ui.Label("ALL AREAS (catalog): " + AudioCatalog.byArea.Count + " area(s), " + catTotal + " audio trigger(s) recorded", new Color(1f, 0.85f, 0.3f, 1f), ui.Width - 330);
            if (Discovery.Busy) { ui.Label(Discovery.status, new Color(1f, 0.85f, 0.4f, 1f), 250); if (ui.Button("Cancel", 60)) Discovery.Cancel(); }
            else if (ui.Button("Catalog all areas", 150)) AudioCatalog.CatalogAll();
            ui.EndRow();
            ui.Label("Areas you play through are added while the editor is in use; 'Catalog all areas' loads each area once (takes a few minutes) so the list covers the whole game.", UI.Dim);
            foreach (var area in catAreas)
            {
                var shown = catShown[area];
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

        static void Nudge(MonoBehaviour o, FieldInfo fi, int dir)
        {
            ChangeRecorder.BeforeField(o, fi.Name, "audio");
            if (fi.FieldType == typeof(int)) fi.SetValue(o, (int)fi.GetValue(o) + dir);
            else { float v = (float)fi.GetValue(o); float step = Mathf.Max(0.05f, Mathf.Abs(v) * 0.1f); fi.SetValue(o, v + dir * step); }
            DevLog.Write("set " + fi.DeclaringType.Name + "." + fi.Name + " = " + Fmt.Any(fi.GetValue(o)));
        }
    }
}
