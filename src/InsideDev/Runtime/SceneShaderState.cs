using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Shader state channels (material round 2026-10-25, docs/REPLAY_DESIGN.md 2.3.2 "Shader state", 2.4.3, 4.2
    // sections 17/18): what the game sets on shader globals, shared materials and renderer property blocks during a take.
    // In a replay the game is frozen, so these writers stop (Shader_SetHuddlePosition.Update returns at deltaTime 0, the
    // huddle arm/leg controllers are neutralized) or write stale values (ColorBlendProbe keeps its last blend-box set), and
    // the state would stay at its end-of-take value. Unity 5.0.4 has no Shader.GetGlobal* (ENG/Shader.cs), so globals are
    // recorded from their script writers (the value each writer left this frame); materials and blocks are read back.
    // Writers (decompiled game code, instances from components_summary.tsv):
    // - Globals, recorded: ColorBlendProbe of type ShaderGlobal (HuddleFXProbe: _PDHuddleAmbient colour, _PDHuddleToon
    //   float; FP/ColorBlendProbe.cs:213-226, 362-458), Player2GrassShader (_Player2GrassPos, _Player2GrassPush),
    //   Player2PlantShader (_Player2PlantPos0..4, _Player2PlantPush). Not recorded (per camera or per render, they stay live):
    //   PDReflectionCuller _ReflectionColor, PDReflectionUtils PD_fogFacts / PD_fogWaterColor / PD_reflectionPlane* and the
    //   SOFTPARTICLES keywords, DepthBufferDownsample depth / dither globals (its _RealTime comes from MultiCamLook and the
    //   record clock, 2.4.7), PDPostCombined _ExposedBloom, PDVelocityBuffer _VelocityPrevVP; engine globals are not readable.
    // - Materials: Shader_SetHuddlePosition writes _MainCharPosWorld and _Aspect on every mat[] (CHARACTER_Huddle_2,
    //   CHARACTER_Huddle_Hair) and the nine dam_* bruise vectors on mat[0] (FP/Shader_SetHuddlePosition.cs:94-127).
    // - Property blocks: HuddleArmControl2.armRenderer and HuddleLegControl.legRenderer _DamageLimb (vector),
    //   BoyOcclusionHandler spheres _Intensity (float). MaterialInstance keeps its own values in its property lists (the
    //   MaterialInstance channel); its block is rewritten by the setters the replayer calls. FogFader (the cafeteria railing
    //   dust haze FX_DustCloud_Dissipate_01/02) fades `_Color` through MaterialInstance.SetColorProperty, so the
    //   MaterialInstance channel carries it (take 4: 1673 `_Color` keys per card from the crash at 4.05 s).
    // - Opt-in (record.blocks): property-block values of named renderers, "<path>|<prop>[:float],...", keyed from their
    //   first non-zero value (a block returns 0 for a property it does not hold).
    // Cost: no allocation per sample; one value read per channel, one GetPropertyBlock per active block renderer, changes
    // only (record.json shaderState, sampleCost.channels.shader, budget ShaderBudgetMs).
    public static class ShaderMirror
    {
        public static bool Same(Vector4 a, Vector4 b) { return a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z) && a.w.Equals(b.w); }

        // Write priority of a ShaderGlobal ColorBlendProbe for this frame (its UmbrellaUpdate loops the active boxes from the
        // last to the first, each overriding box writes, a removed box restores the base): 3 = an overriding box wrote,
        // 1 = base (no active box, or a box left the list and nothing overrode), -1 = no write (the global holds).
        public static int ProbePriority(int boxes, bool shrunk, bool overridden)
        {
            if (overridden) return 3;
            if (boxes == 0 || shrunk) return 1;
            return -1;
        }

        // A channel takes the highest-priority write of this frame; equal priority: the later writer wins. Priority 0 (an
        // inactive writer's own value) only fills a channel that never had a value.
        public static bool Takes(int prio, int current, bool known) { return prio >= 0 && prio >= current && (prio > 0 || !known); }

        // record.blocks entries: "<object path or '/'-bounded suffix>|<property>[:float|:vector],..." -> [path, prop, ...], null when
        // malformed. Properties default to vector (colours read as vectors); ':float' reads GetFloat.
        public static string[] BlockEntry(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            int bar = raw.IndexOf('|');
            if (bar <= 0 || bar == raw.Length - 1) return null;
            string path = raw.Substring(0, bar).Trim().Trim('/');
            if (path.Length == 0) return null;
            var r = new List<string> { path };
            foreach (var part in raw.Substring(bar + 1).Split(','))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                int c = p.IndexOf(':');
                string name = c >= 0 ? p.Substring(0, c).Trim() : p;
                string type = c >= 0 ? p.Substring(c + 1).Trim().ToLowerInvariant() : "vector";
                if (name.Length == 0 || (type != "float" && type != "vector")) return null;
                r.Add(name + ":" + type);
            }
            return r.Count > 1 ? r.ToArray() : null;
        }

        public static string OwnerPath(string componentPath)
        {
            if (componentPath == null) return "";
            int k = componentPath.IndexOf("::", StringComparison.Ordinal);
            return k >= 0 ? componentPath.Substring(0, k) : componentPath;
        }

        public static bool PathMatches(string path, string query)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(query)) return false;
            if (path == query) return true;
            return path.Length > query.Length && path.EndsWith(query, StringComparison.Ordinal) && path[path.Length - query.Length - 1] == '/';
        }

        public static string PropName(string entry) { int c = entry.LastIndexOf(':'); return c >= 0 ? entry.Substring(0, c) : entry; }

        public static byte PropType(string entry) { return entry.EndsWith(":float", StringComparison.Ordinal) ? SrecConst.ValFloat : SrecConst.ValVec4; }

        // A record.blocks value is keyed only after it was non-zero once: a property block returns 0 for a property it does not
        // hold, and writing that 0 back in a replay would override the material's own value.
        public static bool HoldsBack(bool waitNonZero, bool known, Vector4 v) { return waitNonZero && !known && v.x == 0f && v.y == 0f && v.z == 0f && v.w == 0f; }
    }
