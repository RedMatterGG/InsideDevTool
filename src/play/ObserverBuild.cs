using System;
using System.Collections.Generic;
using System.Diagnostics;
using InsideDev.McpExt;
using UnityEngine;

namespace InsideDev.Play
{
    // ObserverBuild.cs (owner OBSERVE): the observation JSON (docs/MCP_PLAY_DESIGN.md section 5.2). Screen order:
    // credits, message, loading (title while the startup title fades), title, pause_menu / menu, dead, cutscene (input
    // disabled while playing, or a scripted custom animation), in_world. Ready = PlayLevels.Ready (own predicate when
    // WORLD_A is not built: playing, title gone, fader done, input enabled, body active) plus body alive and no menu.
    // Extra output fields: body.depth (full), objects[].state "in_reach", truncated in concise when the budget cut
    // something, perf {main_ms, scan_*} (full), warnings scan_pending / reflection_missing.
    public static partial class Observer
    {
        static readonly StuckCounter stuck = new StuckCounter();
        static readonly HashSet<string> stuckSeen = new HashSet<string>();
        static long eventsCursor;
        static long stuckMark;
        static string stuckCheckpoint;

        static Json.Obj Build(ObserveOptions o, bool actionResult)
        {
            var sw = Stopwatch.StartNew();
            ChangeLog.Touch();
            bool full = o.detail == "full";
            var warnings = new List<object>();
            var r = new Json.Obj();
            r["frame"] = (double)Time.frameCount;
            r["step"] = (double)LogicManager.stepCount;
            string reason;
            bool ready = ReadyNow(out reason);
            string screen = ScreenNow();
            r["screen"] = screen;
            r["ready"] = ready;
            r["reason"] = ready ? "" : reason;
            if (!ready) { string next = PlayShared.NextFor(reason, screen); if (next != null) r["next"] = next; }
            Safe(warnings, "time", () => r["time"] = TimeBlock());
            Perception.Ctl c = null;
            Json.Obj body = null;
            Safe(warnings, "body", () => { c = Perception.Controlled(); body = Perception.Body(full); if (body != null) r["body"] = body; });
            Safe(warnings, "checkpoint", () => { var cp = Checkpoint(full); if (cp != null) r["checkpoint"] = cp; });

            var lt = new Latest { epoch = ids.Epoch, frame = Time.frameCount };
            int objTotal = 0, hazTotal = 0;
            var objs = new List<object>();
            var hazs = new List<object>();
            if (c != null)
            {
                lt.hasBody = true;
                lt.bodyPos = c.pos;
                lt.bodyId = body != null ? body.Str("id", null) : null;
                float radius = Mathf.Clamp(o.radius, 1f, 60f);
                int maxObj = Mathf.Clamp(o.maxObjects, 0, 40);
                Safe(warnings, "objects", () =>
                {
                    lt.objects = Perception.ObjectsCounted(c.pos, radius, maxObj, out objTotal);
                    foreach (var x in lt.objects) objs.Add(ObjJson(x, full));
                });
                Safe(warnings, "hazards", () =>
                {
                    lt.hazards = Perception.HazardsCounted(c.pos, Mathf.Max(radius, 20f), full ? 20 : 6, out hazTotal);
                    foreach (var x in lt.hazards) hazs.Add(HazJson(x, full));
                });
            }
            latest = lt;
            r["objects"] = objs;
            r["hazards"] = hazs;

            int evCut = 0;
            var evl = new List<object>();
            Safe(warnings, "events", () =>
            {
                long upTo;
                var evs = ChangeLog.EventsForObserve(eventsCursor, actionResult ? 8 : 20, actionResult, out evCut, out upTo);
                eventsCursor = upTo;
                foreach (var e in evs) evl.Add(e);
            });
            r["events"] = evl;

            if (actionResult) Safe(warnings, "stuck", () => UpdateStuck(c, r["checkpoint"] as Json.Obj));
            var st = new Json.Obj();
            st["calls_without_progress"] = (double)stuck.Count;
            if (stuck.Deaths > 0) st["deaths_here"] = (double)stuck.Deaths;
            string hint = stuck.Hint();
            if (hint != null) st["hint"] = hint;
            r["stuck"] = st;

            Safe(warnings, "warnings", () => Warnings(warnings, c));
            r["warnings"] = warnings;

            if (full)
            {
                Safe(warnings, "input", () => r["input"] = InputBlock());
                Safe(warnings, "bodies", () =>
                {
                    var bl = new List<object>();
                    foreach (var b in Perception.Bodies()) bl.Add(BodyJson(b));
                    r["bodies"] = bl;
                });
                Safe(warnings, "menu", () => r["menu"] = MenuBlock());
                Safe(warnings, "camera", () => r["camera"] = CameraBlock());
                var tr = new Json.Obj();
                if (objTotal > objs.Count) tr["objects"] = (double)(objTotal - objs.Count);
                if (hazTotal > hazs.Count) tr["hazards"] = (double)(hazTotal - hazs.Count);
                if (evCut > 0) tr["events"] = (double)evCut;
                r["truncated"] = tr;
                Safe(warnings, "perf", () =>
                {
                    var perf = Perception.Perf();
                    perf["main_ms"] = PlayShared.R(sw.Elapsed.TotalMilliseconds, 2);
                    r["perf"] = perf;
                });
            }
            else
            {
                if (objTotal > objs.Count) JsonEdit.AddTruncated(r, "objects", objTotal - objs.Count);
                if (hazTotal > hazs.Count) JsonEdit.AddTruncated(r, "hazards", hazTotal - hazs.Count);
                if (evCut > 0) JsonEdit.AddTruncated(r, "events", evCut);
                ConciseBudget.Fit(r, ConciseBudget.MaxChars);
            }
            return r;
        }

