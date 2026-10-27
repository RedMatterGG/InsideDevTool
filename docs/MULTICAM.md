# MultiCam: synchronous multi-camera filming in INSIDE

MultiCam films one run of a scene with many virtual cameras at once. Every camera sees exactly the same game state at every
step: static angles, cameras that follow the boy or an object, keyframed moves, and an in-engine director that flies between
shots. Offline tools then build one video per camera, a Blender-style layout and an edited cut with slow motion and motion blur.
Replay mode (Section 18) records one real-time run instead and films every camera from that recording in its own pass.

MultiCam is part of the InsideDev core (`InsideDev.dll`) and is driven by `mcam` bridge commands. It stays off until such a
command arrives (Section 1.1). The offline tools in `tools\` (spec checker, compositor, QA) run without the game.

Conventions in this guide:

- `<game>` is the INSIDE folder (the one with `INSIDE.exe`), `C:/path/to/InsideDevTool` the folder of this repository or
  release, `<take>` a take folder of your choice (always a full path).
- In-game commands take full paths; forward slashes work. A relative path resolves against the game's working folder, not
  against this repository. Quote a path that contains spaces (`"C:/My Takes/take1"`).
- Python commands run from the repository folder: `python -I tools/...`.

Related documents: [RECORD_REPLAY.md](RECORD_REPLAY.md) (srec recordings and the recording viewer, the cam / hud / clean
helpers), [AUDIO.md](AUDIO.md) (acap sound capture and InsideMuter), [LAB.md](LAB.md) (lab modules), [MCP.md](MCP.md) (MCP server),
[PLAY.md](PLAY.md) (character control through MCP), [MULTICAM_DESIGN.md](MULTICAM_DESIGN.md) and
[REPLAY_DESIGN.md](REPLAY_DESIGN.md) (design contracts), [MULTICAM_ru.md](MULTICAM_ru.md) (short guide in Russian).

Contents

1. How it works (1.1 Switching it on and off, 1.2 Requirements)
2. Quick start
3. Workflow
4. Commands
5. Writing a spec
6. Camera modes
7. Targets
8. Events and cues
9. Timing, slow motion and motion blur
10. Render budget
11. Look: shadows, fog, grade
12. The player-side rule
13. Output folder
14. Compositor and QA
15. The spec checker
16. Troubleshooting (16.1 Known pitfalls)
17. Example specs
18. Replay mode
19. Schema reference

---

## 1. How it works

INSIDE has one real camera. A copy of `_Camera_Main` destroys itself, and the game's post chain crashes when it renders into
a texture. So MultiCam moves the real camera instead:

1. One step frame runs the game (physics, animation, the gameplay camera) at a fixed capture rate.
2. Game time freezes (`timeScale 0`).
3. The real camera visits every virtual camera in turn. Each visit renders 1 warmup frame plus N anti-aliasing (TAA) frames,
   then reads the picture back and saves it.
4. The gameplay camera pose is put back, and the next step runs.

Because time is frozen during the visits, all cameras show the same moment. Slow motion means more steps per game second.
Motion blur means several steps averaged into one output frame.

### 1.1 Switching it on and off

MultiCam has no setting, hotkey or background task. Until a command that starts with `mcam` arrives, none of its code runs
and no game value is changed, so a player who never sends one sees no difference.

Sending commands. `mcam` and its helpers are bridge commands; the editor's in-game console does not run them. Send them
through one of:

- the MCP `command` tool (`InsideDev.Mcp.dll`, see [MCP.md](MCP.md));
- plain HTTP `POST /cmd` on `http://127.0.0.1:47811` (same DLL; the request body is one command line).
  `python -I tools/inside_ctl.py mcam status` sends one (add `--bridge <game>/_mod/cmd` for its file-bridge fallback);
- the file bridge: write `<game>\_mod\cmd\in_<anything>.txt` containing `<id>|<command>`; the answer appears in
  `<game>\_mod\cmd\out.txt`.

What each kind of command does to the game:

| Commands | Effect |
|---|---|
| `mcam check`, `list`, `pose`, `schema`, `status`, `help`, `probe` | Read only. |
| `mcam views` | Moves a loaded srec viewer to each checked time and back. |
| `mcam show <cam> [t]` | Moves the free camera to that camera's pose and applies its `hide` / `backfaces` sets. This stays until `cam off`, the next `mcam show` or an mcam run. |
| `mcam start`, `rehearse`, `preview`, `record`, `replay`, `take`, `passes`, `golden` | A run, with a capture session (below). One run at a time. |
| `mcam readback test` | A 120-frame test of the readback plugin (Section 1.2). |

A run pushes a capture session when it starts and pops it when it ends, on `mcam stop`, on `mcam abort` and after an error.
The session saves and later puts back: time scale, fixed delta time and capture frame rate, vsync and target frame rate,
`Application.runInBackground`, the resolution (`session.res`), the editor overlays and HUD (`session.clean`), the free camera,
the game camera's FOV, near clip and shake flags (`session.handshake`, `session.shake`), the TAA and post values the run
changes, rigidbody interpolation (`interpolate`), kept-loaded subscenes and the culling switch (`stream`), PlayMaker FSM
logging, and objects hidden for a camera.

Stopping a run:

- `mcam stop`: finishes the current step, writes the files and restores the session.
- `mcam abort`: restores the session at once, also when the runner has died; it stops a running sound capture first.
- `mcam status` prints progress. When a session was left behind it says `[session still active: run mcam abort]`, and a new
  run is refused with `the previous capture session was not restored: run 'mcam abort' first`.

Capture lock (`session.lockInput`, default true). While a run holds the game, editor hotkeys, the command palette and editor
mouse actions are off, and outside commands are refused with `busy: mcam capturing (...)`, except
`mcam status|stop|abort|check|list|pose|schema|help`, `srec status|objects|event`, `acap status`, `state`, `game_state`,
`ping` and `audio`. The run's own setup and cue commands pass. MCP play tools refuse world changes during a capture, and the
MCP restore never cancels a run.

What a run leaves behind:

- The spec's `setup` and `cues` commands are not undone: `god on` stays on, `panel off` leaves the editor panel closed, a
  `load` stays loaded, audio rules stay, `seed` has reseeded the game's random generators.
- Replay runs (`record`, `replay`, `take`, `passes`, `golden`) end with the game frozen at the last sample: `Time.timeScale`
  0 is held every frame until `ts <value>` (`ts 1` resumes play), a level load or the next mcam run. `mcam status` shows
  `[game frozen ...]`. With `replay.play.restore: "reload"` the savepoint is loaded again instead.
- A v2 run (`start`, `rehearse`, `preview`) gives the clock back as it found it (a game that was frozen stays frozen); the
  game continues from the take's last step.

Helpers (details in [RECORD_REPLAY.md](RECORD_REPLAY.md)): `clean on|off` switches every editor overlay off and back without
writing `editor.cfg` (`clean off` is refused while a run holds clean frames), `hud on|off` the HUD bar (not saved),
`gamma <0..1>` / `gamma reset` the post gamma (not saved), `cam off` gives the camera back to the game.

### 1.2 Requirements

