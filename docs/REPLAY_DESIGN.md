# Replay mode design (record once, film every camera from the recording)

> These are the design notes written during development. For usage read `docs/RECORD_REPLAY.md`, `docs/AUDIO.md`, `docs/MULTICAM.md`
> and `docs/LAB.md`. Statements marked [unverified] or dated describe the state at that time.

Architect document for the "cutscene" replay mode of MultiCam: one real-time run of a scene is recorded, then the game is
frozen and every virtual camera is filmed in its own continuous pass while the mod drives the scene from the recording.
Status: design only, nothing built apart from the Phase 0 lab. Revision 2 (2026-10-25): review critique verified against
the code and applied; the parts that did not hold are listed in Section 11. Revision 3 (2026-10-25): Phase 0 lab results
(L1-L12, Section 12) applied; every open point the lab decided is closed in the sections it affects. Material round
(2026-10-25): shader-state channels (shader globals from their script writers, shared-material values, renderer property
blocks; 2.3.2, 2.4.3, 4.2 sections 17/18, content version 2), offline only. `docs/MULTICAM_DESIGN.md`
(v2) stays the contract for everything this document does not change (spec binding, rigs, targets, output folder,
compositor). Unity 5.0.4f1, game Mono, mod code C# 7.3.

Markers: **[unverified]** = derived from code or disassembly but never run in the game; **[speculation]** = inference without
direct evidence; **[estimate]** = number not measured. Every [unverified] item has a test in Section 9. `(Lx)` after a
statement = measured in lab test Lx (Section 12).

Paths are relative to the repository root; `<game>` is the INSIDE folder. Evidence abbreviations (the decompiled game code
and the level index are not part of this repository; file:line references point into a decompile of the game's own
assemblies): `FP` = decompiled `Assembly-CSharp-firstpass`, `AC` = decompiled `Assembly-CSharp`, `PM` = decompiled
`PlayMaker`, `ENG` = decompiled `UnityEngine`, `IDX` = a per-level index of the scene data extracted from the game's level
files, `MOD` = `src/InsideDev`, `MC` = `src/InsideDev/Runtime`,
`EXE` = INSIDE.exe (SHA-256 prefix b027a2dbf752ec38, identical GOG and Steam builds). Line numbers as of 2026-10-25.

Contents

0. Summary
1. Decision: which approach for which scenes
2. Architecture: SceneRecorder, SceneReplayer, frame phases, coverage
3. Spec extension and bridge commands
4. File format
5. Motion blur and slow motion
6. Sound (three cases) and the headphone safety rule
7. Determinism test protocol for input replay
8. Implementation plan by file ownership
9. In-game test plan with pass criteria
10. Unverified assumptions, corrections to the research inputs
11. Rejected critique (review of revision 1)
12. Lab results (Phase 0)

---

## 0. Summary

- **Record pass**: the scene runs once in real time at timeScale 1 with the game camera, exactly as in normal play (cues and
  events from the v2 spec). At every rendered frame the recorder stores the world state of everything that moves:
  transforms, boy and huddle bones, active and renderer flags, particles (raw Shuriken particles), lights, MaterialInstance
  values, shader state (globals such as `_PDHuddleAmbient`, the huddle's `dam_*` bruise vectors on its shared material,
  renderer property blocks), blend-probe positions, fog blend values and the full game camera state. Wwise output is captured to a WAV with
  frame-aligned markers (capture works, L5 S1; a sync-grade silent path is still open, below).
- **Replay passes**: the game is frozen (timeScale 0), streaming is frozen and no bounds culler may call SetActive. For each
  camera, one continuous pass renders one frame per output step while the mod writes the recorded state for replay time T at
  fixed points of the frame (RuntimeCallbacks.lateUpdate for everything that moves, L1-L3). Every pass starts from the same
  saved state (2.4.9), camera-dependent visibility is emulated per camera by hiding renderers and lights (exact, L7), the
  window never flickers between angles, any float slow-motion factor works (interpolation between samples), the engine's own
  motion blur comes from real per-frame motion (frozen blur equals running blur, L2), and new cameras can be shot later from
  the same file. `_Time`-animated shaders (water, rain lines, ripples) stay frozen in replay (L10).
- **Sound** comes from the record pass only; it is in sync with every camera because all passes share the record clock.
  In the release the game sound is never muted by default: `acap.output` defaults to `audible` and the capture runs while
  the game plays normally (6.0). The opt-in `muted` output keeps the headphones silent through layered guards (Section
  6.0); there, suspended capture runs at 0.919x real time (L5 S3), so muted sound takes wait until S3b or the muter-only
  stage S2 passes (6.0 G3).
- **Input replay** (Playdead Playback style) is kept only as an experiment behind a determinism gate (Section 7).
  **v2 frozen multi-visit** stays for previews and for effects that a recording cannot drive.

## 1. Decision: which approach for which scenes

### 1.1 The three approaches compared

| Criterion | A: scene recording replay (this design) | B: input replay, one run per camera | C: v2 frozen multi-visit |
|---|---|---|---|
| Same scene state in every camera | yes, by construction | only if the run is deterministic (1.3: unlikely) | yes |
| Window flicker between angles | none (one camera per pass) | none | yes (every angle every step) |
| Slow motion | any float speed, interpolated | captureFramerate per run, changes Update-phase logic | integer R only, game interpolation |
| Motion blur | engine velocity buffer from real motion (works frozen, L2), or sub-frames (`look: clean`) | engine | sub-steps only (engine blur off, MULTICAM_DESIGN 4.6) |
| Sound | one real-time capture, in sync with all cameras | per run, but runs differ | impossible in-take: GlobalAudio posts pause/resume on every freeze (FP/GlobalAudio.cs:164-175) |
| Re-shoot with new cameras later | yes, from the file | yes if deterministic | no, re-run the scene |
| Cost per camera [estimate] | ~1 render per output step (40-60 ms at 1440p) | one real-time run | W+N renders per step (9 renders ~0.3 s) |
| Non-transform simulations (water render textures, `_Time`-animated shaders such as rain lines, water and mud ripples, Hair, plain LineRenderer, procedural meshes) | no: frozen at the take end (L10; RopeLineRenderer is replayed, 2.4.3, L11) | yes | yes |
| Camera-dependent world (bounds cullers, flares, CameraLightSwap, WaterEdge, AudioCameraProbe) | bounds cullers: per-camera visibility emulated by hiding renderers and lights, no SetActive (L7); flares, CameraLightSwap, WaterEdge and AudioCameraProbe live (2.3.1, 2.4.4, L12) | changes the simulation itself | toggles state between steps |

### 1.2 Approach per scene class

| Scene class | Examples | Approach | Reason |
|---|---|---|---|
| Physics and AI with many movers | ChickenGun (24 chicks, bale, machine), crowds, ragdolls, ropes | A | 0.001 kg chick bodies driven by the global UnityEngine.Random stream every FixedUpdate (AC/Chicken.cs:381-470, AC/ChickenGunLogic.cs:215,389,398-399) make B diverge; A records the outputs and never needs the causes |
| Boy traversal | runs, climbs, swims | A; B optional after the gate (Section 7) passes | the boy is cheap to record (74 transforms, sharedassets252 bone tree); B buys nothing unless the shot needs effects A cannot drive |
| Huddle | huddle walks, crowd pushes | A | the cloth is Playdead rigidbody cloth whose visual bones are plain transforms (FP/VisualHuddleCloth.cs:119-175; Unity Cloth count is 0 game-wide) |
| Music-locked gameplay | `#lineup_`, `#forcePushGetWagon_`, `#forcePushRotator_`, `#forcePushMovingCover_`, `#ElevatorRide_` | A | SynchronizeWithAudio slaves game time to the wall-clock Wwise music (FP/GameManager.cs:545-607, FP/Playback/CommonSession.cs:15): runs cannot repeat, a recording does not care |
| Dominated by non-transform simulation | water surface render textures, `_Time`-animated water and rain close-ups (L10), Hair-heavy close-ups, plain LineRenderer effects (RopeLineRenderer ropes are replayed), procedural meshes | C (or A with those effects frozen) | A cannot drive them (2.5); the golden test (R3) shows how much is lost |
| Framing, stills, authoring | | C `mcam preview`, or the A viewer `srec play` | no recording needed, or an interactive free camera over a recording |

**Decision for ChickenGun: A.** Record clock `real`, `game`-look replay passes, the cameras of the ChickenGun v2 spec
unchanged. Wwise capture in the record pass: with the default `audible` output directly; with the opt-in `muted` output
only after a capture path passes (6.0 G3), until then `audio.capture: none`.
Accepted loss (L10): rain lines, mud ripples, splash edges and the grain chute are `_Time`-animated and stay frozen in every
replay camera; a shot that needs them is a C shot.

### 1.3 Why input replay is not the default (evidence)

- Playdead's own replays drifted: InputRecorder low-pass filters the boy position error and calls PositionDesynchronized at 1
  m (FP/Playback/InputRecorder.cs:449-473, :200-202, :228); the Savepoint session then teleports the boy with AdjustPosition
  (FP/Playback/InputRecorder.cs:148-157, FP/Playback.Session/Savepoint.cs:132-140). Props are never corrected.
- ResetRNG only resets RandomGenerator and PDRandom (FP/Playback/Session.cs:50-54); about 173 game files call UnityEngine.Random
  directly, one shared stream, so one extra draw anywhere reorders every later chick decision.
- The simulation depends on the camera: BoundsCullerRuntime calls `gameObject.SetActive(visible)` from the Camera.main frustum
  (FP/BoundsCullerRuntime.cs:236-264, :444-466), legacy Animation culls BasedOnRenderers (all 30 level171 Animations,
  m_CullingType 1), and culled groups own RNG consumers (rope wind, FP/Rope.cs:393-410; VfxParticleController,
  FP/VfxParticleController.cs:203-224; ParticleSound, AC/ParticleSound.cs:154-164). A different camera per run changes the run.
- Particles use auto random seeds (randomSeed 0 on all 51 ChickenGun systems) and subscene streaming is wall-clock
  asynchronous (FP/SubsceneCullerInfo.cs:95-111, FP/PreAwakeManager.cs:123-149).

### 1.4 What v2 keeps

`mcam preview`, `mcam rehearse`, `mcam show`, `mcam check` are unchanged. `mcam start` (v2 capture) stays for scenes in the
"non-transform simulation" row. The replay passes reuse v2's spec, rigs, targets, events, look component, writers and the
whole output format, so the compositor and QA tools read replay takes unchanged (Section 4.1).

---

## 2. Architecture

### 2.1 Components

```
 bridge / MCP  "mcam record|replay|take|passes|golden"  -> MultiCam.cs / MultiCamReplay.cs        [CORE]
               "srec ..."  -> SceneCommands.cs                                                    [PLAY]
               "acap ..."  -> AudioCapture.cs                                                     [AUDIO]
                                   |
   record pass                     |                         replay passes
   MultiCamReplay.RecordBody       |                         MultiCamReplay.ReplayBody
     setup, seed, preroll, cues,   |                           plan pass (no renders): ApplyNow(T_j),
     events, SampleGame            |                           Targets.Step, rigs -> poses of every camera
     SceneRecorder (hooks)  ------ scene.srec + sidecars --->  SceneReplayer (hooks), one pass per camera
     AudioCapture (WAV, markers)   |                           MultiCamGrab / writers (v2) -> <cam>/<step>.jpg
                                   v
   SceneHooks.cs: one place that owns RuntimeCallbacks.update / lateUpdate and Camera.onPreCull subscriptions   [PLAY]
   SrecFormat.cs: SrecWriter (background thread), SrecReader                                                     [REC]
   SceneTracks.cs: sparse tracks, interpolation                                                                  [PLAY]
   ReplayTypes.cs: spec classes, header, constants, IReplaySource, ReplayHub                                     [ARCHITECT]
   MultiCamTargets.cs: replay-aware velocities, pos3, probe                                                      [RIG]
   tools/mcam_audio.py (align, cut, stretch), tools/srec_tool.py (inspect, verify), mcam_compose/qa/take (mux, golden)
```

### 2.2 Frame phases and hook points

Standard Unity 5.0 order of one frame with the game's real execution orders (MonoScript.m_ExecutionOrder from the shipped
project: GameManager -615, SoundEngine -600, GlobalAudio -580, RuntimeCallbacks -400, MaterialInstance -200, CameraScript -1,
LogicManager 50, HuddleArmControl2 10, everything else including the mod 0; extracted from the PS4 sharedassets0.assets of the
same 5.0.4f1 project, assumed identical on Steam; the L1 trace confirms RuntimeCallbacks -400 before order 0 on Steam). Phase
order as measured in L1; revision 2 had the coroutine phase after Update.

| # | Phase | Game systems (evidence) | Record pass | Replay pass |
|---|---|---|---|---|
| 1 | FixedUpdate loop (0..n per frame; none at timeScale 0) | LogicManager.FixedUpdate (FP/LogicManager.cs:73-91), Chicken/ChickenGunLogic/HayBale FixedUpdate, GameManager.FixedUpdate input (FP/GameManager.cs:312-316), physics step | runner counts fixed steps | does not run |
| 2 | Update, RuntimeCallbacks.Update (-400) | public `RuntimeCallbacks.update` first, then `ExecuteInEditModeIncluded.update`: SubsceneCullingManager.Update (frustum refresh even with CullingActive false, FP/SubsceneCullingManager.cs:630-640), BoundsCullerRuntime.UpdateBoundsCullers (hooked at FP/BoundsCullerRuntime.cs:173), TransitionBoxManager.Update reading probe positions (FP/TransitionBoxManager.cs:148-158, :68) (FP/RuntimeCallbacks.cs:101-108) | `SceneHooks.preUpdate`: audio markers (RecordMarkers, CORE, 6.1) | `preUpdate`: activation for T, probe-carrying roots for T_k (boy and huddle roots, TransitionProbe transforms), ColorBlendProbe overrides, camera re-assert |
| 3 | Update, other scripts | CameraScript.Update/MoveCamera (FP/CameraScript.cs:322-330), Actor.Update rewrites every bone (FP/Actor.cs:402-457), LogicManager.Update -> UpdateVisual (FP/LogicManager.cs:93-110), PlayMakerFSM.Update | normal play | frozen writers rewrite end-of-take values (overwritten in 6) |
| 4 | Internal animation update [standard Unity] | legacy Animation, Animator; legacy Animation re-samples every frame even at timeScale 0 (L3: a written root bone was pulled back 20 deg the next frame) | normal | overwritten in 6, every frame |
| 5 | LateUpdate -615 .. -580 | GameManager.UpdateTimeScale (FP/GameManager.cs:545-566), SoundEngine.UpdateEngine -> RenderAudio (FP/SoundEngine.cs:547-576), GlobalAudio pause state | normal | GlobalAudio pauses Wwise (wanted) |
| 6 | LateUpdate -400, RuntimeCallbacks.LateUpdate | public `RuntimeCallbacks.lateUpdate` (FP/RuntimeCallbacks.cs:110-117) | nothing | `SceneHooks.lateWrite`: transforms, bones, particles, rope vertices + Draw. The write holds until culling (L1: 0 of 9 transforms changed over 120 frames) and renders in the same frame for skinned and rigid objects (L2) |
| 7 | LateUpdate -1 | native D11VelocityBufferTag LateUpdate bakes skinned meshes (EXE vtable slot 0xe0 = 0x1400de3d0) | normal | bakes the replayed bones (skinned velocity [unverified R6]) |
| 8 | LateUpdate 0 and later | DevRoot.LateUpdate -> CameraControl.Apply (MOD/DevRoot.cs:66-72), PlayMakerFSM.LateUpdate, UmbrellaExecutive.LateUpdate dispatching the LateUpdate umbrellas CrowdDude, Hair, FakeRagdollJoint2, AkGameObj (FP/UmbrellaExecutive.cs:93-104), HuddleLegControl, HuddleArmControl2 (10), BoyWetnessHandler | normal | neutralize table (2.4.2) removes the writers that ignore deltaTime; the rest re-asserted in 10 |
| 9 | Coroutines after `yield return null`: after every LateUpdate, before the first component OnPreCull (L1 trace: rc.update > rc.lateUpdate > LateUpdate 0 > coroutine > OnPreCull; revision 2 placed this phase after Update) | MultiCamRunner | record step: cues (their effects start in the next frame), t, SampleGame (reads the camera the frame renders), Targets.Step, events (event markers queued for the next preUpdate), `SetVirtual`, rows | runner tick (timeScale re-force after GameManager.UpdateTimeScale, so the value holds from the next frame on; status) |
| 10 | Render of the world camera (ENG/Camera.cs:14, fired at :603) | component OnPreCull messages in component order (first PDReflectionCuller; DepthBufferDownsample writes `_RealTime`, WaterCameraRendering dispatches TranslateWaterEdge to WaterEdge and WaterDust, MultiCamLook and later-added components last), then the `Camera.onPreCull` delegate (L1: after every component OnPreCull in 119 of 119 frames), culling, OnWillRenderObject (velocity tag matrices, BoyToShader), component OnPreRender (FogAmbientCameraBlend, MultiCamLook) then the `Camera.onPreRender` delegate, render, OnPostRender, image effects | `SceneHooks.preCullMain` (the delegate): **SceneRecorder.Sample** | `preCullMain` (the delegate, so after every component OnPreCull): culler visibility for this camera, rigid re-assert and read-back with conflict count, MaterialInstance, shader state (globals, material values, property blocks), shared materials, lights, FogAmbientBlender.currentBlendVal, D11PDPostCombined values, wrapAroundFrame on cuts |
| 11 | WaitForEndOfFrame | | optional game-frame grab (`record.captureGame`) | ReadPixels; then set T and the camera pose of the next step |

Consequences of the measured order (L1):
- preCullMain sees, and overrides, everything a component OnPreCull wrote this frame. The camera-driven writers there
  (WaterCameraRendering.TranslateWaterEdge -> WaterEdge.OnFastEventEx moves the edge, SetActive's it and writes `_Clip`/`_Disp`
  MaterialInstance floats from `Camera.current`, AC/WaterEdge.cs:107-160; WaterDust, AC/WaterDust.cs:71-79) must therefore be
  excluded from every preCullMain write (2.3.1 `camXf` and excluded materials), or the replay would force the game camera's
  values onto every other camera.
- The record step runs after SoundEngine.RenderAudio (-600) of its frame. Anything it posts to Wwise (cue commands, markers)
  enters the next frame's audio render, so markers are posted from `preUpdate` instead (6.1), and a cue stamped at sample k
  acts from frame k+1 (as in v2, whose runner has the same phase).
- The phase was measured for a coroutine started from a bridge command, as the MultiCam runner is. RecordBody asserts it at
  start (a frame stamp written by `lateWrite`/`OnLate` must already equal `Time.frameCount` when the record step resumes) and
  writes `runnerPhase` to record.json; a different phase aborts the record with a log line.

Hook rules (SceneHooks.cs):
- Subscribe once per run: `RuntimeCallbacks.update += OnUpdate`, `RuntimeCallbacks.lateUpdate += OnLate`,
  `Camera.onPreCull += OnPreCull` (a public static delegate field; the game never assigns it, the mod's RenderHost already adds
  to it, MOD/Render/RenderHost.cs:84). Remove in End and on abort.
- Fire each phase at most once per `Time.frameCount`: RuntimeCallbacks exists twice in the data (mainData Bootloader and level251
  `_Globals/_Globals`) and each instance fires every static event (FP/RuntimeCallbacks.cs:101-126). At runtime only one
  instance exists (`_Globals`, 1 including inactive objects and assets) and update, lateUpdate and
  ExecuteInEditModeIncluded.update fire once per frame, fixedUpdate once per logic step (L8: 120/120 frames, 124/124 steps).
  The guard stays as cheap insurance.
- `preCullMain` only when the camera is `RenderHost.WorldCam()` (reflection, water and depth cameras are skipped). It runs
  after every component OnPreCull of that camera (L1, see the consequences above).
- `lateFrame` = `Time.frameCount` of the last `OnLate`, public for the runner phase assert.
- Every subscriber runs in its own try/catch: `PDUtilities.SafeExecute` wraps the whole multicast in one try (FP/PDUtilities.cs:64-74),
  so one throw would skip every later subscriber, including game handlers.

### 2.3 SceneRecorder (REC, `MC/SceneRecorder.cs`)

#### 2.3.1 Record set

- Roots (`record.roots`, default `["auto"]`). `auto` = the `ContentRoot` (FP/SubsceneCuller.cs:227) of every loaded and active
  SubsceneCuller whose `SceneName` starts with the zone prefix of the current savepoint subscene (ChickenGun: `#chickenGun_`
  -> `#chickenGun_Gameplay`, `#chickenGun_Environment`, `#chickenGun_Backdrop`), plus `boy` (the `_Boy` root and
  `ScriptGlobals.boy.visualBoy.actor` subtree), `huddle` when present, and the spawn containers `_Dynamics` (FP/Dynamics.cs:5)
  and `_SubsceneDynamics` (FP/DynamicsSubscene.cs:5). Explicit forms: object path, `subscene:<SceneName>`,
  `zone:<zone>` (every subscene of that zone, e.g. `zone:huddleCeoFallAndDogs` = its `_Gameplay`, `_Environment` and
  `_Backdrop`), `boy`, `huddle`. `record.keep` names subscenes that are kept loaded (AlwaysLoaded) for the record, waited for
  (loaded and active) before sample 0, recorded from sample 0 like a `subscene:` root and written to `header.keptCullers`,
  so every replay keeps them loaded too. `record.box` {center, size} limits `auto` to subscenes whose CullingBounds
  intersect the box.
- Members: every Transform under the roots at Begin. A `subscene:` / `zone:` / `keep` subscene that is loaded but inactive is
  recorded from sample 0 (read once it activates); one that is not loaded is skipped at Begin (Unity's ContentRoot of an
  unloaded culler is the culler object itself, never walked). Subscenes join the recording during the take (added
  2026-10-25): those named by `subscene:`, `zone:` or `keep` when they load, those of the `auto` zone (inside `record.box`)
  when they activate. Triggers: the game's `SubsceneCuller.StaticEvents.postLoad` / `postActivate` (FP/SubsceneCuller.cs:
  30-48, 420, 500, 578-582), hooked while recording, and a poll every `record.rescanEvery` samples (also re-joins a subscene that was
  unloaded and loaded again). The join walks the ContentRoot in the sample of that frame, extends RecordPrep to it
  (AlwaysAnimate, forced cullers), applies camera_driven.tsv rows and `record.exclude` paths, adds the objects inline (OBJA,
  the scan's mover and quiet rules, so a static subscene goes dormant) and its `_SubsceneDynamics` as a spawn container;
  it writes an event row (kind `subscene`) and logs the cost (record.json `subsceneJoins`, a warning above 8 ms: that
  frame paid the walk, so list subscenes that matter in `keep` to record them from sample 0 without a hitch). The replay
  treats joined objects as spawned (2.4.1: not alive before their join sample, not counted against `maxUnresolved`).
- Excluded: the `_Camera_Main` hierarchy, mod objects (names starting with `InsideDev`), `record.exclude` paths, and every
  camera-driven transform writer (kind flag `camXf`: never written in replay, never touched by the rigidbody freeze of 2.4.2).
  Built-in list, confirmed by the camera probe in ChickenGun and the lake (L12: 10 camera-driven transforms each, 0 rigid
  gameplay objects):
  - GameObjects with `LensFlareSimple` (billboard transform and colour, AC/LensFlareSimple.cs:121-145; 9 in ChickenGun, 2 at
    the lake).
  - `AudioCameraProbe` (a BlendProbe with Rigidbody, SphereCollider and WaterProbe whose Update copies Camera.main x/y into its
    localPosition, FP/AudioCameraProbe.cs:28-32; its water enter/exit drives GlobalAudio SetInWater). Never recorded or driven.
  - Every `WaterEdge` component's `edge` and `inverseTransform` subtrees (lake: `WaterTrigger2_Lake/InverseTransform/WaterEdge`
    with the Fog, Reflect and Refract renderers, each Inside and Outside): WaterEdge.OnFastEventEx, dispatched from
    WaterCameraRendering.OnPreCull (AC/WaterCameraRendering.cs:228-238), places the edge from `Camera.current`, calls
    `edge.gameObject.SetActive` from the frustum test and writes `_Clip`/`_Disp` (AC/WaterEdge.cs:107-160). These objects are
    `camXf` and `camDriven` (live activeSelf per camera, like CameraLightSwap targets).
  - The `dustObject` of every WaterDust (position, rotation and scale from `Camera.current`, AC/WaterDust.cs:71-79, same
    dispatch). From code; no WaterDust was active in either probed view (L12).
  - The objects listed by the camera probe (`srec probe camera`, 3.4: freeze, move only the camera for 30 frames, list every
    set transform that changes; mandatory once per scene class, result stored in `record/camera_driven.tsv` and read by later
    records of that zone).
  MaterialInstances excluded from recording and replay (camera-dependent): on GameObjects with `BoyToShader` or
  `LensFlareSimple` (AC/BoyToShader.cs OnWillRenderObject), and every MaterialInstance a WaterEdge writes: the WaterQueue
  MaterialInstances under it (`_Clip`) and those under its `edge` (`_Disp`) (AC/WaterEdge.cs:51-71). preCullMain runs after
  WaterCameraRendering.OnPreCull (L1), so a replayed `_Clip` would override the per-camera value.
