using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // Notes.cs (owner GUIDE, Notes API and the notes schema frozen): moments the user flagged in the game with a hotkey
    // (report 05 item 10). The hotkey (polled in a PlayRegistry tick through the core's EditorInput.Key, so it is ignored
    // while an InsideDev text field has the keyboard) is Home by default: InsideDev binds F1-F11, Insert, Delete, brackets,
    // backslash, backquote, minus / equals, keypad + / - / 0, Ctrl+P/K/Z/Y and P; the game's defaults are arrows, WASD,
    // Space, Ctrl, Alt, Return, Escape, Backspace, PageUp / PageDown and keypad 2/4/6/8; F12 is Steam's screenshot key.
    // EditorState play.notes_key overrides it (a UnityEngine.KeyCode name). A note records {id, time, frame, step, subscene,
    // savepoint, checkpoint name, body position, screenshot path, text} into a ring of 50 persisted to _mod/notes.jsonl
    // (NotesCore.cs); the screenshot is Application.CaptureScreenshot to _mod/notes/note_<id>.png (written at the end of
    // that frame). clear moves notes.jsonl to notes_cleared_<time>.jsonl. Instructions paragraph (play): read notes first.
    public static class Notes
    {
        public const string Owner = "GUIDE";
        public const string DefaultKey = "Home";
        public const int MaxImageBytes = 4 * 1024 * 1024, PreviewWidth = 960;
        static readonly object gate = new object();
        static NoteRing ring;
        static string loadedFrom;
        static volatile string hotkey = DefaultKey;
        static KeyCode key = KeyCode.Home;
        static int ticks;

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("notes", "Flagged moments", PlayShared.Play, Owner,
                "Moments the user flagged in the game with the notes hotkey (or added here): frame, checkpoint, body position, screenshot. list the latest, get one (image attaches its screenshot), add a note at the current moment, clear all (the old list is kept as a backup file).",
                Schema.Obj().Enum("op", "list, get, add or clear.", "list", "list", "get", "add", "clear").Int("id", "op get.", 0, 1000000)
                    .StrMax("text", "op add.", 500).Int("limit", "op list.", 10, 1, 50).Bool("image", "op get: attach the screenshot.", false).Build(),
                Schema.Obj().Arr("notes", "Newest first.", Schema.OutOf(Schema.Obj().Int("id", "", 0, 1000000).Str("time", "Local time.").Int("frame", "", 0, long.MaxValue)
                        .Int("step", "", 0, long.MaxValue).Int("subscene", "", -1, 999).Int("savepoint", "", -1, 999).Str("checkpoint", "")
                        .Vec("pos", "", 3, -1e5, 1e5).Str("text", "").Str("screenshot", "PNG path.")), 0, 50)
                    .Int("total", "", 0, 1000).Str("hotkey", "Key that adds a note in the game.")
                    .Int("cleared", "op clear: notes removed.", 0, 1000).Str("backup", "op clear: file the old notes were moved to.")
                    .Str("image", "op get image: the attached screenshot's size (scaled to 960 px wide).")
                    .Arr("warnings", "", Schema.Str(), 0, 20).BuildOutput()), Handle);
            PlayRegistry.Instructions(McpProfiles.Play, 10, "If the user mentions a moment, a spot or \"here\", read notes {op:\"list\"} first: the user flags moments in the game with a hotkey (checkpoint, position, screenshot, text).");
            PlayRegistry.OnTick("notes", Tick);
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "list");
            var r = PlayShared.Env();
            var warnings = new List<string>();
            var list = new List<object>();
            if (op == "list")
            {
                int limit = PlayShared.Int(a, "limit", 10);
                lock (gate) foreach (var e in Ring().Newest(limit)) list.Add(Out(e));
            }
            else if (op == "get")
            {
                if (!PlayShared.Has(a, "id")) throw new McpException(McpErr.BadArgs, "op get needs id", "notes {op:\"list\"} shows the ids");
                int id = PlayShared.Int(a, "id", -1);
                NoteEntry e;
                lock (gate) e = Ring().Get(id);
                if (e == null) throw new McpException(McpErr.NotFound, "no note " + id, "notes {op:\"list\"} shows the ids");
                list.Add(Out(e));
                r["notes"] = list;
                var tr = Finish(r, warnings);
                if (PlayShared.Bool(a, "image", false)) AttachImage(tr, r, e, warnings);
                r["warnings"] = PlayShared.L(warnings);
                return tr;
            }
            else if (op == "add")
            {
                string text = PlayShared.Str(a, "text", "");
                int id = ctx.Main(() => Add(text), 5000);
                NoteEntry e;
                lock (gate) e = Ring().Get(id);
                if (e != null) list.Add(Out(e));
                warnings.Add("the screenshot file is written at the end of the frame; get it with notes {op:\"get\", id:" + id + ", image:true}");
            }
            else if (op == "clear")
            {
                string backup;
                int n = ClearAll(out backup);
                r["cleared"] = (double)n;
                if (backup != null) r["backup"] = backup;
            }
            else throw new McpException(McpErr.BadArgs, "op: one of list, get, add, clear");
            r["notes"] = list;
            return Finish(r, warnings);
        }

        static ToolResult Finish(Json.Obj r, List<string> warnings)
        {
            lock (gate) r["total"] = (double)Ring().Count;
            r["hotkey"] = hotkey;
            r["warnings"] = PlayShared.L(warnings);
            return ToolResult.Ok(r);
        }

        static void AttachImage(ToolResult tr, Json.Obj r, NoteEntry e, List<string> warnings)
        {
            try
            {
                if (string.IsNullOrEmpty(e.screenshot) || !File.Exists(e.screenshot)) { warnings.Add("screenshot missing: " + e.screenshot); return; }
                var info = new FileInfo(e.screenshot);
                if (info.Length > 64L * 1024 * 1024) { warnings.Add("screenshot is " + (info.Length / 1024) + " KB; open the file at its path"); return; }
                byte[] png = File.ReadAllBytes(e.screenshot), small = null;
                string note = null;
                try { small = Png.Downscale(png, PreviewWidth, out note); }
                catch (Exception ex) { warnings.Add("screenshot not scaled: " + ex.Message); }
                byte[] img = small ?? png;
                if (img.Length > MaxImageBytes) { warnings.Add("screenshot is " + (img.Length / 1024) + " KB, over the inline limit; open the file at its path"); return; }
                tr.AddImage(img, "image/png");
                r["image"] = note ?? "attached";
            }
            catch (Exception ex) { warnings.Add("screenshot unreadable: " + ex.Message); }
        }

        static Json.Obj Out(NoteEntry e)
        {
            var o = e.ToJson();
            if (o["pos"] == null) o.Remove("pos");
            return o;
        }

        static NoteRing Ring()
        {
            string file = FilePath();
            if (ring != null && loadedFrom == file) return ring;
            ring = new NoteRing();
            loadedFrom = file;
            try
            {
                if (file != null && File.Exists(file))
                {
                    int bad;
                    if (ring.Load(File.ReadAllLines(file, Encoding.UTF8), out bad)) File.WriteAllText(file, ring.Serialize(), new UTF8Encoding(false));
                }
            }
            catch (Exception e) { Log("[notes] could not read " + file + ": " + e.Message); }
            return ring;
        }

        static string FilePath()
        {
            string mod = GuideBook.ModDir();
            return mod == null ? null : Path.Combine(mod, "notes.jsonl");
        }

        static string ShotDir()
        {
            string mod = GuideBook.ModDir();
            return mod == null ? null : Path.Combine(mod, "notes");
        }

        static int ClearAll(out string backup)
        {
            backup = null;
            lock (gate)
            {
                var r = Ring();
                int n = r.Count;
                r.Clear();
                string file = FilePath();
                try
                {
                    if (file != null && File.Exists(file))
                    {
                        backup = Path.Combine(Path.GetDirectoryName(file), "notes_cleared_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");
                        if (File.Exists(backup)) File.Delete(backup);
                        File.Move(file, backup);
                    }
                }
                catch (Exception e) { Log("[notes] clear: " + e.Message); }
                return n;
            }
        }

        // main thread: record a note now (hotkey or tool); returns its id
        public static int Add(string text)
        {
            var e = new NoteEntry { time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), text = text ?? "" };
            try { ReadFrame(e); } catch (Exception) { }
            try { ReadStep(e); } catch (Exception) { }
            try { ReadSavepoint(e); } catch (Exception) { }
            try { ReadBody(e); } catch (Exception) { }
            var evicted = new List<NoteEntry>();
            lock (gate)
            {
                var r = Ring();
                r.Add(e, evicted);
                string dir = ShotDir();
                if (dir != null)
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        e.screenshot = Path.Combine(dir, "note_" + e.id + ".png");
                        Shoot(e.screenshot);
                    }
                    catch (Exception ex) { e.screenshot = ""; Log("[notes] screenshot: " + ex.Message); }
                }
                Persist(r, e, evicted);
            }
            string where = e.subscene >= 0 ? " at " + e.subscene + ":" + e.savepoint + (e.checkpoint.Length > 0 ? " (" + e.checkpoint + ")" : "") : "";
            Log("[notes] note " + e.id + " saved" + where + (e.text.Length > 0 ? ": " + e.text : ""));
            return e.id;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ReadFrame(NoteEntry e) { e.frame = Time.frameCount; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ReadStep(NoteEntry e) { e.step = LogicManager.stepCount; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ReadSavepoint(NoteEntry e)
        {
            if (PersistentBehaviour<Savegame>.instance == null) return;
            int s, p;
            Savegame.GetCurrentSavepoint(out s, out p);
            e.subscene = s;
            e.savepoint = p;
            if (s >= 0 && PersistentBehaviour<SavepointManager>.instance != null) e.checkpoint = SavepointManager.GetSavepointName(s, p);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ReadBody(NoteEntry e)
        {
            var c = ScriptGlobals.mainCharacter;
            if (c == null) return;
            var v = c.pos3;
            e.x = v.x; e.y = v.y; e.z = v.z;
            e.hasPos = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Shoot(string path) { Application.CaptureScreenshot(path); }

        static void Persist(NoteRing r, NoteEntry added, List<NoteEntry> evicted)
        {
            string file = FilePath();
            if (file == null) return;
            try
            {
                if (evicted.Count == 0) File.AppendAllText(file, NoteRing.Line(added) + "\n", new UTF8Encoding(false));
                else File.WriteAllText(file, r.Serialize(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log("[notes] could not write " + file + ": " + ex.Message); }
            foreach (var old in evicted)
            {
                try { if (!string.IsNullOrEmpty(old.screenshot) && File.Exists(old.screenshot) && Path.GetFileName(old.screenshot).StartsWith("note_")) File.Delete(old.screenshot); }
                catch (Exception) { }
            }
        }

        // main thread, every frame
        static void Tick()
        {
            if (++ticks % 120 == 1) ReadKey();
            if (key != KeyCode.None && EditorInput.Key(key)) Add("");
        }

        static void ReadKey()
        {
            string want = DefaultKey;
            try { want = EditorState.Get("play.notes_key", DefaultKey); } catch (Exception) { }
            if (string.IsNullOrEmpty(want) || want == hotkey || want == badKey) return;
            if (want.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) || want.Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                key = KeyCode.None;
                hotkey = want;
                Log("[notes] hotkey off (play.notes_key " + want.Trim() + ")");
                return;
            }
            try
            {
                var k = (KeyCode)Enum.Parse(typeof(KeyCode), want.Trim(), true);
                if (k == KeyCode.None) { badKey = want; return; }
                key = k;
                hotkey = k.ToString();
            }
            catch (Exception)
            {
                badKey = want;
                Log("[notes] play.notes_key '" + want + "' is not a KeyCode name; keeping " + hotkey);
            }
        }

        static string badKey;

        static void Log(string msg)
        {
            try { DevLog.Write(msg); } catch (Exception) { }
        }

        public static int Count { get { lock (gate) return Ring().Count; } }
        public static string Hotkey { get { return hotkey; } }
    }
}
