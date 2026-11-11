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
    //   _mod/cmd/in_*.txt     : command files (one command per line, optional "id|" prefix); processed in name order, then deleted
    //   _mod/cmd/out.txt      : results  (">>> id  command" ... "<<< id")
    //   _mod/cmd/out_<id>.txt : the result of one command that was sent with an id, a file of its own (status line first)
    //   _mod/cmd/state.txt    : live snapshot, rewritten every second while command files arrive
    //   _mod/cmd/shot_*.png   : screenshots
    // Nothing is written while nobody sends command files; the folder is looked at every 2 s then.
    public static class Remote
    {
        static string dir, statePath;
        static float nextPoll, nextState, nextClean, lastFileCommandAt = -1000f;
        static int autoId;
        static bool inited, stateAlways;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public static bool enabled = true;
        // seconds the file bridge counts as in use after its last command file
        const float FileUseWindow = 120f;
        // seconds a reply file out_<id>.txt is kept
        const float ReplyKeep = 600f;
        // longest id of "id|command"
        public const int MaxIdLength = 40;

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

        static void Init()
        {
            inited = true;
            statePath = Path.Combine(Dir, "state.txt");
            try { CoreBCommands.Init(); } catch (Exception e) { DevLog.Error("core commands init", e); }
            enabled = EditorState.Get("bridge.enabled", true);
            ReloadConfig();
            if (!enabled) DevLog.Write("remote: file bridge off (editor.cfg bridge.enabled=0); `remote on` starts it for this session");
        }

        public static void ReloadConfig()
        {
            string m = EditorState.Get("bridge.state", "auto");
            stateAlways = m == "always" || m == "1";
        }

        public static void Update(DevCore core)
        {
            if (!inited) Init();
            CoreBCommands.Tick();
            if (!enabled) return;
            try { MemLink.Update(); } catch { }
            float now = Time.realtimeSinceStartup;
            // In use = a command file arrived in the last two minutes: the folder is polled every 0.3 s and state.txt is
            // written every second. Otherwise one directory listing every second (every 2 s while nobody uses the tool at
            // all) and no state.txt, unless editor.cfg bridge.state=always asks for it every 5 s.
            if (now >= nextPoll)
            {
                nextPoll = now + (now - lastFileCommandAt < FileUseWindow ? 0.3f : Background.InUse ? 1f : 2f);
                try { Poll(core); } catch (Exception e) { DevLog.Error("remote poll", e); nextPoll = now + 5f; }
            }
            bool active = now - lastFileCommandAt < FileUseWindow;
            if ((active || stateAlways) && now >= nextState)
            {
                nextState = now + (active ? 1f : 5f);
                try { SafeFile.WriteAllText(statePath, State(core)); } catch { }
            }
        }

        static void Poll(DevCore core)
        {
            var files = Directory.GetFiles(Dir, "in_*.txt");
            if (files.Length == 0) return;
            Array.Sort(files, StringComparer.Ordinal);
            bool any = false;
            foreach (var f in files)
            {
                string[] lines;
                try
                {
                    // created but not written yet: left for the next poll, so the command is not lost
                    var fi = new FileInfo(f);
                    double age = (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalSeconds;
                    if (fi.Length == 0 && age >= 0 && age < 2.0) continue;
                    // left by a client that died: it must not run at the next game start
                    if (age > 30.0) { try { File.Delete(f); } catch { } DevLog.Write("remote: dropped a stale command file " + Path.GetFileName(f)); continue; }
                    lines = File.ReadAllLines(f); File.Delete(f);
                }
                catch { continue; }
                if (!any) { any = true; ObjectDatabase.Demand(60f); }
                lastFileCommandAt = Time.realtimeSinceStartup;
                foreach (var raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    string id, command;
                    bool ownId = SplitId(line, out id, out command);
                    if (!ownId) id = "r" + (++autoId);
                    Execute(id, ownId, command, core);
                }
            }
            if (any) nextState = 0f;   // the snapshot follows the commands at once
        }

        // "id|command": the part before the first '|' is an id when it has no blank and at most MaxIdLength characters,
        // so a command that uses '|' itself ("fsmset Door|Open|0|x|1") stays whole when it is sent without an id.
        public static bool SplitId(string line, out string id, out string command)
        {
            int bar = line.IndexOf('|');
            if (bar > 0 && bar <= MaxIdLength)
            {
                bool plain = true;
                for (int i = 0; i < bar; i++) if (char.IsWhiteSpace(line[i])) { plain = false; break; }
                if (plain) { id = line.Substring(0, bar); command = line.Substring(bar + 1).Trim(); return true; }
            }
            id = null; command = line;
            return false;
        }

        static readonly List<string> execLog = new List<string>();

        static void Execute(string id, bool ownId, string line, DevCore core)
        {
            Background.Touch();
            int frame = Time.frameCount;
            float t = Time.realtimeSinceStartup;
            int logAt = DevLog.Total;
            var body = new StringBuilder();
            bool failed = false;
            bool was = trusted;
            trusted = true;   // a command file comes from somebody who can write into the game folder
            try
            {
                string res = Run(line, core);
                if (res != null) { body.Append(res).Append('\n'); failed = IsError(res); }
            }
            catch (Exception e) { failed = true; body.Append("ERROR ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n').Append(e.StackTrace).Append('\n'); }
            finally { trusted = was; }
            // include anything the command logged (the console commands report through DevLog)
            execLog.Clear();
            DevLog.Since(logAt, execLog);
            if (body.Length == 0 && execLog.Count > 0) failed = IsError(execLog[0]);
            foreach (var l in execLog) body.Append("  log: ").Append(l).Append('\n');
            execLog.Clear();
            var sb = new StringBuilder();
            sb.Append(">>> ").Append(id).Append("  ").Append(line).Append("   [frame ").Append(frame).Append(", t=").Append(t.ToString("0.0", IC)).Append("]\n");
            sb.Append(body).Append("<<< ").Append(id).Append("\n\n");
            try
            {
                string outp = Path.Combine(Dir, "out.txt");
                if (File.Exists(outp) && new FileInfo(outp).Length > 2000000) File.Delete(outp);
                File.AppendAllText(outp, sb.ToString());
            }
            catch { }
            if (ownId) WriteReply(id, line, frame, t, failed, body);
            DevLog.Write("remote> " + line);
        }

        // out_<id>.txt: "key: value" lines (status first), an empty line, then the reply as it stands in out.txt. Written
        // to a temporary name and renamed, so a reader never sees half a file.
        static void WriteReply(string id, string line, int frame, float t, bool failed, StringBuilder body)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("status: ").Append(failed ? "error" : "ok").Append('\n');
                sb.Append("id: ").Append(id).Append('\n');
                sb.Append("command: ").Append(line).Append('\n');
                sb.Append("frame: ").Append(frame.ToString(IC)).Append('\n');
                sb.Append("time: ").Append(t.ToString("0.0", IC)).Append('\n');
                sb.Append('\n').Append(body);
                SafeFile.WriteAllText(Path.Combine(Dir, ReplyFileName(id)), sb.ToString());
                if (t >= nextClean)
                {
                    nextClean = t + 60f;
                    var limit = DateTime.UtcNow.AddSeconds(-ReplyKeep);
                    foreach (var f in Directory.GetFiles(Dir, "out_*.txt*"))
                    {
                        try { if (File.GetLastWriteTimeUtc(f) < limit) File.Delete(f); } catch { }
                    }
                }
            }
            catch (Exception e) { DevLog.Write("remote: reply file for '" + id + "' not written: " + e.Message); }
        }

        // name of the reply file of an id: characters that cannot stand in a file name become '_'
        public static string ReplyFileName(string id)
        {
            var sb = new StringBuilder("out_", id.Length + 8);
            foreach (char c in id) sb.Append((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.' ? c : '_');
            return sb.Append(".txt").ToString();
        }

        // True when the first line of a reply says that the command could not do its job: the first words every client
        // treats as an error (refused, failed, usage, unknown, not found, busy, error, cannot ...).
        public static bool IsError(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return false;
            int nl = reply.IndexOf('\n');
            string first = (nl < 0 ? reply : reply.Substring(0, nl)).Trim();
            if (first.Length == 0) return false;
            if (first.StartsWith("ERROR", StringComparison.Ordinal)) return true;
            string low = first.ToLowerInvariant();
            foreach (var w in errorStarts) if (low.StartsWith(w, StringComparison.Ordinal)) return true;
            foreach (var w in errorParts) if (low.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            return low.EndsWith(" failed", StringComparison.Ordinal) || low.EndsWith(" not found", StringComparison.Ordinal);
        }
        static readonly string[] errorStarts = { "not found", "usage:", "usage ", "unknown ", "unknown:", "busy:", "busy ", "refused", "failed:", "failed to ", "error:", "error in ", "error -", "cannot ", "can't ", "could not ", "couldn't ", "no fsm", "no playmakerfsm", "no active ", "no character", "no type ", "no method ", "no variable ", "no mod ", "no effect ", "no setting ", "no such ", "nothing selected", "not built", "object scan not finished" };
        static readonly string[] errorParts = { "component not found", "field/property not found", "no parameterless method", " failed:", "unknown command" };

        // a file-bridge command or the in-game console: allowed to change the protected settings (see cfg)
        static bool trusted;
        public static bool Trusted { get { return trusted; } }

        // one line for the doctor
        public static string BridgeStatus()
        {
            if (!enabled) return "file bridge off (remote on starts it)";
            float ago = Time.realtimeSinceStartup - lastFileCommandAt;
            return "file bridge on, folder " + Dir + ", " + (lastFileCommandAt < 0f ? "no command file yet" : "last command file " + Fmt.F(ago, "0") + " s ago") +
                   ", state.txt " + (stateAlways ? "always" : "only while command files arrive");
        }

        // set by InsideDev.Mcp: reason text when a line drives a secret / achievement object, else null (main thread)
        public static Func<string, string> ObjectRefusal;

        // RemotePolicy.Refusal plus the object rule, for the untrusted entries below; null = the line may run
        static string Refused(string line, bool allowQuit)
        {
            if (line == null || line.Trim().Length == 0) return null;
            string why = RemotePolicy.Refusal(line, allowQuit, EditorState.Get("play.save_guard", false));
            if (why != null) return why;
            var f = ObjectRefusal;
            if (f == null) return null;
            try { return f(line); }
            catch (Exception e) { return "the object check failed (" + e.GetType().Name + ")"; }
        }

        // entry for other channels (MCP, POST /cmd, Play, Det): same commands as the file bridge, minus RemotePolicy's list
        // (quit allowed here: the command tool and POST /cmd may close the game; scenario steps and det cues check first)
        public static string RunCommand(string line, DevCore core)
        {
            Background.Touch();
            string why = Refused(line, true);
            if (why != null) return "refused: " + why;
            bool was = trusted;
            trusted = false;
            try { return Run(line, core); }
            finally { trusted = was; }
        }

        // the same for a caller that sits at the game itself (the in-game console): may change protected settings
        public static string RunLocal(string line, DevCore core)
        {
            Background.Touch();
            bool was = trusted;
            trusted = true;
            try { return Run(line, core); }
            finally { trusted = was; }
        }

        // MultiCam runner: setup and cue commands pass the capture lock
        static int internalDepth;
        public static string RunInternal(string line, DevCore core)
        {
            string why = Refused(line, false);
            if (why != null) return "refused: " + why;
            internalDepth++;
            bool was = trusted;
            trusted = false;
            try { return Run(line, core); }
            finally { internalDepth--; trusted = was; }
        }

        // ---------------------------------------------------------------- commands
        // Every command has a row in CoreBHelp's table (name, usage, text, least number of arguments). A word without a
        // row goes to the extensions and then to the console's commands; a call with too few arguments, or with a word
        // where a number belongs, is answered with the usage line.
        static string Run(string line, DevCore core)
        {
            if (!inited) Init();
            var a = Split(line);
            if (a.Count == 0) return null;
            string c = a[0].ToLowerInvariant();
            if (internalDepth == 0 && c != "help" && MultiCam.Blocks(a)) return MultiCam.BusyText;
            var cmd = CoreBHelp.Find(c);
            if (cmd == null || cmd.console) return Fallback(a, line, core);
            if (a.Count - 1 < cmd.min) return "usage: " + CoreBHelp.Usage(cmd);
            if (cmd.forwarded) return Dispatch(c, a, line, core);
            try { return Dispatch(c, a, line, core); }
            catch (FormatException e) { return "usage: " + CoreBHelp.Usage(cmd) + "   (" + e.Message + ")"; }
            catch (ArgumentOutOfRangeException e) { return "usage: " + CoreBHelp.Usage(cmd) + "   (an argument is missing: " + e.GetType().Name + ")"; }
        }

        // extensions first, then the console's commands (which answer through the log); never deeper than once, so the
        // console handing an unknown word to the bridge cannot go round in a circle
        static int fallbackDepth;
        static string Fallback(List<string> a, string line, DevCore core)
        {
            var er = Extensions.Command(a, core);
            if (er != null) return er;
            if (fallbackDepth > 0) return "unknown command '" + a[0] + "' (help lists the commands)";
            fallbackDepth++;
            try { Commands.Run(line, core, false); }
            finally { fallbackDepth--; }
            return null;
        }

        static string Dispatch(string c, List<string> a, string line, DevCore core)
        {
            switch (c)
            {
                case "ping": return "pong  InsideDev " + Boot.Version;
                case "state": return CoreBCommands.IsJson(a) ? CoreBCommands.StateJson(core) : State(core);
                case "where":
                    if (CoreBCommands.IsJson(a)) return CoreBCommands.WhereJson();
                    return Fallback(a, line, core);
                case "quit":
                case "doctor":
                case "cfg":
                case "background":
                    return CoreBCommands.Run(c, a, core);
                case "screenshot":
                    {
                        string tag = a.Count > 1 ? a[1] : "";
                        foreach (char bad in Path.GetInvalidFileNameChars()) tag = tag.Replace(bad, '_');
                        if (tag.Length > 60) tag = tag.Substring(0, 60);
                        string name = "shot_" + DateTime.Now.ToString("HHmmss", IC) + (tag.Length > 0 ? "_" + tag : "") + ".png";
                        string p = Path.Combine(Dir, name);
                        Application.CaptureScreenshot(p);
                        return "screenshot -> " + p + " (written at end of frame)";
                    }
                case "diag": return a.Count > 1 && a[1] == "json" ? RenderHost.ReportJson() : RenderHost.Report();
                case "cams": return RenderDiag.CameraReport(a.Count > 1 && a[1] == "json");
                case "render":
                    {
                        string m = a.Count > 1 ? a[1].ToLowerInvariant() : "";
                        switch (m)
                        {
                            case "auto": RenderHost.Request(Backend.Auto); break;
                            case "eof": RenderHost.Request(Backend.EndOfFrame); break;
                            case "overlay": RenderHost.Request(Backend.OverlayCamera); break;
                            case "gamecam": RenderHost.Request(Backend.GameCameraPost); break;
                            case "probe": RenderHost.RequestProbe("remote", 1, true); break;
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
                        if (a.Count > 1 && a[1] == "status") return a.Count > 2 && a[2] == "json" ? FocusTest.StatusJson() : FocusTest.Status();
                        int n = a.Count > 1 ? (int)F(a[1]) : 20, away = a.Count > 2 ? (int)F(a[2]) : 2500, back = a.Count > 3 ? (int)F(a[3]) : 1500;
                        string mode = a.Count > 4 ? a[4] : "alttab";
                        if (a.Contains("force")) mode += "+force";
                        int bg = a.Count > 5 ? (On(a[5]) ? 1 : 0) : -1;
                        return FocusTest.Start(n, away, back, mode, bg);
                    }
                case "keyhold":
                    {
                        if (a.Count > 1 && a[1] == "status") return "keyhold: " + FocusTest.KeyHoldStatus();
                        byte vk = 0x27;   // Right arrow (INSIDE's default "right")
                        if (a.Count > 1)
                        {
                            if (a[1] == "right") vk = 0x27;
                            else if (a[1] == "left") vk = 0x25;
                            else if (a[1] == "up") vk = 0x26;
                            else { try { vk = Convert.ToByte(a[1], 16); } catch { return "usage: keyhold [right|left|up|<hex vk>] [ms] [text]"; } }
                        }
                        int ms = a.Count > 2 ? (int)F(a[2]) : 1500;
                        return FocusTest.KeyHold(vk, ms, a.Count > 3 ? a[3] : null);
                    }
                case "tp":
                    {
                        // tp x y [z]  |  tp <obj>   teleports the boy; two numbers keep his depth (the console's form)
                        float x, y, z;
                        if (a.Count >= 3 && Fmt.TryF(a[1], out x) && Fmt.TryF(a[2], out y))
                        {
                            var ch = G.MainCharacter;
                            if (a.Count < 4) z = ch != null ? ch.pos3.z : 0f;
                            else if (!Fmt.TryF(a[3], out z)) return "usage: tp <x> <y> [z] | tp <obj>";
                            var p = new Vector3(x, y, z);
                            return G.Teleport(p) ? "boy -> " + V(p) : "no active character";
                        }
                        string q = a.Count == 2 ? a[1] : Rest(line);
                        var go = Resolve(q); if (go == null) return "not found: " + q + "   (usage: tp <x> <y> [z] | tp <obj>)";
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
                            case "stats": ObjectDatabase.EnsureFresh(3f); return a.Count > 2 && a[2] == "json" ? ObjectDatabase.StatsJson() : ObjectDatabase.Stats();
                            case "budget": if (a.Count > 2) ObjectDatabase.budgetMs = Mathf.Clamp(F(a[2]), 0.1f, 50f); return "budget " + Fmt.F(ObjectDatabase.budgetMs) + " ms/frame";
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
                case "memwhat": { long v; if (!MemLink.ParseHex(Rest(line), out v)) return "usage: memwhat <hex>"; GameObject o; return MemAddr.WhatIs(v, out o); }
                case "memmethod": { long v; if (!MemLink.ParseHex(Rest(line), out v)) return "usage: memmethod <hex>"; return MemLink.Method(v, true); }
                case "memjit":
                    {
                        var a2 = Rest(line).Split(' '); if (a2.Length < 2) return "usage: memjit <Type> <method> [+hex]";
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
                        long v; var parts = Rest(line).Split(' '); if (!MemLink.ParseHex(parts[0], out v)) return "usage: memread <hex> [bytes]";
                        int n = 16;
                        if (parts.Length > 1 && !Fmt.TryI(parts[1], out n)) return "usage: memread <hex> [bytes]";
                        // whole 4-byte steps (each line reads four bytes) and a bounded reply
                        n = Math.Max(4, Math.Min(4096, (n + 3) & ~3));
                        var sb = new StringBuilder(); if (!MemAddr.Readable((IntPtr)v, n)) return "not readable memory";
                        for (int i = 0; i < n; i += 4) sb.Append((v + i).ToString("X")).Append(": ").Append(System.Runtime.InteropServices.Marshal.ReadInt32((IntPtr)(v + i)).ToString("X8")).Append("  f=").Append(MemAddr.ReadFloat((IntPtr)(v + i), 0).ToString("R", IC)).Append('\n');
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
                    return GameCode.Report();
                case "postfx":
                    {
                        // postfx | postfx on|off|default <Type> | postfx set <Type> <field> <value>
                        if (a.Count < 3) return a.Count == 2 && a[1].ToLowerInvariant() == "json" ? PostFx.ReportJson() : PostFx.Report();
                        var cam = G.Cam(); if (cam == null) return "no gameplay camera";
                        Behaviour b = null; foreach (var x in PostFx.Effects(cam)) if (x.GetType().Name.Equals(a[2], StringComparison.OrdinalIgnoreCase)) b = x;
                        if (b == null) return "no effect " + a[2] + " on " + cam.name;
                        if (a[1] == "on" || a[1] == "off") { PostFx.SetEnabled(b, a[1] == "on"); return b.GetType().Name + " " + a[1]; }
                        if (a[1] == "default") { PostFx.DefaultAll(b); return b.GetType().Name + " back to the game's values"; }
                        if (a[1] == "set" && a.Count > 4) { var st = PostFx.Find(b, a[3]); if (st == null) return "no setting " + a[3]; return PostFx.SetValue(b, st, a[4]); }
                        return "usage: postfx | postfx on|off|default <Type> | postfx set <Type> <field> <value>";
                    }
                case "fsmset":
                    {
                        // fsmset <fsm object>|<state>|<action index>|<field>|<value>   (field "(enabled)" = action on/off)
                        var p = Rest(line).Split(new[] { '|' }, 5);
                        if (p.Length < 5) return "usage: fsmset <fsm object>|<state>|<action index>|<field>|<value>";
                        var go = Resolve(p[0].Trim()); if (go == null) return "not found";
                        var f = go.GetComponent<PlayMakerFSM>(); if (f == null) return "no FSM on " + go.name;
                        return ActionEdit.Apply(f, p[1] + "|" + p[2] + "|" + p[3] + "|" + p[4], "bridge");
                    }
                case "sigwire":
                case "sigunwire":
                    {
                        // sigwire <fsm object>|<signal in>|<sender object>|<signal out>
                        var p = Rest(line).Split('|');
                        if (p.Length < 4) return "usage: " + a[0] + " <fsm object>|<signal in>|<sender object>|<signal out>";
                        var go = Resolve(p[0].Trim()); if (go == null) return "fsm object not found";
                        var f = go.GetComponent<PlayMakerFSM>(); if (f == null) return "no FSM on " + go.name;
                        var snd = Resolve(p[2].Trim()); if (snd == null) return "sender not found";
                        if (c == "sigwire") return SignalEdit.Connect(f, snd, p[3].Trim(), p[1].Trim(), "bridge");
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
                            if (ReferenceIndex.PathsTruncated) sb.Append("(stopped at the work limit: more paths may exist)\n");
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
                case "mcam": return MultiCam.Command(a, core);
                case "srec": return SceneCommands.Command(a, core);
                case "acap": return AudioCapture.Command(a, core);
                case "hud": return MultiCam.Hud(a, core);
                case "clean": return MultiCam.Clean(a, core);
                case "gamma": return MultiCam.Gamma(a);
                case "seed": return MultiCam.Seed(a);
                case "cam":
                    {
                        // cam | cam status [json] | cam look <target> ... | cam back | cam pose x y z yaw pitch [fov] [seconds] [roll] | cam fov <deg> | cam roll <deg> | cam json [name] | cam off
                        string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "";
                        string own = CameraControl.Command(a);
                        if (own != null) return own;
                        if (sub == "off") { CameraControl.SetFree(false); return CameraControl.Describe(); }
                        if (sub == "pose")
                        {
                            if (a.Count < 7) return "usage: cam pose x y z yaw pitch [fov] [seconds] [roll]";
                            return CameraControl.Pose(new Vector3(F(a[2]), F(a[3]), F(a[4])), F(a[5]), F(a[6]), a.Count > 7 ? F(a[7]) : -1f, a.Count > 8 ? F(a[8]) : 0f, a.Count > 9 ? F(a[9]) : 0f);
                        }
                        if (sub == "fov") return a.Count > 2 ? CameraControl.SetFov(F(a[2])) : "usage: cam fov <degrees>";
                        if (sub == "roll") return a.Count > 2 ? CameraControl.SetRoll(F(a[2])) : "usage: cam roll <degrees>";
                        if (sub == "json") return CameraControl.Json(a.Count > 2 ? a[2] : null);
                        return CameraControl.Describe();
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
                            case "scale": if (a.Count > 2) { core.uiScale = Mathf.Clamp(F(a[2]), 0.25f, 8f); EditorState.Set("ui.scale", core.uiScale); } break;
                            case "layout":
                                if (a.Count > 3 && a[2] == "save") return core.workspace.SaveLayout(a[3]);
                                if (a.Count > 3 && a[2] == "load") { core.SetPanel(true, null); return core.workspace.LoadLayout(a[3]); }
                                if (a.Count > 3 && a[2] == "delete") return core.workspace.DeleteLayout(a[3]);
                                if (a.Count > 2 && a[2] == "list") return string.Join(" ", core.workspace.Layouts().ToArray());
                                if (a.Count > 2) core.workspace.SetDocked(a[2] == "dock");
                                break;
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
                            case "docks": return a.Count > 2 && a[2] == "json" ? core.workspace.DescribeJson() : core.workspace.Describe();
                            case "focus": ui.focus = a.Count > 2 && a[2] != "none" ? a[2] : null; break;
                            case "scroll":
                                if (a.Count < 4) return "usage: ui scroll <id> <y>";
                                ui.ScrollTo(a[2], F(a[3])); return "scroll " + a[2] + " = " + a[3];
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
                                    if (m == "find") return "'" + txt + "' #" + nth + " at " + wc.x.ToString("0", IC) + "," + wc.y.ToString("0", IC) + "  rect " + Fmt.S("(x:{0:F2}, y:{1:F2}, width:{2:F2}, height:{3:F2})", r.Value.x, r.Value.y, r.Value.width, r.Value.height);
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
                                    if (a.Count < 4) return "usage: ui click|rclick|hover <x> <y>";
                                    float sc = core.uiScale;
                                    EditorInput.Inject(new Vector2(F(a[2]) * sc, F(a[3]) * sc), a[1] == "click" ? 0 : a[1] == "rclick" ? 1 : -1);
                                    return a[1] + " injected at " + a[2] + "," + a[3];
                                }
                            case "renderer": if (a.Count > 2) { RenderHost.SetRenderer(a[2]); EditorState.Set("render.renderer", a[2]); } break;
                        }
                        string gi; try { gi = GameInput.IsEnabled() ? "enabled" : "DISABLED"; } catch { gi = "?"; }
                        return "ui scale " + Fmt.F(core.uiScale) + "  layout " + (core.workspace.docked ? "dock" : "window") + "  panel " + core.Panel +
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
                        int matches;
                        var go = Resolve(Rest(line), out matches); if (go == null) return "not found";
                        Selection.Set(go, "bridge");
                        return "selected " + Selection.Describe() + (matches > 1 ? "   (the nearest of " + matches + " matches)" : "");
                    }
                case "events":
                    {
                        return EventMonitor.Command(a);
                    }
                case "pick":
                    {
                        if (a.Count > 1 && (a[1] == "on" || a[1] == "off")) { WorldPick.mode = a[1] == "on"; return "pick mode " + WorldPick.mode; }
                        if (a.Count > 2 && a[1] == "filter") { int fi = Array.IndexOf(WorldPick.Filters, a[2]); if (fi < 0) return "filters: " + string.Join(" ", WorldPick.Filters); WorldPick.filter = fi; return "pick filter " + a[2]; }
                        if (a.Count > 1 && a[1] == "status") return "pick mode " + WorldPick.mode + ", filter " + WorldPick.Filters[WorldPick.filter] + ", queries " + WorldPick.queries + "\n" + WorldPick.lastResult;
                        if (a.Count > 3 && a[3] == "look") return WorldPick.PickAt(new Vector2(F(a[1]), F(a[2])), false);
                        if (a.Count > 2) return WorldPick.PickAt(new Vector2(F(a[1]), F(a[2])));
                        var ch = G.MainCharacter; var cm = G.Cam();
                        if (ch == null || cm == null) return "no character/camera";
                        var sp = RenderScale.W2S(cm, ch.pos3 + Vector3.up * 0.5f);
                        return "boy is at screen " + sp.x.ToString("0", IC) + "," + (Screen.height - sp.y).ToString("0", IC) + "\n" + WorldPick.PickAt(new Vector2(sp.x, Screen.height - sp.y));
                    }
                case "gizmo":
                    {
                        // gizmo [status] | gizmo on|off (current selection) | gizmo global on|off | gizmo move x y z (drag test: moves the selection like a drag)
                        var gs = Selection.Current;
                        if (a.Count > 2 && a[1] == "global") { TransformGizmo.SetGlobal(a[2] == "on"); }
                        else if (a.Count > 1 && (a[1] == "on" || a[1] == "off")) { if (gs == null) return "nothing selected"; TransformGizmo.SetFor(gs, a[1] == "on"); }
                        return "gizmo global " + TransformGizmo.globalOn + ", selection " + (gs == null ? "none" : gs.name + " per-object " + TransformGizmo.IsOnFor(gs) + ", visible " + TransformGizmo.Visible + ", pos " + Fmt.V3(gs.transform.position, "F2"));
                    }
                case "labels":
                    {
                        // labels [off|selected|nearby|all]   plus the console's on|off: on = the level in use, or ALL
                        var ov = core.Overlay;
                        if (a.Count > 2 && a[1].ToLowerInvariant() == "types")
                        {
                            // labels types <n>: component names per label (0 only the count, all = every one)
                            int nt;
                            if (a[2].ToLowerInvariant() == "all") nt = -1;
                            else if (!Fmt.TryI(a[2], out nt) || nt < -1 || nt > 50) return "usage: labels types <0..50|all>";
                            ov.labelTypes = nt;
                            EditorState.Set("overlay.label.types", nt);
                            return "labels " + Overlay.LabelLevels[ov.labelLevel] + ", " + (nt < 0 ? "all components" : Fmt.I(nt) + " component name(s) per label");
                        }
                        if (a.Count > 1)
                        {
                            string v = a[1].ToUpperInvariant();
                            int lv = Array.IndexOf(Overlay.LabelLevels, v);
                            bool onWord = lv < 0 && (v == "ON" || v == "1" || v == "TRUE" || v == "YES");
                            if (lv < 0 && (v == "0" || v == "FALSE" || v == "NO")) lv = 0;
                            if (lv < 0 && !onWord) return "usage: labels [off|selected|nearby|all|on]";
                            if (onWord) ov.SetLabels(true); else ov.SetLabelLevel(lv);
                            ov.SaveChoices();
                        }
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
                        // perf [json] | perf reset [json] | perf enable <subsystem> | perf hide ... | perf show
                        return Perf.Command(a);
                    }
                case "history":
                case "undo":
                case "redo":
                case "revert":
                    // history [n] [json] | undo [n | to <id>] | redo [n] | revert all|<n>|<label text> [dry]  (n = index in the NET CHANGES list of "history")
                    return ChangeRecorder.Command(a);
                case "mod":
                    {
                        // mod list | show <name> | record <name> [description...] | dry <name> | validate <name> | enable|disable <name> | reload | delete <name>   [json]
                        return Mods.Command(a);
                    }
                case "bookmark":
                    {
                        // bookmark add [note...] | list | go <n> | del <n>
                        string sub = a.Count > 1 ? a[1] : "list";
                        if (sub == "add") { var mk = Bookmarks.Add(Selection.Current, null, a.Count > 2 ? string.Join(" ", a.GetRange(2, a.Count - 2).ToArray()) : ""); return mk == null ? "nothing selected" : "bookmarked " + mk.name + "  " + mk.selector; }
                        if (sub == "list") { var sb = new System.Text.StringBuilder(); for (int i = 0; i < Bookmarks.marks.Count; i++) sb.Append(i).Append("  ").Append(Bookmarks.marks[i].name).Append("  ").Append(Bookmarks.marks[i].note).Append("  ").Append(Bookmarks.marks[i].selector).Append('\n'); return sb.Length > 0 ? sb.ToString() : "no bookmarks"; }
                        int bi; if (a.Count < 3 || !Fmt.TryI(a[2], out bi) || bi < 0 || bi >= Bookmarks.marks.Count) return "usage: bookmark go|del <n>   (n = 0.." + (Bookmarks.marks.Count - 1) + ")";
                        if (sub == "go") return Bookmarks.Go(Bookmarks.marks[bi], a.Count > 3 && a[3] == "tp");
                        if (sub == "del") { var mk = Bookmarks.marks[bi]; Bookmarks.Remove(mk); return "removed " + mk.name; }
                        return "usage: bookmark add [note] | bookmark list | bookmark go <n> [tp] | bookmark del <n>";
                    }
                case "palette":
                    {
                        // palette <query...> [#n]   list hits, or open hit n
                        int open = -1; var words = new List<string>(a.GetRange(1, a.Count - 1));
                        if (words.Count > 0 && words[words.Count - 1].StartsWith("#")) { if (!Fmt.TryI(words[words.Count - 1].Substring(1), out open)) return "usage: palette <query> [#n]"; words.RemoveAt(words.Count - 1); }
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
                        if (a.Count < 2) return "usage: logic <Type|object> | logic shared [f] | logic anim [f]";
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
                        return "usage: audio rules | mute <ev> | unmute <ev> | nuke <ev> | unnuke <ev> | replace <ev> <with> | force switch|state|rtpc <name> <value> | clear <key|all>";
                    }
                case "hidden": return PassiveHidden.Command(a);
                case "ext": return Extensions.CommandExt(a, core);
                case "live": return Extensions.Command(a, core) ?? "the live link is not installed (_mod\\InsideDev.LiveLink.dll)";
                case "adb":
                    {
                        // adb [stats] | find <text> [filter] | show <name|id> | tree <name|id> | filter <FILTER> [n]
                        string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "stats";
                        var sb = new System.Text.StringBuilder();
                        if (sub == "stats") return a.Count > 2 && a[2] == "json" ? AudioDb.StatsJson() : AudioDb.Stats();
                        if (sub == "export") return AudioExport.Run();
                        if (sub == "exportstatus") return AudioExport.status;
                        if (sub == "find" || sub == "filter")
                        {
                            var f = AudioDb.Filter.All; string q = "";
                            if (sub == "filter") { int fi = Array.IndexOf(AudioDb.FilterNames, a.Count > 2 ? a[2].ToUpperInvariant().Replace("_", " ") : ""); if (fi < 0) return "filters: " + string.Join(", ", AudioDb.FilterNames); f = (AudioDb.Filter)fi; }
                            else q = a.Count > 2 ? a[2] : "";
                            int max = 40; if (sub == "filter" && a.Count > 3 && !Fmt.TryI(a[3], out max)) max = 40;
                            var l = AudioDb.Query(f, q, 100000);
                            sb.Append(l.Count).Append(" record(s)\n");
                            for (int i = 0; i < l.Count && i < max; i++) sb.Append(l[i].kind).Append("  ").Append(l[i].Name).Append("  [").Append(l[i].bank).Append("]  ").Append(AudioDb.Status(l[i])).Append(l[i].refs.Count > 0 ? "  refs " + l[i].refs.Count : "").Append('\n');
                            return sb.ToString();
                        }
                        if (a.Count < 3) return "usage: adb stats|find <text>|filter <F> [n]|show <name|id>|select <name|id>";
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
                        if (sub == "stacks")
                        {
                            if (a.Count > 2) { string m = a[2].ToLowerInvariant(); AudioSpy.stackMode = m == "off" ? 0 : m == "full" ? 2 : 1; }
                            Background.Want(Background.Audio, 600f);
                            return "call stacks: " + AudioSpy.StackModes[AudioSpy.stackMode] + (AudioSpy.stackMode > 0 ? "   (taken while the tool is in use; this call keeps the audio log awake for 10 minutes)" : "");
                        }
                        if (sub == "version") return WwiseApiPanel.VersionLine();
                        if (sub == "stock") return WwiseApiPanel.StockReport();
                        switch (sub)
                        {
                            case "stats": return a.Count > 2 && a[2] == "json" ? AudioTrace.StatusJson() : WwiseApiPanel.Diagnostics() + AudioTrace.Status();
                            case "api": return WwiseApiPanel.Dump(a.Count > 2 ? a[2] : null, false);
                            case "watched": return WwiseApiPanel.Dump(a.Count > 2 ? a[2] : null, true);
                            case "level":
                                {
                                    int lv;
                                    if (a.Count > 2) { if (!Fmt.TryI(a[2], out lv) || lv < 0 || lv > 3) return "usage: wwise level <0-3>"; WwiseNative.SetLevel(lv); }
                                    return "level " + WwiseNative.Level;
                                }
                            case "hash": { string nm = string.Join(" ", a.GetRange(2, a.Count - 2).ToArray()); return nm + " = " + AudioTrace.Hash(nm) + "  (known as: " + AudioTrace.Name(AudioTrace.Hash(nm)) + ")"; }
                            case "pid":
                                {
                                    uint pid;
                                    if (a.Count < 3 || !uint.TryParse(a[2], NumberStyles.Integer, IC, out pid)) return "usage: wwise pid <playing id>";
                                    return AudioTrace.DescribePlay(pid);
                                }
                            case "trace":
                                {
                                    int n = 60; string f = null;
                                    if (a.Count > 2 && !Fmt.TryI(a[2], out n)) { n = 60; f = a[2]; }
                                    if (a.Count > 3) f = a[3];
                                    int shown = 0;
                                    for (int i = 0; i < AudioTrace.Count && shown < n; i++)
                                    {
                                        var e = AudioTrace.Get(i); if (e == null) break;
                                        string tx = AudioTrace.Kind(e.r.type) + "  " + AudioTrace.Text(e, false);
                                        if (f != null && tx.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                        sb.Append(e.t.ToString("0.000", IC)).Append("s f").Append(e.frame).Append("  ").Append(tx).Append('\n'); shown++;
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
                        return "usage: wwise stats|api [f]|watched|trace [n] [f]|level n|objs [f]|banks|pid <id>|hash <name>|ids";
                    }
                case "sounds":
                    {
                        // sounds [filter]  |  sounds audition <event>  |  sounds show <event>
                        if (a.Count > 2 && a[1] == "audition") return SoundLibrary.Audition(a[2]);
                        if (a.Count > 3 && (a[1] == "override" || a[1] == "revert" || a[1] == "post" || a[1] == "trigger"))
                        {
                            // sounds override <event> <binding#> <newEvent> | revert/post/trigger <event> <binding#>
                            SoundLibrary.Def d0; if (!SoundLibrary.defs.TryGetValue(a[2], out d0)) return "unknown sound";
                            int bi; if (!Fmt.TryI(a[3], out bi) || bi < 0 || bi >= d0.bindings.Count) return "usage: binding index 0.." + (d0.bindings.Count - 1);
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
                    if (a.Count > 1 && a[1] == "look") return GraphView.Snapshot(Selection.Current, a.Count > 2 ? a[2] : null, a.Count > 3 ? (int)F(a[3]) : 0, a.Count > 4 ? a[4] : null);
                    if (a.Count > 2) GraphView.SetFilter(a[2]);   // graph <hops> [f|s|m|o|c combination | all]   (c = code edges)
                    return GraphView.Dump(a.Count > 1 ? (int)F(a[1]) : 0);
                case "sel":
                    {
                        // sel | sel back|fwd|clear | sel <obj>   (the last form selects, as the console's sel does)
                        if (a.Count > 1)
                        {
                            if (a[1] == "back") Selection.Back();
                            else if (a[1] == "fwd") Selection.Forward();
                            else if (a[1] == "clear") Selection.Clear("bridge");
                            else
                            {
                                string q = Rest(line);
                                var go = Resolve(q);
                                if (go == null)
                                {
                                    // the console's matching: every word in the name or the path, active objects only
                                    foreach (var r in ObjectDatabase.Search(q, 200)) if (r.go != null && r.activeInHierarchy) { go = r.go; break; }
                                }
                                if (go == null) return "not found: " + q;
                                Selection.Set(go, "bridge");
                            }
                        }
                        return Selection.Describe() + "   history " + Selection.HistoryCount + "   explorer: " + SceneExplorer.Status();
                    }
                case "children":
                    {
                        var go = Resolve(Rest(line)); if (go == null) return "not found";
                        var sb = new StringBuilder();
                        for (int i = 0; i < go.transform.childCount; i++) { var ch = go.transform.GetChild(i); sb.Append(ch.gameObject.activeSelf ? "  " : "  [off] ").Append(ch.name).Append(ch.childCount > 0 ? "  (" + ch.childCount + ")" : "").Append('\n'); }
                        return sb.ToString();
                    }
                case "near":
                    {
                        float r = 15f;
                        if (a.Count > 1 && !Fmt.TryF(a[1], out r)) return "usage: near [radius] [filter]";
                        return Near(r, a.Count > 2 ? a[2] : null);
                    }
                case "triggers":
                    {
                        // triggers [radius]   lists them;   triggers on|off   switches the trigger overlay (the console's form)
                        float r = 20f;
                        if (a.Count > 1)
                        {
                            string v = a[1].ToLowerInvariant();
                            if (v == "on" || v == "off" || v == "true" || v == "false" || v == "yes" || v == "no")
                            {
                                core.Overlay.showTriggers = On(v);
                                core.Overlay.SaveChoices();
                                core.Overlay.ForceRefresh();
                                return "trigger overlay " + (core.Overlay.showTriggers ? "on" : "off");
                            }
                            if (!Fmt.TryF(a[1], out r)) return "usage: triggers [radius] | triggers on|off";
                        }
                        return Triggers(r);
                    }
                case "hiddennear":
                    {
                        core.ScanHidden();
                        var ch = G.MainCharacter; var o = ch != null ? ch.pos3 : Vector3.zero; float r = a.Count > 1 ? F(a[1]) : 30f;
                        var sb = new StringBuilder();
                        foreach (var h in core.hidden) if (h.go != null && (h.go.transform.position - o).magnitude <= r) sb.Append(((h.go.transform.position - o).magnitude).ToString("0.0", IC)).Append("m  ").Append(Inspector.PathOf(h.go.transform)).Append("  [").Append(h.kind).Append("]\n");
                        return sb.Length > 0 ? sb.ToString() : "none within " + Fmt.F(r) + "m";
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
                        if (pi != null && pi.CanWrite)
                        {
                            object before = null;
                            try { if (pi.CanRead) before = pi.GetValue(comp, null); } catch { }
                            if (pi.CanRead)
                            {
                                // undoable: the history reads and writes the property itself
                                var pc = comp; var pp = pi;
                                ChangeRecorder.BeforeCustom(go, "prop:" + comp.GetInstanceID() + ":" + pi.Name, go.name + " " + comp.GetType().Name + "." + pi.Name,
                                                            () => pp.GetValue(pc, null), x => pp.SetValue(pc, x, null), "bridge");
                                pi.SetValue(comp, Parse(val, pi.PropertyType, before), null);
                                return a[3] + " = " + Describe(pi.GetValue(comp, null)) + "   (a property; it was " + Describe(before) + ")";
                            }
                            // a property that cannot be read has nothing to restore: the edit is listed in the history but cannot be undone
                            pi.SetValue(comp, Parse(val, pi.PropertyType, before), null);
                            string now = val;
                            ChangeRecorder.Action(go, "set property", null, comp.GetType().Name + "." + pi.Name + " = " + now + " (was " + Describe(before) + ")", "bridge");
                            return a[3] + " = " + now + "   (a property: listed in the history, not undoable; it was " + Describe(before) + ")";
                        }
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
                        if (a.Count < 3) return "usage: reparent <obj> <newParent|none|up|restore>";
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
                        if (a.Count <= i) return "usage: fsm <obj> [fsmName] event|state|var <name> [value] | wire <state|*> <event> <toState> | unwire <state|*> <event> | resetwires | events";
                        string op = a[i].ToLowerInvariant(); string arg = a.Count > i + 1 ? a[i + 1] : "";
                        if (op == "event") { ChangeRecorder.Action(go, "fsmEvent", fsm.FsmName, arg, "bridge"); fsm.SendEvent(arg); return fsm.FsmName + " <- event '" + arg + "'  now in state [" + fsm.ActiveStateName + "]"; }
                        if (op == "state") { ChangeRecorder.Action(go, "fsmState", fsm.FsmName, arg, "bridge"); fsm.SetState(arg); return fsm.FsmName + " -> state [" + fsm.ActiveStateName + "]"; }
                        if (op == "var")
                        {
                            var v = fsm.FsmVariables.GetVariable(arg);
                            if (v == null) return "no variable " + arg;
                            if (a.Count > i + 2)
                            {
                                string was = Fmt.Any(v.RawValue);
                                string val = string.Join(" ", a.GetRange(i + 2, a.Count - i - 2).ToArray());
                                if (v is FsmBool || v is FsmFloat || v is FsmInt || v is FsmString || v is FsmVector3)
                                {
                                    // undoable: the history reads and writes the variable itself
                                    var vv = v;
                                    ChangeRecorder.BeforeCustom(go, "fsmvar:" + fsm.FsmName + ":" + v.Name, "FSM var " + v.Name, () => vv.RawValue, x => WriteFsmVar(vv, x), "bridge");
                                    SetFsmVar(v, val);
                                    return arg + " = " + Fmt.Any(v.RawValue) + "   (it was " + was + ")";
                                }
                                // FSM variables of other types cannot be written back: the edit is listed in the history, not undoable
                                SetFsmVar(v, val);
                                ChangeRecorder.Action(go, "set fsm variable", fsm.FsmName, fsm.FsmName + "." + arg + " = " + Fmt.Any(v.RawValue) + " (was " + was + ")", "bridge");
                                return arg + " = " + Fmt.Any(v.RawValue) + "   (listed in the history, not undoable; it was " + was + ")";
                            }
                            return arg + " = " + Fmt.Any(v.RawValue);
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
                        int n = 40;
                        if (a.Count > 1 && !Fmt.TryI(a[1], out n)) return "usage: audiolog [n]";
                        // the log sleeps while nobody uses the tool: this call keeps it awake for two minutes, and the
                        // "wake" line in it marks the stretch that was not recorded
                        Background.Want(Background.Audio, 120f);
                        AudioSpy.Wake();
                        var sb = new StringBuilder();
                        if (AudioSpy.Instance == null) sb.Append("(the audio spy is not installed: ").Append(EditorState.Get("audio.spy", true) ? "the sound engine is not ready yet" : "editor.cfg audio.spy=0").Append(")\n");
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
                        Background.Want(Background.Timed, 120f);
                        AudioTimed.Scan(true);
                        if (CoreBCommands.IsJson(a)) return CoreBCommands.TimedJson();
                        var sb = new StringBuilder();
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
                        Background.Want(Background.Timed, 120f);
                        AudioTimed.Scan(true);
                        foreach (var s in AudioTimed.systems)
                            if (s.comp != null && s.typeName.Equals(a[1], StringComparison.OrdinalIgnoreCase))
                                foreach (var t in s.timers)
                                    if (t.field.Equals(a[2], StringComparison.OrdinalIgnoreCase))
                                    {
                                        string op = a[3].ToLowerInvariant();
                                        if (op == "mute") AudioTimed.SetMuted(t, true, "bridge");
                                        else if (op == "unmute") AudioTimed.SetMuted(t, false, "bridge");
                                        else if (op == "fire") AudioTimed.FireNow(t);
                                        else
                                        {
                                            float sec;
                                            if (!Fmt.TryF(op, out sec)) return "usage: timer <Type> <field> <seconds|mute|unmute|fire>";
                                            AudioTimed.SetTime(t, sec, "bridge");
                                        }
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
                    if (a.Count > 1) enabled = On(a[1]);
                    return "remote " + (enabled ? "on" : "off");
                case "help": return CoreBHelp.Help(a);
                default:
                    return Fallback(a, line, core);
            }
        }

        // ---------------------------------------------------------------- snapshot
        public static string State(DevCore core)
        {
            var sb = new StringBuilder();
            sb.Append("InsideDev ").Append(Boot.Version).Append("   ").Append(DateTime.Now.ToString("HH:mm:ss", IC)).Append("   frame ").Append(Time.frameCount).Append('\n');
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
                sb.Append("shockwave: in cover ").Append(ForcePushManager.GetInCover()).Append("  zones ").Append(ForcePushManager.getActiveZoneCount()).Append("  nokill ").Append(Shockwave.noKill).Append("  blow@").Append(Shockwave.CheckerTime("blow").ToString("0.00", IC)).Append("s cycle ").Append(Fmt.F(m.musicCycleTime)).Append('\n');
            }
            sb.Append("\nnearest triggers:\n").Append(Triggers(15f, 25));
            sb.Append("\nFSMs waiting on audio right now:\n");
            foreach (var w in AudioLinks.waits)
                if (w.fsm != null && w.fsm.ActiveStateName == w.state && w.fsm.gameObject.activeInHierarchy)
                    sb.Append("  ").Append(w.kind).Append(" -> ").Append(Inspector.PathOf(w.fsm.transform)).Append(" [").Append(w.state).Append("] fires '").Append(w.finishEvent).Append("'\n");
            sb.Append("\nlast audio events:\n");
            for (int i = Math.Max(0, AudioSpy.log.Count - 15); i < AudioSpy.log.Count; i++) { var e = AudioSpy.log[i]; sb.Append("  ").Append(e.frame).Append(' ').Append(e.kind).Append(' ').Append(e.name).Append(" @").Append(e.source).Append('\n'); }
            sb.Append("\nchanges by user: ").Append(Changes.list.Count).Append('\n');
            sb.Append("\nconsole tail:\n");
            foreach (var l in DevLog.Tail(12)) sb.Append("  ").Append(l).Append('\n');
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
            return sb.Length > 0 ? sb.ToString() : "  none within " + Fmt.F(radius) + "m (trigger scan on? F2)\n";
        }

        // Ranked from the records of the object database (positions of its last pass, at most 3 s old); the engine is asked
        // only for the rows that are printed.
        static string Near(float radius, string filter)
        {
            var ch = G.MainCharacter; var o = ch != null ? ch.pos3 : Vector3.zero;
            ObjectDatabase.Demand(30f);
            Background.Want(Background.Objects, 30f);
            try { ObjectDatabase.EnsureFresh(3f); } catch (Exception e) { DevLog.Error("near: object database", e); }
            if (!ObjectDatabase.Ready) return "object scan not finished, try again in a moment";
            string fl = filter != null ? filter.ToLowerInvariant() : null;
            float r2 = radius * radius;
            var list = new List<KeyValuePair<float, ObjRecord>>();
            foreach (var r in ObjectDatabase.all)
            {
                float d2 = (r.pos - o).sqrMagnitude;
                if (d2 > r2) continue;
                if (fl != null && (r.nameLower == null || r.nameLower.IndexOf(fl, StringComparison.Ordinal) < 0)) continue;
                if (r.go == null) continue;
                list.Add(new KeyValuePair<float, ObjRecord>(Mathf.Sqrt(d2), r));
            }
            list.Sort((x, y) => x.Key.CompareTo(y.Key));
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(150, list.Count); i++)
            {
                var t = list[i].Value.t;
                if (t == null) continue;
                sb.Append(list[i].Key.ToString("0.0", IC)).Append("m  ").Append(t.gameObject.activeInHierarchy ? "" : "[hidden] ").Append(Inspector.PathOf(t)).Append("  #").Append(t.gameObject.GetInstanceID());
                var comps = t.GetComponents<MonoBehaviour>();
                if (comps.Length > 0) { sb.Append("  <"); foreach (var c in comps) if (c != null) sb.Append(c.GetType().Name).Append(' '); sb.Append('>'); }
                sb.Append('\n');
            }
            return list.Count + " object(s) within " + Fmt.F(radius) + "m" + (list.Count > 150 ? " (showing 150)" : "") + "\n" + sb;
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
                if (vars != null && vars.Length > 0) { sb.Append("  variables: "); foreach (var v in vars) sb.Append(v.Name).Append('=').Append(Fmt.Any(v.RawValue)).Append("  "); sb.Append('\n'); }
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
                return raw == null ? null : Fmt.Any(raw);
            }
            var fe = v as FsmEvent; if (fe != null) return "event:" + fe.Name;
            var fo = v as FsmOwnerDefault; if (fo != null) { var g = fo.GameObject != null ? fo.GameObject.Value : null; return g != null ? g.name : "owner"; }
            if (v is string || v.GetType().IsPrimitive || v.GetType().IsEnum) return Fmt.Any(v);
            var uo = v as UObj; if (uo != null) return uo.name;
            if (ValueDump.IsNested(v)) return ValueDump.Compact(v, 4);
            return null;
        }

        // ---------------------------------------------------------------- helpers
        // seconds after which the records of the object database are refreshed before a lookup instead of trusted
        const float StaleRecords = 10f;

        public static GameObject Resolve(string q) { int n; return Resolve(q, out n); }

        // "#instanceID", "path/with/slashes" (exact, then as the end of a path), an exact name (the nearest to the boy), then a
        // part of a name (the nearest). candidates = how many objects the winner was picked from.
        // Answered from the records of the object database, without walking the scene: the records are brought up to date
        // first when the database has slept (not from inside a capture, where a full pass would be a hitch), and once
        // more when nothing matched. "Nearest" is ranked with the positions of the last pass.
        public static GameObject Resolve(string q, out int candidates)
        {
            candidates = 0;
            if (string.IsNullOrEmpty(q)) return null;
            q = q.Trim().Trim('"');
            if (q.Length == 0) return null;
            bool fresh = false;
            try
            {
                ObjectDatabase.Demand(30f);
                Background.Want(Background.Objects, 30f);
                if (!ObjectDatabase.Ready || (internalDepth == 0 && ObjectDatabase.Age > StaleRecords)) { ObjectDatabase.EnsureFresh(3f); fresh = true; }
            }
            catch (Exception e) { DevLog.Error("resolve: object database", e); }
            if (!ObjectDatabase.Ready) return ResolveLive(q, out candidates);
            var go = ResolveRecords(q, out candidates);
            if (go != null || fresh) return go;
            int passes = ObjectDatabase.completedPasses;
            try { ObjectDatabase.EnsureFresh(3f); } catch (Exception e) { DevLog.Error("resolve: object database", e); }
            return ObjectDatabase.completedPasses == passes ? null : ResolveRecords(q, out candidates);
        }

        static GameObject ResolveRecords(string q, out int candidates)
        {
            candidates = 0;
            if (q[0] == '#')
            {
                int id;
                if (!Fmt.TryI(q.Substring(1), out id)) return null;
                var r = ObjectDatabase.Get(id);
                if (r == null) return null;
                candidates = 1;
                return r.go;
            }
            var all = ObjectDatabase.all;
            var ch = G.MainCharacter; var o = ch != null ? ch.pos3 : Vector3.zero;
            ObjRecord best = null; float bd = float.MaxValue; int n = 0;
            if (q.IndexOf('/') >= 0)
            {
                foreach (var r in ObjectDatabase.ByPath(q)) { n++; float d = (r.pos - o).sqrMagnitude; if (d < bd) { bd = d; best = r; } }
                if (best == null)
                {
                    string pl = q.ToLowerInvariant();
                    for (int i = 0; i < all.Count; i++)
                    {
                        var r = all[i];
                        if (r.pathLower == null || !r.pathLower.EndsWith(pl, StringComparison.Ordinal) || r.go == null) continue;
                        n++;
                        float d = (r.pos - o).sqrMagnitude; if (d < bd) { bd = d; best = r; }
                    }
                }
                if (best != null) { candidates = n; return best.go; }
            }
            for (int i = 0; i < all.Count; i++)
            {
                var r = all[i];
                if (r.name != q || r.go == null) continue;
                n++;
                float d = (r.pos - o).sqrMagnitude; if (d < bd) { bd = d; best = r; }
            }
            if (best == null)
            {
                string ql = q.ToLowerInvariant();
                for (int i = 0; i < all.Count; i++)
                {
                    var r = all[i];
                    if (r.nameLower == null || r.nameLower.IndexOf(ql, StringComparison.Ordinal) < 0 || r.go == null) continue;
                    n++;
                    float d = (r.pos - o).sqrMagnitude; if (d < bd) { bd = d; best = r; }
                }
            }
            candidates = n;
            return best != null ? best.go : null;
        }

        // the same search over the live scene, for the moments when the object database has no pass yet
        static GameObject ResolveLive(string q, out int candidates)
        {
            candidates = 0;
            var all = Inspector.WalkSceneTransforms();
            if (q.StartsWith("#"))
            {
                int id;
                if (Fmt.TryI(q.Substring(1), out id)) foreach (var t in all) if (t != null && t.gameObject.GetInstanceID() == id) { candidates = 1; return t.gameObject; }
                return null;
            }
            var ch = G.MainCharacter; var o = ch != null ? ch.pos3 : Vector3.zero;
            if (q.IndexOf('/') >= 0)
            {
                string leaf = q.Substring(q.LastIndexOf('/') + 1);
                foreach (var t in all) if (t != null && t.name.EndsWith(leaf, StringComparison.Ordinal) && Inspector.PathOf(t) == q) { candidates = 1; return t.gameObject; }
                foreach (var t in all) if (t != null && t.name.EndsWith(leaf, StringComparison.OrdinalIgnoreCase) && Inspector.PathOf(t).EndsWith(q, StringComparison.OrdinalIgnoreCase)) { candidates = 1; return t.gameObject; }
            }
            Transform best = null; float bd = float.MaxValue; int n = 0;
            foreach (var t in all) if (t != null && t.name == q) { n++; float d = (t.position - o).sqrMagnitude; if (d < bd) { bd = d; best = t; } }
            if (best == null) foreach (var t in all) if (t != null && t.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { n++; float d = (t.position - o).sqrMagnitude; if (d < bd) { bd = d; best = t; } }
            candidates = n;
            return best != null ? best.gameObject : null;
        }

        // Pre-binds a command for repeated or timing-critical use (replay record cues, REPLAY_DESIGN 2.3.4): for
        // `call <object> <Component> <method>` the object, component and method are resolved now and the returned delegate
        // only invokes, with the same result text as the bridge. It returns null when the target is gone (the caller then runs
        // the line through the bridge). Other verbs are not bound (null, `how` says why).
        public static Func<string> Bind(string line, out string how)
        {
            var a = Split(line ?? "");
            if (a.Count == 0) { how = "empty command"; return null; }
            if (a[0].ToLowerInvariant() != "call") { how = "only 'call' commands are pre-bound"; return null; }
            if (a.Count < 4) { how = "usage: call <object> <Component> <method>"; return null; }
            var go = Resolve(a[1]); if (go == null) { how = "object '" + a[1] + "' not found"; return null; }
            var comp = FindComp(go, a[2]); if (comp == null) { how = "component '" + a[2] + "' not found on " + go.name; return null; }
            var mi = comp.GetType().GetMethod(a[3], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (mi == null) { how = "no parameterless method " + a[3]; return null; }
            string type = comp.GetType().Name, method = a[3];
            bool returns = mi.ReturnType != typeof(void);
            how = type + "." + method + " on " + Inspector.PathOf(go.transform) + " #" + go.GetInstanceID();
            return () =>
            {
                if (comp == null) return null;
                var r = mi.Invoke(comp, null);
                return "called " + type + "." + method + (returns ? " -> " + Describe(r) : "");
            };
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
            // floats the same way everywhere: "0,5" is refused, not read as 5 (Convert would take the comma as a group separator)
            if (t == typeof(float)) return F(s);
            if (t == typeof(double)) return double.Parse(s, NumberStyles.Float, IC);
            return Convert.ChangeType(s, t, IC);
        }

        static void WriteFsmVar(NamedVariable v, object x)
        {
            if (v is FsmBool) ((FsmBool)v).Value = (bool)x;
            else if (v is FsmFloat) ((FsmFloat)v).Value = (float)x;
            else if (v is FsmInt) ((FsmInt)v).Value = (int)x;
            else if (v is FsmString) ((FsmString)v).Value = (string)x;
            else if (v is FsmVector3) ((FsmVector3)v).Value = (Vector3)x;
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
            if (v is UObj) { var uo = (UObj)v; return uo == null ? "null (destroyed)" : uo.name + " (" + uo.GetType().Name + ")"; }
            var nv = v as NamedVariable; if (nv != null) { try { return "fsmvar " + nv.Name + "=" + Fmt.Any(nv.RawValue); } catch { return "fsmvar"; } }
            if (v is Vector3) return V((Vector3)v);
            var col = v as ICollection;
            if (col != null && !(v is string))
            {
                var sb = new StringBuilder(v.GetType().Name + "[" + col.Count + "]");
                int i = 0;
                foreach (var x in col) { if (i++ >= 6) { sb.Append(" ..."); break; } sb.Append(i == 1 ? " { " : ", ").Append(x == null ? "null" : (x is UObj ? ((UObj)x) != null ? ((UObj)x).name : "null" : Fmt.Any(x))); }
                if (i > 0 && i <= 6) sb.Append(" }");
                return sb.ToString();
            }
            string s; try { s = Fmt.Any(v); } catch { s = "?"; }
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
