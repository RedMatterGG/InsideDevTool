# Python tools and guide playbooks

The `tools\*.py` scripts work next to the game: they send commands to a running game, check and compose MultiCam takes,
read recordings, align captured audio and compare determinism runs. The `guide\` folder holds the playbooks that the MCP
`guide` tool serves to an AI that plays the game. Everything here is optional: the editor and the MCP server work without
it, and none of the scripts starts, stops or patches the game.

Contents

1. Requirements
2. The scripts
3. Environment variables and paths
4. Talking to the running game
5. Guide playbooks
6. Pitfalls

---

## 1. Requirements

- Python 3.9 or newer. Keep the scripts in the repository folder (they import each other, and `mcam_spec_check.py` reads
  the C# sources in `src\`) and run them from there with `python -I tools/<script>.py ...` (`-I` keeps a `.py` file in the
  take or current folder from shadowing a module).
- Standard library only: `inside_ctl.py`, `inside_link.py`, `srec_tool.py`, `mcam_spec_check.py`, `det_compare.py`,
  `mcam_ff.py`, and `mcam_track_check.py` (it imports the compositor only for a spec with layouts).
- `mcam_audio.py`: numpy. `mcam_previews.py`: Pillow.
- The MultiCam compositor and QA (`mcam_compose.py`, `mcam_take.py`, `mcam_qa.py`, `fast_review.py`): numpy,
  opencv-python, Pillow and `ffmpeg` / `ffprobe`. numba is optional (a faster compositor; without it the numpy path is
  several times slower with sub-step blur); matplotlib is needed only by `mcam_qa.py paths` and `mcam_qa.py timeline`.
  The defaults encode H.265 with NVENC and decode with NVDEC (an NVIDIA GPU); `mcam_compose.py --codec x265` and `--cpu`,
  `mcam_qa.py --cpu`, `fast_review.py --cpu` work without one. `python -I tools/mcam_compose.py doctor [take]` checks
  ffmpeg, the encoders and filters, memory and the output folder before a first run.
- Fonts for compositor labels: `tools\fonts\Oswald-Regular.ttf` if you put it there (not shipped), else Bahnschrift from
  the Windows fonts folder, else the file given in `compose.labels.font`.

```
python -m pip install numpy opencv-python Pillow
python -m pip install numba matplotlib
```

The second line is optional.

---

## 2. The scripts

| Script | What it does | Needs the game | Documented in |
|---|---|---|---|
| `inside_ctl.py` | Sends console and bridge commands to the running game (POST `/cmd` of the MCP server, the file bridge as fallback), follows `ctl` jobs, `state` (the observe JSON), `snapshot` (the text `state`), `seq`, `ping`, `wait`, `doctor`, `-f <file>`, an interactive `repl`. | yes | [PLAY.md](PLAY.md) section 12, section 4 below |
| `inside_link.py` | The connection library under `inside_ctl.py` and `mcam_previews.py`: `Link` (HTTP with one kept connection, bearer token, file bridge fallback), `McpClient` (a small MCP client), `wait_ready`. Import only. | yes | its own docstring |
| `srec_tool.py` | Reads `scene.srec` recordings: `info` (`--fast`), `verify` (`--strict`), `objects` (`--json`), `export` (by id, path, name, `render`, `shader`, `stamps`, `events`), `dump`, `report` (spec events and content checks), `synth` (test recordings). | no | [RECORD_REPLAY.md](RECORD_REPLAY.md) section 8 |
| `mcam_spec_check.py` | Offline lint of MultiCam specs (the same binding and checks as `mcam check` in the game), segment table, cost and disk estimate (`--res WxH`), `--compare <game report.json>`, `--strict`, optional object lookup in a level index, `--schema-md` for the docs. | no | [MULTICAM.md](MULTICAM.md) section 15 |
| `mcam_compose.py` | Composes a take: per-camera angle videos, layouts, director blocks, edited cuts, grade, motion blur, `review` frames, optional sound (`--audio record`); `doctor`, `--resume` (skip outputs that are up to date), `--json`. | no | [MULTICAM.md](MULTICAM.md) section 14 |
| `mcam_qa.py` | Take checks with exit codes (`qa`), contact sheets, sync sheets, camera path and timeline plots, rehearsal divergence, the replay golden test (`replaydiff`), the audio sync check (`audiosync`); `--json`. | no | [MULTICAM.md](MULTICAM.md) section 14 |
| `mcam_take.py` | Take reader shared by the compositor and QA (not run directly). | no | |
| `mcam_ff.py` | Prints the ffmpeg and ffprobe the tools use and their versions (`--json`); exit 2 when one is missing. Shared by the compositor, QA and review. | no | |
| `mcam_track_check.py` | Where each output camera's subject sits in the visible frame of a replay take; FAIL when it leaves the frame. `--res WxH`, `--no-write`, `--csv`, `--json`. | no | [MULTICAM.md](MULTICAM.md) section 6.6 |
| `mcam_previews.py` | Stills of every camera of a replay spec at every beat or time from a recording, with contact sheets, through POST `/cmd` (file bridge fallback). | yes | [MULTICAM.md](MULTICAM.md) section 14.2 |
| `fast_review.py` | Frames, black / near-black / blur QA and contact sheets of the composed finals of a take; `--json`. | no | [MULTICAM.md](MULTICAM.md) section 14.2 |
| `mcam_audio.py` | Captured WAV files: `fix` (keeps the original as `<name>.bak`), `info`, `align` (marker fit; `--no-write`), `track` (sound on a take's output timeline; `--dry-run`), `stretch`, `check`. Never plays audio. | no | [AUDIO.md](AUDIO.md) section 12 |
| `det_compare.py` | Compares input determinism runs of the `det` lab module by logic step: `compare`, `matrix`, `selftest`; `--json`. | no | [LAB.md](LAB.md) section 3.7 |

Every script prints its usage with `--help` (or without arguments).

Exit codes, the same for every tool: 0 ok; 1 the result is bad (an output failed its check, an encode or decode failed,
QA errors, FAIL, verify problems, audio not usable or drifting); 2 input or usage error, or (`inside_ctl.py`) the game is
not reachable; 3 refused (`inside_ctl.py`: the game refused the command or the token; the compositor: another encode is
running, not enough memory, foreign files in the output folder). `inside_ctl.py` exits 130 on Ctrl+C.

---

## 3. Environment variables and paths

No script has a built-in game or output folder of a particular machine. Paths are arguments; a few defaults come from the
environment:

| Variable | Used by | Meaning |
|---|---|---|
| `INSIDE_GAME` | `inside_ctl.py` (`--game`), `mcam_previews.py` (`--game`) | The INSIDE folder; the file bridge is `<game>\_mod\cmd`. Without it (and without `--bridge`) the bridge folder of a running `INSIDE.exe` is used. |
| `INSIDE_BRIDGE` | `inside_ctl.py`, `mcam_previews.py` (`--bridge`, wins over `--game`) | The file bridge folder itself. |
| `INSIDE_MCP_PORT` | `inside_ctl.py`, `mcam_previews.py` (`--port`), the stdio launcher | The MCP server port (default 47811). |
| `INSIDEDEV_MCP_TOKEN` | `inside_ctl.py`, `mcam_previews.py` (`--token`), the stdio launcher | Bearer token when `mcp.token` is set in the game. |
| `FFMPEG`, `FFPROBE` | `mcam_ff.py` (compositor, QA, review) | Full paths of ffmpeg and ffprobe; else they are looked up on PATH. The game's own recorders find ffmpeg their own way (`ffmpeg.path` in `editor.cfg`, `INSIDEDEV_FFMPEG`, `<game>\_mod\ffmpeg\`, PATH). |
| `MCAM_OUT` | `mcam_compose.py`, `mcam_qa.py` (`fast_review.py` follows the compositor's folder) | Root folder of the outputs (`<MCAM_OUT>\<take name>`) and of the encode lock. |
| `INSIDE_STATS_DIR` | `mcam_take.py` | Folder of the grade statistics cache (default: `_stats` in the output folder). |
| `MCAM_NO_SEEK`, `MCAM_FFMPEG_STALL_S` | `mcam_take.py` | `1` = decode take videos from the first frame instead of seeking; seconds without ffmpeg output before a decode counts as stalled (120). |
| `INSIDE_LEVEL_INDEX` | `mcam_spec_check.py` (`--index-dir`) | A folder of level index JSONL files for `--index`; without it `--index` only warns. The index comes from tooling that is not part of this repository. |

Outputs of `mcam_compose.py` and `mcam_qa.py`: `--out DIR`, else `<MCAM_OUT>\<take name>`, else the
spec's `compose.output.root` (relative to the take folder), else the folder an earlier version used for this take
(`<repo>\out\<take name>`, only when it exists and is not empty), else `<take>\out`. Without `--out` one line on stderr
says which folder was chosen and why. A folder the tools wrote to holds `.mcam_out`; files under the tools' output names
that they did not write are refused unless `--force`. `fast_review.py` reads the finals from the take folder (when it holds
`compose_log.json`) or from the compositor's output folder and writes `fast_review\` there. `srec_tool.py report` writes next to the recording; `mcam_audio.py
align` writes into the take's `record.json` or next to the WAV.

---

## 4. Talking to the running game

Two scripts talk to the game: `inside_ctl.py` and `mcam_previews.py`. Both use `tools\inside_link.py`: the plain HTTP
endpoints of `InsideDev.Mcp.dll` on `127.0.0.1` (no MCP client needed: `POST /cmd` runs one console or bridge command,
`GET /state`, `GET /status` and `GET /ping` answer at once, [MCP.md](MCP.md) section 12), with the bearer token when one is
given, and the file bridge as fallback when nothing listens.

```
python -I tools/inside_ctl.py ping
python -I tools/inside_ctl.py wait --timeout 90
python -I tools/inside_ctl.py doctor
python -I tools/inside_ctl.py --game "C:\path\to\INSIDE" snapshot
python -I tools/inside_ctl.py ctl run right 1.2
python -I tools/inside_ctl.py seq "R1.2 J R0.4+J G1 W0.5 L0.3"
python -I tools/inside_ctl.py srec record status
python -I tools/inside_ctl.py --json --stop-on-error -f run.txt
```

| Command | Answer |
|---|---|
| `<words>` | sends the words as one command; several words are joined, a word with a blank is quoted (`--quote-all` also quotes a single argument). One argument is sent as typed, so `inside_ctl.py "srec record status"` works too. |
| `state` | the observe JSON (`GET /state`, or `ctl status json` over the bridge); needs `InsideDev.Play.dll`. Exit 1 with a hint on stderr when the reply is not JSON. |
| `snapshot` | the text snapshot of the bridge command `state` (boy, savepoint, streaming, triggers, audio, console tail); needs only the core |
| `wait [up \| ready \| savepoint <subscene> <savepoint>] [--timeout S]` | waits until the game answers (`up`), until the world is loaded (`ready`, the default) or until that savepoint is current; replies `ready: pong InsideDev 0.11.0: the world is loaded (GET /state) (3.2 s)` or `savepoint: <label> #<n>  (spawn <sub> <sp>) (1.0 s)`; on timeout `not ready: after 120 s: <reason>` on stderr, exit 2 |
| `doctor [json]` | the game's `doctor` (exit 1 when it starts with `doctor: fail`); when the game is not reachable, `unreachable: ...` and the rows seen from outside (`game process`, `Steam client`, `port`, `game window`, `file bridge`), exit 2 |
| `-f FILE` / `-f -` | the lines of a file (or standard input) as commands; `#` starts a comment |

