using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InsideDev
{
    // One-line JSON for machine-readable command replies ("<command> ... json"). Takes Json.Obj, any IDictionary with
    // string keys, any IList, strings, bools and numbers. Floats are written with their own shortest form ("0.1", not
    // "0.10000000149011612"); NaN and infinities become null. Any thread.
    public static class JsonLine
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static string Write(object v)
        {
            var sb = new StringBuilder(256);
            W(sb, v, 0);
            return sb.ToString();
        }

        // Shortcut for a flat reply: JsonLine.Obj("ok", true, "frames", 12, "file", path)
        public static string Obj(params object[] keyValues)
        {
            var o = new Json.Obj();
            for (int i = 0; i + 1 < keyValues.Length; i += 2) o[Convert.ToString(keyValues[i], IC)] = keyValues[i + 1];
            return Write(o);
        }

        static void W(StringBuilder sb, object v, int depth)
        {
            if (depth > Json.MaxDepth) { sb.Append("null"); return; }
            if (v == null) { sb.Append("null"); return; }
            var s = v as string;
            if (s != null) { Q(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is float) { float f = (float)v; if (float.IsNaN(f) || float.IsInfinity(f)) sb.Append("null"); else sb.Append(f.ToString("R", IC)); return; }
            if (v is double) { double d = (double)v; if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null"); else sb.Append(d.ToString("R", IC)); return; }
            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong || v is ushort || v is sbyte) { sb.Append(Convert.ToString(v, IC)); return; }
            if (v is decimal) { sb.Append(((decimal)v).ToString(IC)); return; }
            var o = v as Json.Obj;
            if (o != null)
            {
                sb.Append('{');
                int n = 0;
                foreach (var k in o.order)
                {
                    if (n++ > 0) sb.Append(',');
                    Q(sb, k); sb.Append(':');
                    W(sb, o[k], depth + 1);
                }
                sb.Append('}');
                return;
            }
            var dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                int n = 0;
                foreach (DictionaryEntry e in dict)
                {
                    if (n++ > 0) sb.Append(',');
                    Q(sb, Convert.ToString(e.Key, IC)); sb.Append(':');
                    W(sb, e.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }
            var list = v as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                int n = 0;
                foreach (var x in list)
                {
                    if (n++ > 0) sb.Append(',');
                    W(sb, x, depth + 1);
                }
                sb.Append(']');
                return;
            }
            Q(sb, Convert.ToString(v, IC));
        }

        public static void Q(StringBuilder sb, string s)
        {
            sb.Append('"');
            int run = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 32 && c != '"' && c != '\\') continue;
                if (i > run) sb.Append(s, run, i - run);
                run = i + 1;
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append("\\u").Append(((int)c).ToString("x4", IC)); break;
                }
            }
            if (s.Length > run) sb.Append(s, run, s.Length - run);
            sb.Append('"');
        }
    }
}