| Part | Needed for | Install |
|---|---|---|
| `bin\InsideDev.dll` (with the loader `bin\version.dll`) | every in-game command | as in the README (`<game>\_mod\`) |
| `bin\InsideDev.Mcp.dll` | optional: the MCP `command` tool and `POST /cmd` (`tools/mcam_previews.py` needs it) | `<game>\_mod\` |
| `bin\InsideReadback.dll` | optional: native asynchronous frame readback | `<game>\INSIDE_Data\Plugins\`, copied while the game is closed |
| ffmpeg | pipe output in game; every video tool | on PATH, or see below |
| Python 3 with numpy, opencv-python, Pillow, numba, matplotlib | the offline tools | `pip install numpy opencv-python Pillow numba matplotlib` |

Readback plugin. `output.readback` chooses how frames come back from the GPU: `auto` (default) uses the plugin when it is
installed and finds the swap chain, else ReadPixels on the game thread (the run log says `native plugin not used (...)`);
`native` fails without the plugin; `readpixels` never uses it. The plugin is used by replay passes with
`output.format: "pipe"` and by `record.captureGame` and golden grabs (Section 18.2); live v2 visits (`mcam start`) always
use ReadPixels. `mcam readback test` (about 12 s, poll `mcam readback status`) checks the plugin; `mcam readback stat` prints
its counters or why it is unavailable (`INSIDE_Data/Plugins/InsideReadback.dll is not installed`).

ffmpeg in game. `output.format: "pipe"` starts one ffmpeg per enabled camera (at most 8, NVENC sessions). `output.ffmpeg`
is `"ffmpeg"` by default, looked up as `ffmpeg` and `ffmpeg.exe` in the folders of the game process's PATH; a full path
works too. `mcam check` warns `output.ffmpeg: not found` when neither resolves. `output.pipeCodec` (default `hevc_nvenc`,
H.265 only) needs an NVIDIA GPU; `libx265` is the CPU alternative.

ffmpeg for the tools. `mcam_take.py` takes ffmpeg and ffprobe from the environment variables `FFMPEG` / `FFPROBE` (full
paths), else from PATH. The compositor needs `hevc_nvenc` (default encoder; `--codec x265` uses libx265) and `zscale`
(libzimg) for the colour conversion. Pipe takes decode on the GPU (NVDEC `-hwaccel cuda` with `scale_cuda`); when that
decode test fails the compositor decodes on the CPU by itself, `--cpu` forces it.

Python packages: numba only speeds up the sub-step blur and saturation (plain numpy is used without it); matplotlib is only
used by `mcam_qa.py paths` and `timeline`.

Label font. The compositor looks for `tools\fonts\Oswald-Regular.ttf`, which is not shipped, then for Bahnschrift
(`C:\Windows\Fonts\bahnschrift.ttf`, part of Windows 10 and 11). `compose.labels.font` takes another font as a `.ttf` /
`.otf` file path. Without any of them the labels use Pillow's small built-in font.

## 2. Quick start

Two example specs ship in `specs\`:

- `specs/template_minimal.json`: a v2 capture of the place where the game already is (no load): the game camera plus one
  static angle, 5 game seconds in real time. Its static pose is a ChickenGun pose; replace it with your own.
- `specs/replay_smoke.json`: replay mode at ChickenGun, the hay bale on the farm (`load 7 0`): the vacuum fires chicks at a
  hay bale on a loft, and the bale falls 3-5 s after suction starts. 10 s recorded once in real time and filmed through the
  game camera and a static view of the outlet; the record pass also grabs every 10th game frame for a golden check.

v2 capture with the template:

1. Copy the template, then check it offline: `python -I tools/mcam_spec_check.py specs/template_minimal.json`.
2. In game, fly the free camera to an angle and run `cam json wide`: it prints the pose as a camera object. Paste it over the
   `wide` camera.
3. `mcam check C:/path/to/InsideDevTool/specs/template_minimal.json`: target report, segments, events, cost.
4. One still per camera: `mcam preview C:/path/to/InsideDevTool/specs/template_minimal.json <take>`, then
   `python -I tools/mcam_qa.py sheet <take>`.
5. Capture: `mcam start C:/path/to/InsideDevTool/specs/template_minimal.json <take>`; follow it with `mcam status`.
6. Videos and checks: `python -I tools/mcam_compose.py all <take>` and `python -I tools/mcam_qa.py qa <take>`. Both write to
   `C:/path/to/InsideDevTool/out/<spec name>/` (`template_minimal` for an unchanged copy; Section 14).

Replay with the smoke spec (the same steps are in its `_workflow` key):

1. `python -I tools/mcam_spec_check.py specs/replay_smoke.json`
2. `mcam check C:/path/to/InsideDevTool/specs/replay_smoke.json`
3. `mcam take C:/path/to/InsideDevTool/specs/replay_smoke.json <take>`: the setup loads ChickenGun, waits for the savepoint
   and spawns the chicks, the record pass runs 10 s in real time, then each camera gets its pass. The game stays frozen at
   the end: `ts 1` resumes.
4. Golden check: `mcam golden C:/path/to/InsideDevTool/specs/replay_smoke.json <take>`, then
   `python -I tools/mcam_qa.py replaydiff <take> --record <take>/record`.
5. `python -I tools/mcam_compose.py angles <take>`, `python -I tools/mcam_compose.py layout <take>`,
   `python -I tools/mcam_qa.py qa <take>`.

## 3. Workflow

| Stage | Tool | What you get | When to repeat |
|---|---|---|---|
| Author | editor, `cam json` | the spec JSON | new scene or angle |
| Check | `tools/mcam_spec_check.py` | errors, warnings, segment table, cost, layout sizes, cut holds | after every edit |
| In-game check | `mcam check <spec>` | target report (`ref -> path #id active pos`), events, segments | after a scene change |
| Preview | `mcam preview <spec> <out> [t]` | one still per camera | until every angle is framed |
| Rehearse | `mcam rehearse <spec> <out>` | events.csv, poses.csv, tracks.csv, no pictures | before every capture with event-relative times |
| Capture | `mcam start <spec> <out>` | the take folder | once per take |
| Compose | `tools/mcam_compose.py` | angle videos, layouts, the cut (H.265) | for every edit change |
| QA | `tools/mcam_qa.py` | qa.json / qa.txt, contact sheets, path plots | after every capture |

Tips:
- Find a pose by flying the free camera, then `cam json name` prints it as a ready camera object.
- Rehearsals are cheap (one frame per step). Use them to tune event times, director timing and paths
  (`mcam_qa.py paths` on the rehearsal folder) before spending capture time.
- `seed` makes the chick spawn and other game randomness repeat between the rehearsal and the capture.

## 4. Commands

All commands below are bridge commands of `InsideDev.dll` (Section 1.1 lists the ways to send them).

### 4.1 `mcam`

| Command | Result |
|---|---|
| `mcam check <spec> [json] [replay\|v2]` | Load + lint + in-game target report, segment table, events, cost. A spec with a `replay` block gets the replay lint; `replay` forces it, `v2` turns it off. Allowed during a run (separate spec object). |
| `mcam start <spec> <out>` | Capture a take. Refused while another run is active or an srec recording / viewer is loaded. |
| `mcam rehearse <spec> <out>` | Full timing, cues and events, all CSV logs, no camera visits. |
| `mcam preview <spec> <out> [t]` | One still per camera now (frozen state, no setup), or at game second `t` of the take (the live game is played to `t`). |
| `mcam stop` | Finish the current step, write the files, restore everything. |
| `mcam abort` | Restore the session at once, even if the runner died; a sound capture is stopped first. |
| `mcam status [json]` | One-line progress, or progress.json. |
| `mcam show <camera> [t]` | Put the free camera at that camera's pose at game second `t` (default 0) of the last loaded spec; the reply has a `view:` and a `hide:` line. Refused during a run. |
| `mcam pose <camera> [t]` | Print that pose: x y z yaw pitch roll fov aim, and a ready `cam pose` line. During a run: the camera's current pose. |
| `mcam views <spec> [shots \| t,t,...] [cams=a,b] [out=<file.json>]` | View checks of camera poses (6.2). |
| `mcam list` | Cameras of the last loaded spec with modes and flags. |
| `mcam probe <obj>` / `mcam probe <x y z r>` | Authoring help: signals, FSMs, triggers, rigidbodies near an object, with ready `events` snippets. |
| `mcam schema` | The live schema from the C# types. |
| `mcam readback test\|status\|stat` | Readback plugin test and counters (1.2). |
| `mcam help` | The usage line. |

The replay commands (`mcam record|replay|take|passes|golden`, `srec`, `acap`) are in Section 18.4. Section 1.1 lists what
passes the capture lock.

### 4.2 Camera and session helpers

| Command | Result |
|---|---|
| `cam` | Describe the camera. |
| `cam pose x y z yaw pitch [fov] [seconds] [roll]` | Move the free camera (eased over `seconds`). |
| `cam off` | Back to the game camera. |
| `cam fov <deg>` / `cam roll <deg>` | Free camera FOV / roll. |
| `cam json [name]` | The current pose as a spec camera: `{"name", "mode": "static", "pos", "yaw", "pitch", "roll", "fov"}`. |
| `hud on/off` | Editor HUD bar (not saved). |
| `clean on/off` | Switch every editor overlay off / restore it (editor.cfg is not written). |
| `gamma <0..1>` / `gamma reset` | Post gamma directly (not saved to the user brightness). |
| `seed <n>` | Seed the game randomness now. |
| `ts <scale>` | Game time scale; also releases the freeze hold after a replay run. |

### 4.3 Setup primitives

Only in `setup`: `wait <real seconds>`, `waitfor loaded`, `waitfor streaming` (streaming active and all cullers done),
`waitfor savepoint <name>`, `waitfor event <name>` (a spec event). `waitfor savepoint` takes `<subscene> [<savepoint>]` as
numbers (as in `load 7 0`) or a part of the label `game_state` prints (e.g. `#chickenGun`). Each `waitfor` fails the run after
60 real seconds (`setup timeout after 60 s`); `wait` has no limit.

### 4.4 Game sound and run rules

- MultiCam never mutes the game: a take plays the game sound as normal play does. To keep the game silent during a take,
  add these two lines at the start of `setup` (before any `load`):

  ```json
  "audio force rtpc rtpc_master_volume 0", "audio native on"
  ```

  The first is an audio rule: every set of the Wwise master volume RTPC uses 0. It lasts until it is removed or the game
  quits, and it is recorded in History. The second mirrors the audio rules into the loader (`version.dll` v3) so they also
  apply to calls that reach AkSoundEngine directly; this switch is remembered in `<game>\_mod\editor.cfg`. Remove the rule
  afterwards with `audio clear rtpc:rtpc_master_volume`: it puts back the last volume the game itself set in this session.
  If the game stays silent, force its volume first (`audio force rtpc rtpc_master_volume <0-100>`) and then clear the rule
  ([AUDIO.md](AUDIO.md) section 10). Switch the mirror off with `audio native off` if you do not want it.
  `audio` commands pass the capture lock, so all of them work during a take.
- Sound capture in replay mode: Sections 18.6 and 18.7.
- `tp x y z` needs three numbers. Object paths starting with `#` are instance ids and change every session.

## 5. Writing a spec

A spec is one JSON object. Minimal example (`specs/template_minimal.json`, notes left out):

```json
{
  "version": 2,
  "name": "template_minimal",
  "seconds": 5,
  "setup": ["god on", "panel off", "triggers off", "labels off", "savepoints off"],
  "render": {"warmup": 1, "samples": 8},
  "cameras": [
    {"name": "game", "mode": "game"},
    {"name": "wide", "mode": "static", "pos": [225, -219, -6], "yaw": 20, "pitch": -18, "fov": 45}
  ],
  "compose": {
    "layouts": [
      {"name": "side_by_side", "width": 2560, "gap": 4, "rows": [[{"cam": "game"}, {"cam": "wide"}]]}
    ]
  }
}
```

Rules:
- Always write `"version": 2`. Without it (or with a version below 2) the v1 defaults apply: fov 40 for non-game,
  non-director cameras without fov, settle 3, JPEG 92, and `boy` / `huddle` targets and looks use the `pos3` point. The
  checker warns when a spec has v2 keys but no version.
- Key names are case-sensitive. Unknown keys are ignored with a warning and a "did you mean" hint.
- Keys starting with `_` and the key `comment` are ignored anywhere: use them for notes (`"_note": "..."`).
- `null` means "not given".
- `cameras` and `events` may be a list or an object map: `"events": {"baleFall": {...}}` names the event `baleFall`.
- A list of strings or targets may be a single value: `"interpolate": "HayBaleHolder/HayBale"`.
- Times are game seconds from t=0 (after `preroll`), or event-relative: `"@baleFall"`, `"@baleFall+0.3"`, `"@baleFall-0.5"`.
- `defaults` holds camera fields applied to every camera that does not set them. Nested blocks (`render`, `post`) are
  merged field by field: root, then `defaults`, then the camera.
- Angles are Unity Euler degrees: yaw 0 looks into the set (+Z), positive yaw turns toward +X, positive pitch looks DOWN.
  FOV is vertical; 0 means the live game FOV (50.99).
- Setup commands change the game and are not undone after the take (Section 1.1).

Run order of a take: setup commands -> seed -> preroll (game runs, nothing captured, no cues) -> t=0 -> steps with cues and
camera visits -> end at `seconds` or `until`, whichever comes first.

## 6. Camera modes

| Mode | Use it for | Key fields |
|---|---|---|
| `game` | The real gameplay shot (top band of the layout). | `clean` (no handheld), `offset` (in the game camera frame), `fov`, `fovScale` |
| `static` | A locked-off angle; with `look` it pans like a tripod operator. | `pos`, `yaw`, `pitch`, `roll`, `look`, `lookOffset`, `lookSmooth` |
| `follow` | A camera travelling with a subject (boy, bale, group). | `target`, `offset`, `local`, `smooth`, `lag`, `lead`, `look`, `planar`, `start` |
| `keys` | Authored moves: dolly, crane, push-in along keyed points. | `keys[]` (`t`, `pos`, `yaw`/`pitch` or `look`, `fov`), `ease`, `interp`, `start` |
| `rail` | A constant-speed truck / dolly / crane along a polyline. | `path`, `duration`, `start`, `look` or `yaw`/`pitch` |
| `orbit` | An arc around a subject. | `target`, `radius`, `height`, `azimuth` [from, to], `duration`, `ease` |
| `gamerel` | Another angle of the game shot that keeps every designer zoom and lock. | `orbitYaw`, `orbitPitch`, `dist` / `distScale`, `shift`, `fov` + `keepSize` |
| `gamelike` | The game's own follow logic (camera boxes, look-ahead) for any target. | `target`, `box`, `camOffset`, `aimOffset`, `extrapolate`, `lag` |
| `frame` | Keep a subject at a screen point and size, also inside the crop band. | `target`, `screen` [u, v], `size`, `yaw`/`pitch`, `crop`, `smooth`, `lead` |
| `director` | An in-engine edit that flies between the other cameras. | `shots[]` (`cam`, `from`, `blend`, `ease`, `transition`, `clock`), `cutAngle`, `minHold` |
| `overview` | A wide level shot at a fixed height and depth that pans in x with the action, never past given frame edges. | `target`, `pos` (y, z), `pitch`, `yaw`, `fov`, `offset.x`, `smooth`, `lead`, `clamp`, `pans[]` |
| `events` | A camera that moves from subject to subject on events and holds each one, keeping the view clear. | `beats[]` (`at`, `target`, `hold`, `move`, `offsets`, `lookOffset`, `fov`), `offsets`, `hold`, `move`, `late`, `ease`, `view` |

Notes per mode:
- `follow`: the camera sits at target point + `offset` (world space; `local` rotates it with the target). `lag: "damp"`
  smooths with `smooth` seconds, `"game"` uses the game camera's own smoothing curves, `"none"` is rigid. It looks at the
  target + `lookOffset` unless `look` says otherwise (`"none"` = fixed `yaw`/`pitch`). `start` holds the first pose until a
  time or event.
- `keys`: `t` is seconds on the camera clock, which starts at `start` (default 0). `ease: "ends"` (default) moves at constant
  speed along the path with a soft start and stop over the first and last 20 %; inner key times only shape the path.
  `"keys"` passes each key at its time, `"each"` stops at every key (v1), `"linear"` has no easing.
- `orbit`: azimuth 0 is straight in front of the subject; stay within +-60 deg.
- `director`: the current shot is the last one whose `from` is reached. While it holds a shot it copies that camera's frame
  (no extra renders). A blend uses the game's orbit interpolation (`transition: "orbit"`), or `"linear"`, or `"cut"`.
  `clock: "output"` measures `blend` in output seconds, so a blend keeps its screen length in slow motion. Angles above
  `cutAngle` become hard cuts. Holds shorter than `minHold` output seconds are warned.
- Every mode except `director` can add a `shake` layer: `hand` / `fx` copy the gameplay camera's handheld and impact shake,
  `replica` / `hard` play the game's handheld noise clips (`clock: "output"` keeps a real-time handheld in slow motion).
- `overview`: the camera keeps `pos.y`, `pos.z`, `pitch`, `yaw` and `fov`; its x follows the target's x + `offset.x` (+ `lead`
  seconds of velocity) with `smooth` seconds of damping. The x is limited before the damping, so the frame slows down
  softly at the `clamp` edges instead of stopping hard. `pans[]`: from each `from` the frame centre moves to `to` (world x)
  over `duration` seconds and stays there (the ending pan back over the route). `mcam_spec_check.py` prints the frame width
  at the subject depth and the centre-x range the clamp leaves.
- `events`: the beats are sorted by their event times; a beat starts at its time or when the previous kept beat's `hold`
  ends, whichever is later, and is dropped when that is more than `late` seconds after its event (a beat on an event that
  never fired is dropped too). The camera sits at the subject + offset looking at the subject + `lookOffset`, follows it
  with `smooth` damping and moves from the previous beat's live pose over `move` seconds (`ease`). Each beat tries its
  candidate offsets (`offsets` of the beat, else of the camera, else `offset`) in order at its first evaluation and keeps
  the first whose view is clear (6.2); with none clear it takes the least bad one and logs why. Before the first beat it
  holds the camera `target` (idle) or the first beat's pose. Beats chain by hold: long early holds make later beats start
  late or get dropped (`late`), so size the holds against the event times.

### 6.1 Frame-edge clamp (`clamp`, any mode)

`clamp: {"left": x0, "right": x1}` keeps the frame's left / right edge at world x >= x0 / <= x1 at the subject depth: the
depth of the pose's aim point along the view axis, or the world plane z = `clamp.z` when `z` is given. The frame corners
count too (`corners: true`), so the outermost edge ray decides. A pose that would pass an edge trucks in x (camera and aim
move together, the orientation stays; pose flag C); a frame wider than both limits is centred between them. `aspect`
replaces the render aspect, for a layout cell that shows a centre crop. A spec-wide wall goes into `defaults.clamp` (for
example `"right": 3524.55`, the CEO office window glass, for every camera). The edge is only checked at that one depth: a
frame that also shows things nearer or farther than the subject can reach past the wall at those depths, so check the
stills.

### 6.2 View checks (`view`, any mode)

