using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // MultiCam v2 target resolution and sampling (RIG). Design: docs/MULTICAM_DESIGN.md sections 3.7 and 8.
    // Strict TargetRef resolution: exact path, unique '/'-bounded suffix, unique exact name, [i] same-named sibling (0-based),
    // ::Component, #id; ambiguity or no match is an error with candidates, never a substring fallback. Points: boy bones,
    // huddle cloth, game camera / aim / probe, object pivot, bounds (DECAL_* excluded), com, rb, collider, top, bottom, bone:*.
    // Groups (of / find / list) with filters, hysteresis, modes and smoothing. Samples are cached per step; Step refreshes them
    // once and re-resolves lost objects from the ObjectDatabase snapshot after streaming changes (no synchronous scans).
    // Replay (REPLAY_DESIGN 8.3 RIG, while ReplayHub.replaying): velocities by finite difference over the replay step (frozen
    // Rigidbody.velocity never read), character pos3/centre and velocity from IReplaySource.Character, probe from
    // IReplaySource.Probe, game points from ctx.game (CORE fills it from GameCam) or GameCam, never CameraScript; bones, bounds,
    // pivots and the rb/com points read the replayed transforms; group active/notKinematic filters use the recorded flags.
    // History (finite differences, group smoothing, hysteresis) restarts at the first replay step after Begin and whenever
    // replay time goes backwards, so a plan pass gives the same samples every time. Wall-clock re-resolve and re-search are
    // off in replay. The pure part (FiniteDiff, ReplayClock, ReplayPoints) builds alone with -define:RIG_MATH_ONLY.
    public static class MultiCamTargets
    {
#if !RIG_MATH_ONLY
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly List<Entry> entries = new List<Entry>();
        static readonly List<TargetRef> tracked = new List<TargetRef>();
        static readonly HashSet<TargetRef> activeRefs = new HashSet<TargetRef>();
        static readonly Dictionary<Type, FieldInfo> deadFields = new Dictionary<Type, FieldInfo>();
        static MultiCamSpec active;
        static MultiCamContext ctx;
        static int session = 1, stamp, streamSeen = -1, passSeen = -1;
        static bool recheck, noRegister;
        static bool Live { get { return active != null && Time.realtimeSinceStartup - lastStepAt < 60f; } }
        static float retryAt, lastStepAt = -100f;
        static readonly ReplayClock clock = new ReplayClock();
        static readonly GameCamState replayCam = new GameCamState();
        static readonly Dictionary<int, int> replayIds = new Dictionary<int, int>();
        static SceneReplayer replayIndexed;
        static bool Replaying { get { return ReplayHub.replaying; } }

        public static void Begin(MultiCamSpec spec, SpecReport report)
        {
            if (spec == null) return;
            if (report == null) report = new SpecReport();
            if (Live && spec != active) { CheckOnly(spec, report); return; }
            Reset();
            active = spec;
            streamSeen = Levels.StreamVersion;
            passSeen = ObjectDatabase.completedPasses;
            try { ObjectDatabase.EnsureFresh(3f); } catch (Exception e) { report.Warn("", "object database refresh failed: " + e.Message); }
            var refs = new List<TargetRef>();
            SpecBind.Walk(spec, "", (node, where) =>
            {
                var r = node as TargetRef;
                if (r == null || !activeRefs.Add(r)) return;
                if (string.IsNullOrEmpty(r.where)) r.where = where;
                refs.Add(r);
            });
            foreach (var r in refs)
            {
                bool ok = Resolve(r, report, true);
                var e = r.cache as Entry;
                if (e != null) { try { e.Refresh(null); } catch { } }
                if (ok && !r.isNone && !r.isSelf) report.Note(r.where, Describe(r));
            }
            BuildTracked(spec);
        }

        static void CheckOnly(MultiCamSpec spec, SpecReport report)
        {
            var seen = new HashSet<TargetRef>();
            noRegister = true;
            try
            {
                SpecBind.Walk(spec, "", (node, where) =>
                {
                    var r = node as TargetRef;
                    if (r == null || !seen.Add(r)) return;
                    if (string.IsNullOrEmpty(r.where)) r.where = where;
                    var e = Build(r, report, true);
                    if (e != null && e.ok && !r.isNone && !r.isSelf) report.Note(r.where, r.text + " -> " + e.resolved);
                });
            }
            finally { noRegister = false; }
        }

        public static bool Resolve(TargetRef r, SpecReport report)
        {
            return Resolve(r, report, !Live || activeRefs.Contains(r));
        }

        static bool Resolve(TargetRef r, SpecReport report, bool register)
        {
            if (r == null) return false;
            if (report == null) report = new SpecReport();
            var e = r.cache as Entry;
            if (e != null && e.session == session)
            {
                if (r.cacheVersion != Levels.StreamVersion) { SafeBind(e, report, Live); r.cacheVersion = Levels.StreamVersion; }
                return e.ok;
            }
            e = Build(r, report, Live);
            r.cache = e;
            r.cacheVersion = Levels.StreamVersion;
            if (e != null && register) Register(e);
            return e != null && e.ok;
        }

        public static GameObject ResolveObject(string query, string where, SpecReport report)
        {
            if (report == null) report = new SpecReport();
            Component comp;
            return FindObject(query, where, report, Live, out comp);
        }

        public static void Step(MultiCamContext c)
        {
            ctx = c;
            stamp++;
            lastStepAt = Time.realtimeSinceStartup;
            bool replaying = Replaying;
            if (replaying && c != null) clock.Step(c.t);
            if (Levels.StreamVersion != streamSeen) { streamSeen = Levels.StreamVersion; recheck = true; try { ObjectDatabase.Demand(10f); } catch { } }
            if (ObjectDatabase.completedPasses != passSeen) { passSeen = ObjectDatabase.completedPasses; foreach (var e in entries) { var g = e as GroupEntry; if (g != null) g.passDirty = true; } }
            if (recheck || (!replaying && Time.realtimeSinceStartup >= retryAt)) Recheck(c);
            for (int i = 0; i < entries.Count; i++)
            {
                try { entries[i].Refresh(c); }
                catch (Exception ex) { entries[i].Fail(c, ex); }
            }
        }

        static void Recheck(MultiCamContext c)
        {
            recheck = false;
            retryAt = Time.realtimeSinceStartup + 1f;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (!e.Lost && (e.ok || !e.retryable)) continue;
                string before = e.ok ? e.resolved : "unresolved";
                var rep = new SpecReport();
                bool ok = SafeBind(e, rep, true);
                string after = ok ? e.resolved : "unresolved: " + e.error;
                if (before != after && c != null) c.Log("warn target " + e.text + " re-resolved: " + before + " -> " + after);
            }
        }

        public static bool Sample(TargetRef r, out TargetSample s)
        {
            s = default(TargetSample);
            if (r == null) return false;
            var e = r.cache as Entry;
            if (e == null || e.session != session)
            {
                var rep = new SpecReport();
                Resolve(r, rep, true);
                e = r.cache as Entry;
                if (rep.errors.Count > 0 && ctx != null) ctx.Log("warn target " + r.text + ": " + rep.errors[0]);
            }
            if (e == null) return false;
            try { e.Refresh(ctx); }
            catch (Exception ex) { e.Fail(ctx, ex); }
            s = e.sample;
            return s.ok;
        }

        public static List<TargetRef> Tracked() { return new List<TargetRef>(tracked); }

        public static string Describe(TargetRef r)
        {
            if (r == null) return "-";
            var e = r.cache as Entry;
            if (e == null) return r.text + " -> (not resolved)";
            return r.text + " -> " + e.resolved;
        }

        public static void End()
        {
            foreach (var e in entries)
            {
                try { e.Release(); } catch { }
                if (e.r != null && e.r.cache == e) { e.r.cache = null; e.r.cacheVersion = -1; }
            }
            Reset();
        }

        static void Reset()
        {
            entries.Clear();
            tracked.Clear();
            activeRefs.Clear();
            active = null;
            ctx = null;
            lastStepAt = -100f;
            recheck = false;
            session++;
            stamp = 0;
            clock.Restart();
            replayIds.Clear();
            replayIndexed = null;
        }

        static void Register(Entry e) { if (!noRegister && !entries.Contains(e)) entries.Add(e); }

        static void BuildTracked(MultiCamSpec spec)
        {
            tracked.Clear();
            var texts = new HashSet<string>();
            Action<TargetRef> add = r =>
            {
                if (r == null || r.isNone || r.isSelf) return;
                if (tracked.Contains(r) || !texts.Add(r.text)) return;
                tracked.Add(r);
            };
            foreach (var r in spec.tracks) add(r);
            foreach (var c in spec.cameras)
            {
                if (c == null) continue;
                add(c.target);
                add(c.look);
                foreach (var k in c.keys) if (k != null) add(k.look);
            }
        }

        // ------------------------------------------------------------------ strict object lookup

        static GameObject FindObject(string query, string where, SpecReport report, bool snapshotOnly, out Component comp)
        {
            comp = null;
            if (string.IsNullOrEmpty(query)) { report.Error(where, "empty object path"); return null; }
            string q = query.Trim().Trim('"');
            string compName = null;
            int k = q.IndexOf("::", StringComparison.Ordinal);
            if (k >= 0) { compName = q.Substring(k + 2).Trim(); q = q.Substring(0, k).Trim(); }
            if (compName != null)
            {
                string cb; int ci;
                if (SplitIndex(compName, out cb, out ci)) { compName = cb; q = q + "[" + ci.ToString(IC) + "]"; }
            }
            GameObject go;
            if (q.StartsWith("#", StringComparison.Ordinal)) go = FindById(q, where, report, snapshotOnly);
            else go = FindByPath(q, where, report, snapshotOnly);
            if (go == null) return null;
            if (!string.IsNullOrEmpty(compName))
            {
                comp = FindComponent(go, compName);
                if (comp == null)
                {
                    var names = new List<string>();
                    foreach (var c in go.GetComponents<Component>()) if (c != null) names.Add(c.GetType().Name);
                    report.Error(where, "no component '" + compName + "' on " + Inspector.PathOf(go.transform) + " (has: " + string.Join(", ", names.ToArray()) + ")");
                    return null;
                }
            }
            return go;
        }

        static GameObject FindById(string q, string where, SpecReport report, bool snapshotOnly)
        {
            int id;
            if (!int.TryParse(q.Substring(1), NumberStyles.Integer, IC, out id)) { report.Error(where, "bad instance id '" + q + "'"); return null; }
            report.Warn(where, "instance id targets are session-local: " + q);
            var rec = ObjectDatabase.Get(id);
            if (rec != null && rec.go != null) return rec.go;
            if (!snapshotOnly)
                foreach (var t in Inspector.AllSceneTransforms())
                    if (t != null && t.gameObject.GetInstanceID() == id) return t.gameObject;
            report.Error(where, "no object with instance id " + id.ToString(IC));
            return null;
        }

        static GameObject FindByPath(string q, string where, SpecReport report, bool snapshotOnly)
        {
            var cands = Candidates(q, snapshotOnly);
            int index = -1;
            if (cands.Count == 0)
            {
                string b; int i;
                if (SplitIndex(q, out b, out i)) { cands = Candidates(b, true); index = i; if (cands.Count > 0) q = b; }
            }
            if (cands.Count == 0)
            {
                report.Error(where, "no object '" + q + "'" + Hints(q));
                return null;
            }
            if (index >= 0)
            {
                var parent = cands[0].parent;
                foreach (var t in cands)
                    if (t.parent != parent)
                    {
                        report.Error(where, "'" + q + "' matches objects under several parents; give a longer path before [" + index.ToString(IC) + "]" + List(cands));
                        return null;
                    }
                cands.Sort((a, c) => a.GetSiblingIndex().CompareTo(c.GetSiblingIndex()));
                if (index >= cands.Count) { report.Error(where, "'" + q + "[" + index.ToString(IC) + "]': only " + cands.Count.ToString(IC) + " same-named siblings (index is 0-based)"); return null; }
                return cands[index].gameObject;
            }
            if (cands.Count == 1) return cands[0].gameObject;
            Transform only = null; int activeCount = 0;
            foreach (var t in cands) if (t.gameObject.activeInHierarchy) { activeCount++; only = t; }
            if (activeCount == 1)
            {
                report.Note(where, "'" + q + "' matches " + cands.Count.ToString(IC) + " objects; using the only active one " + Inspector.PathOf(only) + " #" + only.gameObject.GetInstanceID().ToString(IC));
                return only.gameObject;
            }
            report.Error(where, "'" + q + "' is ambiguous: " + cands.Count.ToString(IC) + " matches; use a longer path, [i] or #id" + List(cands));
            return null;
        }

        static List<Transform> Candidates(string q, bool snapshotOnly)
        {
            var res = new List<Transform>();
            if (!snapshotOnly) { try { ObjectDatabase.EnsureFresh(3f); } catch { } }
            bool slash = q.IndexOf('/') >= 0;
            if (ObjectDatabase.Ready)
            {
                if (slash)
                {
                    foreach (var rec in ObjectDatabase.ByPath(q)) if (rec.t != null) res.Add(rec.t);
                    if (res.Count > 0) return res;
                    string suffix = "/" + q;
                    foreach (var rec in ObjectDatabase.all)
                        if (rec.t != null && rec.path != null && rec.path.EndsWith(suffix, StringComparison.Ordinal)) res.Add(rec.t);
                }
                else
                {
                    foreach (var rec in ObjectDatabase.all) if (rec.t != null && rec.name == q) res.Add(rec.t);
                }
                return res;
            }
            if (snapshotOnly) return res;
            var all = Inspector.WalkSceneTransforms();
            if (slash)
            {
                foreach (var t in all) if (t != null && Inspector.PathOf(t) == q) res.Add(t);
                if (res.Count > 0) return res;
                string suffix = "/" + q;
                foreach (var t in all) if (t != null && Inspector.PathOf(t).EndsWith(suffix, StringComparison.Ordinal)) res.Add(t);
            }
            else foreach (var t in all) if (t != null && t.name == q) res.Add(t);
            return res;
        }

        static bool SplitIndex(string s, out string baseName, out int index)
        {
            baseName = s; index = -1;
            if (s == null || s.Length < 4 || s[s.Length - 1] != ']') return false;
            int k = s.LastIndexOf('[');
            if (k <= 0) return false;
            int v;
            if (!int.TryParse(s.Substring(k + 1, s.Length - k - 2), NumberStyles.None, IC, out v)) return false;
            baseName = s.Substring(0, k);
            index = v;
            return true;
        }

        static string Hints(string q)
        {
            if (!ObjectDatabase.Ready) return "";
            int k = q.LastIndexOf('/');
            string last = (k >= 0 ? q.Substring(k + 1) : q).ToLowerInvariant();
            if (last.Length == 0) return "";
            var sb = new StringBuilder();
            int n = 0;
            foreach (var rec in ObjectDatabase.all)
            {
                if (rec.go == null || rec.nameLower == null || rec.nameLower.IndexOf(last, StringComparison.Ordinal) < 0) continue;
                sb.Append(n == 0 ? "; similar: " : ", ").Append(rec.path);
                if (++n >= 6) break;
            }
            return sb.ToString();
        }

        static string List(List<Transform> cands)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < cands.Count && i < 6; i++)
            {
                var t = cands[i];
                sb.Append(i == 0 ? ": " : "; ").Append(Inspector.PathOf(t)).Append(" #").Append(t.gameObject.GetInstanceID().ToString(IC)).Append(t.gameObject.activeInHierarchy ? " active" : " inactive");
            }
            if (cands.Count > 6) sb.Append("; ...");
            return sb.ToString();
        }

        static Component FindComponent(GameObject go, string name)
        {
            var comps = go.GetComponents<Component>();
            foreach (var c in comps) if (c != null && c.GetType().Name == name) return c;
            foreach (var c in comps) if (c != null && string.Equals(c.GetType().Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            foreach (var c in comps)
            {
                if (c == null) continue;
                for (var t = c.GetType().BaseType; t != null && t != typeof(Component); t = t.BaseType)
                    if (t.Name == name) return c;
            }
            return null;
        }

        static Transform FindChild(Transform root, string name, out int count)
        {
            count = 0;
            Transform first = null;
            if (root == null) return null;
            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (t != root && t.name == name) { count++; if (first == null) first = t; }
                for (int i = 0; i < t.childCount; i++) queue.Enqueue(t.GetChild(i));
            }
            return first;
        }

        static List<Renderer> Renderers(GameObject go)
        {
            var list = new List<Renderer>();
            if (go == null) return list;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r is ParticleSystemRenderer || r is ParticleRenderer || r is LineRenderer || r is TrailRenderer || r is BillboardRenderer) continue;
                if (r.gameObject.name.StartsWith("DECAL_", StringComparison.Ordinal)) continue;
                list.Add(r);
                if (list.Count >= 256) break;
            }
            return list;
        }

        static bool RendererBounds(List<Renderer> rends, out Bounds b)
        {
            b = new Bounds();
            bool any = false;
            if (rends == null) return false;
            for (int i = 0; i < rends.Count; i++)
            {
                var r = rends[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any;
        }

        static FieldInfo DeadField(Type t)
        {
            FieldInfo f;
            if (deadFields.TryGetValue(t, out f)) return f;
            f = null;
            for (var ty = t; ty != null && ty != typeof(MonoBehaviour); ty = ty.BaseType)
            {
                var g = ty.GetField("dead", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (g != null && g.FieldType == typeof(bool)) { f = g; break; }
            }
            deadFields[t] = f;
            return f;
        }

        static string V(Vector3 p) { return "(" + p.x.ToString("0.##", IC) + "," + p.y.ToString("0.##", IC) + "," + p.z.ToString("0.##", IC) + ")"; }

        static double Now(MultiCamContext c) { return c != null ? c.t : 0.0; }

        static bool SafeBind(Entry e, SpecReport report, bool snapshotOnly)
        {
            try { return e.Bind(report, snapshotOnly); }
            catch (Exception ex)
            {
                e.ok = false;
                e.error = ex.GetType().Name + ": " + ex.Message;
                report.Error(e.r != null ? e.r.where : "", "target " + e.text + " resolution failed: " + e.error);
                return false;
            }
        }

        static Entry Build(TargetRef r, SpecReport report, bool snapshotOnly)
        {
            Entry e;
            if (r.isGroup) e = new GroupEntry();
            else if (r.fixedPoint) e = new FixedEntry();
            else
            {
                string of = (r.of ?? "").Trim();
                if (of == "none" || of == "self") e = new NoneEntry();
                else if (of == "boy" || of == "huddle" || of == "main") e = new CharEntry();
                else if (of == "game" || of == "gameaim" || of == "probe") e = new GameEntry();
                else e = new ObjectEntry();
            }
            e.r = r;
            e.session = session;
            e.fd.Sync(clock.epoch);
            SafeBind(e, report, snapshotOnly);
            return e;
        }

        // ------------------------------------------------------------------ replay reads (recorded state, never live game logic)

        static Vector3 TransformCom(Rigidbody rb)
        {
            var tr = rb.transform;
            return tr.position + tr.rotation * rb.centerOfMass;
        }

        static string Who(Character ch) { return ch is Huddle ? "huddle" : ch is Boy ? "boy" : ""; }

        static int ReplayId(Transform t, out SceneReplayer rp)
        {
            rp = ReplayHub.source as SceneReplayer;
            if (t == null || rp == null || rp.tracks == null || rp.tracks.rec == null) return -1;
            if (!ReferenceEquals(replayIndexed, rp))
            {
                replayIndexed = rp;
                replayIds.Clear();
                var objs = rp.tracks.rec.objects;
                for (int i = 0; i < objs.Count; i++)
                {
                    if (objs[i] == null) continue;
                    var tr = rp.Resolved(objs[i].id) as Transform;
                    if (tr != null) replayIds[tr.GetInstanceID()] = objs[i].id;
                }
            }
            int id;
            return replayIds.TryGetValue(t.GetInstanceID(), out id) ? id : -1;
        }

        static bool ActiveAt(GameObject go, double t)
        {
            if (go == null) return false;
            SceneReplayer rp = null;
            int id = Replaying ? ReplayId(go.transform, out rp) : -1;
            if (id < 0) return go.activeInHierarchy;
            var tracks = rp.tracks;
            var objs = tracks.rec.objects;
            for (int guard = 0; guard < 256; guard++)
            {
                var o = id < objs.Count ? objs[id] : null;
                bool self;
                if (!tracks.Alive(id, t)) self = false;
                else if (o != null && (o.flags & SrecConst.ObjCamDriven) != 0) { var x = rp.Resolved(id) as Transform; self = x == null || x.gameObject.activeSelf; }
                else self = (tracks.Flags(id, t) & SrecConst.FlActive) != 0;
                if (!self) return false;
                int p = tracks.ParentAt(id, t);
                if (p < 0)
                {
                    var x = rp.Resolved(id) as Transform;
                    return x == null || x.parent == null || x.parent.gameObject.activeInHierarchy;
                }
                id = p;
            }
            return go.activeInHierarchy;
        }

        static bool KinematicAt(Rigidbody rb, double t)
        {
            SceneReplayer rp = null;
            int id = Replaying ? ReplayId(rb.transform, out rp) : -1;
            if (id < 0) return rb.isKinematic;
            return (rp.tracks.Flags(id, t) & SrecConst.FlKinematic) != 0;
        }

        // ------------------------------------------------------------------ entries

        abstract class Entry : ITarget
        {
            public TargetRef r;
            public int session, stamp = -1;
            public bool ok, retryable = true;
            public string error = "";
            public string resolvedText = "";
            public TargetSample sample;
            public readonly FiniteDiff fd = new FiniteDiff();
            bool failLogged;
            float fixedHeight = -1f;

            protected float FixedHeight(bool hb, Bounds b, float fallback)
            {
                if (fixedHeight < 0f && hb && b.size.y > 0.01f) fixedHeight = b.size.y;
                return fixedHeight > 0f ? fixedHeight : fallback;
            }

            public string text { get { return r != null ? r.text : "?"; } }
            public string resolved
            {
                get
                {
                    if (!ok) return "unresolved: " + error;
                    return sample.ok ? resolvedText + " " + V(sample.pos) : resolvedText;
                }
            }

            public bool Sample(MultiCamContext c, out TargetSample s) { Refresh(c); s = sample; return s.ok; }

            public virtual bool Lost { get { return false; } }
            public abstract bool Bind(SpecReport report, bool snapshotOnly);
            protected abstract bool Raw(MultiCamContext c, ref TargetSample s, out bool hasVel);
            public virtual void Release() { }
            protected virtual void Rewind() { }

            protected bool Fail(SpecReport report, string message)
            {
                ok = false;
                error = message;
                report.Error(r != null ? r.where : "", message);
                return false;
            }

            public void Fail(MultiCamContext c, Exception ex)
            {
                sample.ok = false;
                if (failLogged || c == null) return;
                failLogged = true;
                c.Log("warn target " + text + " sampling failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            public void Refresh(MultiCamContext c)
            {
                if (stamp == MultiCamTargets.stamp) return;
                stamp = MultiCamTargets.stamp;
                if (fd.Sync(clock.epoch)) Rewind();
                var s = default(TargetSample);
                s.count = 1;
                bool hasVel = false;
                if (ok)
                {
                    s.ok = Raw(c, ref s, out hasVel);
                }
                if (!s.ok)
                {
                    s.pos = fd.hasPrev ? fd.prevPos : s.pos;
                    s.vel = Vector3.zero;
                    sample = s;
                    return;
                }
                ReplayPoints.Settle(ref s, hasVel, r.offset, Now(c), fd);
                sample = s;
            }
        }

        sealed class NoneEntry : Entry
        {
            public override bool Bind(SpecReport report, bool snapshotOnly) { ok = true; retryable = false; resolvedText = r.of; return true; }
            protected override bool Raw(MultiCamContext c, ref TargetSample s, out bool hasVel) { hasVel = false; return false; }
        }

        sealed class FixedEntry : Entry
        {
            public override bool Bind(SpecReport report, bool snapshotOnly)
            {
                ok = true; retryable = false;
                resolvedText = "fixed point";
                if (!string.IsNullOrEmpty(r.point)) report.Warn(r.where, "point '" + r.point + "' is ignored for a fixed point");
                return true;
            }

            protected override bool Raw(MultiCamContext c, ref TargetSample s, out bool hasVel)
            {
                hasVel = true;
                s.pos = r.pos;
                s.vel = Vector3.zero;
                return true;
            }
        }

        sealed class GameEntry : Entry
        {
            string kind;

            public override bool Bind(SpecReport report, bool snapshotOnly)
            {
                kind = r.of.Trim();
                retryable = false;
                if (!string.IsNullOrEmpty(r.point)) report.Warn(r.where, "point '" + r.point + "' is ignored for '" + kind + "'");
                ok = true;
                resolvedText = kind == "game" ? "rendered game camera position" : kind == "gameaim" ? "CameraScript.positionAim" : "cameraBlendProbe.worldPosition";
                return true;
            }

            protected override bool Raw(MultiCamContext c, ref TargetSample s, out bool hasVel)
            {
                hasVel = false;
                if (Replaying) return ReplayPoints.Game(kind, ReplayHub.source, c != null ? c.game : null, Now(c), replayCam, ref s, out hasVel);
                if (c != null && c.game.ok)
                {
                    var g = c.game;
                    if (kind == "game") { s.pos = g.rendered.p; s.vel = g.velocity; hasVel = true; }
                    else if (kind == "gameaim") s.pos = g.positionAim;
                    else s.pos = g.probe;
                    return true;
                }
                var cs = CameraScript.main;
                if (cs == null) return false;
                if (kind == "game") { s.pos = cs.transform.position; s.vel = cs.cameraVelocity; hasVel = true; return true; }
                if (kind == "gameaim") { s.pos = cs.positionAim; return true; }
                var pr = cs.cameraBlendProbe;
                if (pr == null) return false;
                s.pos = pr.worldPosition;
                return true;
            }
        }

        sealed class CharEntry : Entry
        {
            static readonly string[] BoyPoints = { "", "torso", "head", "neck", "root", "pos3", "pivot", "bounds", "top", "bottom", "probe" };
            static readonly string[] HuddlePoints = { "", "visual", "centre", "center", "pos3", "pivot", "bounds", "top", "bottom", "probe" };
            string kind, point, bone;
            Actor boneActor; int boneIndex = -1; Transform boneTransform; UnityEngine.Object boneOwner;

            public override bool Bind(SpecReport report, bool snapshotOnly)
            {
                kind = r.of.Trim();
                point = (r.point ?? "").Trim();
                bone = point.StartsWith("bone:", StringComparison.Ordinal) ? point.Substring(5).Trim() : null;
                retryable = true;
                if (bone == null)
                {
                    bool boyOk = Array.IndexOf(BoyPoints, point) >= 0, hudOk = Array.IndexOf(HuddlePoints, point) >= 0;
                    if (kind == "boy" && !boyOk) return Fail(report, "unknown boy point '" + point + "' (torso, head, neck, root, pos3, pivot, bounds, top, bottom, probe, bone:<name>)");
                    if (kind == "huddle" && !hudOk) return Fail(report, "unknown huddle point '" + point + "' (visual, centre, pos3, pivot, bounds, top, bottom, probe, bone:<name>)");
                    if (kind == "main" && !boyOk && !hudOk) return Fail(report, "unknown character point '" + point + "'");
                }
                Character ch = Current();
                if (ch == null)
                {
                    if (kind == "main") { ok = true; resolvedText = "main character (none yet)"; report.Warn(r.where, "no main character in the scene yet"); return true; }
                    return Fail(report, "no " + kind + " in the scene");
                }
                if (!ch.gameObject.activeInHierarchy) report.Warn(r.where, kind + " is inactive");
                if (bone != null && kind != "main" && BoneTransform(ch) == null) return Fail(report, kind + " has no bone '" + bone + "'");
                ok = true;
                resolvedText = kind + " " + Inspector.PathOf(ch.transform) + " #" + ch.gameObject.GetInstanceID().ToString(IC) + (ch.gameObject.activeInHierarchy ? " active" : " inactive") + " ." + (point.Length > 0 ? point : (ch is Huddle ? "visual" : "torso"));
                return true;
            }

            Character Current()
            {
                if (kind == "boy") return ScriptGlobals.boy;
                if (kind == "huddle") return ScriptGlobals.huddle;
                return ScriptGlobals.mainCharacter;
            }

            Transform BoneTransform(Character ch)
            {
                if (boneOwner != ch) { boneOwner = ch; Locate(ch); }
                if (boneActor != null && boneIndex >= 0 && boneIndex < boneActor.numBones) return boneActor.bones[boneIndex].transform;
                return boneTransform;
            }

            void Locate(Character ch)
            {
                boneTransform = null; boneActor = null; boneIndex = -1;
                int n;
                var boy = ch as Boy;
                if (boy != null && boy.visualBoy != null)
                {
                    var a = boy.visualBoy.actor;
                    if (a != null && a.numBones > 0)
                    {
                        int i = a.GetBone(bone);
                        if (i >= 0) { boneActor = a; boneIndex = i; return; }
                        boneTransform = FindChild(a.transform, bone, out n);
                        if (boneTransform != null) return;
                    }
                }
                var hud = ch as Huddle;
                if (hud != null && hud.cloth != null && hud.cloth.bones != null)
                    foreach (var cb in hud.cloth.bones)
                        if (cb != null && cb.visualTransform != null && cb.visualTransform.name == bone) { boneTransform = cb.visualTransform; return; }
                boneTransform = FindChild(ch.transform.parent != null ? ch.transform.parent : ch.transform, bone, out n);
            }

            protected override bool Raw(MultiCamContext c, ref TargetSample s, out bool hasVel)
            {
                hasVel = true;
                var ch = Current();
                if (ch == null) return false;
                s.go = ch.gameObject;
                var boy = ch as Boy;
                var hud = ch as Huddle;
                Vector3 pos3;
                if (Replaying)
                {
                    Vector3 v3;
                    hasVel = ReplayPoints.Character(ReplayHub.source, Who(ch), Now(c), out pos3, out v3);
                    if (!hasVel) pos3 = ch.transform.position;
                    s.vel = v3;
                }
                else { pos3 = ch.pos3; s.vel = ch.velocity3; }
                Bounds b;
                bool hb = boy != null ? BoyBounds(boy, out b) : HuddleBounds(hud, out b);
                s.hasBounds = hb;
                s.bounds = b;
                s.height = point == "bounds" && hb ? b.size.y : boy != null ? MultiCamConst.BoyHeight : FixedHeight(hb, b, MultiCamConst.BoyHeight);
                if (bone != null)
                {
                    var bt = BoneTransform(ch);
                    if (bt == null) return false;
                    s.pos = bt.position;
                    if (boy != null) s.height = MultiCamConst.BoyHeight;
                    return true;
                }
                string p = point;
                if (p == "pos3" || p == "centre" || p == "center") { s.pos = pos3; return true; }
                if (p == "pivot") { s.pos = ch.transform.position; return true; }
                if (p == "probe") return Probe(c, ref s);
                if (p == "bounds" && hb) { s.pos = b.center; return true; }
                if (p == "top" && hb) { s.pos = new Vector3(b.center.x, b.max.y, b.center.z); return true; }
                if (p == "bottom" && hb) { s.pos = new Vector3(b.center.x, b.min.y, b.center.z); return true; }
                if (boy != null)
                {
                    s.height = p == "bounds" && hb ? b.size.y : MultiCamConst.BoyHeight;
                    var sb = SB(boy);
                    if (p == "head") return BonePos(boy, sb != null ? sb.head : -1, 1.2f, pos3, ref s);
                    if (p == "neck") return BonePos(boy, sb != null ? sb.neck : -1, 1.05f, pos3, ref s);
                    if (p == "root") return BonePos(boy, sb != null ? sb.spineRoot : -1, 0.55f, pos3, ref s);
                    return BonePos(boy, sb != null ? sb.torso : -1, 0.75f, pos3, ref s);
                }
                if (hud != null) { s.pos = HuddleVisual(hud, pos3); return true; }
                return false;
            }

            static bool Probe(MultiCamContext c, ref TargetSample s)
            {
                if (Replaying)
                {
                    Vector3 rp;
                    if (ReplayPoints.Probe(ReplayHub.source, Now(c), out rp)) { s.pos = rp; return true; }
                    if (c != null && c.game.ok) { s.pos = c.game.probe; return true; }
                    return false;
                }
                if (c != null && c.game.ok) { s.pos = c.game.probe; return true; }
                var cs = CameraScript.main;
                if (cs == null || cs.cameraBlendProbe == null) return false;
                s.pos = cs.cameraBlendProbe.worldPosition;
                return true;
            }

            static SpecificBones SB(Boy boy)
            {
                var vb = boy.visualBoy;
                if (vb == null) return null;
                var a = vb.actor;
                return a != null && a.numBones > 0 ? a.specificBones : null;
            }

            static Transform Bone(Boy boy, int index)
            {
                if (index < 0 || boy.visualBoy == null) return null;
                var a = boy.visualBoy.actor;
                if (a == null || index >= a.numBones) return null;
                return a.bones[index].transform;
            }

            static bool BonePos(Boy boy, int index, float fallbackUp, Vector3 pos3, ref TargetSample s)
            {
                var t = Bone(boy, index);
                s.pos = t != null ? t.position : pos3 + Vector3.up * fallbackUp;
                return true;
            }

            static void Add(Boy boy, int index, ref Bounds b, ref bool any)
            {
                var t = Bone(boy, index);
                if (t == null) return;
                if (!any) { b = new Bounds(t.position, Vector3.zero); any = true; } else b.Encapsulate(t.position);
            }

            static void AddPair(Boy boy, ConstArray<int> pair, ref Bounds b, ref bool any)
            {
                if (!pair.initialized) return;
                for (int i = 0; i < pair.Length; i++) Add(boy, pair[i], ref b, ref any);
            }

            static bool BoyBounds(Boy boy, out Bounds b)
            {
                b = new Bounds();
                bool any = false;
                var sb = SB(boy);
                if (sb == null) return false;
                Add(boy, sb.head, ref b, ref any);
                Add(boy, sb.neck, ref b, ref any);
                Add(boy, sb.spineRoot, ref b, ref any);
                Add(boy, sb.spineA, ref b, ref any);
                Add(boy, sb.spineB, ref b, ref any);
                Add(boy, sb.spineC, ref b, ref any);
                AddPair(boy, sb.clavicles, ref b, ref any);
                AddPair(boy, sb.elbows, ref b, ref any);
                AddPair(boy, sb.wrists, ref b, ref any);
                AddPair(boy, sb.hips, ref b, ref any);
                AddPair(boy, sb.knees, ref b, ref any);
                AddPair(boy, sb.ankles, ref b, ref any);
                AddPair(boy, sb.balls, ref b, ref any);
                if (any) b.Expand(0.2f);
                return any;
            }

            static bool HuddleBounds(Huddle hud, out Bounds b)
            {
                b = new Bounds();
                if (hud == null || hud.cloth == null || hud.cloth.bones == null) return false;
                bool any = false;
                foreach (var cb in hud.cloth.bones)
                {
                    if (cb == null || cb.visualTransform == null) continue;
                    var p = cb.visualTransform.position;
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
                if (any) b.Expand(0.6f);
                return any;
            }

            static Vector3 HuddleVisual(Huddle hud, Vector3 pos3)
            {
                if (hud.cloth == null || hud.cloth.bones == null) return pos3;
                var sum = Vector3.zero;
                int n = 0;
                foreach (var cb in hud.cloth.bones)
                {
                    if (cb == null || cb.visualTransform == null) continue;
                    sum += cb.visualTransform.position;
                    n++;
                }
                return n > 0 ? sum / n : pos3;
            }
        }

        sealed class ObjectEntry : Entry
        {
            static readonly string[] Points = { "", "pivot", "bounds", "com", "rb", "collider", "top", "bottom" };
            GameObject go;
            Component comp;
            Rigidbody rb;
            Collider col;
            Character ch;
            Transform bone;
            string point;
            List<Renderer> rends;

            public override bool Lost { get { return ok && (object)go != null && go == null; } }

            public override bool Bind(SpecReport report, bool snapshotOnly)
            {
                point = (r.point ?? "").Trim();
                string boneName = point.StartsWith("bone:", StringComparison.Ordinal) ? point.Substring(5).Trim() : null;
                if (boneName == null && Array.IndexOf(Points, point) < 0) { retryable = false; return Fail(report, "unknown object point '" + point + "' (pivot, bounds, com, rb, collider, top, bottom, bone:<child>)"); }
                Component c;
                var g = FindObject(r.of, r.where, report, snapshotOnly, out c);
                if (g == null) { ok = false; error = report.errors.Count > 0 ? report.errors[report.errors.Count - 1] : "not found"; return false; }
                go = g; comp = c;
                rb = comp as Rigidbody;
                if (rb == null) rb = go.GetComponent<Rigidbody>();
                if (rb == null && (point == "com" || point == "rb")) rb = go.GetComponentInChildren<Rigidbody>();
                col = comp as Collider;
                if (col == null) col = go.GetComponent<Collider>();
                if (col == null && point == "collider") col = go.GetComponentInChildren<Collider>();
                ch = go.GetComponent<Character>();
                rends = Renderers(go);
                if ((point == "com" || point == "rb") && rb == null) return Fail(report, "no Rigidbody on " + Inspector.PathOf(go.transform) + " for point '" + point + "'");
                if (point == "collider" && col == null) return Fail(report, "no Collider on " + Inspector.PathOf(go.transform));
                if ((point == "bounds" || point == "top" || point == "bottom") && rends.Count == 0) report.Warn(r.where, "no renderers under " + Inspector.PathOf(go.transform) + "; '" + point + "' falls back to the pivot");
                bone = null;
                if (boneName != null)
                {
                    int n;
                    bone = FindChild(go.transform, boneName, out n);
                    if (bone == null) return Fail(report, "no child '" + boneName + "' under " + Inspector.PathOf(go.transform));
                    if (n > 1) report.Warn(r.where, n.ToString(IC) + " children named '" + boneName + "'; using the first (breadth-first)");
                }
                ok = true;
                resolvedText = Inspector.PathOf(go.transform) + (comp != null ? "::" + comp.GetType().Name : "") + " #" + go.GetInstanceID().ToString(IC) + (go.activeInHierarchy ? " active" : " inactive") + " ." + (point.Length > 0 ? point : rb != null ? "com (default)" : "pivot");
                return true;
            }

            protected override bool Raw(MultiCamContext c, ref TargetSample s, out bool hasVel)
            {
                hasVel = false;
                if (go == null) return false;
                s.go = go;
                bool replaying = Replaying;
                Bounds b;
                bool hb = RendererBounds(rends, out b);
                s.hasBounds = hb;
                s.bounds = b;
                s.height = (point == "bounds" || point == "top" || point == "bottom") && hb ? b.size.y : FixedHeight(hb, b, 0f);
                if (bone != null) s.pos = bone.position;
                else if (point == "bounds") s.pos = hb ? b.center : go.transform.position;
                else if (point == "top") s.pos = hb ? new Vector3(b.center.x, b.max.y, b.center.z) : go.transform.position;
                else if (point == "bottom") s.pos = hb ? new Vector3(b.center.x, b.min.y, b.center.z) : go.transform.position;
                else if (point == "com" || (point.Length == 0 && rb != null)) s.pos = replaying ? TransformCom(rb) : rb.worldCenterOfMass;
                else if (point == "rb") s.pos = replaying ? rb.transform.position : rb.position;
                else if (point == "collider") s.pos = col != null ? col.bounds.center : go.transform.position;
                else s.pos = go.transform.position;
                if (replaying)
                {
                    Vector3 p3, v3;
                    if (ch != null && ReplayPoints.Character(ReplayHub.source, Who(ch), Now(c), out p3, out v3)) { s.vel = v3; hasVel = true; }
                    return true;
                }
                if (rb != null && !rb.isKinematic)
                {
                    s.vel = point == "com" || point == "rb" || point.Length == 0 ? rb.velocity : rb.GetPointVelocity(s.pos);
                    hasVel = true;
                }
                else if (ch != null) { s.vel = ch.velocity3; hasVel = true; }
                return true;
            }
        }

        sealed class GroupEntry : Entry
        {
            sealed class Member
            {
                public GameObject go;
                public Component comp, deadOwner;
                public FieldInfo dead;
                public Rigidbody rb;
                public List<Renderer> rends;
                public Entry entry;
                public bool inGroup, seen, hasB;
                public double lastPass = -1e9;
                public Vector3 pos, vel;
                public Bounds b;
                public float speed;
                public readonly FiniteDiff fd = new FiniteDiff();

                public void Rewind() { fd.Reset(); inGroup = false; lastPass = -1e9; vel = Vector3.zero; speed = 0f; }
            }

            GroupSpec g;
            TargetRef nearRef;
            string typeName, searchQuery, memberPoint, resultPoint;
            GameObject listObj;
            Component listComp;
            FieldInfo listField;
            PropertyInfo listProp;
            readonly Dictionary<int, Member> members = new Dictionary<int, Member>();
            readonly List<Member> fixedMembers = new List<Member>();
            readonly List<Member> selected = new List<Member>();
            public bool passDirty;
            bool searchDirty = true;
            float searchAt = -100f;
            bool init;
            Vector3 curPos, velPos, curExt, velExt;
            double lastT;

            public override bool Bind(SpecReport report, bool snapshotOnly)
            {
                g = r.group;
                memberPoint = (g.point ?? "").Trim();
                resultPoint = (r.point ?? "").Trim();
                nearRef = g.filter != null ? g.filter.near : null;
                members.Clear(); fixedMembers.Clear(); init = false;
                int sources = (g.of.Count > 0 ? 1 : 0) + (g.find.Length > 0 ? 1 : 0) + (g.list.Length > 0 ? 1 : 0);
                if (sources != 1) { retryable = false; return Fail(report, "group needs exactly one of 'of', 'find', 'list'"); }
                string[] modes = { "centroid", "weighted", "bounds", "nearest", "fastest" };
                if (Array.IndexOf(modes, g.mode) < 0) { retryable = false; return Fail(report, "unknown group mode '" + g.mode + "' (centroid, weighted, bounds, nearest, fastest)"); }
                string[] pts = { "", "pivot", "bounds", "com", "rb", "collider", "top", "bottom" };
                if (Array.IndexOf(pts, memberPoint) < 0) { retryable = false; return Fail(report, "unknown group member point '" + memberPoint + "'"); }
                if (nearRef != null) Resolve(nearRef, report);
                if (g.of.Count > 0)
                {
                    int okCount = 0;
                    foreach (var m in g.of)
                    {
                        if (m == null) continue;
                        if (string.IsNullOrEmpty(m.where)) m.where = r.where + ".group.of";
                        Resolve(m, report);
                        var me = m.cache as Entry;
                        if (me == null) continue;
                        if (me.ok) okCount++;
                        fixedMembers.Add(new Member { entry = me });
                    }
                    if (okCount == 0) return Fail(report, "no group member resolved");
                    resolvedText = "group of " + okCount.ToString(IC) + "/" + g.of.Count.ToString(IC) + " members, mode " + g.mode;
                    ok = true;
                    return true;
                }
                if (g.find.Length > 0)
                {
                    var words = new List<string>();
                    typeName = null;
                    foreach (var w in g.find.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (w.StartsWith("t:", StringComparison.OrdinalIgnoreCase)) { typeName = w.Substring(2); words.Add(w); }
                        else if (w == "active") continue;
                        else words.Add(w);
                    }
                    searchQuery = string.Join(" ", words.ToArray());
                    if (searchQuery.Length == 0) { retryable = false; return Fail(report, "group find query is empty"); }
                    searchDirty = true;
                    int n = Search();
                    if (n == 0) report.Warn(r.where, "group find '" + g.find + "' matches nothing yet");
                    ok = true;
                    resolvedText = "group find '" + g.find + "' (" + n.ToString(IC) + " candidates), mode " + g.mode;
                    return true;
                }
                string q = g.list.Trim();
                int k = q.IndexOf("::", StringComparison.Ordinal);
                int d = k >= 0 ? q.IndexOf('.', k + 2) : -1;
                if (k < 0 || d < 0) { retryable = false; return Fail(report, "group list expects \"path::Component.field\""); }
                string objPath = q.Substring(0, k), compName = q.Substring(k + 2, d - k - 2), member = q.Substring(d + 1);
                Component c;
                listObj = FindObject(objPath + "::" + compName, r.where + ".group.list", report, snapshotOnly, out c);
                if (listObj == null || c == null) { ok = false; error = "list object not found"; return false; }
                listComp = c;
                listField = null; listProp = null;
                for (var t = c.GetType(); t != null && listField == null && listProp == null; t = t.BaseType)
                {
                    listField = t.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (listField == null) listProp = t.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                if (listField == null && listProp == null) { retryable = false; return Fail(report, "no field or property '" + member + "' on " + compName); }
                var vt = listField != null ? listField.FieldType : listProp.PropertyType;
                if (!typeof(IEnumerable).IsAssignableFrom(vt)) { retryable = false; return Fail(report, compName + "." + member + " is not a list"); }
                ok = true;
                resolvedText = "group list " + Inspector.PathOf(listObj.transform) + "::" + compName + "." + member + ", mode " + g.mode;
                return true;
            }

            public override bool Lost { get { return ok && (object)listComp != null && listComp == null; } }

            protected override void Rewind()
            {
                init = false;
                curPos = velPos = curExt = velExt = Vector3.zero;
                lastT = 0.0;
                foreach (var m in members.Values) m.Rewind();
                foreach (var m in fixedMembers) m.Rewind();
            }

            int Search()
            {
                searchDirty = false;
                passDirty = false;
                searchAt = Time.realtimeSinceStartup;
                foreach (var m in members.Values) m.seen = false;
                int n = 0;
                foreach (var rec in ObjectDatabase.Search(searchQuery, 1024))
                {
                    if (rec.go == null) continue;
                    Component c = null;
                    if (typeName != null)
                    {
                        c = FindComponent(rec.go, typeName);
                        if (c == null) continue;
                    }
                    Touch(rec.go, c);
                    n++;
                }
                Prune();
                return n;
            }

            void Touch(GameObject go, Component c)
            {
                int id = go.GetInstanceID();
                Member m;
                if (!members.TryGetValue(id, out m))
                {
                    m = new Member { go = go, comp = c };
                    m.rb = c as Rigidbody;
                    if (m.rb == null) m.rb = go.GetComponent<Rigidbody>();
                    foreach (var mb in go.GetComponents<MonoBehaviour>())
                    {
                        if (mb == null) continue;
                        var f = DeadField(mb.GetType());
                        if (f != null) { m.dead = f; m.deadOwner = mb; break; }
                    }
                    members[id] = m;
                }
                m.seen = true;
            }

            void Prune()
            {
                List<int> gone = null;
                foreach (var kv in members) if (!kv.Value.seen || kv.Value.go == null) { if (gone == null) gone = new List<int>(); gone.Add(kv.Key); }
                if (gone != null) foreach (var id in gone) members.Remove(id);
            }

            void ReadList()
            {
                foreach (var m in members.Values) m.seen = false;
                if (listComp == null) { Prune(); return; }
                object v = listField != null ? listField.GetValue(listComp) : listProp.GetValue(listComp, null);
                var en = v as IEnumerable;
                if (en != null)
                    foreach (var item in en)
                    {
                        var c = item as Component;
                        var go = c != null ? c.gameObject : item as GameObject;
                        if (go != null) Touch(go, c);
                    }
                Prune();
            }

            bool MemberSample(Member m, MultiCamContext c, double t)
            {
                if (m.entry != null)
                {
                    m.entry.Refresh(c);
                    var es = m.entry.sample;
                    if (!es.ok) return false;
                    m.pos = es.pos; m.vel = es.vel; m.hasB = es.hasBounds; m.b = es.bounds; m.go = es.go;
                    m.speed = m.vel.magnitude;
                    return true;
                }
                if (m.go == null) return false;
                bool replaying = Replaying;
                bool needB = memberPoint == "bounds" || memberPoint == "top" || memberPoint == "bottom" || g.mode == "bounds";
                if (needB && m.rends == null) m.rends = Renderers(m.go);
                Bounds b = new Bounds();
                m.hasB = needB && RendererBounds(m.rends, out b);
                m.b = b;
                var tr = m.go.transform;
                if (memberPoint == "bounds") m.pos = m.hasB ? b.center : tr.position;
                else if (memberPoint == "top") m.pos = m.hasB ? new Vector3(b.center.x, b.max.y, b.center.z) : tr.position;
                else if (memberPoint == "bottom") m.pos = m.hasB ? new Vector3(b.center.x, b.min.y, b.center.z) : tr.position;
                else if (memberPoint == "com" && m.rb != null) m.pos = replaying ? TransformCom(m.rb) : m.rb.worldCenterOfMass;
                else if (memberPoint == "rb" && m.rb != null) m.pos = replaying ? m.rb.transform.position : m.rb.position;
                else if (memberPoint == "collider") { var col = m.comp as Collider ?? m.go.GetComponent<Collider>(); m.pos = col != null ? col.bounds.center : tr.position; }
                else m.pos = tr.position;
                bool given = !replaying && m.rb != null && !m.rb.isKinematic;
                m.vel = m.fd.Step(m.pos, t, given ? m.rb.velocity : m.vel, given, false);
                m.speed = m.vel.magnitude;
                return true;
            }

            bool Hard(Member m, double t)
            {
                if (m.entry == null && m.go == null) return false;
                var f = g.filter;
                if (f == null) return true;
                if (f.active && m.go != null && !ActiveAt(m.go, t)) return false;
                if (f.alive && m.dead != null && m.deadOwner != null && (bool)m.dead.GetValue(m.deadOwner)) return false;
                return true;
            }

            bool Soft(Member m, bool nearOk, Vector3 nearPos, double t)
            {
                var f = g.filter;
                if (f == null) return true;
                if (f.notKinematic && (m.rb == null || KinematicAt(m.rb, t))) return false;
                if (f.minSpeed > 0f && m.speed < f.minSpeed) return false;
                if (f.boxSize != Vector3.zero)
                {
                    var d = m.pos - f.boxCenter;
                    var h = f.boxSize * 0.5f;
                    if (Mathf.Abs(d.x) > Mathf.Abs(h.x) || Mathf.Abs(d.y) > Mathf.Abs(h.y) || Mathf.Abs(d.z) > Mathf.Abs(h.z)) return false;
                }
                if (f.maxDist > 0f && nearOk && (m.pos - nearPos).magnitude > f.maxDist) return false;
                return true;
            }

            protected override bool Raw(MultiCamContext c, ref TargetSample s, out bool hasVel)
            {
                hasVel = true;
                double t = Now(c);
                if (g.find.Length > 0 && (Replaying ? searchDirty : (searchDirty || passDirty) && Time.realtimeSinceStartup - searchAt >= 1f)) Search();
                if (g.list.Length > 0) ReadList();
                TargetSample ns = default(TargetSample);
                bool nearOk = nearRef != null && MultiCamTargets.Sample(nearRef, out ns);
                Vector3 nearPos = nearOk ? ns.pos : curPos;
                selected.Clear();
                IEnumerable<Member> pool = fixedMembers.Count > 0 ? (IEnumerable<Member>)fixedMembers : members.Values;
                foreach (var m in pool)
                {
                    if (!MemberSample(m, c, t) || !Hard(m, t)) { m.inGroup = false; continue; }
                    if (Soft(m, nearOk, nearPos, t)) { m.inGroup = true; m.lastPass = t; }
                    else if (m.inGroup && t - m.lastPass >= g.hold) m.inGroup = false;
                    if (m.inGroup) selected.Add(m);
                }
                if (g.max > 0 && selected.Count > g.max)
                {
                    selected.Sort((a, b) => b.speed.CompareTo(a.speed));
                    selected.RemoveRange(g.max, selected.Count - g.max);
                }
                s.count = selected.Count;
                if (selected.Count == 0) { s.pos = curPos; return false; }
                Vector3 pos = Vector3.zero, vel = Vector3.zero;
                Bounds ub = new Bounds();
                bool hb = false;
                if (g.mode == "nearest" || g.mode == "fastest")
                {
                    Member best = null;
                    float bestV = float.MaxValue;
                    var refPos = nearOk ? ns.pos : (init ? curPos : selected[0].pos);
                    foreach (var m in selected)
                    {
                        float v = g.mode == "fastest" ? -m.speed : (m.pos - refPos).sqrMagnitude;
                        if (v < bestV) { bestV = v; best = m; }
                    }
                    pos = best.pos; vel = best.vel;
                    hb = best.hasB; ub = best.b;
                    s.go = best.go;
                }
                else
                {
                    float wsum = 0f;
                    foreach (var m in selected)
                    {
                        float w = g.mode == "weighted" ? m.speed + 0.05f : 1f;
                        pos += m.pos * w; vel += m.vel * w; wsum += w;
                        var mb = m.hasB ? m.b : new Bounds(m.pos, Vector3.zero);
                        if (!hb) { ub = mb; hb = true; } else ub.Encapsulate(mb);
                    }
                    pos /= wsum; vel /= wsum;
                    if (g.mode == "bounds") pos = ub.center;
                    s.go = selected[0].go;
                }
                float dt = init ? (float)Math.Max(0.0, t - lastT) : 0f;
                var ext = hb ? ub.extents : Vector3.zero;
                if (!init || g.smooth <= 0f) { curPos = pos; curExt = ext; velPos = Vector3.zero; velExt = Vector3.zero; init = true; }
                else if (dt > 0f)
                {
                    curPos = Vector3.SmoothDamp(curPos, pos, ref velPos, g.smooth, Mathf.Infinity, dt);
                    curExt = Vector3.SmoothDamp(curExt, ext, ref velExt, g.smooth, Mathf.Infinity, dt);
                }
                lastT = t;
                s.vel = vel;
                s.hasBounds = hb;
                if (hb)
                {
                    var shift = curPos - (g.mode == "bounds" ? ub.center : pos);
                    s.bounds = new Bounds(ub.center + shift, curExt * 2f);
                    s.height = s.bounds.size.y;
                }
                s.pos = curPos;
                if (hb && resultPoint == "top") s.pos = new Vector3(curPos.x, s.bounds.max.y, curPos.z);
                else if (hb && resultPoint == "bottom") s.pos = new Vector3(curPos.x, s.bounds.min.y, curPos.z);
                return true;
            }
        }
#endif

        // ------------------------------------------------------------------ replay sampling helpers (pure: managed math, IReplaySource)

        internal sealed class FiniteDiff
        {
            public bool hasPrev, hasFd;
            public Vector3 prevPos, prevFd, lastVel;
            public double prevT;
            int epoch;

            public bool Sync(int now)
            {
                if (epoch == now) return false;
                epoch = now;
                Reset();
                return true;
            }

            public void Reset()
            {
                hasPrev = false;
                hasFd = false;
                prevPos = Vector3.zero;
                prevFd = Vector3.zero;
                lastVel = Vector3.zero;
                prevT = 0.0;
            }

            public Vector3 Step(Vector3 pos, double t, Vector3 given, bool hasGiven, bool averaged)
            {
                Vector3 vel;
                if (hasGiven) vel = given;
                else if (hasPrev && t > prevT + 1e-6)
                {
                    var d = (pos - prevPos) / (float)(t - prevT);
                    vel = averaged && hasFd ? 0.5f * (d + prevFd) : d;
                    prevFd = d;
                    hasFd = true;
                }
                else vel = lastVel;
                if (!hasPrev || t > prevT + 1e-6) { prevPos = pos; prevT = t; hasPrev = true; }
                lastVel = vel;
                return vel;
            }
        }

        internal sealed class ReplayClock
        {
            public int epoch;
            bool fresh = true;
            double last = double.NaN;

            public void Restart() { fresh = true; last = double.NaN; }

            public void Step(double t)
            {
                if (fresh || (!double.IsNaN(last) && t < last - 1e-9)) epoch++;
                fresh = false;
                last = t;
            }
        }

        internal static class ReplayPoints
        {
            public static bool Cam(IReplaySource src, double t, GameCamState g)
            {
                if (src == null || g == null) return false;
                try { return src.GameCam(t, g) && g.ok; }
                catch { return false; }
            }

            public static bool Probe(IReplaySource src, double t, out Vector3 probe)
            {
                probe = Vector3.zero;
                if (src == null) return false;
                try { return src.Probe(t, out probe); }
                catch { probe = Vector3.zero; return false; }
            }

            public static bool Character(IReplaySource src, string who, double t, out Vector3 pos3, out Vector3 vel3)
            {
                pos3 = Vector3.zero;
                vel3 = Vector3.zero;
                if (src == null || string.IsNullOrEmpty(who)) return false;
                string state;
                try { return src.Character(who, t, out pos3, out vel3, out state); }
                catch { pos3 = Vector3.zero; vel3 = Vector3.zero; return false; }
            }

            public static bool Game(string kind, IReplaySource src, GameCamState game, double t, GameCamState scratch, ref TargetSample s, out bool hasVel)
            {
                hasVel = false;
                Vector3 p;
                if (kind == "probe" && Probe(src, t, out p)) { s.pos = p; return true; }
                var g = game != null && game.ok ? game : Cam(src, t, scratch) ? scratch : null;
                if (g == null) return false;
                if (kind == "game") { s.pos = g.rendered.p; s.vel = g.velocity; hasVel = true; }
                else if (kind == "gameaim") s.pos = g.positionAim;
                else s.pos = g.probe;
                return true;
            }

            public static void Settle(ref TargetSample s, bool hasVel, Vector3 offset, double t, FiniteDiff fd)
            {
                s.pos += offset;
                if (s.hasBounds) s.bounds.center += offset;
                s.vel = fd.Step(s.pos, t, s.vel, hasVel, true);
            }
        }
    }
}
