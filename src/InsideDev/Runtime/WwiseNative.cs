using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace InsideDev
{
    // Managed side of the native WwiseTracer that lives in the version.dll proxy.
    // The proxy only observes, timestamps and queues fixed-size records; everything else (naming, database, UI)
    // happens here, on the main thread. If the running version.dll is older and has no tracer, Available = false
    // and the rest of the editor keeps working.
    public static class WwiseNative
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr mod, string name);
        [DllImport("kernel32")] static extern bool QueryPerformanceCounter(out long v);
        [DllImport("kernel32")] static extern bool QueryPerformanceFrequency(out long v);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Void();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_SetFrame(int f);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Int(int v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Buf(IntPtr buf, int n);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Info(int i, IntPtr buf, int len, IntPtr info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_SetI(int v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_None();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Rule(int kind, uint key, uint val);
        static D_SetI pRuleEnforce; static D_None pRuleClear; static D_Rule pRuleAdd; static D_Buf pRuleStats;
        static D_Void pVersion; static D_SetFrame pSetFrame; static D_Int pSetLevel;
        static D_Buf pDrain, pStats, pPath; static D_Info pHook, pExport;

        public static bool Available, tried;
        public static int NativeVersion;
        public static string Problem = "";
        public const int RecSize = 160;
        const int Batch = 2048;
        static IntPtr buf, sbuf, info;
        static long qpcFreq = 1;

        public static bool Init()
        {
            if (tried) return Available;
            tried = true;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Application.dataPath);
                string path = System.IO.Path.Combine(dir, "version.dll");
                IntPtr m = LoadLibraryW(path);   // already loaded: returns our proxy, not System32's copy
                if (m == IntPtr.Zero) { Problem = "could not open " + path; return false; }
                IntPtr v = GetProcAddress(m, "WT_Version");
                if (v == IntPtr.Zero) { Problem = "version.dll has no WwiseTracer (older loader build - replace version.dll and restart)"; return false; }
                pVersion = F<D_Void>(v);
                pSetFrame = F<D_SetFrame>(GetProcAddress(m, "WT_SetFrame"));
                pSetLevel = F<D_Int>(GetProcAddress(m, "WT_SetLevel"));
                pDrain = F<D_Buf>(GetProcAddress(m, "WT_Drain"));
                pStats = F<D_Buf>(GetProcAddress(m, "WT_Stats"));
                pPath = F<D_Buf>(GetProcAddress(m, "WT_ModulePath"));
                pHook = F<D_Info>(GetProcAddress(m, "WT_HookInfo"));
                pExport = F<D_Info>(GetProcAddress(m, "WT_Export"));
                // native audio rules (loader v3+); absent in older version.dll builds
                pRuleEnforce = F<D_SetI>(GetProcAddress(m, "WT_RuleEnforce"));
                pRuleClear = F<D_None>(GetProcAddress(m, "WT_RuleClear"));
                pRuleAdd = F<D_Rule>(GetProcAddress(m, "WT_RuleAdd"));
                pRuleStats = F<D_Buf>(GetProcAddress(m, "WT_RuleStats"));
                NativeVersion = pVersion();
                buf = Marshal.AllocHGlobal(RecSize * Batch);
                sbuf = Marshal.AllocHGlobal(512);
                info = Marshal.AllocHGlobal(64);
                QueryPerformanceFrequency(out qpcFreq);
                Available = true;
                DevLog.Write("[WWISE] native tracer v" + NativeVersion + " connected; " + Stats().Summary());
            }
            catch (Exception e) { Problem = e.Message; DevLog.Error("WwiseNative.Init", e); }
            return Available;
        }

        static T F<T>(IntPtr p) where T : class { return p == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T)); }

        public static long Now() { long v; QueryPerformanceCounter(out v); return v; }
        public static double QpcToSeconds(long delta) { return (double)delta / qpcFreq; }

        // ---------------------------------------------------------------- opt-in native audio rules (loader v3)
        public static bool RulesSupported { get { return Available && pRuleEnforce != null && pRuleClear != null && pRuleAdd != null; } }
        public static void RuleEnforce(bool on) { if (RulesSupported) pRuleEnforce(on ? 1 : 0); }
        public static void RuleClear() { if (RulesSupported) pRuleClear(); }
        public static bool RuleAdd(int kind, uint key, uint val) { return RulesSupported && pRuleAdd(kind, key, val) != 0; }
        // enforce, rules, nuked, muted, replaced, forced
        public static long[] RuleStats()
        {
            var v = new long[6];
            if (!Available || pRuleStats == null) return v;
            int n = pRuleStats(sbuf, 6);
            Marshal.Copy(sbuf, v, 0, Math.Min(n, 6));
            return v;
        }

        public static int SetLevel(int l) { return Available && pSetLevel != null ? pSetLevel(l) : -1; }
        public static int Level { get { return Available ? (int)Stats().level : 0; } }

        public struct NativeStats
        {
            public long total, head, tail, dropped, resolvedAll, resolvedHooked, level, akBase, akSize, akStamp, akExports, render, cbItems, cbBad, hookCount, enabled;
            public string Summary()
            {
                return akBase == 0 ? "AkSoundEngine.dll not seen yet (entry points resolve on first use)"
                    : "AkSoundEngine.dll base 0x" + akBase.ToString("X") + ", " + resolvedHooked + "/" + hookCount + " watched entry points resolved (" + resolvedAll + " CSharp_* resolved in total), " + total + " records, " + dropped + " dropped";
            }
        }

        public static NativeStats Stats()
        {
            var s = new NativeStats();
            if (!Available) return s;
            int n = pStats(sbuf, 16);
            long[] v = new long[16];
            Marshal.Copy(sbuf, v, 0, Math.Min(n, 16));
            s.total = v[0]; s.head = v[1]; s.tail = v[2]; s.dropped = v[3]; s.resolvedAll = v[4]; s.resolvedHooked = v[5]; s.level = v[6];
            s.akBase = v[7]; s.akSize = v[8]; s.akStamp = v[9]; s.akExports = v[10]; s.render = v[11]; s.cbItems = v[12]; s.cbBad = v[13]; s.hookCount = v[14]; s.enabled = v[15];
            return s;
        }

        public static string ModulePath()
        {
            if (!Available) return "";
            pPath(sbuf, 512);
            return Marshal.PtrToStringAnsi(sbuf) ?? "";
        }

        public struct HookInfo { public string name; public bool resolved; public long calls, orig, wrapper; }
        public static bool GetHook(int i, out HookInfo h)
        {
            h = new HookInfo();
            if (!Available || pHook(i, sbuf, 512, info) == 0) return false;
            h.name = Marshal.PtrToStringAnsi(sbuf);
            h.resolved = Marshal.ReadInt64(info, 0) != 0; h.calls = Marshal.ReadInt64(info, 8);
            h.orig = Marshal.ReadInt64(info, 16); h.wrapper = Marshal.ReadInt64(info, 24);
            return true;
        }
        public static bool GetExport(int i, out string name, out long addr)
        {
            name = null; addr = 0;
            if (!Available || pExport(i, sbuf, 512, info) == 0) return false;
            name = Marshal.PtrToStringAnsi(sbuf); addr = Marshal.ReadInt64(info, 0);
            return true;
        }

        // ---------------------------------------------------------------- draining
        public struct Rec
        {
            public int type, api, thread, frame; public long qpc;
            public uint gameObj, eventId, playingId, groupId, valueId; public float fvalue; public uint bankId, cbType; public long cookie;
            public int ret, i1, i2; public uint flags; public Vector3 pos; public string name;
        }

        public static float lastDrainMs; public static int lastDrained, maxDrained;
        static readonly System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();

        // called once per frame on the main thread
        public static void Tick(Action<Rec> sink)
        {
            if (!Available) return;
            sw.Reset(); sw.Start();
            pSetFrame(Time.frameCount);
            int total = 0;
            for (int pass = 0; pass < 8; pass++)
            {
                int n = pDrain(buf, Batch);
                for (int i = 0; i < n; i++)
                {
                    IntPtr p = new IntPtr(buf.ToInt64() + (long)i * RecSize);
                    var r = new Rec();
                    r.type = Marshal.ReadInt16(p, 4) & 0xFFFF; r.api = Marshal.ReadInt16(p, 6) & 0xFFFF;
                    r.thread = Marshal.ReadInt32(p, 8); r.frame = Marshal.ReadInt32(p, 12); r.qpc = Marshal.ReadInt64(p, 16);
                    r.gameObj = (uint)Marshal.ReadInt32(p, 24); r.eventId = (uint)Marshal.ReadInt32(p, 28); r.playingId = (uint)Marshal.ReadInt32(p, 32);
                    r.groupId = (uint)Marshal.ReadInt32(p, 36); r.valueId = (uint)Marshal.ReadInt32(p, 40);
                    r.fvalue = BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(p, 44)), 0);
                    r.bankId = (uint)Marshal.ReadInt32(p, 48); r.cbType = (uint)Marshal.ReadInt32(p, 52); r.cookie = Marshal.ReadInt64(p, 56);
                    r.ret = Marshal.ReadInt32(p, 64); r.i1 = Marshal.ReadInt32(p, 68); r.i2 = Marshal.ReadInt32(p, 72); r.flags = (uint)Marshal.ReadInt32(p, 76);
                    if (r.type == 17 || r.type == 18)
                        r.pos = new Vector3(Fl(p, 80), Fl(p, 84), Fl(p, 88));
                    if (Marshal.ReadInt16(p, 96) != 0) r.name = Marshal.PtrToStringUni(new IntPtr(p.ToInt64() + 96));
                    try { sink(r); } catch (Exception e) { DevLog.Error("wwise sink", e); }
                }
                total += n;
                if (n < Batch) break;
            }
            sw.Stop();
            lastDrained = total; if (total > maxDrained) maxDrained = total;
            lastDrainMs = (float)sw.Elapsed.TotalMilliseconds;
        }
        static float Fl(IntPtr p, int off) { return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(p, off)), 0); }
    }
}
