using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InsideDev.Play
{
    // NotesCore.cs (owner GUIDE): the Unity-free part of Notes.cs. A note is one flagged moment; the ring keeps the newest
    // 50, ids grow and are never reused within the file, one compact JSON object per line in _mod/notes.jsonl.
    internal sealed class NoteEntry
    {
        public int id;
        public string time = "", checkpoint = "", text = "", screenshot = "";
        public long frame, step;
        public int subscene = -1, savepoint = -1;
        public double x, y, z;
        public bool hasPos;

        public Json.Obj ToJson()
        {
            var o = new Json.Obj();
            o["id"] = (double)id;
            o["time"] = time ?? "";
            o["frame"] = (double)frame;
            o["step"] = (double)step;
            o["subscene"] = (double)subscene;
            o["savepoint"] = (double)savepoint;
            o["checkpoint"] = checkpoint ?? "";
            o["pos"] = hasPos ? (object)new List<object> { Math.Round(x, 2), Math.Round(y, 2), Math.Round(z, 2) } : null;
            o["text"] = text ?? "";
            o["screenshot"] = screenshot ?? "";
            return o;
        }

        public static NoteEntry FromJson(Json.Obj o)
        {
            if (o == null || !(o["id"] is double)) return null;
            var e = new NoteEntry
            {
                id = (int)(double)o["id"],
                time = o.Str("time", ""),
                frame = (long)o.Num("frame", 0),
                step = (long)o.Num("step", 0),
                subscene = (int)o.Num("subscene", -1),
                savepoint = (int)o.Num("savepoint", -1),
                checkpoint = o.Str("checkpoint", ""),
                text = o.Str("text", ""),
                screenshot = o.Str("screenshot", "")
            };
            var p = o["pos"] as List<object>;
            if (p != null && p.Count == 3 && p[0] is double && p[1] is double && p[2] is double)
            {
                e.hasPos = true;
                e.x = (double)p[0]; e.y = (double)p[1]; e.z = (double)p[2];
            }
            return e;
        }
    }

    internal sealed class NoteRing
    {
        public const int DefaultCapacity = 50, MaxId = 1000000;
        readonly int capacity;
        readonly List<NoteEntry> items = new List<NoteEntry>();
        int nextId = 1;

        public NoteRing() : this(DefaultCapacity) { }
        public NoteRing(int capacity) { this.capacity = Math.Max(1, capacity); }

        public int Count { get { return items.Count; } }
        public int NextId { get { return nextId; } }

        // assigns the id; notes pushed out of the ring are appended to evicted
        public NoteEntry Add(NoteEntry e, List<NoteEntry> evicted)
        {
            e.id = nextId;
            nextId = nextId >= MaxId ? 1 : nextId + 1;
            items.Add(e);
            while (items.Count > capacity)
            {
                if (evicted != null) evicted.Add(items[0]);
                items.RemoveAt(0);
            }
            return e;
        }

        public NoteEntry Get(int id)
        {
            foreach (var e in items) if (e.id == id) return e;
            return null;
        }

        public List<NoteEntry> Newest(int limit)
        {
            var l = new List<NoteEntry>();
            for (int i = items.Count - 1; i >= 0 && l.Count < limit; i--) l.Add(items[i]);
            return l;
        }

        public List<NoteEntry> Clear()
        {
            var l = new List<NoteEntry>(items);
            items.Clear();
            return l;
        }

        // lines of notes.jsonl; bad lines are skipped and counted. Returns true when the ring dropped lines (the file
        // should be rewritten).
        public bool Load(IEnumerable<string> lines, out int bad)
        {
            bad = 0;
            items.Clear();
            int max = 0, total = 0;
            foreach (var raw in lines)
            {
                string line = raw == null ? "" : raw.Trim();
                if (line.Length == 0) continue;
                NoteEntry e = null;
                try { e = NoteEntry.FromJson(Json.Parse(line) as Json.Obj); } catch (Exception) { }
                if (e == null) { bad++; continue; }
                total++;
                items.Add(e);
                if (e.id > max) max = e.id;
            }
            while (items.Count > capacity) items.RemoveAt(0);
            nextId = max >= MaxId ? 1 : max + 1;
            return bad > 0 || total > capacity;
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            foreach (var e in items) sb.Append(Line(e)).Append('\n');
            return sb.ToString();
        }

        public static string Line(NoteEntry e) { return JsonLine.Compact(e.ToJson()); }
    }

    internal static class JsonLine
    {
        // one line of JSON: the core writer is pretty-printed, and raw newlines occur only as formatting there (string
        // newlines are escaped), so dropping them with the indentation that follows is exact
        public static string Compact(object v)
        {
            string pretty = Json.Write(v);
            var sb = new StringBuilder(pretty.Length);
            bool skip = false;
            foreach (char c in pretty)
            {
                if (c == '\n') { skip = true; continue; }
                if (c == '\r') continue;
                if (skip && c == ' ') continue;
                skip = false;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
