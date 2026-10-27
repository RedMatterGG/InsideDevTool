using System;
using System.Collections.Generic;
using System.Threading;

namespace InsideDev.McpExt
{
    // HostRegistry.cs (owner HOST, signatures frozen): the list of tool providers. Thread-safe. The host's own dev tools
    // are one provider (DevTools.cs); InsideDev.Play.dll adds itself from PlayExtension.Start, and HostDiscovery adds
    // providers found in other loaded InsideDev.*.dll files (at start and after "ext load"). Tool names are unique
    // across all providers and profiles: on a clash the first registered provider wins and the clash is logged.
    // Every Add / Remove / Changed bumps Version, which the server turns into notifications/tools/list_changed.
    // Merged lists are cached per Version, so providers are asked for their lists only after a change (a provider whose
    // Tools() threw is asked again on the next request).
    public static class McpRegistry
    {
        sealed class Snap
        {
            public long version;
            public readonly List<ToolDef> tools = new List<ToolDef>();
            public readonly Dictionary<string, IMcpToolProvider> owner = new Dictionary<string, IMcpToolProvider>();
            public readonly List<ResourceDef> resources = new List<ResourceDef>();
            public readonly List<ResourceTemplateDef> templates = new List<ResourceTemplateDef>();
            public readonly List<PromptDef> prompts = new List<PromptDef>();
        }

        static readonly object gate = new object();
        static readonly List<IMcpToolProvider> providers = new List<IMcpToolProvider>();
        static readonly HashSet<string> reported = new HashSet<string>();
        static long version = 1;
        static Snap snap;
        internal const int InstructionsCap = 4000;

        public static void Add(IMcpToolProvider provider)
        {
            if (provider == null) return;
            string name = SafeName(provider);
            IMcpToolProvider replaced = null;
            lock (gate)
            {
                foreach (var p in providers) if (ReferenceEquals(p, provider)) return;
                foreach (var p in providers) if (SafeName(p) == name) { replaced = p; break; }
                if (replaced != null) providers.Remove(replaced);
                providers.Add(provider);
                Bump();
            }
            HostLog.Write("provider " + name + (replaced != null ? " replaced the earlier one" : " added"));
            Raise();
        }

        public static void Remove(IMcpToolProvider provider)
        {
            if (provider == null) return;
            bool removed;
            lock (gate) { removed = providers.Remove(provider); if (removed) Bump(); }
            if (removed) { HostLog.Write("provider " + SafeName(provider) + " removed"); Raise(); }
        }

        // a provider's tool / resource / prompt lists changed
        public static void Changed()
        {
            lock (gate) Bump();
            Raise();
        }

        public static long Version { get { return Interlocked.Read(ref version); } }
        public static event Action ListChanged;

        public static List<IMcpToolProvider> Providers() { lock (gate) return new List<IMcpToolProvider>(providers); }

        // merged view per profile (play, dev, all), in provider order then registration order
        public static List<ToolDef> ToolsFor(string profile)
        {
            var s = Current();
            var l = new List<ToolDef>();
            foreach (var t in s.tools) if (McpProfile.Includes(profile, t.profiles)) l.Add(t);
            return l;
        }

        public static IMcpToolProvider OwnerOf(string tool)
        {
            if (tool == null) return null;
            IMcpToolProvider p;
            return Current().owner.TryGetValue(tool, out p) ? p : null;
        }

        public static List<ResourceDef> ResourcesFor(string profile)
        {
            var l = new List<ResourceDef>();
            foreach (var r in Current().resources) if (McpProfile.Includes(profile, r.profiles)) l.Add(r);
            return l;
        }

        public static List<ResourceTemplateDef> TemplatesFor(string profile)
        {
            var l = new List<ResourceTemplateDef>();
            foreach (var r in Current().templates) if (McpProfile.Includes(profile, r.profiles)) l.Add(r);
            return l;
        }

        public static List<PromptDef> PromptsFor(string profile)
        {
            var l = new List<PromptDef>();
            foreach (var p in Current().prompts) if (McpProfile.Includes(profile, p.profiles)) l.Add(p);
            return l;
        }

