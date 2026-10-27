// ReplayLab tests without image analysis: L1 write order, L3 rigidbody and animation at timeScale 0, L6 RNG fingerprint,
// L8 RuntimeCallbacks instances.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UObj = UnityEngine.Object;
using URandom = UnityEngine.Random;

namespace InsideDev.ReplayLab
{
    public static class LabCore
    {
        const string ChickenPrep = "ChickenGun after DoSpawn, frozen or running (setup of REPLAY_DESIGN 3.3: load 7 0, tp 221.0 -221.4 0.66, OnElectricityOn, DoSpawn)";

        public static void Register(List<LabTest> t)
        {
            t.Add(new LabTest
            {
                id = "L1", title = "write order (lateUpdate -400 vs Camera.onPreCull) and onPreCull call order",
                usage = "lab l1 [frames=120] [chick=<obj>] [bale=<obj>] [wheel=<obj>] [cam=...]",
                prep = ChickenPrep + "; a chick, the bale and MotorWheel loaded (on screen is not required)",
                body = L1
            });
            t.Add(new LabTest
            {
                id = "L3", title = "rigidbody interpolation / kinematic and legacy Animation at timeScale 0",
                usage = "lab l3 [trials=6] [chick=<obj>] [bale=<obj>] [cam=...]",
                prep = ChickenPrep + "; a chick and the bale loaded",
                body = L3
            });
            t.Add(new LabTest
            {
                id = "L6", title = "UnityEngine.Random.seed fingerprint (read does not consume, set back reproduces)",
                usage = "lab l6",
                prep = "any loaded level",
                body = L6
            });
            t.Add(new LabTest
            {
                id = "L8", title = "RuntimeCallbacks instances and the once-per-frame guard",
                usage = "lab l8 [frames=120]",
                prep = "any loaded level, game running (timeScale 1) so fixedUpdate fires too",
                body = L8
            });
        }

        // ---------------------------------------------------------------- L1

        sealed class Target
        {
            public Transform t; public bool local; public string kind;
            public Vector3 basePos, wantPos; public Quaternion baseRot, wantRot;
            public int written, preCull, probe, preRender;
            public double maxPos, maxRot;
        }

        static void Add(List<Target> l, Transform t, bool local, string kind)
        {
            if (t == null) return;
            foreach (var x in l) if (x.t == t) return;
            l.Add(new Target { t = t, local = local, kind = kind });
        }

        static bool Changed(Target x, out double dp, out double dr)
        {
            var p = x.local ? x.t.localPosition : x.t.position;
            var r = x.local ? x.t.localRotation : x.t.rotation;
            dp = LabUtil.PosDelta(p, x.wantPos);
            dr = LabUtil.RotDelta(r, x.wantRot);
            return dp > 1e-5 || dr > 1e-3;
        }

