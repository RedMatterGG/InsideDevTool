using System.Collections.Generic;

namespace InsideDev
{
    // Who holds a shared resource of the game: the camera, the clock (time scale / stepping) or the native frame readback.
    // A feature takes what it needs before it starts and releases it when it ends, so two features never fight silently:
    // the second one gets a refusal that names the holder. Thread safe.
    public static class Owners
    {
        public const string Camera = "camera", Clock = "clock", Readback = "readback";

        static readonly Dictionary<string, string> held = new Dictionary<string, string>();

        // True when owner holds what after the call (taking it twice is fine). False: holder names who has it.
        public static bool TryTake(string what, string owner, out string holder)
        {
            lock (held)
            {
                if (held.TryGetValue(what, out holder) && holder != owner) return false;
                held[what] = owner;
                holder = owner;
                return true;
            }
        }

        public static void Release(string what, string owner)
        {
            lock (held)
            {
                string h;
                if (held.TryGetValue(what, out h) && h == owner) held.Remove(what);
            }
        }

        public static void ReleaseAll(string owner)
        {
            lock (held)
            {
                var keys = new List<string>();
                foreach (var kv in held) if (kv.Value == owner) keys.Add(kv.Key);
                foreach (var k in keys) held.Remove(k);
            }
        }

        // null when free
        public static string Holder(string what)
        {
            lock (held) { string h; return held.TryGetValue(what, out h) ? h : null; }
        }

        public static string Status()
        {
            lock (held)
            {
                if (held.Count == 0) return "owners: none";
                var sb = new System.Text.StringBuilder("owners:");
                foreach (var kv in held) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
                return sb.ToString();
            }
        }
    }
}
