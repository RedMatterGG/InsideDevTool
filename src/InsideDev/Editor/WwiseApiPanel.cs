using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // WWISE NATIVE API page + audio diagnostics (spec 55 / 93).
    // Rows come from INSIDE's own P/Invoke declarations (AkSoundEnginePINVOKE, EntryPoint = method name) joined
    // with AkSoundEngine.dll's export table and the native tracer's hook table.
    public static class WwiseApiPanel
    {
        public sealed class Row { public string managed, entry, sig, export, text; public long addr; public bool watched, resolved; public long calls; }
        static List<Row> rows;
        static string filter = "";
        static bool onlyWatched, onlyCalled;
        static float builtAt = -99, hooksAt;
        static int builtExports; static bool builtAvailable;
        static readonly Dictionary<string, WwiseNative.HookInfo> hookTmp = new Dictionary<string, WwiseNative.HookInfo>();

        public static List<Row> Build()
        {
            var list = new List<Row>(700);
            Type t = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { t = a.GetType("AkSoundEnginePINVOKE"); if (t != null) break; }
            var exports = new Dictionary<string, long>();
            for (int i = 0; i < 4000; i++) { string n; long ad; if (!WwiseNative.GetExport(i, out n, out ad)) break; exports[n] = ad; }
            var hooks = new Dictionary<string, WwiseNative.HookInfo>();
            for (int i = 0; i < 400; i++) { WwiseNative.HookInfo h; if (!WwiseNative.GetHook(i, out h)) break; hooks[h.name] = h; }
            if (t != null)
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if ((m.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;
                    var r = new Row { managed = m.Name, entry = m.Name, sig = Sig(m) };
                    long ad;
                    if (exports.TryGetValue(r.entry, out ad)) { r.export = "0x" + ad.ToString("X"); r.addr = ad; }
                    else r.export = WwiseNative.Available && exports.Count > 0 ? "MISSING" : "?";
                    WwiseNative.HookInfo h;
                    if (hooks.TryGetValue(r.entry, out h)) { r.watched = true; r.resolved = h.resolved; r.calls = h.calls; }
                    list.Add(r);
                }
            list.Sort((a, b) => string.CompareOrdinal(a.entry, b.entry));
            builtExports = exports.Count; builtAvailable = WwiseNative.Available;
            return list;
        }

        // While the game runs only the hook table changes (calls, resolved): the method list, the signatures and the
        // export addresses of the table are kept, and rows whose counter moved get a new text.
        static bool RefreshHooks(List<Row> list)
        {
            hookTmp.Clear();
            for (int i = 0; i < 400; i++) { WwiseNative.HookInfo h; if (!WwiseNative.GetHook(i, out h)) break; hookTmp[h.name] = h; }
            bool changed = false;
            foreach (var r in list)
            {
                WwiseNative.HookInfo h;
                bool w = hookTmp.TryGetValue(r.entry, out h);
                bool res = w && h.resolved; long calls = w ? h.calls : 0;
                if (w == r.watched && res == r.resolved && calls == r.calls) continue;
                r.watched = w; r.resolved = res; r.calls = calls; r.text = null; changed = true;
            }
            return changed;
        }

        static string Sig(MethodInfo m)
        {
            var sb = new StringBuilder(m.ReturnType.Name).Append('(');
            var ps = m.GetParameters();
            for (int i = 0; i < ps.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(ps[i].ParameterType.Name); }
            return sb.Append(')').ToString();
        }

        public static string Diagnostics()
        {
            var s = WwiseNative.Stats();
            var sb = new StringBuilder();
            sb.Append("AUDIO DIAGNOSTICS\n");
            sb.Append("  version           ").Append(VersionLine()).Append("   integration build: custom (PDB path of a developer machine, static CRT)\n");
            sb.Append("  native tracer     ").Append(WwiseNative.Available ? "OK (v" + WwiseNative.NativeVersion + ", level " + s.level + (s.enabled == 0 ? ", HOOKS DISABLED by wwise_off.flag" : "") + ")" : "NOT AVAILABLE - " + WwiseNative.Problem).Append('\n');
            if (WwiseNative.Available)
            {
                sb.Append("  AkSoundEngine.dll ").Append(s.akBase != 0 ? "base 0x" + s.akBase.ToString("X") + " size 0x" + s.akSize.ToString("X") + " PE timestamp 0x" + s.akStamp.ToString("X8") + " (" + new DateTime(1970, 1, 1).AddSeconds(s.akStamp).ToString("yyyy-MM-dd", Fmt.IC) + "), " + s.akExports + " exports" : "not seen yet").Append('\n');
                sb.Append("  path              ").Append(WwiseNative.ModulePath()).Append('\n');
                sb.Append("  entry points      ").Append(s.resolvedHooked).Append(" of ").Append(s.hookCount).Append(" watched resolved; ").Append(s.resolvedAll).Append(" CSharp_* resolved by Mono in total\n");
                sb.Append("  queue             ").Append(s.total).Append(" records, ").Append(s.head - s.tail).Append(" pending, ").Append(s.dropped).Append(" dropped; last drain ").Append(WwiseNative.lastDrained).Append(" in ").Append(Fmt.F(WwiseNative.lastDrainMs, "0.00")).Append(" ms (max batch ").Append(WwiseNative.maxDrained).Append(")\n");
                sb.Append("  RenderAudio calls ").Append(s.render).Append("   callbacks parsed ").Append(s.cbItems).Append(s.cbBad > 0 ? "  (" + s.cbBad + " malformed walks stopped)" : "").Append('\n');
            }
            int mapped = 0; foreach (var o in AudioTrace.objs.Values) if (AudioTrace.Unity(o.id) != null) mapped++;
            sb.Append("  game objects      ").Append(AudioTrace.objs.Count).Append(" seen, ").Append(mapped).Append(" mapped to a live Unity object\n");
            int loaded = 0; foreach (var b in AudioTrace.banks.Values) if (b.status == "LOADED") loaded++;
            sb.Append("  banks             ").Append(AudioTrace.banks.Count).Append(" seen, ").Append(loaded).Append(" loaded\n");
            sb.Append("  events            ").Append(AudioTrace.posts).Append(" posts, ").Append(AudioTrace.failedPosts).Append(" failed, ").Append(AudioTrace.endOfEvents).Append(" ended via callback\n");
            sb.Append("  ids               ").Append(AudioTrace.KnownIds).Append(" named, ").Append(AudioTrace.unknownIds.Count).Append(" unknown\n");
            return sb.ToString();
        }

        static string[] diag; static float diagAt; static string visFilter; static bool visWatched, visCalled; static List<Row> visRows; static readonly List<Row> vis = new List<Row>();
        static readonly Color cHead = new Color(0.55f, 0.9f, 0.75f, 1f), cMissing = new Color(1f, 0.4f, 0.35f, 1f), cCalled = new Color(0.5f, 1f, 0.6f, 1f);
        static UI curUi;
        static readonly Action<int> drawRow = DrawRow;
        // §86: comparison with the stock Wwise Unity integration 2014.1.2 (build 5195), the closest public reference to
        // INSIDE's 2014.1.6 build 5318. 619 of 635 entry points exist in stock with identical signatures.
        static readonly Dictionary<string, string> nonStock = new Dictionary<string, string>
        {
            { "CSharp_GetPDRevision", "PLAYDEAD-SPECIFIC (PD revision)" },
            { "CSharp_RegisterGameObjInternal", "MODIFIED INTEGRATION (replaces stock RegisterGameObj)" },
            { "CSharp_RegisterGameObjInternal_WithMask", "MODIFIED INTEGRATION (replaces stock RegisterGameObj)" },
            { "CSharp_RegisterGameObjInternal_WithName", "MODIFIED INTEGRATION (replaces stock RegisterGameObj)" },
            { "CSharp_RegisterGameObjInternal_WithName_WithMask", "MODIFIED INTEGRATION (replaces stock RegisterGameObj)" },
            { "CSharp_UnregisterGameObjInternal", "MODIFIED INTEGRATION (replaces stock UnregisterGameObj)" },
            { "CSharp_GetMemoryUsed", "UNKNOWN (not in stock 2014.1.2)" }, { "CSharp_GetPeakMemoryUsed", "UNKNOWN (not in stock 2014.1.2)" },
            { "CSharp_GetNumPools", "UNKNOWN (not in stock 2014.1.2)" }, { "CSharp_GetPoolReserved", "UNKNOWN (not in stock 2014.1.2)" },
            { "CSharp_GetVersionMajor", "UNKNOWN (not in stock 2014.1.2)" }, { "CSharp_GetVersionMinor", "UNKNOWN (not in stock 2014.1.2)" },
            { "CSharp_GetVersionSubMinor", "UNKNOWN (not in stock 2014.1.2)" }, { "CSharp_GetVersionBuild", "UNKNOWN (not in stock 2014.1.2)" },
            { "CSharp_IsDebugBuild", "UNKNOWN (not in stock 2014.1.2)" }, { "CSharp_MuteBackgroundMusic", "UNKNOWN (not in stock 2014.1.2)" },
        };
        public static string Origin(string entry) { string o; return nonStock.TryGetValue(entry, out o) ? o : "STOCK"; }

        public static string StockReport()
        {
            var sb = new StringBuilder("STOCK COMPARISON  (reference: Wwise Unity integration 2014.1.2 build 5195; INSIDE runs " + VersionLine() + ")\n");
            sb.Append("  UNCHANGED STOCK API   619 entry points, identical signatures\n");
            sb.Append("  MODIFIED INTEGRATION  RegisterGameObj x4 / UnregisterGameObj -> RegisterGameObjInternal(_WithMask/_WithName/_WithName_WithMask), UnregisterGameObjInternal\n");
            sb.Append("                        (later official integrations use this naming: may come from 2014.1.3-1.6 or be Playdead's)\n");
            sb.Append("  PLAYDEAD-SPECIFIC     GetPDRevision\n");
            sb.Append("  UNKNOWN               GetMemoryUsed, GetPeakMemoryUsed, GetNumPools, GetPoolReserved, GetVersionMajor/Minor/SubMinor/Build, IsDebugBuild, MuteBackgroundMusic\n");
            sb.Append("  native build          custom (PDB path D:\\schmid\\rcs\\WwiseUnity\\..., static CRT, linked 2016-07-01)\n");
            try
            {
                if ((AkSoundEngine.GetMajorMinorVersion() >> 16) != 0)
                {
                    sb.Append("  runtime               PD revision ").Append(AkSoundEngine.GetPDRevision()).Append("   debug build ").Append(AkSoundEngine.IsDebugBuild() ? "yes" : "no").Append('\n');
                    int pools = AkSoundEngine.GetNumPools();
                    sb.Append("  memory pools          ").Append(pools).Append('\n');
                    for (int i = 0; i < pools && i < 32; i++)
                    {
                        uint used = AkSoundEngine.GetMemoryUsed(i), peak = AkSoundEngine.GetPeakMemoryUsed(i), res = AkSoundEngine.GetPoolReserved(i);
                        if (res == 0 && used == 0) continue;
                        sb.Append("    pool ").Append(i).Append(": used ").Append(used / 1024).Append(" KB, peak ").Append(peak / 1024).Append(" KB, reserved ").Append(res / 1024).Append(" KB\n");
                    }
                }
            }
            catch (Exception e) { sb.Append("  runtime               not readable: ").Append(e.Message).Append('\n'); }
            return sb.ToString();
        }

        // §86: exact SDK version, read from the running engine through its own version getters (no side effects)
        static string version;
        public static string VersionLine()
        {
            if (version != null) return version;
            try
            {
                uint mm = AkSoundEngine.GetMajorMinorVersion(), sb = AkSoundEngine.GetSubminorBuildVersion();
                uint major = mm >> 16, minor = mm & 0xFFFF, sub = sb >> 16, build = sb & 0xFFFF;
                if (major == 0) return "Wwise SDK version: engine not initialised yet";
                version = "Wwise SDK " + major + "." + minor + "." + sub + " build " + build + "   (SoundBank version " + AkSoundEngine.AK_SOUNDBANK_VERSION + ")";
            }
            catch (Exception e) { return "Wwise SDK version: not readable (" + e.Message + ")"; }
            return version;
        }

        public static void Draw(UI ui)
        {
            // diagnostics text refreshed twice a second, the table rows only when the table or its counts change
            Background.Want(Background.Audio, 5f);
            float now = Time.realtimeSinceStartup;
            if (diag == null || now - diagAt > 0.5f) { diag = (Diagnostics() + StockReport()).Split('\n'); diagAt = now; }
            foreach (var line in diag) if (line.Length > 0) ui.Label(line, (line.StartsWith("AUDIO", StringComparison.Ordinal) || line.StartsWith("STOCK", StringComparison.Ordinal)) ? cHead : UI.Txt);
            ui.BeginRow();
            // the whole table again only on request, or while the native module was not there at the last build
            if (ui.Button("Refresh API table") || rows == null || ((builtExports == 0 || builtAvailable != WwiseNative.Available) && now - builtAt > 5f)) { rows = Build(); builtAt = hooksAt = now; }
            else if (now - hooksAt > 5f) { hooksAt = now; if (RefreshHooks(rows)) visRows = null; }
            onlyWatched = ui.Toggle(onlyWatched, "watched only");
            onlyCalled = ui.Toggle(onlyCalled, "called only");
            ui.EndRow();
            ui.BeginRow();
            ui.Label("Filter:", null, 40);
            EditorInput.Field(ui, "wapi_filter", ref filter, Mathf.Max(80, ui.Width - 4));
            ui.EndRow();
            if (filter != visFilter || onlyWatched != visWatched || onlyCalled != visCalled || visRows != rows)
            {
                visFilter = filter; visWatched = onlyWatched; visCalled = onlyCalled; visRows = rows; vis.Clear();
                foreach (var r in rows)
                {
                    if (onlyWatched && !r.watched) continue;
                    if (onlyCalled && r.calls == 0) continue;
                    if (filter.Length > 0 && r.entry.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    vis.Add(r);
                }
            }
            ui.Label("MANAGED (AkSoundEnginePINVOKE)                              NATIVE EXPORT        HOOK", UI.Dim);
            curUi = ui;
            ui.VirtualList("wapi_list", vis.Count, UI.ItemPitch, ui.Remaining, drawRow);
        }

        static void DrawRow(int i)
        {
            if (i >= vis.Count) return;
            var r = vis[i];
            if (r.text == null)
            {
                string hook = !r.watched ? "-" : !r.resolved ? "watched, not resolved yet" : "HOOKED  " + r.calls + " calls";
                string org = Origin(r.entry);
                r.text = r.entry.PadRight(52) + " " + r.export.PadRight(20) + " " + hook + "    " + r.sig + (org != "STOCK" ? "    [" + org + "]" : "");
            }
            var c = r.export == "MISSING" ? cMissing : r.watched && r.resolved ? (r.calls > 0 ? cCalled : UI.Txt) : UI.Dim;
            curUi.Item(r.text, c);
        }

        public static string Dump(string f, bool watchedOnly)
        {
            var l = Build();
            var sb = new StringBuilder(Diagnostics());
            int n = 0;
            foreach (var r in l)
            {
                if (watchedOnly && !r.watched) continue;
                if (!string.IsNullOrEmpty(f) && r.entry.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                n++;
                sb.Append(r.entry).Append("  ").Append(r.export).Append("  ").Append(!r.watched ? "-" : r.resolved ? "HOOKED " + r.calls : "watched/unresolved").Append("  ").Append(r.sig).Append('\n');
            }
            sb.Append(n).Append(" of ").Append(l.Count).Append(" P/Invoke entry points\n");
            return sb.ToString();
        }
    }
}
