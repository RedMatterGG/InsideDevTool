# Lab modules and the input determinism test

Optional parts of the release for measuring how the game behaves under recording and replay:

- `InsideDev.ReplayLab.dll`: the lab tests L1 to L12 (`lab ...` commands). Each test measures one engine or game behaviour
  that the scene recorder and the replay depend on, and writes a one-line verdict.
- `InsideDev.Det.dll`: the input replay determinism test (`det ...` commands): records the input of a ChickenGun run per
  logic step, feeds it back in later runs and logs the scene per step, so `tools/det_compare.py` can tell whether runs
  repeat.
- The lab audio test L5 also uses InsideMuter (`tools\muter\InsideMuter.exe`), an optional program that mutes INSIDE's
  sound in Windows while it runs: [AUDIO.md](AUDIO.md) section 11.

The scene recorder and the viewer are in [RECORD_REPLAY.md](RECORD_REPLAY.md), game audio capture and InsideMuter in
[AUDIO.md](AUDIO.md); MultiCam in
[MULTICAM.md](MULTICAM.md).

Contents

1. Install, switching on and off
2. ReplayLab (`lab`)
3. Input determinism test (`det`) and `tools/det_compare.py`
4. Pitfalls
5. Settings reference

---

## 1. Install, switching on and off

| File in the release | Copy to | Loaded | Off by default | Uninstall |
|---|---|---|---|---|
| `bin\InsideDev.ReplayLab.dll` | `<game>\_mod\` | at game start, or `ext load` (bridge) without a restart | yes: nothing runs until a `lab` command | delete the DLL with the game closed |
| `bin\InsideDev.Det.dll` | `<game>\_mod\` | at game start, or `ext load` | yes: nothing runs until a `det` command | delete the DLL with the game closed |

Both DLLs need `InsideDev.dll` of the same release in `<game>\_mod\`. `ext` (bridge) lists the loaded extensions:
`ReplayLab 0.2.1 (lab help)` and `Det (input replay determinism test, idle)`.

`lab` and `det` are extension commands: they work in the in-game console (the backtick key), through the file bridge
(`<game>\_mod\cmd\in_<x>.txt`) and through the MCP `command` tool. A user who never types `lab` or `det` sees no change;
the modules only tick an idle check every frame.

Building: `GAME=/path/to/INSIDE sh build/build_lab.sh` (both DLLs, needs `bin/InsideDev.dll` first).

---

## 2. ReplayLab (`lab`)

### 2.1 Commands

| Command | Effect |
|---|---|
| `lab` or `lab help` | Usage of every test, the common arguments and the result folder. |
| `lab list` | Every test with its title, usage and preparation. |
| `lab status` | `<test> running: phase '<phase>', frame <n>, <s> s` or `idle`, and the last verdict line. |
| `lab stop` / `lab abort` | Stops the running test; everything it changed is restored in the next frame. Verdict `INFO aborted: lab stop` unless it had one. |
| `lab verdicts` | One line per test: this session's verdict or the `verdict.txt` of `<root>\<test>\` ("(file)"), else `-`. L5 and L12 write into `L5_<stage>` and `L12_<tag>`; read those `verdict.txt` files directly. |
| `lab out [<dir>]` | Prints or sets the result root (session only). Default `<game>\_mod\replay_tests`. Use an absolute path. |
| `lab <test> [args]` | Runs a test (`lab l1`, `lab L7 frames=120`, ...). |

Only one test runs at a time. Refused while an mcam session runs, and without a gameplay camera ("load a level first").
A test runs as a coroutine over many frames; poll `lab status` or wait for `verdict.txt`.

### 2.2 Common arguments

| Argument | Default | Meaning |
|---|---|---|
| `cam=x,y,z,yaw,pitch[,fov[,roll]]` | the current view | Free camera pose held during the test (degrees). |
| `clean=on\|off` | `on` | Hide the editor overlay in grabbed frames (tests with images). |
| `thr=<n>` | per test | Pixel difference threshold (0-255). |
| `settle=<n>` | 12 | Frames rendered before a reference grab (TAA settles). |
| `timeout=<s>` | 600 (L5: secs + 120) | The test fails after this many real seconds. |

Arguments are `key=value` (case-insensitive keys) or single words (`force`, `confirm`, `wake`, a stage name). Object
arguments (`chick=`, `bale=`, `rope=`, `light=`, ...) take a name, a path or `#instanceID` like other bridge commands.

### 2.3 Results

