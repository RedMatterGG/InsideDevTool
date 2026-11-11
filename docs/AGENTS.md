# InsideDev for AI agents: start here

This page is the entry point for an AI agent that works with INSIDE (Playdead, Unity 5.0.4f1) through InsideDev. It says
what each part does, how to reach the game, what is on and off by default, which reference to read for a task, and the
pitfalls that cost the most time. Every statement here is detailed in the linked reference; when the two differ, the
reference and the code win. Every command, setting and MCP tool argument, generated from the code: [COMMANDS.md](COMMANDS.md).

`<game>` is the INSIDE folder (the one with `INSIDE.exe`). `<repo>` is the folder of this repository or release zip.

Contents

1. The parts and their references
2. How to reach the running game
3. Check what is installed
4. Switches and defaults
5. Rules that keep the user's game and ears safe
6. Workflows
7. Pitfalls
8. Where files go

---

## 1. The parts and their references

| Part | File | What it gives you | Reference |
|---|---|---|---|
| Editor (core) | `bin\InsideDev.dll` in `<game>\_mod\` | the in-game editor, console and file bridge; `help`, `doctor`, `cfg`, `background`; the `srec`, `mcam`, `acap`, `cam`, `hud`, `clean`, `gamma`, `seed` commands | README.md, [COMMANDS.md](COMMANDS.md), [RECORD_REPLAY.md](RECORD_REPLAY.md), [MULTICAM.md](MULTICAM.md), [AUDIO.md](AUDIO.md) |
| MCP server | `bin\InsideDev.Mcp.dll` in `<game>\_mod\` | MCP on `http://127.0.0.1:47811` with the dev, play and all profiles; plain `POST /cmd`, `POST /cmds`, `GET /state`, `GET /status`, `GET /ping` | [MCP.md](MCP.md) |
| Character control | `bin\InsideDev.Play.dll` in `<game>\_mod\` (needs the MCP server) | the 15 play tools (`observe`, `act`, `move_to`, `interact`, `wait_until`, `time`, `checkpoint`, `menu`, `screenshot`, `guide`, `notes`, `restore`, `input_track`, `geometry`, `film`), the dev world tools, the `ctl` console commands, the live director camera `campath`, the game-render video `grec`, the real-time recorder `rec`, the 360 degree panorama `pano` and the `vsync` switch | [PLAY.md](PLAY.md) |
| Scene recorder and viewer | core | `srec record`, `srec load` and the recording viewer | [RECORD_REPLAY.md](RECORD_REPLAY.md), design notes [REPLAY_DESIGN.md](REPLAY_DESIGN.md) |
| MultiCam | core (+ optional `bin\InsideReadback.dll` in `<game>\INSIDE_Data\Plugins\`) | multi-camera capture, replay passes from a recording, specs in `specs\` | [MULTICAM.md](MULTICAM.md), design notes [MULTICAM_DESIGN.md](MULTICAM_DESIGN.md) |
| Native readback | `bin\InsideReadback.dll` in `<game>\INSIDE_Data\Plugins\` | GPU copy of the game's frames: needed by `grec`, by the default source of `rec` / `time {op:"record"}` and the `film` video ops; faster MultiCam grabs | [PLAY.md](PLAY.md) section 4.6, [MULTICAM.md](MULTICAM.md) |
| Game audio capture | core (+ optional `tools\muter\InsideMuter.exe`) | `acap` WAV capture with sync markers; InsideMuter mutes the game in Windows while it runs | [AUDIO.md](AUDIO.md) |
| Lab modules | `bin\InsideDev.ReplayLab.dll`, `bin\InsideDev.Det.dll` in `<game>\_mod\` | `lab l1` ... `lab l12` engine measurements, `det` input determinism runs | [LAB.md](LAB.md) |
| Python tools | `tools\*.py` (kept in the repository folder) | command-line control (`inside_ctl.py`, library `inside_link.py`), recording reader, MultiCam spec check, compositor, QA, audio alignment, determinism compare | [TOOLS.md](TOOLS.md) |
| Guide playbooks | `guide\` in `<game>\_mod\guide\` | Markdown playbooks for the MCP `guide` tool | [PLAY.md](PLAY.md) section 9, [TOOLS.md](TOOLS.md) section 5 |
| MCP design notes | | architecture, JSON shapes, threading and clock model of the MCP server and Play (dated notes; the references above are current) | [MCP_PLAY_DESIGN.md](MCP_PLAY_DESIGN.md) |

The character control (play profile) works on its own: it needs only the MCP server and Play. The recorder, MultiCam,
audio capture and the lab modules are separate features that you use only when the task asks for them.

---

## 2. How to reach the running game

| Way | Use it for | Needs |
|---|---|---|
| MCP `http://127.0.0.1:47811/mcp/play` | playing: observe, move, use objects, checkpoints | Mcp + Play |
| MCP `http://127.0.0.1:47811/mcp` (= `/mcp/dev`) | inspecting and editing: objects, FSMs, signals, loads, saves, camera, audio, render, reflection, filming; `command` runs any console or bridge command (`commands:[...]` several, `wait:"<status command>"` waits for a result) | Mcp (world tools need Play) |
| MCP `http://127.0.0.1:47811/mcp/all` | both tool sets (41 tools: too many for a client that takes at most 40) | Mcp + Play |
| stdio `tools\mcp\InsideDev.McpStdio.exe --profile play\|dev\|all` | clients that start a local program; can be connected before the game starts (it always lists `inside_status`) | Mcp |
| `POST http://127.0.0.1:47811/cmd` with one command as the text body; `POST /cmds` with several; `GET /status` | scripts without an MCP client (`tools\inside_ctl.py` wraps them) | Mcp |
| File bridge: write `<game>\_mod\cmd\in_<x>.txt` with `<id>\|<command>` lines, read `<game>\_mod\cmd\out_<id>.txt` (or `out.txt`) | works with the core alone, also when the MCP server is off | core |
| In-game console (backquote key) | a person typing; runs every command, the bridge words included | core |

