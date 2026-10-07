using System;
using UnityEngine;

namespace InsideDev
{
    // Phase 8 UI: live event history (newest first). Click = select the object that raised the event.
    public static class EventsPanel
    {
        static string filter = "";
        static bool showSignals = true, showFsm = true, showSounds, showAnim = true, onlySelected, showUnconnected;
        static readonly Color cSig = new Color(1f, 0.72f, 0.25f, 1f), cFsm = new Color(0.72f, 0.55f, 1f, 1f), cSnd = new Color(0.3f, 0.9f, 0.85f, 1f), cAnim = new Color(0.55f, 1f, 0.55f, 1f);
        public static Color KindColor(char k) { return k == 'S' ? cSig : k == 'F' ? cFsm : k == 'A' ? cSnd : cAnim; }

        public static void Draw(UI ui)
        {
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
            ui.TextField("ev_filter", ref filter, Mathf.Max(80, ui.Width - 4));
            ui.EndRow();
            ui.Label(EventMonitor.Status(), UI.Dim);

            // build the visible index list (newest first)
            int selId = Selection.Current != null ? Selection.Current.GetInstanceID() : 0;
            var idx = new System.Collections.Generic.List<int>(256);
            string fl = filter.Trim();
            for (int i = 0; i < EventMonitor.Count && idx.Count < 3000; i++)
            {
                var e = EventMonitor.Get(i);
                if (e.kind == 'S' && !showSignals) continue;
                if (e.kind == 'F' && !showFsm) continue;
                if (e.kind == 'A' && !showSounds) continue;
                if (e.kind == 'N' && !showAnim) continue;
                if (e.kind == 'S' && !showUnconnected && e.text.EndsWith("(no receivers)")) continue;
                if (onlySelected && e.goId != selId) continue;
                if (fl.Length > 0 && e.text.IndexOf(fl, StringComparison.OrdinalIgnoreCase) < 0) continue;
                idx.Add(i);
            }
            float now = Time.realtimeSinceStartup;
            ui.VirtualList("ev_list", idx.Count, UI.ItemPitch, ui.Remaining, row =>
            {
                var e = EventMonitor.Get(idx[row]);
                var c = KindColor(e.kind);
                if (now - e.t < 1.5f) c = Color.Lerp(Color.white, c, (now - e.t) / 1.5f);
                bool ck = ui.Item(e.t.ToString("0.00") + "s   " + EventMonitor.KindName(e.kind) + " " + e.text, c);
                var r = ObjectDatabase.Get(e.goId);
                if (ui.LastHover && r != null) Selection.SetHover(r.go);
                if (ck && r != null) Selection.Set(r.go, "events");
            });
        }
    }
}
