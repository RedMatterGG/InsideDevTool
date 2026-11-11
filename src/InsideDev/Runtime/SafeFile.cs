using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace InsideDev
{
    // Writes that cannot leave a half-written or empty file behind: the text goes to "<path>.tmp" and replaces the file in
    // one step. With backup the old content is kept once as "<path>.bak". For settings, bookmarks, mods, spawn lists.
    public static class SafeFile
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileExW(string from, string to, uint flags);
        const uint ReplaceExisting = 1, WriteThrough = 8;

        public static void WriteAllText(string path, string text) { WriteAllBytes(path, Utf8.GetBytes(text ?? ""), false); }
        public static void WriteAllText(string path, string text, bool backup) { WriteAllBytes(path, Utf8.GetBytes(text ?? ""), backup); }

        public static void WriteAllBytes(string path, byte[] data, bool backup)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush();
            }
            if (backup && File.Exists(path))
            {
                try { File.Copy(path, path + ".bak", true); } catch { }
            }
            Replace(tmp, path);
        }

        // Moves tmp over path; falls back to delete + move when the kernel call is not available.
        public static void Replace(string tmp, string path)
        {
            bool ok = false;
            try { ok = MoveFileExW(tmp, path, ReplaceExisting | WriteThrough); } catch { ok = false; }
            if (ok) return;
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // Reads a text file; when it is missing or unreadable and a ".bak" exists, reads that and says so.
        public static string ReadAllTextOrBackup(string path, out bool fromBackup)
        {
            fromBackup = false;
            try { if (File.Exists(path)) return File.ReadAllText(path); } catch { }
            string bak = path + ".bak";
            if (File.Exists(bak)) { fromBackup = true; return File.ReadAllText(bak); }
            return null;
        }
    }
}
