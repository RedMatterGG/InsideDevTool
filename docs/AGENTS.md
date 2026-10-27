# InsideDev for AI agents: start here

This page is the entry point for an AI agent that works with INSIDE (Playdead, Unity 5.0.4f1) through InsideDev. It says
what each part does, how to reach the game, what is on and off by default, which reference to read for a task, and the
pitfalls that cost the most time. Every statement here is detailed in the linked reference; when the two differ, the
reference and the code win.

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
| Editor (core) | `bin\InsideDev.dll` in `<game>\_mod\` | the in-game editor, console and file bridge; the `srec`, `mcam`, `acap`, `cam`, `hud`, `clean`, `gamma`, `seed` commands | README.md, [RECORD_REPLAY.md](RECORD_REPLAY.md), [MULTICAM.md](MULTICAM.md), [AUDIO.md](AUDIO.md) |
| MCP server | `bin\InsideDev.Mcp.dll` in `<game>\_mod\` | MCP on `http://127.0.0.1:47811` with the dev, play and all profiles; plain `POST /cmd`, `GET /state`, `GET /ping` | [MCP.md](MCP.md) |
| Character control | `bin\InsideDev.Play.dll` in `<game>\_mod\` (needs the MCP server) | the 14 play tools (`observe`, `act`, `move_to`, `interact`, `wait_until`, `time`, `checkpoint`, `menu`, `screenshot`, `guide`, `notes`, `restore`, `input_track`, `geometry`), the dev world tools and the `ctl` console commands | [PLAY.md](PLAY.md) |
| Scene recorder and viewer | core | `srec record`, `srec load` and the recording viewer | [RECORD_REPLAY.md](RECORD_REPLAY.md), design notes [REPLAY_DESIGN.md](REPLAY_DESIGN.md) |
| MultiCam | core (+ optional `bin\InsideReadback.dll` in `<game>\INSIDE_Data\Plugins\`) | multi-camera capture, replay passes from a recording, specs in `specs\` | [MULTICAM.md](MULTICAM.md), design notes [MULTICAM_DESIGN.md](MULTICAM_DESIGN.md) |
| Game audio capture | core (+ optional `tools\muter\InsideMuter.exe`) | `acap` WAV capture with sync markers; InsideMuter mutes the game in Windows while it runs | [AUDIO.md](AUDIO.md) |
| Lab modules | `bin\InsideDev.ReplayLab.dll`, `bin\InsideDev.Det.dll` in `<game>\_mod\` | `lab l1` ... `lab l12` engine measurements, `det` input determinism runs | [LAB.md](LAB.md) |
| Python tools | `tools\*.py` | command-line control, recording reader, MultiCam spec check, compositor, QA, audio alignment, determinism compare | [TOOLS.md](TOOLS.md) |
| Guide playbooks | `guide\` in `<game>\_mod\guide\` | Markdown playbooks for the MCP `guide` tool | [PLAY.md](PLAY.md) section 9, [TOOLS.md](TOOLS.md) section 5 |
| MCP design notes | | architecture, JSON shapes, threading and clock model of the MCP server and Play | [MCP_PLAY_DESIGN.md](MCP_PLAY_DESIGN.md) |

The character control (play profile) works on its own: it needs only the MCP server and Play. The recorder, MultiCam,
audio capture and the lab modules are separate features that you use only when the task asks for them.

---

## 2. How to reach the running game

| Way | Use it for | Needs |
|---|---|---|
| MCP `http://127.0.0.1:47811/mcp/play` | playing: observe, move, use objects, checkpoints | Mcp + Play |
| MCP `http://127.0.0.1:47811/mcp` (= `/mcp/dev`) | inspecting and editing: objects, FSMs, signals, loads, saves, camera, audio, render, reflection; `command` runs any console or bridge command | Mcp (world tools need Play) |
| MCP `http://127.0.0.1:47811/mcp/all` | both tool sets (40 tools) | Mcp + Play |
| stdio `tools\mcp\InsideDev.McpStdio.exe --profile play\|dev\|all` | clients that start a local program; can be connected before the game starts | Mcp |
| `POST http://127.0.0.1:47811/cmd` with one command as the text body | scripts without an MCP client (`tools\inside_ctl.py` wraps it) | Mcp |
| File bridge: write `<game>\_mod\cmd\in_<x>.txt` with `<id>\|<command>`, read `<game>\_mod\cmd\out.txt` | works with the core alone, also when the MCP server is off | core |
| In-game console (backquote key) | a person typing; runs core console words and extension words (`ctl`, `lab`, `det`), not the bridge-only commands `srec`, `mcam`, `acap`, `cam` | core |

