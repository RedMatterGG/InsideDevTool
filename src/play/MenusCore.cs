using System;
using System.Collections.Generic;
using System.Globalization;

namespace InsideDev.Play
{
    // MenusCore.cs (owner WORLD_A): the Unity-free half of Menus, compiled into InsideDev.Play.dll and into the offline
    // tests (qa/mcp/WORLD_A). MenuKinds maps the game's UIMenuItem classes (FP/*MenuItem.cs) to item kinds and flags the
    // destructive ones; MenuPolicy decides which presses are refused (destructive without allow_destructive, Yes in the
    // quit confirm always, raising the master volume always, storage messages through ui message_answer only);
    // MenuIdTable hands out "m<n>" ids: an item keeps its number for as long as the game keeps the item, a number is
    // never given to another item, and an id resolves only while its item is in the current menu state (else stale);
    // MenuScreen is the screen name when Observer is not built.
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
        const int MaxKnown = 4096;
        // every item that ever got an id: known[i] has the number first + i + 1
        readonly List<object> known = new List<object>();
        // the items of the current menu state, in list order
        readonly List<object> items = new List<object>();
        int generation, first;

        public int Generation { get { return generation; } }
        public int Count { get { return items.Count; } }

        static int IndexOf(List<object> l, object o)
        {
            for (int i = 0; i < l.Count; i++) if (ReferenceEquals(l[i], o)) return i;
            return -1;
        }

        // a new menu state: its items keep the ids they had, new items get the next numbers (the first state is m1..mN
        // in list order)
        public void Reset(IList<object> objs)
        {
            items.Clear();
            if (objs != null) items.AddRange(objs);
            generation++;
            // numbers are never reused: when the list of known items is dropped the numbering goes on behind it
            if (known.Count > MaxKnown) { first += known.Count; known.Clear(); }
            foreach (var o in items) if (o != null && IndexOf(known, o) < 0) known.Add(o);
        }

        public string IdOf(object o)
        {
            if (o == null || IndexOf(items, o) < 0) return null;
            int i = IndexOf(known, o);
            return i < 0 ? null : "m" + (first + i + 1).ToString(CultureInfo.InvariantCulture);
        }

        // -1 = malformed, else the number - 1
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
            int n = Parse(id);
            if (n == -1) { error = "malformed menu item id '" + (id ?? "") + "' (ids look like m3)"; return null; }
            int i = n - first;
            if (i < 0 || i >= known.Count) { error = "menu item " + id + " is not in the current menu state"; return null; }
            var o = known[i];
            if (IndexOf(items, o) < 0) { error = "menu item " + id + " is not in the current menu state (it belongs to an earlier one)"; return null; }
            return o;
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
