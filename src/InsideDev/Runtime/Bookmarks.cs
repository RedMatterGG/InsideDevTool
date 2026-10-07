using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace InsideDev
{
    // Phase 11: bookmarks = named selectors (+ note, + where the boy was), persisted in _mod\bookmarks.json.
    // Going to a bookmark selects the object when it is loaded; otherwise it can take the boy to the saved spot.
    public static class Bookmarks
    {
        public sealed class Mark { public string name, selector, note, area; public Vector3 pos; public bool hasPos; public string created; public string component, method; public bool IsMethod { get { return !string.IsNullOrEmpty(method); } } }
        public static readonly List<Mark> marks = new List<Mark>();
        static string PathJ { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "bookmarks.json"); } }

        public static void Load()
        {
            marks.Clear();
            try
            {
                if (!File.Exists(PathJ)) return;
                var root = Json.Parse(File.ReadAllText(PathJ)) as Json.Obj;
                if (root == null) return;
                foreach (var x in root.Arr("bookmarks"))
                {
                    var j = x as Json.Obj; if (j == null) continue;
                    var m = new Mark { name = j.Str("name"), selector = j.Str("selector"), note = j.Str("note", ""), area = j.Str("area", ""), created = j.Str("created", ""), component = j.Str("component", ""), method = j.Str("method", "") };
                    var p = j.Arr("pos");
                    if (p.Count == 3) { m.hasPos = true; m.pos = new Vector3((float)(double)p[0], (float)(double)p[1], (float)(double)p[2]); }
                    marks.Add(m);
                }
            }
            catch (Exception e) { DevLog.Error("bookmarks load", e); }
        }

        public static void Save()
        {
            try
            {
                var arr = new List<object>();
                foreach (var m in marks)
                {
                    var j = new Json.Obj();
                    j["name"] = m.name; j["selector"] = m.selector; j["note"] = m.note ?? ""; j["area"] = m.area ?? ""; j["created"] = m.created ?? "";
                    if (m.IsMethod) { j["component"] = m.component; j["method"] = m.method; }
                    if (m.hasPos) j["pos"] = new List<object> { (double)m.pos.x, (double)m.pos.y, (double)m.pos.z };
                    arr.Add(j);
                }
                var root = new Json.Obj(); root["format"] = "insidedev-bookmarks"; root["bookmarks"] = arr;
                File.WriteAllText(PathJ, Json.Write(root) + "\n");
            }
            catch (Exception e) { DevLog.Error("bookmarks save", e); }
        }

        public static Mark Add(GameObject go, string name = null, string note = null)
        {
            if (go == null) return null;
            var m = new Mark { name = name ?? go.name, selector = ObjectSelector.From(go).ToString(), note = note ?? "", hasPos = true, pos = go.transform.position, created = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
            try { m.area = AudioCatalog.AreaOf(go); } catch { }
            marks.Add(m);
            Save();
            DevLog.Write("[bookmark] + " + m.name + "  " + m.selector);
            return m;
        }

        // ---- method bookmarks: object selector + component type + parameterless method name
        public static Mark FindMethod(GameObject go, string component, string method)
        {
            if (go == null) return null;
            string s = ObjectSelector.From(go).path;
            foreach (var m in marks) if (m.IsMethod && m.component == component && m.method == method && ObjectSelector.Parse(m.selector).path == s) return m;
            return null;
        }

        // names of bookmarked methods of one component on one object (cheap when there are no method bookmarks)
        static readonly HashSet<string> markedTmp = new HashSet<string>();
        public static HashSet<string> MarkedMethods(GameObject go, string component)
        {
            markedTmp.Clear();
            string s = null;
            foreach (var m in marks)
            {
                if (!m.IsMethod || m.component != component) continue;
                if (s == null) s = ObjectSelector.From(go).path;
                if (ObjectSelector.Parse(m.selector).path == s) markedTmp.Add(m.method);
            }
            return markedTmp;
        }

        public static void ToggleMethod(GameObject go, string component, string method)
        {
            var old = FindMethod(go, component, method);
            if (old != null) { Remove(old); DevLog.Write("[bookmark] - " + old.name); return; }
            var m = new Mark { name = go.name + "  " + component + "." + method + "()", selector = ObjectSelector.From(go).ToString(), note = "", hasPos = true, pos = go.transform.position,
                created = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), component = component, method = method };
            try { m.area = AudioCatalog.AreaOf(go); } catch { }
            marks.Add(m); Save();
            DevLog.Write("[bookmark] + " + m.name);
        }

        // run a method bookmark on the loaded object; returns a status line
        public static string Call(Mark m)
        {
            var l = Mods.ResolveAll(m.selector);
            if (l.Count == 0) return m.name + ": object not loaded (use Go to travel there first)";
            foreach (var c in l[0].GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != m.component) continue;
                var mi = c.GetType().GetMethod(m.method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (mi == null) return "no method " + m.method + " on " + m.component;
                try { var r = mi.Invoke(c, null); DevLog.Write("[call] " + m.component + "." + m.method + "() on " + c.name + " (bookmark)"); return "called " + m.name + (mi.ReturnType != typeof(void) ? " -> " + r : ""); }
                catch (Exception e) { return m.method + " failed: " + (e.InnerException ?? e).Message; }
            }
            return "component " + m.component + " not found on " + l[0].name;
        }

        public static bool Has(GameObject go)
        {
            if (go == null) return false;
            string s = ObjectSelector.From(go).path;
            foreach (var m in marks) if (!m.IsMethod && ObjectSelector.Parse(m.selector).path == s) return true;
            return false;
        }

        public static void Remove(Mark m) { marks.Remove(m); Save(); }

        // select the bookmarked object if it is loaded; returns a status line
        public static string Go(Mark m, bool teleportIfMissing)
        {
            var l = Mods.ResolveAll(m.selector);
            if (l.Count >= 1) { Selection.Set(l[0], "bookmark"); return "selected " + m.name + (l.Count > 1 ? " (" + l.Count + " matches, first taken)" : ""); }
            if (teleportIfMissing && m.hasPos) { G.Teleport(m.pos); return m.name + " is not loaded - moved the boy to where it was bookmarked (" + m.area + ")"; }
            return m.name + " is not loaded (area " + m.area + ")";
        }
    }
}
