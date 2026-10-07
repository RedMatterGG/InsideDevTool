using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InsideDev
{
    // Phase I: offline SoundBank scanner (Wwise bank version 112, INSIDE's Init / Boy / Huddle / Game2 banks).
    //
    // Written from the observed bank layout, validated against the game's own files: every HIRC object's declared
    // size must be consumed exactly by the chunk walk, and every parsed parent id must resolve to a known object.
    // Media (DATA) is never read - it is skipped with a seek - and nothing is extracted or modified (spec 94).
    //
    // Provenance of every link:
    //   PARSED    - read from a field whose position is known and validated (event actions, action target,
    //               node parent, sound / track source media, STMG groups)
    //   INFERRED  - found by scanning the object's bytes for ids of other known objects / game syncs (switch
    //               container assignments, music decision trees); reported as inferred, never as fact
    // No dependency on UnityEngine: the same file builds into a test harness outside the game.
    public static class WwiseBanks
    {
        public enum HType : byte
        {
            State = 1, Sound = 2, Action = 3, Event = 4, RanSeq = 5, Switch = 6, ActorMixer = 7, Bus = 8, Layer = 9,
            MusicSegment = 10, MusicTrack = 11, MusicSwitch = 12, MusicRanSeq = 13, Attenuation = 14, DialogueEvent = 15,
            FeedbackBus = 16, FeedbackNode = 17, FxShareSet = 18, FxCustom = 19, AuxBus = 20, Lfo = 21, Envelope = 22
        }

        public enum Prov { Parsed, Inferred }

        public sealed class Link { public uint to; public string rel; public Prov prov; }

        public sealed class Node
        {
            public uint id; public HType type; public string bank; public int size;
            public uint parent, bus;                       // PARSED for containers, sounds, music nodes
            public readonly List<Link> links = new List<Link>();
            public readonly List<uint> children = new List<uint>();   // filled from parent links
            // event: action ids (links rel "action"); action:
            public ushort actionType; public uint target, group, value, actionBank;
            public bool seekRelative; public float seekValue, seekMin, seekMax; public bool hasSeek;   // Seek actions
            // sound / track
            public readonly List<uint> media = new List<uint>(); public byte stream;
            public string note;
        }

        public sealed class Bank
        {
            public string file, name; public uint id; public int version; public long size;
            public int media, objects; public long dataSize, dataPos;   // DATA chunk payload position in the file (media headers are read on request only)
            public readonly Dictionary<HType, int> counts = new Dictionary<HType, int>();
            public readonly List<string> problems = new List<string>();
        }

        public static readonly Dictionary<uint, Node> nodes = new Dictionary<uint, Node>();
        public static readonly List<Bank> banks = new List<Bank>();
        public static readonly Dictionary<uint, string> bankNames = new Dictionary<uint, string>();    // STID
        public static readonly HashSet<uint> stateGroups = new HashSet<uint>(), switchGroups = new HashSet<uint>(), rtpcs = new HashSet<uint>();
        public static readonly Dictionary<uint, List<uint>> switchValuesByGroup = new Dictionary<uint, List<uint>>();   // from STMG (rtpc-driven switches) + actions
        public static readonly Dictionary<uint, List<uint>> stateValuesByGroup = new Dictionary<uint, List<uint>>();
        public static readonly Dictionary<uint, List<uint>> mediaUsers = new Dictionary<uint, List<uint>>();          // wem id -> sounds / tracks
        public static readonly Dictionary<uint, string> mediaBank = new Dictionary<uint, string>();                // wem id -> bank holding it (DIDX)
        public static readonly Dictionary<uint, uint[]> mediaLoc = new Dictionary<uint, uint[]>();                // wem id -> [offset in DATA, size] (DIDX)
        public static Func<uint, string> MediaLabel;   // set by AudioMedia (file name, length, where it is stored)
        public static readonly List<string> log = new List<string>();
        public static bool Loaded;
        public static double loadMs;

        // ---------------------------------------------------------------- loading
        public static void LoadDir(string dir)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            nodes.Clear(); banks.Clear(); bankNames.Clear(); stateGroups.Clear(); switchGroups.Clear(); rtpcs.Clear();
            switchValuesByGroup.Clear(); stateValuesByGroup.Clear(); mediaUsers.Clear(); mediaBank.Clear(); mediaLoc.Clear(); log.Clear();
            var files = new List<string>(Directory.GetFiles(dir, "*.bnk"));
            files.Sort((a, b) => Path.GetFileName(a).Equals("Init.bnk", StringComparison.OrdinalIgnoreCase) ? -1 : Path.GetFileName(b).Equals("Init.bnk", StringComparison.OrdinalIgnoreCase) ? 1 : string.CompareOrdinal(a, b));
            foreach (var f in files)
            {
                try { LoadBank(f); }
                catch (Exception e) { log.Add(Path.GetFileName(f) + ": " + e.Message); }
            }
            Link2();
            Loaded = true;
            loadMs = sw.Elapsed.TotalMilliseconds;
        }

        static uint U32(byte[] b, int p) { return (uint)(b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24); }
        static ushort U16(byte[] b, int p) { return (ushort)(b[p] | b[p + 1] << 8); }

        static void LoadBank(string file)
        {
            var bk = new Bank { file = file, name = Path.GetFileNameWithoutExtension(file) };
            banks.Add(bk);
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bk.size = fs.Length;
                long o = 0;
                var hdr = new byte[8];
                while (o + 8 <= fs.Length)
                {
                    fs.Position = o;
                    if (fs.Read(hdr, 0, 8) < 8) break;
                    string tag = Encoding.ASCII.GetString(hdr, 0, 4);
                    uint sz = U32(hdr, 4);
                    if (o + 8 + sz > fs.Length) { bk.problems.Add(tag + " chunk runs past end of file"); break; }
                    if (tag == "DATA") { bk.dataSize = sz; bk.dataPos = o + 8; }  // media: skipped here (AudioMedia reads a file header on request)
                    else
                    {
                        var b = new byte[sz];
                        int got = 0; while (got < sz) { int n = fs.Read(b, got, (int)sz - got); if (n <= 0) break; got += n; }
                        switch (tag)
                        {
                            case "BKHD": bk.version = (int)U32(b, 0); bk.id = U32(b, 4); if (bk.version != 112) bk.problems.Add("bank version " + bk.version + " (parser written for 112)"); break;
                            case "DIDX": bk.media = (int)(sz / 12); for (int i = 0; i + 12 <= sz; i += 12) { mediaBank[U32(b, i)] = bk.name; mediaLoc[U32(b, i)] = new[] { U32(b, i + 4), U32(b, i + 8) }; } break;
                            case "HIRC": ParseHirc(bk, b); break;
                            case "STMG": ParseStmg(bk, b); break;
                            case "STID": ParseStid(b); break;
                        }
                    }
                    o += 8 + sz;
                }
            }
            log.Add(bk.name + ": v" + bk.version + ", " + bk.objects + " objects, " + bk.media + " media" + (bk.problems.Count > 0 ? ", problems: " + string.Join("; ", bk.problems.ToArray()) : ""));
        }

        static void ParseStid(byte[] b)
        {
            // u32 type, u32 count, { u32 bankId, u8 len, char[len] }
            int p = 4; uint n = U32(b, p); p += 4;
            for (uint i = 0; i < n && p + 5 <= b.Length; i++)
            {
                uint id = U32(b, p); int len = b[p + 4]; p += 5;
                if (p + len > b.Length) break;
                bankNames[id] = Encoding.ASCII.GetString(b, p, len); p += len;
            }
        }

        static void ParseStmg(Bank bk, byte[] b)
        {
            try
            {
                int p = 4 + 2;                                   // fVolumeThreshold, maxNumVoicesLimitInternal
                uint ns = U32(b, p); p += 4;
                for (uint i = 0; i < ns; i++)
                {
                    uint g = U32(b, p); p += 8;                  // group id, default transition time
                    uint nt = U32(b, p); p += 4 + (int)nt * 12;  // transitions (from, to, time)
                    stateGroups.Add(g);
                }
                uint nw = U32(b, p); p += 4;
                for (uint i = 0; i < nw; i++)
                {
                    uint g = U32(b, p); p += 9;                  // switch group id, rtpc id, rtpc type u8
                    uint np = U32(b, p); p += 4;                 // graph points: x f32, switch id u32, curve u32
                    for (uint k = 0; k < np; k++) { uint sv = U32(b, p + 4); p += 12; AddTo(switchValuesByGroup, g, sv); }
                    switchGroups.Add(g);
                }
                uint nr = U32(b, p); p += 4;
                for (uint i = 0; i < nr && p + 21 <= b.Length; i++) { rtpcs.Add(U32(b, p)); p += 21; }
            }
            catch (Exception e) { bk.problems.Add("STMG: " + e.Message); }
        }

        static void AddTo(Dictionary<uint, List<uint>> d, uint k, uint v)
        {
            List<uint> l;
            if (!d.TryGetValue(k, out l)) { l = new List<uint>(); d[k] = l; }
            if (!l.Contains(v)) l.Add(v);
        }

        static void ParseHirc(Bank bk, byte[] b)
        {
            uint n = U32(b, 0); int p = 4;
            for (uint i = 0; i < n; i++)
            {
                if (p + 5 > b.Length) { bk.problems.Add("HIRC truncated at object " + i); break; }
                var t = (HType)b[p]; int sz = (int)U32(b, p + 1);
                if (p + 5 + sz > b.Length) { bk.problems.Add("HIRC object " + i + " runs past chunk"); break; }
                var body = new byte[sz]; Buffer.BlockCopy(b, p + 5, body, 0, sz);
                var node = new Node { id = U32(body, 0), type = t, bank = bk.name, size = sz };
                try { ParseNode(node, body); } catch (Exception e) { node.note = "parse error: " + e.Message; }
                nodes[node.id] = node;
                raw[node.id] = body;
                int c; bk.counts.TryGetValue(t, out c); bk.counts[t] = c + 1;
                bk.objects++;
                p += 5 + sz;
            }
            if (p != b.Length) bk.problems.Add("HIRC: " + (b.Length - p) + " trailing bytes");
        }

        static readonly Dictionary<uint, byte[]> raw = new Dictionary<uint, byte[]>();   // object bytes, for the inferred-reference pass

        // NodeBaseParams (v112): overrideParentFx u8, numFx u8, [bypass u8, numFx * 7], u8 (always 0 in INSIDE), bus u32, parent u32
        static int NodeBase(Node n, byte[] b, int p)
        {
            int numFx = b[p + 1]; p += 2;
            if (numFx > 0) p += 1 + 7 * numFx;
            p += 1;
            n.bus = U32(b, p); n.parent = U32(b, p + 4);
            return p + 8;
        }

        // AkBankSourceData (v112): plugin u32, streamType u8, sourceId u32, fileId u32, [offset u32, size u32 unless streamed], bits u8
        static int Source(Node n, byte[] b, int p)
        {
            byte st = b[p + 4]; uint src = U32(b, p + 5);
            p += 13; if (st != 2) p += 8; p += 1;
            n.stream = st; n.media.Add(src);
            return p;
        }

        static void ParseNode(Node n, byte[] b)
        {
            switch (n.type)
            {
                case HType.Event:
                    {
                        uint c = U32(b, 4);
                        for (int i = 0; i < c; i++) n.links.Add(new Link { to = U32(b, 8 + i * 4), rel = "action", prov = Prov.Parsed });
                        break;
                    }
                case HType.Action:
                    {
                        n.actionType = U16(b, 4); n.target = U32(b, 6);
                        int p = 11;                                               // after isBus u8
                        int c = b[p]; p += 1 + c * 5;                             // AkPropBundle
                        int r = b[p]; p += 1 + r * 9;                             // ranged props (id u8, min u32, max u32)
                        int kind = n.actionType >> 8;
                        if (kind == 0x04 && p + 5 <= b.Length) n.actionBank = U32(b, p + 1);            // Play: fadeCurve u8, bankId u32
                        else if (kind == 0x1E && p + 14 <= b.Length)                                     // Seek: relative u8, value f32, random min f32, max f32, snap u8
                        { n.hasSeek = true; n.seekRelative = b[p] != 0; n.seekValue = BitConverter.ToSingle(b, p + 1); n.seekMin = BitConverter.ToSingle(b, p + 5); n.seekMax = BitConverter.ToSingle(b, p + 9); }
                        else if ((kind == 0x12 || kind == 0x19) && b.Length >= 8) { n.group = U32(b, b.Length - 8); n.value = U32(b, b.Length - 4); }  // SetState / SetSwitch: group, value at the end
                        if (n.target != 0) n.links.Add(new Link { to = n.target, rel = ActionName(n.actionType).ToLowerInvariant(), prov = Prov.Parsed });
                        break;
                    }
                case HType.Sound:
                    NodeBase(n, b, Source(n, b, 4));
                    break;
                case HType.RanSeq: case HType.Switch: case HType.ActorMixer: case HType.Layer:
                    NodeBase(n, b, 4); break;
                case HType.MusicSegment: case HType.MusicSwitch: case HType.MusicRanSeq:
                    NodeBase(n, b, 5); break;                                     // MusicNodeParams: flags u8 first
                case HType.MusicTrack:
                    {
                        int p = 5; uint ns = U32(b, p); p += 4;                   // flags u8, numSources
                        for (int i = 0; i < ns; i++) p = Source(n, b, p);
                        uint npl = U32(b, p); p += 4 + (int)npl * 40;             // playlist: track u32, source u32, 4 doubles
                        p += 4;                                                   // numSubTrack
                        uint nca = U32(b, p); p += 4;
                        for (int i = 0; i < nca; i++) { uint pts = U32(b, p + 8); p += 12 + (int)pts * 12; }
                        NodeBase(n, b, p);
                        break;
                    }
            }
        }

        public static string ActionName(ushort t)
        {
            switch (t >> 8)
            {
                case 0x01: return "Stop"; case 0x02: return "Pause"; case 0x03: return "Resume"; case 0x04: return "Play"; case 0x05: return "PlayAndContinue";
                case 0x06: return "Mute"; case 0x07: return "Unmute"; case 0x08: return "SetPitch"; case 0x09: return "ResetPitch"; case 0x0A: return "SetVolume";
                case 0x0B: return "ResetVolume"; case 0x0C: return "SetBusVolume"; case 0x0D: return "ResetBusVolume"; case 0x0E: return "SetLPF"; case 0x0F: return "ResetLPF";
                case 0x10: return "UseState"; case 0x11: return "UnuseState"; case 0x12: return "SetState"; case 0x13: return "SetGameParameter"; case 0x14: return "ResetGameParameter";
                case 0x19: return "SetSwitch"; case 0x1A: return "ToggleBypass"; case 0x1B: return "ResetBypass"; case 0x1C: return "Break"; case 0x1D: return "Trigger";
                case 0x1E: return "Seek"; case 0x1F: return "Release"; case 0x20: return "SetHPF"; case 0x21: return "PlayEvent"; case 0x22: return "ResetPlaylist";
                case 0x30: return "SetFX"; case 0x31: return "ResetFX";
            }
            return "Action0x" + t.ToString("X4");
        }

        // ---------------------------------------------------------------- link pass
        static void Link2()
        {
            foreach (var n in nodes.Values)
            {
                if (n.parent != 0) { Node p; if (nodes.TryGetValue(n.parent, out p)) p.children.Add(n.id); }
                foreach (var m in n.media) { List<uint> l; if (!mediaUsers.TryGetValue(m, out l)) { l = new List<uint>(); mediaUsers[m] = l; } l.Add(n.id); }
                if (n.type == HType.Action)
                {
                    int k = n.actionType >> 8;
                    if (k == 0x12) { stateGroups.Add(n.group); AddTo(stateValuesByGroup, n.group, n.value); }
                    if (k == 0x19) { switchGroups.Add(n.group); AddTo(switchValuesByGroup, n.group, n.value); }
                }
            }
            // inferred references for nodes whose layout is not parsed past the header (switch assignments, music
            // decision trees, layer associations): any 4-byte window equal to a known object / game sync id
            var syncs = new HashSet<uint>();
            foreach (var g in stateGroups) syncs.Add(g);
            foreach (var g in switchGroups) syncs.Add(g);
            foreach (var kv in switchValuesByGroup) foreach (var v in kv.Value) syncs.Add(v);
            foreach (var kv in stateValuesByGroup) foreach (var v in kv.Value) syncs.Add(v);
            foreach (var n in nodes.Values)
            {
                if (n.type != HType.Switch && n.type != HType.MusicSwitch && n.type != HType.MusicRanSeq && n.type != HType.MusicSegment && n.type != HType.Layer && n.type != HType.RanSeq) continue;
                byte[] b; if (!raw.TryGetValue(n.id, out b)) continue;
                var seen = new HashSet<uint>();
                for (int p = 4; p + 4 <= b.Length; p++)
                {
                    uint v = U32(b, p);
                    if (v == 0 || v == n.id || v == n.parent || v == n.bus || !seen.Add(v)) continue;
                    Node t;
                    // containers never reference events / actions: an id equal to one of those here is a switch or
                    // state value that shares the event's name (same FNV hash), not a link to the event
                    if (nodes.TryGetValue(v, out t) && t.type != HType.Bus && t.type != HType.AuxBus && t.type != HType.FxCustom && t.type != HType.FxShareSet && t.type != HType.Attenuation && t.type != HType.Event && t.type != HType.Action)
                        n.links.Add(new Link { to = v, rel = t.parent == n.id ? "child ref" : "references", prov = Prov.Inferred });
                    else if (syncs.Contains(v))
                        n.links.Add(new Link { to = v, rel = stateGroups.Contains(v) ? "state group" : switchGroups.Contains(v) ? "switch group" : "switch/state value", prov = Prov.Inferred });
                }
            }
            raw.Clear();
        }

        // ---------------------------------------------------------------- queries
        public static Node Get(uint id) { Node n; return nodes.TryGetValue(id, out n) ? n : null; }

        // plain words for what a non-Play action does to its target
        public static string ActionNote(Node a, Func<uint, string> name)
        {
            if (a == null || a.type != HType.Action) return null;
            int kind = a.actionType >> 8;
            string tgt = a.target != 0 ? name(a.target) : "?";
            switch (kind)
            {
                case 0x01: return "-> stops " + tgt;
                case 0x02: return "-> pauses " + tgt;
                case 0x03: return "-> resumes " + tgt;
                case 0x1E:
                    if (!a.hasSeek) return "-> seeks " + tgt;
                    if (a.seekMax > a.seekMin) return "-> jumps " + tgt + " to a RANDOM position " + Pct(a.seekValue + a.seekMin, a.seekRelative) + " - " + Pct(a.seekValue + a.seekMax, a.seekRelative) + " (a different start every time it plays)";
                    return "-> jumps " + tgt + " to " + Pct(a.seekValue, a.seekRelative);
                case 0x13: case 0x14: return "-> sets game parameter " + tgt;
            }
            return null;
        }
        static string Pct(float v, bool rel) { return rel ? (v * 100f).ToString("0") + "%" : (v / 1000f).ToString("0.##") + " s"; }

        public static IEnumerable<Node> ActionsOf(Node ev)
        {
            foreach (var l in ev.links) if (l.rel == "action") { var a = Get(l.to); if (a != null) yield return a; }
        }

        // what an event can play: Play-action targets and everything below them, down to sounds / tracks and
        // their media. Returns lines; the caller supplies naming.
        public static void Tree(uint id, Func<uint, string> name, StringBuilder sb, int depth, HashSet<uint> seen, int maxLines)
        {
            if (sb.Length > maxLines * 80) return;
            var n = Get(id);
            string ind = new string(' ', depth * 2);
            if (n == null) { sb.Append(ind).Append("? ").Append(name(id)).Append("  (not in any scanned bank)\n"); return; }
            if (!seen.Add(id)) { sb.Append(ind).Append("↺ ").Append(n.type).Append(' ').Append(name(id)).Append('\n'); return; }
            sb.Append(ind).Append(n.type).Append(' ').Append(name(id));
            if (n.type == HType.Action) sb.Append("  ").Append(ActionName(n.actionType)).Append(n.group != 0 ? " " + name(n.group) + " = " + name(n.value) : "");
            if (n.media.Count > 0) { sb.Append("  media "); foreach (var m in n.media) sb.Append(MediaLabel != null ? MediaLabel(m) + " " : m + (n.stream == 2 ? ".wem (streamed) " : n.stream == 1 ? ".wem (prefetch in " + (mediaBank.ContainsKey(m) ? mediaBank[m] : "?") + " + streamed) " : " (in bank " + (mediaBank.ContainsKey(m) ? mediaBank[m] : "?") + ") ")); }
            if (n.type == HType.Action) { string note = ActionNote(n, name); if (note != null) sb.Append("  ").Append(note); }
            sb.Append("   [").Append(n.bank).Append("]\n");
            if (depth > 12) return;
            if (n.type == HType.Event) foreach (var a in ActionsOf(n)) Tree(a.id, name, sb, depth + 1, seen, maxLines);
            else if (n.type == HType.Action) { if (n.target != 0 && (n.actionType >> 8) == 0x04) Tree(n.target, name, sb, depth + 1, seen, maxLines); }
            else
            {
                foreach (var c in n.children) Tree(c, name, sb, depth + 1, seen, maxLines);
                foreach (var l in n.links)
                    if (l.prov == Prov.Inferred && !n.children.Contains(l.to) && Get(l.to) != null && (l.rel == "references" || l.rel == "child ref"))
                    { sb.Append(ind).Append("  (inferred) "); Tree(l.to, name, sb, depth + 1, seen, maxLines); }
            }
        }

        // reverse: which events can (eventually) play this node / media
        public static List<uint> EventsReaching(uint id, int maxDepth = 16)
        {
            var res = new List<uint>();
            var targetsOf = ActionTargets();
            var frontier = new List<uint> { id }; var seen = new HashSet<uint> { id };
            for (int d = 0; d < maxDepth && frontier.Count > 0; d++)
            {
                var next = new List<uint>();
                foreach (var x in frontier)
                {
                    List<uint> acts;
                    if (targetsOf.TryGetValue(x, out acts)) foreach (var a in acts) foreach (var ev in eventsOfAction(a)) if (!res.Contains(ev)) res.Add(ev);
                    var n = Get(x);
                    if (n != null && n.parent != 0 && seen.Add(n.parent)) next.Add(n.parent);
                    List<uint> refs;
                    if (inferredParents.TryGetValue(x, out refs)) foreach (var r in refs) if (seen.Add(r)) next.Add(r);
                }
                frontier = next;
            }
            return res;
        }

        static Dictionary<uint, List<uint>> actionTargets, actionEvents, inferredParents;
        static Dictionary<uint, List<uint>> ActionTargets()
        {
            if (actionTargets != null) return actionTargets;
            actionTargets = new Dictionary<uint, List<uint>>(); actionEvents = new Dictionary<uint, List<uint>>(); inferredParents = new Dictionary<uint, List<uint>>();
            foreach (var n in nodes.Values)
            {
                if (n.type == HType.Action && (n.actionType >> 8) == 0x04 && n.target != 0) AddTo(actionTargets, n.target, n.id);
                if (n.type == HType.Event) foreach (var l in n.links) if (l.rel == "action") AddTo(actionEvents, l.to, n.id);
                foreach (var l in n.links) if (l.prov == Prov.Inferred && nodes.ContainsKey(l.to)) AddTo(inferredParents, l.to, n.id);
            }
            return actionTargets;
        }
        static List<uint> eventsOfAction(uint a) { List<uint> l; return actionEvents.TryGetValue(a, out l) ? l : new List<uint>(); }

        public static string Summary()
        {
            var sb = new StringBuilder();
            sb.Append(nodes.Count).Append(" objects in ").Append(banks.Count).Append(" banks (").Append(loadMs.ToString("0")).Append(" ms); ");
            int ev = 0, snd = 0, mus = 0; foreach (var n in nodes.Values) { if (n.type == HType.Event) ev++; else if (n.type == HType.Sound) snd++; else if (n.type >= HType.MusicSegment && n.type <= HType.MusicRanSeq) mus++; }
            sb.Append(ev).Append(" events, ").Append(snd).Append(" sounds, ").Append(mus).Append(" music nodes, ").Append(mediaUsers.Count).Append(" media ids, ")
              .Append(stateGroups.Count).Append(" state groups, ").Append(switchGroups.Count).Append(" switch groups, ").Append(rtpcs.Count).Append(" RTPCs");
            return sb.ToString();
        }
    }
}
