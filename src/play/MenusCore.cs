using System;
using System.Collections.Generic;
using System.Globalization;

namespace InsideDev.Play
{
    // MenusCore.cs (owner WORLD_A): the Unity-free half of Menus, compiled into InsideDev.Play.dll and into the offline
    // tests (qa/mcp/WORLD_A). MenuKinds maps the game's UIMenuItem classes (FP/*MenuItem.cs) to item kinds and flags the
    // destructive ones; MenuPolicy decides which presses are refused (destructive without allow_destructive, Yes in the
    // quit confirm always, raising the master volume always, storage messages through ui message_answer only);
    // MenuIdTable hands out "m<n>" ids for one menu state; MenuScreen is the screen name when Observer is not built.
    internal static class MenuKinds
    {
        public const string Button = "button", Slider = "slider", Toggle = "toggle", Chapter = "chapter", Confirm = "confirm", Key = "key", Other = "other";

        public static string Kind(string typeName)
        {
            switch (typeName ?? "")
            {
                case "ResumeMenuItem": case "SelectMenuItem": case "SelectWithCancelMenuItem": case "BackMenuItem": case "QuitMenuItem":
                case "StartNewGameMenuItem": case "CustomControlsResetMenuItem": case "ShowStoreMenuItem": case "UpsellMenuItem":
                case "MenuSwitchProfile": case "MenuHelp": case "ControlsMenuItem": case "SavepointsMenuItem":
                    return Button;
                case "AudioVolumeMenuItem": case "BrightnessMenuItem": case "ResolutionMenuItem":
                    return Slider;
                case "ScreenModeMenuItem": case "RumbleMenuItem": case "RunInBackgroundMenuItem":
                    return Toggle;
                case "ChapterUIMenuItem": return Chapter;
                case "ConfirmMessageMenuItem": return Confirm;
                case "CustomizeControlsMenuItem": return Key;
                default: return Other;
            }
        }

        // the classes whose submit quits, resets, signs out, starts a new game or loads a chapter (writes INSIDE.sav)
        public static bool DestructiveType(string typeName)
        {
            switch (typeName ?? "")
            {
                case "QuitMenuItem": case "StartNewGameMenuItem": case "CustomControlsResetMenuItem": case "ChapterUIMenuItem":
                case "SavepointsMenuItem": case "MenuSwitchProfile":
                    return true;
                default: return false;
            }
        }

        // item = its class; goName / nextMenu = the GameObject name and, for Select*MenuItem, the target menu name; confirm
        // items take the class of the item that opened them (ConfirmMessageMenuItem.returnMenu)
        public static bool Destructive(string typeName, string goName, string nextMenu, string confirmReturnType)
        {
            if (DestructiveType(typeName)) return true;
            if (typeName == "ConfirmMessageMenuItem") return DestructiveType(confirmReturnType);
            if ((typeName == "SelectMenuItem" || typeName == "SelectWithCancelMenuItem") && (Has(goName, "savepoints") || Has(nextMenu, "savepoints"))) return true;
            return false;
        }

        static bool Has(string s, string part) { return !string.IsNullOrEmpty(s) && s.ToLowerInvariant().IndexOf(part, StringComparison.Ordinal) >= 0; }
    }

    internal static class MenuPolicy
    {
        // op: up, down, left, right, submit, back, select; null = allowed, else the refusal text
        public static string Check(string op, string typeName, bool destructive, bool allowDestructive, bool confirmYes, string confirmReturnType, out string hint)
        {
            hint = null;
            if (typeName == "ConfirmMessageMenuItem" && confirmReturnType == "QuitMenuItem" && op == "submit" && confirmYes)
            {
                hint = "back (No) closes the confirm; quitting is left to the player";
                return "quitting the game is never done through MCP";
            }
            if (typeName == "AudioVolumeMenuItem" && op == "right")
            {
                hint = "left lowers it; ui {op:\"settings\", volume:0} mutes";
                return "raising the master volume is refused (sound policy)";
            }
            if (!destructive || allowDestructive) return null;
            if (op == "submit" || op == "select")
            {
                hint = "repeat with allow_destructive:true if the user asked for it";
                if (typeName == "ConfirmMessageMenuItem") return confirmYes ? "the confirm answers Yes to " + (confirmReturnType ?? "a destructive item") : null;
                return "the selected item is destructive (" + typeName + ")";
            }
            return null;
        }

        public static string MessageRefusal(string op)
        {
            if (op == "state" || op == "close" || op == "open" || op == "message_answer") return null;
            return "a storage message is up (MessageManager): it reads the physical controller; answer it with message_answer (menu on the play profile, ui on dev), answer and confirm:true";
        }
    }

    internal sealed class MenuIdTable
    {
        readonly List<object> items = new List<object>();
        int generation;

        public int Generation { get { return generation; } }
        public int Count { get { return items.Count; } }

        // a new table: ids m1..mN in list order
        public void Reset(IList<object> objs)
        {
            items.Clear();
            if (objs != null) items.AddRange(objs);
            generation++;
        }

        public string IdOf(object o)
        {
            int i = items.IndexOf(o);
            return i < 0 ? null : "m" + (i + 1).ToString(CultureInfo.InvariantCulture);
        }

        // -1 = malformed, -2 = not in the current table
        public static int Parse(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length < 2 || (id[0] != 'm' && id[0] != 'M')) return -1;
            int n;
            if (!int.TryParse(id.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 1) return -1;
            return n - 1;
        }

        public object Resolve(string id, out string error)
        {
            error = null;
            int i = Parse(id);
            if (i == -1) { error = "malformed menu item id '" + (id ?? "") + "' (ids look like m3)"; return null; }
            if (i >= items.Count) { error = "menu item " + id + " is not in the current menu state"; return null; }
            return items[i];
        }
    }

    internal static class MenuScreen
    {
        public static string For(bool loading, bool title, bool message, bool menuOpen, bool mainGroup, bool credits)
        {
            if (message) return "message";
            if (menuOpen) return mainGroup ? "pause_menu" : "menu";
            if (loading) return "loading";
            if (title) return "title";
            if (credits) return "credits";
            return "in_world";
        }
    }
}
