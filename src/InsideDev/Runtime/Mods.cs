using System;
using System.Collections.Generic;
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
    // never instance ids and never copied game assets. ModRuntime resolves each operation whenever its target is
    // loaded (INSIDE streams subscenes in and out) and applies it once per loaded instance, so the change survives
    // restarts, savepoint loads and streaming. Disabling a mod restores the values it overwrote on objects still loaded.
    //
    //   set     target + property (active | enabled | position | localPosition | localEuler | localScale | field)
    //   action  target + signal / fsmEvent / fsmState / fsmWire ("state|event|toState") / sigWire ("+|>in|out|sender") / fsmAction ("state|index|field|value"); replayed on load only when "replay": "onLoad"
    public static class Mods
    {
        public const string Format = "insidedev-mod";
        public const int FormatVersion = 1;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public sealed class Op
        {
            public int id; public string type = "set";
            public string target, component, property, field, valueType, value, original, note;
            public int componentIndex;
            public string action, fsm, arg, replay = "never";
            public bool enforce;                               // re-apply if the game changes it back

            // runtime state
            public int appliedTo;                              // instance id the op was applied to (0 = not applied)
            public string status = "pending";
            public object restoreValue; public ChangeRecorder.Prop appliedProp;
            public string lastError;
        }

        public sealed class Mod
        {
            public string file, name, title, description, author, created;
            public bool enabled = true;
            public readonly List<Op> ops = new List<Op>();
            public readonly List<string> problems = new List<string>();   // load / validation problems
            public int Applied { get { int n = 0; foreach (var o in ops) if (o.appliedTo != 0) n++; return n; } }
        }

        public static readonly List<Mod> mods = new List<Mod>();
        public static string Dir { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "mods"); } }
        public static bool runtimeEnabled = true;

        // ---------------------------------------------------------------- load / save
        public static void LoadAll()
        {
            mods.Clear();
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                foreach (var f in Directory.GetFiles(Dir, "*.json"))
                {
                    var m = Load(f);
                    if (m != null) mods.Add(m);
                }
                DevLog.Write("[mods] " + mods.Count + " mod(s) in " + Dir + ": " + Summary());
            }
            catch (Exception e) { DevLog.Error("mods load", e); }
        }

        static Mod Load(string file)
        {
            var m = new Mod { file = file, name = Path.GetFileNameWithoutExtension(file) };
            try
            {
                var o = Json.Parse(File.ReadAllText(file)) as Json.Obj;
                if (o == null) { m.problems.Add("not a JSON object"); return m; }
                if (o.Str("format") != Format) m.problems.Add("format is '" + o.Str("format") + "', expected '" + Format + "'");
                if (o.Num("formatVersion") > FormatVersion) m.problems.Add("formatVersion " + o.Num("formatVersion") + " is newer than this editor (" + FormatVersion + ")");
                m.name = o.Str("name", m.name); m.title = o.Str("title", m.name); m.description = o.Str("description", "");
                m.author = o.Str("author", ""); m.created = o.Str("created", ""); m.enabled = o.Bool("enabled", true);
                foreach (var x in o.Arr("operations"))
                {
                    var j = x as Json.Obj; if (j == null) continue;
                    m.ops.Add(new Op
                    {
                        id = (int)j.Num("id", m.ops.Count + 1), type = j.Str("type", "set"), target = j.Str("target"), component = j.Str("component"),
                        componentIndex = (int)j.Num("componentIndex"), property = j.Str("property"), field = j.Str("field"), valueType = j.Str("valueType"),
                        value = j.Str("value"), original = j.Str("original"), note = j.Str("note"), action = j.Str("action"), fsm = j.Str("fsm"),
                        arg = j.Str("arg"), replay = j.Str("replay", "never"), enforce = j.Bool("enforce")
                    });
                }
                Validate(m);
            }
            catch (Exception e) { m.problems.Add("parse error: " + e.Message); m.enabled = false; }
            return m;
        }

        public static string Save(Mod m)
        {
            var o = new Json.Obj();
            o["format"] = Format; o["formatVersion"] = (double)FormatVersion;
            o["name"] = m.name; o["title"] = m.title ?? m.name; o["description"] = m.description ?? ""; o["author"] = m.author ?? "";
            o["created"] = m.created ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"); o["game"] = "INSIDE"; o["editor"] = "InsideDev " + Boot.Version;
            o["enabled"] = m.enabled;
            var ops = new List<object>();
            foreach (var op in m.ops)
            {
                var j = new Json.Obj();
                j["id"] = (double)op.id; j["type"] = op.type; j["target"] = op.target;
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
            if (m.file == null) m.file = Path.Combine(Dir, Safe(m.name) + ".json");
            Directory.CreateDirectory(Dir);
            File.WriteAllText(m.file, Json.Write(o) + "\n");
            return m.file;
        }

        static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "mod";
        }

        public static Mod Find(string name)
        {
            foreach (var m in mods) if (string.Equals(m.name, name, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }

        // ---------------------------------------------------------------- record from history
        // netChanges: state changes to include; actions: history entries whose action should be included
        public static Mod Record(string name, string description, List<ChangeRecorder.Net> netChanges, List<ChangeRecorder.Entry> actions, out List<string> skipped)
        {
            skipped = new List<string>();
            var m = new Mod { name = name, title = name, description = description, created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), enabled = true };
            int id = 1;
            foreach (var n in netChanges)
            {
                var p = n.prop;
                if (!p.Alive) { skipped.Add(p.Label + ": object is gone"); continue; }
                string val, orig, vt;
                string err = Codec.Encode(n.lastAfter, p.ValueType, out val, out vt);
                if (err != null) { skipped.Add(p.Label + ": " + err); continue; }
                string vt2; Codec.Encode(n.original, p.ValueType, out orig, out vt2);
                bool audio = p.kind == ChangeRecorder.PropKind.Audio;
                var op = new Op
                {
                    id = id++, type = "set", target = audio ? AudioTarget : ObjectSelector.From(p.go).ToString(), property = ChangeRecorder.PropName(p.kind),
                    field = p.kind == ChangeRecorder.PropKind.Field || audio ? p.field : null, valueType = vt, value = val, original = orig
                };
                if (p.comp != null) { op.component = p.compType; op.componentIndex = p.compIndex; }
                m.ops.Add(op);
            }
            if (actions != null)
                foreach (var e in actions)
                {
                    var a = e.action;
                    if (a == null || a.go == null) { skipped.Add(e.label + ": object is gone"); continue; }
                    string tgt = (a.action == "reparent" ? Reparent.OriginalSelector(a.go) : null) ?? ObjectSelector.From(a.go).ToString();
                    m.ops.Add(new Op { id = id++, type = "action", target = tgt, action = a.action, fsm = a.fsm, arg = a.arg, replay = "onLoad", note = e.label });
                }
            Validate(m);
            return m;
        }

        // ---------------------------------------------------------------- validation (static)
        public static void Validate(Mod m)
        {
            m.problems.Clear();
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
            }
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
        // Cheap, independent of the object database (which only scans while the editor is open): the root is found
        // by name, then children are walked by name; duplicate names are all followed, ties broken by position.
        public static List<GameObject> ResolveAll(string selector)
        {
            var sel = ObjectSelector.Parse(selector);
            var res = new List<GameObject>();
            var parts = sel.path.Split('/');
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
            if (res.Count > 1 && sel.hasPos)
            {
                var near = res.FindAll(g => (g.transform.position - sel.pos).sqrMagnitude < 0.25f);
                if (near.Count == 1) return near;
            }
            return res;
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
            else if (!ChangeRecorder.TryPropKind(op.property, out k)) { err = "unknown property"; return null; }
            Component c = null;
            if (op.component != null)
            {
                c = CompOf(go, op);
                if (c == null) { err = "no " + op.component + (op.componentIndex > 0 ? "#" + op.componentIndex : "") + " on " + go.name; return null; }
            }
            return ChangeRecorder.Prop.Make(go, c, k, k == ChangeRecorder.PropKind.Field ? op.field : null);
        }

        // ---------------------------------------------------------------- runtime
        static float nextCheck;
        public static int applyCount;

        public static void Tick()
        {
            if (!runtimeEnabled || mods.Count == 0 || Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 0.25f;
            foreach (var m in mods)
            {
                if (!m.enabled || m.problems.Count > 0 && HasBlocking(m)) continue;
                foreach (var op in m.ops) Step(m, op, false);
            }
        }

        static bool HasBlocking(Mod m) { foreach (var p in m.problems) if (p.StartsWith("parse") || p.StartsWith("format") || p.StartsWith("not a")) return true; return false; }

        // dry = only report what would happen
        static string Step(Mod m, Op op, bool dry)
        {
            try
            {
                if (op.type == "set" && op.property == "audio") return StepAudio(m, op, dry);
                var found = ResolveAll(op.target);
                if (found.Count == 0 && op.type == "action" && op.action == "reparent" && op.arg != null && op.arg != "(root)")
                {
                    // already moved (by this mod or by hand): the object is found under its new parent instead
                    var np = ObjectSelector.Parse(op.arg); var ts = ObjectSelector.Parse(op.target);
                    var moved = ResolveAll(np.path + "/" + ts.name);
                    if (moved.Count == 1) { op.appliedTo = moved[0].GetInstanceID(); op.status = "applied"; return "already under " + np.name; }
                }
                if (found.Count == 0) { op.status = "not loaded"; if (!dry && op.appliedTo != 0) op.appliedTo = 0; return "target not loaded: " + op.target; }
                if (found.Count > 1) { op.status = "ambiguous (" + found.Count + ")"; return "ambiguous: " + found.Count + " objects match " + op.target; }
                var go = found[0];
                int iid = go.GetInstanceID();
                if (op.type == "action")
                {
                    if (op.replay != "onLoad") { op.status = "recorded only"; return "action not replayed (replay = never): " + op.note; }
                    if (op.appliedTo == iid) { op.status = "fired"; return "already fired for this load"; }
                    // applied while the object was still off: apply once more when the game switches it on (after
                    // PlayMaker has initialised the FSM), so the change survives the FSM's own start-up
                    if (op.appliedTo == -iid && !go.activeInHierarchy) { op.status = "applied (object still off)"; return "applied early"; }
                    if (dry) return "would " + op.action + " '" + op.arg + "' on " + go.name;
                    // wiring and re-parenting are applied as soon as the object exists, even while the game still
                    // has it switched off (INSIDE switches level groups on a few seconds after load): otherwise a
                    // trigger can fire during that gap and hit the old wiring. Events / states / signals still wait.
                    bool structural = op.action == "fsmWire" || op.action == "sigWire" || op.action == "fsmAction" || op.action == "reparent";
                    if (!structural && !go.activeInHierarchy) { op.status = "waiting (inactive)"; return "target inactive"; }
                    string r = Fire(go, op);
                    bool early = structural && !go.activeInHierarchy;
                    op.appliedTo = early ? -iid : iid; op.status = early ? "applied (object still off)" : "fired"; applyCount++;
                    DevLog.Write("[mods] " + m.name + " op " + op.id + ": " + r);
                    return r;
                }
                string err;
                var prop = PropOf(go, op, out err);
                if (prop == null) { op.status = "error: " + err; op.lastError = err; return err; }
                object v;
                string derr = Codec.Decode(op.value, prop.ValueType, out v);
                if (derr != null) { op.status = "error: " + derr; return derr; }
                var cur = prop.Read();
                bool same = ChangeRecorder.Same(cur, v);
                if (dry) return (same ? "already " : "would set ") + prop.Label + ": " + ChangeRecorder.Short(cur) + (same ? "" : " -> " + ChangeRecorder.Short(v));
                if (op.appliedTo == iid && (!op.enforce || same)) { op.status = same ? "applied" : "applied (game changed it since)"; return "applied"; }
                if (op.appliedTo != iid)
                {
                    op.appliedProp = prop; op.restoreValue = cur;
                    // value already in place (e.g. the change is still live from the session it was recorded in):
                    // restoring should go back to the recorded original, not to the modded value
                    object orig;
                    if (same && op.original != null && Codec.Decode(op.original, prop.ValueType, out orig) == null) op.restoreValue = orig;
                }
                bool was = ChangeRecorder.recording; ChangeRecorder.recording = false;
                try { if (!same) prop.Write(v); }
                finally { ChangeRecorder.recording = was; }
                op.appliedTo = iid; op.status = "applied"; applyCount++;
                DevLog.Write("[mods] " + m.name + " op " + op.id + ": " + prop.Label + " = " + ChangeRecorder.Short(v) + (same ? " (already)" : ""));
                return "applied";
            }
            catch (Exception e) { op.status = "error: " + e.Message; return "error: " + e.Message; }
        }

        // audio rules are global: no target to resolve, applied once per session while the mod is enabled
        public const string AudioTarget = "(audio)";
        static string StepAudio(Mod m, Op op, bool dry)
        {
            string v = op.value == "null" ? null : op.value;
            string cur = AudioRules.Get(op.field);
            if (dry) return (cur == v ? "already " : "would set ") + "audio " + op.field + " = " + (v ?? "(none)");
            var host = AudioRules.Host; int iid = host.GetInstanceID();
            if (op.appliedTo == iid) { op.status = "applied"; return "applied"; }
            op.appliedProp = ChangeRecorder.Prop.Make(host, null, ChangeRecorder.PropKind.Audio, op.field);
            op.restoreValue = cur == v && op.original != null && op.original != "null" ? op.original : cur;
            if (cur == v && op.original == "null") op.restoreValue = null;
            AudioRules.Set(op.field, v);
            op.appliedTo = iid; op.status = "applied"; applyCount++;
            DevLog.Write("[mods] " + m.name + " op " + op.id + ": audio " + op.field + " = " + (v ?? "(none)"));
            return "applied";
        }

        static string Fire(GameObject go, Op op)
        {
            if (op.action == "signal")
            {
                var sm = PersistentBehaviour<SignalManager>.instance;
                int n = 0;
                foreach (var s in sm.signalOuts) if (s != null && s.isActive && s.gameObject == go && s.debugName == op.arg) { s.Signal(); n++; }
                return "fired signal " + go.name + "." + op.arg + " (" + n + " output(s))";
            }
            if (op.action == "reparent") return Reparent.Apply(go, op.arg, "mod");
            if (op.action == "anim")
            {
                var an = go.GetComponent<Animation>();
                if (an == null || !AnimSafe.CanWalkStates(an) || an[op.arg] == null) return "no clip '" + op.arg + "' on " + go.name;
                an.Stop(); an.Play(op.arg); return "played clip " + op.arg + " on " + go.name;
            }
            foreach (var f in go.GetComponents<PlayMakerFSM>())
            {
                if (op.fsm != null && f.FsmName != op.fsm) continue;
                if (op.action == "fsmEvent") { f.SendEvent(op.arg); return "sent '" + op.arg + "' to " + go.name + "/" + f.FsmName; }
                if (op.action == "fsmState") { f.SetState(op.arg); return "forced " + go.name + "/" + f.FsmName + " -> " + op.arg; }
                if (op.action == "fsmWire") return FsmEdit.Apply(f, op.arg, "mod");
                if (op.action == "sigWire") return SignalEdit.Apply(f, op.arg, "mod");
                if (op.action == "fsmAction") return ActionEdit.Apply(f, op.arg, "mod");
            }
            return "no FSM '" + op.fsm + "' on " + go.name;
        }

        public static string DryRun(Mod m)
        {
            var sb = new StringBuilder("DRY RUN " + m.name + " (" + m.ops.Count + " ops, nothing changed)\n");
            foreach (var p in m.problems) sb.Append("  problem: ").Append(p).Append('\n');
            foreach (var op in m.ops) sb.Append("  op ").Append(op.id).Append(": ").Append(Step(m, op, true)).Append('\n');
            return sb.ToString();
        }

        public static void SetEnabled(Mod m, bool on)
        {
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
                    op.appliedTo = 0; op.status = "disabled";
                }
            }
            else foreach (var op in m.ops) op.status = "pending";
            try { Save(m); } catch (Exception e) { DevLog.Error("mod save", e); }
            DevLog.Write("[mods] " + m.name + (on ? " enabled" : " disabled (restored loaded objects)"));
        }

        public static string Summary()
        {
            var sb = new StringBuilder();
            foreach (var m in mods) sb.Append(m.name).Append(m.enabled ? "" : " (off)").Append(' ').Append(m.Applied).Append('/').Append(m.ops.Count).Append(m.problems.Count > 0 ? " !" + m.problems.Count : "").Append("; ");
            return sb.Length > 0 ? sb.ToString() : "none";
        }

        public static string Status(Mod m)
        {
            var sb = new StringBuilder();
            sb.Append(m.name).Append(m.enabled ? "  ENABLED" : "  disabled").Append("   ").Append(m.Applied).Append('/').Append(m.ops.Count).Append(" applied   ").Append(m.file).Append('\n');
            if (!string.IsNullOrEmpty(m.description)) sb.Append("  ").Append(m.description).Append('\n');
            foreach (var p in m.problems) sb.Append("  problem: ").Append(p).Append('\n');
            foreach (var op in m.ops)
            {
                sb.Append("  op ").Append(op.id).Append(" [").Append(op.status).Append("] ");
                if (op.type == "set") sb.Append(op.target).Append(op.component != null ? "::" + op.component + (op.componentIndex > 0 ? "#" + op.componentIndex : "") : "").Append(' ').Append(op.property == "field" ? op.field : op.property).Append(" = ").Append(op.value).Append(op.original != null ? "  (was " + op.original + ")" : "");
                else sb.Append(op.action).Append(' ').Append(op.arg).Append(" on ").Append(op.target).Append("  replay ").Append(op.replay);
                sb.Append('\n');
            }
            return sb.ToString();
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
