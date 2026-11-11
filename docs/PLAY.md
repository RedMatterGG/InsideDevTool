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
6. Tool reference (15 tools)
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
type `ext load` in the InsideDev console (backquote key; since 0.11.0 the console hands words it does not know itself,
such as `ext`, `mcp`, `grec` or `srec`, to the bridge and prints the reply) or send it through the file bridge: it loads
DLL files that are not loaded yet. Copy both DLLs before the first `ext load`: a file that failed to load (Play without
Mcp) is remembered and tried again only after a game restart. Replacing a DLL that is already loaded needs a game restart.

Check in the console:

| Command | Shows |
|---|---|
| `mcp` | server status: endpoint, build line (`build: mcp 0.11.0+<id>, play 0.11.0+<id>, core 0.11.0`), tool counts (play 15, dev 34, all 41), control lane, allowlist, whether a token is required |
| `play` / `play status [json]` | `Play (MCP play/world tools: <n>, problems: <n>)`, the ticks (`ticks: 16 (running now: 2: stepper, input)`), the time since the last agent call, problems; `json` gives one line `{ok, build, tools, ticks, ticks_running[], ticks_disabled[], in_use, idle_s, last_use, clock, clock_touched, input{}, track{}, save_guard, restore_keys[], problems[]}` |
| `doctor` | one self-check of every part; the rows of part `play` (and `film`, `play.perception`) belong to this document |
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
| Event hooks | passive | Play listens to load, death and log events; object scans run only while a client was active in the last 90 s. Play's per-frame ticks sleep while nobody uses Play (`play status` lists the ones running) | none |
| vSync | the game's own | `play.vsync_off=1` (default) switches vSync off only while an agent's tool calls arrive (30 s after the last one) and while the game clock is held (from the first action or timed ctl command until `restore`). A game that only has the DLLs installed keeps its own vSync | `play.vsync_off` in editor.cfg (section 16), `vsync on / off / auto` (section 4.5) |

Off by default, opt in through `<game>\_mod\editor.cfg` (section 16): `play.cheats` (checkpoint jumps), `play.save_guard`
(save backups and confirms), `mcp.token`, `mcp.tools` (allowlist), `play.guide_max` (spoiler cap), `play.hold_timeout_s`
(release held agent input after a quiet time).

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
- The play profile serves 15 tools: `observe`, `act`, `move_to`, `interact`, `wait_until`, `time`, `checkpoint`, `menu`,
  `screenshot`, `geometry`, `guide`, `notes`, `input_track`, `restore`, `film` (new in 0.11.0, section 6.17). Resources: `inside://guide/index`,
  `inside://guide/<group>/<topic>` (template `inside://guide/{chapter}/{topic}`), `inside://shots/{file}`. Prompts:
  `playtest_from_checkpoint` (subscene, savepoint, goal), `solve_here` (goal).
- `/mcp/all` serves the play and dev tools together (41 tools; Cursor's cap is 40, so hide one with `mcp.tools` there).
  `/mcp` and `/mcp/dev` serve the dev profile (34 tools), which has no `act`, `move_to`, `interact`, `checkpoint`, `menu`,
  `guide` or `notes`; `film` is on both.
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
- Step mode sets `captureFramerate` 60 and vSync 0 while a run is active and puts them back after it. Outside the runs
  the vSync rule of `play.vsync_off` applies (4.5): with the default 1, vSync stays off while an agent's calls arrive and
  while the clock is held, and comes back at `restore`.
- `play.step_pacing` (editor.cfg, default `auto`) paces step-mode runs: `auto` = as fast as the game renders, paced to real
  time only in music-timed places (below); `always` = every run at 60 steps per second (watchable, the sound keeps up);
  `never` = no pacing. Track playback in lockstep is not affected.
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
  reused for another object. A respawn after a death is a load. Since 0.11.0 an id survives a load when its object
  exists again at the same hierarchy path afterwards (a respawn, a checkpoint reload of the same place): the old id
  resolves again, and a new observation gives the object its old id back once the core object index has finished a
  pass that began after the load (before that the object gets a new id and the old one still resolves; both name the
  same object). Several same-named siblings at one path are never carried over. The first use of an old id right after
  a load can cost one or two passes of the object index.
- An id that cannot be resolved fails with `stale_id` and one of these messages: `o12 is from before the last load and
  its object is gone (<path> is not loaded now, or several objects share that path)`, `o12 was destroyed and is gone
  (...)`, `o12 is from before the load that is running now` (hint: `wait_until {any:[{ready:true}]}`), or `o12 is from
  before the last load` (ids older than 3 loads, or beyond 4000 retired ids). Call `observe` again.
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
  `menu {op:"close"}` when paused, `wait_until {any:[{alive:true}]}` after a death, `wait_until {any:[{ready:true}]}`
  while loading, `menu {op:"message_answer", ...}` for a storage message). Since 0.11.0 every hint is a valid call as
  written (0.10.0 wrote the short form `wait_until {alive:true}`, which the schema refuses).
- `act`, `move_to` and `interact` take `wait_ready_s` (0..45, default 0). With 0 they answer at once when the game does
  not take input (`not_controllable`, `loading`, `not_ready`, as before). Above 0, when the reason passes by itself
  (loading, a pending load, a fade, streaming, preawake, input disabled, no body, dead), the call lets the game run until
  it is ready on two steps in a row, for at most that many seconds of wall clock, and then acts in the same call. The
  title screen, the pause menu and a storage message are not waited for (they need a call of their own). The result
  has `wait_ready {ready, waited_steps, elapsed_ms, reason}`; still not ready = the old answer with `wait_ready.reason`.
  Example: `act {seq:"R1", wait_ready_s:10}`, `move_to {dx:3, wait_ready_s:8}`.
- Arguments are checked strictly against the tool's input schema (`bad_args` with the path, for example
  `inputs[2].stick: expected 2 numbers`). A string where a number, boolean, array or object is expected is parsed when
  possible and reported in `warnings` as `coerced: ...`.

### 4.5 Speed: frame meter, vSync and real-time recording

The recorder and the fast screenshot run only when called, and `restore` stops a recording.

| Need | Use | Why |
|---|---|---|
| know how fast the game renders | `time {op:"state"}`: `fps`, `frame_ms_max` (console `vsync status`) | frames of the last second and the longest of them |
| realtime without the 30 fps drop | `time {op:"vsync", on:false}` (console `vsync off`) | with vSync on, a frame a little over 16.7 ms halves the rate; restore key `vsync` |
| watch or review a pass | `time {op:"record"}` ... `time {op:"record_stop", review_fps:5}` (console `rec start`, `rec stop 5`) | video of the game's own frames; no per-step screenshots |
| one still, quickly | `screenshot {mode:"path", fast:true}`, or `screenshot {format:"jpeg"}` for a small preview | skips the decode for the black check |

**vSync rule.** `play.vsync_off` in `editor.cfg` decides what Play does with vSync. Changed in 0.11.0:

