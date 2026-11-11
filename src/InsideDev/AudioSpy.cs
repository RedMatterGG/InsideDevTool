using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using Playdead.Audio;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    // Wraps Playdead's sound-engine implementation (SoundEngine.implementation) to:
    //  * log every event post / state / switch / RTPC call with its source object,
    //  * wrap the callback handlers so every Wwise callback (cue marker, beat, end-of-event, duration) is logged
    //    together with who receives it,
    //  * inject fake cue markers / beats exactly where real ones arrive (right after the engine update),
    //    which is what the level state machines' WaitForMusicCueOrMarker / WaitForBeat actions listen for.
    // Every sound event of the game passes through here, so the log, the callback wrappers and the caller stacks are only
    // made while somebody uses the tool (Background.Audio) or with editor.cfg audio.log=always; asleep a post is forwarded
    // without one allocation. Audio rules and cue injection work in both states. editor.cfg audio.spy=0 leaves the
    // wrapper out altogether (then the audio log, the audio rules and cue injection do not exist in that session).
    public class AudioSpy : ISoundEngineImplementation
    {
        public readonly ISoundEngineImplementation inner;
        public AudioSpy(ISoundEngineImplementation inner) { this.inner = inner; }

        // ---------------------------------------------------------------- install
        public static AudioSpy Instance;
        static bool offLogged;
        public static bool Install()
        {
            try
            {
                if (!EditorState.Get("audio.spy", true))
                {
                    if (!offLogged) { offLogged = true; DevLog.Write("audio: spy not installed (editor.cfg audio.spy=0)"); }
                    return false;
                }
                var cur = SoundEngine.GetImplementationDebug();
                if (cur == null) return false;
                if (cur is AudioSpy) { Instance = (AudioSpy)cur; return true; }
                Instance = new AudioSpy(cur);
                SoundEngine.ReplaceImplementation(Instance);
                LearnStatics(typeof(SoundEngine), 0);
                DevLog.Write("audio: spy installed around " + cur.GetType().Name + ", " + names.Count + " names known");
                return true;
            }
            catch (Exception e) { DevLog.Error("audio install", e); return false; }
        }

        // ---------------------------------------------------------------- log
        public struct Entry
        {
            public float time; public int frame; public string kind; public string name; public string source; public string detail;
        }
        public static readonly List<Entry> log = new List<Entry>(1024);
        public const int MaxLog = 1500;
        public static bool logRtpc, logSwitches = true, logStates = true, logVoice;
        public static int totalPosts, totalCallbacks;
        static readonly Dictionary<uint, string> names = new Dictionary<uint, string>();
        public static readonly HashSet<string> knownEvents = new HashSet<string>();

        public static string Resolve(uint id)
        {
            string n;
            return names.TryGetValue(id, out n) ? n : "#" + id.ToString("X8");
        }

        // ---------------------------------------------------------------- awake / asleep
        static int listenFrame = -1, logMode = -1;
        static bool listening;
        static float lastListened = -1f;

        public static void ReloadConfig()
        {
            logMode = EditorState.Get("audio.log", "auto") == "always" ? 1 : 0;
            listenFrame = -1;
        }

        public static void ReloadStacks()
        {
            string st = EditorState.Get("audio.stacks", "important");
            stackMode = st == "off" ? 0 : st == "full" ? 2 : 1;
        }

        // True while the log is being written: the tool is in use, the audio log is wanted, or audio.log=always.
        // Asked once per frame at most. Main thread (the game posts its sounds there).
        public static bool Listening
        {
            get
            {
                int f = Time.frameCount;
                if (f == listenFrame) return listening;
                listenFrame = f;
                if (logMode < 0) { ReloadConfig(); ReloadStacks(); }
                bool now = logMode == 1 || Background.WantedOrInUse(Background.Audio);
                if (now)
                {
                    float t = Time.realtimeSinceStartup;
                    // a reader of the log must see where it has a hole
                    if (!listening && totalPosts > 0)
                        Add("wake", "log resumed", "", "nothing was recorded for the last " + Fmt.F(t - Mathf.Max(0f, lastListened), "0") + " s (the audio log sleeps while nobody uses the tool; editor.cfg audio.log=always keeps it on)");
                    lastListened = t;
                }
                listening = now;
                return listening;
            }
        }

        // For a command that is about to read the log: wakes it now (the caller has asked for Background.Audio) so that
        // the hole marker is in place. True when the log is recording.
        public static bool Wake() { listenFrame = -1; return Listening; }

        // an event the game posted or prepared: remembered once per id (this runs for every post)
        static readonly HashSet<uint> knownEventIds = new HashSet<uint>();
        static void Know(uint id, string name)
        {
            if (!knownEventIds.Add(id)) return;
            names[id] = name;
            knownEvents.Add(name);
        }

        static bool IsVoice(string name)
        {
            return name != null && (name.StartsWith("boy_voc", StringComparison.Ordinal) || name.StartsWith("boy_foley", StringComparison.Ordinal) || name.StartsWith("hdd_voc", StringComparison.Ordinal) || name.StartsWith("fol_", StringComparison.Ordinal));
        }

        // harvest every name Playdead declared in SoundEngine.RTPCs / Switches / States / Events (+ AutoEvent cue names)
        static void LearnStatics(Type t, int depth)
        {
            if (depth > 3) return;
            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object v;
                try { v = f.GetValue(null); } catch { continue; }
                LearnObj(v);
                var arr = v as Array;
                if (arr != null) foreach (var x in arr) LearnObj(x);
            }
            foreach (var nt in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) LearnStatics(nt, depth + 1);
        }
        static void LearnObj(object v)
        {
            if (v == null) return;
            var s = v as string; if (s != null) { Learn(s); return; }
            foreach (var f in v.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (f.FieldType == typeof(string)) { try { Learn(f.GetValue(v) as string); } catch { } }
        }

        public static void Learn(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            try { names[SoundEngine.Name2ID(name)] = name; } catch { }
        }

        static void Add(string kind, string name, string source, string detail)
        {
            if (!logVoice && IsVoice(name)) return;
            if (log.Count >= MaxLog) log.RemoveRange(0, 300);
            log.Add(new Entry { time = Time.realtimeSinceStartup, frame = Time.frameCount, kind = kind, name = name, source = source, detail = detail });
        }

        public static void AddTimed(string what, string source, string signals)
        {
            Add("TIMED", what, source, signals.Length > 0 ? "signals: " + signals : "");
        }

        static string Src(AkGameObj a)
        {
            try { return a == null ? "-" : a.gameObject.name; } catch { return "?"; }
        }

        // which game code posted an event: the managed call stack of the first posts of each event id (bounded:
        // 3 distinct stacks per id, 4000 ids), game frames only. Used by the Audio DB "why did this play".
        public static readonly Dictionary<uint, List<string>> postStacks = new Dictionary<uint, List<string>>();
        static readonly Dictionary<uint, int> stackTries = new Dictionary<uint, int>();
        // §84 correlation modes: 0 OFF, 1 IMPORTANT (default: first posts of each event + bank loads), 2 FULL DEBUG
        // (every post, up to 8 distinct stacks per event). Never for continuous RTPC / switch updates. Start value from
        // editor.cfg audio.stacks (off | important | full); stacks are only taken while the log is awake.
        public static int stackMode = 1;
        public static readonly string[] StackModes = { "OFF", "IMPORTANT", "FULL DEBUG" };
        static void CaptureCaller(uint id)
        {
            if (stackMode == 0) return;
            int n; stackTries.TryGetValue(id, out n);
            if (stackMode == 1 && (n >= 6 || stackTries.Count > 4000)) return;
            if (stackMode == 2 && postStacks.ContainsKey(id) && postStacks[id].Count >= 8) return;
            stackTries[id] = n + 1;
            var st = new System.Diagnostics.StackTrace(2, false);
            var sb = new System.Text.StringBuilder();
            int kept = 0;
            for (int i = 0; i < st.FrameCount && kept < 8; i++)
            {
                var m = st.GetFrame(i).GetMethod(); if (m == null || m.DeclaringType == null) continue;
                string an = m.DeclaringType.Assembly.GetName().Name;
                if (an != "Assembly-CSharp" && an != "Assembly-CSharp-firstpass" && an != "PlayMaker") continue;
                if (sb.Length > 0) sb.Append(" ← ");
                var dt = m.DeclaringType; string tn = dt.IsNested && dt.Name.StartsWith("<") ? dt.DeclaringType.Name : dt.Name;
                sb.Append(tn).Append('.').Append(m.Name);
                kept++;
            }
            if (sb.Length == 0) return;
            string s = sb.ToString();
            List<string> l;
            if (!postStacks.TryGetValue(id, out l)) postStacks[id] = l = new List<string>();
            if (!l.Contains(s) && l.Count < (stackMode == 2 ? 8 : 3)) l.Add(s);
        }

        static string Who(Delegate d)
        {
            if (d == null) return "";
            object t = d.Target;
            string m = d.Method != null ? d.Method.Name : "?";
            if (t == null) return m;
            var comp = t as Component;
            if (comp != null) return comp.GetType().Name + "." + m + " on " + comp.gameObject.name;
            return t.GetType().Name + "." + m;
        }

        // ---------------------------------------------------------------- cue injection
        static readonly List<uint> pendingCues = new List<uint>();
        static bool pendingBeat;
        public static void InjectCue(string cueName) { Learn(cueName); pendingCues.Add(SoundEngine.Name2ID(cueName)); DevLog.Write("audio: injecting cue '" + cueName + "'"); }
        public static void InjectBeat() { pendingBeat = true; DevLog.Write("audio: injecting beat"); }

        void ApplyInjections()
        {
            if (pendingCues.Count == 0 && !pendingBeat) return;
            MusicManager music = null;
            try { music = PersistentBehaviour<GlobalAudio>.instance.music; } catch { }
            if (music == null) { pendingCues.Clear(); pendingBeat = false; DevLog.Write("audio: no MusicManager - cannot inject"); return; }
            foreach (var h in pendingCues)
            {
                var info = new CallbackInfo();
                info.labelHash = h;
                music.HandleEventCallback(AudioCallbackType.CueMarker, info);
                Add("CUE*", Resolve(h), "(injected)", Listeners(h));
            }
            pendingCues.Clear();
            if (pendingBeat) { music.WasThereABeatThisFrame = true; pendingBeat = false; Add("BEAT*", "", "(injected)", ""); }
        }

        // FSMs currently waiting on this cue (active state contains a matching WaitForMusicCueOrMarker)
        public static string Listeners(uint hash)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var w in AudioLinks.waits)
            {
                if (w.fsm == null || !w.isCue) continue;
                if (!(w.any || w.hash == hash)) continue;
                if (w.fsm.ActiveStateName != w.state) continue;
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append("-> ").Append(w.fsm.gameObject.name).Append('/').Append(w.fsm.FsmName).Append(" [").Append(w.state).Append("] fires '").Append(w.finishEvent).Append('\'');
            }
            return sb.Length > 0 ? sb.ToString() : "(no FSM waiting right now)";
        }

        // ---------------------------------------------------------------- callback wrapping
        AudioCallbackHandler Wrap(string eventName, string src, string who, AudioCallbackHandler cb)
        {
            if (cb == null) return null;
            return delegate (AudioCallbackType t, CallbackInfo info)
            {
                try
                {
                    totalCallbacks++;
                    if (t == AudioCallbackType.CueMarker) Add("CUE", Resolve(info.labelHash), src, "from '" + eventName + "' -> " + who + "   " + Listeners(info.labelHash));
                    else if (t == AudioCallbackType.MusicSyncBeat) { if (logRtpc) Add("beat", eventName, src, who); }
                    else if (t == AudioCallbackType.EndOfEvent) Add("end", eventName, src, who);
                    else if (t == AudioCallbackType.Duration) { if (logVoice) Add("dur", eventName, src, Fmt.F(info.duration, "0.00") + "s -> " + who); }
                    else Add(t.ToString(), eventName, src, who);
                }
                catch { }
                cb(t, info);
            };
        }

        // ---------------------------------------------------------------- ISoundEngineImplementation
        public bool IsCapturingOutput { get { return inner.IsCapturingOutput; } }
        public bool isDebugBuild { get { return inner.isDebugBuild; } }
        public string versionName { get { return "InsideDev.AudioSpy(" + inner.versionName + ")"; } }
        public string bankInfo { get { return inner.bankInfo; } }
        public bool IsInitialized { get { return inner.IsInitialized; } }
        public void Restart() { inner.Restart(); }
        public void Update() { inner.Update(); try { ApplyInjections(); } catch (Exception e) { DevLog.Error("audio inject", e); } }
        public void SetListenerTransform(Vector3 pos, Vector3 forward, Vector3 up, uint listenerID) { inner.SetListenerTransform(pos, forward, up, listenerID); }
        public void Stop() { inner.Stop(); }
        public void Terminate() { inner.Terminate(); }

        public uint PostEventID(uint eventID, string eventName, AkGameObj gameObject, AudioCallbackType callbackTypes, AudioCallbackHandler callback)
        {
            totalPosts++;
            if (eventName != null) Know(eventID, eventName);
            bool listen = Listening;
            if (listen) { try { CaptureCaller(eventID); } catch { } }
            // phase N rules (managed wrapper only; the native hooks stay observation-only)
            bool muteIt = false;
            if (AudioRules.Active)
            {
                uint rid; string rname;
                if (AudioRules.Replacement(eventID, out rid, out rname)) { eventID = rid; eventName = rname; AudioRules.replaced++; }
                muteIt = AudioRules.IsMuted(eventID);
                if (AudioRules.IsNuked(eventID))
                {
                    // nuked: never reaches Wwise; the game sees a failed post and gets no callbacks at all
                    AudioRules.nuked++;
                    if (listen) { try { Add("post", (eventName ?? Resolve(eventID)) + " [NUKED]", Src(gameObject), "blocked by audio rule (no sound, no callbacks)"); } catch { } }
                    return 0;
                }
            }
            if (!listen)
            {
                // nobody looks: the game's own callback goes to the engine untouched, nothing is built
                uint q = inner.PostEventID(eventID, eventName, gameObject, callbackTypes, callback);
                if (muteIt && q != 0) { inner.StopPlayingID(q, 0f); AudioRules.muted++; }
                if (EventMonitor.Awake) { try { if (gameObject != null) EventMonitor.AudioPost(gameObject.gameObject, eventName ?? Resolve(eventID)); } catch { } }
                return q;
            }
            string name = eventName ?? Resolve(eventID);
            // voice and foley posts are the bulk of the traffic and are not logged unless asked: decide before any text is
            // built. Their callbacks are wrapped only when a cue marker can come back (the one callback logged for them).
            bool keep = logVoice || !IsVoice(name);
            string src = null, who = null;
            var cb = callback;
            if (callback != null && (keep || (callbackTypes & AudioCallbackType.CueMarker) != 0))
            {
                src = Src(gameObject); who = Who(callback);
                cb = Wrap(name, src, who, callback);
            }
            uint r = inner.PostEventID(eventID, eventName, gameObject, callbackTypes, cb);
            if (muteIt && r != 0) { inner.StopPlayingID(r, 0f); AudioRules.muted++; }   // callbacks (EndOfEvent) still reach the game
            try { SoundLibrary.OnPosted(name, gameObject != null ? gameObject.gameObject : null); } catch { }
            try { if (gameObject != null) EventMonitor.AudioPost(gameObject.gameObject, name); } catch { }
            if (keep) { try { Add("post", name, src ?? Src(gameObject), (callback != null ? "callbacks " + callbackTypes + " -> " + who : "") + (r == 0 ? "  [FAILED]" : "")); } catch { } }
            return r;
        }
        public void StopPlayingID(uint playingID, float t) { inner.StopPlayingID(playingID, t); }
        public void CancelEventCallback(uint playingID) { inner.CancelEventCallback(playingID); }
        public void InnerSetRtpc(uint id, float v, AkGameObj go, float t) { inner.SetRTPCValueID(id, v, go, t); }
        public void InnerSetState(uint g, uint s) { inner.SetStateID(g, s); }
        public void SetRTPCValueID(uint rtpcID, float v, AkGameObj go, float t) { v = AudioRules.Rtpc(rtpcID, v, go == null); inner.SetRTPCValueID(rtpcID, v, go, t); if (logRtpc && Listening) Add("rtpc", Resolve(rtpcID), Src(go), Fmt.F(v)); }
        public void ResetGlobalRTPCValue(uint rtpcID) { inner.ResetGlobalRTPCValue(rtpcID); }
        public void SetSwitchID(uint g, uint s, AkGameObj go) { s = AudioRules.Switch(g, s); inner.SetSwitchID(g, s, go); if (logSwitches && Listening) Add("switch", Resolve(g), Src(go), "= " + Resolve(s)); }
        public void SetStateID(uint g, uint s) { s = AudioRules.State(g, s); inner.SetStateID(g, s); if (logStates && Listening) Add("state", Resolve(g), "global", "= " + Resolve(s)); }
        // bank loads are rare and explain missing sounds later: they are logged in both states
        public bool LoadBank(string bankName, OperationType op) { Add("bank+", bankName, "", op.ToString()); try { if (bankName != null && Listening) CaptureCaller(AudioTrace.Hash(bankName)); } catch { } return inner.LoadBank(bankName, op); }
        public bool UnloadBank(string bankName) { Add("bank-", bankName, "", ""); return inner.UnloadBank(bankName); }
        public void UnloadUnrequiredBanks(List<string> req) { inner.UnloadUnrequiredBanks(req); }
        public void LoadRequiredBanks(List<string> req, OperationType op) { inner.LoadRequiredBanks(req, op); }
        public bool CheckIfBankOperationsComplete() { return inner.CheckIfBankOperationsComplete(); }
        public void PrepareEvent(uint eventID, string eventName) { if (eventName != null) Know(eventID, eventName); inner.PrepareEvent(eventID, eventName); }
        public bool RegisterGameObject(GameObject go) { return inner.RegisterGameObject(go); }
        public bool UnregisterGameObject(GameObject go) { return inner.UnregisterGameObject(go); }
        public void SendGameObjectTransform(GameObject go, Vector3 p, Vector3 f) { inner.SendGameObjectTransform(go, p, f); }
        public void SendGameObjectTransform(GameObject go, float px, float py, float pz, float fx, float fy, float fz) { inner.SendGameObjectTransform(go, px, py, pz, fx, fy, fz); }
        public void EnableAuxSends(GameObject go, AkGameObjType type) { inner.EnableAuxSends(go, type); }
        public void DisableAuxSends(GameObject go) { inner.DisableAuxSends(go); }
        public void SetAuxSend(GameObject go, uint sendID, float v, string sendName) { inner.SetAuxSend(go, sendID, v, sendName); }
        public void SetDefaultAuxSend(string sendName, float v) { inner.SetDefaultAuxSend(sendName, v); }
        public void SetAdditionalAuxSend(uint sendID, float v, string n) { inner.SetAdditionalAuxSend(sendID, v, n); }
        public void DetectAuxSends(AkGameObj akgo) { inner.DetectAuxSends(akgo); }
        public void PostAuxSendsInternal(GameObject go, AuxSends sends, AuxSends add) { inner.PostAuxSendsInternal(go, sends, add); }
        public void SetAutoEvent(string cue, string ev) { Learn(cue); Learn(ev); knownEvents.Add(ev); Add("autoevent", cue, "", "-> posts '" + ev + "'"); inner.SetAutoEvent(cue, ev); }
        public void SetAutoEventGameObject(AkGameObj a) { inner.SetAutoEventGameObject(a); }
        public uint Name2ID(string name) { uint id = inner.Name2ID(name); if (name != null) names[id] = name; return id; }
        public float GetMusicPlayPosition(uint id, bool x) { return inner.GetMusicPlayPosition(id, x); }
        public float GetMusicLoopLength(uint id, bool x) { return inner.GetMusicLoopLength(id, x); }
        public float GetSourcePlayPosition(uint id) { return inner.GetSourcePlayPosition(id); }
        public bool StartOutputCapture(string f) { return inner.StartOutputCapture(f); }
        public bool StopOutputCapture() { return inner.StopOutputCapture(); }
        public uint CreateNewSequence(GameObject go, AudioCallbackType t, AudioCallbackHandler cb)
        {
            if (cb == null || !Listening) return inner.CreateNewSequence(go, t, cb);
            AkGameObj ak = null;
            try { ak = go != null ? go.GetComponent<AkGameObj>() : null; } catch { }
            return inner.CreateNewSequence(go, t, Wrap("sequence@" + (go != null ? go.name : "?"), Src(ak), Who(cb), cb));
        }
        public void DestroySequence(uint id) { inner.DestroySequence(id); }
        public void PlaySequence(uint id) { inner.PlaySequence(id); }
        public void PauseSequence(uint id) { inner.PauseSequence(id); }
        public void ClearSequence(uint id) { inner.ClearSequence(id); }
        public void StopSequence(uint id, float f) { inner.StopSequence(id, f); }
        public void ResumeSequence(uint id) { inner.ResumeSequence(id); }
        public uint ResolveDialogueEvent(uint id) { return inner.ResolveDialogueEvent(id); }
        public bool EnqueueDialogueEvent(uint seq, uint node, string n, float delay) { Learn(n); return inner.EnqueueDialogueEvent(seq, node, n, delay); }
        public int GetSequenceLength(uint id) { return inner.GetSequenceLength(id); }
        public string GetAuxSendInfo(Func<string, bool> filter) { return inner.GetAuxSendInfo(filter); }

        // ---------------------------------------------------------------- manual posting
        public static void Post(string name, GameObject on)
        {
            try
            {
                Learn(name);
                AkGameObj ak = null;
                if (on != null) ak = on.GetComponent<AkGameObj>();
                if (ak == null) ak = AudioKeyObject.AkGameObjs.global;
                uint r = SoundEngine.PostEventByIDFast(SoundEngine.Name2ID(name), name, ak);
                DevLog.Write("audio: posted '" + name + "' on " + (ak != null ? ak.gameObject.name : "?") + (r == 0 ? "  -> FAILED (unknown event or bank not loaded)" : "  playingID " + r));
            }
            catch (Exception e) { DevLog.Error("audio post", e); }
        }
    }

    // Static map of audio -> game links found in the loaded PlayMaker state machines.
    public static class AudioLinks
    {
        public class Wait
        {
            public PlayMakerFSM fsm; public string state; public bool isCue; public bool isBeat; public string cue; public uint hash; public bool any; public string finishEvent;
            public string kind { get { return isCue ? "cue '" + (any ? "*any*" : cue) + "'" : isBeat ? "beat" : cue; } }
        }
        public class PostRef { public PlayMakerFSM fsm; public string state; public string action; public string eventName; }

        public static readonly List<Wait> waits = new List<Wait>();
        public static readonly List<PostRef> posts = new List<PostRef>();
        static float nextScan;
        static bool scanned;
        static int scannedStream = -1;
        // per action type: the fields that can name a Wwise event (the scan visits every action of every loaded FSM)
        static readonly Dictionary<Type, FieldInfo[]> eventFields = new Dictionary<Type, FieldInfo[]>();
        static readonly FieldInfo[] noFields = new FieldInfo[0];

        static FieldInfo[] EventFields(Type ty)
        {
            FieldInfo[] r;
            if (eventFields.TryGetValue(ty, out r)) return r;
            List<FieldInfo> list = null;
            foreach (var f in ty.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                string fn = f.Name.ToLowerInvariant();
                if (!(fn.Contains("event") && (fn.Contains("name") || fn == "audioevent" || fn == "soundevent")) && fn != "eventname") continue;
                if (list == null) list = new List<FieldInfo>();
                list.Add(f);
            }
            r = list == null ? noFields : list.ToArray();
            eventFields[ty] = r;
            return r;
        }

        // What the loaded state machines wait for and post only changes when areas stream in or out: the plain call scans
        // once and then again after a streaming change; force (a Rescan button, a command, a catalog job) scans now.
        public static void Scan(bool force = false)
        {
            if (!force)
            {
                if (Time.realtimeSinceStartup < nextScan) return;
                if (scanned && !Levels.Changed(ref scannedStream)) return;
            }
            nextScan = Time.realtimeSinceStartup + 3f;
            scanned = true;
            scannedStream = Levels.StreamVersion;
            waits.Clear(); posts.Clear();
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM)))
                {
                    var fsm = o as PlayMakerFSM;
                    if (fsm == null || fsm.gameObject.hideFlags != HideFlags.None) continue;
                    FsmState[] states;
                    try { states = fsm.FsmStates; } catch { continue; }
                    if (states == null) continue;
                    foreach (var st in states)
                    {
                        if (st == null || st.Actions == null) continue;
                        foreach (var a in st.Actions)
                        {
                            if (a == null) continue;
                            var ty = a.GetType();
                            string tn = ty.Name;
                            if (tn == "WaitForMusicCueOrMarker")
                            {
                                string cue = Str(a, "cueOrMarkerName");
                                var w = new Wait { fsm = fsm, state = st.Name, isCue = true, cue = cue, any = Bool(a, "waitForAny"), finishEvent = Ev(a, "finishEvent") };
                                w.hash = string.IsNullOrEmpty(cue) ? 0 : SoundEngine.Name2ID(cue);
                                AudioSpy.Learn(cue);
                                waits.Add(w);
                            }
                            else if (tn == "WaitForBeat")
                                waits.Add(new Wait { fsm = fsm, state = st.Name, isBeat = true, cue = "(beat)", finishEvent = Ev(a, "finishEvent") });
                            else if (tn == "WaitForMusicTime")
                            {
                                var tf = ty.GetField("time");
                                var tv = tf != null ? tf.GetValue(a) as FsmFloat : null;
                                waits.Add(new Wait { fsm = fsm, state = st.Name, cue = "music time " + (tv != null ? Fmt.F(tv.Value, "0.00") + "s" : "?"), finishEvent = Ev(a, "finishEvent") });
                            }
                            else
                            {
                                // any action that names a Wwise event (PostEvent-style actions)
                                foreach (var f in EventFields(ty))
                                {
                                    string v = FieldStr(a, f);
                                    if (string.IsNullOrEmpty(v)) continue;
                                    AudioSpy.Learn(v);
                                    AudioSpy.knownEvents.Add(v);
                                    posts.Add(new PostRef { fsm = fsm, state = st.Name, action = tn, eventName = v });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e) { DevLog.Error("AudioLinks.Scan", e); nextScan = Time.realtimeSinceStartup + 30f; }
        }

        static string FieldStr(object a, FieldInfo f)
        {
            try
            {
                object v = f.GetValue(a);
                if (v == null) return null;
                var fs = v as FsmString; if (fs != null) return fs.Value;
                if (v is string) return (string)v;
                var p = v.GetType().GetField("name") ?? v.GetType().GetField("eventName");
                if (p != null) { var s = p.GetValue(v); var sfs = s as FsmString; return sfs != null ? sfs.Value : s as string; }
            }
            catch { }
            return null;
        }
        static string Str(object a, string field)
        {
            var f = a.GetType().GetField(field);
            if (f == null) return null;
            var v = f.GetValue(a) as FsmString;
            return v != null ? v.Value : null;
        }
        static bool Bool(object a, string field)
        {
            var f = a.GetType().GetField(field);
            var v = f != null ? f.GetValue(a) as FsmBool : null;
            return v != null && v.Value;
        }
        static string Ev(object a, string field)
        {
            var f = a.GetType().GetField(field);
            var v = f != null ? f.GetValue(a) as FsmEvent : null;
            return v != null ? v.Name : "(finish)";
        }
    }
}
