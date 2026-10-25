using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Level atlas: what every level file holds, including levels the game has not streamed in yet. Built offline from
    // the level files (tools/atlas) and read from _mod\atlas\*.tsv. Rows use the LevelCompare.Map key (level file +
    // path with " #n" for same-named siblings), so a live object finds its row and a row finds its live object.
    // The atlas records the size of every level file and switches itself off when the running build differs.
    public static class Atlas
    {
        public sealed class Hidden { public string file, path, status, reason, from, scripts; public int objects; }
        public sealed class Wire { public string file, connector, outName, inName, sender, receiver, status, flags; }
        public sealed class FsmIssue { public string file, path, fsm; public string[] unreachable, unsent; }
        public sealed class Area { public int index, savepoints; public string name, file; }
        public sealed class Spawn { public string subscene, file, path, info; public int i; public Vector3 pos; }

        public static readonly List<Hidden> hidden = new List<Hidden>();
        public static readonly List<Wire> wires = new List<Wire>();
        public static readonly List<FsmIssue> fsms = new List<FsmIssue>();
        public static readonly List<Area> areas = new List<Area>();
        static readonly Dictionary<string, Hidden> hiddenByKey = new Dictionary<string, Hidden>();
        static readonly Dictionary<string, List<Wire>> wiresByKey = new Dictionary<string, List<Wire>>();
        static readonly Dictionary<string, List<FsmIssue>> fsmByKey = new Dictionary<string, List<FsmIssue>>();
        static readonly Dictionary<string, Area> areaByName = new Dictionary<string, Area>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, List<Spawn>> spawnsByArea = new Dictionary<string, List<Spawn>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> sceneOfFile = new Dictionary<string, string>();
        static readonly List<KeyValuePair<string, string>> masterKeys = new List<KeyValuePair<string, string>>();
        static readonly Dictionary<string, Transform> liveRoots = new Dictionary<string, Transform>();
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static bool Present, Ready;
        public static string status = "not loaded";
        public static int levelsChecked, levelsDiffer;
        static bool loaded;

        public static string Dir { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "atlas"); } }

        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(Path.Combine(Dir, "levels.tsv"))) { status = "not installed (no _mod\\atlas\\levels.tsv)"; return; }
                Present = true;
                if (!CheckBuild()) return;
                foreach (var f in Rows("hidden.tsv"))
                {
                    var h = new Hidden { file = f[0], path = f[1], status = f[2], objects = Int(f[3]), reason = f[4], from = f[5], scripts = f[6] };
                    hidden.Add(h);
                    hiddenByKey[h.file + "|" + h.path] = h;
                }
                foreach (var f in Rows("wires.tsv"))
                {
                    var w = new Wire { file = f[0], connector = f[1], outName = f[2], inName = f[3], sender = f[4], receiver = f[5], status = f[6], flags = f[7] };
                    wires.Add(w);
                    AddTo(wiresByKey, w.file + "|" + w.connector, w);
                    if (w.sender != w.connector) AddTo(wiresByKey, w.file + "|" + w.sender, w);
                    if (w.receiver != w.connector && w.receiver != w.sender) AddTo(wiresByKey, w.file + "|" + w.receiver, w);
                }
                foreach (var f in Rows("fsm.tsv"))
                {
                    var x = new FsmIssue { file = f[0], path = f[1], fsm = f[2], unreachable = List(f[3]), unsent = List(f[4]) };
                    fsms.Add(x);
                    AddTo(fsmByKey, x.file + "|" + x.path, x);
                }
                foreach (var f in Rows("subscenes.tsv"))
                {
                    var a = new Area { index = Int(f[0]), name = f[1], file = f[2], savepoints = Int(f[3]) };
                    areas.Add(a);
                    areaByName[a.name] = a;
                }
                foreach (var f in Rows("savepoints.tsv"))
                {
                    var s = new Spawn { subscene = f[0], i = Int(f[1]), file = f[2], path = f[3], pos = new Vector3(Float(f[4]), Float(f[5]), Float(f[6])), info = f[7] };
                    AddTo(spawnsByArea, s.subscene, s);
                }
                foreach (var k in AllKeys())
                {
                    int bar = k.IndexOf('|');
                    string file = k.Substring(0, bar), path = k.Substring(bar + 1);
                    if (file != "mainData" && path.Length > 0 && !InContentRoot(path)) masterKeys.Add(new KeyValuePair<string, string>(file, path));
                }
                Ready = true;
                status = "on: " + Summary();
                DevLog.Write("atlas " + status);
            }
            catch (Exception e) { Ready = false; status = "off: " + e.Message; DevLog.Error("atlas load", e); }
        }

        static bool CheckBuild()
        {
            string first = null;
            foreach (var f in Rows("levels.tsv"))
            {
                sceneOfFile[f[0]] = f[1];
                levelsChecked++;
                var fi = new FileInfo(Path.Combine(Application.dataPath, f[0]));
                if (fi.Exists && fi.Length == long.Parse(f[2], IC)) continue;
                levelsDiffer++;
                if (first == null) first = f[0];
            }
            if (levelsDiffer == 0) return true;
            status = "off: made for another game build (" + levelsDiffer + " of " + levelsChecked + " level files differ, first " + first + ")";
            DevLog.Write("atlas " + status);
            return false;
        }

        static IEnumerable<string[]> Rows(string name)
        {
            var lines = File.ReadAllLines(Path.Combine(Dir, name), Encoding.UTF8);
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                var f = lines[i].Split('\t');
                if (f.Length < 3) continue;
                yield return f;
            }
        }

        static int Int(string s) { int v; return int.TryParse(s, NumberStyles.Integer, IC, out v) ? v : 0; }
        static float Float(string s) { float v; return float.TryParse(s, NumberStyles.Float, IC, out v) ? v : 0f; }
        static string[] List(string s) { return string.IsNullOrEmpty(s) ? new string[0] : s.Split(';'); }
        static void AddTo<T>(Dictionary<string, List<T>> d, string k, T v) { List<T> l; if (!d.TryGetValue(k, out l)) d[k] = l = new List<T>(); l.Add(v); }

        static bool InContentRoot(string path)
        {
            int a = path.IndexOf('/');
            if (a < 0) return false;
            int b = path.IndexOf('/', a + 1);
            return (b < 0 ? path.Substring(a + 1) : path.Substring(a + 1, b - a - 1)).EndsWith("_ContentRoot");
        }

        // every object path the atlas names ("file|path"): rows, switches, both ends of wires, FSM owners, savepoints
        static HashSet<string> AllKeys()
        {
            var keys = new HashSet<string>();
            foreach (var h in hidden) { keys.Add(h.file + "|" + h.path); if (h.from.Length > 0) keys.Add(h.file + "|" + h.from); }
            foreach (var w in wires) { keys.Add(w.file + "|" + w.connector); keys.Add(w.file + "|" + w.sender); keys.Add(w.file + "|" + w.receiver); }
            foreach (var x in fsms) keys.Add(x.file + "|" + x.path);
            foreach (var l in spawnsByArea.Values) foreach (var s in l) keys.Add(s.file + "|" + s.path);
            return keys;
        }

        public static string Summary()
        {
            int sw = 0, dm = 0, un = 0;
            foreach (var h in hidden) { if (h.status == "switchable") sw++; else if (h.status == "dormant") dm++; else un++; }
            int sp = 0; foreach (var l in spawnsByArea.Values) sp += l.Count;
            return hidden.Count + " hidden (" + dm + " dormant, " + sw + " switchable, " + un + " unclear), " + wires.Count + " dead wires, " +
                   fsms.Count + " FSMs with dead parts, " + areas.Count + " areas, " + sp + " savepoints; " + levelsChecked + " level files match this build";
        }

        // ---------------------------------------------------------------- live object <-> atlas row
        public static bool Key(GameObject go, out string file, out string path)
        {
            file = path = null;
            if (go == null) return false;
            string why;
            if (LevelCompare.Map(go, out file, out path, out why)) return true;
            file = path = null;
            // a level's own root: the game renames it in play, its content root child still names the scene
            var tr = go.transform;
            for (int i = 0; i < tr.childCount; i++)
            {
                string cn = tr.GetChild(i).name;
                if (!cn.EndsWith("_ContentRoot")) continue;
                var sc = Levels.ByName(cn.Substring(0, cn.Length - 12));
                if (sc != null) { file = sc.File; path = sc.name; return true; }
            }
            var parts = new List<string>();
            for (var t = go.transform; t != null; t = t.parent) parts.Add(t.name + LevelCompare.Dup(t));
            parts.Reverse();
            string live = string.Join("/", parts.ToArray()), tail = "/" + live;
            // master scene: the game moves some objects up at startup (_Globals/_Globals/X is _Globals/X in play),
            // so a file path matches when it ends with the live path
            foreach (var kv in masterKeys)
            {
                if (kv.Value == live) { file = kv.Key; path = kv.Value; return true; }
                if (path == null && kv.Value.EndsWith(tail)) { file = kv.Key; path = kv.Value; }
            }
            return path != null;
        }

        public static string SceneOf(string file) { string s; return sceneOfFile.TryGetValue(file, out s) ? s : file; }

        // streaming state of a row's level: active / loaded / unloaded (the master scene INSIDE is always there)
        public static string LevelState(string file)
        {
            if (file == "level251") return "active";
            string st;
            return Levels.state.TryGetValue(SceneOf(file), out st) ? st : "unloaded";
        }

        public static Hidden HiddenAt(string file, string path) { Hidden h; return hiddenByKey.TryGetValue(file + "|" + path, out h) ? h : null; }

        // the switched-off object itself, or the nearest switched-off parent it sits in
        public static Hidden HiddenAbove(string file, string path)
        {
            for (string p = path; p != null; )
            {
                var h = HiddenAt(file, p);
                if (h != null) return h;
                int s = p.LastIndexOf('/');
                p = s > 0 ? p.Substring(0, s) : null;
            }
            return null;
        }

        static readonly List<Wire> noWires = new List<Wire>();
        public static List<Wire> WiresAt(string file, string path) { List<Wire> l; return wiresByKey.TryGetValue(file + "|" + path, out l) ? l : noWires; }

        public static FsmIssue FsmAt(string file, string path, string fsmName)
        {
            List<FsmIssue> l;
            if (!fsmByKey.TryGetValue(file + "|" + path, out l)) return null;
            foreach (var x in l) if (x.fsm == fsmName) return x;
            return null;
        }

        public static FsmIssue FsmOf(PlayMakerFSM f)
        {
            EnsureLoaded();
            string file, path;
            if (!Ready || f == null || !Key(f.gameObject, out file, out path)) return null;
            return FsmAt(file, path, f.FsmName);
        }

        public static Area AreaNamed(string name) { Area a; return name != null && areaByName.TryGetValue(name, out a) ? a : null; }
        public static List<Spawn> SpawnsOf(string area) { List<Spawn> l; return area != null && spawnsByArea.TryGetValue(area, out l) ? l : null; }

        // the live object for a row, or null when its level is not loaded
        public static GameObject Find(string file, string path)
        {
            var seg = path.Split('/');
            if (seg.Length > 1 && seg[1].EndsWith("_ContentRoot"))
            {
                var t = LiveRoot(seg[1]);
                for (int i = 2; t != null && i < seg.Length; i++) t = Child(t, seg[i]);
                return t != null ? t.gameObject : null;
            }
            if (file == "mainData") return null;
            if (seg.Length == 1)
            {
                var cr = LiveRoot(seg[0] + "_ContentRoot");
                if (cr != null && cr.parent != null) return cr.parent.gameObject;
            }
            ObjectDatabase.EnsureFresh(5f);
            var roots = new List<Transform>();
            foreach (var r in ObjectDatabase.all) if (r.t != null && r.t.parent == null) roots.Add(r.t);
            for (int k = 0; k < seg.Length; k++)
                foreach (var root in roots)
                {
                    if (root.name != seg[k]) continue;
                    var t = root;
                    for (int i = k + 1; t != null && i < seg.Length; i++) t = Child(t, seg[i]);
                    if (t != null) return t.gameObject;
                }
            return null;
        }

        static Transform LiveRoot(string name)
        {
            Transform t;
            if (liveRoots.TryGetValue(name, out t) && t != null && t.name == name) return t;
            liveRoots.Remove(name);
            ObjectDatabase.EnsureFresh(5f);
            foreach (var r in ObjectDatabase.all) if (r.t != null && r.name == name) { liveRoots[name] = r.t; return r.t; }
            return null;
        }

        static Transform Child(Transform p, string key)
        {
            var seen = new Dictionary<string, int>();
            for (int i = 0; i < p.childCount; i++)
            {
                var c = p.GetChild(i);
                int n; seen.TryGetValue(c.name, out n); seen[c.name] = ++n;
                if ((n > 1 ? c.name + " #" + n : c.name) == key) return c;
            }
            return null;
        }

        public static string Leaf(string path) { return path.Substring(path.LastIndexOf('/') + 1); }

        public static string Tail(string path, int parts = 2)
        {
            int p = path.Length;
            for (int k = 0; k < parts && p > 0; k++) p = path.LastIndexOf('/', p - 1);
            return p > 0 ? "…" + path.Substring(p) : path;
        }

        public static string WireMeaning(Wire w)
        {
            switch (w.status)
            {
                case "NO_SINK": return "the receiver has no input called " + w.inName;
                case "NO_SOURCE": return "the sender never creates the output " + w.outName;
                case "NO_SOURCE+NO_SINK": return "neither " + w.outName + " nor " + w.inName + " exists";
                case "SOURCE_NEVER_FIRES": return w.outName + " exists but nothing ever fires it";
            }
            return w.status;
        }

        // every row of the loaded levels: found live, and the live object maps back to the same row
        static string Check()
        {
            int rows = 0, found = 0, back = 0;
            var perLevel = new SortedDictionary<string, int[]>();
            var sb = new StringBuilder();
            foreach (var k in AllKeys())
            {
                int bar = k.IndexOf('|');
                string file = k.Substring(0, bar), path = k.Substring(bar + 1);
                if (path.Length == 0 || LevelState(file) == "unloaded") continue;
                int[] c; if (!perLevel.TryGetValue(file, out c)) perLevel[file] = c = new int[3];
                rows++; c[0]++;
                var g = Find(file, path);
                if (g == null) { if (rows - found <= 10) sb.Append("\n  not found: ").Append(file).Append("  ").Append(path); continue; }
                found++; c[1]++;
                string f2, p2;
                if (Key(g, out f2, out p2) && f2 == file && p2 == path) { back++; c[2]++; }
                else if (found - back <= 10) sb.Append("\n  maps back to another key: ").Append(file).Append("  ").Append(path).Append("  ->  ").Append(f2).Append("  ").Append(p2);
            }
            var head = new StringBuilder("atlas check: " + perLevel.Count + " loaded level(s), " + rows + " object paths, " + found + " found live, " + back + " map back to the same row");
            foreach (var kv in perLevel) head.Append("\n  ").Append(kv.Key).Append(' ').Append(SceneOf(kv.Key)).Append(": ").Append(kv.Value[1]).Append('/').Append(kv.Value[0]).Append(" found, ").Append(kv.Value[2]).Append(" map back");
            return head.ToString() + sb;
        }

        // ---------------------------------------------------------------- console: atlas | atlas find <text> | atlas level <n|name> | atlas check
        public static string Command(string[] a)
        {
            EnsureLoaded();
            if (a.Length < 2) return "atlas " + status + "   (" + Dir + ")";
            if (!Ready) return "atlas " + status;
            string sub = a[1].ToLowerInvariant();
            string q = a.Length > 2 ? string.Join(" ", a, 2, a.Length - 2) : "";
            var sb = new StringBuilder();
            if (sub == "find" && q.Length > 0)
            {
                string f = q.ToLowerInvariant();
                int n = 0;
                foreach (var h in hidden)
                    if ((h.path + " " + h.reason + " " + h.scripts).ToLowerInvariant().Contains(f) && n++ < 40)
                        sb.Append(h.file).Append("  [").Append(h.status).Append(", ").Append(h.objects).Append(" obj]  ").Append(h.path).Append("   ").Append(h.reason).Append('\n');
                foreach (var w in wires)
                    if ((w.connector + " " + w.sender + " " + w.receiver + " " + w.outName + " " + w.inName).ToLowerInvariant().Contains(f) && n++ < 40)
                        sb.Append(w.file).Append("  [wire ").Append(w.status).Append("]  ").Append(Leaf(w.sender)).Append('.').Append(w.outName).Append(" -> ").Append(Leaf(w.receiver)).Append('.').Append(w.inName).Append('\n');
                foreach (var x in fsms)
                    if ((x.path + " " + x.fsm).ToLowerInvariant().Contains(f) && n++ < 40)
                        sb.Append(x.file).Append("  [fsm ").Append(x.fsm).Append("]  ").Append(x.path).Append("   unreachable: ").Append(string.Join(", ", x.unreachable)).Append('\n');
                sb.Append(n).Append(" match(es)").Append(n > 40 ? ", first 40 shown" : "");
                return sb.ToString();
            }
            if (sub == "level" && q.Length > 0)
            {
                string file = null;
                int idx;
                if (int.TryParse(q, NumberStyles.Integer, IC, out idx)) file = "level" + idx;
                else foreach (var kv in sceneOfFile) if (kv.Key.Equals(q, StringComparison.OrdinalIgnoreCase) || kv.Value.TrimStart('#').Equals(q.TrimStart('#'), StringComparison.OrdinalIgnoreCase)) { file = kv.Key; break; }
                if (file == null || !sceneOfFile.ContainsKey(file)) return "no level " + q;
                var st = new Dictionary<string, int>();
                int objs = 0, wn = 0, fn = 0;
                foreach (var h in hidden) if (h.file == file) { int c; st.TryGetValue(h.status, out c); st[h.status] = c + 1; objs += h.objects; }
                foreach (var w in wires) if (w.file == file) wn++;
                foreach (var x in fsms) if (x.file == file) fn++;
                sb.Append(file).Append("  ").Append(SceneOf(file)).Append(":  hidden");
                foreach (var kv in st) sb.Append(' ').Append(kv.Key).Append(' ').Append(kv.Value);
                sb.Append(" (").Append(objs).Append(" objects), ").Append(wn).Append(" dead wires, ").Append(fn).Append(" FSMs with dead parts");
                var area = AreaNamed(SceneOf(file));
                if (area != null) sb.Append(", area ").Append(area.index).Append(" with ").Append(area.savepoints).Append(" savepoint(s)");
                int shown = 0;
                foreach (var h in hidden) if (h.file == file && shown++ < 40) sb.Append("\n  [").Append(h.status).Append("] ").Append(h.path).Append("   ").Append(h.status == "switchable" ? "by " + h.reason : h.reason);
                return sb.ToString();
            }
            if (sub == "check") return Check();
            return "usage: atlas | atlas find <text> | atlas level <n|levelN|scene> | atlas check";
        }
    }
}
