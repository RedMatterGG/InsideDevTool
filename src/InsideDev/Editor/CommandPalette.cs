using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Phase 11: command registry + global search + command palette (Ctrl+P, or Ctrl+K).
    // Search is provider based: every data source registers a provider, so later databases (the audio database,
    // offline bank data) plug into the same box instead of getting their own search (spec 102).
    public static class EditorCommands
    {
        public sealed class Cmd { public string id, title, category, hint; public Func<string> run; public Func<bool> available; }
        public static readonly List<Cmd> all = new List<Cmd>();

        public static void Register(string id, string title, string category, Func<string> run, string hint = null, Func<bool> available = null)
        {
            all.RemoveAll(c => c.id == id);
            all.Add(new Cmd { id = id, title = title, category = category, run = run, hint = hint, available = available });
        }

        public static void Unregister(string id) { all.RemoveAll(c => c.id == id); }

        public static string Run(string id)
        {
            foreach (var c in all)
                if (c.id == id)
                {
                    if (c.available != null && !c.available()) return c.title + ": not available right now";
                    try { var r = c.run(); DevLog.Write("[command] " + c.title + (string.IsNullOrEmpty(r) ? "" : ": " + r)); return r ?? ""; }
                    catch (Exception e) { DevLog.Error("command " + id, e); return "error: " + e.Message; }
                }
            return "no command " + id;
        }
    }

    public static class GlobalSearch
    {
        public sealed class Hit
        {
            public string kind, title, detail; public float score;
            public Action open;          // Enter / click
            // "<kind>:<name or id>", the same for the same thing in every query: "palette open <key>" opens exactly this
            public string key;
            internal string row;         // list row, built when first drawn
        }
        public delegate void Provider(string q, string[] words, List<Hit> into);
        static readonly List<KeyValuePair<string, Provider>> providers = new List<KeyValuePair<string, Provider>>();

        public static void Register(string name, Provider p) { providers.RemoveAll(kv => kv.Key == name); providers.Add(new KeyValuePair<string, Provider>(name, p)); }

        // simple scoring: every word must occur; prefix / whole-word matches rank higher; shorter titles rank higher
        public static float Score(string text, string[] words)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            string t = text.ToLowerInvariant();
            float s = 0;
            foreach (var w in words)
            {
                int i = t.IndexOf(w, StringComparison.Ordinal);
                if (i < 0) return -1;
                s += i == 0 ? 3f : (t[i - 1] == ' ' || t[i - 1] == '/' || t[i - 1] == '_' || t[i - 1] == '.') ? 2f : 1f;
            }
            return s - t.Length * 0.002f;
        }

        public static List<Hit> Query(string q, int max)
        {
            var res = new List<Hit>();
            q = (q ?? "").Trim();
            var words = q.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var kv in providers)
            {
                try { kv.Value(q, words, res); }
                catch (Exception e) { DevLog.Error("search provider " + kv.Key, e); }
            }
            res.Sort((a, b) => b.score.CompareTo(a.score));
            if (res.Count > max) res.RemoveRange(max, res.Count - max);
            foreach (var h in res) if (h.key == null) h.key = h.kind + ":" + h.title;
            return res;
        }

        static string FirstLine(string s) { if (s == null) return ""; int n = s.IndexOf('\n'); return n < 0 ? s : s.Substring(0, n); }

        // "cmd layout-<name>" and the palette entry "Layout: <name>"
        internal static void LayoutCommand(DevCore core, string name)
        {
            if (core == null) return;
            EditorCommands.Register("layout-" + name, "Layout: " + name, "layout", () => FirstLine(core.workspace.LoadLayout(name)), Array.IndexOf(Workspace.Presets, name) >= 0 ? "built in" : "saved");
        }

        // ---------------------------------------------------------------- built-in providers
        public static void RegisterDefaults(DevCore core)
        {
            Register("commands", (q, w, into) =>
            {
                foreach (var c in EditorCommands.all)
                {
                    if (c.available != null && !c.available()) continue;
                    float s = w.Length == 0 ? 0.5f : Score(c.title + " " + c.category, w);
                    if (s < 0) continue;
                    var cc = c;
                    into.Add(new Hit { kind = "command", key = "command:" + c.id, title = c.title, detail = c.category + (c.hint != null ? "   " + c.hint : ""), score = s + 1.5f, open = () => CommandPalette.lastResult = EditorCommands.Run(cc.id) });
                }
            });
            Register("objects", (q, w, into) =>
            {
                if (q.Length < 2 || !ObjectDatabase.Ready) return;
                foreach (var r in ObjectDatabase.Search(q, 40))
                {
                    var rr = r;
                    float s = Score(r.name, w); if (s < 0) s = 0.2f;
                    into.Add(new Hit { kind = "object", key = "object:" + Fmt.I(r.id), title = r.name, detail = r.path + (r.activeInHierarchy ? "" : "   (inactive)"), score = s, open = () => Selection.Set(rr.go, "search") });
                }
            });
            Register("bookmarks", (q, w, into) =>
            {
                foreach (var m in Bookmarks.marks)
                {
                    float s = w.Length == 0 ? 0.4f : Score(m.name + " " + m.note + " " + m.selector, w);
                    if (s < 0) continue;
                    var mm = m;
                    into.Add(new Hit { kind = "bookmark", title = m.name, detail = (m.note.Length > 0 ? m.note + "   " : "") + m.area, score = s + 1f, open = () => CommandPalette.lastResult = Bookmarks.Go(mm, true) });
                }
            });
            Register("sounds", (q, w, into) =>
            {
                if (q.Length < 2) return;
                foreach (var d in SoundLibrary.defs.Values)
                {
                    float s = Score(d.name, w); if (s < 0) continue;
                    var dd = d;
                    into.Add(new Hit { kind = "sound", title = d.name, detail = "used by " + d.UsedBy + ", posts " + d.posts, score = s, open = () => { SoundsPanel.Focus(dd.name); core.ShowPanel("sounds"); } });
                }
            });
            Register("audio", (q, w, into) =>
            {
                if (q.Length < 3) return;
                foreach (var r in AudioDb.Query(AudioDb.Filter.All, q, 30))
                {
                    var rr = r;
                    float s = Score(r.Name, w); if (s < 0) s = 0.3f;
                    into.Add(new Hit { kind = "audio", title = r.Name, detail = r.kind + (r.bank != null ? "  [" + r.bank + "]" : "") + (r.refs.Count > 0 ? "  refs " + r.refs.Count : ""), score = s, open = () => { AudioDbPanel.Select(rr); core.ShowPanel("audiodb"); } });
                }
            });
            Register("spawns", (q, w, into) =>
            {
                if (q.Length < 2) return;
                for (int i = 0; i < CustomSpawns.entries.Count; i++)
                {
                    var e = CustomSpawns.entries[i];
                    float s = Score(e.name + " " + e.subscene, w); if (s < 0) continue;
                    int ii = i;
                    into.Add(new Hit { kind = "spawn", title = e.name, detail = "custom spawn in " + e.subscene, score = s, open = () => CommandPalette.lastResult = CustomSpawns.Go(ii) });
                }
            });
            Register("mods", (q, w, into) =>
            {
                foreach (var m in Mods.mods)
                {
                    float s = w.Length == 0 ? 0.3f : Score(m.name + " " + m.description, w); if (s < 0) continue;
                    into.Add(new Hit { kind = "mod", title = m.name, detail = (m.enabled ? "enabled" : "disabled") + ", " + m.Applied + "/" + m.ops.Count + " applied", score = s, open = () => core.ShowPanel("history") });
                }
            });
            Register("history", (q, w, into) =>
            {
                if (q.Length < 2) return;
                var h = ChangeRecorder.Entries;
                for (int i = h.Count - 1; i >= 0 && i >= h.Count - 100; i--)
                {
                    var e = h[i];
                    float s = Score(e.Describe(), w); if (s < 0) continue;
                    var go = e.action != null ? e.action.go : e.ops.Count > 0 ? e.ops[0].prop.go : null;
                    into.Add(new Hit { kind = "history", key = "history:" + Fmt.I(e.id), title = e.Describe(), detail = "#" + e.id + " " + e.source, score = s - 0.5f, open = () => { if (go != null) Selection.Set(go, "search"); } });
                }
            });
            // named layouts: every one is a command ("Layout: audio", cmd layout-audio); typing
            // "layout save <name>" or "layout delete <name>" offers that step
            foreach (var n in core.workspace.Layouts()) LayoutCommand(core, n);
            Register("layouts", (q, w, into) =>
            {
                if (w.Length < 3 || w[0] != "layout") return;
                string n = Workspace.LayoutName(w[2]);
                if (n == null) return;
                if (w[1] == "save")
                    into.Add(new Hit { kind = "layout", key = "layout-save:" + n, title = "Save the current layout as '" + n + "'", detail = "docks, panel placement and active tabs", score = 9f, open = () => CommandPalette.lastResult = core.workspace.SaveLayout(n) });
                else if (w[1] == "delete")
                    into.Add(new Hit { kind = "layout", key = "layout-delete:" + n, title = "Delete the saved layout '" + n + "'", detail = "the current arrangement stays as it is", score = 9f, open = () => CommandPalette.lastResult = core.workspace.DeleteLayout(n) });
            });
        }
    }

    public static class CommandPalette
    {
        public static bool open;
        public static string query = "";
        public static string lastResult = "";
        static int sel;
        static List<GlobalSearch.Hit> hits = new List<GlobalSearch.Hit>();
        static string hitsFor;
        static long hitsKey;
        static SearchDelay queryDelay;
        static int drawnFrame = -10;
        static readonly List<GlobalSearch.Hit> recent = new List<GlobalSearch.Hit>();   // what was opened last, newest first
        const int MaxRecent = 8;
        const string ListId = "palette_list", FieldId = "palette_q";
        static readonly Color cBack = new Color(0.06f, 0.065f, 0.08f, 0.98f), cSel = new Color(0.22f, 0.37f, 0.6f, 0.6f), cResult = new Color(0.6f, 1f, 0.7f, 1f),
            cCommand = new Color(0.6f, 0.85f, 1f, 1f), cBookmark = new Color(1f, 0.85f, 0.4f, 1f), cSound = new Color(0.85f, 0.6f, 1f, 1f);
        static UI curUi;
        static readonly Action<int> drawRow = DrawRow;

        public static void Toggle(UI ui)
        {
            open = !open;
            if (open) { query = ""; sel = 0; hitsFor = null; queryDelay.Set(""); ui.focus = FieldId; drawnFrame = Time.frameCount; }
            else if (ui.focus == FieldId) ui.focus = null;
        }

        static void Close(UI ui) { open = false; if (ui.focus == FieldId) ui.focus = null; }

        // everything a query can depend on besides its text: the lists the providers read
        static long DataKey() { return SceneStamp.Value * 1000003L + Bookmarks.marks.Count * 8191L + ChangeRecorder.Version * 131L + Mods.mods.Count * 31L + EditorCommands.all.Count; }

        public static void Draw(UI ui)
        {
            if (!open) return;
            // the editor panel was closed while the palette was open (F1): it does not come back with the panel
            if (Time.frameCount - drawnFrame > 1) { Close(ui); return; }
            drawnFrame = Time.frameCount;
            float w = Mathf.Min(720f, ui.ScreenW - 40f), h = Mathf.Min(460f, ui.ScreenH - 120f);
            var r = new Rect((ui.ScreenW - w) * 0.5f, 70f, w, h);
            ui.BeginArea("palette", r, cBack);
            ui.D.Frame(r, UI.Accent);
            ui.Label("Search objects, commands, bookmarks, sounds, spawns, mods, history     Enter = open   Up/Down, PgUp/PgDn, Home/End = choose   Esc = close", UI.Dim);
            if (ui.focus != FieldId) ui.focus = FieldId;
            bool enter = EditorInput.Field(ui, FieldId, ref query, -1);
            // the search runs when the typing pauses, and again only when one of the searched lists changed
            string q = queryDelay.Value(query, 0.1f);
            if (enter && q != query) { queryDelay.Set(query); q = query; }
            long dk = DataKey();
            if (q != hitsFor || dk != hitsKey)
            {
                if (q != hitsFor) sel = 0;
                hits = GlobalSearch.Query(q, 60);
                if (q.Trim().Length == 0 && recent.Count > 0)
                {
                    // empty box: what was opened last comes first
                    for (int i = hits.Count - 1; i >= 0; i--) foreach (var x in recent) if (x.key == hits[i].key) { hits.RemoveAt(i); break; }
                    hits.InsertRange(0, recent);
                }
                hitsFor = q; hitsKey = dk;
            }
            int before = sel, page = Mathf.Max(1, (int)((h - 90f) / UI.ItemPitch) - 1);
            if (Input.GetKeyDown(KeyCode.DownArrow)) sel++;
            if (Input.GetKeyDown(KeyCode.UpArrow)) sel--;
            if (Input.GetKeyDown(KeyCode.PageDown)) sel += page;
            if (Input.GetKeyDown(KeyCode.PageUp)) sel -= page;
            if (Input.GetKeyDown(KeyCode.Home)) sel = 0;
            if (Input.GetKeyDown(KeyCode.End)) sel = hits.Count - 1;
            sel = Mathf.Clamp(sel, 0, Mathf.Max(0, hits.Count - 1));
            if (Input.GetKeyDown(KeyCode.Escape)) { open = false; ui.focus = null; ui.EndArea(); return; }
            if (lastResult.Length > 0) ui.Label(lastResult, cResult);
            float viewH = ui.Remaining;
            if (sel != before)
            {
                // keep the chosen row in view
                float y = sel * UI.ItemPitch, cur = ui.ScrollOf(ListId);
                if (y < cur) ui.ScrollTo(ListId, y);
                else if (y + UI.ItemPitch > cur + viewH) ui.ScrollTo(ListId, y + UI.ItemPitch - viewH);
            }
            curUi = ui;
            ui.VirtualList(ListId, hits.Count, UI.ItemPitch, viewH, drawRow);
            ui.EndArea();
            if (clicked != null) { var c = clicked; clicked = null; Open(c); }
            else if (enter && sel >= 0 && sel < hits.Count) Open(hits[sel]);
            if (!open && ui.focus == FieldId) ui.focus = null;
        }

        static GlobalSearch.Hit clicked;

        static void DrawRow(int i)
        {
            if (i >= hits.Count) return;
            var hh = hits[i];
            if (i == sel) curUi.RowHighlight(cSel);
            var c = hh.kind == "command" ? cCommand : hh.kind == "object" ? UI.Txt : hh.kind == "bookmark" ? cBookmark : hh.kind == "sound" ? cSound : UI.Dim;
            if (hh.row == null) hh.row = hh.kind.ToUpperInvariant().PadRight(10) + hh.title + "      " + hh.detail;
            if (curUi.Item(hh.row, c)) { sel = i; clicked = hh; }
        }

        static void Open(GlobalSearch.Hit h)
        {
            lastResult = "";
            try { if (h.open != null) h.open(); }
            catch (Exception e) { lastResult = "error: " + e.Message; }
            Remember(h);
            // the palette stays only to show what a command answered
            open = h.kind == "command" && lastResult.Length > 0;
        }

        static void Remember(GlobalSearch.Hit h)
        {
            if (h.key == null || h.key.StartsWith("layout-", StringComparison.Ordinal)) return;
            recent.RemoveAll(x => x.key == h.key);
            recent.Insert(0, h);
            if (recent.Count > MaxRecent) recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
            hitsFor = null;
        }

        // Opens the thing a key names, wherever it stands in a list today. command:<id>, object:<instance id> and
        // history:<entry id> are looked up directly, every other kind by its name.
        public static string OpenKey(string key)
        {
            key = (key ?? "").Trim();
            int colon = key.IndexOf(':');
            if (colon <= 0 || colon == key.Length - 1) return "usage: palette open <kind>:<name or id>   (the key printed with each hit)";
            string kind = key.Substring(0, colon), name = key.Substring(colon + 1);
            if (kind == "command") { string r = EditorCommands.Run(name); return r.StartsWith("no command ", StringComparison.Ordinal) ? "not found: " + key : "opened command " + name + (r.Length > 0 ? " -> " + r : ""); }
            if (kind == "object")
            {
                int id; if (!Fmt.TryI(name, out id)) return "usage: palette open object:<instance id>";
                var rec = ObjectDatabase.Get(id);
                if (rec == null) return "not found: " + key + " (ids die on every load)";
                Selection.Set(rec.go, "search");
                return "opened object " + rec.name;
            }
            if (kind == "history")
            {
                int id; if (!Fmt.TryI(name, out id)) return "usage: palette open history:<entry id>";
                foreach (var e in ChangeRecorder.Entries)
                    if (e.id == id)
                    {
                        var go = e.action != null ? e.action.go : e.ops.Count > 0 ? e.ops[0].prop.go : null;
                        if (go == null) return "not found: the object of history entry " + id + " is gone";
                        Selection.Set(go, "search");
                        return "opened history " + e.Describe();
                    }
                return "not found: " + key;
            }
            foreach (var h in GlobalSearch.Query(name, 200))
                if (h.key == key)
                {
                    lastResult = "";
                    if (h.open != null) h.open();
                    return "opened " + h.kind + " " + h.title + (lastResult.Length > 0 ? " -> " + lastResult : "");
                }
            return "not found: " + key;
        }

        // bridge: palette <query>  -> list;  palette <query> #n -> open the n-th hit;  palette open <key> -> open that hit;
        //         palette <query> json -> the list as one JSON line { ok, query, hits: [ { n, kind, title, detail, score, key } ] }
        public static string Bridge(string q, int openIndex)
        {
            q = (q ?? "").Trim();
            if (openIndex < 0 && q.StartsWith("open ", StringComparison.Ordinal) && q.IndexOf(':') > 5) return OpenKey(q.Substring(5));
            bool json = openIndex < 0 && (q == "json" || q.EndsWith(" json", StringComparison.Ordinal));
            if (json) q = q.Substring(0, q.Length - 4).Trim();
            var l = GlobalSearch.Query(q, 40);
            if (openIndex >= 0)
            {
                if (openIndex >= l.Count) return "no hit #" + openIndex;
                lastResult = "";
                if (l[openIndex].open != null) l[openIndex].open();
                return "opened " + l[openIndex].kind + " " + l[openIndex].title + (lastResult.Length > 0 ? " -> " + lastResult : "");
            }
            if (json)
            {
                var rows = new List<object>();
                for (int i = 0; i < l.Count; i++)
                {
                    var o = new Json.Obj();
                    o["n"] = i; o["kind"] = l[i].kind; o["title"] = l[i].title; o["detail"] = l[i].detail; o["score"] = l[i].score; o["key"] = l[i].key;
                    rows.Add(o);
                }
                var root = new Json.Obj(); root["ok"] = true; root["query"] = q; root["hits"] = rows;
                return JsonLine.Write(root);
            }
            var sb = new System.Text.StringBuilder(l.Count + " hit(s)\n");
            for (int i = 0; i < l.Count; i++) sb.Append('#').Append(i).Append(' ').Append(l[i].kind).Append("  ").Append(l[i].title).Append("   ").Append(l[i].detail).Append("   (").Append(Fmt.F(l[i].score, "0.0")).Append(")   [").Append(l[i].key).Append("]\n");
            return sb.ToString();
        }
    }
}
