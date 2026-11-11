using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // One way to print and read numbers for everything that leaves the process: command replies, files, logs, ffmpeg
    // arguments. Always the invariant culture (decimal point), whatever the Windows language is.
    public static class Fmt
    {
        public static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static string F(float v) { return v.ToString("0.###", IC); }
        public static string F(float v, string format) { return v.ToString(format, IC); }
        public static string F(double v) { return v.ToString("0.###", IC); }
        public static string F(double v, string format) { return v.ToString(format, IC); }
        public static string I(int v) { return v.ToString(IC); }
        public static string I(long v) { return v.ToString(IC); }

        // "(x, y)", "(x, y, z)", "(x, y, z, w)": the shape of UnityEngine's own ToString, with a decimal point.
        public static string V2(Vector2 v) { return V2(v, "0.##"); }
        public static string V2(Vector2 v, string format) { return "(" + v.x.ToString(format, IC) + ", " + v.y.ToString(format, IC) + ")"; }
        public static string V3(Vector3 v) { return V3(v, "0.##"); }
        public static string V3(Vector3 v, string format) { return "(" + v.x.ToString(format, IC) + ", " + v.y.ToString(format, IC) + ", " + v.z.ToString(format, IC) + ")"; }
        public static string V4(Vector4 v, string format) { return "(" + v.x.ToString(format, IC) + ", " + v.y.ToString(format, IC) + ", " + v.z.ToString(format, IC) + ", " + v.w.ToString(format, IC) + ")"; }
        public static string Q(Quaternion q, string format) { return "(" + q.x.ToString(format, IC) + ", " + q.y.ToString(format, IC) + ", " + q.z.ToString(format, IC) + ", " + q.w.ToString(format, IC) + ")"; }
        public static string R(Rect r) { return R(r, "0.#"); }
        public static string R(Rect r, string format) { return "(x:" + r.x.ToString(format, IC) + ", y:" + r.y.ToString(format, IC) + ", w:" + r.width.ToString(format, IC) + ", h:" + r.height.ToString(format, IC) + ")"; }
        public static string C(Color c) { return "(" + c.r.ToString("0.###", IC) + ", " + c.g.ToString("0.###", IC) + ", " + c.b.ToString("0.###", IC) + ", " + c.a.ToString("0.###", IC) + ")"; }

        // string.Format with the invariant culture
        public static string S(string format, params object[] args) { return string.Format(IC, format, args); }

        // any boxed value: numbers and Unity vectors with a decimal point, everything else through ToString
        public static string Any(object v)
        {
            if (v == null) return "null";
            if (v is float) return ((float)v).ToString("R", IC);
            if (v is double) return ((double)v).ToString("R", IC);
            if (v is Vector3) return V3((Vector3)v, "0.###");
            if (v is Vector2) return V2((Vector2)v, "0.###");
            if (v is Vector4) return V4((Vector4)v, "0.###");
            if (v is Quaternion) return Q((Quaternion)v, "0.####");
            if (v is Color) return C((Color)v);
            if (v is Rect) return R((Rect)v, "0.###");
            var f = v as IFormattable;
            return f != null ? f.ToString(null, IC) : v.ToString();
        }

        public static bool TryF(string s, out float v) { return float.TryParse(s, NumberStyles.Float, IC, out v); }
        public static bool TryD(string s, out double v) { return double.TryParse(s, NumberStyles.Float, IC, out v); }
        public static bool TryI(string s, out int v) { return int.TryParse(s, NumberStyles.Integer, IC, out v); }
        public static bool TryL(string s, out long v) { return long.TryParse(s, NumberStyles.Integer, IC, out v); }

        public static StringBuilder AppendF(this StringBuilder sb, float v, string format) { return sb.Append(v.ToString(format, IC)); }
    }
}
