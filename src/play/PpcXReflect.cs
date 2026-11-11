using System;
using System.Collections.Generic;
using InsideDev.McpExt;

namespace InsideDev.Play
{
    // PpcXReflect.cs (owner WORLD_B): reflect get and set, single and batch.
    //   get: member, or members [..] (values[] in the result; a refused or missing member fails only its own row).
    //   repeat_frames reads the same members again that many rendered frames later (value_k, changed): "which field
    //   moves when I pull the lever" in one call.
    //   set: member + value, or sets [{member, value}] on one object: every write is checked on its own (refusals and
    //   ranges per member), all are re-read together after one wait. The whole set runs in two main-thread items: the
    //   writes with the first re-read, and the later re-read.
    //   Undo (restore_on_end) is bound to the object that was written, not to the caller's spec: an observe id dies
    //   with a load while a manager object lives on, and the undo must still reach it. No undo is recorded when the
    //   old value could not be read.
    public static partial class Reflect
    {
        const int MaxMembers = 32, MaxSets = 16;

        sealed class Item
        {
            public string member;
            public object json, typed, before, after, afterK;
            public object rb, ra, rk;
            public bool hasValue, readBefore, readAfter, sameAfter, readK, sameK, changed;
            public string type;
            public McpException error;
        }

        static Json.Obj ErrorOf(McpException e)
        {
            var o = new Json.Obj();
            o["code"] = e.code ?? McpErr.Internal;
            o["message"] = e.Message;
            if (e.hint != null) o["hint"] = e.hint;
            return o;
        }

        static McpException Wrap(string member, Exception e)
        {
            var me = e as McpException;
            if (me != null) return me;
            var inner = ReflectValues.Inner(e);
            return new McpException(McpErr.GameError, member + ": " + inner.GetType().Name + ": " + inner.Message);
        }

        // main thread: one read; throws McpException
        static object ReadOne(Target t, string member, out string type)
        {
            var pi = ReflectValues.Resolve(t.type, t.instance, member);
            if (pi.error != null) throw new McpException(McpErr.NotFound, pi.error, "describe_type lists the members");
            RefusePath(t.type, pi, member, false, null);
            object v;
            try { v = ReflectValues.ReadPath(pi, t.instance); }
            catch (Exception e) { throw new McpException(McpErr.GameError, member + ": " + ReflectValues.Inner(e).GetType().Name + ": " + ReflectValues.Inner(e).Message); }
            type = TypeName(v, pi.leafType);
            return v;
        }

