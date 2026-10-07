using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace InsideDev
{
    // Optional add-ons in separate DLLs, so the game tool does not carry features that only some people use.
    // Every _mod\InsideDev.*.dll is loaded at boot; each public class implementing IInsideDevExtension is created,
    // started once, ticked every frame, and asked for bridge / console commands it does not recognise in the core.
    // Example: InsideDev.Mcp.dll (the MCP server for AI assistants).
    public interface IInsideDevExtension
    {
        string Name { get; }
        void Start(DevCore core);
        void Tick(DevCore core);
        // return null when the command is not for this extension
        string Command(List<string> args, DevCore core);
    }

    public static class Extensions
    {
        public static readonly List<IInsideDevExtension> loaded = new List<IInsideDevExtension>();
        public static readonly List<string> problems = new List<string>();
        static bool done;

        public static string Dir { get { return Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"); } }

        public static void LoadAll(DevCore core)
        {
            if (done) return;
            done = true;
            // extensions reference "InsideDev": hand them the copy that is already loaded
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var want = new AssemblyName(e.Name).Name;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == want) return a;
                return null;
            };
            LoadNew(core);
        }

        static readonly HashSet<string> loadedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // loads every _mod\InsideDev.*.dll not loaded yet (at boot, and with the "ext load" command without restarting)
        public static string LoadNew(DevCore core)
        {
            string[] files;
            try { files = Directory.GetFiles(Dir, "InsideDev.*.dll"); } catch (Exception e) { problems.Add("extension folder: " + e.Message); return "extension folder: " + e.Message; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            int before = loaded.Count;
            foreach (var f in files)
            {
                if (!loadedFiles.Add(Path.GetFileName(f))) continue;
                try
                {
                    var asm = Assembly.LoadFrom(f);
                    int n = 0;
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.IsAbstract || !typeof(IInsideDevExtension).IsAssignableFrom(t)) continue;
                        var ext = (IInsideDevExtension)Activator.CreateInstance(t);
                        try { ext.Start(core); } catch (Exception e) { DevLog.Error("[ext] start " + ext.Name, e); }
                        loaded.Add(ext); n++;
                        DevLog.Write("[ext] loaded " + ext.Name + " from " + Path.GetFileName(f));
                    }
                    if (n == 0) problems.Add(Path.GetFileName(f) + ": no extension class");
                }
                catch (Exception e) { problems.Add(Path.GetFileName(f) + ": " + e.Message); DevLog.Error("[ext] " + Path.GetFileName(f), e); }
            }
            return (loaded.Count - before) + " new extension(s) loaded\n" + Summary();
        }

        public static void Tick(DevCore core)
        {
            for (int i = 0; i < loaded.Count; i++)
            {
                try { loaded[i].Tick(core); } catch (Exception e) { DevLog.Error("[ext] " + loaded[i].Name, e); }
            }
        }

        public static string Command(List<string> a, DevCore core)
        {
            foreach (var x in loaded)
            {
                string r;
                try { r = x.Command(a, core); } catch (Exception e) { r = "ERROR in " + x.Name + ": " + e.Message; }
                if (r != null) return r;
            }
            return null;
        }

        public static string Summary()
        {
            var s = loaded.Count + " extension(s)";
            foreach (var x in loaded) s += "\n  " + x.Name;
            foreach (var p in problems) s += "\n  problem: " + p;
            return s;
        }
    }
}