Options (before the command): `--port`, `--host`, `--token`, `--game`, `--bridge`, `--http-only` / `--bridge-only`,
`--force-bridge` (write a bridge command also when no `INSIDE.exe` is seen), `--no-wait` (do not follow a started ctl
job), `--timeout S` (follow a job or wait, 120), `--http-timeout S` (70), `--bridge-timeout S` (20), `--json` (one line
`{"ok", "text", "error", "via", "job", "exit", "command"}` per command), `--stop-on-error`, `--quote-all`.

`mcam_previews.py` takes `--port`, `--url`, `--token`, `--game` and `--bridge` the same way; with `mcp.token` set it needs
`--token` (or `INSIDEDEV_MCP_TOKEN`).

`ctl` commands and `state` need `InsideDev.Play.dll`; plain bridge commands (`snapshot`, `srec ...`, `mcam ...`, `cam ...`)
need only the core, and over HTTP `InsideDev.Mcp.dll`.

---

## 5. Guide playbooks

`guide\` contains Markdown playbooks for an AI that plays INSIDE through the MCP play tools ([PLAY.md](PLAY.md) section 9):

| Path | Content |
|---|---|
| `guide\index.md` | Reading order for an agent. |
| `guide\map.tsv` | Subscene index to scene, area group, level file and savepoint counts. |
| `guide\<group>\<topic>\SKILL.md` | One topic: frontmatter (`name`, `title`, `description`, `chapter`, `order`, `subscenes`, `related`), then `## Hint`, `## Steps`, `## Solution` and per-place `###` sections. Groups: forest, farm, city, sewers, offices, mines, lab, huddle (areas), mechanics, safety, secrets. |
| `guide\scenarios\*.json` | Two example scripted tests for the `run_scenario` tool (dev profile, [MCP.md](MCP.md) section 10.6). |

