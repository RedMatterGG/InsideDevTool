using System.Collections.Generic;
using System.Reflection;

namespace InsideDev.Det
{
    // FilmInput.cs: GameInput.Core replacement for the determinism test (REPLAY_DESIGN 7.1). Installed before the load of
    // every run, never mid-scene. Before k0 (allScenesLoaded) it feeds neutral input; from k0 it records the four input
    // fields per logic step (record) or feeds them from a previous run (replay). k = LogicManager.stepCount - k0, read in
    // GameManager.FixedUpdate (-615) before LogicManager (50) steps, the same k as the RuntimeCallbacks.fixedUpdate hook.
    public struct DetInput
    {
        public float x, y;
        public bool jump, action;
    }

    public sealed class FilmInput : GameInput.Core
    {
        public const int Record = 1, Replay = 2;
        public static FilmInput current;
        static GameInput.Core previous;
        static FieldInfo fCore;

        public int mode = Record;
        public long k0 = long.MinValue;
        public List<DetInput> source;
        public readonly List<DetInput> fed = new List<DetInput>();
        public long lastK = long.MinValue;
        public DetInput last;
        public int missing;

        public long K { get { return k0 == long.MinValue ? -1 : LogicManager.stepCount - k0; } }

        public static FilmInput Install(int mode, List<DetInput> source)
        {
            Uninstall();
            var before = CurrentCore();
            var f = new FilmInput { mode = mode, source = source };
            previous = before is FilmInput ? null : before;
            GameInput.SetInputSystem(f);
            current = f;
            return f;
        }

        public static void Uninstall()
        {
            if (current == null) return;
            if (CurrentCore() == current) GameInput.SetInputSystem(previous ?? new GameInput.Core());
            current = null;
            previous = null;
        }

        public static GameInput.Core CurrentCore()
        {
            if (fCore == null) fCore = typeof(GameInput).GetField("mCore", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return fCore != null ? fCore.GetValue(null) as GameInput.Core : null;
        }

        static void Neutral(GameInput.Cmd cmd)
        {
            cmd.mStep = LogicManager.stepCount;
            cmd.mStick = vector2f.zero;
            cmd.mRawJump = false;
            cmd.mRawAction = false;
        }

        protected override void UpdateCommand(GameInput.Cmd cmd)
        {
            long k = K;
            if (k < 0) { Neutral(cmd); return; }
            if (mode != Replay) { base.UpdateCommand(cmd); return; }
            if (source == null || k >= source.Count) { Neutral(cmd); missing++; return; }
            var r = source[(int)k];
            cmd.mStep = LogicManager.stepCount;
            cmd.mStick = new vector2f(r.x, r.y);
            cmd.mRawJump = r.jump;
            cmd.mRawAction = r.action;
        }

        protected override void CommandReceived(GameInput.Cmd cmd)
        {
            long k = K;
            if (k < 0) return;
            var d = new DetInput { x = cmd.mStick.x, y = cmd.mStick.y, jump = cmd.mRawJump, action = cmd.mRawAction };
            last = d;
            lastK = k;
            if (k > int.MaxValue - 1) return;
            while (fed.Count < k) fed.Add(new DetInput());
            if (fed.Count == k) fed.Add(d); else fed[(int)k] = d;
        }
    }
}
