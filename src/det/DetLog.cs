using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace InsideDev.Det
{
    // DetLog.cs: per-step log of one determinism run (REPLAY_DESIGN 7.3). steps.csv has one row per logic step k, begun at
    // the RuntimeCallbacks.fixedUpdate hook (rng_fixed, state after step k-1) and finished in RuntimeCallbacks.lateUpdate of
    // the same frame (fixed_n, rng_update from the RuntimeCallbacks.update hook, rng_late). rng_fixed -> rng_update brackets
    // the FixedUpdate scripts and physics of step k, rng_update -> rng_late the Update phase, animation events and early
    // LateUpdate. events.csv, rig.csv (Camera.main pose at RuntimeCallbacks.update, one row per frame from the
    // allScenesLoaded frame), chicks.tsv / culls.tsv (column order), inputs.csv and cues.tsv (what a later replay feeds).
    // In a replay every finished row is compared with the source run (det status).
    public struct DetRigRow
    {
        public Vector3 pos;
        public Quaternion rot;
        public float fov;
    }

    public sealed class DetCue
    {
        public long k;
        public string cmd;
        public long lastFiredStep = long.MinValue;
    }

    public sealed class DetLog : IFastEventExListener<Collision>
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        public const float PosEps = 1e-4f;
        public const float LandDv = 1f;
        public const float HitSqr = 50f;

        public readonly string dir;
        StreamWriter steps, events, rig;
        public int rows, eventCount, rigRows;
        public long kBaleFall = -1, kBaleLand = -1, curK = -1;
        public int shots, hits, kills, chickCount, groupCount;
        public string killed = "";
        public readonly List<string> warnings = new List<string>();

        Boy boy;
        Rigidbody bale;
        ChickenGunLogic gun;
        IsTriggeredByProbe trigger;
        Action<IsTriggeredByProbe> onProbe;
        CollisionEvents boxEvents;
        int boxHandle = -1;
        readonly List<Chicken> chicks = new List<Chicken>();
        readonly List<Rigidbody> chickRbs = new List<Rigidbody>();
        readonly List<BoundsCullerRuntime> groups = new List<BoundsCullerRuntime>();
        bool[] prevKin, prevDead;
        bool haveBaleVel, headerDone, resolved;
        Vector3 lastBaleVel;
        int posCount = 6;

        static FieldInfo fState, fSucking, fVac, fBlow, fShoot, fKill, fTarget;

        sealed class Pending { public long k; public StringBuilder sb; public float[] pos; public int rngFixed, rngUpdate; public bool hasUpdate; }
        readonly List<Pending> pending = new List<Pending>();

        sealed class RefRow { public int rngFixed, rngUpdate, rngLate; public float[] pos; }
        Dictionary<long, RefRow> reference;
        public long kStar = -1, kRngFixed = -1, kRngUpdate = -1, kRngLate = -1, kPos = -1, compared;
        public double maxBoy, maxBale, maxChick;
        public string refDir = "";

        public DetLog(string dir)
        {
            this.dir = dir;
            Directory.CreateDirectory(dir);
            steps = new StreamWriter(Path.Combine(dir, "steps.csv"), false, Utf8);
            events = new StreamWriter(Path.Combine(dir, "events.csv"), false, Utf8);
            rig = new StreamWriter(Path.Combine(dir, "rig.csv"), false, Utf8);
            events.WriteLine("k,kind,name,detail");
            rig.WriteLine("f,frame,k,x,y,z,qx,qy,qz,qw,fov,free");
        }

        // ---------------------------------------------------------------- objects

        public void Resolve(List<BoundsCullerRuntime> zoneGroups)
        {
            if (resolved) return;
            boy = ScriptGlobals.boy;
            var gunGo = Remote.Resolve("ChickenGunObject/ChickenGunVacuumLogic");
            gun = gunGo != null ? gunGo.GetComponent<ChickenGunLogic>() : null;
            if (gun == null) gun = UnityEngine.Object.FindObjectOfType(typeof(ChickenGunLogic)) as ChickenGunLogic;
            var baleGo = Remote.Resolve("HayBaleHolder/HayBale");
            bale = baleGo != null ? baleGo.GetComponent<Rigidbody>() : null;
            if (bale == null && gun != null) bale = gun.box;
            var trigGo = Remote.Resolve("HayBaleHolder/HayBaleTrigger");
            trigger = trigGo != null ? trigGo.GetComponent<IsTriggeredByProbe>() : null;
            var spawnGo = Remote.Resolve("_SaveLogic/ChickensLoaded");
            var spawner = spawnGo != null ? spawnGo.GetComponent<ChickenSpawner>() : null;
            var found = new List<Chicken>();
            if (spawner != null && spawner.chickens != null) foreach (var c in spawner.chickens) if (c != null && !found.Contains(c)) found.Add(c);
            if (found.Count == 0) foreach (var o in UnityEngine.Object.FindObjectsOfType(typeof(Chicken))) { var c = o as Chicken; if (c != null) found.Add(c); }
            var keys = new List<KeyValuePair<string, int>>();
            for (int i = 0; i < found.Count; i++) keys.Add(new KeyValuePair<string, int>(Key(found[i].transform), i));
            keys.Sort((a, b) => { int r = string.CompareOrdinal(a.Key, b.Key); return r != 0 ? r : a.Value.CompareTo(b.Value); });
            chicks.Clear(); chickRbs.Clear();
            var tsv = new StringBuilder("i\tpath\n");
            for (int i = 0; i < keys.Count; i++)
            {
                var c = found[keys[i].Value];
                chicks.Add(c);
                chickRbs.Add(c.GetComponent<Rigidbody>());
                tsv.Append(i).Append('\t').Append(keys[i].Key).Append('\n');
            }
            File.WriteAllText(Path.Combine(dir, "chicks.tsv"), tsv.ToString(), Utf8);
            groups.Clear();
            var gkeys = new List<KeyValuePair<string, BoundsCullerRuntime>>();
            if (zoneGroups != null) foreach (var g in zoneGroups) if (g != null) gkeys.Add(new KeyValuePair<string, BoundsCullerRuntime>(Key(g.transform), g));
            gkeys.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            var ctsv = new StringBuilder("i\tpath\n");
            for (int i = 0; i < gkeys.Count; i++) { groups.Add(gkeys[i].Value); ctsv.Append(i).Append('\t').Append(gkeys[i].Key).Append('\n'); }
            File.WriteAllText(Path.Combine(dir, "culls.tsv"), ctsv.ToString(), Utf8);
            chickCount = chicks.Count;
            groupCount = groups.Count;
            posCount = 6 + 3 * chicks.Count;
            prevKin = new bool[chicks.Count];
            prevDead = new bool[chicks.Count];
            for (int i = 0; i < chicks.Count; i++)
            {
                prevKin[i] = chickRbs[i] != null && chickRbs[i].isKinematic;
                prevDead[i] = chicks[i] != null && chicks[i].dead;
            }
            if (fState == null)
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var t = typeof(ChickenGunLogic);
                fState = t.GetField("state", flags); fSucking = t.GetField("isSucking", flags); fVac = t.GetField("chickensInVacuum", flags);
                fBlow = t.GetField("chickensToBlow", flags); fShoot = t.GetField("shootTimer", flags); fKill = t.GetField("killCountdown", flags);
                fTarget = typeof(Chicken).GetField("targetPosTranslation", flags);
            }
            if (boy == null) warnings.Add("boy not found");
            if (gun == null) warnings.Add("ChickenGunLogic not found");
            if (bale == null) warnings.Add("HayBale rigidbody not found");
            if (trigger == null) warnings.Add("HayBaleTrigger IsTriggeredByProbe not found: no baleFall, so no baleLand stop");
            if (chicks.Count != 24) warnings.Add(chicks.Count + " chickens resolved (expected 24)");
            if (trigger != null) { onProbe = OnProbe; trigger.probeEnter.Event += onProbe; }
            if (gun != null && gun.boxCollisions != null) { boxEvents = gun.boxCollisions; boxEvents.collisionEnter.Register(this, ref boxHandle); }
            resolved = true;
        }

        static string Key(Transform t)
        {
            var sb = new StringBuilder();
            for (var p = t; p != null && p.parent != null; p = p.parent) sb.Insert(0, p.GetSiblingIndex().ToString(IC) + (sb.Length > 0 ? "." : ""));
            return Inspector.PathOf(t) + " [" + sb + "]";
        }

        void OnProbe(IsTriggeredByProbe p)
        {
            try
            {
                if (kBaleFall < 0) { kBaleFall = curK; Event(curK, "baleFall", "HayBaleTrigger", "probeEnter"); }
                else Event(curK, "probeEnter", "HayBaleTrigger", "");
            }
            catch (Exception e) { DevLog.Error("[det] probe", e); }
        }

        public void OnFastEventEx(FastEventEx<Collision> sender, ref Collision collision)
        {
            try
            {
                if (collision == null) return;
                float sq = collision.relativeVelocity.sqrMagnitude;
                if (sq <= HitSqr) return;
                hits++;
                string other = collision.collider != null ? collision.collider.name : "?";
                Event(curK, "baleHit", other, F(sq));
            }
            catch (Exception e) { DevLog.Error("[det] bale hit", e); }
        }

        // ---------------------------------------------------------------- rows

        void Header()
        {
            var h = new StringBuilder("k,step,frame,rng_fixed,in_x,in_y,in_jump,in_action,boy_x,boy_y,boy_z,boy_vx,boy_vy,boy_vz,boy_state,");
            h.Append("bale_x,bale_y,bale_z,bale_qx,bale_qy,bale_qz,bale_qw,bale_vx,bale_vy,bale_vz,bale_wx,bale_wy,bale_wz,bale_sleep,bale_kin,");
            h.Append("cg_state,cg_sucking,cg_vac,cg_blow,cg_shoot_timer,cg_kill_countdown,");
            for (int i = 0; i < chicks.Count; i++)
            {
                string c = "c" + i.ToString("00", IC) + "_";
                h.Append(c).Append("x,").Append(c).Append("y,").Append(c).Append("z,").Append(c).Append("vx,").Append(c).Append("vy,").Append(c).Append("vz,");
                h.Append(c).Append("kin,").Append(c).Append("started,").Append(c).Append("dead,").Append(c).Append("tx,").Append(c).Append("ty,").Append(c).Append("tz,");
            }
            h.Append("cull_active,cull_bits,subs_loaded,subs_active,fixed_n,rng_update,rng_late");
            steps.WriteLine(h.ToString());
            headerDone = true;
        }

        public void Step(long k, long step, bool hasInput, DetInput input)
        {
            int rngFixed = UnityEngine.Random.seed;
            curK = k;
            if (!headerDone) Header();
            var p = new Pending { k = k, rngFixed = rngFixed, sb = new StringBuilder(2048), pos = new float[posCount] };
            for (int i = 0; i < p.pos.Length; i++) p.pos[i] = float.NaN;
            var sb = p.sb;
            sb.Append(k.ToString(IC)).Append(',').Append(step.ToString(IC)).Append(',').Append(Time.frameCount.ToString(IC)).Append(',').Append(rngFixed.ToString(IC)).Append(',');
            if (hasInput) sb.Append(F(input.x)).Append(',').Append(F(input.y)).Append(',').Append(B(input.jump)).Append(',').Append(B(input.action)).Append(',');
            else sb.Append(",,,,");
            if (boy != null && boy.isCharacterActive)
            {
                var bp = boy.pos3; var bv = boy.velocity3;
                p.pos[0] = bp.x; p.pos[1] = bp.y; p.pos[2] = bp.z;
                V(sb, bp); V(sb, bv);
                sb.Append(boy.state != null ? boy.state.GetType().Name : "none").Append(',');
            }
            else sb.Append(",,,,,,inactive,");
            if (bale != null)
            {
                var pp = bale.position; var q = bale.rotation; var v = bale.velocity; var w = bale.angularVelocity;
                p.pos[3] = pp.x; p.pos[4] = pp.y; p.pos[5] = pp.z;
                V(sb, pp);
                sb.Append(F(q.x)).Append(',').Append(F(q.y)).Append(',').Append(F(q.z)).Append(',').Append(F(q.w)).Append(',');
                V(sb, v); V(sb, w);
                sb.Append(B(bale.IsSleeping())).Append(',').Append(B(bale.isKinematic)).Append(',');
                if (kBaleFall >= 0 && kBaleLand < 0 && k > kBaleFall && haveBaleVel)
                {
                    float dv = (lastBaleVel - v).magnitude;
                    if (dv >= LandDv && v.magnitude < lastBaleVel.magnitude)
                    {
                        kBaleLand = k;
                        Event(k, "baleLand", "HayBale", "dv " + F(dv) + " speed " + F(lastBaleVel.magnitude) + " -> " + F(v.magnitude));
                    }
                }
                lastBaleVel = v;
                haveBaleVel = true;
            }
            else sb.Append(",,,,,,,,,,,,,,,");
            if (gun != null)
            {
                sb.Append(Cell(fState, gun)).Append(',').Append(Cell(fSucking, gun)).Append(',').Append(Cell(fVac, gun)).Append(',');
                sb.Append(Cell(fBlow, gun)).Append(',').Append(Cell(fShoot, gun)).Append(',').Append(Cell(fKill, gun)).Append(',');
            }
            else sb.Append(",,,,,,");
            for (int i = 0; i < chicks.Count; i++)
            {
                var c = chicks[i];
                var rb = chickRbs[i];
                if (c == null) { sb.Append(",,,,,,,,,,,,"); continue; }
                var cp = rb != null ? rb.position : c.transform.position;
                var cv = rb != null ? rb.velocity : Vector3.zero;
                bool kin = rb != null && rb.isKinematic;
                p.pos[6 + 3 * i] = cp.x; p.pos[7 + 3 * i] = cp.y; p.pos[8 + 3 * i] = cp.z;
                V(sb, cp); V(sb, cv);
                sb.Append(B(kin)).Append(',').Append(B(c.isStarted)).Append(',').Append(B(c.dead)).Append(',');
                object t = fTarget != null ? fTarget.GetValue(c) : null;
                if (t is Vector3) V(sb, (Vector3)t); else sb.Append(",,,");
                string path = c.name;
                if (prevKin[i] && !kin) { shots++; Event(k, "shot", path, "chick " + i.ToString(IC)); }
                if (!prevDead[i] && c.dead) { kills++; killed = path; Event(k, "kill", path, "chick " + i.ToString(IC)); }
                prevKin[i] = kin;
                prevDead[i] = c.dead;
            }
            int act = 0;
            var bits = new StringBuilder(groups.Count);
            foreach (var g in groups)
            {
                bool on = g != null && g.gameObject.activeSelf;
                if (on) act++;
                bits.Append(on ? '1' : '0');
            }
            sb.Append(act.ToString(IC)).Append(',').Append(bits.Length > 0 ? bits.ToString() : "-").Append(',');
            int loaded = 0, active = 0;
            try
            {
                foreach (var sc in SubsceneCullingManager.instance.GetAllSubsceneCullers())
                {
                    if (sc == null) continue;
                    if (sc.IsLoaded) loaded++;
                    if (sc.IsActive) active++;
                }
            }
            catch { }
            sb.Append(loaded.ToString(IC)).Append(',').Append(active.ToString(IC)).Append(',');
            pending.Add(p);
        }

        public void MarkUpdate(int rngUpdate)
        {
            foreach (var p in pending) if (!p.hasUpdate) { p.rngUpdate = rngUpdate; p.hasUpdate = true; }
        }

        public void Late(int rngLate, int fixedN)
        {
            foreach (var p in pending)
            {
                p.sb.Append(fixedN.ToString(IC)).Append(',').Append(p.hasUpdate ? p.rngUpdate.ToString(IC) : "").Append(',').Append(rngLate.ToString(IC));
                steps.WriteLine(p.sb.ToString());
                rows++;
                Compare(p, rngLate);
            }
            pending.Clear();
            if (rows % 60 == 0) Flush();
        }

        public void Rig(int f, long k, Camera c, bool free)
        {
            if (c == null) return;
            var t = c.transform;
            var p = t.position; var q = t.rotation;
            var sb = new StringBuilder(160);
            sb.Append(f.ToString(IC)).Append(',').Append(Time.frameCount.ToString(IC)).Append(',').Append(k.ToString(IC)).Append(',');
            V(sb, p);
            sb.Append(F(q.x)).Append(',').Append(F(q.y)).Append(',').Append(F(q.z)).Append(',').Append(F(q.w)).Append(',').Append(F(c.fieldOfView)).Append(',').Append(B(free));
            rig.WriteLine(sb.ToString());
            rigRows++;
        }

        public void Event(long k, string kind, string name, string detail)
        {
            events.WriteLine(k.ToString(IC) + "," + Csv(kind) + "," + Csv(name) + "," + Csv(detail));
            eventCount++;
            events.Flush();
        }

        public void Flush()
        {
            try { steps.Flush(); rig.Flush(); events.Flush(); } catch { }
        }

        public void Close()
        {
            try { if (trigger != null && onProbe != null) trigger.probeEnter.Event -= onProbe; } catch { }
            onProbe = null;
            try { if (boxEvents != null) boxEvents.collisionEnter.Unregister(ref boxHandle); } catch { }
            boxEvents = null;
            if (pending.Count > 0) Late(UnityEngine.Random.seed, -1);
            foreach (var w in new[] { steps, events, rig }) { try { w.Flush(); w.Close(); } catch { } }
        }

        // ---------------------------------------------------------------- live comparison with the source run

        public void LoadReference(string srcDir)
        {
            reference = null;
            string path = Path.Combine(srcDir, "steps.csv");
            if (!File.Exists(path)) { warnings.Add("no source steps.csv for the live comparison: " + path); return; }
            var lines = File.ReadAllLines(path);
            if (lines.Length < 2) return;
            var head = lines[0].Split(',');
            var idx = new Dictionary<string, int>();
            for (int i = 0; i < head.Length; i++) idx[head[i]] = i;
            int ik, irf, irl, iru;
            if (!idx.TryGetValue("k", out ik) || !idx.TryGetValue("rng_fixed", out irf) || !idx.TryGetValue("rng_late", out irl)) { warnings.Add("source steps.csv has no k/rng columns"); return; }
            if (!idx.TryGetValue("rng_update", out iru)) iru = -1;
            var cols = new List<int>();
            foreach (var n in new[] { "boy", "bale" }) foreach (var ax in new[] { "_x", "_y", "_z" }) { int c; cols.Add(idx.TryGetValue(n + ax, out c) ? c : -1); }
            for (int i = 0; ; i++)
            {
                int cx;
                string c = "c" + i.ToString("00", IC);
                if (!idx.TryGetValue(c + "_x", out cx)) break;
                cols.Add(cx); cols.Add(idx[c + "_y"]); cols.Add(idx[c + "_z"]);
            }
            reference = new Dictionary<long, RefRow>();
            for (int li = 1; li < lines.Length; li++)
            {
                var s = lines[li].Split(',');
                if (s.Length < head.Length) continue;
                long k;
                if (!long.TryParse(s[ik], NumberStyles.Integer, IC, out k)) continue;
                var r = new RefRow { rngFixed = Int(s[irf]), rngUpdate = iru >= 0 ? Int(s[iru]) : int.MinValue, rngLate = Int(s[irl]), pos = new float[cols.Count] };
                for (int j = 0; j < cols.Count; j++) r.pos[j] = cols[j] >= 0 ? Flt(s[cols[j]]) : float.NaN;
                reference[k] = r;
            }
            refDir = srcDir;
        }

        void Compare(Pending p, int rngLate)
        {
            if (reference == null) return;
            RefRow r;
            if (!reference.TryGetValue(p.k, out r)) return;
            compared++;
            bool diff = false;
            if (r.rngFixed != p.rngFixed) { diff = true; if (kRngFixed < 0) kRngFixed = p.k; }
            if (p.hasUpdate && r.rngUpdate != int.MinValue && r.rngUpdate != p.rngUpdate) { diff = true; if (kRngUpdate < 0) kRngUpdate = p.k; }
            if (r.rngLate != rngLate) { diff = true; if (kRngLate < 0) kRngLate = p.k; }
            int n = Math.Min(r.pos.Length, p.pos.Length);
            bool posDiff = false;
            for (int i = 0; i + 2 < n; i += 3)
            {
                double d = Dist(p.pos, r.pos, i);
                if (double.IsNaN(d)) continue;
                if (d > PosEps) posDiff = true;
                if (i == 0) maxBoy = Math.Max(maxBoy, d);
                else if (i == 3) maxBale = Math.Max(maxBale, d);
                else maxChick = Math.Max(maxChick, d);
            }
            if (posDiff && kPos < 0) kPos = p.k;
            if ((diff || posDiff) && kStar < 0) kStar = p.k;
        }

        static double Dist(float[] a, float[] b, int i)
        {
            if (float.IsNaN(a[i]) || float.IsNaN(b[i])) return double.NaN;
            double dx = (double)a[i] - b[i], dy = (double)a[i + 1] - b[i + 1], dz = (double)a[i + 2] - b[i + 2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public string LiveText()
        {
            if (reference == null) return "no live comparison";
            return "vs " + Path.GetFileName(refDir) + ": compared " + compared + " rows, k* " + K(kStar) + " (rng_fixed " + K(kRngFixed) + ", rng_update " + K(kRngUpdate) + ", rng_late " + K(kRngLate) + ", pos > 1e-4 m " + K(kPos) +
                   "), max boy " + Mm(maxBoy) + " bale " + Mm(maxBale) + " chick " + Mm(maxChick);
        }

        static string K(long k) { return k < 0 ? "none" : k.ToString(IC); }
        static string Mm(double m) { return (m * 1000.0).ToString("0.###", IC) + " mm"; }

        // ---------------------------------------------------------------- run files (inputs, cues, rig) for replays

        public static void WriteInputs(string dir, List<DetInput> list)
        {
            var sb = new StringBuilder("k,x,y,jump,action\n");
            for (int k = 0; k < list.Count; k++) sb.Append(k.ToString(IC)).Append(',').Append(F(list[k].x)).Append(',').Append(F(list[k].y)).Append(',').Append(B(list[k].jump)).Append(',').Append(B(list[k].action)).Append('\n');
            File.WriteAllText(Path.Combine(dir, "inputs.csv"), sb.ToString(), Utf8);
        }

        public static List<DetInput> ReadInputs(string dir, out string error)
        {
            error = null;
            string path = Path.Combine(dir, "inputs.csv");
            if (!File.Exists(path)) { error = "no inputs.csv in " + dir; return null; }
            var list = new List<DetInput>();
            var lines = File.ReadAllLines(path);
            for (int i = 1; i < lines.Length; i++)
            {
                var s = lines[i].Split(',');
                if (s.Length < 5) continue;
                long k;
                if (!long.TryParse(s[0], NumberStyles.Integer, IC, out k) || k < 0) continue;
                while (list.Count <= k) list.Add(new DetInput());
                list[(int)k] = new DetInput { x = Flt(s[1]), y = Flt(s[2]), jump = s[3] == "1", action = s[4] == "1" };
            }
            return list;
        }

        public static void WriteCues(string dir, List<DetCue> cues)
        {
            var sb = new StringBuilder("k\tcommand\n");
            foreach (var c in cues) sb.Append(c.k.ToString(IC)).Append('\t').Append(c.cmd).Append('\n');
            File.WriteAllText(Path.Combine(dir, "cues.tsv"), sb.ToString(), Utf8);
        }

        public static List<DetCue> ReadCues(string dir, out string error)
        {
            error = null;
            string path = Path.Combine(dir, "cues.tsv");
            if (!File.Exists(path)) { error = "no cues.tsv in " + dir; return null; }
            var list = new List<DetCue>();
            var lines = File.ReadAllLines(path);
            for (int i = 1; i < lines.Length; i++)
            {
                int tab = lines[i].IndexOf('\t');
                if (tab <= 0) continue;
                long k;
                if (!long.TryParse(lines[i].Substring(0, tab), NumberStyles.Integer, IC, out k)) continue;
                list.Add(new DetCue { k = k, cmd = lines[i].Substring(tab + 1).Trim() });
            }
            return list;
        }

        public static List<DetRigRow> ReadRig(string dir, out string error)
        {
            error = null;
            string path = Path.Combine(dir, "rig.csv");
            if (!File.Exists(path)) { error = "no rig.csv in " + dir; return null; }
            var list = new List<DetRigRow>();
            var lines = File.ReadAllLines(path);
            for (int i = 1; i < lines.Length; i++)
            {
                var s = lines[i].Split(',');
                if (s.Length < 11) continue;
                int f;
                if (!int.TryParse(s[0], NumberStyles.Integer, IC, out f) || f < 0) continue;
                var r = new DetRigRow { pos = new Vector3(Flt(s[3]), Flt(s[4]), Flt(s[5])), rot = new Quaternion(Flt(s[6]), Flt(s[7]), Flt(s[8]), Flt(s[9])), fov = Flt(s[10]) };
                while (list.Count < f) list.Add(list.Count > 0 ? list[list.Count - 1] : r);
                if (list.Count == f) list.Add(r); else list[f] = r;
            }
            if (list.Count == 0) error = "rig.csv in " + dir + " has no rows";
            return list;
        }

        // ---------------------------------------------------------------- formatting

        static string Cell(FieldInfo f, object o)
        {
            if (f == null || o == null) return "";
            object v;
            try { v = f.GetValue(o); } catch { return ""; }
            if (v == null) return "";
            if (v is bool) return B((bool)v);
            if (v is float) return F((float)v);
            if (v is int) return ((int)v).ToString(IC);
            var col = v as ICollection;
            if (col != null) return col.Count.ToString(IC);
            return Csv(v.ToString());
        }

        static void V(StringBuilder sb, Vector3 v) { sb.Append(F(v.x)).Append(',').Append(F(v.y)).Append(',').Append(F(v.z)).Append(','); }
        public static string F(float v) { return v.ToString("G9", IC); }
        static string B(bool b) { return b ? "1" : "0"; }
        static string Csv(string s) { return string.IsNullOrEmpty(s) ? "" : s.Replace(',', ';').Replace('\n', ' ').Replace('\r', ' '); }
        static float Flt(string s) { float v; return float.TryParse(s, NumberStyles.Float, IC, out v) ? v : float.NaN; }
        static int Int(string s) { int v; return int.TryParse(s, NumberStyles.Integer, IC, out v) ? v : int.MinValue; }

        public static string J(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (ch == '\t') sb.Append("\\t");
                else if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4", IC));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }
    }
}
