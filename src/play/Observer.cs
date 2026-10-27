using System.Collections.Generic;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // Observer.cs (owner OBSERVE, public API, data model and the observe schemas frozen): what the agent sees, as data.
    // observe = screen + readiness, the controlled body, nearby objects and hazards with ids and dx/dy in metres from the
    // body, checkpoint and guide topic, events since the previous observe, and a no-progress counter. Concise stays
    // under about 600 tokens. Ids: "b<n>" bodies, "o<n>" objects, "h<n>" hazards; one counter that never restarts, so an
    // id is never reused; every load start bumps Epoch and drops the table, an old id then fails with stale_id ("call
    // observe again"). Never cache Boy / Huddle / Character: read ScriptGlobals each call (re-created on every respawn).
    public sealed class ObserveOptions
    {
        public string detail = "concise";
        public float radius = 12f;
        public int maxObjects = 12;
        // set by actions: the tool that just ran (updates stuck.calls_without_progress)
        public string afterTool;
    }

    public sealed class ObjInfo
    {
        public string id, kind, name, path, state;
        public GameObject go;
        public Vector3 pos;
        public float dx, dy, dz, dist;
        public readonly List<string> actions = new List<string>();
    }

    public sealed class HazardInfo
    {
        public string id, kind, name, state;
        public GameObject go;
        public Vector3 pos;
        public float dx, dy, dist;
        // seconds until it can kill, -1 = unknown
        public float etaS = -1f;
        public bool threat, musicTimed, safe;
    }

    public sealed class BodyInfo
    {
        public string id, kind, name;
        public GameObject go;
        public Vector3 pos;
        public bool controlled, active;
    }

    // Implementation notes (OBSERVE): ObserverBuild.cs builds the JSON (screen, ready, time, body, checkpoint,
    // objects, hazards, events, stuck, warnings; full adds input, bodies, menu, camera, truncated, perf), the concise
    // form is trimmed to ConciseBudget.MaxChars (2400 chars of compact JSON); ObserverIds.cs holds the id table, id and
    // target resolution and the screen marks; ObserverLinks.cs the breakers around other roles' APIs. The concise
    // observation inside action results lists only death / load / checkpoint / error / note events (the action's
    // changes hold the rest).
    public static partial class Observer
    {
        public const string Owner = "OBSERVE";

        public static readonly string[] Screens = { "title", "loading", "in_world", "dead", "cutscene", "pause_menu", "menu", "credits", "message", "unknown" };
        public static readonly string[] ObjectKinds = { "box", "pushable", "ledge", "ladder", "rope", "lever", "valve", "button", "door", "hatch", "elevator",
            "hat", "submarine", "water", "savepoint", "trigger", "creature", "item", "other", "boards", "lift_door", "device" };
        public static readonly string[] HazardKinds = { "dog", "guard", "sentry", "spotlight", "rope_gun", "shockwave", "mermaid", "kill_zone", "drowning",
            "fall", "crusher", "other" };
        public static readonly string[] EventTypes = { "death", "respawn", "load_started", "load_finished", "checkpoint", "signal", "fsm", "toggled",
            "error", "note", "input_hijacked", "stalled" };

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("observe", "Observe", PlayShared.Both, Owner,
                "What the agent sees, as JSON: screen and readiness (next = what to call when not ready), the controlled body, nearby objects and hazards with ids and dx/dy in metres, checkpoint and guide topic, events. Call guide() once, then observe. Rules: the game is frozen between action calls (step mode); ids die on every load; at the title menu {op:\"submit\"} starts; call restore when done. image attaches a screenshot (marked = ids drawn); if your client drops images, use screenshot.",
                Schema.Obj().Enum("detail", "concise (at most about 800 tokens) or full.", "concise", "concise", "full")
                    .Num("radius", "Metres around the body for objects and hazards.", 12, 1, 60)
                    .Int("max_objects", "Objects to list, nearest first.", 12, 0, 40)
                    .Enum("image", "Attach a screenshot: none, preview or marked.", "none", "none", "preview", "marked")
                    .Int("max_width", "Image width (px).", 960, 160, 1920).Build(),
                FullSchema().BuildOutput()).ReadOnly().AlwaysLoad(), Handle);
        }

        // worker thread: stale component caches are rescanned first (3 types per main-thread item), then the
        // observation is built in one main-thread item; image adds a screenshot through the host (McpShot), whose
        // failure becomes a warning, never an error
        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            var o = new ObserveOptions
            {
                detail = PlayShared.Str(a, "detail", "concise"),
                radius = (float)PlayShared.Num(a, "radius", 12),
                maxObjects = PlayShared.Int(a, "max_objects", 12),
            };
            string image = PlayShared.Str(a, "image", "none");
            int maxWidth = PlayShared.Int(a, "max_width", 960);
            ChangeLog.Touch();
            for (int i = 0; i < 12; i++) if (!ctx.Main(() => Perception.RefreshSome(3))) break;
            var obs = ctx.Main(() => Observe(o));
            obs["meta"] = PlayShared.BuildMeta();
            var res = ToolResult.Ok(obs);
            if (image == "preview" || image == "marked") AttachImage(res, obs, image, maxWidth, ctx);
            return res;
        }

        // main thread: the observe structure (FullSchema; concise omits input, bodies, menu, camera)
        public static Json.Obj Observe(ObserveOptions o) { return Build(o ?? new ObserveOptions(), false); }
        // main thread: the concise observation that every action returns as "observe"
        public static Json.Obj Concise(string afterTool) { return Build(new ObserveOptions { afterTool = afterTool }, afterTool != null); }
        // main thread: one of Screens
        public static string Screen() { return ScreenNow(); }
        // main thread: PlayLevels.Ready plus body checks; reason when not ready (loading, title, paused, dead, no_body ...)
        public static bool Ready(out string reason) { return ReadyNow(out reason); }

        // main thread: stable id for a live object; prefix "o", "h" or "b"
        public static string IdFor(Object o, string prefix) { return IdFor(o, prefix, 0); }
        // main thread: the object of an id; throws McpException stale_id (older epoch or destroyed) or not_found
        public static Object Resolve(string id) { return ResolveId(id); }
        // main thread: id (o12/h3/b1), "boy", "huddle", "#<instanceID>", exact hierarchy path, "/"-bounded unique path
        // suffix, or unique exact name (loaded objects). No nearest-match fallback: throws McpException not_found, or
        // ambiguous with data.candidates [{id, path}] (at most 5).
        public static GameObject ResolveTarget(string spec) { return ResolveSpec(spec); }
        public static bool TryResolveTarget(string spec, out GameObject go, out McpException error)
        {
            go = null;
            error = null;
            try { go = ResolveSpec(spec); return true; }
            catch (McpException e) { error = e; return false; }
        }
        // any thread; bumps on every load start
        public static int Epoch { get { return ids.Epoch; } }

        // main thread (McpShot.MarkSource): screen pixels (origin top-left, full-size capture) of the ids of the latest
        // observation that are in front of the camera and on screen, nearest first
        public static List<ShotMark> ScreenMarks(int max) { return Marks(max); }

        // ---------------------------------------------------------------- schemas (contract, implemented)
        // the "observe" field of action results: same shape as observe detail concise (documented once, in observe)
        public static Json.Obj ConciseRef()
        {
            return Schema.Described(Schema.ObjAny(), "Concise observation, same fields as the observe tool (frame, step, screen, ready, time, body, checkpoint, objects, hazards, events, stuck, warnings).");
        }

        public static SchemaObj BodySchema()
        {
            return Schema.Obj().Enum("kind", "Controlled body.", null, "boy", "huddle", "dude", "submarine").Str("id", "Body id (b1).").Str("name", "GameObject name.")
                .Num("x", "World x (m).", -1e5, 1e5).Num("y", "World y (m).", -1e5, 1e5).Num("z", "World z (depth plane, m).", -1e5, 1e5)
                .Num("vx", "m/s.", -1e3, 1e3).Num("vy", "m/s.", -1e3, 1e3).Int("facing", "+1 right, -1 left.", -1, 1)
                .Str("state", "State without the Boy/Huddle prefix and State suffix: Run, Jump, GrabPlateau, ReachPlateau, ClimbDown, Grab, Ladder, Rope, Swim, SwimGrab, Slide, CustomAnim, Ragdoll; huddle: Run, Climb, Grab, Swim, ...")
                .Str("anim", "Animation state (full).").Bool("grounded", "Standing on ground.").Bool("in_water", "In a water volume.")
                .Num("submerged", "0..1.", 0, 1).Num("air_s", "Seconds of air left while diving (absent when not diving).", -1, 1e4)
                .Str("holding", "Id of the grabbed object.").Str("holding_name", "Its name.")
                .Bool("grip_held", "The grab from interact grab is still held (action pressed) until interact release or act {release:true}.")
                .Str("grab", "Huddle grab: none, or GrabBeam, GrabBox, GrabContainer, GrabItem, GrabShield, GrabSitOnTop.")
                .Bool("stick_forced", "The game drives the stick itself now (railing crash, CEO wall ...): do not fight it, wait.")
                .Str("forced_by", "Which script forces the input and what it holds (BoySetInput on _PM RailingCrash ...).")
                .Enum("special", "Special mode.", null, "none", "InsideSub", "ClimbingSub", "ClimbingCliff", "MindControlling", "MindControlled")
                .Bool("dead", "Dead (respawn follows).").Bool("controllable", "Input reaches the body now (playing, input enabled, not dead, not scripted).")
                .Bool("obstructed", "A wall or obstacle is right ahead (full).").Num("edge_left", "Distance to the left ledge (m, full).", -1, 1e3)
                .Num("edge_right", "Distance to the right ledge (m, full).", -1, 1e3);
        }

        public static SchemaObj ObjectSchema()
        {
            return Schema.Obj().Str("id", "Object id (o12), valid until the next load.").Enum("kind", "Kind.", null, ObjectKinds).Str("name", "GameObject name.")
                .Num("dx", "Metres right (+) / left (-) of the body.", -1e4, 1e4).Num("dy", "Metres above (+) / below (-).", -1e4, 1e4)
                .Num("dz", "Depth offset (m, only when not 0).", -1e4, 1e4).Num("dist", "Distance (m).", 0, 1e5)
                .Arr("actions", "interact verbs that can work now or after move_to.", Schema.Str(), 0, 12).Str("state", "Short state (on, off, open, moving ...).")
                .Vec("screen", "Pixel position in the attached image (only with image).", 2, -1e5, 1e5);
        }

        public static SchemaObj HazardSchema()
        {
            return Schema.Obj().Str("id", "Hazard id (h3).").Enum("kind", "Kind.", null, HazardKinds).Str("name", "GameObject name.")
                .Num("dx", "m.", -1e4, 1e4).Num("dy", "m.", -1e4, 1e4).Num("dist", "m.", 0, 1e5)
                .Str("state", "idle, patrol, chase, aim, cycle 3.1/6 s, ...").Num("eta_s", "Seconds until it can kill (-1 unknown).", -1, 1e4)
                .Bool("threat", "Dangerous now.").Bool("safe", "The body is in cover from it.").Bool("music_timed", "Runs on the music clock: use time realtime.");
        }

        public static SchemaObj CheckpointSchema()
        {
            return Schema.Obj().Int("subscene", "Savepoint subscene index (save numbering, 0..82).", -1, 999).Int("savepoint", "Savepoint index.", -1, 999)
                .Str("name", "Subscene name.").Str("area", "Area name.").Int("chapter", "Chapter index (0..65).", -1, 99).Str("chapter_name", "Chapter label.")
                .Str("guide", "guide topic for this place (call guide with it), absent when none.");
        }

        public static SchemaObj EventSchema()
        {
            return Schema.Obj().Int("step", "LogicManager.stepCount.", 0, long.MaxValue).Enum("type", "Event type.", null, EventTypes).Str("detail", "Short detail.");
        }

        public static SchemaObj ConciseSchema()
        {
            return Schema.Obj().Int("frame", "Time.frameCount.", 0, long.MaxValue).Int("step", "LogicManager.stepCount.", 0, long.MaxValue)
                .Enum("screen", "What is on screen.", null, Screens).Bool("ready", "The game accepts play input now.").Str("reason", "Why not ready.")
                .Str("next", "When not ready: the call to make (menu submit at the title, menu close when paused, wait_until ...).")
                .OutObj("time", "Clock.", Schema.Obj().Enum("mode", "step or realtime.", null, "step", "realtime").Bool("frozen", "Held at 0.")
                    .Num("scale", "Time.timeScale.", 0, 100).Num("music_s", "Music position.", -1, 1e6))
                .OutObj("body", "Controlled body.", BodySchema())
                .OutObj("checkpoint", "Current savepoint.", CheckpointSchema())
                .Arr("objects", "Nearby objects, nearest first.", Schema.OutOf(ObjectSchema()), 0, 40)
                .Arr("hazards", "Nearby hazards, most urgent first.", Schema.OutOf(HazardSchema()), 0, 20)
                .Arr("events", "Since the previous observe (max 20).", Schema.OutOf(EventSchema()), 0, 20)
                .OutObj("stuck", "No-progress counter.", Schema.Obj().Int("calls_without_progress", "Action calls in a row without new ground, a new checkpoint or a new event near the body.", 0, 100000)
                    .Int("deaths_here", "Deaths at the current checkpoint.", 0, 100000).Str("hint", "Suggestion when the counter is 3 or more, or after 2 deaths here."))
                .Arr("warnings", "music_timed, input_hijacked, controller_digital ...", Schema.Str(), 0, 20);
        }

        public static SchemaObj FullSchema()
        {
            return ConciseSchema()
                .OutObj("input", "Agent input.", Schema.Obj().Enum("mode", "Input mode.", null, "human", "agent", "mix")
                    .OutObj("held", "Latched input (null = none).", Schema.Obj().Vec("stick", "x, y.", 2, -1, 1).Bool("jump", "").Bool("action", ""))
                    .Int("queued", "Queued steps.", 0, 100000).Str("controller", "Real controller type (Analog, Digital ...).").Bool("hijacked", "Something replaced the input core."))
                .Arr("bodies", "Every controllable body (full).", Schema.OutOf(Schema.Obj().Str("id", "b<n>").Enum("kind", "", null, "boy", "huddle", "dude", "submarine")
                    .Str("name", "").Num("x", "", -1e5, 1e5).Num("y", "", -1e5, 1e5).Bool("controlled", "Receives the agent's input.").Bool("active", "Active in the world.")), 0, 16)
                .OutObj("menu", "Menu summary when a menu is open.", Schema.Obj().Bool("open", "").Str("group", "").Str("selected", "Selected item id."))
                .OutObj("camera", "Camera override (full).", Schema.Obj().Bool("overridden", "A tool holds the camera.").Int("pinned_subscene", "Pinned area while overridden.", -1, 999))
                .OutObj("image", "Attached screenshot (see screenshot).", Schema.Obj().Int("width", "", 0, 8192).Int("height", "", 0, 8192).Str("path", "").Bool("black", ""))
                .OutObj("truncated", "Counts left out per list.", Schema.Obj().Open());
        }
    }
}
