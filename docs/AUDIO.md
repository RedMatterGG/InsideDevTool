# Game audio capture and InsideMuter

Game audio capture (`acap`) writes the game's own sound output (Wwise) to a WAV file with sync markers, alone or as part of
a scene recording (`srec record start ... audio=on`, [RECORD_REPLAY.md](RECORD_REPLAY.md)) or a MultiCam replay take
([MULTICAM.md](MULTICAM.md)). InsideMuter is a small optional Windows program that mutes INSIDE's sound in Windows while it
runs; only the "muted" capture output and the lab audio test L5 ([LAB.md](LAB.md)) need it. `tools/mcam_audio.py` checks
and aligns captured audio offline.

**Game sound is never muted by default.** The default capture output is "audible": the game plays its sound normally while
it is captured. The "muted" output and InsideMuter are opt-in.

Contents

1. Install and what is off by default
2. Where the commands run
3. Workflows
4. The two outputs
5. `acap` commands
6. Files
7. Muted output: what is checked
8. Flag files
9. Consent rule for the muted output
10. Getting the volume back
11. InsideMuter
12. `tools/mcam_audio.py`
13. Pitfalls
14. Settings reference

---

## 1. Install and what is off by default

`acap` is part of `bin\InsideDev.dll`, installed to `<game>\_mod\` (see README.md). InsideMuter is optional:

| File in the release | Copy to | Runs | Off by default | Uninstall |
|---|---|---|---|---|
| `bin\InsideDev.dll` (acap commands) | `<game>\_mod\` | only when an `acap` command, `srec record ... audio=on` or a MultiCam replay spec with audio is used | yes | |
| `tools\muter\InsideMuter.exe` | `<game>\_mod\muter\` | only when you start it | yes: it never starts by itself | `InsideMuter.exe stop` (or close it), delete the file |

| Feature | Starts with | Stops with | Saved between game sessions |
|---|---|---|---|
| Audio capture | `acap start <name>`, `srec record start ... audio=on`, or a spec with `replay.audio.capture: "record"` | `acap stop`, the end of the recording | the WAV files only |
| Audio output choice | `acap output audible\|muted` | `acap output audible` | yes: `acap.output` in `<game>\_mod\editor.cfg`, default `audible` |
| InsideMuter | starting `InsideMuter.exe` | it exits 10 s after the last INSIDE.exe, `InsideMuter.exe stop`, or close it; since 0.11.0 it un-mutes what it muted when it ends | nothing (its heartbeat file only) |

A user who never calls `acap` and never starts InsideMuter sees no change: no audio rule, no muting.

Building: `sh build/build_muter.sh` (needs the .NET Framework 4.x reference assemblies, found automatically on Windows).

---

## 2. Where the commands run

`acap` is a bridge command: send it through the file bridge (`<game>\_mod\cmd\in_<anything>.txt`, reply in
`<game>\_mod\cmd\out.txt`), the MCP `command` tool, `POST /cmd` or `tools\inside_ctl.py`. Since 0.11.0 the in-game console
hands it to the bridge too. Details of the bridge and of quoting: [RECORD_REPLAY.md](RECORD_REPLAY.md) section 2.

Failure replies start with an error word (changed in 0.11.0): `usage: acap start <name> ...: <what>`, `refused: <reason>`
(was `audio capture refused: <reason>`), `failed: audio capture stopped at once: ...`, `refused: no capture running`
(`acap mark`), `failed: Suspend(true): ...`, `failed: acap <sub>: <exception>`. `acap stop` with nothing to stop still
answers `no capture running`.

---

## 3. Workflows

### 3.1 Record a scene with game audio (default output "audible")

```
acap status                                   "output audible", the game volume
acap preflight                                "ok" or the refusal reason
srec record start C:\path\to\takes\take2 audio=on
                                              ... play ...
