using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InsideDev.Play
{
    // ObserverCore.cs (owner OBSERVE): the Unity-free parts of the observation, compiled directly by the offline tests
    // (qa/mcp/OBSERVE): the id registry (one counter that never restarts, epochs, liveness through a delegate), the
    // no-progress counter, state-name shortening, compact JSON size, the concise budget trimmer and the hazard order.
    internal enum IdLookup { Ok, Stale, Destroyed, NotFound, Malformed }

    internal sealed class IdTable
    {
        sealed class Entry { public string id; public long key; public object obj; }

        readonly object gate = new object();
        readonly Dictionary<long, Entry> byKey = new Dictionary<long, Entry>();
        readonly Dictionary<string, Entry> byId = new Dictionary<string, Entry>();
        readonly Func<object, bool> alive;
        long counter;
        volatile int epoch;

        public IdTable(Func<object, bool> alive) { this.alive = alive ?? (o => o != null); }

        public int Epoch { get { return epoch; } }
        public long Issued { get { lock (gate) return counter; } }
        public int Count { get { lock (gate) return byId.Count; } }

        public const int PrefixObject = 0, PrefixHazard = 1, PrefixBody = 2;

        public static int PrefixCode(string prefix)
        {
            if (prefix == "h") return PrefixHazard;
            if (prefix == "b") return PrefixBody;
            return PrefixObject;
        }

        public static long Key(int instanceId, int prefixCode, int sub)
        {
            return ((long)instanceId << 8) | (long)(((prefixCode & 0xF) << 4) | (sub & 0xF));
        }

        public string Assign(long key, object obj, string prefix)
        {
            lock (gate)
            {
                Entry e;
                if (byKey.TryGetValue(key, out e))
                {
                    if (ReferenceEquals(e.obj, obj) && alive(e.obj)) return e.id;
                    byId.Remove(e.id);
                }
                counter++;
                e = new Entry { id = prefix + counter.ToString(CultureInfo.InvariantCulture), key = key, obj = obj };
                byKey[key] = e;
                byId[e.id] = e;
                return e.id;
            }
        }

        public string Existing(long key, object obj)
        {
            lock (gate)
            {
                Entry e;
                if (byKey.TryGetValue(key, out e) && ReferenceEquals(e.obj, obj) && alive(e.obj)) return e.id;
                return null;
            }
        }

        public IdLookup Lookup(string id, out object obj)
        {
            obj = null;
            char p;
            long n;
            if (!Parse(id, out p, out n)) return IdLookup.Malformed;
            lock (gate)
            {
                Entry e;
                if (byId.TryGetValue(id, out e))
                {
                    if (!alive(e.obj)) return IdLookup.Destroyed;
                    obj = e.obj;
                    return IdLookup.Ok;
                }
                return n >= 1 && n <= counter ? IdLookup.Stale : IdLookup.NotFound;
            }
        }

        // a live object registered under this instance id (any prefix), or null
        public object FindByInstance(int instanceId)
        {
            lock (gate)
            {
                foreach (var kv in byKey)
                    if ((kv.Key >> 8) == instanceId && alive(kv.Value.obj)) return kv.Value.obj;
                return null;
            }
        }

        public int BumpEpoch()
        {
            lock (gate)
            {
                byKey.Clear();
                byId.Clear();
                epoch++;
                return epoch;
            }
        }

        public static bool Parse(string id, out char prefix, out long n)
        {
            prefix = '\0';
            n = 0;
            if (string.IsNullOrEmpty(id) || id.Length < 2 || id.Length > 19) return false;
            char c = id[0];
            if (c != 'o' && c != 'h' && c != 'b') return false;
            for (int i = 1; i < id.Length; i++) if (id[i] < '0' || id[i] > '9') return false;
            if (id[1] == '0') return false;
            prefix = c;
            return long.TryParse(id.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out n);
        }

        public static bool LooksLikeId(string s)
        {
            char p;
            long n;
            return Parse(s, out p, out n);
        }
    }

    // action calls in a row without progress. Progress = new ground (the body leaves the box it covered since the
    // checkpoint by MinMove on some axis: walking back and forth or jumping in place is not), a new checkpoint, or a
    // caller-detected change (new near-body events). Deaths at the same checkpoint count separately (hint from
    // DeathHintFrom). No body (title, pause menu) counts as a call without progress.
    internal sealed class StuckCounter
    {
        public const double MinMove = 0.2;
        public const int HintFrom = 3, DeathHintFrom = 2;
        public int Count, Deaths;
        public string Guide;
        bool has;
        double minX, maxX, minY, maxY, minZ, maxZ;
        string checkpoint;

        public int Update(bool hasPos, double x, double y, double z, bool changed) { return Update(hasPos, x, y, z, changed, null, 0); }

        // cp = checkpoint key ("12:3"), null = unknown; deaths = deaths since the previous update
        public int Update(bool hasPos, double x, double y, double z, bool changed, string cp, int deaths)
        {
            if (cp != null && cp != checkpoint)
            {
                if (checkpoint != null) changed = true;
                checkpoint = cp;
                has = false;
                Deaths = 0;
            }
            Deaths += Math.Max(0, deaths);
            bool fresh = false;
            if (hasPos)
            {
                if (!has) { has = true; minX = maxX = x; minY = maxY = y; minZ = maxZ = z; fresh = true; }
                else
                {
                    if (x > maxX + MinMove) { maxX = x; fresh = true; }
                    if (x < minX - MinMove) { minX = x; fresh = true; }
                    if (y > maxY + MinMove) { maxY = y; fresh = true; }
                    if (y < minY - MinMove) { minY = y; fresh = true; }
                    if (z > maxZ + MinMove) { maxZ = z; fresh = true; }
                    if (z < minZ - MinMove) { minZ = z; fresh = true; }
                }
            }
            if (fresh || changed) Count = 0;
            else Count++;
            return Count;
        }

        public void Reset() { Count = 0; Deaths = 0; has = false; checkpoint = null; }

        public string Hint()
        {
            string h = null;
            if (Count >= HintFrom) h = Count + " action calls without progress: change the approach (another verb or direction, jump, wait_until a hazard, read guide for this place, or checkpoint reload)";
            if (Deaths >= DeathHintFrom)
                h = (h != null ? h + "; " : "") + Deaths + " deaths at this checkpoint: read guide" + (string.IsNullOrEmpty(Guide) ? "" : " {topic:\"" + Guide + "\"}") + " for this place, time the hazard (wait_until, hazards eta_s) or try another route";
            return h;
        }
    }

    internal static class StateNames
    {
        static readonly string[] Prefixes = { "VisualBoy", "Boy", "Huddle" };

        public static string Short(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return "";
            string s = typeName;
            int dot = s.LastIndexOf('.');
            if (dot >= 0) s = s.Substring(dot + 1);
            int plus = s.LastIndexOf('+');
            if (plus >= 0) s = s.Substring(plus + 1);
            foreach (var p in Prefixes)
                if (s.Length > p.Length && s.StartsWith(p, StringComparison.Ordinal) && char.IsUpper(s[p.Length])) { s = s.Substring(p.Length); break; }
            if (s.Length > 5 && s.EndsWith("State", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 5);
            return s.Length == 0 ? typeName : s;
        }

        public static string Lower(string enumName)
        {
            if (string.IsNullOrEmpty(enumName)) return "";
            var sb = new StringBuilder(enumName.Length + 4);
            for (int i = 0; i < enumName.Length; i++)
            {
                char c = enumName[i];
                if (char.IsUpper(c))
                {
                    if (i > 0 && (char.IsLower(enumName[i - 1]) || (i + 1 < enumName.Length && char.IsLower(enumName[i + 1]) && char.IsUpper(enumName[i - 1])))) sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string Clip(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 3)) + "...";
        }

        // the tail of a hierarchy path that fits max chars and starts at a segment boundary (a valid suffix spec)
        public static string PathTail(string path, int max)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= max) return path ?? "";
            int i = path.Length - max;
            int slash = path.IndexOf('/', i);
            if (slash < 0 || slash >= path.Length - 1) return path.Substring(path.LastIndexOf('/') + 1);
            return path.Substring(slash + 1);
        }
    }

    // compact JSON text (the server's text block format) and its length, for the concise budget
    internal static class CompactJson
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static string Write(object v)
        {
            var sb = new StringBuilder(512);
            W(sb, v);
            return sb.ToString();
        }

        public static int Length(object v) { return Write(v).Length; }

        static void W(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            var s = v as string;
            if (s != null) { Q(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is double || v is float || v is int || v is long) { sb.Append(Convert.ToDouble(v, IC).ToString("R", IC)); return; }
            var o = v as Json.Obj;
            if (o != null)
            {
                sb.Append('{');
                int n = 0;
                foreach (var k in o.order)
                {
                    if (!o.ContainsKey(k)) continue;
                    if (n++ > 0) sb.Append(',');
                    Q(sb, k);
                    sb.Append(':');
                    W(sb, o[k]);
                }
                sb.Append('}');
                return;
            }
            var l = v as System.Collections.IList;
            if (l != null)
            {
                sb.Append('[');
                for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); W(sb, l[i]); }
                sb.Append(']');
                return;
            }
            Q(sb, Convert.ToString(v, IC));
        }

        static void Q(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", IC));
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }

    internal static class JsonEdit
    {
        // a copy of o without the given keys (Json.Obj keeps its key order in a list, so keys are never removed in place)
        public static Json.Obj Without(Json.Obj o, params string[] keys)
        {
            var r = new Json.Obj();
            foreach (var k in o.order)
            {
                if (!o.ContainsKey(k)) continue;
                bool skip = false;
                foreach (var x in keys) if (x == k) { skip = true; break; }
                if (!skip) r[k] = o[k];
            }
            return r;
        }

        public static List<object> Head(List<object> l, int n)
        {
            if (l == null) return new List<object>();
            if (l.Count <= n) return l;
            return l.GetRange(0, Math.Max(0, n));
        }

        public static void AddTruncated(Json.Obj o, string list, int cut)
        {
            if (cut <= 0) return;
            var t = o["truncated"] as Json.Obj;
            if (t == null) { t = new Json.Obj(); o["truncated"] = t; }
            double was = t[list] is double ? (double)t[list] : 0;
            t[list] = was + cut;
        }
    }

    // shrinks a concise observation until its compact JSON fits maxChars: fewer events, objects and hazards first,
    // then shorter object entries. Every cut is counted in truncated{list: n}.
    internal static class ConciseBudget
    {
        public const int MaxChars = 2400;

        static readonly int[] EventSteps = { 12, 8, 5, 3, 0 };
        static readonly int[] ObjectSteps = { 10, 8, 6, 4, 3 };
        static readonly int[] HazardSteps = { 5, 4, 3, 2 };

        public static int Fit(Json.Obj obs, int maxChars)
        {
            int len = CompactJson.Length(obs);
            if (len <= maxChars) return len;
            for (int round = 0; round < 6 && len > maxChars; round++)
            {
                if (round < EventSteps.Length) Cut(obs, "events", EventSteps[round]);
                if (round >= 1 && round - 1 < ObjectSteps.Length) Cut(obs, "objects", ObjectSteps[round - 1]);
                if (round >= 2 && round - 2 < HazardSteps.Length) Cut(obs, "hazards", HazardSteps[round - 2]);
                if (round == 3) Slim(obs, "objects", "name", "state");
                if (round == 4) Slim(obs, "hazards", "name");
                len = CompactJson.Length(obs);
            }
            if (len > maxChars) { Cut(obs, "objects", 2); Cut(obs, "hazards", 1); Cut(obs, "events", 0); len = CompactJson.Length(obs); }
            return len;
        }

        static void Cut(Json.Obj obs, string key, int keep)
        {
            var l = obs[key] as List<object>;
            if (l == null || l.Count <= keep) return;
            int cut = l.Count - keep;
            obs[key] = l.GetRange(0, keep);
            JsonEdit.AddTruncated(obs, key, cut);
        }

        static void Slim(Json.Obj obs, string key, params string[] fields)
        {
            var l = obs[key] as List<object>;
            if (l == null) return;
            var r = new List<object>(l.Count);
            foreach (var x in l) { var o = x as Json.Obj; r.Add(o != null ? JsonEdit.Without(o, fields) : x); }
            obs[key] = r;
        }
    }

    internal static class HazardRank
    {
        // threat first, then the sooner eta (-1 = unknown sorts after every known eta), then the nearer one
        public static int Compare(bool threatA, double etaA, double distA, bool threatB, double etaB, double distB)
        {
            if (threatA != threatB) return threatA ? -1 : 1;
            bool ka = etaA >= 0, kb = etaB >= 0;
            if (ka != kb) return ka ? -1 : 1;
            if (ka && etaA != etaB) return etaA.CompareTo(etaB);
            return distA.CompareTo(distB);
        }

        // seconds until something closing at speed covers dist; -1 when it is not closing
        public static double Eta(double dist, double closingSpeed)
        {
            if (closingSpeed <= 0.5) return -1;
            return dist / closingSpeed;
        }

        // shockwave: seconds until the blow at blowAt within a cycle of length cycle, music position t
        public static double CycleEta(double t, double blowAt, double cycle)
        {
            if (cycle <= 0) return -1;
            double d = (blowAt - t) % cycle;
            if (d < 0) d += cycle;
            return d;
        }

        // landing speed after falling h metres from vertical speed vy (negative = down) under gravity g, and the time
        public static double LandingSpeed(double vy, double h, double g, out double seconds)
        {
            double v0 = Math.Max(0, -vy);
            seconds = -1;
            if (g <= 0 || h < 0) return v0;
            double v = Math.Sqrt(v0 * v0 + 2 * g * h);
            seconds = (v - v0) / g;
            return v;
        }
    }
}
