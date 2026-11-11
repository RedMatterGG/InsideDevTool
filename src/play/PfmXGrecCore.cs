using System;
using System.Collections.Generic;

namespace InsideDev.Play
{
    // PfmXGrecCore.cs (owner TIME): the Unity-free half of grec (GameRec.cs), compiled into InsideDev.Play.dll and, as is,
    // into the offline placement test. GrecGate picks the frames worth a grab on the game thread (every n-th, and only
    // when game time moved: a frozen game renders the same moment hundreds of times per second and none of it is a new
    // picture). GrecLedger holds the grabs in flight between the game thread and the take thread and matches each
    // picture of the plugin to its grab by tag, so a grab whose picture never came is counted lost exactly once and the
    // stamps stay in step with the pictures whatever the plugin drops. GrecPlacer turns the pictures (in grab order)
    // into file frames: per grab (count mode) or by game time (time mode: file frame k shows the picture nearest to
    // t0 + k / fps, within half a frame, so frames that come at the file rate land one per frame despite jitter).
    internal struct GrecStamp
    {
        // tag: the grab event's tag (session << 8 | n & 0xFF); -1 = a moment that was never sent to the plugin
        public int frame, tag;
        public double game, real;
        // the plugin took the picture at Present because its marker never came (up to one frame late)
        public bool present;
    }

    internal static class GrecCore
    {
        // grabs sent to the plugin and not back yet: above this the game thread stops sending (tags are 8 bits, so the
        // matching stays unique, and nothing waits on a stalled plugin without bound)
        public const int MaxInFlight = 96;
        // plugin events queued and not run: the render thread runs them when it binds a marker, at most a frame or two
        // behind the game thread, so this many means the plugin no longer sees the markers
        public const int StallEvents = 16;
        // time mode: a grab needs this many file frames of game time since the last one. Frames that come slower than
        // 1 / 0.45 = 2.2 times the file rate are never thinned; faster ones keep a grab at most 0.45 + 0.55 = 1 file
        // frame apart, so every slot still has a picture within half a frame (GrecPlacer)
        public const double MinGapSlots = 0.45;
    }

    // game thread
    internal sealed class GrecGate
    {
        readonly int every;
        readonly double minGap;
        double lastGame = double.NegativeInfinity;
        public int still, thinned;

        public GrecGate(int every) : this(every, 0) { }

        public GrecGate(int every, double minGap)
        {
            this.every = every < 1 ? 1 : every;
            this.minGap = minGap > 0 ? minGap : 0;
        }

        public bool Wants(int frame, double game)
        {
            if (frame % every != 0) return false;
            if (game <= lastGame) { still++; return false; }
            if (game - lastGame < minGap) { thinned++; return false; }
            lastGame = game;
            return true;
        }
    }

    // game thread (Issue, Outstanding), take thread (NextLost, Match), close thread (TakeAll)
    internal sealed class GrecLedger
    {
        readonly object gate = new object();
        readonly Queue<GrecStamp> q = new Queue<GrecStamp>();
        int inFlight, maxCount;

        public int Outstanding { get { lock (gate) return inFlight; } }
        public int Count { get { lock (gate) return q.Count; } }
        public int MaxCount { get { lock (gate) return maxCount; } }

        public void Issue(GrecStamp s)
        {
            lock (gate)
            {
                q.Enqueue(s);
                if (s.tag >= 0) inFlight++;
                if (q.Count > maxCount) maxCount = q.Count;
            }
        }

        // the oldest stamp when it will never get a picture (never sent, or its session was closed)
        public bool NextLost(out GrecStamp s)
        {
            lock (gate)
            {
                if (q.Count > 0 && q.Peek().tag < 0) { s = q.Dequeue(); return true; }
            }
            s = new GrecStamp();
            return false;
        }

