using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Shared by the list panels (Events, Audio Timeline, History, Logic, palette ...): the filtered index list of a panel
    // plus one cached text per shown row. A panel rebuilds the list only when its key changes (the write counter of a ring,
    // a history version, the filter text, the toggles) and builds a row text only for the rows that are really drawn.
    //   if (cache.Changed(counter, toggles, selection, filterText)) { for (...) if (wanted) cache.Add(i); }
    //   row callback:  string s = cache.Text(row) ?? cache.Text(row, Build(cache.idx[row]));
    public sealed class ListCache
    {
        public readonly List<int> idx = new List<int>(256);
        readonly List<string> text = new List<string>(256);
        long ka, kb, kc; string ks; bool valid;
        public int builds;   // how often the list was rebuilt

        public int Count { get { return idx.Count; } }

        // True when the key differs from the one the list was built for; the list is then empty and the caller fills it.
        public bool Changed(long a, long b, long c, string s)
        {
            if (valid && a == ka && b == kb && c == kc && s == ks) return false;
            ka = a; kb = b; kc = c; ks = s; valid = true;
            idx.Clear(); text.Clear(); builds++;
            return true;
        }

        public void Invalidate() { valid = false; }
        public void Add(int i) { idx.Add(i); text.Add(null); }
        // a list that shows every item: rows 0 .. n-1
        public void Fill(int n) { for (int i = 0; i < n; i++) { idx.Add(i); text.Add(null); } }

        public string Text(int row) { return row < text.Count ? text[row] : null; }
        public string Text(int row, string s) { if (row < text.Count) text[row] = s; return s; }
        // keeps the rows, forgets their texts (values that tick, such as "3 s ago")
        public void DropTexts() { for (int i = 0; i < text.Count; i++) text[i] = null; }
    }

    // A typed search runs when the typing pauses: Value returns the text that was last left alone for `delay` seconds.
    // An emptied box and the first call take effect at once.
    public struct SearchDelay
    {
        string last, settled; float changedAt;

        public string Value(string input, float delay)
        {
            if (input == null) input = "";
            if (settled == null) { settled = last = input; return settled; }
            if (input != last) { last = input; changedAt = Time.realtimeSinceStartup; }
            if (input != settled && (input.Length == 0 || Time.realtimeSinceStartup - changedAt >= delay)) settled = input;
            return settled;
        }

        // text set by code (a command, a click): no wait
        public void Set(string s) { last = settled = s ?? ""; }
    }

    // Counter that moves when the set of loaded objects changed: a database pass that added or removed something.
    // Panels key their caches on it instead of ObjectDatabase.completedPasses, which moves every second or two while the
    // editor is open although nothing changed.
    public static class SceneStamp
    {
        static int seenPass = -1, stamp;

        public static int Value
        {
            get
            {
                int p = ObjectDatabase.completedPasses;
                if (p != seenPass)
                {
                    // a pass nobody looked at may have carried the change: count it as one
                    if (seenPass < 0 || p - seenPass > 1 || ObjectDatabase.addedLastPass != 0 || ObjectDatabase.removedLastPass != 0) stamp++;
                    seenPass = p;
                }
                return stamp;
            }
        }
    }

    // "Is this a script of the game" (Assembly-CSharp, Assembly-CSharp-firstpass), remembered per type:
    // Assembly.GetName() builds a new AssemblyName on every call.
    public static class GameTypes
    {
        static readonly Dictionary<Type, bool> known = new Dictionary<Type, bool>();

        public static bool IsGame(Type t)
        {
            bool v;
            if (known.TryGetValue(t, out v)) return v;
            string an = t.Assembly.GetName().Name;
            v = an == "Assembly-CSharp" || an == "Assembly-CSharp-firstpass";
            known[t] = v;
            return v;
        }
    }
}
