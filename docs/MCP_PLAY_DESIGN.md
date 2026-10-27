# MCP play design: any AI model plays and drives INSIDE through InsideDev

> These are the design notes written during development. For usage read `docs/MCP.md` and `docs/PLAY.md`. Statements
> marked INFERRED, unverified or dated describe the state at that time.

Status 2026-10-25 (architect pass; integration pass the same day: every stub implemented, see section 15; audit fixes, section 21). Inputs: an internal plan ("PLAN" below) and five research reports ("report 01" to "report 05"); neither is shipped.
This document was the contract for the nine roles (section 10) that built the parts in parallel. The contract also
existed as compiling code: stub files with frozen public signatures, tool definitions (names, parameters, defaults,
limits, annotations, output schemas) and doc headers. The core DLL (now `bin/InsideDev.dll`) plus all stubs built with 0
errors, and the offline contract test passed (49 checks). Nothing here had run in the game at that point.

Words used below:
- **step**: one logic step = one `FixedUpdate` = 1/60 s of game time (`LogicManager.stepCount`).
- **frame**: one rendered frame (`Time.frameCount`). While stepping, one frame = one step.
- **main thread**: the Unity main thread. MCP requests arrive on worker threads.
- **lane**: an exclusive slot for multi-frame work. There is one lane, `control`.
- **VERIFIED / INFERRED**: the labels of the reports. INFERRED behaviour needs a game test.

---

## 1. Scope and status

| Part | State after this pass |
|---|---|
| `src/mcp/ServerApi.cs` | Types complete: profiles, error codes, `ToolDef`, `ToolResult` envelopes, resource / prompt types, `CallInfo`, abstract `McpJob` and `CallContext`, `IMcpToolProvider`, `IToolHost`. Only the `ToJson` serializers are stubs (SERVER). |
| `src/mcp/ServerSchema.cs` | Schema builder implemented and tested. `Schema.Validate` is a stub (SERVER). |
| `src/mcp/McpServer.cs`, `McpStdio.cs` | v0.5 code adapted to the new types: profiles by path, validation call, `CallInfo`. SSE, cancel, resources, prompts, auth: SERVER. |
| `src/mcp/Sse.cs` | Stub (SERVER). |
| `src/mcp/Extension.cs` | v0.5 code adapted: dev tools moved into `DevToolsProvider`, new `IToolHost` members delegate to `McpRegistry`. Dispatch and policies: HOST. |
| `src/mcp/HostRegistry.cs`, `HostContext.cs`, `Jobs.cs`, `Shot.cs` | Stubs (HOST). |
| `src/mcp/DevTools.cs` | Final definitions of the 13 dev tools, `screenshot` and `jobs`. Handlers: HOST. |
| `src/play/PlayExtension.cs`, `PlayShared.cs` | Implemented (ARCH, frozen): extension, provider, `PlayRegistry`, helpers. |
| 22 module files in `src/play/` | Final tool definitions in each `Register()`; handlers return `not_built`; public APIs throw `NotImplementedException`. |
| offline tests (not shipped) | Test kit, contract tests, API probe, stub snapshot, snapshot build. |

Deferred (not in this workflow): a virtual pad for the title screen and the death-skip button (Hook B in report 02),
typed tunables, the 2026-07-28 protocol (`server/discover`), elicitation, MCP tasks. Input record / replay through MCP
was added later as the tool `input_track` (section 17); scene recording (`srec`) stays with the replay workflow.

---

## 2. Architecture

```
MCP client (Claude Code, Codex, Cursor, Gemini CLI, a local model)
   |  HTTP 127.0.0.1:47811  /mcp (= dev) | /mcp/dev | /mcp/play | /mcp/all     or stdio: InsideDev.McpStdio.exe --profile play|dev|all
   |  plain, no session: POST /cmd, GET /state, GET /ping (section 19); tools/inside_ctl.py and the file bridge (_mod/cmd)
   v
InsideDev.Mcp.dll  (namespace InsideDev.McpExt)
   McpServer   HTTP + JSON-RPC, sessions, SSE progress, cancel map, list_changed stream, arg validation      [SERVER]
   McpExtension (IInsideDevExtension + IToolHost)                                                           [HOST]
     McpRegistry   providers -> tools / resources / prompts per profile                                    [HOST]
     HostCallContext + MainQueue   main-thread queue (no zombies), waits, progress, jobs                   [HOST]
     HostJobs      lanes, busy, detached jobs                                                              [HOST]
     McpShot       screenshots (preview / full / marked)                                                   [HOST]
     DevToolsProvider   the 13 v0.5 tools + screenshot + jobs                                              [HOST]
   v                                         McpRegistry.Add(provider)
InsideDev.Play.dll (namespace InsideDev.Play, hot-loadable with "ext load")
   PlayExtension (IInsideDevExtension + IMcpToolProvider) -> PlayRegistry <- each module's static Register() [ARCH]
   AgentCore : GameInput.Core   input for every body                                                       [INPUT]
   Stepper / Waits / Restorer   clock, waits, restore registry                                             [TIME]
   Observer / Perception / ChangeLog   observation, ids, changes                                           [OBSERVE]
   Navigator / NavGeometry      move_to, level slice                                                       [NAV]
   PlayLevels / Saves / Menus   loads, saves, menus                                                        [WORLD_A]
   WorldCamera / WorldLogic / Hazards / AudioRender / Reflect / StateDiff   world tools                    [WORLD_B]
   GuideBook / Notes            playbooks, flagged moments                                                 [GUIDE]
   CtlExtension / CtlCommands   "ctl" console commands through AgentCore + Stepper + the control lane       [CTL]
   v
Game code (GameInput, Boy, Huddle, GameManager, SavepointManager, MenuManager, CameraScript, SignalManager ...)
```

Loading. InsideDev loads every `_mod\InsideDev.*.dll` in name order, so `InsideDev.Mcp.dll` comes before
`InsideDev.Play.dll`, and Play's reference to `InsideDev.Mcp` resolves through the core's `AssemblyResolve` handler to the
copy already loaded. `ext load` adds Play later without a restart. Play needs Mcp: without it, Play fails to load and the
core logs the problem. Every class of an extension DLL that implements `IInsideDevExtension` is created; Play has two:
`PlayExtension` (the MCP provider) and `CtlExtension` (only the `ctl` console word, section 18). A type that fails to load makes `GetTypes()` throw and the whole DLL fails: keep
game-type references resolvable (the build against the real game assemblies checks this).

Path of a tool call:
1. A worker thread (one per HTTP connection) reads the request. The server finds the `ToolDef` in `host.Tools(profile)`,
   runs `Schema.Validate` (strict, fills defaults), creates a `CallInfo` and calls `host.Call(def, args, info)`.
2. HOST wraps the `CallInfo` in a `HostCallContext`, finds the provider (`McpRegistry.OwnerOf`), audits non-read-only
   calls, and calls `provider.Call(tool, args, ctx)` on the same worker thread.
3. The provider reads and writes the game only through `ctx.Main` / `ctx.MainDo`. A long tool loops on the worker:
   `Stepper.Run`, `ctx.WaitUntil`, `ctx.WaitFrames`. It never blocks the main thread.
4. `MainQueue.Pump` runs in `McpExtension.Tick`, which is `DevRoot.Update`, after the frame's `FixedUpdate`s. It runs
   queued items under a budget of about 4 ms per frame, and at least one item.
5. The result goes back as one JSON response, or as an SSE stream when the request carried a `progressToken`.

Why the parts are split like this:
- Tools live in an extension so new tools do not need a new `InsideDev.Mcp.dll`. `listChanged` tells clients.
- The replay workflow owned the core. Play uses only the public core API (`bin/InsideDev.dll`) and game code, and never
  edits the core.
- Waiting happens on the worker thread with short main-thread checks, so no frame is ever held.

---

## 3. Tools per profile

### 3.1 Profiles

