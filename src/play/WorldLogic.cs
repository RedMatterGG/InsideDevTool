using System;
using System.Collections.Generic;
using System.Reflection;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // WorldLogic.cs (owner WORLD_B, the logic / body schemas frozen): Playdead logic ports, triggers, FSMs, physics and
    // animation (report 03 section 6) and the controllable bodies (report 02 sections 3.4, 4). Signal inputs run even on
    // inactive GameObjects, FSM events into inactive FSMs are lost: results say which case was hit (inactive_target).
    // spawn_clone uses Dynamics.Create plus the PreAwake loop (cleared at the next load). Writes are verified: re-read
    // now and after verify_frames frames -> verdict (Reflect.Verdicts). Teleport follows the safe recipe: refuse when
    // dead, in a hat, in the sub, special mode or custom animation, or loading; keep the depth plane; Character.Teleport;
    // CameraScript.FastBlend; for long jumps freeze, refresh cullers, wait AllCullersFinished, unfreeze.
    // This file: registration, triggers_near, trigger_fire, the signal ops, fsm_list and interactables_near (world flags =
    // PersistentBool rows, kind flag, set with signal_in SetTrue / SetFalse). WorldLogicObjects.cs: spawn_clone, body_set,
    // anim_set. WorldLogicBody.cs: the body tool. Savepoints write INSIDE.sav: with EditorState play.save_guard on
    // (Saves.Guard) firing one (trigger_fire, signal_in doSave) is refused unless the save lock is on, and under the lock
    // it marks the lock (Saves.MarkCheat, nothing written at quit); with the guard off (default) it is allowed. Secret / achievement objects (ReflectValues.SecretTypes: FinalSecretFade, SecretPod*, Secret,
    // SecretSuitcase, AchiementTrigger) are never driven: signal_in on them, signal_out / trigger_fire whose wires reach
    // one within 3 hops, and wiring into one are refused (they call User.Achievement.Acquire or wipe the orb bits).
    // Verified: FP/SignalManager.cs:26-32, 259-330, 470-519; FP/SignalIn.cs;
    // FP/SignalOut.cs:47-54; FP/SignalConnection.cs:69-90; AC/IsTriggeredByProbe.cs:5-140 (filters, private
    // send*Events, noOfCharacterProbesInside); FP/Savepoint.cs:106-116, 172; PM/PlayMakerFSM.cs:31, 50, 76, 90;
    // PM/HutongGames.PlayMaker/Fsm.cs:372-381, 1471; FP/PersistentBool.cs:41-70; AC/{Lever, SwitchButtonNew, Gate, Door,
    // Elevator, Hatch, ...}.cs state fields.
    public static partial class WorldLogic
    {
        public const string Owner = "WORLD_B";
        static readonly string[] TriggerKinds = { "probe", "savepoint", "killer", "camera", "fsm", "other" };

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("logic", "World logic", PlayShared.Dev, Owner,
                "Playdead world logic. Read-only: triggers_near, signal_ports, fsm_list, interactables_near (levers, buttons, gates, doors, lifts, hatches, world flags with their signal inputs). Changes: trigger_fire (enter / exit as probe, signal or teleport), signal_in, signal_out, signal_wire / signal_unwire, spawn_clone, body_set (rigidbody), anim_set (legacy Animation). Changes are re-read and get a verdict; signal_out / signal_in / fsm events cannot be undone.",
                Schema.Obj().Enum("op", "What to do.", null, "triggers_near", "trigger_fire", "signal_ports", "signal_in", "signal_out", "signal_wire", "signal_unwire",
                        "fsm_list", "interactables_near", "spawn_clone", "body_set", "anim_set")
                    .Str("object", "Object id, path, #instanceID or unique name.").Num("radius", "Near ops (m).", 15, 0.5, 200)
                    .Arr("kinds", "triggers_near filter: probe, savepoint, killer, camera, fsm, other; interactables_near: lever, button, valve, gate, door, elevator, hatch, jetcrate, flag.", Schema.Str(), 0, 8)
                    .Enum("edge", "trigger_fire.", "enter", "enter", "exit").Enum("mode", "trigger_fire.", "probe", "probe", "signal", "teleport")
                    .Str("input", "signal_in / signal_wire target input.").Str("output", "signal_out / signal_wire source output.")
                    .Str("to", "signal_wire: receiving object.").Str("filter", "fsm_list / near ops: name filter.").Str("state", "fsm_list: only FSMs in this state.")
                    .Vec("position", "spawn_clone: world position.", 3, -1e5, 1e5).Vec("rotation", "spawn_clone: euler degrees.", 3, -720, 720)
                    .Bool("preawake", "spawn_clone: run PreAwake on the clone.", true)
                    .Vec("velocity", "body_set.", 3, -1e3, 1e3).Vec("angular", "body_set.", 3, -1e3, 1e3).Vec("force", "body_set: impulse.", 3, -1e5, 1e5)
                    .Bool("kinematic", "body_set.").Bool("wake", "body_set.").Str("clip", "anim_set: clip name.").Num("time", "anim_set: seconds.", 0, 1e4)
                    .Num("speed", "anim_set.", -10, 10).Bool("play", "anim_set: play / stop.")
                    .Int("limit", "Rows.", 40, 1, 200).Int("offset", "First row.", 0, 0, 10000).Int("verify_frames", "Re-read after this many frames.", 2, 0, 30)
                    .Required("op").Build(),
                Schema.Obj().Enum("outcome", "", null, "done", "refused", "not_found").Arr("items", "Rows of the read ops.", Schema.OutOf(Schema.Obj().Open()), 0, 200)
                    .Int("total", "Rows before paging.", 0, 1000000).Enum("verdict", "", null, Reflect.Verdicts)
                    .Any("before", "State before a change.").Any("after", "Right after.").Any("after_k", "After verify_frames.")
                    .Bool("inactive_target", "The target was inactive (signals still run, FSM events are lost).").Str("note", "")
                    .Prop("changes", null, ChangeLog.Ref()).Arr("warnings", "", Schema.Str(), 0, 20).BuildOutput()).Destructive(), HandleLogic);

            PlayRegistry.Add(PlayShared.Def("body", "Bodies", PlayShared.Dev, Owner,
                "Controllable bodies: list (boy, huddle, hat dudes, submarine), teleport (safe: refuses while dead, in a hat, in the sub or scripted; keeps the depth plane; snaps the camera; waits for streaming), hat (attach / detach / status of mind-control hats, MindHat.ForceAttach / ForceDetach), swap (boy / huddle, MainCharacterSwap).",
                Schema.Obj().Enum("op", "list, teleport, hat or swap.", "list", "list", "teleport", "hat", "swap")
                    .Str("body", "Body id (b1); default the controlled one.").Num("x", "teleport: world x.", -1e5, 1e5).Num("y", "teleport: world y.", -1e5, 1e5)
                    .Num("z", "teleport: world z (default keep the depth plane).", -1e5, 1e5).Num("dx", "teleport: relative x.", -1e3, 1e3).Num("dy", "teleport: relative y.", -1e3, 1e3)
                    .Str("to_object", "teleport: to an object id or path.").Bool("snap_camera", "teleport.", true).Bool("wait_stream", "teleport: wait until streaming settles.", true)
                    .Enum("hat_op", "op hat.", "status", "attach", "detach", "status").Str("hat", "op hat: hat id or path.")
                    .Enum("to", "op swap.", null, "boy", "huddle").Bool("force", "Skip the safety refusals (may break scripted scenes).", false).Build(),
                Schema.Obj().Enum("outcome", "", null, "done", "refused", "not_found", "timeout")
                    .Arr("bodies", "op list.", Schema.OutOf(Schema.Obj().Str("id", "").Enum("kind", "", null, "boy", "huddle", "dude", "submarine").Str("name", "")
                        .Vec("pos", "", 3, -1e5, 1e5).Bool("controlled", "").Bool("active", "").Str("state", "")), 0, 16)
                    .Arr("hats", "op hat.", Schema.OutOf(Schema.Obj().Str("id", "").Str("name", "").Bool("attached", "").Str("wearer", "").Bool("turned_off", "")), 0, 16)
                    .Enum("verdict", "", null, Reflect.Verdicts).Vec("before", "Position before.", 3, -1e5, 1e5).Vec("after", "Position after.", 3, -1e5, 1e5)
                    .Arr("warnings", "", Schema.Str(), 0, 20).Prop("observe", null, Observer.ConciseRef()).BuildOutput()).Destructive(), HandleBody);
        }

        static ToolResult HandleLogic(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "");
            switch (op)
            {
                case "triggers_near": return TriggersNear(a, ctx);
                case "trigger_fire": return TriggerFire(a, ctx);
                case "signal_ports": return SignalPorts(a, ctx);
                case "signal_in": return SignalFire(a, ctx, true);
                case "signal_out": return SignalFire(a, ctx, false);
                case "signal_wire": return SignalWire(a, ctx, true);
                case "signal_unwire": return SignalWire(a, ctx, false);
                case "fsm_list": return FsmList(a, ctx);
                case "interactables_near": return InteractablesNear(a, ctx);
                case "spawn_clone": return SpawnClone(a, ctx);
                case "body_set": return BodySet(a, ctx);
                case "anim_set": return AnimSet(a, ctx);
            }
            return ToolResult.Fail(McpErr.BadArgs, "unknown op " + op);
        }

        static Json.Obj Result(List<string> warnings)
        {
            var r = PlayShared.Env();
            r["outcome"] = "done";
            return r;
        }

        static void Finish(Json.Obj r, List<string> warnings)
        {
            if (!r.ContainsKey("outcome")) r["outcome"] = "done";
            if (warnings != null && warnings.Count > 0) r["warnings"] = World.Strs(warnings.Count > 20 ? warnings.GetRange(0, 20) : warnings);
        }

        static List<string> Kinds(Json.Obj a)
        {
            var l = new List<string>();
            foreach (var s in PlayShared.Strings(a, "kinds")) l.Add(s.Trim().ToLowerInvariant());
            return l;
        }

        static bool NameMatch(string filter, params string[] names)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            foreach (var n in names) if (n != null && n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ---------------------------------------------------------------- triggers
        static string TriggerKind(GameObject go)
        {
            if (go.GetComponent<Savepoint>() != null) return "savepoint";
            if (go.GetComponent<Killer>() != null || go.GetComponent<DelayedKiller>() != null || go.GetComponent<WaterFlushKiller>() != null || go.GetComponent<HeadlessKillTrigger>() != null) return "killer";
            if (go.GetComponent<CameraBlendBox>() != null) return "camera";
            if (go.GetComponent<IsTriggeredByProbe>() != null) return "probe";
            if (go.GetComponent<PlayMakerFSM>() != null) return "fsm";
            foreach (var mb in go.GetComponents<MonoBehaviour>())
                if (mb != null && mb.GetType().Name.StartsWith("PlayMakerTrigger", StringComparison.Ordinal)) return "fsm";
            return "other";
        }

        static readonly Dictionary<int, int> pendingExits = new Dictionary<int, int>();
        static readonly Dictionary<int, Vector3> teleportFrom = new Dictionary<int, Vector3>();

        static int Pending(GameObject go)
        {
            lock (pendingExits) { int n; return pendingExits.TryGetValue(go.GetInstanceID(), out n) ? n : 0; }
        }

        static ToolResult TriggersNear(Json.Obj a, CallContext ctx)
        {
            var kinds = Kinds(a);
            float radius = (float)PlayShared.Num(a, "radius", 15);
            string filter = PlayShared.Str(a, "filter", null);
            int limit = PlayShared.Int(a, "limit", 40), offset = PlayShared.Int(a, "offset", 0);
            var r = ctx.Main(() =>
            {
                var o = Result(null);
                var center = World.Center(PlayShared.Str(a, "object", null));
                World.Ports ports = null;
                try { ports = World.ScanPorts(); } catch (McpException) { }
                var seen = new HashSet<int>();
                var rows = new List<KeyValuePair<float, Json.Obj>>();
                foreach (var col in Physics.OverlapSphere(center, radius))
                {
                    if (col == null || !col.isTrigger) continue;
                    var go = col.gameObject;
                    if (!seen.Add(go.GetInstanceID())) continue;
                    string kind = TriggerKind(go);
                    if (kinds.Count > 0 && !kinds.Contains(kind)) continue;
                    if (!NameMatch(filter, go.name)) continue;
                    var row = new Json.Obj();
                    row["id"] = World.Id(go, "o");
                    row["kind"] = kind;
                    row["name"] = go.name;
                    row["path"] = World.Path(go);
                    var p = col.ClosestPointOnBounds(center);
                    World.Dist(row, col.bounds.center, center);
                    float d = Vector3.Distance(p, center);
                    row["dist"] = PlayShared.R(d);
                    row["inside"] = col.bounds.Contains(center);
                    row["enabled"] = col.enabled;
                    var comps = new List<object>();
                    foreach (var mb in go.GetComponents<MonoBehaviour>()) if (mb != null && comps.Count < 6) comps.Add(mb.GetType().Name);
                    row["components"] = comps;
                    var tp = go.GetComponent<IsTriggeredByProbe>();
                    if (tp != null)
                    {
                        object n;
                        if (World.TryGet(tp, "noOfCharacterProbesInside", out n)) row["probes_inside"] = Convert.ToDouble(n);
                        row["filter"] = tp.useBoyProbe ? "boy" : tp.useHuddleProbe ? "huddle" : tp.anyNonTriggerProbe ? "any_solid" : tp.companionType != CompanionType.none ? "companion " + tp.companionType : tp.probe != null ? "probe " + tp.probe.name : "custom";
                    }
                    int pend = Pending(go);
                    if (pend > 0) row["pending_exits"] = (double)pend;
                    if (ports != null)
                    {
                        var outs = new List<object>();
                        foreach (var c in ports.From(go))
                        {
                            if (outs.Count >= 8) break;
                            var w = new Json.Obj();
                            w["output"] = c.signalOutName;
                            w["to"] = World.Path(c.signalInGameObject);
                            w["input"] = c.signalInName;
                            outs.Add(w);
                        }
                        if (outs.Count > 0) row["outputs"] = outs;
                    }
                    rows.Add(new KeyValuePair<float, Json.Obj>(d, row));
                }
                rows.Sort((x, y) => x.Key.CompareTo(y.Key));
                var list = new List<Json.Obj>();
                foreach (var kv in rows) list.Add(kv.Value);
                World.Page(o, list, offset, limit);
                return o;
            });
            return ToolResult.Ok(r);
        }

        static void SaveGuard(GameObject go, string what)
        {
            if (!Saves.Guard || (go.GetComponent<Savepoint>() == null && go.GetComponentInParent<Savepoint>() == null)) return;
            if (World.SaveLocked()) { World.SaveCheat(what + " savepoint " + go.name); return; }
            throw new McpException(McpErr.Refused, what + " a savepoint writes INSIDE.sav (Steam Cloud) and can unlock chapters", "turn the save lock on first (save {op:\"lock\"}), or let the body walk into it");
        }

        // main thread: refuse driving a secret / achievement object, on go or (downstream) on a receiver of its wires
        static void SecretGuard(GameObject go, bool downstream, string what)
        {
            string hit = ReflectValues.SecretOn(go);
            if (hit == null && downstream) hit = SecretDownstream(go, 3);
            if (hit != null) throw new McpException(McpErr.Refused, what + " reaches " + hit + ", which calls User.Achievement.Acquire (Steam) or clears the orb progress", ReflectValues.SecretHint);
        }

        static string SecretDownstream(GameObject go, int depth)
        {
            World.Ports ports;
            try { ports = World.ScanPorts(); } catch (McpException) { return null; }
            var seen = new HashSet<int> { go.GetInstanceID() };
            var front = new List<GameObject> { go };
            for (int d = 0; d < depth && front.Count > 0 && seen.Count < 300; d++)
            {
                var next = new List<GameObject>();
                foreach (var g in front)
                    foreach (var c in ports.From(g))
                    {
                        var to = c.signalInGameObject;
                        if (to == null || !seen.Add(to.GetInstanceID())) continue;
                        string hit = ReflectValues.SecretOn(to);
                        if (hit != null) return hit;
                        next.Add(to);
                    }
                front = next;
            }
            return null;
        }

        static ToolResult TriggerFire(Json.Obj a, CallContext ctx)
        {
            string spec = PlayShared.Str(a, "object", null);
            string edge = PlayShared.Str(a, "edge", "enter"), mode = PlayShared.Str(a, "mode", "probe");
            bool enter = edge == "enter";
            int k = PlayShared.Int(a, "verify_frames", 2);
            if (mode == "teleport") return TriggerTeleport(a, ctx, spec, enter);
            var warnings = new List<string>();
            long mark = World.Mark();
            var r = Result(warnings);
            ctx.MainDo(() =>
            {
                var go = World.Target(spec, "object");
                SecretGuard(go, true, "firing " + go.name);
                SaveGuard(go, "firing");
                r["inactive_target"] = !go.activeInHierarchy;
                if (!go.activeInHierarchy) warnings.Add("the trigger is inactive (culled or switched off): handlers still ran, but the game would not fire it now");
                if (mode == "probe") r["note"] = FireProbe(go, enter, warnings);
                else r["note"] = FireSignal(go, enter);
            });
            if (k > 0) World.WaitVerify(ctx, k, warnings);
            var ch = World.Since(mark);
            if (ch != null) r["changes"] = ch;
            Finish(r, warnings);
            return ToolResult.Ok(r);
        }

        static string FireProbe(GameObject go, bool enter, List<string> warnings)
        {
            int id = go.GetInstanceID();
            if (!enter && Pending(go) == 0)
                throw new McpException(McpErr.Refused, "no MCP enter is pending on " + go.name + ": a probe exit now would drive its probe counter negative", "fire edge enter first, or use mode signal for the exit outputs");
            var tp = go.GetComponent<IsTriggeredByProbe>();
            Collider probe = null;
            if (tp != null && tp.useHuddleProbe)
            {
                var hud = ScriptGlobals.huddle;
                var hp = hud != null ? World.Child<HuddleProbe>(hud) : null;
                if (hp == null && hud != null) hp = hud.transform.parent != null ? World.Child<HuddleProbe>(hud.transform.parent) : null;
                probe = hp != null ? hp.GetComponent<Collider>() : null;
                if (probe == null) throw new McpException(McpErr.NotReady, "the trigger wants the huddle probe and there is no huddle", "this trigger belongs to the huddle part of the game");
            }
            else
            {
                var boy = ScriptGlobals.boy;
                var bp = boy != null ? World.Child<BoyProbe>(boy) : null;
                probe = bp != null ? bp.GetComponent<Collider>() : null;
                if (probe == null) throw new McpException(McpErr.NotReady, "no boy probe collider", "wait until the game is ready");
                if (tp != null && !tp.useBoyProbe) warnings.Add("this IsTriggeredByProbe filters on " + (tp.anyNonTriggerProbe ? "any solid collider" : "a specific probe") + ": the boy probe may not pass it (try mode signal)");
            }
            string method = enter ? "OnTriggerEnter" : "OnTriggerExit";
            var called = new List<string>();
            foreach (var mb in go.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                var m = World.Method(mb.GetType(), method, new[] { typeof(Collider) });
                if (m == null) continue;
                try { m.Invoke(mb, new object[] { probe }); }
                catch (TargetInvocationException e) { throw new McpException(McpErr.GameError, mb.GetType().Name + "." + method + " threw " + ReflectValues.Inner(e).GetType().Name + ": " + ReflectValues.Inner(e).Message); }
                called.Add(mb.GetType().Name);
            }
            if (called.Count == 0) throw new McpException(McpErr.NotFound, go.name + " has no " + method + "(Collider) handler", "logic {op:\"triggers_near\"} shows the trigger components; mode signal fires outputs directly");
            lock (pendingExits)
            {
                int n;
                pendingExits.TryGetValue(id, out n);
                n = enter ? n + 1 : Math.Max(0, n - 1);
                if (n == 0) pendingExits.Remove(id); else pendingExits[id] = n;
            }
            if (enter) warnings.Add("probe enter fired: fire edge exit later to keep the trigger's probe counter paired");
            return method + "(" + probe.name + ") on " + string.Join(", ", called.ToArray());
        }

        static string FireSignal(GameObject go, bool enter)
        {
            var tp = go.GetComponent<IsTriggeredByProbe>();
            if (tp != null)
            {
                World.Call(tp, enter ? "sendEnterEvents" : "sendExitEvents");
                return "IsTriggeredByProbe." + (enter ? "sendEnterEvents (probeEnter + enterSignal)" : "sendExitEvents (probeExit + exitSignal)");
            }
            var fired = new List<string>();
            string want = enter ? "enter" : "exit";
            foreach (var s in World.ScanPorts().Out(go))
                if (s.debugName != null && s.debugName.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) { s.Signal(); fired.Add(s.debugName); }
            if (fired.Count == 0) throw new McpException(McpErr.NotFound, go.name + " has no " + want + " output", "logic {op:\"signal_ports\"} lists its outputs; signal_out fires one by name");
            return "signalled " + string.Join(", ", fired.ToArray());
        }

        static ToolResult TriggerTeleport(Json.Obj a, CallContext ctx, string spec, bool enter)
        {
            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return busy;
            var warnings = new List<string>();
            long mark = World.Mark();
            Vector3 dest = Vector3.zero;
            bool keepZ = true;
            int key = 0;
            ctx.MainDo(() =>
            {
                var go = World.Target(spec, "object");
                if (enter) SecretGuard(go, true, "teleporting into " + go.name);
                SaveGuard(go, "teleporting into");
                key = go.GetInstanceID();
                if (enter)
                {
                    var col = go.GetComponent<Collider>();
                    if (col == null) throw new McpException(McpErr.NotFound, go.name + " has no collider to teleport into");
                    dest = col.bounds.center;
                    var b = col.bounds;
                    Vector3 body;
                    if (World.BodyPos(out body) && (body.z < b.min.z || body.z > b.max.z)) { keepZ = false; warnings.Add("the trigger is off the body's depth plane: the body moves to z " + PlayShared.F(dest.z)); }
                    if (World.BodyPos(out body)) lock (teleportFrom) teleportFrom[key] = body;
                }
                else
                {
                    Vector3 back;
                    bool has;
                    lock (teleportFrom) has = teleportFrom.TryGetValue(key, out back);
                    if (!has) throw new McpException(McpErr.Refused, "no teleport enter to undo on " + go.name, "fire edge enter with mode teleport first, or use body {op:\"teleport\"}");
                    dest = back;
                    keepZ = false;
                    lock (teleportFrom) teleportFrom.Remove(key);
                }
            });
            var r = Result(warnings);
            var tr = Teleport(ctx, null, from => keepZ ? new Vector3(dest.x, dest.y, from.z) : dest, true, true, false, warnings);
            r["verdict"] = tr.Str("verdict", "unreadable");
            r["before"] = tr["before"];
            r["after"] = tr["after"];
            r["note"] = enter ? "teleported into the trigger (the physics engine fires its enter)" : "teleported back out (the physics engine fires its exit)";
            if (tr.Str("outcome", "done") != "done") r["outcome"] = tr.Str("outcome", "refused");
            var ch = World.Since(mark);
            if (ch != null) r["changes"] = ch;
            Finish(r, warnings);
            return ToolResult.Ok(r);
        }

        // ---------------------------------------------------------------- signals
        static ToolResult SignalPorts(Json.Obj a, CallContext ctx)
        {
            int limit = PlayShared.Int(a, "limit", 40), offset = PlayShared.Int(a, "offset", 0);
            var r = ctx.Main(() =>
            {
                var o = Result(null);
                var go = World.Target(PlayShared.Str(a, "object", null), "object");
                var ports = World.ScanPorts();
                var rows = new List<Json.Obj>();
                foreach (var s in ports.In(go))
                {
                    var row = new Json.Obj();
                    row["dir"] = "in";
                    row["name"] = s.debugName;
                    var from = new List<object>();
                    foreach (var c in ports.To(go)) if (c.signalInName == s.debugName && from.Count < 8) from.Add(World.Path(c.signalOutGameObject) + "." + c.signalOutName);
                    if (from.Count > 0) row["from"] = from;
                    rows.Add(row);
                }
                foreach (var s in ports.Out(go))
                {
                    var row = new Json.Obj();
                    row["dir"] = "out";
                    row["name"] = s.debugName;
                    var to = new List<object>();
                    foreach (var c in ports.From(go)) if (c.signalOutName == s.debugName && to.Count < 8) to.Add(World.Path(c.signalInGameObject) + "." + c.signalInName + (c.isConnected ? "" : " (waiting)"));
                    row["to"] = to;
                    rows.Add(row);
                }
                foreach (var c in ports.To(go))
                    if (c.fsmIn != null)
                    {
                        var row = new Json.Obj();
                        row["dir"] = "fsm_in";
                        row["name"] = c.signalInName;
                        row["from"] = new List<object> { World.Path(c.signalOutGameObject) + "." + c.signalOutName };
                        rows.Add(row);
                    }
                foreach (var e in World.FsmEvents(go))
                {
                    if (!e.StartsWith(SignalIn.prefixFsmSignal, StringComparison.Ordinal)) continue;
                    var row = new Json.Obj();
                    row["dir"] = "fsm_event";
                    row["name"] = e;
                    rows.Add(row);
                }
                World.Page(o, rows, offset, limit);
                o["inactive_target"] = !go.activeInHierarchy;
                o["note"] = World.Path(go) + ": " + ports.In(go).Count + " inputs, " + ports.Out(go).Count + " outputs";
                return o;
            });
            return ToolResult.Ok(r);
        }

        static Json.Obj LocalState(GameObject go)
        {
            var o = new Json.Obj();
            if (go == null) { o["destroyed"] = true; return o; }
            o["active"] = go.activeInHierarchy;
            foreach (var f in go.GetComponents<PlayMakerFSM>())
                if (f != null && f.Fsm != null && o.Count < 10) o["fsm:" + f.FsmName] = f.Fsm.ActiveStateName ?? "";
            foreach (var mb in go.GetComponents<MonoBehaviour>())
            {
                if (mb == null || mb is PlayMakerFSM || o.Count >= 12) continue;
                var s = World.ShortState(mb);
                if (s.Length > 0) o[mb.GetType().Name] = s;
            }
            return o;
        }

        static ToolResult SignalFire(Json.Obj a, CallContext ctx, bool input)
        {
            string name = PlayShared.Str(a, input ? "input" : "output", null);
            if (string.IsNullOrEmpty(name)) return ToolResult.Fail(McpErr.BadArgs, "op " + (input ? "signal_in needs input" : "signal_out needs output"), "logic {op:\"signal_ports\"} lists them");
            int k = PlayShared.Int(a, "verify_frames", 2);
            var warnings = new List<string>();
            long mark = World.Mark();
            var r = Result(warnings);
            GameObject go = null;
            ctx.MainDo(() =>
            {
                go = World.Target(PlayShared.Str(a, "object", null), "object");
                r["before"] = LocalState(go);
                bool inactive = !go.activeInHierarchy;
                r["inactive_target"] = inactive;
                SecretGuard(go, !input, (input ? "signal_in " : "signal_out ") + name + " on " + go.name);
                if (input)
                {
                    if (name == "doSave") SaveGuard(go, "signalling doSave on");
                    var s = World.FindIn(go, name);
                    if (s != null)
                    {
                        if (s.action == null) throw new McpException(McpErr.GameError, name + " has no action (cleared port)");
                        try { s.action(); }
                        catch (Exception e) { throw new McpException(McpErr.GameError, name + " threw " + e.GetType().Name + ": " + e.Message); }
                        r["note"] = "SignalIn " + name + " ran" + (inactive ? " (on an inactive object: signal inputs still run)" : "");
                    }
                    else
                    {
                        var f = SignalManager.GetFsmSignalInput(go, name);
                        if (f == null)
                        {
                            var ins = World.PortNames(World.ScanPorts().In(go));
                            var e = new McpException(McpErr.NotFound, World.Path(go) + " has no input '" + name + "'", "inputs: " + string.Join(", ", ToStr(ins)));
                            e.data = new Json.Obj();
                            e.data["inputs"] = ins;
                            throw e;
                        }
                        if (f.Fsm == null || !f.Fsm.Active)
                        {
                            r["inactive_target"] = true;
                            r["note"] = "FSM " + f.FsmName + " is inactive: the event " + name + " would be lost, so it was not sent";
                            r["outcome"] = "refused";
                            return;
                        }
                        f.Fsm.Event(name);
                        r["note"] = "FSM event " + name + " sent to " + f.FsmName;
                    }
                }
                else
                {
                    var s = World.FindOut(go, name);
                    if (s == null)
                    {
                        var outs = World.PortNames(World.ScanPorts().Out(go));
                        var e = new McpException(McpErr.NotFound, World.Path(go) + " has no output '" + name + "'", "outputs: " + string.Join(", ", ToStr(outs)));
                        e.data = new Json.Obj();
                        e.data["outputs"] = outs;
                        throw e;
                    }
                    try { s.Signal(); }
                    catch (Exception e) { throw new McpException(McpErr.GameError, name + " threw " + e.GetType().Name + ": " + e.Message); }
                    r["note"] = "SignalOut " + name + " raised";
                }
                r["after"] = LocalState(go);
            });
            if (k > 0 && World.WaitVerify(ctx, k, warnings)) ctx.MainDo(() => r["after_k"] = LocalState(go));
            var ch = World.Since(mark);
            if (ch != null) r["changes"] = ch;
            warnings.Add("signals cannot be undone by restore; reload the checkpoint to reset the scene");
            Finish(r, warnings);
            return ToolResult.Ok(r);
        }

        static string[] ToStr(List<object> l)
        {
            var r = new string[l.Count];
            for (int i = 0; i < l.Count; i++) r[i] = Convert.ToString(l[i]);
            return r;
        }

        static FieldInfo outConns;

        // SignalEdit.Disconnect's recipe: drop the wire from its SignalOut, then free the connection slot
        internal static void Disconnect(SignalConnection c)
        {
            if (c == null || !c.isActive) return;
            if (c.signalOut != null)
            {
                if (outConns == null) outConns = typeof(SignalOut).GetField("connections", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var l = outConns != null ? outConns.GetValue(c.signalOut) as FastList<SignalConnection> : null;
                if (l != null) l.Remove(c);
            }
            World.Signals().HandleSignalConnectionDestroyed(c);
        }

        static SignalConnection FindWire(GameObject from, string output, GameObject to, string input)
        {
            foreach (var c in World.ScanPorts().From(from))
                if (c.signalOutName == output && c.signalInGameObject == to && c.signalInName == input) return c;
            return null;
        }

        static ToolResult SignalWire(Json.Obj a, CallContext ctx, bool wire)
        {
            string output = PlayShared.Str(a, "output", null), input = PlayShared.Str(a, "input", null);
            if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(input) || !PlayShared.Has(a, "to") || !PlayShared.Has(a, "object"))
                return ToolResult.Fail(McpErr.BadArgs, "op signal_" + (wire ? "wire" : "unwire") + " needs object (sender), output, to (receiver) and input");
            var warnings = new List<string>();
            var r = Result(warnings);
            ctx.MainDo(() =>
            {
                var from = World.Target(PlayShared.Str(a, "object", null), "object");
                var to = World.Target(PlayShared.Str(a, "to", null), "to");
                if (wire) SecretGuard(to, false, "wiring into " + to.name);
                if (input == "doSave" && wire) SaveGuard(to, "wiring into");
                string label = World.Path(from) + "." + output + " -> " + World.Path(to) + "." + input;
                string key = "logic:" + (wire ? "wire:" : "unwire:") + from.GetInstanceID() + "." + output + ">" + to.GetInstanceID() + "." + input;
                var existing = FindWire(from, output, to, input);
                r["before"] = existing != null ? "wired" : "not wired";
                if (wire)
                {
                    if (existing != null) { r["note"] = "already wired: " + label; r["after"] = "wired"; r["verdict"] = "confirmed"; return; }
                    var c = World.Signals().CreateConnection(from, output, to, input, null);
                    if (c == null) throw new McpException(McpErr.GameError, "the game refused the connection");
                    var cc = c;
                    World.Restore(key, "signal wire " + label, () =>
                    {
                        if (cc.isActive && cc.signalOutGameObject == from && cc.signalInGameObject == to && cc.signalInName == input) { Disconnect(cc); return "unwired"; }
                        return "already gone";
                    });
                    r["note"] = (c.isConnected ? "wired " : "wired, waiting: the sender has not created '" + output + "' yet (it links when it wakes up) ") + label;
                    if (!c.isConnected) warnings.Add("not connected yet: the output does not exist on the sender (check signal_ports)");
                    if (c.signalIn == null && c.fsmIn == null) warnings.Add("the receiver has no input '" + input + "' (no SignalIn and no FSM event of that name): the wire will do nothing");
                }
                else
                {
                    if (existing == null) throw new McpException(McpErr.NotFound, "no wire " + label, "logic {op:\"signal_ports\"} shows the wires");
                    PlayMakerFSM fsm = null;
                    if (existing.fsmIn != null) fsm = existing.fsmIn.Owner as PlayMakerFSM;
                    Disconnect(existing);
                    World.Restore(key, "signal unwire " + label, () =>
                    {
                        if (from == null || to == null) return "endpoints gone";
                        if (FindWire(from, output, to, input) != null) return "already wired again";
                        World.Signals().CreateConnection(from, output, to, input, fsm);
                        return "wired again";
                    });
                    r["note"] = "unwired " + label;
                }
                var now = FindWire(from, output, to, input);
                r["after"] = now != null ? "wired" : "not wired";
                r["verdict"] = ReflectValues.Verdict(true, (now != null) == wire, false, false, false);
            });
            Finish(r, warnings);
            return ToolResult.Ok(r);
        }

        // ---------------------------------------------------------------- FSMs
        static ToolResult FsmList(Json.Obj a, CallContext ctx)
        {
            string filter = PlayShared.Str(a, "filter", null), state = PlayShared.Str(a, "state", null), spec = PlayShared.Str(a, "object", null);
            float radius = (float)PlayShared.Num(a, "radius", 15);
            int limit = PlayShared.Int(a, "limit", 40), offset = PlayShared.Int(a, "offset", 0);
            var r = ctx.Main(() =>
            {
                var o = Result(null);
                IEnumerable<PlayMakerFSM> src;
                Vector3 center;
                bool near = string.IsNullOrEmpty(spec);
                if (!near)
                {
                    var go = World.Target(spec, "object");
                    center = go.transform.position;
                    src = go.GetComponentsInChildren<PlayMakerFSM>(true);
                }
                else
                {
                    center = World.Center(null);
                    src = PlayMakerFSM.FsmList;
                }
                var rows = new List<KeyValuePair<float, Json.Obj>>();
                float r2 = radius * radius;
                foreach (var f in src)
                {
                    if (f == null) continue;
                    var p = f.transform.position;
                    if (near && (p - center).sqrMagnitude > r2) continue;
                    var fsm = f.Fsm;
                    string st = fsm != null ? fsm.ActiveStateName ?? "" : "";
                    if (!string.IsNullOrEmpty(state) && !string.Equals(st, state, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!NameMatch(filter, f.gameObject.name, f.FsmName)) continue;
                    var row = new Json.Obj();
                    row["id"] = World.Id(f.gameObject, "o");
                    row["path"] = World.Path(f.gameObject);
                    row["fsm"] = f.FsmName;
                    row["state"] = st;
                    row["active"] = fsm != null && fsm.Active;
                    row["enabled"] = f.enabled;
                    row["states"] = fsm != null && fsm.States != null ? (double)fsm.States.Length : 0.0;
                    float d = Vector3.Distance(p, center);
                    row["dist"] = PlayShared.R(d);
                    rows.Add(new KeyValuePair<float, Json.Obj>(d, row));
                }
                rows.Sort((x, y) => x.Key.CompareTo(y.Key));
                var list = new List<Json.Obj>();
                foreach (var kv in rows) list.Add(kv.Value);
                World.Page(o, list, offset, limit);
                o["note"] = near ? "enabled FSMs within " + PlayShared.F(radius) + " m (PlayMakerFSM.FsmList); pass object to include inactive ones under it" : "FSMs under " + spec + " (inactive included)";
                return o;
            });
            return ToolResult.Ok(r);
        }

        // ---------------------------------------------------------------- interactables
        static readonly KeyValuePair<Type, string>[] InteractTypes =
        {
            new KeyValuePair<Type, string>(typeof(Lever), "lever"), new KeyValuePair<Type, string>(typeof(LeverAnalog), "lever"), new KeyValuePair<Type, string>(typeof(LeverAnalogWater), "lever"),
            new KeyValuePair<Type, string>(typeof(SwitchButtonNew), "button"), new KeyValuePair<Type, string>(typeof(SwitchValve), "valve"), new KeyValuePair<Type, string>(typeof(SwitchValveSimple), "valve"),
            new KeyValuePair<Type, string>(typeof(Gate), "gate"), new KeyValuePair<Type, string>(typeof(Door), "door"), new KeyValuePair<Type, string>(typeof(Elevator), "elevator"),
            new KeyValuePair<Type, string>(typeof(ElevatorMines), "elevator"), new KeyValuePair<Type, string>(typeof(ElevatorFall), "elevator"), new KeyValuePair<Type, string>(typeof(Hatch), "hatch"),
            new KeyValuePair<Type, string>(typeof(JetCrate), "jetcrate"), new KeyValuePair<Type, string>(typeof(PersistentBool), "flag"),
        };

        static ToolResult InteractablesNear(Json.Obj a, CallContext ctx)
        {
            var kinds = Kinds(a);
            float radius = (float)PlayShared.Num(a, "radius", 15);
            string filter = PlayShared.Str(a, "filter", null), spec = PlayShared.Str(a, "object", null);
            int limit = PlayShared.Int(a, "limit", 40), offset = PlayShared.Int(a, "offset", 0);
            Vector3 center = Vector3.zero;
            World.Ports ports = null;
            ctx.MainDo(() => { center = World.Center(spec); ports = World.ScanPorts(); });
            var rows = new List<KeyValuePair<float, Json.Obj>>();
            float r2 = radius * radius;
            foreach (var kv0 in InteractTypes)
            {
                var kv = kv0;
                if (kinds.Count > 0 && !kinds.Contains(kv.Value)) continue;
                ctx.MainDo(() =>
                {
                    var all = UnityEngine.Object.FindObjectsOfType(kv.Key) as MonoBehaviour[];
                    if (all == null) return;
                    foreach (var c in all)
                    {
                        if (c == null) continue;
                        var p = c.transform.position;
                        if ((p - center).sqrMagnitude > r2) continue;
                        if (!NameMatch(filter, c.gameObject.name, kv.Key.Name)) continue;
                        var row = new Json.Obj();
                        row["id"] = World.Id(c.gameObject, "o");
                        row["kind"] = kv.Value;
                        row["class"] = kv.Key.Name;
                        row["name"] = c.gameObject.name;
                        row["path"] = World.Path(c.gameObject);
                        World.Dist(row, p, center);
                        var pb = c as PersistentBool;
                        if (pb != null)
                        {
                            row["state"] = "value=" + (pb.current ? "true" : "false");
                            row["flag"] = pb.booleanName;
                            row["unique_id"] = pb.uniqueID;
                        }
                        else row["state"] = World.ShortState(c);
                        row["inputs"] = World.PortNames(ports.In(c.gameObject));
                        row["outputs"] = World.PortNames(ports.Out(c.gameObject));
                        row["wires_out"] = (double)ports.From(c.gameObject).Count;
                        rows.Add(new KeyValuePair<float, Json.Obj>((p - center).magnitude, row));
                    }
                });
            }
            rows.Sort((x, y) => x.Key.CompareTo(y.Key));
            var list = new List<Json.Obj>();
            foreach (var kv in rows) list.Add(kv.Value);
            var r = Result(null);
            World.Page(r, list, offset, limit);
            r["note"] = "drive one with signal_in (inputs: loadX set the state without animation, activate / open / doMove act); world flags (kind flag): signal_in SetTrue / SetFalse (saved at the next savepoint)";
            return ToolResult.Ok(r);
        }
    }
}
