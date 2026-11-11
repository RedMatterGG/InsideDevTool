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
    // The pose goes on the main camera in Camera.onPreCull, after CameraScript, so the game camera logic and the input are
    // untouched. The transform keeps the director pose until the game's camera script writes it again in the next frame, so
    // code that reads Camera.main before that (streaming, bounds culling) follows the director's view, which is what keeps
    // the things it looks at loaded. The output rate is the plan's (passes.json / run.json next to the csv files, or fps=),
    // one pose per output frame. Commands: campath load <dir> [camera] [offset_s] [rt] [fps=<n>],
    // campath arm | start | stop | status [json] | mount (the camera rides a transform, e.g. the boy's head for a first-person
    // pass; options by position or by name) | look. Restore stops it.
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
        static string loaded = "", cameraName = "director", fpsFrom = "";
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
        static float tauH, tauV, tauYaw, tauPitch, pitchMax = 89f, pitchUp = 89f, pitchDown = 89f, camYaw, camPitch;
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
            tick = PlayRegistry.OnTick("campath", Tick, TickWhen.OnDemand);
            Film.Register();
        }

        static PlayTick tick;

        // the tick runs only while a path is armed or running
        static void SyncTick()
        {
            var t = tick;
            if (t != null) t.Active = armed || running;
        }

        public const string LoadUsage = "campath load <dir with poses.csv and meta.csv> [camera] [offset_s] [rt] [fps=<output rate>]";

        public static string Command(List<string> args)
        {
            var a = new List<string>(args);
            string sub = a.Count > 1 ? a[1].ToLowerInvariant() : "status";
            if (sub == "load")
            {
                double rate = 0;
                for (int i = a.Count - 1; i >= 2; i--)
                {
                    if (!a[i].StartsWith("fps=", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Fmt.TryD(a[i].Substring(4), out rate) || rate < 1 || rate > 1000) return "usage: fps=<1..1000>, got " + a[i] + "\n" + LoadUsage;
                    a.RemoveAt(i);
                }
                if (a.Count < 3) return "usage: " + LoadUsage;
                bool rt = a.Count > 5 && a[5].ToLowerInvariant() == "rt";
                string cam = a.Count > 3 ? a[3] : "director";
                double off = 0;
                if (a.Count > 4 && !Fmt.TryD(a[4], out off)) return "usage: offset_s must be a number, got " + a[4] + "\n" + LoadUsage;
                if (running || armed) Stop("load");
                try { Load(a[2], cam, off, rt, rate); }
                catch (Exception e) { return "campath: failed: " + e.Message; }
                return "campath: " + count + " pose(s) of " + cameraName + " from " + loaded + (realtime ? " (realtime: no pauses or slow motion, pose by game time)" : "") + ", " + (count / fps).ToString("0.0", IC) + " s output, T " +
                       gameT[0].ToString("0.###", IC) + ".." + gameT[count - 1].ToString("0.###", IC) + ", offset " + offset.ToString("0.###", IC) + ", " + Fmt.F(fps) + " fps (" + fpsFrom + ")";
            }
            if (sub == "arm")
            {
                if (count == 0) return "campath: nothing loaded";
                if (running || armed) Stop("restart");
                armed = true;
                SyncTick();
                return "campath: armed, starts with the next input_track playback step";
            }
            if (sub == "start")
            {
                if (count == 0) return "campath: nothing loaded";
                if (running || armed) Stop("restart");
                Begin();
                return "campath: running";
            }
            if (sub == "stop") return Stop("stop");
            if (sub == "mount") return Mount(a);
            if (sub == "look") return LookCommand(a);
            if (sub == "help") return "usage: " + LoadUsage + " | campath arm | start | stop | status [json] | campath mount ... | campath look ...";
            return FilmShared.TakeLast(a, "json", 2) ? JsonLine.Write(StatusObj()) : Status();
        }

        static string Status()
        {
            string tail = (count > 0 ? ", " + Fmt.F(fps) + " fps" : "") + (mountPath.Length > 0 ? ", mount " + mountPath + (mount == null ? " (not found)" : "") : "") + (looks.Count > 0 ? ", " + looks.Count + " look(s)" : "");
            if (!running) return "campath: " + (armed ? "armed" : "idle") + ", " + count + " pose(s) of " + cameraName + (loaded.Length > 0 ? " from " + loaded : "") + tail;
            double tau = wall.Elapsed.TotalSeconds;
            return "campath: running " + tau.ToString("0.00", IC) + " / " + (count / fps).ToString("0.00", IC) + " s, game " + (Time.timeDbl - gameT0).ToString("0.000", IC) +
                   " (plan " + PlanT(tau * fps).ToString("0.000", IC) + "), " + (frozenByUs ? "paused" : "speed " + lastSpeed.ToString("0.###", IC)) + tail;
        }

        // main thread: campath status json and the film tool
        internal static Json.Obj StatusObj()
        {
            var o = new Json.Obj();
            o["state"] = running ? "running" : armed ? "armed" : "idle";
            o["poses"] = (double)count;
            o["camera"] = cameraName;
            if (loaded.Length > 0) o["dir"] = loaded;
            if (count > 0)
            {
                o["fps"] = fps; o["fps_from"] = fpsFrom;
                o["seconds"] = Math.Round(count / fps, 3);
                o["realtime"] = realtime;
                o["offset"] = offset;
            }
            if (running)
            {
                double tau = wall.Elapsed.TotalSeconds;
                o["at"] = Math.Round(tau, 3);
                o["game"] = Math.Round(Time.timeDbl - gameT0, 3);
                if (!realtime) o["plan"] = Math.Round(PlanT(tau * fps), 3);
                o["paused"] = frozenByUs;
                if (lastSpeed >= 0) o["speed"] = Math.Round(lastSpeed, 3);
            }
            if (mountPath.Length > 0) o["mount"] = MountObj();
            o["looks"] = (double)looks.Count;
            return o;
        }

        // Everything is read into locals; the loaded plan is replaced only when the files were good.
        static void Load(string dir, string camName, double off, bool rt, double rateAsked)
        {
            var poses = Path.Combine(dir, "poses.csv");
            var meta = Path.Combine(dir, "meta.csv");
            if (!File.Exists(poses) || !File.Exists(meta)) throw new Exception("no poses.csv / meta.csv in " + dir);
            var t = new Dictionary<int, double>();
            var sp = new Dictionary<int, double>();
            List<string> head = null;
            int cStep = 0, cTime = 0, cSpeed = 0, cSub = -1, cGroup = -1;
            foreach (var line in File.ReadAllLines(meta))
            {
                var c = Csv(line);
                if (head == null)
                {
                    head = c;
                    cStep = Col(head, "step", "meta.csv"); cTime = Col(head, "game_time", "meta.csv"); cSpeed = Col(head, "speed", "meta.csv");
                    cSub = head.IndexOf("sub"); cGroup = head.IndexOf("group");
                    continue;
                }
                int step, sub, group;
                if (c.Count < head.Count || !Fmt.TryI(c[cStep], out step) || step < 0) continue;
                // a plan with several samples per output frame: the last sample of a frame carries the frame's time
                if (cSub >= 0 && cGroup >= 0 && Fmt.TryI(c[cGroup], out group) && group > 1 && Fmt.TryI(c[cSub], out sub) && sub != group - 1) continue;
                t[step] = D(c[cTime]);
                sp[step] = D(c[cSpeed]);
            }
            if (head == null) throw new Exception("meta.csv is empty");
            var lp = new List<Vector3>();
            var lr = new List<Quaternion>();
            var lf = new List<float>();
            var ln = new List<float>();
            var lt = new List<double>();
            var ls = new List<double>();
            head = null;
            int ix = 0, iqx = 0, ifov = 0, inear = 0, icam = 0, istep = 0;
            foreach (var line in File.ReadAllLines(poses))
            {
                var c = Csv(line);
                if (head == null)
                {
                    head = c;
                    istep = Col(head, "step", "poses.csv"); icam = Col(head, "camera", "poses.csv"); ix = Col(head, "x", "poses.csv"); iqx = Col(head, "qx", "poses.csv");
                    ifov = Col(head, "fov", "poses.csv"); inear = head.IndexOf("near");
                    if (ix + 2 >= head.Count || iqx + 3 >= head.Count) throw new Exception("poses.csv: the x,y,z / qx,qy,qz,qw columns are incomplete");
                    continue;
                }
                int step;
                if (c.Count < head.Count || c[icam] != camName || !Fmt.TryI(c[istep], out step) || !t.ContainsKey(step)) continue;
                lp.Add(new Vector3((float)D(c[ix]), (float)D(c[ix + 1]), (float)D(c[ix + 2])));
                lr.Add(new Quaternion((float)D(c[iqx]), (float)D(c[iqx + 1]), (float)D(c[iqx + 2]), (float)D(c[iqx + 3])));
                lf.Add((float)D(c[ifov]));
                ln.Add(inear >= 0 ? (float)D(c[inear]) : 0f);
                lt.Add(t[step]);
                ls.Add(sp[step]);
            }
            if (head == null) throw new Exception("poses.csv is empty");
            if (lp.Count < 2) throw new Exception("no poses of camera '" + camName + "' in " + poses);
            string from = "";
            double rate = rateAsked > 0 ? rateAsked : PlanRate(dir, out from);
            if (rateAsked > 0) from = "fps=";
            else if (rate <= 0) { rate = 30; from = "assumed: no passes.json / run.json with fps next to the csv files, give fps=<n> when the plan is not 30"; }
            pos = lp.ToArray(); rot = lr.ToArray(); fov = lf.ToArray(); near = ln.ToArray(); gameT = lt.ToArray(); speed = ls.ToArray();
            count = pos.Length;
            fps = rate;
            fpsFrom = from;
            loaded = dir;
            cameraName = camName;
            offset = off;
            realtime = rt;
        }

        static int Col(List<string> head, string name, string file)
        {
            int i = head.IndexOf(name);
            if (i < 0) throw new Exception(file + " has no column '" + name + "'");
            return i;
        }

        // the output rate the plan was made for: "fps" of passes.json, else timeline.fps / fps of run.json; 0 = not found
        static double PlanRate(string dir, out string from)
        {
            from = "";
            foreach (var name in new[] { "passes.json", "run.json" })
            {
                try
                {
                    string f = Path.Combine(dir, name);
                    if (!File.Exists(f)) continue;
                    var o = Json.Parse(File.ReadAllText(f)) as Json.Obj;
                    if (o == null) continue;
                    var tl = o["timeline"] as Json.Obj;
                    object v = tl != null && tl["fps"] is double ? tl["fps"] : o["fps"];
                    if (v is double && (double)v >= 1 && (double)v <= 1000) { from = name; return (double)v; }
                }
                catch { }
            }
            return 0;
        }

        // one csv line into fields; a field in quotes may hold commas, a doubled quote is a quote
        static List<string> Csv(string line)
        {
            var r = new List<string>();
            if (line.IndexOf('"') < 0) { r.AddRange(line.Split(',')); return r; }
            var sb = new System.Text.StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (q)
                {
                    if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; }
                    else sb.Append(ch);
                }
                else if (ch == '"') q = true;
                else if (ch == ',') { r.Add(sb.ToString()); sb.Length = 0; }
                else sb.Append(ch);
            }
            r.Add(sb.ToString());
            return r;
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
            SyncTick();
            frozenByUs = false;
            lastSpeed = -1;
            gameT0 = Time.timeDbl - gameT[0] + offset;
            wall.Reset();
            wall.Start();
            if (!hooked) { Camera.onPreCull += PreCull; hooked = true; }
            Restorer.Register(RestoreKey, "live director camera (campath)", () => Stop("restore"));
            mountHas = false;
            var cam = Camera.main;
            if (cam != null && fovBefore < 0f) { fovBefore = cam.fieldOfView; nearBefore = cam.nearClipPlane; }
            DevLog.Write("[play] campath: running " + count + " pose(s) of " + cameraName);
        }

        static string Stop(string why)
        {
            bool was = running || armed;
            running = armed = false;
            SyncTick();
            mountHas = false;
            if (hooked) { Camera.onPreCull -= PreCull; hooked = false; }
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

        const string MountUsage = "campath mount <transform path|off|status> [lx ly lz] [rx ry rz] [fov] [near] [smooth 0..1] [into_scene_deg] [level 0..1] [hide 0|1] [tau_h tau_v tau_yaw tau_pitch s] [pitch_max_deg] [free_up_deg free_down_deg], or the same by name in any order: local=x,y,z rot=x,y,z fov= near= smooth= into= level= hide=0|1 tau=h,v,yaw,pitch pitch= free=up,down";

        static string Mount(List<string> a)
        {
            if (a.Count < 3) return MountUsage + ": the camera rides the transform (position and rotation every frame), the plan keeps only the clock; hide collapses the transform (the boy's head) while mounted; tau_* > 0 stabilise the eye: position and yaw / pitch follow the transform with those time constants, roll 0, pitch clamped (without it the head bob shakes the picture by 26-33 px a frame); into_scene_deg turns the view about world up toward +z (into the level, away from the unbuilt player side), whichever way the transform faces, fading out while the view looks down 18..38 deg (the boy looking at his hands or the watch); level takes that share of the head's roll out (1 = horizon always level); free_up / free_down clamp the eye's pitch outside the look windows (the head's own up / down tilt: idle, ladders), blended to pitch_max by the look weight";
            string first = a[2].ToLowerInvariant();
            if (first == "off") { Unhide(); mountPath = ""; mount = null; return "campath: mount off"; }
            if (first == "status") return mountPath.Length == 0 ? "campath: mount off" : MountText();
            float[] v = { 0f, 0f, 0f, 0f, 0f, 0f, 60f, 0.03f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 89f, 0f, 0f };
            int at = 0;
            for (int i = 3; i < a.Count; i++)
            {
                string s = a[i];
                int eq = s.IndexOf('=');
                if (eq <= 0)
                {
                    float x;
                    if (at < v.Length && Fmt.TryF(s, out x)) v[at] = x;
                    at++;
                    continue;
                }
                string key = s.Substring(0, eq).ToLowerInvariant(), err = null;
                var val = s.Substring(eq + 1).Split(',');
                switch (key)
                {
                    case "local": err = Named(val, v, 0, 3, 3); break;
                    case "rot": err = Named(val, v, 3, 3, 3); break;
                    case "fov": err = Named(val, v, 6, 1, 1); break;
                    case "near": err = Named(val, v, 7, 1, 1); break;
                    case "smooth": err = Named(val, v, 8, 1, 1); break;
                    case "into": err = Named(val, v, 9, 1, 1); break;
                    case "level": err = Named(val, v, 10, 1, 1); break;
                    case "hide":
                        if (val.Length == 1 && (val[0] == "on" || val[0] == "off")) v[11] = val[0] == "on" ? 1f : 0f;
                        else err = Named(val, v, 11, 1, 1);
                        break;
                    case "tau": err = Named(val, v, 12, 2, 4); break;
                    case "pitch": err = Named(val, v, 16, 1, 1); break;
                    case "free": err = Named(val, v, 17, 2, 2); break;
                    default: err = "unknown option"; break;
                }
                if (err != null) return "usage: " + s + ": " + err + "\n" + MountUsage;
            }
            if (!(v[6] > 0f)) v[6] = 60f;
            if (!(v[7] > 0f)) v[7] = 0.01f;
            if (v[6] > 179f) return "usage: fov " + Fmt.F(v[6]) + " is over 179\n" + MountUsage;
            if (v[7] > 10f) return "usage: near " + Fmt.F(v[7]) + " is over 10 m\n" + MountUsage;
            Unhide();
            mountPath = a[2];
            mount = Find(mountPath);
            mountLocal = new Vector3(v[0], v[1], v[2]);
            mountRot = Quaternion.Euler(v[3], v[4], v[5]);
            mountFov = v[6]; mountNear = v[7]; mountSmooth = Mathf.Clamp01(v[8]); mountIntoScene = Mathf.Clamp(v[9], 0f, 89f); mountLevel = Mathf.Clamp01(v[10]);
            mountHide = v[11] > 0.5f; tauH = Mathf.Max(0f, v[12]); tauV = Mathf.Max(0f, v[13]); tauYaw = Mathf.Max(0f, v[14]); tauPitch = Mathf.Max(0f, v[15]); pitchMax = Mathf.Clamp(v[16], 1f, 89f);
            pitchUp = v[17] > 0f ? Mathf.Clamp(v[17], 1f, pitchMax) : pitchMax; pitchDown = v[18] > 0f ? Mathf.Clamp(v[18], 1f, pitchMax) : pitchMax;
            mountHas = false;
            return MountText();
        }

        // min..max numbers of one named option into v from index at
        static string Named(string[] val, float[] v, int at, int min, int max)
        {
            if (val.Length < min || val.Length > max) return min == max ? "needs " + min + " number(s)" : "needs " + min + " to " + max + " numbers";
            for (int i = 0; i < val.Length; i++)
            {
                float x;
                if (!Fmt.TryF(val[i].Trim(), out x)) return "'" + val[i] + "' is not a number";
                v[at + i] = x;
            }
            return null;
        }

        static string MountText()
        {
            return "campath: mounted on " + mountPath + (mount == null ? " (not found yet, retried every frame)" : "") + ", local " + Fmt.V3(mountLocal, "F3") + ", rot " + Fmt.V3(mountRot.eulerAngles, "F1") + ", fov " + mountFov.ToString("0.#", IC) + ", near " + Fmt.F(mountNear) + (mountSmooth > 0f ? ", smooth " + Fmt.F(mountSmooth) : "") + (mountIntoScene > 0f ? ", into the scene up to " + mountIntoScene.ToString("0.#", IC) + " deg" : "") + (mountLevel > 0f ? ", horizon level " + mountLevel.ToString("0.##", IC) : "") + (mountHide ? ", the transform collapsed while mounted" : "") + (tauH > 0f || tauV > 0f ? ", stabilised (tau h " + tauH.ToString("0.###", IC) + " v " + tauV.ToString("0.###", IC) + " yaw " + tauYaw.ToString("0.###", IC) + " pitch " + tauPitch.ToString("0.###", IC) + " s, pitch within " + pitchMax.ToString("0.#", IC) + " deg" + (pitchUp < pitchMax || pitchDown < pitchMax ? ", free eye " + (-pitchUp).ToString("0.#", IC) + "..+" + pitchDown.ToString("0.#", IC) + " deg" : "") + ")" : "");
        }

        static Json.Obj MountObj()
        {
            var o = new Json.Obj();
            o["path"] = mountPath;
            o["found"] = mount != null;
            var e = mountRot.eulerAngles;
            o["local"] = new List<object> { (double)mountLocal.x, (double)mountLocal.y, (double)mountLocal.z };
            o["rot"] = new List<object> { (double)e.x, (double)e.y, (double)e.z };
            o["fov"] = (double)mountFov; o["near"] = (double)mountNear; o["smooth"] = (double)mountSmooth; o["into"] = (double)mountIntoScene; o["level"] = (double)mountLevel;
            o["hide"] = mountHide;
            o["tau"] = new List<object> { (double)tauH, (double)tauV, (double)tauYaw, (double)tauPitch };
            o["pitch"] = (double)pitchMax;
            o["free"] = new List<object> { (double)pitchUp, (double)pitchDown };
            return o;
        }

        static string LookCommand(List<string> a)
        {
            string op = a.Count > 2 ? a[2].ToLowerInvariant() : "";
            if (op == "clear") { looks.Clear(); return "campath: looks cleared"; }
            if (op != "add" || a.Count < 7) return "campath look clear | campath look add <t0> <t1> <ease_s> <x> <y> <z> | campath look add <t0> <t1> <ease_s> @<transform path>: while mounted, the view turns to the point or transform between plan seconds t0 and t1 (eased in after t0 and out after t1); " + looks.Count + " look(s)";
            var l = new Look();
            if (!Fmt.TryD(a[3], out l.t0) || !Fmt.TryD(a[4], out l.t1) || !Fmt.TryD(a[5], out l.ease)) return "usage: t0, t1 and ease_s must be numbers: campath look add <t0> <t1> <ease_s> <x> <y> <z> | @<transform path>";
            if (a[6].StartsWith("@")) l.path = a[6].Substring(1);
            else
            {
                if (a.Count < 9) return "campath look add <t0> <t1> <ease_s> <x> <y> <z>";
                l.point = new Vector3((float)D(a[6]), (float)D(a[7]), (float)D(a[8]));
            }
            looks.Add(l);
            return "campath: look " + looks.Count + " at " + (l.path.Length > 0 ? l.path : Fmt.V3(l.point, "F2")) + " from " + l.t0.ToString("0.00", IC) + " to " + l.t1.ToString("0.00", IC) + " s, ease " + l.ease.ToString("0.00", IC);
        }

        static Quaternion Looked(Quaternion wr, Vector3 eye, double tp, out float weight)
        {
            weight = 0f;
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
                weight = Mathf.Max(weight, w);
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
            if (!running || Pano.Capturing || cam == null || cam != Camera.main) return;
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
                    float lookW = 0f;
                    if (looks.Count > 0) wr = Looked(wr, wp, realtime ? Time.timeDbl - gameT0 : PlanT(wall.Elapsed.TotalSeconds * fps), out lookW);
                    if (mountLevel > 0f)
                    {
                        var lf = wr * Vector3.forward;
                        if (Mathf.Abs(lf.y) < 0.98f) wr = Quaternion.Slerp(wr, Quaternion.LookRotation(lf, Vector3.up), mountLevel);
                    }
                    if (tauH > 0f || tauV > 0f)
                    {
                        var fw = wr * Vector3.forward;
                        float yaw = Mathf.Atan2(fw.x, fw.z) * Mathf.Rad2Deg;
                        float pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(fw.y, -1f, 1f)) * Mathf.Rad2Deg, -Mathf.Lerp(pitchUp, pitchMax, lookW), Mathf.Lerp(pitchDown, pitchMax, lookW));
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
                    else
                    {
                        // the same feel at any frame rate (the value is the share kept per 1/60 s) and no drift while frozen
                        float keep = 1f - Mathf.Pow(mountSmooth, Time.deltaTime * 60f);
                        mountPos = Vector3.Lerp(mountPos, wp, keep);
                        mountCur = Quaternion.Slerp(mountCur, wr, keep);
                    }
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
