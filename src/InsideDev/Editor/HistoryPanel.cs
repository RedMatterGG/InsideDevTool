using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Phase 11 UI: History (undo / redo / revert), net changes, Record as mod, installed mods, bookmarks.
    // Explore mode shows history and bookmarks; Mod Author mode adds selectors, Record as mod and the mod manager.
    public static class HistoryPanel
    {
        public static bool authorMode;
        static int tab;
        static readonly string[] tabs = { "History", "Changes", "Mods", "Bookmarks" };
        static string modName = "", modDesc = "", status = "", bmNote = "";
        static readonly HashSet<string> excluded = new HashSet<string>();      // net-change keys left out of the mod
        static readonly HashSet<int> actionIn = new HashSet<int>();            // action entries included in the mod
        static string openMod;

        static readonly Color cAct = new Color(1f, 0.72f, 0.3f, 1f), cUndone = new Color(0.45f, 0.47f, 0.52f, 1f), cOk = new Color(0.5f, 1f, 0.6f, 1f), cBad = new Color(1f, 0.45f, 0.4f, 1f);

        public static void Draw(UI ui)
        {
            ui.BeginRow();
            bool am = ui.Toggle(authorMode, "Mod Author mode");
            if (am != authorMode) { authorMode = am; EditorState.Set("mode.author", am); }
            if (ui.Button("Undo  (Ctrl+Z)")) status = ChangeRecorder.Undo();
            if (ui.Button("Redo  (Ctrl+Y)")) status = ChangeRecorder.Redo();
            ui.EndRow();
            string[] shown = authorMode ? tabs : new[] { "History", "Changes", "Bookmarks" };
            int ti = Array.IndexOf(shown, tabs[tab]); if (ti < 0) ti = 0;
            ti = ui.Tabs(ti, shown);
            tab = Array.IndexOf(tabs, shown[ti]);
            if (status.Length > 0) ui.Label(status, cOk);
            switch (tab)
            {
                case 0: History(ui); break;
                case 1: NetChanges(ui); break;
                case 2: ModsTab(ui); break;
                case 3: BookmarksTab(ui); break;
            }
        }

        // ---------------------------------------------------------------- history
        static void History(UI ui)
        {
            var h = ChangeRecorder.Entries;
            int cur = ChangeRecorder.Cursor;
            ui.Label(h.Count + " entries" + (cur < h.Count ? ", " + (h.Count - cur) + " undone (redo available)" : "") + ".  Actions (orange) are recorded but cannot be undone.", UI.Dim);
            ui.VirtualList("hist_list", h.Count, UI.ItemPitch, ui.Remaining, row =>
            {
                int i = h.Count - 1 - row;
                var e = h[i];
                bool undone = i >= cur;
                var c = undone ? cUndone : e.IsAction ? cAct : UI.Txt;
                string when = (Time.realtimeSinceStartup - e.t) < 120f ? ((int)(Time.realtimeSinceStartup - e.t)) + "s ago" : ((int)((Time.realtimeSinceStartup - e.t) / 60f)) + "m ago";
                bool ck = ui.Item((undone ? "(undone) " : "") + "#" + e.id + "  " + (e.IsAction ? "ACTION  " : "") + e.Describe() + "   [" + e.source + ", " + when + "]", c);
                GameObject go = e.action != null ? e.action.go : e.ops.Count > 0 ? e.ops[0].prop.go : null;
                if (ui.LastHover && go != null) Selection.SetHover(go);
                if (ck && go != null) Selection.Set(go, "history");
            });
        }

        // ---------------------------------------------------------------- net changes + record as mod
        static void NetChanges(UI ui)
        {
            var net = ChangeRecorder.NetChanges();
            ui.BeginRow();
            ui.Label(net.Count + " property value(s) differ from the game's original", UI.Dim, ui.Width - 100);
            if (net.Count > 0 && ui.Button("Revert all", 90)) status = ChangeRecorder.RevertAll();
            ui.EndRow();
            float listH = authorMode ? Mathf.Max(UI.RowPitch * 3, ui.Remaining - 190f) : ui.Remaining;
            ui.VirtualList("net_list", net.Count, UI.RowPitch, listH, i =>
            {
                var n = net[i];
                ui.BeginRow();
                if (authorMode) { bool inc = !excluded.Contains(n.prop.key); bool ni = ui.Toggle(inc, "", 20); if (ni != inc) { if (ni) excluded.Remove(n.prop.key); else excluded.Add(n.prop.key); } }
                if (ui.Button("Revert", 60)) status = ChangeRecorder.Revert(n);
                bool ck = ui.Item(n.prop.Label + ":  " + ChangeRecorder.Short(n.original) + "  ->  " + ChangeRecorder.Short(n.current) + (authorMode ? "      " + Selector(n.prop.go) : ""), Changes.Purple);
                if (ui.LastHover) Selection.SetHover(n.prop.go);
                if (ck) Selection.Set(n.prop.go, "history");
                ui.EndRow();
            });
            if (!authorMode) return;

            // actions that can go into the mod (replayed on load)
            var acts = new List<ChangeRecorder.Entry>();
            var h = ChangeRecorder.Entries;
            for (int i = 0; i < ChangeRecorder.Cursor; i++) if (h[i].IsAction) acts.Add(h[i]);
            if (acts.Count > 0)
            {
                ui.Label("Actions (tick to replay them on load in the mod):", UI.Dim);
                for (int i = 0; i < acts.Count; i++)
                {
                    var e = acts[i];
                    ui.BeginRow();
                    bool inc = actionIn.Contains(e.id); bool ni = ui.Toggle(inc, "", 20); if (ni != inc) { if (ni) actionIn.Add(e.id); else actionIn.Remove(e.id); }
                    ui.Label("#" + e.id + " " + e.label, cAct);
                    ui.EndRow();
                }
            }
            ui.Label("RECORD AS MOD", new Color(0.55f, 0.9f, 0.75f, 1f));
            ui.BeginRow(); ui.Label("name", UI.Dim, 80); ui.TextField("mod_name", ref modName, ui.Width - 4); ui.EndRow();
            ui.BeginRow(); ui.Label("description", UI.Dim, 80); ui.TextField("mod_desc", ref modDesc, ui.Width - 4); ui.EndRow();
            ui.BeginRow();
            if (ui.Button("Record as mod") ) status = RecordMod(modName, modDesc, net, acts);
            ui.EndRow();
        }

        public static string RecordMod(string name, string desc, List<ChangeRecorder.Net> net, List<ChangeRecorder.Entry> acts)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "give the mod a name first";
            var incNet = net.FindAll(n => !excluded.Contains(n.prop.key));
            var incAct = acts == null ? new List<ChangeRecorder.Entry>() : acts.FindAll(e => actionIn.Contains(e.id));
            if (incNet.Count == 0 && incAct.Count == 0) return "nothing selected to record";
            List<string> skipped;
            var m = Mods.Record(name, desc, incNet, incAct, out skipped);
            var old = Mods.Find(name);
            if (old != null) { m.file = old.file; Mods.mods.Remove(old); }
            string file = Mods.Save(m);
            Mods.mods.Add(m);
            DevLog.Write("[mods] recorded " + name + ": " + m.ops.Count + " op(s) -> " + file + (skipped.Count > 0 ? "; skipped: " + string.Join("; ", skipped.ToArray()) : ""));
            return "saved " + m.ops.Count + " operation(s) to " + file + (skipped.Count > 0 ? "   skipped " + skipped.Count + ": " + skipped[0] : "") + (m.problems.Count > 0 ? "   problems: " + m.problems[0] : "");
        }

        static string Selector(GameObject g) { try { return ObjectSelector.From(g).ToString(); } catch { return ""; } }

        // ---------------------------------------------------------------- mods
        static void ModsTab(UI ui)
        {
            ui.BeginRow();
            ui.Label(Mods.mods.Count + " mod(s) in _mod\\mods   runtime " + (Mods.runtimeEnabled ? "ON" : "OFF"), UI.Dim, ui.Width - 200);
            if (ui.Button("Reload files")) { Mods.LoadAll(); status = "reloaded: " + Mods.Summary(); }
            bool rt = ui.Toggle(Mods.runtimeEnabled, "runtime"); if (rt != Mods.runtimeEnabled) Mods.runtimeEnabled = rt;
            ui.EndRow();
            foreach (var m in Mods.mods.ToArray())
            {
                ui.BeginRow();
                bool en = ui.Toggle(m.enabled, "", 20); if (en != m.enabled) Mods.SetEnabled(m, en);
                bool isOpen = openMod == m.name;
                if (ui.Item((isOpen ? "v " : "> ") + m.name + "   " + m.Applied + "/" + m.ops.Count + " applied" + (m.problems.Count > 0 ? "   " + m.problems.Count + " problem(s)" : ""), m.problems.Count > 0 ? cBad : m.enabled ? cOk : UI.Dim, ui.Width - 170)) openMod = isOpen ? null : m.name;
                if (ui.Button("Dry run", 70)) { status = Mods.DryRun(m).Replace('\n', ' '); DevLog.Write(Mods.DryRun(m)); }
                if (ui.Button("Validate", 80)) { Mods.Validate(m); status = m.problems.Count == 0 ? m.name + ": valid" : m.name + ": " + string.Join("; ", m.problems.ToArray()); }
                ui.EndRow();
                if (isOpen) foreach (var line in Mods.Status(m).Split('\n')) if (line.Length > 0) ui.Label("    " + line, line.Contains("problem") || line.Contains("error") ? cBad : UI.Dim);
            }
        }

        // ---------------------------------------------------------------- bookmarks
        static void BookmarksTab(UI ui)
        {
            var sel = Selection.Current;
            ui.BeginRow();
            ui.Label("note", UI.Dim, 40);
            ui.TextField("bm_note", ref bmNote, ui.Width - 180);
            if (sel != null && ui.Button("Bookmark selected")) { Bookmarks.Add(sel, null, bmNote); bmNote = ""; }
            ui.EndRow();
            var l = Bookmarks.marks;
            ui.VirtualList("bm_list", l.Count, UI.RowPitch, ui.Remaining, i =>
            {
                var m = l[i];
                ui.BeginRow();
                if (ui.Button("Go", 34)) status = Bookmarks.Go(m, true);
                if (m.IsMethod && ui.Button("Call", 40)) status = Bookmarks.Call(m);
                if (ui.Button("x", 22)) { Bookmarks.Remove(m); ui.EndRow(); return; }
                ui.Item((m.IsMethod ? "[method] " : "") + m.name + (m.note.Length > 0 ? "   — " + m.note : "") + "   [" + m.area + "]" + (authorMode ? "   " + m.selector : ""), new Color(1f, 0.85f, 0.4f, 1f));
                ui.EndRow();
            });
        }
    }
}
