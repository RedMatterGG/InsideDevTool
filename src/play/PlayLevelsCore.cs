using System;
using System.Collections.Generic;
using System.Globalization;

namespace InsideDev.Play
{
    // PlayLevelsCore.cs (owner WORLD_A): the Unity-free half of PlayLevels, compiled into InsideDev.Play.dll and, as is,
    // into the offline tests (qa/mcp/WORLD_A). ReadyRule = the ready predicate of report 03 section 1.4 as a truth table;
    // LoadPolicy = which load modes need confirm / a backup (only with play.save_guard on) / a save lock and which
    // targets are valid; SpIndex =
    // SavepointManager.SavepointIndex ordering (FP/SavepointManager.cs:362-398); ChapterMap = ChapterUIMenuItem's chapter
    // lookup (FP/ChapterUIMenuItem.cs FindClosestChapterIndex); SubsceneNames = strict name -> index resolution; BodyPick =
    // which savepoint culler holds the body; PinBook = reference-counted stream pins; WorldAMembers = every private game
    // member WORLD_A reaches by reflection (checked offline by qa/mcp/WORLD_A/WorldAGameTest.cs).
    internal sealed class ReadyInputs
    {
        public bool hasGame = true, started = true, pendingLoad, loading, faderDone = true, title, cullersBusy, preawake;
        public bool inputEnabled = true, characterActive = true;
    }

    internal static class ReadyRule
    {
        public static readonly string[] Reasons = { "no_game", "not_started", "pending_load", "loading", "fading", "title", "streaming", "preawake", "input_disabled", "no_body" };

        public static bool Evaluate(ReadyInputs i, out string reason)
        {
            if (i == null || !i.hasGame) { reason = "no_game"; return false; }
            if (!i.started) { reason = "not_started"; return false; }
            if (i.pendingLoad) { reason = "pending_load"; return false; }
            if (i.loading) { reason = "loading"; return false; }
            if (!i.faderDone) { reason = "fading"; return false; }
            if (i.title) { reason = "title"; return false; }
            if (i.cullersBusy) { reason = "streaming"; return false; }
            if (i.preawake) { reason = "preawake"; return false; }
            if (!i.inputEnabled) { reason = "input_disabled"; return false; }
            if (!i.characterActive) { reason = "no_body"; return false; }
            reason = "";
            return true;
        }

        public static string Explain(string reason)
        {
            switch (reason)
            {
                case "no_game": return "the game is not started yet (no GameManager)";
                case "not_started": return "the game is still booting";
                case "pending_load": return "a load was requested and starts next frame";
                case "loading": return "a load is running";
                case "fading": return "the screen fade is running";
                case "title": return "the INSIDE title overlay is showing";
                case "streaming": return "subscenes are still streaming in";
                case "preawake": return "PreAwake of a loaded subscene is pending";
                case "input_disabled": return "game input is disabled (fade-in after a load)";
                case "no_body": return "no active main character";
                default: return reason ?? "";
            }
        }
    }

    internal struct SpIndex
    {
        public int sub, sp;
        public SpIndex(int sub, int sp) { this.sub = sub; this.sp = sp; }
        public static readonly SpIndex Unknown = new SpIndex(-1, -1);
        public static readonly SpIndex Start = new SpIndex(0, 0);
        public bool IsUnknown { get { return sub == -1 || sp == -1; } }

        public static int Compare(SpIndex a, SpIndex b)
        {
            if (a.sub < b.sub || (a.sub == b.sub && a.sp < b.sp)) return -1;
            if (a.sub > b.sub || (a.sub == b.sub && a.sp > b.sp)) return 1;
            return 0;
        }

        public override string ToString() { return sub.ToString(CultureInfo.InvariantCulture) + ":" + sp.ToString(CultureInfo.InvariantCulture); }
    }

    internal sealed class LoadDecision
    {
        public string error, message, hint;
        public bool backup, writes, unlocks, lockSave, discover, target, backwards;
        public SpIndex progression = SpIndex.Unknown;
        public bool Refused { get { return error != null; } }
    }

    internal static class LoadPolicy
    {
        public const string Chapter = "chapter", Respawn = "respawn", Quiet = "quiet", ReloadRespawn = "reload_respawn", ReloadChapter = "reload_chapter", NewGame = "newgame";
        public static readonly string[] Modes = { Chapter, Respawn, Quiet, ReloadRespawn, ReloadChapter, NewGame };

