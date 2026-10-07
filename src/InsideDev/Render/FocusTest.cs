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
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        delegate bool EnumProc(IntPtr h, IntPtr l);

        const byte VK_MENU = 0x12, VK_TAB = 0x09;
        const uint KEYUP = 2;
        const int SW_MINIMIZE = 6, SW_RESTORE = 9;

        public static IntPtr hwnd;
        static volatile bool running, done, cancel;
        static volatile int cycle, cycles, leftOk, backOk;
        static int awayMs, backMs;
        static string mode;
        static Thread thread;

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

        // main thread
        public static string Start(int n, int away, int back, string m, int runInBackground)
        {
            if (running) return "focus test already running (cycle " + cycle + "/" + cycles + ")";
            hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero) return "game window not found";
            cycles = Math.Max(1, Math.Min(200, n)); awayMs = Math.Max(200, away); backMs = Math.Max(300, back);
            mode = m == "minimize" ? "minimize" : "alttab";
            s_regain = RenderHost.focusRegainCount; s_lost = RenderHost.focusLostCount; s_first = RenderHost.firstSubmitAfterRegainCount;
            s_verified = RenderHost.probeVerifiedCount; s_failed = RenderHost.probeFailedCount; s_fails = RenderHost.submitFails;
            RenderHost.maxRegainFrames = 0;
            prevBg = Application.runInBackground;
            setBg = runInBackground >= 0;
            if (setBg) Application.runInBackground = runInBackground == 1;
            cycle = 0; leftOk = backOk = 0; done = false; cancel = false; running = true;
            thread = new Thread(Worker) { IsBackground = true, Name = "InsideDev.FocusTest" };
            thread.Start();
            string msg = "FOCUS TEST START: " + cycles + " cycles, mode " + mode + ", away " + awayMs + "ms, back " + backMs + "ms, runInBackground " + Application.runInBackground +
                         ", " + Screen.width + "x" + Screen.height + (Screen.fullScreen ? " fullscreen" : " windowed") + ", backend " + RenderHost.active +
                         ", panel " + (DevCore.Instance != null && DevCore.Instance.Panel) + ", board " + RenderDiag.board + ", hwnd 0x" + hwnd.ToInt64().ToString("X");
            RenderHost.Event(msg);
            return msg;
        }

        public static void Stop() { cancel = true; }

        static void Key(byte vk, bool up) { keybd_event(vk, 0, up ? KEYUP : 0, UIntPtr.Zero); }

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
                WaitFor(true, 2000);
                Thread.Sleep(backMs);
                for (int i = 1; i <= cycles && !cancel; i++)
                {
                    cycle = i;
                    if (mode == "minimize") ShowWindow(hwnd, SW_MINIMIZE); else AltTab();
                    if (WaitFor(false, 1500)) leftOk++;
                    Thread.Sleep(awayMs);
                    if (mode == "minimize") { ShowWindow(hwnd, SW_RESTORE); ForceForeground(); }
                    else { AltTab(); if (!WaitFor(true, 700)) ForceForeground(); }
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
            bool pass = !cancel && backOk == cycles && regain >= cycles && first >= cycles && fail == 0 && ver >= cycles && sf == 0;
            RenderHost.Event("FOCUS TEST " + (pass ? "PASS" : "FAIL") + ": cycles " + cycle + "/" + cycles + " mode " + mode +
                             " | OS: left " + leftOk + " back " + backOk +
                             " | Unity: focus lost " + lost + " regained " + regain +
                             " | first submit after regain " + first + " (max +" + RenderHost.maxRegainFrames + " frames)" +
                             " | pixel probes verified " + ver + " failed " + fail + " | submit failures " + sf +
                             " | backend now " + RenderHost.active + (cancel ? " | CANCELLED" : ""));
            RenderHost.Snapshot("focus test end");
        }

        [DllImport("user32.dll")] static extern short VkKeyScan(char ch);

        // Input-capture test: bring the game to the foreground, type `text`, then hold a key for `ms`.
        public static string KeyHold(byte vk, int ms, string text)
        {
            if (running) return "focus test running";
            if (hwnd == IntPtr.Zero) hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero) return "game window not found";
            var t = new Thread(() =>
            {
                try
                {
                    ForceForeground(); WaitFor(true, 2000); Thread.Sleep(300);
                    if (!string.IsNullOrEmpty(text))
                        foreach (char ch in text) { byte k = (byte)(VkKeyScan(ch) & 0xFF); Key(k, false); Thread.Sleep(30); Key(k, true); Thread.Sleep(30); }
                    Key(vk, false); Thread.Sleep(ms); Key(vk, true);
                    DevLog.Write("[input] keyhold done: vk 0x" + vk.ToString("X2") + " " + ms + "ms, typed '" + text + "', foreground " + (GetForegroundWindow() == hwnd));
                }
                catch (Exception e) { DevLog.Write("[input] keyhold error: " + e.Message); }
            }) { IsBackground = true };
            t.Start();
            return "keyhold started";
        }

        public static string Status()
        {
            return running ? "running cycle " + cycle + "/" + cycles + " (" + mode + ")  left " + leftOk + " back " + backOk : "idle";
        }
    }
}
