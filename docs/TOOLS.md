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

- Python 3.9 or newer. Run the scripts from the repository folder with `python -I tools/<script>.py ...` (`-I` keeps a
  `.py` file in the take or current folder from shadowing a module).
- Standard library only: `inside_ctl.py`, `srec_tool.py`, `mcam_spec_check.py`, `det_compare.py`.
- `mcam_audio.py`: numpy.
- The MultiCam compositor and QA (`mcam_compose.py`, `mcam_take.py`, `mcam_qa.py`, `mcam_track_check.py`,
  `mcam_previews.py`, `fast_review.py`): numpy, opencv-python, Pillow, numba (compositor), matplotlib (QA plots), and
  `ffmpeg` / `ffprobe`. The defaults encode H.265 with NVENC and decode with NVDEC (an NVIDIA GPU); `mcam_compose.py
  --codec x265` and `--cpu`, `fast_review.py --cpu` work without one.
- Fonts for compositor labels: `tools\fonts\Oswald-Regular.ttf` if you put it there (not shipped), else Bahnschrift from
  Windows, else the file given in `compose.labels.font`.

```
python -m pip install numpy opencv-python Pillow numba matplotlib
```

---

## 2. The scripts

| Script | What it does | Needs the game | Documented in |
|---|---|---|---|
| `inside_ctl.py` | Sends console and bridge commands to the running game (POST `/cmd` of the MCP server, the file bridge as fallback), follows `ctl` jobs, `state`, `seq`, `ping`, an interactive `repl`. | yes | [PLAY.md](PLAY.md) section 12 |
| `srec_tool.py` | Reads `scene.srec` recordings: `info`, `verify`, `objects`, `export`, `dump`, `report` (spec events and content checks), `synth` (test recordings). | no | [RECORD_REPLAY.md](RECORD_REPLAY.md) section 8 |
| `mcam_spec_check.py` | Offline lint of MultiCam specs (the same binding and checks as `mcam check` in the game), segment table, cost and disk estimate, optional object lookup in a level index, `--schema-md` for the docs. | no | [MULTICAM.md](MULTICAM.md) section 15 |
| `mcam_compose.py` | Composes a take: per-camera angle videos, layouts, director blocks, edited cuts, grade, motion blur, `review` frames, optional sound (`--audio record`). | no | [MULTICAM.md](MULTICAM.md) section 14 |
| `mcam_qa.py` | Take checks with exit codes (`qa`), contact sheets, sync sheets, camera path and timeline plots, rehearsal divergence, the replay golden test (`replaydiff`), the audio sync check (`audiosync`). | no | [MULTICAM.md](MULTICAM.md) section 14 |
| `mcam_take.py` | Take reader shared by the compositor and QA (not run directly). | no | |
| `mcam_track_check.py` | Where each output camera's subject sits in the visible frame of a replay take; FAIL when it leaves the frame. | no | [MULTICAM.md](MULTICAM.md) section 6.6 |
| `mcam_previews.py` | Stills of every camera of a replay spec at every beat or time from a recording, with contact sheets, through POST `/cmd`. | yes | [MULTICAM.md](MULTICAM.md) section 14.2 |
| `fast_review.py` | Frames, black / near-black / blur QA and contact sheets of the composed finals of a take. | no | [MULTICAM.md](MULTICAM.md) section 14.2 |
| `mcam_audio.py` | Captured WAV files: `fix`, `info`, `align` (marker fit), `track` (sound on a take's output timeline), `stretch`, `check`. Never plays audio. | no | [AUDIO.md](AUDIO.md) section 12 |
| `det_compare.py` | Compares input determinism runs of the `det` lab module by logic step: `compare`, `matrix`, `selftest`. | no | [LAB.md](LAB.md) section 3.7 |

Every script prints its usage with `--help` (or without arguments).

---

## 3. Environment variables and paths

No script has a built-in game or output folder of a particular machine. Paths are arguments; a few defaults come from the
environment:

| Variable | Used by | Meaning |
|---|---|---|
| `INSIDE_GAME` | `inside_ctl.py` (`--game`) | The INSIDE folder; the file bridge is `<game>\_mod\cmd`. |
| `INSIDE_BRIDGE` | `inside_ctl.py` (`--bridge`, wins over `--game`) | The file bridge folder itself. |
| `INSIDE_MCP_PORT` | `inside_ctl.py` (`--port`) | The MCP server port (default 47811). |
| `INSIDEDEV_MCP_TOKEN` | `inside_ctl.py` (`--token`) | Bearer token when `mcp.token` is set in the game. |
| `FFMPEG`, `FFPROBE` | `mcam_take.py` (compositor, QA, review) | Full paths of ffmpeg and ffprobe; else they are looked up on PATH. |
| `INSIDE_LEVEL_INDEX` | `mcam_spec_check.py` (`--index-dir`) | A folder of level index JSONL files for `--index`; without it `--index` only warns. |

Outputs: `mcam_compose.py` and `mcam_qa.py` write into `<repo>\out\<take>` unless `--out` is given (and refuse to write
outside that folder without `--force`); `srec_tool.py report` writes next to the recording; `mcam_audio.py align` writes
into the take's `record.json` or next to the WAV.

---

## 4. Talking to the running game

Two scripts talk to the game: `inside_ctl.py` and `mcam_previews.py`. Both use the plain HTTP endpoints of
`InsideDev.Mcp.dll` on `127.0.0.1` (no MCP client needed): `POST /cmd` runs one console or bridge command, `GET /state` and
`GET /ping` answer at once ([MCP.md](MCP.md) section 12). `inside_ctl.py` falls back to the file bridge when nothing
listens:

```
python -I tools/inside_ctl.py ping
python -I tools/inside_ctl.py --game "C:\path\to\INSIDE" state
python -I tools/inside_ctl.py ctl run right 1.2
python -I tools/inside_ctl.py seq "R1.2 J R0.4+J G1 W0.5 L0.3"
python -I tools/inside_ctl.py "srec record status"
```

`ctl` commands need `InsideDev.Play.dll`; plain bridge commands (`state`, `srec ...`, `mcam ...`, `cam ...`) need only the
core, and over HTTP `InsideDev.Mcp.dll`.

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
reveals.

The playbooks were written from the game's code and scene data; most steps are marked as not verified in play. Edit them
freely: the `guide` tool picks up changed files on the next call.

---

## 6. Pitfalls

- **Wrong folder.** `inside_ctl.py` without `--game` / `--bridge` (or the environment variables) uses HTTP only; when the
  game's MCP server is off it answers "no bridge folder is set".
- **Old MCP DLL.** A 0.6.0 `InsideDev.Mcp.dll` has no `/cmd`; `inside_ctl.py` then needs the file bridge.
- **GPU defaults.** The compositor and review default to NVENC / NVDEC; without an NVIDIA GPU pass `--codec x265` and
  `--cpu`.
- **Minimized game.** `mcam_previews.py` and `inside_ctl.py` wait on the game; a minimized game window pauses the game and
  their commands time out.
