using System;
using System.Collections.Generic;

namespace InsideDev
{
    // What the runtime data part (object database, scanners, monitors, mods, extension modules) tells the rest of the
    // tool about itself: its editor.cfg keys for "cfg" and the "data" rows of "doctor".
    public static class RtXData
    {
        static bool done;

        // main thread, once (called when the mods are loaded at start and again from the first mod tick)
        public static void Init()
        {
            if (done) return;
            done = true;
            try
            {
                EditorState.Describe("data", PassiveHidden.ConfigKey, "0", "1 = the passive hidden-object scanner runs all the time (hidden on / off); 0 = only while the Hidden tab or hidden scan asks");
                EditorState.Describe("data", EventMonitor.ConfigKey, "0", "1 = the event monitor (signal taps, state machine polling) is always on (events on / off); 0 = only while something reads it");
                EditorState.Describe("data", "db.rootRefresh", "60", "seconds between full root searches of the object database while it is awake (at least 10); a streaming change always triggers one");
                EditorState.Describe("data", AudioTrace.ConfigKey, "0", "1 = the Wwise trace runs all the time; 0 = only while an audio tool is in use (the native tracer only counts otherwise)");
                EditorState.Describe("data", AudioDb.ConfigKey, "0", "1 = the audio scanners (sound library, audio database, reference scan) run all the time and record every area you walk through; 0 = only while an audio tool is in use");
                EditorState.Describe("data", Mods.SettleKey, "0.25", "seconds streaming must be quiet after a level loads before its mod operations are resolved (0..30)");
                EditorState.Describe("data", "audio.native", "0", "1 = audio rules are also enforced inside version.dll (audio native on / off)");
                EditorState.Describe("data", "mem.show", "0", "1 = the Inspector shows memory addresses for Cheat Engine (mem on / off)");
                Doctor.Register("data", Check);
            }
            catch (Exception e) { DevLog.Error("data init", e); }
        }

        static List<Doctor.Row> Check()
        {
            var rows = new List<Doctor.Row>();
            // object database
            if (!ObjectDatabase.Ready)
                rows.Add(new Doctor.Row("data", "object database", Doctor.Info, "no pass yet (it scans only while the editor, a command or a client asks)"));
            else
                rows.Add(new Doctor.Row("data", "object database", Doctor.Ok, ObjectDatabase.all.Count + " objects, last pass " + Fmt.F(ObjectDatabase.Age, "0.0") + " s ago (" + Fmt.F(ObjectDatabase.lastPassMs, "0.0") + " ms)" +
                    (ObjectDatabase.Demanded ? ", in demand" : ", asleep")));
            rows.Add(new Doctor.Row("data", "background", Doctor.Info, Background.Status()));
            // monitors and scanners
            rows.Add(new Doctor.Row("data", "hidden scanner", Doctor.Info, PassiveHidden.enabled ? "on (" + PassiveHidden.ConfigKey + "=1)" + (PassiveHidden.Scanning ? ", scanning a level" : "") : PassiveHidden.Running ? "running for a while (asked for)" : "off"));
            rows.Add(new Doctor.Row("data", "event monitor", Doctor.Info,
                !EventMonitor.enabled ? "switched off for this session" : (EventMonitor.Always ? "always on (" + EventMonitor.ConfigKey + "=1)" : "on demand") + (EventMonitor.Awake ? ", awake: " + EventMonitor.TapCount + " signal taps, " + EventMonitor.FsmCount + " state machines" : ", asleep (no taps in the game)")));
            if (!WwiseNative.tried) rows.Add(new Doctor.Row("data", "audio trace", Doctor.Info, "not started yet"));
            else if (!WwiseNative.Available) rows.Add(new Doctor.Row("data", "audio trace", Doctor.Warn, "native tracer not available: " + WwiseNative.Problem, "replace version.dll with the one of this release and restart the game"));
            else rows.Add(new Doctor.Row("data", "audio trace", Doctor.Ok, (AudioTrace.Awake ? "tracing" : "asleep (native tracer counts only)") + ", " + AudioTrace.gaps + " gap(s), " + Fmt.F(AudioTrace.sleptSeconds, "0") + " s not traced"));
            rows.Add(new Doctor.Row("data", "audio scanners", Doctor.Info, (AudioDb.Always ? "always on (" + AudioDb.ConfigKey + "=1)" : AudioDb.ScanWanted ? "running (an audio tool is in use)" : "asleep") + "; " + AudioDb.status));
            // mods
            int en, bad, applied, pending, failed;
            Mods.Totals(out en, out bad, out applied, out pending, out failed);
            string modText = Mods.mods.Count + " mod(s), " + en + " enabled; operations: " + applied + " applied, " + pending + " pending, " + failed + " failed; " + Mods.resolveRounds + " resolve round(s), last " + Fmt.F(Mods.lastRoundMs, "0.###") + " ms";
            if (bad > 0)
            {
                var names = new List<string>();
                foreach (var m in Mods.mods) if (m.loadFailed || (m.problems.Count > 0 && m.enabled)) names.Add(m.name + ": " + (m.problems.Count > 0 ? m.problems[0] : "problem"));
                rows.Add(new Doctor.Row("data", "mods", Doctor.Warn, modText + "; " + bad + " not applied: " + string.Join(" | ", names.ToArray()), "mod show <name> lists what is wrong; fix the file, then mod reload"));
            }
            else rows.Add(new Doctor.Row("data", "mods", failed > 0 ? Doctor.Warn : Doctor.Ok, modText, failed > 0 ? "mod list / mod show <name> name the operations that failed" : null));
            // extension modules
            int off = Extensions.SwitchedOff, slow = Extensions.SlowWarnings;
            string extText = Extensions.loaded.Count + " loaded, " + Extensions.Asleep + " asleep (their levels are not loaded)" + (slow > 0 ? ", " + slow + " slow-tick warning(s)" : "") + (Extensions.problems.Count > 0 ? ", " + Extensions.problems.Count + " problem(s): " + Extensions.problems[0] : "");
            rows.Add(new Doctor.Row("data", "extensions", off > 0 || Extensions.problems.Count > 0 ? Doctor.Warn : Doctor.Ok, extText + (off > 0 ? "; " + off + " switched off after errors" : ""), off > 0 || Extensions.problems.Count > 0 ? "ext lists them; perf enable ext.<name> switches one back on" : null));
            return rows;
        }
    }
}
