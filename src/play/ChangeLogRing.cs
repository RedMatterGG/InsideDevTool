using System;
using System.Collections.Generic;
using System.Globalization;

namespace InsideDev.Play
{
    // ChangeLogRing.cs (owner OBSERVE): the Unity-free event ring behind ChangeLog, compiled directly by the offline
    // tests. Entries get a sequence number that only grows; Since(mark) groups the entries after mark into the changes
    // lists, capped per list (first half and last half kept, the middle counted in truncated), Events(mark) merges them
    // into one step-ordered list for observe.events (deaths, loads, checkpoints, errors and notes are kept first).
    // Repeated identical errors collapse into one entry with a count unless a Mark was handed out in between.
    // Compact(mark) is the default "changes" of action results: signal / fsm entries flagged far (more than 15 m from
    // the body when they fired) are only counted in far{}, every list is capped and the whole object is kept under a
    // character budget by cutting signals, fsm, toggled and other first (deaths, checkpoints, loads, errors stay).
    internal sealed class ChangeEntry
    {
        public long seq, step;
        public string type, list, detail;
        public Json.Obj item;
        public int repeat = 1;
        public bool far;
    }

    internal sealed class ChangeRing
    {
        public static readonly string[] Lists = { "signals", "fsm", "toggled", "errors", "checkpoints", "deaths", "loads" };
        public static readonly string[] Priority = { "death", "respawn", "load_started", "load_finished", "checkpoint", "error", "note", "input_hijacked", "stalled" };
        public const string OtherList = "other";

        readonly object gate = new object();
        readonly ChangeEntry[] ring;
        int head, count;
        long seq, lastMarked;
        ChangeEntry lastError;

        public ChangeRing(int capacity) { ring = new ChangeEntry[Math.Max(16, capacity)]; }

        public int Capacity { get { return ring.Length; } }
        public long Seq { get { lock (gate) return seq; } }
        public int Count { get { lock (gate) return count; } }

        public long Mark()
        {
            lock (gate)
            {
                lastMarked = seq;
                return seq;
            }
        }

        public long Add(string type, string list, long step, string detail, Json.Obj item)
        {
            return Add(type, list, step, detail, item, false);
        }

        // far: the entry happened away from the body (Compact counts it instead of listing it)
        public long Add(string type, string list, long step, string detail, Json.Obj item, bool far)
        {
            lock (gate)
            {
                var e = new ChangeEntry { seq = ++seq, step = step, type = type, list = list, detail = detail ?? "", item = item, far = far };
                ring[head] = e;
                head = (head + 1) % ring.Length;
                if (count < ring.Length) count++;
                if (type == "error") lastError = e;
                return e.seq;
            }
        }

        // an error; the same message again collapses into the previous entry while no Mark was taken after it
        public long AddError(string kind, string message, long step)
        {
            lock (gate)
            {
                if (lastError != null && lastError.seq > lastMarked && lastError.item != null && lastError.item.Str("message", "") == message && lastError.item.Str("type", "") == kind)
                {
                    lastError.repeat++;
                    return lastError.seq;
                }
            }
            var item = new Json.Obj();
            item["step"] = (double)step;
            item["type"] = kind;
            item["message"] = message ?? "";
            return Add("error", "errors", step, kind + ": " + message, item);
        }

        List<ChangeEntry> After(long mark, out long lost)
        {
            var r = new List<ChangeEntry>();
            lock (gate)
            {
                long oldest = count == 0 ? seq + 1 : ring[(head - count + ring.Length) % ring.Length].seq;
                lost = Math.Max(0, oldest - 1 - mark);
                for (int i = count - 1; i >= 0; i--)
                {
                    var e = ring[(head - 1 - i + ring.Length * 2) % ring.Length];
                    if (e != null && e.seq > mark) r.Add(e);
                }
            }
            return r;
        }

        static Json.Obj ItemOf(ChangeEntry e)
        {
            if (e.repeat <= 1 || e.item == null) return e.item;
            var c = new Json.Obj();
            foreach (var k in e.item.order) if (e.item.ContainsKey(k)) c[k] = e.item[k];
            c["message"] = e.item.Str("message", "") + " (x" + e.repeat.ToString(CultureInfo.InvariantCulture) + ")";
            c["repeat"] = (double)e.repeat;
            return c;
        }

