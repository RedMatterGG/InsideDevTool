using System;
using System.Reflection;
using UnityEngine;

namespace InsideDev
{
    // System clipboard. Unity 5.0 keeps GUIUtility.systemCopyBuffer internal, so it is reached by reflection;
    // TextEditor's Copy/Paste is the fallback.
    public static class Clipboard
    {
        static PropertyInfo prop; static bool looked;
        static PropertyInfo Prop
        {
            get
            {
                if (!looked) { looked = true; try { prop = typeof(GUIUtility).GetProperty("systemCopyBuffer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); } catch { } }
                return prop;
            }
        }

        public static void Set(string s)
        {
            try { if (Prop != null) { Prop.SetValue(null, s ?? "", null); return; } } catch { }
            try { var te = new TextEditor(); te.content = new GUIContent(s ?? ""); te.SelectAll(); te.Copy(); } catch { }
        }

        public static string Get()
        {
            try { if (Prop != null) return (string)Prop.GetValue(null, null) ?? ""; } catch { }
            try { var te = new TextEditor(); te.Paste(); return te.content != null ? te.content.text ?? "" : ""; } catch { }
            return "";
        }
    }
}
