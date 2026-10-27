using System;
using UnityEngine;
#if !PLAY_B_MATH_ONLY
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UObj = UnityEngine.Object;
#endif

namespace InsideDev
{
    // Replay mode PLAY_B (docs/REPLAY_DESIGN.md 2.4, revision 3 with the Phase 0 lab results of Section 12): load and
    // resolve a recording, freeze and neutralize (2.4.2), apply the recorded state at replay time T through SceneHooks (2.4.3:
    // activation, probe roots and ColorBlendProbe overrides in preUpdate; every transform, bone, rope and particle system in
    // lateWrite; culler emulation, visibility, read-back, materials, lights, fog, shared materials and cut velocity resets in
    // preCullMain; recorded visibility and mesh swaps are applied at the end of lateWrite too, before skinning and culling, so
    // a renderer the recording switches on draws in that frame, not one frame late), pass isolation with the state hash (2.4.9),
    // monitor, restore (2.4.8: game camera first, cullers last).
    // Contract for CORE_A and PLAY_C (frozen public shapes of 2.6): Load reads the recording into `tracks` (SceneTracks.Load)
    // and resolves every object; in the recording's own session it reuses Ref(id) of SceneRecorder.SessionRecording(folder)
    // (any recorder of this game session that wrote the folder, no level load since). Begin sets current and ReplayHub.source = this (active = true); End clears both.
    // `pass` is the run.json passes[] entry of the current pass (BeginPass: camera, startHash, rngStart; EndPass: endHash,
    // rngEnd). Info() is the run.json recording block. Resolved(id) is the live object of an object table id (Transform for
    // transform kinds, the component for the component kinds, null = unresolved). ObjectsText is `srec objects [filter]`.
    // Component kinds (particles, light, material, probe, rope, renderer) name their owner transform by parentId (4.2,
    // stated there for kind 9) and are matched to the owner's components of that type by their order in the table.
    // T is latched in preUpdate, so one frame applies one replay time even when T is set mid-frame (bridge commands).
    // Seen active (2.4.4 quiet rule): in the recording's own session the recording itself proves an object it shows active
    // in hierarchy at T was active in this session (Awake has run); in a fresh session only objects active at Load count.
    // Every-frame writes: in a fresh session every keyed object; in the recording's own session objects with more than one
    // key, objects with an Animation, Animator or Actor on themselves or an ancestor, and boy bones (the rest is written once
    // and drift-checked every 10th frame). In a fresh session lights, MaterialInstances and shared materials are re-asserted
    // every frame too. A path walk with several matches needs one candidate whose sibling chain (root included) and type
    // signature both match, else the object is ambiguous.
    // Additions (internal): baselineDir (replay_baseline.tsv folder, default the parent of the record folder), MonitorJson().
    // Shader state (SceneShaderState.cs, content version 2): recorded globals every frame, material values every frame and
    // property blocks on change, all in preCullMain (after the frozen game writers of Update and LateUpdate), ApplyNow and
    // the final sample; pass isolation puts materials and blocks back to their Begin values (globals cannot be read back).
    // Shared-material swaps keep a live material whose name equals the recorded one.
#if !PLAY_B_MATH_ONLY
    public sealed partial class SceneReplayer : IReplaySource
    {
        public static SceneReplayer current;
        internal static string baselineDir;

        public int conflicts, activations, monitorHits, readbackErrors;
        public ReplayPassInfo pass;
        public SceneTracks tracks;

        const BindingFlags AnyMember = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const double PosTol = 1e-4, RotTol = 2e-4, ReadbackPos = 0.001, ReadbackRot = 0.05 * Math.PI / 180.0;
        const int MaxLog = 20, StreamingWaitFrames = 600;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly object BoxedFalse = false;
        static readonly Func<BoundsCullerRuntime, bool> NeverUpdate = c => false;
        static readonly string[] KindNames = { "xf", "bone", "cloth", "particles", "light", "material", "probe", "spawned", "rope", "renderer" };

        sealed class Obj
        {
            public int id, depth, owner = -1, liveParent = -1, keys, conflicts, culler = -1;
            public byte kind, flags;
            public SrecObject o;
            public Transform t;
            public GameObject go;
            public Component comp;
            public string how = "";
            public bool xf, resolved, ambiguous, local, camXf, camDriven, spawned, seen, animated, every, scaleVariant, readback, written, wl, lastActive, mover;
            public Vector3 wp;
            public Quaternion wq = Quaternion.identity;
            public D11VelocityBufferTag[] tags;
        }

        sealed class Rend
        {
            public Obj x; public Renderer r; public int[] cullers; public bool begin, last, multi; public int slots; public int[] applied; public Material[] snap;
        }

        struct LightValues
        {
            public bool enabled, stat, dirty; public float intensity, range, spotAngle; public Color color; public Texture cookie; public LightShadows shadows;
        }

        sealed class Lite
        {
            public Obj x; public Light l; public int[] cullers; public bool last, multi; public LightValues begin;
        }

        sealed class Parts
        {
            public Obj x; public ParticleSystem ps; public bool recorded, wasPlaying, wasPaused; public byte[] raw, work; public ParticleSystem.Particle[] buf;
            public int cachedSample = -1, cachedN, lastN = -1;
        }

        sealed class Mat
        {
            public Obj x; public MaterialInstance mi; public bool multi; public int added;
            public MaterialInstance.FloatProperty[] f; public MaterialInstance.ColorProperty[] c; public MaterialInstance.VectorProperty[] v; public MaterialInstance.TextureProperty[] tx;
            public float[] fs; public Color[] cs; public Vector4[] vs; public Texture[] ts;
        }

        sealed class RopeItem
        {
            public Obj x; public RopeLineRenderer rlr; public object blend; public Array verts; public Vector3[] buf, snap; public object[] args; public int bottom = -1;
        }

        sealed class ProbeItem
        {
            public Obj x; public ColorBlendProbe p; public bool useBefore; public Vector3 overrideBefore, snap;
        }

        sealed class CullerItem
        {
            public BoundsCullerRuntime b; public int id = -1; public bool validBefore, ours, handed, vis = true, light, forced;
        }

        sealed class Body
        {
            public Rigidbody rb; public bool kinematic; public RigidbodyInterpolation interp; public Vector3 v, av;
        }

        sealed class Activation
        {
            public Obj x; public bool root; public string mode; public List<Behaviour> off;
        }

        sealed class FsmWatch
        {
            public PlayMakerFSM f; public string state; public bool finished;
        }

        sealed class MeshSwap
        {
            public int id; public SkinnedMeshRenderer smr; public Mesh begin, want; public string applied;
        }

        PlaySpec spec;
        string dir = "";
        SceneRecording rec;
        SceneRecorder sessionRec;
        RecordHeader header;
        bool velocitiesFinal, sameSession, begun, ended, fullApply, hasLastRender, particlesOn = true, streamingPending, cullingSaved, cullingBefore, cameraTaken, timeSaved, holding, monitorOn, passOpen, cullersLive;
        double t, req, lastRenderT, dur, realBase;
        float timeBefore = 1f, gmBefore = 1f;
        int idMismatch, frames, timeLeaks, pendingFrames, resolvedCount, unresolved, unresolvedSpawned, unresolvedStatic, ambiguous, destroyed, visConflicts, promoted, missingTex, missingMat, ropeErrors, particleErrors, quietDisabled, passIndex = -1;
        int monSignals, monSounds, monSoundsExcluded, monFsm, monFinished, monCullerSets, monSubscenes, camToggles, monLogged;
        readonly List<object> monHits = new List<object>();
        long evSeen;
        Obj[] objs;
        readonly List<Obj> xfObjs = new List<Obj>(), everyList = new List<Obj>(), onceList = new List<Obj>(), allList = new List<Obj>(), rootList = new List<Obj>(), cutList = new List<Obj>(), candidates = new List<Obj>(), camWatch = new List<Obj>();
        readonly List<Rend> rends = new List<Rend>();
        readonly List<Lite> lites = new List<Lite>();
        readonly List<Parts> parts = new List<Parts>();
        readonly List<Mat> mats = new List<Mat>();
        readonly List<RopeItem> ropes = new List<RopeItem>();
        readonly List<ProbeItem> probes = new List<ProbeItem>();
        readonly List<CullerItem> cullers = new List<CullerItem>();
        readonly List<Body> bodies = new List<Body>();
        readonly List<Activation> activated = new List<Activation>();
        readonly List<FsmWatch> fsms = new List<FsmWatch>();
        readonly List<MeshSwap> meshSwaps = new List<MeshSwap>();
        Dictionary<string, Mesh> meshByName;
        Mesh[] allMeshes;
        bool meshScanned;
        int missingMesh;
        readonly List<Behaviour> disabledNeutral = new List<Behaviour>(), neutralCandidates = new List<Behaviour>();
        readonly List<CrowdDude> crowdCandidates = new List<CrowdDude>();
        readonly List<KeyValuePair<CrowdDude, int>> crowds = new List<KeyValuePair<CrowdDude, int>>();
        readonly List<SubsceneCuller> keptOwned = new List<SubsceneCuller>();
        readonly List<string> never = new List<string>();
        readonly HashSet<int> neverIds = new HashSet<int>(), setGos = new HashSet<int>();
        readonly Dictionary<int, int> xfByInstance = new Dictionary<int, int>();
        readonly Dictionary<int, Parts> partsByInstance = new Dictionary<int, Parts>();
        readonly Dictionary<int, Texture> texById = new Dictionary<int, Texture>();
        readonly Dictionary<int, Material> matById = new Dictionary<int, Material>();
        Dictionary<string, Texture> texByName;
        Dictionary<string, Material> matByName;
        bool texScanned, matScanned;
        Activation[] activeBy;
        bool[] cullerVis = new bool[0];
        int[] xfIds = new int[0], parA, stack;
        bool[] selfA, aboveA, nodeA;
        sbyte[] aih;
        Dictionary<string, List<Transform>> rootsByName, byPath;
        FieldInfo[] fogFields = new FieldInfo[0];
        readonly SrecVirtual virt = new SrecVirtual();
        Action onPreUpdate, onLateWrite;
        Action<Camera> onPreCull;
        Action<BoundsCullerRuntime, bool> onCullerSet;
        Action<SubsceneCuller> onSubsceneActivate;

        static FieldInfo fBlend, fVerts, fPos, fPost, fLine, fMeshBones, fHandle;
        static MethodInfo mDraw;
        static Type tMode;

        // ------------------------------------------------------------------ IReplaySource

        public bool active { get { return begun && !ended; } }

        public double duration { get { return dur; } }

        internal bool StreamingPending { get { return streamingPending; } }

        public int sampleCount { get { return rec != null ? rec.sampleCount : 0; } }

        public double T { get { return req; } set { req = value; if (!active) t = value; } }

        public double SampleTime(int i)
        {
            if (rec == null || rec.sampleCount <= 0 || rec.times == null) return 0.0;
            return rec.times[Mathf.Clamp(i, 0, rec.sampleCount - 1)];
        }

        public int SampleAt(double time) { return tracks != null ? tracks.SampleAt(time) : 0; }

        public bool GameCam(double time, GameCamState g)
        {
            if (g == null || tracks == null || !tracks.Virtual(time, virt)) return false;
            CopyGame(virt.game, g);
            return g.ok;
        }

        public bool Character(string who, double time, out Vector3 pos3, out Vector3 vel3, out string state)
        {
            pos3 = Vector3.zero; vel3 = Vector3.zero; state = "";
            if (tracks == null || !tracks.Virtual(time, virt)) return false;
            if (who == "boy" && (virt.flags & SrecConst.VirtBoy) != 0) { pos3 = virt.boyPos3; vel3 = virt.boyVel3; state = virt.boyState ?? ""; return true; }
            if (who == "huddle" && (virt.flags & SrecConst.VirtHuddle) != 0) { pos3 = virt.huddlePos3; vel3 = virt.huddleVel3; state = virt.huddleState ?? ""; return true; }
            return false;
        }

        public bool Probe(double time, out Vector3 probe)
        {
            probe = Vector3.zero;
            if (tracks == null || !tracks.Virtual(time, virt)) return false;
            probe = virt.probe;
            return true;
        }

        public double RealTimeAt(double time)
        {
            if (rec == null || rec.sampleCount <= 0) return 0.0;
            return Calc.Interp(rec.times, rec.realTimes, rec.sampleCount, time) - realBase;
        }

        public long LogicStepAt(double time)
        {
            if (rec == null || rec.sampleCount <= 0 || rec.logicSteps == null) return 0;
            return rec.logicSteps[Mathf.Clamp(SampleAt(time), 0, rec.sampleCount - 1)];
        }

        public double EventTime(string name) { return tracks != null ? tracks.EventTime(name) : double.NaN; }

        static void CopyGame(GameCamState s, GameCamState d)
        {
            d.ok = s.ok; d.step = s.step; d.rendered = s.rendered; d.clean = s.clean; d.positionCamera = s.positionCamera; d.positionAim = s.positionAim;
            d.positionUp = s.positionUp; d.velocity = s.velocity; d.aimDistance = s.aimDistance; d.box = s.box; d.probe = s.probe; d.extrapolation = s.extrapolation;
            d.handOffset = s.handOffset; d.handRot = s.handRot; d.shakeOffset = s.shakeOffset; d.shakeRot = s.shakeRot; d.cut = s.cut;
        }

        // ------------------------------------------------------------------ load and resolve (2.4.1)

        public static SceneReplayer Load(string recordDir, PlaySpec spec, SpecReport report)
        {
            if (string.IsNullOrEmpty(recordDir)) { Err(report, "replay.source", "no recording folder"); return null; }
            if (SceneRecorder.active != null) { Err(report, "replay", "a recording is running (srec record stop / mcam stop first)"); return null; }
            string path = Path.Combine(recordDir, ReplayConst.RecordFile);
            if (!File.Exists(path)) { Err(report, "replay.source", "no recording at " + path); return null; }
            var r = new SceneReplayer();
            r.spec = spec ?? new PlaySpec();
            r.dir = recordDir;
            SceneTracks tr;
            try { tr = SceneTracks.Load(path, r.spec.interp, report); }
            catch (Exception e) { Err(report, "replay.source", "reading " + path + " failed: " + e.GetType().Name + ": " + e.Message); return null; }
            if (tr == null || tr.rec == null) { Err(report, "replay.source", "recording unreadable: " + path); return null; }
            r.tracks = tr;
            r.rec = tr.rec;
            r.header = tr.rec.header ?? new RecordHeader();
            if (r.rec.sampleCount <= 0 || r.rec.times == null) { Err(report, "replay.source", "recording has no samples: " + path); return null; }
            int errorsBefore = report != null ? report.errors.Count : 0;
            if (r.rec.truncated) Warn(report, "replay.source", "recording truncated (no footer): replaying the readable prefix of " + r.rec.sampleCount + " samples");
            r.dur = r.rec.times[r.rec.sampleCount - 1];
            r.realBase = Calc.RealBase(r.rec.realTimes, r.rec.sampleCount, r.header.t0Real);
            try { r.sessionRec = SceneRecorder.SessionRecording(recordDir); } catch { r.sessionRec = null; }
            r.sameSession = r.sessionRec != null;
            try { r.Resolve(report); }
            catch (Exception e) { Err(report, "replay", "resolve failed: " + e.GetType().Name + ": " + e.Message); DevLog.Error("replay resolve", e); return null; }
            if (report != null && report.errors.Count > errorsBefore) return null;
            if (!r.sameSession) { try { r.Baseline(); } catch (Exception e) { DevLog.Error("replay baseline", e); } }
            Log("loaded " + recordDir + ": " + r.rec.sampleCount + " samples, " + Num(r.dur) + " s, " + r.objs.Length + " objects, " + r.resolvedCount + " resolved" +
                (r.sameSession ? " (same session" + (r.idMismatch > 0 ? ", " + r.idMismatch + " instance id mismatches re-resolved by path" : "") + ")" : " (fresh session)") + ", unresolved dynamic " + r.unresolved + ", static " + r.unresolvedStatic + ", spawned " + r.unresolvedSpawned + ", ambiguous " + r.ambiguous);
            return r;
        }

