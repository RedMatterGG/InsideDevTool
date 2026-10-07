using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // One interconnected audio database (spec 68-70, 99, 102). Every record keeps where each fact came from.
    //   WWISE side   - offline SoundBank scan (WwiseBanks): events, actions, containers, music, media, game syncs
    //   GAME side    - SoundLibrary bindings (loaded objects), persisted per area in _mod\audio_refs.tsv so they stay
    //                  searchable when the area is unloaded; AudioCatalog music-timed triggers (per area)
    //   RUNTIME      - native tracer (AudioTrace): posts, playing ids, callbacks, banks loaded
    //   NAMES        - FNV-matched names from game references, runtime GetIDFromString, code string literals,
    //                  persisted in _mod\wwise_names.tsv (AudioTrace)
    // Wording rules: nothing is called "cut content"; missing links are reported as "not identified" / "not observed".
    public static class AudioDb
    {
        public enum Kind { Event, Bank, Music, StateGroup, SwitchGroup, Rtpc, GameRef, AreaTrigger, Node }
        public enum Filter { All, Loaded, Playing, GameReferences, WwiseEvents, Banks, Music, Callbacks, Unreferenced, HiddenUnloaded }
        public static readonly string[] FilterNames = { "ALL", "LOADED", "PLAYING", "GAME REFERENCES", "WWISE EVENTS", "BANKS", "MUSIC", "CALLBACKS", "UNREFERENCED", "HIDDEN/UNLOADED" };

        public sealed class Ref
        {
            public string area, path, where, eventName, prov;   // prov: FIELD / FSM / OBSERVED / DATA / ASSET / ANIM / CODE (CODE is not persisted)
            public uint eventId;
            public bool loaded;                                 // object present in the current session
            public bool seen;                                   // DATA / ASSET / ANIM: found by the current AudioRefScan pass
            public SoundLibrary.Binding live;
        }

        public sealed class Rec
        {
            public Kind kind; public uint id; public string name, bank;
            public WwiseBanks.Node node;
            public readonly List<Ref> refs = new List<Ref>();
            public readonly List<AudioCatalog.Entry> triggers = new List<AudioCatalog.Entry>();
            public bool music;                                  // tree reaches music nodes
            public string Name { get { return name ?? AudioTrace.Name(id); } }
        }

        public static readonly List<Rec> recs = new List<Rec>();
        public static readonly Dictionary<uint, Rec> byId = new Dictionary<uint, Rec>();
        public static readonly Dictionary<string, Ref> refs = new Dictionary<string, Ref>();   // area|path|where|event -> ref
        static string Key(string area, string path, string where, string ev) { return area + "|" + path + "|" + where + "|" + ev; }
        public static bool Built { get { return built; } }
        public static string status = "not loaded";
        public static int Version;

        // ---------------------------------------------------------------- startup: banks on a worker thread
        static volatile bool bankThreadDone; static bool started, built; static int streamSeen = -1;
        static Exception bankError;

        public static void Tick()
        {
            if (!started)
            {
                started = true;
                LoadRefs();
                string dir = Path.Combine(Application.streamingAssetsPath, "Audio");
                status = "scanning SoundBanks in " + dir;
                var th = new System.Threading.Thread(() =>
                {
                    try { WwiseBanks.LoadDir(dir); } catch (Exception e) { bankError = e; }
                    bankThreadDone = true;
                });
                th.IsBackground = true; th.Priority = System.Threading.ThreadPriority.BelowNormal;
                th.Start();
            }
            if (bankThreadDone && !built)
            {
                built = true;
                if (bankError != null) { status = "bank scan failed: " + bankError.Message; DevLog.Error("bank scan", bankError); }
                else { DevLog.Write("[audio db] " + WwiseBanks.Summary()); foreach (var l in WwiseBanks.log) DevLog.Write("[audio db]   " + l); }
                Rebuild();
                if (bankError == null) AudioMedia.EnsureStarted();
            }
            HarvestStep();
            // new areas streamed in: give the object database (and with it the sound library) a pass so the game
            // references of the new area get recorded even while the editor is closed
            if (Levels.Changed(ref streamSeen, 2f)) ObjectDatabase.Demand(20f);
            SyncRefs();
        }

        // ---------------------------------------------------------------- records
        public static void Rebuild()
        {
            recs.Clear(); byId.Clear();
            foreach (var n in WwiseBanks.nodes.Values)
            {
                Kind k;
                if (n.type == WwiseBanks.HType.Event) k = Kind.Event;
                else if ((n.type == WwiseBanks.HType.MusicSwitch || n.type == WwiseBanks.HType.MusicRanSeq || n.type == WwiseBanks.HType.MusicSegment) && !IsMusic(n.parent)) k = Kind.Music;
                else continue;
                Add(new Rec { kind = k, id = n.id, node = n, bank = n.bank });
            }
            foreach (var b in WwiseBanks.banks) Add(new Rec { kind = Kind.Bank, id = b.id, name = b.name, bank = b.name });
            foreach (var g in WwiseBanks.stateGroups) Add(new Rec { kind = Kind.StateGroup, id = g, bank = "Init" });
            foreach (var g in WwiseBanks.switchGroups) Add(new Rec { kind = Kind.SwitchGroup, id = g, bank = "Init" });
            foreach (var g in WwiseBanks.rtpcs) Add(new Rec { kind = Kind.Rtpc, id = g, bank = "Init" });
            // events that are named by the game but not found in any bank, and events only observed at runtime
            foreach (var r in refs.Values) EnsureEvent(r.eventId, r.eventName);
            foreach (var kv in AudioTrace.evStats) EnsureEvent(kv.Key, null);
            foreach (var r in refs.Values) { Rec e; if (byId.TryGetValue(r.eventId, out e)) e.refs.Add(r); }
            foreach (var kv in codeRefs) { Rec e; if (byId.TryGetValue(kv.Key, out e)) foreach (var m in kv.Value) e.refs.Add(new Ref { area = "(code)", path = m, where = "string literal \"" + e.Name + "\"", prov = "CODE", eventName = e.Name, eventId = kv.Key }); }
            // music flag: event trees that reach music nodes
            foreach (var r in recs) if (r.kind == Kind.Event && r.node != null) r.music = ReachesMusic(r.node, 0, new HashSet<uint>());
            // catalog (music-timed triggers per area) as records
            AudioCatalog.Load();
            foreach (var kv in AudioCatalog.byArea)
                foreach (var e in kv.Value)
                {
                    var rec = new Rec { kind = Kind.AreaTrigger, id = AudioTrace.Hash(e.area + "|" + e.obj + "|" + e.detail), name = e.obj + " — " + e.detail, bank = e.area };
                    rec.triggers.Add(e);
                    recs.Add(rec);
                }
            status = recs.Count + " records: " + WwiseBanks.Summary() + "; " + refs.Count + " game references (" + CountLoadedRefs() + " loaded)";
            Version++;
        }

        static void Add(Rec r) { if (byId.ContainsKey(r.id)) return; byId[r.id] = r; recs.Add(r); }
        static void EnsureEvent(uint id, string name)
        {
            if (id == 0 || byId.ContainsKey(id)) return;
            Add(new Rec { kind = Kind.Event, id = id, name = name });   // node == null: not found in any scanned bank
        }
        static bool IsMusic(uint id) { var n = WwiseBanks.Get(id); return n != null && n.type >= WwiseBanks.HType.MusicSegment && n.type <= WwiseBanks.HType.MusicRanSeq; }
        static bool ReachesMusic(WwiseBanks.Node n, int d, HashSet<uint> seen)
        {
            if (n == null || d > 8 || !seen.Add(n.id)) return false;
            if (n.type >= WwiseBanks.HType.MusicSegment && n.type <= WwiseBanks.HType.MusicRanSeq) return true;
            if (n.type == WwiseBanks.HType.Event) { foreach (var a in WwiseBanks.ActionsOf(n)) if ((a.actionType >> 8) == 0x04 && ReachesMusic(WwiseBanks.Get(a.target), d + 1, seen)) return true; }
            return false;
        }
        static int CountLoadedRefs() { int n = 0; foreach (var r in refs.Values) if (r.loaded) n++; return n; }

        // ---------------------------------------------------------------- game references (persisted per area)
        static string RefsPath { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "audio_refs.tsv"); } }
        static bool refsDirty, needRebuild; static float refsSaveAt, lastRebuild; static int refsSeenVersion = -1; static float refsSyncAt;

        static void LoadRefs()
        {
            try
            {
                if (!File.Exists(RefsPath)) return;
                foreach (var line in File.ReadAllLines(RefsPath))
                {
                    var p = line.Split('\t');
                    if (p.Length < 5) continue;
                    var r = new Ref { area = p[0], path = p[1], where = p[2], prov = p[3], eventName = p[4], eventId = AudioTrace.Hash(p[4]) };
                    refs[Key(r.area, r.path, r.where, r.eventName)] = r;
                    AudioTrace.Learn(r.eventId, r.eventName, AudioTrace.IdSource.GameReference);
                }
                DevLog.Write("[audio db] " + refs.Count + " game references from audio_refs.tsv");
            }
            catch (Exception e) { DevLog.Error("audio refs load", e); }
        }

        // merge the live SoundLibrary into the persisted reference table (every 2 s at most)
        static void SyncRefs()
        {
            if (Time.realtimeSinceStartup < refsSyncAt) return;
            refsSyncAt = Time.realtimeSinceStartup + 2f;
            int lv = SoundLibrary.defs.Count * 7919 + SoundLibrary.scannedObjects;
            bool changed = false;
            if (lv != refsSeenVersion)
            {
                refsSeenVersion = lv;
                var liveKeys = new HashSet<string>();
                foreach (var d in SoundLibrary.defs.Values)
                    foreach (var b in d.bindings)
                    {
                        if (b.go == null) continue;
                        string area = AudioCatalog.AreaOf(b.go), path = Inspector.PathOf(b.go.transform), ev = b.original ?? d.name;
                        string key = Key(area, path, b.where, ev);
                        liveKeys.Add(key);
                        Ref r;
                        if (!refs.TryGetValue(key, out r)) { r = new Ref { area = area, path = path, where = b.where, eventName = ev, eventId = AudioTrace.Hash(ev) }; refs[key] = r; changed = true; }
                        string prov = b.prov == SoundLibrary.Prov.Field ? "FIELD" : b.prov == SoundLibrary.Prov.Fsm ? "FSM" : "OBSERVED";
                        if (r.prov != prov) { r.prov = prov; changed = true; }
                        r.live = b;
                    }
                foreach (var r in refs.Values) { if (!IsLibProv(r.prov)) continue; bool l = liveKeys.Contains(Key(r.area, r.path, r.where, r.eventName)); if (!l) r.live = null; r.loaded = l; }
            }
            else foreach (var r in refs.Values) if (r.live != null && r.live.go == null) { r.live = null; r.loaded = false; }
            if (noteChanged) { noteChanged = false; changed = true; }
            if (changed) { refsDirty = true; needRebuild = true; }
            if (needRebuild && built && Time.realtimeSinceStartup - lastRebuild > 10f) { needRebuild = false; lastRebuild = Time.realtimeSinceStartup; Rebuild(); }
            if (refsDirty && Time.realtimeSinceStartup > refsSaveAt)
            {
                refsDirty = false; refsSaveAt = Time.realtimeSinceStartup + 30f;
                try
                {
                    var sb = new StringBuilder();
                    foreach (var r in refs.Values) sb.Append(r.area).Append('\t').Append(r.path).Append('\t').Append(r.where).Append('\t').Append(r.prov).Append('\t').Append(r.eventName).Append('\n');
                    File.WriteAllText(RefsPath, sb.ToString());
                }
                catch (Exception e) { DevLog.Error("audio refs save", e); }
            }
        }

        static bool IsLibProv(string p) { return p == "FIELD" || p == "FSM" || p == "OBSERVED"; }
        static bool noteChanged;

        // references found by AudioRefScan (nested data, assets, animation events)
        public static void Note(string area, string path, string where, string prov, string ev)
        {
            string key = Key(area, path, where, ev);
            Ref r;
            if (!refs.TryGetValue(key, out r)) { r = new Ref { area = area, path = path, where = where, prov = prov, eventName = ev, eventId = AudioTrace.Hash(ev) }; refs[key] = r; noteChanged = true; needRebuild = true; }
            r.seen = true; r.loaded = true;
        }
        public static void BeginRefScan() { foreach (var r in refs.Values) if (!IsLibProv(r.prov)) r.seen = false; }
        public static void EndRefScan()
        {
            // an object scanned in this pass that no longer yields a reference it had before: the old row is stale
            // (data changed, or written by an older scanner build) -> drop it
            var scanned = new HashSet<string>();
            foreach (var r in refs.Values) if (!IsLibProv(r.prov) && r.seen) scanned.Add(r.area + "|" + r.path);
            var drop = new List<string>();
            foreach (var kv in refs) { var r = kv.Value; if (!IsLibProv(r.prov) && !r.seen && scanned.Contains(r.area + "|" + r.path)) drop.Add(kv.Key); }
            foreach (var k in drop) refs.Remove(k);
            if (drop.Count > 0) { noteChanged = true; needRebuild = true; }
            foreach (var r in refs.Values) if (!IsLibProv(r.prov)) r.loaded = r.seen;
            Version++;
        }

        // ---------------------------------------------------------------- names from code string literals
        // ldstr operands in Assembly-CSharp(-firstpass) whose FNV hash matches a known bank id / game sync.
        static List<MethodBase> harvestQueue; static int harvestIdx; public static int harvested, harvestedMethods; public static bool harvestDone;
        static readonly Dictionary<uint, List<string>> codeRefs = new Dictionary<uint, List<string>>();   // event id -> Type.Method with the literal
        static void HarvestStep()
        {
            if (harvestDone || !built) return;
            try
            {
                if (harvestQueue == null)
                {
                    harvestQueue = new List<MethodBase>();
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        string n = asm.GetName().Name;
                        if (n != "Assembly-CSharp" && n != "Assembly-CSharp-firstpass") continue;
                        Type[] ts; try { ts = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types; }
                        foreach (var t in ts)
                        {
                            if (t == null) continue;
                            const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                            foreach (var m in t.GetMethods(F)) harvestQueue.Add(m);
                            foreach (var c in t.GetConstructors(F)) harvestQueue.Add(c);
                        }
                    }
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (harvestIdx < harvestQueue.Count && sw.Elapsed.TotalMilliseconds < 1.0)
                {
                    var m = harvestQueue[harvestIdx++];
                    harvestedMethods++;
                    byte[] il = null;
                    try { var body = m.GetMethodBody(); if (body != null) il = body.GetILAsByteArray(); } catch { }
                    if (il == null) continue;
                    for (int i = 0; i + 5 <= il.Length; i++)
                    {
                        if (il[i] != 0x72 || il[i + 4] != 0x70) continue;                  // ldstr <token 0x70xxxxxx>
                        int tok = il[i + 1] | il[i + 2] << 8 | il[i + 3] << 16 | il[i + 4] << 24;
                        string s = null;
                        try { s = m.Module.ResolveString(tok); } catch { }
                        if (string.IsNullOrEmpty(s) || s.Length > 120) continue;
                        uint h = AudioTrace.Hash(s);
                        if (WwiseBanks.nodes.ContainsKey(h) || WwiseBanks.stateGroups.Contains(h) || WwiseBanks.switchGroups.Contains(h) || WwiseBanks.rtpcs.Contains(h) || WwiseBanks.mediaUsers.ContainsKey(h))
                        {
                            AudioTrace.Learn(h, s, AudioTrace.IdSource.CodeString); harvested++;
                            WwiseBanks.Node en;
                            if (WwiseBanks.nodes.TryGetValue(h, out en) && en.type == WwiseBanks.HType.Event)
                            {
                                List<string> l; if (!codeRefs.TryGetValue(h, out l)) codeRefs[h] = l = new List<string>();
                                string where = m.DeclaringType.Name + "." + m.Name;
                                // a literal stored straight into a static field (constants tables such as Events):
                                // the reference is the field, whose readers are the real users
                                // skip what builds the stored value: more string args, small int args, newobj (new AudioEventSimple("..."))
                                int j = i + 5;
                                // (array initialisers too: dup / ldc.i4 idx / ldstr / stelem.ref ... then newobj and stsfld)
                                for (int k = 0; k < 400 && j < il.Length; k++)
                                {
                                    byte op = il[j];
                                    if (op == 0x72 || op == 0x73 || op == 0x8D || op == 0x20) j += 5;
                                    else if ((op >= 0x15 && op <= 0x1E) || op == 0x25 || op == 0xA2) j += 1;
                                    else if (op == 0x1F) j += 2;
                                    else break;
                                }
                                if (j + 5 <= il.Length && il[j] == 0x80)
                                {
                                    int ft = il[j + 1] | il[j + 2] << 8 | il[j + 3] << 16 | il[j + 4] << 24;
                                    try { var fi = m.Module.ResolveField(ft); where = fi.DeclaringType.Name + "." + fi.Name; } catch { }
                                }
                                if (!l.Contains(where) && l.Count < 8) l.Add(where);
                            }
                        }
                    }
                }
                if (harvestIdx >= harvestQueue.Count) { harvestDone = true; harvestQueue = null; needRebuild = true; DevLog.Write("[audio db] code strings: " + harvested + " Wwise names from " + harvestedMethods + " methods"); Version++; }
            }
            catch (Exception e) { harvestDone = true; DevLog.Error("name harvest", e); }
        }

        // ---------------------------------------------------------------- status + filters
        public static bool BankLoaded(string bankName)
        {
            if (bankName == null) return false;
            foreach (var b in AudioTrace.banks.Values) if ((b.name != null && b.name.StartsWith(bankName, StringComparison.OrdinalIgnoreCase)) || b.id == AudioTrace.Hash(bankName)) return b.status == "LOADED";
            return false;
        }

        public static string Status(Rec r)
        {
            var sb = new StringBuilder();
            if (r.kind == Kind.Event)
            {
                sb.Append(r.node != null ? "DEFINED (" + r.bank + ")" : "NOT FOUND IN ANY SCANNED BANK");
                if (r.node != null && BankLoaded(r.bank)) sb.Append(" · BANK LOADED");
                AudioTrace.EvStat st;
                if (AudioTrace.evStats.TryGetValue(r.id, out st))
                {
                    sb.Append(" · POSTED ").Append(st.posts).Append(st.failed > 0 ? " (" + st.failed + " failed)" : "");
                    AudioTrace.Play p;
                    if (st.lastPid != 0 && AudioTrace.plays.TryGetValue(st.lastPid, out p)) sb.Append(p.ended ? " · ENDED" : Time.realtimeSinceStartup - p.t < 2f ? " · PLAYING" : (p.cbFlags & 1) != 0 ? " · PLAYING (no end yet)" : "");
                }
                else sb.Append(" · no runtime activation observed");
            }
            else if (r.kind == Kind.Bank) sb.Append(BankLoaded(r.name) ? "BANK LOADED" : "not loaded (no load observed this session)");
            return sb.ToString();
        }

        public static bool Pass(Rec r, Filter f)
        {
            switch (f)
            {
                case Filter.All: return true;
                case Filter.Loaded: return r.kind == Kind.Event ? r.node != null && BankLoaded(r.bank) : r.kind == Kind.Bank ? BankLoaded(r.name) : r.kind == Kind.GameRef || r.kind == Kind.AreaTrigger ? false : true;
                case Filter.Playing: { AudioTrace.EvStat st; return r.kind == Kind.Event && AudioTrace.evStats.TryGetValue(r.id, out st) && Time.realtimeSinceStartup - st.lastT < 3f; }
                case Filter.GameReferences: return r.refs.Count > 0 || r.kind == Kind.AreaTrigger;
                case Filter.WwiseEvents: return r.kind == Kind.Event;
                case Filter.Banks: return r.kind == Kind.Bank;
                case Filter.Music: return r.kind == Kind.Music || r.music || (r.kind == Kind.AreaTrigger);
                case Filter.Callbacks: { AudioTrace.EvStat st; return r.kind == Kind.Event && AudioTrace.evStats.TryGetValue(r.id, out st) && st.withCallbacks > 0; }
                case Filter.Unreferenced: return r.kind == Kind.Event && r.node != null && r.refs.Count == 0 && !AudioTrace.evStats.ContainsKey(r.id) && !NamedByGame(r.id);
                case Filter.HiddenUnloaded: { int n = 0; foreach (var x in r.refs) { if (x.prov == "CODE") continue; if (x.loaded) return false; n++; } return n > 0; }
            }
            return true;
        }

        // the game itself produced this name: hashed it at runtime (GetIDFromString) or it was seen in game data /
        // a post in this or an earlier session (persisted names)
        public static bool NamedByGame(uint id)
        {
            AudioTrace.IdName nm;
            return AudioTrace.TryName(id, out nm) && (nm.src == AudioTrace.IdSource.RuntimeHash || nm.src == AudioTrace.IdSource.GameReference);
        }

        public static List<Rec> Query(Filter f, string text, int max)
        {
            var res = new List<Rec>();
            string q = (text ?? "").Trim().ToLowerInvariant();
            uint qid; bool isId = uint.TryParse(q.TrimStart('#'), out qid);
            foreach (var r in recs)
            {
                if (!Pass(r, f)) continue;
                if (q.Length > 0)
                {
                    bool hit = isId && r.id == qid;
                    if (!hit) hit = r.Name.ToLowerInvariant().Contains(q);
                    if (!hit) foreach (var x in r.refs) if ((x.area + " " + x.path + " " + x.where).ToLowerInvariant().Contains(q)) { hit = true; break; }
                    if (!hit && r.bank != null && r.kind == Kind.AreaTrigger) hit = r.bank.ToLowerInvariant().Contains(q);
                    if (!hit) continue;
                }
                res.Add(r);
                if (res.Count >= max) break;
            }
            return res;
        }

        // ---------------------------------------------------------------- the three questions (spec 79-81)
        public static string Describe(Rec r)
        {
            var sb = new StringBuilder();
            IdName(sb, r);
            sb.Append("STATUS  ").Append(Status(r)).Append('\n');
            if (r.kind == Kind.AreaTrigger) { foreach (var t in r.triggers) sb.Append("  area ").Append(t.area).Append("  ").Append(t.kind).Append("  ").Append(t.obj).Append("  ").Append(t.detail).Append('\n'); return sb.ToString(); }
            if (r.kind == Kind.Event)
            {
                sb.Append('\n').Append(WhatCanMakeThisPlay(r));
                sb.Append('\n').Append(WhatDoesThisPlay(r));
                sb.Append('\n').Append(WhyDidThisPlay(r));
            }
            else if (r.kind == Kind.Bank)
            {
                List<string> st;
                if (AudioSpy.postStacks.TryGetValue(r.id, out st) && st.Count > 0) { sb.Append("\nLOADED BY (game call stack of its LoadBank calls)\n"); foreach (var x in st) sb.Append("  ").Append(x).Append('\n'); }
                else sb.Append("\nNo LoadBank call observed this session (loaded before the spy was installed, or not loaded).\n");
            }
            else if (r.node != null)
            {
                sb.Append('\n');
                WwiseBanks.Tree(r.id, NameOf, sb, 0, new HashSet<uint>(), 60);
                sb.Append(AudioMedia.Report(r.id));
                var evs = WwiseBanks.EventsReaching(r.id);
                sb.Append("EVENTS THAT CAN PLAY THIS (").Append(evs.Count).Append(")\n");
                foreach (var e in evs) sb.Append("  ").Append(NameOf(e)).Append('\n');
            }
            return sb.ToString();
        }

        static void IdName(StringBuilder sb, Rec r)
        {
            sb.Append(r.kind.ToString().ToUpperInvariant()).Append("  ").Append(r.Name).Append("   id ").Append(r.id);
            AudioTrace.IdName nm;
            if (AudioTrace.TryName(r.id, out nm)) sb.Append("   name source: ").Append(nm.src);
            else if (r.name == null) sb.Append("   (name not identified)");
            sb.Append('\n');
        }

        public static string NameOf(uint id) { string s = AudioTrace.Name(id); return s.StartsWith("#") ? "#" + id : s + " (" + id + ")"; }

        public static string WhatCanMakeThisPlay(Rec r)
        {
            var sb = new StringBuilder("WHAT CAN MAKE THIS PLAY\n");
            AudioTrace.IdName nm; bool named = AudioTrace.TryName(r.id, out nm);
            bool posted = AudioTrace.evStats.ContainsKey(r.id);
            if (r.refs.Count == 0 && !posted && !NamedByGame(r.id)) sb.Append("  No game-side reference currently identified (scripts, nested data, assets, animation events and code of the areas seen so far; unloaded areas are known only after they were visited or catalogued).\n");
            int shown = 0;
            foreach (var x in r.refs)
            {
                if (shown++ >= 40) { sb.Append("  … ").Append(r.refs.Count - 40).Append(" more\n"); break; }
                sb.Append("  [").Append(x.prov).Append("] ").Append(x.area).Append("  ").Append(x.path).Append("  ").Append(x.where).Append(x.prov == "CODE" ? "" : x.loaded ? "   (loaded)" : "   (not loaded now)").Append('\n');
            }
            if (named && nm.src == AudioTrace.IdSource.RuntimeHash) sb.Append("  [RUNTIME] the game computed this id from its name (GetIDFromString) in this or an earlier session\n");
            else if (named && nm.src == AudioTrace.IdSource.GameReference && r.refs.Count == 0) sb.Append("  [NAMED BY GAME] the name was seen in game data or a post in an earlier session (location not recorded)\n");
            CodePaths(r, sb);
            AudioTrace.EvStat st;
            if (AudioTrace.evStats.TryGetValue(r.id, out st)) sb.Append("  [OBSERVED] posted ").Append(st.posts).Append("x this session, last on ").Append(AudioTrace.ObjName(st.lastObj)).Append('\n');
            // other events whose Play actions reach the same targets
            if (r.node != null)
            {
                var others = new HashSet<uint>();
                foreach (var a in WwiseBanks.ActionsOf(r.node)) if ((a.actionType >> 8) == 0x04) foreach (var e in WwiseBanks.EventsReaching(a.target, 1)) if (e != r.id) others.Add(e);
                if (others.Count > 0) { sb.Append("  events that play the same objects: "); int k = 0; foreach (var e in others) { if (k++ > 8) { sb.Append("…"); break; } sb.Append(NameOf(e)).Append("  "); } sb.Append('\n'); }
            }
            return sb.ToString();
        }

        // the game code behind each reference: which methods use the field / contain the literal, and how they run
        static void CodePaths(Rec r, StringBuilder sb)
        {
            if (!LogicPanel.Ready || r.refs.Count == 0) return;
            var done = new HashSet<string>(); int shown = 0;
            var sub = new StringBuilder();
            foreach (var x in r.refs)
            {
                if (shown >= 6) break;
                string typeName = null, member = null;
                if (x.prov == "CODE") { int d = x.path.LastIndexOf('.'); if (d > 0) { typeName = x.path.Substring(0, d); member = x.path.Substring(d + 1); } }
                else if (x.prov == "FIELD" || x.prov == "DATA" || x.prov == "ASSET")
                {
                    string w = x.where; if (w.StartsWith("asset data ")) w = w.Substring(11); else if (w.StartsWith("data ")) w = w.Substring(5);
                    int d = w.IndexOf('.'); if (d <= 0) continue;
                    typeName = w.Substring(0, d);
                    int e = w.IndexOfAny(new[] { '.', '[' }, d + 1); member = e > 0 ? w.Substring(d + 1, e - d - 1) : w.Substring(d + 1);
                }
                if (typeName == null || !done.Add(x.prov + typeName + "." + member)) continue;
                var t = CodeGraph.FindType(typeName); if (t == null) continue;
                if (x.prov == "CODE")
                {
                    var mi = CodeGraph.Method(t, member);
                    if (mi != null)
                    {
                        sub.Append("    ").Append(mi.Short).Append(HasSound(mi) ? "  posts a sound" : "  uses the name").Append('\n');
                        CodeGraph.Triggers(mi, sub, "        ", 2, new HashSet<CodeGraph.MInfo>()); shown++;
                        continue;
                    }
                    // static field holding the name: fall through to its readers
                }
                var seenM = new HashSet<CodeGraph.MInfo>();
                var usesL = CodeGraph.Uses(t, member);
                usesL.Sort((p1, p2) => (p1.Value.kind == "write" ? 1 : 0).CompareTo(p2.Value.kind == "write" ? 1 : 0));
                foreach (var u in usesL)
                {
                    if (u.Key.name == ".cctor" || !seenM.Add(u.Key) || seenM.Count > 4) continue;
                    sub.Append("    ").Append(u.Key.Short).Append(u.Value.kind == "write" ? "  sets " : "  reads ").Append(typeName).Append('.').Append(member).Append(HasSound(u.Key) ? " and posts a sound" : "").Append(u.Value.guard != null ? "   if " + u.Value.guard : "").Append('\n');
                    CodeGraph.Triggers(u.Key, sub, "        ", 1, new HashSet<CodeGraph.MInfo>());
                    shown++;
                }
            }
            if (sub.Length > 0) sb.Append("  CODE BEHIND THESE REFERENCES (static analysis; the path exists in code, it is not implied that it ran)\n").Append(sub);
        }
        static bool HasSound(CodeGraph.MInfo mi) { foreach (var e in mi.effects) if (e.kind == "sound") return true; return false; }

        public static string WhatDoesThisPlay(Rec r)
        {
            var sb = new StringBuilder("WHAT DOES THIS EVENT PLAY\n");
            if (r.node == null) { sb.Append("  The event id is not in any scanned bank: posting it fails (invalid playing id).\n"); return sb.ToString(); }
            var tree = new StringBuilder();
            WwiseBanks.Tree(r.id, NameOf, tree, 1, new HashSet<uint>(), 80);
            sb.Append(tree);
            sb.Append(AudioMedia.Report(r.id));
            // what the result depends on: switch / state groups and random / sequence containers in the tree
            var deps = new List<string>();
            CollectDeps(r.node, deps, new HashSet<uint>(), 0);
            if (deps.Count > 0) { sb.Append("  Depends on: "); sb.Append(string.Join(", ", deps.ToArray())).Append('\n'); }
            else sb.Append("  No switch, state or random container identified in the tree.\n");
            return sb.ToString();
        }

        static void CollectDeps(WwiseBanks.Node n, List<string> deps, HashSet<uint> seen, int d)
        {
            if (n == null || d > 10 || !seen.Add(n.id)) return;
            if (n.type == WwiseBanks.HType.Switch || n.type == WwiseBanks.HType.MusicSwitch)
                foreach (var l in n.links) if (l.rel == "switch group" || l.rel == "state group") { string s = l.rel + " " + NameOf(l.to) + " (inferred)"; if (!deps.Contains(s)) deps.Add(s); }
            if (n.type == WwiseBanks.HType.RanSeq || n.type == WwiseBanks.HType.MusicRanSeq) { string s = "random/sequence container " + NameOf(n.id); if (!deps.Contains(s) && deps.Count < 12) deps.Add(s); }
            if (n.type == WwiseBanks.HType.Event) { foreach (var a in WwiseBanks.ActionsOf(n)) { if ((a.actionType >> 8) == 0x04) CollectDeps(WwiseBanks.Get(a.target), deps, seen, d + 1); } return; }
            foreach (var c in n.children) CollectDeps(WwiseBanks.Get(c), deps, seen, d + 1);
            foreach (var l in n.links) if (l.prov == WwiseBanks.Prov.Inferred) CollectDeps(WwiseBanks.Get(l.to), deps, seen, d + 1);
        }

        public static string WhyDidThisPlay(Rec r)
        {
            var sb = new StringBuilder("WHY DID THIS PLAY (last observed post)\n");
            AudioTrace.EvStat st;
            if (!AudioTrace.evStats.TryGetValue(r.id, out st)) { sb.Append("  No runtime activation observed this session.\n"); return sb.ToString(); }
            if (st.lastPid == 0) { sb.Append("  Last post failed (no playing id).\n"); return sb.ToString(); }
            foreach (var line in AudioTrace.DescribePlay(st.lastPid).Split('\n')) sb.Append("  ").Append(line).Append('\n');
            List<string> stacks;
            if (AudioSpy.postStacks.TryGetValue(r.id, out stacks) && stacks.Count > 0)
            {
                sb.Append("  POSTED BY (game call stack of its first posts, innermost first)\n");
                foreach (var s in stacks) sb.Append("    ").Append(s).Append('\n');
                // how the outermost game method gets run (Unity message, signal, animation event, ...)
                string outer = stacks[0]; int k = outer.LastIndexOf(" ← "); if (k >= 0) outer = outer.Substring(k + 3);
                int d = outer.LastIndexOf('.');
                if (d > 0 && LogicPanel.Ready)
                {
                    var t = CodeGraph.FindType(outer.Substring(0, d)); var mi = t != null ? CodeGraph.Method(t, outer.Substring(d + 1)) : null;
                    if (mi != null && mi.entries.Count > 0) sb.Append("    ").Append(outer).Append(" runs as: ").Append(string.Join("; ", mi.entries.ToArray())).Append('\n');
                }
            }
            else sb.Append("  Unknown: which game code posted it (no managed call stack captured for this event yet).\n");
            return sb.ToString();
        }

        // record for any Wwise id (transient record for containers / sounds / actions)
        public static Rec RecFor(uint id)
        {
            Rec r; if (byId.TryGetValue(id, out r)) return r;
            var n = WwiseBanks.Get(id);
            return new Rec { kind = Kind.Node, id = id, node = n, bank = n != null ? n.bank : null, name = n != null ? n.type + " " + NameOf(id) : null };
        }

        public static Rec Find(string q)
        {
            uint id;
            if (uint.TryParse(q.TrimStart('#'), out id)) { Rec r; if (byId.TryGetValue(id, out r)) return r; }
            uint h = AudioTrace.Hash(q);
            Rec x; if (byId.TryGetValue(h, out x)) return x;
            foreach (var r in recs) if (string.Equals(r.Name, q, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }

        public static string Stats()
        {
            int ev = 0, named = 0, withRefs = 0, posted = 0, missing = 0;
            foreach (var r in recs)
                if (r.kind == Kind.Event)
                {
                    ev++; AudioTrace.IdName nm; if (r.name != null || AudioTrace.TryName(r.id, out nm)) named++;
                    if (r.refs.Count > 0) withRefs++; if (AudioTrace.evStats.ContainsKey(r.id)) posted++; if (r.node == null) missing++;
                }
            var byProv = new Dictionary<string, int>(); int unref = 0, unrefNamed = 0; var unrefBank = new Dictionary<string, int>();
            foreach (var r in recs) if (r.kind == Kind.Event) { if (Pass(r, Filter.Unreferenced)) { unref++; AudioTrace.IdName nm0; if (AudioTrace.TryName(r.id, out nm0)) unrefNamed++; int c0; unrefBank.TryGetValue(r.bank ?? "?", out c0); unrefBank[r.bank ?? "?"] = c0 + 1; }  var ps = new HashSet<string>(); foreach (var x in r.refs) ps.Add(x.prov); foreach (var p in ps) { int c; byProv.TryGetValue(p, out c); byProv[p] = c + 1; } }
            var pl = new List<string>(); foreach (var kv in byProv) pl.Add(kv.Key + " " + kv.Value);
            var bl = new List<string>(); foreach (var kv in unrefBank) bl.Add(kv.Key + " " + kv.Value);
            return "AUDIO DB  " + status + "\n  events with a reference, by kind: " + string.Join(", ", pl.ToArray()) + "\n  no game-side reference currently identified: " + unref + " events (" + unrefNamed + " named; by bank: " + string.Join(", ", bl.ToArray()) + ")\n  deep data scan: " + AudioRefScan.status + "\n  events " + ev + ": " + named + " named, " + withRefs + " with game references, " + posted + " posted this session, " + missing + " referenced/posted but not in any bank\n  code-string names: " + (harvestDone ? harvested + " from " + harvestedMethods + " methods" : "scanning (" + harvestIdx + " methods)") + "\n";
        }
    }
}