        static IEnumerator L1(LabContext c)
        {
            int frames = Math.Max(10, c.I("frames", 120));
            c.phase = "find";
            var targets = new List<Target>();
            var chick = LabUtil.ChickRoot(c);
            if (chick != null)
            {
                Add(targets, chick, false, "chick root");
                foreach (var b in LabUtil.ChickBones(chick)) Add(targets, b, true, "chick bone");
            }
            Add(targets, LabUtil.Bale(c), false, "bale");
            var wheel = LabUtil.Find(c.Arg("wheel", "MotorWheel"));
            if (wheel != null) Add(targets, wheel.transform, true, "MotorWheel");
            if (targets.Count == 0) c.Prep("no chick, HayBale or MotorWheel found: " + ChickenPrep);
            foreach (var x in targets) c.SaveTransform(x.t);
            c.Freeze();
            c.HoldStartCamera();
            var probe = c.AddProbe();
            yield return c.Frames(3);
            foreach (var x in targets)
            {
                x.basePos = x.local ? x.t.localPosition : x.t.position;
                x.baseRot = x.local ? x.t.localRotation : x.t.rotation;
            }
            var right = c.cam.transform.right;
            var culler = c.cam.GetComponent<PDReflectionCuller>();
            var list = culler != null ? culler.frameReflections : null;
            var fv = list != null ? LabUtil.Field(list.GetType(), "_version") : null;
            Func<int> ver = () => { try { return fv != null ? (int)fv.GetValue(list) : -1; } catch { return -1; } };
            int vPost = -1, vDel = -1, firstBeforeDelegate = 0, firstAfterDelegate = 0, firstBeforeProbe = 0, compared = 0;
            int lateBefore = LabHooks.rawLate, late0Before = LabHooks.rawLate0;
            int f0 = Time.frameCount;
            c.traceUntil = f0 + 2;
            c.phase = "write and compare";
            c.onLate = () =>
            {
                int k = Time.frameCount - f0;
                foreach (var x in targets)
                {
                    if (x.t == null) continue;
                    if (x.kind == "chick bone")
                    {
                        x.t.localRotation = x.baseRot * Quaternion.Euler(5f + 10f * Mathf.Sin(0.3f * k), 0f, 0f);
                    }
                    else if (x.kind == "MotorWheel")
                    {
                        x.t.localRotation = x.baseRot * Quaternion.Euler(0f, 0f, 7f * k);
                    }
                    else
                    {
                        x.t.position = x.basePos + right * (0.25f + 0.05f * Mathf.Sin(0.2f * k)) + Vector3.up * 0.1f;
                        x.t.rotation = x.baseRot * Quaternion.Euler(0f, 15f + 5f * Mathf.Sin(0.3f * k), 0f);
                    }
                    x.wantPos = x.local ? x.t.localPosition : x.t.position;
                    x.wantRot = x.local ? x.t.localRotation : x.t.rotation;
                    x.written++;
                }
            };
            c.onPreCull = cam =>
            {
                vDel = ver();
                foreach (var x in targets)
                {
                    if (x.t == null || x.written == 0) continue;
                    double dp, dr;
                    if (Changed(x, out dp, out dr)) { x.preCull++; if (dp > x.maxPos) x.maxPos = dp; if (dr > x.maxRot) x.maxRot = dr; }
                }
            };
            c.onProbePreCull = cam =>
            {
                int v = ver();
                if (vPost >= 0 && v >= 0 && vDel >= 0)
                {
                    compared++;
                    if (vDel != vPost) firstBeforeDelegate++;
                    else if (v != vPost) firstAfterDelegate++;
                    if (v != vPost) firstBeforeProbe++;
                }
                foreach (var x in targets)
                {
                    if (x.t == null || x.written == 0) continue;
                    double dp, dr;
                    if (Changed(x, out dp, out dr)) x.probe++;
                }
            };
            c.onPreRender = cam =>
            {
                foreach (var x in targets)
                {
                    if (x.t == null || x.written == 0) continue;
                    double dp, dr;
                    if (Changed(x, out dp, out dr)) x.preRender++;
                }
            };
            c.onPostRender = cam => { vPost = ver(); };
            c.Trace("coroutine");
            for (int i = 0; i < frames; i++)
            {
                yield return null;
                if (i < 2) c.Trace("coroutine");
            }
            c.onLate = null; c.onPreCull = null; c.onProbePreCull = null; c.onPreRender = null; c.onPostRender = null;
            int n = Time.frameCount - f0;
            c.phase = "report";
            int changedTargets = 0, changesPreCull = 0;
            var rows = new List<object>();
            foreach (var x in targets)
            {
                var o = new Json.Obj();
                o["path"] = LabUtil.ShortPath(x.t); o["kind"] = x.kind; o["space"] = x.local ? "local" : "world";
                o["written"] = x.written; o["changedAtOnPreCull"] = x.preCull; o["changedAtProbeOnPreCull"] = x.probe; o["changedAtOnPreRender"] = x.preRender;
                o["maxPosDelta"] = LabUtil.Num(x.maxPos); o["maxRotDeltaDeg"] = LabUtil.Num(x.maxRot);
                if (x.preCull > 0 || x.preRender > 0) o["writers"] = LabUtil.Strs(LabUtil.Writers(x.t));
                if (x.preCull > 0) changedTargets++;
                changesPreCull += x.preCull;
                rows.Add(o);
            }
            c.result["targets"] = rows;
            var order = new Json.Obj();
            order["trace"] = LabUtil.Strs(c.trace);
            int iDel = IndexOf(c.trace, "cam.onPreCull"), iProbe = IndexOf(c.trace, "probe.OnPreCull");
            string rel = probe == null ? "unknown (component probe unavailable)" : iDel < 0 || iProbe < 0 ? "unknown (not observed)" : iDel < iProbe ? "delegate before the last component" : "delegate after the last component";
            order["onPreCullVsLastComponent"] = rel;
            order["firstComponent"] = culler != null ? "PDReflectionCuller (frameReflections.Clear)" : "PDReflectionCuller missing";
            order["firstComponentVersionField"] = fv != null;
            order["framesCompared"] = compared;
            order["firstComponentBeforeDelegate"] = firstBeforeDelegate;
            order["firstComponentAfterDelegate"] = firstAfterDelegate;
            order["firstComponentBeforeProbe"] = firstBeforeProbe;
            string firstRel = compared == 0 ? "first component order unknown (not compared)" : firstBeforeProbe == 0 ? "first component order unknown (frameReflections version never changed)" : firstBeforeDelegate * 2 > compared ? "first component before the delegate" : firstAfterDelegate * 2 > compared ? "first component after the delegate" : "first component order mixed";
            order["onPreCullVsFirstComponent"] = firstRel;
            order["coroutineYieldNull"] = CoroutinePhase(c.trace, f0 + 1);
            order["lateUpdateCallsPerFrame"] = LabUtil.Num((LabHooks.rawLate - lateBefore) / (double)Math.Max(1, n));
            order["lateUpdate0CallsPerFrame"] = LabUtil.Num((LabHooks.rawLate0 - late0Before) / (double)Math.Max(1, n));
            var comps = new List<object>();
            foreach (var m in c.cam.GetComponents<Behaviour>()) if (m != null) comps.Add(m.GetType().Name + (LabUtil.HasMethod(m.GetType(), "OnPreCull") ? " [OnPreCull]" : ""));
            order["cameraComponents"] = comps;
            c.result["order"] = order;
            c.result["framesObserved"] = n;
            string co = (string)order["coroutineYieldNull"];
            int cut = co.IndexOf(" (", StringComparison.Ordinal);
            string orderText = "onPreCull: " + rel + "; " + firstRel + "; yield null " + (cut > 0 ? co.Substring(0, cut) : co);
            if (changedTargets == 0) c.Pass("0 of " + targets.Count + " transforms changed between lateUpdate -400 and Camera.onPreCull over " + n + " frames; " + orderText);
            else c.Fail(changedTargets + " of " + targets.Count + " transforms changed (" + changesPreCull + " frame changes), writers in result.json; " + orderText);
        }