        // ---------------------------------------------------------------- get
        static ToolResult Get(Json.Obj a, CallContext ctx)
        {
            var list = PlayShared.List(a, "members");
            bool batch = list != null && list.Count > 0;
            int again = PlayShared.Int(a, "repeat_frames", 0);
            var items = new List<Item>();
            if (batch)
            {
                if (list.Count > MaxMembers) return ToolResult.Fail(McpErr.BadArgs, "members: at most " + MaxMembers + " per call");
                foreach (var x in list)
                {
                    var name = x as string;
                    if (string.IsNullOrEmpty(name)) return ToolResult.Fail(McpErr.BadArgs, "members: each entry is a member name (string)");
                    items.Add(new Item { member = name });
                }
            }
            else items.Add(new Item { member = PlayShared.Str(a, "member", null) });
            Target target = null;
            var warnings = new List<string>();
            ctx.MainDo(() =>
            {
                target = Resolve(a);
                warnings.AddRange(target.warnings);
                foreach (var it in items)
                {
                    try { it.before = ReadOne(target, it.member, out it.type); it.readBefore = true; it.rb = Render(it.before); }
                    catch (Exception e)
                    {
                        if (!batch) throw;
                        it.error = Wrap(it.member, e);
                    }
                }
            });
            if (again > 0)
            {
                if (!ctx.WaitFrames(again, 3000 + again * 200)) warnings.Add("repeat_frames: the frames did not come in time (cancelled or the main thread is stalled)");
                else
                {
                    try
                    {
                        ctx.MainDo(() =>
                        {
                            foreach (var it in items)
                            {
                                if (!it.readBefore) continue;
                                if (Gone(target.instance)) continue;
                                try
                                {
                                    string ty;
                                    it.afterK = ReadOne(target, it.member, out ty);
                                    it.readK = true;
                                    it.changed = !ReflectValues.Same(it.before, it.afterK);
                                    it.rk = Render(it.afterK);
                                }
                                catch (Exception) { }
                            }
                        });
                    }
                    catch (McpException e) { warnings.Add("repeat_frames: " + e.Message); }
                }
            }
            var o = PlayShared.Env();
            o["outcome"] = "done";
            if (!batch)
            {
                var it = items[0];
                o["value"] = it.rb;
                o["type"] = it.type;
                if (again > 0 && it.readK) { o["value_k"] = it.rk; o["changed"] = it.changed; }
            }
            else
            {
                var rows = new List<object>();
                int failed = 0;
                foreach (var it in items)
                {
                    var row = new Json.Obj();
                    row["member"] = it.member;
                    if (it.error != null) { row["error"] = ErrorOf(it.error); failed++; }
                    else
                    {
                        row["value"] = it.rb;
                        row["type"] = it.type;
                        if (again > 0 && it.readK) { row["value_k"] = it.rk; row["changed"] = it.changed; }
                    }
                    rows.Add(row);
                }
                o["values"] = rows;
                if (failed == items.Count) o["outcome"] = "refused";
                if (failed > 0) warnings.Add(failed + " of " + items.Count + " members could not be read (see values[].error)");
            }
            if (warnings.Count > 0) o["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(o);
        }

        static bool Gone(object instance)
        {
            var u = instance as UnityEngine.Object;
            return instance is UnityEngine.Object && u == null;
        }

        // ---------------------------------------------------------------- set (verified)
        // main thread: check and write one member, re-read it, record the undo; throws McpException
        static void WriteOne(Target t, Item it, bool restore, List<string> warnings)
        {
            string member = it.member;
            var pi = ReflectValues.Resolve(t.type, t.instance, member);
            if (pi.error != null) throw new McpException(McpErr.NotFound, pi.error, "describe_type lists the members");
            string err;
            it.typed = ReflectValues.Convert(it.json, pi.leafType, World.RefFor, out err);
            if (err != null) throw new McpException(McpErr.BadArgs, member + ": " + err);
            RefusePath(t.type, pi, member, true, it.typed ?? it.json);
            var leaf = pi.members[pi.members.Count - 1];
            string range = Tunables.CheckReflect(leaf.DeclaringType != null ? leaf.DeclaringType.Name : t.type.Name, leaf.Name, it.typed) ?? Tunables.CheckReflect(t.type.Name, leaf.Name, it.typed);
            if (range != null) throw new McpException(McpErr.BadArgs, range, "resource inside://world/tunables lists the ranges");
            try { it.before = ReflectValues.ReadPath(pi, t.instance); it.readBefore = true; } catch { it.before = null; }
            try { ReflectValues.WritePath(pi, t.instance, it.typed); }
            catch (Exception e) { throw new McpException(McpErr.GameError, member + ": " + ReflectValues.Inner(e).GetType().Name + ": " + ReflectValues.Inner(e).Message); }
            try { it.after = ReflectValues.ReadPath(pi, t.instance); it.readAfter = true; it.sameAfter = ReflectValues.Same(it.after, it.typed); }
            catch { it.readAfter = false; }
            it.rb = it.readBefore ? Render(it.before) : null;
            it.ra = it.readAfter ? Render(it.after) : null;
            it.type = TypeName(it.typed, pi.leafType);
            if (!restore) return;
            if (!it.readBefore) { warnings.Add("restore_on_end: the old value of " + member + " could not be read, so no undo was recorded"); return; }
            object inst = t.instance, old = it.before;
            Type ty = t.type;
            World.Restore("reflect:" + t.label + "/" + member, "reflect set " + t.label + "." + member, () =>
            {
                if (Gone(inst)) return "gone (the object was destroyed; a new one has the game's own value)";
                var p2 = ReflectValues.Resolve(ty, inst, member);
                if (p2.error != null) return "gone: " + p2.error;
                ReflectValues.WritePath(p2, inst, old);
                return "put back " + ReflectValues.Compact(Render(old));
            });
        }

        static ToolResult Set(Json.Obj a, CallContext ctx)
        {
            var list = PlayShared.List(a, "sets");
            bool batch = list != null && list.Count > 0;
            var items = new List<Item>();
            if (batch)
            {
                if (list.Count > MaxSets) return ToolResult.Fail(McpErr.BadArgs, "sets: at most " + MaxSets + " per call");
                foreach (var x in list)
                {
                    var so = x as Json.Obj;
                    string name = so != null ? so.Str("member", null) : null;
                    if (string.IsNullOrEmpty(name) || !so.ContainsKey("value")) return ToolResult.Fail(McpErr.BadArgs, "sets: each entry is {member, value}", "value: number, string, bool, null, [x,y,z] ...");
                    items.Add(new Item { member = name, json = so["value"], hasValue = true });
                }
            }
            else
            {
                if (a == null || !a.ContainsKey("value")) return ToolResult.Fail(McpErr.BadArgs, "op set needs value", "value: number, string, bool, null, [x,y,z] ...");
                items.Add(new Item { member = PlayShared.Str(a, "member", null), json = a["value"], hasValue = true });
            }
            int k = PlayShared.Int(a, "verify_frames", 2);
            bool restore = PlayShared.Bool(a, "restore_on_end", false);
            var warnings = new List<string>();
            Target target = null;
            bool frozen = false;
            ctx.MainDo(() =>
            {
                target = Resolve(a);
                warnings.AddRange(target.warnings);
                foreach (var it in items)
                {
                    try { WriteOne(target, it, restore, warnings); }
                    catch (Exception e)
                    {
                        if (!batch) throw;
                        it.error = Wrap(it.member, e);
                    }
                }
                frozen = World.Frozen();
            });
            bool any = false;
            foreach (var it in items) if (it.error == null) any = true;
            bool later = any && k > 0 && World.WaitVerify(ctx, k, warnings, frozen);
            if (later)
            {
                try
                {
                    ctx.MainDo(() =>
                    {
                        if (Gone(target.instance)) return;
                        foreach (var it in items)
                        {
                            if (it.error != null) continue;
                            try
                            {
                                var pi = ReflectValues.Resolve(target.type, target.instance, it.member);
                                if (pi.error != null) continue;
                                it.afterK = ReflectValues.ReadPath(pi, target.instance);
                                it.readK = true;
                                it.sameK = ReflectValues.Same(it.afterK, it.typed);
                                it.rk = Render(it.afterK);
                            }
                            catch (Exception) { }
                        }
                    });
                }
                catch (McpException e) { warnings.Add("after_k: " + e.Message); }
            }
            var o = PlayShared.Env();
            o["outcome"] = "done";
            if (!batch)
            {
                var it = items[0];
                o["verdict"] = ReflectValues.Verdict(it.readAfter, it.sameAfter, k > 0, it.readK, it.sameK);
                o["before"] = it.rb;
                o["after"] = it.ra;
                if (k > 0) o["after_k"] = it.rk;
                o["type"] = it.type;
            }
            else
            {
                var rows = new List<object>();
                var verdicts = new List<string>();
                int failed = 0;
                foreach (var it in items)
                {
                    var row = new Json.Obj();
                    row["member"] = it.member;
                    if (it.error != null) { row["error"] = ErrorOf(it.error); failed++; }
                    else
                    {
                        string v = ReflectValues.Verdict(it.readAfter, it.sameAfter, k > 0, it.readK, it.sameK);
                        verdicts.Add(v);
                        row["verdict"] = v;
                        row["before"] = it.rb;
                        row["after"] = it.ra;
                        if (k > 0) row["after_k"] = it.rk;
                        row["type"] = it.type;
                    }
                    rows.Add(row);
                }
                o["results"] = rows;
                string worst = ReflectValues.Worst(verdicts);
                if (worst != null) o["verdict"] = worst;
                if (!any) o["outcome"] = "refused";
                if (failed > 0) warnings.Add(failed + " of " + items.Count + " writes were not made (see results[].error); the others were");
            }
            if (warnings.Count > 0) o["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(o);
        }
    }
}
