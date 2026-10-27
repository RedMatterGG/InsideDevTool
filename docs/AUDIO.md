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
| `tools\muter\InsideMuter.exe` | `<game>\_mod\muter\` | only when you start it | yes: it never starts by itself | stop it, delete the file |

| Feature | Starts with | Stops with | Saved between game sessions |
|---|---|---|---|
| Audio capture | `acap start <name>`, `srec record start ... audio=on`, or a spec with `replay.audio.capture: "record"` | `acap stop`, the end of the recording | the WAV files only |
| Audio output choice | `acap output audible\|muted` | `acap output audible` | yes: `acap.output` in `<game>\_mod\editor.cfg`, default `audible` |
| InsideMuter | starting `InsideMuter.exe` | it exits 10 s after the last INSIDE.exe, or close it | nothing (its heartbeat file only) |

A user who never calls `acap` and never starts InsideMuter sees no change: no audio rule, no muting.

Building: `sh build/build_muter.sh` (needs the .NET Framework 4.x reference assemblies, found automatically on Windows).

---

## 2. Where the commands run

`acap` is a bridge command: send it through the file bridge (`<game>\_mod\cmd\in_<anything>.txt`, reply in
`<game>\_mod\cmd\out.txt`) or the MCP `command` tool; the in-game console does not run it. Details of the bridge and of
quoting: [RECORD_REPLAY.md](RECORD_REPLAY.md) section 2.

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
python -I tools/mcam_audio.py align <game>\_mod\audio\scene1.wav    writes scene1_align.json
```

---

## 4. The two outputs

`acap output` chooses how a capture treats the sound you hear. It is saved as `acap.output` in `<game>\_mod\editor.cfg`.

| Output | Default | What you hear | Needs |
|---|---|---|---|
| `audible` | yes | The game sound plays normally. Nothing is muted, the master volume is never set to 0. | nothing extra |
| `muted` | no | Nothing of INSIDE reaches the speakers or headphones: the master volume `rtpc_master_volume` is forced to 0 from the first `acap` command, `srec record ... audio=on` or mcam spec with a `replay` block until the game exits or you switch back with `acap output audible`, and is lifted only inside a capture window while InsideMuter keeps every INSIDE session muted in Windows. For capturing while your headphones must stay silent (for example while you listen to something else on the same device). | InsideMuter running (section 11), plus a flag file that only a passing lab stage writes (section 8) |

Audible output in detail: the capture window opens at once at min(`level`, game volume), where the game volume is the
game's own setting x 100 (or the Wwise value if the setting cannot be read), so the master volume is never raised. While
the window is open the master volume is held at that level. At the end acap puts back the rule that was there before (or
sets the game volume and removes its own rule). No heartbeat, no Suspend, no flag files; `acap suspend on` and `acap
rtpctest` are refused.

## 5. `acap` commands

`acap` alone is `acap status`; `acap help` prints the usage.

| Command | Arguments (defaults) | Effect |
|---|---|---|
| `acap status` | | State (`idle`, `starting`, `arming`, `window`, `finalizing`), output, current capture (file, size, markers, errors), game volume and acap's own rule (audible) or the heartbeat (muted), the `rtpc:rtpc_master_volume` rule and the Wwise value, flags (muted), counters, last error, last capture, last rtpctest. |
| `acap output [audible\|muted]` | | Without argument prints the output and the raw editor.cfg value. Switching is refused while a capture, window, rtpctest or wake is active. Switching to `audible` removes acap's own volume rule; switching to `muted` forces the master volume to 0 at once. |
| `acap preflight [suspend=auto\|on\|off] [muteronly=on] [hb=<path>] [hbage=2]` | | Runs the checks of a capture without starting one. Prints `ok` or `refused: <reason>`, then one line per check. In the audible output only the basics are checked (no capture or rtpctest running, the sound engine wrapper installed, Wwise not suspended by acap, the target folder); `suspend`, `muteronly`, `hb`, `hbage` matter only for `muted`. |
| `acap start <name> [suspend=...] [muteronly=on] [level=100] [hb=<path>] [hbage=2]` | name: ASCII letters, digits, `_`, `-`, at most 64 | Manual capture to `<game>\_mod\audio\<name>.wav`, posts marker `s0`. Refused while an mcam run or any recording is active (a recording captures through `srec record start ... audio=on`), or when the file exists. `level` 0-100; `muteronly` only takes `on`. |
| `acap mark <ascii>` | | Marker in the WAV (cue + label) and a row in the sync file; characters other than letters, digits, `_ - . : +` become `_`, at most 64. |
| `acap stop` | | Stops the capture: volume first (audible: put back; muted: forced 0), then the capture; the file is finalized within up to 10 s. |
| `acap suspend on\|off` | muted only | `on`: AkSoundEngine.Suspend(true); Wwise stays suspended until the game exits or `off`. `off`: refused during a capture, window or rtpctest; forces the volume to 0, waits until Wwise reads 0 and 100 ms passed, then WakeupFromSuspend. |
| `acap rtpctest [level=100]` | muted only, level in (0, 100] | Checks that one rule change reaches Wwise: 0 -> level -> 0 with a readback after each. Needs a fresh heartbeat and either a quiet suspended Wwise or the muter-only flag. Result in `acap status` and `<game>\_mod\audio\rtpctest_<time>_<native\|managed>.json`. It lifts the master volume: same consent rule as a muted capture (section 9). |
| `acap changes 1\|2` | default 2 | Rule changes per volume force (2 works around a native-enforcement ordering issue; the readback decides either way). Not saved. |