Every run writes into `<root>\<test>\` (`<root>` = `lab out`, default `<game>\_mod\replay_tests`): `verdict.txt` (one line:
`PASS`, `FAIL` or `INFO`, then the summary, plus "(restore errors: n)"), `result.json`, `log.txt` and the test's images
(`.jpg`). Files of an earlier run of the same test move to `<root>\<test>\history\<yyyyMMdd_HHmmss>\` first.

- `PASS` / `FAIL`: the pass criterion below held or not.
- `INFO`: not run or not measurable (`INFO not run: <missing preparation>`, nothing visible, aborted). Prepare and rerun.
- `FAIL timeout after ...`, `FAIL exception in phase ...`, `FAIL watchdog: the test coroutine stopped advancing` (no
  progress for 600 frames and 10 s): technical failures.

`result.json` always has `test`, `id`, `verdict`, `summary`, `started`, `finished`, `frames`, `seconds`, `args` (with
`words`), `screen`, `timeScaleLeaks`, `hookErrors`, `aborted`, `restore` {`ok`, `errors`} and `grabs` [{`file`, `rect`,
`frame`, `note`}], plus the test's own values. Every test restores everything it changed (time scale, camera, transforms,
rigidbodies, renderers, cullers, TAA, light flags, ...) in reverse order when it ends, also on stop, failure and game quit.
Most tests freeze game time while they run (L2 and L10 let it run for short parts).

### 2.4 ChickenGun preparation

Most tests use the ChickenGun scene on the farm. Through the bridge:

```
load 7 0
state                                                     repeat until the "savepoint:" line shows (spawn 7 0)
tp 221.0 -221.4 0.66
call ChickenGunObject/ChickenGunVacuumLogic ChickenGunLogic OnElectricityOn
call _SaveLogic/ChickensLoaded ChickenSpawner DoSpawn
call ChickenGunObject/ChickenGunVacuumLogic ChickenGunLogic ToggleSuctionOn    L4 only (suction on, particles alive)
```

Loading a savepoint replaces the current game state; whether the game then writes its save file is unverified, so use a
copy of the game if your progress matters.

### 2.5 Tests

| Test | Proves | Scene |
|---|---|---|
| L1 | Transforms written at RuntimeCallbacks.lateUpdate (-400) stay until the camera culls (nothing overwrites them); reports the call order of the frame phases. | ChickenGun after DoSpawn |
| L2 | A pose written at each write point renders in the same frame; frozen engine motion blur equals running blur. | ChickenGun after DoSpawn |
| L3 | A pose written to a rigidbody at timeScale 0 persists (interpolation and kinematic combinations); legacy Animation re-samples while frozen. | ChickenGun after DoSpawn |
| L4 | A frozen particle system can be set back exactly (GetParticles, Pause, Clear, SetParticles, optional Simulate(0)), from the game camera and 60 deg off axis; cost and struct size. | ChickenGun, suction on |
| L5 | Wwise output capture: file, markers, silence, suspended capture speed, muter-only safety (stages S1, S3, S3b, S2). | any level, InsideMuter running |
| L6 | Reading UnityEngine.Random.seed does not consume a value; setting it back reproduces the stream. | any level |
| L7 | Forcing culler groups active has no gameplay side effects; the per-camera visibility emulation matches the game's culling without SetActive. | ChickenGun |
| L8 | How many RuntimeCallbacks instances exist and whether the per-frame guard keeps work to once per frame. | any level, game running |
| L9 | Static shadow caches follow a moved object (as is, static, static + dirty, dynamic); a hidden culler group casts no shadow. | ChickenGun after DoSpawn |
| L10 | Which time-animated shaders freeze at timeScale 0 and whether overriding `_Time` / `_RealTime` drives them. | a view with water, flies or drips |
| L11 | A rope can be replayed: RopeLineRenderer disabled, recorded vertices written, BlendRope.Draw by reflection. | ChickenGun (crane rope on screen) |
| L12 | Which transforms move when only the camera moves (camera-driven objects); no gameplay body among them. | any scene (tag it) |

Results measured during development (one session): L1, L2, L3, L4, L7, L8, L12 and L5 S1 PASS; L5 S3 FAIL (suspended capture
ran at 0.919x real time); L6 FAIL (the seed setter re-seeds, the stream cannot be restored); L9 INFO (no static-shadow light
over a mover in ChickenGun); L10 FAIL (`_Time` cannot be overridden); L11 FAIL by 1 px (max silhouette distance 2 px).

#### L1 write order

`lab l1 [frames=120] [chick=<obj>] [bale=<obj>] [wheel=<obj>] [cam=...]`

Freezes the game, then for `frames` frames (at least 10) writes offset poses to a chick root and its bones, the hay bale and
`MotorWheel` (default for `wheel`) in RuntimeCallbacks.lateUpdate and compares them at Camera.onPreCull, at a probe
component added last to the camera, and at OnPreRender. `chick` defaults to the chicken nearest the screen centre, `bale` to
the HayBale. PASS when no target changed between the write and onPreCull. result.json: `targets` (per object: written,
changed at each point, max deltas, candidate writers), `order` (frame phase trace, onPreCull against the first and last
camera component, coroutine phase, calls per frame), `cameraComponents`. No images.

#### L2 skin timing and frozen blur

`lab l2 [speed=2] [frames=12] [blurframes=24] [chick=<obj>] [bale=<obj>] [only=chick|bale] [chickat=x,y,z] [baleat=x,y,z]
[blurobj=bale|chick] [thr=12] [settle=12] [cam=...]`

Part A, frozen: moves each object along the screen's x axis at `speed` m/s (per 60 Hz frame) for `frames` steps, writing at
(a) lateUpdate -400, (b) an order-0 LateUpdate, (c) onPreCull, and compares each frame with a static reference render of the
written and of the previous pose. Part B: the `blurobj` moved the same way frozen and running (timeScale 1 for 2 x
`blurframes` frames, about 0.8 s of game time), with the game's motion blur and with blur 0; the streak length difference
is the engine blur. `chickat` / `baleat` place the object's visual centre first. The camera must show the objects with
free space to their right (about 0.4 m and 0.8 m of travel).

PASS when write point (a) matches the written pose in at least 90 % of the frames for every measurable object and the
frozen blur is within 10 % of the running blur. INFO when part A could not be measured, or part A passed and the blur
could not be measured. Images: `A_<obj>_plate.jpg`,
`A_<obj>_ref_k<n>.jpg`, `A_<obj>_<a|b|c>_k<n>.jpg`, `B_plate.jpg`, `B_<frozen_game|frozen_mb0|running_game|running_mb0>.jpg`,
`B_blurmask_frozen.jpg`, `B_blurmask_running.jpg`. result.json: `skinTiming`, `blur`, `blurRatioFrozenToRunning`,
`passSkin`, `passBlur`.

#### L3 rigidbody and animation at timeScale 0

`lab l3 [trials=6] [chick=<obj>] [bale=<obj>] [cam=...]`

Frozen. For the chick and the bale (each needs a Rigidbody), for interpolation Interpolate and None, kinematic off and on,
`trials` times: write an offset pose in lateUpdate and measure it at the same and the next frame's onPreCull. Then writes a
chick bone under its legacy Animation. PASS when, with interpolation None and kinematic on, the written pose persists to the
next onPreCull (under 1e-5 m and 1e-3 deg) for every body. result.json: `combinations`, `animatedBone`. No images.

#### L4 particles

`lab l4 [view=game|off|both] [root=ChickenGunObject/FX_PipeSuck] [systems=a,b] [off=60] [edge=0.8] [maxz=-2] [pthr=8]
[repeat=10] [settle=12] [cam=...]`

Systems: every live particle system under `root` and `ChickenGunObject/FX_StrawBlow`, or the objects in `systems`. Frozen.
Grabs the systems from the game camera and from a camera `off` degrees around them (`edge` 0..1.2 puts the systems toward
the frame edge; the off-axis camera stays at z <= `maxz`, the player side), with the particles shown and hidden (the
particle mask). Then Get, Pause, Clear, SetParticles back (plain), and again followed by Simulate(0, false, false), and
counts changed pixels inside the particle mask. `view` limits the cameras; `repeat` sets the cost loop; `pthr` the pixel
threshold.

PASS when `Marshal.SizeOf(Particle)` is 84, Get + Set costs under 0.3 ms per 1000 particles, and either variant reproduces
the frame (changed pixels inside the mask within the tolerance and PSNR at least 40 dB). INFO when fewer than 200 particle
pixels are visible. result.json `variantNeeded` names the variant to use. Images: `before_game.jpg`, `hidden_game.jpg`,
`before_offaxis.jpg`, `hidden_offaxis.jpg`, `after_plain_<game|offaxis>.jpg`, `after_simulate_<game|offaxis>.jpg`.

#### L5 Wwise output capture

`lab l5 s1|s3|s3b|s2 [secs=N] [level=100] [hb=<heartbeat.json>] [hbage=2] [force] [confirm] [wake]` (the stage can also be
given as `stage=`). Results in `L5_S1`, `L5_S3`, `L5_S3B`, `L5_S2`.

Preparation for every stage: InsideMuter running with a fresh heartbeat at `hb` (default `_mod/muter/heartbeat.json`
relative to the game folder; a given `hb=` stays the lab's path for the rest of the game session) and at least one muted
INSIDE session in it. `hbage` (default 2 s) is the heartbeat age limit, taken from the file's write time. A missing or stale
heartbeat or no muted session gives `INFO not run`; any unmuted INSIDE session gives `FAIL refused`.

Every stage forces `rtpc_master_volume` to 0 at the start and again at the end, so the game stays silent afterwards until
you give the volume back (section 4). The capture file is `<game>\_mod\audio\lab_<stage>_<HHmmss>.wav`, moved to the
result folder as `audio.wav`. A guard checks the heartbeat every 0.2 s during the capture and stops it at once (volume 0) on:
a stale or unreadable heartbeat, an unmuted INSIDE session, no muted session while Wwise is not suspended, any session
peak while suspended, an active INSIDE stream inside a suspended window, or a muted INSIDE session showing signal while its
device meter is missing or above 0.

| Stage | Default `secs` | What happens | PASS when | Writes |
|---|---|---|---|---|
| `s1` | 5 | Silent capture: volume stays 0, one marker `lab_s1` halfway. | The file grows linearly (deviation at most 5 %), has a valid format, contains the marker, peak below -90 dBFS, heartbeat clean. | nothing |
| `s3` | 60 | `AkSoundEngine.Suspend(true)` (stays suspended until the game exits), waits until the heartbeat shows no active INSIDE stream and peak 0 for 1 s (at most 15 s), then opens the window: volume forced to `level`. Markers `s0`, `s1`, ... every 5 s. | WAV duration within 0.5 % of the wall time, RMS above -50 dBFS, no active INSIDE stream and peak 0 during the capture, heartbeat clean. | `<game>\_mod\acap_suspend_ok.json` (stage `S3`) |
| `s3b` | 60 | As `s3` with `timeBeginPeriod(1)` during the capture. | As `s3`. | `<game>\_mod\acap_suspend_ok.json` (stage `S3B`, `requiresTimerPeriod` true) |
| `s2` | 10 | Muter-only capture: window at `level`, only InsideMuter keeps it silent. Runs only when `<root>\L5_S3\verdict.txt` is a FAIL (or with `force`), only with `confirm`, and, if S3/S3b suspended Wwise in this session, only with `wake` (volume 0, 100 ms, WakeupFromSuspend). | RMS above -50 dBFS, peak below -0.1 dBFS, heartbeat clean, and at least one heartbeat read showed a muted INSIDE session with signal while its device meter read 0 (the device mix proven silent). | `<game>\_mod\acap_muteronly_ok.json` |

The flag files are written into `<game>\_mod\` whatever `lab out` says, and only on PASS. The core's muted capture output
uses them ([AUDIO.md](AUDIO.md) section 8). Delete a flag to withdraw it.

Consent: S3, S3b and S2 raise the master volume while the headphones are protected only by Suspend or by the Windows mute.
Run them only when the person wearing the headphones has agreed and has them on; S2 additionally needs the word `confirm`,
which an agent never adds on its own. S2 cannot pass while any other application plays sound on the same output device:
the device meter then shows signal and the guard stops the capture. It also needs the game to make sound during the
capture (a scene with ambience), otherwise the device was never checked and the stage fails.

result.json: `stage`, `heartbeatPath`, `heartbeatStart`, `suspend`, `quietAfterS`, `timeBeginPeriod`, `bankPath`,
`captureArg`, `expectedFile`, `startReturned`, `appearedAfterS`, `wallS`, `markers`, `heartbeatDuring`, `mutedSignalReads`,
`maxEndpointPeakWithSignal`, `emergency`, `rtpcReadback` (Wwise value at start, +0.5 s, before and after the close),
`moved`, `wav` (format, duration, peak and RMS dBFS, cue points, labels), `growthBytesPerS`, `growthMaxDeviation`,
`durationError` (S3), `s3Verdict` and `wakeup` (S2), `suspendFlagWritten` / `muterOnlyFlagWritten`.

#### L6 RNG fingerprint

`lab l6`. Reads `UnityEngine.Random.seed` several times, draws, sets the read value back and draws again (leaves the seed
at the first read value). PASS when reading does not consume and the two draws are equal. result.json: the seeds and draws,
`readConsumes`, `drawsEqualAfterSetBack`, `seedChangesOnDraw` (true = usable as a read-only fingerprint), `restored`.

#### L7 forced cullers and visibility emulation

`lab l7 [cullers=3_Interior_CULL,1b_IndoorDetails_CULL] [frames=240] [period=120] [dist=4] [in=x,y,z,yaw,pitch[,fov]]
[out=x,y,z,yaw,pitch[,fov]] [zone=<root prefix>|all]`

Frozen. Phase 1: forces the `cullers` groups active (`valid = false`) with the camera outside them (`out`) and counts sound
posts, SignalOut fires and FSM changes from those groups. Phase 2: unhooks the game's culler update, emulates it every frame
from onPreCull for all cullers of the zone (`zone`: root name prefix, default that of the first culler; `all` = every
culler) while the camera moves between `out` and `in` over `period` frames, hiding renderers and lights instead of
SetActive. Phase 3: the same camera path with the live game culling as the reference. Without `in`/`out` the camera is
placed `dist` metres either side of the first culler's facing plane.

PASS when the forced groups are active and rendered, they caused 0 sound posts and 0 SignalOut fires, the emulation made 0
SetActive calls and its visibility matched the live culling in at least 99 % of the comparisons. Images: `forced_out.jpg`,
`emulated_out.jpg`, `emulated_in.jpg`. result.json: `forced`, `matchRate`, `mismatches`, `emulationSetActiveCalls`, ...

#### L8 RuntimeCallbacks instances

`lab l8 [frames=120]`. Game running (timeScale 1). Counts RuntimeCallbacks instances and the calls of update, lateUpdate,
fixedUpdate and ExecuteInEditModeIncluded.update over `frames` frames (at least 10). PASS when a once-per-frame guard
reduces each of update, lateUpdate and ExecuteInEditModeIncluded.update to one call per frame and they fired in at least
`frames` - 2 frames. result.json: `instances`, `counts` (per event, and fixedUpdate per logic step).

#### L9 static shadows

`lab l9 [light=SpotLight_Machine_BoundsCuller] [bale=<obj>] [group=<culler name>|none] [at=x,y,z] [frames=12] [step=0.05]
[radius=2.5] [thr=10] [lightradius=25] [cam=...]`

Frozen. Moves the bale under `light` (to the floor below the light, or `at`) in `frames` steps of `step` m, once with the
bale casting no shadow (reference) and once per mode: `asis` (the light's own useStaticShadows), `static`, `dirty` (static,
marked dirty every frame), `dynamic`. The shadow is the area darker than the reference; its centroid must follow the dynamic mode's. Also
hides a culler group under the light by its renderers (`group`, default the nearest group within the light's range) and
checks its shadow disappears. `radius` sizes the floor area watched; `lightradius` the list of shadow lights reported.

PASS when in mode `asis` the shadow follows within 2 px in every frame and, when measurable, the hidden group casts no
shadow. INFO when no bale shadow is measurable. Images: `plate_dynamic.jpg`, `reference_nocast.jpg`, `shadow_<mode>.jpg`,
`shadowmask_<mode>.jpg`, `group_hidden_asis.jpg`. result.json: `modes`, `follow`, `shadowLightsNear`, `lightOriginal`.

#### L10 shader time

`lab l10 [objs=a,b] [words=water,fly,drip] [hold=3] [live=0.5] [pthr=8] [settle=12] [cam=...]`

Renderers measured: those of `objs`, or up to 16 visible renderers whose name or material contains a word of `words`
(default water, fly, flies, drip, dust, rain). Grabs them running for `live` s (0 skips), frozen at 0, `hold`/2 and `hold`
seconds, then with `_Time` and with `_RealTime` set in every OnPreCull and OnPreRender for T = 0 and T = `hold`.

PASS when every renderer that animates while frozen is driven by one of the overrides and every renderer that animates
only while running is driven by one of them. INFO when nothing time-driven is in view. Images: `live_0.jpg`, `live_1.jpg`,
`frozen_0s.jpg`, `frozen_half.jpg`, `frozen_end.jpg`, `override_<time|realtime>_T<t>.jpg`. result.json `masks` per renderer.

#### L11 rope replay

`lab l11 [rope=RopeCrane_BoundsCuller/CraneRopeLong] [samples=12] [slow=5] [kick=0] [hold=1] [margin=2] [thr=12] [settle=12]
[cam=...]`

Records `samples` rope vertex sets (running 2 frames between them; `kick` gives the rope's last body an impulse first, keep
it small, about 0.02), then replays them with the RopeLineRenderer disabled, writing the vertices and calling Draw by
reflection, at `slow` output frames per sample and `hold` extra frames before each grab, and compares the silhouettes.
`margin` (m) widens the watched area.

PASS when the rope is visible, Draw ran every frame without exceptions and the silhouette distance is at most 1 px at every
sample. INFO when the rope is not visible or barely moving. Images: `record_s0.jpg`, `record_s<n>.jpg`, `replay_s0.jpg`,
`replay_s<n>.jpg`, `mask_record_s<n>.jpg`, `mask_replay_s<n>.jpg`. result.json: `silhouetteDistancePx`, `draws`,
`exceptions`, `amplitudeM`.

#### L12 camera probe

`lab l12 [tag=<scene name>] [frames=30] [dx=1.5] [yaw=10] [eps=1e-5] [cam=...]`. Result folder `L12_<tag>` (or `L12`).

Frozen. Scans every transform of the scene: `frames` frames with the camera still (control), then `frames` frames
alternating with a pose `dx` m to the right and `yaw` degrees turned. Transforms that changed only while the camera moved
are camera-driven (position or scale change above `eps` m, rotation above 0.001 deg). PASS when every active, rendered
WaterDust dust object and LensFlareSimple is listed and no listed transform has a Rigidbody or Collider (outside the
expected list). Writes `camera_driven.tsv` (path, components, expected, rigid) into the result folder; recordings do not
read it there. To feed recordings, run `srec probe camera` instead (it merges into `<game>\_mod\camera_driven.tsv`).
result.json: `cameraDrivenList`, `missingExpected`, `rigidListed`, `changedWithoutCameraMotion`.

### 2.6 Run order

1. Any level: `lab l6`, `lab l8` (game running).
2. ChickenGun preparation (2.4), without suction: `lab l1`, `lab l3`, `lab l2 cam=...` (a view of a chick and the bale with
   room to their right), `lab l7`, `lab l12 tag=chickengun`, `lab l9 cam=...` (the floor under the machine light), `lab l11
   kick=0.02 cam=...` (the crane rope on screen).
3. Suction on: `lab l4`.
4. A view with water, flies or drips: `lab l10`; a water scene: `lab l12 tag=water`.
5. Sound, InsideMuter running: `lab l5 s1`. Only with the consent of the person wearing the headphones: `lab l5 s3` (or
   `s3b`), and only if S3 failed: `lab l5 s2 confirm` (plus `wake` after S3/S3b in the same session). Then give the volume
   back or restart the game (section 4).

Check `verdict.txt` and the images after every test; `INFO not run` names what to prepare.

---

## 3. Input determinism test (`det`) and `tools/det_compare.py`

The test answers whether a ChickenGun run repeats exactly when the same input is fed again, from the same and from other
camera poses. Every run loads savepoint 7 0 (ChickenGun), resets the random generators, records or feeds the input of every
logic step through its own input layer (FilmInput), fires step-indexed commands (cues) and logs boy, hay bale, vacuum
machine and all chickens per step. It never uses the game's own Playback system.

### 3.1 Commands

| Command | Effect |
|---|---|
| `det` or `det status` | State (`idle`, `loading`, `running`, `stopping`, `finishing`, `done`, `aborted`), the run, step k / kmax, rows, events, cues fired, bale fall and landing steps, shots, hits, kills, the live comparison with the source (replays), warnings, the prep and the cue track size. |
| `det help` | Usage. |
| `det prep [anim=default\|always] [cull=live\|freeze] [stream=live\|keep] [drip=on\|off]` / `det prep full` / `det prep default` | Prep switches (3.2). Without arguments prints them. Refused while a run is busy. |
| `det cue <k> <command>` | Adds a cue: run `<command>` (any bridge or console command) at logic step k (k >= 0, counted from the moment the load has finished, "all scenes loaded"). Kept sorted by k. |
| `det cues` / `det cues clear` | Lists / clears the cue track. |
| `det run <name> <out> [kmax] [kmax=n] [fps=60] [cam=...]` | A record run (3.4). |
| `det replay <source> <out> [kmax=n] [fps=60] [cam=...]` | A replay run of `<source>`'s inputs and cues (3.4). |
| `det zone` | The subscenes of load 7 0 used by the prep, whether they are loaded and kept, and their culler groups, Animations and drip sounds. |
| `det abort` / `det stop` | Ends the running run now (state `aborted`) and restores everything. |

### 3.2 Prep switches

Read at load time, the same in record and replay; kept for the rest of the game session (not saved).

| Switch | Default | Other value |
|---|---|---|
| `anim` | `default` | `always`: every Animation in the zone gets culling type AlwaysAnimate after each load (restored at the end; inactive ones keep it until the next load). |
| `cull` | `live` | `freeze`: the game's culler update is unhooked and every culler group of the zone is switched active (re-hooked at the end). |
| `stream` | `live` | `keep`: every subscene of the zone is pinned loaded during the load (released at the end). |
| `drip` | `on` | `off`: the ParticleSound components on `FX_WaterDripDown2` are disabled. |

`det prep full` = `anim=always cull=freeze stream=keep drip=off`; `det prep default` = the defaults. The zone is every
subscene whose bounds meet the area of load 7 (plus 10 m).

### 3.3 Cue track

The standard ChickenGun track (the same actions as the lab preparation, at fixed steps after the load):

```
det cues clear
det cue 180 tp 221.0 -221.4 0.66
det cue 300 call ChickenGunObject/ChickenGunVacuumLogic ChickenGunLogic OnElectricityOn
det cue 300 call _SaveLogic/ChickensLoaded ChickenSpawner DoSpawn
det cue 480 call ChickenGunObject/ChickenGunVacuumLogic ChickenGunLogic ToggleSuctionOn
```

Cues fire in the fixed-update phase of their step, at most once per step. A record run copies the track into its
`cues.tsv`; a replay takes the cues of its source run, not the current track. Each fired cue and its reply go to
`events.csv` (kind `cue`).

### 3.4 `det run` and `det replay`

| Argument | Default | Meaning |
|---|---|---|
| `<name>` (run) | | A label stored in meta.json. |
| `<out>` | | Run folder: absolute, or relative to `<game>\_mod\det\`. An existing run there is overwritten. |
| `<source>` (replay) | | A folder path, or the name of a run folder next to `<out>`. Must hold `inputs.csv` and `cues.tsv`; must differ from `<out>`. |
| `kmax=n` (or a bare number for `run`) | run: 1200; replay: the source's last step | Last step. A record run also stops 210 steps after the bale landed; a replay runs exactly kmax steps. |
| `fps=n` | 60 | Rendered frames per second of game time: a multiple of 60 from 60 to 600 (120 = 2 frames per logic step). |
| `cam=x,y,z,yaw,pitch,fov` or `cam=x y z yaw pitch fov` | game camera | Free camera at that pose from the load on. |
| `cam=<run>` | | Free camera following that run's `rig.csv` frame by frame (a folder path or a run next to `<out>`). |

A run: refuses while busy, while MultiCam runs or before a game is started ("savepoints not ready"); locks the frame pacing
(vSync 0, targetFrameRate and captureFramerate = fps, fixedDeltaTime 1/60, run in background on), ignores free-camera keys,
installs FilmInput (neutral input during the load, then: record = the input reaching the game, replay = the source's
input; neutral beyond the source's end, counted as "input missing"), loads savepoint 7 0, resets the random generators
(RandomGenerator.ResetAll, PDRandom.seed 0, UnityEngine.Random.seed 0) at the load start and when all scenes are loaded,
sets k = 0 there, applies the prep and the camera, logs one row per logic step, fires the cues and stops at the stop rule.
It aborts on an unexpected load during the run, no logic step for 30 s (game paused, minimized or frozen), a load that does
not start within 20 s or finish within 300 s, or a hook failure. At the end everything is restored and `inputs.csv` and
`meta.json` are written. Reply: `det record|replay <name> done|aborted: <reason>; k ..., rows ..., baleFall ..., baleLand
..., shots ..., kills ... -> <out>`.

A record run takes the live input of keyboard or gamepad; with no one touching them the input is neutral and the cues do
the work. During a replay, `det status` compares every finished row with the source: first differing step k* and per
channel (rng_fixed, rng_update, rng_late, positions above 1e-4 m), and the largest boy, bale and chicken differences.

### 3.5 Run folder files

| File | Content |
|---|---|
| `steps.csv` | One row per logic step k: `k, step, frame, rng_fixed, in_x, in_y, in_jump, in_action, boy_x, boy_y, boy_z, boy_vx, boy_vy, boy_vz, boy_state, bale_x, bale_y, bale_z, bale_qx, bale_qy, bale_qz, bale_qw, bale_vx, bale_vy, bale_vz, bale_wx, bale_wy, bale_wz, bale_sleep, bale_kin, cg_state, cg_sucking, cg_vac, cg_blow, cg_shoot_timer, cg_kill_countdown`, then per chicken `cNN_x, cNN_y, cNN_z, cNN_vx, cNN_vy, cNN_vz, cNN_kin, cNN_started, cNN_dead, cNN_tx, cNN_ty, cNN_tz`, then `cull_active, cull_bits, subs_loaded, subs_active, fixed_n, rng_update, rng_late`. `rng_*` = UnityEngine.Random.seed at the fixed-update hook, the update hook and the late-update hook (reading it does not consume). Numbers with 9 significant digits, booleans 1/0. |
| `events.csv` | `k,kind,name,detail`. Kinds: `start` (k -1), `prepareLoad`, `allScenesLoaded`, `load` (unexpected load), `activate` / `deactivate` (subscenes), `cue`, `baleFall` (the bale trigger fired), `probeEnter`, `baleHit` (bale collision with relative speed squared above 50), `baleLand` (after the fall, the bale's velocity dropped by at least 1 m/s), `shot` (a chicken left the gun: kinematic off), `kill`, `end`, `abort`. |
| `rig.csv` | `f,frame,k,x,y,z,qx,qy,qz,qw,fov,free`: Camera.main once per frame from the load on (`free` 1 = the free camera). |
| `chicks.tsv`, `culls.tsv` | `i, path`: the chicken order of the `cNN_` columns and the culler group order of `cull_bits`. |
| `inputs.csv` | `k,x,y,jump,action`: the input that reached the game per step (what a replay feeds). |
| `cues.tsv` | `k<TAB>command`. |
| `meta.json` | `tool`, `version`, `name`, `mode` (record or replay), `source`, `created`, `state`, `end` (stop reason), `load` [7, 0], `prep`, `prepText`, `prepCounts`, `fps`, `kmax`, `afterLand` (210, or -1 for replays), the pacing before the run, `k0`, `frame0`, `kEnd`, `rows`, `rigRows`, `kBaleFall`, `kBaleLand`, `shots`, `hits`, `kills`, `killed`, `chicks`, `groups`, `cam`, `cues`, `cuesFired`, `zone`, `live` (replay comparison), `warnings`. |

Warnings in meta.json: boy, vacuum machine, hay bale or bale trigger not found (no baleFall, so no landing stop), a chicken
count other than 24, replay steps beyond the source.

### 3.6 The standard run set

`tools/det_compare.py matrix` expects run folders with these names under one root (for example `<game>\_mod\det\`). One
way to make them, after the cue track of 3.3 (the camera poses are those of the design notes):

| Run | Commands | Compared with |
|---|---|---|
| S1 | `det prep default`, `det run S1 S1` | reference |
| S2, S3 | `det replay S1 S2`, `det replay S1 S3` | S1 |
| S4, S5 | `det prep full`, `det replay S1 S4`, `det replay S1 S5` | S1 (information only), S5 against S4 |
| P1 | `det prep default`, `det replay S1 P1 cam=S1` (free camera on S1's game camera path) | S1 |
| C1 | `det replay S1 C1 cam=225,-219,-6,20,-18,45` | S1 |
| C2 | `det replay S1 C2 cam=<a wide pose of your choice>` | S1 |
| C3, C4 | as C1, C2 after `det prep full` | S4 |
| F1 | `det prep full`, `det replay S1 F1 fps=120` | S4 |
| V1 | the same take under MultiCam's step loop (not made by `det`) | S4 |

Missing runs are listed as problems; the decision uses what exists.

### 3.7 `tools/det_compare.py`

Python 3.9 or newer, standard library only. Run folders are compared by step k, never by time.

| Command | Options | Output | Exit codes |
|---|---|---|---|
| `compare <run> <ref>` | `--json <file>` | Text report of one run against a reference; JSON with every value. | 0 (any verdict), 2 (a folder has no `steps.csv`) |
| `matrix <root>` | `--out <file>` (default `<root>\verdict.txt`), `--json <file>` (default `<root>\det_matrix.json`) | Compares the standard runs (3.6), applies the decision rule, prints and writes the verdict. | 0, 2 (not a folder) |
| `selftest` | | Synthetic runs, no game. | 0 ok, 1 failed |

The compare exit code does not carry the verdict: read the first report line.

Window: every reference step up to the reference's bale landing + 210 steps (all reference rows if it has no landing);
chickens for the film verdict only up to landing + 60 steps (1 s).

| Verdict | Rule |
|---|---|
| `PASS strict` | Every step of the window present; rng_fixed, rng_update and rng_late equal at every step; every position (boy, bale, every chicken) within 1e-4 m; identical steps of baleFall, baleLand, shot, baleHit and kill. Runs can be intercut. |
| `PASS film` | Window covered; boy within 5 mm, bale within 10 mm and 0.5 deg, every chicken within 20 mm until landing + 1 s; same first baleFall and baleLand steps and the same shot count. |
| `FAIL` | Otherwise; the reasons are listed. |

The report also gives k* (first step with any difference), the first difference of inputs, loaded subscene counts,
culler group activity and fixed steps per frame, event steps of both runs, shots, killed chicken and a culprit: which
part of the frame first changed the random stream, or "positions diverge with identical random streams" (physics or
float order). `matrix` adds the decision: whether input replay works for the game camera and for free cameras, with
default or full prep, whether 120 fps reproduces 60 fps, and which pairs are strict.

---

## 4. Pitfalls

- **Silent game after the lab audio test.** Every `lab l5` stage leaves the `rtpc:rtpc_master_volume` rule at 0. Give the
  volume back with `audio force rtpc rtpc_master_volume <game volume 0-100>` and then `audio clear
  rtpc:rtpc_master_volume`, or restart the game. After S3/S3b Wwise also stays suspended until the game exits: only `lab l5
  s2 ... wake` wakes it; `acap suspend off` does not ("not suspended by acap").
- **Silent game after InsideMuter.** Its mute stays on the session after it exits ([AUDIO.md](AUDIO.md) section 11).
- **Muter waits forever** when started and no INSIDE.exe ever appears; it mutes every INSIDE.exe, also a second install.
- **Minimized or unfocused game.** A minimized game window pauses the game: lab tests stall (watchdog FAIL after 600 frames
  and 10 s, or timeout) and `det` runs abort after 30 s without a logic step. `bg on` (and the run-in-background setting
  `det` switches on for its runs) keeps the game running without focus; whether it also keeps a minimized game running is
  unverified in game.
- **Load races.** Prepare the scene only after the load finished (`state` shows the savepoint). `det` loads by itself and
  waits for the game's "all scenes loaded".
- **MCP idle restore.** Through MCP, `mcp.idle_restore_s` (default 600 s without a tool call) restores the game: it cancels
  the running action, releases input and sets the clock to real time at speed 1. Lab tests and `det` runs are not stopped
  by it, but a test that needs the game frozen or a prepared state can then see the game running. Keep calling tools.
- **Step mode.** The MCP play tools freeze the game between calls in step mode. Lab tests set their own time scale; `det`
  locks its own pacing. Do not drive the game through MCP step tools during a `det` run (FilmInput replaces the input
  layer; mixing with the MCP agent input is unverified in game).
- **`lab verdicts`** does not find L5 and L12 results (they use `L5_<stage>` and `L12_<tag>` folders).
- **`det_compare.py compare` exits 0 for FAIL.** Use the report text or `--json`.
- **Game progress.** `det` and the ChickenGun preparation load savepoint 7 0; whether the game writes its save file then is
  unverified.

---

## 5. Settings reference

| Setting | Default | Where | Effect |
|---|---|---|---|
| Lab result root | `<game>\_mod\replay_tests` | `lab out <dir>` (session only) | Where tests write. |
| Lab heartbeat path | `_mod/muter/heartbeat.json` (game folder) | `hb=` on `lab l5` (stays for the session) | Heartbeat L5 reads. |
| Lab heartbeat age | 2 s | `hbage=` on `lab l5` | Age limit (file write time). |
| Lab common | `clean=on`, `settle=12`, `timeout=600` | test arguments | 2.2. |
| Suspend flag | absent | `<game>\_mod\acap_suspend_ok.json` (L5 S3/S3b PASS) | Unlocks the core's Suspend capture path. |
| Muter-only flag | absent | `<game>\_mod\acap_muteronly_ok.json` (L5 S2 PASS) | Unlocks the core's muter-only path. |
| Det run folder | relative to `<game>\_mod\det\` | `<out>` argument | Where runs write. |
| Det prep | `anim=default cull=live stream=live drip=on` | `det prep` (session only) | 3.2. |
| Det cue track | empty | `det cue`, `det cues clear` (session only) | 3.3. |
| Det kmax, fps | 1200, 60 | `kmax=`, `fps=` | 3.4. |
| Det stop after landing | 210 steps (record runs) | fixed | 3.4. |
