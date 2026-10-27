using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Replay mode shared contract (architect-owned; implementers must not edit). Design: docs/REPLAY_DESIGN.md 2.6, 3.1, 4.2.
    // Revision 3: PlaySpec has no unityTime (a given `replay.play.unityTime` is a lint error, ReplayConst.CheckRemoved),
    // AudioCapSpec as 3.1, run.json blocks ReplayPassInfo (passes[], with rngStart/rngEnd) and ReplayRecordingInfo.
    // Spec classes of the `replay` block (one public field per JSON key, default = initializer, filled by SpecBind), the
    // scene.srec header and constants, IReplaySource with ReplayHub (what CORE and RIG read while a replay drives the scene)
    // and the record -> replay handover of forced cullers and keep-loaded subscenes.

    // ------------------------------------------------------------------ spec: replay block (MultiCamSpec.replay, null = all defaults)

    public sealed class ReplaySpec : SpecNode
    {
        public string source = "";
        public RecordSpec record = new RecordSpec();
        public PlaySpec play = new PlaySpec();
        public AudioCapSpec audio = new AudioCapSpec();
    }

    public sealed class RecordSpec : SpecNode
    {
        public string clock = "real";
        public int rate = 60;
        public Vector2 res = new Vector2(0f, 0f);
        public List<string> roots = new List<string>(ReplayConst.DefaultRoots);
        public List<string> keep = new List<string>();
        public int settleFrames = 12;
        public List<string> exclude = new List<string>();
        public BoxSpec box;
        public bool characters = true;
        public bool spawned = true;
        public bool keepSpawned = false;
        public string particles = "all";
        public List<string> particleSystems = new List<string>();
        public bool lights = true;
        public bool materials = true;
        public bool probes = true;
        public bool fog = true;
        public bool post = false;
        public bool shaderState = true;
        public List<string> blocks = new List<string>();
        public float posEps = 0.0001f;
        public float rotEps = 0.0012f;
        public float cut = 0.75f;
        public bool alwaysAnimate = true;
        public string forceCullers = "zone";
        public List<string> cullers = new List<string>();
        public int rescanEvery = 60;
        public bool captureGame = false;
        public int captureEvery = 10;
        public List<string> checks = new List<string>();
    }

    public sealed class BoxSpec : SpecNode
    {
        public Vector3 center;
        public Vector3 size;
    }

    public sealed class PlaySpec : SpecNode
    {
        public string look = "game";
        public string order = "camera";
        public string interp = "hermite";
        public string particles = "extrapolate";
        public int warmup = 16;
        public string motionBlur = "game";
        public float shutterAngle = 288f;
        public string cullers = "camera";
        public string shadows = "recorded";
        public string activate = "quiet";
        public Json.Obj activateList;
        public bool copyHolds = true;
        public string realTime = "record";
        public float ramp = 0f;
        public List<string> cams = new List<string>();
        public List<string> coverageOk = new List<string>();
        public Vector2 range;
        public List<string> setup;
        public float maxUnresolved = 0.01f;
        public bool monitor = true;
        public string restore = "final";
    }

    public sealed class AudioCapSpec : SpecNode
    {
        public string capture = "none";
        public string suspend = "auto";
        public bool keepSuspended = true;
        public bool allowMuterOnly = false;
        public float level = 100f;
        public int markerEvery = 30;
        public string onHold = "abort";
        public string heartbeat = "_mod/muter/heartbeat.json";
        public float maxHeartbeatAge = 2f;
    }

    // Allowed values of the string keys above (lint in mcam check and tools/mcam_spec_check.py). PlaySpec.motionBlur is a
    // MotionBlurModes entry or a number (SpecBind turns a JSON number into its invariant string). PlaySpec.range: given
    // (Has("range")) = [from, to] in record seconds, else the whole recording. PlaySpec.setup null = the record spec's setup
    // without its cues, [] = only the header's load + waitfor streaming. PlaySpec.activateList: {path: "quiet" | "normal"}.
    public static class ReplayConst
    {
        public static readonly string[] DefaultRoots = { "auto" };
        public static readonly string[] Clocks = { "real", "fixed" };
        public static readonly int[] FixedRates = { 60, 120, 240 };
        public static readonly string[] RecordParticles = { "all", "none", "list" };
        public static readonly string[] ForceCullers = { "zone", "planned", "list", "none" };
        public static readonly string[] Looks = { "game", "clean" };
        public static readonly string[] Orders = { "camera", "step" };
        public static readonly string[] Interps = { "hermite", "linear", "nearest" };
        public static readonly string[] PlayParticles = { "extrapolate", "match", "sample" };
        public static readonly string[] MotionBlurModes = { "game", "angle", "off" };
        public static readonly string[] Cullers = { "camera", "recorded", "force", "live" };
        public static readonly string[] Shadows = { "recorded", "dirty", "dynamic" };
        public static readonly string[] Activates = { "quiet", "normal", "never" };
        public static readonly string[] ActivateListModes = { "quiet", "normal" };
        public static readonly string[] RealTimes = { "record", "capture" };
        public static readonly string[] Restores = { "final", "reload" };
        public static readonly string[] AudioCaptures = { "none", "record" };
        public static readonly string[] Suspends = { "auto", "on", "off" };
        public static readonly string[] OnHolds = { "abort", "mark" };
        public const string RecordFolder = "record";
        public const string RecordFile = "scene.srec";
        public const string RecordJson = "record.json";
        public const string ObjectsFile = "objects.tsv";
        public const string RecordMetaFile = "record_meta.csv";
        public const string AudioFile = "audio.wav";
        public const string AudioSyncFile = "audio_sync.csv";
        public const string GameFolder = "game";
        public const string GoldenFolder = "golden";
        public const string NoiseFolder = "noise";
        public const string BaselineFile = "replay_baseline.tsv";
        public const string PassesFile = "passes.json";
        public const string SuspendFlagFile = "acap_suspend_ok.json";
        public const string MuterOnlyFlagFile = "acap_muteronly_ok.json";
        public const string CameraDrivenFile = "camera_driven.tsv";

        // Keys that earlier revisions accepted and revision 3 removed: SpecBind only warns "unknown key", the lint
        // (mcam check, tools/mcam_spec_check.py) turns each given one into an error with this reason.
        public static readonly string[] RemovedKeys = { "replay.play.unityTime" };
        public static readonly string[] RemovedReasons = { "removed in revision 3: the engine sets _Time per camera, a global override changes 0 px (REPLAY_DESIGN 2.4.7, L10)" };

        // Culler visibility in play.cullers "camera": the pass camera's frustum and facing planes (the game's own test, run for our
        // camera), except that a culler on a Light stays on while the recording had it on (forced by record.forceCullers, or
        // camera-active in the record): lights, their light-pass volumes and their light fog (LightVolumeFog) must not switch per
        // replay camera (S17: the cafeteria sun patch `light pg 1`, the CEO window haze of `Spotlight_BoundsCuller`).
        public static bool CameraCullerVisible(bool light, bool forced, bool recordedOn, bool cameraVisible)
        {
            return cameraVisible || (light && (forced || recordedOn));
        }

        // objects.tsv seenActive (SceneRecorder.WriteObjects): a transform kind by its own id, a component kind (particles, light,
        // material, probe, rope, renderer) by its owner transform (parentId), in the set of ids active in hierarchy at any sample.
        public static bool SeenActive(byte kind, int id, int parentId, ICollection<int> everActive)
        {
            if (everActive == null) return false;
            bool xf = kind == SrecConst.KindTransform || kind == SrecConst.KindBoyBone || kind == SrecConst.KindClothBone || kind == SrecConst.KindSpawned;
            return everActive.Contains(xf ? id : parentId);
        }

        // Mesh keys of the "mesh" event rows (SkinnedMeshRenderer.sharedMesh swaps) from core R3p: `<name>|<vertexCount>|<instanceId>`
        // (parsed from the right, so a name may hold '|'); records before R3p hold plain names. MeshMatch scores a live mesh
        // against a key: 2 = same name, vertex count and instance id (this session), 1 = same name and vertex count, 0 = no match;
        // a plain name matches by name only (1).
        public static string MeshKey(string name, int vertexCount, int instanceId)
        {
            return (name ?? "") + "|" + vertexCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + instanceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public static bool SplitMeshKey(string key, out string name, out int vertexCount, out int instanceId)
        {
            name = key ?? ""; vertexCount = -1; instanceId = 0;
            if (key == null) return false;
            int b = key.LastIndexOf('|');
            if (b <= 0) return false;
            int a = key.LastIndexOf('|', b - 1);
            if (a < 0) return false;
            int vc, id;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (!int.TryParse(key.Substring(a + 1, b - a - 1), System.Globalization.NumberStyles.Integer, ic, out vc)) return false;
            if (!int.TryParse(key.Substring(b + 1), System.Globalization.NumberStyles.Integer, ic, out id)) return false;
            name = key.Substring(0, a); vertexCount = vc; instanceId = id;
            return true;
        }

        public static int MeshMatch(string key, string name, int vertexCount, int instanceId)
        {
            string kn; int kv, ki;
            if (!SplitMeshKey(key, out kn, out kv, out ki)) return name == key ? 1 : 0;
            if (name != kn || vertexCount != kv) return 0;
            return instanceId == ki ? 2 : 1;
        }

        // Whether the recorder reads an object's localScale this sample: always on full / first / scale-variant / just-activated /
        // reparented / kinematic-on samples and for watched objects (registered or spawned: mod content that scales), within the
        // first `warmup` samples, else every `warmup`-th sample staggered by the object's index.
        public static bool ScaleDue(bool always, bool watch, int k, int index, int sinceFirst, int warmup)
        {
            return always || watch || sinceFirst < warmup || ((k + index) % Math.Max(1, warmup)) == 0;
        }

        public static void CheckRemoved(Json.Obj root, SpecReport report)
        {
            if (root == null || report == null) return;
            for (int i = 0; i < RemovedKeys.Length; i++)
            {
                var parts = RemovedKeys[i].Split('.');
                Json.Obj node = root;
                for (int k = 0; k < parts.Length - 1 && node != null; k++) node = node[parts[k]] as Json.Obj;
                if (node != null && node.ContainsKey(parts[parts.Length - 1])) report.Error(RemovedKeys[i], RemovedReasons[i]);
            }
        }
    }

    // ------------------------------------------------------------------ scene.srec header and constants (4.2)

    public sealed class RecordHeader
    {
        public int version = 1; public string created = "", spec = "", level = "", recorderVersion = "", clock = "real";
        public int captureFramerate, subscene = -1, savepoint = -1, streamVersion, screenW, screenH;
        public double fixedDeltaTime, t0, t0Real; public long stepAtT0; public float gameFov;
        public List<string> subscenes = new List<string>(), keptCullers = new List<string>(), forcedCullers = new List<string>();
        public List<string> fogFields = new List<string>(), postFields = new List<string>();
        public List<string> cameraDriven = new List<string>();
        public float posEps, rotEps, cut;
        public int vSyncCount, targetFrameRate, refreshRate;
    }

    // Version is the preamble (framing) version: readers refuse a newer one. ContentVersion is RecordHeader.version as the
    // recorder writes it: 1 = revision 3, 2 = adds the shader-state sections 17 ShaderDef and 18 ShaderVal (material round
    // 2026-10-25). The framing did not change: version 1 readers skip sections 17 and 18, and version 1 files have none.
    public static class SrecConst
    {
        public const uint Magic = 0x43525349;                          // "ISRC"
        public const ushort Version = 1;
        public const int ContentVersion = 2;
        public const uint TagHead = 0x44485253, TagObjects = 0x544A424F, TagSample = 0x4C504D53, TagIndex = 0x58444E49, TagEnd = 0x444E4553;
        public const byte KindTransform = 0, KindBoyBone = 1, KindClothBone = 2, KindParticles = 3, KindLight = 4, KindMaterial = 5, KindProbe = 6, KindSpawned = 7, KindRope = 8, KindRenderer = 9;
        public const byte SecXf = 1, SecFlags = 2, SecParticles = 3, SecLights = 4, SecMaterials = 5, SecVirtual = 6, SecObjAdd = 7, SecObjDel = 8, SecProbes = 9, SecRender = 10, SecEvents = 11, SecMatProp = 12, SecCamActive = 13, SecRope = 14, SecSharedMat = 15, SecStrings = 16, SecShaderDef = 17, SecShaderVal = 18;
        public const byte ShGlobal = 0, ShMaterial = 1, ShBlock = 2;    // shader channel scope: Shader global, Material property (target = material name string id), Renderer property block (target = kind 9 object id)
        public const byte XfPos = 1, XfRot = 2, XfScale = 4, XfCut = 8, XfParent = 16, XfPreKey = 32;
        public const byte FlActive = 1, FlRenderer = 2, FlKinematic = 4, FlLight = 8;
        public const byte ObjCamDriven = 1, ObjScaleVariant = 2, ObjExcludedMaterial = 4, ObjSpawned = 8, ObjLocal = 16, ObjCamXf = 32, ObjSeenActive = 64;
        public const int ParticleBytes = 84;
        public const ushort SmplMarker = 1, SmplHitch = 2, SmplHold = 4, SmplGameFrame = 8;
        public const uint CompRigidbody = 1, CompAnimation = 2, CompAnimator = 4, CompParticleSystem = 8, CompLight = 16, CompRenderer = 32, CompSkinnedMesh = 64, CompMaterialInstance = 128, CompVelocityTag = 256, CompColorBlendProbe = 512, CompLineRenderer = 1024, CompActor = 2048, CompCollider = 4096;
        public const byte LiEnabled = 1, LiIntensity = 2, LiColor = 4, LiRange = 8, LiSpotAngle = 16, LiCookie = 32, LiStaticShadows = 64, LiShadowsDirty = 128;
        public const byte MatFloat = 0, MatColor = 1, MatVector = 2, MatTexture = 3;
        public const byte ValFloat = 0, ValVec4 = 1, ValInt = 2;
        public const byte VirtBoy = 1, VirtHuddle = 2, VirtHuddleMain = 4;
    }

    // ------------------------------------------------------------------ replay source (read by CORE and RIG while replaying)

    public interface IReplaySource
    {
        bool active { get; }
        double duration { get; }
        int sampleCount { get; }
        double SampleTime(int i);
        int SampleAt(double t);                                         // last sample with time <= t (0 before the first)
        bool GameCam(double t, GameCamState g);                         // interpolated recorded game camera state
        bool Character(string who, double t, out Vector3 pos3, out Vector3 vel3, out string state); // "boy" | "huddle"
        bool Probe(double t, out Vector3 probe);                        // cameraBlendProbe world position
        double RealTimeAt(double t);                                    // record wall clock, seconds since t0Real
        long LogicStepAt(double t);
        double EventTime(string name);                                  // first fire in the record pass, NaN if none
    }

    public static class ReplayHub
    {
        public static IReplaySource source;
        public static bool replaying { get { return source != null && source.active; } }
    }

    // ------------------------------------------------------------------ run.json blocks of replay takes (4.1)

    // One entry of run.json `passes[]` (field names are the JSON keys). SceneReplayer.BeginPass creates SceneReplayer.pass
    // with camera, startHash and rngStart (UnityEngine.Random.seed, read only, never written), EndPass fills endHash and
    // rngEnd; CORE adds renders, warmups and taaResets and appends the entry to its list.
    public sealed class ReplayPassInfo
    {
        public string camera = "";
        public string startHash = "", endHash = "";
        public int rngStart, rngEnd;
        public int renders, warmups, taaResets;
    }

    // run.json `recording` block (field names are the JSON keys), returned by SceneReplayer.Info() after Load and kept
    // current through the passes; CORE serializes it.
    public sealed class ReplayRecordingInfo
    {
        public string dir = "";
        public RecordHeader header;
        public int samples, objects;
        public double duration;
        public int unresolved, unresolvedSpawned;
        public List<string> never_activated = new List<string>();
        public int conflicts, readbackErrors, activations, monitorHits;
    }

    // Record -> replay handover in one session (mcam take, srec record stop keep): RecordPrep.HandOver fills it, the replay
    // keeps the cullers forced (valid = false) and the subscenes loaded until SceneReplayer.End releases them.
    public sealed class ReplayHandover
    {
        public List<BoundsCullerRuntime> forced = new List<BoundsCullerRuntime>();
        public List<SubsceneCuller> keptLoaded = new List<SubsceneCuller>();
    }
}
