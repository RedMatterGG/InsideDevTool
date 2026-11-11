using System;
using System.Collections.Generic;
using System.Diagnostics;
using HutongGames.PlayMaker;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev.Play
{
    // PpcXScan.cs (owner OBSERVE): the component caches behind objects, hazards and bodies.
    //   One pass fills every tracked type at once (each concrete class is matched to the tracked types once and
    //   remembered). Scan mode "all" (default, editor.cfg play.perception_scan): Resources.FindObjectsOfTypeAll, so the
    //   lists also hold the components of switched-off objects (the game's _CULL containers switch gameplay objects off
    //   and on as the camera moves) and every reader tests activeInHierarchy / isActiveAndEnabled at the moment it
    //   reads. Activation therefore never needs a rescan; prefab assets in memory are never active in the hierarchy and
    //   drop out by the same test. A rescan is needed only when objects were created: a level load (epoch), a subscene
    //   that finished streaming in (SubsceneCuller.StaticEvents.postLoad, the same static event the game's own managers
    //   use), a tool that spawned something (InvalidateAll), and, as a net for objects the game instantiates, when an
    //   observation is built WideSteps logic steps or more after the last pass (never inside a run for that reason:
    //   a pass is a long frame).
    //   Scan mode "active": Object.FindObjectsOfType (active objects only, the behaviour before 0.11.0); the lists are
    //   then rescanned for every observation once a logic step ran since the last pass, and every RunSteps steps
    //   inside a run.
    //   Rescans are done by RefreshIfStale, which Observer.Build calls before it reads anything and the hazard query
    //   calls for runs (act stop_on hazard, wait hazard_*). While the real-time recorder films, rescans other than the
    //   one after a load are skipped and observe warns scan_stale.
    //   Hazard rows keep the transform and, for the kinds positioned by their collider bounds, the collider (resolved
    //   at the first use while active), so the distance test runs before any HazardInfo is built. PlayMaker FSMs seen
    //   by the pass are sorted once into kill FSMs and others (an FSM that was never initialised is asked again later).
    public static partial class Perception
    {
        sealed class TypeCache
        {
            public Type type;
            public UObj[] items = NoItems;
            public HazRow[] rows = NoRows;
            public bool hazard;
        }

        // one cached hazard component with what the distance test needs
        internal sealed class HazRow
        {
            public Behaviour comp;
            public Transform tr;
            public Collider col;
            // position = the nearest point of its collider bounds (kill zones, crushers, the KillerKinds classes)
            public bool boxed;
            // 0 = collider not looked up yet, 1 = found, 2 = the object has none
            public byte colState;
        }

        static readonly UObj[] NoItems = new UObj[0];
        static readonly HazRow[] NoRows = new HazRow[0];

        static readonly Dictionary<Type, TypeCache> caches = new Dictionary<Type, TypeCache>();
        static readonly List<TypeCache> order = new List<TypeCache>();
        static readonly Dictionary<Type, int[]> matches = new Dictionary<Type, int[]>();
        static readonly Dictionary<Type, bool> boxedTypes = new Dictionary<Type, bool>();
        static readonly Dictionary<Type, string> kindByType = new Dictionary<Type, string>();
        static readonly List<int> loose = new List<int>();
        static readonly List<PlayMakerFSM> fsmUndecided = new List<PlayMakerFSM>();
        static readonly List<PlayMakerFSM> fsmKill = new List<PlayMakerFSM>();
        static readonly int[] NoMatch = new int[0];

        // steps after which a pass counts as old: for an observation / inside a run (mode active), and the net for
        // objects created without a load (mode all)
        internal const int BuildSteps = 1, RunSteps = 30, WideSteps = 600;

        static bool scanInvalid = true, wide = true, cfgRead, subsceneHook;
        static volatile bool structureDirty;
        static int scanEpoch = -1, scanFrame = -100000, scansTotal, scanObjects, scanTracked, bgCheck;
        static long scanStep = -1;
        static float scanAt = -100f, bgSeconds;
        static double scanMs;
        static string scanWhy = "", scanBlocked;
        static long scanBlockedAge;

        static void InitCaches()
        {
            if (order.Count == 0)
            {
                foreach (var t in ObjectTypes) AddCache(t, false);
                foreach (var t in HazardTypes) AddCache(t, true);
                foreach (var t in OtherTypes) AddCache(t, false);
            }
            if (!cfgRead) ReadConfig();
            if (!subsceneHook)
            {
                subsceneHook = true;
                try { SubsceneCuller.StaticEvents.postLoad += OnSubsceneLoaded; }
                catch (Exception e) { DevLog.Error("[play] perception: SubsceneCuller.StaticEvents.postLoad", e); }
            }
        }

        static void AddCache(Type t, bool hazard)
        {
            TypeCache c;
            if (caches.TryGetValue(t, out c)) { c.hazard = c.hazard || hazard; return; }
            c = new TypeCache { type = t, hazard = hazard };
            caches[t] = c;
            order.Add(c);
            if (!typeof(MonoBehaviour).IsAssignableFrom(t)) loose.Add(order.Count - 1);
        }

        static void ReadConfig()
        {
            cfgRead = true;
            try { bgSeconds = EditorState.Get("play.perception_bg_s", 0f); } catch { bgSeconds = 0f; }
            bool w = true;
            try { w = EditorState.Get("play.perception_scan", "all") != "active"; } catch { w = true; }
            if (w != wide) { wide = w; scanInvalid = true; }
        }

        // a subscene finished streaming in: its components exist now and are in no list yet
        static void OnSubsceneLoaded(SubsceneCuller culler) { structureDirty = true; }

        // main thread: why the lists have to be rescanned now; null = usable
        static string StaleWhy(int maxSteps)
        {
            if (scanInvalid || scanEpoch != Observer.Epoch) return "load";
            if (structureDirty) return "subscene";
            long age = LogicManager.stepCount - scanStep;
            if (age < 0) return "clock";
            if (wide) return maxSteps <= BuildSteps && age >= WideSteps ? "age" : null;
            return age >= maxSteps ? "age" : null;
        }

        // main thread: rescan when the lists are older than the rule allows (maxSteps applies to scan mode active).
        // True when a pass ran. A pass that is due but skipped (recorder) is remembered for the scan_stale warning.
        internal static bool RefreshIfStale(int maxSteps)
        {
            InitCaches();
            string why = StaleWhy(maxSteps);
            if (why == null) { scanBlocked = null; return false; }
            bool loading = false;
            try { loading = GameManager.IsLoading(); } catch { }
            if (loading) { scanBlocked = null; return false; }
            if (why != "load" && Recorder.On)
            {
                scanBlocked = why;
                scanBlockedAge = Math.Max(0, LogicManager.stepCount - scanStep);
                return false;
            }
            scanBlocked = null;
            ScanAll(why);
            return true;
        }

        // the scan_stale warning text, null when the lists are current
        internal static string StaleWarning()
        {
            if (scanBlocked == null) return null;
            return "scan_stale: the object and hazard lists are " + scanBlockedAge.ToString(System.Globalization.CultureInfo.InvariantCulture) + " steps old (" +
                (scanBlocked == "subscene" ? "a subscene streamed in since" : "objects created since are not listed") + "): the rescan waits while the recorder films";
        }

        static void ScanAll(string why)
        {
            var sw = Stopwatch.StartNew();
            UObj[] all;
            try { all = wide ? Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour)) : UObj.FindObjectsOfType(typeof(MonoBehaviour)); }
            catch { all = null; }
            var lists = new List<UObj>[order.Count];
            if (scanEpoch != Observer.Epoch) killFsms.Clear();
            fsmUndecided.Clear();
            fsmKill.Clear();
            int tracked = 0;
            if (all != null)
            {
                var fsmType = typeof(PlayMakerFSM);
                for (int n = 0; n < all.Length; n++)
                {
                    var o = all[n];
                    if (o == null) continue;
                    var ct = o.GetType();
                    if (ct == fsmType) { SortFsm(o as PlayMakerFSM); continue; }
                    int[] idx;
                    if (!matches.TryGetValue(ct, out idx)) { idx = Match(ct); matches[ct] = idx; }
                    for (int k = 0; k < idx.Length; k++)
                    {
                        int i = idx[k];
                        if (lists[i] == null) lists[i] = new List<UObj>();
                        lists[i].Add(o);
                        tracked++;
                    }
                }
            }
            foreach (int i in loose)
            {
                UObj[] found;
                try { found = wide ? Resources.FindObjectsOfTypeAll(order[i].type) : UObj.FindObjectsOfType(order[i].type); }
                catch { found = null; }
                if (found != null) { lists[i] = new List<UObj>(found); tracked += found.Length; }
            }
            for (int i = 0; i < order.Count; i++)
            {
                var c = order[i];
                c.items = lists[i] != null ? lists[i].ToArray() : NoItems;
                c.rows = c.hazard ? RowsOf(c.items) : NoRows;
            }
            scanInvalid = false;
            structureDirty = false;
            scanEpoch = Observer.Epoch;
            scanStep = LogicManager.stepCount;
            scanFrame = Time.frameCount;
            scanAt = Time.realtimeSinceStartup;
            scanMs = sw.Elapsed.TotalMilliseconds;
            scanObjects = all != null ? all.Length : 0;
            scanTracked = tracked;
            scanWhy = why ?? "";
            scansTotal++;
            settleFrame = -1;
        }

        static int[] Match(Type ct)
        {
            List<int> l = null;
            for (int i = 0; i < order.Count; i++)
                if (order[i].type.IsAssignableFrom(ct)) { if (l == null) l = new List<int>(); l.Add(i); }
            return l != null ? l.ToArray() : NoMatch;
        }

        static HazRow[] RowsOf(UObj[] items)
        {
            if (items.Length == 0) return NoRows;
            var l = new List<HazRow>(items.Length);
            foreach (var x in items)
            {
                var b = x as Behaviour;
                if (b == null) continue;
                try { l.Add(new HazRow { comp = b, tr = b.transform, boxed = Boxed(b) }); }
                catch { }
            }
            return l.ToArray();
        }

        static bool Boxed(Behaviour b)
        {
            var t = b.GetType();
            bool r;
            if (boxedTypes.TryGetValue(t, out r)) return r;
            bool point = b is Hulv || b is ChaserBase || b is SentryGun || b is SentryGunAimAndShoot || b is SpotLightDetector || b is RopeGun || b is WaterGirl;
            r = !point && (b is Killer || b is DelayedKiller || b is WaterFlushKiller || b is HeadlessKillTrigger || b is GateCrush || b is CityGateCrush || b is WagonCrush
                || (!(b is ZombieMonitorDetection) && KillerKinds.Known(t.Name)));
            boxedTypes[t] = r;
            return r;
        }

        // the position a hazard row is measured at; only for a row whose component is active now
        static Vector3 RowPos(HazRow r, Vector3 body)
        {
            if (!r.boxed) return r.tr.position;
            if (r.colState == 1 && r.col == null) r.colState = 0;
            if (r.colState == 0)
            {
                var col = r.comp.GetComponent<Collider>();
                if (col == null) col = r.comp.GetComponentInChildren<Collider>();
                r.col = col;
                r.colState = col != null ? (byte)1 : (byte)2;
            }
            return r.colState == 1 ? Closest(r.col.bounds, body) : r.tr.position;
        }

        // a PlayMaker FSM seen by the pass: known kill FSMs and the ones that cannot be judged yet are kept
        static void SortFsm(PlayMakerFSM f)
        {
            if (f == null) return;
            bool k;
            if (killFsms.TryGetValue(f.GetInstanceID(), out k)) { if (k) fsmKill.Add(f); return; }
            fsmUndecided.Add(f);
        }

        static int settleFrame = -1;

        // an FSM can be judged once PlayMaker initialised it (its actions are loaded then); asked once per frame
        static void SettleFsms()
        {
            if (settleFrame == Time.frameCount) return;
            settleFrame = Time.frameCount;
            for (int i = fsmUndecided.Count - 1; i >= 0; i--)
            {
                var f = fsmUndecided[i];
                bool drop = f == null;
                if (!drop)
                {
                    Fsm fsm = null;
                    try { fsm = f.Fsm; } catch { drop = true; }
                    if (!drop && (fsm == null || !fsm.Initialized || fsm.States == null)) continue;
                    if (!drop && IsKillFsm(f)) fsmKill.Add(f);
                }
                int last = fsmUndecided.Count - 1;
                fsmUndecided[i] = fsmUndecided[last];
                fsmUndecided.RemoveAt(last);
            }
        }

        // lists from before a load are rescanned at their first use (never while the load still runs)
        static void EnsureLoaded()
        {
            if (!scanInvalid && scanEpoch == Observer.Epoch) return;
            bool loading = false;
            try { loading = GameManager.IsLoading(); } catch { }
            if (!loading) ScanAll("load");
        }

        // every cached component of a tracked type, switched-off ones included in scan mode all: test Live(...) before use
        internal static UObj[] Items(Type t)
        {
            InitCaches();
            TypeCache c;
            if (!caches.TryGetValue(t, out c)) return NoItems;
            EnsureLoaded();
            return c.items;
        }

        static HazRow[] Rows(Type t)
        {
            TypeCache c;
            if (!caches.TryGetValue(t, out c)) return NoRows;
            return c.rows;
        }

        // a cached component that exists and is active in the scene now
        static bool Live(Component c) { return c != null && c.gameObject.activeInHierarchy; }

        // main thread: a tool created or destroyed objects; the next reader rescans
        internal static void InvalidateAll() { scanInvalid = true; }

        static void TickRefresh()
        {
            InitCaches();
            if (--bgCheck <= 0) { bgCheck = 120; ReadConfig(); }
            if (bgSeconds <= 0f || Time.realtimeSinceStartup - scanAt < bgSeconds || Recorder.On) return;
            string why = StaleWhy(RunSteps);
            if (why != null) ScanAll(why);
        }

        // observe full, perf: what the last pass cost and how old the lists are
        internal static Json.Obj Perf()
        {
            InitCaches();
            var o = new Json.Obj();
            o["scan_mode"] = wide ? "all" : "active";
            o["scan_ms"] = PlayShared.R(scanMs, 2);
            o["scan_ms_total"] = PlayShared.R(scanMs, 2);
            o["scans_total"] = (double)scansTotal;
            o["scan_reason"] = scanWhy;
            o["scan_age_steps"] = scanStep < 0 ? -1.0 : (double)Math.Max(0, LogicManager.stepCount - scanStep);
            o["scan_age_frames"] = scansTotal == 0 ? -1.0 : (double)Math.Max(0, Time.frameCount - scanFrame);
            o["objects_scanned"] = (double)scanObjects;
            o["tracked"] = (double)scanTracked;
            o["kill_fsms"] = (double)fsmKill.Count;
            o["stale_types"] = StaleWhy(BuildSteps) != null ? (double)order.Count : 0.0;
            o["types"] = (double)order.Count;
            return o;
        }

        internal static int ScansTotal { get { return scansTotal; } }

        // one line for the doctor command
        internal static string ScanStatus()
        {
            if (scansTotal == 0) return "no pass yet (mode " + (wide ? "all" : "active") + ")";
            return "mode " + (wide ? "all" : "active") + ", " + scansTotal + " passes, last " + PlayShared.F(scanMs) + " ms over " + scanObjects + " scripts, " + scanTracked + " tracked";
        }
    }
}
