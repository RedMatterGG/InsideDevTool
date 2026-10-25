using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Inspector section ATLAS: what the level atlas knows about the selection - switched off in the level file and who
    // (if anyone) switches it on, signal wires that never connect, PlayMaker states and inputs nothing in the game reaches.
    // Rebuilt when the selection changes; the per-frame draw only reads prebuilt lines.
    public static class AtlasSection
    {
        static readonly Color cHead = new Color(0.95f, 0.6f, 0.95f, 1f), cDormant = new Color(1f, 0.45f, 0.9f, 1f), cSwitch = new Color(0.55f, 1f, 0.6f, 1f),
            cWire = new Color(1f, 0.55f, 0.45f, 1f), cFsm = new Color(1f, 0.8f, 0.45f, 1f);
        struct Line { public string text; public Color c; public string jumpFile, jumpPath, jumpLabel; }

        static int forId;
        static string title;
        static readonly List<Line> lines = new List<Line>();

        static void Rebuild(GameObject go)
        {
            lines.Clear(); title = null;
            Atlas.EnsureLoaded();
            if (!Atlas.Present) return;
            if (!Atlas.Ready) { title = "ATLAS  (" + Atlas.status + ")"; return; }
            string file, path;
            if (!Atlas.Key(go, out file, out path)) return;
            string head = null;
            var h = Atlas.HiddenAbove(file, path);
            if (h != null)
            {
                bool self = h.path == path;
                string where = self ? "switched off in the level file" : "inside " + Atlas.Leaf(h.path) + ", which is switched off in the level file";
                head = h.status;
                if (h.status == "switchable")
                {
                    Add(where + "; the game switches it on: " + h.reason, cSwitch);
                    if (h.from.Length > 0) Add("   from " + h.from, UI.Dim, file, h.from, "select switch");
                }
                else
                {
                    Add(where + (h.status == "dormant" ? "; nothing in the game switches it on" : "; not proven either way"), cDormant);
                    Add("   " + h.reason, UI.Dim);
                }
                Add("   " + h.objects + " object(s)" + (h.scripts.Length > 0 ? ", scripts: " + h.scripts.Replace(",", ", ") : ""), UI.Dim, self ? null : file, self ? null : h.path, self ? null : "select it");
            }
            var ws = Atlas.WiresAt(file, path);
            foreach (var w in ws)
            {
                string role = w.connector == path ? "connector" : w.sender == path ? "sender" : "receiver";
                Add("dead wire (" + role + "): " + Atlas.Leaf(w.sender) + "." + w.outName + " -> " + Atlas.Leaf(w.receiver) + "." + w.inName, cWire);
                Add("   " + Atlas.WireMeaning(w) + (w.flags.Length > 0 ? "   (" + w.flags + ")" : ""), UI.Dim, file, role == "receiver" ? w.sender : w.receiver, role == "receiver" ? "select sender" : "select receiver");
            }
            if (ws.Count > 0) head = (head != null ? head + ", " : "") + ws.Count + " dead wire" + (ws.Count == 1 ? "" : "s");
            foreach (var f in go.GetComponents<PlayMakerFSM>())
            {
                if (f == null) continue;
                var x = Atlas.FsmAt(file, path, f.FsmName);
                if (x == null) continue;
                if (x.unreachable.Length > 0) Add(f.FsmName + ": states no event in the game leads to: " + string.Join(", ", x.unreachable), cFsm);
                if (x.unsent.Length > 0) Add(f.FsmName + ": events nothing in the game sends: " + string.Join(", ", x.unsent), cFsm);
                head = (head != null ? head + ", " : "") + "dead FSM parts";
            }
            if (lines.Count == 0) return;
            title = "ATLAS  (" + head + ")";
        }

        static void Add(string text, Color c, string file = null, string path = null, string label = null)
        {
            lines.Add(new Line { text = "   " + text, c = c, jumpFile = file, jumpPath = path, jumpLabel = label });
        }

        public static void Draw(UI ui, GameObject go)
        {
            int id = go.GetInstanceID();
            if (id != forId) { forId = id; Rebuild(go); }
            if (title == null || !Links.Section(ui, "atlas", title, lines.Count, cHead)) return;
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (l.jumpPath == null) { ui.Label(l.text, l.c); continue; }
                ui.BeginRow();
                ui.Label(l.text, l.c, ui.Width - 130);
                if (ui.Button(l.jumpLabel, 120))
                {
                    var g = Atlas.Find(l.jumpFile, l.jumpPath);
                    if (g != null) { Selection.Set(g, "atlas"); ui.EndRow(); return; }
                    DevLog.Write("atlas: " + l.jumpPath + " is not loaded (" + Atlas.SceneOf(l.jumpFile) + ")");
                }
                ui.EndRow();
            }
        }
    }
}
