// InsideDev.ReplayLab: the Phase 0 lab tests L1-L12 of docs/REPLAY_DESIGN.md (Section 9.1) as "lab" bridge commands.
// Each test runs alone as a coroutine, restores everything it changed in a finally block and writes
// <game>/_mod/replay_tests/<test>/verdict.txt (lab out <dir> picks another folder) (one line PASS/FAIL/INFO), result.json, log.txt and its grabs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace InsideDev.ReplayLab
{
    public sealed class ReplayLabExtension : IInsideDevExtension, IDisposable
    {
        public string Name { get { return "ReplayLab " + Lab.Version + " (lab help)"; } }
        public void Start(DevCore core) { Lab.core = core; Lab.DefaultOut(); }
        public void Tick(DevCore core) { Lab.Tick(); }
        public string Command(List<string> a, DevCore core)
        {
            if (a == null || a.Count == 0 || !a[0].Equals("lab", StringComparison.OrdinalIgnoreCase)) return null;
            Lab.core = core;
            Lab.DefaultOut();
            return Lab.Command(a);
        }
        public void Dispose() { Lab.Quit(); }
    }

    public sealed class LabTest
    {
        public string id, title, usage, prep;
        public Func<LabContext, IEnumerator> body;
        public Func<LabContext, string> name;
    }

    public static class Lab
    {
        public const string Version = "0.2.1";
        public static DevCore core;
        public static string outRoot = "";
        public static void DefaultOut() { if (string.IsNullOrEmpty(outRoot)) outRoot = Path.Combine(Extensions.Dir, "replay_tests").Replace('\\', '/'); }
        static LabContext run;
        static string lastLine = "none";
        static readonly Dictionary<string, string> verdicts = new Dictionary<string, string>();
        static List<LabTest> tests;

        static List<LabTest> Tests
        {
            get
            {
                if (tests == null)
                {
                    tests = new List<LabTest>();
                    LabCore.Register(tests);
                    LabRender.Register(tests);
                    LabCull.Register(tests);
                    LabAudio.Register(tests);
                    tests.Sort((x, y) => Order(x.id).CompareTo(Order(y.id)));
                }
                return tests;
            }
        }

        static int Order(string id) { int n; return int.TryParse(id.Substring(1), out n) ? n : 99; }

        public static string Command(List<string> a)
        {
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "help";
            switch (sub)
            {
                case "help": return Help();
                case "list": return List();
                case "status": return Status();
                case "stop":
                case "abort":
                    if (run == null || run.finished) return "no lab test running";
                    run.abort = true; run.abortReason = "lab " + sub;
                    return "stopping " + run.id + " (state is restored at the next frame)";
                case "out":
                    if (a.Count > 2) outRoot = a[2].Replace('\\', '/').TrimEnd('/');
                    return "lab output root " + outRoot;
                case "verdicts":
                    {
                        var sb = new StringBuilder();
                        foreach (var t in Tests)
                        {
                            string v = ReadVerdict(t.id);
                            sb.Append(t.id).Append("  ").Append(v).Append('\n');
                        }
                        return sb.ToString().TrimEnd();
                    }
            }
            foreach (var t in Tests) if (t.id.Equals(sub, StringComparison.OrdinalIgnoreCase)) return Start(t, a);
            return "unknown lab command '" + sub + "'\n" + Help();
        }

        static string ReadVerdict(string id)
        {
            string v;
            if (verdicts.TryGetValue(id, out v)) return v;
            try
            {
                var p = Path.Combine(outRoot, id + "/verdict.txt");
                if (File.Exists(p)) return File.ReadAllText(p).Trim() + "  (file)";
            }
            catch { }
            return "-";
        }

        static string Help()
        {
            var sb = new StringBuilder();
            sb.Append("lab help | list | status | stop | verdicts | out <dir>\n");
            sb.Append("common args: cam=x,y,z,yaw,pitch[,fov] (free camera pose for the test), clean=on|off (editor overlay, default on), thr=<diff threshold>, timeout=<s>\n");
            foreach (var t in Tests) sb.Append(t.usage).Append('\n');
            sb.Append("results: ").Append(outRoot).Append("/<test>/verdict.txt, result.json, log.txt, *.jpg");
            return sb.ToString();
        }

        static string List()
        {
            var sb = new StringBuilder();
            foreach (var t in Tests)
            {
                sb.Append(t.id).Append("  ").Append(t.title).Append('\n');
                sb.Append("    usage: ").Append(t.usage).Append('\n');
                sb.Append("    prep:  ").Append(t.prep).Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        static string Status()
        {
            if (run != null && !run.finished)
                return run.id + " running: phase '" + run.phase + "', frame " + (Time.frameCount - run.startFrame) + ", " +
                       (Time.realtimeSinceStartup - run.startReal).ToString("0.0", LabUtil.IC) + " s" + (run.abort ? " (stopping)" : "") + "\nlast: " + lastLine;
            return "idle; last: " + lastLine;
        }

        static string Start(LabTest t, List<string> a)
        {
            if (run != null && !run.finished) return "busy: " + run.id + " is running (lab status | lab stop)";
            if (MultiCam.Running) return "refused: an mcam session is running";
            var probe = new LabContext(t.id, t.id, "", a);
            string name = t.name != null ? t.name(probe) : t.id;
            if (name == null) return "usage: " + t.usage;
            string dir = Path.Combine(outRoot, name).Replace('\\', '/');
            var c = new LabContext(t.id, name, dir, a);
            if (c.cam == null) return "no gameplay camera (load a level first)";
            try { Archive(dir); Directory.CreateDirectory(dir); }
            catch (Exception e) { return "cannot create " + dir + ": " + e.Message; }
            bool own;
            var host = LabHooks.Host(out own);
            if (host == null) return "no coroutine host (LabHost and DevRoot unavailable)";
            c.ownHost = own;
            run = c;
            c.Log("start " + name + " (" + string.Join(" ", a.ToArray()) + ")" + (own ? "" : " [LabHost unavailable, DevRoot hosts the coroutine; write point lateUpdate0 off]"));
            host.StartCoroutine(Drive(c, t));
            return "started " + name + " -> " + dir + "  (lab status; verdict in verdict.txt)";
        }

        static void Archive(string dir)
        {
            if (!Directory.Exists(dir)) return;
            var files = Directory.GetFiles(dir);
            if (files.Length == 0) return;
            string dst = Path.Combine(dir, "history/" + DateTime.Now.ToString("yyyyMMdd_HHmmss", LabUtil.IC));
            Directory.CreateDirectory(dst);
            foreach (var f in files)
            {
                try { File.Move(f, Path.Combine(dst, Path.GetFileName(f))); } catch { }
            }
        }

        static IEnumerator Drive(LabContext c, LabTest t)
        {
            var stack = new Stack<IEnumerator>();
            try
            {
                LabHooks.Install(c);
                c.Undo("hooks", LabHooks.Uninstall);
                stack.Push(t.body(c));
                while (stack.Count > 0)
                {
                    if (c.abort) { if (c.verdict == null) c.Info("aborted: " + c.abortReason); break; }
                    if (Time.realtimeSinceStartup - c.startReal > c.timeout) { c.Fail("timeout after " + c.timeout + " s in phase " + c.phase); break; }
                    var top = stack.Peek();
                    bool more = false;
                    object cur = null;
                    try
                    {
                        more = top.MoveNext();
                        if (more) cur = top.Current;
                    }
                    catch (LabPrep p) { c.Info("not run: " + p.Message); c.Log("prep: " + p.Message); break; }
                    catch (Exception e) { c.Fail("exception in phase " + c.phase + ": " + e.GetType().Name + ": " + e.Message); c.Log(e.ToString()); break; }
                    if (!more) { stack.Pop(); continue; }
                    var sub = cur as IEnumerator;
                    if (sub != null) { stack.Push(sub); continue; }
                    yield return cur;
                    c.Step();
                }
            }
            finally { Finish(c); }
        }

        static void Finish(LabContext c)
        {
            if (c.finished) return;
            c.finished = true;
            c.onUpdate = c.onLate = c.onLate0 = null;
            c.onPreCull = c.onPreRender = c.onPostRender = c.onProbePreCull = c.onProbePreRender = c.onProbePostRender = null;
            try { c.RunUndo(); } catch (Exception e) { c.restoreErrors.Add("undo: " + e.Message); }
            try { LabHooks.Uninstall(); } catch { }
            try { LabImage.Release(); } catch { }
            if (c.verdict == null) c.Info("finished without a verdict");
            foreach (var e in c.restoreErrors) c.Log("restore error: " + e);
            c.Log("verdict: " + c.VerdictLine());
            c.WriteOutputs();
            lastLine = c.id + " " + c.VerdictLine();
            verdicts[c.id] = c.VerdictLine();
            if (run == c) run = null;
        }

        public static void Tick()
        {
            LabAudio.Watchdog();
            var c = run;
            if (c == null || c.finished) return;
            if (Time.frameCount - Math.Max(c.lastStepFrame, c.startFrame) > 600 && Time.realtimeSinceStartup - c.startReal > 10f)
            {
                c.abortReason = "watchdog: the test coroutine stopped advancing";
                if (c.verdict == null) c.Fail(c.abortReason);
                Finish(c);
            }
        }

        public static void Quit()
        {
            try { LabAudio.Emergency("game quit"); } catch { }
            var c = run;
            if (c != null && !c.finished) { c.abortReason = "game quit"; if (c.verdict == null) c.Info("aborted: game quit"); Finish(c); }
        }
    }
}
