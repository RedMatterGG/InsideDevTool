using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace InsideDev.Play
{
    // SavesCore.cs (owner WORLD_A): the Unity-free half of Saves, compiled into InsideDev.Play.dll and into the offline
    // tests (qa/mcp/WORLD_A). OrbBits = the 15 local achievement bits (AchievementPlatform.EAchievements, 14 orbs + bit 14
    // PowerSecret, FP/AchievementPlatform.cs:5-21) per layer; BackupNames = _mod/save_backups/INSIDE_<yyyyMMdd_HHmmss>.sav;
    // SavePaths = where Steam keeps the cloud file that StorageSteam reads and writes through SteamRemoteStorage
    // ("INSIDE.sav", FP/StorageSteam.cs:18,91-104,136-141): <Steam>/userdata/<account id>/304430/remote/INSIDE.sav
    // (account id = low 32 bits of the SteamID64, app id 304430 = UserSteam.INSIDE_APPID, FP/UserSteam.cs:10);
    // SavePolicy = which save / ending / settings ops need confirm and which are refused (save-file confirms only with
    // play.save_guard on).
    internal static class OrbBits
    {
        public const int Count = 15, PowerSecret = 14, WristBit = 13;

        public static List<object> ToList(uint mask)
        {
            var l = new List<object>();
            for (int i = 0; i < Count; i++) if ((mask & (1u << i)) != 0) l.Add((double)i);
            return l;
        }

        public static uint Apply(uint mask, IList<int> bits, bool set)
        {
            foreach (var b in bits)
            {
                if (b < 0 || b >= Count) continue;
                if (set) mask |= 1u << b; else mask &= ~(1u << b);
            }
            return mask;
        }

        public static string Validate(IList<int> bits)
        {
            if (bits == null || bits.Count == 0) return "bits: give at least one bit (0..14)";
            foreach (var b in bits) if (b < 0 || b >= Count) return "bits: " + b + " is outside 0..14";
            return null;
        }
    }

    internal static class BackupNames
    {
        public const string Folder = "save_backups";

        public static string FileName(DateTime local)
        {
            return "INSIDE_" + local.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".sav";
        }

        // a free path in dir for this time ("_2", "_3" ... when the second is taken)
        public static string Unique(string dir, DateTime local, Func<string, bool> exists)
        {
            string name = FileName(local);
            string p = Path.Combine(dir, name);
            for (int i = 2; exists(p) && i < 1000; i++) p = Path.Combine(dir, name.Substring(0, name.Length - 4) + "_" + i.ToString(CultureInfo.InvariantCulture) + ".sav");
            return p;
        }
    }

    internal static class SavePaths
    {
        public const string FileName = "INSIDE.sav";
        public const uint AppId = 304430u;

        public static uint AccountId(ulong steamId64) { return (uint)(steamId64 & 0xFFFFFFFFUL); }

        public static string Normalize(string steamRoot)
        {
            if (string.IsNullOrEmpty(steamRoot)) return null;
            string s = steamRoot.Trim().Trim('"').Replace('\\', '/');
            while (s.EndsWith("/")) s = s.Substring(0, s.Length - 1);
            return s.Length == 0 ? null : s;
        }

        public static string RemoteFile(string steamRoot, uint accountId, uint appId)
        {
            string r = Normalize(steamRoot);
            if (r == null || accountId == 0) return null;
            return r + "/userdata/" + accountId.ToString(CultureInfo.InvariantCulture) + "/" + appId.ToString(CultureInfo.InvariantCulture) + "/remote/" + FileName;
        }

        // <...>/steamapps/common/<game>/<game>_Data -> the Steam root, or null when the game is not inside a library
        public static string RootFromDataPath(string dataPath)
        {
            string d = Normalize(dataPath);
            if (d == null) return null;
            int i = d.ToLowerInvariant().LastIndexOf("/steamapps/common/", StringComparison.Ordinal);
            return i > 0 ? d.Substring(0, i) : null;
        }

        // the files to try, best first: the account's own file under each known Steam root
        public static List<string> Candidates(IList<string> roots, uint accountId, uint appId)
        {
            var l = new List<string>();
            if (roots == null) return l;
            foreach (var r in roots)
            {
                string p = RemoteFile(r, accountId, appId);
                if (p != null && !l.Contains(p)) l.Add(p);
            }
            return l;
        }
    }

    internal static class SavePolicy
    {
        // ending kinds: credits fires EndCredits.show (no save write); secret replays FinalSecretFade's local effect
        // (orb bits cleared, new game) without its User.Achievement.Acquire Steam call; restart = StartNewGame (writes 0:0).
        // guard = Saves.Guard (play.save_guard): secret always needs confirm (orb bits), restart only with the guard on
        public static string EndingError(string kind, bool confirm, bool guard, out string code, out string hint)
        {
            code = null; hint = null;
            if (kind != "credits" && kind != "secret" && kind != "restart") { code = "bad_args"; hint = "kind: credits, secret or restart"; return "kind: give credits, secret or restart"; }
            if (kind == "credits") return null;
            if (!confirm && (guard || kind == "secret"))
            {
                code = "confirm_required";
                hint = guard ? "repeat with confirm:true (the save is backed up first)" : "repeat with confirm:true";
                return kind == "secret" ? "ending secret clears every orb bit (Achievements and Rencently) and starts a new game; INSIDE.sav is written" : "ending restart starts a new game: spawn and progression go back to 0:0 in INSIDE.sav";
            }
            return null;
        }

        public static string OrbError(string action, IList<int> bits, bool confirm, out string code, out string hint)
        {
            code = null; hint = null;
            if (action == "read") return null;
            if (action != "set" && action != "clear") { code = "bad_args"; return "action: read, set or clear"; }
            string v = OrbBits.Validate(bits);
            if (v != null) { code = "bad_args"; return v; }
            if (!confirm)
            {
                code = "confirm_required";
                hint = "repeat with confirm:true; set bits reach Steam achievements at the next game start (AchievementSteam pushes every local bit on sign-in)";
                return "orb edits change INSIDE.sav";
            }
            return null;
        }

        // master volume: only 0 (mute) is accepted, nothing raises the game volume
        public static string VolumeError(double volume)
        {
            return volume > 0 ? "volume: only 0 (mute) is accepted through MCP; nothing raises the game volume" : null;
        }

        // guard = Saves.Guard (play.save_guard): brightness / rumble need confirm only with the guard on
        public static string SettingsError(bool brightness, bool rumble, bool confirm, bool guard, out string code)
        {
            code = null;
            if ((brightness || rumble) && !confirm && guard) { code = "confirm_required"; return (brightness ? "brightness" : "rumble") + " is stored in INSIDE.sav: repeat with confirm:true"; }
            return null;
        }
    }
}