| Profile | Endpoint | Tools | tools/list size (chars / 4) | Who |
|---|---|---|---|---|
| play | `/mcp/play`, stdio `--profile play` | 14 | about 16.5k tokens (input_track about 1.5k, geometry 0.6k, `when` on act / move_to / interact 1.9k) | an AI that plays |
| dev | `/mcp` (v0.5 path), `/mcp/dev`, stdio default | 33 | about 26.1k | debugging, puzzles, ARG work |
| all | `/mcp/all`, stdio `--profile all` | 40 | about 34.3k | everything (at Cursor's 40-tool cap: the next tool must be an op of an existing one) |

- **play**: `observe`, `act`, `move_to`, `interact`, `wait_until`, `time`, `checkpoint`, `menu`, `screenshot`, `guide`, `notes`, `restore`, `input_track`, `geometry` (shared with dev since the audit fixes, section 21).
- **dev**: the v0.5 tools `game_state`, `screenshot`, `find_objects`, `inspect`, `select`, `set_active`, `fsm_graph`, `fsm_find`, `fsm`, `signal`, `show_panel`, `log`, `command`, then `jobs`, `level`, `stream`, `save`, `ui`, `camera`, `logic`, `body`, `hazard`, `audio`, `render`, `reflect`, `state_diff`, `geometry`, `run_scenario` (GUIDE, added in the build pass), and the shared `observe`, `time`, `wait_until`, `restore`, `input_track` (section 17), `geometry`.
- **all**: both lists.

Rules:
- Tool names are unique across all profiles. The same definition is served on every profile it is tagged with.
- World tools take an `op` enum, so the dev profile stays under 40 tools. This follows Anthropic's "consolidate tools along workflows" guidance and Cursor's tool cap.
- `observe` has `_meta["anthropic/alwaysLoad"] = true`, so Claude Code does not defer it.
- Kept out of play: `command`, `set_active`, `fsm`, `signal`, `reflect`, cheats (`checkpoint goto|next|prev` only with `play.cheats`).

### 3.2 PLAN names -> tool and op

| PLAN (section) | Here |
|---|---|
| observe, act, move_to, interact, wait_until, time, checkpoint, menu, screenshot, guide, notes, restore (2) | the play tools of the same names |
| level_status, level_list, wait_ready, load_checkpoint (chapter / respawn / quiet), load_chapter (3) | `level` op status, list, wait_ready, load_checkpoint (mode), load_chapter; also `wait_until {ready}` |
| checkpoint_reload, checkpoint_step (report 03) | `level` op reload (kind), step (dir) |
| stream_pin, stream_load, stream_freeze (3) | `stream` op pin / unpin, load, freeze / unfreeze |
| save_info, save_lock, orbs, wrist_secret, ending, savepoints_near (3) | `save` op info, lock / unlock, orbs, wrist_secret, ending, savepoints_near; plus op backup |
| time_state, time_scale, time_freeze, time_step, rng_seed, lockstep (3) | `time` op state, speed, pause / step_mode, step, rng_seed; lockstep = step mode (captureFramerate 60) + rng_seed |
| camera_state, camera_boxes, camera_box_set, camera_follow, camera_poi, camera_fx, camera_pose / freecam (3) | `camera` op state, boxes, box_set, follow, poi, fx, pose, release |
| triggers_near, trigger_fire, signal_ports, signal_in, signal_wire, fsm_list, spawn_clone, body_set, anim_set, interactables_near (3) | `logic` op of the same name (+ signal_out, signal_unwire) |
| bodies, teleport, hat, swap (3) | `body` op list, teleport, hat, swap |
| hazards_near, hazard_set, air, god (3) | `hazard` op near, set, air, god |
| audio_post, audio_rtpc, audio_state, music_state, render_toggle (3) | `audio` op post, stop, rtpc, state, switch, music; `render` op post, fog, default_lights, debug_view |
| call with args, find_methods, describe_type, state_diff, verified writes (3) | `reflect` op call, find_methods, describe_type, get, set (verdict); `state_diff`; verdicts also in `logic`, `camera`, `body`, `hazard` |
| debug_flags, overlay_text, title_hide, menu_lock, Playdead screenshot route (3) | `render` op debug_flags, shot_route; `ui` op overlay_text, title_hide, menu_lock (+ settings, message_answer, savepoint_menu) |
| input_mode, geometry_slice (report 02) | `act` input_mode; `geometry` |
| server fixes (4) | SERVER and HOST, section 4.6 |
| multi-frame jobs (1) | `jobs` (dev) and `wait_until {job}` |

### 3.3 Reference (generated from the code by the offline contract test)

The tables below are the tool definitions the live tree registers (regenerated from the audit-fix build, section 21); the test also wrote `tools_<profile>.json`,
the exact `tools/list` payload (not shipped). Output schemas list shapes only: ranges and defaults are dropped (`Schema.Slim`), and
every output schema also has `ok` and `error`. In action results, `observe` has the shape of `observe` with detail
concise and `changes` the shape of section 5.4.

| Tool | Owner | Profiles | Annotations |
|---|---|---|---|
| `game_state` | HOST | dev | readOnly |
| `screenshot` | HOST | play, dev | readOnly |
| `find_objects` | HOST | dev | readOnly |
| `inspect` | HOST | dev | readOnly |
| `select` | HOST | dev | idempotent |
| `set_active` | HOST | dev | idempotent |
| `fsm_graph` | HOST | dev | readOnly |
| `fsm_find` | HOST | dev | readOnly |
| `fsm` | HOST | dev | destructive |
| `signal` | HOST | dev | destructive |
| `show_panel` | HOST | dev | idempotent |
| `log` | HOST | dev | readOnly |
| `command` | HOST | dev | destructive |
| `jobs` | HOST | dev | write |
| `time` | TIME | play, dev | write |
| `observe` | OBSERVE | play, dev | readOnly, alwaysLoad |
| `act` | INPUT | play | alwaysLoad |
| `move_to` | NAV | play | alwaysLoad |
| `interact` | INPUT | play | alwaysLoad |
| `wait_until` | TIME | play, dev | alwaysLoad |
| `restore` | TIME | play, dev | idempotent |
| `checkpoint` | WORLD_A | play | destructive |
| `level` | WORLD_A | dev | destructive |
| `stream` | WORLD_A | dev | write |
| `menu` | WORLD_A | play | write |
| `ui` | WORLD_A | dev | destructive |
| `save` | WORLD_A | dev | destructive |
| `guide` | GUIDE | play | readOnly |
| `run_scenario` | GUIDE | dev | destructive |
| `notes` | GUIDE | play | write |
| `geometry` | NAV | play, dev | readOnly |
| `camera` | WORLD_B | dev | write |
| `logic` | WORLD_B | dev | destructive |
| `body` | WORLD_B | dev | destructive |
| `hazard` | WORLD_B | dev | write |
| `audio` | WORLD_B | dev | write |
| `render` | WORLD_B | dev | idempotent |
| `reflect` | WORLD_B | dev | destructive |
| `state_diff` | WORLD_B | dev | idempotent |
| `input_track` | INPUT | play, dev | write |

#### `game_state` (HOST; dev; readOnly)

Text snapshot of the running game: InsideDev version, frame, boy position, savepoint, music position, active scenes, nearby triggers. For structured state use observe.

Input: none.

Output (plus `ok`, `error`): `text` string; `truncated` boolean; `total_chars` integer; `offset` integer.

#### `screenshot` (HOST; play, dev; readOnly)

Capture the game screen. preview = inline PNG (default 960 px wide, about 700 image tokens); marked = preview with observe ids drawn on objects; full or path = PNG file only. The JSON text has ok, mode, width, height, src_width, src_height, path, black (an almost black frame), frame and marks [{id, label, x, y}] (pixels in the image). No structuredContent, so clients that pass only structured results still get the image.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `mode` | enum | "preview" | preview, marked, full, path | preview, marked, full or path. |
| `max_width` | integer | 960 | 0 .. 4096 | Scale down to at most this width (preview / marked). 0 = no scaling. |
| `clean` | boolean | true |  | Hide InsideDev panels, HUD and overlays for the shot. |

#### `find_objects` (HOST; dev; readOnly)

Smart search over loaded objects by name, ranked by match and what the object does. Returns up to limit rows.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `query (req)` | string |  |  | Words to match against object names. |
| `category` | enum |  | animation, sounds, scripts, triggers, fsm, characters | Optional filter. |
| `limit` | integer | 25 | 1 .. 100 | Rows. |

Output (plus `ok`, `error`): `text` string; `truncated` boolean; `total_chars` integer; `offset` integer.

#### `inspect` (HOST; dev; readOnly)

Describe an object: path, active state, components and their fields, references, FSM dump. Output is capped at max_chars; page with offset.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `object (req)` | string |  |  | Object id (o12), hierarchy path, #instanceID or unique name. |
| `deep` | boolean | false |  | Expand nested serialized data. |
| `max_chars` | integer | 20000 | 1000 .. 200000 | Cap on returned text. |
| `offset` | integer | 0 | 0 .. 100000000 | Start of the page (characters). |

Output (plus `ok`, `error`): `text` string; `truncated` boolean; `total_chars` integer; `offset` integer.

#### `select` (HOST; dev; idempotent)

Select an object in the InsideDev editor (Inspector, Explorer and graph follow the selection). Editor-only, no game change.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `object (req)` | string |  |  | Object id, path, #instanceID or unique name. |

Output (plus `ok`, `error`): `text` string; `truncated` boolean; `total_chars` integer; `offset` integer.

#### `set_active` (HOST; dev; idempotent)

Switch a GameObject on or off (recorded in History, undo with command 'undo'). Re-reads the state after 2 frames.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `object (req)` | string |  |  | Object id, path, #instanceID or unique name. |
| `active (req)` | boolean |  |  | true = on, false = off. |

Output (plus `ok`, `error`): `text` string; `truncated` boolean; `total_chars` integer; `offset` integer.

#### `fsm_graph` (HOST; dev; readOnly)

Dump a PlayMaker state machine: states, exits with events, current state. open_panel also shows it in the FSM Graph panel.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `object (req)` | string |  |  | Object that has the FSM. |
| `fsm` | string |  |  | FSM name when the object has several. |
| `open_panel` | boolean | false |  | Also select it and open the FSM Graph panel. |

Output (plus `ok`, `error`): `text` string; `truncated` boolean; `total_chars` integer; `offset` integer.

#### `fsm_find` (HOST; dev; readOnly)

Search every loaded PlayMaker FSM (including switched-off ones) for events, signal inputs, states, action settings and signal connections.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `text (req)` | string |  |  | Text to search for. |
| `limit` | integer | 30 | 1 .. 200 | Hits. |

Output (plus `ok`, `error`): `text` string; `truncated` boolean; `total_chars` integer; `offset` integer.

#### `fsm` (HOST; dev; destructive)

Send an event to a PlayMaker FSM, force it into a state, or read/set one of its variables. event and state cannot be undone.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `object (req)` | string |  |  | Object that has the FSM. |
| `action (req)` | enum |  | event, state, var | event, state or var. |
| `name (req)` | string |  |  | Event, state or variable name. |
| `value` | string |  |  | New variable value (action var; omit to read). |
| `fsm` | string |  |  | FSM name when the object has several. |

Output (plus `ok`, `error`): `text` string; `truncated` boolean; `total_chars` integer; `offset` integer.

#### `signal` (HOST; dev; destructive)

Fire one of an object's Playdead signal outputs (raises every connection of that output, like the game does). Cannot be undone.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `object (req)` | string |  |  | Object with the signal output. |
| `output (req)` | string |  |  | Signal output name (see inspect / fsm_find). |

Output (plus `ok`, `error`): `text` string; `truncated` boolean; `total_chars` integer; `offset` integer.

#### `show_panel` (HOST; dev; idempotent)

Open the InsideDev editor on a panel, optionally docking and sizing it. Panels: spawns, levels, triggers, hidden, world, audio, inspector, cheats, console, diag, settings, objects, explorer, graph, events, sounds, atimeline, wwiseapi, history, audiodb, logic, modified, fsmgraph, fsmfind; none closes the editor.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `panel (req)` | string |  |  | Panel id, or none. |
| `dock` | enum |  | left, right, bottom | Dock side. |
| `size` | number |  | 0.1 .. 0.7 | Dock size as a fraction of the screen (left/right 0.12-0.6, bottom 0.1-0.7). |

Output (plus `ok`, `error`): `text` string; `truncated` boolean; `total_chars` integer; `offset` integer.

#### `log` (HOST; dev; readOnly)

The last lines of InsideDev's log (command output, errors, events).

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `lines` | integer | 60 | 1 .. 400 | How many lines. |

Output (plus `ok`, `error`): `text` string; `truncated` boolean; `total_chars` integer; `offset` integer.

#### `command` (HOST; dev; destructive)

Run any InsideDev bridge/console command and return its output plus the log lines it wrote; 'help' lists them. OS-input commands (keyhold, focustest) are refused: use act. isError is set from the command's own failure text.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `command (req)` | string |  |  | The command line, e.g. 'near 10' or 'get "Housing (4)" Transform localPosition'. |
| `max_chars` | integer | 20000 | 1000 .. 200000 | Cap on returned text. |

Output (plus `ok`, `error`): `text` string; `truncated` boolean; `total_chars` integer; `offset` integer.

#### `jobs` (HOST; dev; write)

List, inspect, await or cancel multi-frame jobs (loads, actions, waits). Cancelling releases inputs and re-freezes the game.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "list" | list, status, await, cancel | list, status, await or cancel. |
| `id` | string |  |  | Job id (j12) for status / await / cancel. |
| `timeout_s` | number | 10 | 0 .. 50 | op await: how long to wait. |

Output (plus `ok`, `error`): `jobs` object[] {id, tool, lane, state, elapsed_s, progress, total, message, error}; `job` object {id, tool, lane, state, elapsed_s, progress, total, message, error}; `result` any; `finished` boolean.

#### `time` (TIME; play, dev; write)

Game clock. The game runs in realtime until the first action call; from then on it is in step mode: frozen between calls, running only inside act, move_to, interact, wait_until and time step. op realtime lets it run (speed scales it). Mines shockwave, lineup and lab lift / water lock run on the music clock: step-mode runs there are paced to real time (the music pauses while frozen), slow motion desyncs them; act / move_to / interact take when to wait for a safe phase inside the call.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "state" | state, step_mode, realtime, pause, resume, step, speed, rng_seed | state, step_mode, realtime, pause, resume, step, speed or rng_seed. |
| `steps` | integer | 1 | 1 .. 600 | op step: logic steps to run (1/60 s each), then freeze. |
| `speed` | number | 1 | 0.05 .. 4 | op speed / realtime: game speed while running. |
| `seed` | integer | 0 | 0 .. inf | op rng_seed: PDRandom.seed, RandomGenerator.ResetAll, UnityEngine.Random.seed. |
| `observe` | enum | "concise" | concise, none | op step: observation in the result. |

Output (plus `ok`, `error`): `mode` enum (step, realtime); `frozen` boolean; `speed` number; `scale` number; `game_scale` number; `capture_fps` integer; `frame` integer; `step` integer; `music_s` number; `loading` boolean; `paused` boolean; `steps_run` integer; `warnings` string[]; `observe` object; `changes` object.

#### `observe` (OBSERVE; play, dev; readOnly, alwaysLoad)

What the agent sees, as JSON: screen and readiness (next = what to call when not ready), the controlled body, nearby objects and hazards with ids and dx/dy in metres, checkpoint and guide topic, events. Call guide() once, then observe. Rules: the game is frozen between action calls (step mode); ids die on every load; at the title menu {op:"submit"} starts; call restore when done. image attaches a screenshot (marked = ids drawn); if your client drops images, use screenshot.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `detail` | enum | "concise" | concise, full | concise (at most about 800 tokens) or full. |
| `radius` | number | 12 | 1 .. 60 | Metres around the body for objects and hazards. |
| `max_objects` | integer | 12 | 0 .. 40 | Objects to list, nearest first. |
| `image` | enum | "none" | none, preview, marked | Attach a screenshot: none, preview or marked. |
| `max_width` | integer | 960 | 160 .. 1920 | Image width (px). |

Output (plus `ok`, `error`): `frame` integer; `step` integer; `screen` enum (title, loading, in_world, dead, cutscene, pause_menu, menu, credits, message, unknown); `ready` boolean; `reason` string; `next` string; `time` object {mode, frozen, scale, music_s}; `body` object {kind, id, name, x, y, z, vx, vy, facing, state, anim, grounded, ...}; `checkpoint` object {subscene, savepoint, name, area, chapter, chapter_name, guide}; `objects` object[] {id, kind, name, dx, dy, dz, dist, actions, state, screen}; `hazards` object[] {id, kind, name, dx, dy, dist, state, eta_s, threat, safe, music_timed}; `events` object[] {step, type, detail}; `stuck` object {calls_without_progress, deaths_here, hint}; `warnings` string[]; `input` object {mode, held, queued, controller, hijacked}; `bodies` object[] {id, kind, name, x, y, controlled, active}; `menu` object {open, group, selected}; `camera` object {overridden, pinned_subscene}; `image` object {width, height, path, black}; `truncated` object.

#### `act` (INPUT; play; alwaysLoad)

Press the game's controls for exact logic steps (60 per second): a list of {stick, jump, action, steps}, at most 600 steps per call. The game runs only for those steps (step mode) and stops early on stop_on. hold keeps the last input pressed after the call; a grab from interact grab stays held (release:true lets go). when waits for a condition first, then acts in the same call. Returns outcome, steps run, changes and a fresh observation.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `inputs` | object[] |  | items 0 .. 64 | Input segments in order. |
| `inputs[].stick` | number[2] | [0,0] | -1 .. 1; items 2 .. 2 | Left stick x (right +), y (up +). 0 or magnitude 0.3..1; /x/ > 0.8 runs. |
| `inputs[].jump` | boolean | false |  | Jump button held (a press needs a release before the next one). |
| `inputs[].action` | boolean | false |  | Action / grab button held (held = grab, short tap under 0.3 s = action). |
| `inputs[].steps` | integer | 1 | 1 .. 600 | Logic steps to hold this input (1/60 s each). |
| `hold` | boolean | false |  | Keep the last segment pressed after the call (until release or the next act). |
| `release` | boolean | false |  | Release held input (also a grab) before the inputs run. |
| `input_mode` | enum |  | agent, mix, human | agent (default on first use), mix (agent + physical pad) or human (give control back). |
| `stop_on` | enum[] | ["death","load"] | death, load, state_change, grounded, airborne, checkpoint, hazard, event | Stop early when one of these happens. |
| `watch` | string[] |  | items 0 .. 8 | Object ids: stop when the state of one changes. |
| `when` | object |  |  | Wait first, then act in the same call: one wait_until condition, e.g. {hazard_eta:{target:"h3", min_s:4}} (4+ s to the next shockwave blow) or {music_time:{from:0.2, to:1.5}}; not met in when_timeout_s = outcome timeout, no input. |
| `when.seconds` | number |  | 0.05 .. 600 | Game s. |
| `when.grounded` | boolean |  |  | On ground. |
| `when.airborne` | boolean |  |  | In air. |
| `when.state` | string |  |  | Body state. |
| `when.near` | object |  |  | Within m. |
| `when.near.target (req)` | string |  |  | Id. |
| `when.near.within` | number | 1 | 0.1 .. 100 | m. |
| `when.far_from` | object |  |  | Beyond m. |
| `when.far_from.target (req)` | string |  |  | Id. |
| `when.far_from.beyond` | number | 5 | 0.1 .. 200 | m. |
| `when.hazard_safe` | string |  |  | Hazard id. |
| `when.hazard_eta` | object |  |  | eta_s >= min_s. |
| `when.hazard_eta.target (req)` | string |  |  | Hazard id. |
| `when.hazard_eta.min_s` | number | 4 | 0 .. 600 | s. |
| `when.music_time` | object |  |  | Music s in [from, to). |
| `when.music_time.from (req)` | number |  | 0 .. 100000 | s. |
| `when.music_time.to (req)` | number |  | 0 .. 100000 | s. |
| `when.music_time.cycle` | number | 0 | 0 .. 10000 | s, 0 auto. |
| `when.fsm_state` | object |  |  | FSM state. |
| `when.fsm_state.object (req)` | string |  |  | Id. |
| `when.fsm_state.fsm` | string |  |  | FSM. |
| `when.fsm_state.state (req)` | string |  |  | State. |
| `when.signal` | object |  |  | Signal fired. |
| `when.signal.object (req)` | string |  |  | Id. |
| `when.signal.output (req)` | string |  |  | Output. |
| `when.object_active` | object |  |  | Object active. |
| `when.object_active.target (req)` | string |  |  | Id. |
| `when.object_active.active` | boolean | true |  | Wanted. |
| `when_timeout_s` | number | 15 | 0.1 .. 45 | Longest wait for when (wall clock s). |
| `then` | enum | "freeze" | freeze, run | step mode: freeze after the call (default) or keep running. |
| `changes` | enum | "compact" | compact, full | compact (near the body, plus deaths, checkpoints, loads, errors) or full (every signal and fsm change). |
| `observe` | enum | "concise" | concise, none | Observation in the result. |

Output (plus `ok`, `error`): `outcome` enum (done, stopped, died, loading, cancelled, timeout, not_controllable, stalled); `stop_reason` string; `next` string; `steps` integer; `requested` integer; `frames` integer; `held` object {stick, jump, action}; `input_mode` enum (agent, mix, human); `warnings` string[]; `changes` object; `observe` object.

#### `move_to` (NAV; play; alwaysLoad)

Walk or run the controlled body to an object id (target), a world x, or a relative dx (metres, + right). Climbs small steps, jumps small gaps (jump_gaps) and stops at walls, pits and ledges it cannot pass with outcome blocked and a reason. Runs the game until arrived / blocked / fell / died / timeout.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `target` | string |  |  | Object id from observe (o12). |
| `x` | number |  | -100000 .. 100000 | World x (m). |
| `dx` | number |  | -500 .. 500 | Metres from the body (+ right, - left). |
| `y` | number |  | -100000 .. 100000 | World y for swimming or climbing targets (m). |
| `gait` | enum | "run" | walk, run | walk or run. |
| `tolerance` | number | 0.3 | 0.05 .. 3 | Arrival tolerance (m). |
| `stop` | enum | "adjacent" | adjacent, exact | adjacent = within grab reach facing the target, exact = at the point. |
| `max_steps` | integer | 600 | 1 .. 1800 | Logic-step limit (60 per second). |
| `jump_gaps` | boolean | true |  | Jump gaps and low walls on the way. |
| `climb` | boolean | true |  | Climb onto ledges in the way. |
| `when` | object |  |  | Wait first, then act in the same call: one wait_until condition, e.g. {hazard_eta:{target:"h3", min_s:4}} (4+ s to the next shockwave blow) or {music_time:{from:0.2, to:1.5}}; not met in when_timeout_s = outcome timeout, no input. |
| `when.seconds` | number |  | 0.05 .. 600 | Game s. |
| `when.grounded` | boolean |  |  | On ground. |
| `when.airborne` | boolean |  |  | In air. |
| `when.state` | string |  |  | Body state. |
| `when.near` | object |  |  | Within m. |
| `when.near.target (req)` | string |  |  | Id. |
| `when.near.within` | number | 1 | 0.1 .. 100 | m. |
| `when.far_from` | object |  |  | Beyond m. |
| `when.far_from.target (req)` | string |  |  | Id. |
| `when.far_from.beyond` | number | 5 | 0.1 .. 200 | m. |
| `when.hazard_safe` | string |  |  | Hazard id. |
| `when.hazard_eta` | object |  |  | eta_s >= min_s. |
| `when.hazard_eta.target (req)` | string |  |  | Hazard id. |
| `when.hazard_eta.min_s` | number | 4 | 0 .. 600 | s. |
| `when.music_time` | object |  |  | Music s in [from, to). |
| `when.music_time.from (req)` | number |  | 0 .. 100000 | s. |
| `when.music_time.to (req)` | number |  | 0 .. 100000 | s. |
| `when.music_time.cycle` | number | 0 | 0 .. 10000 | s, 0 auto. |
| `when.fsm_state` | object |  |  | FSM state. |
| `when.fsm_state.object (req)` | string |  |  | Id. |
| `when.fsm_state.fsm` | string |  |  | FSM. |
| `when.fsm_state.state (req)` | string |  |  | State. |
| `when.signal` | object |  |  | Signal fired. |
| `when.signal.object (req)` | string |  |  | Id. |
| `when.signal.output (req)` | string |  |  | Output. |
| `when.object_active` | object |  |  | Object active. |
| `when.object_active.target (req)` | string |  |  | Id. |
| `when.object_active.active` | boolean | true |  | Wanted. |
| `when_timeout_s` | number | 15 | 0.1 .. 45 | Longest wait for when (wall clock s). |
| `observe` | enum | "concise" | concise, none | Observation in the result. |

Output (plus `ok`, `error`): `outcome` enum (arrived, blocked, fell, died, timeout, cancelled, unreachable, loading); `reason` string; `steps` integer; `distance_left` number; `x` number; `y` number; `events` string[]; `warnings` string[]; `changes` object; `observe` object.

#### `interact` (INPUT; play; alwaysLoad)

Do one thing with an object or the current body state: grab, release, push, pull, climb, climb_down, drop, ladder_up / ladder_down / ladder_off, rope_jump, swim_down / swim_up, surface, hat_detach, hat_wear, sub_enter, sub_boost, sub_exit, lever, valve, button, lift (lift doors), throw (huddle). target = id from observe (default nearest that offers the verb). Runs the game until the move completes or fails; returns state before / after, changes and an observation.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `verb (req)` | enum |  | grab, release, push, pull, climb, climb_down, drop, ladder_up, ladder_down, ladder_off, rope_jump, swim_down, swim_up, surface, hat_detach, sub_boost, sub_exit, lever, valve, button, lift, sub_enter, hat_wear, throw | What to do (see objects[].actions in observe). grab keeps holding across later act / move_to calls; release lets go. |
| `target` | string |  |  | Object id from observe (o12). Default: nearest object offering the verb. |
| `dir` | enum | "toward" | left, right, up, down, toward, away | Direction for push (toward = stick toward the target), pull (always pulls the target toward the body unless left / right; hatches and boards: along their axis), throw, rope_jump, ladder_off (left / right jumps off, else drops), sub_boost, lever (default: the way it can move), valve (right / down = clockwise). |
| `seconds` | number | 1 | 0.05 .. 10 | Duration for push, pull, button hold, lift, throw swing, swim_*, ladder_up / ladder_down, sub_boost charge, valve turning. |
| `approach` | boolean | true |  | Walk to the target first when it is out of reach. |
| `allow_direct` | boolean | false |  | hat_detach: if the gesture fails, take the hat off with MindHat.ForceDetach (a direct game call; off keeps takes input-only). |
| `when` | object |  |  | Wait first, then act in the same call: one wait_until condition, e.g. {hazard_eta:{target:"h3", min_s:4}} (4+ s to the next shockwave blow) or {music_time:{from:0.2, to:1.5}}; not met in when_timeout_s = outcome timeout, no input. |
| `when.seconds` | number |  | 0.05 .. 600 | Game s. |
| `when.grounded` | boolean |  |  | On ground. |
| `when.airborne` | boolean |  |  | In air. |
| `when.state` | string |  |  | Body state. |
| `when.near` | object |  |  | Within m. |
| `when.near.target (req)` | string |  |  | Id. |
| `when.near.within` | number | 1 | 0.1 .. 100 | m. |
| `when.far_from` | object |  |  | Beyond m. |
| `when.far_from.target (req)` | string |  |  | Id. |
| `when.far_from.beyond` | number | 5 | 0.1 .. 200 | m. |
| `when.hazard_safe` | string |  |  | Hazard id. |
| `when.hazard_eta` | object |  |  | eta_s >= min_s. |
| `when.hazard_eta.target (req)` | string |  |  | Hazard id. |
| `when.hazard_eta.min_s` | number | 4 | 0 .. 600 | s. |
| `when.music_time` | object |  |  | Music s in [from, to). |
| `when.music_time.from (req)` | number |  | 0 .. 100000 | s. |
| `when.music_time.to (req)` | number |  | 0 .. 100000 | s. |
| `when.music_time.cycle` | number | 0 | 0 .. 10000 | s, 0 auto. |
| `when.fsm_state` | object |  |  | FSM state. |
| `when.fsm_state.object (req)` | string |  |  | Id. |
| `when.fsm_state.fsm` | string |  |  | FSM. |
| `when.fsm_state.state (req)` | string |  |  | State. |
| `when.signal` | object |  |  | Signal fired. |
| `when.signal.object (req)` | string |  |  | Id. |
| `when.signal.output (req)` | string |  |  | Output. |
| `when.object_active` | object |  |  | Object active. |
| `when.object_active.target (req)` | string |  |  | Id. |
| `when.object_active.active` | boolean | true |  | Wanted. |
| `when_timeout_s` | number | 15 | 0.1 .. 45 | Longest wait for when (wall clock s). |
| `observe` | enum | "concise" | concise, none | Observation in the result. |

Output (plus `ok`, `error`): `outcome` enum (done, failed, not_available, out_of_reach, died, loading, timeout, cancelled); `verb` string; `target` string; `state_before` string; `state_after` string; `steps` integer; `detail` string; `direct_calls` string[]; `warnings` string[]; `changes` object; `observe` object.

#### `wait_until` (TIME; play, dev; alwaysLoad)

Let the game run until one condition in any holds (steps, seconds, grounded, controllable, ready, checkpoint_changed, dead, alive, state, screen, near, far_from, hazard_safe, hazard_eta, music_time, fsm_state, signal, object_active, job), then freeze. A timeout is not an error: check satisfied. Use after loads, respawns and timed hazards; to act right after a phase use act / move_to / interact {when}.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `any (req)` | object[] |  | items 1 .. 8 | Conditions; the first that holds ends the wait. Each item has exactly one key. |
| `any[].steps` | integer |  | 1 .. 36000 | After this many logic steps. |
| `any[].seconds` | number |  | 0.05 .. 600 | After this much game time. |
| `any[].grounded` | boolean |  |  | Body on the ground. |
| `any[].airborne` | boolean |  |  | Body in the air. |
| `any[].controllable` | boolean |  |  | Input reaches the body. |
| `any[].ready` | boolean |  |  | Load finished and playable (PlayLevels.Ready). |
| `any[].loaded` | boolean |  |  | No load in progress. |
| `any[].checkpoint_changed` | boolean |  |  | A new savepoint was reached. |
| `any[].dead` | boolean |  |  | Body died. |
| `any[].alive` | boolean |  |  | Body alive again. |
| `any[].state` | string |  |  | Body state name (Run, Jump, Ladder, Swim, ...). |
| `any[].screen` | enum |  | title, loading, in_world, dead, cutscene, pause_menu, menu, credits, message, unknown | Screen value. |
| `any[].near` | object |  |  | Body within distance of an object. |
| `any[].near.target (req)` | string |  |  | Object id or path. |
| `any[].near.within` | number | 1 | 0.1 .. 100 | Metres. |
| `any[].far_from` | object |  |  | Body farther than distance from an object or hazard. |
| `any[].far_from.target (req)` | string |  |  | Id or path. |
| `any[].far_from.beyond` | number | 5 | 0.1 .. 200 | Metres. |
| `any[].hazard_safe` | string |  |  | Hazard id: its threat is over or the body is in cover. |
| `any[].hazard_eta` | object |  |  | A hazard's eta_s is at least min_s (the shockwave: that many seconds before the next blow). |
| `any[].hazard_eta.target (req)` | string |  |  | Hazard id (h3). |
| `any[].hazard_eta.min_s` | number | 4 | 0 .. 600 | Seconds. |
| `any[].music_time` | object |  |  | Music position in [from, to) s, folded into cycle (0 = the running shockwave's cycle, else not folded); from > to wraps. |
| `any[].music_time.from (req)` | number |  | 0 .. 100000 | s. |
| `any[].music_time.to (req)` | number |  | 0 .. 100000 | s. |
| `any[].music_time.cycle` | number | 0 | 0 .. 10000 | Cycle length (s), 0 = auto. |
| `any[].fsm_state` | object |  |  | An FSM enters a state. |
| `any[].fsm_state.object (req)` | string |  |  | Object id or path. |
| `any[].fsm_state.fsm` | string |  |  | FSM name. |
| `any[].fsm_state.state (req)` | string |  |  | State name. |
| `any[].signal` | object |  |  | A signal output fires. |
| `any[].signal.object (req)` | string |  |  | Object id or path. |
| `any[].signal.output (req)` | string |  |  | Signal output name. |
| `any[].object_active` | object |  |  | An object becomes active / inactive. |
| `any[].object_active.target (req)` | string |  |  | Id or path. |
| `any[].object_active.active` | boolean | true |  | Wanted state. |
| `any[].job` | string |  |  | Job id (j3): it finished. |
| `timeout_s` | number | 20 | 0.1 .. 50 | Wall-clock limit. |
| `max_steps` | integer | 3600 | 1 .. 36000 | Game-step limit. |
| `then` | enum | "freeze" | freeze, run | step mode: freeze after (default) or keep running. |
| `observe` | enum | "concise" | concise, none | Observation in the result. |

Output (plus `ok`, `error`): `satisfied` boolean; `which` integer; `condition` object; `timed_out` boolean; `waited_steps` integer; `elapsed_ms` number; `observed` object; `job` object {id, tool, lane, state, elapsed_s, progress, total, message, error}; `warnings` string[]; `changes` object; `observe` object.

#### `restore` (TIME; play, dev; idempotent)

Put the game back to normal: cancel the running action, release held input, give control back to the player, realtime at speed 1, camera back to the game, god / hazards / air / stream pins / menu lock / render and audio changes made through MCP undone. Lists what it reset. The save lock is kept unless include_save_lock.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `only` | string[] |  | items 0 .. 32 | Only these registry keys or key prefixes (camera, time, input, hazard:, ...). Default all. |
| `include_save_lock` | boolean | false |  | Also release the save lock (the game then writes INSIDE.sav). |
| `keep_agent_input` | boolean | false |  | Keep AgentCore installed (neutral input) instead of human control. |
| `observe` | enum | "concise" | concise, none | Observation in the result. |

Output (plus `ok`, `error`): `reset` object[] {key, what, result}; `kept` string[]; `errors` string[]; `observe` object.

#### `checkpoint` (WORLD_A; play; destructive)

Checkpoints (savepoints). current / list are read-only. reload = respawn at the last checkpoint (like dying, no save write; also skips the death wait). next / prev / goto jump to another checkpoint: a cheat, only when the user enabled play.cheats (EditorState play.cheats = 1); it writes the new spawn into INSIDE.sav. Waits until playable unless wait is false; a load still running after timeout_s comes back as job.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "current" | current, list, reload, next, prev, goto | current, list, reload, next, prev or goto. |
| `subscene` | integer |  | 0 .. 999 | op goto: savepoint subscene index (0..82, see list). |
| `savepoint` | integer |  | 0 .. 999 | op goto: savepoint index. |
| `filter` | string |  |  | op list: name filter. |
| `limit` | integer | 40 | 1 .. 200 | op list: rows. |
| `offset` | integer | 0 | 0 .. 1000 | op list: first row. |
| `wait` | boolean | true |  | Wait until playable after a load. |
| `timeout_s` | number | 45 | 1 .. 50 | Wait limit; when it runs out the result has job. |

Output (plus `ok`, `error`): `op` enum (current, list, reload, next, prev, goto); `outcome` enum (done, loading, refused); `current` object {subscene, savepoint, name, area, chapter, chapter_name, guide}; `checkpoints` object[] {subscene, savepoint, name, area, chapter, chapter_name, guide, reached, discovered}; `total` integer; `job` object {id, tool, lane, state, elapsed_s, progress, total, message, error}; `backup` string; `warnings` string[]; `observe` object.

#### `level` (WORLD_A; dev; destructive)

Level state and loads. status / list are read-only; wait_ready waits for the ready predicate. load_checkpoint (mode respawn | quiet | chapter), load_chapter (0..65), reload (respawn | chapter | newgame), step (next | prev savepoint). chapter, load_chapter, reload chapter / newgame write the Steam Cloud save and unlock chapters; respawn writes the spawn; quiet locks the save and writes nothing. No confirm or backup is needed unless the user set EditorState play.save_guard = 1.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "status" | status, list, wait_ready, load_checkpoint, load_chapter, reload, step | status, list, wait_ready, load_checkpoint, load_chapter, reload or step. |
| `subscene` | integer |  | 0 .. 999 | Savepoint subscene index. |
| `savepoint` | integer |  | 0 .. 999 | Savepoint index. |
| `chapter` | integer |  | 0 .. 65 | Chapter index. |
| `mode` | enum | "respawn" | respawn, quiet, chapter | op load_checkpoint. |
| `kind` | enum | "respawn" | respawn, chapter, newgame | op reload. |
| `dir` | enum | "next" | next, prev | op step. |
| `filter` | string |  |  | op list: name filter. |
| `limit` | integer | 100 | 1 .. 300 | op list: rows. |
| `offset` | integer | 0 | 0 .. 1000 | op list: first row. |
| `wait` | boolean | true |  | Wait until ready after a load. |
| `timeout_s` | number | 45 | 1 .. 50 | Wait limit. |
| `settle_frames` | integer | 20 | 0 .. 120 | Frames after ready. |
| `confirm` | boolean | false |  | Only with play.save_guard on: required for loads that write the save and unlock chapters. |

Output (plus `ok`, `error`): `op` enum (status, list, wait_ready, load_checkpoint, load_chapter, reload, step); `outcome` enum (done, loading, refused, timeout); `status` object {state, loading, playing, paused, fader, input_enabled, title, preawake_pending, cullers_busy, ready, reason, load_count, ...}; `current` object {subscene, savepoint, name, area, chapter, chapter_name, guide}; `subscenes` object[] {subscene, name, puzzle, area, savepoints, state}; `chapters` object[] {chapter, name, subscene, savepoint, unlocked}; `total` integer; `job` object {id, tool, lane, state, elapsed_s, progress, total, message, error}; `backup` string; `warnings` string[].

#### `stream` (WORLD_A; dev; write)

Subscene streaming. Streaming follows the camera: a camera away from the boy unloads his area after 4 s of game time. pin keeps a subscene loaded (AlwaysLoaded), load force-loads one (SavepointTask.WaitForSubscene recipe), freeze stops all streaming changes. subscene -1 = the body's area. status lists loaded, pinned and the body's subscenes; filter narrows by name (filter all = every one).

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "status" | status, pin, unpin, load, freeze, unfreeze | status, pin, unpin, load, freeze or unfreeze. |
| `subscene` | integer | -1 | -1 .. 999 | Savepoint subscene index, -1 = the body's area. |
| `name` | string |  |  | Subscene name instead of the index. |
| `activate` | boolean | true |  | op load: activate after loading. |
| `filter` | string |  |  | op status: name filter. |

Output (plus `ok`, `error`): `outcome` enum (done, refused, timeout); `frozen` boolean; `cullers` object[] {subscene, name, state, pinned, visible}; `pinned` integer[]; `warnings` string[].

#### `menu` (WORLD_A; play; write)

Read and drive the pause menu without a mouse: state lists the selected group's items with ids; open / close; up, down, left, right, submit, back (repeat n times); select jumps to an item id. Chapter loads, Quit and Reset To Defaults are hidden unless allow_destructive; quitting is never done. At the title (the boy waits for a button) submit starts the game. message_answer answers a storage message (answer; confirm:true when play.save_guard is on). Works while the game is frozen.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "state" | state, open, close, up, down, left, right, submit, back, select, message_answer | state, open, close, up, down, left, right, submit, back, select or message_answer. |
| `id` | string |  |  | op select: item id from state (m3). |
| `repeat` | integer | 1 | 1 .. 20 | Navigation presses. |
| `allow_destructive` | boolean | false |  | Show and allow Quit, chapter loads over progress, Reset To Defaults. |
| `answer` | enum |  | ok, cancel | op message_answer: ok (CONTINUE) or cancel (RETRY). |
| `confirm` | boolean | false |  | op message_answer: required with play.save_guard on (CONTINUE can overwrite or drop the save). |

Output (plus `ok`, `error`): `outcome` enum (done, refused, not_open, loading); `open` boolean; `screen` enum (title, loading, in_world, dead, cutscene, pause_menu, menu, credits, message, unknown); `group` string; `selected` string; `items` object[] {id, label, kind, enabled, selected, destructive, value}; `hidden_destructive` integer; `chapter` object {index, name, unlocked}; `warnings` string[].

#### `ui` (WORLD_A; dev; destructive)

Game UI switches: menu_lock (MenuManager.reserveMenu: Esc does nothing), title_hide (TitleManager.HideAll), overlay_text (MessageOverlay.Show), settings (volume 0 only = mute, brightness, rumble, fullscreen, resolution; brightness / rumble write INSIDE.sav), message_answer (storage error prompt), savepoint_menu (Playdead's dev savepoint picker, INFERRED; it reads the physical controller).

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op (req)` | enum |  | menu_lock, title_hide, overlay_text, settings, message_answer, savepoint_menu | menu_lock, title_hide, overlay_text, settings, message_answer or savepoint_menu. |
| `on` | boolean |  |  | op menu_lock / title_hide. |
| `text` | string |  | max 200 chars | op overlay_text. |
| `seconds` | number | 3 | 0.5 .. 30 | op overlay_text. |
| `volume` | number |  | 0 .. 1 | op settings: master volume 0..1. |
| `brightness` | number |  | 0 .. 1 | op settings: 0..1. |
| `rumble` | boolean |  |  | op settings. |
| `fullscreen` | boolean |  |  | op settings. |
| `resolution` | integer |  | 0 .. 64 | op settings: index into the resolution list. |
| `answer` | enum |  | ok, cancel | op message_answer. |
| `confirm` | boolean | false |  | Only with play.save_guard on: required for message_answer and settings that write the save. |

Output (plus `ok`, `error`): `outcome` enum (done, refused); `state` object {menu_locked, title_hidden, settings, message}; `warnings` string[].

#### `save` (WORLD_A; dev; destructive)

The Steam Cloud save INSIDE.sav. info and savepoints_near are read-only; backup copies it to _mod/save_backups; lock / unlock stop and resume the game's save writes (released when the game quits). orbs reads or edits orb bits 0..13 and 14 (power secret), only local bits, confirm:true to edit; wrist_secret on / off; ending credits | secret | restart (secret clears orbs: confirm; restart = new game). Never touches Steam achievements directly. Save backups and save confirms apply only when the user set EditorState play.save_guard = 1.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "info" | info, backup, lock, unlock, orbs, wrist_secret, ending, savepoints_near | info, backup, lock, unlock, orbs, wrist_secret, ending or savepoints_near. |
| `action` | enum | "read" | read, set, clear | op orbs. |
| `bits` | integer[] |  | 0 .. 14; items 0 .. 15 | op orbs: bits 0..14. |
| `layer` | enum | "both" | ever, recent, both | op orbs: ever, recent or both. |
| `on` | boolean |  |  | op wrist_secret. |
| `kind` | enum |  | credits, secret, restart | op ending. |
| `radius` | number | 30 | 1 .. 500 | op savepoints_near (m). |
| `confirm` | boolean | false |  | Required for orb edits and ending secret; with play.save_guard on also for ending restart and unlock after a cheat. |

Output (plus `ok`, `error`): `outcome` enum (done, refused); `info` object {spawn, progression, unlocked, brightness, rumble, hash, saving, locked, backup}; `orbs` object {ever, recent}; `backup` object {path, bytes, source}; `savepoints` object[] {id, subscene, savepoint, name, dist, checkpoint_only, dynamic, enabled}; `warnings` string[].

#### `guide` (GUIDE; play; readOnly)

Playbooks for INSIDE: without topic the index (chapters, mechanics, puzzles, known stuck spots); with topic one playbook at a spoiler level: hint (default), steps or solution. observe.checkpoint.guide names the topic for the current place; section picks one heading (a place such as riverWithDog, or Savepoints).

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `topic` | string |  |  | Topic id from the index or observe.checkpoint.guide (e.g. forest/overview). Empty = index. |
| `level` | enum | "hint" | hint, steps, solution | How much to reveal. |
| `section` | string |  |  | Only this section heading of the topic. |

Output (plus `ok`, `error`): `topic` string; `title` string; `level` enum (hint, steps, solution); `text` string; `related` string[]; `index` object[] {topic, title, description, chapter}; `current_topic` string; `sections` string[]; `max_level` string; `warnings` string[].

#### `run_scenario` (GUIDE; dev; destructive)

Run a scripted test: steps in order through the agent input and the step clock. do = load (checkpoint), press / hold / release (stick, jump, action), wait (steps or seconds), until (one wait_until condition within within_s), expect (observe path cmp value), capture (screenshot), command (console), observe, note. Stops at the first failed step unless stop_on_fail is false. A run longer than the call budget ends with outcome timeout and next = the step to pass as start_at. op check only validates; op list shows scenario files. Loads need no confirm unless the user set EditorState play.save_guard = 1 (then loads that write the save need confirm:true in the step).

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "run" | run, check, list | run, check (validate only) or list (scenario files). |
| `file` | string |  |  | Scenario file name in _mod/scenarios or <guide>/scenarios (without .json), instead of steps. |
| `steps` | object[] |  | items 1 .. 200 | Inline steps (instead of file). |
| `steps[].do (req)` | enum |  | load, press, hold, release, wait, until, expect, capture, command, observe, note | Step kind. |
| `steps[].label` | string |  | max 200 chars | Name shown in results. |
| `steps[].stick` | number[2] |  | -1 .. 1; items 2 .. 2 | press / hold: stick [x, y], each -1..1 (0 or magnitude 0.3..1; above 0.8 runs). |
| `steps[].jump` | boolean |  |  | press / hold: jump button (rising edge, buffered 0.25 s). |
| `steps[].action` | boolean |  |  | press / hold: action button (tap under 0.3 s or hold = grab). |
| `steps[].steps` | integer |  | 1 .. 3600 | press / wait: logic steps (60 = 1 s). |
| `steps[].seconds` | number |  | 0.02 .. 60 | wait: game seconds instead of steps. |
| `steps[].stop_on_death` | boolean | true |  | press / wait / until: a death fails the step (false runs through it). |
| `steps[].until` | any |  |  | until: one wait_until condition object, e.g. {"grounded":true} or {"near":{"target":"o3"}}. |
| `steps[].within_s` | number | 10 | 0.1 .. 50 | until: game-time limit in seconds. |
| `steps[].path` | string |  | max 200 chars | expect: observe path (body.state, body.x, screen, checkpoint.savepoint, objects.0.kind, hazards.length). |
| `steps[].cmp` | enum |  | eq, ne, lt, le, gt, ge, exists, missing, contains | expect: comparison (default eq with value, exists without). |
| `steps[].value` | any |  |  | expect: value to compare with. |
| `steps[].tol` | number | 0.01 | 0 .. 1000 | expect: number tolerance. |
| `steps[].mode` | enum | "respawn" | respawn, quiet, chapter, reload_respawn, reload_chapter, newgame | load: respawn, quiet, chapter (writes the save), reload_respawn, reload_chapter or newgame. |
| `steps[].subscene` | integer |  | 0 .. 999 | load: savepoint subscene index. |
| `steps[].savepoint` | integer | 0 | 0 .. 999 | load: savepoint index. |
| `steps[].confirm` | boolean | false |  | load: allow a load that writes INSIDE.sav (needed only with play.save_guard on). |
| `steps[].shot` | enum | "full" | preview, full, marked | capture: preview, full or marked (the PNG stays on disk; only the path is returned). |
| `steps[].cmd` | string |  | max 300 chars | command: console command line. |
| `steps[].text` | string |  | max 300 chars | note: text of the note. |
| `start_at` | integer | 0 | 0 .. 199 | First step index to run (continue after a timeout). |
| `stop_on_fail` | boolean |  |  | Stop at the first failed step (default true; when not given a file's own stop_on_fail applies). |
| `observe` | enum | "concise" | concise, none | Observation at the end. |

Output (plus `ok`, `error`): `op` enum (run, check, list); `outcome` enum (passed, failed, stopped, cancelled, timeout, valid, listed); `name` string; `total` integer; `ran` integer; `next` integer; `failures` integer; `results` object[] {i, do, label, ok, detail, steps, stop, value}; `scenarios` object[] {file, name, description, steps, error}; `game_steps` integer; `warnings` string[]; `changes` object; `observe` object.

#### `notes` (GUIDE; play; write)

Moments the user flagged in the game with the notes hotkey (or added here): frame, checkpoint, body position, screenshot. list the latest, get one (image attaches its screenshot), add a note at the current moment, clear all (the old list is kept as a backup file).

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "list" | list, get, add, clear | list, get, add or clear. |
| `id` | integer |  | 0 .. 1000000 | op get. |
| `text` | string |  | max 500 chars | op add. |
| `limit` | integer | 10 | 1 .. 50 | op list. |
| `image` | boolean | false |  | op get: attach the screenshot. |

Output (plus `ok`, `error`): `notes` object[] {id, time, frame, step, subscene, savepoint, checkpoint, pos, text, screenshot}; `total` integer; `hotkey` string; `cleared` integer; `backup` string; `image` string; `warnings` string[].

#### `geometry` (NAV; play, dev; readOnly)

Occupancy slice of the level around the body (or a point) at its depth plane: text rows with # solid, . free, ~ water, H ladder, | rope, B body, ^ hazard, ? not loaded; plus ledges. Look ahead before move_to (pits, walls, ledges up), plan routes, debug blocked moves.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "slice" | slice, ledges, probe | slice, ledges or probe (one column under / over a point). |
| `center` | string |  |  | Object id or path (default the body). |
| `x` | number |  | -100000 .. 100000 | World x instead of center. |
| `y` | number |  | -100000 .. 100000 | World y. |
| `width` | number | 16 | 2 .. 64 | Slice width (m). |
| `height` | number | 8 | 2 .. 32 | Slice height (m). |
| `cell` | number | 0.25 | 0.1 .. 1 | Cell size (m). |

Output (plus `ok`, `error`): `origin` object {x, y, z}; `cell` number; `cols` integer; `rows` integer; `grid` string[]; `ledges` object[] {x, y, side, climbable}; `probe` object.

#### `camera` (WORLD_B; dev; write)

The gameplay camera. state and boxes are read-only. box_set switches a CameraBlendBox (framing zone) on / off or moves its points; follow targets another object; poi adds or clears a point of interest; fx turns shake / handshake off or on; pose flies the free camera to a pose; release gives the camera back. While overridden the body's area is pinned so it does not unload. Writes are re-read after 2 frames (verdict) and undone by restore.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "state" | state, boxes, box_set, follow, poi, fx, pose, release | state, boxes, box_set, follow, poi, fx, pose or release. |
| `box` | string |  |  | op box_set: box id or path. |
| `active` | boolean |  |  | op box_set. |
| `position_camera` | number[3] |  | -100000 .. 100000; items 3 .. 3 | op box_set: camera point. |
| `position_aim` | number[3] |  | -100000 .. 100000; items 3 .. 3 | op box_set: aim point. |
| `object` | string |  |  | op follow / poi: object id or path; empty poi clears. |
| `blend_s` | number | 1 | 0 .. 10 | op follow: blend seconds. |
| `radius` | number | 30 | 1 .. 500 | op boxes (m). |
| `shake` | boolean |  |  | op fx. |
| `handshake` | boolean |  |  | op fx. |
| `pose` | object |  |  | op pose. |
| `pose.x (req)` | number |  | -100000 .. 100000 | World x. |
| `pose.y (req)` | number |  | -100000 .. 100000 | World y. |
| `pose.z (req)` | number |  | -100000 .. 100000 | World z. |
| `pose.yaw` | number |  | -720 .. 720 | Degrees. |
| `pose.pitch` | number |  | -90 .. 90 | Degrees. |
| `pose.fov` | number |  | 5 .. 140 | Degrees. |
| `pose.roll` | number |  | -180 .. 180 | Degrees. |
| `pose.seconds` | number | 0 | 0 .. 30 | Ease time. |
| `pin` | boolean | true |  | Pin the body's area while overridden. |
| `limit` | integer | 30 | 1 .. 100 | op boxes: rows. |

Output (plus `ok`, `error`): `outcome` enum (done, refused); `camera` object {pos, aim, up, fov, paused, target, overridden, free, active_box, weights}; `boxes` object[] {id, path, active, priority, time_in, time_out, position_camera, position_aim, dist}; `pinned_subscene` integer; `verdict` enum (confirmed, reverted_by_game, not_applied, unreadable); `warnings` string[].

#### `logic` (WORLD_B; dev; destructive)

Playdead world logic. Read-only: triggers_near, signal_ports, fsm_list, interactables_near (levers, buttons, gates, doors, lifts, hatches, world flags with their signal inputs). Changes: trigger_fire (enter / exit as probe, signal or teleport), signal_in, signal_out, signal_wire / signal_unwire, spawn_clone, body_set (rigidbody), anim_set (legacy Animation). Changes are re-read and get a verdict; signal_out / signal_in / fsm events cannot be undone.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op (req)` | enum |  | triggers_near, trigger_fire, signal_ports, signal_in, signal_out, signal_wire, signal_unwire, fsm_list, interactables_near, spawn_clone, body_set, anim_set | What to do. |
| `object` | string |  |  | Object id, path, #instanceID or unique name. |
| `radius` | number | 15 | 0.5 .. 200 | Near ops (m). |
| `kinds` | string[] |  | items 0 .. 8 | triggers_near filter: probe, savepoint, killer, camera, fsm, other; interactables_near: lever, button, valve, gate, door, elevator, hatch, jetcrate, flag. |
| `edge` | enum | "enter" | enter, exit | trigger_fire. |
| `mode` | enum | "probe" | probe, signal, teleport | trigger_fire. |
| `input` | string |  |  | signal_in / signal_wire target input. |
| `output` | string |  |  | signal_out / signal_wire source output. |
| `to` | string |  |  | signal_wire: receiving object. |
| `filter` | string |  |  | fsm_list / near ops: name filter. |
| `state` | string |  |  | fsm_list: only FSMs in this state. |
| `position` | number[3] |  | -100000 .. 100000; items 3 .. 3 | spawn_clone: world position. |
| `rotation` | number[3] |  | -720 .. 720; items 3 .. 3 | spawn_clone: euler degrees. |
| `preawake` | boolean | true |  | spawn_clone: run PreAwake on the clone. |
| `velocity` | number[3] |  | -1000 .. 1000; items 3 .. 3 | body_set. |
| `angular` | number[3] |  | -1000 .. 1000; items 3 .. 3 | body_set. |
| `force` | number[3] |  | -100000 .. 100000; items 3 .. 3 | body_set: impulse. |
| `kinematic` | boolean |  |  | body_set. |
| `wake` | boolean |  |  | body_set. |
| `clip` | string |  |  | anim_set: clip name. |
| `time` | number |  | 0 .. 10000 | anim_set: seconds. |
| `speed` | number |  | -10 .. 10 | anim_set. |
| `play` | boolean |  |  | anim_set: play / stop. |
| `limit` | integer | 40 | 1 .. 200 | Rows. |
| `offset` | integer | 0 | 0 .. 10000 | First row. |
| `verify_frames` | integer | 2 | 0 .. 30 | Re-read after this many frames. |

Output (plus `ok`, `error`): `outcome` enum (done, refused, not_found); `items` object[]; `total` integer; `verdict` enum (confirmed, reverted_by_game, not_applied, unreadable); `before` any; `after` any; `after_k` any; `inactive_target` boolean; `note` string; `changes` object; `warnings` string[].

#### `body` (WORLD_B; dev; destructive)

Controllable bodies: list (boy, huddle, hat dudes, submarine), teleport (safe: refuses while dead, in a hat, in the sub or scripted; keeps the depth plane; snaps the camera; waits for streaming), hat (attach / detach / status of mind-control hats, MindHat.ForceAttach / ForceDetach), swap (boy / huddle, MainCharacterSwap).

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "list" | list, teleport, hat, swap | list, teleport, hat or swap. |
| `body` | string |  |  | Body id (b1); default the controlled one. |
| `x` | number |  | -100000 .. 100000 | teleport: world x. |
| `y` | number |  | -100000 .. 100000 | teleport: world y. |
| `z` | number |  | -100000 .. 100000 | teleport: world z (default keep the depth plane). |
| `dx` | number |  | -1000 .. 1000 | teleport: relative x. |
| `dy` | number |  | -1000 .. 1000 | teleport: relative y. |
| `to_object` | string |  |  | teleport: to an object id or path. |
| `snap_camera` | boolean | true |  | teleport. |
| `wait_stream` | boolean | true |  | teleport: wait until streaming settles. |
| `hat_op` | enum | "status" | attach, detach, status | op hat. |
| `hat` | string |  |  | op hat: hat id or path. |
| `to` | enum |  | boy, huddle | op swap. |
| `force` | boolean | false |  | Skip the safety refusals (may break scripted scenes). |

Output (plus `ok`, `error`): `outcome` enum (done, refused, not_found, timeout); `bodies` object[] {id, kind, name, pos, controlled, active, state}; `hats` object[] {id, name, attached, wearer, turned_off}; `verdict` enum (confirmed, reverted_by_game, not_applied, unreadable); `before` number[]; `after` number[]; `warnings` string[]; `observe` object.

#### `hazard` (WORLD_B; dev; write)

Dangers near the body. near is read-only (kind, state, eta, whether it can be switched). set turns one hazard (target) or all near ones off / on (dogs, guards, sentries, spotlights, mermaid, kill zones, shockwave, kill FSMs). air sets infinite air / refills / changes the dive limit. god toggles invincibility (death animations can still play). Restored by restore. Each switch reports the call used and a verdict.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "near" | near, set, air, god | near, set, air or god. |
| `target` | string |  |  | op set: hazard id or path. |
| `all_near` | boolean | false |  | op set: every hazard within radius. |
| `radius` | number | 20 | 1 .. 200 | op near / set (m). |
| `on` | boolean |  |  | op set: hazard active; op god: invincible. |
| `infinite` | boolean |  |  | op air. |
| `refill_s` | number |  | 0 .. 60 | op air: add seconds of air now. |
| `max_dive_s` | number |  | 1 .. 120 | op air: dive limit of the current water. |

Output (plus `ok`, `error`): `outcome` enum (done, refused, not_found); `hazards` object[] {id, kind, name, dx, dy, dist, state, eta_s, threat, safe, music_timed, switchable, ...}; `changed` object[] {id, kind, how, verdict}; `air` object {infinite, left_s, max_s}; `god` boolean; `warnings` string[].

#### `audio` (WORLD_B; dev; write)

Wwise audio: music (read-only: position, loop, beat, bar), post an event (globally or on an object), stop a playing id, set an RTPC, state or switch. Master volume RTPCs accept only 0 (mute). restore stops posted events and resets changed RTPCs (never the master volume).

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "music" | music, post, stop, rtpc, state, switch | music, post, stop, rtpc, state or switch. |
| `event` | string |  |  | op post: event name. |
| `object` | string |  |  | op post / rtpc / switch: object id or path (default global). |
| `name` | string |  |  | op rtpc: RTPC name. |
| `value` | number |  | -1000000 .. 1000000 | op rtpc. |
| `interp_s` | number | 0 | 0 .. 10 | op rtpc: interpolation. |
| `group` | string |  |  | op state / switch: group name. |
| `state` | string |  |  | op state / switch: value name. |
| `playing_id` | integer |  | 0 .. inf | op stop. |

Output (plus `ok`, `error`): `outcome` enum (done, refused); `playing_id` integer; `music` object {pos_s, loop_s, beat, bar, cue}; `note` string; `warnings` string[].

#### `render` (WORLD_B; dev; idempotent)

Render switches: state (read-only), post (PDPostCombined on / off), fog, default_lights (neutral light rig), debug_view (PDRenderDebug modes), debug_flags (EditorMode flags, list or set one), shot_route (Playdead's 127-savepoint camera route: list, or apply entry index to the free camera). Restored by restore.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "state" | state, post, fog, default_lights, debug_view, debug_flags, shot_route | state, post, fog, default_lights, debug_view, debug_flags or shot_route. |
| `on` | boolean |  |  | op post / fog / default_lights / debug_flags. |
| `mode` | enum |  | off, Depth, Normals, NormalsContrast, Lighting, Shadows, Transparency, Unlit, UnlitTransparency | op debug_view. |
| `flag` | string |  |  | op debug_flags: EDebugFlags name (omit to list). |
| `index` | integer |  | 0 .. 1000 | op shot_route: entry to apply. |
| `apply` | boolean | false |  | op shot_route: move the free camera to the entry. |
| `limit` | integer | 40 | 1 .. 200 | Rows. |
| `offset` | integer | 0 | 0 .. 1000 | First row. |

Output (plus `ok`, `error`): `outcome` enum (done, refused); `state` object {post, fog, default_lights, debug_view}; `flags` object[] {name, on}; `route` object[] {index, subscene, savepoint, pos, cam_pos, cam_rot}; `total` integer; `warnings` string[].

#### `reflect` (WORLD_B; dev; destructive)

Read and change anything by reflection: get / set a field or property (verified: confirmed, reverted_by_game, not_applied, unreadable), call a method with JSON arguments, find_methods, describe_type. object + component for instances, type for statics. member may be a dotted path (properties.swim.infiniteAir). Arguments: numbers, strings, bools, null, [x,y,z] for Vector3, enum names, object ids or paths for object parameters. Steam and OS calls and raising the master volume are refused (save writes too when the user set play.save_guard = 1).

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op (req)` | enum |  | get, set, call, find_methods, describe_type | get, set, call, find_methods or describe_type. |
| `object` | string |  |  | Object id, path, #instanceID or unique name. |
| `component` | string |  |  | Component type name (exact, then suffix). |
| `type` | string |  |  | Type name for statics, find_methods, describe_type. |
| `member` | string |  |  | Field, property or method name. |
| `value` | any |  |  | op set: new value (JSON). |
| `args` | any[] |  | items 0 .. 8 | op call: arguments (JSON). |
| `text` | string |  |  | op find_methods: name filter. |
| `verify_frames` | integer | 2 | 0 .. 30 | op set: re-read after frames. |
| `restore_on_end` | boolean | false |  | op set: undo it on restore. |
| `limit` | integer | 40 | 1 .. 200 | Rows. |

Output (plus `ok`, `error`): `outcome` enum (done, refused, not_found); `value` any; `type` string; `verdict` enum (confirmed, reverted_by_game, not_applied, unreadable); `before` any; `after` any; `after_k` any; `methods` object[] {name, signature, static, declaring}; `members` object[] {name, kind, type, static}; `warnings` string[].

#### `state_diff` (WORLD_B; dev; idempotent)

Snapshot every component field around the body (or a point / root) and diff later: snapshot name, act or wait, then diff name (against a new snapshot or another named one). Shows exactly which fields a lever, trigger or action changed. Covers active flags, transforms, enabled flags, rigidbodies, FSM states and variables, and the public and private fields of game scripts.

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "snapshot" | snapshot, diff, list, drop | snapshot, diff, list or drop. |
| `name` | string | "a" |  | Snapshot name. |
| `against` | string |  |  | op diff: compare with this snapshot instead of a new one. |
| `center` | string |  |  | Object id or path (default the body). |
| `radius` | number | 8 | 1 .. 50 | Metres. |
| `root` | string |  |  | Hierarchy path: everything under it instead of a radius. |
| `components` | string[] |  | items 0 .. 20 | Only these component types. |
| `max_fields` | integer | 5000 | 100 .. 50000 | Field cap per snapshot. |
| `limit` | integer | 100 | 1 .. 500 | op diff: rows. |

Output (plus `ok`, `error`): `snapshot` object {name, objects, components, fields, frame, step}; `snapshots` object[] {name, fields, frame}; `diff` object[] {object, component, field, before, after, change}; `total` integer; `truncated` boolean; `warnings` string[].

#### `input_track` (INPUT; play, dev; write)

Record and replay input. start records every logic step of input that reaches the game (with body position and velocity) while you act / move_to in step mode; stop, info, trim {from, to}, save {name} (_mod/input_tracks), load, list. play feeds the track back one frame per logic step in realtime at the rehearsal's 60 Hz lockstep (input only; drift measured; correct:true opts in to nudges), as a job: wait_until {job}; cancel with op stop or restore. A manual srec recording may run during play (it owns frame pacing).

| Input | Type | Default | Values / range | Description |
|---|---|---|---|---|
| `op` | enum | "status" | status, start, stop, save, load, list, info, trim, play | status, start, stop, save, load, list, info, trim or play. |
| `name` | string |  | max 64 chars | Track name (letters, digits, _ - .); info and play default to the track in memory. |
| `from` | integer | 0 | 0 .. 1000000 | trim: first step kept. |
| `to` | integer | -1 | -1 .. 1000000 | trim: last step kept, -1 = end. |
| `overwrite` | boolean | false |  | save: replace a track of that name. |
| `speed` | number | 1 | 0.05 .. 4 | play: realtime speed (1 for a take). |
| `correct` | boolean | false |  | play: nudge the body toward the recorded path (off: input only, for final takes; drift is measured either way). |
| `gain` | number | 0.05 | 0 .. 1 | play: share of the drift removed per step. |
| `max_step` | number | 0.02 | 0 .. 0.5 | play: largest correction per step (m). |
| `stop_drift` | number | 0 | 0 .. 100 | play: stop beyond this drift (m), 0 = never. |
| `max_start` | number | 1.5 | 0 .. 1000 | play: refuse when the body starts farther from frame 0 (m), 0 = no check. |
| `then` | enum | "run" | run, freeze | play: afterwards keep running or freeze. |
| `wait` | boolean | true |  | play: wait up to timeout_s for the end. |
| `timeout_s` | number | 45 | 0 .. 50 | play / stop: wait limit. |
| `observe` | enum | "concise" | concise, none | play: observation in the result. |

Output (plus `ok`, `error`): `op` enum (status, start, stop, save, load, list, info, trim, play); `outcome` enum (done, recording, stopped, saved, loaded, trimmed, playing, cancelled, died, loading, stalled, desynced); `stop_reason` string; `recording` boolean; `playing` boolean; `track` object {name, body, subscene, steps, forced_steps, forced_spans, samples, saved}; `tracks` object[] {name, steps, body, subscene}; `playback` object {step, of, corrected, max_drift_m, max_drift_step, end_drift_m, drift_per_s}; `job` object {id, tool, lane, state, elapsed_s, progress, total, message, error}; `file` string; `next` string; `warnings` string[]; `observe` object.

---

## 4. Contracts in code

Signatures in the files below are frozen. "Frozen" means: public type names, member names, parameter lists, return
types, tool names, parameter names / types / enums / defaults / limits and output field names do not change without the
architect. Owners implement bodies, add private or internal members, and may improve description texts. A member
documented "any thread" must be safe to call from a worker thread.

### 4.1 Server types (`src/mcp/ServerApi.cs`, Unity-free)

| Type | Purpose |
|---|---|
| `McpProfiles` (flags `Play`, `Dev`, `Both`), `McpProfile` (`play`, `dev`, `all`, `Includes`, `FromPath`) | profile tags and path mapping |
| `McpErr` | error codes: `bad_args`, `busy`, `not_ready`, `stale_id`, `not_found`, `ambiguous`, `refused`, `unsafe`, `confirm_required`, `timeout`, `cancelled`, `game_error`, `not_built`, `internal`, `unauthorized` |
| `McpException(code, message, hint)` + `data` | throw from any provider code: the host returns it as `ToolResult.Fail(e)`; `data` keys are copied into structured |
| `ToolDef` | `name, title, description, inputSchema, outputSchema, meta, readOnly, destructive, idempotent, openWorld, profiles, owner, timeoutMs (55000)`; fluent `ReadOnly()` (also idempotent, not destructive), `Destructive()`, `Idempotent()`, `Owner()`, `Timeout()`, `Meta()`, `AlwaysLoad()`, `RequiresUser()`, `MaxChars()`; `ToJson()` (SERVER) |
| `ToolResult` | `content`, `structured`, `isError`; `Text` (legacy), `Ok(structured)` (adds `ok:true` first), `Fail(code, message, hint)`, `Fail(McpException)`, `Busy(job)`, `AddText`, `AddImage`, `AddResourceLink`; `ToJson()` (SERVER) |
| `ResourceDef`, `ResourceTemplateDef`, `ResourceContents`, `PromptArg`, `PromptDef`, `PromptMessage`, `PromptResult` | resources and prompts; each `ToJson()` (SERVER) |
| `CallInfo` | per request: `profile, tool, sessionId, client, requestId, progressToken, startedUtc, budgetMs, progress` (sink set by the server), `Cancel(reason)`, `Cancelled`, `CancelReason`, `ElapsedMs`, `RemainingMs` |
| `McpLanes.Control`, `McpJobState` (`running`, `done`, `failed`, `cancelled`) | lanes and job states |
| abstract `McpJob` | `Id, Tool, Lane, State, ElapsedMs, Cancelled, CancelReason, Cancel, Progress, Detach, Finish, Fail, Await, Result, Describe` |
| abstract `CallContext` | `Info, Tool, Profile, Cancelled, RemainingMs, Progress, Main<T>(fn, timeoutMs = 10000), MainDo(fn, timeoutMs), WaitFrames, WaitUntil(check, timeoutMs, everyFrames = 1), Sleep, Frame, BeginJob(lane, out busy), FindJob, RunningJob, Log, Audit, ThrowIfCancelled` |
| `IMcpToolProvider` | `ProviderName, Tools(), Call(tool, args, ctx), Resources(), ResourceTemplates(), ReadResource(uri, ctx)` (null = not mine), `Prompts(), GetPrompt(name, args, ctx)` (null = not mine), `Instructions(profile)` (paragraph or null), `Restore(report)` (main thread) |
| `IToolHost` (server -> host) | `ServerVersion, Instructions(profile), Tools(profile), Call(def, args, info), Resources, ResourceTemplates, ReadResource, Prompts, GetPrompt, ListVersion, AuthToken, Allowed(profile, tool), Log` |

### 4.2 Schema builder (`src/mcp/ServerSchema.cs`, implemented)

```
Schema.Obj()                          -> SchemaObj (fluent)
  .Str(name, desc[, def]) .StrMax(name, desc, maxLength)
  .Int(name, desc, min, max) .Int(name, desc, def, min, max)
  .Num(name, desc, min, max) .Num(name, desc, def, min, max)
  .Bool(name, desc[, def]) .Enum(name, desc, def or null, values...)
  .Arr(name, desc, itemSchema, minItems, maxItems) .Vec(name, desc, n, min, max)
  .Obj(name, desc, SchemaObj) (strict nested input)  .OutObj(name, desc, SchemaObj) (nested output)
  .Prop(name, desc or null, schema) .Any(name, desc) .Required(names...) .Default(name, value) .Open()
  .Build()        input:  {type:object, properties, required?, additionalProperties:false}
  .BuildOutput()  output: adds ok + error, no required, extra fields allowed, slim
  .BuildNested()  nested output object, slim
Schema.Str() Int(min,max) IntAny() Num(min,max) NumAny() Bool() Enum(values) Arr(items,min,max) Vec(n,min,max) VecAny(n)
       ObjOf(SchemaObj) OutOf(SchemaObj) ObjAny() Described(schema, desc) ErrorSchema() Slim(schema) Object(...) (legacy)
Schema.Validate(schema, args, out error)   SERVER, see 4.6
```

### 4.3 Host API (`src/mcp/HostRegistry.cs`, `HostContext.cs`, `Jobs.cs`, `Shot.cs`, `DevTools.cs`)

| API | Contract |
|---|---|
| `McpRegistry.Add / Remove / Changed / Version / ListChanged / Providers / ToolsFor(profile) / OwnerOf(tool) / ResourcesFor / TemplatesFor / PromptsFor / InstructionsFor(profile, baseText) / RestoreAll()` | thread-safe provider list; first provider wins a name clash (logged); every change bumps `Version`; instructions = base text + provider paragraphs, capped at 4000 chars, important text first |
| `HostCallContext(CallInfo)` | the `CallContext` implementation |
| `MainQueue.Pump / Frame / Alive / Pending` | per-frame pump from `McpExtension.Tick`; `Alive` = pumped within 2 s |
| `AuditLog.Write(call, what, detail)` | `_mod/mcp_audit.jsonl` |
| `HostJob`, `HostJobs.Begin(lane, call, out busy) / Find / Running / All / CancelAll / EndOfCall(call, result)` | job runner; ids `j1, j2, ...`; finished jobs kept 10 min (last 50); a MultiCam capture (`MultiCam.Capturing`, in the core) holds the control lane |
| `ShotMark {id, label, x, y}`, `ShotRequest {mode, maxWidth, clean, marks}`, `ShotResult {png, path, mode, error, width, height, srcWidth, srcHeight, black, frame, marks}`, `McpShot.MarkSource`, `McpShot.Capture(ctx, req)`, `Describe(r)`, `Attach(result, r)` | screenshots; Play sets `MarkSource = Observer.ScreenMarks` |
| `DevToolsProvider(call)`, `Defs()`, `JobSchema()`, `Name` | the host's provider; `Defs()` are final |

### 4.4 Play registry and helpers (`src/play/PlayExtension.cs`, `PlayShared.cs`, ARCH, implemented)

- `PlayExtension.RegisterAll()` resets `PlayRegistry`, adds the play and dev instruction paragraphs (`PlayIntro`,
  `DevIntro`), then calls in this order: AgentCore, Stepper, ChangeLog, Perception, Observer, Act, Navigator, Interact,
  Waits, Restorer, PlayLevels, Menus, Saves, GuideBook, Notes, NavGeometry, WorldCamera, WorldLogic, Hazards,
  AudioRender, Reflect, StateDiff, ActTrack (added 2026-10-25, section 17). A `Register()` that throws becomes a line in `PlayRegistry.Problems` and the
  others still register.
- `PlayRegistry.Add(def, handler)`, `Find`, `Resource`, `Template`, `Reader(fn)` (return null when the uri is not
  yours), `Prompt(def, fn)`, `Instructions(profiles, order, text)`, `OnTick(name, fn)` (main thread, every frame, a tick
  that throws is logged once and keeps running).
- `PlayExtension.Call` maps `McpException` to `Fail(e)`, `NotImplementedException` to `not_built`, anything else to
  `internal`. `Restore(report)` = `Restorer.RunAll(null, false)`. `Dispose` (game quit) restores, then removes itself.
  Bridge command: `play status | tools | restore`.
- `PlayShared`: `PlayHandler` delegate, `Todo(owner)`, `NotBuilt(tool, owner)`, `Env()`, `Def(...)`, argument readers
  (`Has, Str, Num, Int, Long, Bool, Obj, List, Strings, Vec2, Vec3`), rounding (`R`, 1 cm), `V`, `V2`, `L`, `F`,
  `PathOf`. Readers assume the server validated arguments and filled defaults.

### 4.5 Module APIs (stubs; the comment above each member says the thread)

| Owner | Class (file) | Public API |
|---|---|---|
| INPUT | `AgentCore : GameInput.Core` (AgentCore.cs), `InputMode {Human, Agent, Mix}`, `InputFrame {x, y, jump, action, steps}` | `Deadzone` 0.3, `Instance, Mode, Installed, Hijacked, Install(mode)` (main, returns warning), `Uninstall()` (main), `Enqueue(frames, latchLast)`, `Release()`, `QueuedSteps`, `Latched(out held)`, `LastFed`, `LastFedStep` (any), `Controller()` (main), `Describe()` (any), `Tick()` (main), overrides `UpdateCommand`, `CommandReceived` |
| INPUT | `Act` (Act.cs) | `StopOn`, `Outcomes`, `ParseInputs(list, out total, out error)` |
| INPUT | `ActTrack` (ActTrack.cs, Unity-free half ActTrackCore.cs) | `Owner`, `RestoreKey` "input_track", `Folder` "input_tracks", `Ops`, `Outcomes`, `Register()`; internal hooks `Record`, `Next`, `Recording`, `Playing`, `Brief` called by AgentCore (same role) |
| INPUT | `Interact` (Interact.cs) | `Verbs`, `Outcomes` |
| TIME | `Stepper` (Stepper.cs), `StepMode {Step, Realtime}`, `RunRequest {maxSteps, timeoutMs, stopCheck, stopOnDeath, stopOnLoad, leaveRunning, label}`, `RunStop` (`steps, condition, died, loading, cancelled, timeout, stalled`), `RunResult {steps, frames, stop, detail, fromStep, toStep, elapsedMs, warnings, ToJson()}` | `Mode, Speed, Frozen, Running, Step, Frame` (any), `SetMode, SetSpeed, Freeze, Unfreeze, State, Tick` (main), `Run(ctx, req)` (worker) |
| TIME | `Waits` (Waits.cs) | `Keys`, `ConditionSchema()`, `Compile(condition, ctx, out error)` -> main-thread check |
| TIME | `Restorer` (Restore.cs) | `Register(key, what, undo)`, `Forget`, `IsSet`, `Keys` (any), `RunAll(only, includeSaveLock)` (main) -> `{reset[], kept[], errors[]}` |
| OBSERVE | `Observer` (Observer.cs), `ObserveOptions`, `ObjInfo`, `HazardInfo`, `BodyInfo` | `Observe(o), Concise(afterTool), Screen(), Ready(out reason), IdFor(obj, prefix), Resolve(id), ResolveTarget(spec), TryResolveTarget, ScreenMarks(max)` (main), `Epoch` (any); `Screens, ObjectKinds, HazardKinds, EventTypes`; schemas `BodySchema, ObjectSchema, HazardSchema, CheckpointSchema, EventSchema, ConciseSchema, FullSchema, ConciseRef` (implemented) |
| OBSERVE | `Perception` (Perception.cs, PerceptionInput.cs) | `ActiveCharacter, BodyPos, Body(full), Bodies, Objects(center, radius, limit), Hazards(center, radius, limit), Affordances(go, out kind), MusicTimedNear(out what), ForcedInput(character)` (main; ForcedInput added 2026-10-25, the stub returns null) |
| OBSERVE | `ChangeLog` (ChangeLog.cs) | `Mark, Since(mark, cap), Events(mark, cap), Note(type, detail)` (any); `Ref, Schema` (implemented) |
| NAV | `Navigator` (Navigator.cs), `MoveRequest` | `Outcomes`, `MoveTo(ctx, req)` (worker, caller holds the lane) |
| NAV | `NavGeometry`, `GridSlice` (Geometry.cs) | `GridSlice(originX, originY, z, cell, cols, rows), At, Set, CellOf, ToRows` (pure); `Slice(center, w, h, cell)`, `Ledges(center, r)` (main) |
| WORLD_A | `PlayLevels`, `LoadRequest` (PlayLevels.cs) | `Ready(out reason), Status, Current, BodySubscene, Pin(sub, on, owner), IsPinned` (main), `Loading, LoadCount, LoadStarted, LoadFinished` (any), `StartLoad(ctx, job, req)` (worker), `StatusSchema()` |
| WORLD_A | `Saves` (Saves.cs) | `EnsureBackup(ctx)` (any, throws when no backup), `BackupPath`, `Locked`, `SetLock(on)` (main) |
| WORLD_A | `Menus` (Menus.cs) | `IsOpen(), Summary()` (main) |
| WORLD_B | `WorldCamera` (WorldCamera.cs) | `Overridden, PinnedSubscene` (any), `Release()` (main) |
| WORLD_B | `WorldLogic`, `Hazards`, `AudioRender` (`DebugViews`), `Reflect` (`Verdicts`), `StateDiff`, `Snapshot`, `DiffRow` | tools only; `Snapshot.Diff(a, b, cap, out total)` (pure) |
| GUIDE | `GuideBook` (Guide.cs) | `ResourceIndex` `inside://guide/index`, `ResourceTemplate` `inside://guide/{chapter}/{topic}`, `Root, TopicFor(sub, sp), Exists(topic)` |
| GUIDE | `Notes` (Notes.cs) | `Add(text)` (main), `Count`, `Hotkey` |

Cross-module calls (only through the APIs above): Act / Interact / Navigator / Waits -> AgentCore + Stepper + ChangeLog +
Observer.Concise; Stepper -> PlayLevels.Ready / Loading + Perception.MusicTimedNear + Restorer; Observer -> Perception,
PlayLevels.Current / Ready, Menus.Summary, WorldCamera.Overridden, AgentCore.Describe, Stepper state, GuideBook.TopicFor,
ChangeLog.Events; PlayLevels -> Saves.EnsureBackup, Stepper.Freeze / Unfreeze; WorldCamera -> PlayLevels.Pin; every
module that changes state -> Restorer.Register; resolution of ids and paths -> Observer.ResolveTarget; the restore
tool -> `HostJobs.Running(McpLanes.Control).Cancel("restore")` on the worker, waits up to 2 s for the job to end, then
`Restorer.RunAll` through `ctx.MainDo`.

### 4.6 Server and proxy behaviour (SERVER)

- Protocol: versions `2025-11-25, 2025-06-18, 2025-03-26, 2024-11-05`; echo a known client version, else the newest.
  `MCP-Protocol-Version` header, when present and unknown: HTTP 400. Batches still accepted (no SSE inside a batch).
- Endpoints by `McpProfile.FromPath`. `GET /health` and `GET /` stay plain text. Origin check stays. Plain endpoints
  without a session (`POST /cmd`, `GET /state`, `GET /ping`): section 19.
- Auth: when `host.AuthToken` is not empty, every MCP request needs `Authorization: Bearer <token>`, else HTTP 401.
- initialize: capabilities `tools {listChanged:true}`, `resources {listChanged:true}`, `prompts {listChanged:true}`;
  `instructions = host.Instructions(profile)`; serverInfo version = `host.ServerVersion`.
- `tools/list`: `host.Tools(profile)` filtered by `host.Allowed`, each `ToolDef.ToJson()`: `name, title, description,
  inputSchema, outputSchema` (when set), `annotations {title, readOnlyHint, destructiveHint, idempotentHint,
  openWorldHint}` always explicit, `_meta` when set.
- `tools/call`: unknown or not allowed name -> JSON-RPC -32602. `Schema.Validate` failure -> `ToolResult.Fail(bad_args,
  "<path>: <problem>")`, never a protocol error. `CallInfo.budgetMs = ToolDef.timeoutMs`.
- `ToolResult.ToJson()`: `content` = first a text block with the compact JSON of `structured` (cut at 100 000 chars with
  `..."truncated"`), then image / resource_link blocks; `structuredContent = structured`; `isError`. A legacy result
  without structured gets `structured = {ok: !isError, text}`.
- `Schema.Validate`: types (an integer accepts a double with no fraction), enum, minimum / maximum, minItems / maxItems,
  maxLength, required, additionalProperties:false at every level, nested objects and arrays; fills defaults (also inside
  present nested objects and array items); error text `inputs[2].stick: expected 2 numbers`.
- Progress: a request with `params._meta.progressToken` and an `Accept` with `text/event-stream` is answered as SSE:
  `notifications/progress {progressToken, progress, total, message}` events (the host rate-limits them to 4 per second),
  then the response event, then close. `CallInfo.progress` is the sink. Without a token: plain JSON as today.
- Cancel: `notifications/cancelled {requestId, reason}` -> `CallInfo.Cancel(reason)` of (session, requestId). The
  response is still sent (clients ignore it).
- `resources/list`, `resources/templates/list`, `resources/read {uri}` (unknown uri: -32002), `prompts/list`,
  `prompts/get {name, arguments}` through the host.
- list_changed: `GET` on an MCP path with `Accept: text/event-stream` opens a stream for the session; when
  `host.ListVersion` changes the server sends `notifications/tools/list_changed` (and the resources / prompts ones);
  keep-alive comment every 15 s. `DELETE` ends the session and its stream.
- Stdio proxy (`McpStdio.cs`): `--profile play|dev|all` (default dev), `--port`, `--url`, `--token` or env
  `INSIDEDEV_MCP_TOKEN`; serverInfo version from the game when up. When the game is up at initialize, return the game's
  capabilities and instructions; when down, own short instructions and `inside_status`. Forward
  `notifications/cancelled` (do not swallow it). For requests with a progress token send `Accept: text/event-stream`,
  read the SSE reply line by line (`SseReader`) and relay each message to stdout. Hold a GET stream for list_changed
  while the game is up, plus the existing up / down detection. HTTP timeout 90 s (tool budget 55 s).

### 4.7 Host behaviour (HOST)

- `McpExtension.Start`: registers `DevToolsProvider`; `Tick`: drain the log queue, `MainQueue.Pump()`. Keep `mcp
  status | on | off | port | selftest | test`.
- `Call(def, args, info)`: new `HostCallContext(info)`; provider = `McpRegistry.OwnerOf(def.name)`; check the def is
  served on `info.profile`; audit every non-read-only call (time, profile, tool, args cut at 2000 chars, outcome);
  `McpException` -> `Fail(e)`; other exceptions -> `Fail(internal)`; finally `HostJobs.EndOfCall(info, result)`.
- `HostCallContext.Main`: enqueue and wait; not picked up within the timeout -> remove the item and throw `not_ready`
  ("the main thread did not run the request within N s: window unfocused without runInBackground, or a long load");
  cancelled before it ran -> remove and throw `cancelled`; an exception inside -> `game_error` (McpException passes
  through). Never run an item whose caller gave up.
- `WaitFrames`, `WaitUntil`, `Sleep`: worker-side waits that wake on `MainQueue.Pump`, honour cancel and `RemainingMs`.
- `Progress`: forwards to `CallInfo.progress` at most 4 per second and always the last one.
- `BeginJob(lane)`: `HostJobs.Begin`; busy when the lane is held or `MultiCam.Capturing`.
- Instructions: base text per profile (dev: the v0.5 text; play: one line "InsideDev play tools for INSIDE.") + provider
  paragraphs via `McpRegistry.InstructionsFor`.
- `Allowed`: EditorState `mcp.tools` (comma list of names or `profile:*`; empty = all).
- `SelfTest` (`mcp selftest`, Extension.cs) keeps working and also checks `/mcp/play` and one structured tool (`observe`).
- Restore triggers: `mcp off`, server stop, game quit (`Dispose`), DELETE of the last session, and 10 minutes without
  any tool call (EditorState `mcp.idle_restore_s`, default 600, 0 = never) -> `McpRegistry.RestoreAll()` on the main
  thread. `DevToolsProvider.Restore` closes panels opened through MCP.
- Dev tool fixes (the definitions in DevTools.cs are final): `inspect` and `command` cap output at `max_chars` and page
  with `offset`, report `truncated` and `total_chars`; `fsm_graph` does not touch the selection or panels unless
  `open_panel`; `find_objects` and `fsm_find` honour `limit`; `set_active` re-reads after 2 frames; `command` sets
  isError from the command's own failure text (`not found`, `usage`, `unknown`, `busy`, `no FSM`, `component not
  found`, `field/property not found`, `no parameterless method`, `no active character`, `ERROR in`, `failed`, `not
  built`) and refuses `keyhold`, `focustest`, `remote off`, `mcp off`, `mcp port` (OS input or killing the server from
  inside a call); `screenshot` per section 5.7; `jobs` per its schema.

---

## 5. JSON shapes

### 5.1 Envelope

Every new tool returns `structuredContent` with `ok` first. On failure:

```json
{"ok": false, "error": {"code": "stale_id", "message": "o14 is from before the last load", "hint": "call observe again"}}
```

`isError` is true exactly when `ok` is false. Waits that time out are not failures (`ok: true, satisfied: false`).

### 5.2 observe (detail concise; full adds input, bodies, menu, camera, image, truncated)

```json
{"ok": true, "frame": 81234, "step": 51234, "screen": "in_world", "ready": true, "reason": "",
 "time": {"mode": "step", "frozen": true, "scale": 0, "music_s": 41.27},
 "body": {"kind": "boy", "id": "b1", "x": 412.31, "y": 12.08, "z": 0, "vx": 0, "vy": 0, "facing": 1, "state": "Run",
          "grounded": true, "in_water": false, "special": "none", "dead": false, "controllable": true},
 "checkpoint": {"subscene": 2, "savepoint": 1, "name": "riverWithDog", "chapter": 3, "guide": "forest/river_dog"},
 "objects": [{"id": "o14", "kind": "box", "name": "Crate (2)", "dx": 3.1, "dy": 0, "dist": 3.1, "actions": ["grab", "push", "pull"]},
             {"id": "o15", "kind": "ledge", "dx": 5.02, "dy": 2.2, "dist": 5.48, "actions": ["climb"]}],
 "hazards": [{"id": "h3", "kind": "dog", "dx": -14.2, "dy": 0, "dist": 14.2, "state": "chase", "eta_s": 2.1, "threat": true}],
 "events": [{"step": 51190, "type": "checkpoint", "detail": "2:1"}],
 "stuck": {"calls_without_progress": 0}, "warnings": []}
```

- Ids: `b` bodies, `o` objects, `h` hazards; one counter that never restarts; valid until the next load start
  (`Observer.Epoch`). An old or destroyed id -> `stale_id`. Paths and names resolve strictly: no nearest-match fallback,
  several matches -> `ambiguous` with `candidates [{id, path}]`.
- `dx, dy, dz, dist`: metres from the controlled body, rounded to 1 cm. `dz` only when not 0.
- `actions`: `interact` verbs that can work now or after `move_to`.
- `state`: state class name without `Boy` / `Huddle` prefix and `State` suffix.
- `air_s` only while diving; `holding` is an object id.
- `stuck.calls_without_progress`: action calls in a row that moved the body less than 0.2 m and produced no events;
  `hint` from 3 on.
- Concise stays under about 600 tokens: at most `max_objects` objects, 6 hazards, 20 events.

### 5.3 Action result (act, move_to, interact, wait_until, time step)

```json
{"ok": true, "outcome": "stopped", "stop_reason": "death", "steps": 212, "requested": 300, "frames": 212,
 "held": null, "input_mode": "agent", "warnings": [],
 "changes": {"signals": [], "fsm": [], "toggled": [], "errors": [], "checkpoints": [], "deaths": [{"step": 51420, "cause": "fall"}],
             "loads": [], "truncated": {}},
 "observe": {"...": "concise observation"}}
```

Outcomes: act `done | stopped | died | loading | cancelled | timeout | not_controllable | stalled`; move_to `arrived |
blocked | fell | died | timeout | cancelled | unreachable | loading` (+ `reason`); interact `done | failed |
not_available | out_of_reach | died | loading | timeout | cancelled`. Every action releases its input at the end unless
`act {hold:true}`, and always on error or cancel.

### 5.4 changes

`{signals [{step, object, output}], fsm [{step, object, fsm, from, to}], toggled [{step, object, active}], errors [{step,
type error|exception|assert, message}], checkpoints [{step, subscene, savepoint}], deaths [{step, cause}], loads [{step,
kind, phase started|finished}], truncated {<list>: n}}`; at most 20 entries per list (`ChangeLog.Since(mark, 20)`),
oldest first; objects as id when the object has one, else path. Sources: core `EventMonitor` (signals, FSM states),
`Application.logMessageReceived`, `DeathManager.died`, `GameManager.loadStarted / loadFinished`,
`Savegame.savepointSaved` / savepoint polling, active-state polling of objects near the body.

### 5.5 wait_until result

```json
{"ok": true, "satisfied": true, "which": 1, "condition": {"grounded": true}, "timed_out": false,
 "waited_steps": 37, "elapsed_ms": 410, "observed": {"grounded": true, "state": "Run"}, "changes": {}, "observe": {}}
```

Timeout: `satisfied false, timed_out true, which -1`. A condition with two keys or an unknown id -> `bad_args` /
`stale_id` before the game runs.

### 5.6 Busy and verdicts

```json
{"ok": false, "error": {"code": "busy", "message": "busy: move_to is running (job j12)",
 "hint": "wait for it with wait_until {any:[{job:\"<id>\"}]}, or cancel the running call"},
 "busy": {"job": "j12", "tool": "move_to", "lane": "control", "running_s": 3.4}}
```

Verified writes (`reflect set`, `logic`, `camera box_set`, `body teleport`, `hazard set`): `verdict` = `confirmed` (read
back now and after `verify_frames`), `reverted_by_game` (right after yes, later no), `not_applied` (right after no),
`unreadable`; plus `before`, `after`, `after_k`.

### 5.7 Screenshot

`{ok, mode, width, height, src_width, src_height, path, black, frame, marks [{id, label, x, y}]}` + one image block for
preview / marked. The full PNG always stays on disk (`_mod/shots/`, the newest 200 kept). `marked` draws the ids of the
latest observation (`Observer.ScreenMarks`) as small labels with a dot at the object. `observe {image}` uses the same
service and puts these fields into `image`.

---

## 6. Errors and busy

| Situation | Answer |
|---|---|
| Unknown tool / not allowed | JSON-RPC -32602 |
| Arguments do not match the schema | `bad_args` with the path |
| Another control job runs, or a MultiCam capture | `busy` + `busy{}` |
| A load is pending and another load is asked | `busy` (lane) or `refused` ("load in progress") |
| Main thread did not pick the request up in time | `not_ready` (nothing runs later) |
| Game not ready for play input (loading, title, menu, dead) | action outcome `not_controllable` / `loading`, or `not_ready` for tools that need a body |
| Old id | `stale_id`; ambiguous path / name -> `ambiguous` + `candidates` |
| Missing confirm for a save-writing op (only with `play.save_guard` on) | `confirm_required` (nothing done) |
| Safety rule refuses (cheats off, keyhold, master volume up, quit) | `refused` + hint |
| Game exception | `game_error` with type and message |
| Cancelled by the client or by restore | outcome `cancelled` (actions) or `cancelled` error |
| Stub not implemented | `not_built` |

Rules: every action takes the control lane with `ctx.BeginJob(McpLanes.Control, out busy)` and returns `busy` at once
when it is held; read-only tools never take a lane and may run during an action; `restore` and `jobs cancel` cancel the
running job (they do not wait for the lane); `wait_until {job}` waits on a job without the lane; one load at a time.

---

## 7. Threading rules

1. `IMcpToolProvider.Call` runs on a worker thread. Unity and game objects only inside `ctx.Main` / `ctx.MainDo`, ticks,
   and the game's own callbacks.
2. Keep one main-thread item under about 2 ms. Split scans (`state_diff`, `geometry`, `find`) into chunks with one
   `ctx.Main` per chunk.
3. Never block the main thread on a worker. Never call `ctx.Main` from the main thread (deadlock).
4. Waiting = `Stepper.Run`, `ctx.WaitUntil`, `ctx.WaitFrames` on the worker, checking `ctx.Cancelled` and
   `ctx.RemainingMs` (keep 1.5 s for the reply). A call ends in 55 s at the latest.
5. State shared between the FixedUpdate hook (`AgentCore.UpdateCommand`), ticks and workers lives behind a lock or in
   volatile fields; "any thread" APIs are safe.
6. Never cache `Boy`, `Huddle`, `Character` or anything that a load re-creates. Read `ScriptGlobals` each time; ids map
   to `UnityEngine.Object` and are checked against `null` (destroyed) and `Epoch`.
7. Hooks into game events (`GameManager.loadFinished`, `DeathManager.died`, `RuntimeCallbacks.fixedUpdate`,
   `Application.logMessageReceived`) are added once in `Register` or the first tick, wrapped in try/catch, and removed by
   nothing (the extension lives as long as the game). The core's `SceneHooks` was **not** in the core build
   Play was written against: use `RuntimeCallbacks` directly.
8. Reflection on private game members: cache `FieldInfo` / `MethodInfo` once, and report a missing member as
   `game_error` instead of throwing a `NullReferenceException`.

---

## 8. Clock and input model

- Step mode is the default once an agent acts (`Stepper.Run` switches to it if no mode was chosen in this session). The
  game is frozen between calls: `GameManager.TimeScale` and `Time.timeScale` 0. During a run: `vSyncCount 0`,
  `captureFramerate 60`, time scale 1, one frame = one step (INFERRED; Stepper measures the step delta every frame and
  reports the real count), re-freeze in the tick of the frame whose step satisfied the stop. `then: run` leaves it
  running. `runInBackground = true` while an agent acts (Restorer key `time` puts it back).
- Never freeze while loading or before ready (title hang). A freeze asked during a load is applied once
  `PlayLevels.Ready`. Every load resets the game's time scale: Stepper re-applies its mode after ready.
- Realtime mode: the game runs at `speed`; actions watch the queue and the conditions at frame rate.
- Clock ownership with a scene recorder (2026-10-25): while `InsideDev.SceneRecorder.active` is set (a manual `srec
  record start`, or an mcam record pass; read by reflection, the core build Play was written against had no recorder) the recorder owns the frame pacing
  (`captureFramerate`, `vSyncCount`, `targetFrameRate`; srec re-asserts its values every frame). The Stepper owns only
  the time scale (freeze, speed): it skips `BeginStepping`, defers an `EndStepping` until the recording ends (so neither
  side puts back the other's values), and step-mode runs follow the recorder's clock (warning `recording: ...`, a run
  can end a step or two late). An input track owns only the input (section 17). A MultiCam capture still owns
  everything (`External`).
- Music-timed hazards (mines shockwave `ForcePushManager`, lineup, lab elevator fall and water lock) follow the Wwise
  clock. Freezing at exactly 0 is safe (GlobalAudio pauses the music while `Time.timeScale` is 0). A step-mode run there
  is paced (section 21): every captured frame is held to a 60 Hz wall-clock grid (`FramePacer`, targetFrameRate -1,
  timeBeginPeriod 1), so game time keeps pace with the music during the run; frames slower than 60 fps are counted as
  late in the `paced: ...` warning. Slow motion still desyncs them. `Perception.MusicTimedNear` -> warning
  `music_timed: <what>` in `time`, every action result and `observe.warnings`; `act` / `move_to` / `interact {when}`
  wait for a phase (`hazard_eta`, `music_time`, ...) and act in the same call. Nothing refuses.
- Input: `AgentCore` replaces `GameInput`'s core (Hook A). Queue frames are consumed one per logic step; `hold` latches
  the last frame; neutral otherwise. A grab grip (`interact grab`) presses action in every step on top of that until
  `interact release`, `act {release:true}`, `input_mode human`, a cancel, a death, a respawn or a load. `input_mode mix` merges the physical pad. `Tick` reinstalls the core if Playdead's
  `SessionManager` replaced it and reports `input_hijacked` once. `GameInput.controllerType` follows the real device: if
  it reads `Digital` (keyboard used) the result warns `controller_digital` because submarine, valve and swim branches
  change.
- Button semantics to respect (report 02 section 1.2): jump is a rising edge buffered 0.25 s and needs a release before
  the next press; on a plateau `action` held = grab and a tap under 0.3 s = action; in the huddle both are held levels;
  `drop` = jump + stick down; stick magnitude 0 or 0.3..1, above 0.8 runs.

---

## 9. Safety rules (baked into code)

1. **Save**: off by default. EditorState `play.save_guard` (bool, default false, `Saves.Guard`) turns every save gate on;
   with it off nothing is refused, asks for confirm or is backed up because of the save. With it on: chapter loads,
   `load_chapter`, `reload chapter | newgame` write `INSIDE.sav` (Steam Cloud) and unlock chapters forever:
   `confirm:true` required, `Saves.EnsureBackup` before the first such write of the session (refuse when no backup could
   be made), report `backup`; the other save gates are listed in section 16. `checkpoint goto | next | prev` only with
   EditorState `play.cheats` = 1 (always). `restore` keeps the save lock unless `include_save_lock`.
2. **Steam**: never call `User.Achievement.Acquire`. Orb edits only through `achievementProgression.Acquire / Release`
   with `confirm:true`, with the warning that local bits reach Steam at the next sign-in.
3. **Music-timed hazards**: warn, pace step-mode runs to real time, offer `when` (section 8).
4. **Bodies are re-created on every respawn**: never cache them (section 7).
5. **Streaming follows the camera**: any camera override pins the body's subscene (`PlayLevels.Pin(..., "camera")`)
   unless `pin:false`; restore unpins.
6. **One load at a time**: `busy` / `refused`.
7. **No OS input**: `keyhold` and `focustest` are refused through MCP; input only through `AgentCore`.
8. **Quit**: no tool quits the game; the menu hides Quit, chapter loads over progress and Reset To Defaults unless
   `allow_destructive`.
9. **Sound policy**: master volume RTPCs accept only 0 (mute); nothing raises the game volume.
10. **Hot reload**: Play can be loaded later with `ext load`; it must work when the game is already in the world, in a
    menu, or loading.
11. **Restore everything**: every visible change registers a Restorer key; `restore`, session end, `mcp off`, idle 10
    min and game quit put it back.
12. **Tests**: never on the Steam install; in-game tests ran later, on a separate copy of the game folder.

---

## 10. File ownership

Play files live in `src/play/`, the Mcp files in `src/mcp/`. Each role also had its own offline test suite (not shipped).

| Role | Files (only these; new files must match the patterns) |
|---|---|
| SERVER | `src/mcp/McpServer.cs`, `McpStdio.cs`, `ServerApi.cs`, `ServerSchema.cs`, `Sse.cs`, new `src/mcp/Server*.cs`, `Sse*.cs`; `src/mcp/stdio/Json.cs` (verbatim copy of the core Json.cs, do not edit) |
| HOST | `src/mcp/Extension.cs`, `DevTools.cs`, `HostRegistry.cs`, `HostContext.cs`, `Jobs.cs`, `Shot.cs`, `Png.cs`, `Zlib.cs`, new `Host*.cs`, `Jobs*.cs`, `DevTools*.cs`, `Shot*.cs` |
| INPUT | `src/play/AgentCore.cs`, `Act.cs`, `Interact.cs` (new files `AgentCore*`, `Act*`, `Interact*`) |
| TIME | `Stepper.cs`, `Waits.cs`, `Restore.cs` (new `Stepper*`, `Waits*`, `Restore*`) |
| OBSERVE | `Observer.cs`, `Perception.cs`, `ChangeLog.cs` (new `Observer*`, `Perception*`, `ChangeLog*`) |
| NAV | `Navigator.cs`, `Geometry.cs` (new `Navigator*`, `Geometry*`) |
| WORLD_A | `PlayLevels.cs`, `Saves.cs`, `Menus.cs` (new `PlayLevels*`, `Saves*`, `Menus*`) |
| WORLD_B | `WorldCamera.cs`, `WorldLogic.cs`, `Hazards.cs`, `AudioRender.cs`, `Reflect.cs`, `StateDiff.cs` (new files with those prefixes) |
| GUIDE | `guide/**` (content), `Guide.cs`, `Notes.cs` (new `Guide*`, `Notes*`) |
| CTL (2026-10-25) | `src/play/Ctl*.cs`; plain endpoints: class `ServerPlain` and the routing in `src/mcp/McpServer.cs` (SERVER file); `tools/inside_ctl.py` |
| ARCH (frozen) | `PlayExtension.cs`, `PlayShared.cs`, the build scripts (now `build/build_mcp.sh`, `build/build_play.sh`), the test kit, contract tests and stubs, this document |

SERVER also owns `ServerApi.cs`, `ServerSchema.cs`, `Sse.cs` and `stdio/Json.cs`; HOST also owns `Png.cs`, `Zlib.cs`,
`HostRegistry.cs`, `HostContext.cs`, `Jobs.cs`, `DevTools.cs`, `Shot.cs`. Class names that differ from file names:
`Restorer` (Restore.cs), `NavGeometry` + `GridSlice` (Geometry.cs, `InsideDev.Geometry` exists in the core), `GuideBook`
(Guide.cs, `InsideDev.Guide` exists in the core).

Read-only for this work (owned by the replay workflow): the core `src/InsideDev/**` and `bin/InsideDev.dll`,
`tools/mcam_*`, `tools/srec_tool.py`, `specs/`, `docs/REPLAY_DESIGN.md`, `docs/MULTICAM*.md` and InsideMuter
(`src/muter/`); also the decompiled game code and the Steam install.

---

## 11. What a builder must not change

- Any public signature in sections 4.1-4.5 (ApiProbe fails), any tool name, parameter name, type, enum value, default,
  limit or output field name (the contract test fails), `ToolDef` annotations and profiles.
- `PlayExtension.cs`, `PlayShared.cs`, the build scripts, the test kit, contract tests and stubs, this document (the
  architect updated stubs and docs together).
- Files of another role, even to fix a compile error: report it instead.
- Cross-module access through `internal` members of another role's file (all Play files share one assembly; only the
  public APIs above count).
- The core DLL and anything of the replay workflow.

Allowed: implement bodies, add private / internal members inside your files, add new files matching your patterns,
sharpen tool descriptions (keep the first sentence meaning), add output fields (outputs are open; document them in your
hand-over note).

---

## 12. Build and offline tests

Toolchain: Roslyn `csc.dll` from a .NET SDK, C# 7.3, references `<game>/INSIDE_Data/Managed` and `<game>/GameCode`
(Unity 5.0 Mono, .NET 3.5 BCL: no Task, no CancellationToken, no Tuple / ValueTuple, no `string.IsNullOrWhiteSpace`, no
`Enum.TryParse`) and the core `bin/InsideDev.dll`. The stdio exe is built against .NET Framework 4.x reference assemblies
(`NETFX`, found automatically in the usual Windows and Mono folders).

| Command | Does |
|---|---|
| `GAME=<game> sh build/build_mcp.sh` | `bin/InsideDev.Mcp.dll` (all `src/mcp/*.cs` except McpStdio.cs) and `tools/mcp/InsideDev.McpStdio.exe` (McpStdio.cs, McpServer.cs, Server*.cs, Sse*.cs, stdio/Json.cs). Needs `bin/InsideDev.dll` (`build/build_mod.sh`) |
| `GAME=<game> sh build/build_play.sh` | `bin/InsideDev.Play.dll` from `src/play/*.cs` against `bin/InsideDev.dll` and `bin/InsideDev.Mcp.dll` |

During development each role also had a snapshot build (its live files plus every other role's stubs; it compiled
`ApiProbe.cs` with the frozen signatures and ran the contract test, which wrote `tools.md` and `tools_<profile>.json`) and
a .NET 9 test kit (`TestKit.cs`: `--pure` tests without Unity, `--game` tests against the Unity and game DLLs, where pure
Unity math works and engine calls do not). The CTL suite ran `CtlTest` (pure: durations, seq grammar, ctl parser, held
input, texts, console echo, `/cmd` `/state` `/ping` against a fake host and over loopback HTTP), `CtlGameTest` (in-process
server, host and Play with a fake main thread) and `cli_test.py` (inside_ctl.py against a stub server and a stub bridge);
the TRACK suite ran `TrackTest` (pure: format, trim, scheduling, correction math) and `TrackGameTest` (the tool's file ops
through the real handler). None of these offline tests is shipped.

Results of the architect pass: Mcp.dll, McpStdio.exe, Play.dll build with 0 errors; ApiProbe compiles; SchemaBuilderTest 5 / 5;
ContractTest 44 / 44 (12 / 31 / 38 tools). Integration pass: see section 15.

Offline tests each role owned (they had to pass before hand-over, plus the snapshot build):
- SERVER: `Schema.Validate` (types, enums, ranges, nested, defaults, unknown keys, error paths), `ToJson` shapes, SSE
  framing and parsing round trip, a loopback HTTP test of initialize / tools/list / tools/call / progress stream /
  cancel / resources / prompts / list_changed against a fake `IToolHost` (pure: no Unity).
- HOST: main queue (timeout drops the item, cancel before run, exceptions -> game_error) with a fake pump thread,
  jobs (busy, detach, await, cancel, EndOfCall), registry (clash, version, profile filter, instructions cap), output
  capping / paging, command refusals, PNG label drawing.
- INPUT: queue semantics (steps per frame, latch, release, mix merge, deadzone), `ParseInputs`, verb macro tables.
- TIME: run loop against a fake clock (stop reasons, step accounting with 0 / 2 steps per frame), condition compiler,
  Restorer ordering and idempotency.
- OBSERVE: id registry (never reused, epoch, destroyed), concise size budget (a full fake observation under 2400 chars),
  ChangeLog ring, caps and truncation, state-name shortening.
- NAV: `GridSlice`, path / gap / ledge decisions on hand-made slices.
- WORLD_A: ready predicate truth table, load mode / confirm / backup decisions, menu item classification
  (destructive), chapter list mapping.
- WORLD_B: verdict logic, `Snapshot.Diff`, value rendering, reflection argument conversion (JSON -> typed).
- GUIDE: frontmatter parsing, index, level sections, `TopicFor` mapping, resource uri matching, notes ring.

---

## 13. Builder notes

- **SERVER**: section 4.6. Keep McpServer and every Server* / Sse* file Unity-free and InsideDev-free except `Json`
  (the exe compiles them with `stdio/Json.cs`). Bump the proxy version string to `Boot.Version` of the game when up.
- **HOST**: section 4.7. Implement `McpRegistry`, `HostCallContext`, `MainQueue`, `AuditLog`, `HostJobs`, `McpShot`
  (move the v0.5 screenshot code there; add label drawing with a tiny built-in font), the `jobs` tool, and the dev tool
  fixes. Keep the v0.5 tool behaviour otherwise.
- **INPUT**: `AgentCore` from `src/det/FilmInput.cs` (install / uninstall through the private static
  `GameInput.mCore`), `act`, `interact` macros with state checks after every phase.
- **TIME**: `Stepper` (step / realtime, runs, freeze rules, music warning, runInBackground), `wait_until`, `Restorer`,
  `restore`, the `time` tool.
- **OBSERVE**: `observe`, ids and resolution, perception queries (pure ones only), `ChangeLog` sources, screen marks.
- **NAV**: `move_to` closed loop, `NavGeometry` slices and ledges, the `geometry` tool.
- **WORLD_A**: `checkpoint`, `level`, `stream`, `save`, `menu`, `ui`; the save backup and confirm rules.
- **WORLD_B**: `camera`, `logic`, `body`, `hazard`, `audio`, `render`, `reflect`, `state_diff`; verdicts; Restorer keys.
- **GUIDE**: `guide/` content from per-scene notes of a game code audit and the decoded
  screenshot route (Playdead's debug screenshot positions; neither source is shipped); topics per chapter, mechanics, known
  stuck spots, three spoiler levels; `guide`, resources, prompts, `notes` and its hotkey (check DevRoot's hotkeys first).

---

## 14. Later: in-game checks (separate run, game copy only)

1. Load order: `ext load` with Play after Mcp; `play status` shows no problems; `tools/list` per profile.
2. Forest start, step mode: `observe` -> `act` right 120 steps -> jump -> fall -> death -> `wait_until {alive}`; no
   window focus; `captureFramerate` gives exactly one step per frame.
3. `move_to` / `interact` on the first crate and ledge; `menu` open, navigate, close while frozen.
4. `checkpoint reload`; `level load_checkpoint mode respawn` with backup; `save lock` blocks the write (INFERRED).
5. Camera override with the pin: no unload after 4 s; restore puts everything back.
6. Mines shockwave in realtime; hat, huddle and submarine control through the same input core.
7. Focus loss with `runInBackground = true`; title freeze safety during a startup load.

---

## 15. Integration pass (2026-10-25)

All nine roles landed; no stub is left. The integration run (offline, not shipped) builds the live tree against the
core, compiles ApiProbe, runs ContractTest (45 / 45, 12 / 32 / 39 tools), every role's `--game` test against the
integrated DLLs, the end-to-end test `IntegrationTest` (real McpServer + McpExtension + PlayExtension + DevToolsProvider
over HTTP and through McpStdio.exe), `CoreCompat` (every core member our DLLs reference exists in a given core) and a small
MCP client (`mcp_call.py`). The in-game checks were planned as numbered tests (T0 to T27, the in-game tests named below).

Integration edit: `src/mcp/Extension.cs` subscribes to `McpServer.SessionClosed`; when the last session ends by DELETE
the next `Tick` runs the restore (section 4.7 trigger that HOST could not reach through `IToolHost`). A server `Stop`
does not trigger it (mcp off and quit restore already). `mcp selftest` now DELETEs its two sessions at the end, so they
do not keep the count above 0. Clients that open and DELETE a session per call would restore after every call;
`mcp_call.py` keeps its session and never sends DELETE.

Contract additions reported by the roles (outputs are open; names are now part of the contract):
- screenshot: `uri`, `camera`, `boy`, `note`, `problem`; text tools: `next_offset`; find_objects: `count`; set_active:
  `verdict`, `before`, `after`, `after_k`, `active_in_hierarchy`; jobs: `hint`; job rows: `detached`, `cancel_reason`;
  timeout results: `job`.
- act / interact: `interact.phases[]`; AgentCore `Describe()`: `installed`, `controller`, `last_fed`, `gated_steps`.
  On death act returns outcome `died` with `stop_reason` `death` (section 5.3 shows `stopped`). A successful `grab` keeps
  action held after the call. `act {input_mode:"human"}` releases input, uninstalls the core and runs no inputs.
  `Enqueue(frames, latchLast:false)` clears a held input.
- time: `running`, `freeze_pending`, `vsync`, `run_in_background`, `seed`, `frames_run`, `stop`, `detail`; wait_until:
  `stop`, `detail`; restore rows `job`, `free_camera`, `provider:<name>`. `alive` means playing with the load fade-in
  done; `steps` stands for frames; `checkpoint_changed` for savepoint_changed. `near` / `far_from` measure x / y to the
  target's transform.
- observe / changes: `changes.other` (note, stalled, input_hijacked), `changes.warnings`, `truncated.lost`,
  `errors[].repeat`, `signals[].to`; observe `body.depth`, `objects[].path` (full), state `in_reach`, `truncated`
  (concise), `perf`. Hazards are searched within at least 20 m. Errors come from `Application.logMessageReceived`.
- move_to: `goal`, `state`, `route`, `replans`, `rays`; geometry: `ledges[].source`, `center`, `rays`, `legend`, `notes`
  and the probe fields. `y` everywhere is the body centre (`Boy.pos` = `Boy.pos3`, FP/Boy.cs:279-300); feet = y - 0.665.
- WORLD_A: status `spawn`, `pending`, `press_start`, `reason_text`; `load_ms`; current `scene`; stream `body_subscene`;
  save `locked_by`, backup `from`; menu `message`. Menu code goes through reflection (UnityEngine.UI.dll is not referenced
  by the build). `ui settings` accepts only volume 0; the ending secret is replayed without `User.Achievement.Acquire`.
  A respawn load past the unlocked chapter point needs confirm (only with `play.save_guard` on, section 16).
- WORLD_B: state_diff `diff[].change`, `warnings`; logic rows `pending_exits`, `probes_inside`, `outputs`; interactables
  `class`, `flag`, `unique_id` (world flags are kind `flag`, set with `signal_in SetTrue / SetFalse`). Resources
  `inside://world/camera_route`, `inside://world/tunables`.
- GUIDE: guide `sections`, `max_level`, `warnings`; notes `cleared`, `backup`, `image`, `warnings`. Prompts
  `playtest_from_checkpoint`, `solve_here`. Settings EditorState `play.guide_dir`, `play.guide_max`, `play.notes_key`
  (default Home), env `INSIDEDEV_GUIDE_DIR`.

Open decisions (not changed in this pass): dev tools such as `command` do not take the control lane, so they can change
time or load while a play action runs; AgentCore (`input`) and Stepper (`time`) both save and restore
`Application.runInBackground` (correct in either order); the guide index is about 540 tokens (target 300); `checkpoint
goto | next | prev` stay behind `play.cheats` only, because requiresUserInteraction is per tool.

## 16. Review fixes (2026-10-25)

Two reviews (game safety, agent ergonomics) were applied. Contract changes a client or a later change should know:

| Area | Rule now |
|---|---|
| Save guard | EditorState `play.save_guard` (bool, default false), read through `Saves.Guard` (Play, refreshed on the main thread every 30 frames) and `HostSettings.SaveGuard` (Mcp, `ReadSettings`). Off: no save backup, no save `confirm_required`, no save refusal anywhere. Every rule in this table that names the save, a backup or a savepoint applies only when it is on. Always on regardless: the secret / achievement blocks, orb edit and ending secret confirm, master volume 0 only, keyhold / focustest / remote off / mcp off / mcp port / quit / exit refusals, `play.cheats` for checkpoint goto / next / prev. |
| Save lock | `Saves.MarkCheat` marks the MCP lock when a quiet load, a cheat checkpoint, or a savepoint fired or entered under it runs. A marked lock is kept at quit (INSIDE.sav keeps its pre-lock bytes), `restore {include_save_lock}` keeps it, and `save {op:"unlock"}` needs `confirm:true`. `save info` shows `cheat_under_lock`. |
| Quiet load | Applies Savegame.SetSpawnpoint in memory: spawn, `PersistentData.enabled = dynamic`, progression (target, or -1:-1 for a dynamic one before it). No chapter unlock. |
| Respawn loads | With the guard on, a target before the current progression needs `confirm` (`LoadPolicy.Decide` takes `guard`); a cheat checkpoint only warns. With it off, no load mode needs confirm or makes a backup. |
| Loads through `command` | With the guard on, `load`, `spawn`, `reload`, `respawn`, `cspawn go / respawn` are refused in the dev `command` tool and in scenario `command` steps (`ScenarioPlan.CommandRefused(cmd, saveGuard)`); off, they run. |
| Secret objects | `ReflectValues.SecretTypes` / `HostText.SecretTypes` (FinalSecretFade, SecretPod*, Secret, SecretSuitcase, AchiementTrigger, StartNewGameMenuItem): reflect calls and writes, logic signal_in, signal_out / trigger_fire that reach one within 3 wires, signal_wire into one, and the dev signal / fsm / call / set / signalin / fsmset / sigwire commands on one are refused. |
| body teleport | With the guard on: backs the save up first; a destination inside a savepoint is refused unless the lock is on (then marked) or `force` (`WorldLogic.SavepointEntry`). Off: no backup, every destination allowed. Firing a savepoint (trigger_fire, signal_in doSave), reflect save writes, menu / ui / save tool backups and the brightness / rumble / message_answer / ending restart / unlock confirms follow the same switch. |
| Shockwave restore | Re-enabling ForcePushManager resyncs `previousMusicTime`, drops a pending `killTimer` and re-arms the music checkers. |
| Input | AgentCore steps aside for another InsideDev core (FilmInput) and never puts back a finished one. |
| Camera | The core free camera gets a pin (owner `freecam`); pins are retried every 30 frames; observe warns `camera_away` while the camera is overridden or free. |
| Stream freeze | Undo restores the original CullingActive and leaves it to a running load. |
| Session end | The last-session restore waits `mcp.restore_on_close_s` (default 60, 0 = off) with no session, no call in flight and no control job. |
| Stuck counter | Progress = new ground (outside the box covered since the checkpoint), a new checkpoint, or a near-body event not seen before at this checkpoint. No body counts. `stuck.deaths_here`, hint from 2 deaths. |
| Not ready | observe and act carry `next` (`menu submit` at the title, `menu close` when paused, `wait_until {alive}` / `{ready}`, `menu message_answer` for a storage message). |
| changes | Compact by default: signal / fsm entries more than 15 m from the body are counted in `far{}`, 8 per list, about 1600 chars; `act {changes:"full"}` and the dev logic tool list everything (`ChangeLog.SinceFull`). |
| Arguments | A string where the schema wants an array, object, number, integer or boolean is parsed when it can be; `warnings` starts with `coerced: ...`. |
| Output | A result that breaks its outputSchema loses the offending properties (`Schema.Repair`), named in `warnings` as `schema_repair: ...`. |
| Images | `screenshot` has no outputSchema and its image results go out without structuredContent; observe keeps structuredContent and points to screenshot. In-game test T14 checks real clients. |
| Profiles | Hints name the caller's tools; `menu {op:"message_answer", answer, confirm}` on play. `inside_status` returns the game's instructions once it is up. The loop tools have `anthropic/alwaysLoad`. |
| Input tracks | New tool `input_track` (INPUT; play, dev; write; section 17). Profiles are now play 13, dev 33, all 40 (Cursor's cap: a further tool must be an op of an existing one). IntegrationTest, the INTEG driver and TrackGameTest check 13 / 33 / 40. |
| Clock ownership | A running SceneRecorder (srec record, mcam record pass) owns the frame pacing; the Stepper keeps only the time scale and defers its pacing restore until the recording ends (section 8). `time` state adds `recording` and the warning `recording: ...`; step-mode run results carry the same warning. An input track owns only the input. |
| observe body | `grab` (huddle: none or the grab child state, GrabBeam / GrabBox / GrabContainer / GrabItem / GrabShield / GrabSitOnTop), `holding` now also for the huddle, `stick_forced` (boy and huddle: the game drives the stick, e.g. the railing crash BoySetInput for 3 s or HuddleFallTest at the CEO wall), `forced_by` (script, object, FSM state, what it holds; also when only buttons are forced). `Perception.ForcedInput(character)` is the public check. |
| AgentCore | `Describe()` (observe full `input`) adds `track {recording}` or `track {playing, step, of}` while a track records or plays. While a track plays the step's frame comes from the track, not the queue. |

## 17. Input tracks (2026-10-25)

Rehearse a scene in step mode, keep the exact input, and play that input back as one realtime take that a manual
`srec record` (and later `mcam replay`) films. Final takes are played through game logic by input only; the position
correction below is the Playdead QA-replay correction, not staging. Files: `src/play/ActTrack.cs` (tool, game
side), `ActTrackCore.cs` (Unity-free), hooks in `AgentCore.cs`; offline tests `TrackTest`, `TrackGameTest`.

### 17.1 Recording

- `input_track {op:"start", name?}` installs AgentCore (agent mode unless mix was chosen) and records from the next logic
  step. `AgentCore.CommandReceived` hands every fed command to `ActTrack.Record`: the stick and buttons that reached the
  game (agent queue, latch, neutral; in mix or human mode the pad), `LogicManager.stepCount`, `Time.fixedTime`, and the
  controlled body's `pos3` / `velocity3` read before that step's logic (the point where Playdead's `InputRecorder`
  records). Flags per step: 1 gated (GameInput disabled or not playing), 2 dead, 4 input forced by the game
  (`Perception.ForcedInput`), 8 huddle grabbing, 16 no body.
- k = step - start step; a repeated or older step count still advances k by one, a jump leaves a gap (steps run while
  AgentCore was not installed); playback holds the previous frame across a gap.
- A load start ends the recording (`stop_reason` "a load started"), as does the cap of 72000 frames (20 min); the frames
  so far stay in memory. `stop` ends it; `info` shows counts, `forced_spans` and up to 60 `samples` [k, x, y, vx, vy,
  flags] for choosing a range; `trim {from, to}` keeps that step range and re-keys it to start at 0 (`trimmed {from, to,
  of}` in original steps, nested trims add up); `save {name, overwrite}` writes the file (temp file, then move),
  `load {name}` reads one, `list` reads only the first line of each file.
- Track fields (`track`): name, body, subscene, savepoint, checkpoint, steps, frames, seconds, created, source, clock,
  gaps, forced_steps, gated_steps, dead_steps, grab_steps, forced_spans, start, end, path_m, max_speed, trimmed,
  sample_columns, samples, saved. The output schema lists the main ones (outputs are open).

### 17.2 File format (`<_mod>/input_tracks/<name>.json`, version 1)

```
{"header":{"format":"insidedev.input_track","version":1,"name":...,"created":...,"body":"huddle","subscene":72,"savepoint":0,
 "checkpoint":...,"clock":"step","source":"recorded|trimmed","load_count":...,"start_step":...,"start_time":...,
 "step_s":0.016667,"steps":...,"frames":...,"seconds":...,"trimmed":{"from","to","of"}},
"columns":["k","t","sx","sy","b","px","py","pz","vx","vy","vz","f"],
"frames":[
[0,0,1,0,0,3381.204,-847.112,0.031,0,0,0,0],
...
]}
```

The header is on line 1, so `list` parses one line. Columns are read by name. The stick is written round-trip exact
(`R`), so playback feeds the same floats; t, position and velocity have 3 decimals (float resolution near x 3400 is
0.25 mm). b = 1 jump + 2 action. Names: 1..64 of letters, digits, `_ - .`, not starting with `.`. A 40 s track is about
150 KB.

### 17.3 Playback

- `input_track {op:"play", name?, speed, correct, gain, max_step, stop_drift, max_start, then, wait, timeout_s}` takes
  the control lane (busy otherwise), checks on the main thread that the game is ready, that the body kind matches the
  track's and that the body is within `max_start` (1.5 m; 0 = no check) of frame 0 (refused with the checkpoint load to
  make), installs AgentCore in agent mode, releases queued / held input (and a grip), sets the Stepper to realtime at
  `speed`, holds the rehearsal's lockstep (`Stepper.HoldLockstep`: captureFramerate 60, one logic step per frame, frames
  paced to real time; a scene recorder that samples owns the pacing instead) and arms the player. The next logic step plays frame 0. The job is detached: `wait` (default) waits up to `timeout_s`
  (45 s; a 40 s take fits one call), otherwise the result has `outcome: playing`, `job` and `next`; then
  `wait_until {any:[{job:"j7"}], timeout_s:50}` (watch only, no lane) until it is satisfied. Cancel with
  `input_track {op:"stop"}`, `jobs {op:"cancel"}` or `restore`; a cancelled play call cancels the job too.
- Per logic step (`AgentCore.UpdateCommand`, inside `GameManager.FixedUpdate` before `Huddle.UpdateLogic`, where
  Playdead's `InputRecorder.Replayer` feeds and corrects): `TrackCursor` gives relative step j the last frame with
  k <= j, one frame per step, no 600-step cap; the gate and mix rules of AgentCore stay as they are.
- Ends (tick `input_track`, every frame): `done` (all steps fed), `cancelled`, `died`, `loading`, `stalled` (600 frames
  without a step: pause menu or focus), `desynced` (`stop_drift` > 0 and exceeded). Input is neutral from the next step.
  `then: run` (default) leaves the game running in realtime, `then: freeze` puts the Stepper back into step mode.
  The result: `outcome`, `stop_reason`, `track`, `playback {name, step, of, speed, correct, steps, measured, corrected,
  skipped, forced_steps (live), gated_steps, start_offset_m, max_drift_m, max_drift_step, mean_drift_m, end_drift_m,
  max_shift_m, total_shift_m, drift_per_s[]}`, `warnings`, `observe`.

### 17.4 Correction (InputRecorder.AdjustPosition style)

Playdead's replayer lerps `pos3` and `velocity3` toward the recorded ones and calls `Character.Teleport`
(FP/Playback/InputRecorder.cs:148-157; a 0.01 lerp per step while desynced in the Subscene session). Here, on every step
that was recorded (k == j) and only while the body is alive, active and not gated:

- drift d = |recorded - live| in x / y. Inside the deadband (0.01 m) nothing moves. Otherwise the position moves
  `gain` (0.05) of the error, capped at `max_step` (0.02 m per step, 1.2 m/s at most), and the velocity moves `gain` of
  its error, capped at 0.5 m/s per step. A 0.5 m start offset closes in under 2 s; a body that runs 5 % slower stays
  within about 0.1 m (TrackTest).
- Huddle: `Huddle.Teleport` would set the depth plane (`playfield.posz`) to the corrected z every step and reset the
  cloth render interpolation (`oldPos = pos`), so the correction does what Teleport does, in x / y only: every cloth
  body's transform, `pos`, `oldPos` and visual bone move by the shift, every spine bone `SetPos(pos + shift)`, then
  `Huddle.AddVelocity(dv)`. The depth plane and the huddle's state are untouched (grabs, falls and smashes go on), so the
  speed thresholds (railing >= 2.25 m/s, tables > 3, CEO wall > 5.5) can repeat.
- Boy: `Boy.Teleport` clears his state (`BoyUtils.ClearState`), so the boy gets `Boy.pos += shift` and
  `Boy.AddVelocity(dv)`, only in run, jump or swim without a special mode (Playdead's rule) and not while mind
  controlling; other steps are measured, not corrected.
- `correct: false` (the default since the audit fixes) measures the drift without moving anything: final takes play
  through game logic by input only, and the game reproduces the rehearsal by itself at the same 60 Hz lockstep.
  `correct: true` is the explicit opt-in to the nudges above; its result warns `correction_on` with the corrected
  steps. Nothing here is verified in the game yet (INFERRED).

### 17.5 Clock and recording at the same time

The take: track = input, Stepper = time scale (realtime 1), scene recorder = frame pacing (section 8). `srec record
start` is not an mcam session, so nothing answers busy; `act`, `move_to` and `wait_until` keep working during a manual
recording, and a step-mode run then follows the recorder's real clock (warning `recording: ...`). Start the recording
between calls, before play: the recorder skips frames whose game time did not advance, so a frozen game before play
costs nothing, and the world and the body start together exactly as in the step-mode rehearsal.

### 17.6 Workflow (cafeteria -> CEO office, about 35 s)

1. Rehearse: `level {op:"load_checkpoint", subscene:72, savepoint:0}` (or 73:0 for retakes), `wait_until {ready:true}`,
   `time {op:"rng_seed", seed:1}`, `input_track {op:"start", name:"cafeteria_rehearsal"}`, then `act` / `move_to` in step
   mode. `observe.body.stick_forced` and `forced_by` show the railing crash (stick held right 3 s) and the CEO wall
   (HuddleFallTest): wait those out instead of fighting them.
2. `input_track {op:"stop"}`, `{op:"info"}`, `{op:"trim", from, to}`, `{op:"save", name:"cafeteria_take1"}`.
3. Take: load the same checkpoint, `wait_until {ready:true}` (step mode freezes after ready), the same seed, stream pins
   (`stream {op:"pin"}`), `command "srec record start <dir> roots=auto,subscene:#huddleCeoFallAndDogs_Gameplay
   clock=real audio=on"`, then `input_track {op:"play", name:"cafeteria_take1"}` and, if it returned `playing`,
   `wait_until {any:[{job:"<id>"}], timeout_s:50}`. Read `playback.max_drift_m` and `drift_per_s`.
4. `command "srec record stop"` (no load in between), then `mcam replay` in the same session.

### 17.7 Limits and open points

- Not run in the game yet (in-game test T15). Inferred: the per-step x / y shift of the huddle cloth looks smooth, the
  thresholds repeat with the default gain, `Time.fixedTime` is the step's game time.
- Determinism is not claimed: PhysX, crowd timers and particles differ per run; the correction keeps only the body on
  its path. Large drift means the run diverged (a missed jump, a different smash): `stop_drift` ends such takes.
- The track records the main character (`ScriptGlobals.mainCharacter`); a hat dude driven by the boy is not corrected.
- No save gates (play.save_guard is off by default) and nothing touches Steam achievements.

## 18. ctl console commands (2026-10-25)

Plain-text control of the boy and the huddle for a person or a script, without an MCP client. Files:
`src/play/CtlCore.cs` (Unity-free: durations, seq grammar, word parser, held input, reply texts), `Ctl.cs`
(`CtlExtension`, a second extension class of Play.dll that owns only the `ctl` word, and `CtlCommands`), `CtlRun.cs`
(jobs and the main-thread game side); `CtlConsole.cs` (in-game console hook) was removed by the audit fixes (section 21).
Offline tests `CtlTest`, `CtlGameTest`, `cli_test.py`. `ctl` is a console
command, not an MCP tool: the profiles keep 13 / 33 / 40 tools.

Reachable from:
- the file bridge (`_mod/cmd/in_*.txt` -> `out.txt`) and `Remote.RunCommand`, which ask the extensions for unknown words;
- the MCP `command` tool (dev): `command {command:"ctl run right 1.2"}`; a started job can be awaited with
  `wait_until {any:[{job:"jN"}]}` or `jobs {op:"await"}`;
- `POST /cmd` (section 19) and `tools/inside_ctl.py`;
- the in-game console (` key): since the audit fixes the core's `Commands.Run` (src/InsideDev/DevRoot.cs) hands a word
  it does not know to the loaded extensions' `Command` handlers, tokenised like the bridge (quotes group words), and
  logs the reply; only when no extension answers does it log `unknown command`. The bridge path (`Remote.Run`) asked
  the extensions already and calls `Commands.Run(line, core, false)`. The log-watching `CtlConsole` is gone, so a core
  from before the audit fixes (2026-10-25) no longer runs `ctl` from the console (bridge, `/cmd`
  and the MCP `command` tool still do).

### 18.1 Commands

`<t>` is a duration: a plain number is seconds (`1.2`), a whole number followed by `s` is logic steps (`72s` = 72 steps =
1.2 s; `1.5s` is an error, not seconds). Optional `for` before it. At most 600 s.

| Command | Does |
|---|---|
| `ctl status [json]` | the lane owner, plus the body line; `json` (or `ctl state`): the concise observe JSON on one line |
| `ctl stick <x> <y> [for <t>]` | stick (x right +, y up +; 0 or strength 0.3..1); without `<t>` it is held |
| `ctl press <jump\|action\|grab ...> [for <t>]` | buttons (grab = action); without `<t>` held |
| `ctl tap <button ...> [<t>]` | a tap: pressed 5 steps, released on the 6th (0.1 s); with `<t>` pressed that long |
| `ctl hold <left\|right\|up\|down\|stick <x> <y>\|jump\|action\|grab ...>` | add to the held input (directions add up: `hold up right`) |
| `ctl release [all \| stick\|jump\|action\|grab ...]` | release everything, or only those parts |
| `ctl run left\|right [<t>]`, `ctl walk left\|right [<t>]` | stick x = 1 / 0.5; without `<t>` held |
| `ctl jump [left\|right] [<t>]` | a jump tap; with a direction a running jump: stick that way for `<t>` (default 0.5 s), jump pressed the first 5 steps |
| `ctl grab on\|off` | hold / release action |
| `ctl swim <dir> [<t>]` | dir = left, right, up, down, up-left, up-right, down-left, down-right (ul, ur, dl, dr) |
| `ctl goto <x> [<y>] [walk]` | the `move_to` navigator to world x (y for swim / climb targets), stop exact, 1800 steps at most |
| `ctl stopat <x> [walk]` | plain stick toward x (no jumps or climbs); the stick goes neutral in the step that crosses x (held buttons stay); 60 s at most, else outcome `not_reached` |
| `ctl seq "<script>"` | an input script (18.3) as one job |
| `ctl track rec [name] \| stop \| save <name> [overwrite] \| play [name] [speed <k>] [freeze] [correct\|nocorrect] \| list \| info [name] \| status \| load <name> \| trim <from> [<to>]` | the `input_track` ops (section 17); `play` is a job (the track's own end finishes it); `stop` during a playback cancels its job |
| `ctl time [state] \| freeze \| run [<speed>] \| step <n> \| speed <k>` | the `time` ops: freeze = step mode, run = realtime at the current (or given) speed, step = n logic steps (1..600, a job) |
| `ctl mode agent\|mix\|human` | AgentCore's mode; human gives the pad and keyboard back (the next ctl input takes over again) |
| `ctl job [jN]` | the state of the last (or that) ctl job: `running: ...` or its end line |
| `ctl stop` | cancel the running ctl job and release all input (an MCP job is not touched: busy) |
| `ctl help` | the list above |

### 18.2 Held input, clock

- The held input is AgentCore's latch (the same one `act {hold:true}` sets). `hold`, untimed `stick` / `press` / `run` /
  `walk` / `swim` and `grab on` change it; `release` and `grab off` clear parts of it. Timed commands add the held
  buttons to every step and use the held stick where they give none (so `ctl grab on`, then `ctl run left 1` pulls a
  box), and the held input is queued right after their last step (no gap). Any end other than done (death, load,
  cancel, stall, timeout) releases everything, like `act`.
- Clock: ctl never switches it on its own. Step mode stays step mode (the game is frozen between commands and a timed
  command advances it for exactly its steps). A clock that no tool has touched yet, or one running in realtime, is
  chosen as realtime before the run (the Stepper would pick step mode for an untouched clock), so a person's game keeps
  running. A realtime clock paused with `time pause` stays paused and runs only for the command. `ctl time freeze` /
  `run` switch on purpose.
- Every input command installs AgentCore (agent mode unless mix was chosen). Not ready (loading, title, paused, dead, no
  body) refuses with `failed: not ready: ...`; cutscene and scripted states only warn.

### 18.3 seq grammar

```
script  = segment { (" " | "," | ";") segment }
segment = atom { "+" atom }               atoms of a segment start together; the segment lasts as long as its longest atom
atom    = R<t>[@m] | L<t>[@m] | U<t>[@m] | D<t>[@m]   stick right / left / up / down for t, strength m 0.3..1 (default 1);
                                                       sticks of one segment add up (R1+U1 = diagonal, length capped at 1)
        | J[<t>]   jump: without t a tap (5 steps pressed + 1 released), with t pressed for t
        | A[<t>]   action: the same (a tap under 0.3 s is the game's action, held is grab)
        | G<t>     grab: action held for t
        | W<t>     wait: no input of its own for t (the held input from ctl hold stays)
t       = seconds (1.2, .5) | steps (72s)  letters in any case; at most 500 segments and 600 s in all
```

`ctl seq "R1.2 J R0.4+J G1 W0.5 L0.3"` = run right 72 steps; a jump tap (6); run right 24 steps with a jump tap at its
start; grab 60; wait 30; run left 18: 210 steps, frames `R:72 J:5 -:1 R+J:5 R:19 G:60 -:30 L:18`. A jump still pressed at
the end of one segment and pressed again at the start of the next is one press: the reply warns (`put W0.1 before it`).

### 18.4 Jobs and lanes

- Every ctl command that injects input or runs the clock takes the MCP control lane through `HostJobs` with its own
  `CallInfo` (tool `ctl`). While an MCP action runs, ctl answers `busy: <tool> is running (job jN)`; while a ctl job
  runs, MCP actions get the usual busy result naming `ctl` (`wait_until {job}` works on it). Instant commands take the
  lane for the moment of the change. Read-only commands (`status`, `job`, `track list / info / status`) take none. A
  MultiCam capture makes both busy.
- Timed commands start a detached job and answer at once: `started: <what> job=jN; ctl job jN shows it, ctl stop cancels
  it`. A worker thread runs it: `CtlGame.Prepare` on the main thread (readiness, AgentCore, clock, frames), then
  `Stepper.Run` with death / load stops. Tool-backed ones (goto, time step, track play) call the Play tool's handler with
  the job's own context, so the tool's `BeginJob` gets the same job. When a job ends, the console log gets
  `ctl jN <end line>`. `ctl stop`, MCP `restore`, `jobs {op:"cancel"}` and the host's 10-minute limit for detached jobs
  cancel it.

### 18.5 Replies

Two lines, the result and the body line:

```
done: run right for 72 steps (1.2 s)
body: boy b1 x=412.31 y=12.08 vx=3.5 vy=0 state=Run grounded grab=- stick_forced=no | clock=step frozen | input=agent held=stick:1,0+action
```

The first word is the outcome: `ok:`, `started:`, `running:`, `done:`, `died:`, `loading:`, `cancelled:`, `stalled:`,
`timeout:`, `not_reached:`, a move_to outcome (`arrived:`, `blocked:` ...), or an error that the MCP command tool reports
as isError: `busy:`, `usage:`, `unknown ...`, `refused:`, `not found:`, `failed:`. The body line has kind, id, x, y, vx, vy,
state, grounded / airborne, in_water, grab (the huddle's grab state, else what the boy holds), stick_forced and
forced_by, DEAD / not_controllable, the clock and the input (mode, held, queued steps). `help` and `status json` have no
body line.

### 18.6 Checks (offline, 2026-10-25)

The CTL suite (offline, not shipped): CtlTest 30 / 30 (29 after the audit fixes: the console echo test went with
CtlConsole), CtlGameTest 8 / 8, cli_test 19 / 19, the build with 0 errors and 0 warnings. Every other suite passes as
before (ARCH, SERVER, HOST, INPUT, TIME, OBSERVE, NAV, WORLD_A, WORLD_B, GUIDE, the STUBS and LIVE snapshots; TRACK and
INTEG against the same build). Role snapshots skip `Ctl*.cs` (no role pattern matches it and no role file references
it), so no stub is needed. Nothing here had run in the game at that point: in-game test T16.

## 19. Plain HTTP endpoints, CLI and install (2026-10-25)

### 19.1 Endpoints (class `ServerPlain` in `src/mcp/McpServer.cs`)

The same listener (127.0.0.1 only, port 47811 or EditorState `mcp.port`), the same Origin check and the same bearer
token rule as the MCP paths (EditorState `mcp.token`; `/ping` needs none). No session, no JSON-RPC.

| Request | Answer |
|---|---|
| `POST /cmd`, text body = one console / bridge command | runs on the main thread through the host's `command` tool (allowlist `mcp.tools`, the tool's refusals: keyhold, focustest, remote off, mcp off / port, secret objects; an audit line; 55 s budget); `200 text/plain` with the command's text. A failed command still answers 200, with header `X-InsideDev-Error: <code>` (body: its text, or `error <code>: <message>` and `hint:`). 400 empty body or more than one line, 413 over 8192 characters, 403 not in the allowlist, 405 another method, 401 without the token |
| `GET /state` | the concise observe JSON (the observe tool with its defaults); 503 with the error JSON when observe fails |
| `GET /ping` | `pong InsideDev <version>`; version = `IToolHost.ServerVersion` = `<mcp version>+mcp.<id>.play.<id>` (section 21) |

`curl -s -X POST --data-binary "ctl run right 1.2" http://127.0.0.1:47811/cmd`, `curl -s http://127.0.0.1:47811/state`;
with a token add `-H "Authorization: Bearer <token>"`. `/health` and the 404 text name these endpoints.

### 19.2 tools/inside_ctl.py (Python 3 stdlib)

```
python -I tools/inside_ctl.py ctl status
python -I tools/inside_ctl.py ctl run right 1.2            # follows the job to "done: ..."
python -I tools/inside_ctl.py --no-wait ctl goto 412.5     # prints "started: ... job=jN" only
python -I tools/inside_ctl.py seq "R1.2 J R0.4+J G1 W0.5 L0.3"
python -I tools/inside_ctl.py state                        # the observe JSON
python -I tools/inside_ctl.py repl                         # inside> ctl hold right / ctl release / quit
python -I tools/inside_ctl.py cmd state                    # the core's own state command
```

Options go before the command: `--port` (env `INSIDE_MCP_PORT`), `--host`, `--token` (env `INSIDEDEV_MCP_TOKEN`),
`--bridge DIR` (env `INSIDE_BRIDGE`; set it to `<game>/_mod/cmd`), `--http-only`,
`--bridge-only`, `--no-wait`, `--timeout S` (job follow, 120). When nothing listens on the port (or the game's Mcp.dll has
no `/cmd`) it writes `in_<id>.txt` into the bridge folder (a temp name, then a rename) and reads its block from
`out.txt`. A failure after the request was sent is never retried through the bridge (the command may have run). Ctrl+C
while a job is followed sends `ctl stop`. Exit 0 ok, 1 the command failed, 2 the game is not reachable.

### 19.3 Install

During development an install script (not shipped) copied the core DLL, `InsideDev.Mcp.dll`, `InsideDev.Play.dll`,
`InsideDev.McpStdio.exe` and `guide/` into a test copy's `_mod`, only with the game closed and never into the Steam
folder, and backed replaced files up first. The release installs by copying files: `bin/InsideDev.dll`,
`bin/InsideDev.Mcp.dll` and `bin/InsideDev.Play.dll` into `<game>/_mod/` and `guide/` into `<game>/_mod/guide/` (the
default guide root is the `guide` folder next to `InsideDev.Play.dll`); the stdio exe can stay in `tools/mcp/` (README,
`docs/MCP.md`, `docs/PLAY.md`).

## 20. MCP client config (ready to paste)

The game must be running with InsideDev.Mcp.dll (`mcp status` in the console). Use `/mcp/play` for an AI that plays and
`/mcp/dev` for debugging and world work. `/mcp/all` has 40 tools, Cursor's cap: prefer play or dev there. A client that
DELETEs its session restores the game 60 s after the last session ends (EditorState `mcp.restore_on_close_s`).

Claude Code, the project's `.mcp.json` (HTTP):

```json
{
  "mcpServers": {
    "inside-play": { "type": "http", "url": "http://127.0.0.1:47811/mcp/play" },
    "inside-dev": { "type": "http", "url": "http://127.0.0.1:47811/mcp/dev" }
  }
}
```

With EditorState `mcp.token` set, add `"headers": { "Authorization": "Bearer ${INSIDEDEV_MCP_TOKEN}" }` to each entry.
From the command line: `claude mcp add --transport http inside-play http://127.0.0.1:47811/mcp/play` and
`claude mcp add --transport http inside-dev http://127.0.0.1:47811/mcp/dev`.

Claude Code, the stdio proxy (it answers `inside_status` while the game is down and forwards once it is up):

```json
{
  "mcpServers": {
    "inside-play-stdio": {
      "type": "stdio",
      "command": "<repo>/tools/mcp/InsideDev.McpStdio.exe",
      "args": ["--profile", "play"],
      "env": { "INSIDEDEV_MCP_TOKEN": "" }
    }
  }
}
```

Proxy options: `--profile dev` (the default) or `all`, `--port N`, `--url <full url>`, `--token T`. From the command
line: `claude mcp add inside-play-stdio -- <repo>/tools/mcp/InsideDev.McpStdio.exe --profile play`.

Cursor, `~/.cursor/mcp.json` or the project's `.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "inside-play": { "url": "http://127.0.0.1:47811/mcp/play" },
    "inside-dev-stdio": {
      "command": "<repo>/tools/mcp/InsideDev.McpStdio.exe",
      "args": ["--profile", "dev"]
    }
  }
}
```

With a token, Cursor takes `"headers": { "Authorization": "Bearer <token>" }` on the URL entry and
`"env": { "INSIDEDEV_MCP_TOKEN": "<token>" }` on the stdio entry. `<repo>` is the folder of this repository; the exe can also be copied anywhere.

## 21. Audit fixes (2026-10-25)

Source: an internal audit of 2026-10-25 (not shipped); every finding was checked against the decompiled game before it
was changed. Saves are out of scope (`play.save_guard` stays off by default; audit item M2 is not done). Offline only:
nothing here had run in the game at that point (in-game tests T0, T2b, T2c, T9b, T10b-d, T17-T27). serverInfo version of
that build: `0.6.0+mcp.<id>.play.<id>` (versions row below).

Contract changes (tool schemas through this document and the contract test; outputs are open):

| Area | Change |
|---|---|
| observe objects | One `Device` scan plus `DeviceKinds.ScanTypes` (PerceptionKinds.cs): every lever class (LeverAnalog, LeverAnalogWater, LeverOneTime, LeverElevatorStuck, SuctionLever, SwitchTallRod, ChickenGunSwitch) is `lever`; Hatch / VentHatch / UnderwaterHatch `hatch` with grab, pull; PullOffBoards / BoardBreak / DoorBoarded / AlbinoMultiPullBoards `boards`; OpenElevatorDoors(2) `lift_door` with lift; MinesGate, SuperPillar, WindowInteractive, TorchPickup, UnderwaterCoil, UnderwaterLock, RopeLadder, SuperLadder, WaterStreamGrabHook, HuddleOvenGrab, PushCoverLogic `device`. New object kinds `boards`, `lift_door`, `device`. A device's position is the nearest point of its grab trigger. Secret devices (SecretPod*, ...) are never listed. `button` offers button, a free hat hat_wear, an empty submarine sub_enter, a huddle holding something throw. The cut to max_objects keeps actionable rows first (ObjectPick); `truncated` counts the rest. |
| observe hazards | KillerKinds: Dart (sentry), ChaserGuardCargoHall (guard), CapsuleRunOverLogic, GateAvoid, ForcePushMovingCover (crusher; the cover music-timed), WaterFlushGateKill, BoyExplosion (kill_zone), ZombieMonitorCameraGameplay (sentry, music-timed), FarmPigFight (other); active PlayMaker FSMs with PD_KillBoy or RopeGunShootAndKillMainCharacter (kind from the name). Death causes name them. |
| observe body / meta | `body.grip_held`; the observe tool result has `meta {play, mcp, core}` (not inside action results); warnings `focus_lost` / `focus_recovered`, `types_missing`. |
| act, move_to, interact | `when` (one wait_until condition: seconds, grounded, airborne, state, near, far_from, hazard_safe, hazard_eta, music_time, fsm_state, signal, object_active) and `when_timeout_s` (15 s, 0.1..45): the game runs (or is watched in realtime) until it holds, then the action starts in the same call; the wait never eats the time the action needs (Waits.When, WaitPlan.WhenBudgetMs). Result block `when {condition, satisfied, detail, waited_steps, elapsed_ms}`; not met -> outcome timeout (died / loading / cancelled when that ended it), no input. |
| wait_until | New condition keys `hazard_eta {target, min_s}` and `music_time {from, to, cycle}` (21 keys). |
| grab grip | `AgentCore.Grip` / `SetGrip` / `ReleaseKeepGrip` (public, stubs updated). interact grab sets it; act, move_to, push, pull keep it (action pressed every step; `held.grip`, warning `grip_held`); release:true, interact release, input_mode human, cancel, death, respawn and loads clear it; other verbs let go first (`grip_released`); throw drops it atomically with its first phase. |
| interact | Verbs + `button`, `lift`, `sub_enter`, `hat_wear`, `throw` (24). push / pull on a grab device (`InteractMacros.DeviceAxis`) grab it and move the stick along its axis until a done state. The turn phase follows BoyInput's extra deadzone (BoyStick, read from `BoyInput.GetExtraDeadZone`): the 0.4 walk stick runs until the game lets it register, then 6 steps. `allow_direct` (false): only then may hat_detach fall back to MindHat.ForceDetach; the result lists it in `direct_calls`. |
| geometry | Tagged play + dev (play 14 tools, all still 40). |
| time | Step-mode runs near music-timed hazards and input_track playback are paced to real time (`Stepper.HoldLockstep`, `FramePacer`); `time` state `pacing`; warning `paced: ...`. FocusGuard: once the clock was touched, a Focus layer a focus loss switched off is switched back on (with runInBackground); `Stepper.FocusState` for observe. |
| input_track | `correct` default false (input only; drift measured; `correction_on` warning when true); playback at the rehearsal's lockstep. ctl: `track play ... correct`. |
| versions | `McpBuild` (McpServer.cs): version 0.6.0, build id = first 8 hex of the assembly's SHA-256. `ServerVersion` (serverInfo, /health, /ping) = `0.6.0+mcp.<id>.play.<id>`; `mcp status` has a build line with the core's Boot.Version (unchanged, 0.5.0); `inside_status` adds `proxy_version` = `0.6.0+stdio.<id>`; `PlayShared.BuildVersion` 0.6.0. In release 0.7.0 all of these versions, Boot.Version included, are 0.7.0. |
| console | Core change (src/InsideDev/DevRoot.cs `Commands.Run(line, root, extensions)`, Remote.cs passes false): unknown console words go to the extensions; `CtlConsole.cs` and the echo detection removed. Metadata diff against the core before the audit: only Commands.Run/2 (now a forward), Commands.Run/3, Commands.Words and Remote.Run (one IL byte) differ; the replay, srec and spec offline tests (377, 16 + 20, 39) pass as before. |

Findings rejected or narrowed after reading the game code:
- KillBoyWithVoiceEmotion only sets the death voice (BoyAudio.KillBoyWithEmotion): 16 of the 31 FSMs are not killers;
  the 15 with PD_KillBoy / RopeGunShootAndKillMainCharacter are hazards now.
- ChickenGunLogic kills chickens (Chicken.Kill), never the boy: not a hazard.
- OpenElevatorDoors / OpenElevatorDoors2 call Boy.Kill only when the boy is already dead: dropping the door does not
  kill; they are lift_door devices, not hazards.
- SwitchButtonNew is a hand button (a rising action press in reach, SwitchButtonNew.cs:773), not a step-on plate: the
  `button` verb presses it; floor plates (TriggerCount, MindHatReceiverPressurePlate) stay move_to / push targets.
- The turn rule: a full stick (|stick|^2 > 0.7) sets the extra deadzone to its own side at once, so only weak sticks
  are hidden; float arithmetic (0.6 - 10 x 0.05) lets a 0.4 stick register after 10 steps.

Tests added: InputLogicTest (grip, BoyStick, the new verbs and device macros, not-available rules), InputGameTest
(when, allow_direct, verb list), ObserveCoreTest (DeviceKinds, KillerKinds, ObjectPick), ObserveApiTest (every table
class exists in Assembly-CSharp, every unlisted Device subclass is a secret, a hazard or unplaced), TimeTest
(music_time phase and window, the when budget, FramePacer), TimeGameTest (WhenSchema keys), NavTest (move_to when,
geometry on play), ServerTest (build ids), StdioTest (proxy version), TrackGameTest (correct off), CtlTest (track play
correct), CtlGameTest (the console path without log watching), IntegrationTest (14 play tools, server version with the
build ids).

## 22. Night run fixes (2026-10-26, first in-game run of T10 / T15 / T16)

Only Play.dll was rebuilt (Mcp.dll and the stdio exe unchanged from section 21). The TRACK, CTL and TIME suites and the
INTEG parts probe / roles / e2e / compat / driver pass against it.

| Area | Change |
|---|---|
| input_track play | `Finish` sets `ended` before it builds the result (the observe takes time), so a waiting play could stop on `ended` and answer `outcome: playing` with step n/n and the job still running. `WaitEnd` now waits for the result too (`ActTrack.cs`). |
| pacing note | `RestorePacing` wrote the "paced: music-timed place" note for any pacing, so the next ctl / act run after a playback reported the playback's frames as its own music-timed pacing. A playback's end clears the note (`Stepper.HoldLockstep`). |

In-game facts (huddle, 73:0): the huddle has no jump (`input.jump` only leans it forward, HuddleRunState); the railing crash
forces the stick for 180 steps (forced_spans [117, 296] from a standing start); input-only takes (`correct:false`) drift
0.7 to 2.7 m at most (the railing fall), `correct:true` 0.42 m; a lockstep take runs at about 36 fps on the test machine
(0.6x real time; game time per step is exact). `ctl stopat` lets the huddle coast about 3 m past x at 5 m/s: use `ctl goto`.
