using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Audio Timeline: every observed call into AkSoundEngine.dll plus the callbacks coming back, newest first.
    // Click a row: selects the Unity object that owns the Wwise game object, and for posts/callbacks shows the
    // playing-id detail. "Follow" pins the view to one playing id (post -> callbacks -> end).
    // The visible index list is rebuilt when the trace got a new entry or a toggle, the filter, the followed id or (with
    // "selected object only") the selection changed, not every frame.
    public static class AudioTimelinePanel
    {
        static string filter = "";
        static readonly bool[] cats = { true, true, true, false, true, true, true, false, true };   // per AudioTrace.Cat
        static readonly string[] catNames = { "events", "switch", "state", "rtpc", "banks", "objects", "callbacks", "ids", "other" };
        static readonly string[] levelNames = { "telemetry: OFF", "telemetry: NORMAL", "telemetry: DETAILED", "telemetry: RAW" };
        static string[] stackLabels;
        static bool onlySelected, raw;
        static uint detailPid, detailFor; static float detailAt; static string[] detailLines = new string[0];
        static readonly Stack<uint> back = new Stack<uint>();   // playing ids viewed before (Back)
        static readonly ListCache list = new ListCache();
        static SearchDelay filterDelay;
        static int coalescedSeen; static float coalescedAt;
        static string statusLine; static float statusAt;
        static UI curUi; static float curNow;
        static readonly Action<int> drawRow = DrawRow, drawDetail = DrawDetail;
        static readonly Color cPost = new Color(0.55f, 0.9f, 1f, 1f), cFail = new Color(1f, 0.4f, 0.35f, 1f), cSync = new Color(1f, 0.8f, 0.35f, 1f),
            cRtpc = new Color(0.7f, 0.7f, 0.85f, 1f), cBank = new Color(0.5f, 1f, 0.55f, 1f), cObj = new Color(0.65f, 0.65f, 0.65f, 1f), cCb = new Color(0.85f, 0.6f, 1f, 1f);

        static Color ColorOf(AudioTrace.Ev e)
        {
            var r = e.r;
            if (r.type == AudioTrace.POST && r.playingId == 0) return cFail;
            switch (AudioTrace.CategoryOf(r.type))
            {
                case AudioTrace.Cat.Event: return cPost;
                case AudioTrace.Cat.Switch: case AudioTrace.Cat.State: return cSync;
                case AudioTrace.Cat.Rtpc: return cRtpc;
                case AudioTrace.Cat.Bank: return cBank;
                case AudioTrace.Cat.Callback: return cCb;
            }
            return cObj;
        }

        public static void Draw(UI ui)
        {
            Background.Want(Background.Audio, 5f);
            float now = Time.realtimeSinceStartup;
            ui.BeginRow();
            if (ui.Button(AudioTrace.paused ? "Resume" : "Pause")) AudioTrace.paused = !AudioTrace.paused;
            if (ui.Button("Clear")) AudioTrace.Clear();
            int catMask = 0;
            for (int i = 0; i < cats.Length; i++) { cats[i] = ui.Toggle(cats[i], catNames[i]); if (cats[i]) catMask |= 1 << i; }
            ui.EndRow();
            ui.BeginRow();
            onlySelected = ui.Toggle(onlySelected, "selected object only");
            raw = ui.Toggle(raw, "raw ids");
            int lvl = WwiseNative.Level;
            if (ui.Button(levelNames[Mathf.Clamp(lvl, 0, 3)])) WwiseNative.SetLevel((lvl + 1) % 4);
            if (stackLabels == null) { stackLabels = new string[AudioSpy.StackModes.Length]; for (int i = 0; i < stackLabels.Length; i++) stackLabels[i] = "call stacks: " + AudioSpy.StackModes[i]; }
            if (ui.Button(stackLabels[AudioSpy.stackMode])) AudioSpy.stackMode = (AudioSpy.stackMode + 1) % 3;
            if (AudioTrace.follow != 0) { if (ui.Button("Stop following " + AudioTrace.follow)) AudioTrace.follow = 0; }
            ui.EndRow();
            ui.BeginRow();
            ui.Label("Filter:", null, 40);
            EditorInput.Field(ui, "atl_filter", ref filter, Mathf.Max(80, ui.Width - 4));
            ui.EndRow();
            if (statusLine == null || now - statusAt > 0.5f) { statusLine = AudioTrace.Status(); statusAt = now; }
            ui.Label(statusLine, UI.Dim);

            if (detailPid != 0)
            {
                // navigation first, so it never scrolls away; the detail has its own bounded scroll area
                ui.BeginRow();
                if (ui.Button(back.Count > 0 ? "< Back (" + back.Count + ")" : "< Back")) { detailPid = back.Count > 0 ? back.Pop() : 0; detailFor = 0; }
                if (ui.Button("Close")) { detailPid = 0; back.Clear(); }
                if (ui.Button("Follow this playing id")) AudioTrace.follow = detailPid;
                Play(detailPid, ui);
                ui.EndRow();
                if (detailPid != 0)
                {
                    if (detailFor != detailPid || now - detailAt > 0.5f) { detailLines = AudioTrace.DescribePlay(detailPid).Split('\n'); detailFor = detailPid; detailAt = now; }
                    curUi = ui;
                    ui.VirtualList("atl_detail", detailLines.Length, UI.ItemPitch, Mathf.Min(detailLines.Length * UI.ItemPitch + 4, ui.Remaining * 0.45f), drawDetail);
                }
            }

            int selId = onlySelected && Selection.Current != null ? Selection.Current.GetInstanceID() : 0;
            string fl = filterDelay.Value(filter, 0.12f).Trim();
            int count = AudioTrace.Count;
            long newest = count > 0 && AudioTrace.Get(0) != null ? AudioTrace.Get(0).seq : -1;
            // a repeat that was folded into an existing entry changes that entry's text, not the list: with a filter typed
            // the list follows such changes four times per second at most
            if (fl.Length > 0 && AudioTrace.coalesced != coalescedSeen && now - coalescedAt > 0.25f) { coalescedSeen = AudioTrace.coalesced; coalescedAt = now; list.Invalidate(); }
            long toggles = (uint)catMask | (onlySelected ? 1L << 16 : 0) | (raw ? 1L << 17 : 0) | ((long)AudioTrace.follow << 20);
            if (list.Changed(((long)count << 40) ^ newest, toggles, selId, fl))
            {
                uint follow = AudioTrace.follow;
                for (int i = 0; i < count && list.Count < 4000; i++)
                {
                    var e = AudioTrace.Get(i);
                    if (e == null) break;
                    var r = e.r;
                    if (follow != 0) { if (r.playingId != follow) continue; }
                    else if (!cats[(int)AudioTrace.CategoryOf(r.type)]) continue;
                    if (onlySelected && (int)r.gameObj != selId) continue;
                    if (fl.Length > 0 && AudioTrace.Text(e, raw).IndexOf(fl, StringComparison.OrdinalIgnoreCase) < 0 && AudioTrace.Kind(r.type).IndexOf(fl, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    list.Add(i);
                }
            }
            curUi = ui; curNow = now;
            ui.VirtualList("atl_list", list.Count, UI.ItemPitch, ui.Remaining, drawRow);
        }

        static void DrawDetail(int i) { curUi.Label(detailLines[i], Color.white); }

        static void DrawRow(int row)
        {
            var ui = curUi;
            var e = AudioTrace.Get(list.idx[row]);
            if (e == null) return;
            var c = ColorOf(e);
            if (curNow - e.t < 1.2f) c = Color.Lerp(Color.white, c, (curNow - e.t) / 1.2f);
            if (e.line == null || e.lineCount != e.count || e.lineRaw != raw || (!raw && e.text == null))
            {
                e.line = e.t.ToString("0.00") + "s  f" + e.frame + "  " + AudioTrace.Kind(e.r.type).PadRight(12) + AudioTrace.Text(e, raw);
                e.lineCount = e.count; e.lineRaw = raw;
            }
            bool ck = ui.Item(e.line, c);
            if (ui.LastHover) { var u = AudioTrace.Unity(e.r.gameObj); if (u != null) Selection.SetHover(u); }
            if (ck)
            {
                var u = AudioTrace.Unity(e.r.gameObj);
                if (u != null) Selection.Set(u, "audio timeline");
                if (e.r.playingId != 0 && e.r.playingId != detailPid) { if (detailPid != 0) { back.Push(detailPid); if (back.Count > 50) back.Clear(); } detailPid = e.r.playingId; }
            }
        }

        static void Play(uint pid, UI ui)
        {
            AudioTrace.Play p;
            if (!AudioTrace.plays.TryGetValue(pid, out p)) return;
            var u = AudioTrace.Unity(p.gameObj);
            if (u != null && ui.Button("Select emitter")) Selection.Set(u, "audio timeline");
        }
    }
}
