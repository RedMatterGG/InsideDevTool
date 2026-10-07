using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace InsideDev
{
    // Audio phase N: controlled runtime audio modification.
    //
    // All changes happen in the managed sound-engine wrapper (AudioSpy, which already sits between the game and
    // Wwise); the native hooks in version.dll stay observation-only. Every rule is a History property
    // (ChangeRecorder.PropKind.Audio), so it is undoable, shows in History, and records into mods as
    // property "audio" with target "(audio)".
    //
    //   mute:<event>      = "true"        the game still posts the event (its callbacks, e.g. EndOfEvent, still
    //                                     arrive), then it is stopped at once - game logic waiting for it continues
    //   nuke:<event>      = "true"        the post is blocked: Wwise never sees it, the game gets 0 (failed post), no
    //                                     callbacks at all (EndOfEvent, markers, music beats) - logic waiting on it waits
    //   replace:<event>   = "<event2>"    event2 is posted instead, on the same emitter with the same callbacks
    //   switch:<group>    = "<value>"     every SetSwitch of that group uses this value (applies to the next set)
    //   state:<group>     = "<state>"     every SetState of that group uses this state (applied immediately too)
    //   rtpc:<name>       = "<float>"     every SetRTPCValue of that RTPC uses this value (global applied at once)
    // Removing a rule restores the last value the game itself set (states / global RTPCs).
    public static class AudioRules
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly Dictionary<string, string> rules = new Dictionary<string, string>();
        // fast lookups by Wwise id, used from the sound-engine wrapper on every call
        static readonly HashSet<uint> mute = new HashSet<uint>(), nuke = new HashSet<uint>();
        static readonly Dictionary<uint, KeyValuePair<uint, string>> replace = new Dictionary<uint, KeyValuePair<uint, string>>();
        static readonly Dictionary<uint, uint> forceSwitch = new Dictionary<uint, uint>(), forceState = new Dictionary<uint, uint>();
        static readonly Dictionary<uint, float> forceRtpc = new Dictionary<uint, float>();
        // last values the game set (to restore when a rule is removed)
        static readonly Dictionary<uint, uint> gameState = new Dictionary<uint, uint>();
        static readonly Dictionary<uint, float> gameRtpc = new Dictionary<uint, float>();
        public static int muted, nuked, replaced, forced;       // how often a rule acted
        public static int Version;
        public static int Count { get { return rules.Count; } }
        public static IEnumerable<KeyValuePair<string, string>> All { get { return rules; } }

        static GameObject host;
        // History properties need a live GameObject; audio rules are global, so they hang off a hidden host
        public static GameObject Host
        {
            get
            {
                if (host == null)
                {
                    host = new GameObject("InsideDev_AudioRules");
                    host.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.Object.DontDestroyOnLoad(host);
                }
                return host;
            }
        }

        public static string Get(string key) { string v; return rules.TryGetValue(key, out v) ? v : null; }

        // recorded change (History): key + value (null removes the rule)
        public static string Change(string key, string value, string source)
        {
            string err = Check(key, value);
            if (err != null) return err;
            ChangeRecorder.Before(ChangeRecorder.Prop.Make(Host, null, ChangeRecorder.PropKind.Audio, key), source, "audio " + key + (value != null ? " = " + value : " (removed)"));
            Set(key, value);
            return value != null ? "audio rule " + key + " = " + value : "audio rule " + key + " removed";
        }

        public static string Check(string key, string value)
        {
            int c = key.IndexOf(':');
            if (c <= 0 || c == key.Length - 1) return "rule key must be kind:name (mute / nuke / replace / switch / state / rtpc)";
            string kind = key.Substring(0, c);
            if (kind != "mute" && kind != "nuke" && kind != "replace" && kind != "switch" && kind != "state" && kind != "rtpc") return "unknown rule kind '" + kind + "'";
            if (value == null) return null;
            float f;
            if (kind == "rtpc" && !float.TryParse(value, NumberStyles.Float, IC, out f)) return "rtpc value must be a number";
            if (kind == "replace" && value.Trim().Length == 0) return "replace needs an event name";
            return null;
        }

        // raw write (History undo / redo, mods): no recording
        public static void Set(string key, string value)
        {
            if (value == null) rules.Remove(key); else rules[key] = value;
            int c = key.IndexOf(':');
            string kind = key.Substring(0, c), name = key.Substring(c + 1);
            uint id = Id(name);
            switch (kind)
            {
                case "mute": if (value != null && value != "false") mute.Add(id); else mute.Remove(id); break;
                case "nuke": if (value != null && value != "false") nuke.Add(id); else nuke.Remove(id); break;
                case "replace": if (value != null) replace[id] = new KeyValuePair<uint, string>(Id(value), value); else replace.Remove(id); break;
                case "switch": if (value != null) forceSwitch[id] = Id(value); else forceSwitch.Remove(id); break;
                case "state":
                    if (value != null) { forceState[id] = Id(value); Apply(() => SoundEngineInner.SetStateID(id, Id(value))); }
                    else { forceState.Remove(id); uint g; if (gameState.TryGetValue(id, out g)) Apply(() => SoundEngineInner.SetStateID(id, g)); }
                    break;
                case "rtpc":
                    if (value != null) { float v = float.Parse(value, NumberStyles.Float, IC); forceRtpc[id] = v; Apply(() => SoundEngineInner.SetRTPCValueID(id, v, null, 0f)); }
                    else { forceRtpc.Remove(id); float g; if (gameRtpc.TryGetValue(id, out g)) Apply(() => SoundEngineInner.SetRTPCValueID(id, g, null, 0f)); }
                    break;
            }
            Version++;
            SyncNative();
            DevLog.Write("[audio rules] " + key + (value != null ? " = " + value : " removed") + "  (" + rules.Count + " active)");
        }

        public static void ClearAll(string source)
        {
            if (rules.Count == 0) return;
            ChangeRecorder.Begin("audio rules cleared (" + rules.Count + ")", source);
            try { foreach (var k in new List<string>(rules.Keys)) Change(k, null, source); }
            finally { ChangeRecorder.End(); }
        }

        // ---------------------------------------------------------------- native enforcement (opt-in, version.dll v3)
        // The same rules mirrored into version.dll so they also apply to posts / sets that reach AkSoundEngine without
        // passing the managed wrapper (engine start-up, direct AkSoundEngine calls). Off by default; the managed rules
        // keep working either way. Applying a rule twice is harmless (replace keys the original id; forced values
        // are idempotent; a muted playing id stopped twice stays stopped).
        public static bool nativeEnforce;
        public static string SetNative(bool on)
        {
            if (!WwiseNative.RulesSupported) return "native enforcement needs the new version.dll (loader v3) - replace it and restart the game";
            nativeEnforce = on; SyncNative();
            EditorState.Set("audio.native", on);   // remembered: re-enabled at the next start before the first posts
            DevLog.Write("[audio rules] native enforcement " + (on ? "ON" : "off"));
            return "native enforcement " + (on ? "ON: rules also apply at the AkSoundEngine boundary" : "off (managed rules only)");
        }
        // called every frame from DevRoot: restores the remembered native setting as soon as the tracer is connected
        // (the loader resolves AkSoundEngine ~3 s after boot, so rules from enabled mods are in place for start-up sounds)
        static bool bootDone;
        public static void BootTick()
        {
            if (bootDone || !WwiseNative.Available) return;
            bootDone = true;
            if (EditorState.Get("audio.native", "0") == "1" && WwiseNative.RulesSupported) { nativeEnforce = true; SyncNative(); DevLog.Write("[audio rules] native enforcement ON (remembered)"); }
        }

        static void SyncNative()
        {
            if (!WwiseNative.RulesSupported) return;
            WwiseNative.RuleEnforce(false);
            WwiseNative.RuleClear();
            if (!nativeEnforce) return;
            foreach (var kv in rules)
            {
                int c = kv.Key.IndexOf(':'); string kind = kv.Key.Substring(0, c), name = kv.Key.Substring(c + 1); uint id = Id(name);
                switch (kind)
                {
                    case "nuke": WwiseNative.RuleAdd(1, id, 1); break;
                    case "mute": WwiseNative.RuleAdd(2, id, 1); break;
                    case "replace": WwiseNative.RuleAdd(3, id, Id(kv.Value)); break;
                    case "switch": WwiseNative.RuleAdd(4, id, Id(kv.Value)); break;
                    case "state": WwiseNative.RuleAdd(5, id, Id(kv.Value)); break;
                    case "rtpc": WwiseNative.RuleAdd(6, id, BitConverter.ToUInt32(BitConverter.GetBytes(float.Parse(kv.Value, NumberStyles.Float, IC)), 0)); break;
                }
            }
            WwiseNative.RuleEnforce(true);
        }
        public static string NativeLine()
        {
            if (!WwiseNative.RulesSupported) return "native enforcement: unavailable (loader v" + WwiseNative.NativeVersion + ")";
            var v = WwiseNative.RuleStats();
            return "native enforcement " + (v[0] != 0 ? "ON" : "off") + ": " + v[1] + " rules, acted: nuked " + v[2] + ", muted " + v[3] + ", replaced " + v[4] + ", forced " + v[5];
        }

        static uint Id(string name) { uint id; return uint.TryParse(name.TrimStart('#'), out id) && name.Length > 5 ? id : AudioTrace.Hash(name); }
        static void Apply(Action a) { try { if (SoundEngineInner.Ready) a(); } catch (Exception e) { DevLog.Write("[audio rules] apply failed: " + e.Message); } }

        // ---------------------------------------------------------------- called by the wrapper (AudioSpy)
        public static bool Active { get { return rules.Count > 0; } }
        public static bool IsMuted(uint ev) { return mute.Count > 0 && mute.Contains(ev); }
        public static bool IsNuked(uint ev) { return nuke.Count > 0 && nuke.Contains(ev); }
        public static bool Replacement(uint ev, out uint id, out string name)
        {
            KeyValuePair<uint, string> r;
            if (replace.Count > 0 && replace.TryGetValue(ev, out r)) { id = r.Key; name = r.Value; return true; }
            id = 0; name = null; return false;
        }
        public static uint Switch(uint group, uint value) { uint v; if (forceSwitch.Count > 0 && forceSwitch.TryGetValue(group, out v)) { forced++; return v; } return value; }
        public static uint State(uint group, uint value) { gameState[group] = value; uint v; if (forceState.Count > 0 && forceState.TryGetValue(group, out v)) { forced++; return v; } return value; }
        public static float Rtpc(uint id, float value, bool global) { if (global) gameRtpc[id] = value; float v; if (forceRtpc.Count > 0 && forceRtpc.TryGetValue(id, out v)) { forced++; return v; } return value; }

        public static string Dump()
        {
            var sb = new System.Text.StringBuilder("AUDIO RULES " + rules.Count + "   (acted: muted " + muted + ", nuked " + nuked + ", replaced " + replaced + ", forced " + forced + ")\n");
            // coverage: posts the managed wrapper saw (rules apply to these) vs posts that reached Wwise natively
            sb.Append("  coverage: ").Append(AudioSpy.totalPosts).Append(" posts through the game's SoundEngine layer (rules apply), ").Append(AudioTrace.posts).Append(" posts reached AkSoundEngine natively").Append(AudioTrace.posts > AudioSpy.totalPosts - nuked ? "  -> " + (AudioTrace.posts - (AudioSpy.totalPosts - nuked)) + " came from other paths (not covered by rules)" : "").Append('\n');
            foreach (var kv in rules) sb.Append("  ").Append(kv.Key).Append(" = ").Append(kv.Value).Append('\n');
            sb.Append("  ").Append(NativeLine()).Append('\n');
            if (rules.Count == 0) sb.Append("  none.  audio mute <event> | nuke <event> | replace <event> <with> | force switch|state|rtpc <name> <value> | clear <key|all>\n");
            return sb.ToString();
        }
    }

    // direct access to the real (wrapped) sound engine, for applying forced states / RTPCs outside a game call
    public static class SoundEngineInner
    {
        public static bool Ready { get { return AudioSpy.Instance != null; } }
        public static void SetStateID(uint g, uint s) { AudioSpy.Instance.InnerSetState(g, s); }
        public static void SetRTPCValueID(uint id, float v, AkGameObj go, float t) { AudioSpy.Instance.InnerSetRtpc(id, v, go, t); }
    }
}
