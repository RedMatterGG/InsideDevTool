using System;
using System.Collections.Generic;
using System.Reflection;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // AudioRender.cs (owner WORLD_B, the audio / render schemas frozen): Wwise and the render path (report 03 sections 8,
    // 9). audio: SoundEngine.PostEvent / StopPlayingID / SetRTPCValue (write-only) / SetState / SetSwitch, music clock
    // GlobalAudio.instance.music. Sound policy: never raise the master volume (rtpc on masterVolume or
    // rtpc_master_volume accepts only 0, i.e. mute). render: PDPostCombined enabled, fog, DefaultLightMode,
    // PDRenderDebug renderMode, EditorMode debug flags (core G.SetFlag), Playdead's ScreenShotPositions.bin route (127
    // savepoints with camera poses). Every change registers a Restorer key render:<what> / audio:<what> / debug_flag:<f>.
    // Verified: FP/SoundEngine.cs:692-707 (PostEvent, StopPlayingID), 749 (SetRTPCValue), 772 (SetState), 781
    // (SetSwitchFast), 1053 (Name2ID); FP/AkSoundEngine.cs:1655-1661 (ResetRTPCValue); FP/AudioKeyObject.cs:7, 46
    // (global AkGameObj / GameObject); FP/GlobalAudio.cs:16 + FP/MusicManager.cs:9-60 (LastBeat, Bar, lastCueMarkerHash,
    // GetMusicPosition_s, GetLoopLength_s); FP/PDRenderDebug.cs:7-23; FP/DefaultLightMode.cs:9-128; FP/SceneRenderSettings.cs
    // (re-applies RenderSettings.fog, hence the fog tick); FP/EditorMode.cs:73-107 (EDebugFlags). Posted events are
    // stopped by restore; RTPCs other than the master volume are reset to their defaults (Wwise cannot read them back);
    // the master volume is never reset (it stays where the game or the user set it). States and switches have no undo (the game re-sends its
    // own global syncs). Resources: inside://world/camera_route (the decoded route) and inside://world/tunables.
    public static class AudioRender
    {
        public const string Owner = "WORLD_B";
        public static readonly string[] DebugViews = { "off", "Depth", "Normals", "NormalsContrast", "Lighting", "Shadows", "Transparency", "Unlit", "UnlitTransparency" };
        public const string RouteUri = "inside://world/camera_route";
        public const string TunablesUri = "inside://world/tunables";
        static readonly string[] RefusedFlags = { "TrialMode", "UseSaveGame", "SignInManually", "InputRecording", "Profiling" };

        static readonly object gate = new object();
        static List<RouteEntry> route;
        static string routeError;
        static bool fogHeld, fogWanted;
        static int fogTicks;

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("audio", "Audio", PlayShared.Dev, Owner,
                "Wwise audio: music (read-only: position, loop, beat, bar), post an event (globally or on an object), stop a playing id, set an RTPC, state or switch. Master volume RTPCs accept only 0 (mute). restore stops posted events and resets changed RTPCs (never the master volume).",
                Schema.Obj().Enum("op", "music, post, stop, rtpc, state or switch.", "music", "music", "post", "stop", "rtpc", "state", "switch")
                    .Str("event", "op post: event name.").Str("object", "op post / rtpc / switch: object id or path (default global).")
                    .Str("name", "op rtpc: RTPC name.").Num("value", "op rtpc.", -1e6, 1e6).Num("interp_s", "op rtpc: interpolation.", 0, 0, 10)
                    .Str("group", "op state / switch: group name.").Str("state", "op state / switch: value name.").Int("playing_id", "op stop.", 0, 4294967295L).Build(),
                Schema.Obj().Enum("outcome", "", null, "done", "refused").Int("playing_id", "op post.", 0, 4294967295L)
                    .OutObj("music", "Music clock.", Schema.Obj().Num("pos_s", "", -1, 1e6).Num("loop_s", "", -1, 1e6).Int("beat", "", -1, 100000).Int("bar", "", -1, 100000).Str("cue", ""))
                    .Str("note", "").Arr("warnings", "", Schema.Str(), 0, 20).BuildOutput()), HandleAudio);

            PlayRegistry.Add(PlayShared.Def("render", "Render", PlayShared.Dev, Owner,
                "Render switches: state (read-only), post (PDPostCombined on / off), fog, default_lights (neutral light rig), debug_view (PDRenderDebug modes), debug_flags (EditorMode flags, list or set one), shot_route (Playdead's 127-savepoint camera route: list, or apply entry index to the free camera). Restored by restore.",
                Schema.Obj().Enum("op", "state, post, fog, default_lights, debug_view, debug_flags or shot_route.", "state", "state", "post", "fog", "default_lights", "debug_view", "debug_flags", "shot_route")
                    .Bool("on", "op post / fog / default_lights / debug_flags.").Enum("mode", "op debug_view.", null, DebugViews)
                    .Str("flag", "op debug_flags: EDebugFlags name (omit to list).").Int("index", "op shot_route: entry to apply.", 0, 1000)
                    .Bool("apply", "op shot_route: move the free camera to the entry.", false).Int("limit", "Rows.", 40, 1, 200).Int("offset", "First row.", 0, 0, 1000).Build(),
                Schema.Obj().Enum("outcome", "", null, "done", "refused")
                    .OutObj("state", "Render state.", Schema.Obj().Bool("post", "").Bool("fog", "").Bool("default_lights", "").Enum("debug_view", "", null, DebugViews))
                    .Arr("flags", "op debug_flags.", Schema.OutOf(Schema.Obj().Str("name", "").Bool("on", "")), 0, 64)
                    .Arr("route", "op shot_route.", Schema.OutOf(Schema.Obj().Int("index", "", 0, 1000).Int("subscene", "", -1, 999).Int("savepoint", "", -1, 999)
                        .Vec("pos", "Character position.", 3, -1e5, 1e5).Vec("cam_pos", "", 3, -1e5, 1e5).Vec("cam_rot", "Euler degrees.", 3, -720, 720)), 0, 200)
                    .Int("total", "", 0, 100000).Arr("warnings", "", Schema.Str(), 0, 20).BuildOutput()).Idempotent(), HandleRender);

            PlayRegistry.Resource(new ResourceDef { uri = RouteUri, name = "camera_route", title = "Playdead screenshot route", mimeType = "application/json", profiles = McpProfiles.Dev,
                description = "Playdead's ScreenShotPositions.bin decoded: per savepoint (subscene, savepoint) the character position and camera pose of each sample frame. render {op:\"shot_route\", index, apply:true} flies the free camera there." });
            PlayRegistry.Resource(new ResourceDef { uri = TunablesUri, name = "tunables", title = "Typed tunables", mimeType = "application/json", profiles = McpProfiles.Dev,
                description = "The knobs an agent may turn (time, camera, render, air, god, audio, debug flags) with type, range, units and the tool call that sets each. Out-of-range values are refused, never clamped." });
            PlayRegistry.Reader(ReadResource);
            PlayRegistry.OnTick("render_fog", FogTick);
        }

        // ---------------------------------------------------------------- audio
        static ToolResult HandleAudio(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "music");
            var warnings = new List<string>();
            var r = PlayShared.Env();
            switch (op)
            {
                case "music":
                    ctx.MainDo(() => r["music"] = Music());
                    break;
                case "post":
                    {
                        string ev = PlayShared.Str(a, "event", null);
                        if (string.IsNullOrEmpty(ev)) return ToolResult.Fail(McpErr.BadArgs, "op post needs event");
                        string n = ReflectValues.Norm(ev);
                        if (ReflectValues.IsMasterVolumeName(ev) || n.Contains("unmute")) return Refused("events that change the master volume are not posted (sound policy)", "the game volume is left as it is");
                        string spec = PlayShared.Str(a, "object", null);
                        uint id = 0;
                        ctx.MainDo(() =>
                        {
                            GameObject go = null;
                            if (!string.IsNullOrEmpty(spec))
                            {
                                go = World.Target(spec, "object");
                                if (go.GetComponent<AkGameObj>() == null) warnings.Add("the object has no AkGameObj: Wwise posts it without that object's position");
                                id = SoundEngine.PostEvent(ev, go);
                            }
                            else
                            {
                                go = AudioKeyObject.global;
                                id = go != null ? SoundEngine.PostEvent(ev, go) : SoundEngine.PostEventByIDFast(SoundEngine.Name2ID(ev), ev, AudioKeyObject.AkGameObjs.global);
                            }
                            if (id == 0) warnings.Add("Wwise returned playing id 0: the event is unknown or its bank is not loaded");
                            else
                            {
                                uint pid = id;
                                World.Restore("audio:post:" + pid, "posted event " + ev, () => { SoundEngine.StopPlayingID(pid, 0.2f); return "stopped " + pid; });
                            }
                        });
                        r["playing_id"] = (double)id;
                        r["note"] = "posted " + ev + (string.IsNullOrEmpty(spec) ? " (global)" : " on " + spec);
                        break;
                    }
                case "stop":
                    {
                        if (!PlayShared.Has(a, "playing_id")) return ToolResult.Fail(McpErr.BadArgs, "op stop needs playing_id");
                        uint pid = (uint)PlayShared.Long(a, "playing_id", 0);
                        ctx.MainDo(() => { SoundEngine.StopPlayingID(pid, 0.2f); World.Forget("audio:post:" + pid); });
                        r["playing_id"] = (double)pid;
                        r["note"] = "stopped " + pid;
                        break;
                    }
                case "rtpc":
                    {
                        string name = PlayShared.Str(a, "name", null);
                        if (string.IsNullOrEmpty(name) || !PlayShared.Has(a, "value")) return ToolResult.Fail(McpErr.BadArgs, "op rtpc needs name and value");
                        float value = (float)PlayShared.Num(a, "value", 0);
                        bool master = ReflectValues.IsMasterVolumeName(name);
                        if (master && value != 0f) return Refused("the master volume accepts only 0 (mute): sound policy", "audio {op:\"rtpc\", name:\"" + name + "\", value:0}");
                        float interp = (float)PlayShared.Num(a, "interp_s", 0);
                        string spec = PlayShared.Str(a, "object", null);
                        ctx.MainDo(() =>
                        {
                            GameObject go = string.IsNullOrEmpty(spec) ? null : World.Target(spec, "object");
                            SoundEngine.SetRTPCValue(name, value, go, interp);
                            if (master) { warnings.Add("master volume set to 0; restore keeps it muted"); return; }
                            var g = go;
                            World.Restore("audio:rtpc:" + name + (go != null ? "@" + go.GetInstanceID() : ""), "RTPC " + name, () =>
                            {
                                if (g != null) AkSoundEngine.ResetRTPCValue(name, g); else AkSoundEngine.ResetRTPCValue(name);
                                return "reset to the Wwise default";
                            });
                        });
                        r["note"] = "RTPC " + name + " = " + PlayShared.F(value) + " (write-only: Wwise cannot read it back)";
                        break;
                    }
                case "state":
                    {
                        string group = PlayShared.Str(a, "group", null), state = PlayShared.Str(a, "state", null);
                        if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(state)) return ToolResult.Fail(McpErr.BadArgs, "op state needs group and state");
                        if (ReflectValues.IsMasterVolumeName(group)) return Refused("master volume groups are not touched (sound policy)", null);
                        ctx.MainDo(() => SoundEngine.SetState(SoundEngine.Name2ID(group), group, SoundEngine.Name2ID(state), state));
                        r["note"] = "state " + group + " = " + state;
                        warnings.Add("no undo: the game re-sends its global game syncs, so a state it owns can be set back at any time");
                        break;
                    }
                case "switch":
                    {
                        string group = PlayShared.Str(a, "group", null), state = PlayShared.Str(a, "state", null);
                        if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(state)) return ToolResult.Fail(McpErr.BadArgs, "op switch needs group and state (the switch value)");
                        string spec = PlayShared.Str(a, "object", null);
                        ctx.MainDo(() =>
                        {
                            AkGameObj ak = AudioKeyObject.AkGameObjs.global;
                            if (!string.IsNullOrEmpty(spec))
                            {
                                var go = World.Target(spec, "object");
                                ak = go.GetComponent<AkGameObj>();
                                if (ak == null) throw new McpException(McpErr.NotFound, World.Path(go) + " has no AkGameObj", "switches live on Wwise game objects; omit object for the global one");
                            }
                            SoundEngine.SetSwitchFast(SoundEngine.Name2ID(group), group, SoundEngine.Name2ID(state), state, ak);
                        });
                        r["note"] = "switch " + group + " = " + state + (string.IsNullOrEmpty(spec) ? " (global)" : " on " + spec);
                        warnings.Add("no undo: switches are set by the game's own logic again");
                        break;
                    }
                default: return ToolResult.Fail(McpErr.BadArgs, "unknown op " + op);
            }
            r["outcome"] = "done";
            if (warnings.Count > 0) r["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(r);
        }

        static ToolResult Refused(string why, string hint)
        {
            var f = ToolResult.Fail(McpErr.Refused, why, hint);
            f.structured["outcome"] = "refused";
            return f;
        }

        static Json.Obj Music()
        {
            var o = new Json.Obj();
            var ga = PersistentBehaviour<GlobalAudio>.instance;
            var m = ga != null ? ga.music : null;
            if (m == null) { o["pos_s"] = -1.0; o["loop_s"] = -1.0; o["beat"] = -1.0; o["bar"] = -1.0; o["cue"] = "no music manager"; return o; }
            o["pos_s"] = PlayShared.R(m.GetMusicPosition_s(), 3);
            o["loop_s"] = PlayShared.R(m.GetLoopLength_s(), 3);
            o["beat"] = (double)m.LastBeat;
            o["bar"] = (double)m.Bar;
            o["cue"] = m.lastCueMarkerHash == 0 ? "" : "0x" + m.lastCueMarkerHash.ToString("x8");
            return o;
        }

        // ---------------------------------------------------------------- render
        static ToolResult HandleRender(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "state");
            var warnings = new List<string>();
            var r = PlayShared.Env();
            switch (op)
            {
                case "state": break;
                case "post":
                    {
                        if (!PlayShared.Has(a, "on")) return ToolResult.Fail(McpErr.BadArgs, "op post needs on");
                        bool on = PlayShared.Bool(a, "on", true);
                        ctx.MainDo(() =>
                        {
                            var post = Cam<PDPostCombined>("PDPostCombined");
                            bool before = post.enabled;
                            if (before != on) World.Restore("render:post", "post effects (PDPostCombined)", () => { if (post != null) post.enabled = before; return before ? "post on" : "post off"; });
                            post.enabled = on;
                        });
                        break;
                    }
                case "fog":
                    {
                        if (!PlayShared.Has(a, "on")) return ToolResult.Fail(McpErr.BadArgs, "op fog needs on");
                        bool on = PlayShared.Bool(a, "on", true);
                        ctx.MainDo(() => Fog(on, warnings));
                        warnings.Add("INFERRED: most of INSIDE's fog is shader-side (FogAmbientBlendBox, fog planes); this switches Unity fog and the volumetric light fog");
                        break;
                    }
                case "default_lights":
                    {
                        if (!PlayShared.Has(a, "on")) return ToolResult.Fail(McpErr.BadArgs, "op default_lights needs on");
                        bool on = PlayShared.Bool(a, "on", true);
                        ctx.MainDo(() =>
                        {
                            var dlm = UnityEngine.Object.FindObjectOfType(typeof(DefaultLightMode)) as DefaultLightMode;
                            if (dlm == null || dlm.defaultLight_prefab == null) throw new McpException(McpErr.NotReady, "no DefaultLightMode with a light prefab in the loaded scenes", "it lives on the master scene's _Globals; wait until the game is ready");
                            var prefab = dlm.defaultLight_prefab;
                            bool before = DefaultLightsOn();
                            if (before != on) World.Restore("render:default_lights", "default light rig", () => { DefaultLightMode.SetDefaultLighting(before, prefab); return before ? "default lights on" : "default lights off"; });
                            DefaultLightMode.SetDefaultLighting(on, prefab);
                        });
                        break;
                    }
                case "debug_view":
                    {
                        string mode = PlayShared.Str(a, "mode", null);
                        if (string.IsNullOrEmpty(mode)) return ToolResult.Fail(McpErr.BadArgs, "op debug_view needs mode", string.Join(", ", DebugViews));
                        ctx.MainDo(() =>
                        {
                            var dbg = Cam<PDRenderDebug>("PDRenderDebug");
                            bool en = dbg.enabled;
                            var rm = dbg.renderMode;
                            World.Restore("render:debug_view", "render debug view (PDRenderDebug)", () => { if (dbg != null) { dbg.renderMode = rm; dbg.enabled = en; } return en ? "debug view " + rm : "debug view off"; });
                            if (mode == "off") dbg.enabled = false;
                            else
                            {
                                dbg.renderMode = (PDRenderDebug.RenderModeEnum)Enum.Parse(typeof(PDRenderDebug.RenderModeEnum), mode);
                                dbg.enabled = true;
                                if (!dbg.fog) warnings.Add("PDRenderDebug hides fog while on (its fog flag is off)");
                            }
                        });
                        warnings.Add("INFERRED: the debug shaders are referenced in the data but were not seen running");
                        break;
                    }
                case "debug_flags": return DebugFlags(a, ctx);
                case "shot_route": return ShotRouteOp(a, ctx);
                default: return ToolResult.Fail(McpErr.BadArgs, "unknown op " + op);
            }
            ctx.MainDo(() => r["state"] = RenderState());
            r["outcome"] = "done";
            if (warnings.Count > 0) r["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(r);
        }

        static T Cam<T>(string what) where T : Component
        {
            var cam = G.Cam();
            if (cam == null) throw new McpException(McpErr.NotReady, "no gameplay camera", "wait until the game is ready");
            var c = cam.GetComponent<T>();
            if (c == null) throw new McpException(McpErr.NotFound, what + " is not on the gameplay camera " + cam.name);
            return c;
        }

        static bool DefaultLightsOn()
        {
            var f = typeof(DefaultLightMode).GetField("cached_deflight", BindingFlags.Static | BindingFlags.NonPublic);
            var go = f != null ? f.GetValue(null) as GameObject : null;
            return go != null && go.activeInHierarchy;
        }

        static void Fog(bool on, List<string> warnings)
        {
            var cam = G.Cam();
            var lv = cam != null ? cam.GetComponent<LightVolumeFog_camera>() : null;
            bool before = RenderSettings.fog;
            bool lvBefore = lv != null && lv.enabled;
            lock (gate)
            {
                if (!fogHeld)
                {
                    var l = lv;
                    World.Restore("render:fog", "fog (Unity fog, volumetric light fog)", () =>
                    {
                        lock (gate) fogHeld = false;
                        RenderSettings.fog = before;
                        if (l != null) l.enabled = lvBefore;
                        return before ? "fog on" : "fog off";
                    });
                    fogHeld = true;
                }
                fogWanted = on;
            }
            RenderSettings.fog = on;
            if (lv != null) lv.enabled = on;
            else warnings.Add("no LightVolumeFog_camera on the camera: only Unity fog switched");
        }

        static void FogTick()
        {
            if (!fogHeld || ++fogTicks % 15 != 0) return;
            if (RenderSettings.fog != fogWanted) RenderSettings.fog = fogWanted;
        }

        static Json.Obj RenderState()
        {
            var o = new Json.Obj();
            var cam = G.Cam();
            var post = cam != null ? cam.GetComponent<PDPostCombined>() : null;
            var dbg = cam != null ? cam.GetComponent<PDRenderDebug>() : null;
            var lv = cam != null ? cam.GetComponent<LightVolumeFog_camera>() : null;
            o["post"] = post != null && post.enabled;
            o["fog"] = RenderSettings.fog || (lv != null && lv.enabled);
            o["default_lights"] = DefaultLightsOn();
            o["debug_view"] = dbg != null && dbg.enabled && dbg.renderMode != PDRenderDebug.RenderModeEnum.None && dbg.renderMode != PDRenderDebug.RenderModeEnum.NUM_RENDERMODES ? dbg.renderMode.ToString() : "off";
            return o;
        }

        // ---------------------------------------------------------------- debug flags
        static ToolResult DebugFlags(Json.Obj a, CallContext ctx)
        {
            string flag = PlayShared.Str(a, "flag", null);
            var warnings = new List<string>();
            var r = PlayShared.Env();
            if (!string.IsNullOrEmpty(flag))
            {
                EditorMode.EDebugFlags f;
                if (!TryFlag(flag, out f)) return ToolResult.Fail(McpErr.BadArgs, "no debug flag '" + flag + "'", "render {op:\"debug_flags\"} lists them");
                if (Array.IndexOf(RefusedFlags, f.ToString()) >= 0) return Refused(f + " changes saves, sign-in, trial mode or writes files", "the other flags are free to switch");
                if (PlayShared.Has(a, "on"))
                {
                    bool on = PlayShared.Bool(a, "on", true);
                    ctx.MainDo(() =>
                    {
                        bool before = G.GetFlag(f);
                        if (before == on) return;
                        string key = "debug_flag:" + f;
                        World.Restore(key, "EditorMode debug flag " + f, () => { G.SetFlag(f, before); return f + " " + (before ? "on" : "off"); });
                        G.SetFlag(f, on);
                    });
                    if (f == EditorMode.EDebugFlags.InvincibleBoy) warnings.Add("InvincibleBoy is god mode (hazard {op:\"god\"} does the same)");
                }
            }
            var flags = new List<object>();
            ctx.MainDo(() =>
            {
                foreach (EditorMode.EDebugFlags f in Enum.GetValues(typeof(EditorMode.EDebugFlags)))
                {
                    if (f == EditorMode.EDebugFlags.None || flags.Count >= 64) continue;
                    var o = new Json.Obj();
                    o["name"] = f.ToString();
                    o["on"] = G.GetFlag(f);
                    flags.Add(o);
                }
            });
            r["outcome"] = "done";
            r["flags"] = flags;
            warnings.Add("most flags only gate editor drawing; InvincibleBoy, SkipBlend, DoubleAIUpdate, DisableRumble have runtime effects");
            r["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(r);
        }

        static bool TryFlag(string name, out EditorMode.EDebugFlags f)
        {
            f = EditorMode.EDebugFlags.None;
            foreach (var n in Enum.GetNames(typeof(EditorMode.EDebugFlags)))
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { f = (EditorMode.EDebugFlags)Enum.Parse(typeof(EditorMode.EDebugFlags), n); return f != EditorMode.EDebugFlags.None; }
            return false;
        }

        // ---------------------------------------------------------------- shot route
        static List<RouteEntry> Route(CallContext ctx)
        {
            lock (gate) { if (route != null) return route; }
            string dir = ctx.Main(() => Application.streamingAssetsPath);
            string path = System.IO.Path.Combine(dir, ShotRoute.File);
            byte[] b;
            try { b = System.IO.File.ReadAllBytes(path); }
            catch (Exception e) { throw new McpException(McpErr.NotFound, "cannot read " + path + ": " + e.Message, "the route file ships with the game in StreamingAssets/ScreenshotRunner"); }
            int version;
            string err;
            var l = ShotRoute.Decode(b, out version, out err);
            lock (gate) { route = l; routeError = err; }
            return l;
        }

        static ToolResult ShotRouteOp(Json.Obj a, CallContext ctx)
        {
            var l = Route(ctx);
            var warnings = new List<string>();
            if (routeError != null) warnings.Add("route file: " + routeError);
            var r = PlayShared.Env();
            int offset = PlayShared.Int(a, "offset", 0), limit = PlayShared.Int(a, "limit", 40);
            bool apply = PlayShared.Bool(a, "apply", false);
            var rows = new List<object>();
            if (apply)
            {
                if (!PlayShared.Has(a, "index")) return ToolResult.Fail(McpErr.BadArgs, "apply needs index");
                int index = PlayShared.Int(a, "index", 0);
                if (index < 0 || index >= l.Count) return ToolResult.Fail(McpErr.BadArgs, "index " + index + " is outside 0.." + (l.Count - 1));
                var e = l[index];
                ctx.MainDo(() =>
                {
                    int sub, sp;
                    if (G.CurrentSavepoint(out sub, out sp) && sub != e.subscene)
                        warnings.Add("entry " + index + " is at savepoint " + e.subscene + ":" + e.savepoint + ", the game is at " + sub + ":" + sp + ": load that checkpoint first or the camera looks at unloaded space");
                    warnings.Add(WorldCamera.ApplyPose(e.camPos, e.camRot, true, warnings));
                });
                rows.Add(ShotRoute.Row(e));
            }
            else
                for (int i = offset; i < l.Count && rows.Count < limit; i++) rows.Add(ShotRoute.Row(l[i]));
            r["outcome"] = "done";
            r["route"] = rows;
            r["total"] = (double)l.Count;
            if (!apply) warnings.Add(ShotRoute.Entries(l) + " savepoints, " + l.Count + " frames; apply:true with index flies the free camera there (restore gives it back)");
            r["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(r);
        }

        // ---------------------------------------------------------------- resources
        static ResourceContents ReadResource(string uri, CallContext ctx)
        {
            if (uri == TunablesUri) return new ResourceContents { uri = uri, mimeType = "application/json", text = ReflectValues.Compact(Tunables.ToJson()) };
            if (uri != RouteUri) return null;
            var l = Route(ctx);
            var rows = new List<object>();
            foreach (var e in l) rows.Add(ShotRoute.Row(e));
            var o = new Json.Obj();
            o["source"] = "StreamingAssets/" + ShotRoute.File;
            o["savepoints"] = (double)ShotRoute.Entries(l);
            o["frames"] = (double)l.Count;
            o["fields"] = "index, subscene, savepoint, pos (character), cam_pos, cam_rot (euler degrees)";
            if (routeError != null) o["error"] = routeError;
            o["route"] = rows;
            return new ResourceContents { uri = uri, mimeType = "application/json", text = ReflectValues.Compact(o) };
        }
    }
}
