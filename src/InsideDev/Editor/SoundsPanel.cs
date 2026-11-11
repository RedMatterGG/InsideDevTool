using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Phase 10 UI: one row per sound (Wwise event), "Used by n", live post counts; details with per-binding actions.
    public static class SoundsPanel
    {
        static string filter = "", overrideTo = "", status = "", listedFilter;
        static string selected;
        // show this sound: the list follows at once, not at the next periodic sort
        public static void Focus(string name) { selected = name; filter = name ?? ""; nextSort = 0; }
        static bool onlyUsed = true, onlyHeard, recentFirst;
        static readonly List<SoundLibrary.Def> rows = new List<SoundLibrary.Def>();
        static float nextSort;
        static readonly Color cField = new Color(0.65f, 0.68f, 0.72f, 1f), cFsm = new Color(0.72f, 0.55f, 1f, 1f), cObs = new Color(0.3f, 1f, 0.5f, 1f), cSelRow = new Color(0.22f, 0.37f, 0.6f, 0.75f);

        static string statusLine, statusFor; static float statusAt;
        static readonly Dictionary<SoundLibrary.Def, KeyValuePair<int, string>> rowText = new Dictionary<SoundLibrary.Def, KeyValuePair<int, string>>();
        static UI curUi; static float curNow;
        static readonly Action<int> drawRow = DrawRow;

        // texts of the selected sound's bindings: built when the selection or a binding changes and twice per second
        static readonly List<string> bindText = new List<string>();
        static SoundLibrary.Def bindFor; static float bindAt; static string bindHead, overrideLabel, overrideLabelFor;

        public static void Draw(UI ui)
        {
            Background.Want(Background.Audio, 5f);
            float now = Time.realtimeSinceStartup;
            ui.BeginRow();
            ui.Label("Find:", null, 34);
            EditorInput.Field(ui, "snd_find", ref filter, Mathf.Max(80, ui.Width - 260));
            bool u = ui.Toggle(onlyUsed, "in scene"); if (u != onlyUsed) { onlyUsed = u; nextSort = 0; }
            bool h = ui.Toggle(onlyHeard, "heard"); if (h != onlyHeard) { onlyHeard = h; nextSort = 0; }
            bool rf = ui.Toggle(recentFirst, "recent first"); if (rf != recentFirst) { recentFirst = rf; nextSort = 0; }
            if (ui.Button("Stop auditions")) SoundLibrary.StopAuditions();
            ui.EndRow();
            if (statusLine == null || now - statusAt > 1f || statusFor != status) { statusFor = status; statusAt = now; statusLine = SoundLibrary.Status() + (status.Length > 0 ? "   |   " + status : ""); }
            ui.Label(statusLine, UI.Dim);

            // typing in Find lists at once; otherwise the list is sorted again on a timer so rows do not move under the mouse
            if ((object)filter != (object)listedFilter && filter != listedFilter) nextSort = 0;
            if (now > nextSort)
            {
                nextSort = now + (recentFirst ? 0.5f : 2f);
                listedFilter = filter;
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
            curUi = ui; curNow = now;
            ui.VirtualList("snd_rows", rows.Count, UI.RowPitch, listH, drawRow);
            if (sel == null) return;

            if (bindFor != sel || bindText.Count != sel.bindings.Count || now - bindAt > 0.5f)
            {
                bindFor = sel; bindAt = now; bindText.Clear();
                bindHead = sel.name + "  —  " + sel.bindings.Count + " binding(s) on " + sel.UsedBy + " object(s)" + (sel.lastSource.Length > 0 ? ",  last posted from " + sel.lastSource : "");
                foreach (var b in sel.bindings)
                    bindText.Add("[" + b.prov.ToString().ToUpperInvariant() + "] " + (b.go != null ? b.go.name : "(gone)") + "   " + b.where + (b.postTarget != null ? "   (plays on " + b.postTarget.name + ")" : "") + (b.observedPosts > 0 ? "   heard " + b.observedPosts + "x" : "") + (b.original != null ? "   OVERRIDDEN (was '" + b.original + "')" : ""));
            }
            ui.Label(bindHead, UI.Accent);
            ui.BeginRow();
            ui.Label("Override selected binding with:", null, 190);
            EditorInput.Field(ui, "snd_ovr", ref overrideTo, Mathf.Max(80, ui.Width - 4));
            ui.EndRow();
            string ovr = overrideTo.Trim();
            if (ovr.Length > 0 && overrideLabelFor != ovr) { overrideLabelFor = ovr; overrideLabel = "Override -> " + ovr; }
            ui.BeginScroll("snd_detail", ui.Remaining);
            for (int i = 0; i < sel.bindings.Count && i < bindText.Count; i++)
            {
                var b = sel.bindings[i];
                var c = b.prov == SoundLibrary.Prov.Fsm ? cFsm : b.prov == SoundLibrary.Prov.Observed ? cObs : cField;
                ui.BeginRow();
                bool ck = ui.Item(bindText[i], c);
                if (ui.LastHover && b.go != null) Selection.SetHover(b.go);
                ui.EndRow();
                if (ck && b.go != null) Selection.Set(b.go, "sounds");
                if (b.go == null) continue;
                ui.BeginRow();
                ui.Space(18);
                if (ui.Button("Post from here")) status = SoundLibrary.PostFrom(b, SoundLibrary.CurrentName(b) ?? sel.name);
                if (SoundLibrary.CanTriggerOriginal(b) && ui.Button("Trigger original")) status = SoundLibrary.TriggerOriginal(b);
                if (b.prov != SoundLibrary.Prov.Observed && ovr.Length > 0 && ui.Button(overrideLabel)) { status = SoundLibrary.Override(b, ovr); bindAt = 0f; }
                if (b.original != null && ui.Button("Revert")) { status = SoundLibrary.Revert(b); bindAt = 0f; }
                ui.EndRow();
            }
            ui.Label("Audition = hear it on a neutral emitter (no game logic).  Post from here = same sound at that object.  Trigger original = the game's own path (state machine / trigger) - may run other logic too.  Override = this binding plays another event until reverted.", UI.Dim);
            ui.EndScroll();
        }

        static void DrawRow(int i)
        {
            if (i >= rows.Count) return;
            var ui = curUi; float now = curNow;
            var d = rows[i];
            if (d.name == selected) ui.RowHighlight(cSelRow);
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
        }
    }
}
