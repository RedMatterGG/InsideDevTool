using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Audio Database panel: one searchable view over Wwise banks, game references, catalogued triggers and runtime
    // observation. Filters = spec 69; the detail pane answers what-can / what-does / why (spec 79-81).
    public static class AudioDbPanel
    {
        static AudioDb.Filter filter = AudioDb.Filter.WwiseEvents;
        static string text = "";
        static AudioDb.Rec sel;
        static List<AudioDb.Rec> rows = new List<AudioDb.Rec>();
        static string rowsKey; static float rowsAt;
        static string[] detail = new string[0]; static AudioDb.Rec detailFor; static float detailAt;

        static string statsLine; static float statsAt; static int rulesVersion = -1; static string rulesLine;
        static readonly Dictionary<AudioDb.Rec, KeyValuePair<int, string>> labels = new Dictionary<AudioDb.Rec, KeyValuePair<int, string>>();
        static int labelsVersion = -1; static string countLine; static int countFor = -1;
        // §83: where the references are (areas, loaded or not) and what kind of source they are
        static string RefSummary(AudioDb.Rec r)
        {
            if (r.kind == AudioDb.Kind.AreaTrigger) return "   area " + r.bank;
            if (r.refs.Count == 0) return "";
            var areas = new List<string>(); var provs = new List<string>(); bool loaded = false;
            foreach (var x in r.refs)
            {
                if (x.loaded && x.prov != "CODE") loaded = true;
                string a = x.area == null ? "?" : x.area.StartsWith("#") ? x.area.Substring(x.area.LastIndexOf('#') + 1) : x.area;
                int dot = a.IndexOf('.'); if (dot > 0 && a.StartsWith("#")) a = a.Substring(0, dot);
                if (!areas.Contains(a) && areas.Count < 3) areas.Add(a);
                if (!provs.Contains(x.prov)) provs.Add(x.prov);
            }
            return "   refs " + r.refs.Count + " (" + string.Join("/", provs.ToArray()) + ") in " + string.Join(", ", areas.ToArray()) + (loaded ? "  LOADED" : "");
        }

        public static void Select(AudioDb.Rec r) { sel = r; detailFor = null; }
        public static AudioDb.Rec Current { get { return sel; } }

        // phase N: controlled modification of the selected record (recorded in History, undoable, recordable as a mod)
        static string ruleInput = "";
        static void DrawRules(UI ui, string name)
        {
            if (name.StartsWith("#")) name = sel.id.ToString();
            string kind = sel.kind == AudioDb.Kind.Event ? "event" : sel.kind == AudioDb.Kind.SwitchGroup ? "switch" : sel.kind == AudioDb.Kind.StateGroup ? "state" : sel.kind == AudioDb.Kind.Rtpc ? "rtpc" : null;
            if (kind == null) return;
            ui.BeginRow();
            ui.Label("MODIFY:", new Color(1f, 0.7f, 0.35f, 1f), 60);
            if (kind == "event")
            {
                string mk = "mute:" + name, rk = "replace:" + name;
                bool muted = AudioRules.Get(mk) != null; string rep = AudioRules.Get(rk);
                if (ui.Button(muted ? "Unmute" : "Mute")) AudioRules.Change(mk, muted ? null : "true", "audio db");
                string nk = "nuke:" + name; bool nuked = AudioRules.Get(nk) != null;
                if (ui.Button(nuked ? "Un-nuke" : "Nuke sound", -2, nuked ? (Color?)new Color(0.8f, 0.25f, 0.2f, 1f) : null)) AudioRules.Change(nk, nuked ? null : "true", "audio db");
                ui.Label("replace with:", UI.Dim, 90);
                ui.TextField("adb_rule", ref ruleInput, 200);
                if (ui.Button("Replace") && ruleInput.Trim().Length > 0) AudioRules.Change(rk, ruleInput.Trim(), "audio db");
                if (rep != null && ui.Button("Stop replacing (" + rep + ")")) AudioRules.Change(rk, null, "audio db");
            }
            else
            {
                string k = kind + ":" + name; string cur = AudioRules.Get(k);
                ui.Label(kind == "rtpc" ? "force value:" : "force " + kind + " value:", UI.Dim, 130);
                ui.TextField("adb_rule", ref ruleInput, 200);
                if (ui.Button("Force") && ruleInput.Trim().Length > 0) AudioRules.Change(k, ruleInput.Trim(), "audio db");
                if (cur != null && ui.Button("Release (" + cur + ")")) AudioRules.Change(k, null, "audio db");
            }
            ui.EndRow();
            ui.Label("Nuke = the post is blocked completely: Wwise never gets it, the game sees a failed post and receives NO callbacks (no end, markers or beats) - anything waiting on this sound will wait forever; undo to restore.", new Color(1f, 0.55f, 0.45f, 1f));
            ui.Label("Mute = the game still posts it (callbacks still arrive) and it is stopped at once. Replace = another event plays instead. Force = every set of this switch / state / RTPC uses your value. All changes are in History (undo) and can be recorded as a mod.", UI.Dim);
        }

        public static void Draw(UI ui)
        {
            // everything below is cached: Stats / Query / Describe walk thousands of records and build strings
            if (statsLine == null || Time.realtimeSinceStartup - statsAt > 3f) { statsLine = AudioDb.Stats().Split('\n')[0]; statsAt = Time.realtimeSinceStartup; }
            ui.BeginRow();
            ui.Label(statsLine, UI.Dim, Mathf.Max(100, ui.Width - 130));
            if (ui.Button(AudioExport.Busy ? "Exporting…" : "Export metadata", 125) && !AudioExport.Busy) AudioExport.Run();
            ui.EndRow();
            if (AudioExport.status.Length > 0) ui.Label(AudioExport.status, UI.Dim);
            if (AudioRules.Count > 0)
            {
                ui.BeginRow();
                if (rulesVersion != AudioRules.Version) { rulesVersion = AudioRules.Version; var l = new List<string>(); foreach (var kv in AudioRules.All) l.Add(kv.Key + "=" + kv.Value); rulesLine = "AUDIO RULES ACTIVE: " + string.Join("   ", l.ToArray()); }
                ui.Label(rulesLine, new Color(1f, 0.7f, 0.35f, 1f), Mathf.Max(100, ui.Width - 90));
                if (ui.Button("Clear all", 80)) AudioRules.ClearAll("audio db");
                ui.EndRow();
                ui.BeginRow();
                if (ui.Button(AudioRules.nativeEnforce ? "Native enforcement: ON" : "Native enforcement: off", -2, AudioRules.nativeEnforce ? (Color?)new Color(0.8f, 0.45f, 0.2f, 1f) : null)) AudioRules.SetNative(!AudioRules.nativeEnforce);
                ui.Label("also applies the rules to calls that bypass the game's sound layer (e.g. start-up sounds)", UI.Dim);
                ui.EndRow();
            }
            ui.BeginRow();
            for (int i = 0; i < AudioDb.FilterNames.Length; i++)
                if (ui.Button(AudioDb.FilterNames[i], -2, (int)filter == i ? (Color?)UI.TabSel : null)) filter = (AudioDb.Filter)i;
            ui.EndRow();
            ui.BeginRow();
            ui.Label("Search:", null, 50);
            ui.TextField("adb_q", ref text, Mathf.Max(80, ui.Width - 4));
            ui.EndRow();
            string key = filter + "|" + text + "|" + AudioDb.Version;
            if (key != rowsKey || Time.realtimeSinceStartup - rowsAt > 2f) { rows = AudioDb.Query(filter, text, 5000); rowsKey = key; rowsAt = Time.realtimeSinceStartup; }
            if (countFor != rows.Count) { countFor = rows.Count; countLine = rows.Count + (rows.Count >= 5000 ? "+" : "") + " record(s)"; }
            ui.Label(countLine, UI.Dim);
            if (labelsVersion != AudioDb.Version) { labels.Clear(); labelsVersion = AudioDb.Version; }
            float listH = sel != null ? ui.Remaining * 0.42f : ui.Remaining;
            ui.VirtualList("adb_list", rows.Count, UI.ItemPitch, listH, i =>
            {
                var r = rows[i];
                if (r == sel) ui.RowHighlight(new Color(0.22f, 0.37f, 0.6f, 0.5f));
                AudioTrace.EvStat st; int posts = AudioTrace.evStats.TryGetValue(r.id, out st) ? st.posts : -1;
                KeyValuePair<int, string> lab;
                if (!labels.TryGetValue(r, out lab) || lab.Key != posts)
                {
                    lab = new KeyValuePair<int, string>(posts, r.kind.ToString().PadRight(12) + r.Name + "   [" + (r.bank ?? "-") + "]" + RefSummary(r) + (posts >= 0 ? "   posted " + posts : ""));
                    labels[r] = lab;
                }
                var c = r.kind == AudioDb.Kind.Bank ? new Color(0.5f, 1f, 0.55f, 1f) : r.kind == AudioDb.Kind.Music || r.music ? new Color(1f, 0.8f, 0.4f, 1f) : r.kind == AudioDb.Kind.AreaTrigger ? new Color(0.85f, 0.6f, 1f, 1f) : r.node == null && r.kind == AudioDb.Kind.Event ? new Color(1f, 0.45f, 0.4f, 1f) : UI.Txt;
                if (ui.Item(lab.Value, c)) Select(r);
            });
            if (sel == null) return;
            if (detailFor != sel || Time.realtimeSinceStartup - detailAt > 3f) { detail = AudioDb.Describe(sel).Split('\n'); detailFor = sel; detailAt = Time.realtimeSinceStartup; }
            ui.BeginRow();
            if (ui.Button("Close")) { sel = null; ui.EndRow(); return; }
            string evName = AudioTrace.Name(sel.id);
            if (sel.kind == AudioDb.Kind.Event && sel.node != null && !evName.StartsWith("#") && ui.Button("Audition")) SoundLibrary.Audition(evName);
            foreach (var x in sel.refs) if (x.live != null && x.live.go != null) { if (ui.Button("Select object")) Selection.Set(x.live.go, "audio db"); break; }
            ui.EndRow();
            DrawRules(ui, evName);
            ui.VirtualList("adb_detail", detail.Length, UI.ItemPitch, ui.Remaining, i =>
            {
                string l = detail[i];
                var c = l.Length > 0 && char.IsUpper(l[0]) && !l.StartsWith(" ") ? new Color(0.55f, 0.9f, 0.75f, 1f) : l.Contains("(inferred)") ? UI.Dim : UI.Txt;
                ui.Label(l, c);
            });
        }
    }
}
