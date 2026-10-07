using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Phase 6: hierarchical Scene Explorer over the RuntimeObjectDatabase (no scene walks of its own).
    //   tree      - roots -> children in sibling order, expand/collapse, virtualized rows
    //   filter    - same query language as the Objects list (words, t:Type, k:kind, hidden/active) -> flat results
    //   selection - clicking a row sets the global Selection; any other selection source reveals + scrolls to it
    //   hover     - the row under the mouse is highlighted in the world
    public static class SceneExplorer
    {
        struct Row { public ObjRecord r; public int depth; }

        static readonly Dictionary<int, List<ObjRecord>> kids = new Dictionary<int, List<ObjRecord>>(8192);
        static readonly List<ObjRecord> roots = new List<ObjRecord>();
        static readonly HashSet<int> open = new HashSet<int>();
        static readonly List<Row> rows = new List<Row>(4096);
        static int builtPass = -1, seenSelVersion = -1;
        static float builtAt;
        static bool rowsDirty = true;
        static string filter = "", lastFilter = null;
        static int lastFilterPass = -1;
                static int revealRow = -1;
        static bool showInactive = true;
        public static float lastBuildMs;

        const string ListId = "explorer_rows";

        public static string Status()
        {
            return roots.Count + " roots, " + open.Count + " expanded, " + rows.Count + " rows" + (filter.Length > 0 ? ", find '" + filter + "' " + hits.Count + " hit(s)" : "") + ", build " + lastBuildMs.ToString("0.0") + " ms";
        }

        static void Build()
        {
            if (builtPass == ObjectDatabase.completedPasses) return;
            // a pass that added/removed nothing leaves the tree as it is (rebuilding each pass was most of the
            // panel's garbage); still rebuild every 10 s to pick up reparenting
            if (builtPass >= 0 && ObjectDatabase.addedLastPass == 0 && ObjectDatabase.removedLastPass == 0 && Time.realtimeSinceStartup - builtAt < 10f)
            { builtPass = ObjectDatabase.completedPasses; return; }
            builtAt = Time.realtimeSinceStartup;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            builtPass = ObjectDatabase.completedPasses;
            kids.Clear(); roots.Clear();
            foreach (var r in ObjectDatabase.all)
            {
                if (r.go == null) continue;
                List<ObjRecord> l;
                if (r.parentId == 0 || !ObjectDatabase.byId.ContainsKey(r.parentId)) { roots.Add(r); continue; }
                if (!kids.TryGetValue(r.parentId, out l)) kids[r.parentId] = l = new List<ObjRecord>(4);
                l.Add(r);
            }
            roots.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            foreach (var kv in kids)
                kv.Value.Sort((a, b) =>
                {
                    int ia = a.t != null ? a.t.GetSiblingIndex() : 0, ib = b.t != null ? b.t.GetSiblingIndex() : 0;
                    return ia.CompareTo(ib);
                });
            open.RemoveWhere(id => !ObjectDatabase.byId.ContainsKey(id));
            rowsDirty = true;
            lastBuildMs = (float)sw.Elapsed.TotalMilliseconds;
        }

        static void Flatten()
        {
            if (!rowsDirty) return;
            rowsDirty = false;
            rows.Clear();
            var stack = new Stack<Row>();
            for (int i = roots.Count - 1; i >= 0; i--) stack.Push(new Row { r = roots[i], depth = 0 });
            while (stack.Count > 0)
            {
                var row = stack.Pop();
                if (!showInactive && !row.r.activeInHierarchy) continue;
                rows.Add(row);
                List<ObjRecord> l;
                if (open.Contains(row.r.id) && kids.TryGetValue(row.r.id, out l))
                    for (int i = l.Count - 1; i >= 0; i--) stack.Push(new Row { r = l[i], depth = row.depth + 1 });
            }
        }

        public static void Reveal(GameObject go)
        {
            if (go == null) return;
            for (var p = go.transform.parent; p != null; p = p.parent) open.Add(p.gameObject.GetInstanceID());
            rowsDirty = true;
            revealRow = go.GetInstanceID();
        }

        public static void CollapseAll() { open.Clear(); rowsDirty = true; }

        public static void Draw(UI ui)
        {
            ObjectDatabase.Demand(5f);
            Build();

            // follow selections made elsewhere (inspector links, references, objects list, bridge)
            if (Selection.Version != seenSelVersion)
            {
                seenSelVersion = Selection.Version;
                if (Selection.Current != null && Selection.Source != "explorer") Reveal(Selection.Current);
            }

            ui.BeginRow();
            ui.Label("Find:", null, 34);
            ui.TextField("exp_find", ref filter, Mathf.Max(80, ui.Width - 250));
            if (ui.Button("Reveal selected") && Selection.Current != null) { filter = ""; Reveal(Selection.Current); }
            if (ui.Button("Collapse")) CollapseAll();
            bool si = ui.Toggle(showInactive, "inactive");
            if (si != showInactive) { showInactive = si; rowsDirty = true; }
            ui.EndRow();

            if (!ObjectDatabase.Ready) { ui.Label("scanning scene… (" + ObjectDatabase.Stats() + ")", UI.Dim); return; }

            if (filter.Trim().Length > 0) { DrawFiltered(ui); return; }

            Flatten();
            if (revealRow != 0 && revealRow != -1)
            {
                int idx = rows.FindIndex(x => x.r.id == revealRow);
                if (idx >= 0)
                {
                    float y = idx * UI.ItemPitch, h = ui.Remaining, cur = ui.ScrollOf(ListId);
                    if (y < cur || y > cur + h - UI.ItemPitch * 2) ui.ScrollTo(ListId, y - h * 0.4f);
                }
                revealRow = -1;
            }
            var sel = Selection.Current;
            int selId = sel != null ? sel.GetInstanceID() : 0;
            curUi = ui; curSel = selId;
            ui.VirtualList(ListId, rows.Count, UI.ItemPitch, ui.Remaining, drawTreeRow ?? (drawTreeRow = DrawTreeRow));
        }

        static UI curUi; static int curSel; static Action<int> drawTreeRow;
        static void DrawTreeRow(int i) { DrawRow(curUi, rows[i].r, rows[i].depth, curSel, false); }

        // search: ranked, described, clutter hidden (SmartFind); the old "every path match" behaviour is the
        // "path matches" + "clutter" toggles
        static SmartFind.Cat cat = (SmartFind.Cat)EditorState.Get("explorer.cat", 0);
        static bool showClutter = EditorState.Get("explorer.clutter", false), showPath = EditorState.Get("explorer.path", false);
        static List<SmartFind.Hit> hits = new List<SmartFind.Hit>();
        static int hidClutter, hidPath; static float hitsAt; static string hitsHead = "";
        public static void SetQuery(string q) { filter = q ?? ""; lastFilter = null; }
        public static List<SmartFind.Hit> Hits { get { return hits; } }

        static void DrawFiltered(UI ui)
        {
            string q = filter.Trim();
            ui.BeginRow();
            for (int i = 0; i < SmartFind.CatNames.Length; i++)
                if (ui.Button(SmartFind.CatNames[i], -2, (int)cat == i ? (Color?)UI.Accent : null)) { cat = (SmartFind.Cat)i; EditorState.Set("explorer.cat", i); lastFilter = null; }
            ui.EndRow();
            ui.BeginRow();
            bool cl = ui.Toggle(showClutter, "clutter (static meshes, decals, bones, FX clones)"); if (cl != showClutter) { showClutter = cl; EditorState.Set("explorer.clutter", cl); lastFilter = null; }
            bool pm = ui.Toggle(showPath, "path matches"); if (pm != showPath) { showPath = pm; EditorState.Set("explorer.path", pm); lastFilter = null; }
            ui.EndRow();
            if (q != lastFilter || ObjectDatabase.completedPasses != lastFilterPass || Time.realtimeSinceStartup - hitsAt > 2f)
            {
                lastFilter = q; lastFilterPass = ObjectDatabase.completedPasses; hitsAt = Time.realtimeSinceStartup;
                hits = SmartFind.Run(q, cat, showClutter, showPath, out hidClutter, out hidPath);
                foreach (var h in hits)
                {
                    h.text = SmartFind.Describe(h);
                    var k = h.r.kind;
                    h.color = !h.r.activeSelf ? cOff : !h.r.activeInHierarchy ? UI.Dim : (k & ObjKind.Character) != 0 ? new Color(1f, 0.8f, 0.5f, 1f) : (k & ObjKind.Animation) != 0 ? new Color(0.55f, 1f, 0.55f, 1f) : (k & ObjKind.Trigger) != 0 ? cTrig : (k & ObjKind.StateMachine) != 0 ? UI.Accent : UI.Txt;
                }
                hitsHead = hits.Count + " result(s), best first" + (SmartFind.Truncated > 0 ? "   (" + SmartFind.Truncated + " more not listed - add a word to narrow it)" : "") + (hidClutter > 0 ? "   (" + hidClutter + " clutter hidden)" : "") + (hidPath > 0 ? "   (" + hidPath + " only matched the level / parent name - tick 'path matches')" : "") + ".  Click = select (the Inspector's ACTIVITY section lists its animations, sounds and events).";
            }
            ui.Label(hitsHead, UI.Dim);
            var sel = Selection.Current;
            int selId = sel != null ? sel.GetInstanceID() : 0;
            var list = hits;
            ui.VirtualList("explorer_filtered", list.Count, UI.ItemPitch, ui.Remaining, i =>
            {
                var h = list[i];
                if (h.r.go == null) { ui.Item("(destroyed) " + h.r.name, UI.Dim); return; }
                if (h.r.id == selId) ui.RowHighlight(cSel);
                bool ck = ui.Item(h.text, h.color);
                if (ui.LastHover) Selection.SetHover(h.r.go);
                if (ck) { Selection.Set(h.r.go, "explorer"); Reveal(h.r.go); }
            });
        }

        static readonly Dictionary<int, KeyValuePair<int, string>> labels = new Dictionary<int, KeyValuePair<int, string>>(4096);
        static readonly Color cOff = new Color(1f, 0.45f, 0.9f, 1f), cTrig = new Color(1f, 0.9f, 0.2f, 1f), cSel = new Color(0.22f, 0.37f, 0.6f, 0.75f);

        static void DrawRow(UI ui, ObjRecord r, int depth, int selId, bool flat)
        {
            if (r.go == null) { ui.Item("(destroyed) " + r.name, UI.Dim); return; }
            if (r.id == selId) ui.RowHighlight(cSel);
            List<ObjRecord> l;
            bool hasKids = kids.TryGetValue(r.id, out l) && l.Count > 0;
            bool isOpen = open.Contains(r.id);
            Color c = !r.activeSelf ? cOff : !r.activeInHierarchy ? UI.Dim : (r.kind & ObjKind.Trigger) != 0 ? cTrig : (r.kind & ObjKind.StateMachine) != 0 ? UI.Accent : UI.Txt;
            ui.BeginRow();
            if (!flat)
            {
                ui.Space(depth * 12);
                if (hasKids) { if (ui.Item(isOpen ? "v" : ">", UI.Dim, 14)) { if (isOpen) open.Remove(r.id); else open.Add(r.id); rowsDirty = true; } }
                else ui.Space(18);
            }
            // labels are cached per row: building them (incl. the enum-to-string kind label) every frame was ~18 KB/frame
            int sig = (r.activeSelf ? 1 : 0) | (flat ? 2 : 0) | ((int)r.kind << 2) ^ ((hasKids && !flat ? l.Count : 0) * 7919);
            KeyValuePair<int, string> cached; string label;
            if (labels.TryGetValue(r.id, out cached) && cached.Key == sig) label = cached.Value;
            else
            {
                label = (r.activeSelf ? "" : "[off] ") + r.name + (hasKids && !flat ? "  (" + l.Count + ")" : "") + (r.kind != ObjKind.None ? "   <" + r.KindLabel + ">" : "") + (flat ? "   " + r.path : "");
                if (labels.Count > 50000) labels.Clear();
                labels[r.id] = new KeyValuePair<int, string>(sig, label);
            }
            bool clicked = ui.Item(label, c);
            bool hovered = ui.LastHover;
            ui.EndRow();
            if (hovered) Selection.SetHover(r.go);
            if (clicked)
            {
                Selection.Set(r.go, "explorer");
                if (flat) Reveal(r.go);
            }
        }
    }
}
