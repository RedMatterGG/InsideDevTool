using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace InsideDev.Play
{
    // CamPath.cs (owner TIME): a live director pass. A replay plan (mcam passes <spec> <out>) writes poses.csv (every camera's
    // pose per output step) and meta.csv (step, game time T, speed) without rendering; campath plays one camera's poses on the
    // real game camera while the game itself runs, so every frame is the game's own picture (live particles, _Time shaders,
    // mod effects, TAA). Clock: output time advances with the wall clock from the first logic step of an input_track playback
    // (campath arm) or at once (campath start); the game is driven toward the plan's T: frozen while the plan's speed is under
    // the stepper's minimum (paused tours), else realtime at the plan's speed with a small correction of the drift.
    // The pose goes on the main camera in Camera.onPreCull, after CameraScript, so only the rendered view changes; the game
    // camera logic, its culling probe and the input are untouched. Commands: campath load <dir> [camera] [offset_s],
    // campath arm | start | stop | status | mount (the camera rides a transform, e.g. the boy's head for a first-person pass). Restore stops it.
    public static class CamPath
    {
        public const string RestoreKey = "campath";
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly Stopwatch wall = new Stopwatch();

        static Vector3[] pos = new Vector3[0];
        static Quaternion[] rot = new Quaternion[0];
        static float[] fov = new float[0], near = new float[0];
        static double[] gameT = new double[0], speed = new double[0];
        static int count;
        static double fps = 30, offset, gameT0;
        static string loaded = "", cameraName = "director";
        static bool armed, running, hooked, frozenByUs, realtime;
        static float fovBefore = -1f, nearBefore = -1f;
        static double lastSpeed = -1;
        static string mountPath = "";
        static Transform mount;
        static Vector3 mountLocal;
        static Quaternion mountRot = Quaternion.identity;
        static float mountFov = 60f, mountNear = 0.03f, mountSmooth, mountIntoScene, mountLevel;
        static bool mountHas, mountHide;
        static Vector3 mountPos;
        static Quaternion mountCur;
        static float tauH, tauV, tauYaw, tauPitch, pitchMax = 89f, camYaw, camPitch;
        static Transform hidden;
        static Vector3 hiddenScale = Vector3.one;

        class Look
        {
            public double t0, t1, ease;
            public Vector3 point;
            public string path = "";
            public Transform target;
        }

        static readonly List<Look> looks = new List<Look>();

        public static bool Pausing { get { return running && frozenByUs; } }

        public static void Register()
        {
            PlayRegistry.OnTick("campath", Tick);
        }

        public static string Command(List<string> a)
        {
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            if (sub == "load")
            {
                if (a.Count < 3) return "campath load <dir with poses.csv and meta.csv> [camera] [offset_s] [rt]";
                realtime = a.Count > 5 && a[5].ToLowerInvariant() == "rt";
                cameraName = a.Count > 3 ? a[3] : "director";
                offset = 0;
                if (a.Count > 4) double.TryParse(a[4], NumberStyles.Float, IC, out offset);
                try { Load(a[2]); }
                catch (Exception e) { return "campath: " + e.Message; }
                return "campath: " + count + " pose(s) of " + cameraName + " from " + loaded + (realtime ? " (realtime: no pauses or slow motion, pose by game time)" : "") + ", " + (count / fps).ToString("0.0", IC) + " s output, T " +
                       gameT[0].ToString("0.###", IC) + ".." + gameT[count - 1].ToString("0.###", IC) + ", offset " + offset.ToString("0.###", IC);
            }
            if (sub == "arm") { if (count == 0) return "campath: nothing loaded"; armed = true; running = false; return "campath: armed, starts with the next input_track playback step"; }
            if (sub == "start") { if (count == 0) return "campath: nothing loaded"; Begin(); return "campath: running"; }
            if (sub == "stop") return Stop("stop");
            if (sub == "mount") return Mount(a);
            if (sub == "look") return LookCommand(a);
            return Status();
        }

        static string Status()
        {
            if (!running) return "campath: " + (armed ? "armed" : "idle") + ", " + count + " pose(s) of " + cameraName + (loaded.Length > 0 ? " from " + loaded : "");
            double tau = wall.Elapsed.TotalSeconds;
            return "campath: running " + tau.ToString("0.00", IC) + " / " + (count / fps).ToString("0.00", IC) + " s, game " + (Time.timeDbl - gameT0).ToString("0.000", IC) +
                   " (plan " + PlanT(tau * fps).ToString("0.000", IC) + "), " + (frozenByUs ? "paused" : "speed " + lastSpeed.ToString("0.###", IC));
        }

        static void Load(string dir)
        {
            var poses = Path.Combine(dir, "poses.csv");
            var meta = Path.Combine(dir, "meta.csv");
            if (!File.Exists(poses) || !File.Exists(meta)) throw new Exception("no poses.csv / meta.csv in " + dir);
            var t = new Dictionary<int, double>();
            var s = new Dictionary<int, double>();
            string[] head = null;
            foreach (var line in File.ReadAllLines(meta))
            {
                var p = line.Split(',');
                if (head == null) { head = p; continue; }
                int step;
                if (p.Length < head.Length || !int.TryParse(p[0], out step)) continue;
                t[step] = D(p[Array.IndexOf(head, "game_time")]);
                s[step] = D(p[Array.IndexOf(head, "speed")]);
            }
            var lp = new List<Vector3>();
            var lr = new List<Quaternion>();
            var lf = new List<float>();
            var ln = new List<float>();
            var lt = new List<double>();
            var ls = new List<double>();
            head = null;
            int ix = 0, iqx = 0, ifov = 0, inear = 0, icam = 0;
            foreach (var line in File.ReadAllLines(poses))
            {
                var p = line.Split(',');
                if (head == null)
                {
                    head = p;
                    ix = Array.IndexOf(head, "x"); iqx = Array.IndexOf(head, "qx"); ifov = Array.IndexOf(head, "fov"); inear = Array.IndexOf(head, "near"); icam = Array.IndexOf(head, "camera");
                    continue;
                }
                int step;
                if (p.Length < head.Length || p[icam] != cameraName || !int.TryParse(p[0], out step) || !t.ContainsKey(step)) continue;
                lp.Add(new Vector3((float)D(p[ix]), (float)D(p[ix + 1]), (float)D(p[ix + 2])));
                lr.Add(new Quaternion((float)D(p[iqx]), (float)D(p[iqx + 1]), (float)D(p[iqx + 2]), (float)D(p[iqx + 3])));
                lf.Add((float)D(p[ifov]));
                ln.Add(inear >= 0 ? (float)D(p[inear]) : 0f);
                lt.Add(t[step]);
                ls.Add(s[step]);
            }
            if (lp.Count < 2) throw new Exception("no poses of camera '" + cameraName + "' in " + poses);
            pos = lp.ToArray(); rot = lr.ToArray(); fov = lf.ToArray(); near = ln.ToArray(); gameT = lt.ToArray(); speed = ls.ToArray();
            count = pos.Length;
            fps = 30;
            loaded = dir;
        }

        static double D(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, IC, out v) ? v : 0.0;
        }

        static void Begin()
        {
            armed = false;
            running = true;
            frozenByUs = false;
            lastSpeed = -1;
            gameT0 = Time.timeDbl - gameT[0] + offset;
            wall.Reset();
            wall.Start();
            if (!hooked) { Camera.onPreCull += PreCull; hooked = true; }
            Restorer.Register(RestoreKey, "live director camera (campath)", () => Stop("restore"));
            var cam = Camera.main;
            if (cam != null) { fovBefore = cam.fieldOfView; nearBefore = cam.nearClipPlane; }
            DevLog.Write("[play] campath: running " + count + " pose(s) of " + cameraName);
        }

        static string Stop(string why)
        {
            bool was = running || armed;
            running = armed = false;
            Unhide();
            wall.Stop();
            if (frozenByUs) { try { Stepper.Unfreeze(); } catch (Exception) { } }
            frozenByUs = false;
            if (lastSpeed >= 0) { try { Stepper.SetSpeed(1); } catch (Exception) { } }
            lastSpeed = -1;
            var cam = Camera.main;
            if (cam != null && fovBefore > 0f) { cam.fieldOfView = fovBefore; cam.nearClipPlane = nearBefore; }
            fovBefore = nearBefore = -1f;
            if (was) DevLog.Write("[play] campath: stopped (" + why + ")");
            if (why != "restore") Restorer.Forget(RestoreKey);
            return was ? "campath: stopped (" + why + ")" : "campath: not running";
        }

        static double PlanT(double f)
        {
            int i = Math.Max(0, Math.Min(count - 1, (int)Math.Floor(f)));
            int j = Math.Min(count - 1, i + 1);
            double k = Math.Max(0.0, Math.Min(1.0, f - i));
            return gameT[i] + (gameT[j] - gameT[i]) * k;
        }

        static double ByGameTime(double t)
        {
            int lo = 0, hi = count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (gameT[mid] <= t) lo = mid;
                else hi = mid - 1;
            }
            if (lo >= count - 1) return count - 1;
            double span = gameT[lo + 1] - gameT[lo];
            return lo + (span > 1e-9 ? Math.Max(0.0, Math.Min(1.0, (t - gameT[lo]) / span)) : 1.0);
        }

        static void Tick()
        {
            if (armed && ActTrack.Playing) Begin();
            if (!running) return;
            if (realtime)
            {
                if (Time.timeDbl - gameT0 >= gameT[count - 1]) Stop("end of the path");
                return;
            }
            double f = wall.Elapsed.TotalSeconds * fps;
            if (f >= count - 1) { Stop("end of the path"); return; }
            int i = (int)Math.Floor(f);
            double planSpeed = speed[i];
            double drift = PlanT(f) - (Time.timeDbl - gameT0);
            if (planSpeed < StepClock.MinSpeed)
            {
                if (!frozenByUs && drift < 1.0 / 120.0) { Stepper.Freeze(); frozenByUs = true; }
                return;
            }
            if (frozenByUs) { Stepper.Unfreeze(); frozenByUs = false; }
            double want = planSpeed * Math.Max(0.5, Math.Min(2.0, 1.0 + drift * 4.0));
            want = Math.Max(StepClock.MinSpeed, Math.Min(StepClock.MaxSpeed, want));
            if (Math.Abs(want - lastSpeed) > 0.002) { Stepper.SetSpeed(want); lastSpeed = want; }
        }

        static string Mount(List<string> a)
        {
            if (a.Count < 3) return "campath mount <transform path|off> [lx ly lz] [rx ry rz] [fov] [near] [smooth 0..1] [into_scene_deg] [level 0..1] [hide 0|1] [tau_h tau_v tau_yaw tau_pitch s] [pitch_max_deg]: the camera rides the transform (position and rotation every frame), the plan keeps only the clock; hide collapses the transform (the boy's head) while mounted; tau_* > 0 stabilise the eye: position and yaw / pitch follow the transform with those time constants, roll 0, pitch clamped (without it the head bob shakes the picture by 26-33 px a frame); into_scene_deg turns the view about world up toward +z (into the level, away from the unbuilt player side), whichever way the transform faces, fading out while the view looks down 18..38 deg (the boy looking at his hands or the watch); level takes that share of the head's roll out (1 = horizon always level)";
            if (a[2].ToLowerInvariant() == "off") { Unhide(); mountPath = ""; mount = null; return "campath: mount off"; }
            float[] v = new float[17];
            float[] def = { 0f, 0f, 0f, 0f, 0f, 0f, 60f, 0.03f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 89f };
            for (int i = 0; i < 17; i++) { v[i] = def[i]; float x; if (a.Count > 3 + i && float.TryParse(a[3 + i], NumberStyles.Float, IC, out x)) v[i] = x; }
            Unhide();
            mountPath = a[2];
            mount = Find(mountPath);
            mountLocal = new Vector3(v[0], v[1], v[2]);
            mountRot = Quaternion.Euler(v[3], v[4], v[5]);
            mountFov = v[6]; mountNear = v[7]; mountSmooth = Mathf.Clamp01(v[8]); mountIntoScene = Mathf.Clamp(v[9], 0f, 89f); mountLevel = Mathf.Clamp01(v[10]);
            mountHide = v[11] > 0.5f; tauH = Mathf.Max(0f, v[12]); tauV = Mathf.Max(0f, v[13]); tauYaw = Mathf.Max(0f, v[14]); tauPitch = Mathf.Max(0f, v[15]); pitchMax = Mathf.Clamp(v[16], 1f, 89f);
            mountHas = false;
            return "campath: mounted on " + mountPath + (mount == null ? " (not found yet, retried every frame)" : "") + ", local " + mountLocal.ToString("F3") + ", rot " + mountRot.eulerAngles.ToString("F1") + ", fov " + mountFov.ToString("0.#", IC) + (mountIntoScene > 0f ? ", into the scene up to " + mountIntoScene.ToString("0.#", IC) + " deg" : "") + (mountLevel > 0f ? ", horizon level " + mountLevel.ToString("0.##", IC) : "") + (mountHide ? ", the transform collapsed while mounted" : "") + (tauH > 0f || tauV > 0f ? ", stabilised (tau h " + tauH.ToString("0.###", IC) + " v " + tauV.ToString("0.###", IC) + " yaw " + tauYaw.ToString("0.###", IC) + " pitch " + tauPitch.ToString("0.###", IC) + " s, pitch within " + pitchMax.ToString("0.#", IC) + " deg)" : "");
        }

        static string LookCommand(List<string> a)
        {
            string op = a.Count > 2 ? a[2].ToLowerInvariant() : "";
            if (op == "clear") { looks.Clear(); return "campath: looks cleared"; }
            if (op != "add" || a.Count < 7) return "campath look clear | campath look add <t0> <t1> <ease_s> <x> <y> <z> | campath look add <t0> <t1> <ease_s> @<transform path>: while mounted, the view turns to the point or transform between plan seconds t0 and t1 (eased in after t0 and out after t1); " + looks.Count + " look(s)";
            var l = new Look();
            double.TryParse(a[3], NumberStyles.Float, IC, out l.t0);
            double.TryParse(a[4], NumberStyles.Float, IC, out l.t1);
            double.TryParse(a[5], NumberStyles.Float, IC, out l.ease);
            if (a[6].StartsWith("@")) l.path = a[6].Substring(1);
            else
            {
                if (a.Count < 9) return "campath look add <t0> <t1> <ease_s> <x> <y> <z>";
                l.point = new Vector3((float)D(a[6]), (float)D(a[7]), (float)D(a[8]));
            }
            looks.Add(l);
            return "campath: look " + looks.Count + " at " + (l.path.Length > 0 ? l.path : l.point.ToString("F2")) + " from " + l.t0.ToString("0.00", IC) + " to " + l.t1.ToString("0.00", IC) + " s, ease " + l.ease.ToString("0.00", IC);
        }

        static Quaternion Looked(Quaternion wr, Vector3 eye, double tp)
        {
            foreach (var l in looks)
            {
                double e = Math.Max(0.05, l.ease);
                double k = Math.Min(Math.Max((tp - l.t0) / e, 0.0), 1.0) * Math.Min(Math.Max(1.0 - (tp - l.t1) / e, 0.0), 1.0);
                if (k <= 0.0) continue;
                Vector3 p = l.point;
                if (l.path.Length > 0)
                {
                    if (l.target == null) l.target = Find(l.path);
                    if (l.target == null) continue;
                    p = l.target.position;
                }
                var dir = p - eye;
                if (dir.sqrMagnitude < 1e-4f) continue;
                float w = (float)(k * k * (3.0 - 2.0 * k));
                wr = Quaternion.Slerp(wr, Quaternion.LookRotation(dir, Vector3.up), w);
            }
            return wr;
        }

        static void Unhide()
        {
            if (hidden != null) hidden.localScale = hiddenScale;
            hidden = null;
        }

        static Transform Find(string path)
        {
            var go = GameObject.Find(path);
            return go != null ? go.transform : null;
        }

        static void PreCull(Camera cam)
        {
            if (!running || cam == null || cam != Camera.main) return;
            if (mountPath.Length > 0)
            {
                if (mount == null) mount = Find(mountPath);
                if (mount != null)
                {
                    if (mountHide && hidden != mount) { Unhide(); hidden = mount; hiddenScale = mount.localScale; }
                    if (mountHide) mount.localScale = Vector3.one * 0.001f;
                    var wp = mount.position + mount.rotation * mountLocal;
                    var wr = mount.rotation * mountRot;
                    if (mountIntoScene > 0f)
                    {
                        var fw = wr * Vector3.forward;
                        float flat = new Vector2(fw.x, fw.z).magnitude;
                        if (flat > 1e-3f)
                        {
                            float yaw = Mathf.Atan2(fw.x, fw.z) * Mathf.Rad2Deg;
                            float down = Mathf.Asin(Mathf.Clamp(-fw.y, -1f, 1f)) * Mathf.Rad2Deg;
                            float keep = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(18f, 38f, down));
                            float turn = Mathf.Clamp(Mathf.DeltaAngle(yaw, 0f), -mountIntoScene, mountIntoScene) * keep;
                            wr = Quaternion.AngleAxis(turn, Vector3.up) * wr;
                        }
                    }
                    if (looks.Count > 0) wr = Looked(wr, wp, realtime ? Time.timeDbl - gameT0 : PlanT(wall.Elapsed.TotalSeconds * fps));
                    if (mountLevel > 0f)
                    {
                        var lf = wr * Vector3.forward;
                        if (Mathf.Abs(lf.y) < 0.98f) wr = Quaternion.Slerp(wr, Quaternion.LookRotation(lf, Vector3.up), mountLevel);
                    }
                    if (tauH > 0f || tauV > 0f)
                    {
                        var fw = wr * Vector3.forward;
                        float yaw = Mathf.Atan2(fw.x, fw.z) * Mathf.Rad2Deg;
                        float pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(fw.y, -1f, 1f)) * Mathf.Rad2Deg, -pitchMax, pitchMax);
                        float dt = Time.deltaTime;
                        if (!mountHas) { mountPos = wp; camYaw = yaw; camPitch = pitch; mountHas = true; }
                        else if (dt > 0f)
                        {
                            float ah = tauH > 0f ? 1f - Mathf.Exp(-dt / tauH) : 1f, av = tauV > 0f ? 1f - Mathf.Exp(-dt / tauV) : 1f;
                            mountPos = new Vector3(Mathf.Lerp(mountPos.x, wp.x, ah), Mathf.Lerp(mountPos.y, wp.y, av), Mathf.Lerp(mountPos.z, wp.z, ah));
                            camYaw += Mathf.DeltaAngle(camYaw, yaw) * (tauYaw > 0f ? 1f - Mathf.Exp(-dt / tauYaw) : 1f);
                            camPitch += Mathf.DeltaAngle(camPitch, pitch) * (tauPitch > 0f ? 1f - Mathf.Exp(-dt / tauPitch) : 1f);
                        }
                        mountCur = Quaternion.Euler(camPitch, camYaw, 0f);
                    }
                    else if (!mountHas || mountSmooth <= 0f) { mountPos = wp; mountCur = wr; mountHas = true; }
                    else { mountPos = Vector3.Lerp(mountPos, wp, 1f - mountSmooth); mountCur = Quaternion.Slerp(mountCur, wr, 1f - mountSmooth); }
                    cam.transform.position = mountPos;
                    cam.transform.rotation = mountCur;
                    cam.fieldOfView = mountFov;
                    cam.nearClipPlane = mountNear;
                    return;
                }
            }
            double f = realtime ? ByGameTime(Time.timeDbl - gameT0) : wall.Elapsed.TotalSeconds * fps;
            int i = Math.Max(0, Math.Min(count - 1, (int)Math.Floor(f)));
            int j = Math.Min(count - 1, i + 1);
            float k = (float)Math.Max(0.0, Math.Min(1.0, f - i));
            cam.transform.position = Vector3.Lerp(pos[i], pos[j], k);
            cam.transform.rotation = Quaternion.Slerp(rot[i], rot[j], k);
            cam.fieldOfView = Mathf.Lerp(fov[i], fov[j], k);
            float n = Mathf.Lerp(near[i], near[j], k);
            if (n > 0f) cam.nearClipPlane = n;
        }
    }
}
