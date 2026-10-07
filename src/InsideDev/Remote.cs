using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev
{
    // File bridge so an external helper can drive the mod live:
    //   _mod/cmd/in_*.txt   : command files (one command per line, optional "id|" prefix); processed in name order, then deleted
    //   _mod/cmd/out.txt    : results  (">>> id  command" ... "<<< id")
    //   _mod/cmd/state.txt  : live snapshot, rewritten every second
    //   _mod/cmd/shot_*.png : screenshots
    public static class Remote
    {
        static string dir;
        static float nextPoll, nextState, lastCommandAt = -1000f;
        static int autoId;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public static bool enabled = true;

        static string Dir
        {
            get
            {
                if (dir == null)
                {
                    dir = Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "cmd");
                    try { Directory.CreateDirectory(dir); } catch { }
                }
                return dir;
            }
        }

        public static void Update(DevCore core)
        {
            if (!enabled) return;
            try { MemLink.Update(); } catch { }
            float now = Time.realtimeSinceStartup;
            // Phase 12 throttling: poll fast (0.3 s) while the bridge is in use or the editor is open, 1 s when idle;
            // state.txt every second while active, every 5 s when idle. One directory listing per poll.
            bool active = now - lastCommandAt < 120f || core.panel;
            if (now >= nextPoll)
            {
                nextPoll = now + (active ? 0.3f : 1f);
                try { Poll(core); } catch (Exception e) { DevLog.Error("remote poll", e); nextPoll = now + 5f; }
            }
            if (now >= nextState)
            {
                nextState = now + (active ? 1f : 5f);
                try { File.WriteAllText(Path.Combine(Dir, "state.txt"), State(core)); } catch { }
            }
        }

        static void Poll(DevCore core)
        {
            var files = Directory.GetFiles(Dir, "in_*.txt");
            if (files.Length == 0) return;
            ObjectDatabase.Demand(60f);
            lastCommandAt = Time.realtimeSinceStartup;
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files)
            {
                string[] lines;
                try { lines = File.ReadAllLines(f); File.Delete(f); } catch { continue; }
                foreach (var raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    string id;
                    int bar = line.IndexOf('|');
                    if (bar > 0 && bar < 12) { id = line.Substring(0, bar); line = line.Substring(bar + 1).Trim(); }
                    else id = "r" + (++autoId);
                    Execute(id, line, core);
                }
            }
        }

        static void Execute(string id, string line, DevCore core)
        {
            var sb = new StringBuilder();
            sb.Append(">>> ").Append(id).Append("  ").Append(line).Append("   [frame ").Append(Time.frameCount).Append(", t=").Append(Time.realtimeSinceStartup.ToString("0.0", IC)).Append("]\n");
            int before;
            lock (DevLog.Lines) before = DevLog.Lines.Count;
            try
            {
                string res = Run(line, core);
                if (res != null) sb.Append(res).Append('\n');
            }
            catch (Exception e) { sb.Append("ERROR ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n').Append(e.StackTrace).Append('\n'); }
            lock (DevLog.Lines)
            {
                // include anything the command logged (existing console commands report through DevLog)
                int from = Math.Min(before, DevLog.Lines.Count);
                for (int i = from; i < DevLog.Lines.Count; i++) sb.Append("  log: ").Append(DevLog.Lines[i]).Append('\n');
            }
            sb.Append("<<< ").Append(id).Append("\n\n");
            try
            {
                string outp = Path.Combine(Dir, "out.txt");
                if (File.Exists(outp) && new FileInfo(outp).Length > 2000000) File.Delete(outp);
                File.AppendAllText(outp, sb.ToString());
            }
            catch { }
            DevLog.Write("remote> " + line);
        }

        // entry for other channels (the Unity live link): same commands as the file bridge
        public static string RunCommand(string line, DevCore core) { lastCommandAt = Time.realtimeSinceStartup; return Run(line, core); }

        // ---------------------------------------------------------------- commands
        static string Run(string line, DevCore core)
        {
            var a = Split(line);
            if (a.Count == 0) return null;
            string c = a[0].ToLowerInvariant();
            switch (c)
            {
                case "ping": return "pong  InsideDev " + Boot.Version;
                case "state": return State(core);
                case "screenshot":
                    {
                        string name = "shot_" + DateTime.Now.ToString("HHmmss") + (a.Count > 1 ? "_" + a[1] : "") + ".png";
                        string p = Path.Combine(Dir, name);
                        Application.CaptureScreenshot(p);
                        return "screenshot -> " + p + " (written at end of frame)";
                    }
                case "diag": return RenderHost.Report();
                case "cams": return RenderDiag.CameraReport();
                case "render":
                    {
                        string m = a.Count > 1 ? a[1].ToLowerInvariant() : "";
                        switch (m)
                        {
                            case "auto": RenderHost.Request(Backend.Auto); break;
                            case "eof": RenderHost.Request(Backend.EndOfFrame); break;
                            case "overlay": RenderHost.Request(Backend.OverlayCamera); break;
                            case "gamecam": RenderHost.Request(Backend.GameCameraPost); break;
                            case "probe": RenderHost.RequestProbe("remote", 1); break;
                            case "reset": RenderHost.renderer.DestroyResources(); RenderHost.Event("renderer resources destroyed (remote)"); break;
                            case "snapshot": RenderHost.Snapshot("remote snapshot"); break;
                            case "board": RenderDiag.board = a.Count > 2 ? On(a[2]) : !RenderDiag.board; break;
                            case "": break;
                            default: return "usage: render auto|eof|overlay|gamecam|probe|reset|snapshot|board [on|off]";
                        }
                        return "backend requested " + RenderHost.requested + " active " + RenderHost.active + "   probe: " + RenderHost.probeResult;
                    }
                case "focustest":
                    {
                        if (a.Count > 1 && a[1] == "stop") { FocusTest.Stop(); return "stopping"; }
                        if (a.Count > 1 && a[1] == "status") return FocusTest.Status();
                        int n = a.Count > 1 ? (int)F(a[1]) : 20, away = a.Count > 2 ? (int)F(a[2]) : 2500, back = a.Count > 3 ? (int)F(a[3]) : 1500;
                        string mode = a.Count > 4 ? a[4] : "alttab";
                        int bg = a.Count > 5 ? (On(a[5]) ? 1 : 0) : -1;
                        return FocusTest.Start(n, away, back, mode, bg);
                    }
                case "keyhold":
                    {
                        byte vk = 0x27;   // Right arrow (INSIDE's default "right")
                        if (a.Count > 1) vk = a[1] == "right" ? (byte)0x27 : a[1] == "left" ? (byte)0x25 : a[1] == "up" ? (byte)0x26 : Convert.ToByte(a[1], 16);
                        int ms = a.Count > 2 ? (int)F(a[2]) : 1500;
                        return FocusTest.KeyHold(vk, ms, a.Count > 3 ? a[3] : null);
                    }
                case "tp":
                    {
                        // tp x y z  |  tp <obj>   teleports the boy
                        var parts = Split(line);
                        if (parts.Count >= 4) { var p = new Vector3(F(parts[1]), F(parts[2]), F(parts[3])); return G.Teleport(p) ? "boy -> " + V(p) : "no active character"; }
                        var go = Resolve(parts.Count > 1 ? parts[1] : ""); if (go == null) return "usage: tp x y z | tp <obj>";
                        return G.Teleport(go.transform.position) ? "boy -> " + go.name + " " + V(go.transform.position) : "no active character";
                    }
                case "boypos":
                    {
                        var ch = G.MainCharacter;
                        return ch != null ? "boy " + V(ch.pos3) : "no character";
                    }
                case "uifield":
                    return "inspector find field = '" + core.Inspector.filter + "'";
                case "db":
                    {
                        string m = a.Count > 1 ? a[1].ToLowerInvariant() : "stats";
                        ObjectDatabase.Demand(60f);
                        switch (m)
                        {
                            case "stats": ObjectDatabase.EnsureFresh(3f); return ObjectDatabase.Stats();
                            case "budget": if (a.Count > 2) ObjectDatabase.budgetMs = F(a[2]); return "budget " + ObjectDatabase.budgetMs + " ms/frame";
                            case "dirty": ObjectDatabase.MarkDirty("remote"); return "marked dirty";
                            case "find":
                                {
                                    ObjectDatabase.EnsureFresh(3f);
                                    var res = ObjectDatabase.Search(Rest(Rest(line)), 60);
                                    var sb = new StringBuilder(); sb.Append(res.Count).Append(" result(s)\n");
                                    foreach (var r in res) sb.Append(r.activeInHierarchy ? "  " : "  [off] ").Append(r.path).Append("  <").Append(r.KindLabel).Append(">  #").Append(r.id).Append('\n');
                                    return sb.ToString();
                                }
                            case "rec":
                                {
                                    var go = Resolve(Rest(Rest(line)));
                                    var r = ObjectDatabase.Get(go);
                                    if (r == null) return "no record (object " + (go == null ? "not found" : "not scanned yet") + ")";
                                    return "#" + r.id + "  " + r.path + "\n  scene " + r.scene + "  depth " + r.depth + "  activeSelf " + r.activeSelf + "  inHierarchy " + r.activeInHierarchy +
                                           "  layer " + r.layer + "  tag " + r.tag + "  pos " + V(r.pos) + "\n  kind " + r.KindLabel + "  children " + r.childCount +
                                           "\n  components: " + string.Join(", ", r.comps) + "\n  discovered frame " + r.discoveredFrame + " (t=" + r.discoveredAt.ToString("0.0", IC) + ")" +
                                           "\n  selector: " + ObjectSelector.From(r);
                                }
                            case "selector":
                                {
                                    var go = Resolve(Rest(Rest(line)));
                                    return go == null ? "not found" : ObjectSelector.From(go).ToString();
                                }
                            case "resolve":
                                {
                                    var sel = ObjectSelector.Parse(Rest(Rest(line)));
                                    ObjRecord match; List<ObjRecord> cand;
                                    var res = sel.Resolve(out match, out cand);
                                    var sb = new StringBuilder();
                                    sb.Append(res).Append(match != null ? "  -> #" + match.id + " " + match.path + " @" + V(match.pos) : "").Append("   candidates ").Append(cand.Count).Append('\n');
                                    if (match == null) foreach (var cd in cand) sb.Append("  #").Append(cd.id).Append(' ').Append(cd.path).Append(" @").Append(V(cd.pos)).Append('\n');
                                    return sb.ToString();
                                }
                        }
                        return "usage: db stats|find <query>|rec <obj>|selector <obj>|resolve <selector>|budget <ms>|dirty";
                    }
                case "sfind":
                    {
                        // sfind <words> [cat:animation|sounds|scripts|triggers|fsm|characters] [clutter] [path]
                        var words = new List<string>(); var cat = SmartFind.Cat.All; bool cl = false, pm = false;
                        for (int i = 1; i < a.Count; i++)
                        {
                            if (a[i].StartsWith("cat:")) { int ci = Array.IndexOf(SmartFind.CatNames, a[i].Substring(4).Replace("fsm", "state machines")); if (ci >= 0) cat = (SmartFind.Cat)ci; }
                            else if (a[i] == "clutter") cl = true; else if (a[i] == "path") pm = true; else words.Add(a[i]);
                        }
                        int hc, hp; var hs = SmartFind.Run(string.Join(" ", words.ToArray()), cat, cl, pm, out hc, out hp);
                        var sb = new StringBuilder(hs.Count + " result(s); clutter hidden " + hc + ", path-only hidden " + hp + (SmartFind.Truncated > 0 ? ", " + SmartFind.Truncated + " over the cap" : "") + "\n");
                        for (int i = 0; i < hs.Count && i < 25; i++) sb.Append("  ").Append(hs[i].score).Append("  ").Append(SmartFind.Describe(hs[i])).Append('\n');
                        return sb.ToString();
                    }
                case "mem":
                    {
                        string q = Rest(line);
                        if (q == "on" || q == "off") { MemAddr.Enabled = q == "on"; return "memory addresses " + q + "; " + MemAddr.status; }
                        var go = q.Length == 0 ? Selection.Current : Resolve(q); if (go == null) return "not found";
                        if (!MemAddr.Ready) return MemAddr.status;
                        var sb = new StringBuilder(MemAddr.status + "\n" + go.name + "\n");
                        foreach (var b in MemAddr.Describe(go))
                        {
                            sb.Append("  ").Append(b.title).Append("  script ").Append(MemAddr.Hex(b.addr)).Append(" size ").Append(b.size).Append("  native ").Append(MemAddr.Hex(b.native)).Append('\n');
                            foreach (var r in b.rows) sb.Append("    +0x").Append(r.offset.ToString("X")).Append(' ').Append(r.name).Append("  ").Append(MemAddr.Hex(r.addr)).Append("  ").Append(r.ceType).Append("  = ").Append(r.value).Append('\n');
                        }
                        return sb.ToString();
                    }
                case "memwhat": { long v; if (!MemLink.ParseHex(Rest(line), out v)) return "memwhat <hex>"; GameObject o; return MemAddr.WhatIs(v, out o); }
                case "memmethod": { long v; if (!MemLink.ParseHex(Rest(line), out v)) return "memmethod <hex>"; return MemLink.Method(v, true); }
                case "memjit":
                    {
                        var a2 = Rest(line).Split(' '); if (a2.Length < 2) return "memjit <Type> <method> [+offset]";
                        var t = CodeGraph.FindType(a2[0]);
                        if (t == null) foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { try { foreach (var x in asm.GetTypes()) if (x.Name == a2[0] || x.FullName == a2[0]) { t = x; break; } } catch { } if (t != null) break; }
                        if (t == null) return "no type " + a2[0];
                        var m = t.GetMethod(a2[1], System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
                        if (m == null) return "no method " + a2[1];
                        long p = m.MethodHandle.GetFunctionPointer().ToInt64() + (a2.Length > 2 ? Convert.ToInt64(a2[2].TrimStart('+'), 16) : 0);
                        return "code " + MemAddr.Hex(p) + "\n" + MemLink.Method(p, false);
                    }
                case "clip":
                    {
                        string q = Rest(line);
                        if (q.Length > 0) Clipboard.Set(q);
                        return "clipboard: " + Clipboard.Get();
                    }
                case "memexport": return MemLink.Export(Selection.Current);
                case "memread":
                    {
                        long v; var parts = Rest(line).Split(' '); if (!MemLink.ParseHex(parts[0], out v)) return "memread <hex> [bytes]";
                        int n = parts.Length > 1 ? int.Parse(parts[1]) : 16; var sb = new StringBuilder(); if (!MemAddr.Readable((IntPtr)v, n)) return "not readable memory";
                        for (int i = 0; i < n; i += 4) sb.Append((v + i).ToString("X")).Append(": ").Append(System.Runtime.InteropServices.Marshal.ReadInt32((IntPtr)(v + i)).ToString("X8")).Append("  f=").Append(MemAddr.ReadFloat((IntPtr)(v + i), 0)).Append('\n');
                        return sb.ToString();
                    }
                case "fsmgraph":
                    {
                        // object names often contain spaces: try the whole text first, then "<object> <fsmName>"
                        string q = Rest(line), fname = null;
                        var go = Resolve(q);
                        if (go == null) { int sp = q.LastIndexOf(' '); if (sp > 0) { fname = q.Substring(sp + 1); go = Resolve(q.Substring(0, sp)); } }
                        if (go == null) return "not found";
                        PlayMakerFSM pick = null;
                        foreach (var f in go.GetComponents<PlayMakerFSM>()) if (pick == null || (fname != null && f.FsmName == fname)) pick = f;
                        if (pick == null) return "no FSM on " + go.name;
                        Selection.Set(go, "bridge"); FsmGraph.Open(pick);
                        return FsmGraph.Dump(pick);
                    }
                case "fsmfind": return FsmFind.Report(Rest(line));
                case "gamecode":
                    if (a.Count > 1 && a[1] == "banner") { GameCode.BannerHidden = false; return "banner shown again"; }
                    return GameCode.Report();
                case "postfx":
                    {
                        // postfx | postfx on|off|default <Type> | postfx set <Type> <field> <value>
                        if (a.Count < 3) return PostFx.Report();
                        var cam = G.Cam(); if (cam == null) return "no gameplay camera";
                        Behaviour b = null; foreach (var x in PostFx.Effects(cam)) if (x.GetType().Name.Equals(a[2], StringComparison.OrdinalIgnoreCase)) b = x;
                        if (b == null) return "no effect " + a[2] + " on " + cam.name;
                        if (a[1] == "on" || a[1] == "off") { PostFx.SetEnabled(b, a[1] == "on"); return b.GetType().Name + " " + a[1]; }
                        if (a[1] == "default") { PostFx.DefaultAll(b); return b.GetType().Name + " back to the game's values"; }
                        if (a[1] == "set" && a.Count > 4) { var st = PostFx.Find(b, a[3]); if (st == null) return "no setting " + a[3]; return PostFx.SetValue(b, st, a[4]); }
                        return "postfx | postfx on|off|default <Type> | postfx set <Type> <field> <value>";
                    }
                case "fsmset":
                    {
                        // fsmset <fsm object>|<state>|<action index>|<field>|<value>   (field "(enabled)" = action on/off)
                        var p = Rest(line).Split(new[] { '|' }, 5);
                        if (p.Length < 5) return "fsmset <fsm object>|<state>|<action index>|<field>|<value>";
                        var go = Resolve(p[0].Trim()); if (go == null) return "not found";
                        var f = go.GetComponent<PlayMakerFSM>(); if (f == null) return "no FSM on " + go.name;
                        return ActionEdit.Apply(f, p[1] + "|" + p[2] + "|" + p[3] + "|" + p[4], "bridge");
                    }
                case "sigwire":
                case "sigunwire":
                    {
                        // sigwire <fsm object>|<signal in>|<sender object>|<signal out>
                        var p = Rest(line).Split('|');
                        if (p.Length < 4) return a[0] + " <fsm object>|<signal in>|<sender object>|<signal out>";
                        var go = Resolve(p[0].Trim()); if (go == null) return "fsm object not found";
                        var f = go.GetComponent<PlayMakerFSM>(); if (f == null) return "no FSM on " + go.name;
                        var snd = Resolve(p[2].Trim()); if (snd == null) return "sender not found";
                        if (a[0] == "sigwire") return SignalEdit.Connect(f, snd, p[3].Trim(), p[1].Trim(), "bridge");
                        foreach (var sc in SignalEdit.Incoming(f)) if (sc.signalOutGameObject == snd && sc.signalOutName == p[3].Trim() && sc.signalInName == p[1].Trim()) return SignalEdit.Disconnect(f, sc, "bridge");
                        return "no such connection";
                    }
                case "sigs":
                    {
                        var go = Resolve(Rest(line)); if (go == null) return "not found";
                        var sb = new StringBuilder();
                        foreach (var f in go.GetComponents<PlayMakerFSM>()) sb.Append(go.name).Append('/').Append(f.FsmName).Append(" signal inputs:\n").Append(SignalEdit.Describe(f));
                        return sb.Length > 0 ? sb.ToString() : "no FSM on " + go.name;
                    }
                case "mats":
                    {
                        var go = Resolve(Rest(line)); if (go == null) return "not found";
                        return MaterialFacts.Report(go);
                    }
                case "origin":
                    {
                        string q = Rest(line); bool deep = q.EndsWith(" deep"); if (deep) q = q.Substring(0, q.Length - 5).Trim();
                        var go = Resolve(q); if (go == null) return "not found";
                        return LevelCompare.Report(go, deep);
                    }
                case "refs":
                case "refby":
                    {
                        if (a.Count > 1 && a[1] == "stats") return ReferenceIndex.Stats();
                        bool all = line.EndsWith(" all");
                        string target = Rest(line);
                        if (all) target = target.Substring(0, target.Length - 4).Trim();
                        var go = Resolve(target); if (go == null) return "not found";
                        ReferenceIndex.EnsureComplete();
                        ReferenceIndex.Refresh(go);
                        var list = ReferenceIndex.Filter(c == "refs" ? ReferenceIndex.References(go) : ReferenceIndex.Referencers(go), all);
                        var sb = new StringBuilder();
                        sb.Append(c == "refs" ? "references of " : "referenced by (").Append(c == "refs" ? Inspector.PathOf(go.transform) : Inspector.PathOf(go.transform) + ")").Append(": ").Append(list.Count).Append('\n');
                        foreach (var e in list) sb.Append("  ").Append(ReferenceIndex.Describe(e, c == "refs")).Append(c == "refs" ? "" : "   <" + ReferenceIndex.PathOf(e.srcGo) + ">").Append('\n');
                        if (list.Count == 0 && c == "refby") sb.Append("  No incoming reference found in the current loaded scenes / reference index.\n");
                        return sb.ToString();
                    }
                case "chain":
                    {
                        // chain <obj> [depth] [back]
                        var parts = Split(line);
                        var go = Resolve(parts.Count > 1 ? parts[1] : ""); if (go == null) return "not found";
                        int depth = parts.Count > 2 ? (int)F(parts[2]) : 4;
                        bool back = parts.Count > 3 && parts[3] == "back";
                        ReferenceIndex.EnsureComplete();
                        var ch = ReferenceIndex.Chain(go.GetInstanceID(), !back, depth, 200);
                        var sb = new StringBuilder();
                        sb.Append(back ? "what leads to " : "what ").Append(go.name).Append(back ? " (reverse)" : " leads to").Append(": ").Append(ch.Count).Append('\n');
                        foreach (var kv in ch) sb.Append("  ").Append(ReferenceIndex.PathOf(kv.Key)).Append("   via ").Append(ReferenceIndex.NameOf(kv.Value.srcGo)).Append(' ').Append(kv.Value.srcType).Append('.').Append(kv.Value.field).Append(" [").Append(ReferenceIndex.ProvLabel(kv.Value.prov)).Append("]\n");
                        return sb.ToString();
                    }
                case "path":
                case "paths":
                    {
                        // path <a> <b> [any]  |  paths <a> <b>
                        var parts = Split(line);
                        if (parts.Count < 3) return "usage: path <a> <b> [any] | paths <a> <b>";
                        var ga = Resolve(parts[1]); var gb = Resolve(parts[2]);
                        if (ga == null || gb == null) return "not found: " + (ga == null ? parts[1] : parts[2]);
                        ReferenceIndex.EnsureComplete();
                        var sb = new StringBuilder();
                        if (c == "path")
                        {
                            var p = ReferenceIndex.ShortestPath(ga.GetInstanceID(), gb.GetInstanceID(), parts.Count > 3 && parts[3] == "any");
                            if (p == null) return "no path " + ga.name + " -> " + gb.name + " within 12 steps";
                            sb.Append(p.Count).Append(" step(s)\n");
                            foreach (var e in p) sb.Append("  ").Append(ReferenceIndex.NameOf(e.srcGo)).Append(" --").Append(e.srcType).Append('.').Append(e.field).Append(" [").Append(ReferenceIndex.ProvLabel(e.prov)).Append("]--> ").Append(e.dstName).Append('\n');
                        }
                        else
                        {
                            var ps = ReferenceIndex.AllPaths(ga.GetInstanceID(), gb.GetInstanceID(), 8, 20);
                            sb.Append(ps.Count).Append(" path(s)\n");
                            foreach (var p in ps) { sb.Append("  "); foreach (var e in p) sb.Append(ReferenceIndex.NameOf(e.srcGo)).Append(" -> "); sb.Append(gb.name).Append('\n'); }
                        }
                        return sb.ToString();
                    }
                case "move":
                case "movehere":
                    {
                        // move <obj> x y z   |   movehere <obj> [dx] [dz]   (relative to the boy)
                        var parts = Split(line);
                        var go = Resolve(parts.Count > 1 ? parts[1] : ""); if (go == null) return "not found";
                        Vector3 p;
                        if (c == "move") { if (parts.Count < 5) return "usage: move <obj> x y z"; p = new Vector3(F(parts[2]), F(parts[3]), F(parts[4])); }
                        else
                        {
                            var ch = G.MainCharacter; if (ch == null) return "no character";
                            p = ch.pos3 + new Vector3(parts.Count > 2 ? F(parts[2]) : 2f, 0f, parts.Count > 3 ? F(parts[3]) : 0f);
                        }
                        var old = go.transform.position;
                        TransformMemory.Remember(go.transform);
                        ChangeRecorder.Before(go, ChangeRecorder.PropKind.Position, "bridge");
                        go.transform.position = p;
                        DevLog.Write("move " + go.name + " " + V(old) + " -> " + V(p));
                        return go.name + " moved " + V(old) + " -> " + V(p) + "   (undo: move " + parts[1] + " " + old.x.ToString("R", IC) + " " + old.y.ToString("R", IC) + " " + old.z.ToString("R", IC) + ")";
                    }
                case "resetpos":
                    {
                        if (a.Count > 1 && a[1] == "all") return "restored " + TransformMemory.RestoreAll();
                        var go = Resolve(Rest(line)); if (go == null) return "not found";
                        return TransformMemory.Restore(go.transform) ? "restored " + go.name : "no remembered original for " + go.name;
                    }
                case "freecam":
                    if (a.Count > 1) CameraControl.SetFree(On(a[1])); else CameraControl.ToggleFree();
                    return CameraControl.Status();
                case "zoom":
                    if (a.Count > 1) { if (a[1] == "reset") CameraControl.ResetZoom(); else CameraControl.zoom = 1f / Mathf.Max(0.1f, F(a[1])); }
                    return CameraControl.Status();
                case "cspawn":
                    {
                        string m = a.Count > 1 ? a[1].ToLowerInvariant() : "list";
                        int idx = -1;
                        if (a.Count > 2 && m != "save") int.TryParse(a[2], NumberStyles.Integer, IC, out idx);
                        switch (m)
                        {
                            case "save": return CustomSpawns.SaveHere(a.Count > 2 ? Rest(Rest(line)) : null);
                            case "go": return CustomSpawns.Go(idx);
                            case "respawn": CustomSpawns.SetRespawn(idx); return CustomSpawns.lastStatus;
                            case "del": CustomSpawns.Delete(idx); return CustomSpawns.lastStatus;
                        }
                        CustomSpawns.Load();
                        var sb = new StringBuilder();
                        for (int i = 0; i < CustomSpawns.entries.Count; i++)
                        {
                            var e = CustomSpawns.entries[i];
                            sb.Append(i).Append(CustomSpawns.respawnEntry == i ? " [respawn] " : "  ").Append(e.name).Append("  ").Append(e.subscene).Append(" #").Append(e.savepoint).Append("  @").Append(V(e.pos)).Append('\n');
                        }
                        return sb.Length == 0 ? "no custom spawns (cspawn save [name])" : sb.ToString();
                    }
                case "bg":
                    if (a.Count > 1) Application.runInBackground = On(a[1]);
                    return "runInBackground " + Application.runInBackground;
                case "fullscreen":
                    if (a.Count > 1) Screen.fullScreen = On(a[1]);
                    return "fullscreen requested " + (a.Count > 1 ? a[1] : "?") + " (now " + Screen.fullScreen + ", applies next frame)";
                case "res":
                    {
                        if (a.Count < 3) return "usage: res <w> <h> [full]";
                        bool full = a.Count > 3 ? On(a[3]) : Screen.fullScreen;
                        Screen.SetResolution((int)F(a[1]), (int)F(a[2]), full);
                        return "resolution requested " + a[1] + "x" + a[2] + (full ? " fullscreen" : " windowed");
                    }
                case "ui":
                    {
                        string m = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
                        var ui = core.UIKit;
                        switch (m)
                        {
                            case "scale": if (a.Count > 2) { core.uiScale = F(a[2]); EditorState.Set("ui.scale", core.uiScale); } break;
                            case "layout": if (a.Count > 2) core.workspace.SetDocked(a[2] == "dock"); break;
                            case "reset": core.workspace.ResetLayout(); break;
                            case "show": if (a.Count > 2) { core.SetPanel(true, null); core.workspace.Show(a[2]); } break;
                            case "dock":    // ui dock left|right|bottom <fraction 0.1-0.7 | off | on>
                                if (a.Count < 4) return "usage: ui dock left|right|bottom <fraction>|on|off";
                                if (a[3] == "off" || a[3] == "on") return core.workspace.SetDockVisible(a[2], a[3] == "on");
                                return core.workspace.SetDockSize(a[2], F(a[3]));
                            case "window":  // ui window x y w h   (single-window mode rect, UI pixels)
                                if (a.Count < 6) return "usage: ui window <x> <y> <w> <h>";
                                return core.workspace.SetWindow(F(a[2]), F(a[3]), F(a[4]), F(a[5]));
                            case "put":     // ui put <panel id> left|right|bottom
                                if (a.Count < 4) return "usage: ui put <panel id> left|right|bottom";
                                { string r = core.workspace.Put(a[2], a[3]); core.SetPanel(true, null); return r; }
                            case "docks": return core.workspace.Describe();
                            case "focus": ui.focus = a.Count > 2 && a[2] != "none" ? a[2] : null; break;
                            case "scroll": ui.ScrollTo(a[2], F(a[3])); return "scroll " + a[2] + " = " + a[3];
                            case "record": ui.recordWidgets = a.Count < 3 || On(a[2]); return "widget recording " + ui.recordWidgets;
                            case "find":
                            case "clicktext":
                            case "hovertext":
                                {
                                    ui.recordWidgets = true;
                                    string txt = a.Count > 2 ? a[2] : ""; int nth = a.Count > 3 ? (int)F(a[3]) : 0;
                                    var r = ui.FindWidget(txt, nth);
                                    if (r == null) return "no widget containing '" + txt + "' in the last frame (" + ui.lastWidgets.Count + " logged; recording is now on, retry next frame)";
                                    var wc = r.Value.center;
                                    if (m == "find") return "'" + txt + "' #" + nth + " at " + wc.x.ToString("0", IC) + "," + wc.y.ToString("0", IC) + "  rect " + r.Value;
                                    EditorInput.Inject(wc * core.uiScale, m == "clicktext" ? 0 : -1);
                                    return m + " '" + txt + "' at " + wc.x.ToString("0", IC) + "," + wc.y.ToString("0", IC);
                                }
                            case "widgets":
                                {
                                    ui.recordWidgets = true;
                                    var sbw = new StringBuilder(ui.lastWidgets.Count + " widgets\n");
                                    string f = a.Count > 2 ? a[2] : null;
                                    foreach (var kv in ui.lastWidgets) if (f == null || kv.Key.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) sbw.Append(kv.Value.center.x.ToString("0", IC)).Append(',').Append(kv.Value.center.y.ToString("0", IC)).Append("  ").Append(kv.Key).Append('\n');
                                    return sbw.ToString();
                                }
                            case "click":
                            case "rclick":
                            case "hover":
                                {
                                    // logical UI coordinates (as drawn at the current UI scale)
                                    float sc = core.uiScale;
                                    EditorInput.Inject(new Vector2(F(a[2]) * sc, F(a[3]) * sc), a[1] == "click" ? 0 : a[1] == "rclick" ? 1 : -1);
                                    return a[1] + " injected at " + a[2] + "," + a[3];
                                }
                            case "renderer": if (a.Count > 2) { RenderHost.SetRenderer(a[2]); EditorState.Set("render.renderer", a[2]); } break;
                        }
                        string gi; try { gi = GameInput.IsEnabled() ? "enabled" : "DISABLED"; } catch { gi = "?"; }
                        return "ui scale " + core.uiScale + "  layout " + (core.workspace.docked ? "dock" : "window") + "  panel " + core.Panel +
                               "  focus " + (ui.focus ?? "none") + "  mouseOverUI " + ui.mouseOverUI + "  " + InputCapture.Status + "  GameInput " + gi +
                               "  renderer " + RenderHost.renderer.Name + "\n  left=" + core.workspace.ActivePanel("left") + " right=" + core.workspace.ActivePanel("right") + " bottom=" + core.workspace.ActivePanel("bottom") +
                               "  gameView " + core.workspace.GameViewRect(core.uiScale);
                    }
                case "panel":
                    core.SetPanel(a.Count > 1 ? On(a[1]) : !core.Panel, a.Count > 2 ? a[2] : null);
                    return "panel " + core.Panel;
                case "inspect":
                    {
                        string q = Rest(line); bool deep = false;
                        if (q.EndsWith(" deep")) { deep = true; q = q.Substring(0, q.Length - 5); }
                        return deep ? InspectDeep(Resolve(q)) : Inspect(Resolve(q));
                    }
                case "select":
                    {
                        var go = Resolve(Rest(line)); if (go == null) return "not found";
                        Selection.Set(go, "bridge");
                        return "selected " + Selection.Describe();
                    }
                case "events":
                    {
                        if (a.Count > 1 && a[1] == "on") { EventMonitor.enabled = true; return EventMonitor.Status(); }
                        if (a.Count > 1 && a[1] == "off") { EventMonitor.enabled = false; return EventMonitor.Status(); }
                        if (a.Count > 1 && a[1] == "clear") { EventMonitor.Clear(); return EventMonitor.Status(); }
                        int n = 40; string flt = null;
                        if (a.Count > 1 && !int.TryParse(a[1], out n)) { n = 40; flt = a[1]; }
                        if (a.Count > 2) flt = a[2];
                        return EventMonitor.Dump(n, flt);
                    }
                case "pick":
                    {
                        if (a.Count > 1 && (a[1] == "on" || a[1] == "off")) { WorldPick.mode = a[1] == "on"; return "pick mode " + WorldPick.mode; }
                        if (a.Count > 2 && a[1] == "filter") { int fi = Array.IndexOf(WorldPick.Filters, a[2]); if (fi < 0) return "filters: " + string.Join(" ", WorldPick.Filters); WorldPick.filter = fi; return "pick filter " + a[2]; }
                        if (a.Count > 1 && a[1] == "status") return "pick mode " + WorldPick.mode + ", filter " + WorldPick.Filters[WorldPick.filter] + ", queries " + WorldPick.queries + "\n" + WorldPick.lastResult;
                        if (a.Count > 2) return WorldPick.PickAt(new Vector2(F(a[1]), F(a[2])));
                        var ch = G.MainCharacter; var cm = G.Cam();
                        if (ch == null || cm == null) return "no character/camera";
                        var sp = RenderScale.W2S(cm, ch.pos3 + Vector3.up * 0.5f);
                        return "boy is at screen " + sp.x.ToString("0") + "," + (Screen.height - sp.y).ToString("0") + "\n" + WorldPick.PickAt(new Vector2(sp.x, Screen.height - sp.y));
                    }
                case "gizmo":
                    {
                        // gizmo [status] | gizmo on|off (current selection) | gizmo global on|off | gizmo move x y z (drag test: moves the selection like a drag)
                        var gs = Selection.Current;
                        if (a.Count > 2 && a[1] == "global") { TransformGizmo.SetGlobal(a[2] == "on"); }
                        else if (a.Count > 1 && (a[1] == "on" || a[1] == "off")) { if (gs == null) return "nothing selected"; TransformGizmo.SetFor(gs, a[1] == "on"); }
                        return "gizmo global " + TransformGizmo.globalOn + ", selection " + (gs == null ? "none" : gs.name + " per-object " + TransformGizmo.IsOnFor(gs) + ", visible " + TransformGizmo.Visible + ", pos " + gs.transform.position.ToString("F2"));
                    }
                case "labels":
                    {
                        var ov = core.Overlay;
                        if (a.Count > 1) { int lv = Array.IndexOf(Overlay.LabelLevels, a[1].ToUpperInvariant()); if (lv >= 0) { ov.labelLevel = lv; ov.showLabels = lv > 0; } }
                        return "labels " + Overlay.LabelLevels[ov.labelLevel];
                    }
                case "scope":
                    {
                        // scope [selected|1|2|nearby|all]
                        var ov = core.Overlay;
                        if (a.Count > 1)
                        {
                            string v = a[1].ToLowerInvariant();
                            int sc = v.StartsWith("sel") ? 0 : v == "1" ? 1 : v == "2" ? 2 : v.StartsWith("near") ? 3 : v == "all" ? 4 : -1;
                            if (sc >= 0) { ov.scope = sc; EditorState.Set("overlay.scope", sc); }
                        }
                        return "scope " + Overlay.Scopes[ov.scope] + "   in scope (drawn last frame): " + ov.inScopeCount + " of " + (ov.visibleTriggers + ov.visibleSolids) + " visible colliders";
                    }
                case "perf":
                    {
                        // perf | perf reset | perf enable <subsystem>
                        if (a.Count > 1 && a[1] == "reset") { Perf.Reset(); return "perf stats reset"; }
                        if (a.Count > 2 && a[1] == "enable") return Perf.Enable(a[2], true) ? a[2] + " re-enabled" : "no subsystem " + a[2];
                        return Perf.Report();
                    }
                case "history": return ChangeRecorder.Dump(a.Count > 1 ? int.Parse(a[1]) : 30);
                case "undo": return ChangeRecorder.Undo();
                case "redo": return ChangeRecorder.Redo();
                case "revert":
                    {
                        // revert all | revert <n>  (n = index in the NET CHANGES list of "history")
                        if (a.Count > 1 && a[1] == "all") return ChangeRecorder.RevertAll();
                        var net = ChangeRecorder.NetChanges();
                        int k; if (a.Count < 2 || !int.TryParse(a[1], out k) || k < 0 || k >= net.Count) return "revert all | revert <0.." + (net.Count - 1) + ">";
                        return ChangeRecorder.Revert(net[k]);
                    }
                case "mod":
                    {
                        // mod list | show <name> | record <name> [description...] | dry <name> | validate <name> | enable|disable <name> | reload | delete <name>
                        string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "list";
                        if (sub == "list") return Mods.mods.Count == 0 ? "no mods in " + Mods.Dir : Mods.Summary();
                        if (sub == "reload") { Mods.LoadAll(); return Mods.Summary(); }
                        if (sub == "record")
                        {
                            if (a.Count < 3) return "mod record <name> [description]";
                            var acts = new List<ChangeRecorder.Entry>();
                            return HistoryPanel.RecordMod(a[2], a.Count > 3 ? string.Join(" ", a.GetRange(3, a.Count - 3).ToArray()) : "", ChangeRecorder.NetChanges(), acts);
                        }
                        if (a.Count < 3) return "mod " + sub + " <name>";
                        var m = Mods.Find(a[2]); if (m == null) return "no mod " + a[2];
                        switch (sub)
                        {
                            case "show": return Mods.Status(m);
                            case "dry": return Mods.DryRun(m);
                            case "validate": Mods.Validate(m); return m.problems.Count == 0 ? "valid" : string.Join("\n", m.problems.ToArray());
                            case "enable": Mods.SetEnabled(m, true); return Mods.Status(m);
                            case "disable": Mods.SetEnabled(m, false); return Mods.Status(m);
                            case "delete": Mods.mods.Remove(m); try { System.IO.File.Delete(m.file); } catch (Exception e) { return e.Message; } return "deleted " + m.file;
                        }
                        return "unknown mod command";
                    }
                case "bookmark":
                    {
                        // bookmark add [note...] | list | go <n> | del <n>
                        string sub = a.Count > 1 ? a[1] : "list";
                        if (sub == "add") { var mk = Bookmarks.Add(Selection.Current, null, a.Count > 2 ? string.Join(" ", a.GetRange(2, a.Count - 2).ToArray()) : ""); return mk == null ? "nothing selected" : "bookmarked " + mk.name + "  " + mk.selector; }
                        if (sub == "list") { var sb = new System.Text.StringBuilder(); for (int i = 0; i < Bookmarks.marks.Count; i++) sb.Append(i).Append("  ").Append(Bookmarks.marks[i].name).Append("  ").Append(Bookmarks.marks[i].note).Append("  ").Append(Bookmarks.marks[i].selector).Append('\n'); return sb.Length > 0 ? sb.ToString() : "no bookmarks"; }
                        int bi; if (a.Count < 3 || !int.TryParse(a[2], out bi) || bi < 0 || bi >= Bookmarks.marks.Count) return "bookmark go|del <n>";
                        if (sub == "go") return Bookmarks.Go(Bookmarks.marks[bi], a.Count > 3 && a[3] == "tp");
                        if (sub == "del") { var mk = Bookmarks.marks[bi]; Bookmarks.Remove(mk); return "removed " + mk.name; }
                        return "bookmark add|list|go|del";
                    }
                case "palette":
                    {
                        // palette <query...> [#n]   list hits, or open hit n
                        int open = -1; var words = new List<string>(a.GetRange(1, a.Count - 1));
                        if (words.Count > 0 && words[words.Count - 1].StartsWith("#")) { open = int.Parse(words[words.Count - 1].Substring(1)); words.RemoveAt(words.Count - 1); }
                        return CommandPalette.Bridge(string.Join(" ", words.ToArray()), open);
                    }
                case "cmd":
                    {
                        if (a.Count < 2) { var sb = new System.Text.StringBuilder(); foreach (var ec in EditorCommands.all) sb.Append(ec.id).Append("  ").Append(ec.title).Append('\n'); return sb.ToString(); }
                        return EditorCommands.Run(a[1]);
                    }
                case "mode":
                    {
                        if (a.Count > 1) { HistoryPanel.authorMode = a[1].StartsWith("auth"); EditorState.Set("mode.author", HistoryPanel.authorMode); }
                        return HistoryPanel.authorMode ? "Mod Author mode" : "Explore mode";
                    }
                case "codedeps": return CodeDeps.Dump(a.Count > 1 ? a[1] : null);
                case "logic":
                    {
                        // logic <Type | object> | logic shared [filter] | logic anim [filter]
                        if (!LogicPanel.Ready) return "code graph still scanning (" + CodeGraph.scannedMethods + " methods)";
                        if (a.Count > 1 && a[1] == "shared")
                        {
                            var sb = new System.Text.StringBuilder(); string f = a.Count > 2 ? a[2] : null; int n = 0;
                            foreach (var sh in CodeGraph.SharedState())
                            {
                                string l = sh.type.Name + "." + sh.member + "  set " + sh.setters + " called " + sh.callers + " read " + sh.readers + "  by " + string.Join(", ", sh.by.ToArray());
                                if (f != null && l.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                sb.Append(l).Append('\n'); if (++n >= 80) break;
                            }
                            return sb.ToString();
                        }
                        if (a.Count > 1 && a[1] == "anim") return LogicPanel.AnimDump(a.Count > 2 ? a[2] : null);
                        if (a.Count < 2) return "logic <Type|object> | logic shared [f] | logic anim [f]";
                        string q = string.Join(" ", a.GetRange(1, a.Count - 1).ToArray());
                        var t = CodeGraph.FindType(q);
                        if (t == null) { var go = Resolve(q); if (go != null) { var sb = new System.Text.StringBuilder(); foreach (var mb in go.GetComponents<MonoBehaviour>()) { if (mb == null) continue; string an = mb.GetType().Assembly.GetName().Name; if (an == "Assembly-CSharp" || an == "Assembly-CSharp-firstpass") sb.Append(LogicPanel.Report(mb.GetType())); } return sb.Length > 0 ? sb.ToString() : "no game scripts on " + go.name; } return "no type or object " + q; }
                        LogicPanel.Show(t);
                        return LogicPanel.Report(t);
                    }
                case "audio":
                    {
                        // audio rules | mute <ev> | unmute <ev> | nuke <ev> | unnuke <ev> | replace <ev> <with> | force switch|state|rtpc <name> <value> | clear <key|all>
                        string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "rules";
                        if (sub == "rules") return AudioRules.Dump();
                        if (sub == "native") return a.Count > 2 ? AudioRules.SetNative(a[2] == "on") : AudioRules.NativeLine();
                        if (sub == "mute" && a.Count > 2) return AudioRules.Change("mute:" + a[2], "true", "bridge");
                        if (sub == "unmute" && a.Count > 2) return AudioRules.Change("mute:" + a[2], null, "bridge");
                        if (sub == "nuke" && a.Count > 2) return AudioRules.Change("nuke:" + a[2], "true", "bridge");
                        if (sub == "unnuke" && a.Count > 2) return AudioRules.Change("nuke:" + a[2], null, "bridge");
                        if (sub == "replace" && a.Count > 3) return AudioRules.Change("replace:" + a[2], a[3], "bridge");
                        if (sub == "force" && a.Count > 4) return AudioRules.Change(a[2].ToLowerInvariant() + ":" + a[3], a[4], "bridge");
                        if (sub == "clear" && a.Count > 2) { if (a[2] == "all") { AudioRules.ClearAll("bridge"); return "cleared"; } return AudioRules.Change(a[2], null, "bridge"); }
                        return "audio rules | mute <ev> | unmute <ev> | nuke <ev> | unnuke <ev> | replace <ev> <with> | force switch|state|rtpc <name> <value> | clear <key|all>";
                    }
                case "hidden": return PassiveHidden.Command(a);
                case "ext": return a.Count > 1 && a[1] == "load" ? Extensions.LoadNew(core) : Extensions.Summary();
                case "live": return Extensions.Command(a, core) ?? "the live link is not installed (_mod\\InsideDev.LiveLink.dll)";
                case "adb":
                    {
                        // adb [stats] | find <text> [filter] | show <name|id> | tree <name|id> | filter <FILTER> [n]
                        string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "stats";
                        var sb = new System.Text.StringBuilder();
                        if (sub == "stats") return AudioDb.Stats();
                        if (sub == "export") return AudioExport.Run();
                        if (sub == "exportstatus") return AudioExport.status;
                        if (sub == "find" || sub == "filter")
                        {
                            var f = AudioDb.Filter.All; string q = "";
                            if (sub == "filter") { int fi = Array.IndexOf(AudioDb.FilterNames, a.Count > 2 ? a[2].ToUpperInvariant().Replace("_", " ") : ""); if (fi < 0) return "filters: " + string.Join(", ", AudioDb.FilterNames); f = (AudioDb.Filter)fi; }
                            else q = a.Count > 2 ? a[2] : "";
                            int max = 40; if (sub == "filter" && a.Count > 3) int.TryParse(a[3], out max);
                            var l = AudioDb.Query(f, q, 100000);
                            sb.Append(l.Count).Append(" record(s)\n");
                            for (int i = 0; i < l.Count && i < max; i++) sb.Append(l[i].kind).Append("  ").Append(l[i].Name).Append("  [").Append(l[i].bank).Append("]  ").Append(AudioDb.Status(l[i])).Append(l[i].refs.Count > 0 ? "  refs " + l[i].refs.Count : "").Append('\n');
                            return sb.ToString();
                        }
                        if (a.Count < 3) return "adb stats|find <text>|filter <F> [n]|show <name|id>|select <name|id>";
                        var r0 = AudioDb.Find(string.Join(" ", a.GetRange(2, a.Count - 2).ToArray()));
                        if (r0 == null) return "not found";
                        if (sub == "select") { AudioDbPanel.Select(r0); return "selected " + r0.Name; }
                        return AudioDb.Describe(r0);
                    }
                case "wwise":
                    {
                        // wwise [stats] | api [filter] | watched | trace [n] [filter] | level <0-3> | objs [filter] | banks | pid <id> | hash <name> | ids [filter]
                        string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "stats";
                        var sb = new System.Text.StringBuilder();
                        if (sub == "stacks") { if (a.Count > 2) { string m = a[2].ToLowerInvariant(); AudioSpy.stackMode = m == "off" ? 0 : m == "full" ? 2 : 1; } return "call stacks: " + AudioSpy.StackModes[AudioSpy.stackMode]; }
                        if (sub == "version") return WwiseApiPanel.VersionLine();
                        if (sub == "stock") return WwiseApiPanel.StockReport();
                        switch (sub)
                        {
                            case "stats": return WwiseApiPanel.Diagnostics() + AudioTrace.Status();
                            case "api": return WwiseApiPanel.Dump(a.Count > 2 ? a[2] : null, false);
                            case "watched": return WwiseApiPanel.Dump(a.Count > 2 ? a[2] : null, true);
                            case "level": if (a.Count > 2) WwiseNative.SetLevel(int.Parse(a[2])); return "level " + WwiseNative.Level;
                            case "hash": { string nm = string.Join(" ", a.GetRange(2, a.Count - 2).ToArray()); return nm + " = " + AudioTrace.Hash(nm) + "  (known as: " + AudioTrace.Name(AudioTrace.Hash(nm)) + ")"; }
                            case "pid": return AudioTrace.DescribePlay(uint.Parse(a[2]));
                            case "trace":
                                {
                                    int n = 60; string f = null;
                                    if (a.Count > 2 && !int.TryParse(a[2], out n)) { n = 60; f = a[2]; }
                                    if (a.Count > 3) f = a[3];
                                    int shown = 0;
                                    for (int i = 0; i < AudioTrace.Count && shown < n; i++)
                                    {
                                        var e = AudioTrace.Get(i); if (e == null) break;
                                        string tx = AudioTrace.Kind(e.r.type) + "  " + AudioTrace.Text(e, false);
                                        if (f != null && tx.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                        sb.Append(e.t.ToString("0.000")).Append("s f").Append(e.frame).Append("  ").Append(tx).Append('\n'); shown++;
                                    }
                                    return sb.Length > 0 ? sb.ToString() : "(no records)";
                                }
                            case "objs":
                                foreach (var o in AudioTrace.objs.Values)
                                {
                                    string nm = AudioTrace.ObjName(o.id);
                                    if (a.Count > 2 && nm.IndexOf(a[2], StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    var u = AudioTrace.Unity(o.id);
                                    sb.Append((int)o.id).Append("  ").Append(nm).Append(o.registered ? "  registered" : "  (unregistered)").Append("  posts ").Append(o.posts).Append("  ").Append(u != null ? Inspector.PathOf(u.transform) : "(no live Unity object)").Append('\n');
                                }
                                return sb.Length > 0 ? sb.ToString() : "(none)";
                            case "banks":
                                foreach (var b in AudioTrace.banks.Values) sb.Append(b.id).Append("  ").Append(b.name ?? AudioTrace.Name(b.id)).Append("  ").Append(b.status).Append("  frame ").Append(b.frame).Append('\n');
                                return sb.Length > 0 ? sb.ToString() : "(no bank activity observed since boot)";
                            case "ids":
                                foreach (var id in AudioTrace.unknownIds) { sb.Append("unknown ").Append(id).Append('\n'); if (sb.Length > 4000) break; }
                                return AudioTrace.KnownIds + " named ids\n" + sb;
                        }
                        return "wwise stats|api [f]|watched|trace [n] [f]|level n|objs [f]|banks|pid <id>|hash <name>|ids";
                    }
                case "sounds":
                    {
                        // sounds [filter]  |  sounds audition <event>  |  sounds show <event>
                        if (a.Count > 2 && a[1] == "audition") return SoundLibrary.Audition(a[2]);
                        if (a.Count > 3 && (a[1] == "override" || a[1] == "revert" || a[1] == "post" || a[1] == "trigger"))
                        {
                            // sounds override <event> <binding#> <newEvent> | revert/post/trigger <event> <binding#>
                            SoundLibrary.Def d0; if (!SoundLibrary.defs.TryGetValue(a[2], out d0)) return "unknown sound";
                            int bi; if (!int.TryParse(a[3], out bi) || bi < 0 || bi >= d0.bindings.Count) return "binding index 0.." + (d0.bindings.Count - 1);
                            var b0 = d0.bindings[bi];
                            if (a[1] == "override") return a.Count > 4 ? SoundLibrary.Override(b0, a[4]) : "need new event name";
                            if (a[1] == "revert") return SoundLibrary.Revert(b0);
                            if (a[1] == "post") return SoundLibrary.PostFrom(b0, SoundLibrary.CurrentName(b0) ?? d0.name);
                            return SoundLibrary.CanTriggerOriginal(b0) ? SoundLibrary.TriggerOriginal(b0) : "no game path for this binding";
                        }
                        if (a.Count > 2 && a[1] == "show")
                        {
                            SoundLibrary.Def d; if (!SoundLibrary.defs.TryGetValue(a[2], out d)) return "unknown sound";
                            var sbs = new StringBuilder(d.name + "  used by " + d.UsedBy + ", posts " + d.posts + ", auditions " + d.auditions + "\n");
                            int bn = 0;
                            foreach (var b in d.bindings) if (bn >= 40) { if (bn++ == 40) sbs.Append("  … ").Append(d.bindings.Count - 40).Append(" more\n"); } else sbs.Append("  #").Append(bn++).Append(" [").Append(b.prov).Append("] ").Append(b.go != null ? Inspector.PathOf(b.go.transform) : "(gone)").Append("  ").Append(b.where).Append(b.postTarget != null ? "  (plays on " + b.postTarget.name + ")" : "").Append(b.observedPosts > 0 ? "  heard " + b.observedPosts + "x" : "").Append(SoundLibrary.CanTriggerOriginal(b) ? "  [can trigger original]" : "").Append(b.original != null ? "  OVERRIDDEN now '" + SoundLibrary.CurrentName(b) + "'" : "").Append('\n');
                            return sbs.ToString();
                        }
                        string flt = a.Count > 1 ? a[1] : "";
                        var list = new List<SoundLibrary.Def>();
                        foreach (var d in SoundLibrary.defs.Values) if (d.bindings.Count > 0 && (flt.Length == 0 || d.name.IndexOf(flt, StringComparison.OrdinalIgnoreCase) >= 0)) list.Add(d);
                        list.Sort((x, y) => y.UsedBy.CompareTo(x.UsedBy));
                        var sb2 = new StringBuilder(SoundLibrary.Status() + "\n");
                        for (int i = 0; i < list.Count && i < 40; i++) sb2.Append("  ").Append(list[i].name).Append("   used by ").Append(list[i].UsedBy).Append("   bindings ").Append(list[i].bindings.Count).Append("   posts ").Append(list[i].posts).Append('\n');
                        return sb2.ToString();
                    }
                case "graph":
                    if (a.Count > 2 && (a[1] == "mode")) { GraphView.SetMode(a[2]); return GraphView.Dump(0); }
                    if (a.Count > 1 && a[1] == "live") { if (a.Count > 2) GraphView.SetLive(a[2]); return GraphView.LiveDump(); }   // graph live [s f a n | all | none]
                    if (a.Count > 2) GraphView.SetFilter(a[2]);   // graph <hops> [f|s|m|o combination | all]
                    return GraphView.Dump(a.Count > 1 ? (int)F(a[1]) : 0);
                case "sel":
                    {
                        if (a.Count > 1 && a[1] == "back") Selection.Back();
                        else if (a.Count > 1 && a[1] == "fwd") Selection.Forward();
                        else if (a.Count > 1 && a[1] == "clear") Selection.Clear("bridge");
                        return Selection.Describe() + "   history " + Selection.HistoryCount + "   explorer: " + SceneExplorer.Status();
                    }
                case "children":
                    {
                        var go = Resolve(Rest(line)); if (go == null) return "not found";
                        var sb = new StringBuilder();
                        for (int i = 0; i < go.transform.childCount; i++) { var ch = go.transform.GetChild(i); sb.Append(ch.gameObject.activeSelf ? "  " : "  [off] ").Append(ch.name).Append(ch.childCount > 0 ? "  (" + ch.childCount + ")" : "").Append('\n'); }
                        return sb.ToString();
                    }
                case "near": return Near(a.Count > 1 ? F(a[1]) : 15f, a.Count > 2 ? a[2] : null);
                case "triggers":
                    if (a.Count > 1 && (a[1] == "on" || a[1] == "off")) { Commands.Run(line, core); return "trigger overlay " + a[1]; }
                    return Triggers(a.Count > 1 ? F(a[1]) : 20f);
                case "hiddennear":
                    {
                        core.ScanHidden();
                        var ch = G.MainCharacter; var o = ch != null ? ch.pos3 : Vector3.zero; float r = a.Count > 1 ? F(a[1]) : 30f;
                        var sb = new StringBuilder();
                        foreach (var h in core.hidden) if (h.go != null && (h.go.transform.position - o).magnitude <= r) sb.Append(((h.go.transform.position - o).magnitude).ToString("0.0", IC)).Append("m  ").Append(Inspector.PathOf(h.go.transform)).Append("  [").Append(h.kind).Append("]\n");
                        return sb.Length > 0 ? sb.ToString() : "none within " + r + "m";
                    }
                case "setactive":
                    {
                        var go = Resolve(a[1]); if (go == null) return "not found";
                        bool on = On(a[2]); Changes.Record(go, null, "active", go.activeSelf); go.SetActive(on);
                        return Inspector.PathOf(go.transform) + " activeSelf=" + go.activeSelf + " inHierarchy=" + go.activeInHierarchy;
                    }
                case "enable":
                    {
                        var go = Resolve(a[1]); if (go == null) return "not found";
                        var comp = FindComp(go, a[2]); if (comp == null) return "component not found";
                        bool on = On(a[3]);
                        var b = comp as Behaviour; var r = comp as Renderer; var col = comp as Collider;
                        if (b != null) { Changes.Record(go, comp, comp.GetType().Name, b.enabled); b.enabled = on; }
                        else if (r != null) { Changes.Record(go, comp, "renderer", r.enabled); r.enabled = on; }
                        else if (col != null) { Changes.Record(go, comp, "collider", col.enabled); col.enabled = on; }
                        else return "component has no enabled flag";
                        return comp.GetType().Name + " enabled=" + on;
                    }
                case "get":
                    {
                        var go = Resolve(a[1]); if (go == null) return "not found";
                        var comp = FindComp(go, a[2]); if (comp == null) return "component not found";
                        var fi = FindField(comp.GetType(), a[3]);
                        if (fi != null) return a[3] + " = " + Describe(fi.GetValue(comp));
                        var pi = comp.GetType().GetProperty(a[3], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        return pi != null ? a[3] + " = " + Describe(pi.GetValue(comp, null)) : "field/property not found";
                    }
                case "set":
                    {
                        // set <object> <Component> <field|property> <value...>
                        var go = Resolve(a[1]); if (go == null) return "not found";
                        var comp = FindComp(go, a[2]); if (comp == null) return "component not found";
                        string val = string.Join(" ", a.GetRange(4, a.Count - 4).ToArray());
                        var fi = FindField(comp.GetType(), a[3]);
                        if (fi != null) { ChangeRecorder.BeforeField(comp, fi.Name, "bridge"); fi.SetValue(comp, Parse(val, fi.FieldType, fi.GetValue(comp))); return a[3] + " = " + Describe(fi.GetValue(comp)); }
                        var pi = comp.GetType().GetProperty(a[3], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (pi != null && pi.CanWrite) { pi.SetValue(comp, Parse(val, pi.PropertyType, pi.GetValue(comp, null)), null); return a[3] + " = " + Describe(pi.GetValue(comp, null)); }
                        return "field/property not found or read-only";
                    }
                case "anim":
                    {
                        // anim <object> [rebind]   lists the legacy animation states (or rebuilds their bindings)
                        var go = Resolve(a[1]); if (go == null) return "not found";
                        var an = go.GetComponent<Animation>(); if (an == null) return "no Animation on " + go.name;
                        if (a.Count > 2 && a[2] == "rebind") { Reparent.RebindAnimation(an); return "rebuilt bindings of " + go.name; }
                        var sb2 = new StringBuilder(go.name + ": playing=" + an.isPlaying + " culling=" + an.cullingType + "\n");
                        foreach (AnimationState st in an) sb2.Append("  ").Append(st.name).Append("  enabled=").Append(st.enabled).Append(" time=").Append(st.time.ToString("0.00", IC)).Append("/").Append(st.length.ToString("0.00", IC)).Append(" weight=").Append(st.weight.ToString("0.00", IC)).Append(" speed=").Append(st.speed.ToString("0.00", IC)).Append('\n');
                        return sb2.ToString();
                    }
                case "reparent":
                    {
                        // reparent <object> <newParent|none|up>   (keeps the world position)
                        var go = Resolve(a[1]); if (go == null) return "not found";
                        if (a.Count < 3) return "usage: reparent <obj> <newParent|none|up>";
                        if (a[2] == "none") return Reparent.Do(go, null, "bridge");
                        if (a[2] == "up") return Reparent.Do(go, go.transform.parent != null ? go.transform.parent.parent : null, "bridge");
                        if (a[2] == "restore") return Reparent.Restore(go);
                        var np = Resolve(a[2]); if (np == null) return "new parent not found";
                        return Reparent.Do(go, np.transform, "bridge");
                    }
                case "call":
                    {
                        // call <object> <Component> <method>   (no-arg methods, incl. private)
                        var go = Resolve(a[1]); if (go == null) return "not found";
                        var comp = FindComp(go, a[2]); if (comp == null) return "component not found";
                        var mi = comp.GetType().GetMethod(a[3], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        if (mi == null) return "no parameterless method " + a[3];
                        var r = mi.Invoke(comp, null);
                        return "called " + comp.GetType().Name + "." + a[3] + (mi.ReturnType != typeof(void) ? " -> " + Describe(r) : "");
                    }
                case "fsm":
                    {
                        // fsm <object> [fsmName] event|state|var <name> [value]
                        var go = Resolve(a[1]); if (go == null) return "not found";
                        int i = 2;
                        PlayMakerFSM fsm = null;
                        foreach (var x in go.GetComponents<PlayMakerFSM>()) if (a.Count > 2 && x.FsmName == a[2]) { fsm = x; i = 3; }
                        if (fsm == null) fsm = go.GetComponent<PlayMakerFSM>();
                        if (fsm == null) return "no PlayMakerFSM on object";
                        string op = a[i].ToLowerInvariant(); string arg = a.Count > i + 1 ? a[i + 1] : "";
                        if (op == "event") { ChangeRecorder.Action(go, "fsmEvent", fsm.FsmName, arg, "bridge"); fsm.SendEvent(arg); return fsm.FsmName + " <- event '" + arg + "'  now in state [" + fsm.ActiveStateName + "]"; }
                        if (op == "state") { ChangeRecorder.Action(go, "fsmState", fsm.FsmName, arg, "bridge"); fsm.SetState(arg); return fsm.FsmName + " -> state [" + fsm.ActiveStateName + "]"; }
                        if (op == "var")
                        {
                            var v = fsm.FsmVariables.GetVariable(arg);
                            if (v == null) return "no variable " + arg;
                            if (a.Count > i + 2) { string val = a[i + 2]; SetFsmVar(v, val); }
                            return arg + " = " + v.RawValue;
                        }
                        // wire <state|*> <event> <toState>   unwire <state|*> <event>   resetwires   events
                        if (op == "wire" && a.Count > i + 3) return FsmEdit.Wire(fsm, a[i + 1], a[i + 2], a[i + 3], "bridge");
                        if (op == "unwire" && a.Count > i + 2) return FsmEdit.Unwire(fsm, a[i + 1], a[i + 2], "bridge");
                        if (op == "resetwires") return FsmEdit.Reset(fsm);
                        if (op == "events") return string.Join(", ", FsmEdit.EventNames(fsm).ToArray());
                        return "usage: fsm <obj> [fsmName] event|state|var <name> [value] | wire <state|*> <event> <toState> | unwire <state|*> <event> | resetwires | events";
                    }
                case "signal":
                    {
                        // signal <object> <signalOutName>   fires every connection of that output
                        var go = Resolve(a[1]); if (go == null) return "not found";
                        var sm = PersistentBehaviour<SignalManager>.instance;
                        int n = 0;
                        ChangeRecorder.Action(go, "signal", null, a[2], "bridge");
                        foreach (var s in sm.signalOuts) if (s != null && s.isActive && s.gameObject == go && (s.debugName == a[2])) { s.Signal(); n++; }
                        if (n == 0) foreach (var cn in sm.connections) if (cn != null && cn.isActive && cn.signalOutGameObject == go && cn.signalOutName == a[2]) { if (cn.signalIn != null && cn.signalIn.action != null) cn.signalIn.action(); else if (cn.fsmIn != null) cn.fsmIn.Event(cn.signalInName); n++; }
                        return "fired " + n + " output(s)";
                    }
                case "signalin":
                    {
                        // signalin <object> <signalInName>   invokes the input handler directly
                        var go = Resolve(a[1]); if (go == null) return "not found";
                        var sm = PersistentBehaviour<SignalManager>.instance; int n = 0;
                        foreach (var s in sm.signalIns) if (s != null && s.isActive && s.gameObject == go && s.debugName == a[2] && s.action != null) { s.action(); n++; }
                        return "invoked " + n + " input(s)";
                    }
                case "audiolog":
                    {
                        int n = a.Count > 1 ? (int)F(a[1]) : 40; var sb = new StringBuilder();
                        for (int i = Math.Max(0, AudioSpy.log.Count - n); i < AudioSpy.log.Count; i++) { var e = AudioSpy.log[i]; sb.Append(e.frame).Append(' ').Append(e.kind).Append(' ').Append(e.name).Append(" @").Append(e.source).Append(' ').Append(e.detail).Append('\n'); }
                        return sb.ToString();
                    }
                case "waits":
                    {
                        AudioLinks.Scan(true); var sb = new StringBuilder();
                        foreach (var w in AudioLinks.waits) if (w.fsm != null) sb.Append(w.fsm.ActiveStateName == w.state ? "WAITING " : "        ").Append(w.kind).Append(" -> ").Append(Inspector.PathOf(w.fsm.transform)).Append('/').Append(w.fsm.FsmName).Append(" [").Append(w.state).Append("] fires '").Append(w.finishEvent).Append("'\n");
                        return sb.Length > 0 ? sb.ToString() : "no audio waits loaded";
                    }
                case "timed":
                    {
                        AudioTimed.Scan(true); var sb = new StringBuilder();
                        foreach (var s in AudioTimed.systems)
                        {
                            if (s.comp == null) continue;
                            sb.Append(s.typeName).Append(" on ").Append(Inspector.PathOf(s.comp.transform)).Append(s.comp.enabled ? "" : " [disabled]").Append(s.comp.gameObject.activeInHierarchy ? "" : " [hidden]").Append('\n');
                            foreach (var t in s.timers) sb.Append("   ").Append(t.field).Append(" @").Append(t.muted ? "MUTED" : t.original.ToString("0.###", IC)).Append("s fired ").Append(t.fireCount).Append("x\n");
                        }
                        return sb.Length > 0 ? sb.ToString() : "no audio-timed systems loaded";
                    }
                case "timer":
                    {
                        // timer <TypeName> <field> <seconds|mute|unmute|fire>
                        AudioTimed.Scan(true);
                        foreach (var s in AudioTimed.systems)
                            if (s.comp != null && s.typeName.Equals(a[1], StringComparison.OrdinalIgnoreCase))
                                foreach (var t in s.timers)
                                    if (t.field.Equals(a[2], StringComparison.OrdinalIgnoreCase))
                                    {
                                        string op = a[3].ToLowerInvariant();
                                        if (op == "mute") AudioTimed.SetMuted(t, true);
                                        else if (op == "unmute") AudioTimed.SetMuted(t, false);
                                        else if (op == "fire") AudioTimed.FireNow(t);
                                        else { t.original = F(op); t.chk.SetEventTime(t.original); }
                                        return s.typeName + "." + t.field + " -> " + (t.muted ? "MUTED" : t.original.ToString("0.###", IC) + "s");
                                    }
                        return "timer not found (use 'timed' to list)";
                    }
                case "music":
                    {
                        var m = PersistentBehaviour<GlobalAudio>.instance.music;
                        return string.Format(IC, "music pos {0:0.000}s loop {1:0.000}s beat {2} bar {3}", m.GetMusicPosition_s(), m.GetLoopLength_s(), m.LastBeat, m.Bar);
                    }
                case "remote":
                    if (a.Count > 1 && a[1] == "off") enabled = false;
                    return "remote " + (enabled ? "on" : "off");
                case "help":
                    return "remote: ping | state | diag | cams | render auto|eof|overlay|gamecam|probe|reset|snapshot|board | panel on|off [tab#]\n" +
                           "        focustest <n> [awayMs] [backMs] [alttab|minimize] [bg on|off] | focustest stop|status | bg on|off | fullscreen on|off | res <w> <h> [full]\n" +
                           "        screenshot [tag] | inspect <obj> | children <obj> | near [r] [filter] | triggers [r] | hiddennear [r]\n" +
                           "        setactive <obj> on|off | enable <obj> <Comp> on|off | get/set <obj> <Comp> <field> [value] | call <obj> <Comp> <method>\n" +
                           "        fsm <obj> [fsmName] event|state|var <name> [value] | fsm <obj> wire <state|*> <event> <toState> | unwire <state|*> <event> | resetwires | events\n" +
                           "        signal <obj> <out> | signalin <obj> <in>\n" +
                           "        mats <obj> | origin <obj> [deep] (level file vs live) | graph live [s f a n|all|none]\n" +
                           "        fsmgraph <obj> [fsmName] (opens the FSM Graph panel) | postfx [on|off|default <T> | set <T> <f> <v>] | fsmfind <text> | fsmset <obj>|<state>|<i>|<field>|<value> | sigs <obj> | sigwire|sigunwire <fsm obj>|<>in>|<sender>|<out>\n" +
                           "        mem [obj|on|off] | memwhat <hex> | memmethod <hex> | memread <hex> [n] | memjit <Type> <method> [+hex] | memexport (Cheat Engine)\n" +
                           "        audiolog [n] | waits | timed | timer <Type> <field> <s|mute|unmute|fire> | music\n" +
                           "        <obj> = name (nearest match), full/path, or #instanceID.  Quote names with spaces: \"My Obj\".\n" +
                           "plus every console command (tp, spawn, god, ts, kill, find, sel, flag, post, cue, shockwave, catalog...)";
                default:
                    { var er = Extensions.Command(a, core); if (er != null) return er; }
                    Commands.Run(line, core);
                    return null;
            }
        }

        // ---------------------------------------------------------------- snapshot
        public static string State(DevCore core)
        {
            var sb = new StringBuilder();
            sb.Append("InsideDev ").Append(Boot.Version).Append("   ").Append(DateTime.Now.ToString("HH:mm:ss")).Append("   frame ").Append(Time.frameCount).Append('\n');
            sb.Append("render: backend ").Append(RenderHost.requested).Append("->").Append(RenderHost.active).Append("  submits ").Append(RenderHost.submitCount).Append(" fails ").Append(RenderHost.submitFails)
              .Append("  focus lost/regained ").Append(RenderHost.focusLostCount).Append('/').Append(RenderHost.focusRegainCount).Append("  probe ").Append(RenderHost.probeResult).Append('\n');
            var ch = G.MainCharacter;
            sb.Append("character: ").Append(ch != null ? ch.name + " pos " + V(ch.pos3) + (ch.isDead ? " DEAD" : "") : "none").Append("   god ").Append(G.God).Append("   timescale ").Append(G.TimeScale.ToString("0.##", IC)).Append('\n');
            int sub, sp;
            if (G.CurrentSavepoint(out sub, out sp)) sb.Append("savepoint: ").Append(G.SubsceneLabel(sub)).Append(" #").Append(sp).Append("  (spawn ").Append(sub).Append(' ').Append(sp).Append(")\n");
            try { var m = PersistentBehaviour<GlobalAudio>.instance.music; sb.Append(string.Format(IC, "music: {0:0.00}s / {1:0.00}s\n", m.GetMusicPosition_s(), m.GetLoopLength_s())); } catch { }
            sb.Append("streaming: ").Append(Levels.activeCount).Append(" active / ").Append(Levels.loadedCount).Append(" loaded\n");
            var active = new List<string>();
            foreach (var kv in Levels.state) if (kv.Value == "active") active.Add(kv.Key);
            sb.Append("active scenes: ").Append(string.Join(", ", active.ToArray())).Append('\n');
            if (Shockwave.Mgr != null)
            {
                var m = Shockwave.Mgr;
                sb.Append("shockwave: in cover ").Append(ForcePushManager.GetInCover()).Append("  zones ").Append(ForcePushManager.getActiveZoneCount()).Append("  nokill ").Append(Shockwave.noKill).Append("  blow@").Append(Shockwave.CheckerTime("blow").ToString("0.00", IC)).Append("s cycle ").Append(m.musicCycleTime).Append('\n');
            }
            sb.Append("\nnearest triggers:\n").Append(Triggers(15f, 25));
            sb.Append("\nFSMs waiting on audio right now:\n");
            foreach (var w in AudioLinks.waits)
                if (w.fsm != null && w.fsm.ActiveStateName == w.state && w.fsm.gameObject.activeInHierarchy)
                    sb.Append("  ").Append(w.kind).Append(" -> ").Append(Inspector.PathOf(w.fsm.transform)).Append(" [").Append(w.state).Append("] fires '").Append(w.finishEvent).Append("'\n");
            sb.Append("\nlast audio events:\n");
            for (int i = Math.Max(0, AudioSpy.log.Count - 15); i < AudioSpy.log.Count; i++) { var e = AudioSpy.log[i]; sb.Append("  ").Append(e.frame).Append(' ').Append(e.kind).Append(' ').Append(e.name).Append(" @").Append(e.source).Append('\n'); }
            sb.Append("\nchanges by user: ").Append(Changes.list.Count).Append('\n');
            lock (DevLog.Lines)
            {
                sb.Append("\nconsole tail:\n");
                for (int i = Math.Max(0, DevLog.Lines.Count - 12); i < DevLog.Lines.Count; i++) sb.Append("  ").Append(DevLog.Lines[i]).Append('\n');
            }
            return sb.ToString();
        }

        static string Triggers(float radius, int max = 60)
        {
            var ch = G.MainCharacter; var o = ch != null ? ch.pos3 : Vector3.zero;
            var list = new List<KeyValuePair<float, ColliderEntry>>();
            if (DevCore.Instance == null) return "";
            foreach (var e in DevCore.Instance.Overlay.entries)
            {
                if (e.col == null || !e.trigger || !e.col.gameObject.activeInHierarchy) continue;
                float d = (e.col.bounds.center - o).magnitude;
                if (d <= radius) list.Add(new KeyValuePair<float, ColliderEntry>(d, e));
            }
            list.Sort((x, y) => x.Key.CompareTo(y.Key));
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(max, list.Count); i++)
            {
                var e = list[i].Value;
                sb.Append("  ").Append(list[i].Key.ToString("0.0", IC)).Append("m  ").Append(Inspector.PathOf(e.col.transform)).Append(e.col.enabled ? "" : " (off)").Append("  <").Append(e.types).Append(">  #").Append(e.col.gameObject.GetInstanceID()).Append('\n');
            }
            return sb.Length > 0 ? sb.ToString() : "  none within " + radius + "m (trigger scan on? F2)\n";
        }

        static string Near(float radius, string filter)
        {
            var ch = G.MainCharacter; var o = ch != null ? ch.pos3 : Vector3.zero;
            var list = new List<KeyValuePair<float, Transform>>();
            foreach (var t in Inspector.AllSceneTransforms())
            {
                if (t == null) continue;
                float d = (t.position - o).magnitude;
                if (d > radius) continue;
                if (filter != null && t.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                list.Add(new KeyValuePair<float, Transform>(d, t));
            }
            list.Sort((x, y) => x.Key.CompareTo(y.Key));
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(150, list.Count); i++)
            {
                var t = list[i].Value;
                sb.Append(list[i].Key.ToString("0.0", IC)).Append("m  ").Append(t.gameObject.activeInHierarchy ? "" : "[hidden] ").Append(Inspector.PathOf(t)).Append("  #").Append(t.gameObject.GetInstanceID());
                var comps = t.GetComponents<MonoBehaviour>();
                if (comps.Length > 0) { sb.Append("  <"); foreach (var c in comps) if (c != null) sb.Append(c.GetType().Name).Append(' '); sb.Append('>'); }
                sb.Append('\n');
            }
            return list.Count + " object(s) within " + radius + "m" + (list.Count > 150 ? " (showing 150)" : "") + "\n" + sb;
        }

        public static string Inspect(GameObject go)
        {
            if (go == null) return "not found";
            var sb = new StringBuilder();
            var t = go.transform;
            sb.Append(Inspector.PathOf(t)).Append("   #").Append(go.GetInstanceID()).Append('\n');
            sb.Append("activeSelf ").Append(go.activeSelf).Append("  inHierarchy ").Append(go.activeInHierarchy).Append("  layer ").Append(go.layer).Append("  tag ").Append(go.tag).Append('\n');
            sb.Append("pos ").Append(V(t.position)).Append("  rot ").Append(V(t.eulerAngles)).Append("  scale ").Append(V(t.lossyScale)).Append("  area ").Append(AudioCatalog.AreaOf(go)).Append('\n');
            if (t.parent != null) sb.Append("parent ").Append(Inspector.PathOf(t.parent)).Append('\n');
            sb.Append("children ").Append(t.childCount).Append('\n');
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) { sb.Append("\n[missing script]\n"); continue; }
                var ty = comp.GetType();
                string en = comp is Behaviour ? " enabled=" + ((Behaviour)comp).enabled : comp is Renderer ? " enabled=" + ((Renderer)comp).enabled : comp is Collider ? " enabled=" + ((Collider)comp).enabled : "";
                sb.Append("\n[").Append(ty.FullName).Append("]").Append(en).Append('\n');
                var col = comp as Collider;
                if (col != null) sb.Append("  isTrigger ").Append(col.isTrigger).Append("  bounds ").Append(V(col.bounds.center)).Append(" size ").Append(V(col.bounds.size)).Append('\n');
                if (comp is PlayMakerFSM) { DumpFsm(sb, (PlayMakerFSM)comp); continue; }
                if (comp is Transform || comp is Renderer || comp is MeshFilter) continue;
                int n = 0;
                for (var ct = ty; ct != null && ct != typeof(MonoBehaviour) && ct != typeof(Behaviour) && ct != typeof(Component) && ct != typeof(UObj); ct = ct.BaseType)
                    foreach (var f in ct.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (f.Name.IndexOf('<') >= 0) continue;
                        if (++n > 120) break;
                        object v; try { v = f.GetValue(comp); } catch { v = "?"; }
                        sb.Append("  ").Append(f.Name).Append(" = ").Append(Describe(v)).Append('\n');
                    }
            }
            // signal wiring
            var sm = PersistentBehaviour<SignalManager>.instance;
            if (sm != null)
            {
                sb.Append("\nsignals:\n");
                foreach (var c in sm.connections)
                {
                    if (c == null || !c.isActive) continue;
                    if (c.signalOutGameObject == go) sb.Append("  OUT ").Append(c.signalOutName).Append(" -> ").Append(c.signalInGameObject != null ? Inspector.PathOf(c.signalInGameObject.transform) : "?").Append('.').Append(c.signalInName).Append(c.IsInputFsm ? " (fsm)" : "").Append('\n');
                    if (c.signalInGameObject == go) sb.Append("  IN  ").Append(c.signalOutGameObject != null ? Inspector.PathOf(c.signalOutGameObject.transform) : "?").Append('.').Append(c.signalOutName).Append(" -> ").Append(c.signalInName).Append('\n');
                }
                foreach (var s in sm.signalOuts) if (s != null && s.isActive && s.gameObject == go) sb.Append("  declared out: ").Append(s.debugName).Append('\n');
                foreach (var s in sm.signalIns) if (s != null && s.isActive && s.gameObject == go) sb.Append("  declared in:  ").Append(s.debugName).Append('\n');
            }
            return sb.ToString();
        }

        public static string InspectDeep(GameObject go)
        {
            if (go == null) return "not found";
            var sb = new StringBuilder();
            sb.Append(Inspector.PathOf(go.transform)).Append("   #").Append(go.GetInstanceID()).Append("   (deep: nested game data expanded, 4 levels)\n");
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null || comp is Transform) continue;
                sb.Append("\n[").Append(comp.GetType().FullName).Append("]\n");
                if (comp is PlayMakerFSM)
                {
                    var fsm = (PlayMakerFSM)comp;
                    foreach (var st in fsm.FsmStates)
                    {
                        if (st == null) continue;
                        sb.Append("  state [").Append(st.Name).Append("]").Append(st.Name == fsm.ActiveStateName ? "  <== ACTIVE" : "").Append('\n');
                        if (st.Actions != null)
                            foreach (var ac in st.Actions)
                            {
                                if (ac == null) continue;
                                sb.Append("    action ").Append(ac.GetType().Name).Append('\n');
                                foreach (var f in ValueDump.Fields(ac.GetType(), false))
                                {
                                    if (f.DeclaringType == typeof(FsmStateAction)) continue;
                                    object v; try { v = f.GetValue(ac); } catch { continue; }
                                    if (ValueDump.IsNested(v)) { sb.Append("      ").Append(f.Name).Append(" : ").Append(v.GetType().Name).Append('\n'); ValueDump.Tree(sb, v, "        ", 4, false); }
                                    else sb.Append("      ").Append(f.Name).Append(" = ").Append(ValueDump.Short(v)).Append('\n');
                                }
                            }
                    }
                    continue;
                }
                ValueDump.Tree(sb, comp, "  ", 4, false);
            }
            return sb.ToString();
        }

        static void DumpFsm(StringBuilder sb, PlayMakerFSM fsm)
        {
            sb.Append("  fsm '").Append(fsm.FsmName).Append("' active state [").Append(fsm.ActiveStateName).Append("]\n");
            if (fsm.FsmGlobalTransitions != null) foreach (var gt in fsm.FsmGlobalTransitions) sb.Append("  global: on '").Append(gt.EventName).Append("' -> ").Append(gt.ToState).Append('\n');
            if (fsm.FsmStates != null)
                foreach (var st in fsm.FsmStates)
                {
                    if (st == null) continue;
                    sb.Append("  state [").Append(st.Name).Append("]").Append(st.Name == fsm.ActiveStateName ? "  <== ACTIVE" : "").Append('\n');
                    if (st.Actions != null)
                        foreach (var ac in st.Actions)
                        {
                            if (ac == null) continue;
                            sb.Append("     action ").Append(ac.GetType().Name).Append(ac.Enabled ? "" : " [off]");
                            // key parameters of the action
                            int k = 0;
                            foreach (var f in ValueDump.Fields(ac.GetType(), false))
                            {
                                if (k > 12) break;
                                if (!ValueDump.IsSerialized(f) || f.DeclaringType == typeof(FsmStateAction)) continue;
                                object v; try { v = f.GetValue(ac); } catch { continue; }
                                string d = ActionParam(v);
                                if (d == null) continue;
                                sb.Append(k == 0 ? "  { " : ", ").Append(f.Name).Append('=').Append(d);
                                k++;
                            }
                            if (k > 0) sb.Append(" }");
                            sb.Append('\n');
                        }
                    if (st.Transitions != null) foreach (var tr in st.Transitions) sb.Append("     on '").Append(tr.EventName).Append("' -> ").Append(tr.ToState).Append('\n');
                }
            try
            {
                var vars = fsm.FsmVariables.GetAllNamedVariables();
                if (vars != null && vars.Length > 0) { sb.Append("  variables: "); foreach (var v in vars) sb.Append(v.Name).Append('=').Append(v.RawValue).Append("  "); sb.Append('\n'); }
            }
            catch { }
        }

        public static string ActionParam(object v)
        {
            if (v == null) return null;
            var nv = v as NamedVariable;
            if (nv != null)
            {
                if (nv.UseVariable && !string.IsNullOrEmpty(nv.Name)) return "{" + nv.Name + "}";
                object raw = null; try { raw = nv.RawValue; } catch { }
                var ro = raw as UObj;
                if (ro != null) return ro.name;
                return raw == null ? null : raw.ToString();
            }
            var fe = v as FsmEvent; if (fe != null) return "event:" + fe.Name;
            var fo = v as FsmOwnerDefault; if (fo != null) { var g = fo.GameObject != null ? fo.GameObject.Value : null; return g != null ? g.name : "owner"; }
            if (v is string || v.GetType().IsPrimitive || v.GetType().IsEnum) return v.ToString();
            var uo = v as UObj; if (uo != null) return uo.name;
            if (ValueDump.IsNested(v)) return ValueDump.Compact(v, 4);
            return null;
        }

        // ---------------------------------------------------------------- helpers
        public static GameObject Resolve(string q)
        {
            if (string.IsNullOrEmpty(q)) return null;
            q = q.Trim().Trim('"');
            var all = Inspector.AllSceneTransforms();
            if (q.StartsWith("#"))
            {
                int id;
                if (int.TryParse(q.Substring(1), out id)) foreach (var t in all) if (t != null && t.gameObject.GetInstanceID() == id) return t.gameObject;
                return null;
            }
            var ch = G.MainCharacter; var o = ch != null ? ch.pos3 : Vector3.zero;
            if (q.IndexOf('/') >= 0)
            {
                foreach (var t in all) if (t != null && Inspector.PathOf(t) == q) return t.gameObject;
                foreach (var t in all) if (t != null && Inspector.PathOf(t).EndsWith(q, StringComparison.OrdinalIgnoreCase)) return t.gameObject;
            }
            Transform best = null; float bd = float.MaxValue;
            foreach (var t in all) if (t != null && t.name == q) { float d = (t.position - o).sqrMagnitude; if (d < bd) { bd = d; best = t; } }
            if (best == null) foreach (var t in all) if (t != null && t.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { float d = (t.position - o).sqrMagnitude; if (d < bd) { bd = d; best = t; } }
            return best != null ? best.gameObject : null;
        }

        static Component FindComp(GameObject go, string name)
        {
            foreach (var c in go.GetComponents<Component>()) if (c != null && c.GetType().Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return c;
            foreach (var c in go.GetComponents<Component>()) if (c != null && c.GetType().Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return c;
            return null;
        }

        static FieldInfo FindField(Type t, string name)
        {
            for (var ty = t; ty != null; ty = ty.BaseType)
            {
                var f = ty.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        static object Parse(string s, Type t, object current)
        {
            if (t == typeof(string)) return s;
            if (t == typeof(bool)) return On(s);
            if (t.IsEnum) return Enum.Parse(t, s, true);
            if (t == typeof(Vector3)) { var p = s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries); return new Vector3(F(p[0]), F(p[1]), F(p[2])); }
            if (t == typeof(Vector2)) { var p = s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries); return new Vector2(F(p[0]), F(p[1])); }
            if (typeof(NamedVariable).IsAssignableFrom(t) && current != null) { SetFsmVar((NamedVariable)current, s); return current; }
            return Convert.ChangeType(s, t, IC);
        }

        static void SetFsmVar(NamedVariable v, string s)
        {
            if (v is FsmBool) ((FsmBool)v).Value = On(s);
            else if (v is FsmFloat) ((FsmFloat)v).Value = F(s);
            else if (v is FsmInt) ((FsmInt)v).Value = (int)F(s);
            else if (v is FsmString) ((FsmString)v).Value = s;
            else if (v is FsmVector3) { var p = s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries); ((FsmVector3)v).Value = new Vector3(F(p[0]), F(p[1]), F(p[2])); }
        }

        public static string Describe(object v)
        {
            if (v == null) return "null";
            var uo = v as UObj; if (uo != null) return uo == null ? "null(destroyed)" : uo.name + " (" + uo.GetType().Name + ")";
            if (v is UObj) return "null";
            var nv = v as NamedVariable; if (nv != null) { try { return "fsmvar " + nv.Name + "=" + nv.RawValue; } catch { return "fsmvar"; } }
            if (v is Vector3) return V((Vector3)v);
            var col = v as ICollection;
            if (col != null && !(v is string))
            {
                var sb = new StringBuilder(v.GetType().Name + "[" + col.Count + "]");
                int i = 0;
                foreach (var x in col) { if (i++ >= 6) { sb.Append(" ..."); break; } sb.Append(i == 1 ? " { " : ", ").Append(x == null ? "null" : (x is UObj ? ((UObj)x) != null ? ((UObj)x).name : "null" : x.ToString())); }
                if (i > 0 && i <= 6) sb.Append(" }");
                return sb.ToString();
            }
            string s; try { s = Convert.ToString(v, IC); } catch { s = "?"; }
            return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
        }

        static string V(Vector3 v) { return string.Format(IC, "({0:0.##}, {1:0.##}, {2:0.##})", v.x, v.y, v.z); }
        static float F(string s) { return float.Parse(s, NumberStyles.Float, IC); }
        static bool On(string s) { s = s.ToLowerInvariant(); return s == "on" || s == "1" || s == "true" || s == "yes"; }
        static string Rest(string line) { int i = line.IndexOf(' '); return i < 0 ? "" : line.Substring(i + 1).Trim(); }

        static List<string> Split(string line)
        {
            var r = new List<string>(); var sb = new StringBuilder(); bool q = false;
            foreach (char ch in line)
            {
                if (ch == '"') { q = !q; continue; }
                if (!q && (ch == ' ' || ch == '\t')) { if (sb.Length > 0) { r.Add(sb.ToString()); sb.Length = 0; } continue; }
                sb.Append(ch);
            }
            if (sb.Length > 0) r.Add(sb.ToString());
            return r;
        }
    }
}
