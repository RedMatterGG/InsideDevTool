using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace InsideDev
{
    // The one place that finds ffmpeg. Order: editor.cfg "ffmpeg.path", environment INSIDEDEV_FFMPEG,
    // <game>\_mod\ffmpeg\ffmpeg.exe, the PATH, C:\ffmpeg\bin\ffmpeg.exe. A path that comes from a spec file or a client is
    // accepted only through SafeExe: the file must be named ffmpeg.exe, be a full local path and exist.
    public static class Ffmpeg
    {
        static readonly Dictionary<string, string> encoders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string ModDir { get { return Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"); } }

        // Main thread (reads editor.cfg and Application.dataPath). null when nothing was found; tried lists the places.
        public static string Find(out string tried)
        {
            var seen = new List<string>();
            string why;
            foreach (var c in new[] { EditorState.Get("ffmpeg.path", ""), Environment.GetEnvironmentVariable("INSIDEDEV_FFMPEG"), Path.Combine(ModDir, "ffmpeg\\ffmpeg.exe") })
            {
                if (string.IsNullOrEmpty(c)) continue;
                if (SafeExe(c, out why)) { tried = null; return c; }
                seen.Add(c + " (" + why + ")");
            }
            string onPath = OnPath("ffmpeg.exe");
            if (onPath != null) { tried = null; return onPath; }
            seen.Add("PATH");
            const string fixedPlace = "C:\\ffmpeg\\bin\\ffmpeg.exe";
            if (File.Exists(fixedPlace)) { tried = null; return fixedPlace; }
            seen.Add(fixedPlace);
            tried = string.Join(", ", seen.ToArray());
            return null;
        }

        public static string Find() { string t; return Find(out t); }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW")]
        static extern uint GetDriveTypeW(string root);
        const uint DriveFixed = 3;
        static readonly string[] DriveKinds = { "of unknown type", "missing", "removable", "fixed", "a network drive", "a CD / DVD drive", "a RAM disk" };

        // GetDriveTypeW of "X:\"; refusal text, or null for a fixed local drive. Any thread.
        public static string DriveRefusal(char letter)
        {
            char d = char.ToUpperInvariant(letter);
            uint t;
            try { t = GetDriveTypeW(d + ":\\"); }
            catch (Exception e) { return "the type of drive " + d + ": could not be read (" + e.GetType().Name + ")"; }
            if (t == DriveFixed) return null;
            return "drive " + d + ": is " + (t < DriveKinds.Length ? DriveKinds[t] : "of type " + t) + "; only a fixed local drive is accepted";
        }

        // True when path may be started as ffmpeg: named ffmpeg.exe, a full path with a drive letter (no \\server\share)
        // on a fixed local drive (GetDriveTypeW: no mapped network drive, removable drive or CD), no ':' after the drive,
        // existing. Any thread.
        public static bool SafeExe(string path, out string why)
        {
            why = null;
            if (string.IsNullOrEmpty(path)) { why = "empty"; return false; }
            string p = path.Trim().Trim('"');
            string name;
            try { name = Path.GetFileName(p); } catch { why = "not a path"; return false; }
            if (!string.Equals(name, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) { why = "the file is not named ffmpeg.exe"; return false; }
            if (p.StartsWith("\\\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal)) { why = "network paths are not accepted"; return false; }
            if (p.Length < 3 || p[1] != ':' || (p[2] != '\\' && p[2] != '/') || !char.IsLetter(p[0])) { why = "not a full path with a drive letter"; return false; }
            if (p.IndexOf(':', 2) >= 0) { why = "a ':' after the drive letter (an alternate data stream) is not accepted"; return false; }
            string drive = DriveRefusal(p[0]);
            if (drive != null) { why = drive; return false; }
            if (!File.Exists(p)) { why = "not found"; return false; }
            return true;
        }

        // Any thread.
        public static string OnPath(string exe)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var d in path.Split(';'))
            {
                string dir = d.Trim().Trim('"');
                if (dir.Length == 0) continue;
                try
                {
                    string f = Path.Combine(dir, exe);
                    if (File.Exists(f)) return f;
                }
                catch { }
            }
            return null;
        }

        // Quotes one command line argument for CreateProcess (spaces, quotes, trailing backslashes).
        public static string Quote(string arg)
        {
            if (arg == null) return "\"\"";
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new System.Text.StringBuilder("\"");
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
                sb.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }

        // Runs "ffmpeg -hide_banner -encoders" once per ffmpeg path (blocks up to 8 s the first time: call it from a check
        // command or a worker, not from a frame loop) and answers whether the encoder is listed. error is set when ffmpeg
        // could not be asked. Any thread.
        public static bool HasEncoder(string ffmpeg, string encoder, out string error)
        {
            error = null;
            string list;
            bool cached;
            lock (encoders) cached = encoders.TryGetValue(ffmpeg, out list);
            if (!cached)
            {
                var sb = new System.Text.StringBuilder();
                try
                {
                    var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -encoders") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    using (var p = Process.Start(psi))
                    {
                        ChildJob.Add(p);
                        p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.Append(e.Data).Append('\n'); };
                        p.ErrorDataReceived += (s, e) => { };
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } error = "ffmpeg -encoders did not answer in 8 s"; return false; }
                        p.WaitForExit();
                    }
                }
                catch (Exception e) { error = "cannot run " + ffmpeg + ": " + e.Message; return false; }
                lock (sb) list = sb.ToString();
                lock (encoders) encoders[ffmpeg] = list;
            }
            foreach (var line in (list ?? "").Split('\n'))
            {
                var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && string.Equals(parts[1], encoder, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    // A Windows job object with KILL_ON_JOB_CLOSE. Every child process added to it ends when the game process ends for any
    // reason (quit, crash, kill), so no ffmpeg keeps recording or holding files after the game is gone.
    public static class ChildJob
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        const int ExtendedLimitInformation = 9;
        const uint KillOnJobClose = 0x2000;
        static IntPtr job;
        static bool failed;
        static readonly object gate = new object();

        // False when the job could not be made or the process could not be added (the process then runs as before).
        public static bool Add(Process p)
        {
            if (p == null) return false;
            try
            {
                lock (gate)
                {
                    if (failed) return false;
                    if (job == IntPtr.Zero)
                    {
                        job = CreateJobObjectW(IntPtr.Zero, null);
                        if (job == IntPtr.Zero) { failed = true; return false; }
                        var info = new ExtendedLimits();
                        info.Basic.LimitFlags = KillOnJobClose;
                        if (!SetInformationJobObject(job, ExtendedLimitInformation, ref info, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                        {
                            failed = true;
                            DevLog.Write("child job: SetInformationJobObject failed, error " + Marshal.GetLastWin32Error());
                            return false;
                        }
                    }
                    return AssignProcessToJobObject(job, p.Handle);
                }
            }
            catch (Exception e)
            {
                failed = true;
                DevLog.Write("child job: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }
    }
}