        // one section of the observation; a game exception there becomes a warning instead of failing observe
        static void Safe(List<object> warnings, string section, Action fn)
        {
            try { fn(); }
            catch (Exception e)
            {
                if (warnings.Count < 12) warnings.Add("section_failed: " + section + ": " + e.GetType().Name + ": " + StateNames.Clip(e.Message, 120));
            }
        }

        // progress = new ground, a new checkpoint or a near-body event not seen before at this checkpoint (ChangeLog
        // NovelSince); deaths since the last update are counted per checkpoint
        static void UpdateStuck(Perception.Ctl c, Json.Obj cp)
        {
            string key = cp != null ? ((int)cp.Num("subscene", -1)).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + ((int)cp.Num("savepoint", -1)).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            if (key != null && key != stuckCheckpoint) { stuckSeen.Clear(); stuckCheckpoint = key; }
            bool changed = ChangeLog.NovelSince(stuckMark, stuckSeen) > 0;
            int deaths = ChangeLog.CountSince(stuckMark, ChangeLog.DeathTypes);
            stuck.Guide = cp != null ? cp.Str("guide", null) : null;
            if (c != null) stuck.Update(true, c.pos.x, c.pos.y, c.pos.z, changed, key, deaths);
            else stuck.Update(false, 0, 0, 0, changed, key, deaths);
            stuckMark = ChangeLog.Seq;
        }

        static Json.Obj ObjJson(ObjInfo x, bool full)
        {
            var o = new Json.Obj();
            o["id"] = x.id;
            o["kind"] = x.kind;
            o["name"] = StateNames.Clip(x.name, full ? 80 : 28);
            o["dx"] = PlayShared.R(x.dx);
            o["dy"] = PlayShared.R(x.dy);
            if (Math.Abs(x.dz) >= 0.01f) o["dz"] = PlayShared.R(x.dz);
            o["dist"] = PlayShared.R(x.dist);
            if (x.actions.Count > 0 || full) o["actions"] = PlayShared.L(x.actions);
            if (!string.IsNullOrEmpty(x.state)) o["state"] = x.state;
            if (full) o["path"] = x.path;
            return o;
        }

