using System;
using System.Collections.Generic;
using System.Reflection;

namespace InsideDev.McpExt
{
    // HostDiscovery.cs (owner HOST): finds tool providers in every loaded InsideDev.*.dll (main thread). Extension
    // instances the core created (Extensions.loaded) that implement IMcpToolProvider are registered as they are
    // (PlayExtension also adds itself; McpRegistry.Add ignores the same instance twice). Other public, non-abstract
    // IMcpToolProvider classes with a parameterless constructor in InsideDev.* assemblies are created once. Runs at start
    // and whenever the core's extension list or problem list grows ("ext load").
    internal static class HostDiscovery
    {
        static int seenLoaded = -1, seenProblems = -1, seenAssemblies = -1;
        static readonly HashSet<Type> created = new HashSet<Type>();

        internal static void Check()
        {
            int l = Extensions.loaded.Count, p = Extensions.problems.Count;
            if (l == seenLoaded && p == seenProblems) return;
            seenLoaded = l; seenProblems = p;
            Scan();
        }

        internal static int Scan()
        {
            int added = 0;
            foreach (var ext in Extensions.loaded.ToArray())
            {
                var prov = ext as IMcpToolProvider;
                if (prov == null) continue;
                int before = McpRegistry.Providers().Count;
                McpRegistry.Add(prov);
                if (McpRegistry.Providers().Count > before) added++;
            }
            var own = typeof(McpRegistry).Assembly;
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            if (asms.Length == seenAssemblies) return added;
            seenAssemblies = asms.Length;
            foreach (var asm in asms)
            {
                if (ReferenceEquals(asm, own)) continue;
                string name;
                try { name = asm.GetName().Name; } catch { continue; }
                if (name == null || !name.StartsWith("InsideDev.", StringComparison.Ordinal)) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch (Exception e) { HostLog.Write("provider scan of " + name + ": " + e.Message); continue; }
                if (types == null) continue;
                foreach (var t in types)
                {
                    if (t == null || !t.IsClass || t.IsAbstract || !t.IsPublic) continue;
                    if (!typeof(IMcpToolProvider).IsAssignableFrom(t) || typeof(IInsideDevExtension).IsAssignableFrom(t)) continue;
                    if (t.GetConstructor(Type.EmptyTypes) == null || !created.Add(t)) continue;
                    try
                    {
                        McpRegistry.Add((IMcpToolProvider)Activator.CreateInstance(t));
                        added++;
                        HostLog.Write("provider " + t.FullName + " from " + name + " created");
                    }
                    catch (Exception e) { HostLog.Write("provider " + t.FullName + " from " + name + " failed: " + (e.InnerException ?? e).Message); }
                }
            }
            return added;
        }
    }
}