| Value | Effect |
|---|---|
| `1` (default) | vSync is off while an agent works (a client's play tool call in the last 30 s) and from the moment an agent or a timed ctl command takes the game clock until `restore`; otherwise the game keeps its own vSync. A game that only has the tool installed is never touched. `restore` gives vSync back at once. |
| `0` | Play never touches vSync (step-mode runs still switch it off for their own frames and put it back). |
| `2` | Off from game start, as in 0.8.0 to 0.10.0 (the old meaning of `1`). Set `play.vsync_off=2` to get the old behaviour back. |

An explicit switch wins over the rule in both directions until `vsync auto` or `restore` (restore key `vsync`). A
step-mode run, a scene recorder and a MultiCam capture keep their own pacing; a switch made meanwhile applies when they end
and says so.

| Console / bridge | MCP | Reply |
|---|---|---|
| `vsync on` | `time {op:"vsync", on:true}` | `vsync: on, the game's own 1 (wins over play.vsync_off until vsync auto, vsync off or restore)` |
| `vsync off` | `time {op:"vsync", on:false}` | `vsync: off (the game's own value 1 comes back with vsync on, vsync auto or restore)` |
| `vsync auto` | (`restore`) | `vsync: play.vsync_off=1: off while an agent works or the clock is held; now 1` |
| `vsync` / `vsync status [json]` | `time {op:"state"}` | `vsync 0 (held off), fps 143.2, longest frame 9.1 ms   (vsync off \| on \| auto; rule: ...)`; json `{"ok":true,"vsync":0,"rule":"...","held_off":true,"override":"none","fps":143.2,"frame_ms_max":9.1,"note":""}` |

Anything else answers `usage: vsync [off | on | auto | status] [json]`. `time` results carry `vsync`, `vsync_off`,
`vsync_always_off` (rule 1 or 2), `vsync_rule` (0, 1, 2), `vsync_override` (`none`, `on`, `off`) and `vsync_held_off`.
For a real-time film that a person plays there is nothing to do (no agent, vSync is the game's own); with an agent driving,
`vsync on` keeps the game's vSync.

**Real-time recording (`time {op:"record"}`, `rec`).** Changed in 0.11.0: the default source is the game's own frames.
The start is handed to `grec` (section 4.6), so windows over the game, focus and other programs never reach the file
(tested with a window covering the game: none of it in the video). `record_fps` picks every n-th rendered frame that gives
about that rate (`every = round(frame rate / fps)`: the capture rate, the stepper's 60, else the measured frame rate);
`every` sets n directly. Without `every` the file runs at `record_fps` (passed to grec as `fps=`), frames placed by game
time (section 4.6), whatever the game renders; with `every` the file runs at grec's own rate for it (60 / n in real-time
play). The
file is `<game>\_mod\recordings\grec_<yyyyMMdd_HHmmss>.mkv` unless `file` names one (a bare
name only; it goes to the same folder). While it runs, `rec status`, `rec stop`, `record_stop` and `restore` follow that
grec recording. It needs what grec needs (the readback plugin) and is refused while an mcam pass holds the readback
(`busy: the frame readback is held by mcam ...`) or grec already runs (`busy: the frame readback is held by grec (...); grec
stop first`).

The 0.10.0 desktop capture is opt-in: `time {op:"record", source:"desktop"}` (result warning `desktop capture: windows over
the game will be in the video ...`) or `rec start ... desktop`. It runs ffmpeg with `ddagrab` (DXGI Desktop Duplication:
the GPU copies the screen, the game's main thread does nothing) on the game window's client area. The window and its
monitor are found from inside the game in physical pixels; the DXGI output is the one of the default graphics adapter that
is the window's monitor (matched by monitor handle, else by desktop rectangle), so two monitors of one size are told apart;
`output=<n>` (0..15) picks it by hand when the monitor cannot be identified, and two outputs of one size are refused then
("pass output=<n>"). Desktop duplication records what is on the screen: a window over the game is recorded too, so `raise`
lifts the game window to the top without taking focus, and the state notes when another window covers the centre of the
picture at the start. The file is `<game>\_mod\recordings\rec_<yyyyMMdd_HHmmss>.mkv` (was `<date>_<time>.mkv`). Every
desktop recording is capped at `film.rec_max_s` seconds (editor.cfg, default 14400, 0 = no cap).

Both sources encode with `film.encoder` (editor.cfg, default `auto`: the first of `hevc_nvenc`, `hevc_amf`, `hevc_qsv`,
`libx265`, `libx264` whose short test encode works, tested once per session; or one name), constant quality `cq` (default
16). ffmpeg is never a path a caller names: the `ffmpeg` argument of `time` was removed (a call that still sends it gets
`bad_args`). It is found in this order: `ffmpeg.path` in editor.cfg, the `INSIDEDEV_FFMPEG` environment variable,
`<game>\_mod\ffmpeg\ffmpeg.exe`, `PATH`, `C:\ffmpeg\bin\ffmpeg.exe`. ffmpeg runs in the game's job object, so it ends with
the game; a clean quit waits up to a few seconds so the file is finished. A pass recorded this way runs in realtime: drive
it with `act {hold:true, then:"run"}` and wall-clock waits, or let a person play.

```json
{"op": "vsync", "on": false}
{"op": "record", "record_fps": 30, "cq": 16}
{"op": "record", "source": "desktop", "record_fps": 30, "raise": true}
{"op": "record_stop", "review_fps": 5}
```

Console `rec` (the same recorder; answers the state as JSON):

| Command | Defaults | Reply |
|---|---|---|
| `rec start [fps] [cq] [file] [every=<n>] [enc=auto\|<encoder>]` | fps 30 (5..60), cq 16 (0..51), file `grec_<date>_<time>.mkv` | `rec: starting, source game: the game's own frames through grec, windows over the game stay out (rec status shows the file, or the problem)` |
| `rec start [fps] [cq] [file] [enc=...] desktop [raise] [output=<n>]` | as above, file `rec_<date>_<time>.mkv` | `rec: starting, source desktop: desktop capture: windows over the game will be in the video ...` |
| `rec stop [review_fps] [wait]` | review_fps 0 (0..30) | `rec: stopping[, then cutting review frames at 5 fps]`, with `wait` a last line `wait: rec wait`; `rec: no recording is running` when there is none |
| `rec wait` | | `pending: {json}` while starting, stopping or cutting, else the state JSON |
| `rec status [json]` | | the state (`json` = one line): `on`, `source` (`game` or `desktop`), `phase`, `file`, `seconds`, `rect`, `problem`, `frames_dir`, `encoder`, `max_seconds`, `starting`, `stopping`, `cutting_frames`, `note` |

The first number is fps and the second cq; any other word is the file name. Errors: `busy: a recording is running (...);
rec stop first`, `failed: ffmpeg.exe not found (tried ...)`, `usage: output=<n> is for the desktop source`, `usage:
every=<n> is for the game source`.

### 4.6 Live director passes: `campath` and `grec`

Two console commands of Play (reachable from the console, `POST /cmd`, the file bridge, `tools\inside_ctl.py` and the dev
`command` tool), and the MCP tool `film` (section 6.17) on both profiles. An MCP client on the play profile has no
`command` tool: it uses `film` (grec and pano only; the campath ops need the dev profile) or `POST /cmd` /
`tools\inside_ctl.py` next to its session. Both commands are off until called; `restore` stops both (restore keys
`campath`, `grec`).

- `campath` puts the main game camera on a pose path planned in advance while the game itself runs. Every frame is the
  game's own picture: live particles, shaders animated by time, mod effects, temporal anti-aliasing. The pose is set in
  `Camera.onPreCull`, after the game's camera script, and stays on the camera's transform until the game's camera script
  writes it again, so the game's streaming and bounds culling of the next frame follow the director's view; the game's
  camera logic and the input stay as they are.
- `grec` films the game's own frames into a video file. At the end of every n-th rendered frame in which game time moved
  the native `InsideReadback` plugin copies the back buffer on the GPU (render thread); a take thread copies each finished
  frame out of the plugin at once and a write thread hands it to a separate ffmpeg process. Windows over the game, focus
  and other programs never reach the file. A slow encoder never holds the render thread: a full queue drops a picture
  instead (counted in status).

| Command | Does |
|---|---|
| `campath load <dir> [camera] [offset_s] [rt] [fps=<n>]` | reads `<dir>\poses.csv` and `<dir>\meta.csv` and keeps one camera's poses (default `director`); `offset_s` shifts the plan's game time; `rt`: the pose is looked up by game time, with no pauses or slow motion. The three are positional: `rt` counts only as the fourth word, so give camera and offset before it (`campath load <dir> director 0 rt`; `campath load <dir> rt` looks for a camera named `rt`). The output rate comes from `passes.json` `fps` or `run.json` next to the csv files, else `fps=<n>` (1..1000); the reply ends with `, 30 fps (passes.json)`, `(fps=)` or `(assumed: ...)`. `step` must be the first column of both files. A load, arm or start during a pass stops that pass first; errors read `campath: failed: <why>` |
| `campath arm` | starts with the first logic step of the next `input_track` playback |
| `campath start` | starts at once |
| `campath status [json]` | `idle`, `armed` or `running` with output seconds, game time against plan time, `paused` or the speed, `, 30 fps, mount <path>, 2 look(s)`; `json`: `{state, poses, camera, dir, fps, fps_from, seconds, realtime, offset, at, game, plan, paused, speed, mount {...}, looks}` (`at` = output seconds, `game` = game seconds since the pass started) |
| `campath stop` | stops; the field of view, the near plane, the game speed and a freeze it made are put back, and the mount smoothing starts fresh at the next start |
| `campath mount <transform path\|off\|status> [lx ly lz] [rx ry rz] [fov] [near] [smooth] [into_scene_deg] [level] [hide] [tau_h tau_v tau_yaw tau_pitch] [pitch_max] [free_up free_down]` | the camera rides a transform (position and rotation every frame) and the plan keeps only the clock. Defaults: offsets 0, `fov` 60, `near` 0.03, `smooth` 0 (0..1), `into_scene_deg` 0, `level` 0 (0..1), `hide` 0, `tau_*` 0 s, `pitch_max` 89, `free_up` and `free_down` 0 (no extra limit; section 4.8). `hide 1` collapses the transform while mounted (the boy's head for a first-person view); `tau_*` > 0 smooth position, yaw and pitch with those time constants and keep the roll at 0; `into_scene_deg` turns the view up to that angle toward +z (into the level), fading out while the view looks down 18 to 38 degrees; `level` takes that share of the roll out; `smooth` is the share of the last pose kept per 1/60 s (the same feel at any frame rate). fov <= 0 means 60, near <= 0 means 0.01; fov over 179 or near over 10 is a usage error. `campath mount status` prints the mount; the reply also prints near and smooth |
| `campath mount <path> local=x,y,z rot=x,y,z fov= near= smooth= into= level= hide=0\|1\|on\|off tau=h,v[,yaw,pitch] pitch= free=up,down` | new in 0.11.0: the same options by name, in any order; `pitch=` is `pitch_max`, `into=` is `into_scene_deg`. Example: `campath mount _Boy/_BoyPreprocessed/visual/Boy/Head local=0,0.1,0 fov=75 hide=1 tau=0.12,0.2,0.1,0.15 pitch=60` |
| `campath look add <t0> <t1> <ease_s> <x> <y> <z>`, `campath look add <t0> <t1> <ease_s> @<transform path>`, `campath look clear` | while mounted, the view turns to a point or a transform between plan seconds `t0` and `t1`, eased in and out. A `t0`, `t1` or `ease_s` that is not a number is a usage error |
| `grec start [file] [every] [cq] [enc=auto\|<encoder>] [fps=<rate>] [times=off]` | records every `every`-th rendered frame (1..600, default 2) with constant quality `cq` (0..51, default 16). `file` is optional (file rule below). `enc`: `auto`, `hevc_nvenc`, `h264_nvenc`, `hevc_amf`, `h264_amf`, `hevc_qsv`, `h264_qsv`, `libx265`, `libx264` (default editor.cfg `film.encoder`). `fps=` (1..1000) sets the rate of the file (default below). `<file>.times.csv` is written unless `times=off` |
| `grec stop [wait]` | `grec: stopping (grec status shows when the file is closed)`; with `wait` a last line `wait: grec wait`, and the MCP `command` tool and `POST /cmd` then poll until the file is complete |
| `grec wait` | `pending: grec: closing, ...` while the file is still open, else the status line |
| `grec status [json]` | the status line or JSON below |

Example: `grec start take1 2 16`, reply `grec: opening the readback, then recording every 2 frame(s) into
<game>/_mod/recordings/take1.mkv at 30 fps (nominal 60 / 2, frames placed by game time (real-time play))`. Errors:
`grec: failed: <why>` (no plugin, ffmpeg not found, a folder that cannot be made), `busy: the frame readback is held by
mcam ...`, `grec: already running into <file>`, `usage: ...`. Failures after the reply (the encoder, ffmpeg ending by
itself, a full disk, a window size change: `the back buffer changed from WxH to WxH`) end the capture in that frame, close
the file properly and show as `grec: failed: <why>; <file>, ...` in status and in the `film` tool's reply.

**File rule for grec, pano and rec** (new in 0.11.0). No name: `<game>\_mod\recordings\<kind>_<yyyyMMdd_HHmmss>.<ext>`
with kind `grec`, `pano` or `rec` (`_2`, `_3` when two fall into one second). A bare name: the same folder (the extension
is added when missing). A name with a folder or drive: as given (`rec` keeps the file name only; the `film` tool on the
play profile takes bare names only). Changed: a bare relative name used to land in the game's working folder. An existing
file is replaced and the reply says `(replacing the existing file)`; every reply states the full path.

**grec status.** Text: `grec: <phase>, <file>, N grab(s), M written, K dropped (placed by game time: R repeated, S
skipped, grabs lost: readback a, encoder too slow b, lost c), S still frame(s) left out (game time did not move), WxH
<pixel format>, T s, F fps (<where the rate came from>), <encoder>, video V s, game time G s, file complete with duration,
X MB[, warning: ...][, error: ...]`. Phases: `idle` (before the first start), `opening`, `testing the encoder`,
`recording`, `draining`, `closing`, `done`, `failed`. `written` counts the frames in the file (repeats included).
`grec status json` (one line): `phase`, `recording`, `busy`, `file`, `frames_issued`, `frames_written`,
`frames_repeated`, `frames_skipped`, `frames_still`, `timeline` (`game_time` or `per_grab`), `game_time_start` (absolute
`Time.timeDbl` of video frame 0), `game_time_span`, `max_gap_ms` (time mode), `dropped_readback`,
`dropped_encoder_slow`, `dropped_lost`, `queue`, `queue_max`, `width`, `height`, `pix_fmt`, `every`, `fps`, `fps_from`,
`video_seconds` and `video_time` (the same value), `encoder`, `cq`, `seconds`, `finish`, `ffmpeg_exit`, `mb`,
`has_duration`, `times_file`, `warning`, `error`; when they occurred also `present_grabs` / `present_dropped` and
`readback_reopened` / `readback_stall`. When status says `file complete with duration` the file needs no remux.

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

**Timeline of `grec`** (changed in 0.11.0). The file has a constant frame rate. 0.10.0 fixed the rate once and wrote one
frame per grab, so a minute of play came out about 3.5 s short and the sound no longer lined up. The mode is chosen at
`grec start`, named in the start reply and in `fps_from`, and holds for the whole file:

| Situation at `grec start` | Mode (`timeline`) | File rate | What a video frame is |
|---|---|---|---|
| real-time play (no capture rate, the stepper never touched): a person playing, `input_track` playback, campath takes | time mode (`game_time`) | `fps=`, else nominal 60 / `every` (30 for the default) | frame k shows the newest picture whose game time is at most t0 + k / fps (t0 = the first picture): a frame is repeated when no new picture arrived, a picture is skipped when a newer one came first. Video time = game time = wall time |
| an agent or a timed ctl command holds the stepper (step mode) | time mode (`game_time`) | `fps=`, else 60 / `every` | as above; the time the game stands frozen between calls is left out, so video time = game time |
| a capture rate set by something other than the stepper (`srec record ... clock=fixed:<rate>`, a user's `Time.captureFramerate`) | count mode (`per_grab`) | `fps=`, else capture rate / `every` | one frame per grab; a lost picture is written as a copy of the previous frame. Every frame is the same step of game time |

Frames in which game time did not move (a frozen game, the stepper between agent calls, the pause menu) are never
grabbed; status counts them as still frames left out. Measured (TEST_RESULTS 0.11.0): a 61.7 s real-time recording gave
61.733 s of video for 61.700 s of game time (1852 frames, 109 repeated, 105 skipped, 0 lost); 120 s under load gave
120.133 s of video for 120.100 s of game time and 120.129 s of wall clock; real time, then 180 stepped steps, a 319-step
`move_to` and a checkpoint reload gave 12.17 s of video for 12.13 s of game time, 0 lost, 659 still frames left out. A
recording started in real time and then driven by the stepper keeps time mode.

**`<file>.times.csv`** (on by default, `times=off` or `film {times:false}` switches it off) has one row per video frame:

| Column | Meaning |
|---|---|
| `index` | video frame number |
| `frame` | `Time.frameCount` of the grab shown in this frame |
| `game_time` | absolute `Time.timeDbl` at the end of that grabbed frame |
| `real_time` | absolute `Time.realtimeSinceStartupDbl` at the end of that grabbed frame (the clock of srec `t0Real` and acap `clockBase`) |
| `repeated` | 1 = this frame repeats the previous picture |
| `video_time` | `index / fps` |
| `slot_game_time` | the game time this frame stands for (t0 + index / fps in time mode) |

The first five columns are the 0.10.0 ones, in the same order. Section 4.6.1 uses this file to line up sound and events.

**Readback plugin version 3.** While an agent's stepper runs logic steps, the plugin's usual trigger (a marker render
target the game binds) is not seen, so 0.10.0 lost most frames of stepped runs. Plugin version 3 (`mcam readback stat`
prints the version) also hooks the swap chain's Present: a grab still waiting two presents after it was queued runs at
that Present, on the back buffer of the frame being shown (at most one frame late); of several such grabs only the newest
runs. Status counts them (`N grab(s) taken at Present (marker not seen, up to one frame late)`, json `present_grabs`,
`present_dropped`). When the plugin stops running its events grec reopens its session (`readback reopened N time(s)`).
With an older plugin (versions 1 and 2) grec still starts, but stepped runs lose frames as in 0.10.0: install the
plugin of this release.

**Needs.** `grec` needs `InsideReadback.dll` of this release in `<game>\INSIDE_Data\Plugins\` (the optional MultiCam
plugin; copy it while the game is closed) and ffmpeg (the lookup of section 4.5) with a working encoder (`film.encoder`:
NVIDIA, AMD and Intel hardware encoders or the CPU encoders `libx265` / `libx264`; the first start of a session tests
the candidates with a short test encode, so it takes a little longer). The plugin serves one owner at a time: grec holds it from start to the end
of the close, and an mcam pass that holds it refuses grec (`busy: the frame readback is held by mcam ...`) and the other
way round. `mcam readback stat` names the holder.

**Where the work runs.** Only the frame trigger runs on the game's main thread; the copy runs on the render thread on the
GPU, the hand-over on the background threads `grec take` and `grec write`, the encoding in the ffmpeg process. With srec
and grec recording together the game held 59.8 fps with vSync 60 (0.10.0: 56.5); `grec start` took 51 ms and the stop
until the file was complete 387 ms (TEST_RESULTS 0.11.0). `campath` works inside the frame on the main thread, because
it moves a game object (the camera).

#### 4.6.1 Lining up grec video, acap audio and srec events

All three tools write absolute clock values, so they can be put on one timeline afterwards:

| Tool | Game clock | Real clock |
|---|---|---|
| grec | `times.csv` `game_time` (absolute `Time.timeDbl`); status `game_time_start` = video time 0 | `times.csv` `real_time` (absolute `realtimeSinceStartupDbl`) |
| srec (scene.srec, record_meta.csv) | `game_time` = `Time.timeDbl - header.t0`; absolute = `t0` + `game_time` (`t0` is in the header, `srec_tool.py info`) | `real_time` = `realtimeSinceStartupDbl - header.t0Real` |
| acap (`audio_sync.csv`, `audio_capture.json`) | `game_time` relative to `gameBase` (audio_capture.json, new in 0.11.0: the srec header `t0` when srec or mcam drives the capture, `Time.timeDbl` at `acap start` for a manual capture) | `real_time` relative to `clockBase` (audio_capture.json, absolute); each marker's WAV position is in the WAV's cue chunk (`mcam_audio.py info`) |

By clock mode:

- **Real time** (a person playing, track playback, campath; grec in time mode). Game time and real time run together.
  Video time of an absolute game time g = g - `game_time_start`. An srec event at record time t is at video time
  `header.t0 + t - game_time_start`; an acap marker at video time `gameBase + game_time - game_time_start`. For the sound
  as a whole: marker `s0` sits at absolute real time `clockBase` at the WAV position of its cue, and video frame 0 was
  grabbed at absolute real time `real_time` of times.csv row 0, so video time 0 = WAV position of `s0` + (row 0
  `real_time` - `clockBase`). The offset holds for the whole file. Pauses (pause menu, time scale 0) stop game time:
  the video leaves them out, the WAV does not, so cut the WAV there (the `real_time` column jumps).
- **Capture rate** (srec `clock=fixed:<rate>`, `mcam record` with `record.clock: "fixed"`; grec in count mode). Video
  time = game time - `game_time_start`, one frame per grab. Game time is not wall time here, while the WAV runs on wall
  time: the WAV lines up only through the markers (map each acap marker's game time, plus `t0` for an srec take, to a
  video time). Audio at a capture rate is what mcam replay takes are for (`mcam_audio.py align` and `track`,
  [MULTICAM.md](MULTICAM.md) section 18.7).
- **Agent stepper** (step mode; grec in time mode). The video holds 60 / `every` frames per game second and leaves the
  frozen time between calls out, while the WAV keeps running on wall time. Cut the WAV by the `real_time` column of
  times.csv (each video frame's real time) or by acap markers; the sound is only continuous inside one run. A sound
  capture of a step-mode take is unverified in game.

Example (real time, a manual `acap start` and `grec start` by hand): read `real_time` of the first data row of
`<file>.times.csv` (R0), `clockBase` of `<name>_capture.json` (C) and the WAV position of marker `s0` (P, from
`python -I tools/mcam_audio.py info <name>.wav`). The video starts at P + (R0 - C) seconds into the WAV: cut that much off
the start of the WAV (or delay the video when it is negative) and mux the two.

### 4.7 360 degree panorama: `pano`

One console command of Play (console, `POST /cmd`, the file bridge, the dev `command` tool, `tools\inside_ctl.py`) writes one
equirectangular PNG; the MCP tool `film {op:"pano"}` (section 6.17) does the same on both profiles and answers when the
file is complete. It is off until you call it, keeps nothing running afterwards and never touches the sound.

The six cube faces are six ordinary game frames: the free camera is put at the eye, looking along the face, with a field of
view of 90 degrees over the shorter side of the game view; the next frame runs the whole game path at that pose (streaming,
culling, lighting, fog, water, the post chain) and the centre square of its picture is read. A worker thread then builds the
panorama off the main thread and streams the PNG to disk in bands of at most 32 MiB of pixels, so a 16384 px panorama needs
no more memory than a 4096 px one. While it runs, `pano` switches editor overlays off (`clean on`, put back afterwards),
turns off barrel distortion, chromatic aberration, motion blur and the temporal history at the start of each face, locks
the free camera's keys and suspends a running `campath`; all of it comes back after the capture.

New in 0.11.0: by default the game is held for the six faces (time scale 0 while nobody holds the stepper, else the
stepper's freeze; undone at the end), so all faces show one moment from one point; `live` keeps the game running as in
0.10.0. `overscan=<deg>` renders each face that many degrees wider than 90 and reads only the centre square that spans
exactly 90, which keeps screen-edge effects (vignette, bloom falloff, TAA edge clamps) off the cube seams at the price of
smaller faces.

| Argument | Default | Meaning |
|---|---|---|
| `<file.png>` or `take` | `pano_<date>_<time>.png` | the file rule of section 4.6: no name (`pano take`, or an option as the first word, `pano size=2048`) = `<game>\_mod\recordings\pano_<yyyyMMdd_HHmmss>.png`; a bare name goes to that folder; a path is used as given. `.png` is added if missing. An existing file is replaced (the reply says so). The sidecar is the same name with `.json` (`room.png` -> `room.json`); when a `room.json` that is not a panorama sidecar is there already, it is left alone and the sidecar becomes `room.pano.json` (with a warning). `pano` alone is `pano status` |
| `size=<n>` or a bare number | 4096 | panorama width, 256..16384, rounded down to a multiple of 4; the height is half of it |
| `x y z` or `eye=x,y,z` | the boy's head bone + 0.10 m | the eye. Give it whenever there is no boy, or he is not where you want the eye |
| `noVignette` (or `vignette=0`) | vignette kept | leaves the screen-space vignette out |
| `lens` | off | keeps barrel distortion and chromatic aberration (they bend the face edges) |
| `near=<m>` | the camera's own | near clip, above 0, at most 10 m. Use a small value inside tight rooms and at the head (`near=0.05`) |
| `warm=<n>` | 1 | frames per face that only settle, 0..30 |
| `samples=<n>` | 1 | jittered frames per face accumulated as anti-aliasing, 1..16 |
| `faces=<dir>` | off | also writes `front.png`, `right.png`, `back.png`, `left.png`, `up.png`, `down.png` at full face size |
| `compress` | off | deflates the PNG inside the game (about 10 s at 4096x2048, against under 1 s for the default stored PNG, which is a much larger file). Or recompress it afterwards with any PNG tool |
| `delay=<n>` | 2 | frames between switching the overlays off and the first face, 0..120 |
| `hold` / `live` | `hold` | hold the game for the six faces / let it run (0.10.0 behaviour) |
| `overscan=<deg>` | 0 | 0..45: render each face wider and keep the centre 90 degrees |
| `wait` | off | the reply ends with a line `wait: pano wait`; the MCP `command` tool and `POST /cmd` then poll until the file is written |

Also `pano status [json]`, `pano wait` (`pending: pano #4 capturing ...` until the job ends, then its result line) and
`pano help`. `pano status json`: `{busy, id, phase, file, ms, text, last: {id, ok, file, sidecar, width, height,
face_px, frames, held, game_time, faces_dir, ms: {capture, convert, write, total}, warnings[], error}}`. The sidecar
adds `held`, `overscan_deg` and `view_px`. Example: `pano take size=8192 overscan=8 wait`, reply `pano #4 queued:
<game>/_mod/recordings/pano_<yyyyMMdd_HHmmss>.png 8192x4096, eye head + 0.1 m, 2 frame(s) per face, overscan 8 deg, game
held for the faces, stored PNG; pano status` and `wait: pano wait`. Faces have the shorter side of the game view in pixels: a 2560x1440 game view gives 1440 px per
face, enough for a panorama width of 5760. A wider `size` only interpolates; when the faces hold at least twice what the width
needs, a box filter averages them down.

**Do it by hand.**

1. Open the console (backquote key), or use `python tools\inside_ctl.py <command>` from a command prompt, which also prints the
   replies. Stand where you want the panorama, or load a recording and seek (section 7.8).
2. Type `pano C:/shots/room.png` (or `pano take` for a file in `<game>\_mod\recordings\`). The reply is `pano #1 queued:
   C:/shots/room.png 4096x2048, eye head + 0.1 m, 2 frame(s) per face, game held for the faces, stored PNG; pano status`.
3. You see the picture jump through six directions for under a second to about a second, depending on the frame rate, then the game
   view comes back as it was.
4. Type `pano status` until it says `pano #1 done C:/shots/room.png 4096x2048 from 6 x 1440 px faces in 13 frame(s): capture 781 ms,
   convert 659 ms, write 741 ms, total 2267 ms`, a few seconds (measured with 0.10.0). Warnings, if any, follow that line and
   are in the JSON file. `pano wait` answers `pending: ...` until then, and a client that sends `pano ... wait` through the
   MCP `command` tool or `POST /cmd` gets the done line in one call.
5. Open `room.png` in a panorama viewer; the file carries the GPano metadata that viewers read. In a plain image viewer it is a
   2:1 picture: the middle of the image looks along world +z, into the level; world +x is at three quarters of the width; the top
   edge looks straight up.

There is nothing to switch off afterwards. A capture in progress ends with `restore`. The sidecar (`room.json`, or
`room.pano.json` when a foreign `room.json` exists) holds the eye, the orientation, the game time, `srec_t` in the
recording viewer, the original camera, `held`, `overscan_deg`, `view_px`, the timings and the warnings.

**If it goes wrong.**

| You see | Why | Do |
|---|---|---|
| `pano: busy with #N (...)` | a capture or its conversion runs | `pano status` until it ends. A capture whose frames went on for more than 120 frames without it (a level load) is dropped by the next `pano`, which reports `pano #N failed: dropped, its capture stalled (level load?)` |
| `pano: mcam is capturing` | a MultiCam run holds the camera | wait for it, or `mcam stop` |
| `pano: busy: another capture holds the camera (an mcam pass, det or lab)` | det or a lab test holds the camera | wait for it to end |
| warning `the game ran during the capture` | something changed the time scale while the game was held | take it again; `live` if the game must run |
| `pano #N failed: no head bone (...); give x y z` | no boy in this level state | give the eye: `pano C:/shots/room.png 412 -8.5 -9` |
| `... failed: capture state lost (restore or level load)` | `restore` came during the capture | run it again. The camera is back already (tested); a load can end it the same way (from the code: a checkpoint reload during a capture in the test did not interrupt it) |
| `... failed: view size changed during the capture` | the game window was resized | keep the window as it is |
| `... failed: camera view WxH too small` | the game window is very small | make it larger |
| `pano status` stays at `queued` | the window is minimized: a minimized game runs no frames | restore the window |
| warning `camera moved off the face pose before the read` | something else moved the camera in that frame | stop what moves it (a MultiCam run, a script); look at the face in `faces=` |
| the panorama shows a different place than the recording | `srec load` refused, the viewer never started (seen once at 1.1 % unresolved objects in a fresh session) | read the reply of `srec load`; `srec load <dir> maxUnresolved=0.03` if the missing objects do not matter |
| seams of slightly different brightness | bloom and vignette work on the screen, per face (expected from how they work) | `noVignette`; the seams are weak |

**Limits.** The recording viewer (`srec pause`, `srec seek`) is the safest place: nothing moves there. In live play the game is
held for the faces by default since 0.11.0. With `live` it keeps running during the 13 frames (151 to 272 ms in a small room
of the game, measured with 0.10.0), so moving things shift between faces by their speed times that time (from the code, not
measured on anything moving; at an idle spot no shift showed). `grec`, `rec` and `srec record` are not refused: a `pano` during a `grec` ended with no warning and the film went on with
no frame dropped, so the film holds the six faces (from the code, that film was not viewed). Do not run them together. A checkpoint
reload during a capture did not interrupt it in one test, so its faces can come from the load: take it again. Levels the game has not
streamed in for the eye stay missing; `warm` gives streaming more frames (inferred, not measured).

### 4.8 A first-person camera by hand: `campath mount` and its free-look limits

`campath mount` rides the main camera on a transform (full argument list in section 4.6). This is how the first-person passes of
a long bunker route in a mod scene were filmed. The stabiliser (`tau_*` above 0) follows the head with time constants and keeps
the roll at 0. New in 0.10.0: outside the timed looks, `free_up` and `free_down` (the last two arguments, degrees) limit how far
the head may tilt the view up and down, so the eye does not swing to the ceiling on a ladder. Inside a look they blend to
`pitch_max` with the look's own weight, so a look at something high is still reached. They act only with the stabiliser on
(`tau_h` or `tau_v` above 0); 0 or left out means no extra limit, exactly as before.

The mount keeps only the clock from the plan, so a still plan is enough. This writes one of 60 s (save as `make_plan.py`, run it
with Python 3 once):

```
import pathlib
d = pathlib.Path(r"C:\films\plan"); d.mkdir(parents=True, exist_ok=True)
n = 60 * 30
(d / "poses.csv").write_text("step,camera,x,y,z,fov,qx,qy,qz,qw\n" + "".join(f"{i},director,0,0,0,60,0,0,0,1\n" for i in range(n + 1)))
(d / "meta.csv").write_text("step,game_time,speed\n" + "".join(f"{i},{i / 30:.6f},1\n" for i in range(n + 1)))
```

Then, in the game, with the boy in play:

1. `campath load C:/films/plan director 0 rt`. The reply names 1801 poses. `rt` leaves the game's clock alone.
2. `campath mount _Boy/_BoyPreprocessed/visual/Boy/Head 0 0.10 -0.06 -6 0 0 70 0.05 0 20 1 1 0.12 0.35 0.30 0.45 75 8 30`. In order:
   local offset 0, 0.10, -0.06 m; local rotation -6, 0, 0; `fov` 70; `near` 0.05; `smooth` 0; `into_scene_deg` 20 (see section 4.6:
   the sets have no back side); `level` 1; `hide` 1 (collapses the head); `tau_h` 0.12, `tau_v` 0.35, `tau_yaw` 0.30, `tau_pitch` 0.45;
   `pitch_max` 75; `free_up` 8; `free_down` 30. The reply ends with `pitch within 75 deg, free eye -8..+30 deg)`. Since
   0.11.0 the same mount can be given by name: `campath mount _Boy/_BoyPreprocessed/visual/Boy/Head local=0,0.10,-0.06
   rot=-6,0,0 fov=70 near=0.05 into=20 level=1 hide=1 tau=0.12,0.35,0.30,0.45 pitch=75 free=8,30`; `campath mount status`
   prints what is set.
3. `campath start`. The view moves into the boy's head; you still play him normally. Climb a ladder: the view does not tilt more than
   8 degrees up. To compare, repeat step 2 without the last two numbers.
4. To add a timed look: `campath look add 10 14 0.8 412 -8 2` turns the view to that point between plan seconds 10 and 14.
5. To switch off: `campath stop`, then `campath mount off` (the mount stays set for the next start otherwise) and `campath look clear`.
   The field of view and the near plane come back; `restore` does the same as `campath stop`.

| If you see | Do |
|---|---|
| `campath: no poses.csv / meta.csv in ...`, or `no poses of camera 'director'` | check the folder and the camera name in the file |
| the reply says `not found yet, retried every frame` | the transform does not exist yet or the path is wrong; correct the path, or wait for the boy to load |
| no `free eye` in the reply | `tau_h` and `tau_v` are 0 (the limits then do nothing), or both limits are at or above `pitch_max` |
| the boy's head or neck is in the picture | `hide 1`, or a larger `near` |
| the far end of a room flickers between two brightness levels | see "First-person flicker" in section 15 |

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
| `detail` | enum | `concise` | `concise`, `full`, `delta` | concise is trimmed to about 2400 characters (about 600 to 800 tokens); full adds `x` / `y`, paths, input, bodies, menu, camera, perf; delta (new in 0.11.0) is concise with only the changed rows (below) |
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
| `warnings[]` | `music_timed: ...`, `focus_lost`, `focus_recovered`, `camera_away`, `controller_digital`, `input_hijacked`, `scan_stale: the object and hazard lists are N steps old (...): the rescan waits while the recorder films` ... (`scan_pending` no longer exists) |
| `meta` | `{play, mcp, core}` build versions, all 0.11.0 in this release (only in the observe tool result) |
| full only | `objects[].x`, `objects[].y`, `hazards[].x`, `hazards[].y` (world metres, also in delta), `input {mode, held, queued, controller, hijacked, track}`, `bodies[] {id, kind, name, x, y, controlled, active}`, `menu {open, group, selected}`, `camera {overridden, pinned_subscene}`, `truncated`, `perf {scan_mode, scan_ms, scans_total, scan_reason, scan_age_steps, scan_age_frames, objects_scanned, tracked, ids_live, ids_carried, sections_ms {...}, main_ms ...}` |
| delta only | `objects[].change` (`new`, `changed`, `moved`), `hazards[].change` (`new`, `changed`, `threat`), `gone {objects:[ids], hazards:[ids]}`, `delta {full:false, base_step, base_frame, same_objects, same_hazards}` or `delta {full:true, reason}` |
| with image | `image {width, height, path, black}`; objects get `screen` pixel positions |

`observe {detail:"delta"}` lists in `objects[]` only rows that are new, changed (state or actions) or moved more than
0.5 m, and in `hazards[]` only rows that are new, changed (state, threat, cover; digits in the state text do not count)
or a threat now. `gone` names the ids listed before and not now. The base is the previous observation of the same MCP
session, including the one inside an action result. The first call of a session and the first after a load return the
complete lists with `delta {full:true, reason:"no_baseline"}` or `reason:"load"`. `events` and `stuck` are counted per
MCP session.

Secret devices (orb pods and similar) are never listed. Clients that pass only `structuredContent` to the model (Claude
Code does) drop the image of `observe {image}`: use the `screenshot` tool instead.

### 6.2 act

Press the controls for exact logic steps.

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `inputs` | array | | 0..64 segments | input segments in order |
| `seq` | string | | at most 2000 characters | instead of `inputs` (new in 0.11.0): the `ctl seq` script of section 11 (`R L U D <t>[@0.3..1]` stick, `J [<t>]` jump, `A [<t>]` action (no `<t>` = a tap), `G <t>` grab, `W <t>` wait, `+` = together; `<t>` = seconds `1.2` or steps `72s`). At most 600 steps in total; `inputs` and `seq` together = `bad_args` |
| `wait_ready_s` | number | 0 | 0..45 | wait up to that many seconds for the game to take input, then act in the same call (section 4.4) |
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
`stop_reason`, `next`, `steps` (run), `requested`, `frames`, `held {stick, jump, action, grip}` (absent when nothing is
held; `grip:true` for a grab), `input_mode`, `when {condition, satisfied, detail, waited_steps, elapsed_ms}` (with
`when`), `wait_ready {ready, waited_steps, elapsed_ms, reason}` (with `wait_ready_s`), `warnings`, `changes`, `observe`.

```json
{"seq": "R1.2 J R0.4+J G1 W0.5 L0.3"}
{"seq": "R1", "wait_ready_s": 10}
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
| `when`, `when_timeout_s`, `wait_ready_s` | | | | as in `act` |
| `observe` | enum | `concise` | `concise`, `none` | |

There is no navmesh: the navigator reads a collision slice at the body's depth plane, runs in 30-step segments and
re-plans after landings, climbs and unexpected walls. Returns `outcome` (`arrived`, `blocked`, `fell`, `died`,
`timeout`, `cancelled`, `unreachable`, `loading`), `reason` (for blocked: wall, pit, obstacle, ledge_up, ledge_down,
water, hazard), `steps`, `distance_left`, `x`, `y`, `events[]`, `goal`, `state`, `route`, `replans`, `warnings`,
`changes`, `observe`, plus `when` and `wait_ready` when given. A grab grip stays held on arrived, blocked, fell, timeout
and unreachable (the box comes along) and is let go on cancel, death and loads. A goal that lies inside a gap the body
just jumped ends `arrived` at the gap's edge with a note (`the goal x=... lies in a gap: stopped at its edge`); 0.10.0
jumped the gap back and forth until `max_steps`. The navigator's timers no longer run several times too fast with vSync off (fixed in 0.11.0).

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
| `when`, `when_timeout_s`, `wait_ready_s` | | | | as in `act` |
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
`observed` (the values at the end), `stop` (`steps`, `condition`, `died`, `loading`, `cancelled`, `timeout`,
`stalled`), `detail`, `job` (for a job condition; with the job's `outcome`, `end` and `stop_reason` when it has them),
`warnings`, `changes`, `observe`. Every condition goes inside `any`: `wait_until {alive:true}` is refused with
`bad_args`, write `wait_until {any:[{alive:true}]}`.

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
| `on` | boolean | true | | op vsync: true = the game's own vSync, false = off; either wins over `play.vsync_off` until `vsync auto` or `restore` (section 4.5) |
| `source` | enum | `game` | `game`, `desktop` | op record (new in 0.11.0): `game` = the game's own frames through grec (windows over the game never get in); `desktop` = the screen area of the window (ffmpeg ddagrab; windows on top are recorded too) |
| `record_fps` | integer | 30 | 5..60 | op record: frames per second of the video (source game: every n-th rendered frame that gives about this rate) |
| `every` | integer | | 1..600 | op record, source game: record every n-th rendered frame instead of `record_fps` |
| `cq` | integer | 16 | 0..51 | op record: constant quality of the encoder (lower is better and larger) |
| `raise` | boolean | false | | op record, source desktop: lift the game window to the top without taking focus |
| `file` | string | `grec_<date>_<time>.mkv` (desktop `rec_<date>_<time>.mkv`) | | op record: file name in `<game>\_mod\recordings` |
| `review_fps` | number | 0 | 0..30 | op record_stop: also cut JPG review frames at this rate into `<file>_frames` |

The `ffmpeg` argument was removed in 0.11.0: a call that sends it gets `bad_args` (ffmpeg is found by the tool itself,
section 4.5).

Returns `mode`, `frozen`, `speed`, `scale`, `game_scale`, `capture_fps`, `frame`, `step`, `music_s`, `loading`,
`paused`, `running`, `freeze_pending`, `vsync`, `vsync_off`, `vsync_always_off`, `vsync_rule`, `vsync_override`,
`vsync_held_off`, `fps`, `frame_ms_max`, `run_in_background`, `recording` (a scene recorder samples and owns the frame
pacing), `pacing` (`real_time_60` or `none`), `record {source, on, file, seconds, rect, problem, frames_dir}`,
`warnings`; op step adds `steps_run`, `frames_run`, `stop`, `detail`, `changes`, `observe`; op rng_seed adds `seed`; op
record_stop adds `stop_file`, `stop_mb`, `stop_seconds`, `stop_clean` and `stop_frames_dir`. `state`, `rng_seed`,
`record` and `record_stop` take no lane (a recording runs next to any action). A desktop recording's result carries the
warning `desktop capture: windows over the game will be in the video ...`.

```json
{"op": "realtime", "speed": 1}
{"op": "step", "steps": 30}
{"op": "rng_seed", "seed": 1}
{"op": "record", "every": 2}
```

### 6.7 checkpoint

Savepoints. `current` and `list` are read-only.

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `op` | enum | `current` | `current`, `list`, `reload`, `next`, `prev`, `goto` | |
| `subscene` | integer | | 0..999 | op goto: savepoint subscene index (0..82, see list) |
| `savepoint` | integer | | 0..999 | op goto: savepoint index |
| `name` | string | | | op goto (new in 0.11.0): the subscene by scene or puzzle name instead of `subscene` (exact, else a part that matches one subscene); `savepoint` defaults to 0. No match = `not_found`, several = `ambiguous` with `candidates [{id, path}]`. Example: `checkpoint {op:"goto", name:"forestReveal"}` |
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
| `id` | string | | | op select: item id from state (`m3`). Since 0.11.0 an item keeps its id across menu states and the ids of a later state continue the numbering (a submenu after a 5-item main menu is `m6`, `m7` ...); an id whose item is not in the current state answers `stale_id` ("menu item m3 is not in the current menu state (it belongs to an earlier one)") |
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
| `format` | enum | `png` | `png`, `jpeg` | preview / marked (new in 0.11.0): `jpeg` is several times smaller (a 1600 px shot was 121 KB in 115 ms) |
| `quality` | integer | 80 | 1..100 | jpeg quality |

The result is a JSON text block `{ok, mode, width, height, src_width, src_height, path, uri, black, frame, marks[{id,
label, x, y}], camera {name, x, y, z, pitch, yaw, roll, fov}, boy {x, y, on_screen}, note}` plus the image. Image
results carry no `structuredContent`, so clients that pass only structured results still show the image. `black` flags
an almost black frame. Since 0.11.0 the frame is read once, scaled and encoded by Unity itself (960 px: 2.25 s in 0.10.0,
0.09 s now), and for `preview` / `marked` the `path` names the returned image file, not a full-size PNG. Files go to `<game>\_mod\shots\` (the newest 200 are kept); `full` adds a resource link
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
| `format` | enum | `rows` | `rows`, `runs` | new in 0.11.0: `runs` writes each row as runs, `12# 30. 4~` (count and character, separated by one space) |
| `crop` | boolean | false | | new in 0.11.0: drop border rows and columns that hold only `.` or `?`; `origin`, `cols`, `rows` describe what is left |

Returns `origin {x, y, z}` (bottom-left cell), `cell`, `cols`, `rows`, `grid[]` (text rows, top row first: `#` solid,
`.` free, `~` water, `H` ladder, `|` rope, `B` body, `^` hazard, `?` not loaded), `ledges[] {x, y, side, climbable,
source}`, `probe`, `legend`, `format`, `cropped`, `center`, `rays`, `notes`. Example: `geometry {op:"slice", width:64,
height:32, format:"runs", crop:true}`. Use it before `move_to` to see pits, walls and ledges, and to debug a `blocked` outcome.

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

New in 0.11.0: a play result has `end` (`finished`, `cancelled`, `restore`, `level_load`, `died`, `stalled`,
`desynced`, `error`); a `stop_reason` for a restore says which one (`restore (the restore tool)` or `restore (the host:
idle, session end, mcp off, play restore or quit)`). `status` carries `last_play {name, outcome, end, stop_reason, step,
of, job, ended}` until the next play, and `stop` reports the `end` of the playback it stopped. A recording keeps running
when control is given back to the player (the pad is recorded) and ends with `the game input was replaced by <type>: no
input reaches the recording any more` when another input core takes over. Files larger than 16 MB are refused (`<path>:
23 MB, an input track is at most 16 MB`).

### 6.15 restore (idempotent)

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `only` | string[] | all | 0..32 keys or key prefixes | only these registry keys (`input`, `time`, `camera`, `input_track`, `hazard:` ...) |
| `include_save_lock` | boolean | false | | also release a save lock taken through MCP |
| `keep_agent_input` | boolean | false | | keep AgentCore installed (neutral input) instead of giving control back |
| `dry_run` | boolean | false | | new in 0.11.0: change nothing, list what a restore would do |
| `observe` | enum | `concise` | `concise`, `none` | |

Returns `reset[] {key, what, result}`, `kept[]`, `errors[]`, `clock {mode, frozen, speed, scale, held}`, `observe`.
`kept` also lists notes such as `settings:volume (master volume 0, was 0.8: restore never raises the volume, raise it in
the game's menu)` and `rng_seed (random seed 7 was set with time {op:"rng_seed"}: the random state is not put back)`. With
`dry_run:true` the result is `{dry_run:true, would_cancel:"j12 (act)", would_reset:[{key, what}], kept:[...],
clock:{...}}` and nothing changes; the console form is `play restore list [json]` ("a restore would reset 3 things
(nothing was changed)" plus one line per key). Example: `restore {dry_run:true}`. Section 13 lists what it puts back.

### 6.16 Prompts and resources

| Name | Kind | Content |
|---|---|---|
| `playtest_from_checkpoint` | prompt, arguments `subscene` (required), `savepoint`, `goal` | a ready plan: notes, guide, load, the loop, restore |
| `solve_here` | prompt, argument `goal` | explain the current place with as few spoilers as possible |
| `inside://guide/index` | resource | the playbook index (Markdown) |
| `inside://guide/<group>/<topic>` | resource (one per topic, listed at load time) | one topic at hint level; `?level=steps`, `?level=solution`, `?section=<heading>` |
| `inside://shots/{file}` | resource template | full-size PNG of an earlier screenshot |

### 6.17 film (new in 0.11.0)

One typed door to the filming commands of sections 4.6 and 4.7, with structured results, on the play and dev profiles.
Each op runs the console command of the same name on the main thread, so both doors behave the same; the tool adds the
waiting and the parsed state.

| Parameter | Type | Default | Values | Meaning |
|---|---|---|---|---|
| `op` | enum | `status` | `status`, `pano`, `grec_start`, `grec_stop`, `campath_load`, `campath_arm`, `campath_start`, `campath_stop`, `mount` | what to do; `status` = all of them plus the recorder of `time {op:"record"}` |
| `file` | string | `<kind>_<date>_<time>` | | pano / grec_start: a file name (in `<game>\_mod\recordings`) or a full path (dev profile only); for pano not a name with `=`, a bare number or the word `take` |
| `wait` | boolean | true | | pano / grec_stop: answer when the file is complete (within the call's time) |
| `size`, `eye[3]`, `vignette`, `lens`, `near`, `warm`, `samples`, `delay`, `hold`, `overscan`, `compress` | | as `pano` | | pano options (section 4.7); `vignette` default true, `hold` default true, `overscan` 0..45 |
| `faces_dir` | string | | | pano: also write the six faces there (dev profile only) |
| `every`, `cq`, `enc`, `fps`, `times` | | 2, 16, `film.encoder`, see 4.6, true | | grec_start options (section 4.6) |
| `dir`, `camera`, `offset_s`, `realtime`, `fps` | | `director`, 0, false | | campath_load (dev profile): the plan folder, camera, offset, `rt`, output rate |
| `object`, `local[3]`, `rot[3]`, `fov`, `near`, `smooth`, `into`, `level`, `hide`, `tau[2..4]`, `pitch`, `free[2]` | | | | mount (dev profile): the named options of `campath mount`; `object` is the transform path or `off` |

Returns `{ok, op, text, pending, pano{...}, grec{...}, rec{...}, campath{...}, readback}`: `text` is the console reply,
the objects are the `status json` forms of sections 4.5 to 4.7, `readback` names the holder of the frame readback (`grec`,
`mcam`) or `free`. `pano` and `grec_stop` return when the file is complete (`pending:true` when the call's time ran out:
ask `film {op:"status"}` later); `grec_start` returns when frames are being written, or fails with `game_error` and the
reason (no plugin, encoder, ffmpeg). On the play profile `file` must be a bare name, and `faces_dir` and the campath /
mount ops are refused (`op mount drives the game camera and needs the dev profile`).

```json
{"op": "pano", "size": 4096, "overscan": 8}
{"op": "grec_start", "file": "take1", "every": 2}
{"op": "grec_stop"}
{"op": "status"}
```

The first answers `{ok:true, op:"pano", pano:{busy:false, last:{id:4, ok:true, file:".../pano_....png", ...}}, ...}`;
`grec_stop` answers with `grec.finish` = `file complete with duration, ... MB`.

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

Loads through console commands, mods or the dev profile do not wait for themselves. `wait_until {any:[{ready:true}]}` holds at
once while the old level is still up, before the new load has even started. Wait for the load first, then for ready, and
check `observe.checkpoint` against the target:

```json
{"name": "wait_until", "arguments": {"any": [{"loaded": false}], "timeout_s": 10}}
{"name": "wait_until", "arguments": {"any": [{"ready": true}], "timeout_s": 45}}
```

### 7.7 Film a planned camera over live play

`campath`, `grec`, `srec` and `mcam` are console commands: an MCP client needs the dev or all profile for them (`/mcp/dev`,
`/mcp/all`: the `command` tool and `film`; `input_track`, `time`, `observe`, `restore` are on dev too), or `POST /cmd` /
`tools\inside_ctl.py` next to a play session.

1. Rehearse and record the take as an input track from a checkpoint (section 8). Record it once more with the scene
   recorder (`srec record start <dir> spec=<spec> clock=fixed:60`) while the track plays.
2. Plan the cameras: `mcam passes <spec> <dir>` writes `<dir>\passes\poses.csv` and `meta.csv` (no rendering).
3. Reset to the same start: the same checkpoint, loaded once; release the free camera (`cam off`), it blocks the input.
4. `grec start <dir>\director.mkv` (or `film {op:"grec_start", file:"<dir>\director.mkv"}` on the dev profile); wait until
   `grec status` says `recording`. vSync needs no switch since 0.11.0: grec places the frames by game time.
5. `campath load <dir>\passes director`, `campath arm`, then `input_track {op:"play", name:"<track>", lockstep:true,
   then:"run", wait:false}`. Right after `campath status` turns `running`, read `campath status json` (`game` = game
   seconds since the pass started) and `grec status json` (`game_time_span`): the first pose is at about video time
   `game_time_span - game`. For exact work use `<dir>\director.mkv.times.csv` (section 4.6.1).
6. When `campath status` is back to `idle`: `grec stop wait` (or `film {op:"grec_stop"}`), which answers when the file is
   complete, then `restore`.

A second camera of the same plan is the same run with another camera name; `rt` keeps the game at speed 1 for cameras that
should not pause it.

### 7.8 A panorama of a moment in a recording

The recording viewer holds the whole scene still at any second of a take, which is the safest place for `pano` (section 4.7).
The same note on profiles as in 7.7 applies.

1. Record the take: `srec record start <dir>`, play, `srec record stop keep` (`srec record stop load` stops and loads the
   take in one step).
2. `srec load <dir>`. Read the reply; if it refuses (it starts with `refused:`, `busy:` or `not found:`), the viewer did
   not start and a panorama would show the live level.
3. `srec pause`, then `srec seek 37.5` (seconds into the take).
4. `pano C:/shots/t37.png near=0.05 wait`. With no eye given, the eye is the boy's head of the recording at that second.
5. `pano wait` (or `pano status`) until `done`, then the next `srec seek` and `pano`. `srec end` when you are finished.

The JSON file has `srec_t`, so every panorama says which second it shows.

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
- A playback is a detached job: the host cancels detached jobs after 45 minutes (10 before 0.10.0), so a take that needs
  longer on the wall clock is cut. Lockstep can take far longer than the track: a 431 s take at 0.65x real time needs about 660 s.
  `input_track {op:"status"}` says `done` for every kind of end; the real reason is in `jobs {op:"status", id}` (`outcome`,
  `stop_reason`, `cancel_reason`). A scene recorder that runs during play owns the frame pacing (the take follows its clock).
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
| `ctl` / `ctl status` | the control lane owner and the last job (`ok: control lane free; last job j5: done: run right for 120 steps (2 s)`), plus the body line |
| `ctl status json` / `ctl state` | the concise observe JSON on one line, plus `ctl {lane:"free"\|"held", job{}, last_job{}}` |
| `ctl stick <x> <y> [for <t>]` | stick (x right +, y up +; each -1..1, magnitude 0 or 0.3..1); without `<t>` it is held |
| `ctl press <jump\|action\|grab ...> [for <t>]` | buttons (`grab` = action; `j`, `a`, `g` also work); without `<t>` held |
| `ctl tap <button ...> [<t>]` | a tap: pressed 5 steps, released on the 6th; with `<t>` pressed that long |
| `ctl hold <left\|right\|up\|down\|stick <x> <y>\|jump\|action\|grab ...>` | add to the held input (directions add up: `hold up right`) |
| `ctl release [all \| stick\|jump\|action\|grab ...]` | release everything, or only those parts (a direction word releases the stick) |
| `ctl run left\|right [<t>]`, `ctl walk left\|right [<t>]` | stick x = 1 / 0.5; without `<t>` held |
| `ctl jump [left\|right] [<t>]` | a jump tap; with a direction a running jump (stick that way for `<t>`, default 0.5 s, jump pressed the first 5 steps) |
| `ctl grab on\|off` | hold / release action |
| `ctl swim <dir> [<t>]` | dir: left, right, up, down, up-left, up-right, down-left, down-right (ul, ur, dl, dr) |
| `ctl goto <x> [<y>] [walk\|run]` | the `move_to` navigator to world x (y for swim / climb targets), stop exact, at most 1800 steps; a realtime clock stays realtime (no freeze on arrival) |
| `ctl stopat <x> [walk\|run]` | plain stick toward x, no jumps or climbs; the stick goes neutral in the step that crosses x (held buttons stay); 60 s at most, else `not_reached` |
| `ctl seq "<script>"` | an input script as one job (grammar below) |
| `ctl track rec [name] \| stop \| save <name> [overwrite] \| play [name] [speed <k>] [freeze] [correct\|nocorrect] [free] \| list \| info [name] \| status \| load <name> \| trim <from> [<to>]` | the `input_track` ops; `play` runs as a job; `free` = lockstep off; `stop` during a playback cancels it |
| `ctl time [state] \| freeze \| run [<speed>] \| step <n> \| speed <k>` | `freeze` = step mode, `run` = realtime at the current or given speed (0.05..4), `step` = 1..600 logic steps (a job) |
| `ctl mode agent\|mix\|human` | the input mode; human gives the pad and keyboard back (the next ctl input takes over again) |
| `ctl job [jN] [json]` | the state of the last (or that) ctl job; `json` = `{ok, id, tool, state, what, elapsed_s, line, outcome, end, stop_reason, steps ...}` |
| `ctl wait [jN] [<seconds>]` | new in 0.11.0: block until the job ends (default 30 s, at most 600), then the job line (with ` (not finished after 30 s)` on a timeout). It works through the MCP `command` tool and `POST /cmd`, which run it off the main thread (seconds capped to the call's remaining time). From the console, the file bridge or a script it would stop the game, so it answers `refused: ctl wait would stop the game here (...); poll ctl job j5, or use the MCP tool wait_until {any:[{job:"j5"}]}` |
| `ctl track status` | as `input_track status`; adds `; last play <name> ended <end> at step a/b (<stop_reason>)` |
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
- Timed commands start a job and answer at once: `started: <what> job=jN; ctl job jN shows it, ctl stop cancels it`,
  ending with `; it takes about 2 s` (realtime) or `; it takes at most about 2 s (step mode runs as fast as the game
  renders)`. The console log gets `ctl jN <end line>` when it ends. Any end other than done releases all input.
- ctl and MCP actions share the control lane: while one runs the other answers `busy: <tool> is running (job jN)`. While
  another InsideDev tool drives the body through its own input layer (a replay, det) every input command answers `busy:
  the game input is owned by FilmInput (another InsideDev tool, a replay, drives the body)`.
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
python tools\inside_ctl.py state                        the concise observe JSON (needs InsideDev.Play.dll)
python tools\inside_ctl.py snapshot                     the bridge command state (text, with the "savepoint:" line)
python tools\inside_ctl.py ping                         "pong InsideDev <version>"
python tools\inside_ctl.py wait --timeout 90            until the world is loaded and playable
python tools\inside_ctl.py wait savepoint 3 2           until savepoint 3 2 is the current one
python tools\inside_ctl.py doctor                       the game's self-check; also works when the game is not reachable
python tools\inside_ctl.py "srec record status"         one argument is sent as typed
python tools\inside_ctl.py --stop-on-error -f run.txt   one command per line (# comments); -f - reads stdin
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
| `--force-bridge` | | write the bridge command also when no INSIDE.exe process is seen |
| `--no-wait` | | do not follow a started ctl job |
| `--timeout S` | 120 | how long to follow a job; also the limit of `wait` |
| `--http-timeout S`, `--bridge-timeout S` | 70, 20 | per-request limits |
| `--json` | | print one line `{"ok","text","error","via","job","exit","command"}` per command |
| `--stop-on-error` | | `repl` / `-f`: stop at the first command that does not exit 0 |
| `--quote-all` | | quote every argument that has a blank, also a single one |
| `-f FILE` / `--file FILE` | | run the lines of FILE (`-` = stdin) as commands |

- Any other first word sends the whole line as one console command (`ctl ...`, `boypos`, `help` ...).
- When nothing listens on the port (or the game's `InsideDev.Mcp.dll` has no `/cmd`), the command goes through the file
  bridge. The bridge folder comes from `--bridge` or `--game` (or their environment variables); without them it is the
  `_mod\cmd` folder of a running INSIDE.exe. A failure after the request was sent is never retried through the bridge
  (the command may have run).
- A reply `started: ... job=jN` is followed with `ctl job jN` until it ends. Ctrl+C while following sends `ctl stop`.
- Exit codes: 0 ok; 1 the command failed (busy, usage, failed, not found, unknown ...); 2 the game is not reachable, or
  the request may have run but no reply came back; 3 refused (a reply or header `refused`, `unauthorized`, `unsafe`,
  `confirm_required`, HTTP 401 / 403; was 1 before 0.11.0); 130 Ctrl+C.
- `state` uses `GET /state` (through the bridge: `ctl status json`); both need `InsideDev.Play.dll`, and a reply that is
  not JSON exits 1 with a hint. The text snapshot of the bridge command `state` (the `savepoint:` line that the workflows
  of RECORD_REPLAY.md and LAB.md poll) is `snapshot` (= `cmd state`). `ping` uses `GET /ping`.
- `wait [up | ready | savepoint <subscene> <savepoint>] [--timeout S]` (default `ready`, 120 s) answers `ready: pong
  InsideDev 0.11.0: the world is loaded (GET /state) (3.2 s)`, `up: pong ...` or `savepoint: <label> #<n>  (spawn <sub>
  <sp>) (1.0 s)`; on a timeout stderr says `not ready: after 120 s: <reason>` and the exit code is 2.
- `doctor [json]` prints the game's `doctor` reply (exit 1 when it starts with `doctor: fail`). When the game is not
  reachable it prints `unreachable: ...` on stderr and then rows `seen from outside the game:` for the game process, the
  Steam client, the port, the game window and the file bridge (exit 2).

Commands sent through `POST /cmd` count as MCP tool calls (they run through the `command` tool), so the idle restore
clock (section 13) restarts with each one. Commands through the console or the file bridge do not count.

## 13. Restore

`restore` (and the same routine run by the host) puts back what MCP changed:

| Key | Put back |
|---|---|
| job | the running action, ctl job or track playback is cancelled (not a MultiCam capture) |
| `input` | held input and a grab grip released; the agent core removed (human control), `runInBackground` as before |
| `time` | realtime, speed 1, unfrozen; `captureFramerate`, vSync and `runInBackground` as before |
| `vsync` | an explicit `vsync on` / `off` (`time {op:"vsync"}`) ended; the `play.vsync_off` rule applies again (with the default 1 the game's own vSync comes back at once) |
| `record` | a running recording stopped, either source (the file stays) |
| `menu_open` | a pause menu opened by `menu {op:"open"}` closed |
| `campath` | the director camera stopped; field of view, near plane, game speed and freeze put back |
| `grec` | a game-render video stopped and its file closed |
| `pano` | a running panorama capture ended; camera, TAA and post settings put back |
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

The clock's undo (key `time`) runs after all others. `play restore` in the console runs the Play part only; `play
restore list [json]` and `restore {dry_run:true}` show what a restore would do without changing anything.

## 14. Errors and busy

| Code | Meaning | What to do |
|---|---|---|
| `bad_args` | arguments do not match the schema | read the path in the message |
| `busy` | another action, a ctl job or a MultiCam capture holds the control lane; `busy {job, tool, lane, running_s}` | `wait_until {any:[{job:"<id>"}]}`, or `restore` |
| `not_ready` | the main thread did not run the request in time (minimized window, long load), or no body | restore the window, wait for the load; nothing was run |
| `stale_id` | the id's object is gone (section 4.3 lists the messages), or a menu id belongs to an earlier menu state | `observe` again |
| `not_found`, `ambiguous` | no such object / several matches (`candidates`) | use an id or a full path |
| `refused` | a safety rule (cheats off, quit, master volume up, secret devices) | read `hint` |
| `confirm_required` | only with `play.save_guard` on | repeat with `confirm:true` |
| `timeout` | the call ran past its budget; with `job` it continues | wait for the job |
| `cancelled` | cancelled by the client or by restore | |
| `game_error` | the game threw | read the message; the InsideDev log has the stack |
| `not_built`, `internal` | a missing part or a bug | report it |
| `unauthorized` | the game has `mcp.token` set and the request carried no or a wrong `Authorization: Bearer <token>` (HTTP 401) | send the token |
| `unsafe` | defined in the error schema; no tool of this release returns it | treat as `refused` (`inside_ctl.py` does) |

Requests from web pages are refused before any tool runs: a foreign or `null` `Origin`, a cross-site fetch or a wrong
`Host` header gets HTTP 403, a non-JSON MCP body 415. Local clients (the stdio launcher, `inside_ctl.py`, scripts) are not
affected.

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
  reset this clock. A running mcam run, grec, pano, real-time recording, input track recording, srec recording or replay counts as
  work in progress: the host does not run the idle restore while one of them runs.
- **Held input and the client going away.** `act {hold:true}` and a grab grip stay pressed until `release`, the next act,
  a death, a load or a restore. `play.hold_timeout_s` (default 0 = off) is an optional safety release: with a value above
  0, held agent input is let go after that many seconds without a play tool call while the game runs in realtime and no
  job runs it (changes note `hold released: no tool call for 61 s while the game ran in realtime ...`). It applies only
  when the last use of Play was a client's tool call: input held with ctl by a person is never timed.
- **Ids and loads.** An id survives a respawn or reload when its object is back at the same path (section 4.3); other
  ids fail with `stale_id`. Use ids only from the latest `observe` or action result.
- **Load races.** `checkpoint` with `wait` waits for its own load. Loads started any other way, or `wait:false`, need
  section 7.6: `wait_until {any:[{ready:true}]}` alone can be satisfied by the old level. Do not start an input track or a
  recording before the requested savepoint is playable (`observe.checkpoint`).
- **Music-timed places and speed.** Slow motion desyncs them; step-mode runs there are paced to real time. Use `when`.
- **Free camera.** While the InsideDev free camera is on (Insert key, or the dev `camera pose`) the body is not
  controllable; release it before acting.
- **Keyboard touched.** `controller` becomes Digital and several moves read input differently (`controller_digital`).
- **Menus suppress buttons.** After a menu, send one neutral step before a jump or action.
- **Busy from outside.** A ctl job, a dev-profile action or a MultiCam capture (a separate InsideDev feature) holds the
  control lane; `restore` does not cancel a MultiCam capture (console `mcam stop` does). Dev tools such as `command` do
  not take the lane and can change time or load while a play action runs.
- **Long tracks.** Detached jobs end after 45 minutes, so a track playback that needs longer on the wall clock is cut (section 8).
- **Saves.** Playing forward writes `INSIDE.sav` (the Steam Cloud save) at checkpoints exactly as a person playing does.
  `checkpoint next / prev / goto` write the spawn. Back up the save first if it matters (the dev `save {op:"backup"}` or
  a copy of the file). Tools never call Steam achievements directly, but the game's own code can award them during play,
  as for a person.
- **Director camera and the free camera.** `cam pose` (the free camera) takes the input away from the boy, so an input
  track played under it drives nothing: `cam off` before a `campath` run.
- **`grec` timing.** Since 0.11.0 the video length equals game time in real-time play and in step mode (frames placed by
  game time, section 4.6); vSync no longer matters for it. Scripts written for 0.10.0 that compute `video time = event +
  grabs_at_start / 30` must use `<file>.times.csv` (`slot_game_time`) instead, and the meaning of `written` / `dropped`
  in `grec status` changed (frames in the file / pictures lost). A grec file of a crashed or killed game has no duration
  (ffmpeg ends with the game); it plays, and a remux adds the duration. A clean quit waits for ffmpeg.
- **The game's own picture vs the desktop.** `time {op:"record"}` and `rec start` record the game's frames since 0.11.0.
  Scripts that expect `rec_*.mkv` files or the desktop framing must pass `source:"desktop"` / `rec start ... desktop`.
- **Panorama and the free camera.** `pano` uses the free camera and gives it back, also when `restore` ends the capture (section 4.7).
- **First-person flicker.** Observed on a first-person pass of a long bunker route: the frame flipped at random between two
  brightness levels (24 and 29 of 255) in one hall, though the eye stood still, while the director camera in the same seconds was
  steady. The eye looked along the level, and the main camera's far clip of 600 m made the game stream far levels in (43 of 251
  active). Setting the far clip to 200 m took the flips from 73 % of the frames to 0 % and the active levels to 31, with the dev
  tool `reflect {op:"set", object:"_Camera_Main", component:"Camera", member:"farClipPlane", value:200, restore_on_end:true}`. All
  of that is observed; which streamed level disturbs the frame is not known. Pick a far clip longer than your longest sight line.
- **Updating Play.** `ext load` does not replace a loaded DLL: restart the game after copying a new one. A DLL that failed
  to load (Play copied before Mcp) is tried again only after a restart.
- **Steam must run.** Without the Steam client the game closes itself about 2 s after it starts.
- **The tool sleeps while unused.** With no client and the editor closed, Play's ticks and the core's scanners sleep and
  catch up when the next call wakes them; the first call after a quiet time can take a little longer.

## 16. Settings

Settings are `key=value` lines in `<game>\_mod\editor.cfg` (lines starting with `#` are comments). The game reads the file
once at start and rewrites all of it when any editor setting changes, so edit the file with the game closed. While the
game runs, use the `cfg` command (new in 0.11.0, console, bridge or the `command` tool): `cfg [list] [filter] [json]`
lists every key with its value, default, owner and meaning; `cfg get <key> [json]`; `cfg set <key> <value>` changes and
saves it; `cfg reset <key>` returns it to the default. The `play.*` keys are re-read from memory about once a second (and
when Play wakes), so a `cfg set` applies without a restart. The keys that decide what the network side may do
(`mcp.*`, `play.cheats`, `play.save_guard`, `play.guide_dir`, `ffmpeg.path`, anything with `token` or `password`) can be
changed only from the in-game console, not by a remote client. `mcp.enabled` and `mcp.port` also have their own console
commands.

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
| `play.notes_key` | `Home` | notes hotkey (Unity KeyCode name; `none` or `off` = no hotkey, and the notes tick then sleeps until Play is used again) | editor.cfg |
| `play.vsync_off` | `1` | vSync rule (section 4.5): `1` = off while an agent works or the clock is held, else the game's own; `0` = never touched; `2` = off from game start (the 0.10.0 meaning of `1`) | editor.cfg, `cfg set`; `vsync on / off / auto` overrides it until restore |
| `play.hold_timeout_s` | `0` | optional safety release: seconds without a play tool call after which held agent input is released while the game runs in realtime; 0 = never (section 15) | editor.cfg, `cfg set` |
| `play.step_pacing` | `auto` | `auto` = step-mode runs as fast as the game renders, paced to real time only in music-timed places; `always` = 60 steps per second; `never` = no pacing | editor.cfg, `cfg set` |
| `play.perception_scan` | `all` | which objects the observe component pass keeps: `all` (switched-off objects too) or `active` | editor.cfg, `cfg set` |
| `play.perception_bg_s` | `0` | seconds between background passes of the observe lists while an agent is active; 0 = only when an observation needs them | editor.cfg, `cfg set` |
| `film.encoder` | `auto` | encoder of grec and rec: `auto` tests `hevc_nvenc`, `hevc_amf`, `hevc_qsv`, `libx265`, `libx264` and takes the first that works; or one name | editor.cfg, `cfg set` |
| `film.rec_max_s` | `14400` | longest desktop recording in seconds; 0 = no cap | editor.cfg, `cfg set` |
| `ffmpeg.path` | empty | full path of `ffmpeg.exe`, tried first by every recorder (then `INSIDEDEV_FFMPEG`, `<game>\_mod\ffmpeg\ffmpeg.exe`, `PATH`, `C:\ffmpeg\bin`) | editor.cfg, `cfg set` |

Changed defaults in 0.11.0 that touch this document, with the setting that brings the old behaviour back:

| What | 0.10.0 | 0.11.0 | Old behaviour |
|---|---|---|---|
| vSync with Play installed | off from game start (`play.vsync_off=1`) | off only while an agent works or holds the clock | `play.vsync_off=2` |
| `time {op:"record"}` / `rec start` source | desktop | the game's own frames | `source:"desktop"` / `rec start ... desktop` |
| bare file names of grec / pano | the game's working folder | `<game>\_mod\recordings\` | give a full path |
| pano | the game runs during the faces | the game is held | `live` |
| Background scanners | always on | only while the tool is used | `background=always` (README) |

Example `editor.cfg` lines:

```
mcp.idle_restore_s=1800
play.cheats=1
play.guide_max=steps
play.vsync_off=2
```
