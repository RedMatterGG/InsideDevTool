using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Replay mode REC_A (docs/REPLAY_DESIGN.md 2.3): record set, record pass preparation (RecordPrep), per-frame
    // sampling with change detection, spawn watch, objects.tsv, record_meta.csv and record.json; writes scene.srec through
    // SrecWriter (SrecFormat.cs, REC_B).
    // Contract (frozen public shapes; REC_A adds private or internal members only):
    // - CORE_A: Prepare before Begin (record set prep, forced cullers), Prebuild (internal) a few frames before sample 0 so the
    //   record set scan costs no recorded game time, Begin in the record step of the frame whose preCull takes sample 0 (header filled by CORE_A: t0 = Time.timeDbl, t0Real = Time.realtimeSinceStartupDbl of that step),
    //   SetVirtual and Event every record step, End once. `extra` holds top-level record.json keys CORE_A sets before End
    //   (pacing, runnerPhase, holds, audio); End writes them next to the recorder's own keys (extra wins on a clash).
    //   Begin fills only the header fields CORE_A left at their defaults (subscenes, forcedCullers and recorderVersion when
    //   empty) and always sets the recorder-owned ones (fogFields, postFields, cameraDriven, posEps, rotEps, cut).
    //   Event kinds "marker", "hold" and "game" also set SrecConst.SmplMarker / SmplHold / SmplGameFrame on the next sample.
    // - PLAY_B: `last` is the recorder of the last ended recording of this session (End sets it); Ref(id) is the live
    //   object of an object table id (Transform for transform kinds incl. KindSpawned, the component for component kinds,
    //   null if destroyed).
    // - PLAY_C: RecordSet lists the transforms a record with this spec would scan (roots, minus _Camera_Main, InsideDev*
    //   objects and record.exclude); withCameraDriven keeps the camXf objects (camera probe), else they are removed too.
    // - Mods and `srec event` (public, cafe round 2026-10-25): PostEvent(name, detail) stores a kind "event" row in the active
    //   recording (resolved by `@name`), `posted` reports every valid post, Register(target) records an object created
    //   outside the spawn containers. Subscenes of `subscene:` / `zone:` roots and record.keep join when they load, `auto`
    //   zone subscenes when they activate (SubsceneCuller.StaticEvents hooks plus a poll every rescanEvery samples); new
    //   children of spawned transforms are added in the sample they appear, of other recorded transforms within
    //   rescanEvery samples. Inline additions get the mover / quiet rules of the scan.
    // Stamp: gameTime = Time.timeDbl - header.t0, realTime = Time.realtimeSinceStartupDbl (absolute, 2.3.2; record_meta.csv
    // real_time is the offset from header.t0Real), logicStep = LogicManager.stepCount (absolute; header.stepAtT0 is the
    // base). Frames whose game time did not
    // advance are not sampled (the next sample carries SmplHold). Objects of a subtree that is inactive in hierarchy are
    // not read after sample 0; on activation they get a full cut key (2.3.2 cut rule) and their current flags.
    // Spawned objects: ObjAdd is called before any entry of the new ids, and they get their full first keys in the same
    // sample; a MaterialInstance property added at runtime gets MatProp and its first value in the same sample (the writer
    // puts sections 7 and 12 first). A pre-key entry (XfPreKey) precedes the key entry of the same id.
    // camDriven objects (BoundsCullerRuntime GameObjects, SubsceneCuller ContentRoots, CameraLightSwap lights, WaterEdge
    // edges, WaterDust dust objects): activeSelf goes to CamActive; their Flags entries keep FlActive set.
    // Begin extends the RecordPrep of Prepare to Animations, Animators and cullers that appeared in between (subscenes
    // activated by the keep-loaded prepass), logged and counted in record.json `prep`. Scale is read every sample for
    // 30 samples after an object's first sample and from its first scale change on (scaleVariant), else every 30th.
    // "none" in string-id arguments (no texture, no cookie, no material) is -1. objects.tsv keys = entries of sections 1, 2,
    // 3, 4, 5, 9, 13, 14, 15 for that id (pre-keys included), as the INDX key counts of SrecFormat.cs.
    // Shader state (material round 2026-10-25, SceneShaderState.cs): globals from their script writers, shared-material
    // values and renderer property blocks of known writers as change-only channels (sections 17/18), header.version 2.
    // Offline tests compile this file with -define:REC_A_MATH_ONLY (only the nested math types, see the bottom part).
