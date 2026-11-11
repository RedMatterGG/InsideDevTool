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
    // Remote.RunCommand, raising the master volume, AkSoundEngine capture / dump / write / save members, writes to static
    // readonly fields and to string members named path / file / dir / folder, reads of HostSettings / EditorState /
    // token or password members. Calls and writes are allowed by origin: game code; UnityEngine
    // except its file, registry, display-mode, network and quit members; of the .NET library only the basic System
    // types, collections and text; the tool's own code only through read-only names (ReflectValues, "safety").
    // A property setter called through op call is checked as the write it is.
    // Conversion is strict: out-of-range numbers are errors, never clamped. get, set and their batch forms: PpcXReflect.cs.
    public static partial class Reflect
    {
        public const string Owner = "WORLD_B";
        public static readonly string[] Verdicts = { "confirmed", "reverted_by_game", "not_applied", "unreadable" };

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("reflect", "Reflection", PlayShared.Dev, Owner,
                "Read and change anything by reflection: get / set a field or property (verified: confirmed, reverted_by_game, not_applied, unreadable), call a method with JSON arguments, find_methods, describe_type. object + component for instances, type for statics. member may be a dotted path (properties.swim.infiniteAir). Arguments: numbers, strings, bools, null, [x,y,z] for Vector3, enum names, object ids or paths for object parameters. Steam and OS calls and raising the master volume are refused (save writes too when the user set play.save_guard = 1). Batch: get {members:[..]} reads several members in one call (values[]), set {sets:[{member, value}]} writes several and verifies them together (results[]); get {repeat_frames:n} reads again n frames later (value_k, changed). Refused: calls and writes that reach files, processes, assemblies, the registry (PlayerPrefs), the display mode, the network or quit, also through UnityEngine (Application.CaptureScreenshot, WWW ...) and through the tool's own classes; in game code every AkSoundEngine member named *Capture*, *Dump*, *Write* or *Save* (StartOutputCapture ...), writes to static readonly fields, and writes to string fields or properties named *path*, *file*, *dir* or *folder*; reading the MCP server's settings or token (HostSettings, EditorState, members named *token* or *password*). Other game methods are not checked for file access: a game method that writes its own file at its own place still runs.",
                Schema.Obj().Enum("op", "get, set, call, find_methods or describe_type.", null, "get", "set", "call", "find_methods", "describe_type")
                    .Str("object", "Object id, path, #instanceID or unique name.").Str("component", "Component type name (exact, then suffix).")
                    .Str("type", "Type name for statics, find_methods, describe_type.").Str("member", "Field, property or method name.")
                    .Any("value", "op set: new value (JSON).").Arr("args", "op call: arguments (JSON).", new Json.Obj(), 0, 8)
                    .Arr("members", "op get: several members of the same object or type in one call (dotted paths allowed); the result has values[] instead of value.", Schema.Str(), 0, 32)
                    .Arr("sets", "op set: several writes on the same object or type, each {member, value}; checked one by one, verified together; the result has results[].",
                        Schema.ObjOf(Schema.Obj().Str("member", "Field or property (dotted path allowed).").Any("value", "New value (JSON).").Required("member")), 0, 16)
                    .Int("repeat_frames", "op get: read again after this many rendered frames (value_k, changed).", 0, 0, 120)
                    .Str("text", "op find_methods: name filter.").Int("verify_frames", "op set: re-read after frames.", 2, 0, 30)
                    .Bool("restore_on_end", "op set: undo it on restore.", false).Int("limit", "Rows.", 40, 1, 200).Required("op").Build(),
                Schema.Obj().Enum("outcome", "", null, "done", "refused", "not_found").Any("value", "op get / call result.").Str("type", "Value type.")
                    .Enum("verdict", "op set (sets: the worst of results[].verdict).", null, Verdicts).Any("before", "").Any("after", "").Any("after_k", "")
                    .Any("value_k", "op get with repeat_frames: the value read again later.").Bool("changed", "op get with repeat_frames: value_k differs from value.")
                    .Arr("values", "op get with members: one row per member, in the order asked.", Schema.OutOf(Schema.Obj().Str("member", "").Any("value", "").Str("type", "")
                        .Any("value_k", "").Bool("changed", "").OutObj("error", "This member could not be read.", Schema.Obj().Str("code", "").Str("message", "").Str("hint", ""))), 0, 32)
                    .Arr("results", "op set with sets: one row per write, in the order asked.", Schema.OutOf(Schema.Obj().Str("member", "").Enum("verdict", "", null, Verdicts)
                        .Any("before", "").Any("after", "").Any("after_k", "").Str("type", "")
                        .OutObj("error", "This write was not made.", Schema.Obj().Str("code", "").Str("message", "").Str("hint", ""))), 0, 16)
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
            if (isWrite)
            {
                string wh;
                var ww = ReflectValues.WriteRefusal(pi, out wh);
                if (ww != null) throw new McpException(McpErr.Refused, ww, wh);
            }
            foreach (var m in pi.members)
            {
                Refuse(m.DeclaringType, m.Name, false, isWrite, value, null);
                if (isWrite)
                {
                    string hint;
                    var why = ReflectValues.RefusalOfValueType(ReflectValues.MemberType(m), m.Name, value, out hint);
                    if (why != null) throw new McpException(McpErr.Refused, why, hint);
                }
            }
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
                string setter = pick.method.IsSpecialName && pick.method.Name.StartsWith("set_", StringComparison.Ordinal) && pick.args.Length == 1 ? pick.method.Name.Substring(4) : null;
                if (setter != null)
                {
                    Refuse(pick.method.DeclaringType, setter, false, true, pick.args[0], null);
                    Refuse(t.type, setter, false, true, pick.args[0], null);
                    var ps = pick.method.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string) && ReflectValues.PathName(setter))
                        throw new McpException(McpErr.Refused, setter + " holds a path", "string members named path, file, dir or folder can redirect a file the game writes");
                    string range = Tunables.CheckReflect(pick.method.DeclaringType != null ? pick.method.DeclaringType.Name : t.type.Name, setter, pick.args[0]) ?? Tunables.CheckReflect(t.type.Name, setter, pick.args[0]);
                    if (range != null) throw new McpException(McpErr.BadArgs, range, "resource inside://world/tunables lists the ranges");
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
