"""MultiCam take reader shared by mcam_compose.py and mcam_qa.py: camera folders, meta/poses/events/tracks/rig CSVs (v2,
v2-draft meta without frame columns, v1 JPEG folders only), replay run.json modes (replay/take keep the v2 timeline and
expose real_time, record/golden read record_meta.csv), output-frame timelines (meta frames and warp mode), event time
references, record folder and audio state, WAV read/write with cue/labl markers and the lag-0 marker fit (REPLAY_DESIGN
6.2), a thread-safe frame store (jpg / raw rgba / pipe mkv) and cached per-camera luma statistics. GPU mode (FrameStore
gpu=True, the compositor default): pipe mkv frames decode on NVDEC (-hwaccel cuda) and shrink by 2 / 4 / 8 on the GPU
(scale_cuda bilinear) before the download, one ffmpeg reader per camera and reduce; full-size frames are bit-identical to the
CPU decode, shrunk ones differ from the CPU INTER_AREA shrink by about 1/255 on edges (51-52 dB PSNR); luma statistics (grade
floors) of a pipe mkv always come from one CPU reader that shrinks by 8 inside ffmpeg (scale flags=area), so grades are
identical in both modes. A reader starts near the frame it is asked for (ffmpeg -ss before -i, constant frame rate; env
MCAM_NO_SEEK=1 decodes from the first frame as before) and never waits on a dead or silent ffmpeg (MCAM_FFMPEG_STALL_S, 120).
Frames handed out are read-only and shared with the cache, never copied."""
from __future__ import annotations

import csv
import hashlib
import json
import math
import os
import queue
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import threading
import time
from collections import OrderedDict
from dataclasses import dataclass, field
from pathlib import Path

import cv2
import numpy as np

import mcam_audio as MA

REPO = Path(__file__).resolve().parent.parent
OUT_ROOT = REPO / 'out'
FFMPEG = os.environ.get('FFMPEG') or shutil.which('ffmpeg') or 'ffmpeg'
FFPROBE = os.environ.get('FFPROBE') or shutil.which('ffprobe') or 'ffprobe'
LUMA = np.array([0.22, 0.707, 0.071], np.float32)
FRAME_RE = re.compile(r'^(\d+)\.(jpg|jpeg|png|rgba)$', re.I)
REDUCE_FLAGS = {1: cv2.IMREAD_COLOR, 2: cv2.IMREAD_REDUCED_COLOR_2, 4: cv2.IMREAD_REDUCED_COLOR_4, 8: cv2.IMREAD_REDUCED_COLOR_8}
STATS_VERSION = 4
STATS_REDUCE = 8
REPLAY_MODES = MA.REPLAY_MODES
REPLAY_DIRS = {'record', 'golden', 'passes', 'noise'}
SLOPE_LIMIT = MA.SLOPE_LIMIT


class TakeError(Exception):
    pass


@dataclass
class Camera:
    name: str
    folder: Path | None
    ext: str = 'jpg'
    mode: str = ''
    label: str = ''
    enabled: bool = True
    spec: dict = field(default_factory=dict)
    steps: np.ndarray = field(default_factory=lambda: np.zeros(0, np.int64))
    files: dict = field(default_factory=dict)
    video: Path | None = None


@dataclass
class OutFrame:
    steps: tuple
    weights: tuple
    t: float
    t0: float
    speed: float
    segment: int
    flag: str = ''
    index: int = -1
    real: float = math.nan


class Timeline:
    def __init__(self, frames: list[OutFrame], fps: float, mode: str):
        self.frames = frames
        self.fps = float(fps)
        self.mode = mode
        self.times = np.array([f.t for f in frames], np.float64)

    def __len__(self) -> int:
        return len(self.frames)

    def __getitem__(self, k: int) -> OutFrame:
        return self.frames[k]

    def frame_at(self, t: float) -> int:
        if not len(self.frames):
            return 0
        k = int(np.searchsorted(self.times, t - 1e-4, side='left'))
        return min(max(k, 0), len(self.frames) - 1)

    def frame_at_or_end(self, t: float) -> int:
        if not len(self.frames):
            return 0
        return int(np.searchsorted(self.times, t - 1e-4, side='left'))

    def duration(self) -> float:
        return len(self.frames) / self.fps

    def sliced(self, k0: int, k1: int) -> 'Timeline':
        return Timeline(self.frames[k0:k1], self.fps, self.mode)

    def speed_at(self, t: float) -> float:
        if not len(self.frames):
            return 1.0
        return self.frames[self.frame_at(t)].speed

    def to_json(self) -> dict:
        return {'fps': self.fps, 'mode': self.mode, 'frames': len(self.frames),
                'duration': round(self.duration(), 4),
                'items': [{'k': k, 't': round(f.t, 6), 't0': round(f.t0, 6), 'speed': f.speed, 'segment': f.segment,
                           'steps': list(f.steps), 'flag': f.flag, 'index': f.index,
                           'real': round(f.real, 6) if math.isfinite(f.real) else None} for k, f in enumerate(self.frames)]}


def read_csv(path: Path) -> list[dict]:
    if not path.is_file():
        return []
    with path.open('r', encoding='utf-8', newline='') as fh:
        return [dict(r) for r in csv.DictReader(fh)]


def fnum(v, default: float = math.nan) -> float:
    return MA.fnum(v, default)


def inum(v, default: int = -1) -> int:
    return MA.inum(v, default)


def parse_time_ref(value) -> tuple[str | None, float]:
    if value is None:
        return None, math.nan
    if isinstance(value, (int, float)):
        return None, float(value)
    s = str(value).strip()
    if not s.startswith('@'):
        return None, fnum(s)
    ok, name, off = _spec_time(s)
    if not ok or not name:
        raise TakeError(f'bad time reference {value!r} (use a number or "@event+1.5")')
    return name, off


def _spec_time(text: str) -> tuple:
    here = str(Path(__file__).resolve().parent)
    if here not in sys.path:
        sys.path.insert(0, here)
    from mcam_spec_check import parse_time
    return parse_time(text)


def smoothstep(x: float) -> float:
    x = min(max(x, 0.0), 1.0)
    return x * x * (3.0 - 2.0 * x)


def pretty_name(name: str) -> str:
    s = name.replace('_', ' ').strip()
    return s[:1].upper() + s[1:] if s else name