#if !REC_A_MATH_ONLY
    public sealed class RecordPrep
    {
        internal static RecordPrep current;
        internal RecordSpec spec;
        internal readonly List<Animation> anims = new List<Animation>();
        internal readonly List<AnimationCullingType> animTypes = new List<AnimationCullingType>();
        internal readonly List<Animator> animators = new List<Animator>();
        internal readonly List<AnimatorCullingMode> animatorModes = new List<AnimatorCullingMode>();
        internal readonly List<BoundsCullerRuntime> forced = new List<BoundsCullerRuntime>();
        internal readonly List<bool> forcedWasActive = new List<bool>();
        internal readonly List<string> forcedNames = new List<string>();
        internal readonly HashSet<int> seen = new HashSet<int>();
        internal readonly List<Plane[]> frusta = new List<Plane[]>();
        internal string mode = "zone";
        internal int alreadyInvalid, extendedAnims, extendedCullers;
        bool animRestored, cullersReleased;
        ReplayHandover handover;

        public void Restore()
        {
            RestoreAnimation();
            if (handover == null) ReleaseCullers();
            if (current == this) current = null;
        }

        public ReplayHandover HandOver()
        {
            RestoreAnimation();
            if (handover == null)
            {
                handover = new ReplayHandover();
                if (!cullersReleased) foreach (var b in forced) if (b != null) handover.forced.Add(b);
                cullersReleased = true;
                DevLog.Write("srec: handover of " + handover.forced.Count + " forced culler(s) to the replay");
            }
            if (current == this) current = null;
            return handover;
        }

        internal bool Released { get { return animRestored; } }

        internal List<string> ActivatedGroups()
        {
            var r = new List<string>();
            for (int i = 0; i < forced.Count; i++)
            {
                var b = forced[i];
                if (b == null || forcedWasActive[i]) continue;
                try { if (b.gameObject.activeSelf) r.Add(forcedNames[i]); } catch { }
            }
            return r;
        }

        void RestoreAnimation()
        {
            if (animRestored) return;
            animRestored = true;
            for (int i = 0; i < anims.Count; i++) { try { if (anims[i] != null) anims[i].cullingType = animTypes[i]; } catch { } }
            for (int i = 0; i < animators.Count; i++) { try { if (animators[i] != null) animators[i].cullingMode = animatorModes[i]; } catch { } }
        }

        void ReleaseCullers()
        {
            if (cullersReleased) return;
            cullersReleased = true;
            int n = 0;
            foreach (var b in forced)
            {
                if (b == null) continue;
                try { b.valid = true; b.RefreshRuntimeState(); n++; } catch { }
            }
            if (n > 0) DevLog.Write("srec: released " + n + " forced culler(s)");
        }
    }

    public sealed partial class SceneRecorder
    {
        public static SceneRecorder active, last;
        internal static readonly List<SceneRecorder> sessionRecorders = new List<SceneRecorder>();
        internal int loadCountAtEnd = -1;
        internal static bool quietTransforms = true;

        // The newest recorder of this game session that wrote `recordDir` (or its take folder) with no level load since it
        // ended, so its live references are still the scene's objects; null = a fresh session for that recording. Every
        // recording counts, not only SceneRecorder.last: a manual srec record after a take keeps the take same-session.
        internal static SceneRecorder SessionRecording(string recordDir)
        {
            int lc = CurrentLoadCount();
            sessionRecorders.RemoveAll(r => r == null || r.loadCountAtEnd != lc);
            string want = NormDir(recordDir);
            if (want.Length == 0) return null;
            for (int i = sessionRecorders.Count - 1; i >= 0; i--)
            {
                var r = sessionRecorders[i];
                string d = NormDir(r.dir);
                if (string.Equals(d, want, StringComparison.OrdinalIgnoreCase) || string.Equals(NormDir(Path.Combine(want, ReplayConst.RecordFolder)), d, StringComparison.OrdinalIgnoreCase)) return r;
            }
            return null;
        }

        static int CurrentLoadCount() { try { return GameManager.LoadCount; } catch { return 0; } }

        static string NormDir(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            try { p = Path.GetFullPath(p); } catch { }
            return p.Replace('\\', '/').TrimEnd('/');
        }
        public RecordSpec spec;
        public string dir;
        public int samples, objects;
        public long bytes;
        public readonly Json.Obj extra = new Json.Obj();

        const string Version = "rec_a 1.2 (revision 3, shader state)";
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly bool[] FogIsColor = { true, true, false, false, true, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, true };
        static readonly string[] FogNames =
        {
            "AmbientColor", "FogColor", "FogStart", "FogEnd", "WaterFogColor", "WaterFogStart", "WaterFogEnd", "WaterFogOverride",
            "BloomIntensity", "BloomThreshold", "BloomThresholdHDR", "bloomSize_2", "bloomSize_4", "bloomSize_8", "bloomSize_16",
            "bloomIntensity_2", "bloomIntensity_4", "bloomIntensity_8", "bloomIntensity_16", "bloomIntensity_32", "bloomSaturation",
            "BackgroundColor"
        };

        // ------------------------------------------------------------------ record set scan

        sealed class Node
        {
            public Transform t;
            public GameObject go;
            public int parent = -1, depth;
            public string path = "", owner = "";
            public List<int> ord, sib;
            public byte kind = SrecConst.KindTransform;
            public bool spawned, forceWorld, camXf, camDriven, excludedMi;
            public Component[] comps;
            public uint mask;
            public List<string> types;
            public Rigidbody rb;
            public float extent = 0.1f;
            public bool hasOwn, hasSub;
            public Bounds own, sub;
        }

        sealed class Scan
        {
            public readonly List<Node> nodes = new List<Node>();
            public readonly Dictionary<int, int> byInstance = new Dictionary<int, int>();
            public readonly HashSet<int> skip = new HashSet<int>();
            public readonly List<string> subscenes = new List<string>();
            public readonly List<SubsceneNode> subsceneNodes = new List<SubsceneNode>();
            public readonly HashSet<int> camXfRoots = new HashSet<int>();
            public readonly HashSet<int> camDrivenGo = new HashSet<int>();
            public readonly HashSet<int> excludedMi = new HashSet<int>();
            public readonly HashSet<int> worldBones = new HashSet<int>();
            public readonly Dictionary<int, string> boyBones = new Dictionary<int, string>();
            public readonly Dictionary<int, int> clothBones = new Dictionary<int, int>();
            public readonly HashSet<int> containerIds = new HashSet<int>();
            public readonly List<Transform> containers = new List<Transform>();
            public readonly List<string> tsvPaths = new List<string>();
            public readonly List<string> notes = new List<string>();
            public readonly HashSet<string> camPathsAll = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<string> wantNames = new List<string>();
            public readonly List<string> wantZones = new List<string>();
            public readonly Dictionary<int, Transform> joined = new Dictionary<int, Transform>();
            public List<string> excludePaths;
            public List<Transform> sceneAll;
            public string zone = "";
            public bool auto, useBox;
            public Bounds box;
        }

        static string BoyZone(List<SubsceneCuller> cullers)
        {
            Vector3 bp;
            try { if (ScriptGlobals.boy == null) return ""; bp = ScriptGlobals.boy.transform.position; } catch { return ""; }
            foreach (var c in cullers)
            {
                try { if (c.IsActive && c.CullingBounds.Contains(bp)) return ZoneBase(c.SceneName); } catch { }
            }
            return "";
        }

        static List<Transform> FindPath(Scan s, string q)
        {
            var res = new List<Transform>();
            q = (q ?? "").Trim().Trim('"');
            if (q.Length == 0) return res;
            if (s.sceneAll == null) { try { s.sceneAll = Inspector.AllSceneTransforms(); } catch { s.sceneAll = new List<Transform>(); } }
            bool slash = q.IndexOf('/') >= 0;
            if (slash)
            {
                foreach (var t in s.sceneAll) if (t != null && Inspector.PathOf(t) == q) res.Add(t);
                if (res.Count > 0) return res;
                string suffix = "/" + q;
                foreach (var t in s.sceneAll) if (t != null && Inspector.PathOf(t).EndsWith(suffix, StringComparison.Ordinal)) res.Add(t);
            }
            else foreach (var t in s.sceneAll) if (t != null && t.name == q) res.Add(t);
            return res;
        }

        static bool Excluded(Scan s, Transform t)
        {
            if (s.skip.Contains(t.GetInstanceID())) return true;
            return t.name.StartsWith("InsideDev", StringComparison.Ordinal);
        }

        static bool UnderSkip(Scan s, Transform t)
        {
            for (var p = t; p != null; p = p.parent) if (Excluded(s, p)) return true;
            return false;
        }

        static void ChainOf(Transform t, out List<int> ord, out List<int> sib)
        {
            var chain = new List<Transform>();
            for (var p = t; p != null; p = p.parent) chain.Add(p);
            chain.Reverse();
            ord = new List<int>(chain.Count);
            sib = new List<int>(chain.Count);
            foreach (var c in chain)
            {
                int o = 0;
                var par = c.parent;
                if (par != null)
                {
                    int si = c.GetSiblingIndex();
                    for (int j = 0; j < si && j < par.childCount; j++) if (par.GetChild(j).name == c.name) o++;
                }
                ord.Add(o);
                sib.Add(c.GetSiblingIndex());
            }
        }

        sealed class Pending { public Transform t; public int parent; public string path; public int depth; public List<int> ord, sib; }

        static int Walk(Scan s, Transform root, bool spawned, string owner)
        {
            if (root == null || s.byInstance.ContainsKey(root.GetInstanceID()) || UnderSkip(s, root)) return -1;
            List<int> ord, sib;
            ChainOf(root, out ord, out sib);
            int parentNode = -1;
            var rp = root.parent;
            if (rp != null) { int pi; if (s.byInstance.TryGetValue(rp.GetInstanceID(), out pi)) parentNode = pi; }
            int depth = 0;
            for (var p = root.parent; p != null; p = p.parent) depth++;
            string rootPath = Inspector.PathOf(root);
            if (s.excludePaths != null && ExcludedPath(s.excludePaths, rootPath, root.name)) return -1;
            var stack = new Stack<Pending>();
            stack.Push(new Pending { t = root, parent = parentNode, path = rootPath, depth = depth, ord = ord, sib = sib });
            int first = s.nodes.Count;
            var counts = new Dictionary<string, int>();
            var kids = new List<Pending>();
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                var t = e.t;
                if (t == null) continue;
                int inst = t.GetInstanceID();
                if (s.byInstance.ContainsKey(inst)) continue;
                var n = new Node { t = t, go = t.gameObject, parent = e.parent, path = e.path, depth = e.depth, ord = e.ord, sib = e.sib };
                n.spawned = spawned || (e.parent >= 0 && s.nodes[e.parent].spawned) || (e.parent >= 0 && s.containerIds.Contains(s.nodes[e.parent].t.GetInstanceID()));
                if (n.spawned) n.kind = SrecConst.KindSpawned;
                if (t == root && !string.IsNullOrEmpty(owner)) n.owner = owner;
                else if (n.spawned && e.parent >= 0 && s.containerIds.Contains(s.nodes[e.parent].t.GetInstanceID())) n.owner = "spawned:" + s.nodes[e.parent].t.name;
                int idx = s.nodes.Count;
                s.nodes.Add(n);
                s.byInstance[inst] = idx;
                Classify(s, n);
                counts.Clear();
                kids.Clear();
                int cc = t.childCount;
                for (int i = 0; i < cc; i++)
                {
                    var c = t.GetChild(i);
                    if (c == null) continue;
                    int o;
                    counts.TryGetValue(c.name, out o);
                    counts[c.name] = o + 1;
                    if (Excluded(s, c)) continue;
                    string cp = n.path + "/" + c.name;
                    if (s.excludePaths != null && ExcludedPath(s.excludePaths, cp, c.name)) continue;
                    var co = new List<int>(n.ord); co.Add(o);
                    var cs = new List<int>(n.sib); cs.Add(i);
                    kids.Add(new Pending { t = c, parent = idx, path = cp, depth = n.depth + 1, ord = co, sib = cs });
                }
                for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
            }
            return first;
        }

        static void Classify(Scan s, Node n)
        {
            var comps = n.go.GetComponents<Component>();
            n.comps = comps;
            n.types = new List<string>();
            foreach (var c in comps)
            {
                if (c == null || c is Transform) continue;
                if (c is Rigidbody) { n.mask |= SrecConst.CompRigidbody; if (n.rb == null) n.rb = (Rigidbody)c; }
                else if (c is Animation) n.mask |= SrecConst.CompAnimation;
                else if (c is Animator) n.mask |= SrecConst.CompAnimator;
                else if (c is ParticleSystem) n.mask |= SrecConst.CompParticleSystem;
                else if (c is Light) n.mask |= SrecConst.CompLight;
                else if (c is Renderer)
                {
                    n.mask |= SrecConst.CompRenderer;
                    if (c is SkinnedMeshRenderer) n.mask |= SrecConst.CompSkinnedMesh;
                    if (c is LineRenderer) n.mask |= SrecConst.CompLineRenderer;
                }
                else if (c is D11VelocityBufferTag) n.mask |= SrecConst.CompVelocityTag;
                else if (c is Collider) n.mask |= SrecConst.CompCollider;
                var mb = c as MonoBehaviour;
                if (mb == null) continue;
                n.types.Add(c.GetType().Name);
                if (c is MaterialInstance) n.mask |= SrecConst.CompMaterialInstance;
                else if (c is ColorBlendProbe) n.mask |= SrecConst.CompColorBlendProbe;
                else if (c is Actor) n.mask |= SrecConst.CompActor;
                if (c is LensFlareSimple) { s.camXfRoots.Add(n.t.GetInstanceID()); n.excludedMi = true; }
                else if (c is AudioCameraProbe) s.camXfRoots.Add(n.t.GetInstanceID());
                else if (c is BoyToShader) n.excludedMi = true;
                else if (c is BoundsCullerRuntime) s.camDrivenGo.Add(n.t.GetInstanceID());
                else if (c is WaterEdge) MarkWaterEdge(s, (WaterEdge)c);
                else if (c is WaterDust)
                {
                    var wd = (WaterDust)c;
                    if (wd.dustObject != null) { var dt = wd.dustObject.transform; s.camXfRoots.Add(dt.GetInstanceID()); s.camDrivenGo.Add(dt.GetInstanceID()); }
                }
                else if (c is CameraLightSwap)
                {
                    var cl = (CameraLightSwap)c;
                    if (cl.lightLeft != null) s.camDrivenGo.Add(cl.lightLeft.GetInstanceID());
                    if (cl.lightRight != null) s.camDrivenGo.Add(cl.lightRight.GetInstanceID());
                }
            }
        }

        static void MarkWaterEdge(Scan s, WaterEdge we)
        {
            if (we.edge != null)
            {
                s.camXfRoots.Add(we.edge.GetInstanceID());
                s.camDrivenGo.Add(we.edge.GetInstanceID());
                foreach (var mi in we.edge.GetComponentsInChildren<MaterialInstance>(true)) if (mi != null) s.excludedMi.Add(mi.GetInstanceID());
            }
            if (we.inverseTransform != null) s.camXfRoots.Add(we.inverseTransform.GetInstanceID());
            foreach (var q in we.GetComponentsInChildren<WaterQueue>(true))
            {
                if (q == null) continue;
                var mi = q.GetComponent<MaterialInstance>();
                if (mi != null) s.excludedMi.Add(mi.GetInstanceID());
            }
        }

        static void Finish(Scan s, int from)
        {
            var nodes = s.nodes;
            for (int i = from; i < nodes.Count; i++)
            {
                var n = nodes[i];
                int inst = n.t.GetInstanceID();
                if (s.camXfRoots.Contains(inst) || (n.parent >= 0 && nodes[n.parent].camXf)) n.camXf = true;
                if (s.camDrivenGo.Contains(inst)) n.camDriven = true;
                if (n.camXf) n.excludedMi = true;
                string bone;
                int ci;
                if (s.boyBones.TryGetValue(inst, out bone)) { n.kind = SrecConst.KindBoyBone; n.owner = "boy:bone:" + bone; n.forceWorld = true; }
                else if (s.clothBones.TryGetValue(inst, out ci)) { n.kind = SrecConst.KindClothBone; n.owner = "huddle:cloth:" + ci.ToString(IC); n.forceWorld = true; }
                else if (s.worldBones.Contains(inst)) n.forceWorld = true;
                if (n.comps == null) continue;
                foreach (var c in n.comps)
                {
                    var r = c as Renderer;
                    if (r == null || r is ParticleSystemRenderer || r.name.StartsWith("DECAL", StringComparison.OrdinalIgnoreCase)) continue;
                    bool shown;
                    try { shown = r.enabled && r.gameObject.activeInHierarchy; } catch { shown = false; }
                    if (!shown) continue;
                    var b = r.bounds;
                    if (!n.hasOwn) { n.own = b; n.hasOwn = true; } else n.own.Encapsulate(b);
                }
                if (n.hasOwn) { n.sub = n.own; n.hasSub = true; }
            }
            for (int i = nodes.Count - 1; i >= from; i--)
            {
                var n = nodes[i];
                if (!n.hasSub || n.parent < from || n.parent >= i) continue;
                var p = nodes[n.parent];
                if (!p.hasSub) { p.sub = n.sub; p.hasSub = true; } else p.sub.Encapsulate(n.sub);
            }
            for (int i = from; i < nodes.Count; i++)
            {
                var n = nodes[i];
                float e = n.hasOwn ? n.own.extents.magnitude : n.hasSub ? n.sub.extents.magnitude : 0.1f;
                n.extent = Math.Max(0.1f, e);
            }
        }

        static void AddBoy(Scan s, SpecReport report)
        {
            Boy boy = null;
            try { boy = ScriptGlobals.boy; } catch { }
            var rootGo = GameObject.Find("_Boy");
            if (rootGo != null) Walk(s, rootGo.transform, false, "boy:root");
            if (boy == null) { if (rootGo == null) report.Warn("replay.record.roots", "boy: no _Boy root and no ScriptGlobals.boy"); return; }
            Actor actor = null;
            try { actor = boy.visualBoy != null ? boy.visualBoy.actor : null; } catch { }
            if (actor == null) { report.Warn("replay.record.roots", "boy: no visualBoy actor, bones not recorded as owner references"); return; }
            int nb = 0;
            try { nb = actor.numBones; } catch { }
            for (int i = 0; i < nb; i++)
            {
                Transform bt = null;
                try { bt = actor.bones[i].transform; } catch { }
                if (bt != null) s.boyBones[bt.GetInstanceID()] = bt.name;
            }
            Walk(s, actor.transform, false, "");
            for (int i = 0; i < nb; i++)
            {
                Transform bt = null;
                try { bt = actor.bones[i].transform; } catch { }
                if (bt != null) Walk(s, bt, false, "");
            }
        }

        static bool HuddlePresent()
        {
            try { var h = ScriptGlobals.huddle; return h != null && h.isCharacterActive; } catch { return false; }
        }

        static void AddHuddle(Scan s, SpecReport report)
        {
            Huddle h = null;
            try { h = ScriptGlobals.huddle; } catch { }
            var rootGo = GameObject.Find("_Huddle");
            if (rootGo != null) Walk(s, rootGo.transform, false, "huddle:root");
            if (h == null) { if (rootGo == null) report.Warn("replay.record.roots", "huddle: no _Huddle root and no ScriptGlobals.huddle"); return; }
            var extra = new List<Transform>();
            try
            {
                var cloth = h.cloth;
                var bones = cloth != null ? cloth.bones : null;
                if (bones != null)
                    for (int i = 0; i < bones.Length; i++)
                    {
                        var vt = bones[i] != null ? bones[i].visualTransform : null;
                        if (vt == null) continue;
                        s.clothBones[vt.GetInstanceID()] = i;
                        extra.Add(vt);
                    }
            }
            catch (Exception e) { report.Warn("replay.record.roots", "huddle cloth bones: " + e.Message); }
            var meshRoots = new List<Transform>();
            if (rootGo != null) meshRoots.Add(rootGo.transform);
            try { if (h.visualHuddle != null) meshRoots.Add(h.visualHuddle.transform); } catch { }
            foreach (var mr in meshRoots)
                foreach (var smr in mr.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr == null) continue;
                    var bs = smr.bones;
                    if (bs == null) continue;
                    foreach (var b in bs) if (b != null) { s.worldBones.Add(b.GetInstanceID()); extra.Add(b); }
                }
            foreach (var t in extra) Walk(s, t, false, "");
        }

        static void AddContainer(Scan s, Transform c)
        {
            if (c == null) return;
            int inst = c.GetInstanceID();
            if (!s.containerIds.Add(inst)) return;
            s.containers.Add(c);
            Walk(s, c, false, "");
        }

        static void AddContainers(Scan s)
        {
            var d = GameObject.Find("_Dynamics");
            if (d != null) AddContainer(s, d.transform);
            foreach (var sn in s.subsceneNodes)
            {
                if (sn == null) continue;
                var c = sn.transform.Find("_SubsceneDynamics");
                if (c != null) AddContainer(s, c);
            }
            var g = GameObject.Find("_SubsceneDynamics");
            if (g != null) AddContainer(s, g.transform);
        }

        static bool AddSubscene(Scan s, SubsceneCuller c, string name)
        {
            Transform root = null;
            try { if (c.SubsceneNode != null) root = c.ContentRoot; } catch { }
            if (root == null) return false;
            if (!s.subscenes.Contains(name)) s.subscenes.Add(name);
            try { if (c.SubsceneNode != null && !s.subsceneNodes.Contains(c.SubsceneNode)) s.subsceneNodes.Add(c.SubsceneNode); } catch { }
            s.camDrivenGo.Add(root.GetInstanceID());
            s.joined[c.GetInstanceID()] = root;
            Walk(s, root, false, "subscene:" + name);
            return true;
        }

        internal static SubsceneCuller FindCuller(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return null;
            SubsceneCuller c = null;
            try { c = SubsceneCullingManager.instance.GetSubsceneCullerByName(name); } catch { }
            if (c != null) return c;
            try
            {
                var all = SubsceneCullingManager.instance.GetAllSubsceneCullers();
                for (int i = 0; i < all.Count; i++)
                {
                    var x = all[i];
                    if (x != null && (string.Equals(Plain(x.SceneName), Plain(name), StringComparison.OrdinalIgnoreCase) || x.name == name)) return x;
                }
            }
            catch { }
            return null;
        }

        static void AddNamedSubscene(Scan s, string name, RecordSpec spec, SpecReport report, string where)
        {
            var c = FindCuller(name);
            if (c == null) { report.Error(where, "no subscene culler '" + name + "'"); return; }
            string bn;
            bool loaded = false, act = false;
            try { bn = SceneBase(c.SceneName); loaded = c.IsLoaded && c.SubsceneNode != null; act = c.IsActive; } catch { bn = SceneBase(name); }
            if (!s.wantNames.Contains(bn)) s.wantNames.Add(bn);
            if (!loaded) report.Warn(where, "subscene '" + name + "' is not loaded: it joins the recording when it loads (stream hook, rescanEvery)");
            else
            {
                if (!act) report.Note(where, "subscene '" + name + "' is loaded but inactive: recorded from sample 0, read once it activates");
                AddSubscene(s, c, bn);
            }
            if (spec.spawned) AddContainers(s);
        }

        static void AddZone(Scan s, string arg, RecordSpec spec, SpecReport report)
        {
            string z = ZoneBase(arg);
            if (z.Length == 0) { report.Error("replay.record.roots", "zone: needs a zone name (zone:huddleCeoFallAndDogs)"); return; }
            if (!s.wantZones.Contains(z)) s.wantZones.Add(z);
            int found = 0, added = 0;
            try
            {
                var all = SubsceneCullingManager.instance.GetAllSubsceneCullers();
                for (int i = 0; i < all.Count; i++)
                {
                    var c = all[i];
                    string n;
                    try { if (c == null) continue; n = c.SceneName; } catch { continue; }
                    if (!InZone(n, z)) continue;
                    found++;
                    if (AddSubscene(s, c, SceneBase(n))) added++;
                }
            }
            catch (Exception e) { report.Error("replay.record.roots", "zone:" + z + ": " + e.Message); return; }
            if (found == 0) { report.Error("replay.record.roots", "zone:" + z + ": no subscene culler of that zone"); return; }
            if (added < found) report.Note("replay.record.roots", "zone:" + z + ": " + (found - added) + " of " + found + " subscene(s) not loaded: they join the recording when they load");
            if (spec.spawned) AddContainers(s);
        }

        static void AddAuto(Scan s, RecordSpec spec, SpecReport report)
        {
            var mgr = SubsceneCullingManager.instance;
            int sub, sp;
            string zone = "";
            if (G.CurrentSavepoint(out sub, out sp)) { try { zone = ZoneBase(SavepointManager.GetSubsceneName(sub)); } catch { } }
            var cullers = new List<SubsceneCuller>();
            try { foreach (var c in mgr.GetAllSubsceneCullers()) if (c != null) cullers.Add(c); } catch (Exception e) { report.Error("replay.record.roots", "subscene cullers: " + e.Message); }
            bool any = false;
            foreach (var c in cullers) { try { if (c.IsActive && InZone(c.SceneName, zone)) { any = true; break; } } catch { } }
            if (!any)
            {
                string bz = BoyZone(cullers);
                if (string.IsNullOrEmpty(bz)) { report.Error("replay.record.roots", "auto: no active subscene of the savepoint zone '" + zone + "' and none around the boy"); return; }
                report.Warn("replay.record.roots", "auto: " + (zone.Length > 0 ? "no active subscene of the savepoint zone '" + zone + "'" : "no current savepoint") + ", zone '" + bz + "' taken from the subscene around the boy");
                zone = bz;
            }
            s.zone = zone;
            Bounds box = new Bounds();
            bool useBox = spec.box != null && spec.box.size != Vector3.zero;
            if (useBox) box = new Bounds(spec.box.center, spec.box.size);
            s.auto = true;
            s.useBox = useBox;
            s.box = box;
            int added = 0;
            foreach (var c in cullers)
            {
                string n;
                try { if (!c.IsActive) continue; n = c.SceneName; } catch { continue; }
                if (!InZone(n, zone)) continue;
                if (useBox) { try { if (!box.Intersects(c.CullingBounds)) continue; } catch { continue; } }
                AddSubscene(s, c, SceneBase(n));
                added++;
            }
            if (added == 0) report.Error("replay.record.roots", "auto: no loaded and active subscene of zone '" + zone + "'" + (useBox ? " inside record.box" : ""));
            if (spec.characters) { AddBoy(s, report); if (HuddlePresent()) AddHuddle(s, report); }
            if (spec.spawned) AddContainers(s);
        }

        static Scan BuildScan(RecordSpec spec, string recordDir, SpecReport report)
        {
            var s = new Scan();
            var cam = GameObject.Find("_Camera_Main");
            if (cam != null) s.skip.Add(cam.transform.root.GetInstanceID());
            try { var wc = RenderHost.WorldCam(); if (wc != null) s.skip.Add(wc.transform.root.GetInstanceID()); } catch { }
            if (spec.exclude != null) foreach (var ex in spec.exclude)
            {
                var hits = FindPath(s, ex);
                if (hits.Count == 0) report.Warn("replay.record.exclude", "no object '" + ex + "'");
                foreach (var t in hits) s.skip.Add(t.GetInstanceID());
            }
            var roots = spec.roots != null && spec.roots.Count > 0 ? spec.roots : new List<string>(ReplayConst.DefaultRoots);
            foreach (var raw in roots)
            {
                string r = (raw ?? "").Trim();
                if (r.Length == 0) continue;
                if (r == "auto") AddAuto(s, spec, report);
                else if (r == "boy") AddBoy(s, report);
                else if (r == "huddle") AddHuddle(s, report);
                else if (r.StartsWith("subscene:", StringComparison.Ordinal)) AddNamedSubscene(s, r.Substring(9).Trim(), spec, report, "replay.record.roots");
                else if (r.StartsWith("zone:", StringComparison.Ordinal)) AddZone(s, r.Substring(5).Trim(), spec, report);
                else
                {
                    var hits = FindPath(s, r);
                    if (hits.Count == 0) { report.Error("replay.record.roots", "no object '" + r + "'"); continue; }
                    if (hits.Count > 1) report.Warn("replay.record.roots", "'" + r + "' matches " + hits.Count + " objects, recording all of them");
                    foreach (var t in hits) Walk(s, t, false, "");
                }
            }
            if (spec.keep != null)
                foreach (var raw in spec.keep)
                {
                    string k = (raw ?? "").Trim();
                    if (k.Length == 0) continue;
                    var c = FindCuller(k);
                    string bn = c != null ? SceneBase(c.SceneName) : SceneBase(k);
                    if (s.wantNames.Contains(bn)) continue;
                    AddNamedSubscene(s, k, spec, report, "replay.record.keep");
                }
            LoadCameraDriven(s, recordDir, report);
            Finish(s, 0);
            return s;
        }

        static void LoadCameraDriven(Scan s, string recordDir, SpecReport report)
        {
            string modDir = null;
            try { modDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"); } catch { }
            var files = CameraDrivenFiles(recordDir, modDir, ReplayConst.CameraDrivenFile, 256);
            Dictionary<string, int> byPath = null;
            foreach (var f in files)
            {
                string[] lines;
                try { lines = File.ReadAllLines(f); } catch (Exception e) { report.Warn("replay.record", f + ": " + e.Message); continue; }
                if (byPath == null) { byPath = new Dictionary<string, int>(); for (int i = 0; i < s.nodes.Count; i++) byPath[s.nodes[i].path] = i; }
                int used = 0, rigid = 0, rigidHere = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    int before = rigid;
                    string path = CameraDrivenRow(lines[i], ref rigid);
                    if (path == null)
                    {
                        if (rigid > before) { var rp = lines[i].Split('\t')[0]; if (byPath.ContainsKey(rp)) rigidHere++; }
                        continue;
                    }
                    s.camPathsAll.Add(path);
                    int ni;
                    if (!byPath.TryGetValue(path, out ni)) continue;
                    s.camXfRoots.Add(s.nodes[ni].t.GetInstanceID());
                    if (!s.tsvPaths.Contains(path)) s.tsvPaths.Add(path);
                    used++;
                }
                if (used == 0 && rigidHere == 0) continue;
                s.notes.Add(f + ": " + used + " camera-driven transform(s) of the set" + (rigidHere > 0 ? ", " + rigidHere + " rigid row(s) ignored" : ""));
                if (rigidHere > 0) report.Warn("replay.record", f + ": " + rigidHere + " row(s) of the record set marked rigid are recorded normally (a rigid gameplay object must not be camera-driven; re-run srec probe camera)");
            }
        }

        public static List<Transform> RecordSet(RecordSpec spec, bool withCameraDriven, SpecReport report)
        {
            if (report == null) report = new SpecReport();
            if (spec == null) spec = new RecordSpec();
            var s = BuildScan(spec, null, report);
            var r = new List<Transform>(s.nodes.Count);
            foreach (var n in s.nodes) if (n.t != null && (withCameraDriven || !n.camXf)) r.Add(n.t);
            return r;
        }

        // ------------------------------------------------------------------ record pass preparation (2.3.3)

        public static RecordPrep Prepare(RecordSpec spec, List<CamPose> planned, float aspect, SpecReport report)
        {
            if (report == null) report = new SpecReport();
            if (spec == null) spec = new RecordSpec();
            if (RecordPrep.current != null)
            {
                report.Warn("replay.record", "a previous RecordPrep was still active; restored first");
                RecordPrep.current.Restore();
            }
            var prep = new RecordPrep { spec = spec };
            prep.mode = string.IsNullOrEmpty(spec.forceCullers) ? "zone" : spec.forceCullers;
            if (prep.mode == "planned")
            {
                if (planned != null) foreach (var p in planned) if (p.ok || p.fov > 0f) prep.frusta.Add(Frustum(p, aspect > 0f ? aspect : 16f / 9f));
                if (prep.frusta.Count == 0) report.Warn("replay.record.forceCullers", "planned: no planned camera pose, no culler forced");
            }
            var s = BuildScan(spec, null, report);
            int cullers = ApplyPrep(prep, s, report, false);
            int inactive = 0;
            foreach (var w in prep.forcedWasActive) if (!w) inactive++;
            string msg = "record prep: " + prep.anims.Count + " Animation(s) and " + prep.animators.Count + " Animator(s) set to AlwaysAnimate, " +
                         prep.forced.Count + " of " + cullers + " culler(s) forced (" + prep.mode + "), " + inactive + " group(s) inactive before forcing" +
                         (prep.alreadyInvalid > 0 ? ", " + prep.alreadyInvalid + " already invalid" : "");
            report.Note("replay.record", msg);
            DevLog.Write("srec: " + msg);
            for (int i = 0; i < prep.forced.Count; i++) if (!prep.forcedWasActive[i]) DevLog.Write("srec: forced culler group becomes active: " + prep.forcedNames[i]);
            RecordPrep.current = prep;
            return prep;
        }

        static int ApplyPrep(RecordPrep prep, Scan s, SpecReport report, bool extending)
        {
            var spec = prep.spec;
            var cullers = new List<BoundsCullerRuntime>();
            foreach (var n in s.nodes)
            {
                if (n.comps == null) continue;
                foreach (var c in n.comps)
                {
                    if (c == null || c is Transform || !prep.seen.Add(c.GetInstanceID())) continue;
                    if (spec.alwaysAnimate)
                    {
                        var an = c as Animation;
                        if (an != null) { prep.anims.Add(an); prep.animTypes.Add(an.cullingType); an.cullingType = AnimationCullingType.AlwaysAnimate; if (extending) prep.extendedAnims++; continue; }
                        var at = c as Animator;
                        if (at != null) { prep.animators.Add(at); prep.animatorModes.Add(at.cullingMode); at.cullingMode = AnimatorCullingMode.AlwaysAnimate; if (extending) prep.extendedAnims++; continue; }
                    }
                    var bc = c as BoundsCullerRuntime;
                    if (bc != null) cullers.Add(bc);
                }
            }
            var pick = new List<BoundsCullerRuntime>();
            if (prep.mode == "zone") pick.AddRange(cullers);
            else if (prep.mode == "planned")
            {
                foreach (var b in cullers)
                {
                    Bounds bb;
                    if (!CullerBounds(b, out bb)) continue;
                    foreach (var f in prep.frusta) if (GeometryUtility.TestPlanesAABB(f, bb)) { pick.Add(b); break; }
                }
            }
            else if (prep.mode == "list")
            {
                foreach (var name in spec.cullers)
                {
                    bool hit = false;
                    foreach (var b in cullers)
                    {
                        string path = Inspector.PathOf(b.transform);
                        if (b.name == name || path == name || path.EndsWith("/" + name, StringComparison.Ordinal)) { if (!pick.Contains(b)) pick.Add(b); hit = true; }
                    }
                    if (!hit && !extending) report.Warn("replay.record.cullers", "no bounds culler '" + name + "' in the record set");
                }
            }
            foreach (var b in pick)
            {
                try
                {
                    if (!b.valid) { prep.alreadyInvalid++; continue; }
                    bool was = b.gameObject.activeSelf;
                    b.valid = false;
                    b.RefreshRuntimeState();
                    prep.forced.Add(b);
                    prep.forcedWasActive.Add(was);
                    prep.forcedNames.Add(Inspector.PathOf(b.transform));
                    if (extending)
                    {
                        prep.extendedCullers++;
                        if (!was) DevLog.Write("srec: forced culler group becomes active (found at Begin): " + prep.forcedNames[prep.forcedNames.Count - 1]);
                    }
                }
                catch (Exception e) { report.Warn("replay.record.forceCullers", b.name + ": " + e.Message); }
            }
            return cullers.Count;
        }

        static bool CullerBounds(BoundsCullerRuntime b, out Bounds bb)
        {
            bb = new Bounds();
            try
            {
                bb = b.aabb;
                if (bb.size != Vector3.zero) return true;
                var pts = b.points;
                if (pts == null || pts.Length == 0) return false;
                bb = new Bounds(pts[0], Vector3.zero);
                for (int i = 1; i < pts.Length; i++) bb.Encapsulate(pts[i]);
                return true;
            }
            catch { return false; }
        }

        static Plane[] Frustum(CamPose p, float aspect)
        {
            float near = p.near > 0f ? p.near : MultiCamConst.GameNear;
            var proj = Matrix4x4.Perspective(p.fov > 0f ? p.fov : MultiCamConst.GameFov, aspect, Mathf.Max(0.05f, near), MultiCamConst.GameFar);
            var w2c = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(p.p, p.r, Vector3.one).inverse;
            return GeometryUtility.CalculateFrustumPlanes(proj * w2c);
        }

        // ------------------------------------------------------------------ per-object sampling state

        struct XfItem
        {
            public Transform t;
            public GameObject go;
            public Rigidbody rb;
            public int id, parentIdx, parentId, parentInst;
            public bool local, camXf, camDriven, dead, scaleVariant;
            public float rotThr;
            public XfTrack s;
            public XfQuiet q;
            public bool activeSelf, activeH, kinematic, kinematicKnown, camActive, camActiveSet, spawnedXf, everActive, scaleWatch;
            public int seenAt, firstAt, kids;
            public byte reason;
        }

        struct RendItem
        {
            public Renderer r;
            public int id, owner, slots;
            public bool enabled, dead, matSet, meshSet;
            public Material mat0;
            public Material[] mats;
            public SkinnedMeshRenderer smr;
            public Mesh mesh;
        }

        struct LightItem
        {
            public Light l;
            public int id, owner;
            public bool set, dead;
            public SrecLight last;
            public Texture cookie;
        }

        struct PsItem
        {
            public ParticleSystem ps;
            public int id, owner, lastCount, playingZero, playingSamples;
            public bool dead, stateSet, playing, paused, everCount;
            public float rate;
            public ParticleSystem.Particle[] buf;
        }

        sealed class MiItem
        {
            public MaterialInstance mi;
            public int id, owner;
            public bool dead, excluded, shrunk;
            public readonly int[] names = new int[4];
            public readonly int[] values = new int[4];
            public float[] f = new float[0];
            public Color[] c = new Color[0];
            public Vector4[] v = new Vector4[0];
            public Texture[] tx = new Texture[0];
        }

        struct ProbeItem
        {
            public ColorBlendProbe p;
            public int id, owner;
            public bool set, dead;
            public Vector3 last;
        }

        sealed class RopeItem
        {
            public RopeLineRenderer r;
            public int id, owner, n;
            public bool set, dead;
            public Vector3[] cur = new Vector3[64], last = new Vector3[64];
        }

        sealed class Container
        {
            public Transform t;
            public int xf;
            public int[] known = new int[64];
            public int count;
        }

        // ------------------------------------------------------------------ recorder state

        RecordHeader header;
        SrecWriter writer;
        RecordPrep prep;
        string file = "";
        bool ended, failed, particlesOk = true, dynamicsFound;
        int lastFrame = -1;
        double lastGameTime = double.NegativeInfinity, lastReal;
        long lastStep;
        ushort pendingFlags;
        int errors, consecutiveErrors, skippedFrames, sampleErrorsLogged;
        string lastError = "";

        XfItem[] xs = new XfItem[0]; int nx;
        RendItem[] rs = new RendItem[0]; int nr;
        LightItem[] ls = new LightItem[0]; int nl;
        PsItem[] ps = new PsItem[0]; int np;
        MiItem[] ms = new MiItem[0]; int nm;
        ProbeItem[] pbs = new ProbeItem[0]; int npb;
        RopeItem[] ropes = new RopeItem[0]; int nrope;
        readonly List<Container> containers = new List<Container>();
        readonly List<SubsceneNode> subsceneNodes = new List<SubsceneNode>();
        readonly List<UnityEngine.Object> refs = new List<UnityEngine.Object>();
        readonly List<SrecObject> table = new List<SrecObject>();
        readonly Dictionary<int, int> xfByInstance = new Dictionary<int, int>();
        readonly Dictionary<int, int> texIds = new Dictionary<int, int>();
        readonly Dictionary<int, int> matIds = new Dictionary<int, int>();
        readonly Dictionary<Type, string> typeNames = new Dictionary<Type, string>();
        byte[] flagNow = new byte[0], flagLast = new byte[0];
        bool[] flagHas = new bool[0], flagWritten = new bool[0];
        int[] keyFirst = new int[0], keyLast = new int[0], keyCount = new int[0];
        int[] dels = new int[64]; int ndel;
        readonly List<SrecObject> adds = new List<SrecObject>();
        int[] childBuf = new int[256];

        readonly Vector4[] fogNow = new Vector4[FogNames.Length], fogLast = new Vector4[FogNames.Length];
        readonly byte[] fogType = new byte[FogNames.Length];
        bool fogSet, fogOn;

        readonly SrecVirtual virt = new SrecVirtual();
        int virtFrame = -1;

        readonly List<string> evKind = new List<string>(), evName = new List<string>(), evDetail = new List<string>();
        readonly List<string> warnings = new List<string>();
        readonly List<string> scanNotes = new List<string>();
        readonly List<string> cameraDriven = new List<string>();
        readonly List<string> staleParticles = new List<string>();
        List<string> activatedGroups = new List<string>();
        string zone = "";
        int excludedMaterials, camXfCount, camDrivenCount, spawnedAdded, spawnedDeleted, ropeFallback;
        long xfKeys, preKeys, cuts, particleFrames, particlesTotal, parentChanges;
        int quietEligible, quietEntered, quietMisses, quietCooled;
        readonly int[] xfByReason = new int[9];
        readonly long[] readsByReason = new long[9];
        long quietSkips, quietReads;
        readonly List<string> quietMissPaths = new List<string>();
        static readonly string[] ChannelNames = { "spawns", "transforms", "renderers", "lights", "flags", "particles", "materials", "probes", "ropes", "fog", "virtual", "write", "shader" };
        float[] mChan = new float[0];
        readonly float[] chanNow = new float[ChannelNames.Length];
        long chanT;
        double prebuildMs = -1, buildMs;
        int builtStream, builtFrame;
        Scan scanned;
        static SceneRecorder pending;
        int gcAt30 = -1, gcEnd = -1;
        long heapAt30 = -1;
        double maxDriftMs, endDriftMs;
        int hitches;

        readonly List<SubsceneCuller> cullerList = new List<SubsceneCuller>();
        readonly Dictionary<int, int> cullerIndex = new Dictionary<int, int>();
        bool[] cullerLoadWant = new bool[0], cullerActWant = new bool[0];
        Transform[] cullerRoot = new Transform[0];
        readonly List<int> streamQueue = new List<int>();
        readonly List<string> streamWhy = new List<string>();
        readonly HashSet<int> skipIds = new HashSet<int>();
        readonly List<int> spawnedXfs = new List<int>();
        List<string> excludePaths;
        HashSet<string> camPaths;
        Action<SubsceneCuller> onLoaded, onActivated;
        bool streamHooked;
        int joinsDone, cullersWatched, registered, destroyedSpawned;
        double joinMsMax;
        readonly List<object> joinLog = new List<object>();
        readonly List<string> destroyedPaths = new List<string>();
        static readonly List<Transform> registerQueue = new List<Transform>();
        const double JoinWarnMs = 8.0;
        const int MaxEventLog = 2000;
        readonly List<string> logName = new List<string>(), logDetail = new List<string>();
        readonly List<int> logSample = new List<int>();
        readonly List<double> logTime = new List<double>();
        int posts, eventsLogged, meshSwaps;
        readonly Dictionary<string, Mesh> meshRefs = new Dictionary<string, Mesh>();

        int metaN;
        int[] mFrame = new int[0], mXf = new int[0], mParts = new int[0];
        double[] mGame = new double[0], mDt = new double[0], mReal = new double[0];
        long[] mStep = new long[0], mBytes = new long[0];
        ushort[] mFixed = new ushort[0], mFlags = new ushort[0];
        float[] mScale = new float[0], mCost = new float[0];

        Action<Camera> onPreCull;

        static void Grow<T>(ref T[] a, int need)
        {
            if (a.Length >= need) return;
            int n = Math.Max(need, Math.Max(16, a.Length * 2));
            var b = new T[n];
            Array.Copy(a, b, a.Length);
            a = b;
        }

        // ------------------------------------------------------------------ Begin

        public static SceneRecorder Begin(RecordSpec spec, string dir, RecordHeader header, SpecReport report)
        {
            if (report == null) report = new SpecReport();
            if (spec == null) spec = new RecordSpec();
            if (header == null) header = new RecordHeader();
            if (active != null && !active.ended)
            {
                report.Warn("replay.record", "a recorder was still active; ended first");
                active.End(report);
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var r = TakePrebuilt(spec, dir, report);
            if (r == null)
            {
                r = Construct(spec, dir, report);
                if (r == null) return null;
            }
            r.header = header;
            try
            {
                var scan = r.scanned;
                r.scanned = null;
                r.FillHeader(scan);
                r.writer = new SrecWriter(r.file, header);
                r.writer.Objects(r.table);
                r.objects = r.table.Count;
                r.onPreCull = r.OnPreCull;
                SceneHooks.preCullMain += r.onPreCull;
                SceneHooks.Ensure();
                registerQueue.Clear();
                active = r;
                r.HookStreaming();
                double beginMs = MsSince(t0);
                string msg = "record set: " + r.table.Count + " objects (" + r.nx + " transforms, " + r.nr + " renderers, " + r.nl + " lights, " + r.np + " particle systems, " +
                             r.nm + " MaterialInstances, " + r.npb + " probes, " + r.nrope + " ropes), " + r.camXfCount + " camera-driven transforms, " +
                             r.excludedMaterials + " excluded MaterialInstances, " + r.quietEligible + " quiet-eligible transforms, zone '" + r.zone + "', built in " + r.buildMs.ToString("0", IC) + " ms" +
                             (r.prebuildMs >= 0 ? " before sample 0 (prebuilt), Begin " + beginMs.ToString("0.#", IC) + " ms" : " in the frame of sample 0") +
                             (r.cullersWatched > 0 ? ", " + r.cullersWatched + " subscene(s) join when they load or activate" : "");
                report.Note("replay.record", msg);
                DevLog.Write("srec: begin " + r.file + ": " + msg);
                return r;
            }
            catch (Exception e)
            {
                report.Error("replay.record", "SceneRecorder.Begin: " + e.Message);
                DevLog.Error("srec begin", e);
                try { if (r.onPreCull != null) { SceneHooks.preCullMain -= r.onPreCull; SceneHooks.Release(); } } catch { }
                try { r.UnhookStreaming(); } catch { }
                try { if (r.writer != null) r.writer.Close(); } catch { }
                if (active == r) active = null;
                return null;
            }
        }

        // Builds the record set ahead of Begin (CORE_A calls it a few frames before sample 0, so the scan and object table
        // cost no game time inside the recording). Begin takes it when spec and folder match, the stream version is unchanged
        // and at most 10 frames passed; else Begin builds again. Objects spawned in between enter through the spawn watch.
        internal static bool Prebuild(RecordSpec spec, string dir, SpecReport report)
        {
            if (report == null) report = new SpecReport();
            if (spec == null) spec = new RecordSpec();
            pending = null;
            var r = Construct(spec, dir, report);
            if (r == null) return false;
            r.prebuildMs = r.buildMs;
            pending = r;
            DevLog.Write("srec: record set prebuilt in " + r.buildMs.ToString("0", IC) + " ms (" + r.table.Count + " objects)");
            return true;
        }

        static SceneRecorder TakePrebuilt(RecordSpec spec, string dir, SpecReport report)
        {
            var p = pending;
            pending = null;
            if (p == null) return null;
            string why = null;
            if (!ReferenceEquals(p.spec, spec)) why = "another record spec";
            else if (p.dir != (dir ?? "")) why = "another folder";
            else if (p.builtStream != Levels.StreamVersion) why = "streaming changed (" + p.builtStream + " -> " + Levels.StreamVersion + ")";
            else if (Time.frameCount - p.builtFrame > 10) why = (Time.frameCount - p.builtFrame) + " frames old";
            else if (p.prep != null && p.prep != RecordPrep.current) why = "the record prep changed";
            if (why == null) return p;
            report.Note("replay.record", "prebuilt record set discarded (" + why + "): built again in the frame of sample 0");
            p.prebuildMs = -1;
            return null;
        }

        static double MsSince(long t0) { return (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }

        static SceneRecorder Construct(RecordSpec spec, string dir, SpecReport report)
        {
            var r = new SceneRecorder { spec = spec, dir = dir ?? "", prep = RecordPrep.current };
            try
            {
                if (string.IsNullOrEmpty(r.dir)) { report.Error("replay.record", "no record folder"); return null; }
                Directory.CreateDirectory(r.dir);
                r.file = Path.Combine(r.dir, ReplayConst.RecordFile);
                int size = Marshal.SizeOf(typeof(ParticleSystem.Particle));
                if (size != SrecConst.ParticleBytes)
                {
                    r.particlesOk = false;
                    r.Warn("ParticleSystem.Particle is " + size + " bytes, not " + SrecConst.ParticleBytes + ": particles are not recorded");
                }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int errorsBefore = report.errors.Count;
                var scan = BuildScan(spec, r.dir, report);
                if (report.errors.Count > errorsBefore) { DevLog.Write("srec: record set refused: " + report.Summary(5)); return null; }
                if (r.prep != null && !r.prep.Released)
                {
                    int a0 = r.prep.extendedAnims, c0 = r.prep.extendedCullers;
                    ApplyPrep(r.prep, scan, report, true);
                    int da = r.prep.extendedAnims - a0, dc = r.prep.extendedCullers - c0;
                    if (da + dc > 0) r.Warn("record prep extended at Begin (objects activated after Prepare, e.g. by the keep-loaded prepass): " + da + " Animation/Animator(s) set to AlwaysAnimate, " + dc + " culler(s) forced");
                }
                r.zone = scan.zone;
                r.scanNotes.AddRange(scan.notes);
                r.subsceneNodes.AddRange(scan.subsceneNodes);
                foreach (var id in scan.skip) r.skipIds.Add(id);
                r.excludePaths = spec.exclude != null && spec.exclude.Count > 0 ? new List<string>(spec.exclude) : null;
                r.camPaths = scan.camPathsAll;
                r.PlanJoins(scan);
                r.Build(scan, 0, report);
                r.ShaderFindGlobal();
                r.ShaderResolve();
                r.ShaderBlocks();
                foreach (var c in scan.containers)
                {
                    int xi;
                    if (!r.xfByInstance.TryGetValue(c.GetInstanceID(), out xi)) continue;
                    var ct = new Container { t = c, xf = xi };
                    r.SnapshotChildren(ct);
                    r.containers.Add(ct);
                    if (c.parent == null && c.name == "_Dynamics") r.dynamicsFound = true;
                }
                r.scanned = scan;
                r.builtStream = Levels.StreamVersion;
                r.builtFrame = Time.frameCount;
                r.buildMs = MsSince(t0);
                return r;
            }
            catch (Exception e)
            {
                report.Error("replay.record", "SceneRecorder record set: " + e.Message);
                DevLog.Error("srec record set", e);
                return null;
            }
        }

        void FillHeader(Scan s)
        {
            var h = header;
            if (string.IsNullOrEmpty(h.created)) h.created = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", IC);
            if (string.IsNullOrEmpty(h.recorderVersion)) h.recorderVersion = Version;
            h.version = SrecConst.ContentVersion;
            int sub, sp;
            if ((h.subscene < 0 || h.savepoint < 0) && G.CurrentSavepoint(out sub, out sp)) { h.subscene = sub; h.savepoint = sp; }
            if (string.IsNullOrEmpty(h.level) && h.subscene >= 0) { try { h.level = SavepointManager.GetSubsceneName(h.subscene); } catch { } }
            if (h.streamVersion == 0) h.streamVersion = Levels.StreamVersion;
            if (h.screenW == 0) h.screenW = Screen.width;
            if (h.screenH == 0) h.screenH = Screen.height;
            if (h.fixedDeltaTime == 0) h.fixedDeltaTime = Time.fixedDeltaTime;
            if (h.t0 == 0 && h.t0Real == 0) { h.t0 = Time.timeDbl; h.t0Real = Time.realtimeSinceStartupDbl; }
            if (h.stepAtT0 == 0) { try { h.stepAtT0 = LogicManager.stepCount; } catch { } }
            if (h.gameFov == 0) { try { var c = RenderHost.WorldCam(); if (c != null) h.gameFov = c.fieldOfView; } catch { } }
            if (h.vSyncCount == 0) h.vSyncCount = QualitySettings.vSyncCount;
            if (h.targetFrameRate == 0) h.targetFrameRate = Application.targetFrameRate;
            if (h.refreshRate == 0) { try { h.refreshRate = Screen.currentResolution.refreshRate; } catch { } }
            if (h.captureFramerate == 0) h.captureFramerate = Time.captureFramerate;
            if (h.subscenes.Count == 0) h.subscenes.AddRange(s.subscenes);
            if (h.forcedCullers.Count == 0 && prep != null) h.forcedCullers.AddRange(prep.forcedNames);
            h.fogFields.Clear();
            if (fogOn) h.fogFields.AddRange(FogNames);
            for (int i = 0; i < FogNames.Length; i++) fogType[i] = FogIsColor[i] ? SrecConst.ValVec4 : SrecConst.ValFloat;
            h.postFields.Clear();
            h.cameraDriven.Clear();
            h.cameraDriven.AddRange(cameraDriven);
            h.posEps = spec.posEps;
            h.rotEps = spec.rotEps;
            h.cut = spec.cut;
            lastStep = h.stepAtT0;
            lastReal = 0;
        }

        void Warn(string w)
        {
            if (warnings.Count < 200) warnings.Add(w);
            DevLog.Write("srec: " + w);
            if (!ended && writer != null && evKind.Count < 10000) { evKind.Add("warning"); evName.Add("recorder"); evDetail.Add(w); }
        }

        bool ParticleWanted(Node n)
        {
            if (!particlesOk) return false;
            if (spec.particles == "none") return false;
            if (spec.particles != "list") return true;
            foreach (var q in spec.particleSystems)
            {
                if (string.IsNullOrEmpty(q)) continue;
                if (n.path == q || n.path.EndsWith("/" + q, StringComparison.Ordinal) || n.t.name == q) return true;
            }
            return false;
        }

        int NewId(SrecObject o, UnityEngine.Object live)
        {
            o.id = table.Count;
            table.Add(o);
            refs.Add(live);
            int n = table.Count;
            Grow(ref flagNow, n); Grow(ref flagLast, n); Grow(ref flagHas, n); Grow(ref flagWritten, n);
            Grow(ref keyFirst, n); Grow(ref keyLast, n); Grow(ref keyCount, n);
            keyFirst[o.id] = -1; keyLast[o.id] = -1; keyCount[o.id] = 0;
            flagHas[o.id] = false; flagWritten[o.id] = false;
            return o.id;
        }

        // Why a transform is read every sample (record.json transformReads, the R1 sampling profile): 0 quiet-eligible, else the
        // first mover rule that holds for it or a recorded ancestor.
        static readonly string[] ReadReasons = { "quietEligible", "rigidbody", "animation", "actorBone", "noNotifyWriter", "spawned", "cameraDriven", "world", "belowMover" };

        static byte MoverReason(Node n, byte parentReason)
        {
            if (n.rb != null) return 1;
            if ((n.mask & (SrecConst.CompAnimation | SrecConst.CompAnimator)) != 0) return 2;
            if ((n.mask & SrecConst.CompActor) != 0 || n.kind != SrecConst.KindTransform) return 3;
            if (n.types != null) foreach (var tn in n.types) if (XfMath.MoverType(tn)) return 4;
            if (n.spawned) return 5;
            if (n.camXf) return 6;
            if (n.forceWorld) return 7;
            return parentReason != 0 ? (byte)8 : (byte)0;
        }

        static bool Mover(Node n, bool parentMover)
        {
            const uint moving = SrecConst.CompRigidbody | SrecConst.CompAnimation | SrecConst.CompAnimator | SrecConst.CompActor;
            if (parentMover || n.rb != null || (n.mask & moving) != 0 || n.spawned || n.forceWorld || n.camXf || n.kind != SrecConst.KindTransform) return true;
            if (n.types != null) foreach (var tn in n.types) if (XfMath.MoverType(tn)) return true;
            return false;
        }

        void Build(Scan s, int from, SpecReport report)
        {
            var nodeXf = new int[s.nodes.Count];
            var mover = new bool[s.nodes.Count];
            var reason = new byte[s.nodes.Count];
            fogOn = spec.fog;
            shaderOn = spec.shaderState;
            for (int i = from; i < s.nodes.Count; i++)
            {
                var n = s.nodes[i];
                mover[i] = Mover(n, n.parent >= 0 && n.parent < i && mover[n.parent]);
                reason[i] = mover[i] ? MoverReason(n, n.parent >= 0 && n.parent < i ? reason[n.parent] : (byte)0) : (byte)0;
                if (mover[i] && reason[i] == 0) reason[i] = 8;
                if (n.t == null) { nodeXf[i] = -1; continue; }
                bool local = !n.forceWorld && n.parent >= 0 && n.parent < i && nodeXf[n.parent] >= 0;
                Vector3 p; Quaternion q;
                if (local) n.t.GetLocalPositionRotation(out p, out q); else n.t.GetPositionRotation(out p, out q);
                var o = new SrecObject
                {
                    kind = n.kind, instanceId = n.t.GetInstanceID(), depth = n.depth, path = n.path, owner = n.owner ?? "",
                    comps = n.mask, pos = p, rot = q, scale = n.t.localScale, activeSelf = n.go.activeSelf, extent = n.extent
                };
                o.ordinals.AddRange(n.ord);
                o.siblings.AddRange(n.sib);
                o.types.AddRange(n.types);
                byte fl = 0;
                if (local) fl |= SrecConst.ObjLocal;
                if (n.camXf) fl |= SrecConst.ObjCamXf;
                if (n.camDriven) fl |= SrecConst.ObjCamDriven;
                if (n.spawned) fl |= SrecConst.ObjSpawned;
                if (n.go.activeInHierarchy) fl |= SrecConst.ObjSeenActive;
                o.flags = fl;
                o.parentId = local ? xs[nodeXf[n.parent]].id : (n.parent >= 0 && nodeXf[n.parent] >= 0 ? xs[nodeXf[n.parent]].id : -1);
                int id = NewId(o, n.t);
                Grow(ref xs, nx + 1);
                int xi = nx++;
                nodeXf[i] = xi;
                var x = new XfItem
                {
                    t = n.t, go = n.go, rb = n.rb, id = id, parentIdx = n.parent >= 0 ? nodeXf[n.parent] : -1, parentId = o.parentId,
                    parentInst = n.t.parent != null ? n.t.parent.GetInstanceID() : 0, local = local, camXf = n.camXf, camDriven = n.camDriven,
                    rotThr = XfMath.RotThreshold(spec.posEps, n.extent, spec.rotEps), activeSelf = n.go.activeSelf, activeH = n.go.activeInHierarchy, seenAt = -1,
                    spawnedXf = n.spawned, kids = n.t.childCount
                };
                x.s.Reset();
                x.q.ok = quietTransforms && !mover[i];
                x.reason = reason[i];
                xfByReason[x.reason]++;
                if (x.q.ok) quietEligible++;
                xs[xi] = x;
                xfByInstance[o.instanceId] = xi;
                if (n.spawned) spawnedXfs.Add(xi);
                flagHas[id] = true;
                if (n.camXf) { camXfCount++; if (cameraDriven.Count < 2000) cameraDriven.Add(n.path); }
                if (n.camDriven) camDrivenCount++;
                if (n.comps == null) continue;
                var seen = new Dictionary<Type, int>();
                foreach (var c in n.comps)
                {
                    if (c == null || c is Transform) continue;
                    var ty = c.GetType();
                    int k;
                    seen.TryGetValue(ty, out k);
                    seen[ty] = k + 1;
                    string cpath = n.path + "::" + ty.Name + (k > 0 ? "[" + k.ToString(IC) + "]" : "");
                    if (c is ParticleSystem && ParticleWanted(n)) AddParticles(n, (ParticleSystem)c, cpath, id, xi, fl);
                    else if (c is Light && spec.lights) AddLight(n, (Light)c, cpath, id, xi, fl);
                    else if (c is Renderer) AddRenderer(n, (Renderer)c, cpath, id, xi, fl);
                    else if (c is MaterialInstance && spec.materials) AddMaterial(s, n, (MaterialInstance)c, cpath, id, xi, fl);
                    else if (c is ColorBlendProbe && spec.probes) AddProbe(n, (ColorBlendProbe)c, cpath, id, xi, fl);
                    else if (c is RopeLineRenderer) AddRope(n, (RopeLineRenderer)c, cpath, id, xi, fl, report);
                    ShaderComp(c);
                }
            }
        }

        SrecObject CompObj(Node n, Component c, string cpath, int parentId, byte kind, byte fl)
        {
            var o = new SrecObject
            {
                kind = kind, instanceId = c.GetInstanceID(), parentId = parentId, depth = n.depth, path = cpath, owner = "",
                comps = n.mask, pos = n.t.position, rot = n.t.rotation, scale = n.t.localScale, activeSelf = n.go.activeSelf, extent = n.extent,
                flags = (byte)(fl & (SrecConst.ObjSpawned | SrecConst.ObjCamXf | SrecConst.ObjCamDriven | SrecConst.ObjSeenActive))
            };
            o.types.Add(c.GetType().Name);
            return o;
        }

        void AddParticles(Node n, ParticleSystem p, string cpath, int pid, int xi, byte fl)
        {
            var o = CompObj(n, p, cpath, pid, SrecConst.KindParticles, fl);
            int max = 0;
            try { max = p.maxParticles; o.maxParticles = max; o.simulationSpace = (byte)p.simulationSpace; o.randomSeed = p.randomSeed; } catch { }
            int id = NewId(o, p);
            Grow(ref ps, np + 1);
            ps[np++] = new PsItem { ps = p, id = id, owner = xi, buf = new ParticleSystem.Particle[Math.Max(1, Math.Min(max, 20000))] };
        }

        void AddLight(Node n, Light l, string cpath, int pid, int xi, byte fl)
        {
            var o = CompObj(n, l, cpath, pid, SrecConst.KindLight, fl);
            try { o.lightType = (byte)l.type; o.lightShadows = (byte)l.shadows; o.activeSelf = l.enabled; } catch { }
            int id = NewId(o, l);
            flagHas[id] = true;
            Grow(ref ls, nl + 1);
            ls[nl++] = new LightItem { l = l, id = id, owner = xi };
        }

        void AddRenderer(Node n, Renderer r, string cpath, int pid, int xi, byte fl)
        {
            var o = CompObj(n, r, cpath, pid, SrecConst.KindRenderer, fl);
            int slots = 1;
            try { var m = r.sharedMaterials; slots = m != null ? m.Length : 0; o.activeSelf = r.enabled; } catch { }
            o.materialSlots = slots;
            int id = NewId(o, r);
            flagHas[id] = true;
            Grow(ref rs, nr + 1);
            rs[nr++] = new RendItem { r = r, id = id, owner = xi, slots = slots, mats = slots > 1 ? new Material[slots] : null, smr = r as SkinnedMeshRenderer };
        }

        void AddMaterial(Scan s, Node n, MaterialInstance mi, string cpath, int pid, int xi, byte fl)
        {
            var o = CompObj(n, mi, cpath, pid, SrecConst.KindMaterial, fl);
            bool excl = n.excludedMi || (s != null && s.excludedMi.Contains(mi.GetInstanceID()));
            if (excl) { o.flags |= SrecConst.ObjExcludedMaterial; excludedMaterials++; }
            var item = new MiItem { mi = mi, owner = xi, excluded = excl };
            try
            {
                foreach (var p in mi.FloatProperties) o.floatProps.Add(p != null ? p.propertyName ?? "" : "");
                foreach (var p in mi.ColorProperties) o.colorProps.Add(p != null ? p.propertyName ?? "" : "");
                foreach (var p in mi.VectorProperties) o.vectorProps.Add(p != null ? p.propertyName ?? "" : "");
                foreach (var p in mi.TextureProperties) o.textureProps.Add(p != null ? p.propertyName ?? "" : "");
            }
            catch { }
            item.names[0] = o.floatProps.Count; item.names[1] = o.colorProps.Count; item.names[2] = o.vectorProps.Count; item.names[3] = o.textureProps.Count;
            item.f = new float[Math.Max(4, o.floatProps.Count)];
            item.c = new Color[Math.Max(4, o.colorProps.Count)];
            item.v = new Vector4[Math.Max(4, o.vectorProps.Count)];
            item.tx = new Texture[Math.Max(4, o.textureProps.Count)];
            item.id = NewId(o, mi);
            Grow(ref ms, nm + 1);
            ms[nm++] = item;
        }

        void AddProbe(Node n, ColorBlendProbe p, string cpath, int pid, int xi, byte fl)
        {
            var o = CompObj(n, p, cpath, pid, SrecConst.KindProbe, fl);
            int id = NewId(o, p);
            Grow(ref pbs, npb + 1);
            pbs[npb++] = new ProbeItem { p = p, id = id, owner = xi };
        }

        void AddRope(Node n, RopeLineRenderer r, string cpath, int pid, int xi, byte fl, SpecReport report)
        {
            var o = CompObj(n, r, cpath, pid, SrecConst.KindRope, fl);
            var item = new RopeItem { r = r, owner = xi };
            int cnt = ReadRope(item);
            if (cnt < 0) { item.cur = new Vector3[-cnt + 8]; item.last = new Vector3[-cnt + 8]; cnt = ReadRope(item); }
            o.ropeVertices = Math.Max(0, cnt);
            try { o.ropeDrawMode = (byte)(r.meshOutput != null ? 4 : r.lineDivisions > 2 ? 3 : 1); o.ropeLineDivisions = r.lineDivisions; } catch { }
            item.id = NewId(o, r);
            Grow(ref ropes, nrope + 1);
            ropes[nrope++] = item;
        }

        void SnapshotChildren(Container c)
        {
            int n = c.t.childCount;
            Grow(ref c.known, n);
            for (int i = 0; i < n; i++) c.known[i] = c.t.GetChild(i).GetInstanceID();
            c.count = n;
        }

        // ------------------------------------------------------------------ rope vertices (zero-alloc reader, reflection fallback)

        static VertexReader ropeReader;
        static bool ropeReaderTried, ropeReaderChecked;
        static FieldInfo fBlend, fVerts, fPos;

        int ReadRope(RopeItem item)
        {
            if (!ropeReaderTried)
            {
                ropeReaderTried = true;
                try
                {
                    const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    fBlend = typeof(RopeLineRenderer).GetField("blendRope", all);
                    fVerts = fBlend != null ? fBlend.FieldType.GetField("vertices", all) : null;
                    fPos = fVerts != null ? fVerts.FieldType.GetElementType().GetField("position", all) : null;
                    if (fPos != null) ropeReader = XfMath.BuildVertexReader(typeof(RopeLineRenderer), fBlend, fVerts, fPos);
                }
                catch (Exception e) { ropeReader = null; DevLog.Write("srec: rope reader not emitted, reflection fallback: " + e.Message); }
            }
            if (ropeReader != null)
            {
                try
                {
                    int n = ropeReader(item.r, item.cur);
                    if (ropeReaderChecked || n <= 0) return n;
                    ropeReaderChecked = true;
                    var check = new Vector3[n];
                    int m = ReflectRope(item.r, check);
                    bool same = m == n;
                    for (int i = 0; i < n && same; i++) same = check[i] == item.cur[i];
                    if (same) return n;
                    DevLog.Write("srec: emitted rope reader disagrees with reflection (" + n + " vs " + m + "), reflection fallback");
                }
                catch (Exception e) { DevLog.Write("srec: emitted rope reader failed (" + e.GetType().Name + ": " + e.Message + "), reflection fallback"); }
                ropeReader = null;
            }
            if (fPos == null) return 0;
            ropeFallback++;
            return ReflectRope(item.r, item.cur);
        }

        static int ReflectRope(RopeLineRenderer r, Vector3[] dst)
        {
            try
            {
                var blend = fBlend.GetValue(r);
                var verts = blend != null ? fVerts.GetValue(blend) as Array : null;
                if (verts == null) return 0;
                if (verts.Length > dst.Length) return -verts.Length;
                for (int i = 0; i < verts.Length; i++) dst[i] = (Vector3)fPos.GetValue(verts.GetValue(i));
                return verts.Length;
            }
            catch { return 0; }
        }

        // ------------------------------------------------------------------ public per-frame API

        public UnityEngine.Object Ref(int id)
        {
            if (id < 0 || id >= refs.Count) return null;
            var o = refs[id];
            return o != null ? o : null;
        }

        public void SetVirtual(MultiCamContext ctx)
        {
            if (ended || ctx == null) return;
            CopyGame(ctx.game, virt.game);
            virtFrame = Time.frameCount;
        }

        public void Event(string kind, string name, string detail)
        {
            if (ended) return;
            kind = kind ?? ""; name = name ?? ""; detail = detail ?? "";
            if (kind == "marker") pendingFlags |= SrecConst.SmplMarker;
            else if (kind == "hold") pendingFlags |= SrecConst.SmplHold;
            else if (kind == "game") pendingFlags |= SrecConst.SmplGameFrame;
            if (evKind.Count > 10000) return;
            evKind.Add(kind); evName.Add(name); evDetail.Add(detail);
        }

        internal void Flag(ushort smplFlags) { pendingFlags |= smplFlags; }

        public string Status()
        {
            if (failed) return "record failed: " + lastError;
            var sb = new StringBuilder(ended ? "recorded " : "recording ");
            double t = metaN > 0 ? mGame[metaN - 1] : 0;
            sb.Append(t.ToString("0.00", IC)).Append(" s, ").Append(samples).Append(" samples, ").Append(objects).Append(" objects, ")
              .Append((bytes / 1048576.0).ToString("0.0", IC)).Append(" MB");
            if (writer != null && writer.stalls > 0) sb.Append(", ").Append(writer.stalls).Append(" writer stalls");
            if (eventsLogged > 0) sb.Append(", ").Append(eventsLogged).Append(" events");
            if (joinsDone > 0) sb.Append(", ").Append(joinsDone).Append(" subscene joins");
            if (shChans.Count > 0) sb.Append(", ").Append(shChans.Count).Append(" shader channels");
            if (errors > 0) sb.Append(", ").Append(errors).Append(" sample errors");
            if (writer != null && writer.error != null) sb.Append(", writer: ").Append(writer.error);
            return sb.ToString();
        }

        static void CopyGame(GameCamState a, GameCamState b)
        {
            if (a == null || b == null) return;
            b.ok = a.ok; b.step = a.step; b.rendered = a.rendered; b.clean = a.clean;
            b.positionCamera = a.positionCamera; b.positionAim = a.positionAim; b.positionUp = a.positionUp; b.velocity = a.velocity;
            b.aimDistance = a.aimDistance; b.box = a.box; b.probe = a.probe; b.extrapolation = a.extrapolation;
            b.handOffset = a.handOffset; b.handRot = a.handRot; b.shakeOffset = a.shakeOffset; b.shakeRot = a.shakeRot; b.cut = a.cut;
        }

        string TypeName(object o)
        {
            if (o == null) return "";
            var t = o.GetType();
            string n;
            if (!typeNames.TryGetValue(t, out n)) { n = t.Name; typeNames[t] = n; }
            return n;
        }

        int TexId(Texture t)
        {
            if (t == null) return -1;
            int k = t.GetInstanceID(), id;
            if (texIds.TryGetValue(k, out id)) return id;
            id = writer.StringId(t.name);
            texIds[k] = id;
            return id;
        }

        int MatId(Material m)
        {
            if (m == null) return -1;
            int k = m.GetInstanceID(), id;
            if (matIds.TryGetValue(k, out id)) return id;
            id = writer.StringId(m.name);
            matIds[k] = id;
            return id;
        }

        void Key(int id, int k)
        {
            if (keyFirst[id] < 0) keyFirst[id] = k;
            keyLast[id] = k;
            keyCount[id]++;
        }

        // ------------------------------------------------------------------ sampling (preCullMain, 2.3.2)

        void OnPreCull(Camera cam)
        {
            if (ended || failed || writer == null) return;
            int frame = Time.frameCount;
            if (frame == lastFrame) return;
            lastFrame = frame;
            double game = Time.timeDbl - header.t0;
            if (samples > 0 && game <= lastGameTime) { skippedFrames++; pendingFlags |= SrecConst.SmplHold; return; }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int k = samples;
            bool full = k == 0;
            double dt = k == 0 ? 0 : game - lastGameTime;
            double realAbs = Time.realtimeSinceStartupDbl;
            double real = realAbs - header.t0Real;
            long step = 0;
            try { step = LogicManager.stepCount; } catch { }
            ushort fl = pendingFlags;
            pendingFlags = 0;
            if (Time.captureFramerate == 0 && k > 0 && Time.unscaledDeltaTime > Time.maximumDeltaTime + 1e-4f) { fl |= SrecConst.SmplHitch; hitches++; }
            var stamp = new SrecStamp
            {
                sample = k, gameTime = game, realTime = realAbs, logicStep = step, frameCount = frame,
                fixedSteps = (ushort)Math.Min(65535L, Math.Max(0L, step - lastStep)), flags = fl
            };
            bool begun = false;
            int xfThis = 0, partsThis = 0;
            long bytesBefore = writer.bytes;
            for (int c = 0; c < chanNow.Length; c++) chanNow[c] = 0f;
            chanT = t0;
            try
            {
                writer.BeginSample(stamp);
                begun = true;
                Chan(11);
                ndel = 0;
                WatchStreaming(k);
                TakeRegistered(k);
                WatchSpawns(k);
                WatchChildren(k);
                for (int i = 0; i < adds.Count; i++) writer.ObjAdd(adds[i]);
                adds.Clear();
                Chan(0);
                SampleXf(k, full, dt, ref xfThis);
                Chan(1);
                SampleRenderers(k, full);
                Chan(2);
                SampleLights(k, full);
                Chan(3);
                WriteFlags(k, full);
                Chan(4);
                if (particlesOk) partsThis = SampleParticles(k, full);
                Chan(5);
                SampleMaterials(k, full);
                Chan(6);
                SampleProbes(k, full);
                Chan(7);
                SampleRopes(k, full);
                Chan(8);
                if (fogOn) SampleFog(full);
                Chan(9);
                SampleShader(k, full);
                Chan(12);
                SampleVirtual(cam);
                Chan(10);
                if (ndel > 0)
                {
                    Array.Sort(dels, 0, ndel);
                    for (int i = 0; i < ndel; i++) writer.ObjDel(dels[i]);
                    spawnedDeleted += ndel;
                }
                for (int i = 0; i < evKind.Count; i++)
                {
                    writer.Event(evKind[i], evName[i], evDetail[i]);
                    if (evKind[i] == "event") LogEvent(evName[i], evDetail[i], k, game);
                }
                evKind.Clear(); evName.Clear(); evDetail.Clear();
                consecutiveErrors = 0;
            }
            catch (Exception e)
            {
                errors++;
                consecutiveErrors++;
                lastError = e.GetType().Name + ": " + e.Message;
                if (sampleErrorsLogged++ < 10) DevLog.Error("srec sample " + k, e);
            }
            finally
            {
                if (begun) { try { writer.EndSample(); } catch (Exception e) { errors++; lastError = "EndSample: " + e.Message; } }
                Chan(11);
            }
            if (consecutiveErrors >= 30)
            {
                failed = true;
                Warn("30 consecutive sample errors, recording stopped: " + lastError);
                return;
            }
            samples = k + 1;
            bytes = writer.bytes;
            lastGameTime = game;
            lastReal = real;
            lastStep = step;
            double drift = Math.Abs(real - game) * 1000.0;
            if (drift > maxDriftMs) maxDriftMs = drift;
            endDriftMs = drift;
            double cost = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Meta(k, frame, game, dt, real, step, stamp.fixedSteps, Time.timeScale, fl, xfThis, partsThis, writer.bytes - bytesBefore, (float)cost);
            if (k == 30) { gcAt30 = GC.CollectionCount(0); heapAt30 = GC.GetTotalMemory(false); }
        }

        void Meta(int k, int frame, double game, double dt, double real, long step, ushort fixedSteps, float scale, ushort fl, int xf, int parts, long b, float cost)
        {
            if (metaN >= mFrame.Length)
            {
                int n = Math.Max(4096, mFrame.Length * 2);
                Grow(ref mFrame, n); Grow(ref mXf, n); Grow(ref mParts, n); Grow(ref mGame, n); Grow(ref mDt, n); Grow(ref mReal, n);
                Grow(ref mStep, n); Grow(ref mBytes, n); Grow(ref mFixed, n); Grow(ref mFlags, n); Grow(ref mScale, n); Grow(ref mCost, n);
                Grow(ref mChan, n * ChannelNames.Length);
            }
            int i = metaN++;
            Array.Copy(chanNow, 0, mChan, i * ChannelNames.Length, ChannelNames.Length);
            mFrame[i] = frame; mGame[i] = game; mDt[i] = dt; mReal[i] = real; mStep[i] = step; mFixed[i] = fixedSteps;
            mScale[i] = scale; mFlags[i] = fl; mXf[i] = xf; mParts[i] = parts; mBytes[i] = b; mCost[i] = cost;
        }

        void Chan(int c)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            chanNow[c] += (float)((now - chanT) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            chanT = now;
        }

        void Dead(int id)
        {
            Grow(ref dels, ndel + 1);
            dels[ndel++] = id;
        }

        void SampleXf(int k, bool full, double dt, ref int count)
        {
            float posEps = spec.posEps, cut = spec.cut;
            for (int i = 0; i < nx; i++)
            {
                ref XfItem x = ref xs[i];
                if (x.dead) continue;
                if (x.t == null)
                {
                    x.dead = true; x.activeH = false; Dead(x.id);
                    if (x.spawnedXf && (x.parentIdx < 0 || !xs[x.parentIdx].dead)) DestroyedSpawn(x.id, k);
                    continue;
                }
                bool first = x.seenAt < 0;
                bool parentActive;
                if (x.parentIdx >= 0 && x.parentIdx < i) parentActive = !xs[x.parentIdx].dead && xs[x.parentIdx].activeH;
                else
                {
                    var p = x.t.parent;
                    parentActive = p == null || p.gameObject.activeInHierarchy;
                }
                bool wasActiveH = x.activeH;
                bool kinPrev = x.kinematic, kinKnownPrev = x.kinematicKnown;
                bool read = full || first || parentActive;
                if (read) x.activeSelf = x.go.activeSelf;
                x.activeH = parentActive && x.activeSelf;
                if (x.activeH) x.everActive = true;
                if (read)
                {
                    byte bits = (byte)(x.camDriven ? SrecConst.FlActive : x.activeSelf ? SrecConst.FlActive : 0);
                    if (x.rb != null && (x.activeH || full || first))
                    {
                        x.kinematic = x.rb.isKinematic;
                        x.kinematicKnown = true;
                    }
                    if (x.kinematicKnown && x.kinematic) bits |= SrecConst.FlKinematic;
                    flagNow[x.id] = bits;
                    if (x.camDriven && (!x.camActiveSet || x.camActive != x.activeSelf))
                    {
                        x.camActive = x.activeSelf;
                        x.camActiveSet = true;
                        writer.CamActive(x.id, x.activeSelf);
                        Key(x.id, k);
                    }
                }
                if (x.camXf) { x.seenAt = k; continue; }
                if (!(full || first || x.activeH)) continue;
                bool gated = false, flagged = false;
                if (x.q.dormant && !full && wasActiveH)
                {
                    flagged = x.t.hasChanged;
                    if (!flagged && ((k + i) % XfMath.SweepEvery) != 0)
                    {
                        XfMath.Hold(ref x.s, k);
                        x.q.idle = 0;
                        x.seenAt = k;
                        quietSkips++;
                        continue;
                    }
                    if (flagged) x.t.hasChanged = false;
                    gated = true;
                    quietReads++;
                }
                var tp = x.t.parent;
                int pinst = tp != null ? tp.GetInstanceID() : 0;
                bool parentChanged = !first && pinst != x.parentInst;
                if (parentChanged)
                {
                    x.parentInst = pinst;
                    int pxi;
                    x.parentId = pinst != 0 && xfByInstance.TryGetValue(pinst, out pxi) ? xs[pxi].id : -1;
                    x.parentIdx = pinst != 0 && xfByInstance.TryGetValue(pinst, out pxi) ? pxi : -1;
                    parentChanges++;
                }
                bool becameActive = !first && !full && x.activeH && !wasActiveH;
                bool kinOn = x.rb != null && !first && kinKnownPrev && !kinPrev && x.kinematic;
                if (first) x.firstAt = k;
                Vector3 pos; Quaternion rot;
                if (x.local) x.t.GetLocalPositionRotation(out pos, out rot); else x.t.GetPositionRotation(out pos, out rot);
                readsByReason[x.reason]++;
                bool scaleRead = ReplayConst.ScaleDue(full || first || x.scaleVariant || becameActive || parentChanged || kinOn, x.scaleWatch, k, i, k - x.firstAt, XfMath.ScaleWarmup);
                Vector3 scl = scaleRead ? x.t.localScale : x.s.prevScale;
                if (scaleRead && !first && !x.scaleVariant && XfMath.ScaleChanged(scl, x.s.keyScale)) x.scaleVariant = true;
                byte pre;
                byte kb = XfMath.Decide(ref x.s, k, pos, rot, scl, scaleRead, dt, becameActive || kinOn, parentChanged, posEps, x.rotThr, cut, out pre);
                if (pre != 0)
                {
                    writer.Xf(x.id, (byte)(pre | SrecConst.XfPreKey), x.s.prevPos, x.s.prevRot, x.s.prevScale, x.parentId);
                    preKeys++;
                    count++;
                    Key(x.id, k);
                }
                if (kb != 0)
                {
                    writer.Xf(x.id, kb, pos, rot, scl, x.parentId);
                    xfKeys++;
                    count++;
                    if ((kb & SrecConst.XfCut) != 0) cuts++;
                    Key(x.id, k);
                }
                bool useRb = false;
                float rbSpeed = 0f;
                if (x.rb != null && !x.kinematic) { useRb = true; rbSpeed = x.rb.velocity.magnitude; }
                XfMath.Commit(ref x.s, k, pos, rot, scl, scaleRead, kb, dt, rbSpeed, useRb);
                x.seenAt = k;
                if (!x.q.ok && !x.q.dormant) continue;
                byte qs = XfMath.QuietStep(ref x.q, gated, flagged, kb != 0 || pre != 0, !x.scaleVariant && k - x.firstAt >= XfMath.ScaleWarmup);
                if (qs == XfMath.QuietEnter) { x.t.hasChanged = false; quietEntered++; }
                else if (qs == XfMath.QuietMiss) QuietMissed(x.id, k);
                else if (qs == XfMath.QuietCool) quietCooled++;
            }
        }

        void QuietMissed(int id, int k)
        {
            quietMisses++;
            if (quietMissPaths.Count < 50) quietMissPaths.Add(table[id].path);
            if (quietMisses <= 5) Warn("dormant transform changed without Transform.hasChanged (found by the sweep at sample " + k + ", up to " + XfMath.SweepEvery + " samples late; removed from the quiet set): " + table[id].path);
        }

        void SampleRenderers(int k, bool full)
        {
            for (int i = 0; i < nr; i++)
            {
                ref RendItem r = ref rs[i];
                if (r.dead) continue;
                ref XfItem o = ref xs[r.owner];
                if (o.dead || r.r == null) { r.dead = true; Dead(r.id); continue; }
                if (!(full || o.activeH || !r.matSet)) continue;
                r.enabled = r.r.enabled;
                flagNow[r.id] = r.enabled ? SrecConst.FlRenderer : (byte)0;
                if (r.smr != null) SampleMesh(ref r, k);
                if (r.slots <= 0) { r.matSet = true; continue; }
                var m0 = r.r.sharedMaterial;
                bool multiDue = r.slots > 1 && (full || !r.matSet || ((k + i) % 10) == 0);
                if (multiDue)
                {
                    Material[] cur = r.r.sharedMaterials;
                    int n = cur != null ? Math.Min(cur.Length, r.mats.Length) : 0;
                    for (int s = 0; s < n; s++)
                    {
                        if (r.matSet && cur[s] == r.mats[s]) continue;
                        r.mats[s] = cur[s];
                        writer.SharedMat(r.id, s, MatId(cur[s]));
                        Key(r.id, k);
                    }
                    r.mat0 = n > 0 ? cur[0] : m0;
                }
                else if (!r.matSet || m0 != r.mat0)
                {
                    r.mat0 = m0;
                    if (r.mats != null) r.mats[0] = m0;
                    writer.SharedMat(r.id, 0, MatId(m0));
                    Key(r.id, k);
                }
                r.matSet = true;
            }
        }

        // SkinnedMeshRenderer.sharedMesh swaps (a head pop to the headless mesh): a kind "mesh" event row (name = renderer id,
        // detail = new mesh key, tab, previous key; ReplayConst.MeshKey = name|vertexCount|instanceId, so two meshes of one name,
        // the CEO body and the game's headless mesh "mesh", stay apart) in the sample that sees the swap; both meshes are kept by
        // key (and by name for older lookups) at the swap; SceneTracks.MeshAt and the replayer
        // turn the rows into a step channel. Live meshes are kept by name for a same-session replay.
        void SampleMesh(ref RendItem r, int k)
        {
            var m = r.smr.sharedMesh;
            if (!r.meshSet) { r.meshSet = true; r.mesh = m; return; }
            if ((object)m == (object)r.mesh || (m != null && r.mesh != null && m.GetInstanceID() == r.mesh.GetInstanceID())) return;
            KeepMesh(r.mesh);
            KeepMesh(m);
            string now = MeshKeyOf(m), was = MeshKeyOf(r.mesh);
            r.mesh = m;
            meshSwaps++;
            if (evKind.Count < 10000) { evKind.Add("mesh"); evName.Add(r.id.ToString(IC)); evDetail.Add(now + "\t" + was); }
            Key(r.id, k);
        }

        void KeepMesh(Mesh m)
        {
            if (m == null) return;
            string n = m.name ?? "";
            if (!meshRefs.ContainsKey(n)) meshRefs[n] = m;
            meshRefs[MeshKeyOf(m)] = m;
        }

        static string MeshKeyOf(Mesh m)
        {
            if (m == null) return "";
            int vc = -1;
            try { vc = m.vertexCount; } catch { vc = -1; }
            return ReplayConst.MeshKey(m.name, vc, m.GetInstanceID());
        }

        internal Mesh MeshRef(string name)
        {
            Mesh m;
            return name != null && meshRefs.TryGetValue(name, out m) && m != null ? m : null;
        }

        void SampleLights(int k, bool full)
        {
            for (int i = 0; i < nl; i++)
            {
                ref LightItem it = ref ls[i];
                if (it.dead) continue;
                ref XfItem o = ref xs[it.owner];
                if (o.dead || it.l == null) { it.dead = true; Dead(it.id); continue; }
                if (!(full || o.activeH || !it.set)) continue;
                var l = it.l;
                var v = new SrecLight
                {
                    enabled = l.enabled, intensity = l.intensity, color = l.color, range = l.range, spotAngle = l.spotAngle,
                    useStaticShadows = l.useStaticShadows, staticShadowsDirty = l.staticShadowsDirty
                };
                var ck = l.cookie;
                flagNow[it.id] = v.enabled ? SrecConst.FlLight : (byte)0;
                byte mask = 0;
                if (!it.set || full)
                {
                    mask = 0xFF;
                    it.cookie = ck;
                    v.cookie = TexId(ck);
                }
                else
                {
                    var p = it.last;
                    if (v.enabled != p.enabled) mask |= SrecConst.LiEnabled;
                    if (v.intensity != p.intensity) mask |= SrecConst.LiIntensity;
                    if (v.color != p.color) mask |= SrecConst.LiColor;
                    if (v.range != p.range) mask |= SrecConst.LiRange;
                    if (v.spotAngle != p.spotAngle) mask |= SrecConst.LiSpotAngle;
                    if (ck != it.cookie) { mask |= SrecConst.LiCookie; it.cookie = ck; v.cookie = TexId(ck); } else v.cookie = p.cookie;
                    if (v.useStaticShadows != p.useStaticShadows) mask |= SrecConst.LiStaticShadows;
                    if (v.staticShadowsDirty != p.staticShadowsDirty) mask |= SrecConst.LiShadowsDirty;
                }
                it.last = v;
                it.set = true;
                if (mask == 0) continue;
                writer.Light(it.id, mask, v);
                Key(it.id, k);
            }
        }

        void WriteFlags(int k, bool full)
        {
            int n = table.Count;
            for (int id = 0; id < n; id++)
            {
                if (!flagHas[id]) continue;
                byte b = flagNow[id];
                if (flagWritten[id] && !full && b == flagLast[id]) continue;
                flagLast[id] = b;
                flagWritten[id] = true;
                writer.Flags(id, b);
                Key(id, k);
            }
        }

        int SampleParticles(int k, bool full)
        {
            int total = 0;
            for (int i = 0; i < np; i++)
            {
                ref PsItem it = ref ps[i];
                if (it.dead) continue;
                ref XfItem o = ref xs[it.owner];
                if (o.dead || it.ps == null) { it.dead = true; Dead(it.id); continue; }
                if (!(full || o.activeH)) continue;
                var p = it.ps;
                int count = p.particleCount;
                bool playing = p.isPlaying;
                if (playing) { it.playingSamples++; if (count == 0) it.playingZero++; }
                if (count > 0) it.everCount = true;
                bool paused = p.isPaused;
                float rate = p.emissionRate;
                if (!it.stateSet || playing != it.playing || paused != it.paused || rate != it.rate)
                {
                    if (it.stateSet && evKind.Count < 10000)
                    {
                        evKind.Add("particleState");
                        evName.Add(table[it.id].path);
                        evDetail.Add("playing=" + (playing ? "1" : "0") + " paused=" + (paused ? "1" : "0") + " rate=" + rate.ToString("R", IC));
                    }
                    it.stateSet = true; it.playing = playing; it.paused = paused; it.rate = rate;
                }
                if (count <= 0 && it.lastCount <= 0 && !full) continue;
                int n = 0;
                if (count > 0)
                {
                    if (count > it.buf.Length) it.buf = new ParticleSystem.Particle[Math.Min(count + 64, 400000)];
                    n = p.GetParticles(it.buf);
                }
                if (n > 0 || it.lastCount > 0 || full)
                {
                    writer.Particles(it.id, it.buf, n);
                    particleFrames++;
                    particlesTotal += n;
                    total += n;
                    Key(it.id, k);
                }
                it.lastCount = n;
            }
            return total;
        }

        void SampleMaterials(int k, bool full)
        {
            for (int i = 0; i < nm; i++)
            {
                var it = ms[i];
                if (it.dead || it.excluded) continue;
                ref XfItem o = ref xs[it.owner];
                if (o.dead || it.mi == null) { it.dead = true; Dead(it.id); continue; }
                if (!(full || o.activeH)) continue;
                var mi = it.mi;
                var fp = mi.FloatProperties;
                int lim = Names(it, SrecConst.MatFloat, fp.Count, k);
                Grow(ref it.f, lim);
                for (int j = 0; j < lim; j++)
                {
                    float v = fp[j].value;
                    if (!full && j < it.values[0] && (v == it.f[j] || (float.IsNaN(v) && float.IsNaN(it.f[j])))) continue;
                    it.f[j] = v;
                    writer.Material(it.id, SrecConst.MatFloat, j, new Vector4(v, 0f, 0f, 0f), -1);
                    Key(it.id, k);
                }
                it.values[0] = lim;
                var cp = mi.ColorProperties;
                lim = Names(it, SrecConst.MatColor, cp.Count, k);
                Grow(ref it.c, lim);
                for (int j = 0; j < lim; j++)
                {
                    Color v = cp[j].value;
                    if (!full && j < it.values[1] && v == it.c[j]) continue;
                    it.c[j] = v;
                    writer.Material(it.id, SrecConst.MatColor, j, new Vector4(v.r, v.g, v.b, v.a), -1);
                    Key(it.id, k);
                }
                it.values[1] = lim;
                var vp = mi.VectorProperties;
                lim = Names(it, SrecConst.MatVector, vp.Count, k);
                Grow(ref it.v, lim);
                for (int j = 0; j < lim; j++)
                {
                    Vector4 v = vp[j].value;
                    if (!full && j < it.values[2] && v == it.v[j]) continue;
                    it.v[j] = v;
                    writer.Material(it.id, SrecConst.MatVector, j, v, -1);
                    Key(it.id, k);
                }
                it.values[2] = lim;
                var tp = mi.TextureProperties;
                lim = Names(it, SrecConst.MatTexture, tp.Count, k);
                Grow(ref it.tx, lim);
                for (int j = 0; j < lim; j++)
                {
                    Texture v = tp[j].value;
                    if (!full && j < it.values[3] && v == it.tx[j]) continue;
                    it.tx[j] = v;
                    writer.Material(it.id, SrecConst.MatTexture, j, Vector4.zero, TexId(v));
                    Key(it.id, k);
                }
                it.values[3] = lim;
            }
        }

        int Names(MiItem it, byte kind, int count, int k)
        {
            int known = it.names[kind];
            if (count > known)
            {
                var mi = it.mi;
                for (int j = known; j < count; j++)
                {
                    string name;
                    if (kind == SrecConst.MatFloat) name = mi.FloatProperties[j].propertyName;
                    else if (kind == SrecConst.MatColor) name = mi.ColorProperties[j].propertyName;
                    else if (kind == SrecConst.MatVector) name = mi.VectorProperties[j].propertyName;
                    else name = mi.TextureProperties[j].propertyName;
                    writer.MatProp(it.id, kind, name ?? "");
                }
                it.names[kind] = count;
                return count;
            }
            if (count < known) Shrunk(it);
            return count;
        }

        void Shrunk(MiItem it)
        {
            if (it.shrunk) return;
            it.shrunk = true;
            Warn("MaterialInstance property list shrank at runtime (indices may shift): " + table[it.id].path);
        }

        void SampleProbes(int k, bool full)
        {
            float eps = spec.posEps;
            for (int i = 0; i < npb; i++)
            {
                ref ProbeItem it = ref pbs[i];
                if (it.dead) continue;
                ref XfItem o = ref xs[it.owner];
                if (o.dead || it.p == null) { it.dead = true; Dead(it.id); continue; }
                if (!(full || o.activeH)) continue;
                var p = it.p.GetCurPos();
                if (it.set && !full && XfMath.Dist(p, it.last) <= eps) continue;
                it.last = p;
                it.set = true;
                writer.Probe(it.id, p);
                Key(it.id, k);
            }
        }

        void SampleRopes(int k, bool full)
        {
            float eps = spec.posEps;
            for (int i = 0; i < nrope; i++)
            {
                var it = ropes[i];
                if (it.dead) continue;
                ref XfItem o = ref xs[it.owner];
                if (o.dead || it.r == null) { it.dead = true; Dead(it.id); continue; }
                if (!(full || o.activeH)) continue;
                int n = ReadRope(it);
                if (n < 0) { it.cur = new Vector3[-n + 8]; it.last = new Vector3[-n + 8]; it.set = false; n = ReadRope(it); }
                if (n <= 0) continue;
                bool changed = !it.set || full || n != it.n;
                if (!changed) for (int j = 0; j < n; j++) if (XfMath.Dist(it.cur[j], it.last[j]) > eps) { changed = true; break; }
                if (!changed) continue;
                Array.Copy(it.cur, it.last, n);
                it.n = n;
                it.set = true;
                writer.Rope(it.id, it.cur, n);
                Key(it.id, k);
            }
        }

        void SampleFog(bool full)
        {
            var f = FogAmbientBlender.currentBlendVal;
            if (f == null) return;
            fogNow[0] = C(f.AmbientColor); fogNow[1] = C(f.FogColor); fogNow[2] = S(f.FogStart); fogNow[3] = S(f.FogEnd);
            fogNow[4] = C(f.WaterFogColor); fogNow[5] = S(f.WaterFogStart); fogNow[6] = S(f.WaterFogEnd); fogNow[7] = S(f.WaterFogOverride);
            fogNow[8] = S(f.BloomIntensity); fogNow[9] = S(f.BloomThreshold); fogNow[10] = S(f.BloomThresholdHDR);
            fogNow[11] = S(f.bloomSize_2); fogNow[12] = S(f.bloomSize_4); fogNow[13] = S(f.bloomSize_8); fogNow[14] = S(f.bloomSize_16);
            fogNow[15] = S(f.bloomIntensity_2); fogNow[16] = S(f.bloomIntensity_4); fogNow[17] = S(f.bloomIntensity_8); fogNow[18] = S(f.bloomIntensity_16);
            fogNow[19] = S(f.bloomIntensity_32); fogNow[20] = S(f.bloomSaturation); fogNow[21] = C(f.BackgroundColor);
            for (int i = 0; i < FogNames.Length; i++)
            {
                if (fogSet && !full && fogNow[i] == fogLast[i]) continue;
                fogLast[i] = fogNow[i];
                writer.Render(i, fogType[i], fogNow[i]);
            }
            fogSet = true;
        }

        static Vector4 C(Color c) { return new Vector4(c.r, c.g, c.b, c.a); }

        static Vector4 S(float v) { return new Vector4(v, 0f, 0f, 0f); }

        void SampleVirtual(Camera cam)
        {
            var v = virt;
            if (cam != null)
            {
                Vector3 p; Quaternion r;
                cam.transform.GetPositionRotation(out p, out r);
                v.camPos = p; v.camRot = r; v.camFov = cam.fieldOfView;
            }
            byte fl = 0;
            try
            {
                var boy = ScriptGlobals.boy;
                if (boy != null)
                {
                    fl |= SrecConst.VirtBoy;
                    v.boyPos3 = boy.pos3; v.boyVel3 = boy.velocity3;
                    v.boyState = TypeName(boy.state);
                    var vb = boy.visualBoy;
                    v.boyVisual = vb != null ? TypeName(vb.state) : "";
                }
            }
            catch { }
            try
            {
                var h = ScriptGlobals.huddle;
                if (h != null && h.isCharacterActive)
                {
                    fl |= SrecConst.VirtHuddle;
                    v.huddlePos3 = h.pos3; v.huddleVel3 = h.velocity3;
                    v.huddleState = TypeName(h.state);
                    if (ScriptGlobals.mainCharacter == h) fl |= SrecConst.VirtHuddleMain;
                }
            }
            catch { }
            if (v.game.ok) v.probe = v.game.probe;
            else
            {
                try { var cs = CameraScript.main; if (cs != null && cs.cameraBlendProbe != null) v.probe = cs.cameraBlendProbe.worldPosition; } catch { }
            }
            v.flags = fl;
            writer.Virtual(v);
        }

        void WatchSpawns(int k)
        {
            if (!spec.spawned) return;
            if (k > 0 && k % 30 == 0) FindNewContainers();
            for (int ci = 0; ci < containers.Count; ci++)
            {
                var c = containers[ci];
                if (c.t == null) continue;
                if (xs[c.xf].dead) continue;
                int n = c.t.childCount;
                Grow(ref childBuf, n);
                bool same = n == c.count;
                for (int i = 0; i < n; i++)
                {
                    int id = c.t.GetChild(i).GetInstanceID();
                    childBuf[i] = id;
                    if (same && c.known[i] != id) same = false;
                }
                if (same) continue;
                for (int i = 0; i < n; i++)
                {
                    int id = childBuf[i];
                    if (xfByInstance.ContainsKey(id)) continue;
                    AddSpawned(c.t.GetChild(i), c);
                }
                Grow(ref c.known, n);
                Array.Copy(childBuf, c.known, n);
                c.count = n;
            }
        }

        void FindNewContainers()
        {
            if (!dynamicsFound)
            {
                var d = GameObject.Find("_Dynamics");
                if (d != null) { TryContainer(d.transform); dynamicsFound = true; }
            }
            for (int i = 0; i < subsceneNodes.Count; i++)
            {
                var sn = subsceneNodes[i];
                if (sn == null) continue;
                var c = sn.transform.Find("_SubsceneDynamics");
                if (c != null) TryContainer(c);
            }
        }

        void TryContainer(Transform t)
        {
            int inst = t.GetInstanceID();
            for (int i = 0; i < containers.Count; i++) if (containers[i].t == t) return;
            int xi;
            if (!xfByInstance.TryGetValue(inst, out xi))
            {
                var s = RescanScan();
                s.containerIds.Add(inst);
                Walk(s, t, false, "");
                if (s.nodes.Count == 0) return;
                AddNodes(s, "container");
                if (!xfByInstance.TryGetValue(inst, out xi)) return;
            }
            var c = new Container { t = t, xf = xi };
            containers.Add(c);
            DevLog.Write("srec: spawn container found during the record: " + Inspector.PathOf(t));
        }

        void AddSpawned(Transform child, Container c)
        {
            var s = RescanScan();
            s.containerIds.Add(c.t.GetInstanceID());
            var n0 = new Node { t = c.t, go = c.t.gameObject, path = table[xs[c.xf].id].path, depth = table[xs[c.xf].id].depth };
            s.nodes.Add(n0);
            s.byInstance[c.t.GetInstanceID()] = 0;
            Walk(s, child, true, "spawned:" + c.t.name);
            if (s.nodes.Count <= 1) return;
            int before = table.Count;
            AddNodes(s, "spawn", 1, c.xf);
            spawnedAdded += table.Count - before;
            if (evKind.Count < 10000) { evKind.Add("spawn"); evName.Add(Inspector.PathOf(child)); evDetail.Add((table.Count - before).ToString(IC) + " objects"); }
        }

        void AddNodes(Scan s, string why) { AddNodes(s, why, 0, -1); }

        void AddNodes(Scan s, string why, int from, int containerXf)
        {
            Finish(s, from);
            int before = table.Count;
            int nxBefore = nx;
            var nodes = s.nodes;
            var nodeXf = new int[nodes.Count];
            var mover = new bool[nodes.Count];
            var reason = new byte[nodes.Count];
            for (int i = 0; i < from; i++)
            {
                nodeXf[i] = containerXf;
                reason[i] = containerXf >= 0 ? xs[containerXf].reason : (byte)0;
                mover[i] = reason[i] != 0;
            }
            for (int i = from; i < nodes.Count; i++)
            {
                var n = nodes[i];
                if (n.t == null || xfByInstance.ContainsKey(n.t.GetInstanceID())) { nodeXf[i] = -1; continue; }
                int parentXf = n.parent >= 0 ? nodeXf[n.parent] : -1;
                if (parentXf < 0 && n.t.parent != null) { int pxi; if (xfByInstance.TryGetValue(n.t.parent.GetInstanceID(), out pxi)) parentXf = pxi; }
                byte pr = n.parent >= 0 && n.parent < i ? reason[n.parent] : parentXf >= 0 ? xs[parentXf].reason : (byte)0;
                mover[i] = Mover(n, n.parent >= 0 && n.parent < i ? mover[n.parent] : pr != 0);
                reason[i] = mover[i] ? MoverReason(n, pr) : (byte)0;
                if (mover[i] && reason[i] == 0) reason[i] = 8;
                bool local = !n.forceWorld && parentXf >= 0;
                Vector3 p; Quaternion q;
                if (local) n.t.GetLocalPositionRotation(out p, out q); else n.t.GetPositionRotation(out p, out q);
                var o = new SrecObject
                {
                    kind = n.kind, instanceId = n.t.GetInstanceID(), depth = n.depth, path = n.path, owner = n.owner ?? "",
                    comps = n.mask, pos = p, rot = q, scale = n.t.localScale, activeSelf = n.go.activeSelf, extent = n.extent,
                    parentId = parentXf >= 0 ? xs[parentXf].id : -1
                };
                o.ordinals.AddRange(n.ord);
                o.siblings.AddRange(n.sib);
                o.types.AddRange(n.types);
                byte fl = 0;
                if (local) fl |= SrecConst.ObjLocal;
                if (n.camXf) fl |= SrecConst.ObjCamXf;
                if (n.camDriven) fl |= SrecConst.ObjCamDriven;
                if (n.spawned) fl |= SrecConst.ObjSpawned;
                if (n.go.activeInHierarchy) fl |= SrecConst.ObjSeenActive;
                o.flags = fl;
                int id = NewId(o, n.t);
                adds.Add(o);
                Grow(ref xs, nx + 1);
                int xi = nx++;
                nodeXf[i] = xi;
                var x = new XfItem
                {
                    t = n.t, go = n.go, rb = n.rb, id = id, parentIdx = parentXf, parentId = o.parentId,
                    parentInst = n.t.parent != null ? n.t.parent.GetInstanceID() : 0, local = local, camXf = n.camXf, camDriven = n.camDriven,
                    rotThr = XfMath.RotThreshold(spec.posEps, n.extent, spec.rotEps), activeSelf = n.go.activeSelf, activeH = n.go.activeInHierarchy, seenAt = -1,
                    spawnedXf = n.spawned, kids = n.t.childCount
                };
                x.s.Reset();
                x.q.ok = quietTransforms && !mover[i];
                x.reason = reason[i];
                xfByReason[x.reason]++;
                if (x.q.ok) quietEligible++;
                xs[xi] = x;
                xfByInstance[o.instanceId] = xi;
                if (n.spawned) spawnedXfs.Add(xi);
                flagHas[id] = true;
                if (n.camXf) camXfCount++;
                if (n.comps == null) continue;
                var seen = new Dictionary<Type, int>();
                foreach (var c in n.comps)
                {
                    if (c == null || c is Transform) continue;
                    var ty = c.GetType();
                    int k;
                    seen.TryGetValue(ty, out k);
                    seen[ty] = k + 1;
                    string cpath = n.path + "::" + ty.Name + (k > 0 ? "[" + k.ToString(IC) + "]" : "");
                    int tb = table.Count;
                    if (c is ParticleSystem && ParticleWanted(n)) AddParticles(n, (ParticleSystem)c, cpath, id, xi, fl);
                    else if (c is Light && spec.lights) AddLight(n, (Light)c, cpath, id, xi, fl);
                    else if (c is Renderer) AddRenderer(n, (Renderer)c, cpath, id, xi, fl);
                    else if (c is MaterialInstance && spec.materials) AddMaterial(s, n, (MaterialInstance)c, cpath, id, xi, fl);
                    else if (c is ColorBlendProbe && spec.probes) AddProbe(n, (ColorBlendProbe)c, cpath, id, xi, fl);
                    else if (c is RopeLineRenderer) AddRope(n, (RopeLineRenderer)c, cpath, id, xi, fl, null);
                    ShaderComp(c);
                    for (int j = tb; j < table.Count; j++) adds.Add(table[j]);
                }
            }
            bool watch = why.StartsWith("registered", StringComparison.Ordinal);
            for (int i = nxBefore; i < nx; i++) { xs[i].seenAt = -1; xs[i].scaleWatch = watch || xs[i].spawnedXf; }
            ShaderResolve();
            ShaderBlocks();
            objects = table.Count;
            if (table.Count > before) DevLog.Write("srec: " + why + ": " + (table.Count - before) + " object(s) added inline (OBJA)");
        }

        Scan RescanScan()
        {
            var s = new Scan();
            foreach (var id in skipIds) s.skip.Add(id);
            s.excludePaths = excludePaths;
            return s;
        }

        // ------------------------------------------------------------------ subscenes that load or activate during the record

        // Subscenes named by `subscene:` roots, `zone:` roots and record.keep join when they load; subscenes of the `auto` zone
        // (inside record.box) join when they activate. The game's SubsceneCuller.StaticEvents postLoad / postActivate queue
        // them, a poll every rescanEvery samples catches the rest (and re-joins a subscene that was unloaded and loaded again);
        // the walk runs in the sample of the frame that saw the change, before the ObjAdd flush.
        void PlanJoins(Scan s)
        {
            cullerList.Clear();
            cullerIndex.Clear();
            try
            {
                var all = SubsceneCullingManager.instance.GetAllSubsceneCullers();
                for (int i = 0; i < all.Count; i++) if (all[i] != null) cullerList.Add(all[i]);
            }
            catch { }
            int n = cullerList.Count;
            cullerLoadWant = new bool[n];
            cullerActWant = new bool[n];
            cullerRoot = new Transform[n];
            cullersWatched = 0;
            for (int i = 0; i < n; i++)
            {
                var c = cullerList[i];
                string name;
                Bounds b;
                try { name = c.SceneName; b = c.CullingBounds; cullerIndex[c.GetInstanceID()] = i; } catch { continue; }
                cullerLoadWant[i] = JoinRule(name, s.wantNames, s.wantZones);
                cullerActWant[i] = s.auto && InZone(name, s.zone) && (!s.useBox || s.box.Intersects(b));
                Transform r;
                if (s.joined.TryGetValue(c.GetInstanceID(), out r)) cullerRoot[i] = r;
                if (cullerLoadWant[i] || cullerActWant[i]) cullersWatched++;
            }
        }

        void HookStreaming()
        {
            if (streamHooked || cullersWatched == 0) return;
            onLoaded = c => Streamed(c, "load");
            onActivated = c => Streamed(c, "activate");
            SubsceneCuller.StaticEvents.postLoad += onLoaded;
            SubsceneCuller.StaticEvents.postActivate += onActivated;
            streamHooked = true;
        }

        void UnhookStreaming()
        {
            if (!streamHooked) return;
            streamHooked = false;
            SubsceneCuller.StaticEvents.postLoad -= onLoaded;
            SubsceneCuller.StaticEvents.postActivate -= onActivated;
        }

        void Streamed(SubsceneCuller c, string why)
        {
            if (ended || c == null) return;
            int i;
            if (!cullerIndex.TryGetValue(c.GetInstanceID(), out i) || !(cullerLoadWant[i] || cullerActWant[i]) || streamQueue.Contains(i)) return;
            streamQueue.Add(i);
            streamWhy.Add(why);
        }

        void WatchStreaming(int k)
        {
            if (cullersWatched == 0) return;
            if (k == 0 || k % Math.Max(1, spec.rescanEvery) == 0)
                for (int i = 0; i < cullerList.Count; i++)
                    if ((cullerLoadWant[i] || cullerActWant[i]) && cullerRoot[i] == null && !streamQueue.Contains(i)) { streamQueue.Add(i); streamWhy.Add("rescan"); }
            if (streamQueue.Count == 0) return;
            for (int q = 0; q < streamQueue.Count; q++) TryJoin(streamQueue[q], streamWhy[q], k);
            streamQueue.Clear();
            streamWhy.Clear();
        }

        void TryJoin(int i, string why, int k)
        {
            var c = cullerList[i];
            if (c == null || cullerRoot[i] != null) return;
            bool loaded, act;
            try { loaded = c.IsLoaded && c.SubsceneNode != null; act = c.IsActive; } catch { return; }
            if (!loaded || !(cullerLoadWant[i] || (cullerActWant[i] && act))) return;
            JoinSubscene(i, c, why, k);
        }

        void JoinSubscene(int i, SubsceneCuller c, string why, int k)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            string name = "";
            Transform root = null;
            SubsceneNode node = null;
            try { name = c.SceneName; node = c.SubsceneNode; root = c.ContentRoot; } catch { }
            if (root == null) return;
            int have;
            if (xfByInstance.TryGetValue(root.GetInstanceID(), out have) && !xs[have].dead) { cullerRoot[i] = root; return; }
            string bn = SceneBase(name);
            var s = RescanScan();
            int before = table.Count;
            if (!AddSubscene(s, c, bn)) return;
            MarkCameraPaths(s);
            if (prep != null && !prep.Released) ApplyPrep(prep, s, new SpecReport(), true);
            AddNodes(s, "subscene " + bn);
            cullerRoot[i] = root;
            if (node != null && !subsceneNodes.Contains(node))
            {
                subsceneNodes.Add(node);
                if (spec.spawned) { var d = node.transform.Find("_SubsceneDynamics"); if (d != null) TryContainer(d); }
            }
            int added = table.Count - before;
            double ms = MsSince(t0);
            joinsDone++;
            if (ms > joinMsMax) joinMsMax = ms;
            var j = new Json.Obj();
            j["subscene"] = bn; j["sample"] = k; j["trigger"] = why; j["objects"] = added; j["ms"] = Math.Round(ms, 2);
            if (joinLog.Count < 200) joinLog.Add(j);
            if (evKind.Count < 10000) { evKind.Add("subscene"); evName.Add(name); evDetail.Add("joined (" + why + "): " + added + " objects, " + ms.ToString("0.0", IC) + " ms"); }
            string msg = "subscene " + bn + " joined the record at sample " + k + " (" + why + "): " + added + " objects in " + ms.ToString("0.0", IC) + " ms";
            if (ms > JoinWarnMs) Warn(msg + "; that frame paid the walk (record.keep, or stream.cullers plus a root, records it from sample 0)");
            else DevLog.Write("srec: " + msg);
        }

        void MarkCameraPaths(Scan s)
        {
            if (camPaths == null || camPaths.Count == 0) return;
            foreach (var n in s.nodes) if (n.t != null && camPaths.Contains(n.path)) s.camXfRoots.Add(n.t.GetInstanceID());
        }

        // ------------------------------------------------------------------ objects created during the record

        // Register (public): a mod names an object it created outside _Dynamics / _SubsceneDynamics; it is recorded with its
        // subtree in the next sample (local to its parent when the parent is recorded, else in world space). WatchChildren
        // compares the child count of every spawned transform each sample and of every other recorded transform every
        // rescanEvery samples (staggered): a new unrecorded child (an effect instantiated under a worker's bone) is added as a
        // spawned subtree. InsideDev* objects and record.exclude paths stay out.
        void TakeRegistered(int k)
        {
            if (registerQueue.Count == 0) return;
            var list = registerQueue.ToArray();
            registerQueue.Clear();
            foreach (var t in list)
            {
                if (t == null || xfByInstance.ContainsKey(t.GetInstanceID())) continue;
                int pxi = -1;
                var p = t.parent;
                if (p != null) { int xi; if (xfByInstance.TryGetValue(p.GetInstanceID(), out xi) && !xs[xi].dead) pxi = xi; }
                int added = AddUnder(t, pxi, "registered:" + (p != null ? p.name : "root"), "registered", k);
                if (added > 0) registered++;
            }
        }

        void WatchChildren(int k)
        {
            if (!spec.spawned) return;
            int every = Math.Max(1, spec.rescanEvery);
            int n = nx, ns = spawnedXfs.Count;
            for (int j = 0; j < ns; j++) ChildCheck(spawnedXfs[j], k);
            for (int i = (every - k % every) % every; i < n; i += every) if (!xs[i].spawnedXf) ChildCheck(i, k);
        }

        void ChildCheck(int i, int k)
        {
            if (xs[i].dead || xs[i].camXf) return;
            var t = xs[i].t;
            if (t == null) return;
            int cc = t.childCount;
            if (cc == xs[i].kids) return;
            xs[i].kids = cc;
            AddNewChildren(i, k);
        }

        void AddNewChildren(int pxi, int k)
        {
            var pt = xs[pxi].t;
            string ppath = table[xs[pxi].id].path;
            int n = pt.childCount;
            for (int j = 0; j < n; j++)
            {
                var c = pt.GetChild(j);
                if (c == null) continue;
                int inst = c.GetInstanceID();
                if (xfByInstance.ContainsKey(inst) || skipIds.Contains(inst) || c.name.StartsWith("InsideDev", StringComparison.Ordinal)) continue;
                if (excludePaths != null && ExcludedPath(excludePaths, ppath + "/" + c.name, c.name)) continue;
                AddUnder(c, pxi, "spawned:" + pt.name, "spawn", k);
            }
        }

        int AddUnder(Transform child, int pxi, string owner, string why, int k)
        {
            var s = RescanScan();
            int from = 0;
            if (pxi >= 0)
            {
                var pt = xs[pxi].t;
                var po = table[xs[pxi].id];
                s.nodes.Add(new Node { t = pt, go = pt.gameObject, path = po.path, depth = po.depth, spawned = xs[pxi].spawnedXf });
                s.byInstance[pt.GetInstanceID()] = 0;
                from = 1;
            }
            Walk(s, child, true, owner);
            if (s.nodes.Count <= from) return 0;
            int before = table.Count;
            AddNodes(s, why, from, pxi);
            int added = table.Count - before;
            spawnedAdded += added;
            if (evKind.Count < 10000) { evKind.Add("spawn"); evName.Add(s.nodes[from].path); evDetail.Add(added.ToString(IC) + " objects (" + why + ")"); }
            return added;
        }

        void DestroyedSpawn(int id, int k)
        {
            destroyedSpawned++;
            string path = table[id].path;
            if (destroyedPaths.Count < 50) destroyedPaths.Add(path + " @" + k.ToString(IC));
            if (destroyedSpawned == 1) Warn("spawned object destroyed during the record at sample " + k + " (" + path + "): the replay has no object to show for it; hide objects (SetActive false) instead of Destroy");
        }

        // ------------------------------------------------------------------ recorder events (PostEvent, srec event)

        public static event Action<string, string> posted;

        // A timed event of kind "event" in the active recording (scene.srec section 11, record.json eventTimes) at the game
        // time of the next sample; `@name` in a spec resolves to its first post. Main thread only. Returns false when no
        // recording is active or the name is not a valid event name (letters, digits, '_' and '.'). Listeners of `posted`
        // (spec events of type "posted") hear every valid post, recorded or not.
        public static bool PostEvent(string name, string detail)
        {
            name = (name ?? "").Trim();
            detail = detail ?? "";
            if (!EventName(name)) { DevLog.Write("srec: event name '" + name + "' refused (letters, digits, '_' and '.' only)"); return false; }
            bool stored = false;
            var r = active;
            if (r != null && !r.ended && !r.failed && r.writer != null)
            {
                r.Event("event", name, detail);
                r.posts++;
                stored = true;
            }
            var h = posted;
            if (h != null) { try { h(name, detail); } catch (Exception e) { DevLog.Error("srec posted event listener", e); } }
            return stored;
        }

        // Records `target` (a Transform, GameObject or Component) with its subtree in the next sample of the active recording;
        // false when no recording is active. For objects a mod creates outside _Dynamics while recording.
        public static bool Register(UnityEngine.Object target)
        {
            var r = active;
            if (r == null || r.ended || r.failed || target == null) return false;
            var t = target as Transform;
            if (t == null)
            {
                var go = target as GameObject;
                if (go != null) t = go.transform;
                else { var c = target as Component; if (c != null) t = c.transform; }
            }
            if (t == null) return false;
            if (!registerQueue.Contains(t)) registerQueue.Add(t);
            return true;
        }

        void LogEvent(string name, string detail, int k, double game)
        {
            eventsLogged++;
            if (logName.Count >= MaxEventLog) return;
            logName.Add(name); logDetail.Add(detail); logSample.Add(k); logTime.Add(game);
        }

        internal List<object> EventTimesJson()
        {
            var l = new List<object>(logName.Count);
            for (int i = 0; i < logName.Count; i++)
            {
                var e = new Json.Obj();
                e["name"] = logName[i]; e["time"] = logTime[i]; e["sample"] = logSample[i]; e["detail"] = logDetail[i];
                l.Add(e);
            }
            return l;
        }

        // ------------------------------------------------------------------ End

        public void End(SpecReport report)
        {
            if (ended) return;
            ended = true;
            if (report == null) report = new SpecReport();
            try { if (onPreCull != null) { SceneHooks.preCullMain -= onPreCull; SceneHooks.Release(); } }
            catch (Exception e) { report.Warn("replay.record", "SceneHooks release: " + e.Message); }
            try { UnhookStreaming(); } catch (Exception e) { report.Warn("replay.record", "subscene stream hooks release: " + e.Message); }
            if (active == this) registerQueue.Clear();
            if (destroyedSpawned > 1) Warn(destroyedSpawned + " spawned object(s) destroyed during the record (record.json destroyedSpawnedPaths)");
            if (evKind.Count > 0) Warn(evKind.Count + " event(s) after the last sample are kept in record.json only");
            try { if (writer != null) { writer.Close(); bytes = writer.bytes; } }
            catch (Exception e) { report.Error("replay.record", "scene.srec close: " + e.Message); }
            if (writer != null && writer.error != null) report.Error("replay.record", "scene.srec writer: " + writer.error);
            gcEnd = GC.CollectionCount(0);
            try { if (prep != null) activatedGroups = prep.ActivatedGroups(); } catch { }
            for (int i = 0; i < np; i++)
            {
                var it = ps[i];
                if (it.playingSamples > 10 && it.playingZero == it.playingSamples && staleParticles.Count < 500) staleParticles.Add(table[it.id].path);
            }
            if (staleParticles.Count > 0) Warn(staleParticles.Count + " particle system(s) played with particleCount 0 for the whole record (off-screen culled? possibly stale in side cameras)");
            if (ropeFallback > 0) Warn("rope vertices read by reflection (" + ropeFallback + " reads, allocates)");
            try { WriteObjects(); } catch (Exception e) { report.Warn("replay.record", ReplayConst.ObjectsFile + ": " + e.Message); }
            try { WriteMeta(); } catch (Exception e) { report.Warn("replay.record", ReplayConst.RecordMetaFile + ": " + e.Message); }
            try { WriteJson(); } catch (Exception e) { report.Warn("replay.record", ReplayConst.RecordJson + ": " + e.Message); }
            if (failed) report.Error("replay.record", "recording failed: " + lastError);
            else if (errors > 0) report.Warn("replay.record", errors + " sample error(s), last: " + lastError);
            if (samples == 0) report.Warn("replay.record", "no sample was taken (no preCull of the world camera after Begin?)");
            if (active == this) active = null;
            last = this;
            loadCountAtEnd = CurrentLoadCount();
            sessionRecorders.Remove(this);
            sessionRecorders.Add(this);
            if (sessionRecorders.Count > 16) sessionRecorders.RemoveAt(0);
            DevLog.Write("srec: end " + Status());
        }

        static string Tsv(string s) { return (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '); }

        static string KindName(byte k)
        {
            switch (k)
            {
                case SrecConst.KindTransform: return "transform";
                case SrecConst.KindBoyBone: return "boyBone";
                case SrecConst.KindClothBone: return "clothBone";
                case SrecConst.KindParticles: return "particles";
                case SrecConst.KindLight: return "light";
                case SrecConst.KindMaterial: return "material";
                case SrecConst.KindProbe: return "probe";
                case SrecConst.KindSpawned: return "spawned";
                case SrecConst.KindRope: return "rope";
                case SrecConst.KindRenderer: return "renderer";
            }
            return k.ToString(IC);
        }

        static readonly string[] CompNames = { "Rigidbody", "Animation", "Animator", "ParticleSystem", "Light", "Renderer", "SkinnedMeshRenderer", "MaterialInstance", "D11VelocityBufferTag", "ColorBlendProbe", "LineRenderer", "Actor", "Collider" };

        static string CompText(uint m)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < CompNames.Length; i++)
                if ((m & (1u << i)) != 0) { if (sb.Length > 0) sb.Append(','); sb.Append(CompNames[i]); }
            return sb.ToString();
        }

        static string FlagText(byte f)
        {
            var l = new List<string>();
            if ((f & SrecConst.ObjCamDriven) != 0) l.Add("camDriven");
            if ((f & SrecConst.ObjScaleVariant) != 0) l.Add("scaleVariant");
            if ((f & SrecConst.ObjExcludedMaterial) != 0) l.Add("excludedMaterial");
            if ((f & SrecConst.ObjSpawned) != 0) l.Add("spawned");
            if ((f & SrecConst.ObjLocal) != 0) l.Add("local");
            if ((f & SrecConst.ObjCamXf) != 0) l.Add("camXf");
            if ((f & SrecConst.ObjSeenActive) != 0) l.Add("seenActive");
            return string.Join(",", l.ToArray());
        }

        // objects.tsv flags: the table flags plus scaleVariant, and seenActive also for an object (or a component whose owner
        // transform) was active in hierarchy at any sample, not only when it entered the table: an object registered while
        // inactive (a mod's clone switched on later) shows its activation here too (its FlActive steps are in scene.srec).
        void WriteObjects()
        {
            var sb = new StringBuilder(table.Count * 120);
            sb.Append("id\tkind\tpath\towner\tcomps\tfirst\tlast\tkeys\tflags\n");
            var scaleVar = new HashSet<int>();
            var everActive = new HashSet<int>();
            for (int i = 0; i < nx; i++)
            {
                if (xs[i].scaleVariant) scaleVar.Add(xs[i].id);
                if (xs[i].everActive) everActive.Add(xs[i].id);
            }
            foreach (var o in table)
            {
                byte fl = o.flags;
                if (scaleVar.Contains(o.id)) fl |= SrecConst.ObjScaleVariant;
                if (ReplayConst.SeenActive(o.kind, o.id, o.parentId, everActive)) fl |= SrecConst.ObjSeenActive;
                sb.Append(o.id.ToString(IC)).Append('\t').Append(KindName(o.kind)).Append('\t').Append(Tsv(o.path)).Append('\t').Append(Tsv(o.owner)).Append('\t')
                  .Append(CompText(o.comps)).Append('\t').Append(keyFirst[o.id].ToString(IC)).Append('\t').Append(keyLast[o.id].ToString(IC)).Append('\t')
                  .Append(keyCount[o.id].ToString(IC)).Append('\t').Append(FlagText(fl)).Append('\n');
            }
            File.WriteAllText(Path.Combine(dir, ReplayConst.ObjectsFile), sb.ToString());
        }

        void WriteMeta()
        {
            var sb = new StringBuilder(metaN * 96 + 128);
            sb.Append("sample,frame_count,game_time,dt,real_time,logic_step,fixed_steps,time_scale,flags,xf_keys,particles,bytes\n");
            for (int i = 0; i < metaN; i++)
            {
                sb.Append(i.ToString(IC)).Append(',').Append(mFrame[i].ToString(IC)).Append(',').Append(mGame[i].ToString("0.######", IC)).Append(',')
                  .Append(mDt[i].ToString("0.######", IC)).Append(',').Append(mReal[i].ToString("0.######", IC)).Append(',').Append(mStep[i].ToString(IC)).Append(',')
                  .Append(mFixed[i].ToString(IC)).Append(',').Append(mScale[i].ToString("0.####", IC)).Append(',').Append(mFlags[i].ToString(IC)).Append(',')
                  .Append(mXf[i].ToString(IC)).Append(',').Append(mParts[i].ToString(IC)).Append(',').Append(mBytes[i].ToString(IC)).Append('\n');
            }
            File.WriteAllText(Path.Combine(dir, ReplayConst.RecordMetaFile), sb.ToString());
        }

        static double Pct(float[] a, int n, double q)
        {
            if (n <= 0) return 0;
            var c = new float[n];
            Array.Copy(a, c, n);
            Array.Sort(c);
            int i = (int)Math.Min(n - 1, Math.Max(0, Math.Ceiling(q * n) - 1));
            return c[i];
        }

        static float[] Tail(float[] a, int n)
        {
            var r = new float[Math.Max(0, n - 1)];
            if (n > 1) Array.Copy(a, 1, r, 0, n - 1);
            return r;
        }

        static List<object> Strs(List<string> l) { var r = new List<object>(l.Count); foreach (var s in l) r.Add(s); return r; }

        static double Num(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? 0 : v; }

        static Json.Obj HeaderJson(RecordHeader h)
        {
            var o = new Json.Obj();
            o["version"] = h.version; o["created"] = h.created; o["spec"] = h.spec; o["level"] = h.level; o["recorderVersion"] = h.recorderVersion; o["clock"] = h.clock;
            o["captureFramerate"] = h.captureFramerate; o["subscene"] = h.subscene; o["savepoint"] = h.savepoint; o["streamVersion"] = h.streamVersion;
            o["screenW"] = h.screenW; o["screenH"] = h.screenH; o["fixedDeltaTime"] = h.fixedDeltaTime; o["t0"] = h.t0; o["t0Real"] = h.t0Real;
            o["stepAtT0"] = h.stepAtT0; o["gameFov"] = (double)h.gameFov;
            o["subscenes"] = Strs(h.subscenes); o["keptCullers"] = Strs(h.keptCullers); o["forcedCullers"] = Strs(h.forcedCullers);
            o["fogFields"] = Strs(h.fogFields); o["postFields"] = Strs(h.postFields); o["cameraDriven"] = Strs(h.cameraDriven);
            o["posEps"] = (double)h.posEps; o["rotEps"] = (double)h.rotEps; o["cut"] = (double)h.cut;
            o["vSyncCount"] = h.vSyncCount; o["targetFrameRate"] = h.targetFrameRate; o["refreshRate"] = h.refreshRate;
            return o;
        }

        void WriteJson()
        {
            var o = new Json.Obj();
            o["recorder"] = Version;
            o["file"] = ReplayConst.RecordFile;
            o["samples"] = samples;
            o["objects"] = table.Count;
            var kinds = new Json.Obj();
            var kc = new int[16];
            foreach (var t in table) if (t.kind < kc.Length) kc[t.kind]++;
            for (int i = 0; i < kc.Length; i++) if (kc[i] > 0) kinds[KindName((byte)i)] = kc[i];
            o["objectsByKind"] = kinds;
            o["bytes"] = bytes;
            o["duration"] = metaN > 0 ? mGame[metaN - 1] : 0.0;
            o["realDuration"] = metaN > 0 ? mReal[metaN - 1] : 0.0;
            o["zone"] = zone;
            var keys = new Json.Obj();
            keys["xf"] = xfKeys; keys["preKeys"] = preKeys; keys["cuts"] = cuts; keys["parentChanges"] = parentChanges;
            keys["particleFrames"] = particleFrames; keys["particles"] = particlesTotal;
            o["keys"] = keys;
            var drift = new Json.Obj();
            drift["endMs"] = Num(endDriftMs); drift["maxMs"] = Num(maxDriftMs);
            o["drift"] = drift;
            var cost = new Json.Obj();
            double sum = 0, max = 0;
            for (int i = 0; i < metaN; i++) { sum += mCost[i]; if (mCost[i] > max) max = mCost[i]; }
            cost["meanMs"] = metaN > 0 ? sum / metaN : 0.0; cost["p99Ms"] = Pct(mCost, metaN, 0.99); cost["maxMs"] = max;
            cost["firstMs"] = metaN > 0 ? (double)mCost[0] : 0.0;
            var chans = new Json.Obj();
            int nc = ChannelNames.Length;
            var col = new float[metaN];
            for (int c = 0; c < nc; c++)
            {
                double cs = 0, cm = 0;
                for (int i = 0; i < metaN; i++) { float v = mChan[i * nc + c]; col[i] = v; cs += v; if (v > cm) cm = v; }
                var cj = new Json.Obj();
                cj["meanMs"] = metaN > 1 ? (cs - mChan[c]) / (metaN - 1) : 0.0;
                cj["p99Ms"] = metaN > 1 ? Pct(Tail(col, metaN), metaN - 1, 0.99) : 0.0;
                cj["maxMs"] = cm;
                chans[ChannelNames[c]] = cj;
            }
            cost["channels"] = chans;
            cost["channelsNote"] = "per-channel main-thread ms per sample; mean and p99 without sample 0 (the full keyframe)";
            o["sampleCost"] = cost;
            var qj = new Json.Obj();
            qj["enabled"] = quietTransforms;
            qj["eligible"] = quietEligible; qj["entered"] = quietEntered; qj["cooled"] = quietCooled;
            qj["skips"] = quietSkips; qj["gatedReads"] = quietReads;
            qj["skipsPerSample"] = metaN > 0 ? quietSkips / (double)metaN : 0.0;
            qj["misses"] = quietMisses; qj["missPaths"] = Strs(quietMissPaths);
            qj["after"] = XfMath.QuietAfter; qj["sweepEvery"] = XfMath.SweepEvery;
            o["quietTransforms"] = qj;
            var rj = new Json.Obj();
            long readsAll = 0;
            for (int i = 0; i < readsByReason.Length; i++) readsAll += readsByReason[i];
            rj["perSample"] = metaN > 0 ? readsAll / (double)metaN : 0.0;
            var by = new Json.Obj();
            for (int i = 0; i < ReadReasons.Length; i++)
            {
                var e = new Json.Obj();
                e["transforms"] = (double)xfByReason[i];
                e["readsPerSample"] = metaN > 0 ? readsByReason[i] / (double)metaN : 0.0;
                by[ReadReasons[i]] = e;
            }
            rj["byReason"] = by;
            rj["note"] = "GetPositionRotation reads per sample by the reason a transform is not quiet-eligible (the first rule that holds for it; belowMover = under a recorded mover); inactive objects are not read";
            o["transformReads"] = rj;
            o["recordSetBuildMs"] = buildMs;
            o["prebuilt"] = prebuildMs >= 0;
            var gc = new Json.Obj();
            gc["collectionsAfterSample30"] = gcAt30 >= 0 ? gcEnd - gcAt30 : -1;
            gc["heapAtSample30"] = heapAt30;
            gc["heapAtEnd"] = GC.GetTotalMemory(false);
            o["gc"] = gc;
            o["hitches"] = hitches;
            o["skippedFrames"] = skippedFrames;
            o["sampleErrors"] = errors;
            if (lastError.Length > 0) o["lastSampleError"] = lastError;
            o["failed"] = failed;
            o["writerStalls"] = writer != null ? writer.stalls : 0;
            o["writerError"] = writer != null && writer.error != null ? writer.error : "";
            o["particles"] = particlesOk;
            o["forcedCullers"] = Strs(header.forcedCullers);
            o["activatedGroups"] = Strs(activatedGroups);
            if (prep != null)
            {
                var pj = new Json.Obj();
                pj["mode"] = prep.mode; pj["animations"] = prep.anims.Count; pj["animators"] = prep.animators.Count; pj["forced"] = prep.forced.Count;
                pj["alreadyInvalid"] = prep.alreadyInvalid; pj["extendedAtBegin"] = prep.extendedAnims + prep.extendedCullers;
                o["prep"] = pj;
            }
            o["cameraDrivenTransforms"] = camXfCount;
            o["cameraDrivenFiles"] = Strs(scanNotes);
            o["camDrivenObjects"] = camDrivenCount;
            o["excludedMaterials"] = excludedMaterials;
            o["spawnedAdded"] = spawnedAdded;
            o["deleted"] = spawnedDeleted;
            o["registered"] = registered;
            o["destroyedSpawned"] = destroyedSpawned;
            o["destroyedSpawnedPaths"] = Strs(destroyedPaths);
            var sj = new Json.Obj();
            sj["watched"] = cullersWatched; sj["joined"] = joinsDone; sj["maxMs"] = Math.Round(joinMsMax, 2);
            sj["rescanEvery"] = spec.rescanEvery; sj["log"] = joinLog;
            sj["note"] = "subscenes of subscene:/zone: roots and record.keep join when they load, subscenes of the auto zone when they activate; each join walks the subscene in that sample (ms)";
            o["subsceneJoins"] = sj;
            o["eventTimes"] = EventTimesJson();
            o["eventsLogged"] = eventsLogged;
            o["eventsPosted"] = posts;
            o["meshSwaps"] = meshSwaps;
            o["shaderState"] = ShaderJson();
            o["staleParticles"] = Strs(staleParticles);
            if (evKind.Count > 0)
            {
                var late = new List<object>();
                for (int i = 0; i < evKind.Count && i < 200; i++) late.Add(evKind[i] + " " + evName[i] + " " + evDetail[i]);
                o["lateEvents"] = late;
            }
            o["warnings"] = Strs(warnings);
            o["header"] = HeaderJson(header);
            foreach (var key in extra.order) o[key] = extra[key];
            File.WriteAllText(Path.Combine(dir, ReplayConst.RecordJson), Json.Write(o));
        }
    }
