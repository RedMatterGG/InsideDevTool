using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace InsideDev
{
    // Automated focus-loss/regain stress test (the Alt-Tab rendering test from the spec).
    // A worker thread drives the OS with real Alt+Tab keystrokes (or minimize/restore), so it keeps running
    // even while Unity's main loop is paused by focus loss. The main thread only reads counters RenderHost
    // already maintains (focus events, first submit after regain, pixel-probe verdicts) and writes a summary.
    //
    // keybd_event sends keys to whatever window has the keyboard. Both workers therefore send a key only while the game
    // window is the foreground window (or, for the Alt+Tab that comes back, while the window they left for still is),
    // and say so in the status when they could not. The word "force" in the focus test's mode restores the blind
    // behaviour of the versions before 0.11.0.
    public static class FocusTest
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScanW(char ch);
        [DllImport("user32.dll")] static extern uint SendInput(uint count, IntPtr inputs, int size);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        delegate bool EnumProc(IntPtr h, IntPtr l);

        const byte VK_MENU = 0x12, VK_TAB = 0x09, VK_SHIFT = 0x10, VK_CONTROL = 0x11;
        const uint KEYUP = 2;
        const int SW_MINIMIZE = 6, SW_RESTORE = 9;

        public static IntPtr hwnd;
        static volatile bool running, done, cancel, force, aborted;
        static volatile int cycle, cycles, leftOk, backOk, skipped;
        static int awayMs, backMs;
        static string mode;
        static Thread thread;
        static volatile string lastOutcome;

        // snapshot of RenderHost counters at start
        static int s_regain, s_lost, s_first, s_verified, s_failed; static long s_fails;
        static bool prevBg;
        static bool setBg;

        public static bool Running { get { return running; } }

        static EnumProc enumCb;
        static IntPtr FindGameWindow()
        {
            IntPtr found = IntPtr.Zero;
            uint me = GetCurrentProcessId();
            enumCb = (h, l) =>
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid != me || !IsWindowVisible(h)) return true;
                var sb = new StringBuilder(64); GetClassNameW(h, sb, 64);
                if (sb.ToString() == "UnityWndClass" || found == IntPtr.Zero) found = h;
                return sb.ToString() != "UnityWndClass";
            };
            EnumWindows(enumCb, IntPtr.Zero);
            return found;
        }

        // main thread. m: alttab | minimize, with "force" anywhere in it (alttab+force) to send keys although the game
        // window is not in front.
        public static string Start(int n, int away, int back, string m, int runInBackground)
        {
            if (running) return "focus test already running (cycle " + cycle + "/" + cycles + ")";
            if (keyBusy) return "busy: keyhold is still running";
            hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero) return "game window not found";
            cycles = Math.Max(1, Math.Min(200, n)); awayMs = Math.Max(200, away); backMs = Math.Max(300, back);
            m = m ?? "";
            force = m.IndexOf("force", StringComparison.OrdinalIgnoreCase) >= 0;
            mode = m.IndexOf("minimize", StringComparison.OrdinalIgnoreCase) >= 0 ? "minimize" : "alttab";
            s_regain = RenderHost.focusRegainCount; s_lost = RenderHost.focusLostCount; s_first = RenderHost.firstSubmitAfterRegainCount;
            s_verified = RenderHost.probeVerifiedCount; s_failed = RenderHost.probeFailedCount; s_fails = RenderHost.submitFails;
            RenderHost.maxRegainFrames = 0;
            prevBg = Application.runInBackground;
            setBg = runInBackground >= 0;
            if (setBg) Application.runInBackground = runInBackground == 1;
            cycle = 0; leftOk = backOk = skipped = 0; done = false; cancel = false; aborted = false; running = true;
            thread = new Thread(Worker) { IsBackground = true, Name = "InsideDev.FocusTest" };
            thread.Start();
            string msg = "FOCUS TEST START: " + cycles + " cycles, mode " + mode + (force ? " (forced)" : "") + ", away " + awayMs + "ms, back " + backMs + "ms, runInBackground " + Application.runInBackground +
                         ", " + Screen.width + "x" + Screen.height + (Screen.fullScreen ? " fullscreen" : " windowed") + ", backend " + RenderHost.active +
                         ", panel " + (DevCore.Instance != null && DevCore.Instance.Panel) + ", board " + RenderDiag.board + ", hwnd 0x" + hwnd.ToInt64().ToString("X");
            RenderHost.Event(msg);
            return msg;
        }

        public static void Stop() { cancel = true; }

        // main thread, when the host goes away: the worker finishes its cycle (the window comes back) and stops
        public static void Abort()
        {
            cancel = true;
            if (running && setBg) Application.runInBackground = prevBg;
            running = false;
        }

        static void Key(byte vk, bool up) { keybd_event(vk, 0, up ? KEYUP : 0, UIntPtr.Zero); }

        // A character the active keyboard layout has no key for (Latin text on a Cyrillic layout, for one) is sent as a
        // Unicode key event: one INPUT of type keyboard with wScan = the character and KEYEVENTF_UNICODE.
        // INPUT is 40 bytes on 64-bit Windows (type at 0, the keyboard part at 8) and 28 on 32-bit (keyboard part at 4).
        static bool UnicodeKey(char ch, bool up)
        {
            int size = IntPtr.Size == 8 ? 40 : 28, at = IntPtr.Size == 8 ? 8 : 4;
            IntPtr mem = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(mem, i, 0);
                Marshal.WriteInt32(mem, 0, 1);                                  // INPUT_KEYBOARD
                Marshal.WriteInt16(mem, at, 0);                                 // wVk
                Marshal.WriteInt16(mem, at + 2, unchecked((short)ch));          // wScan
                Marshal.WriteInt32(mem, at + 4, 0x0004 | (up ? 0x0002 : 0));    // KEYEVENTF_UNICODE [| KEYEVENTF_KEYUP]
                return SendInput(1, mem, size) == 1;
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        static bool IsFront() { return GetForegroundWindow() == hwnd; }

        static void AltTab()
        {
            Key(VK_MENU, false); Thread.Sleep(20);
            Key(VK_TAB, false); Thread.Sleep(20);
            Key(VK_TAB, true); Thread.Sleep(20);
            Key(VK_MENU, true);
        }

        static void ForceForeground()
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            if (GetForegroundWindow() == hwnd) return;
            // an Alt tap makes Windows accept SetForegroundWindow from this process
            Key(VK_MENU, false); SetForegroundWindow(hwnd); Key(VK_MENU, true);
        }

        static bool WaitFor(bool foreground, int ms)
        {
            for (int t = 0; t < ms; t += 50)
            {
                if ((GetForegroundWindow() == hwnd) == foreground) return true;
                Thread.Sleep(50);
            }
            return (GetForegroundWindow() == hwnd) == foreground;
        }

        static void Worker()
        {
            try
            {
                ForceForeground();
                if (!WaitFor(true, 2000) && !force) { aborted = true; return; }
                Thread.Sleep(backMs);
                for (int i = 1; i <= cycles && !cancel; i++)
                {
                    cycle = i;
                    // leaving: only from the game window, otherwise Alt+Tab would cycle the user's other windows
                    if (!force && !IsFront())
                    {
                        ForceForeground();
                        if (!WaitFor(true, 2000)) { skipped++; Thread.Sleep(backMs); continue; }
                    }
                    if (mode == "minimize") ShowWindow(hwnd, SW_MINIMIZE); else AltTab();
                    bool left = WaitFor(false, 1500);
                    if (left) leftOk++;
                    IntPtr other = GetForegroundWindow();
                    Thread.Sleep(awayMs);
                    if (mode == "minimize") { ShowWindow(hwnd, SW_RESTORE); ForceForeground(); }
                    else if (force || (left && GetForegroundWindow() == other))
                    {
                        AltTab();
                        if (!WaitFor(true, 700)) ForceForeground();
                    }
                    // the game never left, or the user moved on to another window meanwhile: no second Alt+Tab
                    else if (!IsFront()) ForceForeground();
                    if (WaitFor(true, 2000)) backOk++;
                    Thread.Sleep(backMs);
                }
            }
            catch (Exception e) { DevLog.Write("[render] focus test worker error: " + e.Message); }
            finally { done = true; }
        }

        // main thread, every frame
        public static void Tick()
        {
            if (!running || !done) return;
            running = false;
            if (setBg) Application.runInBackground = prevBg;
            int regain = RenderHost.focusRegainCount - s_regain, lost = RenderHost.focusLostCount - s_lost;
            int first = RenderHost.firstSubmitAfterRegainCount - s_first;
            int ver = RenderHost.probeVerifiedCount - s_verified, fail = RenderHost.probeFailedCount - s_failed;
            long sf = RenderHost.submitFails - s_fails;
            bool pass = !cancel && !aborted && skipped == 0 && backOk == cycles && regain >= cycles && first >= cycles && fail == 0 && ver >= cycles && sf == 0;
            lastOutcome = "FOCUS TEST " + (pass ? "PASS" : "FAIL") + ": cycles " + cycle + "/" + cycles + " mode " + mode +
                             " | OS: left " + leftOk + " back " + backOk + (skipped > 0 ? " skipped " + skipped + " (game window not in front)" : "") +
                             " | Unity: focus lost " + lost + " regained " + regain +
                             " | first submit after regain " + first + " (max +" + RenderHost.maxRegainFrames + " frames)" +
                             " | pixel probes verified " + ver + " failed " + fail + " | submit failures " + sf +
                             " | backend now " + RenderHost.active + (cancel ? " | CANCELLED" : "") +
                             (aborted ? " | ABORTED: the game window could not be brought to the front, no key was sent (mode " + mode + "+force sends them anyway)" : "");
            RenderHost.Event(lastOutcome);
            RenderHost.Snapshot("focus test end");
        }

        // ---------------------------------------------------------------- keyhold
        static volatile bool keyBusy;
        static volatile string keyState;
        const int MaxHoldMs = 60000;

        // Input-capture test: bring the game to the foreground, type `text`, then hold a key for `ms` (at most 60 s).
        // Nothing is typed unless the game window is in front, and the hold ends early when it stops being so.
        // The outcome is in KeyHoldStatus (also the last line of "focustest status").
        public static string KeyHold(byte vk, int ms, string text)
        {
            if (running) return "focus test running";
            if (keyBusy) return "busy: the previous keyhold is still running";
            if (hwnd == IntPtr.Zero) hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero) return "game window not found";
            ms = Math.Max(0, Math.Min(ms, MaxHoldMs));
            keyBusy = true;
            keyState = "running: vk 0x" + vk.ToString("X2") + " " + ms + " ms";
            var t = new Thread(() =>
            {
                try
                {
                    ForceForeground();
                    if (!WaitFor(true, 2000)) { keyState = "aborted: the game window is not in the foreground, nothing was typed"; return; }
                    Thread.Sleep(300);
                    int typed = 0, noKey = 0, len = string.IsNullOrEmpty(text) ? 0 : text.Length;
                    bool lostFront = false;
                    for (int i = 0; i < len && !lostFront; i++)
                    {
                        if (!IsFront()) { lostFront = true; break; }
                        short ks = VkKeyScanW(text[i]);
                        if (ks == -1)
                        {
                            noKey++;
                            bool sent = UnicodeKey(text[i], false); Thread.Sleep(30); UnicodeKey(text[i], true); Thread.Sleep(30);
                            if (sent) typed++;
                            continue;
                        }
                        byte k = (byte)(ks & 0xFF); int mods = (ks >> 8) & 0xFF;
                        if ((mods & 1) != 0) Key(VK_SHIFT, false);
                        if ((mods & 2) != 0) Key(VK_CONTROL, false);
                        if ((mods & 4) != 0) Key(VK_MENU, false);
                        Key(k, false); Thread.Sleep(30); Key(k, true);
                        if ((mods & 4) != 0) Key(VK_MENU, true);
                        if ((mods & 2) != 0) Key(VK_CONTROL, true);
                        if ((mods & 1) != 0) Key(VK_SHIFT, true);
                        Thread.Sleep(30);
                        typed++;
                    }
                    int held = 0;
                    if (!lostFront)
                    {
                        Key(vk, false);
                        try
                        {
                            while (held < ms)
                            {
                                int step = Math.Min(50, ms - held);
                                Thread.Sleep(step); held += step;
                                if (!IsFront()) { lostFront = true; break; }
                            }
                        }
                        finally { Key(vk, true); }
                    }
                    keyState = (lostFront ? "aborted: the game window lost the foreground" : "done") + ", foreground " + (IsFront() ? "yes" : "no") + ", typed " + typed + " of " + len + " chars" +
                               (noKey > 0 ? " (" + noKey + " sent as Unicode: no key on this keyboard layout)" : "") + ", held vk 0x" + vk.ToString("X2") + " " + held + " of " + ms + " ms";
                }
                catch (Exception e) { keyState = "failed: " + e.Message; }
                finally { keyBusy = false; DevLog.Write("[input] keyhold " + keyState); }
            }) { IsBackground = true, Name = "InsideDev.KeyHold" };
            t.Start();
            return "keyhold started";
        }

        // "not run" | "running: ..." | "done, foreground yes, typed 5 of 5 chars, held vk 0x27 1500 of 1500 ms" | "aborted: ..." | "failed: ..."
        public static string KeyHoldStatus() { return keyState ?? "not run"; }

        public static string Status()
        {
            string s = running ? "running cycle " + cycle + "/" + cycles + " (" + mode + ")  left " + leftOk + " back " + backOk + (skipped > 0 ? " skipped " + skipped : "") : "idle";
            string lo = lastOutcome, ks = keyState;
            if (lo != null && !running) s += "\nlast: " + lo;
            if (ks != null) s += "\nkeyhold: " + ks;
            return s;
        }

        public static string StatusJson()
        {
            return JsonLine.Obj("ok", true, "running", running, "cycle", (int)cycle, "cycles", (int)cycles, "mode", mode, "forced", (bool)force, "left", (int)leftOk, "back", (int)backOk,
                "skipped", (int)skipped, "aborted", (bool)aborted, "last", lastOutcome, "keyholdBusy", (bool)keyBusy, "keyhold", KeyHoldStatus());
        }
    }
}
