using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace InsideDev
{
    // Background file work of a replay run, so that the game thread never waits for the disk. A director hold becomes an
    // NTFS hard link to the source camera's frame of that step (no data is copied, no disk space is used); where the volume
    // has no hard links (FAT, exFAT, another volume) the frame is copied instead, still on this thread. The frames an earlier
    // run left in a camera folder are removed before the pass renders into it: a frame file may be a hard link, and writing
    // through it would change the other camera's frame as well. One thread, jobs in the order they were added; the game
    // thread reads the counters with Take.
    internal sealed class McbXFileWorker
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr security);

        const byte KindHold = 0, KindClean = 1;
        // Win32 errors that say "no hard links on this volume": ERROR_INVALID_FUNCTION, ERROR_NOT_SAME_DEVICE, ERROR_NOT_SUPPORTED
        const int NoFunction = 1, OtherDevice = 17, NotSupported = 50, NoEntryPoint = -1;
        const int GiveUpAfter = 8;

        struct Job { public byte kind; public string a, b; }

        readonly Queue<Job> q = new Queue<Job>();
        readonly object gate = new object();
        Thread thread;
        bool busy, quit, link;
        int linked, copied, failed, removed, linkFails;
        string firstError, note;

        public McbXFileWorker(bool hardLinks) { link = hardLinks; }

        // dst becomes the frame src (link, else copy); an existing dst is replaced
        public void Hold(string src, string dst) { Add(new Job { kind = KindHold, a = src, b = dst }); }

        // removes the frame files (<digits><ext>) of dir
        public void Clean(string dir, string ext) { Add(new Job { kind = KindClean, a = dir, b = ext }); }

        void Add(Job j)
        {
            lock (gate)
            {
                if (quit) return;
                q.Enqueue(j);
                if (thread == null)
                {
                    thread = new Thread(Loop);
                    thread.IsBackground = true;
                    thread.Name = "mcam files";
                    thread.Start();
                }
                Monitor.Pulse(gate);
            }
        }

        public bool Idle { get { lock (gate) return q.Count == 0 && !busy; } }

        public int Pending { get { lock (gate) return q.Count + (busy ? 1 : 0); } }

        // Counters since the last Take. error = the first failure, note = why hard links are not used (null = they are).
        public void Take(out int links, out int copies, out int fails, out int removals, out string error, out string linkNote)
        {
            lock (gate)
            {
                links = linked; copies = copied; fails = failed; removals = removed; error = firstError; linkNote = note;
                linked = copied = failed = removed = 0;
                firstError = null; note = null;
            }
        }

        // drop: forget the jobs that have not started. Waits up to waitMs for the thread; it ends by itself after the job
        // it is working on in any case.
        public void Close(bool drop, int waitMs)
        {
            Thread t;
            lock (gate)
            {
                quit = true;
                if (drop) q.Clear();
                t = thread;
                Monitor.PulseAll(gate);
            }
            if (t != null && waitMs > 0) { try { t.Join(waitMs); } catch { } }
        }

        void Loop()
        {
            while (true)
            {
                Job j;
                lock (gate)
                {
                    while (q.Count == 0 && !quit) Monitor.Wait(gate);
                    if (q.Count == 0) return;
                    j = q.Dequeue();
                    busy = true;
                }
                try
                {
                    if (j.kind == KindHold) DoHold(j.a, j.b); else DoClean(j.a, j.b);
                }
                catch (Exception e)
                {
                    lock (gate)
                    {
                        failed++;
                        if (firstError == null) firstError = (j.kind == KindHold ? "hold " + j.a + " -> " + j.b : "clean " + j.a) + ": " + e.GetType().Name + ": " + e.Message;
                    }
                }
                lock (gate) { busy = false; }
            }
        }

        void DoHold(string src, string dst)
        {
            if (File.Exists(dst)) File.Delete(dst);
            bool tryLink;
            lock (gate) tryLink = link;
            if (tryLink)
            {
                bool ok = false;
                int err = 0;
                try
                {
                    ok = CreateHardLinkW(dst, src, IntPtr.Zero);
                    if (!ok) err = Marshal.GetLastWin32Error();
                }
                catch (Exception) { err = NoEntryPoint; }
                lock (gate)
                {
                    if (ok) { linked++; linkFails = 0; return; }
                    linkFails++;
                    if (err == NoFunction || err == OtherDevice || err == NotSupported || err == NoEntryPoint || linkFails >= GiveUpAfter)
                    {
                        link = false;
                        note = (err == NoEntryPoint ? "CreateHardLinkW is not available" : "hard links are not available for " + dst + " (Win32 error " + Fmt.I(err) + ")") + ": holds are copied";
                    }
                }
            }
            File.Copy(src, dst, true);
            lock (gate) copied++;
        }

        void DoClean(string dir, string ext)
        {
            if (!Directory.Exists(dir)) return;
            int n = 0;
            foreach (var f in Directory.GetFiles(dir, "*" + ext))
            {
                if (!string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase) || !IsFrameName(Path.GetFileNameWithoutExtension(f))) continue;
                File.Delete(f);
                n++;
            }
            lock (gate) removed += n;
        }

        // frame files are <step:D5><ext>: digits only, five or more
        public static bool IsFrameName(string name)
        {
            if (name == null || name.Length < 5) return false;
            for (int i = 0; i < name.Length; i++) if (name[i] < '0' || name[i] > '9') return false;
            return true;
        }
    }

    // Writes the newest text given for a file on a background thread (progress.json of a run): the game thread only hands
    // the string over; a text that was not written yet is replaced by the next one. The file is replaced in one step, so a
    // watcher never reads half of it.
    internal sealed class McbXLatestFile
    {
        readonly object gate = new object();
        Thread thread;
        string path, text;
        bool quit;

        public void Post(string file, string content)
        {
            lock (gate)
            {
                if (quit) return;
                path = file; text = content;
                if (thread == null)
                {
                    thread = new Thread(Loop);
                    thread.IsBackground = true;
                    thread.Name = "mcam progress";
                    thread.Start();
                }
                Monitor.Pulse(gate);
            }
        }

        // The text handed over last is still written; waits up to waitMs for that.
        public void Close(int waitMs)
        {
            Thread t;
            lock (gate)
            {
                quit = true;
                t = thread;
                Monitor.PulseAll(gate);
            }
            if (t != null && waitMs > 0) { try { t.Join(waitMs); } catch { } }
        }

        void Loop()
        {
            while (true)
            {
                string p, s;
                lock (gate)
                {
                    while (text == null && !quit) Monitor.Wait(gate);
                    if (text == null) return;
                    p = path; s = text;
                    text = null;
                }
                try { SafeFile.WriteAllText(p, s); } catch { }
            }
        }
    }

    // Keys of a MultiCam spec that are read from the spec text itself (the typed spec classes do not carry them yet):
    // stream.rescan, replay.play.holds, replay.play.planChunkMs. Parsed once per run.
    internal sealed class McbXSpecKeys
    {
        readonly Json.Obj root;

        public McbXSpecKeys(MultiCamSpec spec)
        {
            try { root = spec != null && !string.IsNullOrEmpty(spec.text) ? Json.Parse(spec.text) as Json.Obj : null; }
            catch { root = null; }
        }

        object At(string[] path)
        {
            object cur = root;
            for (int i = 0; i < path.Length; i++)
            {
                var o = cur as Json.Obj;
                if (o == null) return null;
                cur = o[path[i]];
            }
            return cur;
        }

        public string Str(string def, params string[] path)
        {
            var v = At(path);
            return v is string ? ((string)v).Trim() : def;
        }

        public double Num(double def, params string[] path)
        {
            var v = At(path);
            return v is double ? (double)v : def;
        }

        public bool Has(params string[] path) { return At(path) != null; }
    }
}
