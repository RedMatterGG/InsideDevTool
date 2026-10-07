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
            return res;
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
                    into.Add(new Hit { kind = "command", title = c.title, detail = c.category + (c.hint != null ? "   " + c.hint : ""), score = s + 1.5f, open = () => CommandPalette.lastResult = EditorCommands.Run(cc.id) });
                }
            });
            Register("objects", (q, w, into) =>
            {
                if (q.Length < 2 || !ObjectDatabase.Ready) return;
                foreach (var r in ObjectDatabase.Search(q, 40))
                {
                    var rr = r;
                    float s = Score(r.name, w); if (s < 0) s = 0.2f;
                    into.Add(new Hit { kind = "object", title = r.name, detail = r.path + (r.activeInHierarchy ? "" : "   (inactive)"), score = s, open = () => Selection.Set(rr.go, "search") });
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
                    into.Add(new Hit { kind = "history", title = e.Describe(), detail = "#" + e.id + " " + e.source, score = s - 0.5f, open = () => { if (go != null) Selection.Set(go, "search"); } });
                }
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
        static float hitsAt;

        public static void Toggle(UI ui) { open = !open; if (open) { query = ""; sel = 0; hitsFor = null; ui.focus = "palette_q"; } else if (ui.focus == "palette_q") ui.focus = null; }

        public static void Draw(UI ui)
        {
            if (!open) return;
            float w = Mathf.Min(720f, ui.ScreenW - 40f), h = Mathf.Min(460f, ui.ScreenH - 120f);
            var r = new Rect((ui.ScreenW - w) * 0.5f, 70f, w, h);
            ui.BeginArea("palette", r, new Color(0.06f, 0.065f, 0.08f, 0.98f));
            ui.D.Frame(r, UI.Accent);
            ui.Label("Search objects, commands, bookmarks, sounds, spawns, mods, history     Enter = open   Up/Down = choose   Esc = close", UI.Dim);
            if (ui.focus != "palette_q") ui.focus = "palette_q";
            bool enter = ui.TextField("palette_q", ref query, -1);
            if (query != hitsFor || Time.realtimeSinceStartup - hitsAt > 1f) { hits = GlobalSearch.Query(query, 60); if (query != hitsFor) sel = 0; hitsFor = query; hitsAt = Time.realtimeSinceStartup; }
            if (Input.GetKeyDown(KeyCode.DownArrow)) sel = Mathf.Min(sel + 1, hits.Count - 1);
            if (Input.GetKeyDown(KeyCode.UpArrow)) sel = Mathf.Max(sel - 1, 0);
            if (Input.GetKeyDown(KeyCode.Escape)) { open = false; ui.focus = null; ui.EndArea(); return; }
            if (lastResult.Length > 0) ui.Label(lastResult, new Color(0.6f, 1f, 0.7f, 1f));
            ui.VirtualList("palette_list", hits.Count, UI.ItemPitch, ui.Remaining, i =>
            {
                var hh = hits[i];
                if (i == sel) ui.RowHighlight(new Color(0.22f, 0.37f, 0.6f, 0.6f));
                var c = hh.kind == "command" ? new Color(0.6f, 0.85f, 1f, 1f) : hh.kind == "object" ? UI.Txt : hh.kind == "bookmark" ? new Color(1f, 0.85f, 0.4f, 1f) : hh.kind == "sound" ? new Color(0.85f, 0.6f, 1f, 1f) : UI.Dim;
                if (ui.Item(hh.kind.ToUpperInvariant().PadRight(10) + hh.title + "      " + hh.detail, c)) { sel = i; Open(hh); }
            });
            ui.EndArea();
            if (enter && sel >= 0 && sel < hits.Count) Open(hits[sel]);
        }

        static void Open(GlobalSearch.Hit h)
        {
            lastResult = "";
            try { if (h.open != null) h.open(); }
            catch (Exception e) { lastResult = "error: " + e.Message; }
            if (h.kind != "command" || lastResult.Length == 0) open = h.kind == "command" && lastResult.Length > 0;
        }

        // bridge: palette <query>  -> list;  palette <query> #n -> open the n-th hit
        public static string Bridge(string q, int openIndex)
        {
            var l = GlobalSearch.Query(q, 40);
            if (openIndex >= 0)
            {
                if (openIndex >= l.Count) return "no hit #" + openIndex;
                lastResult = "";
                l[openIndex].open();
                return "opened " + l[openIndex].kind + " " + l[openIndex].title + (lastResult.Length > 0 ? " -> " + lastResult : "");
            }
            var sb = new System.Text.StringBuilder(l.Count + " hit(s)\n");
            for (int i = 0; i < l.Count; i++) sb.Append('#').Append(i).Append(' ').Append(l[i].kind).Append("  ").Append(l[i].title).Append("   ").Append(l[i].detail).Append("   (").Append(l[i].score.ToString("0.0")).Append(")\n");
            return sb.ToString();
        }
    }
}
