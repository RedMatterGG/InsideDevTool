# Character control through MCP (InsideDev.Play.dll)

`InsideDev.Play.dll` lets an AI model, or any MCP client, play INSIDE: it reads the world as JSON, presses the game's
own controls for exact logic steps, waits for conditions, respawns at checkpoints, reads playbooks and records and
replays input. It is a feature on its own: it needs `InsideDev.Mcp.dll` (the MCP server, see [MCP.md](MCP.md)) and
nothing else. The scene recorder, MultiCam and the other InsideDev extensions are not needed.

Input goes into the game's input system (no OS keyboard or mouse input, no window focus needed). Time goes through the
game clock. Everything a tool changes is put back by `restore`.

Contents

1. Install
2. What is on by default
3. Connect a client
4. How the game is driven (clock, input, ids, results)
5. First steps
6. Tool reference (14 tools)
7. Workflows
8. Input tracks (rehearse, then take)
9. Guide playbooks
10. Notes (flagged moments)
11. `ctl` console commands
12. `tools/inside_ctl.py`
13. Restore
14. Errors and busy
15. Pitfalls
16. Settings

---

## 1. Install

InsideDev itself must be installed first (`version.dll` and `InsideDev.dll`, see the README).

| File in the release | Copy to | Needed |
|---|---|---|
| `bin\InsideDev.Mcp.dll` | `<game>\_mod\` | yes: the MCP server |
| `bin\InsideDev.Play.dll` | `<game>\_mod\` | yes: the play tools (does not load without `InsideDev.Mcp.dll`) |
| `guide\` | `<game>\_mod\guide\` | optional: playbooks for the `guide` tool |
| `tools\mcp\InsideDev.McpStdio.exe` | anywhere | optional: for clients that start a local program (stdio); needs .NET Framework 4.x (part of Windows) |
| `tools\inside_ctl.py` | anywhere | optional: command-line control without an MCP client (Python 3, standard library only) |

`<game>` is the INSIDE folder (the one with `INSIDE.exe`). Extensions load at game start from
`<game>\_mod\InsideDev.*.dll` in name order, so Mcp loads before Play. With the game already running, copy the files and
type `ext load` in the InsideDev console (backquote key) or send it through the file bridge: it loads DLL files that are
not loaded yet. Replacing a DLL that is already loaded needs a game restart.

Check in the console:

| Command | Shows |
|---|---|
| `mcp` | server status: endpoint, build line (`mcp 0.7.0+<id>, play 0.7.0+<id>, core ...`), tool counts (play 14, dev 33, all 40), control lane, allowlist, whether a token is required |
| `play` | `Play (MCP play/world tools: <n>, problems: <n>)`, tick count, problems |
| `play tools` | every Play tool with its profiles and owner |
| `ext` | loaded extensions and load problems |
| `mcp selftest`, then `mcp test` | a real HTTP session against `/mcp` and `/mcp/play` (PASS or FAIL with the step) |

To remove the feature, delete `InsideDev.Play.dll` from `_mod` (and `InsideDev.Mcp.dll` for the server). There is no
setting that unloads Play.

## 2. What is on by default

With the DLLs installed and no client calling a tool, the game plays exactly as before, with these exceptions:

| Item | Default | Effect | How to change |
|---|---|---|---|
| MCP server | on | listens on `127.0.0.1:47811` (this computer only, no token) | console `mcp off` (saved in editor.cfg), `mcp on`; or remove the DLL |
| Notes hotkey | `Home` | pressing Home in the game saves a note: one line in `_mod\notes.jsonl`, a screenshot `_mod\notes\note_<id>.png`, a log line | `play.notes_key` in editor.cfg remaps it (a Unity KeyCode name); `none` or `off` switches the hotkey off |
| Event hooks | passive | Play listens to load, death and log events; object scans run only while a client was active in the last 90 s | none |

Off by default, opt in through `<game>\_mod\editor.cfg` (section 16): `play.cheats` (checkpoint jumps), `play.save_guard`
(save backups and confirms), `mcp.token`, `mcp.tools` (allowlist), `play.guide_max` (spoiler cap).

The first action call (`act`, `move_to`, `interact`) changes three things until `restore`: the clock goes to step mode
(frozen between calls), the agent input core takes over the controls (your pad and keyboard are ignored), and the game
keeps running when its window loses focus (`runInBackground`). `wait_until` and `time` (other than `state` and
`rng_seed`) change the clock and `runInBackground` but not the input; `input_track {op:"start"}` and `ctl` input
commands install the input core too. Section 4 explains each one; section 13
explains how they are put back.

## 3. Connect a client

| Transport | Address |
|---|---|
| Streamable HTTP | `http://127.0.0.1:47811/mcp/play` |
| stdio | `C:\path\to\InsideDevTool\tools\mcp\InsideDev.McpStdio.exe --profile play` |

Ready-made client files (Claude Code, Claude Desktop, Cursor, VS Code, Codex, Gemini CLI) are in `tools\mcp\clients\`.
Two examples:

```
claude mcp add --transport http inside-play http://127.0.0.1:47811/mcp/play
claude mcp add inside-play -- "C:\path\to\InsideDevTool\tools\mcp\InsideDev.McpStdio.exe" --profile play
```

- The stdio program can be connected before the game starts. Until INSIDE answers it lists only `inside_status`, and it
  tells the client when the game's tools appear or go away. Once the game is up, call `inside_status` once: its result
  carries the game's instructions, which a client that connected early did not get. Clients that ignore
  `list_changed` notifications need a reconnect.
- With a token set in the game (`mcp.token`), HTTP clients send `Authorization: Bearer <token>`; the stdio program takes
  `--token <token>` or the environment variable `INSIDEDEV_MCP_TOKEN`.
- The play profile serves 14 tools: `observe`, `act`, `move_to`, `interact`, `wait_until`, `time`, `checkpoint`, `menu`,
  `screenshot`, `geometry`, `guide`, `notes`, `input_track`, `restore`. Resources: `inside://guide/index`,
  `inside://guide/<group>/<topic>` (template `inside://guide/{chapter}/{topic}`), `inside://shots/{file}`. Prompts:
  `playtest_from_checkpoint` (subscene, savepoint, goal), `solve_here` (goal).
- `/mcp/all` serves the play and dev tools together (40 tools, the tool cap of Cursor). `/mcp` and `/mcp/dev` serve the
  dev profile, which has no `act`, `move_to`, `interact`, `checkpoint`, `menu`, `guide` or `notes`.
- One client at a time should drive the game. Every session acts on the same game; a second action while one runs gets
  `busy` (section 14).

## 4. How the game is driven

### 4.1 Clock: step mode and realtime

| Mode | When | Game time |
|---|---|---|
| realtime (not touched) | before the first action call | runs as usual |
| step mode | chosen automatically by the first call that runs the game (`act`, `move_to`, `interact`, `wait_until`, `time {op:"step"}`) unless the client chose realtime before | frozen between calls (time scale 0); runs only inside those calls, one logic step per rendered frame |
| realtime (chosen) | `time {op:"realtime", speed}` | runs between calls at `speed` (0.05..4); actions watch the game instead of stepping it |

- One logic step is 1/60 s of game time. `act` takes at most 600 steps per call (10 s of game time).
- `time {op:"step_mode"}` goes back to step mode, `time {op:"pause"}` / `{op:"resume"}` freeze and unfreeze without
  changing the mode, `time {op:"step", steps}` runs 1..600 steps and freezes.
- `then:"run"` on `act` or `wait_until` leaves the game running after that one call (step mode stays chosen).
- The game is never frozen while it loads. A freeze asked for during a load is applied once the game is ready
  (`freeze_deferred` warning), so after a checkpoint load in step mode the game runs until it is playable, then stops.
- Step mode sets `captureFramerate` 60 and vSync 0 while a run is active and puts them back after it. In realtime vSync
  stays off too while `editor.cfg` has `play.vsync_off` = 1 (the default from 0.8.0, a development tool); `play.vsync_off=0`
  leaves the game's own vSync, and `time {op:"vsync", on:false}` then holds it off while an agent holds the clock (4.5).
- In places that run on the music clock (mines shockwave, the lineup, lab lift and water lock) a step-mode run is paced
  to real time (60 Hz wall clock), because the music pauses while the game is frozen. Slow motion (`speed` other than 1)
  desyncs these places. `observe`, `time` and every action result warn `music_timed: <what>`. To act at a safe moment,
  give `act`, `move_to` or `interact` a `when` condition (section 6.11).

### 4.2 Input

The agent input core (AgentCore) replaces the game's input core. It feeds one frame per logic step: stick `[x, y]`,
jump, action. The same input drives the boy, the huddle, mind-controlled dudes, the submarine, levers and valves.

| Mode | Set by | Physical pad and keyboard |
|---|---|---|
| agent | the first action call (default) | ignored |
| mix | `act {input_mode:"mix", inputs:[]}` or `ctl mode mix` | merged with the agent input (stick by magnitude, buttons OR) |
| human | `act {input_mode:"human"}`, `ctl mode human`, `restore` | only the physical controls; the agent core is removed |

Game rules the input follows (from the game code):

- Stick: x > 0 right, y > 0 up (screen space). Send 0 or a magnitude 0.3..1 (below 0.3 the game reads 0). Above 0.8 the
  boy runs (about 2.72 m/s), below he walks.
- Jump is a rising edge, buffered 0.25 s; to jump again it must be released for at least one step. Holding it gives the
  full height.
- Action on the ground or in water: held = grab; a tap under 0.3 s = action (fires on release). In the air and on a
  ladder it is a rising edge. For the huddle, jump and action are plain held levels.
