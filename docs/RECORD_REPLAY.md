# Scene recorder, recording viewer and camera helpers

The scene recorder writes what moves in a scene (transforms, bones, particles, lights, materials, the game camera and
timed events) into a take folder with `scene.srec`. The recording viewer drives the frozen scene from such a recording while
you fly the free camera, so you can scrub a take and look at it from any side. This document also covers the camera and
session helpers (`cam`, `hud`, `clean`, `gamma`, `seed`) and the offline tool `tools/srec_tool.py`.

Not in this document:
- MultiCam (`mcam ...`), filming every camera from a recording (replay passes), spec files: [MULTICAM.md](MULTICAM.md),
  section "Replay mode".
- Game audio capture (`acap ...`, `audio=on` on a recording), InsideMuter and `tools/mcam_audio.py`: [AUDIO.md](AUDIO.md).
- Lab tests (`lab ...`) and the input determinism test (`det ...`): [LAB.md](LAB.md).

Contents

1. Install and what is off by default
2. Where the commands run
3. Workflows
4. `srec` command reference
5. Take folder files and formats
6. Clocks and frame pacing
7. Camera and session helpers
8. Offline tool
9. Pitfalls
10. Settings reference

---

## 1. Install and what is off by default

Everything in this document is part of `bin\InsideDev.dll`, installed to `<game>\_mod\` (see README.md). There is nothing
extra to install.

Nothing runs until you call a command. A user who never calls `srec`, `cam`, `hud`, `clean`, `gamma` or `seed` sees no change
in the game: no recording, no camera change.

| Feature | Starts with | Stops with | Saved between game sessions |
|---|---|---|---|
| Scene recorder | `srec record start <dir>` | `srec record stop [keep]`, game quit | nothing (only the take folder) |
| Recording viewer | `srec load <dir>` | `srec end`, game quit | nothing |
| Write-order probe | `srec probe [n]` (viewer loaded) | runs n frames, or `srec probe stop` | `write_probe.tsv` in the recording folder |
| Camera probe | `srec probe camera [n]` | runs about 2n + 5 frames, or `srec probe stop` | `<game>\_mod\camera_driven.tsv` (read by every later recording) |
| Free camera | `cam pose ...`, `cam fov`, `cam roll`, Insert key | `cam off`, Insert key | no |
| HUD, clean frames, gamma | `hud`, `clean`, `gamma` | `hud on`, `clean off`, `gamma reset` | no |
| Random seed | `seed <n>` | cannot be undone (the random stream cannot be restored) | no |

---

## 2. Where the commands run

`srec`, `cam`, `hud`, `clean`, `gamma` and `seed` are bridge commands:

- File bridge: write `<game>\_mod\cmd\in_<anything>.txt` with one command per line (optionally `<id>|<command>`, id at
  most 11 characters); the reply is appended to `<game>\_mod\cmd\out.txt` between `>>> <id>` and `<<< <id>`.
- MCP: the `command` tool of `InsideDev.Mcp.dll` runs any bridge command and returns its reply.
- The in-game console (the backtick key) does not run them: it answers "unknown command".

Tokens are separated by spaces. Double quotes group words (`"C:\path with spaces\take1"`) and are removed; a value can
therefore never contain a double quote, so keys that take a JSON object (`box`, `activateList`) cannot be given on the
command line (use a spec file with `spec=` for `box`). Give folders as absolute paths: a relative folder is resolved
against the game process's working folder (which folder that is when the game starts from Steam is unverified in game).

---

## 3. Workflows

### 3.1 Record a take, scrub it in the viewer, probe, end

```
load 7 0
state                                         repeat until the "savepoint:" line shows the savepoint you loaded
srec record start C:\path\to\takes\take1      the recording starts in this frame
                                              ... play the scene ...
srec event landing                            optional: mark a moment (use it later as @landing)
srec record status                            t, fps, drift, samples, objects, MB
srec record stop keep                         ends the recording, keeps the record state for the viewer
srec load C:\path\to\takes\take1 t=@landing-1 viewer: scene frozen at T = landing - 1 s, free camera at the recorded game camera
srec play 0.25                                plays at quarter speed (real time x 0.25)
srec pause
srec seek 2.5                                 jump to T = 2.5 s
srec step next                                one recorded sample forward (back, or a time in seconds)
srec objects HayBale                          which recorded objects match, resolved or not
srec probe 60                                 write-order probe over 60 frames
srec probe result                             the full list of transforms the replay could not hold
srec end                                      releases the viewer; the scene stays at the recording's last sample
```

`srec record stop` without `keep` is also fine before `srec load`; `keep` only avoids streaming and culling changes between
the end of the record and the viewer (4.4). Use the Insert key, WASD and the right mouse button to fly while the viewer is
loaded (section 7).

### 3.2 A take driven through MCP

When an agent plays the scene through MCP tools, the recording runs while the agent acts:

```
srec record start C:\path\to\takes\take3 spec=C:\path\to\specs\take.json keep=<subscene>,<subscene> clock=fixed:60
                                              waits until the kept subscenes are loaded and active, then 12 more frames