        static string CoroutinePhase(List<string> trace, int frame)
        {
            string p = frame.ToString(LabUtil.IC) + " ";
            var seq = new List<string>();
            foreach (var t in trace) if (t.StartsWith(p, StringComparison.Ordinal)) seq.Add(t.Substring(p.Length));
            int co = seq.IndexOf("coroutine");
            if (co < 0) return "not observed in frame " + frame;
            string before = co > 0 ? seq[co - 1] : "frame start";
            string after = co + 1 < seq.Count ? seq[co + 1] : "frame end";
            return "resumes after " + before + ", before " + after + " (" + string.Join(" > ", seq.ToArray()) + ")";
        }

        static int IndexOf(List<string> trace, string what)
        {
            for (int i = 0; i < trace.Count; i++) if (trace[i].EndsWith(" " + what, StringComparison.Ordinal)) return i;
            return -1;
        }

        // ---------------------------------------------------------------- L3

        static IEnumerator L3(LabContext c)
        {
            int trials = Math.Max(1, c.I("trials", 6));
            var objs = new List<Transform>();
            var chick = LabUtil.ChickRoot(c);
            if (chick != null && chick.GetComponent<Rigidbody>() != null) objs.Add(chick);
            var bale = LabUtil.Bale(c);
            if (bale != null && bale.GetComponent<Rigidbody>() != null) objs.Add(bale);
            if (objs.Count == 0) c.Prep("no chick or HayBale with a Rigidbody: " + ChickenPrep);
            c.Freeze();
            c.HoldStartCamera();
            foreach (var t in objs) { c.SaveTransform(t); c.SaveRigidbody(t.GetComponent<Rigidbody>()); }
            yield return c.Frames(2);
            var rows = new List<object>();
            bool pass = true;
            var modes = new[] { RigidbodyInterpolation.Interpolate, RigidbodyInterpolation.None };
            foreach (var tr in objs)
            {
                var rb = tr.GetComponent<Rigidbody>();
                var orig = new Json.Obj();
                orig["path"] = LabUtil.ShortPath(tr); orig["interpolation"] = rb.interpolation.ToString(); orig["isKinematic"] = rb.isKinematic; orig["sleeping"] = rb.IsSleeping();
                rows.Add(orig);
                Vector3 bp = tr.position; Quaternion br = tr.rotation;
                foreach (var ip in modes)
                {
                    foreach (bool kin in new[] { false, true })
                    {
                        c.phase = tr.name + " " + ip + (kin ? " kinematic" : " dynamic");
                        rb.interpolation = ip;
                        rb.isKinematic = kin;
                        yield return c.Frames(2);
                        var same = new List<double>(); var next = new List<double>(); var nextRot = new List<double>();
                        for (int k = 0; k < trials; k++)
                        {
                            Vector3 wp = bp + new Vector3(0.05f * (k + 1), 0.03f, 0f);
                            Quaternion wr = br * Quaternion.Euler(0f, 4f * (k + 1), 0f);
                            int fw = -1;
                            double ds = double.NaN, dn = double.NaN, dnr = double.NaN;
                            c.onLate = () => { if (fw < 0) { tr.position = wp; tr.rotation = wr; wp = tr.position; wr = tr.rotation; fw = Time.frameCount; } };
                            c.onPreCull = cam =>
                            {
                                if (fw < 0) return;
                                if (Time.frameCount == fw) ds = LabUtil.PosDelta(tr.position, wp);
                                else if (Time.frameCount == fw + 1) { dn = LabUtil.PosDelta(tr.position, wp); dnr = LabUtil.RotDelta(tr.rotation, wr); }
                            };
                            for (int g = 0; g < 10 && (fw < 0 || double.IsNaN(dn)); g++) yield return c.eof;
                            c.onLate = null; c.onPreCull = null;
                            same.Add(ds); next.Add(dn); nextRot.Add(dnr);
                            tr.position = bp; tr.rotation = br;
                            if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                            yield return null;
                        }
                        var o = new Json.Obj();
                        o["path"] = LabUtil.ShortPath(tr); o["interpolation"] = ip.ToString(); o["isKinematic"] = kin;
                        o["sameFrameMaxPos"] = LabUtil.Num(LabUtil.Max(same)); o["nextFrameMaxPos"] = LabUtil.Num(LabUtil.Max(next)); o["nextFrameMaxRotDeg"] = LabUtil.Num(LabUtil.Max(nextRot));
                        bool persists = LabUtil.Max(next) < 1e-5 && LabUtil.Max(nextRot) < 1e-3;
                        o["persists"] = persists;
                        rows.Add(o);
                        if (ip == RigidbodyInterpolation.None && kin && !persists) pass = false;
                        if (double.IsNaN(LabUtil.Max(next))) pass = false;
                    }
                }
            }
            c.result["combinations"] = rows;
            if (chick != null)
            {
                c.phase = "animated bone";
                var bones = LabUtil.ChickBones(chick);
                var bone = bones.Count > 0 ? bones[0] : null;
                var anim = chick.GetComponentInChildren<Animation>();
                var bo = new Json.Obj();
                bo["animation"] = anim != null ? (anim.enabled ? "enabled" : "disabled") + (anim.isPlaying ? ", playing" : ", not playing") + ", culling " + anim.cullingType : "none";
                if (bone != null)
                {
                    c.SaveTransform(bone);
                    var lr = bone.localRotation;
                    Quaternion want = lr * Quaternion.Euler(20f, 0f, 0f);
                    int fw = -1;
                    double same = double.NaN, next = double.NaN;
                    c.onLate = () => { if (fw < 0) { bone.localRotation = want; want = bone.localRotation; fw = Time.frameCount; } };
                    c.onPreCull = cam =>
                    {
                        if (fw < 0) return;
                        if (Time.frameCount == fw) same = LabUtil.RotDelta(bone.localRotation, want);
                        else if (Time.frameCount == fw + 1) next = LabUtil.RotDelta(bone.localRotation, want);
                    };
                    for (int g = 0; g < 10 && (fw < 0 || double.IsNaN(next)); g++) yield return c.eof;
                    c.onLate = null; c.onPreCull = null;
                    bone.localRotation = lr;
                    bo["bone"] = bone.name; bo["sameFrameRotDeg"] = LabUtil.Num(same); bo["nextFrameRotDeg"] = LabUtil.Num(next);
                    bo["persists"] = next < 1e-3;
                }
                c.result["animatedBone"] = bo;
            }
            c.Verdict(pass, pass ? "interpolation None + kinematic: written poses persist to the next onPreCull (< 1e-5 m) for " + objs.Count + " bodies; other combinations in result.json"
                                 : "interpolation None + kinematic: a written pose did not persist to the next onPreCull; see result.json");
        }

