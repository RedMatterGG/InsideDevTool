// ReplayLab helpers: reflection on game types, object lookup, pose parsing, numbers for result.json.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev.ReplayLab
{
    public struct LabPose
    {
        public Vector3 p; public Quaternion r; public float fov;
        public static LabPose Of(Camera c) { return new LabPose { p = c.transform.position, r = c.transform.rotation, fov = c.fieldOfView }; }
        public Json.Obj ToJson()
        {
            var e = r.eulerAngles;
            var o = new Json.Obj();
            o["pos"] = LabUtil.Vec(p); o["yaw"] = LabUtil.Num(LabUtil.Signed(e.y)); o["pitch"] = LabUtil.Num(LabUtil.Signed(e.x)); o["fov"] = LabUtil.Num(fov);
            return o;
        }
    }

    public sealed class LabPrep : Exception { public LabPrep(string m) : base(m) { } }

    public static class LabUtil
    {
        public static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static object Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return null;
            return Math.Round(v, 6);
        }

        public static List<object> Vec(Vector3 v) { return new List<object> { Num(v.x), Num(v.y), Num(v.z) }; }
        public static List<object> V2(Vector2 v) { return new List<object> { Num(v.x), Num(v.y) }; }
        public static List<object> Strs(IEnumerable<string> s) { var l = new List<object>(); foreach (var x in s) l.Add(x); return l; }

        public static float Signed(float a) { return a > 180f ? a - 360f : a; }
        public static string F(double v, string fmt) { return v.ToString(fmt, IC); }

        public static FieldInfo Field(Type t, string name)
        {
            for (var ty = t; ty != null; ty = ty.BaseType)
            {
                var f = ty.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        public static object Get(object o, string name)
        {
            if (o == null) return null;
            var f = Field(o.GetType(), name);
            return f != null ? f.GetValue(o) : null;
        }

        public static bool Set(object o, string name, object v)
        {
            if (o == null) return false;
            var f = Field(o.GetType(), name);
            if (f == null) return false;
            f.SetValue(o, v);
            return true;
        }

        public static bool HasMethod(Type t, string name)
        {
            for (var ty = t; ty != null && ty != typeof(MonoBehaviour) && ty != typeof(object); ty = ty.BaseType)
                if (ty.GetMethod(name, All | BindingFlags.DeclaredOnly) != null) return true;
            return false;
        }

        public static string PathOf(Transform t) { return t == null ? "-" : Inspector.PathOf(t); }

        public static string ShortPath(Transform t)
        {
            var p = PathOf(t);
            int i = p.IndexOf("/_GameLogic/", StringComparison.Ordinal);
            return i >= 0 ? p.Substring(i + 1) : p;
        }

        public static GameObject Find(string q)
        {
            if (string.IsNullOrEmpty(q)) return null;
            try { return Remote.Resolve(q); } catch { return null; }
        }

        public static T FindComp<T>(string q) where T : Component
        {
            var g = Find(q);
            return g != null ? g.GetComponent<T>() : null;
        }

        public static bool TryPose(string s, out LabPose pose)
        {
            pose = new LabPose();
            if (string.IsNullOrEmpty(s)) return false;
            var p = s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 5) return false;
            var v = new float[p.Length];
            for (int i = 0; i < p.Length; i++) if (!float.TryParse(p[i], NumberStyles.Float, IC, out v[i])) return false;
            pose.p = new Vector3(v[0], v[1], v[2]);
            pose.r = Quaternion.Euler(v[4], v[3], p.Length > 6 ? v[6] : 0f);
            pose.fov = p.Length > 5 ? v[5] : -1f;
            return true;
        }

        public static bool TryVec(string s, out Vector3 v)
        {
            v = Vector3.zero;
            if (string.IsNullOrEmpty(s)) return false;
            var p = s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            float x, y, z;
            if (p.Length < 3 || !float.TryParse(p[0], NumberStyles.Float, IC, out x) || !float.TryParse(p[1], NumberStyles.Float, IC, out y) || !float.TryParse(p[2], NumberStyles.Float, IC, out z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        public static string PoseText(LabPose q)
        {
            var e = q.r.eulerAngles;
            return F(q.p.x, "0.###") + "," + F(q.p.y, "0.###") + "," + F(q.p.z, "0.###") + "," + F(Signed(e.y), "0.##") + "," + F(Signed(e.x), "0.##") + "," + F(q.fov, "0.##");
        }

        public static LabPose LookAt(Vector3 from, Vector3 at, float fov)
        {
            var d = at - from;
            if (d.sqrMagnitude < 1e-8f) d = Vector3.forward;
            return new LabPose { p = from, r = Quaternion.LookRotation(d.normalized, Vector3.up), fov = fov };
        }

        public static bool Bounds(GameObject g, out Bounds b)
        {
            b = new Bounds();
            bool any = false;
            if (g == null) return false;
            foreach (var r in g.GetComponentsInChildren<Renderer>(true))
            {
                if (!Shows(r) || r is ParticleSystemRenderer || r.name.StartsWith("DECAL", StringComparison.OrdinalIgnoreCase)) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any;
        }

        public static bool Shows(Renderer r) { return r != null && r.enabled && r.gameObject.activeInHierarchy; }

        public static bool BodyBounds(GameObject g, out Bounds b)
        {
            b = new Bounds();
            if (g == null) return false;
            var col = g.GetComponent<Collider>();
            if (col != null && col.enabled && !col.isTrigger) { b = col.bounds; return true; }
            return Bounds(g, out b);
        }

        public static List<object> BoundsJson(Bounds b) { return new List<object> { Vec(b.center), Vec(b.extents) }; }

        public static List<Renderer> Renderers(GameObject g)
        {
            var l = new List<Renderer>();
            if (g == null) return l;
            foreach (var r in g.GetComponentsInChildren<Renderer>(true)) if (r != null) l.Add(r);
            return l;
        }

        public static double PosDelta(Vector3 a, Vector3 b) { return (a - b).magnitude; }

        public static double RotDelta(Quaternion a, Quaternion b)
        {
            double s = (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w < 0.0 ? -1.0 : 1.0;
            double dx = a.x - s * b.x, dy = a.y - s * b.y, dz = a.z - s * b.z, dw = a.w - s * b.w;
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
            return 4.0 * Math.Asin(Math.Min(1.0, 0.5 * d)) * 180.0 / Math.PI;
        }

        public static List<string> Writers(Transform t)
        {
            var l = new List<string>();
            for (var x = t; x != null; x = x.parent)
            {
                foreach (var c in x.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var ty = c.GetType();
                    string why = null;
                    if (c is Animation || c is Animator) why = "animation";
                    else if (c is MonoBehaviour)
                    {
                        if (HasMethod(ty, "LateUpdate")) why = "LateUpdate";
                        else if (HasMethod(ty, "UmbrellaLateUpdate")) why = "UmbrellaLateUpdate";
                        else if (HasMethod(ty, "OnWillRenderObject")) why = "OnWillRenderObject";
                    }
                    if (why == null) continue;
                    string s = ty.Name + " (" + why + ") on " + x.name;
                    if (!l.Contains(s)) l.Add(s);
                }
            }
            return l;
        }

        public static List<string> OnEnableTypes(GameObject g)
        {
            var l = new List<string>();
            if (g == null) return l;
            foreach (var m in g.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (m == null) continue;
                var ty = m.GetType();
                if (!HasMethod(ty, "OnEnable") || l.Contains(ty.Name)) continue;
                l.Add(ty.Name);
            }
            foreach (var p in g.GetComponentsInChildren<ParticleSystem>(true)) if (p != null && p.playOnAwake && !l.Contains("ParticleSystem.playOnAwake")) l.Add("ParticleSystem.playOnAwake");
            foreach (var a in g.GetComponentsInChildren<Animation>(true)) if (a != null && a.playAutomatically && !l.Contains("Animation.playAutomatically")) l.Add("Animation.playAutomatically");
            return l;
        }

        public static HashSet<int> GoIds(GameObject g)
        {
            var s = new HashSet<int>();
            if (g == null) return s;
            foreach (var t in g.GetComponentsInChildren<Transform>(true)) if (t != null) s.Add(t.gameObject.GetInstanceID());
            return s;
        }

        public static HashSet<string> GoNames(GameObject g)
        {
            var s = new HashSet<string>();
            if (g == null) return s;
            foreach (var t in g.GetComponentsInChildren<Transform>(true)) if (t != null) s.Add(t.name);
            return s;
        }

        public static bool DelegateHas(Type owner, string eventField, Type targetType, string method)
        {
            var f = owner.GetField(eventField, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var d = f != null ? f.GetValue(null) as Delegate : null;
            if (d == null) return false;
            foreach (var x in d.GetInvocationList()) if (x.Method.Name == method && x.Method.DeclaringType == targetType) return true;
            return false;
        }

        public static double Median(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            var s = new List<double>(v);
            s.Sort();
            int n = s.Count;
            return n % 2 == 1 ? s[n / 2] : 0.5 * (s[n / 2 - 1] + s[n / 2]);
        }

        public static double Mean(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            double s = 0;
            foreach (var x in v) s += x;
            return s / v.Count;
        }

        public static double Max(List<double> v)
        {
            double m = double.NaN;
            foreach (var x in v) if (double.IsNaN(m) || x > m) m = x;
            return m;
        }

        public static List<object> Nums(List<double> v) { var l = new List<object>(v.Count); foreach (var x in v) l.Add(Num(x)); return l; }

        public static Transform ChickRoot(LabContext c)
        {
            var q = c.Arg("chick", null);
            if (q != null) { var g = Find(q); return g != null ? g.transform : null; }
            var cam = c.cam;
            Transform best = null; float bd = float.MaxValue;
            foreach (var ch in UObj.FindObjectsOfType<Chicken>())
            {
                if (ch == null || !ch.gameObject.activeInHierarchy) continue;
                var sk = ch.GetComponentInChildren<SkinnedMeshRenderer>();
                if (sk == null || !sk.enabled) continue;
                float score = 1e6f;
                if (cam != null)
                {
                    var s = cam.WorldToViewportPoint(sk.bounds.center);
                    if (s.z > 0 && s.x > 0.05f && s.x < 0.95f && s.y > 0.05f && s.y < 0.95f) score = (new Vector2(s.x, s.y) - new Vector2(0.5f, 0.5f)).sqrMagnitude;
                }
                if (score < bd) { bd = score; best = ch.transform; }
            }
            return best;
        }

        public static Transform Bale(LabContext c)
        {
            var q = c.Arg("bale", null);
            if (q != null) { var g = Find(q); return g != null ? g.transform : null; }
            var hb = UObj.FindObjectOfType<HayBale>();
            if (hb != null) return hb.transform;
            var g2 = Find("HayBaleHolder/HayBale");
            return g2 != null ? g2.transform : null;
        }

        public static List<Transform> ChickBones(Transform chick)
        {
            var l = new List<Transform>();
            if (chick == null) return l;
            var sks = chick.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var sk = sks.Length > 0 ? sks[0] : null;
            if (sk != null && sk.bones != null) foreach (var b in sk.bones) if (b != null && !l.Contains(b)) l.Add(b);
            if (l.Count > 0) return l;
            foreach (var n in new[] { "Root", "Head", "LegL", "FootL", "LegR", "FootR" })
                foreach (var t in chick.GetComponentsInChildren<Transform>(true)) if (t.name == n) { l.Add(t); break; }
            return l;
        }

        public static string ZonePrefix(Transform t)
        {
            if (t == null) return "";
            string r = t.root.name;
            if (r.StartsWith("#"))
            {
                int u = r.IndexOf('_');
                return u > 0 ? r.Substring(0, u + 1) : r;
            }
            return r;
        }

        public static BoundsCullerRuntime Culler(string name)
        {
            if (BoundsCullerRuntime.boundsCullers != null)
                foreach (var b in BoundsCullerRuntime.boundsCullers) if (b != null && b.gameObject.name == name) return b;
            return FindComp<BoundsCullerRuntime>(name);
        }
    }
}