srec record status                            "waiting to record ..." until it starts
                                              ... the agent plays; it marks moments with srec event <name> ...
srec record stop
```

- `spec=` takes `replay.record` of that spec as the defaults and arms its `events`, so `@event` times exist in the
  recording. Keys given on the command line win.
- `clock=fixed:60` gives one sample per logic step when the agent drives the game in step mode (section 6).
- Keep calling tools during the take: the MCP server restores the game after `mcp.idle_restore_s` (default 600 s) without a
  tool call (section 9).
- Film it from many cameras afterwards with `mcam replay` and `replay.source` set to the take folder, in the same game
  session: [MULTICAM.md](MULTICAM.md), "Replay mode".

### 3.3 Check a recording offline

```
python -I tools/srec_tool.py verify C:\path\to\takes\take1        exit 0 = intact
python -I tools/srec_tool.py info   C:\path\to\takes\take1
python -I tools/srec_tool.py report C:\path\to\takes\take1 --check "OfficeDudeE @2..5"
```

---

## 4. `srec` command reference

`srec` alone is `srec status`; `srec help` prints the usage.

| Command | Short description |
|---|---|
| `srec status` | Viewer, recording, pending handover, probe and hook state. "srec idle (srec help)" when nothing is active. |
| `srec objects [filter] [max=n]` | Object table of the loaded recording. |
| `srec record start <dir> [key=value ...]` | Start a manual recording into `<dir>`. |
| `srec record stop [keep]` | Stop it (or cancel the wait for kept subscenes). |
| `srec record status` | State of the manual recording, or "not recording". |
| `srec event <name> [detail ...]` | Store a timed event in the active recording. |
| `srec load <dir> [t=...] [cam=game\|keep] [key=value ...]` | Load a recording into the viewer and freeze the scene. |
| `srec play [speed] [loop\|once]` / `srec pause` | Play the viewer / stop playing. |
| `srec seek <time>` / `srec step [next\|back\|<dt>]` | Move the viewer's replay time T. |
| `srec end` | Close the viewer (or release a pending record handover). |
| `srec probe [n] [eps=m] [rot=deg] [out=dir]` | Write-order probe (viewer loaded). |
| `srec probe camera [n] [dx=m] [yaw=deg] [eps=m] [rot=deg] [out=dir] [record key=value ...]` | Camera probe. |
| `srec probe stop` / `srec probe result` | Stop a running probe / print the last full probe result. |

### 4.1 `srec status`

Prints, when present: `viewer <dir>  T <t> / <duration> s  sample <i>/<n>  paused|playing x<speed> [loop]` and a line with
the replayer state (resolved and unresolved objects, conflicts, activations); `recording <dir> ...` with game time, fps of
the last 60 frames, clock drift in ms, pacing changes, audio state and spec events fired; `handover pending from <dir>`;
the running probe and the last probe summary; the last deferred record start (a start that waited for kept subscenes);
the hook line (`hooks: lateFrame ...`). During an mcam replay or record it shows `replay (mcam)` or `recording (mcam)`.

### 4.2 `srec objects [filter] [max=n]`

Needs a loaded viewer or a running mcam replay. Prints a tab-separated table, at most `max` rows (default 200), then
"... n more":

| Column | Meaning |
|---|---|
| id | object id in the recording |
| kind | `xf` (transform), `bone` (boy bone), `cloth` (huddle cloth bone), `particles`, `light`, `material`, `probe`, `spawned`, `rope`, `renderer` |
| keys | recorded keys of this object |
| resolved | `yes`, `ambiguous` (several live objects match the path) or `no` |
| how | how it was found: `session` (the live object of this game session), a path match, `owner::<Type>[i]` or `owner+<n>` for components |
| conflicts | frames where something else changed it against the recording |
| flags | `L` local (keyed in parent space), `C` camera-driven culler or content root, `X` camera-driven transform (never written), `S` spawned, `V` scale changes, `E` written every frame, `O` written once, `A` under an Animation, Animator or Actor |
| path | scene path |

`filter` matches a kind name exactly or any part of the path (case-insensitive); several words are joined with spaces.

### 4.3 `srec record start <dir> [key=value ...]`

Starts a manual recording into `<dir>` (created if missing). The files go directly into `<dir>` (section 5).

Own keys:

| Key | Default | Values | Meaning |
|---|---|---|---|
| `clock` | `real` | `real`, `fixed:60`, `fixed:120`, `fixed:240` | Sample clock (section 6). |
| `audio` | `off` | `on`, `off` (also `true/false`, `1/0`, `yes/no`) | Capture the game sound into the take ([AUDIO.md](AUDIO.md)). Refused when the audio preflight fails. Needs `clock` `real` or `fixed:60`. |
| `overwrite` | `off` | `on`, `off` | Allow a `<dir>` that already holds `scene.srec`. Without it the start is refused. |
| `spec` | none | path to a MultiCam spec | Use its `replay.record` block as the defaults and arm its `events` (polled every frame; the first fire of each name is stored as an event). A spec with errors refuses the start. |

Record keys (any field of the spec's `replay.record` block; command-line values win over `spec=`). Lists are comma
separated; booleans take `on/off`, `true/false`, `1/0`, `yes/no`.

| Key | Default | Meaning |
|---|---|---|
| `roots` | `auto` | What to record. `auto`: every active subscene of the current savepoint's zone (or, when none is active, of the zone around the boy), the boy and the huddle (with `characters`) and the spawn containers (with `spawned`). Also `boy`, `huddle`, `subscene:<name>`, `zone:<zone>` and object paths or names (every match is recorded). Subscenes named by `subscene:`/`zone:` that load later, and `auto` subscenes that activate later, join the recording in that frame. |
| `keep` | none | Subscene names (as `#name` or the scene name). They are pinned loaded; the start waits up to 30 s until all are loaded and active, then `settleFrames` more frames. After 30 s it starts anyway (warning) and late ones join when they load. |
| `settleFrames` | 12 | Frames to wait after the kept subscenes are ready, so late-awake content is on at sample 0. Used only with `keep`. |
| `exclude` | none | Object paths left out. |
| `box` | none | `{"center":[x,y,z],"size":[x,y,z]}`: limits `auto` subscenes to this box. Spec file only (section 2). |
| `characters` | `true` | Boy bones, huddle cloth, arm and leg bones. |
| `spawned` | `true` | Watch `_Dynamics`, `_SubsceneDynamics` and new children of recorded transforms. |
| `keepSpawned` | `false` | Not built (warning). |
| `particles` | `all` | `all`, `none`, `list` (only `particleSystems`). |
| `particleSystems` | none | Paths for `particles=list`. |
| `lights`, `materials`, `probes`, `fog` | `true` | Record these channels (light values, MaterialInstance values, ColorBlendProbe positions, fog values). |
| `post` | `false` | Not read by the recorder in this build. |
| `shaderState` | `true` | Shader globals, shared-material values and renderer property blocks of the known writers (huddle shading and bruises). |
| `blocks` | none | Extra renderer property-block values: `<object path or suffix>\|<property>[:float]`, comma separated. |
| `posEps` | 0.0001 | Metres: smaller position changes are not keyed. |
| `rotEps` | 0.0012 | Radians: upper bound of the size-scaled rotation threshold. |
| `cut` | 0.75 | Metres per sample treated as a teleport (no interpolation across it). |
| `alwaysAnimate` | `true` | Set every Animation and Animator in the record set to AlwaysAnimate during the record (restored at stop). |
| `forceCullers` | `zone` | `zone`: force every bounds culler group of the record set active during the record, so objects the game camera cannot see still move; `list` (only `cullers`); `none`; `planned` does nothing in a manual record (warning). |
| `cullers` | none | Culler names or paths for `forceCullers=list`. |
| `rescanEvery` | 60 | Samples between polls for joining subscenes and new children (at least 1). |
| `res`, `captureGame`, `captureEvery` | | Ignored by a manual record (warning): used by `mcam record` only. |
| `rate` | 60 | Same as the rate part of `clock=fixed:<rate>` (60, 120 or 240). |
| `checks` | none | Not used by the recorder. `tools/srec_tool.py report` reads checks from a spec file or `--check`. |

