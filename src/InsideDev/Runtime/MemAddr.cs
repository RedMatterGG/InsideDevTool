using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Memory addresses for Cheat Engine.
    // Unity 5.0 runs Mono 2.x with the Boehm GC, which does not move objects: the address of a script object stays
    // valid while the object lives (it changes on every game start / level load). Script fields are at
    //   object address + mono_field_get_offset(field)       (the offset includes the 16-byte object header)
    // The object address comes from a two-instruction DynamicMethod (ldarg.0; conv.i; ret) that hands back the
    // reference as a number. Before any address is shown, the whole scheme is self-checked: values read from memory
    // at the computed addresses must equal the values read through reflection; if not, the feature switches itself
    // off with the reason.
    // Unity's own (native) objects: UnityEngine.Object.m_CachedPtr is the native object; the Transform's local
    // position offset inside it is found once by searching the native memory for the known position.
    public static class MemAddr
    {
        [DllImport("mono")] static extern int mono_field_get_offset(IntPtr field);
        [DllImport("mono")] static extern uint mono_object_get_size(IntPtr obj);
        [DllImport("mono")] static extern IntPtr mono_object_get_class(IntPtr obj);
        [DllImport("mono")] static extern IntPtr mono_class_get_name(IntPtr klass);
        [DllImport("mono")] static extern IntPtr mono_class_get_namespace(IntPtr klass);
        [DllImport("mono")] static extern IntPtr mono_domain_get();
        [DllImport("mono")] static extern IntPtr mono_jit_info_table_find(IntPtr domain, IntPtr addr);
        [DllImport("mono")] static extern IntPtr mono_jit_info_get_method(IntPtr ji);
        [DllImport("mono")] static extern IntPtr mono_method_full_name(IntPtr method, int signature);
        [DllImport("kernel32")] static extern bool GetModuleHandleExW(uint flags, IntPtr addr, out IntPtr module);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern uint GetModuleFileNameW(IntPtr module, StringBuilder name, uint size);
        // "INSIDE.exe+0x1234" for an address inside a loaded module (exe or dll)
        public static string ModuleOf(long addr)
        {
            try
            {
                IntPtr m;
                if (!GetModuleHandleExW(0x4 | 0x2, (IntPtr)addr, out m) || m == IntPtr.Zero) return null;   // FROM_ADDRESS | UNCHANGED_REFCOUNT
                var sb = new StringBuilder(260); GetModuleFileNameW(m, sb, 260);
                return System.IO.Path.GetFileName(sb.ToString()) + "+0x" + (addr - m.ToInt64()).ToString("X");
            }
            catch { return null; }
        }
        [DllImport("kernel32")] static extern IntPtr VirtualQuery(IntPtr addr, out MBI info, IntPtr len);
        [StructLayout(LayoutKind.Sequential)] struct MBI { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public IntPtr RegionSize; public uint State, Protect, Type; }

        public static bool Enabled { get { return EditorState.Get("mem.show", false); } set { EditorState.Set("mem.show", value); } }
        public static string status = "not checked";
        static int ok;   // 0 unknown, 1 working, -1 failed
        static Func<object, IntPtr> addrOf;
        static FieldInfo fCachedPtr;

        public static bool Ready { get { if (ok == 0) SelfCheck(); return ok == 1; } }

        public static IntPtr AddressOf(object o)
        {
            if (o == null || !Ready) return IntPtr.Zero;
            return addrOf(o);
        }

        static void SelfCheck()
        {
            try
            {
                var dm = new DynamicMethod("InsideDev_AddrOf", typeof(IntPtr), new[] { typeof(object) }, typeof(MemAddr).Module, true);
                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Ret);
                addrOf = (Func<object, IntPtr>)dm.CreateDelegate(typeof(Func<object, IntPtr>));
                fCachedPtr = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
                // probe object with known values in every primitive layout we map
                var p = new Probe { i = 0x1234ABCD, f = 3.25f, b = true, l = 0x0102030405060708L, v = new Vector3(1.5f, -2.5f, 7f) };
                IntPtr a = addrOf(p);
                if (a == IntPtr.Zero) throw new Exception("address 0");
                int oi = Offset(typeof(Probe).GetField("i")), of = Offset(typeof(Probe).GetField("f")), ob = Offset(typeof(Probe).GetField("b")), ol = Offset(typeof(Probe).GetField("l")), ov = Offset(typeof(Probe).GetField("v"));
                bool good = Marshal.ReadInt32(a, oi) == p.i && ReadFloat(a, of) == p.f && Marshal.ReadByte(a, ob) == 1 && Marshal.ReadInt64(a, ol) == p.l
                            && ReadFloat(a, ov) == 1.5f && ReadFloat(a, ov + 4) == -2.5f && ReadFloat(a, ov + 8) == 7f;
                uint sz = mono_object_get_size(a);
                if (!good) throw new Exception("values at the computed addresses do not match (offsets " + oi + "/" + of + "/" + ob + "/" + ol + "/" + ov + ")");
                GC.KeepAlive(p);
                ok = 1; status = "ok (self-check passed: object header " + (oi < of ? oi : of) + " bytes, probe size " + sz + ")";
            }
            catch (Exception e) { ok = -1; status = "off: self-check failed - " + e.Message; DevLog.Write("[mem] " + status); }
        }
        sealed class Probe { public int i; public float f; public bool b; public long l; public Vector3 v; }

        public static void Copy(string s) { Clipboard.Set(s); }

        public static int Offset(FieldInfo f) { return mono_field_get_offset(f.FieldHandle.Value); }
        public static float ReadFloat(IntPtr a, int off) { return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(a, off)), 0); }
        public static IntPtr NativePtr(UnityEngine.Object o) { return o == null || fCachedPtr == null ? IntPtr.Zero : (IntPtr)fCachedPtr.GetValue(o); }
        public static string Hex(IntPtr p) { return "0x" + p.ToInt64().ToString("X"); }
        public static string Hex(long p) { return "0x" + p.ToString("X"); }

        // Cheat Engine value type for a field type (null = not a plain value)
        public static string CeType(Type t, out int size)
        {
            size = 0;
            if (t.IsEnum) t = Enum.GetUnderlyingType(t);
            if (t == typeof(int) || t == typeof(uint)) { size = 4; return "4 Bytes"; }
            if (t == typeof(float)) { size = 4; return "Float"; }
            if (t == typeof(double)) { size = 8; return "Double"; }
            if (t == typeof(long) || t == typeof(ulong)) { size = 8; return "8 Bytes"; }
            if (t == typeof(short) || t == typeof(ushort) || t == typeof(char)) { size = 2; return "2 Bytes"; }
            if (t == typeof(bool) || t == typeof(byte) || t == typeof(sbyte)) { size = 1; return "Byte"; }
            if (t == typeof(Vector2)) { size = 8; return "Float x2"; }
            if (t == typeof(Vector3)) { size = 12; return "Float x3"; }
            if (t == typeof(Vector4) || t == typeof(Quaternion) || t == typeof(Color)) { size = 16; return "Float x4"; }
            if (!t.IsValueType) { size = IntPtr.Size; return t == typeof(string) ? "8 Bytes (pointer to string; text at +0x14)" : "8 Bytes (pointer)"; }
            return null;
        }

        public sealed class Row { public string name, ceType, value; public long addr; public int offset, size; public bool isPointer; }
        public sealed class Block { public string title; public long addr; public uint size; public long native; public readonly List<Row> rows = new List<Row>(); }

        // every script field of every component on the object, plus the native pointers and the Transform position
        public static List<Block> Describe(GameObject go)
        {
            var res = new List<Block>();
            if (go == null || !Ready) return res;
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var b = new Block { title = c.GetType().Name, native = NativePtr(c).ToInt64() };
                if (c is MonoBehaviour)
                {
                    IntPtr a = AddressOf(c); b.addr = a.ToInt64();
                    try { b.size = mono_object_get_size(a); } catch { }
                    var fields = new List<FieldInfo>();
                    for (var t = c.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) fields.Add(f);
                    foreach (var f in fields)
                    {
                        int off; try { off = Offset(f); } catch { continue; }
                        int size; string ce = CeType(f.FieldType, out size);
                        if (ce == null) { ce = "struct " + f.FieldType.Name; size = 0; }
                        string val; try { var v = f.GetValue(c); val = v == null ? "null" : v is UnityEngine.Object ? ((UnityEngine.Object)v) != null ? ((UnityEngine.Object)v).name : "null (destroyed)" : v.ToString(); } catch { val = "?"; }
                        if (val.Length > 40) val = val.Substring(0, 39) + "…";
                        b.rows.Add(new Row { name = f.Name, ceType = ce, value = val, addr = b.addr + off, offset = off, size = size, isPointer = !f.FieldType.IsValueType });
                    }
                    b.rows.Sort((x, y) => x.offset.CompareTo(y.offset));
                }
                var tr = c as Transform;
                if (tr != null && b.native != 0)
                {
                    int po = TransformPosOffset(tr);
                    if (po >= 0) b.rows.Add(new Row { name = "localPosition (native)", ceType = "Float x3", value = tr.localPosition.ToString("F3"), addr = b.native + po, offset = po, size = 12 });
                    else b.rows.Add(new Row { name = "localPosition (native)", ceType = "-", value = "offset not found", addr = 0 });
                }
                res.Add(b);
            }
            return res;
        }

        // native Transform: search its first 0x200 bytes for the three floats of localPosition (once, then reused)
        static int posOffset = -2;
        public static int TransformPosOffset(Transform t)
        {
            if (posOffset >= -1) return posOffset;
            IntPtr n = NativePtr(t); if (n == IntPtr.Zero) return -1;
            Vector3 lp = t.localPosition;
            if (lp.sqrMagnitude < 1e-6f) return -1;   // need a non-zero position to recognise it; try another object
            if (!Readable(n, 0x200)) { posOffset = -1; return -1; }
            for (int o = 0; o <= 0x200 - 12; o += 4)
                if (ReadFloat(n, o) == lp.x && ReadFloat(n, o + 4) == lp.y && ReadFloat(n, o + 8) == lp.z) { posOffset = o; DevLog.Write("[mem] native Transform localPosition at +0x" + o.ToString("X")); return o; }
            return -1;
        }

        public static bool Readable(IntPtr a, int len)
        {
            MBI m;
            if (VirtualQuery(a, out m, (IntPtr)Marshal.SizeOf(typeof(MBI))) == IntPtr.Zero) return false;
            if (m.State != 0x1000 || (m.Protect & 0xEE) == 0 || (m.Protect & 0x100) != 0) return false;   // MEM_COMMIT, readable, not guard
            return a.ToInt64() + len <= m.BaseAddress.ToInt64() + m.RegionSize.ToInt64();
        }

        // "what is at this address": scripts of loaded objects (script memory) and native Unity objects (first 0x200 bytes)
        public static string WhatIs(long addr, out GameObject owner)
        {
            owner = null;
            if (!Ready) return "memory addresses are off: " + status;
            string best = null; long bestDist = long.MaxValue;
            foreach (var r in ObjectDatabase.all)
            {
                if (r.go == null) continue;
                foreach (var c in r.go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    if (c is MonoBehaviour)
                    {
                        long a = AddressOf(c).ToInt64(); if (addr < a) goto native;
                        uint sz; try { sz = mono_object_get_size((IntPtr)a); } catch { goto native; }
                        if (addr >= a + sz) goto native;
                        owner = r.go;
                        string field = FieldAt(c.GetType(), (int)(addr - a));
                        return Hex(addr) + " is inside script " + c.GetType().Name + " (object " + Hex(a) + ", " + sz + " bytes, +0x" + (addr - a).ToString("X") + ")" + (field != null ? "  field " + field : addr - a < 16 ? "  (object header: class/lock words)" : "  (padding between fields)") + "\n  on " + r.path;
                    }
                native:
                    long n = NativePtr(c).ToInt64();
                    if (n != 0 && addr >= n && addr < n + 0x200 && addr - n < bestDist) { bestDist = addr - n; owner = r.go; string known = c is Transform && posOffset >= 0 && addr - n >= posOffset && addr - n < posOffset + 12 ? "  = localPosition." + "xyz"[(int)(addr - n - posOffset) / 4] : "";
                        best = Hex(addr) + " is probably inside Unity's native " + c.GetType().Name + " (native object " + Hex(n) + ", +0x" + (addr - n).ToString("X") + known + (known.Length == 0 ? "; native object sizes are not known, so this is the nearest start below it" : "") + ")\n  on " + r.path; }
                }
            }
            if (best != null) return best;
            string m = MethodAt(addr);
            if (m != null) return Hex(addr) + " is game code: " + m;
            string mod = ModuleOf(addr);
            if (mod != null) return Hex(addr) + " is " + mod + "  (native code/data of that module: no names available - for game logic look at script code instead)";
            return Hex(addr) + ": not inside a script object of the loaded objects, a native Unity object start, compiled game code or a loaded module (likely heap memory)";
        }

        static string FieldAt(Type t, int off)
        {
            string best = null; int bo = -1;
            for (var x = t; x != null && x != typeof(MonoBehaviour); x = x.BaseType)
                foreach (var f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    int o; try { o = Offset(f); } catch { continue; }
                    int size; CeType(f.FieldType, out size); if (size == 0) size = 4;
                    if (off >= o && o > bo && (off < o + size || f.FieldType.IsValueType && !f.FieldType.IsPrimitive)) { bo = o; best = f.Name + " (" + f.FieldType.Name + ", +0x" + o.ToString("X") + (off != o ? ", byte " + (off - o) + " of it" : "") + ")"; }
                }
            return best;
        }

        // compiled (JIT) game code address -> "Type:Method (args)"
        public static string MethodAt(long addr)
        {
            try
            {
                IntPtr ji = mono_jit_info_table_find(mono_domain_get(), (IntPtr)addr);
                if (ji == IntPtr.Zero) return null;
                IntPtr m = mono_jit_info_get_method(ji); if (m == IntPtr.Zero) return null;
                IntPtr s = mono_method_full_name(m, 1);
                return s == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(s);
            }
            catch (Exception e) { return "(lookup failed: " + e.Message + ")"; }
        }

        // Cheat Engine address-list XML for a block (paste into CE's address list, or loaded by the link script)
        public static string CheatTableXml(GameObject go, List<Block> blocks)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<CheatTable><CheatEntries>\n");
            int id = 1;
            sb.Append("<CheatEntry><ID>").Append(id++).Append("</ID><Description>\"").Append(Esc(go.name)).Append("\"</Description><GroupHeader>1</GroupHeader><CheatEntries>\n");
            foreach (var b in blocks)
                foreach (var r in b.rows)
                {
                    if (r.addr == 0) continue;
                    string vt = r.ceType.StartsWith("Float") ? "Float" : r.ceType.StartsWith("4 Bytes") ? "4 Bytes" : r.ceType.StartsWith("8 Bytes") ? "8 Bytes" : r.ceType == "Double" ? "Double" : r.ceType == "2 Bytes" ? "2 Bytes" : r.ceType == "Byte" ? "Byte" : null;
                    if (vt == null) continue;
                    int n = r.ceType.EndsWith("x2") ? 2 : r.ceType.EndsWith("x3") ? 3 : r.ceType.EndsWith("x4") ? 4 : 1;
                    for (int k = 0; k < n; k++)
                        sb.Append("<CheatEntry><ID>").Append(id++).Append("</ID><Description>\"").Append(Esc(b.title + "." + r.name + (n > 1 ? "." + "xyzw"[k] : ""))).Append("\"</Description><VariableType>").Append(vt).Append("</VariableType>")
                          .Append(r.isPointer ? "<ShowAsHex>1</ShowAsHex>" : "").Append("<Address>").Append((r.addr + k * 4).ToString("X")).Append("</Address></CheatEntry>\n");
                }
            sb.Append("</CheatEntries></CheatEntry>\n</CheatEntries></CheatTable>\n");
            return sb.ToString();
        }
        static string Esc(string s) { return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "'"); }
    }
}
