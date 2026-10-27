using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // Hazards.cs (owner WORLD_B, the hazard schema frozen): switch single dangers off without god mode (report 03
    // section 7.2): Killer.DisableKiller, Hulv.DisableKill(true) + SetChaseMode, SentryGun.PowerDown, SpotLightDetector
    // doDisable, guardSpotting disable, WaterGirl stopChase / sleep, ChaserBase stopChasing, ForcePushManager enabled /
    // killEnabled, kill FSM actions Enabled = false. Reading uses Perception.Hazards. Every switch registers Restorer key
    // "hazard:<id>"; air uses boy.properties.swim.infiniteAir / GiveBoyExtraAir / BoyWater.maxDivingTime (key "air");
    // god = core G.God (EditorMode InvincibleBoy, key "god"; death animations still play).
    // Per class (verified in AC/): Killer.Enable/DisableKiller + getActiveState (Killer.cs:27-59); DelayedKiller private
    // isEnabled / delayKillActive (DelayedKiller.cs:15-31, 72); HeadlessKillTrigger public isActive; Hulv.DisableKill /
    // private killdisabled / SetChaseMode / GetChaseMode (Hulv.cs:378, 1099-1116, 1329); SentryGun.PowerDown / WakeUp /
    // private isAwake (SentryGun.cs:146, 202-215); SpotLightDetector.doDisable / doEnable / private scriptActive
    // (SpotLightDetector.cs:32, 92-100); guardSpotting private Enable / Disable / public activated (guardSpotting.cs:37,
    // 131-139); WaterGirl private FallASleep / WakeUp / StartChase / awake (WaterGirl.cs:453-497); ChaserBase public
    // stopChasing (ChaserBase.cs:41-47, 108-117, no way back); ForcePushManager.enabled (its private killEnabled is
    // re-armed 0.2 s after a load, ForcePushManager.cs:306-316, so the component is switched instead); crushers, flush
    // killer, rope guns, the lineup monitor and SentryGunAimAndShoot: Behaviour.enabled (their kills run in FixedUpdate);
    // kill FSMs: FsmStateAction.Enabled of PD_KillBoy / KillBoyWithVoiceEmotion / RopeGunShootAndKillMainCharacter
    // (FsmState.cs OnEnter skips disabled actions). Infinite air is re-applied to every new Boy (a tick every 30 frames),
    // since BoyProperties lives on the Boy object that every respawn re-creates (FP/Boy.cs:383-398, BoyUtils.cs:982-986).
    public static class Hazards
    {
        public const string Owner = "WORLD_B";
        static readonly string[] KillActions = { "PD_KillBoy", "KillBoyWithVoiceEmotion", "RopeGunShootAndKillMainCharacter" };

        sealed class Sw
        {
            public Component comp;
            public string kind, offHow, onHow;
            public Func<bool> isOn;
            public Action<bool> set;
            public bool canReArm = true;
        }

        static readonly object gate = new object();
        static readonly Dictionary<string, bool> original = new Dictionary<string, bool>();
        static readonly Dictionary<int, HulvAI.ChaseMode> chaseBefore = new Dictionary<int, HulvAI.ChaseMode>();
        static readonly Dictionary<int, bool> killFsm = new Dictionary<int, bool>();
        static readonly Dictionary<int, KeyValuePair<BoyWater, float>> diveBefore = new Dictionary<int, KeyValuePair<BoyWater, float>>();
        static bool airHeld, airInfinite, airInfiniteBefore, godHeld, godOrig;
        static int airBoyId, airTicks;

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("hazard", "Hazards", PlayShared.Dev, Owner,
                "Dangers near the body. near is read-only (kind, state, eta, whether it can be switched). set turns one hazard (target) or all near ones off / on (dogs, guards, sentries, spotlights, mermaid, kill zones, shockwave, kill FSMs). air sets infinite air / refills / changes the dive limit. god toggles invincibility (death animations can still play). Restored by restore. Each switch reports the call used and a verdict.",
                Schema.Obj().Enum("op", "near, set, air or god.", "near", "near", "set", "air", "god")
                    .Str("target", "op set: hazard id or path.").Bool("all_near", "op set: every hazard within radius.", false)
                    .Num("radius", "op near / set (m).", 20, 1, 200).Bool("on", "op set: hazard active; op god: invincible.")
                    .Bool("infinite", "op air.").Num("refill_s", "op air: add seconds of air now.", 0, 60).Num("max_dive_s", "op air: dive limit of the current water.", 1, 120).Build(),
                Schema.Obj().Enum("outcome", "", null, "done", "refused", "not_found")
                    .Arr("hazards", "op near.", Schema.OutOf(Observer.HazardSchema().Bool("switchable", "set can turn it off.").Bool("switched_off", "Turned off by a tool.")), 0, 50)
                    .Arr("changed", "op set.", Schema.OutOf(Schema.Obj().Str("id", "").Str("kind", "").Str("how", "Call used.").Enum("verdict", "", null, Reflect.Verdicts)), 0, 50)
                    .OutObj("air", "op air.", Schema.Obj().Bool("infinite", "").Num("left_s", "", -1, 1e4).Num("max_s", "", 0, 1e4)).Bool("god", "Invincible now.")
                    .Arr("warnings", "Partial disables can soft-lock scenes that wait for the kill.", Schema.Str(), 0, 20).BuildOutput()), Handle);
            PlayRegistry.OnTick("hazard_air", AirTick);
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "near");
            switch (op)
            {
                case "near": return Near(a, ctx);
                case "set": return Set(a, ctx);
                case "air": return Air(a, ctx);
                case "god": return God(a, ctx);
            }
            return ToolResult.Fail(McpErr.BadArgs, "unknown op " + op);
        }

        // ---------------------------------------------------------------- switches (main thread)
        static Sw SwitchFor(GameObject go)
        {
            foreach (var c in go.GetComponents<MonoBehaviour>())
            {
                var s = SwitchFor(c);
                if (s != null) return s;
            }
            foreach (var f in go.GetComponents<PlayMakerFSM>())
            {
                var s = FsmSwitch(f);
                if (s != null) return s;
            }
            return null;
        }

        static Sw SwitchFor(MonoBehaviour c)
        {
            if (c == null) return null;
            var killer = c as Killer;
            if (killer != null) return new Sw { comp = c, kind = "kill_zone", offHow = "Killer.DisableKiller()", onHow = "Killer.EnableKiller()", isOn = () => killer.getActiveState(), set = on => { if (on) killer.EnableKiller(); else killer.DisableKiller(); } };
            if (c is DelayedKiller)
                return new Sw { comp = c, kind = "fall", offHow = "DelayedKiller.isEnabled = false", onHow = "DelayedKiller.isEnabled = true", isOn = () => (bool)World.Get(c, "isEnabled"), set = on => { World.Set(c, "isEnabled", on); if (!on) World.Set(c, "delayKillActive", false); } };
            var hk = c as HeadlessKillTrigger;
            if (hk != null) return new Sw { comp = c, kind = "kill_zone", offHow = "HeadlessKillTrigger.isActive = false", onHow = "HeadlessKillTrigger.isActive = true", isOn = () => hk.isActive, set = on => hk.isActive = on };
            var hulv = c as Hulv;
            if (hulv != null)
                return new Sw
                {
                    comp = c, kind = "dog", offHow = "Hulv.DisableKill(true) + SetChaseMode(Idle)", onHow = "Hulv.DisableKill(false) + chase mode back",
                    isOn = () => !(bool)World.Get(hulv, "killdisabled"),
                    set = on =>
                    {
                        int id = hulv.GetInstanceID();
                        var ai = hulv.GetHulvAI();
                        if (!on)
                        {
                            hulv.DisableKill(true);
                            if (ai != null)
                            {
                                lock (gate) if (!chaseBefore.ContainsKey(id)) chaseBefore[id] = hulv.GetChaseMode();
                                hulv.SetChaseMode(HulvAI.ChaseMode.Idle);
                            }
                        }
                        else
                        {
                            hulv.DisableKill(false);
                            HulvAI.ChaseMode m;
                            bool had;
                            lock (gate) { had = chaseBefore.TryGetValue(id, out m); chaseBefore.Remove(id); }
                            if (had && ai != null) hulv.SetChaseMode(m);
                        }
                    }
                };
            var sentry = c as SentryGun;
            if (sentry != null) return new Sw { comp = c, kind = "sentry", offHow = "SentryGun.PowerDown()", onHow = "SentryGun.WakeUp()", isOn = () => (bool)World.Get(sentry, "isAwake"), set = on => { if (on) sentry.WakeUp(); else sentry.PowerDown(); } };
            var spot = c as SpotLightDetector;
            if (spot != null) return new Sw { comp = c, kind = "spotlight", offHow = "SpotLightDetector.doDisable()", onHow = "SpotLightDetector.doEnable()", isOn = () => (bool)World.Get(spot, "scriptActive"), set = on => { if (on) spot.doEnable(); else spot.doDisable(); } };
            var gs = c as guardSpotting;
            if (gs != null) return new Sw { comp = c, kind = "guard", offHow = "guardSpotting.Disable()", onHow = "guardSpotting.Enable()", isOn = () => gs.activated, set = on => World.Call(gs, on ? "Enable" : "Disable") };
            var girl = c as WaterGirl;
            if (girl != null)
                return new Sw { comp = c, kind = "mermaid", offHow = "WaterGirl.FallASleep() (stopChase + sleep)", onHow = "WaterGirl.WakeUp() + StartChase()", isOn = () => (bool)World.Get(girl, "awake"),
                    set = on => { if (on) { World.Call(girl, "WakeUp"); World.Call(girl, "StartChase"); } else World.Call(girl, "FallASleep"); } };
            var chaser = c as ChaserBase;
            if (chaser != null)
                return new Sw { comp = c, kind = "guard", offHow = "ChaserBase.stopChasing", onHow = "", canReArm = false, isOn = () => Convert.ToInt32(World.Get(chaser, "guardMode")) != 0,
                    set = on => { if (!on && chaser.stopChasing != null && chaser.stopChasing.action != null) chaser.stopChasing.action(); } };
            var fpm = c as ForcePushManager;
            if (fpm != null)
                return new Sw { comp = c, kind = "shockwave", offHow = "ForcePushManager.enabled = false", onHow = "ForcePushManager.enabled = true (music clock resynced, a pending kill dropped)", isOn = () => fpm.enabled,
                    set = on => { if (on && !fpm.enabled) ResyncShockwave(fpm); fpm.enabled = on; } };
            if (c is WaterFlushKiller) return Enabled(c, "kill_zone", "WaterFlushKiller.enabled");
            if (c is GateCrush || c is CityGateCrush || c is WagonCrush) return Enabled(c, "crusher", c.GetType().Name + ".enabled");
            if (c is SentryGunAimAndShoot) return Enabled(c, "sentry", "SentryGunAimAndShoot.enabled");
            if (c is ZombieMonitorDetection) return Enabled(c, "other", "ZombieMonitorDetection.enabled");
            var rope = c as RopeGun;
            if (rope != null)
                return new Sw { comp = c, kind = "rope_gun", offHow = "RopeGun.DoCancelShot() + enabled = false", onHow = "RopeGun.enabled = true", isOn = () => rope.enabled,
                    set = on => { if (!on) { try { rope.DoCancelShot(); } catch { } } rope.enabled = on; } };
            return null;
        }

        // ForcePushManager keeps previousMusicTime and its MusicEventCheckers while disabled, so the first Update after
        // enabled = true sees a delta of up to a whole cycle: a pending killTimer fires and the missed cues fire
        // (AC/ForcePushManager.cs:229-258, 287-300, 362-376; FP/MusicEventChecker.cs). Put the clock on the current music
        // position, drop a pending kill and re-arm the checkers (SetEventTime re-reads the position on the next Update).
        static void ResyncShockwave(ForcePushManager f)
        {
            try
            {
                var ga = PersistentBehaviour<GlobalAudio>.instance;
                if (ga == null || ga.music == null) return;
                float t = ga.music.GetMusicPosition_s();
                if (t > f.musicCycleTime) t -= f.musicCycleTime;
                World.Set(f, "previousMusicTime", t);
                object k;
                if (World.TryGet(f, "killTimer", out k) && k is float && (float)k > 0f) World.Set(f, "killTimer", 0f);
                foreach (var n in new[] { "blowChecker", "warningChecker", "resetChecker" })
                {
                    object ch;
                    var mc = World.TryGet(f, n, out ch) ? ch as MusicEventChecker : null;
                    if (mc != null) mc.SetEventTime(mc.eventTime_s);
                }
            }
            catch (Exception e) { DevLog.Write("[play] shockwave resync: " + e.GetType().Name + ": " + e.Message); }
        }

        static Sw Enabled(MonoBehaviour c, string kind, string what)
        {
            return new Sw { comp = c, kind = kind, offHow = what + " = false", onHow = what + " = true", isOn = () => c.enabled, set = on => c.enabled = on };
        }

        static List<FsmStateAction> KillActionsOf(PlayMakerFSM f)
        {
            var l = new List<FsmStateAction>();
            var fsm = f.Fsm;
            if (fsm == null || !fsm.Initialized || fsm.States == null) return l;
            foreach (var st in fsm.States)
            {
                if (st == null) continue;
                FsmStateAction[] acts;
                try { acts = st.Actions; } catch { continue; }
                if (acts == null) continue;
                foreach (var act in acts) if (act != null && Array.IndexOf(KillActions, act.GetType().Name) >= 0) l.Add(act);
            }
            return l;
        }

        static bool IsKillFsm(PlayMakerFSM f)
        {
            int id = f.GetInstanceID();
            lock (gate)
            {
                bool k;
                if (killFsm.TryGetValue(id, out k)) return k;
            }
            bool r = KillActionsOf(f).Count > 0;
            var fsm = f.Fsm;
            if (fsm != null && fsm.Initialized) lock (gate) killFsm[id] = r;
            return r;
        }

        static Sw FsmSwitch(PlayMakerFSM f)
        {
            if (f == null || !IsKillFsm(f)) return null;
            var acts = KillActionsOf(f);
            string name = f.gameObject.name.ToLowerInvariant();
            string kind = name.Contains("guard") || name.Contains("man") ? "guard" : name.Contains("rope") ? "rope_gun" : "other";
            return new Sw
            {
                comp = f, kind = kind, offHow = "kill actions disabled (" + acts.Count + " in FSM " + f.FsmName + ")", onHow = "kill actions enabled (FSM " + f.FsmName + ")",
                isOn = () => { foreach (var x in KillActionsOf(f)) if (x.Enabled) return true; return false; },
                set = on => { foreach (var x in KillActionsOf(f)) x.Enabled = on; }
            };
        }

        static List<PlayMakerFSM> KillFsmsNear(Vector3 center, float radius)
        {
            var l = new List<PlayMakerFSM>();
            float r2 = radius * radius;
            foreach (var f in PlayMakerFSM.FsmList)
                if (f != null && (f.transform.position - center).sqrMagnitude <= r2 && IsKillFsm(f)) l.Add(f);
            return l;
        }

        static string Key(string id) { return "hazard:" + id; }

        // ---------------------------------------------------------------- near
        static ToolResult Near(Json.Obj a, CallContext ctx)
        {
            float radius = (float)PlayShared.Num(a, "radius", 20);
            var r = ctx.Main(() =>
            {
                var o = PlayShared.Env();
                Vector3 body;
                if (!World.BodyPos(out body)) throw new McpException(McpErr.NotReady, "no controlled body", "wait until the game is ready");
                var rows = new List<object>();
                var seen = new HashSet<int>();
                foreach (var h in Perception.Hazards(body, radius, 50))
                {
                    if (rows.Count >= 50) break;
                    var row = new Json.Obj();
                    row["id"] = h.id;
                    row["kind"] = h.kind;
                    row["name"] = h.name;
                    row["dx"] = PlayShared.R(h.dx);
                    row["dy"] = PlayShared.R(h.dy);
                    row["dist"] = PlayShared.R(Mathf.Max(0f, h.dist));
                    row["state"] = h.state ?? "";
                    row["eta_s"] = PlayShared.R(h.etaS);
                    row["threat"] = h.threat;
                    row["safe"] = h.safe;
                    row["music_timed"] = h.musicTimed;
                    var sw = h.go != null ? SwitchFor(h.go) : null;
                    row["switchable"] = sw != null && h.kind != "drowning" && h.kind != "fall";
                    row["switched_off"] = sw != null && IsSet(Key(h.id)) && !SafeOn(sw);
                    if (h.go != null) seen.Add(h.go.GetInstanceID());
                    rows.Add(row);
                }
                foreach (var f in KillFsmsNear(body, radius))
                {
                    if (rows.Count >= 50 || seen.Contains(f.gameObject.GetInstanceID())) continue;
                    seen.Add(f.gameObject.GetInstanceID());
                    var sw = FsmSwitch(f);
                    if (sw == null) continue;
                    string id = World.Id(f.gameObject, "h");
                    var row = new Json.Obj();
                    row["id"] = id;
                    row["kind"] = sw.kind;
                    row["name"] = f.gameObject.name;
                    World.Dist(row, f.transform.position, body);
                    row.Remove("dz");
                    row.order.Remove("dz");
                    row["state"] = "fsm " + f.FsmName + ": " + (f.Fsm != null ? f.Fsm.ActiveStateName : "?");
                    row["eta_s"] = -1.0;
                    row["threat"] = false;
                    row["safe"] = false;
                    row["music_timed"] = false;
                    row["switchable"] = true;
                    row["switched_off"] = IsSet(Key(id)) && !SafeOn(sw);
                    rows.Add(row);
                }
                o["outcome"] = "done";
                o["hazards"] = rows;
                return o;
            });
            return ToolResult.Ok(r);
        }

        static bool SafeOn(Sw sw)
        {
            try { return sw.isOn(); } catch { return true; }
        }

        static bool IsSet(string key)
        {
            try { return Restorer.IsSet(key); }
            catch (NotImplementedException) { return false; }
        }

        // ---------------------------------------------------------------- set
        static ToolResult Set(Json.Obj a, CallContext ctx)
        {
            if (!PlayShared.Has(a, "on")) return ToolResult.Fail(McpErr.BadArgs, "op set needs on (false = switch the hazard off)");
            bool on = PlayShared.Bool(a, "on", false);
            bool allNear = PlayShared.Bool(a, "all_near", false);
            string target = PlayShared.Str(a, "target", null);
            if (!allNear && string.IsNullOrEmpty(target)) return ToolResult.Fail(McpErr.BadArgs, "op set needs target or all_near:true", "hazard {op:\"near\"} lists the ids");
            float radius = (float)PlayShared.Num(a, "radius", 20);
            var warnings = new List<string>();
            var list = new List<KeyValuePair<string, Sw>>();
            var rows = new List<Json.Obj>();
            var afterOk = new List<bool>();
            ctx.MainDo(() =>
            {
                if (allNear)
                {
                    Vector3 body;
                    if (!World.BodyPos(out body)) throw new McpException(McpErr.NotReady, "no controlled body", "wait until the game is ready");
                    var seen = new HashSet<int>();
                    foreach (var h in Perception.Hazards(body, radius, 50))
                    {
                        if (h.go == null || !seen.Add(h.go.GetInstanceID()) || h.kind == "drowning" || h.kind == "fall") continue;
                        var sw = SwitchFor(h.go);
                        if (sw != null) list.Add(new KeyValuePair<string, Sw>(h.id, sw));
                    }
                    foreach (var f in KillFsmsNear(body, radius))
                    {
                        if (!seen.Add(f.gameObject.GetInstanceID())) continue;
                        var sw = FsmSwitch(f);
                        if (sw != null) list.Add(new KeyValuePair<string, Sw>(World.Id(f.gameObject, "h"), sw));
                    }
                }
                else
                {
                    var go = World.Target(target, "target");
                    var sw = SwitchFor(go);
                    if (sw == null)
                    {
                        foreach (var t in go.GetComponentsInParent<MonoBehaviour>(true)) { sw = SwitchFor(t); if (sw != null) break; }
                    }
                    if (sw == null) throw new McpException(McpErr.NotFound, World.Path(go) + " is not a switchable hazard", "hazard {op:\"near\"} shows switchable:true rows; god mode covers the rest");
                    list.Add(new KeyValuePair<string, Sw>(World.Id(sw.comp.gameObject, "h"), sw));
                }
                foreach (var kv in list)
                {
                    var sw = kv.Value;
                    var row = new Json.Obj();
                    row["id"] = kv.Key;
                    row["kind"] = sw.kind;
                    bool before = SafeOn(sw);
                    if (on && !sw.canReArm)
                    {
                        row["how"] = "cannot re-arm " + sw.comp.GetType().Name + " (reload the checkpoint)";
                        row["verdict"] = before == on ? "confirmed" : "not_applied";
                        rows.Add(row);
                        afterOk.Add(before == on);
                        continue;
                    }
                    if (before == on)
                    {
                        row["how"] = "already " + (on ? "on" : "off");
                        rows.Add(row);
                        afterOk.Add(true);
                        continue;
                    }
                    string key = Key(kv.Key);
                    lock (gate) if (!original.ContainsKey(key)) original[key] = before;
                    var comp = sw.comp;
                    var undo = sw;
                    bool orig;
                    lock (gate) orig = original[key];
                    World.Restore(key, sw.kind + " " + comp.gameObject.name, () =>
                    {
                        lock (gate) original.Remove(key);
                        if (comp == null) return "gone (unloaded)";
                        if (!orig || undo.canReArm) undo.set(orig);
                        return orig ? (undo.canReArm ? "back on" : "left off: " + comp.GetType().Name + " cannot be re-armed, reload the checkpoint") : "back off";
                    });
                    try { sw.set(on); }
                    catch (McpException) { throw; }
                    catch (Exception e) { throw new McpException(McpErr.GameError, sw.comp.GetType().Name + ": " + ReflectValues.Inner(e).Message); }
                    row["how"] = on ? sw.onHow : sw.offHow;
                    if (on == orig) { World.Forget(key); lock (gate) original.Remove(key); }
                    rows.Add(row);
                    afterOk.Add(SafeOn(sw) == on);
                }
            });
            bool later = rows.Count > 0 && World.WaitVerify(ctx, 2, warnings);
            var changed = new List<object>();
            ctx.MainDo(() =>
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var sw = list[i].Value;
                    bool alive = sw.comp != null;
                    bool sameK = alive && SafeOn(sw) == on;
                    if (!rows[i].ContainsKey("verdict")) rows[i]["verdict"] = ReflectValues.Verdict(true, afterOk[i], true, later && alive, sameK);
                    changed.Add(rows[i]);
                }
            });
            if (!on && changed.Count > 0) warnings.Add("partial disables can soft-lock scenes that wait for the kill; restore puts them back");
            if (allNear && changed.Count == 0) warnings.Add("no switchable hazard within " + PlayShared.F(radius) + " m");
            var r = PlayShared.Env();
            r["outcome"] = "done";
            r["changed"] = changed;
            if (warnings.Count > 0) r["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(r);
        }

        // ---------------------------------------------------------------- air
        static ToolResult Air(Json.Obj a, CallContext ctx)
        {
            var warnings = new List<string>();
            bool hasInf = PlayShared.Has(a, "infinite"), hasRefill = PlayShared.Has(a, "refill_s"), hasMax = PlayShared.Has(a, "max_dive_s");
            var r = ctx.Main(() =>
            {
                var boy = ScriptGlobals.boy;
                if (boy == null) throw new McpException(McpErr.NotReady, "no boy in the scene", "wait until the game is ready");
                if (ScriptGlobals.huddleActive) warnings.Add("air applies to the boy; the huddle has no air limit");
                lock (gate)
                {
                    if (hasInf)
                    {
                        bool v = PlayShared.Bool(a, "infinite", true);
                        if (!airHeld) { airInfiniteBefore = boy.properties.swim.infiniteAir; airHeld = true; }
                        airInfinite = v;
                        boy.properties.swim.infiniteAir = v;
                        airBoyId = boy.GetInstanceID();
                    }
                    var water = boy.status != null ? boy.status.water : null;
                    if (hasMax)
                    {
                        if (water == null) warnings.Add("not_in_water: max_dive_s sets the limit of the water the boy is in; nothing changed");
                        else
                        {
                            int id = water.GetInstanceID();
                            if (!diveBefore.ContainsKey(id)) diveBefore[id] = new KeyValuePair<BoyWater, float>(water, water.maxDivingTime);
                            water.maxDivingTime = (float)PlayShared.Num(a, "max_dive_s", 11);
                        }
                    }
                    if (hasRefill)
                    {
                        float s = (float)PlayShared.Num(a, "refill_s", 0);
                        if (s > 0) boy.GiveBoyExtraAir(s);
                    }
                    if (hasInf || (hasMax && water != null)) World.Restore("air", "air (infinite / dive limits)", UndoAir);
                    if (airHeld && !airInfinite && airInfiniteBefore == false && diveBefore.Count == 0) { airHeld = false; World.Forget("air"); }
                }
                var o = PlayShared.Env();
                o["outcome"] = "done";
                o["air"] = AirState(boy);
                return o;
            });
            if (warnings.Count > 0) r["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(r);
        }

        static Json.Obj AirState(Boy boy)
        {
            var o = new Json.Obj();
            var water = boy.status != null ? boy.status.water : null;
            o["infinite"] = BoyUtils.HasInfiniteAir(boy);
            float max = water != null ? water.maxDivingTime : boy.properties.swim.maxDivingTime;
            o["max_s"] = PlayShared.R(Mathf.Max(0f, max));
            o["left_s"] = water != null && boy.divingTimeElapsed > 0f ? PlayShared.R(Mathf.Max(0f, max - boy.divingTimeElapsed)) : -1.0;
            return o;
        }

        static string UndoAir()
        {
            var parts = new List<string>();
            lock (gate)
            {
                if (airHeld)
                {
                    var boy = ScriptGlobals.boy;
                    if (boy != null) boy.properties.swim.infiniteAir = airInfiniteBefore;
                    parts.Add("infinite air " + (airInfiniteBefore ? "on" : "off"));
                }
                airHeld = false;
                airInfinite = false;
                int n = 0;
                foreach (var kv in diveBefore.Values) if (kv.Key != null) { kv.Key.maxDivingTime = kv.Value; n++; }
                diveBefore.Clear();
                if (n > 0) parts.Add(n + " dive limit(s) back");
            }
            return parts.Count == 0 ? "nothing to undo" : string.Join(", ", parts.ToArray());
        }

        static void AirTick()
        {
            if (!airHeld || ++airTicks % 30 != 0) return;
            var boy = ScriptGlobals.boy;
            if (boy == null || boy.properties == null || boy.properties.swim == null) return;
            int id = boy.GetInstanceID();
            if (id == airBoyId && boy.properties.swim.infiniteAir == airInfinite) return;
            boy.properties.swim.infiniteAir = airInfinite;
            airBoyId = id;
        }

        // ---------------------------------------------------------------- god
        static ToolResult God(Json.Obj a, CallContext ctx)
        {
            var warnings = new List<string>();
            var r = ctx.Main(() =>
            {
                if (PlayShared.Has(a, "on"))
                {
                    bool on = PlayShared.Bool(a, "on", true);
                    bool before = G.God;
                    if (before != on)
                    {
                        bool orig;
                        lock (gate) { if (!godHeld) { godOrig = before; godHeld = true; } orig = godOrig; }
                        World.Restore("god", "god mode (EditorMode InvincibleBoy)", () => { lock (gate) godHeld = false; G.God = orig; return orig ? "god on" : "god off"; });
                        G.God = on;
                        if (on == orig) { World.Forget("god"); lock (gate) godHeld = false; }
                    }
                    if (on) warnings.Add("death animations and scripted kill poses can still play (PD_KillBoy hides the boy); per-hazard set is cleaner");
                }
                var o = PlayShared.Env();
                o["outcome"] = "done";
                o["god"] = G.God;
                return o;
            });
            if (warnings.Count > 0) r["warnings"] = World.Strs(warnings);
            return ToolResult.Ok(r);
        }
    }
}