class Take:
    def __init__(self, root: Path, rate: float | None = None, group: int | None = None,
                 event_overrides: dict | None = None):
        self.root = Path(root).resolve()
        if not self.root.is_dir():
            raise TakeError(f'take folder not found: {self.root}')
        self.warnings: list[str] = []
        self.spec = self._json('spec.json')
        self.run = self._json('run.json')
        self.mode = str((self.run or {}).get('mode') or '')
        self.replay_mode = self.mode if self.mode in REPLAY_MODES else ''
        if not self.mode and (self.root / 'record_meta.csv').is_file():
            self.replay_mode = 'record'
        if self.replay_mode == 'passes':
            raise TakeError(f'{self.root} is an "mcam passes" plan (passes.json, CSVs, no frames)')
        self.meta = read_csv(self.root / 'meta.csv')
        self.events = read_csv(self.root / 'events.csv')
        self.event_overrides = dict(event_overrides or {})
        self.name = (self.spec or {}).get('name') or (self.run or {}).get('name') or self.root.name
        if self.meta and 'frame' in self.meta[0]:
            self.kind = 'v2'
        elif self.meta:
            self.kind = 'v2-draft'
        else:
            self.kind = 'v1'
        self.cli_rate = rate
        self.cli_group = group
        self.cams: OrderedDict[str, Camera] = OrderedDict()
        self._discover_cameras()
        if not self.cams:
            raise TakeError(f'no camera folders with frames in {self.root}')
        if self.replay_mode in ('record', 'golden') and not self.meta:
            self.meta = self._sampled_meta()
            if self.meta:
                self.kind = 'v2'
        self._build_steps()
        self.fps = self._fps()
        self.base = self._meta_timeline()
        self.complete, self.gaps = self._complete_range()
        self._poses = None
        self._tracks = None
        self._rig = None

    def _json(self, name: str) -> dict | None:
        p = self.root / name
        if not p.is_file():
            return None
        try:
            return json.loads(p.read_text(encoding='utf-8'))
        except (OSError, ValueError) as e:
            self.warnings.append(f'{name}: unreadable ({e})')
            return None

    def _spec_cameras(self) -> list[dict]:
        cams = (self.spec or {}).get('cameras') or []
        if isinstance(cams, dict):
            cams = [dict(v, name=k) for k, v in cams.items()]
        defaults = (self.spec or {}).get('defaults') or {}
        out = []
        for c in cams:
            if isinstance(c, dict) and c.get('name'):
                merged = dict(defaults)
                merged.update(c)
                out.append(merged)
        return out

    def _discover_cameras(self) -> None:
        listed_run = (self.run or {}).get('cameras')
        run_cams = {c.get('name'): c for c in (listed_run if isinstance(listed_run, list) else []) if isinstance(c, dict)}
        listed = []
        for c in self._spec_cameras():
            listed.append((c['name'], c))
        for name in run_cams:
            if name and name not in dict(listed):
                listed.append((name, {}))
        seen = set()
        if self.replay_mode == 'golden':
            seen.add('golden')
            cam = self._scan_camera('golden', {'mode': 'game', 'label': 'Golden'}, {'folder': '.'})
            if cam is not None:
                self.cams['golden'] = cam
        for name, spec in listed:
            seen.add(name)
            cam = self._scan_camera(name, spec, run_cams.get(name, {}))
            if cam is not None:
                self.cams[name] = cam
        extra = []
        for d in sorted(self.root.iterdir()):
            name = d.name if d.is_dir() else (d.stem if d.suffix.lower() == '.mkv' else None)
            if name in REPLAY_DIRS:
                continue
            if name and name not in seen:
                cam = self._scan_camera(name, {}, {})
                if cam is not None:
                    seen.add(name)
                    extra.append(cam)
        extra.sort(key=lambda c: (c.name != 'game', c.name))
        for cam in extra:
            self.cams[cam.name] = cam

    def _scan_camera(self, name: str, spec: dict, run_cam: dict) -> Camera | None:
        folder = self.root / (run_cam.get('folder') or name)
        mode = str(spec.get('mode') or run_cam.get('mode') or ('game' if name == 'game' else ''))
        label = str(spec.get('label') or run_cam.get('label') or '')
        enabled = bool(spec.get('enabled', run_cam.get('enabled', True)))
        if folder.is_dir():
            files, exts = {}, set()
            with os.scandir(folder) as it:
                for e in it:
                    m = FRAME_RE.match(e.name)
                    if m and e.is_file():
                        files[int(m.group(1))] = Path(e.path)
                        exts.add(m.group(2).lower())
            if files:
                ext = 'rgba' if 'rgba' in exts else ('png' if exts == {'png'} else 'jpg')
                steps = np.array(sorted(files), np.int64)
                return Camera(name, folder, ext, mode, label, enabled, spec, steps, files)
        mkv = self.root / f'{name}.mkv'
        if mkv.is_file():
            return Camera(name, None, 'mkv', mode, label, enabled, spec, np.zeros(0, np.int64), {}, mkv)
        return None

    def _build_steps(self) -> None:
        if self.meta:
            rows = self.meta
            n = len(rows)
            self.step = np.array([inum(r.get('step'), i) for i, r in enumerate(rows)], np.int64)
            self.t = np.array([fnum(r.get('game_time')) for r in rows], np.float64)
            self.rate = np.array([fnum(r.get('rate'), 30.0) for r in rows], np.float64)
            self.group = np.array([max(1, inum(r.get('group'), 1)) for r in rows], np.int64)
            self.real = np.array([fnum(r.get('real_time')) for r in rows], np.float64)
            if self.kind == 'v2':
                self.frame = np.array([inum(r.get('frame'), -1) for r in rows], np.int64)
                self.sub = np.array([inum(r.get('sub'), -1) for r in rows], np.int64)
                self.captured = np.array([inum(r.get('captured'), 1) for r in rows], np.int64)
                self.speed = np.array([fnum(r.get('speed'), math.nan) for r in rows], np.float64)
                self.segment = np.array([inum(r.get('segment'), 0) for r in rows], np.int64)
            else:
                self.frame = np.full(n, -1, np.int64)
                self.sub = np.full(n, -1, np.int64)
                self.captured = np.ones(n, np.int64)
                self.speed = np.full(n, math.nan, np.float64)
                self.segment = np.zeros(n, np.int64)
            bad = ~np.isfinite(self.t)
            if bad.any():
                self.warnings.append(f'meta.csv: {int(bad.sum())} rows without game_time')
                self.t[bad] = np.interp(np.flatnonzero(bad), np.flatnonzero(~bad), self.t[~bad]) if (~bad).any() else 0.0
            return
        rate = float(self.cli_rate or 30.0)
        group = int(self.cli_group or 1)
        common = None
        for cam in self.cams.values():
            s = set(cam.steps.tolist())
            common = s if common is None else (common | s)
        steps = np.array(sorted(common or []), np.int64)
        n = len(steps)
        self.step = steps
        self.t = (steps + 1) / rate
        self.rate = np.full(n, rate)
        self.group = np.full(n, group, np.int64)
        self.frame = np.full(n, -1, np.int64)
        self.sub = np.full(n, -1, np.int64)
        self.captured = np.ones(n, np.int64)
        self.speed = np.full(n, math.nan)
        self.segment = np.zeros(n, np.int64)
        self.real = np.full(n, math.nan)

    def _sampled_meta(self) -> list[dict]:
        rec = self.record_dir()
        rows = read_csv(rec / 'record_meta.csv') if rec is not None else []
        if not rows:
            self.warnings.append(f'{self.replay_mode} run without record_meta.csv: sample times assumed at the fallback rate')
            return []
        have = set()
        for c in self.cams.values():
            have |= set(int(s) for s in c.steps)
        out = []
        for r in rows:
            s = inum(r.get('sample'))
            if s not in have:
                continue
            dt = fnum(r.get('dt'))
            out.append({'step': s, 'game_time': r.get('game_time'), 'rate': round(1.0 / dt, 3) if dt > 0 else 60.0, 'group': 1,
                        'frame': len(out), 'sub': 0, 'captured': 1, 'speed': 1.0, 'real_time': r.get('real_time'),
                        'fixed_steps': r.get('fixed_steps'), 'segment': 0})
        missing = len(have) - len(out)
        if missing > 0:
            self.warnings.append(f'{missing} frame file(s) without a record_meta.csv row, ignored')
        return out

    def _fps(self) -> float:
        return MA.resolve_fps(self.run, self.spec, self.kind, self.replay_mode, self.t[self.captured == 1],
                              self.rate[0] if len(self.rate) else None, self.group[0] if len(self.group) else None, 30.0)

    def _meta_timeline(self) -> Timeline:
        frames: list[OutFrame] = []
        idx_of = {int(s): i for i, s in enumerate(self.step)}
        if self.kind == 'v2':
            by_frame: dict[int, list[int]] = {}
            for i in range(len(self.step)):
                if self.captured[i] == 1 and self.frame[i] >= 0:
                    by_frame.setdefault(int(self.frame[i]), []).append(i)
            for fidx in sorted(by_frame):
                rows = by_frame[fidx]
                want = int(self.group[rows[0]])
                if len(rows) < want and fidx == max(by_frame):
                    self.warnings.append(f'meta.csv: last output frame {fidx} has {len(rows)} of {want} sub-steps, dropped')
                    continue
                first = rows[0]
                t0 = float(self.t[first - 1]) if first > 0 else 0.0
                spd = float(self.speed[rows[-1]])
                if not math.isfinite(spd) or spd <= 0:
                    span = float(self.t[rows[-1]]) - t0
                    spd = span * self.fps if span > 0 else 1.0
                if first == 0 and self.replay_mode:
                    t0 = max(0.0, float(self.t[rows[-1]]) - spd / self.fps)
                frames.append(OutFrame(tuple(int(self.step[i]) for i in rows), tuple([1.0 / len(rows)] * len(rows)),
                                       float(self.t[rows[-1]]), t0, spd, int(self.segment[rows[-1]]), '', fidx,
                                       float(self.real[rows[-1]])))
        else:
            cur: list[int] = []
            cur_group = None
            seg = 0
            prev_rate = None
            for i in range(len(self.step)):
                g = int(self.group[i])
                r = float(self.rate[i])
                if prev_rate is not None and (r != prev_rate or g != cur_group):
                    if cur:
                        frames.append(self._draft_frame(cur, seg))
                        self.warnings.append(f'meta.csv: partial frame of {len(cur)}/{cur_group} steps at step {self.step[cur[0]]} (rate change)')
                        cur = []
                    seg += 1
                prev_rate, cur_group = r, g
                cur.append(i)
                if len(cur) >= g:
                    frames.append(self._draft_frame(cur, seg))
                    cur = []
            if cur:
                self.warnings.append(f'meta.csv: trailing {len(cur)} steps do not fill a frame of {cur_group}, dropped')
            for k, f in enumerate(frames):
                f.index = k
        return Timeline(frames, self.fps, 'meta')

    def _draft_frame(self, rows: list[int], seg: int) -> OutFrame:
        first = rows[0]
        t0 = float(self.t[first - 1]) if first > 0 else float(self.t[first] - 1.0 / max(self.rate[first], 1e-6))
        g = int(self.group[rows[-1]])
        spd = self.fps * g / float(self.rate[rows[-1]]) if self.rate[rows[-1]] > 0 else 1.0
        return OutFrame(tuple(int(self.step[i]) for i in rows), tuple([1.0 / len(rows)] * len(rows)),
                        float(self.t[rows[-1]]), t0, spd, seg, '', -1, float(self.real[rows[-1]]))

    def _complete_range(self) -> tuple[int, list]:
        file_cams = [c for c in self.cams.values() if c.ext != 'mkv']
        n = len(self.base)
        last_ok = -1
        gaps = []
        for k, f in enumerate(self.base.frames):
            ok = True
            for c in file_cams:
                for s in f.steps:
                    if s not in c.files:
                        ok = False
                        gaps.append((c.name, s, k))
            if ok:
                last_ok = k
        inside = [g for g in gaps if g[2] <= last_ok]
        if inside:
            self.warnings.append(f'{len(inside)} missing frame files inside the complete range (nearest earlier step used): '
                                 + ', '.join(f'{c}/{s:05d}' for c, s, _ in inside[:8]) + (' ...' if len(inside) > 8 else ''))
        if last_ok + 1 < n:
            self.warnings.append(f'common complete range: {last_ok + 1} of {n} output frames')
        self.base = self.base.sliced(0, last_ok + 1)
        return last_ok + 1, inside

    def camera_names(self, enabled_only: bool = True) -> list[str]:
        return [c.name for c in self.cams.values() if c.enabled or not enabled_only]

    def frame_size(self, cam: str) -> tuple[int, int]:
        c = self.cams[cam]
        if c.ext == 'rgba':
            raw = (self.run or {}).get('raw') or {}
            return int(raw.get('w', 0)), int(raw.get('h', 0))
        if c.ext == 'mkv':
            return probe_size(c.video)
        p = c.files[int(c.steps[0])]
        with p.open('rb') as fh:
            head = fh.read(65536)
        w, h = jpeg_size(head) if head[:2] == bytes((0xFF, 0xD8)) else (0, 0)
        if w > 0:
            return w, h
        im = cv2.imdecode(np.fromfile(str(p), np.uint8), cv2.IMREAD_COLOR)
        if im is None:
            raise TakeError(f'cannot decode {p}')
        return im.shape[1], im.shape[0]

    def screen(self) -> tuple[int, int]:
        sc = (self.run or {}).get('screen')
        if isinstance(sc, list) and len(sc) == 2 and sc[0]:
            return int(sc[0]), int(sc[1])
        return self.frame_size(next(iter(self.cams)))

    def event_time(self, name: str) -> float:
        if name in self.event_overrides:
            return float(self.event_overrides[name])
        for r in self.events:
            if r.get('kind') == 'event' and r.get('name') == name:
                return fnum(r.get('game_time'))
        for e in (self.run or {}).get('events', []) or []:
            if isinstance(e, dict) and e.get('name') == name and e.get('fired'):
                return fnum(e['fired'][0])
        events = (self.spec or {}).get('events') or {}
        spec_ev = events.get(name) if isinstance(events, dict) else next((e for e in events if isinstance(e, dict) and e.get('name') == name), None)
        if isinstance(spec_ev, dict) and fnum(spec_ev.get('expect')) == fnum(spec_ev.get('expect')):
            self.warnings.append(f'event {name}: not fired in this take, using spec expect {spec_ev.get("expect")}')
            return fnum(spec_ev.get('expect'))
        return math.nan

    def resolve_time(self, value, default: float = math.nan) -> float:
        if value is None:
            return default
        name, off = parse_time_ref(value)
        if name is None:
            return off
        base = self.event_time(name)
        if math.isnan(base):
            raise TakeError(f'event "{name}" has no time in this take (events.csv, run.json, spec expect); pass --event {name}=<t>')
        return base + off

    def event_list(self) -> list[tuple[float, str, str]]:
        out = []
        for r in self.events:
            k = r.get('kind', '')
            if k in ('event', 'cue', 'shot', 'segment', 'mark', 'warn', 'error'):
                out.append((fnum(r.get('game_time')), k, r.get('name') or r.get('detail') or ''))
        for name, t in self.event_overrides.items():
            out.append((float(t), 'event', name))
        return sorted(out, key=lambda x: x[0])

    def speed_at(self, t: float) -> float:
        return self.base.speed_at(t)

    def step_index(self, step: int) -> int:
        i = int(np.searchsorted(self.step, step))
        return i if i < len(self.step) and self.step[i] == step else -1

    def compose(self) -> dict:
        c = (self.spec or {}).get('compose')
        return dict(c) if isinstance(c, dict) else {}

    def poses(self) -> dict:
        if self._poses is None:
            self._poses = load_poses(self.root / 'poses.csv')
        return self._poses

    def tracks(self) -> dict:
        if self._tracks is None:
            self._tracks = load_tracks(self.root / 'tracks.csv')
        return self._tracks

    def rig(self) -> list[dict]:
        if self._rig is None:
            self._rig = read_csv(self.root / 'rig.csv')
        return self._rig

    def real_of(self, step: int) -> float:
        i = self.step_index(int(step))
        return float(self.real[i]) if i >= 0 else math.nan

    def record_dir(self) -> Path | None:
        return find_record_dir(self.root, self.run, self.replay_mode)

    def record_json(self) -> dict | None:
        rec = self.record_dir()
        if rec is None or not (rec / 'record.json').is_file():
            return None
        try:
            return json.loads((rec / 'record.json').read_text(encoding='utf-8'))
        except (OSError, ValueError) as e:
            self.warnings.append(f'record.json: unreadable ({e})')
            return None

    def audio_state(self) -> dict:
        return audio_state(self.root, self.record_dir(), self.record_json())

    def summary(self) -> dict:
        segs = []
        for f in self.base.frames:
            if not segs or segs[-1]['segment'] != f.segment or abs(segs[-1]['speed'] - f.speed) > 1e-6 or segs[-1]['samples'] != len(f.steps):
                segs.append({'segment': f.segment, 'speed': round(f.speed, 6), 'samples': len(f.steps), 'from_t': round(f.t0, 4),
                             'frames': 0})
            segs[-1]['frames'] += 1
            segs[-1]['to_t'] = round(f.t, 4)
        real = [f.real for f in self.base.frames if math.isfinite(f.real)]
        out = {'take': str(self.root), 'name': self.name, 'kind': self.kind, 'mode': self.mode, 'fps': self.fps,
               'cameras': [{'name': c.name, 'mode': c.mode, 'label': c.label, 'enabled': c.enabled, 'format': c.ext,
                            'files': len(c.files) if c.ext != 'mkv' else None} for c in self.cams.values()],
               'steps': int(len(self.step)), 'captured_steps': int(self.captured.sum()) if len(self.captured) else 0,
               'frames': len(self.base), 'output_seconds': round(self.base.duration(), 3),
               'game_seconds': [round(float(self.base.frames[0].t0), 4), round(float(self.base.frames[-1].t), 4)] if len(self.base) else [0, 0],
               'real_seconds': [round(real[0], 4), round(real[-1], 4)] if real else None,
               'segments': segs, 'events': [{'t': round(t, 4), 'kind': k, 'name': n} for t, k, n in self.event_list()],
               'warnings': list(self.warnings)}
        if self.replay_mode:
            rec = self.record_dir()
            out['record_dir'] = str(rec) if rec else None
            out['audio'] = self.audio_state()
        return out