Bridge and `command` quoting: words are split at spaces, double quotes group words and are removed. Give folders as
absolute paths: relative paths resolve against the game's working folder.

Client configuration examples for Claude Code, Claude Desktop, Cursor, VS Code, Codex and Gemini CLI:
`tools\mcp\clients\`.

---

## 3. Check what is installed

| Ask | Through | Answer tells you |
|---|---|---|
| `ping` | bridge, `command`, `GET /ping` | `pong InsideDev 0.7.0`: the core version |
| `ext` | bridge, `command` | the loaded extensions (MCP server, Play, ReplayLab, Det) |
| `mcp` | console, bridge, `command` | server state, port, `tools: play N, dev N, all N` (with Play: 14 / 33 / 40), build ids |
| `inside_status` | stdio launcher | whether the game is up, the endpoint and the game's instructions |
| `acap status` | bridge, `command` | capture output (`audible` or `muted`), game volume |

---

## 4. Switches and defaults

Nothing added in 0.7.0 runs until it is called or its optional file is installed. Settings are `key=value` lines in
`<game>\_mod\editor.cfg`, read once at game start (edit them with the game closed; an agent cannot change them through
MCP).

| Feature | Default | Switch on | Switch off |
|---|---|---|---|
| MCP server | on when `InsideDev.Mcp.dll` is installed | `mcp on` | `mcp off` (saved as `mcp.enabled`) |
| MCP port, token, allowlist | 47811, none, every tool | `mcp port <n>`, `mcp.token`, `mcp.tools` | |
| MCP restore | 600 s after the last tool call, 60 s after the last session closed | `mcp.idle_restore_s`, `mcp.restore_on_close_s` | `0` = never |
| Character control | idle until a play tool acts | install `InsideDev.Play.dll` | `restore`, or remove the DLL |
| Step mode | set by the first action call (`act`, `move_to`, `interact`) | `time {op:"step_mode"}` | `time {op:"realtime"}`, `restore` |
| Agent input (pad and keyboard ignored) | set by the first action | an action, `input_track`, `ctl` input | `restore`, `act {input_mode:"human"}`, `ctl mode human` |
| Notes hotkey (with Play) | Home | `play.notes_key=<KeyCode>` | `play.notes_key=none` |
| Checkpoint jumps (`checkpoint next/prev/goto`) | off | `play.cheats=1` | |
| Save guard (backups, confirms) | off | `play.save_guard=1` | |
| Guide spoiler cap | `solution` | `play.guide_max=hint\|steps` | |
| Scene recorder, viewer | off | `srec record start`, `srec load` | `srec record stop`, `srec end` |
| MultiCam | off | `mcam start\|take\|replay\|rehearse\|preview` | `mcam stop`, `mcam abort` |
| Free camera, HUD, clean frames, gamma | off | `cam pose`, `hud off`, `clean on`, `gamma <v>` | `cam off`, `hud on`, `clean off`, `gamma reset` |
| Audio capture | off, output `audible` (the sound plays) | `acap start`, `srec record ... audio=on` | `acap stop` |
| Muted capture output | off | `acap output muted` (only with the user's OK, section 5) | `acap output audible` |
| InsideMuter | not running | the user starts `InsideMuter.exe` (it mutes the game) | it exits 10 s after the game |
| Lab modules | not installed | copy the DLLs, `lab ...`, `det ...` | `lab stop`, `det stop`, delete the DLLs |

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
- **Put the game back.** Call `restore` when you are done with the play tools; end the recording viewer (`srec end`),
  the free camera (`cam off`), clean frames (`clean off`); after a MultiCam replay run the game stays frozen until `ts 1`.
- **Do not touch the game window from outside** (no OS keystrokes, no window moves). Input goes through the tools.

---

## 6. Workflows

**Play a scene (MCP play profile).**

1. `observe` (concise) to see the body, objects with ids, hazards and the checkpoint.
2. `guide` with the topic from `observe.checkpoint.guide` for the place, hint level first.
3. `act` / `move_to` / `interact` with ids from the latest observe; `wait_until` for a condition; `observe` again.
4. After death or a load: wait for the requested checkpoint (`checkpoint` with `wait`, or `wait_until`; see PLAY.md
   section 7.6), then observe again (ids changed).
5. `restore` at the end. Details: [PLAY.md](PLAY.md) sections 5 and 7.

**Script the boy without an MCP client.** `ctl run right 1.2`, `ctl seq "R1.2 J R0.4+J G1 W0.5 L0.3"`, `ctl status`
through the console, the bridge, `POST /cmd` or `python -I tools/inside_ctl.py ...` ([PLAY.md](PLAY.md) sections 11-12).

**Record a take and look at it.** Load, wait for the savepoint (`state`), `srec record start <dir>`, play, `srec record
stop keep`, `srec load <dir>`, `srec seek`, fly the free camera, `srec end`; offline `python -I tools/srec_tool.py verify
<dir>` ([RECORD_REPLAY.md](RECORD_REPLAY.md) section 3).

**Film a scene from many cameras.** Write a spec (start from `specs\template_minimal.json` or `specs\replay_smoke.json`),
`python -I tools/mcam_spec_check.py <spec>`, `mcam check`, `mcam preview`, then `mcam start` (live) or `mcam take`
(record once, film every camera from the recording), then `python -I tools/mcam_compose.py all <take>` and
`python -I tools/mcam_qa.py qa <take>` ([MULTICAM.md](MULTICAM.md) sections 2, 3, 18).

**Capture the game's sound.** `acap preflight`, then `srec record start <dir> audio=on` (or `acap start <name>`), stop,
then `python -I tools/mcam_audio.py align <dir>` ([AUDIO.md](AUDIO.md) section 3).

**Measure engine behaviour (lab).** Only with the lab DLLs installed: `lab list`, then the tests in the run order of
[LAB.md](LAB.md) section 2.6; L5 stages only with the user's OK.

---

## 7. Pitfalls

- **A minimized game window pauses the game.** Calls that need frames fail with `not_ready` or time out, the file bridge
  stops answering, recordings and captures hold. `GET /ping` still answers, so it does not prove the game runs. Keep the
  window restored (it may stay behind other windows). Without focus the game also pauses until the first action sets
  `runInBackground` (or `bg on`).
- **Load races.** After a load, `wait_until {ready:true}` can be satisfied by the old level. Wait until the requested
  savepoint is current (`observe.checkpoint`, `state`, `waitfor savepoint ...` in a spec) before acting, arming a
  recording, starting an input track or a take.
- **Step mode freezes the game between calls.** A person watching sees a still frame; `time {op:"realtime"}` or `restore`
  lets it run. Music-timed places drift from the music in step mode and slow motion.
- **MCP idle restore.** 600 s without a tool call (`mcp.idle_restore_s`) and 60 s after the last session closed
  (`mcp.restore_on_close_s`) the server restores the game: it cancels the running action, releases the input, sets the
  clock to realtime and stops input track recording and playback. It does not stop an `srec` recording or a MultiCam
  capture. Console and bridge commands do not reset this clock; keep calling a cheap tool during long work.
- **Input tracks: lockstep vs free play.** `input_track {op:"play"}` holds the rehearsal's 60 Hz lockstep by default
  (one logic step per frame, exact); `lockstep:false` plays in free realtime, where game time follows the wall clock and
  the result can differ. Detached jobs end after 10 minutes, so longer playbacks are cut.
- **Ids die on every load and respawn.** Use ids only from the latest `observe` or action result.
- **Busy.** One control job at a time (actions, waits, loads, ctl jobs, a MultiCam capture). A second request gets
  `busy` with the running job; `wait_until {any:[{job:"jN"}]}` waits for it. `restore` does not cancel a MultiCam
  capture (`mcam stop` does).
- **The free camera blocks the body.** While it is on (Insert, `cam pose`, `srec load`, dev `camera pose`) the boy gets
  no input; release it first.
- **After a MultiCam replay run** the game stays frozen (`ts 1` resumes) unless the spec says `restore: "reload"`.
- **Updating a DLL.** `ext load` loads new DLLs only; replacing a loaded one needs a game restart.
- **Paths.** Bridge and `mcam` / `srec` paths should be absolute; relative paths resolve against the game's working
  folder.

---

## 8. Where files go

| What | Where |
|---|---|
| Settings | `<game>\_mod\editor.cfg` |
| Editor log | `<game>\_mod\insidedev.log` |
| MCP audit log of changing calls | `<game>\_mod\mcp_audit.jsonl` (rotates at 5 MB) |
| MCP screenshots (full size) | `<game>\_mod\shots\` (newest 200), resource `inside://shots/<file>` |
| Notes (Play) | `<game>\_mod\notes.jsonl`, `<game>\_mod\notes\` |
| Input tracks (Play) | `<game>\_mod\input_tracks\<name>.json` |
| Save backups (Play) | `<game>\_mod\save_backups\` |
| Recordings, MultiCam takes | the folder you pass to `srec record start` / `mcam ...` |
| Manual audio captures | `<game>\_mod\audio\` |
| Lab results | `<game>\_mod\replay_tests\<test>\` (`lab out <dir>` changes it) |
| Determinism runs | `<game>\_mod\det\<name>\` |
| Compositor and QA output | `<repo>\out\<take>\` unless `--out` is given |
