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
    public static class ModifiedPanel
    {
        sealed class Item
        {
            public string selector, name, area, updated;
            public readonly List<string> changes = new List<string>();   // newest first
            public GameObject go; public float resolvedAt = -99f;       // cached lookup
        }
        static readonly List<Item> items = new List<Item>();
        static int seenVersion = -1, lastEntryId;
        static bool loaded, dirty;
        static string current;
        static readonly HashSet<string> expanded = new HashSet<string>();
        static readonly HashSet<string> picked = new HashSet<string>();      // entries ticked for "save as mod"
        static string modName = "", saveStatus = "";                                          // selector of the entry being shown
        static float nextSave;
        static string PathJ { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "modified.json"); } }

        public static void Load()
        {
            loaded = true;
            items.Clear();
            try
            {
                if (!File.Exists(PathJ)) return;
                var root = Json.Parse(File.ReadAllText(PathJ)) as Json.Obj;
                if (root == null) return;
                foreach (var x in root.Arr("objects"))
                {
                    var j = x as Json.Obj; if (j == null) continue;
                    var it = new Item { selector = j.Str("selector"), name = j.Str("name"), area = j.Str("area", ""), updated = j.Str("updated", "") };
                    foreach (var c in j.Arr("changes")) if (c is string) it.changes.Add((string)c);
                    if (!string.IsNullOrEmpty(it.selector)) items.Add(it);
                }
            }
            catch (Exception e) { DevLog.Error("modified load", e); }
        }

        static void Save()
        {
            try
            {
                var arr = new List<object>();
                foreach (var it in items)
                {
                    var j = new Json.Obj();
                    j["selector"] = it.selector; j["name"] = it.name; j["area"] = it.area ?? ""; j["updated"] = it.updated ?? "";
                    var ch = new List<object>(); foreach (var c in it.changes) ch.Add(c); j["changes"] = ch;
                    arr.Add(j);
                }
                var root = new Json.Obj(); root["format"] = "insidedev-modified"; root["objects"] = arr;
                File.WriteAllText(PathJ, Json.Write(root) + "\n");
            }
            catch (Exception e) { DevLog.Error("modified save", e); }
        }

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
            if (dirty && Time.realtimeSinceStartup >= nextSave) { dirty = false; Save(); }
        }

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
            dirty = true; nextSave = Time.realtimeSinceStartup + 1f;
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
            dirty = true; nextSave = Time.realtimeSinceStartup + 1f;
        }
        // Collects, for every ticked object that is loaded: its property changes still in effect (position, on/off,
        // enabled, fields...) and its lasting actions from this session (FSM rewiring, re-parenting). One-off actions
        // (fire event, force state, fire signal) are left out: replaying them on every load would start moments by
        // themselves. Writes _mod\mods\<name>.json through the normal mod system.
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
            List<string> skipped;
            var m = Mods.Record(name, "saved from the Mods tab", net, acts, out skipped);
            var old = Mods.Find(name);
            if (old != null) { m.file = old.file; Mods.mods.Remove(old); }
            string file = Mods.Save(m);
            Mods.mods.Add(m);
            DevLog.Write("[mods] saved " + name + " from the Mods tab: " + m.ops.Count + " op(s) -> " + file);
            return "saved " + m.ops.Count + " change(s) to " + file + (skipped.Count > 0 ? "   skipped: " + string.Join("; ", skipped.ToArray()) : "") + (notLoaded.Count > 0 ? "   not loaded: " + string.Join(", ", notLoaded.ToArray()) : "");
        }

        // for edits that do not go through History (e.g. FSM variables)
        public static void NoteExternal(GameObject go, string what) { if (!loaded) Load(); Note(go, what); }

        public static void Draw(UI ui, Inspector insp)
        {
            if (!loaded) Load();
            ui.BeginRow();
            ui.Label(items.Count + " changed object(s)  -  kept across level loads and restarts; click one to open it", UI.Dim, ui.Width - 90);
            if (items.Count > 0 && ui.Button("clear all", 80)) { items.Clear(); current = null; Save(); }
            ui.EndRow();
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
                if (go == null && resolveBudget > 0 && Time.realtimeSinceStartup - it.resolvedAt >= 3f) { resolveBudget--; go = Resolve(it, false); }
                bool live = go != null;
                bool sel = it.selector == current;
                ui.BeginRow();
                bool pk = picked.Contains(it.selector), npk = ui.Toggle(pk, "", 20);
                if (npk != pk) { if (npk) picked.Add(it.selector); else picked.Remove(it.selector); }
                var col = sel ? UI.Accent : live ? UI.Txt : UI.Dim;
                if (ui.Item((sel ? "> " : "  ") + it.name + (live ? "" : "   (not loaded)") + "   " + (string.IsNullOrEmpty(it.area) ? "" : it.area + "   ") + it.updated, col, ui.Width - 40))
                {
                    current = it.selector;
                    var g = Resolve(it, true);
                    if (g != null) insp.Select(g);
                }
                if (ui.Button("x", 30)) remove = it;
                ui.EndRow();
                // every change on its own line (newest first); long lists fold after 4
                bool all = expanded.Contains(it.selector);
                int show = all ? it.changes.Count : Math.Min(4, it.changes.Count);
                for (int c = 0; c < show; c++) ui.Label("      " + (c == 0 ? "- " : "  ") + it.changes[c], c == 0 ? new Color(1f, 0.85f, 0.55f, 1f) : UI.Dim);
                if (it.changes.Count > 4)
                {
                    ui.BeginRow(); ui.Label("", null, 30);
                    if (ui.Button(all ? "show less" : "show all " + it.changes.Count + " changes", 190)) { if (all) expanded.Remove(it.selector); else expanded.Add(it.selector); }
                    ui.EndRow();
                }
            }
            ui.EndScroll();
            if (pendingRemove != null) { items.Remove(pendingRemove); if (current == pendingRemove.selector) current = null; pendingRemove = null; }
            if (remove != null) { items.Remove(remove); if (current == remove.selector) current = null; Save(); }

            // save the ticked objects' changes as a mod (re-applied whenever their level loads)
            ui.BeginRow();
            ui.Label("tick objects, then:", UI.Dim, 120);
            ui.Label("mod name", UI.Dim, 70);
            ui.TextField("modified_modname", ref modName, Mathf.Max(120f, ui.Width - 200f));
            if (ui.Button("Save " + picked.Count + " as mod", 150)) saveStatus = SaveMod();
            ui.EndRow();
            if (saveStatus.Length > 0) ui.Label(saveStatus, saveStatus.StartsWith("saved") ? new Color(0.5f, 1f, 0.6f, 1f) : new Color(1f, 0.6f, 0.4f, 1f));

            var cur = current != null ? items.Find(x => x.selector == current) : null;
            if (cur == null) { ui.Label("Select an object above to open its panel.", UI.Dim); return; }
            if (Resolve(cur, false) == null)
            {
                ui.Label(cur.name + " is not loaded right now (area " + cur.area + "). Go to its level and it will open here.", new Color(1f, 0.7f, 0.4f, 1f));
                ui.Label("changes made:", UI.Dim);
                foreach (var c in cur.changes) ui.Label("   " + c, UI.Dim);
                return;
            }
            insp.Draw(ui);
        }
    }
}
