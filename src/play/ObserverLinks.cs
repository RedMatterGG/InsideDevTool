using System;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // ObserverLinks.cs (owner OBSERVE): the calls observe makes into other roles' public APIs (PlayLevels, Menus,
    // WorldCamera, Stepper, AgentCore, GuideBook). Each goes through a breaker: when the module answers
    // NotImplementedException (not built yet) or throws, observe falls back to its own reading and does not call that
    // module again for 10 s, so a missing module never breaks observe and never throws every frame.
    internal static class ObserverLinks
    {
        const int RetryMs = 10000;
        static readonly int[] downUntil = new int[10];
        const int Levels = 0, MenusM = 1, Camera = 2, Clock = 3, Input = 4, Guide = 5, LevelsCur = 6, MenusSum = 7, Grip = 8, Focus = 9;

        static bool Up(int m) { int u = downUntil[m]; return u == 0 || unchecked(Environment.TickCount - u) >= 0; }
        static void Down(int m) { downUntil[m] = unchecked(Environment.TickCount + RetryMs) | 1; }

        public static bool LevelsReady(out string reason, out bool known)
        {
            reason = null;
            known = false;
            if (!Up(Levels)) return false;
            try { bool r = PlayLevels.Ready(out reason); known = true; return r; }
            catch { Down(Levels); return false; }
        }

        public static Json.Obj LevelsCurrent()
        {
            if (!Up(LevelsCur)) return null;
            try { return PlayLevels.Current(); }
            catch { Down(LevelsCur); return null; }
        }

        public static bool MenuOpen(out bool known)
        {
            known = false;
            if (!Up(MenusM)) return false;
            try { bool r = Menus.IsOpen(); known = true; return r; }
            catch { Down(MenusM); return false; }
        }

        public static Json.Obj MenuSummary()
        {
            if (!Up(MenusSum)) return null;
            try { return Menus.Summary(); }
            catch { Down(MenusSum); return null; }
        }

        public static bool CameraState(out bool overridden, out int pinned)
        {
            overridden = false;
            pinned = -1;
            if (!Up(Camera)) return false;
            try { overridden = WorldCamera.Overridden; pinned = WorldCamera.PinnedSubscene; return true; }
            catch { Down(Camera); return false; }
        }

        public static bool ClockState(out string mode, out bool frozen)
        {
            mode = null;
            frozen = false;
            if (!Up(Clock)) return false;
            try { mode = Stepper.Mode == StepMode.Step ? "step" : "realtime"; frozen = Stepper.Frozen; return true; }
            catch { Down(Clock); return false; }
        }

        public static Json.Obj InputDescribe(out bool hijacked, out bool installed)
        {
            hijacked = installed = false;
            if (!Up(Input)) return null;
            try
            {
                var d = AgentCore.Describe();
                installed = AgentCore.Installed;
                hijacked = AgentCore.Hijacked;
                return d;
            }
            catch { Down(Input); return null; }
        }

        public static string Controller()
        {
            if (Up(Input))
            {
                try { return AgentCore.Controller(); }
                catch { Down(Input); }
            }
            try { return GameInput.controllerType.ToString(); } catch { return null; }
        }

        public static bool GripHeld()
        {
            if (!Up(Grip)) return false;
            try { return AgentCore.Grip; }
            catch { Down(Grip); return false; }
        }

        public static string FocusState()
        {
            if (!Up(Focus)) return null;
            try { return Stepper.FocusState(); }
            catch { Down(Focus); return null; }
        }

        public static string GuideTopic(int subscene, int savepoint)
        {
            if (!Up(Guide) || subscene < 0) return null;
            try { return GuideBook.TopicFor(subscene, savepoint); }
            catch { Down(Guide); return null; }
        }
    }
}
