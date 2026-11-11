using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev
{
    // Inspector section MEMORY (Settings > "Memory addresses for Cheat Engine", off by default).
    // Per component: script object address and size, Unity native object address; per field: offset, address,
    // Cheat Engine type, value, Copy. "Copy all as CE table" puts address-list XML on the clipboard (paste into CE's
    // address list with Ctrl+V). A "what is at" box answers address -> object/field or code -> method.
    // Addresses are valid until the object is destroyed, the level reloads or the game restarts.
    public static class MemorySection
    {
        static readonly Color cHead = new Color(1f, 0.6f, 0.85f, 1f), cAddr = new Color(0.85f, 0.85f, 0.6f, 1f);
        static int forId; static float at;
        static readonly char[] Newline = { (char)10 };
        static List<MemAddr.Block> blocks = new List<MemAddr.Block>();
        static string query = "", answer, sectionTitle;
        static int rows;
        // row texts of the blocks, built when the blocks are read (once per second), not on every frame
        sealed class BlockText { public string title, script, native; public string[] rowName, rowAddr; }
        static readonly List<BlockText> texts = new List<BlockText>();
        static string[] answerLines;
        static string answerFor;

        public static void Draw(UI ui, GameObject go)
        {
            if (!MemAddr.Enabled) return;
            float now = Time.realtimeSinceStartup;
            if (go.GetInstanceID() != forId || now - at > 1f)
            {
                forId = go.GetInstanceID(); at = now;
                try { blocks = MemAddr.Describe(go); } catch (Exception e) { DevLog.Error("memory", e); blocks.Clear(); }
                rows = 0; foreach (var b in blocks) rows += b.rows.Count + 1;
                texts.Clear();
                foreach (var b in blocks)
                {
                    var t = new BlockText { title = "   " + b.title, rowName = new string[b.rows.Count], rowAddr = new string[b.rows.Count] };
                    if (b.addr != 0) t.script = "script " + MemAddr.Hex(b.addr) + " (" + b.size + " B)";
                    if (b.native != 0) t.native = "native " + MemAddr.Hex(b.native);
                    for (int i = 0; i < b.rows.Count; i++)
                    {
                        var r = b.rows[i];
                        t.rowName[i] = "      +0x" + r.offset.ToString("X").PadRight(4) + " " + r.name;
                        t.rowAddr[i] = r.addr != 0 ? MemAddr.Hex(r.addr) : "-";
                    }
                    texts.Add(t);
                }
                sectionTitle = "MEMORY  (Cheat Engine)" + (MemLink.Linked ? "  - CE script linked" : "");
            }
            if (!Links.Section(ui, "memory", sectionTitle ?? "MEMORY  (Cheat Engine)", rows, cHead)) return;
            if (!MemAddr.Ready) { ui.Label("   " + MemAddr.status, Color.red); return; }
            ui.Label("   valid until this object is destroyed, the level reloads or the game restarts.  CE: attach to INSIDE.exe.", UI.Dim);
            ui.BeginRow(); ui.Space(12);
            if (ui.Button("Copy all as CE table")) { MemAddr.Copy(MemAddr.CheatTableXml(go, blocks)); answer = "copied - in Cheat Engine click the address list and press Ctrl+V"; }
            if (MemLink.Linked && ui.Button("Send to CE script")) { MemLink.Export(go); answer = "sent - the CE script rebuilds its InsideDev group"; }
            ui.EndRow();
            for (int bi = 0; bi < blocks.Count && bi < texts.Count; bi++)
            {
                var b = blocks[bi]; var t = texts[bi];
                ui.BeginRow();
                ui.Label(t.title, UI.Txt, 200);
                if (b.addr != 0) { ui.Label(t.script, cAddr, 230); if (ui.Button("Copy", 44)) MemAddr.Copy(b.addr.ToString("X")); }
                if (b.native != 0) { ui.Label(t.native, UI.Dim, 170); if (ui.Button("Copy", 44)) MemAddr.Copy(b.native.ToString("X")); }
                ui.EndRow();
                for (int i = 0; i < b.rows.Count && i < t.rowName.Length; i++)
                {
                    var r = b.rows[i];
                    ui.BeginRow();
                    ui.Label(t.rowName[i], UI.Txt, 230);
                    ui.Label(t.rowAddr[i], cAddr, 130);
                    ui.Label(r.ceType, UI.Dim, 150);
                    ui.Label(r.value, UI.Txt, 150);
                    if (r.addr != 0 && ui.Button("Copy", 44)) MemAddr.Copy(r.addr.ToString("X"));
                    ui.EndRow();
                }
            }
            ui.BeginRow(); ui.Label("   what is at 0x", UI.Dim, 100);
            bool enter = EditorInput.Field(ui, "mem_what", ref query, 160);
            bool what = ui.Button("Object/field") || enter, meth = ui.Button("Code -> method");
            ui.EndRow();
            if (what || meth)
            {
                long v;
                if (!MemLink.ParseHex(query, out v)) answer = "type a hex address, e.g. 1A2B3C40";
                else if (meth) answer = MemLink.Method(v, true);
                else { GameObject owner; answer = MemAddr.WhatIs(v, out owner); if (owner != null && owner != go) Selection.Set(owner, "memory"); }
            }
            if (answer != null)
            {
                if ((object)answerFor != (object)answer) { answerFor = answer; answerLines = answer.Split(Newline); for (int i = 0; i < answerLines.Length; i++) answerLines[i] = "   " + answerLines[i]; }
                foreach (var l in answerLines) ui.Label(l, cHead);
            }
        }
    }
}
