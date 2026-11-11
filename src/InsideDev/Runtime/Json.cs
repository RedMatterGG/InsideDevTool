using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InsideDev
{
    // Minimal JSON for mod files and bookmarks (Unity 5.0 has no JsonUtility).
    // Objects -> Dictionary<string, object> (insertion order kept), arrays -> List<object>,
    // numbers -> double, plus string / bool / null.
    public static class Json
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public sealed class Obj : Dictionary<string, object>
        {
            public readonly List<string> order = new List<string>();
            public new object this[string k]
            {
                get { object v; return TryGetValue(k, out v) ? v : null; }
                set { if (!ContainsKey(k)) order.Add(k); base[k] = value; }
            }
            public string Str(string k, string def = null)
            {
                var v = this[k];
                if (v == null) return def;
                if (v is bool) return (bool)v ? "true" : "false";   // a JSON literal reads like the text form ("True" would not)
                return v as string ?? Convert.ToString(v, IC);
            }
            public bool Bool(string k, bool def = false) { var v = this[k]; return v is bool ? (bool)v : def; }
            public double Num(string k, double def = 0) { var v = this[k]; return v is double ? (double)v : def; }
            public List<object> Arr(string k) { return this[k] as List<object> ?? new List<object>(); }
        }

        // ---------------------------------------------------------------- parse
        public static object Parse(string s)
        {
            if (s == null) throw new FormatException("no text");
            int i = 0;
            try
            {
                var v = Value(s, ref i, 0);
                Ws(s, ref i);
                if (i < s.Length) throw new FormatException("unexpected '" + s[i] + "'");
                return v;
            }
            catch (FormatException e) { throw new FormatException(e.Message + Where(s, i)); }
            catch (OverflowException) { throw new FormatException("number out of range" + Where(s, i)); }
        }

        // false instead of an exception; error says what is wrong and where (line, column)
        public static bool TryParse(string s, out object value, out string error)
        {
            value = null; error = null;
            try { value = Parse(s); return true; }
            catch (Exception e) { error = e.Message; return false; }
        }

        // " at line L, column C" of the place the parser stopped at
        static string Where(string s, int i)
        {
            if (i > s.Length) i = s.Length;
            int line = 1, col = 1;
            for (int k = 0; k < i; k++) { if (s[k] == '\n') { line++; col = 1; } else col++; }
            return " at line " + line.ToString(IC) + ", column " + col.ToString(IC);
        }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        public const int MaxDepth = 128;

        static char At(string s, int i) { if (i >= s.Length) throw new FormatException("unexpected end"); return s[i]; }

        static object Value(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("nesting deeper than " + MaxDepth + " at " + i);
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{')
            {
                var o = new Obj(); i++;
                Ws(s, ref i);
                if (At(s, i) == '}') { i++; return o; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    if (At(s, i) != ':') throw new FormatException("':' expected");
                    i++;
                    o[k] = Value(s, ref i, depth + 1);
                    Ws(s, ref i);
                    if (At(s, i) == ',') { i++; continue; }
                    if (At(s, i) == '}') { i++; return o; }
                    throw new FormatException("',' or '}' expected");
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++;
                Ws(s, ref i);
                if (At(s, i) == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i, depth + 1));
                    Ws(s, ref i);
                    if (At(s, i) == ',') { i++; continue; }
                    if (At(s, i) == ']') { i++; return l; }
                    throw new FormatException("',' or ']' expected");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new FormatException("unexpected '" + c + "'");
            double num;
            if (!double.TryParse(s.Substring(st, i - st), NumberStyles.Float, IC, out num)) { i = st; throw new FormatException("bad number"); }
            return num;
        }

        static string Str(string s, ref int i)
        {
            if (At(s, i) != '"') throw new FormatException("string expected");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        {
                            int code;
                            if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier, IC, out code)) throw new FormatException("bad unicode escape");
                            sb.Append((char)code); i += 4; break;
                        }
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("unterminated string");
        }

        // ---------------------------------------------------------------- write (pretty)
        public static string Write(object v)
        {
            var sb = new StringBuilder();
            W(sb, v, 0);
            return sb.ToString();
        }

        static void W(StringBuilder sb, object v, int ind)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { Q(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            // NaN and the infinities are not JSON (and Parse would refuse the file): null. A float keeps its own
            // shortest text ("0.1", not the double next to it); every other number type is written as a number too.
            if (v is float) { float f = (float)v; if (float.IsNaN(f) || float.IsInfinity(f)) sb.Append("null"); else sb.Append(f.ToString("R", IC)); return; }
            if (v is double) { double d = (double)v; if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null"); else sb.Append(d.ToString("R", IC)); return; }
            if (v is int || v is long || v is uint || v is ulong || v is short || v is ushort || v is byte || v is sbyte) { sb.Append(Convert.ToString(v, IC)); return; }
            if (v is decimal) { sb.Append(((decimal)v).ToString(IC)); return; }
            var o = v as Obj;
            if (o != null)
            {
                if (o.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int n = 0;
                foreach (var k in o.order)
                {
                    if (n++ > 0) sb.Append(",\n");
                    sb.Append(' ', ind + 2); Q(sb, k); sb.Append(": ");
                    W(sb, o[k], ind + 2);
                }
                sb.Append('\n').Append(' ', ind).Append('}');
                return;
            }
            var l = v as System.Collections.IList;
            if (l != null)
            {
                if (l.Count == 0) { sb.Append("[]"); return; }
                bool simple = true;
                foreach (var x in l) if (x is Obj || x is System.Collections.IList) simple = false;
                if (simple)
                {
                    sb.Append('[');
                    for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(", "); W(sb, l[i], ind); }
                    sb.Append(']');
                    return;
                }
                sb.Append("[\n");
                for (int i = 0; i < l.Count; i++)
                {
                    if (i > 0) sb.Append(",\n");
                    sb.Append(' ', ind + 2);
                    W(sb, l[i], ind + 2);
                }
                sb.Append('\n').Append(' ', ind).Append(']');
                return;
            }
            Q(sb, Convert.ToString(v, IC));
        }

        static void Q(StringBuilder sb, string s)
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
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", IC)); else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
