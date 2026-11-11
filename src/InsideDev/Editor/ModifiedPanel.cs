using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace InsideDev
{
    // "Mods" tab: every object you changed (fields, transform, on/off, FSM state / events / rewiring...), bookmarked
    // automatically and kept across level loads, checkpoint reloads and restarts (_mod\modified.json). Click one to open
    // its full inspector panel right here, like picking an object in the Explorer. Objects are found again by their
    // hierarchy path, so after a reload the entry points at the fresh copy; entries whose level is not loaded are grey.
    // The file is replaced in one step and the list as it was at the start of the session stays in modified.json.bak;
    // "clear all" and "x" can be taken back for ten seconds.
    public static class ModifiedPanel
    {
        sealed class Item
        {
            public string selector, name, area, updated;
            public readonly List<string> changes = new List<string>();   // newest first
            public GameObject go; public float resolvedAt = -99f;       // cached lookup
            // row texts, rebuilt when the entry changes (not every frame)
            public string rowText; public int rowSig = -1;
            public readonly List<string> lines = new List<string>(); public bool linesDirty = true; public string moreLabel;
        }
        static readonly List<Item> items = new List<Item>();
        static int seenVersion = -1, lastEntryId;
        static bool loaded, dirty, backedUp, hadFile, mainUnread, unreadWarned;
        static string current;                                                // selector of the entry being shown
        static readonly HashSet<string> expanded = new HashSet<string>();
        static readonly HashSet<string> picked = new HashSet<string>();      // entries ticked for "save as mod"
        static string modName = "", saveStatus = "";
        static float nextSave;
        static string PathJ { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "modified.json"); } }

        public static void Load()
        {
            loaded = true;
            items.Clear();
            string text = null; bool fromBackup = false, exists = false;
            try { exists = File.Exists(PathJ); text = SafeFile.ReadAllTextOrBackup(PathJ, out fromBackup); }
            catch (Exception e) { DevLog.Error("modified load", e); }
            // the file is there but could not be opened (held by another program): its entries are merged in before the
            // first save, and nothing is written over it until then
            mainUnread = exists && (text == null || fromBackup);
            if (text == null) return;
            if (Parse(text, items)) { hadFile = items.Count > 0; if (fromBackup) DevLog.Write("[mods] modified.json was missing or unreadable: loaded modified.json.bak"); return; }
            // damaged: nothing is written over it before a copy exists, and the backup is tried
            items.Clear();
            SetAside();
            if (fromBackup) return;
            try
            {
                string bak = PathJ + ".bak";
                if (File.Exists(bak) && Parse(File.ReadAllText(bak), items)) DevLog.Write("[mods] modified.json is damaged: loaded modified.json.bak (" + items.Count + " entries)");
                else items.Clear();
            }
            catch (Exception e) { DevLog.Error("modified backup load", e); }
        }

        static bool Parse(string text, List<Item> items)
        {
            try
            {
                var root = Json.Parse(text) as Json.Obj;
                if (root == null) return false;
                foreach (var x in root.Arr("objects"))
                {
                    var j = x as Json.Obj; if (j == null) continue;
                    var it = new Item { selector = j.Str("selector"), name = j.Str("name"), area = j.Str("area", ""), updated = j.Str("updated", "") };
                    foreach (var c in j.Arr("changes")) if (c is string) it.changes.Add((string)c);
                    if (!string.IsNullOrEmpty(it.selector)) items.Add(it);
                }
                return true;
            }
            catch (Exception e) { DevLog.Error("modified load", e); return false; }
        }

        // a file that exists but could not be used is kept as modified.json.unreadable before the next save replaces it
        static void SetAside()
        {
            try
            {
                if (!File.Exists(PathJ)) return;
                File.Copy(PathJ, PathJ + ".unreadable", true);
                DevLog.Write("[mods] modified.json could not be read; a copy is kept as modified.json.unreadable");
            }
            catch (Exception e) { DevLog.Error("modified set aside", e); }
        }

        static void Save() { Save(false); }

        static void Save(bool keepOld)
        {
            dirty = false;
            try
            {
                if (mainUnread)
                {
                    string old;
                    try { old = File.ReadAllText(PathJ); }
                    catch (Exception e)
                    {
                        if (!unreadWarned) { unreadWarned = true; DevLog.Write("[mods] modified.json is not saved while it cannot be read (" + e.Message + "); the list of this session stays in memory and the save is tried again"); }
                        dirty = true; nextSave = Time.realtimeSinceStartup + 10f;
                        return;
                    }
                    var inFile = new List<Item>();
                    if (Parse(old, inFile)) { foreach (var x in inFile) if (items.Find(y => y.selector == x.selector) == null) items.Add(x); }
                    else SetAside();
                    mainUnread = false; hadFile = true;
                }
                var arr = new List<object>();
                foreach (var it in items)
                {
                    var j = new Json.Obj();
                    j["selector"] = it.selector; j["name"] = it.name; j["area"] = it.area ?? ""; j["updated"] = it.updated ?? "";
                    var ch = new List<object>(); foreach (var c in it.changes) ch.Add(c); j["changes"] = ch;
                    arr.Add(j);
                }
                var root = new Json.Obj(); root["format"] = "insidedev-modified"; root["objects"] = arr;
                SafeFile.WriteAllText(PathJ, Json.Write(root) + "\n", keepOld || (!backedUp && hadFile));
                backedUp = true;
            }
            catch (Exception e) { DevLog.Error("modified save", e); }
        }

        // writes a pending change now (quit, unload)
        public static void Flush() { if (loaded && dirty) Save(); }

        // every frame: pick up new History entries
        public static void Tick()
        {
            if (!loaded) Load();
            if (ChangeRecorder.Version != seenVersion)
            {
                seenVersion = ChangeRecorder.Version;
                var list = ChangeRecorder.Entries;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (e == null || e.id <= lastEntryId) continue;
                    lastEntryId = e.id;
                    if (e.source == "mod") continue;                    // changes applied by mod files are not yours to review
                    if (e.action != null) Note(e.action.go, e.Describe());
                    else foreach (var op in e.ops) if (op.prop != null) Note(op.prop.go, op.prop.Label + ": " + ChangeRecorder.Short(op.before) + " -> " + ChangeRecorder.Short(op.after));
                }
            }
            if (dirty && Time.realtimeSinceStartup >= nextSave) Save();
        }

        static void Touch(Item it) { it.rowText = null; it.linesDirty = true; dirty = true; nextSave = Time.realtimeSinceStartup + 1f; }

        static void Note(GameObject go, string what)
        {
            if (go == null) return;
            string sel;
            try { sel = ObjectSelector.From(go).ToString(); } catch { return; }
            // one entry per object: match the live object first (its path can change, e.g. after a reparent), then the path
            var it = items.Find(x => x.go == go) ?? items.Find(x => x.selector == sel);
            if (it != null && it.selector != sel)
            {
                var dup = items.Find(x => x != it && x.selector == sel);
                if (dup != null) { Merge(it, dup); items.Remove(dup); }
                it.selector = sel;
            }
            if (it == null)
            {
                it = new Item { selector = sel, name = go.name };
                try { it.area = AudioCatalog.AreaOf(go); } catch { }
                items.Insert(0, it);
            }
            else { items.Remove(it); items.Insert(0, it); }          // most recently changed on top
            it.go = go; it.resolvedAt = Time.realtimeSinceStartup;
            it.updated = DateTime.Now.ToString("HH:mm");
            if (it.changes.Count == 0 || it.changes[0] != what) it.changes.Insert(0, what);
            if (it.changes.Count > 30) it.changes.RemoveRange(30, it.changes.Count - 30);
            Touch(it);
        }

        static GameObject Resolve(Item it, bool force)
        {
            if (it.go != null) return it.go;
            if (!force && Time.realtimeSinceStartup - it.resolvedAt < 3f) return null;
            it.resolvedAt = Time.realtimeSinceStartup;
            try { var l = Mods.ResolveAll(it.selector); it.go = l.Count > 0 ? l[0] : null; } catch { it.go = null; }
            if (it.go != null)
            {
                // two saved entries that turn out to be the same object: keep one
                var other = items.Find(x => x != it && x.go == it.go);
                if (other != null) { Merge(other, it); pendingRemove = it; }
            }
            return it.go;
        }
        static Item pendingRemove;
        static void Merge(Item keep, Item from)
        {
            foreach (var c in from.changes) if (!keep.changes.Contains(c)) keep.changes.Add(c);
            if (keep.changes.Count > 30) keep.changes.RemoveRange(30, keep.changes.Count - 30);
            Touch(keep);
        }
        // Collects, for every ticked object that is loaded: its property changes still in effect (position, on/off,
        // enabled, fields...) and its lasting actions from this session (FSM rewiring, re-parenting). One-off actions
        // (fire event, force state, fire signal) are left out: replaying them on every load would start moments by
        // themselves. Writes _mod\mods\<name>.json through the normal mod system. A file that is already there is
        // replaced only after a second press, and stays as <name>.json.bak.
        static string SaveMod()
        {
            string name = (modName ?? "").Trim();
            if (name.Length == 0) return "give the mod a name first";
            if (picked.Count == 0) return "tick the objects to include (checkbox left of each entry)";
            var gos = new HashSet<GameObject>();
            var notLoaded = new List<string>();
            foreach (var it in items) if (picked.Contains(it.selector)) { var g = Resolve(it, true); if (g != null) gos.Add(g); else notLoaded.Add(it.name); }
            var net = ChangeRecorder.NetChanges().FindAll(n => n.prop != null && n.prop.go != null && gos.Contains(n.prop.go));
            var acts = new List<ChangeRecorder.Entry>();
            var h = ChangeRecorder.Entries;
            for (int i = 0; i < ChangeRecorder.Cursor && i < h.Count; i++)
            {
                var e = h[i];
                if (e.action == null || e.action.go == null || !gos.Contains(e.action.go)) continue;
                if (e.action.action == "fsmWire" || e.action.action == "sigWire" || e.action.action == "fsmAction" || e.action.action == "reparent") acts.Add(e);
            }
            if (net.Count == 0 && acts.Count == 0) return "nothing to save: the ticked objects have no changes from this session" + (notLoaded.Count > 0 ? " (not loaded: " + string.Join(", ", notLoaded.ToArray()) + ")" : "");
            string ask;
            if (!HistoryPanel.ConfirmReplace(name, out ask)) return ask;
            List<string> skipped;
            var m = Mods.Record(name, "saved from the Mods tab", net, acts, out skipped);
            string kept = HistoryPanel.TakePlace(m);
            string file = Mods.Save(m);
            Mods.mods.Add(m);
            DevLog.Write("[mods] saved " + name + " from the Mods tab: " + m.ops.Count + " op(s) -> " + file + kept);
            return "saved " + m.ops.Count + " change(s) to " + file + kept + (skipped.Count > 0 ? "   skipped: " + string.Join("; ", skipped.ToArray()) : "") + (notLoaded.Count > 0 ? "   not loaded: " + string.Join(", ", notLoaded.ToArray()) : "");
        }

        // for edits that do not go through History (e.g. FSM variables)
        public static void NoteExternal(GameObject go, string what) { if (!loaded) Load(); Note(go, what); }

        // ---- taking back "clear all" / "x"
        static readonly List<Item> undoItems = new List<Item>();
        static int undoIndex; static float undoUntil; static string undoText;

        static void Removed(List<Item> gone, int index, string what)
        {
            undoItems.Clear(); undoItems.AddRange(gone);
            undoIndex = index; undoUntil = Time.realtimeSinceStartup + 10f; undoText = what;
        }

        static void Undo()
        {
            int at = Mathf.Clamp(undoIndex, 0, items.Count);
            foreach (var it in undoItems) if (!items.Contains(it) && items.Find(x => x.selector == it.selector) == null) items.Insert(at++, it);
            undoItems.Clear(); undoUntil = 0f;
            Save();
        }

        static readonly Color cNewest = new Color(1f, 0.85f, 0.55f, 1f), cOk = new Color(0.5f, 1f, 0.6f, 1f), cWarn = new Color(1f, 0.6f, 0.4f, 1f), cGone = new Color(1f, 0.7f, 0.4f, 1f);
        static string headLine, saveLabel; static int headFor = -1, saveLabelFor = -1;

        public static void Draw(UI ui, Inspector insp)
        {
            if (!loaded) Load();
            float now = Time.realtimeSinceStartup;
            if (headFor != items.Count) { headFor = items.Count; headLine = items.Count + " changed object(s)  -  kept across level loads and restarts; click one to open it"; }
            ui.BeginRow();
            ui.Label(headLine, UI.Dim, ui.Width - 90);
            if (items.Count > 0 && ui.Button("clear all", 80))
            {
                Removed(items, 0, "cleared " + items.Count + " entries");
                items.Clear(); current = null; Save(true);
            }
            ui.EndRow();
            if (undoItems.Count > 0)
            {
                if (now > undoUntil) undoItems.Clear();
                else
                {
                    ui.BeginRow();
                    ui.Label(undoText, cWarn, Mathf.Max(100, ui.Width - 90));
                    if (ui.Button("Undo", 80)) Undo();
                    ui.EndRow();
                }
            }
            if (items.Count == 0)
            {
                ui.Label("Nothing yet. Anything you change in the Inspector (fields, position, on/off, FSM states, events, rewiring) appears here.", UI.Dim);
                return;
            }
            float need = 8f; foreach (var x in items) need += 24f + 22f * Mathf.Min(x.changes.Count, expanded.Contains(x.selector) ? 30 : 4) + (x.changes.Count > 4 ? 24f : 0f);
            float listH = Mathf.Min(need, Mathf.Max(120f, ui.Remaining * 0.45f));
            ui.BeginScroll("mods_list", listH);
            int resolveBudget = 2;
            Item remove = null;
            foreach (var it in items)
            {
                GameObject go = it.go;
                if (go == null && resolveBudget > 0 && now - it.resolvedAt >= 3f) { resolveBudget--; go = Resolve(it, false); }
                bool live = go != null;
                bool sel = it.selector == current;
                ui.BeginRow();
                bool pk = picked.Contains(it.selector), npk = ui.Toggle(pk, "", 20);
                if (npk != pk) { if (npk) picked.Add(it.selector); else picked.Remove(it.selector); }
                var col = sel ? UI.Accent : live ? UI.Txt : UI.Dim;
                int sig = (sel ? 1 : 0) | (live ? 2 : 0);
                if (it.rowText == null || it.rowSig != sig)
                {
                    it.rowSig = sig;
                    it.rowText = (sel ? "> " : "  ") + it.name + (live ? "" : "   (not loaded)") + "   " + (string.IsNullOrEmpty(it.area) ? "" : it.area + "   ") + it.updated;
                }
                if (ui.Item(it.rowText, col, ui.Width - 40))
                {
                    current = it.selector;
                    var g = Resolve(it, true);
                    if (g != null) insp.Select(g);
                }
                if (ui.Button("x", 30)) remove = it;
                ui.EndRow();
                // every change on its own line (newest first); long lists fold after 4
                if (it.linesDirty)
                {
                    it.linesDirty = false; it.lines.Clear();
                    for (int c = 0; c < it.changes.Count; c++) it.lines.Add("      " + (c == 0 ? "- " : "  ") + it.changes[c]);
                    it.moreLabel = "show all " + it.changes.Count + " changes";
                }
                bool all = expanded.Contains(it.selector);
                int show = all ? it.lines.Count : Math.Min(4, it.lines.Count);
                for (int c = 0; c < show; c++) ui.Label(it.lines[c], c == 0 ? cNewest : UI.Dim);
                if (it.changes.Count > 4)
                {
                    ui.BeginRow(); ui.Label("", null, 30);
                    if (ui.Button(all ? "show less" : it.moreLabel, 190)) { if (all) expanded.Remove(it.selector); else expanded.Add(it.selector); }
                    ui.EndRow();
                }
            }
            ui.EndScroll();
            if (pendingRemove != null) { items.Remove(pendingRemove); if (current == pendingRemove.selector) current = null; pendingRemove = null; }
            if (remove != null)
            {
                Removed(new List<Item> { remove }, items.IndexOf(remove), "removed " + remove.name + " from the list");
                items.Remove(remove); if (current == remove.selector) current = null; Save();
            }

            // save the ticked objects' changes as a mod (re-applied whenever their level loads)
            if (saveLabelFor != picked.Count) { saveLabelFor = picked.Count; saveLabel = "Save " + picked.Count + " as mod"; }
            ui.BeginRow();
            ui.Label("tick objects, then:", UI.Dim, 120);
            ui.Label("mod name", UI.Dim, 70);
            EditorInput.Field(ui, "modified_modname", ref modName, Mathf.Max(120f, ui.Width - 200f));
            if (ui.Button(saveLabel, 150)) saveStatus = SaveMod();
            ui.EndRow();
            if (saveStatus.Length > 0) ui.Label(saveStatus, saveStatus.StartsWith("saved", StringComparison.Ordinal) ? cOk : cWarn);

            var cur = current != null ? items.Find(x => x.selector == current) : null;
            if (cur == null) { ui.Label("Select an object above to open its panel.", UI.Dim); return; }
            if (Resolve(cur, false) == null)
            {
                ui.Label(cur.name + " is not loaded right now (area " + cur.area + "). Go to its level and it will open here.", cGone);
                ui.Label("changes made:", UI.Dim);
                foreach (var c in cur.changes) ui.Label("   " + c, UI.Dim);
                return;
            }
            insp.Draw(ui);
        }
    }
}
