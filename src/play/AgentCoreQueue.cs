using System;
using System.Collections.Generic;

namespace InsideDev.Play
{
    // AgentCoreQueue.cs (owner INPUT): the Unity-free half of AgentCore, compiled into the offline tests too
    // (qa/mcp/INPUT). InputMode and InputFrame live here so the pure code can use them. AgentQueue is the step-keyed
    // queue: Next() is called once per logic step from AgentCore.UpdateCommand and consumes one step of the head frame;
    // when the queue is empty the latched frame repeats, else neutral. Enqueue(latchLast:false) with frames clears the
    // latch (the new frames define the input: queue, then neutral); an empty list changes nothing. Compose() is the
    // per-step mix rule of the three modes and the input gate (GameInput disabled or not playing = neutral agent input).
    // Grip: the action button held for a grab (interact grab). While it is on, every step's agent frame has action
    // pressed (queue, latch and neutral alike), so the next act / move_to does not drop the grab. Release() clears it
    // with the rest; ReleaseKeepGrip() clears only the queue and the latch; SetGrip(false) only the grip.
    public enum InputMode { Human, Agent, Mix }

    public struct InputFrame
    {
        public float x, y;
        public bool jump, action;
        public int steps;
    }

    internal sealed class AgentQueue
    {
        public const float Deadzone = 0.3f;
        const float Noise = 0.05f;

        readonly object gate = new object();
        readonly Queue<InputFrame> queue = new Queue<InputFrame>();
        int headLeft, queued;
        bool hasLatch, grip;
        InputFrame latch;
        long consumed, fromQueue, fromLatch;

        public static InputFrame Neutral(int steps)
        {
            return new InputFrame { steps = steps };
        }

        public static float Magnitude(float x, float y) { return (float)Math.Sqrt(x * x + y * y); }

        public static InputFrame Shape(InputFrame f)
        {
            float x = f.x, y = f.y;
            if (float.IsNaN(x) || float.IsInfinity(x)) x = 0f;
            if (float.IsNaN(y) || float.IsInfinity(y)) y = 0f;
            float m = Magnitude(x, y);
            if (m < Deadzone) { x = 0f; y = 0f; }
            else if (m > 1f) { x /= m; y /= m; }
            return new InputFrame { x = x, y = y, jump = f.jump, action = f.action, steps = f.steps };
        }

        public static bool InDeadzoneBand(float x, float y)
        {
            float m = Magnitude(x, y);
            return m >= Noise && m < Deadzone;
        }

        public static InputFrame Merge(InputFrame agent, InputFrame human)
        {
            bool humanStick = human.x * human.x + human.y * human.y > agent.x * agent.x + agent.y * agent.y;
            return new InputFrame
            {
                x = humanStick ? human.x : agent.x,
                y = humanStick ? human.y : agent.y,
                jump = agent.jump || human.jump,
                action = agent.action || human.action,
                steps = 1
            };
        }

        public static InputFrame Compose(InputMode mode, bool gated, bool agentHas, InputFrame agent, InputFrame human)
        {
            if (mode == InputMode.Human) return One(human);
            if (mode == InputMode.Agent) return gated || !agentHas ? Neutral(1) : One(agent);
            if (gated) return One(human);
            return agentHas ? Merge(agent, human) : One(human);
        }

        static InputFrame One(InputFrame f) { f.steps = 1; return f; }

        public void Enqueue(IList<InputFrame> frames, bool latchLast)
        {
            if (frames == null) return;
            lock (gate)
            {
                bool any = false;
                InputFrame last = default(InputFrame);
                foreach (var f0 in frames)
                {
                    if (f0.steps <= 0) continue;
                    var f = Shape(f0);
                    queue.Enqueue(f);
                    if (queue.Count == 1) headLeft = f.steps;
                    queued += f.steps;
                    last = f;
                    any = true;
                }
                if (!any) return;
                if (latchLast) { latch = One(last); hasLatch = true; }
                else hasLatch = false;
            }
        }

        public void Replace(IList<InputFrame> frames, bool latchLast)
        {
            lock (gate)
            {
                ClearQueueLocked();
                Enqueue(frames, latchLast);
            }
        }

        // Replace and clear the grip in one step (no logic step can see the gap)
        public void ReplaceDropGrip(IList<InputFrame> frames, bool latchLast)
        {
            lock (gate)
            {
                grip = false;
                ClearQueueLocked();
                Enqueue(frames, latchLast);
            }
        }

        public void ClearQueue() { lock (gate) ClearQueueLocked(); }

        void ClearQueueLocked()
        {
            queue.Clear();
            headLeft = 0;
            queued = 0;
        }

        public void Release()
        {
            lock (gate)
            {
                ReleaseKeepGripLocked();
                grip = false;
            }
        }

        public void ReleaseKeepGrip() { lock (gate) ReleaseKeepGripLocked(); }

        void ReleaseKeepGripLocked()
        {
            ClearQueueLocked();
            hasLatch = false;
            latch = default(InputFrame);
        }

        public void SetGrip(bool on) { lock (gate) grip = on; }

        public bool Grip { get { lock (gate) return grip; } }

        public void Latch(InputFrame f)
        {
            lock (gate)
            {
                latch = One(Shape(f));
                hasLatch = true;
            }
        }

        public int QueuedSteps { get { lock (gate) return queued; } }

        public bool Latched(out InputFrame held)
        {
            lock (gate)
            {
                held = hasLatch ? latch : default(InputFrame);
                return hasLatch;
            }
        }

        public bool Idle { get { lock (gate) return queued == 0 && !hasLatch && !grip; } }

        public long Consumed { get { lock (gate) return consumed; } }
        public long FromQueue { get { lock (gate) return fromQueue; } }
        public long FromLatch { get { lock (gate) return fromLatch; } }

        public bool Next(out InputFrame f)
        {
            lock (gate)
            {
                consumed++;
                if (queue.Count > 0)
                {
                    f = One(queue.Peek());
                    headLeft--;
                    queued--;
                    if (headLeft <= 0)
                    {
                        queue.Dequeue();
                        headLeft = queue.Count > 0 ? queue.Peek().steps : 0;
                    }
                    fromQueue++;
                    if (grip) f.action = true;
                    return true;
                }
                if (hasLatch)
                {
                    f = latch;
                    fromLatch++;
                    if (grip) f.action = true;
                    return true;
                }
                f = Neutral(1);
                if (!grip) return false;
                f.action = true;
                return true;
            }
        }

        public static string ModeName(InputMode m)
        {
            return m == InputMode.Agent ? "agent" : m == InputMode.Mix ? "mix" : "human";
        }

        public static bool TryMode(string s, out InputMode m)
        {
            m = InputMode.Human;
            if (s == "agent") { m = InputMode.Agent; return true; }
            if (s == "mix") { m = InputMode.Mix; return true; }
            if (s == "human") { m = InputMode.Human; return true; }
            return false;
        }
    }
}
