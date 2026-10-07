using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Materials of the selected object's renderers, and the sibling that differs.
    // Many INSIDE props show state only through their material (nixie digits: one tube uses the lit
    // GLASS_Lit_ON_Switch_Baked, the rest _Basic_80_Baked), so "which one is different" is the useful question.
    public static class MaterialFacts
    {
        public static string Clean(Material m)
        {
            if (m == null) return "(none)";
            string n = m.name; if (n.EndsWith(" (Instance)")) n = n.Substring(0, n.Length - 11) + " (runtime copy)";
            return n;
        }

        public static string Signature(GameObject g)
        {
            var r = g.GetComponent<Renderer>(); if (r == null) return null;
            Material[] ms; try { ms = r.sharedMaterials; } catch { return null; }
            if (ms == null || ms.Length == 0) return "(none)";
            var parts = new string[ms.Length];
            for (int i = 0; i < ms.Length; i++) parts[i] = Clean(ms[i]);
            return string.Join(" + ", parts);
        }

        // this object's renderers, one line each
        public static List<string> Own(GameObject go)
        {
            var res = new List<string>();
            foreach (var r in go.GetComponents<Renderer>())
            {
                if (r == null) continue;
                Material[] ms; try { ms = r.sharedMaterials; } catch { continue; }
                for (int i = 0; i < (ms != null ? ms.Length : 0); i++)
                {
                    var m = ms[i];
                    var sb = new StringBuilder();
                    sb.Append(r.GetType().Name).Append(ms.Length > 1 ? " [" + i + "]" : "").Append(": ").Append(Clean(m));
                    if (m != null)
                    {
                        try { if (m.shader != null) sb.Append("   shader ").Append(m.shader.name); } catch { }
                        try { var t = m.mainTexture; if (t != null) sb.Append("   texture ").Append(t.name); } catch { }
                        try { if (m.HasProperty("_Color")) { var c = m.GetColor("_Color"); sb.Append("   color ").Append(Hex(c)); } } catch { }
                    }
                    if (!r.enabled) sb.Append("   [renderer off]");
                    res.Add(sb.ToString());
                }
            }
            if (go.GetComponent("MaterialInstance") != null) res.Add("MaterialInstance: the game edits this material's values at runtime (colours / glow can differ from the level file)");
            return res;
        }

        static string Hex(Color c) { return "#" + ((int)(c.r * 255)).ToString("X2") + ((int)(c.g * 255)).ToString("X2") + ((int)(c.b * 255)).ToString("X2") + (c.a < 0.999f ? ((int)(c.a * 255)).ToString("X2") : ""); }

        // the odd ones out among a parent's direct children that have renderers: groups by material; a group is
        // "odd" when it is smaller than the largest group and the parent has at least 3 rendered children
        public sealed class Odd { public Transform parent; public List<GameObject> odd = new List<GameObject>(); public string oddSig, majoritySig; public int majority, total; }

        public static Odd OddChildren(Transform parent)
        {
            if (parent == null || parent.childCount < 3) return null;
            var groups = new Dictionary<string, List<GameObject>>();
            int total = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i).gameObject; string s = Signature(c); if (s == null) continue;
                total++;
                List<GameObject> l; if (!groups.TryGetValue(s, out l)) groups[s] = l = new List<GameObject>(); l.Add(c);
            }
            if (total < 3 || groups.Count < 2) return null;
            string maj = null; int mc = 0;
            foreach (var kv in groups) if (kv.Value.Count > mc) { mc = kv.Value.Count; maj = kv.Key; }
            if (mc < 2) return null;
            var o = new Odd { parent = parent, majoritySig = maj, majority = mc, total = total };
            foreach (var kv in groups)
                if (kv.Key != maj && kv.Value.Count <= Math.Max(1, total / 3)) { o.odd.AddRange(kv.Value); o.oddSig = o.oddSig == null ? kv.Key : o.oddSig + " / " + kv.Key; }
            return o.odd.Count > 0 ? o : null;
        }

        // the selected object vs its siblings, plus odd children in its subtree (two levels down)
        public static List<string> Differences(GameObject go, List<GameObject> hits)
        {
            var res = new List<string>();
            var t = go.transform;
            if (t.parent != null)
            {
                var o = OddChildren(t.parent);
                if (o != null && o.odd.Contains(go))
                    res.Add("this one DIFFERS from its siblings: uses " + Signature(go) + "; " + o.majority + " of " + o.total + " siblings use " + o.majoritySig);
                else if (o != null && Signature(go) == o.majoritySig)
                    res.Add("same as most siblings (" + o.majority + " of " + o.total + "); differs: " + Names(o.odd) + " (" + o.oddSig + ")");
            }
            var stack = new List<KeyValuePair<Transform, int>> { new KeyValuePair<Transform, int>(t, 0) };
            while (stack.Count > 0 && res.Count < 24)
            {
                var kv = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1);
                var o = OddChildren(kv.Key);
                if (o != null)
                {
                    res.Add((kv.Key == t ? "children" : kv.Key.name + "/") + ": " + Names(o.odd) + " use " + o.oddSig + "  (the other " + o.majority + " use " + o.majoritySig + ")");
                    if (hits != null) hits.AddRange(o.odd);
                }
                if (kv.Value < 2) for (int i = kv.Key.childCount - 1; i >= 0; i--) stack.Add(new KeyValuePair<Transform, int>(kv.Key.GetChild(i), kv.Value + 1));
            }
            return res;
        }

        static string Names(List<GameObject> l)
        {
            var n = new List<string>(); foreach (var g in l) { if (n.Count >= 6) { n.Add("…"); break; } n.Add(g.name); }
            return string.Join(", ", n.ToArray());
        }

        public static string Report(GameObject go)
        {
            var sb = new StringBuilder("materials of " + Inspector.PathOf(go.transform) + "\n");
            var own = Own(go);
            if (own.Count == 0) sb.Append("  (no renderer on this object)\n");
            foreach (var l in own) sb.Append("  ").Append(l).Append('\n');
            foreach (var l in Differences(go, null)) sb.Append("  ").Append(l).Append('\n');
            return sb.ToString();
        }
    }
}