srec record stop
```

The game sound plays normally and is captured at the game's own volume. After the stop the WAV is moved into the take
folder (`audio.wav`, `audio_sync.csv`, `audio_capture.json`; this can take up to 10 s). Then, offline:

```
python -I tools/mcam_audio.py info  C:\path\to\takes\take2
python -I tools/mcam_audio.py align C:\path\to\takes\take2      writes the fit into record.json "audio"; exit 0 = usable
```

### 3.2 Audio only

```
acap start scene1                             capture to <game>\_mod\audio\scene1.wav, marker s0
acap mark door                                marker "door" in the WAV and a row in scene1_sync.csv
acap stop
acap result                                   the summary and the path of scene1_capture.json, once it is final
python -I tools/mcam_audio.py align <game>\_mod\audio\scene1.wav    writes scene1_align.json
```

### 3.3 Sound for a grec video

Play's `grec` films the game's own frames ([PLAY.md](PLAY.md) section 4.6); `acap start` captures the sound next to it.
Start both, play, stop both. `grec` writes `<file>.times.csv` (each video frame's absolute game time and real time), and
`<name>_capture.json` has `clockBase` (the absolute real time of marker `s0`) and `gameBase` (new in 0.11.0: the absolute
game time of `game_time` 0 in the sync file). In real-time play the video starts at `P + (R0 - clockBase)` seconds into
the WAV, where P is the WAV position of `s0` (`mcam_audio.py info`) and R0 the `real_time` of the first row of
times.csv. At a capture rate and in an agent's step mode the WAV runs on wall time while the video follows game time:
line them up by markers or by the `real_time` column ([PLAY.md](PLAY.md) section 4.6.1 has the three cases).

---

## 4. The two outputs

`acap output` chooses how a capture treats the sound you hear. It is saved as `acap.output` in `<game>\_mod\editor.cfg`.

| Output | Default | What you hear | Needs |
|---|---|---|---|
| `audible` | yes | The game sound plays normally. Nothing is muted, the master volume is never set to 0. | nothing extra |
| `muted` | no | Nothing of INSIDE reaches the speakers or headphones: the master volume `rtpc_master_volume` is forced to 0 from the first `acap` command, `srec record ... audio=on` or mcam spec with a `replay` block until the game exits or you switch back with `acap output audible`, and is lifted only inside a capture window while InsideMuter keeps every INSIDE session muted in Windows. For capturing while your headphones must stay silent (for example while you listen to something else on the same device). | InsideMuter running with a fresh heartbeat (section 11), a flag file that only a passing lab stage writes (section 8), and the consent of section 9 |

Audible output in detail: the capture window opens at once at min(`level`, game volume), where the game volume is the
game's own setting x 100 (or the Wwise value if the setting cannot be read), so the master volume is never raised. While
the window is open the master volume is held at that level. At the end acap puts back the rule that was there before (or
sets the game volume and removes its own rule). No heartbeat, no Suspend, no flag files; `acap suspend on` and `acap
rtpctest` are refused.

## 5. `acap` commands

`acap` alone is `acap status`; `acap help` prints the usage. During an mcam run the capture lock lets only the full
`acap status` through: a bare `acap` gets the busy answer there.

| Command | Arguments (defaults) | Effect |
|---|---|---|
| `acap status [json]` | | State (`idle`, `starting`, `arming`, `window`, `finalizing`), output, current capture (file, size, markers, errors), game volume and acap's own rule (audible) or the heartbeat (muted), the `rtpc:rtpc_master_volume` rule and the Wwise value, flags (muted), counters, last error, last capture, last rtpctest. It never changes the volume (0.10.0 could mute the game from `acap status`). `json` (new) is one line: `{"state", "output", "guard" (off \| on \| ending), "capturing", "windowOpen", "level", "suspended", "gameVolume", "gameVolumeSource", "rule", "ruleIsAcaps", "ruleBefore", "wwiseReadback", "readbackPending", "nativeEnforce", "changesPerForce", "capture":{..}, "counters":{..}, "lastError", "lastCapture", "lastCaptureInfo", "lastTest"}`. |
| `acap result [json]` | | New: the last finished capture: `acap result: <summary>` and the path of its `audio_capture.json`; `json` = that file's object. `busy: finalizing <name> ...` while it is written, `not found: no capture finished in this session`. |
| `acap guard [on\|off]` | | New: `off` releases acap's rule 0 on `rtpc_master_volume` (`guard off: acap's rule 0 on rtpc_master_volume released (...); nothing is forced until the next preflight, start, rtpctest, suspend on or acap guard on`); `on` subscribes the guard again. Refused while a capture, window, rtpctest or wake is active. |
| `acap output [audible\|muted]` | | Without argument prints the output and the raw editor.cfg value. Switching is refused while a capture, window, rtpctest or wake is active. Switching to `audible` removes acap's own volume rule; switching to `muted` forces the master volume to 0 at once. |
| `acap preflight [suspend=auto\|on\|off] [muteronly=on] [hb=<path>] [hbage=2]` | | Runs the checks of a capture without starting one. Prints `ok` or `refused: <reason>`, then one line per check. In the audible output only the basics are checked (no capture or rtpctest running, the sound engine wrapper installed, Wwise not suspended by acap, the target folder); `suspend`, `muteronly`, `hb`, `hbage` matter only for `muted`. |
| `acap start <name> [suspend=...] [muteronly=on] [level=100] [hb=<path>] [hbage=2]` | name: ASCII letters, digits, `_`, `-`, at most 64 | Manual capture to `<game>\_mod\audio\<name>.wav`, posts marker `s0`. Refused while an mcam run or any recording is active (a recording captures through `srec record start ... audio=on`), or when the file exists. `level` 0-100 (`level needs a number 0..100, got '5O'`). Since 0.11.0 `muteronly` takes `on` / `off` (also `true/false`, `1/0`, `yes/no`; another value is an error, 0.10.0 read it as off), and an unknown key is an error: `unknown argument 'levle' (valid: suspend, muteronly, level, hb, hbage)`. The same holds for `acap preflight`. |
| `acap mark <ascii>` | | Marker in the WAV (cue + label) and a row in the sync file; characters other than letters, digits, `_ - . : +` become `_`, at most 64. |
| `acap stop` | | Stops the capture: volume first (audible: put back; muted: forced 0), then the capture; the file is finalized within up to 10 s. |
| `acap suspend on\|off` | muted only | `on`: AkSoundEngine.Suspend(true); Wwise stays suspended until the game exits or `off`. `off`: refused during a capture, window or rtpctest; forces the volume to 0, waits until Wwise reads 0 and 100 ms passed, then WakeupFromSuspend. |
| `acap rtpctest [level=100]` | muted only, level in (0, 100] | Checks that one rule change reaches Wwise: 0 -> level -> 0 with a readback after each. Needs a fresh heartbeat and either a quiet suspended Wwise or the muter-only flag. Result in `acap status` and `<game>\_mod\audio\rtpctest_<time>_<native\|managed>.json`. It lifts the master volume: same consent rule as a muted capture (section 9). |
| `acap changes 1\|2` | default 2 | Rule changes per volume force (2 works around a native-enforcement ordering issue; the readback decides either way). Not saved. |