        // ---------------------------------------------------------------- L6

        static IEnumerator L6(LabContext c)
        {
            yield return null;
            int s0 = URandom.seed, s0b = URandom.seed;
            float v1 = URandom.value;
            int s1 = URandom.seed;
            URandom.seed = s0;
            int sSet = URandom.seed;
            float v2 = URandom.value;
            int s2 = URandom.seed;
            URandom.seed = s0;
            float a1 = URandom.value;
            URandom.seed = s0;
            int r1 = URandom.seed, r2 = URandom.seed, r3 = URandom.seed;
            float a2 = URandom.value;
            URandom.seed = s0;
            bool readConsumes = BitConverter.ToInt32(BitConverter.GetBytes(a1), 0) != BitConverter.ToInt32(BitConverter.GetBytes(a2), 0);
            bool drawsEqual = BitConverter.ToInt32(BitConverter.GetBytes(v1), 0) == BitConverter.ToInt32(BitConverter.GetBytes(v2), 0);
            bool tracksState = s1 != s0;
            var o = c.result;
            o["seedRead1"] = s0; o["seedRead2"] = s0b; o["drawV1"] = LabUtil.Num(v1); o["seedAfterDraw"] = s1;
            o["seedAfterSetBack"] = sSet; o["drawV2"] = LabUtil.Num(v2); o["seedAfterSecondDraw"] = s2;
            o["readTestDrawA"] = LabUtil.Num(a1); o["readTestDrawB"] = LabUtil.Num(a2); o["readTestSeeds"] = new List<object> { r1, r2, r3 };
            o["readConsumes"] = readConsumes; o["drawsEqualAfterSetBack"] = drawsEqual; o["seedChangesOnDraw"] = tracksState;
            o["restored"] = "Random.seed = " + s0 + (drawsEqual ? " (set back reproduces the stream: exact restore)" : " (the setter re-initialises the generator from the value; the game's original stream is NOT restored, Unity 5.0 has no Random.state)");
            string fp = tracksState ? "seed changes on every draw, usable as a read-only fingerprint" : "seed does not change on draws, NOT usable as a fingerprint";
            c.Verdict(!readConsumes && drawsEqual, (readConsumes ? "reading Random.seed consumes" : "reading Random.seed does not consume") + "; " + (drawsEqual ? "draws equal after set back" : "draws differ after set back (setter re-seeds, getter is not the full state)") + "; " + fp);
        }