        static Json.Obj HazJson(HazardInfo x, bool full)
        {
            var o = new Json.Obj();
            o["id"] = x.id;
            o["kind"] = x.kind;
            o["name"] = StateNames.Clip(x.name, full ? 80 : 28);
            o["dx"] = PlayShared.R(x.dx);
            o["dy"] = PlayShared.R(x.dy);
            o["dist"] = PlayShared.R(x.dist);
            o["state"] = x.state ?? "";
            o["eta_s"] = x.etaS < 0 ? -1.0 : PlayShared.R(x.etaS, 1);
            o["threat"] = x.threat;
            if (x.safe || full) o["safe"] = x.safe;
            if (x.musicTimed || full) o["music_timed"] = x.musicTimed;
            return o;
        }

        static Json.Obj BodyJson(BodyInfo b)
        {
            var o = new Json.Obj();
            o["id"] = b.id;
            o["kind"] = b.kind;
            o["name"] = b.name;
            o["x"] = PlayShared.R(b.pos.x);
            o["y"] = PlayShared.R(b.pos.y);
            o["controlled"] = b.controlled;
            o["active"] = b.active;
            return o;
        }

        // ---------------------------------------------------------------- screen / ready
        static string ScreenNow()
        {
            try
            {
                if (CreditsShowing()) return "credits";
                if (MessageShowing()) return "message";
                if (!GameManager.GameIsStarted() || GameManager.IsLoading())
                    return TitleManager.IsShowing() && !GameManager.IsLoadingScenes() ? "title" : "loading";
                if (TitleManager.IsShowing()) return "title";
                if (MenuOpen()) return MenuIsMain() ? "pause_menu" : "menu";
                Character ch = ScriptGlobals.mainCharacter;
                if (ch == null || !ch.isCharacterActive) return "unknown";
                if (ch.isDead) return "dead";
                if (!GameInput.IsEnabled()) return "cutscene";
                var boy = ch as Boy;
                if (boy != null && boy.state is BoyCustomAnimState) return "cutscene";
                return "in_world";
            }
            catch { return "unknown"; }
        }

        static bool ReadyNow(out string reason)
        {
            try
            {
                bool known;
                string r;
                bool lv = ObserverLinks.LevelsReady(out r, out known);
                if (!known) lv = OwnReady(out r);
                if (!lv) { reason = string.IsNullOrEmpty(r) ? "not_ready" : r; return false; }
                var ch = Perception.ActiveCharacter();
                if (ch == null) { reason = "no_body"; return false; }
                if (ch.isDead) { reason = "dead"; return false; }
                if (MenuOpen() || MessageShowing()) { reason = "paused"; return false; }
                if (GameManager.IsPaused) { reason = "paused"; return false; }
                reason = "";
                return true;
            }
            catch (Exception e)
            {
                reason = "unreadable: " + e.GetType().Name;
                return false;
            }
        }

        static bool OwnReady(out string reason)
        {
            if (!GameManager.GameIsStarted() || GameManager.IsLoading()) { reason = TitleManager.IsShowing() ? "title" : "loading"; return false; }
            if (TitleManager.IsShowing()) { reason = "title"; return false; }
            var gm = PersistentBehaviour<GameManager>.instance;
            if (gm != null && gm.fader != null && !gm.fader.IsDone) { reason = "fading"; return false; }
            if (!GameInput.IsEnabled()) { reason = "input_disabled"; return false; }
            if (!ScriptGlobals.mainCharacterActive) { reason = "no_body"; return false; }
            reason = "";
            return true;
        }

        static bool CreditsShowing()
        {
            var co = SingletonBehaviour<CreditsOverlay>.instance;
            return co != null && !co.IsCompleted();
        }

        static bool MessageShowing()
        {
            if (SingletonBehaviour<MessageManager>.instance == null) return false;
            try { return MessageManager.AnyActive(); } catch { return false; }
        }

        // Menus (WORLD_A) first; else MenuManager's statics. MenuManager derives from UGUI's UIBehaviour
        // (UnityEngine.UI.dll is not referenced by the build), so only its static members are used: comparing an
        // instance with null would not compile. isActive throws while the master scene has no MenuManager yet.
        internal static bool MenuOpen()
        {
            bool known;
            bool open = ObserverLinks.MenuOpen(out known);
            if (known) return open;
            try { return MenuManager.isActive; }
            catch { return false; }
        }

