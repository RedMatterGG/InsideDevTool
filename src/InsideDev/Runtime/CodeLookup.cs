using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace InsideDev
{
    // Game code that calls given Unity APIs on a script field (used by the animation adapter: who calls Play /
    // CrossFade / Stop on an Animation field), with the guarding condition and how that code runs (code graph).
    public static class CodeLookup
    {
        static readonly Regex achv = new Regex(@"IsAcquired\((\d+)\)");
        public static string Pretty(string guard)
        {
            if (guard == null) return null;
            return achv.Replace(guard, m =>
            {
                int n; if (!int.TryParse(m.Groups[1].Value, out n)) return m.Value;
                try { string nm = Enum.GetName(typeof(AchievementPlatform.EAchievements), n); if (nm != null) return "IsAcquired(" + nm + ")"; } catch { }
                return m.Value;
            });
        }

        static string Strip(string s)
        {
            var sb = new StringBuilder(s.Length); int depth = 0;
            foreach (char c in s) { if (c == '[') depth++; else if (c == ']') depth--; else if (depth == 0) sb.Append(c); }
            return sb.ToString();
        }

        public static readonly string[] AnimKeys = { "UnityEngine.Animation::Play", "UnityEngine.Animation::CrossFade", "UnityEngine.Animation::PlayQueued", "UnityEngine.Animation::CrossFadeQueued", "UnityEngine.Animation::Blend", "UnityEngine.Animation::Stop", "UnityEngine.Animation::Sample", "UnityEngine.Animator::Play", "UnityEngine.Animator::CrossFade", "UnityEngine.Animator::SetTrigger", "UnityEngine.Animator::SetBool", "UnityEngine.Animator::SetFloat", "UnityEngine.Animator::SetInteger" };

        // game code that calls one of `keys` (Type::member) on a field of t, with guard and how the code runs
        public static bool CodeFor(Type t, string field, List<string> lines, string src, string[] keys)
        {
            if (!CodeGraph.done) { lines.Add("    (code scan still running - try again in a moment)"); return false; }
            string fpath = Strip(field);
            string last = fpath.Contains(".") ? fpath.Substring(fpath.LastIndexOf('.') + 1) : fpath;
            var readers = new HashSet<CodeGraph.MInfo>();
            int dot = fpath.IndexOf('.'); string first = dot > 0 ? fpath.Substring(0, dot) : fpath;
            foreach (var u in CodeGraph.Uses(t, first)) readers.Add(u.Key);
            bool any = false; int n = 0;
            foreach (var key in keys)
            {
                List<KeyValuePair<CodeGraph.MInfo, CodeGraph.Effect>> l;
                if (!CodeGraph.uses.TryGetValue(key, out l)) continue;
                foreach (var kv in l)
                {
                    var mi = kv.Key; var ef = kv.Value; if (ef.recv == null) continue;
                    string rv = Strip(ef.recv);
                    bool own = mi.type.IsAssignableFrom(t) || t.IsAssignableFrom(mi.type);
                    bool match = own ? (rv == "this." + fpath || rv.EndsWith("." + fpath) || rv == fpath) : readers.Contains(mi) && rv.EndsWith("." + last);
                    if (!match) continue;
                    if (n++ >= 6) { lines.Add("    … more code"); return true; }
                    any = true;
                    string what = ef.member + "(" + (ef.args ?? "") + ")";
                    lines.Add("    [CODE] " + mi.Short + "  " + what + " on " + ef.recv + (ef.guard != null ? "   if " + Pretty(ef.guard) : "") + "   (" + src + ")");
                    var sb = new StringBuilder();
                    CodeGraph.Triggers(mi, sb, "          ", 2, new HashSet<CodeGraph.MInfo>());
                    foreach (var ln in sb.ToString().Split('\n')) if (ln.Trim().Length > 0) lines.Add(Pretty(ln));
                }
            }
            return any;
        }

    }
}