        // a picture with this tag came: the stamps before its own are lost (added to lost, in order). false = no grab
        // in flight has the tag (a picture of a closed session), nothing changes
        public bool Match(int tag, List<GrecStamp> lost, out GrecStamp s)
        {
            lock (gate)
            {
                bool found = false;
                foreach (var x in q) if (x.tag == tag) { found = true; break; }
                if (found)
                {
                    while (true)
                    {
                        var x = q.Dequeue();
                        if (x.tag >= 0) inFlight--;
                        if (x.tag == tag) { s = x; return true; }
                        x.tag = -1;
                        lost.Add(x);
                    }
                }
            }
            s = new GrecStamp();
            return false;
        }

        // a new plugin session: no grab sent before it gets its picture
        public void KillAll()
        {
            lock (gate)
            {
                int n = q.Count;
                for (int i = 0; i < n; i++) { var x = q.Dequeue(); x.tag = -1; q.Enqueue(x); }
                inFlight = 0;
            }
        }

        public void TakeAll(List<GrecStamp> into)
        {
            lock (gate)
            {
                while (q.Count > 0) { var x = q.Dequeue(); x.tag = -1; into.Add(x); }
                inFlight = 0;
            }
        }
    }

    internal interface IGrecSink<T>
    {
        // one file frame; false = the encoder is gone
        bool Frame(T picture, GrecStamp shown, int index, bool repeated, double slotGame);
        void Recycle(T picture);
        // count mode: a lost grab before the first picture
        T Blank();
    }

    // write thread
    internal sealed class GrecPlacer<T>
    {
        public readonly double fps;
        public readonly bool perGrab;
        readonly double half;
        readonly IGrecSink<T> sink;
        bool started, fresh, have;
        T prev;
        GrecStamp shown;
        double lastGrab, lastPicture;
        public volatile int written, repeated, skipped;
        public double t0, tLast, maxGapMs;

        public GrecPlacer(double fps, bool perGrab, IGrecSink<T> sink)
        {
            this.fps = fps;
            this.perGrab = perGrab;
            this.sink = sink;
            half = 0.5 / fps;
        }

        public bool Started { get { return started; } }
        public bool HasPicture { get { return have; } }
        public T Picture { get { return prev; } }

        double SlotGame(int k) { return t0 + k / fps; }

        // one grab in grab order: has = its picture came (pic, owned by the placer from here on), else it is lost.
        // false = the encoder is gone
        public bool Add(T pic, bool has, GrecStamp s)
        {
            if (perGrab) return AddPerGrab(pic, has, s);
            double tg = s.game;
            if (!started)
            {
                if (!has) return true;
                started = have = fresh = true;
                prev = pic;
                shown = s;
                t0 = lastGrab = lastPicture = tLast = tg;
                return true;
            }
            if (tg < lastGrab) tg = lastGrab;
            lastGrab = tg;
            while (SlotGame(written) + half < tg)
                if (!Emit(prev, shown, !fresh, SlotGame(written))) { if (has) sink.Recycle(pic); return false; }
            if (has)
            {
                if (fresh) skipped++;
                double gap = (tg - lastPicture) * 1000.0;
                if (gap > maxGapMs) maxGapMs = gap;
                sink.Recycle(prev);
                prev = pic;
                shown = s;
                fresh = true;
                lastPicture = tg;
            }
            tLast = tg;
            return true;
        }

        bool AddPerGrab(T pic, bool has, GrecStamp s)
        {
            if (!started) { started = true; t0 = s.game; }
            tLast = s.game;
            if (has)
            {
                if (!Emit(pic, s, false, s.game)) { sink.Recycle(pic); return false; }
                if (have) sink.Recycle(prev);
                prev = pic;
                have = true;
                return true;
            }
            if (!have) { prev = sink.Blank(); have = true; }
            return Emit(prev, s, true, s.game);
        }

        bool Emit(T picture, GrecStamp st, bool again, double slotGame)
        {
            if (!sink.Frame(picture, st, written, again, slotGame)) return false;
            fresh = false;
            if (again) repeated++;
            written++;
            return true;
        }

        // end of the run: the last picture gets the slots up to the last grab (time mode)
        public bool Finish()
        {
            if (perGrab || !started) return true;
            while (SlotGame(written) < lastGrab + half)
                if (!Emit(prev, shown, !fresh, SlotGame(written))) return false;
            if (fresh) { skipped++; fresh = false; }
            return true;
        }
    }
}
