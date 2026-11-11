using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    // Phase 11: every change the editor makes to the game goes through here.
    //   State changes (active, enabled, transform, field values) are recorded as before/after pairs: undoable, redoable,
    //   revertible to the original value, and exportable as a mod.
    //   Actions (fire a signal, send an FSM event, force an FSM state) are recorded in History but cannot be undone -
    //   what the game did in response is not tracked.
    // Usage at a mutation site:  ChangeRecorder.Before(prop, source);  <apply the change>
    // The after-value is read when the entry is committed (next frame, or End() of an explicit transaction), so a
    // no-op change disappears on its own and a site never has to know the new value.
    public static class ChangeRecorder
    {
        public enum PropKind { Active, Enabled, Position, LocalPosition, LocalEuler, LocalScale, Field, Audio, Custom }   // Audio: global audio rule (AudioRules), field = rule key; Custom: read / write delegates (FSM variables, properties), not stored in mods

        // ---------------------------------------------------------------- property model
        public sealed class Prop
        {
            public GameObject go; public Component comp; public PropKind kind; public string field;   // field: dotted path inside comp
            public int compIndex; public string compType;
            public string key;
            public System.Func<object> read; public System.Action<object> write; public string label;   // PropKind.Custom

            public static Prop Make(GameObject go, Component comp, PropKind kind, string field)
            {
                var p = new Prop { go = go, comp = comp, kind = kind, field = field };
                if (comp != null)
                {
                    p.compType = comp.GetType().Name;
                    int n = 0;
                    foreach (var c in go.GetComponents(comp.GetType())) { if (c == comp) break; if (c != null && c.GetType() == comp.GetType()) n++; }
                    p.compIndex = n;
                }
                p.key = go.GetInstanceID() + "|" + kind + "|" + (comp != null ? comp.GetInstanceID().ToString() : "") + "|" + field;
                return p;
            }

            public bool Alive { get { return go != null && (kind == PropKind.Custom || kind == PropKind.Active || kind == PropKind.Audio || kind == PropKind.Position || kind == PropKind.LocalPosition || kind == PropKind.LocalEuler || kind == PropKind.LocalScale || comp != null); } }

            public string Label
            {
                get
                {
                    string n = go != null ? go.name : "(destroyed)";
                    switch (kind)
                    {
                        case PropKind.Active: return n + " active";
                        case PropKind.Enabled: return n + " " + compType + (compIndex > 0 ? "#" + compIndex : "") + " enabled";
                        case PropKind.Field: return n + " " + compType + (compIndex > 0 ? "#" + compIndex : "") + "." + field;
                        case PropKind.Audio: return "audio " + field;
                        case PropKind.Custom: return label;
                        default: return n + " " + PropName(kind);
                    }
                }
            }

            public Type ValueType
            {
                get
                {
                    switch (kind)
                    {
                        case PropKind.Active: case PropKind.Enabled: return typeof(bool);
                        case PropKind.Audio: return typeof(string);
                        case PropKind.Custom: return typeof(object);
                        case PropKind.Field: { object owner; var f = Resolve(out owner); return f != null ? f.FieldType : typeof(object); }
                        default: return typeof(Vector3);
                    }
                }
            }

            FieldInfo Resolve(out object owner)
            {
                owner = comp;
                if (comp == null || field == null) return null;
                var parts = field.Split('.');
                FieldInfo f = null;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (owner == null) return null;
                    f = FindField(owner.GetType(), parts[i]);
                    if (f == null) return null;
                    if (i < parts.Length - 1) owner = f.GetValue(owner);
                }
                return f;
            }

            public object Read()
            {
                if (go == null) return null;
                var t = go.transform;
                switch (kind)
                {
                    case PropKind.Active: return go.activeSelf;
                    case PropKind.Enabled: return GetEnabled(comp);
                    case PropKind.Position: return t.position;
                    case PropKind.LocalPosition: return t.localPosition;
                    case PropKind.LocalEuler: return t.localEulerAngles;
                    case PropKind.LocalScale: return t.localScale;
                    case PropKind.Field: { object owner; var f = Resolve(out owner); return f != null ? f.GetValue(owner) : null; }
                    case PropKind.Audio: return AudioRules.Get(field);
                    case PropKind.Custom: try { return read != null ? read() : null; } catch { return null; }
                }
                return null;
            }

            public bool Write(object v)
            {
                if (go == null) return false;
                var t = go.transform;
                try
                {
                    switch (kind)
                    {
                        case PropKind.Active: go.SetActive((bool)v); return true;
                        case PropKind.Enabled: return SetEnabled(comp, (bool)v);
                        case PropKind.Position: t.position = (Vector3)v; return true;
                        case PropKind.LocalPosition: t.localPosition = (Vector3)v; return true;
                        case PropKind.LocalEuler: t.localEulerAngles = (Vector3)v; return true;
                        case PropKind.LocalScale: t.localScale = (Vector3)v; return true;
                        case PropKind.Audio: AudioRules.Set(field, v as string); return true;
                        case PropKind.Custom: if (write == null) return false; write(v); return true;
                        case PropKind.Field:
                            {
                                if (comp == null || field == null) return false;
                                // owners[i] holds fields[i]; a struct owner is a boxed copy, so after the write every
                                // struct on the way is stored back into the object that holds it
                                var parts = field.Split('.');
                                var owners = new object[parts.Length]; var fields = new FieldInfo[parts.Length];
                                object owner = comp;
                                for (int i = 0; i < parts.Length; i++)
                                {
                                    if (owner == null) return false;
                                    var fi = FindField(owner.GetType(), parts[i]);
                                    if (fi == null) return false;
                                    owners[i] = owner; fields[i] = fi;
                                    if (i < parts.Length - 1) owner = fi.GetValue(owner);
                                }
                                int last = parts.Length - 1;
                                fields[last].SetValue(owners[last], v);
                                for (int i = last; i > 0 && owners[i].GetType().IsValueType; i--) fields[i - 1].SetValue(owners[i - 1], owners[i]);
                                OnFieldWritten(comp, fields[last]);
                                return true;
                            }
                    }
                }
                catch (Exception e) { DevLog.Write("[history] write " + Label + " failed: " + e.Message); }
                return false;
            }
        }

        public static string PropName(PropKind k)
        {
            switch (k)
            {
                case PropKind.Active: return "active"; case PropKind.Enabled: return "enabled"; case PropKind.Position: return "position";
                case PropKind.LocalPosition: return "localPosition"; case PropKind.LocalEuler: return "localEuler"; case PropKind.LocalScale: return "localScale";
                case PropKind.Audio: return "audio"; case PropKind.Custom: return "custom";
            }
            return "field";
        }
        public static bool TryPropKind(string s, out PropKind k)
        {
            k = PropKind.Field;
            switch (s)
            {
                case "active": k = PropKind.Active; return true; case "enabled": k = PropKind.Enabled; return true; case "position": k = PropKind.Position; return true;
                case "localPosition": k = PropKind.LocalPosition; return true; case "localEuler": k = PropKind.LocalEuler; return true; case "localScale": k = PropKind.LocalScale; return true;
                case "audio": k = PropKind.Audio; return true;
            }
            return false;
        }

        static readonly Dictionary<string, FieldInfo> fieldCache = new Dictionary<string, FieldInfo>();
        public static FieldInfo FindField(Type t, string name)
        {
            string k = t.FullName + "|" + name;
            FieldInfo f;
            if (fieldCache.TryGetValue(k, out f)) return f;
            for (var tt = t; tt != null && f == null; tt = tt.BaseType)
                f = tt.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            fieldCache[k] = f;
            return f;
        }

        public static object GetEnabled(Component c)
        {
            var b = c as Behaviour; if (b != null) return b.enabled;
            var r = c as Renderer; if (r != null) return r.enabled;
            var col = c as Collider; if (col != null) return col.enabled;
            return null;
        }
        public static bool SetEnabled(Component c, bool v)
        {
            var b = c as Behaviour; if (b != null) { b.enabled = v; return true; }
            var r = c as Renderer; if (r != null) { r.enabled = v; return true; }
            var col = c as Collider; if (col != null) { col.enabled = v; return true; }
            return false;
        }

        // Some game components cache values at init; the known ones are reset so a field write takes effect.
        static void OnFieldWritten(Component c, FieldInfo f)
        {
            try
            {
                var lateInit = c.GetType().GetField("isLateInitialized", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (lateInit != null && lateInit.FieldType == typeof(bool)) lateInit.SetValue(c, false);
            }
            catch { }
        }

        // ---------------------------------------------------------------- history model
        public sealed class Op { public Prop prop; public object before, after; }
        public sealed class ActionRec { public GameObject go; public string action, fsm, arg, target; }   // action: signal | fsmEvent | fsmState

        public sealed class Entry
        {
            public int id; public float t; public string label, source;
            public readonly List<Op> ops = new List<Op>();
            public ActionRec action;
            public bool IsAction { get { return action != null; } }
            public string Describe()
            {
                if (action != null) return label;
                if (ops.Count == 1) return ops[0].prop.Label + ": " + Short(ops[0].before) + " -> " + Short(ops[0].after);
                return label + " (" + ops.Count + " changes)";
            }
        }

        static readonly List<Entry> history = new List<Entry>();
        static int cursor;               // entries [0, cursor) are applied; [cursor, Count) were undone (redo stack)
        static Entry pending;            // collecting before-values for this frame / transaction
        static int txDepth;
        static int nextId = 1;
        public static int Version;       // bumps on every history change
        public static bool recording = true;
        public const int MaxEntries = 500;

        public static IList<Entry> Entries { get { return history; } }
        public static int Cursor { get { return cursor; } }
        public static bool CanUndo { get { Flush(); return cursor > 0; } }
        public static bool CanRedo { get { Flush(); return cursor < history.Count; } }

        // ---------------------------------------------------------------- recording
        public static void Begin(string label, string source)
        {
            if (txDepth++ == 0) { Flush(); pending = new Entry { label = label, source = source, t = Time.realtimeSinceStartup }; }
        }
        public static void End()
        {
            if (txDepth <= 0) return;
            if (--txDepth == 0) Commit();
        }

        public static void Before(Prop p, string source, string label = null)
        {
            if (!recording || p == null || p.go == null) return;
            if (pending == null) pending = new Entry { label = label ?? p.Label, source = source, t = Time.realtimeSinceStartup };
            foreach (var o in pending.ops) if (o.prop.key == p.key) return;     // first before-value in the entry wins
            pending.ops.Add(new Op { prop = p, before = Copy(p.Read()) });
        }

        public static void Before(GameObject go, PropKind kind, string source) { if (go != null) Before(Prop.Make(go, null, kind, null), source); }
        public static void BeforeEnabled(Component c, string source) { if (c != null) Before(Prop.Make(c.gameObject, c, PropKind.Enabled, null), source); }
        public static void BeforeField(Component root, string fieldPath, string source) { if (root != null) Before(Prop.Make(root.gameObject, root, PropKind.Field, fieldPath), source); }

        // a value that is not a field of a component (FSM variable, property): the history reads and writes it through the
        // delegates; undoable while go lives, never stored in a mod. key names the value on go (same key = same property).
        public static void BeforeCustom(GameObject go, string key, string label, System.Func<object> read, System.Action<object> write, string source)
        {
            if (go == null) return;
            var p = new Prop { go = go, kind = PropKind.Custom, field = key, label = label, read = read, write = write };
            p.key = go.GetInstanceID() + "|custom|" + key;
            Before(p, source, label);
        }

        public static void SetActive(GameObject go, bool v, string source) { if (go == null) return; Before(go, PropKind.Active, source); go.SetActive(v); }
        public static void SetEnabledRecorded(Component c, bool v, string source) { if (c == null) return; BeforeEnabled(c, source); SetEnabled(c, v); }

        // one-shot actions go straight into history (not undoable)
        static string WireLabel(string arg)
        {
            var p = (arg ?? "").Split('|');
            if (p.Length < 3) return arg;
            string st = p[0] == "*" ? "global" : "[" + p[0] + "]";
            return p[2].Length == 0 ? st + " remove exit '" + p[1] + "'" : st + " on '" + p[1] + "' -> [" + p[2] + "]";
        }

        public static void Action(GameObject go, string action, string fsm, string arg, string source)
        {
            if (!recording) return;
            Flush();
            string target = go != null ? go.name : "?";
            string label = action == "signal" ? "fire signal " + target + "." + arg
                         : action == "fsmEvent" ? "send event '" + arg + "' to " + target + "/" + fsm
                         : action == "fsmState" ? "force " + target + "/" + fsm + " -> state " + arg
                         : action == "fsmWire" ? "rewire " + target + "/" + fsm + ": " + WireLabel(arg)
                         : action == "fsmAction" ? "action setting " + target + "/" + fsm + ": " + ActionEdit.Label(arg)
                         : action == "sigWire" ? "signal wiring " + target + "/" + fsm + ": " + SignalEdit.Label(arg)
                         : action == "reparent" ? "move " + target + " under " + (arg == "(root)" ? "(scene root)" : arg.Substring(arg.LastIndexOf('/') + 1)) : action + " " + target + " " + arg;
            Push(new Entry { label = label, source = source, t = Time.realtimeSinceStartup, action = new ActionRec { go = go, action = action, fsm = fsm, arg = arg, target = target } });
        }

        // called once per frame: commits the frame's pending changes unless a transaction is open
        public static void Tick() { if (txDepth == 0) Commit(); }
        static void Flush() { if (txDepth == 0) Commit(); }

        static void Commit()
        {
            var e = pending; pending = null;
            if (e == null) return;
            for (int i = e.ops.Count - 1; i >= 0; i--)
            {
                var o = e.ops[i];
                o.after = Copy(o.prop.Read());
                if (Same(o.before, o.after)) e.ops.RemoveAt(i);
            }
            if (e.ops.Count == 0) return;
            // merge continuous edits of the same single property (dragging a value, typing) into one entry
            if (e.ops.Count == 1 && cursor == history.Count && cursor > 0)
            {
                var last = history[cursor - 1];
                if (!last.IsAction && last.ops.Count == 1 && last.ops[0].prop.key == e.ops[0].prop.key && last.source == e.source && e.t - last.t < 1.5f)
                {
                    last.ops[0].after = e.ops[0].after; last.t = e.t; Version++;
                    if (Same(last.ops[0].before, last.ops[0].after)) { history.RemoveAt(cursor - 1); cursor--; }
                    return;
                }
            }
            if (e.label == null) e.label = e.ops[0].prop.Label;
            Push(e);
        }

        // Entries that left the list (more than MaxEntries): what they changed stays known here, so the original value
        // of a property and its place in the net changes do not depend on how long the history is.
        static readonly Dictionary<string, Net> folded = new Dictionary<string, Net>();
        static readonly List<Net> foldedOrder = new List<Net>();

        static void Fold(Entry e)
        {
            foreach (var o in e.ops)
            {
                Net n;
                if (!folded.TryGetValue(o.prop.key, out n)) { n = new Net { prop = o.prop, original = o.before, firstEntry = e.id }; folded[o.prop.key] = n; foldedOrder.Add(n); }
                n.lastEntry = e.id; n.lastAfter = o.after;
            }
            if (foldedOrder.Count > 4096)
            {
                // edits of objects that are gone (unloaded levels) are not changes any more
                foldedOrder.RemoveAll(x => !x.prop.Alive);
                folded.Clear();
                foreach (var x in foldedOrder) folded[x.prop.key] = x;
            }
        }

        static void Push(Entry e)
        {
            if (cursor < history.Count) history.RemoveRange(cursor, history.Count - cursor);
            e.id = nextId++;
            history.Add(e);
            if (history.Count > MaxEntries) { Fold(history[0]); history.RemoveAt(0); }
            cursor = history.Count;
            Version++;
            DevLog.Write("[history] #" + e.id + " " + e.Describe() + " (" + e.source + ")");
        }

        // ---------------------------------------------------------------- undo / redo / revert
        public static string Undo()
        {
            Flush();
            if (cursor == 0) return "nothing to undo";
            var e = history[--cursor];
            Version++;
            if (e.IsAction) return "undo: '" + e.label + "' was an action - the game's reaction can't be undone (skipped)";
            bool was = recording; recording = false;
            try { for (int i = e.ops.Count - 1; i >= 0; i--) e.ops[i].prop.Write(e.ops[i].before); }
            finally { recording = was; }
            return "undo: " + e.Describe();
        }

        public static string Redo()
        {
            Flush();
            if (cursor >= history.Count) return "nothing to redo";
            var e = history[cursor++];
            Version++;
            if (e.IsAction) return "redo: '" + e.label + "' is an action - not replayed (fire it again yourself)";
            bool was = recording; recording = false;
            try { foreach (var o in e.ops) o.prop.Write(o.after); }
            finally { recording = was; }
            return "redo: " + e.Describe();
        }

        // n steps back / forward in one call; the reply lists each step
        public static string Undo(int n)
        {
            if (n <= 1) return Undo();
            var sb = new StringBuilder(); int done = 0;
            for (int i = 0; i < n; i++) { Flush(); if (cursor == 0) break; sb.Append(done > 0 ? "\n" : "").Append(Undo()); done++; }
            return done == 0 ? "nothing to undo" : "undid " + done + " step(s)\n" + sb;
        }

        public static string Redo(int n)
        {
            if (n <= 1) return Redo();
            var sb = new StringBuilder(); int done = 0;
            for (int i = 0; i < n; i++) { Flush(); if (cursor >= history.Count) break; sb.Append(done > 0 ? "\n" : "").Append(Redo()); done++; }
            return done == 0 ? "nothing to redo" : "redid " + done + " step(s)\n" + sb;
        }

        // back until entry #id is undone too (ids are the "#n" of the history list)
        public static string UndoTo(int id)
        {
            Flush();
            int at = -1;
            for (int i = 0; i < cursor; i++) if (history[i].id == id) { at = i; break; }
            if (at < 0) return "not found: no applied history entry #" + id;
            return Undo(cursor - at);
        }

        // net changes whose label contains the text (case ignored)
        public static List<Net> FindNet(string text)
        {
            var res = new List<Net>();
            if (string.IsNullOrEmpty(text)) return res;
            foreach (var n in NetChanges()) if (n.prop.Label.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) res.Add(n);
            return res;
        }

        // what "revert all" (or a revert of these) would write, without writing
        public static string RevertPreview(List<Net> l)
        {
            if (l == null || l.Count == 0) return "nothing to revert";
            var sb = new StringBuilder("would revert " + l.Count + " change(s), nothing changed");
            foreach (var n in l) sb.Append("\n  ").Append(n.prop.Label).Append(": ").Append(Short(n.current)).Append(" -> ").Append(Short(n.original));
            return sb.ToString();
        }

        public static string Revert(List<Net> l, string what)
        {
            if (l == null || l.Count == 0) return "nothing to revert";
            if (l.Count == 1) return Revert(l[0]);
            Begin("revert " + what + " (" + l.Count + ")", "revert");
            try { for (int i = l.Count - 1; i >= 0; i--) { Before(l[i].prop, "revert"); l[i].prop.Write(l[i].original); } }
            finally { End(); }
            return "reverted " + l.Count + " change(s)";
        }

        // net effect of the applied history: property -> original value and the current value
        public sealed class Net { public Prop prop; public object original, current, lastAfter; public int firstEntry, lastEntry; }

        public static List<Net> NetChanges(bool includeUnchanged = false)
        {
            Flush();
            var map = new Dictionary<string, Net>();
            var order = new List<Net>();
            foreach (var f in foldedOrder)
            {
                var n0 = new Net { prop = f.prop, original = f.original, lastAfter = f.lastAfter, firstEntry = f.firstEntry, lastEntry = f.lastEntry };
                map[f.prop.key] = n0; order.Add(n0);
            }
            for (int i = 0; i < cursor; i++)
                foreach (var o in history[i].ops)
                {
                    Net n;
                    if (!map.TryGetValue(o.prop.key, out n)) { n = new Net { prop = o.prop, original = o.before, firstEntry = history[i].id }; map[o.prop.key] = n; order.Add(n); }
                    n.lastEntry = history[i].id;
                    n.lastAfter = o.after;
                }
            var res = new List<Net>();
            foreach (var n in order)
            {
                if (!n.prop.Alive) continue;
                if (n.prop.kind == PropKind.Audio && AudioCapture.OwnsRule(n.prop.field)) continue;
                // what the user set (last recorded after-value) decides whether it is a change; values written by
                // mods or by the game afterwards are not the user's edits
                n.current = Copy(n.prop.Read());
                if (includeUnchanged || !Same(n.original, n.lastAfter)) res.Add(n);
            }
            return res;
        }

        public static string Revert(Net n)
        {
            if (n == null || !n.prop.Alive) return "gone";
            Before(n.prop, "revert", "revert " + n.prop.Label);
            n.prop.Write(n.original);
            Flush();
            return "reverted " + n.prop.Label + " to " + Short(n.original);
        }

        public static string RevertAll()
        {
            var l = NetChanges();
            if (l.Count == 0) return "nothing to revert";
            Begin("revert all (" + l.Count + ")", "revert");
            try { for (int i = l.Count - 1; i >= 0; i--) { Before(l[i].prop, "revert"); l[i].prop.Write(l[i].original); } }
            finally { End(); }
            return "reverted " + l.Count + " change(s)";
        }

        public static void Clear() { Flush(); history.Clear(); folded.Clear(); foldedOrder.Clear(); cursor = 0; Version++; }

        // ---------------------------------------------------------------- values
        static object Copy(object v) { return v; }   // recorded values are value types, strings or object references

        public static bool Same(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null || (a is UObj && (UObj)a == null && b == null) || (b is UObj && (UObj)b == null && a == null);
            if (a is Vector3 && b is Vector3) return ((Vector3)a - (Vector3)b).sqrMagnitude < 1e-10f;
            if (a is float && b is float) return Mathf.Abs((float)a - (float)b) < 1e-6f;
            return a.Equals(b);
        }

        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public static string Short(object v)
        {
            if (v == null) return "null";
            if (v is bool) return (bool)v ? "on" : "off";
            if (v is Vector3) { var x = (Vector3)v; return "(" + x.x.ToString("0.###", IC) + ", " + x.y.ToString("0.###", IC) + ", " + x.z.ToString("0.###", IC) + ")"; }
            if (v is float) return ((float)v).ToString("0.####", IC);
            if (v is double) return ((double)v).ToString("0.####", IC);
            var uo = v as UObj; if (uo != null) return uo.name + " (" + uo.GetType().Name + ")";
            if (v is UObj) return "null (destroyed)";
            // Unity's own ToString of these uses the Windows language and one decimal
            if (v is Vector2 || v is Vector4 || v is Quaternion || v is Color || v is Rect) return Fmt.Any(v);
            if (v is Bounds) { var b = (Bounds)v; return "center " + Fmt.V3(b.center, "0.###") + " size " + Fmt.V3(b.size, "0.###"); }
            return Convert.ToString(v, IC);
        }

        // ---------------------------------------------------------------- text
        public static string Dump(int n)
        {
            Flush();
            var sb = new StringBuilder();
            sb.Append("HISTORY ").Append(history.Count).Append(" entries, cursor ").Append(cursor).Append(cursor < history.Count ? " (" + (history.Count - cursor) + " can be redone)" : "").Append('\n');
            for (int i = history.Count - 1; i >= 0 && i >= history.Count - n; i--)
            {
                var e = history[i];
                sb.Append(i >= cursor ? "  (undone) " : "  ").Append('#').Append(e.id).Append(' ').Append(e.IsAction ? "[action] " : "").Append(e.Describe()).Append("   <").Append(e.source).Append(">\n");
            }
            var net = NetChanges();
            sb.Append("NET CHANGES ").Append(net.Count).Append('\n');
            foreach (var x in net) sb.Append("  ").Append(x.prop.Label).Append(": ").Append(Short(x.original)).Append(" -> ").Append(Short(x.current)).Append('\n');
            return sb.ToString();
        }

        // the same as one JSON line ("history <n> json"): entries newest first, then the net changes with their index for "revert <n>"
        public static string DumpJson(int n)
        {
            Flush();
            var o = new Json.Obj();
            o["ok"] = true; o["entries"] = history.Count; o["cursor"] = cursor; o["canUndo"] = cursor > 0; o["canRedo"] = cursor < history.Count;
            var l = new List<object>();
            for (int i = history.Count - 1; i >= 0 && i >= history.Count - n; i--)
            {
                var e = history[i];
                var j = new Json.Obj();
                j["id"] = e.id; j["undone"] = i >= cursor; j["action"] = e.IsAction; j["text"] = e.Describe(); j["source"] = e.source; j["t"] = e.t; j["changes"] = e.ops.Count;
                if (e.action != null) { j["kind"] = e.action.action; j["target"] = e.action.target; if (e.action.fsm != null) j["fsm"] = e.action.fsm; j["arg"] = e.action.arg; }
                l.Add(j);
            }
            o["history"] = l;
            var net = NetChanges();
            var nl = new List<object>();
            for (int i = 0; i < net.Count; i++)
            {
                var x = net[i];
                var j = new Json.Obj();
                j["index"] = i; j["label"] = x.prop.Label; j["property"] = PropName(x.prop.kind);
                if (x.prop.kind == PropKind.Field || x.prop.kind == PropKind.Audio) j["field"] = x.prop.field;
                j["original"] = Short(x.original); j["current"] = Short(x.current); j["firstEntry"] = x.firstEntry; j["lastEntry"] = x.lastEntry;
                nl.Add(j);
            }
            o["net"] = nl;
            return JsonLine.Write(o);
        }

        // bridge: history [n] [json] | undo [n | to <entry id>] | redo [n] | revert all [dry] | revert <n> [dry] | revert <label text> [dry]
        public static string Command(List<string> a)
        {
            string c = a.Count > 0 ? a[0].ToLowerInvariant() : "history";
            bool json = a.Count > 1 && a[a.Count - 1] == "json";
            bool dry = a.Count > 1 && a[a.Count - 1] == "dry";
            int argc = json || dry ? a.Count - 1 : a.Count;
            int k;
            if (c == "undo")
            {
                if (argc > 2 && a[1] == "to") return Fmt.TryI(a[2].TrimStart('#'), out k) ? UndoTo(k) : "usage: undo [n] | undo to <entry id>";
                if (argc > 1) return Fmt.TryI(a[1], out k) && k > 0 ? Undo(k) : "usage: undo [n] | undo to <entry id>";
                return Undo();
            }
            if (c == "redo")
            {
                if (argc > 1) return Fmt.TryI(a[1], out k) && k > 0 ? Redo(k) : "usage: redo [n]";
                return Redo();
            }
            if (c == "revert")
            {
                if (argc > 1 && a[1] == "all") { var all = NetChanges(); return dry ? RevertPreview(all) : RevertAll(); }
                var net = NetChanges();
                if (argc > 1 && Fmt.TryI(a[1], out k))
                {
                    if (k < 0 || k >= net.Count) return "usage: revert all | revert <0.." + (net.Count - 1) + "> | revert <label text>   (add dry to list only)";
                    return dry ? RevertPreview(new List<Net> { net[k] }) : Revert(net[k]);
                }
                if (argc > 1)
                {
                    string text = string.Join(" ", a.GetRange(1, argc - 1).ToArray());
                    var hits = FindNet(text);
                    if (hits.Count == 0) return "not found: no net change whose label contains '" + text + "'";
                    return dry ? RevertPreview(hits) : Revert(hits, "'" + text + "'");
                }
                return "usage: revert all | revert <0.." + (net.Count - 1) + "> | revert <label text>   (add dry to list only)";
            }
            int n = 30;
            if (argc > 1 && !Fmt.TryI(a[1], out n)) return "usage: history [n] [json]";
            return json ? DumpJson(n) : Dump(n);
        }
    }
}
