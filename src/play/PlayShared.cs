using System;
using System.Collections.Generic;
using System.Globalization;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // PlayShared.cs (owner ARCH, frozen; changes go through the orchestrator): helpers every Play module uses. Argument
    // readers assume the server already validated the arguments against the tool's input schema and filled defaults,
    // so a missing key means "not given and no default". Numbers in results are rounded with R (1 cm / 0.01).
    public delegate ToolResult PlayHandler(Json.Obj args, CallContext ctx);

    public static class PlayShared
    {
        public const McpProfiles Play = McpProfiles.Play, Dev = McpProfiles.Dev, Both = McpProfiles.Both;
        // the version of this Play build; the build id is the first 8 hex digits of the DLL's SHA-256 (McpBuild)
        public const string BuildVersion = "0.7.0";
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // any thread: "<version>+<id>" of InsideDev.Play.dll
        public static string PlayBuild { get { return BuildVersion + "+" + McpBuild.IdOf(typeof(PlayShared).Assembly); } }

        // any thread: observe meta {play, mcp, core}: the builds that answer
        public static Json.Obj BuildMeta()
        {
            var o = new Json.Obj();
            o["play"] = PlayBuild;
            o["mcp"] = McpBuild.Self;
            o["core"] = Boot.Version;
            return o;
        }

        // a stub body: throw PlayShared.Todo("OBSERVE") (PlayExtension turns it into error not_built)
        public static NotImplementedException Todo(string owner) { return new NotImplementedException("not built yet (" + owner + ")"); }

        public static ToolResult NotBuilt(string tool, string owner)
        {
            return ToolResult.Fail(McpErr.NotBuilt, tool + " is not built yet", "owner " + owner + " has not implemented it");
        }

        // a fresh structured result {ok:true}
        public static Json.Obj Env() { var o = new Json.Obj(); o["ok"] = true; return o; }

        public static ToolDef Def(string name, string title, McpProfiles profiles, string owner, string description, Json.Obj input, Json.Obj output)
        {
            return new ToolDef { name = name, title = title, profiles = profiles, owner = owner, description = description, inputSchema = input, outputSchema = output };
        }

        public static bool Has(Json.Obj a, string k) { return a != null && a.ContainsKey(k) && a[k] != null; }
        public static string Str(Json.Obj a, string k, string def) { return a == null ? def : a.Str(k, def); }
        public static double Num(Json.Obj a, string k, double def) { var v = a != null ? a[k] : null; return v is double ? (double)v : def; }
        public static int Int(Json.Obj a, string k, int def) { var v = a != null ? a[k] : null; return v is double ? (int)Math.Round((double)v) : def; }
        public static long Long(Json.Obj a, string k, long def) { var v = a != null ? a[k] : null; return v is double ? (long)Math.Round((double)v) : def; }
        public static bool Bool(Json.Obj a, string k, bool def) { var v = a != null ? a[k] : null; return v is bool ? (bool)v : def; }
        public static Json.Obj Obj(Json.Obj a, string k) { return a != null ? a[k] as Json.Obj : null; }
        public static List<object> List(Json.Obj a, string k) { return a != null ? a[k] as List<object> : null; }

        public static List<string> Strings(Json.Obj a, string k)
        {
            var r = new List<string>();
            var l = List(a, k);
            if (l != null) foreach (var x in l) if (x is string) r.Add((string)x);
            return r;
        }

        public static bool Vec2(Json.Obj a, string k, out float x, out float y)
        {
            x = y = 0f;
            var l = List(a, k);
            if (l == null || l.Count != 2 || !(l[0] is double) || !(l[1] is double)) return false;
            x = (float)(double)l[0]; y = (float)(double)l[1];
            return true;
        }

        public static bool Vec3(Json.Obj a, string k, out Vector3 v)
        {
            v = Vector3.zero;
            var l = List(a, k);
            if (l == null || l.Count != 3 || !(l[0] is double) || !(l[1] is double) || !(l[2] is double)) return false;
            v = new Vector3((float)(double)l[0], (float)(double)l[1], (float)(double)l[2]);
            return true;
        }

        public static double R(double v) { return Math.Round(v, 2); }
        public static double R(double v, int digits) { return Math.Round(v, digits); }
        public static List<object> V(Vector3 v) { return new List<object> { R(v.x), R(v.y), R(v.z) }; }
        public static List<object> V2(float x, float y) { return new List<object> { R(x), R(y) }; }

        public static List<object> L(IEnumerable<string> items)
        {
            var l = new List<object>();
            if (items != null) foreach (var s in items) l.Add(s);
            return l;
        }

        public static string F(double v) { return v.ToString("0.##", IC); }

        // what to call when the game does not take play input (observe / act "next"): reason = Observer.Ready reason,
        // screen = Observer.Screen; null when there is nothing to do
        public static string NextFor(string reason, string screen)
        {
            if (screen == "message") return "menu {op:\"message_answer\", answer:\"cancel\", confirm:true} (a storage message waits for a button)";
            if (screen == "credits") return "the credits are rolling: wait_until {seconds:10}, then observe (they end at the title)";
            if (string.IsNullOrEmpty(reason)) return null;
            switch (reason)
            {
                case "title": return "menu {op:\"submit\"} (the title waits for a button; it starts the game)";
                case "paused": return "menu {op:\"close\"} (the pause menu is open)";
                case "dead": return "wait_until {alive:true} (the game respawns the body at the checkpoint)";
            }
            if (reason.StartsWith("unreadable", StringComparison.Ordinal)) return "observe again";
            return "wait_until {ready:true}";
        }

        // hierarchy path "Root/Child/Leaf" of a live object ("" for null)
        public static string PathOf(Transform t)
        {
            if (t == null) return "";
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }
    }
}
