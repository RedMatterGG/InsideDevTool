using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Persistent, game-wide list of audio-driven triggers per area (_mod/audio_catalog.txt).
    // Filled automatically from whatever is loaded while you play, and completely by "Catalog all areas"
    // (loads each area briefly, records, unloads). Lets you see triggers of areas that are not loaded.
    public static class AudioCatalog
    {
        public class Entry
        {
            public string area;    // subscene base name, e.g. forcePushGetWagon
            public string kind;    // timer | system | cue | beat | musictime
            public string obj;     // object / fsm
            public string detail;  // human readable
        }

        public static readonly Dictionary<string, List<Entry>> byArea = new Dictionary<string, List<Entry>>();
        static bool loaded, dirty;
        static float nextAuto;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        static string FilePath { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "audio_catalog.txt"); } }

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var p = line.Split('\t');
                    if (p.Length < 4) continue;
                    Add(new Entry { area = p[0], kind = p[1], obj = p[2], detail = p[3] });
                }
                DevLog.Write("audio catalog: " + byArea.Count + " area(s) from disk");
            }
            catch (Exception e) { DevLog.Error("audio catalog load", e); }
        }

        static void Add(Entry e)
        {
            List<Entry> l;
            if (!byArea.TryGetValue(e.area, out l)) { l = new List<Entry>(); byArea[e.area] = l; }
            l.Add(e);
        }

        public static void Save()
        {
            if (!dirty) return;
            dirty = false;
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in byArea)
                    foreach (var e in kv.Value)
                        sb.Append(e.area).Append('\t').Append(e.kind).Append('\t').Append(Clean(e.obj)).Append('\t').Append(Clean(e.detail)).Append('\n');
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (Exception e) { DevLog.Error("audio catalog save", e); }
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\n', ' '); }

        // area of a scene object = its scene root "#name_Gameplay_ContentRoot"
        public static string AreaOf(GameObject go)
        {
            if (go == null) return "?";
            string r = go.transform.root.name;
            r = r.TrimStart('#');
            if (r.EndsWith("_ContentRoot")) r = r.Substring(0, r.Length - "_ContentRoot".Length);
            foreach (var suf in new[] { "_Gameplay", "_GamePlay", "_Gamelogic", "_Environment", "_Backdrop", "_Logic" })
                if (r.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) { r = r.Substring(0, r.Length - suf.Length); break; }
            return r;
        }

        // record everything currently loaded (replaces the entries of the areas seen)
        public static void RecordLoaded()
        {
            Load();
            var fresh = new Dictionary<string, List<Entry>>();
            Action<Entry> put = e =>
            {
                List<Entry> l;
                if (!fresh.TryGetValue(e.area, out l)) { l = new List<Entry>(); fresh[e.area] = l; }
                foreach (var x in l) if (x.kind == e.kind && x.obj == e.obj && x.detail == e.detail) return;
                l.Add(e);
            };
            foreach (var s in AudioTimed.systems)
            {
                if (s.comp == null) continue;
                string area = AreaOf(s.comp.gameObject);
                string obj = Inspector.PathOf(s.comp.transform);
                if (s.timers.Count == 0) put(new Entry { area = area, kind = "system", obj = obj, detail = s.typeName + ": " + s.desc });
                foreach (var t in s.timers)
                    put(new Entry { area = area, kind = "timer", obj = obj, detail = s.typeName + "." + t.field + " @" + t.original.ToString("0.00", IC) + "s" + Sig(s.comp.gameObject) });
            }
            foreach (var w in AudioLinks.waits)
            {
                if (w.fsm == null) continue;
                string area = AreaOf(w.fsm.gameObject);
                put(new Entry
                {
                    area = area,
                    kind = w.isCue ? "cue" : w.isBeat ? "beat" : "musictime",
                    obj = Inspector.PathOf(w.fsm.transform) + " / " + w.fsm.FsmName,
                    detail = w.kind + " in state [" + w.state + "] -> fires '" + w.finishEvent + "'"
                });
            }
            foreach (var kv in fresh)
            {
                byArea[kv.Key] = kv.Value;
                dirty = true;
            }
            Save();
        }

        static string Sig(GameObject go)
        {
            string s = AudioTimed.Outgoing(go);
            return s.Length > 0 ? "   signals: " + s : "";
        }

        // background: keep the catalog updated with whatever normal play streams in
        public static void AutoRecord()
        {
            if (Time.realtimeSinceStartup < nextAuto) return;
            nextAuto = Time.realtimeSinceStartup + 10f;
            try { RecordLoaded(); } catch (Exception e) { DevLog.Error("audio catalog auto", e); nextAuto = Time.realtimeSinceStartup + 60f; }
        }

        public static void CatalogAll()
        {
            if (!G.SavepointsReady) { DevLog.Write("catalog: start or continue a game first"); return; }
            for (int s = 0; s < SavepointManager.SubsceneCount; s++) Discovery.Enqueue(s, 0, Discovery.Action.Catalog);
            DevLog.Write("catalog: queued " + SavepointManager.SubsceneCount + " area(s) - each is loaded briefly, scanned and unloaded");
        }
    }
}
