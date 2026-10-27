// ReplayLab L5: Wwise output capture stages S1 / S3 / S3b / S2 with the headphone gates of REPLAY_DESIGN 6.0
// (InsideMuter heartbeat, rtpc_master_volume forced 0 outside the capture window, Suspend without wake, frame watchdog).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace InsideDev.ReplayLab
{
    public static class LabAudio
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        const string RtpcKey = "rtpc:rtpc_master_volume";
        public static string heartbeatPath = "_mod/muter/heartbeat.json";
        static double maxAge = 2.0;
        static bool windowOpen, capturing, timerSet, timerApplied, requireMuted, requireQuiet;
        public static bool suspendedByLab;
        static float lastCheck;
        public static string emergency;

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        public static void Register(List<LabTest> t)
        {
            t.Add(new LabTest
            {
                id = "L5", title = "Wwise output capture (S1 silent, S3 suspended + level, S3b with timeBeginPeriod(1), S2 muter only)",
                usage = "lab l5 s1|s3|s3b|s2 [secs=N] [level=100] [hb=<heartbeat.json>] [hbage=2] [force] [confirm] [wake]",
                prep = "InsideMuter running and writing its heartbeat, INSIDE session muted, `audio force rtpc rtpc_master_volume 0`; order S1, S3, then S2 only after S3 failed (needs confirm, and wake if S3 suspended Wwise in this session)",
                body = L5,
                name = c => { var s = Stage(c); return s == null ? null : "L5_" + s.ToUpperInvariant(); }
            });
        }

        static string Stage(LabContext c)
        {
            string s = c.Arg("stage", c.positional.Count > 0 ? c.positional[0] : null);
            if (s == null) return null;
            s = s.ToLowerInvariant();
            return s == "s1" || s == "s2" || s == "s3" || s == "s3b" ? s : null;
        }

        // ---------------------------------------------------------------- heartbeat

        // Session meters are pre-mute (L5 S2: a muted session read peak 0.0139), so a muted session with signal is judged by the
        // peak meter of the endpoint it plays on (the mixed device output after session mutes), read from the same heartbeat:
        // signalSessions = muted sessions with peak > 0, signalEndpointPeak = the highest endpoint peak among their endpoints
        // (NaN when an endpoint or its meter is missing).
        public sealed class Heartbeat
        {
            public bool ok; public string error; public double age, endpointPeak = double.NaN, signalEndpointPeak = double.NaN;
            public int sessions, muted, unmuted, active, signalSessions; public double maxPeak, mutedSignalPeak;
            public bool signalEndpointUnknown;
        }

        static double Num(Json.Obj o, params string[] keys)
        {
            foreach (var k in keys) { var v = o[k]; if (v is double) return (double)v; if (v is bool) return (bool)v ? 1 : 0; }
            return double.NaN;
        }

        public static Heartbeat Read(string path, double ageLimit)
        {
            var h = new Heartbeat();
            try
            {
                if (!Path.IsPathRooted(path)) path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Extensions.Dir), path));
                var fi = new FileInfo(path);
                if (!fi.Exists) { h.error = "no heartbeat file " + path; return h; }
                h.age = (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalSeconds;
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                var o = Json.Parse(text) as Json.Obj;
                if (o == null) { h.error = "heartbeat is not a JSON object"; return h; }
                List<object> list = null;
                foreach (var k in new[] { "sessions", "inside", "insideSessions" }) { list = o[k] as List<object>; if (list != null) break; }
                if (list == null) { h.error = "heartbeat has no sessions list"; return h; }
                h.endpointPeak = Num(o, "endpointPeak", "endpoint_peak", "headphonePeak");
                var epPeak = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                var eps = o["endpoints"] as List<object>;
                if (eps != null)
                    foreach (var x in eps)
                    {
                        var e = x as Json.Obj;
                        if (e == null) continue;
                        double ep = Num(e, "peak");
                        if (!double.IsNaN(ep) && (double.IsNaN(h.endpointPeak) || ep > h.endpointPeak)) h.endpointPeak = ep;
                        var id = e["id"] as string;
                        if (!string.IsNullOrEmpty(id)) epPeak[id] = ep;
                    }
                foreach (var x in list)
                {
                    var s = x as Json.Obj;
                    if (s == null) continue;
                    h.sessions++;
                    var mv = s["muted"] ?? s["mute"];
                    if (!(mv is bool)) { h.error = "session without a muted flag"; return h; }
                    if ((bool)mv) h.muted++; else h.unmuted++;
                    var st = s["state"] as string;
                    var av = s["active"];
                    bool act = st != null ? st.Equals("active", StringComparison.OrdinalIgnoreCase) : av is bool && (bool)av;
                    if (act) h.active++;
                    double pk = Num(s, "peak", "sessionPeak", "session_peak");
                    if (!double.IsNaN(pk) && pk > h.maxPeak) h.maxPeak = pk;
                    if ((bool)mv && !double.IsNaN(pk) && pk > 0)
                    {
                        h.signalSessions++;
                        if (pk > h.mutedSignalPeak) h.mutedSignalPeak = pk;
                        var sid = s["endpoint"] as string;
                        double ep;
                        if (sid == null || !epPeak.TryGetValue(sid, out ep) || double.IsNaN(ep) || ep < 0) h.signalEndpointUnknown = true;
                        else if (double.IsNaN(h.signalEndpointPeak) || ep > h.signalEndpointPeak) h.signalEndpointPeak = ep;
                    }
                }
                if (h.age > ageLimit) { h.error = "heartbeat is " + h.age.ToString("0.0", IC) + " s old"; return h; }
                h.ok = true;
            }
            catch (Exception e) { h.error = "heartbeat: " + e.GetType().Name + ": " + e.Message; }
            return h;
        }

        static string Text(Heartbeat h)
        {
            return (h.ok ? "ok" : h.error) + ", age " + h.age.ToString("0.0", IC) + " s, sessions " + h.sessions + " (muted " + h.muted + ", unmuted " + h.unmuted + ", active " + h.active + "), peak " + h.maxPeak.ToString("0.######", IC) +
                   ", endpoint peak " + (double.IsNaN(h.endpointPeak) ? "n/a" : h.endpointPeak.ToString("0.######", IC)) +
                   (h.signalSessions > 0 ? ", muted session(s) with signal " + h.signalSessions + " (their endpoint peak " + (h.signalEndpointUnknown ? "unknown" : h.signalEndpointPeak.ToString("0.######", IC)) + ")" : "");
        }

        // ---------------------------------------------------------------- gates

        static void ForceZero()
        {
            try { AudioRules.Change(RtpcKey, "0", "lab"); AudioRules.Change(RtpcKey, "0", "lab"); } catch (Exception e) { DevLog.Write("[lab] rtpc 0 failed: " + e.Message); }
        }

        static void ForceLevel(float level)
        {
            string v = Mathf.Clamp(level, 0f, 100f).ToString("0.###", IC);
            AudioRules.Change(RtpcKey, v, "lab");
            AudioRules.Change(RtpcKey, v, "lab");
        }

        static string ReadRtpc()
        {
            try
            {
                float v; int t = (int)RTPCValue_type.RTPCValue_Global;
                var r = AkSoundEngine.GetRTPCValue("rtpc_master_volume", null, out v, ref t);
                return r + " " + v.ToString("0.###", IC) + " (type " + t + ")";
            }
            catch (Exception e) { return "error " + e.GetType().Name + ": " + e.Message; }
        }

        static void StopCapture()
        {
            if (!capturing) return;
            capturing = false;
            try { SoundEngine.StopOutputCapture(); } catch (Exception e) { DevLog.Write("[lab] StopOutputCapture: " + e.Message); }
        }

        static void EndTimer()
        {
            if (!timerSet) return;
            timerSet = false;
            try { timeEndPeriod(1); } catch { }
        }

        public static void Emergency(string why)
        {
            if (!windowOpen && !capturing) return;
            ForceZero();
            windowOpen = false;
            requireQuiet = false;
            StopCapture();
            EndTimer();
            emergency = why;
            DevLog.Write("[lab] audio emergency stop: " + why);
        }

        public static void Watchdog()
        {
            if (!windowOpen && !capturing) return;
            if (Time.realtimeSinceStartup - lastCheck < 0.2f) return;
            lastCheck = Time.realtimeSinceStartup;
            var h = Read(heartbeatPath, maxAge);
            string why = Unsafe(h);
            if (why != null) Emergency(why);
        }

        static string Unsafe(Heartbeat h)
        {
            if (!h.ok) return h.error;
            if (h.unmuted > 0) return "unmuted INSIDE session";
            if (requireMuted && h.muted == 0) return "no muted INSIDE session while Wwise is not suspended";
            if (!requireMuted && h.maxPeak > 0) return "INSIDE session meter shows output while suspended (peak " + h.maxPeak.ToString("0.######", IC) + ")";
            if (h.signalSessions > 0 && h.signalEndpointUnknown) return "muted INSIDE session shows signal (peak " + h.mutedSignalPeak.ToString("0.######", IC) + ") and its endpoint meter is missing: device not proven silent";
            if (h.signalSessions > 0 && h.signalEndpointPeak > 0) return "muted INSIDE session shows signal (peak " + h.mutedSignalPeak.ToString("0.######", IC) + ") and its endpoint meter shows output (peak " + h.signalEndpointPeak.ToString("0.######", IC) + "): device not proven silent";
            if (requireQuiet && h.active > 0) return "INSIDE render stream active while suspended";
            return null;
        }

        // ---------------------------------------------------------------- paths

        static string GameDir { get { return Path.GetDirectoryName(Application.dataPath); } }

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

        // ---------------------------------------------------------------- WAV

        sealed class Wav
        {
            public bool riff, fmt, data, riffSizeOk, dataSizeOk, cue, labl;
            public int format, channels, rate, bits, blockAlign;
            public long fileLength, riffSize, dataOffset, dataSize;
            public double duration, peakDb = -200, rmsDb = -200;
            public readonly List<string> labels = new List<string>();
            public int cuePoints;
            public string error;
        }

        static Wav ReadWav(string path)
        {
            var w = new Wav();
            try
            {
                var bytes = File.ReadAllBytes(path);
                w.fileLength = bytes.Length;
                if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE") { w.error = "not a RIFF/WAVE file"; return w; }
                w.riff = true;
                w.riffSize = BitConverter.ToUInt32(bytes, 4);
                w.riffSizeOk = w.riffSize != 0xFFFFFFFFL && w.riffSize + 8 == bytes.Length;
                long p = 12;
                while (p + 8 <= bytes.Length)
                {
                    string id = Encoding.ASCII.GetString(bytes, (int)p, 4);
                    long size = BitConverter.ToUInt32(bytes, (int)p + 4);
                    long body = p + 8;
                    if (id == "fmt " && body + 16 <= bytes.Length)
                    {
                        w.fmt = true;
                        w.format = BitConverter.ToUInt16(bytes, (int)body);
                        w.channels = BitConverter.ToUInt16(bytes, (int)body + 2);
                        w.rate = (int)BitConverter.ToUInt32(bytes, (int)body + 4);
                        w.blockAlign = BitConverter.ToUInt16(bytes, (int)body + 12);
                        w.bits = BitConverter.ToUInt16(bytes, (int)body + 14);
                        if (w.format == 0xFFFE && size >= 26 && body + 26 <= bytes.Length) w.format = BitConverter.ToUInt16(bytes, (int)body + 24);
                    }
                    else if (id == "data")
                    {
                        w.data = true;
                        w.dataOffset = body;
                        w.dataSizeOk = size != 0xFFFFFFFFL && body + size <= bytes.Length;
                        w.dataSize = w.dataSizeOk ? size : bytes.Length - body;
                        if (!w.dataSizeOk) break;
                    }
                    else if (id == "cue " && body + 4 <= bytes.Length)
                    {
                        w.cue = true;
                        w.cuePoints = (int)BitConverter.ToUInt32(bytes, (int)body);
                    }
                    else if (id == "LIST" && body + 4 <= bytes.Length && Encoding.ASCII.GetString(bytes, (int)body, 4) == "adtl")
                    {
                        long q = body + 4, end = Math.Min(bytes.Length, body + size);
                        while (q + 8 <= end)
                        {
                            string sid = Encoding.ASCII.GetString(bytes, (int)q, 4);
                            long ssz = BitConverter.ToUInt32(bytes, (int)q + 4);
                            if (sid == "labl" && ssz >= 4 && q + 8 + ssz <= end)
                            {
                                w.labl = true;
                                w.labels.Add(Encoding.ASCII.GetString(bytes, (int)q + 12, (int)ssz - 4).TrimEnd('\0'));
                            }
                            q += 8 + ssz + (ssz & 1);
                        }
                    }
                    if (size == 0xFFFFFFFFL) break;
                    p = body + size + (size & 1);
                }
                if (!w.fmt || !w.data) { w.error = "missing " + (!w.fmt ? "fmt " : "") + (!w.data ? "data" : "") + " chunk"; return w; }
                int bps = Math.Max(1, w.bits / 8);
                if (w.blockAlign <= 0) w.blockAlign = bps * Math.Max(1, w.channels);
                w.duration = w.rate > 0 ? w.dataSize / (double)w.blockAlign / w.rate : 0;
                double peak = 0, sum = 0;
                long n = 0;
                long last = w.dataOffset + w.dataSize - bps;
                for (long i = w.dataOffset; i <= last; i += bps)
                {
                    double v;
                    if (w.format == 3 && bps == 4) v = BitConverter.ToSingle(bytes, (int)i);
                    else if (bps == 2) v = BitConverter.ToInt16(bytes, (int)i) / 32768.0;
                    else if (bps == 3) v = ((bytes[i] | (bytes[i + 1] << 8) | ((sbyte)bytes[i + 2] << 16))) / 8388608.0;
                    else if (bps == 4) v = BitConverter.ToInt32(bytes, (int)i) / 2147483648.0;
                    else v = (bytes[i] - 128) / 128.0;
                    double a = Math.Abs(v);
                    if (a > peak) peak = a;
                    sum += v * v;
                    n++;
                }
                if (peak > 0) w.peakDb = 20.0 * Math.Log10(peak);
                if (n > 0 && sum > 0) w.rmsDb = 10.0 * Math.Log10(sum / n);
            }
            catch (Exception e) { w.error = e.GetType().Name + ": " + e.Message; }
            return w;
        }

        static Json.Obj WavJson(Wav w)
        {
            var o = new Json.Obj();
            if (w.error != null) o["error"] = w.error;
            o["fileBytes"] = w.fileLength; o["riffSizeOk"] = w.riffSizeOk; o["dataSizeOk"] = w.dataSizeOk;
            o["format"] = w.format; o["channels"] = w.channels; o["rate"] = w.rate; o["bits"] = w.bits;
            o["durationS"] = LabUtil.Num(w.duration); o["peakDbfs"] = LabUtil.Num(w.peakDb); o["rmsDbfs"] = LabUtil.Num(w.rmsDb);
            o["cueChunk"] = w.cue; o["cuePoints"] = w.cuePoints; o["labels"] = LabUtil.Strs(w.labels);
            return o;
        }

        // ---------------------------------------------------------------- L5

        static IEnumerator L5(LabContext c)
        {
            string stage = Stage(c);
            heartbeatPath = c.Arg("hb", heartbeatPath);
            maxAge = c.F("hbage", 2f);
            float level = c.F("level", 100f);
            bool s3 = stage == "s3" || stage == "s3b";
            float secs = c.F("secs", stage == "s1" ? 5f : s3 ? 60f : 10f);
            c.timeout = secs + 120f;
            c.result["stage"] = stage;
            c.result["heartbeatPath"] = heartbeatPath;
            emergency = null;
            if (stage == "s2")
            {
                string s3v = null;
                try { var p = Path.Combine(Lab.outRoot, "L5_S3/verdict.txt"); if (File.Exists(p)) s3v = File.ReadAllText(p).Trim(); } catch { }
                c.result["s3Verdict"] = s3v ?? "none";
                if (!(s3v != null && s3v.StartsWith("FAIL")) && !c.Has("force")) c.Prep("S2 runs only after S3 failed (L5_S3 verdict: " + (s3v ?? "none") + "); add force to override");
                if (!c.Has("confirm")) c.Prep("S2 lifts rtpc_master_volume to " + level + " with only InsideMuter guarding; add confirm");
                if (suspendedByLab)
                {
                    if (!c.Has("wake")) c.Prep("Wwise was suspended by L5 S3 in this session; add wake (rtpc forced 0, 100 ms, WakeupFromSuspend)");
                    ForceZero();
                    float tw = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - tw < 0.1f) yield return null;
                    var wr = AkSoundEngine.WakeupFromSuspend();
                    suspendedByLab = false;
                    c.result["wakeup"] = wr.ToString();
                    c.Log("WakeupFromSuspend: " + wr);
                }
            }
            var h0 = Read(heartbeatPath, maxAge);
            c.result["heartbeatStart"] = Text(h0);
            if (!h0.ok) c.Prep(h0.error + " (start InsideMuter first)");
            if (h0.unmuted > 0) { c.Fail("refused: " + h0.unmuted + " unmuted INSIDE session(s)"); yield break; }
            if (!suspendedByLab && h0.muted == 0) c.Prep("no muted INSIDE session in the heartbeat (InsideMuter must see the game's audio session)");
            var rb = new List<string>();
            rb.Add("start " + ReadRtpc());
            ForceZero();
            c.Undo("rtpc forced 0", ForceZero);
            c.Undo("capture stop", () => { windowOpen = false; requireQuiet = false; ForceZero(); StopCapture(); EndTimer(); });
            requireMuted = !suspendedByLab;
            requireQuiet = false;
            if (s3)
            {
                c.phase = "suspend";
                var sr = AkSoundEngine.Suspend(true);
                suspendedByLab = true;
                requireMuted = false;
                c.result["suspend"] = sr.ToString();
                c.Log("AkSoundEngine.Suspend(true): " + sr + " (stays suspended until game exit)");
                float tq = Time.realtimeSinceStartup, quietSince = -1f;
                while (true)
                {
                    var h = Read(heartbeatPath, maxAge);
                    if (!h.ok) { c.Fail("heartbeat lost while waiting for the suspended stream: " + h.error); yield break; }
                    if (h.unmuted > 0) { c.Fail("refused: unmuted INSIDE session while waiting"); yield break; }
                    bool quiet = h.active == 0 && h.maxPeak <= 0;
                    if (quiet) { if (quietSince < 0) quietSince = Time.realtimeSinceStartup; }
                    else quietSince = -1f;
                    if (quietSince >= 0 && Time.realtimeSinceStartup - quietSince >= 1f) break;
                    if (Time.realtimeSinceStartup - tq > 15f) { c.result["heartbeatAtTimeout"] = Text(h); c.Fail("INSIDE render stream still active 15 s after Suspend(true): the rtpc is not lifted"); yield break; }
                    float tw = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - tw < 0.2f) yield return null;
                }
                c.result["quietAfterS"] = LabUtil.Num(Time.realtimeSinceStartup - tq);
            }
            timerApplied = false;
            if (stage == "s3b") { timerSet = timeBeginPeriod(1) == 0; timerApplied = timerSet; c.result["timeBeginPeriod"] = timerSet; }
            string name = "lab_" + stage + "_" + DateTime.Now.ToString("HHmmss", IC);
            string audioDir = Path.Combine(GameDir, "_mod/audio");
            Directory.CreateDirectory(audioDir);
            string abs = Path.GetFullPath(Path.Combine(audioDir, name + ".wav"));
            string bank = SoundBankInfoManager.GetBankPath();
            string rel = Relative(bank, abs);
            c.result["bankPath"] = bank; c.result["captureArg"] = rel; c.result["expectedFile"] = abs;
            if (Path.Combine(bank, rel).Length >= 260) { c.Fail("capture path too long (" + Path.Combine(bank, rel).Length + " characters)"); yield break; }
            c.phase = "capture";
            capturing = true;
            float tStart = Time.realtimeSinceStartup;
            bool started = SoundEngine.StartOutputCapture(rel);
            c.result["startReturned"] = started;
            string found = null;
            while (Time.realtimeSinceStartup - tStart < 2f)
            {
                if (File.Exists(abs)) { found = abs; break; }
                yield return null;
            }
            if (found == null)
            {
                try
                {
                    foreach (var f in Directory.GetFiles(Path.GetDirectoryName(bank.TrimEnd('\\', '/')), name + ".wav", SearchOption.AllDirectories)) { found = f; break; }
                    if (found == null) foreach (var f in Directory.GetFiles(GameDir, name + ".wav", SearchOption.AllDirectories)) { found = f; break; }
                }
                catch { }
                StopCapture();
                c.result["foundElsewhere"] = found ?? "none";
                c.Fail("capture file did not appear at " + abs + " within 2 s" + (found != null ? " (written to " + found + ")" : ""));
                yield break;
            }
            float tAppear = Time.realtimeSinceStartup;
            c.result["appearedAfterS"] = LabUtil.Num(tAppear - tStart);
            if (stage != "s1")
            {
                var h = Read(heartbeatPath, maxAge);
                if (!h.ok || h.unmuted > 0 || (s3 && (h.active > 0 || h.maxPeak > 0))) { c.result["heartbeatBeforeWindow"] = Text(h); c.Fail("gate before the window failed: " + Text(h)); yield break; }
                requireQuiet = s3;
                windowOpen = true;
                ForceLevel(level);
                c.Log("capture window open: rtpc_master_volume " + level);
            }
            var sizesT = new List<double>(); var sizes = new List<double>();
            int markers = 0, everUnmuted = 0, everActive = 0, hbFails = 0, signalReads = 0;
            double everPeak = 0, maxHbAge = 0, maxSignalEndpointPeak = double.NaN;
            var markerResults = new List<string>();
            float nextSample = 0f, nextMarker = stage == "s1" ? secs * 0.5f : 0f;
            while (Time.realtimeSinceStartup - tStart < secs)
            {
                float t = Time.realtimeSinceStartup - tStart;
                if (emergency != null) break;
                if (t >= nextSample)
                {
                    nextSample = t + 0.25f;
                    try { var fi = new FileInfo(found); fi.Refresh(); sizesT.Add(t); sizes.Add(fi.Length); } catch { }
                    var h = Read(heartbeatPath, maxAge);
                    if (!h.ok) hbFails++;
                    maxHbAge = Math.Max(maxHbAge, h.age);
                    if (h.unmuted > 0) everUnmuted++;
                    if (h.active > 0) everActive++;
                    if (h.maxPeak > everPeak) everPeak = h.maxPeak;
                    if (h.signalSessions > 0)
                    {
                        signalReads++;
                        double ep = h.signalEndpointUnknown ? double.PositiveInfinity : h.signalEndpointPeak;
                        if (double.IsNaN(maxSignalEndpointPeak) || ep > maxSignalEndpointPeak) maxSignalEndpointPeak = ep;
                    }
                    string why = Unsafe(h);
                    if (why != null) Emergency(why);
                }
                if (rb.Count < 3 && t >= 0.5f) rb.Add("capture +0.5 s " + ReadRtpc());
                if (t >= nextMarker && (stage != "s1" || markers == 0))
                {
                    string label = stage == "s1" ? "lab_s1" : "s" + markers;
                    var r = AkSoundEngine.AddOutputCaptureMarker(label);
                    markerResults.Add(label + "@" + t.ToString("0.00", IC) + "=" + r);
                    markers++;
                    nextMarker = stage == "s1" ? float.MaxValue : t + 5f;
                }
                yield return null;
            }
            rb.Add("before close " + ReadRtpc());
            ForceZero();
            windowOpen = false;
            requireQuiet = false;
            float tStop = Time.realtimeSinceStartup;
            StopCapture();
            EndTimer();
            c.Log("capture stopped after " + (tStop - tStart).ToString("0.00", IC) + " s");
            c.result["wallS"] = LabUtil.Num(tStop - tStart);
            c.result["wallFromAppearS"] = LabUtil.Num(tStop - tAppear);
            c.result["markers"] = LabUtil.Strs(markerResults);
            c.result["heartbeatDuring"] = "unmuted " + everUnmuted + ", active " + everActive + ", max peak " + everPeak.ToString("0.######", IC) + ", failed reads " + hbFails + ", max age " + maxHbAge.ToString("0.0", IC) + " s" +
                                          ", reads with a muted session showing signal " + signalReads + " (max endpoint peak in those " + (double.IsNaN(maxSignalEndpointPeak) ? "n/a" : double.IsInfinity(maxSignalEndpointPeak) ? "unknown" : maxSignalEndpointPeak.ToString("0.######", IC)) + ")";
            c.result["mutedSignalReads"] = signalReads;
            c.result["maxEndpointPeakWithSignal"] = double.IsNaN(maxSignalEndpointPeak) ? -1.0 : double.IsInfinity(maxSignalEndpointPeak) ? -2.0 : maxSignalEndpointPeak;
            if (emergency != null) c.result["emergency"] = emergency;
            float tr = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - tr < 0.3f) yield return null;
            rb.Add("after close +0.3 s " + ReadRtpc());
            c.result["rtpcReadback"] = LabUtil.Strs(rb);
            c.phase = "finalize";
            string dst = c.File("audio.wav");
            float tm = Time.realtimeSinceStartup;
            bool moved = false;
            string moveErr = null;
            while (Time.realtimeSinceStartup - tm < 10f)
            {
                try { File.Move(found, dst); moved = true; break; } catch (Exception e) { moveErr = e.Message; }
                float tw = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - tw < 0.1f) yield return null;
            }
            c.result["moved"] = moved;
            if (!moved) { c.result["moveError"] = moveErr; dst = found; }
            var w = ReadWav(dst);
            c.result["wav"] = WavJson(w);
            double slope = double.NaN, dev = double.NaN;
            Linear(sizesT, sizes, out slope, out dev);
            c.result["growthBytesPerS"] = LabUtil.Num(slope);
            c.result["growthMaxDeviation"] = LabUtil.Num(dev);
            c.result["growthSamples"] = sizes.Count;
            bool hbClean = everUnmuted == 0 && hbFails == 0 && emergency == null;
            if (stage == "s1")
            {
                bool grows = sizes.Count >= 4 && slope > 0 && dev <= 0.05;
                bool fmtOk = w.fmt && w.rate > 0 && w.channels > 0;
                bool marker = w.labels.Contains("lab_s1");
                bool quiet = w.peakDb < -90;
                c.Verdict(w.error == null && grows && fmtOk && marker && quiet && hbClean,
                    "file appeared after " + LabUtil.F(tAppear - tStart, "0.00") + " s; growth " + (grows ? "linear" : "NOT linear (" + LabUtil.F(dev * 100, "0.0") + " %)") + "; fmt " + w.rate + " Hz x" + w.channels + " " + w.bits + " bit; marker " + (marker ? "found" : "missing (cue " + w.cue + ", labels " + w.labels.Count + ")") + "; peak " + LabUtil.F(w.peakDb, "0.0") + " dBFS" + (hbClean ? "" : "; heartbeat gate tripped"));
            }
            else if (s3)
            {
                double wall = tStop - tStart;
                double err = wall > 0 ? Math.Abs(w.duration - wall) / wall : double.NaN;
                bool durOk = err <= 0.005;
                bool loud = w.rmsDb > -50;
                bool silentOs = everActive == 0 && everPeak <= 0;
                bool ok = w.error == null && durOk && loud && silentOs && hbClean;
                c.result["durationError"] = LabUtil.Num(err);
                c.Verdict(ok, "WAV " + LabUtil.F(w.duration, "0.000") + " s vs wall " + LabUtil.F(wall, "0.000") + " s (" + LabUtil.F(err * 100, "0.00") + " %); RMS " + LabUtil.F(w.rmsDb, "0.0") + " dBFS; INSIDE stream during capture: active " + everActive + ", peak " + everPeak.ToString("0.######", IC) + ", unmuted " + everUnmuted + (stage == "s3b" ? "; timeBeginPeriod(1) " + timerApplied : ""));
                if (ok)
                {
                    try
                    {
                        var flag = new Json.Obj();
                        flag["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", IC); flag["stage"] = stage.ToUpperInvariant(); flag["requiresTimerPeriod"] = stage == "s3b";
                        flag["durationS"] = LabUtil.Num(w.duration); flag["wallS"] = LabUtil.Num(wall); flag["rmsDbfs"] = LabUtil.Num(w.rmsDb); flag["result"] = c.File("result.json");
                        File.WriteAllText(Path.Combine(GameDir, "_mod/acap_suspend_ok.json"), Json.Write(flag) + "\n");
                        c.result["suspendFlagWritten"] = Path.Combine(GameDir, "_mod/acap_suspend_ok.json");
                    }
                    catch (Exception e) { c.Log("flag write failed: " + e.Message); }
                }
            }
            else
            {
                bool loud = w.rmsDb > -50, noClip = w.peakDb < -0.1;
                bool deviceSilent = signalReads > 0 && maxSignalEndpointPeak <= 0;
                bool ok = w.error == null && loud && noClip && hbClean && deviceSilent;
                c.Verdict(ok, "RMS " + LabUtil.F(w.rmsDb, "0.0") + " dBFS, peak " + LabUtil.F(w.peakDb, "0.0") + " dBFS; endpoint meter " + (deviceSilent ? "0 in all " + signalReads + " reads with INSIDE signal" : signalReads == 0 ? "never checked (no read with INSIDE signal)" : "showed output") + (hbClean ? "" : "; heartbeat gate tripped"));
                if (ok)
                {
                    try
                    {
                        var flag = new Json.Obj();
                        flag["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", IC); flag["stage"] = "S2";
                        flag["rmsDbfs"] = LabUtil.Num(w.rmsDb); flag["peakDbfs"] = LabUtil.Num(w.peakDb); flag["wallS"] = LabUtil.Num(tStop - tStart);
                        flag["mutedSignalReads"] = signalReads; flag["maxEndpointPeakWithSignal"] = LabUtil.Num(maxSignalEndpointPeak); flag["result"] = c.File("result.json");
                        File.WriteAllText(Path.Combine(GameDir, "_mod/acap_muteronly_ok.json"), Json.Write(flag) + "\n");
                        c.result["muterOnlyFlagWritten"] = Path.Combine(GameDir, "_mod/acap_muteronly_ok.json");
                    }
                    catch (Exception e) { c.Log("flag write failed: " + e.Message); }
                }
            }
        }

        static void Linear(List<double> x, List<double> y, out double slope, out double dev)
        {
            slope = double.NaN; dev = double.NaN;
            int n = Math.Min(x.Count, y.Count);
            if (n < 3) return;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++) { sx += x[i]; sy += y[i]; sxx += x[i] * x[i]; sxy += x[i] * y[i]; }
            double den = n * sxx - sx * sx;
            if (Math.Abs(den) < 1e-12) return;
            slope = (n * sxy - sx * sy) / den;
            double b = (sy - slope * sx) / n;
            double range = Math.Abs(slope * (x[n - 1] - x[0]));
            if (range <= 0) { dev = double.PositiveInfinity; return; }
            double m = 0;
            for (int i = 0; i < n; i++) m = Math.Max(m, Math.Abs(y[i] - (slope * x[i] + b)));
            dev = m / range;
        }
    }
}