def find_record_dir(root: Path, run: dict | None, mode: str = '') -> Path | None:
    return MA.find_record_dir(root, run, mode)


def audio_state(take_root: Path, rec: Path | None, rj: dict | None) -> dict:
    au = (rj or {}).get('audio') if isinstance((rj or {}).get('audio'), dict) else {}
    track = Path(take_root) / 'audio_track.wav'
    wav = rec / 'audio.wav' if rec is not None else None
    usable = au.get('usable') if au else None
    capture = str(au.get('capture') or 'none') if au else ('none' if rj is not None else None)
    if rj is None:
        verdict = 'no recording (record.json not found)'
    elif capture != 'record' or au.get('started') is False:
        verdict = 'no record audio (audio.capture ' + str(capture) + (', not started' if au.get('started') is False else '') + ')'
    elif usable is False:
        err = fnum(au.get('slopeError'))
        verdict = 'unusable (record.json audio.usable false' + (f', slope error {err * 100:.2f} %' if math.isfinite(err) else '') + ')'
    elif usable is not True:
        verdict = 'not aligned (record.json audio.usable is not set: run mcam_audio.py align)'
    elif not track.is_file():
        verdict = 'aligned, no audio_track.wav (run mcam_audio.py track)'
    else:
        verdict = 'ok'
    return {'capture': capture, 'usable': usable, 'slope': au.get('slope'), 'slopeError': au.get('slopeError'),
            'residualMs': au.get('residualMs'), 'wav': str(wav) if wav is not None and wav.is_file() else None,
            'track': str(track) if track.is_file() else None, 'verdict': verdict, 'ok': verdict == 'ok'}


