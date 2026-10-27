using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace InsideDev
{
    // Replay mode REC_B (docs/REPLAY_DESIGN.md 4.2): scene.srec writer (pooled blocks, writer thread, bounded queue
    // of 64, per-block CRC) and reader (crash-prefix recovery, footer cross-check, particle frames on demand).
    // Contract (frozen public shapes; REC_B adds private/internal members only):
    // - REC_A (SceneRecorder) writes through SrecWriter: constructor (preamble + SRHD), Objects once (OBJT), then per
    //   sample BeginSample, the typed entries, EndSample; Close at the end (INDX + SEND, joins the thread). Entries of one
    //   section are added in ascending id order (SharedMat: ascending id, then slot; a pre-key precedes the key of the
    //   same id); the writer orders sections, writes counts and encodes every value as below. State, texture and material
    //   names become ids through StringId (null or "" = -1, none). Contract violations (entry outside a sample, id not in
    //   the table, descending id, ObjAdd id not the next dense id) drop that entry and set `error`; the file stays valid.
    // - PLAY_A (SceneTracks) implements ISrecSink; SrecReader.Read(path, sink, report) calls it for every non-particle entry
    //   in file order. Particle bytes stay in the file: ReadParticles on demand.
    // - Data types use UnityEngine value types only and no game types, so qa/srec and qa/replay can run this file under
    //   .NET 9 (UnityEngine math with internal calls, e.g. Quaternion.Slerp/Euler, is not available there).
    // - Ids are dense object table indices; parentId -1 = parent outside the set (the file stores id + 1, 0 = outside).
    // Encoding inside 4.2 (shared with tools/srec_tool.py; little endian, varint = unsigned LEB128 of the uint value):
    // - Block = u32 tag, u32 length, payload, u32 CRC-32 (IEEE, as zlib.crc32) of the payload. The reader stops at the
    //   first incomplete or bad block and keeps the prefix (SceneRecording.truncated); unknown tags and sections are skipped.
    // - String-table references (box and state names, cookie, texture, material) are varint (id + 1), 0 = none.
    // - Section 16 Strings (REC_B addition): varint count; varint id, string - every string registered since the previous
    //   sample, so a truncated file keeps its names; INDX repeats the whole table.
    // - Section order in a sample: 16, 7 ObjAdd, 12 MatProp, 17 ShaderDef, 1, 2, 3, 4, 5, 6, 9, 10, 11, 13, 14, 15, 18
    //   ShaderVal, 8 ObjDel (new objects, property names and shader channels precede their entries, deletions follow them);
    //   each section at most once; 6 Virtual has no count.
    // - Section 17 ShaderDef (content version 2): varint count; varint channel (dense, = channels defined before), u8 scope
    //   (SrecConst.Sh*), varint target + 1 (material name string id / kind 9 object id / 0 for globals), u8 type (ValFloat,
    //   ValVec4), string property name. Section 18 ShaderVal: varint count; varint channelDelta (non-decreasing within the
    //   sample), f32 (ValFloat) or 4 f32. Shader values are not object keys (INDX unchanged); sinks get them through the
    //   optional ISrecShaderSink. Version 1 readers skip both sections (unknown ids).
    // - Sections 1, 2, 4, 9, 13, 15: idDelta = id - previous id (previous = 0 at the section start, ids non-decreasing).
    // - Xf: pos, rot, scale and parent are stored only when their bit is set. Lights: values in mask bit order, enabled u8.
    //   Materials: float f32 (value.x), color and vector 4 f32, texture varint (id + 1). Render: ValFloat f32, ValVec4 4 f32,
    //   ValInt i32. Rope: varint id, varint n, n x 3 f32. Stamp realTime is stored as given (REC_A: realtimeSinceStartupDbl).
    // - OBJT payload = varint count + records (4.2 table). INDX = varint samples + u64 block offsets; varint objects +
    //   varint firstSample, lastSample, keyCount per object (0, 0, 0 without keys); varint systems + per system varint id,
    //   varint frames, frames x (varint sample, u64 offset of the entry's varint id); varint strings + strings.
    //   SEND = u64 file length, u32 sample count.
    // - Keys (INDX, SrecObject.keyCount) = entries of sections 1, 2, 3, 4, 5, 9, 13, 14, 15 for that id.
    // - Sinks get dense sample indices (0..n-1). Xf passes zero / identity / one for channels absent from bits and the
    //   object's current parent id; Light fills only the fields in mask (cookie -1 otherwise); Material passes texture -1
    //   for non-texture kinds; SharedMat material and Light cookie are string ids, -1 = none. SrecObject prop lists stay
    //   as in OBJT/OBJA: MatProp additions reach sinks only through MatProp.
    // Internal helpers for PLAY: SrecReader.ReadParticles(rec, obj, sample, ParticleSystem.Particle[]) (same result codes),
    // SceneRecording.Release() closes the particle file handle, SrecWriter.queued = blocks waiting for the thread.
    public sealed class SrecObject
    {
        public int id, parentId = -1, depth, instanceId;
        public byte kind, flags;
        public string path = "", owner = "";
        public List<int> ordinals = new List<int>(), siblings = new List<int>();
        public uint comps;
        public List<string> types = new List<string>();
        public Vector3 pos, scale = Vector3.one;
        public Quaternion rot = Quaternion.identity;
        public bool activeSelf;
        public int maxParticles; public byte simulationSpace; public uint randomSeed;
        public byte lightType, lightShadows;
        public List<string> floatProps = new List<string>(), colorProps = new List<string>(), vectorProps = new List<string>(), textureProps = new List<string>();
        public int ropeVertices; public byte ropeDrawMode; public int ropeLineDivisions;
        public int materialSlots;
        public float extent;
        public int firstSample = -1, lastSample = -1, keyCount;     // reader: counted while reading (cross-checked against INDX)
        public int addedAt = -1, deletedAt = -1;                    // reader: sample of the OBJA / OBJD record, -1 = OBJT / never deleted
    }

    public struct SrecStamp
    {
        public int sample;
        public double gameTime, realTime;
        public long logicStep;
        public int frameCount;
        public ushort fixedSteps, flags;                            // flags: SrecConst.Smpl*
    }

    public struct SrecLight
    {
        public bool enabled;
        public float intensity;
        public Color color;
        public float range, spotAngle;
        public int cookie;                                          // string table id of the texture name, -1 = none
        public bool useStaticShadows, staticShadowsDirty;
    }

    public sealed class SrecVirtual
    {
        public readonly GameCamState game = new GameCamState();
        public Vector3 camPos;
        public Quaternion camRot = Quaternion.identity;
        public float camFov;
        public Vector3 boyPos3, boyVel3;
        public string boyState = "", boyVisual = "";
        public Vector3 huddlePos3, huddleVel3;
        public string huddleState = "";
        public Vector3 probe;
        public byte flags;                                          // SrecConst.Virt*
    }

    public sealed class SrecEvent
    {
        public int sample;
        public double time;
        public string kind = "", name = "", detail = "";
    }

    public sealed class SrecShaderChannel
    {
        public int id, target = -1;                                 // target: material name string id (ShMaterial), kind 9 object id (ShBlock), -1 (ShGlobal)
        public byte scope, type;                                    // SrecConst.Sh*; SrecConst.ValFloat or ValVec4
        public string name = "";                                    // shader property name
        public int definedAt = -1, firstSample = -1, lastSample = -1, keys;   // reader: sample of the def, value statistics
    }

    // Optional second sink of SrecReader (content version 2): called when the ISrecSink also implements it. A channel's
    // ShaderDef precedes its first ShaderVal; c stays valid (SceneRecording.shaderChannels). value.x for ValFloat channels.
    public interface ISrecShaderSink
    {
        void ShaderDef(int sample, SrecShaderChannel c);
        void ShaderVal(int sample, int channel, Vector4 value);
    }

    public interface ISrecSink
    {
        void Begin(SceneRecording rec);                             // header, sample-free object table (rec.objects) read
        void Sample(SrecStamp s);                                   // before the entries of that sample
        void Xf(int sample, int id, byte bits, Vector3 pos, Quaternion rot, Vector3 scale, int parentId);   // bits SrecConst.Xf*; PreKey = values of sample - 1
        void Flags(int sample, int id, byte bits);                  // the full SrecConst.Fl* state of that object after the change
        void Particles(int sample, int id, int count);
        void Light(int sample, int id, byte mask, SrecLight v);     // mask SrecConst.Li*: valid fields
        void Material(int sample, int id, byte kind, int prop, Vector4 value, int texture);   // kind SrecConst.Mat*; prop indexes that kind's list (OBJT names + MatProp)
        void MatProp(int sample, int id, byte kind, string name);
        void Virtual(int sample, SrecVirtual v);                    // v is reused by the reader: copy what you keep
        void ObjAdd(int sample, SrecObject o);
        void ObjDel(int sample, int id);
        void Probe(int sample, int id, Vector3 pos);
        void Render(int sample, int field, byte type, Vector4 value);   // field indexes header fogFields then postFields; type SrecConst.Val*
        void Event(int sample, string kind, string name, string detail);
        void CamActive(int sample, int id, bool active);
        void Rope(int sample, int id, Vector3[] vertices, int count);   // array reused by the reader
        void SharedMat(int sample, int id, int slot, int material);    // material = string table id
        void End(SceneRecording rec);                               // after the last readable sample; rec.strings and rec.truncated final
    }

    internal sealed class SrecBuf
    {
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        public byte[] data;
        public int length;

        public SrecBuf(int capacity) { data = new byte[Math.Max(64, capacity)]; }

        public void Ensure(int n)
        {
            if (length + n <= data.Length) return;
            int c = data.Length;
            while (c < length + n) c *= 2;
            var d = new byte[c];
            Buffer.BlockCopy(data, 0, d, 0, length);
            data = d;
        }

        public void U8(int v) { Ensure(1); data[length++] = (byte)v; }

        public void U16(int v) { Ensure(2); data[length] = (byte)v; data[length + 1] = (byte)(v >> 8); length += 2; }

        public void U32(uint v) { Ensure(4); Put32(data, length, v); length += 4; }

        public void I32(int v) { U32((uint)v); }

        public void U64(ulong v) { Ensure(8); Put32(data, length, (uint)v); Put32(data, length + 4, (uint)(v >> 32)); length += 8; }

        public void I64(long v) { U64((ulong)v); }

        public unsafe void F32(float v) { U32(*(uint*)&v); }

        public unsafe void F64(double v) { U64(*(ulong*)&v); }

        public void Var(uint v)
        {
            Ensure(5);
            while (v >= 0x80) { data[length++] = (byte)(v | 0x80); v >>= 7; }
            data[length++] = (byte)v;
        }

        public void Int(int v) { Var((uint)v); }

        public void Ref(int id) { Var(id < 0 ? 0u : (uint)id + 1u); }

        public void Str(string s)
        {
            if (string.IsNullOrEmpty(s)) { Var(0); return; }
            int n = Utf8.GetByteCount(s);
            Var((uint)n);
            Ensure(n);
            Utf8.GetBytes(s, 0, s.Length, data, length);
            length += n;
        }

        public void V3(Vector3 v) { F32(v.x); F32(v.y); F32(v.z); }

        public void Q(Quaternion q) { F32(q.x); F32(q.y); F32(q.z); F32(q.w); }

        public void V4(Vector4 v) { F32(v.x); F32(v.y); F32(v.z); F32(v.w); }

        public void Col(Color c) { F32(c.r); F32(c.g); F32(c.b); F32(c.a); }

        public void Append(SrecBuf b) { Ensure(b.length); Buffer.BlockCopy(b.data, 0, data, length, b.length); length += b.length; }

        public void Ints(List<int> list)
        {
            int n = list == null ? 0 : list.Count;
            Var((uint)n);
            for (int i = 0; i < n; i++) Int(list[i]);
        }

        public void Strs(List<string> list)
        {
            int n = list == null ? 0 : list.Count;
            Var((uint)n);
            for (int i = 0; i < n; i++) Str(list[i]);
        }

        public static void Put32(byte[] d, int at, uint v) { d[at] = (byte)v; d[at + 1] = (byte)(v >> 8); d[at + 2] = (byte)(v >> 16); d[at + 3] = (byte)(v >> 24); }

        public static uint Get32(byte[] d, int at) { return (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24); }

        public static int VarLen(uint v) { int n = 1; while (v >= 0x80) { v >>= 7; n++; } return n; }
    }

    internal sealed class SrecIn
    {
        public byte[] data;
        public int pos, end;

        public void Need(int n) { if (n < 0 || n > end - pos) throw new InvalidDataException("payload ends early at byte " + pos + " (" + n + " more needed)"); }

        public byte U8() { Need(1); return data[pos++]; }

        public ushort U16() { Need(2); var v = (ushort)(data[pos] | data[pos + 1] << 8); pos += 2; return v; }

        public uint U32() { Need(4); uint v = SrecBuf.Get32(data, pos); pos += 4; return v; }

        public int I32() { return (int)U32(); }

        public ulong U64() { uint lo = U32(); uint hi = U32(); return lo | (ulong)hi << 32; }

        public long I64() { return (long)U64(); }

        public unsafe float F32() { uint v = U32(); return *(float*)&v; }

        public unsafe double F64() { ulong v = U64(); return *(double*)&v; }

        public uint Var()
        {
            uint v = 0;
            for (int shift = 0; ; shift += 7)
            {
                byte b = U8();
                if (shift == 28 && b > 0x0F) throw new InvalidDataException("varint longer than 32 bits at byte " + pos);
                v |= (uint)(b & 0x7F) << shift;
                if (b < 0x80) return v;
            }
        }

        public int Int() { return (int)Var(); }

        public int Ref() { return (int)Var() - 1; }

        public int Count(int minBytes)
        {
            uint v = Var();
            if (minBytes > 0 && v > (uint)(end - pos) / (uint)minBytes) throw new InvalidDataException("count " + v + " exceeds the payload at byte " + pos);
            return (int)v;
        }

        public string Str()
        {
            int n = Count(1);
            Need(n);
            string s = n == 0 ? "" : SrecBuf.Utf8.GetString(data, pos, n);
            pos += n;
            return s;
        }

        public Vector3 V3() { float x = F32(), y = F32(), z = F32(); return new Vector3(x, y, z); }

        public Quaternion Q() { float x = F32(), y = F32(), z = F32(), w = F32(); return new Quaternion(x, y, z, w); }

        public Vector4 V4() { float x = F32(), y = F32(), z = F32(), w = F32(); return new Vector4(x, y, z, w); }

        public Color Col() { float r = F32(), g = F32(), b = F32(), a = F32(); return new Color(r, g, b, a); }

        public void Ints(List<int> dst) { dst.Clear(); int n = Count(1); for (int i = 0; i < n; i++) dst.Add(Int()); }

        public void Strs(List<string> dst) { dst.Clear(); int n = Count(1); for (int i = 0; i < n; i++) dst.Add(Str()); }
    }

    internal static class SrecCrc
    {
        static readonly uint[] Table = Make();

        static uint[] Make()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        public static uint Compute(byte[] d, int off, int n)
        {
            uint c = 0xFFFFFFFFu;
            var t = Table;
            for (int i = off, e = off + n; i < e; i++) c = t[(c ^ d[i]) & 0xFF] ^ (c >> 8);
            return ~c;
        }
    }

    // Shared OBJT/OBJA and SRHD codecs (4.2 tables, RecordHeader in declaration order).
    internal static class SrecCodec
    {
        public const byte SecStrings = 16;
        public const int StampBytes = 36;
        public const int SectionSlots = 19;
        public static readonly byte[] FileOrder =
        {
            SecStrings, SrecConst.SecObjAdd, SrecConst.SecMatProp, SrecConst.SecShaderDef, SrecConst.SecXf, SrecConst.SecFlags,
            SrecConst.SecParticles, SrecConst.SecLights, SrecConst.SecMaterials, SrecConst.SecVirtual, SrecConst.SecProbes,
            SrecConst.SecRender, SrecConst.SecEvents, SrecConst.SecCamActive, SrecConst.SecRope, SrecConst.SecSharedMat,
            SrecConst.SecShaderVal, SrecConst.SecObjDel
        };

        public static string TagName(uint tag)
        {
            var c = new char[4];
            for (int i = 0; i < 4; i++)
            {
                int b = (int)(tag >> (8 * i)) & 0xFF;
                c[i] = b >= 32 && b < 127 ? (char)b : '?';
            }
            return new string(c);
        }

        public static void WriteHeader(SrecBuf b, RecordHeader h)
        {
            b.I32(h.version); b.Str(h.created); b.Str(h.spec); b.Str(h.level); b.Str(h.recorderVersion); b.Str(h.clock);
            b.I32(h.captureFramerate); b.I32(h.subscene); b.I32(h.savepoint); b.I32(h.streamVersion); b.I32(h.screenW); b.I32(h.screenH);
            b.F64(h.fixedDeltaTime); b.F64(h.t0); b.F64(h.t0Real); b.I64(h.stepAtT0); b.F32(h.gameFov);
            b.Strs(h.subscenes); b.Strs(h.keptCullers); b.Strs(h.forcedCullers);
            b.Strs(h.fogFields); b.Strs(h.postFields);
            b.Strs(h.cameraDriven);
            b.F32(h.posEps); b.F32(h.rotEps); b.F32(h.cut);
            b.I32(h.vSyncCount); b.I32(h.targetFrameRate); b.I32(h.refreshRate);
        }

        public static RecordHeader ReadHeader(SrecIn r)
        {
            var h = new RecordHeader();
            h.version = r.I32(); h.created = r.Str(); h.spec = r.Str(); h.level = r.Str(); h.recorderVersion = r.Str(); h.clock = r.Str();
            h.captureFramerate = r.I32(); h.subscene = r.I32(); h.savepoint = r.I32(); h.streamVersion = r.I32(); h.screenW = r.I32(); h.screenH = r.I32();
            h.fixedDeltaTime = r.F64(); h.t0 = r.F64(); h.t0Real = r.F64(); h.stepAtT0 = r.I64(); h.gameFov = r.F32();
            r.Strs(h.subscenes); r.Strs(h.keptCullers); r.Strs(h.forcedCullers);
            r.Strs(h.fogFields); r.Strs(h.postFields);
            r.Strs(h.cameraDriven);
            h.posEps = r.F32(); h.rotEps = r.F32(); h.cut = r.F32();
            h.vSyncCount = r.I32(); h.targetFrameRate = r.I32(); h.refreshRate = r.I32();
            return h;
        }

        public static void WriteObject(SrecBuf b, SrecObject o, int id)
        {
            b.Int(id); b.U8(o.kind); b.U8(o.flags); b.I32(o.instanceId); b.Ref(o.parentId); b.U16(Math.Max(0, Math.Min(o.depth, 65535)));
            b.Str(o.path); b.Str(o.owner);
            b.Ints(o.ordinals); b.Ints(o.siblings);
            b.U32(o.comps);
            b.Strs(o.types);
            b.V3(o.pos); b.Q(o.rot); b.V3(o.scale); b.U8(o.activeSelf ? 1 : 0);
            switch (o.kind)
            {
                case SrecConst.KindParticles: b.I32(o.maxParticles); b.U8(o.simulationSpace); b.U32(o.randomSeed); break;
                case SrecConst.KindLight: b.U8(o.lightType); b.U8(o.lightShadows); break;
                case SrecConst.KindMaterial:
                    b.Int(Count(o.floatProps)); b.Int(Count(o.colorProps)); b.Int(Count(o.vectorProps)); b.Int(Count(o.textureProps));
                    Names(b, o.floatProps); Names(b, o.colorProps); Names(b, o.vectorProps); Names(b, o.textureProps);
                    break;
                case SrecConst.KindRope: b.Int(o.ropeVertices); b.U8(o.ropeDrawMode); b.Int(o.ropeLineDivisions); break;
                case SrecConst.KindRenderer: b.Int(o.materialSlots); break;
            }
            b.F32(o.extent);
        }

        public static SrecObject ReadObject(SrecIn r)
        {
            var o = new SrecObject();
            o.id = r.Int(); o.kind = r.U8(); o.flags = r.U8(); o.instanceId = r.I32(); o.parentId = r.Ref(); o.depth = r.U16();
            o.path = r.Str(); o.owner = r.Str();
            r.Ints(o.ordinals); r.Ints(o.siblings);
            o.comps = r.U32();
            r.Strs(o.types);
            o.pos = r.V3(); o.rot = r.Q(); o.scale = r.V3(); o.activeSelf = r.U8() != 0;
            switch (o.kind)
            {
                case SrecConst.KindParticles: o.maxParticles = r.I32(); o.simulationSpace = r.U8(); o.randomSeed = r.U32(); break;
                case SrecConst.KindLight: o.lightType = r.U8(); o.lightShadows = r.U8(); break;
                case SrecConst.KindMaterial:
                    int nf = r.Count(1), nc = r.Count(1), nv = r.Count(1), nt = r.Count(1);
                    ReadNames(r, o.floatProps, nf); ReadNames(r, o.colorProps, nc); ReadNames(r, o.vectorProps, nv); ReadNames(r, o.textureProps, nt);
                    break;
                case SrecConst.KindRope: o.ropeVertices = r.Int(); o.ropeDrawMode = r.U8(); o.ropeLineDivisions = r.Int(); break;
                case SrecConst.KindRenderer: o.materialSlots = r.Int(); break;
            }
            o.extent = r.F32();
            return o;
        }

        static int Count(List<string> l) { return l == null ? 0 : l.Count; }

        static void Names(SrecBuf b, List<string> l) { if (l != null) for (int i = 0; i < l.Count; i++) b.Str(l[i]); }

        static void ReadNames(SrecIn r, List<string> dst, int n) { dst.Clear(); for (int i = 0; i < n; i++) dst.Add(r.Str()); }

        public static void WritePose(SrecBuf b, CamPose p) { b.V3(p.p); b.Q(p.r); b.F32(p.fov); b.V3(p.aim); b.F32(p.near); }

        public static CamPose ReadPose(SrecIn r, bool ok)
        {
            var p = new CamPose();
            p.p = r.V3(); p.r = r.Q(); p.fov = r.F32(); p.aim = r.V3(); p.near = r.F32(); p.ok = ok;
            return p;
        }

        public static unsafe int ParticleSize() { return sizeof(ParticleSystem.Particle); }
    }

    public sealed class SrecWriter
    {
        public long bytes;
        public int samples, stalls;                                 // stalls: EndSample calls that waited for a full queue
        public string error;                                        // first writer thread failure or contract violation, null = ok

        const int QueueLimit = 64, BlockBudget = QueueLimit + 2;
        static readonly bool ParticleLayoutOk = SrecCodec.ParticleSize() == SrecConst.ParticleBytes;

        readonly FileStream file;
        readonly Thread thread;
        readonly object gate = new object();
        readonly Queue<SrecBuf> queue = new Queue<SrecBuf>(QueueLimit + 4);
        readonly Stack<SrecBuf> pool = new Stack<SrecBuf>(QueueLimit + 8);
        readonly SrecBuf[] sec = new SrecBuf[SrecCodec.SectionSlots];
        readonly int[] entries = new int[SrecCodec.SectionSlots], prev = new int[SrecCodec.SectionSlots];
        readonly Dictionary<string, int> stringIds = new Dictionary<string, int>();
        readonly List<string> strings = new List<string>();
        readonly List<byte> shaderTypes = new List<byte>();
        int stringsSent, objectCount, sample, frameFrom, frames;
        int[] first = new int[256], last = new int[256], keys = new int[256];
        long[] offsets = new long[4096];
        int[] frameId = new int[1 << 14], frameSample = new int[1 << 14];
        long[] frameOffset = new long[1 << 14];
        bool objectsDone, inSample, hasVirtual, closed, closing;
        volatile bool failed;
        SrecStamp stamp;

        public SrecWriter(string path, RecordHeader h)
        {
            for (int i = 1; i < sec.Length; i++) sec[i] = new SrecBuf(i == SrecConst.SecParticles ? 1 << 16 : i == SrecConst.SecXf ? 1 << 15 : 1 << 10);
            file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
            var pre = new byte[8];
            SrecBuf.Put32(pre, 0, SrecConst.Magic);
            pre[4] = (byte)SrecConst.Version; pre[5] = (byte)(SrecConst.Version >> 8);
            file.Write(pre, 0, 8);
            bytes = 8;
            for (int i = 0; i < BlockBudget; i++) pool.Push(new SrecBuf(1 << 16));
            blocks = BlockBudget;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "srec writer";
            thread.Start();
            var head = new SrecBuf(1024);
            SrecCodec.WriteHeader(head, h ?? new RecordHeader());
            Frame(SrecConst.TagHead, head);
        }

        internal int blocks, grows;                                 // pooled blocks (BlockBudget, preallocated; more only after the thread died), block buffer growths

        internal int queued { get { lock (gate) return queue.Count; } }

        public int StringId(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            int id;
            if (stringIds.TryGetValue(s, out id)) return id;
            id = strings.Count;
            strings.Add(s);
            stringIds[s] = id;
            return id;
        }

        public void Objects(List<SrecObject> table)
        {
            if (objectsDone) { Fail("Objects called twice: the second table is ignored"); return; }
            objectsDone = true;
            int n = table == null ? 0 : table.Count;
            var b = new SrecBuf(256 + n * 96);
            b.Int(n);
            for (int i = 0; i < n; i++)
            {
                var o = table[i] ?? new SrecObject();
                if (o.id != i) Fail("Objects: entry " + i + " has id " + o.id + " (ids must equal the table index; written as " + i + ")");
                SrecCodec.WriteObject(b, o, i);
            }
            GrowObjects(n);
            objectCount = n;
            Frame(SrecConst.TagObjects, b);
        }

        public void BeginSample(SrecStamp s)
        {
            if (closed) { Fail("BeginSample after Close"); return; }
            if (inSample) { Fail("BeginSample without EndSample: sample " + sample + " closed automatically"); EndSample(); }
            if (!objectsDone) Objects(null);
            if (s.sample != samples) Fail("BeginSample: stamp sample " + s.sample + ", expected the dense index " + samples);
            stamp = s;
            sample = samples;
            inSample = true;
            hasVirtual = false;
            frameFrom = frames;
            for (int i = 1; i < sec.Length; i++) { sec[i].length = 0; entries[i] = 0; prev[i] = 0; }
        }

        public void Xf(int id, byte bits, Vector3 pos, Quaternion rot, Vector3 scale, int parentId)
        {
            if (!Open(SrecConst.SecXf, id, true)) return;
            if ((bits & SrecConst.XfParent) != 0 && (parentId < -1 || parentId >= objectCount)) { Fail("Xf: parent id " + parentId + " of id " + id + " not in the object table"); return; }
            var b = sec[SrecConst.SecXf];
            Delta(SrecConst.SecXf, id);
            b.U8(bits);
            if ((bits & SrecConst.XfPos) != 0) b.V3(pos);
            if ((bits & SrecConst.XfRot) != 0) b.Q(rot);
            if ((bits & SrecConst.XfScale) != 0) b.V3(scale);
            if ((bits & SrecConst.XfParent) != 0) b.Ref(parentId);
            Touch(id);
        }

        public void Flags(int id, byte bits)
        {
            if (!Open(SrecConst.SecFlags, id, true)) return;
            Delta(SrecConst.SecFlags, id);
            sec[SrecConst.SecFlags].U8(bits);
            Touch(id);
        }

        public void Particles(int id, ParticleSystem.Particle[] buf, int count)
        {
            if (!Open(SrecConst.SecParticles, id, false)) return;
            int n = buf == null ? 0 : Math.Max(0, Math.Min(count, buf.Length));
            if (n > 0 && !ParticleLayoutOk) { Fail("ParticleSystem.Particle is " + SrecCodec.ParticleSize() + " bytes, not " + SrecConst.ParticleBytes + ": particle frames written empty"); n = 0; }
            var b = sec[SrecConst.SecParticles];
            AddFrame(id, b.length);
            b.Int(id);
            b.Int(n);
            b.U8(0);
            if (n > 0) CopyParticles(b, buf, n);
            entries[SrecConst.SecParticles]++;
            Touch(id);
        }

        public void Light(int id, byte mask, SrecLight v)
        {
            if (!Open(SrecConst.SecLights, id, true)) return;
            var b = sec[SrecConst.SecLights];
            Delta(SrecConst.SecLights, id);
            b.U8(mask);
            if ((mask & SrecConst.LiEnabled) != 0) b.U8(v.enabled ? 1 : 0);
            if ((mask & SrecConst.LiIntensity) != 0) b.F32(v.intensity);
            if ((mask & SrecConst.LiColor) != 0) b.Col(v.color);
            if ((mask & SrecConst.LiRange) != 0) b.F32(v.range);
            if ((mask & SrecConst.LiSpotAngle) != 0) b.F32(v.spotAngle);
            if ((mask & SrecConst.LiCookie) != 0) b.Ref(v.cookie);
            if ((mask & SrecConst.LiStaticShadows) != 0) b.U8(v.useStaticShadows ? 1 : 0);
            if ((mask & SrecConst.LiShadowsDirty) != 0) b.U8(v.staticShadowsDirty ? 1 : 0);
            Touch(id);
        }

        public void Material(int id, byte kind, int prop, Vector4 value, int texture)
        {
            if (!Open(SrecConst.SecMaterials, id, false)) return;
            if (kind > SrecConst.MatTexture || prop < 0) { Fail("Material: kind " + kind + " prop " + prop + " of id " + id + " invalid"); return; }
            var b = sec[SrecConst.SecMaterials];
            b.Int(id);
            b.U8(kind);
            b.Int(prop);
            if (kind == SrecConst.MatFloat) b.F32(value.x);
            else if (kind == SrecConst.MatTexture) b.Ref(texture);
            else b.V4(value);
            entries[SrecConst.SecMaterials]++;
            Touch(id);
        }

        public void MatProp(int id, byte kind, string name)
        {
            if (!Open(SrecConst.SecMatProp, id, false)) return;
            if (kind > SrecConst.MatTexture) { Fail("MatProp: kind " + kind + " of id " + id + " invalid"); return; }
            var b = sec[SrecConst.SecMatProp];
            b.Int(id);
            b.U8(kind);
            b.Str(name);
            entries[SrecConst.SecMatProp]++;
        }

        public void Virtual(SrecVirtual v)
        {
            if (!inSample) { Fail("Virtual outside BeginSample/EndSample"); return; }
            if (v == null) return;
            if (hasVirtual) { Fail("Virtual twice in sample " + sample + ": the second is ignored"); return; }
            hasVirtual = true;
            var b = sec[SrecConst.SecVirtual];
            var g = v.game;
            b.U8(g.ok ? 1 : 0); b.I32(g.step);
            SrecCodec.WritePose(b, g.rendered); SrecCodec.WritePose(b, g.clean);
            b.V3(g.positionCamera); b.V3(g.positionAim); b.V3(g.positionUp); b.V3(g.velocity); b.F32(g.aimDistance);
            b.Ref(StringId(g.box)); b.V3(g.probe); b.F32(g.extrapolation);
            b.V3(g.handOffset); b.Q(g.handRot); b.V3(g.shakeOffset); b.Q(g.shakeRot); b.U8(g.cut ? 1 : 0);
            b.V3(v.camPos); b.Q(v.camRot); b.F32(v.camFov);
            b.V3(v.boyPos3); b.V3(v.boyVel3); b.Ref(StringId(v.boyState)); b.Ref(StringId(v.boyVisual));
            b.V3(v.huddlePos3); b.V3(v.huddleVel3); b.Ref(StringId(v.huddleState));
            b.V3(v.probe); b.U8(v.flags);
            entries[SrecConst.SecVirtual] = 1;
        }

        public void ObjAdd(SrecObject o)
        {
            if (!inSample) { Fail("ObjAdd outside BeginSample/EndSample"); return; }
            if (o == null) return;
            if (o.id != objectCount) { Fail("ObjAdd: id " + o.id + ", expected the next dense id " + objectCount + ": not added"); return; }
            if (o.parentId < -1 || o.parentId >= objectCount) { Fail("ObjAdd: parent id " + o.parentId + " of id " + o.id + " not in the object table: not added"); return; }
            SrecCodec.WriteObject(sec[SrecConst.SecObjAdd], o, o.id);
            entries[SrecConst.SecObjAdd]++;
            GrowObjects(objectCount + 1);
            objectCount++;
        }

        public void ObjDel(int id)
        {
            if (!Open(SrecConst.SecObjDel, id, false)) return;
            sec[SrecConst.SecObjDel].Int(id);
            entries[SrecConst.SecObjDel]++;
        }

        public void Probe(int id, Vector3 pos)
        {
            if (!Open(SrecConst.SecProbes, id, true)) return;
            Delta(SrecConst.SecProbes, id);
            sec[SrecConst.SecProbes].V3(pos);
            Touch(id);
        }

        public void Render(int field, byte type, Vector4 value)
        {
            if (!inSample) { Fail("Render outside BeginSample/EndSample"); return; }
            if (field < 0 || type > SrecConst.ValInt) { Fail("Render: field " + field + " type " + type + " invalid"); return; }
            var b = sec[SrecConst.SecRender];
            b.Int(field);
            b.U8(type);
            if (type == SrecConst.ValFloat) b.F32(value.x);
            else if (type == SrecConst.ValInt) b.I32((int)value.x);
            else b.V4(value);
            entries[SrecConst.SecRender]++;
        }

        public void Event(string kind, string name, string detail)
        {
            if (!inSample) { Fail("Event outside BeginSample/EndSample: " + kind + " " + name); return; }
            var b = sec[SrecConst.SecEvents];
            b.Str(kind); b.Str(name); b.Str(detail);
            entries[SrecConst.SecEvents]++;
        }

        public void CamActive(int id, bool active)
        {
            if (!Open(SrecConst.SecCamActive, id, true)) return;
            Delta(SrecConst.SecCamActive, id);
            sec[SrecConst.SecCamActive].U8(active ? 1 : 0);
            Touch(id);
        }

        public void Rope(int id, Vector3[] vertices, int count)
        {
            if (!Open(SrecConst.SecRope, id, false)) return;
            int n = vertices == null ? 0 : Math.Max(0, Math.Min(count, vertices.Length));
            var b = sec[SrecConst.SecRope];
            b.Int(id);
            b.Int(n);
            for (int i = 0; i < n; i++) b.V3(vertices[i]);
            entries[SrecConst.SecRope]++;
            Touch(id);
        }

        public void SharedMat(int id, int slot, int material)
        {
            if (!Open(SrecConst.SecSharedMat, id, true)) return;
            if (slot < 0) { Fail("SharedMat: slot " + slot + " of id " + id + " invalid"); return; }
            var b = sec[SrecConst.SecSharedMat];
            Delta(SrecConst.SecSharedMat, id);
            b.Int(slot);
            b.Ref(material);
            Touch(id);
        }

        public int shaderChannels { get { return shaderTypes.Count; } }

        // Defines the next dense shader channel (section 17) and returns its id, -1 on a contract violation. target: a string
        // id from StringId (ShMaterial), an object table id (ShBlock), ignored for ShGlobal.
        public int ShaderDef(byte scope, int target, byte type, string name)
        {
            if (!inSample) { Fail("ShaderDef outside BeginSample/EndSample: " + name); return -1; }
            if (scope > SrecConst.ShBlock || (type != SrecConst.ValFloat && type != SrecConst.ValVec4) || string.IsNullOrEmpty(name)) { Fail("ShaderDef: scope " + scope + " type " + type + " name '" + name + "' invalid"); return -1; }
            if (scope == SrecConst.ShGlobal) target = -1;
            else if (scope == SrecConst.ShMaterial && (target < 0 || target >= strings.Count)) { Fail("ShaderDef: material string id " + target + " of '" + name + "' not in the string table"); return -1; }
            else if (scope == SrecConst.ShBlock && (target < 0 || target >= objectCount)) { Fail("ShaderDef: renderer id " + target + " of '" + name + "' not in the object table"); return -1; }
            int ch = shaderTypes.Count;
            shaderTypes.Add(type);
            var b = sec[SrecConst.SecShaderDef];
            b.Int(ch);
            b.U8(scope);
            b.Ref(target);
            b.U8(type);
            b.Str(name);
            entries[SrecConst.SecShaderDef]++;
            return ch;
        }

        public void ShaderVal(int channel, Vector4 value)
        {
            if (!inSample) { Fail("ShaderVal outside BeginSample/EndSample"); return; }
            if (channel < 0 || channel >= shaderTypes.Count) { Fail("ShaderVal: channel " + channel + " not defined (" + shaderTypes.Count + " channels)"); return; }
            int p = prev[SrecConst.SecShaderVal];
            if (channel < p) { Fail("ShaderVal: channel " + channel + " after " + p + " (channels must not descend within a sample)"); return; }
            var b = sec[SrecConst.SecShaderVal];
            b.Var((uint)(channel - p));
            prev[SrecConst.SecShaderVal] = channel;
            if (shaderTypes[channel] == SrecConst.ValFloat) b.F32(value.x);
            else b.V4(value);
            entries[SrecConst.SecShaderVal]++;
        }

        public void EndSample()
        {
            if (!inSample) { Fail("EndSample without BeginSample"); return; }
            inSample = false;
            var st = sec[SrecCodec.SecStrings];
            for (int i = stringsSent; i < strings.Count; i++) { st.Int(i); st.Str(strings[i]); entries[SrecCodec.SecStrings]++; }
            stringsSent = strings.Count;
            int len = SrecCodec.StampBytes;
            for (int k = 0; k < SrecCodec.FileOrder.Length; k++)
            {
                int s = SrecCodec.FileOrder[k];
                if (entries[s] > 0) len += 5 + SectionLength(s);
            }
            long start = bytes;
            var blk = Take(12 + len);
            blk.U32(SrecConst.TagSample);
            blk.U32((uint)len);
            blk.U32((uint)stamp.sample); blk.F64(stamp.gameTime); blk.F64(stamp.realTime); blk.I64(stamp.logicStep);
            blk.I32(stamp.frameCount); blk.U16(stamp.fixedSteps); blk.U16(stamp.flags);
            for (int k = 0; k < SrecCodec.FileOrder.Length; k++)
            {
                int s = SrecCodec.FileOrder[k];
                if (entries[s] == 0) continue;
                blk.U8(s);
                blk.U32((uint)SectionLength(s));
                if (s != SrecConst.SecVirtual) blk.Int(entries[s]);
                if (s == SrecConst.SecParticles) for (int f = frameFrom; f < frames; f++) frameOffset[f] += start + blk.length;
                blk.Append(sec[s]);
            }
            blk.U32(0);
            if (samples == offsets.Length) Array.Resize(ref offsets, offsets.Length * 2);
            offsets[samples] = start;
            bytes += blk.length;
            samples++;
            Enqueue(blk);
        }

        public void Close()
        {
            if (closed) return;
            if (inSample) EndSample();
            if (!objectsDone) Objects(null);
            closed = true;
            Frame(SrecConst.TagIndex, Index());
            var end = new SrecBuf(16);
            end.U64((ulong)(bytes + 24));
            end.U32((uint)samples);
            Frame(SrecConst.TagEnd, end);
            lock (gate) { closing = true; Monitor.PulseAll(gate); }
            if (!thread.Join(60000)) Fail("writer thread did not finish within 60 s");
        }

        bool Open(byte s, int id, bool delta)
        {
            if (!inSample) { Fail("section " + s + " entry outside BeginSample/EndSample"); return false; }
            if (id < 0 || id >= objectCount) { Fail("section " + s + ": id " + id + " not in the object table (" + objectCount + " objects)"); return false; }
            if (delta && id < prev[s]) { Fail("section " + s + ": id " + id + " after " + prev[s] + " (ids must not descend within a section)"); return false; }
            return true;
        }

        void Delta(byte s, int id)
        {
            sec[s].Var((uint)(id - prev[s]));
            prev[s] = id;
            entries[s]++;
        }

        void Touch(int id)
        {
            if (keys[id]++ == 0) first[id] = sample;
            last[id] = sample;
        }

        int SectionLength(int s) { return (s == SrecConst.SecVirtual ? 0 : SrecBuf.VarLen((uint)entries[s])) + sec[s].length; }

        void AddFrame(int id, int rel)
        {
            if (frames == frameId.Length)
            {
                Array.Resize(ref frameId, frames * 2);
                Array.Resize(ref frameSample, frames * 2);
                Array.Resize(ref frameOffset, frames * 2);
            }
            frameId[frames] = id;
            frameSample[frames] = sample;
            frameOffset[frames] = rel;
            frames++;
        }

        unsafe void CopyParticles(SrecBuf b, ParticleSystem.Particle[] buf, int n)
        {
            int size = n * SrecConst.ParticleBytes;
            b.Ensure(size);
            fixed (ParticleSystem.Particle* p = buf) Marshal.Copy((IntPtr)p, b.data, b.length, size);
            b.length += size;
        }

        void GrowObjects(int n)
        {
            if (n <= keys.Length) return;
            int c = keys.Length;
            while (c < n) c *= 2;
            Array.Resize(ref first, c);
            Array.Resize(ref last, c);
            Array.Resize(ref keys, c);
        }

        SrecBuf Index()
        {
            var b = new SrecBuf(64 + samples * 8 + objectCount * 6 + frames * 12);
            b.Int(samples);
            for (int i = 0; i < samples; i++) b.U64((ulong)offsets[i]);
            b.Int(objectCount);
            for (int i = 0; i < objectCount; i++)
            {
                if (keys[i] == 0) { b.Int(0); b.Int(0); b.Int(0); continue; }
                b.Int(first[i]); b.Int(last[i]); b.Int(keys[i]);
            }
            var perId = new int[objectCount];
            for (int f = 0; f < frames; f++) perId[frameId[f]]++;
            var fill = new int[objectCount];
            int systems = 0;
            for (int i = 0, sum = 0; i < objectCount; i++) { fill[i] = sum; sum += perId[i]; if (perId[i] > 0) systems++; }
            var order = new int[frames];
            for (int f = 0; f < frames; f++) order[fill[frameId[f]]++] = f;
            b.Int(systems);
            for (int i = 0, at = 0; i < objectCount; i++)
            {
                int n = perId[i];
                if (n == 0) continue;
                b.Int(i);
                b.Int(n);
                for (int k = 0; k < n; k++) { int f = order[at + k]; b.Int(frameSample[f]); b.U64((ulong)frameOffset[f]); }
                at += n;
            }
            b.Strs(strings);
            return b;
        }

        void Frame(uint tag, SrecBuf payload)
        {
            var blk = Take(12 + payload.length);
            blk.U32(tag);
            blk.U32((uint)payload.length);
            blk.Append(payload);
            blk.U32(0);
            bytes += blk.length;
            Enqueue(blk);
        }

        SrecBuf Take(int size)
        {
            SrecBuf b = null;
            lock (gate)
            {
                while (pool.Count == 0 && blocks >= BlockBudget && !failed && thread.IsAlive) Monitor.Wait(gate, 100);
                if (pool.Count > 0) b = pool.Pop(); else blocks++;
            }
            if (b == null) b = new SrecBuf(Math.Max(size, 1 << 16));
            if (b.data.Length < size) grows++;
            b.length = 0;
            b.Ensure(size);
            return b;
        }

        void Enqueue(SrecBuf b)
        {
            lock (gate)
            {
                if (queue.Count >= QueueLimit && !failed)
                {
                    stalls++;
                    while (queue.Count >= QueueLimit && !failed && thread.IsAlive) Monitor.Wait(gate, 100);
                }
                queue.Enqueue(b);
                Monitor.PulseAll(gate);
            }
        }

        void Loop()
        {
            while (true)
            {
                SrecBuf b;
                bool idle;
                lock (gate)
                {
                    while (queue.Count == 0 && !closing) Monitor.Wait(gate);
                    if (queue.Count == 0) break;
                    b = queue.Dequeue();
                    idle = queue.Count == 0;
                    Monitor.PulseAll(gate);
                }
                if (!failed)
                {
                    try
                    {
                        int len = b.length - 12;
                        SrecBuf.Put32(b.data, 8 + len, SrecCrc.Compute(b.data, 8, len));
                        file.Write(b.data, 0, b.length);
                        if (idle) file.Flush();
                    }
                    catch (Exception e) { failed = true; Fail("write failed: " + e.GetType().Name + ": " + e.Message); }
                }
                lock (gate) { pool.Push(b); Monitor.PulseAll(gate); }
            }
            try { file.Flush(); }
            catch (Exception e) { failed = true; Fail("flush failed: " + e.GetType().Name + ": " + e.Message); }
            try { file.Close(); }
            catch (Exception e) { Fail("close failed: " + e.GetType().Name + ": " + e.Message); }
        }

        void Fail(string message) { Interlocked.CompareExchange(ref error, message, null); }
    }

    public sealed class SceneRecording
    {
        public RecordHeader header;
        public int sampleCount;
        public double[] times, realTimes;
        public long[] logicSteps;
        public int[] frameCounts;
        public ushort[] fixedSteps, flags;
        public readonly List<SrecObject> objects = new List<SrecObject>();   // index = id: OBJT entries, then OBJA records
        public readonly List<string> strings = new List<string>();           // string table (inline section 16, completed from INDX)
        public readonly List<SrecShaderChannel> shaderChannels = new List<SrecShaderChannel>();   // index = channel (sections 17/18), empty in content version 1 files
        public string path = "";
        public long bytes;                                                    // file length
        public bool truncated;                                                // no valid INDX + SEND: readable prefix recovered

        internal readonly Dictionary<int, SrecParticleTrack> particleTracks = new Dictionary<int, SrecParticleTrack>();
        internal readonly List<long> sampleOffsets = new List<long>();
        internal long readable;
        internal string stop = "";
        internal bool indexFound;
        readonly object fileGate = new object();
        FileStream particleFile;
        byte[] scratch;

        internal bool ReadAt(long offset, byte[] dst, int size)
        {
            lock (fileGate)
            {
                try
                {
                    if (particleFile == null) particleFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                    particleFile.Position = offset;
                    int got = 0;
                    while (got < size)
                    {
                        int k = particleFile.Read(dst, got, size - got);
                        if (k <= 0) return false;
                        got += k;
                    }
                    return true;
                }
                catch (Exception) { CloseFile(); return false; }
            }
        }

        internal byte[] Scratch(int size)
        {
            if (scratch == null || scratch.Length < size) scratch = new byte[Math.Max(size, 1 << 14)];
            return scratch;
        }

        internal void Release() { lock (fileGate) CloseFile(); }

        void CloseFile()
        {
            if (particleFile == null) return;
            try { particleFile.Close(); }
            catch (Exception) { }
            particleFile = null;
        }
    }

    internal sealed class SrecParticleTrack
    {
        public int count;
        public int[] samples = new int[16], n = new int[16];
        public long[] entry = new long[16], data = new long[16];

        public void Add(int sample, int particles, long entryOffset, long dataOffset)
        {
            if (count == samples.Length)
            {
                Array.Resize(ref samples, count * 2);
                Array.Resize(ref n, count * 2);
                Array.Resize(ref entry, count * 2);
                Array.Resize(ref data, count * 2);
            }
            samples[count] = sample; n[count] = particles; entry[count] = entryOffset; data[count] = dataOffset;
            count++;
        }

        public int Find(int sample)
        {
            int lo = 0, hi = count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (samples[mid] <= sample) lo = mid + 1; else hi = mid;
            }
            return lo > 0 && samples[lo - 1] == sample ? lo - 1 : -1;
        }
    }

    public static class SrecReader
    {
        public static SceneRecording Read(string path, SpecReport report) { return Read(path, null, report); }

        public static SceneRecording Read(string path, ISrecSink sink, SpecReport report)
        {
            return new SrecReadState(path, sink, report ?? new SpecReport()).Run();
        }

        public static int ReadParticles(SceneRecording r, int obj, int sample, byte[] dst)   // n particles of system obj at sample (0 = no frame there) as raw 84-byte records; -1 = dst too small or unreadable
        {
            long offset;
            int n = Frame(r, obj, sample, out offset);
            if (n <= 0) return n;
            int size = n * SrecConst.ParticleBytes;
            if (dst == null || dst.Length < size) return -1;
            return r.ReadAt(offset, dst, size) ? n : -1;
        }

        internal static unsafe int ReadParticles(SceneRecording r, int obj, int sample, ParticleSystem.Particle[] dst)
        {
            long offset;
            int n = Frame(r, obj, sample, out offset);
            if (n <= 0) return n;
            if (dst == null || dst.Length < n || SrecCodec.ParticleSize() != SrecConst.ParticleBytes) return -1;
            int size = n * SrecConst.ParticleBytes;
            var tmp = r.Scratch(size);
            if (!r.ReadAt(offset, tmp, size)) return -1;
            fixed (ParticleSystem.Particle* p = dst) Marshal.Copy(tmp, 0, (IntPtr)p, size);
            return n;
        }

        static int Frame(SceneRecording r, int obj, int sample, out long offset)
        {
            offset = 0;
            if (r == null) return -1;
            SrecParticleTrack t;
            if (!r.particleTracks.TryGetValue(obj, out t)) return 0;
            int f = t.Find(sample);
            if (f < 0) return 0;
            offset = t.data[f];
            return t.n[f];
        }
    }

    internal sealed class SrecReadState
    {
        readonly string path;
        readonly ISrecSink sink;
        readonly ISrecShaderSink shaderSink;
        readonly SpecReport report;
        readonly SceneRecording rec = new SceneRecording();
        readonly SrecIn cur = new SrecIn();
        readonly SrecVirtual virt = new SrecVirtual();
        readonly List<int> parents = new List<int>();
        Vector3[] rope = new Vector3[64];
        bool headerSeen, objectsSeen, begun, complete, endWarned, badRef;
        int renumbered;
        long sendBytes = -1;
        uint sendSamples;
        long[] ixOffsets;
        int[] ixFirst, ixLast, ixKeys;
        Dictionary<int, List<long>> ixFrames;
        List<string> ixStrings;

        public SrecReadState(string file, ISrecSink s, SpecReport r) { path = file; sink = s; shaderSink = s as ISrecShaderSink; report = r; }

        public SceneRecording Run()
        {
            rec.path = path;
            FileStream fs;
            try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16); }
            catch (Exception e) { report.Error(path, "cannot open: " + e.GetType().Name + ": " + e.Message); return null; }
            string stop = null;
            bool decodeError = false;
            using (fs)
            {
                long length = fs.Length;
                rec.bytes = length;
                var head = new byte[8];
                if (length < 8 || !ReadFully(fs, head, 8)) { report.Error(path, "not a scene.srec: shorter than the 8-byte preamble"); return null; }
                uint magic = SrecBuf.Get32(head, 0);
                int version = head[4] | head[5] << 8;
                if (magic != SrecConst.Magic) { report.Error(path, "not a scene.srec: magic 0x" + magic.ToString("X8")); return null; }
                if (version > SrecConst.Version) { report.Error(path, "scene.srec version " + version + " is newer than this reader (" + SrecConst.Version + ")"); return null; }
                long pos = 8;
                rec.readable = pos;
                var buf = new byte[1 << 16];
                while (true)
                {
                    if (complete) { if (pos != length) report.Warn(path, (length - pos) + " bytes after SEND ignored"); break; }
                    if (pos == length) { stop = "file ends without INDX/SEND"; break; }
                    if (length - pos < 12) { stop = "incomplete block header at offset " + pos; break; }
                    if (!ReadFully(fs, head, 8)) { stop = "read error at offset " + pos; break; }
                    uint tag = SrecBuf.Get32(head, 0);
                    uint len = SrecBuf.Get32(head, 4);
                    string name = SrecCodec.TagName(tag);
                    if (len > length - pos - 12) { stop = "incomplete " + name + " block at offset " + pos + " (" + len + " payload bytes, " + (length - pos - 12) + " left)"; break; }
                    if (buf.Length < len + 4) buf = new byte[Math.Max((int)len + 4, buf.Length * 2)];
                    if (!ReadFully(fs, buf, (int)len + 4)) { stop = "read error in " + name + " block at offset " + pos; break; }
                    if (SrecCrc.Compute(buf, 0, (int)len) != SrecBuf.Get32(buf, (int)len))
                    {
                        bool lastBlock = pos + 12 + len == length;
                        stop = "CRC mismatch in " + name + " block at offset " + pos + (lastBlock ? " (last block of the file)" : " (data follows: corrupt, not a crash prefix)");
                        decodeError = !lastBlock;
                        break;
                    }
                    if (!headerSeen && tag != SrecConst.TagHead) { stop = "first block is " + name + ", not SRHD"; decodeError = true; break; }
                    try { Block(tag, buf, (int)len, pos); }
                    catch (Exception e) { stop = name + " block at offset " + pos + ": " + e.GetType().Name + ": " + e.Message; decodeError = true; break; }
                    pos += 12 + len;
                    rec.readable = pos;
                }
            }
            if (!headerSeen) { report.Error(path, "no readable SRHD header" + (stop != null ? " (" + stop + ")" : "")); return null; }
            Finish(stop, decodeError);
            return rec;
        }

        void Finish(string stop, bool decodeError)
        {
            int n = rec.sampleCount;
            Array.Resize(ref rec.times, n); Array.Resize(ref rec.realTimes, n); Array.Resize(ref rec.logicSteps, n);
            Array.Resize(ref rec.frameCounts, n); Array.Resize(ref rec.fixedSteps, n); Array.Resize(ref rec.flags, n);
            rec.truncated = !complete;
            rec.stop = stop ?? "";
            if (complete)
            {
                if (sendBytes != rec.readable) report.Warn(path, "SEND total " + sendBytes + " bytes, readable " + rec.readable);
                if (sendSamples != (uint)n) report.Warn(path, "SEND sample count " + sendSamples + ", read " + n);
                if (!rec.indexFound) report.Warn(path, "SEND without INDX");
            }
            if (rec.indexFound) CrossCheck();
            if (stop != null)
            {
                string line = "readable prefix of " + n + " samples, " + rec.readable + " of " + rec.bytes + " bytes (" + stop + ")";
                if (decodeError) report.Error(path, line); else report.Warn(path, "truncated: " + line);
            }
            if (!begun) Begin();
            if (sink != null)
            {
                try { sink.End(rec); }
                catch (Exception e) { report.Error(path, "sink End: " + e.GetType().Name + ": " + e.Message); }
            }
        }

        void Block(uint tag, byte[] buf, int len, long pos)
        {
            cur.data = buf; cur.pos = 0; cur.end = len;
            switch (tag)
            {
                case SrecConst.TagHead:
                    if (headerSeen) { report.Warn(path, "second SRHD at offset " + pos + " ignored"); return; }
                    rec.header = SrecCodec.ReadHeader(cur);
                    headerSeen = true;
                    return;
                case SrecConst.TagObjects:
                    if (objectsSeen || begun) throw new InvalidDataException("OBJT after the first sample or twice");
                    Objects();
                    objectsSeen = true;
                    return;
                case SrecConst.TagSample:
                    if (!begun) Begin();
                    Sample(pos);
                    return;
                case SrecConst.TagIndex:
                    Index();
                    rec.indexFound = true;
                    return;
                case SrecConst.TagEnd:
                    sendBytes = (long)cur.U64();
                    sendSamples = cur.U32();
                    complete = true;
                    return;
            }
        }

        void Begin()
        {
            begun = true;
            if (rec.header == null) rec.header = new RecordHeader();
            if (sink != null) sink.Begin(rec);
        }

        void Objects()
        {
            int n = cur.Count(8);
            for (int i = 0; i < n; i++)
            {
                var o = SrecCodec.ReadObject(cur);
                if (o.id != i) throw new InvalidDataException("OBJT record " + i + " has id " + o.id);
                rec.objects.Add(o);
                parents.Add(o.parentId);
            }
            for (int i = 0; i < n; i++) if (rec.objects[i].parentId < -1 || rec.objects[i].parentId >= n) throw new InvalidDataException("OBJT record " + i + " has parent id " + rec.objects[i].parentId);
        }

        void Sample(long blockPos)
        {
            int index = rec.sampleCount;
            var s = new SrecStamp();
            s.sample = (int)cur.U32(); s.gameTime = cur.F64(); s.realTime = cur.F64(); s.logicStep = cur.I64();
            s.frameCount = cur.I32(); s.fixedSteps = cur.U16(); s.flags = cur.U16();
            if (s.sample != index)
            {
                if (renumbered++ == 0) report.Warn(path, "sample " + index + " is stamped " + s.sample + ": dense indices used");
                s.sample = index;
            }
            Store(index, s);
            rec.sampleCount = index + 1;
            rec.sampleOffsets.Add(blockPos);
            if (sink != null) sink.Sample(s);
            int sections = cur.pos, end = cur.end;
            while (cur.pos < end)
            {
                byte id = cur.U8();
                int sl = (int)cur.U32();
                cur.Need(sl);
                if (id == SrecCodec.SecStrings) { cur.end = cur.pos + sl; Strings(); cur.end = end; }
                else cur.pos += sl;
            }
            cur.pos = sections;
            while (cur.pos < end)
            {
                byte id = cur.U8();
                int sl = (int)cur.U32();
                cur.Need(sl);
                int e = cur.pos + sl;
                if (id == SrecCodec.SecStrings || id == 0 || id >= SrecCodec.SectionSlots) { cur.pos = e; continue; }
                cur.end = e;
                Section(id, index, blockPos);
                if (cur.pos != e) throw new InvalidDataException("section " + id + " of sample " + index + ": " + (e - cur.pos) + " bytes left over");
                cur.end = end;
            }
        }

        void Store(int i, SrecStamp s)
        {
            if (rec.times == null || i >= rec.times.Length)
            {
                int c = rec.times == null ? 1024 : rec.times.Length * 2;
                Array.Resize(ref rec.times, c); Array.Resize(ref rec.realTimes, c); Array.Resize(ref rec.logicSteps, c);
                Array.Resize(ref rec.frameCounts, c); Array.Resize(ref rec.fixedSteps, c); Array.Resize(ref rec.flags, c);
            }
            rec.times[i] = s.gameTime; rec.realTimes[i] = s.realTime; rec.logicSteps[i] = s.logicStep;
            rec.frameCounts[i] = s.frameCount; rec.fixedSteps[i] = s.fixedSteps; rec.flags[i] = s.flags;
        }

        void Strings()
        {
            int n = cur.Count(2);
            for (int i = 0; i < n; i++)
            {
                int id = cur.Int();
                string s = cur.Str();
                if (id < rec.strings.Count) { if (rec.strings[id] != s) report.Warn(path, "string " + id + " redefined"); continue; }
                if (id > rec.strings.Count) report.Warn(path, "string table gap before id " + id);
                while (rec.strings.Count < id) rec.strings.Add("");
                rec.strings.Add(s);
            }
        }

        void Section(byte sec, int sample, long blockPos)
        {
            int prev = 0;
            int n = sec == SrecConst.SecVirtual ? 1 : cur.Count(1);
            for (int i = 0; i < n; i++)
            {
                switch (sec)
                {
                    case SrecConst.SecXf:
                        {
                            int id = Id(prev += cur.Int());
                            byte bits = cur.U8();
                            Vector3 p = (bits & SrecConst.XfPos) != 0 ? cur.V3() : Vector3.zero;
                            Quaternion q = (bits & SrecConst.XfRot) != 0 ? cur.Q() : Quaternion.identity;
                            Vector3 sc = (bits & SrecConst.XfScale) != 0 ? cur.V3() : Vector3.one;
                            if ((bits & SrecConst.XfParent) != 0)
                            {
                                int parent = cur.Ref();
                                if (parent < -1 || parent >= rec.objects.Count) throw new InvalidDataException("Xf parent id " + parent + " of id " + id + " out of range");
                                parents[id] = parent;
                            }
                            Touch(id, sample);
                            if (sink != null) sink.Xf(sample, id, bits, p, q, sc, parents[id]);
                            break;
                        }
                    case SrecConst.SecFlags:
                        {
                            int id = Id(prev += cur.Int());
                            byte bits = cur.U8();
                            Touch(id, sample);
                            if (sink != null) sink.Flags(sample, id, bits);
                            break;
                        }
                    case SrecConst.SecParticles:
                        {
                            long entry = blockPos + 8 + cur.pos;
                            int id = Id(cur.Int());
                            int count = cur.Count(0);
                            if (cur.U8() != 0) throw new InvalidDataException("particle encoding of id " + id + " is not raw84");
                            if (count > int.MaxValue / SrecConst.ParticleBytes) throw new InvalidDataException("particle count " + count);
                            int size = count * SrecConst.ParticleBytes;
                            cur.Need(size);
                            long data = blockPos + 8 + cur.pos;
                            cur.pos += size;
                            SrecParticleTrack t;
                            if (!rec.particleTracks.TryGetValue(id, out t)) { t = new SrecParticleTrack(); rec.particleTracks[id] = t; }
                            t.Add(sample, count, entry, data);
                            Touch(id, sample);
                            if (sink != null) sink.Particles(sample, id, count);
                            break;
                        }
                    case SrecConst.SecLights:
                        {
                            int id = Id(prev += cur.Int());
                            byte mask = cur.U8();
                            var v = new SrecLight();
                            v.cookie = -1;
                            if ((mask & SrecConst.LiEnabled) != 0) v.enabled = cur.U8() != 0;
                            if ((mask & SrecConst.LiIntensity) != 0) v.intensity = cur.F32();
                            if ((mask & SrecConst.LiColor) != 0) v.color = cur.Col();
                            if ((mask & SrecConst.LiRange) != 0) v.range = cur.F32();
                            if ((mask & SrecConst.LiSpotAngle) != 0) v.spotAngle = cur.F32();
                            if ((mask & SrecConst.LiCookie) != 0) v.cookie = StringRef(cur.Ref());
                            if ((mask & SrecConst.LiStaticShadows) != 0) v.useStaticShadows = cur.U8() != 0;
                            if ((mask & SrecConst.LiShadowsDirty) != 0) v.staticShadowsDirty = cur.U8() != 0;
                            Touch(id, sample);
                            if (sink != null) sink.Light(sample, id, mask, v);
                            break;
                        }
                    case SrecConst.SecMaterials:
                        {
                            int id = Id(cur.Int());
                            byte kind = cur.U8();
                            int prop = cur.Int();
                            var value = Vector4.zero;
                            int texture = -1;
                            if (kind == SrecConst.MatFloat) value.x = cur.F32();
                            else if (kind == SrecConst.MatColor || kind == SrecConst.MatVector) value = cur.V4();
                            else if (kind == SrecConst.MatTexture) texture = StringRef(cur.Ref());
                            else throw new InvalidDataException("material kind " + kind + " of id " + id);
                            Touch(id, sample);
                            if (sink != null) sink.Material(sample, id, kind, prop, value, texture);
                            break;
                        }
                    case SrecConst.SecMatProp:
                        {
                            int id = Id(cur.Int());
                            byte kind = cur.U8();
                            string name = cur.Str();
                            if (sink != null) sink.MatProp(sample, id, kind, name);
                            break;
                        }
                    case SrecConst.SecVirtual:
                        Virtual();
                        if (sink != null) sink.Virtual(sample, virt);
                        break;
                    case SrecConst.SecObjAdd:
                        {
                            var o = SrecCodec.ReadObject(cur);
                            if (o.id != rec.objects.Count) throw new InvalidDataException("OBJA id " + o.id + ", expected " + rec.objects.Count);
                            if (o.parentId < -1 || o.parentId >= o.id) throw new InvalidDataException("OBJA id " + o.id + " has parent id " + o.parentId);
                            o.addedAt = sample;
                            rec.objects.Add(o);
                            parents.Add(o.parentId);
                            if (sink != null) sink.ObjAdd(sample, o);
                            break;
                        }
                    case SrecConst.SecObjDel:
                        {
                            int id = Id(cur.Int());
                            if (rec.objects[id].deletedAt < 0) rec.objects[id].deletedAt = sample;
                            if (sink != null) sink.ObjDel(sample, id);
                            break;
                        }
                    case SrecConst.SecProbes:
                        {
                            int id = Id(prev += cur.Int());
                            var p = cur.V3();
                            Touch(id, sample);
                            if (sink != null) sink.Probe(sample, id, p);
                            break;
                        }
                    case SrecConst.SecRender:
                        {
                            int field = cur.Int();
                            byte type = cur.U8();
                            var value = Vector4.zero;
                            if (type == SrecConst.ValFloat) value.x = cur.F32();
                            else if (type == SrecConst.ValVec4) value = cur.V4();
                            else if (type == SrecConst.ValInt) value.x = cur.I32();
                            else throw new InvalidDataException("render value type " + type);
                            if (sink != null) sink.Render(sample, field, type, value);
                            break;
                        }
                    case SrecConst.SecEvents:
                        {
                            string kind = cur.Str(), name = cur.Str(), detail = cur.Str();
                            if (sink != null) sink.Event(sample, kind, name, detail);
                            break;
                        }
                    case SrecConst.SecCamActive:
                        {
                            int id = Id(prev += cur.Int());
                            bool active = cur.U8() != 0;
                            Touch(id, sample);
                            if (sink != null) sink.CamActive(sample, id, active);
                            break;
                        }
                    case SrecConst.SecRope:
                        {
                            int id = Id(cur.Int());
                            int count = cur.Count(12);
                            if (rope.Length < count) rope = new Vector3[Math.Max(count, rope.Length * 2)];
                            for (int k = 0; k < count; k++) rope[k] = cur.V3();
                            Touch(id, sample);
                            if (sink != null) sink.Rope(sample, id, rope, count);
                            break;
                        }
                    case SrecConst.SecSharedMat:
                        {
                            int id = Id(prev += cur.Int());
                            int slot = cur.Int();
                            int material = StringRef(cur.Ref());
                            Touch(id, sample);
                            if (sink != null) sink.SharedMat(sample, id, slot, material);
                            break;
                        }
                    case SrecConst.SecShaderDef:
                        {
                            int ch = cur.Int();
                            byte scope = cur.U8();
                            int target = cur.Ref();
                            byte type = cur.U8();
                            string name = cur.Str();
                            if (ch != rec.shaderChannels.Count) throw new InvalidDataException("shader channel " + ch + " defined out of order (" + rec.shaderChannels.Count + " defined)");
                            if (scope > SrecConst.ShBlock || (type != SrecConst.ValFloat && type != SrecConst.ValVec4)) throw new InvalidDataException("shader channel " + ch + ": scope " + scope + " type " + type);
                            if (scope == SrecConst.ShBlock) Id(target);
                            else if (scope == SrecConst.ShMaterial) StringRef(target);
                            else target = -1;
                            var c = new SrecShaderChannel { id = ch, scope = scope, target = target, type = type, name = name, definedAt = sample };
                            rec.shaderChannels.Add(c);
                            if (shaderSink != null) shaderSink.ShaderDef(sample, c);
                            break;
                        }
                    case SrecConst.SecShaderVal:
                        {
                            int ch = prev += cur.Int();
                            if (ch < 0 || ch >= rec.shaderChannels.Count) throw new InvalidDataException("shader value of undefined channel " + ch + " (" + rec.shaderChannels.Count + " defined)");
                            var c = rec.shaderChannels[ch];
                            var value = Vector4.zero;
                            if (c.type == SrecConst.ValFloat) value.x = cur.F32();
                            else value = cur.V4();
                            if (c.keys++ == 0) c.firstSample = sample;
                            c.lastSample = sample;
                            if (shaderSink != null) shaderSink.ShaderVal(sample, ch, value);
                            break;
                        }
                    default:
                        cur.pos = cur.end;
                        return;
                }
            }
        }

        void Virtual()
        {
            var g = virt.game;
            g.ok = cur.U8() != 0; g.step = cur.I32();
            g.rendered = SrecCodec.ReadPose(cur, g.ok); g.clean = SrecCodec.ReadPose(cur, g.ok);
            g.positionCamera = cur.V3(); g.positionAim = cur.V3(); g.positionUp = cur.V3(); g.velocity = cur.V3(); g.aimDistance = cur.F32();
            g.box = Name(cur.Ref()); g.probe = cur.V3(); g.extrapolation = cur.F32();
            g.handOffset = cur.V3(); g.handRot = cur.Q(); g.shakeOffset = cur.V3(); g.shakeRot = cur.Q(); g.cut = cur.U8() != 0;
            virt.camPos = cur.V3(); virt.camRot = cur.Q(); virt.camFov = cur.F32();
            virt.boyPos3 = cur.V3(); virt.boyVel3 = cur.V3(); virt.boyState = Name(cur.Ref()); virt.boyVisual = Name(cur.Ref());
            virt.huddlePos3 = cur.V3(); virt.huddleVel3 = cur.V3(); virt.huddleState = Name(cur.Ref());
            virt.probe = cur.V3(); virt.flags = cur.U8();
        }

        int Id(int id)
        {
            if (id < 0 || id >= rec.objects.Count) throw new InvalidDataException("object id " + id + " out of range (" + rec.objects.Count + " objects)");
            return id;
        }

        int StringRef(int id)
        {
            if (id >= rec.strings.Count) { WarnRef(id); }
            return id;
        }

        string Name(int id)
        {
            if (id < 0) return "";
            if (id >= rec.strings.Count) { WarnRef(id); return ""; }
            return rec.strings[id];
        }

        void WarnRef(int id)
        {
            if (badRef) return;
            badRef = true;
            report.Warn(path, "string id " + id + " used before its definition (" + rec.strings.Count + " strings known)");
        }

        void Touch(int id, int sample)
        {
            var o = rec.objects[id];
            if (o.keyCount++ == 0) o.firstSample = sample;
            o.lastSample = sample;
        }

        void Index()
        {
            int ns = cur.Count(8);
            ixOffsets = new long[ns];
            for (int i = 0; i < ns; i++) ixOffsets[i] = (long)cur.U64();
            int no = cur.Count(3);
            ixFirst = new int[no]; ixLast = new int[no]; ixKeys = new int[no];
            for (int i = 0; i < no; i++) { ixFirst[i] = cur.Int(); ixLast[i] = cur.Int(); ixKeys[i] = cur.Int(); }
            int systems = cur.Count(2);
            ixFrames = new Dictionary<int, List<long>>();
            for (int i = 0; i < systems; i++)
            {
                int id = cur.Int();
                int frames = cur.Count(9);
                var l = new List<long>(frames * 2);
                for (int k = 0; k < frames; k++) { l.Add(cur.Int()); l.Add((long)cur.U64()); }
                ixFrames[id] = l;
            }
            ixStrings = new List<string>();
            cur.Strs(ixStrings);
        }

        void CrossCheck()
        {
            var bad = new List<string>();
            if (ixOffsets.Length != rec.sampleCount) bad.Add("samples " + ixOffsets.Length + " vs " + rec.sampleCount + " read");
            else for (int i = 0; i < ixOffsets.Length; i++) if (ixOffsets[i] != rec.sampleOffsets[i]) { bad.Add("offset of sample " + i); break; }
            if (ixKeys.Length != rec.objects.Count) bad.Add("objects " + ixKeys.Length + " vs " + rec.objects.Count + " read");
            else
            {
                for (int i = 0; i < ixKeys.Length; i++)
                {
                    var o = rec.objects[i];
                    bool same = o.keyCount == ixKeys[i] && (o.keyCount == 0 || (o.firstSample == ixFirst[i] && o.lastSample == ixLast[i]));
                    if (!same) { bad.Add("key statistics of object " + i); break; }
                }
            }
            if (ixFrames.Count != rec.particleTracks.Count) bad.Add("particle systems " + ixFrames.Count + " vs " + rec.particleTracks.Count + " read");
            else
            {
                foreach (var kv in ixFrames)
                {
                    SrecParticleTrack t;
                    bool same = rec.particleTracks.TryGetValue(kv.Key, out t) && t.count * 2 == kv.Value.Count;
                    for (int k = 0; same && k < t.count; k++) same = kv.Value[2 * k] == t.samples[k] && kv.Value[2 * k + 1] == t.entry[k];
                    if (!same) { bad.Add("particle frames of object " + kv.Key); break; }
                }
            }
            int common = Math.Min(ixStrings.Count, rec.strings.Count);
            for (int i = 0; i < common; i++) if (ixStrings[i] != rec.strings[i]) { bad.Add("string " + i); break; }
            if (ixStrings.Count < rec.strings.Count) bad.Add("strings " + ixStrings.Count + " vs " + rec.strings.Count + " inline");
            for (int i = rec.strings.Count; i < ixStrings.Count; i++) rec.strings.Add(ixStrings[i]);
            if (bad.Count > 0) report.Warn(path, "INDX does not match the samples: " + string.Join(", ", bad.ToArray()));
        }

        static bool ReadFully(Stream s, byte[] dst, int n)
        {
            int got = 0;
            while (got < n)
            {
                int k = s.Read(dst, got, n - got);
                if (k <= 0) return false;
                got += k;
            }
            return true;
        }
    }
}
