using System;
using System.Collections.Generic;
using System.Threading;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // ChangeLog.cs (owner OBSERVE, public API and the changes schema frozen): "what did my action do". A ring of
    // events with a monotonically increasing sequence number: signals fired and FSM state changes (InsideDev
    // EventMonitor, which already taps every SignalOut and polls FSMs), GameObjects switched on / off near the body,
    // Unity errors and exceptions (Application.logMessageReceived), savepoints reached, deaths (DeathManager.died),
    // loads (GameManager.loadStarted / loadFinished). Actions call Mark() before they run and put Since(mark, 20) into
    // their result as "changes". Every list is capped (cap per list) and truncated{} counts what was cut.
    // Polling runs in the "changelog" tick and again inside Since / Events when they are called on the main thread, so
    // a result built in the frame the action stopped already holds that frame. Game hooks are added in the first tick
    // (never in Register: the offline contract test registers every module without a game). Toggle polling and the
    // core ObjectDatabase demand (which the EventMonitor FSM watch depends on) run only while an agent was active in
    // the last 90 s. Extra output: "other" [{step, type, detail}] for note / input_hijacked / stalled, truncated.lost
    // when the ring (4000) overflowed, "warnings" when the event monitor is off. Since is compact (ChangeRing.Compact):
    // signals and fsm changes of objects more than NearRadius from the body are only counted in far{}, lists are capped at
    // CompactCap and the object stays under CompactChars; SinceFull lists everything (act changes:"full", dev tools).
    public static class ChangeLog
    {
        public const string Owner = "OBSERVE";
        public const float NearRadius = 15f;
        public const int CompactCap = 8, CompactChars = 1600;

        static readonly ChangeRing ring = new ChangeRing(4000);
        static readonly object gate = new object();
        static int mainThread = -1;
        static bool hooked;
        static long lastStep;
        static long lastMonitorTotal = -1;
        static int lastPollFrame = -1;
        static long activeUntilTicks;
        static bool hasCheckpoint;
        static int cpSub = -1, cpSp = -1;

        static readonly int[] frameOf = new int[128];
        static readonly long[] stepOf = new long[128];

        static readonly Dictionary<int, bool> nearActive = new Dictionary<int, bool>();
        static readonly List<ObjRecord> nearSet = new List<ObjRecord>();
        static Vector3 nearCenter;
        static int nearBuilt = -100000, nearEpoch = -1, lastToggleFrame = -100000;
        static string bodyRootPath;

        public static void Register()
        {
            PlayRegistry.OnTick("changelog", Tick);
        }

        // any thread: the current sequence number
        public static long Mark()
        {
            Touch();
            if (OnMain()) SafePoll(true);
            return ring.Mark();
        }

        // any thread: what happened after mark near the body, compact (Schema)
        public static Json.Obj Since(long mark, int capPerList)
        {
            Touch();
            if (OnMain()) SafePoll(true);
            var o = ring.Compact(mark, Math.Min(Math.Max(1, capPerList), CompactCap), CompactChars);
            if (!MonitorOn()) o["warnings"] = new List<object> { "event monitor is off (bridge: events on): no signals or fsm changes" };
            return o;
        }

        // any thread: everything after mark, near or far (Schema)
        public static Json.Obj SinceFull(long mark, int capPerList)
        {
            Touch();
            if (OnMain()) SafePoll(true);
            var o = ring.Since(mark, capPerList);
            if (!MonitorOn()) o["warnings"] = new List<object> { "event monitor is off (bridge: events on): no signals or fsm changes" };
            return o;
        }

        // any thread: entries after mark of the given types (Observer: deaths for the stuck counter)
        internal static int CountSince(long mark, string[] types) { return ring.CountAfter(mark, types); }

        // any thread: one merged, step-ordered list for observe.events (Observer.EventSchema)
        public static List<Json.Obj> Events(long mark, int cap)
        {
            int cut;
            return Events(mark, cap, out cut);
        }

        internal static List<Json.Obj> Events(long mark, int cap, out int cut)
        {
            Touch();
            if (OnMain()) SafePoll(true);
            return ring.Events(mark, cap, out cut);
        }

        // main thread (Observer): flush, then the events in (mark, Seq]; upTo = that Seq for the next cursor
        internal static List<Json.Obj> EventsForObserve(long mark, int cap, bool priorityOnly, out int cut, out long upTo)
        {
            Touch();
            if (OnMain()) SafePoll(true);
            upTo = ring.Seq;
            return ring.Events(mark, upTo, cap, priorityOnly, out cut);
        }

        // any thread: add an event (type one of Observer.EventTypes)
        public static void Note(string type, string detail)
        {
            string t = Array.IndexOf(Observer.EventTypes, type) >= 0 ? type : "note";
            if (t == "error") { ring.AddError("error", StateNames.Clip(detail, 300), Step); return; }
            string list = ChangeRing.OtherList;
            Json.Obj item = null;
            if (t == "death") { list = "deaths"; item = Item(Step); item["cause"] = detail ?? ""; }
            ring.Add(t, list, Step, StateNames.Clip(detail, 300), item);
        }

        // the "changes" field of action results (documented once, here and in docs/MCP_PLAY_DESIGN.md section 5)
        public static Json.Obj Ref()
        {
            return McpExt.Schema.Described(McpExt.Schema.ObjAny(), "What changed during the call near the body: signals, fsm, toggled, errors, checkpoints, deaths, loads (at most 8 entries each {step, ...}); far{} counts signal / fsm changes away from the body, truncated{} what was cut.");
        }

        // contract, implemented
        public static SchemaObj Schema()
        {
            return McpExt.Schema.Obj()
                .Arr("signals", "Signal outputs fired.", McpExt.Schema.OutOf(McpExt.Schema.Obj().Int("step", "", 0, long.MaxValue).Str("object", "Path or id.").Str("output", "Signal output.")), 0, 20)
                .Arr("fsm", "FSM state changes.", McpExt.Schema.OutOf(McpExt.Schema.Obj().Int("step", "", 0, long.MaxValue).Str("object", "").Str("fsm", "").Str("from", "").Str("to", "")), 0, 20)
                .Arr("toggled", "GameObjects switched on / off.", McpExt.Schema.OutOf(McpExt.Schema.Obj().Int("step", "", 0, long.MaxValue).Str("object", "").Bool("active", "")), 0, 20)
                .Arr("errors", "Unity errors and exceptions.", McpExt.Schema.OutOf(McpExt.Schema.Obj().Int("step", "", 0, long.MaxValue).Enum("type", "", null, "error", "exception", "assert").Str("message", "")), 0, 20)
                .Arr("checkpoints", "Savepoints reached.", McpExt.Schema.OutOf(McpExt.Schema.Obj().Int("step", "", 0, long.MaxValue).Int("subscene", "", -1, 999).Int("savepoint", "", -1, 999)), 0, 20)
                .Arr("deaths", "Deaths.", McpExt.Schema.OutOf(McpExt.Schema.Obj().Int("step", "", 0, long.MaxValue).Str("cause", "")), 0, 20)
                .Arr("loads", "Load phases.", McpExt.Schema.OutOf(McpExt.Schema.Obj().Int("step", "", 0, long.MaxValue).Str("kind", "Startup, Respawn, Chapter").Enum("phase", "", null, "started", "finished")), 0, 20)
                .OutObj("far", "Signal / fsm entries more than 15 m from the body, counted only (compact view).", McpExt.Schema.Obj().Open())
                .OutObj("truncated", "Entries cut per list.", McpExt.Schema.Obj().Open());
        }

        // ---------------------------------------------------------------- internal (OBSERVE files)
        internal static long Step { get { return Interlocked.Read(ref lastStep); } }
        internal static long Seq { get { return ring.Seq; } }
        internal static bool Active { get { return DateTime.UtcNow.Ticks < Interlocked.Read(ref activeUntilTicks); } }
        internal static bool OnMain() { return mainThread >= 0 && Thread.CurrentThread.ManagedThreadId == mainThread; }

        internal static readonly string[] ProgressTypes = { "signal", "fsm", "toggled", "checkpoint" };
        internal static readonly string[] DeathTypes = { "death" };

        // any thread: near-body progress entries after mark that were not seen before (key type|detail added to seen);
        // a trigger crossed again or an FSM cycling through the same states is not progress
        internal static int NovelSince(long mark, HashSet<string> seen) { return ring.NovelAfter(mark, ProgressTypes, seen); }
        internal static int ProgressSince(long mark) { return ring.CountAfter(mark, ProgressTypes); }

        // any thread: an agent is using the tools (keeps the scans and the core object index warm for 90 s, longer
        // than one call budget)
        internal static void Touch()
        {
            Interlocked.Exchange(ref activeUntilTicks, DateTime.UtcNow.Ticks + TimeSpan.TicksPerSecond * 90);
        }

        static Json.Obj Item(long step)
        {
            var o = new Json.Obj();
            o["step"] = (double)step;
            return o;
        }

        static void Tick()
        {
            if (mainThread < 0) mainThread = Thread.CurrentThread.ManagedThreadId;
            if (!hooked) Hook();
            SafePoll(false);
        }

        static void Hook()
        {
            hooked = true;
            try { GameManager.loadStarted += OnLoadStarted; } catch (Exception e) { DevLog.Error("[play] changelog: GameManager.loadStarted", e); }
            try { GameManager.loadFinished += OnLoadFinished; } catch (Exception e) { DevLog.Error("[play] changelog: GameManager.loadFinished", e); }
            try { DeathManager.died += OnDied; } catch (Exception e) { DevLog.Error("[play] changelog: DeathManager.died", e); }
            try { Application.logMessageReceived += OnLog; } catch (Exception e) { DevLog.Error("[play] changelog: Application.logMessageReceived", e); }
        }

        static void OnLoadStarted(GameManager.Load load, Savepoint.CharacterType ch)
        {
            try
            {
                long st = LogicManager.stepCount;
                Interlocked.Exchange(ref lastStep, st);
                Observer.OnLoadStarted();
                lock (gate) { nearActive.Clear(); nearSet.Clear(); }
                var it = Item(st);
                it["kind"] = load.ToString();
                it["phase"] = "started";
                ring.Add("load_started", "loads", st, load.ToString(), it);
            }
            catch { }
        }

        static void OnLoadFinished(GameManager.Load load, Savepoint.CharacterType ch)
        {
            try
            {
                long st = LogicManager.stepCount;
                Interlocked.Exchange(ref lastStep, st);
                Observer.OnLoadFinished();
                var it = Item(st);
                it["kind"] = load.ToString();
                it["phase"] = "finished";
                ring.Add(load == GameManager.Load.Respawn ? "respawn" : "load_finished", "loads", st, load.ToString() + (ch != Savepoint.CharacterType.Boy ? " as " + ch : ""), it);
                hasCheckpoint = false;
            }
            catch { }
        }

        static void OnDied()
        {
            try
            {
                long st = LogicManager.stepCount;
                Interlocked.Exchange(ref lastStep, st);
                string cause = Perception.DeathCause();
                var it = Item(st);
                it["cause"] = cause;
                ring.Add("death", "deaths", st, cause, it);
            }
            catch { }
        }

        static void OnLog(string msg, string stack, LogType type)
        {
            try
            {
                string kind = type == LogType.Exception ? "exception" : type == LogType.Assert ? "assert" : type == LogType.Error ? "error" : null;
                if (kind == null) return;
                string m = msg ?? "";
                int nl = m.IndexOf('\n');
                if (nl >= 0) m = m.Substring(0, nl);
                m = StateNames.Clip(m.Trim(), 240);
                if (kind == "exception" && !string.IsNullOrEmpty(stack))
                {
                    string s = stack;
                    int n2 = s.IndexOf('\n');
                    if (n2 >= 0) s = s.Substring(0, n2);
                    m += " @ " + StateNames.Clip(s.Trim(), 120);
                }
                ring.AddError(kind, m, Interlocked.Read(ref lastStep));
            }
            catch { }
        }

        static bool MonitorOn()
        {
            try { return EventMonitor.enabled; } catch { return false; }
        }

        static void SafePoll(bool force)
        {
            try { Poll(force); }
            catch (Exception e)
            {
                if (!pollFailed) { pollFailed = true; DevLog.Error("[play] changelog poll", e); }
            }
        }
        static bool pollFailed;

        static void Poll(bool force)
        {
            int f = Time.frameCount;
            long st = LogicManager.stepCount;
            Interlocked.Exchange(ref lastStep, st);
            int slot = f & 127;
            frameOf[slot] = f;
            stepOf[slot] = st;
            if (f == lastPollFrame && !force) return;
            bool firstThisFrame = f != lastPollFrame;
            lastPollFrame = f;
            Vector3 bp;
            bool hasBody = false;
            try { hasBody = Perception.BodyPos(out bp); } catch { bp = Vector3.zero; }
            drainBody = bp;
            drainHasBody = hasBody;
            DrainMonitor(st);
            if (firstThisFrame) PollCheckpoint(st);
            bool active = Active;
            if (active)
            {
                try { ObjectDatabase.Demand(2f); } catch { }
                PollToggles(force, st);
            }
        }

        static Vector3 drainBody;
        static bool drainHasBody;

        // main thread: the event's object was more than NearRadius from the body (no body or no object = near)
        static bool Far(ObjRecord rec)
        {
            if (!drainHasBody || rec == null || rec.go == null) return false;
            Vector3 p;
            try { p = rec.go.transform.position; } catch { return false; }
            return (p - drainBody).sqrMagnitude > NearRadius * NearRadius;
        }

        static long StepAtFrame(int frame, long fallback)
        {
            int slot = frame & 127;
            return frameOf[slot] == frame ? stepOf[slot] : fallback;
        }

        static void DrainMonitor(long st)
        {
            long total = EventMonitor.total;
            if (lastMonitorTotal < 0 || total < lastMonitorTotal) { lastMonitorTotal = total; return; }
            long n = total - lastMonitorTotal;
            lastMonitorTotal = total;
            if (n <= 0) return;
            int avail = EventMonitor.Count;
            if (n > avail) n = avail;
            if (n > 400) n = 400;
            for (int i = (int)n - 1; i >= 0; i--)
            {
                var ev = EventMonitor.Get(i);
                if (ev.text == null) continue;
                long es = StepAtFrame(ev.frame, st);
                if (ev.kind == 'S') AddSignal(ev, es);
                else if (ev.kind == 'F') AddFsm(ev, es);
            }
        }

        static void AddSignal(EventMonitor.Ev ev, long step)
        {
            string text = ev.text;
            string head = text, to = "";
            int arrow = text.IndexOf("  ->  ", StringComparison.Ordinal);
            if (arrow >= 0) { head = text.Substring(0, arrow); to = text.Substring(arrow + 6); }
            else
            {
                int nr = text.IndexOf("  (no receivers)", StringComparison.Ordinal);
                if (nr >= 0) head = text.Substring(0, nr);
            }
            string goName, output;
            var rec = Rec(ev.goId);
            if (rec != null && rec.name != null && head.StartsWith(rec.name + ".", StringComparison.Ordinal)) { goName = rec.name; output = head.Substring(rec.name.Length + 1); }
            else
            {
                int dot = head.LastIndexOf('.');
                goName = dot > 0 ? head.Substring(0, dot) : head;
                output = dot > 0 ? head.Substring(dot + 1) : "";
            }
            string obj = ObjName(rec, goName);
            var it = Item(step);
            it["object"] = obj;
            it["output"] = output;
            if (to.Length > 0) it["to"] = StateNames.Clip(to, 100);
            ring.Add("signal", "signals", step, obj + "." + output + (to.Length > 0 ? " -> " + StateNames.Clip(to, 60) : ""), it, Far(rec));
        }

        static void AddFsm(EventMonitor.Ev ev, long step)
        {
            string text = ev.text;
            int a = text.IndexOf(" / ", StringComparison.Ordinal);
            int b = a >= 0 ? text.IndexOf(":  ", a + 3, StringComparison.Ordinal) : -1;
            int c = b >= 0 ? text.IndexOf("  ->  ", b + 3, StringComparison.Ordinal) : -1;
            if (a < 0 || b < 0 || c < 0) return;
            string goName = text.Substring(0, a);
            string fsm = text.Substring(a + 3, b - a - 3);
            string from = text.Substring(b + 3, c - b - 3);
            string to = text.Substring(c + 6);
            var rec = Rec(ev.goId);
            string obj = ObjName(rec, goName);
            var it = Item(step);
            it["object"] = obj;
            it["fsm"] = fsm;
            it["from"] = from;
            it["to"] = to;
            ring.Add("fsm", "fsm", step, obj + " " + fsm + ": " + from + " -> " + to, it, Far(rec));
        }

        static ObjRecord Rec(int goId)
        {
            try { return ObjectDatabase.Get(goId); } catch { return null; }
        }

        static string ObjName(ObjRecord rec, string fallback)
        {
            if (rec != null && rec.go != null)
            {
                string id = Observer.ExistingId(rec.go);
                if (id != null) return id;
                if (!string.IsNullOrEmpty(rec.path)) return StateNames.PathTail(rec.path, 90);
            }
            return fallback ?? "?";
        }

        static void PollCheckpoint(long st)
        {
            if (PersistentBehaviour<Savegame>.instance == null) return;
            var cp = Savegame.GetCurrentSavepoint();
            if (!hasCheckpoint) { hasCheckpoint = true; cpSub = cp.subscene; cpSp = cp.savepoint; return; }
            if (cp.subscene == cpSub && cp.savepoint == cpSp) return;
            cpSub = cp.subscene;
            cpSp = cp.savepoint;
            var it = Item(st);
            it["subscene"] = (double)cpSub;
            it["savepoint"] = (double)cpSp;
            ring.Add("checkpoint", "checkpoints", st, cpSub + ":" + cpSp, it);
        }

        static void PollToggles(bool force, long st)
        {
            if (!ObjectDatabase.Ready) return;
            if (GameManager.IsLoading())
            {
                lock (gate) { nearActive.Clear(); nearSet.Clear(); }
                return;
            }
            int f = Time.frameCount;
            if (f == lastToggleFrame) return;
            if (!force && f - lastToggleFrame < 6) return;
            lastToggleFrame = f;
            Vector3 c;
            if (!Perception.BodyPos(out c)) return;
            lock (gate)
            {
                if (nearEpoch != Observer.Epoch || nearSet.Count == 0 && f - nearBuilt > 30 || (c - nearCenter).sqrMagnitude > 9f || f - nearBuilt > 120) RebuildNear(c, f);
                int added = 0;
                for (int i = 0; i < nearSet.Count; i++)
                {
                    var r = nearSet[i];
                    var go = r.go;
                    if (go == null) continue;
                    bool on = go.activeSelf;
                    bool was;
                    if (nearActive.TryGetValue(r.id, out was) && was != on && added < 60)
                    {
                        added++;
                        string obj = ObjName(r, r.name);
                        var it = Item(st);
                        it["object"] = obj;
                        it["active"] = on;
                        ring.Add("toggled", "toggled", st, obj + (on ? " on" : " off"), it);
                    }
                    nearActive[r.id] = on;
                }
            }
        }

        static void RebuildNear(Vector3 c, int f)
        {
            nearBuilt = f;
            nearEpoch = Observer.Epoch;
            nearCenter = c;
            nearSet.Clear();
            bodyRootPath = null;
            var ch = Perception.ActiveCharacter();
            if (ch != null)
            {
                var br = ObjectDatabase.Get(ch.gameObject);
                if (br != null && br.path != null) bodyRootPath = br.path + "/";
            }
            const float r2 = 15f * 15f;
            var all = ObjectDatabase.all;
            for (int i = 0; i < all.Count && nearSet.Count < 3000; i++)
            {
                var r = all[i];
                if (r == null || r.go == null) continue;
                if ((r.pos - c).sqrMagnitude > r2) continue;
                if (bodyRootPath != null && r.path != null && r.path.StartsWith(bodyRootPath, StringComparison.Ordinal)) continue;
                if (IsCuller(r)) continue;
                nearSet.Add(r);
            }
            var keep = new Dictionary<int, bool>();
            foreach (var r in nearSet) { bool v; if (nearActive.TryGetValue(r.id, out v)) keep[r.id] = v; }
            nearActive.Clear();
            foreach (var kv in keep) nearActive[kv.Key] = kv.Value;
        }

        static bool IsCuller(ObjRecord r)
        {
            if (r.name != null && r.name.EndsWith("_CULL", StringComparison.Ordinal)) return true;
            var cs = r.comps;
            if (cs != null) foreach (var n in cs) if (n == "BoundsCullerRuntime") return true;
            return false;
        }
    }
}
