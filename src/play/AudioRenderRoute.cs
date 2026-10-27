using System;
using System.Collections.Generic;
using System.Globalization;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // AudioRenderRoute.cs (owner WORLD_B): engine-free data for the render and reflect tools, offline tested
    // (qa/mcp/WORLD_B). ShotRoute decodes Playdead's StreamingAssets/ScreenshotRunner/ScreenShotPositions.bin
    // (FP/ScreenshotProfiler.cs:118, 218; layout from ScreenshotProfiler.PositionSamples.Load as decoded by the audit:
    // int32 version, int32 entries, then per entry int32 subscene, int32 savepoint, int32 frames and per frame 10 floats
    // = character x, y, z, camera x, y, z, rotation quaternion x, y, z, w; 127 entries, 615 frames). Tunables is the
    // typed list of the knobs an agent may turn, with type, range, units and the tool call that sets each; reflect set
    // checks the reflect-reachable ones against their range (an error, never a clamp).
    public sealed class RouteEntry
    {
        public int index, subscene, savepoint, frame;
        public Vector3 pos, camPos;
        public Quaternion camRot;
    }

    public static class ShotRoute
    {
        public const string File = "ScreenshotRunner/ScreenShotPositions.bin";

        public static List<RouteEntry> Decode(byte[] b, out int version, out string error)
        {
            version = 0;
            error = null;
            var l = new List<RouteEntry>();
            if (b == null || b.Length < 8) { error = "file too short"; return l; }
            version = BitConverter.ToInt32(b, 0);
            int count = BitConverter.ToInt32(b, 4);
            if (count < 0 || count > 100000) { error = "bad entry count " + count; return l; }
            int off = 8;
            for (int e = 0; e < count; e++)
            {
                if (off + 12 > b.Length) { error = "truncated at entry " + e; return l; }
                int sub = BitConverter.ToInt32(b, off), sp = BitConverter.ToInt32(b, off + 4), n = BitConverter.ToInt32(b, off + 8);
                off += 12;
                if (n < 0 || n > 100000) { error = "bad frame count " + n + " at entry " + e; return l; }
                for (int j = 0; j < n; j++)
                {
                    if (off + 40 > b.Length) { error = "truncated in entry " + e; return l; }
                    var f = new float[10];
                    for (int k = 0; k < 10; k++) f[k] = BitConverter.ToSingle(b, off + 4 * k);
                    off += 40;
                    l.Add(new RouteEntry { index = l.Count, subscene = sub, savepoint = sp, frame = j, pos = new Vector3(f[0], f[1], f[2]), camPos = new Vector3(f[3], f[4], f[5]), camRot = new Quaternion(f[6], f[7], f[8], f[9]) });
                }
            }
            if (off != b.Length) error = "trailing " + (b.Length - off) + " bytes";
            return l;
        }

        public static Json.Obj Row(RouteEntry e)
        {
            var o = new Json.Obj();
            o["index"] = (double)e.index;
            o["subscene"] = (double)e.subscene;
            o["savepoint"] = (double)e.savepoint;
            o["pos"] = PlayShared.V(e.pos);
            o["cam_pos"] = PlayShared.V(e.camPos);
            o["cam_rot"] = PlayShared.V(ReflectValues.ToEuler(e.camRot));
            return o;
        }

        public static int Entries(List<RouteEntry> l)
        {
            var seen = new HashSet<long>();
            foreach (var e in l) seen.Add(((long)e.subscene << 32) | (uint)e.savepoint);
            return seen.Count;
        }
    }

    public sealed class Tunable
    {
        public string name, type, units, how, desc;
        public double min, max;
        public string[] values;
        // reflect reach: type name + member path (null = tool only)
        public string reflectType, reflectMember;
    }

    public static class Tunables
    {
        public static readonly Tunable[] All =
        {
            T("time.speed", "number", 0.05, 4, "x", "time {op:\"speed\", speed}", "Game speed while running (realtime). Music-timed hazards desync below 1."),
            T("time.rng_seed", "integer", 0, 2147483647, "", "time {op:\"rng_seed\", seed}", "PDRandom.seed + RandomGenerator.ResetAll + UnityEngine.Random.seed."),
            T("time.scale", "number", 0, 10, "x", "time (step mode owns it)", "GameManager.TimeScale; G clamps 0..10; the time tool re-applies its own value.", "GameManager", "TimeScale"),
            T("camera.fov", "number", 5, 140, "deg", "camera {op:\"pose\", pose:{x, y, z, fov}}", "Free camera field of view (the game camera never changes FOV)."),
            T("camera.shake", "boolean", 0, 1, "", "camera {op:\"fx\", shake}", "CameraScript.bCustomEffects."),
            T("camera.handshake", "boolean", 0, 1, "", "camera {op:\"fx\", handshake}", "CameraScript.bHandshakeEffect."),
            T("camera.free_speed", "number", 0.1, 200, "m/s", "reflect set type CameraControl member speed", "Core free camera fly speed.", "CameraControl", "speed"),
            T("camera.free_zoom", "number", 0.15, 3, "x", "reflect set type CameraControl member zoom", "Core free camera zoom.", "CameraControl", "zoom"),
            T("camera.box_time_in", "number", 0, 30, "s", "reflect set (CameraBlendBox) member timeIn", "Blend-in seconds of one framing box.", "CameraBlendBox", "timeIn"),
            T("camera.box_time_out", "number", 0, 30, "s", "reflect set (CameraBlendBox) member timeOut", "Blend-out seconds of one framing box.", "CameraBlendBox", "timeOut"),
            T("render.post", "boolean", 0, 1, "", "render {op:\"post\", on}", "PDPostCombined on the gameplay camera."),
            T("render.fog", "boolean", 0, 1, "", "render {op:\"fog\", on}", "Unity fog + volumetric light fog (LightVolumeFog_camera)."),
            T("render.default_lights", "boolean", 0, 1, "", "render {op:\"default_lights\", on}", "DefaultLightMode neutral light rig."),
            E("render.debug_view", AudioRender.DebugViews, "render {op:\"debug_view\", mode}", "PDRenderDebug render mode."),
            T("render.gamma", "number", -1, 1, "", "reflect set (PDPostCombined) member post_gamma", "Post gamma offset (INFERRED range).", "PDPostCombined", "post_gamma"),
            T("render.fader", "boolean", 0, 1, "", "reflect set (GameManager) member enableFader", "false hides the black fades; load timing stays the same.", "GameManager", "enableFader"),
            T("hazard.god", "boolean", 0, 1, "", "hazard {op:\"god\", on}", "EditorMode InvincibleBoy (death animations still play)."),
            T("air.infinite", "boolean", 0, 1, "", "hazard {op:\"air\", infinite}", "boy.properties.swim.infiniteAir, re-applied after respawns."),
            T("air.max_dive_s", "number", 1, 120, "s", "hazard {op:\"air\", max_dive_s}", "Dive limit of the water the boy is in (default 11 s).", "BoyWater", "maxDivingTime"),
            T("air.refill_s", "number", 0, 60, "s", "hazard {op:\"air\", refill_s}", "Seconds of air given back now (Boy.GiveBoyExtraAir)."),
            T("audio.master_volume", "number", 0, 0, "", "audio {op:\"rtpc\", name:\"rtpc_master_volume\", value:0}", "Sound policy: only 0 (mute) is allowed."),
            T("debug.flag", "boolean", 0, 1, "", "render {op:\"debug_flags\", flag, on}", "EditorMode debug flags (list with render {op:\"debug_flags\"})."),
        };

        static Tunable T(string name, string type, double min, double max, string units, string how, string desc, string rt = null, string rm = null)
        {
            return new Tunable { name = name, type = type, min = min, max = max, units = units, how = how, desc = desc, reflectType = rt, reflectMember = rm };
        }

        static Tunable E(string name, string[] values, string how, string desc)
        {
            return new Tunable { name = name, type = "enum", values = values, how = how, desc = desc, units = "" };
        }

        // reflect set: an error text when typeName.member is a listed numeric tunable and value is outside its range
        public static string CheckReflect(string typeName, string member, object value)
        {
            if (typeName == null || member == null || !ReflectValues.IsNumber(value)) return null;
            double v = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            foreach (var t in All)
            {
                if (t.reflectType != typeName || t.reflectMember != member || t.type != "number") continue;
                if (double.IsNaN(v) || v < t.min || v > t.max)
                    return t.reflectType + "." + t.reflectMember + " = " + v.ToString("R", CultureInfo.InvariantCulture) + " is outside " + t.name + " (" + t.min.ToString("R", CultureInfo.InvariantCulture) + " .. " + t.max.ToString("R", CultureInfo.InvariantCulture) + (t.units.Length > 0 ? " " + t.units : "") + ")";
            }
            return null;
        }

        public static Json.Obj ToJson()
        {
            var l = new List<object>();
            foreach (var t in All)
            {
                var o = new Json.Obj();
                o["name"] = t.name;
                o["type"] = t.type;
                if (t.values != null) o["values"] = PlayShared.L(t.values);
                else if (t.type != "boolean") { o["min"] = t.min; o["max"] = t.max; }
                if (t.units.Length > 0) o["units"] = t.units;
                o["set_with"] = t.how;
                o["description"] = t.desc;
                l.Add(o);
            }
            var r = new Json.Obj();
            r["tunables"] = l;
            r["note"] = "Values outside a range are refused, never clamped. reflect set checks the reflect-reachable ones.";
            return r;
        }
    }
}
