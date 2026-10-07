using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Inspector sections: MATERIALS, LEVEL FILE. Computed for the selection and cached
    // (recomputed when the selection or History changes, or every few seconds); the per-frame draw only reads
    // prebuilt strings (no per-frame allocation).
    public static class FactsSections
    {
        static readonly Color cAct = new Color(0.55f, 1f, 0.6f, 1f), cNever = new Color(1f, 0.55f, 0.45f, 1f), cMat = new Color(0.95f, 0.8f, 0.55f, 1f), cFile = new Color(0.7f, 0.8f, 1f, 1f);
        struct Line { public string text; public Color c; }

        static int forId, forVersion = -1; static float at;
        static bool deep, pending, showAct, showMat;
        static string actTitle, matTitle, fileTitle;
        static Color actColor;
        static readonly List<Line> actLines = new List<Line>(), matLines = new List<Line>(), fileLines = new List<Line>();
        static readonly List<GameObject> matHits = new List<GameObject>();
        static int actCount, matCount, fileCount;

        static void Rebuild(GameObject go)
        {
            actLines.Clear(); matLines.Clear(); fileLines.Clear(); matHits.Clear();
            showAct = showMat = false;
            try
            {
                var own = MaterialFacts.Own(go); var diff = MaterialFacts.Differences(go, matHits);
                if (own.Count + diff.Count > 0)
                {
                    showMat = true; matCount = own.Count + diff.Count; matTitle = "MATERIALS" + (diff.Count > 0 ? "  (odd one out found)" : "");
                    foreach (var l in own) matLines.Add(new Line { text = "   " + l, c = UI.Txt });
                    foreach (var l in diff) matLines.Add(new Line { text = "   " + l, c = cMat });
                }
            }
            catch (Exception e) { DevLog.Error("materials", e); }
            try
            {
                var cmp = LevelCompare.Compare(go, deep, false);
                pending = cmp.pending;
                int diff = 0; foreach (var x in cmp.rows) if (x.differs) diff++;
                fileTitle = "LEVEL FILE" + (cmp.pending ? "  (reading…)" : cmp.error != null ? "" : cmp.notInFile ? "  (not in the file: made at runtime)" : diff > 0 ? "  (" + diff + " differ)" : "  (as authored)");
                if (cmp.error != null) fileLines.Add(new Line { text = "   " + cmp.error, c = UI.Dim });
                else if (!cmp.pending && !cmp.notInFile)
                {
                    fileLines.Add(new Line { text = "   " + cmp.levelFile + "   " + cmp.filePath, c = UI.Dim });
                    foreach (var x in cmp.rows)
                        fileLines.Add(new Line { text = "   " + x.what + ":  file " + x.file + "   live " + x.live + "   " + x.origin, c = x.differs ? (x.origin.StartsWith("CHANGED BY") ? Changes.Purple : cNever) : UI.Dim });
                    if (deep)
                    {
                        fileLines.Add(new Line { text = "   below: " + cmp.subtreeChecked + " objects checked, " + cmp.subtreeDiffers + " on/off differ from the file", c = UI.Dim });
                        foreach (var l in cmp.subtree) fileLines.Add(new Line { text = "   " + l, c = l.Contains("CHANGED IN GAME") ? cNever : l.Contains("CHANGED BY") ? Changes.Purple : UI.Dim });
                    }
                }
                fileCount = fileLines.Count;
            }
            catch (Exception e) { DevLog.Error("level compare", e); fileTitle = "LEVEL FILE  (error: " + e.Message + ")"; }
        }

        public static void Draw(UI ui, GameObject go)
        {
            int id = go.GetInstanceID();
            float now = Time.realtimeSinceStartup;
            if (id != forId || forVersion != ChangeRecorder.Version || now - at > 3f || (pending && now - at > 0.25f))
            {
                if (id != forId) deep = false;
                forId = id; forVersion = ChangeRecorder.Version; at = now;
                Rebuild(go);
            }

            if (showMat && Links.Section(ui, "materials", matTitle, matCount, cMat))
            {
                for (int i = 0; i < matLines.Count; i++) ui.Label(matLines[i].text, matLines[i].c);
                if (matHits.Count > 0)
                {
                    ui.BeginRow(); ui.Space(12);
                    for (int i = 0; i < matHits.Count && i < 6; i++) { var g = matHits[i]; if (g != null && ui.Button(g.name)) { Selection.Set(g, "materials"); ui.EndRow(); return; } }
                    ui.EndRow();
                }
            }

            if (fileTitle != null && Links.Section(ui, "levelfile", fileTitle, fileCount, cFile))
            {
                for (int i = 0; i < fileLines.Count; i++) ui.Label(fileLines[i].text, fileLines[i].c);
                if (!deep && !pending && fileLines.Count > 1) { ui.BeginRow(); ui.Space(12); if (ui.Button("Compare everything below")) { deep = true; at = -100f; } ui.EndRow(); }
            }
        }
    }
}