A view check answers two questions for a pose and its subject (the pose's aim point, or the beat target):

- target visible: the camera -> subject segment, stopped `subjectRadius` short of the subject, hits no non-trigger collider
  on `layers` ([] = all but Ignore Raycast; paths containing an `ignore` entry skipped; colliders under the subject's own
  object never count, for the boy and the huddle under its parent) and misses the bounds of the `avoid` targets and the
  huddle cloth bounds (`huddle: true`), each grown by `margin`. A box that holds the subject is the subject and is skipped.
- near plane inside geometry: the camera point and the four near-plane corners, each inside a collider, inside an avoid
  box, or behind a surface that faces the subject but not the camera (a closed collider around the camera, or a one-sided
  set wall seen from its back; Unity 5.0 rays do not hit back faces).

Props without colliders (the cafeteria tables) are invisible to the collider test: add them to `avoid` (point `bounds`) or
keep the camera above them. Where the checks run:

- `events` cameras choose each beat's offset with it.
- Replay plan passes check every enabled camera's pose every `view.every` record seconds (default 0.5): `views.csv`
  (step, game_time, camera, source shot, visible, blocker, block_m, inside, inside_by, camera and subject positions,
  path_hit) and run.json `views` (per camera: checks, blocked, inside, pathHits, first times and blockers).
- Camera path check (replay plan passes, every captured step, every enabled camera): the straight segment from the camera's
  previous position to the current one, cast both ways (rays miss back faces), crossing a collider is a path hit:
  `views.csv` `path_hit` carries the first collider crossed since the camera's previous row, run.json `views` has
  `pathHits`, `firstPathHit`, `firstPathBy`, and the run warns once per camera ("its path passes through level geometry",
  and "camera or near plane inside geometry" for the inside checks). `mcam passes <spec> <out>` runs the plan pass without
  rendering, so these warnings come before any frame is shot. `mcam_qa.py qa` repeats them per camera with game times.
- Colliders under objects the camera hides at that moment (6.4) never count, in all checks.
- `mcam views <spec> [shots | t,t,...] [cams=a,b] [out=<file.json>]` with the srec viewer loaded: moves the recording to each
  time, evaluates the cameras and prints one line per pose; `shots` (default) checks every director shot after its blend
  and at mid-hold and every events beat after its move and at mid-hold. The scene goes back to the viewer's time after.
  Without a loaded recording it checks the live scene as it is now.
- `mcam show <cam> [t]` and `mcam pose <cam> [t]` print a `view:` line under the pose.

### 6.3 Motion limits (`motion`, any mode except `game`)

Fast camera moves smear: INSIDE's TAA motion blur works from the camera's motion between renders, so a whip pan or a long
fast blend reads as a white or black smear. `motion` limits a camera's speed on the output clock (what the viewer sees):

| Key | Default | Meaning |
|---|---|---|
| `maxSpeed` | 6 | Peak camera speed, metres per output second (0 = no limit). About 35 deg/s of parallax for a subject 10 m away. |
| `maxTurn` | 35 | Peak turn rate, degrees per output second (0 = no limit). |
| `ease` | 0.6 | Seconds to speed up and to slow down: a planned move lasts at least 2 x ease; the limiter accelerates by maxSpeed / ease per second. |
| `blurScale` | 1 | Scale of this camera's replay pass blur strength (6.3.1). |

- **Director blends** (always for v2 specs, with these defaults when the camera gives no `motion`): at each blend start the
  director measures the blend path (16 samples of the actual orbit / linear interpolation) and the turn between the two end
  orientations, and stretches the blend to `MoveSeconds` = max(peak x distance / maxSpeed, peak x turn / maxTurn, 2 x ease),
  with peak = 1.5 (smooth), 1.875 (smoother), pi/2 (sine), 1 (linear, which has no ease). `blend` stays the minimum. The log
  says `blend 1.2 s stretched to 3.4 s by the motion limits (14.3 m, 64 deg ...)`. A stretched blend can run into the next
  shot (the next blend starts from the current pose).
- **Events beat moves** (same defaults): the move from the previous beat is stretched the same way (straight path, ease
  `ease`). A move interrupted by the next beat starts the next one from the camera's current pose (no jump).
- **Other modes** (follow, overview, keys, rail, orbit, frame, gamelike, gamerel, static with look): only when `motion` is
  given (on the camera or in `defaults`), the final pose (after the player-side and edge clamps, before shake) follows the
  wanted pose through a limiter: speed and turn rate never above the limits, accelerating and braking by limit / ease per
  second and braking early enough to stop on the target. A jump of more than 25 m (a cut in the source) is taken at once.
  It is not applied to `game`. Subject-relative: a pose that tracks a subject carries it (`subj`, its velocity, and for
  follow / frame / overview the velocity the position follows); before each limit step the limiter state first moves with
  the subject (the position by that velocity, and the rotation by the turn that keeps the subject on the same view ray when
  the camera aims at it), so the allowed motion is maxSpeed / maxTurn plus the subject's own speed and angular rate seen
  from the camera. Tracking a subject at 70 deg/s under maxTurn 35 stays within 1 deg; reframing and switches to another
  subject stay capped.
- v1 specs (no `version`) get no default limits.
- Offline: `mcam_spec_check.py` warns when a blend between two static cameras cannot meet the limits in its time window; with
  `--poses <take>` it measures every camera of a shot take (poses.csv) per output frame and warns where a camera with limits
  went faster, with game times, and checks every director blend and events move of the take against its window.

#### 6.3.1 Blur scale (`shots[].motionBlur`, `motion.blurScale`)

Replay passes set the native TAA's `motionBlurStrength` (D11PDTemporalReprojection3, read every frame by its resolve) before
every render and put the session value back at the pass end and at the session pop. The strength of a render is the
`play.motionBlur` value (18.3: `game` = 0.8 x (1/60) / record seconds per render) times the camera's `motion.blurScale`
times, for a director, the `motionBlur` of the shot on air (its blend included; 1 = unchanged, 0 = no blur). Copied director
holds require equal strengths.

### 6.4 Hiding objects (`hide`, `shots[].hide`)

`hide` lists object paths (strict resolution, Section 7) whose renderers (the object and its children) a camera does not
draw. A director's hide set at a time is its own `hide`, plus the shot on air's `hide` and that shot's source camera's `hide`;
inside a blend (pose flag B) also the previous shot's set (an object only in the old shot's set appears when the blend ends;
list it in both shots to keep it hidden).

| Where | How |
|---|---|
| v2 visits (`mcam start`) | the renderers are disabled for the visit and enabled after it (directors per shot) |
| replay passes (`mcam replay`, `take`) | the replayer owns `Renderer.enabled` (culling emulation), so the GameObjects of the hidden renderers move to a spare layer (31 unless an active renderer uses it; unnamed layers 31, 26, 25, 24, 19, 9, 7, 6, 3 are the candidates: no object of any level uses them) and the pass camera's `cullingMask` drops that layer for each render of the set; layers and mask go back when the set changes, at the pass end, on abort and at the session pop (the mask exactly: the saved value when nothing else changed it, else only the spare bit is restored) |
| `mcam show <cam> [t]` | the same layer mechanism on the free camera; the reply has a `hide:` line; it stays until the next `mcam show`, `cam off` or an mcam run |
| view and path checks (6.2) | colliders under hidden objects never count |

Part of a mesh: `"<path> box=x0,y0,z0,x1,y1,z1"` hides only the triangles of the object's meshes whose three vertices lie inside
that world box. Use it for a prop baked into a level batch mesh (for example an office floor lamp that is 2692 of the 12172
triangles of `1_OfficeDetails_CULL/Batch__Basic_50_Baked_36516`, a batch that also holds the window frames). While the set is
applied the MeshFilter shows a cut copy of its mesh (built once per filter and box); every restore puts the game's mesh back.
The path must not start with `#` (that is an instance id). A mesh that is not readable or has no triangle in the box gives a
warning and hides nothing; `mcam show` and the run log print `box cut N of M triangle(s) of <path> in [box]`. The checker
validates the box.
Several boxes: `"<path> box=x0,y0,z0,x1,y1,z1;x0,y0,z0,x1,y1,z1"` (up to 31, separated by `;`) cuts every triangle whose three
vertices lie inside one of them. Separate entries whose boxes hit the same MeshFilter in one set (a camera's `hide`, a
director's shot and source lists) are merged too: one cut copy with all their boxes, cached by the sorted boxes, so the same
boxes in any order reuse it. With more than one box the reply / log lists the triangles per box
(`[box 1]=2692 + [box 2]=385 (triangles per box)`); an entry whose boxes cut nothing gets the `box cut nothing` warning. A
triangle that spans two boxes is not cut: make one box cover it. `backfaces` boxes work the same way.
`mcam check` resolves the path part and lists `-> <path> box [box]` (a malformed box is an error). v2 visits (`mcam start`)
resolve the path but do not cut: the run log notes it. To draw a mesh from behind instead of hiding it see 6.5
(`backfaces`).

run.json `pass_stats[].hide` lists per pass the layer, the hidden object paths, the renderer count, names that did not
resolve, and `spans` (record time from / to and the objects of each set the pass used). The log has one line per set:
`hide set 0 (first for director at 26.1 s): 3 renderer(s) of ...`.

Code facts (unverified in game): the game's velocity camera copies the main camera's mask (PDVelocityBuffer.OnPreCull
CopyFrom), so hidden objects write no velocity there; the explicit velocity buffer (D11PDVelocityBufferExplicit) draws
VelocityBufferTag meshes without any layer test; planar reflections use their own `ReflectionMask`. Whether a hidden object
still throws its shadow (static shadow caches, cached shadow maps) is not checked yet. A set that changes mid-pass (a
director shot switch) is not a TAA reset: the object fades out over the TAA history in a few frames, so switch sets inside
a blend, not on a hold.

### 6.5 Drawing set meshes from behind (`backfaces`)

Sets are built for the game camera: many level meshes are single-sided (back faces culled), so a replay camera behind or below
them sees through them. `backfaces` lists object paths (strict, as `hide`; `"<path> box=x0,y0,z0,x1,y1,z1"` = only the
triangles whose three vertices lie inside that world box, tested at the rest pose) whose meshes the camera also draws from
behind. Per camera, or for every camera through `defaults.backfaces` (a camera's own list replaces the default, like every
`defaults` field); a director takes the list of the shot on air's source camera (and the previous shot's inside a blend).

```json
"defaults": {"backfaces": ["_Moment Big Railing/CantenaRailing/Platform"]},
"cameras": [{"name": "dir_rail", "backfaces": ["_Moment VentSmash_CULL/Batch_DIFFUSE_Brownish_Baked_4650 box=3420,-843,-7,3433,-835,8"]}]
```

How: while the camera's set is applied, every MeshFilter and SkinnedMeshRenderer under the object shows a two-sided copy of its
mesh (each selected triangle gets a reversed twin on duplicated vertices with negated normals and tangent w, same submesh and
material), built once per renderer and box; every restore (set change, pass end, abort, `cam off`, the next `mcam show`, and
`srec record start`) puts the game's mesh back. Same renderer, materials, layer and property blocks; no objects are added, so
nothing reaches a recording. Only replay passes and `mcam show` apply it; live visits (`mcam start`) and the live game never do.
Limits: a copy above 65000 vertices is refused (narrow it with a box); a skinned renderer whose mesh the record swaps (head
pops) keeps the recorded mesh; a box hide on the same MeshFilter wins. `mcam show` and the run log print
`backfaces N of M triangle(s) of <path>`, `mcam check` lists `cameras[i].backfaces: ... -> <path>`, run.json
`pass_stats[].hide.backfaces` the flipped paths. The checker validates the box.

A closed mesh needs no back faces: from outside it always shows its front. When a closed slab vanishes, the camera is inside it
or within the near clip (1 m) of its face (for example the cafeteria balcony `CantenaRailing/Platform`, a watertight 0.8 m
slab: views.csv `inside` / `path_hit` name its `Floor` collider when a camera crosses it). Move the camera path out of the
slab; back faces would only fill the frame with the slab's inside.

### 6.6 Keeping up with a moving subject (`feedForward`, track check)

Damped tracking (`smooth` of follow, frame, overview and events beats, `lookSmooth` of every look) is a critically damped
SmoothDamp; plain, it lags a steadily moving subject by v x smoothTime (a 10 m/s fall with smooth 0.6 = 6 m behind). The
damper aims at the subject plus its velocity x smoothTime (`feedForward`, default true; `false` per camera or in
`defaults` = plain damping). The velocity is the target's own (recorded character velocity or finite difference), smoothed
over 0.1 s, capped at 30 m/s; a sample above 60 m/s (a teleport or cut) keeps the previous value; a tracker that starts on the
subject also starts with its velocity. Steady lag is then 0 (within 0.2 m of a 10 m/s subject after 1 s; a 2 s free fall
under a quarter of the plain lag); jitter is still filtered. `lead` keeps its meaning and adds on top. `gamelike` keeps the
game camera's own laws.

poses.csv has the columns `subj_x, subj_y, subj_z`: the point each camera tracks this step before damping and limits (empty
when it tracks none; a director row carries its source camera's subject and is empty inside a blend; an events move carries
the next beat's).
`python -I tools/mcam_track_check.py <take>` (also run by `mcam_qa.py qa` on replay takes) projects that subject into every
output as the viewer sees it: layout cells by their crop and aspect (a 853 x 640 cell of a 2560 x 1440 frame shows +-0.75 of
the half-width, the 2560 x 800 band crop [0, 340, 2560, 800] v -0.58..+0.53), pans included, angle videos the full frame.
FAIL when the subject stays beyond 0.8 of the visible half-frame for more than 0.3 output s, WARN beyond 0.65; blend rows are
skipped (a director turning to its next subject), so a director shot that points at another subject is judged on that
subject. Writes `<take>/track_check.txt` and `.json`, exit 1 on a FAIL. Options: `--subject "<tracks.csv target>"` measures
one tracks.csv target instead (e.g. `"huddle .visual"`, also for takes without the `subj_*` columns), `--cams a,b`,
`--fail 0.8`, `--warn 0.65`, `--hold 0.3`.

## 7. Targets

A target is a string, a fixed point `[x, y, z]`, or an object `{"of": base, "point": p, "offset": [x, y, z]}`, or a group.

| Base | Meaning | Points (default first) |
|---|---|---|
| `boy` | the boy | `torso`, `head`, `neck`, `root`, `pos3` (capsule base), `bounds`, `bone:<name>` |
| `huddle` | the huddle | `visual`, `centre`, `top`, `bottom`, `bounds`, `bone:<name>` |
| `main` | huddle when active, else boy | as above |
| `game` / `gameaim` / `probe` | rendered game camera / its aim point / the camera blend probe | none |
| `<path>` | an object: exact path, else a unique `/`-bounded suffix, else a unique name | `pivot`, `bounds` (renderers, `DECAL_*` excluded), `com`, `rb`, `collider`, `top`, `bottom`, `bone:<child>` |
| `<path>[i]` | the i-th same-named sibling, e.g. `_GameLogic/Chickens/Chicken[3]` | as above |
| `<path>::Component` | pick a component for `rb` / `collider` / `com` | as above |
| `#<id>` | instance id (session-local, avoid) | as above |
| `none` / `self` | look only: no look target / the camera's own target | - |

Resolution is strict: an ambiguous or missing path is an error with the candidate list, never a silent guess. `mcam check`
prints `ref -> path #id active pos` for every target. Offline, `mcam_spec_check.py --index` looks the paths up in a level
index (see Section 15).

Groups follow several objects as one subject:

```json
{"group": {"list": "_SaveLogic/ChickensLoaded::ChickenSpawner.chickens", "mode": "bounds",
           "filter": {"minSpeed": 1.5}, "smooth": 0.5}}
```

- Members: `of` (a list of targets), `list` (`"<path>::<Component>.<field>"`, a list field of a component), or `find`
  (an object-database query). `find: "t:Chicken"` matches every component whose type name contains `Chicken`
  (also `ChickenGunLogic`, `ChickenAINode`, `ChickenSpawner`), so prefer `list`.
- `mode`: `centroid`, `weighted` (by speed), `bounds`, `nearest` (to `filter.near`), `fastest`.
- `filter`: `active`, `alive`, `notKinematic`, `minSpeed`, a box (`boxCenter`, `boxSize`), `near` + `maxDist`.
- `hold` keeps members for a while after they stop matching; `max` caps the member count.
- `point` on the group applies to every member and accepts only the object points `pivot`, `bounds`, `com`, `rb`,
  `collider`, `top`, `bottom`; anything else fails in game (`unknown group member point`). For other points (`torso`,
  `visual`, `bone:<name>`) give each member its own `{"of", "point"}` in `of`. The offline checker does not catch this,
  `mcam check` does.

## 8. Events and cues

### 8.1 Cues

Cues run bridge commands at game times, before the step frame of that time:

```json
"cues": [
  [1.0, "call ChickenGunObject/ChickenGunVacuumLogic ChickenGunLogic ToggleSuctionOn"],
  {"at": "@baleLand+0.5", "cmds": ["signal SomeObject someOutput", "fsm SomeObject event go"], "label": "after landing"}
]
```

`call` versus `signal`:
- `call <obj> <Component> <method>` invokes one parameterless method directly. Nothing else in the game reacts unless that
  method triggers it.
- `signal <obj> <output>` fires a SignalOut and every connection wired to it: the game's own chain (other receivers,
  animations, sounds). Prefer it when the scene depends on the chain.
