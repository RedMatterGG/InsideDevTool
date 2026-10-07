using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Phase 11: guided actions + contextual explanations at the top of the Inspector.
    // Explains in game terms what the selected object is, why it may not be doing anything, and offers the
    // relevant one-click actions (all recorded in History, so they can be undone or turned into a mod).
    public static class Guide
    {
        static readonly Dictionary<string, string> explain = new Dictionary<string, string>
        {
            { "IsTriggeredByProbe", "trigger volume: raises signals when the boy (or another probe) enters / leaves" },
            { "Savepoint", "checkpoint: the game saves / respawns here" },
            { "PlayMakerFSM", "state machine (PlayMaker): scripted logic that reacts to events and signals" },
            { "SignalConnector", "wiring: connects signal outputs to inputs of other objects" },
            { "AkGameObj", "Wwise emitter: sounds posted on this object play from its position" },
            { "AudioEventSimple", "sound hook: posts a Wwise event when its owner asks" },
            { "BoyDepthObstruction", "depth obstruction: keeps the boy from walking through / grabbing across it" },
            { "ForcePushObject", "shockwave prop: reacts to the mines shockwave" },
            { "Rigidbody", "physics body" },
            { "Animator", "Mecanim animator" },
            { "Animation", "legacy animation player" },
            { "Camera", "camera" },
            { "Light", "light" },
            { "PersistentBool", "saved flag: part of the savegame's world state" },
        };

        static readonly Color cHead = new Color(0.6f, 0.85f, 1f, 1f), cWarn = new Color(1f, 0.7f, 0.4f, 1f);

        static int codeFor; static readonly List<KeyValuePair<Type, string>> codeLines = new List<KeyValuePair<Type, string>>();
        public static void Draw(UI ui, GameObject go)
        {
            if (go == null) return;
            var lines = new List<string>();
            var seen = new HashSet<string>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                string n = c.GetType().Name, e;
                if (explain.TryGetValue(n, out e) && seen.Add(n)) lines.Add(e);
            }
            var col = go.GetComponent<Collider>();
            if (col != null && seen.Count == 0) lines.Add(col.isTrigger ? "trigger collider (no known game script on it)" : "solid collider");
            if (lines.Count > 0) ui.Label("WHAT IS THIS:  " + string.Join("  •  ", lines.ToArray()), cHead);

            // why is it not doing anything? + the matching fix
            ui.BeginRow();
            bool any = false;
            if (!go.activeSelf)
            {
                ui.Label("switched off (hidden) ", cWarn); any = true;
                if (ui.Button("Show it")) ChangeRecorder.SetActive(go, true, "guide");
            }
            else if (!go.activeInHierarchy)
            {
                var p = go.transform.parent; while (p != null && p.gameObject.activeSelf) p = p.parent;
                ui.Label("a parent is off (" + (p != null ? p.name : "?") + ") ", cWarn); any = true;
                if (p != null && ui.Button("Show parent")) ChangeRecorder.SetActive(p.gameObject, true, "guide");
            }
            if (col != null && !col.enabled)
            {
                ui.Label((col.isTrigger ? "trigger" : "collider") + " disabled ", cWarn); any = true;
                if (ui.Button("Enable collider")) ChangeRecorder.SetEnabledRecorded(col, true, "guide");
            }
            foreach (var b in go.GetComponents<Behaviour>())
                if (b != null && !b.enabled && explain.ContainsKey(b.GetType().Name))
                {
                    ui.Label(b.GetType().Name + " disabled ", cWarn); any = true;
                    if (ui.Button("Enable " + b.GetType().Name)) ChangeRecorder.SetEnabledRecorded(b, true, "guide");
                }
            if (!any) ui.Space(0);
            // code logic summary per script (who else sets / calls / reads it) - details in the Logic panel
            if (LogicPanel.Ready)
            {
                // per selected object, rebuilt when the selection changes (the code graph does not change)
                int gid = go.GetInstanceID();
                if (gid != codeFor)
                {
                    codeFor = gid; codeLines.Clear();
                    foreach (var mb in go.GetComponents<MonoBehaviour>())
                    {
                        if (mb == null) continue;
                        var t = mb.GetType(); string an = t.Assembly.GetName().Name;
                        if (an != "Assembly-CSharp" && an != "Assembly-CSharp-firstpass") continue;
                        var inc = CodeGraph.Incoming(t);
                        if (inc.Count == 0) continue;
                        var setters = new List<string>(); var readers = new List<string>();
                        foreach (var x in inc) { var l = x.verb == "reads" ? readers : setters; if (!l.Contains(x.by.type.Name)) l.Add(x.by.type.Name); }
                        codeLines.Add(new KeyValuePair<Type, string>(t, "CODE  " + t.Name + ":" + (setters.Count > 0 ? "  set/called by " + string.Join(", ", setters.ToArray()) : "") + (readers.Count > 0 ? "   read by " + string.Join(", ", readers.ToArray()) : "")));
                    }
                }
                foreach (var kv in codeLines)
                {
                    ui.EndRow(); ui.BeginRow();
                    ui.Label(kv.Value, new Color(0.4f, 0.95f, 0.95f, 1f), Mathf.Max(100, ui.Width - 70));
                    if (ui.Button("Logic", 60)) { LogicPanel.Show(kv.Key); DevCore.Instance.ShowPanel("logic"); }
                }
            }
            if (ui.Button(Bookmarks.Has(go) ? "Bookmarked" : "Bookmark", 90) && !Bookmarks.Has(go)) Bookmarks.Add(go);
            ui.EndRow();
        }
    }
}
