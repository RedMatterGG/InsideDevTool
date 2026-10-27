// ReplayLab culling tests: L7 forced bounds cullers and the per-camera visibility emulation without SetActive,
// L12 camera probe (transforms that change when only the camera moves).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev.ReplayLab
{
    public static class LabCull
    {
        public static void Register(List<LabTest> t)
        {
            t.Add(new LabTest
            {
                id = "L7", title = "forced cullers (valid=false) and the emulation UpdateBoundsCullers(c => false) + hide by IsVisble",
                usage = "lab l7 [cullers=3_Interior_CULL,1b_IndoorDetails_CULL] [frames=240] [period=120] [dist=4] [in=x,y,z,yaw,pitch[,fov]] [out=...] [zone=<root prefix>|all]",
                prep = "ChickenGun loaded (#chickenGun_Environment); groups inactive at the start (camera outside their facing planes; the test places the camera itself unless in=/out= are given)",
                body = L7
            });
            t.Add(new LabTest
            {
                id = "L12", title = "camera probe: transforms that change when only the camera moves (WaterDust, LensFlareSimple)",
                usage = "lab l12 [tag=<scene name>] [frames=30] [dx=1.5] [yaw=10] [eps=1e-5] [cam=...]",
                prep = "run once in ChickenGun (tag=chickengun) and once in a water scene with WaterDust (tag=water); results in L12_<tag>",
                body = L12,
                name = c => "L12" + (c.Arg("tag", null) != null ? "_" + c.Arg("tag", null) : "")
            });
        }

        // ---------------------------------------------------------------- L7

        sealed class Group
        {
            public BoundsCullerRuntime b; public string path; public bool validBefore;
        }

        sealed class Hidden
        {
            public Renderer r; public Light l; public bool enabled; public int[] cullers;
        }

        static LabPose Lerp(LabPose a, LabPose b, float t) { return new LabPose { p = Vector3.Lerp(a.p, b.p, t), r = Quaternion.Slerp(a.r, b.r, t), fov = Mathf.Lerp(a.fov, b.fov, t) }; }

        static IEnumerator L7(LabContext c)
        {
            var names = c.List("cullers");
            if (names.Count == 0) names = new List<string> { "3_Interior_CULL", "1b_IndoorDetails_CULL" };
            var forced = new List<BoundsCullerRuntime>();
            var missing = new List<string>();
            foreach (var n in names) { var b = LabUtil.Culler(n); if (b != null) forced.Add(b); else missing.Add(n); }
            if (forced.Count == 0) c.Prep("cullers not found: " + string.Join(", ", names.ToArray()) + " (load ChickenGun)");
            if (missing.Count > 0) c.result["missingCullers"] = LabUtil.Strs(missing);
            int frames = Math.Max(20, c.I("frames", 240)), period = Math.Max(10, c.I("period", 120));
            float dist = c.F("dist", 4f);
            c.Freeze();
            c.CleanFrames();
            var b0 = forced[0];
            float fov = c.cam.fieldOfView;
            LabPose pin, pout;
            Vector3 target = b0.aabb.center;
            if (b0.points != null && b0.points.Length > 0)
            {
                var sum = Vector3.zero;
                foreach (var q in b0.points) sum += q;
                target = sum / b0.points.Length;
            }
            if (b0.planes != null && b0.planes.Count > 0)
            {
                var pl = b0.planes[0];
                var pw = b0.transform.position + pl.localPosition;
                var n = pl.normal.normalized;
                pin = LabUtil.LookAt(pw + n * dist + Vector3.up, target, fov);
                pout = LabUtil.LookAt(pw - n * dist + Vector3.up, pw + n * dist, fov);
            }
            else
            {
                var from = target - c.cam.transform.forward * Math.Max(dist, b0.aabb.extents.magnitude * 1.5f);
                pin = LabUtil.LookAt(from, target, fov);
                pout = pin; pout.r = Quaternion.AngleAxis(180f, Vector3.up) * pin.r;
            }
            LabPose tmp;
            if (LabUtil.TryPose(c.Arg("in", null), out tmp)) { if (tmp.fov <= 0) tmp.fov = fov; pin = tmp; }
            if (LabUtil.TryPose(c.Arg("out", null), out tmp)) { if (tmp.fov <= 0) tmp.fov = fov; pout = tmp; }
            c.result["poseIn"] = pin.ToJson(); c.result["poseOut"] = pout.ToJson();
            c.HoldCamera(pout);
            yield return c.Render(4);
            bool hooked = LabUtil.DelegateHas(typeof(RuntimeCallbacks.ExecuteInEditModeIncluded), "update", typeof(BoundsCullerRuntime), "UpdateBoundsCullers");
            c.result["updateBoundsCullersHookedAtStart"] = hooked;
            var groups = new Dictionary<BoundsCullerRuntime, Group>();
            Func<BoundsCullerRuntime, Group> touch = b =>
            {
                Group g;
                if (groups.TryGetValue(b, out g)) return g;
                g = new Group { b = b, path = LabUtil.ShortPath(b.transform), validBefore = b.valid };
                groups[b] = g;
                var bb = b; bool v = b.valid;
                c.Undo("culler " + b.name, () => { if (bb == null) return; bb.valid = v; bb.RefreshRuntimeState(); });
                return g;
            };
            int phaseIdx = 0;
            var setActive = new int[4];
            var setActiveNames = new List<string>();
            Action<BoundsCullerRuntime, bool> onSet = (b, vis) =>
            {
                setActive[phaseIdx]++;
                if (phaseIdx == 2 && setActiveNames.Count < 20) setActiveNames.Add((b != null ? b.name : "?") + "=" + vis);
            };
            BoundsCullerRuntime.staticSetVisible += onSet;
            c.Undo("staticSetVisible counter", () => BoundsCullerRuntime.staticSetVisible -= onSet);
            c.Undo("UpdateBoundsCullers hook", () =>
            {
                RuntimeCallbacks.ExecuteInEditModeIncluded.update -= BoundsCullerRuntime.UpdateBoundsCullers;
                if (hooked) RuntimeCallbacks.ExecuteInEditModeIncluded.update += BoundsCullerRuntime.UpdateBoundsCullers;
            });

            c.phase = "1 force";
            phaseIdx = 1;
            var p1 = new List<object>();
            var before = new Dictionary<BoundsCullerRuntime, bool>();
            foreach (var b in forced) before[b] = b.gameObject.activeSelf;
            int frame0 = Time.frameCount;
            long ev0 = EventMonitor.total;
            foreach (var b in forced) { touch(b); b.valid = false; b.RefreshRuntimeState(); }
            yield return c.Render(3);
            var rendered = new Dictionary<BoundsCullerRuntime, int>();
            foreach (var b in forced)
            {
                int n = 0;
                foreach (var r in b.GetComponentsInChildren<Renderer>(true)) if (r != null && r.isVisible) n++;
                rendered[b] = n;
            }
            c.SaveGrab(LabImage.Grab(RectI.Screen(), null), "forced_out.jpg", "groups forced, camera outside");
            yield return c.Frames(10);
            bool p1Ok = true;
            int postsGroup = 0, signalsGroup = 0, fsmGroup = 0, postsAll = 0;
            foreach (var b in forced)
            {
                var ids = LabUtil.GoIds(b.gameObject);
                var gn = LabUtil.GoNames(b.gameObject);
                int posts = 0, sig = 0, fsm = 0;
                var postNames = new List<string>();
                foreach (var e in AudioSpy.log)
                {
                    if (e.frame < frame0 || e.kind != "post") continue;
                    if (gn.Contains(e.source)) { posts++; if (postNames.Count < 10) postNames.Add(e.name + " @" + e.source); }
                }
                long newEv = Math.Min(EventMonitor.Count, EventMonitor.total - ev0);
                for (int i = 0; i < newEv; i++)
                {
                    var ev = EventMonitor.Get(i);
                    if (!ids.Contains(ev.goId)) continue;
                    if (ev.kind == 'S') sig++;
                    else if (ev.kind == 'F') fsm++;
                }
                bool active = b.gameObject.activeInHierarchy;
                bool hasR = b.GetComponentsInChildren<Renderer>(true).Length > 0;
                var o = new Json.Obj();
                o["culler"] = LabUtil.ShortPath(b.transform); o["activeBefore"] = before[b]; o["activeAfter"] = active;
                o["renderersVisible"] = rendered[b]; o["hasRenderers"] = hasR;
                o["onEnableTypes"] = LabUtil.Strs(LabUtil.OnEnableTypes(b.gameObject));
                o["soundPostsFromGroup"] = posts; o["soundPosts"] = LabUtil.Strs(postNames); o["signalOutFires"] = sig; o["fsmStateChanges"] = fsm;
                p1.Add(o);
                postsGroup += posts; signalsGroup += sig; fsmGroup += fsm;
                if (!active || (hasR && rendered[b] == 0)) p1Ok = false;
            }
            foreach (var e in AudioSpy.log) if (e.frame >= frame0 && e.kind == "post") postsAll++;
            c.result["forced"] = p1;
            c.result["phase1SetActiveCalls"] = setActive[1];
            c.result["soundPostsAllSources"] = postsAll;
            c.result["eventMonitorTaps"] = EventMonitor.TapCount;

            c.phase = "2 emulation";
            string prefix = c.Arg("zone", LabUtil.ZonePrefix(b0.transform));
            var zone = new List<BoundsCullerRuntime>();
            if (BoundsCullerRuntime.boundsCullers != null)
                foreach (var b in BoundsCullerRuntime.boundsCullers)
                    if (b != null && (prefix == "all" || b.transform.root.name.StartsWith(prefix, StringComparison.Ordinal)) && !zone.Contains(b)) zone.Add(b);
            foreach (var b in forced) if (!zone.Contains(b)) zone.Add(b);
            foreach (var b in zone) { touch(b); if (b.valid) { b.valid = false; b.RefreshRuntimeState(); } }
            yield return c.Frames(3);
            var zi = new Dictionary<BoundsCullerRuntime, int>();
            for (int i = 0; i < zone.Count; i++) zi[zone[i]] = i;
            var hidden = new List<Hidden>();
            Func<Transform, int[]> chain = t =>
            {
                var l = new List<int>();
                for (var x = t; x != null; x = x.parent) { var bc = x.GetComponent<BoundsCullerRuntime>(); int k; if (bc != null && zi.TryGetValue(bc, out k)) l.Add(k); }
                return l.ToArray();
            };
            var seenR = new HashSet<Renderer>();
            var seenL = new HashSet<Light>();
            foreach (var b in zone)
            {
                foreach (var r in b.GetComponentsInChildren<Renderer>(true)) if (r != null && seenR.Add(r)) { hidden.Add(new Hidden { r = r, enabled = r.enabled, cullers = chain(r.transform) }); c.SaveRenderer(r); }
                foreach (var l in b.GetComponentsInChildren<Light>(true)) if (l != null && seenL.Add(l)) { hidden.Add(new Hidden { l = l, enabled = l.enabled, cullers = chain(l.transform) }); c.SaveEnabled(l); }
            }
            c.result["zonePrefix"] = prefix; c.result["zoneCullers"] = zone.Count; c.result["hiddenItems"] = hidden.Count;
            RuntimeCallbacks.ExecuteInEditModeIncluded.update -= BoundsCullerRuntime.UpdateBoundsCullers;
            c.onUpdate = () => { RuntimeCallbacks.ExecuteInEditModeIncluded.update -= BoundsCullerRuntime.UpdateBoundsCullers; };
            phaseIdx = 2;
            var emu = new bool[frames, zone.Count];
            var emuDone = new bool[frames];
            int k0 = -1;
            Func<BoundsCullerRuntime, bool> never = x => false;
            c.onPreCull = cam =>
            {
                int k = k0;
                if (k < 0 || k >= frames) return;
                BoundsCullerRuntime.UpdateBoundsCullers(never);
                for (int i = 0; i < zone.Count; i++) emu[k, i] = zone[i] != null && zone[i].IsVisble();
                foreach (var h in hidden)
                {
                    bool vis = h.enabled;
                    foreach (var ci in h.cullers) if (!emu[k, ci]) { vis = false; break; }
                    if (h.r != null) { if (h.r.enabled != vis) h.r.enabled = vis; }
                    else if (h.l != null && h.l.enabled != vis) h.l.enabled = vis;
                }
                emuDone[k] = true;
            };
            for (int k = 0; k < frames; k++)
            {
                c.HoldCamera(Lerp(pout, pin, 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * k / period)));
                k0 = k;
                yield return c.eof;
                if (k == period / 2) c.SaveGrab(LabImage.Grab(RectI.Screen(), null), "emulated_in.jpg", "emulation, camera inside");
                if (k == 0) c.SaveGrab(LabImage.Grab(RectI.Screen(), null), "emulated_out.jpg", "emulation, camera outside");
            }
            c.onPreCull = null;
            k0 = -1;
            foreach (var h in hidden)
            {
                if (h.r != null) { if (h.r.enabled != h.enabled) h.r.enabled = h.enabled; }
                else if (h.l != null && h.l.enabled != h.enabled) h.l.enabled = h.enabled;
            }
            int emuSetActive = setActive[2];

            c.phase = "3 live reference";
            phaseIdx = 3;
            c.onUpdate = null;
            foreach (var g in groups.Values) if (g.b != null) { g.b.valid = g.validBefore; g.b.RefreshRuntimeState(); }
            if (hooked && !LabUtil.DelegateHas(typeof(RuntimeCallbacks.ExecuteInEditModeIncluded), "update", typeof(BoundsCullerRuntime), "UpdateBoundsCullers"))
                RuntimeCallbacks.ExecuteInEditModeIncluded.update += BoundsCullerRuntime.UpdateBoundsCullers;
            var live = new bool[frames, zone.Count];
            var liveDone = new bool[frames];
            c.onPreCull = cam =>
            {
                int k = k0;
                if (k < 0 || k >= frames) return;
                for (int i = 0; i < zone.Count; i++) live[k, i] = zone[i] != null && zone[i].previousVisible;
                liveDone[k] = true;
            };
            c.HoldCamera(pout);
            yield return c.Render(3);
            for (int k = 0; k < frames; k++)
            {
                c.HoldCamera(Lerp(pout, pin, 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * k / period)));
                k0 = k;
                yield return c.eof;
            }
            c.onPreCull = null;
            k0 = -1;
            int cmp = 0, same = 0;
            var mism = new List<string>();
            var perCuller = new int[zone.Count];
            for (int k = 2; k < frames; k++)
            {
                if (!emuDone[k] || !liveDone[k]) continue;
                for (int i = 0; i < zone.Count; i++)
                {
                    if (zone[i] == null || !groups[zone[i]].validBefore) continue;
                    cmp++;
                    if (emu[k, i] == live[k, i]) same++;
                    else { perCuller[i]++; if (mism.Count < 30) mism.Add("frame " + k + " " + zone[i].name + " emulated " + emu[k, i] + " live " + live[k, i]); }
                }
            }
            double rate = cmp > 0 ? same / (double)cmp : double.NaN;
            var pc = new Json.Obj();
            for (int i = 0; i < zone.Count; i++) if (perCuller[i] > 0 && zone[i] != null) pc[zone[i].name] = perCuller[i];
            c.result["emulationSetActiveCalls"] = emuSetActive;
            c.result["liveSetActiveCalls"] = setActive[3];
            c.result["emulationSetActiveNames"] = LabUtil.Strs(setActiveNames);
            c.result["comparisons"] = cmp; c.result["matchRate"] = LabUtil.Num(rate);
            c.result["mismatchesPerCuller"] = pc; c.result["mismatches"] = LabUtil.Strs(mism);
            bool sideOk = postsGroup == 0 && signalsGroup == 0;
            bool emuOk = emuSetActive == 0;
            bool matchOk = !double.IsNaN(rate) && rate >= 0.99;
            string s = "forced groups active+rendered " + p1Ok + "; group sound posts " + postsGroup + ", SignalOut fires " + signalsGroup + ", FSM changes " + fsmGroup +
                       "; emulation SetActive calls " + emuSetActive + "; IsVisble matches live " + LabUtil.F(rate * 100.0, "0.0") + " % of " + cmp;
            c.Verdict(p1Ok && sideOk && emuOk && matchOk, s);
        }

        // ---------------------------------------------------------------- L12

        static IEnumerator L12(LabContext c)
        {
            int n = Math.Max(4, c.I("frames", 30));
            float dx = c.F("dx", 1.5f), dyaw = c.F("yaw", 10f), eps = c.F("eps", 1e-5f);
            c.Freeze();
            var a = c.StartPose();
            c.HoldCamera(a);
            c.result["camera"] = a.ToJson();
            c.CleanFrames();
            yield return c.Render(3);
            var camT = c.cam.transform;
            var list = new List<Transform>();
            foreach (var t in Inspector.AllSceneTransforms())
            {
                if (t == null || t == camT || t.IsChildOf(camT)) continue;
                if (t.root.name.StartsWith("InsideDev", StringComparison.Ordinal)) continue;
                list.Add(t);
            }
            int count = list.Count;
            var pos = new Vector3[count]; var rot = new Quaternion[count]; var scl = new Vector3[count];
            for (int i = 0; i < count; i++) { var t = list[i]; if (t == null) continue; pos[i] = t.position; rot[i] = t.rotation; scl[i] = t.localScale; }
            var control = new HashSet<int>();
            var moved = new HashSet<int>();
            Action<HashSet<int>> scan = set =>
            {
                for (int i = 0; i < count; i++)
                {
                    var t = list[i];
                    if (t == null) continue;
                    var p = t.position; var r = t.rotation; var ls = t.localScale;
                    if ((p - pos[i]).sqrMagnitude > eps * eps || LabUtil.RotDelta(r, rot[i]) > 1e-3 || (ls - scl[i]).sqrMagnitude > eps * eps) set.Add(i);
                    pos[i] = p; rot[i] = r; scl[i] = ls;
                }
            };
            c.phase = "control (camera still)";
            for (int f = 0; f < n; f++) { yield return c.eof; scan(control); }
            c.phase = "camera moving";
            var b = new LabPose { p = a.p + camT.right * dx, r = Quaternion.AngleAxis(dyaw, Vector3.up) * a.r, fov = a.fov };
            for (int f = 0; f < n; f++)
            {
                c.HoldCamera(f % 2 == 0 ? b : a);
                yield return c.eof;
                scan(moved);
            }
            c.HoldCamera(a);
            yield return c.Render(2);
            var expected = new HashSet<Transform>();
            var dusts = new List<Transform>();
            var flares = new List<Transform>();
            foreach (var w in UObj.FindObjectsOfType<WaterDust>())
            {
                if (w == null || w.dustObject == null) continue;
                var dt = w.dustObject.transform;
                expected.Add(dt);
                if (dt.parent != null) expected.Add(dt.parent);
                if (w.enabled && dt.gameObject.activeInHierarchy) dusts.Add(dt);
            }
            foreach (var lf in UObj.FindObjectsOfType<LensFlareSimple>())
            {
                if (lf == null) continue;
                foreach (var t in lf.GetComponentsInChildren<Transform>(true)) expected.Add(t);
                if (lf.enabled && lf.gameObject.activeInHierarchy) flares.Add(lf.transform);
            }
            var probes = new List<Transform>();
            foreach (var ap in UObj.FindObjectsOfType<AudioCameraProbe>())
            {
                if (ap == null) continue;
                foreach (var t in ap.GetComponentsInChildren<Transform>(true)) expected.Add(t);
                probes.Add(ap.transform);
            }
            var camDriven = new List<int>();
            foreach (var i in moved) if (!control.Contains(i)) camDriven.Add(i);
            camDriven.Sort((x, y) => string.CompareOrdinal(LabUtil.PathOf(list[x]), LabUtil.PathOf(list[y])));
            var tsv = new StringBuilder("path\tcomponents\texpected\trigid\n");
            var rows = new List<object>();
            var rigid = new List<string>();
            var listed = new HashSet<Transform>();
            foreach (var i in camDriven)
            {
                var t = list[i];
                if (t == null) continue;
                listed.Add(t);
                var comps = new List<string>();
                foreach (var cp in t.GetComponents<Component>()) if (cp != null && !(cp is Transform)) comps.Add(cp.GetType().Name);
                bool exp = expected.Contains(t);
                bool rg = t.GetComponent<Rigidbody>() != null || t.GetComponent<Collider>() != null;
                string path = LabUtil.PathOf(t);
                tsv.Append(path).Append('\t').Append(string.Join(",", comps.ToArray())).Append('\t').Append(exp ? "1" : "0").Append('\t').Append(rg && !exp ? "1" : "0").Append('\n');
                if (rg && !exp) rigid.Add(path);
                if (rows.Count < 200) rows.Add(path + "  [" + string.Join(",", comps.ToArray()) + "]" + (exp ? " expected" : "") + (rg && !exp ? " RIGID" : ""));
            }
            try { File.WriteAllText(c.File("camera_driven.tsv"), tsv.ToString()); } catch (Exception e) { c.Log("camera_driven.tsv: " + e.Message); }
            var missing = new List<string>();
            var notRendered = new List<string>();
            foreach (var t in dusts)
            {
                if (listed.Contains(t)) continue;
                if (Rendered(t)) missing.Add("WaterDust dust " + LabUtil.PathOf(t)); else notRendered.Add("WaterDust dust " + LabUtil.PathOf(t));
            }
            foreach (var t in flares)
            {
                bool any = false;
                foreach (var x in t.GetComponentsInChildren<Transform>(true)) if (listed.Contains(x)) { any = true; break; }
                if (any) continue;
                if (Rendered(t)) missing.Add("LensFlareSimple " + LabUtil.PathOf(t)); else notRendered.Add("LensFlareSimple " + LabUtil.PathOf(t));
            }
            c.result["expectedNotRendered"] = LabUtil.Strs(notRendered);
            var ctrl = new List<string>();
            foreach (var i in control) if (list[i] != null && ctrl.Count < 100) ctrl.Add(LabUtil.PathOf(list[i]));
            c.result["transformsScanned"] = count;
            c.result["changedWithoutCameraMotion"] = control.Count;
            c.result["changedWithoutCameraMotionSample"] = LabUtil.Strs(ctrl);
            c.result["cameraDriven"] = camDriven.Count;
            c.result["cameraDrivenList"] = rows;
            c.result["waterDustActive"] = dusts.Count; c.result["lensFlaresActive"] = flares.Count; c.result["audioCameraProbes"] = probes.Count;
            c.result["missingExpected"] = LabUtil.Strs(missing);
            c.result["rigidListed"] = LabUtil.Strs(rigid);
            string s = camDriven.Count + " camera-driven transforms (" + dusts.Count + " WaterDust, " + flares.Count + " LensFlareSimple active, " + missing.Count + " of them not listed), " + rigid.Count + " rigid gameplay objects listed; camera_driven.tsv written";
            c.Verdict(missing.Count == 0 && rigid.Count == 0, s);
        }

        static bool Rendered(Transform t)
        {
            foreach (var r in t.GetComponentsInChildren<Renderer>(true)) if (r != null && r.isVisible) return true;
            return false;
        }
    }
}
