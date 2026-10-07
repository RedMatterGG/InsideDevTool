using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // "Where does this value come from?" - the live object next to the level file on disk.
    //   AS AUTHORED      the live value equals the level file
    //   CHANGED BY YOU   History holds an edit of it (editor / bridge), or an enabled mod was applied to it
    //   CHANGED IN GAME  differs from the level file and you did not touch it: game code, an animation or the
    //                    savegame changed it after the level loaded
    // Mapping: the level's content root ("#X_Gameplay_ContentRoot") names the build scene (#X_Gameplay -> levelN);
    // the path below it is matched in the file with the same " #n" numbering for same-named siblings.
    public static class LevelCompare
    {
        public sealed class Row { public string what, file, live, origin; public bool differs; }
        public sealed class Result
        {
            public string error, levelFile, filePath; public bool pending, notInFile;
            public readonly List<Row> rows = new List<Row>();
            public readonly List<string> subtree = new List<string>();
            public int subtreeChecked, subtreeDiffers;
        }

        public static bool Map(GameObject go, out string levelFile, out string filePath, out string why)
        {
            levelFile = filePath = why = null;
            var parts = new List<string>();
            Transform root = null;
            for (var t = go.transform; t != null; t = t.parent)
            {
                parts.Add(t.name + Dup(t));
                if (t.name.EndsWith("_ContentRoot") || t.GetComponent<SubsceneContentRoot>() != null) { root = t; break; }
            }
            if (root == null) { why = "not inside a level's content root (master scene, persistent or spawned object)"; return false; }
            string scene = root.name.EndsWith("_ContentRoot") ? root.name.Substring(0, root.name.Length - 12) : root.name;
            var s = Levels.ByName(scene);
            if (s == null) { why = "no build scene named " + scene; return false; }
            levelFile = s.File;
            parts.Add(scene);
            parts.Reverse();
            filePath = string.Join("/", parts.ToArray());
            return true;
        }

        static string Dup(Transform t)
        {
            if (t.parent == null) return "";
            int nth = 0;
            for (int i = 0; i < t.parent.childCount; i++) { var c = t.parent.GetChild(i); if (c.name == t.name) nth++; if (c == t) break; }
            return nth > 1 ? " #" + nth : "";
        }

        static int ClassOf(Component c)
        {
            if (c is MonoBehaviour) return 114;
            switch (c.GetType().Name)
            {
                case "Transform": return 4; case "RectTransform": return 224; case "MeshRenderer": return 23; case "MeshFilter": return 33;
                case "SkinnedMeshRenderer": return 137; case "BoxCollider": return 65; case "SphereCollider": return 135; case "CapsuleCollider": return 136;
                case "MeshCollider": return 64; case "Light": return 108; case "Animation": return 111; case "Animator": return 95; case "AudioSource": return 82;
                case "Camera": return 20; case "Rigidbody": return 54; case "ParticleSystem": return 198; case "ParticleSystemRenderer": return 199;
            }
            return -1;
        }

        static bool LiveEnabled(Component c, out bool v)
        {
            v = false;
            var b = c as Behaviour; if (b != null) { v = b.enabled; return true; }
            var r = c as Renderer; if (r != null) { v = r.enabled; return true; }
            var col = c as Collider; if (col != null) { v = col.enabled; return true; }
            return false;
        }

        // which of your edits / mods touched this property
        static string Yours(GameObject go, Component comp, ChangeRecorder.PropKind kind)
        {
            foreach (var n in ChangeRecorder.NetChanges())
                if (n.prop.go == go && n.prop.kind == kind && (comp == null || n.prop.comp == comp)) return "CHANGED BY YOU (History)";
            int iid = go.GetInstanceID();
            foreach (var m in Mods.mods)
            {
                if (!m.enabled) continue;
                foreach (var op in m.ops)
                    if ((op.appliedTo == iid || op.appliedTo == -iid) && op.type == "set" && (kind == ChangeRecorder.PropKind.Active ? op.property == "active" : kind == ChangeRecorder.PropKind.Enabled ? op.property == "enabled" : op.property == "position" || op.property == "localPosition"))
                        return "CHANGED BY MOD '" + m.name + "'";
            }
            return null;
        }

        static void Add(Result r, string what, string file, string live, bool same, GameObject go, Component comp, ChangeRecorder.PropKind kind)
        {
            string y = Yours(go, comp, kind);
            r.rows.Add(new Row { what = what, file = file, live = live, differs = !same, origin = same ? (y != null ? "as authored (you changed it and set it back)" : "AS AUTHORED") : y ?? "CHANGED IN GAME (game code, animation or savegame)" });
        }

        public static Result Compare(GameObject go, bool deep, bool wait)
        {
            var r = new Result();
            string lf, fp, why;
            if (!Map(go, out lf, out fp, out why)) { r.error = why; return r; }
            r.levelFile = lf; r.filePath = fp;
            if (LevelFile.DataDir == null) LevelFile.DataDir = Application.dataPath;
            var lv = wait ? LevelFile.GetNow(lf) : LevelFile.Get(lf);
            if (lv == null) { r.pending = true; return r; }
            if (lv.error != null) { r.error = "reading " + lf + ": " + lv.error; return r; }
            LevelFile.Go fg;
            if (!lv.byPath.TryGetValue(fp, out fg)) { r.notInFile = true; return r; }

            Add(r, "active (switched on)", fg.active ? "on" : "OFF", go.activeSelf ? "on" : "OFF", fg.active == go.activeSelf, go, null, ChangeRecorder.PropKind.Active);
            var comps = go.GetComponents<Component>();
            if (comps.Length != fg.comps.Count) r.rows.Add(new Row { what = "components", file = fg.comps.Count.ToString(), live = comps.Length.ToString(), differs = true, origin = "CHANGED IN GAME (components added / removed at runtime)" });
            for (int i = 0; i < comps.Length && i < fg.comps.Count; i++)
            {
                var c = comps[i]; var fc = fg.comps[i]; if (c == null) continue;
                int cls = ClassOf(c);
                if (cls >= 0 && cls != fc.cls) { r.rows.Add(new Row { what = "component " + i, file = LevelFile.ClassName(fc.cls), live = c.GetType().Name, differs = true, origin = "order differs - not compared" }); continue; }
                bool le;
                if (fc.enabled >= 0 && LiveEnabled(c, out le))
                    Add(r, c.GetType().Name + " enabled", fc.enabled != 0 ? "on" : "OFF", le ? "on" : "OFF", (fc.enabled != 0) == le, go, c, ChangeRecorder.PropKind.Enabled);
                var ren = c as Renderer;
                if (ren != null && fc.materials != null)
                {
                    Material[] ms; try { ms = ren.sharedMaterials; } catch { ms = new Material[0]; }
                    var live = new List<string>(); foreach (var m in ms) live.Add(MaterialFacts.Clean(m));
                    string fs = string.Join(" + ", fc.materials.ToArray()), ls = string.Join(" + ", live.ToArray());
                    bool same = fs == ls || ls.Replace(" (runtime copy)", "") == fs;
                    r.rows.Add(new Row { what = c.GetType().Name + " materials", file = fs, live = ls, differs = !same, origin = same ? (ls.Contains("(runtime copy)") ? "AS AUTHORED (the game made a runtime copy to edit its values)" : "AS AUTHORED") : "CHANGED IN GAME (material swapped at runtime)" });
                }
            }
            if (fg.localPos != null)
            {
                var lp = go.transform.localPosition; var fpv = new Vector3(fg.localPos[0], fg.localPos[1], fg.localPos[2]);
                bool same = (lp - fpv).sqrMagnitude < 1e-6f;
                Add(r, "local position", V(fpv), V(lp), same, go, null, ChangeRecorder.PropKind.Position);
            }
            if (deep) Subtree(r, go, fg, lv);
            return r;
        }

        static string V(Vector3 v) { return v.x.ToString("0.###") + ", " + v.y.ToString("0.###") + ", " + v.z.ToString("0.###"); }

        // every object below: active state against the file (the pod-lamp question: is the on/off pattern authored?)
        static void Subtree(Result r, GameObject go, LevelFile.Go fg, LevelFile.Level lv)
        {
            var stack = new List<KeyValuePair<Transform, LevelFile.Go>> { new KeyValuePair<Transform, LevelFile.Go>(go.transform, fg) };
            while (stack.Count > 0)
            {
                var kv = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1);
                var t = kv.Key;
                var count = new Dictionary<string, int>();
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    int nth; count.TryGetValue(c.name, out nth); count[c.name] = ++nth;
                    LevelFile.Go fc; string path = kv.Value.path + "/" + c.name + (nth > 1 ? " #" + nth : "");
                    if (!lv.byPath.TryGetValue(path, out fc)) { if (r.subtree.Count < 30) r.subtree.Add("  + " + Rel(go, c) + "   not in the level file (created at runtime)"); continue; }
                    r.subtreeChecked++;
                    if (fc.active != c.gameObject.activeSelf)
                    {
                        r.subtreeDiffers++;
                        string y = Yours(c.gameObject, null, ChangeRecorder.PropKind.Active);
                        if (r.subtree.Count < 30) r.subtree.Add("  " + (c.gameObject.activeSelf ? "on " : "OFF") + "  " + Rel(go, c) + "   file: " + (fc.active ? "on" : "OFF") + "   " + (y ?? "CHANGED IN GAME"));
                    }
                    stack.Add(new KeyValuePair<Transform, LevelFile.Go>(c, fc));
                }
            }
        }

        static string Rel(GameObject root, Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null && x != root.transform; x = x.parent) parts.Add(x.name);
            parts.Reverse(); return string.Join("/", parts.ToArray());
        }

        public static string Report(GameObject go, bool deep)
        {
            var r = Compare(go, deep, true);
            var sb = new StringBuilder("level file vs live: " + Inspector.PathOf(go.transform) + "\n");
            if (r.error != null) return sb.Append("  ").Append(r.error).Append('\n').ToString();
            sb.Append("  file ").Append(r.levelFile).Append("  path ").Append(r.filePath).Append('\n');
            if (r.notInFile) return sb.Append("  not in the level file: created or renamed at runtime\n").ToString();
            foreach (var x in r.rows) sb.Append("  ").Append(x.what).Append(":  file ").Append(x.file).Append("   live ").Append(x.live).Append("   -> ").Append(x.origin).Append('\n');
            if (deep)
            {
                sb.Append("  below: ").Append(r.subtreeChecked).Append(" objects checked, ").Append(r.subtreeDiffers).Append(" on/off differ from the file\n");
                foreach (var l in r.subtree) sb.Append("  ").Append(l).Append('\n');
            }
            return sb.ToString();
        }
    }
}
