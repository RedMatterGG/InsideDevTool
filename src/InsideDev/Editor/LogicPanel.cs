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
        static string[] lines = new string[0]; static Type linesFor; static int linesVersion = -1;
        static int tab;
        static readonly string[] tabs = { "Type logic", "Shared state", "Animation events" };
        static List<CodeGraph.Shared> shared; static string sharedKey;

        // --------------------------------------------------------------- background scan
        static System.Threading.Thread thread; static volatile bool threadDone;
        public static void Tick()
        {
            if (thread == null)
            {
                thread = new System.Threading.Thread(() =>
                {
                    try { while (!CodeGraph.done) CodeGraph.Step(50); } catch (Exception e) { err = e.Message; }
                    threadDone = true;
                });
                thread.IsBackground = true; thread.Priority = System.Threading.ThreadPriority.BelowNormal;
                thread.Start();
            }
            if (threadDone && !announced) { announced = true; DevLog.Write("[logic] code graph: " + CodeGraph.methods.Count + " methods, " + CodeGraph.effectsCount + " effects from " + CodeGraph.scannedMethods + " IL bodies" + (err != null ? " (error " + err + ")" : "")); }
        }
        static bool announced; static string err;
        public static bool Ready { get { return threadDone; } }

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
        public static void Draw(UI ui)
        {
            ui.Label(Ready ? "Code graph: " + CodeGraph.methods.Count + " methods, " + CodeGraph.effectsCount + " effects (static analysis of the game's code; provenance CODE)" : "Scanning game code … " + CodeGraph.scannedMethods + " methods", UI.Dim);
            tab = ui.Tabs(tab, tabs);
            if (tab == 1) { DrawShared(ui); return; }
            if (tab == 2) { DrawAnim(ui); return; }
            // scripts of the selection
            var sel = Selection.Current;
            if (sel != null)
            {
                ui.BeginRow();
                ui.Label("Scripts on " + sel.name + ":", UI.Dim, 150);
                foreach (var mb in sel.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    var t = mb.GetType(); string an = t.Assembly.GetName().Name;
                    if (an != "Assembly-CSharp" && an != "Assembly-CSharp-firstpass") continue;
                    if (ui.Button(t.Name, -2, t == type ? (Color?)UI.TabSel : null)) Show(t);
                }
                ui.EndRow();
            }
            ui.BeginRow();
            ui.Label("Type:", null, 40);
            bool enter = ui.TextField("logic_q", ref query, Mathf.Max(80, ui.Width - 70));
            if ((ui.Button("Show", 60) || enter) && query.Trim().Length > 0) { var t = CodeGraph.FindType(query.Trim()); if (t != null) Show(t); }
            ui.EndRow();
            if (type == null) { ui.Label("Pick a script above, type a class name, or select an object.", UI.Dim); return; }
            if (!Ready) { ui.Label("waiting for the code scan …", UI.Dim); return; }
            if (linesFor != type || linesVersion != CodeGraph.effectsCount) { lines = Report(type).Split('\n'); linesFor = type; linesVersion = CodeGraph.effectsCount; }
            ui.VirtualList("logic_lines", lines.Length, UI.ItemPitch, ui.Remaining, i =>
            {
                string l = lines[i];
                var c = l.StartsWith("CODE LOGIC") || l.StartsWith("  WHAT") || l.StartsWith("  LOADED") ? new Color(0.55f, 0.9f, 0.75f, 1f)
                      : l.Contains("SET by") ? new Color(1f, 0.75f, 0.4f, 1f) : l.Contains("called by") && !l.Contains("←") ? new Color(0.6f, 0.85f, 1f, 1f)
                      : l.Contains("←") ? UI.Dim : l.StartsWith("  " + type.Name + ".") ? Color.white : UI.Txt;
                if (ui.Item(l, c))
                {
                    // click: jump to the first other type named on the line
                    var t = TypeOnLine(l);
                    if (t != null && t != type) Show(t);
                }
            });
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
            ui.BeginRow(); ui.Label("Filter:", null, 44); ui.TextField("logic_sf", ref sharedFilter, Mathf.Max(80, ui.Width - 4)); ui.EndRow();
            if (!Ready) { ui.Label("waiting for the code scan …", UI.Dim); return; }
            string key = sharedFilter;
            if (shared == null || sharedKey != key)
            {
                sharedKey = key; shared = new List<CodeGraph.Shared>();
                foreach (var s in CodeGraph.SharedState())
                    if (key.Length == 0 || (s.type.Name + "." + s.member + " " + string.Join(" ", s.by.ToArray())).IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) shared.Add(s);
            }
            ui.Label(shared.Count + " members of game types that other types set or call (click = open that type's logic)", UI.Dim);
            ui.VirtualList("logic_shared", shared.Count, UI.ItemPitch, ui.Remaining, i =>
            {
                var s = shared[i];
                if (ui.Item(s.type.Name + "." + s.member + "   " + (s.setters > 0 ? "set " + s.setters + "  " : "") + (s.callers > 0 ? "called " + s.callers + "  " : "") + (s.readers > 0 ? "read " + s.readers + "  " : "") + "  by " + string.Join(", ", s.by.ToArray()), s.setters > 0 ? new Color(1f, 0.75f, 0.4f, 1f) : UI.Txt))
                { Show(s.type); tab = 0; }
            });
        }

        // --------------------------------------------------------------- animation events (runtime clips)
        public sealed class AnimEv { public GameObject go; public string clip, function, param; public float time; public List<string> receivers = new List<string>(); }
        public static readonly List<AnimEv> animEvents = new List<AnimEv>();
        public static string ScanAnimationEvents()
        {
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
            foreach (var e in evs)
            {
                var a = new AnimEv { go = go, clip = clip.name, function = e.functionName, time = e.time, param = !string.IsNullOrEmpty(e.stringParameter) ? "\"" + e.stringParameter + "\"" : e.objectReferenceParameter != null ? e.objectReferenceParameter.name : e.intParameter != 0 ? e.intParameter.ToString() : e.floatParameter != 0 ? e.floatParameter.ToString("0.###") : "" };
                foreach (var mb in go.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    MethodInfo m = null;
                    for (var t = mb.GetType(); t != null && m == null; t = t.BaseType)
                        try { m = t.GetMethod(e.functionName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); } catch { }
                    if (m == null) continue;
                    a.receivers.Add(mb.GetType().Name + "." + e.functionName);
                    var mi = CodeGraph.Method(m.DeclaringType, e.functionName);
                    string entry = "animation event in clip '" + clip.name + "' @" + e.time.ToString("0.00") + "s on " + go.name;
                    if (mi != null && !mi.entries.Contains(entry)) mi.entries.Add(entry);
                }
                animEvents.Add(a);
            }
        }

        static string animStatus = "";
        static void DrawAnim(UI ui)
        {
            ui.BeginRow();
            if (ui.Button("Scan loaded objects")) animStatus = ScanAnimationEvents();
            ui.Label(animStatus, UI.Dim);
            ui.EndRow();
            ui.Label("Each event calls the named method on every script of the animated object that has it. Events with no receiver do nothing.", UI.Dim);
            ui.VirtualList("logic_anim", animEvents.Count, UI.ItemPitch, ui.Remaining, i =>
            {
                var a = animEvents[i];
                string recv = a.receivers.Count > 0 ? "→ " + string.Join(", ", a.receivers.ToArray()) : "→ (no receiver on the object)";
                if (ui.Item(a.go.name + "   clip " + a.clip + " @" + a.time.ToString("0.00") + "s   " + a.function + "(" + a.param + ")   " + recv, a.receivers.Count > 0 ? UI.Txt : UI.Dim) && a.go != null)
                    Selection.Set(a.go, "logic");
            });
        }

        public static string AnimDump(string filter)
        {
            var sb = new StringBuilder(ScanAnimationEvents() + "\n");
            foreach (var a in animEvents)
            {
                string l = a.go.name + "  clip " + a.clip + " @" + a.time.ToString("0.00") + "s  " + a.function + "(" + a.param + ")  " + (a.receivers.Count > 0 ? "→ " + string.Join(", ", a.receivers.ToArray()) : "→ no receiver");
                if (filter == null || l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(l).Append('\n');
                if (sb.Length > 30000) break;
            }
            return sb.ToString();
        }
    }
}