        public Json.Obj Since(long mark, int capPerList)
        {
            long lost;
            var es = After(mark, out lost);
            int cap = Math.Max(1, capPerList);
            var groups = new Dictionary<string, List<ChangeEntry>>();
            foreach (var n in Lists) groups[n] = new List<ChangeEntry>();
            var other = new List<ChangeEntry>();
            foreach (var e in es)
            {
                if (e.list == null || e.item == null) { if (e.list == OtherList || e.list == null) other.Add(e); continue; }
                List<ChangeEntry> g;
                if (groups.TryGetValue(e.list, out g)) g.Add(e); else other.Add(e);
            }
            var o = new Json.Obj();
            var trunc = new Json.Obj();
            foreach (var n in Lists)
            {
                var g = groups[n];
                var l = new List<object>();
                foreach (var e in Capped(g, cap)) l.Add(ItemOf(e));
                o[n] = l;
                if (g.Count > cap) trunc[n] = (double)(g.Count - cap);
            }
            if (other.Count > 0)
            {
                var l = new List<object>();
                foreach (var e in Capped(other, cap))
                {
                    var x = new Json.Obj();
                    x["step"] = (double)e.step;
                    x["type"] = e.type;
                    x["detail"] = e.detail;
                    l.Add(x);
                }
                o[OtherList] = l;
                if (other.Count > cap) trunc[OtherList] = (double)(other.Count - cap);
            }
            if (lost > 0) trunc["lost"] = (double)lost;
            o["truncated"] = trunc;
            return o;
        }

        public static readonly string[] Cuttable = { "signals", "fsm", "toggled", OtherList };

        // entries after mark of the given types that are not far and whose type|detail is new to seen (added)
        public int NovelAfter(long mark, string[] types, HashSet<string> seen)
        {
            long lost;
            int n = 0;
            if (seen.Count > 4000) seen.Clear();
            foreach (var e in After(mark, out lost))
            {
                if (e.far || Array.IndexOf(types, e.type) < 0) continue;
                if (seen.Add(e.type + "|" + e.detail)) n++;
            }
            return n;
        }

        // the action-result view: near entries only for signals / fsm (far ones counted in far{}), cap per list, then
        // entries cut from the Cuttable lists (longest first) until the estimated JSON size is under maxChars
        public Json.Obj Compact(long mark, int capPerList, int maxChars)
        {
            long lost;
            var es = After(mark, out lost);
            int cap = Math.Max(1, capPerList);
            var groups = new Dictionary<string, List<ChangeEntry>>();
            foreach (var n in Lists) groups[n] = new List<ChangeEntry>();
            groups[OtherList] = new List<ChangeEntry>();
            var far = new Dictionary<string, int>();
            foreach (var e in es)
            {
                string list = e.list == null || e.item == null || !groups.ContainsKey(e.list) ? OtherList : e.list;
                if (e.far && list != OtherList)
                {
                    int n;
                    far.TryGetValue(list, out n);
                    far[list] = n + 1;
                    continue;
                }
                groups[list].Add(e);
            }
            var trunc = new Json.Obj();
            var kept = new Dictionary<string, List<ChangeEntry>>();
            foreach (var kv in groups)
            {
                var c = Capped(kv.Value, cap);
                kept[kv.Key] = new List<ChangeEntry>(c);
                if (kv.Value.Count > cap) trunc[kv.Key] = (double)(kv.Value.Count - cap);
            }
            Json.Obj o = null;
            for (int guard = 0; guard < 400; guard++)
            {
                o = Render(kept, trunc, far, lost);
                if (maxChars <= 0 || Size(o) <= maxChars) break;
                string longest = null;
                foreach (var n in Cuttable) if (kept[n].Count > 0 && (longest == null || kept[n].Count > kept[longest].Count)) longest = n;
                if (longest == null) break;
                var l = kept[longest];
                l.RemoveAt(l.Count / 2);
                trunc[longest] = (trunc[longest] is double ? (double)trunc[longest] : 0) + 1;
            }
            return o;
        }

