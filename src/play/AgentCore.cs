using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // AgentCore.cs (owner INPUT, public API frozen): the agent's game input, a GameInput.Core subclass installed with
    // GameInput.SetInputSystem (precedent: ext/InsideDev.Det/FilmInput.cs; Playdead's Playback.InputRecorder uses the
    // same hook). UpdateCommand runs once per logic step inside GameManager.FixedUpdate, before Boy/Huddle logic, and
    // fills Cmd.mStick (screen space, x right, y up), mRawJump, mRawAction. That one hook drives the boy, the huddle,
    // hat dudes, the submarine, levers and input-reading FSMs. No OS input, no window focus.
    //   Human: base.UpdateCommand only (the physical pad / keyboard). Agent: queue or latched input only (physical input
    //   ignored). Mix: per field the stronger of human and agent (stick by magnitude, buttons OR).
    // Queue semantics (AgentCoreQueue.cs): frames are consumed one logic step at a time (InputFrame.steps each); when the
    // queue is empty the latched frame (if any) repeats, else neutral input. Neutral = stick 0, no buttons. Frames are
    // consumed even while the game gates input (GameInput disabled during fades and loads, or not playing): those steps
    // feed neutral and count in GatedSteps, so a run of N steps always spends N queued steps. Values below Deadzone are 0
    // to the game; send 0 or a magnitude in [0.3, 1] (the hardware shaping of Axis2D is bypassed, longer vectors are
    // normalised).
    // Install moves the old core's state into the new one (enabled flag, suppressAction, the 100-step command history and
    // the PlayerGrabInput, so a held jump or a tap in progress survives) and keeps the previous core for Uninstall, which
    // moves the state back. It keeps Application.runInBackground on (KeepAlive, restored by the Restorer key "input").
    // Tick (main thread, every frame while the core is installed, off otherwise) and a RuntimeCallbacks.fixedUpdate hook
    // (every logic step, also only while installed) reinstall the core when something else replaced GameInput.mCore
    // (Playdead SessionManager) and note input_hijacked once per install. Another InsideDev core
    // (InsideDev.Det.FilmInput, a replay) is never fought: Install answers busy while it owns the input, and a core
    // that is replaced by one steps aside as in human mode. Uninstall does not put back a foreign core whose owner has
    // finished (its type's static current no longer points to it, FilmInput.cs:19, 44-49): a fresh GameInput.Core gets
    // the state instead. While an input track records, Uninstall only switches to human mode and keeps the core in
    // place (it is the recorder's tap on the pad); the uninstall happens when the recording ends.
    // Held input without a caller: a latch (act hold, ctl hold) and the grip are let go when the body that holds them
    // dies, is re-created or a load starts (changes note). An optional safety release (dead man, off by default): with
    // editor.cfg play.hold_timeout_s > 0, when the last use of Play was a client's tool call, held input is let go after
    // that many seconds without a call while the game runs in realtime and no job runs it, so a client that went away
    // does not leave the body running (mcp.idle_restore_s also restores it later). Input held with ctl is never timed.
    // Input tracks (ActTrack.cs): while a track plays, UpdateCommand takes the step's frame from ActTrack.Next instead of
    // the queue (the same mix / gate rules apply); CommandReceived hands every fed command to ActTrack.Record while a
    // track records. Describe adds track {recording | playing, step, of} while one of them runs.
    // Grip (AgentCoreQueue.cs): a successful interact grab sets it; act, move_to and push / pull keep it (action stays
    // pressed in every step) until interact release, act {release:true}, input_mode human, Release() or restore. Tick
    // clears it, with a changes note, when the body that grabbed dies, is re-created (respawn) or a load starts.
    // Describe and HeldJson report it as grip:true; observe shows body.grip_held.
    public sealed class AgentCore : GameInput.Core
    {
        public const float Deadzone = 0.3f;
        public const string RestoreKey = "input";

        static readonly AgentQueue queue = new AgentQueue();
        static readonly object fedGate = new object();
        static volatile AgentCore instance;
        static volatile int mode;
        static volatile bool hijacked, hijackNoted, hooked;
        static volatile string controllerCache = "unknown";
        static int lastType = -1;
        static GameInput.Core previous;
        static InputFrame lastFed;
        static long lastFedStep = -1, gatedSteps;
        static FieldInfo fCore, fEnabled, fSuppress, fBuffer, fCursor, fFilled;
        static bool fieldsLooked, uninstallDeferred;
        static volatile int gripBody;
        static int latchBody, holdCheck;
        static PlayTick tick;
        public const double HoldTimeoutDefaultS = 0;

        // main thread from PlayExtension.Start; the tick runs only while the core is installed
        public static void Register()
        {
            tick = PlayRegistry.OnTick("input", Tick, TickWhen.OnDemand).Critical();
        }

        public static AgentCore Instance { get { return instance; } }
        public static InputMode Mode { get { return instance == null ? InputMode.Human : (InputMode)mode; } }
        public static bool Installed { get { var i = instance; return i != null && ReferenceEquals(CurrentCore(), i); } }
        public static bool Hijacked { get { return hijacked; } }

        internal static long GatedSteps { get { return Interlocked.Read(ref gatedSteps); } }
        internal static long ConsumedSteps { get { return queue.Consumed; } }

        // main thread; returns a warning (e.g. "controller is Digital: keyboard was used") or null
        public static string Install(InputMode m)
        {
            var cur = CurrentCore();
            var inst = instance;
            if (inst == null || !ReferenceEquals(cur, inst))
            {
                if (IsInsideDevCore(cur) && !Stale(cur))
                    throw new McpException(McpErr.Busy, "busy: the game input is owned by " + cur.GetType().Name + " (another InsideDev tool, a replay, drives the body)",
                        "wait for the replay to end, or stop it; nothing was changed");
                var core = new AgentCore();
                if (cur != null)
                {
                    TransferState(cur, core);
                    previous = IsOurType(cur) ? null : cur;
                }
                else previous = null;
                mode = (int)m;
                instance = core;
                GameInput.SetInputSystem(core);
                hijacked = false;
                hijackNoted = false;
            }
            else mode = (int)m;
            uninstallDeferred = false;
            if (m != InputMode.Human) KeepAlive.Acquire(RestoreKey);
            RegisterRestore();
            EnsureHooks();
            CacheController();
            var t = tick;
            if (t != null) t.Active = true;
            if (controllerCache == "Digital")
                return "controller_digital: the keyboard was the last device used, so the submarine boost and exit, valves, swim climb-out and drop use their keyboard rules (touch a pad or leave the keyboard alone)";
            return null;
        }

        // main thread; puts the previous core back (or a plain GameInput.Core)
        public static void Uninstall()
        {
            var inst = instance;
            if (inst == null) return;
            queue.Release();
            latchBody = 0;
            if (ActTrack.Recording && ReferenceEquals(CurrentCore(), inst))
            {
                mode = (int)InputMode.Human;
                uninstallDeferred = true;
                return;
            }
            uninstallDeferred = false;
            if (ReferenceEquals(CurrentCore(), inst))
            {
                var back = previous;
                if (back != null && Stale(back)) back = null;
                back = back ?? new GameInput.Core();
                TransferState(inst, back);
                GameInput.SetInputSystem(back);
            }
            instance = null;
            previous = null;
            mode = (int)InputMode.Human;
            Idle();
        }

        // main thread: nothing of ours is installed: no step hook, no tick
        static void Idle()
        {
            DropHooks();
            var t = tick;
            if (t != null) t.Active = false;
        }

        // any thread: append frames; latchLast keeps the last frame applied after the queue empties (hold)
        public static void Enqueue(IList<InputFrame> frames, bool latchLast) { queue.Enqueue(frames, latchLast); }
        // any thread: clear the queue and the latched frame (neutral from the next step)
        public static void Release() { queue.Release(); }
        // any thread: logic steps still queued
        public static int QueuedSteps { get { return queue.QueuedSteps; } }
        // any thread: the latched (held) frame, if any
        public static bool Latched(out InputFrame held) { return queue.Latched(out held); }
        // any thread: the command applied at the last logic step and that step (LogicManager.stepCount)
        public static InputFrame LastFed { get { lock (fedGate) return lastFed; } }
        public static long LastFedStep { get { lock (fedGate) return lastFedStep; } }

        internal static void Replace(IList<InputFrame> frames, bool latchLast) { queue.Replace(frames, latchLast); }
        internal static void ReplaceDropGrip(IList<InputFrame> frames, bool latchLast) { queue.ReplaceDropGrip(frames, latchLast); }
        internal static void ClearQueue() { queue.ClearQueue(); }
        internal static void Latch(InputFrame f) { queue.Latch(f); }

        // any thread: the grab grip (action held for interact grab across act / move_to calls)
        public static bool Grip { get { return queue.Grip; } }
        // main thread: set or clear the grip; on remembers the body that holds
        public static void SetGrip(bool on)
        {
            if (on)
            {
                var ch = ScriptGlobals.mainCharacter;
                gripBody = ch != null ? ch.GetInstanceID() : 0;
            }
            queue.SetGrip(on);
        }
        // any thread: clear the queue and the latched frame, keep the grip (the end of act, move_to, interact)
        public static void ReleaseKeepGrip() { queue.ReleaseKeepGrip(); }

        // main thread: GameInput.controllerType of the real devices ("Analog", "Digital", ...)
        public static string Controller()
        {
            GameController.Type t;
            return ControllerType(out t) ? t.ToString() : "unknown";
        }

        static bool ControllerType(out GameController.Type t)
        {
            t = GameController.Type.None;
            try
            {
                var c = User.GetController();
                t = c != null ? c.type : GameController.Type.None;
                if (t == GameController.Type.None) t = GameInput.controllerType;
                return true;
            }
            catch (Exception) { return false; }
        }

        static void CacheController()
        {
            GameController.Type t;
            if (!ControllerType(out t)) { lastType = -1; controllerCache = "unknown"; return; }
            if ((int)t == lastType) return;
            lastType = (int)t;
            controllerCache = t.ToString();
        }

        internal static bool Digital { get { return controllerCache == "Digital"; } }

        // any thread: {mode, held{stick, jump, action} or null, queued, hijacked}
        public static Json.Obj Describe()
        {
            var o = new Json.Obj();
            o["mode"] = AgentQueue.ModeName(Mode);
            o["held"] = HeldNow();
            if (Grip) o["grip"] = true;
            o["queued"] = (double)QueuedSteps;
            o["hijacked"] = Hijacked;
            o["installed"] = Installed;
            o["controller"] = controllerCache;
            var lf = LastFed;
            var last = HeldJson(lf);
            last["step"] = (double)LastFedStep;
            o["last_fed"] = last;
            long g = GatedSteps;
            if (g > 0) o["gated_steps"] = (double)g;
            var track = ActTrack.Brief();
            if (track != null) o["track"] = track;
            return o;
        }

        internal static Json.Obj HeldJson(InputFrame h)
        {
            var o = new Json.Obj();
            o["stick"] = PlayShared.V2(h.x, h.y);
            o["jump"] = h.jump;
            o["action"] = h.action;
            return o;
        }

        // any thread: what stays pressed after a call (the latch, or the grip alone); null = nothing
        internal static Json.Obj HeldNow()
        {
            InputFrame h;
            bool latched = Latched(out h);
            bool grip = Grip;
            if (!latched && !grip) return null;
            if (grip) h.action = true;
            var o = HeldJson(h);
            if (grip) o["grip"] = true;
            return o;
        }

        // main thread, every frame while the core is installed
        public static void Tick()
        {
            var inst = instance;
            if (inst == null) { Idle(); return; }
            if (uninstallDeferred && !ActTrack.Recording) { Uninstall(); return; }
            CacheController();
            CheckHeld();
            if (!ReferenceEquals(CurrentCore(), inst)) Reclaim();
            if (instance == null) { Idle(); return; }
            if (mode != (int)InputMode.Human) KeepAlive.Hold();
            if (++holdCheck >= 30)
            {
                holdCheck = 0;
                DeadMan();
            }
        }

        // the grip and a latch belong to the body that held them: gone when it dies, is re-created or a load starts
        static void CheckHeld()
        {
            bool grip = queue.Grip, latch = queue.HasLatch;
            if (!latch) latchBody = 0;
            if (!grip && !latch) return;
            string gripWhy = null, latchWhy = null;
            try
            {
                var ch = ScriptGlobals.mainCharacter;
                int id = ch != null ? ch.GetInstanceID() : 0;
                bool loading = GameManager.IsLoading(), dead = ch != null && ch.isDead;
                if (grip)
                {
                    if (loading) gripWhy = "a load started";
                    else if (ch == null) gripWhy = "no body";
                    else if (dead) gripWhy = "the body died";
                    else if (id != gripBody) gripWhy = "the body was re-created";
                }
                if (latch)
                {
                    if (latchBody == 0 && id != 0 && !dead && !loading) latchBody = id;
                    else if (loading && latchBody != 0) latchWhy = "a load started";
                    else if (dead && latchBody != 0) latchWhy = "the body died";
                    else if (latchBody != 0 && id != 0 && id != latchBody) latchWhy = "the body was re-created";
                }
            }
            catch (Exception) { return; }
            if (gripWhy != null)
            {
                queue.SetGrip(false);
                Note("note", "grip released: " + gripWhy + " (the grab from interact grab ended)");
            }
            if (latchWhy != null)
            {
                latchBody = 0;
                if (queue.Unlatch()) Note("note", "hold released: " + latchWhy + " (the held input from act hold / ctl hold ended; hold again after the respawn)");
            }
        }

        // a client that went away must not leave the body running: release what is held when the last use of Play was a
        // tool call, the game runs in realtime, no job runs it and nothing called for play.hold_timeout_s seconds
        static void DeadMan()
        {
            if (!queue.Held || !PlayRegistry.LastUseWasTool) return;
            double limit = HoldTimeoutDefaultS;
            try { limit = EditorState.Get("play.hold_timeout_s", (float)HoldTimeoutDefaultS); } catch (Exception) { }
            if (limit <= 0) return;
            double idle = PlayRegistry.IdleSeconds;
            if (idle < limit) return;
            try
            {
                if (Stepper.Frozen || Stepper.Clock.WantFrozen || Stepper.Running || ActTrack.Playing) return;
                if (HostJobs.Running(McpLanes.Control) != null) return;
            }
            catch (Exception) { return; }
            queue.Release();
            latchBody = 0;
            Note("note", "hold released: no tool call for " + PlayShared.F(Math.Round(idle)) + " s while the game ran in realtime (editor.cfg play.hold_timeout_s = " + PlayShared.F(limit) + ", 0 = never)");
        }

        static void OnFixedStep()
        {
            var inst = instance;
            if (inst == null || mode == (int)InputMode.Human) return;
            if (!ReferenceEquals(CurrentCore(), inst)) Reclaim();
        }

        static void Reclaim()
        {
            var inst = instance;
            var other = CurrentCore();
            if (inst == null || ReferenceEquals(other, inst)) return;
            hijacked = true;
            string who = other == null ? "null" : other.GetType().FullName;
            bool ours = IsInsideDevCore(other);
            bool human = mode == (int)InputMode.Human || ours;
            if (!hijackNoted)
            {
                hijackNoted = true;
                Note("input_hijacked", "GameInput core replaced by " + who + (ours ? "; another InsideDev tool owns input now, AgentCore stepped aside" : human ? "; human mode, left in place" : "; AgentCore put back"));
            }
            if (human)
            {
                instance = null;
                previous = null;
                uninstallDeferred = false;
                if (ActTrack.Recording) ActTrack.InputLost("the game input was replaced by " + who);
                return;
            }
            if (other != null)
            {
                TransferState(other, inst);
                if (!IsOurType(other)) previous = other;
            }
            GameInput.SetInputSystem(inst);
        }

        static void Note(string type, string detail)
        {
            try { ChangeLog.Note(type, detail); } catch (Exception) { }
        }

        static void EnsureHooks()
        {
            if (hooked) return;
            hooked = true;
            try { RuntimeCallbacks.fixedUpdate += OnFixedStep; }
            catch (Exception e) { DevLog.Error("[play] AgentCore fixedUpdate hook", e); }
        }

        static void DropHooks()
        {
            if (!hooked) return;
            hooked = false;
            try { RuntimeCallbacks.fixedUpdate -= OnFixedStep; }
            catch (Exception) { }
        }

        static void RegisterRestore()
        {
            try { Restorer.Register(RestoreKey, "agent input core (AgentCore) and runInBackground", RestoreAll); }
            catch (Exception) { }
        }

        // main thread (Restorer): release input, give the game its own core back, runInBackground as it was
        internal static string RestoreAll()
        {
            bool was = instance != null;
            Release();
            Uninstall();
            string r = was ? "human control" : "human control (already)";
            if (instance != null) r += " (the core stays in place as the tap of the running input track recording)";
            string k = KeepAlive.Release(RestoreKey);
            if (k != null) r += ", " + k;
            return r;
        }

        static bool IsOurType(GameInput.Core c)
        {
            return c != null && c.GetType().FullName == typeof(AgentCore).FullName;
        }

        // an input core of another InsideDev tool (namespace InsideDev.*), e.g. the replay's FilmInput
        static bool IsInsideDevCore(GameInput.Core c)
        {
            if (c == null || IsOurType(c)) return false;
            string ns = c.GetType().Namespace ?? "";
            return ns == "InsideDev" || ns.StartsWith("InsideDev.", StringComparison.Ordinal);
        }

        // a foreign core whose owner has finished: its type has a static "current" of its own type that no longer
        // points to it (FilmInput.Uninstall clears current and leaves mCore alone when it is not installed)
        static bool Stale(GameInput.Core c)
        {
            try
            {
                var f = c.GetType().GetField("current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null || !f.FieldType.IsAssignableFrom(c.GetType())) return false;
                return !ReferenceEquals(f.GetValue(null), c);
            }
            catch (Exception) { return false; }
        }

        internal static GameInput.Core CurrentCore()
        {
            if (fCore == null) fCore = typeof(GameInput).GetField("mCore", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return fCore != null ? fCore.GetValue(null) as GameInput.Core : null;
        }

        static void LookFields()
        {
            if (fieldsLooked) return;
            fieldsLooked = true;
            var t = typeof(GameInput.Core);
            const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            fEnabled = t.GetField("enabled", f);
            fSuppress = t.GetField("suppressAction", f);
            fBuffer = t.GetField("mCmdBuffer", f);
            fCursor = t.GetField("mCmdCursor", f);
            fFilled = t.GetField("mCmdFilled", f);
        }

        static void TransferState(GameInput.Core from, GameInput.Core to)
        {
            if (from == null || to == null || ReferenceEquals(from, to)) return;
            if (from.IsEnabled()) to.Enable(); else to.Disable();
            try
            {
                LookFields();
                if (fSuppress != null) fSuppress.SetValue(to, fSuppress.GetValue(from));
                var a = fBuffer != null ? fBuffer.GetValue(from) as GameInput.Cmd[] : null;
                var b = fBuffer != null ? fBuffer.GetValue(to) as GameInput.Cmd[] : null;
                if (a != null && b != null && a.Length == b.Length && fCursor != null && fFilled != null)
                {
                    for (int i = 0; i < a.Length; i++) if (a[i] != null && b[i] != null) a[i].CloneTo(b[i]);
                    fCursor.SetValue(to, fCursor.GetValue(from));
                    fFilled.SetValue(to, fFilled.GetValue(from));
                }
                if (from.mGrabInput != null) to.mGrabInput = from.mGrabInput;
            }
            catch (Exception e) { DevLog.Error("[play] AgentCore state transfer", e); }
        }

        static InputFrame FromCmd(GameInput.Cmd cmd)
        {
            return new InputFrame { x = cmd.mStick.x, y = cmd.mStick.y, jump = cmd.mRawJump, action = cmd.mRawAction, steps = 1 };
        }

        protected override void UpdateCommand(GameInput.Cmd cmd)
        {
            base.UpdateCommand(cmd);
            var m = (InputMode)mode;
            if (m == InputMode.Human || !ReferenceEquals(this, instance)) return;
            bool gated = !IsEnabled() || !GameManager.IsPlaying();
            InputFrame agent;
            bool has = ActTrack.Playing ? ActTrack.Next(gated, out agent) : queue.Next(out agent);
            if (gated && has) Interlocked.Increment(ref gatedSteps);
            var f = AgentQueue.Compose(m, gated, has, agent, FromCmd(cmd));
            cmd.mStep = LogicManager.stepCount;
            cmd.mStick = new vector2f(f.x, f.y);
            cmd.mRawJump = f.jump;
            cmd.mRawAction = f.action;
        }

        protected override void CommandReceived(GameInput.Cmd cmd)
        {
            base.CommandReceived(cmd);
            InputFrame fed = FromCmd(cmd);
            lock (fedGate)
            {
                lastFed = fed;
                lastFedStep = cmd.mStep;
            }
            if (ActTrack.Recording) ActTrack.Record(fed, cmd.mStep, !IsEnabled() || !GameManager.IsPlaying());
        }
    }
}