        static bool SameDir(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            string fa = Full(a), fb = Full(b);
            return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase) || string.Equals(Full(Path.Combine(a, ReplayConst.RecordFolder)), fb, StringComparison.OrdinalIgnoreCase);
        }

        static string Full(string p)
        {
            try { return Path.GetFullPath(p).Replace('\\', '/').TrimEnd('/'); } catch { return p.Replace('\\', '/').TrimEnd('/'); }
        }

        static bool IsXfKind(byte k) { return k == SrecConst.KindTransform || k == SrecConst.KindBoyBone || k == SrecConst.KindClothBone || k == SrecConst.KindSpawned; }

        static Type ComponentType(byte k)
        {
            switch (k)
            {
                case SrecConst.KindParticles: return typeof(ParticleSystem);
                case SrecConst.KindLight: return typeof(Light);
                case SrecConst.KindMaterial: return typeof(MaterialInstance);
                case SrecConst.KindProbe: return typeof(ColorBlendProbe);
                case SrecConst.KindRope: return typeof(RopeLineRenderer);
                case SrecConst.KindRenderer: return typeof(Renderer);
            }
            return null;
        }

        void Resolve(SpecReport report)
        {
            var list = rec.objects;
            int n = list.Count;
            objs = new Obj[n];
            for (int i = 0; i < n; i++)
            {
                var o = list[i] ?? new SrecObject();
                var x = new Obj { id = i, o = o, kind = o.kind, flags = o.flags, depth = o.depth, xf = IsXfKind(o.kind) };
                x.local = (o.flags & SrecConst.ObjLocal) != 0;
                x.camDriven = (o.flags & SrecConst.ObjCamDriven) != 0;
                x.scaleVariant = (o.flags & SrecConst.ObjScaleVariant) != 0;
                x.spawned = (o.flags & SrecConst.ObjSpawned) != 0 || o.kind == SrecConst.KindSpawned || o.addedAt >= 0 || (o.owner ?? "").StartsWith("spawned:", StringComparison.Ordinal);
                try { x.keys = tracks.Keys(i); } catch { x.keys = 0; }
                objs[i] = x;
            }
            for (int i = 0; i < n; i++)
            {
                var x = objs[i];
                for (int p = i, guard = 0; p >= 0 && p < n && guard < 256; p = objs[p].o.parentId, guard++)
                    if ((objs[p].flags & SrecConst.ObjCamXf) != 0) { x.camXf = true; break; }
            }
            SceneRecorder last = null;
            if (sameSession) last = sessionRec;
            for (int i = 0; i < n; i++)
            {
                var x = objs[i];
                if (!x.xf) continue;
                xfObjs.Add(x);
                Transform tr = null;
                if (last != null)
                {
                    tr = AsTransform(SafeRef(last, i));
                    if (tr != null && x.o.instanceId != 0 && tr.GetInstanceID() != x.o.instanceId && tr.gameObject.GetInstanceID() != x.o.instanceId) { idMismatch++; tr = null; }
                    if (tr != null) x.how = "session";
                }
                if (tr == null) tr = ResolveTransform(x);
                if (tr != null)
                {
                    x.t = tr; x.go = tr.gameObject; x.resolved = true; resolvedCount++;
                    xfByInstance[tr.GetInstanceID()] = i;
                    setGos.Add(x.go.GetInstanceID());
                }
            }
            var ordinal = new Dictionary<long, int>();
            for (int i = 0; i < n; i++)
            {
                var x = objs[i];
                if (x.xf) continue;
                var type = ComponentType(x.kind);
                int op = x.o.parentId;
                x.owner = op >= 0 && op < n && objs[op].xf ? op : -1;
                long key = ((long)x.owner << 8) | x.kind;
                int ord;
                ordinal.TryGetValue(key, out ord);
                ordinal[key] = ord + 1;
                if (type == null) continue;
                Component c = null;
                if (last != null) { c = SafeRef(last, i) as Component; if (c != null && !type.IsInstanceOfType(c)) c = c.GetComponent(type); if (c != null) x.how = "session"; }
                if (c == null)
                {
                    string ownerPath, typeName;
                    int typeIndex;
                    SplitComponentPath(x.o.path, out ownerPath, out typeName, out typeIndex);
                    Transform ot = x.owner >= 0 ? objs[x.owner].t : null;
                    if (ot == null && x.owner < 0 && ownerPath.Length > 0)
                    {
                        var ox = new Obj { o = new SrecObject { path = ownerPath, ordinals = x.o.ordinals, siblings = x.o.siblings } };
                        ot = ResolveTransform(ox);
                    }
                    if (ot != null)
                    {
                        if (typeName.Length > 0) c = NthComponent(ot, typeName, typeIndex, type);
                        if (c != null) x.how = "owner::" + typeName + (typeIndex > 0 ? "[" + typeIndex + "]" : "");
                        else
                        {
                            var arr = ot.GetComponents(type);
                            if (ord < arr.Length) { c = arr[ord]; x.how = "owner+" + ord; }
                        }
                    }
                }
                if (c != null) { x.comp = c; x.t = c.transform; x.go = c.gameObject; x.resolved = true; resolvedCount++; }
            }
            foreach (var x in xfObjs)
            {
                if (x.t == null) continue;
                var pt = x.t.parent;
                int pid;
                x.liveParent = pt != null && xfByInstance.TryGetValue(pt.GetInstanceID(), out pid) ? pid : -1;
                bool seenLive = false;
                try { seenLive = x.go.activeInHierarchy; } catch { }
                x.seen = seenLive || sameSession;
                for (var a = x.t; a != null && !x.animated; a = a.parent)
                    if (a.GetComponent<Animation>() != null || a.GetComponent<Animator>() != null || a.GetComponent<Actor>() != null) x.animated = true;
            }
            var bad = new List<string>();
            int dynamic = 0;
            foreach (var x in objs)
            {
                if (x.camXf) continue;
                if (x.spawned) { if (!x.resolved) unresolvedSpawned++; continue; }
                if (x.keys > 1) dynamic++;
                if (x.resolved) continue;
                if (x.keys > 1) { unresolved++; bad.Add(x.id + " " + KindName(x.kind) + " " + x.o.path + (x.ambiguous ? " (ambiguous)" : "")); }
                else unresolvedStatic++;
            }
            foreach (var s in bad) DevLog.Write("[replay] unresolved: " + s);
            for (int i = 0; i < bad.Count && i < 50; i++) Warn(report, "replay.resolve", "unresolved " + bad[i]);
            if (bad.Count > 50) Warn(report, "replay.resolve", (bad.Count - 50) + " more unresolved objects (log.txt / insidedev.log)");
            if (unresolvedSpawned > 0) Note(report, "replay.resolve", unresolvedSpawned + " spawned objects not in this session (not counted against play.maxUnresolved)");
            double frac = dynamic > 0 ? unresolved / (double)dynamic : 0.0;
            if (frac > spec.maxUnresolved)
                Err(report, "replay.play.maxUnresolved", unresolved + " of " + dynamic + " dynamic objects unresolved or ambiguous (" + Num(frac * 100.0) + " % > " + Num(spec.maxUnresolved * 100.0) + " %)");
            int count = xfObjs.Count;
            xfIds = new int[count];
            for (int i = 0; i < count; i++) xfIds[i] = xfObjs[i].id;
            parA = new int[n]; selfA = new bool[n]; aboveA = new bool[n]; nodeA = new bool[n]; aih = new sbyte[n]; stack = new int[Math.Max(16, n)];
            for (int i = 0; i < n; i++) { parA[i] = -1; nodeA[i] = objs[i].xf; }
            activeBy = new Activation[n];
        }

        static void SplitComponentPath(string path, out string owner, out string typeName, out int index)
        {
            owner = path ?? ""; typeName = ""; index = 0;
            int k = owner.LastIndexOf("::", StringComparison.Ordinal);
            if (k < 0) return;
            string tail = owner.Substring(k + 2);
            owner = owner.Substring(0, k);
            int b = tail.IndexOf('[');
            if (b > 0 && tail.EndsWith("]", StringComparison.Ordinal))
            {
                int.TryParse(tail.Substring(b + 1, tail.Length - b - 2), NumberStyles.Integer, IC, out index);
                tail = tail.Substring(0, b);
            }
            typeName = tail;
        }

