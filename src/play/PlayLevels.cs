using System;
using System.Collections.Generic;
using System.Reflection;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // PlayLevels.cs (owner WORLD_A, public API and the checkpoint / level / stream schemas frozen): loads, checkpoints,
    // readiness and streaming (report 03 sections 1, 2.1). The four load entry points are GameManager.LoadSavepoint,
    // Respawn, ReloadScene, StartNewGame; one load at a time (a request while one is pending is dropped by the game, so
    // StartLoad answers busy). Ready = IsPlaying, GameInput.IsEnabled, fader done, no title, AllCullersFinished, no
    // pending PreAwake, main character active, then settle_frames frames. Save safety, only with EditorState
    // play.save_guard on (Saves.Guard; off by default: no confirm, no backup): chapter mode, load_chapter, reload
    // chapter / newgame write INSIDE.sav (Steam Cloud) and unlock chapters forever, so they need confirm:true and call
    // Saves.EnsureBackup first; respawn mode writes the spawn too (backup, no confirm unless it would unlock the chapter
    // menu further or move the progression back). Quiet mode (INFERRED: spawn fields by reflection, the persistent-data
    // and progression rules of Savegame.SetSpawnpoint in memory, save lock + Respawn) writes nothing; with the guard on it
    // marks the lock (Saves.MarkCheat), so quitting keeps INSIDE.sav as it was before the lock. Freeze undo restores
    // the original CullingActive and leaves culling to a running load. Hints name the tools of the caller's profile.
    // Undiscovered subscenes are discovered first (core Discovery.Enqueue). After loadFinished nothing cached survives
    // (Observer epoch, Boy re-created).
    // Game code checked (FP/ = Assembly-CSharp-firstpass decompiled): GameManager Respawn / ReloadScene / StartNewGame /
    // LoadSavepoint (FP/GameManager.cs:144-195), IsLoading / IsPlaying / GameIsStarted / IsPaused / LoadCount / debugState
    // / fader (:93-123, 190-205), loadStarted / loadFinished (:136-142), private loadRoutine + StartSceneLoad drop rule
    // (:42, 294-324), FadeAndReload / Reload (:386-472); CameraFader.IsDone / Progress (FP/CameraFader.cs:19-21);
    // GameInput.IsEnabled (FP/GameInput.cs:314); TitleManager.IsShowing / IsPressStartShowing (FP/TitleManager.cs:349,370);
    // PreAwakeManager.PendingPreawake (FP/PreAwakeManager.cs:38); SubsceneCullingManager.instance / CullingActive /
    // AllCullersFinished / InsideViewfrustum (FP/SubsceneCullingManager.cs:76-98, 259, 476); SubsceneCuller LoadedState
    // / IsActive / InProgress / AlwaysLoaded / CullingBounds / LoadSubscene / ActivateSubsceneImmediate
    // (FP/SubsceneCuller.cs:11-27, 159-225, 328, 338); SavepointManager SubsceneCount / GetCuller / GetSubsceneInfos /
    // GetSubsceneSavepointCount / IsSavepointValid / SavepointTask FindNext / FindPrevious / IsCompleted / Failed /
    // savepointIndex (FP/SavepointManager.cs:138-275, 473, 530-629); SubsceneInfo name / puzzleName / areaName /
    // savepointCount (:36-42); Savegame GetCurrentSavepoint / GetCurrentProgression / SetSavepoint, private
    // savegameDataMapItem with public spawnSubscene / spawnSavepoint (FP/Savegame.cs:29-31, 274, 377-420);
    // SavepointData.scenes / Scene.name / GetSavepointIndex (FP/SavepointData.cs:7-27); User.GameProgression.GetSavepoint
    // (FP/User.cs:11, FP/GameProgressionPlatform.cs:56); ScriptGlobals.mainCharacter / mainCharacterActive
    // (FP/ScriptGlobals.cs:15-17); StreamManager.LoadStrategy (FP/StreamManager.cs:15); SpawnManager.SpawnAtSavepoint
    // (FP/SpawnManager.cs:67-94: an invalid index searches forward and rewrites the save, so targets are validated first).
    public sealed class LoadRequest
    {
        // chapter | respawn | quiet | reload_respawn | reload_chapter | newgame
        public string mode = "respawn";
        public int subscene = -1, savepoint = 0;
        public bool wait = true;
        public int timeoutMs = 45000, settleFrames = 20;
        public bool confirm;
        internal bool cheat;
        internal string label, profile;
    }

    public static class PlayLevels
    {
        public const string Owner = "WORLD_A";

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("checkpoint", "Checkpoints", PlayShared.Play, Owner,
                "Checkpoints (savepoints). current / list are read-only. reload = respawn at the last checkpoint (like dying, no save write; also skips the death wait). next / prev / goto jump to another checkpoint: a cheat, only when the user enabled play.cheats (EditorState play.cheats = 1); it writes the new spawn into INSIDE.sav. Waits until playable unless wait is false; a load still running after timeout_s comes back as job.",
                Schema.Obj().Enum("op", "current, list, reload, next, prev or goto.", "current", "current", "list", "reload", "next", "prev", "goto")
                    .Int("subscene", "op goto: savepoint subscene index (0..82, see list).", 0, 999).Int("savepoint", "op goto: savepoint index.", 0, 999)
                    .Str("filter", "op list: name filter.").Int("limit", "op list: rows.", 40, 1, 200).Int("offset", "op list: first row.", 0, 0, 1000)
                    .Bool("wait", "Wait until playable after a load.", true).Num("timeout_s", "Wait limit; when it runs out the result has job.", 45, 1, 50).Build(),
                Schema.Obj().Enum("op", "", null, "current", "list", "reload", "next", "prev", "goto").Enum("outcome", "", null, "done", "loading", "refused")
                    .OutObj("current", "Current checkpoint.", Observer.CheckpointSchema())
                    .Arr("checkpoints", "op list.", Schema.OutOf(Observer.CheckpointSchema().Bool("reached", "At or before progression.").Bool("discovered", "Known this session.")), 0, 200)
                    .Int("total", "Rows before paging.", 0, 10000).OutObj("job", "Load still running (wait_until {job}).", DevToolsProvider.JobSchema())
                    .Str("backup", "Save backup made before this load.").Arr("warnings", "", Schema.Str(), 0, 20)
                    .Prop("observe", null, Observer.ConciseRef()).BuildOutput()).Destructive(), HandleCheckpoint);

            PlayRegistry.Add(PlayShared.Def("level", "Levels and loads", PlayShared.Dev, Owner,
                "Level state and loads. status / list are read-only; wait_ready waits for the ready predicate. load_checkpoint (mode respawn | quiet | chapter), load_chapter (0..65), reload (respawn | chapter | newgame), step (next | prev savepoint). chapter, load_chapter, reload chapter / newgame write the Steam Cloud save and unlock chapters; respawn writes the spawn; quiet locks the save and writes nothing. No confirm or backup is needed unless the user set EditorState play.save_guard = 1.",
                Schema.Obj().Enum("op", "status, list, wait_ready, load_checkpoint, load_chapter, reload or step.", "status",
                        "status", "list", "wait_ready", "load_checkpoint", "load_chapter", "reload", "step")
                    .Int("subscene", "Savepoint subscene index.", 0, 999).Int("savepoint", "Savepoint index.", 0, 999).Int("chapter", "Chapter index.", 0, 65)
                    .Enum("mode", "op load_checkpoint.", "respawn", "respawn", "quiet", "chapter")
                    .Enum("kind", "op reload.", "respawn", "respawn", "chapter", "newgame").Enum("dir", "op step.", "next", "next", "prev")
                    .Str("filter", "op list: name filter.").Int("limit", "op list: rows.", 100, 1, 300).Int("offset", "op list: first row.", 0, 0, 1000)
                    .Bool("wait", "Wait until ready after a load.", true).Num("timeout_s", "Wait limit.", 45, 1, 50).Int("settle_frames", "Frames after ready.", 20, 0, 120)
                    .Bool("confirm", "Only with play.save_guard on: required for loads that write the save and unlock chapters.", false).Build(),
                Schema.Obj().Enum("op", "", null, "status", "list", "wait_ready", "load_checkpoint", "load_chapter", "reload", "step")
                    .Enum("outcome", "", null, "done", "loading", "refused", "timeout")
                    .OutObj("status", "Load state.", StatusSchema()).OutObj("current", "Current checkpoint.", Observer.CheckpointSchema())
                    .Arr("subscenes", "op list: savepoint subscenes.", Schema.OutOf(Schema.Obj().Int("subscene", "", 0, 999).Str("name", "").Str("puzzle", "").Str("area", "")
                        .Int("savepoints", "-1 = not discovered yet.", -1, 999).Str("state", "Culler state.")), 0, 300)
                    .Arr("chapters", "op list: chapter menu entries.", Schema.OutOf(Schema.Obj().Int("chapter", "", 0, 99).Str("name", "").Int("subscene", "", -1, 999)
                        .Int("savepoint", "", -1, 999).Bool("unlocked", "")), 0, 80)
                    .Int("total", "", 0, 10000).OutObj("job", "Load still running.", DevToolsProvider.JobSchema()).Str("backup", "Save backup path.")
                    .Arr("warnings", "", Schema.Str(), 0, 20).BuildOutput()).Destructive(), HandleLevel);

            PlayRegistry.Add(PlayShared.Def("stream", "Streaming", PlayShared.Dev, Owner,
                "Subscene streaming. Streaming follows the camera: a camera away from the boy unloads his area after 4 s of game time. pin keeps a subscene loaded (AlwaysLoaded), load force-loads one (SavepointTask.WaitForSubscene recipe), freeze stops all streaming changes. subscene -1 = the body's area. status lists loaded, pinned and the body's subscenes; filter narrows by name (filter all = every one).",
                Schema.Obj().Enum("op", "status, pin, unpin, load, freeze or unfreeze.", "status", "status", "pin", "unpin", "load", "freeze", "unfreeze")
                    .Int("subscene", "Savepoint subscene index, -1 = the body's area.", -1, -1, 999).Str("name", "Subscene name instead of the index.")
                    .Bool("activate", "op load: activate after loading.", true).Str("filter", "op status: name filter.").Build(),
                Schema.Obj().Enum("outcome", "", null, "done", "refused", "timeout").Bool("frozen", "CullingActive is off.")
                    .Arr("cullers", "Culler states.", Schema.OutOf(Schema.Obj().Int("subscene", "", -1, 999).Str("name", "").Str("state", "ELoadedState.")
                        .Bool("pinned", "").Bool("visible", "")), 0, 300)
                    .Arr("pinned", "Pinned subscenes.", Schema.IntAny(), 0, 300).Arr("warnings", "", Schema.Str(), 0, 20).BuildOutput()), HandleStream);

            PlayRegistry.OnTick("levels", Tick);
        }

        public static SchemaObj StatusSchema()
        {
            return Schema.Obj().Str("state", "GameManager state (Playing, Fading, Loading ...).").Bool("loading", "").Bool("playing", "").Bool("paused", "")
                .Num("fader", "Fader progress.", 0, 1).Bool("input_enabled", "").Bool("title", "Title overlay showing.").Bool("preawake_pending", "")
                .Bool("cullers_busy", "").Bool("ready", "").Str("reason", "Why not ready.").Int("load_count", "", 0, 1000000)
                .OutObj("progression", "Progression savepoint.", Schema.Obj().Int("subscene", "", -1, 999).Int("savepoint", "", -1, 999))
                .OutObj("unlocked", "Chapter menu unlocked up to.", Schema.Obj().Int("subscene", "", -1, 999).Int("savepoint", "", -1, 999));
        }

        // ---------------------------------------------------------------- tool handlers (worker thread)
        static ToolResult HandleCheckpoint(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "current");
            if (op == "current") return ToolResult.Ok(ctx.Main(() => { var o = Head(op, "done"); o["current"] = Current(); return o; }));
            if (op == "list")
            {
                string filter = PlayShared.Str(a, "filter", null);
                int limit = PlayShared.Int(a, "limit", 40), offset = PlayShared.Int(a, "offset", 0);
                return ToolResult.Ok(ctx.Main(() => CheckpointList(op, filter, limit, offset)));
            }
            var req = new LoadRequest { wait = PlayShared.Bool(a, "wait", true), timeoutMs = TimeoutMs(a), label = "checkpoint " + op, profile = ctx.Profile };
            if (op == "reload") req.mode = LoadPolicy.ReloadRespawn;
            else
            {
                if (!ctx.Main(() => Cheats())) throw new McpException(McpErr.Refused, "checkpoint " + op + " is a cheat and play.cheats is off",
                    "ask the user to enable it (play.cheats=1 in <game>/_mod/editor.cfg, edited while the game is closed); checkpoint reload and normal play need no cheat");
                req.mode = LoadPolicy.Respawn;
                req.cheat = true;
                if (op == "goto")
                {
                    if (!PlayShared.Has(a, "subscene")) throw new McpException(McpErr.BadArgs, "op goto needs subscene (and savepoint)", "checkpoint {op:\"list\"} lists them");
                    req.subscene = PlayShared.Int(a, "subscene", 0);
                    req.savepoint = PlayShared.Int(a, "savepoint", 0);
                }
            }
            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return busy ?? ToolResult.Fail(McpErr.Busy, "the control lane is busy", "wait for the running action, or call restore");
            var pre = new List<string>();
            if (op == "next" || op == "prev")
            {
                var t = FindStep(ctx, op == "next", pre);
                req.subscene = t.sub;
                req.savepoint = t.sp;
            }
            var r = StartLoad(ctx, job, req);
            var res = Head(op, r.Str("outcome", "done"));
            Copy(r, res, "current", "job", "backup");
            Warn(res, pre, r.Arr("warnings"));
            if (r.Str("outcome", "") == "done")
            {
                object obs = ctx.Main(() => Concise("checkpoint"));
                if (obs != null) res["observe"] = obs;
            }
            return ToolResult.Ok(res);
        }

        static ToolResult HandleLevel(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "status");
            if (op == "status") return ToolResult.Ok(ctx.Main(() => { var o = Head(op, "done"); o["status"] = Status(); o["current"] = Current(); return o; }));
            if (op == "list")
            {
                string filter = PlayShared.Str(a, "filter", null);
                int limit = PlayShared.Int(a, "limit", 100), offset = PlayShared.Int(a, "offset", 0);
                return ToolResult.Ok(ctx.Main(() => LevelList(op, filter, limit, offset)));
            }
            int settle = PlayShared.Int(a, "settle_frames", 20);
            if (op == "wait_ready")
            {
                bool ok = WaitReady(ctx, TimeoutMs(a), settle);
                return ToolResult.Ok(ctx.Main(() =>
                {
                    var o = Head(op, ok ? "done" : "timeout");
                    o["status"] = Status();
                    o["current"] = Current();
                    return o;
                }));
            }
            var req = new LoadRequest { wait = PlayShared.Bool(a, "wait", true), timeoutMs = TimeoutMs(a), settleFrames = settle, confirm = PlayShared.Bool(a, "confirm", false), label = "level " + op, profile = ctx.Profile };
            switch (op)
            {
                case "load_checkpoint":
                    if (!PlayShared.Has(a, "subscene")) throw new McpException(McpErr.BadArgs, "op load_checkpoint needs subscene (and savepoint)", "level {op:\"list\"} lists them");
                    req.mode = PlayShared.Str(a, "mode", "respawn");
                    req.subscene = PlayShared.Int(a, "subscene", 0);
                    req.savepoint = PlayShared.Int(a, "savepoint", 0);
                    break;
                case "load_chapter":
                    if (!PlayShared.Has(a, "chapter")) throw new McpException(McpErr.BadArgs, "op load_chapter needs chapter (0..65)", "level {op:\"list\"} lists the chapters");
                    int ch = PlayShared.Int(a, "chapter", 0);
                    var t = ctx.Main(() => ChapterTarget(ch));
                    req.mode = LoadPolicy.Chapter;
                    req.subscene = t.sub;
                    req.savepoint = t.sp;
                    break;
                case "reload":
                    string kind = PlayShared.Str(a, "kind", "respawn");
                    req.mode = kind == "chapter" ? LoadPolicy.ReloadChapter : kind == "newgame" ? LoadPolicy.NewGame : LoadPolicy.ReloadRespawn;
                    break;
                case "step":
                    req.mode = LoadPolicy.Respawn;
                    break;
                default:
                    throw new McpException(McpErr.BadArgs, "op: unknown " + op);
            }
            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return busy ?? ToolResult.Fail(McpErr.Busy, "the control lane is busy", "wait for the running action, or call restore");
            var pre = new List<string>();
            if (op == "step")
            {
                var t = FindStep(ctx, PlayShared.Str(a, "dir", "next") != "prev", pre);
                req.subscene = t.sub;
                req.savepoint = t.sp;
            }
            var r = StartLoad(ctx, job, req);
            var res = Head(op, r.Str("outcome", "done"));
            Copy(r, res, "current", "job", "backup", "load_ms");
            Warn(res, pre, r.Arr("warnings"));
            if (r.Str("outcome", "") == "done") res["status"] = ctx.Main(() => Status());
            return ToolResult.Ok(res);
        }

        static ToolResult HandleStream(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "status");
            string filter = PlayShared.Str(a, "filter", null);
            if (op == "status") return ToolResult.Ok(ctx.Main(() => StreamState("done", filter, -1, null)));
            int sub = PlayShared.Int(a, "subscene", -1);
            string name = PlayShared.Str(a, "name", null);
            if (op == "freeze" || op == "unfreeze")
                return ToolResult.Ok(ctx.Main(() => { var w = new List<string>(); SetFreeze(op == "freeze", w); return StreamState("done", null, -1, w); }));
            if (op == "pin" || op == "unpin")
            {
                return ToolResult.Ok(ctx.Main(() =>
                {
                    var w = new List<string>();
                    int s = ResolveStreamTarget(sub, name);
                    bool done = Pin(s, op == "pin", "mcp");
                    if (!done) w.Add(op == "unpin" ? "unpin: subscene " + s + " was not pinned through MCP" : "pin: no culler for subscene " + s);
                    else if (op == "pin") w.Add("pinned: " + Label(s) + " stays loaded and its logic keeps running until unpin or restore");
                    return StreamState(done ? "done" : "refused", null, s, w);
                }));
            }
            if (op == "load")
            {
                bool activate = PlayShared.Bool(a, "activate", true);
                ToolResult busy;
                var job = ctx.BeginJob(McpLanes.Control, out busy);
                if (job == null) return busy ?? ToolResult.Fail(McpErr.Busy, "the control lane is busy", "wait for the running action, or call restore");
                int s = ctx.Main(() => ResolveStreamTarget(sub, name));
                var w = new List<string>();
                string outcome = PlayLevelsStream.Load(ctx, s, activate, w);
                return ToolResult.Ok(ctx.Main(() => StreamState(outcome, null, s, w)));
            }
            throw new McpException(McpErr.BadArgs, "op: unknown " + op);
        }

        // ---------------------------------------------------------------- public API
        // main thread: the ready predicate (report 03 section 1.4); reason when false
        public static bool Ready(out string reason) { return ReadyRule.Evaluate(ReadInputs(), out reason); }

        // any thread: a load is in progress (mirrors GameManager.loadStarted / loadFinished)
        public static bool Loading { get { return loadingFlag || requested; } }
        public static int LoadCount { get { return loadCount; } }

        // main thread: StatusSchema fields / Observer.CheckpointSchema fields
        public static Json.Obj Status()
        {
            var o = new Json.Obj();
            var gm = GM();
            var ri = ReadInputs();
            string reason;
            bool ready = ReadyRule.Evaluate(ri, out reason);
            o["state"] = gm != null ? gm.debugState.ToString() : "none";
            o["loading"] = ri.loading || ri.pendingLoad;
            o["playing"] = gm != null && GameManager.IsPlaying();
            o["paused"] = gm != null && GameManager.IsPaused;
            double fader = 0;
            try { if (gm != null && gm.fader != null) fader = gm.fader.Progress; } catch { }
            o["fader"] = PlayShared.R(Math.Max(0, Math.Min(1, fader)));
            o["input_enabled"] = ri.inputEnabled;
            o["title"] = ri.title;
            o["preawake_pending"] = ri.preawake;
            o["cullers_busy"] = ri.cullersBusy;
            o["ready"] = ready;
            o["reason"] = reason;
            o["load_count"] = (double)(gm != null ? GameManager.LoadCount : 0);
            o["progression"] = Idx(Progression());
            o["unlocked"] = Idx(Unlocked());
            o["spawn"] = Idx(Spawn());
            o["pending"] = ri.pendingLoad;
            if (reason.Length > 0) o["reason_text"] = ReadyRule.Explain(reason);
            bool press = false;
            try { press = TitleManager.IsPressStartShowing(); } catch { }
            if (press) o["press_start"] = true;
            try { if (Discovery.Busy) o["discovery"] = Discovery.status ?? "busy"; } catch { }
            return o;
        }

        public static Json.Obj Current()
        {
            var cur = Spawn();
            var o = CheckpointRow(cur.sub, cur.sp, ChapterIndexes());
            string guide = Guide(cur.sub, cur.sp);
            if (guide != null) o["guide"] = guide;
            return o;
        }

        // worker thread; the caller took the control lane (job). Starts the load (throws McpException refused,
        // confirm_required, busy, not_ready), waits for ready when req.wait within req.timeoutMs. Returns {outcome done |
        // loading, current, backup, job}; on loading the job is Detached, keeps the lane until ready and then Finishes
        // with the same fields (outcome done).
        public static Json.Obj StartLoad(CallContext ctx, McpJob job, LoadRequest req)
        {
            if (req == null) throw new McpException(McpErr.BadArgs, "no load request");
            var warnings = new List<string>();
            var target = new SpIndex(req.subscene, req.savepoint);
            var probe = ctx.Main(() => Probe(target.sub));
            ThrowProbe(probe);
            bool guard = Saves.Guard;
            var d = LoadPolicy.Decide(req.mode, target, probe.unlocked, probe.progression, req.confirm, req.cheat, guard, probe.subsceneCount, probe.savepointCount);
            if (d.Refused) throw new McpException(d.error, d.message, d.hint);
            if (d.discover)
            {
                Discover(ctx, target.sub, warnings);
                probe = ctx.Main(() => Probe(target.sub));
                ThrowProbe(probe);
                d = LoadPolicy.Decide(req.mode, target, probe.unlocked, probe.progression, req.confirm, req.cheat, guard, probe.subsceneCount, probe.savepointCount);
                if (d.Refused) throw new McpException(d.error, d.message, d.hint);
            }
            string backup = d.backup ? Saves.EnsureBackup(ctx) : Saves.BackupPath;
            var started = DateTime.UtcNow;
            bool play = req.profile == McpProfile.Play;
            bool cheat = req.cheat;
            int issued = ctx.Main(() => Issue(req.mode, target, d, warnings, play, cheat));
            var res = PlayShared.Env();
            if (backup != null) res["backup"] = backup;
            int waitMs = Math.Min(Math.Max(0, req.timeoutMs), ctx.RemainingMs - 2500);
            bool done = req.wait && waitMs > 0 && ctx.WaitUntil(() => LoadDone(issued), waitMs);
            if (ctx.Cancelled) throw new McpException(McpErr.Cancelled, "cancelled: " + ctx.Info.CancelReason, "the load itself keeps running in the game; wait_until {ready:true}");
            if (done && req.settleFrames > 0) ctx.WaitFrames(req.settleFrames, Math.Max(500, ctx.RemainingMs - 1500));
            if (done)
            {
                res["outcome"] = "done";
                res["load_ms"] = Math.Round((DateTime.UtcNow - started).TotalMilliseconds);
                res["current"] = ctx.Main(() => Current());
                AddWarnings(res, warnings);
                return res;
            }
            res["outcome"] = "loading";
            warnings.Add("loading: the load is still running; wait_until {job:\"" + (job != null ? job.Id : "") + "\"} or {ready:true}, then observe");
            if (job != null)
            {
                job.Detach();
                var w = new LoadWatch { job = job, issued = issued, settle = req.settleFrames, backup = backup, startedUtc = started, warnings = new List<string>(warnings) };
                lock (watches) watches.Add(w);
                res["job"] = job.Describe();
            }
            AddWarnings(res, warnings);
            return res;
        }

        // main thread: a load is running or requested right now (not the tick-refreshed Loading)
        internal static bool LoadingNow()
        {
            var gm = GM();
            if (gm == null) return false;
            try { return GameManager.IsLoading() || PendingLoad(gm) || requested; } catch { return false; }
        }

        // main thread: the savepoint subscene index that contains the body (-1 unknown)
        public static int BodySubscene()
        {
            if (!G.SavepointsReady) return -1;
            var ch = ScriptGlobals.mainCharacter;
            if (ch == null) return -1;
            var p = ch.transform.position;
            var cands = new List<CullerCandidate>();
            int n = SavepointManager.SubsceneCount;
            for (int i = 0; i < n; i++)
            {
                SubsceneCuller c = null;
                try { c = SavepointManager.GetCuller(i); } catch { }
                if (c == null) continue;
                var b = c.CullingBounds;
                cands.Add(new CullerCandidate { index = i, contains = b.size != Vector3.zero && b.Contains(p), active = c.IsActive, volume = (double)b.size.x * b.size.y * b.size.z });
            }
            return BodyPick.Pick(cands, Spawn().sub);
        }

        // main thread: pin / unpin a subscene for owner (pins are reference counted per owner; Restorer key stream_pin:<sub>)
        public static bool Pin(int subscene, bool on, string owner)
        {
            SubsceneCuller c = null;
            try { if (G.SavepointsReady) c = SavepointManager.GetCuller(subscene); } catch { }
            if (on)
            {
                if (c == null) return false;
                if (pins.Add(subscene, owner, c.AlwaysLoaded))
                {
                    c.AlwaysLoaded = true;
                    int s = subscene;
                    Reg("stream_pin:" + s, "stream pin of " + Label(s), () => UnpinAll(s));
                }
                return true;
            }
            if (!pins.IsPinned(subscene)) return false;
            if (pins.Remove(subscene, owner))
            {
                bool orig = pins.Original(subscene);
                pins.Drop(subscene);
                if (c != null) c.AlwaysLoaded = orig;
                Forget("stream_pin:" + subscene);
            }
            return true;
        }

        public static bool IsPinned(int subscene) { return pins.IsPinned(subscene); }

        // any thread, main-thread mirrors of GameManager.loadStarted / loadFinished for other modules
        public static event Action LoadStarted;
        public static event Action LoadFinished;
        static void RaiseStarted() { var h = LoadStarted; if (h != null) h(); }
        static void RaiseFinished() { var h = LoadFinished; if (h != null) h(); }

        // ---------------------------------------------------------------- state, hooks, tick
        static readonly PinBook pins = new PinBook();
        static readonly List<LoadWatch> watches = new List<LoadWatch>();
        static volatile bool loadingFlag, requested, hooked;
        static volatile int loadCount;
        static int requestedFrame;
        static bool freezeWas = true;
        static FieldInfo loadRoutineField, savegameItemField;
        static bool loadRoutineMissing;

        sealed class LoadWatch
        {
            public McpJob job;
            public int issued, settle, settled = -1;
            public string backup;
            public DateTime startedUtc;
            public List<string> warnings;
        }

        sealed class LoadProbe
        {
            public string notReady, notReadyHint, busy;
            public int subsceneCount, savepointCount = -1;
            public SpIndex unlocked = SpIndex.Unknown, progression = SpIndex.Unknown;
        }

        static void Tick()
        {
            if (!hooked) Hook();
            var gm = GM();
            bool loading = false;
            if (gm != null)
            {
                try { loading = GameManager.IsLoading() || PendingLoad(gm); } catch { }
                try { loadCount = GameManager.LoadCount; } catch { }
            }
            loadingFlag = loading;
            if (requested && (loading || Time.frameCount - requestedFrame > 600)) requested = false;
            PlayLevelsStream.Tick(loading);
            if (watches.Count > 0) TickWatches();
        }

        static void Hook()
        {
            hooked = true;
            try
            {
                GameManager.loadStarted += OnLoadStarted;
                GameManager.loadFinished += OnLoadFinished;
            }
            catch (Exception e) { DevLog.Error("[play] levels load hooks", e); }
        }

        static void OnLoadStarted(GameManager.Load load, Savepoint.CharacterType character)
        {
            loadingFlag = true;
            try { RaiseStarted(); } catch (Exception e) { DevLog.Error("[play] LoadStarted handler", e); }
        }

        static void OnLoadFinished(GameManager.Load load, Savepoint.CharacterType character)
        {
            try { RaiseFinished(); } catch (Exception e) { DevLog.Error("[play] LoadFinished handler", e); }
        }

        static void TickWatches()
        {
            LoadWatch[] arr;
            lock (watches) arr = watches.ToArray();
            foreach (var w in arr)
            {
                bool end = false;
                try
                {
                    if (w.job.Cancelled) { w.job.Fail(McpErr.Cancelled, "cancelled: " + w.job.CancelReason + " (the load itself keeps running)"); end = true; }
                    else if (w.settled < 0)
                    {
                        if (LoadDone(w.issued)) w.settled = 0;
                        else if ((DateTime.UtcNow - w.startedUtc).TotalSeconds > 180) { w.job.Fail(McpErr.Timeout, "the load did not become ready within 180 s"); end = true; }
                    }
                    if (!end && w.settled >= 0 && ++w.settled > w.settle)
                    {
                        var r = PlayShared.Env();
                        r["outcome"] = "done";
                        r["load_ms"] = Math.Round((DateTime.UtcNow - w.startedUtc).TotalMilliseconds);
                        r["current"] = Current();
                        if (w.backup != null) r["backup"] = w.backup;
                        AddWarnings(r, w.warnings);
                        w.job.Finish(r);
                        end = true;
                    }
                }
                catch (Exception e) { end = true; try { w.job.Fail(McpErr.GameError, e.GetType().Name + ": " + e.Message); } catch { } }
                if (end) lock (watches) watches.Remove(w);
            }
        }

        internal static GameManager GM()
        {
            try { return PersistentBehaviour<GameManager>.instance; } catch { return null; }
        }

        static bool PendingLoad(GameManager gm)
        {
            if (gm == null || loadRoutineMissing) return false;
            if (loadRoutineField == null)
            {
                loadRoutineField = typeof(GameManager).GetField(WorldAMembers.LoadRoutine, BindingFlags.NonPublic | BindingFlags.Instance);
                if (loadRoutineField == null) { loadRoutineMissing = true; DevLog.Write("[play] GameManager.loadRoutine not found: pending loads are not seen"); return false; }
            }
            return loadRoutineField.GetValue(gm) != null;
        }

        internal static ReadyInputs ReadInputs()
        {
            var i = new ReadyInputs();
            var gm = GM();
            if (gm == null) { i.hasGame = false; return i; }
            try { i.started = GameManager.GameIsStarted(); } catch { i.started = false; }
            try { i.pendingLoad = PendingLoad(gm) || requested; } catch { }
            try { i.loading = GameManager.IsLoading(); } catch { }
            try { var f = gm.fader; i.faderDone = f == null || f.IsDone; } catch { }
            try { i.title = TitleManager.IsShowing(); } catch { }
            try { i.cullersBusy = !SubsceneCullingManager.instance.AllCullersFinished(); } catch { }
            try { i.preawake = PreAwakeManager.PendingPreawake(); } catch { }
            try { i.inputEnabled = GameInput.IsEnabled(); } catch { }
            try { i.characterActive = ScriptGlobals.mainCharacterActive; } catch { i.characterActive = false; }
            return i;
        }

        // main thread: the issued load is past its load count and the ready predicate holds
        static bool LoadDone(int issuedLoadCount)
        {
            if (GameManager.LoadCount <= issuedLoadCount) return false;
            string reason;
            return Ready(out reason);
        }

        static bool WaitReady(CallContext ctx, int timeoutMs, int settle)
        {
            int ms = Math.Min(timeoutMs, ctx.RemainingMs - 2000);
            if (ms <= 0) return false;
            bool ok = ctx.WaitUntil(() => { string r; return Ready(out r); }, ms);
            if (ok && settle > 0) ctx.WaitFrames(settle, Math.Max(500, ctx.RemainingMs - 1500));
            return ok;
        }

        static LoadProbe Probe(int sub)
        {
            var p = new LoadProbe();
            var gm = GM();
            if (gm == null || !G.SavepointsReady || PersistentBehaviour<Savegame>.instance == null)
            {
                p.notReady = "the game has not started yet (no GameManager / SavepointManager / Savegame)";
                p.notReadyHint = "wait until the game shows the world, then retry";
                return p;
            }
            if (!GameManager.GameIsStarted()) { p.notReady = "the game is still booting"; p.notReadyHint = "wait_until {ready:true}"; return p; }
            if (GameManager.IsLoading() || PendingLoad(gm) || requested) p.busy = "a load is in progress (" + gm.debugState + ")";
            else
            {
                try { if (Discovery.Busy) p.busy = "the InsideDev discovery queue is loading a subscene (" + (Discovery.status ?? "") + ")"; } catch { }
            }
            if (p.busy == null && PlayLevelsStream.Busy) p.busy = "a stream load is running";
            p.subsceneCount = SavepointManager.SubsceneCount;
            p.savepointCount = sub >= 0 && sub < p.subsceneCount ? SavepointManager.GetSubsceneSavepointCount(sub) : -1;
            p.unlocked = Unlocked();
            p.progression = Progression();
            return p;
        }

        static void ThrowProbe(LoadProbe p)
        {
            if (p.notReady != null) throw new McpException(McpErr.NotReady, p.notReady, p.notReadyHint);
            if (p.busy != null) throw new McpException(McpErr.Busy, "busy: " + p.busy, "one load at a time: wait_until {ready:true}, then retry");
        }

        static void Discover(CallContext ctx, int sub, List<string> warnings)
        {
            ctx.MainDo(() => Discovery.Enqueue(sub, 0, Discovery.Action.Discover));
            int ms = Math.Min(25000, ctx.RemainingMs - 6000);
            bool ok = ms > 0 && ctx.WaitUntil(() => SavepointManager.GetSubsceneSavepointCount(sub) >= 0 && !Discovery.Busy, ms);
            if (!ok) throw new McpException(McpErr.Timeout, "discovering subscene " + sub + " did not finish in time", "retry; the discovery keeps running (level {op:\"status\"} shows it)");
            int n = ctx.Main(() => SavepointManager.GetSubsceneSavepointCount(sub));
            warnings.Add("discovered: subscene " + sub + " has " + n + " savepoint(s) (loaded once to read them)");
        }

        // main thread: issue one load; returns GameManager.LoadCount before it
        static int Issue(string mode, SpIndex t, LoadDecision d, List<string> warnings, bool play, bool cheat)
        {
            var gm = GM();
            if (gm == null) throw new McpException(McpErr.NotReady, "no GameManager");
            if (GameManager.IsLoading() || PendingLoad(gm)) throw new McpException(McpErr.Busy, "busy: a load started meanwhile", "wait_until {ready:true}, then retry");
            int before = GameManager.LoadCount;
            bool lockedBefore = Saves.Locked;
            string listHint = play ? "checkpoint {op:\"list\"}" : "level {op:\"list\"}";
            switch (mode)
            {
                case LoadPolicy.Chapter:
                    if (!SavepointManager.IsSavepointValid(t.sub, t.sp)) throw new McpException(McpErr.BadArgs, "savepoint " + t + " is not valid", listHint);
                    GameManager.LoadSavepoint(t.sub, t.sp);
                    break;
                case LoadPolicy.Respawn:
                    if (!SavepointManager.IsSavepointValid(t.sub, t.sp)) throw new McpException(McpErr.BadArgs, "savepoint " + t + " is not valid", listHint);
                    Savegame.SetSavepoint(t.sub, t.sp);
                    GameManager.Respawn();
                    if (cheat && lockedBefore) Saves.MarkCheat("cheat checkpoint jump to " + t);
                    if (d.backwards) warnings.Add("progression: moved back from " + d.progression + " to " + t + (lockedBefore ? " in memory" : " in INSIDE.sav") + (Saves.BackupPath != null ? " (the session backup " + Saves.BackupPath + " has the old one)" : ""));
                    break;
                case LoadPolicy.Quiet:
                    if (!SavepointManager.IsSavepointValid(t.sub, t.sp)) throw new McpException(McpErr.BadArgs, "savepoint " + t + " is not valid", listHint);
                    var item = SavegameItem();
                    if (item == null) throw new McpException(McpErr.GameError, "Savegame.savegameDataMapItem not found by reflection", "use mode respawn");
                    if (!lockedBefore && !Saves.SetLock(true)) throw new McpException(McpErr.GameError, "the save lock (UserStoragePlatform.suspended) is not available", "use mode respawn");
                    QuietSpawn(item, t);
                    GameManager.Respawn();
                    Saves.MarkCheat("quiet load to " + t);
                    warnings.Add(Saves.Guard ? "save_lock: on (quiet mode); INSIDE.sav keeps its state from before the lock: quitting the game drops this jump and everything after it; save {op:\"unlock\", confirm:true} writes the in-memory state instead (restore keeps the lock)"
                        : "save_lock: on (quiet mode); INSIDE.sav is written at save {op:\"unlock\"}, restore {include_save_lock:true} or quit");
                    break;
                case LoadPolicy.ReloadRespawn:
                    GameManager.Respawn();
                    break;
                case LoadPolicy.ReloadChapter:
                    GameManager.ReloadScene();
                    break;
                case LoadPolicy.NewGame:
                    GameManager.StartNewGame();
                    break;
                default:
                    throw new McpException(McpErr.BadArgs, "mode: unknown " + mode);
            }
            if (!loadRoutineMissing && !GameManager.IsLoading() && !PendingLoad(gm)) throw new McpException(McpErr.GameError, "the game dropped the load request", "retry once the game is playing");
            requested = true;
            requestedFrame = Time.frameCount;
            loadingFlag = true;
            if (d.writes)
            {
                if (lockedBefore) warnings.Add(play ? "save_lock: on, so INSIDE.sav is not written now (the user releases the lock)" : "save_lock: on, so INSIDE.sav is written only after save {op:\"unlock\"}");
                else warnings.Add("save: this load writes INSIDE.sav (Steam Cloud)");
            }
            if (d.unlocks && d.writes) warnings.Add("unlocked: the chapter menu is now unlocked up to " + t + " (permanent)");
            if (mode == LoadPolicy.Chapter || mode == LoadPolicy.ReloadChapter) warnings.Add("orbs: a chapter load clears the recent orb bits (Rencently)");
            return before;
        }

        // Savegame.SetSpawnpoint (FP/Savegame.cs:427-445) on the in-memory item, without the save and without
        // User.GameProgression.SetSavepoint (no chapter unlock): spawn, PersistentData.enabled = dynamic (false clears
        // the dynamic puzzle state), progression = target for a normal savepoint, -1:-1 for a dynamic one before it
        static void QuietSpawn(Savegame.SavegameDataMapItem item, SpIndex t)
        {
            bool dynamic = false;
            try { dynamic = SavepointManager.IsSavepointDynamic(t.sub, t.sp); } catch { }
            item.spawnSubscene = t.sub;
            item.spawnSavepoint = t.sp;
            if (item.persistentData != null) item.persistentData.enabled = dynamic;
            if (!dynamic)
            {
                item.progressionSubscene = t.sub;
                item.progressionSavepoint = t.sp;
            }
            else if (SpIndex.Compare(t, new SpIndex(item.progressionSubscene, item.progressionSavepoint)) < 0)
            {
                item.progressionSubscene = -1;
                item.progressionSavepoint = -1;
            }
        }

        static Savegame.SavegameDataMapItem SavegameItem()
        {
            var sg = PersistentBehaviour<Savegame>.instance;
            if (sg == null) return null;
            if (savegameItemField == null) savegameItemField = typeof(Savegame).GetField(WorldAMembers.SavegameItem, BindingFlags.NonPublic | BindingFlags.Instance);
            return savegameItemField != null ? savegameItemField.GetValue(sg) as Savegame.SavegameDataMapItem : null;
        }

        // worker: SavepointManager.SavepointTask(cur).FindNext / FindPrevious (loads and unloads subscenes while it searches)
        static SpIndex FindStep(CallContext ctx, bool next, List<string> warnings)
        {
            var probe = ctx.Main(() => Probe(-1));
            ThrowProbe(probe);
            var task = ctx.Main(() =>
            {
                var cur = Savegame.GetCurrentSavepoint();
                var tk = new SavepointManager.SavepointTask(cur);
                if (next) tk.FindNext(); else tk.FindPrevious();
                return tk;
            });
            int ms = Math.Min(30000, ctx.RemainingMs - 8000);
            bool ok = ms > 0 && ctx.WaitUntil(() => task.IsCompleted(), ms);
            if (!ok) throw new McpException(McpErr.Timeout, "the savepoint search did not finish in time", "retry; it keeps running and the next try is faster (subscenes are discovered)");
            var r = ctx.Main(() => task.Failed() ? SpIndex.Unknown : new SpIndex(task.savepointIndex.subscene, task.savepointIndex.savepoint));
            if (r.IsUnknown) throw new McpException(McpErr.NotFound, "there is no " + (next ? "next" : "previous") + " savepoint", "checkpoint {op:\"list\"}");
            warnings.Add("target: " + (next ? "next" : "previous") + " savepoint is " + r);
            return r;
        }

        static SpIndex ChapterTarget(int chapter)
        {
            var sd = SingletonBehaviour<SavepointData>.instance;
            if (sd == null || sd.scenes == null) throw new McpException(McpErr.NotReady, "SavepointData is not loaded yet");
            if (chapter < 0 || chapter >= sd.scenes.Count) throw new McpException(McpErr.BadArgs, "chapter " + chapter + " is out of range (0.." + (sd.scenes.Count - 1) + ")");
            var idx = sd.scenes[chapter].GetSavepointIndex();
            return new SpIndex(idx.subscene, idx.savepoint);
        }

        internal static SpIndex Spawn()
        {
            try { if (PersistentBehaviour<Savegame>.instance != null) { int s, p; Savegame.GetCurrentSavepoint(out s, out p); return new SpIndex(s, p); } } catch { }
            return SpIndex.Unknown;
        }

        internal static SpIndex Progression()
        {
            try { if (PersistentBehaviour<Savegame>.instance != null) { int s, p; Savegame.GetCurrentProgression(out s, out p); return new SpIndex(s, p); } } catch { }
            return SpIndex.Unknown;
        }

        internal static SpIndex Unlocked()
        {
            try { if (User.IsInitialized) { var u = User.GameProgression.GetSavepoint(); return new SpIndex(u.subscene, u.savepoint); } } catch { }
            return SpIndex.Unknown;
        }

        static List<SpIndex> ChapterIndexes()
        {
            var l = new List<SpIndex>();
            try
            {
                var sd = SingletonBehaviour<SavepointData>.instance;
                if (sd != null && sd.scenes != null) foreach (var s in sd.scenes) l.Add(new SpIndex(s.subsceneIndex, s.savepointIndex));
            }
            catch { }
            return l;
        }

        static string ChapterName(int i)
        {
            try
            {
                var sd = SingletonBehaviour<SavepointData>.instance;
                if (sd != null && sd.scenes != null && i >= 0 && i < sd.scenes.Count) return sd.scenes[i].name ?? "";
            }
            catch { }
            return "";
        }

        static Json.Obj CheckpointRow(int sub, int sp, List<SpIndex> chapters)
        {
            var o = new Json.Obj();
            o["subscene"] = (double)sub;
            o["savepoint"] = (double)sp;
            string scene = "", puzzle = "", area = "";
            try
            {
                if (G.SavepointsReady && sub >= 0 && sub < SavepointManager.SubsceneCount)
                {
                    var info = SavepointManager.GetSubsceneInfos()[sub];
                    scene = info.name ?? ""; puzzle = info.puzzleName ?? ""; area = info.areaName ?? "";
                }
            }
            catch { }
            o["name"] = puzzle.Length > 0 && puzzle != "_NoPuzzleFolder_" ? puzzle : scene;
            o["area"] = area;
            int ch = sub >= 0 ? ChapterMap.Closest(chapters, new SpIndex(sub, sp)) : -1;
            o["chapter"] = (double)ch;
            o["chapter_name"] = ChapterName(ch);
            if (scene.Length > 0) o["scene"] = scene;
            return o;
        }

        static string Guide(int sub, int sp)
        {
            if (sub < 0) return null;
            try { return GuideBook.TopicFor(sub, sp); } catch { return null; }
        }

        static Json.Obj CheckpointList(string op, string filter, int limit, int offset)
        {
            if (!G.SavepointsReady) throw new McpException(McpErr.NotReady, "the savepoint manager is not ready (the game is still booting)");
            var o = Head(op, "done");
            var chapters = ChapterIndexes();
            var prog = Progression();
            var rows = new List<object>();
            int total = 0;
            var infos = SavepointManager.GetSubsceneInfos();
            for (int s = 0; s < infos.Count; s++)
            {
                int count = infos[s].savepointCount;
                bool discovered = count >= 0;
                if (count < 0)
                {
                    count = 1;
                    try { var ce = SpawnCache.Get(s); if (ce != null && ce.count > 0) count = ce.count; } catch { }
                }
                for (int p = 0; p < count; p++)
                {
                    var row = CheckpointRow(s, p, chapters);
                    if (!TextFilter.Match(filter, row.Str("name", ""), row.Str("area", ""), row.Str("chapter_name", ""), row.Str("scene", ""))) continue;
                    total++;
                    if (total <= offset || rows.Count >= limit) continue;
                    row["reached"] = SpIndex.Compare(new SpIndex(s, p), prog) <= 0;
                    row["discovered"] = discovered;
                    rows.Add(row);
                }
            }
            o["checkpoints"] = rows;
            o["total"] = (double)total;
            o["current"] = Current();
            if (total > offset + rows.Count) AddWarnings(o, new List<string> { "more: " + (total - offset - rows.Count) + " rows after these (offset " + (offset + rows.Count) + ")" });
            return o;
        }

        static Json.Obj LevelList(string op, string filter, int limit, int offset)
        {
            if (!G.SavepointsReady) throw new McpException(McpErr.NotReady, "the savepoint manager is not ready (the game is still booting)");
            var o = Head(op, "done");
            var subs = new List<object>();
            int total = 0;
            var infos = SavepointManager.GetSubsceneInfos();
            for (int s = 0; s < infos.Count; s++)
            {
                var info = infos[s];
                if (!TextFilter.Match(filter, info.name, info.puzzleName, info.areaName)) continue;
                total++;
                if (total <= offset || subs.Count >= limit) continue;
                var r = new Json.Obj();
                r["subscene"] = (double)s;
                r["name"] = info.name ?? "";
                r["puzzle"] = info.puzzleName ?? "";
                r["area"] = info.areaName ?? "";
                r["savepoints"] = (double)info.savepointCount;
                string state = "none";
                try { var c = SavepointManager.GetCuller(s); if (c != null) state = c.LoadedState.ToString(); } catch { }
                r["state"] = state;
                if (pins.IsPinned(s)) r["pinned"] = true;
                subs.Add(r);
            }
            o["subscenes"] = subs;
            o["total"] = (double)total;
            var chapters = ChapterIndexes();
            var unlocked = Unlocked();
            var chl = new List<object>();
            for (int i = 0; i < chapters.Count; i++)
            {
                string name = ChapterName(i);
                if (!TextFilter.Match(filter, name, Label(chapters[i].sub))) continue;
                var r = new Json.Obj();
                r["chapter"] = (double)i;
                r["name"] = name;
                r["subscene"] = (double)chapters[i].sub;
                r["savepoint"] = (double)chapters[i].sp;
                r["unlocked"] = ChapterMap.Unlocked(chapters, i, unlocked);
                chl.Add(r);
            }
            o["chapters"] = chl;
            o["current"] = Current();
            return o;
        }

        // ---------------------------------------------------------------- streaming state
        static int ResolveStreamTarget(int sub, string name)
        {
            if (!G.SavepointsReady) throw new McpException(McpErr.NotReady, "the savepoint manager is not ready (the game is still booting)");
            int n = SavepointManager.SubsceneCount;
            if (!string.IsNullOrEmpty(name))
            {
                var scenes = new List<string>();
                var puzzles = new List<string>();
                foreach (var i in SavepointManager.GetSubsceneInfos()) { scenes.Add(i.name); puzzles.Add(i.puzzleName); }
                string err;
                List<int> cands;
                int r = SubsceneNames.Resolve(scenes, puzzles, name, out err, out cands);
                if (r >= 0) return r;
                var e = new McpException(cands.Count > 1 ? McpErr.Ambiguous : McpErr.NotFound, err, "level {op:\"list\", filter} lists the names");
                if (cands.Count > 1)
                {
                    e.data = new Json.Obj();
                    var l = new List<object>();
                    for (int i = 0; i < cands.Count && i < 5; i++) { var c = new Json.Obj(); c["id"] = (double)cands[i]; c["path"] = scenes[cands[i]]; l.Add(c); }
                    e.data["candidates"] = l;
                }
                throw e;
            }
            if (sub == -1)
            {
                int b = BodySubscene();
                if (b < 0) throw new McpException(McpErr.NotFound, "no savepoint subscene contains the body", "give subscene or name");
                return b;
            }
            if (sub < 0 || sub >= n) throw new McpException(McpErr.BadArgs, "subscene " + sub + " is out of range (0.." + (n - 1) + ")");
            return sub;
        }

        static void SetFreeze(bool on, List<string> w)
        {
            var mgr = SubsceneCullingManager.instance;
            if (on)
            {
                bool was = mgr.CullingActive;
                if (!PlayLevelsStream.Frozen) freezeWas = was;
                PlayLevelsStream.Frozen = true;
                mgr.CullingActive = false;
                Reg("stream_freeze", "streaming frozen (CullingActive off)", Thaw);
                w.Add("frozen: nothing streams in or out until unfreeze (a load turns culling on for itself; it is switched off again after it)" + (was ? "" : "; culling was already off"));
                return;
            }
            if (PlayLevelsStream.Frozen) w.Add("unfrozen: " + Thaw());
            else w.Add("unfreeze: streaming was not frozen through MCP, nothing changed");
            Forget("stream_freeze");
        }

        // main thread: end the freeze; culling goes back to its value before the freeze, except during a load, which
        // switches culling off and on itself (GameManager.ReloadOnlySubscenes)
        static string Thaw()
        {
            PlayLevelsStream.Frozen = false;
            if (LoadingNow()) return "CullingActive left to the running load (it switches culling back on itself)";
            var m = SubsceneCullingManager.instance;
            if (m == null) return "no culling manager";
            m.CullingActive = freezeWas;
            return "CullingActive back " + (freezeWas ? "on" : "off");
        }

        static Json.Obj StreamState(string outcome, string filter, int focus, List<string> warnings)
        {
            if (!G.SavepointsReady) throw new McpException(McpErr.NotReady, "the savepoint manager is not ready (the game is still booting)");
            var o = PlayShared.Env();
            o["outcome"] = outcome;
            var mgr = SubsceneCullingManager.instance;
            o["frozen"] = !mgr.CullingActive;
            int body = BodySubscene();
            bool all = filter == "all";
            var rows = new List<object>();
            int n = SavepointManager.SubsceneCount, loaded = 0;
            for (int s = 0; s < n; s++)
            {
                SubsceneCuller c = null;
                try { c = SavepointManager.GetCuller(s); } catch { }
                if (c == null) continue;
                bool isLoaded = c.LoadedState != SubsceneCuller.ELoadedState.Unloaded;
                if (isLoaded) loaded++;
                string name = c.SceneName ?? "";
                bool show = all || (filter == null ? (isLoaded || pins.IsPinned(s) || s == body || s == focus) : TextFilter.Match(filter, name, Label(s)));
                if (!show || rows.Count >= 300) continue;
                var r = new Json.Obj();
                r["subscene"] = (double)s;
                r["name"] = name;
                r["state"] = c.LoadedState.ToString();
                r["pinned"] = pins.IsPinned(s);
                bool vis = false;
                try { vis = mgr.InsideViewfrustum(c.CullingBounds); } catch { }
                r["visible"] = vis;
                if (c.AlwaysLoaded && !pins.IsPinned(s)) r["always_loaded"] = true;
                if (s == body) r["body"] = true;
                rows.Add(r);
            }
            o["cullers"] = rows;
            var pl = new List<object>();
            foreach (var s in pins.All()) pl.Add((double)s);
            o["pinned"] = pl;
            o["body_subscene"] = (double)body;
            o["loaded"] = (double)loaded;
            AddWarnings(o, warnings);
            return o;
        }

        static string UnpinAll(int s)
        {
            bool orig = pins.Original(s);
            pins.Drop(s);
            SubsceneCuller c = null;
            try { c = SavepointManager.GetCuller(s); } catch { }
            if (c != null) c.AlwaysLoaded = orig;
            return "AlwaysLoaded back to " + (orig ? "on" : "off");
        }

        internal static string Label(int s)
        {
            try { return G.SubsceneLabel(s); } catch { return "#" + s; }
        }

        // ---------------------------------------------------------------- small helpers
        static bool Cheats()
        {
            try { return EditorState.Get("play.cheats", false); } catch { return false; }
        }

        static object Concise(string tool)
        {
            try { return Observer.Concise(tool); } catch { return null; }
        }

        static int TimeoutMs(Json.Obj a) { return (int)Math.Round(PlayShared.Num(a, "timeout_s", 45) * 1000); }

        static Json.Obj Head(string op, string outcome)
        {
            var o = PlayShared.Env();
            o["op"] = op;
            o["outcome"] = outcome;
            return o;
        }

        static Json.Obj Idx(SpIndex i)
        {
            var o = new Json.Obj();
            o["subscene"] = (double)i.sub;
            o["savepoint"] = (double)i.sp;
            return o;
        }

        static void Copy(Json.Obj from, Json.Obj to, params string[] keys)
        {
            foreach (var k in keys) if (from.ContainsKey(k) && from[k] != null) to[k] = from[k];
        }

        static void Warn(Json.Obj o, List<string> pre, List<object> more)
        {
            var l = new List<string>(pre);
            if (more != null) foreach (var x in more) if (x is string) l.Add((string)x);
            AddWarnings(o, l);
        }

        internal static void AddWarnings(Json.Obj o, List<string> w)
        {
            if (w == null || w.Count == 0) return;
            var l = o["warnings"] as List<object>;
            if (l == null) { l = new List<object>(); o["warnings"] = l; }
            foreach (var s in w) if (s != null && !l.Contains(s) && l.Count < 20) l.Add(s);
        }

        internal static void Reg(string key, string what, Func<string> undo)
        {
            try { Restorer.Register(key, what, undo); } catch (Exception e) { DevLog.Write("[play] restore key " + key + " not registered: " + e.Message); }
        }

        internal static void Forget(string key)
        {
            try { Restorer.Forget(key); } catch { }
        }
    }
}
