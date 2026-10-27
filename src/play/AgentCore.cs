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
    // moves the state back. It sets Application.runInBackground (restored by the Restorer key "input"). Tick (main thread,
    // every frame) and a RuntimeCallbacks.fixedUpdate hook (every logic step) reinstall the core when something else
    // replaced GameInput.mCore (Playdead SessionManager) and note input_hijacked once per install. Another InsideDev core
    // (InsideDev.Det.FilmInput, a replay) is never fought: AgentCore steps aside as in human mode. Uninstall does not put
    // back a foreign core whose owner has finished (its type's static current no longer points to it, FilmInput.cs:19,
    // 44-49): a fresh GameInput.Core gets the state instead.
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
        static bool runInBackgroundSaved, runInBackgroundBefore;
        static FieldInfo fCore, fEnabled, fSuppress, fBuffer, fCursor, fFilled;
        static bool fieldsLooked;
        static volatile int gripBody;

        // main thread from PlayExtension.Start: OnTick("input", Tick)
        public static void Register()
        {
            PlayRegistry.OnTick("input", Tick);
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
            if (m != InputMode.Human) KeepRunningInBackground();
            RegisterRestore();
            EnsureHooks();
            CacheController();
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

        // main thread, every frame
        public static void Tick()
        {
            EnsureHooks();
            CacheController();
            CheckGrip();
            var inst = instance;
            if (inst == null) return;
            if (!ReferenceEquals(CurrentCore(), inst)) Reclaim();
            if (mode != (int)InputMode.Human && !Application.runInBackground) Application.runInBackground = true;
        }

        static void CheckGrip()
        {
            if (!queue.Grip) return;
            string why = null;
            try
            {
                var ch = ScriptGlobals.mainCharacter;
                if (GameManager.IsLoading()) why = "a load started";
                else if (ch == null) why = "no body";
                else if (ch.isDead) why = "the body died";
                else if (ch.GetInstanceID() != gripBody) why = "the body was re-created";
            }
            catch (Exception) { why = null; }
            if (why == null) return;
            queue.SetGrip(false);
            Note("note", "grip released: " + why + " (the grab from interact grab ended)");
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

        static void KeepRunningInBackground()
        {
            if (!runInBackgroundSaved)
            {
                runInBackgroundSaved = true;
                runInBackgroundBefore = Application.runInBackground;
            }
            if (!Application.runInBackground) Application.runInBackground = true;
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
            if (runInBackgroundSaved)
            {
                runInBackgroundSaved = false;
                if (Application.runInBackground != runInBackgroundBefore)
                {
                    Application.runInBackground = runInBackgroundBefore;
                    r += ", runInBackground " + (runInBackgroundBefore ? "on" : "off");
                }
            }
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