def jpeg_size(data: bytes) -> tuple[int, int]:
    i = 2
    n = len(data)
    while i + 9 < n:
        if data[i] != 0xFF:
            i += 1
            continue
        marker = data[i + 1]
        if marker in (0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF):
            h = (data[i + 5] << 8) | data[i + 6]
            w = (data[i + 7] << 8) | data[i + 8]
            return w, h
        if marker in (0xD8, 0x01) or 0xD0 <= marker <= 0xD7:
            i += 2
            continue
        seg = (data[i + 2] << 8) | data[i + 3]
        i += 2 + seg
    return 0, 0


_CUDA_OK: dict[str, bool] = {}
_VIDEO_INFO: dict[tuple, dict] = {}
_VIDEO_LOCK = threading.Lock()
PROBE_TIMEOUT_S = 30.0


def run_tool(cmd: list, timeout: float = PROBE_TIMEOUT_S) -> subprocess.CompletedProcess:
    try:
        return subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=timeout)
    except FileNotFoundError as e:
        raise TakeError(f'{cmd[0]} not found: set the FFMPEG / FFPROBE environment variables or put ffmpeg on PATH') from e
    except subprocess.TimeoutExpired as e:
        raise TakeError(f'{Path(str(cmd[0])).name} gave no answer in {timeout:.0f} s for {cmd[-1]}') from e
    except OSError as e:
        raise TakeError(f'cannot run {cmd[0]}: {e}') from e