Install: copy `guide\` to `<game>\_mod\guide\` (or point `play.guide_dir` in `<game>\_mod\editor.cfg`, or the environment
variable `INSIDEDEV_GUIDE_DIR`, at another folder). Without it the `guide` tool answers with an empty index and a warning;
everything else works. `play.guide_max` (`hint`, `steps`, `solution`; default `solution`) caps how much a guide call
reveals; `cfg set play.guide_max hint` changes it while the game runs.

The playbooks were written from the game's code and scene data; most steps are marked as not verified in play. Edit them
freely: the `guide` tool picks up changed files on the next call.

---

## 6. Pitfalls

- **No game found.** `inside_ctl.py` without `--game` / `--bridge` (or the environment variables) uses the bridge folder
  of a running `INSIDE.exe`; when no game runs and the MCP server does not answer, `doctor` says what it sees from
  outside. The Steam copy of the game closes itself about 2 s after it starts when the Steam client is not running.
- **`state` is not the bridge `state`.** `inside_ctl.py state` is the Play observe JSON; the text snapshot of the bridge
  command `state` is `inside_ctl.py snapshot`.
- **Old MCP DLL.** A 0.6.0 `InsideDev.Mcp.dll` has no `/cmd`; `inside_ctl.py` then needs the file bridge.
- **Token.** With `mcp.token` set in the game, both scripts need `--token` or `INSIDEDEV_MCP_TOKEN` (HTTP 401 otherwise,
  `inside_ctl.py` exit 3).
- **GPU defaults.** The compositor and review default to NVENC / NVDEC; without an NVIDIA GPU pass `--codec x265` and
  `--cpu`. `mcam_compose.py doctor` says which encoders work.
- **Minimized game.** `mcam_previews.py` and `inside_ctl.py` wait on the game; a minimized game window pauses the game and
  their commands time out.
- **Tools copied out of the repository.** `mcam_spec_check.py` stops with "cannot read the schema" (exit 2) without
  `..\src\InsideDev`; pass `--types` and `--design`, or run it in the repository.
- **The atlas generator.** `tools\atlas\make_atlas.py` needs the per-level index and audit tables of tooling that is not in
  this repository; run it from the repository root (`python -I tools/atlas/make_atlas.py ... --out data/atlas`).
