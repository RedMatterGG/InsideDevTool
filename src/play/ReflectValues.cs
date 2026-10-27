using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // ReflectValues.cs (owner WORLD_B): the engine-free half of the research tools, offline tested (qa/mcp/WORLD_B).
    // Verified-write verdicts, JSON -> typed conversion (strict: no clamping, integers must be whole, enums by name),
    // value rendering (depth, item and character caps), tolerant equality, dotted member paths with value-type
    // write-back, overload scoring, signatures, type lookup and the safety deny list (Steam, OS, master volume; saves only
    // with EditorState play.save_guard on).
    public static class ReflectValues
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        public const int MaxChars = 2000;
        public const int MaxItems = 20;

        // ---------------------------------------------------------------- verdicts
        public static string Verdict(bool readAfter, bool sameAfter, bool checkLater, bool readAfterK, bool sameAfterK)
        {
            if (!readAfter) return "unreadable";
            if (!sameAfter) return "not_applied";
            if (!checkLater) return "confirmed";
            if (!readAfterK) return "unreadable";
            return sameAfterK ? "confirmed" : "reverted_by_game";
        }

        static int Rank(string v)
        {
            switch (v)
            {
                case "not_applied": return 3;
                case "reverted_by_game": return 2;
                case "unreadable": return 1;
                default: return 0;
            }
        }

        // the worst of several field verdicts (not_applied > reverted_by_game > unreadable > confirmed); null when empty
        public static string Worst(IEnumerable<string> verdicts)
        {
            string w = null;
            foreach (var v in verdicts) if (v != null && (w == null || Rank(v) > Rank(w))) w = v;
            return w;
        }

        // ---------------------------------------------------------------- equality
        public static bool Same(object a, object b)
        {
            if (a == null || b == null) return IsNullish(a) && IsNullish(b);
            if (IsNumber(a) && IsNumber(b))
            {
                double x = System.Convert.ToDouble(a, IC), y = System.Convert.ToDouble(b, IC);
                if (double.IsNaN(x) || double.IsNaN(y)) return double.IsNaN(x) && double.IsNaN(y);
                return Math.Abs(x - y) <= 1e-4 * Math.Max(1.0, Math.Max(Math.Abs(x), Math.Abs(y)));
            }
            float[] fa = Floats(a), fb = Floats(b);
            if (fa != null && fb != null)
            {
                if (fa.Length != fb.Length) return false;
                for (int i = 0; i < fa.Length; i++) if (Math.Abs(fa[i] - fb[i]) > 1e-3f * Math.Max(1f, Math.Abs(fa[i]))) return false;
                return true;
            }
            if (a is UnityEngine.Object || b is UnityEngine.Object) return ReferenceEquals(a, b);
            if (a is string || b is string) return string.Equals(a as string, b as string, StringComparison.Ordinal);
            var la = a as IList;
            var lb = b as IList;
            if (la != null && lb != null)
            {
                if (la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++) if (!Same(la[i], lb[i])) return false;
                return true;
            }
            return a.Equals(b);
        }

        static bool IsNullish(object o)
        {
            if (o == null) return true;
            var u = o as UnityEngine.Object;
            return u != null && !Alive(u);
        }

        // destroyed Unity objects compare equal to null through their == operator
        public static bool Alive(UnityEngine.Object o) { return o != null; }

        public static bool IsNumber(object o)
        {
            return o is double || o is float || o is int || o is long || o is short || o is byte || o is uint || o is ulong || o is ushort || o is sbyte || o is decimal;
        }

        public static float[] Floats(object o)
        {
            if (o is Vector3) { var v = (Vector3)o; return new[] { v.x, v.y, v.z }; }
            if (o is Vector2) { var v = (Vector2)o; return new[] { v.x, v.y }; }
            if (o is Vector4) { var v = (Vector4)o; return new[] { v.x, v.y, v.z, v.w }; }
            if (o is Quaternion) { var q = (Quaternion)o; return new[] { q.x, q.y, q.z, q.w }; }
            if (o is Color) { var c = (Color)o; return new[] { c.r, c.g, c.b, c.a }; }
            if (o is Color32) { var c = (Color32)o; return new float[] { c.r, c.g, c.b, c.a }; }
            if (o is Rect) { var r = (Rect)o; return new[] { r.x, r.y, r.width, r.height }; }
            return null;
        }

        // ---------------------------------------------------------------- JSON -> typed
        // refResolver(spec, type): a UnityEngine.Object of type for an id / path (main thread in the game); throws on failure
        public static object Convert(object json, Type t, Func<string, Type, object> refResolver, out string error)
        {
            error = null;
            if (t == null) { error = "no type"; return null; }
            if (t == typeof(object)) return json;
            var under = Nullable.GetUnderlyingType(t);
            if (under != null)
            {
                if (json == null) return null;
                t = under;
            }
            if (json == null)
            {
                if (t.IsValueType) { error = "null is not a " + FriendlyName(t); return null; }
                return null;
            }
            if (t == typeof(string))
            {
                if (json is string) return json;
                error = "expected a string for " + FriendlyName(t);
                return null;
            }
            if (t == typeof(bool))
            {
                if (json is bool) return json;
                error = "expected true or false";
                return null;
            }
            if (t.IsEnum) return ToEnum(json, t, out error);
            if (t == typeof(char))
            {
                var s = json as string;
                if (s != null && s.Length == 1) return s[0];
                error = "expected a one-character string";
                return null;
            }
            if (IsNumericType(t)) return ToNumber(json, t, out error);
            if (t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4) || t == typeof(Quaternion) || t == typeof(Color) || t == typeof(Color32) || t == typeof(Rect))
                return ToStruct(json, t, out error);
            if (t == typeof(LayerMask))
            {
                var n = ToNumber(json, typeof(int), out error);
                if (error != null) return null;
                LayerMask m = (int)n;
                return m;
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(t))
            {
                var spec = json as string;
                if (spec == null) { error = "expected an object id or path (string) for " + FriendlyName(t); return null; }
                if (refResolver == null) { error = "object references cannot be resolved here"; return null; }
                return refResolver(spec, t);
            }
            var list = json as IList;
            if (list != null && t.IsArray && t.GetArrayRank() == 1)
            {
                var et = t.GetElementType();
                var arr = Array.CreateInstance(et, list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    var v = Convert(list[i], et, refResolver, out error);
                    if (error != null) { error = "[" + i + "]: " + error; return null; }
                    arr.SetValue(v, i);
                }
                return arr;
            }
            if (list != null && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var et = t.GetGenericArguments()[0];
                var l = (IList)Activator.CreateInstance(t);
                for (int i = 0; i < list.Count; i++)
                {
                    var v = Convert(list[i], et, refResolver, out error);
                    if (error != null) { error = "[" + i + "]: " + error; return null; }
                    l.Add(v);
                }
                return l;
            }
            if (t.IsInstanceOfType(json)) return json;
            error = "cannot convert " + JsonKind(json) + " to " + FriendlyName(t);
            return null;
        }

        public static string JsonKind(object json)
        {
            if (json == null) return "null";
            if (json is string) return "a string";
            if (json is bool) return "a boolean";
            if (json is double) return "a number";
            if (json is IList) return "an array";
            if (json is Json.Obj) return "an object";
            return json.GetType().Name;
        }

        public static bool IsNumericType(Type t)
        {
            return t == typeof(int) || t == typeof(float) || t == typeof(double) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
                || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte) || t == typeof(decimal);
        }

        static object ToNumber(object json, Type t, out string error)
        {
            error = null;
            if (!(json is double)) { error = "expected a number for " + FriendlyName(t); return null; }
            double d = (double)json;
            if (double.IsNaN(d) || double.IsInfinity(d)) { error = "not a finite number"; return null; }
            if (t == typeof(double)) return d;
            if (t == typeof(float))
            {
                if (Math.Abs(d) > float.MaxValue) { error = d.ToString("R", IC) + " is out of range for float"; return null; }
                return (float)d;
            }
            if (t == typeof(decimal)) return (decimal)d;
            if (Math.Floor(d) != d) { error = d.ToString("R", IC) + " is not a whole number (" + FriendlyName(t) + ")"; return null; }
            double min, max;
            IntRange(t, out min, out max);
            if (d < min || d > max) { error = d.ToString("R", IC) + " is out of range for " + FriendlyName(t) + " (" + min.ToString("R", IC) + " .. " + max.ToString("R", IC) + ")"; return null; }
            if (t == typeof(int)) return (int)d;
            if (t == typeof(long)) return (long)d;
            if (t == typeof(short)) return (short)d;
            if (t == typeof(byte)) return (byte)d;
            if (t == typeof(uint)) return (uint)d;
            if (t == typeof(ulong)) return (ulong)d;
            if (t == typeof(ushort)) return (ushort)d;
            return (sbyte)d;
        }

        static void IntRange(Type t, out double min, out double max)
        {
            if (t == typeof(int)) { min = int.MinValue; max = int.MaxValue; return; }
            if (t == typeof(long)) { min = -9007199254740992.0; max = 9007199254740992.0; return; }
            if (t == typeof(short)) { min = short.MinValue; max = short.MaxValue; return; }
            if (t == typeof(byte)) { min = 0; max = byte.MaxValue; return; }
            if (t == typeof(uint)) { min = 0; max = uint.MaxValue; return; }
            if (t == typeof(ulong)) { min = 0; max = 9007199254740992.0; return; }
            if (t == typeof(ushort)) { min = 0; max = ushort.MaxValue; return; }
            min = sbyte.MinValue; max = sbyte.MaxValue;
        }

        static object ToEnum(object json, Type t, out string error)
        {
            error = null;
            var s = json as string;
            if (s != null)
            {
                foreach (var part in s.Split(','))
                {
                    string p = part.Trim();
                    bool found = false;
                    foreach (var n in Enum.GetNames(t)) if (string.Equals(n, p, StringComparison.OrdinalIgnoreCase)) found = true;
                    if (!found)
                    {
                        var names = Enum.GetNames(t);
                        error = "'" + p + "' is not a " + t.Name + " (" + string.Join(", ", Take(names, 24)) + ")";
                        return null;
                    }
                }
                return Enum.Parse(t, s, true);
            }
            if (json is double)
            {
                double d = (double)json;
                if (Math.Floor(d) != d) { error = "enum value must be a whole number"; return null; }
                return Enum.ToObject(t, (long)d);
            }
            error = "expected an enum name (string) for " + t.Name;
            return null;
        }

        static string[] Take(string[] a, int n)
        {
            if (a.Length <= n) return a;
            var r = new string[n + 1];
            Array.Copy(a, r, n);
            r[n] = "...";
            return r;
        }

        static object ToStruct(object json, Type t, out string error)
        {
            error = null;
            var f = new List<double>();
            var list = json as IList;
            var obj = json as Json.Obj;
            if (list != null)
            {
                foreach (var x in list)
                {
                    if (!(x is double)) { error = "expected numbers for " + t.Name; return null; }
                    f.Add((double)x);
                }
            }
            else if (obj != null)
            {
                string[] keys = t == typeof(Color) || t == typeof(Color32) ? new[] { "r", "g", "b", "a" } : t == typeof(Rect) ? new[] { "x", "y", "width", "height" } : new[] { "x", "y", "z", "w" };
                foreach (var k in keys)
                {
                    if (!obj.ContainsKey(k)) break;
                    if (!(obj[k] is double)) { error = k + ": expected a number"; return null; }
                    f.Add((double)obj[k]);
                }
            }
            else { error = "expected an array of numbers for " + t.Name; return null; }
            int n = f.Count;
            if (t == typeof(Vector2)) { if (n != 2) return Need(t, "2", out error); return new Vector2((float)f[0], (float)f[1]); }
            if (t == typeof(Vector3)) { if (n != 3) return Need(t, "3", out error); return new Vector3((float)f[0], (float)f[1], (float)f[2]); }
            if (t == typeof(Vector4)) { if (n != 4) return Need(t, "4", out error); return new Vector4((float)f[0], (float)f[1], (float)f[2], (float)f[3]); }
            if (t == typeof(Quaternion))
            {
                if (n == 4) return new Quaternion((float)f[0], (float)f[1], (float)f[2], (float)f[3]);
                if (n == 3) return FromEuler((float)f[0], (float)f[1], (float)f[2]);
                return Need(t, "4 (x, y, z, w) or 3 (euler degrees)", out error);
            }
            if (t == typeof(Color))
            {
                if (n == 3) return new Color((float)f[0], (float)f[1], (float)f[2], 1f);
                if (n == 4) return new Color((float)f[0], (float)f[1], (float)f[2], (float)f[3]);
                return Need(t, "3 or 4", out error);
            }
            if (t == typeof(Color32))
            {
                if (n != 3 && n != 4) return Need(t, "3 or 4", out error);
                var b = new byte[4] { 255, 255, 255, 255 };
                for (int i = 0; i < n; i++)
                {
                    if (f[i] < 0 || f[i] > 255 || Math.Floor(f[i]) != f[i]) { error = "Color32 components are whole numbers 0..255"; return null; }
                    b[i] = (byte)f[i];
                }
                return new Color32(b[0], b[1], b[2], b[3]);
            }
            if (n != 4) return Need(t, "4", out error);
            return new Rect((float)f[0], (float)f[1], (float)f[2], (float)f[3]);
        }

        static object Need(Type t, string n, out string error)
        {
            error = FriendlyName(t) + " needs " + n + " numbers";
            return null;
        }

        // Unity's Quaternion.Euler (z, then x, then y), in managed code
        public static Quaternion FromEuler(float xDeg, float yDeg, float zDeg)
        {
            double hx = xDeg * Math.PI / 360.0, hy = yDeg * Math.PI / 360.0, hz = zDeg * Math.PI / 360.0;
            double cx = Math.Cos(hx), sx = Math.Sin(hx), cy = Math.Cos(hy), sy = Math.Sin(hy), cz = Math.Cos(hz), sz = Math.Sin(hz);
            return new Quaternion((float)(cy * sx * cz + sy * cx * sz), (float)(sy * cx * cz - cy * sx * sz), (float)(cy * cx * sz - sy * sx * cz), (float)(cy * cx * cz + sy * sx * sz));
        }

        // Unity's Quaternion.eulerAngles convention, in managed code, each angle in -180 .. 180
        public static Vector3 ToEuler(Quaternion q)
        {
            double x = q.x, y = q.y, z = q.z, w = q.w;
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (n < 1e-9) return Vector3.zero;
            x /= n; y /= n; z /= n; w /= n;
            double sinX = 2.0 * (w * x - y * z);
            if (sinX > 1) sinX = 1;
            if (sinX < -1) sinX = -1;
            double ax = Math.Asin(sinX);
            double ay = Math.Atan2(2.0 * (w * y + x * z), 1.0 - 2.0 * (x * x + y * y));
            double az = Math.Atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (x * x + z * z));
            const double r = 180.0 / Math.PI;
            return new Vector3((float)(ax * r), (float)(ay * r), (float)(az * r));
        }

        // ---------------------------------------------------------------- rendering
        // JSON-ready value: primitives as JSON, vectors as arrays, enums by name, Unity objects through objRender, lists
        // capped at MaxItems, other objects as their fields down to depth; the whole value capped at MaxChars
        public static object Render(object v, int depth, Func<UnityEngine.Object, object> objRender)
        {
            var r = RenderInner(v, depth, objRender);
            var s = Compact(r);
            if (s.Length <= MaxChars) return r;
            return s.Substring(0, MaxChars) + "...(truncated, " + s.Length + " chars)";
        }

        static object RenderInner(object v, int depth, Func<UnityEngine.Object, object> objRender)
        {
            if (v == null) return null;
            if (v is bool || v is string) return Cut(v);
            if (v is double) return v;
            if (v is float) return FloatToDouble((float)v);
            if (IsNumber(v)) return System.Convert.ToDouble(v, IC);
            if (v is char) return v.ToString();
            if (v is Enum) return v.ToString();
            var fl = Floats(v);
            if (fl != null)
            {
                var l = new List<object>();
                foreach (var x in fl) l.Add(FloatToDouble(x));
                return l;
            }
            if (v is UnityEngine.Object) return objRender != null ? objRender((UnityEngine.Object)v) : SafeName((UnityEngine.Object)v);
            if (v is LayerMask) return (double)((LayerMask)v).value;
            if (v is Type) return ((Type)v).FullName;
            if (v is Delegate) { var d = (Delegate)v; return "delegate " + (d.Method != null ? d.Method.Name : "?"); }
            if (v is MemberInfo) return ((MemberInfo)v).Name;
            var dict = v as IDictionary;
            if (dict != null)
            {
                var o = new Json.Obj();
                int n = 0;
                foreach (DictionaryEntry e in dict)
                {
                    if (n++ >= MaxItems) { o["..."] = "+" + (dict.Count - MaxItems) + " more"; break; }
                    o[System.Convert.ToString(e.Key, IC) ?? "null"] = depth > 0 ? RenderInner(e.Value, depth - 1, objRender) : Shallow(e.Value);
                }
                return o;
            }
            var en = v as IEnumerable;
            if (en != null)
            {
                var l = new List<object>();
                int n = 0, total = 0;
                foreach (var x in en)
                {
                    total++;
                    if (n >= MaxItems) continue;
                    l.Add(depth > 0 ? RenderInner(x, depth - 1, objRender) : Shallow(x));
                    n++;
                }
                if (total > n) l.Add("+" + (total - n) + " more");
                return l;
            }
            if (depth <= 0) return Shallow(v);
            var fo = new Json.Obj();
            int count = 0;
            foreach (var f in InstanceFields(v.GetType()))
            {
                if (count++ >= 40) { fo["..."] = "more fields"; break; }
                object fv;
                try { fv = f.GetValue(v); } catch (Exception e) { fv = "unreadable: " + e.GetType().Name; }
                fo[CleanName(f.Name)] = RenderInner(fv, depth - 1, objRender);
            }
            if (fo.Count == 0) return v.ToString();
            return fo;
        }

        static object Shallow(object v)
        {
            if (v == null || v is bool || v is string || IsNumber(v) || v is Enum || Floats(v) != null) return RenderInner(v, 0, null);
            if (v is UnityEngine.Object) return SafeName((UnityEngine.Object)v);
            return v.GetType().Name;
        }

        public static string SafeName(UnityEngine.Object u)
        {
            try { return u != null ? u.name : "null (destroyed)"; }
            catch { return "?"; }
        }

        static object Cut(object v)
        {
            var s = v as string;
            if (s != null && s.Length > MaxChars) return s.Substring(0, MaxChars) + "...";
            return v;
        }

        public static double FloatToDouble(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return 0;
            return double.Parse(f.ToString("R", IC), IC);
        }

        public static string CleanName(string n)
        {
            if (n.StartsWith("<", StringComparison.Ordinal))
            {
                int e = n.IndexOf('>');
                if (e > 1) return n.Substring(1, e - 1);
            }
            return n;
        }

        // instance fields from the type up to (not including) MonoBehaviour / Behaviour / Component / Object / ValueType
        public static List<FieldInfo> InstanceFields(Type t)
        {
            var l = new List<FieldInfo>();
            for (var x = t; x != null && !StopType(x); x = x.BaseType)
                foreach (var f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) l.Add(f);
            return l;
        }

        public static bool StopType(Type x)
        {
            return x == typeof(object) || x == typeof(ValueType) || x == typeof(MonoBehaviour) || x == typeof(Behaviour) || x == typeof(Component) || x == typeof(UnityEngine.Object) || x == typeof(Enum);
        }

        // compact JSON text (for size checks)
        public static string Compact(object v)
        {
            var sb = new StringBuilder();
            WriteCompact(sb, v);
            return sb.ToString();
        }

        static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        static void WriteCompact(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { Quote(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (IsNumber(v)) { sb.Append(System.Convert.ToDouble(v, IC).ToString("R", IC)); return; }
            var o = v as Json.Obj;
            if (o != null)
            {
                sb.Append('{');
                int i = 0;
                foreach (var k in o.order) { if (i++ > 0) sb.Append(','); WriteCompact(sb, k); sb.Append(':'); WriteCompact(sb, o[k]); }
                sb.Append('}');
                return;
            }
            var l = v as IList;
            if (l != null)
            {
                sb.Append('[');
                for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); WriteCompact(sb, l[i]); }
                sb.Append(']');
                return;
            }
            WriteCompact(sb, System.Convert.ToString(v, IC));
        }

        // ---------------------------------------------------------------- members and dotted paths
        public static MemberInfo FindMember(Type t, string name, bool wantStatic, bool allowStaticOnInstance)
        {
            for (var x = t; x != null; x = x.BaseType)
            {
                var f = x.GetField(name, Declared);
                if (f != null && (f.IsStatic == wantStatic || (allowStaticOnInstance && f.IsStatic))) return f;
                var p = x.GetProperty(name, Declared);
                if (p != null && p.GetIndexParameters().Length == 0)
                {
                    var acc = p.GetGetMethod(true) ?? p.GetSetMethod(true);
                    if (acc != null && (acc.IsStatic == wantStatic || (allowStaticOnInstance && acc.IsStatic))) return p;
                }
            }
            return null;
        }

        public static Type MemberType(MemberInfo m)
        {
            var f = m as FieldInfo;
            if (f != null) return f.FieldType;
            var p = m as PropertyInfo;
            return p != null ? p.PropertyType : null;
        }

        public static bool IsStatic(MemberInfo m)
        {
            var f = m as FieldInfo;
            if (f != null) return f.IsStatic;
            var p = m as PropertyInfo;
            if (p == null) return false;
            var acc = p.GetGetMethod(true) ?? p.GetSetMethod(true);
            return acc != null && acc.IsStatic;
        }

        public static object GetMember(MemberInfo m, object owner)
        {
            var f = m as FieldInfo;
            if (f != null) return f.GetValue(f.IsStatic ? null : owner);
            var p = (PropertyInfo)m;
            var g = p.GetGetMethod(true);
            if (g == null) throw new InvalidOperationException(p.Name + " has no getter");
            return g.Invoke(g.IsStatic ? null : owner, null);
        }

        public static void SetMember(MemberInfo m, object owner, object value)
        {
            var f = m as FieldInfo;
            if (f != null)
            {
                if (f.IsLiteral) throw new InvalidOperationException(f.Name + " is a constant");
                f.SetValue(f.IsStatic ? null : owner, value);
                return;
            }
            var p = (PropertyInfo)m;
            var s = p.GetSetMethod(true);
            if (s == null) throw new InvalidOperationException(p.Name + " is read-only (no setter)");
            s.Invoke(s.IsStatic ? null : owner, new[] { value });
        }

        public sealed class PathInfo
        {
            public readonly List<MemberInfo> members = new List<MemberInfo>();
            public Type leafType;
            public string error;
        }

        // root null = statics of rootType; later segments may be static too
        public static PathInfo Resolve(Type rootType, object root, string path)
        {
            var pi = new PathInfo();
            if (string.IsNullOrEmpty(path)) { pi.error = "member is required"; return pi; }
            var parts = path.Split('.');
            Type t = root != null ? root.GetType() : rootType;
            object cur = root;
            for (int i = 0; i < parts.Length; i++)
            {
                var name = parts[i].Trim();
                if (name.Length == 0) { pi.error = "empty segment in '" + path + "'"; return pi; }
                bool wantStatic = cur == null;
                var m = FindMember(t, name, wantStatic, true);
                if (m == null)
                {
                    pi.error = (wantStatic ? "no static field or property '" : "no field or property '") + name + "' on " + t.FullName;
                    return pi;
                }
                pi.members.Add(m);
                var mt = MemberType(m);
                if (i < parts.Length - 1)
                {
                    object next;
                    try { next = GetMember(m, cur); }
                    catch (Exception e) { pi.error = name + ": " + Inner(e).Message; return pi; }
                    if (next == null) { pi.error = name + " is null, cannot read '" + parts[i + 1] + "'"; return pi; }
                    cur = next;
                    t = next.GetType();
                }
                else pi.leafType = mt;
            }
            return pi;
        }

        public static object ReadPath(PathInfo pi, object root)
        {
            object cur = root;
            foreach (var m in pi.members) cur = GetMember(m, cur);
            return cur;
        }

        // writes the leaf and writes value-type owners back up the chain
        public static void WritePath(PathInfo pi, object root, object value)
        {
            int n = pi.members.Count;
            var owners = new object[n];
            owners[0] = root;
            for (int i = 0; i < n - 1; i++) owners[i + 1] = GetMember(pi.members[i], owners[i]);
            SetMember(pi.members[n - 1], owners[n - 1], value);
            for (int i = n - 2; i >= 0; i--)
            {
                var mt = MemberType(pi.members[i]);
                if (mt == null || !mt.IsValueType) break;
                SetMember(pi.members[i], owners[i], owners[i + 1]);
            }
        }

        public static Exception Inner(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }

        // ---------------------------------------------------------------- methods
        public sealed class Pick
        {
            public MethodInfo method;
            public object[] args;
            public string error;
            public readonly List<string> candidates = new List<string>();
        }

        // overload resolution for args (JSON) among methods named name; wantStatic = no instance target
        public static Pick ChooseMethod(Type t, string name, bool wantStatic, List<object> args, Func<string, Type, object> refResolver)
        {
            var pick = new Pick();
            var all = new List<MethodInfo>();
            for (var x = t; x != null; x = x.BaseType)
                foreach (var m in x.GetMethods(Declared))
                    if (m.Name == name && (!wantStatic || m.IsStatic) && !m.ContainsGenericParameters) all.Add(m);
            if (all.Count == 0) { pick.error = "no " + (wantStatic ? "static " : "") + "method '" + name + "' on " + t.FullName; return pick; }
            int best = int.MinValue;
            var bestList = new List<KeyValuePair<MethodInfo, object[]>>();
            var reasons = new List<string>();
            var seen = new HashSet<string>();
            foreach (var m in all)
            {
                string sig = Signature(m);
                if (!seen.Add(sig)) continue;
                pick.candidates.Add(sig);
                string why;
                object[] conv;
                int score = Score(m, args, refResolver, out conv, out why);
                if (why != null) { reasons.Add(sig + ": " + why); continue; }
                if (score > best) { best = score; bestList.Clear(); }
                if (score == best) bestList.Add(new KeyValuePair<MethodInfo, object[]>(m, conv));
            }
            if (bestList.Count == 0)
            {
                pick.error = "no overload of " + name + " takes these " + args.Count + " argument(s): " + string.Join("; ", reasons.ToArray());
                return pick;
            }
            if (bestList.Count > 1)
            {
                var sigs = new List<string>();
                foreach (var kv in bestList) sigs.Add(Signature(kv.Key));
                pick.error = "ambiguous call, several overloads fit equally: " + string.Join("; ", sigs.ToArray());
                return pick;
            }
            pick.method = bestList[0].Key;
            pick.args = bestList[0].Value;
            return pick;
        }

        static int Score(MethodInfo m, List<object> args, Func<string, Type, object> refResolver, out object[] conv, out string why)
        {
            conv = null;
            why = null;
            var ps = m.GetParameters();
            foreach (var p in ps) if (p.ParameterType.IsByRef) { why = "out / ref parameters are not supported"; return 0; }
            if (args.Count > ps.Length) { why = "takes " + ps.Length + " argument(s)"; return 0; }
            for (int i = args.Count; i < ps.Length; i++)
                if (!ps[i].IsOptional) { why = "needs " + Required(ps) + " argument(s)"; return 0; }
            conv = new object[ps.Length];
            int score = 0;
            for (int i = 0; i < ps.Length; i++)
            {
                if (i >= args.Count) { conv[i] = ps[i].DefaultValue; continue; }
                string err;
                var v = Convert(args[i], ps[i].ParameterType, refResolver, out err);
                if (err != null) { why = "argument " + (i + 1) + " (" + ps[i].Name + "): " + err; return 0; }
                conv[i] = v;
                score += Fit(args[i], ps[i].ParameterType);
            }
            score -= ps.Length - args.Count;
            return score;
        }

        static int Required(ParameterInfo[] ps)
        {
            int n = 0;
            foreach (var p in ps) if (!p.IsOptional) n++;
            return n;
        }

        static int Fit(object json, Type t)
        {
            var u = Nullable.GetUnderlyingType(t);
            if (u != null) t = u;
            if (t == typeof(object)) return 0;
            if (json == null) return 1;
            if (json is string) return t == typeof(string) ? 4 : t.IsEnum ? 3 : 2;
            if (json is bool) return t == typeof(bool) ? 4 : 0;
            if (json is double)
            {
                double d = (double)json;
                if (t == typeof(double) || t == typeof(float)) return Math.Floor(d) == d ? 3 : 4;
                if (t == typeof(int) || t == typeof(long)) return 4;
                return t.IsEnum ? 1 : 2;
            }
            if (json is IList) return t == typeof(Vector3) || t == typeof(Vector2) ? 4 : 2;
            return 1;
        }

        public static string Signature(MethodInfo m)
        {
            var sb = new StringBuilder();
            if (m.IsStatic) sb.Append("static ");
            sb.Append(FriendlyName(m.ReturnType)).Append(' ').Append(m.Name).Append('(');
            var ps = m.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(FriendlyName(ps[i].ParameterType)).Append(' ').Append(ps[i].Name);
                if (ps[i].IsOptional) sb.Append(" = ").Append(ps[i].DefaultValue == null ? "null" : System.Convert.ToString(ps[i].DefaultValue, IC));
            }
            return sb.Append(')').ToString();
        }

        public static string FriendlyName(Type t)
        {
            if (t == null) return "?";
            if (t == typeof(void)) return "void";
            if (t == typeof(int)) return "int";
            if (t == typeof(float)) return "float";
            if (t == typeof(double)) return "double";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string";
            if (t == typeof(long)) return "long";
            if (t == typeof(uint)) return "uint";
            if (t == typeof(object)) return "object";
            if (t.IsByRef) return "ref " + FriendlyName(t.GetElementType());
            if (t.IsArray) return FriendlyName(t.GetElementType()) + "[]";
            if (t.IsGenericType)
            {
                var n = t.Name;
                int q = n.IndexOf('`');
                if (q > 0) n = n.Substring(0, q);
                var args = new List<string>();
                foreach (var a in t.GetGenericArguments()) args.Add(FriendlyName(a));
                return n + "<" + string.Join(", ", args.ToArray()) + ">";
            }
            return t.IsNested && t.DeclaringType != null ? FriendlyName(t.DeclaringType) + "." + t.Name : t.Name;
        }

        // ---------------------------------------------------------------- types
        static readonly object typeGate = new object();
        static readonly Dictionary<string, Type[]> typeCache = new Dictionary<string, Type[]>();

        // exact full name, then simple name, then "." / "+" suffix; several distinct matches -> candidates (error)
        public static Type FindType(string name, IEnumerable<Assembly> assemblies, out List<string> candidates)
        {
            candidates = new List<string>();
            if (string.IsNullOrEmpty(name)) return null;
            string key = name.Trim();
            Type[] hits;
            lock (typeGate)
            {
                if (!typeCache.TryGetValue(key, out hits))
                {
                    hits = Search(key, assemblies);
                    if (hits.Length > 0) typeCache[key] = hits;
                }
            }
            if (hits.Length == 1) return hits[0];
            foreach (var h in hits) candidates.Add(h.FullName + " (" + h.Assembly.GetName().Name + ")");
            return null;
        }

        static Type[] Search(string key, IEnumerable<Assembly> assemblies)
        {
            var all = new List<Type>();
            foreach (var asm in assemblies)
            {
                Type[] ts;
                try { ts = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { ts = e.Types; }
                catch { continue; }
                foreach (var t in ts) if (t != null) all.Add(t);
            }
            var exact = new List<Type>();
            foreach (var t in all) if (t.FullName == key || (t.FullName != null && t.FullName.Replace('+', '.') == key)) exact.Add(t);
            if (exact.Count > 0) return Distinct(exact);
            var simple = new List<Type>();
            foreach (var t in all) if (t.Name == key) simple.Add(t);
            if (simple.Count > 0) return Prefer(Distinct(simple));
            var suffix = new List<Type>();
            foreach (var t in all)
            {
                var fn = t.FullName;
                if (fn == null) continue;
                if (fn.EndsWith("." + key, StringComparison.Ordinal) || fn.EndsWith("+" + key, StringComparison.Ordinal) || fn.Replace('+', '.').EndsWith("." + key, StringComparison.Ordinal)) suffix.Add(t);
            }
            return Prefer(Distinct(suffix));
        }

        static Type[] Distinct(List<Type> l)
        {
            var seen = new HashSet<Type>();
            var r = new List<Type>();
            foreach (var t in l) if (seen.Add(t)) r.Add(t);
            return r.ToArray();
        }

        // a game-assembly type wins over same-named types elsewhere
        static Type[] Prefer(Type[] l)
        {
            if (l.Length <= 1) return l;
            var game = new List<Type>();
            foreach (var t in l)
            {
                var an = t.Assembly.GetName().Name;
                if (an == "Assembly-CSharp" || an == "Assembly-CSharp-firstpass") game.Add(t);
            }
            return game.Count == 1 ? game.ToArray() : l;
        }

        // ---------------------------------------------------------------- safety
        static readonly string[] BlockedNamespaces = { "System.IO", "System.Diagnostics", "System.Net", "System.Reflection", "Microsoft.Win32", "System.Runtime", "System.Security", "Steamworks", "Galaxy", "System.Threading" };
        static readonly string[] BlockedSystemTypes = { "Activator", "AppDomain", "Environment", "Type", "Console", "Delegate", "MulticastDelegate", "GC" };
        static readonly string[] StringDispatch = { "SendMessage", "SendMessageUpwards", "BroadcastMessage", "Invoke", "InvokeRepeating", "StartCoroutine", "StartCoroutine_Auto" };
        static readonly string[] SaveTypes = { "Savegame", "SavepointManager", "UserStorage", "UserStoragePlatform", "StorageBase", "PersistentDataManager", "PersistentDataArchive", "GameProgression", "GameProgressionPlatform" };

        // game classes whose code calls User.Achievement.Acquire (a Steam call) or ResetProgression (clears every orb
        // bit): AC/FinalSecretFade.cs:100-106, AC/SecretPod.cs:443-479, SecretPodCeiling.cs:190, SecretPodLarge.cs:281,
        // Secret.cs:366, SecretSuitcase.cs:148, AchiementTrigger.cs:9 (Playdead's spelling), FP/StartNewGameMenuItem.cs:23.
        // The same list is in src/mcp/HostText.cs (dev tools).
        public static readonly string[] SecretTypes = { "FinalSecretFade", "SecretPod", "SecretPodCeiling", "SecretPodLarge", "Secret", "SecretSuitcase", "AchiementTrigger", "AchievementTrigger", "StartNewGameMenuItem" };
        public const string SecretHint = "orb bits: save {op:\"orbs\", confirm:true}; the ending: save {op:\"ending\", kind:\"secret\", confirm:true} (no Steam call)";

        // the first secret type among type and its base types (null = none)
        public static string SecretTypeOf(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (Array.IndexOf(SecretTypes, t.Name) >= 0) return t.Name;
            return null;
        }

        // main thread: "name (Type)" of the first component on go that is a secret type, else null
        public static string SecretOn(GameObject go)
        {
            if (go == null) return null;
            foreach (var mb in go.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                if (SecretTypeOf(mb.GetType()) != null) return go.name + " (" + mb.GetType().Name + ")";
            }
            return null;
        }

        // null when allowed, else the refusal reason; hint gets the alternative
        public static string Refusal(Type type, string member, bool isCall, bool isWrite, object value, IList<object> args, out string hint)
        {
            return Refusal(type, member, isCall, isWrite, value, args, Saves.Guard, out hint);
        }

        // saveGuard = Saves.Guard (play.save_guard): the save writes (GameManager loads, Savepoint Activate / Save, the
        // save classes) are refused only when it is on
        public static string Refusal(Type type, string member, bool isCall, bool isWrite, object value, IList<object> args, bool saveGuard, out string hint)
        {
            hint = null;
            if (type == null) return null;
            string ns = type.Namespace ?? "";
            string tn = type.Name;
            string full = type.FullName ?? tn;
            string m = member ?? "";
            foreach (var b in BlockedNamespaces)
                if (ns == b || ns.StartsWith(b + ".", StringComparison.Ordinal) || full.StartsWith(b + ".", StringComparison.Ordinal))
                {
                    hint = "file, process, network, thread and Steam APIs are not reachable through MCP";
                    return full + " is in a blocked namespace (" + b + ")";
                }
            if (ns == "System" && Array.IndexOf(BlockedSystemTypes, tn) >= 0 && (isCall || isWrite))
            {
                hint = "runtime, process and type-loading APIs are not reachable through MCP";
                return "System." + tn + " is blocked";
            }
            if ((ns == "InsideDev.McpExt" || ns.StartsWith("InsideDev.McpExt.", StringComparison.Ordinal) || ns == "InsideDev.Play") && (isCall || isWrite))
            {
                hint = "the MCP layer is driven through its tools (restore, jobs, time ...)";
                return full + " is part of the MCP layer";
            }
            if (ns == "InsideDev" && (tn == "EditorState" || tn == "Extensions" || tn == "Mods") && (isWrite || (isCall && !ReadOnlyName(m))))
            {
                hint = tn == "EditorState" ? "user settings (play.cheats, mcp.tools ...) are changed by the user, not through MCP" : "loading code is not reachable through MCP";
                return "InsideDev." + tn + "." + m + " is not allowed";
            }
            if (isCall && Array.IndexOf(StringDispatch, m) >= 0)
            {
                hint = "call the target method itself (string dispatch would bypass the safety checks)";
                return m + " dispatches by name";
            }
            string secret = SecretTypeOf(type);
            if (secret != null && (isWrite || (isCall && !ReadOnlyName(m))))
            {
                hint = SecretHint;
                return secret + "." + m + " can call User.Achievement.Acquire (Steam) or clear the orb progress";
            }
            if ((tn.IndexOf("Achievement", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("Achiement", StringComparison.OrdinalIgnoreCase) >= 0) && (isWrite || (isCall && !ReadOnlyName(m))))
            {
                hint = "orb bits: save {op:\"orbs\", confirm:true}; never User.Achievement.Acquire";
                return "achievement writes reach Steam";
            }
            if (tn.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("Galaxy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hint = "platform (Steam / GOG) calls are not reachable through MCP";
                return full + " talks to the platform";
            }
            if (tn == "Application" && (m == "Quit" || m == "CancelQuit" || m == "OpenURL" || m.StartsWith("LoadLevel", StringComparison.Ordinal) || m == "ExternalCall" || m == "ExternalEval"))
            {
                hint = m.StartsWith("LoadLevel", StringComparison.Ordinal) ? "use the level tool" : "no tool quits the game or opens URLs";
                return "Application." + m + " is not allowed";
            }
            if (saveGuard && tn == "GameManager" && (m == "LoadSavepoint" || m == "ReloadScene" || m == "StartNewGame" || m == "Respawn" || m == "FadeAndReload"))
            {
                hint = "use level {op:\"load_checkpoint\" | \"reload\"} or checkpoint (save backup, confirm)";
                return "GameManager." + m + " writes INSIDE.sav (Steam Cloud)";
            }
            if (saveGuard && tn == "Savepoint" && (m == "Activate" || m == "Save"))
            {
                hint = "turn the save lock on (save {op:\"lock\"}) and walk into the savepoint";
                return "Savepoint." + m + " writes INSIDE.sav";
            }
            if (saveGuard)
                foreach (var s in SaveTypes)
                    if (tn == s || tn.StartsWith(s, StringComparison.Ordinal))
                        if ((isCall && !ReadOnlyName(m)) || isWrite)
                        {
                            hint = "save, lock and backup go through the save tool";
                            return full + "." + m + " can write the save";
                        }
            if (tn == "Remote" && m == "RunCommand")
            {
                hint = "use the command tool (it refuses OS input and server shutdown)";
                return "Remote.RunCommand bypasses the command tool's refusals";
            }
            if (tn.IndexOf("WinDrive", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("KeyHold", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("KeyHold", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hint = "input goes through act / interact (AgentCore)";
                return "OS input is not allowed";
            }
            if (tn == "PlayerPrefs" && (m == "DeleteAll" || m == "DeleteKey"))
            {
                hint = "settings stay as they are";
                return "PlayerPrefs." + m + " deletes settings";
            }
            if (RaisesVolume(m, isWrite ? value : null, isCall ? args : null))
            {
                hint = "sound policy: master volume only goes to 0 (audio {op:\"rtpc\", name:\"rtpc_master_volume\", value:0})";
                return "raising the master volume is not allowed";
            }
            return null;
        }

        static bool ReadOnlyName(string m)
        {
            return m.StartsWith("Get", StringComparison.Ordinal) || m.StartsWith("Is", StringComparison.Ordinal) || m.StartsWith("Has", StringComparison.Ordinal)
                || m.StartsWith("get_", StringComparison.Ordinal) || m.StartsWith("Can", StringComparison.Ordinal) || m == "ToString";
        }

        public static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // rtpc_master_volume, masterVolume, Master Volume ...
        public static bool IsMasterVolumeName(string name)
        {
            var n = Norm(name);
            return n.Contains("mastervolume") || n == "mastervol" || n == "rtpcmaster";
        }

        public static bool PositiveNumber(object v)
        {
            if (v == null || !IsNumber(v)) return false;
            double d = System.Convert.ToDouble(v, IC);
            return d > 0 || double.IsNaN(d);
        }

        // a member / call that would set a master volume above 0 (AudioListener.volume, any *masterVolume*, an RTPC call
        // whose name argument is the master volume, a PlayerPrefs key with volume)
        public static bool RaisesVolume(string member, object value, IList<object> args)
        {
            string m = Norm(member);
            if (value != null && (IsMasterVolumeName(member) || m == "volume") && PositiveNumber(value)) return true;
            if (args != null && args.Count >= 2)
            {
                var name = args[0] as string;
                bool rtpcCall = m.Contains("rtpc") || m.Contains("volume") || m.StartsWith("setfloat") || m.StartsWith("setint");
                if (rtpcCall && name != null && (IsMasterVolumeName(name) || Norm(name).Contains("volume")) && PositiveNumber(args[1])) return true;
            }
            if (args != null && args.Count >= 1 && IsMasterVolumeName(member) && PositiveNumber(args[0])) return true;
            return false;
        }
    }
}
