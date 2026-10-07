using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace InsideDev
{
    // Live editing of PlayMaker action settings (e.g. a Wait's time, RandomFloat min/max, PlayAnimation's clip name,
    // which event an action sends) and switching single actions on/off.
    //   editable: FsmFloat / FsmInt / FsmBool / FsmString (set as a constant - a field that read an FSM variable gets
    //             its own constant instead, the variable itself is untouched), FsmEvent, float / int / bool / string,
    //             enums, and the action's own on/off ("(enabled)").
    // Actions read most settings when their state is entered, so a change shows the next time the state runs.
    // Originals are kept for "reset edits". Recorded in History as action "fsmAction",
    // arg "<state>|<action index>|<field>|<value>", so the Mods tab can keep it.
    public static class ActionEdit
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public const string EnabledField = "(enabled)";
        sealed class Orig { public FsmStateAction action; public string field; public object value; public string label; }
        static readonly Dictionary<int, List<Orig>> origs = new Dictionary<int, List<Orig>>();

        public static bool IsEditable(Type t)
        {
            return t == typeof(FsmFloat) || t == typeof(FsmInt) || t == typeof(FsmBool) || t == typeof(FsmString) || t == typeof(FsmEvent)
                || t == typeof(float) || t == typeof(int) || t == typeof(bool) || t == typeof(string) || t.IsEnum;
        }

        // the settings shown for an action (designer-visible fields)
        public static List<FieldInfo> Fields(FsmStateAction a)
        {
            var l = new List<FieldInfo>();
            foreach (var f in ValueDump.Fields(a.GetType(), false))
                if (f.DeclaringType != typeof(FsmStateAction) && ValueDump.IsSerialized(f)) l.Add(f);
            return l;
        }

        public static string Show(object v)
        {
            if (v == null) return "(none)";
            var ev = v as FsmEvent; if (ev != null) return ev.Name;
            var nv = v as NamedVariable;
            if (nv != null)
            {
                object raw = null; try { raw = nv.RawValue; } catch { }
                string r = raw is float ? ((float)raw).ToString("0.###", IC) : raw == null ? "" : raw is UnityEngine.Object ? ((UnityEngine.Object)raw != null ? ((UnityEngine.Object)raw).name : "null") : raw.ToString();
                return nv.UseVariable && !string.IsNullOrEmpty(nv.Name) ? "{" + nv.Name + "} = " + r : r;
            }
            if (v is float) return ((float)v).ToString("0.###", IC);
            string s = Remote.ActionParam(v); return s ?? v.GetType().Name;
        }

        // text to pre-fill the editor with (the constant value, without the variable name)
        public static string EditText(object v)
        {
            if (v == null) return "";
            var ev = v as FsmEvent; if (ev != null) return ev.Name;
            var nv = v as NamedVariable;
            if (nv != null) { object raw = null; try { raw = nv.RawValue; } catch { } return raw is float ? ((float)raw).ToString("0.###", IC) : raw == null ? "" : raw.ToString(); }
            if (v is float) return ((float)v).ToString("0.###", IC);
            return v.ToString();
        }

        static FsmState State(PlayMakerFSM f, string name) { if (f.FsmStates != null) foreach (var s in f.FsmStates) if (s != null && s.Name == name) return s; return null; }

        public static bool IsEdited(PlayMakerFSM f) { List<Orig> l; return f != null && origs.TryGetValue(f.GetInstanceID(), out l) && l.Count > 0; }
        public static bool IsEdited(PlayMakerFSM f, FsmStateAction a, string field)
        {
            List<Orig> l; if (f == null || !origs.TryGetValue(f.GetInstanceID(), out l)) return false;
            foreach (var o in l) if (o.action == a && o.field == field) return true;
            return false;
        }

        public static string Set(PlayMakerFSM f, string state, int index, string field, string value, string source)
        {
            if (f == null) return "no FSM";
            var st = State(f, state); if (st == null) return "no state '" + state + "'";
            if (st.Actions == null || index < 0 || index >= st.Actions.Length || st.Actions[index] == null) return "no action #" + index + " in [" + state + "]";
            var a = st.Actions[index];
            object newVal; object oldVal; FieldInfo fi = null;
            if (field == EnabledField)
            {
                bool b; if (!ParseBool(value, out b)) return "not on/off: " + value;
                oldVal = a.Enabled; newVal = b;
            }
            else
            {
                fi = a.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi == null) return a.GetType().Name + " has no setting '" + field + "'";
                if (!IsEditable(fi.FieldType)) return field + " (" + fi.FieldType.Name + ") can't be edited here";
                string err = Make(f, fi.FieldType, value, out newVal);
                if (err != null) return field + ": " + err;
                oldVal = fi.GetValue(a);
            }
            // remember the very first value for reset
            List<Orig> l; if (!origs.TryGetValue(f.GetInstanceID(), out l)) origs[f.GetInstanceID()] = l = new List<Orig>();
            bool known = false; foreach (var o in l) if (o.action == a && o.field == field) known = true;
            if (!known) l.Add(new Orig { action = a, field = field, value = oldVal, label = "[" + state + "] " + a.GetType().Name + "." + field });
            if (fi == null) a.Enabled = (bool)newVal; else fi.SetValue(a, newVal);
            string msg = f.gameObject.name + "/" + f.FsmName + " [" + state + "] " + a.GetType().Name + "." + field + " = " + Show(newVal) + "  (was " + Show(oldVal) + ")";
            DevLog.Write("[fsm] " + msg);
            ChangeRecorder.Action(f.gameObject, "fsmAction", f.FsmName, state + "|" + index + "|" + field + "|" + value, source);
            return msg;
        }

        static bool ParseBool(string s, out bool b)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            b = s == "true" || s == "1" || s == "on" || s == "yes";
            return b || s == "false" || s == "0" || s == "off" || s == "no";
        }

        static string Make(PlayMakerFSM f, Type t, string s, out object v)
        {
            v = null; s = s ?? "";
            float fl; int i; bool b;
            if (t == typeof(FsmFloat)) { if (!float.TryParse(s, NumberStyles.Float, IC, out fl)) return "not a number"; v = new FsmFloat { Value = fl }; return null; }
            if (t == typeof(FsmInt)) { if (!int.TryParse(s, NumberStyles.Integer, IC, out i)) return "not a whole number"; v = new FsmInt { Value = i }; return null; }
            if (t == typeof(FsmBool)) { if (!ParseBool(s, out b)) return "not true/false"; v = new FsmBool { Value = b }; return null; }
            if (t == typeof(FsmString)) { v = new FsmString { Value = s }; return null; }
            if (t == typeof(FsmEvent)) { v = s.Trim().Length == 0 ? null : FsmEdit.EventFor(f, s.Trim()); return null; }
            if (t == typeof(float)) { if (!float.TryParse(s, NumberStyles.Float, IC, out fl)) return "not a number"; v = fl; return null; }
            if (t == typeof(int)) { if (!int.TryParse(s, NumberStyles.Integer, IC, out i)) return "not a whole number"; v = i; return null; }
            if (t == typeof(bool)) { if (!ParseBool(s, out b)) return "not true/false"; v = b; return null; }
            if (t == typeof(string)) { v = s; return null; }
            if (t.IsEnum) { try { v = Enum.Parse(t, s.Trim(), true); return null; } catch { return "one of: " + string.Join(", ", Enum.GetNames(t)); } }
            return "unsupported type " + t.Name;
        }

        // undo one setting
        public static string Revert(PlayMakerFSM f, FsmStateAction a, string field)
        {
            List<Orig> l; if (f == null || !origs.TryGetValue(f.GetInstanceID(), out l)) return "not edited";
            for (int k = l.Count - 1; k >= 0; k--)
            {
                var o = l[k]; if (o.action != a || o.field != field) continue;
                Restore(o); l.RemoveAt(k);
                DevLog.Write("[fsm] " + f.gameObject.name + "/" + f.FsmName + " " + o.label + " back to " + Show(o.value));
                return o.label + " back to " + Show(o.value);
            }
            return "not edited";
        }

        static void Restore(Orig o)
        {
            if (o.field == EnabledField) { o.action.Enabled = (bool)o.value; return; }
            var fi = o.action.GetType().GetField(o.field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi != null) fi.SetValue(o.action, o.value);
        }

        public static string Reset(PlayMakerFSM f)
        {
            List<Orig> l; if (f == null || !origs.TryGetValue(f.GetInstanceID(), out l) || l.Count == 0) return "no action edits";
            int n = l.Count;
            for (int k = l.Count - 1; k >= 0; k--) Restore(l[k]);
            l.Clear();
            DevLog.Write("[fsm] " + f.gameObject.name + "/" + f.FsmName + ": " + n + " action setting(s) back to the original");
            return n + " action setting(s) back to the original";
        }

        // mod / bridge form: "<state>|<action index>|<field>|<value>"
        public static string Apply(PlayMakerFSM f, string arg, string source)
        {
            var p = (arg ?? "").Split(new[] { '|' }, 4);
            if (p.Length < 4) return "bad action edit '" + arg + "' (want state|index|field|value)";
            int idx; if (!int.TryParse(p[1], out idx)) return "bad action index " + p[1];
            return Set(f, p[0], idx, p[2], p[3], source);
        }

        public static string Label(string arg)
        {
            var p = (arg ?? "").Split(new[] { '|' }, 4);
            return p.Length < 4 ? arg : "[" + p[0] + "] action #" + p[1] + " " + p[2] + " = " + p[3];
        }
    }
}