        static bool MenuIsMain()
        {
            var sum = ObserverLinks.MenuSummary();
            if (sum != null)
            {
                string g = sum.Str("group", "");
                return g.Length == 0 || g.IndexOf("Main", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            try
            {
                var mm = MenuManager.MainMenu;
                return mm == null || mm.activeInHierarchy;
            }
            catch { return true; }
        }

        // ---------------------------------------------------------------- blocks
        static Json.Obj TimeBlock()
        {
            var t = new Json.Obj();
            string mode;
            bool frozen;
            if (!ObserverLinks.ClockState(out mode, out frozen)) { mode = "realtime"; frozen = Time.timeScale == 0f; }
            t["mode"] = mode;
            t["frozen"] = frozen;
            t["scale"] = PlayShared.R(Time.timeScale, 3);
            t["music_s"] = PlayShared.R(MusicPos(), 2);
            return t;
        }

        static double MusicPos()
        {
            try
            {
                var ga = PersistentBehaviour<GlobalAudio>.instance;
                if (ga == null || ga.music == null) return -1;
                return ga.music.GetMusicPosition_s();
            }
            catch { return -1; }
        }

        static Json.Obj Checkpoint(bool full)
        {
            var cp = ObserverLinks.LevelsCurrent() ?? OwnCheckpoint(full);
            if (cp == null) return null;
            if (!cp.ContainsKey("guide"))
            {
                string g = ObserverLinks.GuideTopic((int)cp.Num("subscene", -1), (int)cp.Num("savepoint", -1));
                if (!string.IsNullOrEmpty(g)) cp["guide"] = g;
            }
            return cp;
        }

        static Json.Obj OwnCheckpoint(bool full)
        {
            try
            {
                if (PersistentBehaviour<Savegame>.instance == null) return null;
                var cur = Savegame.GetCurrentSavepoint();
                var o = new Json.Obj();
                o["subscene"] = (double)cur.subscene;
                o["savepoint"] = (double)cur.savepoint;
                if (PersistentBehaviour<SavepointManager>.instance != null)
                {
                    var infos = SavepointManager.GetSubsceneInfos();
                    if (infos != null && cur.subscene >= 0 && cur.subscene < infos.Count && infos[cur.subscene] != null)
                    {
                        o["name"] = infos[cur.subscene].puzzleName ?? infos[cur.subscene].name;
                        if (full) o["area"] = infos[cur.subscene].areaName;
                    }
                }
                var sd = SingletonBehaviour<SavepointData>.instance;
                if (sd != null && sd.scenes != null)
                {
                    int ch = -1;
                    for (int i = 0; i < sd.scenes.Count; i++)
                    {
                        var s = sd.scenes[i];
                        if (s == null) continue;
                        if (s.GetSavepointIndex().CompareTo(cur) <= 0) ch = i;
                    }
                    o["chapter"] = (double)ch;
                    if (full && ch >= 0) o["chapter_name"] = sd.scenes[ch].name;
                }
                return o;
            }
            catch { return null; }
        }

        static void Warnings(List<object> w, Perception.Ctl c)
        {
            string what;
            if (Perception.MusicTimedNear(out what)) w.Add("music_timed: " + what + "; step runs are paced to real time here, slow motion desyncs it; act {when:{hazard_eta | music_time}} waits for the phase");
            bool hijacked, installed;
            ObserverLinks.InputDescribe(out hijacked, out installed);
            if (hijacked) w.Add("input_hijacked: something replaced the agent input core");
            if (installed && ObserverLinks.Controller() == "Digital") w.Add("controller_digital: the keyboard was used, swim / valve / submarine input branches differ");
            string focus = ObserverLinks.FocusState();
            if (focus != null) w.Add(focus);
            if (Perception.ScanPending) w.Add("scan_pending: some object types were not rescanned since the last load");
            if (Perception.Failures.Count > 0) w.Add("perception_failed: " + StateNames.Clip(Perception.Failures[0], 140) + (Perception.Failures.Count > 1 ? " (+" + (Perception.Failures.Count - 1) + ")" : ""));
            if (Perception.MissingMembers.Count > 0) w.Add("reflection_missing: " + Perception.MissingMembers[0] + (Perception.MissingMembers.Count > 1 ? " (+" + (Perception.MissingMembers.Count - 1) + ")" : ""));
            if (Perception.MissingTypes.Count > 0) w.Add("types_missing: " + string.Join(", ", Perception.MissingTypes.ToArray()) + " (not in this game build: not scanned)");
            if (c == null && GameManager.IsPlaying()) w.Add("no_body: no active character");
            bool camAway = false;
            try { camAway = WorldCamera.Overridden; } catch (NotImplementedException) { }
            try { camAway = camAway || CameraControl.free; } catch { }
            if (camAway && c != null) w.Add("camera_away: the camera is overridden or the free camera is on; gameplay containers (_CULL) near the body switch off when off screen, so triggers and savepoints can be missed: restore {only:[\"camera\"]} (dev: camera {op:\"release\"}) before playing");
        }

        static Json.Obj InputBlock()
        {
            bool hijacked, installed;
            var d = ObserverLinks.InputDescribe(out hijacked, out installed);
            var o = new Json.Obj();
            if (d != null) foreach (var k in d.order) if (d.ContainsKey(k)) o[k] = d[k];
            if (!o.ContainsKey("mode")) o["mode"] = installed ? "agent" : "human";
            if (!o.ContainsKey("held")) o["held"] = null;
            if (!o.ContainsKey("queued")) o["queued"] = 0.0;
            o["controller"] = ObserverLinks.Controller() ?? "unknown";
            o["hijacked"] = hijacked;
            return o;
        }

        static Json.Obj MenuBlock()
        {
            var s = ObserverLinks.MenuSummary();
            if (s != null) return s;
            var o = new Json.Obj();
            o["open"] = MenuOpen();
            return o;
        }

        static Json.Obj CameraBlock()
        {
            bool ov;
            int pin;
            ObserverLinks.CameraState(out ov, out pin);
            var o = new Json.Obj();
            o["overridden"] = ov;
            o["pinned_subscene"] = (double)pin;
            return o;
        }

        // ---------------------------------------------------------------- image
        static void AttachImage(ToolResult res, Json.Obj obs, string mode, int maxWidth, CallContext ctx)
        {
            var warn = obs["warnings"] as List<object>;
            if (warn == null) { warn = new List<object>(); obs["warnings"] = warn; }
            try
            {
                int sw = 0, sh = 0;
                var marks = ctx.Main(() => { sw = UnityEngine.Screen.width; sh = UnityEngine.Screen.height; return Marks(40); });
                var shot = McpShot.Capture(ctx, new ShotRequest { mode = mode, maxWidth = maxWidth, clean = true, marks = mode == "marked" ? marks : null });
                if (shot == null) { warn.Add("image: no screenshot"); return; }
                if (!string.IsNullOrEmpty(shot.error)) warn.Add("image: " + shot.error);
                int srcW = shot.srcWidth > 0 ? shot.srcWidth : sw, srcH = shot.srcHeight > 0 ? shot.srcHeight : sh;
                float fx = srcW > 0 && shot.width > 0 ? (float)shot.width / srcW : 1f;
                float fy = srcH > 0 && shot.height > 0 ? (float)shot.height / srcH : 1f;
                float kx = sw > 0 && srcW > 0 ? (float)srcW / sw : 1f, ky = sh > 0 && srcH > 0 ? (float)srcH / sh : 1f;
                var pos = new Dictionary<string, ShotMark>();
                foreach (var m in marks) if (m != null && m.id != null) pos[m.id] = m;
                foreach (var key in new[] { "objects", "hazards" })
                {
                    var l = obs[key] as List<object>;
                    if (l == null) continue;
                    foreach (var x in l)
                    {
                        var jo = x as Json.Obj;
                        ShotMark m;
                        if (jo == null || !pos.TryGetValue(jo.Str("id", ""), out m)) continue;
                        jo["screen"] = PlayShared.V2(m.x * kx * fx, m.y * ky * fy);
                    }
                }
                McpShot.Attach(res, shot);
            }
            catch (McpException e) { warn.Add("image: " + e.code + ": " + e.Message); }
            catch (NotImplementedException e) { warn.Add("image: " + e.Message); }
            catch (Exception e) { warn.Add("image: " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
