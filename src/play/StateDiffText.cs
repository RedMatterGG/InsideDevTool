using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace InsideDev.Play
{
    // StateDiffText.cs (owner WORLD_B): the engine-free half of state_diff, offline tested (qa/mcp/WORLD_B). Short value
    // text for snapshot fields (floats "0.####", vectors to 0.01, quaternions to 0.001, enums by name, strings cut at 80,
    // collections as Type[count], Unity objects through refPath), snapshot keys "object path|component|field" and their
    // split from the right (field and component names never hold '|'), and the hierarchy segment with a sibling index.
    public static class StateDiffText
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public const int MaxText = 80;

        public static string Value(object v, Func<UnityEngine.Object, string> refPath)
        {
            if (v == null) return "null";
            if (v is UnityEngine.Object) return refPath != null ? refPath((UnityEngine.Object)v) : "ref";
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is float) return Num((float)v);
            if (v is double) return Num((double)v);
            if (v is string) return Cut((string)v);
            if (v is Enum) return v.ToString();
            if (v is Vector3) { var x = (Vector3)v; return "(" + V(x.x) + ", " + V(x.y) + ", " + V(x.z) + ")"; }
            if (v is Vector2) { var x = (Vector2)v; return "(" + V(x.x) + ", " + V(x.y) + ")"; }
            if (v is Vector4) { var x = (Vector4)v; return "(" + V(x.x) + ", " + V(x.y) + ", " + V(x.z) + ", " + V(x.w) + ")"; }
            if (v is Quaternion) { var q = (Quaternion)v; return "(" + Q(q.x) + ", " + Q(q.y) + ", " + Q(q.z) + ", " + Q(q.w) + ")"; }
            if (v is Color) { var c = (Color)v; return "rgba(" + V(c.r) + ", " + V(c.g) + ", " + V(c.b) + ", " + V(c.a) + ")"; }
            if (v is LayerMask) return ((LayerMask)v).value.ToString(IC);
            if (v is ICollection) return Short(v.GetType()) + "[" + ((ICollection)v).Count.ToString(IC) + "]";
            if (ReflectValues.IsNumber(v)) return Convert.ToString(v, IC);
            if (v is char) return v.ToString();
            return Short(v.GetType());
        }

        public static bool IsLeaf(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4)
                || t == typeof(Quaternion) || t == typeof(Color) || t == typeof(LayerMask) || typeof(UnityEngine.Object).IsAssignableFrom(t);
        }

        static string Short(Type t)
        {
            if (t.IsArray) return Short(t.GetElementType());
            var n = t.Name;
            int q = n.IndexOf('`');
            return q > 0 ? n.Substring(0, q) : n;
        }

        public static string Num(double d)
        {
            if (double.IsNaN(d)) return "NaN";
            if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
            var s = d.ToString("0.####", IC);
            return s == "-0" ? "0" : s;
        }

        static string V(float f) { var s = ((double)f).ToString("0.##", IC); return s == "-0" ? "0" : s; }
        static string Q(float f) { var s = ((double)f).ToString("0.###", IC); return s == "-0" ? "0" : s; }

        public static string Cut(string s)
        {
            if (s == null) return "null";
            s = s.Replace("\n", " ").Replace("\r", " ");
            return s.Length <= MaxText ? s : s.Substring(0, MaxText) + "...";
        }

        public static string Key(string path, string component, string field) { return path + "|" + component + "|" + field; }

        public static bool Split(string key, out string path, out string component, out string field)
        {
            path = component = field = "";
            if (key == null) return false;
            int b = key.LastIndexOf('|');
            if (b <= 0) { path = key; return false; }
            int a = key.LastIndexOf('|', b - 1);
            if (a < 0) { path = key.Substring(0, b); field = key.Substring(b + 1); return false; }
            path = key.Substring(0, a);
            component = key.Substring(a + 1, b - a - 1);
            field = key.Substring(b + 1);
            return true;
        }

        // one path segment: the name, plus "[n]" when n earlier siblings carry the same name
        public static string Segment(string name, int sameNameBefore)
        {
            var n = (name ?? "").Replace("|", "_");
            return sameNameBefore > 0 ? n + "[" + sameNameBefore.ToString(IC) + "]" : n;
        }

        // the label of the k-th component of one type on an object: "Type", "Type[1]", ...
        public static string ComponentLabel(string typeName, int index) { return index > 0 ? typeName + "[" + index.ToString(IC) + "]" : typeName; }

        public static string FieldName(string raw) { return ReflectValues.CleanName(raw); }

        // a difference that says nothing about game logic: an Animation clip time, a Rigidbody's velocity / angular
        // velocity / sleeping flag, or a Transform value that moved by at most 0.02 in every component
        public static bool IsNoise(string component, string field, string before, string after)
        {
            string c = component ?? "", f = field ?? "";
            int br = c.IndexOf('[');
            if (br > 0) c = c.Substring(0, br);
            if (c == "Animation") return f.StartsWith("time.", StringComparison.Ordinal);
            if (c == "Rigidbody") return f == "velocity" || f == "angular" || f == "sleeping";
            if (c == "Transform" && (f == "position" || f == "euler" || f == "scale"))
            {
                var x = Numbers(before);
                var y = Numbers(after);
                if (x == null || y == null || x.Count != y.Count || x.Count == 0) return false;
                for (int i = 0; i < x.Count; i++) if (Math.Abs(x[i] - y[i]) > 0.0201) return false;
                return true;
            }
            return false;
        }

        // the numbers in a value text ("(1.5, -2, 0)" -> 1.5, -2, 0); null for null
        public static List<double> Numbers(string s)
        {
            if (s == null) return null;
            var l = new List<double>();
            int i = 0;
            while (i < s.Length)
            {
                char ch = s[i];
                if (!(char.IsDigit(ch) || ((ch == '-' || ch == '.') && i + 1 < s.Length && (char.IsDigit(s[i + 1]) || s[i + 1] == '.')))) { i++; continue; }
                int j = i + 1;
                while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.' || ((s[j] == 'E' || s[j] == 'e') && j + 1 < s.Length) || ((s[j] == '-' || s[j] == '+') && (s[j - 1] == 'E' || s[j - 1] == 'e')))) j++;
                double d;
                if (double.TryParse(s.Substring(i, j - i), System.Globalization.NumberStyles.Float, IC, out d)) l.Add(d);
                i = j;
            }
            return l;
        }
    }
}
