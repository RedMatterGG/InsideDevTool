using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Phase 10 UI: one row per sound (Wwise event), "Used by n", live post counts; details with per-binding actions.
    public static class SoundsPanel
    {
        static string filter = "", overrideTo = "", status = "";
        static string selected;
        public static void Focus(string name) { selected = name; filter = name; }
        static bool onlyUsed = true, onlyHeard, recentFirst;
        static readonly List<SoundLibrary.Def> rows = new List<SoundLibrary.Def>();
        static float nextSort;
        static readonly Color cField = new Color(0.65f, 0.68f, 0.72f, 1f), cFsm = new Color(0.72f, 0.55f, 1f, 1f), cObs = new Color(0.3f, 1f, 0.5f, 1f);

        static string statusLine, statusFor; static float statusAt;
        static readonly Dictionary<SoundLibrary.Def, KeyValuePair<int, string>> rowText = new Dictionary<SoundLibrary.Def, KeyValuePair<int, string>>();
        public static void Draw(UI ui)
        {
            ui.BeginRow();
            ui.Label("Find:", null, 34);
            ui.TextField("snd_find", ref filter, Mathf.Max(80, ui.Width - 260));
            bool u = ui.Toggle(onlyUsed, "in scene"); if (u != onlyUsed) { onlyUsed = u; nextSort = 0; }
            bool h = ui.Toggle(onlyHeard, "heard"); if (h != onlyHeard) { onlyHeard = h; nextSort = 0; }
            bool rf = ui.Toggle(recentFirst, "recent first"); if (rf != recentFirst) { recentFirst = rf; nextSort = 0; }
            if (ui.Button("Stop auditions")) SoundLibrary.StopAuditions();
            ui.EndRow();
            if (statusLine == null || Time.realtimeSinceStartup - statusAt > 1f || statusFor != status) { statusFor = status; statusAt = Time.realtimeSinceStartup; statusLine = SoundLibrary.Status() + (status.Length > 0 ? "   |   " + status : ""); }
            ui.Label(statusLine, UI.Dim);

            if (Time.realtimeSinceStartup > nextSort)
            {
                nextSort = Time.realtimeSinceStartup + (recentFirst ? 0.5f : 2f);
                rows.Clear();
                string f = filter.Trim();
                foreach (var d in SoundLibrary.defs.Values)
                {
                    if (onlyUsed && d.bindings.Count == 0) continue;
                    if (onlyHeard && d.posts == 0) continue;
                    if (f.Length > 0 && d.name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    rows.Add(d);
                }
                // alphabetical by default so rows don't move under the mouse; "recent first" re-sorts live
                if (recentFirst) rows.Sort((a, b) => b.lastPost != a.lastPost ? b.lastPost.CompareTo(a.lastPost) : string.CompareOrdinal(a.name, b.name));
                else rows.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            }

            SoundLibrary.Def sel = null;
            if (selected != null) SoundLibrary.defs.TryGetValue(selected, out sel);
            float listH = sel != null ? Mathf.Max(UI.ItemPitch * 4, ui.Remaining * 0.45f) : ui.Remaining;
            float now = Time.realtimeSinceStartup;
            var rs = rows;
            ui.VirtualList("snd_rows", rs.Count, UI.RowPitch, listH, i =>
            {
                var d = rs[i];
                if (d.name == selected) ui.RowHighlight(new Color(0.22f, 0.37f, 0.6f, 0.75f));
                ui.BeginRow();
                if (ui.Button("Audition", 70)) status = SoundLibrary.Audition(d.name);
                bool fresh = d.lastPost > 0 && now - d.lastPost < 1.5f;
                // row text cached per definition; rebuilt when its counters change or once per second while "s ago" ticks
                int key = d.posts * 7919 + d.bindings.Count * 131 + d.auditions * 17 + (d.lastPost > 0 ? (int)(now - d.lastPost) : -1);
                KeyValuePair<int, string> lab;
                if (!rowText.TryGetValue(d, out lab) || lab.Key != key)
                {
                    lab = new KeyValuePair<int, string>(key, d.name + "   used by " + d.UsedBy + "   posts " + d.posts + (d.lastPost > 0 ? "   " + (now - d.lastPost).ToString("0") + " s ago" : "") + (d.auditions > 0 ? "   auditioned " + d.auditions : ""));
                    rowText[d] = lab;
                }
                if (ui.Item(lab.Value, fresh ? Color.white : d.bindings.Count > 0 ? UI.Txt : UI.Dim))
                { selected = d.name; overrideTo = ""; }
                ui.EndRow();
            });
            if (sel == null) return;

            ui.Label(sel.name + "  —  " + sel.bindings.Count + " binding(s) on " + sel.UsedBy + " object(s)" + (sel.lastSource.Length > 0 ? ",  last posted from " + sel.lastSource : ""), UI.Accent);
            ui.BeginRow();
            ui.Label("Override selected binding with:", null, 190);
            ui.TextField("snd_ovr", ref overrideTo, Mathf.Max(80, ui.Width - 4));
            ui.EndRow();
            ui.BeginScroll("snd_detail", ui.Remaining);
            foreach (var b in sel.bindings)
            {
                var c = b.prov == SoundLibrary.Prov.Fsm ? cFsm : b.prov == SoundLibrary.Prov.Observed ? cObs : cField;
                ui.BeginRow();
                bool ck = ui.Item("[" + b.prov.ToString().ToUpperInvariant() + "] " + (b.go != null ? b.go.name : "(gone)") + "   " + b.where + (b.postTarget != null ? "   (plays on " + b.postTarget.name + ")" : "") + (b.observedPosts > 0 ? "   heard " + b.observedPosts + "x" : "") + (b.original != null ? "   OVERRIDDEN (was '" + b.original + "')" : ""), c);
                if (ui.LastHover && b.go != null) Selection.SetHover(b.go);
                ui.EndRow();
                if (ck && b.go != null) Selection.Set(b.go, "sounds");
                if (b.go == null) continue;
                ui.BeginRow();
                ui.Space(18);
                if (ui.Button("Post from here")) status = SoundLibrary.PostFrom(b, SoundLibrary.CurrentName(b) ?? sel.name);
                if (SoundLibrary.CanTriggerOriginal(b) && ui.Button("Trigger original")) status = SoundLibrary.TriggerOriginal(b);
                if (b.prov != SoundLibrary.Prov.Observed && overrideTo.Trim().Length > 0 && ui.Button("Override -> " + overrideTo.Trim())) status = SoundLibrary.Override(b, overrideTo.Trim());
                if (b.original != null && ui.Button("Revert")) status = SoundLibrary.Revert(b);
                ui.EndRow();
            }
            ui.Label("Audition = hear it on a neutral emitter (no game logic).  Post from here = same sound at that object.  Trigger original = the game's own path (state machine / trigger) - may run other logic too.  Override = this binding plays another event until reverted.", UI.Dim);
            ui.EndScroll();
        }
    }
}
