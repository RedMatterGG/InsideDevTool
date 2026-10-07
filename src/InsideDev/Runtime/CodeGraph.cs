using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace InsideDev
{
    // Game logic from code (spec: "how does the game reach X and who sets it").
    //
    // A static pass over the IL of every type in Assembly-CSharp(-firstpass) - components AND plain classes such as
    // SpawnManager - with a small stack simulation, so every member access gets a readable receiver
    // ("WristSecret.instance.Activate()", "this.lightAfter.SetActive(False)") and the condition that guards it
    // ("if User.Achievement.IsAcquired(49)"). From that it builds:
    //   effects   per method, in code order: reads / writes / calls / signals fired / sounds posted / SetActive ...
    //   entries   how a method gets run: Unity message (Start, Update, OnTriggerEnter ...), signal input
    //             (SignalIn.Create("name", ..., Method)), coroutine / lambda of another method, Invoke("name"),
    //             animation events (added at runtime from clips), otherwise "called by ..."
    //   indexes   member -> readers / writers / callers, so "who sets WristSecret.isActive" is a lookup, including
    //             indirect writes through a setter method (Activate() writes isActive).
    // Provenance: CODE (static analysis). Guards are best-effort text of the branch condition; "≈" marks receivers
    // the simulation could not follow exactly. The pass never runs game code.
    // No UnityEngine dependency: builds into the offline test harness too.
    public static class CodeGraph
    {
        public sealed class Effect
        {
            public string kind;            // read, write, call, signal, sound, setActive, enable, instantiate, destroy, anim, invoke
            public Type type; public string member; public string recv; public string guard; public string args;
            public bool isCall;                // a call to a method (not a field access)
            public string Text()
            {
                string r = recv != null ? recv + "." : "";
                switch (kind)
                {
                    case "read": return "reads " + r + member;
                    case "write": return "sets " + r + member + (args != null ? " = " + args : "");
                    case "signal": return "fires signal " + r.TrimEnd('.');
                    case "sound": return "posts sound " + r.TrimEnd('.') + (member != "PostFast" && member != "Post" ? " (" + member + ")" : "");
                    case "lookup": return r + member + "<" + (type != null ? type.Name : "?") + ">(" + (args ?? "") + ")";
                    default: return r + member + "(" + (args ?? "") + ")";
                }
            }
        }

        public sealed class MInfo
        {
            public Type type; public string name; public readonly List<Effect> effects = new List<Effect>();
            public readonly List<string> entries = new List<string>();
            public readonly List<MInfo> callers = new List<MInfo>();
            public string Key { get { return type.FullName + "::" + name; } }
            public string Short { get { return type.Name + "." + name; } }
        }

        public static readonly Dictionary<string, MInfo> methods = new Dictionary<string, MInfo>();
        public static readonly Dictionary<string, List<KeyValuePair<MInfo, Effect>>> uses = new Dictionary<string, List<KeyValuePair<MInfo, Effect>>>();  // "Type::member" -> accesses
        public static readonly Dictionary<Type, List<MInfo>> byType = new Dictionary<Type, List<MInfo>>();
        public static readonly Dictionary<string, MInfo> signalInputs = new Dictionary<string, MInfo>();   // "Type::signalName" -> handler
        public static bool done; public static int scannedMethods, effectsCount; static int qi;
        static List<MethodBase> queue;
        static readonly Dictionary<short, OpCode> ops = new Dictionary<short, OpCode>();
        public static Type ComponentType, GameObjectType, BehaviourType, UObjType;

        static readonly HashSet<string> unityMessages = new HashSet<string> {
            "Awake","Start","Update","FixedUpdate","LateUpdate","OnEnable","OnDisable","OnDestroy","OnTriggerEnter","OnTriggerExit","OnTriggerStay",
            "OnCollisionEnter","OnCollisionExit","OnCollisionStay","OnBecameVisible","OnBecameInvisible","OnAnimatorMove","OnAnimatorIK","OnGUI",
            "OnLevelWasLoaded","OnApplicationQuit","OnApplicationFocus","OnApplicationPause","OnPreRender","OnPostRender","OnRenderObject","OnWillRenderObject","Reset","OnValidate" };

        // ---------------------------------------------------------------- scanning (budgeted)
        public static void Step(double budgetMs)
        {
            if (done) return;
            if (queue == null) Init();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (qi < queue.Count && sw.Elapsed.TotalMilliseconds < budgetMs) { try { Scan(queue[qi]); } catch { } qi++; scannedMethods++; }
            if (qi >= queue.Count) { done = true; queue = null; Finish(); pool.Clear(); }
        }
        public static int Pending { get { return queue == null ? 0 : queue.Count - qi; } }

        static void Init()
        {
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            { var o = f.GetValue(null); if (o is OpCode) { var oc = (OpCode)o; ops[oc.Value] = oc; } }
            queue = new List<MethodBase>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = asm.GetName().Name;
                if (n == "UnityEngine" && ComponentType == null) { ComponentType = asm.GetType("UnityEngine.Component"); GameObjectType = asm.GetType("UnityEngine.GameObject"); BehaviourType = asm.GetType("UnityEngine.Behaviour"); UObjType = asm.GetType("UnityEngine.Object"); }
                if (n != "Assembly-CSharp" && n != "Assembly-CSharp-firstpass") continue;
                Type[] ts; try { ts = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types; }
                foreach (var t in ts)
                {
                    if (t == null) continue;
                    const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                    try { foreach (var m in t.GetMethods(F)) queue.Add(m); foreach (var c in t.GetConstructors(F)) queue.Add(c); } catch { }
                }
            }
        }

        static bool IsGameType(Type t)
        {
            if (t == null) return false;
            string a = t.Assembly.GetName().Name;
            return a == "Assembly-CSharp" || a == "Assembly-CSharp-firstpass";
        }

        // compiler-generated coroutine / lambda / closure types map back to the method that wrote them
        static void Owner(MethodBase m, out Type type, out string name, out string how)
        {
            type = m.DeclaringType; name = m.Name; how = null;
            if (name.StartsWith("<") && name.IndexOf('>') > 1) { how = "lambda in " + name.Substring(1, name.IndexOf('>') - 1); name = name.Substring(1, name.IndexOf('>') - 1); }
            while (type != null && type.Name.StartsWith("<") && type.DeclaringType != null)
            {
                string tn = type.Name;
                int gt = tn.IndexOf('>');
                if (gt > 1) { name = tn.Substring(1, gt - 1); how = tn.Contains("Iterator") ? "coroutine" : "closure"; }
                type = type.DeclaringType;
            }
        }

        static MInfo Get(Type t, string name)
        {
            string k = t.FullName + "::" + name;
            MInfo mi;
            if (!methods.TryGetValue(k, out mi))
            {
                mi = new MInfo { type = t, name = name };
                methods[k] = mi;
                List<MInfo> l; if (!byType.TryGetValue(t, out l)) { l = new List<MInfo>(); byType[t] = l; } l.Add(mi);
            }
            return mi;
        }

        struct V { public string text; public Type type; public V(string s, Type t) { text = s; type = t; } }

        static int OperandSize(OperandType t, byte[] il, int p)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8: case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return 4 + 4 * BitConverter.ToInt32(il, p);
                default: return 4;
            }
        }

        static int Pops(StackBehaviour s)
        {
            switch (s)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
                case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi: case StackBehaviour.Popi_popi8: case StackBehaviour.Popi_popr4:
                case StackBehaviour.Popi_popr8: case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: return 2;
                case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8: case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref: return 3;
            }
            return 0;
        }
        static int Pushes(StackBehaviour s)
        {
            switch (s)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1_push1: return 2;
                case StackBehaviour.Push1: case StackBehaviour.Pushi: case StackBehaviour.Pushi8: case StackBehaviour.Pushr4: case StackBehaviour.Pushr8: case StackBehaviour.Pushref: return 1;
            }
            return 0;
        }

        static void Scan(MethodBase m)
        {
            byte[] il; MethodBody body;
            try { body = m.GetMethodBody(); if (body == null) return; il = body.GetILAsByteArray(); } catch { return; }
            if (il == null || il.Length == 0) return;
            Type ot; string oname, how;
            Owner(m, out ot, out oname, out how);
            if (ot == null) return;
            var info = Get(ot, oname);
            if (how == null && unityMessages.Contains(oname) && ComponentType != null && ComponentType.IsAssignableFrom(ot)) Entry(info, "Unity message " + oname);
            if (how == "coroutine") Entry(info, "(runs as a coroutine)");
            var mod = m.Module;
            Type[] targs = null, margs = null;
            try { if (m.DeclaringType.IsGenericType) targs = m.DeclaringType.GetGenericArguments(); if (m.IsGenericMethod) margs = m.GetGenericArguments(); } catch { }
            var ps = m.GetParameters();
            var locals = body.LocalVariables;
            var stack = new List<V>();
            var guards = new List<KeyValuePair<int, string>>();   // (end offset, condition text)
            var localText = new Dictionary<int, string>();
            bool guardDirty = false; string guardText = null;          // what a local was last set to ("bool flag2 = IsAcquired(...)" -> its text)
            int p = 0;
            while (p < il.Length)
            {
                int at = p;
                if (guards.RemoveAll(g => at >= g.Key) > 0) guardDirty = true;
                short v = il[p];
                if (v == 0xFE && p + 1 < il.Length) { v = (short)(0xFE00 | il[p + 1]); p += 2; } else p += 1;
                OpCode oc;
                if (!ops.TryGetValue(v, out oc)) return;
                int size = OperandSize(oc.OperandType, il, p);
                int tok = size == 4 ? BitConverter.ToInt32(il, p) : 0;
                string name = oc.Name;
                // nested conditions all hold: "if A && B" (the compiler turns "if (a && b)" into two branches)
                if (guardDirty) { guardDirty = false; guardText = guards.Count == 0 ? null : guards.Count == 1 ? guards[0].Value : JoinGuards(guards); }
                string guard = guardText;
                try
                {
                    if (name.StartsWith("ldarg"))
                    {
                        int idx = name == "ldarg.0" ? 0 : name == "ldarg.1" ? 1 : name == "ldarg.2" ? 2 : name == "ldarg.3" ? 3 : size == 1 ? il[p] : size == 2 ? BitConverter.ToInt16(il, p) : 0;
                        if (!m.IsStatic) { if (idx == 0) { stack.Add(new V("this", m.DeclaringType)); p += size; continue; } idx--; }
                        stack.Add(idx < ps.Length ? new V(ps[idx].Name, ps[idx].ParameterType) : new V("arg", null));
                    }
                    else if (name.StartsWith("ldloc"))
                    {
                        int idx = name == "ldloc.0" ? 0 : name == "ldloc.1" ? 1 : name == "ldloc.2" ? 2 : name == "ldloc.3" ? 3 : size == 1 ? il[p] : size == 2 ? BitConverter.ToInt16(il, p) : 0;
                        Type lt = null; try { if (idx < locals.Count) lt = locals[idx].LocalType; } catch { }
                        string lv; if (localText.TryGetValue(idx, out lv)) stack.Add(new V(lv, lt));
                        else stack.Add(new V(lt != null ? "(" + lt.Name + " local)" : "(local)", lt));
                    }
                    else if (name.StartsWith("stloc"))
                    {
                        int idx = name == "stloc.0" ? 0 : name == "stloc.1" ? 1 : name == "stloc.2" ? 2 : name == "stloc.3" ? 3 : size == 1 ? il[p] : size == 2 ? BitConverter.ToInt16(il, p) : 0;
                        var val = Pop(stack);
                        // remember readable values only (method results, fields, constants); loop counters etc. stay "(local)"
                        if (val.text != null && val.text.Length > 0 && val.text.Length < 90 && val.text != "≈" && !val.text.StartsWith("(")) localText[idx] = val.text; else localText.Remove(idx);
                    }
                    else if (name.StartsWith("ldc.i4")) { int k = name == "ldc.i4" ? tok : name == "ldc.i4.s" ? (sbyte)il[p] : name == "ldc.i4.m1" ? -1 : name[name.Length - 1] - '0'; stack.Add(new V(k.ToString(), typeof(int))); }
                    else if (name == "ldc.r4") { stack.Add(new V(BitConverter.ToSingle(il, p).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), typeof(float))); }
                    else if (name == "ldstr") { string s = null; try { s = mod.ResolveString(tok); } catch { } stack.Add(new V("\"" + s + "\"", typeof(string))); }
                    else if (name == "ldnull") stack.Add(new V("null", null));
                    else if (name == "ldsfld" || name == "ldfld" || name == "ldflda" || name == "ldsflda")
                    {
                        var f = mod.ResolveField(tok, targs, margs);
                        V recv = name.StartsWith("lds") ? new V(f.DeclaringType.Name, f.DeclaringType) : Pop(stack);
                        string txt = (recv.text == "this" && !name.StartsWith("lds") ? "this" : recv.text) + "." + f.Name;
                        if (!name.StartsWith("lds") && recv.text == "this") txt = "this." + f.Name;
                        if (IsGameType(f.DeclaringType) && f.DeclaringType != ot && !f.DeclaringType.Name.StartsWith("<"))
                            Add(info, new Effect { kind = "read", type = f.DeclaringType, member = f.Name, recv = name.StartsWith("lds") ? f.DeclaringType.Name : recv.text, guard = guard });
                        stack.Add(new V(txt, f.FieldType));
                    }
                    else if (name == "stfld" || name == "stsfld")
                    {
                        var f = mod.ResolveField(tok, targs, margs);
                        V val = Pop(stack); V recv = name == "stfld" ? Pop(stack) : new V(f.DeclaringType.Name, f.DeclaringType);
                        if (IsGameType(f.DeclaringType) && !f.DeclaringType.Name.StartsWith("<"))
                            Add(info, new Effect { kind = "write", type = f.DeclaringType, member = f.Name, recv = recv.text, args = Trim(val.text), guard = guard });
                    }
                    else if (oc.FlowControl == FlowControl.Call && oc.OperandType == OperandType.InlineMethod)
                    {
                        var mb = mod.ResolveMethod(tok, targs, margs);
                        var pars = mb.GetParameters();
                        var args = new List<V>();
                        for (int i = 0; i < pars.Length; i++) args.Insert(0, Pop(stack));
                        for (int i = 0; i < pars.Length && i < args.Count; i++)
                        {
                            int iv; var pt = pars[i].ParameterType;
                            if (pt.IsEnum && int.TryParse(args[i].text, out iv)) { string en = null; try { en = Enum.GetName(pt, iv); } catch { } if (en != null) args[i] = new V(pt.Name + "." + en, pt); }
                        }
                        V recv = (!mb.IsStatic && name != "newobj") ? Pop(stack) : new V(mb.DeclaringType.Name, mb.DeclaringType);
                        string argText = Args(args);
                        Type ret = mb is MethodInfo ? ((MethodInfo)mb).ReturnType : mb.DeclaringType;
                        CallEffect(info, mb, recv, args, argText, guard, name);
                        if (name == "newobj" && typeof(Delegate).IsAssignableFrom(mb.DeclaringType) && args.Count >= 2 && args[1].text.StartsWith("&"))
                            stack.Add(new V(args[1].text, mb.DeclaringType));
                        else if (name == "newobj" || (ret != null && ret != typeof(void)))
                        {
                            string mname = mb.Name.StartsWith("get_") ? mb.Name.Substring(4) : mb.Name;
                            string t = name == "newobj" ? "new " + mb.DeclaringType.Name + "(" + argText + ")" : (recv.text + "." + mname + (mb.Name.StartsWith("get_") ? "" : "(" + argText + ")"));
                            stack.Add(new V(t, ret));
                        }
                    }
                    else if (name == "ldftn" || name == "ldvirtftn")
                    {
                        var mb = mod.ResolveMethod(tok, targs, margs);
                        if (name == "ldvirtftn") Pop(stack);
                        stack.Add(new V("&" + mb.Name, null));
                    }
                    else if (oc.FlowControl == FlowControl.Cond_Branch)
                    {
                        int target = p + size + (size == 1 ? (sbyte)il[p] : tok);
                        string cond = null;
                        if (name.StartsWith("brfalse")) cond = Pop(stack).text;                    // fallthrough runs when cond is true
                        else if (name.StartsWith("brtrue")) cond = "!" + Pop(stack).text;
                        else if (oc.OperandType != OperandType.InlineSwitch) { var b = Pop(stack); var a = Pop(stack); cond = a.text + Inv(name) + b.text; }
                        else Pop(stack);
                        if (cond != null && target > p + size) { guards.Add(new KeyValuePair<int, string>(target, Trim(cond))); guardDirty = true; }
                    }
                    else if (oc.FlowControl == FlowControl.Branch || oc.FlowControl == FlowControl.Return || oc.FlowControl == FlowControl.Throw) stack.Clear();
                    else if (name == "ceq" || name == "cgt" || name == "clt" || name == "cgt.un" || name == "clt.un")
                    { var b = Pop(stack); var a = Pop(stack); stack.Add(new V("(" + a.text + (name == "ceq" ? " == " : name.StartsWith("cgt") ? " > " : " < ") + b.text + ")", typeof(bool))); }
                    else if (name == "dup") { var t = Pop(stack); stack.Add(t); stack.Add(t); }
                    else if (name == "isinst" || name == "castclass" || name == "box" || name == "unbox.any" || name.StartsWith("conv")) { var t = Pop(stack); stack.Add(t); }
                    else
                    {
                        int np = Pops(oc.StackBehaviourPop), nu = Pushes(oc.StackBehaviourPush);
                        for (int i = 0; i < np; i++) Pop(stack);
                        for (int i = 0; i < nu; i++) stack.Add(new V("≈", null));
                    }
                }
                catch { stack.Clear(); }
                p += size;
            }
        }

        static string JoinGuards(List<KeyValuePair<int, string>> g)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < g.Count; i++) { if (sb.Length > 0) sb.Append(" && "); sb.Append(g[i].Value); if (sb.Length > 200) { sb.Append(" …"); break; } }
            return sb.ToString();
        }

        static string Inv(string br)
        {
            // branch jumps away when the relation holds, so the guarded fallthrough runs when it does not
            if (br.StartsWith("beq")) return " != "; if (br.StartsWith("bne")) return " == ";
            if (br.StartsWith("bge")) return " < "; if (br.StartsWith("bgt")) return " <= "; if (br.StartsWith("ble")) return " > "; if (br.StartsWith("blt")) return " >= ";
            return " ? ";
        }

        static V Pop(List<V> s) { if (s.Count == 0) return new V("≈", null); var v = s[s.Count - 1]; s.RemoveAt(s.Count - 1); return v; }
        static string Trim(string s) { if (s == null) return null; return s.Length > 90 ? s.Substring(0, 88) + "…" : s; }
        static string Args(List<V> a) { var sb = new StringBuilder(); for (int i = 0; i < a.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(a[i].text); } return Trim(sb.ToString()); }

        static void Entry(MInfo mi, string e) { if (!mi.entries.Contains(e)) mi.entries.Add(I(e)); }

        // string pool: effect texts repeat a lot (receivers, guards, member names) - one copy each keeps the
        // graph small, which matters because Mono's GC pause grows with the live heap
        static readonly Dictionary<string, string> pool = new Dictionary<string, string>();
        static string I(string s) { if (s == null) return null; string p; if (pool.TryGetValue(s, out p)) return p; pool[s] = s; return s; }

        static void Add(MInfo mi, Effect e)
        {
            e.kind = I(e.kind); e.member = I(e.member); e.recv = I(e.recv); e.guard = I(e.guard); e.args = I(e.args);
            foreach (var x in mi.effects) if (x.kind == e.kind && x.type == e.type && x.member == e.member && x.recv == e.recv && x.guard == e.guard && x.args == e.args) return;
            mi.effects.Add(e); effectsCount++;
        }

        static void CallEffect(MInfo info, MethodBase mb, V recv, List<V> args, string argText, string guard, string op)
        {
            var dt = mb.DeclaringType;
            string n = mb.Name;
            // signal input registration: SignalIn.Create("name", go, Handler)
            if (dt.Name == "SignalIn" && n == "Create" && args.Count >= 3 && args[2].text.StartsWith("&"))
            {
                string sig = args[0].text.Trim('"'), handler = args[2].text.Substring(1);
                var h = Get(info.type, handler);
                Entry(h, "signal input '" + sig + "'");
                signalInputs[info.type.FullName + "::" + sig] = h;
                return;
            }
            // delegate hookup to own methods: new Action(&M) etc. -> M runs via that delegate
            if (op == "newobj" && args.Count >= 2 && args[1].text.StartsWith("&") && typeof(Delegate).IsAssignableFrom(dt))
            { Entry(Get(info.type, args[1].text.Substring(1)), "delegate created in " + info.name); return; }
            // Invoke("M") / InvokeRepeating / StartCoroutine("M") / SendMessage("M")
            if ((n == "Invoke" || n == "InvokeRepeating" || n == "StartCoroutine" || n == "SendMessage" || n == "BroadcastMessage") && args.Count > 0 && args[0].text.StartsWith("\""))
            { Entry(Get(info.type, args[0].text.Trim('"')), n + " from " + info.name); Add(info, new Effect { kind = "invoke", type = info.type, member = n, recv = recv.text, args = argText, guard = guard, isCall = mb != null }); return; }
            // generic lookups of a game type: GetComponent<T>, GetComponentsInChildren<T>, FindObjectOfType<T>, AddComponent<T> ...
            if (mb.IsGenericMethod && (n.StartsWith("GetComponent") || n.StartsWith("FindObject") || n == "AddComponent" || n.StartsWith("GetComponents")))
            {
                Type ga = null; try { ga = mb.GetGenericArguments()[0]; } catch { }
                if (ga != null && IsGameType(ga) && ga != info.type)
                { Add(info, new Effect { kind = "lookup", type = ga, member = n, recv = recv.text, args = argText, guard = guard, isCall = mb != null }); return; }
            }
            string kind = null;
            if (dt.Name == "SignalOut" && n == "Signal") kind = "signal";
            else if ((dt.Name == "AudioEventSimple" || dt.Name == "AudioEventWithCallback" || dt.Name == "SoundEngine") && n.StartsWith("Post")) kind = "sound";
            else if (GameObjectType != null && dt == GameObjectType && n == "SetActive") kind = "setActive";
            else if (n == "set_enabled" && UObjType != null && UObjType.IsAssignableFrom(dt)) kind = "enable";
            else if (n == "Instantiate") kind = "instantiate";
            else if (n == "Destroy" || n == "DestroyImmediate") kind = "destroy";
            else if ((dt.Name == "Animation" || dt.Name == "Animator") && (n.StartsWith("Play") || n.StartsWith("CrossFade") || n.StartsWith("Set") || n == "Stop")) kind = "anim";
            else if (IsGameType(dt) && !dt.Name.StartsWith("<")) kind = n.StartsWith("get_") ? "read" : n.StartsWith("set_") ? "write" : "call";
            if (kind == null) return;
            string member = n.StartsWith("get_") || n.StartsWith("set_") ? n.Substring(4) : n;
            Type owner; string oname, how; Owner(mb, out owner, out oname, out how);
            // base.M() from an override is not a trigger of the base method (it is the override running) - skip it
            bool baseCall = owner != null && owner != info.type && owner.IsAssignableFrom(info.type) && oname == info.name;
            if (IsGameType(dt) && owner != null && !baseCall) { var callee = Get(owner, oname); if (!callee.callers.Contains(info)) callee.callers.Add(info); }
            if (kind == "read" && dt == info.type) return;
            if (kind == "call" && dt == info.type && owner == info.type) { Add(info, new Effect { kind = "call", type = dt, member = oname, recv = "this", args = argText, guard = guard, isCall = mb != null }); return; }
            Add(info, new Effect { kind = kind, type = dt, member = member, recv = kind == "signal" || kind == "sound" ? recv.text : recv.text, args = kind == "write" ? (args.Count > 0 ? args[args.Count - 1].text : null) : argText, guard = guard, isCall = mb != null });
        }

        // indexes: member -> accesses; setters: a method that writes field F of its own type makes its callers
        // indirect writers of F
        static void Finish()
        {
            uses.Clear();
            foreach (var mi in methods.Values)
                foreach (var e in mi.effects)
                {
                    if (e.type == null) continue;
                    string k = e.type.FullName + "::" + e.member;
                    List<KeyValuePair<MInfo, Effect>> l; if (!uses.TryGetValue(k, out l)) { l = new List<KeyValuePair<MInfo, Effect>>(); uses[k] = l; }
                    l.Add(new KeyValuePair<MInfo, Effect>(mi, e));
                }
            // overrides: code calling the base method (virtual dispatch) reaches the override - the base method is
            // shown as its caller so the trigger chain continues through it
            var all = new List<MInfo>(methods.Values);
            foreach (var mi in all)
                for (var bt = mi.type.BaseType; bt != null && IsGameType(bt); bt = bt.BaseType)
                {
                    MInfo bm;
                    if (methods.TryGetValue(bt.FullName + "::" + mi.name, out bm) && bm != mi)
                    { if (!mi.callers.Contains(bm)) mi.callers.Add(bm); if (!mi.entries.Contains("override of " + bt.Name + "." + mi.name)) mi.entries.Add("override of " + bt.Name + "." + mi.name); break; }
                }
        }

        public static List<KeyValuePair<MInfo, Effect>> Uses(Type t, string member)
        {
            var res = new List<KeyValuePair<MInfo, Effect>>();
            for (var x = t; x != null && IsGameType(x); x = x.BaseType)
            {
                List<KeyValuePair<MInfo, Effect>> l;
                if (uses.TryGetValue(x.FullName + "::" + member, out l)) res.AddRange(l);
            }
            return res;
        }

        static readonly Dictionary<Type, List<Access>> incomingCache = new Dictionary<Type, List<Access>>();
        public sealed class Access { public string member, verb, guard, via; public MInfo by; }
        // other code touching members declared on t (the data the report and the LOGIC graph are built from)
        public static List<Access> Incoming(Type t)
        {
            List<Access> cached;
            if (incomingCache.TryGetValue(t, out cached)) return cached;
            var res = new List<Access>();
            foreach (var kv in uses)
            {
                int sep = kv.Key.IndexOf("::");
                if (kv.Key.Substring(0, sep) != t.FullName) continue;
                string mem = kv.Key.Substring(sep + 2);
                if (mem == ".ctor") continue;
                foreach (var u in kv.Value)
                {
                    if (u.Key.type == t || u.Value.recv == "this" || (u.Value.kind == "read" && IsSingletonAccessor(t, mem))) continue;
                    res.Add(new Access { member = mem, verb = u.Value.kind == "write" ? "sets" : u.Value.kind == "read" ? "reads" : u.Value.kind == "lookup" ? "finds" : "calls", guard = u.Value.guard, via = u.Value.recv, by = u.Key });
                }
            }
            incomingCache[t] = res;
            return res;
        }

        // members of game types that code in OTHER types sets or calls: the cross-object state of the game
        public sealed class Shared { public Type type; public string member; public int setters, readers, callers; public readonly List<string> by = new List<string>(); }
        public static List<Shared> SharedState()
        {
            var res = new List<Shared>();
            foreach (var kv in uses)
            {
                int sep = kv.Key.IndexOf("::");
                string mem = kv.Key.Substring(sep + 2);
                if (mem == ".ctor") continue;
                Shared sh = null;
                bool writtenAnywhere = false;
                foreach (var u in kv.Value) if (u.Value.kind == "write") writtenAnywhere = true;
                foreach (var u in kv.Value)
                {
                    if (u.Value.type == null || u.Key.type == u.Value.type || u.Value.recv == "this") continue;
                    if (u.Value.kind == "read" && IsSingletonAccessor(u.Value.type, mem)) continue;
                    if (sh == null) sh = new Shared { type = u.Value.type, member = mem };
                    if (u.Value.kind == "write") sh.setters++; else if (u.Value.kind == "read") sh.readers++; else sh.callers++;
                    string b = u.Key.type.Name; if (!sh.by.Contains(b)) sh.by.Add(b);
                }
                if (sh != null && (sh.setters > 0 || sh.callers > 0 || (sh.readers > 0 && writtenAnywhere))) res.Add(sh);
            }
            res.Sort((a, b) => (b.setters + b.callers).CompareTo(a.setters + a.callers));
            return res;
        }

        public static MInfo Method(Type t, string name) { MInfo m; return methods.TryGetValue(t.FullName + "::" + name, out m) ? m : null; }

        // how a method gets run: its entries, else its callers (recursively, a few levels)
        public static void Triggers(MInfo mi, StringBuilder sb, string ind, int depth, HashSet<MInfo> seen)
        {
            if (mi == null || !seen.Add(mi)) return;
            foreach (var e in mi.entries) sb.Append(ind).Append("← ").Append(e).Append('\n');
            if (depth <= 0) { if (mi.callers.Count > 0) sb.Append(ind).Append("← … ").Append(mi.callers.Count).Append(" more caller(s)\n"); return; }
            foreach (var c in mi.callers)
            {
                string g = null;
                foreach (var e in c.effects) if (e.isCall && e.member == mi.name && e.type == mi.type) { g = e.guard; break; }
                sb.Append(ind).Append("← called by ").Append(c.Short).Append(g != null ? "   if " + g : "").Append('\n');
                Triggers(c, sb, ind + "    ", depth - 1, seen);
            }
            if (mi.entries.Count == 0 && mi.callers.Count == 0) sb.Append(ind).Append("← no caller found in game code (may be run by an animation event, a PlayMaker action, a Unity callback or reflection)\n");
        }

        // members of a type that other code accesses, with who reads / writes / calls them
        public static string TypeReport(Type t, int depth)
        {
            var sb = new StringBuilder();
            sb.Append("CODE LOGIC  ").Append(t.Name).Append("   (the script TYPE, game-wide: every ").Append(t.Name).Append(" in the game, not only the selected object; the object's own links are its References / Referenced by)\n");
            // incoming: grouped by member
            var members = new SortedDictionary<string, List<KeyValuePair<MInfo, Effect>>>();
            foreach (var kv in uses)
            {
                int sep = kv.Key.IndexOf("::");
                string tn = kv.Key.Substring(0, sep), mem = kv.Key.Substring(sep + 2);
                if (tn != t.FullName || mem == ".ctor") continue;       // members declared on this type (inherited ones belong to the base type's report)
                foreach (var u in kv.Value) if (u.Key.type != t && u.Value.recv != "this" && !(u.Value.kind == "read" && IsSingletonAccessor(t, mem)))
                    { List<KeyValuePair<MInfo, Effect>> l; if (!members.TryGetValue(mem, out l)) { l = new List<KeyValuePair<MInfo, Effect>>(); members[mem] = l; } l.Add(u); }
            }
            if (members.Count == 0) sb.Append("  No other game code accesses ").Append(t.Name).Append(" directly.\n");
            foreach (var kv in members)
            {
                sb.Append("  ").Append(t.Name).Append('.').Append(kv.Key);
                var own = Method(t, kv.Key);
                if (own != null) { var w = OwnWrites(own); if (w.Length > 0) sb.Append("   (method: ").Append(w).Append(')'); }
                sb.Append('\n');
                foreach (var u in kv.Value)
                {
                    string verb = u.Value.kind == "write" ? "SET by" : u.Value.kind == "read" ? "read by" : u.Value.kind == "lookup" ? "looked up (" + u.Value.member + ") by" : "called by";
                    sb.Append("    ").Append(verb).Append(" ").Append(u.Key.Short).Append("   via ").Append(u.Value.recv ?? "?");
                    if (u.Value.guard != null) sb.Append("   if ").Append(u.Value.guard);
                    sb.Append('\n');
                    Triggers(u.Key, sb, "        ", depth, new HashSet<MInfo>());
                }
            }
            // outgoing: what this type's code does to other things
            List<MInfo> ms;
            if (byType.TryGetValue(t, out ms))
            {
                sb.Append("  WHAT ").Append(t.Name).Append(" DOES\n");
                foreach (var mi in ms)
                {
                    if (mi.effects.Count == 0) continue;
                    sb.Append("    ").Append(mi.name).Append(mi.entries.Count > 0 ? "   [" + string.Join("; ", mi.entries.ToArray()) + "]" : mi.callers.Count > 0 ? "   [called by " + CallerList(mi) + "]" : "").Append('\n');
                    int n = 0;
                    foreach (var e in mi.effects)
                    {
                        if (e.kind == "read" && e.recv == "this") continue;
                        if (++n > 25) { sb.Append("        …\n"); break; }
                        sb.Append("        ").Append(e.guard != null ? "if " + e.guard + ": " : "").Append(e.Text()).Append('\n');
                    }
                }
            }
            return sb.ToString();
        }

        // "instance"-style static field / property of the type's own type: every access to the type goes through it
        static bool IsSingletonAccessor(Type t, string member)
        {
            try
            {
                var f = t.GetField(member, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (f != null && f.FieldType.IsAssignableFrom(t)) return true;
                var p = t.GetProperty(member, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (p != null && p.PropertyType.IsAssignableFrom(t)) return true;
            }
            catch { }
            return false;
        }

        static string OwnWrites(MInfo m)
        {
            var l = new List<string>();
            foreach (var e in m.effects) if (e.kind == "write" && e.type == m.type) l.Add("sets " + e.member + (e.args != null ? " = " + e.args : ""));
            return string.Join(", ", l.ToArray());
        }
        static string CallerList(MInfo mi) { var l = new List<string>(); foreach (var c in mi.callers) { if (l.Count >= 4) { l.Add("…"); break; } l.Add(c.Short); } return string.Join(", ", l.ToArray()); }

        public static Type FindType(string name)
        {
            foreach (var t in byType.Keys) if (t.Name == name || t.FullName == name) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = asm.GetName().Name;
                if (n != "Assembly-CSharp" && n != "Assembly-CSharp-firstpass") continue;
                var t = asm.GetType(name); if (t != null) return t;
            }
            return null;
        }
    }
}