- Drop (let go of a ladder, rope or ledge) = jump + stick down (y < -0.5).
- After a menu or UI change the game ignores jump and action until every button was released once: send a neutral step.
- During fades and loads input is off; `wait_until {controllable:true}` before acting.
- When the keyboard was the last device used (`controller` Digital in `observe` full), the submarine boost, valves, the
  cliff climb and the huddle throw behave differently and results warn `controller_digital`. Leave the keyboard alone
  while an agent plays.
- Scripted moments drive the stick themselves (the cafeteria railing crash, the CEO wall): `observe.body.stick_forced`
  is true and `forced_by` names the script. Wait them out.

### 4.3 Ids, coordinates, names

- Ids from `observe`: `b<n>` bodies, `o<n>` objects, `h<n>` hazards. One counter that never restarts, so an id is never
  reused. Every load start ends all ids, and a respawn after a death is a load. An old id fails with `stale_id`: call
  `observe` again.
- Tools that take a target accept an id, `boy`, `huddle`, `#<instanceID>`, an exact hierarchy path, a unique path suffix
  or a unique exact name. There is no nearest-match fallback: several matches give `ambiguous` with `candidates`.
- Positions are world metres. `dx > 0` is right, `dy > 0` is up, relative to the controlled body. `y` is the body
  centre (the boy's feet are at y - 0.665). Never use screen pixels.

### 4.4 Results

Every result is `structuredContent` with `ok` first (a compact JSON copy is the first text block):

```json
{"ok": false, "error": {"code": "stale_id", "message": "o14 is from before the last load", "hint": "call observe again"}}
```

- `isError` is true exactly when `ok` is false.
- A wait that times out is not an error: `ok:true, satisfied:false, timed_out:true`.
- Action results carry `outcome`, `steps`, `changes` (what the action did) and a concise `observe`, so the next
  decision rarely needs another `observe` call.
- When the game cannot take input, results carry `next`: the call to make (`menu {op:"submit"}` at the title,
  `menu {op:"close"}` when paused, `wait_until {alive:true}` after a death, `wait_until {ready:true}` while loading,
  `menu {op:"message_answer", ...}` for a storage message).
- Arguments are checked strictly against the tool's input schema (`bad_args` with the path, for example
  `inputs[2].stick: expected 2 numbers`). A string where a number, boolean, array or object is expected is parsed when
  possible and reported in `warnings` as `coerced: ...`.

### 4.5 Speed: frame meter, vSync and real-time recording

vSync is off from the start by default (`play.vsync_off` = 1 in `editor.cfg`); the recorder and the fast screenshot run
only when called, and `restore` stops a recording.

| Need | Use | Why |
|---|---|---|
| know how fast the game renders | `time {op:"state"}`: `fps`, `frame_ms_max` | frames of the last second and the longest of them |
| realtime without the 30 fps drop | `time {op:"vsync", on:false}` (console `vsync off`) | with vSync on, a frame a little over 16.7 ms halves the rate; restore key `vsync` |
| watch or review a pass | `time {op:"record"}` ... `time {op:"record_stop", review_fps:5}` (console `rec start`, `rec stop 5`) | real-time video of the game window; no per-step screenshots |
| one still, quickly | `screenshot {mode:"path", fast:true}` | skips the decode for the black check |

The recorder runs ffmpeg with `ddagrab` (DXGI Desktop Duplication: the GPU copies the screen, the game's main thread does
nothing) on the game window's client area and encodes with `hevc_nvenc` (`preset p6`, `tune hq`, constant quality `cq`,
default 16) into `<game>\_mod\recordings\<date>_<time>.mkv`. The window and its monitor are found from inside the game
in physical pixels; the DXGI output is the first one of the default graphics adapter with the monitor's size. Desktop
duplication records what is on the screen: a window over the game is recorded too, so `raise:true` lifts the game window
to the top without taking focus. ffmpeg comes from the `ffmpeg` argument, then the `INSIDEDEV_FFMPEG` environment
variable, then `<game>\_mod\ffmpeg\ffmpeg.exe`, `C:/ffmpeg/bin/ffmpeg.exe` and `PATH`. A pass recorded this way runs
in realtime: drive it with `act {hold:true, then:"run"}` and wall-clock waits, or let a person play.

```json
{"op": "vsync", "on": false}
{"op": "record", "record_fps": 30, "cq": 16, "raise": true}
{"op": "record_stop", "review_fps": 5}
```

### 4.6 Live director passes: `campath` and `grec`

Two console commands of Play (reachable from the console, `POST /cmd`, the file bridge and the dev `command` tool). Both
are off until called; `restore` stops both (restore keys `campath`, `grec`).

- `campath` puts the main game camera on a pose path planned in advance while the game itself runs. Every frame is the
  game's own picture: live particles, shaders animated by time, mod effects, temporal anti-aliasing. The pose is set in
  `Camera.onPreCull`, after the game's camera script, so only the rendered view changes: the game's camera logic, its
  culling probe and the input stay as they are.
- `grec` films the game's own frames into an H.265 file. At the end of every n-th rendered frame the native
  `InsideReadback` plugin copies the back buffer on the GPU (render thread); a background thread hands the frames to a
  separate ffmpeg process that encodes them with `hevc_nvenc`. Windows over the game, focus and other programs never reach
  the file (the recorder of section 4.5 films the desktop instead).

