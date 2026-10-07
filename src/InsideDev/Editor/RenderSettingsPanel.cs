using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace InsideDev
{
    // Settings tab, "Rendering" block: Playdead's post-effect chain on the gameplay
    // camera (on/off per effect, settings per effect, "default" = the game's value). See RenderScale / PostFx.
    public static class RenderSettingsPanel
    {
        static string openFx;
        static readonly Dictionary<string, string> buf = new Dictionary<string, string>();
        static readonly Color cChanged = new Color(1f, 0.6f, 0.3f, 1f);

        public static void Draw(UI ui)
        {
            ui.Label("Rendering", UI.Dim);
            // ---- post-effect chain
            var cam = G.Cam();
            ui.Space();
            ui.Label("Post effects on the gameplay camera" + (cam != null ? " (" + cam.name + ", in render order)" : ": no gameplay camera"), UI.Dim);
            if (cam == null) return;
            var fx = PostFx.Effects(cam);
            if (fx.Count == 0) ui.Label("   none found", UI.Dim);
            foreach (var b in fx)
            {
                string tn = b.GetType().Name;
                bool open = openFx == tn;
                bool changed = PostFx.IsChanged(b, "(enabled)");
                var fields = PostFx.Settings(b);
                foreach (var f in fields) if (PostFx.IsChanged(b, f.name)) { changed = true; break; }
                ui.BeginRow();
                bool on = ui.Toggle(b.enabled, "", 22);
                if (on != b.enabled) PostFx.SetEnabled(b, on);
                if (ui.Item((open ? "v " : "> ") + tn + (b.enabled ? "" : "   (off)") + (fields.Count > 0 ? "   " + fields.Count + " settings" : ""), changed ? cChanged : b.enabled ? UI.Txt : UI.Dim, Mathf.Max(120, ui.Width - 110)))
                    openFx = open ? null : tn;
                if (changed && ui.Button("default", 70)) { PostFx.DefaultAll(b); buf.Clear(); }
                ui.EndRow();
                if (!open) continue;
                if (fields.Count == 0) ui.Label("        (no adjustable settings)", UI.Dim);
                foreach (var f in fields)
                {
                    object v; try { v = f.Get(b); } catch { continue; }
                    bool ch = PostFx.IsChanged(b, f.name);
                    string k = tn + "." + f.name;
                    ui.BeginRow();
                    ui.Label("        " + f.name, ch ? cChanged : UI.Dim, 220);
                    if (f.type == typeof(bool))
                    {
                        bool cur = (bool)v, nb = ui.Toggle(cur, cur ? "true" : "false");
                        if (nb != cur) PostFx.SetValue(b, f, nb ? "1" : "0");
                    }
                    else
                    {
                        string s; if (!buf.TryGetValue(k, out s)) s = PostFx.Text(v);
                        string ns = s;
                        bool enter = ui.TextField("pfx:" + k, ref ns, 120);
                        if (ns != s) buf[k] = ns;
                        if (ui.Button("set") || enter) { DevLog.Write("[postfx] " + PostFx.SetValue(b, f, ns)); buf.Remove(k); }
                    }
                    if (ch && ui.Button("default")) { PostFx.Default(b, f.name); buf.Remove(k); }
                    ui.EndRow();
                }
            }
            ui.Label("   choices are saved and re-applied to new gameplay cameras; 'default' = the game's own value", UI.Dim);
        }
    }
}