#endif

    // Pure change-detection core of 2.3.2 (managed math only, no internal calls, no game types): compiled into the mod and,
    // with -define:REC_A_MATH_ONLY, into the offline tests under .NET 9.
    public sealed partial class SceneRecorder
    {
        internal delegate int VertexReader(object owner, Vector3[] dst);

        static readonly string[] ZoneSuffixes = { "_Gameplay", "_Environment", "_Backdrop", "_Logic", "_Gamelogic" };

        static bool StartsWithIgnoreCase(string s, string p) { return s.StartsWith(p, StringComparison.OrdinalIgnoreCase); }

        internal static string SceneBase(string n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            int k = n.LastIndexOf('/');
            if (k >= 0) n = n.Substring(k + 1);
            if (n.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 6);
            return n;
        }

        internal static string Plain(string n)
        {
            n = SceneBase(n);
            int h = n.LastIndexOf('#');
            if (h >= 0) n = n.Substring(h + 1);
            return n;
        }

        internal static string ZoneBase(string n)
        {
            n = Plain(n);
            foreach (var suf in ZoneSuffixes)
                if (n.Length > suf.Length && n.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) return n.Substring(0, n.Length - suf.Length);
            return n;
        }

        internal static bool InZone(string sceneName, string zone)
        {
            if (string.IsNullOrEmpty(zone)) return false;
            string b = Plain(sceneName);
            return string.Equals(b, zone, StringComparison.OrdinalIgnoreCase) || StartsWithIgnoreCase(b, zone + "_");
        }

        // A subscene joins the recording on load when a `subscene:` root or record.keep names it (SceneBase, any case) or a
        // `zone:` root covers its zone.
        internal static bool JoinRule(string sceneName, List<string> names, List<string> zones)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            string b = SceneBase(sceneName);
            if (names != null) foreach (var n in names) if (string.Equals(SceneBase(n), b, StringComparison.OrdinalIgnoreCase)) return true;
            if (zones != null) foreach (var z in zones) if (InZone(sceneName, z)) return true;
            return false;
        }

        // record.exclude match for objects that appear after Begin (the same rule as the scan: with a '/' the exact path or a
        // path suffix, else the object name).
        internal static bool ExcludedPath(List<string> queries, string path, string name)
        {
            foreach (var raw in queries)
            {
                string q = (raw ?? "").Trim().Trim('"');
                if (q.Length == 0) continue;
                if (q.IndexOf('/') >= 0) { if (path == q || path.EndsWith("/" + q, StringComparison.Ordinal)) return true; }
                else if (name == q) return true;
            }
            return false;
        }

        internal static bool EventName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
            foreach (char ch in name)
                if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '.')) return false;
            return true;
        }

        // camera_driven.tsv (srec probe camera): path, components, expected, rigid. Returns the path of a row to apply,
        // null for the header, empty rows and rows marked rigid (counted in `rigid`).
        internal static string CameraDrivenRow(string line, ref int rigid)
        {
            if (string.IsNullOrEmpty(line)) return null;
            var cols = line.TrimEnd('\r').Split('\t');
            if (cols.Length == 0 || cols[0].Length == 0 || cols[0] == "path") return null;
            if (cols.Length > 3 && cols[3].Trim() == "1") { rigid++; return null; }
            return cols[0];
        }

        // Files read for camXf objects (2.3.1 "read by later records of that zone"): the record folder, the take folder,
        // the record folders of sibling takes, <_mod>/camera_driven.tsv and <_mod>/srec/*/camera_driven.tsv. Only rows
        // whose path is in the record set are applied, so files of other zones are harmless.
        internal static List<string> CameraDrivenFiles(string recordDir, string modDir, string fileName, int max)
        {
            var r = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = f =>
            {
                if (r.Count >= max || string.IsNullOrEmpty(f)) return;
                string full;
                try { full = Path.GetFullPath(f); } catch { return; }
                if (seen.Add(full) && File.Exists(full)) r.Add(full);
            };
            Action<string> subdirs = root =>
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
                string[] dirs;
                try { dirs = Directory.GetDirectories(root); } catch { return; }
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                foreach (var d in dirs) { add(Path.Combine(d, fileName)); add(Path.Combine(Path.Combine(d, "record"), fileName)); }
            };
            if (!string.IsNullOrEmpty(recordDir))
            {
                string rd = null, take = null, takes = null;
                try { rd = Path.GetFullPath(recordDir).TrimEnd('/', '\\'); take = Path.GetDirectoryName(rd); takes = string.IsNullOrEmpty(take) ? null : Path.GetDirectoryName(take); } catch { }
                add(Path.Combine(rd ?? recordDir, fileName));
                if (!string.IsNullOrEmpty(take)) add(Path.Combine(take, fileName));
                subdirs(takes);
            }
            if (!string.IsNullOrEmpty(modDir))
            {
                add(Path.Combine(modDir, fileName));
                subdirs(Path.Combine(modDir, "srec"));
            }
            return r;
        }

        internal struct XfTrack
        {
            public Vector3 keyPos, keyScale, prevPos, prevScale;
            public Quaternion keyRot, prevRot;
            public int posAt, rotAt, scaleAt, prevAt, prevScaleAt;
            public float prevSpeed;

            public void Reset()
            {
                posAt = rotAt = scaleAt = prevAt = prevScaleAt = -1;
                prevSpeed = 0f;
                keyPos = prevPos = new Vector3(0f, 0f, 0f);
                keyScale = prevScale = new Vector3(1f, 1f, 1f);
                keyRot = prevRot = new Quaternion(0f, 0f, 0f, 1f);
            }
        }

        // Quiet transforms (sampling budget, R1): a transform without Rigidbody, Animation, Animator, Actor or NoNotify writer on
        // itself or a recorded ancestor (`ok`) that produced no key for QuietAfter samples goes dormant: its values are read
        // only when Transform.hasChanged is set (the recorder owns the flag; the game never reads it) or on the staggered
        // sweep every SweepEvery samples. A sweep key without the flag is a miss: the object leaves the quiet set for good.
        // Woken WakeIdleLimit times in a row without a key (a moving parent) it waits QuietCooldown samples before the next try.
        internal struct XfQuiet
        {
            public bool ok, dormant;
            public int run, idle;
        }

        internal static class XfMath
        {
            public const double MinRotThreshold = 2e-6, MinExtent = 0.1;
            public const int ScaleWarmup = 30;
            public const int QuietAfter = 8, SweepEvery = 32, WakeIdleLimit = 4, QuietCooldown = 120;
            public const byte QuietNone = 0, QuietEnter = 1, QuietMiss = 2, QuietCool = 3;

            static readonly HashSet<string> MoverTypes = new HashSet<string>(StringComparer.Ordinal)
            {
                "Chicken", "FakeRagdollJoint", "FakeRagdollJoint2", "FakeRope", "AnimcontrolledCharacter", "AnimControlCustomCode",
                "SleepWalker", "HuddleArmControl2", "HuddleLegControl", "Boy", "VisualBoy", "Huddle", "HuddleCloth", "Actor"
            };

            public static bool MoverType(string typeName) { return typeName != null && MoverTypes.Contains(typeName); }

            public static bool ScaleChanged(Vector3 scale, Vector3 key) { return !Same(scale, key); }

            public static bool SameRot(Quaternion a, Quaternion b) { return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w; }

            public static void Hold(ref XfTrack s, int k)
            {
                s.prevAt = k;
                s.prevScaleAt = k;
                s.prevSpeed = 0f;
            }

            public static byte QuietStep(ref XfQuiet q, bool gated, bool flagged, bool keyed, bool warm)
            {
                if (gated && keyed && !flagged) { q.ok = false; q.dormant = false; q.run = 0; q.idle = 0; return QuietMiss; }
                if (keyed) { q.dormant = false; q.run = 0; q.idle = 0; return QuietNone; }
                if (q.dormant)
                {
                    if (!flagged) { q.idle = 0; return QuietNone; }
                    if (++q.idle < WakeIdleLimit) return QuietNone;
                    q.dormant = false; q.idle = 0; q.run = -QuietCooldown;
                    return QuietCool;
                }
                if (!q.ok) return QuietNone;
                if (q.run < QuietAfter) q.run++;
                if (q.run < QuietAfter || !warm) return QuietNone;
                q.dormant = true; q.idle = 0;
                return QuietEnter;
            }

            public static double RotAngle(Quaternion a, Quaternion b)
            {
                double dot = (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w;
                double s = dot < 0.0 ? -1.0 : 1.0;
                double dx = a.x - s * b.x, dy = a.y - s * b.y, dz = a.z - s * b.z, dw = a.w - s * b.w;
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
                return 4.0 * Math.Asin(Math.Min(1.0, 0.5 * d));
            }

            public static double Dist(Vector3 a, Vector3 b)
            {
                double dx = (double)a.x - b.x, dy = (double)a.y - b.y, dz = (double)a.z - b.z;
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            public static float RotThreshold(float posEps, float extent, float rotEps)
            {
                double e = Math.Max(MinExtent, extent);
                double th = Math.Min(rotEps, Math.Max(MinRotThreshold, posEps / e));
                return (float)th;
            }

            static bool Same(Vector3 a, Vector3 b) { return a.x == b.x && a.y == b.y && a.z == b.z; }

            public static byte Decide(ref XfTrack s, int k, Vector3 pos, Quaternion rot, Vector3 scale, bool scaleRead, double dt,
                                      bool forceCut, bool parentChanged, float posEps, float rotThr, float cut, out byte preBits)
            {
                preBits = 0;
                if (s.posAt < 0) return (byte)(SrecConst.XfPos | SrecConst.XfRot | SrecConst.XfScale);
                byte bits = 0;
                if (!Same(pos, s.keyPos) && Dist(pos, s.keyPos) > posEps) bits |= SrecConst.XfPos;
                if (!SameRot(rot, s.keyRot) && RotAngle(rot, s.keyRot) > rotThr) bits |= SrecConst.XfRot;
                if (scaleRead && !Same(scale, s.keyScale)) bits |= SrecConst.XfScale;
                bool isCut = forceCut || parentChanged;
                if (!isCut && s.prevAt == k - 1 && dt > 0.0 && !Same(pos, s.prevPos))
                {
                    double bound = Math.Max(cut, 3.0 * s.prevSpeed * dt);
                    if (Dist(pos, s.prevPos) > bound) isCut = true;
                }
                if (isCut) bits |= (byte)(SrecConst.XfPos | SrecConst.XfRot | SrecConst.XfCut | (scaleRead ? SrecConst.XfScale : 0));
                if (parentChanged) bits |= SrecConst.XfParent;
                if (bits != 0 && s.prevAt == k - 1)
                {
                    if ((bits & SrecConst.XfPos) != 0 && s.posAt < k - 1) preBits |= SrecConst.XfPos;
                    if ((bits & SrecConst.XfRot) != 0 && s.rotAt < k - 1) preBits |= SrecConst.XfRot;
                    if ((bits & SrecConst.XfScale) != 0 && s.scaleAt < k - 1 && s.prevScaleAt == k - 1) preBits |= SrecConst.XfScale;
                }
                return bits;
            }

            public static void Commit(ref XfTrack s, int k, Vector3 pos, Quaternion rot, Vector3 scale, bool scaleRead, byte bits, double dt, float rbSpeed, bool useRb)
            {
                float speed = 0f;
                if (useRb) speed = rbSpeed;
                else if ((bits & SrecConst.XfCut) == 0 && s.prevAt == k - 1 && dt > 0.0 && !Same(pos, s.prevPos)) speed = (float)(Dist(pos, s.prevPos) / dt);
                if ((bits & SrecConst.XfPos) != 0) { s.keyPos = pos; s.posAt = k; }
                if ((bits & SrecConst.XfRot) != 0) { s.keyRot = rot; s.rotAt = k; }
                if ((bits & SrecConst.XfScale) != 0) { s.keyScale = scale; s.scaleAt = k; }
                s.prevPos = pos;
                s.prevRot = rot;
                s.prevAt = k;
                s.prevSpeed = speed;
                if (scaleRead) { s.prevScale = scale; s.prevScaleAt = k; }
            }

            public static VertexReader BuildVertexReader(Type ownerType, FieldInfo holder, FieldInfo vertices, FieldInfo position)
            {
                var dm = new DynamicMethod("InsideDevVertexRead", typeof(int), new[] { typeof(object), typeof(Vector3[]) }, typeof(XfMath).Module, true);
                var il = dm.GetILGenerator();
                var verts = il.DeclareLocal(vertices.FieldType);
                var i = il.DeclareLocal(typeof(int));
                var n = il.DeclareLocal(typeof(int));
                var hasHolder = il.DefineLabel();
                var none = il.DefineLabel();
                var fits = il.DefineLabel();
                var cond = il.DefineLabel();
                var loop = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, ownerType);
                il.Emit(OpCodes.Ldfld, holder);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Brtrue, hasHolder);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(hasHolder);
                il.Emit(OpCodes.Ldfld, vertices);
                il.Emit(OpCodes.Stloc, verts);
                il.Emit(OpCodes.Ldloc, verts);
                il.Emit(OpCodes.Brfalse, none);
                il.Emit(OpCodes.Ldloc, verts);
                il.Emit(OpCodes.Ldlen);
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Stloc, n);
                il.Emit(OpCodes.Ldloc, n);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldlen);
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Ble, fits);
                il.Emit(OpCodes.Ldloc, n);
                il.Emit(OpCodes.Neg);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(fits);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stloc, i);
                il.Emit(OpCodes.Br, cond);
                il.MarkLabel(loop);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldloc, i);
                il.Emit(OpCodes.Ldelema, typeof(Vector3));
                il.Emit(OpCodes.Ldloc, verts);
                il.Emit(OpCodes.Ldloc, i);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Ldfld, position);
                il.Emit(OpCodes.Stobj, typeof(Vector3));
                il.Emit(OpCodes.Ldloc, i);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, i);
                il.MarkLabel(cond);
                il.Emit(OpCodes.Ldloc, i);
                il.Emit(OpCodes.Ldloc, n);
                il.Emit(OpCodes.Blt, loop);
                il.Emit(OpCodes.Ldloc, n);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(none);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);
                return (VertexReader)dm.CreateDelegate(typeof(VertexReader));
            }
        }
    }
}
