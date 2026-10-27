# MultiCam v2 design

> These are the design notes written during development. For usage read `docs/MULTICAM.md` (and `docs/RECORD_REPLAY.md`
> for the replay mode). Statements marked unverified or dated describe the state at that time.

Architect document for the synchronous multi-camera filming tool inside INSIDE (Unity 5.0.4f1, game Mono, mod code C# 7.3).
It is the contract for the build phase. The shared C# contract is `src/InsideDev/Runtime/MultiCamTypes.cs` (architect-owned,
compiled and binder-tested). If this document and that file disagree, the C# file wins for names and types and this
document wins for behaviour.

Paths below are relative to the repository root; `<game>` is the INSIDE folder.
Game code evidence (not part of this repository; file:line references point into a decompile of the game's own
assemblies): `FP` = decompiled `Assembly-CSharp-firstpass`, `AC` = decompiled `Assembly-CSharp`.

Contents

1. Goals and feature list
2. Hard constraints (why the design looks like this)
3. Architecture and runtime behaviour
4. JSON spec schema
5. Bridge commands
6. Output folder format
7. Compositor, QA and spec-check CLIs (Python) and the `compose` section
8. C# interface between CORE and RIG
9. File ownership
10. Build, test and game-run protocol
11. Task lists (CORE, RIG, COMPOSITOR, DOCS)
12. Unverified assumptions and how to verify them

---

## 1. Goals and feature list

One run of a scene is filmed by many virtual cameras at once, all from the same game state at every step.

