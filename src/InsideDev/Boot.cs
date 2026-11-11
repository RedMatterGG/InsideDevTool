using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    // Entry point called once by the version.dll proxy (via mono_runtime_invoke on the main thread).
    public static class Boot
    {
        public const string Version = "0.11.0";
        static GameObject host;

        static DevCore core;
        static int tickFrame = -1;

        // Called by the loader from Savegame.Update (fallback driver). Savegame updates before DevRoot in a frame, so the
        // fallback takes over only when Unity left DevRoot.Update out for a whole frame; a long frame alone does not count.
        public static void Tick()
        {
            try
            {
                if (core == null) return;
                if (Time.frameCount == tickFrame) return;
                tickFrame = Time.frameCount;
                if (Time.frameCount - core.unityUpdateFrame >= 2)
                {
                    if (!core.fallbackUpdLogged) { core.fallbackUpdLogged = true; DevLog.Write("Update driven by loader fallback (Savegame.Update)"); }
                    RenderHost.OnUpdate();
                    core.Update();
                }
            }
            catch (Exception e) { DevLog.Error("Boot.Tick", e); }
        }

        // Called by the loader from Savegame.OnGUI (only if the engine ever dispatches OnGUI).
        public static void Gui()
        {
            // IMGUI is disabled in INSIDE's engine; if it ever fires, only count it (rendering is owned by RenderHost)
            try { if (core != null && Event.current != null && Event.current.type == EventType.Repaint) RenderHost.OnLoaderGui(); }
            catch (Exception e) { DevLog.Error("Boot.Gui", e); }
        }

        public static void Init()
        {
            if (host != null) return;
            core = new DevCore();
            DevLog.Write("InsideDev " + Version + " booting (Unity " + Application.unityVersion + ")");
            host = new GameObject("InsideDev");
            UObj.DontDestroyOnLoad(host);
            var root = host.AddComponent<DevRoot>();
            if (root != null) root.core = core;
            DevLog.Write("AddComponent -> " + (root != null ? "ok, enabled=" + root.enabled + " active=" + host.activeInHierarchy : "NULL"));
            DevLog.Write("DevRoot attached. F1 = panel, ` = console");
        }
    }

    public static class DevLog
    {
        // Lines holds the last MaxLines messages. Readers lock (DevLog.Lines); the writer locks the same object.
        public static readonly List<string> Lines = new List<string>();
        static readonly object gate = Lines;
        static StreamWriter writer;
        static bool writerTried;
        public const int MaxLines = 400;
        static int total, clears, errors;

        // Number of messages written since start. Lines.Count stops at MaxLines, so "what did this command log" is asked with
        // Total before and Since(total, list) after.
        public static int Total { get { lock (gate) return total; } }

        // ERROR / [Exception] messages since start (exact, also when more than MaxLines arrive between two readers)
        public static int Errors { get { lock (gate) return errors; } }

        // Appends the messages written after the given Total to into (at most the MaxLines still kept) and returns the new Total.
        public static int Since(int since, List<string> into)
        {
            lock (gate)
            {
                int added = total - since;
                if (added > Lines.Count) added = Lines.Count;
                if (into != null) for (int i = Lines.Count - added; i < Lines.Count; i++) if (i >= 0) into.Add(Lines[i]);
                return total;
            }
        }

        // Copy of the last n messages, oldest first.
        public static string[] Tail(int n)
        {
            lock (gate)
            {
                if (n > Lines.Count) n = Lines.Count;
                if (n < 0) n = 0;
                var a = new string[n];
                Lines.CopyTo(Lines.Count - n, a, 0, n);
                return a;
            }
        }

        // Number of times Clear ran: a view that shows the messages rebuilds when Total or Clears moved.
        public static int Clears { get { lock (gate) return clears; } }

        // Empties the kept messages (the console's "clear"); the log file and Total are not touched.
        public static void Clear() { lock (gate) { Lines.Clear(); clears++; } }

        // False when <game>\_mod\insidedev.log could not be opened: the messages then live in memory only.
        public static bool FileOpen { get { lock (gate) return Writer != null; } }

        // <game>\_mod\insidedev.log: started empty once per session and kept open (other programs can read it while the
        // game runs). Every line is handed to the operating system at once, so a crash of the game loses nothing; opening
        // and closing the file for each line cost far more than the write.
        static StreamWriter Writer
        {
            get
            {
                if (writerTried) return writer;
                writerTried = true;
                try
                {
                    string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "insidedev.log");
                    FileStream fs;
                    // Delete: tools may move or remove the log while the game runs
                    try { fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
                    catch (ArgumentException) { fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite); }
                    writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false));
                }
                catch { writer = null; }
                return writer;
            }
        }

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "  " + msg;
            lock (gate)
            {
                Lines.Add(msg);
                total++;
                if (msg != null && (msg.StartsWith("ERROR", StringComparison.Ordinal) || msg.StartsWith("[Exception]", StringComparison.Ordinal))) errors++;
                if (Lines.Count > MaxLines) Lines.RemoveRange(0, Lines.Count - MaxLines);
                try { var w = Writer; if (w != null) { w.WriteLine(line); w.Flush(); } } catch { }
            }
        }

        public static void Error(string where, Exception e)
        {
            Write("ERROR in " + where + ": " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Thin, defensive wrappers around Playdead's game API.
    public static class G
    {
        public static Character MainCharacter
        {
            get { try { var c = ScriptGlobals.mainCharacter; return c != null ? c : null; } catch { return null; } }
        }

        public static bool SavepointsReady
        {
            get { try { return PersistentBehaviour<SavepointManager>.instance != null; } catch { return false; } }
        }

        public static Camera Cam()
        {
            try
            {
                var cs = CameraScript.main;
                if (cs != null)
                {
                    var c = cs.GetComponent<Camera>();
                    if (c != null && c.enabled) return c;
                }
            }
            catch { }
            var m = Camera.main;
            if (m != null && m.enabled) return m;
            Camera best = null;
            foreach (var c in Camera.allCameras)
                if (c.enabled && c.targetTexture == null && c != RenderHost.overlayCam && (best == null || c.depth > best.depth)) best = c;
            return best;
        }

        static FieldInfo stateField;
        public static bool GetFlag(EditorMode.EDebugFlags f)
        {
            try { return EditorMode.DebugFlagActive(f); } catch { return false; }
        }
        public static void SetFlag(EditorMode.EDebugFlags f, bool on)
        {
            if (stateField == null)
                stateField = typeof(EditorMode).GetField("mState", BindingFlags.Static | BindingFlags.NonPublic);
            var s = EditorMode.state;
            if (on) s.debugFlags |= f; else s.debugFlags &= ~f;
            stateField.SetValue(null, s);
        }

        public static bool God
        {
            get { return GetFlag(EditorMode.EDebugFlags.InvincibleBoy); }
            set { SetFlag(EditorMode.EDebugFlags.InvincibleBoy, value); }
        }

        public static float TimeScale
        {
            get { if (Time.timeScale == 0f) return 0f; try { return GameManager.TimeScale; } catch { return Time.timeScale; } }
            set
            {
                value = Mathf.Clamp(value, 0f, 10f);
                MultiCamFreeze.Release("time scale set to " + value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                try { GameManager.TimeScale = value; } catch { }
                Time.timeScale = value;
            }
        }

        public static void Kill()
        {
            var c = MainCharacter;
            if (c == null) { DevLog.Write("kill: no active character"); return; }
            c.Kill();
            DevLog.Write("kill: " + c.name);
        }

        public static bool Teleport(Vector3 p)
        {
            var c = MainCharacter;
            if (c == null || !c.isCharacterActive) { DevLog.Write("tp: no active character"); return false; }
            c.Teleport(p);
            DevLog.Write("tp: " + c.name + " -> " + Fmt.V3(p, "F2"));
            return true;
        }

        public static bool CurrentSavepoint(out int sub, out int sp)
        {
            sub = sp = -1;
            try { Savegame.GetCurrentSavepoint(out sub, out sp); return sub >= 0; } catch { return false; }
        }

        public static void LoadSavepoint(int sub, int sp)
        {
            if (!SavepointsReady) { DevLog.Write("load: savepoint manager not ready (start/continue a game first)"); return; }
            if (!SavepointManager.IsSavepointValid(sub, sp))
            {
                if (sub >= 0 && sub < SavepointManager.SubsceneCount && SavepointManager.GetSubsceneSavepointCount(sub) < 0)
                {
                    DevLog.Write("load: " + SavepointManager.GetSubsceneName(sub) + " not discovered yet - loading it first, then spawning");
                    Discovery.Enqueue(sub, sp, Discovery.Action.Spawn);
                    return;
                }
                if (sub >= 0 && sub < SavepointManager.SubsceneCount && SavepointManager.GetSubsceneSavepointCount(sub) == 0)
                {
                    int ns, np;
                    if (NearestSavepointToArea(sub, out ns, out np))
                    {
                        DevLog.Write("load: " + SavepointManager.GetSubsceneName(sub) + " has no savepoints (secret/overlay area) - spawning at nearest: " + SavepointManager.GetSubsceneName(ns) + " #" + np);
                        GameManager.LoadSavepoint(ns, np);
                    }
                    else DevLog.Write("load: " + SavepointManager.GetSubsceneName(sub) + " has no savepoints and no nearby known one - run Discover all first");
                    return;
                }
                DevLog.Write("load: invalid savepoint " + sub + "/" + sp); return;
            }
            DevLog.Write("load: " + SavepointManager.GetSubsceneName(sub) + " #" + sp);
            GameManager.LoadSavepoint(sub, sp);
        }

        public static bool AreaBounds(int sub, out Bounds b)
        {
            b = new Bounds();
            try { var c = SavepointManager.GetCuller(sub); if (c == null) return false; b = c.CullingBounds; return b.size != Vector3.zero; }
            catch { return false; }
        }

        // nearest savepoint (known to the game or the spawn cache) to an area's culling bounds
        public static bool NearestSavepointToArea(int sub, out int bestSub, out int bestSp)
        {
            bestSub = bestSp = -1;
            Bounds b;
            if (!AreaBounds(sub, out b)) return false;
            float best = float.MaxValue;
            for (int s = 0; s < SavepointManager.SubsceneCount; s++)
            {
                int n = SavepointManager.GetSubsceneSavepointCount(s);
                var ce = SpawnCache.Get(s);
                int m = n >= 0 ? n : (ce != null ? ce.count : 0);
                for (int i = 0; i < m; i++)
                {
                    Vector3 p = Vector3.zero;
                    if (n >= 0) { try { p = SavepointManager.GetSavepointPosition(s, i); } catch { } }
                    if (p == Vector3.zero && ce != null && i < ce.count) p = ce.pos[i];
                    if (p == Vector3.zero) continue;
                    float d = b.SqrDistance(p);
                    if (d < best) { best = d; bestSub = s; bestSp = i; }
                }
            }
            return bestSub >= 0;
        }

        static readonly System.Collections.Generic.Dictionary<int, string> subLabels = new System.Collections.Generic.Dictionary<int, string>();
        public static string SubsceneLabel(int sub)
        {
            string l;
            if (subLabels.TryGetValue(sub, out l)) return l;
            try { l = SavepointManager.GetSubsceneName(sub) + " [" + SavepointManager.GetPuzzleName(sub) + "]"; }
            catch { return "#" + sub; }
            if (!string.IsNullOrEmpty(l)) subLabels[sub] = l;
            return l;
        }
    }
}
