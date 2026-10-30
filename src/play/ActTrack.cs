using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // ActTrack.cs (owner INPUT): the input_track tool (docs/MCP_PLAY_DESIGN.md section 17): rehearse in step mode while
    // every logic step of input is recorded, trim it, save it, then play the same input back as one realtime take.
    //   Recording: AgentCore.CommandReceived calls Record once per logic step with the command that reached the game
    //   (agent queue, latch, neutral, or the pad in mix / human mode), the controlled body's pos3 / velocity3 before that
    //   step's logic, the game time and flags (gated, dead, Perception.ForcedInput, huddle grabbing). A load or 72000
    //   frames end the recording; the frames so far stay in memory.
    //   Playback: while a track plays AgentCore.UpdateCommand takes the step's input from Next instead of its queue
    //   (TrackCursor: one frame per logic step, gaps hold the previous frame, no 600-step cap). On every recorded step
    //   Next first corrects the body toward the recorded one (TrackCorrect, the Playback.InputRecorder.AdjustPosition
    //   pattern, x / y only): the huddle by moving its cloth bodies (transform, pos, oldPos, visual) and spine bones the
    //   way Huddle.Teleport does but without touching its depth plane, plus Huddle.AddVelocity; the boy by Boy.pos and
    //   Boy.AddVelocity, only in run, jump or swim without a special mode (Boy.Teleport would reset his state). Nothing
    //   is corrected while dead, gated or without a body. The playback is a detached control-lane job that the tick
    //   "input_track" ends: done, cancelled (job cancel, op stop, restore), died, loading, stalled (600 frames without a
    //   step) or desynced (stop_drift).
    //   Clock: play puts the Stepper in realtime at speed and holds the rehearsal's lockstep (Stepper.HoldLockstep:
    //   captureFramerate 60, one logic step per frame, frames paced to real time) so the game replays the rehearsal by
    //   itself; afterwards the game keeps running (then run) or goes back to step mode (then freeze). A scene recorder
    //   (srec record) owns the frame pacing instead (Stepper.cs), so a manual recording can run during play.
    //   Authentic takes: correct is off by default (input only; the drift is still measured and reported); correct:true
    //   is the explicit opt-in to the AdjustPosition-style nudges, and the result then warns correction_on.
    // Files: <_mod>/input_tracks/<name>.json (TrackFile, ActTrackCore.cs). The Restorer key input_track stops both.
    public static class ActTrack
    {
        public const string Owner = "INPUT";
        public const string RestoreKey = "input_track", Folder = "input_tracks";
        public const int StallFrames = 600, MaxSamples = 60, ProgressEvery = 30;
        public static readonly string[] Ops = { "status", "start", "stop", "save", "load", "list", "info", "trim", "play" };
        public static readonly string[] Outcomes = { "done", "recording", "stopped", "saved", "loaded", "trimmed", "playing", "cancelled", "died", "loading", "stalled", "desynced" };

        sealed class Player
        {
            public TrackCursor cursor;
            public bool lockstep = true;
            public TrackHeader header;
            public readonly CorrectParams cp = new CorrectParams();
            public readonly DriftStats stats = new DriftStats();
            public McpJob job;
            public double speed = 1;
            public bool freezeAfter, wantObserve;
            public volatile bool desynced, ended;
            public int lastStep, idleFrames, nextProgress;
            public readonly List<string> warnings = new List<string>();
            public volatile Json.Obj result;
        }

        static readonly object gate = new object();
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static InputTrack current;
        static bool dirty, applyFailed;
        static volatile InputTrack rec;
        static volatile Player player;
        static volatile string lastStop = "";

        public static void Register()
        {
            PlayRegistry.Add(Def(), Handle);
            PlayRegistry.OnTick("input_track", Tick);
        }

        internal static ToolDef Def()
        {
            return PlayShared.Def("input_track", "Input track (rehearse, then take)", PlayShared.Both, Owner,
                "Record and replay input. start records every logic step of input that reaches the game (with body position and velocity) while you act / move_to in step mode; stop, info, trim {from, to}, save {name} (_mod/input_tracks), load, list. play feeds the track back one frame per logic step in realtime at the rehearsal's 60 Hz lockstep (input only; drift measured; correct:true opts in to nudges), as a job: wait_until {job}; cancel with op stop or restore. A manual srec recording may run during play (it owns frame pacing).",
                Schema.Obj().Enum("op", "status, start, stop, save, load, list, info, trim or play.", "status", Ops)
                    .StrMax("name", "Track name (letters, digits, _ - .); info and play default to the track in memory.", TrackFile.MaxName)
                    .Int("from", "trim: first step kept.", 0, 0, 1000000)
                    .Int("to", "trim: last step kept, -1 = end.", -1, -1, 1000000)
                    .Bool("overwrite", "save: replace a track of that name.", false)
                    .Num("speed", "play: realtime speed (1 for a take).", 1, 0.05, 4)
                    .Bool("lockstep", "play: hold the rehearsal's 60 Hz lockstep (default). false = free realtime: the game keeps its own 60 Hz logic steps at any frame rate, so game time follows the wall clock (sound takes).", true)
                    .Bool("correct", "play: nudge the body toward the recorded path (off: input only, for final takes; drift is measured either way).", false)
                    .Num("gain", "play: share of the drift removed per step.", 0.05, 0, 1)
                    .Num("max_step", "play: largest correction per step (m).", 0.02, 0, 0.5)
                    .Num("stop_drift", "play: stop beyond this drift (m), 0 = never.", 0, 0, 100)
                    .Num("max_start", "play: refuse when the body starts farther from frame 0 (m), 0 = no check.", 1.5, 0, 1000)
                    .Enum("then", "play: afterwards keep running or freeze.", "run", "run", "freeze")
                    .Bool("wait", "play: wait up to timeout_s for the end.", true)
                    .Num("timeout_s", "play / stop: wait limit.", 45, 0, 50)
                    .Enum("observe", "play: observation in the result.", "concise", "concise", "none").Build(),
                Schema.Obj().Enum("op", "", null, Ops).Enum("outcome", "", null, Outcomes).Str("stop_reason", "Why play or recording ended.")
                    .Bool("recording", "").Bool("playing", "")
                    .OutObj("track", "Track in memory (info with name: the saved one); see docs for all fields.", TrackSchema())
                    .Arr("tracks", "op list.", Schema.OutOf(Schema.Obj().Str("name", "").Int("steps", "", 0, 1000000).Str("body", "").Int("subscene", "", -1, 999)), 0, 500)
                    .OutObj("playback", "Progress and drift (m).", PlaybackSchema())
                    .OutObj("job", "Playback job.", DevToolsProvider.JobSchema())
                    .Str("file", "").Str("next", "")
                    .Arr("warnings", "", Schema.Str(), 0, 20)
                    .Prop("observe", null, Observer.ConciseRef()).BuildOutput());
        }

        static SchemaObj TrackSchema()
        {
            return Schema.Obj().Str("name", "").Str("body", "").Int("subscene", "", -1, 999).Int("steps", "", 0, 1000000)
                .Int("forced_steps", "Steps where the game forced the input.", 0, 1000000)
                .Arr("forced_spans", "[from, to].", Schema.Arr(Schema.IntAny(), 2, 2), 0, 8)
                .Arr("samples", "[k, x, y, vx, vy, flags]; flags 1 gated, 2 dead, 4 forced, 8 grab, 16 no body.", Schema.Arr(Schema.NumAny(), 6, 6), 0, MaxSamples)
                .Bool("saved", "Matches its file.");
        }

        static SchemaObj PlaybackSchema()
        {
            return Schema.Obj().Int("step", "", 0, 1000000).Int("of", "", 0, 1000000).Int("corrected", "", 0, 1000000)
                .Num("max_drift_m", "", 0, 1e5).Int("max_drift_step", "", -1, 1000000).Num("end_drift_m", "", 0, 1e5)
                .Arr("drift_per_s", "Largest drift per second.", Schema.NumAny(), 0, DriftStats.PerSecondCap);
        }

        // ---------------------------------------------------------------- hooks from AgentCore (main thread)
        internal static bool Recording { get { return rec != null; } }
        internal static bool Playing { get { var p = player; return p != null && !p.ended; } }

        // AgentCore.CommandReceived: one fed logic step (step = LogicManager.stepCount of the command)
        internal static void Record(InputFrame fed, long step, bool gated)
        {
            var r = rec;
            if (r == null) return;
            if (LoadingNow()) { EndRecording("a load started"); return; }
            var f = new TrackFrame { sx = fed.x, sy = fed.y, jump = fed.jump, action = fed.action };
            if (gated) f.flags |= TrackFrame.Gated;
            var ch = ScriptGlobals.mainCharacter;
            if (ch != null && ch.isCharacterActive)
            {
                Vector3 p = ch.pos3, v = ch.velocity3;
                f.px = p.x; f.py = p.y; f.pz = p.z;
                f.vx = v.x; f.vy = v.y; f.vz = v.z;
                if (ch.isDead) f.flags |= TrackFrame.Dead;
                if (ForcedNow(ch)) f.flags |= TrackFrame.Forced;
                var hud = ch as Huddle;
                if (hud != null && Grabbing(hud)) f.flags |= TrackFrame.Grabbing;
            }
            else f.flags |= TrackFrame.NoBody;
            bool full;
            lock (gate) full = !r.Append(step, Time.fixedTime, f);
            if (full) EndRecording("the track is full (" + InputTrack.MaxFrames + " steps)");
        }

        // AgentCore.UpdateCommand while a track plays: this step's input, after the correction of the body
        internal static bool Next(bool gated, out InputFrame f)
        {
            f = AgentQueue.Neutral(1);
            var p = player;
            TrackFrame tf;
            bool exact;
            if (p == null || p.ended || !p.cursor.Next(out tf, out exact)) return false;
            var ch = ScriptGlobals.mainCharacter;
            bool forced = ch != null && ch.isCharacterActive && ForcedNow(ch);
            lock (p.stats) p.stats.Fed(forced, gated);
            if (exact) Correct(p, ch, tf, p.cursor.Step - 1, gated);
            f = tf.Input;
            return true;
        }

        static void Correct(Player p, Character ch, TrackFrame tf, int j, bool gated)
        {
            if (ch == null || !ch.isCharacterActive || ch.isDead || gated) { lock (p.stats) p.stats.Skipped(); return; }
            Vector3 pos = ch.pos3, vel = ch.velocity3;
            var c = TrackCorrect.Step(p.cp, pos.x, pos.y, vel.x, vel.y, tf.px, tf.py, tf.vx, tf.vy);
            bool applied = c.apply && !c.stop && Apply(ch, c);
            lock (p.stats) p.stats.Measured(j, c, applied);
            if (c.stop) p.desynced = true;
        }

        static bool Apply(Character ch, CorrectStep c)
        {
            try
            {
                var hud = ch as Huddle;
                if (hud != null) return ShiftHuddle(hud, c);
                var boy = ch as Boy;
                if (boy != null) return ShiftBoy(boy, c);
            }
            catch (Exception e)
            {
                if (!applyFailed) { applyFailed = true; DevLog.Error("[play] input_track correction", e); }
            }
            return false;
        }

        static bool ShiftHuddle(Huddle hud, CorrectStep c)
        {
            var cloth = hud.cloth;
            var spine = hud.spine;
            if (cloth == null || spine == null || cloth.bones == null || spine.bones == null) return false;
            if (c.dx != 0 || c.dy != 0)
            {
                var d = new Vector3((float)c.dx, (float)c.dy, 0f);
                foreach (var b in cloth.bones)
                {
                    if (b == null || b.body == null) continue;
                    var t = b.body.transform;
                    t.position = t.position + d;
                    b.pos += d;
                    b.oldPos += d;
                    if (b.visualTransform != null) b.visualTransform.position = b.visualTransform.position + d;
                }
                var bones = spine.bones;
                for (int i = 0; i != bones.Length; i++) bones[i].SetPos(bones[i].pos + d);
            }
            if (c.dvx != 0 || c.dvy != 0) hud.AddVelocity(new Vector3((float)c.dvx, (float)c.dvy, 0f));
            return true;
        }

        static bool ShiftBoy(Boy boy, CorrectStep c)
        {
            if (boy.specialBoyMode != Boy.SpecialBoyMode.None || boy.isMindControlling) return false;
            var st = boy.state;
            if (!(st is BoyRunState || st is BoyJumpState || st is BoySwimState)) return false;
            if (c.dx != 0 || c.dy != 0) boy.pos = boy.pos + new vector2f((float)c.dx, (float)c.dy);
            if (c.dvx != 0 || c.dvy != 0) boy.AddVelocity(new vector2f((float)c.dvx, (float)c.dvy));
            return true;
        }

        static bool ForcedNow(Character ch)
        {
            try { return Perception.ForcedInput(ch) != null; }
            catch (Exception) { return false; }
        }

        static bool Grabbing(Huddle hud)
        {
            try { var g = hud.grabState; return g != null && g.IsGrabbing(); }
            catch (Exception) { return false; }
        }

        static bool LoadingNow()
        {
            try { return GameManager.IsLoading(); } catch { return false; }
        }

        static bool DeadNow()
        {
            try { var c = ScriptGlobals.mainCharacter; return c != null && c.isDead; } catch { return false; }
        }

        static string KindOf(Character ch)
        {
            if (ch is Huddle) return "huddle";
            if (ch is Boy) return "boy";
            return ch != null ? ch.GetType().Name : "";
        }

        // main thread, every frame: end the playback when it is over
        static void Tick()
        {
            var p = player;
            if (p == null || p.ended) return;
            string end = null, detail = "";
            int step = p.cursor.Step;
            if (p.job.Cancelled) { end = "cancelled"; detail = p.job.CancelReason; }
            else if (p.cursor.Done) { end = "done"; detail = step + " steps"; }
            else if (p.desynced) { end = "desynced"; detail = "the body drifted more than stop_drift " + F(p.cp.stopDrift) + " m from the path"; }
            else if (LoadingNow()) { end = "loading"; detail = "a load started"; }
            else if (DeadNow()) { end = "died"; detail = "the body died"; }
            else if (step != p.lastStep) { p.lastStep = step; p.idleFrames = 0; }
            else if (CamPath.Pausing) p.idleFrames = 0;
            else if (++p.idleFrames >= StallFrames) { end = "stalled"; detail = "no logic step for " + StallFrames + " frames (paused, menu or window focus)"; }
            if (end != null) { Finish(p, end, detail); return; }
            if (step < p.nextProgress) return;
            p.nextProgress = step + ProgressEvery;
            try { p.job.Progress(step, p.cursor.Length, "input_track: step " + step + "/" + p.cursor.Length); } catch (Exception) { }
        }

        static void Finish(Player p, string outcome, string detail)
        {
            if (p.ended) return;
            p.ended = true;
            if (player == p) player = null;
            try { Stepper.HoldLockstep(false); } catch (Exception) { }
            var w = p.warnings;
            int nudged;
            lock (p.stats) nudged = p.stats.corrected;
            if (p.cp.on) w.Add("correction_on: the body was nudged toward the recorded path on " + nudged + " steps (correct:true); a final take plays with correct:false (input only)");
            if (p.freezeAfter && outcome != "loading")
            {
                try { string s = Stepper.SetMode(StepMode.Step); if (s != null) w.Add(s); }
                catch (Exception e) { w.Add("freeze_failed: " + e.Message); }
            }
            else if (!p.freezeAfter) w.Add("clock: the game keeps running in realtime (then run); time {op:\"step_mode\"} freezes it again");
            var r = Result("play", outcome);
            r["stop_reason"] = detail ?? "";
            r["playback"] = Progress(p);
            r["track"] = Brief(p.header, p.cursor.Length);
            if (p.wantObserve)
            {
                try { r["observe"] = Observer.Concise("input_track"); }
                catch (Exception) { w.Add("observe_unavailable"); }
            }
            r["warnings"] = PlayShared.L(Cap(w));
            p.result = r;
            Note("playback " + outcome + ": " + detail);
            try { p.job.Finish(r); } catch (Exception e) { DevLog.Error("[play] input_track finish", e); }
        }

        static void EndRecording(string why)
        {
            if (rec == null) return;
            rec = null;
            lastStop = why ?? "";
            Note("recording stopped: " + why);
        }

        static void Note(string detail)
        {
            try { ChangeLog.Note("note", "input_track " + detail); } catch (Exception) { }
        }

        static void RegisterRestore()
        {
            try { Restorer.Register(RestoreKey, "input track recording and playback", RestoreAll); } catch (Exception) { }
        }

        // main thread (Restorer): stop a playback and a recording; the track stays in memory
        static string RestoreAll()
        {
            var parts = new List<string>();
            var p = player;
            if (p != null) { Finish(p, "cancelled", "restore"); parts.Add("playback cancelled"); }
            if (rec != null) { EndRecording("restore"); parts.Add("recording stopped (the track stays in memory)"); }
            return parts.Count == 0 ? "nothing running" : string.Join(", ", parts.ToArray());
        }

        // any thread: the input block of observe full (AgentCore.Describe), null when idle
        internal static Json.Obj Brief()
        {
            var o = new Json.Obj();
            var r = rec;
            if (r != null) { lock (gate) o["recording"] = (double)r.Count; return o; }
            var p = player;
            if (p == null || p.ended) return null;
            o["playing"] = p.header.name;
            o["step"] = (double)p.cursor.Step;
            o["of"] = (double)p.cursor.Length;
            return o;
        }

        // ---------------------------------------------------------------- the tool (worker thread)
        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "status");
            switch (op)
            {
                case "status": return Status();
                case "start": return Start(a, ctx);
                case "stop": return Stop(a, ctx);
                case "save": return Save(a);
                case "load": return Load(a);
                case "list": return List();
                case "info": return Info(a);
                case "trim": return Trim(a);
                case "play": return Play(a, ctx);
            }
            return ToolResult.Fail(McpErr.BadArgs, "op: unknown " + op, "one of " + string.Join(", ", Ops));
        }

        static Json.Obj Result(string op, string outcome)
        {
            var r = PlayShared.Env();
            r["op"] = op;
            r["outcome"] = outcome;
            r["recording"] = rec != null;
            r["playing"] = Playing;
            return r;
        }

        static ToolResult Status()
        {
            var r = Result("status", rec != null ? "recording" : Playing ? "playing" : "done");
            var t = Current();
            if (t != null) r["track"] = Summary(t);
            var p = player;
            if (p != null && !p.ended)
            {
                r["playback"] = Progress(p);
                r["job"] = p.job.Describe();
            }
            if (lastStop.Length > 0 && rec == null) r["stop_reason"] = "recording: " + lastStop;
            r["warnings"] = new List<object>();
            return ToolResult.Ok(r);
        }

        static ToolResult Start(Json.Obj a, CallContext ctx)
        {
            string name = PlayShared.Str(a, "name", null);
            if (name != null) { string bad = TrackFile.NameError(name); if (bad != null) return ToolResult.Fail(McpErr.BadArgs, bad, "e.g. cafe_ceo_take1"); }
            var warnings = new List<string>();
            var info = ctx.Main(() =>
            {
                if (Playing) throw new McpException(McpErr.Busy, "a track is playing", "wait_until {any:[{job:\"<id>\"}]} or input_track {op:\"stop\"}");
                if (rec != null) throw new McpException(McpErr.Refused, "already recording (" + rec.Count + " steps)", "input_track {op:\"stop\"} first");
                string w = ActProbe.EnsureAgent();
                if (w != null) warnings.Add(w);
                string reason;
                if (!ActProbe.Ready(out reason)) warnings.Add("not_ready: " + reason + ": recording starts anyway; steps without input reaching the game are flagged gated");
                var t = new InputTrack();
                Fill(t.header, name);
                lock (gate)
                {
                    if (current != null && dirty && current.Count > 0) warnings.Add("unsaved: the previous track " + current.header.name + " (" + current.Steps + " steps) was replaced");
                    current = t;
                    dirty = true;
                }
                lastStop = "";
                rec = t;
                RegisterRestore();
                return Summary(t);
            });
            var r = Result("start", "recording");
            r["track"] = info;
            r["next"] = "act / move_to / interact / wait_until as usual (every logic step is recorded), then input_track {op:\"stop\"}, {op:\"trim\", from, to}, {op:\"save\", name}";
            r["warnings"] = PlayShared.L(warnings);
            return ToolResult.Ok(r);
        }

        static void Fill(TrackHeader h, string name)
        {
            h.name = name ?? "track_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", IC);
            h.created = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", IC);
            h.body = KindOf(ScriptGlobals.mainCharacter);
            try
            {
                var cur = PlayLevels.Current();
                h.subscene = (int)cur.Num("subscene", -1);
                h.savepoint = (int)cur.Num("savepoint", -1);
                h.checkpoint = cur.Str("name", "") ?? "";
            }
            catch (Exception) { }
            try { h.loadCount = PlayLevels.LoadCount; } catch (Exception) { }
            try { h.clock = Stepper.Mode == StepMode.Step ? "step" : "realtime"; } catch (Exception) { }
            h.source = "recorded";
        }

        static ToolResult Stop(Json.Obj a, CallContext ctx)
        {
            var p = player;
            if (p != null && !p.ended)
            {
                p.job.Cancel("input_track stop");
                WaitEnd(p, ctx, 3000);
                var res = p.result;
                var r = Result("stop", res != null ? res.Str("outcome", "cancelled") : p.ended ? "cancelled" : "playing");
                r["stop_reason"] = res != null ? res.Str("stop_reason", "input_track stop") : "input_track stop";
                r["playback"] = res != null ? res["playback"] : Progress(p);
                r["job"] = p.job.Describe();
                r["warnings"] = new List<object>();
                return ToolResult.Ok(r);
            }
            if (rec != null)
            {
                ctx.MainDo(() => EndRecording("stopped"));
                var r = Result("stop", "stopped");
                r["stop_reason"] = "stopped";
                var t = Current();
                if (t != null) r["track"] = Summary(t);
                r["next"] = "input_track {op:\"info\"}, {op:\"trim\", from, to}, {op:\"save\", name}";
                r["warnings"] = new List<object>();
                return ToolResult.Ok(r);
            }
            var n = Result("stop", "done");
            n["warnings"] = new List<object> { "nothing to stop: no recording or playback is running" };
            return ToolResult.Ok(n);
        }

        static ToolResult Save(Json.Obj a)
        {
            var t = Current();
            if (t == null) return ToolResult.Fail(McpErr.NotFound, "no track in memory", "input_track {op:\"start\"} and play, or {op:\"load\", name}");
            if (rec == t) return ToolResult.Fail(McpErr.Refused, "the track is still being recorded", "input_track {op:\"stop\"} first");
            string name = PlayShared.Str(a, "name", null) ?? t.header.name;
            string bad = TrackFile.NameError(name);
            if (bad != null) return ToolResult.Fail(McpErr.BadArgs, bad, "save {name:\"cafe_ceo_take1\"}");
            string path = PathFor(name);
            if (File.Exists(path) && !PlayShared.Bool(a, "overwrite", false))
                return ToolResult.Fail(McpErr.Refused, "a track named " + name + " exists", "overwrite:true, or another name");
            string text;
            lock (gate)
            {
                t.header.name = name;
                text = TrackFile.Write(t);
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e) { return ToolResult.Fail(McpErr.GameError, "cannot write " + path + ": " + e.Message, "check the _mod folder"); }
            lock (gate) if (current == t) dirty = false;
            var r = Result("save", "saved");
            r["file"] = path;
            r["track"] = Summary(t);
            r["warnings"] = new List<object>();
            return ToolResult.Ok(r);
        }

        static ToolResult Load(Json.Obj a)
        {
            string name = PlayShared.Str(a, "name", null);
            if (name == null) return ToolResult.Fail(McpErr.BadArgs, "name: required for op load", "input_track {op:\"list\"} shows the saved tracks");
            if (rec != null || Playing) return ToolResult.Fail(McpErr.Refused, "a track is being recorded or played", "input_track {op:\"stop\"} first");
            string path;
            var t = ReadFile(name, out path);
            var warnings = new List<string>();
            lock (gate)
            {
                if (current != null && dirty && current.Count > 0 && current != t) warnings.Add("unsaved: the previous track " + current.header.name + " (" + current.Steps + " steps) was replaced");
                current = t;
                dirty = false;
            }
            var r = Result("load", "loaded");
            r["file"] = path;
            r["track"] = Summary(t);
            r["warnings"] = PlayShared.L(warnings);
            return ToolResult.Ok(r);
        }

        static ToolResult List()
        {
            var rows = new List<object>();
            string dir = Dir();
            var warnings = new List<object>();
            try
            {
                if (Directory.Exists(dir))
                {
                    var files = Directory.GetFiles(dir, "*" + TrackFile.Extension);
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (var f in files)
                    {
                        if (rows.Count >= 500) { warnings.Add("list: more than 500 tracks, the rest left out"); break; }
                        Json.Obj h;
                        using (var rd = new StreamReader(f)) h = TrackFile.HeaderOfLine(rd.ReadLine());
                        if (h == null) continue;
                        var o = new Json.Obj();
                        o["name"] = Path.GetFileNameWithoutExtension(f);
                        o["steps"] = h.Num("steps", 0);
                        o["seconds"] = Math.Round(h.Num("seconds", 0), 2);
                        o["body"] = h.Str("body", "");
                        o["subscene"] = h.Num("subscene", -1);
                        o["savepoint"] = h.Num("savepoint", -1);
                        o["checkpoint"] = h.Str("checkpoint", "");
                        o["created"] = h.Str("created", "");
                        o["source"] = h.Str("source", "");
                        o["bytes"] = (double)new FileInfo(f).Length;
                        rows.Add(o);
                    }
                }
            }
            catch (Exception e) { return ToolResult.Fail(McpErr.GameError, "cannot list " + dir + ": " + e.Message); }
            var r = Result("list", "done");
            r["tracks"] = rows;
            r["file"] = dir;
            r["warnings"] = warnings;
            return ToolResult.Ok(r);
        }

        static ToolResult Info(Json.Obj a)
        {
            string name = PlayShared.Str(a, "name", null);
            var r = Result("info", "done");
            if (name != null)
            {
                string path;
                var t = ReadFile(name, out path);
                r["track"] = Summary(t);
                r["file"] = path;
            }
            else
            {
                var t = Current();
                if (t == null) return ToolResult.Fail(McpErr.NotFound, "no track in memory", "input_track {op:\"start\"}, or {op:\"info\", name} for a saved one");
                r["track"] = Summary(t);
            }
            r["warnings"] = new List<object>();
            return ToolResult.Ok(r);
        }

        static ToolResult Trim(Json.Obj a)
        {
            if (rec != null || Playing) return ToolResult.Fail(McpErr.Refused, "a track is being recorded or played", "input_track {op:\"stop\"} first");
            var t = Current();
            if (t == null) return ToolResult.Fail(McpErr.NotFound, "no track in memory", "input_track {op:\"load\", name} first");
            string err;
            InputTrack cut;
            lock (gate) cut = t.Trim(PlayShared.Int(a, "from", 0), PlayShared.Int(a, "to", -1), out err);
            if (cut == null) return ToolResult.Fail(McpErr.BadArgs, err, "input_track {op:\"info\"} lists the steps and samples");
            lock (gate)
            {
                current = cut;
                dirty = true;
            }
            var r = Result("trim", "trimmed");
            r["track"] = Summary(cut);
            r["next"] = "input_track {op:\"save\", name} (the trimmed track replaces the one in memory)";
            r["warnings"] = new List<object>();
            return ToolResult.Ok(r);
        }

        static ToolResult Play(Json.Obj a, CallContext ctx)
        {
            string name = PlayShared.Str(a, "name", null);
            InputTrack t;
            if (name != null) { string path; t = ReadFile(name, out path); }
            else t = Current();
            if (t == null) return ToolResult.Fail(McpErr.NotFound, "no track in memory", "input_track {op:\"load\", name}, or play {name}");
            if (rec != null && rec == t) return ToolResult.Fail(McpErr.Refused, "the track is still being recorded", "input_track {op:\"stop\"} first");
            var p = new Player { speed = PlayShared.Num(a, "speed", 1), freezeAfter = PlayShared.Str(a, "then", "run") == "freeze", wantObserve = PlayShared.Str(a, "observe", "concise") != "none" };
            lock (gate)
            {
                p.header = t.header.Copy();
                p.cursor = new TrackCursor(t.frames.ToArray());
            }
            p.cp.on = PlayShared.Bool(a, "correct", false);
            p.lockstep = PlayShared.Bool(a, "lockstep", true);
            p.cp.gain = PlayShared.Num(a, "gain", 0.05);
            p.cp.maxStep = PlayShared.Num(a, "max_step", 0.02);
            p.cp.stopDrift = PlayShared.Num(a, "stop_drift", 0);
            double maxStart = PlayShared.Num(a, "max_start", 1.5);
            bool wait = PlayShared.Bool(a, "wait", true);
            double timeoutS = PlayShared.Num(a, "timeout_s", 45);
            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return busy;
            p.job = job;
            ctx.MainDo(() => Arm(p, maxStart));
            job.Detach();
            if (wait && timeoutS > 0)
            {
                WaitEnd(p, ctx, (int)Math.Min(timeoutS * 1000, Math.Max(0, ctx.RemainingMs - 2000)));
                var res = p.result;
                if (p.ended && res != null) return ToolResult.Ok(res);
            }
            var r = Result("play", "playing");
            r["stop_reason"] = "";
            r["playback"] = Progress(p);
            r["track"] = Brief(p.header, p.cursor.Length);
            r["job"] = job.Describe();
            r["next"] = "wait_until {any:[{job:\"" + job.Id + "\"}], timeout_s:50} (repeat until satisfied); input_track {op:\"stop\"} cancels";
            r["warnings"] = PlayShared.L(Cap(new List<string>(p.warnings)));
            return ToolResult.Ok(r);
        }

        // main thread: the last checks, then the first logic step of the next frame plays frame 0
        static void Arm(Player p, double maxStart)
        {
            if (Playing) throw new McpException(McpErr.Busy, "a track is already playing", "wait_until {any:[{job:\"<id>\"}]} or input_track {op:\"stop\"}");
            if (rec != null) throw new McpException(McpErr.Refused, "a track is being recorded", "input_track {op:\"stop\"} first");
            string reason;
            if (!ActProbe.Ready(out reason)) throw new McpException(McpErr.NotReady, "the game is not ready to play (" + reason + ")", PlayShared.NextFor(reason, null) ?? "wait_until {ready:true}");
            var ch = ScriptGlobals.mainCharacter;
            if (ch == null) throw new McpException(McpErr.NotReady, "no controlled body", "wait_until {ready:true}");
            Vector3 pos = ch.pos3;
            double off = TrackPlan.StartOffset(p.cursor.First, pos.x, pos.y);
            string why = TrackPlan.StartRefusal(p.header, p.cursor.Length, KindOf(ch), off, maxStart);
            if (why != null) throw new McpException(McpErr.Refused, "play refused: " + why, TrackPlan.LoadHint(p.header));
            string w = AgentCore.Install(InputMode.Agent);
            if (w != null) p.warnings.Add(w);
            AgentCore.Release();
            string m = Stepper.SetMode(StepMode.Realtime);
            if (m != null) p.warnings.Add(m);
            string s = Stepper.SetSpeed(p.speed);
            if (s != null) p.warnings.Add(s);
            if (p.lockstep) Stepper.HoldLockstep(true);
            else p.warnings.Add("lockstep: off, free realtime (game time follows the wall clock; frames vary)");
            if (off > 0.25) p.warnings.Add("start_offset: the body starts " + F(off) + " m from the first frame; " + (p.cp.on ? "the correction pulls it in over the first steps" : "without correct the take starts that far off the rehearsal"));
            if (Math.Abs(p.speed - 1) > 1e-6) p.warnings.Add("speed: " + F(p.speed) + " is a preview; a take with sound runs at speed 1");
            p.lastStep = 0;
            player = p;
            RegisterRestore();
            Note("playback started: " + p.header.name + " (" + p.cursor.Length + " steps)");
        }

        static void WaitEnd(Player p, CallContext ctx, int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(Math.Max(0, ms));
            while ((!p.ended || p.result == null) && DateTime.UtcNow < until)
            {
                if (ctx.Cancelled) { p.job.Cancel("the call was cancelled"); until = DateTime.UtcNow.AddMilliseconds(2000); }
                p.job.Await(100);
                try { ctx.Progress(p.cursor.Step, p.cursor.Length, "input_track: step " + p.cursor.Step + "/" + p.cursor.Length); } catch (Exception) { }
            }
        }

        // ---------------------------------------------------------------- helpers
        static InputTrack Current() { lock (gate) return current; }

        static Json.Obj Summary(InputTrack t)
        {
            lock (gate)
            {
                var o = t.Summary(MaxSamples);
                o["saved"] = t == current ? !dirty : true;
                return o;
            }
        }

        static Json.Obj Brief(TrackHeader h, int steps)
        {
            var o = new Json.Obj();
            o["name"] = h.name;
            o["body"] = h.body;
            o["subscene"] = (double)h.subscene;
            o["savepoint"] = (double)h.savepoint;
            o["steps"] = (double)steps;
            o["seconds"] = Math.Round(steps * InputTrack.StepSeconds, 2);
            return o;
        }

        static Json.Obj Progress(Player p)
        {
            Json.Obj o;
            lock (p.stats) o = p.stats.ToJson();
            o["name"] = p.header.name;
            o["step"] = (double)p.cursor.Step;
            o["of"] = (double)p.cursor.Length;
            o["speed"] = p.speed;
            o["correct"] = p.cp.on;
            return o;
        }

        static List<string> Cap(List<string> w)
        {
            if (w.Count > 20) w.RemoveRange(20, w.Count - 20);
            return w;
        }

        static string F(double v) { return v.ToString("0.##", IC); }

        // any thread: <_mod>/input_tracks (the folder InsideDev.Play.dll was loaded from)
        internal static string Dir()
        {
            string mod = null;
            try
            {
                string loc = typeof(ActTrack).Assembly.Location;
                mod = string.IsNullOrEmpty(loc) ? null : Path.GetDirectoryName(loc);
            }
            catch (Exception) { }
            return Path.Combine(mod ?? ".", Folder);
        }

        static string PathFor(string name) { return Path.Combine(Dir(), name + TrackFile.Extension); }

        static InputTrack ReadFile(string name, out string path)
        {
            string bad = TrackFile.NameError(name);
            if (bad != null) throw new McpException(McpErr.BadArgs, bad, "input_track {op:\"list\"} shows the saved tracks");
            path = PathFor(name);
            if (!File.Exists(path)) throw new McpException(McpErr.NotFound, "no saved track " + name, "input_track {op:\"list\"} shows the saved tracks");
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception e) { throw new McpException(McpErr.GameError, "cannot read " + path + ": " + e.Message); }
            string err;
            var t = TrackFile.Read(text, out err);
            if (t == null) throw new McpException(McpErr.BadArgs, path + ": " + err, "the file is not an input track");
            t.header.name = name;
            return t;
        }
    }
}
