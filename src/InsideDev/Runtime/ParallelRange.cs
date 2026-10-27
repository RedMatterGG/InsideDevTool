using System;
using System.Threading;

namespace InsideDev
{
    // Fork/join over an index range for pure computations (no Unity API) on Unity 5.0's Mono 3.5 profile, which has no
    // Parallel.For or CountdownEvent. The calling thread runs chunk 0 itself and waits for the workers; workers sleep on
    // their own event between runs. Used by SceneReplayer to interpolate the recorded transforms of a frame.
    internal sealed class ParallelRange
    {
        readonly Thread[] threads;
        readonly AutoResetEvent[] go;
        readonly ManualResetEvent done = new ManualResetEvent(false);
        Action<int, int> body;
        int count, pending, chunks;
        volatile bool quit;
        volatile string error;

        public int Workers { get { return threads.Length; } }

        public ParallelRange(int workers, string name)
        {
            threads = new Thread[Math.Max(0, workers)];
            go = new AutoResetEvent[threads.Length];
            for (int i = 0; i < threads.Length; i++)
            {
                int k = i;
                go[i] = new AutoResetEvent(false);
                threads[i] = new Thread(() => Loop(k));
                threads[i].IsBackground = true;
                threads[i].Name = name + " " + (i + 1);
                threads[i].Start();
            }
        }

        public static int DefaultWorkers() { return Math.Max(1, Math.Min(6, Environment.ProcessorCount - 2)); }

        public void Run(int n, Action<int, int> fn)
        {
            if (n <= 0) return;
            if (threads.Length == 0 || n < 64) { fn(0, n); return; }
            body = fn; count = n; error = null;
            chunks = threads.Length + 1;
            pending = chunks;
            done.Reset();
            for (int i = 0; i < threads.Length; i++) go[i].Set();
            Chunk(0);
            done.WaitOne();
            body = null;
            if (error != null) throw new Exception("parallel range: " + error);
        }

        void Chunk(int k)
        {
            int a = (int)((long)count * k / chunks), b = (int)((long)count * (k + 1) / chunks);
            try { if (b > a) body(a, b); }
            catch (Exception e) { if (error == null) error = e.GetType().Name + ": " + e.Message; }
            if (Interlocked.Decrement(ref pending) == 0) done.Set();
        }

        void Loop(int k)
        {
            while (true)
            {
                go[k].WaitOne();
                if (quit) return;
                Chunk(k + 1);
            }
        }

        public void Dispose()
        {
            quit = true;
            for (int i = 0; i < threads.Length; i++) go[i].Set();
        }
    }
}