- Child decals (`DECAL_AO_*`, `DECAL_ReflectPoint`) of a moving object are part of it: they are children in the set and
  move with it (L9: the bale's `DECAL_AO_Box_Analytic_HayBale` looked like a shadow that follows the bale). Bounds used for
  the rotation threshold extent (2.3.2) count only shown, non-decal renderers (the chick's AO decals have about 1 m extents).
- Camera-driven activation: GameObjects carrying BoundsCullerRuntime, SubsceneCuller ContentRoots and CameraLightSwap targets
  (AC/CameraLightSwap.cs:110-154) get kind flag `camDriven`; their activeSelf is recorded on the info channel `camActive` and
  applied only with `play.cullers: "recorded"`. Forced cullers (2.3.3) keep their groups active for the whole record pass,
  so `camActive` normally only changes for unforced cullers.
- Characters: boy bones `actor.bones[i].transform` (FP/Actor.cs:112; bones are flat under the skeleton, :293-299) with owner
  reference `boy:bone:<transform name>`; huddle `huddle.cloth.bones[i].visualTransform` (FP/HuddleCloth.cs:113,239,
  FP/Huddle.cs:241) with owner `huddle:cloth:<i>`, plus `SkinnedMeshRenderer.bones` (ENG/SkinnedMeshRenderer.cs:7) of every
  huddle mesh (arms, legs) and the children of the cloth visual bones. These are instantiated at runtime (FP/HuddleCloth.cs:466,
  495, 585), so they are discovered live, never from the scene index.

#### 2.3.2 Sampling (once per frame in `preCullMain`)

- Stamp: sample index, game time `Time.timeDbl - t0`, `Time.realtimeSinceStartupDbl`, `LogicManager.stepCount`,
  `Time.frameCount`, fixed steps since the previous sample, `Time.timeScale`. The sample is the state the frame renders: after
  rigidbody interpolation, Actor.Update, animation, every LateUpdate, the record step (2.2 phase 9) and every component
  OnPreCull (L1).
- Transforms, space per object (OBJT flag `local`): objects whose parent is in the set are keyed in local space with
  `Transform.GetLocalPositionRotation` (ENG/Transform.cs:323), so rigid children of a moving object produce no keys and a
  hierarchy (chick `ChickA/bones/Root/LegL/FootL`, level171) keeps its limb lengths under interpolation; set roots and the
  flat Actor bones (FP/Actor.cs:293-299) are keyed in world space with `GetPositionRotation` (ENG/Transform.cs:310). Values
  go into preallocated arrays; `localScale` every sample only for objects whose scale changed in the first 30 samples
  (`scaleVariant`), every 30th sample for the others.
- Change detection by value against the last written key, never `Transform.hasChanged`: SetLocalRotationNoNotify writes
  (AC/Chicken.cs:488, FP/FakeRagdollJoint2.cs:370-385) may not raise it. Position key when the distance exceeds `posEps`
  (1e-4 m). Rotation key when the angle `4 asin(|q - q_last| / 2)` (computed in double from the float components; sign of
  q_last flipped when dot < 0; |q1 - q2| = 2 sin(angle / 4) for unit quaternions, revision 2 wrote `2 asin`, which is half the
  angle) exceeds `θ = clamp(posEps / extent, 2e-6, rotEps)` rad, extent = half the diagonal of the object's shown, non-decal
  renderer bounds (or of its subtree for objects without a renderer, 0.1 m minimum), so the tip error of a large slow
  rotator stays below posEps (5 m crane: 2e-5 rad). `2 acos(|dot|)` is unusable: it reads about 0.04 deg for identical float
  quaternions because |q|^2 < 1 (the lab's first metric, 206 false changes in L1). Scale on any change.
- Pre-key: when a track emits a key after one or more samples without a key, the previous sample's true value is emitted
  too, so a slow mover never holds and then jumps inside one interval (5.2 interpolates across gaps).
- Cut bit (step instead of interpolation) when, in one sample: `|Δp| > max(record.cut, 3 |v| dt)` with v = the previous
  sample's `Rigidbody.velocity` for non-kinematic bodies or the previous sample's displacement rate otherwise, dt = the sample interval
  (a 0.1 s hitch frame at 10 m/s is not a cut); `Rigidbody.isKinematic` turns false -> true (ChickenCaptured sets it and
  teleports the chick to the muzzle, AC/ChickenGunLogic.cs:394-402; the shot's true -> false at :383 is continuous motion and
  is not a cut); the object becomes active in hierarchy; its parent changes.
- Sample 0 is a full keyframe of every channel of every object (the baseline): a replay in a fresh session restores state that
  changed during setup (machine running, door open) without extra work.
- Flags (changes only):
  - `activeSelf` of every GameObject in the set (ChickenGun about 1300: level171 642, level204 624, level37 36 GameObjects).
    Containers count: the FSM `TriggerDisableChickens` deactivates `_GameLogic/Chickens` (level171 pathID 549: only
    Transform, ChickenManager, PointOfInterestBlocker; IDX/../fsm/level171__..._TriggerDisableChickens__FSM__2009.json), which
    hides all 24 chicks without changing their own activeSelf.
  - `Renderer.enabled` of every renderer, `Light.enabled` of every light, `Rigidbody.isKinematic` (the game toggles it,
    AC/ChickenGunLogic.cs:383,396).
  - The replay derives `activeInHierarchy` from the recorded activeSelf chain (2.4.4).
- Particles: for every ParticleSystem with `particleCount > 0` now or in the previous sample, `GetParticles(buf)`
  (ENG/ParticleSystem.cs:399) into a per-system `Particle[maxParticles]` allocated at Begin, then an unsafe raw copy of 84 bytes
  per particle. Field layout from the stub (ENG/ParticleSystem.cs:17-43, sequential): position 0, velocity 12, animatedVelocity
  24, axisOfRotation 36, rotation 48 (radians), angularVelocity 52 (radians/s), size 56, color32 60, randomSeed 64, lifetime 68,
  startLifetime 72, emitAccumulator0 76, emitAccumulator1 80. Begin asserts `Marshal.SizeOf(typeof(ParticleSystem.Particle)) ==
  84` (84 in the game, L4) and refuses particles otherwise. Also recorded (info): isPlaying, isPaused, emissionRate. Get + Set
  costs 0.034-0.155 ms per 1000 particles (L4).
- Lights: enabled, intensity, color, range, spotAngle, cookie (texture table id), shadows, useStaticShadows,
  staticShadowsDirty (ENG/Light.cs:18-196). Changes only.
- MaterialInstance: the `.value` of every entry of FloatProperties, ColorProperties, VectorProperties, TextureProperties
  (FP/MaterialInstance.cs:99-105, :262-268; textures as table ids). Lists can grow at runtime (`GetOrAddFloatProperty`,
  FP/MaterialInstance.cs:351; `_PDTime` added by AC/FlameTrail.cs:84), so new property names are written inline. Changes only.
- Material swaps (P0): `Renderer.sharedMaterial` of every renderer in the set by reference compare, `sharedMaterials` (allocates)
  every 10th sample for renderers with more than one material, as material table ids. Writers: SwitchLight swaps
  `surface.material` (AC/SwitchLight.cs:42-66; level171 has two: `LeverReturn/LeverLight`, `LeverReturn/LeverLightExtra`),
  FSM SetMaterial actions.
- Rope geometry: for every RopeLineRenderer in the set (level171: `RopeCrane_BoundsCuller/CraneRopeLong`, with a Rope
  component), the private `blendRope` (AC/RopeLineRenderer.cs:543) `vertices[i].position` (BlendVertex.position, :41) by
  reflection (accessors built once at Begin), changes only. Draw passes exactly these to `LineRenderer.SetPosition`
  (:280-283) or the spline and mesh paths; the top point is `Rope.topPos`, derived from transforms (FP/Rope.cs:141-148).
- Blend probes: `ColorBlendProbe.GetCurPos()` (FP/ColorBlendProbe.cs:352-360) of every probe in the set (the boy carries two).
  Changes only.
- Render state: the fields of `FogAmbientBlender.currentBlendVal` (AC/FogAmbientBlender.cs:6; field list stored in the header,
  read by reflection once into accessors), and (P1) the D11PDPostCombined scalars the look component touches (gamma_gamma,
  post_inBlack, post_inWhite, post_gamma, post_blacklift, saturationAdjustment, wideparm_bloomIntensity, hdrparm_bloomIntensity,
  waterDist, colorDistortionPx720p, barrelValue). Changes only.
- Shader state (material round 2026-10-25, `MC/SceneShaderState.cs`, `record.shaderState`, default true): what the game
  sets on shader globals, shared materials and renderer property blocks during a take. Without it a replay keeps the
  end-of-take values: Shader_SetHuddlePosition.Update returns at deltaTime 0 (FP/Shader_SetHuddlePosition.cs:94-99), the
  arm and leg controllers that write `_DamageLimb` are neutralized (2.4.2), and a ShaderGlobal ColorBlendProbe keeps its
  last active blend-box set because trigger enter/exit stops with physics (FP/ColorBlendProbe.cs:362-458,
  FP/MaterialInstanceBlendBox.cs:100-158), so a probe position alone (the probe channel above) does not re-derive the
  zone values once the huddle crossed a box. Writers were found by grepping the decompiled code for Shader.SetGlobal*,
  Material.Set*, SetPropertyBlock and ReplaceProperty*, and kept when components_summary.tsv has instances:
  - Globals. Unity 5.0.4 has no `Shader.GetGlobal*` (ENG/Shader.cs) and no native global can be read back, so the
    recorder evaluates each script writer at preCull and stores the value it left this frame: ShaderGlobal ColorBlendProbes
    (`_PDHuddleAmbient` colour, `_PDHuddleToon` float on HuddleFXProbe, the only names game-wide; the probe's own reverse
    box loop with GetBlendValue / GetBlendProperties, which are side-effect free; no box or a box that left the list = base
    value, active boxes without an override = no write, so the value holds), Player2GrassShader (`_Player2GrassPos`,
    `_Player2GrassPush` from its fields) and Player2PlantShader (`_Player2PlantPos0..4`, `_Player2PlantPush`). Writers are
    found in the record set and scene-wide (FindObjectsOfType at the record set build, then inline on joins). Not
    recorded, because they are per camera or per render and stay live: PDReflectionCuller `_ReflectionColor`,
    PDReflectionUtils `PD_fogFacts`, `PD_fogWaterColor`, `PD_reflectionPlaneView/World` and the global SOFTPARTICLES
    keywords (planar reflection renders), DepthBufferDownsample depth and dither globals (`_RealTime` comes from
    MultiCamLook and the record clock, 2.4.7), PDPostCombined `_ExposedBloom`, PDVelocityBuffer `_VelocityPrevVP`.
    Exactness [unverified]: one frame differs when a blend box leaves the list in the same frame as a higher box
    overrides (the game restores the base after that box; the next frame agrees again).
  - Materials, read back with `Material.GetVector`: Shader_SetHuddlePosition writes `_MainCharPosWorld` and `_Aspect` on
    every `mat[]` (`CHARACTER_Huddle_2`, `CHARACTER_Huddle_Hair`) and the nine bruise vectors `dam_top`, `dam_bottom`,
    `dam_leftTop`, `dam_leftBottom`, `dam_rightTop`, `dam_rightBottom`, `dam_centerTop`, `dam_centerLeft`,
    `dam_centerRight` on `mat[0]` (x = impact damage from HuddleCollisionAnalyzer, y = water, z = blood splat, w = burn,
    FP/Shader_SetHuddlePosition.cs:94-153). A property the material does not have yet is retried every 30 samples.
  - Property blocks, read back with `Renderer.GetPropertyBlock` into one reused block: `_DamageLimb` of every
    HuddleArmControl2.armRenderer and HuddleLegControl.legRenderer (FP/HuddleArmControl2.cs:1530-1534,
    FP/HuddleLegControl.cs:732-740), `_Intensity` of the BoyOcclusionHandler spheres (HuddleSphereSetBoyNaked changes it).
    Only renderers in the record set; inactive ones are not read after their first value.
  - Not covered (no instance in the shipped data, or not on a recorded renderer): SoftBodyHelper, HuddleAmbientLight,
    ParticleSystemScaling, BoomScript, ConveyorBelt, AnimatedUVs, ContinuousTracks, FalloffMinMax,
    ScreenBloomLightSourceStandard (0 instances); SentryGun `_Illum` / `_TintColor` (level74 only); cookie render
    materials of AnimateCookieTexture / SubmarineCookieTexture; the three FSMs with SetMaterial*/SetTextureOffset actions
    (one-shot UV offsets on worker helmets and the lineup crowd). MaterialInstance keeps its own values in its property
    lists, which the MaterialInstance channel above records; its block is written by the setters the replayer calls.
  - Channels are defined inline (section 17: scope global / material / block, the material name or the kind 9 renderer
    id, type float or vector, property name) in the sample that first carries a value, values are change-only
    (section 18, exact float compare, every known channel in sample 0). No allocation per sample. record.json
    `shaderState` lists the globals, the material names with their properties, block renderers and values, writers,
    keys and the cost (mean, p99, max against a 0.25 ms budget, samples over budget); `sampleCost.channels.shader` too.
- Virtual channel (filled by `SetVirtual(ctx)` from the record loop in the same frame): the whole `GameCamState`
  (MC/MultiCamTypes.cs:632-651), the camera transform and fieldOfView read at preCull, boy `pos3`/`velocity3` (FP/Boy.cs:279-309)
  and state class name, VisualBoy state class name, huddle pos3/velocity3/state, cameraBlendProbe world position,
  `LogicManager.stepCount`.
- Spawned objects: every sample compares childCount and child instance ids of `_Dynamics` and `_SubsceneDynamics`; a new child
  -> `AddTree` and an inline OBJA record; a vanished one -> OBJD. Spawns come from Dynamics.Create / DynamicsSubscene.Create
  (FP/Dynamics.cs:58-63, FP/DynamicsSubscene.cs:47-50, FP/VfxController.cs:53-65,121-130) and can be destroyed later
  (FP/VfxControllerManager.cs:196). P2 `record.keepSpawned`: at spawn, Instantiate a disabled clone under `InsideDev_ReplaySpawns`
  with every MonoBehaviour disabled, so the replay can show it after the original is destroyed.
- Objects a mod creates during the take (added 2026-10-25): a new direct child of a container is added in the sample of
  the frame it appears (above); a new child of a spawned transform (an effect instantiated or reparented under a worker's
  bone) also in that sample, by a child-count check of every spawned transform per sample; a new child of any other
  recorded transform within `rescanEvery` samples (staggered child-count check, 1/rescanEvery of the set per sample).
  Objects created elsewhere are recorded from the next sample once the mod calls the public
  `SceneRecorder.Register(UnityEngine.Object)` (Transform, GameObject or Component; local to its parent when the parent is
  recorded, else world space). Names starting `InsideDev` and `record.exclude` paths stay out. Inactive objects are
  recorded (flags carry activeSelf), so hide-and-show is replayed; a destroyed spawned object is gone for the replay too
  (Ref null, OBJD), so mods hide instead of Destroy: the first destruction is a recorder warning, all are listed in
  record.json `destroyedSpawned` / `destroyedSpawnedPaths`.
- Events: `Event(kind, name, detail)` writes event rows into the stream (fired spec events, cues, load holds, warnings).
  Mods and agents post moments with the public `SceneRecorder.PostEvent(name, detail)` (or the bridge command
  `srec event <name> [detail]`, allowed during mcam runs): a kind `event` row in the next sample, so `@name` resolves in
  every replay (`EventTime` = first row of that name; names use letters, digits, `_` and `.`). The static
  `SceneRecorder.posted(name, detail)` reports every valid post, recorded or not (spec events of type `posted`, MULTICAM.md
  8.2). Every kind `event` row is listed with its sample and game time in record.json `eventTimes` (mcam record: run.json
  `record.event_times`); replay run.json `events` lists posted names that no spec event declares with type `posted`.
- Mesh swaps (added 2026-10-25): `SkinnedMeshRenderer.sharedMesh` of every recorded skinned renderer is compared each sample it is
  read; a change writes an event row of kind `mesh` (name = renderer object id, detail = new mesh name, tab, previous name),
  so the file format is unchanged and older readers skip it. `SceneTracks.MeshAt(id, t)` turns the rows into a step
  channel (the previous name before the first swap); the replayer asserts that mesh before culling every frame (also in
  ApplyNow and the final sample), resolves names through the recorder of the same session, the Begin meshes and then
  `Resources.FindObjectsOfTypeAll<Mesh>`, and puts the Begin mesh back between passes. record.json `meshSwaps` counts them.
- Cost [estimate]: ChickenGun about 1300 transforms plus 80 bones: 0.3-0.6 ms per sample; flags and shared materials 0.1 ms;
  particles 0.5-2 ms at peak firing. No per-frame allocation: block buffers are pooled; the writer thread takes blocks from a
  bounded queue of 64 (blocks the main thread when full and logs it). No GZipStream (MonoPosixHelper is missing,
  MULTICAM_DESIGN.md 3.10).
- Measured (R1, 2026-10-25): the ChickenGun auto set is 4767 objects (4002 transforms, about 2000 of them in the inactive
  `_Boy/.../Themes` subtree, 510 renderers, 91 particle systems, 92 MaterialInstances); sampling took 2.3-3.4 ms mean, p99
  4.2-7.7 ms. Two exact savings since then: values bitwise equal to the last key or the previous sample skip the double
  distance and angle math, and quiet transforms skip their reads. A transform is quiet-eligible when neither it nor a
  recorded ancestor has a Rigidbody, Animation, Animator, Actor or one of the game's NoNotify writers (Chicken,
  FakeRagdollJoint/2, FakeRope, AnimcontrolledCharacter, AnimControlCustomCode, SleepWalker, HuddleArmControl2, HuddleLegControl,
  Boy, VisualBoy, Huddle, HuddleCloth) and it is not a camXf, spawned or character bone object. After 8 samples without a key
  (and past the 30-sample scale warmup) it goes dormant: the recorder clears `Transform.hasChanged` (the game and the mod
  never read it) and from then on reads the transform only when the flag is set or on a staggered sweep every 32 samples,
  committing the unchanged value otherwise (so pre-keys stay exact). activeSelf is still read every sample. A sweep that
  finds a change without the flag is a miss (warning, record.json `quietTransforms.misses`; the object leaves the quiet set);
  four wakes in a row without a key (a moving parent) send it back to normal reads for 120 samples. This is the one place
  `hasChanged` is used: as a wake hint for objects that no NoNotify writer touches, checked by the sweep. record.json
  `sampleCost.channels` gives mean, p99 and max per channel (spawns, transforms, renderers, lights, flags, particles,
  materials, probes, ropes, fog, virtual, write) for the next budget decision.

#### 2.3.3 Record pass preparation (`RecordPrep`, saved and restored in finally)

- `Animation.cullingType = AnimationCullingType.AlwaysAnimate` on every Animation in the set, `Animator.cullingMode =
  AnimatorCullingMode.AlwaysAnimate` on every Animator (ENG/Animation.cs:96, ENG/AnimatorCullingMode.cs). Without it chicks the
  game camera cannot see stop animating and are recorded with stale poses.
- `record.forceCullers`: `"zone"` (default: every BoundsCullerRuntime under the roots), `"planned"` (cullers whose bounds
  intersect a planned camera frustum from `rig.Plan`, MULTICAM_DESIGN 3.9), `"list"` (`record.cullers` names), `"none"`. Forcing
  = `valid = false` (FP/BoundsCullerRuntime.cs:62) + `RefreshRuntimeState()` (:335); UpdateVisible then keeps the group active
  (`IsVisble() || !valid`, :459-466). The names of groups that became active are logged (their OnEnable side effects run).
  L7: forced groups go active and render the next frame (3_Interior_CULL inactive -> active with 14 renderers visible); the
  OnEnable types that run are BoundsCullerRuntime, QueueInstance, MaterialInstance, VfxParticleController,
  ParticleSpeedScale, DecalSpecularParams, PlayMakerFSM, ParticleSystem.playOnAwake and Animation.playAutomatically, with 0
  sound posts, 0 SignalOut fires and 0 FSM state changes.
- Handover: `RecordPrep.Restore` restores Animation/Animator culling but not the forced cullers and not the keep-loaded set
  when the replay follows in the same session (`mcam take`, `srec record stop keep`): `RecordPrep.HandOver()` passes both
  to the replay session, which keeps `valid = false` until replay End (2.4.8). A plain `mcam record` restores everything.
- Keep-loaded: the v2 prepass (MULTICAM_DESIGN 3.9) with the planned poses of the spec cameras, before sample 0; the culler list
  goes into the header so a replay in another session keeps the same subscenes. RecordBody owns the AlwaysLoaded handles
  itself: it never runs the v2 prepass End, which releases them (MULTICAM_DESIGN 3.9: "End: RemoveAlwaysLoaded").
- Particle off-screen culling cannot be disabled in 5.0 (no cullingMode API) [speculation]: side-camera-only particle systems
  may be recorded stale. Flagged per system in record.json when `particleCount` stays 0 while `isPlaying` is true.

#### 2.3.4 Recording clock (`record.clock`)

Start (both `srec record start` and the mcam record pass): sample 0 waits `record.settleFrames` (default 12) frames after
the kept subscenes are loaded and active, because LateAwakeNode content under them switches on 2-10 frames after its
subscene activates (initializationFrame <= 10). An early test take recorded CEO_Room (wall, desk, CEO) off at samples 0-1; record.json
`settle` gives the frames waited. `tools/srec_tool.py report <record> [--spec]` then lists every recorded event, the spec
events (fired, sample, time, delta to expect, posted matches) and the `record.checks` (first active / visible sample and
spans, window verdicts) in record_report.json / .txt; exit 1 on a failed check.

- `"real"` (default): captureFramerate 0, timeScale 1, fixedDeltaTime untouched (1/60), frame pacing locked to 60 Hz. The v2
  session push sets vSyncCount 0 and targetFrameRate -1 because `SessionSpec.vsync` defaults to false (MC/MultiCamTypes.cs:422,
  MC/MultiCam.cs:2115-2120), and the v2 Preroll and Freeze set `Time.captureFramerate = plan.segs[0].rate`
  (MC/MultiCam.cs:1114, :1127). RecordBody therefore never calls the v2 Preroll, Freeze or Hold; after the session push it runs
  its own real-time preroll and sets `FramerateController.automatic = false`, `Time.captureFramerate = 0` and either
  `FramerateController.SetVSyncCount(1)` + `QualitySettings.vSyncCount = 1` + `targetFrameRate = -1` when
  `Screen.currentResolution.refreshRate == 60` (ENG/Screen.cs:40, ENG/Resolution.cs:35), or the own frame limiter otherwise
  (vsync on a 144 Hz display would sample at 144 Hz). R1 (2026-10-25, hybrid-GPU laptop, displays at 60 and 75 Hz) showed
  that neither choice can be trusted: vSyncCount 1 followed the 75 Hz monitor although `currentResolution` said 60 Hz
  (engine interval median 13-14 ms, 26 % in 15-18.5 ms), and `targetFrameRate = 60` ran at a median of 18.1 ms (43 %). So the
  preroll measures the engine frame interval (`Time.unscaledDeltaTime`) after its first 5 frames; vsync is kept only when
  the median of at least 40 intervals is within 0.5 ms of 16.67 ms and 90 % lie in 15-18.5 ms (a short preroll is extended
  until 40 intervals exist). Otherwise, and on every display that does not report 60 Hz, the record uses the limiter:
  vSyncCount 0, targetFrameRate -1, `timeBeginPeriod(1)` for the record, and at the end of every record frame (after
  WaitForEndOfFrame, the grab and the cues, before Present) a wait for the next point of a fixed 1/60 s grid (Sleep(1) while
  more than 2.5 ms are left, then a Stopwatch spin). A frame up to 1.5 ms past its grid point keeps the grid (the next wait
  is shorter, both intervals stay in 15-18.5 ms); a later one re-anchors the grid. record.json `pacing` reports `mode`
  (vsync or limiter), the `calibration`, the limiter counters and the engine interval statistics `dt_*` next to the record
  step intervals `frame_ms_*`; R1 judges `dt_*`. Every record frame
  asserts the three values; a change is restored and logged as event `pacing`. Update-phase logic sees the usual deltaTime,
  game time tracks wall time, the Wwise capture is in sync by construction. Samples are irregular (one per rendered frame);
  the interpolator handles that. `record.res` (default [0,0] = keep the output resolution) can lower the resolution of the record pass so frames stay
  below 16.7 ms; the record view is never used for output (except the golden test). Frames over 0.1 s lose game time
  (`Time.maximumDeltaTime`, ENG/Time.cs:80) and are flagged `hitch`: a `hitch` event with the lost game time, and
  record.json `drift.hitch_loss_ms` and `drift.residual_ms` (signed drift minus hitch loss).
- Nothing expensive runs inside the recorded frames (R1: a cue stalled one frame 0.3-1.5 s, the record set build and the
  full keyframe made the first frame a hitch, mod background scans and a 288 ms GC ran during the record): cue commands are
  bound before sample 0 (`Remote.Bind`: object, component and method of a `call`; other verbs and missing targets run
  through the bridge with a warning, and every cue logs its cost), the mod's background scanners (object database,
  reference index, sound library, audio database and reference scan, hidden scans, code dependencies, spawn cache, live
  savepoints, timed-audio and audio catalog scans) wait while any mcam run, srec recording or srec viewer is active and
  until streaming has been quiet for 3 s (`DevCore.BackgroundOk`, round 1: they ran on their own timers during setup,
  preroll and the replay passes, 30-170 ms each), a `GC.Collect` and `SceneRecorder.Prebuild` (record set scan and object table)
  run 3 frames before sample 0, and `Begin` in the frame of sample 0 only opens the writer and hooks preCull.
- `record.captureGame` grabs use the native async readback (InsideReadback) and one ffmpeg mjpeg pipe (full-range 4:4:4,
  frames renamed to `record/game/<sample:D5>.jpg` at the end; `mcam golden` grabs through the same path so both sides of
  replaydiff share one encoder, record.json `game_frames_encoder`, golden run.json `golden.encoder`); without the plugin
  (or `output.readback: readpixels`) they are synchronous (ReadPixels + EncodeToJPG on the main thread, about 34 ms per
  grab at 1280x720 in R1, more at full resolution). A record with captureGame stays a golden reference, never pacing grade:
  record.json `pacing.grade` is `golden` (else `pacing`) and the `nograb_*` statistics leave the frames after a grab out.
  The take record runs without captureGame; the golden test records separately (the take spec with `captureGame: true`).
- `"fixed"`: captureFramerate = `record.rate` (60, 120 or 240) and the frame limiter at 1/rate (targetFrameRate -1; the
  targetFrameRate path measured 18.1 ms per frame at 60 in R1). Regular samples, exactly
  rate/60 frames per physics step; for silent takes, determinism experiments and dense legacy-animation sampling. Audio capture
  is allowed only at rate 60 and only when the drift check passes (6.2).

### 2.4 SceneReplayer (PLAY, `MC/SceneReplayer.cs`, `MC/SceneTracks.cs`)

#### 2.4.1 Load and resolve

- `SrecReader` decodes the object table and every non-particle channel into sparse tracks; particle frames stay in the file and
  are read on demand through the footer offset index into a two-frame cache.
- Same session (the recording was just made): objects keep the recorder's references, validated by instance id and non-null.
- Another session: (1) owner references: `boy:bone:<name>` through `actor.GetBone(name)` (FP/Actor.cs:190), `huddle:cloth:<i>`
  with a name check; (2) path walk from the root name with the same-name ordinal chain (survives inserted siblings with other
  names; `Chicken` x23), verified against the sibling-index chain and the component type signature; (3) exact
  `Inspector.PathOf` (MOD/Inspector.cs:134). Refuse when more than `play.maxUnresolved` (1 %) of the objects with more than one
  key are unresolved or ambiguous; report every one. Kind `spawned` objects are excluded from that fraction and reported in
  their own list: `_Dynamics` is cleared on every load (`Dynamics.Clear()` in GameManager.Reload, FP/GameManager.cs:446), so
  they never exist in a fresh session (shown only with `record.keepSpawned`, P2). Every object added inline (OBJA: spawns,
  `Register`, subscenes that joined during the take) counts as spawned: written and shown only between its add and delete
  samples; in a fresh session joined subscene objects still resolve by path when that subscene is loaded.
- Fresh-session baseline: after `play.setup`, a diff of the live state against sample 0 (position > 1 cm, rotation > 1 deg,
  activeSelf, Renderer.enabled, sharedMaterial, Light values) is written to `replay_baseline.tsv` with the 50 largest
  differences in log.txt; the file goes into the output folder of the run that loads the recording (`<out>` for replay,
  `<out>/golden` for golden), never into the source take. Same session means: any recorder of this game session wrote the
  folder and no level load happened since it ended (`SceneRecorder.SessionRecording`, GameManager.LoadCount), so a manual
  `srec record` after a take does not turn later replays of the take into fresh-session reloads (round 1).

#### 2.4.2 Freeze and neutralize (`Begin`; everything saved, restored in `End`)

`Begin`/`End` run once per replay session (all passes of one `mcam replay`); `BeginPass`/`EndPass` (2.4.9) run around each
camera pass.

- `Time.timeScale = 0` and `GameManager.TimeScale = 0`, re-forced every frame (GameManager.LateUpdate can rewrite it,
  FP/GameManager.cs:119-132,545-566; v2 already does this, MC/MultiCam.cs:1618-1628).
- CameraScript paused through CameraControl (MC/CameraControl.cs:171-198); the camera pose is written with `CameraControl.Set`.
- Streaming frozen: the keep-loaded set (handed over from the record pass, or the header's `keptCullers` plus a prepass for the
  replay cameras' planned poses in a fresh session) stays added until End; after it is loaded and active,
  `SubsceneCullingManager.instance.CullingActive = false` (FP/SubsceneCullingManager.cs:78-92; the game does the same during
  loads, FP/GameManager.cs:347,477). With culling off, `Update` only refreshes the view frustum (:630-640) and never calls
  PerformLoadUnload, so a moving replay camera cannot load or activate subscenes (no Start/PreAwake while frozen, no hitches).
- Cullers never toggle: every zone culler keeps `valid = false` (handed over, or set at Begin from the header's
  `forcedCullers` followed by `RefreshRuntimeState()`, :335). Zone cullers the record did not force (`forceCullers` planned,
  list or none) are set the same way at Begin; their groups activate once there (logged), before pass 1, identically for
  every pass, and their recorded `camActive` feeds `cullers: recorded`. `RuntimeCallbacks.ExecuteInEditModeIncluded.update -=
  BoundsCullerRuntime.UpdateBoundsCullers` (public static, :236; AddBoundsCuller re-hooks it only when the culler count was
  0, :169-173, so SceneHooks repeats the `-=` every frame, a no-op when absent). Per-camera visibility is computed in
  `preCullMain` (2.4.4) without SetActive. Reason: `SetVisible` calls `gameObject.SetActive` (:444-455), which runs
  OnDisable/OnEnable of the group content: DirtyStaticShadowManager queues static-shadow re-renders on `staticSetVisible`
  (FP/DirtyStaticShadowManager.cs:34, :54-57), PlayMakerFSMs stop and resume (1b_IndoorDetails_CULL holds 2, 1c_LadderArea_CULL
  1), particles and animations restart, and the state then differs between passes.
- Rigidbodies of the set, except the boy logic aux body and `camXf` objects (AudioCameraProbe): save isKinematic,
  interpolation, velocity, angularVelocity; set `interpolation = None`, `isKinematic = true` (Unity 5.0 has no Physics.Simulate
  or autoSimulation, ENG/Physics.cs). L3: at timeScale 0 a pose written in lateUpdate persists to the same frame's and the next
  frame's onPreCull (delta 0 m, 0 deg) in all four combinations of Interpolate/None and dynamic/kinematic, for the chick and
  the bale; None + kinematic stays as a safeguard.
- ParticleSystems with any recorded frame: save isPlaying/isPaused, then `Pause(false)` (ENG/ParticleSystem.cs:521).
- Neutralize table (per component type; writers that land after the -400 hook and ignore deltaTime). `srec probe` (3.4) is
  mandatory once per new scene class and its findings extend this table:

  | Type | Why | Neutralization |
  |---|---|---|
  | HuddleArmControl2 | LateUpdate order 10, no enable hooks (FP/HuddleArmControl2.cs:1456-1470) | `enabled = false` |
  | HuddleLegControl | LateUpdate (FP/HuddleLegControl.cs:716-735) | `enabled = false`; its OnEnable calls EnableAnimations on restore (:316-319), logged |
  | CrowdDude | `UmbrellaLateUpdate` (order 0 via UmbrellaExecutive.LateUpdate, FP/UmbrellaExecutive.cs:93-104) rewrites `dudeHolder` rotation and `headTransform.rotation` every frame without a deltaTime gate (AC/CrowdDude.cs:807-819); after the -1 bake, so skinned heads stay frozen | removed from its umbrella, not disabled: `Umbrella<CrowdDude>.instance.Remove(umbrellaHandle)` (FP/Umbrella.cs:82; handle = protected field `UmbrellaBehaviour.umbrellaHandle`, FP/UmbrellaBehaviour.cs:7, by reflection) and `Insert` (:72) at End with the new handle written back. Disabling would run CrowdDude.OnEnable at restore, which draws `UnityEngine.Random` and calls `animator.Play` (AC/CrowdDude.cs:307-389) |
  | RopeLineRenderer | Update runs Step + Draw; Step is not a no-op at deltaTime 0 (2.4.3) | `enabled = false` (the class has no OnEnable/OnDisable); the replayer draws it (2.4.3) |
  | Hair, FakeRagdollJoint2 | LateUpdate umbrellas with their own deltaTime gates (AC/Hair.cs:292-296, FP/FakeRagdollJoint2.cs:390) | none |

- ColorBlendProbe: save and set `useOverridePosition = true` (public fields, FP/ColorBlendProbe.cs:61-63).
- Left running on purpose: legacy Animation, Animator, Actor, VisualBehaviour, PlayMakerFSM, CameraScript.Update. Their frozen
  writes land before the -400 hook and are overwritten; legacy Animation re-samples every frame at timeScale 0 (L3), so every
  object under one is written every frame (2.4.3). Never disabled: LogicManager (OnDisable clears its lists,
  FP/LogicManager.cs:65-71) and PlayMakerFSM (OnDisable calls `Fsm.Stop`, which sets Finished and, with RestartOnEnable,
  exits and resets the state; PM/PlayMakerFSM.cs:301-308, PM/HutongGames.PlayMaker/Fsm.cs:1150-1162, 1271-1296).
- VisualHuddleCloth stops writing while `DebugMap.isPaused` (|timeScale| < 0.001, FP/DebugMap.cs:189; FP/VisualHuddleCloth.cs:119-170),
  Hair skips at deltaTime 0 (AC/Hair.cs:292-296), FakeRagdollJoint2 skips at deltaTime 0.
- Monitor (`play.monitor`, default true): counts SignalOut fires, FSM state changes and Finished toggles, culler SetActive
  calls (a handler on `BoundsCullerRuntime.staticSetVisible`, FP/BoundsCullerRuntime.cs:125), subscene activations
  (`SubsceneCuller.StaticEvents.postActivate`) and sound posts on recorded objects during the replay (expected 0 apart from
  GlobalAudio pause/resume, which are counted apart as `soundPostsExcluded`); written to run.json and log.txt. Every hit is
  named (kind, frame, pass, T, object path, event text) in log.txt and run.json `monitor.hits` (first 50).
- Kept subscenes (round 1, R8): before Begin, replay and golden add the header's `keptCullers` to the session's keep-loaded
  set and wait (up to `stream.timeout`) until every one is loaded and active and `AllCullersFinished`, so Begin forces their
  cullers and freezes culling with them active; after Begin the run also waits while the replayer reports kept subscenes
  still pending.

#### 2.4.3 Apply at replay time T

- End of frame k-1 (runner, after ReadPixels): `replay.T = T_k`, `CameraControl.Set(pose_k)`, `MultiCamLook.clock`, TAA values.
- `preUpdate` (frame k): activation states for T (2.4.4); probe-carrying roots written for T_k so Update-phase readers see the
  current step: the boy and huddle roots (`Player2GrassShader.Update` reads `ScriptGlobals.boy.transform.position` for the grass
  push globals, FP/Player2GrassShader.cs:35-58) and every TransitionProbe transform (TransitionBoxManager reads them in
  `ExecuteInEditModeIncluded.update` and rewrites `FogAmbientBlender.currentBlendVal` and the post boxes,
  FP/TransitionBoxManager.cs:148-158, AC/FogAmbientBlendBox.cs:56-69; the public `update` event fires first,
  FP/RuntimeCallbacks.cs:101-107); `ColorBlendProbe.overridePosition = probe(T)` so a probe's UmbrellaUpdate blends its
  MaterialInstances for T (FP/ColorBlendProbe.cs:362-400; the shader globals it also writes are only right while its
  frozen blend-box set still holds, so recordings with shader state overwrite them in preCullMain); re-assert the camera
  pose.
- `lateWrite`: keyed transforms, parents before children (stable sort by depth): `SetLocalPositionRotation` for `local`
  objects (ENG/Transform.cs:312), `SetPositionRotation` for world objects (:299), `localScale` for scale tracks; particles
  (2.4.5); rope vertices then `blendRope.Draw` by reflection with the component's own
  mode and arguments (mesh mode when `meshOutput` is set, SplineUniform when `lineDivisions > 2`, else Line,
  AC/RopeLineRenderer.cs:682-698) after forcing the private `postSleepDraw = false` (the CheckSleeping early-out, :257-262,
  would skip Draw for a sleeping last body). L11: crane rope (SplineUniform, 15 vertices, 1.14 m swing) with the component
  disabled, 80 of 80 Draws, 0 exceptions, silhouette within 1-2 px of the record frame at every sample (2 px tolerance in R3).
  Draw also writes `bottomAttachedObject.transform.position` (:397), which the next lateWrite overwrites with the recorded
  value. Write-once rule: in the recording's own session, objects with one key are written once in Begin and checked for
  drift at preCull every 10th frame (a drifting object is promoted to every-frame writes and logged), except objects with an
  Animation or Animator on themselves or an ancestor and Actor bones, which are always written every frame (legacy
  Animation re-samples every frame even at timeScale 0 and overwrote a written bone 20 deg back the next frame, L3); in
  another session every keyed object is written every frame, because frozen legacy Animation, Actor and FSM writers
  re-assert the load-state pose each frame.
- `preCullMain` (runs after every component OnPreCull, L1; it never writes `camXf` objects or excluded MaterialInstances, so
  the per-camera writes made there survive): culler visibility for this camera, then the Renderer and Light enabled flags from
  the visibility formula (2.4.4; before culling, so this render uses them); read back every driven rigid (non-skinned) transform against the
  value written in `lateWrite` and the recording at T (this read-back is the R3 overwrite metric); re-write and count a
  conflict per object (probe mode names the components with a LateUpdate on that object or its parents); MaterialInstance values
  through `SetFloatProperty/SetColorProperty/SetVectorProperty/SetTextureProperty` (FP/MaterialInstance.cs:306,413,520,626; the
  setters compare and only write changes, missing properties are created by name with GetOrAdd*Property); shader state
  (content version 2 recordings; 2.3.2): every recorded global with `Shader.SetGlobalFloat/SetGlobalVector` and every
  material value with `Material.SetFloat/SetVector` every frame (the frozen ColorBlendProbe rewrites its globals in
  Update), property blocks on change or full apply (`GetPropertyBlock`, `SetFloat/SetVector` of the recorded names
  only, `SetPropertyBlock`, so other names in the block stay); same session: the recorder's Material references, else
  the material by name (renderers of the set first, then Resources); lights;
  `FogAmbientBlender.currentBlendVal` (applied later in the same render by FogAmbientCameraBlend.OnPreRender,
  AC/FogAmbientCameraBlend.cs:21-37); D11PDPostCombined values (P1; MultiCamLook takes them as its base); shared materials;
  a renderer that already shows a material of the recorded shared-material name keeps it (instances made by
  `Renderer.material` share one name, e.g. a texture offset a mod gave each worker, so the name table cannot pick the
  right one); for each object whose track has a cut between the previous render and this one, `D11VelocityBufferTag.wrapAroundFrame =
  true` on every tag in its subtree (the chick tags sit on the child `Chicken/ChickA/mesh`, useSkinnedMesh 1, while the cut is
  on the chick root; velocity-tag list extracted from the level data) (ENG/D11VelocityBufferTag.cs:17; the native tag clears it
  after one render, EXE accessor 0x14033cdb0).
- `ApplyNow(T)` (plan pass, no render): performs the lateWrite and preCullMain work immediately.
- Write point: `lateWrite` for every transform, skinned bones included (before the tag bake at -1). L2 (bale at 28.4
  px/frame, chick alone at 48 px/frame): writes at lateUpdate -400, at an order-0 LateUpdate and at onPreCull all render the
  written pose in the same frame (12 of 12 frames each, PSNR against a static render of the written pose 41.8-42.7 dB, against
  the previous pose 29.9-30.9 dB), so the preCull re-assert of rigid objects is safe and a bone written there would not lag
  either. Bones stay in lateWrite because animation runs before it (2.2 phase 4) and that keeps the -1 velocity bake on the
  replayed pose.

#### 2.4.4 Activation and visibility policy

Recorded renderer and light visibility (and sharedMesh swaps) are applied twice per frame: at the end of lateWrite, before
skinning and culling, so a renderer the recording switches on draws in that frame (in an early take the CEO blinked out on the
pop frame when only the pre-cull applied it), and again in preCullMain with the camera's culler emulation.

- One owner: SceneReplayer is the only writer of `Renderer.enabled` and `Light.enabled` of set objects during the replay. Saved
  once at Begin, restored once at End. Per frame, for every renderer and light:
  `visible = recordedEnabled(T) && recordedActiveInHierarchy(T) && cullerVisible(camera)`, where
  `recordedActiveInHierarchy` walks the recorded activeSelf of the chain up to the set root (the part above the root uses the
  live `activeInHierarchy`), and `cullerVisible` is false only inside a culler group that the current camera cannot see
  (below). Changes only; the preCull pass verifies and counts foreign writes as conflicts.
- Recorded active in hierarchy at T, live inactive: the topmost live-inactive ancestor in the set is activated per
  `play.activate`:
  - `"quiet"` (default), only for objects seen active in this session (by the recorder, or at the replayer's Load snapshot):
    set `enabled = false` on every Behaviour of the subtree except MaterialInstance, including legacy `Animation` and
    `Animator` (playAutomatically and Animator rebind would otherwise run on activation), saved; then `SetActive(true)`; then
    `Pause()` and `SetParticles` + `Simulate(0f, false, false)` (or `Clear()`) on every ParticleSystem of the subtree
    (ParticleSystem is a Component, not a Behaviour, so `playOnAwake` still fires, ENG/ParticleSystem.cs:9, :220; the
    Simulate call is required, 2.4.5). Hide again with `SetActive(false)`; restore the
    enabled flags while the object is inactive, so no callbacks run on restore either.
  - Objects never activated in this session default to `"never"` even under `quiet`: Unity runs Awake on first activation even
    for disabled MonoBehaviours, which a quiet activation cannot prevent. They are reported (`never_activated` in run.json);
    the spec can name them in `play.activateList` to force `quiet` or `normal`.
  - `"normal"`: plain SetActive (OnEnable side effects such as ChickenGunLogic posting `chickenGunStart`,
    AC/ChickenGunLogic.cs:193-207, are logged).
  - `"never"`: stays hidden, reported.
- Recorded inactive at T, live active: hidden through the visibility formula. No script callbacks.
- Camera-driven objects follow `play.cullers`:
  - `"camera"` (default): culler groups stay active (`valid = false`, 2.4.2). In `preCullMain`, after the frustum refresh of
    this frame (SubsceneCullingManager.Update uses the pose set at the end of the previous frame), call
    `BoundsCullerRuntime.UpdateBoundsCullers(c => false)` (FP/BoundsCullerRuntime.cs:241-262): it fills `insideViewFrustum` and
    `facingPlaneVisible` from Camera.main but never calls UpdateVisible (it only marks `noChanges`). Then `IsVisble()` (:439)
    per culler gives `cullerVisible` for its group. The same render is culled as the live game would cull it, without
    SetActive, without DirtyStaticShadowManager and without FSM side effects. Subscene culling is frozen (2.4.2).
    Confirmed exact (L7): with UpdateBoundsCullers unhooked and called with `c => false` from onPreCull, 0 SetActive calls
    (97 in the live reference) and `IsVisble` equal to the live `previousVisible` in 100 % of 15708 comparisons over 66 zone
    cullers, also for auto-placed poses.
    CameraLightSwap stays live: it calls `SetActive` on its light GameObjects from the camera position in Update
    (AC/CameraLightSwap.cs:47-50, 142-155; ChickenGun has 7, level204 3 and level37 4). WaterEdge stays live the same way: it
    calls `SetActive` on its edge from the camera frustum in the camera's OnPreCull (AC/WaterEdge.cs:136-143). Their targets
    are `camDriven`, the visibility formula uses their live activeSelf, and the monitor reports their toggles separately
    (expected per camera, not a contamination of recorded objects).
  - `"recorded"`: recorded camActive states applied as `cullerVisible` (golden test).
  - `"force"`: `cullerVisible` always true (everything visible from any side; it creates no back-side geometry, 3.2 lint).
  - `"live"` (diagnostic only, lint warning): cullers re-hooked and `valid` restored, so groups toggle with SetActive per camera;
    R10 and R4 are expected to fail.

#### 2.4.5 Particles in replay

- k = last sample with t_k <= T; n = recorded count (0 clears the system).
- Apply (every mode, every frame the system has a recorded frame, and for quiet activations): `SetParticles(buf, n)`
  (ENG/ParticleSystem.cs:395), then always `Simulate(0f, false, false)` (ENG/ParticleSystem.cs:449) on the paused or cleared
  system. A plain SetParticles renders nothing: in L4 (FX_PipeSuck and FX_StrawBlow during suction, 11 systems, about 400-430
  particles, 18830 particle pixels) it changed 19999 pixels inside the particle mask, i.e. the particles vanished; with
  Simulate it changed 41 pixels (tolerance 376) in the game view and 248 (tolerance 325) in a player-side view 60 deg off axis
  with the stream at the frame edge. So size and colour over lifetime are re-evaluated and there is no stale-bounds culling.
  Cost is negligible (Get + Set 0.034-0.155 ms per 1000 particles).
- `"extrapolate"` (default): dt = T - t_k; per particle position += (velocity + animatedVelocity) * dt, rotation +=
  angularVelocity * dt (both radians), lifetime -= dt (drop the particle when lifetime <= 0); then apply as above. Exact at
  sample times; at 4x slow motion one emission pop per 4 output frames.
- `"match"` (P1): match particles of k and k+1 by randomSeed and lerp position, rotation, size, colour; unmatched ones as
  extrapolate.
- Local simulation space: positions are local to the system transform, which is replayed too.

#### 2.4.6 Lights and shadows

Recorded light values are applied in `preCullMain` every frame for lights with more than one key (live writers such as
ForceFlicker, AC/ForceFlicker.cs:529-808, and DynamicStaticShadowSwitcher, AC/DynamicStaticShadowSwitcher.cs:166-257, run
earlier in Update). `play.shadows`: `"recorded"` (default; recorded useStaticShadows/dirty), `"dirty"` (`staticShadowsDirty =
true` every frame on zone lights with shadows), `"dynamic"` (`useStaticShadows = false`). The default stays `recorded`.

L9 could not decide the static-cache question in ChickenGun: the only shadow-casting light near the machine is
`SpotLight_Machine` (Spot, Hard, useStaticShadows false), and with a no-cast reference pass (shadowCastingMode Off) the
replayed bale cast 0 px of measurable shadow in modes as-is, forced static, static + dirty and dynamic, at two placements.
The only loaded static-shadow light is the `#fields` darkroom DirLocal (useStaticShadows true), and a bale moved there is not
rendered because its ChickenGun culler group deactivates. Whether cached static shadow maps include moving casters, and
whether a group hidden by renderers still casts into a static cache, stays [unverified L9b] (a scene with a useStaticShadows
light over a native mover). Until then the `dirty` and `dynamic` modes are diagnostics (P1).

#### 2.4.7 Clocks in replay

- `_RealTime`: `MultiCamLook.realTime = true`, `MultiCamLook.clock = header.t0Real + r(T)` where r(T) is the recorded
  `realtimeSinceStartupDbl` offset interpolated at T, written in OnPreRender as v2 does (MC/MultiCam.cs:2420-2428).
  DepthBufferDownsample writes the wall clock in its OnPreCull (FP/DepthBufferDownsample.cs:197-214). Revision 2 added a
  second write in a new `MultiCamLook.OnPreCull` for sub-renders between the two; it is dropped: L10 found no visible
  `_RealTime` consumer in ChickenGun or at the lake (overriding `_RealTime` from the delegate and component OnPreCull and
  OnPreRender changed 0 px in every mask). Whether another scene has a visible consumer is [unverified]; R4 would show it as
  a pass-to-pass difference. If one is found, the write goes into preCullMain (the Camera.onPreCull delegate runs after
  every component OnPreCull, L1).
- `_PDTime` is a MaterialInstance float (AC/AnimateMaterialInstance.cs:59, AC/AnimateMultipleMaterialInstances.cs:61): covered.
- Unity's built-in `_Time` follows scaled time and freezes at timeScale 0, and it cannot be overridden: the engine sets it
  per camera, so `Shader.SetGlobalVector("_Time", ...)` from Camera.onPreCull/onPreRender or a component OnPreCull/OnPreRender
  changed 0 px (L10). `play.unityTime` is dropped. `_Time`-animated shaders are class C (frozen at the take end, 2.5):
  ChickenGun rain lines, mud ripples, splash edges and grain chute (9 of 16 masks animate running, 0-3.5 % of that frozen;
  FX_Moths 12 %, 32 px), and at the lake rain lines and the water Refract/Reflect/Fog and edge (82k-124k px per 0.5 s running
  against 455-1021 px over 3 s frozen).
- The native post `_RealTime_local` (managed reference FP/PDPostCombined.cs:787-788) cannot be set: grain or dither may differ
  between passes (R4 measures it).
- Frozen-frame drift (L10, L2): after settling, consecutive frozen frames differ by 0 px, but bright geometry edges drift
  slowly over seconds with the camera held (about 100-2500 px over 30+ frames). The R3/R4 noise floors measure it (9.2).

#### 2.4.8 End and restore

Put the game camera back first: release CameraControl and set the recorded final GameCamState pose, transform and
CameraScript members (positionCamera/Aim/Up, interpolatedPosition/Rotation, cameraVelocity, lastPositionCamera; MoveCamera
rewrites the transform from them every Update, R7 found it 0.64 m off with the transform alone), so the culler re-hook
below and any later unfreeze evaluate the game view. In the lab, unfreezing with the free camera 145 m away let the bounds
cullers deactivate the ChickenGun collider groups and the boy fell to y = -38517 (Section 12). Then apply the final sample
(the state the game froze in after the record pass), then restore rigidbody flags and velocities (the Begin-time velocities
only when the game has not advanced since the record's last sample, `Time.timeDbl == t0 + duration` in the same session;
otherwise zero, R7),
particle play state (Play for systems that were playing), probe overrides, the neutralize table (controllers re-enabled,
CrowdDude re-inserted into its umbrella, RopeLineRenderer re-enabled), Renderer and Light enabled flags, quiet activations;
then cullers: `valid` restored, `RefreshRuntimeState()` on every culler (marks it changed, so the next live update
re-evaluates it, FP/BoundsCullerRuntime.cs:150-167, :335-341), `ExecuteInEditModeIncluded.update += UpdateBoundsCullers`
re-hooked if the culler count is not 0; `CullingActive = true`; RemoveAlwaysLoaded for the keep-loaded set; remove the hooks;
pop the session. `play.restore: "reload"` additionally loads the savepoint from the header; use it whenever time will be
unfrozen afterwards, because sleep states, contact caches and FSM timers are not restored exactly.
Freeze hold (round 1, R7): a single `Time.timeScale = 0` does not survive the session pop. TimeScaleController on
`_Globals` sets `GameManager.TimeScale = 1` every Update while captureFramerate is 0 (FP/TimeScaleController.cs), and
GameManager.UpdateTimeScale (LateUpdate -615) writes Time.timeScale whenever its computed value differs from its private
latch `oldTimeScale`, which the frozen passes left at 0. Every record, replay, take, golden and passes run that ends with
the game frozen therefore calls `MultiCamFreeze.Hold` after the session pop: Time.timeScale 0, the latch set to the value
the game computes next (1, as after a console `ts 0`), and a RuntimeCallbacks.lateUpdate (-400) re-assert of 0 every frame.
`G.TimeScale` (`ts`, the cheats slider and hotkeys), a level load or the next mcam session releases it; a session pushed
while frozen restores the hold at its pop.

#### 2.4.9 Pass isolation

Unrecorded state can leak from pass i into pass i+1 (quiet activations, GetOrAdd*Property list growth, Animation state,
particle play state, culler side effects). Every camera pass therefore starts from the same state:
- `BeginPass`: restore the Begin snapshot of every mutable item the replayer touches (activation and quiet flags, Renderer and
  Light enabled flags, particle systems cleared and paused, rope vertices, shared materials, MaterialInstance values written
  back to the Begin values, probe overrides, shader-state material values and property blocks written back to their
  Begin values; shader globals cannot be read back and are set by the pass's first full apply), then apply T of the
  pass's first warmup render. End applies the final sample to the shader state like to every other channel.
- `EndPass`: undo this pass's quiet activations (SetActive(false) while the Behaviours are still disabled) and restore the
  Begin snapshot again.
- State hash at every BeginPass and after the last render of a pass: FNV-1a over the activeSelf bitset of the set, Renderer
  and Light enabled bitsets, every PlayMakerFSM's `ActiveStateName` and `Fsm.Finished` in the set (PM/PlayMakerFSM.cs:76,
  PM/HutongGames.PlayMaker/Fsm.cs:384), MaterialInstance list sizes, `BoundsCullerRuntime.boundsCullers.Count` and loaded
  subscene count. Written to run.json `passes[]`; a pass whose start hash differs from pass 0 is flagged (R4).
- `UnityEngine.Random.seed` is read at BeginPass and EndPass into `passes[].rngStart/rngEnd` (reported, not hashed: frozen
  Update-phase scripts may draw harmlessly). Reading does not consume and the value changes on every draw, so it is a valid
  read-only fingerprint; the setter re-seeds and Unity 5.0 has no `Random.state`, so the stream cannot be saved and restored
  (L6). The replayer never writes the seed.

### 2.5 Coverage

| System | Recorded as | Replay | Status |
|---|---|---|---|
| Rigid transforms (bale, machine parts, doors, lever, rope bodies) | TRS keys, local space under a set parent | written in lateWrite, re-asserted at preCull | covered |
| Activation of containers (FSM ActivateGameObject on `_GameLogic/Chickens`) | activeSelf of every set GameObject | visibility formula, quiet activation of the topmost inactive ancestor | covered |
| Boy bones, boy root | world TRS (bones flat under the skeleton) | lateWrite (root first by depth) | covered; Actor.Update overwritten |
| Boy logic position (Boy.pos3 = custom 2D body, FP/Boy.cs:279-309) | virtual channel | not driven (never Teleport, FP/Boy.cs:501-516); its consumers (DynamicStaticShadowSwitcher light flags, ChickenGunFadeEffects MaterialInstance) are replayed as outputs; MultiCamTargets reads the channel | covered by outputs |
| Huddle cloth, arms, legs | world TRS of visual bones and mesh bones | lateWrite; arm/leg controllers disabled | covered (P1 test) |
| FakeRagdollJoint2 jiggle | its transforms | written | covered; frame-rate dependent at record time (FP/FakeRagdollJoint2.cs:344-368) |
| Legacy Animation, Animator | resulting transforms | written over the frozen evaluation | covered (CrossFadeQueued clones not rebuilt, not needed) |
| Rigidbody interpolation | rendered (interpolated) transforms | interpolation None + kinematic | covered (L3: writes persist in every combination at timeScale 0) |
| Shuriken particles incl. sub-emitters, collision, external forces | raw particles per system | SetParticles + Simulate(0, false, false) on paused systems | covered (L4, game view and 60 deg off axis); off-screen culled systems stale |
| Lights (flicker, switch lights, shadow switches) | light values | preCull | covered; CameraLightSwap live |
| MaterialInstance incl. `_PDTime`, wetness, fades, decal fades | property values | Set*Property at preCull | covered |
| Material swaps (SwitchLight, FSM SetMaterial) | sharedMaterial ids of every set renderer | preCull; a live material of the recorded name is kept | covered (P0) |
| Huddle skin vectors (`_MainCharPosWorld`, `_Aspect`, nine `dam_*` bruises on `CHARACTER_Huddle_2`, hair material) | shader state, material scope | preCull every frame | covered [unverified in game] |
| Property blocks (`_DamageLimb` of huddle arms and legs, boy occlusion `_Intensity`) | shader state, block scope | preCull on change | covered [unverified in game] |
| CrowdDude head and holder rotation | transforms | umbrella removal (2.4.2) | covered |
| RopeLineRenderer geometry (crane rope) | BlendVertex positions | component disabled, Draw by reflection in lateWrite | covered (L11: within 2 px) |
| ColorBlendProbe shader globals (`_PDHuddleAmbient`, `_PDHuddleToon`), Player2 grass and plant globals | shader state, global scope (writer values; content version 1 recordings: probe positions only) | preCull every frame; version 1: override position, probe re-derives | covered [unverified in game]; version 1 recordings only while the active blend-box set is unchanged (risk 10) |
| Fog / ambient / bloom blend | FogAmbientBlender.currentBlendVal | preCull | covered |
| Post values (D11PDPostCombined) | scalars | preCull, P1 | partial |
| Decals, incl. child AO decals of movers (L9) | transforms + MaterialInstance | | covered |
| Spawned VFX | inline objects | driven while alive; destroyed ones need keepSpawned (P2) | partial |
| FluidVolume level, WaterSurfaceWave rings | transform, MaterialInstance vectors | | covered |
| Water render-texture simulation (WaterQueueMaster), `_Time`-animated shaders (rain lines, water Refract/Reflect/Fog, mud ripples, splash edges, grain chute; L10), Hair point simulation, other LineRenderer geometry (no getters in 5.0, ENG/LineRenderer.cs:17-44), procedural meshes | - | frozen at take end | not covered |
| Bounds cullers | camActive (info) | per-camera visibility by hiding (`cullers: camera`), no SetActive | covered (L7: exact) |
| Subscene culling | keep-loaded list in the header | frozen (`CullingActive = false`) for the whole replay | intended |
| CameraLightSwap (SetActive on light GameObjects), LensFlareSimple, AudioCameraProbe, WaterEdge edge subtrees and their `_Clip`/`_Disp` materials, WaterDust dust objects, BoyToShader | excluded or camActive | live per camera | intended (L12) |
| Sound | WAV of the record pass | silent passes (GlobalAudio paused) | Section 6; muted output: blocked until a capture path passes (6.0 G3) |

### 2.6 C# contract

`MC/ReplayTypes.cs` holds the spec classes and shared types. The public signatures below are the contract between the
files: they were written first as compiling stubs (the v2 pattern), then implemented. C# 7.3 on old Mono: no tuples or
ValueTuple, no newer BCL; Unity 5.0 API only; no comments inside function bodies; English identifiers. The `-nostdlib` build
against the game's mscorlib (MULTICAM_DESIGN 10) turns .NET 4 APIs into compile errors; the usual ones in writer and reader
code: Stream.CopyTo, Stopwatch.Restart, StringBuilder.Clear (use `Length = 0`), Path.Combine with 3+ arguments,
string.Join(IEnumerable), Enum.TryParse, Tuple.

```csharp
// MC/ReplayTypes.cs (ARCHITECT): spec classes of 3.1 (SpecNode subclasses) plus:
public sealed class RecordHeader
{
    public int version = 1; public string created = "", spec = "", level = "", recorderVersion = "", clock = "real";
    public int captureFramerate, subscene = -1, savepoint = -1, streamVersion, screenW, screenH;
    public double fixedDeltaTime, t0, t0Real; public long stepAtT0; public float gameFov;
    public List<string> subscenes = new List<string>(), keptCullers = new List<string>(), forcedCullers = new List<string>();
    public List<string> fogFields = new List<string>(), postFields = new List<string>();
    public List<string> cameraDriven = new List<string>();
    public float posEps, rotEps, cut;
    public int vSyncCount, targetFrameRate, refreshRate;
}
public static class SrecConst
{
    public const uint Magic = 0x43525349;                          // "ISRC"
    public const ushort Version = 1;
    public const uint TagHead = 0x44485253, TagObjects = 0x544A424F, TagSample = 0x4C504D53, TagIndex = 0x58444E49, TagEnd = 0x444E4553;
    public const byte KindTransform = 0, KindBoyBone = 1, KindClothBone = 2, KindParticles = 3, KindLight = 4, KindMaterial = 5, KindProbe = 6, KindSpawned = 7, KindRope = 8, KindRenderer = 9;
    public const byte SecXf = 1, SecFlags = 2, SecParticles = 3, SecLights = 4, SecMaterials = 5, SecVirtual = 6, SecObjAdd = 7, SecObjDel = 8, SecProbes = 9, SecRender = 10, SecEvents = 11, SecMatProp = 12, SecCamActive = 13, SecRope = 14, SecSharedMat = 15, SecShaderDef = 17, SecShaderVal = 18;
    public const int ContentVersion = 2;                           // RecordHeader.version the recorder writes (4.2 Versions)
    public const byte ShGlobal = 0, ShMaterial = 1, ShBlock = 2;    // shader channel scopes
    public const byte XfPos = 1, XfRot = 2, XfScale = 4, XfCut = 8, XfParent = 16, XfPreKey = 32;
    public const byte FlActive = 1, FlRenderer = 2, FlKinematic = 4, FlLight = 8;
    public const byte ObjCamDriven = 1, ObjScaleVariant = 2, ObjExcludedMaterial = 4, ObjSpawned = 8, ObjLocal = 16, ObjCamXf = 32, ObjSeenActive = 64;
    public const int ParticleBytes = 84;
}
public interface IReplaySource
{
    bool active { get; }
    double duration { get; }
    int sampleCount { get; }
    double SampleTime(int i);
    int SampleAt(double t);                                         // last sample with time <= t (0 before the first)
    bool GameCam(double t, GameCamState g);                         // interpolated recorded game camera state
    bool Character(string who, double t, out Vector3 pos3, out Vector3 vel3, out string state); // "boy" | "huddle"
    bool Probe(double t, out Vector3 probe);                        // cameraBlendProbe world position
    double RealTimeAt(double t);                                    // record wall clock, seconds since t0Real
    long LogicStepAt(double t);
    double EventTime(string name);                                  // first fire in the record pass, NaN if none
}
public static class ReplayHub { public static IReplaySource source; public static bool replaying { get { return source != null && source.active; } } }
public sealed class ReplayPassInfo { public string camera, startHash, endHash; public int rngStart, rngEnd, renders, warmups, taaResets; }   // run.json passes[] (rev 3)
public sealed class ReplayRecordingInfo { public string dir; public RecordHeader header; public int samples, objects; public double duration; public int unresolved, unresolvedSpawned; public List<string> never_activated; public int conflicts, readbackErrors, activations, monitorHits; }   // run.json recording
// ReplayConst (rev 3): allowed values, file names, MuterOnlyFlagFile, RemovedKeys + CheckRemoved(Json.Obj root, SpecReport) (`replay.play.unityTime` -> error)
// SrecConst (rev 3): also the Smpl*, Comp*, Li*, Mat*, Val*, Virt* bit values of 4.2

// MC/SceneHooks.cs (PLAY_B)
public static class SceneHooks
{
    public static event Action preUpdate, lateWrite;
    public static event Action<Camera> preCullMain;                 // Camera.onPreCull: after every component OnPreCull (L1)
    public static int lateFrame { get; }                            // Time.frameCount of the last RuntimeCallbacks.lateUpdate
    public static Camera main { get; }
    public static void Ensure();                                    // subscribe once (idempotent), cache RenderHost.WorldCam()
    public static void Release();                                   // unsubscribe when no client is left
}

// MC/SceneRecorder.cs (REC_A)
public sealed class RecordPrep { public void Restore(); public ReplayHandover HandOver(); }  // HandOver: Restore minus cullers and keep-loaded
public sealed class ReplayHandover { public List<BoundsCullerRuntime> forced; public List<SubsceneCuller> keptLoaded; }  // in ReplayTypes.cs
public sealed class SceneRecorder
{
    public static SceneRecorder active, last;                       // last: the last ended recorder of this session (same-session resolve)
    public RecordSpec spec; public string dir; public int samples, objects; public long bytes;
    public readonly Json.Obj extra;                                 // record.json keys CORE sets before End (pacing, runnerPhase, holds, audio)
    public static RecordPrep Prepare(RecordSpec spec, List<CamPose> planned, float aspect, SpecReport report);
    public static SceneRecorder Begin(RecordSpec spec, string dir, RecordHeader header, SpecReport report); // set + sample 0 on the next preCull
    internal static bool Prebuild(RecordSpec spec, string dir, SpecReport report); // record set built a few frames before sample 0; Begin takes it (2.3.4)
    public static List<Transform> RecordSet(RecordSpec spec, bool withCameraDriven, SpecReport report);    // for srec probe camera
    public UnityEngine.Object Ref(int id);                          // live object of an object table id
    public void SetVirtual(MultiCamContext ctx);                   // same frame, before render
    public void Event(string kind, string name, string detail);
    public string Status();
    public void End(SpecReport report);                             // flush, footer, objects.tsv, record.json
}

// MC/SrecFormat.cs (REC_B); full shapes and rules in the stub header
public sealed class SrecObject { /* one OBJT/OBJA record of 4.2; parentId -1 = outside the set */ }
public struct SrecStamp { public int sample; public double gameTime, realTime; public long logicStep; public int frameCount; public ushort fixedSteps, flags; }
public struct SrecLight { /* light values, cookie as string id */ }  public sealed class SrecVirtual { /* virtual channel */ }  public sealed class SrecEvent { }
public interface ISrecSink { void Begin(SceneRecording r); void Sample(SrecStamp s); void Xf(int sample, int id, byte bits, Vector3 pos, Quaternion rot, Vector3 scale, int parentId); /* one method per section, then End */ }
public sealed class SrecShaderChannel { public int id, target; public byte scope, type; public string name; public int definedAt, firstSample, lastSample, keys; }   // material round
public interface ISrecShaderSink { void ShaderDef(int sample, SrecShaderChannel c); void ShaderVal(int sample, int channel, Vector4 value); }   // optional, the reader calls it when the sink implements it
public sealed class SrecWriter
{
    public SrecWriter(string path, RecordHeader h); public long bytes; public int samples, stalls; public string error;
    public int StringId(string s); public void Objects(List<SrecObject> table);
    public void BeginSample(SrecStamp s); /* Xf, Flags, Particles(id, Particle[], n), Light, Material, MatProp, Virtual, ObjAdd, ObjDel, Probe, Render, Event, CamActive, Rope, SharedMat: ascending id per section */ public void EndSample();
    public int ShaderDef(byte scope, int target, byte type, string name); public void ShaderVal(int channel, Vector4 value); public int shaderChannels { get; }   // sections 17/18: dense channel ids, values non-decreasing per sample
    public void Close();
}
public sealed class SceneRecording { public RecordHeader header; public int sampleCount; public double[] times, realTimes; public long[] logicSteps; public int[] frameCounts; public ushort[] fixedSteps, flags; public List<SrecObject> objects; public List<string> strings; public List<SrecShaderChannel> shaderChannels; public string path; public long bytes; public bool truncated; }
public static class SrecReader { public static SceneRecording Read(string path, SpecReport report); public static SceneRecording Read(string path, ISrecSink sink, SpecReport report); public static int ReadParticles(SceneRecording r, int obj, int sample, byte[] dst); }

// MC/SceneTracks.cs (PLAY_A): SceneTracks : ISrecSink, ISrecShaderSink (explicit), Load(path, interp, report), SampleAt, Keys, Xf(id, t, out pos, out rot, out scale),
// CutBetween, ParentAt, Flags, CamActive, Light, PropCount/PropName/Prop, SharedMat, Probe, Render, Rope, HasParticles, ParticleCount, Virtual(t, dst), Alive,
// EventTime, ShaderCount, ShaderChannel(ch), Shader(ch, t, out value); managed math only and no game types (the offline track tests run it under .NET 9)
// MC/SceneShaderState.cs (material round): ShaderMirror (pure probe rules, also compiled into the offline tests), the recorder's
// shader channels (SampleShader in preCull, ShaderMaterial(channel) for a same-session replay) and the replayer's
// ResolveShader / ApplyShader / RestoreShader (partial classes)

// MC/SceneReplayer.cs (PLAY_B)
public sealed class SceneReplayer : IReplaySource
{
    public static SceneReplayer current;                            // Begin sets current and ReplayHub.source = this; End clears both
    public ReplayPassInfo pass; public SceneTracks tracks;          // pass: BeginPass camera/startHash/rngStart, EndPass endHash/rngEnd
    public ReplayRecordingInfo Info(); public UnityEngine.Object Resolved(int id); public string ObjectsText(string filter, int max);
    public static SceneReplayer Load(string recordDir, PlaySpec spec, SpecReport report);  // read + resolve
    public void Begin(ReplayHandover handover, SpecReport report);  // freeze, streaming freeze, cullers, neutralize, hooks on (handover may be null)
    public void BeginPass(string camera);                           // restore the Begin snapshot, state hash
    public void EndPass(string camera);                             // undo quiet activations, restore snapshot, state hash
    public string StateHash();
    public double T { get; set; }                                   // applied by the next frame's hooks
    public void ApplyNow(double t);
    public int conflicts, activations, monitorHits, readbackErrors;
    public string Status();
    public void End(SpecReport report);                             // final sample, restore
}

// MC/SceneCommands.cs (PLAY_C): public static string Command(List<string> a, DevCore core);   // "srec ..."
// MC/MultiCamReplay.cs (CORE_A): the CORE_A / CORE_B contract (ReplayModes, ReplayArgs, ReplayProgress, ReplayMain, ReplayRunJson, ReplayFinish) is its header
// MC/AudioCapture.cs (AUDIO)
public static class AudioCapture
{
    public static bool capturing;
    public static double clockBase;                                 // CORE sets RecordHeader.t0Real; audio_sync.csv real_time = realtimeSinceStartupDbl - clockBase
    public static string Preflight(AudioCapSpec spec);              // null = ok, else the reason (refuse)
    public static bool Start(AudioCapSpec spec, string outDir, SpecReport report);
    public static void Marker(string label, int sample, double gameTime, int lag); // Update phase only (6.1); lag = frames between the labelled sample and this post
    public static bool ForceLevel(float level);                     // AudioRules.Change + GetRTPCValue readback; false = readback mismatch (caller aborts)
    public static void Ensure();                                    // subscribes Tick to RuntimeCallbacks.update and Stop to RuntimeCallbacks.applicationQuit (once)
    public static void Tick();                                      // every frame, self-subscribed: watchdog, rtpc re-force and readback outside windows
    public static void Stop(string why);                            // idempotent, also from abort/quit
    public static string Command(List<string> a, DevCore core);     // "acap ..."
}
```

---

## 3. Spec extension and bridge commands

### 3.1 `replay` block (`ReplaySpec`, new field `public ReplaySpec replay;` of MultiCamSpec, null = all defaults)

| Key | Type | Default | Meaning |
|---|---|---|---|
| replay.source | string | "" | Recording folder to replay; "" = `<out>/record` of the same take. |
| replay.record | RecordSpec | {} | Record pass (below). |
| replay.play | PlaySpec | {} | Replay passes (below). |
| replay.audio | AudioCapSpec | {} | Capture and safety (below). |

`replay.record` (`RecordSpec`):

| Key | Type | Default | Meaning |
|---|---|---|---|
| clock | string | "real" | `real` (normal pacing, captureFramerate 0) or `fixed` (2.3.4). |
| rate | int | 60 | fixed clock: captureFramerate and targetFrameRate (60, 120, 240). |
| res | [a,b] | [0,0] | Record pass resolution; [0,0] (default) keeps the current one. |
| roots | string[] | ["auto"] | `auto`, object paths, `subscene:<name>`, `zone:<zone>`, `boy`, `huddle`; subscenes that load or activate later join (2.3.1). |
| keep | string[] | [] | Subscenes kept loaded and active from before sample 0 to the replay (header.keptCullers), recorded from sample 0. |
| settleFrames | int | 12 | Frames to wait after the kept subscenes are loaded and active before sample 0 (`srec record start` and the mcam record pass), so late-awake content (LateAwakeNode, initializationFrame <= 10: CEO_Room, the cafeteria railing FX) is on at sample 0 (2.3.4). |
| exclude | string[] | [] | Object paths left out. |
| box | object | null | `{center:[x,y,z], size:[x,y,z]}` limits `auto` subscenes. |
| characters | bool | true | Boy actor bones, huddle cloth/arm/leg bones. |
| spawned | bool | true | Watch `_Dynamics`, `_SubsceneDynamics` and new children of recorded transforms. |
| keepSpawned | bool | false | P2: disabled clones of spawned objects. |
| particles | string | "all" | `all`, `none`, `list` (`particleSystems` paths). |
| particleSystems | string[] | [] | For `list`. |
| lights / materials / probes / fog / post | bool | true / true / true / true / false | Channels (post is P1). |
| shaderState | bool | true | Shader globals from their script writers, shared-material values and renderer property blocks of the known writers (huddle bruise `dam_*`, `_PDHuddleAmbient`/`_PDHuddleToon`, `_DamageLimb`), change-only, sections 17/18 (2.3.2). |
| blocks | string[] | [] | Opt-in property-block values of named renderers that no known writer covers: `<object path or suffix>\|<property>[:float],...`, keyed from their first non-zero value (sections 17/18, scope block). |
| posEps | number | 0.0001 | Metres. |
| rotEps | number | 0.0012 | Radians, upper bound of the extent-scaled rotation threshold (2.3.2). |
| cut | number | 0.75 | Metres per sample that count as a teleport, combined with the velocity rule and isKinematic false -> true (2.3.2). |
| alwaysAnimate | bool | true | 2.3.3. |
| forceCullers | string | "zone" | `zone`, `planned`, `list`, `none`. |
| cullers | string[] | [] | Names for `list`. |
| rescanEvery | int | 60 | Samples between subscene join polls; every recorded transform's child count is checked once per this many samples. |
| captureGame | bool | false | Also grab the game camera at record res (golden test). |
| captureEvery | int | 10 | Grab every n-th sample. Each grab stalls the main thread (about 34 ms at 1280x720, R1), so a captureGame record is a golden reference, not pacing grade (2.3.4). |
| checks | string[] | [] | Content checks of `tools/srec_tool.py report`: `#Subscene` or an object path / suffix / name, optional ` @<from>..<to>` window (seconds or event[+-s]) the object must be active (visible when it has renderers) over. |

`replay.play` (`PlaySpec`):

| Key | Type | Default | Meaning |
|---|---|---|---|
| look | string | "game" | `game`: one render per step, TAA keep, engine blur (5.3). `clean`: the v2 render budget (warmup + N TAA-reset samples) per step. |
| order | string | "camera" | `camera`: one continuous pass per camera. `step`: v2-style visits of all cameras per step over the recording (stills, clean look). |
| interp | string | "hermite" | `hermite`, `linear`, `nearest` (5.4). |
| particles | string | "extrapolate" | `extrapolate`, `match` (P1), `sample` (no extrapolation). |
| warmup | int | 16 | Renders before the first captured step of a pass and before every director run, along the shot's own poses at the preceding times, first one with TAA reset (5.3). |
| motionBlur | string or number | "game" | `game`, `angle`, `off`, or a fixed motionBlurStrength (5.3). Frozen engine blur works (L2), no sub-frame fallback. |
| shutterAngle | number | 288 | For `angle`. |
| cullers | string | "camera" | `camera`, `recorded`, `force`, `live` (diagnostic) (2.4.4). |
| shadows | string | "recorded" | `recorded`, `dirty`, `dynamic` (2.4.6). |
| activate | string | "quiet" | `quiet`, `normal`, `never` (2.4.4); objects never active in this session use `never`. |
| activateList | object | {} | `{path: "quiet"\|"normal"}` overrides for never-activated objects. |
| copyHolds | bool | true | Director holds copy the source camera's frames. |
| realTime | string | "record" | `record`: `_RealTime` from the recorded wall clock (OnPreRender, 2.4.7); `capture`: v2 capture clock. |
| ramp | number | 0 | Output seconds over which speed changes between segments are blended (0 = step). |
| cams | string[] | [] | Subset of cameras for this run ([] = all enabled). |
| coverageOk | string[] | [] | Top-down cameras whose floor coverage was checked with a still (silences the 65 deg warning, 3.2). |
| range | [a,b] | null | Replay only this record-time range. |
| setup | string[] | null | Commands for a replay in a fresh session; null = the record spec's `setup` (stored in record/spec.json) without its cues, so unrecorded state (FSM states, machine on, spawned chicks) matches as closely as possible; then the baseline diff (2.4.1). `[]` = only `load <subscene> <savepoint>` from the header + `waitfor streaming`. |
| maxUnresolved | number | 0.01 | Refuse above this fraction of unresolved dynamic objects (kind `spawned` excluded, reported separately). |
| monitor | bool | true | 2.4.2. |
| restore | string | "final" | `final` or `reload` (2.4.8). |

`replay.audio` (`AudioCapSpec`):

`suspend`, `keepSuspended`, `allowMuterOnly`, `heartbeat` and `maxHeartbeatAge` apply only to the opt-in `muted` output;
the default `audible` output (6.0) uses `capture`, `level`, `markerEvery` and `onHold`.

| Key | Type | Default | Meaning |
|---|---|---|---|
| capture | string | "none" | `none`, `record` (WAV of the record pass). `log` (Wwise call log for per-camera mixes) is P2. |
| suspend | string | "auto" | `auto`: AkSoundEngine.Suspend(true) when the lab flag `_mod/acap_suspend_ok.json` exists (not written: S3 rendered at 0.919x, L5); `on` (diagnostic: the WAV may run slow, the slope check of 6.2 flags it); `off`. |
| keepSuspended | bool | true | Stay suspended after the capture until the game exits. |
| allowMuterOnly | bool | false | Allow a capture without Suspend (InsideMuter is then the only guard). Preflight also requires `_mod/acap_muteronly_ok.json`, written only by a passing L5 S2. |
| level | number | 100 | rtpc_master_volume forced during the capture window (0..100). |
| markerEvery | int | 30 | Samples between output-capture markers (plus one marker at every fired spec event, 6.1). |
| onHold | string | "abort" | A hold (game paused or loading) during a capture: `abort` the take, or `mark` (continue; the gap is written to audio_sync.csv and cut by `mcam_audio.py track`). |
| heartbeat | string | "_mod/muter/heartbeat.json" | InsideMuter heartbeat file, relative to the game folder. |
| maxHeartbeatAge | number | 2 | Seconds; older aborts the capture window. |

### 3.2 Existing v2 fields in record and replay

| Field | Record pass | Replay passes |
|---|---|---|
| setup, seed, preroll | as v2 (preroll is not recorded) | `play.setup` only, in a fresh session |
| cues | run at their times (they drive gameplay) | ignored (they would change the frozen game) |
| events | armed and logged as v2; fire times stored | times read from the recording: no predictions, `rehearsal` and `expect` ignored |
| seconds, until | end of the recording | duration = recording (or `play.range`) |
| timing.fps, segments (from, speed, samples, shutter, label) | not used | the output timeline (5.1); any float speed |
| segments.rate, legacy `rates`, settle | error in replay | |
| timing.physics | `game` only (`fine` is an error) | not used |
| render | not used | `look: clean`: warmup + samples per step as v2; `look: game`: only `render.motionBlur` < 0 means "keep the game value" |
| session | as v2, `res` replaced by `record.res`; `vsync` ignored: RecordBody sets the 60 Hz pacing after the push (2.3.4) | as v2 (`res` = output resolution) |
| stream | keep-loaded prepass with the planned poses, handles kept for the replay (2.3.3) | prepass again for the replay cameras, then `CullingActive = false` until End (2.4.2); `addDuringTake` has no effect |
| interpolate | as v2 | ignored (interpolation None) |
| post, defaults, cameras, tracks, output, compose | cameras evaluated only for `rig.Plan` | as v2 |

Lint (CORE in `mcam check`, DOCS offline): errors: unknown `look`/`order`/`interp`/`cullers`/`shadows`/`activate`/`clock`;
`clock: fixed` with rate outside {60, 120, 240}; segment `rate` or legacy `rates` in a replay spec; `audio.capture: record`
with `clock: fixed` and rate != 60; `audio.capture: record` with `suspend: off` and `allowMuterOnly: false`; `timing.physics:
fine`; `play.range` outside [0, seconds]; `play.unityTime` (removed: the engine sets `_Time` per camera, L10). Warnings:
`record.res` above 1920x1080 (record pass may fall below real time), `look: clean` with samples > 8 (cost), `motionBlur` > 2
(shader clamp unknown), cues with `cam`/`res` commands, `play.cullers: recorded` with cameras far from the game camera,
`play.cullers: live` (contaminates passes), `audio.onHold: mark`, `audio.suspend: on` (suspended capture renders at 0.919x
real time, L5), `audio.capture: record` (with the muted output refused by Preflight until a capture path has passed, 6.0 G3).
Player-side rule for replay and re-shoot cameras (sets have no back side; `play.cullers: force` makes groups visible but
creates no geometry): on top of the v2 player-side checks (error for a forward with z < -0.05 while pitch < 60,
MULTICAM_DESIGN 4.x "Player-side clamp"), a warning for every static, key, rail or orbit orientation whose forward is more
than 65 deg from +z, unless pitch > 60 (top-down) and the camera is listed in `play.coverageOk` after a verified still.

### 3.3 Example (ChickenGun; cameras as in the ChickenGun v2 spec)

```json
{
  "version": 2, "name": "chickengun_replay", "seconds": 12, "until": "@baleLand+3.5", "preroll": 1.0, "seed": 1,
  "setup": ["audio force rtpc rtpc_master_volume 0", "audio native on", "god on", "panel off", "triggers off", "labels off",
            "savepoints off", "load 7 0", "waitfor savepoint #chickenGun", "waitfor streaming", "wait 3",
            "tp 221.0 -221.4 0.66", "wait 2",
            "call ChickenGunObject/ChickenGunVacuumLogic ChickenGunLogic OnElectricityOn",
            "call _SaveLogic/ChickensLoaded ChickenSpawner DoSpawn", "wait 1"],
  "cues": [{"at": 1.0, "cmd": "call ChickenGunObject/ChickenGunVacuumLogic ChickenGunLogic ToggleSuctionOn"}],
  "events": {
    "baleFall": {"type": "signal", "obj": "HayBaleHolder/HayBaleTrigger", "signal": "enterSignal"},
    "baleLand": {"type": "impact", "target": "HayBaleHolder/HayBale", "min": 1.0, "after": "@baleFall"}
  },
  "timing": {"fps": 30, "segments": [
    {"from": 0, "speed": 1, "label": "real"},
    {"from": "@baleFall-1.8", "speed": 0.5, "label": "ramp_in"},
    {"from": "@baleFall-1.0", "speed": 0.2, "label": "slowmo"},
    {"from": "@baleLand+0.8", "speed": 0.5, "label": "ramp_out"},
    {"from": "@baleLand+1.4", "speed": 1, "label": "real"}]},
  "session": {"res": [2560, 1440]},
  "post": {"brightness": 0.55, "blacklift": 0.006, "postGamma": 0.03, "inWhiteScale": 0.95},
  "replay": {
    "record": {"clock": "real", "res": [0, 0], "forceCullers": "zone", "captureGame": false},
    "play": {"look": "game", "motionBlur": "game", "warmup": 16, "ramp": 0.4, "cullers": "camera"},
    "audio": {"capture": "none", "suspend": "auto", "level": 100}
  },
  "cameras": ["... the seven cameras of the ChickenGun v2 spec ..."]
}
```

Workflow: `mcam check` -> `mcam record <spec> <out>` -> (first time per scene, a separate golden record with the same spec and
`captureGame: true`: `mcam record` + `mcam golden` + `mcam_qa.py replaydiff` on that
folder; captureGame records are not pacing grade, 2.3.4) -> `mcam passes <spec> <out>` -> `mcam replay <spec> <out>` -> `mcam_compose.py all <out> --audio record` -> qa. Later
re-shoots: a new spec with `"replay": {"source": "<out>/record"}` and new cameras, `mcam replay <spec2> <out2>`.
`audio.capture` is `none` in the example. With the default `audible` output `"capture": "record"` needs no lab flag (6.0);
with the opt-in `muted` output it needs a passed capture path (6.0 G3): `suspend: auto` (S3b passed, flag present) or
`allowMuterOnly: true` (S2 passed). The first setup line (`audio force rtpc rtpc_master_volume 0`) mutes the game through an
rtpc rule; it belongs to the muted-output recipe and is optional with the default output.

### 3.4 Bridge commands

All are bridge commands in InsideDev.dll (reachable from the MCP `command` tool without rebuilding InsideDev.Mcp.dll).

| Command | Result |
|---|---|
| `mcam record <spec> <out>` | Record pass only (setup, seed, preroll, cues, events, real time, capture). Ends frozen at the last sample. Writes `<out>/record/`. |
| `mcam replay <spec> <out> [cams=a,b] [range=t0:t1]` | Load the recording (`replay.source` or `<out>/record`), plan pass, one pass per camera (EvalOrder, directors after sources). Writes the v2 take layout in `<out>`. |
| `mcam take <spec> <out>` | record + replay in one session (no reload in between); forced cullers and the keep-loaded set are handed over and released only at replay End. |
| `mcam passes <spec> <out>` | Plan only: event times from the recording, segment table (T ranges, speed, steps), per camera: renders, copies, estimated seconds and disk. |
| `mcam golden <spec> <out> [every]` | Replay the `game` camera at record res with `cullers: recorded`, rendering every record sample in order (TAA history and the velocity buffer's previous matrices then come from the previous sample, as in the record pass) and grabbing every n-th (default `captureEvery`) into `<out>/golden/<sample:D5>.jpg` for `mcam_qa.py replaydiff`. Also writes `golden/noise/` (renders of one frozen frame at the start after the 12-render settle, then +1, +16, +120 renders later: N1 = consecutive-render floor incl. TAA jitter phase, N120 = the slow edge-drift floor of frozen frames, 2.4.7; R3/R4 use the lower one). |
| `mcam status [json]` | As v2; states `recording` (t, samples, objects, MB, fps, drift ms, audio state) and `replay pass i/n <camera> step j/m eta`. |
| `mcam stop` / `mcam abort` | As v2; abort also stops the capture (window closed first: rtpc 0 with the muted output, back to the game volume with the audible one), ends the replay (restore) and closes the writer. |
| `srec record start <dir> [roots=...] [keep=a,b] [spec=<file>] [clock=real\|fixed:<rate>] [audio=on]` / `srec record stop [keep]` | Manual recording (free play, MCP-driven takes); `keep=` pins subscenes and starts the recording once they are loaded and active (at most 30 s, `srec record stop` cancels the wait); `spec=` takes `replay.record` of that spec as the defaults (command keys win) and arms its `events` like the mcam record pass, so `@event` times exist in manual recordings too; `stop keep` hands the forced cullers and kept subscenes to the next `srec load` in this session. |
| `srec event <name> [detail]` | Store a timed event (kind `event`) in the active recording, manual or mcam (`SceneRecorder.PostEvent`); passes the mcam busy gate. |
| `srec load <dir>` / `srec end` | Load a recording for the viewer and freeze; restore and release. |
| `srec play [speed] [loop]` / `srec pause` / `srec seek <t>` / `srec step <dt>` | Viewer: T advances by unscaledDeltaTime * speed while the user flies the free camera (no capture). |
| `srec status` / `srec objects [filter]` | State; object table with key counts, resolution and conflicts. |
| `srec probe [n]` | Write-order probe for n frames: names every object changed between lateWrite and preCull and the candidate writers (components with LateUpdate, Umbrella types with UmbrellaLateUpdate). Mandatory once per scene class; results extend the neutralize table (2.4.2). |
| `srec probe camera [n]` | Camera probe: frozen, only the camera moves between two poses for n frames (default 30); lists every set transform that changes (camera-driven writers such as LensFlareSimple, AudioCameraProbe, WaterEdge subtrees, WaterDust) into `camera_driven.tsv`, with columns path, components, expected (on the built-in list of 2.3.1), rigid. Rotation deltas use the metric of 2.3.2. |
| `acap preflight` | Muted output: muter heartbeat age, muted INSIDE sessions, Suspend and muter-only flags, rtpc rules and the GetRTPCValue readback, target folder. Audible output: no capture or rtpctest running, AudioSpy installed, Wwise not suspended, game volume, target folder. "ok" or the refusal reason. |
| `acap start <name>` / `acap mark <ascii>` / `acap stop` | Manual capture to `<game>/_mod/audio/<name>.wav` with the same guards. |
| `acap suspend on\|off` / `acap status` | AkSoundEngine.Suspend(true) (muted output only) / WakeupFromSuspend (wake only with the rtpc forced to 0); capture state, file size. |
| `acap output [audible\|muted]` | Show or set the sound output (EditorState `acap.output` in editor.cfg, default `audible`); refused while a capture, window, rtpctest or wake is active. |

Busy gate during record and replay: as v2 (MULTICAM_DESIGN 3.6), external commands are rejected except `mcam status|stop|abort`,
`srec status`, `acap status`, `game_state`.

---

## 4. File format

### 4.1 Take folder

```
<out>/
  record/                     record pass (the recording; reusable by later replays)
    scene.srec                binary recording (4.2)
    objects.tsv               object table, human readable: id kind path owner comps first last keys
    record_meta.csv           sample,frame_count,game_time,dt,real_time,logic_step,fixed_steps,time_scale,flags,xf_keys,particles,bytes
    events.csv                v2 schema (fired events, cues, setup, warnings) on record time
    rig.csv                   v2 schema, recorded game camera per sample
    tracks.csv                v2 schema (spec tracks)
    audio.wav                 Wwise capture (capture: record)
    audio_sync.csv            marker,label,sample,game_time,real_time,lag
    game/<sample:D5>.jpg      captureGame frames
    record.json               header, counts, sizes, drift statistics, forced cullers, activated groups, shaderState, warnings
    spec.json, log.txt
  spec.json run.json progress.json log.txt meta.csv poses.csv events.csv tracks.csv rig.csv   (v2 schema, replay)
  <cam>/<step:D5>.jpg         replay frames
  golden/<sample:D5>.jpg      mcam golden
```

Replay files keep the v2 columns (MULTICAM_DESIGN 6) so mcam_compose and mcam_qa work unchanged:
- `meta.csv`: step = rendered replay step index; game_time = T (record seconds); rate = round(M / E) (equivalent steps per game
  second, 5.2); group = M; frame, sub, captured = 1 for every rendered step; speed = s; real_time = r(T) (recorded wall
  seconds since record start: the audio clock); fixed_steps = recorded logic steps in (T_prev, T]; segment index.
- `poses.csv`, `tracks.csv`: from the plan pass. `rig.csv`: the recorded GameCamState at T. `events.csv`: the record events
  re-stamped to the first replay step with T >= the record time (`step` column), game_time = record time.
- `run.json`: mode `replay` (or `record`, `take`, `golden`) plus a `recording` block {dir, header, samples, duration, objects,
  unresolved, unresolvedSpawned, never_activated, conflicts, readbackErrors, activations, monitorHits} and `passes[]` {camera,
  startHash, endHash, rngStart, rngEnd, renders, warmups, taaResets}.
- `record/record.json` adds `pacing` {vSyncCount, targetFrameRate, refreshRate, changes, frame-time mean/p99, hitches},
  `runnerPhase` (2.2), `holds[]` (wall-time start, duration, reason) and `audio` {slope, slopeError, residualMs, usable}
  (6.2).

### 4.2 `scene.srec` (little endian, BinaryWriter)

File = 8-byte preamble (`u32 Magic "ISRC"`, `u16 version 1`, `u16 flags`) followed by blocks. Every block is `u32 tag`,
`u32 payloadLength`, payload, `u32 crc32(payload)`. Readers skip unknown tags; a crash leaves a readable prefix (the reader
stops at the first incomplete or bad block). Strings = `varint byteLength` + UTF-8. `varint` = unsigned LEB128. Floats f32,
times f64.

Versions: the preamble version is the framing version (readers refuse a newer one) and stays 1. `RecordHeader.version`
is the content version the recorder writes (`SrecConst.ContentVersion`): 1 = revision 3, 2 = the material round adds
sections 17 and 18 (shader state). Readers do not gate on it: version 1 readers skip the unknown sections 17 and 18
(sample sections with an unknown id are skipped), and version 1 files simply have no shader channels (SceneTracks
ShaderCount 0, the replayer falls back to the probe re-derive of 2.4.3). The offline srec tests check both directions (a version 1
file through both readers, a version 2 file through a reader without sections 17/18).

Blocks in order: `SRHD` (header), `OBJT` (object table at Begin), then `SMPL` blocks (one per sample), `INDX` (footer), `SEND`
(u64 total bytes, u32 sample count).

`SRHD`: every RecordHeader field (2.6) in declaration order, lists as varint count + strings.

`OBJT` and inline `OBJA` records (one per object):

| Field | Type |
|---|---|
| id | varint (dense, 0..) |
| kind | u8 (SrecConst.Kind*) |
| flags | u8 (1 camDriven, 2 scaleVariant, 4 excludedMaterial, 8 spawned, 16 local (TRS keyed in parent space), 32 camXf (camera-driven transform, never written), 64 seenActive) |
| instanceId | i32 (session-local) |
| parentId | varint (id + 1; 0 = parent outside the set) |
| depth | u16 |
| path | string (Inspector.PathOf) |
| owner | string (`boy:bone:SpineB`, `huddle:cloth:17`, `spawned:_Dynamics`, "") |
| ordinals | varint count + varints (same-name ordinal chain from the root) |
| siblings | varint count + varints (sibling index chain) |
| comps | u32 mask: Rigidbody 1, Animation 2, Animator 4, ParticleSystem 8, Light 16, Renderer 32, SkinnedMeshRenderer 64, MaterialInstance 128, D11VelocityBufferTag 256, ColorBlendProbe 512, LineRenderer 1024, Actor 2048, Collider 4096 |
| types | varint count + strings (MonoBehaviour type names, the resolve signature) |
| initial | pos 3 f32, rot 4 f32, scale 3 f32, u8 activeSelf |
| particles | (kind 3) i32 maxParticles, u8 simulationSpace, u32 randomSeed |
| light | (kind 4) u8 type, u8 shadows |
| material | (kind 5) varint counts of float/color/vector/texture properties + property names |
| rope | (kind 8) varint vertexCount, u8 drawMode, varint lineDivisions |
| renderer | (kind 9) varint material slot count (every Renderer of the set has a kind 9 entry; its owner object is the parent id) |
| extent | f32 (rotation threshold extent, 2.3.2) |

`SMPL` payload: `u32 sample`, `f64 gameTime`, `f64 realTime`, `i64 logicStep`, `i32 frameCount`, `u16 fixedSteps`, `u16 flags`
(1 marker posted, 2 hitch, 4 hold, 8 captureGame frame), then sections `u8 id`, `u32 length`, payload; unknown ids skipped.

| Section | Payload (entries are changes only, except in sample 0) |
|---|---|
| 1 Xf | varint count; per entry: varint idDelta (id - previous id - 1), u8 bits (pos 1, rot 2, scale 4, cut 8, parent 16, preKey 32: the values belong to the previous sample), pos 3 f32, rot 4 f32, scale 3 f32 (local values for `local` objects), parent varint (id + 1) |
| 2 Flags | varint count; varint idDelta, u8 bits (active 1, renderer 2, kinematic 4, light 8); active for any set GameObject, renderer for kind 9 entries |
| 3 Particles | varint systems; per system: varint id, varint n, u8 encoding (0 raw84), n * 84 bytes |
| 4 Lights | varint count; varint idDelta, u8 mask (enabled 1, intensity 2, color 4, range 8, spotAngle 16, cookie 32, useStaticShadows 64, staticShadowsDirty 128), values (f32, 4 f32, f32, f32, varint texture id, u8, u8) |
| 5 Materials | varint count; varint id, u8 kind (float 0, color 1, vector 2, texture 3), varint propIndex, value (f32 / 4 f32 / 4 f32 / varint texture id) |
| 6 Virtual | GameCamState fields in declaration order (CamPose as p, r, fov, aim, near), camera transform + fov at preCull, boy pos3, velocity3, state string id, visual state string id, huddle pos3, velocity3, state id, probe, u8 flags (boy present 1, huddle present 2, huddle main 4) |
| 7 ObjAdd | varint count of OBJA records |
| 8 ObjDel | varint count; varint id |
| 9 Probes | varint count; varint idDelta, 3 f32 curPos |
| 10 Render | varint count; varint fieldIndex (header fogFields then postFields), u8 type, value |
| 11 Events | varint count; string kind, string name, string detail |
| 12 MatProp | varint count; varint id, u8 kind, string name (property appended to that MaterialInstance) |
| 13 CamActive | varint count; varint idDelta, u8 active |
| 14 Rope | varint count; varint id, varint n, n * 3 f32 vertex positions |
| 15 SharedMat | varint count; varint idDelta (kind 9 ids), varint slot, varint material table id |
| 17 ShaderDef | (content version 2) varint count; varint channel (dense: the number of channels defined before), u8 scope (0 global, 1 material, 2 property block), varint target + 1 (material name string id / kind 9 renderer id / 0), u8 type (0 float, 1 vector), string property name |
| 18 ShaderVal | (content version 2) varint count; varint channelDelta (non-decreasing within the sample), f32 (float) or 4 f32 (vector); change-only, every known channel in sample 0; not object keys (INDX unchanged) |

Section order inside a sample: 16 Strings, 7 ObjAdd, 12 MatProp, 17 ShaderDef, 1, 2, 3, 4, 5, 6, 9, 10, 11, 13, 14, 15,
18 ShaderVal, 8 ObjDel (definitions before the entries that use them, deletions last).

`INDX`: varint sampleCount; per sample u64 block offset; per object varint firstSample, lastSample, keyCount; per particle
system varint frame count followed by (sample, u64 offset) pairs; string table (states, texture names, material names).

Tracks in the reader: per object and channel `int[] keySample` + value arrays. Continuous channels (position, rotation,
scale, light values, MaterialInstance floats/colours/vectors, probes, fog/post values, rope vertices) interpolate between
consecutive keys, also across gaps; the pre-key (2.3.2) makes every gap start and end at true values, so the error inside a
gap is bounded by the eps and the motion never stops and jumps. Step channels (flags, textures, shared materials, cookies,
shadow flags, camActive) hold until the next key. A cut key steps at its sample.

### 4.3 Sidecars

`audio_sync.csv` (AUDIO): one row per marker: `marker` (index), `label` (`s<sample>` or `e<event>`), `sample`, `game_time`,
`real_time` (the `realtimeSinceStartupDbl` offset at the AddOutputCaptureMarker call), `lag` (frames between the labelled
sample and the post: 0 for periodic markers, 1 for event markers, 6.1). The WAV positions of the markers come from the WAV
`cue `/`LIST adtl labl` chunks, which the release DLL writes (L5 S1: cue chunk with the label present; S3: 12 cue points
with labels s0-s11).

### 4.4 Size and cost [estimate]

ChickenGun, 15 s at 60 samples/s (900 samples): transforms about 400 changing x 41 B = 16 KB per sample (15 MB; local-space
keys remove the rigid children of moving objects, so this is an upper bound), pre-keys add at most one entry per gap; flags,
shared materials, lights, materials, probes, rope under 5 KB per sample; particles 0 to 170 KB per sample (2000 live
particles x 84 B) = up to 150 MB. Shader state (huddle, cafeteria -> CEO office): about 13 material values (2 change every sample),
2 globals (changing inside blend zones), up to 36 property-block values (`_DamageLimb`, changing with the huddle's turn),
so 40-300 B per sample, under 0.6 MB for 35 s; reads 0.02-0.07 ms per sample (one GetPropertyBlock per active arm or
leg, about 14), replay writes under 0.05 ms per frame [estimate; record.json `shaderState` measures it, budget 0.25 ms].
Total 20-170 MB per take; record overhead 0.5-3 ms per frame. R2 checks the real size
against this formula evaluated with the counts of record_meta.csv (sum of xf_keys x 41 B + particle bytes + 5 KB per sample). Replay: transforms decoded into memory (under 30 MB), particles read
per frame from disk (OS cache). Replay pass cost about 40-60 ms per step at 1440p (render + ReadPixels + JPEG on the writer
thread), so 6 cameras x 840 steps = 4-6 min plus warmups, against about 23 min for v2 with W+N = 9.

---

## 5. Motion blur and slow motion

### 5.1 Output timeline

- Segments come from `timing.segments` (`from` on record time, resolved from the recording's events, `speed` s = record
  seconds per output second). Output clock: `T_0 = range start (0)`, `T_{f+1} = T_f + s(T_f) / fps`; with `play.ramp` r > 0
  the speed blends linearly over r output seconds centred on each switch. The take ends at the first frame with T >= duration
  (or range end). Any float speed is valid (no integer step rate, unlike v2 3.4).
- Sub-frames: M = segment `samples`, shutter S degrees, exposure E = S/360 * s/fps (record seconds); sub-frame i of frame f is
  rendered at `T_{f,i} = T_f - E + (i + 1) * E / M` (end-aligned like v2), clamped to >= 0.
- `meta.csv` gets one row per rendered sub-frame (4.1); the compositor groups them by `frame` exactly as for v2 takes.

### 5.2 Interpolation

- Transforms: between the keys a and b that bracket T (consecutive keys, adjacent samples or the two ends of a gap);
  interpolation parameter u = (T - t_a) / (t_b - t_a). Position: non-uniform Catmull-Rom (Hermite with tangents scaled by
  segment durations) when the keys at b-2, b-1, b, b+1 are consecutive samples and none is a cut; else linear. Rotation:
  Quaternion.Slerp after flipping the sign of b when dot < 0; tracks rotating more than 90 deg in one sample are reported as
  "fast rotator: record at a higher rate". Scale: lerp. A cut key makes the value step at t_b. `local` objects interpolate
  their local TRS and are written after their parent, so a child stays rigidly attached through the linear fallback and limb
  lengths of hierarchical rigs (chick bones) never change; world-space objects (set roots, flat Actor bones) interpolate
  world TRS.
- Flags: step at the sample time. Lights (intensity, colour, range, spotAngle) and MaterialInstance floats, colours and vectors:
  lerp; textures, cookies, shadows flags: step. Fog and post values: lerp. Probe positions: lerp. Game camera: position Hermite,
  rotation slerp, fov lerp, `cut` step. Particles: 2.4.5.
- Why this is faithful: the game itself draws world-space blends between 60 Hz states (Actor history blend FP/Actor.cs:402-440,
  rigidbody interpolation), so Hermite between rendered samples is at least as smooth as the game's own render interpolation.
  The recording clock matters for frame-rate-dependent secondary motion (FakeRagdollJoint2 per-frame filters,
  FP/FakeRagdollJoint2.cs:344-368): choose it once per take.

### 5.3 In-engine motion blur (`play.look: "game"`)

- The native velocity buffer has camera reprojection plus per-object motion for D11VelocityBufferTag objects only
  (FP/PDVelocityBufferExplicit.cs:53-149). ChickenGun level171 has 38 tags: the 24 chick meshes (skinned), Belt, HandleWire,
  WireHanging (skinned), MotorWheel, Wheel2, Hay_Box, Hay_box_HiRes_A, the bale Details, the door boards
  (velocity-tag list extracted from the level data); the boy and huddle meshes are tagged in level251. Rigid tags sample matrices at
  OnWillRenderObject of the main camera; skinned tags bake in LateUpdate -1 (2.2). Replay writes land before both (lateWrite
  -400; L2 showed the rendered pose; the skinned velocity bake itself is checked by R6, a flying chick against its record
  frame).
- One render per output step gives real per-render displacement, so blur and TAA should behave as in play. v2 has no game blur
  because its W+N renders at one pose zero the velocity after the first render.
- Frozen blur works (L2). The managed `PDVelocityBufferExplicit` and `PDTemporalReprojection3` never run in the game: their
  Awake adds the native `D11PDVelocityBufferExplicit` / `D11PDTemporalReprojection3` and destroys itself
  (FP/PDVelocityBufferExplicit.cs:151-161, FP/PDTemporalReprojection3.cs:551-569), so the managed latch of the last non-zero
  timeScale (FP/PDVelocityBufferExplicit.cs:67-68) and `_MotionScale = motionBlurStrength * min(1, 1/timeScale)`
  (FP/PDTemporalReprojection3.cs:456) were only a reference. L2 measured the native path directly: the bale at 3 m/s (28.4
  px per render), engine blur strength 0.8, TAA jitter off, gives a streak of 13.5 px frozen (timeScale 0, lab writes) against
  15.0 px running (timeScale 1, the same per-render displacement), ratio 0.90, and 3408 against 3221 differing blur pixels
  (ratio 1.06). `motionBlur: game` therefore stays the default with no sub-frame fallback; `look: clean` (5.4) remains an
  explicit choice for exact blur of untagged objects. The ratio sits at the edge of the +-10 % criterion, so R6 confirms it
  on more frames and speeds. Running references must be non-AI bodies (the bale): at timeScale 1 the Chicken AI moves and
  rotates the chick visual even with a kinematic root.
- Blur length is assumed proportional to `motionBlurStrength` x displacement per render (TAA `_MotionScale`,
  FP/PDTemporalReprojection3.cs:447,456); L2 confirmed it at one strength (0.8) only, the scaling is [unverified R6]. With
  Δ = record seconds per render (s / (fps * M)):
  - `"game"`: same exposure as play at 60 fps: strength = 0.8 x (1/60) / Δ. fps 30, s 1: 0.4; fps 60, s 1: 0.8; fps 30, s 0.25:
    1.6 (shader clamp [unverified R6]).
  - `"angle"`: constant shutter angle in output time: strength = shutterAngle / 360 (0.8 at 288 deg); slow motion then shows
    proportionally shorter blur (high-speed camera look).
  - `"off"` or a number: fixed strength. Sub-frame mode (M > 1) forces 0 (the compositor averages sub-frames).
- Pass start: `play.warmup` renders at the pre-roll times `T_0 - (W - i) * Δ_0` (clamped at 0) with the shot's own poses at
  those times (the plan pass evaluates the rig there; clamped times give the pose at 0), so TAA history, the camera's previous
  view and every tag's previous matrix are primed with this camera's motion; capture starts at step 0. The first warmup render
  resets TAA history: `feedbackMin = feedbackMax = 0` on the native `D11PDTemporalReprojection3` for that one render (the v2
  reset, MULTICAM_DESIGN.md:157), then the game values are restored. Without it the history still holds the previous pass's
  camera (0.88^16 to 0.97^16 = 13-61 % of it after 16 renders without neighbourhood clipping, game feedback 0.88/0.97,
  FP/PDTemporalReprojection3.cs:109-112). The warmup count is padded so every pass starts at the same TAA jitter phase: W' = W
  + ((16 - (renders + W) % 16) % 16) with the session render counter (assumes one jitter advance per main-camera render
  [unverified R4]).
- Director cuts and runs after copied holds (the director pass did not render while it copied a source camera, so its TAA
  history and previous matrices are many steps old): the same rule as a pass start, W renders along the new shot's own poses
  at T_{j-W} .. T_{j-1} with the TAA reset on the first one, then step j. This is the research rule "after a skipped run,
  reset TAA for W frames"; cost W renders per director run (`mcam passes` reports it). Object teleports inside the recording
  set `wrapAroundFrame` on their subtree (2.4.3).
- TAA in continuous passes reprojects untagged movers like the live game (the ChickenGun barrel animation has no tag); that is the
  game look. `look: "clean"` removes it at v2 cost.

### 5.4 Clean look and sub-frame blur (`play.look: "clean"`)

Per rendered sub-frame: warmup W + N TAA-reset samples as v2 (MULTICAM_DESIGN 3.3), engine blur 0, M sub-frames averaged by the
compositor in linear light. Use it for hero slow-motion shots where untagged objects need exact blur. Cost per output frame =
M x (W + N) renders.

### 5.5 Limits

- Particle emission pops at sample times (one per sample interval of record time).
- Objects faster than about 90 deg or 0.75 m per sample need `clock: fixed` with rate 120/240 for clean interpolation.
- Frozen-at-end systems (2.5, not covered) do not move in any camera.

---

## 6. Sound

### 6.0 Safety rule: the headphones stay silent (opt-in muted output)

Release default: the game sound is never muted. `acap.output` (EditorState in editor.cfg, set with `acap output
audible|muted`) defaults to `audible`: the game plays normally, a capture runs in real time and opens its window at once at
min(`audio.level`, game volume), and no InsideMuter heartbeat, Suspend, flag file or rtpc force is used
(`src/InsideDev/Runtime/AudioCapture.cs`, header). The gates below describe the opt-in `muted` output, a local option for a
machine where the game must stay inaudible while it records (for example other media on the same headphones). InsideMuter
(G1) and the volume step G0 are opt-in parts of it. With the muted output every path that can make INSIDE audible is
gated, and a sound take requires all applicable gates.

| Gate | Rule | Owner |
|---|---|---|
| G0 Boot (opt-in) | The game starts at master volume 0, so start-up posts are silent before any mod rule exists (AudioRules are not persisted; only the native-enforcement flag is, `EditorState "audio.native"`, MC/AudioRules.cs:123-139). The saved volume is PlayerPrefs `"Volume"` (FP/SystemSettingsPlayerPrefs.cs:36-47), applied at GlobalAudio start-up through `HandleMasterVolumeUpdate` to `rtpc_master_volume` (FP/GlobalAudio.cs:101-111). PlayerPrefs live in the Windows user's registry per company and product name (`HKCU\Software\Playdead\INSIDE\Volume_h3158011725`, checked by the Phase 0 launch step), so the value is shared by every INSIDE install of that Windows user: setting it to 0 is a local opt-in step (not shipped), and the in-game volume slider restores it. Capture windows force `audio.level` anyway (G4), so the saved 0 does not affect recordings. | AUDIO (opt-in launch step) |
| G1 Muter first (opt-in) | InsideMuter (`tools/muter/InsideMuter.exe`, source `src/muter/InsideMuter.cs`, built by `build/build_muter.sh`; ISimpleAudioVolume.SetMute(true) on every INSIDE.exe session of every active render endpoint, 50 ms poll; it mutes, it does not set volume 0) starts before the game and runs longer than the session: it never expires while an INSIDE.exe process exists and exits 10 s (`--linger`) after the last one ended (the muter used before Phase 0 stopped after a minute limit). It writes its heartbeat every 250 ms (`heartbeat.json` next to the exe, or the file given with `--heartbeat <path>`; the game reads `replay.audio.heartbeat`, default `_mod/muter/heartbeat.json` relative to the game folder): time, per INSIDE session (endpoint id, muted, `IAudioSessionControl.GetState` active/inactive, session peak from the session's IAudioMeterInformation), endpoint peak of the headphone device. | AUDIO |
| G2 Preflight | `AudioCapture.Preflight` refuses unless the heartbeat is younger than `maxHeartbeatAge` (2 s), reports no unmuted INSIDE session on any endpoint, and reports at least one muted INSIDE session, or none at all while Suspend is active. | AUDIO |
| G3 Suspend | Capture under `AkSoundEngine.Suspend(true)` (render-anyway: sinks replaced by AkOutput_Dummy, FP/AkSoundEngine.cs:1783) runs only when the flag file `_mod/acap_suspend_ok.json` exists, and only a passing S3 variant writes it. L5 S3: Suspend(true) returned AK_Success, the INSIDE OS session went inactive 1.30 s later and stayed inactive with session peak 0 and muted for the whole 60 s capture (no OS stream: the headphones stay silent), and the WAV recorded content (RMS -44.0 dBFS, peak -21.5 dBFS at rtpc 100, 12 cue markers). But the suspended render ran at 0.919x real time (55.125 s of WAV per 60.013 s wall, 176.4 kB/s, steady within 0.05 %), far outside the 0.5 % criterion, so the flag was not written and Suspend capture is unusable for sync. [speculation] 176.4 kB/s is 44096 frames/s, within 0.01 % of 44100: the dummy sink probably paces 48 kHz buffers at 44.1 kHz, which the S3b timer variant would not change. Next steps (not run): S3b (`timeBeginPeriod(1)`), then S2 (muter-only). The rtpc is lifted only after the heartbeat shows no active INSIDE render stream (session state not active, session peak 0) for 1 s, never on the API return of `Suspend(true)` (the swap took 1.30 s). It stays suspended until game exit (`keepSuspended`). Without the flag a capture needs `allowMuterOnly: true` in the spec, and that path stays refused by Preflight until S2 has passed (`_mod/acap_muteronly_ok.json`, written by S2 only). | AUDIO |
| G4 Window | `rtpc_master_volume` stays forced to 0 (the v2 recipe) except inside the capture window, where `AudioCapture.ForceLevel(level)` forces `audio.level`: `AudioRules.Change("rtpc:rtpc_master_volume", level.ToString(CultureInfo.InvariantCulture), "acap")` (the value is a string, MC/AudioRules.cs:60-67), then a readback with `AkSoundEngine.GetRTPCValue` (FP/AkSoundEngine.cs:1855) that must return the forced value; a mismatch closes the window (ForceLevel(0)) and aborts the take. Reason (L5): with native enforcement on, `AudioRules.Set` calls `SetRTPCValueID` (MC/AudioRules.cs:100) before `SyncNative()` (:105), so the native hook still enforces the previous rule value and every rtpc change reaches Wwise one change late: opening the window to 100 left Wwise at 0, and closing it to 0 left Wwise at 100 until the next set (S3 run 1: under a second, while suspended, OS stream inactive and session muted). Fix in Phase 1: SyncNative before Apply in Set (AUDIO, 8.2). Until the fix is in, ForceLevel calls Change twice (the lab workaround; readback 100 inside the window and 0 after). Stop, abort, runner destruction and quit (`RuntimeCallbacks.applicationQuit`, FP/RuntimeCallbacks.cs:75,151-154, subscribed by `AudioCapture.Ensure`; DevRoot's OnApplicationQuit is not ours) call ForceLevel(0) first (retried once on a readback mismatch, then logged as an error), then StopOutputCapture. | AUDIO, CORE |
| G5 Watchdog | `AudioCapture.Tick`, subscribed to `RuntimeCallbacks.update` by `AudioCapture.Ensure` (so a manual `acap start` is guarded without a runner): inside a window, heartbeat stale > 2 s, any unmuted INSIDE session, any INSIDE session peak > 0, while suspended any active INSIDE stream, or no muted session while not suspended -> ForceLevel(0), stop the capture, mark an error, keep Suspend (the L5 lab guard set). Outside a window, every frame: if the rtpc rule is not `0` (removed by `audio clear`, an undo of the History entry, ChangeRecorder via MC/AudioRules.cs:63, or a mod) it is forced to 0 again and logged; every 10th frame the Wwise value is read back with GetRTPCValue and a non-zero value is forced to 0 and logged. `Ensure` runs on the first `acap` command, `Preflight`, `Start`, and at the session push of any spec with a `replay.audio` block, and stays for the rest of the game session. | AUDIO |
| G6 Wake | WakeupFromSuspend only on explicit `acap suspend off`, only with the rtpc forced to 0, and only after waiting 100 ms. Replay passes never lift the rtpc force; GlobalAudio pauses Wwise at timeScale 0 anyway (FP/GlobalAudio.cs:164-175,435-466). | AUDIO |
| G7 No playback | No tool plays captured audio; checks are numeric (RMS, peak, cross-correlation). Muxed videos are previewed muted. | AUDIO, COMPOSITOR |
| G8 Optional | The user may route INSIDE.exe to a non-headphone output in the Windows volume mixer (user action; also the fallback WASAPI loopback source if capture fails). | user |

### 6.1 Capture mechanics (verified in game, L5 S1)

- `SoundEngine.StartOutputCapture(name)` / `StopOutputCapture()` (FP/SoundEngine.cs:952-960) pass through the mod's AudioSpy
  (MOD/AudioSpy.cs:279-280) to `AkSoundEngine.StartOutputCapture` (FP/AkSoundEngine.cs:1743-1751); markers only through
  `AkSoundEngine.AddOutputCaptureMarker` (:1753-1755, ANSI text: use ASCII).
- The release DLL implements capture (CSharp_StartOutputCapture queues engine command 0x26 and returns AK_Success); the file is
  opened later on the audio thread, so the return value proves nothing: AudioCapture checks that the file appears and grows.
  L5 S1: the file appeared 0.01-0.02 s after Start and grew linearly at 191.7 kB/s (max deviation 0.25 %); PCM 48000 Hz, 2 ch,
  16 bit; 5.013 s of WAV for 5.025 s wall; peak -200 dBFS (digital silence at rtpc 0).
- Path: the Wwise IO hook prepends the bank base path `SoundBankInfoManager.GetBankPath()` = streaming assets + `Audio\`
  (FP/SoundBankInfoManager.cs:114-121, set with `AkSoundEngine.SetBasePath` in FP/Playdead.Wwise/AkGlobalSoundEngine.cs:379-381)
  and does not detect absolute paths. AudioCapture creates `<game>/_mod/audio/` and builds the relative path at runtime from
  `GetBankPath()` to that folder (one `..\` per directory level of the base path below the common root; in the player this is
  `..\..\..\_mod\audio\<name>.wav`; Playdead used four levels from the editor's project layout, AC/HuddleSequencerTest.cs:80).
  Confirmed (L5 S1, S3): base path `<game>/INSIDE_Data/StreamingAssets\Audio\`, the argument above, the file at
  `<game>/_mod/audio/<name>.wav`, no platform subfolder. Start still checks that the file appears within 2 s, else stops and
  reports. Total path < 260 characters.
- Finalization: after StopOutputCapture, `File.Move` to `<out>/record/audio.wav` is retried every 100 ms for up to 10 s (it
  fails while the audio thread still holds the file); then the RIFF and data sizes are checked: neither may be 0xFFFFFFFF and
  data size + header must equal the file size, else `mcam_audio.py fix` is run and the take is flagged. A stable size alone
  does not prove that the writer closed the file.
- Markers: `AddOutputCaptureMarker` is queued like any Wwise call and taken by the next `RenderAudio` (SoundEngine.LateUpdate
  -600). The record step runs after every LateUpdate (2.2 phase 9, L1), so a marker posted there would land one game frame
  after that frame's gameplay posts. Markers are therefore posted in the Update phase: CORE's `RecordMarkers` handler on
  `SceneHooks.preUpdate` (RuntimeCallbacks.update -400, before RenderAudio) posts `s<n>` for n = `SceneRecorder.active.samples`
  (the index of the sample this frame's preCull will take) at n = 0 and every `markerEvery` samples, through
  `AudioCapture.Marker(label, n, gameTime, 0)`; game time does not change inside a frame, so `game_time` is that sample's.
  These markers enter the same audio render as the gameplay posts of the frame they label. Event markers `e<event>` (baleLand
  in ChickenGun, R9) are detected in the record step of frame k and can only be posted by the next preUpdate: they carry
  `lag 1`, are excluded from the alignment fit and serve as labelled references (6.2). L5 S1 and S3 found the cue/labl chunks
  in the WAV, so the onset fallback of revision 2 (`align --onsets` from an AudioSpy PostEvent log) is dropped.
- A crash before Stop leaves 0xFFFFFFFF RIFF/data sizes: `mcam_audio.py fix` rewrites them from the file size.

### 6.2 Case 1: real-time single camera (the record pass, or a plain real-time game-camera take)

- One pass, the game camera is the listener (AkListener on Camera.main, FP/SoundEngine.cs:610-622; camera-relative RTPCs in
  AkGameObjUmbrella, FP/AkGameObjUmbrella.cs:46-65,106-130). Never freeze during it (v2 freezes toggle the Wwise pause). The v2
  `Hold` (MC/MultiCam.cs:1419-1433) calls TickFrozen, which sets timeScale 0 (:1624-1640), and GlobalAudio then posts
  'pause' (`IsTimestopped => Time.timeScale == 0`, FP/GameManager.cs:117; FP/GlobalAudio.cs:164-175): RecordBody uses its own
  hold that never touches timeScale, and any hold inside a capture window follows `audio.onHold` (abort by default; `mark`
  writes the wall-time gap to audio_sync.csv).
- Clock `real` (default): game time tracks wall time. Clock `fixed` 60: allowed only if the drift check passes:
  |r(t) - t| stays below 2 frames over the take (record.json `drift`).
- Alignment (`mcam_audio.py align`): fit WAV sample position p = a * real_time + b by least squares over the `lag 0` markers
  (expect a ≈ 48000 from the fmt chunk; residual RMS reported; markers are quantised to the 1024-sample Wwise frame of about
  21 ms, the fit averages that out). Video time of game time T maps to WAV position p(r(T)). Slope check: |a / fmt rate - 1|
  must stay below 0.5 %, else record.json `audio.usable = false` and `track` / `--audio record` refuse the take. A suspended
  capture of the kind L5 S3 measured fits a ≈ 44100 (8.1 % off) and is refused here even if the flag were present.
  `lag 1` event markers are reported against the fit (their offset from p(r(t_event)), expected about one frame).
- For a real-time game-camera video straight from the record pass: `record.captureGame` with `captureEvery 1` at full res
  (pacing risk: warn when frames exceed 16.7 ms).

### 6.3 Case 2: replay passes (every camera)

- Passes are silent (Wwise paused; with the muted output the rtpc force also stays 0). Every camera gets the identical sound bed: the record WAV.
- Speed-1 segments: output frames f..g use WAV [p(r(T_f)), p(r(T_g + 1/fps))) as one continuous slice (no per-frame cutting,
  no resampling unless the fit slope differs from the fmt rate by more than 0.1 %, then a pitch-preserving stretch to fit;
  above 0.5 % the take's audio is refused, 6.2).
  Wall-time gaps (a `mark` hold, a hitch longer than `maximumDeltaTime`) are where r advances while T does not
  (record_meta.csv); `track` splits the slice there and drops the gap, so the audio stays on the game-time timeline.
- `mcam_audio.py track <take>` writes `<take>/audio_track.wav` on the output timeline from replay meta.csv (`real_time` column);
  `mcam_compose.py ... --audio record` muxes it: H.265 video unchanged, audio AAC-LC 48 kHz 320 kb/s in the MP4 (or FLAC in
  MKV with `--audio-codec flac`); ffprobe check: one hevc and one audio stream, durations within one frame. Default stays `-an`.
- Per-camera mixes (listener at each virtual camera) are P2: an audio-only real-time pass that re-posts the AudioSpy call log
  (PostEventID with the live AkGameObj, SetRTPCValueID, SetSwitchID, SetStateID, StopPlayingID with a playing-id map, no
  callbacks) at the recorded times, after `AkSoundEngine.SetRandomSeed(k)` (FP/AkSoundEngine.cs:1161), with 'pause'/'resume'
  blocked by `audio nuke pause` / `audio nuke resume`. Playdead's SoundEngineLogReplayer cannot be used as shipped (null
  AkGameObj arguments, FP/LogEvent.cs:32-53).

### 6.4 Case 3: slow motion

- Default `slow: "stretch"`: the WAV slice of a segment with speed s < 1 is time-stretched by 1/s with pitch preserved
  (rubberband CLI with a time map for ramps; fallback ffmpeg `atempo` chain, each factor in [0.5, 2], e.g. 0.25 = 0.5 x 0.5),
  50 ms crossfades at segment edges, optional music duck (`--duck-music`, needs the music bus stem: not available, so only a
  global -6 dB on the stretched part).
- `slow: "mute"`: silence with 0.3 s fades (ambience-free slow motion).
- `slow: "retime"` (P2): the call-log pass of 6.3 with trigger times at wall = (t - t0) / s and RTPC interpolation times scaled
  by 1/s: one-shots keep natural pitch and duration, spread out in time; music taken from the original WAV or muted. Wwise 2014
  has no global time-stretch and no INSIDE RTPC slows audio.

### 6.5 v2 and input replay

- v2 takes cannot carry sound (pause/resume every step). Use A, or record a separate real-time pass of the same spec (sync only
  approximate, because the run differs).
- B: sound from one run only (the game-camera run); every other run silent; `AkSoundEngine.SetRandomSeed` before each run for
  diagnostics only.

---

## 7. Determinism test protocol for input replay (B)

Built as the extension `InsideDev.Det.dll` (source `src/det/`, owner DET, Section 8), so the core DLL is untouched; commands reach it through the
extension fallback (MOD/Remote.cs:1057). It never touches Playback.SessionManager: its static constructor swaps the input core
(FP/Playback/SessionManager.cs:9-13).

### 7.1 Mechanism

- `FilmInput : GameInput.Core` overrides `UpdateCommand(Cmd)` and `CommandReceived(Cmd)` (FP/GameInput.cs:90-109), installed with
  `GameInput.SetInputSystem` (:334-337). Record: one row per logic step (k = LogicManager.stepCount - k0, mStick.x, mStick.y,
  mRawJump, mRawAction). Replay: fields from row k, `mStep = LogicManager.stepCount`. No AdjustPosition, no teleports.
  A new Core starts with `enabled = true`, `suppressAction = false` and a fresh 100-command buffer (FP/GameInput.cs:33-66), so
  FilmInput is installed before `load` in every run (record and replay alike), never mid-scene.
- k0 = LogicManager.stepCount in a `GameManager.allScenesLoaded` handler (FP/GameManager.cs:433-472). The game itself never
  resets the RNGs outside Playback sessions (ResetRNG is called only from FP/Playback.Session/*.cs; FP/Playback/Session.cs:50-54),
  so the DET prep below is the only reset.
- Cues are step-indexed `(k, command)` and fired from `RuntimeCallbacks.fixedUpdate` (as FP/Playback/FreeInput.cs:31) via
  `Remote.RunCommand` (MOD/Remote.cs:114) when `stepCount - k0 == k`; never from the 0.3 s bridge poll or real-time `wait`.
  The event fires once per RuntimeCallbacks instance; at runtime only one exists and fixedUpdate fires once per logic step
  (L8: 124 of 124), so the guard is insurance: each cue keeps `lastFiredStep` and fires at most once per LogicManager step;
  the same guard applies to the per-step log row.
- Prep after every load, identical in record and replay: `RandomGenerator.ResetAll(); PDRandom.seed = 0;
  UnityEngine.Random.seed = 0` at prepareLoad and at allScenesLoaded; vSyncCount 0, captureFramerate 60, targetFrameRate 60,
  fixedDeltaTime 1/60; optional prep switches below. Writing the seed is only ever a re-seed to the constant 0, never a restore
  of a read value: the getter is not the generator state (L6: the draw after setting the read value back differed, 0.046932 vs
  0.870586). Whether two re-seeds to 0 give the same stream is [unverified]; S1-S3 show it through `rng_fixed`.

### 7.2 Commands

| Command | Effect |
|---|---|
| `det prep [anim=default\|always] [cull=live\|freeze] [stream=live\|keep] [drip=on\|off]` | `always`: Animation.cullingType AlwaysAnimate on every Animation of the zone after each load. `freeze`: `RuntimeCallbacks.ExecuteInEditModeIncluded.update -= BoundsCullerRuntime.UpdateBoundsCullers` plus SetActive(true) of every zone culler group (re-applied after each load, AddBoundsCuller re-hooks, FP/BoundsCullerRuntime.cs:169-182). `keep`: AddAlwaysLoaded for every zone SubsceneCuller before the load finishes. `drip=off`: disable ParticleSound on FX_WaterDripDown2. |
| `det cue <k> <command>` / `det cues clear` | Step-indexed cue track. |
| `det run <name> <out> [kmax]` | Lock frames, `load 7 0`, ResetRNG, k0, prep, record inputs, fire cues, log, stop at kmax (default 1200) or baleLand + 210, unlock. |
| `det replay <name> <out> [cam=x y z yaw pitch fov]` | The same with inputs and cues from `<name>`; optional free camera pose from k = 0. |
| `det status` | Run state, k, divergence so far. |

### 7.3 Logged per step (one CSV row per logic step, same hook in every run)

k, LogicManager.stepCount, Time.frameCount, fixed steps this frame; `rng_fixed` = UnityEngine.Random.seed at the fixedUpdate hook,
`rng_late` = UnityEngine.Random.seed in LateUpdate of the same frame (reading `seed` does not consume, L6: three reads, equal draws); input fields;
boy pos3, velocity3, state class; HayBale Rigidbody position, rotation, velocity, angularVelocity, IsSleeping, isKinematic;
ChickenGunLogic private fields by reflection (state, isSucking, chickensInVacuum.Count, chickensToBlow.Count, shootTimer,
killCountdown); each of the 24 Chicken components sorted by path (position, velocity, isKinematic, isStarted, dead,
targetPosTranslation); bitmask of active culler groups; loaded subscene count. `events.csv`: k of baleFall, baleLand, every
shot, every bale hit (relativeVelocity.sqrMagnitude > 50, AC/ChickenGunLogic.cs:420-431), the killed chick.

### 7.4 Runs (all from `load 7 0`; cues k = 180 `tp 221.0 -221.4 0.66`, k = 300 OnElectricityOn + DoSpawn, k = 480 ToggleSuctionOn; offsets derived from the v2 real-second waits, approximate)

| Run | Camera | Prep |
|---|---|---|
| S1, S2, S3 | game camera | default |
| S4, S5 | game camera | full (always, freeze, keep, drip off) |
| P1 | game camera pose, but CameraScript paused from k = 0 (what the free camera does, MC/CameraControl.cs:179-180) with the pose copied from S1's rig.csv each step | default |
| C1 | free camera from k = 0 at the outlet pose (225 -219 -6, yaw 20, pitch -18, fov 45) | default |
| C2 | wide free camera framing the door crane rope and the ladder area | default |
| C3, C4 | as C1, C2 | full |
| F1 | game camera, captureFramerate 120 (2 frames per step) | full |
| V1 | the same take under the v2 freeze-and-visit loop | full |

### 7.5 Metric and thresholds (`tools/det_compare.py`, reference S1)

- Align by k, never by time. k* = first k where rng_fixed or rng_late differs or any position differs by more than 1e-4 m.
  Per object max |p_run - p_ref| over k <= k_baleLand + 210: boy, bale, max over chicks; bale rotation angle; event steps,
  shot count, killed chick.
- **PASS strict** (needed to intercut N runs): RNG fingerprints identical at every step, every position within 1e-4 m, identical
  event steps.
- **PASS film**: boy < 5 mm, bale < 1 cm and 0.5 deg, every chick < 2 cm until baleLand + 1 s, identical baleFall/baleLand steps
  and shot count.
- **FAIL** otherwise (the v2 T7 bound of 5 cm is too loose for intercut runs).

### 7.6 Decision rule

- S1-S3 fail: base nondeterminism (streaming, PreAwake timing, PhysX): B is dropped; A only.
- S pass, C1/C2 fail, C3/C4 pass: B viable only with full prep and all subscenes kept loaded.
- P1 fails while S passes: the divergence comes from paused camera logic (CameraScript state, camera-relative audio and
  POI logic), not from the camera pose; C1/C2 results are then read against P1, not S1.
- F1 fails: B runs must be exactly 60 captured fps; slow motion only through A.
- S4/S5 pass, S1-S3 fail: the prep is mandatory.
- Locate the culprit with k* and the rng_fixed/rng_late split: Update phase = ParticleSound or Animation events, FixedUpdate
  phase = Chicken, Rope, VFX.

---

## 8. Implementation plan by file ownership

### 8.1 Preconditions

- Phase 0 was built while MultiCam v2 was still being finished, so it did not edit `src`, `tools` or `specs`: the lab and
  DET code are separate extension DLLs (now `src/replaylab/` and `src/det/`), built against a fixed copy of the core DLL
  (now `bin/InsideDev.dll`).
- Every `_mod/InsideDev.*.dll` is loaded at boot (MOD/Runtime/Extensions.cs:10-11), so the ReplayLab and Det DLLs are
  installed only for a lab session and removed afterwards.
- Game runs for the lab: one game at a time, driven through the in-game MCP server, in a separate copy of the game folder
  (never the Steam install), with the muted output (InsideMuter started before the game and stopped after it).
- Phase 0 lab session done (2026-10-25, Section 12). Afterwards the ReplayLab DLL was removed again, no
  `_mod/acap_suspend_ok.json` existed and `_mod/audio` was removed.

### 8.2 Ownership (files never overlap)

| Path | Owner | Notes |
|---|---|---|
| docs/REPLAY_DESIGN.md | ARCHITECT | This file. |
| src/InsideDev/Runtime/ReplayTypes.cs (new) | ARCHITECT | Spec classes 3.1, RecordHeader, SrecConst, IReplaySource, ReplayHub. Frozen after Phase 1 start. |
| src/InsideDev/Runtime/MultiCamTypes.cs | ARCHITECT | One field `public ReplaySpec replay;` in MultiCamSpec. Nothing else. |
| stubs of every new src file below | ARCHITECT, then the owner | Public signatures of 2.6, compiling, bodies throw NotImplementedException or return "not built". |
| src/InsideDev/Runtime/SceneRecorder.cs (new) | REC | Record set, RecordPrep, sampling, change detection, spawn watch, record.json, objects.tsv. |
| src/InsideDev/Runtime/SrecFormat.cs (new) | REC | SrecWriter (pooled blocks, writer thread, CRC), SrecReader, SceneRecording. |
| tools/srec_tool.py (new), offline srec tests | REC | `info`, `verify` (CRC, counts), `objects`, `export <id> csv`, `synth` (synthetic recordings for PLAY and COMPOSITOR tests). |
| src/InsideDev/Runtime/SceneHooks.cs (new) | PLAY | 2.2 hook rules. |
| src/InsideDev/Runtime/SceneTracks.cs (new) | PLAY | Track decode, cursors, interpolation 5.2. |
| src/InsideDev/Runtime/SceneReplayer.cs (new) | PLAY | Resolve, freeze (streaming, cullers), neutralize table, apply, visibility owner, activation, culler emulation, particles, lights, materials, shared materials, rope, probes, pass isolation and state hash, monitor, restore. |
| src/InsideDev/Runtime/SceneCommands.cs (new), offline replay tests | PLAY | `srec` commands incl. viewer and probe. |
| src/InsideDev/Runtime/MultiCam.cs | CORE | `partial` on MultiCamRunner; `mcam record|replay|take|passes|golden`; replay lint (incl. the 65 deg warning); busy-gate whitelist; status states; session push/pop additions (record res, clocks); MultiCamLook: TAA reset hook for warmups (the revision 2 OnPreCull `_RealTime` write and `play.unityTime` are dropped, 2.4.7). |
| src/InsideDev/Runtime/MultiCamReplay.cs (new) | CORE | `partial class MultiCamRunner`: RecordBody/RecordLoop (own preroll, 60 Hz pacing and its per-frame assert, runner phase assert, own hold), RecordMarkers (preUpdate marker posting, 6.1), handover, ReplayBody, PlanPass, CameraPass (BeginPass/EndPass, warmup along shot poses, TAA reset), GoldenPass (every sample rendered), replay rows and run.json. |
| src/InsideDev/Remote.cs | CORE | Cases `srec` -> SceneCommands.Command, `acap` -> AudioCapture.Command; busy-gate whitelist entries; `Bind` (a `call` resolved once, for the record cues, 2.3.4). |
| src/InsideDev/Runtime/MultiCamTargets.cs | RIG | Replay-aware sampling (below). |
| src/InsideDev/Runtime/MultiCamRig.cs | RIG | Audit: every read of live game state goes through ctx.game or ReplayHub (gamelike probe and box). |
| src/InsideDev/Runtime/AudioCapture.cs (new), tools/mcam_audio.py (new), offline audio tests | AUDIO | Capture, gates G1-G7, self-subscription (Ensure: Tick on RuntimeCallbacks.update, Stop on applicationQuit), ForceLevel with readback, markers, sync CSV, WAV finalization; offline `fix`, `info`, `align` (lag-0 fit, slope check), `track` (incl. gap cutting), `stretch`, `check` (RMS/peak/transient, never playback). |
| src/InsideDev/Runtime/AudioRules.cs | AUDIO | One change only: in `Set` (MC/AudioRules.cs:83-107), the switch keeps the `Apply(...)` action of state and rtpc rules and invokes it after `SyncNative()` (:105, :141-161), so the native rule table already holds the new value when SetRTPCValueID/SetStateID reach the hook (6.0 G4, L5). Nothing else in the file. |
| src/muter/ (new; tools/muter/InsideMuter.exe) | AUDIO | InsideMuter (opt-in) without expiry while INSIDE.exe runs, heartbeat.json with per-session state and session/endpoint peaks; build/build_muter.sh. The opt-in G0 launch step (PlayerPrefs volume 0) is not shipped. |
| tools/mcam_take.py, tools/mcam_compose.py, tools/mcam_qa.py, offline compositor tests | COMPOSITOR | Replay run.json modes, `real_time` audio clock, `--audio record` mux, `replaydiff`, `audiosync`. |
| tools/mcam_spec_check.py, specs/replay_smoke.json (new), a ChickenGun replay spec (3.3), docs/MULTICAM.md, docs/MULTICAM_ru.md, offline spec tests | DOCS | Schema from MultiCamTypes.cs + ReplayTypes.cs (`--types` accepts several files), replay lint, examples, user guide chapter "Replay mode". |
| src/replaylab/ (new; bin/InsideDev.ReplayLab.dll) | LAB | Phase 0 probes (Section 9.1) as an extension DLL. |
| src/det/ (new; bin/InsideDev.Det.dll), tools/det_compare.py (new), offline DET tests | DET | Section 7. |
| every other file under src/InsideDev (CameraControl.cs, DevRoot.cs, AudioSpy.cs, EventMonitor.cs, RenderHost.cs, ...) | nobody | Used through their public APIs only. AudioSpy.cs goes to AUDIO only for the P2 call log. |

Phase 1 split by file (11 parallel roles, revision 3; each role owns only its files; the contracts are the stub file
headers and 2.6):

| Role | Files |
|---|---|
| REC_A | src/InsideDev/Runtime/SceneRecorder.cs |
| REC_B | src/InsideDev/Runtime/SrecFormat.cs, tools/srec_tool.py |
| PLAY_A | src/InsideDev/Runtime/SceneTracks.cs (plus offline track tests on synthetic recordings) |
| PLAY_B | src/InsideDev/Runtime/SceneReplayer.cs, src/InsideDev/Runtime/SceneHooks.cs |
| PLAY_C | src/InsideDev/Runtime/SceneCommands.cs |
| CORE_A | src/InsideDev/Runtime/MultiCamReplay.cs (RecordBody, RecordLoop, handover, markers, ReplayBody, PlanPass, CameraPass, GoldenPass, rows, run.json) |
| CORE_B | src/InsideDev/Runtime/MultiCam.cs (mcam record/replay/take/passes/golden, lint, status, session additions, MultiCamLook), src/InsideDev/Remote.cs cases |
| RIG | src/InsideDev/Runtime/MultiCamTargets.cs, src/InsideDev/Runtime/MultiCamRig.cs |
| AUDIO | src/InsideDev/Runtime/AudioCapture.cs, src/InsideDev/Runtime/AudioRules.cs (ordering fix only), tools/mcam_audio.py, src/muter/ |
| COMPOSITOR | tools/mcam_take.py, tools/mcam_compose.py, tools/mcam_qa.py |
| DOCS | tools/mcam_spec_check.py (its offline binder check also compiles ReplayTypes.cs, with `-r:` Assembly-CSharp-firstpass.dll), specs/replay_smoke.json, a ChickenGun replay spec, docs/MULTICAM.md, docs/MULTICAM_ru.md |

ARCHITECT keeps ReplayTypes.cs, the MultiCamTypes.cs field and this document. Scope of this round: P0 only.

### 8.3 Phases and tasks

**Phase 0** (done 2026-10-25 apart from L5 S3b, L5 S2 and L9b; results in Section 12)

- LAB P0 (done): `src/replaylab/` (ReplayLab.cs and the Lab*.cs files, version 0.2.0 at the time; `build/build_lab.sh`
  builds `bin/InsideDev.ReplayLab.dll`), `lab` commands for L1-L12; installed for the lab session only and removed after it.
  Results go to `<game>/_mod/replay_tests/<test>/` (`lab out <dir>` picks another folder).
- DET P0 (built): `src/det/` (FilmInput.cs, DetLog.cs, DetCommands.cs; `build/build_lab.sh` builds `bin/InsideDev.Det.dll`)
  per Section 7.
- AUDIO P0 (done): InsideMuter (`src/muter/`, opt-in) in run mode (no expiry while INSIDE.exe runs, per-session state,
  session and endpoint peak meters, heartbeat; final heartbeat of the lab session 62828 polls, 0 errors, unmutedSeenTotal 0);
  the opt-in G0 launch step was written but not run, and is not shipped.
- Lab game session (done): L1-L4, L6-L12, L5 S1 and S3. The results decided the write points, the Camera.onPreCull order,
  the coroutine phase, frozen blur, particles, `_Time`, rope Draw, the culler emulation and the camera-driven list (Section 12).
- Still open, LAB: L5 S3b (`timeBeginPeriod(1)` during a suspended capture; writes `_mod/acap_suspend_ok.json` only on a
  pass), then L5 S2 (muter-only, rtpc 100, 10 s; writes `_mod/acap_muteronly_ok.json` only on a pass); both concern only the
  opt-in muted output. Also L9b (static shadows in a scene with a useStaticShadows light over a native mover, plus the
  shadow of a group hidden by renderers).

**Phase 1** (after MultiCam v2 was finished; every item below already includes the Phase 0 decisions)

ARCHITECT (first, blocking): ReplayTypes.cs (PlaySpec without `unityTime`; AudioCapSpec as 3.1; RecordHeader; SrecConst;
`passes[]` fields rngStart/rngEnd), the MultiCamTypes.cs field, compiling stubs of every new file with the revision 3
signatures of 2.6 (`SceneHooks.lateFrame`, `AudioCapture.Marker(label, sample, gameTime, lag)`, `AudioCapture.ForceLevel`);
offline binder test (as v2) on the ChickenGun replay spec (3.3).

REC
- P0: record set (roots, characters, camDriven) with the exclusions of 2.3.1: the built-in `camXf` list (LensFlareSimple,
  AudioCameraProbe, WaterEdge `edge`/`inverseTransform` subtrees, WaterDust dustObject) plus `camera_driven.tsv`, and the
  excluded MaterialInstances (BoyToShader, LensFlareSimple, WaterEdge `_Clip`/`_Disp`); RecordPrep (AlwaysAnimate, forced
  cullers, HandOver); sampling of transforms (local space under set parents, world for roots and Actor bones), flags (activeSelf
  of every set GameObject, Renderer/Light enabled, isKinematic), shared materials, rope vertices, particles (raw 84 B, size
  assert), lights, materials, probes, fog, virtual; change detection with the angle `4 asin(|q - q_last| / 2)` in double, the
  extent from shown non-decal renderers, and pre-keys; cut rule (velocity bound, kinematic false -> true, activation, parent);
  sample 0 full; spawn watch; events; SrecWriter with pooled buffers, writer thread, bounded queue, per-block CRC; SrecReader
  with crash-prefix recovery; objects.tsv, record_meta.csv, record.json.
- P1: post values, rescans, srec_tool.py (`info`, `verify`, `objects`, `export`, `synth`).
- P2: keepSpawned clones; FixedUpdate physics keys channel.
- Acceptance: synthetic round trip (write -> read -> identical values); verify passes on a truncated file prefix; zero
  allocations per sample after warmup (GC count flat over 600 samples in R1); rotation metric unit test (identical float
  quaternions -> 0, a known 2e-5 rad rotation -> 2e-5 rad within 1 %).

PLAY
- P0: SceneHooks (incl. the per-frame `-= UpdateBoundsCullers` and `lateFrame`); SceneTracks (keys interpolated across gaps,
  pre-keys, local/world TRS, Hermite/linear/slerp, cuts, step channels); SceneReplayer Load/resolve (same session and
  owner/path re-resolve, spawned excluded from maxUnresolved, baseline diff), Begin (freeze, streaming freeze, cullers
  valid=false and unhooked, rigidbody freeze skipping `camXf` bodies, neutralize table incl. CrowdDude umbrella removal and
  RopeLineRenderer), apply per hook (probe-carrying roots in preUpdate; every transform and bone in lateWrite; write-once rule
  by session, never for objects under an Animation/Animator and never for Actor bones; preCull read-back; `camXf` objects and
  excluded MaterialInstances never written), visibility owner and `cullers: camera` emulation (WaterEdge and CameraLightSwap
  targets live), quiet activation (Behaviours incl. Animation/Animator, particle Pause + SetParticles + Simulate(0, false,
  false), never for unseen objects), particles extrapolate with `Simulate(0f, false, false)` after every SetParticles, lights,
  materials, shared materials, rope Draw, probes, fog, wrapAroundFrame on the cut subtree, BeginPass/EndPass/StateHash
  (Random.seed read into rngStart/rngEnd, never written), End/restore with the game camera put back before the culler re-hook,
  monitor; `srec` commands (load, play, seek, pause, step, status, objects, end, probe, probe camera with the `expected`
  column, record start/stop through SceneRecorder).
- P1: particles `match`, shadows modes (`dirty`, `dynamic`; diagnostics until L9b), conflict writer naming.
- Acceptance: srec_tool synth recordings replay with exact values at sample times (offline unit test using the reader and
  tracks only, incl. a slow rotator with gaps and a two-level hierarchy under linear fallback); R2-R8, R10 in game.

CORE
- P0: `partial` MultiCamRunner; RecordBody (setup, seed, record res, 60 Hz pacing after the session push, own real-time
  preroll, events, rig.Plan prepass, RecordPrep, keep-loaded handles owned by the record, SceneRecorder.Begin,
  AudioCapture.Start, RecordLoop, AudioCapture.Stop, Frozen, SceneRecorder.End, Restore or HandOver); runner phase assert at
  record start (`SceneHooks.lateFrame == Time.frameCount` when the record step resumes, else abort; `runnerPhase` in
  record.json); RecordLoop per frame (runs after LateUpdate, 2.2 phase 9): pacing assert, cues (stamped at their sample,
  acting from the next frame), `yield return null`, t, SampleGame, Targets.Step, events (event markers queued with lag 1),
  SetVirtual, rows, own hold with `audio.onHold`; `RecordMarkers` on `SceneHooks.preUpdate` posting `s<n>` for n =
  `SceneRecorder.active.samples` every `markerEvery` samples plus the queued event markers (6.1); never the v2 Preroll, Freeze
  or Hold. ReplayBody (Load, Begin with handover, timeline 5.1, PlanPass incl. poses at warmup times, CameraPass per
  EvalOrder: BeginPass, warmup along the shot's poses with TAA reset on the first render, the same before every director run,
  copies, grab, rows, EndPass; End); MultiCamLook keeps v2's `_RealTime` write in OnPreRender with the record clock (no new
  OnPreCull write, no `play.unityTime`); `mcam record|replay|take|passes|golden` (golden renders every sample, grabs every
  n-th, noise-floor frames at settle, +1, +16 and +120 renders); status; abort path (AudioCapture.Stop with ForceLevel(0) and
  readback first, replay End, writer close); replay lint 3.2 (`play.unityTime` error, `audio.suspend: on` and
  `audio.capture: record` warnings, no blur-fallback warning).
- P1: `play.order: step`, `look: clean`, `range`, `cams`.
- Acceptance: zero-error build with all owners; R1, R3, R4, R7, R11.

RIG
- P0: in MultiCamTargets, when `ReplayHub.replaying`: velocities by finite difference over the replay step (Rigidbody.velocity is
  frozen), `pos3`/`centre` and character velocity from `IReplaySource.Character`, `probe` from `IReplaySource.Probe`;
  everything else (bones, bounds, rb point = transform-driven pivot) reads the replayed transforms. MultiCamRig: SampleGame is
  not called in replay (CORE fills ctx.game from `GameCam`); verify gamelike and director read no live CameraScript state.
  Unchanged by Phase 0.
- Acceptance: a rehearsal-style plan pass over a recording produces the same poses.csv twice (bit-identical) and poses within
  1 cm of a v2 rehearsal of the same scene for static/follow cameras.

AUDIO
- P0: the AudioRules.cs ordering fix (8.2: SyncNative before Apply in Set). AudioCapture: Ensure self-subscription; Preflight
  with the any-endpoint rule, refusing Suspend capture without `_mod/acap_suspend_ok.json` and muter-only capture without
  `_mod/acap_muteronly_ok.json`; Start with folder creation, the relative path from GetBankPath (confirmed form, 6.1) and the
  2 s appearance check; `ForceLevel` (Change, twice until the fix is in, then the GetRTPCValue readback; a mismatch closes the
  window and aborts) for every window open and close; Suspend per flag with the rtpc lift only after 1 s of heartbeat
  inactivity (the swap took 1.30 s); `Marker(label, sample, gameTime, lag)` (Update phase only); Tick watchdog with the L5
  guard set (stale heartbeat, unmuted session, session peak > 0, active stream while suspended, no muted session while not
  suspended) and, outside windows, the rule re-force plus a GetRTPCValue readback every 10th frame; Stop idempotent incl.
  abort/quit, File.Move retry and RIFF check; `acap` commands; audio_sync.csv with `lag` and hold gaps. mcam_audio.py `fix`,
  `info` (fmt, cue/labl markers), `align` (fit over lag-0 markers + residuals, slope check 0.5 % -> `audio.usable`; no
  `--onsets`), `track` (output-timeline WAV from meta.csv incl. stretch/mute per segment and gap cutting; refuses unusable
  audio), `check` (RMS, peak in dBFS, transient vs the event marker). InsideMuter and the G0 launch step are Phase 0
  deliverables (both opt-in; G0 is not shipped).
- P2: AudioSpy call log and the per-camera/retime audio pass.
- Acceptance: offline synthetic WAVs with cue chunks (align residual < 1 ms on synthetic data; a capture paced at 44100
  frames/s under a 48000 header is refused by the slope check); in game, with the muter running, the session muted, no capture
  window and native enforcement on and off: rtpc 0 -> 100 -> 0 through AudioRules with one Change each and a GetRTPCValue
  readback after each (expected 0, 100, 0), followed by a silent S1-style capture at rtpc 0 through AudioCapture with periodic
  and event markers. R9 only after S3b or S2 has passed.

COMPOSITOR
- P0: mcam_take.py reads replay run.json modes and keeps the v2 timeline; `real_time` exposed; mcam_compose.py `--audio
  record|none` (default none) muxing `audio_track.wav` (AAC-LC 320 kb/s, or FLAC in MKV), refusing a take whose record.json
  `audio.usable` is false, ffprobe check updated; mcam_qa.py `replaydiff <take> --record <dir>` (PSNR/SSIM global and in masks
  from tracks.csv bounds, silhouette distance at 99 % coverage in masks, noise floor min(N1, N120) from `golden/noise`) and
  `audiosync`.
- Acceptance: R12; the existing compositor tests stay green.

DOCS
- P0: mcam_spec_check.py parses ReplayTypes.cs too and implements the 3.2 lint (incl. the 65 deg player-side warning,
  `cullers: live`, `onHold: mark`, `audio.suspend: on` and `audio.capture: record` warnings, the `play.unityTime` error);
  specs/replay_smoke.json (10 s, 2 cameras, no audio), a ChickenGun replay spec (3.3 with the v2 cameras and
  `audio.capture: none`); MULTICAM.md chapter "Replay mode" (workflow, commands, the sound outputs and the safety rule of the
  muted output, limits incl. the frozen `_Time` effects of L10); MULTICAM_ru.md short section.
- Acceptance: both specs pass with 0 errors; negative fixtures for each lint error.

LAB: L5 S3b, L5 S2 and L9b above. DET: runs Section 7 after R1 passes; the `rng_*` columns are read-only fingerprints (L6), and the only seed write is the re-seed to 0 in the prep.

### 8.4 Build and integration

- Core DLL: `build/build_mod.sh` (the MULTICAM_DESIGN 10 csc command: all `*.cs` under src/InsideDev, `-unsafe` already
  set; the particle copy needs it) writes `bin/InsideDev.dll`.
- Extensions: `build/build_lab.sh` writes `bin/InsideDev.ReplayLab.dll` (from `src/replaylab/`) and `bin/InsideDev.Det.dll`
  (from `src/det/`) with the same compiler options and a reference to `bin/InsideDev.dll`. During Phase 0 they were built
  against a fixed copy of the core DLL until v2 was finished.
- InsideMuter (opt-in): `build/build_muter.sh` writes `tools/muter/InsideMuter.exe`. The native readback plugin used by
  `record.captureGame`: `build/build_readback.sh` writes `bin/InsideReadback.dll`.

---

## 9. In-game test plan (pass criteria measurable)

The tests ran in a separate copy of the game, driven through MCP, with the muted output (InsideMuter running,
`audio force rtpc rtpc_master_volume 0` except inside L5/R9 capture windows). ChickenGun recipe: setup of 3.3. Lab results
go to `<game>/_mod/replay_tests/<test>/` (`lab out <dir>` picks another folder) with a one-line verdict.

### 9.1 Lab tests (Phase 0, `lab ...` commands)

The table is the plan as run on 2026-10-25. Measured values, verdicts and the metrics the lab replaced (L2 centroid, L4 and
L10 PSNR) are in Section 12. Still to run: L5 S3b, S2 and L9b.

| Test | Procedure | Pass criterion |
|---|---|---|
| L1 write order | Freeze after DoSpawn; for 120 frames write a chick root, its 6 bones, the bale and MotorWheel to offset poses in a RuntimeCallbacks.lateUpdate handler; compare at Camera.onPreCull. Also log the call order of Camera.onPreCull against a component OnPreCull on `_Camera_Main` (first and last component) | 0 transforms changed between the two points; otherwise the list of writers (component types) is the deliverable; onPreCull order reported |
| L2 skin timing, velocity and frozen blur | Move a chick and the bale at 2 m/s while frozen, writes at (a) lateUpdate -400, (b) DevRoot.LateUpdate, (c) onPreCull; grab each frame. Then the same per-render displacement once frozen (timeScale 0, replay writes) and once running (timeScale 1, a kinematic body moved by the lab at the same speed) | (a) silhouette at the written pose in the same frame (centroid error < 1 px vs projection); (b)/(c) results recorded as the reference for the rule "bones only in lateWrite"; frozen streak length within +-10 % of the running one, else `motionBlur: game` falls back to sub-frames (5.3) |
| L3 rigidbody and animation at timeScale 0 | Chick (Interpolate) and bale: write transform in lateUpdate with interpolation Interpolate / None, kinematic false / true; legacy Animation enabled | with None + kinematic the written pose persists to the next frame's onPreCull (delta < 1e-5 m); report the other combinations |
| L4 particles | Freeze during suction; GetParticles of FX_PipeSuck / straw systems, Pause, Clear, SetParticles back; grab before/after from the game camera and from a camera 60 deg off axis that sees the system only at its edge; repeat with `Simulate(0, false, false)` after SetParticles | PSNR >= 40 dB in the system's screen bounds from both cameras (an off-axis drop means stale bounds, then the Simulate variant must pass); cost per 1000 particles (Get + Set) < 0.3 ms; Marshal.SizeOf(Particle) == 84 |
| L5 audio capture | Order S1, S3, and S2 only if S3 fails. S1: muter verified, rtpc forced 0, capture 5 s with one marker. S3: Suspend(true), wait until the heartbeat shows no active INSIDE stream for 1 s, then rtpc forced 100, capture 60 s; variant S3b with `timeBeginPeriod(1)` during the capture ([speculation]: an `AK Suspended` thread that sleeps about 21 ms would wake every ~31 ms at the default 15.6 ms timer resolution; that predicts about 0.68x, while S3 measured 0.919x, which fits 44.1 kHz pacing better, 6.0 G3). S2 (fallback, muter only): rtpc forced 100, 10 s of ambience. S3 ran and failed on duration (0.919x, Section 12); S3b and S2 were not run | S1: file exists, size grows linearly (+-5 %), fmt chunk valid, cue/labl chunk with the marker, peak < -90 dBFS (value reported). S3: WAV duration within 0.5 % of wall time (S3 and S3b reported), RMS > -50 dBFS, during the whole capture no INSIDE session exists or every INSIDE session is muted and inactive with session peak 0 (heartbeat), never an unmuted one. S2: RMS > -50 dBFS, peak < -0.1 dBFS. Then write `_mod/acap_suspend_ok.json` (S3 or S3b only) or `_mod/acap_muteronly_ok.json` (S2 only); capture windows open and close through ForceLevel with GetRTPCValue readback |
| L6 RNG fingerprint | read seed, draw, read, set back, draw again | reading does not consume; the two draws are equal |
| L7 forced cullers and emulation | valid = false + RefreshRuntimeState on 3_Interior_CULL and 1b_IndoorDetails_CULL with the free camera outside their facing planes; then unhook UpdateBoundsCullers, call `UpdateBoundsCullers(c => false)` from onPreCull while the camera moves in and out, and hide by IsVisble | groups active and rendered next frame; list of OnEnable side effects; 0 sound posts and 0 SignalOut fires caused; with the emulation: 0 SetActive calls (staticSetVisible counter 0), IsVisble matches the live culler result of the same pose in >= 99 % of frames |
| L8 RuntimeCallbacks instances | FindObjectsOfType<RuntimeCallbacks>() and a counter in a static handler | count reported; frame guard keeps handler work to once per frame |
| L9 static shadows | Move the bale under SpotLight_Machine while frozen with useStaticShadows as is / dirty / false; also hide a culler group under that light by renderer | per mode: the bale shadow centroid follows the bale within 2 px; hidden group casts no shadow (yes/no); GPU ms per mode (Perf) |
| L9b static shadows, second scene | A scene with a useStaticShadows light over a native mover (candidate: the `#fields` darkroom DirLocal with a mover of that zone): move the mover while frozen in modes as is / dirty / dynamic / forced static, with a no-cast reference pass (shadowCastingMode Off); hide a culler group under that light by renderer | per mode the shadow follows within 2 px; hidden group casts no shadow into the static cache (yes/no); child decals counted as part of the object |
| L10 shader time | Freeze 3 s; grab water, flies, drips at 0 / 1.5 / 3 s; then set `_Time` and `_RealTime` in OnPreCull and OnPreRender | per material: PSNR between the 0 s and 3 s grabs (< 40 dB = animates when frozen); the override is effective when, frozen, grabs with `_Time` set for T = 0 and T = 3 differ (PSNR < 40 dB) in that material's mask |
| L11 rope Draw | Freeze with the crane rope swinging; disable RopeLineRenderer, write recorded vertex positions, call Draw by reflection with postSleepDraw false, for 60 frames at 0.2x | rope silhouette within 1 px of the record frame at sample times; no Draw skipped (counter); no exception |
| L12 camera probe | `srec probe camera` in ChickenGun and one water scene | list includes every WaterDust dust object and LensFlareSimple; 0 rigid gameplay objects listed |

### 9.2 Integrated tests (Phase 2)

| Test | Procedure | Pass criterion |
|---|---|---|
| R1 record overhead and pacing | `mcam record` ChickenGun at the output resolution (2560x1440), clock real, with the ChickenGun replay spec of 3.3 (captureGame false: record.json `pacing.grade` = pacing; a captureGame record is judged only on its `nograb_*` statistics) | sampling mean < 1.5 ms, p99 < 3 ms per frame (report `sampleCost.channels` and `quietTransforms`); record.json pacing: `mode` and vSyncCount/targetFrameRate as chosen in 2.3.4 (incl. the preroll `calibration`), captureFramerate 0 on every frame, 0 pacing changes; engine frame interval (`pacing.dt_*`, Time.unscaledDeltaTime) mean 16.7 ms +-0.3 ms and >= 98 % within 15-18.5 ms; no GC pause > 5 ms; drift |r(t) - t| < 1 frame at the end (`drift.hitch_loss_ms` and `residual_ms` reported); cues `bound` with `cues.max_ms` < 4; record.json `runnerPhase` = after LateUpdate (2.2) |
| R2 file integrity | `srec_tool.py verify` | all CRCs ok; sample count = record_meta rows; every OBJA/OBJD consistent; file size within 0.5x-2x of the 4.4 formula evaluated with this recording's counts |
| R3 golden | `mcam golden` (every sample rendered, every 10th grabbed) vs `record/game` frames | first measure the noise floor N = min(N1, N120) from `golden/noise` (consecutive renders and 120 renders apart, for the slow frozen edge drift of L10); pass: global PSNR >= min(32, N - 6) dB and in moving-object masks >= min(30, N - 8) dB, SSIM >= 0.95; preCull read-back: 0 driven objects whose rendered transform differs from the recording at T by more than 1 mm or 0.05 deg (rotation metric of 2.3.2); rope silhouette within 2 px of the record frame at 99 % coverage (L11); `_Time`-animated masks (L10) excluded and reported |
| R4 identity across passes | Replay the same camera twice in separate passes | per-step poses.csv and tracks.csv bit-identical; pass start hashes equal; frames PSNR >= N - 3 dB with N the R3 noise floor (TAA, `_RealTime_local` grain, frozen edge drift and native noise only; N reported); passes[].rngStart/rngEnd reported |
| R5 slow motion smoothness | 0.2x segment around the fall | chick/bale second differences of position on the output timeline: no step > 3x the local median except at cut flags; slow rotators (extent > 2 m): no output frame with zero angular change between two frames that both have non-zero change |
| R6 game blur | Speed 1 and 0.25 with `motionBlur: game`; also the first frame of a pass and the first frame after a director run | falling-bale streak length within +-20 % of the record frame at speed 1; no streak on pass start or run-start frames (streak < 2 px for static objects); report the shader clamp; strength scaling: streak length at strengths 0.4, 0.8 and 1.6 proportional within +-15 %; a flying chick against its record frame within +-20 % (skinned velocity bake); running references only with non-AI bodies (L2) |
| R7 restore | After `mcam replay`, compare every recorded object with the final sample; unfreeze for 5 s | max position error < 1 mm; no exceptions in the log; chicks move after unfreeze; the game camera is back at the recorded final pose before the unfreeze (boy position change during the 5 s < 2 m, no fall); cullers re-hooked and CullingActive true |
| R8 re-shoot after reload | New session: default `play.setup` (record setup minus cues), resolve, golden test | >= 99 % of non-spawned dynamic objects resolved, 0 ambiguous; baseline diff written; R3 PSNR within 1 dB of the same-session value |
| R9 sound end to end | `mcam record` with capture, after S3b (Suspend flag) or S2 (muter-only flag) has passed | fit slope within 0.5 % of the fmt rate (record.json `audio.usable`); marker fit residual RMS < 5 ms; GetRTPCValue readback equals the window level inside and 0 after; bale landing onset (first sample above -40 dBFS after p(r(t_baleLand)), relative to that position; the lag-1 `ebaleLand` marker offset reported) reported, and its spread over 3 takes < 1 output frame; heartbeat: never an unmuted INSIDE session, and under Suspend never an active one; rtpc back to 0 after stop (`audio rules` and GetRTPCValue) |
| R10 monitor | During all replay passes | 0 SignalOut fires, 0 FSM state changes and 0 Finished toggles on recorded objects, 0 culler SetActive (staticSetVisible counter), 0 subscene loads or activations, 0 sound posts other than pause/resume, 0 timeScale leaks |
| R11 replay cost | 6 cameras, 12 s take, 1440p, look game | ms per step per camera reported; total <= 30 % of the v2 capture time of the same spec |
| R12 compositor | `mcam_compose.py all --audio record` + `mcam_qa.py qa` | qa exit 0; ffprobe: hevc hvc1 + one audio stream, durations within one frame; angles and layouts encode (hevc_nvenc Main10 by default; a cut only from an explicit `compose.edit`, `all` never drafts an autocut) |
| R13 input replay | Section 7 runs | verdict per 7.6 (`tools/det_compare.py`) |

---

## 10. Unverified assumptions and corrections

### 10.1 Assumptions

| Assumption | Risk if wrong | Test | Status after Phase 0 |
|---|---|---|---|
| Writes in RuntimeCallbacks.lateUpdate (-400) are not overwritten before render except by the known order-0+ writers | flicker, conflicts | L1, srec probe | closed (L1: 0 of 9 changed over 120 frames); `srec probe` stays mandatory per new scene class |
| The native tag LateUpdate is in the same sorted LateUpdate list as scripts (order -1), so -400 runs first (inferred from AddBehaviour 0x140179000, sort not disassembled) | skinned blur one frame late | L2, R6 | rendered pose closed (L2: a/b/c render the written pose in the same frame); skinned velocity bake open [unverified R6] |
| The native tag compares against Camera.main in OnWillRenderObject (0x140228e10 inferred) | no object blur in replay | R6 | open [unverified R6]; rigid object blur frozen measured equal to running (L2) |
| Rigidbody interpolation / kinematic do not rewrite driven transforms at timeScale 0 when set to None + kinematic | jitter | L3 | closed (L3: true for all four combinations) |
| SetParticles on a paused system renders exactly, incl. size/colour over lifetime | particle look differs | L4, R3 | closed (L4): false for a plain SetParticles (particles vanish); exact with Simulate(0, false, false) |
| Wwise capture works in the release DLL, records post-master before the OS mixer, and keeps recording under Suspend(true) | no sound or audible output | L5 | closed (L5 S1, S3): works, records with the OS stream inactive, but suspended capture runs at 0.919x real time and is unusable for sync; S3b and S2 open |
| Reading UnityEngine.Random.seed does not consume a value | wrong determinism fingerprint | L6 | closed (L6: true; the setter re-seeds, no restore) |
| Forced cullers have no gameplay side effects beyond OnEnable of their content | different take | L7 | closed (L7: 0 sound posts, 0 SignalOut, 0 FSM changes) |
| Static shadow caches show replayed movers | stale shadows | L9 | open [unverified L9b]: ChickenGun has no static-shadow light over a visible mover |
| `_Time`-driven shaders freeze at timeScale 0 and the override works | frozen water/flies | L10 | closed (L10): they freeze, the override has no effect; class C |
| ColorBlendProbe active blend-box set at the end equals the set during the take (boy stands still in ChickenGun) | wrong colour blend in other scenes | R3 | content version 1 recordings only: version 2 records the globals (2.3.2 shader state) |
| The shader-state writers left the values the recorder reads (globals from the probe and Player2 fields, `Material.GetVector` after Shader_SetHuddlePosition.Update, `GetPropertyBlock` after the arm/leg LateUpdate), `Material.HasProperty` is true once the game set a property, and preCullMain writes are not overwritten before render | huddle shading or bruises differ between record and replay | record.json `shaderState` (channels, keys, cost) and a cafeteria -> CEO office golden: `_PDHuddleAmbient` / `dam_*` at the table, wall and office | open [unverified, offline only] |
| The TAA jitter index advances once per main-camera render | pass-to-pass noise differences | R4 | open |
| Off-screen Shuriken culling does not freeze side-camera particles during recording | stale particles | record.json flag, R3 | open |
| The execution orders from the PS4 data equal Steam | hook order | L1 | -400 before order 0 confirmed (L1 trace); -615/-600 unmeasured, and no decision depends on them any more (markers are posted in the Update phase) |
| The native velocity buffer and TAA produce normal blur at timeScale 0 (the managed latch is never used) | no or wrong game blur | L2 | closed (L2: 13.5 vs 15.0 px, ratio 0.90); strength scaling open [unverified R6] |
| Camera.onPreCull fires before the component OnPreCull messages | preCull writes overwritten by camera-driven writers, or the reverse | L1, L12 | closed, false (L1): the delegate fires after every component OnPreCull; consequences in 2.2 |
| A paused system after SetParticles keeps correct bounds for off-axis cameras | particles culled in side views | L4 | closed (L4: with Simulate, 248 px changed against a tolerance of 325 at 60 deg off axis) |
| `Suspend(true)` replaces the sink before an rtpc lift could become audible | audible output | L5 S3 (lift only on the heartbeat) | closed (L5 S3): not on return; the OS session went inactive 1.30 s later, so the heartbeat rule is required |
| PlayerPrefs `Volume` is the registry value shared by all INSIDE installs of the Windows user | G0 misses, or changes the Steam install | G0 launch step reads it back | closed by the Phase 0 launch step: `HKCU\Software\Playdead\INSIDE\Volume_h3158011725` |
| RopeLineRenderer Draw by reflection with postSleepDraw false redraws a disabled component's rope | frozen crane rope | L11 | closed (L11: 80 of 80 Draws, within 2 px) |
| Hiding a culler group by renderers matches the game's SetActive look, incl. static shadow caches | shadow of hidden groups | L7, L9 | visibility closed (L7: 100 % IsVisble match); static caches open [unverified L9b] |
| The record step coroutine resumes after LateUpdate in every session, as measured for one bridge-started coroutine | markers, cues and SampleGame in another phase | runner phase assert (2.2), R1 | open, asserted at runtime |
| Re-seeding `UnityEngine.Random.seed = 0` twice gives the same stream | DET prep not deterministic | DET S1-S3 | open |
| With SyncNative before Apply, one AudioRules.Change reaches Wwise | window level wrong, audible level after close | AUDIO acceptance (readback), R9 | open; ForceLevel calls Change twice until verified |

### 10.2 Corrections to the research inputs

- Velocity tags in ChickenGun: one input said nothing in level171 is tagged; the velocity-tag list extracted from the level data has 38 tags in level171
  (chick meshes skinned, bale box parts, machine wheels, belt, wires, door boards). Chicks and the bale do get object blur.
- Chicken random scale and yaw are applied in `OnPreProcess` behind the serialized `preProcessed` flag (AC/Chicken.cs:151-163),
  not in Start; re-activating a chick in replay does not re-randomize it. PreAwake draws (AC/Chicken.cs:165-194) run once.
- InsideMuter mutes the sessions (ISimpleAudioVolume.SetMute) instead of setting volume 0.
- Lifting `rtpc_master_volume` by removing the rule restores the game value only if the game set it after the rule was
  installed (MC/AudioRules.cs:99-102 uses `gameRtpc`); the capture window therefore forces an explicit `audio.level` instead.
- Revision 1 said PlayMakerFSM.OnDisable resets the FSM. It calls `Fsm.Stop`, which resets only with RestartOnEnable
  (PM/HutongGames.PlayMaker/Fsm.cs:1271-1296); the FSMs in the ChickenGun culler groups have RestartOnEnable false
  (IDX/../fsm/level204__~..._fxVoumeCone_hiDef_c0ef65__FSM__2180.json, ..._Moth_PM_da726a__FSM__2198.json), so they stop
  and resume.
- Revision 1 listed RopeLineRenderer as frozen because it steps with deltaTime 0. Step is not a no-op at deltaTime 0:
  `BlendStep` lerps by `blendCursor`, not by the frame fraction (AC/RopeLineRenderer.cs:55-65; the cursor is normally 1), and
  the feetDelta smoothing lerps by 0.1 per frame without deltaTime (:239, :243), so the live rope drifts frame by frame while
  frozen. It is now replayed (2.4.3).
- The InsideMuter used before Phase 0 expired after its minute argument (default 240). The Phase 0 build
  (`src/muter/InsideMuter.cs`) ignores that argument and runs until 10 s after the last INSIDE.exe ended.
- CameraLightSwap does not swap light values: it calls `SetActive` on its light GameObjects (AC/CameraLightSwap.cs:47-50).
- The Wwise base path evidence is FP/Playdead.Wwise/AkGlobalSoundEngine.cs:379-381 (revision 1 omitted the folder).
- Revision 2 placed `yield return null` coroutines after Update (its 2.2 row 4) and posted markers from the record step. They
  resume after every LateUpdate, before the first OnPreCull (L1), so markers moved to `preUpdate` (6.1).
- Revision 2 left the order of Camera.onPreCull against component OnPreCull open and planned around "MultiCamLook runs
  last". The delegate fires after every component OnPreCull (L1).
- Revision 2 expected the RuntimeCallbacks events to fire twice per frame (two instances in the data). One instance exists at
  runtime (L8).
- G4 of revision 2 said forcing a value re-sends it (MC/AudioRules.cs:99-102). With native enforcement on, the re-send happens
  before SyncNative and Wwise receives the previous rule value (L5).
- The rotation metric of revision 2, `2 asin(|q - q_last| / 2)`, is half the rotation angle; the angle is `4 asin(...)`
  (2.3.2; found when the lab's own `2 acos(|dot|)` metric produced false changes).
- Revision 2 proposed overriding `_Time` (`play.unityTime`). The engine sets it per camera, so the override does nothing
  (L10).
- Revision 2 listed WaterDust as the water camera's transform writer. WaterEdge (edge placement, SetActive and
  `_Clip`/`_Disp`) and AudioCameraProbe are camera-driven too (L12, AC/WaterEdge.cs:107-160, FP/AudioCameraProbe.cs:28-32).

---

## 11. Rejected critique (review of revision 1)

All 28 review points were checked against the code. Most were applied in full (the sections are named in the summary of
changes below). This section lists only the parts that were rejected or changed, with the reason. It records revision 2;
where Phase 0 changed a decision (Section 12), the cited sections and Section 12 take precedence.

| Point | Rejected part | Reason (evidence) | Applied instead |
|---|---|---|---|
| 2 | "force vSyncCount 1" as the only option | vSyncCount 1 locks to the display refresh rate, which is 60 only on a 60 Hz display; on 120/144 Hz it would sample faster than 60 Hz | vsync 1 when `Screen.currentResolution.refreshRate == 60`, else vSyncCount 0 + targetFrameRate 60 (2.3.4) |
| 3 | "PlayMakerFSM resets and restarts (RestartOnEnable); 1b_IndoorDetails_CULL has 2 FSMs" | The count is right, but both FSMs (lamp `fxVoumeCone_hiDef` LookAt, `Moth/PM` PlayAnimation) have RestartOnEnable false: OnDisable only stops them and OnEnable resumes the active state (PM/HutongGames.PlayMaker/Fsm.cs:1150-1162, 1271-1287) | The point itself stands through SetActive side effects (DirtyStaticShadowManager, Finished toggles, particles, light cullers); fix applied as proposed (2.4.2, 2.4.4) |
| 9 | "set the game copy's saved master volume to 0" | The volume is PlayerPrefs `"Volume"` (FP/SystemSettingsPlayerPrefs.cs:36-47); PlayerPrefs are stored per Windows user and product, not per install (key confirmed by the Phase 0 launch step, 6.0 G0), so it cannot be limited to the game copy | G0 as an opt-in step with the shared-setting note (6.0) |
| 13 | "disable the UmbrellaBehaviour; OnDisable removes it" | Removal works, but re-enabling at restore runs CrowdDude.OnEnable, which draws UnityEngine.Random and calls `animator.Play` (AC/CrowdDude.cs:307-389) | Remove from and re-insert into `Umbrella<CrowdDude>` directly (FP/Umbrella.cs:72-82), no enable toggling (2.4.2) |
| 16 | "scale rotEps by object extent" in the `1 - |dot|` metric | For a 5 m object the needed threshold is about 5e-11, below the float precision of `1 - |dot|` (about 6e-8) | Angle metric `4 asin(|q - q_last| / 2)` (revision 2 wrote 2 asin, corrected in revision 3) in double with `θ = clamp(posEps / extent, 2e-6, rotEps)`; pre-keys and interpolation across gaps (2.3.2, 4.2) |
| 17 | "cut on isKinematic toggles" (both directions) | true -> false is the shot (AC/ChickenGunLogic.cs:383): the chick leaves the muzzle continuously, a cut would turn its first sample into a step | Cut on false -> true only (the capture teleport, :394-402), plus the velocity bound (2.3.2) |
| 18 | "with dt 0, Step blends by fraction 0" and "write the vertices in preUpdate so the live Update/Draw draws the replayed rope" | `BlendStep` lerps by blendCursor (normally 1), so Step snaps the vertices to targets from the bodies and `rope.handPos`/feetDelta (AC/RopeLineRenderer.cs:55-65, 154-253) in the same Update, after preUpdate and before Draw; the preUpdate write would be overwritten | Disable RopeLineRenderer (no OnEnable/OnDisable) and call `blendRope.Draw` by reflection in lateWrite after writing the recorded vertices (2.4.3, L11) |
| 21 | "MultiCamLook.OnPreCull runs after DepthBufferDownsample, so sub-renders see the replay clock" (complete) | Correct for culling, OnWillRenderObject and every OnPreRender, but a sub-render inside the OnPreCull of a component between DepthBufferDownsample and MultiCamLook would still see the wall clock; none found in managed code (WaterCameraRendering.OnPreCull renders nothing, AC/WaterCameraRendering.cs:228-274) | Fix applied with the residual marked for L10 (2.4.7); revision 3 dropped the OnPreCull write because L10 found no visible `_RealTime` consumer |
| 26b | "Mono 2.0 BCL traps" | Not runtime traps: the build uses `-nostdlib` against the game's mscorlib (MULTICAM_DESIGN 10), so these APIs fail to compile | Kept as a hint list for owners (2.6) |
| 28 | New player-side lint | v2 already errors on a forward with z < -0.05 while pitch < 60 and clamps positions (MULTICAM_DESIGN "Player-side clamp"); only the 65 deg band and the top-down coverage rule were missing | 65 deg warning + `play.coverageOk` (3.2) |

Claims from the review that were confirmed and need no change: Camera.onPreCull is never assigned by the game; the game never
calls Wwise Suspend or Wakeup; the 38 level171 velocity tags exist; the Particle struct is 84 B with the stated offsets; the
MaterialInstance setters apply immediately; ColorBlendProbe has no deltaTime smoothing; Maximum Allowed Timestep is 0.1;
RuntimeCallbacks.lateUpdate has no game transform writers; Chicken scale and yaw are set in OnPreProcess;
GetOrAdd*Property, SubsceneCuller.ContentRoot/SceneName and Light.useStaticShadows/staticShadowsDirty exist.

Summary of applied changes by review point: 1 flags and visibility owner (2.3.2, 2.4.4); 2 record pacing (2.3.4, R1);
3 cullers never toggle, `cullers: camera` (2.4.2, 2.4.4, 2.4.8); 4 TAA reset and warmup along shot poses (5.3); 5 golden
renders every sample, noise floor (3.4, R3); 6 frozen blur [unverified] with fallback (5.3, L2); 7 L5 order and lift on
heartbeat (6.0 G3, L5); 8 guard rules, self-subscription, string value (6.0 G2/G4/G5, 2.6); 9 G0 and muter without expiry
(6.0); 10 holds, -90 dBFS, timer variant (6.2, 6.3, L5); 11 write-once by session, shared materials P0, default setup,
spawned excluded (2.3.2, 2.4.1, 2.4.3, 3.1); 12 quiet activation (2.4.4); 13 neutralize table (2.4.2); 14 probe-carrying
roots in preUpdate (2.4.3); 15 local-space keys (2.3.2, 5.2); 16 pre-keys and gap interpolation (2.3.2, 4.2); 17 cut rule
and subtree wrapAroundFrame (2.3.2, 2.4.3); 18 rope replay (2.4.3, L11); 19 streaming freeze and keep-loaded handover
(2.3.3, 2.4.2); 20 pass isolation and state hash (2.4.9, R4); 21 `_RealTime` in OnPreCull (2.4.7); 22 camera probe
(2.3.1, 3.4, L12); 23 criteria (9.1, 9.2); 24 ownership and pinned DLL (8.1, 8.2, 8.4); 25 DET fixes and run P1 (7.1, 7.4,
7.6); 26 off-axis particles (2.4.5, L4); 27 WAV path and finalization, onset fallback (6.1); 28 lint (3.2).

---

## 12. Lab results (Phase 0)

One lab session on 2026-10-25 in a separate copy of the game, driven through MCP only, with `InsideDev.ReplayLab.dll`
0.2.0 (built from `src/replaylab/`) against the unchanged core. ChickenGun recipe of 3.3 unless noted; L10 and L12 also ran
at the lake (water scene). Results: one folder per test under the lab output root (`<game>/_mod/replay_tests/<test>/` by
default: result.json, verdict.txt, log.txt, grabs), earlier runs of each test in its `history/`. The alternative runs cited
below (L2 chick run, L4 game/off-axis v2, L5 S3 run 1, L9 behind/front/darkroom, L10 lake and ChickenGun v2, watchdog logs)
were kept outside the repository and are not shipped. The grabs of L2,
L4, L7, L9, L10 and L11 were reviewed before judging. A FAIL below means the 9.1 criterion failed; the design was adapted to
the measured behaviour in every case.

### 12.1 Results

| Test | Verdict | Measured | Decision (sections changed) |
|---|---|---|---|
| L1 write order | PASS | 0 of 9 transforms (chick root, 6 chick bones, bale, MotorWheel) changed between the RuntimeCallbacks.lateUpdate (-400) write and Camera.onPreCull over 120 frames (maxPos 0, maxRot 0). Component OnPreCull messages fire before the `Camera.onPreCull` delegate: the first component (PDReflectionCuller) in 119 of 119 frames, and the probe added last too. Trace per frame: rc.update > rc.lateUpdate > order-0 LateUpdate > yield-null coroutine > component OnPreCull > Camera.onPreCull > component OnPreRender > Camera.onPreRender > OnPostRender. lateUpdate and order-0 LateUpdate each fire once per frame. | lateWrite (-400) is the write point and holds until culling (2.2, 2.4.3). Assumption "onPreCull before components" is false: preCullMain overrides component OnPreCull writers, so camera-driven ones are excluded from its writes (2.2, 2.3.1); MultiCamLook.OnPreCull runs before preCullMain. The runner coroutine runs after LateUpdate, not after Update: markers move to preUpdate (6.1), cues act from the next frame, runner phase asserted at runtime (2.2, 8.3 CORE). |
| L2 skin timing and frozen blur | PASS | Bale isolated in front of the back window at 3 m/s (28.4 px/frame): write points a (lateUpdate -400), b (order-0 LateUpdate), c (onPreCull) matched a static render of the written pose in 12 of 12 frames each (PSNR 42.2 / 41.8 / 41.8 dB, against 30.9 dB for the previous pose). Chick alone, skinned, 48 px/frame: a/b/c 12/12 at the written pose (42.7 vs 29.9 dB). Centroid error 3-7 px (shading and shadow bias, so the centroid metric was replaced). Engine blur, strength 0.8, jitter off: frozen streak 13.5 px against 15.0 px running, ratio 0.90; differing blur pixels 3408 / 3221 = 1.06. The chick's running reference was invalid (Chicken AI rotates and moves the visual at timeScale 1). | Bones and rigid bodies written in lateWrite render the same frame; b and c are equivalent, so the preCull rigid re-assert is safe (2.4.3). Frozen engine blur equals running blur: `motionBlur: game` stays without the sub-frame fallback (0, 1.1, 3.1, 3.2, 5.3). Ratio at the 0.90 edge: R6 confirms strength scaling and the skinned velocity bake; running references use non-AI bodies (9.2 R6). |
| L3 rigidbody and animation at timeScale 0 | PASS | Chick (Interpolate) and bale, all 4 combinations of Interpolate/None and dynamic/kinematic: a pose written in lateUpdate stayed until the same frame's and the next frame's onPreCull (0 m, 0 deg). Chick root bone under a playing legacy Animation (culling BasedOnRenderers): kept in the same frame (0 deg), overwritten the next frame (20.0 deg back), also at timeScale 0. | Rigid writes persist whatever the settings; None + kinematic kept as a safeguard (2.4.2, 2.5). Legacy Animation re-samples every frame while frozen: animated objects and bones are written every frame, never write-once (2.2, 2.4.2, 2.4.3). |
| L4 particles | PASS | FX_PipeSuck and FX_StrawBlow during suction: 11 systems, about 400-430 particles. Game camera: 18830 particle pixels; after Get/Pause/Clear/SetParticles 19999 pixels changed inside the dilated particle mask (the particles vanished); after SetParticles + Simulate(0, false, false) 41 changed (tolerance 376). Player-side camera 60 deg off axis with the stream at the frame edge: 16279 particle pixels, plain set changed 18129, set + Simulate 248 (tolerance 325). Full-frame PSNR 41-43 dB in every case (frame-sized bounds hide the vanish). Marshal.SizeOf(Particle) = 84. Get + Set 0.034-0.155 ms per 1000 particles. | Always `Simulate(0f, false, false)` after SetParticles on a paused or cleared system, in the per-frame apply and in quiet activation; no stale-bounds problem with it; cost negligible (2.3.2, 2.4.4, 2.4.5, 2.5). |
| L5 S1 capture | PASS | File at `..\..\..\_mod\audio\<name>.wav` relative to GetBankPath, no platform subfolder; appeared after 0.01-0.02 s; linear growth 191.7 kB/s (max deviation 0.25 %); PCM 48000 Hz, 2 ch, 16 bit; cue chunk with labl `lab_s1`; 5.013 s WAV for 5.025 s wall; peak -200 dBFS (digital silence at rtpc 0). Heartbeat: unmuted 0, peak 0, max age 0.3 s. | Capture works in the release DLL; the relative path rule is confirmed; cue/labl markers are written, so the onset fallback is dropped (4.3, 6.1, 8.3 AUDIO). |
| L5 S3 suspended capture | FAIL | Run 1, one AudioRules.Change: RMS -200 dBFS, the rtpc never reached 100 (native-enforcement ordering bug). Run 2 with the lab fix (Change twice, GetRTPCValue readback 100 inside the window, 0 after): Suspend(true) returned AK_Success, the OS session went inactive 1.30 s later; WAV 55.125 s against 60.013 s wall (-8.14 %; 176.4 kB/s = 0.919x real time, growth deviation 0.05 %); RMS -44.0 dBFS, peak -21.5 dBFS, 12 cue markers. During the capture: heartbeat active 0, peak 0, unmuted 0, max age 0.3 s; no watchdog tripped. Run 1 (silent content) was 1.04 % short. 176.4 kB/s = 44096 frames/s, within 0.01 % of 44100. | Suspend silences the OS stream and records content but runs about 8 % slow, failing the 0.5 % criterion: `_mod/acap_suspend_ok.json` not written, Suspend capture unusable for sync, muted sound takes blocked until S3b or S2 pass; [speculation] 44.1 kHz pacing of the dummy sink, which S3b would not change (0, 1.2, 6.0 G3). Lift on the heartbeat, never on the return (G3). AudioRules.Set ordering bug fixed in Phase 1 and every window level verified by GetRTPCValue readback (6.0 G4, 8.2). Slope check refuses slow audio (6.2, 6.3). Watchdog gets the lab guard set (G5). |
| L6 RNG fingerprint | FAIL | Three reads of Random.seed do not consume (draws equal: 0.870586 / 0.870586). The getter changes on every draw (1902713416 -> -851270697). After setting the read value back the draw differs (0.046932 vs 0.870586). | Random.seed is a read-only fingerprint (DET `rng_*` columns fine). The setter re-seeds and Unity 5.0 has no Random.state: the stream cannot be saved and restored, so DET and replay never write it to restore; the replay reports it per pass outside the state hash (2.4.9, 4.1, 7.1, 7.3). |
| L7 forced cullers and emulation | PASS | Player-side poses. valid = false + RefreshRuntimeState: 3_Interior_CULL inactive -> active with 14 renderers visible the next frame; 1b_IndoorDetails_CULL stayed active with 50 visible. OnEnable types: BoundsCullerRuntime, QueueInstance, MaterialInstance, VfxParticleController, ParticleSpeedScale, DecalSpecularParams, PlayMakerFSM, ParticleSystem.playOnAwake, Animation.playAutomatically. 0 sound posts, 0 SignalOut, 0 FSM changes. Emulation (UpdateBoundsCullers unhooked, UpdateBoundsCullers(c => false) from onPreCull): 0 SetActive calls (97 in the live reference), IsVisble equal to the live previousVisible in 100 % of 15708 comparisons over 66 zone cullers; auto-placed poses at z = 0 identical. | `cullers: camera` confirmed exact; forcing cullers has no gameplay or sound side effects in ChickenGun (2.3.3, 2.4.4, 2.5). |
| L8 RuntimeCallbacks instances | PASS | 1 instance (`_Globals`), 1 including inactive objects and assets. update, lateUpdate and ExecuteInEditModeIncluded.update 1 call per frame over 120 frames; fixedUpdate once per logic step (124/124). | The static events fire once per frame in this build; the frame guard stays as cheap insurance (2.2, 7.1). |
| L9 static shadows | INFO | The only shadow-casting light near the machine is SpotLight_Machine (Spot, Hard, useStaticShadows false). The first run's "shadow follows" was the bale's own child DECAL_AO_Box_Analytic_HayBale. With a no-cast reference pass (shadowCastingMode Off) the bale shadow was 0 px in modes as-is, forced static, static + dirty and dynamic, at two placements (219.6 / z 0.6 and 220.3 / z -0.8). The only loaded static-shadow light is the `#fields` darkroom DirLocal (useStaticShadows true, Soft); a bale moved there is not rendered (its ChickenGun culler group deactivates). Group shadow under the light: not visible. Frame 86-128 ms per mode (CPU frame time, not GPU). | Not decidable in ChickenGun: `play.shadows` default stays `recorded`, `dirty`/`dynamic` are diagnostics; the static-cache test moves to L9b in a scene with a useStaticShadows light over a native mover (2.4.6, 9.1). Child AO decals count as part of the object (2.3.1, 2.5). |
| L10 shader time | FAIL | ChickenGun: 9 of 16 masks change while running (rain lines, mud ripples, splash edges, grain chute); frozen they stay at 0-3.5 % of the running change; FX_Moths 12 % (32 px). Overriding `_Time` or `_RealTime` with Shader.SetGlobalVector in Camera.onPreCull/onPreRender and in component OnPreCull/OnPreRender changed 0 px in every mask. Lake: rain lines and water Refract/Reflect/Fog (+ edge) change 82k-124k px per 0.5 s running, 455-1021 px over 3 s frozen; overrides 0 px. After settle, consecutive frozen frames differ by 0 px, but bright edges drift slowly over seconds. | `_Time`-driven water and rain shaders freeze at timeScale 0 and the global override has no effect (the engine sets `_Time` per camera): `play.unityTime` dropped, these effects are class C (0, 1.1, 1.2, 2.4.7, 2.5, 3.1, 3.2). No visible `_RealTime` consumer: the revision 2 MultiCamLook.OnPreCull write is dropped (2.4.7, 8.2). The drift sets the R3/R4 noise floors (3.4, 9.2). |
| L11 rope Draw | FAIL | Crane rope, kick 0.02: amplitude 1.14 m, SplineUniform, 15 vertices. RopeLineRenderer disabled, recorded vertices written, BlendRope.Draw by reflection with postSleepDraw false: 80 of 80 Draws, 0 exceptions, 0 skipped. Silhouette distance to the record frame at 99 % coverage per sample: [2,1,1,1,2,1,2,1,1,1,1,1] px (max 2), about 8-9k mask px per sample. A kick of 1 flipped the rope over the beam (7.6 m). | The 2.4.3 rope mechanism works; only the strict 1 px criterion failed: R3 uses a 2 px rope tolerance; lab kicks stay small (2.4.3, 2.5, 9.2 R3). |
| L12 camera probe | PASS | ChickenGun: 18058 transforms scanned, 0 changed without camera motion; 10 camera-driven: 9 LensFlareSimple and AudioCameraProbe (Rigidbody + SphereCollider, follows Camera.main); 0 WaterDust active; 0 rigid gameplay objects. Lake: 16919 transforms; 10 camera-driven: 2 LensFlareSimple, AudioCameraProbe, and `WaterTrigger2_Lake/InverseTransform/WaterEdge` with its 6 renderers (Fog, Reflect, Refract, each Inside and Outside); 0 WaterDust active; 0 rigid. | The built-in camera-driven list contains LensFlareSimple, AudioCameraProbe and the WaterEdge subtrees (plus WaterDust from code); never recorded or driven; WaterEdge's `_Clip`/`_Disp` MaterialInstances excluded; AudioCameraProbe's body left out of the rigidbody freeze (2.3.1, 2.4.2, 2.4.4, 2.5, 3.4). |

### 12.2 Open after Phase 0

- Sound capture path of the muted output: L5 S3b (`timeBeginPeriod(1)`) and S2 (muter-only). Until one passes, a muted
  capture is refused by Preflight (6.0 G3); the default `audible` output does not depend on them.
- Static shadow caches with replayed movers and hidden groups: L9b (2.4.6).
- Blur strength scaling, the shader clamp and the skinned velocity bake: R6 (5.3).
- TAA jitter phase per render (R4), the ColorBlendProbe blend-box set (R3), off-screen Shuriken culling during the record
  (record.json flag, R3), a visible `_RealTime` consumer in other scenes (R4).
- The runner coroutine phase in contexts other than a bridge-started coroutine: asserted at runtime (2.2).
- DET: whether two re-seeds to 0 give the same stream (S1-S3).
- The AudioRules fix: verified by the AUDIO acceptance readback test before ForceLevel drops its second Change.

### 12.3 Lab method changes that carry into the design

- Rotation deltas: `4 asin(|q1 -/+ q2| / 2)` in double. `2 acos(|dot|)` reads about 0.04 deg for identical float quaternions
  (|q|^2 < 1); it caused a false L1 FAIL (206 "changes" at maxPos 0) and 867 false L12 control hits. Used by the recorder,
  `srec probe` and `srec probe camera` (2.3.2, 3.4).
- Settle: grabs right after a TAA reset differ at edges for about 5-10 frames (jitter and TAA settling); the lab waits 12
  renders before a reference grab. `golden/noise` starts after the same settle (3.4).
- Bounds: only shown, non-decal renderers count (the chick's DECAL_AO_Finalpass and DECAL_ReflectPoint have about 1 m
  extents, the bale has an AO decal); collider bounds for placement (2.3.1, 2.3.2).
- Image metrics: changed-pixel counts inside dilated object masks (renderers off against on) with a noise floor, and static
  reference renders per pose, instead of full-frame PSNR and centroids, which hid the L4 vanish and biased L2 (9.2 R3, 8.3
  COMPOSITOR replaydiff).
- Running references only with non-AI bodies (L2), off-axis cameras only on the player side (L4: maxz = -2), a no-cast
  reference pass for shadow measurements (L9), sample poses held one extra frame before replay grabs (L11).
- L5 safety: the lab capture aborts (rtpc 0, stop) on any INSIDE session meter peak > 0 and, while suspended, on any active
  INSIDE stream; ForceLevel/ForceZero call Change twice; GetRTPCValue readback goes to result.json. All three are now design
  rules (6.0 G4, G5).

### 12.4 Runtime observations

- Restore: after the darkroom L9 run the game was unfrozen with the free camera 145 m away; the bounds cullers deactivated
  the ChickenGun collider groups and the boy fell to y = -38517 (god mode). Reloading the recipe fixed it. Replay End puts the
  game camera back before the culler re-hook and any unfreeze (2.4.8, R7).
- Frozen-frame drift: with the camera held, consecutive frozen frames are identical, but bright geometry edges drift by about
  100-2500 px over 30+ frames (2.4.7; R3/R4 noise floors).
- At timeScale 1 the Chicken AI moves and rotates the chick visual even with a kinematic root (5.3).
- The L4 off-axis verdict file says "game camera" because it ran with `cam=` set to the computed player-side 60 deg pose.
- Audio safety record (muted output): the headphones stayed silent. Two muters ran for the whole session (InsideMuter in
  run mode with heartbeat, and the older muter build as backup); every launch was followed by `audio force rtpc rtpc_master_volume 0` and
  `audio native on`; final heartbeat 62828 polls, 0 poll errors, unmutedSeenTotal 0, and every INSIDE session of the 5
  launches was already muted when found. S2, S3b and the G0 launch step were not run. The AudioRules ordering bug left Wwise
  at 100 for under a second in S3 run 1, while Wwise was suspended, the OS stream inactive and the session muted, until the
  undo's second ForceZero. At the end the game was closed, the muters were stopped after INSIDE.exe had
  exited, and `_mod/audio` was removed.
