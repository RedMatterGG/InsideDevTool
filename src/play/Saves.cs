using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // Saves.cs (owner WORLD_A, public API and the save schema frozen): the save file, orbs and endings (report 03
    // section 2). Rules baked in: never call User.Achievement.Acquire (it calls Steam); orb edits use only
    // User.Achievement.achievementProgression.Acquire / Release (local bits, still pushed to Steam at the next sign-in)
    // and need confirm:true; ending secret clears orb bits and needs confirm:true. Save guard: EditorState
    // play.save_guard (default off, Guard): only when it is on do loads, edits and teleports that write INSIDE.sav back
    // it up first, need confirm or get refused, and does a cheat under the lock keep it (orb edits and the ending secret
    // keep their confirm: they touch the achievement bits). Backup: before the first load or
    // edit that can write INSIDE.sav in a game session, EnsureBackup copies the save (Steam userdata remote file, or the
    // in-memory DataMap bytes when the file is not found) to _mod/save_backups/INSIDE_<yyyyMMdd_HHmmss>.sav and returns
    // the path; later calls in the same session return the same path. lock = UserStoragePlatform.suspended by
    // reflection (INFERRED): writes stop, dirty flags stay, unlock writes the newest in-memory state.
    // Backup sources, best first: the cloud file on disk (<Steam>/userdata/<account>/304430/remote/INSIDE.sav, Steam root
    // from HKCU\Software\Valve\Steam SteamPath or the game's steamapps folder, account = UserSteam.steamID), then
    // SteamRemoteStorage.FileRead("INSIDE.sav") (what StorageSteam.LoadFromFile reads; read-only), then
    // UserPlatform.dataMap.Buffer (the last loaded or written 2048 bytes). The lock is released when the game quits
    // (RuntimeCallbacks.applicationQuit) and pending writes are handed to Steam then (StorageBase.UpdateStorage), unless
    // a cheat ran under the lock (MarkCheat: quiet load, cheat checkpoint, a savepoint fired or entered while locked):
    // then the lock stays at quit and INSIDE.sav keeps its pre-lock bytes (no DataMap.Save runs while suspended,
    // FP/StorageBase.cs:138-144), restore keeps the lock, and only unlock with confirm:true writes the in-memory state.
    // Ending secret is a replica of FinalSecretFade.SetFadeState (AC/FinalSecretFade.cs:96-101) without its
    // User.Achievement.Acquire(PowerSecret) Steam call: User.Achievement.ResetProgression (local clear) then
    // GameEventManager.StartNewGame.
    // Game code checked: User.Storage / Achievement / GameProgression / Brightness / Rumble / IsInitialized / userPlatform
    // (FP/User.cs:5-47); UserPlatform.dataMap / storage (FP/UserPlatform.cs:18,24); DataMap.Buffer (FP/DataMap.cs:533);
    // UserStoragePlatform private suspended / IsSuspended (FP/UserStoragePlatform.cs:5,29-32); StorageBase IsSaving /
    // UpdateStorage / UpdateSave gate (FP/StorageBase.cs:69,79-83,138-144); StorageSteam kFileName and SteamRemoteStorage
    // reads (FP/StorageSteam.cs:18,91-104); UserSteam private steamID, IsSteamInitialized, INSIDE_APPID
    // (FP/UserSteam.cs:10-14,56); Steamworks SteamRemoteStorage.FileExists / GetFileSize / FileRead, CSteamID.m_SteamID
    // (Steamworks.NET SteamRemoteStorage.cs:15,95,109, CSteamID.cs:17); AchievementPlatform.EAchievements,
    // achievementProgression, AchievementProgression achievementsMap / recentlyMap / Acquire / Release / IsAcquired,
    // ResetProgression (FP/AchievementPlatform.cs:5-87,100,152-155); AchievementSteam.ResetProgression (main app only,
    // FP/AchievementSteam.cs:116-122) and the sign-in push (:102-115); Savegame.SaveHash (FP/Savegame.cs:326); WristSecret
    // instance / isActive / Activate (FP/WristSecret.cs); SpawnManager re-activates the wrist from bit 13 on every spawn
    // (FP/SpawnManager.cs:210-213); EndCredits private SignalIn show (AC/EndCredits.cs:25,64-66,77-81); SignalIn.action
    // (FP/SignalIn.cs:14); CreditsOverlay.HideCredits / IsCompleted (FP/CreditsOverlay.cs:118-126); GameEventManager
    // .StartNewGame (FP/GameEventManager.cs:145); Savepoint subsceneIndex / index / isCheckpointOnly / isDynamic /
    // getEnabledState (FP/Savepoint.cs:26-36,148); RuntimeCallbacks.applicationQuit (FP/RuntimeCallbacks.cs:75,151).
    public static class Saves
    {
        public const string Owner = "WORLD_A";

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("save", "Save, orbs, endings", PlayShared.Dev, Owner,
                "The Steam Cloud save INSIDE.sav. info and savepoints_near are read-only; backup copies it to _mod/save_backups; lock / unlock stop and resume the game's save writes (released when the game quits). orbs reads or edits orb bits 0..13 and 14 (power secret), only local bits, confirm:true to edit; wrist_secret on / off; ending credits | secret | restart (secret clears orbs: confirm; restart = new game). Never touches Steam achievements directly. Save backups and save confirms apply only when the user set EditorState play.save_guard = 1.",
                Schema.Obj().Enum("op", "info, backup, lock, unlock, orbs, wrist_secret, ending or savepoints_near.", "info",
                        "info", "backup", "lock", "unlock", "orbs", "wrist_secret", "ending", "savepoints_near")
                    .Enum("action", "op orbs.", "read", "read", "set", "clear").Arr("bits", "op orbs: bits 0..14.", Schema.Int(0, 14), 0, 15)
                    .Enum("layer", "op orbs: ever, recent or both.", "both", "ever", "recent", "both").Bool("on", "op wrist_secret.")
                    .Enum("kind", "op ending.", null, "credits", "secret", "restart").Num("radius", "op savepoints_near (m).", 30, 1, 500)
                    .Bool("confirm", "Required for orb edits and ending secret; with play.save_guard on also for ending restart and unlock after a cheat.", false).Build(),
                Schema.Obj().Enum("outcome", "", null, "done", "refused")
                    .OutObj("info", "Save state.", Schema.Obj().OutObj("spawn", "", Schema.Obj().Int("subscene", "", -1, 999).Int("savepoint", "", -1, 999))
                        .OutObj("progression", "", Schema.Obj().Int("subscene", "", -1, 999).Int("savepoint", "", -1, 999))
                        .OutObj("unlocked", "", Schema.Obj().Int("subscene", "", -1, 999).Int("savepoint", "", -1, 999))
                        .Num("brightness", "", 0, 10).Bool("rumble", "").Str("hash", "Savegame.SaveHash.").Bool("saving", "").Bool("locked", "").Str("backup", "This session's backup."))
                    .OutObj("orbs", "Orb bits.", Schema.Obj().Arr("ever", "", Schema.Int(0, 14), 0, 15).Arr("recent", "", Schema.Int(0, 14), 0, 15))
                    .OutObj("backup", "op backup.", Schema.Obj().Str("path", "").Int("bytes", "", 0, 100000000).Str("source", "file or memory"))
                    .Arr("savepoints", "op savepoints_near.", Schema.OutOf(Schema.Obj().Str("id", "").Int("subscene", "", -1, 999).Int("savepoint", "", -1, 999)
                        .Str("name", "").Num("dist", "", 0, 1e5).Bool("checkpoint_only", "").Bool("dynamic", "").Bool("enabled", "")), 0, 100)
                    .Arr("warnings", "", Schema.Str(), 0, 20).BuildOutput()).Destructive(), Handle);
            PlayRegistry.OnTick("saves", Tick);
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "info");
            bool confirm = PlayShared.Bool(a, "confirm", false);
            var w = new List<string>();
            switch (op)
            {
                case "info":
                    return ToolResult.Ok(ctx.Main(() => Result("done", true, true, null)));
                case "backup":
                {
                    var b = MakeBackup(ctx);
                    lock (makeGate) { if (backupPath == null) backupPath = b.path; }
                    return ToolResult.Ok(ctx.Main(() => { var o = Result("done", true, false, null); o["backup"] = b.ToJson(); return o; }));
                }
                case "lock":
                case "unlock":
                {
                    bool on = op == "lock";
                    if (on && Guard) EnsureBackup(ctx);
                    return ToolResult.Ok(ctx.Main(() =>
                    {
                        string cheat = cheatReason;
                        if (!on && lockedByUs && cheat != null && !confirm && Guard)
                        {
                            w.Add("unlock refused: a cheat ran under the lock (" + cheat + "); unlocking writes it into INSIDE.sav (Steam Cloud). Repeat with confirm:true to write it, or quit the game to drop it (the lock is kept at quit)");
                            return Result("refused", true, false, w);
                        }
                        bool ok = SetLock(on);
                        if (!ok) w.Add(on ? "lock: UserStoragePlatform.suspended is not reachable" : "unlock: the lock is held by the game (a storage message is up) or was not set through MCP");
                        else w.Add(on ? "locked: the game keeps playing but INSIDE.sav is not written until unlock, restore {include_save_lock:true} or quit (a cheat under the lock keeps it at quit)" : "unlocked: the newest in-memory state is written within a frame" + (cheat != null ? " (including the cheat: " + cheat + ")" : ""));
                        return Result(ok ? "done" : "refused", true, false, w);
                    }));
                }
                case "orbs":
                    return Orbs(a, ctx, confirm, w);
                case "wrist_secret":
                {
                    if (!PlayShared.Has(a, "on")) throw new McpException(McpErr.BadArgs, "op wrist_secret needs on (true / false)");
                    bool on = PlayShared.Bool(a, "on", true);
                    return ToolResult.Ok(ctx.Main(() =>
                    {
                        var ws = WristSecret.instance;
                        if (ws == null) throw new McpException(McpErr.NotFound, "WristSecret is not in the scene yet", "wait until the game shows the world");
                        bool orig = ws.isActive;
                        if (on) ws.Activate(); else ws.isActive = false;
                        PlayLevels.Reg("wrist_secret", "wrist secret (WristSecret.isActive)", () => { var x = WristSecret.instance; if (x != null) x.isActive = orig; return "isActive back to " + (orig ? "on" : "off"); });
                        w.Add("wrist_secret: " + (on ? "on" : "off") + " now; every spawn sets it again from orb bit 13 (on when bit 13 is set)");
                        var o = Result("done", false, true, w);
                        o["wrist_secret"] = ws.isActive;
                        return o;
                    }));
                }
                case "ending":
                    return Ending(PlayShared.Str(a, "kind", null), ctx, confirm, w);
                case "savepoints_near":
                {
                    double radius = PlayShared.Num(a, "radius", 30);
                    return ToolResult.Ok(ctx.Main(() => Near(radius)));
                }
            }
            throw new McpException(McpErr.BadArgs, "op: unknown " + op);
        }

        // any thread (uses ctx.Main as needed): this session's backup path; throws McpException game_error when no
        // backup could be made (callers must then refuse the write)
        public static string EnsureBackup(CallContext ctx)
        {
            string p = backupPath;
            if (p != null) return p;
            lock (makeGate)
            {
                if (backupPath != null) return backupPath;
                var b = MakeBackup(ctx);
                backupPath = b.path;
                ctx.Log("save backup: " + b.path + " (" + b.bytes + " bytes from " + b.source + ")");
                return b.path;
            }
        }

        public static string BackupPath { get { return backupPath; } }

        // any thread: EditorState play.save_guard (default off), read on the main thread every 30 frames; off = no save
        // backup, no save confirm, no save refusal, the lock is never kept for a cheat
        public static bool Guard { get { return guard; } }

        // main thread
        public static bool Locked
        {
            get
            {
                var st = Storage();
                var f = SuspendedField();
                if (st == null || f == null) return false;
                try { return (bool)f.GetValue(st); } catch { return false; }
            }
        }

        public static bool SetLock(bool on)
        {
            var st = Storage();
            var f = SuspendedField();
            if (st == null || f == null) return false;
            bool cur = (bool)f.GetValue(st);
            if (on)
            {
                if (!cur) f.SetValue(st, true);
                if (!lockedByUs && !cur)
                {
                    lockedByUs = true;
                    cheatReason = null;
                    RegisterLockUndo();
                }
                return true;
            }
            if (!lockedByUs) return !cur;
            f.SetValue(st, false);
            lockedByUs = false;
            cheatReason = null;
            PlayLevels.Forget("save_lock");
            return true;
        }

        // main thread: something the user's save must not get happened while MCP holds the lock (quiet load, cheat
        // checkpoint, a savepoint fired or entered under the lock); from then on quit and restore keep the lock
        public static void MarkCheat(string what)
        {
            if (!Guard || !lockedByUs || cheatReason != null) return;
            cheatReason = what ?? "cheat";
            DevLog.Write("[play] save lock: " + cheatReason + " ran under the lock; INSIDE.sav keeps its pre-lock state unless save {op:\"unlock\", confirm:true}");
        }

        // the cheat that keeps the lock (null = none)
        public static string Cheat { get { return lockedByUs ? cheatReason : null; } }

        static void RegisterLockUndo()
        {
            PlayLevels.Reg("save_lock", "save write lock (UserStoragePlatform.suspended)", () =>
            {
                string cheat = cheatReason;
                if (cheat != null && lockedByUs)
                {
                    RegisterLockUndo();
                    return "kept: a cheat ran under the lock (" + cheat + "); INSIDE.sav keeps its pre-lock state; save {op:\"unlock\", confirm:true} writes the in-memory state, quitting drops it";
                }
                bool r = SetLock(false);
                return r ? "save writes resumed (the newest in-memory state is written)" : "the lock is held by the game";
            });
        }

        // ---------------------------------------------------------------- state
        static readonly object makeGate = new object();
        static volatile string backupPath, cheatReason;
        static volatile bool lockedByUs, hooked, guard;
        static int ticks;
        static FieldInfo suspendedField, steamIdField;
        static bool suspendedMissing;

        sealed class Backup
        {
            public string path, source, from;
            public int bytes;

            public Json.Obj ToJson()
            {
                var o = new Json.Obj();
                o["path"] = path;
                o["bytes"] = (double)bytes;
                o["source"] = source;
                if (from != null) o["from"] = from;
                return o;
            }
        }

        sealed class Sources
        {
            public string modDir;
            public List<string> files = new List<string>();
            public List<string> roots = new List<string>();
            public uint account;
            public byte[] steam, memory;
            public string problem;
        }

        static void Tick()
        {
            if (++ticks % 30 == 1)
            {
                try { guard = EditorState.Get("play.save_guard", false); } catch { }
            }
            if (hooked) return;
            hooked = true;
            try { RuntimeCallbacks.applicationQuit += OnQuit; }
            catch (Exception e) { DevLog.Error("[play] saves quit hook", e); }
        }

        static void OnQuit()
        {
            if (!lockedByUs) return;
            string cheat = cheatReason;
            if (cheat != null)
            {
                DevLog.Write("[play] save lock kept at quit: " + cheat + " ran under it, so INSIDE.sav keeps its pre-lock state" + (backupPath != null ? " (backup " + backupPath + ")" : ""));
                return;
            }
            try
            {
                SetLock(false);
                var st = Storage();
                if (st != null) st.UpdateStorage();
                DevLog.Write("[play] save lock released at quit; pending writes handed to Steam");
            }
            catch (Exception e) { DevLog.Error("[play] save lock release at quit", e); }
        }

        static UserStoragePlatform Storage()
        {
            try { return User.IsInitialized ? User.Storage : null; } catch { return null; }
        }

        static FieldInfo SuspendedField()
        {
            if (suspendedField != null || suspendedMissing) return suspendedField;
            suspendedField = typeof(UserStoragePlatform).GetField(WorldAMembers.Suspended, BindingFlags.NonPublic | BindingFlags.Instance);
            if (suspendedField == null) { suspendedMissing = true; DevLog.Write("[play] UserStoragePlatform.suspended not found: save lock unavailable"); }
            return suspendedField;
        }

        // worker: gather on the main thread, read and write files here
        static Backup MakeBackup(CallContext ctx)
        {
            var s = ctx.Main(() => Gather());
            if (s.modDir == null) throw new McpException(McpErr.GameError, "no backup: the _mod folder is unknown", "refusing the save write");
            byte[] data = null;
            string source = null, from = null;
            var files = new List<string>(s.files);
            if (files.Count == 0) files.AddRange(ScanUserdata(s.roots));
            foreach (var f in files)
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    var b = File.ReadAllBytes(f);
                    if (b.Length == 0) continue;
                    data = b; source = "file"; from = f;
                    break;
                }
                catch { }
            }
            if (data == null && s.steam != null && s.steam.Length > 0) { data = s.steam; source = "steam"; from = "SteamRemoteStorage " + SavePaths.FileName; }
            if (data == null && s.memory != null && s.memory.Length > 0) { data = s.memory; source = "memory"; from = "UserPlatform.dataMap.Buffer"; }
            if (data == null) throw new McpException(McpErr.GameError, "no backup could be made: INSIDE.sav was not found on disk, Steam and memory gave nothing" + (s.problem != null ? " (" + s.problem + ")" : ""), "refusing the save write; copy INSIDE.sav by hand first");
            string dir = Path.Combine(s.modDir, BackupNames.Folder);
            try
            {
                Directory.CreateDirectory(dir);
                string path = BackupNames.Unique(dir, DateTime.Now, File.Exists);
                File.WriteAllBytes(path, data);
                return new Backup { path = path, bytes = data.Length, source = source, from = from };
            }
            catch (Exception e) { throw new McpException(McpErr.GameError, "no backup could be made: " + e.GetType().Name + ": " + e.Message, "refusing the save write"); }
        }

        static IEnumerable<string> ScanUserdata(List<string> roots)
        {
            var found = new List<string>();
            foreach (var r in roots)
            {
                try
                {
                    string ud = Path.Combine(r, "userdata");
                    if (!Directory.Exists(ud)) continue;
                    foreach (var acc in Directory.GetDirectories(ud))
                    {
                        string p = Path.Combine(Path.Combine(Path.Combine(acc, SavePaths.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "remote"), SavePaths.FileName);
                        if (File.Exists(p) && !found.Contains(p)) found.Add(p);
                    }
                }
                catch { }
            }
            return found.Count == 1 ? found : new List<string>();
        }

        // main thread
        static Sources Gather()
        {
            var s = new Sources();
            try { s.modDir = Extensions.Dir; } catch { }
            if (s.modDir == null) try { s.modDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"); } catch { }
            AddRoot(s.roots, RegistryString("HKEY_CURRENT_USER\\Software\\Valve\\Steam", "SteamPath"));
            AddRoot(s.roots, RegistryString("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Valve\\Steam", "InstallPath"));
            AddRoot(s.roots, RegistryString("HKEY_LOCAL_MACHINE\\SOFTWARE\\Valve\\Steam", "InstallPath"));
            try { AddRoot(s.roots, SavePaths.RootFromDataPath(Application.dataPath)); } catch { }
            UserSteam us = null;
            try { if (User.IsInitialized) us = SingletonBehaviour<User>.instance.userPlatform as UserSteam; } catch { }
            if (us != null)
            {
                try
                {
                    if (steamIdField == null) steamIdField = typeof(UserSteam).GetField(WorldAMembers.SteamId, BindingFlags.NonPublic | BindingFlags.Instance);
                    object id = steamIdField != null ? steamIdField.GetValue(us) : null;
                    var vf = id != null ? id.GetType().GetField(WorldAMembers.SteamIdValue) : null;
                    if (vf != null) s.account = SavePaths.AccountId((ulong)vf.GetValue(id));
                }
                catch (Exception e) { s.problem = "steam id: " + e.Message; }
                s.files = SavePaths.Candidates(s.roots, s.account, SavePaths.AppId);
                try { if (us.IsSteamInitialized) s.steam = SteamRead(); } catch (Exception e) { s.problem = "steam read: " + e.Message; }
            }
            try
            {
                if (User.IsInitialized)
                {
                    var dm = SingletonBehaviour<User>.instance.userPlatform.dataMap;
                    if (dm != null && dm.Buffer != null) s.memory = (byte[])dm.Buffer.Clone();
                }
            }
            catch { }
            return s;
        }

        static void AddRoot(List<string> roots, string r)
        {
            r = SavePaths.Normalize(r);
            if (r != null && !roots.Contains(r)) roots.Add(r);
        }

        static string RegistryString(string key, string name)
        {
            try { return Microsoft.Win32.Registry.GetValue(key, name, null) as string; } catch { return null; }
        }

        static byte[] SteamRead()
        {
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("Steamworks.SteamRemoteStorage", false); } catch { }
                if (t != null) break;
            }
            if (t == null) return null;
            var exists = t.GetMethod("FileExists", new[] { typeof(string) });
            var size = t.GetMethod("GetFileSize", new[] { typeof(string) });
            var read = t.GetMethod("FileRead", new[] { typeof(string), typeof(byte[]), typeof(int) });
            if (exists == null || size == null || read == null) return null;
            if (!(bool)exists.Invoke(null, new object[] { SavePaths.FileName })) return null;
            int n = (int)size.Invoke(null, new object[] { SavePaths.FileName });
            if (n <= 0 || n > (1 << 20)) return null;
            var buf = new byte[n];
            int got = (int)read.Invoke(null, new object[] { SavePaths.FileName, buf, n });
            return got == n ? buf : null;
        }

        // ---------------------------------------------------------------- ops
        static ToolResult Orbs(Json.Obj a, CallContext ctx, bool confirm, List<string> w)
        {
            string action = PlayShared.Str(a, "action", "read");
            string layer = PlayShared.Str(a, "layer", "both");
            var bits = new List<int>();
            var l = PlayShared.List(a, "bits");
            if (l != null) foreach (var x in l) if (x is double) bits.Add((int)Math.Round((double)x));
            string code, hint;
            string err = SavePolicy.OrbError(action, bits, confirm, out code, out hint);
            if (err != null) throw new McpException(code, err, hint);
            if (action == "read") return ToolResult.Ok(ctx.Main(() => Result("done", false, true, null)));
            if (Guard) EnsureBackup(ctx);
            bool set = action == "set";
            return ToolResult.Ok(ctx.Main(() =>
            {
                if (!User.IsInitialized) throw new McpException(McpErr.NotReady, "the user platform is not signed in yet");
                var ap = User.Achievement.achievementProgression;
                if (layer == "both")
                {
                    foreach (var b in bits)
                    {
                        var e = (AchievementPlatform.EAchievements)b;
                        if (set) ap.Acquire(e); else ap.Release(e);
                    }
                }
                else if (layer == "ever") ap.achievementsMap.Value = OrbBits.Apply(ap.achievementsMap.Value, bits, set);
                else ap.recentlyMap.Value = OrbBits.Apply(ap.recentlyMap.Value, bits, set);
                w.Add("orbs: local bits only (achievementProgression); INSIDE.sav is written next frame" + (Locked ? " after unlock" : ""));
                if (set) w.Add("steam: set bits become Steam achievements at the next game start (AchievementSteam pushes every local bit on sign-in)");
                w.Add("visible: secret pods read the bits in Start, so the change shows after the subscene reloads");
                if (bits.Contains(OrbBits.WristBit)) w.Add("wrist: bit 13 turns the wrist secret on at every spawn");
                return Result("done", false, true, w);
            }));
        }

        static ToolResult Ending(string kind, CallContext ctx, bool confirm, List<string> w)
        {
            string code, hint;
            string err = SavePolicy.EndingError(kind, confirm, Guard, out code, out hint);
            if (err != null) throw new McpException(code, err, hint);
            if (kind == "credits")
            {
                return ToolResult.Ok(ctx.Main(() =>
                {
                    var ec = UnityEngine.Object.FindObjectOfType(typeof(EndCredits)) as EndCredits;
                    if (ec == null) throw new McpException(McpErr.NotFound, "EndCredits is not loaded (level137 Credits)", "load the last chapter or walk to the end first; ending restart works anywhere");
                    var f = typeof(EndCredits).GetField(WorldAMembers.CreditsShow, BindingFlags.NonPublic | BindingFlags.Instance);
                    var si = f != null ? f.GetValue(ec) as SignalIn : null;
                    if (si == null || si.action == null) throw new McpException(McpErr.GameError, "EndCredits.show is not set up (PreAwake has not run)", "wait a few frames and retry");
                    si.action();
                    w.Add("credits: EndCredits.show fired; slates follow after about 5 s; at the end the game waits for a face button on the real controller, save {op:\"ending\", kind:\"restart\", confirm:true} restarts instead");
                    return Result("done", false, false, w);
                }));
            }
            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return busy ?? ToolResult.Fail(McpErr.Busy, "the control lane is busy", "wait for the running action, or call restore");
            if (Guard) EnsureBackup(ctx);
            return ToolResult.Ok(ctx.Main(() =>
            {
                if (PlayLevels.Loading || PlayLevels.GM() == null || PlayLevels.LoadingNow()) throw new McpException(McpErr.Busy, "busy: a load is in progress", "wait_until {ready:true}, then retry");
                if (kind == "secret")
                {
                    User.Achievement.ResetProgression();
                    w.Add("secret: replica of FinalSecretFade without its Steam call (User.Achievement.Acquire is never used): every local orb bit cleared, new game started");
                }
                else
                {
                    try { var co = SingletonBehaviour<CreditsOverlay>.instance; if (co != null && !co.IsCompleted()) co.HideCredits(); } catch { }
                    w.Add("restart: new game started (spawn and progression 0:0 in INSIDE.sav; unlocked chapters and orb bits stay)");
                }
                GameEventManager.StartNewGame();
                w.Add("loading: wait_until {ready:true}; at the title the boy waits for a button: menu {op:\"submit\"} starts the intro");
                return Result("done", true, true, w);
            }));
        }

        static Json.Obj Near(double radius)
        {
            var o = PlayShared.Env();
            o["outcome"] = "done";
            var ch = ScriptGlobals.mainCharacter;
            if (ch == null) throw new McpException(McpErr.NotReady, "no active character", "wait_until {ready:true}");
            var p = ch.transform.position;
            var rows = new List<KeyValuePair<double, Json.Obj>>();
            foreach (var x in UnityEngine.Object.FindObjectsOfType(typeof(Savepoint)))
            {
                var sp = x as Savepoint;
                if (sp == null) continue;
                double d = Vector3.Distance(sp.transform.position, p);
                if (d > radius) continue;
                var r = new Json.Obj();
                string id = null;
                try { id = Observer.IdFor(sp, "o"); } catch { }
                r["id"] = id ?? ("#" + sp.GetInstanceID());
                r["subscene"] = (double)sp.subsceneIndex;
                r["savepoint"] = (double)sp.index;
                r["name"] = sp.name;
                r["dist"] = PlayShared.R(d);
                r["checkpoint_only"] = sp.isCheckpointOnly;
                r["dynamic"] = sp.isDynamic;
                r["enabled"] = sp.getEnabledState();
                r["reached"] = SpIndex.Compare(new SpIndex(sp.subsceneIndex, sp.index), PlayLevels.Progression()) <= 0;
                rows.Add(new KeyValuePair<double, Json.Obj>(d, r));
            }
            rows.Sort((u, v) => u.Key.CompareTo(v.Key));
            var l = new List<object>();
            foreach (var r in rows) if (l.Count < 100) l.Add(r.Value);
            o["savepoints"] = l;
            return o;
        }

        // main thread: {ok, outcome, info?, orbs?, warnings}
        static Json.Obj Result(string outcome, bool info, bool orbs, List<string> warnings)
        {
            var o = PlayShared.Env();
            o["outcome"] = outcome;
            if (info) o["info"] = Info();
            if (orbs) o["orbs"] = OrbsJson();
            PlayLevels.AddWarnings(o, warnings);
            return o;
        }

        static Json.Obj Info()
        {
            var o = new Json.Obj();
            o["spawn"] = IdxJson(PlayLevels.Spawn());
            o["progression"] = IdxJson(PlayLevels.Progression());
            o["unlocked"] = IdxJson(PlayLevels.Unlocked());
            bool init = false;
            try { init = User.IsInitialized; } catch { }
            o["brightness"] = init ? PlayShared.R(User.Brightness, 3) : 0.0;
            o["rumble"] = init && User.Rumble;
            int hash = 0;
            try { hash = Savegame.SaveHash; } catch { }
            o["hash"] = hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
            bool saving = false;
            try { var st = Storage(); saving = st != null && st.IsSaving(); } catch { }
            o["saving"] = saving;
            o["locked"] = Locked;
            o["backup"] = BackupPath ?? "";
            if (lockedByUs) o["locked_by"] = "mcp";
            if (lockedByUs && cheatReason != null) o["cheat_under_lock"] = cheatReason;
            return o;
        }

        static Json.Obj OrbsJson()
        {
            var o = new Json.Obj();
            uint ever = 0, recent = 0;
            try
            {
                if (User.IsInitialized)
                {
                    var ap = User.Achievement.achievementProgression;
                    ever = ap.achievements;
                    recent = ap.recently;
                }
            }
            catch { }
            o["ever"] = OrbBits.ToList(ever);
            o["recent"] = OrbBits.ToList(recent);
            return o;
        }

        static Json.Obj IdxJson(SpIndex i)
        {
            var o = new Json.Obj();
            o["subscene"] = (double)i.sub;
            o["savepoint"] = (double)i.sp;
            return o;
        }
    }
}
