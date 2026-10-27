using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Replay mode PLAY_A (docs/REPLAY_DESIGN.md 4.2, 5.2): sparse tracks built from SrecReader callbacks, cursors and
    // interpolation (Hermite / linear / slerp across gaps with pre-keys, local and world TRS, cuts, step channels).
    // Frozen public shapes (architect contract for SceneReplayer, PLAY_B, and SceneCommands, PLAY_C); PLAY_A adds private
    // members only. ISrecSink is implemented explicitly. Time arguments are record seconds (sample gameTime). Every query
    // returns false / 0 / -1 when the object has no key on that channel; before its first key a channel holds that key,
    // after its last key it holds the last one. Math is managed only (no Quaternion.Slerp/Euler/Angle or other internal
    // calls) and no game types are used, so qa/replay runs this file with SrecFormat.cs under .NET 9.
    // Rules (5.2): keys are bracketed by the last sample with time <= t. Transforms interpolate across gaps (the recorder's
    // pre-keys bound the error inside a gap by the eps; a pre-key never replaces a key already written for that sample);
    // position is non-uniform Catmull-Rom (Hermite, tangent = slope of the parabola through three keys, exact for constant
    // acceleration) when the keys at b-2..b+1 sit on consecutive samples without a cut, else linear; rotation slerp with the
    // sign of b flipped when dot < 0; scale lerp. A cut of the object in (t_a, t_b] holds a until t_b. Light, material,
    // probe, fog/post and rope channels have no pre-key bit: an unkeyed gap holds a up to the sample before b (implicit
    // pre-key), then interpolates. The recorder keys lights, materials and fog/post on change and probes and ropes past
    // posEps, so the hold is exact for the first and keeps the gap error below posEps for the others (a gap-wide lerp
    // would spread the next step over the whole gap). The recorder writes no closing key: after a track's last key the
    // value holds it, within that track's eps of the true end state.
    // Step channels (flags, parent, camActive, textures, cookies, shadow flags, shared materials) hold until the next key.
    // Probes and ropes step on the cuts of their owner (parent id of a component entry). `nearest` = no interpolation (the
    // key at SampleAt(t), as particles and step channels). Virtual: camera positions Hermite, rotations slerp, the rest
    // lerp, strings, flags and `cut` step; a cut at b holds a until t_b.
    // Shader channels (content version 2, ISrecShaderSink; public ShaderCount / ShaderChannel / Shader): change-only keys
    // like materials (unkeyed gap holds a up to the sample before b, then lerp; nearest steps); a recording without them
    // has ShaderCount 0.
    public sealed class SceneTracks : ISrecSink, ISrecShaderSink
    {
        public SceneRecording rec;
        public string interp = "hermite";                           // PlaySpec.interp: hermite | linear | nearest
        public readonly List<SrecEvent> events = new List<SrecEvent>();
        public readonly List<string> warnings = new List<string>();  // fast rotators (> 90 deg per sample) and similar

        const int ModeHermite = 0, ModeLinear = 1, ModeNearest = 2;
        const int MaxListed = 20;
        const double HalfPi = Math.PI * 0.5;

        int mode;
        int count;
        double[] times = new double[256], realTimes = new double[256];
        long[] logicSteps = new long[256];
        int[] frameCounts = new int[256];
        ushort[] fixedSteps = new ushort[256], stampFlags = new ushort[256];
        int timeCursor;
        double lastT = double.NaN;
        int lastS = -1;
        bool finished;
        readonly List<Obj> objs = new List<Obj>();
        readonly VTrack virt = new VTrack();
        readonly List<FTrack> render = new List<FTrack>();
        readonly List<SrecShaderChannel> shaderDefs = new List<SrecShaderChannel>();
        readonly List<FTrack> shader = new List<FTrack>();

        public SceneTracks(string interpMode)
        {
            interp = string.IsNullOrEmpty(interpMode) ? "hermite" : interpMode;
            mode = interp == "linear" ? ModeLinear : interp == "nearest" ? ModeNearest : ModeHermite;
            if (interp != "hermite" && interp != "linear" && interp != "nearest") warnings.Add("interp \"" + interp + "\" unknown, using hermite");
        }

        public static SceneTracks Load(string path, string interpMode, SpecReport report)
        {
            var tracks = new SceneTracks(interpMode);
            SceneRecording r;
            try { r = SrecReader.Read(path, tracks, report); }
            catch (Exception e)
            {
                if (report != null) report.Error(path, "srec read failed: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
            if (r == null)
            {
                if (report != null && report.ok) report.Error(path, "srec read returned no recording");
                return null;
            }
            if (tracks.rec == null) tracks.Attach(r);
            tracks.Finish(r);
            if (report != null) foreach (var w in tracks.warnings) report.Warn(path, w);
            return tracks;
        }

        public int SampleAt(double t)
        {
            if (count == 0) return -1;
            if (t == lastT) return lastS;
            int c = timeCursor, s;
            if (c < count && times[c] <= t && (c + 1 == count || times[c + 1] > t)) s = c;
            else if (c + 1 < count && times[c + 1] <= t && (c + 2 == count || times[c + 2] > t)) s = c + 1;
            else
            {
                int lo = 0, hi = count - 1;
                s = 0;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (times[mid] <= t) { s = mid; lo = mid + 1; } else hi = mid - 1;
                }
            }
            timeCursor = s; lastT = t; lastS = s;
            return s;
        }

        public int Keys(int id) { var o = Get(id); return o != null ? o.keys : 0; }

        public bool Xf(int id, double t, out Vector3 pos, out Quaternion rot, out Vector3 scale)
        {
            var o = Get(id);
            if (o == null || (Empty(o.pos) && Empty(o.rot) && Empty(o.scale)))
            {
                pos = default(Vector3); rot = new Quaternion(0f, 0f, 0f, 1f); scale = new Vector3(1f, 1f, 1f);
                return false;
            }
            pos = !Empty(o.pos) ? Position(o.pos, o, t) : o.src != null ? o.src.pos : default(Vector3);
            rot = !Empty(o.rot) ? Rotation(o.rot, o, t) : o.src != null ? o.src.rot : new Quaternion(0f, 0f, 0f, 1f);
            scale = !Empty(o.scale) ? Linear3(o.scale, o, t, false) : o.src != null ? o.src.scale : new Vector3(1f, 1f, 1f);
            return true;
        }

        public bool CutBetween(int id, double t0, double t1)
        {
            var o = Get(id);
            if (o == null || o.cuts == null) return false;
            foreach (int c in o.cuts) if (c < count && times[c] > t0 && times[c] <= t1) return true;
            return false;
        }

        public int ParentAt(int id, double t)
        {
            var o = Get(id);
            if (o == null) return -1;
            if (Empty(o.parent)) return o.initialParent;
            int k = o.parent.Find(SampleAt(t));
            return k < 0 ? o.initialParent : o.parent.v[k];
        }

        public byte Flags(int id, double t)
        {
            var o = Get(id);
            if (o == null || Empty(o.flags)) return 0;
            return (byte)Step(o.flags, t);
        }

        public bool CamActive(int id, double t, out bool active)
        {
            var o = Get(id);
            active = false;
            if (o == null || Empty(o.camActive)) return false;
            active = Step(o.camActive, t) != 0;
            return true;
        }

        public bool Light(int id, double t, out SrecLight v)
        {
            var o = Get(id);
            v = default(SrecLight);
            v.cookie = -1;
            if (o == null || Empty(o.light)) return false;
            int a; double u;
            Bracket(o.light, null, t, true, out a, out u);
            v = o.light.v[a];
            if (u <= 0) return true;
            var b = o.light.v[a + 1];
            v.intensity = Lerp(v.intensity, b.intensity, u);
            v.color = new Color(Lerp(v.color.r, b.color.r, u), Lerp(v.color.g, b.color.g, u), Lerp(v.color.b, b.color.b, u), Lerp(v.color.a, b.color.a, u));
            v.range = Lerp(v.range, b.range, u);
            v.spotAngle = Lerp(v.spotAngle, b.spotAngle, u);
            return true;
        }

        public int PropCount(int id, byte kind)
        {
            var o = Get(id);
            if (o == null || kind > SrecConst.MatTexture || o.names[kind] == null) return 0;
            return o.names[kind].Count;
        }

        public string PropName(int id, byte kind, int prop)
        {
            var o = Get(id);
            if (o == null || kind > SrecConst.MatTexture || o.names[kind] == null || prop < 0 || prop >= o.names[kind].Count) return null;
            return o.names[kind][prop];
        }

        public bool Prop(int id, byte kind, int prop, double t, out Vector4 value, out int texture)
        {
            value = default(Vector4);
            texture = -1;
            var o = Get(id);
            if (o == null || prop < 0) return false;
            if (kind == SrecConst.MatTexture)
            {
                if (o.tex == null || prop >= o.tex.Count || Empty(o.tex[prop])) return false;
                texture = Step(o.tex[prop], t);
                return true;
            }
            if (kind > SrecConst.MatTexture || o.mat[kind] == null || prop >= o.mat[kind].Count || Empty(o.mat[kind][prop])) return false;
            value = Linear4(o.mat[kind][prop], null, t, true);
            return true;
        }

        public bool SharedMat(int id, int slot, double t, out int material)
        {
            material = -1;
            var o = Get(id);
            if (o == null || o.shared == null || slot < 0 || slot >= o.shared.Count || Empty(o.shared[slot])) return false;
            material = Step(o.shared[slot], t);
            return true;
        }

        public bool Probe(int id, double t, out Vector3 pos)
        {
            pos = default(Vector3);
            var o = Get(id);
            if (o == null || Empty(o.probe)) return false;
            pos = Linear3(o.probe, CutOwner(o), t, true);
            return true;
        }

        public bool Render(int field, double t, out Vector4 value)
        {
            value = default(Vector4);
            if (field < 0 || field >= render.Count || Empty(render[field])) return false;
            var tr = render[field];
            int a; double u;
            Bracket(tr, null, t, true, out a, out u);
            int ia = a * 5;
            value = new Vector4(tr.v[ia], tr.v[ia + 1], tr.v[ia + 2], tr.v[ia + 3]);
            if (u <= 0) return true;
            int ib = ia + 5;
            if (tr.v[ia + 4] == SrecConst.ValInt || tr.v[ib + 4] == SrecConst.ValInt) return true;
            value = new Vector4(Lerp(tr.v[ia], tr.v[ib], u), Lerp(tr.v[ia + 1], tr.v[ib + 1], u), Lerp(tr.v[ia + 2], tr.v[ib + 2], u), Lerp(tr.v[ia + 3], tr.v[ib + 3], u));
            return true;
        }

        public int ShaderCount { get { return shaderDefs.Count; } }

        public SrecShaderChannel ShaderChannel(int channel) { return channel >= 0 && channel < shaderDefs.Count ? shaderDefs[channel] : null; }

        public bool Shader(int channel, double t, out Vector4 value)
        {
            value = default(Vector4);
            if (channel < 0 || channel >= shader.Count || Empty(shader[channel])) return false;
            value = Linear4(shader[channel], null, t, true);
            return true;
        }

        public int Rope(int id, double t, Vector3[] dst)
        {
            var o = Get(id);
            if (o == null || Empty(o.rope) || dst == null) return 0;
            var tr = o.rope;
            int a; double u;
            Bracket(tr, CutOwner(o), t, true, out a, out u);
            int na = tr.c[a];
            int w = Math.Min(na, dst.Length);
            float[] va = tr.v[a];
            if (u <= 0 || tr.c[a + 1] != na)
            {
                for (int i = 0; i < w; i++) dst[i] = new Vector3(va[i * 3], va[i * 3 + 1], va[i * 3 + 2]);
                return w;
            }
            float[] vb = tr.v[a + 1];
            for (int i = 0; i < w; i++) dst[i] = new Vector3(Lerp(va[i * 3], vb[i * 3], u), Lerp(va[i * 3 + 1], vb[i * 3 + 1], u), Lerp(va[i * 3 + 2], vb[i * 3 + 2], u));
            return w;
        }

        public bool HasParticles(int id) { var o = Get(id); return o != null && o.pSample != null && o.pSample.Count > 0; }

        public int ParticleCount(int id, int sample)
        {
            var o = Get(id);
            if (o == null || o.pSample == null) return 0;
            int i = o.pSample.BinarySearch(sample);
            return i >= 0 ? o.pCount[i] : 0;
        }

        public bool Virtual(double t, SrecVirtual dst)
        {
            if (dst == null || Empty(virt)) return false;
            int a; double u;
            Bracket(virt, null, t, false, out a, out u);
            var A = virt.v[a];
            CopyVirtual(A, dst);
            if (u <= 0) return true;
            var B = virt.v[a + 1];
            if (B.game.cut) return true;
            bool herm = mode == ModeHermite && a >= 1 && a + 2 < virt.n && virt.s[a - 1] == virt.s[a] - 1 && virt.s[a + 1] == virt.s[a] + 1
                && virt.s[a + 2] == virt.s[a] + 2 && !A.game.cut && !virt.v[a + 2].game.cut;
            SrecVirtual P = herm ? virt.v[a - 1] : null, N = herm ? virt.v[a + 2] : null;
            double h0 = 0, h = 0, h1 = 0;
            if (herm)
            {
                int sa = virt.s[a];
                h0 = times[sa] - times[sa - 1]; h = times[sa + 1] - times[sa]; h1 = times[sa + 2] - times[sa + 1];
                herm = h0 > 0 && h > 0 && h1 > 0;
            }
            var g = dst.game; GameCamState ga = A.game, gb = B.game;
            g.rendered = Pose(ga.rendered, gb.rendered, u, herm ? P.game.rendered.p : default(Vector3), herm ? N.game.rendered.p : default(Vector3), herm && P.game.rendered.ok && N.game.rendered.ok, h0, h, h1);
            g.clean = Pose(ga.clean, gb.clean, u, herm ? P.game.clean.p : default(Vector3), herm ? N.game.clean.p : default(Vector3), herm && P.game.clean.ok && N.game.clean.ok, h0, h, h1);
            g.positionCamera = herm ? Herm3(P.game.positionCamera, ga.positionCamera, gb.positionCamera, N.game.positionCamera, h0, h, h1, u) : Lerp3(ga.positionCamera, gb.positionCamera, u);
            g.positionAim = herm ? Herm3(P.game.positionAim, ga.positionAim, gb.positionAim, N.game.positionAim, h0, h, h1, u) : Lerp3(ga.positionAim, gb.positionAim, u);
            g.positionUp = Lerp3(ga.positionUp, gb.positionUp, u);
            g.velocity = Lerp3(ga.velocity, gb.velocity, u);
            g.aimDistance = Lerp(ga.aimDistance, gb.aimDistance, u);
            g.probe = Lerp3(ga.probe, gb.probe, u);
            g.extrapolation = Lerp(ga.extrapolation, gb.extrapolation, u);
            g.handOffset = Lerp3(ga.handOffset, gb.handOffset, u);
            g.handRot = SlerpQ(ga.handRot, gb.handRot, u);
            g.shakeOffset = Lerp3(ga.shakeOffset, gb.shakeOffset, u);
            g.shakeRot = SlerpQ(ga.shakeRot, gb.shakeRot, u);
            dst.camPos = herm ? Herm3(P.camPos, A.camPos, B.camPos, N.camPos, h0, h, h1, u) : Lerp3(A.camPos, B.camPos, u);
            dst.camRot = SlerpQ(A.camRot, B.camRot, u);
            dst.camFov = Lerp(A.camFov, B.camFov, u);
            if ((A.flags & SrecConst.VirtBoy) != 0 && (B.flags & SrecConst.VirtBoy) != 0)
            {
                dst.boyPos3 = Lerp3(A.boyPos3, B.boyPos3, u);
                dst.boyVel3 = Lerp3(A.boyVel3, B.boyVel3, u);
            }
            if ((A.flags & SrecConst.VirtHuddle) != 0 && (B.flags & SrecConst.VirtHuddle) != 0)
            {
                dst.huddlePos3 = Lerp3(A.huddlePos3, B.huddlePos3, u);
                dst.huddleVel3 = Lerp3(A.huddleVel3, B.huddleVel3, u);
            }
            dst.probe = Lerp3(A.probe, B.probe, u);
            return true;
        }

        public bool Alive(int id, double t)
        {
            var o = Get(id);
            if (o == null) return false;
            if (o.addedAt < 0 && o.deletedAt < 0) return true;
            int s = SampleAt(t);
            return (o.addedAt < 0 || s >= o.addedAt) && (o.deletedAt < 0 || s < o.deletedAt);
        }

        public double EventTime(string name)
        {
            if (name == null) return double.NaN;
            foreach (var e in events) if (e.kind == "event" && e.name == name) return e.time;
            return double.NaN;
        }

        // SkinnedMeshRenderer.sharedMesh swaps travel as event rows of kind "mesh" (name = renderer object id, detail = new mesh
        // key, a tab, the previous key; ReplayConst.MeshKey from R3p, plain names before): MeshAt is the key at t (the previous
        // key of the first swap before it),
        // null for an object that never swapped. Built on first use from `events`.
        sealed class MeshSteps { public string initial; public readonly List<double> times = new List<double>(); public readonly List<string> names = new List<string>(); }

        Dictionary<int, MeshSteps> meshSteps;

        internal List<int> MeshIds()
        {
            BuildMeshSteps();
            return new List<int>(meshSteps.Keys);
        }

        internal string MeshAt(int id, double t)
        {
            BuildMeshSteps();
            MeshSteps m;
            if (!meshSteps.TryGetValue(id, out m)) return null;
            string r = m.initial;
            for (int i = 0; i < m.times.Count && m.times[i] <= t; i++) r = m.names[i];
            return r;
        }

        void BuildMeshSteps()
        {
            if (meshSteps != null) return;
            meshSteps = new Dictionary<int, MeshSteps>();
            foreach (var e in events)
            {
                if (e == null || e.kind != "mesh" || double.IsNaN(e.time)) continue;
                int id;
                if (!int.TryParse(e.name, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out id)) continue;
                string d = e.detail ?? "";
                int tab = d.IndexOf('\t');
                string now = tab >= 0 ? d.Substring(0, tab) : d, was = tab >= 0 ? d.Substring(tab + 1) : "";
                MeshSteps m;
                if (!meshSteps.TryGetValue(id, out m)) { m = new MeshSteps { initial = was }; meshSteps[id] = m; }
                int at = m.times.Count;
                while (at > 0 && m.times[at - 1] > e.time) at--;
                m.times.Insert(at, e.time);
                m.names.Insert(at, now);
            }
        }

        void ISrecSink.Begin(SceneRecording r) { Attach(r); }

        void ISrecSink.Sample(SrecStamp s)
        {
            int i = s.sample;
            if (i < 0) return;
            if (i != count) warnings.Add("sample " + i + " arrived after " + count + " samples");
            Grow(i + 1);
            double prev = count > 0 ? times[count - 1] : double.NegativeInfinity;
            for (int k = count; k < i; k++) { times[k] = prev; realTimes[k] = count > 0 ? realTimes[count - 1] : s.realTime; }
            times[i] = s.gameTime < prev ? prev : s.gameTime;
            realTimes[i] = s.realTime;
            logicSteps[i] = s.logicStep;
            frameCounts[i] = s.frameCount;
            fixedSteps[i] = s.fixedSteps;
            stampFlags[i] = s.flags;
            if (s.gameTime < prev) warnings.Add("sample " + i + ": game time goes back (" + s.gameTime.ToString("0.######") + " < " + prev.ToString("0.######") + "), held");
            if (i + 1 > count) count = i + 1;
            lastT = double.NaN;
        }

        void ISrecSink.Xf(int sample, int id, byte bits, Vector3 pos, Quaternion rot, Vector3 scale, int parentId)
        {
            var o = Need(id);
            bool pre = (bits & SrecConst.XfPreKey) != 0;
            int at = pre ? sample - 1 : sample;
            if (at < 0) return;
            if ((bits & SrecConst.XfPos) != 0)
            {
                if (o.pos == null) o.pos = new FTrack(3);
                int k = Key(o.pos, at, pre);
                if (k >= 0) { k *= 3; o.pos.v[k] = pos.x; o.pos.v[k + 1] = pos.y; o.pos.v[k + 2] = pos.z; }
            }
            if ((bits & SrecConst.XfRot) != 0)
            {
                if (o.rot == null) o.rot = new FTrack(4);
                int k = Key(o.rot, at, pre);
                if (k >= 0) { k *= 4; o.rot.v[k] = rot.x; o.rot.v[k + 1] = rot.y; o.rot.v[k + 2] = rot.z; o.rot.v[k + 3] = rot.w; }
            }
            if ((bits & SrecConst.XfScale) != 0)
            {
                if (o.scale == null) o.scale = new FTrack(3);
                int k = Key(o.scale, at, pre);
                if (k >= 0) { k *= 3; o.scale.v[k] = scale.x; o.scale.v[k + 1] = scale.y; o.scale.v[k + 2] = scale.z; }
            }
            if ((bits & SrecConst.XfParent) != 0)
            {
                if (o.parent == null) o.parent = new ITrack();
                int k = o.parent.Slot(at);
                o.parent.v[k] = parentId;
            }
            if ((bits & SrecConst.XfCut) != 0)
            {
                if (o.cuts == null) o.cuts = new List<int>();
                int i = o.cuts.BinarySearch(at);
                if (i < 0) o.cuts.Insert(~i, at);
            }
            Touch(o, at);
        }

        void ISrecSink.Flags(int sample, int id, byte bits)
        {
            var o = Need(id);
            if (o.flags == null) o.flags = new ITrack();
            int k = o.flags.Slot(sample);
            o.flags.v[k] = bits;
            Touch(o, sample);
        }

        void ISrecSink.Particles(int sample, int id, int count)
        {
            var o = Need(id);
            if (o.pSample == null) { o.pSample = new List<int>(); o.pCount = new List<int>(); }
            int i = o.pSample.BinarySearch(sample);
            if (i >= 0) o.pCount[i] = count;
            else { o.pSample.Insert(~i, sample); o.pCount.Insert(~i, count); }
            Touch(o, sample);
        }

        void ISrecSink.Light(int sample, int id, byte mask, SrecLight v)
        {
            var o = Need(id);
            if (o.light == null) o.light = new LTrack();
            int before = o.light.n, k = o.light.Slot(sample);
            SrecLight m = o.light.n == before ? o.light.v[k] : k > 0 ? o.light.v[k - 1] : NoLight();
            if ((mask & SrecConst.LiEnabled) != 0) m.enabled = v.enabled;
            if ((mask & SrecConst.LiIntensity) != 0) m.intensity = v.intensity;
            if ((mask & SrecConst.LiColor) != 0) m.color = v.color;
            if ((mask & SrecConst.LiRange) != 0) m.range = v.range;
            if ((mask & SrecConst.LiSpotAngle) != 0) m.spotAngle = v.spotAngle;
            if ((mask & SrecConst.LiCookie) != 0) m.cookie = v.cookie;
            if ((mask & SrecConst.LiStaticShadows) != 0) m.useStaticShadows = v.useStaticShadows;
            if ((mask & SrecConst.LiShadowsDirty) != 0) m.staticShadowsDirty = v.staticShadowsDirty;
            o.light.v[k] = m;
            Touch(o, sample);
        }

        void ISrecSink.Material(int sample, int id, byte kind, int prop, Vector4 value, int texture)
        {
            var o = Need(id);
            if (prop < 0 || kind > SrecConst.MatTexture) return;
            if (kind == SrecConst.MatTexture)
            {
                if (o.tex == null) o.tex = new List<ITrack>();
                while (o.tex.Count <= prop) o.tex.Add(null);
                if (o.tex[prop] == null) o.tex[prop] = new ITrack();
                int k = o.tex[prop].Slot(sample);
                o.tex[prop].v[k] = texture;
            }
            else
            {
                if (o.mat[kind] == null) o.mat[kind] = new List<FTrack>();
                var list = o.mat[kind];
                while (list.Count <= prop) list.Add(null);
                if (list[prop] == null) list[prop] = new FTrack(4);
                int k = list[prop].Slot(sample) * 4;
                list[prop].v[k] = value.x; list[prop].v[k + 1] = value.y; list[prop].v[k + 2] = value.z; list[prop].v[k + 3] = value.w;
            }
            Touch(o, sample);
        }

        void ISrecSink.MatProp(int sample, int id, byte kind, string name)
        {
            var o = Need(id);
            if (kind > SrecConst.MatTexture) return;
            if (o.names[kind] == null) o.names[kind] = new List<string>();
            o.names[kind].Add(name ?? "");
        }

        void ISrecSink.Virtual(int sample, SrecVirtual v)
        {
            if (v == null || sample < 0) return;
            int k = virt.Slot(sample);
            if (virt.v[k] == null) virt.v[k] = new SrecVirtual();
            CopyVirtual(v, virt.v[k]);
        }

        void ISrecSink.ObjAdd(int sample, SrecObject o)
        {
            if (o == null) return;
            var x = Need(o.id);
            Init(x, o);
            x.addedAt = sample;
        }

        void ISrecSink.ObjDel(int sample, int id) { Need(id).deletedAt = sample; }

        void ISrecSink.Probe(int sample, int id, Vector3 pos)
        {
            var o = Need(id);
            if (o.probe == null) o.probe = new FTrack(3);
            int k = o.probe.Slot(sample) * 3;
            o.probe.v[k] = pos.x; o.probe.v[k + 1] = pos.y; o.probe.v[k + 2] = pos.z;
            Touch(o, sample);
        }

        void ISrecSink.Render(int sample, int field, byte type, Vector4 value)
        {
            if (field < 0) return;
            while (render.Count <= field) render.Add(null);
            if (render[field] == null) render[field] = new FTrack(5);
            int k = render[field].Slot(sample) * 5;
            var v = render[field].v;
            v[k] = value.x; v[k + 1] = value.y; v[k + 2] = value.z; v[k + 3] = value.w; v[k + 4] = type;
        }

        void ISrecSink.Event(int sample, string kind, string name, string detail)
        {
            var e = new SrecEvent();
            e.sample = sample;
            e.time = sample >= 0 && sample < count ? times[sample] : double.NaN;
            e.kind = kind ?? ""; e.name = name ?? ""; e.detail = detail ?? "";
            events.Add(e);
        }

        void ISrecSink.CamActive(int sample, int id, bool active)
        {
            var o = Need(id);
            if (o.camActive == null) o.camActive = new ITrack();
            int k = o.camActive.Slot(sample);
            o.camActive.v[k] = active ? 1 : 0;
            Touch(o, sample);
        }

        void ISrecSink.Rope(int sample, int id, Vector3[] vertices, int count)
        {
            var o = Need(id);
            if (o.rope == null) o.rope = new RTrack();
            int m = vertices == null ? 0 : Math.Max(0, Math.Min(count, vertices.Length));
            int k = o.rope.Slot(sample);
            var buf = new float[m * 3];
            for (int i = 0; i < m; i++) { buf[i * 3] = vertices[i].x; buf[i * 3 + 1] = vertices[i].y; buf[i * 3 + 2] = vertices[i].z; }
            o.rope.v[k] = buf;
            o.rope.c[k] = m;
            Touch(o, sample);
        }

        void ISrecSink.SharedMat(int sample, int id, int slot, int material)
        {
            var o = Need(id);
            if (slot < 0) return;
            if (o.shared == null) o.shared = new List<ITrack>();
            while (o.shared.Count <= slot) o.shared.Add(null);
            if (o.shared[slot] == null) o.shared[slot] = new ITrack();
            int k = o.shared[slot].Slot(sample);
            o.shared[slot].v[k] = material;
            Touch(o, sample);
        }

        void ISrecShaderSink.ShaderDef(int sample, SrecShaderChannel c)
        {
            if (c == null || c.id < 0) return;
            while (shaderDefs.Count <= c.id) { shaderDefs.Add(null); shader.Add(null); }
            shaderDefs[c.id] = c;
        }

        void ISrecShaderSink.ShaderVal(int sample, int channel, Vector4 value)
        {
            if (channel < 0 || sample < 0) return;
            while (shader.Count <= channel) { shader.Add(null); shaderDefs.Add(null); }
            if (shader[channel] == null) shader[channel] = new FTrack(4);
            int k = shader[channel].Slot(sample) * 4;
            var v = shader[channel].v;
            v[k] = value.x; v[k + 1] = value.y; v[k + 2] = value.z; v[k + 3] = value.w;
        }

        void ISrecSink.End(SceneRecording r) { Finish(r); }

        void Attach(SceneRecording r)
        {
            rec = r;
            if (r == null) return;
            for (int i = 0; i < r.objects.Count; i++) if (r.objects[i] != null) Init(Need(i), r.objects[i]);
        }

        void Init(Obj x, SrecObject o)
        {
            x.src = o;
            x.kind = o.kind;
            x.initialParent = o.parentId;
            x.names[SrecConst.MatFloat] = Names(o.floatProps);
            x.names[SrecConst.MatColor] = Names(o.colorProps);
            x.names[SrecConst.MatVector] = Names(o.vectorProps);
            x.names[SrecConst.MatTexture] = Names(o.textureProps);
        }

        static List<string> Names(List<string> list) { return list != null ? new List<string>(list) : new List<string>(); }

        void Finish(SceneRecording r)
        {
            if (finished) return;
            finished = true;
            if (rec == null) rec = r;
            foreach (var e in events) if (double.IsNaN(e.time) && e.sample >= 0 && e.sample < count) e.time = times[e.sample];
            if (r != null && (r.times == null || r.times.Length < count))
            {
                r.times = Trim(times); r.realTimes = Trim(realTimes);
                r.logicSteps = new long[count]; Array.Copy(logicSteps, r.logicSteps, count);
                r.frameCounts = new int[count]; Array.Copy(frameCounts, r.frameCounts, count);
                r.fixedSteps = new ushort[count]; Array.Copy(fixedSteps, r.fixedSteps, count);
                r.flags = new ushort[count]; Array.Copy(stampFlags, r.flags, count);
                if (r.sampleCount < count) r.sampleCount = count;
            }
            int fast = 0;
            for (int i = 0; i < objs.Count; i++)
            {
                var o = objs[i];
                if (o == null || Empty(o.rot)) continue;
                double worst = 0; int at = -1;
                for (int k = 1; k < o.rot.n; k++)
                {
                    if (o.rot.s[k] != o.rot.s[k - 1] + 1 || CutIn(o, o.rot.s[k - 1], o.rot.s[k])) continue;
                    double ang = RotAngle(o.rot.v, (k - 1) * 4, k * 4);
                    if (ang > worst) { worst = ang; at = o.rot.s[k]; }
                }
                if (worst <= HalfPi) continue;
                fast++;
                if (fast <= MaxListed) warnings.Add("fast rotator: id " + i + " " + (o.src != null ? o.src.path : "") + " turns " + (worst * 180.0 / Math.PI).ToString("0.#") + " deg in one sample at sample " + at + ": record at a higher rate");
            }
            if (fast > MaxListed) warnings.Add("fast rotator: " + (fast - MaxListed) + " more objects turn over 90 deg in one sample");
        }

        double[] Trim(double[] a) { var r = new double[count]; Array.Copy(a, r, count); return r; }

        void Grow(int n)
        {
            if (n <= times.Length) return;
            int cap = times.Length;
            while (cap < n) cap *= 2;
            Array.Resize(ref times, cap); Array.Resize(ref realTimes, cap); Array.Resize(ref logicSteps, cap);
            Array.Resize(ref frameCounts, cap); Array.Resize(ref fixedSteps, cap); Array.Resize(ref stampFlags, cap);
        }

        Obj Get(int id) { return id >= 0 && id < objs.Count ? objs[id] : null; }

        Obj Need(int id)
        {
            if (id < 0) throw new ArgumentOutOfRangeException("id", "negative object id " + id);
            while (objs.Count <= id) objs.Add(null);
            return objs[id] ?? (objs[id] = new Obj());
        }

        Obj CutOwner(Obj o)
        {
            if (o.kind < SrecConst.KindParticles || o.kind == SrecConst.KindSpawned) return o;
            var p = Get(o.initialParent);
            return p ?? o;
        }

        static void Touch(Obj o, int sample)
        {
            if (sample == o.lastKey || sample == o.prevKey) return;
            o.keys++;
            if (sample > o.lastKey) { o.prevKey = o.lastKey; o.lastKey = sample; }
            else if (sample > o.prevKey) o.prevKey = sample;
        }

        static int Key(FTrack tr, int sample, bool preKey)
        {
            int before = tr.n, k = tr.Slot(sample);
            return preKey && tr.n == before ? -1 : k;
        }

        static bool Empty(KeyTrack tr) { return tr == null || tr.n == 0; }

        static SrecLight NoLight() { var l = default(SrecLight); l.cookie = -1; return l; }

        int Step(ITrack tr, double t)
        {
            int k = tr.Find(SampleAt(t));
            return tr.v[k < 0 ? 0 : k];
        }

        static bool CutIn(Obj o, int lo, int hi)
        {
            if (o == null || o.cuts == null) return false;
            int i = o.cuts.BinarySearch(lo + 1);
            if (i < 0) i = ~i;
            return i < o.cuts.Count && o.cuts[i] <= hi;
        }

        void Bracket(KeyTrack tr, Obj cutsOf, double t, bool gapHold, out int a, out double u)
        {
            u = 0;
            int k = tr.Find(SampleAt(t));
            a = k < 0 ? 0 : k;
            if (k < 0 || k + 1 >= tr.n || mode == ModeNearest) return;
            int sa = tr.s[k], sb = tr.s[k + 1];
            if (sb >= count || CutIn(cutsOf, sa, sb)) return;
            double t0 = gapHold && sb > sa + 1 ? times[sb - 1] : times[sa], t1 = times[sb];
            if (t <= t0 || t1 <= t0) return;
            u = (t - t0) / (t1 - t0);
            if (u >= 1) { a = k + 1; u = 0; }
        }

        Vector3 Position(FTrack tr, Obj o, double t)
        {
            int a; double u;
            Bracket(tr, o, t, false, out a, out u);
            float[] v = tr.v;
            int ia = a * 3;
            if (u <= 0) return new Vector3(v[ia], v[ia + 1], v[ia + 2]);
            int ib = ia + 3, sa = tr.s[a];
            if (mode == ModeHermite && a >= 1 && a + 2 < tr.n && tr.s[a - 1] == sa - 1 && tr.s[a + 1] == sa + 1 && tr.s[a + 2] == sa + 2
                && sa + 2 < count && !CutIn(o, sa - 1, sa + 2))
            {
                double h0 = times[sa] - times[sa - 1], h = times[sa + 1] - times[sa], h1 = times[sa + 2] - times[sa + 1];
                if (h0 > 0 && h > 0 && h1 > 0)
                {
                    int ip = ia - 3, inx = ib + 3;
                    return new Vector3(
                        (float)Herm(v[ip], v[ia], v[ib], v[inx], h0, h, h1, u),
                        (float)Herm(v[ip + 1], v[ia + 1], v[ib + 1], v[inx + 1], h0, h, h1, u),
                        (float)Herm(v[ip + 2], v[ia + 2], v[ib + 2], v[inx + 2], h0, h, h1, u));
                }
            }
            return new Vector3(Lerp(v[ia], v[ib], u), Lerp(v[ia + 1], v[ib + 1], u), Lerp(v[ia + 2], v[ib + 2], u));
        }

        Quaternion Rotation(FTrack tr, Obj o, double t)
        {
            int a; double u;
            Bracket(tr, o, t, false, out a, out u);
            float[] v = tr.v;
            int ia = a * 4;
            if (u <= 0) return new Quaternion(v[ia], v[ia + 1], v[ia + 2], v[ia + 3]);
            int ib = ia + 4;
            return Slerp(v[ia], v[ia + 1], v[ia + 2], v[ia + 3], v[ib], v[ib + 1], v[ib + 2], v[ib + 3], u);
        }

        Vector3 Linear3(FTrack tr, Obj o, double t, bool gapHold)
        {
            int a; double u;
            Bracket(tr, o, t, gapHold, out a, out u);
            float[] v = tr.v;
            int ia = a * 3;
            if (u <= 0) return new Vector3(v[ia], v[ia + 1], v[ia + 2]);
            int ib = ia + 3;
            return new Vector3(Lerp(v[ia], v[ib], u), Lerp(v[ia + 1], v[ib + 1], u), Lerp(v[ia + 2], v[ib + 2], u));
        }

        Vector4 Linear4(FTrack tr, Obj o, double t, bool gapHold)
        {
            int a; double u;
            Bracket(tr, o, t, gapHold, out a, out u);
            float[] v = tr.v;
            int ia = a * 4;
            if (u <= 0) return new Vector4(v[ia], v[ia + 1], v[ia + 2], v[ia + 3]);
            int ib = ia + 4;
            return new Vector4(Lerp(v[ia], v[ib], u), Lerp(v[ia + 1], v[ib + 1], u), Lerp(v[ia + 2], v[ib + 2], u), Lerp(v[ia + 3], v[ib + 3], u));
        }

        static float Lerp(float a, float b, double u) { return (float)(a + (b - (double)a) * u); }

        static Vector3 Lerp3(Vector3 a, Vector3 b, double u) { return new Vector3(Lerp(a.x, b.x, u), Lerp(a.y, b.y, u), Lerp(a.z, b.z, u)); }

        static double Herm(double p0, double p1, double p2, double p3, double h0, double h, double h1, double u)
        {
            double d0 = (p1 - p0) / h0, d1 = (p2 - p1) / h, d2 = (p3 - p2) / h1;
            double m1 = (d0 * h + d1 * h0) / (h0 + h), m2 = (d1 * h1 + d2 * h) / (h + h1);
            double u2 = u * u, u3 = u2 * u;
            return (2 * u3 - 3 * u2 + 1) * p1 + (u3 - 2 * u2 + u) * h * m1 + (3 * u2 - 2 * u3) * p2 + (u3 - u2) * h * m2;
        }

        static Vector3 Herm3(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, double h0, double h, double h1, double u)
        {
            return new Vector3((float)Herm(p0.x, p1.x, p2.x, p3.x, h0, h, h1, u), (float)Herm(p0.y, p1.y, p2.y, p3.y, h0, h, h1, u), (float)Herm(p0.z, p1.z, p2.z, p3.z, h0, h, h1, u));
        }

        static CamPose Pose(CamPose a, CamPose b, double u, Vector3 prev, Vector3 next, bool herm, double h0, double h, double h1)
        {
            if (!a.ok || !b.ok) return a;
            var r = a;
            r.p = herm ? Herm3(prev, a.p, b.p, next, h0, h, h1, u) : Lerp3(a.p, b.p, u);
            r.r = SlerpQ(a.r, b.r, u);
            r.fov = Lerp(a.fov, b.fov, u);
            r.aim = Lerp3(a.aim, b.aim, u);
            r.near = Lerp(a.near, b.near, u);
            return r;
        }

        static Quaternion SlerpQ(Quaternion a, Quaternion b, double u)
        {
            double na = (double)a.x * a.x + (double)a.y * a.y + (double)a.z * a.z + (double)a.w * a.w;
            double nb = (double)b.x * b.x + (double)b.y * b.y + (double)b.z * b.z + (double)b.w * b.w;
            if (na < 0.25 || nb < 0.25) return a;
            return Slerp(a.x, a.y, a.z, a.w, b.x, b.y, b.z, b.w, u);
        }

        static Quaternion Slerp(double ax, double ay, double az, double aw, double bx, double by, double bz, double bw, double u)
        {
            if (ax * bx + ay * by + az * bz + aw * bw < 0) { bx = -bx; by = -by; bz = -bz; bw = -bw; }
            double dx = ax - bx, dy = ay - by, dz = az - bz, dw = aw - bw, sx = ax + bx, sy = ay + by, sz = az + bz, sw = aw + bw;
            double phi = 2.0 * Math.Atan2(Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw), Math.Sqrt(sx * sx + sy * sy + sz * sz + sw * sw));
            double wa = 1 - u, wb = u;
            if (phi > 1e-12)
            {
                double sn = Math.Sin(phi);
                wa = Math.Sin((1 - u) * phi) / sn;
                wb = Math.Sin(u * phi) / sn;
            }
            double x = wa * ax + wb * bx, y = wa * ay + wb * by, z = wa * az + wb * bz, w = wa * aw + wb * bw;
            double len = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (len > 0) { x /= len; y /= len; z /= len; w /= len; }
            return new Quaternion((float)x, (float)y, (float)z, (float)w);
        }

        static double RotAngle(float[] v, int i, int j)
        {
            double s = (double)v[i] * v[j] + (double)v[i + 1] * v[j + 1] + (double)v[i + 2] * v[j + 2] + (double)v[i + 3] * v[j + 3] < 0 ? -1 : 1;
            double dx = v[i] - s * v[j], dy = v[i + 1] - s * v[j + 1], dz = v[i + 2] - s * v[j + 2], dw = v[i + 3] - s * v[j + 3];
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw) / 2;
            return 4 * Math.Asin(d > 1 ? 1 : d);
        }

        static void CopyVirtual(SrecVirtual s, SrecVirtual d)
        {
            GameCamState a = s.game, b = d.game;
            b.ok = a.ok; b.step = a.step; b.rendered = a.rendered; b.clean = a.clean;
            b.positionCamera = a.positionCamera; b.positionAim = a.positionAim; b.positionUp = a.positionUp; b.velocity = a.velocity;
            b.aimDistance = a.aimDistance; b.box = a.box; b.probe = a.probe; b.extrapolation = a.extrapolation;
            b.handOffset = a.handOffset; b.handRot = a.handRot; b.shakeOffset = a.shakeOffset; b.shakeRot = a.shakeRot; b.cut = a.cut;
            d.camPos = s.camPos; d.camRot = s.camRot; d.camFov = s.camFov;
            d.boyPos3 = s.boyPos3; d.boyVel3 = s.boyVel3; d.boyState = s.boyState; d.boyVisual = s.boyVisual;
            d.huddlePos3 = s.huddlePos3; d.huddleVel3 = s.huddleVel3; d.huddleState = s.huddleState;
            d.probe = s.probe; d.flags = s.flags;
        }

        sealed class Obj
        {
            public SrecObject src;
            public byte kind;
            public int initialParent = -1, addedAt = -1, deletedAt = -1;
            public int keys, lastKey = -1, prevKey = -1;
            public FTrack pos, rot, scale, probe;
            public ITrack flags, parent, camActive;
            public List<int> cuts;
            public LTrack light;
            public RTrack rope;
            public List<ITrack> shared, tex;
            public readonly List<FTrack>[] mat = new List<FTrack>[3];
            public readonly List<string>[] names = new List<string>[4];
            public List<int> pSample, pCount;
        }

        abstract class KeyTrack
        {
            public int[] s = new int[4];
            public int n, cur;

            protected abstract void Resize(int cap);

            protected abstract void Shift(int i);

            public int Slot(int sample)
            {
                int i = n;
                while (i > 0 && s[i - 1] > sample) i--;
                if (i > 0 && s[i - 1] == sample) return i - 1;
                if (n == s.Length) { Array.Resize(ref s, n * 2); Resize(n * 2); }
                if (i < n) { Array.Copy(s, i, s, i + 1, n - i); Shift(i); }
                s[i] = sample;
                n++;
                return i;
            }

            public int Find(int sample)
            {
                if (n == 0) return -1;
                int c = cur;
                if (c < n && s[c] <= sample)
                {
                    if (c + 1 == n || s[c + 1] > sample) return c;
                    if (c + 2 == n || s[c + 2] > sample) { cur = c + 1; return c + 1; }
                }
                if (sample < s[0]) return -1;
                int lo = 0, hi = n - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) >> 1;
                    if (s[mid] <= sample) lo = mid; else hi = mid - 1;
                }
                cur = lo;
                return lo;
            }
        }

        sealed class FTrack : KeyTrack
        {
            public readonly int stride;
            public float[] v;

            public FTrack(int stride) { this.stride = stride; v = new float[4 * stride]; }

            protected override void Resize(int cap) { Array.Resize(ref v, cap * stride); }

            protected override void Shift(int i) { Array.Copy(v, i * stride, v, (i + 1) * stride, (n - i) * stride); }
        }

        sealed class ITrack : KeyTrack
        {
            public int[] v = new int[4];

            protected override void Resize(int cap) { Array.Resize(ref v, cap); }

            protected override void Shift(int i) { Array.Copy(v, i, v, i + 1, n - i); }
        }

        sealed class LTrack : KeyTrack
        {
            public SrecLight[] v = new SrecLight[4];

            protected override void Resize(int cap) { Array.Resize(ref v, cap); }

            protected override void Shift(int i) { Array.Copy(v, i, v, i + 1, n - i); }
        }

        sealed class RTrack : KeyTrack
        {
            public float[][] v = new float[4][];
            public int[] c = new int[4];

            protected override void Resize(int cap) { Array.Resize(ref v, cap); Array.Resize(ref c, cap); }

            protected override void Shift(int i) { Array.Copy(v, i, v, i + 1, n - i); Array.Copy(c, i, c, i + 1, n - i); }
        }

        sealed class VTrack : KeyTrack
        {
            public SrecVirtual[] v = new SrecVirtual[4];

            protected override void Resize(int cap) { Array.Resize(ref v, cap); }

            protected override void Shift(int i) { Array.Copy(v, i, v, i + 1, n - i); v[i] = null; }
        }
    }
}