Unknown keys are reported. `posEps`, `rotEps` and `cut` must be positive.

What the start does, in this order:
- Releases a pending handover of an earlier `srec record stop keep`.
- Pins the `keep` subscenes; if they are not ready yet, replies "srec record start: waiting for ..." and starts later.
- Saves and sets the frame pacing (section 6); re-asserts it every frame and counts changes ("pacing changes").
- Arms the spec events, sets Animations/Animators to AlwaysAnimate and forces the culler groups (`forceCullers`).
- Opens `scene.srec`, stores an event `srec / record start`, starts the audio capture with `audio=on`.
- Replies `recording -> <dir>  clock ..., pacing captureFramerate ... vSyncCount ... targetFrameRate ... (display ... Hz), roots ...`.

Refused when: an mcam run, session, replay or recording is active; a manual recording is already running; the viewer is
loaded; a probe is running; `<dir>` already holds `scene.srec` without `overwrite=on`; a `keep` name is not a subscene; a
key is invalid; the audio preflight fails. A timeScale other than 1 at the start gives a warning (game time runs at that
rate while recording).

### 4.4 `srec record stop [keep]`, `srec record status`

`stop` ends the recording: closes the audio capture (volume put back first), stores `srec / record stop`, writes
`objects.tsv`, `record_meta.csv` and `record.json`, restores the frame pacing and the Animation culling, and replies
`recording stopped -> <dir>: <n> samples, <n> objects, <MB> MB, <s> s, pacing changes <n>; ...`. While the start is still
waiting for kept subscenes, `stop` cancels it.

