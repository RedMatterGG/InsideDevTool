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
        public enum PropKind { Active, Enabled, Position, LocalPosition, LocalEuler, LocalScale, Field, Audio }   // Audio: global audio rule (AudioRules), field = rule key

        // ---------------------------------------------------------------- property model
        public sealed class Prop
        {
            public GameObject go; public Component comp; public PropKind kind; public string field;   // field: dotted path inside comp
            public int compIndex; public string compType;
            public string key;

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

            public bool Alive { get { return go != null && (kind == PropKind.Active || kind == PropKind.Audio || kind == PropKind.Position || kind == PropKind.LocalPosition || kind == PropKind.LocalEuler || kind == PropKind.LocalScale || comp != null); } }

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
                        case PropKind.Field:
                            {
                                object owner; var f = Resolve(out owner);
                                if (f == null || owner == null || owner.GetType().IsValueType) return false;
                                f.SetValue(owner, v);
                                OnFieldWritten(comp, f);
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
                case PropKind.Audio: return "audio";
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

        static void Push(Entry e)
        {
            if (cursor < history.Count) history.RemoveRange(cursor, history.Count - cursor);
            e.id = nextId++;
            history.Add(e);
            if (history.Count > MaxEntries) history.RemoveAt(0);
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

        // net effect of the applied history: property -> original value and the current value
        public sealed class Net { public Prop prop; public object original, current, lastAfter; public int firstEntry, lastEntry; }

        public static List<Net> NetChanges(bool includeUnchanged = false)
        {
            Flush();
            var map = new Dictionary<string, Net>();
            var order = new List<Net>();
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

        public static void Clear() { Flush(); history.Clear(); cursor = 0; Version++; }

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
            var uo = v as UObj; if (uo != null) return uo.name + " (" + uo.GetType().Name + ")";
            if (v is UObj) return "null (destroyed)";
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
    }
}
