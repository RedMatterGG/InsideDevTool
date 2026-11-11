using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Persistent, game-wide list of audio-driven triggers per area (_mod/audio_catalog.txt).
    // Completely filled by "Catalog all areas" (loads each area briefly, records, unloads); while the tool is in use the
    // areas that stream in during play are added to it. Lets you see triggers of areas that are not loaded.
    // editor.cfg key audio.catalog: auto (default: add rows while the tool is in use), always, off (only "Catalog all areas").
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
        // bumps whenever byArea changes (the panel rebuilds its lists from it)
        public static int Version;
        static bool loaded, dirty, loadFailed, loadFailLogged;
        static int seenTimedScan = -1;
        static int mode = -1;   // 0 auto, 1 always, 2 off
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly string[] areaSuffixes = { "_Gameplay", "_GamePlay", "_Gamelogic", "_Environment", "_Backdrop", "_Logic" };

        static string FilePath { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "audio_catalog.txt"); } }

        static int Mode
        {
            get
            {
                if (mode < 0) { string m = EditorState.Get("audio.catalog", "auto"); mode = m == "always" ? 1 : m == "off" ? 2 : 0; }
                return mode;
            }
        }
        // the user asked for the catalog to be kept up to date even while nobody uses the tool
        public static bool Always { get { return Mode == 1; } }
        public static void ReloadConfig() { mode = -1; }

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                bool fromBackup;
                string text = SafeFile.ReadAllTextOrBackup(FilePath, out fromBackup);
                if (text == null) return;
                foreach (var raw in text.Split('\n'))
                {
                    var p = raw.TrimEnd('\r').Split('\t');
                    if (p.Length < 4 || p[0].Length == 0) continue;
                    Add(new Entry { area = p[0], kind = p[1], obj = p[2], detail = p[3] });
                }
                Version++;
                if (fromBackup) dirty = true;
                DevLog.Write("audio catalog: " + byArea.Count + " area(s) from disk" + (fromBackup ? " (audio_catalog.txt was unreadable, its .bak copy was used)" : ""));
            }
            catch (Exception e)
            {
                // the file is there but could not be read: never write a shorter list over it
                loadFailed = true;
                DevLog.Error("audio catalog load", e);
            }
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
            if (loadFailed)
            {
                if (!loadFailLogged) { loadFailLogged = true; DevLog.Write("audio catalog: not saved, audio_catalog.txt could not be read at start (the file is left as it is)"); }
                return;
            }
            dirty = false;
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in byArea)
                    foreach (var e in kv.Value)
                        sb.Append(e.area).Append('\t').Append(e.kind).Append('\t').Append(Clean(e.obj)).Append('\t').Append(Clean(e.detail)).Append('\n');
                SafeFile.WriteAllText(FilePath, sb.ToString(), true);
            }
            catch (Exception e) { DevLog.Error("audio catalog save", e); }
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '); }

        // area of a scene object = its scene root "#name_Gameplay_ContentRoot"
        public static string AreaOf(GameObject go)
        {
            if (go == null) return "?";
            string r = go.transform.root.name;
            r = r.TrimStart('#');
            if (r.EndsWith("_ContentRoot")) r = r.Substring(0, r.Length - "_ContentRoot".Length);
            foreach (var suf in areaSuffixes)
                if (r.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) { r = r.Substring(0, r.Length - suf.Length); break; }
            return r;
        }

        // record everything currently loaded (replaces the entries of the areas seen): right after a Catalog job, when the
        // timers and the PlayMaker waits of the loaded areas were both scanned
        public static void RecordLoaded() { RecordLoaded(true); }

        // replace = false: only add rows that are not in the catalog yet. Normal play knows the timers of the loaded areas
        // but mostly not their PlayMaker waits, so replacing would delete the cue / beat rows a full catalog run collected.
        public static void RecordLoaded(bool replace)
        {
            Load();
            var fresh = new Dictionary<string, List<Entry>>();
            foreach (var s in AudioTimed.systems)
            {
                if (s.comp == null) continue;
                string area = AreaOf(s.comp.gameObject);
                string obj = Inspector.PathOf(s.comp.transform);
                if (s.timers.Count == 0) Put(fresh, new Entry { area = area, kind = "system", obj = obj, detail = s.typeName + ": " + s.desc });
                foreach (var t in s.timers)
                    Put(fresh, new Entry { area = area, kind = "timer", obj = obj, detail = s.typeName + "." + t.field + " @" + t.game.ToString("0.00", IC) + "s" + Sig(s.comp.gameObject) });
            }
            foreach (var w in AudioLinks.waits)
            {
                if (w.fsm == null) continue;
                string area = AreaOf(w.fsm.gameObject);
                Put(fresh, new Entry
                {
                    area = area,
                    kind = w.isCue ? "cue" : w.isBeat ? "beat" : "musictime",
                    obj = Inspector.PathOf(w.fsm.transform) + " / " + w.fsm.FsmName,
                    detail = w.kind + " in state [" + w.state + "] -> fires '" + w.finishEvent + "'"
                });
            }
            bool changed = false;
            foreach (var kv in fresh)
            {
                List<Entry> old;
                bool known = byArea.TryGetValue(kv.Key, out old);
                if (replace)
                {
                    if (known && Same(old, kv.Value)) continue;
                    byArea[kv.Key] = kv.Value;
                    changed = true;
                }
                else
                {
                    if (!known) { old = new List<Entry>(); byArea[kv.Key] = old; }
                    foreach (var e in kv.Value)
                    {
                        if (Has(old, e)) continue;
                        old.Add(e);
                        changed = true;
                    }
                }
            }
            if (!changed) return;
            dirty = true;
            Version++;
            Save();
        }

        static void Put(Dictionary<string, List<Entry>> into, Entry e)
        {
            List<Entry> l;
            if (!into.TryGetValue(e.area, out l)) { l = new List<Entry>(); into[e.area] = l; }
            foreach (var x in l) if (x.kind == e.kind && x.obj == e.obj && x.detail == e.detail) return;
            l.Add(e);
        }

        // what a row is about, without the parts that change while the game runs (a timer's time and signal list)
        static string Subject(Entry e)
        {
            if (e.kind != "timer") return e.detail;
            int at = e.detail.IndexOf(" @", StringComparison.Ordinal);
            return at < 0 ? e.detail : e.detail.Substring(0, at);
        }

        static bool Has(List<Entry> l, Entry e)
        {
            string subject = Subject(e);
            foreach (var x in l) if (x.kind == e.kind && x.obj == e.obj && Subject(x) == subject) return true;
            return false;
        }

        static bool Same(List<Entry> a, List<Entry> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].kind != b[i].kind || a[i].obj != b[i].obj || Clean(a[i].detail) != Clean(b[i].detail)) return false;
            return true;
        }

        static string Sig(GameObject go)
        {
            string s = AudioTimed.Outgoing(go);
            return s.Length > 0 ? "   signals: " + s : "";
        }

        // background: add what normal play streams in. Runs after each scan of the music-timed systems (which itself runs
        // after a streaming change, while the tool is in use); nothing is read or written between two scans.
        public static void AutoRecord()
        {
            int scan = AudioTimed.ScanCount;
            if (scan == seenTimedScan) return;
            int m = Mode;
            if (m == 2) return;
            if (m != 1 && !Background.WantedOrInUse(Background.Catalog)) return;
            seenTimedScan = scan;
            try { RecordLoaded(false); } catch (Exception e) { DevLog.Error("audio catalog auto", e); }
        }

        public static void CatalogAll()
        {
            if (!G.SavepointsReady) { DevLog.Write("catalog: start or continue a game first"); return; }
            for (int s = 0; s < SavepointManager.SubsceneCount; s++) Discovery.Enqueue(s, 0, Discovery.Action.Catalog);
            DevLog.Write("catalog: queued " + SavepointManager.SubsceneCount + " area(s) - each is loaded briefly, scanned and unloaded");
        }
    }
}
