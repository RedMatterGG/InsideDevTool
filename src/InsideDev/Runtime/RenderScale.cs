using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace InsideDev
{
    // Settings > Rendering: Playdead's post-effect chain (see PostFx below).
    // The internal resolution slider was removed: INSIDE's post chain is native engine code (UnityEngine.D11PD*)
    // that crashes when the camera renders into a texture, Display.SetRenderingResolution is an empty stub in this
    // build, and changing the fullscreen mode had no visible effect. The coordinate helpers stay (identity now).
    public static class RenderScale
    {
        public static RenderTexture rt;                  // always null now

        public static void Tick()
        {
            try { PostFx.Tick(); } catch (Exception e) { DevLog.Error("post fx", e); }
        }

        // screen <-> camera pixel conversions (identity unless a scaled render texture is in use)
        static bool Scaled(Camera c) { return c != null && rt != null && c.targetTexture == rt; }
        public static Vector3 W2S(Camera c, Vector3 world)
        {
            var sp = c.WorldToScreenPoint(world);
            if (Scaled(c)) { sp.x *= Screen.width / (float)rt.width; sp.y *= Screen.height / (float)rt.height; }
            return sp;
        }
        public static Ray Ray(Camera c, Vector3 screen)
        {
            if (Scaled(c)) { screen.x *= rt.width / (float)Screen.width; screen.y *= rt.height / (float)Screen.height; }
            return c.ScreenPointToRay(screen);
        }
    }

    // Post effects on the gameplay camera: Playdead's native D11PD* behaviours (settings are engine properties) and
    // any managed image effect. On/off and every number / on-off / whole-number setting, remembered per component
    // type + setting name in editor.cfg and re-applied to new gameplay cameras.
    public static class PostFx
    {
        public sealed class Setting
        {
            public string name; public Type type; public PropertyInfo prop; public FieldInfo field;
            public object Get(Behaviour b) { return prop != null ? prop.GetValue(b, null) : field.GetValue(b); }
            public void Set(Behaviour b, object v) { if (prop != null) prop.SetValue(b, v, null); else field.SetValue(b, v); }
        }
        sealed class Orig { public bool enabled; public readonly Dictionary<string, object> values = new Dictionary<string, object>(); }
        static readonly Dictionary<int, Orig> origs = new Dictionary<int, Orig>();
        static readonly Dictionary<Type, List<Setting>> cache = new Dictionary<Type, List<Setting>>();
        static int appliedCam; static float nextCheck;
        static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;
        static readonly Dictionary<Type, bool> effectTypes = new Dictionary<Type, bool>();
        static List<Behaviour> effects = new List<Behaviour>();
        static int effectsCam; static float effectsAt = -100f;
        static bool described;

        // The post effects on a camera. The list is kept for one second per camera (the settings panel asks every frame)
        // and is shared: callers read it and do not change it.
        public static List<Behaviour> Effects(Camera c)
        {
            if (c == null) return new List<Behaviour>();
            int id = c.GetInstanceID();
            float now = Time.realtimeSinceStartup;
            if (id == effectsCam && now - effectsAt < 1f && now >= effectsAt)
            {
                bool alive = true;
                for (int i = 0; i < effects.Count; i++) if (effects[i] == null) { alive = false; break; }
                if (alive) return effects;
            }
            var l = new List<Behaviour>();
            foreach (var b in c.GetComponents<Behaviour>())
            {
                if (b == null || b is Camera) continue;
                if (IsEffect(b.GetType())) l.Add(b);
            }
            effects = l; effectsCam = id; effectsAt = now;
            return l;
        }

        static bool IsEffect(Type t)
        {
            bool yes;
            if (effectTypes.TryGetValue(t, out yes)) return yes;
            yes = t.Name.StartsWith("D11") || t.Name.StartsWith("PD") || t.GetMethod("OnRenderImage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
            effectTypes[t] = yes;
            return yes;
        }

        static bool Editable(Type t) { return t == typeof(float) || t == typeof(int) || t == typeof(bool); }

        public static List<Setting> Settings(Behaviour b)
        {
            var t = b.GetType(); List<Setting> l;
            if (cache.TryGetValue(t, out l)) return l;
            l = new List<Setting>();
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (p.CanRead && p.CanWrite && Editable(p.PropertyType) && p.GetIndexParameters().Length == 0) l.Add(new Setting { name = p.Name, type = p.PropertyType, prop = p });
            if (!t.Name.StartsWith("D11"))
                foreach (var f in ValueDump.Fields(t, false))
                    if (!f.IsStatic && Editable(f.FieldType) && ValueDump.IsSerialized(f) && f.DeclaringType != typeof(MonoBehaviour)) l.Add(new Setting { name = f.Name, type = f.FieldType, field = f });
            cache[t] = l;
            return l;
        }

        static string Key(Behaviour b, string s) { return "postfx." + b.GetType().Name + "." + s; }
        static Orig OrigOf(Behaviour b)
        {
            Orig o; if (origs.TryGetValue(b.GetInstanceID(), out o)) return o;
            o = new Orig { enabled = b.enabled };
            foreach (var s in Settings(b)) { try { o.values[s.name] = s.Get(b); } catch { } }
            origs[b.GetInstanceID()] = o;
            return o;
        }

        public static void SetEnabled(Behaviour b, bool on) { OrigOf(b); b.enabled = on; EditorState.Set(Key(b, "(enabled)"), on ? "1" : "0"); DevLog.Write("[postfx] " + b.GetType().Name + (on ? " on" : " off")); }

        public static string SetValue(Behaviour b, Setting s, string text)
        {
            object v;
            try
            {
                if (s.type == typeof(float)) v = float.Parse(text, IC);
                else if (s.type == typeof(int)) v = int.Parse(text, IC);
                else v = text == "1" || text.ToLowerInvariant() == "true";
            }
            catch { return "bad value for " + s.name + ": " + text; }
            OrigOf(b);
            s.Set(b, v);
            EditorState.Set(Key(b, s.name), Text(v));
            return b.GetType().Name + "." + s.name + " = " + Text(v);
        }
        public static string Text(object v) { return v is float ? ((float)v).ToString("0.####", IC) : v is bool ? ((bool)v ? "1" : "0") : v is int ? ((int)v).ToString(IC) : v == null ? "null" : v.ToString(); }

        public static bool IsChanged(Behaviour b, string s) { return EditorState.Get(Key(b, s), (string)null) != null; }

        public static void Default(Behaviour b, string s)
        {
            var o = OrigOf(b);
            if (s == "(enabled)") b.enabled = o.enabled;
            else foreach (var st in Settings(b)) if (st.name == s) { object v; if (o.values.TryGetValue(s, out v)) st.Set(b, v); }
            EditorState.Remove(Key(b, s));
        }
        public static void DefaultAll(Behaviour b) { Default(b, "(enabled)"); foreach (var s in Settings(b)) Default(b, s.name); }

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 1f;
            if (!described)
            {
                described = true;
                EditorState.Describe("postfx", "postfx.<Type>.<setting>", "", "a changed setting of a post effect on the gameplay camera (postfx set, Settings > Rendering), re-applied to every new camera; postfx.<Type>.(enabled) is 1 or 0");
            }
            var c = G.Cam(); if (c == null) return;
            if (c.GetInstanceID() == appliedCam) return;
            appliedCam = c.GetInstanceID();
            int n = 0;
            foreach (var b in Effects(c))
            {
                string e = EditorState.Get(Key(b, "(enabled)"), (string)null);
                if (e != null) { OrigOf(b); b.enabled = e == "1"; n++; }
                foreach (var s in Settings(b)) { string v = EditorState.Get(Key(b, s.name), (string)null); if (v != null) { SetValue(b, s, v); n++; } }
            }
            if (n > 0) DevLog.Write("[postfx] re-applied " + n + " stored setting(s) to " + c.name);
        }

        public static Setting Find(Behaviour b, string name) { foreach (var s in Settings(b)) if (s.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return s; return null; }

        // The report as one line of JSON ("postfx json"): numbers as numbers, the settings of every effect by name.
        public static string ReportJson()
        {
            var c = G.Cam();
            if (c == null) return JsonLine.Obj("ok", false, "error", "no gameplay camera");
            var o = new Json.Obj();
            o["ok"] = true; o["camera"] = c.name; o["depth"] = c.depth; o["hdr"] = c.hdr;
            o["target"] = c.targetTexture != null ? c.targetTexture.name : "screen";
            o["screen"] = new List<object> { Screen.width, Screen.height };
            var fx = new List<object>();
            foreach (var b in Effects(c))
            {
                var e = new Json.Obj();
                e["type"] = b.GetType().Name; e["enabled"] = b.enabled;
                var values = new Json.Obj();
                var changed = new List<object>();
                foreach (var s in Settings(b))
                {
                    object v = null;
                    try { v = s.Get(b); } catch { }
                    values[s.name] = v;
                    if (IsChanged(b, s.name)) changed.Add(s.name);
                }
                e["settings"] = values; e["changed"] = changed;
                fx.Add(e);
            }
            o["effects"] = fx;
            return JsonLine.Write(o);
        }

        public static string Report()
        {
            var c = G.Cam(); if (c == null) return "no gameplay camera";
            var d = Display.main;
            var sb = new System.Text.StringBuilder("gameplay camera " + c.name + "  depth " + Fmt.F(c.depth, "0.##") + "  hdr " + c.hdr + "  target " + (c.targetTexture != null ? c.targetTexture.name : "screen") + "   display system " + Fmt.I(d.systemWidth) + "x" + Fmt.I(d.systemHeight) + " rendering " + Fmt.I(d.renderingWidth) + "x" + Fmt.I(d.renderingHeight) + "  screen " + Fmt.I(Screen.width) + "x" + Fmt.I(Screen.height) + "\n");
            foreach (var b in Effects(c))
            {
                sb.Append("  ").Append(b.enabled ? "[on]  " : "[off] ").Append(b.GetType().FullName).Append('\n');
                foreach (var s in Settings(b)) { object v = null; try { v = s.Get(b); } catch { } sb.Append("      ").Append(s.name).Append(" = ").Append(Text(v)).Append(IsChanged(b, s.name) ? "   (changed)" : "").Append('\n'); }
            }
            sb.Append("all components on the camera: ");
            foreach (var x in c.GetComponents<Component>()) sb.Append(x.GetType().Name).Append(", ");
            sb.Append("\nall cameras:\n");
            foreach (var x in Camera.allCameras) sb.Append("  ").Append(x.name).Append("  depth ").Append(Fmt.F(x.depth, "0.##")).Append("  target ").Append(x.targetTexture != null ? x.targetTexture.name : "screen").Append('\n');
            return sb.ToString();
        }
    }
}
