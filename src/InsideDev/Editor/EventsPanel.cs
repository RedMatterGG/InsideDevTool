using System;
using UnityEngine;

namespace InsideDev
{
    // Phase 8 UI: live event history (newest first). Click = select the object that raised the event.
    // The visible index list is rebuilt when an event arrives or a toggle, the filter or (with "selected only") the
    // selection changes; row texts are built for the rows on screen and kept until then.
    public static class EventsPanel
    {
        static string filter = "";
        static bool showSignals = true, showFsm = true, showSounds, showAnim = true, onlySelected, showUnconnected;
        static readonly Color cSig = new Color(1f, 0.72f, 0.25f, 1f), cFsm = new Color(0.72f, 0.55f, 1f, 1f), cSnd = new Color(0.3f, 0.9f, 0.85f, 1f), cAnim = new Color(0.55f, 1f, 0.55f, 1f);
        public static Color KindColor(char k) { return k == 'S' ? cSig : k == 'F' ? cFsm : k == 'A' ? cSnd : cAnim; }

        static readonly ListCache list = new ListCache();
        static SearchDelay filterDelay;
        static string statusLine; static float statusAt;
        static UI curUi; static float curNow;
        static readonly Action<int> drawRow = DrawRow;

        public static void Draw(UI ui)
        {
            Background.Want(Background.Events, 5f);
            ui.BeginRow();
            bool en = ui.Toggle(EventMonitor.enabled, "monitor"); if (en != EventMonitor.enabled) EventMonitor.enabled = en;
            if (ui.Button(EventMonitor.paused ? "Resume" : "Pause")) EventMonitor.paused = !EventMonitor.paused;
            if (ui.Button("Clear")) EventMonitor.Clear();
            bool s = ui.Toggle(showSignals, "signals"); if (s != showSignals) showSignals = s;
            bool f = ui.Toggle(showFsm, "state machines"); if (f != showFsm) showFsm = f;
            bool sn = ui.Toggle(showSounds, "sounds"); if (sn != showSounds) showSounds = sn;
            bool an = ui.Toggle(showAnim, "animation"); if (an != showAnim) showAnim = an;
            bool o = ui.Toggle(onlySelected, "selected only"); if (o != onlySelected) onlySelected = o;
            bool u = ui.Toggle(showUnconnected, "unconnected"); if (u != showUnconnected) showUnconnected = u;
            ui.EndRow();
            ui.BeginRow();
            ui.Label("Filter:", null, 40);
            EditorInput.Field(ui, "ev_filter", ref filter, Mathf.Max(80, ui.Width - 4));
            ui.EndRow();
            float now = Time.realtimeSinceStartup;
            if (statusLine == null || now - statusAt > 0.5f) { statusLine = EventMonitor.Status(); statusAt = now; }
            ui.Label(statusLine, UI.Dim);

            // the visible index list (newest first)
            int selId = onlySelected && Selection.Current != null ? Selection.Current.GetInstanceID() : 0;
            string fl = filterDelay.Value(filter, 0.12f).Trim();
            int toggles = (showSignals ? 1 : 0) | (showFsm ? 2 : 0) | (showSounds ? 4 : 0) | (showAnim ? 8 : 0) | (onlySelected ? 16 : 0) | (showUnconnected ? 32 : 0);
            if (list.Changed(EventMonitor.total, ((long)EventMonitor.Count << 8) | (uint)toggles, selId, fl))
            {
                int n = EventMonitor.Count;
                for (int i = 0; i < n && list.Count < 3000; i++)
                {
                    var e = EventMonitor.Get(i);
                    if (e.kind == 'S' && !showSignals) continue;
                    if (e.kind == 'F' && !showFsm) continue;
                    if (e.kind == 'A' && !showSounds) continue;
                    if (e.kind == 'N' && !showAnim) continue;
                    if (e.kind == 'S' && !showUnconnected && e.text != null && e.text.EndsWith("(no receivers)", StringComparison.Ordinal)) continue;
                    if (onlySelected && e.goId != selId) continue;
                    if (fl.Length > 0 && (e.text == null || e.text.IndexOf(fl, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                    list.Add(i);
                }
            }
            curUi = ui; curNow = now;
            ui.VirtualList("ev_list", list.Count, UI.ItemPitch, ui.Remaining, drawRow);
        }

        static void DrawRow(int row)
        {
            var ui = curUi;
            var e = EventMonitor.Get(list.idx[row]);
            var c = KindColor(e.kind);
            if (curNow - e.t < 1.5f) c = Color.Lerp(Color.white, c, (curNow - e.t) / 1.5f);
            string s = list.Text(row) ?? list.Text(row, e.t.ToString("0.00") + "s   " + EventMonitor.KindName(e.kind) + " " + e.text);
            bool ck = ui.Item(s, c);
            bool hover = ui.LastHover;
            if (!ck && !hover) return;
            var r = ObjectDatabase.Get(e.goId);
            if (r == null) return;
            if (hover) Selection.SetHover(r.go);
            if (ck) Selection.Set(r.go, "events");
        }
    }
}
