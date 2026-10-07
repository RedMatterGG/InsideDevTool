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
        static List<MemAddr.Block> blocks = new List<MemAddr.Block>();
        static string query = "", answer;
        static int rows;

        public static void Draw(UI ui, GameObject go)
        {
            if (!MemAddr.Enabled) return;
            float now = Time.realtimeSinceStartup;
            if (go.GetInstanceID() != forId || now - at > 1f)
            {
                forId = go.GetInstanceID(); at = now;
                try { blocks = MemAddr.Describe(go); } catch (Exception e) { DevLog.Error("memory", e); blocks.Clear(); }
                rows = 0; foreach (var b in blocks) rows += b.rows.Count + 1;
            }
            if (!Links.Section(ui, "memory", "MEMORY  (Cheat Engine)" + (MemLink.Linked ? "  - CE script linked" : ""), rows, cHead)) return;
            if (!MemAddr.Ready) { ui.Label("   " + MemAddr.status, Color.red); return; }
            ui.Label("   valid until this object is destroyed, the level reloads or the game restarts.  CE: attach to INSIDE.exe.", UI.Dim);
            ui.BeginRow(); ui.Space(12);
            if (ui.Button("Copy all as CE table")) { MemAddr.Copy(MemAddr.CheatTableXml(go, blocks)); answer = "copied - in Cheat Engine click the address list and press Ctrl+V"; }
            if (MemLink.Linked && ui.Button("Send to CE script")) { MemLink.Export(go); answer = "sent - the CE script rebuilds its InsideDev group"; }
            ui.EndRow();
            foreach (var b in blocks)
            {
                ui.BeginRow();
                ui.Label("   " + b.title, UI.Txt, 200);
                if (b.addr != 0) { ui.Label("script " + MemAddr.Hex(b.addr) + " (" + b.size + " B)", cAddr, 230); if (ui.Button("Copy", 44)) MemAddr.Copy(b.addr.ToString("X")); }
                if (b.native != 0) { ui.Label("native " + MemAddr.Hex(b.native), UI.Dim, 170); if (ui.Button("Copy", 44)) MemAddr.Copy(b.native.ToString("X")); }
                ui.EndRow();
                foreach (var r in b.rows)
                {
                    ui.BeginRow();
                    ui.Label("      +0x" + r.offset.ToString("X").PadRight(4) + " " + r.name, UI.Txt, 230);
                    ui.Label(r.addr != 0 ? MemAddr.Hex(r.addr) : "-", cAddr, 130);
                    ui.Label(r.ceType, UI.Dim, 150);
                    ui.Label(r.value, UI.Txt, 150);
                    if (r.addr != 0 && ui.Button("Copy", 44)) MemAddr.Copy(r.addr.ToString("X"));
                    ui.EndRow();
                }
            }
            ui.BeginRow(); ui.Label("   what is at 0x", UI.Dim, 100);
            bool enter = ui.TextField("mem_what", ref query, 160);
            bool what = ui.Button("Object/field") || enter, meth = ui.Button("Code -> method");
            ui.EndRow();
            if (what || meth)
            {
                long v;
                if (!MemLink.ParseHex(query, out v)) answer = "type a hex address, e.g. 1A2B3C40";
                else if (meth) answer = MemLink.Method(v, true);
                else { GameObject owner; answer = MemAddr.WhatIs(v, out owner); if (owner != null && owner != go) Selection.Set(owner, "memory"); }
            }
            if (answer != null) foreach (var l in answer.Split('\n')) ui.Label("   " + l, cHead);
        }
    }
}