| Area | Feature |
|---|---|
| Cameras | `game` (the gameplay camera, rendered or clean), `static`, `follow` (boy, huddle, any object, a group), `keys` (keyframed paths with proper easing), `rail` (constant-speed dolly/truck/crane), `orbit`, `gamerel` (alternate angle of the game shot), `gamelike` (replica of the game follow logic for any target, using the level's own camera boxes), `frame` (screen-space framing solver, crop-aware), `director` (moves between shots in-engine with the game's own orbit interpolation) |
| Targets | boy / huddle / main character bones and points, objects by strict path, `::Component`, sibling index `[i]`, rigidbody / collider / bounds points (decals excluded), game camera and aim, fixed points, groups (chicks) with filters and smoothing, velocities |
| Look | look-at targets with critically damped smoothing, look-ahead (lead), player-side clamp, roll, FOV with keep-size |
| Shake | live game handshake and impact shake copied onto any camera in its own frame, or a handheld replica sampled from the game's own noise clips on game or output time |
| Timing | output fps, playback speed segments (slow motion), sub-step motion blur with a shutter angle, integer step rates, game physics kept at 60 Hz by default, measured game time |
| Events | named triggers on signals, signal inputs, FSM state enter/exit/events, sound posts, trigger probes, impacts, boxes, conditions, field watches, boy state, visual node, death, load, game camera cuts; event-relative times (`"@baleFall+0.3"`) for cues, segments, shots, camera clocks and the take end; rehearsal pass and seeded randomness for anticipating moves |
| Quality | explicit render budget per camera visit (warmup + TAA accumulation samples with history reset), jitter pattern matched to the sample count, in-engine motion blur off during capture, `_RealTime` on the capture clock, per-camera post overrides (gentle lift, fog match, no water), near clip override, per-camera hide list |
| Robustness | session push/pop in try/finally, `mcam abort`, focus and pause safety, input and hotkey lockdown, clean frames (all editor overlays suppressed), keep-loaded subscene prepass, late-load detection, strict spec lint, target report |
| Outputs | per-camera JPEG (or raw RGBA, or an ffmpeg pipe), `meta.csv`, `poses.csv`, `events.csv`, `tracks.csv`, `rig.csv`, `run.json`, `progress.json` |
| Modes | `start` (capture), `rehearse` (no captures, all logs), `preview` (one still per camera now or at time t), `show` (put the free camera at a camera's pose), `check` (lint + target report), `probe` (authoring discovery) |
| Offline | compositor: per-angle videos, Blender-style layout (wide crop band on top, 2-3 cells below, 4 px black gaps), edited cut with dissolves / dips / long holds, speed ramps, linear-light sub-step blur, floor-anchored shadow lift, labels, H.265 only; QA: sheets, sync sheet, path plots, checks with exit codes; offline spec checker driven by the C# types |

## 2. Hard constraints (why the design looks like this)

- Cloned cameras are impossible: `CameraScript` is a `SingletonBehaviour`, a copy of `_Camera_Main` destroys itself, and the native
  post chain (`UnityEngine.D11PD*`) crashes when a camera renders into a RenderTexture (`Runtime/RenderScale.cs` header). So the
  REAL main camera visits every virtual camera while game time is frozen, and the back buffer is read at `WaitForEndOfFrame`
  (`ReadPixels`; there is no `GetRawTextureData` and no async readback in this Unity).
- The game camera is driven by (camera point, aim point, up), never by yaw/pitch (`FP/CameraScript.cs:124-128,533-536`). FOV
  never changes in the shipped game (vertical 50.987 deg, near 1.0, far 600). All designer zooms are distance changes.
- `CameraScript.MoveCamera` runs in `Update` and adds handshake and shake (`FP/CameraScript.cs:517-552`). Handshake
  `GetMovement()` uses `transform.parent.forward`, so the real camera must carry the game pose during each step frame.
- Culling (`BoundsCullerRuntime`, `SubsceneCullingManager`) runs in the Update phase from `Camera.main`. A pose written in
  LateUpdate is culled one frame late. TAA history and the bloom mip carry the previous render. Hence: write the pose at the
  end of the previous frame, plus one warmup render, plus a TAA history reset.
- The native TAA (`D11PDTemporalReprojection3`) is the live component; the managed `PDTemporalReprojection3` deletes itself in
  Awake. Every TAA control goes through the native properties (`feedbackMin`, `feedbackMax`, `frustumJitterPattern`,
  `motionBlurStrength`, ...).
- Frozen frames (`timeScale 0`) still run Update logic (PlayMaker transitions, Actor.Update, CameraScript.Update). Physics,
  FixedUpdate and animators only advance in the step frame.
- `GameManager.LateUpdate` can rewrite `Time.timeScale` (pause, focus loss, loading). It must be forced back and logged.
- `LogicManager` caches `invFixedDeltaTime`; `CameraScript` smoothing uses a hardcoded `/60` per fixed step; handshake
  averages are 60 samples. Changing `fixedDeltaTime` changes the game. Default policy: keep 60 Hz physics.
- Player-side rule: cameras only in front of the sets (z below about -2) or above them, never behind the sets and never
  looking back toward the screen (sets have no back side).
- v2 takes carry no sound: every freeze pauses Wwise (REPLAY_DESIGN 6.5). The replay mode records sound in its record pass
  (REPLAY_DESIGN 6).

## 3. Architecture and runtime behaviour

### 3.1 Components

```
 bridge / MCP "mcam ..."  ->  Remote.cs (mcam, cam, hud, clean, gamma, seed cases)            [CORE]
                                  |
                                  v
 MultiCam.cs  : spec load + lint, Runner coroutine, timing, cues, session push/pop,           [CORE]
                keep-loaded prepass, preview / rehearse / show, status, abort
 MultiCamEvents.cs  : event trigger sources -> fired times (EventTimeLookup)                   [CORE]
 MultiCamCapture.cs : readback, frame writers (jpg/raw/pipe), CSV writers                       [CORE]
 MultiCamLook.cs    : camera MonoBehaviour: per-render post overrides, _RealTime, TAA schedule   [CORE]
 CameraControl.cs   : immediate pose write, capture lock, game-step toggling                     [CORE]
                                  |   MultiCamContext (types file)
                                  v
 MultiCamTargets.cs : TargetRef resolution, points, bones, groups, velocities, per-step cache  [RIG]
 MultiCamRig.cs     : ICamRig per camera (all modes), shake layer, player-side clamp,           [RIG]
                      SampleGame (CameraScript snapshot), Validate, EvalOrder
                                  |
                                  v
 take folder (JPEG folders + CSV + JSON)  ->  tools/mcam_compose.py, tools/mcam_qa.py          [COMPOSITOR]
 specs/*.json, tools/mcam_spec_check.py, docs/MULTICAM.md, docs/MULTICAM_ru.md                 [DOCS]
```

As built: the CORE parts listed as `MultiCamEvents.cs`, `MultiCamCapture.cs` and `MultiCamLook.cs` live in `MultiCam.cs`
(classes `MultiCamEvents`, `MultiCamGrab` / `MultiCamFrameWriter` / `MultiCamCsv`, `MultiCamLook`). `EventMonitor.cs` and
`RenderHost.cs` are unchanged: signal events use CORE's own taps, and clean frames swap the editor renderer for a null renderer
(`MultiCamNullRenderer`) instead of a `RenderHost.suppress` flag.

### 3.2 Run lifecycle (CORE)

```
mcam start <spec> <out>
  1  Load: read file -> MultiCamSpec.Parse (SpecBind) -> legacy mapping (4.12) -> defaults overlay -> CORE lint
         -> MultiCamRig.Validate -> refuse on errors. Refuse when a run is active.
  2  Session push (3.6) inside try/finally; clean frames on; capture lock on.
  3  Setup commands in order (runner primitives `wait`, `waitfor` are handled here, 5.3); each result -> events.csv kind=setup.
  4  Seed (if seed >= 0): UnityEngine.Random.seed = seed; RandomGenerator.ResetAll(); PDRandom.seed = seed.
  5  Preroll (game seconds, first segment rate, no visits, no cues, events not armed).
  6  Freeze. MultiCamTargets.Begin(spec, report) (strict; errors abort). Interpolate list -> Rigidbody.interpolation.
  7  Events arm (EventMonitor.HookNow, subscriptions). Rehearsal predictions loaded (spec.rehearsal).
  8  Rigs: MultiCamRig.Create per camera (enabled or not), EvalOrder. ctx at t=0: SampleGame, Targets.Step,
     rig.Begin, one Evaluate (initial poses).
  9  Keep-loaded prepass (3.9).
 10  t0 = Time.timeDbl. Step loop (3.3) until t >= seconds or `until` reached (checked at frame boundaries) or stop.
 11  finally: flush writers and CSVs, run.json, rig.End, Targets.End, events unsubscribe, RemoveAlwaysLoaded,
     interpolation restore, session pop, status "done"/"stopped"/"failed: <error>".
```

### 3.3 Step loop and visit protocol (exact frame accounting)

Invariant: every iteration starts at the END of a frame (after its render).

```
loop:
  a  due cues: every cue with ctx.Reached(at) not yet run -> Remote.RunCommand (runner-internal, bypasses the lock).
  b  frame boundary? -> pick segment (last listed segment with Reached(from)); derive rate R, steps per frame N,
     captured per frame M (3.4). Inside a frame keep the segment.
  c  game restore: camera.transform.position/rotation = ctx.game.rendered (last step), fieldOfView = game fov,
     nearClipPlane = game near; CameraScript.paused = false (CameraControl "game step" state).
  d  Time.captureFramerate = R; Time.timeScale = 1 (also GameManager time scale); fixedDeltaTime per physics policy.
  e  yield return null                  // STEP FRAME S: FixedUpdate(s), Update, CameraScript.MoveCamera
  f  Time.timeScale = 0; CameraScript.paused = true.
  g  t = Time.timeDbl - t0; dt = t - tPrev; step++; frame/sub/captured from (b).
  h  MultiCamRig.SampleGame(cam, ctx)    // BEFORE anything moves the camera
     MultiCamTargets.Step(ctx); Events.Poll(ctx) (signals/sounds collected by callbacks during S are stamped now)
  i  for i in EvalOrder: pose[i] = rig.Evaluate(ctx)   (directors read ctx.PoseOf for cameras earlier in the order)
  j  rows: meta.csv, poses.csv (every camera, every step, flags), rig.csv, tracks.csv
  k  yield return new WaitForEndOfFrame()   // S renders the game pose (discarded)
  l  if captured and not rehearse: for each enabled camera in spec order whose pose has no copyOf:
         VISIT(camera, pose)
     then copies: camera with copyOf = X -> duplicate X's frame (unless its post/render differ: then it was visited)
  m  progress.json, periodic flush, stop/abort check
```

```
VISIT(camera, pose)        // precondition: called at the end of a frame
  apply now: transform.position/rotation = pose, fieldOfView = pose.fov, nearClipPlane = pose.near or game near;
             CameraControl stores the same pose (its LateUpdate Apply writes identical values); hide list renderers off;
             MultiCamLook.current = effective PostSpec of this camera
  for r in 0 .. W+N-1:                      // W = warmup, N = samples (effective RenderSpec of this camera)
      yield return null                     // frame F_r after Update; F_r renders with this pose
      Time.timeScale = 0 if something reset it (log 'warn' event "timeScale leak")
      TAA for F_r:  r <  W : game feedback values
                    r >= W : i = r-W+1; taa "reset": feedbackMin = feedbackMax = (i-1)/i
  yield return new WaitForEndOfFrame()      // after F_{W+N-1} rendered
  readback (3.10), hide list restored
```

- Renders per visit are exactly W+N for every camera; the culling of F_0 already sees the new pose because the pose was written
  at the end of the previous frame. The first accumulation render ignores history (feedback 0); the last one leaves the exact
  mean of N jittered renders (supersampling), so nothing of the previous camera survives.
- Default W=1, N=4 (draft); use N=8 standard, N=16 for the final wide shot. Jitter pattern by N when `render.jitter` is 0:
  N<=4 -> 6 (Rotated4), N<=8 -> 11 (Halton_2_3_X8), N<=16 -> 12 (Halton_2_3_X16, game default), else 13 (X32).
- `render.taa`: `"reset"` (default, as above), `"keep"` (game feedback on every render; legacy), `"toggle"` (fallback if the
  native TAA caches feedback: disable the component on F_W, enable on F_{W+1}; experimental).
- Uncaptured steps (closed shutter, preroll, rehearse) cost one frame each.
- Rigs are evaluated on EVERY step (captured or not) so smoothing state stays continuous.
- Copies: a director holding a source shot sets `copyOf`; CORE writes the source frame bytes again under the director's
  folder, saving W+N renders.

### 3.4 Timing model (CORE)

Segment parameters (`timing.segments[]`): `speed` s (game seconds per output second; 0.25 = 4x slow motion), `samples` M
(sub-steps averaged into one output frame, motion blur), `shutter` S degrees, optional explicit `rate`.

```
frame interval (game time)  Tf = s / fps
M == 1:   N = 1, R = fps / s
M  > 1:   N = round(M * 360 / S)  (steps per output frame; the LAST M are captured)   R = N * fps / s
explicit rate > 0: R = rate, N = M (all captured)
legacy rates [from, R, G]: R = R, N = M = G (all captured, shutter 360), s = G * fps / R
```

`meta.csv` column `speed` is this s (for an explicit rate: N * fps / R). Example: fps 30, `[0, 120, 1]` gives s = 0.25 (4x
slow motion); `[0, 120, 4]` gives s = 1 with 4 blur sub-steps.

- R must be an integer in [10, 960] (captureFramerate is an int; maximumDeltaTime is 0.1). Non-integer derived R is an error
  in `mcam check` ("choose fps/speed/samples so that R is whole"). Examples at fps 30: real time R=30; 4x slow motion R=120;
  real time with 4 blur samples at 180 deg: N=8, R=240, 4 of 8 steps captured; 4x slow with 2 samples at 180 deg: N=4, R=480.
- Physics policy `timing.physics`:
  - `"game"` (default): `Time.fixedDeltaTime` stays 1/60. Physics trajectories match normal play; render interpolation of
    the boy (Actor), CameraScript and interpolated rigidbodies fills the in-between steps. Use `interpolate: [targets]` for
    rigidbodies that are not interpolated. Lint warns when R<60 and 60 % R != 0, or R>60 and R % 60 != 0 (uneven cadence).
  - `"fine"` (experimental): fixedDeltaTime = dt / ceil(dt*60 - 1e-6); warn that trajectories, CameraScript smoothing and
    LogicManager interpolation change.
- Segment switches happen at frame boundaries only. `meta.csv` records every step with its frame/sub/captured flags.
- Game time is MEASURED: `t = Time.timeDbl - t0` after each step frame (never summed). Output time = frame / fps.
- The take ends at the first frame boundary where `t >= seconds` or `until` is reached.

### 3.5 Events, cues and time references (CORE: MultiCamEvents.cs)

- Every `events[]` entry becomes a trigger source (types in 4.5). A trigger "fires" at most once by default (`once`), on the
  `count`-th match, only after `after`, with `cooldown`. Repeats are still logged (`events.csv` kind `event+`).
- Stamp: the game time `t` of the step whose step frame or frozen frames saw it (detections during frozen frames of step k
  get t_k). Signals and sounds arrive through callbacks at any time; they are queued and stamped in step (h).
- `EventTimeLookup(name)`: fired time; else the prediction (`expect`, or the first `event` row for that name in
  `<rehearsal>/events.csv`); else NaN. When the real event fires at a different time than predicted, log
  `warn "event X fired at a, predicted b"`; references then use the real time.
- TimeRef everywhere (`at`, `from`, `start`, `after`, `until`): number = absolute game seconds; `"@name"`, `"@name+1.5"`,
  `"@name-0.5"`. `ctx.TimeOf(ref)` returns NaN while unresolved; `ctx.Reached(ref)` is false while unresolved.
- Cues run in (a) before the step frame, so their effect appears in that step. Cues are sorted by resolved time; a cue whose
  event never fires never runs (reported at the end).
- Prefer `signal <sender> <out>` cues over `call` when every receiver of the sender must run. For ChickenGun both are
  equivalent: the lever wire `LeverReturn.left -> ChickenGunVacuumLogic.startSuction` calls `ToggleSuctionOn` directly
  (`AC/ChickenGunLogic.cs:175`), and the lever's `>pullLever` FSM wire is dead (no such FSM event), so
  `call ... ToggleSuctionOn` only skips the lever animation.
- `until` and event `after` use REAL fire times only (a wrong prediction can never end a take early or open `after`); cues,
  segments and director shots use predictions until the event fires.
- Signal sources: CORE taps the SignalOuts itself (re-checked every step and on `Levels.StreamVersion` changes); `EventMonitor.cs`
  is unchanged. `impact` currently uses the velocity jump only (abs(dv) >= max(min, 0.25) and the speed drops), not
  CollisionAnalyzer.
- Setup primitive `waitfor event <name>`: events are armed for it before t=0, but types that need target sampling (`impact`,
  `cond`, `box`) cannot be waited for (targets resolve after setup).

### 3.6 Session push/pop (CORE)

Saved at start, restored in `finally` and by `mcam abort` (state kept in a static session object so abort works even if the
coroutine died): `Time.timeScale`, GameManager time scale, `Time.fixedDeltaTime`, `Time.captureFramerate`,
`Application.runInBackground`, vsync (`FramerateController.SetVSyncCount`, `automatic`), `Application.targetFrameRate`,
`CameraScript.paused`, `bHandshakeEffect`, `bCustomEffects`, camera transform / fieldOfView / nearClipPlane, native TAA
(`feedbackMin`, `feedbackMax`, `motionBlurStrength`, `frustumJitterPattern`, `enabled`), every `D11PDPostCombined` value the look
component touches (restored after each render anyway), `RenderSettings` fog distances, editor overlays (panel, hud, triggers,
solids, savepoints, labels, hidden markers, pick, gizmo global, selection, diag board) WITHOUT writing editor.cfg,
`RenderHost.suppress`, `CameraControl` capture lock, Rigidbody.interpolation of `interpolate` targets, AlwaysLoaded cullers,
`SubsceneCullingManager.CullingActive`, DevLog quiet flag. Random seed is not restored.

During the run: `GameManager.IsPaused` -> hold (no stepping) and log; `IsLoading` -> hold when `session.holdWhileLoading`;
focus loss is only marked (`mark focus`) and the unattended take continues. Capture lock (`session.lockInput`): CameraControl
ignores keyboard/mouse; DevRoot hotkeys (F-keys, `[ ] \`, Insert, zoom keys, Ctrl+P/K palette, mouse back/forward, middle-click
teleport) do nothing; external bridge/MCP commands are rejected with `busy: mcam capturing` except `mcam status|stop|abort`,
`game_state`; runner-issued setup/cue commands bypass the lock.

### 3.7 Targets (RIG: MultiCamTargets.cs)

TargetRef JSON forms: a string (base only), `[x,y,z]` (fixed point), or `{ "of": base, "point": p, "offset": [x,y,z] }`,
or `{ "group": {...} }`, or `{ "pos": [x,y,z] }`.

Bases (`of`):

| Base | Meaning | Default point | Other points |
|---|---|---|---|
| `boy` | ScriptGlobals.boy | `torso` (SpineB bone, visual) | `head`, `neck`, `root`, `pos3` (logic body, capsule base), `bounds` (bone AABB head..balls + 0.1 m), `bone:<TransformName>` (Actor.GetBone / specificBones) |
| `huddle` | ScriptGlobals.huddle | `visual` (mean of cloth bone visualTransforms) | `centre` (pos3, physics spine average), `top`, `bottom`, `bounds`, `bone:<bnDefNN name>` |
| `main` | ScriptGlobals.mainCharacter (huddle when active, else boy) | as the resolved character | as above |
| `game` | rendered game camera position (ctx.game.rendered.p) | - | - |
| `gameaim` | CameraScript.positionAim | - | - |
| `probe` | cameraBlendProbe.worldPosition (steady through climbs) | - | - |
| `#<id>` | instance id (session-local; warn) | `pivot` | object points |
| `<path>[::Component][[i]]` | strict path: exact `Inspector.PathOf`, else unique `/`-bounded suffix, else unique exact name; ambiguity or no match = error listing candidates; `[i]` = i-th same-named sibling (e.g. `.../Chickens/Chicken[3]`); `::Component` selects the component for `rb`, `collider`, `com` and group lists | `pivot` | `bounds` (active enabled Renderers, names starting `DECAL_` excluded; fallback pivot), `com` (Rigidbody.worldCenterOfMass), `rb` (Rigidbody.position, not interpolated), `collider` (Collider.bounds.center), `top`, `bottom` (of bounds), `bone:<child name>` |
| `none`, `self` | look only: no look target / the camera's own target | - | - |

- Legacy `point` on the camera applies to `target` when the TargetRef has no point.
- `offset` is world space, added after the point.
- Velocity: Rigidbody.velocity when the point is on a rigidbody, Character.velocity3 for characters, else finite difference
  over the step, smoothed over 2 steps. `TargetSample.height` = bounds height (or 1.33 for the boy torso point).
- Groups (`GroupSpec`): members from `of` (list of TargetRefs), `find` (ObjectDatabase.Search query such as
  `"t:Chicken active"`, refreshed on stream change), or `list` (`"path::Component.field"`, an IList of Components or
  GameObjects, e.g. `"_SaveLogic/ChickensLoaded::ChickenSpawner.chickens"`). `filter`: active, alive (`dead` field false when
  present), notKinematic, minSpeed, box (boxCenter/boxSize), near + maxDist. Membership hysteresis `hold` seconds. `mode`:
  `centroid`, `weighted` (speed-weighted centroid), `bounds` (union bounds centre, sets hasBounds), `nearest` (to filter.near),
  `fastest`. `smooth` seconds SmoothDamp on point and bounds extents. `max` caps members by speed. Empty group -> ok=false.
- Resolution happens once in `Begin` (strict, reported) and again only when `Levels.StreamVersion` changes or an object is
  destroyed; a changed resolution is logged as a `warn` event. Never a silent substring fallback, never a synchronous full
  ObjectDatabase scan in the step loop.
- `Sample` is cached per step (keyed by TargetRef instance); `Step(ctx)` refreshes the cache once per step.

### 3.8 Camera modes (RIG: MultiCamRig.cs)

Conventions: Unity world, z<0 is the player side; yaw/pitch/roll are Unity Euler degrees (`Quaternion.Euler(pitch, yaw,
roll)`): yaw 0 looks along +Z into the set, positive yaw turns toward +X, POSITIVE PITCH LOOKS DOWN. FOV is vertical degrees;
`fov <= 0` means the live game camera FOV. Every rig fills `CamPose.aim` (look point; `p + forward * focus` when there is none).

| Mode | Behaviour |
|---|---|
| `game` | `clean` false: ctx.game.rendered (with game handshake/shake as drawn); true: interpolatedPosition/Rotation (no shake). `offset` in the game camera's local frame, `fov` (if > 0) and `fovScale`. aim = p + forward * game aimDistance. |
| `static` | p = `pos`. Rotation: `look` target (+lookOffset) smoothed by `lookSmooth` (critically damped, seconds) else Euler(pitch, yaw, roll). |
| `follow` | point P of `target`; want = P + offset (`local`: offset rotated by the target transform yaw). `lag`: `"damp"` SmoothDamp with `smooth` seconds (0 = rigid), `"game"` the game law (below), `"none"`. `lead` seconds of look-ahead on the look point (velocity x/y, clamped 4 m). `planar`: camera z fixed at its Begin value. `look` default = target (self) + lookOffset; `"none"` = fixed Euler. `start`: hold the Begin pose (flag Waiting) until reached, then follow smoothly. |
| `keys` | keys sorted by `t` (seconds on the camera clock, origin `start`). Path `interp`: `"catmull"` (centripetal Catmull-Rom) or `"linear"`. Timing `ease`: `"ends"` (default: constant speed by arc length with trapezoid ease over the first and last 20 % of the duration; inner key times only shape the path), `"keys"` (pass each key at its time with continuous velocity: time-parametrised Hermite with Catmull-Rom tangents scaled by segment durations), `"each"` (v1: smoothstep per segment, stops at every key), `"linear"`. Orientation: key `look` targets interpolate the look point; otherwise slerp of key Euler rotations; `lookSmooth` on the result; key `fov` (0 = camera fov). Before start: first key, flag Waiting. |
| `rail` | `path` (>= 2 points) traversed at constant speed over `duration` seconds from `start` with the `"ends"` ease; look as in `static`. Truck = constant z points, crane = vertical points. |
| `orbit` | centre = `target` point + offset; camera at centre + (sin az * radius, height, -cos az * radius); az from `azimuth[0]` to `azimuth[1]` (deg, 0 = straight in front) over `duration` from `start`, `ease` ("ends" = smoothstep in time, "linear"); looks at the centre (+lookOffset) with lookSmooth. Validate warns when abs(az) > 60. |
| `gamerel` | pivot A = game clean position + forward * aimDistance (aimDistance = abs(positionAim - positionCamera)). v = p_game - A rotated by `orbitYaw` (world up) and `orbitPitch` (camera right); length = `dist` if > 0 else abs(v) * `distScale`; with `fov` > 0 and `keepSize`, length *= tan(gameFov/2) / tan(fov/2). p = A + v; looks at A with game up; `shift` [right, up] metres moves p and aim together; `roll`. Inherits every designer zoom, border and lock of the game shot. |
| `gamelike` | Replica of the game follow for any target. Anchor P = target point (`probe` for the boy is the game's own anchor); `planar` true by default here (P.z = 0, zPlayground). `box`: `"auto"` = highest-priority active CameraBlendBox containing P (enumerate `Umbrella<BlendBox>.instance.targets[0..size)` as CameraBlendBox), `"none"` = camOffset/aimOffset, or a box name/path. Box pivot via `box.UpdateCameraPivot(ref CameraPivot, P, gameProbe)` (borders and locks included). Look-ahead integrator: ext += dx * 0.166 * curve(InverseLerp(0, 2, abs(sign(dx) - ext))), curve linear (0, 0.3)->(0.7, 1.0), clamp [-1, 1]; offset = right * ext * extrapolate (box values, or `extrapolate` metres if >= 0). Smoothing `lag` default `"game"`. Box changes blend over the box `timeIn` with EaseInOut through `CameraBlendProbe.InterpolateCamera / InterpolateAim`. |
| `frame` | Screen-space solver. Subject S = target point (+ velocity * `lead`), height H = `subjectHeight` or sample height. Given yaw, pitch, roll, fov: depth d = H / (2 * size * tan(vfov/2)); screen point (u, v) from `screen` (u from left, v from top); x_n = 2u-1, y_n = 1-2v; p = S - R * (x_n * tan(hfov/2) * d, y_n * tan(vfov/2) * d, d). `crop` [x, y, w, h] (pixels of the full capture) makes u, v and size relative to that window (for the 2560x1080 band of a 1440p capture). `smooth` SmoothDamp on p. aim = S. |
| `director` | `shots` sorted by `from`; current shot = last reached. Holding: pose = source pose and `copyOf` = source (unless the director has own `post`/`render`). Blend over `blend` seconds (`clock` `"game"`, or `"output"`: blend_game = blend * speed) with `ease` (`smooth`, `smoother`, `linear`, `sine`) and `transition`: `"orbit"` (default: camera = InterpolateCamera(prev.aim, cur.aim, prev.p, cur.p, w), aim = InterpolateAim(prev.p, cur.p, prev.aim, cur.aim, w), up slerp, fov/roll lerp), `"linear"` (lerp p, slerp r), `"cut"`. Automatic hard cut when the angle between the two forwards > `cutAngle` or abs(prev.aim - cur.aim) > `cutDistance` (when > 0); flag Cut. Validate: references and cycles (RIG); holds shorter than `minHold` warn (CORE plan lint, output seconds from a shot's start to the next shot's start, predicted event times). |

Game smoothing law (`lag: "game"`): per axis, d = abs(want - cur), maxDelta = d * curve(d) * dt with the live
`CameraScript.smoothCurveX/Y/Z` (extrapolate linearly past the last key like `AnimationCurveExtension.EvaluateExtend`);
`MoveTowards`. This is the game law made time-correct (the game uses `/60` per fixed step).

Shake layer (every mode except `director`, which inherits from its sources), `shake` block:
- `hand` k: p += r * (ctx.game.handOffset * k), r = r * Slerp(identity, game.handRot, k); `pullback` false drops the z
  component (the running pull-back of up to about 1 m).
- `fx` k: the same with shakeOffset/shakeRot (forcePush, cargo lift, submarine impacts hit all angles together).
- `replica` (soft) and `hard` amplitudes: the game's handshake clips `noiseSoft` / `noiseHard` (Animation on
  `_Camera_Main/CameraHandshake`) sampled with `AnimationClip.SampleAnimation(dummy, time)` on a hidden dummy GameObject;
  time = (`clock` `"game"`: ctx.t, `"output"`: ctx.outputTime) * `rate` + `phase`. Output clock keeps real-time handheld during
  slow motion. Flag Shake.

Player-side clamp (`playerSide` true): if p.z > `zMax` and pitch < 45 (not overhead) -> p.z = zMax, flag Clamped.
Validate errors for static positions behind the line, warns for keys/rail/orbit samples behind it, and errors for any static
or key orientation whose forward has z < -0.05 while pitch < 60 (looking back toward the screen).

`SampleGame(cam, ctx)` fills `ctx.game` right after the step frame: `rendered` (transform + fov), `clean`
(interpolatedPosition/Rotation), positionCamera/Aim/Up, cameraVelocity, aimDistance, active box name
(`GetActiveCameraModifier() as CameraBlendBox`), probe worldPosition, extrapolationValue, handOffset =
Inverse(clean.r) * cameraHandshake.GetMovement(), handRot = cameraHandshake.GetRotation(), shake likewise from cameraShake,
`cut` = positionCamera jumped more than 2 m in one step.

Rig rules: Evaluate must not touch the real camera or scene state (only its own dummy objects), must handle dt = 0 (preview)
and NaN times (unfired events: hold, flag Waiting), must cost well under 0.2 ms, and must return `ok=false` + flag Fallback
only when it has no pose at all (CORE then captures the game pose and QA reports it).

### 3.9 Keep-loaded subscenes (CORE)

- `stream.keepLoaded`: `"auto"` (prepass), `"none"`, or `"list"` (cullers by name from `stream.cullers` through
  `SubsceneCullingManager.instance.GetSubsceneCullerByName`).
- Prepass: poses = the t=0 pose of every camera + `rig.Plan(ctx, poses)` (static pose, every key and midpoint, rail and orbit
  samples, director shots via sources). Per pose: planes = `GeometryUtility.CalculateFrustumPlanes(Matrix4x4.Perspective(fov,
  aspect, near, 600) * worldToCamera)` with worldToCamera = `Matrix4x4.Scale(1,1,-1) * Matrix4x4.TRS(p, r, 1).inverse`;
  `TestPlanesAABB` against each `GetAllSubsceneCullers()[i].CullingBounds` -> `AddAlwaysLoaded`. Wait (real time, up to
  `timeout`) until `AllCullersFinished()` and every added culler is loaded and active; optional `visitFrames` frozen frames
  at each pose. Log culler names to run.json.
- During the take (`addDuringTake`): each captured step tests each pose against cullers not yet loaded -> AddAlwaysLoaded and a
  `warn` event `late load <culler>` (pop-in risk in that shot).
- `freeze` true: `CullingActive = false` after the prepass (experimental). End: RemoveAlwaysLoaded for every added culler.

### 3.10 Look component and readback (CORE)

`MultiCamLook` (MonoBehaviour added LAST to the camera GameObject at session start):
- OnPreRender: when `session.realTime == "game"` set the global `_RealTime` = (T, 1000T, sin T, frac T) with
  T = start realtime + ctx.t; apply the camera's effective PostSpec on top of the values the game wrote this frame (store
  base): `gamma_gamma` = brightness (absolute, if set), `post_blacklift` += blacklift, `post_gamma` += postGamma,
  `post_inWhite` *= inWhiteScale, `saturationAdjustment` += saturation, bloom intensities *= bloomScale,
  `colorDistortionPx720p` and `barrelValue` *= distortionScale, `noWater` -> `waterDist` = 0, fog start/end += fogOffset +
  (fogMatch ? abs(pose.aim - pose.p) - game.aimDistance : 0).
- OnPostRender: restore the stored base values (no accumulation). Verify the order once (Section 12).
- Recommended gentle lift for dark scenes: brightness 0.55-0.60, blacklift 0.008-0.015 (<= 0.025), postGamma 0.03-0.08,
  inWhiteScale 0.92-0.95, saturation untouched. Offline lift (7.4) is the alternative.

Readback (MultiCamCapture.cs): format `jpg`: ReadPixels into an RGB24 texture, EncodeToJPG(output.jpeg) on the main thread,
bytes to a background writer thread (bounded queue of `output.queue` frames per camera; block when full). `raw`: RGBA32
texture, GetPixels32, copy into a pooled byte[] (GCHandle + Marshal.Copy or unsafe), writer thread writes `.rgba`
(bottom-up rows). `pipe`: one ffmpeg process per camera (`System.Diagnostics.Process`, stdin rawvideo rgba, stderr drained by a
thread, `-vf vflip -an -c:v <pipeCodec> -qp <pipeQp> -tag:v hvc1 <cam>.mkv`), killed and reported on abort; refuse more
pipes than NVENC sessions allow (8). No BCL compression (no MonoPosixHelper).

### 3.11 Preview, rehearse, show

- `mcam preview <spec> <out> [t]`: without t: the current frozen state; no setup, cues or seed; Begin + one Evaluate (dt 0)
  per camera; each camera visited with its render budget; standard folder with one step (`run.json` mode preview). With t:
  run like rehearse (setup, seed, cues, events) until t, then capture one step. Contact sheet: `mcam_qa.py sheet <out>`.
- `mcam rehearse <spec> <out>`: full timing and events, no visits; writes meta/poses/events/tracks/rig/run.json. Use its
  events.csv as `rehearsal` for negative offsets and its poses.csv for path QA before spending capture time.
- `mcam show <camera>`: evaluate one camera of the last loaded spec now and leave the free camera at that pose.

### 3.12 Status and logs

`progress.json` (every step): state, mode, step, frame, game_time, seconds, segment, camera, s_per_step, eta_s, queue depth,
phase ms (step frame, visits, readback, encode, queue wait), vsyncTimeWait if available, GC count, errors.
`mcam status` prints a one-line summary; `mcam status json` prints progress.json. SetFree logging is silenced during capture
(`session.quietLog`); runner lines go to `<out>/log.txt` and DevLog.

---

## 4. JSON spec schema

Rules: field name = JSON key (case-sensitive); unknown keys are warnings with a suggestion; keys starting with `_` and the key
`comment` are ignored anywhere; `null` means "not given". Types: `number`, `int` (whole number), `bool`, `string`, `[x,y,z]`,
`[a,b]`, `number[]`, `time` (TimeRef), `target` (TargetRef), lists `T[]`. A list of named objects (`cameras`, `events`) may
also be written as an object map `{ "name": {...} }`. A list of strings or targets may be a single value. Game time is in
seconds from t=0 (after preroll). `mcam schema` prints the live schema from the C# types.

Nested blocks are REPLACED by a camera-level block, not deep-merged, except where CORE merges explicitly:
effective render = Overlay(Overlay(root.render, defaults.render), camera.render); the same for `post`; shake =
Overlay(defaults.shake, camera.shake); camera = Overlay(defaults, camera) for all other fields (`SpecBind.Overlay` copies the
fields present in the upper node).

### 4.1 Root (`MultiCamSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| version | int | 2 | Spec version. A spec without `version` is treated as v1 for the legacy defaults (4.12). |
| name | string | "" | Take name (folder default for the compositor). |
| seconds | number | 10 | Hard cap of the take in game seconds. |
| until | time | null | End when reached (e.g. `"@baleLand+3"`); `seconds` still caps. |
| preroll | number | 0 | Game seconds simulated after setup and before t=0 (no captures, no cues, events not armed). |
| seed | int | -1 | >= 0: UnityEngine.Random.seed, RandomGenerator.ResetAll, PDRandom.seed after setup. |
| rehearsal | string | "" | Folder of a rehearsal take; its events.csv predicts event times for negative offsets. |
| setup | string[] | [] | Bridge commands run before t=0, plus runner primitives `wait <real s>`, `waitfor loaded`, `waitfor streaming`, `waitfor savepoint <name>`, `waitfor event <name>`. |
| cues | CueSpec[] | [] | Timed commands (4.4). |
| events | EventSpec[] | [] | Named triggers (4.5). |
| timing | TimingSpec | {} | Output fps, segments, physics policy (4.2). |
| rates | RateSpec[] | [] | v1 rate segments `[from, stepsPerGameSecond, stepsPerOutputFrame]`; mapped to segments with explicit rate (4.12). |
| settle | int | -1 | v1: settle frames; >= 0 maps to render warmup=settle, samples=1, taa "keep". |
| jpeg | int | -1 | v1: JPEG quality; >= 0 maps to output.jpeg. |
| render | RenderSpec | {} | Render budget per camera visit (4.6). |
| output | OutputSpec | {} | Frame format and logs (4.7). |
| session | SessionSpec | {} | Capture session settings (4.8). |
| post | PostSpec | {} | In-engine look overrides for all cameras (4.9). |
| stream | StreamSpec | {} | Keep-loaded subscenes (4.10). |
| interpolate | target[] | [] | Rigidbodies set to Interpolate during capture (e.g. the hay bale if needed). |
| tracks | target[] | [] | Extra targets logged to tracks.csv (camera targets are always logged). |
| defaults | CamSpec | null | Fields applied to every camera unless the camera sets them. |
| cameras | CamSpec[] | [] | Cameras (4.11). Order = visit order and folder list. |
| compose | object | null | Compositor section (7.3); copied verbatim, ignored by the game. |

### 4.2 `timing` (`TimingSpec`) and `timing.segments[]` (`SegmentSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| timing.fps | number | 30 | Output frames per second. |
| timing.physics | string | "game" | `"game"` keeps fixedDeltaTime 1/60; `"fine"` whole substeps per step (experimental). |
| timing.segments | SegmentSpec[] | [] | Empty = one segment {from 0, speed 1, samples 1}. Listed in time order. |
| from | time | 0 | Segment start (switch at the next frame boundary after it is reached). |
| speed | number | 1 | Game seconds per output second (0.25 = 4x slow motion). |
| samples | int | 1 | Sub-steps averaged into one output frame (motion blur). |
| shutter | number | 180 | Shutter angle in degrees when samples > 1 (144 matches the game's own blur). |
| rate | int | 0 | > 0: explicit steps per game second; then all `samples` steps of a frame are captured. |
| label | string | "" | Name in meta.csv / timeline plots. |

### 4.3 `rates[]` (`RateSpec`, legacy)

`[from, rate, group]` or `{from, rate, group}`: from (time, 0), rate (number, 30, must be whole), group (int, 1; `null` or
omitted = 1).

### 4.4 `cues[]` (`CueSpec`)

`[time, "command", "command2", ...]` or `{at, cmd, cmds, label}`: at (time, 0), cmd (string, ""), cmds (string[], []),
label (string, ""). Commands go through `Remote.RunCommand` as runner-internal commands.

### 4.5 `events[]` (`EventSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| name | string | "" | Event id used in `"@name"` (letters, digits, `_`, `.`). Required, unique. |
| type | string | "" | One of the types below. |
| obj | string | "" | Object path for signal, signalIn, fsm, sound filter, probe, field. |
| comp | string | "" | Component type name (field watch). |
| field | string | "" | Field or property name (field watch); suffix `.Count` for list sizes. |
| signal | string | "" | SignalOut name (signal) or SignalIn name (signalIn). |
| fsm | string | "" | FSM name filter (fsm). |
| state | string | "" | FSM state name (fsm enter/exit). |
| fsmEvent | string | "" | FSM event name (fsm, edge "event"). |
| sound | string | "" | Sound event name (sound). |
| edge | string | "enter" | `enter` / `exit` (fsm, probe, box, boystate, visual), `event` (fsm). |
| target | target | null | Subject for impact, box, cond. |
| to | target | null | Second target for cond prop `dist`. |
| prop | string | "" | cond: `x`, `y`, `z`, `vx`, `vy`, `vz`, `speed`, `dist`. |
| op | string | "" | `<`, `<=`, `>`, `>=`, `==`, `!=`. |
| value | number | 0 | Threshold (cond, field numeric; time for type `time`). |
| match | string | "" | String compare (field enum names like `Suction`, boystate class name, visual node class name, camcut `box`). |
| center | [x,y,z] | [0,0,0] | Box centre (box). |
| size | [x,y,z] | [0,0,0] | Box size (box). |
| min | number | 0 | impact: minimum normal impact speed; camcut: jump metres (0 = 2). |
| once | bool | true | Record only the first fire as the event time (repeats logged). |
| count | int | 1 | Fire on the n-th match. |
| after | time | null | Arm only after this time. |
| cooldown | number | 0 | Minimum game seconds between matches. |
| expect | number | unset | Predicted game time for negative offsets before it fires. |
| log | bool | true | Write fires to events.csv. |

Types: `time` (t >= value), `signal` (EventMonitor.Fired with sender == obj and debugName == signal), `signalIn` (a fired
connection whose receiver == obj and signalInName == signal; FSM inputs carry the `>` prefix), `fsm` (FsmLog entry diff on the
FSMs of obj, `Fsm.MyLog.Entries` from a remembered index, reset on `Resized`; fallback ActiveStateName polling every frame
including frozen frames), `sound` (SoundEngine.eventPosted name, optional obj filter incl. children), `probe`
(IsTriggeredByProbe on obj: probeEnter/probeExit `.Event`), `impact` (CollisionAnalyzer aggregator impact event on target with
impact >= min; fallback velocity jump), `box` (target point enters/exits the box), `cond` (false->true transition of
`prop op value`), `field` (reflection watch, false->true transition), `boystate` (ScriptGlobals.boy.state class name),
`visual` (VisualBoy.PostStateChangeEvent node class name), `death` (DeathManager.died), `loaded` (GameManager.loadFinished),
`camcut` (ctx.game.cut, or the active box name changed when match = "box"). All C# event subscriptions are removed at the end
of the run and on unload.

### 4.6 `render` (`RenderSpec`; root, `defaults.render`, camera `render`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| warmup | int | 1 | Renders at the new pose before accumulation (>= 1; 0 is an error). |
| samples | int | 4 | TAA accumulation renders (4 draft, 8 standard, 16 final wide). |
| taa | string | "reset" | `reset` (feedback (i-1)/i), `keep` (game feedback), `toggle` (experimental fallback). |
| jitter | int | 0 | Jitter pattern id 1..15 (`FrustumJitterPattern`; 0 is Still in the game enum, so it cannot be chosen); 0 = auto from samples (6 / 11 / 12 / 13). |
| motionBlur | number | 0 | TAA motionBlurStrength during capture: 0 = off (use sub-step blur); a negative value keeps the game value (0.8). |

Different budgets between cameras are allowed (per-camera settle) but warned: frozen frames run Update logic per render.

### 4.7 `output` (`OutputSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| format | string | "jpg" | `jpg`, `raw` (`.rgba` bottom-up), `pipe` (ffmpeg per camera -> `<cam>.mkv`). |
| jpeg | int | 95 | JPEG quality. |
| queue | int | 3 | Writer queue depth per camera (backpressure). |
| poses | bool | true | Write poses.csv. |
| rig | bool | true | Write rig.csv. |
| tracks | bool | true | Write tracks.csv. |
| flushEvery | int | 30 | Steps between CSV flushes. |
| ffmpeg | string | "ffmpeg" | ffmpeg for pipe mode (default: `ffmpeg` on PATH). |
| pipeCodec | string | "hevc_nvenc" | Pipe codec (H.265 only). |
| pipeQp | int | 14 | Constant QP for the pipe. |
| readback | string | "auto" | `auto`: the native async readback plugin (`bin/InsideReadback.dll`) when it loads, else ReadPixels; `native`: the plugin is required; `readpixels`: synchronous ReadPixels on the game thread. Used by `pipe` output, `record.captureGame` and `mcam golden` (added with the replay mode). |

### 4.8 `session` (`SessionSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| clean | bool | true | Suppress all editor overlays (null editor renderer + overlay toggles, no editor.cfg writes). |
| vsync | bool | false | false = vsync off and targetFrameRate -1 during capture. |
| runInBackground | bool | true | Keep running without focus. |
| lockInput | bool | true | Capture lock (3.6). |
| handshake | bool | true | CameraScript.bHandshakeEffect during the take (the game camera's own handheld). |
| shake | bool | true | CameraScript.bCustomEffects during the take (impact shakes). |
| res | [a,b] | [0,0] | Resolution to apply and verify before step 0; [0,0] keeps the current one. |
| realTime | string | "game" | `game`: `_RealTime` follows the capture clock; `wall`: untouched. |
| holdWhileLoading | bool | true | Hold stepping while the game is loading. |
| quietLog | bool | true | Silence per-step DevLog lines (SetFree ON/OFF). |
| gcSteps | int | 0 | > 0: GC.Collect every n steps (long raw takes). |

### 4.9 `post` (`PostSpec`; root, `defaults.post`, camera `post`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| brightness | number | unset | Absolute `gamma_gamma` (0..1, game 0.5) per render. |
| blacklift | number | 0 | Added to `post_blacklift` (keep <= 0.025 total). |
| postGamma | number | 0 | Added to `post_gamma`. |
| inWhiteScale | number | 1 | Multiplies `post_inWhite` (< 1 brightens). |
| saturation | number | 0 | Added to `saturationAdjustment`. |
| bloomScale | number | 1 | Multiplies bloom intensities. |
| distortionScale | number | 1 | Multiplies chromatic and barrel distortion. |
| fogMatch | bool | false | Shift fog start/end by (camera-to-aim distance - game aim distance). |
| fogOffset | number | 0 | Extra metres added to fog start/end. |
| noWater | bool | false | Force waterDist 0 (front cameras whose XY lies in a water volume). |

### 4.10 `stream` (`StreamSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| keepLoaded | string | "auto" | `auto`, `none`, `list`. |
| cullers | string[] | [] | Culler names for `list` (also added in `auto`). |
| prepass | bool | true | Run the frustum prepass before t=0. |
| visitFrames | int | 0 | Frozen frames rendered at each planned pose during the prepass. |
| timeout | number | 20 | Real seconds to wait for loading. |
| freeze | bool | false | CullingActive = false during the take (experimental). |
| addDuringTake | bool | true | Late-load detection and AddAlwaysLoaded during the take. |

### 4.11 `cameras[]` (`CamSpec`)

Common keys:

| Key | Type | Default | Meaning |
|---|---|---|---|
| name | string | "" | Required, unique, folder name (`[A-Za-z0-9_-]`). |
| mode | string | "static" | One of 3.8. |
| label | string | "" | Caption for the compositor. |
| enabled | bool | true | false = evaluated (directors may use it) but never captured. |
| fov | number | 0 | Vertical FOV degrees; 0 = live game FOV (50.99). |
| near | number | 0 | Near clip; 0 = game 1.0; >= 0.3 for close-ups. |
| focus | number | 10 | Aim distance when there is no look target. |
| pos | [x,y,z] | [0,0,0] | Position (static). |
| yaw / pitch / roll | number | 0 | Unity Euler degrees; positive pitch looks down. Roll is applied on top of look-at too. |
| target | target | null | Subject (follow, orbit centre, gamelike, frame). |
| look | target | null | Look-at target; `"none"` fixed Euler; `"self"` = target. |
| point | string | "" | Legacy point for `target` (`pivot`, `bounds`, ...). |
| offset | [x,y,z] | [0,0,0] | follow: camera offset from the target point; game: offset in the game camera frame; orbit: centre offset. |
| lookOffset | [x,y,z] | [0,0,0] | Added to the look point. |
| local | bool | false | follow: offset rotated by the target yaw. |
| smooth | number | 0 | Position SmoothDamp seconds (follow, frame). |
| lookSmooth | number | 0 | Look point SmoothDamp seconds. |
| fovScale | number | 1 | game: FOV multiplier. |
| lag | string | "damp" | `damp`, `game`, `none` (gamelike defaults to `game`). |
| lead | number | 0 | Look-ahead seconds (follow, frame). |
| planar | bool | false | Keep camera z fixed (gamelike defaults to true). |
| start | time | null | Camera clock origin (keys, rail, orbit) / follow start. |
| playerSide | bool | true | Clamp and lint the player-side rule. |
| zMax | number | -2 | Player-side limit. |
| render | RenderSpec | null | Per-camera render budget (per-camera settle). |
| post | PostSpec | null | Per-camera look overrides. |
| shake | ShakeSpec | null | Shake layer. |
| hide | string[] | [] | Object paths hidden only in this camera's renders: v2 visits disable the renderers; replay passes and `mcam show` move them to a spare layer the camera does not draw (MULTICAM.md 6.4). |
| motion | MotionSpec | null | Camera motion limits on the output clock (below); directors and events cameras use the defaults when not given. |

Mode keys:

| Key | Type | Default | Modes | Meaning |
|---|---|---|---|---|
| keys | KeySpec[] | [] | keys | Key list (below). |
| interp | string | "catmull" | keys, rail | `catmull` or `linear`. |
| ease | string | "ends" | keys, rail, orbit | 3.8. |
| duration | number | 0 | rail, orbit | Seconds on the camera clock. |
| path | [x,y,z][] | [] | rail | Rail points. |
| radius | number | 8 | orbit | Metres. |
| height | number | 1.5 | orbit | Metres above the centre. |
| azimuth | [a,b] | [0,0] | orbit | From/to degrees (0 = in front). |
| orbitYaw / orbitPitch | number | 0 | gamerel | Degrees around the game aim point. |
| dist | number | 0 | gamerel | Metres from the pivot (0 = game distance * distScale). |
| distScale | number | 1 | gamerel | Distance multiplier. |
| shift | [a,b] | [0,0] | gamerel | Right/up metres. |
| keepSize | bool | true | gamerel | Distance compensation for a different FOV. |
| clean | bool | false | game, gamerel | Use the unshaken game pose. |
| box | string | "auto" | gamelike | `auto`, `none`, box name/path. |
| camOffset | [x,y,z] | [0,2,-11] | gamelike | Offset without a box (game probe default). |
| aimOffset | [x,y,z] | [0,2,0] | gamelike | Aim offset without a box. |
| extrapolate | number | -1 | gamelike | Look-ahead metres; -1 = from the box/probe. |
| screen | [a,b] | [0.48,0.6] | frame, static, follow, keys, rail, orbit | Subject screen point (u from left, v from top); look-at modes use it only when given (a `defaults.screen` counts as given). |
| size | number | 0.12 | frame | Subject height as a fraction of frame (or crop) height. |
| subjectHeight | number | 0 | frame | Metres; 0 = from bounds / 1.33 for the boy. |
| crop | number[] | null | frame | [x,y,w,h] pixels of the capture the framing refers to. |
| shots | ShotSpec[] | [] | director | Shot list. |
| cutAngle | number | 90 | director | Auto hard cut above this angle. |
| cutDistance | number | 0 | director | Auto hard cut above this aim distance (0 = off). |
| minHold | number | 4 | director | Lint: minimum hold seconds. |
| pans | PanSpec[] | [] | overview | Pan segments: from `from` the frame centre x moves to `to` over `duration` (ease) and holds there (the ending pan back over the route). |
| beats | BeatSpec[] | [] | events | Event beats: on `at` the camera moves to `target` and holds it (below). |
| offsets | [x,y,z][] | [] | events | Candidate camera offsets from the subject, tried in order: the first with a clear view (MultiCamView) wins, else the least bad; [] = `offset`. |
| hold | number | 2 | events | Seconds a beat holds its target before the next beat may start. |
| move | number | 1 | events | Seconds of the smooth move from the previous beat's pose (ease `ease`; `ends` = smooth). |
| late | number | 2.5 | events | A beat that would start more than this many seconds after its event (queued behind earlier holds) is dropped. |
| clamp | ClampSpec | null | any | Frame-edge limits: world x of the left / right frame edge at the subject depth; the pose trucks in x to stay inside (flag C). |
| view | ViewSpec | null | any | Line-of-sight and near-plane check settings (events candidates, replay views.csv, mcam views / show); null = defaults. |

`keys[]` (`KeySpec`): t (number, 0, camera clock seconds), pos ([x,y,z]), yaw / pitch / roll (number, 0), fov (number, 0 =
camera fov), look (target, null), lookOffset ([x,y,z]).

`shots[]` (`ShotSpec`): cam (string, required), from (time, 0), blend (number, 0), ease (string, "smooth"),
transition (string, "orbit"), clock (string, "game"), label (string, "").

`shots[]` also take hide (string[], [], hidden while the shot is on air) and motionBlur (number, 1, replay blur scale).

`shake` (`ShakeSpec`): hand (0), fx (0), pullback (false), replica (0), hard (0), clock ("game"), rate (1), phase (0).

`motion` (`MotionSpec`):

| Key | Type | Default | Meaning |
|---|---|---|---|
| maxSpeed | number | 6 | Peak camera speed in metres per output second (0 = no limit). |
| maxTurn | number | 35 | Peak turn rate in degrees per output second (0 = no limit). |
| ease | number | 0.6 | Seconds to speed up and to slow down: a move lasts at least 2 x ease; the limiter's acceleration is maxSpeed / ease. |
| blurScale | number | 1 | Scale of this camera's replay pass motion blur (times the shot's `motionBlur` for directors). |

`clamp` (`ClampSpec`):

| Key | Type | Default | Meaning |
|---|---|---|---|
| left | number | 0 | World x the frame's left edge may not pass (only when given). |
| right | number | 0 | World x the frame's right edge may not pass (only when given), e.g. the CEO window glass 3524.55. |
| z | number | 0 | When given: measure the edges where the edge rays meet the world plane z = this; else at the aim point's depth. |
| aspect | number | 0 | Frame aspect for the edges (0 = the render's; give the cell's aspect for a centre-cropped layout cell). |
| corners | bool | true | Count the frame corners too, not only the mid-edge rays (the outermost x wins). |

`pans` (`PanSpec`):

| Key | Type | Default | Meaning |
|---|---|---|---|
| from | time | 0 | Pan start (record seconds or "@event+s"). |
| duration | number | 4 | Seconds of the move. |
| to | number | 0 | World x of the frame centre at the end (clamped by `clamp`). |
| ease | string | "smooth" | `smooth`, `smoother`, `linear`, `sine`. |

`beats` (`BeatSpec`):

| Key | Type | Default | Meaning |
|---|---|---|---|
| at | time | 0 | Beat time ("@event+s" or seconds); an event that never fired drops the beat. |
| target | target | null | Subject (null = the camera's target). |
| point | string | "" | Point on the target (bone:Head, ...). |
| hold | number | 0 | Hold seconds (0 = the camera's). |
| move | number | 0 | Move seconds (0 = the camera's). |
| late | number | -1 | Drop limit seconds (< 0 = the camera's). |
| fov | number | 0 | FOV of this beat (0 = the camera's). |
| offset | [x,y,z] | [0,0,0] | Camera offset from the subject (when given). |
| offsets | [x,y,z][] | [] | Candidate offsets for this beat (first clear view wins). |
| lookOffset | [x,y,z] | [0,0,0] | Look point offset from the subject (when given; else the camera's). |
| label | string | "" | Name in the log and in mcam views. |

`view` (`ViewSpec`):

| Key | Type | Default | Meaning |
|---|---|---|---|
| avoid | target[] | [] | Targets whose bounds the camera -> subject ray must miss (point bounds for props without colliders). |
| huddle | bool | true | Also keep the ray off the huddle cloth bounds (unless the subject is inside them). |
| layers | int[] | [] | Collider layers that block ([] = all but 2 Ignore Raycast); triggers never block. |
| ignore | string[] | [] | Colliders whose path contains one of these never block (invisible helper colliders). |
| margin | number | 0.25 | Metres the avoid / huddle bounds grow by. |
| subjectRadius | number | 0.3 | The ray stops this far short of the subject point. |
| near | bool | true | Test the camera point and the near-plane corners for inside / behind geometry. |
| every | number | 0.5 | Record seconds between checks in replay plan passes (views.csv; 0 = off). |

Fields that a mode ignores are warned by `MultiCamRig.Validate` (mode -> keys table above; `MultiCamRig.ModeKeys` and
`MODE_KEYS` in `tools/mcam_spec_check.py` are the same table). Camera-level `look`, `target` and `point` also apply to
`static`, `keys`, `rail` and `orbit`.

### 4.12 Legacy (v1) mapping, applied by CORE after binding

A spec is v1 when it has no `version` key or `version` < 2 (CORE, RIG and the checker use the same test).

- v1: cameras without `fov` get 40, except `game` (v1 used the game fov x fovScale; the rig ignores `fov` there) and
  `director`; `settle` default 3; `jpeg` default 92 unless `jpeg` or `output.jpeg` is given.
- `rates` non-empty and `timing.segments` empty: one segment per rate {from, rate: R, samples: G, shutter 360}; `timing.fps`
  defaults to R0/G0 when not given. Non-whole R is an error.
- `settle` >= 0: root render = {warmup: max(1, settle), samples: 1, taa: "keep"} unless `render` is given (`settle 0` warns).
- `jpeg` >= 0: output.jpeg.
- Camera `point` -> target.point when the TargetRef has none (CORE replaces the camera's TargetRef before targets resolve, so
  the mapped ref is the one tracked). `look: "self"` / `"none"` keep their v1 meaning.
- v1 points (CORE): `boy` / `huddle` / `main` targets and looks without a point use `pos3` (the v1 anchor); object looks
  without a point use `bounds`.
- v1 rig defaults (RIG, only when the key is not given): `keys` ease `"each"`, director transition `"linear"`.
- v1 `follow` with `look` absent looks at the target point + lookOffset (unchanged).

### 4.13 Lint (`mcam check`, offline subset in `mcam_spec_check.py`)

Errors: unknown mode, missing/duplicate/invalid camera names, director unknown camera or cycle, unknown event in a `"@"` time,
duplicate event name, unsorted keys, segment rate not whole (tolerance 1e-3) or outside [10, 960], warmup < 1, samples < 1,
jitter outside 0..15, two director shots at the same time, `mcam` commands in setup or cues, cues that move or resize the
capture camera (`cam`, `freecam`, `zoom`, `res`, `fullscreen`), static position behind the player-side line, looking back toward
the screen, unresolved or ambiguous target (in-game), more pipes than NVENC sessions. Warnings: unknown keys, keys ignored by
the mode, fov > 60 (note above 51), abs(yaw) > 40, pitch < -20 or > 35, distance to the look point < 1.5 m with near not set,
physics cadence (3.4), unequal render budgets, director holds below `minHold` (output seconds from a shot's start to the next
shot's start, using predicted event times and segment speeds; the last shot runs to the predicted take end), orbit
abs(azimuth) > 60, cues after `seconds`, events never referenced, `settle 0`, estimated cost and disk size (steps x cameras x
(W+N) renders, about 25-40 ms per render at 1440p with vsync off; JPEG about 1 MB, raw 14.7 MB per frame). A director holding
a disabled camera, or a camera with a different render/post/hide list, is rendered (visited) instead of copied.

### 4.14 Example (ChickenGun)

```json
{
  "version": 2,
  "name": "chickengun_bale",
  "seconds": 14,
  "until": "@baleLand+3",
  "seed": 1,
  "setup": [
    "audio force rtpc rtpc_master_volume 0", "audio native on",
    "god on", "panel off", "triggers off", "labels off", "savepoints off",
    "load 7 0", "waitfor savepoint #chickenGun", "waitfor streaming", "wait 3",
    "tp 221.0 -221.4 0.66",
    "call ChickenGunObject/ChickenGunVacuumLogic ChickenGunLogic OnElectricityOn",
    "call _SaveLogic/ChickensLoaded ChickenSpawner DoSpawn"
  ],
  "cues": [[1.0, "call ChickenGunObject/ChickenGunVacuumLogic ChickenGunLogic ToggleSuctionOn"]],
  "events": {
    "baleFall": {"type": "signal", "obj": "HayBaleHolder/HayBaleTrigger", "signal": "enterSignal", "expect": 6.0},
    "baleLand": {"type": "impact", "target": "HayBaleHolder/HayBale", "min": 1.0, "after": "@baleFall", "expect": 6.5}
  },
  "timing": {"fps": 30, "segments": [
    {"from": 0, "speed": 1, "samples": 2, "shutter": 180},
    {"from": "@baleFall-1.0", "speed": 0.25, "samples": 2, "shutter": 180, "label": "slowmo"},
    {"from": "@baleLand+1.0", "speed": 1, "samples": 2, "shutter": 180}
  ]},
  "render": {"warmup": 1, "samples": 8},
  "post": {"brightness": 0.57, "blacklift": 0.01},
  "cameras": [
    {"name": "game", "mode": "game", "render": {"warmup": 1, "samples": 16}},
    {"name": "low_left", "mode": "static", "pos": [215.5, -220.3, -6.5], "fov": 50, "look": {"of": "HayBaleHolder/HayBale", "point": "bounds"}, "lookSmooth": 0.4},
    {"name": "outlet_bale", "mode": "static", "pos": [225, -219, -6], "yaw": 20, "pitch": -18, "fov": 45},
    {"name": "high", "mode": "static", "pos": [222, -213, -11], "yaw": 10, "pitch": 25, "fov": 50},
    {"name": "bale_follow", "mode": "follow", "target": {"of": "HayBaleHolder/HayBale", "point": "bounds"}, "offset": [-1.5, 0.5, -7], "smooth": 0.6, "lookSmooth": 0.3, "start": "@baleFall-0.3", "shake": {"replica": 0.6, "clock": "output"}},
    {"name": "chicks", "mode": "frame", "enabled": false, "target": {"group": {"list": "_SaveLogic/ChickensLoaded::ChickenSpawner.chickens", "mode": "bounds", "filter": {"minSpeed": 1.5}}}, "yaw": 12, "pitch": 5, "fov": 45, "size": 0.35, "smooth": 0.5},
    {"name": "director", "mode": "director", "shots": [
      {"cam": "game", "from": 0},
      {"cam": "low_left", "from": 4.0, "blend": 1.5, "clock": "output"},
      {"cam": "bale_follow", "from": "@baleFall-0.2", "blend": 1.5, "clock": "output"},
      {"cam": "high", "from": "@baleLand+0.5", "blend": 2.0, "clock": "output"}
    ]}
  ],
  "compose": {
    "layouts": [{"name": "main", "width": 2560, "gap": 4, "rows": [
      [{"cam": "game", "crop": [0, 180, 2560, 1080]}],
      [{"cam": "low_left"}, {"cam": "outlet_bale"}, {"cam": "high"}]
    ]}],
    "edit": {"name": "cut1", "fadeIn": 0.5, "fadeOut": 1.0, "shots": [
      {"cam": "game", "from": 0},
      {"cam": "low_left", "from": 4.0, "in": {"type": "dissolve", "dur": 1.0}},
      {"cam": "bale_follow", "from": "@baleFall-0.2", "in": {"type": "cut"}},
      {"cam": "high", "from": "@baleLand+0.5", "in": {"type": "dissolve", "dur": 1.2}}
    ]}
  }
}
```

The first setup line (`audio force rtpc rtpc_master_volume 0`) mutes the game for the take; it is optional, since v2 takes
carry no sound (Section 2).

---

## 5. Bridge commands

All new features are bridge commands in InsideDev.dll, so the MCP `command` tool reaches them without rebuilding
InsideDev.Mcp.dll.

### 5.1 `mcam`

| Command | Result |
|---|---|
| `mcam check <spec> [json]` | Load + lint + in-game target report (`target -> path #id active pos`), segment table (R, N, M, speed), event list, cost estimate. Allowed during a run (loads into a separate spec object; never touches the running one). |
| `mcam start <spec> <out>` | Capture take. Refused while running. |
| `mcam rehearse <spec> <out>` | Timing, events and logs without visits. |
| `mcam preview <spec> <out> [t]` | One still per camera now, or at game time t of the take. |
| `mcam stop` | Finish the current step, flush, restore. |
| `mcam abort` | Restore the session immediately, even if the runner died; writers closed, ffmpeg pipes killed. |
| `mcam status [json]` | One line, or progress.json. |
| `mcam show <camera>` | Free camera to that camera's current pose (last loaded spec). |
| `mcam pose <camera>` | Print that pose (x y z yaw pitch roll fov aim). |
| `mcam list` | Cameras of the last loaded spec with modes and flags. |
| `mcam probe <obj> \| <x y z r>` | Authoring: SignalOuts (sender + debugName), SignalConnectors, PlayMakerFSMs (name, states, events), IsTriggeredByProbe (probe), CollisionAnalyzer, Rigidbodies (mass, interpolation), recent sound posts with source; prints ready-to-paste `events` JSON snippets. |
| `mcam schema` | `SpecBind.Schema(typeof(MultiCamSpec))`. |

### 5.2 Camera and session helpers

| Command | Result |
|---|---|
| `cam` | Describe the camera. |
| `cam pose x y z yaw pitch [fov] [seconds] [roll]` | Eased free-camera move; roll now supported. |
| `cam off` | Back to the game camera. |
| `cam fov <deg>` / `cam roll <deg>` | Free camera FOV / roll. |
| `cam json [name]` | Current pose as a spec camera object (`{"name","mode":"static","pos","yaw","pitch","roll","fov"}`). |
| `hud on\|off` | Editor HUD bar (not persisted when called by the runner). |
| `clean on\|off` | Save and switch off every editor overlay / restore it (no editor.cfg writes). |
| `gamma <0..1>\|reset` | D11PDPostCombined.SetGamma directly (never User.Brightness); not persisted. |
| `seed <n>` | Seed as in 3.2 step 4. |

### 5.3 Runner primitives (setup only)

`wait <real seconds>`, `waitfor loaded` (GameManager not loading), `waitfor streaming` (`Levels.activeCount > 0` and
`AllCullersFinished()`), `waitfor savepoint <name>` (game_state savepoint matches), `waitfor event <name>` (a spec event
fires; time runs at timeScale 1 meanwhile). Each has a real-time timeout of 60 s (error).

---

## 6. Output folder format

```
<out>/
  spec.json        verbatim copy of the spec
  run.json         run header and summary (written at start, rewritten at end)
  progress.json    live progress (3.12)
  log.txt          runner log (setup and cue results, warnings)
  meta.csv
  poses.csv
  events.csv
  tracks.csv       (output.tracks)
  rig.csv          (output.rig)
  <cam>/<step:D5>.jpg     captured steps only (raw mode: <step:D5>.rgba); parse names as integers
  <cam>.mkv               pipe mode: one frame per captured step, in step order
```

CSV: comma separated, header line, `.` decimal, LF, fields with `,` or `"` quoted with doubled quotes. Positions in metres,
angles in degrees, times in game seconds. Flushed every `output.flushEvery` steps and at the end: readers use the common
complete range (min of meta rows and complete frames per camera).

`meta.csv`: `step,game_time,rate,group,frame,sub,captured,speed,real_time,fixed_steps,segment`
- step: 0.. every step; game_time: measured at the end of the step; rate: R; group: M (captured sub-steps per output frame);
  frame: output frame index (-1 when not part of an output frame); sub: 0..M-1 for captured steps, -1 otherwise; captured:
  1/0; speed: s; real_time: wall seconds since run start; fixed_steps: FixedUpdates seen in the step frame; segment: index.
- The first four columns keep the v1 meaning (v1 takes have only these, or no meta.csv at all).

`poses.csv`: `step,camera,x,y,z,pitch,yaw,roll,fov,qx,qy,qz,qw,aim_x,aim_y,aim_z,near,flags,copy_of`
- one row per camera per step (captured or not); Euler = Unity eulerAngles of the rotation (x pitch, y yaw, z roll); the
  quaternion is authoritative for projection; flags = PoseFlags letters (F fallback, C clamped, W waiting, B blending, X cut,
  T no target, P copy, S shake).

`events.csv`: `step,game_time,frame,kind,name,source,detail`
- kind: `event` (spec trigger first fire), `event+` (repeat), `cue` (command run, detail = result), `setup`, `segment`
  (segment change, detail = R/N/M/speed), `shot` (director shot change, name = camera), `signal`, `fsm`, `sound` (raw taps
  when EventMonitor is on), `warn`, `error`, `mark` (late load, timeScale leak, focus hold, prediction mismatch).

`tracks.csv`: `step,game_time,target,ok,x,y,z,vx,vy,vz,count,bmin_x,bmin_y,bmin_z,bmax_x,bmax_y,bmax_z`
- target = TargetRef text; bounds columns empty when the sample has none.

`rig.csv`: `step,game_time,box,cam_x,cam_y,cam_z,aim_x,aim_y,aim_z,up_x,up_y,up_z,x,y,z,qx,qy,qz,qw,fov,clean_x,clean_y,clean_z,clean_qx,clean_qy,clean_qz,clean_qw,vel_x,vel_y,vel_z,probe_x,probe_y,probe_z,extrap,hand_x,hand_y,hand_z,shake_x,shake_y,shake_z,cut`
- cam/aim/up = positionCamera/Aim/Up; x..fov = rendered; clean = interpolated; hand/shake = local offsets.

`run.json`: `{version, name, mode (capture|rehearse|preview), out, started, finished, status, screen [w,h], fps,
segments [{index, from, from_resolved, speed, samples, shutter, rate, steps_per_frame, label}], cameras [{name, mode, label,
enabled, folder, render {warmup, samples, taa, jitter}, post {...}}], targets [{ref, resolved}], events [{name, type,
fired [times], predicted}], keep_loaded [names], late_loads [names], game {fov, near, taa {feedbackMin, feedbackMax,
motionBlurStrength, frustumJitterPattern}, post {gamma_gamma, post_inBlack, post_inWhite, post_gamma, post_blacklift,
saturationAdjustment}}, raw {w, h, format "rgba", rowOrder "bottomUp"} (raw mode), steps, frames, captured_steps,
real_seconds, warnings [], errors []}`.

v1 take (the first ChickenGun take, `mcam_take1`): only `<cam>/<step:D5>.jpg`, 30 steps per second, 450 steps; readers
fall back to `--rate 30 --group 1`.

---

## 7. Compositor, QA and spec-check CLIs

All tools: Python 3.13, `python -I`, pathlib, f-strings, type hints, numpy + PIL (cv2/numba optional with numpy fallback),
`sys.path.insert(0, str(Path(__file__).resolve().parent))` for sibling modules, outputs only under `<repo>/out/<take>/`
(default; `--force` writes elsewhere), H.265 only, never audio (the replay mode later added `--audio record`,
REPLAY_DESIGN 6.3). One heavy encode at a time; refuse to start below 10 GB free commit
(GlobalMemoryStatusEx.ullAvailPageFile) unless `--force`; warn when INSIDE.exe is running.

### 7.1 `tools/mcam_compose.py`

```
python -I tools/mcam_compose.py <command> <take> [options]
  scan      validate the take, print cameras/steps/segments/frames/slow-mo, write timeline.json
  angles    one video per camera: angle_<cam>_h265.mp4            [--cams a,b] [--speed meta|realtime]
  layout    Blender-style layout: layout_<name>_h265.mp4         [--layout name|file.json]
  cut       edited cut: cut_<name>_h265.mp4                     [--edit name|file.json]
  all       angles + every layout + the cut from the compose section
  director  emit a director camera block for the capture spec from an edit   --edit e.json --out director.json
  autocut   draft edit.json from motion energy and events        [--hold 4:6] [--dissolve 1.0]
options: --compose file.json (else <take>/spec.json "compose"), --out DIR, --fps, --range t0:t1 (game s), --frames N,
         --blur linear|gamma, --shutter deg (warp mode), --grade file.json, --bits 10|8, --codec x265|nvenc, --draft
         (nvenc, half size), --crf 18, --preset slow, --threads 8, --rate R --group G (v1 fallback), --force
```

Timeline: output frames from meta.csv (`frame` groups of captured steps, equal weights = box shutter); v2-draft takes
without `frame` group consecutive steps by `group`; v1 takes by `--rate/--group`. Warp mode (edit `speed` keys): output time
integrates T_{k+1} = T_k + speed(T_k)/fps (smoothstep between keys); each frame averages captured steps overlapping
[T_k - E, T_k] with E = shutter/360 * speed/fps (nearest step when E is shorter than a step); QA flags undersampling.
Game time -> frame: first k with t_end >= T - 1e-4.

Frame engine: decode in a thread pool (cv2.imdecode, reduced decode for cells and stats), bounded prefetch queue, average in
linear light (`(v/255)^2.2`) or gamma space, uint16 internal, grade LUT (7.4), labels, compose, rawvideo rgb48le pipe to ffmpeg
from a writer thread.

Encode (verified commands):
- final: `ffmpeg -hide_banner -loglevel error -y -f rawvideo -pix_fmt rgb48le -s WxH -framerate FPS -i - -an -vf "zscale=matrix=709:range=limited:dither=error_diffusion,format=yuv420p10le,setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv" -c:v libx265 -preset slow -crf 18 -x265-params "log-level=error:aq-mode=3:colorprim=bt709:transfer=bt709:colormatrix=bt709:range=limited" -tag:v hvc1 -movflags +faststart out.mp4`
- draft: same input, `-vf "scale=out_color_matrix=bt709:out_range=tv:threads=8,format=yuv420p10le,setparams=..." -c:v hevc_nvenc -preset p5 -rc vbr -cq 23 -b:v 0 -tag:v hvc1`
- never feed JPEGs to ffmpeg directly (full-range yuvj420p bug of the v1 videos); ffprobe every output (hevc, hvc1, no audio
  stream, yuv420p10le, bt709 tags, frame count).

### 7.2 Layout geometry

- Canvas width W (2560), gap g (4) black (exact zero) between cells and rows.
- A row with one cell and `crop` [x,y,w,h] (source pixels): crop, scale to W x round(h*W/w) (no scale when w == W).
  `pan`: [[game_t, x, y], ...] keys of the crop origin, smoothstep, clamped to the source.
- A row of n cells without sizes: inner = W - (n-1)g; widths = inner // n, the first (inner % n) cells get +1; row height
  H = 2*ceil(max_width*9/32) unless `height` is given; each cell cover-fits its source (scale = max(w/srcW, H/srcH)) and
  centre-crops (or uses the cell `crop` / `anchor`). Results: 2 cells 1278x720 -> canvas 2560x1804; 3 cells 851/851/850 x 480
  -> 2560x1564.
- `preset`: `"16x9_3"` = band [0,242,2560,956] + 4 + 3 cells x 480 = 2560x1440; `"16x9_2"` = band 716 + 4 + 2 cells x 720.
- A cell `cam` may be a camera name, `"cut"` (the edit) or the director camera. Every dimension even by construction.

### 7.3 `compose` section (in spec.json or `--compose` file)

```json
"compose": {
  "fps": 30,
  "blur": {"space": "linear", "shutter": 144},
  "grade": {"default": {"lift": 0.5, "k": 4, "floor": "auto", "ev": 0, "saturation": 0},
            "cams": {"high": {"ev": 0.2}},
            "auto": {"cams": ["director"], "target": "game", "maxEv": 0.8, "smooth": 1.5}},
  "labels": {"show": true, "font": "Oswald", "size": 0.016, "color": [216, 210, 192], "alpha": 0.7,
             "names": {"low_left": "Low left"}},
  "angles": {"cams": "all", "speed": "meta"},
  "layouts": [{"name": "main", "width": 2560, "gap": 4, "preset": null,
               "rows": [[{"cam": "game", "crop": [0, 180, 2560, 1080], "pan": [[0, 0, 180]]}],
                        [{"cam": "low_left"}, {"cam": "outlet_bale"}, {"cam": "high"}]]}],
  "edit": {"name": "cut1", "fadeIn": 0.5, "fadeOut": 1.0, "minHold": 4, "maxHold": 6.5,
           "speed": [[0, 1], [4.5, 0.25], [7.0, 1]],
           "shots": [{"cam": "game", "from": 0},
                     {"cam": "low_left", "from": 4.5, "in": {"type": "dissolve", "dur": 1.0, "align": "center"}, "label": "Low left"},
                     {"cam": "high", "from": "@baleLand+0.5", "to": 12, "replay": null, "in": {"type": "dip", "dur": 0.8}}]},
  "encode": {"codec": "x265", "crf": 18, "preset": "slow", "bits": 10,
             "draft": {"codec": "nvenc", "cq": 23, "scale": 0.5}}
}
```

- Edit times are game seconds or `"@event+s"` resolved from the take's events.csv (first `event` row). Transitions: `cut`,
  `dissolve` (out = A + (B - A) * smoothstep((j+0.5)/n), n = round(dur*fps), `align` center|start|end), `dip` (to black and
  back). The outgoing camera keeps running during a dissolve (all cameras exist for every frame). `replay` [t0, t1] re-reads
  a source range out of order. Head/tail fades from black. Labels fade in 0.4 s, hold 2 s, fade out 0.6 s, hidden during
  dissolves.
- `edit.speed` turns on warp mode for the cut; without it the cut follows meta frames. Warp mode also reads `edit.shutter`
  (degrees, default `blur.shutter`) and `edit.interp` (`nearest` | `blend`).
- `labels.size` is a fraction of the output WIDTH (0.016 = 41 px at 2560). A layout `labels: false` hides that layout's
  labels, `angles.labels: true` draws them on angle videos, a cell or shot `label` overrides the text (`false` hides it).
- `grade.auto.target`: a number is a log-mean luma level (INSIDE scenes sit at about 15-29, so 30 pins `maxEv`); a camera
  name (recommended: `"game"`) matches each auto camera's exposure to that camera's median.
- Accepted shorthands: a string for `in` (the transition type), a single cell object as a row, a `heights` map per layout.
- Pipe-mode takes: the k-th frame of `<cam>.mkv` is that camera's k-th captured step (director copy frames included); raw
  takes need `run.json` `raw {w, h}`.

### 7.4 Grade (offline lift)

Floor-anchored lift per camera: u = (x - floor)/(1 - floor), g = u + s*u*(1-u)^k, y = floor + (1 - floor)*g; defaults
s 0.5, k 4, floor = p0.5 luma of the camera over the take (INSIDE luma weights 0.22/0.707/0.071). Optional exposure in linear
light above the floor (lin' = lf + 2^ev (lin - lf)) followed by the game's shoulder (from
an analysis of the game's post shader; the analysis script is not shipped). Auto exposure only for moving cameras: per-frame log-mean
luma on 320x180 decodes, ev = clip(target - m, 0, maxEv), zero-phase gaussian (smooth seconds), slew limit 0.5 EV/s, within
+-0.3 EV of the camera median. Never apply the game's post levels/saturation LUT to game captures (post already ran
in-engine). Exportable as `.cube` / ffmpeg `curves`.

### 7.5 `tools/mcam_qa.py`

```
python -I tools/mcam_qa.py <command> <take> [options]
  qa        checks -> qa.json + qa.txt; exit 0 ok, 1 errors, 2 unreadable take          [--edit file] [--strict]
  sheet     contact sheets per camera and a cut storyboard                                [--every 1.0] [--edit] [--ab]
  sync      sync sheet: rows = cues/events/cuts, columns = cameras
  paths     top (x-z) and side (x-y) plots of camera paths and targets from poses/tracks, player-side line z=-2
  timeline  timeline PNG: segments shaded by slow-mo factor, events, cues, shots, holds out of range in red
  diverge   compare tracks.csv of two takes (rehearsal vs capture): max deviation per target
```

Checks: missing/corrupt frames, partial last step, black frames (luma p50 < 2), frozen steps (all cameras identical to the
previous captured step while game_time advanced), no-advance steps (game_time not increasing), clipping/crush percentages,
exposure jumps, fallback poses (flag F or a non-game camera equal to the game row), clamped poses, player-side violations
(z > -2 unless pitch > 45, forward z < -0.05), speed dips and jerk per shot (second differences of position and aim), director
path crossing the line, undersampled slow motion, holds outside [minHold, maxHold], transitions longer than 40 % of a shot,
late loads and timeScale leaks from events.csv, render budget differences, TAA convergence helper (`--taa` compares n=4/8/16
stills against an n=64 reference when such a preview set exists).

### 7.6 `tools/mcam_spec_check.py` (DOCS)

```
python -I tools/mcam_spec_check.py <spec.json>... [--types src/InsideDev/Runtime/MultiCamTypes.cs] [--schema-md] [--quiet]
```

Builds the schema by parsing MultiCamTypes.cs (classes deriving `SpecNode`; one `public <type> <name> [= default];` per line;
fields marked `[SpecIgnore]` skipped; `IJsonValue` classes accept their alternate forms: TimeRef number/`"@ev+s"`, TargetRef
string / [x,y,z] / object, CueSpec `[t, cmd...]`, RateSpec `[from, rate, group]`; map form for lists of named objects; single
value for string/target lists). Reports the same unknown-key and type errors as SpecBind, then the offline lint subset of
4.13 and the cost estimate. `--schema-md` prints the schema tables used in docs/MULTICAM.md. Exit 0 ok, 1 errors, 2 bad
input.

---

## 8. C# interface between CORE and RIG

Shared types (MultiCamTypes.cs, frozen): `SpecNode`, `SpecReport`, `IJsonValue`, `SpecIgnoreAttribute`, `TimeRef`,
`TargetRef`, `GroupSpec`, `GroupFilter`, `MultiCamSpec`, `TimingSpec`, `SegmentSpec`, `RateSpec`, `CueSpec`, `EventSpec`,
`RenderSpec`, `OutputSpec`, `SessionSpec`, `PostSpec`, `StreamSpec`, `ShakeSpec`, `KeySpec`, `ShotSpec`, `CamSpec`,
`MultiCamConst`, `PoseFlags`, `CamPose`, `GameCamState`, `TargetSample`, delegates `TargetLookup`, `PoseLookup`,
`EventTimeLookup`, `MultiCamContext`, `ICamRig`, `ITarget`, `SpecBind` (`Fill`, `Convert`, `Read<T>`, `Overlay<T>`,
`Walk`, `Schema`, `Vec2`, `Vec3`, `Fields`, `Field`). The file was frozen during the build.

RIG provides (public signatures already compiled as stubs in MultiCamRig.cs / MultiCamTargets.cs; keep them exactly):

```csharp
public static class MultiCamRig
{
    public static ICamRig Create(CamSpec spec);                              // never null; one instance per camera per run
    public static void Validate(MultiCamSpec spec, SpecReport report);       // mode rules, mode->keys, director refs/cycles,
                                                                             // keys order, player-side and range lint (4.13)
    public static List<int> EvalOrder(MultiCamSpec spec, SpecReport report); // indices into spec.cameras, directors after sources
    public static void SampleGame(Camera cam, MultiCamContext ctx);          // fills ctx.game right after the step frame
}

public interface ICamRig                                                     // (types file)
{
    CamSpec spec { get; }
    void Begin(MultiCamContext ctx);                                         // after targets resolved; reset smoothing state
    CamPose Evaluate(MultiCamContext ctx);                                   // every step, in EvalOrder
    void Plan(MultiCamContext ctx, List<CamPose> poses);                     // keep-loaded prepass samples (ctx.planning = true)
    void End();                                                              // destroy dummy objects
}

public static class MultiCamTargets
{
    public static void Begin(MultiCamSpec spec, SpecReport report);          // strict resolution of every TargetRef in the spec
                                                                             // (SpecBind.Walk), report "ref -> path #id active pos"
    public static bool Resolve(TargetRef r, SpecReport report);              // one ref (sets r.cache / r.cacheVersion)
    public static GameObject ResolveObject(string query, string where, SpecReport report); // strict object lookup for CORE
                                                                             // (event objects, hide lists, interpolate)
    public static void Step(MultiCamContext ctx);                            // once per step after SampleGame: positions,
                                                                             // velocities, groups, re-resolve on stream change
    public static bool Sample(TargetRef r, out TargetSample s);              // TargetLookup; cached per step
    public static List<TargetRef> Tracked();                                 // tracks.csv rows: spec.tracks + camera targets/looks
    public static string Describe(TargetRef r);                              // resolution text for check/run.json
    public static void End();                                                // clear caches and subscriptions
}
```

CORE wires the context once per run: `ctx.spec`, `ctx.camera`, `ctx.screenW/H`, `ctx.aspect`, `ctx.fps`,
`ctx.target = MultiCamTargets.Sample`, `ctx.poseOf` (poses of this step, filled in EvalOrder), `ctx.eventTime`
(MultiCamEvents), `ctx.log` (log.txt + DevLog); and per step: `step`, `t`, `dt`, `frame`, `sub`, `captured`, `rate`, `speed`,
`outputTime` (= frame/fps, or the running output clock for uncaptured steps), `outputDt` (= dt / speed), `realTime`,
`preview`, `rehearse`, `planning`.

Call order per step (CORE): SampleGame -> MultiCamTargets.Step -> events poll -> Evaluate (EvalOrder) -> CSV rows -> visits.
Disabled cameras are evaluated but not visited. `CamPose.ok == false` -> CORE captures `ctx.game.rendered` and sets flag
Fallback in poses.csv. CORE applies `pose.near` (0 = game near) and honours `copyOf`.

---

## 9. File ownership

| Path | Owner | Notes |
|---|---|---|
| docs/MULTICAM_DESIGN.md | ARCHITECT | This file. |
| src/InsideDev/Runtime/MultiCamTypes.cs | ARCHITECT | Frozen shared contract; nobody else edits it. |
| src/InsideDev/Runtime/MultiCam.cs | CORE | Rewrite: load/lint/legacy, runner, timing, cues, session, keep-loaded, preview/rehearse/show, status, abort. |
| src/InsideDev/Runtime/MultiCamEvents.cs (new) | CORE | Trigger sources, EventTimeLookup, predictions. |
| src/InsideDev/Runtime/MultiCamCapture.cs (new) | CORE | Readback, writers, CSV/JSON writers, ffmpeg pipes. |
| src/InsideDev/Runtime/MultiCamLook.cs (new) | CORE | Camera MonoBehaviour: post overrides, _RealTime, TAA config helpers. |
| src/InsideDev/Runtime/CameraControl.cs | CORE | Immediate pose write, capture lock, roll in Pose, fov/roll/json helpers. |
| src/InsideDev/Remote.cs | CORE | Only the `mcam`, `cam`, `hud`, `clean`, `gamma`, `seed` cases and the capture-lock gate. |
| src/InsideDev/Runtime/EventMonitor.cs | CORE | `Fired` event, `HookNow`, stale-tap fix. Surgical. |
| src/InsideDev/Render/RenderHost.cs | CORE | `suppress` flag (Submit and RequestProbe early return). Surgical. |
| src/InsideDev/DevRoot.cs | CORE | HUD setter for `hud`/`clean`, hotkeys off while capturing. Surgical. |
| src/InsideDev/Runtime/MultiCamRig.cs | RIG | Replace the architect stub bodies; keep the public signatures. |
| src/InsideDev/Runtime/MultiCamTargets.cs | RIG | Replace the architect stub bodies; keep the public signatures. |
| tools/mcam_compose.py | COMPOSITOR | |
| tools/mcam_qa.py | COMPOSITOR | |
| tools/mcam_take.py (new, shared reader/timeline) | COMPOSITOR | Imported by mcam_compose.py and mcam_qa.py. |
| tools/fonts/ | COMPOSITOR | Local copies of label fonts (Oswald / IBM Plex Mono), Bahnschrift fallback. |
| offline compositor tests (not shipped) | COMPOSITOR | Unit tests, synthetic v2 take generator. |
| tools/mcam_spec_check.py | DOCS | |
| specs/*.json | DOCS | |
| docs/MULTICAM.md | DOCS | User guide (English). |
| docs/MULTICAM_ru.md | DOCS | Short Russian guide. |
| offline spec-check tests (not shipped) | DOCS | Spec-check tests and negative fixtures. |
| out/ | everyone (own subfolders) | Generated outputs only. |
| every other file under src/InsideDev | nobody | Do not touch. |
| the Steam install of the game, the decompiled game code | nobody | Never written (tests ran in a separate copy of the game). |

## 10. Build, test and game-run protocol

- Build (zero errors required): `GAME=<game> sh build/build_mod.sh` writes `bin/InsideDev.dll`. In `src/InsideDev` it runs
  Roslyn `csc.dll` from a .NET SDK: `-nologo -noconfig -nostdlib -t:library -langversion:7.3 -optimize+ -unsafe
  -nowarn:0618,0414,0649,0169,0219,0162,0168 -r:$M/mscorlib.dll -r:$M/System.dll -r:$M/System.Core.dll -r:$M/System.Xml.dll
  -r:$G/UnityEngine.dll -r:$G/PlayMaker.dll -r:$G/Assembly-CSharp-firstpass.dll -r:$G/Assembly-CSharp.dll
  $(find . -name "*.cs" | sort)` with `M = <game>/INSIDE_Data/Managed` and `G = <game>/GameCode` (README, "Game code").
- CORE and RIG compile together. The stubs let CORE run with `game`-pose cameras before RIG landed.
- C# rules: C# 7.3 on old Mono (no ValueTuple/tuple syntax, no newer BCL APIs), Unity 5.0 API only, no comments inside
  function bodies, English identifiers, match the surrounding style (string concatenation, `CultureInfo.InvariantCulture`).
  Animation API only on active objects (AnimSafe).
- Offline binder test (architect, passed): MultiCamTypes.cs + Json.cs compiled for .NET 9 against the real UnityEngine.dll
  bind a v2 spec (all forms: map lists, TimeRef strings, cue/rate arrays, target forms, groups, defaults overlay) and a v1
  spec with no warnings; unknown keys and type errors are reported with paths and suggestions.
- Game runs during development: one game at a time, driven through the in-game MCP server, in a separate copy of the game
  folder (`bin/InsideDev.dll` installed as `_mod/InsideDev.dll`), with the game muted (InsideMuter + `audio force rtpc
  rtpc_master_volume 0` + `audio native on`; a local choice, the release never mutes the game by default). Recommended order: CORE smoke tests with stubs (T1-T4), then the integration run after RIG (T5-T9).
- In-game tests (ChickenGun recipe of 4.14):
  - T1 `mcam check` on every specs/*.json: 0 errors, targets resolved (after RIG).
  - T2 freeze integrity: preview twice without stepping, images identical within noise; timeScale stays 0 through visits.
  - T3 TAA: preview one static pose with taa keep/reset and samples 1/4/16; reset+16 visibly cleaner, no ghost of the previous
    camera (place a very different camera before it).
  - T4 game pose restore: rig.csv rendered pose of a run with no `game` camera equals a run with one (within 1 cm).
  - T5 ChickenGun take with 5-6 cameras incl. director, events baleFall/baleLand fire, slow-motion segment switches on time.
  - T6 abort mid-take: game unfrozen, overlays and settings restored, editor.cfg unchanged.
  - T7 rehearse + capture with seed: event times within one frame, tracks diverge < 5 cm before the fall.
  - T8 keep-loaded: no late loads for the planned cameras; late-load mark appears when a camera looks somewhere new.
  - T9 compositor end-to-end on the take: angles, layout, cut, qa exit 0.

## 11. Task lists

### CORE (MultiCam.cs, MultiCamEvents.cs, MultiCamCapture.cs, MultiCamLook.cs, CameraControl.cs, Remote.cs cases, EventMonitor.cs, RenderHost.cs, DevRoot.cs)

P0
1. Load: MultiCamSpec.Parse, legacy mapping 4.12, defaults/render/post/shake overlays, CORE lint 4.13 (timing integrality,
   names, events and `@` references, render budget), MultiCamRig.Validate, in-game MultiCamTargets.Begin for check/start.
2. Runner per 3.2-3.3 with the exact frame accounting, measured game time, segments 3.4 (R, N, M, frame/sub/captured),
   physics policy "game" (fixedDeltaTime untouched), `until`, preroll, stop.
3. Game pose restore before every step frame (fixes the v1 bug at MultiCam.cs:253/275); SampleGame before any camera move.
4. CameraControl: `Set` writes transform/FOV/near immediately (and stores the pose for Apply); capture lock (no input);
   a game-step toggle that unpauses CameraScript without writing the camera; `cam pose` roll, `cam fov`, `cam roll`, `cam json`.
5. Session push/pop 3.6 in try/finally + `mcam abort` from a static session object; timeScale forced after each yield
   (GameManager too) with leak marks; focus/pause/loading hold.
6. Clean frames: RenderHost.suppress (Submit and RequestProbe early return), overlay save/restore without editor.cfg, `hud`
   and `clean` commands; DevRoot hotkeys off while capturing; external command gate with whitelist.
7. Visit protocol with warmup + N samples, TAA feedback schedule, jitter pattern, motionBlur 0, restore of TAA values.
8. Writers: jpg on a background writer thread with bounded queue, raw (.rgba), CSV writers for meta/poses/events/tracks/rig
   with the exact columns of Section 6, run.json, progress.json, log.txt, periodic flush.
9. Cues with TimeRef, events engine (MultiCamEvents.cs) for all types in 4.5, EventMonitor `Fired`/`HookNow`/stale-tap fix,
   predictions (`expect`, rehearsal folder), events.csv kinds.
10. Bridge: `mcam check|start|rehearse|preview|stop|abort|status [json]|show|pose|list|schema`, setup primitives 5.3.

P1
11. Keep-loaded prepass and late-load detection 3.9; `stream.freeze`.
12. MultiCamLook: `_RealTime` on the capture clock; per-camera PostSpec with base restore; fog match; noWater; hide lists.
13. Seed command and spec seed; `interpolate` list; `session.res`, vsync/targetFrameRate/runInBackground push.
14. `mcam probe` authoring output; status phase timings; GC every n steps.

P2
15. `pipe` output (ffmpeg per camera, stderr drain, kill on abort, NVENC session guard).
16. `taa: "toggle"` fallback; physics "fine" (experimental, with warnings).

Acceptance: zero-error build; T1-T4 with stubs, T5-T8 after RIG; no state left changed after stop/abort/exception.

### RIG (MultiCamRig.cs, MultiCamTargets.cs)

P0
1. MultiCamTargets: grammar and strict resolution 3.7 (exact path, `/`-bounded unique suffix, unique name, `[i]`,
   `::Component`, `#id`, no substring fallback, candidates on ambiguity), points (pivot, bounds without DECAL_*, com, rb,
   collider, top/bottom, bone:*), boy points via Actor bones (torso default, head, neck, root, pos3, bounds, probe), huddle
   points (visual default, centre, top, bottom, bounds, bone:*), game/gameaim/probe, fixed points, offsets; velocities;
   per-step cache; ResolveObject; Describe; re-resolve on StreamVersion change; Tracked.
2. MultiCamRig.SampleGame (all GameCamState fields incl. handshake/shake local offsets and cut detection).
3. Rigs: game (rendered/clean/offset/fovScale), static (look + lookSmooth), follow (damp/game/none lag, lead, planar, local,
   start), keys (catmull/linear, ease ends/keys/each/linear with arc-length timing, look interpolation, start), director
   (orbit/linear/cut transitions with InterpolateCamera/InterpolateAim, ease, clock output, auto cut, copyOf on hold, Cut
   flag). Every rig fills `aim`, flags, near.
4. Validate (mode rules, mode->keys warnings, director refs and cycles, keys order, player-side and range lint 4.13) and
   EvalOrder (topological).
5. Player-side clamp and flags.

P1
6. gamerel, gamelike (Umbrella<BlendBox> enumeration, UpdateCameraPivot, look-ahead integrator, game smoothing law,
   box blend), orbit, rail, frame (crop-aware solver).
7. Groups (of/find/list, filters, hysteresis, modes, smoothing, max).
8. Shake layer: live hand/fx with pullback option; replica from noiseSoft/noiseHard via AnimationClip.SampleAnimation on a
   hidden dummy (AnimSafe rules), game or output clock; End() cleanup.
9. Plan(): keep-loaded samples for static/keys/rail/orbit and the t=0 pose of the others.

Acceptance: zero-error build with CORE; every mode produces smooth poses in a rehearsal (no speed dips at keys with
ease "ends", director blends keep the subject on screen); `mcam check` lists resolved targets for the example spec.

### COMPOSITOR (tools/mcam_compose.py, tools/mcam_qa.py, tools/mcam_take.py, tools/fonts/, offline tests)

P0
1. mcam_take.py: take reader (v2 columns of Section 6, v2-draft and v1 fallbacks, common complete range), timeline builder
   (meta frames and warp mode with box-shutter weights), game time -> frame, event time resolution from events.csv.
2. Frame engine: threaded decode with bounded prefetch, reduced decode for cells, linear/gamma averaging in uint16, grade
   LUT 7.4, writer thread into the ffmpeg rawvideo pipe.
3. Encoder: final libx265 Main10 and draft hevc_nvenc commands of 7.1, ffprobe verification after each encode.
4. Commands `scan`, `angles`, `layout` (geometry 7.2 incl. presets and pan), `cut` (cut/dissolve/dip, fades, holds, labels).

P1
5. Grade: per-camera floor-anchored lift, linear exposure + shoulder, auto exposure for moving cameras, `.cube` export.
6. Labels (PIL patches, local fonts), `all`, `director` (edit -> director shots, output-second blends to game seconds),
   `autocut`.
7. mcam_qa.py: `qa` checks of 7.5 with qa.json/qa.txt and exit codes, `sheet`, `sync`, `paths`, `timeline`, `diverge`.
8. Safety guards: free-commit check, one encode at a time, INSIDE.exe warning, outputs under `<repo>/out` only (unless
   `--force`).
9. Tests: grouping, shutter weights, time -> frame, warp integration, layout geometry (widths + gaps = 2560, even sizes), LUT
   monotonic with a fixed floor; synthetic v2 take from mcam_take1 with mixed segments; smoke on mcam_test (20 steps) with
   ffprobe assertions; regression on mcam_take1 (v1 fallback).

Acceptance: mcam_test smoke passes; mcam_take1 angles + layout + cut encode as H.265 hvc1 without audio, correct bt709 tags
and frame counts; qa exits 0 on a good take and 1 on a corrupted copy.

### DOCS (tools/mcam_spec_check.py, specs/*.json, docs/MULTICAM.md, docs/MULTICAM_ru.md, offline spec tests)

P0
1. mcam_spec_check.py per 7.6: schema parsed from MultiCamTypes.cs, alternate forms, unknown keys with suggestions, type
   errors with paths, offline lint subset of 4.13, segment table (R, N, M), cost and disk estimate, `--schema-md`.
2. specs: `template_minimal.json` and ChickenGun examples (a v1-format compat spec without a version key; game + 3 static +
   follow bale; a director with orbit blends and 4-6 s holds; baleFall/baleLand events with a slow-motion segment, sub-step
   blur and event-relative cues/shots; a framing preview with warmup 1, samples 1), each with a `compose` block; all pass
   mcam_spec_check with 0 errors; poses on the player side (z < -2). The release ships only `template_minimal.json` and
   `replay_smoke.json`.
3. docs/MULTICAM.md: workflow (author -> check -> preview -> rehearse -> capture -> compose -> qa), commands, schema
   reference (generated tables), camera mode guide with when-to-use, events cookbook (signal vs call), timing and cost table,
   render budget guidance, look/lift guidance, output format, compositor usage, troubleshooting (overlays, focus, late
   loads, TAA ghosts, pose fallback), the player-side rule.

P1
4. docs/MULTICAM_ru.md: short Russian guide (workflow, commands, one full example, common errors).
5. Offline spec tests (not shipped): negative fixtures (unknown keys, bad types, cycles, unknown events, behind-the-set
   positions) with expected checker output; a test runner script.

Acceptance: every spec passes; negative fixtures fail with the expected messages; the docs match the types (schema tables
regenerated with `--schema-md`).

## 12. Unverified assumptions and how to verify them

| Assumption | Risk if wrong | Verification |
|---|---|---|
| Native TAA reads feedbackMin/Max and frustumJitterPattern every frame | history reset does nothing | T3: feedback 0 on a static pose must shimmer; fallback `taa: "toggle"` |
| MultiCamLook added last runs OnPreRender after FogAmbientCameraBlend | fog/bloom overrides overwritten | read back RenderSettings.fog* in OnPostRender once and log |
| FsmLog.LoggingEnabled is true in the player | fsm triggers miss transient states | log `FsmLog.LoggingEnabled` and an Entries count at arm time; polling fallback |
| Two WaitForEndOfFrame coroutines: overlays may draw before ReadPixels | overlays in frames | RenderHost.suppress makes the order irrelevant |
| GetPixels32 allocation (14.7 MB) is tolerable | GC pauses / heap growth in raw mode | log GC count and step time; `session.gcSteps` |
| Physics at 60 Hz with R=120/240 gives smooth interpolated motion for the boy, bale (Interpolate) and chicks (Interpolate) | stutter in slow motion | rehearse + tracks.csv second differences; `interpolate` list; physics "fine" as fallback |
| CameraHandshake child transform reflects the step frame's handshake when SampleGame runs | wrong hand offsets | compare rig.csv hand offsets between steps at rest (about 10 % soft noise) and while running |
| AnimationClip.SampleAnimation on a hidden dummy works for the handshake clips | no replica handheld | in-game rig test; fallback: procedural noise with the same spectrum |
| Streaming progresses while time is frozen during the keep-loaded prepass | prepass waits `stream.timeout`, pop-in | log line "culler(s) not active after ..."; T8 |
| `GameManager.TimeScale = 0` alongside `Time.timeScale` during frozen frames has no side effects | menus/audio react to the pause | T2/T6: no pause menu, state restored after stop/abort |
| `box.UpdateCameraPivot` writing the box's scratch pivot during frozen frames is harmless | game camera jumps after a take | T4 rig.csv comparison with and without a `gamelike` camera |
| `gamelike` box choice (point inside the box collider) matches the game's probe trigger | different box at box edges | compare rig.csv `box` with a `gamelike` camera's box on the boy |
| Huddle `bounds` padding 0.3 m | loose/tight framing of the huddle | first huddle take |
| The bale's landing fires `impact` (it may land in water) | `baleLand` never fires; `until` falls back to `seconds` | T5 events.csv; rehearsal first |
