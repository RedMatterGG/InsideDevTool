using System;
using System.Collections.Generic;

namespace InsideDev.McpExt
{
    // ServerSchema.cs (owner SERVER, signatures frozen): JSON Schema builder for tool input and output schemas, plus the
    // argument validator the server runs before every tools/call. Unity-free (compiled into the stdio proxy too).
    //   input:  Schema.Obj().Enum("op", "What to do.", "state", "state", "open").Int("repeat", "Presses.", 1, 1, 20).Build()
    //   output: Schema.Obj().Str("screen", "Screen.").Arr("items", "Items.", Schema.ObjOf(...), 0, 50).BuildOutput()
    // Build() gives {type:object, properties, required?, additionalProperties:false}. BuildOutput() adds ok and error,
    // never lists required fields and allows extra fields, so error results and later additions always validate.
    public sealed class SchemaObj
    {
        readonly Json.Obj props = new Json.Obj();
        readonly List<object> req = new List<object>();
        bool open;

        public SchemaObj Prop(string name, string desc, Json.Obj schema)
        {
            var s = schema ?? new Json.Obj();
            if (desc != null) s["description"] = desc;
            props[name] = s;
            return this;
        }

        public SchemaObj Str(string name, string desc) { return Prop(name, desc, Schema.Str()); }
        public SchemaObj Str(string name, string desc, string def) { var s = Schema.Str(); if (def != null) s["default"] = def; return Prop(name, desc, s); }
        public SchemaObj StrMax(string name, string desc, int maxLength) { var s = Schema.Str(); s["maxLength"] = (double)maxLength; return Prop(name, desc, s); }
        public SchemaObj Int(string name, string desc, long min, long max) { return Prop(name, desc, Schema.Int(min, max)); }
        public SchemaObj Int(string name, string desc, long def, long min, long max) { var s = Schema.Int(min, max); s["default"] = (double)def; return Prop(name, desc, s); }
        public SchemaObj Num(string name, string desc, double min, double max) { return Prop(name, desc, Schema.Num(min, max)); }
        public SchemaObj Num(string name, string desc, double def, double min, double max) { var s = Schema.Num(min, max); s["default"] = def; return Prop(name, desc, s); }
        public SchemaObj Bool(string name, string desc) { return Prop(name, desc, Schema.Bool()); }
        public SchemaObj Bool(string name, string desc, bool def) { var s = Schema.Bool(); s["default"] = def; return Prop(name, desc, s); }
        // def null = no default
        public SchemaObj Enum(string name, string desc, string def, params string[] values) { var s = Schema.Enum(values); if (def != null) s["default"] = def; return Prop(name, desc, s); }
        public SchemaObj Arr(string name, string desc, Json.Obj items, int minItems, int maxItems) { return Prop(name, desc, Schema.Arr(items, minItems, maxItems)); }
        // fixed-length number array, e.g. Vec("stick", "...", 2, -1, 1)
        public SchemaObj Vec(string name, string desc, int n, double min, double max) { return Prop(name, desc, Schema.Vec(n, min, max)); }
        public SchemaObj Obj(string name, string desc, SchemaObj inner) { return Prop(name, desc, inner.Build()); }
        public SchemaObj OutObj(string name, string desc, SchemaObj inner) { return Prop(name, desc, inner.BuildNested()); }
        // value of any JSON type (reflect set, echo fields)
        public SchemaObj Any(string name, string desc) { return Prop(name, desc, new Json.Obj()); }
        public SchemaObj Required(params string[] names) { foreach (var n in names) if (!req.Contains(n)) req.Add(n); return this; }
        // set "default" of an already added property (arrays, objects)
        public SchemaObj Default(string name, object value) { var s = props[name] as Json.Obj; if (s != null) s["default"] = value; return this; }
        // allow properties that are not listed
        public SchemaObj Open() { open = true; return this; }

        public Json.Obj Build()
        {
            var o = new Json.Obj(); o["type"] = "object"; o["properties"] = props;
            if (req.Count > 0) o["required"] = new List<object>(req);
            o["additionalProperties"] = open;
            return o;
        }

