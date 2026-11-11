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
        static AudioDb.Filter rowsFilter; static string rowsText; static int rowsVersion = -1; static float rowsAt;
        static string[] detail = new string[0]; static Color[] detailColor = new Color[0]; static AudioDb.Rec detailFor; static float detailAt;
        static SearchDelay textDelay;

        static string statsLine; static float statsAt; static int rulesVersion = -1; static string rulesLine;
        static readonly Dictionary<AudioDb.Rec, KeyValuePair<int, string>> labels = new Dictionary<AudioDb.Rec, KeyValuePair<int, string>>();
        static int labelsVersion = -1; static string countLine; static int countFor = -1;
        static readonly Color cRule = new Color(1f, 0.7f, 0.35f, 1f), cNuke = new Color(0.8f, 0.25f, 0.2f, 1f), cNukeText = new Color(1f, 0.55f, 0.45f, 1f), cNative = new Color(0.8f, 0.45f, 0.2f, 1f),
            cSelRow = new Color(0.22f, 0.37f, 0.6f, 0.5f), cBank = new Color(0.5f, 1f, 0.55f, 1f), cMusic = new Color(1f, 0.8f, 0.4f, 1f), cArea = new Color(0.85f, 0.6f, 1f, 1f), cMissing = new Color(1f, 0.45f, 0.4f, 1f),
            cDetailHead = new Color(0.55f, 0.9f, 0.75f, 1f);
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

        public static void Select(AudioDb.Rec r) { sel = r; detailFor = null; ruleFor = null; }
        public static AudioDb.Rec Current { get { return sel; } }

        // phase N: controlled modification of the selected record (recorded in History, undoable, recordable as a mod)
        static string ruleInput = "";
        // rule keys, their state and the button texts for the selected record: read again when the rules or the selection change
        static AudioDb.Rec ruleFor; static int ruleVersion = -1;
        static string ruleKind, rkMute, rkReplace, rkNuke, rkForce, ruleReplace, ruleForce, lblStop, lblRelease, lblForce;
        static bool ruleMuted, ruleNuked;

        static void DrawRules(UI ui, string name)
        {
            if (ruleFor != sel || ruleVersion != AudioRules.Version)
            {
                ruleFor = sel; ruleVersion = AudioRules.Version;
                if (name.StartsWith("#", StringComparison.Ordinal)) name = sel.id.ToString();
                ruleKind = sel.kind == AudioDb.Kind.Event ? "event" : sel.kind == AudioDb.Kind.SwitchGroup ? "switch" : sel.kind == AudioDb.Kind.StateGroup ? "state" : sel.kind == AudioDb.Kind.Rtpc ? "rtpc" : null;
                if (ruleKind == "event")
                {
                    rkMute = "mute:" + name; rkReplace = "replace:" + name; rkNuke = "nuke:" + name;
                    ruleMuted = AudioRules.Get(rkMute) != null; ruleReplace = AudioRules.Get(rkReplace); ruleNuked = AudioRules.Get(rkNuke) != null;
                    lblStop = ruleReplace != null ? "Stop replacing (" + ruleReplace + ")" : null;
                }
                else if (ruleKind != null)
                {
                    rkForce = ruleKind + ":" + name; ruleForce = AudioRules.Get(rkForce);
                    lblForce = ruleKind == "rtpc" ? "force value:" : "force " + ruleKind + " value:";
                    lblRelease = ruleForce != null ? "Release (" + ruleForce + ")" : null;
                }
            }
            if (ruleKind == null) return;
            ui.BeginRow();
            ui.Label("MODIFY:", cRule, 60);
            if (ruleKind == "event")
            {
                if (ui.Button(ruleMuted ? "Unmute" : "Mute")) AudioRules.Change(rkMute, ruleMuted ? null : "true", "audio db");
                if (ui.Button(ruleNuked ? "Un-nuke" : "Nuke sound", -2, ruleNuked ? (Color?)cNuke : null)) AudioRules.Change(rkNuke, ruleNuked ? null : "true", "audio db");
                ui.Label("replace with:", UI.Dim, 90);
                EditorInput.Field(ui, "adb_rule", ref ruleInput, 200);
                if (ui.Button("Replace") && ruleInput.Trim().Length > 0) AudioRules.Change(rkReplace, ruleInput.Trim(), "audio db");
                if (lblStop != null && ui.Button(lblStop)) AudioRules.Change(rkReplace, null, "audio db");
            }
            else
            {
                ui.Label(lblForce, UI.Dim, 130);
                EditorInput.Field(ui, "adb_rule", ref ruleInput, 200);
                if (ui.Button("Force") && ruleInput.Trim().Length > 0) AudioRules.Change(rkForce, ruleInput.Trim(), "audio db");
                if (lblRelease != null && ui.Button(lblRelease)) AudioRules.Change(rkForce, null, "audio db");
            }
            ui.EndRow();
            ui.Label("Nuke = the post is blocked completely: Wwise never gets it, the game sees a failed post and receives NO callbacks (no end, markers or beats) - anything waiting on this sound will wait forever; undo to restore.", cNukeText);
            ui.Label("Mute = the game still posts it (callbacks still arrive) and it is stopped at once. Replace = another event plays instead. Force = every set of this switch / state / RTPC uses your value. All changes are in History (undo) and can be recorded as a mod.", UI.Dim);
        }

        static UI curUi;
        static readonly Action<int> drawRow = DrawRow, drawDetail = DrawDetail;

        public static void Draw(UI ui)
        {
            Background.Want(Background.Audio, 5f);
            Background.Want(Background.Catalog, 5f);
            float now = Time.realtimeSinceStartup;
            // everything below is cached: Stats / Query / Describe walk thousands of records and build strings
            if (statsLine == null || now - statsAt > 3f) { statsLine = AudioDb.Stats().Split('\n')[0]; statsAt = now; }
            ui.BeginRow();
            ui.Label(statsLine, UI.Dim, Mathf.Max(100, ui.Width - 130));
            if (ui.Button(AudioExport.Busy ? "Exporting…" : "Export metadata", 125) && !AudioExport.Busy) AudioExport.Run();
            ui.EndRow();
            if (AudioExport.status.Length > 0) ui.Label(AudioExport.status, UI.Dim);
            if (AudioRules.Count > 0)
            {
                ui.BeginRow();
                if (rulesVersion != AudioRules.Version) { rulesVersion = AudioRules.Version; var l = new List<string>(); foreach (var kv in AudioRules.All) l.Add(kv.Key + "=" + kv.Value); rulesLine = "AUDIO RULES ACTIVE: " + string.Join("   ", l.ToArray()); }
                ui.Label(rulesLine, cRule, Mathf.Max(100, ui.Width - 90));
                if (ui.Button("Clear all", 80)) AudioRules.ClearAll("audio db");
                ui.EndRow();
                ui.BeginRow();
                if (ui.Button(AudioRules.nativeEnforce ? "Native enforcement: ON" : "Native enforcement: off", -2, AudioRules.nativeEnforce ? (Color?)cNative : null)) AudioRules.SetNative(!AudioRules.nativeEnforce);
                ui.Label("also applies the rules to calls that bypass the game's sound layer (e.g. start-up sounds)", UI.Dim);
                ui.EndRow();
            }
            ui.BeginRow();
            for (int i = 0; i < AudioDb.FilterNames.Length; i++)
                if (ui.Button(AudioDb.FilterNames[i], -2, (int)filter == i ? (Color?)UI.TabSel : null)) filter = (AudioDb.Filter)i;
            ui.EndRow();
            ui.BeginRow();
            ui.Label("Search:", null, 50);
            EditorInput.Field(ui, "adb_q", ref text, Mathf.Max(80, ui.Width - 4));
            ui.EndRow();
            // the list is queried when the search text settled, the filter or the database changed, and every 2 s
            string q = textDelay.Value(text, 0.12f);
            if (filter != rowsFilter || q != rowsText || AudioDb.Version != rowsVersion || now - rowsAt > 2f)
            {
                rows = AudioDb.Query(filter, q, 5000);
                rowsFilter = filter; rowsText = q; rowsVersion = AudioDb.Version; rowsAt = now;
            }
            if (countFor != rows.Count) { countFor = rows.Count; countLine = rows.Count + (rows.Count >= 5000 ? "+" : "") + " record(s)"; }
            ui.Label(countLine, UI.Dim);
            if (labelsVersion != AudioDb.Version) { labels.Clear(); labelsVersion = AudioDb.Version; }
            float listH = sel != null ? ui.Remaining * 0.42f : ui.Remaining;
            curUi = ui;
            ui.VirtualList("adb_list", rows.Count, UI.ItemPitch, listH, drawRow);
            if (sel == null) return;
            if (detailFor != sel || now - detailAt > 3f)
            {
                detail = AudioDb.Describe(sel).Split('\n'); detailFor = sel; detailAt = now;
                detailColor = new Color[detail.Length];
                for (int i = 0; i < detail.Length; i++)
                {
                    string l = detail[i];
                    detailColor[i] = l.Length > 0 && char.IsUpper(l[0]) ? cDetailHead : l.Contains("(inferred)") ? UI.Dim : UI.Txt;
                }
            }
            ui.BeginRow();
            if (ui.Button("Close")) { sel = null; ui.EndRow(); return; }
            string evName = AudioTrace.Name(sel.id);
            if (sel.kind == AudioDb.Kind.Event && sel.node != null && !evName.StartsWith("#", StringComparison.Ordinal) && ui.Button("Audition")) SoundLibrary.Audition(evName);
            foreach (var x in sel.refs) if (x.live != null && x.live.go != null) { if (ui.Button("Select object")) Selection.Set(x.live.go, "audio db"); break; }
            ui.EndRow();
            DrawRules(ui, evName);
            ui.VirtualList("adb_detail", detail.Length, UI.ItemPitch, ui.Remaining, drawDetail);
        }

        static void DrawRow(int i)
        {
            if (i >= rows.Count) return;
            var ui = curUi;
            var r = rows[i];
            if (r == sel) ui.RowHighlight(cSelRow);
            AudioTrace.EvStat st; int posts = AudioTrace.evStats.TryGetValue(r.id, out st) ? st.posts : -1;
            KeyValuePair<int, string> lab;
            if (!labels.TryGetValue(r, out lab) || lab.Key != posts)
            {
                lab = new KeyValuePair<int, string>(posts, r.kind.ToString().PadRight(12) + r.Name + "   [" + (r.bank ?? "-") + "]" + RefSummary(r) + (posts >= 0 ? "   posted " + posts : ""));
                labels[r] = lab;
            }
            var c = r.kind == AudioDb.Kind.Bank ? cBank : r.kind == AudioDb.Kind.Music || r.music ? cMusic : r.kind == AudioDb.Kind.AreaTrigger ? cArea : r.node == null && r.kind == AudioDb.Kind.Event ? cMissing : UI.Txt;
            if (ui.Item(lab.Value, c)) Select(r);
        }

        static void DrawDetail(int i) { if (i < detail.Length) curUi.Label(detail[i], detailColor[i]); }
    }
}
