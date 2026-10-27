using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace InsideDev.Play
{
    // GuideCore.cs (owner GUIDE): the Unity-free part of the playbooks (Guide.cs). SKILL.md parsing (frontmatter, level
    // sections ## Hint / ## Steps / ## Solution, ### place sub-sections), the topic library loaded from <root>/<group>/<topic>/SKILL.md,
    // savepoint -> topic routing (frontmatter "subscenes: 0-4, 5:1" plus guide/map.tsv, which maps the runtime subscene names
    // to the indices the specs use) and inside://guide uri parsing. Compiled into InsideDev.Play.dll and into the offline
    // tests (qa/mcp/GUIDE, --pure).
    internal static class GuideLevels
    {
        public const string Hint = "hint", Steps = "steps", Solution = "solution";
        public static readonly string[] All = { Hint, Steps, Solution };

        public static int Rank(string level)
        {
            if (level == null) return -1;
            string l = level.Trim().ToLowerInvariant();
            if (l == Hint || l == "hints") return 0;
            if (l == Steps || l == "step") return 1;
            if (l == Solution || l == "solutions") return 2;
            return -1;
        }

        public static string Of(int rank) { return rank <= 0 ? Hint : rank == 1 ? Steps : Solution; }
    }

    internal sealed class GuideSpan
    {
        public int subFrom, subTo, spFrom = -1, spTo = -1;

        public bool Covers(int sub, int sp)
        {
            if (sub < subFrom || sub > subTo) return false;
            return spFrom < 0 || (sp >= spFrom && sp <= spTo);
        }

        public int Breadth { get { return (subTo - subFrom + 1) * (spFrom < 0 ? 100 : spTo - spFrom + 1); } }
    }

    internal sealed class GuideTopic
    {
        public string id = "", name = "", title = "", description = "", file = "", body = "";
        public int chapter = -1, order = 1000;
        public readonly List<string> related = new List<string>();
        public readonly List<GuideSpan> spans = new List<GuideSpan>();

        public bool Covers(int sub, int sp)
        {
            foreach (var s in spans) if (s.Covers(sub, sp)) return true;
            return false;
        }

        public int BreadthAt(int sub, int sp)
        {
            int best = int.MaxValue;
            foreach (var s in spans) if (s.Covers(sub, sp) && s.Breadth < best) best = s.Breadth;
            return best;
        }
    }

    internal sealed class GuideSection
    {
        public int depth;
        public string heading = "";
        public readonly StringBuilder text = new StringBuilder();
        public readonly List<GuideSection> children = new List<GuideSection>();
        public int levelRank = -1;
    }

    internal static class GuideText
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static bool SplitFrontmatter(string text, Dictionary<string, string> fm, out string body)
        {
            body = text ?? "";
            if (body.Length > 0 && body[0] == '﻿') body = body.Substring(1);
            string norm = body.Replace("\r\n", "\n");
            if (!norm.StartsWith("---\n")) { body = norm; return false; }
            int end = norm.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end < 0) { body = norm; return false; }
            string head = norm.Substring(4, end - 4);
            int after = norm.IndexOf('\n', end + 4);
            body = after < 0 ? "" : norm.Substring(after + 1);
            foreach (var raw in head.Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0 || line.TrimStart().StartsWith("#")) continue;
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                string k = line.Substring(0, c).Trim().ToLowerInvariant();
                string v = Unquote(line.Substring(c + 1).Trim());
                if (k.Length > 0) fm[k] = v;
            }
            return true;
        }

        public static string Unquote(string v)
        {
            if (v.Length >= 2 && ((v[0] == '"' && v[v.Length - 1] == '"') || (v[0] == '\'' && v[v.Length - 1] == '\'')))
                return v.Substring(1, v.Length - 2).Replace("\\\"", "\"");
            return v;
        }

        public static List<string> SplitList(string v)
        {
            var l = new List<string>();
            if (string.IsNullOrEmpty(v)) return l;
            string s = v.Trim();
            if (s.StartsWith("[") && s.EndsWith("]")) s = s.Substring(1, s.Length - 2);
            foreach (var p in s.Split(','))
            {
                string x = Unquote(p.Trim());
                if (x.Length > 0) l.Add(x);
            }
            return l;
        }

        // "0-5, 6:1, 7:0-2" -> spans; malformed items go to problems
        public static List<GuideSpan> ParseSpans(string spec, List<string> problems)
        {
            var l = new List<GuideSpan>();
            if (string.IsNullOrEmpty(spec)) return l;
            foreach (var part in spec.Split(','))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                var s = new GuideSpan();
                string subPart = p, spPart = null;
                int c = p.IndexOf(':');
                if (c >= 0) { subPart = p.Substring(0, c).Trim(); spPart = p.Substring(c + 1).Trim(); }
                int a, b;
                if (!Range(subPart, out a, out b)) { if (problems != null) problems.Add("bad subscene range '" + p + "'"); continue; }
                s.subFrom = a; s.subTo = b;
                if (spPart != null)
                {
                    if (!Range(spPart, out a, out b)) { if (problems != null) problems.Add("bad savepoint range '" + p + "'"); continue; }
                    s.spFrom = a; s.spTo = b;
                }
                l.Add(s);
            }
            return l;
        }

        static bool Range(string s, out int a, out int b)
        {
            a = b = -1;
            int d = s.IndexOf('-');
            if (d < 0) { if (!int.TryParse(s, NumberStyles.Integer, IC, out a) || a < 0) return false; b = a; return true; }
            if (!int.TryParse(s.Substring(0, d).Trim(), NumberStyles.Integer, IC, out a)) return false;
            if (!int.TryParse(s.Substring(d + 1).Trim(), NumberStyles.Integer, IC, out b)) return false;
            return a >= 0 && b >= a;
        }

        // body -> preamble (depth 0) + ## sections, each with its ### children; fenced code is never split
        public static GuideSection Parse(string body)
        {
            var root = new GuideSection { depth = 0 };
            GuideSection h2 = null, cur = root;
            bool fence = false;
            foreach (var raw in (body ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw;
                if (line.TrimStart().StartsWith("```")) fence = !fence;
                if (!fence && line.StartsWith("## ") && !line.StartsWith("### "))
                {
                    h2 = new GuideSection { depth = 2, heading = line.Substring(3).Trim() };
                    h2.levelRank = GuideLevels.Rank(h2.heading);
                    root.children.Add(h2);
                    cur = h2;
                    continue;
                }
                if (!fence && line.StartsWith("### ") && h2 != null)
                {
                    var h3 = new GuideSection { depth = 3, heading = line.Substring(4).Trim(), levelRank = h2.levelRank };
                    h2.children.Add(h3);
                    cur = h3;
                    continue;
                }
                cur.text.Append(line).Append('\n');
            }
            return root;
        }

        public static string Key(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }

        static bool HeadingMatches(string heading, string want)
        {
            string w = Key(want);
            if (w.Length == 0) return false;
            string h = Key(heading);
            if (h == w) return true;
            if (w.Length >= 3 && h.StartsWith(w)) return true;
            int open = heading.LastIndexOf('('), close = heading.LastIndexOf(')');
            if (open >= 0 && close > open && Key(heading.Substring(open + 1, close - open - 1)) == w) return true;
            int sp = heading.IndexOf(' ');
            return sp > 0 && Key(heading.Substring(0, sp)) == w;
        }

        static void Emit(StringBuilder sb, GuideSection s, bool withHeading)
        {
            if (withHeading) sb.Append(s.depth == 2 ? "## " : "### ").Append(s.heading).Append('\n');
            sb.Append(s.text);
            foreach (var c in s.children) Emit(sb, c, true);
        }

        // text of body at a spoiler rank (0 hint, 1 steps, 2 solution): the preamble, every ## section that is not a
        // level section, and the level sections up to rank. section != null: only the matching ## section or the
        // matching ### blocks (under their ## heading). found = false when the section exists nowhere; capped = it exists
        // only above rank.
        public static string ForLevel(string body, int rank, string section, out bool found, out bool capped, List<string> headings)
        {
            var root = Parse(body);
            var sb = new StringBuilder();
            found = true;
            capped = false;
            if (headings != null)
                foreach (var h2 in root.children)
                {
                    if (!headings.Contains(h2.heading)) headings.Add(h2.heading);
                    foreach (var h3 in h2.children) if (!headings.Contains(h3.heading)) headings.Add(h3.heading);
                }
            if (string.IsNullOrEmpty(section))
            {
                sb.Append(root.text);
                foreach (var h2 in root.children) if (h2.levelRank <= rank) Emit(sb, h2, true);
                return Tidy(sb.ToString());
            }
            bool any = false, above = false;
            foreach (var h2 in root.children)
            {
                if (!HeadingMatches(h2.heading, section)) continue;
                if (h2.levelRank > rank) { above = true; continue; }
                Emit(sb, h2, true);
                any = true;
            }
            if (!any)
                foreach (var h2 in root.children)
                {
                    bool headed = false;
                    foreach (var h3 in h2.children)
                    {
                        if (!HeadingMatches(h3.heading, section)) continue;
                        if (h2.levelRank > rank) { above = true; continue; }
                        if (!headed) { sb.Append("## ").Append(h2.heading).Append('\n'); headed = true; }
                        Emit(sb, h3, true);
                        any = true;
                    }
                }
            if (!any) { found = above; capped = above; }
            return Tidy(sb.ToString());
        }

        public static string Tidy(string s)
        {
            var sb = new StringBuilder();
            int blank = 0;
            foreach (var line in s.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Trim().Length == 0) { blank++; if (blank > 1) continue; }
                else blank = 0;
                sb.Append(line.TrimEnd()).Append('\n');
            }
            return sb.ToString().Trim('\n');
        }
    }

    internal sealed class GuideLibrary
    {
        public const string Scheme = "inside://guide/";
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public string root = "", indexTitle = "INSIDE guide", indexDescription = "", indexText = "";
        public readonly List<GuideTopic> topics = new List<GuideTopic>();
        public readonly List<string> problems = new List<string>();
        public readonly Dictionary<string, int> sceneIndex = new Dictionary<string, int>();
        public readonly List<string> sceneNames = new List<string>();
        public bool exists;

        public static GuideLibrary Load(string root)
        {
            var lib = new GuideLibrary { root = root ?? "" };
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { lib.problems.Add("guide folder not found: " + root); return lib; }
            lib.exists = true;
            string index = Path.Combine(root, "index.md");
            if (File.Exists(index))
            {
                try
                {
                    var fm = new Dictionary<string, string>();
                    string body;
                    GuideText.SplitFrontmatter(File.ReadAllText(index, Encoding.UTF8), fm, out body);
                    string v;
                    if (fm.TryGetValue("name", out v)) lib.indexTitle = v;
                    if (fm.TryGetValue("description", out v)) lib.indexDescription = v;
                    lib.indexText = GuideText.Tidy(body);
                }
                catch (Exception e) { lib.problems.Add("index.md: " + e.Message); }
            }
            string map = Path.Combine(root, "map.tsv");
            if (File.Exists(map))
            {
                try { lib.LoadMap(File.ReadAllLines(map, Encoding.UTF8)); }
                catch (Exception e) { lib.problems.Add("map.tsv: " + e.Message); }
            }
            string[] groups;
            try { groups = Directory.GetDirectories(root); } catch (Exception e) { lib.problems.Add(e.Message); return lib; }
            Array.Sort(groups, StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(g); } catch { continue; }
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                foreach (var d in dirs)
                {
                    string f = Path.Combine(d, "SKILL.md");
                    if (!File.Exists(f)) continue;
                    string id = (Path.GetFileName(g) + "/" + Path.GetFileName(d)).ToLowerInvariant();
                    try { lib.topics.Add(ParseTopic(id, f, File.ReadAllText(f, Encoding.UTF8), lib.problems)); }
                    catch (Exception e) { lib.problems.Add(id + ": " + e.Message); }
                }
            }
            lib.topics.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) : string.CompareOrdinal(a.id, b.id));
            return lib;
        }

        public static GuideTopic ParseTopic(string id, string file, string text, List<string> problems)
        {
            var t = new GuideTopic { id = id, file = file ?? "" };
            var fm = new Dictionary<string, string>();
            string body;
            if (!GuideText.SplitFrontmatter(text, fm, out body) && problems != null) problems.Add(id + ": no frontmatter");
            t.body = body;
            string v;
            t.name = fm.TryGetValue("name", out v) ? v : id;
            t.title = fm.TryGetValue("title", out v) ? v : t.name;
            t.description = fm.TryGetValue("description", out v) ? v : "";
            int n;
            if (fm.TryGetValue("chapter", out v) && int.TryParse(v, NumberStyles.Integer, IC, out n)) t.chapter = n;
            if (fm.TryGetValue("order", out v) && int.TryParse(v, NumberStyles.Integer, IC, out n)) t.order = n;
            if (fm.TryGetValue("related", out v)) foreach (var r in GuideText.SplitList(v)) t.related.Add(r.ToLowerInvariant());
            if (fm.TryGetValue("subscenes", out v)) t.spans.AddRange(GuideText.ParseSpans(v, problems));
            if (t.description.Length == 0 && problems != null) problems.Add(id + ": no description");
            return t;
        }

        // map.tsv: subscene <tab> scene name [<tab> ...]; '#' lines are comments
        public void LoadMap(IEnumerable<string> lines)
        {
            foreach (var raw in lines)
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var p = line.Split('\t');
                int i;
                if (p.Length < 2 || !int.TryParse(p[0].Trim(), NumberStyles.Integer, IC, out i) || i < 0) continue;
                while (sceneNames.Count <= i) sceneNames.Add("");
                sceneNames[i] = p[1].Trim();
                string k = SceneKey(p[1]);
                if (k.Length > 0 && !sceneIndex.ContainsKey(k)) sceneIndex[k] = i;
            }
        }

        // "#riverWithDog_Gameplay" -> "riverwithdog"
        public static string SceneKey(string scene)
        {
            string s = (scene ?? "").Trim().TrimStart('#').ToLowerInvariant();
            foreach (var suffix in new[] { "_gameplay", "_gamelogic", "_logic" })
                if (s.EndsWith(suffix)) { s = s.Substring(0, s.Length - suffix.Length); break; }
            return GuideText.Key(s);
        }

        public static string NormalizeId(string idOrUri)
        {
            string s = (idOrUri ?? "").Trim();
            int q = s.IndexOf('?');
            if (q >= 0) s = s.Substring(0, q);
            if (s.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) s = s.Substring(Scheme.Length);
            s = s.Replace('\\', '/').Trim('/');
            if (s.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 9);
            return s.ToLowerInvariant();
        }

        public GuideTopic Find(string idOrUri)
        {
            string id = NormalizeId(idOrUri);
            if (id.Length == 0) return null;
            foreach (var t in topics) if (t.id == id) return t;
            foreach (var t in topics) if (string.Equals(t.name, id, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        public List<string> Candidates(string partial, int max)
        {
            var l = new List<string>();
            string id = NormalizeId(partial);
            string k = GuideText.Key(id);
            int slash = id.IndexOf('/');
            string group = slash > 0 ? id.Substring(0, slash) : id;
            foreach (var t in topics)
            {
                string tk = GuideText.Key(t.id), tg = t.id.Substring(0, Math.Max(0, t.id.IndexOf('/'))), last = GuideText.Key(t.id.Substring(t.id.IndexOf('/') + 1));
                if (k.Length == 0 || tk.Contains(k) || k.Contains(last) || tg == group || GuideText.Key(t.title).Contains(k)) l.Add(t.id);
                if (l.Count >= max) break;
            }
            if (l.Count == 0) foreach (var t in topics) { l.Add(t.id); if (l.Count >= max) break; }
            return l;
        }

        // the most specific topic whose subscenes spec covers (sub, sp). runtimeScene (the culler name at that index in
        // the running game) re-maps sub through map.tsv when the game's numbering differs from the specs.
        public string TopicFor(int sub, int sp, string runtimeScene)
        {
            if (sub < 0) return null;
            int eff = sub;
            if (!string.IsNullOrEmpty(runtimeScene))
            {
                int j;
                if (sceneIndex.TryGetValue(SceneKey(runtimeScene), out j)) eff = j;
            }
            GuideTopic best = null;
            int bestBreadth = int.MaxValue;
            foreach (var t in topics)
            {
                int b = t.BreadthAt(eff, sp < 0 ? 0 : sp);
                if (b < bestBreadth) { best = t; bestBreadth = b; }
            }
            return best != null ? best.id : null;
        }

        // inside://guide/index | inside://guide/<group>/<topic>[?level=steps&section=x]
        public static bool ParseUri(string uri, out string topic, out string level, out string section)
        {
            topic = level = section = null;
            if (string.IsNullOrEmpty(uri) || !uri.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = uri.Substring(Scheme.Length);
            int q = rest.IndexOf('?');
            if (q >= 0)
            {
                foreach (var kv in rest.Substring(q + 1).Split('&'))
                {
                    int e = kv.IndexOf('=');
                    if (e <= 0) continue;
                    string k = kv.Substring(0, e).ToLowerInvariant(), v = Unescape(kv.Substring(e + 1));
                    if (k == "level") level = v;
                    else if (k == "section") section = v;
                }
                rest = rest.Substring(0, q);
            }
            rest = rest.Trim('/');
            if (rest.Length == 0) return false;
            topic = rest.ToLowerInvariant();
            return topic == "index" || topic.IndexOf('/') > 0;
        }

        static string Unescape(string s)
        {
            try { return Uri.UnescapeDataString(s.Replace('+', ' ')); } catch { return s; }
        }

        public string IndexMarkdown()
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(indexTitle).Append("\n\n");
            if (indexText.Length > 0) sb.Append(indexText).Append("\n\n");
            foreach (var t in topics) sb.Append("- ").Append(t.id).Append(": ").Append(t.description).Append('\n');
            if (!exists) sb.Append("\n(guide folder missing: ").Append(root).Append(")\n");
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        public string TopicMarkdown(GuideTopic t, int rank)
        {
            bool found, capped;
            string text = GuideText.ForLevel(t.body, rank, null, out found, out capped, null);
            return "# " + t.title + "\n\n" + text + "\n";
        }
    }
}
