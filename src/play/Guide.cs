using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // Guide.cs (owner GUIDE, GuideBook API, the guide schema and the resource / prompt names frozen; class name GuideBook
    // because InsideDev.Guide exists in the core): playbooks on demand (PLAN section 6, report 04 section 6). Files:
    // <_mod>/guide/<group>/<topic>/SKILL.md (source tree: the repository's guide/ folder), frontmatter name + description (+ title, chapter,
    // order, related, subscenes "3:0-2, 4"), level sections "## Hint", "## Steps", "## Solution" (other ## sections are
    // shown at every level, ### sub-sections are places); guide/index.md is the map, guide/map.tsv maps subscene indices to
    // scene names. Parsing and routing live in GuideCore.cs. Topic ids are the folder paths ("forest/overview"). TopicFor
    // maps a savepoint to its topic (Observer puts it into checkpoint.guide). The same files are served as resources
    // inside://guide/index, inside://guide/<group>/<topic> and the template inside://guide/{chapter}/{topic} (read-only,
    // ?level= and ?section= accepted). Prompts: playtest_from_checkpoint (subscene, savepoint, goal), solve_here (goal).
    // Root: env INSIDEDEV_GUIDE_DIR, else EditorState play.guide_dir, else <folder of InsideDev.Play.dll>/guide. EditorState
    // play.guide_max (hint | steps | solution, default solution) caps the spoiler level the AI can ask for. Register also
    // registers run_scenario (GuideScenario.cs).
    public static class GuideBook
    {
        public const string Owner = "GUIDE";
        public const string ResourceIndex = "inside://guide/index";
        public const string ResourceTemplate = "inside://guide/{chapter}/{topic}";
        public const string PromptPlaytest = "playtest_from_checkpoint", PromptSolve = "solve_here";
        public const string EnvRoot = "INSIDEDEV_GUIDE_DIR";
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly object gate = new object();
        static GuideLibrary lib;
        static string libRoot;
        static long libStamp;
        static int libCheckedAt;
        static volatile string[] sceneNames;
        static volatile string maxLevel = GuideLevels.Solution, dirSetting;
        static int ticks;

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("guide", "Game guide", PlayShared.Play, Owner,
                "Playbooks for INSIDE: without topic the index (chapters, mechanics, puzzles, known stuck spots); with topic one playbook at a spoiler level: hint (default), steps or solution. observe.checkpoint.guide names the topic for the current place; section picks one heading (a place such as riverWithDog, or Savepoints).",
                Schema.Obj().Str("topic", "Topic id from the index or observe.checkpoint.guide (e.g. forest/overview). Empty = index.")
                    .Enum("level", "How much to reveal.", "hint", "hint", "steps", "solution").Str("section", "Only this section heading of the topic.").Build(),
                Schema.Obj().Str("topic", "").Str("title", "").Enum("level", "", null, "hint", "steps", "solution").Str("text", "Markdown of the topic at that level.")
                    .Arr("related", "Related topic ids.", Schema.Str(), 0, 20)
                    .Arr("index", "Without topic: every topic.", Schema.OutOf(Schema.Obj().Str("topic", "").Str("title", "").Str("description", "").Int("chapter", "", -1, 99)), 0, 300)
                    .Str("current_topic", "Topic for the current place, when known.")
                    .Arr("sections", "Headings of the topic (for section).", Schema.Str(), 0, 200)
                    .Str("max_level", "Spoiler cap set by the user (EditorState play.guide_max), when it lowered level.")
                    .Arr("warnings", "", Schema.Str(), 0, 20).BuildOutput()).ReadOnly(), Handle);

            PlayRegistry.Resource(new ResourceDef { uri = ResourceIndex, name = "guide-index", title = "INSIDE guide index", description = "Chapters, mechanics and safety topics of the INSIDE playbooks with one-line descriptions.", profiles = McpProfiles.Both });
            var l = Library(false);
            foreach (var t in l.topics)
                PlayRegistry.Resource(new ResourceDef { uri = GuideLibrary.Scheme + t.id, name = t.id, title = t.title, description = t.description, profiles = McpProfiles.Both });
            PlayRegistry.Template(new ResourceTemplateDef { uriTemplate = ResourceTemplate, name = "guide-topic", title = "INSIDE guide topic",
                description = "One playbook topic (ids in inside://guide/index), hint level by default; append ?level=steps or ?level=solution for more, ?section=<heading> for one place.", profiles = McpProfiles.Both });
            PlayRegistry.Reader(Read);
            PlayRegistry.Prompt(PlaytestDef(), GetPlaytest);
            PlayRegistry.Prompt(SolveDef(), GetSolve);
            PlayRegistry.Instructions(McpProfiles.Play, 20, "Call guide() first; observe.checkpoint.guide names the topic for the current place (guide {topic, level: hint | steps | solution}; ask for more only when stuck).");
            PlayRegistry.OnTick("guide", Tick);
            try { ScenarioRunner.Register(); }
            catch (Exception e) { PlayRegistry.Problems.Add("ScenarioRunner: " + e.GetType().Name + ": " + e.Message); }
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            var book = Library(true);
            string topic = PlayShared.Str(a, "topic", "").Trim();
            string level = PlayShared.Str(a, "level", GuideLevels.Hint);
            string section = PlayShared.Str(a, "section", null);
            var warnings = new List<string>();
            var r = PlayShared.Env();
            string current = CurrentTopic(ctx);
            if (topic.Length == 0 || GuideLibrary.NormalizeId(topic) == "index")
            {
                r["topic"] = "index";
                r["title"] = book.indexTitle;
                r["text"] = book.indexText;
                var idx = new List<object>();
                foreach (var t in book.topics)
                {
                    var o = new Json.Obj();
                    o["topic"] = t.id;
                    o["description"] = t.description;
                    if (t.chapter >= 0) o["chapter"] = (double)t.chapter;
                    idx.Add(o);
                }
                r["index"] = idx;
                if (current != null) r["current_topic"] = current;
                if (!book.exists) warnings.Add("guide folder missing: " + book.root + " (copy the guide folder of the release to _mod/guide or set EditorState play.guide_dir)");
                foreach (var p in book.problems) if (warnings.Count < 20) warnings.Add(p);
                r["warnings"] = PlayShared.L(warnings);
                return ToolResult.Ok(r);
            }
            var tp = book.Find(topic);
            if (tp == null)
            {
                var e = new McpException(McpErr.NotFound, "no guide topic '" + topic + "'", "call guide() for the index, or use observe.checkpoint.guide");
                e.data = new Json.Obj();
                e.data["candidates"] = PlayShared.L(book.Candidates(topic, 8));
                throw e;
            }
            int rank = Math.Max(0, GuideLevels.Rank(level));
            int cap = GuideLevels.Rank(maxLevel);
            if (cap >= 0 && rank > cap)
            {
                rank = cap;
                r["max_level"] = GuideLevels.Of(cap);
                warnings.Add("the user capped spoilers at " + GuideLevels.Of(cap) + " (EditorState play.guide_max)");
            }
            var headings = new List<string>();
            bool found, capped;
            string text = GuideText.ForLevel(tp.body, rank, section, out found, out capped, headings);
            if (!found)
            {
                var e = new McpException(McpErr.NotFound, "topic " + tp.id + " has no section '" + section + "'", "pick one of sections, or leave section empty");
                e.data = new Json.Obj();
                e.data["sections"] = PlayShared.L(headings);
                throw e;
            }
            if (capped) { text = ""; warnings.Add("section '" + section + "' exists only at a higher level than " + GuideLevels.Of(rank)); }
            r["topic"] = tp.id;
            r["title"] = tp.title;
            r["level"] = GuideLevels.Of(rank);
            r["text"] = text;
            r["related"] = PlayShared.L(tp.related);
            if (current != null) r["current_topic"] = current;
            r["sections"] = PlayShared.L(headings);
            r["warnings"] = PlayShared.L(warnings);
            return ToolResult.Ok(r);
        }

        static string CurrentTopic(CallContext ctx)
        {
            try
            {
                var cur = ctx.Main(() => CurrentSavepoint(), 1500);
                return cur == null ? null : TopicFor(cur[0], cur[1]);
            }
            catch (Exception) { return null; }
        }

        // main thread: the spawn savepoint {subscene, savepoint} or null before the save system is up
        static int[] CurrentSavepoint()
        {
            if (PersistentBehaviour<Savegame>.instance == null) return null;
            int s, p;
            Savegame.GetCurrentSavepoint(out s, out p);
            return s < 0 ? null : new[] { s, p };
        }

        static ResourceContents Read(string uri, CallContext ctx)
        {
            string topic, level, section;
            if (!GuideLibrary.ParseUri(uri, out topic, out level, out section)) return null;
            var l = Library(true);
            if (topic == "index") return new ResourceContents { uri = uri, mimeType = "text/markdown", text = l.IndexMarkdown() };
            var t = l.Find(topic);
            if (t == null) return null;
            int rank = Math.Max(0, GuideLevels.Rank(level ?? GuideLevels.Hint));
            int cap = GuideLevels.Rank(maxLevel);
            if (cap >= 0 && rank > cap) rank = cap;
            bool found, capped;
            string text = GuideText.ForLevel(t.body, rank, section, out found, out capped, null);
            if (!found) text = "(no section '" + section + "' in " + t.id + ")";
            return new ResourceContents { uri = uri, mimeType = "text/markdown", text = "# " + t.title + " (" + GuideLevels.Of(rank) + ")\n\n" + text + "\n" };
        }

        static PromptDef PlaytestDef()
        {
            var d = new PromptDef { name = PromptPlaytest, title = "Play from a checkpoint", profiles = McpProfiles.Both,
                description = "Start a playthrough (or a playtest) of INSIDE from one checkpoint with a goal, using the guide and notes." };
            d.arguments.Add(new PromptArg { name = "subscene", description = "Savepoint subscene index (checkpoint list).", required = true });
            d.arguments.Add(new PromptArg { name = "savepoint", description = "Savepoint index in that subscene (default 0).", required = false });
            d.arguments.Add(new PromptArg { name = "goal", description = "What to reach or test (default: the next checkpoint).", required = false });
            return d;
        }

        static PromptDef SolveDef()
        {
            var d = new PromptDef { name = PromptSolve, title = "What does this do / solve here", profiles = McpProfiles.Both,
                description = "Look at the current place: what the nearby objects do and how to get past it, with as few spoilers as possible." };
            d.arguments.Add(new PromptArg { name = "goal", description = "What you want to find out or reach here.", required = false });
            return d;
        }

        static PromptResult GetPlaytest(Json.Obj a, CallContext ctx)
        {
            int sub = ArgInt(a, "subscene", -1), sp = ArgInt(a, "savepoint", 0);
            string goal = ArgStr(a, "goal", "reach the next checkpoint (wait_until {checkpoint_changed})");
            string topic = sub >= 0 ? TopicFor(sub, sp) : null;
            var l = Library(false);
            var t = topic != null ? l.Find(topic) : null;
            string where = "checkpoint subscene " + sub.ToString(IC) + ", savepoint " + sp.ToString(IC) + (t != null ? " (" + t.title + ")" : "");
            var pr = new PromptResult { description = "Play INSIDE from " + where };
            pr.messages.Add(new PromptMessage { role = "user", text =
                "Play INSIDE from " + where + ". Goal: " + goal + ".\n" +
                "1. notes {op:list}: moments I flagged (the screenshot and position show what I mean).\n" +
                "2. guide {topic:\"mechanics/controls\"} once, then guide {topic:\"" + (topic ?? "<observe.checkpoint.guide>") + "\", level:\"hint\"}" + (t != null ? ", section \"" + SceneOf(sub) + "\"" : "") + ". Ask for steps or solution only after 3 failed tries.\n" +
                "3. Load it: checkpoint {op:\"goto\", subscene:" + sub.ToString(IC) + ", savepoint:" + sp.ToString(IC) + "} (needs play.cheats; on the dev profile: level {op:\"load_checkpoint\", mode:\"respawn\"}). If loading is refused, tell me and play from where you are.\n" +
                "4. Loop: observe -> act / move_to / interact / wait_until -> read outcome, changes and observe. Use ids from the latest observe only. Deaths are fine: wait_until {alive:true}.\n" +
                "5. In music-timed places (warning music_timed) switch to time {op:\"realtime\"}.\n" +
                "6. When done or stuck for 10 calls, report what you tried and what happened, then call restore." });
            return pr;
        }

        static PromptResult GetSolve(Json.Obj a, CallContext ctx)
        {
            string goal = ArgStr(a, "goal", "get past this place");
            string topic = CurrentTopic(ctx);
            var pr = new PromptResult { description = "Explain the current place and how to " + goal };
            pr.messages.Add(new PromptMessage { role = "user", text =
                "I am stuck in INSIDE and want to " + goal + ".\n" +
                "1. observe {detail:\"full\"}; read notes {op:list} if I flagged this spot.\n" +
                "2. guide {topic:\"" + (topic ?? "<observe.checkpoint.guide>") + "\", level:\"hint\"}, section = the place name from observe.checkpoint.name when it matches a heading.\n" +
                "3. For each object in observe.objects and each hazard: say what it is and what it probably does (actions list, state). Try the cheapest experiment with interact or act and read changes (signals, fsm, toggled) to confirm.\n" +
                "4. Give me a hint first. Only if I ask, or after 3 failed tries, read level steps, then solution.\n" +
                "5. Mark anything you did not see happen in the game as a guess. Call restore when done." });
            return pr;
        }

        static string SceneOf(int sub)
        {
            var l = Library(false);
            var names = sceneNames;
            string n = names != null && sub >= 0 && sub < names.Length ? names[sub] : sub >= 0 && sub < l.sceneNames.Count ? l.sceneNames[sub] : "";
            n = (n ?? "").TrimStart('#');
            int u = n.LastIndexOf('_');
            return u > 0 ? n.Substring(0, u) : n;
        }

        static int ArgInt(Json.Obj a, string k, int def)
        {
            var v = a != null ? a[k] : null;
            if (v is double) return (int)(double)v;
            int i;
            var s = v as string;
            return s != null && int.TryParse(s.Trim(), NumberStyles.Integer, IC, out i) ? i : def;
        }

        static string ArgStr(Json.Obj a, string k, string def)
        {
            var s = a != null ? a[k] as string : null;
            return string.IsNullOrEmpty(s) ? def : s.Length > 300 ? s.Substring(0, 300) : s;
        }

        // main thread, every frame: once a second read the EditorState settings and the runtime subscene names
        static void Tick()
        {
            if (++ticks % 60 != 1) return;
            try
            {
                string m = EditorState.Get("play.guide_max", GuideLevels.Solution);
                maxLevel = GuideLevels.Rank(m) >= 0 ? GuideLevels.Of(GuideLevels.Rank(m)) : GuideLevels.Solution;
                string d = EditorState.Get("play.guide_dir", (string)null);
                dirSetting = string.IsNullOrEmpty(d) ? null : d;
            }
            catch (Exception) { }
            try { RefreshNames(); } catch (Exception) { }
        }

        static void RefreshNames()
        {
            if (PersistentBehaviour<SavepointManager>.instance == null) return;
            int n = SavepointManager.SubsceneCount;
            var cur = sceneNames;
            if (n <= 0 || (cur != null && cur.Length == n)) return;
            var a = new string[n];
            for (int i = 0; i < n; i++) a[i] = SavepointManager.GetSubsceneName(i);
            sceneNames = a;
        }

        internal static GuideLibrary Library(bool checkStale)
        {
            lock (gate)
            {
                string root = ResolveRoot();
                bool reload = lib == null || libRoot != root;
                if (!reload && checkStale && unchecked(Environment.TickCount - libCheckedAt) > 3000)
                {
                    libCheckedAt = Environment.TickCount;
                    reload = Stamp(root) != libStamp;
                }
                if (reload)
                {
                    lib = GuideLibrary.Load(root);
                    libRoot = root;
                    libStamp = Stamp(root);
                    libCheckedAt = Environment.TickCount;
                }
                return lib;
            }
        }

        static long Stamp(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return -1;
                long s = 0;
                foreach (var f in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    if (!f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase)) continue;
                    s = unchecked(s * 31 + File.GetLastWriteTimeUtc(f).Ticks + f.Length);
                }
                return s;
            }
            catch (Exception) { return -2; }
        }

        static string ResolveRoot()
        {
            string env = null;
            try { env = Environment.GetEnvironmentVariable(EnvRoot); } catch (Exception) { }
            if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);
            var d = dirSetting;
            if (!string.IsNullOrEmpty(d)) return d;
            string mod = ModDir();
            return Path.Combine(mod ?? ".", "guide");
        }

        // any thread: the folder InsideDev.Play.dll was loaded from (the game's _mod), or null
        internal static string ModDir()
        {
            try
            {
                string loc = typeof(GuideBook).Assembly.Location;
                return string.IsNullOrEmpty(loc) ? null : Path.GetDirectoryName(loc);
            }
            catch (Exception) { return null; }
        }

        // folder that holds the guide (default <_mod>/guide)
        public static string Root { get { return ResolveRoot(); } }

        // any thread: topic id for a savepoint, or null
        public static string TopicFor(int subscene, int savepoint)
        {
            if (subscene < 0) return null;
            var names = sceneNames;
            string name = names != null && subscene < names.Length ? names[subscene] : null;
            return Library(false).TopicFor(subscene, savepoint, name);
        }

        public static bool Exists(string topic) { return !string.IsNullOrEmpty(topic) && Library(false).Find(topic) != null; }
    }
}