File bridge rules: the id is at most 40 characters and has no blank; one command per line, `#` starts a comment line;
write the file under another name and rename it to `in_*.txt` when it is complete; a command file older than 30 s is
dropped unrun. `out_<id>.txt` starts with `status: ok` or `status: error`, then `id`, `command`, `frame`, `time`, an
empty line and the reply; it is removed after 10 minutes. `out.txt` collects every reply and is deleted when it passes
2 MB, so start reading at 0 again when it got shorter.

Bridge and `command` quoting: words are split at spaces, double quotes group words and are removed. Paths: `srec` takes a
relative folder under `<game>\_mod\srec\`, `grec` / `pano` / `rec` a bare file name under `<game>\_mod\recordings\`, `det`
relative to `<game>\_mod\det\`, `mcam` without an out folder writes to `<spec folder>\takes\`; give other paths as
absolute paths (relative ones resolve against the game's working folder).

Client configuration examples for Claude Code, Claude Desktop, Cursor, VS Code, Codex and Gemini CLI:
`tools\mcp\clients\`. An HTTP client must connect while the game runs; reconnect it after a late game start.

---

## 3. Check what is installed

| Ask | Through | Answer tells you |
|---|---|---|
| `ping` | console, bridge, `command` | `pong  InsideDev 0.11.0` (two blanks): the core version |
| `GET /ping` | HTTP | `pong InsideDev 0.11.0+mcp.<id>[.play.<id>]`: the MCP server build; answers from the server thread, so it does not prove the game runs |
| `GET /status` | HTTP | server and game state as JSON without waiting for the game: versions (`version_problem` when DLLs differ), tool counts, `main_thread.alive`, `window.minimized`, the control job, the last tool error |
| `doctor [json]` | console, bridge, `command` | one self-check of every part (loader, modules and versions, debugger, disk, ffmpeg and encoders, window, time scale, background mode, MCP, Play, recorders) with a fix per problem; first line `doctor: ok\|warn\|fail (N check(s), F fail, W warn)` |
| `help [command]` | console, bridge, `command` | every command, or the usage of one |
| `cfg [filter] [json]` | console, bridge, `command` | every setting with value, default and meaning |
| `ext [json]` | console, bridge, `command` | the loaded extensions (MCP server, Play, ReplayLab, Det) with their cost and levels |
| `mcp` / `mcp status json` | console, bridge, `command` | server state, port, `tools: play N, dev N, all N` (with Play: 15 / 34 / 41), build ids, sessions |
| `inside_status` | stdio launcher | whether the game is up, the endpoint, tool counts, whether the main thread is alive, and the game's instructions |
| `acap status [json]` | console, bridge, `command` | capture output (`audible` or `muted`), game volume |
| `python -I tools/inside_ctl.py doctor` | from outside | the game's `doctor`, or, when the game is not reachable, what is seen from outside (game process, Steam client, port, game window, file bridge) |

---

## 4. Switches and defaults

Nothing added since 0.7.0 runs until it is called or its optional file is installed, and since 0.11.0 the core's
scanners sleep while nobody uses the tool. Settings are `key=value` lines in `<game>\_mod\editor.cfg`; `cfg` reads and
changes them while the game runs. The `mcp.*` keys, `play.cheats`, `play.save_guard`, `play.guide_dir`, `ffmpeg.path` and
any key with `token` or `password` are protected: an agent cannot change them through MCP or `POST /cmd` (they are refused
with `refused: ...`). Other keys can be changed through `command` (`cfg set`, `acap output`, `ui scale` ...): change one
only when the task asks for it, and say so.

| Feature | Default | Switch on | Switch off |
|---|---|---|---|
| Background scanners of the core | `auto`: only while the tool is used (a call or panel in the last 30 s) | `background always` | `background auto`, `background off` |
| MCP server | on when `InsideDev.Mcp.dll` is installed | `mcp on` | `mcp off` (saved as `mcp.enabled`) |
| MCP port, token, allowlist, host names | 47811, none, every tool, none | `mcp port <n>`, `mcp token new`, `mcp tools <list>`, `mcp hosts <names>` (in-game console or file bridge only) | `mcp token clear`, `mcp tools clear`, `mcp hosts clear` |
| MCP restore | 600 s after the last tool call, 60 s after the last client went away; waits while a recording runs | `mcp.idle_restore_s`, `mcp.restore_on_close_s` | `0` = never |
| Character control | idle until a play tool acts | install `InsideDev.Play.dll` | `restore`, or remove the DLL |
| Step mode | set by the first action call (`act`, `move_to`, `interact`) | `time {op:"step_mode"}` | `time {op:"realtime"}`, `restore` |
| Agent input (pad and keyboard ignored) | set by the first action | an action, `input_track`, `ctl` input | `restore`, `act {input_mode:"human"}`, `ctl mode human` |
| vSync (Play) | `play.vsync_off=1`: off only while an agent's calls arrive or the clock is held | `vsync off`, `time {op:"vsync", on:false}` | `vsync on`, `time {op:"vsync", on:true}` (wins until `vsync auto` or restore) |
| Held input released after quiet time (Play) | off (`play.hold_timeout_s=0`) | `play.hold_timeout_s=<s>` | `0` |
| Notes hotkey (with Play) | Home | `play.notes_key=<KeyCode>` | `play.notes_key=none` |
| Checkpoint jumps (`checkpoint next/prev/goto`) | off | `play.cheats=1` | |
| Save guard (backups, confirms) | off | `play.save_guard=1` | |
| Guide spoiler cap | `solution` | `play.guide_max=hint\|steps` | |
| Game hotkeys (F7 god, F8 kill, time scale, middle-click teleport) | on, also with the editor closed | `hotkeys.game=1` | `hotkeys.game=0` |
| Scene recorder, viewer | off | `srec record start`, `srec load` | `srec record stop`, `srec end` |
| MultiCam | off | `mcam start\|take\|replay\|rehearse\|preview` | `mcam stop`, `mcam abort` |
| Overwriting a take folder | refused | `overwrite=on` | leave it out |
| Free camera, HUD, clean frames, gamma | off | `cam pose`, `cam look`, `hud off`, `clean on`, `gamma <v>` | `cam off`, `hud on`, `clean off`, `gamma reset` |
| Audio capture | off, output `audible` (the sound plays) | `acap start`, `srec record ... audio=on` | `acap stop` |
| Muted capture output | off | `acap output muted` (only with the user's OK, section 5) | `acap output audible` |
| InsideMuter | not running | the user starts `InsideMuter.exe` (it mutes the game) | `InsideMuter stop`; it exits 10 s after the game and un-mutes what it muted |
| Real-time recorder (Play) | off; source = the game's own frames | `rec start`, `time {op:"record"}` | `rec stop`, `time {op:"record_stop"}`, `restore` |
| Live director camera (Play) | off | `campath load <dir>`, `campath arm` / `start` | `campath stop`, `restore` |
| Game-render video (Play, needs `InsideReadback.dll` and ffmpeg) | off | `grec start [file.mkv]`, `film {op:"grec_start"}` | `grec stop`, `film {op:"grec_stop"}`, `restore` |
| Panorama (Play) | off | `pano [file.png]`, `film {op:"pano"}`, one capture | ends by itself; `restore` during a capture |
| First-person free-look limits (Play) | off | the last two arguments of `campath mount`, or `free=up,down` | leave them out or `0` |
| Hide renderers to measure them | off | `perf hide <root>[,<root>...] [name part \| mesh:<prefix>]` | `perf show` (not `restore`) |
| Mono debugger port | off | the user creates `_mod\debugger.flag` | delete it |
| Lab modules | not installed | copy the DLLs, `lab ...`, `det ...` | `lab stop`, `det abort`, delete the DLLs |

---

## 5. Rules that keep the user's game and ears safe

- **Never mute the game unless the user asks.** Do not switch `acap output muted`, do not start InsideMuter, do not add
  `audio force rtpc rtpc_master_volume 0` to a spec on your own. The default capture output plays the sound normally.
- **The muted output and the lab audio stages need the user's explicit OK, with headphones on.** Inside a muted capture
  window the master volume is raised; the speakers stay silent only while InsideMuter keeps every INSIDE session muted.
  Never create the flag files `acap_suspend_ok.json` / `acap_muteronly_ok.json` and never pass `confirm` to a lab stage
  yourself ([AUDIO.md](AUDIO.md) section 9, [LAB.md](LAB.md)).
- **Saves.** Playing writes `INSIDE.sav` (the Steam Cloud save) at checkpoints like a person playing; loads through the
  tools can too. Back it up first when it matters (dev `save {op:"backup"}`), or ask the user to set `play.save_guard=1`.
- **Settings and the game window.** Do not change settings (`cfg set`, `background`, `hotkeys.game` ...) and do not send
  `quit` unless the task asks for it. Do not touch the game window from outside (no OS keystrokes, no window moves).
  Input goes through the tools.
- **Put the game back.** Call `restore` when you are done with the play tools (`restore {dry_run:true}` says what it would
  undo); end the recording viewer (`srec end`), the free camera (`cam off`), clean frames (`clean off`); after a MultiCam
  replay run the game stays frozen until `ts 1`.
- **Do not overwrite a take.** A folder that already holds a recording is refused; add `overwrite=on` only when the user
  wants that take replaced.

---

## 6. Workflows

**First contact.** `GET /status` (or `inside_status` over stdio): is the game up, is the main thread alive, is the
window minimized, which tools are there. Then `doctor` through `command` (or `python -I tools/inside_ctl.py doctor`) and
fix what it reports before anything else. If nothing answers: the game is not running, the Steam client was not running
when it started (the game then closes itself after about 2 s), or the MCP server is off (`mcp on` in the console).

**Play a scene (MCP play profile).**

1. `observe` (concise) to see the body, objects with ids, hazards and the checkpoint; later `observe {detail:"delta"}`
   lists only what changed since your last observation.
2. `guide` with the topic from `observe.checkpoint.guide` for the place, hint level first.
3. `act` / `move_to` / `interact` with ids from the latest observe (`act {seq:"R1.2 J R0.4+J"}` for a short script;
   `wait_ready_s` lets an action wait for a load or fade instead of being refused); `wait_until` for a condition;
   `observe` again.
4. After death or a load: wait for the requested checkpoint (`checkpoint` with `wait`, or
   `wait_until {any:[{ready:true}]}` and check `observe.checkpoint`; see PLAY.md section 7.6), then observe again.
   `checkpoint {op:"goto", name:"..."}` (with `play.cheats=1`) loads a checkpoint by name.
5. `restore` at the end. Details: [PLAY.md](PLAY.md) sections 5 and 7.

**Script the boy without an MCP client.** `ctl run right 1.2`, `ctl seq "R1.2 J R0.4+J G1 W0.5 L0.3"`, `ctl status`
through the console, the bridge, `POST /cmd` or `python -I tools/inside_ctl.py ...` ([PLAY.md](PLAY.md) sections 11-12).
`python -I tools/inside_ctl.py wait` waits until the game is up and the world is loaded.

**Run several commands at once.** `command {commands:["boypos", "ctl status"], stop_on_error:true}` or `POST /cmds` with
one command per line: one request, one result per line. For a command that finishes later, `wait` asks a status command
until it is done: `command {command:"pano shot.png", wait:"pano status"}`, `POST /cmd?wait=grec%20status`.

**Record a take and look at it.** Load, wait for the savepoint (`state`), `srec record start <dir>`, play, `srec record
stop keep`, `srec load <dir>`, `srec seek`, fly the free camera, `srec end`; offline `python -I tools/srec_tool.py verify
<dir>` ([RECORD_REPLAY.md](RECORD_REPLAY.md) section 3).

**Film a scene from many cameras.** Write a spec (start from `specs\template_minimal.json` or `specs\replay_smoke.json`),
`python -I tools/mcam_spec_check.py <spec>`, `mcam check`, `mcam preview`, then `mcam start` (live) or `mcam take`
(record once, film every camera from the recording), then `python -I tools/mcam_compose.py doctor <take>`,
`python -I tools/mcam_compose.py all <take>` and `python -I tools/mcam_qa.py qa <take>` ([MULTICAM.md](MULTICAM.md)
sections 2, 3, 18).

**Film a planned camera over live play.** Record the take as an input track and with `srec`, plan it with
`mcam passes <spec> <dir>`, reset to the same checkpoint, `grec start <file.mkv>`, `campath load <dir>\passes director`,
`campath arm`, play the track; `grec stop` when the path ends ([PLAY.md](PLAY.md) sections 4.6 and 7.7).

**Record what the game shows.** `time {op:"record"}` ... `time {op:"record_stop"}` (console `rec start` / `rec stop`) or
`film {op:"grec_start"}` ... `film {op:"grec_stop"}`: the game's own frames, placed by game time in real-time play, with
`<file>.times.csv` for the audio. Windows over the game stay out of the video.

**Take a 360 degree panorama.** Best in the recording viewer, where nothing moves: `srec load <dir>` (read the reply), `srec pause`,
`srec seek <t>`, `film {op:"pano"}` or `pano <file.png> near=0.05 wait`, then `pano wait` until the result line. One
capture holds the game about 13 frames ([PLAY.md](PLAY.md) sections 4.7 and 7.8).

**Find what costs frame time.** `perf reset`, wait 10 to 30 s without other calls, `perf`; then `perf hide <root>`, compare the
frame rate at the same view, `perf show` ([MCP.md](MCP.md) section 13.1).

**Capture the game's sound.** `acap preflight`, then `srec record start <dir> audio=on` (or `acap start <name>`), stop,
then `python -I tools/mcam_audio.py align <dir>` ([AUDIO.md](AUDIO.md) section 3).

**Measure engine behaviour (lab).** Only with the lab DLLs installed: `lab list`, then the tests in the run order of
[LAB.md](LAB.md) section 2.6; L5 stages only with the user's OK.

---

## 7. Pitfalls

- **The Steam client must run.** Without it the Steam copy of the game closes itself about 2 s after it starts, before
  InsideDev or the MCP server come up.
- **A minimized game window pauses the game.** Calls that need frames fail with `not_ready` or time out, the file bridge
  stops answering, recordings and captures hold. `GET /ping` still answers, so it does not prove the game runs;
  `GET /status` shows `main_thread.alive` and `window.minimized`. Keep the window restored (it may stay behind other
  windows). Without focus the game also pauses until the first action sets `runInBackground` (or `bg on`).
- **The tool sleeps while unused.** With no call and no panel for 30 s the core's scanners stop and catch up on the next
  call; `perf` and the audio log then show the gap. `background always` keeps them running (only when the task needs it).
- **Load races.** After a load, `wait_until {any:[{ready:true}]}` can be satisfied by the old level. Wait until the
  requested savepoint is current (`observe.checkpoint`, `state`, `waitfor savepoint ...` in a spec) before acting, arming
  a recording, starting an input track or a take. The short form `wait_until {ready:true}` is not valid.
- **Step mode freezes the game between calls.** A person watching sees a still frame; `time {op:"realtime"}` or `restore`
  lets it run. Music-timed places drift from the music in step mode and slow motion.
- **MCP idle restore.** 600 s without a tool call (`mcp.idle_restore_s`) and 60 s after the last client went away
  (`mcp.restore_on_close_s`) the server restores the game: it cancels the running action, releases the input, sets the
  clock to realtime and gives vSync back. It waits while a recording, a capture, a panorama or an input track recording
  runs. Console and bridge commands do not reset this clock; keep calling a cheap tool during long work.
- **Input tracks: lockstep vs free play.** `input_track {op:"play"}` holds the rehearsal's 60 Hz lockstep by default
  (one logic step per frame, exact); `lockstep:false` plays in free realtime, where game time follows the wall clock and
  the result can differ. Detached jobs end after 45 minutes, so longer playbacks are cut; a lockstep take on a busy
  machine can need much more wall clock than its length. `input_track {op:"status"}` has `last_play` with the `end`
  (`finished`, `cancelled`, `restore`, `level_load`, `died`, `stalled`, `desynced`, `error`) and `stop_reason`.
- **Ids after a load.** An id of an object that exists again at the same path resolves again; `stale_id` means the
  object is gone: observe again.
- **Names are matched loosely.** `set_active`, `fsm`, `signal` with a plain name act on the matching object nearest the
  boy and report `matches` / `candidates`; pass `strict:true`, a path or `#<id>` for anything that cannot be undone.
