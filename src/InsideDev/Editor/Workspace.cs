using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    public sealed class Panel
    {
        public string id, title;
        public Action draw;
    }

    // Editor shell: docks (left / right / bottom around the game view) or a single floating window.
    // Every dock is a tabbed panel host. Geometry and panel assignment persist in EditorState.
    public sealed class Workspace
    {
        public sealed class Host
        {
            public string id, name;
            public readonly List<string> panels = new List<string>();
            public int active;
            public bool visible = true;
            public Rect rect;
        }

        public readonly Dictionary<string, Panel> panels = new Dictionary<string, Panel>();
        public readonly List<string> order = new List<string>();
        readonly Host left = new Host { id = "left", name = "Left" }, right = new Host { id = "right", name = "Right" }, bottom = new Host { id = "bottom", name = "Bottom" };
        readonly Host single = new Host { id = "window", name = "Window" };
        Host[] Docks { get { return new[] { left, bottom, right }; } }

        public bool docked = true;
        float leftW = 0.30f, rightW = 0.30f, bottomH = 0.30f;   // fractions of the screen
        Rect winRect = new Rect(30, 70, 760, 580);
        bool loaded;

        public void Register(string id, string title, Action draw)
        {
            panels[id] = new Panel { id = id, title = title, draw = draw };
            if (!order.Contains(id)) order.Add(id);
        }

        // ---------------------------------------------------------------- persistence
        static readonly string[][] DefaultDocks =
        {
            new[] { "explorer", "fsmfind", "spawns", "levels", "triggers", "hidden", "world", "objects" },
            new[] { "console", "events", "graph", "fsmgraph", "sounds", "audiodb", "atimeline", "diag", "audio", "wwiseapi" },
            new[] { "inspector", "logic", "modified", "history", "cheats", "settings" },
        };

        public void Load()
        {
            loaded = true;
            docked = EditorState.Get("layout.mode", "dock") == "dock";
            leftW = Mathf.Clamp(EditorState.Get("dock.left.w", 0.30f), 0.12f, 0.6f);
            rightW = Mathf.Clamp(EditorState.Get("dock.right.w", 0.30f), 0.12f, 0.6f);
            bottomH = Mathf.Clamp(EditorState.Get("dock.bottom.h", 0.30f), 0.1f, 0.7f);
            winRect = new Rect(EditorState.Get("window.x", 30f), EditorState.Get("window.y", 70f), EditorState.Get("window.w", 760f), EditorState.Get("window.h", 580f));
            var docks = Docks;
            var placed = new HashSet<string>();
            for (int i = 0; i < docks.Length; i++)
            {
                var h = docks[i];
                h.panels.Clear();
                string csv = EditorState.Get("dock." + h.id + ".panels", string.Join(",", DefaultDocks[i]));
                foreach (var p in csv.Split(',')) { var t = p.Trim(); if (t.Length > 0 && panels.ContainsKey(t) && placed.Add(t)) h.panels.Add(t); }
                h.active = EditorState.Get("dock." + h.id + ".active", 0);
                h.visible = EditorState.Get("dock." + h.id + ".visible", true);
            }
            // panels registered after the layout was saved go to their default dock (left if they have none)
            foreach (var id in order)
                if (!placed.Contains(id))
                {
                    int di = 0;
                    for (int k = 0; k < DefaultDocks.Length; k++) if (Array.IndexOf(DefaultDocks[k], id) >= 0) di = k;
                    // right after the panel that precedes it in the default layout (e.g. Mods after Logic)
                    int at = -1, pos = Array.IndexOf(DefaultDocks[di], id);
                    for (int q = pos - 1; q >= 0 && at < 0; q--) { int x = docks[di].panels.IndexOf(DefaultDocks[di][q]); if (x >= 0) at = x + 1; }
                    if (at >= 0) docks[di].panels.Insert(at, id); else docks[di].panels.Add(id);
                }
            single.active = EditorState.Get("window.active", 0);
        }

        void Save()
        {
            EditorState.Set("layout.mode", docked ? "dock" : "window");
            EditorState.Set("dock.left.w", leftW); EditorState.Set("dock.right.w", rightW); EditorState.Set("dock.bottom.h", bottomH);
            EditorState.Set("window.x", winRect.x); EditorState.Set("window.y", winRect.y); EditorState.Set("window.w", winRect.width); EditorState.Set("window.h", winRect.height);
            foreach (var h in Docks)
            {
                EditorState.Set("dock." + h.id + ".panels", string.Join(",", h.panels.ToArray()));
                EditorState.Set("dock." + h.id + ".active", h.active);
                EditorState.Set("dock." + h.id + ".visible", h.visible);
            }
            EditorState.Set("window.active", single.active);
        }

        public void ResetLayout()
        {
            EditorState.Reset("dock.");
            EditorState.Reset("window.");
            EditorState.Set("layout.mode", "dock");
            Load();
            Save();
        }

        public void SetDocked(bool on) { docked = on; Save(); }

        // ---------------------------------------------------------------- navigation
        public void Show(string panelId)
        {
            if (!loaded) Load();
            if (!docked)
            {
                int i = order.IndexOf(panelId);
                if (i >= 0) single.active = i;
                Save();
                return;
            }
            foreach (var h in Docks)
            {
                int i = h.panels.IndexOf(panelId);
                if (i >= 0) { h.active = i; h.visible = true; Save(); return; }
            }
        }

        public string ActivePanel(string hostId)
        {
            foreach (var h in Docks) if (h.id == hostId && h.panels.Count > 0) return h.panels[Mathf.Clamp(h.active, 0, h.panels.Count - 1)];
            return null;
        }

        public bool IsVisible(string panelId)
        {
            if (!docked) { int i = order.IndexOf(panelId); return i == Mathf.Clamp(single.active, 0, order.Count - 1); }
            foreach (var h in Docks) if (h.visible && h.panels.Count > 0 && h.panels[Mathf.Clamp(h.active, 0, h.panels.Count - 1)] == panelId) return true;
            return false;
        }

        // ---------------------------------------------------------------- scripted layout (bridge "ui dock|window|put", MCP tool "layout")
        public string SetDockSize(string dock, float frac)
        {
            if (!loaded) Load();
            if (dock == "left") leftW = Mathf.Clamp(frac, 0.12f, 0.6f);
            else if (dock == "right") rightW = Mathf.Clamp(frac, 0.12f, 0.6f);
            else if (dock == "bottom") bottomH = Mathf.Clamp(frac, 0.1f, 0.7f);
            else return "dock must be left, right or bottom";
            foreach (var h in Docks) if (h.id == dock) h.visible = frac > 0;
            Save(); return Describe();
        }
        public string SetDockVisible(string dock, bool on) { if (!loaded) Load(); foreach (var h in Docks) if (h.id == dock) { h.visible = on; Save(); return Describe(); } return "dock must be left, right or bottom"; }
        public string SetWindow(float x, float y, float w, float h)
        {
            if (!loaded) Load();
            winRect = new Rect(x, y, Mathf.Max(200, w), Mathf.Max(150, h)); Save(); return Describe();
        }
        public string Put(string panelId, string dock)
        {
            if (!loaded) Load();
            if (!panels.ContainsKey(panelId)) return "unknown panel '" + panelId + "' (" + string.Join(", ", order.ToArray()) + ")";
            Host to = null; foreach (var h in Docks) if (h.id == dock) to = h;
            if (to == null) return "dock must be left, right or bottom";
            foreach (var h in Docks) if (h.panels.Remove(panelId)) h.active = Mathf.Clamp(h.active, 0, Math.Max(0, h.panels.Count - 1));
            to.panels.Add(panelId); to.active = to.panels.Count - 1; to.visible = true;
            Save(); return Describe();
        }
        public string Describe()
        {
            var sb = new System.Text.StringBuilder(docked ? "docked" : "single window " + winRect);
            sb.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "  left {0:0.##} right {1:0.##} bottom {2:0.##}", leftW, rightW, bottomH));
            foreach (var h in Docks) sb.Append("\n  ").Append(h.id).Append(h.visible ? "" : " (hidden)").Append(": ").Append(string.Join(", ", h.panels.ToArray())).Append(h.panels.Count > 0 ? "   showing " + h.panels[Mathf.Clamp(h.active, 0, h.panels.Count - 1)] : "");
            return sb.ToString();
        }

        void MovePanel(Host from, string id)
        {
            var docks = Docks;
            int k = Array.IndexOf(docks, from);
            var to = docks[(k + 1) % docks.Length];
            from.panels.Remove(id);
            to.panels.Add(id);
            to.active = to.panels.Count - 1; to.visible = true;
            from.active = Mathf.Clamp(from.active, 0, Math.Max(0, from.panels.Count - 1));
            Save();
        }

        // ---------------------------------------------------------------- build
        UI curUi;
        public void Build(UI ui, float top, string title)
        {
            curUi = ui;
            if (!loaded) Load();
            if (docked) BuildDocks(ui, top, title); else BuildWindow(ui, title);
        }

        void BuildWindow(UI ui, string title)
        {
            winRect.width = Mathf.Clamp(winRect.width, 320, ui.ScreenW - 20);
            winRect.height = Mathf.Clamp(winRect.height, 200, ui.ScreenH - 40);
            var before = winRect;
            winRect = ui.Window("window", winRect, title + "   —   drag title bar, F1 to close");
            if (winRect != before) Save();
            var names = new string[order.Count];
            for (int i = 0; i < order.Count; i++) names[i] = panels[order[i]].title;
            int sel = ui.Tabs(Mathf.Clamp(single.active, 0, order.Count - 1), names);
            if (sel != single.active) { single.active = sel; Save(); }
            DrawPanel(order[sel]);
            ui.EndWindow();
        }

        const float SplitW = 5f;

        void BuildDocks(UI ui, float top, string title)
        {
            float W = ui.ScreenW, H = ui.ScreenH;
            bool L = left.visible && left.panels.Count > 0, R = right.visible && right.panels.Count > 0, B = bottom.visible && bottom.panels.Count > 0;
            float lw = L ? Mathf.Round(leftW * W) : 0, rw = R ? Mathf.Round(rightW * W) : 0, bh = B ? Mathf.Round(bottomH * H) : 0;
            left.rect = new Rect(0, top, lw, H - top);
            right.rect = new Rect(W - rw, top, rw, H - top);
            bottom.rect = new Rect(lw + (L ? SplitW : 0), H - bh, W - lw - rw - (L ? SplitW : 0) - (R ? SplitW : 0), bh);

            bool changed = false;
            if (L) { float dx = ui.Splitter("left", new Rect(lw, top, SplitW, H - top), true); if (dx != 0) { leftW = Mathf.Clamp(leftW + dx / W, 0.12f, 0.6f); changed = true; } }
            if (R) { float dx = ui.Splitter("right", new Rect(W - rw - SplitW, top, SplitW, H - top), true); if (dx != 0) { rightW = Mathf.Clamp(rightW - dx / W, 0.12f, 0.6f); changed = true; } }
            if (B) { float dy = ui.Splitter("bottom", new Rect(bottom.rect.x, H - bh - SplitW, bottom.rect.width, SplitW), false); if (dy != 0) { bottomH = Mathf.Clamp(bottomH - dy / H, 0.1f, 0.7f); changed = true; } }
            if (changed) Save();

            if (L) BuildHost(ui, left);
            if (R) BuildHost(ui, right);
            if (B) BuildHost(ui, bottom);

            // restore buttons for hidden docks (small tabs on the screen edge)
            float y = top + 4;
            foreach (var h in Docks)
            {
                if (h.visible || h.panels.Count == 0) continue;
                var r = h == left ? new Rect(0, y, 90, 20) : h == right ? new Rect(W - 90, y, 90, 20) : new Rect(W * 0.5f - 45, H - 20, 90, 20);
                ui.BeginArea("restore:" + h.id, r, UI.Title);
                ui.BeginRow();
                if (ui.Button("Show " + h.name, 80)) { h.visible = true; Save(); }
                ui.EndRow();
                ui.EndArea();
                if (h == left) y += 24;
            }
        }

        void BuildHost(UI ui, Host h)
        {
            var r = h.rect;
            ui.BeginArea("dock:" + h.id, r, UI.Bg);
            h.active = Mathf.Clamp(h.active, 0, h.panels.Count - 1);
            // header: tabs + move/hide buttons
            var names = new string[h.panels.Count];
            for (int i = 0; i < names.Length; i++) names[i] = panels[h.panels[i]].title;
            ui.NarrowRight(46);   // keep the tab strip clear of the header icons
            int sel = ui.Tabs(h.active, names);
            ui.WidenRight(46);
            if (sel != h.active) { h.active = sel; Save(); }
            string activeId = h.panels[h.active];
            // header icons (top-right corner of the dock)
            var hide = new Rect(r.xMax - 22, r.y + 3, 16, 16);
            var move = new Rect(r.xMax - 42, r.y + 3, 16, 16);
            bool doMove = ui.IconButton(move, UI.Glyph.Arrow, "dock:" + h.id);
            bool doHide = ui.IconButton(hide, UI.Glyph.Minus, "dock:" + h.id);
            DrawPanel(activeId);
            ui.EndArea();
            if (doMove) MovePanel(h, activeId);
            if (doHide) { h.visible = false; Save(); }
        }

        static readonly Dictionary<string, string> perfNames = new Dictionary<string, string>();
        void DrawPanel(string id)
        {
            Panel p;
            if (!panels.TryGetValue(id, out p)) return;
            string pn; if (!perfNames.TryGetValue(id, out pn)) perfNames[id] = pn = "panel " + id;
            Perf.RunNested(pn, () => { try { if (curUi != null) GameCode.DrawBanner(curUi); p.draw(); } catch (Exception e) { DevLog.Error("panel " + id, e); } });
        }

        // physical-pixel rect of the game view left uncovered by docks (whole screen in window mode)
        public Rect GameViewRect(float scale)
        {
            if (!docked) return new Rect(0, 0, Screen.width, Screen.height);
            float x0 = left.visible && left.panels.Count > 0 ? left.rect.xMax + SplitW : 0;
            float x1 = right.visible && right.panels.Count > 0 ? right.rect.xMin - SplitW : Screen.width / scale;
            float y1 = bottom.visible && bottom.panels.Count > 0 ? bottom.rect.yMin - SplitW : Screen.height / scale;
            return new Rect(x0 * scale, 0, Mathf.Max(0, x1 - x0) * scale, y1 * scale);
        }

        public Rect DockRect(string hostId) { foreach (var h in Docks) if (h.id == hostId) return h.rect; return new Rect(); }
    }
}
