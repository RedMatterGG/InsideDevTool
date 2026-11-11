// InsideMuter (optional, gate G1 of docs/REPLAY_DESIGN.md 6.0): mutes every INSIDE.exe audio session on every active
// render endpoint every 50 ms (ISimpleAudioVolume.SetMute only, the volume is never changed) and writes heartbeat.json every
// 250 ms. No minute limit: it runs while INSIDE.exe runs and exits 10 s (--linger) after the last INSIDE.exe ended.
// A session's owner is identified by its pid (image name, once per process); the process list (Toolhelp snapshot)
// is read every 1 s (5 s while an INSIDE.exe is known; its exit is read from its handle every poll), the render endpoint list
// on every device notification and every heartbeat, and each endpoint's session manager is activated once.
// Version 4: the muter remembers the sessions it switched from audible to muted and switches them back when it ends
// (test end, Ctrl+C, closing the console, the stop command, and the game's own sessions when the game exits), so the
// game is not left muted in the Windows volume mixer. --keep-muted keeps the old behaviour (the mute stays). A muter
// that is killed cannot undo anything: InsideMuter unmute does it afterwards.
// Usage: InsideMuter [run] [--heartbeat <path>] [--linger <s>] [--wait <s>] [--keep-muted]
//        InsideMuter list | status [--heartbeat <path>] | stop [--keep-muted] | unmute
//        InsideMuter test <seconds> [--heartbeat <path>] [--also <process name>] [--keep-muted]
// --wait: leave when no INSIDE.exe was seen for that many seconds after the start (0, the default, waits for ever).
// status: prints the heartbeat of the running muter (exit code 0 fresh, 2 stale or missing). stop: asks the running
// muter to end (it un-mutes what it muted unless --keep-muted). unmute: stops a running muter, then clears the mute of
// every INSIDE session. --also (test mode only) adds a second process name next to INSIDE, for an offline check.
// Exit codes: 0 ok, 1 usage, 2 status: no fresh heartbeat / stop: no muter running, 3 second run instance.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IMMNotificationClient client);
    void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
}

[ComVisible(true)]
sealed class DeviceEvents : IMMNotificationClient
{
    public static volatile bool dirty = true;
    public void OnDeviceStateChanged(string id, int state) { dirty = true; }
    public void OnDeviceAdded(string id) { dirty = true; }
    public void OnDeviceRemoved(string id) { dirty = true; }
    public void OnDefaultDeviceChanged(int flow, int role, string id) { dirty = true; }
    public void OnPropertyValueChanged(string id, PropertyKey key) { }
}

[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    void GetCount(out int count);
    void Item(int index, out IMMDevice device);
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    void OpenPropertyStore(int access, out IPropertyStore store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out int state);
}