        static Component NthComponent(Transform owner, string typeName, int index, Type kindType)
        {
            int seen = 0;
            foreach (var c in owner.GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != typeName || !kindType.IsInstanceOfType(c)) continue;
                if (seen == index) return c;
                seen++;
            }
            return null;
        }

        static UObj SafeRef(SceneRecorder r, int id)
        {
            try { return r.Ref(id); } catch { return null; }
        }

        static Transform AsTransform(UObj o)
        {
            if (o == null) return null;
            var tr = o as Transform;
            if (tr != null) return tr;
            var g = o as GameObject;
            if (g != null) return g.transform;
            var c = o as Component;
            return c != null ? c.transform : null;
        }

        Transform ResolveTransform(Obj x)
        {
            var o = x.o;
            if (!string.IsNullOrEmpty(o.owner))
            {
                var ot = OwnerRef(o.owner, o.path);
                if (ot != null) { x.how = "owner"; return ot; }
            }
            if (string.IsNullOrEmpty(o.path)) return null;
            bool amb;
            var w = Walk(o, out amb);
            if (w != null) { x.how = "path"; return w; }
            var l = PathList(o.path);
            if (l != null && l.Count == 1) { x.how = "exact"; return l[0]; }
            if (amb || (l != null && l.Count > 1)) { x.ambiguous = true; ambiguous++; }
            return null;
        }

        static Transform OwnerRef(string owner, string path)
        {
            try
            {
                if (owner.StartsWith("boy:bone:", StringComparison.Ordinal))
                {
                    var b = ScriptGlobals.boy;
                    var vb = b != null ? b.visualBoy : null;
                    var a = vb != null ? vb.actor : null;
                    if (a == null) return null;
                    int i = a.GetBone(owner.Substring(9));
                    return i >= 0 ? a.bones[i].transform : null;
                }
                if (owner.StartsWith("huddle:cloth:", StringComparison.Ordinal))
                {
                    int i;
                    if (!int.TryParse(owner.Substring(13), NumberStyles.Integer, IC, out i)) return null;
                    var h = ScriptGlobals.huddle;
                    var cloth = h != null ? h.cloth : null;
                    var bones = cloth != null ? cloth.bones : null;
                    if (bones == null || i < 0 || i >= bones.Length || bones[i] == null) return null;
                    var vt = bones[i].visualTransform;
                    if (vt == null) return null;
                    string lastName = LastSegment(path);
                    return lastName.Length == 0 || vt.name == lastName ? vt : null;
                }
            }
            catch (Exception e) { DevLog.Error("replay owner " + owner, e); }
            return null;
        }

        static string LastSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int k = path.LastIndexOf('/');
            return k >= 0 ? path.Substring(k + 1) : path;
        }

        void IndexRoots()
        {
            if (rootsByName != null) return;
            rootsByName = new Dictionary<string, List<Transform>>();
            foreach (var tr in Inspector.AllSceneTransforms())
            {
                if (tr == null || tr.parent != null) continue;
                List<Transform> l;
                if (!rootsByName.TryGetValue(tr.name, out l)) rootsByName[tr.name] = l = new List<Transform>();
                l.Add(tr);
            }
        }

        List<Transform> PathList(string path)
        {
            if (byPath == null)
            {
                byPath = new Dictionary<string, List<Transform>>();
                foreach (var tr in Inspector.AllSceneTransforms())
                {
                    if (tr == null) continue;
                    string p = Inspector.PathOf(tr);
                    List<Transform> l;
                    if (!byPath.TryGetValue(p, out l)) byPath[p] = l = new List<Transform>();
                    l.Add(tr);
                }
            }
            List<Transform> r;
            return byPath.TryGetValue(path, out r) ? r : null;
        }

        Transform Walk(SrecObject o, out bool amb)
        {
            amb = false;
            var names = o.path.Split('/');
            if (o.ordinals == null || o.ordinals.Count != names.Length) return null;
            IndexRoots();
            List<Transform> roots;
            if (!rootsByName.TryGetValue(names[0], out roots)) return null;
            Transform best = null, strongBest = null;
            int good = 0, strong = 0;
            for (int r = 0; r < roots.Count; r++)
            {
                var cur = roots[r];
                for (int k = 1; k < names.Length && cur != null; k++) cur = NthChild(cur, names[k], o.ordinals[k]);
                if (cur == null) continue;
                bool sib = SiblingsMatch(cur, o.siblings), types = TypesMatch(cur, o.types);
                if (!sib && !types) continue;
                good++;
                if (best == null) best = cur;
                bool rootSib = o.siblings != null && o.siblings.Count > 0 && roots[r].GetSiblingIndex() == o.siblings[0];
                if (sib && types && rootSib) { strong++; strongBest = cur; }
            }
            if (good <= 1) return best;
            if (strong == 1) return strongBest;
            amb = true;
            return null;
        }

        static Transform NthChild(Transform p, string name, int ordinal)
        {
            int seen = 0, n = p.childCount;
            for (int i = 0; i < n; i++)
            {
                var c = p.GetChild(i);
                if (c.name != name) continue;
                if (seen == ordinal) return c;
                seen++;
            }
            return null;
        }

        static bool SiblingsMatch(Transform tr, List<int> chain)
        {
            if (chain == null || chain.Count == 0) return false;
            int k = chain.Count - 1;
            for (var a = tr; a != null && k >= 1; a = a.parent, k--)
                if (a.GetSiblingIndex() != chain[k]) return false;
            return k <= 0;
        }

        static bool TypesMatch(Transform tr, List<string> types)
        {
            if (types == null) return false;
            var live = new List<string>();
            foreach (var mb in tr.GetComponents<MonoBehaviour>()) if (mb != null) live.Add(mb.GetType().Name);
            if (live.Count != types.Count) return false;
            var want = new List<string>(types);
            live.Sort(string.CompareOrdinal);
            want.Sort(string.CompareOrdinal);
            for (int i = 0; i < live.Count; i++) if (live[i] != want[i]) return false;
            return true;
        }

        void Baseline()
        {
            double t0 = rec.times[0];
            var rows = new List<KeyValuePair<double, string>>();
            foreach (var x in xfObjs)
            {
                if (!x.resolved || x.camXf || x.spawned || x.t == null) continue;
                Vector3 p, s, lp; Quaternion q, lq;
                if (tracks.Xf(x.id, t0, out p, out q, out s))
                {
                    if (x.local) x.t.GetLocalPositionRotation(out lp, out lq); else x.t.GetPositionRotation(out lp, out lq);
                    double dp = (lp - p).magnitude, dr = Calc.Angle(lq, q) * 180.0 / Math.PI;
                    if (dp > 0.01) rows.Add(new KeyValuePair<double, string>(dp, "position\t" + V(lp) + "\t" + V(p) + "\t" + x.o.path));
                    if (dr > 1.0) rows.Add(new KeyValuePair<double, string>(dr / 10.0, "rotation\t" + Num(dr) + " deg\t0\t" + x.o.path));
                }
                if (x.camDriven) continue;
                bool rs = (tracks.Flags(x.id, t0) & SrecConst.FlActive) != 0;
                if (rs != x.go.activeSelf) rows.Add(new KeyValuePair<double, string>(1.0, "activeSelf\t" + x.go.activeSelf + "\t" + rs + "\t" + x.o.path));
            }
            foreach (var x in objs)
            {
                if (!x.resolved || x.camXf) continue;
                if (x.kind == SrecConst.KindRenderer)
                {
                    var r = x.comp as Renderer;
                    if (r == null) continue;
                    bool re = (tracks.Flags(x.id, t0) & SrecConst.FlRenderer) != 0;
                    if (re != r.enabled) rows.Add(new KeyValuePair<double, string>(1.0, "renderer\t" + r.enabled + "\t" + re + "\t" + x.o.path));
                    int mid;
                    if (tracks.SharedMat(x.id, 0, t0, out mid))
                    {
                        string want = Str(mid), have = r.sharedMaterial != null ? r.sharedMaterial.name : "";
                        if (want != null && want != have) rows.Add(new KeyValuePair<double, string>(1.0, "sharedMaterial\t" + have + "\t" + want + "\t" + x.o.path));
                    }
                }
                else if (x.kind == SrecConst.KindLight)
                {
                    var l = x.comp as Light;
                    SrecLight v;
                    if (l == null || !tracks.Light(x.id, t0, out v)) continue;
                    if (v.enabled != l.enabled) rows.Add(new KeyValuePair<double, string>(1.0, "light\t" + l.enabled + "\t" + v.enabled + "\t" + x.o.path));
                    double di = Math.Abs(v.intensity - l.intensity);
                    if (di > 0.01) rows.Add(new KeyValuePair<double, string>(di, "lightIntensity\t" + Num(l.intensity) + "\t" + Num(v.intensity) + "\t" + x.o.path));
                }
            }
            rows.Sort((a, b) => b.Key.CompareTo(a.Key));
            var sb = new StringBuilder("score\tchannel\tlive\trecorded\tpath\n");
            foreach (var r in rows) sb.Append(Num(r.Key)).Append('\t').Append(r.Value).Append('\n');
            string folder = !string.IsNullOrEmpty(baselineDir) ? baselineDir : Path.GetDirectoryName(Full(dir));
            string file = Path.Combine(folder ?? dir, ReplayConst.BaselineFile);
            try { File.WriteAllText(file, sb.ToString()); } catch (Exception e) { DevLog.Error("replay baseline write " + file, e); }
            Log("fresh-session baseline: " + rows.Count + " differences against sample 0 -> " + file);
            for (int i = 0; i < rows.Count && i < 50; i++) DevLog.Write("[replay] baseline " + Num(rows[i].Key) + "  " + rows[i].Value.Replace('\t', ' '));
        }

        // ------------------------------------------------------------------ freeze and neutralize (2.4.2)

        public void Begin(ReplayHandover handover, SpecReport report)
        {
            if (begun) { Warn(report, "replay", "Begin called twice"); return; }
            if (ended) throw new InvalidOperationException("SceneReplayer: this replay has ended");
            if (objs == null) throw new InvalidOperationException("SceneReplayer: not loaded");
            if (current != null && current != this && current.active) throw new InvalidOperationException("another replay is active (" + current.dir + "): end it first");
            cullersLive = spec.cullers == "live";
            Step(report, "time", FreezeTime);
            Step(report, "camera", TakeCamera);
            Step(report, "streaming", () => FreezeStreaming(handover, report));
            Step(report, "components", ScanComponents);
            Step(report, "cullers", () => ForceCullers(handover, report));
            Step(report, "neutralize", Neutralize);
            Step(report, "rigidbodies", FreezeBodies);
            Step(report, "particles", () => PrepareParticles(report));
            Step(report, "probes", OverrideProbes);
            Step(report, "materials", ResolveProps);
            Step(report, "ropes", ResolveRopes);
            Step(report, "fog", ResolveFog);
            Step(report, "lists", BuildLists);
            Step(report, "meshes", BuildMeshes);
            Step(report, "snapshot", Snapshot);
            Step(report, "shader", ResolveShader);
            if (sameSession) Step(report, "write once", () => { foreach (var x in onceList) WriteOne(x, true); });
            if (spec.monitor) Step(report, "monitor", ArmMonitor);
            onPreUpdate = OnPreUpdate; onLateWrite = OnLateWrite; onPreCull = OnPreCull;
            SceneHooks.preUpdate += onPreUpdate;
            SceneHooks.lateWrite += onLateWrite;
            SceneHooks.preCullMain += onPreCull;
            SceneHooks.Ensure();
            current = this;
            ReplayHub.source = this;
            begun = true;
            fullApply = true;
            Log("begin: " + everyList.Count + " transforms every frame, " + onceList.Count + " write-once, " + rends.Count + " renderers, " + lites.Count + " lights, " +
                parts.Count + " particle systems, " + mats.Count + " material instances, " + (shGlobals.Count + shMats.Count + shBlocks.Count) + " shader values, " + ropes.Count + " ropes, " + probes.Count + " probes, " + cullers.Count + " cullers (" +
                spec.cullers + "), " + bodies.Count + " bodies frozen, " + disabledNeutral.Count + " controllers disabled, " + crowds.Count + " CrowdDudes removed, " +
                candidates.Count + " inactive candidates" + (streamingPending ? ", waiting for kept subscenes" : ""));
        }

        void Step(SpecReport report, string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Err(report, "replay.begin." + what, e.GetType().Name + ": " + e.Message); DevLog.Error("replay begin " + what, e); }
        }

        void FreezeTime()
        {
            timeBefore = Time.timeScale;
            try { gmBefore = GameManager.TimeScale; } catch { gmBefore = timeBefore; }
            timeSaved = true;
            SetTime(0f);
        }

        static void SetTime(float v)
        {
            Time.timeScale = v;
            try { GameManager.TimeScale = v; } catch { }
        }

        void ReforceTime()
        {
            if (Time.timeScale != 0f)
            {
                timeLeaks++;
                if (timeLeaks <= 3) Log("timeScale leak " + Time.timeScale.ToString("0.###", IC) + " (re-forced 0)");
                Time.timeScale = 0f;
            }
            try { if (GameManager.TimeScale != 0f) GameManager.TimeScale = 0f; } catch { }
        }

        void TakeCamera()
        {
            if (CameraControl.free) return;
            CameraControl.SetFree(true);
            cameraTaken = CameraControl.free;
        }

        static SubsceneCuller FindSubscene(string name)
        {
            var m = SubsceneCullingManager.instance;
            SubsceneCuller c = null;
            try { c = m.GetSubsceneCullerByName(name); } catch { }
            if (c != null) return c;
            foreach (var s in m.GetAllSubsceneCullers())
                if (s != null && (s.name == name || s.SceneName == name)) return s;
            return null;
        }

        void FreezeStreaming(ReplayHandover handover, SpecReport report)
        {
            var m = SubsceneCullingManager.instance;
            int handed = 0, added = 0;
            if (handover != null && handover.keptLoaded != null)
                foreach (var c in handover.keptLoaded) if (c != null && !keptOwned.Contains(c)) { keptOwned.Add(c); handed++; }
            var missing = new List<string>();
            if (header.keptCullers != null) foreach (var name in header.keptCullers)
            {
                var c = FindSubscene(name);
                if (c == null) { missing.Add(name); continue; }
                if (keptOwned.Contains(c) || MultiCamSession.IsKept(c)) continue;
                m.AddAlwaysLoaded(c);
                keptOwned.Add(c);
                added++;
            }
            if (missing.Count > 0) Warn(report, "replay.stream", "kept subscenes of the recording not found: " + string.Join(", ", missing.ToArray()));
            cullingBefore = m.CullingActive;
            cullingSaved = true;
            if (KeptReady()) m.CullingActive = false;
            else { streamingPending = true; pendingFrames = 0; }
            Log("streaming: " + keptOwned.Count + " kept subscenes (" + added + " added from the header, " + handed + " handed over), culling " +
                (streamingPending ? "frozen once they are loaded" : "frozen"));
        }

        bool KeptReady()
        {
            foreach (var c in keptOwned) if (c != null && (!c.IsLoaded || c.InProgress)) return false;
            return true;
        }

        void CheckStreaming()
        {
            pendingFrames++;
            bool ready = KeptReady();
            if (!ready && pendingFrames < StreamingWaitFrames) return;
            streamingPending = false;
            try { SubsceneCullingManager.instance.CullingActive = false; } catch (Exception e) { DevLog.Error("replay culling freeze", e); }
            Log(ready ? "kept subscenes loaded after " + pendingFrames + " frames: culling frozen" : "kept subscenes still loading after " + pendingFrames + " frames: culling frozen anyway");
        }

        static bool IsTransitionProbe(Type ty)
        {
            for (var b = ty; b != null && b != typeof(MonoBehaviour); b = b.BaseType)
                if (b.IsGenericType && b.GetGenericTypeDefinition() == typeof(TransitionProbe<>)) return true;
            return false;
        }

        void ScanComponents()
        {
            Transform boyRoot = null, huddleRoot = null;
            try { if (ScriptGlobals.boy != null) boyRoot = ScriptGlobals.boy.transform; } catch { }
            try { if (ScriptGlobals.huddle != null) huddleRoot = ScriptGlobals.huddle.transform; } catch { }
            var cullerIds = new Dictionary<int, int>();
            var probeRoots = new HashSet<int>();
            var tpTypes = new Dictionary<Type, bool>();
            foreach (var x in xfObjs)
            {
                if (!x.resolved || x.go == null || x.camXf) continue;
                if (x.t == boyRoot || x.t == huddleRoot) probeRoots.Add(x.id);
                bool character = false;
                Rigidbody rb = null;
                foreach (var c in x.go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    if (c is Rigidbody) { rb = (Rigidbody)c; continue; }
                    if (c is global::Character) { character = true; continue; }
                    var b = c as BoundsCullerRuntime;
                    if (b != null) { if (!cullerIds.ContainsKey(x.id)) { cullerIds[x.id] = cullers.Count; cullers.Add(new CullerItem { b = b, id = x.id }); } continue; }
                    if (c is HuddleArmControl2 || c is HuddleLegControl) { neutralCandidates.Add((Behaviour)c); continue; }
                    var cd = c as CrowdDude;
                    if (cd != null) { crowdCandidates.Add(cd); continue; }
                    var fsm = c as PlayMakerFSM;
                    if (fsm != null) { fsms.Add(new FsmWatch { f = fsm }); continue; }
                    var ty = c.GetType();
                    bool tp;
                    if (!tpTypes.TryGetValue(ty, out tp)) tpTypes[ty] = tp = IsTransitionProbe(ty);
                    if (tp) probeRoots.Add(x.id);
                }
                if (rb != null && !character) bodies.Add(new Body { rb = rb });
            }
            foreach (var kv in cullerIds) objs[kv.Key].culler = kv.Value;
            var inRoots = new HashSet<int>();
            foreach (var id in probeRoots)
                for (int p = id, guard = 0; p >= 0 && guard < 256; p = objs[p].o.parentId, guard++)
                {
                    if (p >= objs.Length || !objs[p].xf) break;
                    if (objs[p].resolved && !objs[p].camXf) inRoots.Add(p);
                }
            foreach (var id in inRoots) rootList.Add(objs[id]);
            rootList.Sort(ByDepth);
        }

        static int ByDepth(Obj a, Obj b) { return Calc.CompareDepth(a.depth, a.id, b.depth, b.id); }

        void Neutralize()
        {
            foreach (var b in neutralCandidates) if (b != null && b.enabled) { b.enabled = false; disabledNeutral.Add(b); }
            foreach (var cd in crowdCandidates) if (cd != null) RemoveCrowd(cd);
        }

        void RemoveCrowd(CrowdDude cd)
        {
            if (!cd.isActiveAndEnabled) return;
            if (fHandle == null) fHandle = typeof(UmbrellaBehaviour<CrowdDude>).GetField("umbrellaHandle", AnyMember);
            var u = Umbrella<CrowdDude>.instance;
            if (fHandle == null || u == null) return;
            int h = (int)fHandle.GetValue(cd);
            if (h < 0) return;
            u.Remove(h);
            fHandle.SetValue(cd, -1);
            crowds.Add(new KeyValuePair<CrowdDude, int>(cd, h));
        }

        void ForceCullers(ReplayHandover handover, SpecReport report)
        {
            var known = new HashSet<BoundsCullerRuntime>();
            foreach (var c in cullers) known.Add(c.b);
            var handed = new HashSet<BoundsCullerRuntime>();
            if (handover != null && handover.forced != null) foreach (var b in handover.forced) if (b != null) handed.Add(b);
            var extra = new List<BoundsCullerRuntime>(handed);
            if (header.forcedCullers != null && header.forcedCullers.Count > 0 && BoundsCullerRuntime.boundsCullers != null)
            {
                var names = new HashSet<string>(header.forcedCullers);
                foreach (var b in BoundsCullerRuntime.boundsCullers)
                    if (b != null && (names.Contains(b.name) || names.Contains(Inspector.PathOf(b.transform)))) extra.Add(b);
            }
            foreach (var b in extra)
            {
                if (b == null || known.Contains(b)) continue;
                known.Add(b);
                cullers.Add(new CullerItem { b = b });
            }
            foreach (var c in cullers) { c.handed = handed.Contains(c.b); c.validBefore = c.handed || c.b.valid; }
            var forcedNames = header.forcedCullers != null ? new HashSet<string>(header.forcedCullers) : new HashSet<string>();
            int lightCullers = 0;
            foreach (var c in cullers)
            {
                if (c.b == null) continue;
                c.light = c.b.GetComponent<Light>() != null;
                c.forced = forcedNames.Contains(c.b.name) || forcedNames.Contains(Inspector.PathOf(c.b.transform));
                if (c.light) lightCullers++;
            }
            if (lightCullers > 0 && spec.cullers == "camera") Log("cullers: " + lightCullers + " culler(s) on a Light stay on while the recording had them on (camera mode)");
            if (cullersLive)
            {
                foreach (var c in cullers) if (c.handed && !c.b.valid) { c.b.valid = true; c.b.RefreshRuntimeState(); }
                Warn(report, "replay.play.cullers", "cullers: live (diagnostic): groups toggle with SetActive per camera, passes are contaminated");
                cullerVis = new bool[cullers.Count];
                for (int i = 0; i < cullerVis.Length; i++) cullerVis[i] = true;
                return;
            }
            var before = new bool[cullers.Count];
            var zone = new HashSet<BoundsCullerRuntime>();
            int unregistered = 0;
            for (int i = 0; i < cullers.Count; i++)
            {
                var c = cullers[i];
                before[i] = c.b.gameObject.activeSelf;
                zone.Add(c.b);
                if (!c.b.HasValidIndex()) unregistered++;
                if (c.b.valid) { c.b.valid = false; c.ours = true; }
                c.b.RefreshRuntimeState();
            }
            BoundsCullerRuntime.UpdateBoundsCullers(b => zone.Contains(b));
            var act = new List<string>();
            for (int i = 0; i < cullers.Count; i++) if (!before[i] && cullers[i].b.gameObject.activeSelf) act.Add(cullers[i].b.name);
            SceneHooks.UnhookCullers();
            SceneHooks.cullerHolds++;
            holding = true;
            cullerVis = new bool[cullers.Count];
            for (int i = 0; i < cullerVis.Length; i++) cullerVis[i] = true;
            Log("cullers: " + cullers.Count + " zone cullers valid=false (" + handed.Count + " handed over), " + act.Count + " groups activated" + (act.Count > 0 ? ": " + string.Join(", ", act.ToArray()) : "") +
                (unregistered > 0 ? ", " + unregistered + " never registered (inactive since load, not forceable)" : "") + "; UpdateBoundsCullers unhooked");
        }

        // Body velocities saved at Begin are the final-sample velocities only when the game has not advanced since the record's
        // last sample (same session, Time.timeDbl unchanged); otherwise (a fresh session, or the game ran in between) End zeroes
        // them instead of pairing the final poses with unrelated velocities (R7: a chick fell out of the world after unfreeze).
        void FreezeBodies()
        {
            double last = header.t0 + dur;
            velocitiesFinal = sameSession && Math.Abs(Time.timeDbl - last) < 1e-3;
            Log("rigidbodies: " + bodies.Count + " frozen; velocities " + (velocitiesFinal ? "are the record's final ones (restored at End)" : "belong to another state (game time " + Num(Time.timeDbl - header.t0) + " s vs record end " + Num(dur) + " s): zeroed at End"));
            foreach (var b in bodies)
            {
                var rb = b.rb;
                b.kinematic = rb.isKinematic; b.interp = rb.interpolation;
                if (!rb.isKinematic) { b.v = rb.velocity; b.av = rb.angularVelocity; }
                rb.interpolation = RigidbodyInterpolation.None;
                rb.isKinematic = true;
            }
        }

        void PrepareParticles(SpecReport report)
        {
            int size = Marshal.SizeOf(typeof(ParticleSystem.Particle));
            if (size != Calc.ParticleBytes) { particlesOn = false; Err(report, "replay.particles", "ParticleSystem.Particle is " + size + " bytes, expected " + Calc.ParticleBytes + ": particles not replayed"); }
            foreach (var x in objs)
            {
                if (x.kind != SrecConst.KindParticles || !x.resolved || x.camXf) continue;
                var ps = x.comp as ParticleSystem;
                if (ps == null) continue;
                var p = new Parts { x = x, ps = ps };
                try { p.recorded = tracks.HasParticles(x.id); } catch { p.recorded = false; }
                partsByInstance[ps.GetInstanceID()] = p;
                parts.Add(p);
                if (!p.recorded || !particlesOn) continue;
                p.wasPlaying = ps.isPlaying; p.wasPaused = ps.isPaused;
                int max = Math.Max(Math.Max(x.o.maxParticles, ps.maxParticles), 16);
                p.raw = new byte[max * Calc.ParticleBytes];
                p.work = new byte[max * Calc.ParticleBytes];
                p.buf = new ParticleSystem.Particle[max];
                ps.Pause(false);
            }
        }

        void OverrideProbes()
        {
            foreach (var x in objs)
            {
                if (x.kind != SrecConst.KindProbe || !x.resolved || x.camXf) continue;
                var cbp = x.comp as ColorBlendProbe;
                if (cbp == null) continue;
                var p = new ProbeItem { x = x, p = cbp, useBefore = cbp.useOverridePosition, overrideBefore = cbp.overridePosition };
                cbp.overridePosition = cbp.GetCurPos();
                cbp.useOverridePosition = true;
                probes.Add(p);
            }
        }

        void ResolveProps()
        {
            foreach (var x in objs)
            {
                if (x.kind != SrecConst.KindMaterial || !x.resolved || x.camXf || (x.flags & SrecConst.ObjExcludedMaterial) != 0) continue;
                if (x.owner >= 0 && (objs[x.owner].flags & SrecConst.ObjExcludedMaterial) != 0) continue;
                var mi = x.comp as MaterialInstance;
                if (mi == null) continue;
                var m = new Mat { x = x, mi = mi, multi = x.keys > 1 || !sameSession };
                int nf = tracks.PropCount(x.id, SrecConst.MatFloat), nc = tracks.PropCount(x.id, SrecConst.MatColor), nv = tracks.PropCount(x.id, SrecConst.MatVector), nt = tracks.PropCount(x.id, SrecConst.MatTexture);
                m.f = new MaterialInstance.FloatProperty[nf]; m.c = new MaterialInstance.ColorProperty[nc]; m.v = new MaterialInstance.VectorProperty[nv]; m.tx = new MaterialInstance.TextureProperty[nt];
                for (int i = 0; i < nf; i++) { string nm = tracks.PropName(x.id, SrecConst.MatFloat, i); if (nm == null) continue; try { if (!mi.HasFloatProperty(nm)) m.added++; m.f[i] = mi.GetOrAddFloatProperty(nm); } catch { } }
                for (int i = 0; i < nc; i++) { string nm = tracks.PropName(x.id, SrecConst.MatColor, i); if (nm == null) continue; try { if (!mi.HasColorProperty(nm)) m.added++; m.c[i] = mi.GetOrAddColorProperty(nm); } catch { } }
                for (int i = 0; i < nv; i++) { string nm = tracks.PropName(x.id, SrecConst.MatVector, i); if (nm == null) continue; try { if (!mi.HasVectorProperty(nm)) m.added++; m.v[i] = mi.GetOrAddVectorProperty(nm); } catch { } }
                for (int i = 0; i < nt; i++) { string nm = tracks.PropName(x.id, SrecConst.MatTexture, i); if (nm == null) continue; try { if (!mi.HasTextureProperty(nm)) m.added++; m.tx[i] = mi.GetOrAddTextureProperty(nm); } catch { } }
                mats.Add(m);
            }
        }

        static bool RopeReflection()
        {
            if (mDraw != null) return true;
            fBlend = typeof(RopeLineRenderer).GetField("blendRope", AnyMember);
            fLine = typeof(RopeLineRenderer).GetField("lineRenderer", AnyMember);
            fMeshBones = typeof(RopeLineRenderer).GetField("meshBones", AnyMember);
            var tb = fBlend != null ? fBlend.FieldType : null;
            if (tb == null) return false;
            fVerts = tb.GetField("vertices", AnyMember);
            fPost = tb.GetField("postSleepDraw", AnyMember);
            var tv = tb.GetNestedType("BlendVertex", AnyMember);
            fPos = tv != null ? tv.GetField("position", AnyMember) : null;
            tMode = tb.GetNestedType("DrawMode", AnyMember);
            var m = tb.GetMethod("Draw", AnyMember);
            if (fVerts == null || fPost == null || fPos == null || tMode == null || m == null) return false;
            mDraw = m;
            return true;
        }

        void ResolveRopes()
        {
            if (!RopeReflection()) { Log("rope internals not found by reflection: ropes not replayed"); return; }
            foreach (var x in objs)
            {
                if (x.kind != SrecConst.KindRope || !x.resolved || x.camXf) continue;
                var rlr = x.comp as RopeLineRenderer;
                if (rlr == null) continue;
                var item = new RopeItem { x = x, rlr = rlr };
                int bid;
                if (rlr.bottomAttachedObject != null && xfByInstance.TryGetValue(rlr.bottomAttachedObject.transform.GetInstanceID(), out bid)) item.bottom = bid;
                ropes.Add(item);
                if (!StartRope(item)) Log("rope " + x.o.path + ": not started yet (blendRope is created in Start), left enabled until it starts");
            }
        }

        bool StartRope(RopeItem item)
        {
            var rlr = item.rlr;
            var blend = fBlend.GetValue(rlr);
            var verts = blend != null ? fVerts.GetValue(blend) as Array : null;
            if (verts == null || verts.Length == 0) return false;
            int mode = Calc.RopeMode(rlr.meshOutput != null, rlr.lineDivisions);
            object em = Enum.ToObject(tMode, mode);
            item.args = mode == 4
                ? new object[] { em, null, rlr.lineDivisions, rlr.visualCollisionSafety, fMeshBones != null ? fMeshBones.GetValue(rlr) : null, rlr.meshBoneLength }
                : new object[] { em, fLine != null ? fLine.GetValue(rlr) : null, rlr.lineDivisions, rlr.visualCollisionSafety, null, 0.4f };
            item.blend = blend;
            item.verts = verts;
            item.buf = new Vector3[Math.Max(verts.Length, item.x.o.ropeVertices) + 1];
            item.snap = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++) item.snap[i] = (Vector3)fPos.GetValue(verts.GetValue(i));
            if (rlr.enabled) { rlr.enabled = false; disabledNeutral.Add(rlr); }
            return true;
        }

        void ResolveFog()
        {
            var names = header.fogFields ?? new List<string>();
            fogFields = new FieldInfo[names.Count];
            for (int i = 0; i < names.Count; i++) fogFields[i] = typeof(FogAmbientBlendValues).GetField(names[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        void BuildLists()
        {
            moverCache.Clear();
            movers = 0;
            var skinned = new HashSet<int>();
            foreach (var x in objs)
            {
                if (!x.resolved || x.camXf) continue;
                if (x.kind == SrecConst.KindRenderer)
                {
                    var r = x.comp as Renderer;
                    if (r == null) continue;
                    var smr = r as SkinnedMeshRenderer;
                    if (smr != null) { var bones = smr.bones; if (bones != null) foreach (var b in bones) if (b != null) skinned.Add(b.GetInstanceID()); }
                    int slots = Math.Max(x.o.materialSlots, 0);
                    rends.Add(new Rend { x = x, r = r, cullers = Chain(x.owner), multi = x.keys > 1 || !sameSession, slots = slots, applied = new int[slots] });
                }
                else if (x.kind == SrecConst.KindLight)
                {
                    var l = x.comp as Light;
                    if (l != null) lites.Add(new Lite { x = x, l = l, cullers = Chain(x.owner), multi = x.keys > 1 || !sameSession });
                }
            }
            foreach (var x in xfObjs)
            {
                if (!x.resolved || x.camXf || x.t == null) continue;
                x.every = !sameSession || x.keys > 1 || x.animated || x.kind == SrecConst.KindBoyBone;
                x.readback = x.kind != SrecConst.KindBoyBone && x.kind != SrecConst.KindClothBone && !skinned.Contains(x.t.GetInstanceID());
                x.mover = MovedByGame(x.t, moverCache);
                if (x.mover) movers++;
                (x.every ? everyList : onceList).Add(x);
                allList.Add(x);
                try { if (tracks.CutBetween(x.id, -1.0, dur + 1.0)) cutList.Add(x); } catch { }
                if (x.camDriven) { if (x.culler < 0) camWatch.Add(x); continue; }
                if (!x.go.activeSelf) candidates.Add(x);
            }
            everyList.Sort(ByDepth); onceList.Sort(ByDepth); allList.Sort(ByDepth); candidates.Sort(ByDepth);
        }

        readonly Dictionary<int, bool> moverCache = new Dictionary<int, bool>();
        int movers;

        static bool MovedByGame(Transform t, Dictionary<int, bool> cache)
        {
            if (t == null) return false;
            int id = t.GetInstanceID();
            bool r;
            if (cache.TryGetValue(id, out r)) return r;
            r = false;
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                if (c is Rigidbody || c is Animation || c is Animator || SceneRecorder.XfMath.MoverType(c.GetType().Name)) { r = true; break; }
            }
            if (!r) r = MovedByGame(t.parent, cache);
            cache[id] = r;
            return r;
        }

        int[] Chain(int owner)
        {
            var l = new List<int>();
            for (int p = owner, guard = 0; p >= 0 && p < objs.Length && guard < 256; p = objs[p].o.parentId, guard++)
            {
                if (!objs[p].xf) break;
                if (objs[p].culler >= 0) l.Add(objs[p].culler);
            }
            return l.Count > 0 ? l.ToArray() : null;
        }

        void Snapshot()
        {
            foreach (var r in rends)
            {
                r.begin = r.last = r.r.enabled;
                for (int i = 0; i < r.applied.Length; i++) r.applied[i] = -2;
                if (r.multi && r.slots > 1) r.snap = r.r.sharedMaterials;
                else if (r.multi && r.slots == 1) r.snap = new[] { r.r.sharedMaterial };
                SeedMaterials(r.r);
            }
            foreach (var l in lites)
            {
                var v = new LightValues();
                v.enabled = l.l.enabled; v.intensity = l.l.intensity; v.color = l.l.color; v.range = l.l.range; v.spotAngle = l.l.spotAngle; v.cookie = l.l.cookie;
                v.stat = l.l.useStaticShadows; v.dirty = l.l.staticShadowsDirty; v.shadows = l.l.shadows;
                l.begin = v; l.last = v.enabled;
                if (v.cookie != null) SeedTexture(v.cookie);
            }
            foreach (var m in mats)
            {
                m.fs = new float[m.f.Length]; m.cs = new Color[m.c.Length]; m.vs = new Vector4[m.v.Length]; m.ts = new Texture[m.tx.Length];
                for (int i = 0; i < m.f.Length; i++) if (m.f[i] != null) m.fs[i] = m.mi.GetFloatValue(m.f[i]);
                for (int i = 0; i < m.c.Length; i++) if (m.c[i] != null) m.cs[i] = m.mi.GetColorValue(m.c[i]);
                for (int i = 0; i < m.v.Length; i++) if (m.v[i] != null) m.vs[i] = m.mi.GetVectorValue(m.v[i]);
                for (int i = 0; i < m.tx.Length; i++) if (m.tx[i] != null) { m.ts[i] = m.mi.GetTextureValue(m.tx[i]); if (m.ts[i] != null) SeedTexture(m.ts[i]); }
            }
            foreach (var p in probes) p.snap = p.p.overridePosition;
        }

        void ArmMonitor()
        {
            evSeen = EventMonitor.total;
            foreach (var w in fsms) { try { w.state = w.f.ActiveStateName; w.finished = w.f.Fsm != null && w.f.Fsm.Finished; } catch { } }
            foreach (var x in camWatch) x.lastActive = x.go != null && x.go.activeSelf;
            onCullerSet = OnCullerSet;
            onSubsceneActivate = OnSubsceneActivate;
            BoundsCullerRuntime.staticSetVisible += onCullerSet;
            SubsceneCuller.StaticEvents.postActivate += onSubsceneActivate;
            monitorOn = true;
        }

        void OnCullerSet(BoundsCullerRuntime b, bool visible)
        {
            if (!active) return;
            monCullerSets++;
            string who = "?";
            try { who = b != null ? Inspector.PathOf(b.transform) : "(null)"; } catch { }
            MonitorHit("culler SetActive", Time.frameCount, who, "visible " + visible);
        }

        void OnSubsceneActivate(SubsceneCuller c)
        {
            if (!active) return;
            monSubscenes++;
            string who = "?";
            try { who = c != null ? c.SceneName : "(null)"; } catch { }
            MonitorHit("subscene activation", Time.frameCount, who, "");
        }

        // Every monitor hit is named (kind, frame, pass, replay T, object, event text) in log.txt and run.json monitor.hits;
        // the GlobalAudio 'pause' and 'resume' posts are allowed by R10 and counted apart (soundPostsExcluded).
        void MonitorHit(string kind, int frame, string obj, string text)
        {
            monLogged++;
            if (monLogged <= MaxLog * 2 || monLogged % 100 == 0)
                Log("monitor: " + kind + " at frame " + frame + ", pass " + passIndex + (pass != null ? " " + pass.camera : "") + ", T " + Num(t) + ": " + obj + (string.IsNullOrEmpty(text) ? "" : " | " + text));
            if (monHits.Count >= 50) return;
            var h = new Json.Obj();
            h["kind"] = kind; h["frame"] = (double)frame; h["pass"] = (double)passIndex; h["camera"] = pass != null ? pass.camera : ""; h["T"] = t; h["object"] = obj ?? ""; h["text"] = text ?? "";
            monHits.Add(h);
        }

        string GoPath(int goId)
        {
            foreach (var x in xfObjs) if (x.go != null && x.go.GetInstanceID() == goId) return x.o.path;
            return "#" + goId.ToString(IC);
        }

        static string FsmPath(PlayMakerFSM f)
        {
            try { return Inspector.PathOf(f.transform); } catch { return f != null ? f.name : "(null)"; }
        }

        static bool PauseOrResume(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int k = text.LastIndexOf("posts", StringComparison.Ordinal);
            string ev = (k >= 0 ? text.Substring(k + 5) : text).Trim();
            return string.Equals(ev, "pause", StringComparison.OrdinalIgnoreCase) || string.Equals(ev, "resume", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ per-frame apply (2.4.3 - 2.4.6)

        readonly double[] phaseMs = new double[3], phaseMax = new double[3];
        readonly int[] phaseN = new int[3];
        readonly long[] subTicks = new long[6];

        void PhaseDone(int i, long c0)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - c0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            phaseMs[i] += ms; phaseN[i]++;
            if (ms > phaseMax[i]) phaseMax[i] = ms;
        }

        public string PhaseText()
        {
            string[] names = { "preUpdate", "lateWrite", "preCull" };
            var sb = new System.Text.StringBuilder("replayer phases:");
            for (int i = 0; i < 3; i++)
                sb.Append(' ').Append(names[i]).Append(' ').Append((phaseN[i] > 0 ? phaseMs[i] / phaseN[i] : 0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append(" ms avg / ").Append(phaseMax[i].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" max (").Append(phaseN[i]).Append(')');
            string[] sub = { "interpolate", "unity set", "parent fix", "ropes", "particles", "shader" };
            sb.Append("; writes ").Append(writesDone).Append(", skipped unchanged ").Append(writesSkipped).Append(" (always written: ").Append(movers).Append(" moved by game code)").Append(", xf threads ").Append(xfPool != null ? xfPool.Workers + 1 : 1);
            sb.Append("; per frame:");
            int n = Math.Max(1, phaseN[1]);
            for (int i = 0; i < sub.Length; i++) sb.Append(' ').Append(sub[i]).Append(' ').Append((subTicks[i] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / n).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append(" ms");
            return sb.ToString();
        }

        void OnPreUpdate()
        {
            if (!active) return;
            long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { PreUpdateBody(); } finally { PhaseDone(0, c0); }
        }

        void PreUpdateBody()
        {
            t = req;
            ReforceTime();
            if (CameraControl.free) CameraControl.Apply();
            if (streamingPending) CheckStreaming();
            EvalActive();
            Activate();
            foreach (var x in rootList) WriteOne(x, false);
            ApplyProbes();
            if (monitorOn) MonitorTick();
        }

        void OnLateWrite()
        {
            if (!active) return;
            long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { LateWriteBody(); } finally { PhaseDone(1, c0); }
        }

        void LateWriteBody()
        {
            ReforceTime();
            bool full = fullApply;
            var list = full ? allList : everyList;
            if (!full && list.Count >= 256)
            {
                long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
                PrecomputeXf(list, t);
                subTicks[0] += System.Diagnostics.Stopwatch.GetTimestamp() - p0;
                for (int i = 0; i < list.Count; i++) WriteOne(list[i], false, i);
            }
            else foreach (var x in list) WriteOne(x, full);
            long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
            DrawRopes();
            long c1 = System.Diagnostics.Stopwatch.GetTimestamp();
            ApplyParticles();
            subTicks[3] += c1 - c0;
            subTicks[4] += System.Diagnostics.Stopwatch.GetTimestamp() - c1;
            ApplyVisibility();
            ApplyMeshes();
        }

        void OnPreCull(Camera cam)
        {
            if (!active) return;
            long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { PreCullBody(); } finally { PhaseDone(2, c0); }
        }

        void PreCullBody()
        {
            bool full = fullApply;
            CullerVisibility();
            ApplyVisibility();
            Readback();
            ApplyMaterials(full);
            ApplyShader(full);
            ApplyLights(full);
            ApplyFog();
            ApplySharedMats(full);
            ApplyMeshes();
            ApplyCuts();
            lastRenderT = t;
            hasLastRender = true;
            fullApply = false;
            frames++;
        }

        public void ApplyNow(double time)
        {
            t = req = time;
            if (!active) return;
            try
            {
                EvalActive();
                Activate();
                foreach (var x in rootList) WriteOne(x, false);
                ApplyProbes();
                foreach (var x in fullApply ? allList : everyList) WriteOne(x, fullApply);
                DrawRopes();
                for (int i = 0; i < cullerVis.Length; i++) cullerVis[i] = true;
                ApplyVisibility();
                ApplyMaterials(fullApply);
                ApplyShader(fullApply);
                ApplyLights(fullApply);
                ApplyFog();
                ApplySharedMats(fullApply);
                ApplyMeshes();
            }
            catch (Exception e) { DevLog.Error("replay ApplyNow", e); }
        }

        void EvalActive()
        {
            double tt = t;
            for (int i = 0; i < xfObjs.Count; i++)
            {
                var x = xfObjs[i];
                int id = x.id;
                bool alive = tracks.Alive(id, tt);
                bool self;
                if (!alive) self = false;
                else if (x.camDriven) self = x.go == null || x.go.activeSelf;
                else self = (tracks.Flags(id, tt) & SrecConst.FlActive) != 0;
                selfA[id] = self;
                int p = tracks.ParentAt(id, tt);
                parA[id] = p;
                if (p < 0 || p >= nodeA.Length || !nodeA[p]) aboveA[id] = x.t != null && (x.t.parent == null || x.t.parent.gameObject.activeInHierarchy);
            }
            Calc.ResolveActive(xfIds, xfIds.Length, parA, nodeA, selfA, aboveA, aih, stack);
        }

        bool RecActive(int id) { return id >= 0 && id < aih.Length && aih[id] == 1; }

        string ModeFor(Obj x)
        {
            var list = spec.activateList;
            if (list != null && list.Count > 0)
            {
                string m = list.Str(x.o.path, null);
                if (m == null)
                    foreach (var kv in list)
                        if (x.o.path.EndsWith("/" + kv.Key, StringComparison.Ordinal)) { m = kv.Value as string; break; }
                if (m == "quiet" || m == "normal") return m;
            }
            if (!x.seen) return "never";
            return spec.activate;
        }

        void Activate()
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                var go = c.go;
                if (go == null) continue;
                bool want = RecActive(c.id);
                var a = activeBy[c.id];
                if (want)
                {
                    if (go.activeSelf) continue;
                    var pt = c.t.parent;
                    if (pt != null && !pt.gameObject.activeInHierarchy) continue;
                    string mode = ModeFor(c);
                    if (mode == "never") { if (neverIds.Add(c.id)) { never.Add(c.o.path); if (never.Count <= MaxLog) Log("never activated (not seen active in this session; play.activateList can force it): " + c.o.path); } continue; }
                    if (a != null) Deactivate(a);
                    DoActivate(c, mode);
                }
                else if (a != null) Deactivate(a);
            }
        }

        Activation ActivatedRoot(Obj c)
        {
            for (var p = c.t.parent; p != null; p = p.parent)
            {
                int id;
                if (!xfByInstance.TryGetValue(p.GetInstanceID(), out id)) continue;
                var a = activeBy[id];
                if (a != null && a.root) return a;
            }
            return null;
        }

        void DoActivate(Obj c, string mode)
        {
            var a = new Activation { x = c, mode = mode };
            if (mode == "quiet" && ActivatedRoot(c) == null)
            {
                a.root = true;
                a.off = new List<Behaviour>();
                foreach (var b in c.go.GetComponentsInChildren<Behaviour>(true))
                {
                    if (b == null || !b.enabled || KeepEnabled(b)) continue;
                    b.enabled = false;
                    a.off.Add(b);
                }
                quietDisabled += a.off.Count;
            }
            c.go.SetActive(true);
            if (mode == "quiet") QuietParticles(c.go);
            activated.Add(a);
            activeBy[c.id] = a;
            activations++;
            if (activations <= MaxLog) Log("activated (" + mode + (a.root ? ", " + a.off.Count + " behaviours off" : "") + ") at T " + Num(t) + ": " + c.o.path);
        }

        static bool KeepEnabled(Behaviour b)
        {
            if (b is MaterialInstance || b is Light) return true;
            var ty = b.GetType();
            return ty.Assembly == typeof(Behaviour).Assembly && ty.Name.StartsWith("D11", StringComparison.Ordinal);
        }

        void QuietParticles(GameObject go)
        {
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps == null) continue;
                ps.Pause(false);
                Parts p;
                if (particlesOn && partsByInstance.TryGetValue(ps.GetInstanceID(), out p) && p.recorded) { p.lastN = -1; continue; }
                ps.Clear(false);
            }
        }

        void Deactivate(Activation a)
        {
            if (a.x.go != null) a.x.go.SetActive(false);
            if (a.root)
            {
                for (int i = activated.Count - 1; i >= 0; i--)
                {
                    var n = activated[i];
                    if (n == a || n.x.t == null || a.x.t == null || !n.x.t.IsChildOf(a.x.t)) continue;
                    if (n.x.go != null) n.x.go.SetActive(false);
                    activeBy[n.x.id] = null;
                    activated.RemoveAt(i);
                }
                foreach (var b in a.off) if (b != null) b.enabled = true;
            }
            activeBy[a.x.id] = null;
            activated.Remove(a);
        }

        void UndoActivations()
        {
            if (activated.Count == 0) return;
            var order = new List<Activation>(activated);
            order.Sort((p, q) => ByDepth(p.x, q.x));
            foreach (var a in order) if (activated.Contains(a)) { try { Deactivate(a); } catch (Exception e) { DevLog.Error("replay deactivate " + a.x.o.path, e); activated.Remove(a); } }
        }

        bool WriteOne(Obj x, bool full) { return WriteOne(x, full, -1); }

        bool WriteOne(Obj x, bool full, int pre)
        {
            bool was = x.written;
            x.written = false;
            var tr = x.t;
            if (tr == null) { destroyed++; return false; }
            Vector3 p, s; Quaternion q;
            long c1;
            if (pre >= 0)
            {
                if (!preOk[pre]) return false;
                p = preP[pre]; q = preQ[pre]; s = preS[pre];
                c1 = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            else
            {
                if (x.spawned && !tracks.Alive(x.id, t)) return false;
                long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool got = tracks.Xf(x.id, t, out p, out q, out s);
                c1 = System.Diagnostics.Stopwatch.GetTimestamp();
                subTicks[0] += c1 - c0;
                if (!got) return false;
            }
            bool world = !x.local;
            if (x.local)
            {
                int rp = tracks.ParentAt(x.id, t);
                if (rp >= 0 && rp != x.liveParent && rp < objs.Length && objs[rp].t != null)
                {
                    var pt = objs[rp].t;
                    p = pt.TransformPoint(p);
                    q = pt.rotation * q;
                    world = true;
                }
            }
            long c2 = System.Diagnostics.Stopwatch.GetTimestamp();
            subTicks[2] += c2 - c1;
            if (!full && was && !x.mover && !x.scaleVariant && x.wl == !world && Same(x.wp, p) && Same(x.wq, q) && !tr.hasChanged)
            {
                x.written = true;
                writesSkipped++;
                subTicks[1] += System.Diagnostics.Stopwatch.GetTimestamp() - c2;
                return true;
            }
            if (world) tr.SetPositionRotation(p, q); else tr.SetLocalPositionRotation(p, q);
            if ((x.scaleVariant || full || x.every) && (s.x != 0f || s.y != 0f || s.z != 0f) && tr.localScale != s) tr.localScale = s;
            tr.hasChanged = false;
            writesDone++;
            subTicks[1] += System.Diagnostics.Stopwatch.GetTimestamp() - c2;
            x.wp = p; x.wq = q; x.wl = !world; x.written = true;
            return true;
        }

        static bool Same(Vector3 a, Vector3 b) { return a.x == b.x && a.y == b.y && a.z == b.z; }

        static bool Same(Quaternion a, Quaternion b) { return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w; }

        ParallelRange xfPool;
        Vector3[] preP = new Vector3[0], preS = new Vector3[0];
        Quaternion[] preQ = new Quaternion[0];
        bool[] preOk = new bool[0];
        List<Obj> preList;
        double preT;
        Action<int, int> preBody;
        long writesDone, writesSkipped;

        void PrecomputeXf(List<Obj> list, double tt)
        {
            int n = list.Count;
            if (preP.Length < n) { preP = new Vector3[n]; preS = new Vector3[n]; preQ = new Quaternion[n]; preOk = new bool[n]; }
            tracks.SampleAt(tt);
            if (xfPool == null) xfPool = new ParallelRange(ParallelRange.DefaultWorkers(), "replay xf");
            if (preBody == null) preBody = PrecomputeRange;
            preList = list; preT = tt;
            xfPool.Run(n, preBody);
        }

        void PrecomputeRange(int a, int b)
        {
            var list = preList;
            double tt = preT;
            for (int i = a; i < b; i++)
            {
                var x = list[i];
                preOk[i] = !(x.spawned && !tracks.Alive(x.id, tt)) && tracks.Xf(x.id, tt, out preP[i], out preQ[i], out preS[i]);
            }
        }

        void ApplyProbes()
        {
            foreach (var p in probes)
            {
                Vector3 pos;
                if (p.p != null && tracks.Probe(p.x.id, t, out pos)) p.p.overridePosition = pos;
            }
        }

        void DrawRopes()
        {
            foreach (var r in ropes)
            {
                try
                {
                    if (r.rlr == null) continue;
                    if (r.blend == null && !StartRope(r)) continue;
                    int n = tracks.Rope(r.x.id, t, r.buf);
                    if (n <= 0) continue;
                    int m = Math.Min(n, r.verts.Length);
                    for (int i = 0; i < m; i++) fPos.SetValue(r.verts.GetValue(i), r.buf[i]);
                    fPost.SetValue(r.blend, BoxedFalse);
                    mDraw.Invoke(r.blend, r.args);
                    if (r.bottom >= 0) WriteOne(objs[r.bottom], false);
                }
                catch (Exception e)
                {
                    ropeErrors++;
                    if (ropeErrors <= 3) DevLog.Error("replay rope " + r.x.o.path, e.InnerException ?? e);
                }
            }
        }

        void ApplyParticles()
        {
            if (!particlesOn) return;
            double tt = t;
            int k = tracks.SampleAt(tt);
            bool extrapolate = spec.particles != "sample";
            float dt = extrapolate ? (float)Math.Max(0.0, tt - rec.times[Mathf.Clamp(k, 0, rec.sampleCount - 1)]) : 0f;
            foreach (var p in parts)
            {
                if (!p.recorded || p.ps == null) continue;
                try
                {
                    if (!p.ps.gameObject.activeInHierarchy) { p.lastN = -1; continue; }
                    int n = tracks.ParticleCount(p.x.id, k);
                    if (n <= 0)
                    {
                        if (p.lastN != 0) { p.ps.Clear(false); p.lastN = 0; }
                        continue;
                    }
                    if (p.cachedSample != k)
                    {
                        if (n * Calc.ParticleBytes > p.raw.Length) GrowParticles(p, n);
                        int got = SrecReader.ReadParticles(rec, p.x.id, k, p.raw);
                        if (got < 0) { GrowParticles(p, n); got = SrecReader.ReadParticles(rec, p.x.id, k, p.raw); }
                        p.cachedSample = k;
                        p.cachedN = Math.Max(0, got);
                    }
                    int m = Calc.Extrapolate(p.raw, p.cachedN, dt, p.work);
                    CopyParticles(p.work, m, p.buf);
                    p.ps.SetParticles(p.buf, m);
                    p.ps.Simulate(0f, false, false);
                    p.lastN = m;
                }
                catch (Exception e)
                {
                    particleErrors++;
                    if (particleErrors <= 3) DevLog.Error("replay particles " + p.x.o.path, e);
                }
            }
        }

        static void GrowParticles(Parts p, int n)
        {
            int max = Math.Max(n, p.buf.Length * 2);
            p.raw = new byte[max * Calc.ParticleBytes];
            p.work = new byte[max * Calc.ParticleBytes];
            p.buf = new ParticleSystem.Particle[max];
            p.cachedSample = -1;
        }

        static unsafe void CopyParticles(byte[] src, int n, ParticleSystem.Particle[] dst)
        {
            if (n <= 0) return;
            fixed (ParticleSystem.Particle* d = dst) Marshal.Copy(src, 0, (IntPtr)d, n * Calc.ParticleBytes);
        }

        void CullerVisibility()
        {
            string mode = spec.cullers;
            if (mode == "camera" && cullers.Count > 0)
            {
                BoundsCullerRuntime.UpdateBoundsCullers(NeverUpdate);
                for (int i = 0; i < cullers.Count; i++)
                {
                    var c = cullers[i];
                    bool a;
                    bool recordedOn = c.light && c.id >= 0 && tracks.CamActive(c.id, t, out a) && a;
                    cullerVis[i] = c.b == null || ReplayConst.CameraCullerVisible(c.light, c.forced, recordedOn, c.b.IsVisble());
                }
            }
            else if (mode == "recorded")
            {
                for (int i = 0; i < cullers.Count; i++)
                {
                    bool a;
                    int id = cullers[i].id;
                    cullerVis[i] = id < 0 || !tracks.CamActive(id, t, out a) || a;
                }
            }
            else for (int i = 0; i < cullerVis.Length; i++) cullerVis[i] = true;
        }

        void ApplyVisibility()
        {
            double tt = t;
            foreach (var r in rends)
            {
                if (r.r == null) continue;
                var x = r.x;
                bool vis = tracks.Alive(x.id, tt) && (tracks.Flags(x.id, tt) & SrecConst.FlRenderer) != 0 && RecActive(x.owner) && Calc.ChainVisible(r.cullers, cullerVis);
                bool cur = r.r.enabled;
                if (cur != r.last) visConflicts++;
                if (cur != vis) r.r.enabled = vis;
                r.last = vis;
            }
            foreach (var l in lites)
            {
                if (l.l == null) continue;
                var x = l.x;
                SrecLight v;
                bool on = tracks.Light(x.id, tt, out v) ? v.enabled : (tracks.Flags(x.id, tt) & SrecConst.FlLight) != 0;
                bool vis = on && tracks.Alive(x.id, tt) && RecActive(x.owner) && Calc.ChainVisible(l.cullers, cullerVis);
                bool cur = l.l.enabled;
                if (cur != l.last) visConflicts++;
                if (cur != vis) l.l.enabled = vis;
                l.last = vis;
            }
        }

        void Readback()
        {
            bool drift = sameSession && frames % 10 == 0;
            foreach (var x in everyList)
            {
                if (!x.written || !x.readback || x.t == null) continue;
                if (!Differs(x, PosTol, RotTol)) continue;
                conflicts++;
                x.conflicts++;
                if (Differs(x, ReadbackPos, ReadbackRot)) readbackErrors++;
                if (x.conflicts <= 1 && conflicts <= MaxLog) Log("conflict: " + x.o.path + " changed between lateWrite and preCull at T " + Num(t) + " (re-written)");
                Rewrite(x);
            }
            if (!drift) return;
            for (int i = onceList.Count - 1; i >= 0; i--)
            {
                var x = onceList[i];
                if (!x.written || x.t == null || !Differs(x, PosTol, RotTol)) continue;
                promoted++;
                if (promoted <= MaxLog) Log("write-once object drifted, written every frame from now on: " + x.o.path);
                onceList.RemoveAt(i);
                x.every = true;
                everyList.Add(x);
                everyList.Sort(ByDepth);
                Rewrite(x);
            }
        }

        static bool Differs(Obj x, double pos, double rot)
        {
            Vector3 p; Quaternion q;
            if (x.wl) x.t.GetLocalPositionRotation(out p, out q); else x.t.GetPositionRotation(out p, out q);
            return (p - x.wp).magnitude > pos || Calc.Angle(q, x.wq) > rot;
        }

        static void Rewrite(Obj x)
        {
            if (x.wl) x.t.SetLocalPositionRotation(x.wp, x.wq); else x.t.SetPositionRotation(x.wp, x.wq);
        }

        void ApplyMaterials(bool full)
        {
            double tt = t;
            foreach (var m in mats)
            {
                if ((!full && !m.multi) || m.mi == null) continue;
                int id = m.x.id;
                Vector4 v; int tex;
                try
                {
                    for (int i = 0; i < m.f.Length; i++) if (m.f[i] != null && tracks.Prop(id, SrecConst.MatFloat, i, tt, out v, out tex)) m.mi.SetFloatProperty(m.f[i], v.x);
                    for (int i = 0; i < m.c.Length; i++) if (m.c[i] != null && tracks.Prop(id, SrecConst.MatColor, i, tt, out v, out tex)) m.mi.SetColorProperty(m.c[i], new Color(v.x, v.y, v.z, v.w));
                    for (int i = 0; i < m.v.Length; i++) if (m.v[i] != null && tracks.Prop(id, SrecConst.MatVector, i, tt, out v, out tex)) m.mi.SetVectorProperty(m.v[i], v);
                    for (int i = 0; i < m.tx.Length; i++) if (m.tx[i] != null && tracks.Prop(id, SrecConst.MatTexture, i, tt, out v, out tex)) m.mi.SetTextureProperty(m.tx[i], Tex(tex));
                }
                catch (Exception e) { DevLog.Error("replay material " + m.x.o.path, e); m.mi = null; }
            }
        }

        void ApplyLights(bool full)
        {
            double tt = t;
            string mode = spec.shadows;
            foreach (var l in lites)
            {
                var li = l.l;
                if (li == null) continue;
                if (full || l.multi)
                {
                    SrecLight v;
                    if (tracks.Light(l.x.id, tt, out v))
                    {
                        if (li.intensity != v.intensity) li.intensity = v.intensity;
                        if (li.color != v.color) li.color = v.color;
                        if (li.range != v.range) li.range = v.range;
                        if (li.spotAngle != v.spotAngle) li.spotAngle = v.spotAngle;
                        var ck = Tex(v.cookie);
                        if (li.cookie != ck && (ck != null || v.cookie < 0)) li.cookie = ck;
                        if (mode == "recorded")
                        {
                            if (li.useStaticShadows != v.useStaticShadows) li.useStaticShadows = v.useStaticShadows;
                            if (li.staticShadowsDirty != v.staticShadowsDirty) li.staticShadowsDirty = v.staticShadowsDirty;
                        }
                    }
                }
                if (mode == "dirty" && li.shadows != LightShadows.None) li.staticShadowsDirty = true;
                else if (mode == "dynamic" && li.useStaticShadows) li.useStaticShadows = false;
            }
        }

        void ApplyFog()
        {
            if (fogFields.Length == 0) return;
            var cur = FogAmbientBlender.currentBlendVal;
            if (cur == null) return;
            double tt = t;
            for (int i = 0; i < fogFields.Length; i++)
            {
                var f = fogFields[i];
                Vector4 v;
                if (f == null || !tracks.Render(i, tt, out v)) continue;
                SetField(cur, f, v);
            }
        }

        static void SetField(object target, FieldInfo f, Vector4 v)
        {
            var ft = f.FieldType;
            if (ft == typeof(float)) f.SetValue(target, v.x);
            else if (ft == typeof(Color)) f.SetValue(target, new Color(v.x, v.y, v.z, v.w));
            else if (ft == typeof(Vector4)) f.SetValue(target, v);
            else if (ft == typeof(Vector3)) f.SetValue(target, new Vector3(v.x, v.y, v.z));
            else if (ft == typeof(int)) f.SetValue(target, (int)Math.Round(v.x));
            else if (ft == typeof(bool)) f.SetValue(target, v.x != 0f);
        }

        void ApplySharedMats(bool full)
        {
            double tt = t;
            foreach (var r in rends)
            {
                if (r.r == null || r.slots <= 0 || (!full && !r.multi)) continue;
                for (int s = 0; s < r.slots; s++)
                {
                    int mid;
                    if (!tracks.SharedMat(r.x.id, s, tt, out mid)) continue;
                    if (r.applied[s] == mid && !full) continue;
                    var mat = MatOf(mid);
                    r.applied[s] = mid;
                    if (mat == null) continue;
                    if (r.slots == 1) { var cur = r.r.sharedMaterial; if (cur != mat && !SameName(cur, mid)) r.r.sharedMaterial = mat; continue; }
                    var arr = r.r.sharedMaterials;
                    if (s < arr.Length && arr[s] != mat && !SameName(arr[s], mid)) { arr[s] = mat; r.r.sharedMaterials = arr; }
                }
            }
        }

        void ApplyCuts()
        {
            if (!hasLastRender || !(t > lastRenderT)) return;
            foreach (var x in cutList)
            {
                if (x.t == null || !tracks.CutBetween(x.id, lastRenderT, t)) continue;
                if (x.tags == null) x.tags = x.t.GetComponentsInChildren<D11VelocityBufferTag>(true);
                foreach (var tag in x.tags) if (tag != null) tag.wrapAroundFrame = true;
            }
        }

        string Str(int sid) { return sid >= 0 && rec.strings != null && sid < rec.strings.Count ? rec.strings[sid] : null; }

        // A renderer that already shows a material of the recorded name keeps it: instances made by Renderer.material share
        // their name (head-pop workers, each with its own texture offset), and the name table cannot tell them apart.
        bool SameName(Material cur, int sid) { return cur != null && cur.name == Str(sid); }

        void SeedTexture(Texture tx)
        {
            if (texByName == null) texByName = new Dictionary<string, Texture>();
            if (tx != null && !texByName.ContainsKey(tx.name)) texByName[tx.name] = tx;
        }

        void SeedMaterials(Renderer r)
        {
            if (matByName == null) matByName = new Dictionary<string, Material>();
            var ms = r.sharedMaterials;
            if (ms == null) return;
            foreach (var m in ms) if (m != null && !matByName.ContainsKey(m.name)) matByName[m.name] = m;
        }

        Texture Tex(int sid)
        {
            if (sid < 0) return null;
            Texture tx;
            if (texById.TryGetValue(sid, out tx)) return tx;
            string name = Str(sid);
            if (name == null) return null;
            if (texByName == null) texByName = new Dictionary<string, Texture>();
            if (!texByName.TryGetValue(name, out tx) && !texScanned)
            {
                texScanned = true;
                foreach (var o in Resources.FindObjectsOfTypeAll<Texture>()) if (o != null && !texByName.ContainsKey(o.name)) texByName[o.name] = o;
                texByName.TryGetValue(name, out tx);
            }
            if (tx == null) { missingTex++; if (missingTex <= MaxLog) Log("texture not found: " + name); }
            texById[sid] = tx;
            return tx;
        }

        Material MatOf(int sid)
        {
            if (sid < 0) return null;
            Material m;
            if (matById.TryGetValue(sid, out m)) return m;
            string name = Str(sid);
            if (name == null) return null;
            if (matByName == null) matByName = new Dictionary<string, Material>();
            if (!matByName.TryGetValue(name, out m) && !matScanned)
            {
                matScanned = true;
                foreach (var o in Resources.FindObjectsOfTypeAll<Material>()) if (o != null && !matByName.ContainsKey(o.name)) matByName[o.name] = o;
                matByName.TryGetValue(name, out m);
            }
            if (m == null) { missingMat++; if (missingMat <= MaxLog) Log("material not found: " + name); }
            matById[sid] = m;
            return m;
        }

        void BuildMeshes()
        {
            foreach (int id in tracks.MeshIds())
            {
                if (id < 0 || id >= objs.Length) continue;
                var x = objs[id];
                var smr = x.comp as SkinnedMeshRenderer;
                if (!x.resolved || smr == null) continue;
                meshSwaps.Add(new MeshSwap { id = id, smr = smr, begin = smr.sharedMesh });
            }
            if (meshSwaps.Count > 0) Log("mesh swaps: " + meshSwaps.Count + " skinned renderer(s) change sharedMesh in the recording");
        }

        void ApplyMeshes()
        {
            double tt = t;
            foreach (var ms in meshSwaps)
            {
                if (ms.smr == null) continue;
                string name = tracks.MeshAt(ms.id, tt);
                if (name == null) continue;
                if (name != ms.applied) { ms.applied = name; ms.want = MeshNamed(name, ms); }
                if (ms.want != null && ms.smr.sharedMesh != ms.want) ms.smr.sharedMesh = ms.want;
            }
        }

        // A keyed name (records from R3p: name|vertexCount|instanceId) resolves to the mesh the recorder kept at the swap (this
        // session), else to the live mesh with the same name and vertex count (same instance id first); a plain name (older
        // records) keeps the old order: the renderers' meshes at Begin, the recorder's kept mesh, then any loaded mesh.
        Mesh MeshNamed(string name, MeshSwap ms)
        {
            if (meshByName == null)
            {
                meshByName = new Dictionary<string, Mesh>();
                foreach (var s in meshSwaps) if (s.begin != null && !meshByName.ContainsKey(s.begin.name)) meshByName[s.begin.name] = s.begin;
            }
            Mesh m;
            if (meshByName.TryGetValue(name, out m) && m != null) return m;
            string bare;
            int vc, inst;
            if (ReplayConst.SplitMeshKey(name, out bare, out vc, out inst))
            {
                if (sessionRec != null) { m = sessionRec.MeshRef(name); if (m != null) { meshByName[name] = m; return m; } }
                if (allMeshes == null) allMeshes = Resources.FindObjectsOfTypeAll<Mesh>();
                int best = 0;
                foreach (var o in allMeshes)
                {
                    if (o == null) continue;
                    int vco = -1;
                    try { vco = o.vertexCount; } catch { vco = -1; }
                    int score = ReplayConst.MeshMatch(name, o.name, vco, o.GetInstanceID());
                    if (score > best) { best = score; m = o; }
                }
                if (m != null) { meshByName[name] = m; return m; }
                missingMesh++;
                if (missingMesh <= MaxLog) Log("mesh not found: " + name + " (renderer " + objs[ms.id].o.path + ")");
                return null;
            }
            if (sessionRec != null) { m = sessionRec.MeshRef(name); if (m != null) { meshByName[name] = m; return m; } }
            if (!meshScanned)
            {
                meshScanned = true;
                foreach (var o in Resources.FindObjectsOfTypeAll<Mesh>()) if (o != null && !meshByName.ContainsKey(o.name)) meshByName[o.name] = o;
                if (meshByName.TryGetValue(name, out m) && m != null) return m;
            }
            missingMesh++;
            if (missingMesh <= MaxLog) Log("mesh not found: " + name + " (renderer " + objs[ms.id].o.path + ")");
            return null;
        }

        void MonitorTick()
        {
            long tot = EventMonitor.total;
            long fresh = tot - evSeen;
            evSeen = tot;
            if (fresh > 0)
            {
                int m = (int)Math.Min(fresh, EventMonitor.Count);
                for (int i = m - 1; i >= 0; i--)
                {
                    var ev = EventMonitor.Get(i);
                    if (!setGos.Contains(ev.goId)) continue;
                    if (ev.kind == 'S') { monSignals++; MonitorHit("signal", ev.frame, GoPath(ev.goId), ev.text); }
                    else if (ev.kind == 'A')
                    {
                        if (PauseOrResume(ev.text)) { monSoundsExcluded++; MonitorHit("sound (pause/resume, allowed)", ev.frame, GoPath(ev.goId), ev.text); }
                        else { monSounds++; MonitorHit("sound", ev.frame, GoPath(ev.goId), ev.text); }
                    }
                }
            }
            foreach (var w in fsms)
            {
                if (w.f == null) continue;
                string s = w.f.ActiveStateName;
                bool fin = w.f.Fsm != null && w.f.Fsm.Finished;
                if (!string.Equals(s, w.state, StringComparison.Ordinal)) { monFsm++; MonitorHit("fsm", Time.frameCount, FsmPath(w.f), w.f.FsmName + ": " + w.state + " -> " + s); w.state = s; }
                if (fin != w.finished) { monFinished++; MonitorHit("fsm finished", Time.frameCount, FsmPath(w.f), w.f.FsmName + ": Finished " + w.finished + " -> " + fin); w.finished = fin; }
            }
            foreach (var x in camWatch)
            {
                bool a = x.go != null && x.go.activeSelf;
                if (a != x.lastActive) { camToggles++; x.lastActive = a; }
            }
            monitorHits = monSignals + monSounds + monFsm + monFinished + monCullerSets + monSubscenes;
        }

        // ------------------------------------------------------------------ pass isolation (2.4.9)

        public void BeginPass(string camera)
        {
            if (!active) throw new InvalidOperationException("SceneReplayer.BeginPass before Begin");
            if (passOpen) EndPass(pass != null ? pass.camera : "");
            RestoreSnapshot();
            fullApply = true;
            hasLastRender = false;
            passIndex++;
            for (int i = 0; i < 3; i++) { phaseMs[i] = 0; phaseMax[i] = 0; phaseN[i] = 0; }
            for (int i = 0; i < subTicks.Length; i++) subTicks[i] = 0;
            writesDone = writesSkipped = 0;
            pass = new ReplayPassInfo { camera = camera ?? "", startHash = StateHash(), rngStart = UnityEngine.Random.seed };
            passOpen = true;
            Log("pass " + passIndex + " " + pass.camera + ": start hash " + pass.startHash);
        }

        public void EndPass(string camera)
        {
            if (!active) return;
            if (pass == null) pass = new ReplayPassInfo { camera = camera ?? "" };
            pass.endHash = StateHash();
            pass.rngEnd = UnityEngine.Random.seed;
            RestoreSnapshot();
            passOpen = false;
            Log("pass " + passIndex + " " + pass.camera + ": end hash " + pass.endHash + ", conflicts " + conflicts + ", activations " + activations + ", monitor " + monitorHits);
        }

        void RestoreSnapshot()
        {
            UndoActivations();
            foreach (var r in rends)
            {
                if (r.r == null) continue;
                if (r.r.enabled != r.begin) r.r.enabled = r.begin;
                r.last = r.begin;
                if (r.snap != null)
                {
                    if (r.slots == 1) { if (r.snap.Length > 0 && r.r.sharedMaterial != r.snap[0]) r.r.sharedMaterial = r.snap[0]; }
                    else r.r.sharedMaterials = r.snap;
                }
                for (int i = 0; i < r.applied.Length; i++) r.applied[i] = -2;
            }
            foreach (var l in lites) { if (l.l != null) { SetLight(l.l, l.begin); l.last = l.begin.enabled; } }
            foreach (var ms in meshSwaps)
            {
                if (ms.smr != null && ms.begin != null && ms.smr.sharedMesh != ms.begin) ms.smr.sharedMesh = ms.begin;
                ms.applied = null; ms.want = null;
            }
            foreach (var p in parts)
            {
                if (!p.recorded || p.ps == null || !particlesOn) continue;
                p.ps.Clear(false);
                p.ps.Pause(false);
                p.lastN = -1;
            }
            foreach (var r in ropes)
            {
                if (r.verts == null || r.snap == null) continue;
                for (int i = 0; i < r.verts.Length && i < r.snap.Length; i++) fPos.SetValue(r.verts.GetValue(i), r.snap[i]);
            }
            foreach (var m in mats)
            {
                if (m.mi == null || m.fs == null) continue;
                for (int i = 0; i < m.f.Length; i++) if (m.f[i] != null) m.mi.SetFloatProperty(m.f[i], m.fs[i]);
                for (int i = 0; i < m.c.Length; i++) if (m.c[i] != null) m.mi.SetColorProperty(m.c[i], m.cs[i]);
                for (int i = 0; i < m.v.Length; i++) if (m.v[i] != null) m.mi.SetVectorProperty(m.v[i], m.vs[i]);
                for (int i = 0; i < m.tx.Length; i++) if (m.tx[i] != null) m.mi.SetTextureProperty(m.tx[i], m.ts[i]);
            }
            foreach (var p in probes) if (p.p != null) p.p.overridePosition = p.snap;
            RestoreShader();
        }

        static void SetLight(Light l, LightValues v)
        {
            if (l.enabled != v.enabled) l.enabled = v.enabled;
            if (l.intensity != v.intensity) l.intensity = v.intensity;
            if (l.color != v.color) l.color = v.color;
            if (l.range != v.range) l.range = v.range;
            if (l.spotAngle != v.spotAngle) l.spotAngle = v.spotAngle;
            if (l.cookie != v.cookie) l.cookie = v.cookie;
            if (l.useStaticShadows != v.stat) l.useStaticShadows = v.stat;
            if (l.staticShadowsDirty != v.dirty) l.staticShadowsDirty = v.dirty;
        }

        public string StateHash()
        {
            ulong h = Calc.FnvOffset;
            if (objs == null) return h.ToString("x16");
            foreach (var x in xfObjs)
            {
                if (x.camDriven || x.camXf) continue;
                h = Calc.Fnv(h, x.go == null ? (byte)2 : x.go.activeSelf ? (byte)1 : (byte)0);
            }
            h = Calc.Fnv(h, (byte)0xF1);
            foreach (var r in rends) h = Calc.Fnv(h, r.r == null ? (byte)2 : r.r.enabled ? (byte)1 : (byte)0);
            h = Calc.Fnv(h, (byte)0xF2);
            foreach (var l in lites) h = Calc.Fnv(h, l.l == null ? (byte)2 : l.l.enabled ? (byte)1 : (byte)0);
            h = Calc.Fnv(h, (byte)0xF3);
            foreach (var w in fsms)
            {
                if (w.f == null) { h = Calc.Fnv(h, (byte)2); continue; }
                h = Calc.Fnv(h, w.f.ActiveStateName);
                h = Calc.Fnv(h, w.f.Fsm != null && w.f.Fsm.Finished ? (byte)1 : (byte)0);
            }
            h = Calc.Fnv(h, (byte)0xF4);
            foreach (var m in mats)
            {
                if (m.mi == null) { h = Calc.Fnv(h, -1); continue; }
                h = Calc.Fnv(h, m.mi.FloatProperties.Count); h = Calc.Fnv(h, m.mi.ColorProperties.Count);
                h = Calc.Fnv(h, m.mi.VectorProperties.Count); h = Calc.Fnv(h, m.mi.TextureProperties.Count);
            }
            h = Calc.Fnv(h, (byte)0xF5);
            h = Calc.Fnv(h, BoundsCullerRuntime.boundsCullers != null ? BoundsCullerRuntime.boundsCullers.Count : -1);
            int loaded = 0;
            try { foreach (var s in SubsceneCullingManager.instance.GetAllSubsceneCullers()) if (s != null && s.IsLoaded) loaded++; } catch { loaded = -1; }
            h = Calc.Fnv(h, loaded);
            return h.ToString("x16");
        }

        // ------------------------------------------------------------------ end and restore (2.4.8)

        public void End(SpecReport report)
        {
            if (xfPool != null) { xfPool.Dispose(); xfPool = null; }
            if (ended) return;
            ended = true;
            if (!begun)
            {
                if (current == this) current = null;
                if (ReplayHub.source == this) ReplayHub.source = null;
                return;
            }
            SceneHooks.preUpdate -= onPreUpdate;
            SceneHooks.lateWrite -= onLateWrite;
            SceneHooks.preCullMain -= onPreCull;
            begun = false;
            Restore(report, "camera", RestoreCamera);
            Restore(report, "final sample", FinalSample);
            Restore(report, "rigidbodies", RestoreBodies);
            Restore(report, "particles", () => { foreach (var p in parts) if (p.recorded && p.ps != null && particlesOn && p.wasPlaying && !p.wasPaused) p.ps.Play(false); });
            Restore(report, "probes", () => { foreach (var p in probes) if (p.p != null) { p.p.overridePosition = p.overrideBefore; p.p.useOverridePosition = p.useBefore; } });
            Restore(report, "neutralize", RestoreNeutral);
            Restore(report, "activations", UndoActivations);
            Restore(report, "renderers", () =>
            {
                foreach (var r in rends) if (r.r != null && r.r.enabled != r.begin) r.r.enabled = r.begin;
                foreach (var l in lites) if (l.l != null && l.l.enabled != l.begin.enabled) l.l.enabled = l.begin.enabled;
            });
            Restore(report, "monitor", DisarmMonitor);
            Restore(report, "cullers", RestoreCullers);
            Restore(report, "streaming", RestoreStreaming);
            bool reload = spec.restore == "reload" && header.subscene >= 0 && header.savepoint >= 0;
            Restore(report, "time", () =>
            {
                if (!timeSaved) return;
                if (reload) MultiCamFreeze.Restore(timeBefore > 0f ? timeBefore : 1f, timeBefore > 0f ? timeBefore : 1f, false, "replay end");
                else MultiCamFreeze.Restore(timeBefore, gmBefore, false, "replay end: frozen before the replay");
            });
            SceneHooks.Release();
            Restore(report, "recording file", ReleaseRecording);
            if (current == this) current = null;
            if (ReplayHub.source == this) ReplayHub.source = null;
            Log("end: conflicts " + conflicts + " (visibility " + visConflicts + "), readback errors " + readbackErrors + ", promoted " + promoted + ", activations " + activations +
                ", never activated " + never.Count + ", monitor " + MonitorText() + ", time leaks " + timeLeaks + ", rope errors " + ropeErrors + ", particle errors " + particleErrors +
                ", missing textures " + missingTex + ", materials " + missingMat + ", destroyed writes " + destroyed);
            if (reload) Restore(report, "reload", () => { Log("restore reload: savepoint " + header.subscene + " " + header.savepoint); G.LoadSavepoint(header.subscene, header.savepoint); });
        }

        void Restore(SpecReport report, string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Warn(report, "replay.end." + what, e.GetType().Name + ": " + e.Message); DevLog.Error("replay end " + what, e); }
        }

        void RestoreCamera()
        {
            bool ok = tracks.Virtual(dur, virt);
            var cam = RenderHost.WorldCam();
            if (CameraControl.free) CameraControl.SetFree(false);
            cameraTaken = false;
            if (!ok || cam == null) { Log("game camera released (no recorded final pose)"); return; }
            var p = virt.camPos;
            var q = virt.camRot;
            if (virt.game != null && virt.game.ok && virt.game.rendered.ok) { p = virt.game.rendered.p; q = virt.game.rendered.r; }
            cam.transform.position = p;
            cam.transform.rotation = q;
            float fov = virt.camFov > 0f ? virt.camFov : virt.game != null && virt.game.rendered.fov > 0f ? virt.game.rendered.fov : 0f;
            if (fov > 0f) cam.fieldOfView = fov;
            string script = RestoreCameraScript(cam, virt.game);
            Log("game camera released and put back at the recorded final pose " + V(p) + script);
        }

        // CameraScript keeps its own pose state (positionCamera/Aim/Up, interpolatedPosition/Rotation, cameraVelocity and the
        // private lastPositionCamera, FP/CameraScript.cs:92-140) and MoveCamera writes the transform from it every Update, so
        // the transform alone is undone one frame later (R7: 0.64 m off after a fresh-session replay). The recorded final
        // GameCamState goes back into those members (private setters by reflection).
        static string RestoreCameraScript(Camera cam, GameCamState g)
        {
            if (cam == null || g == null || !g.ok) return "";
            CameraScript cs = null;
            try { cs = cam.GetComponent<CameraScript>(); } catch { }
            if (cs == null) return " (no CameraScript)";
            int n = 0;
            var ty = typeof(CameraScript);
            Vector3 up = g.positionUp.sqrMagnitude > 1e-6f ? g.positionUp : Vector3.up;
            var clean = g.clean.ok ? g.clean : g.rendered;
            n += SetMember(ty, cs, "positionCamera", g.positionCamera);
            n += SetMember(ty, cs, "positionAim", g.positionAim);
            n += SetMember(ty, cs, "positionUp", up);
            n += SetMember(ty, cs, "cameraVelocity", g.velocity);
            n += SetMember(ty, cs, "lastPositionCamera", g.positionCamera);
            if (clean.ok)
            {
                n += SetMember(ty, cs, "interpolatedPosition", clean.p);
                n += SetMember(ty, cs, "interpolatedRotation", clean.r);
            }
            return ", CameraScript state restored (" + n + " of 7 members)";
        }

        static int SetMember(Type ty, object target, string name, object value)
        {
            try
            {
                var pr = ty.GetProperty(name, AnyMember);
                var set = pr != null ? pr.GetSetMethod(true) : null;
                if (set != null) { set.Invoke(target, new[] { value }); return 1; }
                var f = ty.GetField(name, AnyMember);
                if (f != null && !f.IsStatic) { f.SetValue(target, value); return 1; }
            }
            catch (Exception e) { DevLog.Error("replay camera state " + name, e); }
            return 0;
        }

        void FinalSample()
        {
            t = req = dur;
            EvalActive();
            foreach (var x in allList) WriteOne(x, true);
            DrawRopes();
            ApplyParticles();
            ApplyProbes();
            ApplyMaterials(true);
            ApplyShader(true);
            ApplyLights(true);
            ApplyFog();
            ApplySharedMats(true);
            ApplyMeshes();
        }

        void ReleaseRecording()
        {
            var m = typeof(SceneRecording).GetMethod("Release", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (m != null && rec != null) m.Invoke(rec, null);
        }

        void RestoreBodies()
        {
            foreach (var b in bodies)
            {
                if (b.rb == null) continue;
                b.rb.isKinematic = b.kinematic;
                if (!b.kinematic) { b.rb.velocity = velocitiesFinal ? b.v : Vector3.zero; b.rb.angularVelocity = velocitiesFinal ? b.av : Vector3.zero; }
                b.rb.interpolation = b.interp;
            }
        }

        void RestoreNeutral()
        {
            int legs = 0;
            foreach (var b in disabledNeutral)
            {
                if (b == null) continue;
                if (b is HuddleLegControl) legs++;
                b.enabled = true;
            }
            if (legs > 0) Log(legs + " HuddleLegControl re-enabled (OnEnable calls EnableAnimations)");
            var u = Umbrella<CrowdDude>.instance;
            int back = 0;
            foreach (var kv in crowds)
            {
                var cd = kv.Key;
                if (cd == null || u == null || fHandle == null) continue;
                if ((int)fHandle.GetValue(cd) != -1 || !cd.isActiveAndEnabled) continue;
                fHandle.SetValue(cd, u.Insert(cd));
                back++;
            }
            if (crowds.Count > 0) Log(back + " of " + crowds.Count + " CrowdDudes re-inserted into their umbrella");
        }

        void DisarmMonitor()
        {
            if (!monitorOn) return;
            BoundsCullerRuntime.staticSetVisible -= onCullerSet;
            SubsceneCuller.StaticEvents.postActivate -= onSubsceneActivate;
            monitorOn = false;
        }

        void RestoreCullers()
        {
            foreach (var c in cullers)
            {
                if (c.b == null) continue;
                if (c.ours || c.handed) c.b.valid = c.validBefore;
            }
            if (BoundsCullerRuntime.boundsCullers != null) foreach (var b in BoundsCullerRuntime.boundsCullers) if (b != null) b.RefreshRuntimeState();
            if (holding) { SceneHooks.cullerHolds = Math.Max(0, SceneHooks.cullerHolds - 1); holding = false; }
            if (SceneHooks.cullerHolds == 0 && BoundsCullerRuntime.boundsCullers != null && BoundsCullerRuntime.boundsCullers.Count > 0) SceneHooks.RehookCullers();
        }

        void RestoreStreaming()
        {
            var m = SubsceneCullingManager.instance;
            if (cullingSaved) { m.CullingActive = cullingBefore; cullingSaved = false; }
            foreach (var c in keptOwned) if (c != null) m.RemoveAlwaysLoaded(c);
            keptOwned.Clear();
            streamingPending = false;
        }

        // ------------------------------------------------------------------ reports

        public ReplayRecordingInfo Info()
        {
            var i = new ReplayRecordingInfo();
            i.dir = dir;
            i.header = header;
            i.samples = rec != null ? rec.sampleCount : 0;
            i.objects = objs != null ? objs.Length : 0;
            i.duration = dur;
            i.unresolved = unresolved;
            i.unresolvedSpawned = unresolvedSpawned;
            i.never_activated = new List<string>(never);
            i.conflicts = conflicts;
            i.readbackErrors = readbackErrors;
            i.activations = activations;
            i.monitorHits = monitorHits;
            return i;
        }

        internal Json.Obj MonitorJson()
        {
            var o = new Json.Obj();
            o["signals"] = (double)monSignals; o["fsmChanges"] = (double)monFsm; o["finishedToggles"] = (double)monFinished;
            o["cullerSetActive"] = (double)monCullerSets; o["subsceneActivations"] = (double)monSubscenes; o["soundPosts"] = (double)monSounds;
            o["soundPostsExcluded"] = (double)monSoundsExcluded; o["hits"] = new List<object>(monHits);
            o["cameraDrivenToggles"] = (double)camToggles; o["timeLeaks"] = (double)timeLeaks; o["visibilityConflicts"] = (double)visConflicts;
            o["promotedWriteOnce"] = (double)promoted; o["quietBehavioursOff"] = (double)quietDisabled;
            return o;
        }

        string MonitorText()
        {
            return monitorHits + " (signals " + monSignals + ", FSM " + monFsm + ", finished " + monFinished + ", culler SetActive " + monCullerSets + ", subscene activations " +
                   monSubscenes + ", sound posts " + monSounds + (monSoundsExcluded > 0 ? " (+" + monSoundsExcluded + " pause/resume, allowed)" : "") + "; camera-driven toggles " + camToggles + ")";
        }

        public UObj Resolved(int id)
        {
            if (objs == null || id < 0 || id >= objs.Length) return null;
            var x = objs[id];
            if (!x.resolved) return null;
            return x.xf ? (UObj)x.t : x.comp;
        }

        public string ObjectsText(string filter, int max)
        {
            if (objs == null) return "no recording loaded";
            if (max <= 0) max = 200;
            var sb = new StringBuilder("id\tkind\tkeys\tresolved\thow\tconflicts\tflags\tpath\n");
            int shown = 0, matched = 0;
            foreach (var x in objs)
            {
                string kn = KindName(x.kind);
                if (!string.IsNullOrEmpty(filter) && kn != filter && (x.o.path ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matched++;
                if (shown >= max) continue;
                shown++;
                sb.Append(x.id).Append('\t').Append(kn).Append('\t').Append(x.keys).Append('\t').Append(x.resolved ? "yes" : x.ambiguous ? "ambiguous" : "no").Append('\t')
                  .Append(x.how).Append('\t').Append(x.conflicts).Append('\t').Append(FlagText(x)).Append('\t').Append(x.o.path).Append('\n');
            }
            if (matched > shown) sb.Append("... ").Append(matched - shown).Append(" more\n");
            return sb.ToString();
        }

        static string FlagText(Obj x)
        {
            var sb = new StringBuilder();
            if (x.local) sb.Append('L');
            if (x.camDriven) sb.Append('C');
            if (x.camXf) sb.Append('X');
            if (x.spawned) sb.Append('S');
            if (x.scaleVariant) sb.Append('V');
            if (x.xf && x.resolved) sb.Append(x.every ? 'E' : 'O');
            if (x.animated) sb.Append('A');
            return sb.Length > 0 ? sb.ToString() : "-";
        }

        public string Status()
        {
            if (rec == null) return "replay: nothing loaded";
            string state = ended ? "ended" : begun ? "active" : "loaded";
            return "replay " + state + " " + dir + ": T " + Num(t) + "/" + Num(dur) + " s, sample " + SampleAt(t) + "/" + rec.sampleCount + ", objects " + resolvedCount + "/" +
                   (objs != null ? objs.Length : 0) + " resolved (unresolved dynamic " + unresolved + ", spawned " + unresolvedSpawned + ", ambiguous " + ambiguous + ")" +
                   (pass != null ? ", pass " + passIndex + " " + pass.camera + (passOpen ? "" : " (closed)") : "") + ", frames " + frames + ", conflicts " + conflicts + ", readback " + readbackErrors +
                   ", activations " + activations + ", never " + never.Count + ", monitor " + monitorHits + (streamingPending ? ", waiting for kept subscenes" : "") +
                   (timeLeaks > 0 ? ", time leaks " + timeLeaks : "");
        }

        static string KindName(byte k) { return k < KindNames.Length ? KindNames[k] : "kind" + k; }

        static string Num(double v) { return v.ToString("0.###", IC); }

        static string V(Vector3 v) { return Num(v.x) + "," + Num(v.y) + "," + Num(v.z); }

        static void Log(string s) { DevLog.Write("[replay] " + s); }

        static void Err(SpecReport r, string where, string msg) { if (r != null) r.Error(where, msg); DevLog.Write("[replay] error " + where + ": " + msg); }

        static void Warn(SpecReport r, string where, string msg) { if (r != null) r.Warn(where, msg); DevLog.Write("[replay] warning " + where + ": " + msg); }

        static void Note(SpecReport r, string where, string msg) { if (r != null) r.Note(where, msg); DevLog.Write("[replay] " + where + ": " + msg); }
    }
#endif

    // Pure helpers (managed math and UnityEngine value types only, no game state). Offline tests compile this part alone
    // with -define:PLAY_B_MATH_ONLY under .NET 9 (offline tests).
    public sealed partial class SceneReplayer
    {
        internal static class Calc
        {
            public const ulong FnvOffset = 14695981039346656037UL, FnvPrime = 1099511628211UL;
            public const int ParticleBytes = 84, OffPos = 0, OffVel = 12, OffAnimVel = 24, OffRot = 48, OffAngVel = 52, OffLife = 68;

            public static ulong Fnv(ulong h, byte b) { return (h ^ b) * FnvPrime; }

            public static ulong Fnv(ulong h, int v)
            {
                h = Fnv(h, (byte)v); h = Fnv(h, (byte)(v >> 8)); h = Fnv(h, (byte)(v >> 16)); return Fnv(h, (byte)(v >> 24));
            }

            public static ulong Fnv(ulong h, string s)
            {
                if (s == null) return Fnv(Fnv(h, (byte)0xFE), (byte)0);
                for (int i = 0; i < s.Length; i++) { char c = s[i]; h = Fnv(h, (byte)c); h = Fnv(h, (byte)(c >> 8)); }
                return Fnv(h, (byte)0);
            }

            public static double Angle(Quaternion a, Quaternion b)
            {
                double dot = (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w;
                double s = dot < 0.0 ? -1.0 : 1.0;
                double dx = a.x - s * b.x, dy = a.y - s * b.y, dz = a.z - s * b.z, dw = a.w - s * b.w;
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
                return 4.0 * Math.Asin(Math.Min(1.0, 0.5 * d));
            }

            public static void ResolveActive(int[] ids, int count, int[] parent, bool[] node, bool[] self, bool[] above, sbyte[] state, int[] stack)
            {
                int n = state.Length;
                for (int i = 0; i < count; i++) state[ids[i]] = 0;
                for (int i = 0; i < count; i++)
                {
                    int id = ids[i];
                    if (state[id] == 1 || state[id] == 2) continue;
                    int top = 0, cur = id;
                    while (cur >= 0 && state[cur] == 0 && top < stack.Length)
                    {
                        state[cur] = 3;
                        stack[top++] = cur;
                        int p = parent[cur];
                        cur = p >= 0 && p < n && node[p] ? p : -1;
                    }
                    for (int k = top - 1; k >= 0; k--)
                    {
                        int e = stack[k];
                        int p = parent[e];
                        bool pv;
                        if (p >= 0 && p < n && node[p] && (state[p] == 1 || state[p] == 2)) pv = state[p] == 1;
                        else pv = above[e];
                        state[e] = (sbyte)(self[e] && pv ? 1 : 2);
                    }
                }
            }

            public static unsafe int Extrapolate(byte[] src, int n, float dt, byte[] dst)
            {
                if (n <= 0) return 0;
                if (!(dt > 0f)) { Buffer.BlockCopy(src, 0, dst, 0, n * ParticleBytes); return n; }
                int m = 0;
                fixed (byte* s0 = src)
                fixed (byte* d0 = dst)
                {
                    for (int i = 0; i < n; i++)
                    {
                        byte* s = s0 + i * ParticleBytes;
                        float life = *(float*)(s + OffLife) - dt;
                        if (life <= 0f) continue;
                        byte* d = d0 + m * ParticleBytes;
                        int* si = (int*)s, di = (int*)d;
                        for (int k = 0; k < ParticleBytes / 4; k++) di[k] = si[k];
                        float* p = (float*)(d + OffPos);
                        float* v = (float*)(d + OffVel);
                        float* a = (float*)(d + OffAnimVel);
                        p[0] += (v[0] + a[0]) * dt; p[1] += (v[1] + a[1]) * dt; p[2] += (v[2] + a[2]) * dt;
                        *(float*)(d + OffRot) += *(float*)(d + OffAngVel) * dt;
                        *(float*)(d + OffLife) = life;
                        m++;
                    }
                }
                return m;
            }

            public static double Interp(double[] xs, double[] ys, int count, double x)
            {
                if (xs == null || ys == null || count <= 0) return 0.0;
                if (count == 1 || x <= xs[0]) return ys[0];
                if (x >= xs[count - 1]) return ys[count - 1];
                int lo = 0, hi = count - 1;
                while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (xs[mid] <= x) lo = mid; else hi = mid; }
                double span = xs[hi] - xs[lo];
                double u = span > 0.0 ? (x - xs[lo]) / span : 0.0;
                return ys[lo] + (ys[hi] - ys[lo]) * u;
            }

            public static double RealBase(double[] real, int count, double t0Real)
            {
                if (real == null || count <= 0 || !(t0Real > 0.0)) return 0.0;
                return Math.Abs(real[0] - t0Real) < Math.Abs(real[0]) ? t0Real : 0.0;
            }

            public static int RopeMode(bool mesh, int lineDivisions) { return mesh ? 4 : lineDivisions > 2 ? 3 : 1; }

            public static bool ChainVisible(int[] chain, bool[] vis)
            {
                if (chain == null) return true;
                for (int i = 0; i < chain.Length; i++) if (chain[i] >= 0 && chain[i] < vis.Length && !vis[chain[i]]) return false;
                return true;
            }

            public static int CompareDepth(int depthA, int idA, int depthB, int idB) { return depthA != depthB ? depthA.CompareTo(depthB) : idA.CompareTo(idB); }
        }

    }
}
