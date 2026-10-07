using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Link to the Cheat Engine script (tools\cheatengine\InsideDev.CT). Inert until that script writes _mod\ce\.link.
    //   _mod\ce\selection.txt  written when the selection changes (or on request): the selected object's addresses,
    //                          one record per line:  name|CE type|hex address|pointer(0/1)
    //   _mod\ce\ask.txt        written by Cheat Engine: "id|what <hex>" or "id|method <hex>" or "id|refresh"
    //   _mod\ce\answer.txt     our reply: first line the id, then the text
    public static class MemLink
    {
        [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        static string dir; static bool exists; static float nextCheck, nextPoll;
        static int lastSel = int.MinValue, stamp;
        public static string lastAsk = "";

        static string Dir { get { if (dir == null) dir = Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "_mod"), "ce"); return dir; } }
        public static bool Linked { get { return exists; } }

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now >= nextCheck) { nextCheck = now + 2f; try { if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir); bool was = exists; exists = File.Exists(Path.Combine(Dir, ".link")); if (exists && !was) lastSel = int.MinValue; } catch { exists = false; } }
            if (!exists || now < nextPoll) return;
            nextPoll = now + 0.25f;
            try
            {
                var go = Selection.Current;
                int id = go != null ? go.GetInstanceID() : 0;
                if (id != lastSel) { lastSel = id; Export(go); }
                string ask = Path.Combine(Dir, "ask.txt");
                if (File.Exists(ask))
                {
                    string q = File.ReadAllText(ask).Trim(); File.Delete(ask);
                    int bar = q.IndexOf('|'); string qid = bar > 0 ? q.Substring(0, bar) : "0"; q = bar > 0 ? q.Substring(bar + 1).Trim() : q;
                    lastAsk = q;
                    string ans = Answer(q);
                    File.WriteAllText(Path.Combine(Dir, "answer.txt"), qid + "\n" + ans);
                }
            }
            catch (Exception e) { DevLog.Error("ce link", e); nextPoll = now + 3f; }
        }

        static string Answer(string q)
        {
            string[] a = q.Split(new[] { ' ' }, 2);
            string arg = a.Length > 1 ? a[1].Trim() : "";
            switch (a[0])
            {
                case "what": { long v; if (!ParseHex(arg, out v)) return "not an address: " + arg; GameObject owner; var s = MemAddr.WhatIs(v, out owner); if (owner != null) Selection.Set(owner, "cheat engine"); return s; }
                case "method": { long v; if (!ParseHex(arg, out v)) return "not an address: " + arg; return Method(v, true); }
                case "refresh": Export(Selection.Current); return "exported " + (Selection.Current != null ? Selection.Current.name : "nothing (no selection)");
            }
            return "unknown request " + q;
        }

        // code address -> method; optionally open its type in the Logic panel
        public static string Method(long addr, bool open)
        {
            if (!MemAddr.Ready) return "memory addresses are off: " + MemAddr.status;
            string m = MemAddr.MethodAt(addr);
            if (m == null) return MemAddr.Hex(addr) + ": not compiled game/script code (Unity engine code, mono itself or not code)";
            int colon = m.IndexOf(':');
            if (open && colon > 0)
            {
                var t = CodeGraph.FindType(m.Substring(0, colon));
                if (t != null) { LogicPanel.Show(t); try { DevCore.Instance.ShowPanel("logic"); } catch { } return MemAddr.Hex(addr) + " is in " + m + "\n(opened " + t.Name + " in the Logic panel)"; }
            }
            return MemAddr.Hex(addr) + " is in " + m;
        }

        // accepts what Cheat Engine copies: a bare address, "0x…", "INSIDE.exe+…"-free hex, a copied address-list
        // record (XML with <Address>) or a memory-viewer line ("addr - bytes - opcode"): the first hex number wins
        public static bool ParseHex(string s, out long v)
        {
            v = 0; if (s == null) return false;
            int a = s.IndexOf("<Address>", StringComparison.Ordinal);
            if (a >= 0) { int b = s.IndexOf("</Address>", a, StringComparison.Ordinal); if (b > a) s = s.Substring(a + 9, b - a - 9); }
            s = s.Trim().Trim('"');
            var parts = s.Split(new[] { ' ', '\t', '-', ',', ';', ':', '[', ']', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in parts)
            {
                string t = raw.Trim(); if (t.StartsWith("0x") || t.StartsWith("0X")) t = t.Substring(2);
                int plus = t.IndexOf('+');
                if (plus > 0)
                {   // "INSIDE.exe+1A2B" / "mono.dll+…": module base + offset
                    long off; IntPtr mb = IntPtr.Zero;
                    try { mb = GetModuleHandle(t.Substring(0, plus).Trim('"')); } catch { }
                    if (mb != IntPtr.Zero && long.TryParse(t.Substring(plus + 1), System.Globalization.NumberStyles.HexNumber, null, out off)) { v = mb.ToInt64() + off; return true; }
                    continue;
                }
                if (t.Length == 0 || t.Length > 16 || (t.Length < 5 && parts.Length > 1)) continue;   // skip byte columns of a memory-viewer line
                if (long.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out v) && v > 0) return true;
            }
            v = 0; return false;
        }

        public static string Export(GameObject go)
        {
            var sb = new StringBuilder();
            sb.Append("stamp|").Append(++stamp).Append('\n');
            if (go == null) sb.Append("none|no selection\n");
            else if (!MemAddr.Ready) sb.Append("none|").Append(MemAddr.status).Append('\n');
            else
            {
                string path = go.name; for (var t = go.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                sb.Append("object|").Append(go.name.Replace('|', '/')).Append('|').Append(path.Replace('|', '/')).Append('\n');
                foreach (var b in MemAddr.Describe(go))
                {
                    sb.Append("comp|").Append(b.title).Append('|').Append(b.addr.ToString("X")).Append('|').Append(b.native.ToString("X")).Append('\n');
                    foreach (var r in b.rows)
                    {
                        if (r.addr == 0) continue;
                        string vt = r.ceType.StartsWith("Float") ? "Float" : r.ceType.StartsWith("4 Bytes") ? "4 Bytes" : r.ceType.StartsWith("8 Bytes") ? "8 Bytes" : r.ceType == "Double" ? "Double" : r.ceType == "2 Bytes" ? "2 Bytes" : r.ceType == "Byte" ? "Byte" : null;
                        if (vt == null) continue;
                        int n = r.ceType.EndsWith("x2") ? 2 : r.ceType.EndsWith("x3") ? 3 : r.ceType.EndsWith("x4") ? 4 : 1;
                        for (int k = 0; k < n; k++)
                            sb.Append("rec|").Append((b.title + "." + r.name + (n > 1 ? "." + "xyzw"[k] : "")).Replace('|', '/')).Append('|').Append(vt).Append('|').Append((r.addr + k * 4).ToString("X")).Append('|').Append(r.isPointer ? 1 : 0).Append('\n');
                    }
                }
            }
            try { if (exists) File.WriteAllText(Path.Combine(Dir, "selection.txt"), sb.ToString()); } catch { }
            return sb.ToString();
        }
    }
}