        // nested object inside an output schema: no required list, extra fields allowed, slim (Schema.Slim)
        public Json.Obj BuildNested()
        {
            var o = new Json.Obj(); o["type"] = "object"; o["properties"] = props;
            return Schema.Slim(o);
        }

        public Json.Obj BuildOutput()
        {
            var o = new Json.Obj(); o["type"] = "object";
            var p = new Json.Obj();
            var ok = Schema.Bool(); ok["description"] = "false when the call failed; see error";
            p["ok"] = ok;
            var e = Schema.ErrorSchema(); e["description"] = "only when ok is false";
            p["error"] = e;
            foreach (var k in props.order) if (k != "ok" && k != "error") p[k] = props[k];
            o["properties"] = p;
            return Schema.Slim(o);
        }
    }

    public static class Schema
    {
        public static SchemaObj Obj() { return new SchemaObj(); }

        public static Json.Obj Str() { var s = new Json.Obj(); s["type"] = "string"; return s; }
        public static Json.Obj Int(long min, long max) { var s = new Json.Obj(); s["type"] = "integer"; s["minimum"] = (double)min; s["maximum"] = (double)max; return s; }
        public static Json.Obj IntAny() { var s = new Json.Obj(); s["type"] = "integer"; return s; }
        public static Json.Obj Num(double min, double max) { var s = new Json.Obj(); s["type"] = "number"; s["minimum"] = min; s["maximum"] = max; return s; }
        public static Json.Obj NumAny() { var s = new Json.Obj(); s["type"] = "number"; return s; }
        public static Json.Obj Bool() { var s = new Json.Obj(); s["type"] = "boolean"; return s; }
        public static Json.Obj Enum(params string[] values)
        {
            var s = new Json.Obj(); s["type"] = "string";
            var l = new List<object>(); foreach (var v in values) l.Add(v);
            s["enum"] = l; return s;
        }
        public static Json.Obj Arr(Json.Obj items, int minItems, int maxItems)
        {
            var s = new Json.Obj(); s["type"] = "array"; s["items"] = items ?? new Json.Obj();
            if (minItems > 0) s["minItems"] = (double)minItems;
            if (maxItems > 0) s["maxItems"] = (double)maxItems;
            return s;
        }
        public static Json.Obj Vec(int n, double min, double max) { var s = Arr(Num(min, max), n, n); return s; }
        public static Json.Obj VecAny(int n) { return Arr(NumAny(), n, n); }
        public static Json.Obj ObjOf(SchemaObj inner) { return inner.Build(); }
        // any object; for fields documented elsewhere (observe, changes) so output schemas stay small
        public static Json.Obj ObjAny() { var s = new Json.Obj(); s["type"] = "object"; return s; }
        public static Json.Obj OutOf(SchemaObj inner) { return inner.BuildNested(); }
        public static Json.Obj Described(Json.Obj schema, string desc) { schema["description"] = desc; return schema; }

        // output schemas describe shapes, not limits: drops minimum, maximum, minItems, maxItems, maxLength, default and
        // empty descriptions at every level (keeps tools/list small; outputs are never validated against ranges)
        public static Json.Obj Slim(Json.Obj s)
        {
            if (s == null) return null;
            foreach (var k in new[] { "minimum", "maximum", "minItems", "maxItems", "maxLength", "default" }) if (s.ContainsKey(k)) { s.Remove(k); s.order.Remove(k); }
            if (s.ContainsKey("description") && (s["description"] as string) == "") { s.Remove("description"); s.order.Remove("description"); }
            var props = s["properties"] as Json.Obj;
            if (props != null) foreach (var k in props.order) Slim(props[k] as Json.Obj);
            Slim(s["items"] as Json.Obj);
            return s;
        }

