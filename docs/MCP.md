# MCP server (InsideDev.Mcp.dll)

`InsideDev.Mcp.dll` is a [Model Context Protocol](https://modelcontextprotocol.io) server that runs inside the game, so an
AI assistant or any MCP client can look at and change the live game. It speaks Streamable HTTP on
`http://127.0.0.1:47811` (this computer only) and serves three tool sets (profiles):

| Profile | Endpoint | Tools | For |
|---|---|---|---|
| dev | `/mcp` (the 0.5 / 0.6 path), `/mcp/dev` | 33 | inspecting and editing the world: objects, FSMs, signals, loads, saves, camera, hazards, audio, render, reflection |
| play | `/mcp/play` | 14 | playing the game: see [PLAY.md](PLAY.md) |
| all | `/mcp/all` | 40 | both (40 is the tool cap of Cursor) |

The server alone provides the 13 editor tools of earlier versions plus `screenshot` and `jobs` (15 tools on dev). The
world and play tools come from `InsideDev.Play.dll` (section 1). This document covers the server, the dev profile and
everything shared; [PLAY.md](PLAY.md) covers character control and the play tools in full.

Contents

1. Install
2. On, off and defaults
3. Settings
4. Endpoints and profiles
5. Connecting a client (stdio program, client files)
6. Protocol details
7. Results and errors
8. Jobs, the control lane, progress and cancel
9. Restore
10. Dev tool reference
11. Resources and prompts
12. Plain HTTP endpoints
13. Console commands
14. Files the server writes
15. Pitfalls
16. Troubleshooting

---

## 1. Install

InsideDev itself must be installed first (`version.dll` next to `INSIDE.exe`, `InsideDev.dll` in `<game>\_mod\`, see
the README). `<game>` is the INSIDE folder.

| File in the release | Copy to | What it is |
|---|---|---|
| `bin\InsideDev.Mcp.dll` | `<game>\_mod\` | the MCP server (optional) |
| `bin\InsideDev.Play.dll` | `<game>\_mod\` | optional: play and world tools; needs `InsideDev.Mcp.dll` |
| `guide\` | `<game>\_mod\guide\` | optional: playbooks for the `guide` tool, example scenarios for `run_scenario` |
| `tools\mcp\InsideDev.McpStdio.exe` | anywhere | optional: stdio launcher for clients that start a local program (needs .NET Framework 4.x, part of Windows) |
| `tools\mcp\clients\` | (reference) | example client configurations |
| `tools\inside_ctl.py` | anywhere | optional: command-line control through `POST /cmd` (see PLAY.md section 12) |

Extensions load at game start from `<game>\_mod\InsideDev.*.dll` in name order (Mcp before Play). With the game
running, copy the files and run the console command `ext load`: it loads DLL files that are not loaded yet. A DLL that is
already loaded is replaced only by a game restart. Other `InsideDev.*.dll` files that implement the provider interface
are picked up too (their tools join the lists, `list_changed` tells clients).

With Play loaded, `mcp` in the console reports `tools: play 14, dev 33, all 40`. Without Play: `play 1, dev 15, all 15`
(only `screenshot` is shared).

## 2. On, off and defaults

| Item | Default | Effect | Switch |
|---|---|---|---|
| server | on (`mcp.enabled` = 1) | listens on `127.0.0.1:47811`; never on the network | console `mcp off` / `mcp on` (saved in editor.cfg); or remove the DLL |
| token | none | any local program can call the server | `mcp.token` in editor.cfg |
| tool allowlist | empty = every tool | | `mcp.tools` in editor.cfg |
| idle restore | 600 s | after 600 s without a tool call the game is put back (section 9) | `mcp.idle_restore_s` |
| restore on close | 60 s | 60 s after the last session closed (DELETE) the game is put back | `mcp.restore_on_close_s` |
| save guard | off | no save backups, confirms or refusals because of the save | `play.save_guard` |
| cheats | off | `checkpoint next / prev / goto` refused | `play.cheats` |

While no client calls a tool, the server changes nothing in the game. The restore timers only act after a tool call. The
same listener also serves the plain endpoints `POST /cmd`, `GET /state`, `GET /ping` (section 12), under the same token
rule. The file bridge (`_mod\cmd`) is part of the core and works with or without this DLL.

`InsideDev.Play.dll` adds one default that acts without a client: the notes hotkey (Home; `play.notes_key=none` switches it off), see PLAY.md section 2.

## 3. Settings

Settings are `key=value` lines in `<game>\_mod\editor.cfg` (`#` starts a comment line). The game reads the file once at
start and rewrites all of it whenever an editor setting changes, so edit it with the game closed. Only `mcp.enabled` and
`mcp.port` have console commands. There is no MCP tool that changes these settings (`reflect` refuses `EditorState`).

| Key | Default | Effect | Change | Read in |
|---|---|---|---|---|
| `mcp.enabled` | `1` | `0` = the server does not start | console `mcp on` / `mcp off` | src/mcp/Extension.cs:100 |
| `mcp.port` | `47811` | listening port on 127.0.0.1 | console `mcp port <1024-65535>` (restarts the listener) | Extension.cs:123 |
| `mcp.token` | empty | when set: every MCP request and `POST /cmd`, `GET /state` need `Authorization: Bearer <token>` (HTTP 401 otherwise) | editor.cfg | Extension.cs:107 |
| `mcp.tools` | empty | allowlist, see below | editor.cfg | Extension.cs:106 |
| `mcp.idle_restore_s` | `600` | restore after this many seconds without a tool call; 0 = never | editor.cfg | Extension.cs:109 |
| `mcp.restore_on_close_s` | `60` | restore this many seconds after the last session closed; 0 = never on close | editor.cfg | Extension.cs:112 |
| `play.save_guard` | off | `1`: save backups, `confirm:true` for save-writing loads and edits, refusal of console loads through MCP | editor.cfg | Extension.cs:114, src/play/Saves.cs:262 |
| `play.cheats` | off | `1`: `checkpoint next / prev / goto` | editor.cfg | src/play/PlayLevels.cs:1015 |
| `play.guide_dir` | `<game>\_mod\guide` | playbook folder (environment `INSIDEDEV_GUIDE_DIR` wins) | editor.cfg | src/play/Guide.cs:257 |
| `play.guide_max` | `solution` | spoiler cap of `guide`: `hint`, `steps`, `solution` | editor.cfg | Guide.cs:255 |
| `play.notes_key` | `Home` | notes hotkey (Unity KeyCode name; `none` or `off` = no hotkey) | editor.cfg | src/play/Notes.cs:270 |

Allowlist (`mcp.tools`): entries separated by commas, semicolons, spaces or tabs, case-insensitive:

| Entry | Allows |
|---|---|
| `*` | everything |
| `<tool>` | that tool on every profile (`observe`) |
| `<profile>:*` | every tool of that endpoint (`play:*`, `dev:*`, `all:*`) |
| `<profile>:<tool>` | one tool on one endpoint (`dev:inspect`) |

A tool that is not allowed is left out of `tools/list`; a call to it gets JSON-RPC -32602 ("it is disabled by the game's
tool allowlist"). The allowlist comes from editor.cfg at game start. The profile is the endpoint's: tools on
`/mcp/all` need `all:...`. `POST /cmd` needs `command` allowed on dev, `GET /state` needs `observe` allowed on dev.

Environment variables: `INSIDEDEV_MCP_TOKEN` (stdio program and inside_ctl.py), `INSIDEDEV_GUIDE_DIR` (game), and for
inside_ctl.py `INSIDE_MCP_PORT`, `INSIDE_BRIDGE`, `INSIDE_GAME`.

Built-in limits (not settings): tool budget 55 s plus 2.5 s grace; at most 128 sessions, a session unused for 6 h is
dropped; finished jobs are kept 10 minutes (the last 50); detached jobs are cancelled after 10 minutes; the newest 200
screenshots are kept; `_mod\mcp_audit.jsonl` rotates at 5 MB.

## 4. Endpoints and profiles

| Path | Method | Answer |
|---|---|---|
| `/mcp`, `/mcp/dev` | POST, GET (SSE), DELETE | MCP, dev profile |
| `/mcp/play` | POST, GET, DELETE | MCP, play profile |
| `/mcp/all` | POST, GET, DELETE | MCP, all tools |
| `/cmd` | POST | one console command, text reply (section 12) |
| `/state` | GET | the concise observe JSON (needs Play) |
| `/ping` | GET | `pong InsideDev <version>` (no token needed) |
| `/`, `/health` | GET | plain text: server version and the endpoint list (no token needed) |
| anything else | | 404 with the endpoint list |

`tools/list` depends only on the path and the allowlist, never on the session. A tool that exists on another profile is
answered with JSON-RPC -32602 and the endpoint that serves it.

| Tool | dev | play | Owner |
|---|---|---|---|
| `game_state`, `find_objects`, `inspect`, `select`, `set_active`, `fsm_graph`, `fsm_find`, `fsm`, `signal`, `show_panel`, `log`, `command`, `jobs` | yes | | server |
| `screenshot` | yes | yes | server |
| `observe`, `time`, `wait_until`, `restore`, `geometry`, `input_track` | yes | yes | Play |
| `act`, `move_to`, `interact`, `checkpoint`, `menu`, `guide`, `notes` | | yes | Play |
| `level`, `stream`, `ui`, `save`, `run_scenario`, `camera`, `logic`, `body`, `hazard`, `audio`, `render`, `reflect`, `state_diff` | yes | | Play |

The dev profile cannot move the body through `act`, `move_to` or `interact`; it can through `command` with `ctl ...`
(PLAY.md section 11), `run_scenario` and `input_track`.

## 5. Connecting a client

### 5.1 HTTP

Point the client at `http://127.0.0.1:47811/mcp/play`, `/mcp/dev` or `/mcp/all` (transport "http" or "Streamable
HTTP"). With a token, add the header `Authorization: Bearer <token>`.

```
claude mcp add --transport http inside-dev http://127.0.0.1:47811/mcp/dev
claude mcp add --transport http inside-play http://127.0.0.1:47811/mcp/play
```

### 5.2 stdio: InsideDev.McpStdio.exe

For clients that start a local program. It relays every message to the game and can be started before the game.

```
InsideDev.McpStdio.exe [--profile play|dev|all] [--port 47811] [--url <endpoint>] [--token <token>]
```

| Option | Default | Meaning |
|---|---|---|
| `--profile` | `dev` | picks `/mcp` (dev), `/mcp/play` or `/mcp/all` |
| `--port` | 47811 | 1..65535 |
| `--url` | | full endpoint URL; replaces `--port` and the profile path |
| `--token` | env `INSIDEDEV_MCP_TOKEN` | bearer token |
| `--help`, `-h`, `/?` | | usage on stderr |

An unknown argument prints the usage and exits with code 2.

Behaviour:

- `initialize`: when the game is up, the game's capabilities, server version and instructions; when it is down, short
  own instructions and a tool list with only `inside_status`.
- `inside_status` (read-only, no arguments) returns `{running, endpoint, profile, version, proxy_version, instructions,
  text}`. It works while the game is up too: a client that initialized before the game started gets the game's
  instructions this way.
- Every 3 s it checks the game and sends `notifications/tools/list_changed` (and the resources / prompts ones) when the
  game comes up or goes away; while the game is up it holds the game's notification stream and relays its
  `list_changed` notifications.
- Requests with a progress token are relayed as they stream (each SSE event becomes one stdout line);
  `notifications/cancelled` is forwarded.
- After a game restart it opens a new game session by itself (on HTTP 404 for the old one).
- HTTP timeout 90 s (the game's tool budget is 55 s). Batches are not supported over stdio.
- When its input ends (the client exits) it closes the game session with DELETE, which starts the restore-on-close timer.
- While the game is down, `tools/call` answers `not_ready`; a 401 from the game names the token.
- Version string: `0.7.0+stdio.<id>` (`<id>` = first 8 hex digits of the exe's SHA-256).

### 5.3 Client files

`tools\mcp\clients\` has ready examples: Claude Code (`claude mcp add` lines and `.mcp.json`), Claude Desktop
(`claude_desktop_config.json`, stdio only), Cursor (`mcp.json`), VS Code (`mcp.json`), Codex (`config.toml`) and Gemini
CLI (`settings.json`). Replace `C:\path\to\InsideDevTool` with the folder of the release.

Client notes:

- Claude Code passes only `structuredContent` to the model when a result has it. Tools that return an image without an
  output schema (`screenshot`) are sent as content only, so the image arrives; `observe {image}` keeps its
  structuredContent and its image is dropped there: use `screenshot`.
- The loop tools (`observe`, `act`, `move_to`, `interact`, `wait_until`) carry `_meta["anthropic/alwaysLoad"]`, so
  Claude Code does not defer them.
- The instructions text is capped at 4000 characters.

## 6. Protocol details

- Protocol versions 2025-11-25 (newest), 2025-06-18, 2025-03-26, 2024-11-05. A known requested version is echoed; an
  unknown one gets the newest. An unknown `MCP-Protocol-Version` header gets HTTP 400 (not checked on `initialize` and
  `server/discover`).
- Capabilities: `tools`, `resources`, `prompts`, each with `listChanged: true`. `serverInfo`: name `insidedev`, title
  `InsideDev (INSIDE runtime editor)`, version `0.7.0+mcp.<id>[.play.<id>]` (build ids = first 8 hex digits of each
  DLL's SHA-256). `instructions` per profile.
- Sessions: `initialize` returns `Mcp-Session-Id`; later requests send it. An unknown or expired session gets 404
  (initialize again). DELETE with the session id ends the session, its stream and its running calls.
- Methods: `initialize`, `ping`, `tools/list`, `tools/call`, `resources/list`, `resources/templates/list`,
  `resources/read`, `prompts/list`, `prompts/get`; notifications `notifications/initialized`,
  `notifications/cancelled`.
- Batches (JSON arrays) are accepted over HTTP; no streaming inside a batch.
- Notification stream: GET on the MCP path with `Accept: text/event-stream` and the session id. It sends
  `notifications/tools/list_changed`, `notifications/resources/list_changed` and `notifications/prompts/list_changed`
  when providers or the allowlist change, and a keep-alive comment every 15 s. A new GET replaces the old stream.
- Progress: a `tools/call` with `params._meta.progressToken` and an `Accept` header that includes `text/event-stream`
  is answered as SSE: `notifications/progress {progressToken, progress, total, message}` (at most 4 per second, the last
  one always sent), then the response, then the connection closes. Without a token: one `application/json` reply.
- Cancel: `notifications/cancelled {requestId, reason}` cancels that call of the session; a cancel that arrives before
  its request is kept 10 s. The response is still sent.
- Arguments are validated strictly against the tool's `inputSchema` (types, enums, ranges, array sizes, string length,
  required keys, `additionalProperties: false` at every level); defaults are filled in. A string where a number,
  boolean, array or object is expected is parsed when possible and reported as `coerced: ...` in `warnings`.
- Output: a result that does not match its `outputSchema` loses the offending properties (named in `warnings` as
  `schema_repair: ...`), so strict clients do not reject it.
- Security: listens on 127.0.0.1 only; a request with an `Origin` header that is not localhost / 127.0.0.1 / ::1 gets
  403 (browser pages from other sites cannot call it); the token, when set, is compared in constant time.

## 7. Results and errors

### 7.1 Envelope

`tools/call` returns `{content, structuredContent, isError}`. `structuredContent` has `ok` first; `content[0]` is the
same JSON as text (cut at 100000 characters), followed by image or resource_link blocks. `isError` is true exactly when
`ok` is false.

```json
{"ok": false, "error": {"code": "busy", "message": "busy: move_to is running (job j12)", "hint": "wait for it with wait_until {any:[{job:\"j12\"}]}, or cancel the running call"},
 "busy": {"job": "j12", "tool": "move_to", "lane": "control", "running_s": 3.4}}
```

The text tools of the server (`game_state`, `find_objects`, `inspect`, `select`, `set_active`, `fsm_graph`, `fsm_find`,
`fsm`, `signal`, `show_panel`, `log`, `command`) return `{ok, text, truncated, total_chars, offset, next_offset}`.
`isError` comes from the first line of the game's reply: `not found`, `usage`, `unknown`, `busy`, `no FSM`,
`component not found`, `field/property not found`, `no parameterless method`, `no active character`, `ERROR in`,
`failed`, `cannot`, `could not`, `refused`, `not built`.

### 7.2 Error codes (`error.code`)

| Code | When |
|---|---|
| `bad_args` | arguments do not match the schema (message names the path) |
| `busy` | the control lane is held by another job, a ctl job or a MultiCam capture (`busy {job, tool, lane, running_s}`) |
| `not_ready` | the main thread did not run the request in time (nothing ran later), the game is not playable, or (stdio) the game is down |
| `stale_id` | an observe id from before the last load |
| `not_found` | no such object, topic, job, track ... |
| `ambiguous` | a name or path matches several objects (`candidates`) |
| `refused` | a safety rule or the allowlist |
| `confirm_required` | a save-writing op without `confirm:true` (only with `play.save_guard` on, plus orb edits and the secret ending always) |
| `timeout` | the call ran past its budget; with `job` it continues as that job |
| `cancelled` | cancelled by the client, `jobs cancel` or restore |
| `game_error` | the game threw (type and message; the stack is in the log) |
| `not_built` | a part of the build is missing |
| `internal` | a bug in InsideDev |
| `unauthorized` | reserved |

### 7.3 JSON-RPC and HTTP codes

| JSON-RPC | Meaning |
|---|---|
| -32700 | parse error (HTTP 400) |
| -32600 | invalid request, empty batch, unsupported protocol version (HTTP 400) |
| -32601 | method not found |
| -32602 | unknown tool (the message names the endpoint that serves it, or the allowlist), unknown prompt, missing `params.name` / `uri` |
| -32603 | internal error |
| -32001 | unauthorized (HTTP 401 with `WWW-Authenticate: Bearer realm="insidedev"`) |
| -32002 | resource not found |
| -32000 | stdio program: the game is down, or it closed the stream before answering |

| HTTP | Meaning |
|---|---|
| 200 | JSON reply, or an SSE stream |
| 202 | a notification or response was accepted |
| 400 | bad request line, parse error, unsupported version, DELETE / GET stream without a session id |
| 401 | token missing or wrong |
| 403 | non-local `Origin`; `/cmd` or `/state` tool not in the allowlist |
| 404 | unknown or expired session, unknown path |
| 405 | wrong method (GET without `Accept: text/event-stream`) |
| 413 | `/cmd` body over 8192 characters |
| 500 | the server could not read its token |
| 503 | `/state` when observe fails or Play is not loaded; `/cmd` when the command tool is missing |

## 8. Jobs, the control lane, progress and cancel

- Lane `control`: anything that advances game time, injects input, loads or holds the camera over several frames takes
  it. One job at a time; a second request gets `busy` at once. Read-only tools never take it and may run during an
  action. A MultiCam capture (a core feature) holds the lane as job `mcam`; `restore` does not cancel it, the console
  command `mcam stop` does. Console `ctl` jobs take the same lane (tool `ctl`).
- Not every changing tool takes the lane: `command`, `set_active`, `fsm`, `signal` and the instant world ops run at once,
  so they can change time or start a load while a play action runs.
- Job ids: `j1`, `j2` ... per game session. A tool call that runs past its budget while holding a job returns `timeout`
  with `job`, and the job keeps running (loads still fading in, a track playback). Finished jobs are kept 10 minutes
  (the last 50).
- Waiting for a job: `wait_until {any:[{job:"j7"}], timeout_s:50}` (both profiles; watches without the lane) or
  `jobs {op:"await", id:"j7", timeout_s}` (dev). Cancelling: `jobs {op:"cancel", id}` (dev), `restore`, or the call's
  own `notifications/cancelled`. A cancelled action releases its input and freezes the game again.
- Detached jobs running longer than 10 minutes are cancelled, and failed 5 s later if they do not end.
- Progress notifications (only when the request carried a progress token): calls that run the game report `step n/N`
  (`act`, `interact`, `wait_until`, `time step`, ctl jobs), `move_to` reports its x and goal, a watch-only wait reports
  elapsed ms, `input_track` play reports `step n/N`, `run_scenario` the step index, `jobs await` the time waited. Loads
  send none.

## 9. Restore

Restore cancels the running control job (not a MultiCam capture), then runs every provider's undo, newest first:
input released and the agent input core removed (the player has control), the clock back to realtime at speed 1 with
`captureFramerate`, vSync and `runInBackground` as before, input track recording and playback stopped (the track stays
in memory), camera overrides released, stream pins and freezes undone, god, air and hazard switches undone, menu lock,
title hide and overlay text undone, render and audio changes undone, reflect writes made with `restore_on_end`, editor
panels opened by `show_panel` closed. A save lock taken through MCP is kept unless `include_save_lock:true`.

| Trigger | When |
|---|---|
| `restore` tool | at once (also switches the core free camera off) |
| idle | `mcp.idle_restore_s` (600) seconds without any tool call, after at least one call since the last restore, while no call is in flight and no control job runs. `POST /cmd` and `GET /state` count as calls; console and bridge commands do not |
| last session closed | `mcp.restore_on_close_s` (60) seconds after the last session ended by DELETE, with still no session, no call in flight and no control job. A new session cancels it. Clients that never send DELETE leave this to the idle restore |
| console `mcp off`, `mcp restore` | at once |
| game quit | at once |

The `restore` tool parameters and keys are in PLAY.md section 6.15 and 13. Console `play restore` runs only the Play part.

## 10. Dev tool reference

The play-profile tools shared with dev (`observe`, `time`, `wait_until`, `restore`, `geometry`, `input_track`,
`screenshot`) are documented in [PLAY.md](PLAY.md) section 6. Annotations: readOnly tools change nothing; destructive
tools can change the game in ways that cannot be undone.

Objects are named by an observe id (`o12`, `h3`, `b1`), a hierarchy path (`A/B/C`), `#<instanceID>` or a unique name.
Ids resolve strictly: an old id gives `stale_id`.

### 10.1 Editor tools (server)

| Tool | Parameters (type, default, range) | Notes |
|---|---|---|
| `game_state` (readOnly) | none | text snapshot: InsideDev version, frame, boy position, savepoint, music position, active scenes, nearby triggers |
| `find_objects` (readOnly) | `query` string (required); `category` enum animation, sounds, scripts, triggers, fsm, characters; `limit` integer 25 (1..100) | ranked search over loaded objects; adds `count` |
| `inspect` (readOnly) | `object` (required); `deep` boolean false; `max_chars` integer 20000 (1000..200000); `offset` integer 0 | path, active state, components, fields, references, FSM dump; page with `offset = next_offset` |
| `select` (idempotent) | `object` (required) | selects in the editor (Inspector, Explorer, graph follow); no game change |
| `set_active` (idempotent) | `object`, `active` boolean (both required) | recorded in History (`command "undo"`); re-reads after 2 frames: `verdict` (confirmed, reverted_by_game, not_applied, unreadable), `before`, `after`, `after_k`, `active_in_hierarchy` |
| `fsm_graph` (readOnly) | `object` (required); `fsm` string; `open_panel` boolean false | states, exits with events, current state; `open_panel` also selects it and opens the FSM Graph panel |
| `fsm_find` (readOnly) | `text` (required); `limit` integer 30 (1..200) | searches every loaded PlayMaker FSM, including switched-off ones |
| `fsm` (destructive) | `object`, `action` enum event, state, var, `name` (required); `value` string; `fsm` string | send an event, force a state, read (no value) or set a variable; event and state cannot be undone |
| `signal` (destructive) | `object`, `output` (required) | fires a Playdead signal output (every connection, like the game); cannot be undone |
| `show_panel` (idempotent) | `panel` (required: spawns, levels, triggers, hidden, world, audio, inspector, cheats, console, diag, settings, objects, explorer, graph, events, sounds, atimeline, wwiseapi, history, audiodb, logic, modified, fsmgraph, fsmfind, or none); `dock` enum left, right, bottom; `size` number 0.1..0.7 | opens the editor; restore closes a panel it opened |
| `log` (readOnly) | `lines` integer 60 (1..400) | the last lines of the InsideDev log |
| `command` (destructive) | `command` string (required); `max_chars` integer 20000 (1000..200000) | any bridge / console command (`help` lists them); returns its reply plus the log lines it wrote |
| `jobs` | `op` enum list, status, await, cancel (default list); `id` string; `timeout_s` number 10 (0..50) | returns `jobs[]`, `job {id, tool, lane, state, elapsed_s, progress, total, message, error, detached, cancel_reason}`, `result`, `finished`, `hint`; `cancel` without id cancels the running control job |

`command` refuses: `keyhold` and `focustest` (OS input; use `act`), `remote off`, `mcp off`, `mcp port` (they would cut
the server from inside a call); lines that drive a secret / achievement object (`signal`, `signalin`, `fsm`, `call`,
`set`, `fsmset`, `sigwire` on FinalSecretFade, SecretPod*, Secret, SecretSuitcase, AchiementTrigger,
StartNewGameMenuItem, or a signal reaching one within 3 wires); with `play.save_guard` on also `load`, `spawn`,
`reload`, `respawn`, `cspawn go`, `cspawn respawn`. Examples:

```json
{"command": "near 10"}
{"command": "get \"Housing (4)\" Transform localPosition"}
{"command": "ctl run right 1.2"}
```

### 10.2 level (destructive)

| Parameter | Type | Default | Values |
|---|---|---|---|
| `op` | enum | `status` | status, list, wait_ready, load_checkpoint, load_chapter, reload, step |
| `subscene`, `savepoint` | integer | | 0..999 |
| `chapter` | integer | | 0..65 |
| `mode` | enum | `respawn` | respawn, quiet, chapter (op load_checkpoint) |
| `kind` | enum | `respawn` | respawn, chapter, newgame (op reload) |
| `dir` | enum | `next` | next, prev (op step) |
| `filter` | string | | op list |
| `limit` | integer | 100 | 1..300 |
| `offset` | integer | 0 | 0..1000 |
| `wait` | boolean | true | wait until ready |
| `timeout_s` | number | 45 | 1..50 |
| `settle_frames` | integer | 20 | 0..120, frames after ready |
| `confirm` | boolean | false | only with `play.save_guard` on |

- `respawn` writes the spawn into `INSIDE.sav`; `chapter`, `load_chapter`, `reload chapter / newgame` write the Steam
  Cloud save and unlock chapters for good; `quiet` locks the save and writes nothing (it sets the spawn in memory; the
  quiet mode is marked INFERRED in the code: unverified in game).
- `wait_ready` waits for the ready predicate (playing, input enabled, fader done, no title, streaming idle, no pending
  PreAwake, body active, then `settle_frames`).
- With `wait`, the tool waits for its own load. A load past `timeout_s` returns `outcome:"loading"` and `job`, and that
  job holds the control lane until ready.
- Returns `op`, `outcome` (done, loading, refused, timeout), `status {state, loading, playing, paused, fader,
  input_enabled, title, preawake_pending, cullers_busy, ready, reason, load_count, progression, unlocked, spawn,
  pending, press_start}`, `current`, `subscenes[]`, `chapters[]`, `total`, `job`, `backup`, `load_ms`, `warnings`.

### 10.3 stream (write)

Streaming follows the camera: a camera away from the body unloads its area after 4 s of game time.

| Parameter | Type | Default | Values |
|---|---|---|---|
| `op` | enum | `status` | status, pin, unpin, load, freeze, unfreeze |
| `subscene` | integer | -1 | -1..999 (-1 = the body's area) |
| `name` | string | | subscene name instead of the index |
| `activate` | boolean | true | op load: activate after loading |
| `filter` | string | | op status (`all` = every subscene) |

`pin` keeps a subscene loaded, `load` force-loads one (the game's own WaitForSubscene recipe), `freeze` stops all
streaming changes. All undone by restore. Returns `outcome`, `frozen`, `cullers[] {subscene, name, state, pinned,
visible}`, `pinned[]`, `body_subscene`, `warnings`.

### 10.4 ui (destructive)

| Parameter | Type | Default | Values |
|---|---|---|---|
| `op` (required) | enum | | menu_lock, title_hide, overlay_text, settings, message_answer, savepoint_menu |
| `on` | boolean | | menu_lock, title_hide |
| `text` | string | | at most 200 characters (overlay_text) |
| `seconds` | number | 3 | 0.5..30 |
| `volume` | number | | 0..1, only 0 is accepted (mute) |
| `brightness` | number | | 0..1 (written to INSIDE.sav) |
| `rumble`, `fullscreen` | boolean | | |
| `resolution` | integer | | 0..64, index into the resolution list |
| `answer` | enum | | ok, cancel (message_answer) |
| `confirm` | boolean | false | only with `play.save_guard` on |

`savepoint_menu` opens Playdead's dev savepoint picker (marked INFERRED in the code; it reads the physical controller).
Returns `outcome`, `state {menu_locked, title_hidden, settings, message}`, `warnings`.

### 10.5 save (destructive)

| Parameter | Type | Default | Values |
|---|---|---|---|
| `op` | enum | `info` | info, backup, lock, unlock, orbs, wrist_secret, ending, savepoints_near |
| `action` | enum | `read` | read, set, clear (op orbs) |
| `bits` | integer[] | | 0..14, at most 15 items |
| `layer` | enum | `both` | ever, recent, both |
| `on` | boolean | | op wrist_secret |
| `kind` | enum | | credits, secret, restart (op ending) |
| `radius` | number | 30 | 1..500 (op savepoints_near) |
| `confirm` | boolean | false | orb edits and ending secret always; with `play.save_guard` also ending restart and unlock after a cheat |

- `backup` copies the save to `_mod\save_backups\INSIDE_<date>_<time>.sav` (from the Steam userdata file, else Steam
  remote storage, else the in-memory bytes).
- `lock` / `unlock` stop and resume the game's save writes (marked INFERRED in the code: unverified in game); the lock is
  released when the game quits.
- `orbs` edits local bits 0..13 (orbs) and 14 (power secret) through the game's achievement progression; set bits reach
  Steam achievements at the next game start. Tools never call Steam achievements directly.
- `ending secret` replays the secret ending without its Steam call and clears the orb bits; `restart` = new game.
- Returns `outcome`, `info {spawn, progression, unlocked, brightness, rumble, hash, saving, locked, backup}`, `orbs {ever,
  recent}`, `backup {path, bytes, source}`, `savepoints[]`, `warnings`.

### 10.6 run_scenario (destructive)

A scripted test: steps run in order under one control-lane job through the agent input and the step clock.

| Parameter | Type | Default | Values |
|---|---|---|---|
| `op` | enum | `run` | run, check (validate only), list (scenario files) |
| `file` | string | | name in `_mod\scenarios\` or `<guide>\scenarios\` without `.json` |
| `steps` | array | | 1..200 inline steps (instead of `file`) |
| `start_at` | integer | 0 | 0..199, continue after a timeout |
| `stop_on_fail` | boolean | true | when absent, a file's own `stop_on_fail` applies |
| `observe` | enum | `concise` | concise, none |

| Step `do` | Fields |
|---|---|
| `load` | `mode` (respawn, quiet, chapter, reload_respawn, reload_chapter, newgame; default respawn), `subscene`, `savepoint` (0), `confirm` |
| `press` | `stick`, `jump`, `action`, `steps` (1..3600), `stop_on_death` (true) |
| `hold`, `release` | `stick`, `jump`, `action` (hold latches) |
| `wait` | `steps` or `seconds` (0.02..60), `stop_on_death` |
| `until` | `until` (one wait_until condition), `within_s` (10, game seconds, 0.1..50), `stop_on_death` |
| `expect` | `path` (body.state, body.x, screen, checkpoint.savepoint, objects.0.kind, hazards.length), `cmp` (eq, ne, lt, le, gt, ge, exists, missing, contains), `value`, `tol` (0.01) |
| `capture` | `shot` (preview, full, marked; default full); the PNG stays on disk |
| `command` | `cmd` (at most 300 characters); refused: keyhold, focustest, remote off, mcp off, mcp port, quit, exit, and with the save guard the console loads |
| `observe` | |
| `note` | `text` |

Every step may have a `label`. A scenario file is `{name, description, stop_on_fail, steps}`. A run longer than the call
budget ends with `outcome:"timeout"` and `next` = the step to pass as `start_at`. Input is released at the end. Returns
`op`, `outcome` (passed, failed, stopped, cancelled, timeout, valid, listed), `name`, `total`, `ran`, `next`,
`failures`, `results[] {i, do, label, ok, detail, steps, stop, value}`, `scenarios[]`, `game_steps`, `warnings`,
`changes`, `observe`.

```json
{"op": "run", "file": "smoke_respawn_run"}
{"op": "run", "steps": [{"do": "hold", "stick": [1, 0]}, {"do": "wait", "steps": 90}, {"do": "release"}, {"do": "expect", "path": "body.vx", "cmp": "le", "value": 0.5}]}
```

### 10.7 camera (write)

| Parameter | Type | Default | Values |
|---|---|---|---|
| `op` | enum | `state` | state, boxes, box_set, follow, poi, fx, pose, release |
| `box` | string | | op box_set: box id or path |
| `active` | boolean | | op box_set |
| `position_camera`, `position_aim` | number[3] | | op box_set |
| `object` | string | | op follow / poi (empty poi clears) |
| `blend_s` | number | 1 | 0..10 |
| `radius` | number | 30 | 1..500 (op boxes) |
| `shake`, `handshake` | boolean | | op fx |
| `pose` | object | | op pose: `x`, `y`, `z` (required), `yaw` (-720..720), `pitch` (-90..90), `fov` (5..140), `roll` (-180..180), `seconds` (0) |
| `pin` | boolean | true | pin the body's area while overridden |
| `limit` | integer | 30 | 1..100 |

While the camera is overridden the body's area is pinned (streaming follows the camera) and observe warns `camera_away`.
A free camera pose makes the body not controllable: release it before acting. Writes are re-read after 2 frames
(`verdict`). Returns `outcome`, `camera {pos, aim, up, fov, paused, target, overridden, free, active_box, weights}`,
`boxes[]`, `pinned_subscene`, `verdict`, `warnings`.

### 10.8 logic (destructive)

| Parameter | Type | Default | Values |
|---|---|---|---|
| `op` (required) | enum | | triggers_near, trigger_fire, signal_ports, signal_in, signal_out, signal_wire, signal_unwire, fsm_list, interactables_near, spawn_clone, body_set, anim_set |
| `object` | string | | target |
| `radius` | number | 15 | 0.5..200 |
| `kinds` | string[] | | triggers_near: probe, savepoint, killer, camera, fsm, other; interactables_near: lever, button, valve, gate, door, elevator, hatch, jetcrate, flag |
| `edge` | enum | `enter` | enter, exit (trigger_fire) |
| `mode` | enum | `probe` | probe, signal, teleport (trigger_fire) |
| `input` / `output` / `to` | string | | signal_in, signal_out, signal_wire |
| `filter`, `state` | string | | fsm_list and near ops |
| `position`, `rotation` | number[3] | | spawn_clone |
| `preawake` | boolean | true | spawn_clone |
| `velocity`, `angular`, `force` | number[3] | | body_set (force = impulse) |
| `kinematic`, `wake` | boolean | | body_set |
| `clip`, `time`, `speed`, `play` | | | anim_set (legacy Animation) |
| `limit` | integer | 40 | 1..200 |
| `offset` | integer | 0 | |
| `verify_frames` | integer | 2 | 0..30 |

Changes are re-read and get a `verdict`; `signal_out`, `signal_in` and FSM events cannot be undone. Firing a savepoint
writes `INSIDE.sav` (with the save guard on: backed up first). World flags are interactables of kind `flag`, set with
`signal_in SetTrue / SetFalse`. Returns `outcome`, `items[]`, `total`, `verdict`, `before`, `after`, `after_k`,
`inactive_target`, `note`, `changes`, `warnings`.

### 10.9 body (destructive)

| Parameter | Type | Default | Values |
|---|---|---|---|
| `op` | enum | `list` | list, teleport, hat, swap |
| `body` | string | the controlled one | body id |
| `x`, `y`, `z` | number | | teleport: world position (z default: keep the depth plane) |
| `dx`, `dy` | number | | -1000..1000, relative |
| `to_object` | string | | teleport to an object |
| `snap_camera` | boolean | true | |
| `wait_stream` | boolean | true | wait until streaming settles |
| `hat_op` | enum | `status` | attach, detach, status |
| `hat` | string | | hat id or path |
| `to` | enum | | boy, huddle (op swap) |
| `force` | boolean | false | skip the safety refusals (may break scripted scenes) |

Teleport refuses while dead, in a hat, in the submarine or scripted (unless `force`). Returns `outcome`, `bodies[]`,
`hats[]`, `verdict`, `before`, `after`, `warnings`, `observe`.

### 10.10 hazard (write)

| Parameter | Type | Default | Values |
|---|---|---|---|
| `op` | enum | `near` | near, set, air, god |
| `target` | string | | op set: hazard id or path |
| `all_near` | boolean | false | op set: every hazard within radius |
| `radius` | number | 20 | 1..200 |
| `on` | boolean | | op set: hazard active; op god: invincible |
| `infinite` | boolean | | op air |
| `refill_s` | number | | 0..60, add seconds of air |
| `max_dive_s` | number | | 1..120, dive limit of the current water |

Switches dogs, guards, sentries, spotlights, the mermaid, kill zones, the shockwave and kill FSMs off one by one without
god mode. God mode still plays death animations. Infinite air is re-applied after every respawn. All restored by
restore. Returns `outcome`, `hazards[]` (+ `switchable`), `changed[] {id, kind, how, verdict}`, `air {infinite,
left_s, max_s}`, `god`, `warnings`.

### 10.11 audio (write)

| Parameter | Type | Default | Values |
|---|---|---|---|
| `op` | enum | `music` | music, post, stop, rtpc, state, switch |
| `event` | string | | op post |
| `object` | string | global | op post / rtpc / switch |
| `name` | string | | RTPC name |
| `value` | number | | -1000000..1000000 |
| `interp_s` | number | 0 | 0..10 |
| `group`, `state` | string | | op state / switch |
| `playing_id` | integer | | op stop |

Master volume RTPCs accept only 0 (mute); nothing raises the game volume. Restore stops posted events and resets changed
RTPCs (never the master volume); states and switches have no undo. Returns `outcome`, `playing_id`, `music {pos_s,
loop_s, beat, bar, cue}`, `note`, `warnings`.

### 10.12 render (idempotent)

| Parameter | Type | Default | Values |
|---|---|---|---|
| `op` | enum | `state` | state, post, fog, default_lights, debug_view, debug_flags, shot_route |
| `on` | boolean | | post, fog, default_lights, debug_flags |
| `mode` | enum | | off, Depth, Normals, NormalsContrast, Lighting, Shadows, Transparency, Unlit, UnlitTransparency |
| `flag` | string | | EDebugFlags name (omit to list) |
| `index` | integer | | 0..1000, shot_route entry |
| `apply` | boolean | false | shot_route: move the free camera there |
| `limit` | integer | 40 | 1..200 |
| `offset` | integer | 0 | |

Debug flags TrialMode, UseSaveGame, SignInManually, InputRecording and Profiling are refused. `shot_route` is Playdead's
127-savepoint screenshot route. Restored by restore. Returns `outcome`, `state {post, fog, default_lights, debug_view}`,
`flags[]`, `route[] {index, subscene, savepoint, pos, cam_pos, cam_rot}`, `total`, `warnings`.

### 10.13 reflect (destructive)

| Parameter | Type | Default | Values |
|---|---|---|---|
| `op` (required) | enum | | get, set, call, find_methods, describe_type |
| `object` | string | | instance target |
| `component` | string | | component type (exact, then full name, then suffix; omitted = the GameObject) |
| `type` | string | | type name for statics, find_methods, describe_type |
| `member` | string | | field, property or method; dotted paths allowed (`properties.swim.infiniteAir`) |
| `value` | any | | op set (JSON) |
| `args` | array | | op call, at most 8: numbers, strings, bools, null, `[x,y,z]` (Vector3), `[x,y]` (Vector2), enum names, object ids or paths |
| `text` | string | | op find_methods filter |
| `verify_frames` | integer | 2 | 0..30 |
| `restore_on_end` | boolean | false | op set: undo it on restore |
| `limit` | integer | 40 | 1..200 |

Conversion is strict (out-of-range numbers are errors, never clamped). Refused: Steam and achievement calls, OS, file and
process APIs, `Application.Quit`, `Remote.RunCommand`, writes to `EditorState` / `Extensions` / `Mods`,
`PlayerPrefs.DeleteAll / DeleteKey`, raising the master volume, and with the save guard on, save writers. Returns
`outcome`, `value`, `type`, `verdict`, `before`, `after`, `after_k`, `methods[]`, `members[]`, `warnings`.

### 10.14 state_diff (idempotent)

| Parameter | Type | Default | Values |
|---|---|---|---|
| `op` | enum | `snapshot` | snapshot, diff, list, drop |
| `name` | string | `a` | snapshot name |
| `against` | string | | op diff: another snapshot instead of a new one |
| `center` | string | the body | object id or path |
| `radius` | number | 8 | 1..50 |
| `root` | string | | hierarchy path: everything under it instead of a radius |
| `components` | string[] | | only these component types (0..20) |
| `max_fields` | integer | 5000 | 100..50000 |
| `limit` | integer | 100 | 1..500 |

Captures active flags, transforms, enabled flags, rigidbodies, animations, lights, FSM states and variables and every
field of game scripts. At most 8 snapshots are kept in memory. Typical use: `snapshot`, pull a lever, `diff`. Returns
`snapshot {name, objects, components, fields, frame, step}`, `snapshots[]`, `diff[] {object, component, field, before,
after, change}`, `total`, `truncated`, `warnings`.

## 11. Resources and prompts

| URI / name | Profiles | Content |
|---|---|---|
| `inside://shots/{file}` (template) | play, dev | full-size PNG of an earlier screenshot (`uri` of a screenshot result); the newest 200 are kept |
| `inside://guide/index` | play, dev | playbook index (Markdown) |
| `inside://guide/<group>/<topic>` | play, dev | one topic at hint level, listed when Play loads; template `inside://guide/{chapter}/{topic}`; `?level=steps`, `?level=solution`, `?section=<heading>` |
| `inside://world/camera_route` | dev | Playdead's screenshot route as JSON |
| `inside://world/tunables` | dev | the knobs an agent may turn with type, range, units and the tool call that sets each |
| prompt `playtest_from_checkpoint` | play, dev | arguments `subscene` (required), `savepoint`, `goal` |
| prompt `solve_here` | play, dev | argument `goal` |

An unknown resource gives JSON-RPC -32002.

## 12. Plain HTTP endpoints

For scripts and people without an MCP client or session. Same listener, the same `Origin` check and the same token rule
as the MCP paths (`/ping` needs no token).

| Request | Answer |
|---|---|
| `POST /cmd`, text body = one console / bridge command | runs through the `command` tool (allowlist, refusals, audit line, 55 s budget) and answers `200 text/plain` with the command's reply. A failed command still answers 200, with the header `X-InsideDev-Error: <code>` (and `X-InsideDev-Truncated: 1` when cut at 200000 characters). 400: empty body or more than one line; 413: over 8192 characters; 403: `command` not in the allowlist; 405: another method |
| `GET /state` | the concise observe JSON (`observe` with its defaults); 503 with the error JSON when observe fails or Play is not loaded |
| `GET /ping` | `pong InsideDev <version>` |

```
curl -s -X POST --data-binary "ctl run right 1.2" http://127.0.0.1:47811/cmd
curl -s http://127.0.0.1:47811/state
curl -s -H "Authorization: Bearer <token>" -X POST --data-binary "mcp" http://127.0.0.1:47811/cmd
```

Each `/cmd` and `/state` request counts as a tool call for the idle restore. `/ping` answers from the server thread
alone, so it answers even while the game's main thread is stopped (minimized window): it does not prove the game runs.

## 13. Console commands

Typed in the InsideDev console (backquote key), sent through the file bridge (`_mod\cmd\in_<x>.txt` with
`<id>|<command>`, answer in `_mod\cmd\out.txt`), or through `POST /cmd` / the `command` tool (except the ones the
command tool refuses).

| Command | Does |
|---|---|
| `mcp` / `mcp status` | endpoint, requests, tool calls, errors, build line (mcp, play, core versions), last client, last error, tool counts per profile, providers, list version, control lane owner, calls in flight, main-thread queue, allowlist, whether a token is required |
| `mcp on` / `mcp off` | start / stop the server and save `mcp.enabled`; `off` also runs a restore |
| `mcp port <1024-65535>` | save `mcp.port` and restart the listener (open sessions end) |
| `mcp restore` | restore now |
| `mcp jobs` | running and recent jobs, one JSON line each |
| `mcp providers` | tool providers and their tool counts |
| `mcp selftest`, then `mcp test` | a real HTTP session from a worker thread: initialize, tools/list on dev and play, game_state, screenshot, observe, a dev tool on /mcp/play (expect unknown tool), DELETE both sessions; ends with PASS or FAIL |
| `play` / `play status` | Play tool count, ticks, problems |
| `play tools` | every Play tool with profiles and owner |
| `play restore` | the Play restore only |
| `ext` | loaded extensions and problems |
| `ext load` | load new `_mod\InsideDev.*.dll` files |
| `ctl ...` | character control (PLAY.md section 11) |

## 14. Files the server writes

| Path (under `<game>\_mod\`) | What |
|---|---|
| `mcp_audit.jsonl` (+ `.1`) | one JSON line per non-read-only tool call: time, profile, tool, session (8 characters), client, args (cut at 2000), outcome, ms; rotated at 5 MB |
| `shots\<frame>_<n>.png` | screenshots, newest 200 kept |
| `editor.cfg` | `mcp.enabled`, `mcp.port` when changed by the console |
| `input_tracks\<name>.json` | saved input tracks (Play) |
| `notes.jsonl`, `notes\note_<id>.png` | notes (Play) |
| `save_backups\INSIDE_<date>_<time>.sav` | save backups (`save backup`, or the save guard) |
| `scenarios\*.json` | read by `run_scenario` (you put them there) |

## 15. Pitfalls

- **Minimized window.** A minimized INSIDE window stops the game loop (observed in tests). Calls that need the main
  thread answer `not_ready` after their wait ("the main thread did not run the request within N s: the main thread is
  not running ..."); screenshots fail with "not written within 10 s"; a running action ends with `timeout`. Restore the
  window (it may stay behind other windows). An unfocused but visible window keeps running once an action has set
  `runInBackground`.
- **Step mode.** The first action call puts the game into step mode: frozen between calls. `time {op:"realtime"}` or
  `restore` lets it run. See PLAY.md section 4.1.
- **Agent input.** After the first action the physical pad and keyboard are ignored until restore or input mode human.
- **Idle restore.** 10 minutes without a tool call put the game back and stop an input track recording. Raise
  `mcp.idle_restore_s` for long sessions with pauses.
- **Restore on close.** A client that closes its session (DELETE) gets the game restored 60 s later. Clients that open a
  session per call are covered by the 60 s grace.
- **Load races.** `level` and `checkpoint` with `wait` wait for their own load. A load started through `command` (`load`,
  `spawn`, `cspawn`, mod commands) does not: `wait_until {ready:true}` can be satisfied by the old level before the new
  load starts. Wait for `{loaded:false}` first, or check `checkpoint` / `load_count` against the target, before acting or
  starting a recording. While a load job holds the lane, wait with `{job}`.
- **Ids die on every load and respawn.** `stale_id` means: observe again.
- **Commands outside the lane.** `command`, `set_active`, `fsm`, `signal` do not take the control lane and can change
  time or load in the middle of a play action.
- **MultiCam and the scene recorder (core features).** A MultiCam capture owns the clock and the control lane (job
  `mcam`, `busy` for actions, warning `external`); restore does not stop it (`mcam stop` does). A running scene recorder
  (`srec record`) owns the frame pacing: step-mode runs follow its clock and can end a step or two late (warning
  `recording: ...`).
- **Saves.** Normal play writes `INSIDE.sav` at checkpoints. Dev loads in respawn, chapter and newgame modes write it,
  chapter loads unlock chapters for good. Turn on `play.save_guard` for backups and confirms, or back up with
  `save {op:"backup"}`.
- **Orbs.** `save orbs set` changes local bits that reach Steam achievements at the next game start.
- **Updating DLLs.** `ext load` does not replace a loaded DLL; restart the game.
- **Editing editor.cfg while the game runs.** The game does not re-read it and overwrites it on its next settings save.
- **Token.** Without `mcp.token`, any program on this computer can drive the game through the server, `/cmd` included.

## 16. Troubleshooting

| Symptom | Check |
|---|---|
| connection refused on 47811 | game running? `mcp` in the console; `mcp on`; port changed (`mcp.port`)? `ext` lists load problems |
| stdio client shows only `inside_status` | the game is not reachable yet; call `inside_status` (its `text` says why); a 401 means the token |
| `tools/list` misses play tools | Play not loaded (`play`, `ext`); wrong endpoint (`/mcp` is dev); allowlist (`mcp.tools`) |
| 401 | `mcp.token` is set: send `Authorization: Bearer <token>` (stdio `--token` or `INSIDEDEV_MCP_TOKEN`) |
| 404 on every call | the game restarted and the session is gone: initialize again (the stdio program does it by itself) |
| `not_ready` everywhere | minimized window, a long load, or the game paused itself on focus loss |
| every action answers `busy` | `jobs {op:"list"}` (dev) or `mcp jobs`; a ctl job, a load job or a MultiCam capture holds the lane; `restore` |
| `/ping` answers but nothing else | the main thread is stopped (minimized window) |
| `guide` returns an empty index | the guide folder is not at `<game>\_mod\guide` (or `play.guide_dir` / `INSIDEDEV_GUIDE_DIR`) |