#if !SHADER_MIRROR_ONLY

    public sealed partial class SceneRecorder
    {
        const float ShaderBudgetMs = 0.25f;
        const int ShaderRetry = 30;
        static readonly string[] HuddleDamageNames = { "dam_top", "dam_bottom", "dam_leftTop", "dam_leftBottom", "dam_rightTop", "dam_rightBottom", "dam_centerTop", "dam_centerLeft", "dam_centerRight" };

        sealed class ShaderChan
        {
            public byte scope, type;
            public string name = "", source = "";
            public int prop, ch = -1, rend = -1, retry, prio;
            public Material mat;
            public bool known, ready = true, dead, wrote, waitNonZero;
            public Vector4 last, now;
        }

        sealed class ShaderSource
        {
            public ColorBlendProbe probe;
            public Player2GrassShader grass;
            public Player2PlantShader plant;
            public int color = -1, flt = -1, vec = -1, push = -1;
            public int[] pos;
            public int boxes;
            public bool on;
        }

        readonly List<ShaderChan> shChans = new List<ShaderChan>();
        readonly List<ShaderSource> shSources = new List<ShaderSource>();
        readonly Dictionary<string, int> shIndex = new Dictionary<string, int>();
        readonly HashSet<int> shWriters = new HashSet<int>();
        readonly List<Component> shPending = new List<Component>();
        readonly Dictionary<int, int> shRend = new Dictionary<int, int>();
        readonly List<string> shWriterTypes = new List<string>();
        MaterialPropertyBlock shBlock;
        bool shaderOn, shSpecParsed;
        int shRendIndexed, shKeys, shMissingRenderers, shUnready, shSpecIndexed, shSpecChans;
        readonly List<string[]> shSpecBlocks = new List<string[]>();

        void ShaderComp(Component c)
        {
            if (!shaderOn || c == null) return;
            var p = c as ColorBlendProbe;
            bool writer = c is Shader_SetHuddlePosition || c is HuddleArmControl2 || c is HuddleLegControl || c is BoyOcclusionHandler || c is Player2GrassShader || c is Player2PlantShader
                          || (p != null && p.probeType == ColorBlendProbe.ProbeType.ShaderGlobal);
            if (writer && shWriters.Add(c.GetInstanceID())) shPending.Add(c);
        }

        void ShaderFindGlobal()
        {
            if (!shaderOn) return;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<ColorBlendProbe>()) ShaderComp(p);
            foreach (var g in UnityEngine.Object.FindObjectsOfType<Player2GrassShader>()) ShaderComp(g);
            foreach (var g in UnityEngine.Object.FindObjectsOfType<Player2PlantShader>()) ShaderComp(g);
            foreach (var h in UnityEngine.Object.FindObjectsOfType<Shader_SetHuddlePosition>()) ShaderComp(h);
        }

        void ShaderResolve()
        {
            if (!shaderOn || shPending.Count == 0) return;
            if (shBlock == null) shBlock = new MaterialPropertyBlock();
            for (; shRendIndexed < nr; shRendIndexed++) if (rs[shRendIndexed].r != null) shRend[rs[shRendIndexed].r.GetInstanceID()] = shRendIndexed;
            int before = shChans.Count;
            foreach (var c in shPending)
            {
                if (c == null) continue;
                string tn = c.GetType().Name;
                if (!shWriterTypes.Contains(tn)) shWriterTypes.Add(tn);
                var h = c as Shader_SetHuddlePosition;
                if (h != null)
                {
                    var mats = h.mat;
                    if (mats == null) continue;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null) continue;
                        MatChan(m, "_MainCharPosWorld", tn);
                        MatChan(m, "_Aspect", tn);
                        if (i == 0) foreach (var n in HuddleDamageNames) MatChan(m, n, tn);
                    }
                    continue;
                }
                var arm = c as HuddleArmControl2;
                if (arm != null) { BlockChan(arm.armRenderer, "_DamageLimb", SrecConst.ValVec4, tn); continue; }
                var leg = c as HuddleLegControl;
                if (leg != null) { BlockChan(leg.legRenderer, "_DamageLimb", SrecConst.ValVec4, tn); continue; }
                var occ = c as BoyOcclusionHandler;
                if (occ != null)
                {
                    foreach (var list in new[] { occ.spheresTop, occ.spheresMid, occ.spheresBottom })
                        if (list != null) foreach (var r in list) BlockChan(r, "_Intensity", SrecConst.ValFloat, tn);
                    continue;
                }
                var s = new ShaderSource();
                var p = c as ColorBlendProbe;
                var g = c as Player2GrassShader;
                var pl = c as Player2PlantShader;
                if (p != null)
                {
                    s.probe = p;
                    s.color = GlobalChan(p.colorPropertyName, SrecConst.ValVec4, tn);
                    s.flt = GlobalChan(p.floatPropertyName, SrecConst.ValFloat, tn);
                    s.vec = GlobalChan(p.vectorPropertyName, SrecConst.ValVec4, tn);
                }
                else if (g != null)
                {
                    s.grass = g;
                    s.pos = new[] { GlobalChan(g.shaderPosName, SrecConst.ValVec4, tn) };
                    s.push = GlobalChan(g.shaderPushName, SrecConst.ValVec4, tn);
                }
                else if (pl != null)
                {
                    s.plant = pl;
                    var names = pl.shaderPosName ?? new string[0];
                    s.pos = new int[names.Length];
                    for (int i = 0; i < names.Length; i++) s.pos[i] = GlobalChan(names[i], SrecConst.ValVec4, tn);
                    s.push = GlobalChan(pl.shaderPushName, SrecConst.ValVec4, tn);
                }
                else continue;
                shSources.Add(s);
            }
            shPending.Clear();
            if (shChans.Count > before) DevLog.Write("srec: shader state: " + (shChans.Count - before) + " channel(s) added (" + shChans.Count + " total, " + shSources.Count + " global writers)");
        }

        int AddChan(string key, ShaderChan c)
        {
            int i;
            if (shIndex.TryGetValue(key, out i)) return i;
            c.prop = Shader.PropertyToID(c.name);
            i = shChans.Count;
            shChans.Add(c);
            shIndex[key] = i;
            return i;
        }

        int GlobalChan(string name, byte type, string source)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            return AddChan("g:" + name, new ShaderChan { scope = SrecConst.ShGlobal, type = type, name = name, source = source });
        }

        void MatChan(Material m, string name, string source)
        {
            AddChan("m:" + m.GetInstanceID().ToString(IC) + ":" + name, new ShaderChan { scope = SrecConst.ShMaterial, type = SrecConst.ValVec4, name = name, mat = m, source = source });
        }

        void BlockChan(Renderer r, string name, byte type, string source)
        {
            BlockChan(r, name, type, source, false);
        }

        int BlockChan(Renderer r, string name, byte type, string source, bool waitNonZero)
        {
            if (r == null) return -1;
            int ri;
            if (!shRend.TryGetValue(r.GetInstanceID(), out ri)) { shMissingRenderers++; return -1; }
            return AddChan("b:" + ri.ToString(IC) + ":" + name, new ShaderChan { scope = SrecConst.ShBlock, type = type, name = name, rend = ri, source = source, waitNonZero = waitNonZero });
        }

        // record.blocks (opt-in): property-block values of named renderers that no known writer covers (an effect found in a
        // preview, e.g. an animated flare colour). Resolved over every recorded renderer, again after inline additions.
        void ShaderBlocks()
        {
            if (!shaderOn || spec.blocks == null || spec.blocks.Count == 0) return;
            if (!shSpecParsed)
            {
                shSpecParsed = true;
                foreach (var raw in spec.blocks)
                {
                    var e = ShaderMirror.BlockEntry(raw);
                    if (e == null) Warn("record.blocks: '" + raw + "' is not <path>|<property>[:float],...; ignored");
                    else shSpecBlocks.Add(e);
                }
            }
            if (shSpecBlocks.Count == 0) return;
            if (shBlock == null) shBlock = new MaterialPropertyBlock();
            for (; shRendIndexed < nr; shRendIndexed++) if (rs[shRendIndexed].r != null) shRend[rs[shRendIndexed].r.GetInstanceID()] = shRendIndexed;
            int before = shChans.Count;
            for (; shSpecIndexed < nr; shSpecIndexed++)
            {
                var r = rs[shSpecIndexed].r;
                if (r == null) continue;
                string path = ShaderMirror.OwnerPath(table[rs[shSpecIndexed].id].path);
                foreach (var e in shSpecBlocks)
                {
                    if (!ShaderMirror.PathMatches(path, e[0])) continue;
                    for (int i = 1; i < e.Length; i++) BlockChan(r, ShaderMirror.PropName(e[i]), ShaderMirror.PropType(e[i]), "record.blocks", true);
                }
            }
            shSpecChans += shChans.Count - before;
            if (shChans.Count > before)
            {
                if (!shWriterTypes.Contains("record.blocks")) shWriterTypes.Add("record.blocks");
                DevLog.Write("srec: record.blocks: " + (shChans.Count - before) + " property-block channel(s) added (" + shSpecChans + " total)");
            }
        }

        internal Material ShaderMaterial(int channel)
        {
            foreach (var c in shChans) if (c.ch == channel && c.scope == SrecConst.ShMaterial) return c.mat != null ? c.mat : null;
            return null;
        }

        void ShaderPut(int ch, Vector4 v, int prio)
        {
            if (ch < 0) return;
            var c = shChans[ch];
            if (!ShaderMirror.Takes(prio, c.prio, c.known)) return;
            c.prio = prio;
            c.now = v;
            c.wrote = true;
        }

        void ShaderProbe(ShaderSource s)
        {
            var p = s.probe;
            if (p == null) return;
            var bc = p.shaderGlobalBaseColor;
            float bf = p.shaderGlobalBaseFloat;
            var bv = p.shaderGlobalBaseVector4;
            if (!p.isActiveAndEnabled)
            {
                int off = s.on ? 2 : 0;
                s.on = false;
                s.boxes = 0;
                ShaderPut(s.color, new Vector4(bc.r, bc.g, bc.b, bc.a), off);
                ShaderPut(s.flt, new Vector4(bf, 0f, 0f, 0f), off);
                ShaderPut(s.vec, bv, off);
                return;
            }
            s.on = true;
            var list = p.activeBlendBoxes;
            int n = list != null ? list.size : 0;
            bool shrunk = n < s.boxes;
            s.boxes = n;
            Color oc = bc;
            float of = bf;
            Vector4 ov = bv;
            bool over = false;
            if (n > 0)
            {
                Vector3 pos = p.GetCurPos();
                for (int i = n - 1; i >= 0; i--)
                {
                    var box = list.raw[i];
                    if (box == null || !box.enabled || !box.isCurrentlyRunning) continue;
                    if (box.blendType == MaterialInstanceBlendBox.blendAxis.Constant && p.probeIsBlending) continue;
                    Color c = bc;
                    float f = bf;
                    Vector4 v = bv;
                    bool active = false;
                    box.GetBlendProperties(p, box.GetBlendValue(pos), ref c, ref f, ref v, ref active);
                    if (!active) continue;
                    oc = c; of = f; ov = v; over = true;
                }
            }
            int prio = ShaderMirror.ProbePriority(n, shrunk, over);
            if (prio < 0) return;
            ShaderPut(s.color, new Vector4(oc.r, oc.g, oc.b, oc.a), prio);
            ShaderPut(s.flt, new Vector4(of, 0f, 0f, 0f), prio);
            ShaderPut(s.vec, ov, prio);
        }

        void ShaderGrass(ShaderSource s)
        {
            var g = s.grass;
            if (g == null || !g.setProperty || !g.isActiveAndEnabled) return;
            var boy = ScriptGlobals.boy;
            var hud = ScriptGlobals.huddle;
            Vector4 push;
            if (boy != null && boy.gameObject.activeInHierarchy) push = g.boyGrassPush;
            else if (hud != null && hud.gameObject.activeInHierarchy) push = g.huddleGrassPush;
            else return;
            ShaderPut(s.push, push, 2);
            if (s.pos != null && s.pos.Length > 0) ShaderPut(s.pos[0], g.shaderVector, 2);
        }

        void ShaderPlant(ShaderSource s)
        {
            var pl = s.plant;
            if (pl == null || !pl.isActiveAndEnabled) return;
            bool live = pl.setProperty && ScriptGlobals.mainCharacterActive;
            var sv = pl.shaderVector;
            int prio = live ? 2 : 0;
            if (s.pos != null) for (int i = 0; i < s.pos.Length; i++) ShaderPut(s.pos[i], sv != null && i < sv.Length ? sv[i] : Vector4.zero, prio);
            if (live) ShaderPut(s.push, ScriptGlobals.mainCharacter is Boy ? pl.boyGrassPush : pl.huddleGrassPush, 2);
        }

        void SampleShader(int k, bool full)
        {
            int n = shChans.Count;
            if (!shaderOn || n == 0) return;
            for (int i = 0; i < n; i++)
            {
                var c = shChans[i];
                c.wrote = false;
                c.prio = -1;
                if (c.ch >= 0 || c.dead) continue;
                int target = -1;
                if (c.scope == SrecConst.ShMaterial)
                {
                    if (c.mat == null) { c.dead = true; continue; }
                    string mn = c.mat.name;
                    target = writer.StringId(string.IsNullOrEmpty(mn) ? "(unnamed material)" : mn);
                }
                else if (c.scope == SrecConst.ShBlock) target = rs[c.rend].id;
                c.ch = writer.ShaderDef(c.scope, target, c.type, c.name);
                if (c.ch < 0) c.dead = true;
            }
            for (int i = 0; i < shSources.Count; i++)
            {
                var s = shSources[i];
                if (s.probe != null) ShaderProbe(s);
                else if (s.grass != null) ShaderGrass(s);
                else if (s.plant != null) ShaderPlant(s);
            }
            int lastRend = -1;
            for (int i = 0; i < n; i++)
            {
                var c = shChans[i];
                if (c.dead || c.ch < 0) continue;
                Vector4 v;
                if (c.scope == SrecConst.ShGlobal)
                {
                    if (c.wrote) v = c.now;
                    else if (full && c.known) v = c.last;
                    else continue;
                }
                else if (c.scope == SrecConst.ShMaterial)
                {
                    var m = c.mat;
                    if (m == null) { c.dead = true; continue; }
                    if (!c.ready)
                    {
                        if (k < c.retry) continue;
                        c.ready = m.HasProperty(c.prop);
                        if (!c.ready) { c.retry = k + ShaderRetry; continue; }
                    }
                    else if (!c.known && !m.HasProperty(c.prop)) { c.ready = false; c.retry = k + ShaderRetry; shUnready++; continue; }
                    v = c.type == SrecConst.ValFloat ? new Vector4(m.GetFloat(c.prop), 0f, 0f, 0f) : m.GetVector(c.prop);
                }
                else
                {
                    ref RendItem r = ref rs[c.rend];
                    if (r.dead || r.r == null) continue;
                    if (!full && c.known && !xs[r.owner].activeH) continue;
                    if (lastRend != c.rend) { r.r.GetPropertyBlock(shBlock); lastRend = c.rend; }
                    v = c.type == SrecConst.ValFloat ? new Vector4(shBlock.GetFloat(c.prop), 0f, 0f, 0f) : shBlock.GetVector(c.prop);
                    if (ShaderMirror.HoldsBack(c.waitNonZero, c.known, v)) continue;
                }
                if (c.known && !full && ShaderMirror.Same(v, c.last)) continue;
                c.last = v;
                c.known = true;
                writer.ShaderVal(c.ch, v);
                shKeys++;
            }
        }

        Json.Obj ShaderJson()
        {
            var o = new Json.Obj();
            o["enabled"] = shaderOn;
            int ng = 0, nm = 0, nb = 0, dead = 0;
            var globals = new List<object>();
            var mats = new Json.Obj();
            var blockNames = new Json.Obj();
            var blockRends = new HashSet<int>();
            foreach (var c in shChans)
            {
                if (c.dead) dead++;
                if (c.scope == SrecConst.ShGlobal) { ng++; globals.Add(c.name + " (" + c.source + ")"); }
                else if (c.scope == SrecConst.ShMaterial)
                {
                    nm++;
                    string mn = c.mat != null ? c.mat.name : "(destroyed)";
                    var l = mats[mn] as List<object>;
                    if (l == null) { l = new List<object>(); mats[mn] = l; }
                    l.Add(c.name);
                }
                else
                {
                    nb++;
                    blockRends.Add(c.rend);
                    object cnt = blockNames[c.name];
                    blockNames[c.name] = (cnt is int ? (int)cnt : 0) + 1;
                }
            }
            o["channels"] = shChans.Count;
            var by = new Json.Obj();
            by["global"] = ng; by["material"] = nm; by["block"] = nb;
            o["byScope"] = by;
            o["globals"] = globals;
            o["materials"] = mats;
            var bj = new Json.Obj();
            bj["renderers"] = blockRends.Count; bj["values"] = nb; bj["names"] = blockNames; bj["missingRenderers"] = shMissingRenderers;
            o["propertyBlocks"] = bj;
            o["writers"] = Strs(shWriterTypes);
            o["globalWriters"] = shSources.Count;
            o["keys"] = shKeys;
            o["deadChannels"] = dead;
            o["materialPropertyNotYetSet"] = shUnready;
            o["recordBlocks"] = shSpecChans;
            int ci = Array.IndexOf(ChannelNames, "shader"), nc = ChannelNames.Length;
            double sum = 0, max = 0;
            int over = 0;
            var col = new float[Math.Max(0, metaN - 1)];
            for (int i = 1; i < metaN; i++) { float v = mChan[i * nc + ci]; col[i - 1] = v; sum += v; if (v > max) max = v; if (v > ShaderBudgetMs) over++; }
            o["budgetMs"] = (double)ShaderBudgetMs;
            o["meanMs"] = metaN > 1 ? sum / (metaN - 1) : 0.0;
            o["p99Ms"] = metaN > 1 ? Pct(col, metaN - 1, 0.99) : 0.0;
            o["maxMs"] = max;
            o["overBudgetSamples"] = over;
            o["note"] = "globals from their script writers (Unity 5.0 has no Shader.GetGlobal*), materials and property blocks read back; change-only keys in sections 17/18; cost without sample 0";
            return o;
        }
    }

    public sealed partial class SceneReplayer
    {
        sealed class ShaderItem
        {
            public SrecShaderChannel c;
            public int prop;
            public Material mat;
            public Renderer r;
            public bool hasBegin, applied, want;
            public Vector4 begin, last, now;
        }

        readonly List<ShaderItem> shGlobals = new List<ShaderItem>(), shMats = new List<ShaderItem>(), shBlocks = new List<ShaderItem>();
        MaterialPropertyBlock shBlock;
        int shUnresolved;

        void ResolveShader()
        {
            int n = tracks.ShaderCount;
            if (n == 0) return;
            shBlock = new MaterialPropertyBlock();
            for (int i = 0; i < n; i++)
            {
                var c = tracks.ShaderChannel(i);
                if (c == null || string.IsNullOrEmpty(c.name)) continue;
                var it = new ShaderItem { c = c, prop = Shader.PropertyToID(c.name) };
                if (c.scope == SrecConst.ShGlobal) { shGlobals.Add(it); continue; }
                if (c.scope == SrecConst.ShMaterial)
                {
                    Material m = sessionRec != null ? sessionRec.ShaderMaterial(i) : null;
                    if (m == null) m = MatOf(c.target);
                    if (m == null) { shUnresolved++; continue; }
                    it.mat = m;
                    if (m.HasProperty(it.prop)) { it.begin = c.type == SrecConst.ValFloat ? new Vector4(m.GetFloat(it.prop), 0f, 0f, 0f) : m.GetVector(it.prop); it.hasBegin = true; }
                    shMats.Add(it);
                    continue;
                }
                var x = c.target >= 0 && c.target < objs.Length ? objs[c.target] : null;
                var r = x != null && x.resolved && !x.camXf ? x.comp as Renderer : null;
                if (r == null) { shUnresolved++; continue; }
                it.r = r;
                r.GetPropertyBlock(shBlock);
                it.begin = c.type == SrecConst.ValFloat ? new Vector4(shBlock.GetFloat(it.prop), 0f, 0f, 0f) : shBlock.GetVector(it.prop);
                it.hasBegin = true;
                shBlocks.Add(it);
            }
            shBlocks.Sort((a, b) => a.c.target != b.c.target ? a.c.target.CompareTo(b.c.target) : a.c.id.CompareTo(b.c.id));
            Log("shader state: " + n + " channels: " + shGlobals.Count + " globals, " + shMats.Count + " material values, " + shBlocks.Count + " property-block values" + (shUnresolved > 0 ? ", " + shUnresolved + " unresolved" : ""));
        }

        void ApplyShader(bool full)
        {
            if (shGlobals.Count + shMats.Count + shBlocks.Count == 0) return;
            long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double tt = t;
            Vector4 v;
            for (int i = 0; i < shGlobals.Count; i++)
            {
                var it = shGlobals[i];
                if (!tracks.Shader(it.c.id, tt, out v)) continue;
                if (it.c.type == SrecConst.ValFloat) Shader.SetGlobalFloat(it.prop, v.x);
                else Shader.SetGlobalVector(it.prop, v);
            }
            for (int i = 0; i < shMats.Count; i++)
            {
                var it = shMats[i];
                if (it.mat == null || !tracks.Shader(it.c.id, tt, out v)) continue;
                if (it.c.type == SrecConst.ValFloat) it.mat.SetFloat(it.prop, v.x);
                else it.mat.SetVector(it.prop, v);
            }
            for (int i = 0; i < shBlocks.Count;)
            {
                var r = shBlocks[i].r;
                int j = i;
                bool write = false;
                for (; j < shBlocks.Count && (object)shBlocks[j].r == (object)r; j++)
                {
                    var it = shBlocks[j];
                    it.want = tracks.Shader(it.c.id, tt, out v);
                    if (!it.want) continue;
                    it.now = v;
                    if (full || !it.applied || !ShaderMirror.Same(v, it.last)) write = true;
                }
                if (write && r != null)
                {
                    r.GetPropertyBlock(shBlock);
                    for (int k = i; k < j; k++)
                    {
                        var it = shBlocks[k];
                        if (!it.want) continue;
                        if (it.c.type == SrecConst.ValFloat) shBlock.SetFloat(it.prop, it.now.x);
                        else shBlock.SetVector(it.prop, it.now);
                        it.last = it.now;
                        it.applied = true;
                    }
                    r.SetPropertyBlock(shBlock);
                }
                i = j;
            }
            subTicks[5] += System.Diagnostics.Stopwatch.GetTimestamp() - c0;
        }

        void RestoreShader()
        {
            foreach (var it in shMats)
            {
                it.applied = false;
                if (!it.hasBegin || it.mat == null) continue;
                if (it.c.type == SrecConst.ValFloat) it.mat.SetFloat(it.prop, it.begin.x);
                else it.mat.SetVector(it.prop, it.begin);
            }
            for (int i = 0; i < shBlocks.Count;)
            {
                var r = shBlocks[i].r;
                int j = i;
                while (j < shBlocks.Count && (object)shBlocks[j].r == (object)r) j++;
                if (r != null)
                {
                    r.GetPropertyBlock(shBlock);
                    for (int k = i; k < j; k++)
                    {
                        var it = shBlocks[k];
                        it.applied = false;
                        if (it.c.type == SrecConst.ValFloat) shBlock.SetFloat(it.prop, it.begin.x);
                        else shBlock.SetVector(it.prop, it.begin);
                    }
                    r.SetPropertyBlock(shBlock);
                }
                i = j;
            }
        }
    }
#endif
}