- `mcam probe <obj>` lists an object's outputs, inputs and FSMs.
- ChickenGun: the lever's `left` output is wired to `ChickenGunVacuumLogic.startSuction`, whose handler is
  `ToggleSuctionOn`. So `call ... ToggleSuctionOn` gives the same suction without the lever animation. It is the call
  checked in game.

### 8.2 Events

Events turn game moments into named times:

```json
"events": {
  "baleFall": {"type": "signal", "obj": "HayBaleHolder/HayBaleTrigger", "signal": "enterSignal", "expect": 6.0},
  "baleLand": {"type": "impact", "target": "HayBaleHolder/HayBale", "min": 1.0, "after": "@baleFall", "expect": 6.5}
}
```

| Type | Fires when | Needs |
|---|---|---|
| `time` | game time >= `value` | `value` |
| `signal` | a SignalOut of `obj` named `signal` fires | `obj`, `signal` |
| `signalIn` | a connection into `obj`'s input `signal` fires (FSM inputs start with `>`) | `obj`, `signal` |
| `fsm` | an FSM of `obj` enters / exits `state`, or gets `fsmEvent` (`edge: "event"`) | `obj`, `state` or `fsmEvent` |
| `sound` | a sound event named `sound` is posted (optionally only from `obj` and its children) | `sound` |
| `probe` | an `IsTriggeredByProbe` trigger `obj` is entered / exited | `obj` |
| `impact` | `target` gets a collision impact of at least `min` m/s | `target` |
| `box` | the target point enters / exits a box | `target`, `center`, `size` |
| `cond` | `prop op value` becomes true (`x y z vx vy vz speed`, or `dist` to `to`) | `target`, `prop`, `op` |
| `field` | a component field or property starts to match (`op` + `value`, or `match` for enum names) | `obj`, `comp`, `field` |
| `boystate` / `visual` | the boy's state class / visual node class becomes `match` | `match` |
| `death` / `loaded` / `camcut` | the boy dies / a load finishes / the game camera cuts | - |
| `posted` | a mod or agent posts this name (`SceneRecorder.PostEvent`, `srec event <name>`); `match` listens for another name, a trailing `*` for a prefix (`pop_*`) | - |

Common options: `once` (only the first fire counts, default), `count` (fire on the n-th match), `after` (arm only after a
time or another event), `cooldown`, `log`. The first fire of every event goes to `events.csv`.

### 8.3 Event-relative times and predictions

Any time field takes `"@event+s"`: cues, segment starts, director shots, camera `start`, `until`, and the compositor's edit.
A positive offset simply waits for the event. A negative offset needs to know the time BEFORE the event fires, so it uses a
prediction:

1. `expect` on the event (a guess in game seconds), then
2. the first `event` row of that name in the `rehearsal` folder's events.csv.

When the real event fires at another time, the runner logs a warning and switches to the real time. Workflow: guess with
`expect`, rehearse with the same `seed`, set `rehearsal` to the rehearsal folder, delete `expect` (it wins over the
rehearsal), and capture. The checker warns about every negative offset without a prediction and about an `expect` that
disagrees with the rehearsal. Tie shots that belong to the action to the event (`"@baleFall-1.8"`), not to absolute
seconds, so they move with it.

## 9. Timing, slow motion and motion blur

`timing.fps` is the output frame rate (30). `timing.segments` changes speed and blur over time; a segment starts at its
`from` time (switching at the next output frame boundary):

| Key | Meaning |
|---|---|
| `speed` s | game seconds per output second: 1 real time, 0.5 = 2x slow, 0.25 = 4x slow |
| `samples` M | sub-steps averaged into one output frame (motion blur); 1 = no blur |
| `shutter` S | shutter angle in degrees when M > 1; 180 film-like, 144 matches the game's own blur |
| `rate` | explicit steps per game second; then all M sub-steps of a frame are captured |

Derived per segment: steps per output frame N = round(M x 360 / S) (only the last M are captured), and step rate
R = N x fps / s. R must be a whole number in [10, 960]. Physics stays at 60 Hz (`timing.physics: "game"`), so R should be a
multiple or a divisor of 60; interpolated rigidbodies fill the steps in between (the hay bale and the chicks interpolate).

At fps 30:

| Look | speed | samples | shutter | N | R | captured per frame |
|---|---|---|---|---|---|---|
| real time, no blur | 1 | 1 | - | 1 | 30 | 1 |
| real time, light blur | 1 | 2 | 180 | 4 | 120 | 2 |
| real time, smooth blur | 1 | 4 | 180 | 8 | 240 | 4 |
| real time, game-like blur | 1 | 4 | 144 | 10 | 300 | 4 |
| 2x slow, light blur | 0.5 | 2 | 180 | 4 | 240 | 2 |
| 4x slow, no blur | 0.25 | 1 | - | 1 | 120 | 1 |
| 4x slow, light blur | 0.25 | 2 | 180 | 4 | 480 | 2 |
| 4x slow, smooth blur | 0.25 | 4 | 180 | 8 | 960 | 4 |
| 8x slow, light blur | 0.125 | 2 | 180 | 4 | 960 | 2 |

Use exact speeds (1, 0.5, 0.25, 0.125). A speed like 0.3 gives R = 100.00001 in float maths and is fragile.

Speed ramps: hard speed jumps look abrupt in the cut, so step through 0.5 on the way in and out:

```json
"segments": [
  {"from": 0, "speed": 1, "samples": 4, "shutter": 180, "label": "real"},
  {"from": "@baleFall-1.8", "speed": 0.5, "samples": 2, "shutter": 180, "label": "ramp_in"},
  {"from": "@baleFall-1.0", "speed": 0.25, "samples": 2, "shutter": 180, "label": "slowmo"},
  {"from": "@baleLand+0.8", "speed": 0.5, "samples": 2, "shutter": 180, "label": "ramp_out"},
  {"from": "@baleLand+1.4", "speed": 1, "samples": 4, "shutter": 180, "label": "real"}
]
```

Cost: every step costs one engine frame; every captured step costs (warmup + samples) frames per visited camera; at 1440p
with vsync off a frame takes about 25-40 ms. A JPEG frame is about 1 MB, raw 14.7 MB. The checker prints the full estimate.
A seven-camera ChickenGun take with a speed ramp and 2-4 blur sub-steps is in the order of 100 000 engine frames (40-65 min)
and 10 GB of JPEG; replay mode (Section 18) films the same cameras in a few minutes.

## 10. Render budget

`render` (root, `defaults.render` or per camera):

| Key | Default | Guidance |
|---|---|---|
| `warmup` | 1 | Keep 1. The first render after a pose change refreshes culling and the bloom history. |
| `samples` | 4 | TAA renders averaged per visit: 1 framing previews, 4 drafts, 8 standard, 16 the final wide shot. |
| `taa` | `reset` | `reset` restarts the TAA history at every visit, so nothing of the previous camera survives. `keep` = v1 behaviour. `toggle` = experimental fallback. |
| `jitter` | 0 | 0 picks the jitter pattern from `samples` (Rotated4 / Halton x8 / x16 / x32); explicit ids are 1..15. |
| `motionBlur` | 0 | The game's TAA motion blur during capture; keep 0 and use sub-step blur instead (a negative value keeps the game's 0.8). |

Different budgets per camera are allowed (the wide game camera usually gets 16); the checker notes it because frozen frames
still run some game logic per render.

## 11. Look: shadows, fog, grade

INSIDE scenes are dark. Lift the shadows gently and keep the blacks anchored.

In engine (`post`, per render, restored after each visit):

| Key | Gentle range | Effect |
|---|---|---|
| `brightness` | 0.55-0.60 (game 0.5) | absolute post gamma |
| `blacklift` | 0.006-0.015 (keep the total <= 0.025) | lifts the floor; more looks washed out |
| `postGamma` | 0.03-0.08 | mid-tone lift |
| `inWhiteScale` | 0.92-0.95 | slightly brighter highlights |
| `saturation` | 0 | leave it |
| `fogMatch` | true for close cameras | moves the fog by the camera's distance to its subject, so close cameras get the game's fog depth |
| `noWater` | true for front cameras inside a water volume | stops the underwater look |

Offline (`compose.grade`): a floor-anchored lift per camera (`lift` 0.3-0.5, `k` 4, `floor: "auto"` = the camera's own
black level), optional exposure, and auto exposure only for moving cameras. Give `grade.auto.target` a camera name
(`"game"`): each auto camera then follows that camera's median exposure. A number is a log-mean luma level, and INSIDE scenes
sit at about 15-29, so `30` keeps auto exposure at `maxEv`. `labels.size` is a fraction of the output width (0.016 = 41 px at
2560). Use either a moderate in-engine lift or the offline lift as the main one; stacking two strong lifts washes the picture
out.

## 12. The player-side rule

The sets have no back side. Cameras stay in front of the set (z below about -2, `zMax`) or above it, and never look back
toward the screen (forward z < 0).

- The checker and `mcam check` report static positions behind the line and orientations that look back as errors; key,
  rail and orbit points behind the line and follow offsets that likely end up behind it are warnings.
- During the take, a camera that drifts behind the line is clamped to `zMax` (pose flag C) unless it looks down more than
  45 deg.
- `playerSide: false` turns the rule off for one camera (only for shots that really need it).

## 13. Output folder

```
<out>/
  spec.json        copy of the spec
  run.json         header and summary: segments, cameras, targets, events, game TAA/post values, warnings, errors
  progress.json    live progress (mcam status json)
  log.txt          setup and cue results, warnings
  meta.csv         step,game_time,rate,group,frame,sub,captured,speed,real_time,fixed_steps,segment
  poses.csv        step,camera,x,y,z,pitch,yaw,roll,fov,qx,qy,qz,qw,aim_x,aim_y,aim_z,near,flags,copy_of
  events.csv       step,game_time,frame,kind,name,source,detail
  tracks.csv       targets per step (positions, velocities, bounds)
  rig.csv          the gameplay camera per step (camera, aim, rendered and clean pose, probe, shake offsets)
  <cam>/00000.jpg  captured steps only, numbered by step (output.format "jpg"; "raw" writes .rgba, "pipe" <cam>.mkv)
```

Pose flags: F fallback (no pose, game pose used), C clamped, W waiting, B blending, X cut, T no target, P copy, S shake.
v1 takes have only the JPEG folders (30 steps per second); the compositor reads them with `--rate` and `--group`.

## 14. Compositor and QA

`tools/mcam_compose.py <command> <take> [options]` (H.265 only, no audio unless `--audio record`):

| Command | Output |
|---|---|
| `scan` | validates the take, prints cameras, segments and frames |
| `angles` | one video per camera |
| `layout` | Blender-style layout: the wide band on top, 2-3 cells below, 4 px black gaps |
| `cut` | the edit: holds, dissolves, dips, fades, labels |
| `all` | angles + layouts, plus a cut only from an explicit edit (`compose.edit` or `--edit`; never an autocut) |
| `finals` | only the two deliverables: the director angle (director cameras no layout shows; `--cams` or `compose.finals.cams` name others) and the layouts; grade stats of all their cameras in parallel processes first, `--jobs` (1) encodes at once |
| `director` | turns an edit into a director camera block for the capture spec |
| `autocut` | a draft edit from motion and events |
| `grade` | each camera's grade as `grade_<cam>.cube` (a 1D LUT) plus ffmpeg curves, for an external editor |
| `review` | full-resolution PNG frames of every output (below) |

Where the files go: the take folder is only read. Outputs go to `C:/path/to/InsideDevTool/out/<name>/`, where `<name>` is
the spec's `name` (else the run's name, else the take folder name). `--out DIR` chooses another folder inside
`C:/path/to/InsideDevTool/out/`; a folder outside it is refused unless `--force` is given. `mcam_qa.py` uses the same rule.
Two more guards, both lifted by `--force`: only one heavy encode runs at a time (lock file `out/.encode.lock`), and an encode
is refused with less than 10 GB of free commit memory. A warning is printed when INSIDE.exe is running (both compete for the
CPU and GPU).

