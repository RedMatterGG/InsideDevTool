using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace InsideDev.McpExt
{
    // Shot.cs (owner HOST, signatures frozen): screenshots for the screenshot tool and for observe {image}. Capture on the
    // main thread (Application.CaptureScreenshot to _mod/shots/<frame>_<n>.png, end of frame), decode / scale / label /
    // encode on the worker thread (Png.cs, Zlib.cs, ShotDraw.cs). Modes: preview = inline PNG at most maxWidth wide;
    // full = file only, no inline image (plus a resource link inside://shots/<file>); marked = preview with the id labels
    // of MarkSource drawn on it (set-of-mark); path = file only (alias of full, path_only accepted). black = mean luma
    // below 2/255 over a sparse grid (flag it, never fail). clean hides InsideDev panels, HUD and overlays through the
    // core's "clean on" for one frame and puts them back with "clean off" (only when this capture switched it on).
    // Metadata: frame, camera pose (G.Cam), boy screen position (G.MainCharacter.pos3 through WorldToScreenPoint).
    public static partial class McpShot
    {
        static int counter;
        static FieldInfo cleanField;
        static bool cleanLooked;
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // worker thread
        public static ShotResult Capture(CallContext ctx, ShotRequest req)
        {
            if (req == null) req = new ShotRequest();
            var r = new ShotResult { mode = NormalizeMode(req.mode) };
            string dir = ShotsDir;
            if (string.IsNullOrEmpty(dir)) { r.error = "the screenshot folder is not set (the MCP extension did not start)"; r.code = McpErr.NotReady; return r; }
            try { Directory.CreateDirectory(dir); }
            catch (Exception e) { r.error = "cannot create " + dir + ": " + e.Message; r.code = McpErr.GameError; return r; }
            int n = Interlocked.Increment(ref counter);
            bool cleaned = false;
            try
            {
                if (req.clean)
                {
                    cleaned = ctx.Main(() => CleanOn(), 5000);
                    if (cleaned) ctx.WaitFrames(1, 2000);
                }
                bool marked = r.mode == "marked";
                ctx.MainDo(() => Grab(r, dir, n, marked, req), 10000);
                if (!WaitFile(ctx, r.path))
                {
                    r.error = ctx.Cancelled ? "cancelled before the screenshot was written" : "the screenshot was not written within 10 s (is the game window minimized or not rendering?)";
                    r.code = ctx.Cancelled ? McpErr.Cancelled : McpErr.NotReady;
                    return r;
                }
            }
            catch (McpException e) { r.error = e.Message; r.code = e.code; return r; }
            finally { if (cleaned) MainQueue.Post("clean off", CleanOff); }
            byte[] png = null;
            for (int i = 0; i < 5 && png == null; i++)
            {
                try { png = File.ReadAllBytes(r.path); }
                catch (IOException) { if (!ctx.Sleep(40)) break; }
                catch (Exception e) { r.error = "cannot read " + r.path + ": " + e.Message; r.code = McpErr.GameError; return r; }
            }
            if (png == null) { r.error = "cannot read " + r.path; r.code = McpErr.GameError; return r; }
            Process(r, png, req);
            Prune(dir, Keep);
            return r;
        }

        static void Grab(ShotResult r, string dir, int n, bool marked, ShotRequest req)
        {
            r.frame = Time.frameCount;
            r.file = r.frame.ToString(IC) + "_" + n.ToString(IC) + ".png";
            r.path = Path.Combine(dir, r.file);
            Application.CaptureScreenshot(r.path);
            r.srcWidth = Screen.width; r.srcHeight = Screen.height;
            Camera cam = null;
            try { cam = G.Cam(); } catch { }
            if (cam != null)
            {
                var t = cam.transform; var p = t.position; var e = t.eulerAngles;
                var c = new Json.Obj();
                c["name"] = cam.name;
                c["x"] = R(p.x); c["y"] = R(p.y); c["z"] = R(p.z);
                c["pitch"] = R(e.x); c["yaw"] = R(e.y); c["roll"] = R(e.z);
                c["fov"] = R(cam.fieldOfView);
                r.camera = c;
                try
                {
                    var ch = G.MainCharacter;
                    if (ch != null)
                    {
                        var sp = cam.WorldToScreenPoint(ch.pos3);
                        r.hasBoy = true;
                        r.boyX = sp.x; r.boyY = Screen.height - sp.y;
                        r.boyOn = sp.z > 0 && sp.x >= 0 && sp.x <= Screen.width && r.boyY >= 0 && r.boyY <= Screen.height;
                    }
                }
                catch { }
            }
            if (!marked) return;
            if (req.marks != null) { r.marks = new List<ShotMark>(req.marks); return; }
            var src = MarkSource;
            if (src == null) { r.marks = new List<ShotMark>(); r.note = "no mark source (InsideDev.Play is not loaded)"; return; }
            try { r.marks = src(MaxMarks) ?? new List<ShotMark>(); }
            catch (Exception e) { r.marks = new List<ShotMark>(); r.note = "marks unavailable: " + e.GetType().Name + ": " + e.Message; }
            if (r.marks.Count == 0 && r.note == null) r.note = "no observed ids on screen (call observe first)";
        }

        static double R(float v) { return Math.Round(v, 2); }

        static bool WaitFile(CallContext ctx, string path)
        {
            ctx.WaitFrames(1, 2000);
            long last = -1; int stable = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10000)
            {
                long len = 0;
                try { var fi = new FileInfo(path); if (fi.Exists) len = fi.Length; } catch { }
                if (len > 0)
                {
                    if (len == last) { if (++stable >= 2) return true; }
                    else { last = len; stable = 0; }
                }
                if (!ctx.Sleep(30)) return false;
            }
            return false;
        }

        // main thread: true when this call switched clean frames on (and must switch them off)
        static bool CleanOn()
        {
            var core = DevCore.Instance;
            var f = CleanField();
            if (core == null || f == null) return false;
            if ((bool)f.GetValue(null)) return false;
            Remote.RunCommand("clean on", core);
            return (bool)f.GetValue(null);
        }

        static void CleanOff()
        {
            var core = DevCore.Instance;
            var f = CleanField();
            if (core == null || f == null || !(bool)f.GetValue(null)) return;
            Remote.RunCommand("clean off", core);
        }

        static FieldInfo CleanField()
        {
            if (cleanLooked) return cleanField;
            cleanLooked = true;
            try
            {
                var t = typeof(DevCore).Assembly.GetType("InsideDev.MultiCamClean");
                cleanField = t != null ? t.GetField("on", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
                if (cleanField != null && cleanField.FieldType != typeof(bool)) cleanField = null;
            }
            catch { cleanField = null; }
            if (cleanField == null) HostLog.Write("screenshot clean: InsideDev.MultiCamClean.on not found; shots include the editor UI");
            return cleanField;
        }
    }
}