        // the error object of every output schema
        public static Json.Obj ErrorSchema()
        {
            return Obj().Enum("code", "Error code.", null, McpErr.BadArgs, McpErr.Busy, McpErr.NotReady, McpErr.StaleId, McpErr.NotFound,
                    McpErr.Ambiguous, McpErr.Refused, McpErr.Unsafe, McpErr.ConfirmRequired, McpErr.Timeout, McpErr.Cancelled,
                    McpErr.GameError, McpErr.NotBuilt, McpErr.Internal, McpErr.Unauthorized)
                .Str("message", "What went wrong.").Str("hint", "How to fix it.").BuildNested();
        }

        // legacy flat builder used by the 13 dev tools: name, type, description, required(bool) ...
        public static Json.Obj Object(params object[] props)
        {
            var o = new Json.Obj(); o["type"] = "object";
            var p = new Json.Obj(); var req = new List<object>();
            for (int i = 0; i + 3 < props.Length; i += 4)
            {
                var d = new Json.Obj(); d["type"] = (string)props[i + 1]; d["description"] = (string)props[i + 2];
                p[(string)props[i]] = d;
                if ((bool)props[i + 3]) req.Add(props[i]);
            }
            o["properties"] = p;
            if (req.Count > 0) o["required"] = req;
            o["additionalProperties"] = false;
            return o;
        }

        // Strict check of args against an input schema built above: type (an integer accepts a double without fraction),
        // enum, minimum / maximum (and the exclusive forms), minLength / maxLength, minItems / maxItems, required,
        // additionalProperties false at every level, nested objects and array items. A null value of an optional
        // property counts as absent (models often send null for "not set"). Fills "default" values into args, into nested
        // objects that are present and into object items of arrays. On failure returns false with error =
        // "<path>: <problem>" (e.g. "inputs[2].stick: expected 2 numbers"); the server answers isError with bad_args.
        public static bool Validate(Json.Obj schema, Json.Obj args, out string error)
        {
            return Validate(schema, args, out error, null);
        }

        // lenient form: a string where the schema wants an array, object, number, integer or boolean is parsed when it
        // parses to that type ("[{...}]", "30", "true"); every such fix is added to coerced ("inputs: was a JSON string")
        public static bool Validate(Json.Obj schema, Json.Obj args, out string error, List<string> coerced)
        {
            error = null;
            if (schema == null) return true;
            if (args == null) { error = "arguments: expected an object"; return false; }
            var before = coercions;
            coercions = coerced;
            try { return Check(schema, args, "", true, out error); }
            finally { coercions = before; }
        }

        [ThreadStatic] static List<string> coercions;

        static object Coerce(Json.Obj s, object v, string at)
        {
            var str = v as string;
            if (coercions == null || str == null || s == null) return v;
            var type = s["type"] as string;
            if (type == null || type == "string") return v;
            string t = str.Trim();
            object r = null;
            switch (type)
            {
                case "number":
                case "integer":
                    {
                        double d;
                        if (double.TryParse(t, System.Globalization.NumberStyles.Float, IC, out d) && !double.IsNaN(d) && !double.IsInfinity(d)) r = d;
                        break;
                    }
                case "boolean":
                    if (string.Equals(t, "true", StringComparison.OrdinalIgnoreCase)) r = true;
                    else if (string.Equals(t, "false", StringComparison.OrdinalIgnoreCase)) r = false;
                    break;
                case "array":
                case "object":
                    {
                        if (t.Length < 2 || (type == "array" ? t[0] != '[' : t[0] != '{')) break;
                        object p = null;
                        try { p = Json.Parse(t); } catch { p = null; }
                        if (type == "array" ? p is List<object> : p is Json.Obj) r = p;
                        break;
                    }
            }
            if (r == null) return v;
            if (coercions.Count < 20) coercions.Add("coerced: " + at + " was the string " + Show(str.Length > 40 ? str.Substring(0, 40) + "..." : str) + ", read as " + type);
            return r;
        }

