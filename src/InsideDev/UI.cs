using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Immediate-mode UI toolkit, built in Update from EditorInput, recorded into Draw's UI layer.
    // Coordinates are logical (UI scale is applied by Draw). Layout state is per frame; persistent widget state
    // (scroll offsets, focus, drag) lives here, window/dock geometry lives in Workspace/EditorState.
    //
    // Areas: every panel/window is an area = own z-order segment + clip rect. Hit testing uses last frame's
    // area stack, so only the topmost area under the mouse is interactive (no click-through between panels).
    public class UI
    {
        readonly Draw d;
        public UI(Draw draw) { d = draw; }

        // ---------------------------------------------------------------- style (dark, restrained)
        public static readonly Color Bg = new Color(0.075f, 0.08f, 0.095f, 0.95f);
        public static readonly Color BgAlt = new Color(0.1f, 0.105f, 0.125f, 0.97f);
        public static readonly Color Title = new Color(0.13f, 0.15f, 0.19f, 0.98f);
        public static readonly Color Btn = new Color(0.2f, 0.22f, 0.27f, 0.95f);
        public static readonly Color BtnHot = new Color(0.3f, 0.34f, 0.43f, 0.98f);
        public static readonly Color TabSel = new Color(0.22f, 0.37f, 0.6f, 1f);
        public static readonly Color Accent = new Color(0.35f, 0.65f, 1f, 1f);
        public static readonly Color Txt = new Color(0.9f, 0.91f, 0.93f, 1f);
        public static readonly Color Dim = new Color(0.58f, 0.61f, 0.66f, 1f);
        public static readonly Color Border = new Color(0.25f, 0.28f, 0.35f, 1f);
        public const int LineH = 20;
        const int Pad = 6;

        // ---------------------------------------------------------------- input (logical, top-left)
        public Vector2 mouse;
        public bool click, held, released, rightClick;
        float wheel;
        public string focus;            // focused text field id
        string activeDrag;              // slider / window / splitter / scrollbar drag
        Vector2 dragOffset;
        public bool mouseOverUI;
        public float Scale { get { return d.uiScale; } }
        public float ScreenW { get { return Screen.width / d.uiScale; } }
        public float ScreenH { get { return Screen.height / d.uiScale; } }

        // area stack for z-ordered hit testing
        readonly List<KeyValuePair<string, Rect>> areasNow = new List<KeyValuePair<string, Rect>>();
        string hotArea;
        string curArea;

        public void BeginFrame(float scale)
        {
            d.ClearUI();
            d.uiScale = Mathf.Clamp(scale, 0.5f, 2f);
            var em = EditorInput.mouse;
            mouse = em / d.uiScale;
            click = EditorInput.down[0];
            held = EditorInput.held[0];
            released = EditorInput.up[0];
            rightClick = EditorInput.down[1];
            wheel = EditorInput.wheel;
            if (!held && activeDrag != null) activeDrag = null;
            hotArea = null;
            for (int i = areasNow.Count - 1; i >= 0; i--) if (areasNow[i].Value.Contains(mouse)) { hotArea = areasNow[i].Key; break; }
            mouseOverUI = hotArea != null;
            areasNow.Clear();
            if (click && hotArea == null && focus != null) focus = null;
            // widget log of the previous frame (bridge UI tests: "ui find <text>", "ui clicktext <text>")
            if (recordWidgets) { lastWidgets.Clear(); lastWidgets.AddRange(widgetsNow); }
            tipText = null;
            widgetsNow.Clear();
        }

        public bool recordWidgets;
        readonly List<KeyValuePair<string, Rect>> widgetsNow = new List<KeyValuePair<string, Rect>>(512);
        public readonly List<KeyValuePair<string, Rect>> lastWidgets = new List<KeyValuePair<string, Rect>>(512);
        void Log(string s, Rect r) { if (recordWidgets && Visible(r)) widgetsNow.Add(new KeyValuePair<string, Rect>(s, r)); }
        public Rect? FindWidget(string text, int nth = 0)
        {
            foreach (var kv in lastWidgets)
                if (kv.Key != null && kv.Key.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 && nth-- == 0) return kv.Value;
            return null;
        }

        // ---------------------------------------------------------------- areas / windows
        Rect win;
        float cx, cy, rowX, rowH;
        bool inRow;
        float left, right;

        int areaClipDepth;

        public void BeginArea(string id, Rect r, Color? bg = null)
        {
            d.uiLayer = true;
            d.BeginSegment();
            areaClipDepth = d.ClipDepth;
            scrollId = null; inRow = false;
            areasNow.Add(new KeyValuePair<string, Rect>(id, r));
            curArea = id;
            if (bg.HasValue) d.Fill(r, bg.Value);
            d.PushClip(r);
            win = r;
            left = r.x + Pad; right = r.xMax - Pad;
            cx = left; cy = r.y + Pad; inRow = false;
        }

        // always restores the clip stack to where the area began, even if a panel threw halfway through
        public void EndArea() { d.PopClipTo(areaClipDepth); d.uiLayer = false; curArea = null; scrollId = null; inRow = false; }

        public void NarrowRight(float px) { right -= px; }
        public void WidenRight(float px) { right += px; }

        public bool AreaHot { get { return hotArea != null && hotArea == curArea; } }

        // floating window with draggable title bar; returns the (possibly moved) rect
        public Rect Window(string id, Rect r, string title)
        {
            var bar = new Rect(r.x, r.y, r.width, 22);
            if (click && bar.Contains(mouse) && hotArea == id) { activeDrag = "win:" + id; dragOffset = mouse - new Vector2(r.x, r.y); }
            if (activeDrag == "win:" + id && held)
            {
                r.x = Mathf.Clamp(mouse.x - dragOffset.x, -r.width + 60, ScreenW - 60);
                r.y = Mathf.Clamp(mouse.y - dragOffset.y, 0, ScreenH - 30);
            }
            BeginArea(id, r, Bg);
            d.Fill(bar, Title);
            d.Frame(r, Border);
            d.Text(r.x + 8, r.y + 4, title, Txt);
            cy = r.y + 28;
            return r;
        }

        public void EndWindow() { EndArea(); }

        public float Remaining { get { return win.yMax - cy - Pad; } }
        public float Width { get { return right - (inRow ? rowX : left); } }
        public float CursorY { get { return cy; } }

        // ---------------------------------------------------------------- layout
        public void BeginRow() { inRow = true; rowX = left; rowH = LineH; }
        public void EndRow() { inRow = false; cy += rowH + 2; }
        public void Space(float h = 6) { if (inRow) rowX += h; else cy += h; }

        Rect lastRect;
        Rect Place(float w, float h)
        {
            var r = PlaceRaw(w, h);
            lastRect = r;
            return r;
        }
        // highlight a full-width band behind the next row (selection marker in lists)
        public void RowHighlight(Color c) { d.Fill(new Rect(left, cy, right - left, LineH - 2), c); }
        public float Left { get { return left; } }
        public Draw D { get { return d; } }
        public float Wheel { get { return wheel; } }
        // free-draw region inside the current area (graph views etc.); caller draws with D in logical coords
        public Rect Canvas(float h) { var r = Place(-1, Mathf.Max(LineH, h)); return r; }
        public bool Hot(Rect r) { return AreaHot && r.Contains(mouse); }

        Rect PlaceRaw(float w, float h)
        {
            Rect r;
            if (inRow)
            {
                // fixed-width widgets that no longer fit wrap onto the next line instead of running past the edge
                if (w >= 0 && rowX > left + 0.5f && rowX + w > right + 0.5f) { cy += rowH + 2; rowX = left; rowH = LineH; }
                if (w < 0) w = Mathf.Max(20, right - rowX);
                r = new Rect(rowX, cy, w, h);
                rowX += w + 4;
                rowH = Mathf.Max(rowH, h);
            }
            else
            {
                if (w < 0) w = right - left;
                r = new Rect(left, cy, w, h);
                cy += h + 2;
            }
            return r;
        }

        Rect ClipNow { get { return d.CurrentClipLogical; } }
        bool Visible(Rect r) { var c = ClipNow; return r.xMax > c.xMin && r.xMin < c.xMax && r.yMax > c.yMin && r.yMin < c.yMax; }
        bool Hover(Rect r) { return AreaHot && activeDrag == null && r.Contains(mouse) && ClipNow.Contains(mouse); }

        // ---------------------------------------------------------------- widgets
        public void Label(string s, Color? c = null, float w = -1)
        {
            if (s == null) s = "";
            int lines;
            // flexible-width labels wrap to the space they get; fixed-width ones stay on one line (tooltip shows the rest)
            if (w < 0) s = Wrapped(s, AvailW() - 6, out lines);
            else { lines = 1; foreach (char ch in s) if (ch == '\n') lines++; }
            var r = Place(w, lines * (Draw.DefaultSize + 3) + 3);
            if (Visible(r)) { d.PushClip(r); d.Text(r.x + 2, r.y + 2, s, c ?? Txt); d.PopClip(); if (w >= 0) Tip(r, s); }
        }

        float AvailW() { return inRow ? Mathf.Max(20, right - rowX) : right - left; }

        // ---------------------------------------------------------------- word wrap (cached per text + width)
        sealed class WrapEntry { public float w; public string text; public int lines; }
        readonly Dictionary<string, WrapEntry> wrapCache = new Dictionary<string, WrapEntry>();
        readonly List<string> wrapTmp = new List<string>();
        string Wrapped(string s, float maxW, out int lines)
        {
            WrapEntry e;
            if (wrapCache.TryGetValue(s, out e) && Mathf.Abs(e.w - maxW) < 1f) { lines = e.lines; return e.text; }
            wrapTmp.Clear();
            foreach (var para in s.Split('\n')) WrapInto(para, maxW, wrapTmp);
            string t = wrapTmp.Count == 1 ? wrapTmp[0] : string.Join("\n", wrapTmp.ToArray());
            if (wrapCache.Count > 6000) wrapCache.Clear();
            wrapCache[s] = new WrapEntry { w = maxW, text = t, lines = wrapTmp.Count };
            lines = wrapTmp.Count;
            return t;
        }

        public bool Button(string s, float w = -2, Color? col = null)
        {
            if (w == -2) w = d.Measure(s) + 14;
            var r = Place(w, LineH);
            if (!Visible(r)) return false;
            Log(s, r);
            bool hot = Hover(r);
            d.Fill(r, hot ? BtnHot : (col ?? Btn));
            d.PushClip(r); d.Text(r.x + 7, r.y + 3, s, Txt); d.PopClip();
            if (hot) Tip(r, s);
            return hot && click;
        }

        // greyed, non-clickable button; tip explains why
        public static readonly Color BtnOff = new Color(0.16f, 0.17f, 0.2f, 0.8f);
        public void ButtonDisabled(string s, string why = null, float w = -2)
        {
            if (w == -2) w = d.Measure(s) + 14;
            var r = Place(w, LineH);
            if (!Visible(r)) return;
            Log("[disabled] " + s, r);
            d.Fill(r, BtnOff);
            d.PushClip(r); d.Text(r.x + 7, r.y + 3, s, new Color(Dim.r, Dim.g, Dim.b, 0.6f)); d.PopClip();
            if (Hover(r) && why != null) Tip(r, why);
        }

        // flat, left-aligned clickable text (for lists)
        public bool Item(string s, Color? c = null, float w = -1)
        {
            if (s == null) s = "";
            int lines = 1;
            string full = s;
            if (w < 0) s = Wrapped(s, AvailW() - 8, out lines);
            var r = Place(w, LineH - 2 + (lines - 1) * (Draw.DefaultSize + 3));
            if (!Visible(r)) return false;
            Log(s, r);
            bool hot = Hover(r);
            if (hot) d.Fill(r, new Color(1, 1, 1, 0.08f));
            d.PushClip(r); d.Text(r.x + 3, r.y + 2, s, c ?? Txt); d.PopClip();
            if (hot && w >= 0) Tip(r, full);
            return hot && click;
        }

        // ---------------------------------------------------------------- tooltips for cut-off text
        // Any label / item / button whose text does not fit shows the whole text in a wrapped box after a short hover.
        string tipText, tipPrev; float tipSince;
        void Tip(Rect r, string s)
        {
            if (string.IsNullOrEmpty(s) || !Hover(r) && !(AreaHot && r.Contains(mouse) && ClipNow.Contains(mouse))) return;
            var clipR = ClipNow;
            float visibleW = Mathf.Min(r.xMax, clipR.xMax) - r.x - 6;
            if (s.IndexOf('\n') < 0 && d.Measure(s) <= visibleW) return;
            if (s.IndexOf('\n') >= 0) { bool wide = false; foreach (var l in s.Split('\n')) if (d.Measure(l) > visibleW) { wide = true; break; } if (!wide) return; }
            tipText = s;
        }

        // call once per frame after all areas (draws on top of everything)
        public void DrawTooltip()
        {
            string t = tipText; tipText = null;
            if (t == null) { tipPrev = null; return; }
            float now = Time.realtimeSinceStartup;
            if (t != tipPrev) { tipPrev = t; tipSince = now; }
            if (now - tipSince < 0.35f) return;
            float maxW = Mathf.Min(720f, ScreenW - 24f);
            var lines = new List<string>();
            foreach (var para in t.Split('\n')) WrapInto(para, maxW - 12, lines);
            if (lines.Count > 40) { lines.RemoveRange(40, lines.Count - 40); lines.Add("…"); }
            float w = 0; foreach (var l in lines) w = Mathf.Max(w, d.Measure(l));
            w += 12; float lh = Draw.DefaultSize + 3, h = lines.Count * lh + 8;
            float x = mouse.x + 14, y = mouse.y + 18;
            if (x + w > ScreenW - 4) x = Mathf.Max(4, ScreenW - 4 - w);
            if (y + h > ScreenH - 4) y = Mathf.Max(4, mouse.y - 8 - h);
            bool was = d.uiLayer; d.uiLayer = true;
            d.BeginSegment();
            var box = new Rect(x, y, w, h);
            d.Fill(box, new Color(0.05f, 0.055f, 0.07f, 0.97f)); d.Frame(box, Accent);
            for (int i = 0; i < lines.Count; i++) d.Text(x + 6, y + 4 + i * lh, lines[i], Txt);
            d.uiLayer = was;
        }

        void WrapInto(string s, float maxW, List<string> into)
        {
            if (s.Length == 0 || d.Measure(s) <= maxW) { into.Add(s); return; }
            float space = Mathf.Max(1f, d.Measure("a b") - d.Measure("ab"));
            int ind = 0; while (ind < s.Length && s[ind] == ' ') ind++;
            string indent = new string(' ', Mathf.Min(ind + 2, 12)); float indW = d.Measure(indent);   // continuation lines are indented
            var line = new System.Text.StringBuilder();
            line.Append(s, 0, ind); float lw = ind > 0 ? d.Measure(line.ToString()) : 0f;
            bool empty = true;                                   // no word on the current line yet
            foreach (var raw in s.Substring(ind).Split(' '))
            {
                string word = raw;
                if (word.Length == 0) { if (!empty) { line.Append(' '); lw += space; } continue; }   // keep runs of spaces
                float ww = d.Measure(word);
                if (!empty && lw + space + ww > maxW) { into.Add(line.ToString()); line.Length = 0; line.Append(indent); lw = indW; empty = true; }
                while (lw + ww > maxW && word.Length > 1)       // a word longer than the line: cut it
                {
                    int n = word.Length;
                    while (n > 1 && lw + d.Measure(word.Substring(0, n)) > maxW) n = n * 3 / 4;
                    if (n < 1) n = 1;
                    line.Append(word, 0, n); into.Add(line.ToString()); line.Length = 0; line.Append(indent); lw = indW; empty = true;
                    word = word.Substring(n); ww = d.Measure(word);
                }
                if (!empty) { line.Append(' '); lw += space; }
                line.Append(word); lw += ww; empty = false;
            }
            if (!empty) into.Add(line.ToString());
        }

        public bool Toggle(bool v, string s, float w = -2)
        {
            if (w == -2) w = d.Measure(s) + 26;
            var r = Place(w, LineH);
            if (!Visible(r)) return v;
            Log("[toggle] " + s, r);
            var box = new Rect(r.x + 2, r.y + 4, 12, 12);
            d.Fill(box, Btn);
            if (v) d.Fill(new Rect(box.x + 2, box.y + 2, 8, 8), Accent);
            d.PushClip(r); d.Text(r.x + 20, r.y + 3, s, Txt); d.PopClip();
            if (Hover(r) && click) return !v;
            return v;
        }

        // tab strip; wraps onto more rows when the area is narrow
        public int Tabs(int sel, string[] names)
        {
            BeginRow();
            for (int i = 0; i < names.Length; i++)
            {
                float w = d.Measure(names[i]) + 14;
                if (rowX > left && rowX + w > right) { EndRow(); BeginRow(); }
                if (Button(names[i], w, i == sel ? TabSel : (Color?)null)) sel = i;
            }
            EndRow();
            return sel;
        }

        public float Slider(string id, float v, float min, float max, float w = -1)
        {
            var r = Place(w, LineH);
            if (!Visible(r)) return v;
            Log("[slider] " + id, r);
            var track = new Rect(r.x, r.y + 8, r.width, 4);
            d.Fill(track, Btn);
            float t = Mathf.InverseLerp(min, max, v);
            d.Fill(new Rect(r.x + t * (r.width - 8), r.y + 3, 8, 14), Accent);
            if (click && Hover(r)) activeDrag = "sl:" + id;
            if (activeDrag == "sl:" + id && held)
                v = Mathf.Lerp(min, max, Mathf.Clamp01((mouse.x - r.x) / r.width));
            return v;
        }

        // returns true on Enter
        public bool TextField(string id, ref string value, float w = -1)
        {
            var r = Place(w, LineH);
            if (!Visible(r)) return false;
            bool focused = focus == id;
            if (Hover(r) && click) { focus = id; focused = true; }
            d.Fill(r, focused ? new Color(0.02f, 0.03f, 0.05f, 1f) : new Color(0.12f, 0.13f, 0.16f, 1f));
            d.Frame(r, focused ? Accent : new Color(0.3f, 0.32f, 0.38f, 1f));
            bool enter = false;
            if (focused)
            {
                // Ctrl+V paste (first line), Ctrl+C copy the field, Ctrl+Backspace clear
                bool paste = EditorInput.ctrl && Input.GetKeyDown(KeyCode.V), copy = EditorInput.ctrl && Input.GetKeyDown(KeyCode.C);
                if (EditorInput.ctrl && Input.GetKeyDown(KeyCode.Backspace)) value = "";
                if (copy) Clipboard.Set(value ?? "");
                if (paste)
                {
                    string clip = Clipboard.Get(); int nl = clip.IndexOfAny(new[] { '\r', '\n' });
                    if (nl >= 0 && clip.IndexOf("<Address>", StringComparison.Ordinal) < 0) clip = clip.Substring(0, nl);
                    value = (value ?? "") + clip.Replace('\t', ' ').Replace("\r", " ").Replace("\n", " ");
                }
                foreach (char ch in EditorInput.chars)
                {
                    if (EditorInput.ctrl && ch != '\b') continue;   // Ctrl+letter shortcuts are not text
                    if (ch == '\b') { if (value.Length > 0) value = value.Substring(0, value.Length - 1); }
                    else if (ch == '\n' || ch == '\r') enter = true;
                    else if (ch == '`' || ch == 27) { }
                    else if (ch >= ' ') value += ch;
                }
                if (Input.GetKeyDown(KeyCode.Escape)) { focus = null; focused = false; }
            }
            string shown = value ?? "";
            float maxW = r.width - 10;
            while (shown.Length > 0 && d.Measure(shown) > maxW) shown = shown.Substring(1);
            d.PushClip(r);
            d.Text(r.x + 4, r.y + 3, shown + (focused && (Time.realtimeSinceStartup % 1f) < 0.55f ? "|" : ""), Txt);
            d.PopClip();
            return enter;
        }

        // ---------------------------------------------------------------- scroll regions
        string scrollId;
        float scrollTop, scrollStartY, scrollOffset, scrollHeight;
        readonly Dictionary<string, float> scrolls = new Dictionary<string, float>();
        readonly Dictionary<string, float> contentHeights = new Dictionary<string, float>();
        float savedAfter;
        Rect scrollRect;

        public void BeginScroll(string id, float height)
        {
            height = Mathf.Max(LineH, height);
            var r = Place(-1, height);
            scrollRect = r;
            d.Fill(r, new Color(0, 0, 0, 0.22f));
            float s; scrolls.TryGetValue(id, out s);
            float ch; contentHeights.TryGetValue(id, out ch);
            float maxS = Mathf.Max(0, ch - height);
            bool over = AreaHot && r.Contains(mouse);
            if (over && wheel != 0) s -= wheel * LineH * 3;
            // scrollbar (drag the thumb or click the track)
            var track = new Rect(r.xMax - 7, r.y, 7, height);
            if (ch > height)
            {
                float barH = Mathf.Max(20, height * height / ch);
                if (click && over && track.Contains(mouse)) { activeDrag = "sb:" + id; dragOffset = new Vector2(0, mouse.y); }
                if (activeDrag == "sb:" + id && held)
                    s = Mathf.Clamp01((mouse.y - r.y - barH * 0.5f) / Mathf.Max(1, height - barH)) * maxS;
                s = Mathf.Clamp(s, 0, maxS);
                float barY = r.y + (height - barH) * (maxS > 0 ? s / maxS : 0);
                d.Fill(new Rect(r.xMax - 6, barY, 5, barH), activeDrag == "sb:" + id ? new Color(1, 1, 1, 0.45f) : new Color(1, 1, 1, 0.25f));
            }
            else s = 0;
            scrolls[id] = s;
            scrollId = id; scrollTop = r.y; scrollOffset = s; scrollHeight = height;
            scrollStartY = r.y;
            savedAfter = r.yMax + 2;
            d.PushClip(new Rect(r.x, r.y, r.width - 8, r.height));
            cy = r.y - s;
            right -= 9;
        }

        public void EndScroll()
        {
            if (scrollId == null) return;
            contentHeights[scrollId] = cy + scrolls[scrollId] - scrollStartY;
            d.PopClip();
            scrollId = null;
            right += 9;
            cy = savedAfter;
        }

        // Virtualized list: only rows intersecting the viewport are laid out. `pitch` = row height incl. spacing.
        // Virtualized list: only rows intersecting the viewport are laid out. `pitch` = default row height; rows that
        // wrap onto more lines are measured when drawn and keep their real height (variable-height rows).
        readonly Dictionary<string, List<float>> vlHeights = new Dictionary<string, List<float>>();
        public void VirtualList(string id, int count, float pitch, float height, Action<int> row)
        {
            List<float> hs;
            if (!vlHeights.TryGetValue(id, out hs)) vlHeights[id] = hs = new List<float>();
            if (hs.Count > count) hs.RemoveRange(count, hs.Count - count);
            while (hs.Count < count) hs.Add(pitch);
            BeginScroll(id, height);
            float top = scrollTop - scrollOffset;
            int i = 0; float y = 0;
            while (i < count && y + hs[i] < scrollOffset) { y += hs[i]; i++; }
            float bottom = scrollOffset + scrollHeight;
            for (; i < count && y < bottom; i++)
            {
                cy = top + y; float c0 = cy;
                try { row(i); } catch (Exception e) { if (inRow) EndRow(); DevLog.Error("virtual list row", e); }
                if (inRow) EndRow();
                float h = cy - c0; if (h <= 0.5f) h = pitch;
                hs[i] = h; y += h;
            }
            for (; i < count; i++) y += hs[i];
            cy = top + y;
            EndScroll();
        }

        public const float RowPitch = LineH + 2;     // BeginRow/EndRow with standard widgets
        public const float ItemPitch = LineH;        // single Item/Label line

        // Drag handle ("scrubber"): drag left to decrease, right to increase. Shift = fine (x0.1), Ctrl = coarse (x10).
        float dragLastX;
        public float DragHandle(string id, string label, float v, float speedPerPx, float w = 18)
        {
            var r = Place(w, LineH);
            if (!Visible(r)) return v;
            Log("[drag] " + id, r);
            string key = "dv:" + id;
            bool active = activeDrag == key;
            bool hot = Hover(r);
            if (click && hot) { activeDrag = key; dragLastX = mouse.x; active = true; focus = null; }
            d.Fill(r, active ? Accent : hot ? BtnHot : Btn);
            d.PushClip(r); d.Text(r.x + 4, r.y + 3, label, active ? Color.black : Txt); d.PopClip();
            if (active && held)
            {
                float dx = mouse.x - dragLastX; dragLastX = mouse.x;
                float k = EditorInput.shift ? 0.1f : EditorInput.ctrl ? 10f : 1f;
                v += dx * speedPerPx * k;
            }
            return v;
        }
        public bool IsDragging(string id) { return activeDrag == "dv:" + id; }
        public bool AnyValueDrag { get { return activeDrag != null && activeDrag.StartsWith("dv:"); } }

        public bool FocusIs(string id) { return focus == id; }
        public void ScrollToEnd(string id) { scrolls[id] = 1e9f; }
        public void ScrollTo(string id, float y) { scrolls[id] = Mathf.Max(0, y); }
        public float ScrollOf(string id) { float s; return scrolls.TryGetValue(id, out s) ? s : 0f; }
        // true when the last placed widget/row rect is under the mouse (used for hover highlighting)
        public bool LastHover { get { return lastRect.Contains(mouse) && AreaHot; } }

        // ---------------------------------------------------------------- splitters
        // draggable bar; returns the drag delta along its axis this frame (0 if not dragging)
        public float Splitter(string id, Rect bar, bool vertical)
        {
            bool hot = bar.Contains(mouse) && (hotArea == null || hotArea == "split:" + id);
            areasNow.Add(new KeyValuePair<string, Rect>("split:" + id, bar));
            float delta = 0;
            if (click && hot) { activeDrag = "split:" + id; dragOffset = mouse; }
            bool dragging = activeDrag == "split:" + id && held;
            if (dragging)
            {
                delta = vertical ? mouse.x - dragOffset.x : mouse.y - dragOffset.y;
                dragOffset = mouse;
            }
            d.uiLayer = true;
            d.BeginSegment();
            d.Fill(bar, dragging ? Accent : hot ? BtnHot : new Color(0.16f, 0.18f, 0.22f, 0.95f));
            d.uiLayer = false;
            return delta;
        }

        // small square icon button drawn with primitives (no icon font available)
        public enum Glyph { Close, Minus, Arrow }
        public bool IconButton(Rect r, Glyph g, string areaId)
        {
            bool hot = r.Contains(mouse) && hotArea == areaId && activeDrag == null;
            d.Fill(r, hot ? BtnHot : Btn);
            var c = new Vector2(r.center.x, r.center.y);
            float k = r.width * 0.28f;
            if (g == Glyph.Close) { d.Line(c + new Vector2(-k, -k), c + new Vector2(k, k), Txt); d.Line(c + new Vector2(-k, k), c + new Vector2(k, -k), Txt); }
            else if (g == Glyph.Minus) d.Fill(new Rect(c.x - k, c.y - 1, 2 * k, 2), Txt);
            else d.Triangle(c + new Vector2(-k, -k), c + new Vector2(k, 0), c + new Vector2(-k, k), Txt);
            return hot && click;
        }
    }
}