def _rate(text) -> float:
    a, _, b = str(text or '').partition('/')
    try:
        v = float(a) / float(b or 1)
    except (ValueError, ZeroDivisionError):
        return 0.0
    return v if math.isfinite(v) and v > 0 else 0.0


def video_info(path: Path) -> dict:
    path = Path(path)
    try:
        st = path.stat()
    except OSError as e:
        raise TakeError(f'{path}: {e}') from e
    key = (str(path), st.st_mtime_ns, st.st_size)
    with _VIDEO_LOCK:
        hit = _VIDEO_INFO.get(key)
    if hit is not None:
        return hit
    p = run_tool([FFPROBE, '-v', 'error', '-select_streams', 'v:0', '-show_entries',
                  'stream=width,height,pix_fmt,r_frame_rate,avg_frame_rate', '-of', 'json', str(path)])
    tail = ' | '.join((p.stderr or '').strip().splitlines()[-2:])
    try:
        stream = (json.loads(p.stdout or '{}').get('streams') or [{}])[0] if p.returncode == 0 else {}
    except ValueError:
        stream = {}
    w, h = inum(stream.get('width'), 0), inum(stream.get('height'), 0)
    if w <= 0 or h <= 0:
        raise TakeError(f'{path}: no readable video stream (ffprobe exit {p.returncode}' + (f': {tail}' if tail else '') + ')')
    fps, avg = _rate(stream.get('r_frame_rate')), _rate(stream.get('avg_frame_rate'))
    info = {'width': w, 'height': h, 'pix_fmt': str(stream.get('pix_fmt') or ''), 'fps': fps,
            'cfr': fps > 0 and (avg <= 0 or abs(avg - fps) <= fps * 0.002)}
    with _VIDEO_LOCK:
        _VIDEO_INFO[key] = info
    return info


def gpu_download_format(path: Path) -> str:
    try:
        pix = video_info(path)['pix_fmt']
    except TakeError:
        pix = ''
    deep = any(k in pix for k in ('10', '12', '16'))
    if '444' in pix:
        return 'yuv444p16le' if deep else 'yuv444p'
    return 'p016le' if deep else 'nv12'


def cuda_ok(path: Path) -> bool:
    key = str(path)
    if key not in _CUDA_OK:
        try:
            p = subprocess.run([FFMPEG, '-v', 'error', '-hwaccel', 'cuda', '-hwaccel_output_format', 'cuda', '-i', key, '-frames:v', '1', '-vf',
                                f'scale_cuda=64:36,hwdownload,format={gpu_download_format(path)}', '-f', 'null', '-'],
                               capture_output=True, text=True, timeout=60)
            _CUDA_OK[key] = p.returncode == 0
        except (OSError, subprocess.SubprocessError):
            _CUDA_OK[key] = False
    return _CUDA_OK[key]


def probe_size(path: Path) -> tuple[int, int]:
    info = video_info(path)
    return info['width'], info['height']


def load_poses(path: Path) -> dict:
    rows = read_csv(path)
    out: dict[str, dict] = {}
    if not rows:
        return out
    cols = ['step', 'x', 'y', 'z', 'pitch', 'yaw', 'roll', 'fov', 'qx', 'qy', 'qz', 'qw', 'aim_x', 'aim_y', 'aim_z', 'near']
    tmp: dict[str, dict[str, list]] = {}
    for r in rows:
        d = tmp.setdefault(r.get('camera', ''), {k: [] for k in cols + ['flags', 'copy_of']})
        for k in cols:
            d[k].append(fnum(r.get(k)))
        d['flags'].append(r.get('flags', '') or '')
        d['copy_of'].append(r.get('copy_of', '') or '')
    for cam, d in tmp.items():
        e = {k: np.array(v, np.float64) for k, v in d.items() if k not in ('flags', 'copy_of')}
        e['step'] = e['step'].astype(np.int64)
        e['flags'] = d['flags']
        e['copy_of'] = d['copy_of']
        e['forward'] = forward_vectors(e)
        out[cam] = e
    return out


def forward_vectors(e: dict) -> np.ndarray:
    qx, qy, qz, qw = e['qx'], e['qy'], e['qz'], e['qw']
    fq = np.stack([2 * (qx * qz + qw * qy), 2 * (qy * qz - qw * qx), 1 - 2 * (qx * qx + qy * qy)], 1)
    p, y = np.radians(e['pitch']), np.radians(e['yaw'])
    fe = np.stack([np.cos(p) * np.sin(y), -np.sin(p), np.cos(p) * np.cos(y)], 1)
    have_q = np.isfinite(qw)[:, None]
    return np.where(have_q, fq, fe)


def signed_angle(a: np.ndarray) -> np.ndarray:
    return (np.asarray(a, np.float64) + 180.0) % 360.0 - 180.0


def load_tracks(path: Path) -> dict:
    rows = read_csv(path)
    out: dict[str, dict] = {}
    tmp: dict[str, dict[str, list]] = {}
    cols = ['step', 'game_time', 'ok', 'x', 'y', 'z', 'vx', 'vy', 'vz', 'count']
    for r in rows:
        d = tmp.setdefault(r.get('target', ''), {k: [] for k in cols})
        for k in cols:
            d[k].append(fnum(r.get(k)))
    for name, d in tmp.items():
        out[name] = {k: np.array(v, np.float64) for k, v in d.items()}
    return out