        // ---------------------------------------------------------------- L8

        static IEnumerator L8(LabContext c)
        {
            int frames = Math.Max(10, c.I("frames", 120));
            var inst = UObj.FindObjectsOfType<RuntimeCallbacks>();
            var paths = new List<string>();
            foreach (var r in inst) if (r != null) paths.Add(LabUtil.PathOf(r.transform) + (r.enabled ? "" : " (disabled)"));
            int allCount = Resources.FindObjectsOfTypeAll(typeof(RuntimeCallbacks)).Length;
            var raw = new int[4];
            var work = new int[4];
            var last = new int[4] { -1, -1, -1, -1 };
            var perFrame = new Dictionary<int, int[]>();
            long lastStep = -1; int fixedRaw = 0, fixedSteps = 0, fixedWork = 0;
            Action<int> hit = k =>
            {
                raw[k]++;
                int f = Time.frameCount;
                int[] row;
                if (!perFrame.TryGetValue(f, out row)) { row = new int[4]; perFrame[f] = row; }
                row[k]++;
                if (last[k] == f) return;
                last[k] = f;
                work[k]++;
            };
            Action hU = () => hit(0), hL = () => hit(1), hE = () => hit(3);
            Action hF = () =>
            {
                hit(2);
                fixedRaw++;
                long s = LogicManager.stepCount;
                if (s == lastStep) return;
                lastStep = s;
                fixedWork++;
            };
            RuntimeCallbacks.update += hU;
            RuntimeCallbacks.lateUpdate += hL;
            RuntimeCallbacks.fixedUpdate += hF;
            RuntimeCallbacks.ExecuteInEditModeIncluded.update += hE;
            c.Undo("l8 handlers", () =>
            {
                RuntimeCallbacks.update -= hU;
                RuntimeCallbacks.lateUpdate -= hL;
                RuntimeCallbacks.fixedUpdate -= hF;
                RuntimeCallbacks.ExecuteInEditModeIncluded.update -= hE;
            });
            long step0 = LogicManager.stepCount;
            c.phase = "counting";
            for (int i = 0; i < frames; i++) yield return null;
            fixedSteps = (int)(LogicManager.stepCount - step0);
            var names = new[] { "update", "lateUpdate", "fixedUpdate", "ExecuteInEditModeIncluded.update" };
            var stats = new Json.Obj();
            bool ok = true;
            for (int k = 0; k < 4; k++)
            {
                int seen = 0, mn = int.MaxValue, mx = 0;
                foreach (var kv in perFrame) if (kv.Value[k] > 0) { seen++; mn = Math.Min(mn, kv.Value[k]); mx = Math.Max(mx, kv.Value[k]); }
                var o = new Json.Obj();
                o["rawCalls"] = raw[k]; o["framesWithCalls"] = seen; o["guardedWork"] = work[k];
                o["callsPerFrameMin"] = seen > 0 ? mn : 0; o["callsPerFrameMax"] = mx; o["callsPerFrameMean"] = LabUtil.Num(seen > 0 ? raw[k] / (double)seen : 0);
                o["guardOncePerFrame"] = work[k] == seen;
                stats[names[k]] = o;
                if (k != 2 && (work[k] != seen || seen < frames - 2)) ok = false;
            }
            var fx = new Json.Obj();
            fx["rawCalls"] = fixedRaw; fx["logicSteps"] = fixedSteps; fx["guardedPerStep"] = fixedWork;
            fx["callsPerStep"] = LabUtil.Num(fixedSteps > 0 ? fixedRaw / (double)fixedSteps : 0);
            stats["fixedUpdatePerLogicStep"] = fx;
            c.result["instances"] = LabUtil.Strs(paths);
            c.result["instancesIncludingInactiveAndAssets"] = allCount;
            c.result["counts"] = stats;
            c.result["timeScale"] = LabUtil.Num(Time.timeScale);
            var lu = (Json.Obj)stats["lateUpdate"];
            c.Verdict(ok, inst.Length + " RuntimeCallbacks instance(s) (" + string.Join(", ", paths.ToArray()) + "); lateUpdate " + lu["callsPerFrameMean"] + " call(s)/frame; frame guard " + (ok ? "keeps work to once per frame" : "FAILED"));
        }
    }
}
