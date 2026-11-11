using System;
using System.Collections.Generic;
using System.IO;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // PfmXFilm.cs (owner TIME): the MCP tool "film", one typed door to the filming commands with structured results:
    // pano (a 360 degree picture), grec_start / grec_stop (video of the game's own frames), campath_load / campath_arm /
    // campath_start / campath_stop and mount (the live director camera), status (everything at once, with the desktop
    // recorder of time {op:"record"}). Each op runs the console command of the same name on the main thread, so both
    // doors behave the same; the tool adds the waiting (pano and grec_stop answer when the file is complete, grec_start
    // answers when frames are being written or the start failed) and the parsed state.
    // On the play profile files stay in <_mod>/recordings (bare names only) and the director camera ops are refused.
    // Also here: the part's config keys for the cfg command and its doctor check.
    public static class Film
    {
        public const string Owner = "TIME", Tool = "film";
        static readonly string[] Ops = { "status", "pano", "grec_start", "grec_stop", "campath_load", "campath_arm", "campath_start", "campath_stop", "mount" };

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def(Tool, "Filming", PlayShared.Both, Owner,
                "Pictures and video of the game. pano: one 360 degree equirectangular PNG at the boy's head or at eye (the game is held for the six faces). grec_start / grec_stop: video of the game's own frames (windows over the game never get in) at a constant frame rate: one frame per every-th rendered frame while the game runs at a capture rate or held by the stepper, frames placed by game time in real-time play (video time = game time). campath_load / campath_arm / campath_start / campath_stop / mount: play a replay plan's camera or ride a transform on the live game camera (dev profile). status: all of them plus the real-time recorder of time {op:\"record\"}. Files without a folder go to <game>/_mod/recordings; every result names the full path. pano and grec_stop wait for the finished file unless wait is false.",
                Schema.Obj().Enum("op", "What to do.", "status", Ops)
                    .Str("file", "op pano / grec_start: file name (in _mod/recordings) or a full path; default <kind>_<date>_<time>.")
                    .Bool("wait", "op pano / grec_stop: answer when the file is complete (within the call's time).", true)
                    .Int("size", "op pano: width in pixels (height is half).", 4096, 256, 16384)
                    .Vec("eye", "op pano: world point of the eye (default: the boy's head).", 3, -1e5, 1e5)
                    .Bool("vignette", "op pano: keep the game's vignette.", true).Bool("lens", "op pano: keep barrel distortion and chromatic aberration.", false)
                    .Num("near", "op pano / mount: near clip plane in metres.", 0.001, 10)
                    .Int("warm", "op pano: frames to settle per face.", 1, 0, 30).Int("samples", "op pano: accumulated frames per face.", 1, 1, 16)
                    .Int("delay", "op pano: frames to wait before the capture.", 2, 0, 120)
                    .Bool("hold", "op pano: hold the game for the six faces.", true)
                    .Num("overscan", "op pano: render each face this many degrees wider and use the centre (keeps screen-edge effects off the seams).", 0, 0, 45)
                    .Bool("compress", "op pano: deflate the PNG in the game (slow).", false)
                    .Str("faces_dir", "op pano: also write the six faces into this folder (dev profile).")
                    .Int("every", "op grec_start: record every n-th rendered frame.", 2, 1, 600)
                    .Int("cq", "op grec_start: constant quality, lower is better and larger.", 16, 0, 51)
                    .Str("enc", "op grec_start: video encoder (auto, hevc_nvenc, hevc_amf, hevc_qsv, libx265, libx264 ...); default editor.cfg film.encoder.")
                    .Num("fps", "op grec_start: frame rate of the file (default: the capture rate / every, the stepper's 60 / every, or in real-time play a nominal 60 / every with frames placed by game time). op campath_load: output rate of the plan (default: from the plan).", 1, 1000)
                    .Bool("times", "op grec_start: also write <file>.times.csv (per video frame: shown frame, game time, real time, repeated, video time, slot game time).", true)
                    .Str("dir", "op campath_load: folder with poses.csv and meta.csv (mcam passes).")
                    .Str("camera", "op campath_load: camera name in the plan.", "director")
                    .Num("offset_s", "op campath_load: shift of the plan against the game clock.", 0, -1e6, 1e6)
                    .Bool("realtime", "op campath_load: pose by game time, no pauses or slow motion.", false)
                    .Str("object", "op mount: transform path the camera rides, or off.")
                    .Vec("local", "op mount: offset in the transform's space (m).", 3, -100, 100).Vec("rot", "op mount: rotation offset (degrees).", 3, -360, 360)
                    .Num("fov", "op mount: field of view (degrees).", 1, 179)
                    .Num("smooth", "op mount: share of the last pose kept per 1/60 s.", 0, 1).Num("into", "op mount: turn toward +z by up to this many degrees.", 0, 89)
                    .Num("level", "op mount: share of the roll taken out.", 0, 1).Bool("hide", "op mount: collapse the transform while mounted.")
                    .Arr("tau", "op mount: time constants h, v [, yaw, pitch] in seconds.", Schema.Num(0, 60), 2, 4)
                    .Num("pitch", "op mount: pitch limit (degrees).", 1, 89).Vec("free", "op mount: free eye pitch up, down (degrees).", 2, 0, 89).Build(),
                Schema.Obj().Str("op", "").Str("text", "The reply of the console command.").Bool("pending", "The file is not complete yet; ask status.")
                    .OutObj("pano", "Panorama: busy, id, phase, file; last = the last finished one {id, ok, file, sidecar, width, height, frames, held, ms, warnings, error}.", Schema.Obj().Open())
                    .OutObj("grec", "Game video: phase, file, frames_issued / written / repeated, fps, encoder, seconds, has_duration, error.", Schema.Obj().Open())
                    .OutObj("rec", "Real-time recorder (time {op:\"record\"}): source game (through grec, the default) or desktop.", Schema.Obj().Open())
                    .OutObj("campath", "Director camera: state, poses, fps, mount.", Schema.Obj().Open())
                    .Str("readback", "Who holds the frame readback (grec, mcam), or free.").BuildOutput()), Handle);
            Recorder.PrimeTick = PlayRegistry.OnTick("film", Recorder.Prime, TickWhen.OnDemand);
            EditorState.Describe("film", FilmEnc.ConfigKey, FilmEnc.Auto, "Video encoder of grec and rec: auto tries hevc_nvenc, hevc_amf, hevc_qsv, libx265, libx264 with a short test encode and takes the first that works; or one of " + FilmEnc.List + ".");
            EditorState.Describe("film", Recorder.MaxKey, Fmt.I(Recorder.MaxDefault), "Longest desktop recording (rec start ... desktop, time record source desktop) in seconds; ffmpeg ends by itself after that. 0 = no cap.");
            Doctor.Register("film", Check);
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "status");
            bool play = ctx.Profile == McpProfile.Play;
            switch (op)
            {
                case "status": return ToolResult.Ok(State(ctx, op, null));
                case "pano": return PanoOp(a, ctx, play);
                case "grec_start": return GrecStart(a, ctx, play);
                case "grec_stop": return GrecStop(a, ctx);
            }
            if (Array.IndexOf(Ops, op) < 0) return ToolResult.Fail(McpErr.BadArgs, "op: unknown " + op);
            if (play) return ToolResult.Fail(McpErr.Refused, "op " + op + " drives the game camera and needs the dev profile");
            var line = new List<string> { "campath" };
            if (op == "campath_load")
            {
                string dir = PlayShared.Str(a, "dir", null);
                if (string.IsNullOrEmpty(dir)) return ToolResult.Fail(McpErr.BadArgs, "op campath_load needs dir");
                line.Add("load"); line.Add(dir); line.Add(PlayShared.Str(a, "camera", "director")); line.Add(Fmt.F(PlayShared.Num(a, "offset_s", 0), "R"));
                line.Add(PlayShared.Bool(a, "realtime", false) ? "rt" : "-");
                if (PlayShared.Has(a, "fps")) line.Add("fps=" + Fmt.F(PlayShared.Num(a, "fps", 30), "R"));
            }
            else if (op == "mount")
            {
                string obj = PlayShared.Str(a, "object", null);
                if (string.IsNullOrEmpty(obj)) return ToolResult.Fail(McpErr.BadArgs, "op mount needs object (a transform path, or off)");
                line.Add("mount"); line.Add(obj);
                AddList(line, a, "local"); AddList(line, a, "rot"); AddList(line, a, "tau"); AddList(line, a, "free");
                foreach (var k in new[] { "fov", "near", "smooth", "into", "level", "pitch" }) if (PlayShared.Has(a, k)) line.Add(k + "=" + Fmt.F(PlayShared.Num(a, k, 0), "R"));
                if (PlayShared.Has(a, "hide")) line.Add("hide=" + (PlayShared.Bool(a, "hide", false) ? "1" : "0"));
            }
            else line.Add(op.Substring("campath_".Length));
            string text = ctx.Main(() => CamPath.Command(line));
            return Reply(ctx, op, text, IsError(text) || text.StartsWith("campath: nothing loaded", StringComparison.Ordinal));
        }

        static void AddList(List<string> line, Json.Obj a, string key)
        {
            var l = PlayShared.List(a, key);
            if (l == null || l.Count == 0) return;
            var parts = new List<string>();
            foreach (var x in l) if (x is double) parts.Add(Fmt.F((double)x, "R"));
            line.Add(key + "=" + string.Join(",", parts.ToArray()));
        }

        static ToolResult PanoOp(Json.Obj a, CallContext ctx, bool play)
        {
            var line = new List<string> { "pano" };
            string file = PlayShared.Str(a, "file", null);
            float number;
            if (!string.IsNullOrEmpty(file) && (file.IndexOf('=') >= 0 || Fmt.TryF(file, out number) || file.ToLowerInvariant() == "take"))
                return ToolResult.Fail(McpErr.BadArgs, "file: a name with '=', a bare number or the word take is not accepted");
            line.Add(string.IsNullOrEmpty(file) ? "take" : file);
            line.Add("size=" + Fmt.I(PlayShared.Int(a, "size", 4096)));
            Vector3 eye;
            if (PlayShared.Vec3(a, "eye", out eye)) line.Add("eye=" + Fmt.F(eye.x, "R") + "," + Fmt.F(eye.y, "R") + "," + Fmt.F(eye.z, "R"));
            if (!PlayShared.Bool(a, "vignette", true)) line.Add("noVignette");
            if (PlayShared.Bool(a, "lens", false)) line.Add("lens");
            if (PlayShared.Has(a, "near")) line.Add("near=" + Fmt.F(PlayShared.Num(a, "near", 0.1), "R"));
            line.Add("warm=" + Fmt.I(PlayShared.Int(a, "warm", 1)));
            line.Add("samples=" + Fmt.I(PlayShared.Int(a, "samples", 1)));
            line.Add("delay=" + Fmt.I(PlayShared.Int(a, "delay", 2)));
            line.Add(PlayShared.Bool(a, "hold", true) ? "hold" : "live");
            if (PlayShared.Num(a, "overscan", 0) > 0) line.Add("overscan=" + Fmt.F(PlayShared.Num(a, "overscan", 0), "R"));
            if (PlayShared.Bool(a, "compress", false)) line.Add("compress");
            string faces = PlayShared.Str(a, "faces_dir", null);
            if (!string.IsNullOrEmpty(faces)) line.Add("faces=" + faces);
            int id = 0;
            string text = ctx.Main(() =>
            {
                string r = Pano.Start(line, DevCore.Instance, play);
                id = Pano.LastId;
                return r;
            });
            if (!text.StartsWith("pano #", StringComparison.Ordinal)) return Reply(ctx, "pano", text, true);
            if (!PlayShared.Bool(a, "wait", true)) { var o = State(ctx, "pano", text); o["pending"] = true; return ToolResult.Ok(o); }
            while (!Pano.Idle && ctx.RemainingMs > 2000 && !ctx.Cancelled) ctx.Sleep(40);
            var st = State(ctx, "pano", text);
            var pano = st["pano"] as Json.Obj;
            var last = pano != null ? pano["last"] as Json.Obj : null;
            if (!Pano.Idle || last == null || PlayShared.Int(last, "id", -1) != id)
            {
                st["pending"] = true;
                return ToolResult.Ok(st);
            }
            if (PlayShared.Bool(last, "ok", false)) return ToolResult.Ok(st);
            return WithState(ToolResult.Fail(McpErr.GameError, "pano #" + Fmt.I(id) + " failed: " + PlayShared.Str(last, "error", "no reason recorded")), st);
        }

        static ToolResult GrecStart(Json.Obj a, CallContext ctx, bool play)
        {
            var line = new List<string> { "grec", "start" };
            string file = PlayShared.Str(a, "file", null);
            if (!string.IsNullOrEmpty(file))
            {
                if (play && file.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return ToolResult.Fail(McpErr.Refused, "on the play profile file is a name without a folder (it goes to _mod/recordings)");
                if (file.IndexOf('=') >= 0 || file.ToLowerInvariant() == "times") return ToolResult.Fail(McpErr.BadArgs, "file: a name with '=' or the word times is not accepted");
                int n;
                if (Fmt.TryI(file, out n)) return ToolResult.Fail(McpErr.BadArgs, "file: a bare number is not accepted as a name");
                line.Add(file);
            }
            line.Add(Fmt.I(PlayShared.Int(a, "every", 2)));
            line.Add(Fmt.I(PlayShared.Int(a, "cq", 16)));
            string enc = PlayShared.Str(a, "enc", null);
            if (!string.IsNullOrEmpty(enc)) line.Add("enc=" + enc.Trim().ToLowerInvariant());
            if (PlayShared.Has(a, "fps")) line.Add("fps=" + Fmt.F(PlayShared.Num(a, "fps", 30), "R"));
            if (!PlayShared.Bool(a, "times", true)) line.Add("times=off");
            string text = ctx.Main(() => GameRec.Command(line));
            if (!text.StartsWith("grec: opening", StringComparison.Ordinal)) return Reply(ctx, "grec_start", text, true);
            // the start goes on over some frames (swap chain, encoder test, ffmpeg): answer when frames are being written
            int budget = Math.Min(20000, ctx.RemainingMs - 2000);
            var until = DateTime.UtcNow.AddMilliseconds(budget);
            while (DateTime.UtcNow < until && !ctx.Cancelled)
            {
                string ph = GameRec.Phase;
                if (ph == "recording" || ph == "failed" || ph == "done" || !GameRec.Running) break;
                ctx.Sleep(40);
            }
            var st = State(ctx, "grec_start", text);
            if (GameRec.Phase == "failed") return WithState(ToolResult.Fail(McpErr.GameError, FirstLine(GameRec.Status())), st);
            if (GameRec.Phase != "recording") st["pending"] = true;
            return ToolResult.Ok(st);
        }

        static ToolResult GrecStop(Json.Obj a, CallContext ctx)
        {
            string text = ctx.Main(() => GameRec.Command(new List<string> { "grec", "stop" }));
            if (!text.StartsWith("grec: stopping", StringComparison.Ordinal)) return Reply(ctx, "grec_stop", text, true);
            if (PlayShared.Bool(a, "wait", true))
                while (GameRec.Running && ctx.RemainingMs > 2000 && !ctx.Cancelled) ctx.Sleep(40);
            var st = State(ctx, "grec_stop", text);
            if (GameRec.Running) { st["pending"] = true; return ToolResult.Ok(st); }
            if (GameRec.Phase == "failed") return WithState(ToolResult.Fail(McpErr.GameError, FirstLine(GameRec.Status())), st);
            return ToolResult.Ok(st);
        }

        static string FirstLine(string s)
        {
            int i = (s ?? "").IndexOf('\n');
            return i < 0 ? s ?? "" : s.Substring(0, i);
        }

        static bool IsError(string text)
        {
            string l = FirstLine(text).ToLowerInvariant();
            return l.StartsWith("usage", StringComparison.Ordinal) || l.StartsWith("busy", StringComparison.Ordinal) || l.StartsWith("refused", StringComparison.Ordinal) ||
                   l.StartsWith("failed", StringComparison.Ordinal) || l.IndexOf(" failed:", StringComparison.Ordinal) >= 0 || l.IndexOf(": busy", StringComparison.Ordinal) >= 0 ||
                   l.IndexOf(": refused", StringComparison.Ordinal) >= 0 || l.IndexOf("unknown option", StringComparison.Ordinal) >= 0;
        }

        static ToolResult Reply(CallContext ctx, string op, string text, bool error)
        {
            var st = State(ctx, op, text);
            if (!error) return ToolResult.Ok(st);
            string l = FirstLine(text), low = l.ToLowerInvariant();
            string code = low.IndexOf("busy", StringComparison.Ordinal) >= 0 || low.IndexOf("already running", StringComparison.Ordinal) >= 0 || low.IndexOf("is capturing", StringComparison.Ordinal) >= 0 ? McpErr.Busy
                : low.StartsWith("usage", StringComparison.Ordinal) || low.IndexOf("unknown option", StringComparison.Ordinal) >= 0 || low.IndexOf("outside", StringComparison.Ordinal) >= 0 ? McpErr.BadArgs
                : low.IndexOf("refused", StringComparison.Ordinal) >= 0 ? McpErr.Refused
                : low.IndexOf("not running", StringComparison.Ordinal) >= 0 || low.IndexOf("nothing loaded", StringComparison.Ordinal) >= 0 ? McpErr.NotReady : McpErr.GameError;
            return WithState(ToolResult.Fail(code, l), st);
        }

        static ToolResult WithState(ToolResult fail, Json.Obj st)
        {
            foreach (var k in st.order) if (k != "ok" && k != "error") fail.structured[k] = st[k];
            return fail;
        }

        // the state of everything the tool drives, read on the main thread
        static Json.Obj State(CallContext ctx, string op, string text)
        {
            return ctx.Main(() =>
            {
                var o = new Json.Obj();
                o["op"] = op;
                if (text != null) o["text"] = text;
                o["pano"] = Pano.StatusObj();
                o["grec"] = GameRec.StatusObj();
                o["rec"] = Recorder.StateObj();
                o["campath"] = CamPath.StatusObj();
                o["readback"] = Owners.Holder(Owners.Readback) ?? "free";
                return o;
            });
        }

        // main thread, read-only: the rows of the doctor command for the filming part
        static List<Doctor.Row> Check()
        {
            var rows = new List<Doctor.Row>();
            string tried, exe = Ffmpeg.Find(out tried);
            if (exe == null)
                rows.Add(new Doctor.Row("film", "ffmpeg", Doctor.Warn, "not found (tried " + tried + "): grec, rec and mcam pipes cannot write video", "cfg ffmpeg.path <full path of ffmpeg.exe>, or put ffmpeg.exe in <game>\\_mod\\ffmpeg"));
            else
            {
                rows.Add(new Doctor.Row("film", "ffmpeg", Doctor.Ok, exe));
                string want = EditorState.Get(FilmEnc.ConfigKey, FilmEnc.Auto).Trim().ToLowerInvariant();
                string enc = FilmEnc.IsKnown(want) ? FilmEnc.Cached(exe, want) : null;
                if (!FilmEnc.IsKnown(want)) rows.Add(new Doctor.Row("film", "encoder", Doctor.Fail, FilmEnc.ConfigKey + " = " + want + " is not a known encoder", "cfg " + FilmEnc.ConfigKey + " auto (or one of " + FilmEnc.List + ")"));
                else if (enc != null) rows.Add(new Doctor.Row("film", "encoder", Doctor.Ok, enc + " (test encode passed in this session" + (want == FilmEnc.Auto ? "" : ", set by " + FilmEnc.ConfigKey) + ")"));
                else rows.Add(new Doctor.Row("film", "encoder", Doctor.Info, FilmEnc.ConfigKey + " = " + want + ", not tested yet: the first grec start or rec start runs a short test encode" + (want == FilmEnc.Auto ? " of " + string.Join(", ", FilmEnc.Order) + " and takes the first that works" : "")));
            }
            string plugin = Path.Combine(Path.Combine(Application.dataPath, "Plugins"), "InsideReadback.dll");
            if (!File.Exists(plugin)) rows.Add(new Doctor.Row("film", "readback plugin", Doctor.Warn, "InsideReadback.dll is not in INSIDE_Data/Plugins: grec (video of the game's own frames) cannot run", "copy bin/InsideReadback.dll of the release into <game>\\INSIDE_Data\\Plugins"));
            else
            {
                string state = GameRec.PluginState();
                rows.Add(new Doctor.Row("film", "readback plugin", state.StartsWith("version", StringComparison.Ordinal) ? Doctor.Ok : Doctor.Fail, state + ", held by " + (Owners.Holder(Owners.Readback) ?? "nobody")));
            }
            string dir = Path.Combine(Extensions.Dir, FilmFiles.Folder), probeDir = Directory.Exists(dir) ? dir : Extensions.Dir;
            try
            {
                string probe = Path.Combine(probeDir, ".film_write_test_" + Fmt.I(System.Diagnostics.Process.GetCurrentProcess().Id));
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                try { if (File.Exists(probe)) File.Delete(probe); } catch { }
                rows.Add(new Doctor.Row("film", "recordings folder", Doctor.Ok, dir.Replace('\\', '/') + (probeDir == dir ? " is writable" : " does not exist yet and can be made")));
            }
            catch (Exception e) { rows.Add(new Doctor.Row("film", "recordings folder", Doctor.Fail, probeDir.Replace('\\', '/') + " is not writable: " + e.Message, "free the folder or its drive; give grec / pano a full path elsewhere")); }
            var now = new List<string>();
            if (GameRec.Running) now.Add(FirstLine(GameRec.Status()));
            if (Recorder.On || Recorder.Pending) { var rs = Recorder.StateObj(); now.Add("rec (" + rs.Str("source", "?") + ") " + (Recorder.On ? "recording" : "starting or closing") + " " + rs.Str("file", "")); }
            if (!Pano.Idle) now.Add(FirstLine(Pano.StatusObj().Str("text", "pano busy")));
            string cp = CamPath.StatusObj().Str("state", "idle");
            if (cp != "idle") now.Add("campath " + cp);
            rows.Add(new Doctor.Row("film", "recording now", Doctor.Info, now.Count == 0 ? "nothing" : string.Join("; ", now.ToArray())));
            return rows;
        }
    }
}
