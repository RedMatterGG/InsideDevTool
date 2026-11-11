using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace InsideDev
{
    // Who needs the background work. Scanners and monitors ask Background.Wanted(feature) or Background.InUse before they
    // spend frame time; panels, commands and clients call Background.Want(feature, seconds) or Background.Touch() when they
    // use the tool. With nobody using it the tool costs nothing per frame.
    // editor.cfg key "background": auto (default: work only while somebody uses it), always (every scanner runs all the time,
    // as before 0.11.0), off (scanners run only for an explicit Want).
    public static class Background
    {
        public const string Objects = "objects", Audio = "audio", Hidden = "hidden", Events = "events", Code = "code", Catalog = "catalog", Timed = "timed";

        // seconds the tool counts as "in use" after the last panel frame, console / bridge command or client call
        public const float UseWindow = 30f;

        static readonly Dictionary<string, long> until = new Dictionary<string, long>();
        static long touchedAt = long.MinValue;
        static int mode = -1;
        static bool everOpened;

        static long Ticks(float seconds) { return (long)(seconds * Stopwatch.Frequency); }

        // main thread
        public static void Reload()
        {
            string m = EditorState.Get("background", "auto");
            mode = m == "always" ? 1 : m == "off" ? 2 : 0;
        }

        public static string Mode
        {
            get { if (mode < 0) Reload(); return mode == 1 ? "always" : mode == 2 ? "off" : "auto"; }
        }

        // main thread; false for an unknown word
        public static bool SetMode(string m)
        {
            if (m != "auto" && m != "always" && m != "off") return false;
            EditorState.Set("background", m);
            Reload();
            return true;
        }

        // Any thread. A client, the bridge or the console did something.
        public static void Touch() { Interlocked.Exchange(ref touchedAt, Stopwatch.GetTimestamp()); }

        // Main thread, once per frame while the editor panel is open.
        public static void PanelOpen() { everOpened = true; Touch(); }

        // The editor panel was opened at least once in this game session.
        public static bool EverOpened { get { return everOpened; } }

        // True while somebody uses the tool. "always": true. "off": false. Before the first main-thread call the mode
        // counts as auto.
        public static bool InUse
        {
            get
            {
                if (mode == 1) return true;
                if (mode == 2) return false;
                long t = Interlocked.Read(ref touchedAt);
                return t != long.MinValue && Stopwatch.GetTimestamp() - t < Ticks(UseWindow);
            }
        }

        // Main thread. feature is needed for the next seconds (a panel that shows it, a command that reads it).
        public static void Want(string feature, float seconds)
        {
            long end = Stopwatch.GetTimestamp() + Ticks(seconds);
            long old;
            if (!until.TryGetValue(feature, out old) || old < end) until[feature] = end;
        }

        // Main thread. True while feature was asked for with Want. "always": true.
        public static bool Wanted(string feature)
        {
            if (mode < 0) Reload();
            if (mode == 1) return true;
            long end;
            return until.TryGetValue(feature, out end) && Stopwatch.GetTimestamp() < end;
        }

        // Main thread. Wanted(feature), or the tool is in use at all: for work that every panel and client benefits from.
        public static bool WantedOrInUse(string feature) { return Wanted(feature) || InUse; }

        // main thread
        public static string Status()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("background ").Append(Mode).Append(", in use ").Append(InUse ? "yes" : "no");
            long now = Stopwatch.GetTimestamp();
            foreach (var kv in until)
            {
                double left = (kv.Value - now) / (double)Stopwatch.Frequency;
                if (left > 0) sb.Append(", ").Append(kv.Key).Append(' ').Append(left.ToString("0", Fmt.IC)).Append(" s");
            }
            return sb.ToString();
        }
    }
}
