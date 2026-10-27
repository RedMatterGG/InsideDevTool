using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Replay mode AUDIO (docs/REPLAY_DESIGN.md 6.0-6.2, 4.3): Wwise output capture of the record pass, ForceLevel with
    // GetRTPCValue readback, markers, audio_sync.csv, WAV finalization and "acap ...". Two outputs, EditorState "acap.output"
    // (editor.cfg; `acap output audible|muted`, refused while a capture, window, rtpctest or wake is active):
    // - "audible" (default, shipped): the game sound plays normally (real-time output capture, exact sync). No InsideMuter
    //   heartbeat, no Suspend, no flag files, rtpc_master_volume is never forced to 0. Ensure() and the per-frame guard remove
    //   acap's own rtpc rule while no capture runs (back to the game volume). Preflight checks the basics (spec, no capture or
    //   rtpctest running, AudioSpy installed, Wwise not suspended by acap) and the capture path and target folder. Start issues
    //   StartOutputCapture and opens the window at once at min(audio.level, game volume) (game volume =
    //   GameSystem.Settings.volume x 100, the value the game itself sets; GetRTPCValue if that is unavailable), so the master
    //   volume is never raised above the game's. Closing the window (ForceLevel(0), Stop, emergency) puts back the rule that
    //   was there before it (or, without one, sets the game volume and removes the rule) instead of forcing 0. The window
    //   guard keeps the file appearance (2 s), rule and readback checks, without the heartbeat. `acap suspend on` and
    //   `acap rtpctest` are refused.
    // - "muted" (opt-in: for a capture while the speakers or headphones must stay silent): the headphone gates G2-G6 below. The Windows
    //   session peak meter is pre-mute (L5_MUTEPROBE), so the peak only fails while suspended or on a session that is not muted.
    // Status lines, the Start note and audio_capture.json ("output") name the active output. The rest describes "muted".
    // Protocol for callers (CORE_A RecordBody, PLAY_C srec record, the acap commands):
    // - Ensure() subscribes Tick to RuntimeCallbacks.update and Stop to RuntimeCallbacks.applicationQuit once, forces
    //   rtpc_master_volume to 0 (muted) and stays for the game session. Preflight and Start call it too.
    // - Preflight(spec) = null or the refusal reason: InsideMuter heartbeat (younger than maxHeartbeatAge, mode run, status
    //   running, this INSIDE.exe listed, no unmuted session, no session peak > 0 unless every session is muted (the session
    //   meter is pre-mute: L5_MUTEPROBE, a muted session reaches the device as digital silence), own session muted unless Wwise is
    //   suspended), a capture path (Suspend needs <game>/_mod/acap_suspend_ok.json; without it allowMuterOnly and
    //   <game>/_mod/acap_muteronly_ok.json), the rtpc rule 0 and a GetRTPCValue readback of 0, the target folder.
    // - Start(spec, outDir, report) returns at once: true = StartOutputCapture was issued and `capturing` is true (markers are
    //   accepted from now on). The rest runs in Tick: the file must appear within 2 s, then the window opens (ForceLevel
    //   spec.level) on the first frame after the file appeared (muter-only) or after the heartbeat has shown no active INSIDE
    //   stream and no session peak for 1 s (Suspend; the swap took 1.30 s in L5). Running `acap suspend on` in the spec setup
    //   moves that wait before the take. The WAV is silent until the window opens (`wopen` marker).
    // - Marker(label, sample, gameTime, lag) posts AddOutputCaptureMarker and appends an audio_sync.csv row
    //   (marker,label,sample,game_time,real_time,lag; real_time = realtimeSinceStartupDbl - clockBase). Only in the Update
    //   phase: a marker posted after this frame's RuntimeCallbacks.lateUpdate (SceneHooks.lateFrame == frameCount) is past
    //   RenderAudio, so its row gets lag + 1. Window markers `wopen`/`wclose` have sample -1 and lag -1.
    // - ForceLevel(level) applies AudioRules.Change (changesPerForce times, 2 until the single-Change fix is verified by
    //   `acap rtpctest`) and reads GetRTPCValue. Wwise applies queued RTPC sets at the next RenderAudio, so the readback that
    //   decides is taken by Tick from the second frame on (0.5 s limit, one repeat of the Change): a mismatch at level > 0
    //   closes the window and stops the capture (capturing turns false, lastError set); at level 0 it is logged as an error and
    //   the watchdog keeps forcing. ForceLevel returns false when the rule or GetRTPCValue call fails, or for level > 0 outside
    //   a capture window (refused). ForceLevel(0) during a window closes it.
    // - Tick (every frame): appearance check, Suspend quiet wait, window guard (heartbeat at most every 250 ms: stale, mode or
    //   status changed, unmuted session, unmutedSeenTotal grown since Start, session peak > 0 on an unmuted session or while
    //   suspended, active stream while suspended,
    //   own session not muted while not suspended; rule and readback must keep the window level) -> ForceLevel(0), stop,
    //   lastError, Suspend kept. Outside windows: rule re-forced to 0 every frame, GetRTPCValue every 10th frame (non-zero ->
    //   forced 0 and logged). Finalization after Stop: File.Move to <outDir>/audio.wav every 100 ms for up to 10 s, RIFF chunk
    //   walk (no 0xFFFFFFFF sizes, RIFF size + 8 = file size, chunks end at the file end; else needsFix, run mcam_audio.py fix),
    //   then <outDir>/audio_capture.json (paths, times since clockBase, window, Suspend, heartbeat stats, readbacks, WAV check,
    //   error) for tools/mcam_audio.py.
    // - Stop(why) is idempotent (stop, abort, runner destruction, quit): ForceLevel(0) first, then StopOutputCapture;
    //   capturing is false on return, `finalizing` until the file is moved. Suspend is never woken automatically (G6: only
    //   `acap suspend off`, rtpc 0 verified and 100 ms later); keepSuspended false is reported, not acted on.
    public static class AudioCapture
    {
        public static bool capturing;
        public static double clockBase;                                 // set by CORE to RecordHeader.t0Real before the first Marker; audio_sync.csv real_time = Time.realtimeSinceStartupDbl - clockBase

        public const string RtpcName = "rtpc_master_volume";
        public const string RtpcKey = "rtpc:" + RtpcName;
        public const string CaptureInfoFile = "audio_capture.json";
        public const string Source = "acap";
        public const string OutputKey = "acap.output";
        public const string OutputAudible = "audible", OutputMuted = "muted";
        public static int changesPerForce = 2;
        public static string state = "idle";                            // idle | starting | arming | window | finalizing
        public static bool windowOpen, suspended;
        public static float windowLevel;
        public static string lastError, lastCapture = "", lastTest = "";
        public static int readbackErrors, reforces, nonZeroReadbacks, emergencies, lateMarkers;
        public static bool finalizing { get { return state == "finalizing"; } }

        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly string DefaultHeartbeat = new AudioCapSpec().heartbeat;
        static bool subscribed, opening, inEmergency;
        static int tickFrame, ownPid = -2;
        static double suspendedAt = -1, zeroVerifiedAt = -1;
        static Cap cur;
        static Verify verify;
        static Wake wake;
        static RtpcTest test;
        static AudioHeartbeat lastHb, lastRead;
        static string hbPath = "";
        static string ownRule, priorRule;
        static double nextReadbackForce;
        static double nextHbRead;
        static long quietStartMs = -1, quietStartSeq;
        static readonly Dictionary<string, int> rate = new Dictionary<string, int>();

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        sealed class Cap
        {
            public AudioCapSpec spec;
            public string name = "", dir, abs = "", rel = "", bank = "", dest = "", sync = "", info = "", final = "";
            public bool manual, muted, useSuspend, timer, moved, needsFix;
            public float level, gameLevel = float.NaN;
            public string gameLevelSource = "";
            public double t0, tAppear = -1, tOpen = -1, tClose = -1, tStop = -1, tFinal = -1, nextMove, quietWaitS = -1;
            public int markers, markerFails, late, hbReads, hbBad, everActive;
            public long seenStart = -1, seenEnd = -1;
            public double maxAge, maxPeak;
            public StreamWriter rows;
            public string error, why = "", moveError, suspendResult = "", hbStart = "", hbEnd = "", flagStage = "";
            public AudioWavCheck wav;
            public readonly List<string> readbacks = new List<string>(), notes = new List<string>();
        }

        sealed class Verify { public float target; public double t0; public int frame; public bool retried; public string purpose = ""; public float first = float.NaN; }
        sealed class Wake { public double t0; }
        sealed class RtpcTest { public float level; public float[] values; public int index = -1; public double stepT0, t0; public int stepFrame; public bool native, underSuspend; public readonly List<string> lines = new List<string>(); }

        static string GameDir { get { return Path.GetDirectoryName(Application.dataPath); } }
        static string ModDir { get { return Path.Combine(GameDir, "_mod"); } }
        static string AudioDir { get { return Path.Combine(ModDir, "audio"); } }
        static string SuspendFlag { get { return Path.Combine(ModDir, ReplayConst.SuspendFlagFile); } }
        static string MuterFlag { get { return Path.Combine(ModDir, ReplayConst.MuterOnlyFlagFile); } }
        static double Now { get { return Time.realtimeSinceStartupDbl; } }

        public static string Output { get { return string.Equals(EditorState.Get(OutputKey, OutputAudible).Trim(), OutputMuted, StringComparison.OrdinalIgnoreCase) ? OutputMuted : OutputAudible; } }
        public static bool MutedOutput { get { return Output == OutputMuted; } }
        static bool CloseMuted { get { return cur != null ? cur.muted : MutedOutput; } }
        static string PathName(Cap c) { return c.useSuspend ? "Suspend" : c.muted ? "muter-only" : "audible"; }

        // ------------------------------------------------------------------ guard subscription

        public static void Ensure()
        {
            if (!subscribed)
            {
                RuntimeCallbacks.update += Tick;
                RuntimeCallbacks.applicationQuit += OnQuit;
                subscribed = true;
                DevLog.Write("[acap] guard on, output " + Output + ": " + (MutedOutput ? "rtpc " + RtpcName + " forced 0 outside capture windows, GetRTPCValue every 10th frame, heartbeat watchdog in windows" : "game sound audible, acap's own rtpc " + RtpcName + " rule removed outside captures, no heartbeat, no Suspend"));
            }
            if (windowOpen) return;
            if (MutedOutput) { if (!IsZero(AudioRules.Get(RtpcKey))) ZeroRule("ensure"); }
            else if (cur == null && test == null && wake == null && OwnRuleActive()) Release("ensure: audible output");
        }

        static bool OwnRuleActive() { return ownRule != null && AudioRules.Get(RtpcKey) == ownRule; }

        static string ForeignRule()
        {
            string r = AudioRules.Get(RtpcKey);
            return ownRule != null && r == ownRule ? null : r;
        }

        static float GameLevel(out string source)
        {
            try
            {
                var s = GameSystem.Settings;
                if (s != null && s.volume != null) { source = "GameSystem.Settings.volume"; return Mathf.Clamp(s.volume.value * 100f, 0f, 100f); }
            }
            catch { }
            float v; string err;
            if (ReadRtpc(out v, out err) && !float.IsNaN(v)) { source = "GetRTPCValue"; return Mathf.Clamp(v, 0f, 100f); }
            source = err ?? "unknown";
            return float.NaN;
        }

        static void OnQuit()
        {
            try
            {
                Stop("game quit");
                if (cur != null && state == "finalizing") Finalize(cur, true);
            }
            catch (Exception e) { DevLog.Error("acap quit", e); }
        }

        // ------------------------------------------------------------------ preflight (G2, G3 flags)

        public static string Preflight(AudioCapSpec spec) { return Check(spec, null); }

        static string Check(AudioCapSpec spec, StringBuilder sb)
        {
            Ensure();
            if (spec == null) return "no audio spec";
            if (spec.capture != "record") return "audio.capture is '" + spec.capture + "' ('record' required)";
            if (cur != null) return state == "finalizing" ? "the previous capture is still being finalized (" + cur.name + ")" : "a capture is already running (" + state + ", " + cur.name + ")";
            if (test != null) return "acap rtpctest is running";
            if (!SoundEngineInner.Ready) return "the sound engine wrapper (AudioSpy) is not installed yet";
            bool muted = MutedOutput;
            Line(sb, "output: " + Output + " (" + OutputKey + (muted ? "; InsideMuter heartbeat, flags, rtpc 0 outside the window)" : "; game sound plays normally, no heartbeat, no Suspend, no flags)"));
            if (muted)
            {
                string why = MutedCheck(spec, sb);
                if (why != null) return why;
            }
            else
            {
                if (suspended) return "audible capture refused: Wwise is suspended by acap (Suspend renders slow); 'acap suspend off' first";
                string src;
                float g = GameLevel(out src);
                Line(sb, "game volume " + (float.IsNaN(g) ? "unknown (" + src + ")" : g.ToString("0.###", IC) + " (" + src + ")") + ", rule " + RtpcKey + " = " + (AudioRules.Get(RtpcKey) ?? "(none)") + "; the window opens at once at min(audio.level, game volume)");
            }
            return CheckTarget(sb, muted && UsesSuspend(spec));
        }

        static string MutedCheck(AudioCapSpec spec, StringBuilder sb)
        {
            bool sFlag = File.Exists(SuspendFlag), mFlag = File.Exists(MuterFlag);
            string mode = spec.suspend ?? "auto";
            if (Array.IndexOf(ReplayConst.Suspends, mode) < 0) return "audio.suspend '" + mode + "' is not auto|on|off";
            bool useSuspend = UsesSuspend(spec);
            Line(sb, "flags: " + ReplayConst.SuspendFlagFile + " " + (sFlag ? "present" : "absent") + ", " + ReplayConst.MuterOnlyFlagFile + " " + (mFlag ? "present" : "absent") + "; path " + (useSuspend ? "Suspend" : "muter-only") + " (suspend " + mode + ", allowMuterOnly " + spec.allowMuterOnly + ")");
            if (useSuspend && !sFlag) return "Suspend capture refused: no _mod/" + ReplayConst.SuspendFlagFile + " (L5 S3 rendered at 0.919x real time; only a passing S3b writes the flag, user consent first)";
            if (!useSuspend && !spec.allowMuterOnly) return "no Suspend flag (_mod/" + ReplayConst.SuspendFlagFile + ") and allowMuterOnly is false: no guarded capture path";
            if (!useSuspend && !mFlag) return "muter-only capture refused: no _mod/" + ReplayConst.MuterOnlyFlagFile + " (only a passing L5 S2 writes it, user consent first)";
            if (!useSuspend && suspended) return "muter-only capture refused: Wwise is suspended by acap (the dummy sink renders slow); 'acap suspend off' first";
            var h = Heartbeat(spec.heartbeat, true);
            Line(sb, "heartbeat: " + h.Text());
            string gate = AudioCaptureLogic.Gate(h, spec.maxHeartbeatAge, suspended, false, -1, OwnPid());
            if (gate != null) return "InsideMuter gate: " + gate;
            string rule = AudioRules.Get(RtpcKey);
            Line(sb, "rule " + RtpcKey + " = " + (rule ?? "(none)") + ", native enforcement " + (AudioRules.nativeEnforce ? "on" : "off") + ", changes per force " + changesPerForce);
            if (!IsZero(rule)) return "rtpc rule is '" + (rule ?? "none") + "', not 0";
            float v; string err;
            if (!ReadRtpc(out v, out err)) return "GetRTPCValue failed: " + err;
            Line(sb, "GetRTPCValue " + RtpcName + " = " + v.ToString("0.###", IC));
            if (Math.Abs(v) > 0.01f) return "Wwise reads " + RtpcName + " " + v.ToString("0.###", IC) + " (not 0 yet): retry next frame";
            return null;
        }

        static string CheckTarget(StringBuilder sb, bool useSuspend)
        {
            try { Directory.CreateDirectory(AudioDir); } catch (Exception e) { return "cannot create " + AudioDir + ": " + e.Message; }
            string bank;
            try { bank = SoundBankInfoManager.GetBankPath(); } catch (Exception e) { return "GetBankPath: " + e.Message; }
            string probe = Path.GetFullPath(Path.Combine(AudioDir, "rec_00000000_000000_00.wav"));
            string rel = AudioCaptureLogic.Relative(bank, probe);
            if (rel == probe || Path.IsPathRooted(rel)) return "the capture folder " + AudioDir + " shares no root with the bank path " + bank;
            if (Path.Combine(bank, rel).Length >= 240 || probe.Length >= 240) return "capture path too long (" + Path.Combine(bank, rel).Length + " characters)";
            Line(sb, "target " + AudioDir + " (argument " + rel + " from " + bank + ")");
            if (useSuspend) Line(sb, "suspend: " + (suspended ? "active since " + (Now - suspendedAt).ToString("0.0", IC) + " s, quiet " + (QuietFor() ? "yes" : "no") : "Suspend(true) at Start, window after 1 s of a quiet heartbeat") + (FlagTimer() ? ", timeBeginPeriod(1) (flag)" : ""));
            return null;
        }

        static void Line(StringBuilder sb, string s) { if (sb != null) sb.Append("  ").Append(s).Append('\n'); }

        static bool UsesSuspend(AudioCapSpec spec)
        {
            string mode = spec.suspend ?? "auto";
            return mode == "on" || (mode == "auto" && File.Exists(SuspendFlag));
        }

        static bool FlagTimer()
        {
            try { return File.Exists(SuspendFlag) && AudioCaptureLogic.FlagRequiresTimer(File.ReadAllText(SuspendFlag)); }
            catch { return false; }
        }

        // ------------------------------------------------------------------ start

        public static bool Start(AudioCapSpec spec, string outDir, SpecReport report)
        {
            string err;
            bool ok = StartCapture(spec, outDir, null, out err);
            if (!ok && report != null) report.Error("audio", err);
            if (ok && report != null) report.Note("audio", "capture started (output " + (cur.muted ? OutputMuted : OutputAudible) + ", " + PathName(cur) + "): " + cur.abs + " -> " + cur.dest + "; " + (cur.muted ? "the window opens at level " + cur.level.ToString("0.###", IC) + " " + (cur.useSuspend ? "after 1 s of a quiet heartbeat" : "once the file appeared") : "the window opened at once at level " + cur.level.ToString("0.###", IC) + " (game volume " + cur.gameLevel.ToString("0.###", IC) + ", audio.level " + cur.spec.level.ToString("0.###", IC) + ")"));
            return ok;
        }

        static bool StartCapture(AudioCapSpec spec, string outDir, string manualName, out string err)
        {
            err = Preflight(spec);
            if (err != null) { err = "audio capture refused: " + err; DevLog.Write("[acap] " + err); return false; }
            var c = new Cap();
            c.spec = spec;
            c.manual = manualName != null;
            c.muted = MutedOutput;
            c.level = float.IsNaN(spec.level) ? 0f : Mathf.Clamp(spec.level, 0f, 100f);
            c.useSuspend = c.muted && UsesSuspend(spec);
            if (!c.muted)
            {
                c.gameLevel = GameLevel(out c.gameLevelSource);
                if (float.IsNaN(c.gameLevel)) { err = "audio capture refused: game volume unknown (" + c.gameLevelSource + "), the window level cannot be capped at it"; DevLog.Write("[acap] " + err); return false; }
                c.level = Math.Min(c.level, c.gameLevel);
            }
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", IC);
            c.name = c.manual ? manualName : "rec_" + stamp;
            try
            {
                Directory.CreateDirectory(AudioDir);
                c.abs = Path.GetFullPath(Path.Combine(AudioDir, c.name + ".wav"));
                for (int k = 1; File.Exists(c.abs) && !c.manual; k++) { c.name = "rec_" + stamp + "_" + k.ToString("00", IC); c.abs = Path.GetFullPath(Path.Combine(AudioDir, c.name + ".wav")); }
                if (File.Exists(c.abs)) { err = "audio capture refused: " + c.abs + " exists (choose another name)"; return false; }
                c.bank = SoundBankInfoManager.GetBankPath();
                c.rel = AudioCaptureLogic.Relative(c.bank, c.abs);
                if (c.manual)
                {
                    c.dir = AudioDir;
                    c.dest = c.abs;
                    c.sync = Path.Combine(AudioDir, c.name + "_sync.csv");
                    c.info = Path.Combine(AudioDir, c.name + "_capture.json");
                }
                else
                {
                    c.dir = Path.GetFullPath(outDir);
                    Directory.CreateDirectory(c.dir);
                    c.dest = Path.Combine(c.dir, ReplayConst.AudioFile);
                    c.sync = Path.Combine(c.dir, ReplayConst.AudioSyncFile);
                    c.info = Path.Combine(c.dir, CaptureInfoFile);
                }
            }
            catch (Exception e) { err = "audio capture refused: paths: " + e.GetType().Name + ": " + e.Message; return false; }
            if (Path.Combine(c.bank, c.rel).Length >= 260 || c.abs.Length >= 260) { err = "audio capture refused: capture path too long (" + Path.Combine(c.bank, c.rel).Length + " characters)"; return false; }
            if (c.muted)
            {
                var h = Heartbeat(spec.heartbeat, true);
                c.seenStart = h.unmutedSeenTotal;
                c.hbStart = h.Text();
            }
            if (c.useSuspend)
            {
                try { c.flagStage = AudioCaptureLogic.FlagStage(File.ReadAllText(SuspendFlag)); } catch { }
                if (!suspended)
                {
                    AKRESULT r;
                    try { r = AkSoundEngine.Suspend(true); }
                    catch (Exception e) { err = "audio capture refused: Suspend(true): " + e.GetType().Name + ": " + e.Message; return false; }
                    c.suspendResult = r.ToString();
                    if (r != AKRESULT.AK_Success) { err = "audio capture refused: Suspend(true) returned " + r; DevLog.Write("[acap] " + err); return false; }
                    MarkSuspended("capture " + c.name);
                }
                else c.suspendResult = "already suspended";
                if (FlagTimer()) { try { c.timer = timeBeginPeriod(1) == 0; } catch (Exception e) { c.notes.Add("timeBeginPeriod: " + e.Message); } }
            }
            else if (c.muted)
            {
                try { c.flagStage = AudioCaptureLogic.FlagStage(File.ReadAllText(MuterFlag)); } catch { }
            }
            if (!spec.keepSuspended && c.useSuspend) c.notes.Add("keepSuspended false is not acted on: WakeupFromSuspend only through 'acap suspend off' (G6)");
            try
            {
                SetAside(c.dest, stamp);
                SetAside(c.sync, stamp);
                SetAside(c.info, stamp);
                c.rows = new StreamWriter(c.sync, false, new UTF8Encoding(false));
                c.rows.WriteLine("marker,label,sample,game_time,real_time,lag");
                c.rows.Flush();
            }
            catch (Exception e) { EndTimer(c); err = "audio capture refused: " + c.sync + ": " + e.Message; return false; }
            bool started;
            try { started = SoundEngine.StartOutputCapture(c.rel); }
            catch (Exception e) { started = false; c.notes.Add("StartOutputCapture: " + e.GetType().Name + ": " + e.Message); }
            if (!started)
            {
                EndTimer(c);
                try { c.rows.Close(); File.Delete(c.sync); } catch { }
                err = "audio capture refused: StartOutputCapture(" + c.rel + ") returned false" + (c.notes.Count > 0 ? " (" + c.notes[c.notes.Count - 1] + ")" : "");
                DevLog.Write("[acap] " + err);
                return false;
            }
            c.t0 = Now;
            cur = c;
            capturing = true;
            state = "starting";
            lastError = null;
            DevLog.Write("[acap] capture " + c.name + " started (output " + (c.muted ? OutputMuted : OutputAudible) + ", " + PathName(c) + (c.timer ? ", timeBeginPeriod(1)" : "") + "): StartOutputCapture(" + c.rel + ") -> " + c.abs + (c.manual ? "" : " -> " + c.dest) + "; window at " + c.level.ToString("0.###", IC) +
                         (c.muted ? " once the file appeared" + (c.useSuspend ? " and the heartbeat was quiet for 1 s" : "") : " at once (game volume " + c.gameLevel.ToString("0.###", IC) + " from " + c.gameLevelSource + ")"));
            if (c.muted) return true;
            OpenWindow(c);
            if (capturing) return true;
            err = "audio capture stopped at once: " + (lastError ?? "window open failed");
            return false;
        }

        static void SetAside(string path, string stamp)
        {
            if (!File.Exists(path)) return;
            string to = path + ".old_" + stamp;
            File.Move(path, to);
            DevLog.Write("[acap] kept the previous " + Path.GetFileName(path) + " as " + Path.GetFileName(to));
        }

        static void MarkSuspended(string by)
        {
            suspended = true;
            suspendedAt = Now;
            quietStartMs = -1;
            DevLog.Write("[acap] AkSoundEngine.Suspend(true) (" + by + "): stays suspended until game exit or 'acap suspend off'");
        }

        // ------------------------------------------------------------------ markers

        public static void Marker(string label, int sample, double gameTime, int lag)
        {
            var c = cur;
            if (!capturing || c == null || c.rows == null) return;
            string l = AudioCaptureLogic.SafeLabel(label);
            int eff = lag;
            if (lag >= 0 && SceneHooks.lateFrame == Time.frameCount)
            {
                eff = lag + 1;
                c.late++;
                lateMarkers++;
                Rate("late marker", "[acap] marker " + l + " posted after this frame's LateUpdate (past RenderAudio): row lag " + eff);
            }
            Post(c, l, sample, gameTime, eff);
        }

        static void Post(Cap c, string label, int sample, double gameTime, int lag)
        {
            AKRESULT r;
            try { r = AkSoundEngine.AddOutputCaptureMarker(label); }
            catch (Exception e) { c.markerFails++; Rate("marker error", "[acap] AddOutputCaptureMarker(" + label + "): " + e.GetType().Name + ": " + e.Message); return; }
            if (r != AKRESULT.AK_Success) { c.markerFails++; Rate("marker result", "[acap] AddOutputCaptureMarker(" + label + ") returned " + r); return; }
            try
            {
                c.rows.WriteLine(AudioCaptureLogic.SyncRow(c.markers, label, sample, gameTime, Now - clockBase, lag));
                c.rows.Flush();
            }
            catch (Exception e) { Rate("sync row", "[acap] " + ReplayConst.AudioSyncFile + ": " + e.Message); }
            c.markers++;
        }

        // ------------------------------------------------------------------ level (G4)

        public static bool ForceLevel(float level)
        {
            Ensure();
            if (float.IsNaN(level)) level = 0f;
            level = Mathf.Clamp(level, 0f, 100f);
            if (level > 0f && !opening && !(windowOpen && cur != null && state == "window"))
            {
                DevLog.Write("[acap] ForceLevel(" + level.ToString("0.###", IC) + ") refused: no open capture window");
                return false;
            }
            if (level <= 0f) return Close("close");
            if (cur != null && !cur.muted && !float.IsNaN(cur.gameLevel)) level = Math.Min(level, cur.gameLevel);
            if (windowOpen) windowLevel = level;
            return ForceInternal(level, "level");
        }

        static bool Close(string purpose) { return CloseMuted ? ForceInternal(0f, purpose) : Release(purpose); }

        static bool Release(string purpose)
        {
            if (windowOpen) CloseWindowState(purpose);
            bool own = OwnRuleActive();
            string back = priorRule;
            ownRule = null;
            priorRule = null;
            if (!own) return true;
            float target = float.NaN;
            string src = "rule " + back + " put back";
            int n = Math.Max(1, changesPerForce);
            try
            {
                if (back != null)
                {
                    if (!float.TryParse(back, NumberStyles.Float, IC, out target)) target = float.NaN;
                    for (int i = 0; i < n; i++) AudioRules.Change(RtpcKey, back, Source);
                }
                else
                {
                    string gs;
                    target = GameLevel(out gs);
                    if (!float.IsNaN(target)) for (int i = 0; i < n; i++) AudioRules.Change(RtpcKey, target.ToString("0.###", IC), Source);
                    AudioRules.Change(RtpcKey, null, Source);
                    src = "rule removed, game volume " + (float.IsNaN(target) ? "unknown (" + gs + ")" : target.ToString("0.###", IC) + " from " + gs);
                }
            }
            catch (Exception e) { DevLog.Error("acap rtpc release", e); return false; }
            DevLog.Write("[acap] rtpc " + RtpcName + " released (" + purpose + "): " + src);
            if (float.IsNaN(target) || !SoundEngineInner.Ready) return true;
            verify = new Verify { target = target, t0 = Now, frame = Time.frameCount, purpose = "release" };
            return true;
        }

        static bool ForceInternal(float level, string purpose)
        {
            string v = level.ToString("0.###", IC);
            if (AudioRules.Check(RtpcKey, v) != null) return false;
            if (level <= 0f && windowOpen) CloseWindowState(purpose);
            int n = Math.Max(1, changesPerForce);
            try { for (int i = 0; i < n; i++) AudioRules.Change(RtpcKey, v, Source); }
            catch (Exception e) { DevLog.Error("acap rtpc " + v, e); return false; }
            ownRule = v;
            if (!SoundEngineInner.Ready) { Rate("not ready", "[acap] rtpc rule " + v + " set; the sound engine wrapper is not installed, Wwise gets it at the game's next set"); return false; }
            verify = new Verify { target = level, t0 = Now, frame = Time.frameCount, purpose = purpose };
            float rb; string err;
            if (!ReadRtpc(out rb, out err)) { readbackErrors++; Rate("readback api", "[acap] GetRTPCValue failed: " + err); return false; }
            verify.first = rb;
            return true;
        }

        static void ZeroRule(string why)
        {
            reforces++;
            Rate("zero rule", "[acap] rtpc " + RtpcName + " forced 0 (" + why + ")");
            ForceInternal(0f, "watchdog");
        }

        static bool ReadRtpc(out float v, out string err)
        {
            v = float.NaN;
            try
            {
                int t = (int)RTPCValue_type.RTPCValue_Global;
                var r = AkSoundEngine.GetRTPCValue(RtpcName, null, out v, ref t);
                if (r != AKRESULT.AK_Success) { err = "GetRTPCValue returned " + r; return false; }
                err = null;
                return true;
            }
            catch (Exception e) { err = e.GetType().Name + ": " + e.Message; return false; }
        }

        static bool IsZero(string rule) { return AudioCaptureLogic.IsZeroRule(rule); }

        static void CloseWindowState(string why)
        {
            if (!windowOpen) return;
            windowOpen = false;
            var c = cur;
            if (c != null && c.tClose < 0 && test == null)
            {
                c.tClose = Now - clockBase;
                if (capturing) Post(c, "wclose", -1, double.NaN, -1);
                DevLog.Write("[acap] capture window closed (" + why + ")");
            }
        }

        // ------------------------------------------------------------------ tick (G5 watchdog)

        public static void Tick()
        {
            int f = Time.frameCount;
            if (f == tickFrame) return;
            tickFrame = f;
            try
            {
                TickVerify();
                if (suspended) TrackQuiet();
                if (wake != null) TickWake();
                if (test != null) TickTest();
                var c = cur;
                if (c != null)
                {
                    if (state == "starting") TickStarting(c);
                    else if (state == "arming") TickArming(c);
                    else if (state == "window") TickWindow(c);
                    else if (state == "finalizing") TickFinalize(c);
                }
                if (windowOpen && test == null && (c == null || state != "window")) { DevLog.Write("[acap] window without an owner: closed"); Close("orphan window"); windowOpen = false; }
                if (!windowOpen) Outside(f);
            }
            catch (Exception e)
            {
                Rate("tick", "ERROR in acap tick: " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);
                if (windowOpen) { try { Emergency("tick exception: " + e.Message); } catch { } }
            }
        }

        static void Outside(int f)
        {
            if (!CloseMuted)
            {
                if (cur == null && test == null && wake == null && verify == null && OwnRuleActive()) Release("audible output: acap rule outside a capture");
                return;
            }
            if (!IsZero(AudioRules.Get(RtpcKey))) { ZeroRule("watchdog: rule was " + (AudioRules.Get(RtpcKey) ?? "removed")); return; }
            if (verify != null || test != null || f % 10 != 0 || !SoundEngineInner.Ready) return;
            float v; string err;
            if (!ReadRtpc(out v, out err)) { Rate("watch readback", "[acap] watchdog GetRTPCValue failed: " + err); return; }
            if (Math.Abs(v) > 0.01f)
            {
                nonZeroReadbacks++;
                if (Now < nextReadbackForce) { Rate("readback nonzero", "ERROR [acap] watchdog: Wwise still reads " + v.ToString("0.###", IC) + " outside a window"); return; }
                nextReadbackForce = Now + 1.0;
                ZeroRule("watchdog: Wwise read " + v.ToString("0.###", IC) + " outside a window");
            }
        }

        static void TickVerify()
        {
            var q = verify;
            if (q == null || Time.frameCount - q.frame < 2) return;
            float v; string err;
            bool ok = ReadRtpc(out v, out err);
            if (ok && Math.Abs(v - q.target) <= 0.01f)
            {
                verify = null;
                if (q.target <= 0f) zeroVerifiedAt = Now;
                string line = q.purpose + " " + q.target.ToString("0.###", IC) + ": readback ok" + (q.retried ? " after a repeated Change" : "") + " (" + (Time.frameCount - q.frame) + " frames)";
                if (cur != null) cur.readbacks.Add(line);
                if (q.purpose != "watchdog") DevLog.Write("[acap] " + line);
                return;
            }
            if (Now - q.t0 < 0.5) return;
            if (!q.retried)
            {
                q.retried = true;
                q.t0 = Now;
                q.frame = Time.frameCount;
                string tv = q.target.ToString("0.###", IC);
                DevLog.Write("[acap] readback " + (ok ? v.ToString("0.###", IC) : err) + " != " + tv + " after 0.5 s (" + q.purpose + "): Change repeated");
                try { for (int i = 0; i < Math.Max(1, changesPerForce); i++) AudioRules.Change(RtpcKey, tv, Source); } catch (Exception e) { DevLog.Error("acap rtpc repeat", e); }
                return;
            }
            verify = null;
            readbackErrors++;
            string msg = "rtpc readback mismatch: Wwise reads " + (ok ? v.ToString("0.###", IC) : err) + ", expected " + q.target.ToString("0.###", IC) + " (" + q.purpose + ", after a repeated Change)";
            DevLog.Write("ERROR [acap] " + msg);
            if (cur != null) cur.readbacks.Add(msg);
            lastError = msg;
            if (q.target > 0f && windowOpen) Emergency(msg);
        }

        static void TickStarting(Cap c)
        {
            if (File.Exists(c.abs))
            {
                c.tAppear = Now;
                state = "arming";
                DevLog.Write("[acap] capture file appeared after " + (c.tAppear - c.t0).ToString("0.000", IC) + " s");
                TickArming(c);
                return;
            }
            if (Now - c.t0 > 2.0) Emergency("capture file did not appear at " + c.abs + " within 2 s");
        }

        static void TickArming(Cap c)
        {
            var h = Heartbeat(c.spec.heartbeat, false);
            Observe(c, h);
            string gate = AudioCaptureLogic.Gate(h, c.spec.maxHeartbeatAge, suspended, false, c.seenStart, OwnPid());
            if (gate != null) { Emergency("InsideMuter gate before the window: " + gate); return; }
            if (c.useSuspend && !QuietFor())
            {
                if (Now - c.t0 > 15.0) Emergency("INSIDE render stream still active 15 s after Suspend(true): the rtpc is not lifted");
                return;
            }
            if (c.useSuspend) c.quietWaitS = Now - c.t0;
            OpenWindow(c);
        }

        static void OpenWindow(Cap c)
        {
            if (!c.muted) priorRule = ForeignRule();
            if (c.level <= 0f)
            {
                state = "window";
                c.tOpen = Now - clockBase;
                c.notes.Add("level 0: the window stays at rtpc 0");
                if (!c.muted && !ForceInternal(0f, "open")) Emergency("window open: the rtpc rule or GetRTPCValue call failed");
                return;
            }
            opening = true;
            bool ok;
            try
            {
                windowLevel = c.level;
                windowOpen = true;
                state = "window";
                c.tOpen = Now - clockBase;
                Post(c, "wopen", -1, double.NaN, -1);
                ok = ForceInternal(c.level, "open");
            }
            finally { opening = false; }
            if (!ok) { Emergency("window open: the rtpc rule or GetRTPCValue call failed"); return; }
            DevLog.Write("[acap] capture window open: " + RtpcName + " " + c.level.ToString("0.###", IC) + (c.useSuspend ? " (suspended, quiet after " + c.quietWaitS.ToString("0.00", IC) + " s)" : c.muted ? " (muter-only)" : " (audible, game volume " + c.gameLevel.ToString("0.###", IC) + (priorRule != null ? ", rule " + priorRule + " put back at close" : "") + ")"));
        }

        static void TickWindow(Cap c)
        {
            if (c.tAppear < 0)
            {
                if (File.Exists(c.abs)) { c.tAppear = Now; DevLog.Write("[acap] capture file appeared after " + (c.tAppear - c.t0).ToString("0.000", IC) + " s"); }
                else if (Now - c.t0 > 2.0) { Emergency("capture file did not appear at " + c.abs + " within 2 s"); return; }
            }
            if (!windowOpen) return;
            string rule = AudioRules.Get(RtpcKey);
            float rv;
            if (rule == null || !float.TryParse(rule, NumberStyles.Float, IC, out rv) || Math.Abs(rv - windowLevel) > 0.001f) { Emergency("rtpc rule changed inside the window: " + (rule ?? "removed")); return; }
            if (c.muted)
            {
                var h = Heartbeat(c.spec.heartbeat, false);
                Observe(c, h);
                string gate = AudioCaptureLogic.Gate(h, c.spec.maxHeartbeatAge, suspended, c.useSuspend, c.seenStart, OwnPid());
                if (gate != null) { Emergency("InsideMuter gate: " + gate); return; }
            }
            if (verify != null || Time.frameCount % 10 != 0) return;
            float v; string err;
            if (!ReadRtpc(out v, out err)) { Emergency("GetRTPCValue failed inside the window: " + err); return; }
            if (Math.Abs(v - windowLevel) > 0.01f) Emergency("Wwise reads " + RtpcName + " " + v.ToString("0.###", IC) + " inside the window (level " + windowLevel.ToString("0.###", IC) + ")");
        }

        static void Observe(Cap c, AudioHeartbeat h)
        {
            if (h == null || !h.fresh) return;
            h.fresh = false;
            c.hbReads++;
            if (!h.parsed) { c.hbBad++; return; }
            if (h.age > c.maxAge) c.maxAge = h.age;
            if (h.maxPeak > c.maxPeak) c.maxPeak = h.maxPeak;
            if (h.active > 0) c.everActive++;
            if (h.unmutedSeenTotal >= 0) c.seenEnd = h.unmutedSeenTotal;
        }

        // ------------------------------------------------------------------ emergency and stop

        static void Emergency(string why)
        {
            if (inEmergency) return;
            inEmergency = true;
            try
            {
                emergencies++;
                lastError = why;
                DevLog.Write("ERROR [acap] emergency stop: " + why + (CloseMuted ? " -> rtpc 0, capture stopped, Suspend kept" : " -> rtpc released to the game volume, capture stopped (audible output)"));
                if (test != null) { AbortTest(why); }
                Close("emergency");
                windowOpen = false;
                if (cur != null && state != "finalizing") StopCapture(why, true);
            }
            finally { inEmergency = false; }
        }

        public static void Stop(string why)
        {
            try
            {
                if (cur == null || state == "finalizing" || state == "idle")
                {
                    if (windowOpen && test == null) Close("stop");
                    return;
                }
                StopCapture(why ?? "stop", false);
            }
            catch (Exception e) { DevLog.Error("acap stop", e); capturing = false; }
        }

        static void StopCapture(string why, bool isError)
        {
            var c = cur;
            if (c == null) return;
            Close(isError ? "emergency" : "close");
            windowOpen = false;
            try { SoundEngine.StopOutputCapture(); } catch (Exception e) { c.notes.Add("StopOutputCapture: " + e.GetType().Name + ": " + e.Message); }
            capturing = false;
            c.tStop = Now;
            c.why = why ?? "";
            if (isError && c.error == null) c.error = why;
            if (c.muted && lastHb != null) c.hbEnd = lastHb.Text();
            try { if (c.rows != null) { c.rows.Flush(); c.rows.Close(); } } catch { }
            c.rows = null;
            EndTimer(c);
            state = "finalizing";
            c.nextMove = Now + 0.1;
            DevLog.Write("[acap] capture " + c.name + " stopped (" + c.why + ") after " + (c.tStop - c.t0).ToString("0.00", IC) + " s, " + c.markers + " marker(s)" + (c.error != null ? ", error: " + c.error : "") + "; finalizing");
        }

        static void EndTimer(Cap c)
        {
            if (c == null || !c.timer) return;
            c.timer = false;
            try { timeEndPeriod(1); } catch { }
        }

        static void TickFinalize(Cap c)
        {
            if (Now < c.nextMove) return;
            c.nextMove = Now + 0.1;
            Finalize(c, false);
        }

        static void Finalize(Cap c, bool last)
        {
            if (c.final.Length == 0)
            {
                string fail = null;
                try
                {
                    if (!File.Exists(c.abs)) fail = "capture file missing: " + c.abs;
                    else if (c.dest != c.abs) { File.Move(c.abs, c.dest); c.moved = true; c.final = c.dest; }
                    else { using (new FileStream(c.abs, FileMode.Open, FileAccess.Read, FileShare.None)) { } c.final = c.abs; }
                }
                catch (Exception e) { c.moveError = e.GetType().Name + ": " + e.Message; }
                if (fail != null) { c.final = c.abs; if (c.error == null) c.error = fail; }
                else if (c.final.Length == 0)
                {
                    if (!last && Now - c.tStop < 10.0) return;
                    c.final = c.abs;
                    string m = "File.Move to " + c.dest + " failed for " + (Now - c.tStop).ToString("0.0", IC) + " s: " + c.moveError;
                    c.notes.Add(m);
                    if (c.error == null) c.error = m;
                }
            }
            c.tFinal = Now;
            try
            {
                if (File.Exists(c.final))
                    using (var fs = new FileStream(c.final, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) c.wav = AudioCaptureLogic.CheckWav(fs, fs.Length);
            }
            catch (Exception e) { c.notes.Add("RIFF check: " + e.Message); }
            c.needsFix = c.wav == null || !c.wav.ok;
            WriteInfo(c);
            double wall = c.tAppear >= 0 ? c.tStop - c.tAppear : c.tStop - c.t0;
            string sum = c.name + " -> " + c.final + ": " + (c.wav != null ? c.wav.Text() : "no RIFF check") + ", wall " + wall.ToString("0.000", IC) + " s" +
                         (c.wav != null && c.wav.seconds > 0 && wall > 0 ? " (WAV/wall " + (c.wav.seconds / wall).ToString("0.0000", IC) + ")" : "") + (c.needsFix ? "; header needs mcam_audio.py fix" : "") + (c.error != null ? "; error: " + c.error : "");
            lastCapture = sum;
            DevLog.Write("[acap] finalized " + sum);
            cur = null;
            state = "idle";
        }

        static void WriteInfo(Cap c)
        {
            try
            {
                var o = new Json.Obj();
                o["format"] = "acap.capture/1";
                o["name"] = c.name;
                o["manual"] = c.manual;
                o["file"] = c.final;
                o["captureFile"] = c.abs;
                o["captureArg"] = c.rel;
                o["bankPath"] = c.bank;
                o["moved"] = c.moved;
                if (c.moveError != null) o["moveError"] = c.moveError;
                o["sync"] = c.sync;
                o["output"] = c.muted ? OutputMuted : OutputAudible;
                o["path"] = c.useSuspend ? "suspend" : c.muted ? "muteronly" : "audible";
                if (!c.muted) { o["gameLevel"] = Round(c.gameLevel); o["gameLevelSource"] = c.gameLevelSource; o["specLevel"] = Round(c.spec.level); }
                o["flagStage"] = c.flagStage;
                o["suspendResult"] = c.suspendResult;
                o["timerPeriod1"] = c.useSuspend && FlagTimer();
                o["level"] = (double)c.level;
                o["clockBase"] = clockBase;
                o["startReal"] = Round(c.t0 - clockBase);
                o["appearedAfterS"] = c.tAppear >= 0 ? Round(c.tAppear - c.t0) : -1.0;
                o["quietWaitS"] = Round(c.quietWaitS);
                o["windowOpenReal"] = c.tOpen >= 0 ? Round(c.tOpen) : (object)null;
                o["windowCloseReal"] = c.tClose >= 0 ? Round(c.tClose) : (object)null;
                o["stopReal"] = c.tStop >= 0 ? Round(c.tStop - clockBase) : (object)null;
                o["wallSeconds"] = c.tStop >= 0 ? Round(c.tStop - (c.tAppear >= 0 ? c.tAppear : c.t0)) : 0.0;
                o["markers"] = (double)c.markers;
                o["markerFailures"] = (double)c.markerFails;
                o["lateMarkers"] = (double)c.late;
                var hb = new Json.Obj();
                hb["start"] = c.hbStart; hb["end"] = c.hbEnd; hb["reads"] = (double)c.hbReads; hb["badReads"] = (double)c.hbBad;
                hb["maxAgeS"] = Round(c.maxAge); hb["maxSessionPeak"] = c.maxPeak; hb["activeReads"] = (double)c.everActive;
                hb["unmutedSeenStart"] = (double)c.seenStart; hb["unmutedSeenEnd"] = (double)c.seenEnd;
                o["heartbeat"] = hb;
                o["readbacks"] = new List<object>(c.readbacks.ToArray());
                o["readbackErrors"] = (double)readbackErrors;
                if (c.wav != null) o["wav"] = c.wav.ToJson();
                o["needsFix"] = c.needsFix;
                o["stopReason"] = c.why;
                o["error"] = c.error;
                o["notes"] = new List<object>(c.notes.ToArray());
                File.WriteAllText(c.info, Json.Write(o) + "\n");
            }
            catch (Exception e) { DevLog.Error("acap " + CaptureInfoFile, e); }
        }

        static double Round(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? -1.0 : Math.Round(v, 6); }

        // ------------------------------------------------------------------ heartbeat

        static AudioHeartbeat Heartbeat(string path, bool force)
        {
            if (string.IsNullOrEmpty(path)) path = DefaultHeartbeat;
            try { if (!Path.IsPathRooted(path)) path = Path.GetFullPath(Path.Combine(GameDir, path)); } catch (Exception) { }
            long nowMs = UnixMs();
            if (path != hbPath) { hbPath = path; lastHb = null; lastRead = null; }
            if (!force && lastRead != null && Now < nextHbRead) { Age(lastRead, nowMs); return lastRead; }
            nextHbRead = Now + 0.25;
            string text = null, err = null;
            bool missing = false;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
            }
            catch (FileNotFoundException) { missing = true; }
            catch (DirectoryNotFoundException) { missing = true; }
            catch (Exception e) { err = "heartbeat read: " + e.GetType().Name + ": " + e.Message; }
            if (missing) err = "no heartbeat file " + path + " (start InsideMuter first)";
            if (text != null)
            {
                var h = AudioCaptureLogic.ParseHeartbeat(text, nowMs, OwnPid());
                if (h.parsed) { h.fresh = true; lastHb = h; lastRead = h; return h; }
                err = h.error;
            }
            if (lastHb != null && !missing)
            {
                Age(lastHb, nowMs);
                lastHb.note = err;
                lastRead = lastHb;
                return lastHb;
            }
            var miss = new AudioHeartbeat();
            miss.error = err ?? "no heartbeat";
            miss.fresh = true;
            if (missing) lastHb = null;
            lastRead = miss;
            return miss;
        }

        static void Age(AudioHeartbeat h, long nowMs)
        {
            if (h.parsed) h.age = h.unixMs > 0 ? (nowMs - h.unixMs) / 1000.0 : double.PositiveInfinity;
        }

        static long UnixMs() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        static int OwnPid()
        {
            if (ownPid != -2) return ownPid;
            try { ownPid = System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch (Exception e) { ownPid = -1; DevLog.Write("[acap] own process id unavailable (" + e.Message + "): the heartbeat checks use all INSIDE sessions"); }
            return ownPid;
        }

        static void TrackQuiet()
        {
            var h = Heartbeat(cur != null ? cur.spec.heartbeat : DefaultHeartbeat, false);
            if (!h.parsed) { quietStartMs = -1; return; }
            if (AudioCaptureLogic.Quiet(h)) { if (quietStartMs < 0) { quietStartMs = h.unixMs; quietStartSeq = h.seq; } }
            else quietStartMs = -1;
        }

        static bool QuietFor()
        {
            var h = lastHb;
            return h != null && h.parsed && quietStartMs >= 0 && AudioCaptureLogic.Quiet(h) && h.unixMs - quietStartMs >= 1000 && h.seq - quietStartSeq >= 4;
        }

        // ------------------------------------------------------------------ suspend (G3, G6)

        static string SuspendOn()
        {
            if (suspended) return "already suspended by acap (" + (Now - suspendedAt).ToString("0.0", IC) + " s ago); quiet heartbeat for 1 s: " + (QuietFor() ? "yes" : "not yet");
            if (!MutedOutput) return "refused: output is audible (no Suspend); 'acap output muted' first";
            Ensure();
            AKRESULT r;
            try { r = AkSoundEngine.Suspend(true); } catch (Exception e) { return "Suspend(true) failed: " + e.GetType().Name + ": " + e.Message; }
            if (r != AKRESULT.AK_Success) return "Suspend(true) returned " + r;
            MarkSuspended("acap suspend on");
            return "AkSoundEngine.Suspend(true): " + r + " (render anyway; stays until game exit or 'acap suspend off')";
        }

        static string SuspendOff()
        {
            if (!suspended) return "not suspended by acap";
            if (cur != null || windowOpen || test != null) return "refused: a capture, window or rtpctest is active";
            if (wake != null) return "wake already scheduled";
            wake = new Wake { t0 = Now };
            ForceInternal(0f, "wake");
            return "wake scheduled: rtpc forced 0, WakeupFromSuspend once the readback shows 0 and 100 ms have passed (G6)";
        }

        static void TickWake()
        {
            var w = wake;
            if (cur != null || windowOpen || test != null) { wake = null; DevLog.Write("[acap] wake cancelled: a capture or window started"); return; }
            if (Now - w.t0 > 3.0) { wake = null; DevLog.Write("ERROR [acap] wake cancelled: rtpc 0 not verified within 3 s"); return; }
            if (verify != null || zeroVerifiedAt < w.t0 || Now - w.t0 < 0.1) return;
            if (!IsZero(AudioRules.Get(RtpcKey))) return;
            wake = null;
            AKRESULT r;
            try { r = AkSoundEngine.WakeupFromSuspend(); } catch (Exception e) { DevLog.Write("ERROR [acap] WakeupFromSuspend: " + e.Message); return; }
            suspended = false;
            quietStartMs = -1;
            DevLog.Write("[acap] WakeupFromSuspend: " + r + " (rtpc 0 verified)");
        }

        // ------------------------------------------------------------------ rtpctest (AUDIO acceptance: 0 -> level -> 0, one Change each)

        static string StartTest(float level)
        {
            Ensure();
            if (!MutedOutput) return "refused: rtpctest belongs to the muted output (it ends with the rule at 0); 'acap output muted' first";
            if (cur != null) return "refused: a capture is active";
            if (test != null) return "refused: rtpctest already running";
            if (!SoundEngineInner.Ready) return "refused: the sound engine wrapper (AudioSpy) is not installed yet";
            if (!(level > 0f) || level > 100f) return "refused: level must be in (0, 100]";
            var h = Heartbeat(DefaultHeartbeat, true);
            string gate = AudioCaptureLogic.Gate(h, 2.0, suspended, false, -1, OwnPid());
            if (gate != null) return "refused: InsideMuter gate: " + gate;
            bool underSuspend = suspended && QuietFor();
            if (!underSuspend && !File.Exists(MuterFlag)) return "refused: run 'acap suspend on' first and wait for a quiet heartbeat (" + (suspended ? "suspended, not quiet for 1 s yet" : "not suspended") + "), or pass L5 S2 (user consent first)";
            test = new RtpcTest { level = level, values = new[] { 0f, level, 0f }, t0 = Now, native = AudioRules.nativeEnforce, underSuspend = underSuspend };
            NextTestStep();
            return "rtpctest started: " + RtpcName + " 0 -> " + level.ToString("0.###", IC) + " -> 0, one AudioRules.Change each, GetRTPCValue after each (native enforcement " + (test.native ? "on" : "off") + ", " + (underSuspend ? "suspended" : "muter-only") + "); result in acap status";
        }

        static void NextTestStep()
        {
            var t = test;
            t.index++;
            if (t.index >= t.values.Length) { FinishTest(true, "one Change per value reached Wwise"); return; }
            float v = t.values[t.index];
            if (v > 0f) { windowLevel = v; windowOpen = true; }
            else windowOpen = false;
            try { AudioRules.Change(RtpcKey, v.ToString("0.###", IC), Source + " rtpctest"); }
            catch (Exception e) { AbortTest("Change: " + e.Message); return; }
            ownRule = v.ToString("0.###", IC);
            t.stepT0 = Now;
            t.stepFrame = Time.frameCount;
        }

        static void TickTest()
        {
            var t = test;
            if (windowOpen)
            {
                var h = Heartbeat(DefaultHeartbeat, false);
                string gate = AudioCaptureLogic.Gate(h, 2.0, suspended, suspended, -1, OwnPid());
                if (gate != null) { AbortTest("InsideMuter gate: " + gate); return; }
            }
            if (Time.frameCount - t.stepFrame < 2) return;
            float v; string err;
            bool ok = ReadRtpc(out v, out err);
            float want = t.values[t.index];
            if (ok && Math.Abs(v - want) <= 0.01f)
            {
                t.lines.Add(want.ToString("0.###", IC) + ": ok after " + (Time.frameCount - t.stepFrame) + " frames (" + ((Now - t.stepT0) * 1000.0).ToString("0", IC) + " ms)");
                NextTestStep();
                return;
            }
            if (Now - t.stepT0 < 0.5) return;
            t.lines.Add(want.ToString("0.###", IC) + ": Wwise reads " + (ok ? v.ToString("0.###", IC) : err) + " after 0.5 s");
            AbortTest("readback mismatch at " + want.ToString("0.###", IC));
        }

        static void AbortTest(string why)
        {
            var t = test;
            if (t == null) return;
            windowOpen = false;
            ForceInternal(0f, "rtpctest abort");
            FinishTest(false, why);
        }

        static void FinishTest(bool pass, string why)
        {
            var t = test;
            test = null;
            windowOpen = false;
            lastTest = (pass ? "PASS" : "FAIL") + " rtpctest (native enforcement " + (t.native ? "on" : "off") + ", " + (t.underSuspend ? "suspended" : "muter-only") + ", level " + t.level.ToString("0.###", IC) + "): " + why + "; " + string.Join("; ", t.lines.ToArray());
            DevLog.Write("[acap] " + lastTest);
            try
            {
                var o = new Json.Obj();
                o["verdict"] = pass ? "PASS" : "FAIL";
                o["reason"] = why;
                o["nativeEnforce"] = t.native;
                o["underSuspend"] = t.underSuspend;
                o["level"] = (double)t.level;
                o["steps"] = new List<object>(t.lines.ToArray());
                o["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", IC);
                Directory.CreateDirectory(AudioDir);
                File.WriteAllText(Path.Combine(AudioDir, "rtpctest_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", IC) + (t.native ? "_native" : "_managed") + ".json"), Json.Write(o) + "\n");
            }
            catch (Exception e) { DevLog.Error("acap rtpctest result", e); }
        }

        // ------------------------------------------------------------------ commands

        const string Usage =
            "acap status | preflight [suspend=auto|on|off] [muteronly=on] [hb=<heartbeat.json>] [hbage=2]\n" +
            "acap start <name> [suspend=auto|on|off] [muteronly=on] [level=100] [hb=<heartbeat.json>] [hbage=2]   (to <game>/_mod/audio/<name>.wav)\n" +
            "acap mark <ascii> | acap stop | acap suspend on|off | acap rtpctest [level=100] | acap changes 1|2\n" +
            "acap output [audible|muted]   (" + OutputKey + " in editor.cfg, default audible; suspend, muteronly, hb, hbage, rtpctest and 'suspend on' are muted-only)";

        public static string Command(List<string> a, DevCore core)
        {
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            try
            {
                Ensure();
                switch (sub)
                {
                    case "status": return Status();
                    case "help": return Usage;
                    case "preflight": return PreflightText(a);
                    case "start": return ManualStart(a);
                    case "mark": return ManualMark(a);
                    case "stop":
                        if (cur == null) return "no capture running" + (lastCapture.Length > 0 ? "; last: " + lastCapture : "");
                        if (state == "finalizing") return "finalizing " + cur.name;
                        Stop("acap stop");
                        return "capture stopped (" + (cur.muted ? "rtpc forced 0 first" : "rtpc released to the game volume first") + "); finalizing " + cur.name + " -> " + cur.dest;
                    case "output": return OutputCommand(a);
                    case "suspend":
                        if (a.Count < 3) return "usage: acap suspend on|off";
                        if (a[2] == "on") return SuspendOn();
                        if (a[2] == "off") return SuspendOff();
                        return "usage: acap suspend on|off";
                    case "rtpctest": return StartTest(Arg(a, "level", 100f));
                    case "changes":
                        int n;
                        if (a.Count < 3 || !int.TryParse(a[2], out n) || n < 1 || n > 2) return "usage: acap changes 1|2 (now " + changesPerForce + ")";
                        changesPerForce = n;
                        DevLog.Write("[acap] AudioRules.Change calls per ForceLevel: " + n);
                        return "ForceLevel uses " + n + " Change call(s) per level (readback still decides)";
                }
            }
            catch (Exception e) { DevLog.Error("acap " + sub, e); return "acap " + sub + " failed: " + e.GetType().Name + ": " + e.Message; }
            return "usage:\n" + Usage;
        }

        static string OutputCommand(List<string> a)
        {
            string raw = EditorState.Get(OutputKey, (string)null);
            if (a.Count < 3) return "acap output " + Output + " (" + OutputKey + " = " + (raw ?? "(unset)") + ", default " + OutputAudible + ")";
            string v = a[2].Trim().ToLowerInvariant();
            if (v != OutputAudible && v != OutputMuted) return "usage: acap output audible|muted (now " + Output + ")";
            if (cur != null || windowOpen || test != null || wake != null) return "refused: a capture, window, rtpctest or wake is active (output stays " + Output + ")";
            string was = Output;
            EditorState.Set(OutputKey, v);
            DevLog.Write("[acap] output " + was + " -> " + v + " (" + OutputKey + " saved to editor.cfg)");
            Ensure();
            return "output " + v + (was == v ? " (unchanged)" : " (was " + was + ")") + ": " + (v == OutputMuted ?
                "InsideMuter heartbeat and flag files required, rtpc " + RtpcName + " forced 0 outside capture windows" :
                "game sound plays normally, no heartbeat, no Suspend, no flags; acap's own rtpc rule removed") + (suspended ? "; Wwise is still suspended by acap ('acap suspend off')" : "");
        }

        static AudioCapSpec ManualSpec(List<string> a)
        {
            var s = new AudioCapSpec();
            s.capture = "record";
            s.suspend = ArgS(a, "suspend", s.suspend);
            s.allowMuterOnly = ArgS(a, "muteronly", "off") == "on";
            s.level = Arg(a, "level", s.level);
            s.heartbeat = ArgS(a, "hb", s.heartbeat);
            s.maxHeartbeatAge = Arg(a, "hbage", s.maxHeartbeatAge);
            return s;
        }

        static string PreflightText(List<string> a)
        {
            var sb = new StringBuilder();
            string why = Check(ManualSpec(a), sb);
            return (why == null ? "ok" : "refused: " + why) + "\n" + sb.ToString().TrimEnd('\n');
        }

        static double manualGame0;
        static int manualMarks;

        static string ManualStart(List<string> a)
        {
            if (a.Count < 3 || a[2].IndexOf('=') >= 0) return "usage: acap start <name> [suspend=auto|on|off] [muteronly=on] [level=100]";
            string name = a[2];
            foreach (char ch in name) if (!(char.IsLetterOrDigit(ch) && ch < 128) && ch != '_' && ch != '-') return "name: ASCII letters, digits, '_' and '-' only";
            if (name.Length > 64) return "name: 64 characters at most";
            if (MultiCam.runner != null) return "busy: an mcam run owns the capture";
            if (SceneRecorder.active != null) return "busy: a recording is active (its capture goes through srec record ... audio=on)";
            var spec = ManualSpec(a);
            double base0 = Now;
            double was = clockBase;
            clockBase = base0;
            string err;
            if (!StartCapture(spec, null, name, out err)) { clockBase = was; return err; }
            manualGame0 = Time.timeDbl;
            manualMarks = 0;
            Post(cur, "s0", 0, 0.0, 0);
            manualMarks = 1;
            return "capture " + name + " started -> " + cur.abs + " (output " + (cur.muted ? OutputMuted : OutputAudible) + ", " + PathName(cur) + ", window at " + cur.level.ToString("0.###", IC) + (cur.muted ? "" : ", open, game volume " + cur.gameLevel.ToString("0.###", IC)) + "); acap mark <ascii>, acap stop";
        }

        static string ManualMark(List<string> a)
        {
            if (cur == null || !capturing) return "no capture running";
            if (a.Count < 3) return "usage: acap mark <ascii>";
            string label = AudioCaptureLogic.SafeLabel(a[2]);
            Marker(label, manualMarks, Time.timeDbl - manualGame0, 0);
            manualMarks++;
            return "marker " + label + " posted (" + cur.markers + " so far)";
        }

        static float Arg(List<string> a, string key, float def)
        {
            string s = ArgS(a, key, null);
            float v;
            return s != null && float.TryParse(s, NumberStyles.Float, IC, out v) ? v : def;
        }

        static string ArgS(List<string> a, string key, string def)
        {
            for (int i = 2; i < a.Count; i++)
                if (a[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[i].Substring(key.Length + 1);
            return def;
        }

        public static string Status()
        {
            var sb = new StringBuilder();
            var c = cur;
            bool muted = MutedOutput;
            sb.Append("acap ").Append(state).Append(", output ").Append(Output).Append(capturing ? ", capturing" : "").Append(windowOpen ? ", window open at " + windowLevel.ToString("0.###", IC) : "")
              .Append(suspended ? ", Wwise suspended " + (Now - suspendedAt).ToString("0", IC) + " s (quiet " + (QuietFor() ? "yes" : "no") + ")" : "").Append('\n');
            if (c != null)
            {
                long size = -1;
                try { var fi = new FileInfo(c.final.Length > 0 ? c.final : c.abs); if (fi.Exists) size = fi.Length; } catch { }
                sb.Append("  ").Append(c.name).Append(" (").Append(c.muted ? OutputMuted : OutputAudible).Append(", ").Append(PathName(c)).Append(c.muted ? "" : ", game volume " + c.gameLevel.ToString("0.###", IC)).Append("): ").Append(c.abs).Append(" -> ").Append(c.dest)
                  .Append(", ").Append((Now - c.t0).ToString("0.0", IC)).Append(" s, file ").Append(size >= 0 ? (size / 1024.0).ToString("0", IC) + " kB" : "not yet")
                  .Append(", markers ").Append(c.markers).Append(c.markerFails > 0 ? " (" + c.markerFails + " failed)" : "").Append(c.late > 0 ? ", late " + c.late : "")
                  .Append(c.error != null ? ", error: " + c.error : "").Append('\n');
            }
            var h = lastRead ?? lastHb;
            if (muted) sb.Append("  heartbeat: ").Append(h != null ? h.Text() : "not read yet").Append('\n');
            else
            {
                string src;
                float g = GameLevel(out src);
                sb.Append("  audible output: no heartbeat, no Suspend, no flags; game volume ").Append(float.IsNaN(g) ? "unknown (" + src + ")" : g.ToString("0.###", IC) + " (" + src + ")")
                  .Append(", acap rule ").Append(OwnRuleActive() ? "active (" + ownRule + ")" : "none").Append(priorRule != null ? ", rule " + priorRule + " put back at close" : "").Append('\n');
            }
            sb.Append("  rule ").Append(RtpcKey).Append(" = ").Append(AudioRules.Get(RtpcKey) ?? "(none)");
            float v; string err;
            if (ReadRtpc(out v, out err)) sb.Append(", Wwise ").Append(v.ToString("0.###", IC)); else sb.Append(", Wwise readback failed: ").Append(err);
            sb.Append(verify != null ? ", readback pending (" + verify.purpose + " " + verify.target.ToString("0.###", IC) + ")" : "");
            sb.Append(", native enforcement ").Append(AudioRules.nativeEnforce ? "on" : "off").Append(", changes per force ").Append(changesPerForce).Append('\n');
            sb.Append("  ").Append(muted ? "flags: suspend " + (File.Exists(SuspendFlag) ? "present" : "absent") + ", muter-only " + (File.Exists(MuterFlag) ? "present" : "absent") : "flags: not used (audible)")
              .Append("; re-forces ").Append(reforces).Append(", non-zero readbacks ").Append(nonZeroReadbacks).Append(", readback errors ").Append(readbackErrors)
              .Append(", emergencies ").Append(emergencies).Append(", late markers ").Append(lateMarkers).Append(subscribed ? "" : ", guard NOT subscribed").Append('\n');
            if (lastError != null) sb.Append("  last error: ").Append(lastError).Append('\n');
            if (lastCapture.Length > 0) sb.Append("  last capture: ").Append(lastCapture).Append('\n');
            if (test != null) sb.Append("  rtpctest running: ").Append(string.Join("; ", test.lines.ToArray())).Append('\n');
            if (lastTest.Length > 0) sb.Append("  last rtpctest: ").Append(lastTest).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        static void Rate(string key, string msg)
        {
            int n;
            rate.TryGetValue(key, out n);
            rate[key] = ++n;
            if (n <= 5 || n % 100 == 0) DevLog.Write(msg + (n > 5 ? " (" + n + "x)" : ""));
        }
    }

    // ------------------------------------------------------------------ AudioCapture pure logic (System types and Json only; qa/audio runs it under .NET 9)

    public sealed class AudioHeartbeat
    {
        public bool parsed, fresh;
        public string error = "", note, mode = "", status = "", format = "";
        public long seq = -1, unixMs, unmutedSeenTotal = -1;
        public double age = double.PositiveInfinity, maxPeak, endpointPeak = -1;
        public int sessions, muted, unmuted, active, ownSessions, ownMuted, ownUnmuted, insideCount;
        public bool ownListed, peakUnknown;

        public string Text()
        {
            var ic = CultureInfo.InvariantCulture;
            if (!parsed) return "unusable: " + error;
            return "age " + age.ToString("0.00", ic) + " s, " + mode + "/" + status + ", seq " + seq + ", sessions " + sessions + " (muted " + muted + ", unmuted " + unmuted +
                   ", active " + active + ", own " + ownSessions + "), max session peak " + maxPeak.ToString("0.######", ic) + (peakUnknown ? " (some meters unavailable)" : "") +
                   ", unmutedSeenTotal " + unmutedSeenTotal + (note != null ? " [" + note + "]" : "");
        }
    }

    public sealed class AudioWavCheck
    {
        public bool riff, fmt, data, riffOk, dataOk, walkOk, ok;
        public int format, channels, rate, bits, blockAlign, cuePoints, labels;
        public long fileBytes, riffSize, dataOffset, dataSize, dataSizeRaw, walkEnd;
        public double seconds;
        public string error;

        public string Text()
        {
            var ic = CultureInfo.InvariantCulture;
            if (error != null && !fmt) return "RIFF check failed: " + error;
            return (ok ? "RIFF ok" : "RIFF NOT ok (" + (riffOk ? "" : "riff size, ") + (dataOk ? "" : "data size, ") + (walkOk ? "" : "chunk walk") + ")") + ", " + rate + " Hz x" + channels + " " + bits + " bit, " +
                   seconds.ToString("0.000", ic) + " s, cue points " + cuePoints + ", labels " + labels;
        }

        public Json.Obj ToJson()
        {
            var o = new Json.Obj();
            o["ok"] = ok; o["riffOk"] = riffOk; o["dataOk"] = dataOk; o["walkOk"] = walkOk;
            o["fileBytes"] = (double)fileBytes; o["riffSize"] = (double)riffSize; o["dataOffset"] = (double)dataOffset; o["dataSize"] = (double)dataSize; o["dataSizeRaw"] = (double)dataSizeRaw;
            o["format"] = (double)format; o["channels"] = (double)channels; o["rate"] = (double)rate; o["bits"] = (double)bits; o["blockAlign"] = (double)blockAlign;
            o["seconds"] = Math.Round(seconds, 6); o["cuePoints"] = (double)cuePoints; o["labels"] = (double)labels;
            if (error != null) o["error"] = error;
            return o;
        }
    }

    public static class AudioCaptureLogic
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static AudioHeartbeat ParseHeartbeat(string text, long nowUnixMs, int ownPid)
        {
            var h = new AudioHeartbeat();
            Json.Obj o;
            try { o = Json.Parse(text) as Json.Obj; }
            catch (Exception e) { h.error = "heartbeat parse: " + e.Message; return h; }
            if (o == null) { h.error = "heartbeat is not a JSON object"; return h; }
            h.format = o.Str("format", "");
            h.mode = o.Str("mode", "");
            h.status = o.Str("status", "");
            h.seq = (long)o.Num("seq", -1);
            h.unixMs = (long)o.Num("unixMs", 0);
            h.unmutedSeenTotal = (long)o.Num("unmutedSeenTotal", -1);
            var list = o["sessions"] as List<object>;
            if (list == null) { h.error = "heartbeat has no sessions list"; return h; }
            if (h.unixMs <= 0) { h.error = "heartbeat has no unixMs"; return h; }
            foreach (var x in list)
            {
                var s = x as Json.Obj;
                if (s == null) continue;
                var mv = s["muted"];
                if (!(mv is bool)) { h.error = "session without a muted flag"; return h; }
                bool m = (bool)mv;
                int pid = (int)s.Num("pid", -1);
                h.sessions++;
                if (m) h.muted++; else h.unmuted++;
                if (string.Equals(s.Str("state", ""), "active", StringComparison.OrdinalIgnoreCase)) h.active++;
                double pk = s.Num("peak", -1);
                if (pk < 0) h.peakUnknown = true;
                else if (pk > h.maxPeak) h.maxPeak = pk;
                if (ownPid > 0 && pid == ownPid) { h.ownSessions++; if (m) h.ownMuted++; else h.ownUnmuted++; }
            }
            var inside = o["inside"] as List<object>;
            if (inside != null)
                foreach (var x in inside)
                {
                    var p = x as Json.Obj;
                    if (p == null) continue;
                    h.insideCount++;
                    if (ownPid > 0 && (int)p.Num("pid", -1) == ownPid) h.ownListed = true;
                }
            var eps = o["endpoints"] as List<object>;
            if (eps != null) foreach (var x in eps) { var e = x as Json.Obj; if (e != null) h.endpointPeak = Math.Max(h.endpointPeak, e.Num("peak", -1)); }
            h.age = (nowUnixMs - h.unixMs) / 1000.0;
            h.parsed = true;
            return h;
        }

        // null = safe, else the reason. suspendedNow: Wwise suspended by acap (no muted session required); requireQuiet: no active
        // INSIDE stream allowed (inside a suspended window); seenStart >= 0: unmutedSeenTotal may not grow past it.
        public static string Gate(AudioHeartbeat h, double maxAge, bool suspendedNow, bool requireQuiet, long seenStart, int ownPid)
        {
            if (h == null || !h.parsed) return h != null && h.error.Length > 0 ? h.error : "no heartbeat";
            if (!(h.age <= maxAge)) return "heartbeat is " + h.age.ToString("0.0", IC) + " s old (max " + maxAge.ToString("0.0", IC) + " s)";
            if (h.mode != "run") return "muter mode '" + h.mode + "' ('run' required)";
            if (h.status != "running") return "muter status '" + h.status + "' ('running' required)";
            if (ownPid > 0 && !h.ownListed) return "the muter does not list this INSIDE.exe (pid " + ownPid + ")";
            if (h.unmuted > 0) return h.unmuted + " unmuted INSIDE session(s)";
            if (seenStart >= 0 && h.unmutedSeenTotal > seenStart) return "the muter found an unmuted INSIDE session (unmutedSeenTotal " + seenStart + " -> " + h.unmutedSeenTotal + ")";
            if (h.maxPeak > 0 && (requireQuiet || h.muted < h.sessions)) return "INSIDE session meter shows output (peak " + h.maxPeak.ToString("0.######", IC) + ") on a session that is not muted";
            if (requireQuiet && h.active > 0) return "INSIDE render stream active while suspended (" + h.active + " session(s))";
            if (!suspendedNow && (ownPid > 0 ? h.ownMuted == 0 : h.muted == 0)) return "no muted INSIDE session" + (ownPid > 0 ? " of this process" : "") + " while Wwise is not suspended";
            return null;
        }

        public static bool Quiet(AudioHeartbeat h) { return h != null && h.parsed && h.active == 0 && h.maxPeak <= 0; }

        public static string Relative(string fromDir, string toFile)
        {
            var sep = new[] { '\\', '/' };
            var a = Path.GetFullPath(fromDir).TrimEnd(sep).Split(sep);
            var b = Path.GetFullPath(toFile).Split(sep);
            int n = 0;
            while (n < a.Length && n < b.Length - 1 && a[n].Equals(b[n], StringComparison.OrdinalIgnoreCase)) n++;
            if (n == 0) return toFile;
            var sb = new StringBuilder();
            for (int i = n; i < a.Length; i++) sb.Append("..\\");
            for (int i = n; i < b.Length; i++) { sb.Append(b[i]); if (i < b.Length - 1) sb.Append('\\'); }
            return sb.ToString();
        }

        public static string SafeLabel(string s)
        {
            if (string.IsNullOrEmpty(s)) return "m";
            var sb = new StringBuilder(Math.Min(s.Length, 64));
            foreach (char c in s)
            {
                if (sb.Length >= 64) break;
                bool keep = c < 128 && (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ':' || c == '+');
                sb.Append(keep ? c : '_');
            }
            return sb.ToString();
        }

        public static string SyncRow(int marker, string label, int sample, double gameTime, double realTime, int lag)
        {
            return marker.ToString(IC) + "," + label + "," + sample.ToString(IC) + "," + (double.IsNaN(gameTime) ? "" : gameTime.ToString("0.######", IC)) + "," +
                   realTime.ToString("0.######", IC) + "," + lag.ToString(IC);
        }

        public static bool IsZeroRule(string v)
        {
            float f;
            return v != null && float.TryParse(v, NumberStyles.Float, IC, out f) && Math.Abs(f) < 1e-6f;
        }

        public static bool FlagRequiresTimer(string json)
        {
            try { var o = Json.Parse(json) as Json.Obj; return o != null && o.Bool("requiresTimerPeriod", false); }
            catch { return false; }
        }

        public static string FlagStage(string json)
        {
            try { var o = Json.Parse(json) as Json.Obj; return o != null ? o.Str("stage", "") : ""; }
            catch { return ""; }
        }

        public static AudioWavCheck CheckWav(Stream s, long length)
        {
            var w = new AudioWavCheck();
            w.fileBytes = length;
            var b = new byte[40];
            try
            {
                s.Position = 0;
                if (ReadFull(s, b, 12) < 12) { w.error = "shorter than a RIFF header"; return w; }
                if (Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") { w.error = "not a RIFF/WAVE file"; return w; }
                w.riff = true;
                w.riffSize = BitConverter.ToUInt32(b, 4);
                w.riffOk = w.riffSize != 0xFFFFFFFFL && w.riffSize + 8 == length;
                long p = 12;
                bool broken = false;
                while (p + 8 <= length)
                {
                    s.Position = p;
                    if (ReadFull(s, b, 8) < 8) { broken = true; break; }
                    string id = Encoding.ASCII.GetString(b, 0, 4);
                    long size = BitConverter.ToUInt32(b, 4);
                    long body = p + 8;
                    if (id == "fmt " && size >= 16 && body + 16 <= length)
                    {
                        int n = ReadFull(s, b, (int)Math.Min(40, size));
                        if (n >= 16)
                        {
                            w.fmt = true;
                            w.format = BitConverter.ToUInt16(b, 0);
                            w.channels = BitConverter.ToUInt16(b, 2);
                            w.rate = (int)BitConverter.ToUInt32(b, 4);
                            w.blockAlign = BitConverter.ToUInt16(b, 12);
                            w.bits = BitConverter.ToUInt16(b, 14);
                            if (w.format == 0xFFFE && n >= 26) w.format = BitConverter.ToUInt16(b, 24);
                        }
                    }
                    else if (id == "data")
                    {
                        w.data = true;
                        w.dataOffset = body;
                        w.dataSizeRaw = size;
                        w.dataOk = size != 0xFFFFFFFFL && body + size <= length;
                        w.dataSize = w.dataOk ? size : length - body;
                        if (!w.dataOk) { broken = true; break; }
                    }
                    else if (id == "cue " && body + 4 <= length)
                    {
                        if (ReadFull(s, b, 4) == 4) w.cuePoints = (int)BitConverter.ToUInt32(b, 0);
                    }
                    else if (id == "LIST" && body + 4 <= length && size != 0xFFFFFFFFL)
                    {
                        if (ReadFull(s, b, 4) == 4 && Encoding.ASCII.GetString(b, 0, 4) == "adtl")
                        {
                            long q = body + 4, end = Math.Min(length, body + size);
                            while (q + 8 <= end)
                            {
                                s.Position = q;
                                if (ReadFull(s, b, 8) < 8) break;
                                long ssz = BitConverter.ToUInt32(b, 4);
                                if (Encoding.ASCII.GetString(b, 0, 4) == "labl") w.labels++;
                                q += 8 + ssz + (ssz & 1);
                            }
                        }
                    }
                    if (size == 0xFFFFFFFFL) { broken = true; break; }
                    long next = body + size + (size & 1);
                    if (next > length && !(next == length + 1 && body + size == length)) { broken = true; p = next; break; }
                    p = Math.Min(next, length);
                }
                w.walkEnd = p;
                w.walkOk = !broken && p == length;
                if (!w.fmt || !w.data) w.error = "missing " + (!w.fmt ? "fmt " : "") + (!w.data ? "data" : "") + " chunk";
                int align = w.blockAlign > 0 ? w.blockAlign : Math.Max(1, w.bits / 8) * Math.Max(1, w.channels);
                w.seconds = w.rate > 0 ? w.dataSize / (double)align / w.rate : 0.0;
                w.ok = w.riffOk && w.dataOk && w.walkOk && w.fmt && w.data;
            }
            catch (Exception e) { w.error = e.GetType().Name + ": " + e.Message; }
            return w;
        }

        static int ReadFull(Stream s, byte[] b, int n)
        {
            int got = 0;
            while (got < n)
            {
                int k = s.Read(b, got, n - got);
                if (k <= 0) break;
                got += k;
            }
            return got;
        }
    }
}