        // Conform, then remove each property / array element that still breaks the schema (at most 20), so a client that
        // validates structuredContent gets a valid result; fixes = "<path>: <problem>" per removal. False when a
        // mismatch is left (the root itself, or a path that cannot be removed).
        internal static bool Repair(Json.Obj schema, Json.Obj value, List<string> fixes, out string problem)
        {
            problem = null;
            for (int i = 0; i < 20; i++)
            {
                if (Conform(schema, value, out problem)) return true;
                if (problem == null) return false;
                int c = problem.IndexOf(": ", StringComparison.Ordinal);
                string path = c > 0 ? problem.Substring(0, c) : null;
                if (path == null || path == "arguments" || !RemovePath(value, path)) return false;
                if (fixes != null) fixes.Add(problem);
            }
            return Conform(schema, value, out problem);
        }

        // "a.b[2].c": remove c from a.b[2]; "warnings[3]": remove that element
        internal static bool RemovePath(Json.Obj root, string path)
        {
            var parts = new List<object>();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < path.Length; i++)
            {
                char ch = path[i];
                if (ch == '.') { if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Length = 0; } continue; }
                if (ch == '[')
                {
                    if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Length = 0; }
                    int end = path.IndexOf(']', i);
                    int n;
                    if (end < 0 || !int.TryParse(path.Substring(i + 1, end - i - 1), System.Globalization.NumberStyles.None, IC, out n)) return false;
                    parts.Add(n);
                    i = end;
                    continue;
                }
                sb.Append(ch);
            }
            if (sb.Length > 0) parts.Add(sb.ToString());
            if (parts.Count == 0) return false;
            object cur = root;
            for (int i = 0; i < parts.Count - 1; i++)
            {
                if (parts[i] is int) { var l = cur as List<object>; int n = (int)parts[i]; if (l == null || n < 0 || n >= l.Count) return false; cur = l[n]; }
                else { var o = cur as Json.Obj; if (o == null || !o.ContainsKey((string)parts[i])) return false; cur = o[(string)parts[i]]; }
            }
            var last = parts[parts.Count - 1];
            if (last is int)
            {
                var l = cur as List<object>;
                int n = (int)last;
                if (l == null || n < 0 || n >= l.Count) return false;
                l.RemoveAt(n);
                return true;
            }
            var obj = cur as Json.Obj;
            string k = (string)last;
            if (obj == null || !obj.ContainsKey(k)) return false;
            obj.Remove(k);
            obj.order.Remove(k);
            return true;
        }

        // Non-mutating check of a tool result against its output schema. Removes null-valued properties the schema does
        // not allow (absent is always valid: output schemas have no required list) and reports the first other mismatch
        // in problem. Returns false when a mismatch is left.
        internal static bool Conform(Json.Obj schema, Json.Obj value, out string problem)
        {
            problem = null;
            if (schema == null || value == null) return true;
            StripNulls(schema, value);
            return Check(schema, value, "", false, out problem);
        }

        const int MaxListed = 30;
        static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;

        static bool Check(Json.Obj s, object v, string path, bool fill, out string error)
        {
            error = null;
            if (s == null) return true;
            string at = path.Length == 0 ? "arguments" : path;
            object type = s["type"];
            if (type != null && !TypeOk(type, v))
            {
                if (TypeName(type) == "integer" && IsNumber(v)) error = at + ": expected an integer, got " + Show(v);
                else error = at + ": expected " + TypeName(type) + ", got " + Kind(v);
                return false;
            }
            var en = s["enum"] as List<object>;
            if (en != null && !InEnum(en, v)) { error = at + ": " + Show(v) + " is not one of " + Listed(en); return false; }
            if (s.ContainsKey("const") && !SameValue(s["const"], v)) { error = at + ": must be " + Show(s["const"]); return false; }
            if (IsNumber(v))
            {
                double d = Convert.ToDouble(v, IC);
                if (double.IsNaN(d) || double.IsInfinity(d)) { error = at + ": expected a finite number"; return false; }
                if (s["minimum"] is double && d < (double)s["minimum"]) { error = at + ": " + Show(v) + " is below the minimum " + Show(s["minimum"]); return false; }
                if (s["maximum"] is double && d > (double)s["maximum"]) { error = at + ": " + Show(v) + " is above the maximum " + Show(s["maximum"]); return false; }
                if (s["exclusiveMinimum"] is double && d <= (double)s["exclusiveMinimum"]) { error = at + ": must be above " + Show(s["exclusiveMinimum"]); return false; }
                if (s["exclusiveMaximum"] is double && d >= (double)s["exclusiveMaximum"]) { error = at + ": must be below " + Show(s["exclusiveMaximum"]); return false; }
            }
            var str = v as string;
            if (str != null)
            {
                if (s["maxLength"] is double && str.Length > (double)s["maxLength"]) { error = at + ": longer than " + Show(s["maxLength"]) + " characters (" + str.Length + ")"; return false; }
                if (s["minLength"] is double && str.Length < (double)s["minLength"]) { error = at + ": shorter than " + Show(s["minLength"]) + " characters"; return false; }
            }
            var list = v as List<object>;
            if (list != null) return CheckArray(s, list, at, fill, out error);
            var obj = v as Json.Obj;
            if (obj != null && (type != null || s["properties"] is Json.Obj)) return CheckObject(s, obj, path, fill, out error);
            return true;
        }

        static bool CheckArray(Json.Obj s, List<object> list, string at, bool fill, out string error)
        {
            error = null;
            var items = s["items"] as Json.Obj;
            int min = s["minItems"] is double ? (int)(double)s["minItems"] : -1;
            int max = s["maxItems"] is double ? (int)(double)s["maxItems"] : -1;
            bool numbers = items != null && (items.Str("type") == "number" || items.Str("type") == "integer");
            if (min >= 0 && min == max && numbers && list.Count != min) { error = at + ": expected " + min + " numbers, got " + list.Count; return false; }
            if (min >= 0 && list.Count < min) { error = at + ": expected at least " + min + " item" + (min == 1 ? "" : "s") + ", got " + list.Count; return false; }
            if (max >= 0 && list.Count > max) { error = at + ": expected at most " + max + " item" + (max == 1 ? "" : "s") + ", got " + list.Count; return false; }
            if (items == null) return true;
            for (int i = 0; i < list.Count; i++)
            {
                if (fill) list[i] = Coerce(items, list[i], at + "[" + i + "]");
                if (!Check(items, list[i], at + "[" + i + "]", fill, out error)) return false;
            }
            return true;
        }

        static bool CheckObject(Json.Obj s, Json.Obj o, string path, bool fill, out string error)
        {
            error = null;
            var props = s["properties"] as Json.Obj ?? new Json.Obj();
            var req = s["required"] as List<object> ?? new List<object>();
            object extra = s["additionalProperties"];
            foreach (var k in new List<string>(o.order))
            {
                if (!o.ContainsKey(k)) continue;
                string at = path.Length == 0 ? k : path + "." + k;
                var ps = props[k] as Json.Obj;
                if (ps == null && !props.ContainsKey(k))
                {
                    if (extra is bool && !(bool)extra)
                    {
                        error = at + ": unknown property" + (props.Count > 0 ? "; allowed: " + Listed(props.order.ConvertAll(x => (object)x)) : "; this object takes no properties");
                        return false;
                    }
                    var es = extra as Json.Obj;
                    if (es != null && !Check(es, o[k], at, fill, out error)) return false;
                    continue;
                }
                if (o[k] == null && !AllowsNull(ps))
                {
                    if (req.Contains(k)) { error = at + ": required"; return false; }
                    if (fill) { o.Remove(k); o.order.Remove(k); continue; }
                }
                if (fill) { var cv = Coerce(ps, o[k], at); if (!ReferenceEquals(cv, o[k])) o[k] = cv; }
                if (!Check(ps, o[k], at, fill, out error)) return false;
            }
            foreach (var r in req)
            {
                string k = r as string;
                if (k == null) continue;
                if (!o.ContainsKey(k) || (o[k] == null && !AllowsNull(props[k] as Json.Obj))) { error = (path.Length == 0 ? k : path + "." + k) + ": required"; return false; }
            }
            if (fill)
                foreach (var k in props.order)
                {
                    var ps = props[k] as Json.Obj;
                    if (ps != null && ps.ContainsKey("default") && !o.ContainsKey(k)) o[k] = Clone(ps["default"]);
                }
            return true;
        }

        static void StripNulls(Json.Obj s, object v)
        {
            if (s == null) return;
            var o = v as Json.Obj;
            if (o != null)
            {
                var props = s["properties"] as Json.Obj;
                if (props == null) return;
                foreach (var k in new List<string>(o.order))
                {
                    if (!o.ContainsKey(k)) continue;
                    var ps = props[k] as Json.Obj;
                    if (ps == null) continue;
                    if (o[k] == null) { if (!AllowsNull(ps)) { o.Remove(k); o.order.Remove(k); } continue; }
                    StripNulls(ps, o[k]);
                }
                return;
            }
            var l = v as List<object>;
            var items = s["items"] as Json.Obj;
            if (l != null && items != null) foreach (var x in l) StripNulls(items, x);
        }

        static bool AllowsNull(Json.Obj s)
        {
            if (s == null) return true;
            object t = s["type"];
            if (t == null) return true;
            if (t is string) return (string)t == "null";
            var l = t as List<object>;
            return l != null && l.Contains("null");
        }

        static bool TypeOk(object type, object v)
        {
            var one = type as string;
            if (one != null) return TypeOk(one, v);
            var many = type as List<object>;
            if (many == null) return true;
            foreach (var t in many) if (t is string && TypeOk((string)t, v)) return true;
            return false;
        }

        static bool TypeOk(string type, object v)
        {
            switch (type)
            {
                case "string": return v is string;
                case "boolean": return v is bool;
                case "number": return IsNumber(v);
                case "integer":
                    {
                        if (v is int || v is long) return true;
                        if (!(v is double) && !(v is float)) return false;
                        double d = Convert.ToDouble(v, IC);
                        return !double.IsInfinity(d) && !double.IsNaN(d) && d == Math.Floor(d);
                    }
                case "array": return v is List<object>;
                case "object": return v is Json.Obj;
                case "null": return v == null;
                default: return true;
            }
        }

        static bool IsNumber(object v) { return v is double || v is int || v is long || v is float; }

        static string TypeName(object type)
        {
            var one = type as string;
            if (one != null) return one;
            var many = type as List<object>;
            if (many == null) return "?";
            var parts = new List<string>(); foreach (var t in many) parts.Add(Convert.ToString(t, IC));
            return string.Join(" or ", parts.ToArray());
        }

        static string Kind(object v)
        {
            if (v == null) return "null";
            if (v is string) return "string " + Show(v);
            if (v is bool) return "boolean";
            if (IsNumber(v)) return "number " + Show(v);
            if (v is List<object>) return "array";
            if (v is Json.Obj) return "object";
            return v.GetType().Name;
        }

        static string Show(object v)
        {
            string s = JsonOut.Write(v);
            return s.Length > 60 ? s.Substring(0, 57) + "..." : s;
        }

        static string Listed(List<object> values)
        {
            var parts = new List<string>();
            foreach (var x in values) { if (parts.Count >= MaxListed) { parts.Add("..."); break; } parts.Add(x is string ? (string)x : JsonOut.Write(x)); }
            return string.Join(", ", parts.ToArray());
        }

        static bool InEnum(List<object> en, object v)
        {
            foreach (var x in en) if (SameValue(x, v)) return true;
            return false;
        }

        static bool SameValue(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (IsNumber(a) && IsNumber(b)) return Convert.ToDouble(a, IC) == Convert.ToDouble(b, IC);
            if (a is string || a is bool) return a.Equals(b);
            return JsonOut.Write(a) == JsonOut.Write(b);
        }

        // deep copy of a default value, so filled arguments never share lists or objects with the schema
        internal static object Clone(object v)
        {
            var o = v as Json.Obj;
            if (o != null) { var c = new Json.Obj(); foreach (var k in o.order) if (o.ContainsKey(k)) c[k] = Clone(o[k]); return c; }
            var l = v as List<object>;
            if (l != null) { var c = new List<object>(l.Count); foreach (var x in l) c.Add(Clone(x)); return c; }
            return v;
        }
    }
}
