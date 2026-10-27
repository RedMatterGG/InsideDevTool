using System;
using System.Collections.Generic;
using System.Reflection;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // Reflect.cs (owner WORLD_B, Verdicts and the reflect schema frozen): research by reflection (report 05 items 6, 12).
    // get / set any field or property of a component (or a static member of a type), call a method with typed JSON
    // arguments (string, number, bool, null, [x,y,z] -> Vector3, [x,y] -> Vector2, enums by name, object refs by id or
    // path), find_methods, describe_type. set re-reads now and after verify_frames frames:
    //   confirmed = both reads show the value; reverted_by_game = the first read had it, the later one not;
    //   not_applied = the first read already differs; unreadable = could not read back.
    // Values are rendered with depth 2, at most 2000 chars each. Undo: set records the old value as Restorer key
    // "reflect:<object>/<member>" only when restore_on_end is true.
    // Members may be dotted paths ("properties.swim.infiniteAir"); value-type owners are written back. Targets: object
    // (+ component, exact type name, then full name, then suffix; omitted = the GameObject itself) or type (statics).
    // Refused (ReflectValues.Refusal): save writers (only with play.save_guard on), achievements / Steam, OS / file /
    // process APIs, Application.Quit,
    // Remote.RunCommand, raising the master volume. Conversion is strict: out-of-range numbers are errors, never clamped.
    public static class Reflect
    {
        public const string Owner = "WORLD_B";
        public static readonly string[] Verdicts = { "confirmed", "reverted_by_game", "not_applied", "unreadable" };

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("reflect", "Reflection", PlayShared.Dev, Owner,
                "Read and change anything by reflection: get / set a field or property (verified: confirmed, reverted_by_game, not_applied, unreadable), call a method with JSON arguments, find_methods, describe_type. object + component for instances, type for statics. member may be a dotted path (properties.swim.infiniteAir). Arguments: numbers, strings, bools, null, [x,y,z] for Vector3, enum names, object ids or paths for object parameters. Steam and OS calls and raising the master volume are refused (save writes too when the user set play.save_guard = 1).",
                Schema.Obj().Enum("op", "get, set, call, find_methods or describe_type.", null, "get", "set", "call", "find_methods", "describe_type")
                    .Str("object", "Object id, path, #instanceID or unique name.").Str("component", "Component type name (exact, then suffix).")
                    .Str("type", "Type name for statics, find_methods, describe_type.").Str("member", "Field, property or method name.")
                    .Any("value", "op set: new value (JSON).").Arr("args", "op call: arguments (JSON).", new Json.Obj(), 0, 8)
                    .Str("text", "op find_methods: name filter.").Int("verify_frames", "op set: re-read after frames.", 2, 0, 30)
                    .Bool("restore_on_end", "op set: undo it on restore.", false).Int("limit", "Rows.", 40, 1, 200).Required("op").Build(),
                Schema.Obj().Enum("outcome", "", null, "done", "refused", "not_found").Any("value", "op get / call result.").Str("type", "Value type.")
                    .Enum("verdict", "op set.", null, Verdicts).Any("before", "").Any("after", "").Any("after_k", "")
                    .Arr("methods", "op find_methods.", Schema.OutOf(Schema.Obj().Str("name", "").Str("signature", "").Bool("static", "").Str("declaring", "")), 0, 200)
                    .Arr("members", "op describe_type.", Schema.OutOf(Schema.Obj().Str("name", "").Str("kind", "field, property, method, event").Str("type", "").Bool("static", "")), 0, 400)
                    .Arr("warnings", "", Schema.Str(), 0, 20).BuildOutput()).Destructive(), Handle);
        }

        sealed class Target
        {
            public Type type;
            public object instance;
            public string label;
            public readonly List<string> warnings = new List<string>();
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "get");
            switch (op)
            {
                case "get": return Get(a, ctx);
                case "set": return Set(a, ctx);
                case "call": return CallOp(a, ctx);
                case "find_methods": return FindMethods(a, ctx);
                case "describe_type": return Describe(a, ctx);
            }
            return ToolResult.Fail(McpErr.BadArgs, "unknown op " + op);
        }

        // ---------------------------------------------------------------- targets
        static IEnumerable<Assembly> Assemblies() { return AppDomain.CurrentDomain.GetAssemblies(); }

        internal static Type TypeByName(string name)
        {
            List<string> cands;
            var t = ReflectValues.FindType(name, Assemblies(), out cands);
            if (t != null) return t;
            if (cands.Count > 1)
            {
                var e = new McpException(McpErr.Ambiguous, "type '" + name + "' matches " + cands.Count + " types", "use the full name");
                e.data = new Json.Obj();
                var l = new List<object>();
                foreach (var c in cands) { if (l.Count >= 8) break; l.Add(c); }
                e.data["candidates"] = l;
                throw e;
            }
            throw new McpException(McpErr.NotFound, "no type '" + name + "'", "exact class name (Boy, CameraScript, HutongGames.PlayMaker.Fsm)");
        }

        // main thread
        static Target Resolve(Json.Obj a)
        {
            var t = new Target();
            string obj = PlayShared.Str(a, "object", null), comp = PlayShared.Str(a, "component", null), type = PlayShared.Str(a, "type", null);
            if (!string.IsNullOrEmpty(obj))
            {
                var go = World.Target(obj, "object");
                if (string.IsNullOrEmpty(comp) || comp == "GameObject")
                {
                    t.instance = go;
                    t.type = typeof(GameObject);
                    t.label = obj;
                }
                else
                {
                    var c = FindComponent(go, comp, t.warnings);
                    t.instance = c;
                    t.type = c.GetType();
                    t.label = obj + "/" + t.type.Name;
                }
                return t;
            }
            if (!string.IsNullOrEmpty(type))
            {
                t.type = TypeByName(type);
                t.label = t.type.FullName;
                return t;
            }
            throw new McpException(McpErr.BadArgs, "object (+ component) or type is required");
        }

        internal static Component FindComponent(GameObject go, string name, List<string> warnings)
        {
            var all = go.GetComponents<Component>();
            var hits = new List<Component>();
            foreach (var c in all) if (c != null && c.GetType().Name == name) hits.Add(c);
            if (hits.Count == 0) foreach (var c in all) if (c != null && c.GetType().FullName == name) hits.Add(c);
            if (hits.Count == 0) foreach (var c in all) if (c != null && (c.GetType().Name.EndsWith(name, StringComparison.Ordinal) || c.GetType().FullName.EndsWith("." + name, StringComparison.Ordinal))) hits.Add(c);
            if (hits.Count == 0)
            {
                var names = new List<object>();
                foreach (var c in all) if (c != null) names.Add(c.GetType().Name);
                var e = new McpException(McpErr.NotFound, World.Path(go) + " has no component '" + name + "'", "components here: " + string.Join(", ", ToStrings(names)));
                e.data = new Json.Obj();
                e.data["components"] = names;
                throw e;
            }
            var types = new HashSet<Type>();
            foreach (var c in hits) types.Add(c.GetType());
            if (types.Count > 1)
            {
                var names = new List<object>();
                foreach (var x in types) names.Add(x.FullName);
                var e = new McpException(McpErr.Ambiguous, "'" + name + "' matches " + types.Count + " component types on " + World.Path(go), "use the exact type name");
                e.data = new Json.Obj();
                e.data["candidates"] = names;
                throw e;
            }
            if (hits.Count > 1 && warnings != null) warnings.Add("several " + hits[0].GetType().Name + " components on the object: using the first");
            return hits[0];
        }

        static string[] ToStrings(List<object> l)
        {
            var r = new string[l.Count];
            for (int i = 0; i < l.Count; i++) r[i] = Convert.ToString(l[i]);
            return r;
        }

        static object Render(object v) { return ReflectValues.Render(v, 2, World.ObjRender); }

        static string TypeName(object v, Type declared)
        {
            if (v != null) return ReflectValues.FriendlyName(v.GetType());
            return ReflectValues.FriendlyName(declared);
        }

        static void Refuse(Type type, string member, bool isCall, bool isWrite, object value, IList<object> args)
        {
            string hint;
            var why = ReflectValues.Refusal(type, member, isCall, isWrite, value, args, out hint);
            if (why != null) throw new McpException(McpErr.Refused, why, hint);
        }

        static void RefusePath(Type rootType, ReflectValues.PathInfo pi, string member, bool isWrite, object value)
        {
            Refuse(rootType, member, false, isWrite, value, null);
            foreach (var m in pi.members)
            {
                Refuse(m.DeclaringType, m.Name, false, isWrite, value, null);
                if (isWrite) Refuse(ReflectValues.MemberType(m), m.Name, false, isWrite, value, null);
            }
        }

        // ---------------------------------------------------------------- get
        static ToolResult Get(Json.Obj a, CallContext ctx)
        {
            string member = PlayShared.Str(a, "member", null);
            var r = ctx.Main(() =>
            {
                var t = Resolve(a);
                var pi = ReflectValues.Resolve(t.type, t.instance, member);
                if (pi.error != null) throw new McpException(McpErr.NotFound, pi.error, "describe_type lists the members");
                RefusePath(t.type, pi, member, false, null);
                object v;
                try { v = ReflectValues.ReadPath(pi, t.instance); }
                catch (Exception e) { throw new McpException(McpErr.GameError, member + ": " + ReflectValues.Inner(e).GetType().Name + ": " + ReflectValues.Inner(e).Message); }
                var o = PlayShared.Env();
                o["outcome"] = "done";
                o["value"] = Render(v);
                o["type"] = TypeName(v, pi.leafType);
                if (t.warnings.Count > 0) o["warnings"] = World.Strs(t.warnings);
                return o;
            });
            return ToolResult.Ok(r);
        }

        // ---------------------------------------------------------------- set (verified)
        static ToolResult Set(Json.Obj a, CallContext ctx)
        {
            string member = PlayShared.Str(a, "member", null);
            if (a == null || !a.ContainsKey("value")) return ToolResult.Fail(McpErr.BadArgs, "op set needs value", "value: number, string, bool, null, [x,y,z] ...");
            object json = a["value"];
            int k = PlayShared.Int(a, "verify_frames", 2);
            bool restore = PlayShared.Bool(a, "restore_on_end", false);
            var warnings = new List<string>();
            object typed = null, before = null, after = null;
            bool readAfter = false, sameAfter = false;
            string label = null;
            ctx.MainDo(() =>
            {
                var t = Resolve(a);
                warnings.AddRange(t.warnings);
                label = t.label;
                var pi = ReflectValues.Resolve(t.type, t.instance, member);
                if (pi.error != null) throw new McpException(McpErr.NotFound, pi.error, "describe_type lists the members");
                string err;
                typed = ReflectValues.Convert(json, pi.leafType, World.RefFor, out err);
                if (err != null) throw new McpException(McpErr.BadArgs, member + ": " + err);
                RefusePath(t.type, pi, member, true, typed ?? json);
                var leaf = pi.members[pi.members.Count - 1];
                string range = Tunables.CheckReflect(leaf.DeclaringType != null ? leaf.DeclaringType.Name : t.type.Name, leaf.Name, typed) ?? Tunables.CheckReflect(t.type.Name, leaf.Name, typed);
                if (range != null) throw new McpException(McpErr.BadArgs, range, "resource inside://world/tunables lists the ranges");
                try { before = ReflectValues.ReadPath(pi, t.instance); } catch { before = null; }
                try { ReflectValues.WritePath(pi, t.instance, typed); }
                catch (Exception e) { throw new McpException(McpErr.GameError, member + ": " + ReflectValues.Inner(e).GetType().Name + ": " + ReflectValues.Inner(e).Message); }
                try { after = ReflectValues.ReadPath(pi, t.instance); readAfter = true; sameAfter = ReflectValues.Same(after, typed); }
                catch { readAfter = false; }
                if (restore)
                {
                    var old = before;
                    var spec = new Json.Obj();
                    foreach (var key in new[] { "object", "component", "type" }) if (PlayShared.Has(a, key)) spec[key] = a[key];
                    World.Restore("reflect:" + label + "/" + member, "reflect set " + label + "." + member, () =>
                    {
                        var t2 = Resolve(spec);
                        var p2 = ReflectValues.Resolve(t2.type, t2.instance, member);
                        if (p2.error != null) return "gone: " + p2.error;
                        ReflectValues.WritePath(p2, t2.instance, old);
                        return "put back " + ReflectValues.Compact(Render(old));
                    });
                }
            });
            object afterK = null;
            bool readK = false, sameK = false;
            bool later = k > 0 && World.WaitVerify(ctx, k, warnings);
            if (later)
            {
                try
                {
                    ctx.MainDo(() =>
                    {
                        var t = Resolve(a);
                        var pi = ReflectValues.Resolve(t.type, t.instance, member);
                        if (pi.error != null) return;
                        afterK = ReflectValues.ReadPath(pi, t.instance);
                        readK = true;
                        sameK = ReflectValues.Same(afterK, typed);
                    });
                }
                catch (McpException e) { warnings.Add("after_k: " + e.Message); }
            }
            var o = PlayShared.Env();
            o["outcome"] = "done";
            o["verdict"] = ReflectValues.Verdict(readAfter, sameAfter, k > 0, readK, sameK);
            object rb = null, ra = null, rk = null;
            ctx.MainDo(() => { rb = Render(before); ra = readAfter ? Render(after) : null; rk = readK ? Render(afterK) : null; });
            o["before"] = rb;
            o["after"] = ra;
            if (k > 0) o["after_k"] = rk;
            o["type"] = TypeName(typed, null);
            if (warnings.Count > 0) o["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(o);
        }

        // ---------------------------------------------------------------- call
        static ToolResult CallOp(Json.Obj a, CallContext ctx)
        {
            string member = PlayShared.Str(a, "member", null);
            if (string.IsNullOrEmpty(member)) return ToolResult.Fail(McpErr.BadArgs, "op call needs member (the method name)");
            var args = PlayShared.List(a, "args") ?? new List<object>();
            var r = ctx.Main(() =>
            {
                var t = Resolve(a);
                var pick = ReflectValues.ChooseMethod(t.type, member, t.instance == null, args, World.RefFor);
                if (pick.method == null)
                {
                    var e = new McpException(pick.candidates.Count == 0 ? McpErr.NotFound : McpErr.BadArgs, pick.error, "find_methods shows the signatures");
                    e.data = new Json.Obj();
                    e.data["candidates"] = World.Strs(pick.candidates);
                    throw e;
                }
                Refuse(pick.method.DeclaringType, pick.method.Name, true, false, null, args);
                Refuse(t.type, pick.method.Name, true, false, null, args);
                object v;
                try { v = pick.method.Invoke(pick.method.IsStatic ? null : t.instance, pick.args); }
                catch (TargetInvocationException e)
                {
                    var inner = ReflectValues.Inner(e);
                    throw new McpException(McpErr.GameError, member + " threw " + inner.GetType().Name + ": " + inner.Message);
                }
                catch (ArgumentException e) { throw new McpException(McpErr.BadArgs, member + ": " + e.Message); }
                var o = PlayShared.Env();
                o["outcome"] = "done";
                o["value"] = Render(v);
                o["type"] = TypeName(v, pick.method.ReturnType);
                var w = new List<string>(t.warnings);
                w.Add("called " + ReflectValues.Signature(pick.method));
                if (typeof(System.Collections.IEnumerator).IsAssignableFrom(pick.method.ReturnType)) w.Add("coroutine: the returned IEnumerator was not started (the game would StartCoroutine it)");
                o["warnings"] = World.Strs(w);
                return o;
            });
            return ToolResult.Ok(r);
        }

        // ---------------------------------------------------------------- find_methods / describe_type
        static Type TypeOf(Json.Obj a, CallContext ctx)
        {
            if (!string.IsNullOrEmpty(PlayShared.Str(a, "object", null))) return ctx.Main(() => Resolve(a).type);
            string type = PlayShared.Str(a, "type", null);
            if (string.IsNullOrEmpty(type)) throw new McpException(McpErr.BadArgs, "type (or object + component) is required");
            return TypeByName(type);
        }

        static ToolResult FindMethods(Json.Obj a, CallContext ctx)
        {
            var t = TypeOf(a, ctx);
            string text = PlayShared.Str(a, "text", "") ?? "";
            int limit = PlayShared.Int(a, "limit", 40);
            var rows = new List<object>();
            int total = 0;
            var seen = new HashSet<string>();
            for (var x = t; x != null && (x == t || !ReflectValues.StopType(x)); x = x.BaseType)
                foreach (var m in x.GetMethods(ReflectValues.Declared))
                {
                    if (m.IsSpecialName || m.Name.IndexOf('<') >= 0) continue;
                    if (text.Length > 0 && m.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var sig = ReflectValues.Signature(m);
                    if (!seen.Add(sig)) continue;
                    total++;
                    if (rows.Count >= limit) continue;
                    var o = new Json.Obj();
                    o["name"] = m.Name;
                    o["signature"] = sig;
                    o["static"] = m.IsStatic;
                    o["declaring"] = m.DeclaringType != null ? m.DeclaringType.FullName : "";
                    rows.Add(o);
                }
            var r = PlayShared.Env();
            r["outcome"] = "done";
            r["type"] = t.FullName;
            r["methods"] = rows;
            if (total > rows.Count) r["warnings"] = new List<object> { "showing " + rows.Count + " of " + total + " methods (raise limit or narrow text)" };
            return ToolResult.Ok(r);
        }

        static ToolResult Describe(Json.Obj a, CallContext ctx)
        {
            var t = TypeOf(a, ctx);
            int limit = PlayShared.Int(a, "limit", 40);
            string text = PlayShared.Str(a, "text", "") ?? "";
            var rows = new List<object>();
            int total = 0;
            Action<string, string, Type, bool> add = (name, kind, type, st) =>
            {
                if (name.IndexOf('<') >= 0 && kind != "field") return;
                if (text.Length > 0 && name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) return;
                total++;
                if (rows.Count >= limit) return;
                var o = new Json.Obj();
                o["name"] = ReflectValues.CleanName(name);
                o["kind"] = kind;
                o["type"] = ReflectValues.FriendlyName(type);
                o["static"] = st;
                rows.Add(o);
            };
            for (var x = t; x != null && (x == t || !ReflectValues.StopType(x)); x = x.BaseType)
            {
                foreach (var f in x.GetFields(ReflectValues.Declared)) add(f.Name, "field", f.FieldType, f.IsStatic);
                foreach (var p in x.GetProperties(ReflectValues.Declared))
                    if (p.GetIndexParameters().Length == 0) add(p.Name, "property", p.PropertyType, ReflectValues.IsStatic(p));
                foreach (var e in x.GetEvents(ReflectValues.Declared))
                {
                    var am = e.GetAddMethod(true);
                    add(e.Name, "event", e.EventHandlerType, am != null && am.IsStatic);
                }
                foreach (var m in x.GetMethods(ReflectValues.Declared))
                    if (!m.IsSpecialName && m.Name.IndexOf('<') < 0) add(m.Name, "method", m.ReturnType, m.IsStatic);
            }
            var chain = new List<string>();
            for (var x = t.BaseType; x != null && chain.Count < 6; x = x.BaseType) chain.Add(ReflectValues.FriendlyName(x));
            var r = PlayShared.Env();
            r["outcome"] = "done";
            r["type"] = t.FullName + (chain.Count > 0 ? " : " + string.Join(" : ", chain.ToArray()) : "");
            r["members"] = rows;
            if (total > rows.Count) r["warnings"] = new List<object> { "showing " + rows.Count + " of " + total + " members (raise limit up to 200, or filter with text)" };
            return ToolResult.Ok(r);
        }
    }
}
