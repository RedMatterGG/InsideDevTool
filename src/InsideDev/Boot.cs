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
        public const string Version = "0.5.0";
        static GameObject host;

        static DevCore core;
        static int tickFrame = -1;

        // Called by the loader from Savegame.Update (fallback driver).
        public static void Tick()
        {
            try
            {
                if (core == null) return;
                if (Time.frameCount == tickFrame) return;
                tickFrame = Time.frameCount;
                if (Time.realtimeSinceStartup - core.unityUpdateRT > 0.25f)
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
        static readonly object gate = new object();
        static string path;
        public static readonly List<string> Lines = new List<string>();
        public const int MaxLines = 400;

        static string LogPath
        {
            get
            {
                if (path == null)
                {
                    try
                    {
                        string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod");
                        Directory.CreateDirectory(dir);
                        path = Path.Combine(dir, "insidedev.log");
                        File.WriteAllText(path, "");
                    }
                    catch { path = ""; }
                }
                return path;
            }
        }

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            lock (gate)
            {
                Lines.Add(msg);
                if (Lines.Count > MaxLines) Lines.RemoveRange(0, Lines.Count - MaxLines);
                try { if (LogPath.Length > 0) File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
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
            get { try { return GameManager.TimeScale; } catch { return Time.timeScale; } }
            set
            {
                value = Mathf.Clamp(value, 0f, 10f);
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
            DevLog.Write(string.Format("tp: {0} -> ({1:F2}, {2:F2}, {3:F2})", c.name, p.x, p.y, p.z));
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