        // savepointCount: SavepointManager.GetSubsceneSavepointCount(sub) (-1 = not discovered yet); cheat = a checkpoint
        // goto / next / prev the user allowed with play.cheats (no confirm for the unlock, a warning instead); guard =
        // Saves.Guard (play.save_guard): off = no confirm and no backup for any mode
        public static LoadDecision Decide(string mode, SpIndex target, SpIndex unlocked, bool confirm, bool cheat, bool guard, int subsceneCount, int savepointCount)
        {
            return Decide(mode, target, unlocked, SpIndex.Unknown, confirm, cheat, guard, subsceneCount, savepointCount);
        }

        // progression: Savegame.GetCurrentProgression; a respawn before it moves the saved progression back
        // (Savegame.SetSpawnpoint sets it to the target), so with the guard on it needs confirm like an unlock (a cheat
        // only warns)
        public static LoadDecision Decide(string mode, SpIndex target, SpIndex unlocked, SpIndex progression, bool confirm, bool cheat, bool guard, int subsceneCount, int savepointCount)
        {
            var d = new LoadDecision();
            d.progression = progression;
            if (Array.IndexOf(Modes, mode) < 0) return Fail(d, "bad_args", "mode: unknown load mode " + (mode ?? "null"), "one of " + string.Join(", ", Modes));
            d.target = mode == Chapter || mode == Respawn || mode == Quiet;
            if (d.target)
            {
                if (target.sub < 0 || target.sub >= subsceneCount)
                    return Fail(d, "bad_args", "subscene " + target.sub + " is out of range (0.." + (subsceneCount - 1) + ")", "checkpoint {op:\"list\"} or level {op:\"list\"} lists them");
                if (target.sp < 0) return Fail(d, "bad_args", "savepoint " + target.sp + " is negative", null);
                if (savepointCount == 0) return Fail(d, "not_found", "subscene " + target.sub + " has no savepoints (secret or overlay area)", "pick a neighbouring subscene from level {op:\"list\"}");
                if (savepointCount > 0 && target.sp >= savepointCount)
                    return Fail(d, "bad_args", "savepoint " + target.sp + " is out of range: subscene " + target.sub + " has " + savepointCount, null);
                d.discover = savepointCount < 0;
                d.unlocks = SpIndex.Compare(target, unlocked) > 0;
                d.backwards = !progression.IsUnknown && SpIndex.Compare(target, progression) < 0;
            }
            switch (mode)
            {
                case Chapter:
                    d.writes = true; d.backup = guard;
                    if (guard && !confirm) return Fail(d, "confirm_required", "a chapter load writes INSIDE.sav (Steam Cloud), sets progression to " + target + (d.unlocks ? ", unlocks chapters forever" : "") + " and clears the recent orb bits", "repeat with confirm:true (the save is backed up first), or use mode quiet");
                    break;
                case Respawn:
                    d.writes = true; d.backup = guard;
                    if (guard && d.unlocks && !confirm && !cheat) return Fail(d, "confirm_required", "respawn at " + target + " writes INSIDE.sav and unlocks the chapter menu beyond " + unlocked + " forever", "repeat with confirm:true, or use mode quiet (no save write)");
                    if (guard && d.backwards && !confirm && !cheat) return Fail(d, "confirm_required", "respawn at " + target + " writes INSIDE.sav and moves the saved progression back from " + progression + " to " + target, "repeat with confirm:true (the save is backed up first), or use mode quiet (no save write)");
                    break;
                case Quiet:
                    d.lockSave = true; d.backup = guard; d.unlocks = false; d.backwards = false;
                    break;
                case ReloadRespawn:
                    break;
                case ReloadChapter:
                    d.writes = true; d.backup = guard;
                    if (guard && !confirm) return Fail(d, "confirm_required", "reload chapter writes INSIDE.sav and clears the recent orb bits", "repeat with confirm:true, or use kind respawn (no save write)");
                    break;
                case NewGame:
                    d.writes = true; d.backup = guard;
                    if (guard && !confirm) return Fail(d, "confirm_required", "a new game writes INSIDE.sav: spawn and progression go back to 0:0 (unlocked chapters stay)", "repeat with confirm:true");
                    break;
            }
            return d;
        }

