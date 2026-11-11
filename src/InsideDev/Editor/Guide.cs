using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Phase 11: guided actions + contextual explanations at the top of the Inspector.
    // Explains in game terms what the selected object is, why it may not be doing anything, and offers the
    // relevant one-click actions (all recorded in History, so they can be undone or turned into a mod).
    // The texts are worked out when the selection or the History changes and twice per second, not on every frame.
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

        static readonly Color cHead = new Color(0.6f, 0.85f, 1f, 1f), cWarn = new Color(1f, 0.7f, 0.4f, 1f), cCode = new Color(0.4f, 0.95f, 0.95f, 1f);

        // what was worked out for the selection
        struct Off { public Behaviour b; public string label, button; }
        static int forId, forVersion = -1; static float at;
        static string what, parentOff, colliderOff;
        static Collider col; static GameObject offParent;
        static bool bookmarked, hasScripts;
        static readonly List<Off> offs = new List<Off>();
        static readonly List<Type> gameScripts = new List<Type>();

        static int codeFor; static bool codeBuilt; static readonly List<KeyValuePair<Type, string>> codeLines = new List<KeyValuePair<Type, string>>();

        static void Refresh(GameObject go)
        {
            var lines = new List<string>();
            var seen = new HashSet<string>();
            offs.Clear(); gameScripts.Clear();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var t = c.GetType();
                string n = t.Name, e;
                bool known = explain.TryGetValue(n, out e);
                if (known && seen.Add(n)) lines.Add(e);
                var b = c as Behaviour;
                if (known && b != null && !b.enabled) offs.Add(new Off { b = b, label = n + " disabled ", button = "Enable " + n });
                if (c is MonoBehaviour && GameTypes.IsGame(t) && !gameScripts.Contains(t)) gameScripts.Add(t);
            }
            hasScripts = gameScripts.Count > 0;
            col = go.GetComponent<Collider>();
            if (col != null && seen.Count == 0) lines.Add(col.isTrigger ? "trigger collider (no known game script on it)" : "solid collider");
            what = lines.Count > 0 ? "WHAT IS THIS:  " + string.Join("  •  ", lines.ToArray()) : null;
            colliderOff = col != null ? (col.isTrigger ? "trigger" : "collider") + " disabled " : null;
            offParent = null; parentOff = null;
            if (go.activeSelf && !go.activeInHierarchy)
            {
                var p = go.transform.parent; while (p != null && p.gameObject.activeSelf) p = p.parent;
                offParent = p != null ? p.gameObject : null;
                parentOff = "a parent is off (" + (p != null ? p.name : "?") + ") ";
            }
            bookmarked = Bookmarks.Has(go);
        }

        public static void Draw(UI ui, GameObject go)
        {
            if (go == null) return;
            int gid = go.GetInstanceID();
            float now = Time.realtimeSinceStartup;
            if (gid != forId || forVersion != ChangeRecorder.Version || now - at > 0.5f)
            {
                forId = gid; forVersion = ChangeRecorder.Version; at = now;
                Refresh(go);
            }
            if (what != null) ui.Label(what, cHead);

            // why is it not doing anything? + the matching fix
            ui.BeginRow();
            bool any = false;
            if (!go.activeSelf)
            {
                ui.Label("switched off (hidden) ", cWarn); any = true;
                if (ui.Button("Show it")) ChangeRecorder.SetActive(go, true, "guide");
            }
            else if (parentOff != null && !go.activeInHierarchy)
            {
                ui.Label(parentOff, cWarn); any = true;
                if (offParent != null && ui.Button("Show parent")) ChangeRecorder.SetActive(offParent, true, "guide");
            }
            if (col != null && !col.enabled)
            {
                ui.Label(colliderOff, cWarn); any = true;
                if (ui.Button("Enable collider")) ChangeRecorder.SetEnabledRecorded(col, true, "guide");
            }
            for (int i = 0; i < offs.Count; i++)
            {
                var o = offs[i];
                if (o.b == null || o.b.enabled) continue;
                ui.Label(o.label, cWarn); any = true;
                if (ui.Button(o.button)) ChangeRecorder.SetEnabledRecorded(o.b, true, "guide");
            }
            if (!any) ui.Space(0);
            // code logic summary per script (who else sets / calls / reads it) - details in the Logic panel.
            // An object with game scripts is what the code scan is for: showing one starts it.
            if (hasScripts && LogicPanel.Ready)
            {
                // per selected object, rebuilt when the selection changes (the code graph does not change)
                if (gid != codeFor || !codeBuilt)
                {
                    codeFor = gid; codeBuilt = true; codeLines.Clear();
                    foreach (var t in gameScripts)
                    {
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
                    ui.Label(kv.Value, cCode, Mathf.Max(100, ui.Width - 70));
                    if (ui.Button("Logic", 60)) { LogicPanel.Show(kv.Key); DevCore.Instance.ShowPanel("logic"); }
                }
            }
            else codeBuilt = false;
            if (ui.Button(bookmarked ? "Bookmarked" : "Bookmark", 90) && !bookmarked) { Bookmarks.Add(go); bookmarked = true; }
            ui.EndRow();
        }
    }
}
