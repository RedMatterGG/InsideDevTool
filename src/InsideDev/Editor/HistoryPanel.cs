using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Phase 11 UI: History (undo / redo / revert), net changes, Record as mod, installed mods, bookmarks.
    // Explore mode shows history and bookmarks; Mod Author mode adds selectors, Record as mod and the mod manager.
    // Lists are rebuilt when the history changes (ChangeRecorder.Version), live values twice per second; row texts are
    // built for the rows on screen and kept.
    public static class HistoryPanel
    {
        public static bool authorMode;
        static int tab;
        static readonly string[] tabs = { "History", "Changes", "Mods", "Bookmarks" };
        static readonly string[] exploreTabs = { "History", "Changes", "Bookmarks" };
        static string modName = "", modDesc = "", status = "", bmNote = "";
        static readonly HashSet<string> excluded = new HashSet<string>();      // net-change keys left out of the mod
        static readonly HashSet<int> actionIn = new HashSet<int>();            // action entries included in the mod
        static string openMod;

        static readonly Color cAct = new Color(1f, 0.72f, 0.3f, 1f), cUndone = new Color(0.45f, 0.47f, 0.52f, 1f), cOk = new Color(0.5f, 1f, 0.6f, 1f), cBad = new Color(1f, 0.45f, 0.4f, 1f),
            cMark = new Color(1f, 0.85f, 0.4f, 1f), cHead = new Color(0.55f, 0.9f, 0.75f, 1f);

        static UI curUi; static float curNow;
        static readonly Action<int> drawHistory = HistoryRow, drawNet = NetRow, drawMark = MarkRow;

        public static void Draw(UI ui)
        {
            curUi = ui; curNow = Time.realtimeSinceStartup;
            ui.BeginRow();
            bool am = ui.Toggle(authorMode, "Mod Author mode");
            if (am != authorMode) { authorMode = am; EditorState.Set("mode.author", am); netRows.Invalidate(); markRows.Invalidate(); }
            if (ui.Button("Undo  (Ctrl+Z)")) status = ChangeRecorder.Undo();
            if (ui.Button("Redo  (Ctrl+Y)")) status = ChangeRecorder.Redo();
            ui.EndRow();
            string[] shown = authorMode ? tabs : exploreTabs;
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
        static readonly ListCache histRows = new ListCache();
        static string histHead; static int histSecond;

        static void History(UI ui)
        {
            var h = ChangeRecorder.Entries;
            int cur = ChangeRecorder.Cursor;
            // the "12s ago" part of a row moves once per second: texts are rebuilt then, for the rows on screen
            if (histRows.Changed(ChangeRecorder.Version, ((long)h.Count << 32) | (uint)cur, 0, null))
            {
                histRows.Fill(h.Count);
                histHead = h.Count + " entries" + (cur < h.Count ? ", " + (h.Count - cur) + " undone (redo available)" : "") + ".  Actions (orange) are recorded but cannot be undone.";
            }
            else if ((int)curNow != histSecond) histRows.DropTexts();
            histSecond = (int)curNow;
            ui.Label(histHead, UI.Dim);
            ui.VirtualList("hist_list", h.Count, UI.ItemPitch, ui.Remaining, drawHistory);
        }

        static void HistoryRow(int row)
        {
            var ui = curUi;
            var h = ChangeRecorder.Entries;
            int i = h.Count - 1 - row;
            if (i < 0 || i >= h.Count) return;
            var e = h[i];
            bool undone = i >= ChangeRecorder.Cursor;
            var c = undone ? cUndone : e.IsAction ? cAct : UI.Txt;
            string s = histRows.Text(row);
            if (s == null)
            {
                float age = curNow - e.t;
                string when = age < 120f ? ((int)age) + "s ago" : ((int)(age / 60f)) + "m ago";
                s = histRows.Text(row, (undone ? "(undone) " : "") + "#" + e.id + "  " + (e.IsAction ? "ACTION  " : "") + e.Describe() + "   [" + e.source + ", " + when + "]");
            }
            bool ck = ui.Item(s, c);
            bool hover = ui.LastHover;
            if (!ck && !hover) return;
            GameObject go = e.action != null ? e.action.go : e.ops.Count > 0 ? e.ops[0].prop.go : null;
            if (go == null) return;
            if (hover) Selection.SetHover(go);
            if (ck) Selection.Set(go, "history");
        }

        // ---------------------------------------------------------------- net changes + record as mod
        static List<ChangeRecorder.Net> net = new List<ChangeRecorder.Net>();
        static readonly List<ChangeRecorder.Entry> acts = new List<ChangeRecorder.Entry>();
        static readonly List<string> actLabels = new List<string>();
        static readonly ListCache netRows = new ListCache();
        static int netVersion = -1; static float netAt; static string netHead = "";

        // the list of changed properties: replayed from the history when it changed, and twice per second so values the
        // game writes by itself show up
        static void RefreshNet()
        {
            if (netVersion == ChangeRecorder.Version && curNow - netAt < 0.5f) return;
            bool historyChanged = netVersion != ChangeRecorder.Version;
            netVersion = ChangeRecorder.Version; netAt = curNow;
            net = ChangeRecorder.NetChanges();
            netRows.Invalidate();
            netHead = net.Count + " property value(s) differ from the game's original";
            if (!historyChanged) return;
            acts.Clear(); actLabels.Clear();
            var h = ChangeRecorder.Entries;
            for (int i = 0; i < ChangeRecorder.Cursor && i < h.Count; i++) if (h[i].IsAction) { acts.Add(h[i]); actLabels.Add("#" + h[i].id + " " + h[i].label); }
        }

        static void NetChanges(UI ui)
        {
            RefreshNet();
            if (netRows.Changed(net.Count, authorMode ? 1 : 0, 0, null)) netRows.Fill(net.Count);
            ui.BeginRow();
            ui.Label(netHead, UI.Dim, ui.Width - 100);
            if (net.Count > 0 && ui.Button("Revert all", 90)) { status = ChangeRecorder.RevertAll(); netVersion = -1; }
            ui.EndRow();
            float listH = authorMode ? Mathf.Max(UI.RowPitch * 3, ui.Remaining - 190f) : ui.Remaining;
            ui.VirtualList("net_list", net.Count, UI.RowPitch, listH, drawNet);
            if (!authorMode) return;

            // actions that can go into the mod (replayed on load)
            if (acts.Count > 0)
            {
                ui.Label("Actions (tick to replay them on load in the mod):", UI.Dim);
                for (int i = 0; i < acts.Count; i++)
                {
                    var e = acts[i];
                    ui.BeginRow();
                    bool inc = actionIn.Contains(e.id); bool ni = ui.Toggle(inc, "", 20); if (ni != inc) { if (ni) actionIn.Add(e.id); else actionIn.Remove(e.id); }
                    ui.Label(actLabels[i], cAct);
                    ui.EndRow();
                }
            }
            ui.Label("RECORD AS MOD", cHead);
            ui.BeginRow(); ui.Label("name", UI.Dim, 80); EditorInput.Field(ui, "mod_name", ref modName, ui.Width - 4); ui.EndRow();
            ui.BeginRow(); ui.Label("description", UI.Dim, 80); EditorInput.Field(ui, "mod_desc", ref modDesc, ui.Width - 4); ui.EndRow();
            ui.BeginRow();
            if (ui.Button("Record as mod"))
            {
                string ask;
                status = (modName ?? "").Trim().Length > 0 && !ConfirmReplace(modName.Trim(), out ask) ? ask : RecordMod(modName, modDesc, net, acts);
            }
            ui.EndRow();
        }

        static void NetRow(int i)
        {
            if (i >= net.Count) return;
            var ui = curUi;
            var n = net[i];
            ui.BeginRow();
            if (authorMode) { bool inc = !excluded.Contains(n.prop.key); bool ni = ui.Toggle(inc, "", 20); if (ni != inc) { if (ni) excluded.Remove(n.prop.key); else excluded.Add(n.prop.key); } }
            if (ui.Button("Revert", 60)) { status = ChangeRecorder.Revert(n); netVersion = -1; }
            string s = netRows.Text(i) ?? netRows.Text(i, n.prop.Label + ":  " + ChangeRecorder.Short(n.original) + "  ->  " + ChangeRecorder.Short(n.current) + (authorMode ? "      " + Selector(n.prop.go) : ""));
            bool ck = ui.Item(s, Changes.Purple);
            if (ui.LastHover) Selection.SetHover(n.prop.go);
            if (ck) Selection.Set(n.prop.go, "history");
            ui.EndRow();
        }

        // ---- replacing a mod file
        // Where Mods.Save writes a mod of this name: the file of the loaded mod with that name, else <name>.json with
        // every character that is not a letter, a digit, '-' or '_' turned into '_' (the rule of Mods.Save).
        public static string ModFile(string name, out bool exists)
        {
            var old = Mods.Find(name);
            string file = old != null ? old.file : null;
            if (file == null)
            {
                var sb = new StringBuilder();
                foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
                file = Path.Combine(Mods.Dir, (sb.Length > 0 ? sb.ToString() : "mod") + ".json");
            }
            exists = File.Exists(file);
            return file;
        }

        static string confirmName; static float confirmAt;

        // Buttons that write a mod: true = go ahead. When a file for that name exists the first press returns false with
        // the question in ask; a second press within 10 s goes ahead.
        public static bool ConfirmReplace(string name, out string ask)
        {
            ask = null;
            bool exists; string file = ModFile(name, out exists);
            if (!exists) return true;
            float now = Time.realtimeSinceStartup;
            if (confirmName == name && now - confirmAt < 10f) { confirmName = null; return true; }
            confirmName = name; confirmAt = now;
            ask = "a mod file for this name exists (" + Path.GetFileName(file) + "): press again within 10 s to replace it; the old file is kept as " + Path.GetFileName(file) + ".bak";
            return false;
        }

        // Called right before Mods.Save(m): m takes the file of the mod it replaces, that file is kept once as
        // <file>.bak and the replaced mod leaves the loaded list. Returns the note for the reply ("" when nothing was
        // replaced).
        public static string TakePlace(Mods.Mod m)
        {
            bool exists; string file = ModFile(m.name, out exists);
            var old = Mods.Find(m.name);
            if (old != null) { m.file = old.file; Mods.mods.Remove(old); }
            if (!exists) return "";
            for (int i = Mods.mods.Count - 1; i >= 0; i--)
                if (Mods.mods[i] != m && Mods.mods[i].file != null && string.Equals(Path.GetFullPath(Mods.mods[i].file), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) Mods.mods.RemoveAt(i);
            try { File.Copy(file, file + ".bak", true); }
            catch (Exception e) { DevLog.Error("mod backup", e); return "   (replaced " + Path.GetFileName(file) + "; the old file could not be kept: " + e.Message + ")"; }
            return "   (replaced " + Path.GetFileName(file) + "; the old file is kept as " + Path.GetFileName(file) + ".bak)";
        }

        // Bridge "mod record" and the button. An existing file of that name is replaced (the old one stays as .bak); the
        // button asks first (ConfirmReplace).
        public static string RecordMod(string name, string desc, List<ChangeRecorder.Net> net, List<ChangeRecorder.Entry> acts)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "give the mod a name first";
            var incNet = net.FindAll(n => !excluded.Contains(n.prop.key));
            var incAct = acts == null ? new List<ChangeRecorder.Entry>() : acts.FindAll(e => actionIn.Contains(e.id));
            if (incNet.Count == 0 && incAct.Count == 0) return "nothing selected to record";
            List<string> skipped;
            var m = Mods.Record(name, desc, incNet, incAct, out skipped);
            string kept = TakePlace(m);
            string file = Mods.Save(m);
            Mods.mods.Add(m);
            DevLog.Write("[mods] recorded " + name + ": " + m.ops.Count + " op(s) -> " + file + kept + (skipped.Count > 0 ? "; skipped: " + string.Join("; ", skipped.ToArray()) : ""));
            return "saved " + m.ops.Count + " operation(s) to " + file + kept + (skipped.Count > 0 ? "   skipped " + skipped.Count + ": " + skipped[0] : "") + (m.problems.Count > 0 ? "   problems: " + m.problems[0] : "");
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
        static readonly ListCache markRows = new ListCache();
        static Bookmarks.Mark removeMark, undoMark; static int undoMarkAt; static float undoMarkUntil;

        static void BookmarksTab(UI ui)
        {
            var sel = Selection.Current;
            ui.BeginRow();
            ui.Label("note", UI.Dim, 40);
            EditorInput.Field(ui, "bm_note", ref bmNote, ui.Width - 180);
            if (sel != null && ui.Button("Bookmark selected")) { Bookmarks.Add(sel, null, bmNote); bmNote = ""; }
            ui.EndRow();
            if (undoMark != null)
            {
                if (curNow > undoMarkUntil) undoMark = null;
                else
                {
                    ui.BeginRow();
                    ui.Label("removed bookmark " + undoMark.name, cBad, Mathf.Max(100, ui.Width - 90));
                    if (ui.Button("Undo", 80)) { Bookmarks.marks.Insert(Mathf.Clamp(undoMarkAt, 0, Bookmarks.marks.Count), undoMark); Bookmarks.Save(); undoMark = null; }
                    ui.EndRow();
                }
            }
            var l = Bookmarks.marks;
            long sig = l.Count;
            if (l.Count > 0) sig = sig * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(l[0]) * 7L + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(l[l.Count - 1]);
            if (markRows.Changed(sig, authorMode ? 1 : 0, 0, null)) markRows.Fill(l.Count);
            removeMark = null;
            ui.VirtualList("bm_list", l.Count, UI.RowPitch, ui.Remaining, drawMark);
            // removing inside the list would shift the rows the list is still drawing
            if (removeMark != null)
            {
                undoMarkAt = l.IndexOf(removeMark); undoMark = removeMark; undoMarkUntil = curNow + 10f;
                Bookmarks.Remove(removeMark);
                removeMark = null;
            }
        }

        static void MarkRow(int i)
        {
            var l = Bookmarks.marks;
            if (i >= l.Count) return;
            var ui = curUi;
            var m = l[i];
            ui.BeginRow();
            if (ui.Button("Go", 34)) status = Bookmarks.Go(m, true);
            if (m.IsMethod && ui.Button("Call", 40)) status = Bookmarks.Call(m);
            if (ui.Button("x", 22)) removeMark = m;
            string s = markRows.Text(i) ?? markRows.Text(i, (m.IsMethod ? "[method] " : "") + m.name + (m.note.Length > 0 ? "   — " + m.note : "") + "   [" + m.area + "]" + (authorMode ? "   " + m.selector : ""));
            ui.Item(s, cMark);
            ui.EndRow();
        }
    }
}
