// ReplayLab tests that measure rendered frames: L2 skin timing and frozen blur, L4 particles, L9 static shadows,
// L10 shader time, L11 rope Draw by reflection.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev.ReplayLab
{
    public static class LabRender
    {
        const string ChickenPrep = "ChickenGun after DoSpawn (REPLAY_DESIGN 3.3 setup)";
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Register(List<LabTest> t)
        {
            t.Add(new LabTest
            {
                id = "L2", title = "skin timing per write point, velocity and frozen vs running engine blur",
                usage = "lab l2 [speed=2] [frames=12] [blurframes=24] [chick=<obj>] [bale=<obj>] [only=chick|bale] [chickat=x,y,z] [baleat=x,y,z] [blurobj=bale|chick] [thr=12] [settle=12] [cam=...]",
                prep = ChickenPrep + "; camera (cam= or the current view) shows one chick and the bale with free space to their right (they move ~0.4 m / 0.8 m along screen x); runs 2 x blurframes frames at timeScale 1 (the game advances ~0.8 s)",
                body = L2
            });
            t.Add(new LabTest
            {
                id = "L4", title = "particles: GetParticles, Pause, Clear, SetParticles on a frozen system, game camera and 60 deg off axis",
                usage = "lab l4 [view=game|off|both] [root=ChickenGunObject/FX_PipeSuck] [systems=a,b] [off=60] [edge=0.8] [maxz=-2] [pthr=8] [repeat=10] [settle=12] [cam=...]",
                prep = ChickenPrep + " with suction on (ToggleSuctionOn) so FX_PipeSuck and the straw systems have live particles; the game camera sees them",
                body = L4
            });
            t.Add(new LabTest
            {
                id = "L9", title = "static shadow caches: bale shadow under SpotLight_Machine as is / dirty / dynamic, hidden culler group shadow",
                usage = "lab l9 [light=SpotLight_Machine_BoundsCuller] [bale=<obj>] [group=<culler name>|none] [at=x,y,z] [frames=12] [step=0.05] [radius=2.5] [thr=10] [cam=...]",
                prep = ChickenPrep + "; camera (cam=) sees the floor under the machine spotlight (the bale is moved there, at= overrides the spot)",
                body = L9
            });
            t.Add(new LabTest
            {
                id = "L10", title = "shader time while frozen (_Time, _RealTime) and the OnPreCull/OnPreRender override",
                usage = "lab l10 [objs=a,b] [words=water,fly,drip] [hold=3] [live=0.5] [pthr=8] [settle=12] [cam=...]",
                prep = "a view with water, flies or drips (ChickenGun: FX_WaterDripDown2; a water scene for water); objs= names the renderers to measure",
                body = L10
            });
            t.Add(new LabTest
            {
                id = "L11", title = "rope replay: RopeLineRenderer disabled, recorded vertices written, BlendRope.Draw by reflection",
                usage = "lab l11 [rope=RopeCrane_BoundsCuller/CraneRopeLong] [samples=12] [slow=5] [kick=0] [hold=1] [margin=2] [thr=12] [settle=12] [cam=...]",
                prep = ChickenPrep + " (level171 crane rope loaded and on screen); the rope should swing (push it in game, or kick=<impulse> adds one)",
                body = L11
            });
        }

        static int Thr(LabContext c, int def) { return Math.Max(1, c.I("thr", def)); }

        static void Hook(LabContext c, string point, Action w)
        {
            c.onLate = null; c.onLate0 = null; c.onPreCull = null;
            if (w == null) return;
            if (point == "a") c.onLate = w;
            else if (point == "b") c.onLate0 = w;
            else c.onPreCull = cam => w();
        }

        static Dictionary<Renderer, bool> SaveRenderers(LabContext c, GameObject g)
        {
            var d = new Dictionary<Renderer, bool>();
            foreach (var r in LabUtil.Renderers(g)) { if (d.ContainsKey(r)) continue; d[r] = r.enabled; c.SaveRenderer(r); }
            return d;
        }

        static void Show(Dictionary<Renderer, bool> d, bool on)
        {
            foreach (var kv in d) if (kv.Key != null) { bool v = on && kv.Value; if (kv.Key.enabled != v) kv.Key.enabled = v; }
        }

        static RectI RoiAlong(Camera cam, Bounds b, Vector3 travel, int pad)
        {
            var a = LabImage.ProjectBounds(cam, b, pad);
            var b2 = b; b2.center += travel;
            return RectI.Union(a, LabImage.ProjectBounds(cam, b2, pad)).Clamp();
        }

        static void Kinematic(LabContext c, Transform t)
        {
            var rb = t != null ? t.GetComponent<Rigidbody>() : null;
            if (rb == null) return;
            c.SaveRigidbody(rb);
            rb.interpolation = RigidbodyInterpolation.None;
            rb.isKinematic = true;
        }

        // ---------------------------------------------------------------- L2

        static void Place(LabContext c, Transform t, string key)
        {
            Vector3 at;
            if (t == null || !LabUtil.TryVec(c.Arg(key, null), out at)) return;
            Bounds b;
            if (LabUtil.Bounds(t.gameObject, out b)) t.position += at - b.center; else t.position = at;
            c.Log(key + ": " + t.name + " visual centre placed at " + LabUtil.F(at.x, "0.###") + "," + LabUtil.F(at.y, "0.###") + "," + LabUtil.F(at.z, "0.###"));
        }

        static List<object> RendererList(GameObject g)
        {
            var l = new List<object>();
            if (g == null) return l;
            foreach (var r in g.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                var b = r.bounds;
                l.Add(r.GetType().Name + " " + LabUtil.ShortPath(r.transform) + (LabUtil.Shows(r) ? "" : " (not shown)") + " centre " + LabUtil.F(b.center.x, "0.##") + "," + LabUtil.F(b.center.y, "0.##") + "," + LabUtil.F(b.center.z, "0.##") + " extents " + LabUtil.F(b.extents.x, "0.##") + "," + LabUtil.F(b.extents.y, "0.##") + "," + LabUtil.F(b.extents.z, "0.##"));
            }
            return l;
        }

        static IEnumerator L2(LabContext c)
        {
            float speed = c.F("speed", 2f);
            int K = Math.Max(4, c.I("frames", 12)), K2 = Math.Max(12, c.I("blurframes", 24)), thr = Thr(c, 12);
            string only = c.Arg("only", "both").ToLowerInvariant();
            var chick = only == "bale" ? null : LabUtil.ChickRoot(c);
            var bale = only == "chick" ? null : LabUtil.Bale(c);
            if (chick == null && bale == null) c.Prep("no chick and no HayBale: " + ChickenPrep);
            c.Freeze();
            c.HoldStartCamera();
            c.CleanFrames();
            if (c.Taa() == null) c.Log("no D11PDTemporalReprojection3 on the camera");
            c.TaaClean(true);
            var objs = new List<Transform>();
            if (chick != null) objs.Add(chick);
            if (bale != null) objs.Add(bale);
            var rends = new Dictionary<Transform, Dictionary<Renderer, bool>>();
            foreach (var t in objs) { c.SaveTransform(t); Kinematic(c, t); rends[t] = SaveRenderers(c, t.gameObject); }
            Place(c, chick, "chickat");
            Place(c, bale, "baleat");
            yield return c.Settle();
            var axis = c.cam.transform.right;
            float d = speed / 60f;
            c.result["metresPerFrame"] = LabUtil.Num(d);
            var rowsA = new List<object>();
            var objInfo = new Json.Obj();
            bool passA = true;
            int measuredA = 0;
            var points = new[] { "a", "b", "c" };
            var pointNames = new Dictionary<string, string> { { "a", "RuntimeCallbacks.lateUpdate (-400)" }, { "b", "LateUpdate order 0 (LabHost, beside DevRoot)" }, { "c", "Camera.onPreCull" } };
            foreach (var t in objs)
            {
                Vector3 bp = t.position;
                var info = new Json.Obj();
                objInfo[LabUtil.ShortPath(t)] = info;
                info["renderers"] = RendererList(t.gameObject);
                Bounds bb;
                if (!LabUtil.Bounds(t.gameObject, out bb)) { rowsA.Add(Row(t, "-", "no renderer bounds")); continue; }
                info["bounds"] = LabUtil.BoundsJson(bb);
                var roi = RoiAlong(c.cam, bb, axis * d * (K + 1), 24);
                info["roi"] = roi.ToJson();
                if (roi.Empty) { rowsA.Add(Row(t, "-", "off screen")); continue; }
                c.phase = "A plate " + t.name;
                Show(rends[t], false);
                yield return c.Render(4);
                var plate = c.Grab(roi, "A_" + t.name + "_plate.jpg", "object hidden");
                Show(rends[t], true);
                c.phase = "A references " + t.name;
                var refs = new Frame[K + 1];
                for (int k = 0; k <= K; k++)
                {
                    t.position = bp + axis * d * k;
                    yield return c.Render(3);
                    refs[k] = c.Grab(roi, k == K / 2 ? "A_" + t.name + "_ref_k" + k + ".jpg" : null, k == K / 2 ? "static reference, pose k" + k : null);
                }
                t.position = bp;
                yield return c.Render(3);
                var ref0b = c.Grab(roi, null, null);
                double noise = LabImage.Psnr(refs[0], ref0b), step = LabImage.Psnr(refs[K / 2], refs[K / 2 - 1]);
                info["referenceNoisePsnr"] = LabUtil.Num(noise);
                info["onePoseStepPsnr"] = LabUtil.Num(step);
                bool separable = step < Math.Min(noise, 99.0) - 3.0;
                info["posesSeparable"] = separable;
                foreach (var p in points)
                {
                    if (p == "b" && !c.ownHost) { rowsA.Add(Row(t, pointNames[p], "skipped: LabHost unavailable")); continue; }
                    c.phase = "A " + t.name + " " + p;
                    t.position = bp;
                    yield return c.Render(3);
                    var rf = c.Grab(roi, null, null);
                    var m0 = LabImage.Diff(plate, rf, thr);
                    LabImage.Despeckle(m0, roi.w, roi.h);
                    int n0;
                    var c0 = LabImage.Centroid(m0, roi, out n0);
                    var p0 = LabImage.Screen(c.cam, bp);
                    var now = new List<double>(); var prev = new List<double>();
                    var psSame = new List<double>(); var psPrev = new List<double>();
                    int atPose = 0, late = 0;
                    Vector3 want = bp;
                    Hook(c, p, () => { if (t != null) t.position = want; });
                    for (int k = 1; k <= K; k++)
                    {
                        want = bp + axis * d * k;
                        yield return c.eof;
                        var f = c.Grab(roi, k == K / 2 ? "A_" + t.name + "_" + p + "_k" + k + ".jpg" : null, k == K / 2 ? "moving, write point " + p : null);
                        double ps = LabImage.Psnr(refs[k], f), pp = LabImage.Psnr(refs[k - 1], f);
                        psSame.Add(ps); psPrev.Add(pp);
                        if (ps > pp + 3.0) atPose++; else if (pp > ps + 3.0) late++;
                        if (n0 < 20) continue;
                        var m = LabImage.Diff(plate, f, thr);
                        LabImage.Despeckle(m, roi.w, roi.h);
                        int n;
                        var ck = LabImage.Centroid(m, roi, out n);
                        if (n < 20) continue;
                        var ek = c0 + (LabImage.Screen(c.cam, want) - p0);
                        var ep = c0 + (LabImage.Screen(c.cam, want - axis * d) - p0);
                        now.Add((ck - ek).magnitude);
                        prev.Add((ck - ep).magnitude);
                    }
                    Hook(c, p, null);
                    t.position = bp;
                    var o = Row(t, pointNames[p], null);
                    o["frames"] = K;
                    o["psnrVsWrittenPoseMean"] = LabUtil.Num(LabUtil.Mean(psSame)); o["psnrVsPreviousPoseMean"] = LabUtil.Num(LabUtil.Mean(psPrev));
                    o["psnrVsWrittenPoseMin"] = LabUtil.Num(Min(psSame));
                    o["framesAtWrittenPose"] = atPose; o["framesOneFrameLate"] = late;
                    o["silhouette"] = !separable ? "poses not separable" : atPose * 10 >= K * 9 ? "at the written pose" : late * 10 >= K * 9 ? "one frame late" : "mixed";
                    o["centroidFrames"] = now.Count; o["meanErrPx"] = LabUtil.Num(LabUtil.Mean(now)); o["maxErrPx"] = LabUtil.Num(LabUtil.Max(now)); o["meanErrVsPreviousPosePx"] = LabUtil.Num(LabUtil.Mean(prev));
                    o["pxPerFrame"] = LabUtil.Num((LabImage.Screen(c.cam, bp + axis * d) - p0).magnitude);
                    if (n0 < 20) o["centroidNote"] = "silhouette too small for the centroid (" + n0 + " px)";
                    rowsA.Add(o);
                    if (p == "a" && separable)
                    {
                        measuredA++;
                        if (atPose * 10 < K * 9) passA = false;
                        if (now.Count > 0 && !(LabUtil.Mean(now) < 1.0)) c.Log("write point a, " + t.name + ": centroid error " + LabUtil.F(LabUtil.Mean(now), "0.00") + " px (reference match decides)");
                    }
                }
            }
            c.result["objects"] = objInfo;
            c.result["skinTiming"] = rowsA;
            var bt = c.Arg("blurobj", "bale") == "chick" || bale == null ? chick : bale;
            double blurFrozen = double.NaN, blurRunning = double.NaN;
            int pxFrozen = -1, pxRunning = -1;
            var rowsB = new List<object>();
            Bounds bbb;
            if (bt != null && LabUtil.Bounds(bt.gameObject, out bbb) && c.Taa() != null)
            {
                Vector3 bp = bt.position;
                var roi = RoiAlong(c.cam, bbb, axis * d * (K2 + 1), 32);
                if (!roi.Empty)
                {
                    c.phase = "B plate";
                    c.TaaGame(-1f);
                    c.taa.frustumJitter = false;
                    Show(rends[bt], false);
                    yield return c.Render(30);
                    var plate = c.Grab(roi, "B_plate.jpg", "blur plate, game TAA without jitter");
                    Show(rends[bt], true);
                    float gameBlur = c.TaaGameBlur;
                    var streak = new double[4];
                    var last = new Frame[4];
                    var names = new[] { "frozen_game", "frozen_mb0", "running_game", "running_mb0" };
                    for (int v = 0; v < 4; v++)
                    {
                        c.phase = "B " + names[v];
                        bool running = v >= 2;
                        c.TaaGame(v % 2 == 0 ? gameBlur : 0f);
                        c.taa.frustumJitter = false;
                        c.Freeze();
                        bt.position = bp;
                        yield return c.Render(30);
                        var rest = c.Grab(roi, null, null);
                        var mr = LabImage.Diff(plate, rest, thr);
                        LabImage.Despeckle(mr, roi.w, roi.h);
                        double e0 = LabImage.RowExtent(mr, roi);
                        Vector3 want = bp;
                        Hook(c, "a", () => { if (bt != null) bt.position = want; });
                        if (running) c.TimeScale(1f);
                        var ext = new List<double>();
                        for (int k = 1; k <= K2; k++)
                        {
                            want = bp + axis * d * k;
                            yield return c.eof;
                            if (k <= K2 / 3) continue;
                            var f = c.Grab(roi, k == K2 - 1 ? "B_" + names[v] + ".jpg" : null, k == K2 - 1 ? names[v] : null);
                            if (k == K2 - 1) last[v] = f;
                            var m = LabImage.Diff(plate, f, thr);
                            LabImage.Despeckle(m, roi.w, roi.h);
                            double e = LabImage.RowExtent(m, roi);
                            if (!double.IsNaN(e)) ext.Add(e);
                        }
                        Hook(c, "a", null);
                        c.Freeze();
                        bt.position = bp;
                        streak[v] = LabUtil.Median(ext) - e0;
                        var o = new Json.Obj();
                        o["variant"] = names[v]; o["restExtentPx"] = LabUtil.Num(e0); o["movingExtentPx"] = LabUtil.Num(LabUtil.Median(ext)); o["streakPx"] = LabUtil.Num(streak[v]);
                        o["motionBlurStrength"] = LabUtil.Num(v % 2 == 0 ? gameBlur : 0f);
                        rowsB.Add(o);
                    }
                    blurFrozen = streak[0] - streak[1];
                    blurRunning = streak[2] - streak[3];
                    var dm = LabImage.Diff(last[0], last[1], thr); LabImage.Despeckle(dm, roi.w, roi.h); pxFrozen = LabImage.Count(dm);
                    var dr = LabImage.Diff(last[2], last[3], thr); LabImage.Despeckle(dr, roi.w, roi.h); pxRunning = LabImage.Count(dr);
                    if (dm != null) LabImage.SaveMask(dm, roi, c.File("B_blurmask_frozen.jpg"));
                    if (dr != null) LabImage.SaveMask(dr, roi, c.File("B_blurmask_running.jpg"));
                    c.result["gameAdvancedSeconds"] = LabUtil.Num(2.0 * K2 / 60.0);
                    c.result["blurRoi"] = roi.ToJson();
                }
            }
            c.result["blur"] = rowsB;
            c.result["blurFrozenPx"] = LabUtil.Num(blurFrozen);
            c.result["blurRunningPx"] = LabUtil.Num(blurRunning);
            c.result["blurPixelsFrozen"] = pxFrozen;
            c.result["blurPixelsRunning"] = pxRunning;
            double ratio = Math.Abs(blurRunning) >= 1.0 ? blurFrozen / blurRunning : double.NaN;
            double pxRatio = pxRunning > 0 ? pxFrozen / (double)pxRunning : double.NaN;
            c.result["blurRatioFrozenToRunning"] = LabUtil.Num(ratio);
            c.result["blurPixelRatioFrozenToRunning"] = LabUtil.Num(pxRatio);
            string blurText;
            bool blurOk = false, blurMeasured = false;
            if (double.IsNaN(blurRunning)) blurText = "blur not measured";
            else if (Math.Abs(blurRunning) < 1.0 && Math.Abs(blurFrozen) < 1.0) blurText = "no engine blur measurable (frozen " + LabUtil.F(blurFrozen, "0.0") + " px, running " + LabUtil.F(blurRunning, "0.0") + " px; blur pixels " + pxFrozen + " / " + pxRunning + ")";
            else
            {
                blurMeasured = true;
                blurOk = !double.IsNaN(ratio) && ratio >= 0.9 && ratio <= 1.1;
                blurText = "blur frozen " + LabUtil.F(blurFrozen, "0.0") + " px vs running " + LabUtil.F(blurRunning, "0.0") + " px (ratio " + LabUtil.F(ratio, "0.00") + (blurOk ? ", within 10 %" : ", outside 10 %: motionBlur game falls back to sub-frames") + "; blur pixels " + pxFrozen + " / " + pxRunning + ")";
            }
            string aText = measuredA == 0 ? "write point a not measured (poses not separable or objects not visible)" : passA ? "write point a: frame matches the written pose's static reference for " + measuredA + " object(s)" : "write point a: frame does NOT match the written pose (one frame late or mixed)";
            c.result["passSkin"] = measuredA > 0 && passA;
            c.result["passBlur"] = blurOk;
            if (measuredA == 0) c.Info(aText + "; " + blurText);
            else if (!blurMeasured) { if (passA) c.Info(aText + "; " + blurText); else c.Fail(aText + "; " + blurText); }
            else c.Verdict(passA && blurOk, aText + "; " + blurText);
        }

        static double Min(List<double> v)
        {
            double m = double.NaN;
            foreach (var x in v) if (double.IsNaN(m) || x < m) m = x;
            return m;
        }

        static Json.Obj Row(Transform t, string point, string note)
        {
            var o = new Json.Obj();
            o["object"] = LabUtil.ShortPath(t); o["writePoint"] = point;
            if (note != null) o["note"] = note;
            return o;
        }

        // ---------------------------------------------------------------- L4

        static List<ParticleSystem> FindSystems(LabContext c)
        {
            var l = new List<ParticleSystem>();
            var names = c.List("systems");
            if (names.Count > 0)
            {
                foreach (var n in names)
                {
                    var g = LabUtil.Find(n);
                    if (g == null) { c.Log("system not found: " + n); continue; }
                    foreach (var ps in g.GetComponentsInChildren<ParticleSystem>(true)) if (!l.Contains(ps)) l.Add(ps);
                }
            }
            else
            {
                foreach (var rootName in new[] { c.Arg("root", "ChickenGunObject/FX_PipeSuck"), "ChickenGunObject/FX_StrawBlow" })
                {
                    var g = LabUtil.Find(rootName);
                    if (g == null) continue;
                    foreach (var ps in g.GetComponentsInChildren<ParticleSystem>(true)) if (!l.Contains(ps)) l.Add(ps);
                }
            }
            l.RemoveAll(ps => ps == null || !ps.gameObject.activeInHierarchy || ps.particleCount == 0);
            return l;
        }

        static LabPose OffAxis(Camera cam, LabPose g, Vector3 center, float offDeg, float edge, float maxZ, out float used)
        {
            float dist = (g.p - center).magnitude;
            var dir0 = (g.p - center).normalized;
            float a = Math.Abs(offDeg);
            float sign = (center + (Quaternion.AngleAxis(a, Vector3.up) * dir0) * dist).z <= (center + (Quaternion.AngleAxis(-a, Vector3.up) * dir0) * dist).z ? 1f : -1f;
            while (a > 5f && (center + (Quaternion.AngleAxis(sign * a, Vector3.up) * dir0) * dist).z > maxZ) a -= 5f;
            used = sign * a;
            var dir = Quaternion.AngleAxis(used, Vector3.up) * dir0;
            var look = LabUtil.LookAt(center + dir * dist, center, g.fov);
            float hf = Mathf.Atan(Mathf.Tan(g.fov * 0.5f * Mathf.Deg2Rad) * cam.aspect) * Mathf.Rad2Deg;
            look.r = Quaternion.AngleAxis(-sign * hf * edge, Vector3.up) * look.r;
            return look;
        }

        static void ShowParticles(List<ParticleSystem> l, bool on)
        {
            foreach (var ps in l) { var r = ps != null ? ps.GetComponent<Renderer>() : null; if (r != null && r.enabled != on) r.enabled = on; }
        }

        static bool[] Dilate(bool[] m, int w, int h, int r)
        {
            if (m == null) return null;
            var o = new bool[m.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!m[y * w + x]) continue;
                    for (int yy = Math.Max(0, y - r); yy <= Math.Min(h - 1, y + r); yy++)
                        for (int xx = Math.Max(0, x - r); xx <= Math.Min(w - 1, x + r); xx++) o[yy * w + xx] = true;
                }
            return o;
        }

        static int ChangedIn(Frame a, Frame b, int thr, bool[] mask)
        {
            var m = LabImage.Diff(a, b, thr);
            if (m == null || mask == null) return -1;
            LabImage.Despeckle(m, a.r.w, a.r.h);
            int n = 0;
            for (int i = 0; i < m.Length; i++) if (m[i] && mask[i]) n++;
            return n;
        }

        static int Changed(Frame a, Frame b, int thr)
        {
            var m = LabImage.Diff(a, b, thr);
            if (m == null) return -1;
            LabImage.Despeckle(m, a.r.w, a.r.h);
            return LabImage.Count(m);
        }

        static RectI SystemsRect(Camera cam, List<ParticleSystem> l, int pad)
        {
            var r = new RectI();
            foreach (var ps in l)
            {
                var rr = ps.GetComponent<Renderer>();
                if (rr == null) continue;
                r = RectI.Union(r, LabImage.ProjectBounds(cam, rr.bounds, pad));
            }
            return r.Clamp();
        }

        static IEnumerator L4(LabContext c)
        {
            int size = Marshal.SizeOf(typeof(ParticleSystem.Particle));
            c.result["particleSize"] = size;
            var systems = FindSystems(c);
            if (systems.Count == 0) c.Prep("no particle system with live particles: freeze during suction (" + ChickenPrep + ", ToggleSuctionOn)");
            float off = c.F("off", 60f);
            int repeat = Math.Max(1, c.I("repeat", 10));
            c.Freeze();
            var camG = c.StartPose();
            c.HoldCamera(camG);
            c.result["camera"] = camG.ToJson();
            c.CleanFrames();
            c.TaaClean(true);
            var bufs = new Dictionary<ParticleSystem, ParticleSystem.Particle[]>();
            foreach (var ps in systems)
            {
                bool playing = ps.isPlaying, paused = ps.isPaused;
                var p = ps;
                c.Undo("particles " + ps.name, () => { if (p != null && playing && !paused) p.Play(false); });
                bufs[ps] = new ParticleSystem.Particle[Math.Max(1, ps.maxParticles)];
            }
            yield return c.Settle();
            Bounds ub = new Bounds();
            bool any = false;
            foreach (var ps in systems) { var rr = ps.GetComponent<Renderer>(); if (rr == null) continue; if (!any) { ub = rr.bounds; any = true; } else ub.Encapsulate(rr.bounds); }
            float usedOff;
            var camO = OffAxis(c.cam, camG, ub.center, off, Mathf.Clamp(c.F("edge", 0.8f), 0f, 1.2f), c.F("maxz", -2f), out usedOff);
            c.result["cameraOffAxis"] = camO.ToJson();
            c.result["offAxisDegrees"] = LabUtil.Num(usedOff);
            c.result["systemsBounds"] = LabUtil.BoundsJson(ub);
            int pthr = Math.Max(1, c.I("pthr", 8));
            string view = c.Arg("view", "both").ToLowerInvariant();
            bool doG = view != "off", doO = view != "game";
            c.result["view"] = view;
            var roiG = new RectI(); var roiO = new RectI();
            Frame bG = null, bG2 = null, hG = null, bO = null, bO2 = null, hO = null;
            if (doO)
            {
                c.phase = "before, off-axis";
                c.HoldCamera(camO);
                yield return c.Settle();
                roiO = SystemsRect(c.cam, systems, 8);
                bO = roiO.Empty ? null : c.Grab(roiO, "before_offaxis.jpg", "off-axis camera, before");
                if (bO != null)
                {
                    yield return c.Render(3);
                    bO2 = c.Grab(roiO, null, null);
                    ShowParticles(systems, false);
                    yield return c.Render(3);
                    hO = c.Grab(roiO, "hidden_offaxis.jpg", "off-axis camera, particle renderers off");
                    ShowParticles(systems, true);
                    yield return c.Render(3);
                }
            }
            if (doG)
            {
                c.phase = "before, game camera";
                c.HoldCamera(camG);
                yield return c.Settle();
                roiG = SystemsRect(c.cam, systems, 8);
                bG = roiG.Empty ? null : c.Grab(roiG, "before_game.jpg", "game camera, before");
                if (bG == null) c.Prep("the systems are not on screen from the game camera (cam=)");
                yield return c.Render(3);
                bG2 = c.Grab(roiG, null, null);
                ShowParticles(systems, false);
                yield return c.Render(3);
                hG = c.Grab(roiG, "hidden_game.jpg", "game camera, particle renderers off");
                ShowParticles(systems, true);
                yield return c.Render(3);
            }
            if (doO && !doG && bO == null) c.Prep("the off-axis camera does not see the systems");
            var cov = new Json.Obj();
            cov["roiGame"] = roiG.ToJson(); cov["roiOffAxis"] = roiO.ToJson();
            cov["noisePsnrGame"] = LabUtil.Num(LabImage.Psnr(bG, bG2)); cov["noisePsnrOffAxis"] = LabUtil.Num(LabImage.Psnr(bO, bO2));
            int noiseG = bG != null ? Changed(bG, bG2, pthr) : -1, noiseO = bO != null ? Changed(bO, bO2, pthr) : -1;
            cov["noisePixelsGame"] = noiseG; cov["noisePixelsOffAxis"] = noiseO;
            int covG = bG != null ? Changed(bG, hG, pthr) : -1, covO = bO != null ? Changed(bO, hO, pthr) : -1;
            cov["particlePixelsGame"] = covG; cov["particlePixelsOffAxis"] = covO;
            bool[] pmG = bG != null ? Dilate(LabImage.Diff(bG, hG, pthr), roiG.w, roiG.h, 3) : null;
            bool[] pmO = bO != null ? Dilate(LabImage.Diff(bO, hO, pthr), roiO.w, roiO.h, 3) : null;
            int noiseInG = bG != null ? ChangedIn(bG, bG2, pthr, pmG) : -1, noiseInO = bO != null ? ChangedIn(bO, bO2, pthr, pmO) : -1;
            cov["noisePixelsInParticleMaskGame"] = noiseInG; cov["noisePixelsInParticleMaskOffAxis"] = noiseInO;
            c.result["coverage"] = cov;
            int[] plainIn = { -1, -1 }, simIn = { -1, -1 };
            var counts = new Json.Obj();
            int total = 0;
            foreach (var ps in systems) { counts[LabUtil.ShortPath(ps.transform)] = ps.particleCount; total += ps.particleCount; }
            c.result["systems"] = counts;
            c.result["particles"] = total;
            double[] plain = { double.NaN, double.NaN }, sim = { double.NaN, double.NaN };
            int[] plainPx = { -1, -1 }, simPx = { -1, -1 };
            for (int variant = 0; variant < 2; variant++)
            {
                c.phase = variant == 0 ? "plain set" : "set + Simulate(0)";
                foreach (var ps in systems)
                {
                    var buf = bufs[ps];
                    int n = ps.GetParticles(buf);
                    ps.Pause(false);
                    ps.Clear(false);
                    ps.SetParticles(buf, n);
                    if (variant == 1) ps.Simulate(0f, false, false);
                }
                string tag = variant == 0 ? "plain" : "simulate";
                if (bG != null)
                {
                    yield return c.Settle();
                    var aG = c.Grab(roiG, "after_" + tag + "_game.jpg", "game camera, after " + tag);
                    if (variant == 0) { plain[0] = LabImage.Psnr(bG, aG); plainPx[0] = Changed(bG, aG, pthr); plainIn[0] = ChangedIn(bG, aG, pthr, pmG); } else { sim[0] = LabImage.Psnr(bG, aG); simPx[0] = Changed(bG, aG, pthr); simIn[0] = ChangedIn(bG, aG, pthr, pmG); }
                }
                else if (bO != null)
                {
                    yield return c.Settle();
                    var aO = c.Grab(roiO, "after_" + tag + "_offaxis.jpg", "off-axis camera, after " + tag);
                    if (variant == 0) { plain[1] = LabImage.Psnr(bO, aO); plainPx[1] = Changed(bO, aO, pthr); plainIn[1] = ChangedIn(bO, aO, pthr, pmO); } else { sim[1] = LabImage.Psnr(bO, aO); simPx[1] = Changed(bO, aO, pthr); simIn[1] = ChangedIn(bO, aO, pthr, pmO); }
                }
            }
            var cp = new Json.Obj();
            cp["plainGame"] = plainPx[0]; cp["plainOffAxis"] = plainPx[1]; cp["simulateGame"] = simPx[0]; cp["simulateOffAxis"] = simPx[1];
            cp["plainGameInParticleMask"] = plainIn[0]; cp["plainOffAxisInParticleMask"] = plainIn[1]; cp["simulateGameInParticleMask"] = simIn[0]; cp["simulateOffAxisInParticleMask"] = simIn[1];
            c.result["changedPixelsAfterSet"] = cp;
            c.phase = "cost";
            var sw = Stopwatch.StartNew();
            for (int r = 0; r < repeat; r++)
                foreach (var ps in systems)
                {
                    var buf = bufs[ps];
                    int n = ps.GetParticles(buf);
                    ps.SetParticles(buf, n);
                }
            sw.Stop();
            double msPer1000 = total > 0 ? sw.Elapsed.TotalMilliseconds / repeat / (total / 1000.0) : double.NaN;
            c.result["msPer1000GetSet"] = LabUtil.Num(msPer1000);
            var pr = new Json.Obj();
            pr["plainGame"] = LabUtil.Num(plain[0]); pr["plainOffAxis"] = LabUtil.Num(plain[1]); pr["simulateGame"] = LabUtil.Num(sim[0]); pr["simulateOffAxis"] = LabUtil.Num(sim[1]);
            c.result["psnr"] = pr;
            int vi = bG != null ? 0 : 1;
            int covV = vi == 0 ? covG : covO, noiseV = vi == 0 ? noiseInG : noiseInO;
            int tol = Math.Max(3 * Math.Max(0, noiseV), Math.Max(20, covV / 50));
            bool visible = covV >= 200;
            bool plainOk = visible && plainIn[vi] >= 0 && plainIn[vi] <= tol && plain[vi] >= 40;
            bool simOk = visible && simIn[vi] >= 0 && simIn[vi] <= tol && sim[vi] >= 40;
            bool sizeOk = size == 84, costOk = !double.IsNaN(msPer1000) && msPer1000 < 0.3;
            string vname = vi == 0 ? "game camera" : "off-axis camera (" + LabUtil.F(usedOff, "0") + " deg)";
            string need = !visible ? "particles not visible in this view" : plainOk ? "plain SetParticles is exact" : simOk ? "needs Simulate(0, false, false) after SetParticles" : "neither variant reproduces the frame";
            c.result["variantNeeded"] = need;
            c.result["tolerancePixels"] = tol;
            string s = vname + ": particle pixels " + covV + "; changed inside the particle mask after plain set " + plainIn[vi] + ", after set + Simulate(0) " + simIn[vi] + " (tolerance " + tol + "; whole frame " + plainPx[vi] + " / " + simPx[vi] + "); PSNR plain " + LabUtil.F(plain[vi], "0.0") + " / simulate " + LabUtil.F(sim[vi], "0.0") + " dB; " + need + "; Particle " + size + " B; " + total + " particles in " + systems.Count + " systems; " + LabUtil.F(msPer1000, "0.000") + " ms per 1000 (Get+Set)";
            if (!visible) c.Info(s);
            else c.Verdict(sizeOk && costOk && (plainOk || simOk), s);
        }

        // ---------------------------------------------------------------- L9

        static bool FloorBelow(Vector3 from, Transform ignore, out Vector3 p)
        {
            p = from;
            var hits = Physics.RaycastAll(from, Vector3.down, 60f);
            float best = float.MaxValue;
            bool found = false;
            foreach (var h in hits)
            {
                if (h.collider == null || h.collider.isTrigger) continue;
                if (ignore != null && h.collider.transform.IsChildOf(ignore)) continue;
                if (h.distance < best) { best = h.distance; p = h.point; found = true; }
            }
            return found;
        }

        static BoundsCullerRuntime GroupNear(Light light)
        {
            BoundsCullerRuntime best = null;
            float bd = float.MaxValue;
            if (BoundsCullerRuntime.boundsCullers == null) return null;
            foreach (var b in BoundsCullerRuntime.boundsCullers)
            {
                if (b == null || b.gameObject == light.gameObject || light.transform.IsChildOf(b.transform)) continue;
                if (!b.gameObject.activeInHierarchy || b.GetComponentInChildren<Renderer>() == null) continue;
                float d = b.aabb.SqrDistance(light.transform.position);
                if (d > light.range * light.range) continue;
                float dc = (b.aabb.center - light.transform.position).sqrMagnitude;
                if (dc < bd) { bd = dc; best = b; }
            }
            return best;
        }

        static IEnumerator L9(LabContext c)
        {
            var lg = LabUtil.Find(c.Arg("light", "SpotLight_Machine_BoundsCuller"));
            var light = lg != null ? lg.GetComponent<Light>() : null;
            var bale = LabUtil.Bale(c);
            if (light == null || bale == null) c.Prep("SpotLight_Machine light or HayBale missing: " + ChickenPrep);
            int N = Math.Max(4, c.I("frames", 12)), thr = Thr(c, 10);
            float step = c.F("step", 0.05f), radius = c.F("radius", 2.5f);
            c.Freeze();
            c.HoldStartCamera();
            c.CleanFrames();
            c.TaaClean(true);
            c.SaveTransform(bale);
            Kinematic(c, bale);
            bool us = light.useStaticShadows;
            var ls = light;
            c.Undo("light flags", () => { if (ls == null) return; ls.useStaticShadows = us; ls.staticShadowsDirty = true; });
            c.result["lightOriginal"] = "useStaticShadows " + us + ", staticShadowsDirty " + light.staticShadowsDirty + ", shadows " + light.shadows;
            var right = c.cam.transform.right;
            Bounds bb;
            if (!LabUtil.BodyBounds(bale.gameObject, out bb)) c.Prep("no bounds for the bale");
            c.result["baleBounds"] = LabUtil.BoundsJson(bb);
            Vector3 spot;
            if (!LabUtil.TryVec(c.Arg("at", null), out spot) && !FloorBelow(light.transform.position, bale, out spot)) c.Prep("no floor collider below the light (at=x,y,z)");
            Vector3 offset = bb.center - bale.position;
            Vector3 start = spot + Vector3.up * (bb.extents.y + 0.01f) - offset - right * step * (N / 2);
            Vector3 far = bale.position + Vector3.down * 200f;
            var circle = new List<Vector3>();
            for (int i = 0; i < 16; i++) { float a = i * Mathf.PI * 2f / 16f; circle.Add(spot + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius); }
            var roi = LabImage.Project(c.cam, circle, 8);
            var bStart = bb; bStart.center = start + offset;
            var bEnd = bStart; bEnd.center += right * step * N;
            roi = RectI.Union(roi, RectI.Union(LabImage.ProjectBounds(c.cam, bStart, 8), LabImage.ProjectBounds(c.cam, bEnd, 8))).Clamp();
            if (roi.Empty) c.Prep("the floor under the light is not on screen (cam=)");
            c.result["roi"] = roi.ToJson();
            c.result["spot"] = LabUtil.Vec(spot);
            BoundsCullerRuntime group = null;
            string gname = c.Arg("group", null);
            if (gname == null) group = GroupNear(light);
            else if (gname != "none") group = LabUtil.Culler(gname);
            var gr = group != null ? SaveRenderers(c, group.gameObject) : new Dictionary<Renderer, bool>();
            var gRects = new List<RectI>();
            foreach (var kv in gr) if (kv.Key != null && kv.Value) gRects.Add(LabImage.ProjectBounds(c.cam, kv.Key.bounds, 3));
            c.result["group"] = group != null ? LabUtil.ShortPath(group.transform) : "none";
            foreach (var fc in new[] { lg.GetComponent<BoundsCullerRuntime>(), group })
            {
                if (fc == null || !fc.valid) continue;
                var f2 = fc;
                c.Undo("culler " + fc.name, () => { if (f2 == null) return; f2.valid = true; f2.RefreshRuntimeState(); });
                fc.valid = false;
                fc.RefreshRuntimeState();
            }
            yield return c.Settle();
            if (!light.enabled || !light.gameObject.activeInHierarchy) c.Prep("the light is inactive or disabled (" + LabUtil.ShortPath(light.transform) + ")");
            c.phase = "plate";
            light.useStaticShadows = false;
            bale.position = far;
            yield return c.Render(4);
            var plate = c.Grab(roi, "plate_dynamic.jpg", "bale away, dynamic shadows");
            var nearLights = new List<object>();
            foreach (var l in UObj.FindObjectsOfType<Light>())
            {
                if (l == null || !l.enabled || !l.gameObject.activeInHierarchy || l.shadows == LightShadows.None) continue;
                if ((l.transform.position - spot).magnitude > c.F("lightradius", 25f)) continue;
                nearLights.Add(LabUtil.ShortPath(l.transform) + " " + l.type + " range " + LabUtil.F(l.range, "0.#") + " shadows " + l.shadows + " useStaticShadows " + l.useStaticShadows);
            }
            c.result["shadowLightsNear"] = nearLights;
            c.phase = "reference (bale casts no shadow)";
            var casters = new List<Renderer>();
            var castBefore = new List<UnityEngine.Rendering.ShadowCastingMode>();
            foreach (var r in bale.GetComponentsInChildren<Renderer>(true))
            {
                if (!LabUtil.Shows(r) || r.name.StartsWith("DECAL", StringComparison.OrdinalIgnoreCase)) continue;
                casters.Add(r); castBefore.Add(r.shadowCastingMode);
                var rr = r; var sm = r.shadowCastingMode;
                c.Undo("shadowCastingMode " + r.name, () => { if (rr != null) rr.shadowCastingMode = sm; });
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            var refs = new Frame[N];
            Vector3 wantR = start;
            c.onLate = () => { if (bale != null) bale.position = wantR; };
            yield return c.Render(4);
            for (int k = 0; k < N; k++)
            {
                wantR = start + right * step * k;
                yield return c.eof;
                refs[k] = c.Grab(roi, k == N / 2 ? "reference_nocast.jpg" : null, k == N / 2 ? "bale shadow casting off" : null);
            }
            c.onLate = null;
            for (int i = 0; i < casters.Count; i++) if (casters[i] != null) casters[i].shadowCastingMode = castBefore[i];
            c.result["shadowCasters"] = casters.Count;
            var modes = new[] { "asis", "static", "dirty", "dynamic" };
            var traj = new Dictionary<string, List<Vector2>>();
            var bright = new Dictionary<string, int>();
            var rows = new List<object>();
            foreach (var m in modes)
            {
                c.phase = "mode " + m;
                light.useStaticShadows = m == "dynamic" ? false : m == "asis" ? us : true;
                bool dirty = m == "dirty";
                Vector3 want = start;
                c.onLate = () => { if (dirty && ls != null) ls.staticShadowsDirty = true; if (bale != null) bale.position = want; };
                yield return c.Render(4);
                var tr = new List<Vector2>();
                var counts = new List<double>();
                var ms = new List<double>();
                for (int k = 0; k < N; k++)
                {
                    want = start + right * step * k;
                    yield return c.eof;
                    ms.Add(Time.unscaledDeltaTime * 1000.0);
                    var f = c.Grab(roi, k == N / 2 ? "shadow_" + m + ".jpg" : null, k == N / 2 ? "mode " + m : null);
                    var mask = LabImage.Darker(refs[k], f, thr);
                    if (k == N / 2) LabImage.SaveMask(mask, roi, c.File("shadowmask_" + m + ".jpg"));
                    var bk = bb; bk.center = want + offset;
                    LabImage.Exclude(mask, roi, LabImage.ProjectBounds(c.cam, bk, 6));
                    LabImage.Despeckle(mask, roi.w, roi.h);
                    int n;
                    var sc = LabImage.Centroid(mask, roi, out n);
                    tr.Add(n >= 30 ? sc : new Vector2(float.NaN, float.NaN));
                    counts.Add(n);
                }
                traj[m] = tr;
                want = far;
                Show(gr, true);
                yield return c.Render(4);
                var ga = c.Grab(roi, null, null);
                Show(gr, false);
                yield return c.Render(4);
                var gb = c.Grab(roi, m == "asis" ? "group_hidden_asis.jpg" : null, m == "asis" ? "group hidden by renderer, mode asis" : null);
                Show(gr, true);
                var bm = LabImage.Darker(gb, ga, thr);
                foreach (var gx in gRects) LabImage.Exclude(bm, roi, gx);
                LabImage.Despeckle(bm, roi.w, roi.h);
                bright[m] = group != null ? LabImage.Count(bm) : 0;
                c.onLate = null;
                var o = new Json.Obj();
                o["mode"] = m; o["shadowPixels"] = LabUtil.Nums(counts);
                var tl = new List<object>();
                foreach (var v in tr) tl.Add(float.IsNaN(v.x) ? null : (object)LabUtil.V2(v));
                o["centroids"] = tl;
                o["groupShadowRemovedPixels"] = bright[m];
                o["frameMs"] = LabUtil.Num(LabUtil.Mean(ms));
                rows.Add(o);
            }
            light.useStaticShadows = us;
            bale.position = start;
            var dyn = traj["dynamic"];
            int valid = 0;
            float span = 0f;
            for (int k = 0; k < dyn.Count; k++) if (!float.IsNaN(dyn[k].x)) { valid++; if (!float.IsNaN(dyn[0].x)) span = Math.Max(span, (dyn[k] - dyn[0]).magnitude); }
            bool dynOk = valid >= dyn.Count * 2 / 3 && span > 1f;
            var follow = new Json.Obj();
            var followOk = new Dictionary<string, bool>();
            foreach (var m in modes)
            {
                double mx = 0; int miss = 0;
                for (int k = 0; k < dyn.Count; k++)
                {
                    var a = traj[m][k]; var b = dyn[k];
                    if (float.IsNaN(b.x)) continue;
                    if (float.IsNaN(a.x)) { miss++; continue; }
                    mx = Math.Max(mx, (a - b).magnitude);
                }
                bool ok = dynOk && miss == 0 && mx <= 2.0;
                followOk[m] = ok;
                var o = new Json.Obj();
                o["maxErrVsDynamicPx"] = LabUtil.Num(mx); o["missingFrames"] = miss; o["follows"] = ok;
                bool? gone = null;
                if (group != null && bright["dynamic"] > 50) gone = bright[m] >= 0.3 * bright["dynamic"];
                o["hiddenGroupCastsNoShadow"] = gone.HasValue ? (object)gone.Value : "n/a";
                follow[m] = o;
            }
            c.result["modes"] = rows;
            c.result["follow"] = follow;
            c.result["dynamicReference"] = "valid frames " + valid + "/" + dyn.Count + ", span " + LabUtil.F(span, "0.0") + " px";
            string gtxt = group == null ? "no group" : bright["dynamic"] <= 50 ? "group shadow not visible in the ROI" : "hidden group shadow gone: asis " + (bright["asis"] >= 0.3 * bright["dynamic"]) + ", static " + (bright["static"] >= 0.3 * bright["dynamic"]) + ", dirty " + (bright["dirty"] >= 0.3 * bright["dynamic"]);
            string s = "bale shadow follows: asis(useStaticShadows " + us + ") " + followOk["asis"] + ", static " + followOk["static"] + ", static+dirty " + followOk["dirty"] + ", dynamic " + followOk["dynamic"] + "; " + gtxt;
            if (!dynOk) c.Info("no measurable bale shadow in the ROI (dynamic reference: " + valid + "/" + dyn.Count + " frames, span " + LabUtil.F(span, "0.0") + " px); " + gtxt);
            else
            {
                bool groupOk = group == null || bright["dynamic"] <= 50 || bright["asis"] >= 0.3 * bright["dynamic"];
                c.Verdict(followOk["asis"] && groupOk, s);
            }
        }

        // ---------------------------------------------------------------- L10

        sealed class Mask { public Renderer r; public RectI rect; public string label; }

        static int CountIn(Frame a, Frame b, int thr, RectI sub)
        {
            if (a == null || b == null || a.px.Length != b.px.Length) return -1;
            var m = LabImage.Diff(a, b, thr);
            LabImage.Despeckle(m, a.r.w, a.r.h);
            int n = 0;
            for (int y = Math.Max(0, sub.y - a.r.y); y < Math.Min(a.r.h, sub.y + sub.h - a.r.y); y++)
                for (int x = Math.Max(0, sub.x - a.r.x); x < Math.Min(a.r.w, sub.x + sub.w - a.r.x); x++)
                    if (m[y * a.r.w + x]) n++;
            return n;
        }

        static IEnumerator L10(LabContext c)
        {
            float hold = Math.Max(1f, c.F("hold", 3f)), liveS = Math.Max(0f, c.F("live", 0.5f));
            int pthr = Math.Max(1, c.I("pthr", 8));
            c.Freeze();
            c.HoldStartCamera();
            c.CleanFrames();
            c.TaaClean(true);
            yield return c.Settle();
            var masks = new List<Mask>();
            var names = c.List("objs");
            if (names.Count > 0)
            {
                foreach (var n in names)
                {
                    var g = LabUtil.Find(n);
                    if (g == null) { c.Log("not found: " + n); continue; }
                    foreach (var r in LabUtil.Renderers(g)) AddMask(c, masks, r);
                }
            }
            else
            {
                var words = c.List("words");
                if (words.Count == 0) words = new List<string> { "water", "fly", "flies", "drip", "dust", "rain" };
                var cand = new List<Mask>();
                foreach (var r in UObj.FindObjectsOfType<Renderer>())
                {
                    if (r == null || !r.enabled || !r.isVisible || !r.gameObject.activeInHierarchy) continue;
                    string key = (r.name + " " + (r.sharedMaterial != null ? r.sharedMaterial.name : "")).ToLowerInvariant();
                    bool hit = false;
                    foreach (var w in words) if (key.Contains(w.ToLowerInvariant())) { hit = true; break; }
                    if (hit) AddMask(c, cand, r);
                }
                cand.Sort((a, b) => b.rect.Count.CompareTo(a.rect.Count));
                for (int i = 0; i < cand.Count && masks.Count < 16; i++) masks.Add(cand[i]);
            }
            if (masks.Count == 0) c.Prep("no water / fly / drip renderer on screen (objs=<names> or words=)");
            var roi = new RectI();
            foreach (var m in masks) roi = RectI.Union(roi, m.rect);
            roi = roi.Clamp();
            Frame l0 = null, l1 = null;
            if (liveS > 0f)
            {
                c.phase = "live reference (timeScale 1)";
                yield return c.eof;
                l0 = c.Grab(roi, "live_0.jpg", "running, start");
                c.TimeScale(1f);
                float tl = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - tl < liveS) yield return null;
                yield return c.eof;
                l1 = c.Grab(roi, "live_1.jpg", "running, +" + LabUtil.F(liveS, "0.0") + " s");
                c.Freeze();
                yield return c.Settle();
            }
            c.phase = "frozen hold";
            float t0 = Time.realtimeSinceStartup;
            yield return c.eof;
            var f0 = c.Grab(roi, "frozen_0s.jpg", "frozen t=0");
            yield return c.Render(2);
            var f0b = c.Grab(roi, null, null);
            while (Time.realtimeSinceStartup - t0 < hold * 0.5f) yield return null;
            yield return c.eof;
            var f15 = c.Grab(roi, "frozen_half.jpg", "frozen t=" + LabUtil.F(hold * 0.5, "0.0") + " s");
            while (Time.realtimeSinceStartup - t0 < hold) yield return null;
            yield return c.eof;
            var f3 = c.Grab(roi, "frozen_end.jpg", "frozen t=" + LabUtil.F(hold, "0.0") + " s");
            var probe = c.AddProbe();
            int idTime = Shader.PropertyToID("_Time"), idReal = Shader.PropertyToID("_RealTime");
            var ov = new Frame[2, 3];
            for (int which = 0; which < 2; which++)
            {
                c.phase = which == 0 ? "override _Time" : "override _RealTime";
                double T = 0;
                int id = which == 0 ? idTime : idReal;
                Action<Camera> set = cam =>
                {
                    if (id == idTime) Shader.SetGlobalVector(idTime, new Vector4((float)(T / 20.0), (float)T, (float)(2.0 * T), (float)(3.0 * T)));
                    else Shader.SetGlobalVector(idReal, new Vector4((float)T, (float)(1000.0 * T), (float)Math.Sin(T), (float)(T - Math.Floor(T))));
                };
                c.onPreCull = set; c.onPreRender = set; c.onProbePreCull = set; c.onProbePreRender = set;
                string tag = which == 0 ? "time" : "realtime";
                var Ts = new[] { 0.0, (double)hold, 0.0 };
                for (int j = 0; j < 3; j++)
                {
                    T = Ts[j];
                    yield return c.Render(3);
                    ov[which, j] = c.Grab(roi, j < 2 ? "override_" + tag + "_T" + LabUtil.F(Ts[j], "0.#") + ".jpg" : null, j < 2 ? "override " + tag + " T=" + LabUtil.F(Ts[j], "0.#") : null);
                }
                c.onPreCull = null; c.onPreRender = null; c.onProbePreCull = null; c.onProbePreRender = null;
                yield return c.Render(3);
            }
            var rows = new List<object>();
            int liveAnim = 0, frozenAnim = 0, timeEff = 0, realEff = 0, uncontrolled = 0, liveNotDriven = 0;
            foreach (var m in masks)
            {
                int noise = CountIn(f0, f0b, pthr, m.rect);
                int lim = Math.Max(20, 3 * Math.Max(0, noise));
                int live = l0 != null ? CountIn(l0, l1, pthr, m.rect) : -1;
                int frozenHalf = CountIn(f0, f15, pthr, m.rect), frozenEnd = CountIn(f0, f3, pthr, m.rect);
                int tOv = CountIn(ov[0, 0], ov[0, 1], pthr, m.rect), tRep = CountIn(ov[0, 0], ov[0, 2], pthr, m.rect);
                int rOv = CountIn(ov[1, 0], ov[1, 1], pthr, m.rect), rRep = CountIn(ov[1, 0], ov[1, 2], pthr, m.rect);
                bool animLive = live > lim;
                int frozenMax = Math.Max(frozenHalf, frozenEnd);
                bool anim = frozenMax > lim && (live < 0 || frozenMax * 10 > live);
                bool tEff = tOv > lim && tRep <= lim, rEff = rOv > lim && rRep <= lim;
                var o = new Json.Obj();
                o["renderer"] = m.label; o["rect"] = m.rect.ToJson();
                o["pixelsNoise"] = noise; o["pixelsLive"] = live; o["pixelsFrozenHalf"] = frozenHalf; o["pixelsFrozenEnd"] = frozenEnd;
                o["pixelsTimeOverride"] = tOv; o["pixelsTimeRepeat"] = tRep; o["pixelsRealTimeOverride"] = rOv; o["pixelsRealTimeRepeat"] = rRep;
                o["psnr0vsEnd"] = LabUtil.Num(LabImage.PsnrIn(f0, f3, m.rect)); o["psnrTimeOverride"] = LabUtil.Num(LabImage.PsnrIn(ov[0, 0], ov[0, 1], m.rect)); o["psnrRealTimeOverride"] = LabUtil.Num(LabImage.PsnrIn(ov[1, 0], ov[1, 1], m.rect));
                o["animatesRunning"] = animLive; o["animatesWhenFrozen"] = anim; o["frozenToRunningRatio"] = LabUtil.Num(live > 0 ? frozenMax / (double)live : double.NaN); o["timeOverrideEffective"] = tEff; o["realTimeOverrideEffective"] = rEff;
                rows.Add(o);
                if (animLive) liveAnim++;
                if (anim) frozenAnim++;
                if (tEff) timeEff++;
                if (rEff) realEff++;
                if (anim && !(tEff || rEff)) uncontrolled++;
                if (animLive && !anim && !(tEff || rEff)) liveNotDriven++;
            }
            c.result["masks"] = rows;
            c.result["componentProbe"] = probe != null;
            c.result["pixelThreshold"] = pthr;
            c.result["roi"] = roi.ToJson();
            string s = masks.Count + " renderers: " + liveAnim + " change while running (" + LabUtil.F(liveS, "0.0") + " s), " + frozenAnim + " animate while frozen, _Time override changes " + timeEff + ", _RealTime override changes " + realEff + "; frozen-animating and not controllable " + uncontrolled + "; running-only and not driven by either override " + liveNotDriven;
            if (frozenAnim == 0 && timeEff == 0 && realEff == 0 && liveAnim == 0) c.Info(s + " (nothing time-driven in these masks)");
            else c.Verdict(uncontrolled == 0 && liveNotDriven == 0, s);
        }

        static void AddMask(LabContext c, List<Mask> l, Renderer r)
        {
            if (r == null) return;
            var rect = LabImage.ProjectBounds(c.cam, r.bounds, 2);
            if (rect.Empty || rect.Count < 64) return;
            l.Add(new Mask { r = r, rect = rect, label = LabUtil.ShortPath(r.transform) + " [" + (r.sharedMaterial != null ? r.sharedMaterial.name + " / " + (r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "?") : "no material") + "]" });
        }

        // ---------------------------------------------------------------- L11

        static IEnumerator L11(LabContext c)
        {
            var go = LabUtil.Find(c.Arg("rope", "RopeCrane_BoundsCuller/CraneRopeLong"));
            var rlr = go != null ? go.GetComponent<RopeLineRenderer>() : null;
            if (rlr == null) c.Prep("RopeLineRenderer not found (rope=<obj>): " + ChickenPrep);
            var fBlend = LabUtil.Field(typeof(RopeLineRenderer), "blendRope");
            var blend = fBlend != null ? fBlend.GetValue(rlr) : null;
            if (blend == null) c.Prep("blendRope is null (the rope has not started; is it active?)");
            var tb = blend.GetType();
            var fVerts = LabUtil.Field(tb, "vertices");
            var fPost = LabUtil.Field(tb, "postSleepDraw");
            var verts = fVerts != null ? fVerts.GetValue(blend) as Array : null;
            var tv = tb.GetNestedType("BlendVertex", All);
            var fPos = tv != null ? LabUtil.Field(tv, "position") : null;
            var tMode = tb.GetNestedType("DrawMode", All);
            var mDraw = tb.GetMethod("Draw", All);
            var lr = LabUtil.Get(rlr, "lineRenderer") as LineRenderer;
            var meshBones = LabUtil.Get(rlr, "meshBones") as Transform[];
            if (verts == null || fPos == null || fPost == null || tMode == null || mDraw == null) c.Prep("RopeLineRenderer internals not found by reflection");
            int modeId = rlr.meshOutput != null ? 4 : rlr.lineDivisions > 2 ? 3 : 1;
            object mode = Enum.ToObject(tMode, modeId);
            Renderer ropeR = rlr.meshOutput != null ? (Renderer)rlr.meshOutput : lr;
            if (ropeR == null) c.Prep("rope has no LineRenderer and no meshOutput");
            object[] drawArgs = modeId == 4 ? new object[] { mode, null, rlr.lineDivisions, rlr.visualCollisionSafety, meshBones, rlr.meshBoneLength }
                                            : new object[] { mode, lr, rlr.lineDivisions, rlr.visualCollisionSafety, null, 0.4f };
            int N = Math.Max(3, c.I("samples", 12)), slow = Math.Max(1, c.I("slow", 5)), thr = Thr(c, 12);
            float kick = c.F("kick", 0f);
            var rope = go.GetComponent<Rope>();
            int vc = verts.Length;
            c.result["mode"] = Enum.GetName(tMode, mode);
            c.result["vertexCount"] = vc;
            c.SaveEnabled(rlr);
            c.SaveRenderer(ropeR);
            bool postBefore = (bool)fPost.GetValue(blend);
            c.Undo("rope postSleepDraw", () => { try { fPost.SetValue(blend, postBefore); } catch { } });
            c.HoldStartCamera();
            c.CleanFrames();
            c.TaaClean(true);
            c.Freeze();
            yield return c.Settle();
            int sampleHold = Math.Max(0, c.I("hold", 1));
            Func<Vector3[]> read = () => { var a = new Vector3[vc]; for (int i = 0; i < vc; i++) a[i] = (Vector3)fPos.GetValue(verts.GetValue(i)); return a; };
            Action<Vector3[]> write = a => { for (int i = 0; i < vc; i++) fPos.SetValue(verts.GetValue(i), a[i]); };
            if (kick > 0f && rope != null && rope.bodies != null && rope.bodies.Length > 0)
            {
                c.phase = "kick";
                var body = rope.bodies[rope.bodies.Length - 1];
                if (body != null && !body.isKinematic) body.AddForce(c.cam.transform.right * kick, ForceMode.Impulse);
                c.TimeScale(1f);
                yield return c.Frames(30);
                c.Freeze();
                c.result["kick"] = LabUtil.Num(kick);
            }
            var vNow = read();
            var pts = new List<Vector3>(vNow);
            if (rope != null) pts.Add(rope.topPos);
            var eb = ropeR.bounds;
            eb.Expand(c.F("margin", 2f));
            var roi = RectI.Union(LabImage.Project(c.cam, pts, 60), LabImage.ProjectBounds(c.cam, eb, 40)).Clamp();
            if (roi.Empty) c.Prep("the rope is not on screen (cam=)");
            c.result["roi"] = roi.ToJson();
            var V = new List<Vector3[]>();
            var tops = new List<Vector3>();
            var rec = new List<bool[]>();
            var recCounts = new List<double>();
            bool clipped = false;
            for (int s = 0; s < N; s++)
            {
                c.phase = "record " + s;
                ropeR.enabled = true;
                rlr.enabled = true;
                c.TimeScale(1f);
                yield return null;
                yield return null;
                V.Add(read());
                tops.Add(rope != null ? rope.topPos : Vector3.zero);
                c.Freeze();
                rlr.enabled = false;
                yield return c.eof;
                yield return c.eof;
                var a = c.Grab(roi, s == 0 || s == N / 2 ? "record_s" + s + ".jpg" : null, "record sample " + s);
                ropeR.enabled = false;
                yield return c.eof;
                var b = c.Grab(roi, null, null);
                ropeR.enabled = true;
                var m = LabImage.Diff(a, b, thr);
                LabImage.Despeckle(m, roi.w, roi.h);
                rec.Add(m);
                recCounts.Add(LabImage.Count(m));
                if (Touches(m, roi.w, roi.h)) clipped = true;
            }
            double amp = 0;
            for (int s = 1; s < N; s++) for (int i = 0; i < vc; i++) amp = Math.Max(amp, (V[s][i] - V[0][i]).magnitude);
            double topVar = 0;
            for (int s = 1; s < N; s++) topVar = Math.Max(topVar, (tops[s] - tops[0]).magnitude);
            c.result["amplitudeM"] = LabUtil.Num(amp);
            c.result["topPosVariationM"] = LabUtil.Num(topVar);
            c.phase = "replay";
            rlr.enabled = false;
            int draws = 0, exceptions = 0, skipped = 0;
            string firstError = null;
            Vector3[] cur = V[0];
            c.onLate = () =>
            {
                try
                {
                    write(cur);
                    if ((bool)fPost.GetValue(blend)) skipped++;
                    fPost.SetValue(blend, false);
                    mDraw.Invoke(blend, drawArgs);
                    draws++;
                }
                catch (Exception e) { exceptions++; if (firstError == null) firstError = (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
            };
            var dist = new List<double>();
            var repCounts = new List<double>();
            int outFrames = slow * (N - 1);
            for (int j = 0; j <= outFrames; j++)
            {
                double T = j / (double)slow;
                int i0 = Math.Min((int)Math.Floor(T), N - 2);
                float u = (float)(T - i0);
                var p = new Vector3[vc];
                for (int i = 0; i < vc; i++) p[i] = Vector3.Lerp(V[i0][i], V[i0 + 1][i], u);
                cur = p;
                yield return c.eof;
                if (j % slow != 0) continue;
                for (int h = 0; h < sampleHold; h++) yield return c.eof;
                int s = j / slow;
                var a = c.Grab(roi, s == 0 || s == N / 2 ? "replay_s" + s + ".jpg" : null, "replay at sample " + s);
                ropeR.enabled = false;
                yield return c.eof;
                var b = c.Grab(roi, null, null);
                ropeR.enabled = true;
                var m = LabImage.Diff(a, b, thr);
                LabImage.Despeckle(m, roi.w, roi.h);
                repCounts.Add(LabImage.Count(m));
                dist.Add(LabImage.SilhouetteDistance(rec[s], m, roi.w, roi.h, 6, 0.99));
                if (s == N / 2) { LabImage.SaveMask(rec[s], roi, c.File("mask_record_s" + s + ".jpg")); LabImage.SaveMask(m, roi, c.File("mask_replay_s" + s + ".jpg")); }
            }
            c.onLate = null;
            write(V[N - 1]);
            c.result["silhouetteDistancePx"] = LabUtil.Nums(dist);
            c.result["recordMaskPixels"] = LabUtil.Nums(recCounts);
            c.result["replayMaskPixels"] = LabUtil.Nums(repCounts);
            c.result["draws"] = draws; c.result["drawsExpected"] = outFrames + 1 + N * (1 + sampleHold); c.result["sampleHoldFrames"] = sampleHold; c.result["postSleepDrawWasTrue"] = skipped; c.result["exceptions"] = exceptions;
            if (firstError != null) c.result["firstException"] = firstError;
            c.result["maskClippedByRoi"] = clipped;
            bool visible = LabUtil.Mean(recCounts) >= 20;
            double maxD = LabUtil.Max(dist);
            bool ok = visible && exceptions == 0 && draws >= outFrames + 1 && maxD <= 1.0;
            string s2 = "rope silhouette max " + LabUtil.F(maxD, "0") + " px over " + dist.Count + " samples, " + draws + " draws, " + exceptions + " exceptions, amplitude " + LabUtil.F(amp, "0.000") + " m";
            if (!visible) c.Info("rope not visible in the frame (record mask " + LabUtil.F(LabUtil.Mean(recCounts), "0") + " px); " + s2);
            else if (amp < 0.01 && ok) c.Info("rope barely moving (use kick=1); " + s2);
            else c.Verdict(ok, s2);
        }

        static bool Touches(bool[] m, int w, int h)
        {
            if (m == null) return false;
            for (int x = 0; x < w; x++) if (m[x] || m[(h - 1) * w + x]) return true;
            for (int y = 0; y < h; y++) if (m[y * w] || m[y * w + w - 1]) return true;
            return false;
        }
    }
}
