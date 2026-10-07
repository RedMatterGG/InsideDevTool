using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    // Reflection helpers shared by the Inspector (Normal / Advanced / Raw) and the bridge.
    // "Nested" values are plain serializable game classes/structs (e.g. AudioCommands.BoyVoiceConfig inside a
    // PlayMaker action) that were previously printed as their type name only.
    public static class ValueDump
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();
        static readonly Dictionary<Type, FieldInfo[]> rawFieldCache = new Dictionary<Type, FieldInfo[]>();

        public static bool IsLeaf(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Vector2) || t == typeof(Vector3) ||
                   t == typeof(Vector4) || t == typeof(Quaternion) || t == typeof(Color) || t == typeof(Color32) || t == typeof(Rect) || t == typeof(Bounds) ||
                   t == typeof(LayerMask) || t == typeof(AnimationCurve) || t == typeof(Gradient);
        }

        // plain game data object worth expanding (not a Unity object, collection, delegate or FSM variable)
        public static bool IsNested(object v)
        {
            if (v == null) return false;
            var t = v.GetType();
            if (IsLeaf(t) || v is UObj || v is Delegate || v is ICollection || v is NamedVariable || v is Type || v is MemberInfo) return false;
            if (t.IsPointer || t == typeof(IntPtr)) return false;
            string asm = t.Assembly.GetName().Name;
            return asm.StartsWith("Assembly-CSharp") || asm == "PlayMaker";
        }

        // fields shown for a type. raw=false: skips compiler-generated backing fields.
        public static FieldInfo[] Fields(Type type, bool raw)
        {
            var cache = raw ? rawFieldCache : fieldCache;
            FieldInfo[] res;
            if (cache.TryGetValue(type, out res)) return res;
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(object) && t != typeof(MonoBehaviour) && t != typeof(Behaviour) && t != typeof(Component) && t != typeof(UObj) && t != typeof(ValueType); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!raw && f.Name.IndexOf('<') >= 0) continue;
                    list.Add(f);
                }
            res = list.ToArray();
            cache[type] = res;
            return res;
        }

        // Normal mode: public or [SerializeField] fields only (what a designer would have set)
        public static bool IsSerialized(FieldInfo f)
        {
            if (f.IsNotSerialized || f.IsStatic) return false;
            if (f.IsPublic) return true;
            return f.GetCustomAttributes(typeof(SerializeField), true).Length > 0;
        }

        public static string Short(object v, int max = 120)
        {
            if (v == null) return "null";
            var uo = v as UObj;
            if (!ReferenceEquals(uo, null)) return uo == null ? "null (destroyed)" : uo.name + " (" + uo.GetType().Name + ")";
            var nv = v as NamedVariable;
            if (nv != null) { try { return (nv.UseVariable ? "var " + nv.Name + " = " : "") + Short(nv.RawValue, max); } catch { return "fsm var"; } }
            if (v is Vector3) { var p = (Vector3)v; return string.Format(IC, "({0:0.###}, {1:0.###}, {2:0.###})", p.x, p.y, p.z); }
            if (v is float) return ((float)v).ToString("0.####", IC);
            var col = v as ICollection;
            if (col != null && !(v is string))
            {
                var sb = new StringBuilder(v.GetType().Name + "[" + col.Count + "]");
                int i = 0;
                foreach (var x in col) { if (i++ >= 5) { sb.Append(" …"); break; } sb.Append(i == 1 ? " { " : ", ").Append(Short(x, 40)); }
                if (i > 0 && i <= 5) sb.Append(" }");
                return Cut(sb.ToString(), max);
            }
            string s; try { s = Convert.ToString(v, IC); } catch { s = "?"; }
            return Cut(s, max);
        }

        static string Cut(string s, int max) { return s == null ? "" : s.Length > max ? s.Substring(0, max) + "…" : s; }

        // one-line nested form: {a=1, b={c=2}}  (null / default-false members are skipped to keep it short)
        public static string Compact(object v, int depth = 3)
        {
            if (!IsNested(v)) return Short(v);
            if (depth <= 0) return "{…}";
            var sb = new StringBuilder("{");
            int n = 0;
            foreach (var f in Fields(v.GetType(), false))
            {
                object x; try { x = f.GetValue(v); } catch { continue; }
                if (x == null || (x is bool && !(bool)x)) continue;
                if (n++ > 0) sb.Append(", ");
                sb.Append(f.Name).Append('=').Append(IsNested(x) ? Compact(x, depth - 1) : Short(x, 60));
                if (sb.Length > 600) { sb.Append(" …"); break; }
            }
            return sb.Append('}').ToString();
        }

        // multi-line tree (bridge "inspect <obj> deep")
        public static void Tree(StringBuilder sb, object v, string indent, int depth, bool raw)
        {
            if (v == null || depth <= 0) return;
            foreach (var f in Fields(v.GetType(), raw))
            {
                object x; try { x = f.GetValue(v); } catch (Exception e) { sb.Append(indent).Append(f.Name).Append(" = <").Append(e.GetType().Name).Append(">\n"); continue; }
                if (IsNested(x))
                {
                    sb.Append(indent).Append(f.Name).Append(" : ").Append(x.GetType().Name).Append('\n');
                    Tree(sb, x, indent + "  ", depth - 1, raw);
                }
                else sb.Append(indent).Append(f.Name).Append(" = ").Append(Short(x)).Append('\n');
            }
        }
    }
}