        static LoadDecision Fail(LoadDecision d, string code, string message, string hint)
        {
            d.error = code; d.message = message; d.hint = hint;
            return d;
        }
    }

    internal static class ChapterMap
    {
        // FindClosestChapterIndex: the last chapter whose savepoint is at or before idx, stopping at the first one after
        public static int Closest(IList<SpIndex> chapters, SpIndex idx)
        {
            if (chapters == null || chapters.Count == 0) return -1;
            if (idx.sub == -1 && idx.sp == -1) return 0;
            int r = 0;
            for (int i = 0; i < chapters.Count && SpIndex.Compare(chapters[i], idx) <= 0; i++) r = i;
            return r;
        }

        public static bool Unlocked(IList<SpIndex> chapters, int chapter, SpIndex unlocked)
        {
            if (chapters == null || chapter < 0 || chapter >= chapters.Count) return false;
            return chapter <= Closest(chapters, unlocked);
        }
    }

    internal static class SubsceneNames
    {
        // strict: exact (case-insensitive) scene or puzzle name, else a unique substring; no nearest match
        public static int Resolve(IList<string> scenes, IList<string> puzzles, string query, out string error, out List<int> candidates)
        {
            error = null;
            candidates = new List<int>();
            if (string.IsNullOrEmpty(query)) { error = "empty name"; return -1; }
            string q = query.Trim().ToLowerInvariant();
            int n = Math.Max(scenes != null ? scenes.Count : 0, puzzles != null ? puzzles.Count : 0);
            for (int i = 0; i < n; i++)
                if (Eq(At(scenes, i), q) || Eq(At(puzzles, i), q) || Eq(Strip(At(scenes, i)), q)) candidates.Add(i);
            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count == 0)
                for (int i = 0; i < n; i++)
                    if (At(scenes, i).ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) >= 0 || At(puzzles, i).ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) >= 0) candidates.Add(i);
            if (candidates.Count == 1) return candidates[0];
            error = candidates.Count == 0 ? "no subscene matches '" + query + "'" : candidates.Count + " subscenes match '" + query + "'";
            return -1;
        }

        static string At(IList<string> l, int i) { return l != null && i < l.Count && l[i] != null ? l[i] : ""; }
        static bool Eq(string a, string q) { return a.Length > 0 && a.ToLowerInvariant() == q; }

        // "#forestReveal_Gameplay" -> "forestreveal"
        public static string Strip(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return "";
            string s = scene.TrimStart('#');
            int u = s.LastIndexOf('_');
            if (u > 0) s = s.Substring(0, u);
            return s;
        }
    }

    internal sealed class CullerCandidate
    {
        public int index;
        public bool contains, active;
        public double volume;
    }

    internal static class BodyPick
    {
        // the savepoint culler that holds the body: bounds contain it, active first, then the smallest bounds; the spawn
        // subscene when it is among the containing ones or nothing contains the body but the spawn culler is active
        public static int Pick(IList<CullerCandidate> cands, int spawnSub)
        {
            CullerCandidate best = null;
            bool spawnContains = false, spawnActive = false;
            if (cands != null)
                foreach (var c in cands)
                {
                    if (c.index == spawnSub) { spawnContains = c.contains; spawnActive = c.active; }
                    if (!c.contains) continue;
                    if (best == null || (c.active && !best.active) || (c.active == best.active && c.volume < best.volume)) best = c;
                }
            if (best == null) return spawnActive ? spawnSub : -1;
            if (spawnContains && spawnActive == best.active) return spawnSub;
            return best.index;
        }
    }

    internal sealed class PinBook
    {
        readonly Dictionary<int, List<string>> owners = new Dictionary<int, List<string>>();
        readonly Dictionary<int, bool> original = new Dictionary<int, bool>();

        // true when this is the first pin of the subscene (the caller then sets AlwaysLoaded and remembers orig)
        public bool Add(int sub, string owner, bool orig)
        {
            List<string> l;
            bool first = !owners.TryGetValue(sub, out l);
            if (first) { l = new List<string>(); owners[sub] = l; original[sub] = orig; }
            if (!l.Contains(owner ?? "")) l.Add(owner ?? "");
            return first;
        }

        // true when the last owner left (the caller then puts AlwaysLoaded back to Original)
        public bool Remove(int sub, string owner)
        {
            List<string> l;
            if (!owners.TryGetValue(sub, out l)) return false;
            l.Remove(owner ?? "");
            if (l.Count > 0) return false;
            owners.Remove(sub);
            return true;
        }

        public bool Original(int sub)
        {
            bool b;
            return original.TryGetValue(sub, out b) && b;
        }

        public void Drop(int sub) { owners.Remove(sub); original.Remove(sub); }
        public bool IsPinned(int sub) { return owners.ContainsKey(sub); }

        public List<string> Owners(int sub)
        {
            List<string> l;
            return owners.TryGetValue(sub, out l) ? new List<string>(l) : new List<string>();
        }

        public List<int> All()
        {
            var r = new List<int>(owners.Keys);
            r.Sort();
            return r;
        }
    }

    internal static class TextFilter
    {
        public static bool Match(string filter, params string[] fields)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            string f = filter.ToLowerInvariant();
            foreach (var s in fields) if (!string.IsNullOrEmpty(s) && s.ToLowerInvariant().IndexOf(f, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }

    // private game members reached by reflection (type, member, kind: f = instance field, sf = static field, m = instance
    // method, sm = static method, p = property, nt = nested type). The decompiled file:line is in the comment of the user.
    internal static class WorldAMembers
    {
        public const string LoadRoutine = "loadRoutine";
        public const string SavegameItem = "savegameDataMapItem";
        public const string Suspended = "suspended";
        public const string MessageList = "messages";
        public const string MessageType = "Message";
        public const string MessageSelected = "selected";
        public const string MessageText = "message";
        public const string MessageCancel = "cancel";
        public const string MessageAccept = "accept";
        public const string CloseMessage = "CloseMessage";
        public const string CreditsShow = "show";
        public const string StartState = "state";
        public const string StartTitleWait = "SetTitleWaitState";
        public const string ChapterSelected = "selectedIndex";
        public const string ConfirmSelected = "selected";
        public const string ConfirmReturn = "returnMenu";
        public const string SteamId = "steamID";
        public const string SteamIdValue = "m_SteamID";
        public const string WaitForSubscene = "WaitForSubscene";
        public const string SelectNextMenu = "nextMenu";

        public static readonly string[][] Table =
        {
            new[] { "GameManager, Assembly-CSharp-firstpass", LoadRoutine, "f" },
            new[] { "Savegame, Assembly-CSharp-firstpass", SavegameItem, "f" },
            new[] { "Savegame+SavegameDataMapItem, Assembly-CSharp-firstpass", "spawnSubscene", "f" },
            new[] { "Savegame+SavegameDataMapItem, Assembly-CSharp-firstpass", "spawnSavepoint", "f" },
            new[] { "UserStoragePlatform, Assembly-CSharp-firstpass", Suspended, "f" },
            new[] { "MessageManager, Assembly-CSharp-firstpass", MessageList, "f" },
            new[] { "MessageManager, Assembly-CSharp-firstpass", MessageType, "nt" },
            new[] { "MessageManager+Message, Assembly-CSharp-firstpass", MessageSelected, "f" },
            new[] { "MessageManager+Message, Assembly-CSharp-firstpass", MessageText, "f" },
            new[] { "MessageManager+Message, Assembly-CSharp-firstpass", MessageCancel, "f" },
            new[] { "MessageManager+Message, Assembly-CSharp-firstpass", MessageAccept, "f" },
            new[] { "MessageManager, Assembly-CSharp-firstpass", CloseMessage, "m" },
            new[] { "EndCredits, Assembly-CSharp", CreditsShow, "f" },
            new[] { "StartSequence, Assembly-CSharp-firstpass", StartState, "f" },
            new[] { "StartSequence, Assembly-CSharp-firstpass", StartTitleWait, "m" },
            new[] { "ChapterUIMenuItem, Assembly-CSharp-firstpass", ChapterSelected, "f" },
            new[] { "ConfirmMessageMenuItem, Assembly-CSharp-firstpass", ConfirmSelected, "f" },
            new[] { "ConfirmMessageMenuItem, Assembly-CSharp-firstpass", ConfirmReturn, "f" },
            new[] { "UserSteam, Assembly-CSharp-firstpass", SteamId, "f" },
            new[] { "SavepointManager+SavepointTask, Assembly-CSharp-firstpass", WaitForSubscene, "sm" },
            new[] { "SelectMenuItem, Assembly-CSharp-firstpass", SelectNextMenu, "f" },
        };
    }
}