def build_warp(base: Timeline, keys: list, fps: float, shutter: float, interp: str = 'nearest',
               t_start: float | None = None, t_end: float | None = None, step_times: dict | None = None) -> Timeline:
    steps, segs = [], []
    for f in base.frames:
        for s in f.steps:
            steps.append(s)
            segs.append(f.segment)
    if not steps:
        return Timeline([], fps, 'warp')
    step_t = step_times or _step_times(base)
    ts = np.array([step_t[s] for s in steps], np.float64)
    order = np.argsort(ts, kind='stable')
    ts, sids = ts[order], np.array(steps, np.int64)[order]
    seg_arr = np.array(segs, np.int64)[order]
    keys = sorted([(float(k[0]), float(k[1])) for k in keys], key=lambda k: k[0]) or [(0.0, 1.0)]

    def speed(t: float) -> float:
        if t <= keys[0][0]:
            return keys[0][1]
        for (ta, sa), (tb, sb) in zip(keys, keys[1:]):
            if t <= tb:
                return sa + (sb - sa) * smoothstep((t - ta) / max(tb - ta, 1e-9))
        return keys[-1][1]

    spacing = float(np.median(np.diff(ts))) if len(ts) > 1 else 1.0 / fps
    T = float(ts[0] if t_start is None else max(t_start, ts[0]))
    end = float(ts[-1] if t_end is None else min(t_end, ts[-1]))
    frames: list[OutFrame] = []
    prev = None
    guard = 0
    while T <= end + 1e-9 and guard < 10_000_000:
        guard += 1
        s = speed(T)
        if s <= 0:
            raise TakeError('edit speed keys must be > 0')
        E = shutter / 360.0 * s / fps
        lo = int(np.searchsorted(ts, T - E + 1e-9, side='left'))
        hi = int(np.searchsorted(ts, T + 1e-9, side='right'))
        flag = ''
        if hi > lo and E >= spacing * 0.999:
            sel = list(range(lo, hi))
            w = [1.0 / len(sel)] * len(sel)
        else:
            c = T - E * 0.5
            j = int(np.searchsorted(ts, c))
            if interp == 'blend' and 0 < j < len(ts):
                a = (c - ts[j - 1]) / max(ts[j] - ts[j - 1], 1e-12)
                sel, w = [j - 1, j], [1.0 - a, a]
                if a <= 1e-6:
                    sel, w = [j - 1], [1.0]
                elif a >= 1 - 1e-6:
                    sel, w = [j], [1.0]
            else:
                cands = [x for x in (j - 1, j) if 0 <= x < len(ts)]
                best = min(cands, key=lambda x: abs(ts[x] - c))
                sel, w = [best], [1.0]
            flag = 'n'
        key = tuple(int(sids[i]) for i in sel)
        if prev is not None and key == prev and flag:
            flag = 'u'
        frames.append(OutFrame(key, tuple(w), T, T - E, s, int(seg_arr[sel[-1]]), flag))
        prev = key
        T += s / fps
    return Timeline(frames, fps, 'warp')


def _step_times(base: Timeline) -> dict:
    out = {}
    for f in base.frames:
        n = len(f.steps)
        if n == 1:
            out[f.steps[0]] = f.t
        else:
            dt = (f.t - f.t0) / n if f.t > f.t0 else 0.0
            for i, s in enumerate(f.steps):
                out[s] = f.t - (n - 1 - i) * dt
    return out


def attach_step_times(take: Take) -> dict:
    return {int(s): float(t) for s, t in zip(take.step, take.t)}


def read_exact(f, size: int) -> bytearray:
    buf = bytearray(size)
    mv = memoryview(buf)
    got = 0
    while got < size:
        k = f.readinto(mv[got:])
        if not k:
            break
        got += k
    return buf if got == size else buf[:got]