- **Busy.** One control job at a time (actions, waits, loads, ctl jobs, a MultiCam capture). A second request gets
  `busy` with the running job; `wait_until {any:[{job:"jN"}]}` waits for it. `restore` does not cancel a MultiCam
  capture (`mcam stop` does).
- **The free camera blocks the body.** While it is on (Insert, `cam pose`, `srec load`, dev `camera pose`) the boy gets
  no input; release it first.
- **Recording needs the readback plugin.** `grec`, `rec start` and `time {op:"record"}` (game frames) need
  `InsideReadback.dll` in `<game>\INSIDE_Data\Plugins\` and ffmpeg; they are refused while an `mcam` pass holds the
  readback. In real-time play `grec` places frames by game time, so the video length equals game time whatever the frame
  rate; with an agent stepping the game, frozen stretches are left out. Line audio up with `<file>.times.csv`.
- **`pano` uses the free camera** and gives it back, also after `restore`. In live play the game keeps running during the
  capture unless `hold` (the default) holds it for the six faces; the recording viewer is the calm place.
- **`perf hide` survives `restore`.** Always end with `perf show`.
- **Keyed cameras (MultiCam).** The player-side clamp applies only under pitch 45, so a keyed path crossing 45 degrees behind
  the line jumps for a frame; two equal keys under `catmull` are not a hold; run `mcam passes` without core pinning
  ([MULTICAM.md](MULTICAM.md) section 12 and section 16). First-person flicker: [PLAY.md](PLAY.md) section 15.
- **After a MultiCam replay run** the game stays frozen (`ts 1` resumes) unless the spec says `restore: "reload"`.
- **Updating a DLL.** `ext load` loads new DLLs only; replacing a loaded one needs a game restart.
- **41 tools on `/mcp/all`.** A client that takes at most 40 tools fails there: use `/mcp/play` or `/mcp/dev`.
- **Paths.** See section 2: `srec`, `grec`, `pano`, `rec`, `det` and `mcam` have their own default folders; other
  relative paths resolve against the game's working folder.

---

## 8. Where files go

| What | Where |
|---|---|
| Settings | `<game>\_mod\editor.cfg` (comment lines are dropped when the game rewrites it) |
| Loader log (first place to look when nothing loads) | `<game>\_mod\hook.log`, written anew at every start |
| Editor log | `<game>\_mod\insidedev.log`, written anew at every start |
| File bridge | `<game>\_mod\cmd\` (`in_*.txt`, `out.txt`, `out_<id>.txt`, `state.txt` while command files arrive) |
| MCP audit log of changing calls | `<game>\_mod\mcp_audit.jsonl` (rotates at 5 MB) |
| MCP screenshots | `<game>\_mod\shots\` (newest 200, `.png` or `.jpg`), resource `inside://shots/<file>` |
| Notes (Play) | `<game>\_mod\notes.jsonl`, `<game>\_mod\notes\` |
| Input tracks (Play) | `<game>\_mod\input_tracks\<name>.json` |
| Save backups (Play) | `<game>\_mod\save_backups\` |
| `grec`, `pano`, `rec` files given without a folder | `<game>\_mod\recordings\` |
| Scene recordings | the folder you pass to `srec record start`; a relative one or none: `<game>\_mod\srec\` |
| MultiCam takes | the folder you pass to `mcam ...`; none: `<spec folder>\takes\<spec name>_<date>_<time>` |
| Manual audio captures | `<game>\_mod\audio\` |
| Lab results | `<game>\_mod\replay_tests\<test>\` (`lab out <dir>` changes it) |
| Determinism runs | `<game>\_mod\det\<name>\` |
| Compositor and QA output | `--out` if given, else `MCAM_OUT\<take>`, else the spec's `compose.output.root`, else the folder an earlier version used (`<repo>\out\<take>`, only when it exists), else `<take>\out` |
