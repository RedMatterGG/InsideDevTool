using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // A mod feature lives in one level and looks only at that level, only when it loads.
    //
    //   static readonly LevelGate Room = new LevelGate("#HeadExplodeSecret_Gameplay", false);
    //   int seen;
    //   void Tick() { if (Room.Rose(ref seen)) Bind(Room.Root); if (!Room.Up) return; ... }
    //
    // Loaded / Active read Levels.state (the core's own once-a-second culler poll: a dictionary lookup, no scan).
    // Rose(ref seen) is true once per load of the level, after it has been up for `settle` seconds with streaming quiet
    // as long; Root is the level's content root from its SubsceneCuller; Find / FindAll / FindPath search only under it.
    // A destroyed Root counts as an unload, so a reload rises again with a fresh Root. Main thread.
    //
    // The two attributes mark code that is allowed to search: a [LevelBind] member runs only from a Rose edge of the
    // named level, an [OnCommand] member only from a console / bridge command. They do nothing at run time; a build
    // lint can refuse any scene-wide find outside such members.
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Constructor)]
    public sealed class LevelBindAttribute : Attribute
    {
        public readonly string Scene;
        public LevelBindAttribute(string scene) { Scene = scene; }
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
    public sealed class OnCommandAttribute : Attribute { }

    public sealed class LevelGate
    {
        public const string ContentRootSuffix = "_ContentRoot";

        public readonly string Scene;       // key of Levels.state: the scene file name, e.g. "#fields_Gameplay"
        readonly string sceneAlt;           // the same without the leading '#'
        readonly bool needActive;
        readonly float settle;
        // false (default): the level does not rise while a multi-camera capture runs (its frames must not carry a scan)
        public bool RiseDuringCapture;
        int polled = -1;
        bool up, hadRoot;
        float since = -1f, nextResolve;
        int generation;
        Transform root;

        public LevelGate(string scene, bool needActive = true, float settle = 3f)
        {
            Scene = scene ?? "";
            sceneAlt = Scene.Length > 1 && Scene[0] == '#' ? Scene.Substring(1) : null;
            this.needActive = needActive;
            this.settle = settle;
        }

        // "active", "loaded", "streaming" or "unloaded"
        public string StateNow { get { return StateOf(Scene, sceneAlt); } }
        public bool Loaded { get { string st = StateNow; return st == "active" || st == "loaded"; } }
        public bool Active { get { return StateNow == "active"; } }
        // the level is there, settled, and its root is known
        public bool Up { get { Poll(); return up; } }
        // counts the loads of the level seen by this gate (0 = never up)
        public int Generation { get { Poll(); return generation; } }
        public Transform Root { get { Poll(); return up ? root : null; } }

        // true once for every load of the level: the place to bind
        public bool Rose(ref int seen)
        {
            Poll();
            if (!up || seen == generation) return false;
            seen = generation;
            return true;
        }

        public T Find<T>(bool includeInactive = false) where T : Component
        {
            var r = Root;
            if (r == null) return null;
            if (!includeInactive) return r.GetComponentInChildren<T>();
            var all = r.GetComponentsInChildren<T>(true);
            return all.Length > 0 ? all[0] : null;
        }

        public T[] FindAll<T>(bool includeInactive = false) where T : Component
        {
            var r = Root;
            return r == null ? new T[0] : r.GetComponentsInChildren<T>(includeInactive);
        }

        // "a/b/c" below the content root (names only; the first match when names repeat), null when it is not there
        public Transform FindPath(string path)
        {
            var r = Root;
            return r == null ? null : FindOne(r, path);
        }

        // every object at that path below the content root (same-named siblings are all followed)
        public List<Transform> FindAllPath(string path)
        {
            var res = new List<Transform>();
            var r = Root;
            if (r != null) FindAll(r, path == null ? new string[0] : path.Split('/'), 0, res);
            return res;
        }

        void Poll()
        {
            if (polled == Time.frameCount) return;
            polled = Time.frameCount;
            string st = StateNow;
            bool on = needActive ? st == "active" : st == "active" || st == "loaded";
            if (on && hadRoot && root == null) on = false;
            if (!on) { up = hadRoot = false; root = null; since = -1f; return; }
            if (up) return;
            float now = Time.realtimeSinceStartup;
            if (since < 0f) since = now;
            if (now - since < settle || now - Levels.StreamChangedAt < settle || (!RiseDuringCapture && MultiCam.Running) || now < nextResolve) return;
            nextResolve = now + 1f;
            root = ResolveRoot(Scene);
            if (root == null) return;
            hadRoot = up = true;
            generation++;
        }

        public override string ToString() { return Scene + " " + StateNow + (up ? " up #" + generation : ""); }

        // ---------------------------------------------------------------- shared helpers (no gate needed)
        static string StateOf(string scene, string alt)
        {
            string st;
            if (Levels.state.TryGetValue(scene, out st)) return st;
            if (alt != null && Levels.state.TryGetValue(alt, out st)) return st;
            return "unloaded";
        }

        // state of a level by name ("active" / "loaded" / "streaming" / "unloaded")
        public static string StateOf(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return "unloaded";
            string st;
            if (Levels.state.TryGetValue(scene, out st)) return st;
            if (scene.Length > 1 && scene[0] == '#' && Levels.state.TryGetValue(scene.Substring(1), out st)) return st;
            return "unloaded";
        }

        public static bool IsLoaded(string scene) { string st = StateOf(scene); return st == "active" || st == "loaded"; }

        // the same for a caller that keeps both spellings of the name (no allocation): alt = scene without its leading '#', or null
        public static bool IsLoaded(string scene, string alt) { string st = StateOf(scene, alt); return st == "active" || st == "loaded"; }
        public static string AltName(string scene) { return scene != null && scene.Length > 1 && scene[0] == '#' ? scene.Substring(1) : null; }

        // The content root of a loaded level, without the settle wait (for an explicit command); null when the level
        // is not loaded. The answer is remembered until that root is destroyed.
        static readonly Dictionary<string, Transform> roots = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
        public static Transform RootOf(string scene)
        {
            if (!IsLoaded(scene)) return null;
            Transform r;
            if (roots.TryGetValue(scene, out r) && r != null) return r;
            r = ResolveRoot(scene);
            if (r != null) roots[scene] = r; else roots.Remove(scene);
            return r;
        }

        static Transform ResolveRoot(string scene)
        {
            SubsceneCullingManager mgr = null;
            try { mgr = SubsceneCullingManager.instance; } catch (Exception) { }
            if (mgr == null) { var g0 = GameObject.Find("/" + scene + ContentRootSuffix); return g0 != null ? g0.transform : null; }
            string alt = scene.Length > 1 && scene[0] == '#' ? scene.Substring(1) : null;
            foreach (var c in mgr.GetAllSubsceneCullers())
            {
                if (c == null) continue;
                string n;
                try { n = c.SceneName; } catch (Exception) { continue; }
                if (string.IsNullOrEmpty(n)) continue;
                n = n.Substring(n.LastIndexOf('/') + 1);
                if (n.EndsWith(".unity", StringComparison.Ordinal)) n = n.Substring(0, n.Length - 6);
                if (!string.Equals(n, scene, StringComparison.OrdinalIgnoreCase) && !(alt != null && string.Equals(n, alt, StringComparison.OrdinalIgnoreCase))) continue;
                Transform r;
                try { r = c.ContentRoot; } catch (Exception) { return null; }
                if (r == null || r.name.EndsWith(ContentRootSuffix, StringComparison.Ordinal)) return r;
                var inner = r.Find(scene + ContentRootSuffix);
                return inner != null ? inner : r;
            }
            // no culler answers for it: the root object with the level's name, if it is there
            var byName = GameObject.Find("/" + scene + ContentRootSuffix);
            return byName != null ? byName.transform : null;
        }

        // The level an object path belongs to, from its names alone (the object need not be loaded):
        // "#x_Gameplay_ContentRoot/a/b" -> "#x_Gameplay". index = the path segment that names the level. null when the
        // path is not below a level's content root (master scene, DontDestroyOnLoad, spawned objects).
        public static string SceneOfPath(string[] segments, out int index)
        {
            index = -1;
            if (segments == null) return null;
            for (int i = 0; i < segments.Length; i++)
            {
                string s = segments[i];
                if (s.Length <= ContentRootSuffix.Length || !s.EndsWith(ContentRootSuffix, StringComparison.Ordinal)) continue;
                var sc = Levels.ByName(s.Substring(0, s.Length - ContentRootSuffix.Length));
                if (sc != null) { index = i; return sc.name; }
            }
            if (segments.Length > 0)
            {
                var sc = Levels.ByName(segments[0]);
                if (sc != null) { index = 0; return sc.name; }
            }
            return null;
        }

        public static string SceneOfPath(string path)
        {
            int i;
            return string.IsNullOrEmpty(path) ? null : SceneOfPath(path.Split('/'), out i);
        }

        // the level a live object belongs to (walks up to its content root); null for objects outside every level
        public static string SceneOf(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                string n = t.name;
                bool named = n.EndsWith(ContentRootSuffix, StringComparison.Ordinal);
                if (!named && t.GetComponent<SubsceneContentRoot>() == null) continue;
                var sc = Levels.ByName(named ? n.Substring(0, n.Length - ContentRootSuffix.Length) : n);
                return sc != null ? sc.name : null;
            }
            return null;
        }

        // Path walk below one root: segments[from..] are child names, every same-named child is followed.
        public static void FindAll(Transform root, string[] segments, int from, List<Transform> into)
        {
            into.Clear();
            if (root == null) return;
            var cur = scratchA; var next = scratchB;
            cur.Clear(); next.Clear();
            cur.Add(root);
            for (int i = from; i < segments.Length && cur.Count > 0; i++)
            {
                string name = segments[i];
                next.Clear();
                for (int k = 0; k < cur.Count; k++)
                {
                    var t = cur[k];
                    int n = t.childCount;
                    for (int c = 0; c < n; c++) { var ch = t.GetChild(c); if (ch.name == name) next.Add(ch); }
                }
                var sw = cur; cur = next; next = sw;
            }
            into.AddRange(cur);
            scratchA.Clear(); scratchB.Clear();
        }
        static readonly List<Transform> scratchA = new List<Transform>(), scratchB = new List<Transform>();
        static readonly List<Transform> oneTmp = new List<Transform>();

        public static Transform FindOne(Transform root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return root;
            FindAll(root, path.Split('/'), 0, oneTmp);
            var r = oneTmp.Count > 0 ? oneTmp[0] : null;
            oneTmp.Clear();
            return r;
        }
    }

    // A feature that spans several levels: Rose(ref seen) is true once each time any of them rises; Roots / FindAll
    // cover only the levels that are up right now.
    public sealed class LevelSet
    {
        readonly LevelGate[] gates;

        public LevelSet(params LevelGate[] gates) { this.gates = gates ?? new LevelGate[0]; }

        public bool AnyUp { get { foreach (var g in gates) if (g.Up) return true; return false; } }
        public bool AnyLoaded { get { foreach (var g in gates) if (g.Loaded) return true; return false; } }

        public int Generation { get { int sum = 0; foreach (var g in gates) sum += g.Generation; return sum; } }

        public bool Rose(ref int seen)
        {
            int sum = Generation;
            if (sum == 0 || sum == seen) return false;
            seen = sum;
            return true;
        }

        public List<Transform> Roots()
        {
            var l = new List<Transform>();
            foreach (var g in gates) { var r = g.Root; if (r != null) l.Add(r); }
            return l;
        }

        public List<T> FindAll<T>(bool includeInactive = false) where T : Component
        {
            var l = new List<T>();
            foreach (var g in gates) { var r = g.Root; if (r != null) l.AddRange(r.GetComponentsInChildren<T>(includeInactive)); }
            return l;
        }

        public override string ToString()
        {
            var parts = new List<string>();
            foreach (var g in gates) parts.Add(g.ToString());
            return string.Join(", ", parts.ToArray());
        }
    }
}
