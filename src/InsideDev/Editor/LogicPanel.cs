using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Logic panel: game logic recovered from code (CodeGraph) for any script type, loaded or not.
    //   - who SETS / READS / CALLS members of the type, under which condition, and how that code gets run
    //     (Unity message, signal input, coroutine, animation event, called by ... up to the entry point)
    //   - what the type's own methods do (signals, sounds, SetActive, writes to other objects), in code order
    //   - Shared state: every member of any game type that other types set or call (cross-object flags such as
    //     WristSecret.isActive), searchable
    //   - Animation events of loaded objects (clip, time, function) and the script methods they run
    // Types are clickable; loaded instances can be selected.
    public static class LogicPanel
    {
        static Type type; static string query = "", sharedFilter = "";
        static string[] lines = new string[0]; static Color[] lineColors = new Color[0]; static Type linesFor; static int linesVersion = -1;
        static int tab;
        static readonly string[] tabs = { "Type logic", "Shared state", "Animation events" };
        static List<CodeGraph.Shared> shared; static string sharedKey;
        static readonly ListCache sharedRows = new ListCache();
        static SearchDelay sharedDelay;
        static readonly Color cHead = new Color(0.55f, 0.9f, 0.75f, 1f), cSet = new Color(1f, 0.75f, 0.4f, 1f), cCall = new Color(0.6f, 0.85f, 1f, 1f);

        // --------------------------------------------------------------- background scan (on demand)
        // The scan reads every method body of Assembly-CSharp and Assembly-CSharp-firstpass on a worker thread. It starts
        // when somebody needs the code graph: this panel, the LOGIC graph, the Inspector's code lines, a "logic" command,
        // the Audio DB's code paths (asking Ready counts as needing it), or anything that called
        // Background.Want(Background.Code, seconds). With background=always it starts with the game, as before 0.11.0.
        static System.Threading.Thread thread; static volatile bool threadDone, wanted;
        static int tickN;
        static bool announced; static string err;

        // any thread
        public static void Request() { wanted = true; }

        // main thread, every frame: two branches until the scan is wanted, one after it was announced
        public static void Tick()
        {
            if (announced) return;
            if (thread == null)
            {
                if (!wanted && ((++tickN & 31) != 0 || !Background.Wanted(Background.Code))) return;
                thread = new System.Threading.Thread(() =>
                {
                    try { while (!CodeGraph.done) CodeGraph.Step(50); } catch (Exception e) { err = e.Message; }
                    threadDone = true;
                });
                thread.IsBackground = true; thread.Priority = System.Threading.ThreadPriority.BelowNormal;
                thread.Name = "InsideDev.CodeGraph";
                thread.Start();
                DevLog.Write("[logic] code scan started");
            }
            if (threadDone) { announced = true; DevLog.Write("[logic] code graph: " + CodeGraph.methods.Count + " methods, " + CodeGraph.effectsCount + " effects from " + CodeGraph.scannedMethods + " IL bodies" + (err != null ? " (error " + err + ")" : "")); }
        }

        // True once the code graph can be read. Asking starts the scan when it has not started yet.
        public static bool Ready { get { if (!threadDone) wanted = true; return threadDone; } }
        // The same answer without starting anything (for code that only uses the graph when it happens to be there).
        public static bool Scanned { get { return threadDone; } }
        public static string ScanStatus
        {
            get
            {
                return threadDone ? "ready: " + CodeGraph.methods.Count + " methods, " + CodeGraph.effectsCount + " effects" + (err != null ? " (error " + err + ")" : "")
                     : thread != null ? "scanning: " + CodeGraph.scannedMethods + " methods so far"
                     : "not started (starts when the Logic panel, the LOGIC graph or a logic command needs it)";
            }
        }

        public static void Show(Type t) { type = t; tab = 0; linesFor = null; }

        public static string Report(Type t) { return Ready ? CodeGraph.TypeReport(t, 3) + LiveInstances(t) : "code graph still scanning (" + CodeGraph.scannedMethods + " methods)"; }

        static string LiveInstances(Type t)
        {
            if (!typeof(Component).IsAssignableFrom(t)) return "  (plain class: no scene objects)\n";
            UnityEngine.Object[] objs; try { objs = Resources.FindObjectsOfTypeAll(t); } catch { return ""; }
            var sb = new StringBuilder("  LOADED INSTANCES: ");
            int n = 0;
            foreach (var o in objs) { var c = o as Component; if (c == null || c.gameObject.hideFlags != HideFlags.None) continue; if (n++ < 6) sb.Append(Inspector.PathOf(c.transform)).Append(c.gameObject.activeInHierarchy ? "" : " (inactive)").Append("; "); }
            if (n == 0) sb.Append("none (the area using it is not loaded)");
            else if (n > 6) sb.Append("… ").Append(n).Append(" in total");
            return sb.Append('\n').ToString();
        }

        // --------------------------------------------------------------- UI
        static string headLine; static float headAt; static bool headFinal;
        static int scriptsFor; static float scriptsAt; static string scriptsLabel;
        static readonly List<Type> scripts = new List<Type>();
        static UI curUi;
        static readonly Action<int> drawLine = DrawLine, drawShared = DrawSharedRow, drawAnim = DrawAnimRow;

        public static void Draw(UI ui)
        {
            Request();
            Background.Want(Background.Code, 5f);
            float now = Time.realtimeSinceStartup;
            if (!headFinal && (headLine == null || now - headAt > 0.25f))
            {
                headAt = now; headFinal = threadDone;
                headLine = threadDone ? "Code graph: " + CodeGraph.methods.Count + " methods, " + CodeGraph.effectsCount + " effects (static analysis of the game's code; provenance CODE)" : "Scanning game code … " + CodeGraph.scannedMethods + " methods";
            }
            ui.Label(headLine, UI.Dim);
            tab = ui.Tabs(tab, tabs);
            curUi = ui;
            if (tab == 1) { DrawShared(ui); return; }
            if (tab == 2) { DrawAnim(ui); return; }
            // scripts of the selection (the list is read again when the selection changes and once per second)
            var sel = Selection.Current;
            if (sel != null)
            {
                int sid = sel.GetInstanceID();
                if (sid != scriptsFor || now - scriptsAt > 1f)
                {
                    scriptsFor = sid; scriptsAt = now; scripts.Clear();
                    scriptsLabel = "Scripts on " + sel.name + ":";
                    foreach (var mb in sel.GetComponents<MonoBehaviour>())
                    {
                        if (mb == null) continue;
                        var t = mb.GetType();
                        if (GameTypes.IsGame(t) && !scripts.Contains(t)) scripts.Add(t);
                    }
                }
                ui.BeginRow();
                ui.Label(scriptsLabel, UI.Dim, 150);
                for (int i = 0; i < scripts.Count; i++)
                    if (ui.Button(scripts[i].Name, -2, scripts[i] == type ? (Color?)UI.TabSel : null)) Show(scripts[i]);
                ui.EndRow();
            }
            else scriptsFor = 0;
            ui.BeginRow();
            ui.Label("Type:", null, 40);
            bool enter = EditorInput.Field(ui, "logic_q", ref query, Mathf.Max(80, ui.Width - 70));
            if ((ui.Button("Show", 60) || enter) && query.Trim().Length > 0)
            {
                if (!threadDone) Note("the code scan is still running: try again in a moment");
                else { var t = CodeGraph.FindType(query.Trim()); if (t != null) Show(t); else Note("no game type named '" + query.Trim() + "'"); }
            }
            ui.EndRow();
            if (note != null && now - noteAt < 5f) ui.Label(note, new Color(1f, 0.7f, 0.4f, 1f));
            if (type == null) { ui.Label("Pick a script above, type a class name, or select an object.", UI.Dim); return; }
            if (!threadDone) { ui.Label("waiting for the code scan …", UI.Dim); return; }
            if (linesFor != type || linesVersion != CodeGraph.effectsCount)
            {
                lines = Report(type).Split('\n'); linesFor = type; linesVersion = CodeGraph.effectsCount;
                lineColors = new Color[lines.Length];
                string own = "  " + type.Name + ".";
                for (int i = 0; i < lines.Length; i++)
                {
                    string l = lines[i];
                    lineColors[i] = l.StartsWith("CODE LOGIC", StringComparison.Ordinal) || l.StartsWith("  WHAT", StringComparison.Ordinal) || l.StartsWith("  LOADED", StringComparison.Ordinal) ? cHead
                                  : l.Contains("SET by") ? cSet : l.Contains("called by") && !l.Contains("←") ? cCall
                                  : l.Contains("←") ? UI.Dim : l.StartsWith(own, StringComparison.Ordinal) ? Color.white : UI.Txt;
                }
            }
            ui.VirtualList("logic_lines", lines.Length, UI.ItemPitch, ui.Remaining, drawLine);
        }

        static string note; static float noteAt;
        static void Note(string s) { note = s; noteAt = Time.realtimeSinceStartup; }

        static void DrawLine(int i)
        {
            if (i >= lines.Length) return;
            string l = lines[i];
            if (curUi.Item(l, lineColors[i]))
            {
                // click: jump to the first other type named on the line
                var t = TypeOnLine(l);
                if (t != null && t != type) Show(t);
            }
        }

        static Type TypeOnLine(string l)
        {
            foreach (var word in l.Split(new[] { ' ', '(', ')', ',', '[', ']', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int dot = word.IndexOf('.');
                if (dot <= 0) continue;
                var t = CodeGraph.FindType(word.Substring(0, dot));
                if (t != null && t != type) return t;
            }
            return null;
        }

        static void DrawShared(UI ui)
        {
            ui.BeginRow(); ui.Label("Filter:", null, 44); EditorInput.Field(ui, "logic_sf", ref sharedFilter, Mathf.Max(80, ui.Width - 4)); ui.EndRow();
            if (!threadDone) { ui.Label("waiting for the code scan …", UI.Dim); return; }
            string key = sharedDelay.Value(sharedFilter, 0.12f);
            if (shared == null || sharedKey != key)
            {
                sharedKey = key; shared = new List<CodeGraph.Shared>();
                foreach (var s in CodeGraph.SharedState())
                    if (key.Length == 0 || (s.type.Name + "." + s.member + " " + string.Join(" ", s.by.ToArray())).IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) shared.Add(s);
                sharedRows.Invalidate();
                sharedHead = shared.Count + " members of game types that other types set or call (click = open that type's logic)";
            }
            if (sharedRows.Changed(shared.Count, 0, 0, key)) sharedRows.Fill(shared.Count);
            ui.Label(sharedHead, UI.Dim);
            ui.VirtualList("logic_shared", shared.Count, UI.ItemPitch, ui.Remaining, drawShared);
        }
        static string sharedHead = "";

        static void DrawSharedRow(int i)
        {
            if (i >= shared.Count) return;
            var s = shared[i];
            string t = sharedRows.Text(i) ?? sharedRows.Text(i, s.type.Name + "." + s.member + "   " + (s.setters > 0 ? "set " + s.setters + "  " : "") + (s.callers > 0 ? "called " + s.callers + "  " : "") + (s.readers > 0 ? "read " + s.readers + "  " : "") + "  by " + string.Join(", ", s.by.ToArray()));
            if (curUi.Item(t, s.setters > 0 ? cSet : UI.Txt)) { Show(s.type); tab = 0; }
        }

        // --------------------------------------------------------------- animation events (runtime clips)
        public sealed class AnimEv
        {
            public GameObject go; public string clip, function, param; public float time; public List<string> receivers = new List<string>();
            public string goName, text, gone;   // name at scan time, the row shown in the panel, the row once the object is unloaded
        }
        public static readonly List<AnimEv> animEvents = new List<AnimEv>();

        // Lists the animation events of the loaded objects and notes them as entry points of the methods they call.
        // That writes into the code graph, so it waits for the scan (the worker fills the same tables).
        public static string ScanAnimationEvents()
        {
            if (!Ready) return "waiting for the code scan (" + CodeGraph.scannedMethods + " methods so far): try again in a moment";
            animEvents.Clear();
            var seen = new HashSet<int>();
            int clips = 0;
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(Animation)))
                {
                    var an = o as Animation; if (an == null || an.gameObject.hideFlags != HideFlags.None) continue;
                    var cl = new List<AnimationClip>(); AnimSafe.Clips(an, cl); foreach (var c in cl) { clips++; AddClip(an.gameObject, c, seen); }
                }
                foreach (var o in Resources.FindObjectsOfTypeAll(typeof(Animator)))
                {
                    var am = o as Animator; if (am == null || am.gameObject.hideFlags != HideFlags.None || am.runtimeAnimatorController == null) continue;
                    foreach (var clip in am.runtimeAnimatorController.animationClips) { if (clip == null) continue; clips++; AddClip(am.gameObject, clip, seen); }
                }
            }
            catch (Exception e) { return "scan failed: " + e.Message; }
            return animEvents.Count + " animation events in " + clips + " clips of loaded objects";
        }

        static void AddClip(GameObject go, AnimationClip clip, HashSet<int> seen)
        {
            AnimationEvent[] evs; try { evs = clip.events; } catch { return; }
            string goName = go.name;
            foreach (var e in evs)
            {
                var a = new AnimEv { go = go, goName = goName, clip = clip.name, function = e.functionName, time = e.time, param = !string.IsNullOrEmpty(e.stringParameter) ? "\"" + e.stringParameter + "\"" : e.objectReferenceParameter != null ? e.objectReferenceParameter.name : e.intParameter != 0 ? Fmt.I(e.intParameter) : e.floatParameter != 0 ? Fmt.F(e.floatParameter) : "" };
                foreach (var mb in go.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    MethodInfo m = null;
                    for (var t = mb.GetType(); t != null && m == null; t = t.BaseType)
                        try { m = t.GetMethod(e.functionName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); } catch { }
                    if (m == null) continue;
                    a.receivers.Add(mb.GetType().Name + "." + e.functionName);
                    var mi = CodeGraph.Method(m.DeclaringType, e.functionName);
                    string entry = "animation event in clip '" + clip.name + "' @" + Fmt.F(e.time, "0.00") + "s on " + goName;
                    if (mi != null && !mi.entries.Contains(entry)) mi.entries.Add(entry);
                }
                a.text = goName + "   clip " + a.clip + " @" + Fmt.F(a.time, "0.00") + "s   " + a.function + "(" + a.param + ")   " + (a.receivers.Count > 0 ? "→ " + string.Join(", ", a.receivers.ToArray()) : "→ (no receiver on the object)");
                animEvents.Add(a);
            }
        }

        static string animStatus = "";
        static void DrawAnim(UI ui)
        {
            if (!threadDone) { ui.Label("waiting for the code scan …", UI.Dim); return; }
            ui.BeginRow();
            if (ui.Button("Scan loaded objects")) animStatus = ScanAnimationEvents();
            ui.Label(animStatus, UI.Dim);
            ui.EndRow();
            ui.Label("Each event calls the named method on every script of the animated object that has it. Events with no receiver do nothing.", UI.Dim);
            ui.VirtualList("logic_anim", animEvents.Count, UI.ItemPitch, ui.Remaining, drawAnim);
        }

        static void DrawAnimRow(int i)
        {
            if (i >= animEvents.Count) return;
            var a = animEvents[i];
            // the objects of a scan do not survive a level or checkpoint load
            if (a.go == null) { if (a.gone == null) a.gone = "(unloaded) " + a.text; curUi.Item(a.gone, UI.Dim); return; }
            if (curUi.Item(a.text, a.receivers.Count > 0 ? UI.Txt : UI.Dim)) Selection.Set(a.go, "logic");
        }

        public static string AnimDump(string filter)
        {
            var sb = new StringBuilder(ScanAnimationEvents() + "\n");
            foreach (var a in animEvents)
            {
                string l = a.goName + "  clip " + a.clip + " @" + Fmt.F(a.time, "0.00") + "s  " + a.function + "(" + a.param + ")  " + (a.receivers.Count > 0 ? "→ " + string.Join(", ", a.receivers.ToArray()) : "→ no receiver");
                if (filter == null || l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(l).Append('\n');
                if (sb.Length > 30000) break;
            }
            return sb.ToString();
        }
    }
}
