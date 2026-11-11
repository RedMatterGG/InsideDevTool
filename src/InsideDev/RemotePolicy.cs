using System;
using System.Collections.Generic;
using System.Text;

namespace InsideDev
{
    // The one refusal list for commands that come from anything but the in-game console or the file bridge: the MCP
    // command tool, POST /cmd and /cmds, run_scenario command steps, det cues and mcam spec lines. Judged on the words the
    // console will see (quotes dropped, blanks and tabs split), so quoting cannot hide a command. Also the key rules of
    // editor.cfg. Unity-free and thread-free.
    public static class RemotePolicy
    {
        // config keys of the MCP server itself (who may call it, whether and where it listens): no remote line may name them
        public static readonly string[] AccessKeys = { "mcp.token", "mcp.tools", "mcp.hosts", "mcp.enabled", "mcp.port" };

        // allowQuit: quit / exit pass (the MCP command tool and POST /cmd); saveGuard (play.save_guard on): game loads that
        // skip the save backup and confirm are refused too. Reason text, or null when the line may run.
        public static string Refusal(string line, bool allowQuit, bool saveGuard)
        {
            if (line == null) return "empty command";
            foreach (char ch in line)
                if (ch != '\t' && char.IsControl(ch))
                    return ch == '\n' || ch == '\r' ? "one command per call (the line contains a line break)" : "the line contains a control character";
            var a = Words(line);
            if (a.Count == 0) return "empty command";
            string why = WordRefusal(a, allowQuit, saveGuard);
            if (why != null) return why;
            var plain = Words(Plain(line));
            if (plain.Count > 0 && (plain.Count != a.Count || plain[0] != a[0])) why = WordRefusal(plain, allowQuit, saveGuard);
            if (why != null) return why;
            string low = line.ToLowerInvariant();
            string joined = string.Join(" ", a.ToArray());
            foreach (var key in AccessKeys)
                if (low.IndexOf(key, StringComparison.Ordinal) >= 0 || joined.IndexOf(key, StringComparison.Ordinal) >= 0)
                    return "the line names " + key + ", a setting of this MCP server itself; change it in the in-game console";
            return null;
        }

        // the rules on the first words: a = the console's words, or the same line with every quote dropped and split on
        // blanks (so a quoted "mcp tools" is judged like mcp tools)
        static string WordRefusal(List<string> a, bool allowQuit, bool saveGuard)
        {
            string c = a[0];
            string s1 = a.Count > 1 ? a[1] : "";
            if (c == "keyhold" || c == "focustest") return c + " sends real OS keyboard input; use the act tool (input through AgentCore, no window focus needed)";
            if (c == "remote" && s1 == "off") return "remote off cannot be undone without a game restart";
            if (c == "mcp" && (s1 == "off" || s1 == "port")) return "mcp " + s1 + " would stop this MCP server from inside a call; use the in-game console";
            if (c == "mcp" && (s1 == "token" || s1 == "tools" || s1 == "hosts")) return "mcp " + s1 + " decides who may use this MCP server; it works in the in-game console only";
            if (!allowQuit && (c == "quit" || c == "exit")) return c + " closes the game; scenario steps, det cues and spec lines may not quit (the in-game console, the file bridge, the command tool and POST /cmd may)";
            if (!saveGuard) return null;
            if (c == "load" || c == "spawn" || c == "reload" || c == "respawn" || (c == "cspawn" && (s1 == "go" || s1 == "respawn")))
                return (c == "cspawn" ? "cspawn " + s1 : c) + " loads through the game without the save backup or confirm (play.save_guard is on; a chapter load writes INSIDE.sav and unlocks chapters forever); use level {op:\"load_checkpoint\" | \"reload\"}, checkpoint {op:\"reload\"} or a scenario load step";
            return null;
        }

        // every quote dropped, every kind of blank made a plain blank
        static string Plain(string line)
        {
            var sb = new StringBuilder(line.Length);
            foreach (char ch in line)
            {
                if (ch == '"') continue;
                sb.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
            }
            return sb.ToString();
        }

        // the console's split (Remote.Split), each word trimmed of every kind of blank and lower-cased; words that were only
        // blanks (a non-breaking space) are dropped, so they cannot shift the real first word
        public static List<string> Words(string line)
        {
            var r = new List<string>();
            var sb = new StringBuilder();
            bool q = false;
            foreach (char ch in line ?? "")
            {
                if (ch == '"') { q = !q; continue; }
                if (!q && (ch == ' ' || ch == '\t')) { Add(r, sb); continue; }
                sb.Append(ch);
            }
            Add(r, sb);
            return r;
        }

        static void Add(List<string> r, StringBuilder sb)
        {
            if (sb.Length == 0) return;
            string w = sb.ToString().Trim().ToLowerInvariant();
            sb.Length = 0;
            if (w.Length > 0) r.Add(w);
        }

        // a key that cfg set / reset / get accepts: [A-Za-z0-9_.-]+ (the caller trims first)
        public static bool CfgKeyOk(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            foreach (char ch in key)
                if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '.' || ch == '-')) return false;
            return true;
        }

        // a key editor.cfg can hold as one line: not empty, no blank of any kind, no '=', no '#', no control character
        public static bool StoreKeyOk(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            foreach (char ch in key)
                if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == '=' || ch == '#') return false;
            return true;
        }

        // a value editor.cfg can hold on the key's line: no line break
        public static bool StoreValueOk(string value)
        {
            return value == null || (value.IndexOf('\n') < 0 && value.IndexOf('\r') < 0);
        }
    }
}
