using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Persisted editor preferences and layout (_mod\editor.cfg, key=value lines). Lives outside the renderer
    // and the UI toolkit so neither a renderer reset nor a scene change can lose it.
    public static class EditorState
    {
        static readonly Dictionary<string, string> kv = new Dictionary<string, string>();
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static string path;
        static bool loaded, dirty;
        static float saveAt;

        static string Path_
        {
            get
            {
                if (path == null) path = Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "editor.cfg");
                return path;
            }
        }

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(Path_)) return;
                foreach (var line in File.ReadAllLines(Path_))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0 || line.StartsWith("#")) continue;
                    kv[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                DevLog.Write("editor state: " + kv.Count + " value(s) from editor.cfg");
            }
            catch (Exception e) { DevLog.Error("editor state load", e); }
        }

        public static void Tick()
        {
            if (dirty && Time.realtimeSinceStartup >= saveAt) Save();
        }

        public static void Save()
        {
            dirty = false;
            try
            {
                var sb = new StringBuilder("# InsideDev editor layout and preferences\n");
                var keys = new List<string>(kv.Keys); keys.Sort(StringComparer.Ordinal);
                foreach (var k in keys) sb.Append(k).Append('=').Append(kv[k]).Append('\n');
                File.WriteAllText(Path_, sb.ToString());
            }
            catch (Exception e) { DevLog.Error("editor state save", e); }
        }

        static void Mark() { if (!dirty) { dirty = true; saveAt = Time.realtimeSinceStartup + 1.5f; } }

        public static void Set(string k, string v) { Load(); string old; if (kv.TryGetValue(k, out old) && old == v) return; kv[k] = v; Mark(); }
        public static void Set(string k, float v) { Set(k, v.ToString("0.###", IC)); }
        public static void Set(string k, int v) { Set(k, v.ToString(IC)); }
        public static void Set(string k, bool v) { Set(k, v ? "1" : "0"); }

        public static void Remove(string k) { Load(); if (kv.Remove(k)) Mark(); }
        public static string Get(string k, string def) { Load(); string v; return kv.TryGetValue(k, out v) ? v : def; }
        public static float Get(string k, float def) { float f; return float.TryParse(Get(k, (string)null), NumberStyles.Float, IC, out f) ? f : def; }
        public static int Get(string k, int def) { int i; return int.TryParse(Get(k, (string)null), NumberStyles.Integer, IC, out i) ? i : def; }
        public static bool Get(string k, bool def) { var s = Get(k, (string)null); return s == null ? def : s == "1" || s == "true"; }

        public static void Reset(string prefix)
        {
            Load();
            var rm = new List<string>();
            foreach (var k in kv.Keys) if (k.StartsWith(prefix)) rm.Add(k);
            foreach (var k in rm) kv.Remove(k);
            Mark();
        }
    }
}
