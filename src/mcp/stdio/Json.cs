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
            public string Str(string k, string def = null) { var v = this[k]; return v == null ? def : v as string ?? Convert.ToString(v, IC); }
            public bool Bool(string k, bool def = false) { var v = this[k]; return v is bool ? (bool)v : def; }
            public double Num(string k, double def = 0) { var v = this[k]; return v is double ? (double)v : def; }
            public List<object> Arr(string k) { return this[k] as List<object> ?? new List<object>(); }
        }

        // ---------------------------------------------------------------- parse
        public static object Parse(string s)
        {
            int i = 0;
            var v = Value(s, ref i);
            Ws(s, ref i);
            if (i < s.Length) throw new FormatException("unexpected '" + s[i] + "' at " + i);
            return v;
        }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{')
            {
                var o = new Obj(); i++;
                Ws(s, ref i);
                if (s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    if (s[i] != ':') throw new FormatException("':' expected at " + i);
                    i++;
                    o[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return o; }
                    throw new FormatException("',' or '}' expected at " + i);
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++;
                Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException("',' or ']' expected at " + i);
                }
            }
            if (c == '"') return Str(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new FormatException("unexpected '" + c + "' at " + i);
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, IC);
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("string expected at " + i);
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
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
            if (v is double || v is float || v is int || v is long) { sb.Append(Convert.ToDouble(v).ToString("R", IC)); return; }
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
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
