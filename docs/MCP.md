# MCP server (InsideDev.Mcp.dll)

`InsideDev.Mcp.dll` is a [Model Context Protocol](https://modelcontextprotocol.io) server that runs inside the game, so an
AI assistant or any MCP client can look at and change the live game. It speaks Streamable HTTP on
`http://127.0.0.1:47811` (this computer only) and serves three tool sets (profiles):

| Profile | Endpoint | Tools | For |
|---|---|---|---|
| dev | `/mcp` (the 0.5 / 0.6 path), `/mcp/dev` | 34 | inspecting and editing the world: objects, FSMs, signals, loads, saves, camera, hazards, audio, render, reflection, filming |
| play | `/mcp/play` | 15 | playing the game: see [PLAY.md](PLAY.md) |
| all | `/mcp/all` | 41 | both (a client that takes at most 40 tools, such as Cursor, should use `/mcp/dev` or `/mcp/play`, or drop a tool with `mcp.tools`) |

The server alone provides the 12 editor text tools of earlier versions plus `screenshot` and `jobs` (14 tools on dev). The
world and play tools come from `InsideDev.Play.dll` (section 1). This document covers the server, the dev profile and
everything shared; [PLAY.md](PLAY.md) covers character control and the play tools in full. Every tool argument with its
type, default and range, generated from the code: [COMMANDS.md](COMMANDS.md) section 4.

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
running, copy the files and run `ext load` (in-game console, file bridge or `POST /cmd`): it loads DLL files that are not
loaded yet. A DLL that is already loaded is replaced only by a game restart. Other `InsideDev.*.dll` files that implement
the provider interface are picked up too (their tools join the lists, `list_changed` tells clients).

With Play loaded, `mcp` in the console reports `tools: play 15, dev 34, all 41`. Without Play: `play 1, dev 14, all 14`
(only `screenshot` is shared). `doctor` checks the server (part `mcp`: server, versions, tools, access, queue, last error);
a DLL of another version than the core is named there and in `mcp status`.

First call, step by step:

1. Start the Steam client, then the game; without the Steam client the game closes itself about 2 s after it starts.
2. `ping` in the in-game console answers `pong  InsideDev 0.11.0`; `doctor` lists every check.
3. `curl.exe http://127.0.0.1:47811/status` answers with the server state (version, tool counts, main thread, window).
4. Register the client while the game runs (section 5) and call `game_state` (dev) or `observe` (play).

## 2. On, off and defaults

| Item | Default | Effect | Switch |
|---|---|---|---|
| server | on (`mcp.enabled` = 1) | listens on `127.0.0.1:47811` and `[::1]:47811`; never on the network | console `mcp off` / `mcp on` (saved in editor.cfg); or remove the DLL |
| who may call | programs on this computer and pages of `127.0.0.1`, `localhost`, `[::1]` | other web pages, a foreign `Host`, `Origin: null` and cross-site requests get 403 (section 6) | `mcp hosts <names>` adds Host names |
| token | none | any local program can call the server | `mcp token new` / `mcp token clear` (console or file bridge) |
| tool allowlist | empty = every tool | | `mcp tools <list>` / `mcp tools clear` |
| idle restore | 600 s | after 600 s without a tool call the game is put back (section 9) | `mcp.idle_restore_s` |
| restore on close | 60 s | 60 s after the last client went away the game is put back | `mcp.restore_on_close_s` |
| text copy of results | full | `content[0]` carries the whole JSON result as text | `mcp.text_copy=short` (a one-line summary) |
| save guard | off | no save backups, confirms or refusals because of the save | `play.save_guard` |
| cheats | off | `checkpoint next / prev / goto` refused | `play.cheats` |

While no client calls a tool, the server changes nothing in the game, and the core's scanners sleep (`background auto`):
the first call after a quiet time may take a few milliseconds more while they catch up. The restore timers only act after
a tool call. The same listener also serves the plain endpoints `POST /cmd`, `POST /cmds`, `GET /state`, `GET /status`,
`GET /ping` (section 12), under the same token rule. The file bridge (`_mod\cmd`) is part of the core and works with or
without this DLL.

`InsideDev.Play.dll` adds one default that acts without a client: the notes hotkey (Home; `play.notes_key=none` switches
it off), see PLAY.md section 2. Its vSync rule (`play.vsync_off=1`) acts only while an agent's calls arrive or the clock
is held.

## 3. Settings

Settings are `key=value` lines in `<game>\_mod\editor.cfg` (`#` starts a comment line). The game reads the file at start
and rewrites all of it whenever a setting changes; comment lines are dropped then. While the game runs, `cfg` shows and
changes settings (`cfg get <key>`, `cfg set <key> <value>`, `cfg reset <key>`, `cfg json`). The `mcp.*` keys, `play.cheats`,
`play.save_guard` and `play.guide_dir` are protected: `cfg set` and every line that names an `mcp.*` access key are
refused through MCP and `POST /cmd`, so a client cannot widen its own access. Change them in the in-game console (`mcp
token`, `mcp tools`, `mcp hosts`, `mcp on|off`, `mcp port`, `cfg set`), through the file bridge, or in the file with the
game closed. `reflect` refuses `EditorState`.

| Key | Default | Effect | Change | Read in |
|---|---|---|---|---|
| `mcp.enabled` | `1` | the server starts at game start only when the value is exactly `1` (`true`, `on` or `yes` keep it off) | console `mcp on` / `mcp off` | src/mcp/Extension.cs |
| `mcp.port` | `47811` | listening port on 127.0.0.1 and [::1] | console `mcp port <1024-65535>` (restarts the listener) | src/mcp/Extension.cs |
| `mcp.token` | empty | when set: every MCP request and `POST /cmd`, `POST /cmds`, `GET /state`, `GET /status` need `Authorization: Bearer <token>` (HTTP 401 otherwise); `GET /ping` does not | console `mcp token new` (prints the token and how to pass it), `mcp token clear`, `mcp token show` | src/mcp/Extension.cs |
| `mcp.tools` | empty | allowlist, see below | console `mcp tools <allowlist>`, `mcp tools clear` | src/mcp/Extension.cs |
| `mcp.hosts` | empty | more names accepted in the `Host` header next to 127.0.0.1, localhost and [::1] (a port forward under another name) | console `mcp hosts <names>`, `mcp hosts clear` | src/mcp/Extension.cs |
| `mcp.idle_restore_s` | `600` | restore after this many seconds without a tool call; 0 = never | editor.cfg, `cfg set` in the console | src/mcp/Extension.cs |
| `mcp.restore_on_close_s` | `60` | restore this many seconds after the last client went away; 0 = never on close | editor.cfg, `cfg set` in the console | src/mcp/Extension.cs |
| `mcp.text_copy` | `full` | `full`: `content[0]` holds the result JSON as text; `short`: a one-line summary there (for clients that read `structuredContent`) | editor.cfg, `cfg set` | src/mcp/Extension.cs |
| `play.save_guard` | `0` | `1`: save backups, `confirm:true` for save-writing loads and edits, refusal of console loads through MCP | editor.cfg | src/mcp/Extension.cs, src/play/PlayExtension.cs |
| `play.cheats` | `0` | `1`: `checkpoint next / prev / goto` | editor.cfg | src/play/PlayLevels.cs |
| `play.guide_dir` | empty = `<game>\_mod\guide` | playbook folder (environment `INSIDEDEV_GUIDE_DIR` wins) | editor.cfg | src/play/Guide.cs |
| `play.guide_max` | `solution` | spoiler cap of `guide`: `hint`, `steps`, `solution` | editor.cfg, `cfg set` | src/play/Guide.cs |
| `play.notes_key` | `Home` | notes hotkey (Unity KeyCode name; `none` or `off` = no hotkey) | editor.cfg, `cfg set` | src/play/Notes.cs |
| `play.vsync_off` | `1` | `1`: vSync off while an agent's calls arrive (30 s after the last) and while the clock is held, else the game's own; `0`: Play never touches it; `2`: off from game start (0.8.0 to 0.10.0) | editor.cfg, `cfg set`; `vsync on / off / auto` overrides it until restore | src/play/PlayExtension.cs |
| `play.hold_timeout_s` | `0` | seconds without a play tool call after which input held by an agent is released while the game runs in realtime; 0 = never | editor.cfg, `cfg set` | src/play/PlayExtension.cs |
| `play.step_pacing` | `auto` | step-mode runs: `auto` = as fast as the game renders, paced in music-timed places; `always` = 60 steps per second; `never` | editor.cfg, `cfg set` | src/play/PlayExtension.cs |
| `play.perception_scan` | `all` | which objects the observe pass keeps: `all` (switched-off ones too) or `active` | editor.cfg, `cfg set` | src/play/Perception.cs |
| `play.perception_bg_s` | `0` | seconds between background refreshes of the observe caches while an agent is active; 0 = only when `observe` needs them | editor.cfg, `cfg set` | src/play/Perception.cs |

The `play.*` keys are re-read from memory while the game runs (about once a second while Play is in use), so `cfg set`
applies them at once; the `mcp.*` keys are read at start and by the console commands that set them.

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

Environment variables: `INSIDEDEV_MCP_TOKEN` and `INSIDE_MCP_PORT` (stdio program and the Python tools),
`INSIDEDEV_GUIDE_DIR` (game), and for the Python tools `INSIDE_BRIDGE`, `INSIDE_GAME`.

Built-in limits (not settings): tool budget 55 s plus 2.5 s grace; at most 128 sessions, a session unused for 6 h is
dropped; finished jobs are kept 10 minutes (the last 50); detached jobs are cancelled after 45 minutes; the newest 200
screenshots are kept, an image over 3.5 MB is not sent inline; `_mod\mcp_audit.jsonl` rotates at 5 MB; a request body
is at most 16 MB, a header line 64 KB, 200 header lines; JSON nests at most 128 levels; `POST /cmds` takes at most 200
lines.

## 4. Endpoints and profiles

| Path | Method | Answer |
|---|---|---|
| `/mcp`, `/mcp/dev` | POST, GET (SSE), DELETE | MCP, dev profile |
| `/mcp/play` | POST, GET, DELETE | MCP, play profile |
| `/mcp/all` | POST, GET, DELETE | MCP, all tools |
| `/cmd` | POST | one console command, text reply (section 12) |
| `/cmds` | POST | several commands, JSON reply (section 12) |
| `/state` | GET | the concise observe JSON (needs Play) |
| `/status` | GET | server and game health as JSON, answered without the game's main thread (section 12) |
| `/ping` | GET | `pong InsideDev <server version>` (no token needed) |
| `/`, `/health` | GET | plain text: server version and the endpoint list (no token needed) |
| anything else | | 404 with the endpoint list |

Every path is served on `127.0.0.1` and `[::1]`, and every path checks `Host`, `Origin` and `Sec-Fetch-Site` first
(section 6).

`tools/list` depends only on the path and the allowlist, never on the session. A tool that exists on another profile is
answered with JSON-RPC -32602 and the endpoint that serves it. A Play tool called while `InsideDev.Play.dll` is not loaded is answered
with -32602 `unknown tool: observe; observe is a tool of InsideDev.Play.dll, which is not loaded (copy InsideDev.Play.dll
into the game's _mod folder, then run the console command 'ext load' or restart the game)`.

| Tool | dev | play | Owner |
|---|---|---|---|
| `game_state`, `find_objects`, `inspect`, `select`, `set_active`, `fsm_graph`, `fsm_find`, `fsm`, `signal`, `show_panel`, `log`, `command`, `jobs` | yes | | server |
| `screenshot` | yes | yes | server |
| `observe`, `time`, `wait_until`, `restore`, `geometry`, `input_track`, `film` | yes | yes | Play |
| `act`, `move_to`, `interact`, `checkpoint`, `menu`, `guide`, `notes` | | yes | Play |
| `level`, `stream`, `ui`, `save`, `run_scenario`, `camera`, `logic`, `body`, `hazard`, `audio`, `render`, `reflect`, `state_diff` | yes | | Play |

The dev profile cannot move the body through `act`, `move_to` or `interact`; it can through `command` with `ctl ...`
(PLAY.md section 11), `run_scenario` and `input_track`.

## 5. Connecting a client

### 5.1 HTTP

Point the client at `http://127.0.0.1:47811/mcp/play`, `/mcp/dev` or `/mcp/all` (transport "http" or "Streamable
HTTP"). With a token, add the header `Authorization: Bearer <token>`. The game must be running when an HTTP client
connects; a client started before the game shows no tools until you reconnect the server in the client (for Claude Code:
`/mcp`, then reconnect). The stdio launcher (5.2) has no such order.

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
| `--port` | env `INSIDE_MCP_PORT`, else 47811 | 1..65535 |
| `--url` | | full endpoint URL; replaces `--port` and the profile path |
| `--token` | env `INSIDEDEV_MCP_TOKEN` | bearer token |
| `--help`, `-h`, `/?` | | usage on stderr |

An unknown argument prints the usage and exits with code 2.

Behaviour:

- `initialize`: when the game is up, the game's capabilities, server version and instructions; when it is down, short
  own instructions and a tool list with only `inside_status`.
- `inside_status` (read-only, no arguments) is always in the tool list, also while the game is up. It returns
  `{running, endpoint, profile, version, proxy_version, instructions, text}` plus `tools` (counts per profile),
  `play_loaded`, `main_thread_alive` and `game`. A client that initialized before the game started gets the game's
  instructions this way.
- Every 3 s it checks the game and sends `notifications/tools/list_changed` (and the resources / prompts ones) when the
  game comes up or goes away; while the game is up it holds the game's notification stream and relays its
  `list_changed` notifications.
- Requests with a progress token are relayed as they stream (each SSE event becomes one stdout line);
  `notifications/cancelled` is forwarded.
- After a game restart it opens a new game session by itself (on HTTP 404 for the old one).
- HTTP timeout 90 s (the game's tool budget is 55 s). Batches are not supported over stdio.
- When its input ends (the client exits) it answers the requests still in flight, then closes the game session with
  DELETE, which starts the restore-on-close timer.
- While the game is down, `tools/call` answers `not_ready`; a 401 from the game names the token.
- Version string: `<release>+stdio.<id>`, for this release `0.11.0+stdio.<id>` (`<id>` = first 8 hex digits of the exe's
  SHA-256).

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
  `InsideDev (INSIDE runtime editor)`, version `<release>+mcp.<id>[.play.<id>]`, for this release
  `0.11.0+mcp.<id>[.play.<id>]` (build ids = first 8 hex digits of each DLL's SHA-256). `instructions` per profile.
- Sessions: `initialize` returns `Mcp-Session-Id`; later requests send it. An unknown or expired session gets 404
  (initialize again). DELETE with the session id ends the session, its stream and its running calls. A request without
  any `Mcp-Session-Id` is served too (a one-line `tools/call` works without `initialize`, see section 12), but it cannot
  open the notification stream, is not counted as a live client for the restore on close, and shows an empty session in
  the audit log. A session counts as live while a call of it runs, while its stream is open (plus 30 s after the stream
  closed), or, without a stream, for 15 minutes after its last request. `mcp sessions [json]` lists them.
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
  boolean, array or object is expected is parsed when possible; for tools whose result has `warnings` (not the 12 text
  tools and `jobs`) this is reported as `coerced: ...` there.
- A `tools/call` whose HTTP client closes the connection is cancelled ("the client closed the connection").
- Output: a result that does not match its `outputSchema` loses the offending properties (named in `warnings` as
  `schema_repair: ...`), so strict clients do not reject it.
- Security (every path, checked before anything else):
  - The server listens on 127.0.0.1 and [::1] only.
  - `Host` must be `127.0.0.1`, `localhost` or `[::1]` with any port, or a name in `mcp.hosts`: else 403
    `host not allowed: ...` (this stops DNS rebinding). A request without `Host` is accepted.
  - `Origin`, when present, must be a page of one of those three hosts: else 403 `origin not allowed: ...`. `Origin: null`
    (sandboxed frames, `data:` and `file:` pages, redirects) is refused too.
  - `Sec-Fetch-Site: cross-site` gets 403 `cross-site requests are not allowed`.
  - A POST to an MCP path with a `Content-Type` header must name `application/json` (415 otherwise; no header is
    accepted). With curl: `-H "Content-Type: application/json"`.
  - Programs on this computer send none of the browser headers and are not affected; pages served from this computer
    (`http://localhost:<any port>`) are trusted like local programs. `mcp.token` makes every caller, those pages
    included, show the token; it is compared in constant time. Refused requests are counted in `GET /status`
    (`refused`, `last_error`) and logged at most every 10 s.

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
`isError` comes from the first line of the game's reply or, for console commands that answer through the log, from the
first log line they wrote. A line fails when it starts with `not found`, `usage`, `unknown`, `busy`, `refused`, `failed`,
`error`, `ERROR`, `cannot`, `can't`, `could not`, `no fsm`, `no active`, `no character`, `no type`, `no method`,
`no variable`, `no mod`, `no effect`, `no setting`, `no such`, `nothing selected`, `not built`, `object scan not finished`,
or contains `component not found`, `field/property not found`, `no parameterless method`, `" failed:"`, `unknown command`,
or ends with ` failed` / ` not found`. The same rule sets `X-InsideDev-Error` on `POST /cmd` and `status: error` in a file
bridge reply file. `content[0]` holds the result as JSON text; a text copy over 100000 characters is still valid JSON
with `truncated:true` and a note (`mcp.text_copy=short` makes it a one-line summary). Float values print as `0.1`, not
`0.10000000149011612`.

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
| `unauthorized` | reserved (a missing token is HTTP 401, JSON-RPC -32001) |
| `unsafe` | reserved: listed in every error schema, not returned by this build |

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
| 100 | `Continue`, for a request with `Expect: 100-continue` |
| 400 | bad request line, bad `Content-Length` or chunk size, parse error, unsupported protocol version, DELETE / GET stream without a session id, `/cmd` with an empty body or a line break, `/cmds` with an empty or malformed body |
| 401 | token missing or wrong |
| 403 | foreign `Host`, foreign or `null` `Origin`, `Sec-Fetch-Site: cross-site`; `/cmd`, `/cmds` or `/state` tool not in the allowlist |
| 404 | unknown or expired session, unknown path |
| 405 | wrong method; GET on an MCP path without `Accept: text/event-stream` (empty body; open `/` in a browser for the endpoint list) |
| 413 | body over 16 MB; `/cmd` command over 8192 characters |
| 415 | POST to an MCP path with a `Content-Type` other than `application/json` |
| 431 | a header line over 64 KB, or more than 200 header lines |
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
- Waiting for a job: `wait_until {any:[{job:"j7"}], timeout_s:50}` (both profiles; watches without the lane),
  `jobs {op:"await", id:"j7", timeout_s}` (dev), or for a ctl job `command {command:"ctl wait j7 30"}` (it waits on the
  tool's worker thread, at most the call's remaining time). Cancelling: `jobs {op:"cancel", id}` (dev), `restore`, or the call's
  own `notifications/cancelled`. A cancelled action releases its input and freezes the game again.
- Detached jobs running longer than 45 minutes (10 before 0.10.0) are cancelled with the reason `job ran longer than 2700 s`, and
  failed 5 s later if they do not end. The limit is built in, not a setting. Why it grew: a lockstep pass runs below real time on
  a busy machine, and a 431 s take that needed about 660 s of wall clock at 0.65x was cut at 600 s.
- The other side of that limit: a control job that hangs keeps the lane (`busy` for every action) and the idle restore waits while
  a control job runs, so without a call the game can stay in agent mode for up to 45 minutes instead of 10. By hand: `mcp jobs` shows
  it, `jobs {op:"cancel", id}`, `restore` or `mcp restore` ends it at once.
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
| last client gone | `mcp.restore_on_close_s` (60) seconds after no live session is left: every session ended by DELETE, or its client vanished (stream closed and 30 s of silence, or 15 minutes of silence without a stream), and only when a tool or command ran since the last restore. A new session cancels it |
| console `mcp off`, `mcp restore` | at once (with jobs running: `restore (mcp restore): 1 job(s) cancelled; the game is put back when they have ended (2 s at most)`) |
| game quit | at once |

Both automatic restores wait while a recording or capture is running: `mcam`, `acap`, `srec`, `grec`, `pano`, `rec` and a
recording input track (`mcp status` names what a due restore waits for). The `restore` tool parameters and keys are in
PLAY.md section 6.15 and 13; `restore {dry_run:true}` and the console `play restore list [json]` say what a restore would
undo without doing it. Console `play restore` runs only the Play part.

## 10. Dev tool reference

The play-profile tools shared with dev (`observe`, `time`, `wait_until`, `restore`, `geometry`, `input_track`,
`screenshot`) are documented in [PLAY.md](PLAY.md) section 6. Annotations: readOnly tools change nothing; destructive
tools can change the game in ways that cannot be undone.

Objects are named by an observe id (`o12`, `h3`, `b1`), a hierarchy path (`A/B/C`), `#<instanceID>` or a name. A name
is matched loosely: the exact name first, else the text inside a name; of several matches the one nearest to the boy is
used. `set_active`, `fsm` and `signal` then add `matches` (how many fitted) and `candidates` (up to 10 as
`<path> #<id>`, nearest first) and a warning; with `strict:true` such a name fails with `ambiguous` and the same
`candidates`. Use a path or `#<id>` for anything that cannot be undone. Ids of objects that exist again after a load at
the same path resolve again; an id whose object is gone gives `stale_id` with the reason.

### 10.1 Editor tools (server)

| Tool | Parameters (type, default, range) | Notes |
|---|---|---|
| `game_state` (readOnly) | none | text snapshot: InsideDev version, frame, boy position, savepoint, music position, active scenes, nearby triggers |
| `find_objects` (readOnly) | `query` string (required); `category` enum animation, sounds, scripts, triggers, fsm, characters; `limit` integer 25 (1..100) | ranked search over loaded objects; adds `count` |
| `inspect` (readOnly) | `object` (required); `deep` boolean false; `max_chars` integer 20000 (1000..200000); `offset` integer 0 | path, active state, components, fields, references, FSM dump; page with `offset = next_offset`; `page_of` = the frame the paged text was made in |
| `select` (idempotent) | `object` (required) | selects in the editor (Inspector, Explorer, graph follow); no game change |
| `set_active` (idempotent) | `object`, `active` boolean (both required); `strict` boolean false | recorded in History (`command "undo"`); re-reads after 2 frames: `verdict` (confirmed, reverted_by_game, not_applied, unreadable), `before`, `after`, `after_k`, `active_in_hierarchy` |
| `fsm_graph` (readOnly) | `object` (required); `fsm` string; `open_panel` boolean false | states, exits with events, current state; `open_panel` also selects it and opens the FSM Graph panel |
| `fsm_find` (readOnly) | `text` (required); `limit` integer 30 (1..200) | searches every loaded PlayMaker FSM, including switched-off ones |
| `fsm` (destructive) | `object`, `action` enum event, state, var, `name` (required); `value` string; `fsm` string; `strict` boolean false | send an event, force a state, read (no value) or set a variable; event and state cannot be undone |
| `signal` (destructive) | `object`, `output` (required); `strict` boolean false | fires a Playdead signal output (every connection, like the game); cannot be undone |
| `show_panel` (idempotent) | `panel` (required: spawns, levels, triggers, hidden, world, audio, inspector, cheats, console, diag, settings, objects, explorer, graph, events, sounds, atimeline, wwiseapi, history, audiodb, logic, modified, fsmgraph, fsmfind, or none); `dock` enum left, right, bottom; `size` number 0.1..0.7 | opens the editor; restore closes a panel it opened |
| `log` (readOnly) | `lines` integer 60 (1..400) | the last lines of the InsideDev log |
| `command` (destructive) | `command` string, or `commands` array of strings (1..200; one of the two); `max_chars` integer 20000 (1000..200000); `stop_on_error` boolean false; `dry_run` boolean false; `wait` string; `until` string `done\|failed\|idle\|not running\|stopped\|finished\|closed\|error`; `wait_timeout_s` number 45 (0..50) | any command (`help` lists them, `help <command>` prints one); returns its reply plus the log lines it wrote: `text`, `from` (reply, log, both, none), with `commands` also `results [{command, ok, code, text}]`, `count`, `failed`; with `wait` the status command is asked after the command until its reply contains one of the `until` texts: `wait_done`, `wait_hit`, `waited_s`, `wait_reply`. A reply that ends in `wait: pano wait`, `grec wait` or `rec wait` is followed by itself until the file is complete |
| `jobs` | `op` enum list, status, await, cancel (default list); `id` string; `timeout_s` number 10 (0..50) | returns `jobs[]`, `job {id, tool, lane, state, elapsed_s, progress, total, message, error, detached, cancel_reason}`, `result`, `finished`, `hint`; `cancel` without id cancels the running control job |

`command` (and `POST /cmd`, `POST /cmds`) refuses: `keyhold` and `focustest` (OS input; use `act`), `remote off`,
`mcp off`, `mcp port` (they would cut the server from inside a call); `mcp token`, `mcp tools`, `mcp hosts` and any line
that names `mcp.token`, `mcp.tools`, `mcp.hosts`, `mcp.enabled` or `mcp.port` (they decide who may use the server); `cfg
set` of a protected setting; lines with a line break or a control character; lines that drive a secret / achievement
object (`signal`, `signalin`, `fsm`, `call`,
`set`, `fsmset`, `sigwire` on FinalSecretFade, SecretPod*, Secret, SecretSuitcase, AchiementTrigger,
StartNewGameMenuItem, or a signal reaching one within 3 wires); with `play.save_guard` on also `load`, `spawn`,
`reload`, `respawn`, `cspawn go`, `cspawn respawn`. Examples:

```json
{"command": "near 10"}
{"command": "get \"Housing (4)\" Transform localPosition"}
{"command": "ctl run right 1.2"}
{"commands": ["boypos", "ctl status"], "stop_on_error": true}
{"command": "pano shot.png", "wait": "pano status"}
```

`screenshot` (both profiles) takes `mode` (preview, marked, full, path; default preview), `max_width` (960, 0..4096,
0 = no scaling), `clean` (true), `fast` (false; full / path: return once the file is written, without the black check),
`format` (`png` default, `jpeg`; preview / marked) and `quality` (80, 1..100, jpeg). The result has `format`, `bytes`,
`ms` (the whole call) and `main_ms` (its main-thread part); for preview and marked, `path` and `uri` name the returned
image saved as a file (`_mod\shots\<frame>_<n>.png` or `.jpg`), not a full-size PNG; `full` and `path` still write the
full-size PNG. Example: `screenshot {format:"jpeg", quality:80}`; at 1600 px that was 121 KB in 0.12 s.

### 10.2 level (destructive)

| Parameter | Type | Default | Values |
|---|---|---|---|
| `op` | enum | `status` | status, list, wait_ready, load_checkpoint, load_chapter, reload, step |
| `subscene`, `savepoint` | integer | | 0..999 |
| `name` | string | | op load_checkpoint: the subscene by scene or puzzle name (exact, else a part that matches one subscene; savepoint defaults to 0); `not_found` / `ambiguous` with `candidates` |
| `what` | enum | `all` | all, subscenes, chapters (op list) |
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
- While a `next` / `prev` savepoint search that outlived its call still runs, loads answer `busy: a savepoint search is
  still running (it loads and unloads subscenes)`.
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
streaming changes. All undone by restore. A second `load` while one is still running answers `busy: a stream load is still
running (an earlier call ended before it did)`. Returns `outcome`, `frozen`, `cullers[] {subscene, name, state, pinned,
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
| `resolution` | integer | | 0..64, index into the resolution list (a bad index changes nothing: `bad_args`) |
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
`signal_in SetTrue / SetFalse`. `interactables_near` lists one row per component. `signal_unwire` of a wire made through
MCP notes that nothing is left to undo. Returns `outcome`, `items[]`, `total`, `verdict`, `before`, `after`, `after_k`,
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
god mode. Kill-FSM rows have the kind `kill_zone`, `spotlight`, `rope_gun` or `guard`. God mode still plays death animations. Infinite air is re-applied after every respawn. All restored by
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
| `members` | string[] | | op get: up to 32 members of the same object at once; result `values[] {member, value, type}` or `{member, error}` |
| `sets` | object[] | | op set: up to 16 `{member, value}` on the same object; result `results[] {member, verdict, before, after, after_k, type}`, `verdict` = the worst one |
| `repeat_frames` | integer | 0 | 0..120, op get: read again that many rendered frames later (`value_k`, `changed`) |
| `value` | any | | op set (JSON) |
| `args` | array | | op call, at most 8: numbers, strings, bools, null, `[x,y,z]` (Vector3), `[x,y]` (Vector2), enum names, object ids or paths |
| `text` | string | | op find_methods filter |
| `verify_frames` | integer | 2 | 0..30 |
| `restore_on_end` | boolean | false | op set: undo it on restore |
| `limit` | integer | 40 | 1..200 |

Conversion is strict (out-of-range numbers are errors, never clamped). Calls and writes go through an allow list (code
`refused`; reads through `get` are not limited except where noted): game code (Assembly-CSharp, the firstpass assembly,
PlayMaker), UnityEngine (except `Application` calls and writes other than `targetFrameRate`, `runInBackground`,
`backgroundLoadingPriority` and read-only names, `PlayerPrefs` writes, `Screen.SetResolution` / `fullScreen`, and the
network, WWW, microphone, webcam, profiler and similar types), and from the rest of .NET only `System.Object`, strings,
numbers, `Math`, `Convert`, `DateTime`, `TimeSpan`, `Guid`, arrays, collections, `System.Text`, `System.Globalization`
and a few more. Refused, among others: `System.Xml`, `System.IO`, processes, code loading, the registry, Steam and
achievement calls, InsideDev's own code (`InsideDev.SafeFile.WriteAllText is InsideDev code and is not callable through
reflect`), writes to `EditorState` / `Extensions` / `Mods`, raising the master volume (a property setter called through
`call` is checked as a write), and with the save guard on, save writers. A game method that itself writes a file is still
callable. Non-finite floats are the strings `"NaN"`, `"Infinity"`, `"-Infinity"`; lists show at most 20 items. Returns
`outcome`, `value`, `values[]`, `results[]`, `type`, `verdict`, `before`, `after`, `after_k`, `methods[]`, `members[]`,
`warnings`.

```json
{"op": "get", "object": "boy", "component": "Boy", "members": ["isDead", "velocity", "properties.swim.infiniteAir"]}
{"op": "set", "object": "boy", "component": "Boy", "sets": [{"member": "properties.swim.infiniteAir", "value": true}], "restore_on_end": true}
```

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
| `ignore_noise` | boolean | false | op diff: leave out Animation time, Rigidbody velocity / angular / sleeping and Transform moves of at most 0.02 (`ignored` = how many) |
| `group` | boolean | false | op diff: `objects[{object, changes[]}]` instead of `diff[]` |
| `rolling` | boolean | false | op diff without `against`: the fresh snapshot replaces the named one (`rolled:true`) |

Captures active flags, transforms, enabled flags, rigidbodies, animations, lights, FSM states and variables and every
field of game scripts. At most 8 snapshots are kept in memory. Typical use: `snapshot`, pull a lever, `diff`. Returns
`snapshot {name, objects, components, fields, frame, step}`, `snapshots[]`, `diff[] {object, component, field, before,
after, change}`, `total`, `truncated`, `warnings`.

### 10.15 film (both profiles, Play)

Panorama, game-frame video and the live director camera with structured results: `op` status, pano, grec_start,
grec_stop, campath_load, campath_arm, campath_start, campath_stop, mount. `pano` and `grec_stop` return when the file is
complete (`pending:true` when the call's time ran out); `grec_start` returns when frames are being written, or with
`game_error` and the reason. On the play profile `file` must be a bare name (it goes to `<game>\_mod\recordings\`) and
`faces_dir` and the campath / mount ops are refused; the dev profile has them all. Example:
`film {op:"pano", size:4096, overscan:8}`, `film {op:"grec_start", file:"take1", every:2}` then `film {op:"grec_stop"}`.
Every argument: [PLAY.md](PLAY.md) section 6.17 and [COMMANDS.md](COMMANDS.md) section 4.

## 11. Resources and prompts

| URI / name | Profiles | Content |
|---|---|---|
| `inside://shots/{file}` (template) | play, dev | an earlier screenshot file (`uri` of a screenshot result): PNG, or JPEG for `format:"jpeg"`; the newest 200 are kept |
| `inside://guide/index` | play, dev | playbook index (Markdown) |
| `inside://guide/<group>/<topic>` | play, dev | one topic at hint level, listed when Play loads; template `inside://guide/{chapter}/{topic}`; `?level=steps`, `?level=solution`, `?section=<heading>` |
| `inside://world/camera_route` | dev | Playdead's screenshot route as JSON |
| `inside://world/tunables` | dev | the knobs an agent may turn with type, range, units and the tool call that sets each |
| prompt `playtest_from_checkpoint` | play, dev | arguments `subscene` (required), `savepoint`, `goal` |
| prompt `solve_here` | play, dev | argument `goal` |

An unknown resource gives JSON-RPC -32002.

## 12. Plain HTTP endpoints

For scripts and people without an MCP client or session. Same listener, the same `Host` / `Origin` / `Sec-Fetch-Site`
checks and the same token rule as the MCP paths (`/ping` needs no token).

| Request | Answer |
|---|---|
| `POST /cmd`, text body = one command | runs through the `command` tool (allowlist, refusals, audit line, 55 s budget) and answers `200 text/plain` with the command's reply. A failed command still answers 200, with the header `X-InsideDev-Error: <code>` (and `X-InsideDev-Truncated: 1` when cut at 200000 characters). 400: empty body or a line break; 413: over 8192 characters; 403: `command` not in the allowlist; 405: another method |
| `POST /cmd?wait=<status command>[&until=<text\|text>][&timeout=<0..50>]` | runs the body, then asks the status command until its reply contains one of the `until` texts (default `done\|failed\|idle\|not running\|stopped\|finished\|closed\|error`); the reply is the command's output, a line `--- <status command> ---` and the last status reply; header `X-InsideDev-Wait: done` or `timeout` |
| `POST /cmds[?stop_on_error=1][&dry_run=1]`, body = one command per line (empty lines and `#` lines skipped) or a JSON array of strings, at most 200 | `application/json`: `[{"command", "ok", "code", "text"}, ...]`; a failed line has `ok:false` and `code`; with `stop_on_error` the rest get `code:"skipped"`; `dry_run` only reports which lines would be refused; header `X-InsideDev-Error: <first code>`. 400 for an empty or malformed body |
| `GET /state` | the concise observe JSON (`observe` with its defaults); 503 with the error JSON when observe fails or Play is not loaded |
| `GET /status` | server and game health, answered on the server thread without waiting for the game: `{ok, version, port, requests, tool_calls, errors, refused, calls_in_flight, sessions {total, live}, last_client, last_error, mcp, core, version_problem (only when the DLL versions differ), main_thread {alive, stalled_s, queue}, frame, control {job, tool, elapsed_s} or null, calls_running, screen, screen_frame, last_tool_error, window {minimized, focused}, tools {play, dev, all}, play_loaded, restore {idle_s, on_close_s, last_call_ago_s, held}, token_required}` |
| `GET /ping` | `pong InsideDev <server version>` (for example `pong InsideDev 0.11.0+mcp.<id>.play.<id>`) |

The examples call `curl.exe`, which is part of Windows 10 and 11 (in Windows PowerShell 5.1 a plain `curl` is another
command). The last line posts a JSON-RPC call without a session; put the body in a file `call.json`:
`{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"game_state","arguments":{}}}`.

```
curl.exe -s -X POST --data-binary "ctl run right 1.2" http://127.0.0.1:47811/cmd
curl.exe -s -X POST --data-binary "pano shot.png" "http://127.0.0.1:47811/cmd?wait=pano%20status"
curl.exe -s http://127.0.0.1:47811/status
curl.exe -s http://127.0.0.1:47811/state
curl.exe -s -H "Authorization: Bearer <token>" -X POST --data-binary "mcp" http://127.0.0.1:47811/cmd
curl.exe -s -X POST -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" --data-binary @call.json http://127.0.0.1:47811/mcp/dev
```

PowerShell 5.1:

```
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:47811/cmd -Body "ctl status"
```

Each `/cmd`, `/cmds` and `/state` request counts as a tool call for the idle restore. `/ping` and `/status` answer from the
server thread alone, so they answer even while the game's main thread is stopped (minimized window): `/ping` does not
prove the game runs; `/status` says so in `main_thread.alive` and `window.minimized`.

## 13. Console commands

Typed in the InsideDev console (backquote key), sent through the file bridge (`_mod\cmd\in_<x>.txt` with
`<id>|<command>`, answer in `_mod\cmd\out.txt` and `_mod\cmd\out_<id>.txt`), or through `POST /cmd` / the `command` tool
(except the ones the command tool refuses). Since 0.11.0 the in-game console runs every command, also the bridge words
(`ext`, `perf`, `mcam`, `srec`, `acap` ...). `help` lists them all, `help <command>` prints the usage of one, and
[COMMANDS.md](COMMANDS.md) has the full table.

| Command | Does |
|---|---|
| `mcp` / `mcp status` | endpoint, requests, tool calls, errors, refused requests, sessions (live), build line (mcp, play, core versions; a version warning when they differ), last client, last error, tool counts per profile, providers, list version, control lane owner, calls in flight, main-thread queue, allowlist, extra hosts, text copy mode, restore settings and what a due restore waits for, whether a token is required |
| `mcp status json` (or `mcp json`) | one JSON line: `GET /status` plus `"running":true`; server off: `{"ok":true,"running":false,"enabled":...,"error":"..."}` |
| `mcp on` / `mcp off` | start / stop the server and save `mcp.enabled`; `off` also runs a restore. `mcp on` prints the HTTP URL, the stdio client line and an example |
| `mcp port <1024-65535>` | save `mcp.port` and restart the listener (open sessions end) |
| `mcp sessions [json]` | one line per session: id, profile, live or silent, idle, age, stream, calls, requests, client |
| `mcp token new\|clear\|show` | console and file bridge only: `new` makes a 32-hex-digit token, saves it as `mcp.token` and prints how to pass it (`mcp token set: <token>` ...); `clear` removes it; `show` prints it |
| `mcp tools [<allowlist>\|clear]` | console and file bridge only: show or set `mcp.tools`. Example: `mcp tools play:* screenshot` |
| `mcp hosts [<names>\|clear]` | console and file bridge only: show or set `mcp.hosts`. Example: `mcp hosts gamebox` |
| `mcp restore` | restore now |
| `mcp jobs` | running and recent jobs, one JSON line each |
| `mcp providers` | tool providers and their tool counts |
| `mcp selftest`, then `mcp test` | a real HTTP session from a worker thread: initialize, tools/list on dev and play, game_state, screenshot, observe, a dev tool on /mcp/play (expect unknown tool), DELETE both sessions; ends with PASS or FAIL |
| `doctor [json]` | the self-check of every part, the MCP server included |
| `cfg [list\|get\|set\|reset] ...` | the settings (section 3) |
| `play` / `play status [json]` | Play tool count, ticks (and which run now), last agent call, problems |
| `play tools` | every Play tool with profiles and owner |
| `play restore [list [json]]` | the Play restore only; `list` says what it would reset and changes nothing |
| `ext [json]` | loaded extensions, their cost per frame, their levels and problems |
| `ext load [json]` | load new `_mod\InsideDev.*.dll` files |
| `ctl ...` | character control (PLAY.md section 11) |
| `perf ...` | the profiler (section 13.1) |
| `rec ...`, `vsync ...` | real-time recorder and vSync switch (PLAY.md section 4.5) |
| `pano ...`, `campath ...`, `grec ...` | panorama, live director camera, game-render video (PLAY.md sections 4.6 to 4.8) |

### 13.1 `perf`: the built-in profiler

`perf` answers in the in-game console, through the file bridge, `POST /cmd`, the dev `command` tool and
`tools\inside_ctl.py`; the editor's Diagnostics panel shows the same report. It measures InsideDev itself (not the game)
and always runs: only `hide` changes anything in the scene. `perf json` and `perf reset json` give the report as one JSON
object (`version`, `editorOpen`, `background`, `inUse`, `frame`, `gc`, `sinceReset`, `allocBytesPerFrame`, `rows`).

| Command | Does |
|---|---|
| `perf [json]` | the report: cost per frame over the last 300 frames, garbage collections, the background mode and whether the tool is in use, one row per subsystem and one per extension (`ext.<name>`) |
| `perf reset` | clears the rows and starts the "since perf reset" line |
| `perf enable <subsystem>` | switches a subsystem back on after the fail-safe took it off (30 exceptions in a row) |
| `perf hide <root>[,<root>...] [name part \| mesh:<prefix>]` | disables the renderers under active objects named `<root>`; with a name part only renderers whose object name contains it (any case), with `mesh:` only those whose mesh name starts with the prefix (exact case). Lights stay on |
| `perf show` | enables everything `perf hide` disabled |

What the report says (the layout; the numbers are an example):

```
InsideDev cost per frame (last 300 frames): avg 0.570 ms, max 6.70 ms   editor closed
  garbage collections: 31 since start (1.9/min), 12 ran inside mod calls (max 516.0 ms), heap 450 MB
  since perf reset 10.0 s ago: 0 garbage collection(s), 0 inside mod calls (max 0.0 ms)
  subsystem                avg ms    max(win)   max(all)  closed avg  open avg   fails  alloc KB/call
  Extensions               0.1200      1.300      13.00      0.1200         -       0            1.5
  mcp.pump                 0.0100      0.500     127.70      0.0100         -       0            0.0  (part of EditorUI)
```

- Since 0.11.0 the report also says `since perf reset ... avg X ms per frame over N frames` and
  `background <mode>, tool in use` or `tool idle (scanners asleep)`. Asking for `perf` counts as using the tool, so the
  scanners wake for 30 s; measure an idle game with `perf reset`, then wait, then `perf`, and read the "since perf reset"
  line.
- The `max` on the second line is the session's longest pause, so one load can stay in it for hours. The `since perf reset` line (new
  in 0.10.0) has its own count and maximum, so every measuring window shows its own collections. It appears after the first `perf reset`.
- A pause is charged to the call a collection happened to run in, and every allocation in the process causes it: a row with a big
  `max(all)` and a collection in the log is not by itself a slow row.
- `mcp.log`, `mcp.pump` (tool calls that run on the main thread), `mcp.discovery` and `mcp.sweep` used to be hidden in `Extensions`. They
  are parts of it and are not added to the frame total again. Their rows carry the label `(part of EditorUI)`, which every nested row
  prints: for these rows read it as "part of Extensions".
- These four are under the fail-safe now: `mcp.pump` throwing 30 times in a row would go off (MCP calls would stop being served and
  the log would say `FAIL-SAFE`) until `perf enable mcp.pump`. It catches the errors of each task itself, so this is not expected; before
  0.10.0 such an exception was only logged.

**Measure a window by hand.**

1. `python tools\inside_ctl.py perf reset` with the game at the place you want to measure.
2. Wait 10 to 30 s. Do not call other tools meanwhile: their calls land in `mcp.pump`.
3. `python tools\inside_ctl.py perf`, and read the first three lines and the top rows.

**Measure what a part of the scene costs by hand.**

1. Note the frame rate (`time {op:"state"}`, or the HUD).
2. Find the name of the part in the editor's scene explorer (F1), then `python tools\inside_ctl.py perf hide "<root>"`. The reply says what
   went: `perf hide: N renderer(s) off under 1 root(s); N shadow caster(s), N mesh(es) with N vertices, N material(s); lights untouched; N
   hidden in total (perf show restores)`. The part vanishes from the picture and from its shadows; colliders and logic stay.
3. Compare the frame rate at the same view. In a mod scene with thousands of kit pieces this showed at once that a dark room was not
   dark because of the kit.
4. `python tools\inside_ctl.py perf show`. The reply is `perf show: N renderer(s) back on`, and the part is back in the picture.

| If you see | Why | Do |
|---|---|---|
| `(not found: <root>)` in the reply | the name is not an active object (`GameObject.Find`) | use the exact name or a full path; quote a name with spaces; several roots are comma separated, no spaces |
| `0 renderer(s) off` | no enabled renderer matched | leave out the name part; a skinned mesh has no `mesh:` match, use the name part for it |
| things stay hidden after `restore` | `restore` does not know `perf hide` | `perf show`. A level load destroys hidden renderers of that level; those are not counted back |

## 14. Files the server writes

| Path (under `<game>\_mod\`) | What |
|---|---|
| `mcp_audit.jsonl` (+ `.1`) | one JSON line per non-read-only tool call: time, profile, tool, session (8 characters), client, args (cut at 2000), outcome, ms; rotated at 5 MB |
| `shots\<frame>_<n>.png` / `.jpg` | screenshots, newest 200 kept |
| `editor.cfg` | `mcp.enabled`, `mcp.port`, `mcp.token`, `mcp.tools`, `mcp.hosts` when changed by the console, and any key set with `cfg set` |
| `recordings\` | `grec`, `pano` and `rec` files given without a folder (`film` on the play profile always) |
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
- **Idle restore.** 10 minutes without a tool call put the game back (it waits while a recording, a capture or an input
  track recording runs). Raise `mcp.idle_restore_s` for long sessions with pauses.
- **Restore on close.** A client that closes its session (DELETE) or vanishes gets the game restored 60 s later. Clients that open a
  session per call are covered by the 60 s grace.
- **Load races.** `level` and `checkpoint` with `wait` wait for their own load. A load started through `command` (`load`,
  `spawn`, `cspawn`, mod commands) does not: `wait_until {any:[{ready:true}]}` can be satisfied by the old level before
  the new load starts. Wait for `{any:[{loaded:false}]}` first, or check `checkpoint` / `load_count` against the target,
  before acting or starting a recording. While a load job holds the lane, wait with `{any:[{job:"jN"}]}`.
- **Ids after a load.** An id of an object that exists again at the same path resolves again; `stale_id` (object gone,
  or from before the load that is running now) means: observe again.
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
- **Long jobs.** Detached jobs end after 45 minutes; a hung one holds the lane that long unless you cancel it (section 8).
- **Renderers hidden by `perf hide`** stay hidden through `restore`: `perf show` brings them back (section 13.1).
- **Updating DLLs.** `ext load` does not replace a loaded DLL; restart the game.
- **Editing editor.cfg while the game runs.** The game does not re-read the file and overwrites it on its next settings
  save (comment lines are dropped). Use `cfg set` instead.
- **Token.** Without `mcp.token`, any program on this computer can drive the game through the server, `/cmd` included.
  Web pages cannot (section 6).
- **Steam client.** Without it the Steam copy of the game closes itself about 2 s after it starts; the server never comes up.
- **The tool sleeps while unused.** With no panel open and no call for 30 s the core's scanners stop
  (`background auto`). A call wakes them; the first `observe` or `find_objects` after a long pause can include a short
  catch-up. `background always` keeps them running.
- **41 tools on `/mcp/all`.** A client that takes at most 40 tools sees an error or drops one: use `/mcp/dev` or
  `/mcp/play`.

## 16. Troubleshooting

| Symptom | Check |
|---|---|
| connection refused on 47811 | game running? `mcp` in the console; `mcp on`; port changed (`mcp.port`)? `ext` lists load problems |
| stdio client shows only `inside_status` | the game is not reachable yet; call `inside_status` (its `text` says why); a 401 means the token |
| `tools/list` misses play tools | Play not loaded (`play`, `ext`); wrong endpoint (`/mcp` is dev); allowlist (`mcp.tools`) |
| 401 | `mcp.token` is set: send `Authorization: Bearer <token>` (stdio `--token` or `INSIDEDEV_MCP_TOKEN`); `mcp token show` in the console prints it |
| 404 on every call | the game restarted and the session is gone: initialize again (the stdio program does it by itself) |
| `not_ready` everywhere | minimized window, a long load, or the game paused itself on focus loss |
| every action answers `busy` | `jobs {op:"list"}` (dev) or `mcp jobs`; a ctl job, a load job or a MultiCam capture holds the lane; `restore` |
| `/ping` answers but nothing else | the main thread is stopped (minimized window); `GET /status` shows `main_thread.alive` and `window.minimized` |
| 403 `host not allowed` / `origin not allowed` / `cross-site requests are not allowed` | the request came from a web page or under another host name; use a local client, or `mcp hosts <name>` for a port forward |
| 415 on `/mcp` | the client sent a `Content-Type` other than `application/json` |
| the game closes about 2 s after it starts | the Steam client is not running |
| anything else | `doctor` in the console, or `python -I tools/inside_ctl.py doctor` from outside |
| `guide` returns an empty index | the guide folder is not at `<game>\_mod\guide` (or `play.guide_dir` / `INSIDEDEV_GUIDE_DIR`) |
