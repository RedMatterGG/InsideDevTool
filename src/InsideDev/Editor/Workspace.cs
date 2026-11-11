using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    public sealed class Panel
    {
        public string id, title;
        public Action draw;
        // made once at Register: the name the cost table shows and the draw call with its error guard
        internal string perfName;
        internal Action guarded;
    }

    // Editor shell: docks (left / right / bottom around the game view) or a single floating window.
    // Every dock is a tabbed panel host. Geometry and panel assignment persist in EditorState.
    // The whole arrangement can be kept under a name ("layout.<name>.*" in editor.cfg) and brought back in one step;
    // three arrangements are built in (explore, audio, fsm).
    public sealed class Workspace
    {
        public sealed class Host
        {
            public string id, name;
            public readonly List<string> panels = new List<string>();
            public int active;
            public bool visible = true;
            public Rect rect;
            internal string areaId, restoreId, showLabel;
            internal string[] tabNames;     // titles of panels, rebuilt after the list changed (null)
        }

        public readonly Dictionary<string, Panel> panels = new Dictionary<string, Panel>();
        public readonly List<string> order = new List<string>();
        readonly Host left = NewHost("left", "Left"), right = NewHost("right", "Right"), bottom = NewHost("bottom", "Bottom");
        readonly Host single = NewHost("window", "Window");
        readonly Host[] docks;
        Host[] Docks { get { return docks; } }
        string[] orderNames;

        static Host NewHost(string id, string name) { return new Host { id = id, name = name, areaId = "dock:" + id, restoreId = "restore:" + id, showLabel = "Show " + name }; }

        public Workspace() { docks = new[] { left, bottom, right }; }

        public bool docked = true;
        float leftW = 0.30f, rightW = 0.30f, bottomH = 0.30f;   // fractions of the screen
        Rect winRect = new Rect(30, 70, 760, 580);
        bool loaded;

        public void Register(string id, string title, Action draw)
        {
            var p = new Panel { id = id, title = title, draw = draw, perfName = "panel " + id };
            p.guarded = () => { try { p.draw(); } catch (Exception e) { DevLog.Error(p.perfName, e); } };
            panels[id] = p;
            if (!order.Contains(id)) order.Add(id);
            orderNames = null;
            foreach (var h in docks) h.tabNames = null;
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
            var placed = new HashSet<string>();
            for (int i = 0; i < docks.Length; i++)
            {
                var h = docks[i];
                h.panels.Clear(); h.tabNames = null;
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
            foreach (var h in docks)
            {
                EditorState.Set("dock." + h.id + ".panels", string.Join(",", h.panels.ToArray()));
                EditorState.Set("dock." + h.id + ".active", h.active);
                EditorState.Set("dock." + h.id + ".visible", h.visible);
            }
            EditorState.Set("window.active", single.active);
        }

        // The arrangement that is thrown away stays available as the layout "before-reset" (ui layout load before-reset,
        // or "Layout: before-reset" in the palette). A second reset within a minute keeps the first one's copy.
        float resetAt = -1000f;
        public void ResetLayout()
        {
            if (loaded && Time.realtimeSinceStartup - resetAt > 60f) SaveLayout("before-reset");
            resetAt = Time.realtimeSinceStartup;
            EditorState.Reset("dock.");
            EditorState.Reset("window.");
            EditorState.Set("layout.mode", "dock");
            Load();
            Save();
        }

        public void SetDocked(bool on) { docked = on; Save(); }

        // ---------------------------------------------------------------- named layouts
        public static readonly string[] Presets = { "explore", "audio", "fsm" };
        // the keys that make up one layout (the live ones; a saved layout holds them as layout.<name>.<key>)
        static readonly string[] LayoutKeys =
        {
            "layout.mode", "dock.left.w", "dock.right.w", "dock.bottom.h", "window.x", "window.y", "window.w", "window.h", "window.active",
            "dock.left.panels", "dock.left.active", "dock.left.visible", "dock.bottom.panels", "dock.bottom.active", "dock.bottom.visible",
            "dock.right.panels", "dock.right.active", "dock.right.visible",
        };

        // lower-case letters, digits, '-' and '_', at most 24 characters; null when nothing usable is left
        public static string LayoutName(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (name ?? "").Trim().ToLowerInvariant())
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_') { if (sb.Length < 24) sb.Append(c); }
            return sb.Length == 0 || sb.ToString() == "mode" ? null : sb.ToString();
        }

        // saved names first (in the order they were saved), then the built-in ones that are not overridden
        public List<string> Layouts()
        {
            var l = new List<string>();
            foreach (var n in EditorState.Get("layouts", "").Split(',')) { string t = LayoutName(n); if (t != null && !l.Contains(t)) l.Add(t); }
            foreach (var p in Presets) if (!l.Contains(p)) l.Add(p);
            return l;
        }

        bool IsSaved(string name) { return Array.IndexOf(EditorState.Get("layouts", "").Split(','), name) >= 0; }

        public string SaveLayout(string name)
        {
            string n = LayoutName(name);
            if (n == null) return "usage: ui layout save <name>   (letters, digits, - and _; 'mode' is taken)";
            if (!loaded) Load();
            Save();
            foreach (var k in LayoutKeys) EditorState.Set("layout." + n + "." + k, EditorState.Get(k, ""));
            if (!IsSaved(n)) { string cur = EditorState.Get("layouts", ""); EditorState.Set("layouts", cur.Length > 0 ? cur + "," + n : n); }
            GlobalSearch.LayoutCommand(DevCore.Instance, n);     // "Layout: <name>" in the palette, cmd layout-<name>
            return "layout " + n + " saved" + (Array.IndexOf(Presets, n) >= 0 ? " (replaces the built-in one of that name until it is deleted)" : "");
        }

        public string LoadLayout(string name)
        {
            string n = LayoutName(name);
            if (n == null) return "usage: ui layout load <name>   (" + string.Join(", ", Layouts().ToArray()) + ")";
            if (IsSaved(n))
            {
                foreach (var k in LayoutKeys) { string v = EditorState.Get("layout." + n + "." + k, (string)null); if (v != null) EditorState.Set(k, v); }
                Load();
                return "layout " + n + " loaded\n" + Describe();
            }
            switch (n)
            {
                case "explore": Preset(new[] { "explorer" }, new[] { "console" }, new[] { "inspector" }, 0.30f, 0.30f, 0.30f); break;
                case "audio": Preset(new[] { "atimeline", "sounds", "explorer" }, new[] { "audiodb", "events", "console" }, new[] { "inspector" }, 0.30f, 0.28f, 0.40f); break;
                case "fsm": Preset(new[] { "fsmfind", "explorer" }, new[] { "fsmgraph", "events", "console" }, new[] { "inspector", "logic" }, 0.26f, 0.28f, 0.45f); break;
                default: return "unknown layout '" + n + "' (" + string.Join(", ", Layouts().ToArray()) + ")";
            }
            return "layout " + n + " loaded (built in)\n" + Describe();
        }

        public string DeleteLayout(string name)
        {
            string n = LayoutName(name);
            if (n == null || !IsSaved(n)) return "not found: no saved layout '" + name + "' (" + string.Join(", ", Layouts().ToArray()) + ")";
            EditorState.Reset("layout." + n + ".");
            var keep = new List<string>();
            foreach (var x in EditorState.Get("layouts", "").Split(',')) if (x.Length > 0 && x != n) keep.Add(x);
            EditorState.Set("layouts", string.Join(",", keep.ToArray()));
            if (Array.IndexOf(Presets, n) < 0) EditorCommands.Unregister("layout-" + n);
            return "layout " + n + " deleted" + (Array.IndexOf(Presets, n) >= 0 ? " (the built-in one is back)" : "");
        }

        // the default arrangement with the named panels moved to the front of their dock
        void Preset(string[] leftFirst, string[] bottomFirst, string[] rightFirst, float lw, float rw, float bh)
        {
            EditorState.Reset("dock.");
            EditorState.Set("layout.mode", "dock");
            Load();
            Front(left, leftFirst); Front(bottom, bottomFirst); Front(right, rightFirst);
            leftW = lw; rightW = rw; bottomH = bh; docked = true;
            Save();
        }

        void Front(Host to, string[] ids)
        {
            for (int i = ids.Length - 1; i >= 0; i--)
            {
                if (!panels.ContainsKey(ids[i])) continue;
                foreach (var h in docks) if (h.panels.Remove(ids[i])) h.tabNames = null;
                to.panels.Insert(0, ids[i]);
            }
            foreach (var h in docks) h.active = Mathf.Clamp(h.active, 0, Math.Max(0, h.panels.Count - 1));
            to.active = 0; to.visible = true; to.tabNames = null;
        }

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
            foreach (var h in docks)
            {
                int i = h.panels.IndexOf(panelId);
                if (i >= 0) { h.active = i; h.visible = true; Save(); return; }
            }
        }

        public string ActivePanel(string hostId)
        {
            foreach (var h in docks) if (h.id == hostId && h.panels.Count > 0) return h.panels[Mathf.Clamp(h.active, 0, h.panels.Count - 1)];
            return null;
        }

        public bool IsVisible(string panelId)
        {
            if (!docked) { int i = order.IndexOf(panelId); return i == Mathf.Clamp(single.active, 0, order.Count - 1); }
            foreach (var h in docks) if (h.visible && h.panels.Count > 0 && h.panels[Mathf.Clamp(h.active, 0, h.panels.Count - 1)] == panelId) return true;
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
            foreach (var h in docks) if (h.id == dock) h.visible = frac > 0;
            Save(); return Describe();
        }
        public string SetDockVisible(string dock, bool on) { if (!loaded) Load(); foreach (var h in docks) if (h.id == dock) { h.visible = on; Save(); return Describe(); } return "dock must be left, right or bottom"; }
        public string SetWindow(float x, float y, float w, float h)
        {
            if (!loaded) Load();
            w = Mathf.Max(200, w); h = Mathf.Max(150, h);
            // the same limits as dragging the title bar: a strip of the window always stays on screen
            var core = DevCore.Instance;
            float scale = core != null ? Mathf.Clamp(core.uiScale, 0.5f, 2f) : 1f;
            float sw = Screen.width / scale, sh = Screen.height / scale;
            x = Mathf.Clamp(x, -w + 60, sw - 60); y = Mathf.Clamp(y, 0, sh - 30);
            winRect = new Rect(x, y, w, h); Save(); return Describe();
        }
        public string Put(string panelId, string dock)
        {
            if (!loaded) Load();
            if (!panels.ContainsKey(panelId)) return "unknown panel '" + panelId + "' (" + string.Join(", ", order.ToArray()) + ")";
            Host to = null; foreach (var h in docks) if (h.id == dock) to = h;
            if (to == null) return "dock must be left, right or bottom";
            foreach (var h in docks) if (h.panels.Remove(panelId)) { h.active = Mathf.Clamp(h.active, 0, Math.Max(0, h.panels.Count - 1)); h.tabNames = null; }
            to.panels.Add(panelId); to.active = to.panels.Count - 1; to.visible = true; to.tabNames = null;
            Save(); return Describe();
        }
        public string Describe()
        {
            var sb = new System.Text.StringBuilder(docked ? "docked" : "single window " + Fmt.S("(x:{0:F2}, y:{1:F2}, width:{2:F2}, height:{3:F2})", winRect.x, winRect.y, winRect.width, winRect.height));
            sb.Append(Fmt.S("  left {0:0.##} right {1:0.##} bottom {2:0.##}", leftW, rightW, bottomH));
            foreach (var h in docks) sb.Append("\n  ").Append(h.id).Append(h.visible ? "" : " (hidden)").Append(": ").Append(string.Join(", ", h.panels.ToArray())).Append(h.panels.Count > 0 ? "   showing " + h.panels[Mathf.Clamp(h.active, 0, h.panels.Count - 1)] : "");
            return sb.ToString();
        }

        // "ui docks json": { ok, mode, left, right, bottom, window: {x,y,w,h}, docks: { left: {visible, showing, panels[]}, ... }, layouts[] }
        public string DescribeJson()
        {
            if (!loaded) Load();
            var root = new Json.Obj();
            root["ok"] = true; root["mode"] = docked ? "dock" : "window";
            root["left"] = leftW; root["right"] = rightW; root["bottom"] = bottomH;
            var w = new Json.Obj(); w["x"] = winRect.x; w["y"] = winRect.y; w["w"] = winRect.width; w["h"] = winRect.height;
            root["window"] = w;
            var ds = new Json.Obj();
            foreach (var h in docks)
            {
                var d = new Json.Obj();
                d["visible"] = h.visible;
                d["showing"] = h.panels.Count > 0 ? h.panels[Mathf.Clamp(h.active, 0, h.panels.Count - 1)] : null;
                d["panels"] = new List<object>(h.panels.ToArray());
                ds[h.id] = d;
            }
            root["docks"] = ds;
            root["layouts"] = new List<object>(Layouts().ToArray());
            return JsonLine.Write(root);
        }

        void MovePanel(Host from, string id)
        {
            int k = Array.IndexOf(docks, from);
            var to = docks[(k + 1) % docks.Length];
            from.panels.Remove(id);
            to.panels.Add(id);
            to.active = to.panels.Count - 1; to.visible = true;
            from.active = Mathf.Clamp(from.active, 0, Math.Max(0, from.panels.Count - 1));
            from.tabNames = to.tabNames = null;
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

        string winTitleFor, winTitle;
        void BuildWindow(UI ui, string title)
        {
            winRect.width = Mathf.Clamp(winRect.width, 320, ui.ScreenW - 20);
            winRect.height = Mathf.Clamp(winRect.height, 200, ui.ScreenH - 40);
            var before = winRect;
            if ((object)winTitleFor != (object)title) { winTitleFor = title; winTitle = title + "   —   drag title bar, F1 to close"; }
            winRect = ui.Window("window", winRect, winTitle);
            if (winRect != before) Save();
            if (orderNames == null || orderNames.Length != order.Count)
            {
                orderNames = new string[order.Count];
                for (int i = 0; i < order.Count; i++) orderNames[i] = panels[order[i]].title;
            }
            int sel = ui.Tabs(Mathf.Clamp(single.active, 0, order.Count - 1), orderNames);
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
            for (int i = 0; i < docks.Length; i++)
            {
                var h = docks[i];
                if (h.visible || h.panels.Count == 0) continue;
                var r = h == left ? new Rect(0, y, 90, 20) : h == right ? new Rect(W - 90, y, 90, 20) : new Rect(W * 0.5f - 45, H - 20, 90, 20);
                ui.BeginArea(h.restoreId, r, UI.Title);
                ui.BeginRow();
                if (ui.Button(h.showLabel, 80)) { h.visible = true; Save(); }
                ui.EndRow();
                ui.EndArea();
                if (h == left) y += 24;
            }
        }

        void BuildHost(UI ui, Host h)
        {
            var r = h.rect;
            ui.BeginArea(h.areaId, r, UI.Bg);
            h.active = Mathf.Clamp(h.active, 0, h.panels.Count - 1);
            // header: tabs + move/hide buttons
            if (h.tabNames == null || h.tabNames.Length != h.panels.Count)
            {
                h.tabNames = new string[h.panels.Count];
                for (int i = 0; i < h.tabNames.Length; i++) h.tabNames[i] = panels[h.panels[i]].title;
            }
            ui.NarrowRight(46);   // keep the tab strip clear of the header icons
            int sel = ui.Tabs(h.active, h.tabNames);
            ui.WidenRight(46);
            if (sel != h.active) { h.active = sel; Save(); }
            string activeId = h.panels[h.active];
            // header icons (top-right corner of the dock)
            var hide = new Rect(r.xMax - 22, r.y + 3, 16, 16);
            var move = new Rect(r.xMax - 42, r.y + 3, 16, 16);
            bool doMove = ui.IconButton(move, UI.Glyph.Arrow, h.areaId);
            bool doHide = ui.IconButton(hide, UI.Glyph.Minus, h.areaId);
            DrawPanel(activeId);
            ui.EndArea();
            if (doMove) MovePanel(h, activeId);
            if (doHide) { h.visible = false; Save(); }
        }

        void DrawPanel(string id)
        {
            Panel p;
            if (!panels.TryGetValue(id, out p)) return;
            Perf.RunNested(p.perfName, p.guarded);
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

        public Rect DockRect(string hostId) { foreach (var h in docks) if (h.id == hostId) return h.rect; return new Rect(); }
    }
}
