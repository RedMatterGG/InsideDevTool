using System;
using System.Collections.Generic;

namespace InsideDev.McpExt
{
    // McpKeepAlive.cs (owner HOST): long work of other modules that the MCP server's automatic restores must not cut
    // short. A module registers once, at start:
    //     McpKeepAlive.Register("srec", () => recording ? "recording take 12" : null);
    // busyReason is called on the game's main thread (from the MCP tick, about once a second while a restore is due,
    // and from "mcp status"); it must be cheap, must not throw and returns null when the module is idle. While any entry
    // returns a reason, the restore after mcp.idle_restore_s and the restore after the last session closed wait, and
    // "mcp status" names the reason. "mcp restore", "mcp off" and the game quitting do not wait. Unity-free.
    public static class McpKeepAlive
    {
        static readonly object gate = new object();
        // replaced as a whole on every change, so readers take no lock and allocate nothing
        static volatile KeyValuePair<string, Func<string>>[] entries = new KeyValuePair<string, Func<string>>[0];

        // name = a short word for the module ("mcam", "srec", "grec", "pano"); the same name again replaces the entry
        public static void Register(string name, Func<string> busyReason)
        {
            if (string.IsNullOrEmpty(name) || busyReason == null) return;
            lock (gate)
            {
                var l = new List<KeyValuePair<string, Func<string>>>(entries);
                l.RemoveAll(e => e.Key == name);
                l.Add(new KeyValuePair<string, Func<string>>(name, busyReason));
                entries = l.ToArray();
            }
        }

        public static void Unregister(string name)
        {
            lock (gate)
            {
                var l = new List<KeyValuePair<string, Func<string>>>(entries);
                if (l.RemoveAll(e => e.Key == name) > 0) entries = l.ToArray();
            }
        }

        public static int Count { get { return entries.Length; } }

        // main thread: "<name>: <reason>" of the first busy entry, or null when every module is idle
        public static string Busy()
        {
            var list = entries;
            for (int i = 0; i < list.Length; i++)
            {
                string r;
                try { r = list[i].Value(); }
                catch (Exception) { r = null; }
                if (!string.IsNullOrEmpty(r)) return list[i].Key + ": " + r;
            }
            return null;
        }

        // main thread: every busy entry as "<name>: <reason>"
        public static List<string> BusyAll()
        {
            var res = new List<string>();
            var list = entries;
            for (int i = 0; i < list.Length; i++)
            {
                string r;
                try { r = list[i].Value(); }
                catch (Exception) { r = null; }
                if (!string.IsNullOrEmpty(r)) res.Add(list[i].Key + ": " + r);
            }
            return res;
        }

        // offline tests only
        internal static void ResetForTests() { lock (gate) entries = new KeyValuePair<string, Func<string>>[0]; }
    }
}
