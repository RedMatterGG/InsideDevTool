using System;
using System.Collections.Generic;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // RestoreCore.cs (owner TIME): the Unity-free restore registry behind Restorer, compiled into InsideDev.Play.dll and
    // into the offline tests (qa/mcp/TIME). Thread-safe. Register keeps the first undo of a key (it holds the original
    // state); RunAll runs the matching undos newest first, each in try/catch. An entry leaves the registry before its
    // undo runs (so an undo may Forget or re-register its key); a failed undo goes back in its place for the next
    // restore unless the key was registered again meanwhile. "save_lock" runs only with includeSaveLock. only = exact
    // keys or key prefixes ("hazard:", "stream_pin"); empty = all.
    internal sealed class RestoreBook
    {
        public const string SaveLock = "save_lock";

        sealed class Entry
        {
            public string key, what;
            public Func<string> undo;
        }

        readonly object gate = new object();
        readonly List<Entry> entries = new List<Entry>();

        public void Register(string key, string what, Func<string> undo)
        {
            if (string.IsNullOrEmpty(key) || undo == null) return;
            lock (gate)
            {
                foreach (var e in entries) if (e.key == key) return;
                entries.Add(new Entry { key = key, what = what ?? key, undo = undo });
            }
        }

        public void Forget(string key)
        {
            lock (gate) { entries.RemoveAll(e => e.key == key); }
        }

        public bool IsSet(string key)
        {
            lock (gate) { foreach (var e in entries) if (e.key == key) return true; }
            return false;
        }

        public List<string> Keys()
        {
            var l = new List<string>();
            lock (gate) { foreach (var e in entries) l.Add(e.key); }
            return l;
        }

        public int Count { get { lock (gate) { return entries.Count; } } }

        public static bool Matches(string key, List<string> only)
        {
            if (only == null || only.Count == 0) return true;
            foreach (var p in only) if (!string.IsNullOrEmpty(p) && (key == p || key.StartsWith(p, StringComparison.Ordinal))) return true;
            return false;
        }

        // main thread in the game; {reset[{key, what, result}], kept[], errors[]}
        public Json.Obj RunAll(List<string> only, bool includeSaveLock, ICollection<string> exclude)
        {
            List<Entry> snap;
            lock (gate) { snap = new List<Entry>(entries); }
            var reset = new List<object>();
            var kept = new List<string>();
            var errors = new List<object>();
            for (int i = snap.Count - 1; i >= 0; i--)
            {
                var e = snap[i];
                if (!Matches(e.key, only) || (exclude != null && exclude.Contains(e.key)) || (e.key == SaveLock && !includeSaveLock)) { kept.Insert(0, e.key); continue; }
                int at;
                lock (gate) { at = entries.IndexOf(e); if (at >= 0) entries.RemoveAt(at); }
                if (at < 0) continue;
                string result;
                try { result = e.undo(); }
                catch (Exception ex)
                {
                    errors.Add(e.key + ": " + ex.GetType().Name + ": " + ex.Message);
                    lock (gate)
                    {
                        bool again = false;
                        foreach (var x in entries) if (x.key == e.key) again = true;
                        if (!again) entries.Insert(Math.Min(at, entries.Count), e);
                    }
                    continue;
                }
                var o = new Json.Obj();
                o["key"] = e.key;
                o["what"] = e.what;
                o["result"] = string.IsNullOrEmpty(result) ? "done" : result;
                reset.Add(o);
            }
            var r = new Json.Obj();
            r["reset"] = reset;
            var k = new List<object>();
            foreach (var s in kept) k.Add(s);
            r["kept"] = k;
            r["errors"] = errors;
            return r;
        }

        public static Json.Obj Row(string key, string what, string result)
        {
            var o = new Json.Obj();
            o["key"] = key;
            o["what"] = what;
            o["result"] = result ?? "done";
            return o;
        }
    }
}
