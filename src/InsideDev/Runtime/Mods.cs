using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    // Phase 11: mod format + ModRuntime.
    //
    // A mod is _mod\mods\<name>.json: a list of operations against selectors (hierarchy path + approximate position),
    // never instance ids and never copied game assets. Disabling a mod restores the values it overwrote on objects
    // still loaded.
    //
    //   set     target + property (active | enabled | position | localPosition | localEuler | localScale | field)
    //   action  target + signal / fsmEvent / fsmState / fsmWire ("state|event|toState") / sigWire ("+|>in|out|sender") / fsmAction ("state|index|field|value"); replayed on load only when "replay": "onLoad"
    //
    // Every operation belongs to a level: the scene whose content root its target path starts under ("level" in the
    // file, derived from the path when missing), or to no level ("global": master scene, DontDestroyOnLoad objects,
    // audio rules). The runtime keeps one list of operations per level and works a list only while that level is
    // loaded (Levels.state, the core's own once-a-second culler poll):
    //   - a level comes up (streaming quiet for mods.settle seconds): its pending operations are resolved once, below
    //     that level's content root only, and applied;
    //   - what is still missing is tried again after 0.5, 1, 2, 4 and 8 s (late-awake content), then reported as
    //     "not found in <level>" and tried again every 5, 10, 20, then 30 s for as long as the level stays loaded
    //     (one path walk below the level root per operation);
    //   - a level goes away (and stays away for mods.settle seconds): its operations go back to pending; an object
    //     that is still alive when the level comes back keeps what was applied to it (nothing fires twice);
    //   - global operations follow the same rule on every level change.
    // Operations of levels that are not loaded cost nothing: no path walk, no scene search, no text, and no branch per
    // operation; four times a second each level list costs one dictionary lookup.
    public static class Mods
    {
        public const string Format = "insidedev-mod";
        public const int FormatVersion = 1;
        public const string SettleKey = "mods.settle";
        public const float SettleDefault = 0.25f;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public sealed class Op
        {
            public int id; public string type = "set";
            public string target, component, property, field, valueType, value, original, note;
            public int componentIndex;
            public string action, fsm, arg, replay = "never";
            public bool enforce;                               // re-apply if the game changes it back
            public string level;                               // file field: the level of the target ("global" = none); null = from the target path

            // runtime state
            public int appliedTo;                              // instance id the op was applied to (0 = not applied)
            public string status = "pending";
            public object restoreValue; public ChangeRecorder.Prop appliedProp;
            public string lastError;
            public string detail;                              // more about the status: the candidates of an ambiguous target

            internal bool derived;                             // lvl / parts / sel are filled
            internal string lvl;                               // effective level (canonical scene name), null = global
            internal string[] parts;                           // path segments of the target
            internal ObjectSelector sel;
            internal GameObject go;                            // the resolved target while it is alive
            internal bool settled;                             // nothing to do until the next load edge
            internal bool watch;                               // found, needs a cheap look four times a second (enforce, waits to become active)
            internal bool needsOther;                          // waits for an object of another level
            internal bool slow;                                // settled after the quick retries: tried again on the slow timer
            internal bool hasDecoded; internal object decoded; // decoded value (not for "ref:" values, which depend on what is loaded)
            internal float logAt = -100f;

            // the level the runtime uses: a scene name, or null for a global operation
            public string Level { get { return lvl; } }
        }

        public sealed class Mod
        {
            public string file, name, title, description, author, created;
            public bool enabled = true;
            public readonly List<Op> ops = new List<Op>();
            public readonly List<string> problems = new List<string>();   // load / validation problems
            public readonly List<string> loadProblems = new List<string>();   // found while reading the file (kept by Validate)
            public readonly List<string> levels = new List<string>();     // file field "levels": the levels this mod belongs to (for targets outside every level)
            public bool loadFailed;                                        // the file could not be read as a mod: never written, never applied
            public double lastResolveMs;                                   // cost of the last resolve round that had work for this mod
            internal long roundTicks; internal bool roundWork;
            public int Applied { get { int n = 0; foreach (var o in ops) if (o.appliedTo != 0) n++; return n; } }
        }

        public static readonly List<Mod> mods = new List<Mod>();
        public static string Dir { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "mods"); } }
        public static bool runtimeEnabled = true;

        // ---------------------------------------------------------------- load / save
        public static void LoadAll()
        {
            RtXData.Init();
            settle = -1f; gates.Clear();   // mods.settle is read again with the next grouping
            var old = new List<Mod>(mods);
            mods.Clear();
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                var files = Directory.GetFiles(Dir, "*.json");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    var m = Load(f);
                    if (m == null) continue;
                    foreach (var o in old) if (string.Equals(o.file, m.file, StringComparison.OrdinalIgnoreCase)) { CarryOver(o, m); break; }
                    mods.Add(m);
                }
                DevLog.Write("[mods] " + mods.Count + " mod(s) in " + Dir + ": " + Summary());
            }
            catch (Exception e) { DevLog.Error("mods load", e); }
            groupsDirty = true;
        }

        // a reload keeps what is applied: an unchanged operation is not fired or captured a second time
        static void CarryOver(Mod was, Mod now)
        {
            if (!was.enabled || !now.enabled) return;
            foreach (var n in now.ops)
                foreach (var o in was.ops)
                {
                    if (o.id != n.id) continue;
                    if (o.type == n.type && o.target == n.target && o.property == n.property && o.field == n.field && o.component == n.component && o.componentIndex == n.componentIndex &&
                        o.value == n.value && o.action == n.action && o.fsm == n.fsm && o.arg == n.arg && o.replay == n.replay)
                    {
                        n.appliedTo = o.appliedTo; n.restoreValue = o.restoreValue; n.appliedProp = o.appliedProp; n.status = o.status; n.go = o.go;
                        n.settled = o.settled; n.watch = o.watch; n.needsOther = o.needsOther; n.slow = o.slow; n.lastError = o.lastError; n.detail = o.detail;
                    }
                    break;
                }
        }

        static Mod Load(string file)
        {
            var m = new Mod { file = file, name = Path.GetFileNameWithoutExtension(file) };
            try
            {
                object parsed; string perr;
                if (!Json.TryParse(File.ReadAllText(file), out parsed, out perr)) { m.loadProblems.Add("parse error: " + perr); m.loadFailed = true; m.enabled = false; m.problems.AddRange(m.loadProblems); return m; }
                var o = parsed as Json.Obj;
                if (o == null) { m.loadProblems.Add("not a JSON object"); m.loadFailed = true; m.enabled = false; m.problems.AddRange(m.loadProblems); return m; }
                string fmt = o.Str("format");
                if (fmt == null) m.loadProblems.Add("no \"format\" field (expected '" + Format + "'): applied anyway");
                else if (fmt != Format) m.loadProblems.Add("format is '" + fmt + "', expected '" + Format + "': not applied");
                if (o.Num("formatVersion") > FormatVersion) m.loadProblems.Add("formatVersion " + o.Num("formatVersion").ToString("0.###", IC) + " is newer than this editor (" + FormatVersion + "): not applied");
                m.name = o.Str("name", m.name); m.title = o.Str("title", m.name); m.description = o.Str("description", "");
                m.author = o.Str("author", ""); m.created = o.Str("created", ""); m.enabled = o.Bool("enabled", true);
                foreach (var x in o.Arr("levels")) { var s = x as string; if (!string.IsNullOrEmpty(s) && !m.levels.Contains(s)) m.levels.Add(s); }
                int n = 0;
                foreach (var x in o.Arr("operations"))
                {
                    n++;
                    var j = x as Json.Obj; if (j == null) { m.loadProblems.Add("operation " + n + " is not an object: skipped"); continue; }
                    m.ops.Add(new Op
                    {
                        id = (int)j.Num("id", m.ops.Count + 1), type = j.Str("type", "set"), target = j.Str("target"), component = j.Str("component"),
                        componentIndex = (int)j.Num("componentIndex"), property = j.Str("property"), field = j.Str("field"), valueType = j.Str("valueType"),
                        value = j.Str("value"), original = j.Str("original"), note = j.Str("note"), action = j.Str("action"), fsm = j.Str("fsm"),
                        arg = j.Str("arg"), replay = j.Str("replay", "never"), enforce = j.Bool("enforce"), level = j.Str("level")
                    });
                }
                Validate(m);
            }
            catch (Exception e) { m.loadProblems.Add("parse error: " + e.Message); m.loadFailed = true; m.enabled = false; m.problems.Clear(); m.problems.AddRange(m.loadProblems); }
            return m;
        }

        // Writes the mod file (temp file + replace, the previous content stays as <file>.bak). A mod whose file could not
        // be read is never written: that would replace the user's file with an empty mod.
        public static string Save(Mod m)
        {
            if (m.loadFailed) throw new InvalidOperationException("mod file " + Path.GetFileName(m.file ?? "?") + " could not be read (" + (m.loadProblems.Count > 0 ? m.loadProblems[0] : "parse error") + "): fix the file, then mod reload");
            var o = new Json.Obj();
            o["format"] = Format; o["formatVersion"] = (double)FormatVersion;
            o["name"] = m.name; o["title"] = m.title ?? m.name; o["description"] = m.description ?? ""; o["author"] = m.author ?? "";
            o["created"] = m.created ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", IC); o["game"] = "INSIDE"; o["editor"] = "InsideDev " + Boot.Version;
            o["enabled"] = m.enabled;
            if (m.levels.Count > 0) o["levels"] = new List<object>(m.levels.ToArray());
            var ops = new List<object>();
            foreach (var op in m.ops)
            {
                Derive(op);
                var j = new Json.Obj();
                j["id"] = (double)op.id; j["type"] = op.type; j["target"] = op.target;
                string lv = op.level ?? op.lvl;
                if (lv != null) j["level"] = lv;
                if (op.component != null) { j["component"] = op.component; j["componentIndex"] = (double)op.componentIndex; }
                if (op.type == "set")
                {
                    j["property"] = op.property;
                    if (op.field != null) j["field"] = op.field;
                    j["valueType"] = op.valueType; j["value"] = op.value;
                    if (op.original != null) j["original"] = op.original;
                    if (op.enforce) j["enforce"] = true;
                }
                else { j["action"] = op.action; if (op.fsm != null) j["fsm"] = op.fsm; j["arg"] = op.arg; j["replay"] = op.replay; }
                if (!string.IsNullOrEmpty(op.note)) j["note"] = op.note;
                ops.Add(j);
            }
            o["operations"] = ops;
            Directory.CreateDirectory(Dir);
            if (m.file == null) m.file = FreeFile(m);
            SafeFile.WriteAllText(m.file, Json.Write(o) + "\n", true);
            groupsDirty = true;
            return m.file;
        }

        // the file of a new mod: <safe name>.json, or <safe name>_2.json ... when that file belongs to another mod
        // (two names can map to one file name) or is a file this session did not write
        static string FreeFile(Mod m)
        {
            string stem = Safe(m.name);
            for (int k = 1; k < 1000; k++)
            {
                string f = Path.Combine(Dir, stem + (k == 1 ? "" : "_" + k.ToString(IC)) + ".json");
                if (!File.Exists(f)) return f;
                foreach (var x in mods) if (x != m && string.Equals(x.file, f, StringComparison.OrdinalIgnoreCase) && string.Equals(x.name, m.name, StringComparison.OrdinalIgnoreCase)) return f;
            }
            return Path.Combine(Dir, stem + "_" + DateTime.UtcNow.Ticks.ToString(IC) + ".json");
        }

        static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "mod";
        }

        public static Mod Find(string name)
        {
            foreach (var m in mods) if (string.Equals(m.name, name, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }

        // ---------------------------------------------------------------- record from history
        // The world position of an object before the recorded moves (its own or of a parent): the selector's position
        // only breaks ties between same-named objects, and on the next load the object is at its original place.
        static Vector3 OriginalPosition(GameObject go, List<ChangeRecorder.Net> nets)
        {
            var t = go.transform;
            Vector3 pos = t.position;
            if (nets == null) return pos;
            List<GameObject> done = null;
            foreach (var n in nets)
            {
                var p = n.prop;
                if (p.kind != ChangeRecorder.PropKind.Position && p.kind != ChangeRecorder.PropKind.LocalPosition) continue;
                if (p.go == null || !(n.original is Vector3)) continue;
                if (p.go != go && !t.IsChildOf(p.go.transform)) continue;
                if (done != null && done.Contains(p.go)) continue;
                (done ?? (done = new List<GameObject>())).Add(p.go);
                var mt = p.go.transform;
                Vector3 was = (Vector3)n.original;
                if (p.kind == ChangeRecorder.PropKind.LocalPosition && mt.parent != null) was = mt.parent.TransformPoint(was);
                pos += was - mt.position;
            }
            return pos;
        }

        static string RecordSelector(GameObject go, List<ChangeRecorder.Net> nets)
        {
            var sel = ObjectSelector.From(go);
            sel.hasPos = true; sel.pos = OriginalPosition(go, nets);
            return sel.ToString();
        }

        // netChanges: state changes to include; actions: history entries whose action should be included
        public static Mod Record(string name, string description, List<ChangeRecorder.Net> netChanges, List<ChangeRecorder.Entry> actions, out List<string> skipped)
        {
            skipped = new List<string>();
            var m = new Mod { name = name, title = name, description = description, created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", IC), enabled = true };
            int id = 1;
            foreach (var n in netChanges)
            {
                var p = n.prop;
                if (!p.Alive) { skipped.Add(p.Label + ": object is gone"); continue; }
                if (p.kind == ChangeRecorder.PropKind.Custom) { skipped.Add(p.Label + ": cannot be stored in a mod"); continue; }
                if (p.kind == ChangeRecorder.PropKind.Audio && AudioCapture.OwnsRule(p.field)) { skipped.Add(p.Label + ": held by the running audio capture (acap)"); continue; }
                string val, orig, vt;
                string err = Codec.Encode(n.lastAfter, p.ValueType, out val, out vt);
                if (err != null) { skipped.Add(p.Label + ": " + err); continue; }
                string vt2; Codec.Encode(n.original, p.ValueType, out orig, out vt2);
                bool audio = p.kind == ChangeRecorder.PropKind.Audio;
                var op = new Op
                {
                    id = id++, type = "set", target = audio ? AudioTarget : RecordSelector(p.go, netChanges), property = ChangeRecorder.PropName(p.kind),
                    field = p.kind == ChangeRecorder.PropKind.Field || audio ? p.field : null, valueType = vt, value = val, original = orig
                };
                if (!audio) op.level = LevelGate.SceneOf(p.go.transform);
                if (p.comp != null) { op.component = p.compType; op.componentIndex = p.compIndex; }
                m.ops.Add(op);
            }
            if (actions != null)
                foreach (var e in actions)
                {
                    var a = e.action;
                    if (a == null || a.go == null) { skipped.Add(e.label + ": object is gone"); continue; }
                    string tgt = (a.action == "reparent" ? Reparent.OriginalSelector(a.go) : null) ?? RecordSelector(a.go, netChanges);
                    var op = new Op { id = id++, type = "action", target = tgt, action = a.action, fsm = a.fsm, arg = a.arg, replay = "onLoad", note = e.label };
                    // a moved object is found at its original place on the next load: its level is the one of that path
                    if (a.action != "reparent") op.level = LevelGate.SceneOf(a.go.transform);
                    m.ops.Add(op);
                }
            Validate(m);
            return m;
        }

        // ---------------------------------------------------------------- validation (static)
        public static void Validate(Mod m)
        {
            m.problems.Clear();
            m.problems.AddRange(m.loadProblems);
            if (m.loadFailed) return;
            var ids = new HashSet<int>();
            foreach (var op in m.ops)
            {
                string p = "op " + op.id + ": ";
                if (!ids.Add(op.id)) m.problems.Add(p + "duplicate id");
                if (string.IsNullOrEmpty(op.target)) { m.problems.Add(p + "no target"); continue; }
                if (op.type == "set")
                {
                    ChangeRecorder.PropKind k;
                    if (op.property == "audio")
                    {
                        if (op.target != AudioTarget) m.problems.Add(p + "audio ops use target " + AudioTarget);
                        string e = op.field == null ? "audio op needs field (rule key)" : AudioRules.Check(op.field, op.value == "null" ? null : op.value);
                        if (e != null) m.problems.Add(p + e);
                    }
                    else if (op.property == "field")
                    {
                        if (op.component == null || op.field == null) { m.problems.Add(p + "field op needs component and field"); continue; }
                        var t = FindType(op.component);
                        if (t == null) { m.problems.Add(p + "unknown component type " + op.component); continue; }
                        var ft = FieldTypeOf(t, op.field);
                        if (ft == null) { m.problems.Add(p + op.component + " has no field '" + op.field + "'"); continue; }
                        if (!Codec.CanDecode(op.value, ft)) m.problems.Add(p + "value '" + op.value + "' is not a valid " + ft.Name);
                    }
                    else if (!ChangeRecorder.TryPropKind(op.property ?? "", out k)) m.problems.Add(p + "unknown property '" + op.property + "'");
                    else if (k == ChangeRecorder.PropKind.Enabled && op.component == null) m.problems.Add(p + "enabled needs a component");
                    else if (!Codec.CanDecode(op.value, k == ChangeRecorder.PropKind.Active || k == ChangeRecorder.PropKind.Enabled ? typeof(bool) : typeof(Vector3))) m.problems.Add(p + "bad value '" + op.value + "'");
                }
                else if (op.type == "action")
                {
                    if (op.action != "signal" && op.action != "fsmEvent" && op.action != "fsmState" && op.action != "fsmWire" && op.action != "sigWire" && op.action != "fsmAction" && op.action != "reparent" && op.action != "anim") m.problems.Add(p + "unknown action '" + op.action + "'");
                    if (op.replay != "never" && op.replay != "onLoad") m.problems.Add(p + "replay must be never or onLoad");
                }
                else m.problems.Add(p + "unknown type '" + op.type + "'");
                if (op.level != null && op.level != GlobalLevel && Levels.ByName(op.level) == null) m.problems.Add(p + "level '" + op.level + "' is not a scene of the game (the op is never applied)");
            }
            foreach (var lv in m.levels) if (Levels.ByName(lv) == null) m.problems.Add("levels: '" + lv + "' is not a scene of the game");
        }

        static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();
        public static Type FindType(string name)
        {
            Type t;
            if (typeCache.TryGetValue(name, out t)) return t;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var x in a.GetTypes()) if ((x.Name == name || x.FullName == name) && typeof(Component).IsAssignableFrom(x)) { t = x; break; }
                }
                catch { }
                if (t != null) break;
            }
            typeCache[name] = t;
            return t;
        }

        static Type FieldTypeOf(Type t, string path)
        {
            foreach (var part in path.Split('.'))
            {
                var f = ChangeRecorder.FindField(t, part);
                if (f == null) return null;
                t = f.FieldType;
            }
            return t;
        }

        // ---------------------------------------------------------------- resolution
        public const string GlobalLevel = "global";
        static readonly string[] noParts = new string[0];

        // fills the level and the parsed target of an operation (once)
        static void Derive(Op op)
        {
            if (op.derived) return;
            op.derived = true;
            op.parts = noParts; op.lvl = null;
            if (op.type == "set" && op.property == "audio") return;
            if (!string.IsNullOrEmpty(op.target))
            {
                try { op.sel = ObjectSelector.Parse(op.target); op.parts = op.sel.path.Split('/'); } catch { op.sel = null; }
            }
            if (op.level == GlobalLevel) return;
            if (op.level != null) { var sc = Levels.ByName(op.level); op.lvl = sc != null ? sc.name : op.level; return; }
            int idx;
            op.lvl = LevelGate.SceneOfPath(op.parts, out idx);
        }

        static readonly List<Transform> foundT = new List<Transform>();

        // every object at this path below a level's content root; the path may start above the root, at it or below it
        static void FindInLevel(Transform root, string[] parts, List<Transform> into)
        {
            into.Clear();
            if (root == null) return;
            string rn = root.name;
            int from = 0;
            for (int i = 0; i < parts.Length; i++) if (parts[i] == rn) { from = i + 1; break; }
            LevelGate.FindAll(root, parts, from, into);
        }

        // Objects that match a selector. A target below a level's content root is looked for under that root only, and
        // not at all while that level is not loaded; anything else is found from its root object's name. Duplicate
        // names are all followed, ties broken by position.
        public static List<GameObject> ResolveAll(string selector)
        {
            var res = new List<GameObject>();
            if (string.IsNullOrEmpty(selector)) return res;
            var sel = ObjectSelector.Parse(selector);
            var parts = sel.path.Split('/');
            int idx;
            string lv = LevelGate.SceneOfPath(parts, out idx);
            Resolve(lv, parts, sel, res);
            return res;
        }

        static void Resolve(string lv, string[] parts, ObjectSelector sel, List<GameObject> res)
        {
            res.Clear();
            if (parts == null || parts.Length == 0) return;
            bool walked = false;
            if (lv != null)
            {
                var root = LevelGate.RootOf(lv);
                if (root != null)
                {
                    FindInLevel(root, parts, foundT);
                    for (int i = 0; i < foundT.Count; i++) res.Add(foundT[i].gameObject);
                    foundT.Clear();
                    // a root under another name than the path's (or the path does not run through it): the old walk
                    walked = res.Count > 0 || Array.IndexOf(parts, root.name) >= 0;
                }
                else if (noRootLogged.Add(lv)) DevLog.Write("[mods] the content root of " + lv + " was not found (level state " + LevelGate.StateOf(lv) + "): searching from the scene roots by name instead");
            }
            if (!walked) WalkFromRoots(parts, res);
            if (res.Count > 1 && sel != null && sel.hasPos)
            {
                GameObject near = null; int n = 0;
                for (int i = 0; i < res.Count; i++) if ((res[i].transform.position - sel.pos).sqrMagnitude < 0.25f) { near = res[i]; n++; }
                if (n == 1) { res.Clear(); res.Add(near); }
            }
        }

        static readonly HashSet<string> noRootLogged = new HashSet<string>();

        // 0.10.0: the root object by name (GameObject.Find, and the object database for inactive roots), then children by name
        static void WalkFromRoots(string[] parts, List<GameObject> res)
        {
            var cur = new List<Transform>();
            foreach (var r in Roots(parts[0])) cur.Add(r);
            for (int i = 1; i < parts.Length && cur.Count > 0; i++)
            {
                var next = new List<Transform>();
                foreach (var t in cur)
                    for (int c = 0; c < t.childCount; c++) { var ch = t.GetChild(c); if (ch.name == parts[i]) next.Add(ch); }
                cur = next;
            }
            foreach (var t in cur) res.Add(t.gameObject);
        }

        static readonly Dictionary<string, List<Transform>> rootCache = new Dictionary<string, List<Transform>>();
        static int rootCacheFrame = -1;
        static List<Transform> Roots(string name)
        {
            if (rootCacheFrame != Time.frameCount) { rootCache.Clear(); rootCacheFrame = Time.frameCount; }
            List<Transform> l;
            if (rootCache.TryGetValue(name, out l)) return l;
            l = new List<Transform>();
            var g = GameObject.Find("/" + name);
            if (g != null) l.Add(g.transform);
            // inactive roots are invisible to GameObject.Find; the object database knows them if it has scanned
            foreach (var r in ObjectDatabase.ByPath(name)) if (r.go != null && r.go.transform.parent == null && !l.Contains(r.go.transform)) l.Add(r.go.transform);
            rootCache[name] = l;
            return l;
        }

        static Component CompOf(GameObject go, Op op)
        {
            if (op.component == null) return null;
            int n = 0;
            foreach (var c in go.GetComponents<Component>())
                if (c != null && c.GetType().Name == op.component) { if (n == op.componentIndex) return c; n++; }
            return null;
        }

        static ChangeRecorder.Prop PropOf(GameObject go, Op op, out string err)
        {
            err = null;
            ChangeRecorder.PropKind k;
            if (op.property == "field") k = ChangeRecorder.PropKind.Field;
            else if (!ChangeRecorder.TryPropKind(op.property ?? "", out k)) { err = "unknown property"; return null; }
            Component c = null;
            if (op.component != null)
            {
                c = CompOf(go, op);
                if (c == null) { err = "no " + op.component + (op.componentIndex > 0 ? "#" + op.componentIndex : "") + " on " + go.name; return null; }
            }
            return ChangeRecorder.Prop.Make(go, c, k, k == ChangeRecorder.PropKind.Field ? op.field : null);
        }

        // ---------------------------------------------------------------- runtime: one list of operations per level
        sealed class Group
        {
            public string label;                       // level name, "global", or the levels of a mod's "levels" list
            public LevelGate[] gates = noGates;        // none = global
            public int[] seen = noInts;
            public readonly List<Mod> mods = new List<Mod>();   // parallel to ops
            public readonly List<Op> ops = new List<Op>();
            public bool up;
            public int streamSeen = -1, otherSeen = -1;
            public bool working; public int round; public float nextRound;
            public int watching; public bool retryOnChange;
            public int slowOps; public float nextSlow, slowPause;      // slow retries of what the quick ones did not find
            public float downSince = -1f;                               // its levels went away at (unload after mods.settle)
            public double lastMs; public int rounds, edges;
        }
        static readonly LevelGate[] noGates = new LevelGate[0];
        static readonly int[] noInts = new int[0];
        static readonly float[] RetryPauses = { 0.5f, 1f, 2f, 4f, 8f };
        const float SlowFirst = 5f, SlowMax = 30f;

        static readonly List<Group> groups = new List<Group>();
        static readonly Dictionary<string, LevelGate> gates = new Dictionary<string, LevelGate>(StringComparer.OrdinalIgnoreCase);
        static readonly List<Mod> grouped = new List<Mod>();   // the mod list the groups were built from
        static bool groupsDirty = true;
        static float settle = -1f;
        static readonly double tickMs = 1000.0 / Stopwatch.Frequency;

        static float nextCheck;
        public static int applyCount;
        // totals for "mod list", the doctor check and scripts
        public static int resolveRounds; public static double lastRoundMs;

        public static void Tick()
        {
            // animations that were switched off when something below them was moved (by a mod or by hand)
            if (Reparent.PendingRebinds > 0)
            {
                float t = Time.realtimeSinceStartup;
                if (t >= nextRebind) { nextRebind = t + 0.25f; Reparent.RebindPending(); }
            }
            if (!runtimeEnabled) return;
            if (mods.Count == 0) { if (groups.Count > 0) { groups.Clear(); grouped.Clear(); } return; }
            float now = Time.realtimeSinceStartup;
            if (now < nextCheck) return;
            nextCheck = now + 0.25f;
            if (groupsDirty || ListChanged()) Regroup();
            for (int i = 0; i < groups.Count; i++) TickGroup(groups[i], now);
        }
        static float nextRebind;

        // other code adds to and removes from the public list directly
        static bool ListChanged()
        {
            if (grouped.Count != mods.Count) return true;
            for (int i = 0; i < grouped.Count; i++) if (!ReferenceEquals(grouped[i], mods[i])) return true;
            return false;
        }

        static bool HasBlocking(Mod m)
        {
            if (m.loadFailed) return true;
            foreach (var p in m.problems) if (p.StartsWith("parse", StringComparison.Ordinal) || p.StartsWith("format", StringComparison.Ordinal) || p.StartsWith("not a", StringComparison.Ordinal)) return true;
            return false;
        }

        static LevelGate GateFor(string level)
        {
            LevelGate g;
            if (!gates.TryGetValue(level, out g))
            {
                // mods are applied to a level as soon as it is loaded (also while the game still has it switched off) and
                // also while a capture runs: a take that walks into the level needs the mod there
                g = new LevelGate(level, false, settle) { RiseDuringCapture = true };
                gates[level] = g;
            }
            return g;
        }

        static void Regroup()
        {
            RtXData.Init();
            if (settle < 0f) { settle = SettleDefault; try { settle = Mathf.Clamp(EditorState.Get(SettleKey, SettleDefault), 0f, 30f); } catch { } }
            groupsDirty = false;
            grouped.Clear(); grouped.AddRange(mods);
            var old = new Dictionary<string, Group>();
            foreach (var g in groups) old[g.label] = g;
            groups.Clear();
            var byLabel = new Dictionary<string, Group>();
            foreach (var m in mods)
            {
                if (!m.enabled || HasBlocking(m)) continue;
                string anyLabel = null;
                foreach (var op in m.ops)
                {
                    Derive(op);
                    string label; string[] levels = null;
                    if (op.lvl != null) label = op.lvl;
                    else if (m.levels.Count > 0 && !(op.type == "set" && op.property == "audio"))
                    {
                        if (anyLabel == null) anyLabel = "any of " + string.Join(", ", m.levels.ToArray());
                        label = anyLabel; levels = m.levels.ToArray();
                    }
                    else label = GlobalLevel;
                    Group g;
                    if (!byLabel.TryGetValue(label, out g))
                    {
                        g = new Group { label = label };
                        if (op.lvl != null) g.gates = new[] { GateFor(op.lvl) };
                        else if (levels != null)
                        {
                            g.gates = new LevelGate[levels.Length];
                            for (int i = 0; i < levels.Length; i++) { var sc = Levels.ByName(levels[i]); g.gates[i] = GateFor(sc != null ? sc.name : levels[i]); }
                        }
                        g.seen = new int[g.gates.Length];
                        Group was;
                        if (old.TryGetValue(label, out was)) { g.up = was.up; g.streamSeen = was.streamSeen; g.otherSeen = was.otherSeen; g.lastMs = was.lastMs; g.rounds = was.rounds; g.edges = was.edges; for (int i = 0; i < g.seen.Length && i < was.seen.Length; i++) g.seen[i] = was.seen[i]; }
                        byLabel[label] = g; groups.Add(g);
                    }
                    g.mods.Add(m); g.ops.Add(op);
                    if (op.needsOther && op.settled && op.appliedTo == 0) g.retryOnChange = true;
                    if (op.slow) g.slowOps++;
                    if (g.gates.Length > 0 && !g.up && op.appliedTo == 0 && !op.settled) op.status = "pending (level not loaded)";
                }
            }
            // a list that is already up gets a round for the operations that are new in it
            float now = Time.realtimeSinceStartup;
            foreach (var g in groups)
            {
                if (!g.up) continue;
                bool work = false; g.watching = 0;
                foreach (var op in g.ops) { if (!op.settled) work = true; if (op.watch) g.watching++; }
                if (work) { g.working = true; g.round = 0; g.nextRound = now; }
                if (g.slowOps > 0) { g.slowPause = SlowFirst; g.nextSlow = now + SlowFirst; }
            }
        }

        static bool StreamQuiet(float now) { return now - Levels.StreamChangedAt >= settle; }

        static void TickGroup(Group g, float now)
        {
            bool edge = false;
            if (g.gates.Length == 0)
            {
                // no level: every level change is the edge, once streaming has been quiet
                g.up = true;
                if (Levels.StreamVersion != g.streamSeen && StreamQuiet(now)) { g.streamSeen = Levels.StreamVersion; edge = true; }
            }
            else
            {
                bool any = false;
                for (int k = 0; k < g.gates.Length; k++)
                {
                    if (g.gates[k].Rose(ref g.seen[k])) edge = true;
                    if (g.gates[k].Up) any = true;
                }
                if (!any)
                {
                    // a level that flickers out of "loaded" for less than mods.settle is not an unload
                    if (g.up) { if (g.downSince < 0f) g.downSince = now; else if (now - g.downSince >= settle) { g.downSince = -1f; Unload(g); } }
                    return;
                }
                g.downSince = -1f;
                if (!g.up) edge = true;
                g.up = true;
            }
            if (edge) StartEdge(g, now);
            else if (g.retryOnChange && Levels.StreamVersion != g.otherSeen && StreamQuiet(now))
            {
                // an operation waits for an object of another level: another level change is its next chance
                g.otherSeen = Levels.StreamVersion;
                bool any = false;
                foreach (var op in g.ops) if (op.needsOther && op.appliedTo == 0) { op.settled = false; any = true; }
                if (any) { g.working = true; g.round = 0; g.nextRound = now; }
            }
            if (!g.working && g.slowOps > 0 && now >= g.nextSlow)
            {
                // one more single try for what was not found, with growing pauses while the level stays loaded
                for (int i = 0; i < g.ops.Count; i++) { var op = g.ops[i]; if (op.slow) { op.slow = false; op.settled = false; } }
                g.slowOps = 0;
                g.slowPause = Math.Min(Math.Max(g.slowPause * 2f, SlowFirst), SlowMax);
                g.working = true; g.round = RetryPauses.Length; g.nextRound = now;
            }
            if (g.working && now >= g.nextRound) Round(g, now);
            if (g.watching > 0) Watch(g, now);
        }

        static void StartEdge(Group g, float now)
        {
            g.edges++;
            g.otherSeen = Levels.StreamVersion;
            g.retryOnChange = false;
            g.slowOps = 0; g.slowPause = 0f;
            bool work = false;
            for (int i = 0; i < g.ops.Count; i++)
            {
                var op = g.ops[i];
                // still applied to the same live object: nothing to do (enforce ops stay watched)
                if (op.appliedTo != 0 && op.go != null && (op.go.GetInstanceID() == op.appliedTo || op.go.GetInstanceID() == -op.appliedTo)) continue;
                if (op.type == "set" && op.property == "audio" && op.appliedTo != 0) continue;
                op.go = null; op.settled = false; op.watch = false; op.needsOther = false; op.slow = false;
                if (op.appliedTo != 0) { op.appliedTo = 0; op.appliedProp = null; op.restoreValue = null; }
                op.status = "pending"; op.detail = null;
                work = true;
            }
            g.watching = 0; foreach (var op in g.ops) if (op.watch) g.watching++;
            if (work) { g.working = true; g.round = 0; g.nextRound = now; }
        }

        // An operation whose object is still alive keeps what was applied (target, applied id, restore value): when the
        // level comes back the next load edge sees the same object and does nothing; anything else goes back to pending.
        static void Unload(Group g)
        {
            g.up = false; g.working = false; g.watching = 0; g.retryOnChange = false; g.slowOps = 0;
            for (int i = 0; i < g.ops.Count; i++)
            {
                var op = g.ops[i];
                if (op.type == "set" && op.property == "audio") continue;   // global rules stay as they are
                if (op.appliedTo != 0 && op.go != null) continue;            // kept whole, including a watch for a later re-fire
                op.watch = false; op.needsOther = false; op.slow = false;
                op.go = null; op.settled = false;
                op.appliedTo = 0; op.appliedProp = null; op.restoreValue = null;
                op.status = "pending (level not loaded)"; op.detail = null;
            }
        }

        static void Round(Group g, float now)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < g.mods.Count; i++) { g.mods[i].roundTicks = 0; g.mods[i].roundWork = false; }
            bool left = false;
            for (int i = 0; i < g.ops.Count; i++)
            {
                var op = g.ops[i];
                if (op.settled) continue;
                var m = g.mods[i];
                long a = Stopwatch.GetTimestamp();
                Step(m, op, g);
                m.roundTicks += Stopwatch.GetTimestamp() - a; m.roundWork = true;
                if (!op.settled) left = true;
            }
            bool last = g.round >= RetryPauses.Length;
            if (left && last)
            {
                // late-awake content had its quick chances: from now on a slow retry while the level stays loaded (and,
                // for an object of another level, also on every level change)
                for (int i = 0; i < g.ops.Count; i++)
                {
                    var op = g.ops[i];
                    if (op.settled) continue;
                    op.settled = true; op.slow = true; g.slowOps++;
                    if (op.needsOther) { g.retryOnChange = true; op.status = "failed: " + (op.lastError ?? "an object it needs is not loaded") + " (tried again later)"; }
                    else if (op.status != null && op.status.StartsWith("error", StringComparison.Ordinal)) { /* keeps its error text */ }
                    else { op.status = "not found in " + g.label; op.lastError = "target not found in " + g.label + ": " + op.target; }
                }
                if (g.slowOps > 0) { if (g.slowPause <= 0f) g.slowPause = SlowFirst; g.nextSlow = now + g.slowPause; }
                left = false;
            }
            if (!left) g.working = false; else g.nextRound = now + RetryPauses[g.round++];
            g.watching = 0; foreach (var op in g.ops) if (op.watch) g.watching++;
            g.lastMs = (Stopwatch.GetTimestamp() - t0) * tickMs; g.rounds++;
            resolveRounds++; lastRoundMs = g.lastMs;
            for (int i = 0; i < g.mods.Count; i++) { var m = g.mods[i]; if (m.roundWork) { m.lastResolveMs = m.roundTicks * tickMs; m.roundWork = false; } }
        }

        // operations that are found and wait for something cheap to see: the object becoming active, or the game
        // changing an enforced value back. No path is walked here.
        static void Watch(Group g, float now)
        {
            int n = 0; bool round = false;
            for (int i = 0; i < g.ops.Count; i++)
            {
                var op = g.ops[i];
                if (!op.watch) continue;
                if (op.go == null)
                {
                    op.watch = false; op.go = null; op.appliedTo = 0; op.appliedProp = null; op.restoreValue = null;
                    op.settled = false; op.status = "pending (object went away)"; round = true;
                    continue;
                }
                Step(g.mods[i], op, g);
                if (op.watch) n++;
                if (!op.settled) round = true;
            }
            g.watching = n;
            if (round && !g.working) { g.working = true; g.round = 0; g.nextRound = now; }
        }

        static readonly List<GameObject> foundTmp = new List<GameObject>();

        static string Candidates(List<GameObject> l)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < l.Count && i < 6; i++)
            {
                var t = l[i].transform;
                sb.Append(i > 0 ? "; " : "").Append('#').Append(i + 1).Append(" at ").Append(Fmt.V3(t.position)).Append(l[i].activeInHierarchy ? "" : " [off]");
            }
            if (l.Count > 6) sb.Append("; ... ").Append(l.Count - 6).Append(" more");
            return sb.ToString();
        }

        // one operation of a loaded level: resolve when the target is not known yet, then apply
        static void Step(Mod m, Op op, Group g)
        {
            try
            {
                if (op.type == "set" && op.property == "audio") { StepAudio(m, op); return; }
                if (string.IsNullOrEmpty(op.target) || op.parts == null || op.parts.Length == 0) { op.status = "error: no target"; op.lastError = "no target"; op.settled = true; return; }
                var go = op.go;
                if (go == null)
                {
                    op.go = null;
                    Resolve(op.lvl, op.parts, op.sel, foundTmp);
                    if (foundTmp.Count == 0 && op.type == "action" && op.action == "reparent" && op.arg != null && op.arg != "(root)")
                    {
                        // already moved (by this mod or by hand): the object is found under its new parent instead
                        var np = ObjectSelector.Parse(op.arg);
                        var moved = ResolveAll(np.path + "/" + op.parts[op.parts.Length - 1]);
                        if (moved.Count == 1) { op.go = moved[0]; op.appliedTo = moved[0].GetInstanceID(); op.status = "applied"; op.settled = true; op.watch = false; return; }
                    }
                    if (foundTmp.Count == 0) { op.status = "not loaded"; return; }   // tried again in the next round
                    if (foundTmp.Count > 1)
                    {
                        op.status = "ambiguous (" + foundTmp.Count + ")"; op.detail = foundTmp.Count + " objects match: " + Candidates(foundTmp);
                        op.lastError = "ambiguous: " + foundTmp.Count + " objects match " + op.target; op.settled = true; foundTmp.Clear(); return;
                    }
                    go = op.go = foundTmp[0];
                    foundTmp.Clear();
                }
                int iid = go.GetInstanceID();
                if (op.type == "action") { StepAction(m, op, go, iid); return; }
                StepSet(m, op, go, iid);
            }
            catch (Exception e)
            {
                // tried again in the next rounds (an object destroyed between two calls, an FSM not set up yet)
                op.status = "error: " + e.Message; op.lastError = e.Message; op.watch = false; op.go = null;
            }
        }

        static void StepAction(Mod m, Op op, GameObject go, int iid)
        {
            if (op.replay != "onLoad") { op.status = "recorded only"; op.settled = true; op.watch = false; return; }
            if (op.appliedTo == iid) { op.status = "fired"; op.settled = true; op.watch = false; return; }
            bool active = go.activeInHierarchy;
            // applied while the object was still off: apply once more when the game switches it on (after
            // PlayMaker has initialised the FSM), so the change survives the FSM's own start-up
            if (op.appliedTo == -iid && !active) { if (op.status == null || !op.status.StartsWith("waiting", StringComparison.Ordinal)) op.status = "applied (object still off)"; op.settled = true; op.watch = true; return; }
            // wiring and re-parenting are applied as soon as the object exists, even while the game still
            // has it switched off (INSIDE switches level groups on a few seconds after load): otherwise a
            // trigger can fire during that gap and hit the old wiring. Events / states / signals still wait.
            bool structural = op.action == "fsmWire" || op.action == "sigWire" || op.action == "fsmAction" || op.action == "reparent";
            if (!structural && !active) { op.status = "waiting (inactive)"; op.settled = true; op.watch = true; return; }
            string r; bool ok;
            // a replay is not an edit of the user: it stays out of History (and out of mods recorded from it)
            bool was = ChangeRecorder.recording; ChangeRecorder.recording = false;
            try { actionOk = false; r = Fire(go, op); ok = actionOk; }
            finally { ChangeRecorder.recording = was; }
            if (!ok)
            {
                bool waits = r != null && r.IndexOf("not loaded", StringComparison.Ordinal) >= 0;
                if (op.lastError != r) DevLog.Write("[mods] " + m.name + " op " + op.id + (waits ? " waits: " : " FAILED: ") + r);
                op.lastError = r;
                if (waits) { op.status = "waiting: " + r; op.needsOther = true; op.watch = false; op.settled = false; return; }   // next round
                if (structural && !active)
                {
                    // the FSM may not be set up while its object is off (0.10.0 marked it and fired it again when active)
                    op.appliedTo = -iid; op.status = "waiting (object still off): " + r; op.settled = true; op.watch = true;
                    return;
                }
                op.appliedTo = 0; op.status = "failed: " + r; op.settled = true; op.watch = false;
                return;
            }
            bool early = structural && !active;
            op.appliedTo = early ? -iid : iid; op.status = early ? "applied (object still off)" : "fired"; applyCount++;
            op.settled = true; op.watch = early; op.needsOther = false; op.lastError = null;
            DevLog.Write("[mods] " + m.name + " op " + op.id + ": " + r);
        }

        static bool IsRef(string v) { return v != null && v.StartsWith("ref:", StringComparison.Ordinal); }

        static void StepSet(Mod m, Op op, GameObject go, int iid)
        {
            var prop = op.appliedTo == iid ? op.appliedProp : null;
            if (prop == null)
            {
                string err;
                prop = PropOf(go, op, out err);
                if (prop == null) { op.status = "error: " + err; op.lastError = err; op.watch = false; return; }   // tried again
            }
            object v;
            if (op.hasDecoded) v = op.decoded;
            else
            {
                string derr = Codec.Decode(op.value, prop.ValueType, out v);
                if (derr != null)
                {
                    op.lastError = derr;
                    if (derr == "referenced object not loaded") { op.status = "waiting: " + derr; op.needsOther = true; return; }
                    op.status = "error: " + derr; op.settled = true; op.watch = false; return;
                }
                if (!IsRef(op.value)) { op.decoded = v; op.hasDecoded = true; }
            }
            var cur = prop.Read();
            bool same = ChangeRecorder.Same(cur, v);
            if (op.appliedTo == iid && (!op.enforce || same)) { op.status = same ? "applied" : "applied (game changed it since)"; op.settled = true; op.watch = op.enforce; return; }
            bool first = op.appliedTo != iid;
            if (first)
            {
                op.appliedProp = prop; op.restoreValue = cur;
                // value already in place (e.g. the change is still live from the session it was recorded in):
                // restoring should go back to the recorded original, not to the modded value
                object orig;
                if (same && op.original != null && Codec.Decode(op.original, prop.ValueType, out orig) == null) op.restoreValue = orig;
            }
            bool ok = same;
            if (!same)
            {
                bool was = ChangeRecorder.recording; ChangeRecorder.recording = false;
                try { ok = prop.Write(v); }
                finally { ChangeRecorder.recording = was; }
            }
            if (!ok)
            {
                op.status = "error: could not write " + prop.Label; op.lastError = "could not write " + prop.Label + " (a value inside a null object, or the component refused it)";
                op.appliedProp = null; op.restoreValue = null; op.settled = true; op.watch = false;
                DevLog.Write("[mods] " + m.name + " op " + op.id + " FAILED: " + op.lastError);
                return;
            }
            op.appliedTo = iid; op.status = "applied"; applyCount++; op.settled = true; op.watch = op.enforce; op.needsOther = false; op.lastError = null;
            // an enforced value the game keeps changing back is written four times a second: one log line in ten seconds
            float now = Time.realtimeSinceStartup;
            if (first || now - op.logAt >= 10f)
            {
                op.logAt = now;
                DevLog.Write("[mods] " + m.name + " op " + op.id + ": " + prop.Label + " = " + ChangeRecorder.Short(v) + (same ? " (already)" : first ? "" : " (enforced again)"));
            }
        }

        // audio rules are global: no target to resolve, applied once per session while the mod is enabled
        public const string AudioTarget = "(audio)";
        static void StepAudio(Mod m, Op op)
        {
            op.settled = true; op.watch = false;
            string v = op.value == "null" ? null : op.value;
            if (op.field == null) { op.status = "error: audio op needs field (rule key)"; return; }
            string bad = AudioRules.Check(op.field, v);
            if (bad != null) { op.status = "error: " + bad; op.lastError = bad; return; }
            string cur = AudioRules.Get(op.field);
            var host = AudioRules.Host; int iid = host.GetInstanceID();
            if (op.appliedTo == iid) { op.status = "applied"; return; }
            op.appliedProp = ChangeRecorder.Prop.Make(host, null, ChangeRecorder.PropKind.Audio, op.field);
            op.restoreValue = cur == v && op.original != null && op.original != "null" ? op.original : cur;
            if (cur == v && op.original == "null") op.restoreValue = null;
            AudioRules.Set(op.field, v);
            op.appliedTo = iid; op.status = "applied"; applyCount++;
            DevLog.Write("[mods] " + m.name + " op " + op.id + ": audio " + op.field + " = " + (v ?? "(none)"));
        }

        // set by an applier when the action took effect (or was in effect already); a reply text alone does not say
        internal static bool actionOk;

        static string Fire(GameObject go, Op op)
        {
            if (op.action == "signal")
            {
                var sm = PersistentBehaviour<SignalManager>.instance;
                int n = 0;
                foreach (var s in sm.signalOuts) if (s != null && s.isActive && s.gameObject == go && s.debugName == op.arg) { s.Signal(); n++; }
                actionOk = true;
                return "fired signal " + go.name + "." + op.arg + " (" + n + " output(s))";
            }
            if (op.action == "reparent") return Reparent.Apply(go, op.arg, "mod");
            if (op.action == "anim")
            {
                var an = go.GetComponent<Animation>();
                if (an == null || !AnimSafe.CanWalkStates(an) || an[op.arg] == null) return "no clip '" + op.arg + "' on " + go.name;
                an.Stop(); an.Play(op.arg); actionOk = true; return "played clip " + op.arg + " on " + go.name;
            }
            foreach (var f in go.GetComponents<PlayMakerFSM>())
            {
                if (op.fsm != null && f.FsmName != op.fsm) continue;
                if (op.action == "fsmEvent") { f.SendEvent(op.arg); actionOk = true; return "sent '" + op.arg + "' to " + go.name + "/" + f.FsmName; }
                if (op.action == "fsmState") { f.SetState(op.arg); actionOk = true; return "forced " + go.name + "/" + f.FsmName + " -> " + op.arg; }
                if (op.action == "fsmWire") return FsmEdit.Apply(f, op.arg, "mod");
                if (op.action == "sigWire") return SignalEdit.Apply(f, op.arg, "mod");
                if (op.action == "fsmAction") return ActionEdit.Apply(f, op.arg, "mod");
            }
            return "no FSM '" + op.fsm + "' on " + go.name;
        }

        // ---------------------------------------------------------------- dry run (an explicit command: resolves now, changes nothing)
        static string DryStep(Mod m, Op op)
        {
            try
            {
                Derive(op);
                if (op.type == "set" && op.property == "audio")
                {
                    string av = op.value == "null" ? null : op.value;
                    string acur = op.field != null ? AudioRules.Get(op.field) : null;
                    return (acur == av ? "already " : "would set ") + "audio " + op.field + " = " + (av ?? "(none)");
                }
                if (string.IsNullOrEmpty(op.target)) return "no target";
                if (op.lvl != null && !LevelGate.IsLoaded(op.lvl)) return "level not loaded: " + op.lvl + " (" + LevelGate.StateOf(op.lvl) + ")";
                var found = new List<GameObject>();
                Resolve(op.lvl, op.parts, op.sel, found);
                if (found.Count == 0) return "target not loaded: " + op.target + (op.lvl != null ? " (not found in " + op.lvl + ")" : "");
                if (found.Count > 1) return "ambiguous: " + found.Count + " objects match " + op.target + " (" + Candidates(found) + ")";
                var go = found[0];
                if (op.type == "action")
                {
                    if (op.replay != "onLoad") return "action not replayed (replay = never): " + op.note;
                    if (op.appliedTo == go.GetInstanceID()) return "already fired for this load";
                    return "would " + op.action + " '" + op.arg + "' on " + go.name + (go.activeInHierarchy ? "" : " (the object is switched off now)");
                }
                string err;
                var prop = PropOf(go, op, out err);
                if (prop == null) return err;
                object v;
                string derr = Codec.Decode(op.value, prop.ValueType, out v);
                if (derr != null) return derr;
                var cur = prop.Read();
                bool same = ChangeRecorder.Same(cur, v);
                return (same ? "already " : "would set ") + prop.Label + ": " + ChangeRecorder.Short(cur) + (same ? "" : " -> " + ChangeRecorder.Short(v));
            }
            catch (Exception e) { return "error: " + e.Message; }
        }

        public static string DryRun(Mod m)
        {
            var sb = new StringBuilder("DRY RUN " + m.name + " (" + m.ops.Count + " ops, nothing changed)\n");
            foreach (var p in m.problems) sb.Append("  problem: ").Append(p).Append('\n');
            foreach (var op in m.ops) sb.Append("  op ").Append(op.id).Append(": ").Append(DryStep(m, op)).Append('\n');
            return sb.ToString();
        }

        // ---------------------------------------------------------------- enable / disable
        public static void SetEnabled(Mod m, bool on) { TrySetEnabled(m, on); }

        // null when done; otherwise why not (the mod is left as it was)
        public static string TrySetEnabled(Mod m, bool on)
        {
            if (m.loadFailed)
            {
                string why = "refused: " + Path.GetFileName(m.file ?? m.name) + " could not be read (" + (m.loadProblems.Count > 0 ? m.loadProblems[0] : "parse error") + "); the file is left as it is - fix it, then mod reload";
                if (!m.problems.Contains(why)) m.problems.Add(why);
                DevLog.Write("[mods] " + m.name + ": " + why);
                return why;
            }
            m.enabled = on;
            if (!on)
            {
                // restore what the mod overwrote on objects that are still loaded
                foreach (var op in m.ops)
                {
                    if (op.appliedTo != 0 && op.appliedProp != null && op.appliedProp.Alive && op.appliedProp.go.GetInstanceID() == op.appliedTo)
                    {
                        bool was = ChangeRecorder.recording; ChangeRecorder.recording = false;
                        try { op.appliedProp.Write(op.restoreValue); } finally { ChangeRecorder.recording = was; }
                    }
                    op.appliedTo = 0; op.appliedProp = null; op.restoreValue = null; op.go = null;
                    op.settled = false; op.watch = false; op.needsOther = false; op.status = "disabled"; op.detail = null;
                }
            }
            else foreach (var op in m.ops) { op.status = "pending"; op.settled = false; op.watch = false; }
            groupsDirty = true;
            string err = null;
            try { Save(m); } catch (Exception e) { err = "failed: " + m.name + " is " + (on ? "enabled" : "disabled") + " for this session but could not be saved: " + e.Message; DevLog.Error("mod save", e); }
            DevLog.Write("[mods] " + m.name + (on ? " enabled" : " disabled (restored loaded objects)"));
            return err;
        }

        // ---------------------------------------------------------------- text
        static void Count(Mod m, out int applied, out int pending, out int failed, out int skipped)
        {
            applied = pending = failed = skipped = 0;
            foreach (var o in m.ops)
            {
                string w = Word(o);
                if (w == "applied") applied++; else if (w == "error" || w == "ambiguous" || w == "not_found") failed++; else if (w == "skipped" || w == "disabled") skipped++; else pending++;
            }
        }

        // the status as one word: applied | pending | waiting | not_found | ambiguous | error | skipped | disabled
        static string Word(Op o)
        {
            string s = o.status ?? "";
            if (s.StartsWith("error", StringComparison.Ordinal) || s.StartsWith("failed", StringComparison.Ordinal)) return "error";
            if (s.StartsWith("ambiguous", StringComparison.Ordinal)) return "ambiguous";
            if (s.StartsWith("not found", StringComparison.Ordinal)) return "not_found";
            if (s.StartsWith("waiting", StringComparison.Ordinal)) return "waiting";
            if (o.appliedTo != 0 || s == "fired" || s == "applied") return "applied";
            if (s == "recorded only") return "skipped";
            if (s == "disabled") return "disabled";
            return "pending";
        }

        // levels the mod's operations belong to (null entry = global)
        static List<string> LevelsOf(Mod m)
        {
            var l = new List<string>();
            foreach (var op in m.ops) { Derive(op); string lv = op.lvl ?? GlobalLevel; if (!l.Contains(lv)) l.Add(lv); }
            foreach (var x in m.levels) { var sc = Levels.ByName(x); string lv = sc != null ? sc.name : x; if (!l.Contains(lv)) l.Add(lv); }
            return l;
        }

        // one entry per mod, "; " between them: "<name>[ (off)] <applied>/<ops>[ !<problems>] [levels ...; applied a, pending p, failed f; resolve x ms]"
        public static string Summary()
        {
            var sb = new StringBuilder();
            foreach (var m in mods)
            {
                sb.Append(m.name).Append(m.enabled ? "" : " (off)").Append(' ').Append(m.Applied).Append('/').Append(m.ops.Count).Append(m.problems.Count > 0 ? " !" + m.problems.Count : "");
                if (m.loadFailed) { sb.Append(" [file could not be read, not applied]; "); continue; }
                int a, p, f, s; Count(m, out a, out p, out f, out s);
                sb.Append(" [levels ");
                var lv = LevelsOf(m);
                for (int i = 0; i < lv.Count; i++) sb.Append(i > 0 ? ", " : "").Append(lv[i]).Append(lv[i] == GlobalLevel ? "" : LevelGate.IsLoaded(lv[i]) ? " (loaded)" : " (not loaded)");
                if (lv.Count == 0) sb.Append("none");
                sb.Append("; applied ").Append(a).Append(", pending ").Append(p).Append(", failed ").Append(f);
                sb.Append("; resolve ").Append(Fmt.F(m.lastResolveMs, "0.###")).Append(" ms]; ");
            }
            return sb.Length > 0 ? sb.ToString() : "none";
        }

        static Json.Obj ModJson(Mod m, bool withOps)
        {
            var o = new Json.Obj();
            int a, p, f, s; Count(m, out a, out p, out f, out s);
            o["name"] = m.name; o["enabled"] = m.enabled; o["file"] = m.file; o["title"] = m.title; o["description"] = m.description;
            o["ops"] = m.ops.Count; o["applied"] = a; o["pending"] = p; o["failed"] = f; o["skipped"] = s;
            o["loadFailed"] = m.loadFailed; o["blocked"] = HasBlocking(m);
            o["problems"] = new List<object>(m.problems.ToArray());
            var lv = new List<object>();
            foreach (var name in LevelsOf(m))
            {
                var j = new Json.Obj();
                j["level"] = name;
                if (name == GlobalLevel) { j["state"] = "global"; j["loaded"] = true; }
                else { j["state"] = LevelGate.StateOf(name); j["loaded"] = LevelGate.IsLoaded(name); }
                lv.Add(j);
            }
            o["levels"] = lv;
            o["lastResolveMs"] = m.lastResolveMs;
            if (!withOps) return o;
            var ops = new List<object>();
            foreach (var op in m.ops)
            {
                var j = new Json.Obj();
                j["id"] = op.id; j["type"] = op.type; j["target"] = op.target; j["level"] = op.lvl ?? GlobalLevel;
                j["status"] = Word(op); j["text"] = op.status;
                if (op.detail != null) j["detail"] = op.detail;
                if (op.lastError != null) j["error"] = op.lastError;
                if (op.type == "set")
                {
                    j["property"] = op.property; if (op.field != null) j["field"] = op.field; if (op.component != null) { j["component"] = op.component; j["componentIndex"] = op.componentIndex; }
                    j["value"] = op.value; if (op.original != null) j["original"] = op.original; j["enforce"] = op.enforce;
                }
                else { j["action"] = op.action; if (op.fsm != null) j["fsm"] = op.fsm; j["arg"] = op.arg; j["replay"] = op.replay; }
                ops.Add(j);
            }
            o["operations"] = ops;
            return o;
        }

        // "mod list json": every mod with its levels, counts and the cost of its last resolve round
        public static string ListJson()
        {
            var o = new Json.Obj();
            o["ok"] = true; o["runtime"] = runtimeEnabled; o["dir"] = Dir; o["count"] = mods.Count;
            o["resolveRounds"] = resolveRounds; o["lastRoundMs"] = lastRoundMs; o["settleSeconds"] = settle < 0 ? SettleDefault : settle;
            var l = new List<object>();
            foreach (var m in mods) l.Add(ModJson(m, false));
            o["mods"] = l;
            var gl = new List<object>();
            foreach (var g in groups)
            {
                var j = new Json.Obj();
                j["level"] = g.label; j["up"] = g.up; j["ops"] = g.ops.Count; j["working"] = g.working; j["watching"] = g.watching; j["loads"] = g.edges; j["rounds"] = g.rounds; j["lastRoundMs"] = g.lastMs;
                gl.Add(j);
            }
            o["levelLists"] = gl;
            return JsonLine.Write(o);
        }

        // "mod show <name> json"
        public static string StatusJson(Mod m) { var o = ModJson(m, true); o["ok"] = true; return JsonLine.Write(o); }

        public static string Status(Mod m)
        {
            var sb = new StringBuilder();
            sb.Append(m.name).Append(m.enabled ? "  ENABLED" : "  disabled").Append("   ").Append(m.Applied).Append('/').Append(m.ops.Count).Append(" applied   ").Append(m.file).Append('\n');
            if (!string.IsNullOrEmpty(m.description)) sb.Append("  ").Append(m.description).Append('\n');
            foreach (var p in m.problems) sb.Append("  problem: ").Append(p).Append('\n');
            if (!m.loadFailed)
            {
                int a, pe, f, s; Count(m, out a, out pe, out f, out s);
                sb.Append("  levels: ");
                var lv = LevelsOf(m);
                for (int i = 0; i < lv.Count; i++) sb.Append(i > 0 ? ", " : "").Append(lv[i]).Append(lv[i] == GlobalLevel ? "" : " (" + LevelGate.StateOf(lv[i]) + ")");
                sb.Append("   applied ").Append(a).Append(", pending ").Append(pe).Append(", failed ").Append(f).Append("   last resolve ").Append(Fmt.F(m.lastResolveMs, "0.###")).Append(" ms\n");
            }
            foreach (var op in m.ops)
            {
                sb.Append("  op ").Append(op.id).Append(" [").Append(op.status).Append("] ");
                if (op.type == "set") sb.Append(op.target).Append(op.component != null ? "::" + op.component + (op.componentIndex > 0 ? "#" + op.componentIndex : "") : "").Append(' ').Append(op.property == "field" ? op.field : op.property).Append(" = ").Append(op.value).Append(op.original != null ? "  (was " + op.original + ")" : "").Append(op.enforce ? "  enforce" : "");
                else sb.Append(op.action).Append(' ').Append(op.arg).Append(" on ").Append(op.target).Append("  replay ").Append(op.replay);
                sb.Append('\n');
                if (op.detail != null) sb.Append("      ").Append(op.detail).Append('\n');
            }
            return sb.ToString();
        }

        // bridge: mod list | show <name> | record <name> [description] | dry <name> | validate <name> | enable|disable <name> | reload | delete <name>
        // (last word "json" for list / show / enable / disable / reload: one JSON line)
        public static string Command(List<string> a)
        {
            bool json = a.Count > 1 && a[a.Count - 1] == "json";
            int argc = json ? a.Count - 1 : a.Count;
            string sub = argc > 1 ? a[1].ToLowerInvariant() : "list";
            if (sub == "list") return json ? ListJson() : mods.Count == 0 ? "no mods in " + Dir : Summary();
            if (sub == "reload") { LoadAll(); return json ? ListJson() : Summary(); }
            if (sub == "record")
            {
                if (argc < 3) return "usage: mod record <name> [description]";
                return HistoryPanel.RecordMod(a[2], argc > 3 ? string.Join(" ", a.GetRange(3, argc - 3).ToArray()) : "", ChangeRecorder.NetChanges(), new List<ChangeRecorder.Entry>());
            }
            if (argc < 3) return "usage: mod " + sub + " <name>";
            var m = Find(a[2]); if (m == null) return "not found: no mod " + a[2];
            switch (sub)
            {
                case "show": return json ? StatusJson(m) : Status(m);
                case "dry": return DryRun(m);
                case "validate": Validate(m); groupsDirty = true; return m.problems.Count == 0 ? "valid" : string.Join("\n", m.problems.ToArray());
                case "enable":
                case "disable":
                    {
                        string err = TrySetEnabled(m, sub == "enable");
                        if (json) { var o = ModJson(m, true); o["ok"] = err == null; if (err != null) o["error"] = err; return JsonLine.Write(o); }
                        return err != null ? err + "\n" + Status(m) : Status(m);
                    }
                case "delete":
                    {
                        TrySetEnabledNoSave(m);
                        mods.Remove(m); groupsDirty = true;
                        try { if (m.file != null) File.Delete(m.file); } catch (Exception e) { return "failed: " + e.Message; }
                        return "deleted " + m.file;
                    }
            }
            return "unknown mod command";
        }

        // a mod that is removed gives back what it changed on loaded objects (its file is not written)
        static void TrySetEnabledNoSave(Mod m)
        {
            foreach (var op in m.ops)
            {
                if (op.appliedTo != 0 && op.appliedProp != null && op.appliedProp.Alive && op.appliedProp.go.GetInstanceID() == op.appliedTo)
                {
                    bool was = ChangeRecorder.recording; ChangeRecorder.recording = false;
                    try { op.appliedProp.Write(op.restoreValue); } catch { } finally { ChangeRecorder.recording = was; }
                }
                op.appliedTo = 0; op.appliedProp = null; op.go = null;
            }
        }

        // numbers for the doctor check
        public static void Totals(out int enabled, out int withProblems, out int applied, out int pending, out int failed)
        {
            enabled = withProblems = applied = pending = failed = 0;
            foreach (var m in mods)
            {
                if (m.enabled) enabled++;
                if (m.loadFailed || HasBlocking(m)) withProblems++;
                if (!m.enabled || m.loadFailed) continue;
                int a, p, f, s; Count(m, out a, out p, out f, out s);
                applied += a; pending += p; failed += f;
            }
        }
    }

    // value <-> text for mod files
    public static class Codec
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static string Encode(object v, Type t, out string text, out string typeName)
        {
            text = null; typeName = t != null ? t.Name : "null";
            if (v == null || (v is UObj && (UObj)v == null)) { text = "null"; return typeof(UObj).IsAssignableFrom(t) || !t.IsValueType ? null : "null value for " + t.Name; }
            if (v is bool) { text = (bool)v ? "true" : "false"; return null; }
            if (v is float) { text = ((float)v).ToString("R", IC); return null; }
            if (v is double) { text = ((double)v).ToString("R", IC); return null; }
            if (v is int || v is uint || v is long || v is short || v is byte) { text = Convert.ToString(v, IC); return null; }
            if (v is string) { text = (string)v; return null; }
            if (v is Enum) { text = v.ToString(); return null; }
            if (v is Vector3) { var x = (Vector3)v; text = F(x.x) + "," + F(x.y) + "," + F(x.z); return null; }
            if (v is Vector2) { var x = (Vector2)v; text = F(x.x) + "," + F(x.y); return null; }
            if (v is Vector4) { var x = (Vector4)v; text = F(x.x) + "," + F(x.y) + "," + F(x.z) + "," + F(x.w); return null; }
            if (v is Quaternion) { var x = (Quaternion)v; text = F(x.x) + "," + F(x.y) + "," + F(x.z) + "," + F(x.w); return null; }
            if (v is Color) { var x = (Color)v; text = F(x.r) + "," + F(x.g) + "," + F(x.b) + "," + F(x.a); return null; }
            var go = v as GameObject;
            if (go != null) { text = "ref:" + ObjectSelector.From(go).ToString(); return null; }
            var c = v as Component;
            if (c != null) { text = "ref:" + ObjectSelector.From(c.gameObject).ToString() + "|" + c.GetType().Name; return null; }
            var uo = v as UObj;
            if (uo != null) return "asset references (" + uo.GetType().Name + " '" + uo.name + "') are not stored in mods";
            return "values of type " + v.GetType().Name + " are not supported yet";
        }

        static string F(float f) { return f.ToString("R", IC); }

        public static bool CanDecode(string s, Type t) { object v; return Decode(s, t, out v) == null || (s != null && s.StartsWith("ref:")); }

        public static string Decode(string s, Type t, out object v)
        {
            v = null;
            try
            {
                if (s == null || s == "null") { if (t.IsValueType) return "null for " + t.Name; return null; }
                if (t == typeof(bool)) { v = s == "true" || s == "1" || s == "on"; if (s != "true" && s != "false" && s != "1" && s != "0" && s != "on" && s != "off") return "not a bool"; return null; }
                if (t == typeof(string)) { v = s; return null; }
                if (t.IsEnum) { v = Enum.Parse(t, s); return null; }
                if (t == typeof(float) || t == typeof(double) || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(short) || t == typeof(byte)) { v = Convert.ChangeType(double.Parse(s, NumberStyles.Float, IC), t, IC); return null; }
                var p = s.Split(',');
                Func<int, float> f = i => float.Parse(p[i], NumberStyles.Float, IC);
                if (t == typeof(Vector3)) { if (p.Length != 3) return "need x,y,z"; v = new Vector3(f(0), f(1), f(2)); return null; }
                if (t == typeof(Vector2)) { if (p.Length != 2) return "need x,y"; v = new Vector2(f(0), f(1)); return null; }
                if (t == typeof(Vector4)) { if (p.Length != 4) return "need x,y,z,w"; v = new Vector4(f(0), f(1), f(2), f(3)); return null; }
                if (t == typeof(Quaternion)) { if (p.Length != 4) return "need x,y,z,w"; v = new Quaternion(f(0), f(1), f(2), f(3)); return null; }
                if (t == typeof(Color)) { if (p.Length != 4) return "need r,g,b,a"; v = new Color(f(0), f(1), f(2), f(3)); return null; }
                if (s.StartsWith("ref:") && typeof(UObj).IsAssignableFrom(t))
                {
                    string body = s.Substring(4), comp = null;
                    int bar = body.LastIndexOf('|');
                    if (bar > 0) { comp = body.Substring(bar + 1); body = body.Substring(0, bar); }
                    var l = Mods.ResolveAll(body);
                    if (l.Count != 1) return l.Count == 0 ? "referenced object not loaded" : "referenced object ambiguous";
                    if (t == typeof(GameObject)) { v = l[0]; return null; }
                    foreach (var c in l[0].GetComponents<Component>()) if (c != null && t.IsAssignableFrom(c.GetType()) && (comp == null || c.GetType().Name == comp)) { v = c; return null; }
                    return "referenced component not found";
                }
                return "unsupported type " + t.Name;
            }
            catch (Exception e) { return e.Message; }
        }
    }
}