Main options: `--compose FILE` (else the take's spec.json `compose`), `--range t0:t1` (game seconds or `@event+s`),
`--cams a,b`, `--layout NAME`, `--edit FILE`, `--codec nvenc|x265`, `--draft` (hevc_nvenc at half size), `--bits 8|10`,
`--threads N` (keep layouts at 8 or below), `--cache-mb 1536`, `--stills N`, `--labels` / `--no-labels`, `--no-grade`,
`--audio none|record`, `--audio-codec aac|flac`, `--cpu`; `python -I tools/mcam_compose.py --help` lists all.

The `compose` section of the spec drives it:

```json
"compose": {
  "layouts": [{"name": "main", "width": 2560, "gap": 4, "rows": [
    [{"cam": "game", "crop": [0, 180, 2560, 1080]}],
    [{"cam": "boy"}, {"cam": "bale"}, {"cam": "outlet"}]
  ]}],
  "edit": {"name": "cut1", "fadeIn": 0.5, "fadeOut": 1.0, "minHold": 4, "maxHold": 6, "shots": [
    {"cam": "game", "from": 0},
    {"cam": "barrel", "from": "@baleFall-1.8", "in": {"type": "dissolve", "dur": 1.0}},
    {"cam": "bale", "from": "@baleFall-0.3", "in": {"type": "dissolve", "dur": 0.8}},
    {"cam": "landing", "from": "@baleLand+0.4", "in": {"type": "dissolve", "dur": 1.2}}
  ]}
}
```

Layout geometry: a one-cell row with `crop [x, y, w, h]` becomes a full-width band (2560x1080 from a 1440p capture); a row
of n cells splits 2560 minus the gaps evenly (3 cells: 851/851/850 x 480, 2 cells: 1278 x 720). The main layout above is
2560x1564. Presets: `16x9_3` (band 956 + 3 cells x 480 = 2560x1440), `16x9_2` (band 716 + 2 cells x 720).

Edit timing: shot times are game seconds or `"@event+s"` (resolved from the take's events.csv); holds are measured in
output seconds, so slow motion stretches them. The checker prints every hold with the predicted event times: aim for 4-6 s
holds and transitions shorter than 40 % of a shot.

Grade time ranges: `grade.default`, `grade.cams.<cam>` and a layout cell's own `grade` take `ranges: [{"from", "to",
"ramp", "ev", "lift", "k", "saturation"}]` (times are game seconds or `"@event+s"`): inside [from, to] the range adds `ev`
and replaces lift / k / saturation, with `ramp` seconds (default 0.5) of smooth ramp outside both ends. A cell `grade`
(any grade key, e.g. `{"lift": 0.9, "ranges": [...]}`) applies to that cell only; `grade.cams.<cam>` applies to the
camera's angle video and every cell that shows it. Example for a dark office span between two events:
`"office": {"lift": 0.6, "ranges": [{"from": "@WallSmash-1", "to": "@pop_ceo+14", "ramp": 1.0, "ev": 0.3, "lift": 0.85}]}`.
The compositor never sharpens: cells shrink with INTER_AREA and the encoder gets the rendered size (hevc_nvenc Main10).

A 16:9 layout with a top band and three cells (2560x1440):
`{"name": "v2", "width": 2560, "gap": 0, "labels": false, "rows": [[{"cam": "overview", "crop": [0, 340, 2560, 800],
"height": 800}], [{"cam": "huddle", "height": 640}, {"cam": "people", "height": 640}, {"cam": "office", "height": 640,
"anchor": [1.0, 0.5]}]]}` gives a 2560x800 band and cells 854/853/853 x 640; `anchor` [1, 0.5] keeps the right part of the
16:9 frame in the 4:3 cell (its right edge is the frame's right edge). Labels are off with `labels.show: false` or a
layout's `labels: false`; `labels.font` picks the font (Section 1.2). A director is its own 2560x1440 angle video
(`angle_director_h265.mp4`).

`review` writes full-resolution PNG frames of every output (angles and layouts; `--outputs angles|layouts|angle_<cam>|
layout_<name>`) into `<out>/review/<output>/`, every `near` s (0.5) within `around` s (1.0) of each review event and every
`every` s (2.0) elsewhere, with `index.csv` (frame, output_t, game_t, why, file) and `review/review.json`. Events come from
`compose.review.events` (names, fnmatch patterns, `@event+s`) or `--events`; `--range` limits the timeline as for the
videos.

Decode and speed: pipe-mode camera videos decode on the GPU by default (NVDEC with `-hwaccel cuda`; one ffmpeg reader per
camera and shrink factor; layout cells shrink by 2 / 4 / 8 on the GPU with `scale_cuda` before the download, prefetching 8
frames). Full-size frames are bit-identical to the CPU decode; shrunk cells differ from the INTER_AREA shrink by about
1/255 on edges (51-52 dB PSNR); the grade floors always come from the full-size decode, so grades, ranges, crops and sizes are
identical. Grading (time-varying LUTs per camera and cell) and the canvas stay in numpy, the RGB -> BT.709 limited 10-bit
conversion stays in zscale (hevc_nvenc's own RGB input converts but tags the stream BT.601), hevc_nvenc encodes. `--cpu` is
the CPU decode path (INTER_AREA). See 14.1 for measured times.

`tools/mcam_qa.py <command> <take>`: `qa` (checks with exit codes: missing frames, black (luma p50 < 2) and near-black
(more than `--dark-cover` 0.5 of a camera frame below `--dark-level` 3/255) frames as errors with game-time runs; every
composed video in compose_log.json per layout cell (`--videos DIR`, `--no-outputs`): black, near-black (more than
`--cell-dark-cover` 0.30 of the cell) as errors and blur (the cell's Laplacian variance against the 75th percentile of its
neighbours within +-1.5 s, 3 frames each side left out: below `--blur-warn` 0.35 a warning, below `--blur-fail` 0.2 an error;
black and near-black frames are not judged; `--no-blur`) with output-time runs; frozen steps, fallback and clamped poses,
player-side violations, jerk, holds, late loads; replay takes also views.csv (cameras inside geometry and camera paths
through geometry as warnings with game times) and the track check of 6.6), `sheet` (contact sheets; `--layouts`,
`--storyboard`, `--ab`), `sync` (cues and events per camera), `paths` (top and side plots of the camera paths with the
z = -2 line), `timeline`, `diverge <take> <other>` (rehearsal vs capture tracks), `replaydiff` and `audiosync` (Section 18).
Exit codes of `qa`: 0 ok, 1 errors, 2 unreadable take.

### 14.1 Measured compose speed

One replay take with five 2560x1440 angles and a 2560x1440 layout, 1473 output frames each, `--threads 6 --cache-mb 1024`,
on an RTX 3070 Laptop GPU with 8 logical CPUs:

| Output | `--cpu` | GPU path (default) | Note |
|---|---|---|---|
| each angle | 70-75 s (20-21 fps) | 64-65 s (23 fps) | byte-identical files; bound by zscale + hevc_nvenc |
| layout | 188 s (7.9 fps) | 96 s (15.6 fps) | cells 48-50 dB PSNR against the CPU path after the encode |
| `all` | 550 s | 420 s | |
| `finals` (director angle + layout) | | 145 s | one after the other; both at once was slower, hence `--jobs 1` |

What limits: the layout decodes four 1440p HEVC 10-bit streams per frame on NVDEC; the angles are bound by the CPU
RGB -> YUV conversion (zscale), so two angles at once gained little and they stay sequential. Grade stats of a fresh take
(no `out/_stats` cache) run one process per camera (about 110 s for five cameras).

### 14.2 Other tools

| Tool | Use |
|---|---|
| `tools/fast_review.py <take> [--videos DIR] [--out DIR] [--every 2] [--events a,b] [--around 0] [--near 0.5] [--range t0:t1] [--threads 8] [--cpu] [--no-blur]` | Review of the finals: full-resolution PNG frames at every review event and every `--every` s, the composed-video QA (black, near-black per cell, blur) and contact sheets, in one folder (default `<videos>/fast_review`). `--videos` is the folder with the finals' compose_log.json (default the take folder, so usually pass the compose output folder). Exit 0 ok, 1 QA errors, 2 nothing to review. |
| `tools/mcam_previews.py <spec> <record_dir> <out_dir> [--beats <scene.json>] [--times t,@ev+s,...] [--cams a,b] [--settle 0.5] [--url http://127.0.0.1:47811]` | Stills of a recorded take with the running game: `srec load` of the record folder (unless already loaded), `mcam check`, then per time `srec seek` and per camera `mcam show <cam> <t>` + screenshot; full-resolution PNGs plus contact sheets per camera and per beat. Needs `InsideDev.Mcp.dll` (`POST /cmd`). Ends the viewer and runs `cam off` only when it loaded the viewer itself. Exit 0 ok, 1 some stills failed, 2 not started. |
| `tools/mcam_track_check.py <take>` | Subject tracking check (6.6). |
| `tools/mcam_audio.py fix\|info\|align\|track\|stretch\|check` | Record-pass sound (18.7). |
| `tools/srec_tool.py info\|verify\|objects\|export\|dump\|synth\|report` | Recordings (`scene.srec`), see [RECORD_REPLAY.md](RECORD_REPLAY.md). |

## 15. The spec checker

```
python -I tools/mcam_spec_check.py <spec.json>... [options]
```

| Option | Effect |
|---|---|
| (none) | messages, segment table, events with predictions, cameras with visits and renders, cost and disk estimate, layout sizes, cut holds |
| `--quiet` | summary, errors and warnings only |
| `--json` | machine-readable result (messages with stage, all tables) |
| `--index` | look every object path up in a static level index: a folder with one JSON-lines file per level file (the format `tools/atlas/make_atlas.py --index` reads; not shipped). The folder comes from `--index-dir` or the environment variable `INSIDE_LEVEL_INDEX` (no default); `--levels chickenGun` limits the files |
| `--rehearsal DIR` | use that rehearsal folder's events.csv for predictions |
| `--poses DIR` | a shot take (poses.csv, meta.csv, events.csv): every camera's peak speed and turn rate per output second against its motion limits, every director blend and events move of the take against its time window (6.3) |
| `--ms LO:HI` | engine milliseconds per frame for the time estimate (default 25:40) |
| `--replay` | apply the replay lint also to a spec without a `replay` block (like `mcam check <spec> replay`) |
| `--v2` | v2 lint only, even for a spec with a `replay` block |
| `--schema-md` | print the schema tables (Section 19); differences with the design docs go to stderr |
| `--update-doc FILE` | replace the schema block of a Markdown file |
| `--bind-only` | only the binder messages (for the C# cross-check) |
| `--types FILE...` | other schema sources (default `MultiCamTypes.cs` and `ReplayTypes.cs`; several files, or the option repeated) |
| `--design FILE...` | other design docs for the `--schema-md` meanings (default `MULTICAM_DESIGN.md` and `REPLAY_DESIGN.md`) |

Exit codes: 0 no errors, 1 errors, 2 unreadable input (missing file, invalid JSON, schema not parsable).

What it checks:
- Binding exactly like the game: the schema is read from `src/InsideDev/Runtime/MultiCamTypes.cs` and `ReplayTypes.cs` (one
  field per JSON key, defaults from the initializers), with the same unknown-key warnings, type errors and alternative forms.
- The v1 mapping (rates, settle, jpeg, legacy point).
- Camera names, modes, keys a mode ignores, required fields per mode, keyframe order, director references and cycles,
  director holds, the player-side rule, angle and FOV ranges, near clip, render budgets, `motion` values, shot `hide` and
  `motionBlur`, director blends between static cameras that cannot meet the motion limits in their window (6.3).
- Events: names, types, required fields per type, unknown `@` references, `after` cycles, negative offsets without a
  prediction, events never used.
- Timing: whole step rates in [10, 960], physics cadence, shutter, segment order.
- Setup and cues: command names (read from `Remote.cs` / `DevRoot.cs`; extension commands are not checked), `tp` with three
  numbers, call/signal/fsm/load arguments, setup-only primitives, `load` without a following `waitfor`, instance ids.
- Compose: cameras in layouts and the edit (disabled cameras have no frames), crops, presets, transitions, holds, encoder
  (H.265 only).
- Replay specs (a `replay` block, or `--replay`): the replay lint of Section 18.10, the replay timeline instead of step rates,
  and a replay estimate (record pass, renders per camera pass, disk).

What it cannot check offline: whether a path resolves in the running game, whether an event fires, how a follow or director
camera moves. `mcam check`, `mcam preview` and `mcam rehearse` cover those.

## 16. Troubleshooting

### 16.1 Known pitfalls

- **A minimized game window pauses the game.** Unity stops: nothing renders, bridge and MCP commands time out, and a run or
  a tool such as `mcam_previews.py` stalls until the window is restored (`<game>\_mod\insidedev.log` shows
  `application paused`). The process still reports itself as responding. Leave the window open; it may sit behind other
  windows. Focus loss is a different case: keep `session.runInBackground` true (default) and do not click into the game
  window during a take.
- **Load races.** `load` is asynchronous, and the game may still be on the level it booted into. A setup that starts the take
  right after `load`, or after `waitfor loaded` alone, can start on the old level or mid-load. Follow every `load` with
  `waitfor savepoint <the requested savepoint>` and `waitfor streaming` (the checker warns `runs right after 'load'`). When
  you drive the game yourself (MCP, `srec record`), wait until `game_state` reports the requested savepoint before you start.
- **MCP idle restore.** The MCP server puts back what MCP tools changed after 600 s without any tool call (EditorState
  `mcp.idle_restore_s`, 0 = never; also when the last MCP session closes, after `mcp.restore_on_close_s`, default 60 s): it
  releases held agent input, stops an input track recording or playback, ends step mode (real time, speed 1) and undoes
  god / hazard / stream pin / render / audio changes made through MCP. It never cancels an mcam run, but a take that you drive
  through MCP (a manual `srec record` plus play tools) loses its input when no call arrives for 10 minutes. MCP tool calls
  and `POST /cmd` count as calls (`mcam status` is enough); file-bridge commands do not. See [MCP.md](MCP.md) section 9.
- **Step mode and frozen time.** The MCP play tools put the game into step mode with the first call that runs it: frozen
  between calls ([PLAY.md](PLAY.md) section 4.1). An mcam run owns the clock while its session is active: the stepper hands
  off and applies its mode again afterwards. A manual `srec record` does not take the clock: in step mode the game moves
  only inside action calls, so choose real time first (`time {op:"realtime"}`) when the recording must run in real time.
  After a replay run the game stays frozen until `ts 1` (Section 1.1); during a v2 run frozen frames still run some Update
  logic per render (Section 10).
- **A recording blocks runs.** Every run is refused while the srec viewer or a replay is loaded (`srec end` first) or a manual
  recording runs (`srec record stop` first).
- **A run that died** leaves its session: `mcam status` says `[session still active: run mcam abort]`; run `mcam abort`.
- **Relative paths** in `mcam` commands resolve against the game's working folder; use full paths.

### 16.2 Problems and fixes

| Problem | Cause and fix |
|---|---|
| Editor overlays or labels in the frames | `session.clean` must stay true; the setup's `panel off`, `triggers off`, `labels off`, `savepoints off` also help. |
| The take pauses | Focus loss or the pause menu: the runner holds and logs it. Keep `session.runInBackground` true and do not click into the game window. A minimized window stops the game (16.1). |
| `busy: mcam capturing` | A run holds the capture lock (Section 1.1): wait (`mcam status`), or `mcam stop` / `mcam abort`. |
| Objects pop in during a shot | A subscene loaded late: see `late load` marks in events.csv; keep `stream.keepLoaded: "auto"` or add the culler to `stream.cullers`. |
| A ghost of the previous camera | TAA history: keep `render.taa: "reset"`, `warmup` 1, `samples` >= 4. If it stays, try `taa: "toggle"`. |
| A camera shows the game view (flag F) | Its target did not resolve: run `mcam check` and fix the path (strict resolution, see Section 7). |
| An event never fires | Check `mcam probe <obj>`, the event's `obj` / `signal` names, and events.csv of a rehearsal. Shots and segments waiting for it never start. |
| Slow motion starts too late | A negative offset without a prediction: add `expect` or `rehearsal`. |
| `step rate R is not whole` | Pick speed / samples / shutter from the table in Section 9. |
| Stutter in slow motion | A rigidbody without interpolation: add it to `interpolate`; check tracks.csv. |
| A camera jumps to z -2 (flag C) | It went behind the player-side line and was clamped; move it forward. |
| Washed-out picture | Too much lift: lower `blacklift` and `postGamma`, or switch the offline lift off. |
| The game is audible during a take | Expected: MultiCam never mutes the game. Add the optional setup lines of Section 4.4. |
| The game stays frozen after a take | Replay runs hold time scale 0 at the last sample: `ts 1`. |
| `output.ffmpeg: not found` | ffmpeg is not on the game's PATH: put its folder on PATH before starting the game, or give the full path in `output.ffmpeg`. |
| `output ... is outside ...; pass --force` | The compositor and QA write under `out/` of the repository (Section 14): choose a folder there or add `--force`. |
| White or black smear in a fast camera move | The game's TAA blur follows the camera motion: keep the motion limits (6.3), check the take with `mcam_spec_check.py <spec> --poses <take>`, lower a shot's `motionBlur`; `mcam_qa.py qa` lists blurred frames per cell. |
| A prop blocks a replay camera (lamp pole, holdout slab) | `hide` on the camera or on the director shot (6.4); check it with `mcam show` (the `hide:` line) before the pass. |
| A director or events camera arrives late | Its blend / move was stretched by the motion limits (log line "stretched"): start it earlier, move less, or raise `motion` for that camera. |

## 17. Example specs

| Spec | What it is |
|---|---|
| `specs/template_minimal.json` | v2 capture: game camera + one static angle where the game already is, 5 s, a side-by-side layout; copy it to start |
| `specs/replay_smoke.json` | replay mode: 10 s of ChickenGun with suction on, game camera + outlet static, speed 1, engine blur, no audio, golden grabs, `mcam take` |

Checked ChickenGun poses (x y z yaw pitch fov): low_left 215.5 -220.3 -6.5 30 -12 50; outlet_bale 225 -219 -6 20 -18 45;
high 222 -213 -11 10 25 50; close_machine 219.5 -220.6 -4 18 -5 40; the game camera is about (220.2, -220.5, -13.4) yaw 15
fov 51. The ChickenGun setup of `replay_smoke.json` (`load 7 0`, `waitfor savepoint #chickenGun`, `tp 221.0 -221.4 0.66`,
`OnElectricityOn`, `DoSpawn`, the `ToggleSuctionOn` cue) also works for v2 captures.

## 18. Replay mode

Replay mode records one real-time run of a scene, then freezes the game and films every camera in its own continuous pass
while the mod drives the scene from the recording. The design is [REPLAY_DESIGN.md](REPLAY_DESIGN.md) (its Section 12 holds
the lab results). It reuses everything else of v2: the spec format, rigs, targets, events, the look settings, the take
folder, the compositor and QA. Recordings, the viewer and manual records are described in
[RECORD_REPLAY.md](RECORD_REPLAY.md).

### 18.1 How it works

1. **Record pass.** The scene runs once in real time (timeScale 1, about 60 rendered frames per second) with the game camera,
   exactly as in normal play: setup, seed, preroll, cues and events as in a v2 take. At every rendered frame the recorder stores
   what moves: transforms, the boy's and the huddle's bones, active and renderer flags, Shuriken particles, lights, material
   values, blend probes, fog blend values and the full game camera state. The recording goes to `<out>/record/`.
2. **Replay passes.** The game is frozen (timeScale 0), streaming is frozen and no bounds culler switches objects on or off.
   For each camera, one continuous pass renders one frame per output step while the mod writes the recorded state for replay
   time T into the scene. Every pass starts from the same saved state. Per-camera culling is emulated by hiding renderers and
   lights (measured exact in the lab), so every camera sees what the game would show from its pose.

What you get compared with a v2 capture (`mcam start`):
- Every camera shows the same moment by construction, and the window never flickers between angles (one camera per pass).
- Any float speed works for slow motion: the replay interpolates between recorded samples (Hermite for positions, slerp for
  rotations). There is no whole step rate.
- The engine's own motion blur comes from the real motion between renders (frozen blur measured equal to running blur).
- New cameras can be shot later from the same recording (re-shoot, Section 18.2).
- Cost: about one render per output step and camera, 40-60 ms at 1440p. Seven ChickenGun cameras need a few minutes of
  replay passes plus the record pass, where the v2 capture of the same cameras needs 40-65 min.
- Sound comes from the record pass only, so it is in sync with every camera (Sections 18.6 and 18.7).

When to use which:

| Scene | Use | Why |
|---|---|---|
| Physics and AI with many movers (ChickenGun, crowds, ropes), boy and huddle traversal, music-locked gameplay | replay | a recording does not care that runs never repeat |
| Shots that depend on `_Time`-animated shaders (water surfaces, rain lines, ripples) or water render textures, Hair, plain LineRenderer effects | v2 `mcam start` | the replay cannot drive them: they stay frozen (Section 18.8) |
| Framing and stills | `mcam preview`, or the recording viewer `srec play` | no recording needed |

### 18.2 Workflow

All `mcam` commands take full paths (Section 1.1). `specs/replay_smoke.json` follows this recipe in one session with
`mcam take`.

1. Offline check: `python -I tools/mcam_spec_check.py <spec>`.
2. In game: `mcam check <spec>` (a spec with a `replay` block gets the replay lint automatically; `mcam check <spec> replay`
   forces it for any spec).
3. Record pass: `mcam record <spec> <take>`. It ends frozen at the last sample: Time.timeScale 0 is held every frame
   (`mcam status` shows `[game frozen ...]`) until `ts <value>`, a level load or the next mcam run; the same holds after
   `mcam replay`, `take`, `golden` and `passes`.
4. First time in a scene, the golden test, on its own record: a copy of the spec with `record.captureGame: true` (every 10th
   sample of the game camera into `record/game/`; `replay_smoke.json` has it on). With the readback plugin (Section 1.2) the
   grabs go through the native async readback and an ffmpeg JPEG pipe (record.json `game_frames_encoder`); without it each
   grab stalls the record about 34 ms (ReadPixels + JPEG on the main thread). Either way that record is a golden reference,
   not a take (record.json `pacing.grade` golden); `mcam golden` grabs through the same encoder. `mcam record <golden spec>
   <golden take>`, then `mcam golden <golden spec> <golden take> [every]` replays the game camera at the record resolution
   with the recorded culling, rendering every sample and grabbing every 10th into `<golden take>/golden/` (also
   `golden/noise/`, renders of one frozen frame for the noise floor); then
   `python -I tools/mcam_qa.py replaydiff <golden take> --record <golden take>/record` compares it with the grabbed game
   frames (output `<golden take>/replaydiff/` unless `--out`). The edge silhouette is judged for rope targets only
   (`--sil-targets`, default `rope`) and reported for the rest: Canny edges of dark masks are TAA grain. A pass means the
   recording drives the scene faithfully.
5. Plan: `mcam passes <spec> <take>` prints the event times from the recording, the segment table and, per camera, renders,
   copies, estimated seconds and disk. No frames.
6. Replay passes: `mcam replay <spec> <take>`; `cams=game,bale` and `range=t0:t1` (record seconds) limit a draft.
   `mcam take <spec> <take>` does the record pass and the replay passes in one session.
7. Videos: `python -I tools/mcam_compose.py all <take>`: one video per camera (directors included) and the layouts,
   hevc_nvenc Main10 (`--codec x265` only on request: libx265 slow is many times slower). `all` makes a cut only from an
   explicit `compose.edit` and never drafts an autocut (`mcam_compose.py cut` still does when asked); transitions can be
   director camera moves in the game instead.
8. QA: `python -I tools/mcam_qa.py qa <take>`.

Re-shoot with new cameras later: a new spec with `"replay": {"source": "<take>/record"}` (a relative source resolves against
the spec's folder) and the new cameras, then `mcam replay <spec2> <take2>`. In a fresh game session the replay first runs
`replay.play.setup` (default: the record spec's setup without its cues), resolves every recorded object by path and refuses
when more than `play.maxUnresolved` (1 %) of the moving objects are missing; `replay_baseline.tsv` in the run's output folder
lists what differs from sample 0. Before the first pass (replay and golden alike) it waits until every subscene the record
kept loaded is loaded and active again. A recording made earlier in this game session with no level load since (any
`mcam record` or `srec record`, not only the last one) replays in the same session. Objects that only came to exist during
the record session (for example effects a mod spawned) can push a fresh session over the `maxUnresolved` limit, so run the
camera re-passes of such a record before you quit the game.

Takes played through MCP (an agent drives the game, so no mcam record session may hold it) use a manual record and a
same-session replay:

1. `srec record start <take>/record spec=<take spec> keep=#<subscene>,#<subscene> audio=on` (`audio=on` only for a sound
   take, 18.7). `spec=` makes `replay.record` of the take spec the defaults (roots such as `["auto", "zone:<zone>"]`) and
   arms its `events`, so `@<event>` exists in the recording. `keep=` pins subscenes and starts the recording once they are
   loaded and active (`srec record status` shows the wait); they are recorded from sample 0 and kept loaded by every replay.
   Subscenes of `subscene:` / `zone:` roots that load later, and `auto` zone subscenes that activate later, join the
   recording in that frame (logged with their cost in record.json `subsceneJoins`).
2. Drive the scene in real time. A mod marks moments with `SceneRecorder.PostEvent("<name>", "<detail>")` (by reflection
   from a mod built against an older core), or the agent sends `srec event <name>`; both work during mcam runs too.
   Objects a mod spawns under `_Dynamics`, or instantiates under a recorded object (an effect under a character's bone), are
   recorded from the frame they appear; anything else needs `SceneRecorder.Register(obj)`. Hide objects, never Destroy
   them: a destroyed object cannot be replayed. A swap of `SkinnedMeshRenderer.sharedMesh` is recorded and replayed
   (record.json `meshSwaps`); toggling two renderers works too.
3. `srec record stop`, then without a level load `mcam replay <take spec> <take>` with `replay.source` set to
   `<take>/record`. Declare the posted names as spec events of type `posted` (`"<name>": {"type": "posted"}`) to use
   `@<name>` in director shots and segments; replay run.json `events` also lists posted names that the spec does not
   declare.

The pitfalls of Section 16.1 matter most here: the MCP idle restore, step mode and load races.

### 18.3 What the v2 fields mean in a replay spec

| Field | Record pass | Replay passes |
|---|---|---|
| `setup`, `seed`, `preroll` | as v2 (the preroll is not recorded) | `replay.play.setup` only, in a fresh session |
| `cues` | run at their times (they drive the gameplay) | ignored |
| `events` | armed and logged as v2; fire times stored | times read from the recording: `expect` and `rehearsal` are not used (the offline estimate still uses `expect`) |
| `seconds`, `until` | end of the recording | duration = the recording, or `replay.play.range` |
| `timing.fps`, `segments` (`from`, `speed`, `samples`, `shutter`, `label`) | not used | the output timeline; any float speed; `samples` > 1 = sub-frames averaged by the compositor |
| segment `rate`, legacy `rates`, `settle`, `timing.physics: "fine"` | error | error |
| `render` | not used | with `look: "game"` only `render.motionBlur` < 0 (keep the game value) is read |
| `session` | as v2 but `replay.record.res` is the resolution and `vsync` is ignored (the record pass sets 60 Hz pacing) | as v2 (`res` = output resolution) |
| `stream` | keep-loaded prepass with the planned poses; the handles stay for the replay | prepass again, then streaming frozen until the end |
| `interpolate` | as v2 | ignored |
| `post`, `defaults`, `cameras`, `tracks`, `output`, `compose` | cameras only plan the keep-loaded set | as v2 |

The `replay` block (all keys in Section 19):

```json
"replay": {
  "record": {"clock": "real", "res": [0, 0], "forceCullers": "zone", "captureGame": false},
  "play": {"look": "game", "motionBlur": "game", "warmup": 16, "ramp": 0.4, "cullers": "camera"},
  "audio": {"capture": "none"}
}
```

- `record.clock`: `real` (default) samples once per rendered frame at 60 Hz pacing; `fixed` with `rate` 60, 120 or 240 gives
  regular samples for silent takes and fast objects. Pacing: on a display that reports 60 Hz the record tries vsync and
  checks the frame interval during the preroll; when vsync does not hold 16.7 ms (a 75 Hz monitor, a hybrid-GPU laptop) and
  on every other display it uses its own frame limiter (vsync off, a fixed 1/60 s grid, `timeBeginPeriod(1)`). Cue commands
  are bound before sample 0, the mod's background scans pause and a GC runs before the record, so no recorded frame stalls;
  record.json `pacing` and `drift` report what happened. `record.res` keeps the record pass below 16.7 ms per frame; the record
  view is never part of the output (except the golden test). `forceCullers: "zone"` keeps every culler group of the zone
  active during the record, so objects the game camera cannot see still animate.
- `play.look`: `game` = one render per step with TAA and the engine blur. `clean` (v2 render budget per step) and
  `play.order: "step"` are not built yet; the runner falls back to `game` / `camera` with a warning.
- `play.motionBlur: "game"` keeps the exposure of play at 60 fps: strength = 0.8 x (1/60) / (record seconds per render). At
  30 fps that is 0.4 at speed 1 and 2.0 at speed 0.2; above 2 the shader clamp is unknown (warning). `"angle"` keeps a
  constant shutter (`shutterAngle`, 288 = 0.8) in output time; a number is a fixed strength; `"off"`.
- `play.warmup`: renders before the first captured step of each pass, along the shot's own poses at the preceding times,
  with a TAA reset on the first one, so nothing of the previous camera survives. A director run after a copied hold gets the
  same warmup.
- `play.ramp`: output seconds over which speed changes between segments blend (0 = step).
- `play.cullers`: `camera` (default, the exact per-camera emulation), `recorded` (the game camera's culling, for the golden
  test), `force` (everything visible; it creates no back side), `live` (diagnostic, contaminates passes).
  `camera` keeps a culler that sits on a Light (the game's `*_BoundsCuller` lights: their light, light-pass volumes and
  LightVolumeFog) on whenever the recording had it on: forced by `record.forceCullers` (header `forcedCullers`) or
  camera-active in the record. Only geometry groups follow the pass camera's frustum and facing planes, so a replay camera
  on the other side of a light culler's facing plane does not switch the light off. The log line reads
  `cullers: N culler(s) on a Light stay on ...`.
- `play.cams`, `play.range`: a subset of cameras, a record-time range.
- `play.restore`: after the last pass the game stays frozen at the final sample (held until `ts <value>`) with the game
  camera and its CameraScript state at the recorded final pose; body velocities are the record's final ones when the game
  has not run since the record, else zero; `reload` loads the savepoint again. Use `reload` before playing on (sleep states
  and FSM timers are not restored).

### 18.4 Commands

| Command | Result |
|---|---|
| `mcam record <spec> <out>` | Record pass only. Writes `<out>/record/`. |
| `mcam replay <spec> <out> [cams=a,b] [range=t0:t1]` | Load the recording (`replay.source` or `<out>/record`), plan, one pass per camera (directors after their sources). Writes the v2 take layout in `<out>`. |
| `mcam take <spec> <out> [cams=a,b] [range=t0:t1]` | Record pass and replay passes in one session (no reload between them). |
| `mcam passes <spec> <out> [cams=a,b] [range=t0:t1]` | Plan only: event times from the recording, segments, renders, copies, time and disk per camera. |
| `mcam golden <spec> <out> [every]` | Golden test (Section 18.2 step 4); also writes `golden/noise/` (renders of one frozen frame, for the noise floor). |
| `mcam status [json]` / `mcam stop` / `mcam abort` | As v2; states `recording` (t, samples, objects, MB, fps, drift) and `replay pass i/n <camera> step j/m eta`. Abort also stops a sound capture first. |
| `srec load <dir>` / `srec play [speed] [loop]` / `srec pause` / `srec seek <t>` / `srec step <dt>` / `srec end` | Recording viewer: the scene follows the recording while you fly the free camera (no capture). |
| `srec status` / `srec objects [filter]` | State; object table with key counts, resolution and conflicts. |
| `srec record start <dir> [roots=a,b] [keep=a,b] [spec=<file>] [clock=real\|fixed:<rate>] [audio=on] [overwrite=on] [<record key>=value ...]` / `srec record stop [keep]` / `srec record status` | Manual recording; `keep=` waits (up to 30 s) until those subscenes are loaded and active, `spec=` takes the spec's `replay.record` as defaults and arms its `events`. |
| `srec event <name> [detail]` | Timed event in the active recording (manual or mcam); `@name` resolves to its first post. |
| `acap preflight` / `acap status` / `acap output [audible\|muted]` | Sound capture: "ok" or the refusal reason; capture state; the capture output (18.6). |

The full `srec` and `acap` references are in [RECORD_REPLAY.md](RECORD_REPLAY.md) and [AUDIO.md](AUDIO.md). During record and replay runs the capture
lock of Section 1.1 applies (`session.lockInput`).

### 18.5 Output

```
<out>/
  record/              the recording, reusable by later replays
    scene.srec         binary recording
    objects.tsv        object table (id, kind, path, owner, components, keys)
    record_meta.csv    one row per sample (frame, game time, real time, logic step, keys, bytes)
    events.csv rig.csv tracks.csv    v2 schemas on record time
    game/<sample>.jpg  record.captureGame frames
    record.json        header, counts, pacing, runner phase, holds, warnings
    record_report.json / .txt   tools/srec_tool.py report: events, spec events, record.checks per sample
    audio.wav audio_sync.csv audio_capture.json   only with audio.capture "record"
  spec.json run.json progress.json log.txt meta.csv poses.csv events.csv tracks.csv rig.csv   v2 schemas
  views.csv            view checks of every enabled camera every view.every s (6.2); summary in run.json views
  <cam>/<step>.jpg     replay frames (or <cam>.mkv with output.format "pipe")
  golden/<sample>.jpg  mcam golden
```

`meta.csv` keeps the v2 columns: `game_time` is the replay time T on record seconds and `real_time` the recorded wall clock
(the audio clock). `run.json` adds `recording` (samples, objects, unresolved, conflicts, activations, monitor hits) and
`passes[]` (camera, start and end state hash, RNG fingerprints, renders, warmups, TAA resets). A pass whose start hash differs
from the first pass is flagged.

### 18.6 Sound

Sound reaches a take only through the record pass: with `replay.audio.capture: "record"` the record pass captures the game's
Wwise output (AkSoundEngine.StartOutputCapture) into `record/audio.wav` with markers (`audio_sync.csv`,
`audio_capture.json`). The default is `"none"`: no WAV. Replay passes run with the game frozen and record no sound.

How a capture behaves depends on the capture output, `acap output audible|muted` (EditorState `acap.output` in
`<game>\_mod\editor.cfg`, default `audible`):

| Output | Behaviour |
|---|---|
| `audible` (default) | The game sound plays normally during the record pass. The capture window opens at once at min(`audio.level`, game volume), so the master volume is never raised above the game's own setting. No InsideMuter, heartbeat or flag files; `suspend`, `allowMuterOnly`, `heartbeat` and `maxHeartbeatAge` are not used. |
| `muted` (opt-in) | For a machine where INSIDE must stay inaudible. From the first `acap` command (or `srec record ... audio=on`, or a run of a spec with a `replay` block) until `acap output audible` or the game exits, the master volume is held at 0 outside capture windows. `tools\muter\InsideMuter.exe` ([LAB.md](LAB.md)) mutes INSIDE's Windows audio sessions and writes a heartbeat (`replay.audio.heartbeat`, default `_mod/muter/heartbeat.json` relative to the game folder; `maxHeartbeatAge` 2 s). The 0 is read back from Wwise, and a stale heartbeat or an unmuted session closes a window. A capture also needs a capture path flag: `<game>\_mod\acap_suspend_ok.json` (suspended capture, `suspend: "auto"`) or `allowMuterOnly: true` plus `<game>\_mod\acap_muteronly_ok.json`; only a passing lab stage writes these files ([LAB.md](LAB.md)). Suspended capture rendered at 0.919x real time in the lab, outside the 0.5 % sync limit. |

`acap preflight` answers "ok" or why a capture would be refused, for the current output; run it before a sound take.
Details of both outputs, `acap start` for manual captures and the remaining `acap` commands: [AUDIO.md](AUDIO.md).

### 18.7 Sound takes

1. Spec: `"audio": {"capture": "record"}` (keep `suspend` at its default `auto`; `level` 100 means the game's own volume
   with the audible output). `audio.onHold` (`abort`, default) aborts the take when the game pauses or loads during the
   capture; `mark` continues and writes the gap to `audio_sync.csv` for `mcam_audio.py track` to cut. With
   `record.clock: "fixed"` only rate 60 is accepted.
2. `acap preflight`, then `mcam record <spec> <take>` or `mcam take <spec> <take>` (or a manual
   `srec record start ... audio=on`).
3. `python -I tools/mcam_audio.py align <take>/record`: fits the WAV markers to the record clock (lag-0 fit, slope check)
   and writes `record.json` `audio`; audio that runs more than 0.5 % off is marked not usable.
4. `python -I tools/mcam_audio.py track <take>`: `audio_track.wav` on the output timeline (speed-1 pieces copied, slow-motion
   parts time-stretched with pitch kept, `--slow mute` silences them instead; wall-time gaps cut).
5. `python -I tools/mcam_compose.py all <take> --audio record`: muxes it (AAC-LC 320 kb/s in the MP4, or FLAC in MKV with
   `--audio-codec flac`; the H.265 video is unchanged). Every camera gets the same sound bed. Muxing is refused unless
   `record.json` `audio.usable` is true. `python -I tools/mcam_qa.py audiosync <take>` checks the sync numerically.

`mcam_audio.py` also has `fix` (repair RIFF / data sizes of a WAV), `info`, `stretch <src> <dst> <factor>` and `check`
(levels, onsets after event markers). None of the tools plays audio. Exit codes: 0 ok, 1 check failed or audio not usable,
2 usage or input error, 3 refused.

### 18.8 Limits

- **Frozen `_Time` effects.** Unity's built-in `_Time` follows scaled time, freezes at timeScale 0 and cannot be overridden
  (the engine sets it per camera; a global override changed 0 px in the lab). Shaders animated by it stay frozen at the
  take end in every replay camera. ChickenGun: rain lines, mud ripples, splash edges and the grain chute (moths partly). Lake:
  rain lines and the water surface (refraction, reflection, fog, edge). A shot that needs them is a v2 shot. The old key
  `replay.play.unityTime` is removed and is a lint error.
- Also not driven: water render-texture simulation, Hair point simulation, plain LineRenderer geometry, procedural meshes.
  Rope renderers (the crane rope) are replayed (within 2 px).
- Camera-driven objects stay live per camera: lens flares, CameraLightSwap lights, the water edge (WaterEdge), the audio camera
  probe.
- Particles: emission pops at sample times (one per sample interval); systems only a side camera saw may be stale.
- Objects faster than about 90 deg or 0.75 m per sample need `record.clock: "fixed"` with rate 120 or 240 (silent takes).
- Static shadow caches with moving casters are not tested yet; `play.shadows` stays `recorded`.
- Frozen frames are not perfectly still at bright edges over many renders; the golden test measures that noise floor.
- The player-side rule gets stricter for replay cameras: on top of Section 12, every static, key, rail or orbit orientation
  more than 65 deg from +z is a warning, unless it looks down more than 60 deg and the camera is listed in
  `replay.play.coverageOk` after a still has shown that the floor is there.

### 18.9 Troubleshooting

| Problem | Cause and fix |
|---|---|
| `replay.play.unityTime: removed in revision 3` | Delete the key: `_Time` cannot be set (Section 18.8). |
| `not allowed in a replay spec: give speed instead` | Segment `rate`, legacy `rates` and `settle` are v2 only; use `speed` (any float). |
| `audio capture refused by preflight: ...` | `acap preflight` prints the same reason. With the muted output a capture needs InsideMuter, a fresh heartbeat and a capture path flag (18.6). |
| Water, rain or ripples do not move | `_Time` effects are frozen in replay (Section 18.8); film that shot with `mcam start`. |
| The replay refuses: too many unresolved objects | A fresh session differs from the record: check `replay_baseline.tsv`, fix `replay.play.setup`, or record again in this session. |
| A camera shows the side of the set | 65 deg warning: turn it into the set, or verify a top-down view with a still and list it in `coverageOk`. |
| The game runs on after a replay | It should not: replay modes hold Time.timeScale 0 at the final sample until `ts <value>`; check `mcam status`. |

### 18.10 Replay lint

`mcam check` and the offline checker apply it to every spec with a `replay` block (`--replay` / `mcam check <spec> replay`
force it). The v2 whole-step-rate rules are off for these specs.

- Errors: unknown values of `record.clock`, `record.particles`, `record.forceCullers`, `play.look`, `play.order`,
  `play.interp`, `play.particles`, `play.cullers`, `play.shadows`, `play.activate`, `play.realTime`, `play.restore`,
  `audio.capture`, `audio.suspend`, `audio.onHold`, `play.activateList` values, a `play.motionBlur` that is neither a mode nor a
  number >= 0; `clock: "fixed"` with a rate other than 60, 120, 240; segment `rate`, legacy `rates`, `settle`,
  `timing.physics: "fine"`; `audio.capture: "record"` with `clock: "fixed"` and rate != 60, or with `suspend: "off"` and
  `allowMuterOnly: false`; `play.range` outside [0, seconds]; `play.unityTime`; unknown `play.cams`; bad numbers (`res`,
  `roots`, eps values, `warmup`, `ramp`, `shutterAngle`, `maxUnresolved`, `level`, `markerEvery`, `maxHeartbeatAge`, box size);
  `waitfor event` in `play.setup`.
- Warnings: `record.res` above 1920x1080; `look: "clean"` or `order: "step"` (not built yet) and `look: "clean"` with
  render samples > 8; segment samples > 8; a blur strength above 2; `play.cullers: "live"`, and `"recorded"` with other cameras
  than the game camera; `play.warmup: 0`; `audio.capture: "record"`, `suspend: "on"`, `onHold: "mark"`, `allowMuterOnly`,
  `keepSuspended: false`, `maxHeartbeatAge` above 2 s; the 65 deg player-side rule; a setup without
  `audio force rtpc rtpc_master_volume 0` before its first `load` (offline checker only; the line is optional, Section 4.4).
- Notes: keys the replay ignores (`session.vsync`, `stream.addDuringTake`, `interpolate`, `rehearsal`, `expect`, camera
  render budgets with `look: "game"`), P1/P2 options.

## 19. Schema reference

Generated from `MultiCamTypes.cs` and `ReplayTypes.cs`; regenerate with
`python -I tools/mcam_spec_check.py --update-doc docs/MULTICAM.md`. Section numbers in the Meaning column refer to
[MULTICAM_DESIGN.md](MULTICAM_DESIGN.md) for the v2 tables and to [REPLAY_DESIGN.md](REPLAY_DESIGN.md) for the `replay`
tables, unless they name MULTICAM.md.

<!-- schema:begin -->
Generated by `python -I tools/mcam_spec_check.py --schema-md` from `MultiCamTypes.cs` and `ReplayTypes.cs`; meanings from the design docs.

#### Root (`MultiCamSpec`)

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
| replay | ReplaySpec | null | Replay mode (REPLAY_DESIGN 3.1): record pass, replay passes, sound capture; null = all defaults. A given block switches `mcam check` and the offline checker to the replay lint. |

#### timing (`TimingSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| fps | number | 30 | Output frames per second. |
| physics | string | "game" | `"game"` keeps fixedDeltaTime 1/60; `"fine"` whole substeps per step (experimental). |
| segments | SegmentSpec[] | [] | Empty = one segment {from 0, speed 1, samples 1}. Listed in time order. |

#### timing.segments[] (`SegmentSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| from | time | 0 | Segment start (switch at the next frame boundary after it is reached). |
| speed | number | 1 | Game seconds per output second (0.25 = 4x slow motion). |
| samples | int | 1 | Sub-steps averaged into one output frame (motion blur). |
| shutter | number | 180 | Shutter angle in degrees when samples > 1 (144 matches the game's own blur). |
| rate | int | 0 | > 0: explicit steps per game second; then all `samples` steps of a frame are captured. |
| label | string | "" | Name in meta.csv / timeline plots. |

#### rates[] (v1) (`RateSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| from | time | 0 | Game time the rate starts. |
| rate | number | 30 | Steps per game second (whole number). |
| group | int | 1 | Steps averaged into one output frame. |

#### cues[] (`CueSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| at | time | 0 | Game time or "@event+s". |
| cmd | string | "" | Bridge command. |
| cmds | string[] | [] | More commands at the same time. |
| label | string | "" | Name in events.csv. |

#### events[] (`EventSpec`)

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

#### render (root, defaults.render, camera render) (`RenderSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| warmup | int | 1 | Renders at the new pose before accumulation (>= 1; 0 is an error). |
| samples | int | 4 | TAA accumulation renders (4 draft, 8 standard, 16 final wide). |
| taa | string | "reset" | `reset` (feedback (i-1)/i), `keep` (game feedback), `toggle` (experimental fallback). |
| jitter | int | 0 | Jitter pattern id 1..15 (`FrustumJitterPattern`; 0 is Still in the game enum, so it cannot be chosen); 0 = auto from samples (6 / 11 / 12 / 13). |
| motionBlur | number | 0 | TAA motionBlurStrength during capture: 0 = off (use sub-step blur); a negative value keeps the game value (0.8). |

#### output (`OutputSpec`)

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

#### session (`SessionSpec`)

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

#### post (root, defaults.post, camera post) (`PostSpec`)

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

#### stream (`StreamSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| keepLoaded | string | "auto" | `auto`, `none`, `list`. |
| cullers | string[] | [] | Culler names for `list` (also added in `auto`). |
| prepass | bool | true | Run the frustum prepass before t=0. |
| visitFrames | int | 0 | Frozen frames rendered at each planned pose during the prepass. |
| timeout | number | 20 | Real seconds to wait for loading. |
| freeze | bool | false | CullingActive = false during the take (experimental). |
| addDuringTake | bool | true | Late-load detection and AddAlwaysLoaded during the take. |

#### cameras[] and defaults (`CamSpec`)

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
| yaw | number | 0 | Unity Euler degrees; positive pitch looks down. Roll is applied on top of look-at too. |
| pitch | number | 0 | Unity Euler degrees; positive pitch looks down. Roll is applied on top of look-at too. |
| roll | number | 0 | Unity Euler degrees; positive pitch looks down. Roll is applied on top of look-at too. |
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
| feedForward | bool | true | Damped target tracking (smooth / lookSmooth of follow, frame, overview, events and every look) aims at the subject plus its smoothed velocity x the damping time, so a moving subject has no steady lag (MULTICAM.md 6.6); false = the old plain damping. A given lead still adds on top. |
| planar | bool | false | Keep camera z fixed (gamelike defaults to true). |
| start | time | null | Camera clock origin (keys, rail, orbit) / follow start. |
| keys | KeySpec[] | [] | (keys) Key list (below). |
| interp | string | "catmull" | (keys, rail) `catmull` or `linear`. |
| ease | string | "ends" | (keys, rail, orbit) 3.8. |
| duration | number | 0 | (rail, orbit) Seconds on the camera clock. |
| path | [x,y,z][] | [] | (rail) Rail points. |
| radius | number | 8 | (orbit) Metres. |
| height | number | 1.5 | (orbit) Metres above the centre. |
| azimuth | [a,b] | [0,0] | (orbit) From/to degrees (0 = in front). |
| orbitYaw | number | 0 | (gamerel) Degrees around the game aim point. |
| orbitPitch | number | 0 | (gamerel) Degrees around the game aim point. |
| dist | number | 0 | (gamerel) Metres from the pivot (0 = game distance * distScale). |
| distScale | number | 1 | (gamerel) Distance multiplier. |
| shift | [a,b] | [0,0] | (gamerel) Right/up metres. |
| keepSize | bool | true | (gamerel) Distance compensation for a different FOV. |
| clean | bool | false | (game, gamerel) Use the unshaken game pose. |
| box | string | "auto" | (gamelike) `auto`, `none`, box name/path. |
| camOffset | [x,y,z] | [0,2,-11] | (gamelike) Offset without a box (game probe default). |
| aimOffset | [x,y,z] | [0,2,0] | (gamelike) Aim offset without a box. |
| extrapolate | number | -1 | (gamelike) Look-ahead metres; -1 = from the box/probe. |
| screen | [a,b] | [0.48,0.6] | (frame, static, follow, keys, rail, orbit) Subject screen point (u from left, v from top); look-at modes use it only when given (a `defaults.screen` counts as given). |
| size | number | 0.12 | (frame) Subject height as a fraction of frame (or crop) height. |
| subjectHeight | number | 0 | (frame) Metres; 0 = from bounds / 1.33 for the boy. |
| crop | number[] | null | (frame) [x,y,w,h] pixels of the capture the framing refers to. |
| shots | ShotSpec[] | [] | (director) Shot list. |
| cutAngle | number | 90 | (director) Auto hard cut above this angle. |
| cutDistance | number | 0 | (director) Auto hard cut above this aim distance (0 = off). |
| minHold | number | 4 | (director) Lint: minimum hold seconds. |
| pans | PanSpec[] | [] | (overview) Pan segments: from `from` the frame centre x moves to `to` over `duration` (ease) and holds there (the ending pan back over the route). |
| beats | BeatSpec[] | [] | (events) Event beats: on `at` the camera moves to `target` and holds it (below). |
| offsets | [x,y,z][] | [] | (events) Candidate camera offsets from the subject, tried in order: the first with a clear view (MultiCamView) wins, else the least bad; [] = `offset`. |
| hold | number | 2 | (events) Seconds a beat holds its target before the next beat may start. |
| move | number | 1 | (events) Seconds of the smooth move from the previous beat's pose (ease `ease`; `ends` = smooth). |
| late | number | 2.5 | (events) A beat that would start more than this many seconds after its event (queued behind earlier holds) is dropped. |
| clamp | ClampSpec | null | (any) Frame-edge limits: world x of the left / right frame edge at the subject depth; the pose trucks in x to stay inside (flag C). |
| view | ViewSpec | null | (any) Line-of-sight and near-plane check settings (events candidates, replay views.csv, mcam views / show); null = defaults. |
| playerSide | bool | true | Clamp and lint the player-side rule. |
| zMax | number | -2 | Player-side limit. |
| render | RenderSpec | null | Per-camera render budget (per-camera settle). |
| post | PostSpec | null | Per-camera look overrides. |
| shake | ShakeSpec | null | Shake layer. |
| hide | string[] | [] | Object paths hidden only in this camera's renders: v2 visits disable the renderers; replay passes and `mcam show` move them to a spare layer the camera does not draw (MULTICAM.md 6.4). |
| backfaces | string[] | [] | Object paths whose meshes this camera also draws from behind, in replay passes and `mcam show` only: each MeshFilter / SkinnedMeshRenderer shows a two-sided copy (`<path> box=x0,y0,z0,x1,y1,z1` = only the triangles inside); a director takes the source camera's list of the shot on air (MULTICAM.md 6.5). |
| motion | MotionSpec | null | Camera motion limits on the output clock (below); directors and events cameras use the defaults when not given. |

#### cameras[].keys[] (`KeySpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| t | number | 0 | Seconds on the camera clock (origin `start`). |
| pos | [x,y,z] | [0,0,0] | Camera position. |
| yaw | number | 0 | Euler yaw (used without `look`). |
| pitch | number | 0 | Euler pitch, positive looks down. |
| roll | number | 0 | Roll degrees. |
| fov | number | 0 | FOV at this key; 0 = camera fov. |
| look | target | null | Look-at target at this key. |
| lookOffset | [x,y,z] | [0,0,0] | Added to the key look point. |

#### cameras[].shots[] (director) (`ShotSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| cam | string | "" | Source camera (required). |
| from | time | 0 | Shot start (game time or "@event+s"). |
| blend | number | 0 | Blend seconds into this shot (0 = cut). |
| ease | string | "smooth" | `smooth`, `smoother`, `linear`, `sine`. |
| transition | string | "orbit" | `orbit` (game interpolation), `linear`, `cut`. |
| clock | string | "game" | `game` seconds or `output` seconds (blend x speed in game time). |
| label | string | "" | Name in events.csv. |
| hide | string[] | [] | Object paths hidden while this shot is on air (with its source camera's `hide`; the union with the previous shot's inside the blend), also in replay passes and `mcam show` (MULTICAM.md 6.4). |
| motionBlur | number | 1 | Scale of the replay pass motion blur while this shot is on air, its blend included (1 = unchanged, 0 = off). |

#### cameras[].shake (`ShakeSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| hand | number | 0 | Game handshake copied in this camera's frame (k). |
| fx | number | 0 | Game impact shake copied (k). |
| pullback | bool | false | Keep the running pull-back (z) of the handshake. |
| replica | number | 0 | Soft handheld from the game's noise clip. |
| hard | number | 0 | Hard handheld from the game's noise clip. |
| clock | string | "game" | `game` or `output` clock for the replica. |
| rate | number | 1 | Replica playback rate. |
| phase | number | 0 | Replica phase seconds. |

#### cameras[].clamp (`ClampSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| left | number | 0 | World x the frame's left edge may not pass (only when given). |
| right | number | 0 | World x the frame's right edge may not pass (only when given), e.g. the CEO window glass 3524.55. |
| z | number | 0 | When given: measure the edges where the edge rays meet the world plane z = this; else at the aim point's depth. |
| aspect | number | 0 | Frame aspect for the edges (0 = the render's; give the cell's aspect for a centre-cropped layout cell). |
| corners | bool | true | Count the frame corners too, not only the mid-edge rays (the outermost x wins). |

#### cameras[].pans[] (overview) (`PanSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| from | time | 0 | Pan start (record seconds or "@event+s"). |
| duration | number | 4 | Seconds of the move. |
| to | number | 0 | World x of the frame centre at the end (clamped by `clamp`). |
| ease | string | "smooth" | `smooth`, `smoother`, `linear`, `sine`. |

#### cameras[].beats[] (events) (`BeatSpec`)

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

#### cameras[].view (`ViewSpec`)

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

#### target object form (`TargetRef`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| of | string | null | Base: `boy`, `huddle`, `main`, `game`, `gameaim`, `probe`, path, `#id`, `none`, `self`. |
| point | string | null | Point on the base (`torso`, `bounds`, `com`, `bone:<name>`, ...). |
| offset | [x,y,z] | [0,0,0] | World offset added after the point. |
| pos | [x,y,z] | [0,0,0] | Fixed world point. |
| group | GroupSpec | null | Group of objects (below). |

#### target group (`GroupSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| of | target[] | [] | Member targets. |
| find | string | "" | ObjectDatabase query (`t:Type` is a substring match). |
| list | string | "" | `<path>::<Component>.<field>` list of members. |
| filter | GroupFilter | {} | Member filter (below). |
| mode | string | "centroid" | `centroid`, `weighted`, `bounds`, `nearest`, `fastest`. |
| point | string | "" | Point used on every member. |
| smooth | number | 0.3 | SmoothDamp seconds on the group point. |
| hold | number | 0.5 | Membership hysteresis seconds. |
| max | int | 0 | Keep at most this many members (fastest first); 0 = all. |

#### group filter (`GroupFilter`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| active | bool | true | Only active members. |
| alive | bool | true | Skip members whose `dead` field is true. |
| notKinematic | bool | false | Skip kinematic rigidbodies. |
| minSpeed | number | 0 | Minimum speed m/s. |
| boxCenter | [x,y,z] | [0,0,0] | Box filter centre. |
| boxSize | [x,y,z] | [0,0,0] | Box filter size (0 = off). |
| near | target | null | Reference target for `maxDist` / `nearest`. |
| maxDist | number | 0 | Maximum distance to `near` (0 = off). |

#### replay (`ReplaySpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| source | string | "" | Recording folder to replay; "" = `<out>/record` of the same take. |
| record | RecordSpec | {} | Record pass (below). |
| play | PlaySpec | {} | Replay passes (below). |
| audio | AudioCapSpec | {} | Capture and safety (below). |

#### replay.record (`RecordSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| clock | string | "real" | `real` (normal pacing, captureFramerate 0) or `fixed` (2.3.4). |
| rate | int | 60 | fixed clock: captureFramerate and targetFrameRate (60, 120, 240). |
| res | [a,b] | [0,0] | Record pass resolution; [0,0] (default) keeps the current one. |
| roots | string[] | ["auto"] | `auto`, object paths, `subscene:<name>`, `zone:<zone>`, `boy`, `huddle`; subscenes that load or activate later join (2.3.1). |
| keep | string[] | [] | Subscenes kept loaded and active from before sample 0 to the replay (header.keptCullers), recorded from sample 0. |
| settleFrames | int | 12 | Frames to wait after the kept subscenes are loaded and active before sample 0 (`srec record start` and the mcam record pass), so late-awake content (LateAwakeNode, initializationFrame <= 10: CEO_Room, the cafeteria railing FX) is on at sample 0 (2.3.4). |
| exclude | string[] | [] | Object paths left out. |
| box | BoxSpec | null | `{center:[x,y,z], size:[x,y,z]}` limits `auto` subscenes. |
| characters | bool | true | Boy actor bones, huddle cloth/arm/leg bones. |
| spawned | bool | true | Watch `_Dynamics`, `_SubsceneDynamics` and new children of recorded transforms. |
| keepSpawned | bool | false | P2: disabled clones of spawned objects. |
| particles | string | "all" | `all`, `none`, `list` (`particleSystems` paths). |
| particleSystems | string[] | [] | For `list`. |
| lights | bool | true | Channels (post is P1). |
| materials | bool | true | Channels (post is P1). |
| probes | bool | true | Channels (post is P1). |
| fog | bool | true | Channels (post is P1). |
| post | bool | false | Channels (post is P1). |
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

#### replay.record.box (`BoxSpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| center | [x,y,z] | [0,0,0] | Box centre (world metres). |
| size | [x,y,z] | [0,0,0] | Box size (world metres, every axis > 0). |

#### replay.play (`PlaySpec`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| look | string | "game" | `game`: one render per step, TAA keep, engine blur (5.3). `clean`: the v2 render budget (warmup + N TAA-reset samples) per step. |
| order | string | "camera" | `camera`: one continuous pass per camera. `step`: v2-style visits of all cameras per step over the recording (stills, clean look). |
| interp | string | "hermite" | `hermite`, `linear`, `nearest` (5.4). |
| particles | string | "extrapolate" | `extrapolate`, `match` (P1), `sample` (no extrapolation). |
| warmup | int | 16 | Renders before the first captured step of a pass and before every director run, along the shot's own poses at the preceding times, first one with TAA reset (5.3). |
| motionBlur | string | "game" | `game`, `angle`, `off`, or a fixed motionBlurStrength (5.3). Frozen engine blur works (L2), no sub-frame fallback. |
| shutterAngle | number | 288 | For `angle`. |
| cullers | string | "camera" | `camera`, `recorded`, `force`, `live` (diagnostic) (2.4.4). |
| shadows | string | "recorded" | `recorded`, `dirty`, `dynamic` (2.4.6). |
| activate | string | "quiet" | `quiet`, `normal`, `never` (2.4.4); objects never active in this session use `never`. |
| activateList | object | null | `{path: "quiet"\|"normal"}` overrides for never-activated objects. |
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

#### replay.audio (`AudioCapSpec`)

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
<!-- schema:end -->
