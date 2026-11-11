using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace InsideDev.Play
{
    // PfmXDxgi.cs (owner TIME): the monitors of the default graphics adapter in DXGI order, which is the order of ffmpeg
    // ddagrab's output_idx (ddagrab creates its device on the default adapter and takes IDXGIAdapter::EnumOutputs(idx)).
    // Read through the COM vtables directly (CreateDXGIFactory1, IDXGIFactory::EnumAdapters(0), IDXGIAdapter::EnumOutputs,
    // IDXGIOutput::GetDesc), so no COM interop support of the runtime is needed. No dependency on Unity or the core.
    internal static class Dxgi
    {
        public struct Output
        {
            public int index, left, top, right, bottom;
            public IntPtr monitor;
            public bool attached;
        }

        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumFn(IntPtr self, uint index, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DescFn(IntPtr self, IntPtr desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);

        const int SlotRelease = 2, SlotEnumAdapters = 7, SlotEnumOutputs = 7, SlotOutputDesc = 7, DescBytes = 96;

        static Delegate Fn(IntPtr obj, int slot, Type t)
        {
            IntPtr vt = Marshal.ReadIntPtr(obj);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, slot * IntPtr.Size), t);
        }

        static void Release(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return;
            try { ((ReleaseFn)Fn(obj, SlotRelease, typeof(ReleaseFn)))(obj); } catch { }
        }

        // Any thread. null with error when DXGI could not be asked (the caller falls back to probing with ffmpeg).
        public static List<Output> Outputs(out string error)
        {
            error = null;
            IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero, desc = IntPtr.Zero;
            try
            {
                var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                int hr = CreateDXGIFactory1(ref iid, out factory);
                if (hr < 0 || factory == IntPtr.Zero) { error = "CreateDXGIFactory1 0x" + hr.ToString("x8"); return null; }
                hr = ((EnumFn)Fn(factory, SlotEnumAdapters, typeof(EnumFn)))(factory, 0, out adapter);
                if (hr < 0 || adapter == IntPtr.Zero) { error = "EnumAdapters 0x" + hr.ToString("x8"); return null; }
                var list = new List<Output>();
                var enumOutputs = (EnumFn)Fn(adapter, SlotEnumOutputs, typeof(EnumFn));
                desc = Marshal.AllocHGlobal(DescBytes);
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr output;
                    if (enumOutputs(adapter, i, out output) < 0 || output == IntPtr.Zero) break;
                    try
                    {
                        for (int b = 0; b < DescBytes; b += 4) Marshal.WriteInt32(desc, b, 0);
                        if (((DescFn)Fn(output, SlotOutputDesc, typeof(DescFn)))(output, desc) >= 0)
                            list.Add(new Output
                            {
                                index = (int)i,
                                left = Marshal.ReadInt32(desc, 64), top = Marshal.ReadInt32(desc, 68), right = Marshal.ReadInt32(desc, 72), bottom = Marshal.ReadInt32(desc, 76),
                                attached = Marshal.ReadInt32(desc, 80) != 0,
                                monitor = Marshal.ReadIntPtr(desc, 88)
                            });
                    }
                    finally { Release(output); }
                }
                return list;
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return null; }
            finally
            {
                if (desc != IntPtr.Zero) Marshal.FreeHGlobal(desc);
                Release(adapter);
                Release(factory);
            }
        }
    }
}