- Without `keep`: the forced culler groups are released and the kept subscenes unpinned.
- With `keep`: the forced cullers and kept subscenes stay as they are and are handed to the next `srec load` in this game
  session (the viewer releases them at `srec end`). `srec status` shows "handover pending". `srec end` without a viewer, or
  the next `srec record start`, releases a pending handover.

`status` prints the manual recording line (as in `srec status`), "waiting to record ..." with the kept subscenes ready and
the seconds left, "recording (mcam): ..." for an mcam record, or "not recording".

A manual recording also ends when the game quits (stopped without keep) and when its recorder is ended by something else.

### 4.5 `srec event <name> [detail ...]`

Stores a kind `event` row with the next sample of the active recording (manual or `mcam record`). `name`: letters,
digits, `_` and `.`. `detail` defaults to "srec event". In the viewer and in specs, `@name` is the time of the first post of
that name. Replies `event '<name>' stored with sample <n> of the manual recording <dir>` or "no active recording". Allowed
during mcam runs. Mods can do the same with `InsideDev.SceneRecorder.PostEvent(name, detail)`; an object a mod creates
outside `_Dynamics` while recording is added with `InsideDev.SceneRecorder.Register(obj)`.

### 4.6 Viewer: `srec load`, `play`, `pause`, `seek`, `step`, `end`

`srec load <dir> [t=<time>] [cam=game|keep] [key=value ...]`

- `<dir>`: a folder with `scene.srec`, or a take folder with `record\scene.srec`.
- `t=`: start time (time syntax below), default 0.
- `cam=game` (default): the free camera is put at the recorded game camera pose at T. `cam=keep`: the free camera stays
  where it is (it is switched on if it was off).
