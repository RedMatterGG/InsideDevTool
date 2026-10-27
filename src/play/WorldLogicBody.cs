using System;
using System.Collections.Generic;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // WorldLogicBody.cs (owner WORLD_B): the body tool (report 02 sections 3.4, 4). list = Perception.Bodies plus the
    // state class; teleport, hat and swap take the control lane. Safe teleport (report 02 section 3.4): refuse while
    // loading (always), and unless force while dead, mind-controlling (in a hat), in a special mode (sub, cliff),
    // in BoyCustomAnimState, or not active; keep the depth plane (Boy.plane.posz, Huddle.playfield.posz) unless z is
    // given; Character.Teleport(pos) (Boy.Teleport clears the state machine, FP/Boy.cs:501-516; Huddle.Teleport shifts
    // cloth and spine but keeps its state, FP/Huddle.cs:354-379); Submarine.Teleport(pos, vel) (AC/Submarine.cs:464);
    // CameraScript.FastBlend (FP/CameraScript.cs:199-221); jumps over 25 m: freeze if running, RefreshVisibilityState +
    // RefreshAllCullers, wait AllCullersFinished up to 10 s (FP/SubsceneCullingManager.cs:259-274), unfreeze. Only with
    // EditorState play.save_guard on (Saves.Guard): the save is backed up (Saves.EnsureBackup) before any teleport and a
    // destination inside a savepoint is refused unless the save lock is on (then it marks the lock, Saves.MarkCheat) or
    // force (needs the backup); with the guard off every destination is allowed (SavepointEntry). hat: MindHat.ForceAttach /
    // ForceDetach / GetBoyInHat / IsMindHatTurnedOff (AC/MindHat.cs:959-1041). swap: MainCharacterSwap.SwapToBoy /
    // SwapToHuddle (FP/MainCharacterSwap.cs:13-29). The Boy is re-created on every respawn: nothing here is cached.
    public static partial class WorldLogic
    {
        const float LongJump = 25f;

        static ToolResult HandleBody(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "list");
            switch (op)
            {
                case "list": return BodyList(ctx);
                case "teleport": return BodyTeleport(a, ctx);
                case "hat": return Hat(a, ctx);
                case "swap": return Swap(a, ctx);
            }
            return ToolResult.Fail(McpErr.BadArgs, "unknown op " + op);
        }

        internal static string StateName(object state)
        {
            if (state == null) return "";
            var n = state.GetType().Name;
            if (n.StartsWith("Boy", StringComparison.Ordinal) && n.Length > 3) n = n.Substring(3);
            else if (n.StartsWith("Huddle", StringComparison.Ordinal) && n.Length > 6) n = n.Substring(6);
            if (n.EndsWith("State", StringComparison.Ordinal) && n.Length > 5) n = n.Substring(0, n.Length - 5);
            return n;
        }

        static string BodyState(GameObject go)
        {
            if (go == null) return "";
            var boy = go.GetComponent<Boy>();
            if (boy != null) return (boy.isDead ? "dead " : "") + StateName(boy.state) + (boy.specialBoyMode != Boy.SpecialBoyMode.None ? " " + boy.specialBoyMode : "") + (boy.isMindControlling ? " MindControlling" : "") + (boy.isMindControlled ? " MindControlled" : "");
            var hud = go.GetComponent<Huddle>();
            if (hud != null) return StateName(hud.state);
            var sub = go.GetComponent<Submarine>();
            if (sub != null)
            {
                var b = ScriptGlobals.boy;
                return b != null && b.specialBoyMode == Boy.SpecialBoyMode.InsideSub ? "occupied" : "empty";
            }
            return "";
        }

        static ToolResult BodyList(CallContext ctx)
        {
            var r = ctx.Main(() =>
            {
                var o = Result(null);
                var rows = new List<object>();
                foreach (var b in Perception.Bodies())
                {
                    if (rows.Count >= 16) break;
                    var row = new Json.Obj();
                    row["id"] = b.id;
                    row["kind"] = b.kind;
                    row["name"] = b.name;
                    row["pos"] = PlayShared.V(b.pos);
                    row["controlled"] = b.controlled;
                    row["active"] = b.active;
                    row["state"] = BodyState(b.go);
                    rows.Add(row);
                }
                o["bodies"] = rows;
                return o;
            });
            return ToolResult.Ok(r);
        }

        // ---------------------------------------------------------------- teleport
        sealed class TpBody
        {
            public Character ch;
            public Submarine sub;
            public string name;
            public Vector3 Pos { get { return ch != null ? ch.pos3 : sub != null ? sub.pos3 : Vector3.zero; } }
            public bool Alive { get { return ch != null || sub != null; } }
        }

        static TpBody ResolveBody(string spec, bool force, List<string> warnings)
        {
            var b = new TpBody();
            if (string.IsNullOrEmpty(spec))
            {
                b.ch = World.Active();
                if (b.ch == null) throw new McpException(McpErr.NotReady, "no controlled body", "wait until the game is ready");
            }
            else
            {
                var go = World.Target(spec, "body");
                b.ch = go.GetComponent<Character>();
                if (b.ch == null) b.sub = go.GetComponent<Submarine>();
                if (!b.Alive) throw new McpException(McpErr.NotFound, World.Path(go) + " is not a body (boy, huddle, dude or submarine)", "body {op:\"list\"} shows the bodies");
            }
            b.name = b.ch != null ? b.ch.name : b.sub.name;
            if (b.ch != null && !b.ch.isCharacterActive) throw new McpException(McpErr.Refused, b.name + " is not active", "activate the body first (swap)");
            var boy = b.ch as Boy;
            var why = new List<string>();
            if (b.ch != null && b.ch.isDead) why.Add("dead (wait_until {alive:true} or checkpoint reload)");
            if (boy != null)
            {
                if (boy.isMindControlling) why.Add("wearing a mind-control hat (body {op:\"hat\", hat_op:\"detach\"})");
                if (boy.specialBoyMode != Boy.SpecialBoyMode.None) why.Add("in special mode " + boy.specialBoyMode + (boy.specialBoyMode == Boy.SpecialBoyMode.InsideSub ? " (teleport the submarine instead)" : ""));
                if (boy.state is BoyCustomAnimState) why.Add("in a scripted animation (BoyCustomAnimState)");
            }
            if (why.Count > 0)
            {
                if (!force) throw new McpException(McpErr.Refused, b.name + " cannot be teleported now: " + string.Join("; ", why.ToArray()), "force:true skips this check (may break scripted scenes)");
                warnings.Add("forced past: " + string.Join("; ", why.ToArray()));
            }
            return b;
        }

        static float PlaneZ(TpBody b)
        {
            var boy = b.ch as Boy;
            if (boy != null && boy.plane != null) return boy.plane.posz;
            var hud = b.ch as Huddle;
            if (hud != null && hud.playfield != null) return hud.playfield.posz;
            return b.Pos.z;
        }

        // a teleport destination inside a savepoint: go (play.save_guard off: allowed), mark (save lock on: Saves.MarkCheat),
        // refused (no lock, no force), no_backup (forced but the save backup failed), forced (forced with the backup)
        public static string SavepointEntry(bool guard, bool locked, bool force, bool backedUp)
        {
            if (!guard) return "go";
            if (locked) return "mark";
            if (!force) return "refused";
            return backedUp ? "forced" : "no_backup";
        }

        // worker thread; the caller holds the control lane. target gets (x, y, depth plane z) and returns the destination.
        internal static Json.Obj Teleport(CallContext ctx, string bodySpec, Func<Vector3, Vector3> target, bool snap, bool waitStream, bool force, List<string> warnings)
        {
            TpBody body = null;
            Vector3 from = Vector3.zero, to = Vector3.zero, after = Vector3.zero;
            bool far = false, unfreeze = false;
            string backupProblem = null;
            bool guard = Saves.Guard;
            if (guard)
            {
                try { Saves.EnsureBackup(ctx); }
                catch (McpException e) { backupProblem = e.Message; }
                catch (NotImplementedException) { backupProblem = "the save module is not built"; }
            }
            ctx.MainDo(() =>
            {
                if (GameManager.IsLoading()) throw new McpException(McpErr.Refused, "a load is in progress", "wait_until {ready:true} first");
                body = ResolveBody(bodySpec, force, warnings);
                from = body.Pos;
                to = target(new Vector3(from.x, from.y, PlaneZ(body)));
                if (float.IsNaN(to.x) || float.IsNaN(to.y) || float.IsNaN(to.z)) throw new McpException(McpErr.BadArgs, "destination is not a number");
                Savepoint hit = null;
                foreach (var c in Physics.OverlapSphere(to, 0.6f))
                {
                    if (c == null || !c.isTrigger) continue;
                    hit = c.GetComponent<Savepoint>();
                    if (hit == null) hit = c.GetComponentInParent<Savepoint>();
                    if (hit != null) break;
                }
                if (hit != null)
                {
                    string where = "the destination is inside savepoint " + World.Path(hit) + ": entering it writes INSIDE.sav (Steam Cloud) and can unlock chapters";
                    switch (SavepointEntry(guard, World.SaveLocked(), force, backupProblem == null))
                    {
                        case "refused": throw new McpException(McpErr.Refused, where, "turn the save lock on first (save {op:\"lock\"}), pick a spot outside it, or force:true");
                        case "no_backup": throw new McpException(McpErr.GameError, where + "; and no save backup could be made (" + backupProblem + ")", "copy INSIDE.sav by hand first, or turn the save lock on");
                        case "mark": World.SaveCheat("teleport into savepoint " + hit.name); break;
                        case "forced": warnings.Add("forced: " + where + " (backup " + (Saves.BackupPath ?? "none") + ")"); break;
                    }
                }
                else if (backupProblem != null) warnings.Add("no_backup: " + backupProblem);
                float dist = Vector3.Distance(from, to);
                far = dist > LongJump;
                if (dist > 60f) warnings.Add("far jump (" + PlayShared.F(dist) + " m): if that area is not streamed in the body falls; a checkpoint load is safer");
                if (body.ch != null) body.ch.Teleport(to);
                else body.sub.Teleport(to, Vector3.zero);
                if (snap)
                {
                    var cs = CameraScript.main;
                    if (cs != null) cs.FastBlend();
                    if (WorldCamera.Overridden) warnings.Add("the camera is overridden: it does not follow the teleport until camera release");
                }
                if (far && waitStream)
                {
                    if (!World.Frozen())
                    {
                        try { Stepper.Freeze(); unfreeze = true; }
                        catch (NotImplementedException) { warnings.Add("freeze unavailable (time module not built): streaming catches up while the game runs"); }
                    }
                    var cm = SubsceneCullingManager.instance;
                    cm.RefreshVisibilityState();
                    cm.RefreshAllCullers();
                }
                after = body.Pos;
            });
            var r = new Json.Obj();
            r["outcome"] = "done";
            try
            {
                if (far && waitStream)
                {
                    bool settled = ctx.WaitUntil(() => SubsceneCullingManager.instance.AllCullersFinished(), Math.Min(10000, Math.Max(1000, ctx.RemainingMs - 4000)), 2);
                    if (!settled) { warnings.Add("streaming did not settle within the wait: the area may still be loading"); r["outcome"] = "timeout"; }
                }
            }
            finally
            {
                if (unfreeze) { try { ctx.MainDo(() => Stepper.Unfreeze()); } catch (Exception e) { warnings.Add("unfreeze: " + e.Message); } }
            }
            bool sameNow = (after - to).sqrMagnitude < 0.01f;
            bool later = World.WaitVerify(ctx, 2, warnings);
            bool readK = false, sameK = false;
            Vector3 afterK = after;
            if (later)
                ctx.MainDo(() =>
                {
                    if (body == null || !body.Alive) return;
                    try { afterK = body.Pos; readK = true; sameK = (afterK - to).sqrMagnitude < 1f; }
                    catch (Exception) { readK = false; }
                });
            r["verdict"] = ReflectValues.Verdict(true, sameNow, true, readK, sameK);
            r["before"] = PlayShared.V(from);
            r["after"] = PlayShared.V(readK ? afterK : after);
            if (readK && !sameK) warnings.Add("the body moved away from the destination within 2 frames (fell, was pushed, or a scripted scene moved it)");
            return r;
        }

        static ToolResult BodyTeleport(Json.Obj a, CallContext ctx)
        {
            bool hasX = PlayShared.Has(a, "x"), hasY = PlayShared.Has(a, "y"), hasZ = PlayShared.Has(a, "z"), hasDx = PlayShared.Has(a, "dx"), hasDy = PlayShared.Has(a, "dy");
            string toObj = PlayShared.Str(a, "to_object", null);
            bool hasObj = !string.IsNullOrEmpty(toObj);
            if (!hasX && !hasY && !hasDx && !hasDy && !hasObj && !hasZ) return ToolResult.Fail(McpErr.BadArgs, "op teleport needs x / y, dx / dy or to_object");
            if (hasObj && (hasX || hasY || hasDx || hasDy)) return ToolResult.Fail(McpErr.BadArgs, "to_object cannot be combined with x / y / dx / dy");
            float x = (float)PlayShared.Num(a, "x", 0), y = (float)PlayShared.Num(a, "y", 0), z = (float)PlayShared.Num(a, "z", 0);
            float dx = (float)PlayShared.Num(a, "dx", 0), dy = (float)PlayShared.Num(a, "dy", 0);
            bool force = PlayShared.Bool(a, "force", false);
            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return busy;
            var warnings = new List<string>();
            Vector3 objPos = Vector3.zero;
            if (hasObj) ctx.MainDo(() => objPos = World.Target(toObj, "to_object").transform.position);
            var tr = Teleport(ctx, PlayShared.Str(a, "body", null), plane =>
            {
                var p = plane;
                if (hasObj) { p.x = objPos.x; p.y = objPos.y; }
                if (hasX) p.x = x;
                if (hasY) p.y = y;
                p.x += dx;
                p.y += dy;
                if (hasZ) p.z = z;
                return p;
            }, PlayShared.Bool(a, "snap_camera", true), PlayShared.Bool(a, "wait_stream", true), force, warnings);
            var r = PlayShared.Env();
            foreach (var key in tr.order) r[key] = tr[key];
            var obs = ctx.Main(() => World.Concise("body"));
            if (obs != null) r["observe"] = obs;
            if (warnings.Count > 0) r["warnings"] = World.Strs(warnings.Count > 20 ? warnings.GetRange(0, 20) : warnings);
            return ToolResult.Ok(r);
        }

        // ---------------------------------------------------------------- hats
        static Json.Obj HatRow(MindHat h)
        {
            var o = new Json.Obj();
            o["id"] = World.Id(h.gameObject, "o");
            o["name"] = h.gameObject.name;
            var w = h.GetBoyInHat();
            o["attached"] = w != null;
            o["wearer"] = w != null ? World.Path(w.gameObject) : "";
            o["turned_off"] = h.IsMindHatTurnedOff();
            return o;
        }

        static ToolResult Hat(Json.Obj a, CallContext ctx)
        {
            string hop = PlayShared.Str(a, "hat_op", "status");
            string spec = PlayShared.Str(a, "hat", null);
            bool force = PlayShared.Bool(a, "force", false);
            var warnings = new List<string>();
            var r = PlayShared.Env();
            if (hop == "status")
            {
                ctx.MainDo(() => r["hats"] = HatList());
                r["outcome"] = "done";
                return ToolResult.Ok(r);
            }
            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return busy;
            MindHat hat = null;
            bool want = hop == "attach", sameNow = false;
            ctx.MainDo(() =>
            {
                if (GameManager.IsLoading()) throw new McpException(McpErr.Refused, "a load is in progress", "wait_until {ready:true} first");
                hat = PickHat(spec, want);
                var boy = ScriptGlobals.boy;
                if (want)
                {
                    if (boy == null) throw new McpException(McpErr.NotReady, "no boy");
                    if (!force && (boy.isDead || boy.specialBoyMode != Boy.SpecialBoyMode.None || boy.isMindControlling))
                        throw new McpException(McpErr.Refused, "the boy is dead, in a special mode or already in a hat", "force:true skips this check");
                    if (hat.GetBoyInHat() != null) { warnings.Add("the hat is already worn"); sameNow = true; return; }
                    if (Vector3.Distance(boy.pos3, hat.transform.position) > 6f) warnings.Add("the hat is " + PlayShared.F(Vector3.Distance(boy.pos3, hat.transform.position)) + " m away: ForceAttach pulls the boy into it");
                    hat.ForceAttach();
                    sameNow = hat.GetBoyInHat() != null;
                }
                else
                {
                    if (hat.GetBoyInHat() == null) { warnings.Add("the hat is not worn"); sameNow = true; return; }
                    hat.ForceDetach(false);
                    sameNow = true;
                    warnings.Add("detach runs the hat's drop sequence; the boy is free once it ends (a few steps of game time)");
                }
            });
            bool later = World.WaitVerify(ctx, 2, warnings);
            bool readK = false, sameK = false, frozen = false;
            ctx.MainDo(() =>
            {
                frozen = World.Frozen();
                if (later && hat != null) { readK = true; sameK = (hat.GetBoyInHat() != null) == want; }
                r["hats"] = HatList();
            });
            bool checkLater = want || !frozen;
            if (!checkLater) warnings.Add("frozen: the detach finishes once game time runs (time step or act), so it is not re-checked");
            r["outcome"] = "done";
            r["verdict"] = ReflectValues.Verdict(hat != null, sameNow, checkLater, readK, sameK);
            warnings.Add("hats have no undo: detach (or reload the checkpoint) to get the boy back");
            var obs = ctx.Main(() => World.Concise("body"));
            if (obs != null) r["observe"] = obs;
            r["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(r);
        }

        static List<object> HatList()
        {
            var l = new List<object>();
            var all = UnityEngine.Object.FindObjectsOfType(typeof(MindHat)) as MindHat[];
            if (all != null) foreach (var h in all) if (h != null && l.Count < 16) l.Add(HatRow(h));
            return l;
        }

        static MindHat PickHat(string spec, bool attach)
        {
            if (!string.IsNullOrEmpty(spec))
            {
                var go = World.Target(spec, "hat");
                var h = go.GetComponent<MindHat>();
                if (h == null) h = go.GetComponentInParent<MindHat>();
                if (h == null) throw new McpException(McpErr.NotFound, World.Path(go) + " is not a MindHat", "body {op:\"hat\", hat_op:\"status\"} lists the hats");
                return h;
            }
            var all = UnityEngine.Object.FindObjectsOfType(typeof(MindHat)) as MindHat[];
            MindHat best = null;
            float bd = float.MaxValue;
            Vector3 body;
            World.BodyPos(out body);
            if (all != null)
                foreach (var h in all)
                {
                    if (h == null) continue;
                    if (!attach) { if (h.GetBoyInHat() != null) return h; continue; }
                    float d = Vector3.Distance(h.transform.position, body);
                    if (d < bd) { bd = d; best = h; }
                }
            if (best == null) throw new McpException(McpErr.NotFound, attach ? "no mind-control hat in the loaded scenes" : "no hat is worn", "hats exist in pigFight, SubmarineSecret, mindhatBasement and mindHatWagon");
            return best;
        }

        // ---------------------------------------------------------------- swap
        static ToolResult Swap(Json.Obj a, CallContext ctx)
        {
            string to = PlayShared.Str(a, "to", null);
            if (to != "boy" && to != "huddle") return ToolResult.Fail(McpErr.BadArgs, "op swap needs to: boy or huddle");
            bool force = PlayShared.Bool(a, "force", false);
            ToolResult busy;
            var job = ctx.BeginJob(McpLanes.Control, out busy);
            if (job == null) return busy;
            var warnings = new List<string>();
            Vector3 before = Vector3.zero, after = Vector3.zero;
            bool sameNow = false;
            ctx.MainDo(() =>
            {
                if (GameManager.IsLoading()) throw new McpException(McpErr.Refused, "a load is in progress", "wait_until {ready:true} first");
                var boy = ScriptGlobals.boy;
                var hud = ScriptGlobals.huddle;
                if (boy == null || hud == null) throw new McpException(McpErr.NotReady, "swap needs both the boy and the huddle in the scene", "the huddle exists once the master scene is loaded");
                var cur = ScriptGlobals.mainCharacter;
                if (cur != null) before = cur.pos3;
                bool already = to == "boy" ? ScriptGlobals.boyActive && !ScriptGlobals.huddleActive : ScriptGlobals.huddleActive;
                if (already) { warnings.Add("already the " + to); sameNow = true; after = before; return; }
                if (!force)
                {
                    if (cur != null && cur.isDead) throw new McpException(McpErr.Refused, "the body is dead", "wait_until {alive:true}");
                    if (boy.isMindControlling || boy.specialBoyMode != Boy.SpecialBoyMode.None) throw new McpException(McpErr.Refused, "the boy is in a hat or a special mode", "force:true skips this check");
                    throw new McpException(McpErr.Refused, "swapping outside the game's own swap triggers can break scripted scenes (the huddle chapters expect their own setup)", "force:true swaps anyway (MainCharacterSwap)");
                }
                if (to == "boy") MainCharacterSwap.SwapToBoy(); else MainCharacterSwap.SwapToHuddle();
                var now = ScriptGlobals.mainCharacter;
                after = now != null ? now.pos3 : before;
                sameNow = to == "boy" ? now is Boy : now is Huddle;
                warnings.Add("swap has no undo: swap back, or reload the checkpoint");
            });
            bool later = World.WaitVerify(ctx, 2, warnings);
            bool readK = false, sameK = false;
            if (later) ctx.MainDo(() => { var now = ScriptGlobals.mainCharacter; readK = now != null; sameK = to == "boy" ? now is Boy : now is Huddle; });
            var r = PlayShared.Env();
            r["outcome"] = "done";
            r["verdict"] = ReflectValues.Verdict(true, sameNow, true, readK, sameK);
            r["before"] = PlayShared.V(before);
            r["after"] = PlayShared.V(after);
            var obs = ctx.Main(() => World.Concise("body"));
            if (obs != null) r["observe"] = obs;
            r["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(r);
        }
    }
}