Every other `acap` command first subscribes the capture guard for the rest of the game session (and, with the muted
output, forces the master volume to 0). Since 0.11.0 `status`, `help`, `result`, `output` and `guard` without a value,
and `changes` start no guard and write no rule, and acap's own volume rule is never recorded into a mod or the History.

## 6. Files

| Capture | WAV | Sync rows | Capture info |
|---|---|---|---|
| `acap start <name>` | `<game>\_mod\audio\<name>.wav` | `<name>_sync.csv` | `<name>_capture.json` |
| `srec record start ... audio=on`, mcam record | `<dir>\audio.wav` (written to `<game>\_mod\audio\rec_<time>.wav`, moved after the stop) | `<dir>\audio_sync.csv` | `<dir>\audio_capture.json` |

Existing files of the same name are kept as `<file>.old_<time>`. Wwise writes the file through a path relative to its
sound bank folder (`<game>\INSIDE_Data\StreamingAssets\Audio\`), so that path must stay under 240 characters; a very deep
game folder can exceed it. In development the WAV was PCM 48000 Hz, 2 channels, 16 bit.

- `audio_sync.csv`: `marker,label,sample,game_time,real_time,lag`. Labels `s<sample>` (a recording posts `s0` and then one
  every 30 samples; `acap start` posts `s0`), your `acap mark` labels, `wopen`/`wclose` (capture window, sample -1, lag
  -1). `real_time` is seconds since the recording's start (or since `acap start`); `lag` is 0, or 1 when the marker was
  posted after that frame's audio render.
- `audio_capture.json` (`format "acap.capture/1"`): name, files and paths, `output` (audible or muted), `path`
  (`audible`, `suspend` or `muteronly`), game and spec level (audible), `level`, times (`startReal`, `appearedAfterS`,
  `windowOpenReal`, `windowCloseReal`, `stopReal`, `wallSeconds`), marker counts, heartbeat statistics (muted), volume
  readbacks, the RIFF check `wav`, `needsFix` (header broken: run `mcam_audio.py fix`), `stopReason`, `error`, `notes`, and
  the clock bases `clockBase` (absolute `realtimeSinceStartupDbl` of `real_time` 0) and, new in 0.11.0, `gameBase`
  (absolute `Time.timeDbl` of `game_time` 0: the srec header `t0` when srec or mcam drives the capture, the game time at
  `acap start` for a manual capture), plus `silent` (bool), `stalls` and `maxNoGrowthS` (how long the file did not grow).
- `<take>\audio.wav.part` exists only while the WAV is copied to another drive.
- A recording whose capture is closed early (guard) continues without sound; record.json `audio.closedEarly` is true.

## 7. Muted output: what is checked

All of these must hold for a muted capture. The heartbeat is `heartbeat.json` of InsideMuter; default
`_mod/muter/heartbeat.json` relative to the game folder (`hb=` overrides it, `hbage=` sets the age limit, default 2 s).

| Gate | Check |
|---|---|
| Capture path | `suspend=on`, or `suspend=auto` (default) with `<game>\_mod\acap_suspend_ok.json` present: Suspend path (Wwise renders into a dummy sink, nothing reaches Windows). Otherwise the muter-only path: needs `muteronly=on` (spec `allowMuterOnly: true`) and `<game>\_mod\acap_muteronly_ok.json`, and Wwise not suspended by acap. |
| Heartbeat | Parsed; younger than `hbage` (from its `unixMs`); `mode` run; `status` running; lists this INSIDE.exe; no unmuted INSIDE session; a session meter peak above 0 only if every session is muted (the meter reads before the mute); at least one muted session of this process unless Wwise is suspended. |
| Volume | The `rtpc:rtpc_master_volume` rule is 0 and Wwise reads 0. |
| Target | `<game>\_mod\audio\` writable, shares a root with the bank folder, path short enough. |
| Window open | The file must appear within 2 s. Suspend path: the heartbeat must show no active INSIDE stream and peak 0 for 1 s (at most 15 s). Then the volume is forced to `level` and read back from Wwise. |
| Window guard | Every frame: the rule must keep the window level; the heartbeat (read at most every 250 ms) must keep passing, `unmutedSeenTotal` must not grow, no active stream while suspended; Wwise is read back every 10th frame. Any failure closes the window at once (volume 0), stops the capture and records the error. |
| Outside windows | The rule is forced back to 0 every frame; Wwise is read back every 10th frame and a non-zero value is forced to 0 again. |
| Stop and quit | Volume 0 first, then the capture stops. Suspend is never woken automatically (`acap suspend off` only). |

## 8. Flag files

| File | Written by | Unlocks |
|---|---|---|
| `<game>\_mod\acap_suspend_ok.json` | a PASS of lab L5 stage S3 or S3b (S3b also sets `requiresTimerPeriod`, then acap uses `timeBeginPeriod(1)` on the Suspend path) | the Suspend path (`suspend=auto` uses it whenever the file exists) |
| `<game>\_mod\acap_muteronly_ok.json` | a PASS of lab L5 stage S2 | the muter-only path, together with `muteronly=on` / `allowMuterOnly: true` |

Only the file's presence is checked. Delete a file to disable that path. The audible output ignores both. In development
the Suspend path recorded at 0.919x real time (L5 S3 FAIL), so audio from it does not pass `mcam_audio.py align`. Details of
the stages: [LAB.md](LAB.md). The refusal without the flag still reads `Suspend capture refused: no
_mod/acap_suspend_ok.json (L5 S3 rendered at 0.919x real time; only a passing S3b writes the flag, user consent first)`;
the lab writes the flag on a PASS of S3 as well as S3b (table above), and the 0.919x is the development measurement, not
one of your machine.

`srec record start ... audio=on` uses the default capture settings (`suspend=auto`, no muter-only, level 100, default
heartbeat). With the muted output it therefore works only on the Suspend path; a muter-only capture is possible with
`acap start ... muteronly=on` or an mcam spec with `allowMuterOnly: true`.

## 9. Consent rule for the muted output

Inside a muted capture window the master volume is raised to `level` (100 by default). The headphones stay silent only as
long as InsideMuter keeps every INSIDE session muted (muter-only path) or Wwise stays suspended (Suspend path). If the
muter stops, the game gets a new audio session, or something unmutes INSIDE, game sound at that level can reach the
headphones until the guard reacts (heartbeat checked every 250 ms, stale after `hbage` = 2 s). Therefore:

- The person wearing the headphones must agree before a lab stage that writes a flag (L5 S3, S3b, S2), before `acap
  rtpctest`, and before every muted capture, and should have the headphones on to notice a failure.
- An agent never creates the flag files, never passes `confirm` to `lab l5 s2` and never switches `acap output muted` on its
  own.

## 10. Getting the volume back

- Audible output: acap removes its own rule at every stop.
- Muted output: `acap output audible` removes acap's rule. If the game stays silent, force the game volume and then clear
  the rule: `audio force rtpc rtpc_master_volume <game volume 0-100>` followed by `audio clear rtpc:rtpc_master_volume`
  (`acap status` in the audible output prints the game volume). Removing the rule alone can leave Wwise at 0.
- InsideMuter: since 0.11.0 (muter version 4) it un-mutes the sessions it muted when it ends; `InsideMuter.exe unmute`
  stops a running muter and un-mutes every INSIDE session. A muter started with `--keep-muted` (or a 0.10.0 muter) leaves
  the mute on the session: unmute INSIDE in the Windows volume mixer (section 11).

---

## 11. InsideMuter

**InsideMuter is off by default and never starts by itself. While it runs, it mutes the sound of every running INSIDE.exe
in Windows.** If you started it and the game is silent, this is why. It exits by itself 10 s after the last INSIDE.exe has
ended.

Changed in 0.11.0 (muter version 4): when it ends (test end, Ctrl+C, closing its console, `InsideMuter.exe stop`, and for
the game's own sessions when the game exits) it un-mutes the sessions it switched from audible to muted, so the game is
not left muted in the Windows volume mixer. `--keep-muted` keeps the 0.10.0 behaviour (the mute stays). A muter that is
killed cannot undo anything: `InsideMuter.exe unmute` does it afterwards, or unmute INSIDE in the Windows volume mixer
(Settings > System > Sound > Volume mixer). Whether Windows also remembers a mute for the next start of INSIDE is
unverified.

What it does: every 50 ms, while an INSIDE.exe process exists, it calls `ISimpleAudioVolume.SetMute(true)` on every audio
session of every process named `INSIDE` (every install and copy alike) on every active output device. It never changes a
volume; it un-mutes only what it muted itself, when it ends. Every 250 ms it writes a heartbeat file that the core's muted
capture output and lab test L5 read to prove the game is muted.

Version 3 (InsideDev 0.10.0) keeps the 50 ms poll, the 250 ms heartbeat and the file format, and costs about 3 % of a core
instead of 40-60 %. Version 2 walked every process of the PC by name on every poll (17 ms of CPU per scan with 374
processes). Version 3 identifies the owner of an audio session by its process id (the image name is read once per process
and the handle stays open), so an INSIDE session is muted on the first poll that sees it; the process list is read every
1 s, every 5 s while an INSIDE.exe is known, and the exit of a known game is read from its handle on every poll; each
device's session manager is activated once; the device list is read again on every Windows device notification and every
heartbeat. Measured without the game, with a scratch process that plays silence as the `--also` target, `test 25`: version
2 used 62 % of a core, version 3 2.7 %. Waiting with no game (`run`, 12 s): 42 % against 7.7 %. 40 unmutes from outside, 300 ms
apart: all 40 muted again, median 46 ms, max 67 ms (version 2: median 32 ms, max 62 ms). With the game running, 2 muters
together took 11-18 % of a core on a loaded machine. A device being added or removed was not exercised.

To check it by hand: start `InsideMuter.exe`, then look at `InsideMuter.exe` in Task Manager, Details tab. The CPU column shows a
share of all logical processors: with 8 of them about 0 to 2 % is expected, 5 to 8 % means an old version. `muterVersion` in the
heartbeat must say 4 for the muter of this release (3 for 0.10.0; version 4 keeps the poll, the heartbeat and their
costs). If the game stays audible, read the health check at the end of section 11.3.

### 11.1 Commands

It is a console program (.NET Framework 4.x, built into Windows, x64). Start it from a command prompt or by double-clicking;
the console window logs every session it finds and every time it had to mute one again. Close the window or end the
process to stop it.

| Command | Effect |
|---|---|
| `InsideMuter.exe` or `InsideMuter.exe run [--heartbeat <path>] [--linger <s>] [--wait <s>] [--keep-muted]` | Guard mode. Waits while no INSIDE.exe exists (status `waiting`), mutes while one runs (`running`), then exits `--linger` seconds (default 10) after the last one ended (`linger`, then `stopped`). `--wait <s>` (new): leave when no INSIDE.exe showed up within that many seconds of the start (default 0 = wait forever). `--keep-muted` (new): leave the mute on at the end. Heartbeat default: `heartbeat.json` next to the exe. Only one `run` instance per Windows session (mutex `Local\InsideDev.InsideMuter.run`). |
| `InsideMuter.exe list` | Read-only: every output device (name, id, default, peak meter) and every audio session on it (pid, process, state, muted or audible, volume, peak), and the number of INSIDE.exe processes. Mutes nothing. |
| `InsideMuter.exe status [--heartbeat <path>]` | New: one line from the running muter's heartbeat (`InsideMuter 4 run: running, heartbeat <age> s old ...`). Exit 0 when the heartbeat is fresh, 2 when it is stale or missing. |
| `InsideMuter.exe stop [--keep-muted]` | New: asks the running `run` instance to end; it un-mutes what it muted unless `--keep-muted`. Exit 0, or 2 `no InsideMuter run instance is active`. |
| `InsideMuter.exe unmute` | New: stops a running muter, then clears the mute of every INSIDE session (also after a killed muter or a 0.10.0 one). |
| `InsideMuter.exe test <seconds> [--heartbeat <path>] [--also <process name>] [--keep-muted]` | Runs the mute loop for that many seconds and exits, without waiting or lingering. Heartbeat default `heartbeat_test.json` next to the exe. `--also` mutes a second process name too (for checking the mute path without the game); test mode only. |

A number as the first argument (a minute limit of older builds) is ignored with a notice.

| Exit code | Meaning |
|---|---|
| 0 | normal end (`run` after linger, `test` after its time, `list`, `stop`, a fresh `status`) |
| 1 | usage error, or `--also` outside test mode |
| 2 | `status`: no fresh heartbeat; `stop`: no muter running |
| 3 | another `run` instance is active |

### 11.2 Where the heartbeat must be

The core (`acap`, `srec record ... audio=on`, mcam sound capture) and lab L5 read the heartbeat from
`_mod/muter/heartbeat.json`, a path relative to the game folder. So either:

- copy `InsideMuter.exe` to `<game>\_mod\muter\` and start it there (the default heartbeat lands at
  `<game>\_mod\muter\heartbeat.json`), or
- start it anywhere with `--heartbeat <game>\_mod\muter\heartbeat.json`, or
- point the readers elsewhere: `hb=<path>` on `acap preflight|start`, `hb=<path>` on `lab l5`, spec key
  `replay.audio.heartbeat`.

Start it before the game, so the first audio session is muted as soon as it appears.

### 11.3 `heartbeat.json`

Written to `heartbeat.json.tmp` and swapped in atomically every 250 ms (`format "insidemuter.heartbeat/1"`, ASCII JSON,
non-ASCII escaped). It contains the game's exe path and device names; do not share it if that matters to you.

| Field | Meaning |
|---|---|
| `muterVersion`, `muterPid` | Program version ("4" in this release) and process id (stop a muter by this pid, not by name, if several run). |
| `waitS`, `keepMuted`, `mutedByMuter`, `restoredTotal` | New in version 4: the `--wait` time, whether `--keep-muted` is on, the sessions this muter switched to muted (it un-mutes them at the end), sessions it un-muted so far. |
| `mode`, `status` | `run`: `waiting`, `running`, `linger`, `stopped` (last write). `test`: `test`, then `stopped`. |
| `targets` | Process names muted (`INSIDE`, plus the `--also` name). |
| `seq`, `time`, `unixMs` | Write counter, UTC time. Age = now - `unixMs`. |
| `startedUnixMs`, `lastInsideUnixMs` | Start time; last poll that found an INSIDE.exe (-1 = never). |
| `pollMs`, `heartbeatMs`, `lingerS` | 50, 250, the linger time. |
| `polls`, `pollErrors`, `writeErrors`, `lastError` | Loop counters and the last error. |
| `unmutedSeenTotal`, `lastUnmutedSeenUnixMs` | Polls that found an INSIDE session unmuted (and muted it). A new session counts once. |
| `inside[]` | `{pid, path}` of every INSIDE.exe found. |
| `summary` | `sessions`, `muted`, `unmuted`, `active`, `maxSessionPeak` over `sessions[]`. |
| `sessions[]` | One per INSIDE session per device: `pid`, `endpoint`, `endpointName`, `state` (`inactive`, `active`, `expired`), `muted` (after this poll), `volume`, `peak` (session meter 0..1, -1 = unavailable; it reads before the mute), `unmutedSeen`, `instance`. |
| `endpoints[]` | Every active output device: `id`, `name`, `default`, `peak` (meter of the whole device mix, all applications), `insideSessions`. |

Healthy while the game runs: `mode "run"`, `status "running"`, age under 1 s, `summary.unmuted` 0, every `sessions[].muted`
true. A running InsideMuter.exe alone is no proof: check the heartbeat age.

---

## 12. `tools/mcam_audio.py` (needs Python 3.9+ and numpy)

Never plays audio. `path` is a WAV file or a folder with `audio.wav` (then `audio_sync.csv`, `audio_capture.json`,
`record.json` and `record_meta.csv` of that folder are used). For `<name>.wav` it uses `<name>_sync.csv` and
`<name>_capture.json` when present. Output is JSON on stdout.

| Command | Options (defaults) | What it does | Exit codes |
|---|---|---|---|
| `fix WAV` | `--check` (report only) | Rewrites broken RIFF and data sizes from the file size (after a crash). Idempotent. Since 0.11.0 the original is kept as `<name>.bak` and stderr says `mcam_audio fix: <file>: <changes>; original kept as <name>.bak`; JSON adds `tail`, `changes`, `backup` (0.10.0 could cut a WAV short without a backup). | 0 (already ok, fixed, or fixable with `--check`), 2 (cannot be fixed or not found) |
| `info PATH` | | Format, sizes, RIFF state, every cue marker with its label and time, sync row counts, capture info; adds `tail`, `problems` and `fix` {dataSize, newBytes, changes, seconds} when a fix is needed. | 0, 2 |
| `align PATH` | `--no-write` | Fixes a broken header first, then fits WAV position = a x real_time + b over the lag-0 markers of the sync file matched to the WAV's cue labels. Usable when at least 2 markers match, the slope is within 0.5 % of the format rate, every marker is within 50 ms of the fit and the capture info has no error. Writes the result into `record.json` `audio` (when the WAV is `audio.wav` next to a record.json) or into `audio_align.json` / `<name>_align.json`. | 0 usable, 1 not usable, 2 |
| `track TAKE` | `--record DIR`, `--slow stretch\|mute` (stretch), `--out FILE`, `--dry-run` (new: writes nothing, prints the piece table) | For a replay take (MULTICAM): builds `<take>\audio_track.wav` on the output timeline from `meta.csv`, speed-1 parts copied, slow parts time-stretched with pitch kept (or muted), wall-time gaps cut, 50 ms crossfades; writes `audio_track.json` (adds `firstFrame`, `maxDriftMs`, `driftLimitMs`, `driftOk`, `cuts`, `dryRun`). Refuses unusable audio. | 0, 1 (`driftOk` false), 2, 3 (refused) |
| `stretch SRC DST FACTOR` | | Pitch-preserving stretch; FACTOR = output / input duration. | 0, 2 |
| `check PATH` | `--threshold dBFS` (-40), `--search s` (1.0), `--event NAME` (repeatable) | RMS and peak of the file and of the capture window; for event markers (`e<name>`, lag 1) the first sample above the threshold after the predicted position. JSON adds `ok` and `eventsNotFound`. | 0, 1 (not ok, new), 2 |

`align PATH --no-write` changes no file at all since 0.11.0 (it may report `wouldFix`). For a grec video next to a manual
capture there is no `mcam_audio.py` command: line it up by hand with times.csv and `clockBase` (section 3.3).

---

## 13. Pitfalls

- **Muted output persists.** `acap.output=muted` stays in editor.cfg; from then on the first `acap` command, `srec record
  ... audio=on` or mcam replay spec silences the game for the session. Check `acap output` if the game is silent.
- **Silent game after InsideMuter.** Since 0.11.0 a muter that ends normally un-mutes what it muted. A killed muter, one
  started with `--keep-muted` or a 0.10.0 muter leaves the mute on the INSIDE audio session: run `InsideMuter.exe
  unmute`, or unmute INSIDE in the Windows volume mixer (section 11). Scripts that restart the muter mid-session and need
  the game to stay silent must pass `--keep-muted`, else the game becomes audible between the two muters.
- **Lab audio stages.** After any `lab l5` stage the master volume rule is 0 (the game stays silent), and after S3/S3b Wwise
  stays suspended until the game exits; `acap suspend off` cannot wake a lab Suspend ([LAB.md](LAB.md)).
- **Minimized or unfocused game.** A minimized game window pauses the game. What a capture writes while the game is paused is
  unverified in game.
- **Step mode.** What a sound capture gives for a take played in MCP step mode (game frozen between calls) is unverified in
  game. The WAV runs on wall time while the game stands still between calls: a grec video of such a take leaves the frozen
  time out, so cut the WAV by the `real_time` column of grec's times.csv ([PLAY.md](PLAY.md) section 4.6.1).
- **Deep game folders.** Wwise writes the WAV through a path relative to its bank folder; keep that path under 240
  characters (section 6). The preflight refuses at 240; a manual capture name longer than the probe name is checked
  against 260 at the start, so stay under 240 with the full name too.
- **Steam must run.** Without the Steam client the game closes itself about 2 s after it starts.

---

## 14. Settings reference

| Setting | Default | Where | Effect |
|---|---|---|---|
| `acap.output` | `audible` | `<game>\_mod\editor.cfg`, `acap output` | Audible or muted capture output (section 4). |
| `acap changes` | 2 | session only | Rule changes per volume force. |
| Audio heartbeat path | `_mod/muter/heartbeat.json` (relative to the game folder) | `hb=` on `acap preflight\|start`, spec `replay.audio.heartbeat` | Where the muted output reads InsideMuter's heartbeat. |
| Heartbeat age limit | 2 s | `hbage=`, spec `maxHeartbeatAge` | Older heartbeats fail the muted gates. |
| Capture level | 100 | `level=`, spec `replay.audio.level` | Window level (audible: capped at the game volume). |
| Marker interval | 30 samples | spec `replay.audio.markerEvery` (manual records use the default) | Sync markers during a recording. |
| Suspend flag | absent | `<game>\_mod\acap_suspend_ok.json` | Enables the Suspend path (muted output). |
| Muter-only flag | absent | `<game>\_mod\acap_muteronly_ok.json` | Enables the muter-only path with `muteronly=on`. |
| InsideMuter heartbeat | `heartbeat.json` next to the exe | `--heartbeat <path>` | Section 11.2. |
| InsideMuter linger | 10 s | `--linger <s>` | Exit delay after the last INSIDE.exe. |
| InsideMuter wait | 0 (forever) | `--wait <s>` | Leave when no INSIDE.exe showed up within that time. |
| InsideMuter keep muted | off (un-mute at the end) | `--keep-muted` on `run`, `stop`, `test` | Keep the 0.10.0 behaviour: the mute stays after the muter ends. |
| InsideMuter poll / heartbeat | 50 ms / 250 ms | fixed | |
