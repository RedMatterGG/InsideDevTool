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
        // lines of the file that are not a spawn (damaged, or written by a newer build): kept and written back as they are
        static readonly List<string> keptLines = new List<string>();
        static bool keepBadFile;   // the file could not be read at all: copied to custom_spawns.cfg.bad before the first save
        static Entry pending; static string pendingWhy; static float pendingAt; static int pendingSub = -1;
        const float PendingTimeout = 60f;
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
                string respawnName = null; int bad = 0;
                foreach (var raw in File.ReadAllLines(PathCfg))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("respawn=", StringComparison.Ordinal)) { respawnName = line.Substring(8).Trim(); continue; }
                    // name|subscene|savepoint|x|y|z|created
                    var p = line.Split('|');
                    int sp; float x, y, z;
                    if (p.Length < 6 || !Fmt.TryI(p[2].Trim(), out sp) || !Fmt.TryF(p[3].Trim(), out x) || !Fmt.TryF(p[4].Trim(), out y) || !Fmt.TryF(p[5].Trim(), out z))
                    {
                        keptLines.Add(raw); bad++;
                        continue;
                    }
                    entries.Add(new Entry { name = p[0], subscene = p[1], savepoint = sp, pos = new Vector3(x, y, z), created = p.Length > 6 ? p[6] : "" });
                }
                respawnEntry = respawnName == null ? -1 : entries.FindIndex(e => e.name == respawnName || Clean(e.name) == Clean(respawnName));
                DevLog.Write("custom spawns: " + entries.Count + " loaded" + (respawnEntry >= 0 ? ", respawn point '" + entries[respawnEntry].name + "'" : "") + (bad > 0 ? ", " + bad + " line(s) not understood (kept in the file as they are)" : ""));
            }
            catch (Exception e)
            {
                keepBadFile = File.Exists(PathCfg);
                DevLog.Error("custom spawns load (the file is kept as custom_spawns.cfg.bad before anything is saved)", e);
            }
        }

        // a name that survives the file format: no field separator, not a comment, not the respawn line
        static string Clean(string n)
        {
            n = (n ?? "").Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ').Trim();
            n = n.TrimStart('#').Trim();
            if (n.StartsWith("respawn=", StringComparison.Ordinal)) n = "respawn " + n.Substring(8).Trim();
            return n.Length > 0 ? n : "Spawn";
        }

        static void Save()
        {
            try
            {
                var sb = new StringBuilder("# InsideDev custom spawns: name|subscene|savepoint|x|y|z|created\n");
                if (respawnEntry >= 0 && respawnEntry < entries.Count) sb.Append("respawn=").Append(Clean(entries[respawnEntry].name)).Append('\n');
                foreach (var e in entries)
                    sb.Append(Clean(e.name)).Append('|').Append((e.subscene ?? "").Replace('|', '/')).Append('|').Append(e.savepoint.ToString(IC)).Append('|')
                      .Append(e.pos.x.ToString("R", IC)).Append('|').Append(e.pos.y.ToString("R", IC)).Append('|').Append(e.pos.z.ToString("R", IC)).Append('|').Append((e.created ?? "").Replace('|', '/')).Append('\n');
                foreach (var l in keptLines) sb.Append(l).Append('\n');
                if (keepBadFile) { File.Copy(PathCfg, PathCfg + ".bad", true); keepBadFile = false; }
                SafeFile.WriteAllText(PathCfg, sb.ToString(), true);
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
            var e = new Entry { name = UniqueName(name), subscene = subName, savepoint = sp, pos = ch.pos3, created = DateTime.Now.ToString("yyyy-MM-dd HH:mm", IC) };
            entries.Add(e);
            Save();
            return Status("saved '" + e.name + "' at " + Fmt.V3(e.pos, "F2") + " (" + subName + " savepoint #" + sp + ")");
        }

        static string UniqueName(string n)
        {
            n = Clean(n);
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
            pending = e; pendingWhy = "go"; loadDone = false; settleFrames = 0; pendingAt = Time.realtimeSinceStartup; pendingSub = sub;
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
            if (pending == null) return;
            // the game did not do the load that was asked for (menu, cutscene, another load running): forget the
            // request instead of moving the boy after some later, unrelated load
            if (Time.realtimeSinceStartup - pendingAt > PendingTimeout)
            {
                var gone = pending; pending = null;
                Status("the load for '" + gone.name + "' did not finish within " + Fmt.F(PendingTimeout, "0") + " s: not moved");
                return;
            }
            if (!loadDone) return;
            var ch = G.MainCharacter;
            bool loading = false;
            try { loading = GameManager.IsLoading(); } catch { }
            if (ch == null || !ch.isCharacterActive || loading) { settleFrames = 0; return; }
            if (++settleFrames < 20) return;   // let savepoint placement and camera settle first
            var e = pending; pending = null;
            int nowSub, nowSp;
            if (pendingSub >= 0 && (!G.CurrentSavepoint(out nowSub, out nowSp) || nowSub != pendingSub))
            {
                Status("the load did not reach " + e.subscene + ": not moved to '" + e.name + "'");
                return;
            }
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
                pending = e; pendingWhy = "respawn"; loadDone = true; settleFrames = 0; pendingAt = Time.realtimeSinceStartup; pendingSub = sub;
            }
            catch (Exception ex) { DevLog.Error("custom spawns loadFinished", ex); }
        }
    }
}