Every `acap` command first subscribes the capture guard for the rest of the game session (and, with the muted output,
forces the master volume to 0).

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
  readbacks, the RIFF check `wav`, `needsFix` (header broken: run `mcam_audio.py fix`), `stopReason`, `error`, `notes`.
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
the stages: [LAB.md](LAB.md).

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
- InsideMuter: its mute stays on the INSIDE session after it exits (section 11).

---

## 11. InsideMuter

**InsideMuter is off by default and never starts by itself. While it runs, it mutes the sound of every running INSIDE.exe
in Windows.** If you started it and the game is silent, this is why. It exits by itself 10 s after the last INSIDE.exe has
ended.

Stopping InsideMuter does not bring the sound back: the mute is set on the game's Windows audio session and stays there.
Unmute INSIDE in the Windows volume mixer (Settings > System > Sound > Volume mixer). Whether Windows also remembers the mute
for the next start of INSIDE is unverified.

What it does: every 50 ms, while an INSIDE.exe process exists, it calls `ISimpleAudioVolume.SetMute(true)` on every audio
session of every process named `INSIDE` (every install and copy alike) on every active output device. It never changes a
volume and never unmutes. Every 250 ms it writes a heartbeat file that the core's muted capture output and lab test L5
read to prove the game is muted.

### 11.1 Commands

It is a console program (.NET Framework 4.x, built into Windows, x64). Start it from a command prompt or by double-clicking;
the console window logs every session it finds and every time it had to mute one again. Close the window or end the
process to stop it.

| Command | Effect |
|---|---|
| `InsideMuter.exe` or `InsideMuter.exe run [--heartbeat <path>] [--linger <s>]` | Guard mode. Waits while no INSIDE.exe exists (status `waiting`), mutes while one runs (`running`), then exits `--linger` seconds (default 10) after the last one ended (`linger`, then `stopped`). If INSIDE never starts it waits forever. Heartbeat default: `heartbeat.json` next to the exe. Only one `run` instance per Windows session (mutex `Local\InsideDev.InsideMuter.run`). |
| `InsideMuter.exe list` | Read-only: every output device (name, id, default, peak meter) and every audio session on it (pid, process, state, muted or audible, volume, peak), and the number of INSIDE.exe processes. Mutes nothing. |
| `InsideMuter.exe test <seconds> [--heartbeat <path>] [--also <process name>]` | Runs the mute loop for that many seconds and exits, without waiting or lingering. Heartbeat default `heartbeat_test.json` next to the exe. `--also` mutes a second process name too (for checking the mute path without the game); test mode only. |

A number as the first argument (a minute limit of older builds) is ignored with a notice.

| Exit code | Meaning |
|---|---|
| 0 | normal end (`run` after linger, `test` after its time, `list`) |
| 1 | usage error, or `--also` outside test mode |
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
| `muterVersion`, `muterPid` | Program version ("2") and process id (stop a muter by this pid, not by name, if several run). |
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
| `fix WAV` | `--check` (report only) | Rewrites broken RIFF and data sizes from the file size (after a crash). Idempotent. | 0 (already ok, fixed, or fixable with `--check`), 2 (cannot be fixed or not found) |
| `info PATH` | | Format, sizes, RIFF state, every cue marker with its label and time, sync row counts, capture info. | 0, 2 |
| `align PATH` | `--no-write` | Fixes a broken header first, then fits WAV position = a x real_time + b over the lag-0 markers of the sync file matched to the WAV's cue labels. Usable when at least 2 markers match, the slope is within 0.5 % of the format rate, every marker is within 50 ms of the fit and the capture info has no error. Writes the result into `record.json` `audio` (when the WAV is `audio.wav` next to a record.json) or into `audio_align.json` / `<name>_align.json`. | 0 usable, 1 not usable, 2 |
| `track TAKE` | `--record DIR`, `--slow stretch\|mute` (stretch), `--out FILE` | For a replay take (MULTICAM): builds `<take>\audio_track.wav` on the output timeline from `meta.csv`, speed-1 parts copied, slow parts time-stretched with pitch kept (or muted), wall-time gaps cut, 50 ms crossfades; writes `audio_track.json`. Refuses unusable audio. | 0, 2, 3 (refused) |
| `stretch SRC DST FACTOR` | | Pitch-preserving stretch; FACTOR = output / input duration. | 0, 2 |
| `check PATH` | `--threshold dBFS` (-40), `--search s` (1.0), `--event NAME` (repeatable) | RMS and peak of the file and of the capture window; for event markers (`e<name>`, lag 1) the first sample above the threshold after the predicted position. | 0, 2 |

---

## 13. Pitfalls

- **Muted output persists.** `acap.output=muted` stays in editor.cfg; from then on the first `acap` command, `srec record
  ... audio=on` or mcam replay spec silences the game for the session. Check `acap output` if the game is silent.
- **Silent game after InsideMuter.** Its mute stays on the INSIDE audio session after it exits; unmute INSIDE in the Windows
  volume mixer (section 11).
- **Lab audio stages.** After any `lab l5` stage the master volume rule is 0 (the game stays silent), and after S3/S3b Wwise
  stays suspended until the game exits; `acap suspend off` cannot wake a lab Suspend ([LAB.md](LAB.md)).
- **Minimized or unfocused game.** A minimized game window pauses the game. What a capture writes while the game is paused is
  unverified in game.
- **Step mode.** What a sound capture gives for a take played in MCP step mode (game frozen between calls) is unverified in
  game.
- **Deep game folders.** Wwise writes the WAV through a path relative to its bank folder; keep that path under 240
  characters (section 6).

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
| InsideMuter poll / heartbeat | 50 ms / 250 ms | fixed | |