        // base text of the profile first, then each provider's paragraph; capped at 4000 chars (Claude Code cuts at 4096)
        public static string InstructionsFor(string profile, string baseText)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(baseText)) parts.Add(baseText.Trim());
            string host = null;
            foreach (var p in Providers())
            {
                string t;
                try { t = p.Instructions(profile); }
                catch (Exception e) { Report("instructions:" + SafeName(p), "instructions of provider " + SafeName(p) + " failed: " + e.Message); continue; }
                if (string.IsNullOrEmpty(t)) continue;
                if (SafeName(p) == DevToolsProvider.Name) host = t.Trim(); else parts.Add(t.Trim());
            }
            if (host != null) parts.Add(host);
            return Cap(parts, InstructionsCap);
        }

        internal static string Cap(List<string> parts, int cap)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                string sep = sb.Length > 0 ? "\n\n" : "";
                if (sb.Length + sep.Length + part.Length <= cap) { sb.Append(sep).Append(part); continue; }
                int room = cap - sb.Length - sep.Length;
                if (room < 120) break;
                string cut = part.Substring(0, room);
                int end = cut.LastIndexOf(". ", StringComparison.Ordinal);
                if (end < room / 2) end = cut.LastIndexOf(' ');
                if (end <= 0) end = room;
                else end = Math.Min(room, end + 1);
                sb.Append(sep).Append(cut.Substring(0, end).TrimEnd());
                break;
            }
            return sb.ToString();
        }

        // main thread: every provider's Restore, exceptions caught and reported
        public static List<string> RestoreAll()
        {
            var report = new List<string>();
            var l = Providers();
            for (int i = l.Count - 1; i >= 0; i--)
            {
                var p = l[i];
                var part = new List<string>();
                try { p.Restore(part); }
                catch (NotImplementedException e) { part.Add("not built: " + e.Message); }
                catch (Exception e) { part.Add("error: " + e.GetType().Name + ": " + e.Message); }
                foreach (var line in part) report.Add(SafeName(p) + ": " + line);
            }
            return report;
        }

        static void Raise()
        {
            var h = ListChanged;
            if (h == null) return;
            foreach (Action a in h.GetInvocationList())
            {
                try { a(); } catch (Exception e) { HostLog.Write("list_changed handler failed: " + e.Message); }
            }
        }

        static void Bump() { Interlocked.Increment(ref version); snap = null; }

        static string SafeName(IMcpToolProvider p)
        {
            try { return p.ProviderName ?? p.GetType().Name; } catch { return p.GetType().Name; }
        }

        static void Report(string key, string text)
        {
            bool first;
            lock (reported) first = reported.Add(key);
            if (first) HostLog.Write(text);
        }

        static Snap Current()
        {
            lock (gate)
            {
                if (snap != null && snap.version == version) return snap;
                var s = new Snap { version = version };
                var uris = new HashSet<string>();
                var tpls = new HashSet<string>();
                var pnames = new HashSet<string>();
                bool failed = false;
                foreach (var p in providers)
                {
                    string pn = SafeName(p);
                    List<ToolDef> tools = null;
                    try { tools = p.Tools(); } catch (Exception e) { failed = true; Report("tools:" + pn + ":" + e.Message, "provider " + pn + ": Tools() failed: " + e.Message); }
                    if (tools != null)
                        foreach (var t in tools)
                        {
                            if (t == null || string.IsNullOrEmpty(t.name)) continue;
                            IMcpToolProvider first;
                            if (s.owner.TryGetValue(t.name, out first))
                            {
                                if (!ReferenceEquals(first, p)) Report("clash:" + t.name + ":" + pn, "tool " + t.name + " of provider " + pn + " clashes with provider " + SafeName(first) + "; the first one wins");
                                continue;
                            }
                            s.owner[t.name] = p;
                            s.tools.Add(t);
                        }
                    try { var rs = p.Resources(); if (rs != null) foreach (var r in rs) if (r != null && r.uri != null && uris.Add(r.uri)) s.resources.Add(r); }
                    catch (Exception e) { Report("res:" + pn, "provider " + pn + ": Resources() failed: " + e.Message); }
                    try { var ts = p.ResourceTemplates(); if (ts != null) foreach (var r in ts) if (r != null && r.uriTemplate != null && tpls.Add(r.uriTemplate)) s.templates.Add(r); }
                    catch (Exception e) { Report("tpl:" + pn, "provider " + pn + ": ResourceTemplates() failed: " + e.Message); }
                    try { var ps = p.Prompts(); if (ps != null) foreach (var r in ps) if (r != null && r.name != null && pnames.Add(r.name)) s.prompts.Add(r); }
                    catch (Exception e) { Report("prm:" + pn, "provider " + pn + ": Prompts() failed: " + e.Message); }
                }
                if (!failed) snap = s;
                return s;
            }
        }

        // offline tests only
        internal static void ResetForTests()
        {
            lock (gate) { providers.Clear(); Bump(); }
            lock (reported) reported.Clear();
            ListChanged = null;
        }
    }
}