[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    void GetCount(out int count);
    void GetAt(int index, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
}

[StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid fmtid; public int pid; }

[StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort vt, r1, r2, r3; public IntPtr p, p2; }

[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    void GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
    void GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
    void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
}

[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    void GetCount(out int count);
    void GetSession(int index, out IAudioSessionControl2 session);
}

[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    void GetState(out int state);
    void GetDisplayName(out IntPtr name);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
    void GetIconPath(out IntPtr path);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
    void GetGroupingParam(out Guid g);
    void SetGroupingParam(ref Guid g, ref Guid ctx);
    void RegisterAudioSessionNotification(IntPtr n);
    void UnregisterAudioSessionNotification(IntPtr n);
    void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetProcessId(out uint pid);
}

[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISimpleAudioVolume
{
    void SetMasterVolume(float level, ref Guid ctx);
    void GetMasterVolume(out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation
{
    void GetPeakValue(out float peak);
    void GetMeteringChannelCount(out int count);
    void GetChannelsPeakValues(int count, [Out, MarshalAs(UnmanagedType.LPArray)] float[] peaks);
    void QueryHardwareSupport(out int mask);
}

sealed class SessionInfo
{
    public uint pid;
    public string endpoint = "", endpointName = "", state = "", instance = "";
    public bool muted;
    public float volume, peak;
    public int unmutedSeen;
}

sealed class EndpointCache
{
    public string id = "", name = "";
    public object manager, meter;
    public int pass;
    // INSIDE sessions seen on this endpoint in the last poll and in the one before: while the number does not change
    // and every session is muted, the session names are not asked for again (no strings per poll)
    public int targets, lastTargets = -1;
}

sealed class EndpointInfo
{
    public string id = "", name = "";
    public bool isDefault;
    public float peak;
    public int insideSessions;
}

static class InsideMuter
{
    const int RenderFlow = 0, RoleConsole = 0, StateActive = 1, ClsCtxAll = 23, PollMs = 50, HeartbeatMs = 250, VtLpwstr = 31;
    const string Version = "4";
    const string RunMutex = "Local\\InsideDev.InsideMuter.run", StopEventName = "Local\\InsideDev.InsideMuter.stop", StopKeepEventName = "Local\\InsideDev.InsideMuter.stop.keep";
    const string Usage = "usage: InsideMuter [run] [--heartbeat <path>] [--linger <s>] [--wait <s>] [--keep-muted] | list | status [--heartbeat <path>] | stop [--keep-muted] | unmute | test <seconds> [--heartbeat <path>] [--also <process name>] [--keep-muted]";
    static readonly Guid MuteContext = new Guid("6C1F0E52-6E1B-4E47-9C5B-1A5D3C0A7B11");
    static readonly Guid SessionManagerIid = typeof(IAudioSessionManager2).GUID;
    static readonly Guid MeterIid = typeof(IAudioMeterInformation).GUID;
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;
    static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static string heartbeatPath, mode = "run", alsoTarget;
    static double lingerSeconds = 10, waitSeconds = 0;
    static bool keepMuted;
    // 0 = run on, 1 = end and un-mute what this muter muted, 2 = end and leave it muted
    static volatile int stopRequest;
    static readonly ManualResetEvent loopEnded = new ManualResetEvent(false);
    // sessions (endpoint|pid|instance) and pids that this muter switched from audible to muted
    static readonly HashSet<string> mutedByMuter = new HashSet<string>();
    static readonly HashSet<uint> mutedPids = new HashSet<uint>();
    static readonly List<uint> exitedPids = new List<uint>();
    static long restoredTotal;
    static readonly HashSet<uint> insidePids = new HashSet<uint>();
    static readonly Dictionary<uint, IntPtr> insideHandles = new Dictionary<uint, IntPtr>();
    static readonly Dictionary<uint, IntPtr> otherHandles = new Dictionary<uint, IntPtr>();
    static readonly HashSet<uint> unopened = new HashSet<uint>();
    static readonly List<uint> pidScratch = new List<uint>();
    static readonly HashSet<uint> scanSeen = new HashSet<uint>();
    static bool seenInside;
    static readonly Dictionary<uint, string> processPaths = new Dictionary<uint, string>();
    static readonly Dictionary<string, string> endpointNames = new Dictionary<string, string>();
    static readonly Dictionary<string, EndpointCache> endpointCache = new Dictionary<string, EndpointCache>();
    static readonly List<string> endpointGone = new List<string>();
    static int endpointPass;
    static readonly Dictionary<string, int> unmutedSeen = new Dictionary<string, int>();
    static readonly HashSet<string> reported = new HashSet<string>();
    static readonly List<SessionInfo> sessions = new List<SessionInfo>();
    static readonly List<EndpointInfo> endpoints = new List<EndpointInfo>();
    static long seq, polls, pollErrors, writeErrors, unmutedSeenTotal, lastUnmutedSeenMs, startedMs, lastInsideMs;
    static string lastError = "";
    static long lastErrorLogMs;
    static int muterPid;

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileExW(string from, string to, int flags);

    delegate bool CtrlHandler(int type);
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(CtrlHandler handler, bool add);
    static CtrlHandler ctrlHandler;

    // Ctrl+C, Ctrl+Break, the console window closing, logoff, shutdown: ask the loop to end and give it time to
    // un-mute. Windows ends the process when this returns for every type but Ctrl+C / Ctrl+Break.
    static bool OnCtrl(int type)
    {
        if (stopRequest == 0) stopRequest = 1;
        loopEnded.WaitOne(3000);
        return true;
    }

    const int ScanWaitingMs = 1000, ScanRunningMs = 5000;
    static readonly DeviceEvents deviceEvents = new DeviceEvents();
    static IMMDeviceEnumerator notifying;
    const uint SnapProcess = 2, QueryLimited = 0x1000, Synchronize = 0x00100000, WaitSignaled = 0;
    static readonly IntPtr InvalidHandle = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint size, usage, pid;
        public IntPtr heapId;
        public uint moduleId, threads, parentPid;
        public int basePriority;
        public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string exe;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    [MTAThread]
    static int Main(string[] args)
    {
        string command = "run";
        double testSeconds = 0;
        heartbeatPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            double minutes;
            if (a == "--heartbeat" && i + 1 < args.Length)
            {
                try { heartbeatPath = Path.GetFullPath(args[++i]); }
                catch (Exception e) { Console.WriteLine("--heartbeat: " + e.Message); Console.WriteLine(Usage); return 1; }
            }
            else if (a == "--linger" && i + 1 < args.Length) { if (!Seconds(args[++i], out lingerSeconds)) return BadNumber("--linger", args[i]); }
            else if (a == "--wait" && i + 1 < args.Length) { if (!Seconds(args[++i], out waitSeconds)) return BadNumber("--wait", args[i]); }
            else if (a == "--keep-muted") keepMuted = true;
            else if (a == "--also" && i + 1 < args.Length) alsoTarget = args[++i];
            else if (a == "run" || a == "list" || a == "status" || a == "stop" || a == "unmute") command = a;
            else if (a == "test" && i + 1 < args.Length) { command = "test"; if (!Seconds(args[++i], out testSeconds)) return BadNumber("test", args[i]); }
            else if (i == 0 && double.TryParse(a, NumberStyles.Float, IC, out minutes)) Console.WriteLine("minute limit " + a + " ignored: this muter runs until " + lingerSeconds.ToString(IC) + " s after the last INSIDE.exe ended");
            else { Console.WriteLine(Usage); return 1; }
        }
        if (alsoTarget != null && command != "test") { Console.WriteLine("--also is accepted in test mode only"); return 1; }
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        if (command == "list") { List(); return 0; }
        if (command == "status") return Status(heartbeatPath ?? Path.Combine(exeDir, "heartbeat.json"));
        if (command == "stop") return Stop(keepMuted, true) ? 0 : 2;
        if (command == "unmute") return Unmute();
        ctrlHandler = OnCtrl;
        try { SetConsoleCtrlHandler(ctrlHandler, true); } catch (Exception e) { Console.WriteLine("no console control handler (" + e.Message + "): Ctrl+C will not un-mute"); }
        if (command == "test")
        {
            mode = "test";
            if (heartbeatPath == null) heartbeatPath = Path.Combine(exeDir, "heartbeat_test.json");
            int tc = Loop(testSeconds, null);
            loopEnded.Set();
            return tc;
        }
        if (heartbeatPath == null) heartbeatPath = Path.Combine(exeDir, "heartbeat.json");
        bool created;
        using (var single = new Mutex(true, RunMutex, out created))
        {
            if (!created) { Console.WriteLine("another InsideMuter run instance is active; exiting"); return 3; }
            int code;
            using (var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName))
            using (var stopKeep = new EventWaitHandle(false, EventResetMode.AutoReset, StopKeepEventName))
                code = Loop(-1, new WaitHandle[] { stop, stopKeep });
            single.ReleaseMutex();
            loopEnded.Set();
            return code;
        }
    }

    // a number of seconds from the command line: invariant culture, finite, not negative
    static bool Seconds(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, IC, out value) && !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
    }

    static int BadNumber(string option, string text)
    {
        Console.WriteLine(option + " needs a number of seconds (0 or more, with a decimal point), got '" + text + "'");
        Console.WriteLine(Usage);
        return 1;
    }

    // true while a run instance holds the single-instance mutex
    static bool RunInstanceActive()
    {
        bool created;
        using (var m = new Mutex(false, RunMutex, out created)) return !created;
    }

    // asks the running muter to end; true when one was running. wait: come back when it has ended (3 s at most)
    static bool Stop(bool keep, bool report)
    {
        if (!RunInstanceActive()) { if (report) Console.WriteLine("no InsideMuter run instance is active"); return false; }
        try
        {
            using (var e = EventWaitHandle.OpenExisting(keep ? StopKeepEventName : StopEventName)) e.Set();
        }
        catch (Exception e) { if (report) Console.WriteLine("the running InsideMuter does not take the stop command (an older version?): " + e.Message); return false; }
        for (int i = 0; i < 60 && RunInstanceActive(); i++) Thread.Sleep(50);
        bool ended = !RunInstanceActive();
        if (report) Console.WriteLine(ended ? "InsideMuter stopped" + (keep ? " (the sessions stay muted)" : " (it un-muted what it had muted)") : "stop sent; the muter has not ended yet");
        return true;
    }

    // status: the heartbeat of the running muter in one line; 0 = fresh, 2 = missing or older than 2 s
    static int Status(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) { Console.WriteLine("no heartbeat at " + path + " (" + e.GetType().Name + "); run instance " + (RunInstanceActive() ? "active" : "not active")); return 2; }
        long unixMs;
        long.TryParse(Field(text, "unixMs"), NumberStyles.Integer, IC, out unixMs);
        double age = (NowMs() - unixMs) / 1000.0;
        string status = Field(text, "status");
        bool fresh = unixMs > 0 && age < 2.0 && status != "stopped";
        Console.WriteLine("InsideMuter " + Field(text, "muterVersion") + " " + Field(text, "mode") + ": " + status + ", heartbeat " + age.ToString("0.0", IC) + " s old" + (fresh ? "" : " (not running)") +
                          ", sessions " + Field(text, "sessions") + " (muted " + Field(text, "muted") + ", audible " + Field(text, "unmuted") + "), muted by this muter " + Field(text, "mutedByMuter") +
                          ", un-muted again " + Field(text, "unmutedSeenTotal") + " time(s), pid " + Field(text, "muterPid") + ", errors " + Field(text, "pollErrors"));
        return fresh ? 0 : 2;
    }

    // the first value of "name": in the heartbeat text (a number, true / false, or a string without escapes)
    static string Field(string json, string name)
    {
        int i = json.IndexOf("\"" + name + "\":", StringComparison.Ordinal);
        if (i < 0) return "?";
        i += name.Length + 3;
        while (i < json.Length && json[i] == ' ') i++;
        if (i < json.Length && json[i] == '"')
        {
            int e = json.IndexOf('"', i + 1);
            return e < 0 ? "?" : json.Substring(i + 1, e - i - 1);
        }
        int j = i;
        while (j < json.Length && json[j] != ',' && json[j] != '}' && json[j] != '\n' && json[j] != ' ') j++;
        return j > i ? json.Substring(i, j - i) : "?";
    }

    // unmute: a running muter is stopped first (it would mute again at once), then every INSIDE session is un-muted
    static int Unmute()
    {
        if (RunInstanceActive()) Stop(false, true);
        IMMDeviceEnumerator enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            FindInside();
            int n = SetMuteAll(enumerator, null, false);
            Console.WriteLine(insidePids.Count == 0 ? "no INSIDE.exe is running (its sessions exist only while it runs; start the game and run unmute again if it is still muted)"
                : "un-muted " + n + " INSIDE session(s) of pid " + JoinPids());
            return 0;
        }
        catch (Exception e) { Console.WriteLine("unmute failed: " + e.Message); return 1; }
        finally { Release(enumerator); }
    }

    // Sets the mute of sessions on every active render endpoint; returns how many were changed. only = the session
    // keys to touch (null = every session of a known INSIDE pid).
    static int SetMuteAll(IMMDeviceEnumerator enumerator, HashSet<string> only, bool mute)
    {
        int changed = 0;
        IMMDeviceCollection devices = null;
        try
        {
            enumerator.EnumAudioEndpoints(RenderFlow, StateActive, out devices);
            int n;
            devices.GetCount(out n);
            for (int d = 0; d < n; d++)
            {
                IMMDevice dev = null;
                object manager = null;
                IAudioSessionEnumerator list = null;
                try
                {
                    devices.Item(d, out dev);
                    string id;
                    dev.GetId(out id);
                    var iid = SessionManagerIid;
                    dev.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out manager);
                    ((IAudioSessionManager2)manager).GetSessionEnumerator(out list);
                    int count;
                    list.GetCount(out count);
                    for (int s = 0; s < count; s++)
                    {
                        IAudioSessionControl2 ctl = null;
                        try
                        {
                            list.GetSession(s, out ctl);
                            uint pid;
                            ctl.GetProcessId(out pid);
                            if (only == null) { if (!insidePids.Contains(pid)) continue; }
                            else
                            {
                                if (!mutedPids.Contains(pid)) continue;
                                string instance = "";
                                try { ctl.GetSessionInstanceIdentifier(out instance); } catch (Exception) { }
                                if (!only.Contains(id + "|" + pid.ToString(IC) + "|" + instance)) continue;
                            }
                            var volume = (ISimpleAudioVolume)ctl;
                            bool muted;
                            volume.GetMute(out muted);
                            if (muted == mute) continue;
                            var ctx = MuteContext;
                            volume.SetMute(mute, ref ctx);
                            changed++;
                        }
                        catch (Exception) { }
                        finally { Release(ctl); }
                    }
                }
                catch (Exception) { }
                finally { Release(list); Release(manager); Release(dev); }
            }
        }
        finally { Release(devices); }
        return changed;
    }

    // un-mute the sessions this muter muted (all of them, or those of the given pids); returns how many
    static int Restore(IMMDeviceEnumerator enumerator, List<uint> pids)
    {
        if (mutedByMuter.Count == 0 || enumerator == null) return 0;
        HashSet<string> keys = mutedByMuter;
        if (pids != null)
        {
            keys = new HashSet<string>();
            foreach (string k in mutedByMuter)
                foreach (uint pid in pids)
                    if (k.IndexOf("|" + pid.ToString(IC) + "|", StringComparison.Ordinal) >= 0) { keys.Add(k); break; }
            if (keys.Count == 0) return 0;
        }
        int n = 0;
        try { n = SetMuteAll(enumerator, keys, false); }
        catch (Exception e) { Fault("un-mute: " + e.Message); }
        if (pids == null) { mutedByMuter.Clear(); mutedPids.Clear(); }
        else
        {
            foreach (string k in keys) mutedByMuter.Remove(k);
            foreach (uint pid in pids) mutedPids.Remove(pid);
        }
        restoredTotal += n;
        return n;
    }

    // stopHandles: the named events of the stop command (run mode), or null
    static int Loop(double testSeconds, WaitHandle[] stopHandles)
    {
        IMMDeviceEnumerator enumerator = null;
        var clock = Stopwatch.StartNew();
        muterPid = Process.GetCurrentProcess().Id;
        startedMs = NowMs();
        lastInsideMs = -1;
        long nextPoll = 0, nextBeat = 0, nextScan = 0, lastInsideClock = 0;
        seenInside = false;
        string why = "";
        Log("InsideMuter " + Version + " " + mode + ": poll " + PollMs + " ms, heartbeat " + HeartbeatMs + " ms -> " + heartbeatPath +
            (mode == "test" ? ", test for " + testSeconds.ToString(IC) + " s" : ", exits " + lingerSeconds.ToString(IC) + " s after the last INSIDE.exe" + (waitSeconds > 0 ? ", or when none shows up for " + waitSeconds.ToString(IC) + " s" : "")) +
            (keepMuted ? "; the sessions stay muted when it ends (--keep-muted)" : "; sessions it mutes are un-muted when it ends"));
        while (true)
        {
            long now = NowMs();
            bool full = clock.ElapsedMilliseconds >= nextBeat;
            try
            {
                if (clock.ElapsedMilliseconds >= nextScan) { FindInside(); nextScan = clock.ElapsedMilliseconds + (insidePids.Count > 0 ? ScanRunningMs : ScanWaitingMs); }
                else CheckExits();
            }
            catch (Exception e) { Error("process scan: " + e.Message); }
            if (exitedPids.Count > 0)
            {
                // the process is gone but its sessions are still listed for a moment: un-mute them now, or Windows
                // remembers the mute for the game's next start
                if (!keepMuted && enumerator != null)
                {
                    int back = Restore(enumerator, exitedPids);
                    Log(back > 0 ? "INSIDE.exe ended: un-muted " + back + " session(s) it left behind"
                        : "INSIDE.exe ended while muted by this muter and its sessions are gone: Windows may keep it muted at its next start (InsideMuter unmute while the game runs turns it on)");
                }
                exitedPids.Clear();
            }
            Seen(now, "");
            if (insidePids.Count > 0) lastInsideClock = clock.ElapsedMilliseconds;
            if (full) { sessions.Clear(); endpoints.Clear(); }
            if (full || insidePids.Count > 0) polls++;
            try
            {
                if (enumerator == null) { enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo(); Notify(enumerator); }
                Poll(enumerator, full);
            }
            catch (Exception e) { Error("poll: " + e.Message); DropEndpoints(); Unnotify(); Release(enumerator); enumerator = null; }
            Seen(now, " (by its audio session)");
            if (insidePids.Count > 0) lastInsideClock = clock.ElapsedMilliseconds;
            string status = mode == "test" ? "test" : !seenInside ? "waiting" : insidePids.Count > 0 ? "running" : "linger";
            if (full)
            {
                WriteHeartbeat(status);
                nextBeat += HeartbeatMs;
                if (nextBeat <= clock.ElapsedMilliseconds) nextBeat = clock.ElapsedMilliseconds + HeartbeatMs;
            }
            if (stopRequest != 0) { why = "stop asked"; break; }
            if (mode == "test" && clock.Elapsed.TotalSeconds >= testSeconds) { why = "test finished"; break; }
            if (mode == "run" && seenInside && insidePids.Count == 0 && clock.ElapsedMilliseconds - lastInsideClock >= lingerSeconds * 1000) { why = "no INSIDE.exe for " + lingerSeconds.ToString(IC) + " s"; break; }
            if (mode == "run" && !seenInside && waitSeconds > 0 && clock.Elapsed.TotalSeconds >= waitSeconds) { why = "no INSIDE.exe showed up in " + waitSeconds.ToString(IC) + " s (--wait)"; break; }
            nextPoll += PollMs;
            long wait = nextPoll - clock.ElapsedMilliseconds;
            if (wait < 0) { nextPoll = clock.ElapsedMilliseconds; wait = 0; }
            if (stopHandles == null) Thread.Sleep((int)wait);
            else
            {
                int hit = WaitHandle.WaitAny(stopHandles, (int)wait);
                if (hit == 0 && stopRequest == 0) stopRequest = 1;
                else if (hit == 1 && stopRequest == 0) stopRequest = 2;
            }
        }
        bool keep = keepMuted || stopRequest == 2;
        int left = mutedByMuter.Count;
        int restored = 0;
        if (!keep && left > 0)
        {
            if (enumerator == null) { try { enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo(); } catch (Exception e) { Fault("un-mute: " + e.Message); } }
            restored = Restore(enumerator, null);
        }
        sessions.Clear();
        endpoints.Clear();
        DropEndpoints();
        Unnotify();
        Release(enumerator);
        WriteHeartbeat("stopped");
        Log(why + "; exiting" + (left == 0 ? "" : keep ? "; " + left + " session(s) this muter muted stay muted (InsideMuter unmute, or the Windows volume mixer, turns them on)"
            : "; un-muted " + restored + " of the " + left + " session(s) this muter had muted"));
        return 0;
    }

    static void Seen(long now, string how)
    {
        if (insidePids.Count == 0) return;
        if (!seenInside) Log("INSIDE.exe found" + how + ": pid " + JoinPids());
        seenInside = true;
        lastInsideMs = now;
    }

    static bool IsTargetName(string exeName)
    {
        string n = Path.GetFileNameWithoutExtension(exeName ?? "");
        return string.Equals(n, "INSIDE", StringComparison.OrdinalIgnoreCase) || (alsoTarget != null && string.Equals(n, alsoTarget, StringComparison.OrdinalIgnoreCase));
    }

    static void FindInside()
    {
        scanSeen.Clear();
        IntPtr snap = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snap == InvalidHandle || snap == IntPtr.Zero) throw new Exception("process snapshot failed, win32 error " + Marshal.GetLastWin32Error());
        try
        {
            var e = new ProcessEntry();
            e.size = (uint)Marshal.SizeOf(typeof(ProcessEntry));
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                if (IsTargetName(e.exe)) { scanSeen.Add(e.pid); if (!insidePids.Contains(e.pid)) AddInside(e.pid, OpenProcess(QueryLimited | Synchronize, false, e.pid)); }
        }
        finally { CloseHandle(snap); }
        pidScratch.Clear();
        foreach (uint pid in insidePids) if (!scanSeen.Contains(pid) || Exited(insideHandles[pid])) pidScratch.Add(pid);
        foreach (uint pid in pidScratch) DropInside(pid);
        pidScratch.Clear();
        foreach (var kv in otherHandles) if (Exited(kv.Value)) pidScratch.Add(kv.Key);
        foreach (uint pid in pidScratch) { CloseHandle(otherHandles[pid]); otherHandles.Remove(pid); }
        unopened.Clear();
    }

    static void CheckExits()
    {
        pidScratch.Clear();
        foreach (var kv in insideHandles) if (kv.Value != IntPtr.Zero && Exited(kv.Value)) pidScratch.Add(kv.Key);
        foreach (uint pid in pidScratch) DropInside(pid);
    }

    static bool Exited(IntPtr handle)
    {
        return handle != IntPtr.Zero && WaitForSingleObject(handle, 0) == WaitSignaled;
    }

    static void AddInside(uint pid, IntPtr handle)
    {
        insidePids.Add(pid);
        insideHandles[pid] = handle;
        string path;
        if (!processPaths.TryGetValue(pid, out path) || path.Length == 0) processPaths[pid] = handle != IntPtr.Zero ? ImagePath(handle) : "";
    }

    static void DropInside(uint pid)
    {
        IntPtr h;
        if (insideHandles.TryGetValue(pid, out h) && h != IntPtr.Zero) CloseHandle(h);
        insideHandles.Remove(pid);
        insidePids.Remove(pid);
        if (mutedPids.Contains(pid)) exitedPids.Add(pid);
    }

    static string ImagePath(IntPtr handle)
    {
        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        return QueryFullProcessImageNameW(handle, 0, sb, ref size) ? sb.ToString(0, size) : "";
    }

    static bool IsTargetPid(uint pid)
    {
        if (insidePids.Contains(pid)) return true;
        if (pid == 0 || otherHandles.ContainsKey(pid) || unopened.Contains(pid)) return false;
        IntPtr h = OpenProcess(QueryLimited | Synchronize, false, pid);
        if (h == IntPtr.Zero) { unopened.Add(pid); return false; }
        string path = ImagePath(h);
        if (IsTargetName(path)) { processPaths[pid] = path; AddInside(pid, h); return true; }
        otherHandles[pid] = h;
        return false;
    }

    static void Notify(IMMDeviceEnumerator enumerator)
    {
        DeviceEvents.dirty = true;
        try { enumerator.RegisterEndpointNotificationCallback(deviceEvents); notifying = enumerator; }
        catch (Exception e) { Fault("device notifications unavailable (endpoint list read every poll): " + e.Message); notifying = null; }
    }

    static void Unnotify()
    {
        if (notifying == null) return;
        try { notifying.UnregisterEndpointNotificationCallback(deviceEvents); } catch (Exception) { }
        notifying = null;
    }

    static readonly List<EndpointCache> endpointOrder = new List<EndpointCache>();

    static void Poll(IMMDeviceEnumerator enumerator, bool full)
    {
        if (!full && !DeviceEvents.dirty && notifying != null && endpointCache.Count > 0)
        {
            endpointOrder.Clear();
            endpointOrder.AddRange(endpointCache.Values);
            foreach (var c in endpointOrder) PollCached(c);
            return;
        }
        DeviceEvents.dirty = false;
        IMMDeviceCollection devices = null;
        try
        {
            enumerator.EnumAudioEndpoints(RenderFlow, StateActive, out devices);
            int n;
            devices.GetCount(out n);
            string defaultId = full ? DefaultEndpointId(enumerator) : "";
            endpointPass++;
            for (int d = 0; d < n; d++) PollEndpoint(devices, d, full, defaultId);
            endpointGone.Clear();
            foreach (var kv in endpointCache) if (kv.Value.pass != endpointPass) endpointGone.Add(kv.Key);
            foreach (var id in endpointGone) DropEndpoint(id);
        }
        finally { Release(devices); }
    }

    static EndpointCache CachedEndpoint(IMMDevice dev, string id)
    {
        EndpointCache c;
        if (endpointCache.TryGetValue(id, out c)) return c;
        c = new EndpointCache();
        c.id = id;
        c.name = EndpointName(dev, id);
        var iid = SessionManagerIid;
        dev.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out c.manager);
        try { var mid = MeterIid; dev.Activate(ref mid, ClsCtxAll, IntPtr.Zero, out c.meter); } catch (Exception) { c.meter = null; }
        endpointCache[id] = c;
        return c;
    }

    static void DropEndpoint(string id)
    {
        EndpointCache c;
        if (!endpointCache.TryGetValue(id, out c)) return;
        endpointCache.Remove(id);
        Release(c.meter);
        Release(c.manager);
    }

    static void DropEndpoints()
    {
        endpointGone.Clear();
        endpointGone.AddRange(endpointCache.Keys);
        foreach (var id in endpointGone) DropEndpoint(id);
    }

    static void PollEndpoint(IMMDeviceCollection devices, int index, bool full, string defaultId)
    {
        IMMDevice dev = null;
        IAudioSessionEnumerator list = null;
        string id = null;
        try
        {
            devices.Item(index, out dev);
            dev.GetId(out id);
            var cache = CachedEndpoint(dev, id);
            cache.pass = endpointPass;
            string name = cache.name;
            EndpointInfo ep = null;
            if (full)
            {
                ep = new EndpointInfo();
                ep.id = id;
                ep.name = name;
                ep.isDefault = id == defaultId;
                ep.peak = CachedPeak(cache);
                endpoints.Add(ep);
            }
            ((IAudioSessionManager2)cache.manager).GetSessionEnumerator(out list);
            int count;
            list.GetCount(out count);
            cache.targets = 0;
            for (int s = 0; s < count; s++) PollSession(list, s, cache, ep);
            cache.lastTargets = cache.targets;
        }
        catch (Exception e) { Error("endpoint " + index + ": " + e.Message); if (id != null) DropEndpoint(id); }
        finally { Release(list); Release(dev); }
    }

    static void PollCached(EndpointCache c)
    {
        IAudioSessionEnumerator list = null;
        try
        {
            ((IAudioSessionManager2)c.manager).GetSessionEnumerator(out list);
            int count;
            list.GetCount(out count);
            c.targets = 0;
            for (int s = 0; s < count; s++) PollSession(list, s, c, null);
            c.lastTargets = c.targets;
        }
        catch (Exception e) { Error("endpoint '" + c.name + "': " + e.Message); DropEndpoint(c.id); DeviceEvents.dirty = true; }
        finally { Release(list); }
    }

    static float CachedPeak(EndpointCache c)
    {
        if (c.meter == null) return -1f;
        try { float peak; ((IAudioMeterInformation)c.meter).GetPeakValue(out peak); return peak; }
        catch (Exception) { return -1f; }
    }

    static void PollSession(IAudioSessionEnumerator list, int index, EndpointCache cache, EndpointInfo ep)
    {
        IAudioSessionControl2 ctl = null;
        string endpointId = cache.id, endpointName = cache.name;
        try
        {
            list.GetSession(index, out ctl);
            uint pid;
            ctl.GetProcessId(out pid);
            if (!IsTargetPid(pid)) return;
            var volume = (ISimpleAudioVolume)ctl;
            bool muted;
            volume.GetMute(out muted);
            var ctx = MuteContext;
            if (!muted) volume.SetMute(true, ref ctx);
            cache.targets++;
            // a muted session on an endpoint whose number of INSIDE sessions did not change is one that was handled
            // before: nothing to record, so its name is not fetched
            if (muted && ep == null && cache.targets <= cache.lastTargets) return;
            string instance = "";
            try { ctl.GetSessionInstanceIdentifier(out instance); } catch (Exception) { }
            string key = endpointId + "|" + pid.ToString(IC) + "|" + instance;
            int seen;
            unmutedSeen.TryGetValue(key, out seen);
            if (!muted)
            {
                seen++;
                unmutedSeen[key] = seen;
                unmutedSeenTotal++;
                lastUnmutedSeenMs = NowMs();
                mutedByMuter.Add(key);
                mutedPids.Add(pid);
            }
            if (reported.Add(key)) Log("INSIDE pid " + pid + " session on '" + endpointName + "': was " + (muted ? "muted" : "audible, muted now"));
            else if (!muted) Log("INSIDE pid " + pid + " session on '" + endpointName + "' was unmuted again, muted now (" + seen + " times)");
            if (ep == null) return;
            var info = new SessionInfo();
            info.pid = pid;
            info.endpoint = endpointId;
            info.endpointName = endpointName;
            info.instance = instance;
            info.unmutedSeen = seen;
            bool after;
            volume.GetMute(out after);
            info.muted = after;
            volume.GetMasterVolume(out info.volume);
            int state;
            ctl.GetState(out state);
            info.state = state == 0 ? "inactive" : state == 1 ? "active" : state == 2 ? "expired" : "state" + state;
            info.peak = -1f;
            var meter = ctl as IAudioMeterInformation;
            if (meter != null) { float peak; meter.GetPeakValue(out peak); info.peak = peak; }
            sessions.Add(info);
            ep.insideSessions++;
        }
        catch (Exception e) { Error("session " + index + " on '" + endpointName + "': " + e.Message); }
        finally { Release(ctl); }
    }

    static string DefaultEndpointId(IMMDeviceEnumerator enumerator)
    {
        IMMDevice dev = null;
        try
        {
            enumerator.GetDefaultAudioEndpoint(RenderFlow, RoleConsole, out dev);
            string id;
            dev.GetId(out id);
            return id;
        }
        catch (Exception) { return ""; }
        finally { Release(dev); }
    }

    static string EndpointName(IMMDevice dev, string id)
    {
        string name;
        if (endpointNames.TryGetValue(id, out name)) return name;
        name = "";
        IPropertyStore store = null;
        try
        {
            dev.OpenPropertyStore(0, out store);
            var key = new PropertyKey();
            key.fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
            key.pid = 14;
            PropVariant value;
            store.GetValue(ref key, out value);
            if (value.vt == VtLpwstr && value.p != IntPtr.Zero) name = Marshal.PtrToStringUni(value.p);
            PropVariantClear(ref value);
        }
        catch (Exception) { }
        finally { Release(store); }
        endpointNames[id] = name;
        return name;
    }

    static float EndpointPeak(IMMDevice dev)
    {
        object meter = null;
        try
        {
            var iid = MeterIid;
            dev.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out meter);
            float peak;
            ((IAudioMeterInformation)meter).GetPeakValue(out peak);
            return peak;
        }
        catch (Exception) { return -1f; }
        finally { Release(meter); }
    }

    static void WriteHeartbeat(string status)
    {
        seq++;
        long now = NowMs();
        int muted = 0, unmuted = 0, active = 0;
        float maxPeak = 0f;
        foreach (var s in sessions)
        {
            if (s.muted) muted++; else unmuted++;
            if (s.state == "active") active++;
            if (s.peak > maxPeak) maxPeak = s.peak;
        }
        var sb = new StringBuilder(2048);
        sb.Append("{\n");
        sb.Append("  \"format\": \"insidemuter.heartbeat/1\",\n");
        sb.Append("  \"muterVersion\": \"").Append(Version).Append("\",\n");
        sb.Append("  \"muterPid\": ").Append(N(muterPid)).Append(",\n");
        sb.Append("  \"mode\": \"").Append(mode).Append("\",\n");
        sb.Append("  \"targets\": [\"INSIDE\"").Append(alsoTarget != null ? ", " + Str(alsoTarget) : "").Append("],\n");
        sb.Append("  \"status\": \"").Append(status).Append("\",\n");
        sb.Append("  \"seq\": ").Append(N(seq)).Append(",\n");
        sb.Append("  \"time\": \"").Append(Epoch.AddMilliseconds(now).ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'", IC)).Append("\",\n");
        sb.Append("  \"unixMs\": ").Append(N(now)).Append(",\n");
        sb.Append("  \"startedUnixMs\": ").Append(N(startedMs)).Append(",\n");
        sb.Append("  \"lastInsideUnixMs\": ").Append(N(lastInsideMs)).Append(",\n");
        sb.Append("  \"pollMs\": ").Append(N(PollMs)).Append(", \"heartbeatMs\": ").Append(N(HeartbeatMs)).Append(", \"lingerS\": ").Append(Num(lingerSeconds)).Append(", \"waitS\": ").Append(Num(waitSeconds)).Append(",\n");
        sb.Append("  \"polls\": ").Append(N(polls)).Append(", \"pollErrors\": ").Append(N(pollErrors)).Append(", \"writeErrors\": ").Append(N(writeErrors)).Append(",\n");
        sb.Append("  \"lastError\": ").Append(Str(lastError)).Append(",\n");
        sb.Append("  \"unmutedSeenTotal\": ").Append(N(unmutedSeenTotal)).Append(", \"lastUnmutedSeenUnixMs\": ").Append(N(lastUnmutedSeenMs)).Append(",\n");
        sb.Append("  \"keepMuted\": ").Append(keepMuted ? "true" : "false").Append(", \"mutedByMuter\": ").Append(N(mutedByMuter.Count)).Append(", \"restoredTotal\": ").Append(N(restoredTotal)).Append(",\n");
        sb.Append("  \"inside\": [");
        bool first = true;
        foreach (uint pid in insidePids)
        {
            string path;
            processPaths.TryGetValue(pid, out path);
            sb.Append(first ? "" : ", ").Append("{\"pid\": ").Append(N(pid)).Append(", \"path\": ").Append(Str(path ?? "")).Append("}");
            first = false;
        }
        sb.Append("],\n");
        sb.Append("  \"summary\": {\"sessions\": ").Append(N(sessions.Count)).Append(", \"muted\": ").Append(N(muted)).Append(", \"unmuted\": ").Append(N(unmuted))
          .Append(", \"active\": ").Append(N(active)).Append(", \"maxSessionPeak\": ").Append(Num(maxPeak)).Append("},\n");
        sb.Append("  \"sessions\": [");
        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            sb.Append(i == 0 ? "\n" : ",\n").Append("    {\"pid\": ").Append(N(s.pid)).Append(", \"endpoint\": ").Append(Str(s.endpoint)).Append(", \"endpointName\": ").Append(Str(s.endpointName))
              .Append(", \"state\": \"").Append(s.state).Append("\", \"muted\": ").Append(s.muted ? "true" : "false").Append(", \"volume\": ").Append(Num(s.volume))
              .Append(", \"peak\": ").Append(Num(s.peak)).Append(", \"unmutedSeen\": ").Append(N(s.unmutedSeen)).Append(", \"instance\": ").Append(Str(s.instance)).Append("}");
        }
        sb.Append(sessions.Count > 0 ? "\n  ],\n" : "],\n");
        sb.Append("  \"endpoints\": [");
        for (int i = 0; i < endpoints.Count; i++)
        {
            var e = endpoints[i];
            sb.Append(i == 0 ? "\n" : ",\n").Append("    {\"id\": ").Append(Str(e.id)).Append(", \"name\": ").Append(Str(e.name)).Append(", \"default\": ").Append(e.isDefault ? "true" : "false")
              .Append(", \"peak\": ").Append(Num(e.peak)).Append(", \"insideSessions\": ").Append(N(e.insideSessions)).Append("}");
        }
        sb.Append(endpoints.Count > 0 ? "\n  ]\n" : "]\n");
        sb.Append("}\n");
        string tmp = heartbeatPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(heartbeatPath));
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            for (int attempt = 0; ; attempt++)
            {
                if (MoveFileExW(tmp, heartbeatPath, 1)) break;
                if (attempt >= 3) { writeErrors++; Fault("heartbeat replace failed, win32 error " + Marshal.GetLastWin32Error()); break; }
                Thread.Sleep(5);
            }
        }
        catch (Exception e) { writeErrors++; Fault("heartbeat: " + e.Message); }
    }

    static void List()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        foreach (int mask in new[] { 1, 15 })
        {
            IMMDeviceCollection all;
            enumerator.EnumAudioEndpoints(RenderFlow, mask, out all);
            int total;
            all.GetCount(out total);
            Console.WriteLine("state mask " + mask + ": " + total + " render endpoints");
            Release(all);
        }
        string defaultId = DefaultEndpointId(enumerator);
        IMMDeviceCollection devices;
        enumerator.EnumAudioEndpoints(RenderFlow, StateActive, out devices);
        int n;
        devices.GetCount(out n);
        for (int d = 0; d < n; d++)
        {
            try { ListEndpoint(devices, d, defaultId); }
            catch (Exception e) { Console.WriteLine("endpoint " + d + ": error " + e.Message); }
        }
        Release(devices);
        Console.WriteLine("INSIDE.exe processes: " + Process.GetProcessesByName("INSIDE").Length);
    }

    static void ListEndpoint(IMMDeviceCollection devices, int d, string defaultId)
    {
        IMMDevice dev;
        devices.Item(d, out dev);
        string id;
        dev.GetId(out id);
        Console.WriteLine("endpoint " + d + (id == defaultId ? " (default)" : "") + " '" + EndpointName(dev, id) + "' " + id + " peak " + Num(EndpointPeak(dev)));
        object manager;
        var iid = SessionManagerIid;
        dev.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out manager);
        IAudioSessionEnumerator list;
        ((IAudioSessionManager2)manager).GetSessionEnumerator(out list);
        int count;
        list.GetCount(out count);
        for (int s = 0; s < count; s++)
        {
            IAudioSessionControl2 ctl;
            list.GetSession(s, out ctl);
            uint pid;
            ctl.GetProcessId(out pid);
            string name = "?";
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch (Exception) { }
            int state;
            ctl.GetState(out state);
            bool muted;
            ((ISimpleAudioVolume)ctl).GetMute(out muted);
            float volume;
            ((ISimpleAudioVolume)ctl).GetMasterVolume(out volume);
            float peak = -1f;
            var meter = ctl as IAudioMeterInformation;
            if (meter != null) meter.GetPeakValue(out peak);
            Console.WriteLine("  session " + s + " pid " + pid + " " + name + " state " + state + (muted ? " muted" : " audible") + " volume " + Num(volume) + " peak " + Num(peak));
            Release(ctl);
        }
        Release(list);
        Release(manager);
        Release(dev);
    }

    static string JoinPids()
    {
        var sb = new StringBuilder();
        foreach (uint pid in insidePids) sb.Append(sb.Length > 0 ? "," : "").Append(N(pid));
        return sb.ToString();
    }

    // whole numbers in JSON and logs: invariant culture (some cultures write the minus sign as U+2212)
    static string N(long v)
    {
        return v.ToString(IC);
    }

    static string Num(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "-1";
        return v.ToString("0.#########", IC);
    }

    static string Str(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4", IC));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    static long NowMs()
    {
        return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds;
    }

    static void Log(string message)
    {
        Console.WriteLine(DateTime.Now.ToString("yyyy'-'MM'-'dd HH':'mm':'ss'.'fff", IC) + " " + message);
    }

    static void Error(string message)
    {
        pollErrors++;
        Fault(message);
    }

    static void Fault(string message)
    {
        long now = NowMs();
        if (message != lastError || now - lastErrorLogMs > 5000) { Log("error: " + message); lastErrorLogMs = now; }
        lastError = message;
    }

    static void Release(object o)
    {
        if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }
}
