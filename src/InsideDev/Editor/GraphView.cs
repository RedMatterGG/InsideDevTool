using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Phase 7: reference graph around the current selection, built only from the ReferenceIndex (real data).
    //   columns   - incoming hops on the left, the selection in the middle, outgoing hops on the right
    //   edges     - one per (source, target) pair, styled by provenance:
    //                 SIGNAL  solid amber      FSM  dashed violet      FIELD  solid grey
    //                 REFLECTION dashed grey   OBSERVED (fired recently, Phase 8) animated
    //   layout    - recomputed only when the topology key changes (selection, hop count, index size, toggles)
    //   input     - click node = select (graph re-centres), hover = world highlight + edge details,
    //               drag background = pan, wheel = zoom, Fit = reset view
    public static class GraphView
    {
        sealed class Node
        {
            public int id; public ObjRecord rec; public string name, tag; public int col; public Vector2 pos; public Color color;
            public float w;
            public uint audio; public bool isAudio;      // Wwise object node (audio graph modes)
            public Type codeType; public GameObject live; public string tip;   // LOGIC mode: script type node
        }
        sealed class Edge
        {
            public Node a, b; public Provenance prov; public int count; public string label; public readonly List<string> fields = new List<string>();
            public bool audio; public uint evId;        // audio edge; animates when this event was posted recently
        }

        static readonly Dictionary<long, Node> nodes = new Dictionary<long, Node>();
        // Phase J/K: GAME (reference graph), WWISE (bank structure around the Audio DB selection or the selected
        // object's sounds), COMBINED (reference graph + the Wwise events each object posts, and their structure)
        static int mode = EditorState.Get("graph.mode", 0);
        static readonly string[] Modes = { "GAME", "WWISE", "COMBINED", "LOGIC" };
        static readonly Color cAudio = new Color(0.3f, 0.9f, 0.85f, 1f);
        static readonly List<Edge> edgesList = new List<Edge>();
        static string builtKey;
        static int hops = 1;
        static bool showFields = true;
        // edge-type filters (phase 8 leftover): fields / signals / state machines / other (adapter, observed, inferred)
        static bool fFields = true, fSignals = true, fFsm = true, fOther = true, fCode = true;
        // which kinds of LIVE activity light up the graph and fill its event strip (phase 8 leftover: event history by type)
        static bool liveSig = EditorState.Get("graph.live.s", true), liveFsm = EditorState.Get("graph.live.f", true), liveSnd = EditorState.Get("graph.live.a", true), liveAnim = EditorState.Get("graph.live.n", true);
        static bool LiveWanted(char k) { return k == 'S' ? liveSig : k == 'F' ? liveFsm : k == 'A' ? liveSnd : k == 'N' ? liveAnim : false; }
        public static void SetLive(string f)
        {
            if (f == "all") f = "sfan"; if (f == "none") f = "";
            liveSig = f.Contains("s"); liveFsm = f.Contains("f"); liveSnd = f.Contains("a"); liveAnim = f.Contains("n");
            EditorState.Set("graph.live.s", liveSig); EditorState.Set("graph.live.f", liveFsm); EditorState.Set("graph.live.a", liveSnd); EditorState.Set("graph.live.n", liveAnim);
        }
        public static string LiveFilter { get { return (liveSig ? "s" : "") + (liveFsm ? "f" : "") + (liveSnd ? "a" : "") + (liveAnim ? "n" : ""); } }
        // recent events raised by objects shown in the graph, filtered by the live kinds (rebuilt 4x per second)
        static readonly List<int> stripIdx = new List<int>(); static readonly List<string> stripText = new List<string>(); static float stripAt; static long stripTotal = -1; static string stripKey;
        public static string LiveDump()
        {
            Build(Selection.Current); BuildStrip();
            var sb = new System.Text.StringBuilder("graph live filter '" + LiveFilter + "' (s signals, f state machines, a sounds, n animation); recent events of graph objects:\n");
            foreach (var t in stripText) sb.Append("  ").Append(t).Append('\n');
            return sb.ToString();
        }
        static void BuildStrip()
        {
            float now = Time.realtimeSinceStartup;
            string k = builtKey + LiveFilter;
            if (now - stripAt < 0.25f && stripTotal == EventMonitor.total && k == stripKey) return;
            stripAt = now; stripTotal = EventMonitor.total; stripKey = k;
            stripIdx.Clear(); stripText.Clear();
            for (int i = 0; i < EventMonitor.Count && stripIdx.Count < 6; i++)
            {
                var e = EventMonitor.Get(i);
                if (!LiveWanted(e.kind) || !nodes.ContainsKey(e.goId)) continue;
                stripIdx.Add(i); stripText.Add(EventMonitor.KindName(e.kind) + " " + e.text);
            }
        }
        static bool EdgeWanted(RefEdge e)
        {
            switch (e.prov)
            {
                case Provenance.SerializedField: case Provenance.ReflectionField: return fFields;
                case Provenance.SignalConnection: return fSignals;
                case Provenance.StateMachine: return fFsm;
                case Provenance.Code: return fCode;
            }
            return fOther;
        }
        static Vector2 pan; static float zoom = Mathf.Clamp(EditorState.Get("graph.zoom", 1f), 0.3f, 2.5f);
        static bool panning; static Vector2 panStart, panOrigin;
        public static int NodeCount { get { return nodes.Count; } }
        public static int EdgeCount { get { return edgesList.Count; } }
        const float NodeH = 22f, ColW = 250f, RowH = 38f;

        static readonly Color cSignal = new Color(1f, 0.72f, 0.25f, 1f), cFsm = new Color(0.72f, 0.55f, 1f, 1f), cField = new Color(0.65f, 0.68f, 0.72f, 0.9f),
                              cReflect = new Color(0.55f, 0.58f, 0.62f, 0.7f), cObserved = new Color(0.3f, 1f, 0.5f, 1f);

        // text form of the graph for the bridge ("graph [hops]")
        // filter string: any of f s m o (fields, signals, fsm, other); "all" = everything
        public static void SetMode(string m) { int i = Array.IndexOf(Modes, m.ToUpperInvariant()); if (i >= 0) { mode = i; EditorState.Set("graph.mode", mode); builtKey = null; } }

        public static void SetFilter(string f)
        {
            if (f == "all") f = "fsmoc";
            fFields = f.Contains("f"); fSignals = f.Contains("s"); fFsm = f.Contains("m"); fOther = f.Contains("o"); fCode = f.Contains("c"); builtKey = null;
        }

        public static string Dump(int h)
        {
            if (h >= 1 && h <= 3) { hops = h; builtKey = null; }
            Build(Selection.Current);
            if (mode == 3 && liveQueue.Count > 0) { while (liveQueue.Count > 0) LiveTick(); Build(Selection.Current); }   // bridge: resolve now
            var sb = new System.Text.StringBuilder(Status() + "\n");
            var cols = new List<Node>(nodes.Values);
            cols.Sort((x, y) => x.col != y.col ? x.col.CompareTo(y.col) : string.CompareOrdinal(x.name, y.name));
            foreach (var n in cols) sb.Append("  col ").Append(n.col).Append("  ").Append(n.name).Append(n.tag.Length > 0 ? "  <" + n.tag + ">" : "").Append('\n');
            foreach (var e in edgesList) sb.Append("  ").Append(e.a.name).Append(" --").Append(e.audio ? (e.prov == Provenance.Inferred ? "AUDIO(INFERRED)" : "AUDIO") : ReferenceIndex.ProvLabel(e.prov)).Append(' ').Append(e.label).Append(e.count > 1 ? " +" + (e.count - 1) : "").Append("--> ").Append(e.b.name).Append('\n');
            return sb.ToString();
        }

        public static string Status() { return nodes.Count + " nodes, " + edgesList.Count + " edges, hops " + hops + ", key " + builtKey; }

        static Color KindColor(ObjRecord r)
        {
            if (r == null) return UI.Dim;
            if (!r.activeInHierarchy) return new Color(1f, 0.45f, 0.9f, 1f);
            if ((r.kind & ObjKind.Trigger) != 0) return new Color(1f, 0.9f, 0.2f, 1f);
            if ((r.kind & ObjKind.StateMachine) != 0) return new Color(0.72f, 0.55f, 1f, 1f);
            if ((r.kind & ObjKind.Signal) != 0) return cSignal;
            if ((r.kind & ObjKind.Audio) != 0) return new Color(0.45f, 0.85f, 1f, 1f);
            return UI.Txt;
        }

        static Color EdgeColor(Provenance p)
        {
            switch (p)
            {
                case Provenance.SignalConnection: return cSignal;
                case Provenance.StateMachine: return cFsm;
                case Provenance.SerializedField: return cField;
                case Provenance.Observed: return cObserved;
                case Provenance.Code: return new Color(0.4f, 0.95f, 0.95f, 0.95f);
                default: return cReflect;
            }
        }

        static int ProvRank(Provenance p) { return p == Provenance.SignalConnection ? 0 : p == Provenance.StateMachine ? 1 : p == Provenance.SerializedField ? 2 : 3; }

        // ---------------------------------------------------------------- build
        static void Build(GameObject center)
        {
            var arec = AudioDbPanel.Current;
            // LOGIC depends only on the selected object's scripts, the code graph and live-instance results
            string key = mode == 3
                ? (center != null ? center.GetInstanceID() : 0) + "/L/" + fFields + fSignals + fFsm + fCode + fOther + "/" + LogicPanel.Ready + "/" + liveVersion
                : (center != null ? center.GetInstanceID() : 0) + "/" + hops + "/" + ReferenceIndex.edges + "/" + ObjectDatabase.completedPasses + "/" + fFields + fSignals + fFsm + fCode + fOther
                  + "/" + mode + "/" + (arec != null ? arec.id : 0) + "/" + AudioDb.Version + "/" + SoundLibrary.defs.Count;
            if (key == builtKey) return;
            builtKey = key;
            nodes.Clear(); edgesList.Clear(); reachCache.Clear();
            if (mode == 1) { BuildWwise(center, arec); return; }
            if (mode == 3) { BuildLogic(center); return; }
            if (center == null) return;
            int cid = center.GetInstanceID();
            AddNode(cid, 0);
            var frontierOut = new List<int> { cid };
            var frontierIn = new List<int> { cid };
            var pairs = new Dictionary<long, Edge>();
            for (int h = 1; h <= hops; h++)
            {
                var nextOut = new List<int>();
                foreach (int n in frontierOut)
                    foreach (var e in ReferenceIndex.References(n))
                    {
                        if (e.noise || e.dstGo == 0 || e.dstGo == n || !EdgeWanted(e)) continue;
                        bool isNew = !nodes.ContainsKey(e.dstGo);
                        if (isNew && nodes.Count >= 80) continue;
                        if (isNew) { AddNode(e.dstGo, h); nextOut.Add(e.dstGo); }
                        Link(pairs, n, e.dstGo, e);
                    }
                var nextIn = new List<int>();
                foreach (int n in frontierIn)
                    foreach (var e in ReferenceIndex.Referencers(n))
                    {
                        if (e.noise || e.srcGo == 0 || e.srcGo == n || !EdgeWanted(e)) continue;
                        bool isNew = !nodes.ContainsKey(e.srcGo);
                        if (isNew && nodes.Count >= 80) continue;
                        if (isNew) { AddNode(e.srcGo, -h); nextIn.Add(e.srcGo); }
                        Link(pairs, e.srcGo, n, e);
                    }
                frontierOut = nextOut; frontierIn = nextIn;
            }
            if (mode == 2) AddPostedEvents(pairs);
            edgesList.AddRange(pairs.Values);
            Layout();
            var watch = new List<GameObject>(); foreach (var n in nodes.Values) if (n.rec != null && n.rec.go != null) watch.Add(n.rec.go);
            EventMonitor.WatchAnimations(watch);
            if (cid != fitCenter || hops != fitHops) { fitCenter = cid; fitHops = hops; needFit = true; }
        }

        // ---------------------------------------------------------------- LOGIC graph (code: who sets / reads / calls)
        static long TKey(Type t) { return (2L << 40) | (uint)t.FullName.GetHashCode(); }
        static readonly Color cLogicSet = new Color(1f, 0.7f, 0.35f, 1f), cLogicRead = new Color(0.4f, 0.95f, 0.95f, 1f);

        static Node AddType(Type t, int col, string extra)
        {
            Node n;
            if (nodes.TryGetValue(TKey(t), out n)) return n;
            GameObject live; int count;
            LiveOf(t, out live, out count);
            string nm = t.Name + (live != null ? "  (" + (count > 1 ? count + " loaded" : live.name) + ")" : count < 0 ? "  (checking…)" : typeof(Component).IsAssignableFrom(t) ? "  (none active)" : "  (code)");
            n = new Node { id = 0, codeType = t, live = live, col = col, name = nm, tag = "code", color = live != null ? new Color(0.9f, 0.9f, 0.95f, 1f) : new Color(0.6f, 0.62f, 0.7f, 1f) };
            n.tip = extra;
            n.w = Mathf.Clamp(nm.Length * 7.2f + 16, 90, ColW - 30);
            nodes[TKey(t)] = n;
            return n;
        }

        // live instances per type, cached (FindObjectsOfType is expensive; the graph rebuilds on topology changes)
        // FindObjectsOfType costs 10-30 ms each in a loaded area, so lookups are queued and resolved one per frame
        // (LiveTick); a build uses what is known and the graph rebuilds when a result changes (liveVersion).
        static readonly Dictionary<Type, KeyValuePair<GameObject, int>> liveCache = new Dictionary<Type, KeyValuePair<GameObject, int>>();
        static readonly List<Type> liveQueue = new List<Type>();
        static float liveCacheAt; static int liveStream = -1, liveVersion;
        static void LiveOf(Type t, out GameObject live, out int count)
        {
            KeyValuePair<GameObject, int> kv;
            if (liveCache.TryGetValue(t, out kv)) { live = kv.Key; count = kv.Value; if (kv.Value > 0 && live == null && !liveQueue.Contains(t)) liveQueue.Add(t); return; }
            live = null; count = -1;
            if (typeof(Component).IsAssignableFrom(t) && !liveQueue.Contains(t)) liveQueue.Add(t);
        }
        public static void LiveTick()
        {
            if (Time.realtimeSinceStartup - liveCacheAt > 10f || Levels.Changed(ref liveStream))
            {
                liveCacheAt = Time.realtimeSinceStartup;                     // refresh known types in the background, keep old values meanwhile
                foreach (var t in liveCache.Keys) if (!liveQueue.Contains(t)) liveQueue.Add(t);
            }
            if (liveQueue.Count == 0) return;
            var ty = liveQueue[0]; liveQueue.RemoveAt(0);
            GameObject live = null; int count = 0;
            try { foreach (var o in UnityEngine.Object.FindObjectsOfType(ty)) { var c = o as Component; if (c == null) continue; if (live == null) live = c.gameObject; count++; } } catch { }
            KeyValuePair<GameObject, int> old;
            bool changed = !liveCache.TryGetValue(ty, out old) || old.Value != count || old.Key != live;
            liveCache[ty] = new KeyValuePair<GameObject, int>(live, count);
            if (changed) liveVersion++;
        }

        static void LinkT(Dictionary<long, Edge> pairs, Node a, Node b, string label, bool sets)
        {
            if (a == null || b == null || a == b) return;
            long k = (a.codeType != null ? TKey(a.codeType) : a.id) * 31 ^ (b.codeType != null ? TKey(b.codeType) : b.id);
            Edge ed;
            if (!pairs.TryGetValue(k, out ed)) pairs[k] = ed = new Edge { a = a, b = b, prov = Provenance.Code, label = label };
            else if (ed.fields.Count < 12 && !ed.label.Contains(label)) ed.label = ed.label.Length < 40 ? ed.label + ", " + label : ed.label;
            if (sets) ed.audio = false;
            ed.count++;
            if (ed.fields.Count < 12) ed.fields.Add("[CODE] " + label);
        }

        static void BuildLogic(GameObject center)
        {
            var pairs = new Dictionary<long, Edge>();
            var centers = new List<Type>();
            if (center != null)
                foreach (var mb in center.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue; string an = mb.GetType().Assembly.GetName().Name;
                    if ((an == "Assembly-CSharp" || an == "Assembly-CSharp-firstpass") && !centers.Contains(mb.GetType())) centers.Add(mb.GetType());
                }
            if (!LogicPanel.Ready || centers.Count == 0) { edgesList.Clear(); Layout(); return; }
            foreach (var t in centers)
            {
                var cn = AddType(t, 0, "script on " + center.name);
                // who sets / reads / calls members of this script (left)
                foreach (var acc in CodeGraph.Incoming(t))
                {
                    if (acc.by.type.Name.StartsWith("<") || nodes.Count >= 70) continue;
                    var an = AddType(acc.by.type, -1, null);
                    LinkT(pairs, an, cn, acc.verb + " " + acc.member + (acc.guard != null ? " if " + Short(acc.guard) : "") + " (" + acc.by.name + ")", acc.verb != "reads");
                    if (hops >= 2)
                        foreach (var caller in acc.by.callers)
                        {
                            if (caller.type == acc.by.type || nodes.Count >= 70) continue;
                            var cc = AddType(caller.type, -2, null);
                            LinkT(pairs, cc, an, "calls " + acc.by.name, true);
                        }
                    // entry points of the accessing method become part of the tooltip
                    if (acc.by.entries.Count > 0) an.tip = (an.tip != null ? an.tip + " | " : "") + acc.by.name + " runs on: " + string.Join(", ", acc.by.entries.ToArray());
                }
                // what this script does to other types (right)
                List<CodeGraph.MInfo> ms;
                if (CodeGraph.byType.TryGetValue(t, out ms))
                    foreach (var mi in ms)
                        foreach (var e in mi.effects)
                        {
                            if (e.type == null || e.type == t || e.kind == "read" || e.type.Name.StartsWith("<") || e.type.Name == "ScriptGlobals" || nodes.Count >= 70) continue;
                            if (e.recv == "this") continue;
                            var bn = AddType(e.type, 1, null);
                            LinkT(pairs, cn, bn, (e.kind == "write" ? "sets " : e.kind == "call" ? "calls " : e.kind + " ") + e.member + " (" + mi.name + ")", e.kind != "read");
                        }
            }
            edgesList.AddRange(pairs.Values);
            Layout();
            int fc = center.GetInstanceID() ^ 0x3C3C3C;
            if (fc != fitCenter || hops != fitHops) { fitCenter = fc; fitHops = hops; needFit = true; }
        }

        static string Short(string g) { return g.Length > 40 ? g.Substring(0, 39) + "…" : g; }

        // ---------------------------------------------------------------- audio graph (Phase J / K)
        static long AKey(uint id) { return (1L << 40) | id; }
        static uint tipFor; static int tipPosts; static List<string> tipLines;

        static Node AddAudio(uint id, int col)
        {
            Node n;
            if (nodes.TryGetValue(AKey(id), out n)) return n;
            var wn = WwiseBanks.Get(id);
            string nm = AudioTrace.Name(id);
            n = new Node { id = 0, audio = id, isAudio = true, col = col, name = nm.StartsWith("#") ? (wn != null ? wn.type + " " : "") + "#" + id : nm, tag = wn != null ? wn.type.ToString() : "not in banks" };
            n.color = wn == null ? new Color(1f, 0.45f, 0.4f, 1f) : wn.type == WwiseBanks.HType.Event ? cAudio : wn.type >= WwiseBanks.HType.MusicSegment && wn.type <= WwiseBanks.HType.MusicRanSeq ? new Color(1f, 0.8f, 0.4f, 1f) : wn.type == WwiseBanks.HType.Action ? new Color(0.6f, 0.75f, 0.8f, 1f) : new Color(0.55f, 0.8f, 1f, 1f);
            if (wn != null && wn.type == WwiseBanks.HType.Action) n.name = WwiseBanks.ActionName(wn.actionType) + (wn.group != 0 ? " " + AudioTrace.Name(wn.group) + "=" + AudioTrace.Name(wn.value) : "") + (wn.hasSeek && wn.seekMax > wn.seekMin ? " (random start)" : "");
            // sounds and music tracks are named after the file they play (image.wav) when Wwise gave them no name
            if (wn != null && wn.media.Count > 0 && nm.StartsWith("#")) { var mi = AudioMedia.Get(wn.media[0]); n.name = mi.File + (wn.media.Count > 1 ? " +" + (wn.media.Count - 1) : ""); if (mi.fileMissing) n.color = new Color(1f, 0.45f, 0.4f, 1f); }
            n.w = Mathf.Clamp(n.name.Length * 7.2f + 16, 90, ColW - 30);
            nodes[AKey(id)] = n;
            return n;
        }

        static void LinkA(Dictionary<long, Edge> pairs, Node a, Node b, string label, bool inferred, uint evId)
        {
            if (a == null || b == null || a == b) return;
            long k = (a.isAudio ? AKey(a.audio) : a.id) * 31 ^ (b.isAudio ? AKey(b.audio) : b.id);
            Edge ed;
            if (!pairs.TryGetValue(k, out ed)) pairs[k] = ed = new Edge { a = a, b = b, prov = inferred ? Provenance.Inferred : Provenance.Adapter, audio = true, evId = evId, label = label };
            ed.count++;
            if (ed.fields.Count < 12) ed.fields.Add((inferred ? "[INFERRED] " : "[PARSED] ") + label);
        }

        // event -> actions -> targets -> children, up to `depth` columns to the right of `col`
        static void AudioTree(Dictionary<long, Edge> pairs, uint id, int col, int depth, uint evId)
        {
            var from = AddAudio(id, col);
            var n = WwiseBanks.Get(id);
            if (n == null || depth <= 0 || nodes.Count >= 90) return;
            if (n.type == WwiseBanks.HType.Event)
            {
                foreach (var a in WwiseBanks.ActionsOf(n)) { if (nodes.Count >= 90) break; bool isNew = !nodes.ContainsKey(AKey(a.id)); var an = AddAudio(a.id, col + 1); LinkA(pairs, from, an, "action", false, id); if (isNew) AudioTree(pairs, a.id, col + 1, depth - 1, id); }
                return;
            }
            if (n.type == WwiseBanks.HType.Action)
            {
                if (n.target != 0 && WwiseBanks.Get(n.target) != null && (n.actionType >> 8) != 0x12 && (n.actionType >> 8) != 0x19)
                { bool isNew = !nodes.ContainsKey(AKey(n.target)); var t = AddAudio(n.target, col + 1); LinkA(pairs, from, t, WwiseBanks.ActionName(n.actionType).ToLowerInvariant(), false, evId); if (isNew) AudioTree(pairs, n.target, col + 1, depth - 1, evId); }
                return;
            }
            foreach (var c in n.children)
            {
                if (nodes.Count >= 90) break;
                bool isNew = !nodes.ContainsKey(AKey(c)); var cn = AddAudio(c, col + 1); LinkA(pairs, from, cn, "child", false, evId);
                if (isNew) AudioTree(pairs, c, col + 1, depth - 1, evId);
            }
            foreach (var l in n.links)
                if (l.prov == WwiseBanks.Prov.Inferred && WwiseBanks.Get(l.to) != null && !n.children.Contains(l.to) && nodes.Count < 90)
                { bool isNew = !nodes.ContainsKey(AKey(l.to)); var cn = AddAudio(l.to, col + 1); LinkA(pairs, from, cn, l.rel, true, evId); if (isNew) AudioTree(pairs, l.to, col + 1, depth - 1, evId); }
        }

        static void BuildWwise(GameObject center, AudioDb.Rec arec)
        {
            var pairs = new Dictionary<long, Edge>();
            uint root = arec != null ? arec.id : 0;
            if (root == 0 && center != null) { var evs = EventsOf(center); if (evs.Count > 0) root = AudioTrace.Hash(evs[0]); }
            if (root != 0)
            {
                AudioTree(pairs, root, 0, hops * 2 + 1, root);
                // left: loaded game objects that reference / posted this event
                Rec0(pairs, root);
            }
            edgesList.AddRange(pairs.Values);
            Layout();
            int fc = (int)(root & 0x7FFFFFFF);
            if (fc != fitCenter || hops != fitHops) { fitCenter = fc; fitHops = hops; needFit = true; }
        }

        static void Rec0(Dictionary<long, Edge> pairs, uint ev)
        {
            AudioDb.Rec r; if (!AudioDb.byId.TryGetValue(ev, out r)) return;
            var root = AddAudio(ev, 0);
            foreach (var x in r.refs)
            {
                if (x.live == null || x.live.go == null || nodes.Count >= 90) continue;
                var go = x.live.postTarget != null ? x.live.postTarget : x.live.go;
                int gid = x.live.go.GetInstanceID();
                if (!nodes.ContainsKey(gid)) AddNode(gid, -1);
                Node gn; if (nodes.TryGetValue(gid, out gn)) LinkA(pairs, gn, root, "posts (" + x.prov + " " + x.where + ")", false, ev);
            }
        }

        static List<string> EventsOf(GameObject g)
        {
            var l = new List<string>();
            foreach (var d in SoundLibrary.defs.Values) foreach (var b in d.bindings) if ((b.go == g || b.postTarget == g) && !l.Contains(d.name)) l.Add(d.name);
            return l;
        }

        // COMBINED: every game node gets the events it posts (one column further out) and their first levels
        static void AddPostedEvents(Dictionary<long, Edge> pairs)
        {
            var gameNodes = new List<Node>(nodes.Values);
            foreach (var n in gameNodes)
            {
                if (n.isAudio || n.rec == null || n.rec.go == null || nodes.Count >= 90) continue;
                foreach (var ev in EventsOf(n.rec.go))
                {
                    uint id = AudioTrace.Hash(ev);
                    int col = n.col >= 0 ? n.col + 1 : n.col - 1;
                    bool isNew = !nodes.ContainsKey(AKey(id));
                    var en = AddAudio(id, col);
                    LinkA(pairs, n, en, "posts", false, id);
                    if (isNew && n.col == 0) AudioTree(pairs, id, col, 2, id);
                }
            }
        }

        static void AddNode(int id, int col)
        {
            var r = ObjectDatabase.Get(id);
            var n = new Node { id = id, rec = r, col = col, name = r != null ? r.name : ReferenceIndex.NameOf(id) };
            n.tag = r != null && r.kind != ObjKind.None ? r.KindLabel : "";
            n.color = KindColor(r);
            n.w = Mathf.Clamp(Mathf.Max(n.name.Length, n.tag.Length * 0.8f) * 7.2f + 16, 90, ColW - 30);
            nodes[(long)id] = n;
        }

        static void Link(Dictionary<long, Edge> pairs, int from, int to, RefEdge e)
        {
            Node a, b;
            if (!nodes.TryGetValue(from, out a) || !nodes.TryGetValue(to, out b)) return;
            long k = ((long)from << 32) ^ (uint)to;
            Edge ed;
            if (!pairs.TryGetValue(k, out ed)) pairs[k] = ed = new Edge { a = a, b = b, prov = e.prov };
            if (ProvRank(e.prov) < ProvRank(ed.prov)) ed.prov = e.prov;   // strongest provenance wins the style
            ed.count++;
            string f = e.field;
            if (f.Length > 48) f = "…" + f.Substring(f.Length - 47);
            if (ed.fields.Count < 12) ed.fields.Add("[" + ReferenceIndex.ProvLabel(e.prov) + "] " + e.srcType + "." + f);
            if (ed.label == null) ed.label = ShortField(e);
        }

        static string ShortField(RefEdge e)
        {
            string f = e.field;
            if (e.prov == Provenance.Code)
            {
                int ar = f.IndexOf('→'), inn = f.IndexOf("  (in ");
                string s = ar >= 0 ? f.Substring(ar + 1, (inn > ar ? inn : f.Length) - ar - 1).Trim() : f;
                return s.Length > 30 ? s.Substring(0, 29) + "…" : s;
            }
            int gt = f.LastIndexOf('>');
            if (e.prov == Provenance.SignalConnection && gt >= 0) return f.Substring(gt + 1).Trim();
            int dot = f.LastIndexOf('.');
            if (dot >= 0 && dot < f.Length - 1) f = f.Substring(dot + 1);
            return f.Length > 26 ? f.Substring(0, 25) + "…" : f;
        }

        static bool needFit;
        static int fitCenter = -1, fitHops = -1;
        // userView: the user zoomed or panned; new selections then keep that zoom and are only re-centred
        static bool userView = EditorState.Get("graph.userzoom", false);
        static void SaveView() { EditorState.Set("graph.userzoom", userView); EditorState.Set("graph.zoom", zoom); }
        static void Layout()
        {
            var byCol = new SortedDictionary<int, List<Node>>();
            foreach (var n in nodes.Values) { List<Node> l; if (!byCol.TryGetValue(n.col, out l)) byCol[n.col] = l = new List<Node>(); l.Add(n); }
            // column x positions from the widest node of each column (centre column at x = 0)
            var colW = new Dictionary<int, float>();
            foreach (var kv in byCol) { float w = 0; foreach (var n in kv.Value) w = Mathf.Max(w, n.w); colW[kv.Key] = w; }
            const float gap = 110f;
            var colX = new Dictionary<int, float> { { 0, 0f } };
            for (int c = 1; byCol.ContainsKey(c); c++) colX[c] = colX[c - 1] + colW[c - 1] * 0.5f + gap + colW[c] * 0.5f;
            for (int c = -1; byCol.ContainsKey(c); c--) colX[c] = colX[c + 1] - colW[c + 1] * 0.5f - gap - colW[c] * 0.5f;
            foreach (var kv in byCol)
            {
                var l = kv.Value;
                l.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
                float total = l.Count * RowH;
                float cx; if (!colX.TryGetValue(kv.Key, out cx)) cx = kv.Key * ColW;
                for (int i = 0; i < l.Count; i++) l[i].pos = new Vector2(cx, -total * 0.5f + i * RowH + RowH * 0.5f);
            }
        }

        // zoom/pan so the whole graph fits the canvas (max 1:1)
        static void Fit(Rect area)
        {
            needFit = false;
            if (nodes.Count == 0) { pan = Vector2.zero; zoom = 1f; return; }
            float minX = 1e9f, maxX = -1e9f, minY = 1e9f, maxY = -1e9f;
            foreach (var n in nodes.Values)
            {
                minX = Mathf.Min(minX, n.pos.x - n.w * 0.5f); maxX = Mathf.Max(maxX, n.pos.x + n.w * 0.5f);
                minY = Mathf.Min(minY, n.pos.y - NodeH); maxY = Mathf.Max(maxY, n.pos.y + NodeH);
            }
            float w = maxX - minX + 40, h = maxY - minY + 40;
            if (!userView) zoom = Mathf.Clamp(Mathf.Min(area.width / w, area.height / h), 0.3f, 1f);
            pan = -new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f) * zoom;
        }

        // ---------------------------------------------------------------- draw
        public static void Draw(UI ui)
        {
            var center = Selection.Current;
            if (mode == 3) LiveTick();
            Build(center);

            ui.BeginRow();
            if (ui.Button("<", 22, Selection.CanBack ? (Color?)null : UI.BgAlt) && Selection.CanBack) Selection.Back();
            if (ui.Button(">", 22, Selection.CanForward ? (Color?)null : UI.BgAlt) && Selection.CanForward) Selection.Forward();
            string head = mode == 3 ? (nodes.Count == 0 ? "LOGIC: select an object with game scripts (code scan " + (LogicPanel.Ready ? "ready" : "running") + ")." : "Code logic around " + (center != null ? center.name : "?") + ": " + nodes.Count + " script types, " + edgesList.Count + " links   (left: who sets / reads / calls it, right: what it does)") : mode == 1 ? (nodes.Count == 0 ? "WWISE: select an event in the Audio DB (or an object that posts sounds)." : "Wwise structure: " + nodes.Count + " nodes, " + edgesList.Count + " links")
                        : center == null ? "Select an object (Explorer, Objects, Inspector, bridge) to see its reference graph." : "Graph of " + center.name + ":  " + nodes.Count + " objects, " + edgesList.Count + " links";
            ui.Label(head, UI.Dim, Mathf.Max(100, ui.Width - 300));
            if (ui.Button(Modes[mode])) { mode = (mode + 1) % 4; EditorState.Set("graph.mode", mode); builtKey = null; needFit = true; }
            if (ui.Button(hops == 1 ? "1 hop" : hops + " hops")) { hops = hops % 3 + 1; builtKey = null; }
            bool sf = ui.Toggle(showFields, "labels"); if (sf != showFields) showFields = sf;
            fFields = ui.Toggle(fFields, "fields"); fSignals = ui.Toggle(fSignals, "signals"); fFsm = ui.Toggle(fFsm, "fsm"); fCode = ui.Toggle(fCode, "code"); fOther = ui.Toggle(fOther, "other");
            if (ui.Button("Fit")) { userView = false; needFit = true; SaveView(); }
            ui.EndRow();
            ui.BeginRow();
            ui.Label("live:", UI.Dim, 34);
            bool ls = ui.Toggle(liveSig, "signals"), lf = ui.Toggle(liveFsm, "state machines"), la = ui.Toggle(liveSnd, "sounds"), ln = ui.Toggle(liveAnim, "animation");
            if (ls != liveSig || lf != liveFsm || la != liveSnd || ln != liveAnim) SetLive((ls ? "s" : "") + (lf ? "f" : "") + (la ? "a" : "") + (ln ? "n" : ""));
            ui.EndRow();
            if (mode != 1 && mode != 3)
            {
                BuildStrip();
                float now = Time.realtimeSinceStartup;
                for (int i = 0; i < stripIdx.Count; i++)
                {
                    var e = EventMonitor.Get(stripIdx[i]);
                    var c = EventsPanel.KindColor(e.kind); if (now - e.t < 1.5f) c = Color.Lerp(Color.white, c, (now - e.t) / 1.5f);
                    if (ui.Item(stripText[i], c)) { var r = ObjectDatabase.Get(e.goId); if (r != null) Selection.Set(r.go, "graph"); }
                }
            }
            ui.Label("amber = signal   violet dashed = state machine   grey = field   cyan dashed = code (singleton / lookup)   grey dashed = reflection      drag = pan   wheel = zoom   click = select", UI.Dim);

            var area = ui.Canvas(ui.Remaining);
            var d = ui.D;
            d.Fill(area, new Color(0, 0, 0, 0.25f));
            if (center == null && nodes.Count == 0) return;
            if (mode == 3 && !LogicPanel.Ready) builtKey = null;   // rebuild once the code scan completes
            if (needFit) Fit(area);
            d.PushClip(area);
            try { DrawGraph(ui, d, area); }
            finally { d.PopClip(); }
        }

        static Vector2 ToScreen(Rect area, Vector2 p) { return area.center + pan + p * zoom; }

        static void DrawGraph(UI ui, Draw d, Rect area)
        {
            bool hot = ui.Hot(area);
            if (hot && ui.Wheel != 0)
            {
                float old = zoom;
                zoom = Mathf.Clamp(zoom * (ui.Wheel > 0 ? 1.15f : 1f / 1.15f), 0.3f, 2.5f);
                userView = true; SaveView();
                var m = ui.mouse - area.center - pan;
                pan -= m * (zoom / old - 1f);
            }

            // hover / click hit test first (nodes on top)
            Node hoverNode = null;
            foreach (var n in nodes.Values)
            {
                var r = NodeRect(area, n);
                if (hot && r.Contains(ui.mouse)) hoverNode = n;
            }
            if (hot && ui.click && hoverNode == null) { panning = true; panStart = ui.mouse; panOrigin = pan; }
            if (panning) { if (ui.held) { pan = panOrigin + (ui.mouse - panStart); if ((ui.mouse - panStart).sqrMagnitude > 9) userView = true; } else panning = false; }

            Edge hoverEdge = null;
            float best = 8f;
            foreach (var e in edgesList)
            {
                Vector2 a = Anchor(area, e.a, true), b = Anchor(area, e.b, false);
                bool emph = hoverNode != null && (e.a == hoverNode || e.b == hoverNode);
                DrawEdge(d, a, b, e, emph);
                if (hot && hoverNode == null)
                {
                    float dist = DistToSegment(ui.mouse, a, b);
                    if (dist < best) { best = dist; hoverEdge = e; }
                }
            }
            // §89: hovered edge between two world objects -> highlighted in the world overlay
            if (hoverEdge != null && hoverEdge.a != null && hoverEdge.b != null && hoverEdge.a.id != 0 && hoverEdge.b.id != 0) { HoverA = hoverEdge.a.id; HoverB = hoverEdge.b.id; HoverAt = Time.realtimeSinceStartup; }
            var ar = AudioDbPanel.Current;
            linkAge.Clear();
            foreach (var e in edgesList)
            {
                if (e.a == null || e.b == null || e.a.id == 0 || e.b.id == 0) continue;
                float fa = EventMonitor.FiredAge(e.a.id, e.b.id);
                if (fa < 2f) { float o; if (!linkAge.TryGetValue(e.a.id, out o) || fa < o) linkAge[e.a.id] = fa; if (!linkAge.TryGetValue(e.b.id, out o) || fa < o) linkAge[e.b.id] = fa; }
            }
            foreach (var n in nodes.Values) DrawNode(d, area, n, n == hoverNode, n.isAudio ? (ar != null && ar.id == n.audio && mode == 1) : n.id == (Selection.Current != null ? Selection.Current.GetInstanceID() : 0));
            if (showFields && zoom >= 0.6f) DrawLabels(d, area, hoverNode);

            if (hoverNode != null && hoverNode.codeType != null)
            {
                var lines = new List<string> { hoverNode.codeType.FullName + (hoverNode.live != null ? "   live: " + Inspector.PathOf(hoverNode.live.transform) : "   no loaded instance") };
                if (hoverNode.tip != null) foreach (var part in hoverNode.tip.Split('|')) lines.Add(part.Trim());
                lines.Add(hoverNode.live != null ? "click = select the loaded object   (Logic panel shows the full code logic)" : "click = open its code logic");
                Tooltip(d, area, ui.mouse, lines);
                if (ui.click) { if (hoverNode.live != null) Selection.Set(hoverNode.live, "graph"); LogicPanel.Show(hoverNode.codeType); }
            }
            else if (hoverNode != null && hoverNode.isAudio)
            {
                var wn = WwiseBanks.Get(hoverNode.audio);
                AudioTrace.EvStat st; bool posted = AudioTrace.evStats.TryGetValue(hoverNode.audio, out st);
                int posts = posted ? st.posts : 0;
                if (tipFor != hoverNode.audio || tipPosts != posts)
                {
                    tipFor = hoverNode.audio; tipPosts = posts;
                    tipLines = new List<string> { hoverNode.name + "   id " + hoverNode.audio, (wn != null ? wn.type + " in " + wn.bank : "not in any scanned bank") + (posted ? "   posted " + st.posts + "x" : "") };
                    if (wn != null) { string note = WwiseBanks.ActionNote(wn, AudioDb.NameOf); if (note != null) tipLines.Add(note); foreach (var m in wn.media) tipLines.AddRange(AudioMedia.Details(m)); }
                    tipLines.Add("click = open in Audio DB");
                }
                Tooltip(d, area, ui.mouse, tipLines);
                if (ui.click) { AudioDbPanel.Select(AudioDb.RecFor(hoverNode.audio)); }
            }
            else if (hoverNode != null && hoverNode.rec != null && hoverNode.rec.go != null)
            {
                Selection.SetHover(hoverNode.rec.go);
                Tooltip(d, area, ui.mouse, new List<string> { hoverNode.rec.path, hoverNode.tag + (hoverNode.rec.activeInHierarchy ? "" : "   [inactive]") });
                if (ui.click) Selection.Set(hoverNode.rec.go, "graph");
            }
            else if (hoverEdge != null)
            {
                var lines = new List<string> { hoverEdge.a.name + "  ->  " + hoverEdge.b.name + "   (" + hoverEdge.count + " link" + (hoverEdge.count > 1 ? "s" : "") + ")" };
                lines.AddRange(hoverEdge.fields);
                Tooltip(d, area, ui.mouse, lines);
            }
        }

        static Rect NodeRect(Rect area, Node n)
        {
            var c = ToScreen(area, n.pos);
            float w = n.w * zoom, h = NodeH * zoom;
            return new Rect(c.x - w * 0.5f, c.y - h * 0.5f, w, h);
        }

        static Vector2 Anchor(Rect area, Node n, bool outgoing)
        {
            var r = NodeRect(area, n);
            return new Vector2(outgoing ? r.xMax : r.xMin, r.center.y);
        }

        // §78 / §90: runtime activity. A node flashes (fading over 2 s) when it was just active:
        //   Wwise event posted; container / sound under a posted event (the whole reachable path - which child a random
        //   or switch container picked is not reported by Wwise); game object that posted audio or whose link fired.
        public static int HoverA, HoverB; public static float HoverAt = -9f;   // graph edge under the mouse (world sync)
        static readonly Dictionary<uint, List<uint>> reachCache = new Dictionary<uint, List<uint>>();
        static readonly Dictionary<int, float> linkAge = new Dictionary<int, float>();
        static float ActivityAge(Node n)
        {
            float now = Time.realtimeSinceStartup, best = 1e9f;
            AudioTrace.EvStat st;
            if (n.isAudio && n.audio != 0)
            {
                if (!liveSnd) return best;
                if (AudioTrace.evStats.TryGetValue(n.audio, out st)) return now - st.lastT;
                List<uint> evs;
                if (!reachCache.TryGetValue(n.audio, out evs)) { try { evs = WwiseBanks.EventsReaching(n.audio, 8); } catch { evs = new List<uint>(); } reachCache[n.audio] = evs; }
                foreach (var e in evs) if (AudioTrace.evStats.TryGetValue(e, out st)) best = Mathf.Min(best, now - st.lastT);
                return best;
            }
            if (n.id != 0)
            {
                AudioTrace.WObj o;
                if (liveSnd && AudioTrace.objs.TryGetValue((uint)n.id, out o) && o.lastPost >= 0) best = now - o.lastPost;
                float la; if (liveSig && linkAge.TryGetValue(n.id, out la)) best = Mathf.Min(best, la);
                if (liveFsm) best = Mathf.Min(best, EventMonitor.LastAge(n.id, 'F'));
                if (liveAnim) best = Mathf.Min(best, EventMonitor.LastAge(n.id, 'N'));
            }
            return best;
        }

        static void DrawNode(Draw d, Rect area, Node n, bool hover, bool selected)
        {
            var r = NodeRect(area, n);
            if (!r.Overlaps(area)) return;
            float age = ActivityAge(n);
            if (age < 2f)
            {
                float a = 1f - age * 0.5f;
                var g = new Rect(r.x - 3, r.y - 3, r.width + 6, r.height + 6);
                d.Fill(g, new Color(0.3f, 1f, 0.45f, 0.18f * a));
                d.Frame(g, new Color(0.3f, 1f, 0.45f, a));
            }
            d.Fill(r, selected ? new Color(0.16f, 0.3f, 0.5f, 0.98f) : hover ? new Color(0.22f, 0.24f, 0.3f, 0.98f) : new Color(0.12f, 0.13f, 0.16f, 0.96f));
            d.Frame(r, n.color);
            if (zoom >= 0.55f)
            {
                d.PushClip(r);
                d.Text(r.x + 5, r.y + (NodeH * zoom - 13) * 0.5f, n.name, n.color, Mathf.RoundToInt(12 * Mathf.Clamp(zoom, 0.7f, 1.3f)));
                d.PopClip();
            }
        }

        static void DrawEdge(Draw d, Vector2 a, Vector2 b, Edge e, bool emph)
        {
            var c = EdgeColor(e.prov);
            if (emph) c = new Color(Mathf.Min(1, c.r + 0.25f), Mathf.Min(1, c.g + 0.25f), Mathf.Min(1, c.b + 0.25f), 1f);
            bool dashed = e.prov == Provenance.StateMachine || e.prov == Provenance.ReflectionField || e.prov == Provenance.Inferred || e.prov == Provenance.Code;
            bool animated = e.prov == Provenance.Observed;
            if (e.audio) { c = emph ? Color.white : new Color(cAudio.r, cAudio.g, cAudio.b, 0.8f); AudioTrace.EvStat st; if (e.evId != 0 && AudioTrace.evStats.TryGetValue(e.evId, out st) && Time.realtimeSinceStartup - st.lastT < 2.5f) { animated = true; c = cObserved; } }
            else if (liveSig && EventMonitor.FiredAge(e.a.id, e.b.id) < 2.5f) { animated = true; c = cObserved; }
            // gentle curve: 3 segments through horizontal tangents
            var m1 = new Vector2(a.x + (b.x - a.x) * 0.35f, a.y);
            var m2 = new Vector2(a.x + (b.x - a.x) * 0.65f, b.y);
            if (b.x < a.x) { m1 = new Vector2(a.x + 40, a.y); m2 = new Vector2(b.x - 40, b.y); }
            Seg(d, a, m1, c, dashed, animated); Seg(d, m1, m2, c, dashed, animated); Seg(d, m2, b, c, dashed, animated);
            if (e.prov == Provenance.SignalConnection || emph) { Seg(d, a + Vector2.up, m1 + Vector2.up, c, dashed, animated); Seg(d, m1 + Vector2.up, m2 + Vector2.up, c, dashed, animated); Seg(d, m2 + Vector2.up, b + Vector2.up, c, dashed, animated); }
            // arrow head
            var dir = (b - m2).normalized; if (dir == Vector2.zero) dir = Vector2.right;
            var perp = new Vector2(-dir.y, dir.x);
            d.Triangle(b, b - dir * 8 + perp * 4, b - dir * 8 - perp * 4, c);
        }

        // Edge labels sit next to the end of the line where the lines are spread out (the target side, or the source
        // side for links coming into the centre object), on a dark backing, and are nudged apart so none overlap
        // each other or a node box.
        static readonly List<Rect> placed = new List<Rect>();
        static void DrawLabels(Draw d, Rect area, Node hoverNode)
        {
            placed.Clear();
            foreach (var n in nodes.Values) placed.Add(NodeRect(area, n));
            int size = Mathf.RoundToInt(11 * Mathf.Clamp(zoom, 0.8f, 1.2f));
            float lh = size + 4;
            foreach (var e in edgesList)
            {
                if (e.label == null) continue;
                bool emph = hoverNode != null && (e.a == hoverNode || e.b == hoverNode);
                if (hoverNode != null && !emph) continue;          // while hovering a node only its links are labelled
                string txt = e.label + (e.count > 1 ? "  +" + (e.count - 1) : "");
                float w = d.Measure(txt, size) + 8;
                Vector2 a = Anchor(area, e.a, true), b = Anchor(area, e.b, false);
                bool nearSource = e.b.col == 0 && e.a.col != 0;     // many links converge on the centre: label at the far end
                var r = nearSource ? new Rect(a.x + 8, a.y - lh - 2, w, lh) : new Rect(b.x - w - 12, b.y - lh - 2, w, lh);
                // nudge: up first, then down, in line-height steps
                for (int tries = 0; tries < 12 && Collides(r); tries++)
                {
                    int k = tries / 2 + 1;
                    float dy = (tries % 2 == 0 ? -1 : 1) * k * (lh + 1);
                    r.y = (nearSource ? a.y - lh - 2 : b.y - lh - 2) + dy;
                }
                placed.Add(r);
                var c = EdgeColor(e.prov);
                if (liveSig && EventMonitor.FiredAge(e.a.id, e.b.id) < 2.5f) c = cObserved;
                d.Fill(r, new Color(0.04f, 0.05f, 0.07f, 0.82f));
                d.Text(r.x + 4, r.y + 1, txt, emph ? Color.white : new Color(c.r, c.g, c.b, 0.95f), size);
            }
        }

        static bool Collides(Rect r)
        {
            var g = new Rect(r.x - 2, r.y - 1, r.width + 4, r.height + 2);
            foreach (var p in placed) if (p.Overlaps(g)) return true;
            return false;
        }

        static void Seg(Draw d, Vector2 a, Vector2 b, Color c, bool dashed, bool animated)
        {
            if (!dashed && !animated) { d.Line(a, b, c); return; }
            float len = (b - a).magnitude; if (len < 0.5f) return;
            var dir = (b - a) / len;
            float dash = 7f, gap = 5f, off = animated ? (Time.realtimeSinceStartup * 30f) % (dash + gap) : 0f;
            for (float t = -off; t < len; t += dash + gap)
            {
                float s = Mathf.Max(0, t), e = Mathf.Min(len, t + dash);
                if (e > s) d.Line(a + dir * s, a + dir * e, c);
            }
        }

        static void Tooltip(Draw d, Rect area, Vector2 m, List<string> lines)
        {
            float w = 0; foreach (var l in lines) w = Mathf.Max(w, d.Measure(l, 12));
            float h = lines.Count * 15 + 8;
            var r = new Rect(Mathf.Min(m.x + 14, area.xMax - w - 14), Mathf.Min(m.y + 14, area.yMax - h - 4), w + 12, h);
            d.Fill(r, new Color(0.05f, 0.06f, 0.08f, 0.96f));
            d.Frame(r, UI.Border);
            for (int i = 0; i < lines.Count; i++) d.Text(r.x + 6, r.y + 4 + i * 15, lines[i], i == 0 ? UI.Txt : UI.Dim, 12);
        }

        static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a; float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-4f, ab.sqrMagnitude));
            return (a + ab * t - p).magnitude;
        }
    }
}