- Play keys (fields of a spec's `replay.play` block). The viewer uses these; the others are accepted and ignored:

| Key | Default | Values | Effect in the viewer |
|---|---|---|---|
| `interp` | `hermite` | `hermite`, `linear`, `nearest` | Interpolation between samples. |
| `particles` | `extrapolate` | `extrapolate`, `match`, `sample` | `sample` shows particles only as recorded, no extrapolation. |
| `cullers` | `camera` | `camera`, `recorded`, `force`, `live` | Which culler groups are visible: from the viewing camera (exact emulation), as the game camera saw them in the record, all, or the live game culling (diagnostic, warning). |
| `shadows` | `recorded` | `recorded`, `dirty`, `dynamic` | Light static-shadow flags as recorded; `dirty`: static shadows marked dirty every frame; `dynamic`: static shadows switched off on the recorded lights. |
| `activate` | `quiet` | `quiet`, `normal`, `never` | How objects that were inactive at load but active in the recording are switched on. |
| `maxUnresolved` | 0.01 | 0..1 | Refuse the load above this fraction of unresolved moving objects (spawned objects not counted). |
| `monitor` | `true` | | Count gameplay side effects (signals, FSM changes, sound posts) during the replay. |
| `restore` | `final` | `final`, `reload` | What `srec end` leaves (below). |

What `load` does: reads and resolves the recording (in the game session that recorded it, with no level load since, it
uses the recorded live objects; otherwise it resolves every object by path and writes `replay_baseline.tsv` next to the
recording folder: columns score, channel, live, recorded, path, the differences against sample 0), freezes game time
(timeScale 0, re-forced every frame; `srec status` shows "timeScale re-forced Nx" when something else wrote it), takes the
free camera, freezes streaming and culling, keeps the subscenes the recording kept, freezes rigidbodies, disables the
game's own movers of recorded objects, and from then on writes the recorded state for time T every frame. A pending
record handover is taken over. Reply: `srec loaded <dir>: <n> samples, <s> s, <n> objects; frozen at T ...; free camera at
the recorded game camera ...` plus warnings (for example unresolved objects).

Refused when: an mcam run owns the scene; a viewer is already loaded; a manual recording or a probe runs; no
`scene.srec`; a key is invalid; more than `maxUnresolved` of the moving objects cannot be found.

Time syntax (`t=`, `seek`): seconds (`2.5`), `start`, `end`, `@<event>`, `@<event>+<s>`, `@<event>-<s>`. Times are clamped
to the recording. An event that never fired is an error ("event '<name>' did not fire in the recording").

| Command | Effect |
|---|---|
| `srec play [speed] [loop\|once]` | T advances by real time x speed every frame. `speed`: non-zero, at most 100 in size, negative plays backwards, a trailing `x` is allowed (`2x`). Giving arguments resets looping to `once` unless `loop` is given; without arguments the previous speed and loop setting are kept. At the end without loop playback stops; `play` at the end starts again from 0 (or from the end for negative speed). |
| `srec pause` | Stops playing; prints the viewer line. |
| `srec seek <time>` | Sets T. |
| `srec step [next\|back\|prev\|<dt>]` | Default `next`: the next recorded sample; `back`/`prev`: the previous one; a number moves T by that many seconds. Pauses playback. |
| `srec end` | Closes the viewer. Without a viewer it releases a pending record handover. |

What `srec end` leaves: the scene at the recording's last sample (not where it was before `load`), the game camera at the
recorded final pose (with the camera script's state), rigidbodies unfrozen (velocities of the last sample when the game
has not run since the record, else zero), culling and streaming released, game time scale as it was before `load` (if the
game was running it continues from the last sample; FSM timers and sleep states are not restored). With `restore=reload`
the recorded savepoint is loaded again after the end.

Keys while the viewer is loaded: Insert toggles the free camera, WASD or arrow keys move, E / Page Up up, Q / Page Down
down, hold the right mouse button to look, the mouse wheel changes the fly speed, Shift x4, Ctrl x0.25 (section 7).
`cam pose ...` places the camera exactly.

### 4.7 Probes

`srec probe [n] [eps=m] [rot=deg] [out=dir]` (write-order probe)

Needs a loaded viewer. For `n` frames (default 60, 1-10000) it compares every replayed transform right after the
replayer's write (lateUpdate -400) with the same transform at the last component OnPreCull of the game camera. Anything
that changed in between was moved by another script and will not show the recorded pose. `eps` (default 0.00001 m) and
`rot` (default 0.001 deg) are the thresholds. Writes `write_probe.tsv` into `out` (default: the recording folder):
columns `path`, `frames` (frames changed), `maxPos` (m), `maxRotDeg`, `writers` (components with LateUpdate or
UmbrellaLateUpdate on the object or its parents). `srec status` shows progress, `srec probe result` the list (first 200)
and the camera's OnPreCull components. Run it once per new kind of scene.

`srec probe camera [n] [dx=m] [yaw=deg] [eps=m] [rot=deg] [out=dir] [record key=value ...]` (camera probe)

Finds transforms that move when only the camera moves (lens flares, the audio camera probe, water edges, water dust). Such
objects are recorded as camera-driven and never written by a replay. It freezes the game (if no viewer is loaded), holds
the camera still for 3 + `n` frames (`n` default 30, 4-2000), then alternates between the start pose and a pose `dx`
(default 1.5 m) to the right and `yaw` (default 10 deg) turned for `n` frames, and restores everything. It scans the record
set of the given record keys (default `roots=auto`) or, if that is empty, the whole scene. Writes `camera_driven.tsv` into
`out` (default `<game>\_mod\`), merged with the rows of transforms this run did not scan: columns `path`, `components`,
`expected` (1 = on the built-in list), `rigid` (1 = has a Rigidbody or a solid Collider). Every later recording reads
`camera_driven.tsv` from the recording folder, the take folder, sibling take folders, `<game>\_mod\` and
`<game>\_mod\srec\*\` (and their `record\`); rows marked rigid are ignored with a warning. The summary says `ok` when no
built-in camera writer is missing and nothing rigid was listed, else `check`.

A probe stops by itself after 300 frames without progress. `srec end` and a game quit stop it too.

### 4.8 Busy rules

- `srec load`, `srec record start` and `srec probe` are refused while MultiCam runs (`busy: an mcam run owns the scene`,
  `an mcam session is active`, `a replay owned by mcam is active`, `a recording owned by mcam is active`).
- During an mcam capture, record or replay the game refuses outside commands except `srec status|objects|event`, `acap
  status`, `mcam status|stop|abort|check|list|pose|schema|help`, `state`, `game_state`, `ping` and `audio`.
- A manual recording or the viewer does not block other commands. Commands still cost frame time (section 9).

---

## 5. Take folder files and formats

A manual recording writes into `<dir>`:

| File | Content |
|---|---|
| `scene.srec` | The binary recording. |
| `objects.tsv` | Object table, written at stop: `id kind path owner comps first last keys flags`. kind = transform, boyBone, clothBone, particles, light, material, probe, spawned, rope, renderer; owner e.g. `boy:bone:<name>`, `huddle:cloth:<i>`, `spawned:_Dynamics`, `subscene:<name>`; comps = Rigidbody, Animation, Animator, ParticleSystem, Light, Renderer, SkinnedMeshRenderer, MaterialInstance, D11VelocityBufferTag, ColorBlendProbe, LineRenderer, Actor, Collider; first/last = first and last sample with a key; flags = camDriven, scaleVariant, excludedMaterial, spawned, local, camXf, seenActive. |
| `record_meta.csv` | One row per sample: `sample, frame_count, game_time, dt, real_time, logic_step, fixed_steps, time_scale, flags, xf_keys, particles, bytes`. game_time and real_time are seconds since sample 0; flags: 1 audio marker, 2 hitch, 4 hold (the game time stood still before this sample), 8 game frame grabbed. |
| `record.json` | Header (level, savepoint, screen, pacing values, subscenes, kept and forced cullers), counts (`samples`, `objects`, `objectsByKind`, `bytes`, `duration`, `realDuration`), costs (`sampleCost`, `recordSetBuildMs`, `gc`, `hitches`, `skippedFrames`), spawned and deleted objects, `subsceneJoins`, `eventTimes`, `eventsPosted`, `meshSwaps`, `shaderState`, `staleParticles`, `warnings`. A manual record adds `source` ("srec record"), `command`, `pacing` {frames, frameMeanMs, frameP99Ms, within15to18_5ms, hitches, clock, captureFramerate, vSyncCount, targetFrameRate, refreshRate, changes, driftMsEnd, driftMsMax}, `keptSubscenes`, `settle`, `spec` and `specEvents` (with `spec=`), `audio` {capture, closedEarly}. |
| `audio.wav`, `audio_sync.csv`, `audio_capture.json` | With `audio=on` ([AUDIO.md](AUDIO.md) section 6). |
| `write_probe.tsv` | `srec probe` output (default folder). |
| `record_report.json`, `record_report.txt` | Written by `tools/srec_tool.py report`. |

`mcam record` and `mcam take` write the same files into `<out>\record\` plus `events.csv`, `rig.csv`, `tracks.csv`,
`spec.json` and `game\` frames: [MULTICAM.md](MULTICAM.md).

`scene.srec` in short (little endian; the exact layout is the header comment of `src/InsideDev/Runtime/SrecFormat.cs`): an
8-byte preamble (`ISRC`, framing version 1, flags), then blocks `u32 tag, u32 length, payload, u32 CRC-32`: `SRHD` header,
`OBJT` object table, one `SMPL` block per sample (stamp: sample, game time, real time, logic step, frame, fixed steps,
flags; then sections with changes only: transforms, active flags, particles, lights, materials, the game camera, added and
deleted objects, probes, fog values, events, camera-driven activity, ropes, shared materials, strings, shader state), `INDX`
footer and `SEND`. A crash leaves a readable prefix: the viewer and the tools read the samples before the first broken
block ("recording truncated").

Event kinds in a recording: `srec` (record start and stop), `event` (spec events and `srec event`), `marker` (audio
markers), `pacing` (pacing restored), `audio` (capture closed early), `mesh` (a SkinnedMeshRenderer mesh swap) and others
of the mcam record pass.

---

## 6. Clocks and frame pacing

| `clock` | Sampling | Pacing set by the recorder (restored at stop) |
|---|---|---|
| `real` (default) | One sample per rendered frame in which game time advanced. Game time follows real time. | Display reporting 60 Hz: vSyncCount 1, targetFrameRate -1. Any other display: vSyncCount 0, targetFrameRate 60. captureFramerate 0. |
| `fixed:<rate>` (60, 120, 240) | One sample per rendered frame; each frame advances game time by exactly 1/rate s, however long it takes to render. | captureFramerate = rate, targetFrameRate = rate, vSyncCount 0. |

- Frames in which game time does not advance (pause menu, a minimized or unfocused game, a frozen MCP step mode) are not
  sampled; the next sample carries the hold flag (record_meta.csv flags 4).
- With the MCP play tools in step mode the game is frozen between tool calls and runs only inside them. While a recording
  is active the recorder owns the frame pacing; the stepper only freezes and unfreezes. `clock=fixed:60` then gives exactly
  one sample per logic step (dt 1/60 s); `clock=real` gives 1 to 3 logic steps per sample (both measured during
  development).
- Audio needs `real` or `fixed:60`. With `fixed:60` game time and the real-time sound drift apart whenever the game renders
  slower than 60 fps; record.json `pacing.driftMsMax` shows by how much.
- Something that changes captureFramerate, vSyncCount or targetFrameRate during a recording is overruled the next frame
  and counted in "pacing changes" (record.json `pacing.changes`).

---

## 7. Camera and session helpers

All are bridge commands and none is saved.

| Command | Arguments and defaults | Effect |
|---|---|---|
| `cam` | | Prints `free\|game cam at (x, y, z) yaw <deg> pitch <deg> [roll <deg>] fov <deg> near <m>` (`[capture lock]` during a capture). |
| `cam pose x y z yaw pitch [fov] [seconds] [roll]` | fov: omitted or <= 0 keeps the current one; seconds: 0 = at once, else eased (smoothstep) over that many real seconds; roll default 0 | Switches the free camera on and moves it there. Degrees; pitch positive looks down. |
| `cam fov <deg>` | clamped to 5-140 | Free camera field of view (switches the free camera on). |
| `cam roll <deg>` | | Free camera roll (switches the free camera on). |
| `cam json [name]` | name default `cam1` | Prints the current camera (free or game) as a MultiCam camera object: `{"name": ..., "mode": "static", "pos": [x, y, z], "yaw": ..., "pitch": ..., "roll": ..., "fov": ...}`. |
| `cam off` | | Free camera off; the game camera resumes from where it is. Prints the camera. |
| `hud [on\|off]` | no argument toggles | Editor HUD bar on or off. |
| `clean [on\|off]` | no argument toggles | `on`: remembers and switches off the editor panel, HUD, trigger, solid, savepoint, label and hidden overlays, pick mode, the transform gizmo, the selection overlay and the render diagnostics board. `off`: puts them back. Refused to switch off while an mcam session holds clean frames. editor.cfg is not written. |
| `gamma [<0..1>\|reset]` | value clamped to 0-1 | Without argument prints the post gamma (`gamma_gamma`). A value sets the game's post gamma directly (D11PDPostCombined.SetGamma); the user's brightness setting is not changed. `reset` puts back the value from before the first `gamma`. |
| `seed <n>` | integer | Seeds UnityEngine.Random, resets the game's RandomGenerators and sets PDRandom.seed. Run it right before the moment you want to repeat. It cannot be undone. |

Related bridge commands: `freecam [on|off]` (no argument toggles), `zoom <factor>|reset`, `bg on|off`
(Application.runInBackground: the game keeps running without focus).

Free camera keys (not while a text field has focus or during a capture): Insert toggles it, WASD or arrow keys move, E /
Page Up up, Q / Page Down down, hold the right mouse button over the game view to look, the mouse wheel changes the speed
(0.5-100 m/s, default 6), Shift x4, Ctrl x0.25, + / - zoom, 0 resets the zoom. While the free camera is on the boy gets no
input.

---

## 8. Offline tool

Run them with Python 3.9 or newer from the release folder, for example `python -I tools/srec_tool.py ...`. They never start
or touch the game. Paths are arguments; there are no built-in folders.

### 8.1 `tools/srec_tool.py` (standard library only)

`PATH` is a `.srec` file, a folder with `scene.srec`, or a take folder with `record\scene.srec`.

| Command | Options | Output | Exit codes |
|---|---|---|---|
| `info PATH` | `--json` | Header, sample count and duration, frame interval statistics, hitches, holds, markers, objects by kind, keys, particles, shader channels, per-section sizes, problems and warnings. | 0, 2 (unreadable or missing) |
| `verify PATH` | `--strict` (a truncated recording fails), `--size` (fail when the size is outside 0.5-2x the size estimate), `--json` | `OK` or `FAIL` with problems: block CRCs, sample and id order, added and deleted objects, string references, footer, `record_meta.csv` and `objects.tsv` row counts. A crash prefix passes without `--strict`. | 0 ok, 1 problems, 2 unreadable |
| `objects PATH [FILTER]` | `--kind K` (kind name or number) | Object table as TSV to stdout: `id kind path owner comps first last keys flags parent depth added deleted types`. FILTER matches part of the path or owner. | 0, 2 |
| `export PATH WHAT` | `--out CSV` (default stdout) | WHAT = an object id: every key of that object (`sample, game_time, channel, bits, px..pz, qx..qw, sx..sz, parent, value`); `virtual`: the recorded game camera, boy and huddle per sample; `stamps`: the sample stamps; `events`: `sample, game_time, kind, name, detail`. | 0, 2 (bad id or WHAT) |
| `dump PATH` | `--out JSON` | Every decoded entry in file order (for tests). | 0, 2 |
| `report PATH` | `--spec SPEC`, `--check "<query> [@<from>..<to>]"` (repeatable), `--out JSON`, `--strict` | Every recorded event, the spec's events (fired or missing, sample, time, delta to `expect`), and content checks. Writes `record_report.json` and `.txt` next to the recording (or `--out`). | 0, 1 (a check failed; with `--strict` also an unfired spec event), 2 (bad check or unreadable) |
| `synth OUT_DIR` | `--samples N` (240), `--rate R` (60), `--seed S` (1), `--jitter J` (0), `--hitch`, `--truth`, `--no-sidecars`, `--no-shader`, `--name` (`scene.srec`) | A synthetic recording with every section, plus objects.tsv, record_meta.csv, events.csv, record.json (and truth.json), for testing readers. | 0, 2 |

Report spec lookup: `--spec`, else the `spec` path in record.json, else `spec.json` in the recording folder or its parent.
Checks come from the spec's `replay.record.checks` plus every `--check`. A check query is `#<subscene>`, an object path, a
path suffix or a name; it passes when a match was active at some sample. With a window `@<from>..<to>` (seconds or
`event[+-s]`) the match must be active over the whole window, and visible when it has renderers. Example:
`--check "OfficeDudeE @WallSmash..pop_ceo+0.5"`.

---

## 9. Pitfalls

- **Minimized or unfocused game.** A minimized game window pauses the game. Without focus the game also pauses unless
  `bg on` was sent (Application.runInBackground). A paused game records nothing (the next sample carries the hold flag).
  What an audio capture writes while the game is paused is unverified in game.
- **Load races.** A recording started during a level load records the wrong or a half-loaded scene. After `load <sub> <sp>`,
  poll `state` until its `savepoint:` line names the requested savepoint and streaming settled, and use `keep=` for
  subscenes that must be complete at sample 0. `waitfor savepoint <name>` exists only inside an mcam spec `setup`. A level
  load during a recording is not handled specially (unverified in game): stop first.
- **Late-awake content.** Some content switches on a few frames after its subscene reports active. With `keep=` the start
  waits `settleFrames` (12) frames; without `keep` wait about 10 frames after the load yourself.
- **MCP idle restore.** The MCP server restores the game after `mcp.idle_restore_s` seconds (default 600; 0 = never;
  `<game>\_mod\editor.cfg`, edit with the game closed) without any tool call, and after `mcp.restore_on_close_s` (default
  60) once the last MCP session closed. The restore stops an `input_track` recording and playback, cancels the running
  action, gives control back to the player and sets the game clock to real time at speed 1. It does not stop an `srec`
  recording: that keeps recording a scene in which nobody acts any more. Keep calling a cheap tool (for example the
  `command` tool with `srec record status`) during long takes.
- **Step mode and clocks.** In MCP step mode the game is frozen between calls; use `clock=fixed:60` for one sample per
  logic step (section 6). What a sound capture gives for a step-mode take is unverified in game.
- **Free camera.** While the free camera is on (`cam pose`, `srec load`), the boy gets no input. Switch it off (`cam off`)
  before a take you play.
- **`stop keep` and the viewer.** `srec record stop keep` leaves the culler groups forced and the subscenes pinned until the
  viewer that takes them ends, or until `srec end` / the next `srec record start` releases them. Do not leave a handover
  pending.
- **`srec end` does not go back.** It leaves the scene at the recording's last sample and resumes the time scale from before
  `load`. Use `restore=reload` on `srec load` to get the savepoint back.
- **Spawned objects** (anything under `_Dynamics`, mod objects) replay only in the game session that recorded them, with no
  level load in between. A recording of mod-spawned content may be refused in a fresh session (`maxUnresolved`).
- **Destroyed objects** cannot be replayed. Mods should hide objects instead of destroying them while recording.
- **Commands during a recording.** A command that searches objects by name (inspect, call, set, ...) can stall a frame when
  the object database is stale (up to about 1.5 s measured in development); the recording then shows a hitch. Resolve
  targets before the take.
- **Time effects.** Shaders animated by Unity's `_Time` (water surfaces, rain lines, ripples) stay frozen in the viewer;
  water render-texture simulation and hair are not driven either.
- **Audio.** Pitfalls of game audio capture and InsideMuter: [AUDIO.md](AUDIO.md) section 13.

---

## 10. Settings reference

| Setting | Default | Where | Effect |
|---|---|---|---|
| Record keys | 4.3 | `srec record start` key=value, spec `replay.record` | What and how to record. |
| Play keys | 4.6 | `srec load` key=value | Viewer behaviour. |
| Keep wait | 30 s | fixed | Longest wait for kept subscenes. |
| Camera-driven list | none | `<game>\_mod\camera_driven.tsv` (written by `srec probe camera`) | Transforms every later recording treats as camera-driven. |
| `mcp.idle_restore_s` | 600 (0 = never) | editor.cfg | MCP idle restore (section 9). |
| `mcp.restore_on_close_s` | 60 (0 = never) | editor.cfg | Restore after the last MCP session closed. |
