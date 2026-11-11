using System;
using System.Runtime.InteropServices;

namespace InsideDev
{
    // Free space of the drive that holds a folder: the recorder's check before a recording starts and the writer thread's
    // watch while it runs (record.minFreeMB). kernel32 GetDiskFreeSpaceExW, because DriveInfo is not dependable on the
    // game's Mono. Any thread; -1 = unknown (folder missing, call failed, not Windows).
    internal static class SrecDisk
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeForCaller, out ulong total, out ulong totalFree);

        public static long FreeBytes(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return -1;
            try
            {
                char last = folder[folder.Length - 1];
                if (last != '\\' && last != '/') folder += "\\";
                ulong free, total, totalFree;
                if (!GetDiskFreeSpaceExW(folder, out free, out total, out totalFree)) return -1;
                return free > long.MaxValue ? long.MaxValue : (long)free;
            }
            catch (Exception) { return -1; }
        }
    }
}
