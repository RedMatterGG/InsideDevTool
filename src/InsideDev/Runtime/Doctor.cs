using System;
using System.Collections.Generic;
using System.Text;

namespace InsideDev
{
    // The self-check behind the "doctor" command. Every part (core, MCP, Play, recorder ...) registers one check that
    // returns rows; "doctor" prints them, "doctor json" returns them as JSON. A check runs on the main thread and must be
    // quick (no scene scans, no process starts longer than a second) and must not change anything.
    public static class Doctor
    {
        public const string Ok = "ok", Warn = "warn", Fail = "fail", Info = "info";

        public struct Row
        {
            public string part, name, status, detail, fix;
            public Row(string part, string name, string status, string detail, string fix = null)
            {
                this.part = part; this.name = name; this.status = status; this.detail = detail; this.fix = fix;
            }
        }

        static readonly List<KeyValuePair<string, Func<List<Row>>>> checks = new List<KeyValuePair<string, Func<List<Row>>>>();

        // part = a short name ("core", "mcp", "play", "srec", "mcam", "audio"). Registering the same part again replaces it.
        public static void Register(string part, Func<List<Row>> check)
        {
            lock (checks)
            {
                for (int i = 0; i < checks.Count; i++) if (checks[i].Key == part) { checks[i] = new KeyValuePair<string, Func<List<Row>>>(part, check); return; }
                checks.Add(new KeyValuePair<string, Func<List<Row>>>(part, check));
            }
        }

        public static void Unregister(string part)
        {
            lock (checks) checks.RemoveAll(c => c.Key == part);
        }

        // main thread
        public static List<Row> Run()
        {
            KeyValuePair<string, Func<List<Row>>>[] list;
            lock (checks) list = checks.ToArray();
            var rows = new List<Row>();
            foreach (var c in list)
            {
                try
                {
                    var r = c.Value();
                    if (r != null) rows.AddRange(r);
                }
                catch (Exception e) { rows.Add(new Row(c.Key, "check", Fail, e.GetType().Name + ": " + e.Message)); }
            }
            return rows;
        }

        public static string Worst(List<Row> rows)
        {
            string w = Ok;
            foreach (var r in rows)
            {
                if (r.status == Fail) return Fail;
                if (r.status == Warn) w = Warn;
            }
            return w;
        }

        public static string Text(List<Row> rows)
        {
            var sb = new StringBuilder();
            int fails = 0, warns = 0;
            foreach (var r in rows) { if (r.status == Fail) fails++; else if (r.status == Warn) warns++; }
            sb.Append("doctor: ").Append(Worst(rows)).Append(" (").Append(rows.Count).Append(" check(s), ").Append(fails).Append(" fail, ").Append(warns).Append(" warn)");
            foreach (var r in rows)
            {
                sb.Append('\n').Append(r.status.PadRight(5)).Append(r.part).Append(" / ").Append(r.name).Append(": ").Append(r.detail);
                if (!string.IsNullOrEmpty(r.fix) && r.status != Ok) sb.Append("  -> ").Append(r.fix);
            }
            return sb.ToString();
        }

        public static string Json(List<Row> rows)
        {
            var o = new InsideDev.Json.Obj();
            o["ok"] = Worst(rows) != Fail;
            o["status"] = Worst(rows);
            var l = new List<object>();
            foreach (var r in rows)
            {
                var x = new InsideDev.Json.Obj();
                x["part"] = r.part; x["name"] = r.name; x["status"] = r.status; x["detail"] = r.detail;
                if (!string.IsNullOrEmpty(r.fix)) x["fix"] = r.fix;
                l.Add(x);
            }
            o["checks"] = l;
            return JsonLine.Write(o);
        }
    }
}
