using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace InsideDev.Play
{
    // MenusUi.cs (owner WORLD_A): the UGUI bridge of Menus. The build references UnityEngine.dll but not UnityEngine.UI.dll,
    // and every member call on a type that derives from UGUI's UIBehaviour (MenuManager, MenuGroup, every UIMenuItem) needs
    // it at compile time (CS0012), so EventSystem, ExecuteEvents and those calls go through reflection resolved once from
    // the loaded UnityEngine.UI assembly. Events are sent exactly the way MenuInputModule sends them: ExecuteEvents.Execute
    // on EventSystem.current.currentSelectedGameObject with ExecuteEvents.submitHandler / cancelHandler /
    // updateSelectedHandler, MenuInputModule's own private static navigationHandler with a NavigationEventData, and
    // MenuExecuteEvents.toggleGameMenuHandler (FP/MenuInputModule.cs:31,52-55,119-131,217-240, FP/MenuExecuteEvents.cs:5).
    // Selection changes made by items are deferred to their next OnUpdateSelected (FP/UIMenuItem.cs:60-79,150-161) and a
    // selected MenuGroup picks its first item there (FP/MenuGroup.cs:139-145,232-244), so Pump sends updateSelected until
    // the selection is stable, which is what EventSystem.Update does once per frame (SendUpdateEvent).
    internal static class MenusUi
    {
        static bool resolved;
        static string problem;
        static Type tEventSystem, tBaseEventData, tExecuteEvents, tMoveDirection, tNavData, tINav, tISubmit, tICancel, tIUpdate, tIToggle, tText;
        static PropertyInfo pCurrent, pSelected, pText;
        static MethodInfo mSetSelected, mExecute, mOpen, mCloseAll, mIsVisible;
        static object fnSubmit, fnCancel, fnUpdate, fnNav, fnToggle;
        static FieldInfo fNavDirection;
        static readonly Dictionary<string, MethodInfo> executes = new Dictionary<string, MethodInfo>();

        public static string Problem { get { Resolve(); return problem; } }
        public static bool Ok { get { Resolve(); return problem == null; } }
        public static Type TextType { get { Resolve(); return tText; } }

        static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            try
            {
                Assembly ui = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "UnityEngine.UI") { ui = a; break; }
                if (ui == null) { problem = "UnityEngine.UI is not loaded"; return; }
                tEventSystem = ui.GetType("UnityEngine.EventSystems.EventSystem");
                tBaseEventData = ui.GetType("UnityEngine.EventSystems.BaseEventData");
                tExecuteEvents = ui.GetType("UnityEngine.EventSystems.ExecuteEvents");
                tMoveDirection = ui.GetType("UnityEngine.EventSystems.MoveDirection");
                tISubmit = ui.GetType("UnityEngine.EventSystems.ISubmitHandler");
                tICancel = ui.GetType("UnityEngine.EventSystems.ICancelHandler");
                tIUpdate = ui.GetType("UnityEngine.EventSystems.IUpdateSelectedHandler");
                tText = ui.GetType("UnityEngine.UI.Text");
                tNavData = typeof(MenuInputModule).GetNestedType("NavigationEventData", BindingFlags.Public);
                tINav = typeof(MenuInputModule).GetNestedType("INavigationHandler", BindingFlags.Public);
                tIToggle = typeof(MenuInputModule).Assembly.GetType("IToggleGameMenuHandler");
                if (tEventSystem == null || tBaseEventData == null || tExecuteEvents == null || tMoveDirection == null || tNavData == null || tINav == null || tIToggle == null)
                { problem = "UGUI / MenuInputModule types not found"; return; }
                pCurrent = tEventSystem.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                pSelected = tEventSystem.GetProperty("currentSelectedGameObject", BindingFlags.Public | BindingFlags.Instance);
                mSetSelected = tEventSystem.GetMethod("SetSelectedGameObject", new[] { typeof(GameObject) });
                foreach (var m in tExecuteEvents.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "Execute" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3) { mExecute = m; break; }
                fnSubmit = StaticProp(tExecuteEvents, "submitHandler");
                fnCancel = StaticProp(tExecuteEvents, "cancelHandler");
                fnUpdate = StaticProp(tExecuteEvents, "updateSelectedHandler");
                var nf = typeof(MenuInputModule).GetField("navigationHandler", BindingFlags.NonPublic | BindingFlags.Static);
                fnNav = nf != null ? nf.GetValue(null) : null;
                fnToggle = StaticProp(typeof(MenuExecuteEvents), "toggleGameMenuHandler");
                fNavDirection = tNavData.GetField("direction");
                mOpen = typeof(MenuManager).GetMethod("Open", BindingFlags.Public | BindingFlags.Static);
                mCloseAll = typeof(MenuManager).GetMethod("CloseAll", BindingFlags.Public | BindingFlags.Static);
                mIsVisible = typeof(MenuGroup).GetMethod("IsVisible", BindingFlags.Public | BindingFlags.Instance);
                if (tText != null) pText = tText.GetProperty("text");
                if (pCurrent == null || pSelected == null || mSetSelected == null || mExecute == null || fnSubmit == null || fnCancel == null || fnUpdate == null
                    || fnNav == null || fnToggle == null || fNavDirection == null || mOpen == null || mCloseAll == null)
                    problem = "a UGUI / menu member was not found by reflection";
            }
            catch (Exception e) { problem = "menu reflection: " + e.GetType().Name + ": " + e.Message; }
        }

        static object StaticProp(Type t, string name)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            return p != null ? p.GetValue(null, null) : null;
        }

        static void Need()
        {
            Resolve();
            if (problem != null) throw new InsideDev.McpExt.McpException(InsideDev.McpExt.McpErr.GameError, "menus unavailable: " + problem);
        }

        public static object Current()
        {
            Resolve();
            return problem == null ? pCurrent.GetValue(null, null) : null;
        }

        public static GameObject Selected()
        {
            var es = Current();
            return es != null ? pSelected.GetValue(es, null) as GameObject : null;
        }

        public static void Select(GameObject go)
        {
            Need();
            var es = Current();
            if (es == null) throw new InsideDev.McpExt.McpException(InsideDev.McpExt.McpErr.NotReady, "no EventSystem yet");
            mSetSelected.Invoke(es, new object[] { go });
        }

        public static void Open() { Need(); mOpen.Invoke(null, null); }
        public static void CloseAll() { Need(); mCloseAll.Invoke(null, null); }

        public static bool GroupVisible(Component group)
        {
            Resolve();
            if (group == null || mIsVisible == null) return false;
            try { return (bool)mIsVisible.Invoke(group, null); } catch { return false; }
        }

        // which: submit, cancel, update, toggle, up, down, left, right
        public static bool Send(GameObject target, string which)
        {
            Need();
            var es = Current();
            if (es == null || target == null) return false;
            object data, fn;
            Type handler;
            switch (which)
            {
                case "submit": data = Activator.CreateInstance(tBaseEventData, es); fn = fnSubmit; handler = tISubmit; break;
                case "cancel": data = Activator.CreateInstance(tBaseEventData, es); fn = fnCancel; handler = tICancel; break;
                case "update": data = Activator.CreateInstance(tBaseEventData, es); fn = fnUpdate; handler = tIUpdate; break;
                case "toggle": data = Activator.CreateInstance(tBaseEventData, es); fn = fnToggle; handler = tIToggle; break;
                case "up": case "down": case "left": case "right":
                    data = Activator.CreateInstance(tNavData, es);
                    fNavDirection.SetValue(data, Enum.Parse(tMoveDirection, char.ToUpperInvariant(which[0]) + which.Substring(1)));
                    fn = fnNav; handler = tINav;
                    break;
                default: return false;
            }
            MethodInfo m;
            lock (executes)
            {
                if (!executes.TryGetValue(handler.FullName, out m)) { m = mExecute.MakeGenericMethod(handler); executes[handler.FullName] = m; }
            }
            return (bool)m.Invoke(null, new[] { target, data, fn });
        }

        // EventSystem.Update's SendUpdateEvent, repeated until the selection is stable (at most 4 times)
        public static void Pump()
        {
            for (int i = 0; i < 4; i++)
            {
                var before = Selected();
                if (before == null) return;
                Send(before, "update");
                if (Selected() == before) return;
            }
        }

        public static string TextOf(Component c, int nth)
        {
            Resolve();
            if (c == null || tText == null || pText == null) return null;
            try
            {
                var ts = c.GetComponentsInChildren(tText, false);
                if (ts == null || nth >= ts.Length) return null;
                return pText.GetValue(ts[nth], null) as string;
            }
            catch { return null; }
        }

        public static string TextValue(object text)
        {
            Resolve();
            if (text == null || pText == null) return null;
            try { return pText.GetValue(text, null) as string; } catch { return null; }
        }

        public static object Field(object o, string name)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }
    }
}
