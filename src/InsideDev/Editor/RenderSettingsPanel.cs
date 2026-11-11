using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace InsideDev
{
    // Settings tab, "Rendering" block: Playdead's post-effect chain on the gameplay
    // camera (on/off per effect, settings per effect, "default" = the game's value). See RenderScale / PostFx.
    // The effect list and what is marked as changed are read once per second and after every click here; looking the
    // effects up means a component scan and a reflection test per component.
    public static class RenderSettingsPanel
    {
        static string openFx;
        static readonly Dictionary<string, string> buf = new Dictionary<string, string>();
        static readonly Color cChanged = new Color(1f, 0.6f, 0.3f, 1f);

        sealed class FieldRow { public PostFx.Setting f; public string label, key, fieldId; public bool changed; }
        sealed class FxRow
        {
            public Behaviour b; public string type, openText, closedText, offOpenText, offClosedText; public bool changed;
            public List<PostFx.Setting> fields; public readonly List<FieldRow> rows = new List<FieldRow>();
        }
        static readonly List<FxRow> fx = new List<FxRow>();
        static int fxFor; static float fxAt; static string head;

        static void Refresh(Camera cam)
        {
            fx.Clear();
            head = "Post effects on the gameplay camera (" + cam.name + ", in render order)";
            foreach (var b in PostFx.Effects(cam))
            {
                var r = new FxRow { b = b, type = b.GetType().Name, fields = PostFx.Settings(b) };
                r.changed = PostFx.IsChanged(b, "(enabled)");
                foreach (var f in r.fields)
                {
                    string k = r.type + "." + f.name;
                    var fr = new FieldRow { f = f, label = "        " + f.name, key = k, fieldId = "pfx:" + k, changed = PostFx.IsChanged(b, f.name) };
                    if (fr.changed) r.changed = true;
                    r.rows.Add(fr);
                }
                string tail = r.fields.Count > 0 ? "   " + r.fields.Count + " settings" : "";
                r.openText = "v " + r.type + tail; r.closedText = "> " + r.type + tail;
                r.offOpenText = "v " + r.type + "   (off)" + tail; r.offClosedText = "> " + r.type + "   (off)" + tail;
                fx.Add(r);
            }
        }

        public static void Draw(UI ui)
        {
            ui.Label("Rendering", UI.Dim);
            // ---- post-effect chain
            var cam = G.Cam();
            ui.Space();
            if (cam == null) { ui.Label("Post effects on the gameplay camera: no gameplay camera", UI.Dim); fxFor = 0; return; }
            float now = Time.realtimeSinceStartup;
            int cid = cam.GetInstanceID();
            if (cid != fxFor || now - fxAt > 1f) { fxFor = cid; fxAt = now; Refresh(cam); }
            ui.Label(head, UI.Dim);
            if (fx.Count == 0) ui.Label("   none found", UI.Dim);
            for (int n = 0; n < fx.Count; n++)
            {
                var r = fx[n];
                var b = r.b;
                if (b == null) { fxAt = 0f; continue; }
                bool open = openFx == r.type;
                ui.BeginRow();
                bool on = ui.Toggle(b.enabled, "", 22);
                if (on != b.enabled) { PostFx.SetEnabled(b, on); fxAt = 0f; }
                if (ui.Item(b.enabled ? (open ? r.openText : r.closedText) : (open ? r.offOpenText : r.offClosedText), r.changed ? cChanged : b.enabled ? UI.Txt : UI.Dim, Mathf.Max(120, ui.Width - 110)))
                    openFx = open ? null : r.type;
                if (r.changed && ui.Button("default", 70)) { PostFx.DefaultAll(b); buf.Clear(); fxAt = 0f; }
                ui.EndRow();
                if (!open) continue;
                if (r.rows.Count == 0) ui.Label("        (no adjustable settings)", UI.Dim);
                for (int i = 0; i < r.rows.Count; i++)
                {
                    var fr = r.rows[i]; var f = fr.f;
                    object v; try { v = f.Get(b); } catch { continue; }
                    string k = fr.key;
                    ui.BeginRow();
                    ui.Label(fr.label, fr.changed ? cChanged : UI.Dim, 220);
                    if (f.type == typeof(bool))
                    {
                        bool cur = (bool)v, nb = ui.Toggle(cur, cur ? "true" : "false");
                        if (nb != cur) { PostFx.SetValue(b, f, nb ? "1" : "0"); fxAt = 0f; }
                    }
                    else
                    {
                        string s; if (!buf.TryGetValue(k, out s)) s = PostFx.Text(v);
                        string ns = s;
                        bool enter = EditorInput.Field(ui, fr.fieldId, ref ns, 120);
                        if (ns != s) buf[k] = ns;
                        if (ui.Button("set") || enter) { DevLog.Write("[postfx] " + PostFx.SetValue(b, f, ns)); buf.Remove(k); fxAt = 0f; }
                    }
                    if (fr.changed && ui.Button("default")) { PostFx.Default(b, f.name); buf.Remove(k); fxAt = 0f; }
                    ui.EndRow();
                }
            }
            ui.Label("   choices are saved and re-applied to new gameplay cameras; 'default' = the game's own value", UI.Dim);
        }
    }
}
