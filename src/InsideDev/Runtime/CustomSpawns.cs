using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // User-defined spawn points, persisted in _mod\custom_spawns.cfg.
    // The game's own spawning is never modified: launching / continuing (Load.Startup) always uses the game's savepoint.
    //   Go            loads the entry's savepoint through the game (GameManager.LoadSavepoint), then teleports to the
    //                 stored position once loadFinished fired and the character is active.
    //   Respawn here  optional, one entry at a time: after a Load.Respawn (death / respawn during play) in the SAME area,
    //                 the character is moved to the entry. Respawns in other areas are left alone.
    public static class CustomSpawns
    {
        public sealed class Entry
        {
            public string name, subscene;   // subscene name is stable across runs (indices are looked up by name)
            public int savepoint;
            public Vector3 pos;
            public string created;
        }

        public static readonly List<Entry> entries = new List<Entry>();
        public static int respawnEntry = -1;         // index into entries, -1 = off
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static bool loaded, hooked;
        static Entry pending; static string pendingWhy;
        static bool loadDone; static int settleFrames;
        public static string lastStatus = "";

        static string PathCfg { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "custom_spawns.cfg"); } }

        // ---------------------------------------------------------------- persistence
        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(PathCfg)) return;
                string respawnName = null;
                foreach (var raw in File.ReadAllLines(PathCfg))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.StartsWith("respawn=")) { respawnName = line.Substring(8); continue; }
                    // name|subscene|savepoint|x|y|z|created
                    var p = line.Split('|');
                    if (p.Length < 6) continue;
                    entries.Add(new Entry
                    {
                        name = p[0], subscene = p[1], savepoint = int.Parse(p[2], IC),
                        pos = new Vector3(float.Parse(p[3], IC), float.Parse(p[4], IC), float.Parse(p[5], IC)),
                        created = p.Length > 6 ? p[6] : ""
                    });
                }
                respawnEntry = respawnName == null ? -1 : entries.FindIndex(e => e.name == respawnName);
                DevLog.Write("custom spawns: " + entries.Count + " loaded" + (respawnEntry >= 0 ? ", respawn point '" + entries[respawnEntry].name + "'" : ""));
            }
            catch (Exception e) { DevLog.Error("custom spawns load", e); }
        }

        static void Save()
        {
            try
            {
                var sb = new StringBuilder("# InsideDev custom spawns: name|subscene|savepoint|x|y|z|created\n");
                if (respawnEntry >= 0 && respawnEntry < entries.Count) sb.Append("respawn=").Append(entries[respawnEntry].name).Append('\n');
                foreach (var e in entries)
                    sb.Append(e.name.Replace("|", "/")).Append('|').Append(e.subscene).Append('|').Append(e.savepoint.ToString(IC)).Append('|')
                      .Append(e.pos.x.ToString("R", IC)).Append('|').Append(e.pos.y.ToString("R", IC)).Append('|').Append(e.pos.z.ToString("R", IC)).Append('|').Append(e.created).Append('\n');
                File.WriteAllText(PathCfg, sb.ToString());
            }
            catch (Exception e) { DevLog.Error("custom spawns save", e); }
        }

        // ---------------------------------------------------------------- actions
        public static string SaveHere(string name)
        {
            Load();
            var ch = G.MainCharacter;
            if (ch == null) return Status("no active character");
            int sub, sp;
            if (!G.CurrentSavepoint(out sub, out sp)) return Status("current savepoint unknown");
            string subName;
            try { subName = SavepointManager.GetSubsceneName(sub); } catch { return Status("subscene name unknown"); }
            if (string.IsNullOrEmpty(name)) name = "Spawn " + (entries.Count + 1) + " " + subName.TrimStart('#').Replace("_Gameplay", "");
            var e = new Entry { name = UniqueName(name), subscene = subName, savepoint = sp, pos = ch.pos3, created = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
            entries.Add(e);
            Save();
            return Status("saved '" + e.name + "' at " + e.pos.ToString("F2") + " (" + subName + " savepoint #" + sp + ")");
        }

        static string UniqueName(string n)
        {
            string b = n; int k = 2;
            while (entries.Exists(x => x.name == n)) n = b + " (" + (k++) + ")";
            return n;
        }

        public static void Delete(int i)
        {
            if (i < 0 || i >= entries.Count) return;
            var n = entries[i].name;
            if (respawnEntry == i) respawnEntry = -1; else if (respawnEntry > i) respawnEntry--;
            entries.RemoveAt(i);
            Save();
            Status("deleted '" + n + "'");
        }

        public static void Rename(int i, string n)
        {
            if (i < 0 || i >= entries.Count || string.IsNullOrEmpty(n)) return;
            entries[i].name = UniqueName(n.Trim());
            Save();
        }

        public static void SetRespawn(int i)
        {
            respawnEntry = respawnEntry == i ? -1 : i;
            Save();
            Status(respawnEntry >= 0 ? "respawn point: '" + entries[respawnEntry].name + "' (only after deaths/respawns in " + entries[respawnEntry].subscene + ")" : "respawn point off");
        }

        static int SubIndex(string subName)
        {
            try { for (int s = 0; s < SavepointManager.SubsceneCount; s++) if (SavepointManager.GetSubsceneName(s) == subName) return s; } catch { }
            return -1;
        }

        public static string Go(int i)
        {
            if (i < 0 || i >= entries.Count) return Status("no such spawn");
            var e = entries[i];
            int sub = SubIndex(e.subscene);
            if (sub < 0) return Status("area '" + e.subscene + "' not found");
            // already in that area and playing: just teleport
            int cs, csp;
            var ch = G.MainCharacter;
            bool here = ch != null && ch.isCharacterActive && !ch.isDead && G.CurrentSavepoint(out cs, out csp) && cs == sub;
            if (here) { G.Teleport(e.pos); return Status("teleported to '" + e.name + "'"); }
            pending = e; pendingWhy = "go"; loadDone = false; settleFrames = 0;
            G.LoadSavepoint(sub, e.savepoint);
            return Status("loading " + e.subscene + " #" + e.savepoint + ", then moving to '" + e.name + "'");
        }

        static string Status(string s) { lastStatus = s; DevLog.Write("[spawn] " + s); return s; }

        // ---------------------------------------------------------------- game hooks
        public static void Tick()
        {
            Load();
            if (!hooked)
            {
                try { GameManager.loadFinished += OnLoadFinished; hooked = true; }
                catch (Exception ex) { DevLog.Error("custom spawns hook", ex); hooked = true; }
            }
            if (pending == null || !loadDone) return;
            var ch = G.MainCharacter;
            bool loading = false;
            try { loading = GameManager.IsLoading(); } catch { }
            if (ch == null || !ch.isCharacterActive || loading) { settleFrames = 0; return; }
            if (++settleFrames < 20) return;   // let savepoint placement and camera settle first
            var e = pending; pending = null;
            G.Teleport(e.pos);
            Status("moved to '" + e.name + "' after " + pendingWhy);
        }

        static void OnLoadFinished(GameManager.Load load, Savepoint.CharacterType type)
        {
            try
            {
                if (pending != null) { loadDone = true; settleFrames = 0; return; }
                if (load != GameManager.Load.Respawn || respawnEntry < 0 || respawnEntry >= entries.Count) return;   // Startup/Chapter: game default
                var e = entries[respawnEntry];
                int sub, sp;
                if (!G.CurrentSavepoint(out sub, out sp) || SubIndex(e.subscene) != sub) return;   // respawned in another area: leave it
                pending = e; pendingWhy = "respawn"; loadDone = true; settleFrames = 0;
            }
            catch (Exception ex) { DevLog.Error("custom spawns loadFinished", ex); }
        }
    }
}
