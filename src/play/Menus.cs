using System;
using System.Collections.Generic;
using System.Reflection;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // Menus.cs (owner WORLD_A, public API and the menu / ui schemas frozen): the pause menu (the game's main menu) and
    // UI switches without a mouse (report 03 section 3). Navigation = ExecuteEvents on
    // EventSystem.current.currentSelectedGameObject (submitHandler, cancelHandler, an INavigationHandler function with
    // MenuInputModule.NavigationEventData, MenuExecuteEvents.toggleGameMenuHandler), open / close = MenuManager.Open /
    // CloseAll. Item ids are "m<n>", valid until the next menu state change. Destructive items (Quit and its confirm,
    // Reset To Defaults, a chapter load over progress, Savepoints cheat) are hidden and refused without
    // allow_destructive. Menus run on unscaled time, so they work while the game is frozen.
    // More rules: Yes in the quit confirm is refused even with allow_destructive (no tool quits the game); raising the
    // master volume is refused (sound policy); with EditorState play.save_guard on (Saves.Guard) a chapter submit, the
    // Savepoints picker and brightness / rumble changes back the save up first (Saves.EnsureBackup) and message_answer /
    // brightness / rumble need confirm. The title "press any button" (StartSequence TitleMenu / InputWait
    // reads the real controller) is answered by menu submit through StartSequence.SetTitleWaitState; storage messages
    // (MessageManager reads the real controller) only through message_answer (menu on the play profile,
    // ui on dev; hints name the one the caller has). UGUI calls go through
    // MenusUi (reflection; UnityEngine.UI.dll is not referenced by the build).
    // Game code checked: MenuManager isActive / HiddenMenu / MainMenu / reserveMenu / Open / CloseAll / CanOpen rules
    // (FP/MenuManager.cs:48-60, 210-275); MenuGroup IsVisible / child items (FP/MenuGroup.cs:48-56, 186-197);
    // UIMenuItem / PDSelectable (FP/UIMenuItem.cs, FP/PDSelectable.cs); SelectMenuItem.nextMenu (FP/SelectMenuItem.cs);
    // QuitMenuItem / CustomControlsResetMenuItem / StartNewGameMenuItem confirms (their .cs); ConfirmMessageMenuItem
    // private selected / returnMenu, public messageText / yesText / noText, left = No, right = Yes (FP/ConfirmMessageMenuItem.cs);
    // ChapterUIMenuItem private selectedIndex, submit = GameManager.LoadSavepoint (FP/ChapterUIMenuItem.cs:417-425);
    // AudioVolumeMenuItem right raises GameSystem.Settings.volume (FP/AudioVolumeMenuItem.cs); BrightnessMenuItem writes
    // User.Brightness, RumbleMenuItem User.Rumble (their .cs, FP/User.cs:20-47); SavepointsMenuItem (FP/SavepointsMenuItem.cs:
    // 227-233, 376-440); MessageManager AnyActive / private messages / Message selected / CloseMessage (FP/MessageManager.cs:
    // 9-36, 68, 132, 190-198); UIMessage Cancel / Ok (FP/UIMessage.cs); MessageOverlay.Show (FP/MessageOverlay.cs:24);
    // TitleManager HideAll / IsShowing / IsPressStartShowing (FP/TitleManager.cs:340-372); StartSequence private state /
    // SetTitleWaitState (FP/StartSequence.cs:5-12, 55, 133-140); GameEventManager IsTitleMenu / Layers (FP/
    // GameEventManager.cs:32, 135); GameSystem.Settings.volume (FP/GameSystem.cs:23, FP/SystemSettingsPlatform.cs:50);
    // ScreenSettings IsFullscreen / SetFullscreen / SetResolution / GetResolutionName / GetCurrentResolutionIndex /
    // resolutionCount (FP/ScreenSettings.cs:167-226).
    public static class Menus
    {
        public const string Owner = "WORLD_A";

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("menu", "Menus", PlayShared.Play, Owner,
                "Read and drive the pause menu without a mouse: state lists the selected group's items with ids; open / close; up, down, left, right, submit, back (repeat n times); select jumps to an item id. Chapter loads, Quit and Reset To Defaults are hidden unless allow_destructive; quitting is never done. At the title (the boy waits for a button) submit starts the game. message_answer answers a storage message (answer; confirm:true when play.save_guard is on). Works while the game is frozen.",
                Schema.Obj().Enum("op", "state, open, close, up, down, left, right, submit, back, select or message_answer.", "state",
                        "state", "open", "close", "up", "down", "left", "right", "submit", "back", "select", "message_answer")
                    .Str("id", "op select: item id from state (m3).").Int("repeat", "Navigation presses.", 1, 1, 20)
                    .Bool("allow_destructive", "Show and allow Quit, chapter loads over progress, Reset To Defaults.", false)
                    .Enum("answer", "op message_answer: ok (CONTINUE) or cancel (RETRY).", null, "ok", "cancel")
                    .Bool("confirm", "op message_answer: required with play.save_guard on (CONTINUE can overwrite or drop the save).", false).Build(),
                Schema.Obj().Enum("outcome", "", null, "done", "refused", "not_open", "loading").Bool("open", "A menu is open.")
                    .Enum("screen", "", null, Observer.Screens).Str("group", "Visible menu group.").Str("selected", "Selected item id.")
                    .Arr("items", "Items of the visible group.", Schema.OutOf(Schema.Obj().Str("id", "m<n>").Str("label", "").Enum("kind", "", null,
                        "button", "slider", "toggle", "chapter", "confirm", "key", "other").Bool("enabled", "").Bool("selected", "").Bool("destructive", "").Str("value", "")), 0, 80)
                    .Int("hidden_destructive", "Items hidden by allow_destructive false.", 0, 100)
                    .OutObj("chapter", "Chapter menu selection.", Schema.Obj().Int("index", "", 0, 99).Str("name", "").Bool("unlocked", ""))
                    .Arr("warnings", "", Schema.Str(), 0, 20).BuildOutput()), HandleMenu);

            PlayRegistry.Add(PlayShared.Def("ui", "UI switches", PlayShared.Dev, Owner,
                "Game UI switches: menu_lock (MenuManager.reserveMenu: Esc does nothing), title_hide (TitleManager.HideAll), overlay_text (MessageOverlay.Show), settings (volume 0 only = mute, brightness, rumble, fullscreen, resolution; brightness / rumble write INSIDE.sav), message_answer (storage error prompt), savepoint_menu (Playdead's dev savepoint picker, INFERRED; it reads the physical controller).",
                Schema.Obj().Enum("op", "menu_lock, title_hide, overlay_text, settings, message_answer or savepoint_menu.", null,
                        "menu_lock", "title_hide", "overlay_text", "settings", "message_answer", "savepoint_menu")
                    .Bool("on", "op menu_lock / title_hide.").StrMax("text", "op overlay_text.", 200).Num("seconds", "op overlay_text.", 3, 0.5, 30)
                    .Num("volume", "op settings: master volume 0..1.", 0, 1).Num("brightness", "op settings: 0..1.", 0, 1).Bool("rumble", "op settings.")
                    .Bool("fullscreen", "op settings.").Int("resolution", "op settings: index into the resolution list.", 0, 64)
                    .Enum("answer", "op message_answer.", null, "ok", "cancel").Bool("confirm", "Only with play.save_guard on: required for message_answer and settings that write the save.", false)
                    .Required("op").Build(),
                Schema.Obj().Enum("outcome", "", null, "done", "refused")
                    .OutObj("state", "UI state.", Schema.Obj().Bool("menu_locked", "").Bool("title_hidden", "")
                        .OutObj("settings", "", Schema.Obj().Num("volume", "", 0, 1).Num("brightness", "", 0, 1).Bool("rumble", "").Bool("fullscreen", "").Int("resolution", "", -1, 64))
                        .OutObj("message", "", Schema.Obj().Bool("shown", "").Str("text", "")))
                    .Arr("warnings", "", Schema.Str(), 0, 20).BuildOutput()).Destructive(), HandleUi);
        }

        // ---------------------------------------------------------------- menu
        static readonly MenuIdTable ids = new MenuIdTable();
        static readonly object idGate = new object();

        sealed class PressPlan
        {
            public string status;
            public bool backup, title;
            public GameObject target;
        }

        static ToolResult HandleMenu(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "state");
            bool allow = PlayShared.Bool(a, "allow_destructive", false);
            int repeat = Math.Max(1, PlayShared.Int(a, "repeat", 1));
            var w = new List<string>();
            if (op == "state") return ToolResult.Ok(ctx.Main(() => State("done", allow, w)));
            if (op == "message_answer")
            {
                AnswerMessage(PlayShared.Str(a, "answer", null), PlayShared.Bool(a, "confirm", false), ctx, w);
                return ToolResult.Ok(ctx.Main(() => State("done", allow, w)));
            }
            string msgHint = MessageHint(ctx.Profile);
            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return busy ?? ToolResult.Fail(McpErr.Busy, "the control lane is busy", "wait for the running action, or call restore");
            if (op == "open")
            {
                string outcome = ctx.Main(() => OpenMenu(w, msgHint));
                if (outcome == "done") ctx.WaitFrames(2, 2000);
                return ToolResult.Ok(ctx.Main(() => State(outcome, allow, w)));
            }
            if (op == "close")
            {
                ctx.MainDo(() => { if (MenuOpen()) MenusUi.CloseAll(); else w.Add("close: no menu was open"); });
                ctx.WaitFrames(2, 2000);
                return ToolResult.Ok(ctx.Main(() => State("done", allow, w)));
            }
            string id = PlayShared.Str(a, "id", null);
            if (op == "select")
            {
                if (string.IsNullOrEmpty(id)) throw new McpException(McpErr.BadArgs, "op select needs id (from menu state)");
                repeat = 1;
            }
            else if (op != "up" && op != "down" && op != "left" && op != "right" && op != "submit" && op != "back")
                throw new McpException(McpErr.BadArgs, "op: unknown " + op);
            string result = "done";
            for (int i = 0; i < repeat; i++)
            {
                var plan = ctx.Main(() => Plan(op, id, allow, w, msgHint));
                if (plan.status != null) { result = plan.status; break; }
                if (plan.backup && Saves.Guard) Saves.EnsureBackup(ctx);
                string after = ctx.Main(() => Press(op, plan, allow, w));
                if (after != null) { result = after; break; }
                ctx.WaitFrames(i < repeat - 1 ? 2 : 1, 2000);
            }
            return ToolResult.Ok(ctx.Main(() => State(result, allow, w)));
        }

        // the message_answer call of the caller's profile
        static string MessageHint(string profile)
        {
            return (profile == McpProfile.Dev ? "ui" : "menu") + " {op:\"message_answer\", answer:\"cancel\", confirm:true}";
        }

        // worker thread: answer the storage message (MessageManager) the way its button would
        static void AnswerMessage(string answer, bool confirm, CallContext ctx, List<string> w)
        {
            if (answer != "ok" && answer != "cancel") throw new McpException(McpErr.BadArgs, "op message_answer needs answer ok or cancel");
            if (!confirm && Saves.Guard) throw new McpException(McpErr.ConfirmRequired, "answering a storage message can overwrite or drop the save (CONTINUE overwrites it, RETRY retries)", "read the message first (observe screen message, ui / menu state); repeat with confirm:true");
            bool has = ctx.Main(() => MessageActive());
            if (!has) throw new McpException(McpErr.NotFound, "no storage message is up");
            if (Saves.Guard) Saves.EnsureBackup(ctx);
            ctx.MainDo(() =>
            {
                Json.Obj j;
                var m = LastMessage(out j);
                if (m == null) throw new McpException(McpErr.NotFound, "no storage message is up");
                bool twoButtons = j.ContainsKey("cancel");
                var choice = answer == "ok" || !twoButtons ? UIMessage.Ok : UIMessage.Cancel;
                if (answer == "cancel" && !twoButtons) w.Add("answer: the message has one button, answered ok");
                var f = m.GetType().GetField(WorldAMembers.MessageSelected);
                var close = typeof(MessageManager).GetMethod(WorldAMembers.CloseMessage, BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null || close == null) throw new McpException(McpErr.GameError, "MessageManager message members not found by reflection");
                f.SetValue(m, choice);
                close.Invoke(SingletonBehaviour<MessageManager>.instance, new[] { m });
                w.Add("answered: '" + j.Str("text", "") + "' with " + (choice == UIMessage.Ok ? j.Str("accept", "ok") : j.Str("cancel", "cancel")));
            });
        }

        static string OpenMenu(List<string> w, string msgHint)
        {
            if (PlayLevels.Loading) { w.Add("open: a load is running; the menu opens once it is playable"); return "loading"; }
            if (MessageActive()) { w.Add("open: a storage message is up; " + msgHint + " answers it"); return "refused"; }
            if (MenuOpen()) { w.Add("open: the menu was already open"); return "done"; }
            string why = null;
            if (MenuManager.reserveMenu) why = "menu_lock is on (ui {op:\"menu_lock\", on:false})";
            else if (!GameManager.GameIsStarted()) why = "the game is still booting";
            else if (TitleLayer()) why = "the title overlay is showing";
            else if (!GameInput.IsEnabled()) why = "game input is disabled (fade after a load)";
            if (why != null) { w.Add("open: " + why); return "refused"; }
            MenusUi.Open();
            MenusUi.Pump();
            if (!MenuOpen()) { w.Add("open: MenuManager.Open did not open the menu"); return "refused"; }
            return "done";
        }

        // main thread: what this press needs (status = outcome to stop with)
        static PressPlan Plan(string op, string id, bool allow, List<string> w, string msgHint)
        {
            var p = new PressPlan();
            if (PlayLevels.Loading) { p.status = "loading"; w.Add(op + ": a load is running"); return p; }
            if (MessageActive())
            {
                string r = MenuPolicy.MessageRefusal(op);
                if (r != null) throw new McpException(McpErr.Refused, r, msgHint);
            }
            if (!MenuOpen())
            {
                if (op == "submit" && TitleWaiting() != null) { p.title = true; return p; }
                p.status = "not_open";
                w.Add(op + ": no menu is open; menu {op:\"open\"} opens the pause menu");
                return p;
            }
            GameObject target;
            if (op == "select")
            {
                string err;
                object o;
                lock (idGate) o = ids.Resolve(id, out err);
                var c = o as Component;
                if (err != null) throw new McpException(McpErr.StaleId, err, "call menu {op:\"state\"} and use its ids");
                if (c == null || !c.gameObject.activeInHierarchy) throw new McpException(McpErr.StaleId, "menu item " + id + " is gone or hidden now", "call menu {op:\"state\"}");
                target = c.gameObject;
            }
            else target = MenusUi.Selected();
            var item = ItemOn(target);
            var info = Describe(item);
            string hint;
            string refuse = MenuPolicy.Check(op == "back" ? "back" : op, info.type, info.destructive, allow, info.confirmYes, info.confirmReturn, out hint);
            if (refuse != null) throw new McpException(McpErr.Refused, refuse, hint);
            p.target = target;
            if (info.type == "ChapterUIMenuItem" && op == "submit") p.backup = true;
            if (info.type == "SavepointsMenuItem" && (op == "select" || op == "submit")) p.backup = true;
            if ((info.type == "BrightnessMenuItem" || info.type == "RumbleMenuItem") && (op == "left" || op == "right" || op == "submit")) p.backup = true;
            if (info.type == "SelectMenuItem" && info.destructive && op == "submit") p.backup = true;
            return p;
        }

        // main thread: one press; returns an outcome to stop with, or null to continue
        static string Press(string op, PressPlan plan, bool allow, List<string> w)
        {
            if (plan.title)
            {
                var ss = TitleWaiting();
                if (ss == null) { w.Add("submit: the title stopped waiting"); return "not_open"; }
                var m = typeof(StartSequence).GetMethod(WorldAMembers.StartTitleWait, BindingFlags.NonPublic | BindingFlags.Instance);
                if (m == null) throw new McpException(McpErr.GameError, "StartSequence.SetTitleWaitState not found");
                m.Invoke(ss, null);
                w.Add("title: the intro starts (StartSequence.SetTitleWaitState, as if a button was pressed)");
                return "done";
            }
            if (op == "select")
            {
                MenusUi.Select(plan.target);
                MenusUi.Pump();
                return null;
            }
            var sel = MenusUi.Selected();
            if (sel != plan.target) { w.Add(op + ": the selection changed meanwhile, pressed nothing"); return "done"; }
            var info = Describe(ItemOn(sel));
            string which = op == "back" ? "cancel" : op;
            MenusUi.Send(sel, which);
            MenusUi.Pump();
            if (info.type == "ChapterUIMenuItem" && op == "submit")
            {
                w.Add("chapter: the chapter load started; it writes INSIDE.sav" + (Saves.BackupPath != null ? " (backup " + Saves.BackupPath + ")" : "") + "; wait_until {ready:true}");
                return "loading";
            }
            if ((info.type == "BrightnessMenuItem" || info.type == "RumbleMenuItem") && op != "up" && op != "down") w.Add("settings: " + (info.type == "BrightnessMenuItem" ? "brightness" : "rumble") + " is stored in INSIDE.sav");
            if (info.type == "SavepointsMenuItem") w.Add("savepoints: Playdead's picker reads the physical controller; a pick loads with GameManager.LoadSavepoint (writes INSIDE.sav)");
            return null;
        }

        sealed class ItemInfo
        {
            public string type = "", kind = MenuKinds.Other, label = "", value = "", confirmReturn;
            public bool destructive, confirmYes;
        }

        static Component ItemOn(GameObject go)
        {
            if (go == null) return null;
            foreach (var c in go.GetComponents(typeof(MonoBehaviour)))
                if (c != null && typeof(UIMenuItem).IsInstanceOfType(c)) return c;
            return null;
        }

        static ItemInfo Describe(Component c)
        {
            var i = new ItemInfo();
            if (c == null) return i;
            i.type = c.GetType().Name;
            i.kind = MenuKinds.Kind(i.type);
            string next = null;
            if (i.type == "SelectMenuItem" || i.type == "SelectWithCancelMenuItem")
            {
                var g = MenusUi.Field(c, WorldAMembers.SelectNextMenu) as GameObject;
                if (g != null) next = g.name;
            }
            if (i.type == "ConfirmMessageMenuItem")
            {
                var ret = MenusUi.Field(c, WorldAMembers.ConfirmReturn);
                i.confirmReturn = ret != null ? ret.GetType().Name : null;
                var sel = MenusUi.Field(c, WorldAMembers.ConfirmSelected);
                i.confirmYes = sel is bool && (bool)sel;
            }
            i.destructive = MenuKinds.Destructive(i.type, c.name, next, i.confirmReturn);
            i.label = Label(c, i.type);
            i.value = Value(c, i);
            return i;
        }

        static string Label(Component c, string type)
        {
            if (type == "ConfirmMessageMenuItem")
            {
                var t = MenusUi.TextValue(MenusUi.Field(c, "messageText"));
                if (!string.IsNullOrEmpty(t)) return t;
            }
            var s = MenusUi.TextOf(c, 0);
            return string.IsNullOrEmpty(s) ? c.name : s.Trim();
        }

        static string Value(Component c, ItemInfo i)
        {
            try
            {
                switch (i.type)
                {
                    case "AudioVolumeMenuItem": return PlayShared.F(GameSystem.Settings.volume.value);
                    case "BrightnessMenuItem": return PlayShared.F(User.Brightness);
                    case "RumbleMenuItem": return User.Rumble ? "on" : "off";
                    case "ScreenModeMenuItem": return Screen.fullScreen ? "fullscreen" : "windowed";
                    case "ResolutionMenuItem":
                        int ri = ScreenSettings.GetCurrentResolutionIndex();
                        return ri >= 0 ? ScreenSettings.GetResolutionName(ri) : Screen.width + "x" + Screen.height;
                    case "CustomizeControlsMenuItem": return MenusUi.TextOf(c, 1) ?? "";
                    case "ConfirmMessageMenuItem":
                        return (i.confirmYes ? "yes" : "no") + " (" + (MenusUi.TextValue(MenusUi.Field(c, "noText")) ?? "No") + " / " + (MenusUi.TextValue(MenusUi.Field(c, "yesText")) ?? "Yes") + ")";
                    case "ChapterUIMenuItem":
                        var idx = MenusUi.Field(c, WorldAMembers.ChapterSelected);
                        return idx is int ? ChapterName((int)idx) : "";
                }
            }
            catch { }
            return "";
        }

        static string ChapterName(int i)
        {
            try
            {
                var sd = SingletonBehaviour<SavepointData>.instance;
                if (sd != null && i >= 0 && i < sd.scenes.Count) return sd.scenes[i].name ?? "";
            }
            catch { }
            return "";
        }

        // main thread: the menu result
        static Json.Obj State(string outcome, bool allow, List<string> warnings)
        {
            var o = PlayShared.Env();
            o["outcome"] = outcome;
            bool msg = MessageActive(), open = MenuOpen();
            o["open"] = open || msg;
            var sel = open ? MenusUi.Selected() : null;
            Component group = null;
            if (sel != null)
            {
                group = sel.GetComponent(typeof(MenuGroup));
                if (group == null) group = sel.GetComponentInParent(typeof(MenuGroup));
            }
            o["screen"] = ScreenName(open, msg, group);
            o["group"] = group != null ? group.name : msg ? "message" : "";
            var list = new List<object>();
            var comps = new List<object>();
            int hidden = 0;
            string selectedId = "";
            Component selectedItem = ItemOn(sel);
            if (group != null)
            {
                foreach (var c in group.GetComponentsInChildren(typeof(MonoBehaviour), false))
                {
                    if (c == null || !typeof(UIMenuItem).IsInstanceOfType(c) || !c.gameObject.activeInHierarchy) continue;
                    if (c.GetComponentInParent(typeof(MenuGroup)) != group) continue;
                    comps.Add(c);
                }
            }
            lock (idGate) ids.Reset(comps);
            Json.Obj chapter = null;
            for (int k = 0; k < comps.Count; k++)
            {
                var c = (Component)comps[k];
                var info = Describe(c);
                string id = "m" + (k + 1);
                bool isSel = c == selectedItem;
                if (isSel) selectedId = id;
                if (info.kind == MenuKinds.Chapter && isSel) chapter = ChapterJson(c);
                if (info.destructive && !allow)
                {
                    hidden++;
                    if (isSel) { selectedId = ""; PlayLevels.AddWarnings(o, new List<string> { "selected: a destructive item (" + info.type + ") is selected; it is hidden without allow_destructive" }); }
                    continue;
                }
                if (list.Count >= 80) continue;
                var r = new Json.Obj();
                r["id"] = id;
                r["label"] = info.label;
                r["kind"] = info.kind;
                var b = c as Behaviour;
                r["enabled"] = b == null || b.enabled;
                r["selected"] = isSel;
                r["destructive"] = info.destructive;
                r["value"] = info.value;
                list.Add(r);
            }
            o["selected"] = selectedId;
            o["items"] = list;
            o["hidden_destructive"] = (double)hidden;
            if (chapter != null) o["chapter"] = chapter;
            if (group != null && !MenusUi.GroupVisible(group)) PlayLevels.AddWarnings(o, new List<string> { "fading: the group is still blending in (menus run on unscaled time)" });
            if (msg) o["message"] = MessageJson();
            if (!open && !msg && TitleWaiting() != null) PlayLevels.AddWarnings(o, new List<string> { "title: the boy waits for a button; menu {op:\"submit\"} starts the game" });
            if (!MenusUi.Ok) PlayLevels.AddWarnings(o, new List<string> { "menus: " + MenusUi.Problem });
            PlayLevels.AddWarnings(o, warnings);
            return o;
        }

        static Json.Obj ChapterJson(Component c)
        {
            var idx = MenusUi.Field(c, WorldAMembers.ChapterSelected);
            int i = idx is int ? (int)idx : -1;
            if (i < 0) return null;
            var o = new Json.Obj();
            o["index"] = (double)i;
            o["name"] = ChapterName(i);
            var chapters = new List<SpIndex>();
            try { var sd = SingletonBehaviour<SavepointData>.instance; if (sd != null) foreach (var s in sd.scenes) chapters.Add(new SpIndex(s.subsceneIndex, s.savepointIndex)); } catch { }
            o["unlocked"] = ChapterMap.Unlocked(chapters, i, PlayLevels.Unlocked());
            return o;
        }

        static string ScreenName(bool open, bool msg, Component group)
        {
            try { var s = Observer.Screen(); if (!string.IsNullOrEmpty(s)) return s; } catch { }
            bool main = group != null && MenuManager.MainMenu != null && group.gameObject == MenuManager.MainMenu;
            bool credits = false;
            try { var co = SingletonBehaviour<CreditsOverlay>.instance; credits = co != null && co.creditsGroup != null && co.creditsGroup.alpha > 0f; } catch { }
            bool title = false;
            try { title = TitleManager.IsShowing() || TitleManager.IsPressStartShowing(); } catch { }
            return MenuScreen.For(PlayLevels.Loading, title, msg, open, main, credits);
        }

        static bool TitleLayer()
        {
            try { return PersistentBehaviour<GameEventManager>.instance != null && GameEventManager.IsTitleMenu(); } catch { return false; }
        }

        // the StartSequence that waits for "press any button", or null
        static StartSequence TitleWaiting()
        {
            try
            {
                var ss = UnityEngine.Object.FindObjectOfType(typeof(StartSequence)) as StartSequence;
                if (ss == null) return null;
                var f = typeof(StartSequence).GetField(WorldAMembers.StartState, BindingFlags.NonPublic | BindingFlags.Instance);
                var v = f != null ? f.GetValue(ss) : null;
                string s = v != null ? v.ToString() : "";
                return s == "TitleMenu" || s == "InputWait" ? ss : null;
            }
            catch { return null; }
        }

        static bool MenuOpen()
        {
            try { return MenuManager.HiddenMenu != null && MenusUi.Current() != null && MenuManager.isActive; } catch { return false; }
        }

        static bool MessageActive()
        {
            try { return SingletonBehaviour<MessageManager>.instance != null && MessageManager.AnyActive(); } catch { return false; }
        }

        // the newest storage message: {text, cancel, accept, selected}
        static object LastMessage(out Json.Obj json)
        {
            json = null;
            var mm = SingletonBehaviour<MessageManager>.instance;
            if (mm == null) return null;
            var list = MenusUi.Field(mm, WorldAMembers.MessageList) as System.Collections.IList;
            if (list == null || list.Count == 0) return null;
            var m = list[list.Count - 1];
            json = new Json.Obj();
            json["text"] = MenusUi.Field(m, WorldAMembers.MessageText) as string ?? "";
            var cancel = MenusUi.Field(m, WorldAMembers.MessageCancel) as string;
            if (cancel != null) json["cancel"] = cancel;
            json["accept"] = MenusUi.Field(m, WorldAMembers.MessageAccept) as string ?? "";
            var sel = MenusUi.Field(m, WorldAMembers.MessageSelected);
            json["selected"] = sel != null ? sel.ToString().ToLowerInvariant() : "";
            return m;
        }

        static Json.Obj MessageJson()
        {
            Json.Obj j;
            LastMessage(out j);
            return j ?? new Json.Obj();
        }

        // ---------------------------------------------------------------- public API
        // main thread: a menu (or the storage message) is open
        public static bool IsOpen() { return MenuOpen() || MessageActive(); }

        // main thread: {open, group, selected} for observe.menu
        public static Json.Obj Summary()
        {
            var o = new Json.Obj();
            bool msg = MessageActive(), open = MenuOpen();
            o["open"] = open || msg;
            string group = msg ? "message" : "", selected = "";
            if (open)
            {
                var sel = MenusUi.Selected();
                if (sel != null)
                {
                    var g = sel.GetComponent(typeof(MenuGroup));
                    if (g == null) g = sel.GetComponentInParent(typeof(MenuGroup));
                    if (g != null) group = g.name;
                    var item = ItemOn(sel);
                    if (item != null)
                    {
                        string id;
                        lock (idGate) id = ids.IdOf(item);
                        var info = Describe(item);
                        selected = id ?? info.label;
                        if (info.destructive) o["selected_destructive"] = true;
                    }
                }
            }
            o["group"] = group;
            o["selected"] = selected;
            return o;
        }

        // ---------------------------------------------------------------- ui
        static ToolResult HandleUi(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", null);
            bool confirm = PlayShared.Bool(a, "confirm", false);
            var w = new List<string>();
            switch (op)
            {
                case "menu_lock":
                {
                    if (!PlayShared.Has(a, "on")) throw new McpException(McpErr.BadArgs, "op menu_lock needs on (true / false)");
                    bool on = PlayShared.Bool(a, "on", true);
                    return ToolResult.Ok(ctx.Main(() =>
                    {
                        bool orig = MenuManager.reserveMenu;
                        MenuManager.reserveMenu = on;
                        if (on) PlayLevels.Reg("menu_lock", "pause menu lock (MenuManager.reserveMenu)", () => { MenuManager.reserveMenu = orig; return "reserveMenu back to " + (orig ? "on" : "off"); });
                        else PlayLevels.Forget("menu_lock");
                        w.Add(on ? "menu_lock: Esc / Start do not open the pause menu now" : "menu_lock: off");
                        return UiResult("done", w);
                    }));
                }
                case "title_hide":
                {
                    if (!PlayShared.Has(a, "on")) throw new McpException(McpErr.BadArgs, "op title_hide needs on (true / false)");
                    bool on = PlayShared.Bool(a, "on", true);
                    return ToolResult.Ok(ctx.Main(() =>
                    {
                        if (on)
                        {
                            TitleManager.HideAll();
                            titleHidden = true;
                            PlayLevels.Reg("title_hide", "title overlay hidden (TitleManager.HideAll)", () => { titleHidden = false; return "the title overlay stays hidden; it shows again at the next startup load"; });
                            if (TitleWaiting() != null) w.Add("title: hidden, but the boy still waits for a button: menu {op:\"submit\"} starts the game");
                        }
                        else
                        {
                            titleHidden = false;
                            PlayLevels.Forget("title_hide");
                            w.Add("title_hide off: nothing to show back; the title shows again at the next startup load");
                        }
                        return UiResult("done", w);
                    }));
                }
                case "overlay_text":
                {
                    string text = PlayShared.Str(a, "text", null);
                    if (text == null) throw new McpException(McpErr.BadArgs, "op overlay_text needs text");
                    float secs = (float)PlayShared.Num(a, "seconds", 3);
                    return ToolResult.Ok(ctx.Main(() =>
                    {
                        if (SingletonBehaviour<MessageOverlay>.instance == null) throw new McpException(McpErr.NotFound, "MessageOverlay is not in the scene yet");
                        MessageOverlay.Show(text, secs);
                        PlayLevels.Reg("overlay", "on-screen text (MessageOverlay)", () => { if (SingletonBehaviour<MessageOverlay>.instance != null) MessageOverlay.Show("", 0f); return "overlay text cleared"; });
                        return UiResult("done", w);
                    }));
                }
                case "settings":
                    return Settings(a, ctx, confirm, w);
                case "message_answer":
                {
                    AnswerMessage(PlayShared.Str(a, "answer", null), confirm, ctx, w);
                    return ToolResult.Ok(ctx.Main(() => UiResult("done", w)));
                }
                case "savepoint_menu":
                {
                    ctx.Main(() =>
                    {
                        if (PlayLevels.Loading) throw new McpException(McpErr.Busy, "busy: a load is in progress", "wait_until {ready:true}");
                        if (!GameManager.GameIsStarted()) throw new McpException(McpErr.NotReady, "the game is still booting");
                        if (SavepointsMenu() == null) throw new McpException(McpErr.NotFound, "the Savepoints Menu item is not in the menu tree");
                        return true;
                    });
                    if (Saves.Guard) Saves.EnsureBackup(ctx);
                    return ToolResult.Ok(ctx.Main(() =>
                    {
                        var go = SavepointsMenu();
                        if (go == null) throw new McpException(McpErr.NotFound, "the Savepoints Menu item is gone");
                        MenusUi.Select(go);
                        MenusUi.Pump();
                        w.Add("savepoint_menu: Playdead's picker is selected (INFERRED: it draws with IMGUI); it reads the physical controller; a pick loads with GameManager.LoadSavepoint and writes INSIDE.sav; menu {op:\"close\"} leaves it");
                        return UiResult(MenuOpen() ? "done" : "refused", w);
                    }));
                }
            }
            throw new McpException(McpErr.BadArgs, "op: give menu_lock, title_hide, overlay_text, settings, message_answer or savepoint_menu");
        }

        static bool titleHidden;

        static GameObject SavepointsMenu()
        {
            var root = MenuManager.HiddenMenu;
            if (root == null) return null;
            var all = root.GetComponentsInChildren(typeof(SavepointsMenuItem), true);
            return all != null && all.Length > 0 ? all[0].gameObject : null;
        }

        static ToolResult Settings(Json.Obj a, CallContext ctx, bool confirm, List<string> w)
        {
            bool hasVol = PlayShared.Has(a, "volume"), hasBri = PlayShared.Has(a, "brightness"), hasRum = PlayShared.Has(a, "rumble");
            bool hasFs = PlayShared.Has(a, "fullscreen"), hasRes = PlayShared.Has(a, "resolution");
            double vol = PlayShared.Num(a, "volume", 0), bri = PlayShared.Num(a, "brightness", 0.5);
            bool rum = PlayShared.Bool(a, "rumble", true), fs = PlayShared.Bool(a, "fullscreen", true);
            int res = PlayShared.Int(a, "resolution", 0);
            if (hasVol)
            {
                string e = SavePolicy.VolumeError(vol);
                if (e != null) throw new McpException(McpErr.Refused, e, "volume 0 mutes; the player raises it in the menu");
            }
            string code;
            bool guard = Saves.Guard;
            string err = SavePolicy.SettingsError(hasBri, hasRum, confirm, guard, out code);
            if (err != null) throw new McpException(code, err, null);
            if ((hasBri || hasRum) && guard) Saves.EnsureBackup(ctx);
            return ToolResult.Ok(ctx.Main(() =>
            {
                if (hasVol)
                {
                    GameSystem.Settings.volume.value = 0f;
                    w.Add("volume: 0 (muted, stored in the registry); restore never raises it");
                }
                if (hasBri)
                {
                    float orig = User.Brightness;
                    User.Brightness = (float)bri;
                    PlayLevels.Reg("settings:brightness", "brightness (INSIDE.sav)", () => { User.Brightness = orig; return "brightness back to " + PlayShared.F(orig); });
                    w.Add("brightness: written to INSIDE.sav");
                }
                if (hasRum)
                {
                    bool orig = User.Rumble;
                    User.Rumble = rum;
                    PlayLevels.Reg("settings:rumble", "rumble (INSIDE.sav)", () => { User.Rumble = orig; return "rumble back to " + (orig ? "on" : "off"); });
                    w.Add("rumble: written to INSIDE.sav");
                }
                if (hasFs)
                {
                    bool orig = ScreenSettings.IsFullscreen();
                    ScreenSettings.SetFullscreen(fs);
                    PlayLevels.Reg("settings:fullscreen", "fullscreen", () => { ScreenSettings.SetFullscreen(orig); return "fullscreen back to " + (orig ? "on" : "off"); });
                }
                if (hasRes)
                {
                    int n = ScreenSettings.resolutionCount;
                    if (res < 0 || res >= n) throw new McpException(McpErr.BadArgs, "resolution " + res + " is out of range (0.." + (n - 1) + ")");
                    int orig = ScreenSettings.GetCurrentResolutionIndex();
                    ScreenSettings.SetResolution(res);
                    if (orig >= 0) PlayLevels.Reg("settings:resolution", "screen resolution", () => { ScreenSettings.SetResolution(orig); return "resolution back to " + ScreenSettings.GetResolutionName(orig); });
                    w.Add("resolution: " + ScreenSettings.GetResolutionName(res) + " (applies next frame)");
                }
                return UiResult("done", w);
            }));
        }

        static Json.Obj UiResult(string outcome, List<string> warnings)
        {
            var o = PlayShared.Env();
            o["outcome"] = outcome;
            var s = new Json.Obj();
            s["menu_locked"] = MenuManager.reserveMenu;
            bool showing = false;
            try { showing = TitleManager.IsShowing() || TitleManager.IsPressStartShowing(); } catch { }
            s["title_hidden"] = titleHidden || !showing;
            var st = new Json.Obj();
            try { st["volume"] = PlayShared.R(GameSystem.Settings.volume.value); } catch { }
            try { if (User.IsInitialized) { st["brightness"] = PlayShared.R(User.Brightness, 3); st["rumble"] = User.Rumble; } } catch { }
            try { st["fullscreen"] = ScreenSettings.IsFullscreen(); st["resolution"] = (double)ScreenSettings.GetCurrentResolutionIndex(); } catch { }
            s["settings"] = st;
            var m = new Json.Obj();
            bool shown = MessageActive();
            m["shown"] = shown;
            Json.Obj j = null;
            if (shown) LastMessage(out j);
            m["text"] = j != null ? j.Str("text", "") : "";
            if (j != null) { if (j.ContainsKey("cancel")) m["cancel"] = j["cancel"]; m["accept"] = j["accept"]; m["selected"] = j["selected"]; }
            s["message"] = m;
            o["state"] = s;
            PlayLevels.AddWarnings(o, warnings);
            return o;
        }
    }
}