| Command | Does |
|---|---|
| `campath load <dir> [camera] [offset_s] [rt]` | reads `<dir>\poses.csv` and `<dir>\meta.csv` and keeps one camera's poses (default `director`); `offset_s` shifts the plan's game time; `rt`: the pose is looked up by game time, with no pauses or slow motion |
| `campath arm` | starts with the first logic step of the next `input_track` playback |
| `campath start` | starts at once |
| `campath status` | `idle`, `armed` or `running` with output seconds, game time against plan time, `paused` or the speed |
| `campath stop` | stops; the field of view, the near plane, the game speed and a freeze it made are put back |
| `campath mount <transform path\|off> [lx ly lz] [rx ry rz] [fov] [near] [smooth] [into_scene_deg] [level] [hide] [tau_h tau_v tau_yaw tau_pitch] [pitch_max]` | the camera rides a transform (position and rotation every frame) and the plan keeps only the clock. Defaults: offsets 0, `fov` 60, `near` 0.03, `smooth` 0 (0..1), `into_scene_deg` 0, `level` 0 (0..1), `hide` 0, `tau_*` 0 s, `pitch_max` 89. `hide 1` collapses the transform while mounted (the boy's head for a first-person view); `tau_*` > 0 smooth position, yaw and pitch with those time constants and keep the roll at 0; `into_scene_deg` turns the view up to that angle toward +z (into the level), fading out while the view looks down 18 to 38 degrees; `level` takes that share of the roll out |
| `campath look add <t0> <t1> <ease_s> <x> <y> <z>`, `campath look add <t0> <t1> <ease_s> @<transform path>`, `campath look clear` | while mounted, the view turns to a point or a transform between plan seconds `t0` and `t1`, eased in and out |
| `grec start <file.mkv> [every] [cq]` | records every `every`-th rendered frame (default 2) with constant quality `cq` (default 16); give an absolute path |
| `grec stop` | stops; the file is complete when `grec status` says `done` |
| `grec status` | phase (`opening`, `recording`, `draining`, `closing`, `done`, `failed`), file, grabs, written, dropped, size and pixel format, seconds, error |

**Clock of `campath`.** Output time runs with the wall clock at 30 poses a second. In director mode (without `rt`) the game
is driven toward the plan's game time: frozen while the plan's speed is below the stepper's minimum (a paused tour),
otherwise realtime at the plan's speed with a drift correction between 0.5x and 2x of it. An `input_track` playback waits
while campath holds the game frozen. The path stops by itself at its last pose.

**Plan files.** `mcam passes <spec> <out>` writes them for every camera of a spec into `<out>\passes\`
([MULTICAM.md](MULTICAM.md) section 18.4); any script can write them too. `poses.csv`: a header with `step`, `camera`,
`x`, `y`, `z` (adjacent), `fov` (vertical, degrees), `qx`, `qy`, `qz`, `qw` (adjacent) and an optional `near` (0 keeps
the camera's); one row per camera and output step (30 a second). `meta.csv`: `step`, `game_time`, `speed`. A still
camera for four seconds of a test looks like this (rows for steps 1 to 120 repeat the pose):

```
poses.csv
step,camera,x,y,z,pitch,yaw,roll,fov,qx,qy,qz,qw,near
0,director,412.0,-8.5,-9.0,8,0,0,40,0.069756,0,0,0.997564,0.1
meta.csv
step,game_time,rate,group,frame,sub,captured,speed,real_time,fixed_steps,segment
0,0.000000,30,1,0,0,1,1,0.000000,0,0
```

**Frame rate of `grec`.** The file is written at 60 / `every` frames a second (30 for the default) and gets one frame per
`every` rendered frames, so it plays at game speed only while the game renders 60 frames a second. vSync is off by default
(section 4.5) and the game then renders faster: turn it on for a real-time file (`time {op:"vsync", on:true}`, console
`vsync on`) and keep the frame time under 16.7 ms.

**Needs.** `grec` needs `InsideReadback.dll` in `<game>\INSIDE_Data\Plugins\` (the optional MultiCam plugin), ffmpeg
(the same lookup as the recorder: `INSIDEDEV_FFMPEG`, `<game>\_mod\ffmpeg\ffmpeg.exe`, `C:/ffmpeg/bin/ffmpeg.exe`,
`PATH`) and an NVIDIA GPU. It cannot run together with a MultiCam run (`InsideReadback busy`): the plugin serves one owner.

**Where the work runs.** Only the frame trigger runs on the game's main thread; the copy runs on the render thread on the
GPU, the hand-over on the background thread `grec pump`, the encoding in the ffmpeg process on NVENC. Windows schedules
these on other CPU cores, so the game's frame time stays free (2560x1440, 30 fps file: 3222 grabs, 0 dropped on a laptop
RTX 3070). `campath` works inside the frame on the main thread, because it moves a game object (the camera).

## 5. First steps

The loop: `observe` -> `act` / `move_to` / `interact` -> `wait_until` -> read `outcome`, `changes`, `observe` -> repeat.
Call `restore` when you stop.

1. Read the playbook index and the rules once (needs the `guide` folder):

   ```json
   {"name": "guide", "arguments": {}}
   {"name": "guide", "arguments": {"topic": "mechanics/controls"}}
   ```

2. Look:

   ```json
   {"name": "observe", "arguments": {}}
   ```

   At the title screen the result has `"screen": "title", "ready": false, "next": "menu {op:\"submit\"} ..."`. Start the
   game with `menu {op:"submit"}`, then `wait_until {any:[{ready:true}]}`.

3. Act. Run right for one second, then a jump while still running:

   ```json
   {"name": "act", "arguments": {"inputs": [{"stick": [1, 0], "steps": 60}, {"stick": [1, 0], "jump": true, "steps": 12}]}}
   ```

   Walk to an object from `observe.objects` and grab it:

   ```json
   {"name": "move_to", "arguments": {"target": "o14"}}
   {"name": "interact", "arguments": {"verb": "grab", "target": "o14"}}
   {"name": "interact", "arguments": {"verb": "pull", "target": "o14", "seconds": 1.5}}
   {"name": "interact", "arguments": {"verb": "release"}}
   ```

4. Wait for something:

   ```json
   {"name": "wait_until", "arguments": {"any": [{"grounded": true}, {"seconds": 3}], "timeout_s": 20}}
   ```

5. Died? `wait_until {any:[{alive:true}]}` (the game respawns at the checkpoint), or `checkpoint {op:"reload"}`.

6. Done: `restore` (realtime at speed 1, your controls back).

The loop in short: keep calls short (20 to 60 steps), read `observe` in each result, use ids from the latest result
only, change approach when `stuck.calls_without_progress` grows, and read the guide topic named in
`observe.checkpoint.guide` (hint first, steps or solution only when stuck).

## 6. Tool reference

Common to the action tools (`act`, `move_to`, `interact`, `wait_until`, `time step`): they take the control lane
(section 14), run the game in step mode or watch it in realtime, release their input at the end (except `act {hold}`
and a grab grip), and return `changes` plus a concise `observe` unless `observe:"none"`.

Every tool call has a budget of 55 s wall time (plus 2.5 s grace on the host). A call that holds a job and runs over
continues as that job (error `timeout` with `job`; wait for it with `wait_until {any:[{job:"j7"}]}`); one without a job
is cancelled.

### 6.1 observe (read-only)

What the agent sees, as JSON.

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `detail` | enum | `concise` | `concise`, `full` | concise is trimmed to about 2400 characters (about 600 to 800 tokens) |
| `radius` | number | 12 | 1..60 | metres around the body for objects and hazards (hazards are searched within at least 20 m) |
| `max_objects` | integer | 12 | 0..40 | objects to list; actionable rows are kept first |
| `image` | enum | `none` | `none`, `preview`, `marked` | attach a screenshot (`marked` draws the ids) |
| `max_width` | integer | 960 | 160..1920 | image width |

Returns:

| Field | Content |
|---|---|
| `frame`, `step` | `Time.frameCount`, `LogicManager.stepCount` |
| `screen` | `title`, `loading`, `in_world`, `dead`, `cutscene`, `pause_menu`, `menu`, `credits`, `message`, `unknown` |
| `ready`, `reason`, `next` | whether the game takes play input now; why not; the call to make |
| `time` | `{mode: step or realtime, frozen, scale, music_s}` |
| `body` | the controlled body: `kind` (boy, huddle, dude, submarine), `id`, `name`, `x`, `y`, `z`, `vx`, `vy`, `facing` (+1 right, -1 left), `state` (Run, Jump, GrabPlateau, ReachPlateau, ClimbDown, Grab, Ladder, Rope, Swim, SwimGrab, Slide, CustomAnim, Ragdoll; huddle Run, Climb, Grab, Swim ...), `anim`, `grounded`, `in_water`, `submerged` (0..1), `air_s` (while diving), `holding` (id), `holding_name`, `grip_held`, `grab` (huddle: none, GrabBeam, GrabBox, GrabContainer, GrabItem, GrabShield, GrabSitOnTop), `stick_forced`, `forced_by`, `special` (none, InsideSub, ClimbingSub, ClimbingCliff, MindControlling, MindControlled), `dead`, `controllable`; full adds `obstructed`, `edge_left`, `edge_right`, `depth` |
| `checkpoint` | `{subscene (0..82), savepoint, name, area, chapter, chapter_name, guide}`; `guide` is the playbook topic for this place |
| `objects[]` | `{id, kind, name, path, dx, dy, dz, dist, actions[], state, screen}`; kinds: box, pushable, ledge, ladder, rope, lever, valve, button, door, hatch, elevator, hat, submarine, water, savepoint, trigger, creature, item, boards, lift_door, device, other; `actions` = interact verbs that work now or after `move_to`; `state` may be `in_reach` |
| `hazards[]` | `{id, kind, name, dx, dy, dist, state, eta_s, threat, safe, music_timed}`, most urgent first; kinds: dog, guard, sentry, spotlight, rope_gun, shockwave, mermaid, kill_zone, drowning, fall, crusher, other; `eta_s` = seconds until it can kill (-1 unknown) |
| `events[]` | since the previous observe, at most 20: `{step, type, detail}`; types death, respawn, load_started, load_finished, checkpoint, signal, fsm, toggled, error, note, input_hijacked, stalled |
| `stuck` | `{calls_without_progress, deaths_here, hint}`; progress = new ground, a new checkpoint or a new event near the body; `hint` from 3 calls or 2 deaths |
| `warnings[]` | `music_timed: ...`, `focus_lost`, `focus_recovered`, `camera_away`, `controller_digital`, `input_hijacked`, `scan_pending` ... |
| `meta` | `{play, mcp, core}` build versions (only in the observe tool result) |
| full only | `input {mode, held, queued, controller, hijacked, track}`, `bodies[] {id, kind, name, x, y, controlled, active}`, `menu {open, group, selected}`, `camera {overridden, pinned_subscene}`, `truncated`, `perf` |
| with image | `image {width, height, path, black}`; objects get `screen` pixel positions |

Secret devices (orb pods and similar) are never listed. Clients that pass only `structuredContent` to the model (Claude
Code does) drop the image of `observe {image}`: use the `screenshot` tool instead.

### 6.2 act

Press the controls for exact logic steps.

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `inputs` | array | | 0..64 segments | input segments in order |
| `inputs[].stick` | [number, number] | [0, 0] | each -1..1 | stick x (right +), y (up +); magnitude 0 or 0.3..1 |
| `inputs[].jump` | boolean | false | | jump held during the segment |
| `inputs[].action` | boolean | false | | action held during the segment |
| `inputs[].steps` | integer | 1 | 1..600 | logic steps of this segment; all segments together at most 600 |
| `hold` | boolean | false | | keep the last segment pressed after the call (until `release` or the next act) |
| `release` | boolean | false | | release held input (and a grab grip) before the inputs run |
| `input_mode` | enum | | `agent`, `mix`, `human` | switch the input mode (section 4.2); `human` runs no inputs |
| `stop_on` | enum[] | `["death","load"]` | `death`, `load`, `state_change`, `grounded`, `airborne`, `checkpoint`, `hazard`, `event` | stop early |
| `watch` | string[] | | 0..8 ids | stop when one of these objects moves 10 cm, turns 10 degrees, switches on or off, or its FSM state changes |
| `when` | object | | one wait condition | wait for it first, then act in the same call (6.11) |
| `when_timeout_s` | number | 15 | 0.1..45 | wall-clock limit for `when`; not met = outcome `timeout`, no input |
| `then` | enum | `freeze` | `freeze`, `run` | step mode: freeze after the call or keep the game running |
| `changes` | enum | `compact` | `compact`, `full` | compact = near the body plus deaths, checkpoints, loads, errors |
| `observe` | enum | `concise` | `concise`, `none` | observation in the result |

`stop_on`: `hazard` checks every 6 steps for a new threat or a hazard that can kill within 1 s; `event` stops on any new
change entry. After a full run with `hold` the last segment stays pressed; after a stop by `stop_on` / `watch` the input
of that step stays pressed; after death, load, cancel, timeout or stall nothing is held.

Returns `outcome` (`done`, `stopped`, `died`, `loading`, `cancelled`, `timeout`, `not_controllable`, `stalled`),
`stop_reason`, `next`, `steps` (run), `requested`, `frames`, `held {stick, jump, action, grip}`, `input_mode`,
`when {condition, satisfied, detail, waited_steps, elapsed_ms}` (with `when`), `warnings`, `changes`, `observe`.

```json
{"inputs": [{"stick": [1, 0], "steps": 40}], "stop_on": ["death", "load", "airborne"]}
{"inputs": [{"stick": [0, 1], "steps": 20}], "hold": true}
{"inputs": [{"stick": [-1, 0], "steps": 90}], "when": {"hazard_eta": {"target": "h3", "min_s": 4}}}
{"input_mode": "human"}
```

### 6.3 move_to

Walk or run the controlled body to a place. Give exactly one of `target`, `x`, `dx`.

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `target` | string | | | object id from observe |
| `x` | number | | -100000..100000 | world x (m) |
| `dx` | number | | -500..500 | metres from the body (+ right) |
| `y` | number | | -100000..100000 | world y for swimming or climbing targets (body centre) |
| `gait` | enum | `run` | `walk`, `run` | |
| `tolerance` | number | 0.3 | 0.05..3 | arrival tolerance (m) |
| `stop` | enum | `adjacent` | `adjacent`, `exact` | adjacent = within grab reach, facing the target; exact = at the point |
| `max_steps` | integer | 600 | 1..1800 | logic-step limit |
| `jump_gaps` | boolean | true | | jump gaps and low walls on the way |
| `climb` | boolean | true | | climb onto ledges in the way |
| `when`, `when_timeout_s` | | | | as in `act` |
| `observe` | enum | `concise` | `concise`, `none` | |

There is no navmesh: the navigator reads a collision slice at the body's depth plane, runs in 30-step segments and
re-plans after landings, climbs and unexpected walls. Returns `outcome` (`arrived`, `blocked`, `fell`, `died`,
`timeout`, `cancelled`, `unreachable`, `loading`), `reason` (for blocked: wall, pit, obstacle, ledge_up, ledge_down,
water, hazard), `steps`, `distance_left`, `x`, `y`, `events[]`, `goal`, `state`, `route`, `replans`, `warnings`,
`changes`, `observe`. A grab grip stays held on arrived, blocked, fell, timeout and unreachable (the box comes along) and
is let go on cancel, death and loads.

```json
{"dx": 6}
{"x": 412.5, "gait": "walk", "stop": "exact"}
{"target": "o14", "jump_gaps": false}
```

### 6.4 interact

One move with an object or the current body state, built from the game's own input thresholds.

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `verb` (required) | enum | | see below | what to do; `observe.objects[].actions` lists the verbs an object offers |
| `target` | string | nearest object offering the verb | | object id |
| `dir` | enum | `toward` | `left`, `right`, `up`, `down`, `toward`, `away` | direction for push, pull, throw, rope_jump, ladder_off, sub_boost, lever, valve |
| `seconds` | number | 1 | 0.05..10 | duration for push, pull, button hold, lift, throw swing, swim_*, ladder_up / ladder_down, sub_boost charge, valve turning |
| `approach` | boolean | true | | walk to the target first when it is out of reach |
| `allow_direct` | boolean | false | | hat_detach only: if the gesture fails, call `MindHat.ForceDetach` (a direct game call, listed in `direct_calls`) |
| `when`, `when_timeout_s` | | | | as in `act` |
| `observe` | enum | `concise` | `concise`, `none` | |

| Verb | Input the macro sends |
|---|---|
| `grab` | action held facing the grabbable; the grip stays held across later `act` / `move_to` calls |
| `release` | lets go of the grip |
| `push`, `pull` | grab, then the stick toward (push) or away (pull) for `seconds`; hatches and boards along their axis; grab devices until their done state |
| `climb` | stick up at a ledge |
| `climb_down` | stick down near an edge |
| `drop` | jump + stick down (let go of a ladder, rope or ledge) |
| `ladder_up`, `ladder_down` | stick up / down on a ladder for `seconds` |
| `ladder_off` | `dir` left / right jumps off, otherwise drops |
| `rope_jump` | jump off a rope toward `dir` |
| `swim_down`, `swim_up`, `surface` | stick down / up for `seconds`; swim to the surface |
| `hat_detach` | stick down (y < -0.8) for 1/3 s, then action |
| `hat_wear` | walk or jump into a free hat |
| `sub_enter` | on top of the submarine near the hatch, stick straight down |
| `sub_boost` | hold jump for `seconds` (at least 0.5 s), release with a direction (`dir`) |
| `sub_exit` | action after a release, at the surface |
| `lever` | grab, wait 0.21 s, stick along the lever (`dir`, default the way it can move) |
| `valve` | grab, then a turning stick for `seconds` (`dir` right / down = clockwise) |
| `button` | a rising action press in reach, facing the button; held for `seconds` |
| `lift` | lift doors: action held facing, stick up until open |
| `throw` | huddle: release the held object with the stick toward `dir` |

Every verb other than grab, push and pull lets go of a grip first (`grip_released` warning). Returns `outcome` (`done`,
`failed`, `not_available`, `out_of_reach`, `died`, `loading`, `timeout`, `cancelled`), `verb`, `target`,
`state_before`, `state_after`, `steps`, `detail`, `phases[] {name, steps, result}`, `direct_calls[]`, `warnings`,
`changes`, `observe`.

```json
{"verb": "lever", "target": "o22"}
{"verb": "push", "target": "o14", "dir": "right", "seconds": 2}
{"verb": "drop"}
```

### 6.5 wait_until

Let the game run (step mode) or watch it (realtime) until one condition in `any` holds, then freeze.

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `any` (required) | array | | 1..8 items | conditions; the first that holds ends the wait; each item has exactly one key |
| `timeout_s` | number | 20 | 0.1..50 | wall-clock limit |
| `max_steps` | integer | 3600 | 1..36000 | game-step limit (raised to cover a `steps` / `seconds` condition) |
| `then` | enum | `freeze` | `freeze`, `run` | |
| `observe` | enum | `concise` | `concise`, `none` | |

| Condition key | Value | Holds when |
|---|---|---|
| `steps` | integer 1..36000 | that many logic steps ran |
| `seconds` | number 0.05..600 | that much game time passed |
| `grounded` / `airborne` | boolean | the body is on the ground / in the air |
| `controllable` | boolean | input reaches the body |
| `ready` | boolean | a load finished and the game is playable (must hold on two steps in a row during a run) |
| `loaded` | boolean | no load in progress (`false` = a load is in progress) |
| `checkpoint_changed` | boolean | the current savepoint differs from the one at the start |
| `dead` / `alive` | boolean | the body died / is alive and playing again (fade-in done) |
| `state` | string | the body state name (Run, Jump, Ladder, Swim ...) |
| `screen` | enum | one of the observe screen values |
| `near` | `{target, within=1}` | body within `within` m (0.1..100) of the target (x / y distance to its transform) |
| `far_from` | `{target, beyond=5}` | body farther than `beyond` m (0.1..200) |
| `hazard_safe` | hazard id | its threat is over or the body is in cover |
| `hazard_eta` | `{target, min_s=4}` | the hazard's `eta_s` is at least `min_s` (the shockwave: seconds before the next blow) |
| `music_time` | `{from, to, cycle=0}` | the music position is in [from, to); `cycle` 0 = the running shockwave's cycle; `from > to` wraps |
| `fsm_state` | `{object, fsm, state}` | a PlayMaker FSM enters the state |
| `signal` | `{object, output}` | a signal output fires |
| `object_active` | `{target, active=true}` | the object becomes active / inactive |
| `job` | job id | that job finished |

Rules: ids and paths are resolved before the game runs (`stale_id`, `not_found`, `ambiguous`). `dead`, `alive`,
`ready`, `loaded`, `screen`, `checkpoint_changed`, `controllable` and `job` waits run through deaths and loads; the other
conditions stop there (`stop` died / loading, `satisfied:false`). A condition that already holds ends the wait at once
(`waited_steps` 0). A wait with only `job` conditions takes no lane and only watches.

Returns `satisfied`, `which` (index into `any`, -1 on timeout), `condition`, `timed_out`, `waited_steps`, `elapsed_ms`,
`observed` (the values at the end), `stop`, `detail`, `job` (for a job condition), `warnings`, `changes`, `observe`.

```json
{"any": [{"alive": true}], "timeout_s": 30}
{"any": [{"near": {"target": "o9", "within": 0.5}}, {"seconds": 4}]}
{"any": [{"music_time": {"from": 0.2, "to": 1.5}}]}
{"any": [{"job": "j7"}], "timeout_s": 50}
```

### 6.6 time

The game clock.

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `op` | enum | `state` | `state`, `step_mode`, `realtime`, `pause`, `resume`, `step`, `speed`, `rng_seed`, `vsync`, `record`, `record_stop` | |
| `steps` | integer | 1 | 1..600 | op step: logic steps to run, then freeze (switches a realtime clock to step mode) |
| `speed` | number | 1 | 0.05..4 | op speed / realtime: game speed while running (stored only in step mode) |
| `seed` | integer | 0 | 0..2147483647 | op rng_seed: sets `PDRandom.seed`, `RandomGenerator.ResetAll`, `UnityEngine.Random.seed` |
| `observe` | enum | `concise` | `concise`, `none` | op step only |
| `on` | boolean | true | | op vsync: true = the game's own vSync, false = off in realtime while an agent holds the clock |
| `record_fps` | integer | 30 | 5..60 | op record: frames per second of the video |
| `cq` | integer | 16 | 0..51 | op record: NVENC constant quality (lower is better and larger) |
| `raise` | boolean | false | | op record: lift the game window to the top without taking focus |
| `file` | string | `<date>_<time>.mkv` | | op record: file name in `<game>\_mod\recordings` |
| `ffmpeg` | string | | | op record: path of `ffmpeg.exe` |
| `review_fps` | number | 0 | 0..30 | op record_stop: also cut JPG review frames at this rate into `<file>_frames` |

Returns `mode`, `frozen`, `speed`, `scale`, `game_scale`, `capture_fps`, `frame`, `step`, `music_s`, `loading`,
`paused`, `running`, `freeze_pending`, `vsync`, `vsync_off`, `fps`, `frame_ms_max`, `run_in_background`, `recording`,
`pacing` (`real_time_60` or `none`), `record {on, file, seconds, rect, problem, frames_dir}`, `warnings`; op step adds
`steps_run`, `frames_run`, `stop`, `detail`, `changes`, `observe`; op rng_seed adds `seed`; op record_stop adds
`stop_file`, `stop_mb`, `stop_seconds`, `stop_clean` and `stop_frames_dir`. `state`, `rng_seed`, `record` and `record_stop`
take no lane (a recording runs next to any action).

```json
{"op": "realtime", "speed": 1}
{"op": "step", "steps": 30}
{"op": "rng_seed", "seed": 1}
```

### 6.7 checkpoint

Savepoints. `current` and `list` are read-only.

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `op` | enum | `current` | `current`, `list`, `reload`, `next`, `prev`, `goto` | |
| `subscene` | integer | | 0..999 | op goto: savepoint subscene index (0..82, see list) |
| `savepoint` | integer | | 0..999 | op goto: savepoint index |
| `filter` | string | | | op list: name filter |
| `limit` | integer | 40 | 1..200 | op list rows |
| `offset` | integer | 0 | 0..1000 | op list first row |
| `wait` | boolean | true | | wait until playable after the load |
| `timeout_s` | number | 45 | 1..50 | wait limit; a load still running comes back as `job` |

- `reload` respawns at the last checkpoint like a death (no save write, no death wait). Always allowed.
- `next`, `prev`, `goto` are cheats: refused unless `play.cheats=1` is in editor.cfg (section 16). They write the new
  spawn into `INSIDE.sav` (the Steam Cloud save).
- With `wait` the tool waits for its own load (the game's load count must pass the one it issued, then the ready rule)
  and 20 settle frames, so the result is never the old level.
- With `wait:false`, or when the load runs past `timeout_s`, the result has `outcome:"loading"` and `job`. That job
  keeps the control lane until the game is ready, so actions and a plain `wait_until {any:[{ready:true}]}` answer
  `busy` meanwhile. Wait with `wait_until {any:[{job:"<id>"}]}` (it only watches).
- A `checkpoint` call does not choose step mode by itself; the clock keeps its mode.

Returns `op`, `outcome` (`done`, `loading`, `refused`), `current {subscene, savepoint, name, area, chapter,
chapter_name, guide}`, `checkpoints[]` (+ `reached`, `discovered`), `total`, `job`, `backup`, `warnings`, `observe`.

### 6.8 menu

The pause menu without a mouse. Works while the game is frozen (menus run on unscaled time).

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `op` | enum | `state` | `state`, `open`, `close`, `up`, `down`, `left`, `right`, `submit`, `back`, `select`, `message_answer` | |
| `id` | string | | | op select: item id from state (`m3`); ids change with every menu state |
| `repeat` | integer | 1 | 1..20 | navigation presses |
| `allow_destructive` | boolean | false | | show and allow Quit, chapter loads over progress, Reset To Defaults |
| `answer` | enum | | `ok`, `cancel` | op message_answer: ok (CONTINUE) or cancel (RETRY) |
| `confirm` | boolean | false | | op message_answer: required only with `play.save_guard` on |

- At the title ("press any button") `submit` starts the game.
- Quitting is never done: Yes in the quit confirm is refused even with `allow_destructive`. Raising the master volume
  is refused.
- Storage messages (Steam Cloud prompts) are answered only with `message_answer`.

Returns `outcome` (`done`, `refused`, `not_open`, `loading`), `open`, `screen`, `group`, `selected`, `items[] {id,
label, kind (button, slider, toggle, chapter, confirm, key, other), enabled, selected, destructive, value}`,
`hidden_destructive`, `chapter {index, name, unlocked}`, `warnings`.

### 6.9 screenshot (read-only)

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `mode` | enum | `preview` | `preview`, `marked`, `full`, `path` | preview = inline PNG; marked = preview with the latest observe ids drawn; full / path = file only |
| `max_width` | integer | 960 | 0..4096 | scale down to this width (0 = no scaling) |
| `clean` | boolean | true | | hide InsideDev panels, HUD and overlays for the shot |
| `fast` | boolean | false | | full / path: return once the file is written, without decoding it for the `black` check |

The result is a JSON text block `{ok, mode, width, height, src_width, src_height, path, uri, black, frame, marks[{id,
label, x, y}], camera {name, x, y, z, pitch, yaw, roll, fov}, boy {x, y, on_screen}, note}` plus the image. Image
results carry no `structuredContent`, so clients that pass only structured results still show the image. `black` flags
an almost black frame. Files go to `<game>\_mod\shots\` (the newest 200 are kept); `full` adds a resource link
`inside://shots/<file>` that `resources/read` returns. A minimized window gives `not_ready` ("the screenshot was not
written within 10 s").

### 6.10 geometry (read-only)

Occupancy slice of the level at the body's depth plane.

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `op` | enum | `slice` | `slice`, `ledges`, `probe` | probe = ground and ceiling under / over one point |
| `center` | string | the body | | object id or path |
| `x`, `y` | number | | -100000..100000 | world point instead of `center` |
| `width` | number | 16 | 2..64 | slice width (m) |
| `height` | number | 8 | 2..32 | slice height (m) |
| `cell` | number | 0.25 | 0.1..1 | cell size (m) |

Returns `origin {x, y, z}` (bottom-left cell), `cell`, `cols`, `rows`, `grid[]` (text rows, top row first: `#` solid,
`.` free, `~` water, `H` ladder, `|` rope, `B` body, `^` hazard, `?` not loaded), `ledges[] {x, y, side, climbable,
source}`, `probe`, `legend`. Use it before `move_to` to see pits, walls and ledges, and to debug a `blocked` outcome.

### 6.11 The `when` condition (act, move_to, interact)

`when` is one `wait_until` condition (keys `seconds`, `grounded`, `airborne`, `state`, `near`, `far_from`,
`hazard_safe`, `hazard_eta`, `music_time`, `fsm_state`, `signal`, `object_active`; not `job`). The game runs (or is
watched in realtime) until it holds, then the action starts in the same call, so no frame passes between the phase and
the input. The wait never uses the time the action needs. Not met within `when_timeout_s` (default 15, max 45, wall
clock): outcome `timeout`, no input sent.

```json
{"name": "move_to", "arguments": {"dx": 8, "when": {"hazard_eta": {"target": "h3", "min_s": 4}}}}
```

### 6.12 guide (read-only)

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `topic` | string | | | topic id (`forest/overview`) or `observe.checkpoint.guide`; empty = index |
| `level` | enum | `hint` | `hint`, `steps`, `solution` | how much to reveal |
| `section` | string | | | only this heading of the topic (a place name such as `riverWithDog`) |

Returns `topic`, `title`, `level`, `text` (Markdown), `related[]`, `index[] {topic, title, description, chapter}`
(without topic), `current_topic`, `sections[]`, `max_level` (when the user's cap lowered the level), `warnings`. An
unknown topic gives `not_found` with `candidates`; an unknown section gives `not_found` with `sections`. Section 9 has
the folder layout.

### 6.13 notes

Moments the user flagged with the notes hotkey (or added here).

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `op` | enum | `list` | `list`, `get`, `add`, `clear` | |
| `id` | integer | | 0..1000000 | op get |
| `text` | string | | at most 500 characters | op add |
| `limit` | integer | 10 | 1..50 | op list |
| `image` | boolean | false | | op get: attach the screenshot (scaled to 960 px) |

Returns `notes[] {id, time, frame, step, subscene, savepoint, checkpoint, pos, text, screenshot}` (newest first),
`total`, `hotkey`, `cleared`, `backup`, `image`, `warnings`. Section 10 has the details.

### 6.14 input_track

Record input during a step-mode rehearsal, save it, and play it back as one realtime take. Section 8 has the workflow.

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `op` | enum | `status` | `status`, `start`, `stop`, `save`, `load`, `list`, `info`, `trim`, `play` | |
| `name` | string | | 1..64 of letters, digits, `_ - .` (not starting with `.`) | track name; info and play default to the track in memory; start defaults to `track_<date>_<time>` |
| `from` | integer | 0 | 0..1000000 | trim: first step kept |
| `to` | integer | -1 | -1..1000000 | trim: last step kept, -1 = end |
| `overwrite` | boolean | false | | save: replace a file of that name |
| `speed` | number | 1 | 0.05..4 | play: realtime speed (1 for a take) |
| `lockstep` | boolean | true | | play: hold the rehearsal's 60 Hz lockstep (one logic step per frame, paced to real time); false = free realtime (game time follows the wall clock) |
| `correct` | boolean | false | | play: nudge the body toward the recorded path (off = input only) |
| `gain` | number | 0.05 | 0..1 | play: share of the drift removed per step |
| `max_step` | number | 0.02 | 0..0.5 | play: largest correction per step (m) |
| `stop_drift` | number | 0 | 0..100 | play: stop with `desynced` beyond this drift (m), 0 = never |
| `max_start` | number | 1.5 | 0..1000 | play: refuse when the body starts farther than this from frame 0 (m), 0 = no check |
| `then` | enum | `run` | `run`, `freeze` | play: afterwards keep running in realtime or go back to step mode |
| `wait` | boolean | true | | play: wait up to `timeout_s` for the end |
| `timeout_s` | number | 45 | 0..50 | play / stop: wait limit |
| `observe` | enum | `concise` | `concise`, `none` | play |

Returns `op`, `outcome` (`done`, `recording`, `stopped`, `saved`, `loaded`, `trimmed`, `playing`, `cancelled`, `died`,
`loading`, `stalled`, `desynced`), `stop_reason`, `recording`, `playing`, `track {name, body, subscene, savepoint,
checkpoint, steps, frames, seconds, forced_steps, forced_spans, gated_steps, dead_steps, grab_steps, gaps, path_m,
max_speed, trimmed, samples, saved ...}`, `tracks[]` (op list: `{name, steps, seconds, body, subscene, savepoint,
checkpoint, created, source, bytes}`), `playback {name, step, of, speed, correct, corrected, skipped, start_offset_m,
max_drift_m, max_drift_step, mean_drift_m, end_drift_m, max_shift_m, total_shift_m, drift_per_s[]}`, `job`, `file`,
`next`, `warnings`, `observe`. `samples` rows are `[k, x, y, vx, vy, flags]`, flags 1 gated, 2 dead, 4 input forced
by the game, 8 huddle grabbing, 16 no body.

### 6.15 restore (idempotent)

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `only` | string[] | all | 0..32 keys or key prefixes | only these registry keys (`input`, `time`, `camera`, `input_track`, `hazard:` ...) |
| `include_save_lock` | boolean | false | | also release a save lock taken through MCP |
| `keep_agent_input` | boolean | false | | keep AgentCore installed (neutral input) instead of giving control back |
| `observe` | enum | `concise` | `concise`, `none` | |

Returns `reset[] {key, what, result}`, `kept[]`, `errors[]`, `observe`. Section 13 lists what it puts back.

### 6.16 Prompts and resources

| Name | Kind | Content |
|---|---|---|
| `playtest_from_checkpoint` | prompt, arguments `subscene` (required), `savepoint`, `goal` | a ready plan: notes, guide, load, the loop, restore |
| `solve_here` | prompt, argument `goal` | explain the current place with as few spoilers as possible |
| `inside://guide/index` | resource | the playbook index (Markdown) |
| `inside://guide/<group>/<topic>` | resource (one per topic, listed at load time) | one topic at hint level; `?level=steps`, `?level=solution`, `?section=<heading>` |
| `inside://shots/{file}` | resource template | full-size PNG of an earlier screenshot |

## 7. Workflows

### 7.1 From the title to play

```json
{"name": "observe", "arguments": {}}
{"name": "menu", "arguments": {"op": "submit"}}
{"name": "wait_until", "arguments": {"any": [{"ready": true}], "timeout_s": 45}}
{"name": "observe", "arguments": {}}
```

`observe.next` names the call when the game shows the pause menu (`menu {op:"close"}`), a storage message or the credits.

### 7.2 Death and retry

Results stop with `outcome:"died"` (act, move_to, interact). Then `wait_until {any:[{alive:true}], timeout_s:30}`, or
`checkpoint {op:"reload"}` to skip the death wait. Ids are gone after the respawn: `observe` again. After two deaths at the
same checkpoint `stuck.hint` suggests a change of approach; read the guide topic at level `steps`.

### 7.3 Boxes and devices

```json
{"name": "move_to", "arguments": {"target": "o14"}}
{"name": "interact", "arguments": {"verb": "grab", "target": "o14"}}
{"name": "move_to", "arguments": {"dx": -3}}
{"name": "interact", "arguments": {"verb": "release"}}
```

The grip from `grab` keeps action pressed through `move_to` and `act` until `release`, `act {release:true}`, a cancel, a
death, a respawn or a load. Levers, valves, hatches, boards, lift doors and buttons have their own verbs (6.4).

### 7.4 Music-timed places

Use `when` so the action starts in the right phase:

```json
{"name": "observe", "arguments": {}}
{"name": "act", "arguments": {"inputs": [{"stick": [1, 0], "steps": 180}], "when": {"hazard_eta": {"target": "h3", "min_s": 4}}, "stop_on": ["death", "load", "hazard"]}}
```

Do not use slow motion there. Step-mode runs are paced to real time; when the game renders below 60 fps the warning
`paced: ... late` says game time fell behind the music. `time {op:"realtime"}` at speed 1 also keeps it in sync.

### 7.5 A person and the agent together

`act {input_mode:"mix", inputs:[]}` merges the agent's input with the physical pad. `act {input_mode:"human"}` gives
the controls back without the rest of `restore`. To let the game run while you watch: `time {op:"realtime"}`.

### 7.6 Loads started outside the play tools

Loads through console commands, mods or the dev profile do not wait for themselves. `wait_until {ready:true}` holds at
once while the old level is still up, before the new load has even started. Wait for the load first, then for ready, and
check `observe.checkpoint` against the target:

```json
{"name": "wait_until", "arguments": {"any": [{"loaded": false}], "timeout_s": 10}}
{"name": "wait_until", "arguments": {"any": [{"ready": true}], "timeout_s": 45}}
```

### 7.7 Film a planned camera over live play

1. Rehearse and record the take as an input track from a checkpoint (section 8). Record it once more with the scene
   recorder (`srec record start <dir> spec=<spec> clock=fixed:60`) while the track plays.
2. Plan the cameras: `mcam passes <spec> <dir>` writes `<dir>\passes\poses.csv` and `meta.csv` (no rendering).
3. Reset to the same start: the same checkpoint, loaded once; release the free camera (`cam off`), it blocks the input.
4. `time {op:"vsync", on:true}`, then `grec start <dir>\director.mkv`; wait until `grec status` says `recording`.
5. `campath load <dir>\passes director`, `campath arm`, then `input_track {op:"play", name:"<track>", lockstep:true,
   then:"run", wait:false}`. Read `grec status` right after `campath status` turns `running`: that grab count is the number of
   frames before the first pose.
6. When `campath status` is back to `idle`: `grec stop`, wait for `done`, then `restore`.

A second camera of the same plan is the same run with another camera name; `rt` keeps the game at speed 1 for cameras that
should not pause it.

## 8. Input tracks (rehearse, then take)

Purpose: rehearse a scene in step mode (as slowly as needed), keep the exact input of every logic step, then play that
input back as one realtime take, for example while a recording runs.

1. Go to the start: `checkpoint {op:"reload"}` (or a cheat `goto`), then `wait_until {any:[{ready:true}]}`. Optional:
   `time {op:"rng_seed", seed:1}` for the same random seeds in rehearsal and take.
2. `input_track {op:"start", name:"take1"}`. Every logic step from now on is recorded: the stick and buttons that reached
   the game, the body position and velocity before the step, the game time and flags.
3. Rehearse with `act`, `move_to`, `interact`, `wait_until` as usual.
4. `input_track {op:"stop"}`, `{op:"info"}` (counts, `forced_spans`, up to 60 samples), `{op:"trim", from, to}`
   (keeps a step range and renumbers it from 0), `{op:"save", name:"take1"}`.
5. Take: load the same checkpoint, wait for ready, the same seed, then
   `input_track {op:"play", name:"take1"}`. With `wait` (default) the call returns when the track ends or after
   `timeout_s` (45 s); otherwise it returns `outcome:"playing"` with `job`: repeat
   `wait_until {any:[{job:"<id>"}], timeout_s:50}` until satisfied. Read `playback.max_drift_m` and `drift_per_s`.
6. Stop early with `input_track {op:"stop"}` or `restore`.

Rules and limits:

- Recording also captures the pad in mix mode (set `act {input_mode:"mix", inputs:[]}` before `start` to record a
  person). `start` installs the agent core in agent mode unless mix was chosen.
- A load start ends a recording ("a load started"); the frames so far stay in memory. A recording holds at most 72000
  steps (20 min).
- Play refuses when the controlled body kind differs from the track's (boy / huddle) or when the body is more than
  `max_start` (1.5 m) from the first frame; the refusal names the checkpoint to load.
- Play puts the clock in realtime at `speed`, holds the 60 Hz lockstep and arms the track; the next logic step plays
  frame 0. Input becomes neutral after the last step.
- Ends: `done`, `cancelled`, `died`, `loading`, `stalled` (600 frames without a logic step: pause menu or focus),
  `desynced` (`stop_drift`).
- `correct:false` (default) plays input only and measures the drift. `correct:true` moves the body a share (`gain`) of
  the error per step, at most `max_step` m (boy: only in run, jump or swim); the result warns `correction_on`.
- Physics, crowds and particles differ per run, so long open-loop tracks drift; observed in tests: 0.7 to 2.7 m at most
  over a 35 s huddle take without correction, 0.42 m with it.
- Lockstep runs one logic step per rendered frame: when the game renders below 60 fps the take plays slower than real
  time (about 0.6x at 36 to 42 fps in tests); game time per step stays exact. `lockstep:false` keeps the game's own
  timing at any frame rate (use it when sound must follow the wall clock).
- A playback is a detached job: the host cancels detached jobs after 10 minutes, so a take longer than 10 minutes is
  cut. A scene recorder that runs during play owns the frame pacing (the take follows its clock).
- Files: `<game>\_mod\input_tracks\<name>.json`. Line 1 is `{"header":{format "insidedev.input_track", version 1,
  name, created, body, subscene, savepoint, checkpoint, clock, source, load_count, start_step, start_time, step_s,
  steps, frames, seconds, trimmed}`, line 2 the columns `k, t, sx, sy, b, px, py, pz, vx, vy, vz, f` (b = 1 jump + 2
  action), then one array per step. A 40 s track is about 150 KB.

## 9. Guide playbooks

The `guide` tool reads Markdown playbooks: `<root>/<group>/<topic>/SKILL.md`, `<root>/index.md` and `<root>/map.tsv`
(subscene index to scene name). The release ships topics for every area (forest, farm, city, sewers, offices, mines,
lab, huddle), mechanics (controls, climbing, death, huddle, mind_hats, objects, shockwave, submarine, water), safety
(rules, timing) and secrets/orbs, plus two example scenarios in `guide\scenarios\`.

- Root: environment variable `INSIDEDEV_GUIDE_DIR`, else `play.guide_dir` in editor.cfg, else `<game>\_mod\guide`.
  A missing folder makes `guide` return an empty index with a warning.
- Levels: `## Hint`, `## Steps`, `## Solution` sections; other `##` sections show at every level; `###` sub-sections are
  places (use them as `section`).
- Frontmatter keys: `name`, `description`, `title`, `chapter`, `order`, `related`, `subscenes` (for example
  `3:0-2, 4`), which route a savepoint to its topic (`observe.checkpoint.guide`).
- `play.guide_max` (`hint`, `steps` or `solution`, default `solution`) caps what the tool reveals; results then carry
  `max_level` and a warning.
- Edited files are picked up on the next call (the folder is checked every 3 s); the resource list is built when Play
  loads.

## 10. Notes (flagged moments)

The user presses the notes hotkey (Home by default) in the game to flag a moment. A note stores time, frame, step,
subscene, savepoint, checkpoint name, body position, text and a screenshot. The newest 50 are kept in
`<game>\_mod\notes.jsonl`; screenshots go to `<game>\_mod\notes\note_<id>.png` (deleted when their note drops out of
the 50).
`notes {op:"clear"}` moves the file to `notes_cleared_<date>_<time>.jsonl`. The hotkey is ignored while an InsideDev text
field has the keyboard. `play.notes_key` takes a Unity KeyCode name (`F12` is Steam's screenshot key; InsideDev uses F1 to
F11, Insert, Delete, brackets, backslash, backquote, minus / equals, keypad + / - / 0, P); `none` or `off` switches the
hotkey off. An unknown name is logged and the old key stays.

When the user says "here" or "this spot", read `notes {op:"list"}` first; `notes {op:"get", id, image:true}` attaches the
screenshot.

## 11. `ctl` console commands

Plain-text control of the boy and the huddle for a person or a script, without an MCP client. `ctl` is a console
command, not an MCP tool. Reachable from:

- the in-game console (backquote key);
- the file bridge: write `<game>\_mod\cmd\in_<anything>.txt` with `<id>|ctl run right 1.2`, read the answer in
  `<game>\_mod\cmd\out.txt` (between `>>> <id>` and `<<< <id>`; the id is at most 11 characters);
- `POST http://127.0.0.1:47811/cmd` with the command as the text body (section 12);
- the dev profile's `command` tool: `command {command:"ctl run right 1.2"}`.

Durations `<t>`: a plain number is seconds (`1.2`); a whole number followed by `s` is logic steps (`72s` = 1.2 s;
`1.5s` is an error). An optional `for` may precede it. At most 600 s (36000 steps).

| Command | Does |
|---|---|
| `ctl` / `ctl status` | the control lane owner, plus the body line |
| `ctl status json` / `ctl state` | the concise observe JSON on one line |
| `ctl stick <x> <y> [for <t>]` | stick (x right +, y up +; each -1..1, magnitude 0 or 0.3..1); without `<t>` it is held |
| `ctl press <jump\|action\|grab ...> [for <t>]` | buttons (`grab` = action; `j`, `a`, `g` also work); without `<t>` held |
| `ctl tap <button ...> [<t>]` | a tap: pressed 5 steps, released on the 6th; with `<t>` pressed that long |
| `ctl hold <left\|right\|up\|down\|stick <x> <y>\|jump\|action\|grab ...>` | add to the held input (directions add up: `hold up right`) |
| `ctl release [all \| stick\|jump\|action\|grab ...]` | release everything, or only those parts (a direction word releases the stick) |
| `ctl run left\|right [<t>]`, `ctl walk left\|right [<t>]` | stick x = 1 / 0.5; without `<t>` held |
| `ctl jump [left\|right] [<t>]` | a jump tap; with a direction a running jump (stick that way for `<t>`, default 0.5 s, jump pressed the first 5 steps) |
| `ctl grab on\|off` | hold / release action |
| `ctl swim <dir> [<t>]` | dir: left, right, up, down, up-left, up-right, down-left, down-right (ul, ur, dl, dr) |
| `ctl goto <x> [<y>] [walk\|run]` | the `move_to` navigator to world x (y for swim / climb targets), stop exact, at most 1800 steps |
| `ctl stopat <x> [walk\|run]` | plain stick toward x, no jumps or climbs; the stick goes neutral in the step that crosses x (held buttons stay); 60 s at most, else `not_reached` |
| `ctl seq "<script>"` | an input script as one job (grammar below) |
| `ctl track rec [name] \| stop \| save <name> [overwrite] \| play [name] [speed <k>] [freeze] [correct\|nocorrect] [free] \| list \| info [name] \| status \| load <name> \| trim <from> [<to>]` | the `input_track` ops; `play` runs as a job; `free` = lockstep off; `stop` during a playback cancels it |
| `ctl time [state] \| freeze \| run [<speed>] \| step <n> \| speed <k>` | `freeze` = step mode, `run` = realtime at the current or given speed (0.05..4), `step` = 1..600 logic steps (a job) |
| `ctl mode agent\|mix\|human` | the input mode; human gives the pad and keyboard back (the next ctl input takes over again) |
| `ctl job [jN]` | the state of the last (or that) ctl job |
| `ctl stop` | cancel the running ctl job and release all input (an MCP job is not touched: busy) |
| `ctl help` | the command list |

`seq` grammar: segments separated by spaces, commas or semicolons; atoms of one segment joined by `+` start together and
the segment lasts as long as its longest atom; at most 500 segments and 600 s.

| Atom | Meaning |
|---|---|
| `R<t>[@m]`, `L<t>[@m]`, `U<t>[@m]`, `D<t>[@m]` | stick right / left / up / down for `t`, strength `m` 0.3..1 (default 1); sticks of one segment add up (`R1+U1` = diagonal, length capped at 1) |
| `J[<t>]` | jump: without `t` a tap (5 steps pressed + 1 released), with `t` pressed for `t` |
| `A[<t>]` | action, the same way (a tap under 0.3 s is the game's action, held is grab) |
| `G<t>` | grab: action held for `t` |
| `W<t>` | wait: no input of its own for `t` (held input from `ctl hold` stays) |

`ctl seq "R1.2 J R0.4+J G1 W0.5 L0.3"` = run right 72 steps; a jump tap (6); run right 24 steps with a jump tap at its
start; grab 60; wait 30; run left 18 (210 steps). A jump still pressed at the end of a segment and pressed again at the
start of the next is one press: the reply warns `put W0.1 before it`.

Behaviour:

- Held input is the agent core's latch (the same one `act {hold:true}` sets). Timed commands add the held buttons to every
  step and use the held stick where they give none (`ctl grab on`, then `ctl run left 1` pulls a box); the held input
  continues right after them. `ctl hold` adds to what is held: release first when you want only the new input.
- Clock: ctl never freezes the game on its own. An untouched or running realtime clock stays realtime (the game keeps
  running for a person watching). Step mode stays step mode: the game is frozen between commands and a timed command
  advances it for exactly its steps. Untimed commands (`hold`, untimed `stick` / `run`) in step mode only latch: nothing
  moves until a timed command or `ctl time run`.
- Every input command installs the agent core (agent mode unless mix was chosen). Not ready (loading, title, paused,
  dead, no body) answers `failed: not ready: ...`; cutscenes and scripted states only warn.
- Timed commands start a job and answer at once: `started: <what> job=jN; ctl job jN shows it, ctl stop cancels it`.
  The console log gets `ctl jN <end line>` when it ends. Any end other than done releases all input.
- ctl and MCP actions share the control lane: while one runs the other answers `busy: <tool> is running (job jN)`.
- Replies are two lines, the result and the body line:

  ```
  done: run right for 72 steps (1.2 s)
  body: boy b1 x=412.31 y=12.08 vx=3.5 vy=0 state=Run grounded grab=- stick_forced=no | clock=step frozen | input=agent held=stick:1,0+action
  ```

  The first word is the outcome: `ok:`, `started:`, `running:`, `done:`, `died:`, `loading:`, `cancelled:`, `stalled:`,
  `timeout:`, `not_reached:`, a move_to outcome (`arrived:`, `blocked:` ...), or an error: `busy:`, `usage:`,
  `unknown ...`, `refused:`, `not found:`, `failed:`. `help` and `status json` have no body line.
- Observed in tests: the huddle has no jump (jump only leans it forward); `ctl stopat` lets the huddle coast about 3 m
  past x at 5 m/s, so use `ctl goto` for exact stops.

## 12. `tools/inside_ctl.py`

Command-line control through `POST /cmd` on the game's port, with the file bridge as a fallback. Python 3, standard
library only. Options go before the command.

```
python tools\inside_ctl.py ctl status
python tools\inside_ctl.py ctl run right 1.2            follows the job until "done: ..."
python tools\inside_ctl.py --no-wait ctl goto 412.5     prints "started: ... job=jN" only
python tools\inside_ctl.py seq "R1.2 J R0.4+J G1 W0.5 L0.3"
python tools\inside_ctl.py state                        the concise observe JSON
python tools\inside_ctl.py ping                         "pong InsideDev <version>"
python tools\inside_ctl.py repl                         inside> prompt, one command per line, quit or exit ends
python tools\inside_ctl.py cmd state                    sends the words as they are (a console command named state, seq, repl, ping or cmd)
python tools\inside_ctl.py --game "C:\path\to\INSIDE" ctl status
```

| Option | Default | Meaning |
|---|---|---|
| `--port N` | 47811, env `INSIDE_MCP_PORT` | the game's MCP port |
| `--host H` | 127.0.0.1 | |
| `--token T` | env `INSIDEDEV_MCP_TOKEN` | bearer token when the game has `mcp.token` |
| `--bridge DIR` | env `INSIDE_BRIDGE` | the bridge folder (`<game>\_mod\cmd`) |
| `--game DIR` | env `INSIDE_GAME` | the INSIDE folder; the bridge is then `<game>\_mod\cmd` |
| `--http-only` / `--bridge-only` | | use only one path |
| `--no-wait` | | do not follow a started ctl job |
| `--timeout S` | 120 | how long to follow a job |

- Any other first word sends the whole line as one console command (`ctl ...`, `boypos`, `help` ...).
- When nothing listens on the port (or the game's `InsideDev.Mcp.dll` has no `/cmd`), the command goes through the file
  bridge. The bridge fallback needs a folder from `--bridge` or `--game` (or their environment variables); there is no
  built-in default folder. A failure after the request was sent is never retried through the bridge (the command may
  have run).
- A reply `started: ... job=jN` is followed with `ctl job jN` until it ends. Ctrl+C while following sends `ctl stop`.
- Exit codes: 0 ok; 1 the command failed (busy, usage, refused, failed, not found, unknown ...); 2 the game is not
  reachable, or the request may have run but no reply came back.
- `state` uses `GET /state` (through the bridge: `ctl status json`); `ping` uses `GET /ping`.

Commands sent through `POST /cmd` count as MCP tool calls (they run through the `command` tool), so the idle restore
clock (section 13) restarts with each one. Commands through the console or the file bridge do not count.

## 13. Restore

`restore` (and the same routine run by the host) puts back what MCP changed:

| Key | Put back |
|---|---|
| job | the running action, ctl job or track playback is cancelled (not a MultiCam capture) |
| `input` | held input and a grab grip released; the agent core removed (human control), `runInBackground` as before |
| `time` | realtime, speed 1, unfrozen; `captureFramerate`, vSync and `runInBackground` as before |
| `vsync` | the game's own vSync after `time {op:"vsync", on:false}` |
| `record` | a running recording stopped (the file stays) |
| `campath` | the director camera stopped; field of view, near plane, game speed and freeze put back |
| `grec` | a game-render video stopped and its file closed |
| `input_track` | playback cancelled, recording stopped (the track stays in memory) |
| `camera` | a camera override released |
| others | stream pins, god, air, hazards, menu lock, title hide, overlay text, render and audio changes, reflect writes with `restore_on_end` (dev tools) |
| `save_lock` | kept unless `include_save_lock:true` |

The host runs the same restore by itself:

| Trigger | When |
|---|---|
| idle | `mcp.idle_restore_s` (default 600) seconds without any MCP tool call, after at least one call since the last restore, while no call and no control job runs; 0 = never |
| last session closed | `mcp.restore_on_close_s` (default 60) seconds after the last MCP session was closed by DELETE, with still no session, no call in flight and no control job; 0 = never. The stdio program sends DELETE when its client exits |
| `mcp off`, `mcp restore` (console), game quit | at once |

`play restore` in the console runs the Play part only.

## 14. Errors and busy

| Code | Meaning | What to do |
|---|---|---|
| `bad_args` | arguments do not match the schema | read the path in the message |
| `busy` | another action, a ctl job or a MultiCam capture holds the control lane; `busy {job, tool, lane, running_s}` | `wait_until {any:[{job:"<id>"}]}`, or `restore` |
| `not_ready` | the main thread did not run the request in time (minimized window, long load), or no body | restore the window, wait for the load; nothing was run |
| `stale_id` | the id is from before the last load | `observe` again |
| `not_found`, `ambiguous` | no such object / several matches (`candidates`) | use an id or a full path |
| `refused` | a safety rule (cheats off, quit, master volume up, secret devices) | read `hint` |
| `confirm_required` | only with `play.save_guard` on | repeat with `confirm:true` |
| `timeout` | the call ran past its budget; with `job` it continues | wait for the job |
| `cancelled` | cancelled by the client or by restore | |
| `game_error` | the game threw | read the message; the InsideDev log has the stack |
| `not_built`, `internal` | a missing part or a bug | report it |

Read-only tools (`observe`, `screenshot`, `geometry`, `guide`, `notes list`, `time state`) take no lane and work while an
action runs.

## 15. Pitfalls

- **Minimized window.** A minimized INSIDE window stops the game loop (observed in tests, even with `runInBackground`
  on). Calls that need the main thread fail after their wait with `not_ready` ("the main thread is not running ...";
  screenshots: "not written within 10 s"); a running action returns `timeout`; the file bridge does not answer. `GET
  /ping` still answers, so it does not prove the game runs. Keep the window restored; it may stay behind other windows.
- **Unfocused window before the first action.** Without `runInBackground` the game pauses itself when its window loses
  focus (`focus_lost` warning). The first action sets `runInBackground` and switches the focus layer back on.
- **Step mode freezes the game.** After the first action the game stands still between calls. A person watching sees a
  frozen frame; use `time {op:"realtime"}` or `restore`.
- **Your controls stop working.** In agent mode the physical pad and keyboard are ignored until `restore`,
  `act {input_mode:"human"}` or `ctl mode human`.
- **Idle restore.** 10 minutes without a tool call puts everything back and stops an input track recording. Keep calling
  tools during a long rehearsal, or raise `mcp.idle_restore_s` (section 16). Console and bridge ctl commands do not
  reset this clock.
- **Ids die on every load and every respawn.** Use ids only from the latest `observe` or action result.
- **Load races.** `checkpoint` with `wait` waits for its own load. Loads started any other way, or `wait:false`, need
  section 7.6: `wait_until {ready:true}` alone can be satisfied by the old level. Do not start an input track or a
  recording before the requested savepoint is playable (`observe.checkpoint`).
- **Music-timed places and speed.** Slow motion desyncs them; step-mode runs there are paced to real time. Use `when`.
- **Free camera.** While the InsideDev free camera is on (Insert key, or the dev `camera pose`) the body is not
  controllable; release it before acting.
- **Keyboard touched.** `controller` becomes Digital and several moves read input differently (`controller_digital`).
- **Menus suppress buttons.** After a menu, send one neutral step before a jump or action.
- **Busy from outside.** A ctl job, a dev-profile action or a MultiCam capture (a separate InsideDev feature) holds the
  control lane; `restore` does not cancel a MultiCam capture (console `mcam stop` does). Dev tools such as `command` do
  not take the lane and can change time or load while a play action runs.
- **Long tracks.** Detached jobs end after 10 minutes, so a track playback longer than that is cut.
- **Saves.** Playing forward writes `INSIDE.sav` (the Steam Cloud save) at checkpoints exactly as a person playing does.
  `checkpoint next / prev / goto` write the spawn. Back up the save first if it matters (the dev `save {op:"backup"}` or
  a copy of the file). Tools never call Steam achievements directly, but the game's own code can award them during play,
  as for a person.
- **Director camera and the free camera.** `cam pose` (the free camera) takes the input away from the boy, so an input
  track played under it drives nothing: `cam off` before a `campath` run.
- **`grec` speed.** The file runs at 60 / `every` frames a second; with vSync off the game renders faster and the video
  plays slow. Turn vSync on for a real-time film (section 4.6).
- **Updating Play.** `ext load` does not replace a loaded DLL: restart the game after copying a new one.

## 16. Settings

Settings are `key=value` lines in `<game>\_mod\editor.cfg` (lines starting with `#` are comments). The game reads the file
once at start and rewrites all of it when any editor setting changes, so edit it with the game closed. Only `mcp.enabled`
and `mcp.port` have console commands.

| Key | Default | Effect | Change |
|---|---|---|---|
| `mcp.enabled` | `1` | `0` = the MCP server does not start | console `mcp on` / `mcp off` (saved) |
| `mcp.port` | `47811` | listening port (127.0.0.1 only) | console `mcp port <1024-65535>` (saved, restarts the server) |
| `mcp.token` | empty | when set, every MCP request and `/cmd`, `/state` need `Authorization: Bearer <token>` | editor.cfg |
| `mcp.tools` | empty = all | tool allowlist: names, `<profile>:*`, `<profile>:<tool>` or `*`, separated by commas, semicolons or spaces; others are hidden and refused | editor.cfg |
| `mcp.idle_restore_s` | `600` | restore after this many seconds without a tool call; 0 = never | editor.cfg |
| `mcp.restore_on_close_s` | `60` | restore this many seconds after the last session closed; 0 = never | editor.cfg |
| `play.cheats` | off | `1` allows `checkpoint next / prev / goto` | editor.cfg |
| `play.save_guard` | off | `1` turns on the save gates: a backup of `INSIDE.sav` to `_mod\save_backups\` before the first save-writing load, `confirm:true` for loads that write the save or unlock chapters and for `menu message_answer`, refusals of console loads through MCP | editor.cfg |
| `play.guide_dir` | `<game>\_mod\guide` | playbook folder (the environment variable `INSIDEDEV_GUIDE_DIR` wins) | editor.cfg |
| `play.guide_max` | `solution` | spoiler cap: `hint`, `steps` or `solution` | editor.cfg |
| `play.notes_key` | `Home` | notes hotkey (Unity KeyCode name; `none` or `off` = no hotkey) | editor.cfg |

Example `editor.cfg` lines:

```
mcp.idle_restore_s=1800
play.cheats=1
play.guide_max=steps
```
