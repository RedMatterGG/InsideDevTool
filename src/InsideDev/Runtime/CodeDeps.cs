using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace InsideDev
{
    // Code dependencies that no serialized field shows: a script reaching another object through a static
    // singleton (WristSecret.instance, PersistentBehaviour<X>.instance, X.Instance) or FindObjectOfType<X>()
    // and then reading / writing / calling something on it. Example: SecretJoystick.CheckPassword reads
    // WristSecret.instance.isActive, SecretPodLarge.OnLightTurnOff calls WristSecret.instance.Activate().
    //
    // Found by a static walk of the IL of Assembly-CSharp(-firstpass) (budgeted, background), then turned into
    // graph edges at runtime by resolving the singleton to the live object. Provenance: CODE (static analysis) -
    // the dependency exists in code; whether that code path runs is not implied.
    public static class CodeDeps
    {
        public sealed class Dep
        {
            public Type from, target; public string method, via, member, access;   // access: reads / writes / calls / uses
            public FieldInfo staticField; public PropertyInfo staticProp; public MethodInfo staticGetter;
        }

        static readonly Dictionary<Type, List<Dep>> byType = new Dictionary<Type, List<Dep>>();
        public static bool done; public static int methods, deps;
        static List<MethodBase> queue; static int qi;
        static readonly Dictionary<short, OpCode> ops = new Dictionary<short, OpCode>();

        public static List<Dep> For(Type t)
        {
            List<Dep> res = null;
            for (var x = t; x != null && x != typeof(MonoBehaviour); x = x.BaseType)
            {
                List<Dep> l;
                if (byType.TryGetValue(x, out l)) { if (res == null) res = new List<Dep>(); res.AddRange(l); }
            }
            return res;
        }

        public static void Tick()
        {
            if (done) return;
            try
            {
                if (queue == null) Init();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (qi < queue.Count && sw.Elapsed.TotalMilliseconds < 1.0) { Scan(queue[qi++]); methods++; }
                if (qi >= queue.Count)
                {
                    done = true; queue = null;
                    DevLog.Write("[code deps] " + deps + " singleton / lookup dependencies in " + byType.Count + " script types (" + methods + " methods)");
                    ReferenceIndex.RequeueTypes(new HashSet<Type>(byType.Keys));
                }
            }
            catch (Exception e) { done = true; DevLog.Error("code deps", e); }
        }

        static void Init()
        {
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var o = f.GetValue(null);
                if (o is OpCode) { var oc = (OpCode)o; ops[oc.Value] = oc; }
            }
            queue = new List<MethodBase>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = asm.GetName().Name;
                if (n != "Assembly-CSharp" && n != "Assembly-CSharp-firstpass") continue;
                Type[] ts; try { ts = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types; }
                foreach (var t in ts)
                {
                    if (t == null || !typeof(Component).IsAssignableFrom(t)) continue;
                    const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                    foreach (var m in t.GetMethods(F)) queue.Add(m);
                }
            }
        }

        static int OperandSize(OperandType t, byte[] il, int p)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8: case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return 4 + 4 * (il[p] | il[p + 1] << 8 | il[p + 2] << 16 | il[p + 3] << 24);
                default: return 4;
            }
        }

        static void Scan(MethodBase m)
        {
            byte[] il;
            try { var b = m.GetMethodBody(); if (b == null) return; il = b.GetILAsByteArray(); } catch { return; }
            if (il == null) return;
            var mod = m.Module;
            Type[] targs = null, margs = null;
            try { if (m.DeclaringType.IsGenericType) targs = m.DeclaringType.GetGenericArguments(); if (m.IsGenericMethod) margs = m.GetGenericArguments(); } catch { }
            Type pending = null; string via = null; FieldInfo pf = null; PropertyInfo pp = null; MethodInfo pg = null;
            int p = 0;
            while (p < il.Length)
            {
                short v = il[p];
                if (v == 0xFE && p + 1 < il.Length) { v = (short)(0xFE00 | il[p + 1]); p += 2; } else p += 1;
                OpCode oc;
                if (!ops.TryGetValue(v, out oc)) return;          // unknown opcode: stop this method (never guess)
                int size = OperandSize(oc.OperandType, il, p);
                int tok = size == 4 && p + 3 < il.Length ? il[p] | il[p + 1] << 8 | il[p + 2] << 16 | il[p + 3] << 24 : 0;
                Type found = null; string foundVia = null; FieldInfo ff = null; PropertyInfo fp = null; MethodInfo fg = null;
                try
                {
                    if (oc.OperandType == OperandType.InlineField)
                    {
                        var f = mod.ResolveField(tok, targs, margs);
                        if (oc.Value == OpCodes.Ldsfld.Value && f.IsStatic && typeof(Component).IsAssignableFrom(f.FieldType) && f.FieldType != m.DeclaringType)
                        { found = f.FieldType; foundVia = f.DeclaringType.Name + "." + f.Name; ff = f; }
                        else if (pending != null && !f.IsStatic && f.DeclaringType.IsAssignableFrom(pending))
                        { Record(m, pending, via, f.Name, oc.Value == OpCodes.Stfld.Value ? "writes" : "reads", pf, pp, pg); pending = null; }
                    }
                    else if (oc.OperandType == OperandType.InlineMethod)
                    {
                        var mb = mod.ResolveMethod(tok, targs, margs);
                        var mi = mb as MethodInfo;
                        if (mi != null && mi.IsStatic && mi.GetParameters().Length == 0 && typeof(Component).IsAssignableFrom(mi.ReturnType) && mi.ReturnType != m.DeclaringType && mi.Name.StartsWith("get_"))
                        {
                            found = mi.ReturnType; foundVia = mi.DeclaringType.Name + "." + mi.Name.Substring(4); fg = mi;
                            fp = mi.DeclaringType.GetProperty(mi.Name.Substring(4), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                        }
                        else if (mi != null && mi.Name == "FindObjectOfType" && mi.IsGenericMethod && typeof(Component).IsAssignableFrom(mi.ReturnType))
                        { found = mi.ReturnType; foundVia = "FindObjectOfType<" + mi.ReturnType.Name + ">"; }
                        else if (pending != null && !mb.IsStatic && mb.DeclaringType.IsAssignableFrom(pending) && mb.DeclaringType != typeof(UnityEngine.Object) && mb.DeclaringType != typeof(Component) && mb.DeclaringType != typeof(object))
                        {
                            string nm = mb.Name.StartsWith("get_") ? mb.Name.Substring(4) : mb.Name.StartsWith("set_") ? mb.Name.Substring(4) : mb.Name;
                            Record(m, pending, via, nm, mb.Name.StartsWith("get_") ? "reads" : mb.Name.StartsWith("set_") ? "writes" : "calls", pf, pp, pg);
                            pending = null;
                        }
                    }
                }
                catch { }
                if (found != null) { if (pending != null) Record(m, pending, via, null, "uses", pf, pp, pg); pending = found; via = foundVia; pf = ff; pp = fp; pg = fg; }
                else if (pending != null && oc.Value != OpCodes.Nop.Value && oc.Value != OpCodes.Dup.Value && oc.OperandType != OperandType.InlineField && oc.OperandType != OperandType.InlineMethod
                         && oc.Value != OpCodes.Ldc_I4_0.Value && oc.Value != OpCodes.Ldc_I4_1.Value && oc.Value != OpCodes.Ldnull.Value && !oc.Name.StartsWith("ldloc") && !oc.Name.StartsWith("ldarg"))
                { Record(m, pending, via, null, "uses", pf, pp, pg); pending = null; }
                p += size;
            }
            if (pending != null) Record(m, pending, via, null, "uses", pf, pp, pg);
        }

        static void Record(MethodBase m, Type target, string via, string member, string access, FieldInfo f, PropertyInfo prop, MethodInfo getter)
        {
            var t = m.DeclaringType;
            List<Dep> l;
            if (!byType.TryGetValue(t, out l)) { l = new List<Dep>(); byType[t] = l; }
            foreach (var d in l) if (d.target == target && d.member == member && d.access == access && d.method == m.Name) return;
            l.Add(new Dep { from = t, target = target, method = m.Name, via = via, member = member, access = access, staticField = f, staticProp = prop, staticGetter = getter });
            deps++;
        }

        // live object behind a dependency (singleton value, or the first object of that type)
        public static Component Resolve(Dep d)
        {
            try
            {
                if (d.staticField != null) return d.staticField.GetValue(null) as Component;
                if (d.staticProp != null) return d.staticProp.GetValue(null, null) as Component;
                if (d.staticGetter != null) return d.staticGetter.Invoke(null, null) as Component;
                return UnityEngine.Object.FindObjectOfType(d.target) as Component;
            }
            catch { return null; }
        }

        public static string Dump(string typeName)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in byType)
            {
                if (typeName != null && kv.Key.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) < 0 && !kv.Value.Exists(d => d.target.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                foreach (var d in kv.Value) sb.Append(kv.Key.Name).Append('.').Append(d.method).Append("  ").Append(d.access).Append(' ').Append(d.via).Append(d.member != null ? "." + d.member : "").Append('\n');
                if (sb.Length > 20000) break;
            }
            return (done ? "" : "(scan in progress: " + methods + " methods)\n") + (sb.Length > 0 ? sb.ToString() : "(none)");
        }
    }
}
