using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Spec §94: export of the reconstructed audio METADATA (no audio media is read or written, ever).
    //   _mod\export\audio_<time>\
    //     README.txt               what each file is
    //     audio_ids.csv            Wwise id dictionary: id, name, name source, kind
    //     audio_events.json        every event: bank, defined/missing, game references, runtime stats, posting call stacks
    //     audio_game_refs.csv      game-side references (area, object, where, provenance, event)
    //     audio_banks.csv          bank inventory (version, objects, media count, data size, loaded this session)
    //     audio_graph.graphml      HIRC graph (event -> action -> target -> ... -> sound) + game objects -> events
    //     audio_timeline.csv       playing ids seen this session (post, emitter, end, context, managed caller)
    //     audio_callbacks.csv      callbacks per playing id
    // The main thread takes a snapshot (a few ms); formatting and writing happen on a worker thread.
    public static class AudioExport
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public static string status = "";
        static volatile bool busy;

        sealed class EvSnap { public uint id; public string name, bank; public bool defined; public int posts, failed, withCb; public List<string[]> refs = new List<string[]>(); public List<string> stacks; }
        sealed class PlaySnap { public uint pid, ev; public string evName, emitter, context, caller; public float t, endT; public int frame; public bool ended; public List<string> cbs; }

        public static string Run()
        {
            if (busy) return "export already running";
            if (!AudioDb.Built) return "audio database not ready yet";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // ---- snapshot (main thread)
            var names = AudioTrace.SnapshotNames();
            var evs = new List<EvSnap>();
            foreach (var r in AudioDb.recs)
            {
                if (r.kind != AudioDb.Kind.Event) continue;
                var e = new EvSnap { id = r.id, name = r.Name, bank = r.bank, defined = r.node != null };
                foreach (var x in r.refs) e.refs.Add(new[] { x.prov, x.area, x.path, x.where, x.loaded ? "loaded" : "" });
                AudioTrace.EvStat st; if (AudioTrace.evStats.TryGetValue(r.id, out st)) { e.posts = st.posts; e.failed = st.failed; e.withCb = st.withCallbacks; }
                List<string> stacks; if (AudioSpy.postStacks.TryGetValue(r.id, out stacks)) e.stacks = new List<string>(stacks);
                evs.Add(e);
            }
            var refs = new List<string[]>();
            foreach (var x in AudioDb.refs.Values) refs.Add(new[] { x.area, x.path, x.where, x.prov, x.eventName, x.eventId.ToString(IC), x.loaded ? "1" : "0" });
            var plays = new List<PlaySnap>();
            foreach (var p in AudioTrace.plays.Values)
                plays.Add(new PlaySnap { pid = p.pid, ev = p.eventId, evName = AudioTrace.Name(p.eventId), emitter = AudioTrace.ObjName(p.gameObj), context = p.context, caller = p.caller, t = p.t, endT = p.endT, frame = p.frame, ended = p.ended, cbs = new List<string>(p.callbacks) });
            var loadedBanks = new HashSet<string>();
            foreach (var b in WwiseBanks.banks) if (AudioDb.BankLoaded(b.name)) loadedBanks.Add(b.name);
            string dir = Path.Combine(Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "export"), "audio_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            float snapMs = (float)sw.Elapsed.TotalMilliseconds;
            busy = true; status = "exporting to " + dir + " …";
            // ---- write (worker thread; WwiseBanks is read-only after load)
            var th = new System.Threading.Thread(() =>
            {
                var tw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    Directory.CreateDirectory(dir);
                    Func<uint, string> nm = id => { AudioTrace.IdName n; return names.TryGetValue(id, out n) ? n.name : ""; };
                    WriteIds(dir, names);
                    WriteEvents(dir, evs);
                    WriteRefs(dir, refs);
                    WriteBanks(dir, loadedBanks);
                    int nodesN, edgesN; WriteGraph(dir, nm, refs, out nodesN, out edgesN);
                    WriteTimeline(dir, plays);
                    WriteReadme(dir, evs.Count, refs.Count, plays.Count, nodesN, edgesN);
                    status = "exported " + evs.Count + " events, " + refs.Count + " game references, " + nodesN + " graph nodes / " + edgesN + " edges, " + plays.Count + " playing ids to " + dir + " (snapshot " + snapMs.ToString("0") + " ms, write " + tw.ElapsedMilliseconds + " ms on a worker thread)";
                }
                catch (Exception e) { status = "export failed: " + e.Message; }
                busy = false;
            });
            th.IsBackground = true; th.Priority = System.Threading.ThreadPriority.BelowNormal; th.Start();
            return status;
        }

        public static bool Busy { get { return busy; } }

        // ---------------------------------------------------------------- writers
        static string Csv(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " | ") + "\"";
        }
        static void Row(StringBuilder sb, params string[] cols) { for (int i = 0; i < cols.Length; i++) { if (i > 0) sb.Append(','); sb.Append(Csv(cols[i])); } sb.Append('\n'); }
        static string Xml(string s) { return s == null ? "" : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;"); }

        static string KindOf(uint id)
        {
            WwiseBanks.Node n;
            if (WwiseBanks.nodes.TryGetValue(id, out n)) return n.type.ToString();
            if (WwiseBanks.stateGroups.Contains(id)) return "StateGroup";
            if (WwiseBanks.switchGroups.Contains(id)) return "SwitchGroup";
            if (WwiseBanks.rtpcs.Contains(id)) return "RTPC";
            foreach (var b in WwiseBanks.banks) if (b.id == id) return "Bank";
            return "";
        }

        static void WriteIds(string dir, Dictionary<uint, AudioTrace.IdName> names)
        {
            var sb = new StringBuilder("id,name,name_source,kind\n");
            var ids = new List<uint>(names.Keys); ids.Sort();
            foreach (var id in ids) { var n = names[id]; Row(sb, id.ToString(IC), n.name, n.src.ToString(), KindOf(id)); }
            File.WriteAllText(Path.Combine(dir, "audio_ids.csv"), sb.ToString());
        }

        static void WriteEvents(string dir, List<EvSnap> evs)
        {
            var arr = new List<object>();
            foreach (var e in evs)
            {
                var o = new Json.Obj();
                o["id"] = (double)e.id; o["name"] = e.name; o["bank"] = e.bank ?? ""; o["defined_in_bank"] = e.defined;
                o["posts_this_session"] = (double)e.posts; o["failed_posts"] = (double)e.failed; o["posts_with_callbacks"] = (double)e.withCb;
                var rl = new List<object>();
                foreach (var r in e.refs) { var j = new Json.Obj(); j["provenance"] = r[0]; j["area"] = r[1]; j["object"] = r[2]; j["where"] = r[3]; j["loaded"] = r[4] == "loaded"; rl.Add(j); }
                o["game_references"] = rl;
                o["reference_status"] = e.refs.Count > 0 || e.posts > 0 ? "referenced" : "No game-side reference currently identified";
                if (e.stacks != null) o["posted_by"] = new List<object>(e.stacks.ConvertAll(x => (object)x));
                WwiseBanks.Node n;
                if (WwiseBanks.nodes.TryGetValue(e.id, out n))
                {
                    var acts = new List<object>();
                    foreach (var a in WwiseBanks.ActionsOf(n)) { var j = new Json.Obj(); j["action"] = (double)a.id; j["type"] = WwiseBanks.ActionName(a.actionType); j["target"] = (double)a.target; acts.Add(j); }
                    o["actions"] = acts;
                }
                arr.Add(o);
            }
            var root = new Json.Obj(); root["format"] = "insidedev-audio-events"; root["version"] = 1.0; root["note"] = "metadata only; no audio media"; root["events"] = arr;
            File.WriteAllText(Path.Combine(dir, "audio_events.json"), Json.Write(root) + "\n");
        }

        static void WriteRefs(string dir, List<string[]> refs)
        {
            var sb = new StringBuilder("area,object,where,provenance,event,event_id,loaded\n");
            foreach (var r in refs) Row(sb, r);
            File.WriteAllText(Path.Combine(dir, "audio_game_refs.csv"), sb.ToString());
        }

        static void WriteBanks(string dir, HashSet<string> loaded)
        {
            var sb = new StringBuilder("bank,id,version,objects,media_count,data_bytes,file_bytes,loaded_this_session,problems\n");
            foreach (var b in WwiseBanks.banks) Row(sb, b.name, b.id.ToString(IC), b.version.ToString(IC), b.objects.ToString(IC), b.media.ToString(IC), b.dataSize.ToString(IC), b.size.ToString(IC), loaded.Contains(b.name) ? "1" : "0", string.Join("; ", b.problems.ToArray()));
            File.WriteAllText(Path.Combine(dir, "audio_banks.csv"), sb.ToString());
        }

        static void WriteGraph(string dir, Func<uint, string> nm, List<string[]> refs, out int nodesN, out int edgesN)
        {
            nodesN = edgesN = 0;
            using (var w = new StreamWriter(Path.Combine(dir, "audio_graph.graphml"), false, new UTF8Encoding(false)))
            {
                w.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                w.WriteLine("<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\">");
                w.WriteLine("  <key id=\"type\" for=\"node\" attr.name=\"type\" attr.type=\"string\"/>");
                w.WriteLine("  <key id=\"name\" for=\"node\" attr.name=\"name\" attr.type=\"string\"/>");
                w.WriteLine("  <key id=\"bank\" for=\"node\" attr.name=\"bank\" attr.type=\"string\"/>");
                w.WriteLine("  <key id=\"rel\" for=\"edge\" attr.name=\"relation\" attr.type=\"string\"/>");
                w.WriteLine("  <key id=\"prov\" for=\"edge\" attr.name=\"provenance\" attr.type=\"string\"/>");
                w.WriteLine("  <graph id=\"inside_audio\" edgedefault=\"directed\">");
                foreach (var n in WwiseBanks.nodes.Values)
                {
                    w.WriteLine("    <node id=\"w" + n.id + "\"><data key=\"type\">" + n.type + "</data><data key=\"name\">" + Xml(nm(n.id)) + "</data><data key=\"bank\">" + Xml(n.bank) + "</data></node>");
                    nodesN++;
                }
                int eid = 0;
                Action<string, string, string, string> edge = (a, b, rel, prov) => { w.WriteLine("    <edge id=\"e" + (eid++) + "\" source=\"" + a + "\" target=\"" + b + "\"><data key=\"rel\">" + Xml(rel) + "</data><data key=\"prov\">" + prov + "</data></edge>"); };
                foreach (var n in WwiseBanks.nodes.Values)
                {
                    foreach (var c in n.children) if (WwiseBanks.nodes.ContainsKey(c)) edge("w" + n.id, "w" + c, "child", "BANK");
                    if (n.type == WwiseBanks.HType.Action && n.target != 0 && WwiseBanks.nodes.ContainsKey(n.target)) edge("w" + n.id, "w" + n.target, WwiseBanks.ActionName(n.actionType), "BANK");
                    foreach (var l in n.links)
                        if (l.rel != "child" && WwiseBanks.nodes.ContainsKey(l.to) && !(n.type == WwiseBanks.HType.Action && l.to == n.target))
                            edge("w" + n.id, "w" + l.to, l.rel, l.prov == WwiseBanks.Prov.Inferred ? "INFERRED" : "BANK");
                }
                // game objects -> events
                var gameNodes = new Dictionary<string, string>();
                foreach (var r in refs)
                {
                    uint ev; if (!uint.TryParse(r[5], NumberStyles.Integer, IC, out ev) || !WwiseBanks.nodes.ContainsKey(ev)) continue;
                    string key = r[0] + "|" + r[1], gid;
                    if (!gameNodes.TryGetValue(key, out gid))
                    {
                        gid = "g" + gameNodes.Count; gameNodes[key] = gid;
                        w.WriteLine("    <node id=\"" + gid + "\"><data key=\"type\">GameObject</data><data key=\"name\">" + Xml(r[1]) + "</data><data key=\"bank\">" + Xml(r[0]) + "</data></node>");
                        nodesN++;
                    }
                    edge(gid, "w" + ev, r[2], r[3]);
                }
                edgesN = eid;
                w.WriteLine("  </graph>");
                w.WriteLine("</graphml>");
            }
        }

        static void WriteTimeline(string dir, List<PlaySnap> plays)
        {
            plays.Sort((a, b) => a.t.CompareTo(b.t));
            var sb = new StringBuilder("playing_id,event_id,event,emitter,posted_s,frame,ended,end_s,context,managed_caller\n");
            var cb = new StringBuilder("playing_id,event,callback\n");
            foreach (var p in plays)
            {
                Row(sb, p.pid.ToString(IC), p.ev.ToString(IC), p.evName, p.emitter, p.t.ToString("0.000", IC), p.frame.ToString(IC), p.ended ? "1" : "0", p.ended ? p.endT.ToString("0.000", IC) : "", p.context, p.caller);
                foreach (var c in p.cbs) Row(cb, p.pid.ToString(IC), p.evName, c);
            }
            File.WriteAllText(Path.Combine(dir, "audio_timeline.csv"), sb.ToString());
            File.WriteAllText(Path.Combine(dir, "audio_callbacks.csv"), cb.ToString());
        }

        static void WriteReadme(string dir, int evs, int refs, int plays, int nodes, int edges)
        {
            var sb = new StringBuilder();
            sb.Append("INSIDE audio metadata export (InsideDev ").Append(Boot.Version).Append(", ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", IC)).Append(")\n\n");
            sb.Append("Reconstructed metadata only. No audio media (WEM data) is read or included.\n\n");
            sb.Append("audio_ids.csv          Wwise id dictionary (FNV-1 32 of the lower-case name), name source, kind\n");
            sb.Append("audio_events.json      ").Append(evs).Append(" events: bank, game references, runtime stats, posting call stacks, actions\n");
            sb.Append("audio_game_refs.csv    ").Append(refs).Append(" game-side references (FIELD / FSM / OBSERVED / DATA / ASSET / ANIM)\n");
            sb.Append("audio_banks.csv        bank inventory\n");
            sb.Append("audio_graph.graphml    ").Append(nodes).Append(" nodes, ").Append(edges).Append(" edges (open with yEd / Gephi / Cytoscape)\n");
            sb.Append("audio_timeline.csv     ").Append(plays).Append(" playing ids seen this session\n");
            sb.Append("audio_callbacks.csv    callbacks per playing id\n\n");
            sb.Append("Provenance: BANK = parsed from the SoundBank, INFERRED = byte-scan reference, FIELD/FSM/DATA/ASSET/ANIM = game data, OBSERVED = seen at runtime.\n");
            sb.Append("\"No game-side reference currently identified\" means none was found in the areas seen so far; it is not a claim that the content is unused.\n");
            File.WriteAllText(Path.Combine(dir, "README.txt"), sb.ToString());
        }
    }
}