        static Json.Obj Render(Dictionary<string, List<ChangeEntry>> kept, Json.Obj trunc, Dictionary<string, int> far, long lost)
        {
            var o = new Json.Obj();
            foreach (var n in Lists)
            {
                var l = new List<object>();
                foreach (var e in kept[n]) l.Add(ItemOf(e));
                o[n] = l;
            }
            if (kept[OtherList].Count > 0)
            {
                var l = new List<object>();
                foreach (var e in kept[OtherList])
                {
                    var x = new Json.Obj();
                    x["step"] = (double)e.step;
                    x["type"] = e.type;
                    x["detail"] = e.detail;
                    l.Add(x);
                }
                o[OtherList] = l;
            }
            var f = new Json.Obj();
            foreach (var n in Lists) { int c; if (far.TryGetValue(n, out c)) f[n] = (double)c; }
            o["far"] = f;
            var t = new Json.Obj();
            foreach (var k in trunc.order) if (trunc.ContainsKey(k)) t[k] = trunc[k];
            if (lost > 0) t["lost"] = (double)lost;
            o["truncated"] = t;
            return o;
        }

        // estimated compact JSON length
        public static int Size(object v)
        {
            if (v == null) return 4;
            var s = v as string;
            if (s != null) return s.Length + 2;
            if (v is bool) return 5;
            if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture).Length;
            var o = v as Json.Obj;
            if (o != null)
            {
                int n = 2;
                foreach (var k in o.order) if (o.ContainsKey(k)) n += k.Length + 4 + Size(o[k]);
                return n;
            }
            var l = v as List<object>;
            if (l != null)
            {
                int n = 2;
                foreach (var x in l) n += Size(x) + 1;
                return n;
            }
            return v.ToString().Length;
        }

        // first half and last half of a list longer than cap, in order
        public static List<T> Capped<T>(List<T> l, int cap)
        {
            if (l.Count <= cap) return l;
            int first = (cap + 1) / 2, last = cap - first;
            var r = new List<T>(cap);
            r.AddRange(l.GetRange(0, first));
            r.AddRange(l.GetRange(l.Count - last, last));
            return r;
        }

        public List<Json.Obj> Events(long mark, int cap, out int cut)
        {
            return Events(mark, long.MaxValue, cap, false, out cut);
        }

        // entries in (mark, upTo]; priorityOnly keeps only Priority types (the concise observation inside action
        // results, whose changes already list signals, fsm and toggles)
        public List<Json.Obj> Events(long mark, long upTo, int cap, bool priorityOnly, out int cut)
        {
            long lost;
            var es = After(mark, out lost);
            if (upTo < long.MaxValue || priorityOnly) es.RemoveAll(e => e.seq > upTo || (priorityOnly && !IsPriority(e.type)));
            cut = 0;
            cap = Math.Max(0, cap);
            List<ChangeEntry> keep;
            if (es.Count <= cap) keep = es;
            else
            {
                var pri = new List<ChangeEntry>();
                var rest = new List<ChangeEntry>();
                foreach (var e in es) (IsPriority(e.type) ? pri : rest).Add(e);
                if (pri.Count >= cap) keep = pri.GetRange(pri.Count - cap, cap);
                else
                {
                    keep = new List<ChangeEntry>(pri);
                    int room = cap - pri.Count;
                    keep.AddRange(rest.GetRange(rest.Count - room, room));
                }
                keep.Sort((a, b) => a.seq.CompareTo(b.seq));
                cut = es.Count - keep.Count;
            }
            var r = new List<Json.Obj>(keep.Count);
            foreach (var e in keep)
            {
                var x = new Json.Obj();
                x["step"] = (double)e.step;
                x["type"] = e.type;
                x["detail"] = e.repeat > 1 ? e.detail + " (x" + e.repeat.ToString(CultureInfo.InvariantCulture) + ")" : e.detail;
                r.Add(x);
            }
            return r;
        }

        // entries after mark whose type is one of types (progress detection for the stuck counter)
        public int CountAfter(long mark, string[] types)
        {
            long lost;
            int n = 0;
            foreach (var e in After(mark, out lost))
                foreach (var t in types) if (e.type == t) { n++; break; }
            return n;
        }

        public static bool IsPriority(string type)
        {
            foreach (var p in Priority) if (p == type) return true;
            return false;
        }
    }
}
