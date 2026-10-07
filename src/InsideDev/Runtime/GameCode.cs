using System;
using System.IO;
using UnityEngine;

namespace InsideDev
{
    // <game>\GameCode\ holds the four assemblies the source build, code mods and the Unity editor project compile against:
    //   UnityEngine.dll, PlayMaker.dll       plain copies of INSIDE_Data\Managed\ (copied here automatically if missing)
    //   Assembly-CSharp.dll,
    //   Assembly-CSharp-firstpass.dll        the game's own code. InsideDev does NOT extract or dump these: the user has
    //                                        to supply them. Until they do, a notice is shown (Settings tab + panel banner).
    // Nothing in the in-game GUI needs them; the game's code is used straight from memory while it runs.
    public static class GameCode
    {
        public static readonly string[] UserFiles = { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll" };
        public static readonly string[] CopiedFiles = { "UnityEngine.dll", "PlayMaker.dll" };

        static string dir, managed;
        static float nextCheck = -1;
        static string missing = "";       // comma list of user-supplied files that are missing or not valid
        static string copyNote = "";

        public static string Dir { get { Init(); return dir; } }
        public static bool Complete { get { Check(); return missing.Length == 0; } }
        public static string Missing { get { Check(); return missing; } }
        public static bool BannerHidden { get { return EditorState.Get("gamecode.hidebanner", "0") == "1"; } set { EditorState.Set("gamecode.hidebanner", value); } }

        static void Init()
        {
            if (dir != null) return;
            string game = Path.GetDirectoryName(Application.dataPath);
            dir = Path.Combine(game, "GameCode");
            managed = Path.Combine(Application.dataPath, "Managed");
        }

        static bool Valid(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < 1024) return false;
                using (var f = File.OpenRead(path)) return f.ReadByte() == 'M' && f.ReadByte() == 'Z';
            }
            catch { return false; }
        }

        public static void Check(bool force = false)
        {
            if (!force && Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 5f;
            Init();
            string m = "";
            foreach (var f in UserFiles) if (!Valid(Path.Combine(dir, f))) m += (m.Length > 0 ? ", " : "") + f;
            if (m != missing)
            {
                missing = m;
                DevLog.Write(m.Length == 0 ? "[gamecode] GameCode\\ is complete" : "[gamecode] GameCode\\ is missing " + m + " - supply them yourself (InsideDev does not extract the game's code)");
            }
        }

        // UnityEngine.dll / PlayMaker.dll ship as ordinary files in INSIDE_Data\Managed: copy them if they are missing
        public static void CopyShippedRefs()
        {
            Init();
            int n = 0;
            try
            {
                foreach (var f in CopiedFiles)
                {
                    string dst = Path.Combine(dir, f), src = Path.Combine(managed, f);
                    if (Valid(dst) || !File.Exists(src)) continue;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.Copy(src, dst, true); n++;
                }
                copyNote = n > 0 ? "copied " + n + " file(s) from INSIDE_Data\\Managed" : "";
                if (n > 0) DevLog.Write("[gamecode] " + copyNote);
            }
            catch (Exception e) { DevLog.Error("gamecode copy", e); }
        }

        public static void Boot() { CopyShippedRefs(); Check(true); }

        public static string Report()
        {
            Check(true);
            Init();
            var sb = new System.Text.StringBuilder("GameCode folder: " + dir + "\n");
            foreach (var f in CopiedFiles) sb.Append("  ").Append(Valid(Path.Combine(dir, f)) ? "[ok]      " : "[missing] ").Append(f).Append("   (copy of INSIDE_Data\\Managed)\n");
            foreach (var f in UserFiles) sb.Append("  ").Append(Valid(Path.Combine(dir, f)) ? "[ok]      " : "[missing] ").Append(f).Append("   (supplied by you)\n");
            if (copyNote.Length > 0) sb.Append("  ").Append(copyNote).Append('\n');
            return sb.ToString();
        }

        static readonly Color cWarn = new Color(1f, 0.55f, 0.3f, 1f);

        // one line above every panel until the files are there (or the banner is hidden)
        public static void DrawBanner(UI ui)
        {
            if (BannerHidden || Complete) return;
            ui.BeginRow();
            ui.Label("GameCode\\ is missing " + missing + " - see Settings > Game code", cWarn, Mathf.Max(120, ui.Width - 60));
            if (ui.Button("hide", 50)) BannerHidden = true;
            ui.EndRow();
        }

        // Settings tab block
        public static void DrawSettings(UI ui)
        {
            ui.Label("Game code", UI.Dim);
            bool ok = Complete;
            foreach (var f in CopiedFiles) ui.Label("   " + (Valid(Path.Combine(dir, f)) ? "[ok]       " : "[missing]  ") + f + "   (copied from INSIDE_Data\\Managed)", UI.Dim);
            foreach (var f in UserFiles) { bool v = Valid(Path.Combine(dir, f)); ui.Label("   " + (v ? "[ok]       " : "[missing]  ") + f + "   (you supply this)", v ? UI.Dim : cWarn); }
            if (!ok)
            {
                ui.Label("   InsideDev does not extract the game's code. Dump " + missing + " yourself", cWarn);
                ui.Label("   and place them in " + dir, cWarn);
                ui.Label("   Needed for building InsideDev / code mods from source and for the Unity editor project.", UI.Dim);
                ui.Label("   Everything in this GUI works without them.", UI.Dim);
            }
            ui.BeginRow();
            if (ui.Button("Check again")) { CopyShippedRefs(); Check(true); }
            if (!ok && BannerHidden && ui.Button("Show reminder banner")) BannerHidden = false;
            ui.EndRow();
        }
    }
}