class MkvReader:
    LOOKBACK = 16
    MAX_LOOKBACK = 64
    AHEAD = 8
    AHEAD_CPU = 4
    SEEK_FORWARD = 240
    PRE_ROLL = 8

    def __init__(self, path: Path, w: int, h: int, reduce: int = 1, gpu: str = '', fps: float = 0.0):
        self.path, self.reduce, self.gpu = path, max(1, reduce), gpu
        self.w, self.h = (w, h) if self.reduce == 1 else (max(1, w // self.reduce), max(1, h // self.reduce))
        self.fps = float(fps) if fps and fps > 0 and os.environ.get('MCAM_NO_SEEK') != '1' else 0.0
        self.stall = max(1.0, fnum(os.environ.get('MCAM_FFMPEG_STALL_S'), 120.0))
        self.proc = None
        self.pos = 0
        self.frames: int | None = None
        self.lookback = self.LOOKBACK
        self.last_error = ''
        self.stalled = False
        self.opens = 0
        self.lock = threading.Lock()
        self.recent: OrderedDict = OrderedDict()
        self.ahead: queue.Queue | None = None
        self.pump: threading.Thread | None = None
        self.stop = threading.Event()
        self.errlog = None

    def command(self, start: int = 0) -> list:
        seek = ['-ss', f'{max(0.0, (start - 0.5) / self.fps):.6f}'] if start > 0 and self.fps > 0 else []
        if not self.gpu:
            vf = ['-vf', f'scale={self.w}:{self.h}:flags=area'] if self.reduce > 1 else []
            return [FFMPEG, '-v', 'error', *seek, '-i', str(self.path), *vf, '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-']
        vf = (f'scale_cuda={self.w}:{self.h}:interp_algo=bilinear,' if self.reduce > 1 else '') + f'hwdownload,format={self.gpu}'
        return [FFMPEG, '-v', 'error', '-hwaccel', 'cuda', '-hwaccel_output_format', 'cuda', *seek, '-i', str(self.path), '-vf', vf,
                '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-']

    def _open(self, start: int = 0) -> None:
        self.close()
        if self.fps <= 0:
            start = 0
        self.errlog = tempfile.TemporaryFile()
        try:
            self.proc = subprocess.Popen(self.command(start), stdout=subprocess.PIPE, stderr=self.errlog, bufsize=0)
        except OSError as e:
            self.errlog.close()
            self.errlog = None
            raise TakeError(f'{FFMPEG} cannot be started ({e}): set the FFMPEG environment variable or put ffmpeg on PATH') from e
        self.pos = start
        self.opens += 1
        self.stalled = False
        self.last_error = ''
        self.stop = threading.Event()
        self.ahead = queue.Queue(self.AHEAD if self.gpu else self.AHEAD_CPU)
        self.pump = threading.Thread(target=self._pump, args=(self.proc, self.ahead, self.stop), daemon=True)
        self.pump.start()

    def _pump(self, proc, q: queue.Queue, stop: threading.Event) -> None:
        size = self.w * self.h * 3
        while not stop.is_set():
            try:
                buf = read_exact(proc.stdout, size)
            except (OSError, ValueError):
                buf = b''
            item = buf if len(buf) == size else None
            while not stop.is_set():
                try:
                    q.put(item, timeout=0.2)
                    break
                except queue.Full:
                    continue
            if item is None:
                return

    def _next(self) -> bytes:
        waited = time.monotonic()
        while True:
            try:
                item = self.ahead.get(timeout=0.25)
            except queue.Empty:
                if self.pump is None or not self.pump.is_alive():
                    try:
                        item = self.ahead.get_nowait()
                    except queue.Empty:
                        return b''
                elif time.monotonic() - waited > self.stall:
                    self.stalled = True
                    return b''
                else:
                    continue
            return item if item is not None else b''

    def _error_tail(self) -> str:
        if self.errlog is None:
            return ''
        try:
            self.errlog.seek(0, 2)
            n = self.errlog.tell()
            self.errlog.seek(max(0, n - 600))
            text = self.errlog.read().decode('utf-8', 'replace')
        except (OSError, ValueError):
            return ''
        return ' | '.join(ln.strip() for ln in text.strip().splitlines()[-2:] if ln.strip())

    def held_bytes(self) -> int:
        q = self.ahead
        return (len(self.recent) + (q.qsize() if q is not None else 0)) * self.w * self.h * 3

    def read(self, index: int) -> np.ndarray:
        with self.lock:
            hit = self.recent.get(index)
            if hit is not None:
                return hit
            if self.frames is not None and index >= self.frames:
                raise TakeError(f'{self.path}: frame {index} not available (the video has {self.frames} frames'
                                + (f'; {self.last_error}' if self.last_error else '') + ')')
            if self.proc is None or index < self.pos or index - self.pos > self.SEEK_FORWARD:
                if self.proc is not None and 0 < self.pos - index <= self.MAX_LOOKBACK:
                    self.lookback = min(self.MAX_LOOKBACK, max(self.lookback * 2, self.pos - index + self.PRE_ROLL))
                self._open(max(0, index - self.PRE_ROLL))
            first = self.pos
            while True:
                buf = self._next()
                if len(buf) < self.w * self.h * 3:
                    end = self.pos
                    stalled = self.stalled
                    self.close()
                    if stalled:
                        raise TakeError(f'{self.path}: frame {index} not available: ffmpeg gave no frame for {self.stall:.0f} s'
                                        + (f' ({self.last_error})' if self.last_error else ''))
                    if end > first or first == 0:
                        self.frames = end if self.frames is None else min(self.frames, end)
                    raise TakeError(f'{self.path}: frame {index} not available' + (f' (the video has {end} frames)' if self.frames == end else '')
                                    + (f': {self.last_error}' if self.last_error else ''))
                im = np.frombuffer(buf, np.uint8).reshape(self.h, self.w, 3)
                im.setflags(write=False)
                self.recent[self.pos] = im
                self.recent.move_to_end(self.pos)
                while len(self.recent) > self.lookback:
                    self.recent.popitem(last=False)
                self.pos += 1
                if self.pos - 1 == index:
                    return im

    def close(self) -> None:
        self.stop.set()
        proc, self.proc = self.proc, None
        if proc is not None:
            try:
                proc.kill()
            except OSError:
                pass
        ended = True
        if self.pump is not None:
            self.pump.join(2)
            ended = not self.pump.is_alive()
            self.pump = None
        if proc is not None:
            if ended:
                try:
                    proc.stdout.close()
                except (OSError, ValueError):
                    pass
            try:
                proc.wait(timeout=5)
            except (OSError, subprocess.SubprocessError):
                pass
        self.ahead = None
        if self.errlog is not None:
            if proc is not None:
                self.last_error = self._error_tail()
            try:
                self.errlog.close()
            except OSError:
                pass
            self.errlog = None


class FrameStore:
    def __init__(self, take: Take, cache_mb: int = 1536, gpu: bool = False):
        self.take = take
        self.gpu = gpu
        self.cache: OrderedDict = OrderedDict()
        self.cache_bytes = 0
        self.limit = cache_mb * 1024 * 1024
        self.lock = threading.Lock()
        self.inflight: dict = {}
        self.mkv: dict[tuple, MkvReader] = {}
        self.mkv_index: dict[str, dict] = {}
        self.decodes = 0

    def _path(self, cam: Camera, step: int) -> Path:
        p = cam.files.get(step)
        if p is not None:
            return p
        i = int(np.searchsorted(cam.steps, step)) - 1
        if i < 0:
            i = 0
        return cam.files[int(cam.steps[i])]

    def _reader(self, cam: Camera, reduce: int, gpu: bool) -> MkvReader:
        key = (cam.name, reduce, gpu)
        with self.lock:
            reader = self.mkv.get(key)
        if reader is None:
            info = video_info(cam.video)
            made = MkvReader(cam.video, info['width'], info['height'], reduce, gpu_download_format(cam.video) if gpu else '',
                             info['fps'] if info['cfr'] else 0.0)
            with self.lock:
                reader = self.mkv.setdefault(key, made)
        return reader

    def _frame_index(self, cam: Camera, step: int) -> int:
        with self.lock:
            index = self.mkv_index.get(cam.name)
            if index is None:
                cap = [int(s) for s, c in zip(self.take.step, self.take.captured) if c == 1]
                index = self.mkv_index[cam.name] = {s: i for i, s in enumerate(cap)}
        idx = index.get(step)
        if idx is None:
            raise TakeError(f'{cam.name}.mkv: step {step} was not captured')
        return idx

    def stats_frame(self, cam: Camera, step: int) -> np.ndarray:
        if cam.ext != 'mkv':
            return self._decode(cam, step, STATS_REDUCE, exact=True)
        return self._reader(cam, STATS_REDUCE, False).read(self._frame_index(cam, step))

    def _decode(self, cam: Camera, step: int, reduce: int, exact: bool = False) -> np.ndarray:
        if cam.ext == 'mkv':
            r = reduce if self.gpu and not exact and reduce in (2, 4, 8) else 1
            im = self._reader(cam, r, self.gpu).read(self._frame_index(cam, step))
            return im if r == reduce else shrink(im, reduce)
        path = self._path(cam, step)
        if cam.ext == 'rgba':
            w, h = self.take.frame_size(cam.name)
            a = np.fromfile(str(path), np.uint8)
            if a.size != w * h * 4:
                raise TakeError(f'{path}: {a.size} bytes, expected {w}x{h}x4')
            im = np.ascontiguousarray(a.reshape(h, w, 4)[::-1, :, :3])
            return shrink(im, reduce)
        data = np.fromfile(str(path), np.uint8)
        im = cv2.imdecode(data, REDUCE_FLAGS.get(reduce, cv2.IMREAD_COLOR))
        if im is None:
            raise TakeError(f'cannot decode {path}')
        if reduce not in REDUCE_FLAGS:
            im = shrink(im, reduce)
        return cv2.cvtColor(im, cv2.COLOR_BGR2RGB)

    def get(self, cam_name: str, step: int, reduce: int = 1) -> np.ndarray:
        key = (cam_name, int(step), int(reduce))
        while True:
            with self.lock:
                hit = self.cache.get(key)
                if hit is not None:
                    self.cache.move_to_end(key)
                    return hit
                ev = self.inflight.get(key)
                if ev is None:
                    ev = threading.Event()
                    self.inflight[key] = ev
                    break
            ev.wait()
        try:
            im = self._decode(self.take.cams[cam_name], int(step), int(reduce))
            im.setflags(write=False)
            with self.lock:
                self.decodes += 1
                self.cache[key] = im
                self.cache_bytes += im.nbytes
                held = sum(r.held_bytes() for r in self.mkv.values())
                while self.cache_bytes + held > self.limit and len(self.cache) > 1:
                    _, old = self.cache.popitem(last=False)
                    self.cache_bytes -= old.nbytes
            return im
        finally:
            with self.lock:
                self.inflight.pop(key, None)
            ev.set()

    def close(self) -> None:
        with self.lock:
            readers = list(self.mkv.values())
        for r in readers:
            r.close()

    def drop_readers(self) -> None:
        with self.lock:
            readers, self.mkv = list(self.mkv.values()), {}
        for r in readers:
            r.close()


def shrink(im: np.ndarray, reduce: int) -> np.ndarray:
    if reduce <= 1:
        return im
    h, w = im.shape[:2]
    return cv2.resize(im, (max(1, w // reduce), max(1, h // reduce)), interpolation=cv2.INTER_AREA)


def stats_dir(take: Take) -> Path:
    h = hashlib.sha1(str(take.root).lower().encode('utf-8')).hexdigest()[:10]
    base = Path(os.environ['INSIDE_STATS_DIR']) if os.environ.get('INSIDE_STATS_DIR') else OUT_ROOT / '_stats'
    return base / f'{take.root.name}_{h}'


def camera_stats(take: Take, store: FrameStore, cam: str, steps: list[int], workers: int = 8) -> dict:
    from concurrent.futures import ThreadPoolExecutor
    c = take.cams[cam]
    steps_arr = np.array(sorted(set(int(s) for s in steps)), np.int64)
    mtime = max((os.path.getmtime(c.files[int(s)]) for s in steps_arr if int(s) in c.files), default=0.0) if c.ext != 'mkv' else os.path.getmtime(c.video)
    d = stats_dir(take)
    path = d / f'{cam}.npz'
    if path.is_file():
        try:
            z = np.load(path)
            if int(z['version']) == STATS_VERSION and np.array_equal(z['steps'], steps_arr) and abs(float(z['mtime']) - mtime) < 1e-3:
                return {k: z[k] for k in z.files}
        except (OSError, ValueError, KeyError):
            pass

    def one(s: int):
        try:
            im = store.stats_frame(c, int(s))
        except Exception:
            return None
        y = (im.astype(np.float32) @ LUMA)
        hist = np.bincount(np.clip(y + 0.5, 0, 255).astype(np.int64).ravel(), minlength=256)[:256]
        lin = (y / 255.0) ** 2.2
        return hist, float(np.exp(np.mean(np.log(lin + 1e-5)))), cv2.resize(y, (80, 45), interpolation=cv2.INTER_AREA)

    with ThreadPoolExecutor(workers) as pool:
        res = list(pool.map(one, steps_arr.tolist()))
    n = len(steps_arr)
    hists = np.zeros((n, 256), np.int32)
    logmean = np.full(n, np.nan)
    ok = np.zeros(n, bool)
    energy = np.zeros(n)
    prev = None
    for i, r in enumerate(res):
        if r is None:
            prev = None
            continue
        hists[i], logmean[i], thumb = r
        ok[i] = True
        energy[i] = float(np.mean(np.abs(thumb - prev))) if prev is not None else 0.0
        prev = thumb
    cs = np.cumsum(hists, 1)
    tot = np.maximum(cs[:, -1:], 1)
    p50 = np.argmax(cs >= tot * 0.5, 1).astype(np.float64)
    p005 = np.argmax(cs >= tot * 0.005, 1).astype(np.float64)
    p99 = np.argmax(cs >= tot * 0.99, 1).astype(np.float64)
    mean = (hists * np.arange(256)).sum(1) / tot[:, 0]
    clip = hists[:, 254:].sum(1) / tot[:, 0]
    crush = hists[:, :2].sum(1) / tot[:, 0]
    out = {'version': np.array(STATS_VERSION), 'steps': steps_arr, 'mtime': np.array(mtime), 'hist': hists, 'logmean': logmean,
           'ok': ok, 'energy': energy, 'p50': p50, 'p005': p005, 'p99': p99, 'mean': mean, 'clip': clip, 'crush': crush}
    try:
        d.mkdir(parents=True, exist_ok=True)
        np.savez_compressed(path, **out)
    except OSError:
        pass
    return out


def stats_job(root: str, rate: float | None, group: int | None, events: dict | None, cam: str, gpu: bool = False, workers: int = 8) -> str:
    take = Take(Path(root), rate, group, events)
    store = FrameStore(take, cache_mb=64, gpu=gpu)
    try:
        camera_stats(take, store, cam, sorted({s for f in take.base.frames for s in f.steps}), workers)
    finally:
        store.close()
    return cam


def pooled_percentile(hist: np.ndarray, q: float) -> float:
    h = hist.sum(0) if hist.ndim == 2 else hist
    cs = np.cumsum(h)
    if cs[-1] <= 0:
        return 0.0
    return float(np.argmax(cs >= cs[-1] * q))


@dataclass
class Wav:
    path: Path
    rate: int
    channels: int
    bits: int
    fmt: int
    frames: int
    data: np.ndarray | None
    markers: list = field(default_factory=list)
    cues: dict = field(default_factory=dict)
    problems: list = field(default_factory=list)

    @property
    def seconds(self) -> float:
        return self.frames / self.rate if self.rate > 0 else 0.0

    def mono(self) -> np.ndarray:
        if self.data is None:
            return np.zeros(0, np.float32)
        return self.data.mean(axis=1) if self.data.ndim == 2 else self.data


def read_wav(path: Path, with_data: bool = True) -> Wav:
    try:
        w = MA.load_wav(path, with_data)
    except MA.AudioError as e:
        raise TakeError(str(e)) from None
    cues: dict[str, int] = {}
    for m in w.markers:
        if m['label'] and m['label'] not in cues:
            cues[m['label']] = m['sample']
    return Wav(w.path, int(w.rate), int(w.channels), int(w.bits), int(w.format), int(w.frames), w.data, w.markers, cues, w.problems)


def write_wav(path: Path, data: np.ndarray, rate: int, pcm16: bool = False, markers: list | None = None) -> Path:
    return MA.write_wav(path, data, rate, pcm16, markers)


def dbfs(x) -> float:
    return MA.dbfs(x)


def fit_markers(wav: Wav, rows: list[dict], real_of_game=None) -> dict:
    return MA.fit_markers(wav, rows, real_of_game)
