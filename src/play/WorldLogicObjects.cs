using System;
using System.Collections.Generic;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // WorldLogicObjects.cs (owner WORLD_B): logic ops spawn_clone, body_set, anim_set (report 03 sections 6.4-6.6).
    // spawn_clone: Dynamics.Create(original, pos, rot) (FP/Dynamics.cs:58-63, children cleared on every load,
    // FP/GameManager.cs:446) plus Prewarm's PreAwake recipe: IPreAwake components (inactive included) ordered by
    // IPrioritized.Priority descending, run from the end, PreAwake(callcount++) until true or 1000 calls
    // (FP/Prewarm.cs:26-90). Clones of characters, savepoints and singletons are refused; persistent components warn
    // (their string id is shared with the original). Restorer key logic:clone:<iid> destroys the clone.
    // body_set: Rigidbody velocity / angularVelocity / isKinematic / AddForce(Impulse) / WakeUp / Sleep; the verdict
    // covers what can be read back at once (an impulse shows only after the next physics step). Key logic:body:<iid>
    // puts isKinematic back. anim_set: legacy Animation on active objects only: Play / Stop, AnimationState time / speed
    // (time on a stopped clip is sampled once). Key logic:anim:<iid>:<clip> puts speed, time and playing back.
    public static partial class WorldLogic
    {
        // ---------------------------------------------------------------- spawn_clone
        static ToolResult SpawnClone(Json.Obj a, CallContext ctx)
        {
            int k = PlayShared.Int(a, "verify_frames", 2);
            bool pre = PlayShared.Bool(a, "preawake", true);
            Vector3 pos, rot;
            bool hasPos = PlayShared.Vec3(a, "position", out pos), hasRot = PlayShared.Vec3(a, "rotation", out rot);
            var warnings = new List<string>();
            var r = Result(warnings);
            GameObject clone = null;
            ctx.MainDo(() =>
            {
                var orig = World.Target(PlayShared.Str(a, "object", null), "object");
                if (World.Child<Character>(orig) != null) throw new McpException(McpErr.Refused, "cloning a character breaks ScriptGlobals and the camera", "characters are re-created by the game on respawn");
                if (World.Child<Savepoint>(orig) != null) throw new McpException(McpErr.Refused, "the object holds a Savepoint: a clone would register under the same savepoint index and write the save", "clone a part without the savepoint");
                foreach (var c in orig.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (c == null) continue;
                    var bt = c.GetType().BaseType;
                    if (bt != null && bt.IsGenericType && (bt.Name.StartsWith("PersistentBehaviour", StringComparison.Ordinal) || bt.Name.StartsWith("SingletonBehaviour", StringComparison.Ordinal)))
                        throw new McpException(McpErr.Refused, c.GetType().Name + " is a game singleton: a clone would replace the instance", "clone an ordinary scene object");
                }
                var persistent = new List<string>();
                foreach (var c in orig.GetComponentsInChildren<MonoBehaviour>(true))
                    if (c != null && c.GetType().Name.StartsWith("Persistent", StringComparison.Ordinal) && !persistent.Contains(c.GetType().Name)) persistent.Add(c.GetType().Name);
                if (persistent.Count > 0) warnings.Add("persistent components (" + string.Join(", ", persistent.ToArray()) + ") share the original's save id: the clone writes the original's flag");
                if (PersistentBehaviour<Dynamics>.instance == null) throw new McpException(McpErr.NotReady, "no Dynamics container (master scene not loaded)", "wait until the game is ready");
                var p = hasPos ? pos : orig.transform.position + Vector3.right;
                var q = hasRot ? Quaternion.Euler(rot) : orig.transform.rotation;
                clone = Dynamics.Create(orig, p, q);
                if (clone == null) throw new McpException(McpErr.GameError, "Dynamics.Create returned nothing");
                clone.name = orig.name + " (mcp clone)";
                int calls = 0, errors = 0, comps = 0;
                if (pre)
                {
                    var list = new List<Component>(clone.GetComponentsInChildren(typeof(IPreAwake), true));
                    list.Sort((x, y) => Prio(y).CompareTo(Prio(x)));
                    comps = list.Count;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var pa = list[i] as IPreAwake;
                        if (pa == null) continue;
                        for (int n = 0; n <= 1000; n++)
                        {
                            bool done;
                            calls++;
                            try { done = pa.PreAwake(n); }
                            catch (Exception) { errors++; done = true; }
                            if (done) break;
                        }
                    }
                    if (errors > 0) warnings.Add(errors + " PreAwake call(s) threw (the Unity log has them)");
                }
                else warnings.Add("no PreAwake: the clone has no signal ports and its SignalConnectors make no wires");
                var go = clone;
                World.Restore("logic:clone:" + clone.GetInstanceID(), "clone " + clone.name, () =>
                {
                    if (go == null) return "already gone (cleared by a load)";
                    UnityEngine.Object.Destroy(go);
                    return "destroyed";
                });
                var row = new Json.Obj();
                row["id"] = World.Id(clone, "o");
                row["path"] = World.Path(clone);
                row["active"] = clone.activeInHierarchy;
                row["preawake_components"] = (double)comps;
                row["preawake_calls"] = (double)calls;
                r["items"] = new List<object> { row };
                r["total"] = 1.0;
                r["after"] = PlayShared.V(clone.transform.position);
                if (!orig.activeInHierarchy) warnings.Add("the original is inactive, so the clone is inactive too (set_active it)");
            });
            bool later = k > 0 && World.WaitVerify(ctx, k, warnings);
            bool alive = false;
            if (later) ctx.MainDo(() => { alive = clone != null; if (alive) r["after_k"] = PlayShared.V(clone.transform.position); });
            r["verdict"] = ReflectValues.Verdict(clone != null, true, k > 0, later, alive);
            r["note"] = "clone under _Dynamics: it is destroyed by the next load (or by restore); streaming never culls it";
            Finish(r, warnings);
            return ToolResult.Ok(r);
        }

        static int Prio(Component c)
        {
            var p = c as IPrioritized;
            return p != null ? p.Priority : 0;
        }

        // ---------------------------------------------------------------- body_set
        static Json.Obj RbState(Rigidbody rb)
        {
            var o = new Json.Obj();
            if (rb == null) { o["destroyed"] = true; return o; }
            o["pos"] = PlayShared.V(rb.position);
            o["velocity"] = PlayShared.V(rb.velocity);
            o["angular"] = PlayShared.V(rb.angularVelocity);
            o["kinematic"] = rb.isKinematic;
            o["sleeping"] = rb.IsSleeping();
            return o;
        }

        static ToolResult BodySet(Json.Obj a, CallContext ctx)
        {
            int k = PlayShared.Int(a, "verify_frames", 2);
            Vector3 vel, ang, force;
            bool hasVel = PlayShared.Vec3(a, "velocity", out vel), hasAng = PlayShared.Vec3(a, "angular", out ang), hasForce = PlayShared.Vec3(a, "force", out force);
            bool hasKin = PlayShared.Has(a, "kinematic"), kin = PlayShared.Bool(a, "kinematic", false);
            bool hasWake = PlayShared.Has(a, "wake"), wake = PlayShared.Bool(a, "wake", true);
            if (!hasVel && !hasAng && !hasForce && !hasKin && !hasWake) return ToolResult.Fail(McpErr.BadArgs, "op body_set needs velocity, angular, force, kinematic or wake");
            var warnings = new List<string>();
            var r = Result(warnings);
            Rigidbody rb = null;
            var now = new List<string>();
            ctx.MainDo(() =>
            {
                var go = World.Target(PlayShared.Str(a, "object", null), "object");
                rb = go.GetComponent<Rigidbody>();
                if (rb == null) rb = go.GetComponentInChildren<Rigidbody>();
                if (rb == null) rb = go.GetComponentInParent<Rigidbody>();
                if (rb == null) throw new McpException(McpErr.NotFound, World.Path(go) + " has no Rigidbody", "body_set moves physics objects (crates, debris); body {op:\"teleport\"} moves characters");
                if (rb.GetComponentInParent<Character>() != null) warnings.Add("this rigidbody belongs to a character: its controller overwrites it every step");
                r["before"] = RbState(rb);
                if (hasKin)
                {
                    bool before = rb.isKinematic;
                    var b = rb;
                    if (before != kin) World.Restore("logic:body:" + rb.GetInstanceID(), "rigidbody " + rb.name + " kinematic", () => { if (b == null) return "gone"; b.isKinematic = before; return "kinematic " + before; });
                    rb.isKinematic = kin;
                }
                if (hasVel) rb.velocity = vel;
                if (hasAng) rb.angularVelocity = ang;
                if (hasForce) { rb.AddForce(force, ForceMode.Impulse); warnings.Add("the impulse acts at the next physics step (time must run to see it)"); }
                if (hasWake) { if (wake) rb.WakeUp(); else rb.Sleep(); }
                r["after"] = RbState(rb);
                if (hasKin && rb.isKinematic != kin) now.Add("kinematic");
                if (hasVel && !rb.isKinematic && (rb.velocity - vel).sqrMagnitude > 1e-4f) now.Add("velocity");
                if (hasAng && !rb.isKinematic && (rb.angularVelocity - ang).sqrMagnitude > 1e-4f) now.Add("angular");
                if (rb.isKinematic && (hasVel || hasAng)) warnings.Add("kinematic bodies ignore velocity");
            });
            bool later = k > 0 && World.WaitVerify(ctx, k, warnings);
            bool readK = false, sameK = true;
            if (later)
                ctx.MainDo(() =>
                {
                    if (rb == null) return;
                    readK = true;
                    r["after_k"] = RbState(rb);
                    if (hasKin && rb.isKinematic != kin) sameK = false;
                });
            r["verdict"] = ReflectValues.Verdict(rb != null, now.Count == 0, k > 0 && hasKin, readK, sameK);
            if (now.Count > 0) r["note"] = "not applied: " + string.Join(", ", now.ToArray());
            else r["note"] = "velocities change by physics as soon as time runs; the verdict checks them right after the write and kinematic after verify_frames";
            Finish(r, warnings);
            return ToolResult.Ok(r);
        }

        // ---------------------------------------------------------------- anim_set
        static Json.Obj AnimState(Animation anim, string only)
        {
            var o = new Json.Obj();
            if (anim == null) { o["destroyed"] = true; return o; }
            var clips = new List<object>();
            foreach (AnimationState st in anim)
            {
                if (st == null || (only != null && st.name != only) || clips.Count >= 20) continue;
                var c = new Json.Obj();
                c["clip"] = st.name;
                c["time"] = PlayShared.R(st.time, 3);
                c["length"] = PlayShared.R(st.length, 3);
                c["speed"] = PlayShared.R(st.speed, 3);
                c["playing"] = anim.IsPlaying(st.name);
                c["weight"] = PlayShared.R(st.weight, 3);
                clips.Add(c);
            }
            o["playing"] = anim.isPlaying;
            o["clips"] = clips;
            return o;
        }

        static ToolResult AnimSet(Json.Obj a, CallContext ctx)
        {
            int k = PlayShared.Int(a, "verify_frames", 2);
            string clip = PlayShared.Str(a, "clip", null);
            bool hasTime = PlayShared.Has(a, "time"), hasSpeed = PlayShared.Has(a, "speed"), hasPlay = PlayShared.Has(a, "play");
            float time = (float)PlayShared.Num(a, "time", 0), speed = (float)PlayShared.Num(a, "speed", 1);
            bool play = PlayShared.Bool(a, "play", true);
            var warnings = new List<string>();
            var r = Result(warnings);
            Animation anim = null;
            string name = null;
            bool sameNow = true;
            ctx.MainDo(() =>
            {
                var go = World.Target(PlayShared.Str(a, "object", null), "object");
                anim = go.GetComponent<Animation>();
                if (anim == null) anim = go.GetComponentInChildren<Animation>();
                if (anim == null) throw new McpException(McpErr.NotFound, World.Path(go) + " has no legacy Animation", "doors, lifts and platforms usually carry one; logic {op:\"interactables_near\"} lists them");
                if (!anim.gameObject.activeInHierarchy) throw new McpException(McpErr.Refused, World.Path(anim.gameObject) + " is inactive: Animation calls on inactive objects do nothing", "activate it first (set_active) or pick an active object");
                name = !string.IsNullOrEmpty(clip) ? clip : anim.clip != null ? anim.clip.name : null;
                if (name == null) throw new McpException(McpErr.BadArgs, "the Animation has no default clip: pass clip");
                var st = anim[name];
                if (st == null)
                {
                    var names = new List<object>();
                    foreach (AnimationState s in anim) if (s != null) names.Add(s.name);
                    var e = new McpException(McpErr.NotFound, "no clip '" + name + "' on " + anim.name, "clips: " + string.Join(", ", ToStr(names)));
                    e.data = new Json.Obj();
                    e.data["clips"] = names;
                    throw e;
                }
                if (!hasTime && !hasSpeed && !hasPlay) { r["items"] = new List<object> { AnimState(anim, null) }; r["total"] = 1.0; return; }
                r["before"] = AnimState(anim, name);
                float t0 = st.time, s0 = st.speed;
                bool p0 = anim.IsPlaying(name);
                var an = anim;
                string cn = name;
                World.Restore("logic:anim:" + anim.GetInstanceID() + ":" + name, "animation " + anim.name + "." + name, () =>
                {
                    if (an == null) return "gone";
                    var s = an[cn];
                    if (s == null) return "clip gone";
                    if (p0 && !an.IsPlaying(cn)) an.Play(cn); else if (!p0 && an.IsPlaying(cn)) an.Stop(cn);
                    s.speed = s0;
                    s.time = t0;
                    if (!p0) Sample(an, s);
                    return "speed " + PlayShared.F(s0) + ", time " + PlayShared.F(t0) + (p0 ? ", playing" : ", stopped");
                });
                if (hasPlay) { if (play) anim.Play(name); else anim.Stop(name); }
                st = anim[name];
                if (hasSpeed) st.speed = speed;
                if (hasTime)
                {
                    st.time = time;
                    if (!anim.IsPlaying(name)) Sample(anim, st);
                    if (time > st.length && st.wrapMode != WrapMode.Loop) warnings.Add("time is past the clip length " + PlayShared.F(st.length));
                }
                r["after"] = AnimState(anim, name);
                if (hasPlay && anim.IsPlaying(name) != play) sameNow = false;
                if (hasSpeed && Mathf.Abs(st.speed - speed) > 1e-4f) sameNow = false;
                if (hasTime && Mathf.Abs(st.time - time) > 1e-3f) sameNow = false;
            });
            if (!hasTime && !hasSpeed && !hasPlay)
            {
                r["note"] = "no change asked: the clips are listed";
                Finish(r, warnings);
                return ToolResult.Ok(r);
            }
            bool later = k > 0 && World.WaitVerify(ctx, k, warnings);
            bool readK = false, sameK = true;
            if (later)
                ctx.MainDo(() =>
                {
                    if (anim == null) return;
                    var st = anim[name];
                    if (st == null) return;
                    readK = true;
                    r["after_k"] = AnimState(anim, name);
                    if (hasPlay && anim.IsPlaying(name) != play) sameK = false;
                    if (hasSpeed && Mathf.Abs(st.speed - speed) > 1e-4f) sameK = false;
                });
            r["verdict"] = ReflectValues.Verdict(anim != null, sameNow, k > 0 && (hasPlay || hasSpeed), readK, sameK);
            r["note"] = "FSMs re-drive animations (PlayAnimation, SetAnimationTime): reverted_by_game means one did; time is checked only right after the write";
            Finish(r, warnings);
            return ToolResult.Ok(r);
        }

        static void Sample(Animation anim, AnimationState st)
        {
            bool en = st.enabled;
            float w = st.weight;
            st.enabled = true;
            st.weight = 1f;
            anim.Sample();
            st.enabled = en;
            st.weight = w;
        }
    }
}
