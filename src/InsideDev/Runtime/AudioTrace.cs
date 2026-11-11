using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Live Wwise observation built from the native tracer records (managed -> AkSoundEngine.dll boundary).
    // Holds: the audio timeline, Wwise game objects (id = Unity instance id in INSIDE), playing ids with the
    // switch/state/RTPC context captured at post time, banks, and an id -> name table with provenance.
    public static class AudioTrace
    {
        // ---------------------------------------------------------------- record kinds (match wwise_tracer.c)
        public const int POST = 1, EXEC = 3, SEEK = 4, STOPALL = 5, STOPPID = 6, RTPC = 7, RTPCPID = 8, RTPCRESET = 9, SWITCH = 10,
            TRIGGER = 12, STATE = 13, REGISTER = 15, UNREGISTER = 16, SETPOS = 17, LISTENER = 18, LOADBANK = 19, UNLOADBANK = 20,
            PREPARE = 21, CANCELCB = 22, CALLBACK = 23, CBINIT = 24, IDSTR = 26, PREPSYNC = 27;
        // a row written by this class, not by the tracer: the trace slept (text in Rec.name)
        public const int GAP = 30;

        public enum Cat { Event, Switch, State, Rtpc, Bank, Object, Callback, Id, Other }
        public static Cat CategoryOf(int t)
        {
            switch (t)
            {
                case POST: case EXEC: case SEEK: case STOPALL: case STOPPID: case CANCELCB: return Cat.Event;
                case SWITCH: case TRIGGER: return Cat.Switch;
                case STATE: return Cat.State;
                case RTPC: case RTPCPID: case RTPCRESET: return Cat.Rtpc;
                case LOADBANK: case UNLOADBANK: case PREPARE: case PREPSYNC: return Cat.Bank;
                case REGISTER: case UNREGISTER: case SETPOS: case LISTENER: return Cat.Object;
                case CALLBACK: case CBINIT: return Cat.Callback;
                case IDSTR: return Cat.Id;
            }
            return Cat.Other;
        }

        public sealed class Ev
        {
            public float t; public int frame; public WwiseNative.Rec r; public int count = 1;   // count > 1: coalesced repeats
            public string text;   // built lazily
            public string line; public int lineCount; public bool lineRaw;   // timeline row text (panel cache)
            public long seq;
        }

        public sealed class WObj
        {
            public uint id; public string name; public bool registered; public int regFrame, unregFrame = -1;
            public int posts; public float lastPost = -1; public Vector3 lastPos; public bool hasPos;
            public readonly Dictionary<uint, uint> switches = new Dictionary<uint, uint>();
            public readonly Dictionary<uint, float> rtpcs = new Dictionary<uint, float>();
        }

        public sealed class Play
        {
            public uint pid, eventId, gameObj; public float t; public int frame; public bool ended; public float endT; public uint cbFlags;
            public string context;       // switches/states/rtpcs relevant at post time (text snapshot)
            public string caller;        // managed wrapper that posted it, if correlated (see Correlate)
            public string callerProv;
            public readonly List<string> callbacks = new List<string>();
        }

        public sealed class Bank { public uint id; public string name; public string status; public int frame; public int ret; public float t; }

        public enum IdSource { RuntimeHash, GameReference, SpyTable, CodeString, Computed, Bank, Persisted }
        public struct IdName { public string name; public IdSource src; }

        const int Cap = 20000;
        static readonly Ev[] ring = new Ev[Cap];
        static int head, count;
        public static int Count { get { return count; } }
        public static Ev Get(int newestIndex) { return ring[(head - 1 - newestIndex + Cap * 2) % Cap]; }

        public static readonly Dictionary<uint, WObj> objs = new Dictionary<uint, WObj>();
        public static readonly Dictionary<uint, Play> plays = new Dictionary<uint, Play>();
        static readonly Queue<uint> playOrder = new Queue<uint>();
        public static readonly Dictionary<uint, Bank> banks = new Dictionary<uint, Bank>();
        public static readonly Dictionary<uint, uint> states = new Dictionary<uint, uint>();
        public static readonly Dictionary<uint, float> globalRtpc = new Dictionary<uint, float>();
        static readonly Dictionary<uint, IdName> ids = new Dictionary<uint, IdName>();
        public static readonly HashSet<uint> unknownIds = new HashSet<uint>();
        public static int posts, failedPosts, callbacks, endOfEvents, coalesced;
        public static bool paused;
        public static uint follow;          // FOLLOW PLAYING ID (0 = off)

        // ---------------------------------------------------------------- tick
        // The trace runs while an audio tool is in use (Background.WantedOrInUse(Background.Audio): the editor is open,
        // a command or client was active in the last seconds, a panel asked) or editor.cfg audio.trace = 1. Otherwise
        // the native tracer is set to "count only" and nothing is drained, named or kept; calls made in that time are
        // not in the timeline and switch / state / bank changes of that time are not known (Status says so).
        public const string ConfigKey = "audio.trace";
        static bool started, awake, keyOn, keyRead;
        static float keyReadAt = -100f, sleptAt = -1f;
        public static int gaps; public static float sleptSeconds;
        public static bool Awake { get { return awake; } }

        static bool ShouldRun()
        {
            float now = Time.realtimeSinceStartup;
            if (!keyRead || now - keyReadAt >= 1f) { keyRead = true; keyReadAt = now; try { keyOn = EditorState.Get(ConfigKey, false); } catch { } }
            return keyOn || Background.WantedOrInUse(Background.Audio);
        }

        // main thread: a panel or command reads the trace (keeps it running for the next seconds)
        public static void Want(float seconds) { Background.Want(Background.Audio, seconds); }

        public static void Tick()
        {
            bool want = ShouldRun();
            if (!started)
            {
                // once at start, wanted or not: what the game did before this frame (banks loaded, objects registered,
                // names hashed) goes into the state tables, not into the timeline
                started = true; bootFrame = Time.frameCount;
                WwiseNative.Init();
                stateOnly = true;
                try { WwiseNative.Tick(sinkAction); } finally { stateOnly = false; }
                if (!want) { WwiseNative.SetTracing(false); sleptAt = Time.realtimeSinceStartup; return; }
            }
            if (!want) { if (awake) Sleep(); return; }
            if (!awake) WakeUp();
            RefreshGameNames();
            SaveNamesMaybe();
            WwiseNative.Tick(sinkAction);
        }

        static void Sleep()
        {
            awake = false; sleptAt = Time.realtimeSinceStartup;
            WwiseNative.SetTracing(false);
            if (namesLoaded) { namesSaveAt = 0; SaveNamesMaybe(); }
            lastByKey.Clear();
            PruneObjects();
        }

        static void WakeUp()
        {
            awake = true;
            LoadNames();
            WwiseNative.SetTracing(true);
            if (Time.frameCount == bootFrame) return;   // running from the first frame: nothing was missed
            float now = Time.realtimeSinceStartup;
            float slept = sleptAt >= 0 ? now - sleptAt : now;
            gaps++; sleptSeconds += slept;
            // what banks did while the trace slept is not known: a status from before the gap is not reported as now
            foreach (var b in banks.Values) if (b.status != null && !b.status.StartsWith("?", StringComparison.Ordinal)) b.status = "? (" + b.status + " before a trace gap)";
            if (WwiseNative.Available && !paused)
                Add(new WwiseNative.Rec { type = GAP, frame = Time.frameCount, name = "trace slept " + Fmt.F(slept, "0.0") + " s (nobody used the audio tools; " + ConfigKey + "=1 keeps it on): calls in that time are not listed" }, now);
        }

        // Wwise objects that were unregistered long ago: ids are new after every load, the table would grow all session
        static void PruneObjects()
        {
            if (objs.Count < 2000) return;
            int frame = Time.frameCount;
            pruneTmp.Clear();
            foreach (var kv in objs) if (!kv.Value.registered && kv.Value.unregFrame >= 0 && frame - kv.Value.unregFrame > 3600) pruneTmp.Add(kv.Key);
            foreach (var k in pruneTmp) objs.Remove(k);
            pruneTmp.Clear();
        }
        static readonly List<uint> pruneTmp = new List<uint>();
        static bool stateOnly; static int bootFrame = -1;

        static readonly Action<WwiseNative.Rec> sinkAction = Sink;

        static long drainQpc; static float drainT;
        static void Sink(WwiseNative.Rec r)
        {
            if (drainQpc == 0 || Time.frameCount != lastSinkFrame) { lastSinkFrame = Time.frameCount; drainQpc = WwiseNative.Now(); drainT = Time.realtimeSinceStartup; }
            float t = drainT - (float)WwiseNative.QpcToSeconds(drainQpc - r.qpc);
            Apply(r, t);
            if (paused || stateOnly) return;
            // coalesce repeated RTPC writes to the same (object, rtpc) and repeated positions
            if (r.type == RTPC || r.type == SETPOS || r.type == LISTENER)
            {
                long key = ((long)r.type << 56) ^ ((long)r.gameObj << 24) ^ r.groupId;
                Ev last;
                if (lastByKey.TryGetValue(key, out last) && t - last.t < 0.5f && Recent(last) && KeyOf(last.r) == key) { last.r = r; last.count++; last.text = null; coalesced++; return; }
                if (lastByKey.Count > 8192) lastByKey.Clear();   // keys of objects that are long gone
                var ev = Add(r, t); lastByKey[key] = ev; return;
            }
            Add(r, t);
        }
        static int lastSinkFrame;
        static readonly Dictionary<long, Ev> lastByKey = new Dictionary<long, Ev>();
        static long added;
        static long KeyOf(WwiseNative.Rec r) { return ((long)r.type << 56) ^ ((long)r.gameObj << 24) ^ r.groupId; }
        static bool Recent(Ev e) { long age = added - 1 - e.seq; return age >= 0 && age < Math.Min(count, 64) && ring[(int)(e.seq % Cap)] == e; }

        static Ev Add(WwiseNative.Rec r, float t)
        {
            var ev = count == Cap ? ring[head] : null;
            if (ev == null) ev = new Ev();
            ev.t = t; ev.frame = r.frame; ev.r = r; ev.count = 1; ev.text = null; ev.line = null; ev.lineCount = 0; ev.lineRaw = false;
            ev.seq = added++;
            ring[head] = ev; head = (head + 1) % Cap; if (count < Cap) count++;
            return ev;
        }

        public static void Clear() { Array.Clear(ring, 0, Cap); head = count = 0; added = 0; lastByKey.Clear(); }

        // ---------------------------------------------------------------- state machine
        static void Apply(WwiseNative.Rec r, float t)
        {
            switch (r.type)
            {
                case REGISTER:
                    {
                        var o = Obj(r.gameObj);
                        o.registered = true; o.regFrame = r.frame; o.unregFrame = -1;
                        if (r.name != null) o.name = r.name;
                        break;
                    }
                case UNREGISTER: { var o = Obj(r.gameObj); o.registered = false; o.unregFrame = r.frame; break; }
                case SETPOS: { var o = Obj(r.gameObj); o.lastPos = r.pos; o.hasPos = true; break; }
                case SWITCH: if (r.name == null) Obj(r.gameObj).switches[r.groupId] = r.valueId; break;
                case STATE: if (r.name == null) states[r.groupId] = r.valueId; break;
                case RTPC:
                    if (r.name == null)
                    {
                        if (r.gameObj == 0 || r.gameObj == 0xFFFFFFFF) globalRtpc[r.groupId] = r.fvalue;
                        else Obj(r.gameObj).rtpcs[r.groupId] = r.fvalue;
                    }
                    break;
                case POST:
                    {
                        posts++;
                        uint ev = r.eventId;
                        if (r.name != null && ev == 0) { ev = Hash(r.name); Learn(ev, r.name, IdSource.Computed); }
                        EvStat es; if (!evStats.TryGetValue(ev, out es)) { es = new EvStat(); evStats[ev] = es; }
                        es.posts++; es.lastT = t; es.lastPid = r.playingId; if (r.playingId == 0) es.failed++; if (r.cbType != 0) es.withCallbacks++;
                        es.lastObj = r.gameObj;
                        var o = Obj(r.gameObj); o.posts++; o.lastPost = t;
                        if (r.playingId == 0) { failedPosts++; break; }
                        var p = new Play { pid = r.playingId, eventId = ev, gameObj = r.gameObj, t = t, frame = r.frame, cbFlags = r.cbType };
                        p.context = Context(o);
                        Correlate(p);
                        plays[p.pid] = p; playOrder.Enqueue(p.pid);
                        while (playOrder.Count > 4000) plays.Remove(playOrder.Dequeue());
                        break;
                    }
                case CALLBACK:
                    {
                        callbacks++;
                        Play p;
                        if (r.cbType == 0x40000000u) { var b = BankOf(r.bankId); b.status = r.ret == 1 ? "LOADED" : "LOAD RESULT " + r.ret; b.t = t; break; }
                        if (r.playingId != 0 && plays.TryGetValue(r.playingId, out p))
                        {
                            if (r.cbType == 1) { p.ended = true; p.endT = t; endOfEvents++; }
                            if (p.callbacks.Count < 48) p.callbacks.Add(Fmt.F(t, "0.00") + "s " + CbName(r.cbType) + CbDetail(r));
                        }
                        break;
                    }
                case LOADBANK:
                    {
                        uint id = r.bankId != 0 ? r.bankId : (r.name != null ? Hash(r.name) : 0);
                        var b = BankOf(id);
                        if (r.name != null) { b.name = r.name; Learn(id, r.name, IdSource.Bank); }
                        b.ret = r.ret; b.frame = r.frame; b.t = t;
                        b.status = r.ret == 1 ? "LOADED" : r.ret == 46 || r.ret == 3 ? "PENDING/ASYNC (" + r.ret + ")" : "RESULT " + r.ret;   // AK_Success = 1
                        break;
                    }
                case UNLOADBANK:
                    {
                        uint id = r.bankId != 0 ? r.bankId : (r.name != null ? Hash(r.name) : 0);
                        var b = BankOf(id);
                        if (r.name != null && b.name == null) b.name = r.name;
                        b.status = "UNLOADED"; b.frame = r.frame; b.t = t;
                        break;
                    }
                case IDSTR: if (r.name != null) Learn((uint)r.ret, r.name, IdSource.RuntimeHash); break;
            }
        }

        static WObj Obj(uint id)
        {
            WObj o;
            if (!objs.TryGetValue(id, out o))
            {
                o = new WObj { id = id };
                var rec = ObjectDatabase.Get((int)id);
                if (rec != null) o.name = rec.name;
                objs[id] = o;
            }
            return o;
        }
        static Bank BankOf(uint id) { Bank b; if (!banks.TryGetValue(id, out b)) { b = new Bank { id = id, status = "?" }; banks[id] = b; } return b; }

        static string Context(WObj o)
        {
            var sb = new StringBuilder();
            foreach (var kv in o.switches) sb.Append("switch ").Append(Name(kv.Key)).Append('=').Append(Name(kv.Value)).Append("; ");
            foreach (var kv in o.rtpcs) sb.Append("rtpc ").Append(Name(kv.Key)).Append('=').AppendF(kv.Value, "0.##").Append("; ");
            int n = 0;
            foreach (var kv in states) { if (kv.Value == 0) continue; if (n++ > 24) { sb.Append("..."); break; } sb.Append("state ").Append(Name(kv.Key)).Append('=').Append(Name(kv.Value)).Append("; "); }
            return sb.ToString();
        }

        // Managed caller for a post: the AudioSpy wrapper sees SoundEngine posts on the same frame.
        // Matching is by event name + frame (OBSERVED TEMPORAL); a matching SoundLibrary binding on the same
        // Unity object is reported as the likely owner.
        static void Correlate(Play p)
        {
            string ev = Name(p.eventId);
            if (ev.StartsWith("#")) return;
            var log = AudioSpy.log;
            for (int i = log.Count - 1; i >= 0 && i >= log.Count - 40; i--)
            {
                var e = log[i];
                if (e.frame < p.frame - 1) break;
                if (e.name == ev) { p.caller = (e.kind ?? "") + " " + (e.source ?? ""); p.callerProv = "OBSERVED TEMPORAL (same frame, same event)"; return; }
            }
            var d = SoundLibrary.Get(ev);
            if (d != null && !string.IsNullOrEmpty(d.lastSource) && Mathf.Abs(d.lastPost - Time.realtimeSinceStartup) < 0.5f) { p.caller = d.lastSource; p.callerProv = "OBSERVED TEMPORAL (sound library)"; }
        }

        // ---------------------------------------------------------------- ids
        // Wwise short ids are FNV-1 32-bit hashes of the lower-case name.
        public static uint Hash(string name)
        {
            uint h = 2166136261;
            for (int i = 0; i < name.Length; i++)          // no allocation (same result as ToLowerInvariant per char)
            {
                char ch = name[i];
                if (ch >= 'A' && ch <= 'Z') ch = (char)(ch + 32); else if (ch > 127) ch = char.ToLowerInvariant(ch);
                h *= 16777619; h ^= (byte)ch;
            }
            return h;
        }
        public static int mismatchedNames;
        public static void Learn(uint id, string name, IdSource src)
        {
            if (id == 0 || string.IsNullOrEmpty(name)) return;
            // only names that really hash to the id: the native record's name field holds 31 characters, so longer
            // runtime-hashed strings arrive truncated (the managed spy table still resolves those to the full name)
            if (Hash(name) != id) { mismatchedNames++; return; }
            IdName cur;
            if (ids.TryGetValue(id, out cur) && cur.src <= src) return;   // lower enum value = stronger provenance
            ids[id] = new IdName { name = name, src = src };
            unknownIds.Remove(id);
            namesDirty = true;
        }

        // per-event runtime statistics (for the audio database)
        public sealed class EvStat { public int posts, failed, withCallbacks; public float lastT; public uint lastPid, lastObj; }
        public static readonly Dictionary<uint, EvStat> evStats = new Dictionary<uint, EvStat>();

        // ---------------------------------------------------------------- persisted names (_mod\wwise_names.tsv)
        // Names learned in any session (game references, runtime hashing, code strings) are kept so unloaded areas
        // and later sessions resolve the same ids. Only names whose FNV hash equals the id are ever stored.
        static bool namesDirty, namesLoaded, namesLoadFailed; static float namesSaveAt;
        static string NamesPath { get { return System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "_mod"), "wwise_names.tsv"); } }
        public static void LoadNames()
        {
            if (namesLoaded) return; namesLoaded = true;
            try
            {
                if (!System.IO.File.Exists(NamesPath)) return;
                int n = 0;
                foreach (var line in System.IO.File.ReadAllLines(NamesPath))
                {
                    var p = line.Split('\t'); uint id;
                    if (p.Length < 2 || !uint.TryParse(p[0], System.Globalization.NumberStyles.Integer, Fmt.IC, out id) || Hash(p[1]) != id) continue;
                    IdSource src = IdSource.Persisted;
                    if (p.Length > 2) { try { src = (IdSource)Enum.Parse(typeof(IdSource), p[2]); } catch { } }
                    IdName cur;
                    if (!ids.TryGetValue(id, out cur) || src < cur.src) { ids[id] = new IdName { name = p[1], src = src }; n++; }
                }
                DevLog.Write("[audio] " + n + " names from wwise_names.tsv");
            }
            catch (Exception e)
            {
                // the file stays as it is: a save now would keep only the names of this session
                namesLoadFailed = true;
                DevLog.Error("names load (wwise_names.tsv is left untouched for this session)", e);
            }
        }
        static void SaveNamesMaybe()
        {
            if (!namesDirty || !namesLoaded || namesLoadFailed || Time.realtimeSinceStartup < namesSaveAt) return;
            namesDirty = false; namesSaveAt = Time.realtimeSinceStartup + 30f;
            try
            {
                var sb = new StringBuilder(ids.Count * 32);
                foreach (var kv in ids) if (Hash(kv.Value.name) == kv.Key) sb.Append(kv.Key.ToString(Fmt.IC)).Append('\t').Append(kv.Value.name).Append('\t').Append(kv.Value.src).Append('\n');
                SafeFile.WriteAllText(NamesPath, sb.ToString());
            }
            catch (Exception e) { DevLog.Error("names save", e); }
        }
        public static string Name(uint id)
        {
            IdName n;
            if (ids.TryGetValue(id, out n)) return n.name;
            string s = AudioSpy.Resolve(id);
            if (!s.StartsWith("#")) { ids[id] = new IdName { name = s, src = IdSource.SpyTable }; return s; }
            if (id != 0 && unknownIds.Count < 5000) unknownIds.Add(id);
            return s;
        }
        public static bool TryName(uint id, out IdName n) { return ids.TryGetValue(id, out n); }
        public static Dictionary<uint, IdName> SnapshotNames() { return new Dictionary<uint, IdName>(ids); }   // for background export
        public static int KnownIds { get { return ids.Count; } }

        static int gameNamesSeen = -1;
        static void RefreshGameNames()
        {
            if (SoundLibrary.defs.Count == gameNamesSeen) return;
            gameNamesSeen = SoundLibrary.defs.Count;
            foreach (var k in SoundLibrary.defs.Keys) Learn(Hash(k), k, IdSource.GameReference);
            foreach (var k in AudioSpy.knownEvents) Learn(Hash(k), k, IdSource.GameReference);
        }

        public static string ObjName(uint id)
        {
            if (id == 0xFFFFFFFF) return "(global)";
            WObj o;
            if (objs.TryGetValue(id, out o) && o.name != null) return o.name;
            var u = Unity(id);
            return u != null ? u.name : "go#" + (int)id;
        }
        // Wwise game object id = Unity instance id. The object database only scans while the editor is open, so
        // misses fall back to a map of live AkGameObj emitters, rebuilt at most every 30 s (or after streaming).
        static readonly Dictionary<int, GameObject> emitters = new Dictionary<int, GameObject>();
        static float emittersAt = -100f; static int emittersStream = -1;
        public static GameObject Unity(uint id)
        {
            var rec = ObjectDatabase.Get((int)id);
            if (rec != null) return rec.go;
            GameObject g;
            if (emitters.TryGetValue((int)id, out g) && g != null) return g;
            if (Time.realtimeSinceStartup - emittersAt > 30f || Levels.Changed(ref emittersStream))
            {
                emittersAt = Time.realtimeSinceStartup;
                emitters.Clear();
                foreach (var o in UnityEngine.Object.FindObjectsOfType(typeof(AkGameObj))) { var c = o as Component; if (c != null) emitters[c.gameObject.GetInstanceID()] = c.gameObject; }
                if (emitters.TryGetValue((int)id, out g) && g != null) return g;
            }
            return null;
        }

        public static string CbName(uint t)
        {
            switch (t)
            {
                case 1: return "EndOfEvent"; case 2: return "EndOfDynSeqItem"; case 4: return "Marker"; case 8: return "Duration";
                case 64: return "MusicPlaylistSelect"; case 128: return "MusicPlayStarted"; case 256: return "MusicSyncBeat"; case 512: return "MusicSyncBar";
                case 1024: return "MusicSyncEntry"; case 2048: return "MusicSyncExit"; case 4096: return "MusicSyncGrid"; case 8192: return "MusicSyncUserCue";
                case 16384: return "MusicSyncPoint"; case 65536: return "MidiEvent"; case 0x40000000u: return "Bank"; case 0x20000000u: return "Monitoring";
            }
            return "cb 0x" + t.ToString("X");
        }
        static string CbDetail(WwiseNative.Rec r)
        {
            if (r.cbType == 8192) return " cue " + Name((uint)r.i1);
            if (r.cbType == 4) return " marker " + r.i1 + " @" + r.i2;
            return "";
        }

        // ---------------------------------------------------------------- text
        static readonly string[] kindNames = new string[32];
        static AudioTrace()
        {
            kindNames[POST] = "POST"; kindNames[EXEC] = "ACTION"; kindNames[SEEK] = "SEEK"; kindNames[STOPALL] = "STOP ALL"; kindNames[STOPPID] = "STOP";
            kindNames[RTPC] = "RTPC"; kindNames[RTPCPID] = "RTPC/PID"; kindNames[RTPCRESET] = "RTPC RESET"; kindNames[SWITCH] = "SWITCH"; kindNames[TRIGGER] = "WWISE TRIGGER";
            kindNames[STATE] = "STATE"; kindNames[REGISTER] = "REGISTER"; kindNames[UNREGISTER] = "UNREGISTER"; kindNames[SETPOS] = "POSITION"; kindNames[LISTENER] = "LISTENER";
            kindNames[LOADBANK] = "BANK LOAD"; kindNames[UNLOADBANK] = "BANK UNLOAD"; kindNames[PREPARE] = "PREPARE"; kindNames[CANCELCB] = "CANCEL CB"; kindNames[CALLBACK] = "CALLBACK";
            kindNames[CBINIT] = "CB INIT"; kindNames[IDSTR] = "ID"; kindNames[PREPSYNC] = "PREPARE SYNCS"; kindNames[GAP] = "TRACE GAP";
        }
        public static string Kind(int t) { return t >= 0 && t < kindNames.Length && kindNames[t] != null ? kindNames[t] : "#" + t; }

        public static string Text(Ev e, bool raw)
        {
            if (e.text != null && !raw) return e.text;
            var r = e.r; var sb = new StringBuilder();
            if (r.type == GAP) { e.text = r.name ?? ""; return e.text; }
            string on = " on " + ObjName(r.gameObj) + (raw ? " [go " + r.gameObj + "]" : "");
            switch (r.type)
            {
                case POST:
                    sb.Append(r.name ?? Name(r.eventId)).Append(on);
                    sb.Append(r.playingId != 0 ? "  -> playing " + r.playingId : "  -> FAILED (invalid playing id: event not in a loaded bank?)");
                    if (r.cbType != 0) sb.Append("  callbacks ").Append(CbFlags(r.cbType));
                    break;
                case EXEC: sb.Append(r.name ?? Name(r.eventId)).Append(" action ").Append(r.groupId).Append(on).Append(" ").Append(r.i1).Append(" ms"); break;
                case SEEK: sb.Append(r.name ?? Name(r.eventId)).Append(on).Append(" to ").Append(r.i1 != 0 ? r.i1 + " ms" : Fmt.F(r.fvalue * 100, "0.#") + "%"); break;
                case STOPALL: sb.Append(r.gameObj != 0 || r.api == 0 ? ObjName(r.gameObj) : "everything"); break;
                case STOPPID: sb.Append("playing ").Append(r.playingId).Append(PlayName(r.playingId)).Append(" fade ").Append(r.i1).Append(" ms"); break;
                case RTPC: case RTPCRESET: sb.Append(r.name ?? Name(r.groupId)).Append(" = ").Append(r.type == RTPCRESET ? "default" : Fmt.F(r.fvalue, "0.###")).Append(r.gameObj != 0 ? on : " (global)"); if (r.i1 != 0) sb.Append(" over ").Append(r.i1).Append(" ms"); break;
                case RTPCPID: sb.Append(r.name ?? Name(r.groupId)).Append(" = ").AppendF(r.fvalue, "0.###").Append(" for playing ").Append(r.playingId); break;
                case SWITCH: sb.Append(r.name ?? (Name(r.groupId) + " = " + Name(r.valueId))).Append(on); break;
                case TRIGGER: sb.Append(r.name ?? Name(r.groupId)).Append(on); break;
                case STATE: sb.Append(r.name ?? (Name(r.groupId) + " = " + Name(r.valueId))); break;
                case REGISTER: sb.Append(r.name ?? ObjName(r.gameObj)).Append(raw ? " [go " + r.gameObj + "]" : ""); break;
                case UNREGISTER: sb.Append(ObjName(r.gameObj)); break;
                case SETPOS: case LISTENER: sb.Append(ObjName(r.gameObj)).Append(" ").Append(Fmt.V3(r.pos, "F1")); break;
                case LOADBANK: case UNLOADBANK: sb.Append(r.name ?? Name(r.bankId)).Append(r.bankId != 0 ? " (bank " + r.bankId + ")" : "").Append(" result ").Append(r.ret); break;
                case PREPARE: case PREPSYNC: sb.Append(r.groupId == 0 ? "load " : "unload ").Append(r.i2).Append(" item(s)").Append(r.valueId != 0 ? " first " + Name(r.valueId) : "").Append(" result ").Append(r.ret); break;
                case CALLBACK:
                    sb.Append(CbName(r.cbType));
                    if (r.cbType == 0x40000000u) sb.Append(" bank ").Append(Name(r.bankId)).Append(" result ").Append(r.ret);
                    else if (r.cbType == 0x20000000u) sb.Append(" error ").Append(r.i1).Append(" level ").Append(r.i2).Append(" playing ").Append(r.playingId);
                    else sb.Append(" playing ").Append(r.playingId).Append(PlayName(r.playingId)).Append(on).Append(CbDetail(r));
                    break;
                case IDSTR: sb.Append(r.name).Append(" = ").Append((uint)r.ret); break;
                case CBINIT: sb.Append("callback buffer ").Append(r.i1).Append(" bytes"); break;
                default: sb.Append("type ").Append(r.type); break;
            }
            if (e.count > 1) sb.Append("   (x").Append(e.count).Append(')');
            if (raw) sb.Append("   {api ").Append(r.api).Append(" thr ").Append(r.thread).Append(" ev ").Append(r.eventId).Append(" pid ").Append(r.playingId).Append(" grp ").Append(r.groupId).Append(" val ").Append(r.valueId).Append(" f ").Append(Fmt.F(r.fvalue, "R")).Append(" ret ").Append(r.ret).Append(" cookie 0x").Append(r.cookie.ToString("X")).Append('}');
            var s = sb.ToString();
            if (!raw) e.text = s;
            return s;
        }
        static string PlayName(uint pid) { Play p; return plays.TryGetValue(pid, out p) ? " (" + Name(p.eventId) + ")" : ""; }
        public static string CbFlags(uint f)
        {
            var sb = new StringBuilder();
            for (int b = 0; b < 31; b++) if ((f & (1u << b)) != 0) { if (sb.Length > 0) sb.Append('|'); sb.Append(CbName(1u << b)); }
            return sb.ToString();
        }

        public static string Status()
        {
            if (!WwiseNative.Available) return "native tracer: " + (WwiseNative.Problem.Length > 0 ? WwiseNative.Problem : "not connected");
            bool wasAwake = awake;
            if (!wasAwake) Want(30f);
            string s = posts + " posts (" + failedPosts + " failed), " + plays.Count + " playing ids tracked, " + callbacks + " callbacks, " + objs.Count + " game objects, " + banks.Count + " banks, " + KnownIds + " named ids, " + unknownIds.Count + " unknown ids";
            if (!wasAwake) s += "; the trace was asleep (nobody used the audio tools): collecting from now, try again in a moment";
            else if (gaps > 0) s += "; slept " + gaps + " time(s), " + Fmt.F(sleptSeconds, "0") + " s in total: calls and switch / state / bank changes of that time were not seen (" + ConfigKey + "=1 keeps the trace on)";
            return s;
        }

        // the same as one JSON line
        public static string StatusJson()
        {
            bool wasAwake = awake;
            if (!wasAwake && WwiseNative.Available) Want(30f);
            return JsonLine.Obj("ok", WwiseNative.Available, "available", WwiseNative.Available, "problem", WwiseNative.Problem, "awake", wasAwake, "collecting", WwiseNative.Available && !wasAwake,
                "always", keyOn, "level", WwiseNative.Level, "posts", posts, "failedPosts", failedPosts, "playingIds", plays.Count, "callbacks", callbacks, "objects", objs.Count,
                "banks", banks.Count, "namedIds", KnownIds, "unknownIds", unknownIds.Count, "timeline", count, "gaps", gaps, "sleptSeconds", sleptSeconds);
        }

        public static string DescribePlay(uint pid)
        {
            Play p;
            if (!plays.TryGetValue(pid, out p)) return "playing id " + pid + " not tracked";
            var sb = new StringBuilder();
            sb.Append("PLAYING ID ").Append(pid).Append("\n  event    ").Append(Name(p.eventId)).Append(" (").Append(p.eventId).Append(")");
            IdName nm; if (TryName(p.eventId, out nm)) sb.Append("  name source: ").Append(nm.src);
            sb.Append("\n  emitter  ").Append(ObjName(p.gameObj)).Append(" [go ").Append((int)p.gameObj).Append("]");
            var u = Unity(p.gameObj); if (u != null) sb.Append("  ").Append(Inspector.PathOf(u.transform));
            sb.Append("\n  posted   ").AppendF(p.t, "0.00").Append("s frame ").Append(p.frame);
            sb.Append("\n  status   ").Append(p.ended ? "ENDED at " + Fmt.F(p.endT, "0.00") + "s (" + Fmt.F(p.endT - p.t, "0.00") + " s)" : (p.cbFlags & 1) != 0 ? "PLAYING (no EndOfEvent yet)" : "POSTED (end not observable: no EndOfEvent callback requested)");
            sb.Append("\n  caller   ").Append(p.caller ?? "unknown (no managed post seen on that frame)");
            if (p.callerProv != null) sb.Append("  [").Append(p.callerProv).Append(']');
            sb.Append("\n  context  ").Append(p.context.Length > 0 ? p.context : "(no switch/state/rtpc observed yet for this emitter)");
            if (p.callbacks.Count > 0) { sb.Append("\n  callbacks"); foreach (var c in p.callbacks) sb.Append("\n    ").Append(c); }
            return sb.ToString();
        }
    }
}
