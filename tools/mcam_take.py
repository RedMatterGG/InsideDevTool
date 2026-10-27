"""MultiCam take reader shared by mcam_compose.py and mcam_qa.py: camera folders, meta/poses/events/tracks/rig CSVs (v2,
v2-draft meta without frame columns, v1 JPEG folders only), replay run.json modes (replay/take keep the v2 timeline and
expose real_time, record/golden read record_meta.csv), output-frame timelines (meta frames and warp mode), event time
references, record folder and audio state, WAV read/write with cue/labl markers and the lag-0 marker fit (REPLAY_DESIGN
6.2), a thread-safe frame store (jpg / raw rgba / pipe mkv) and cached per-camera luma statistics. GPU mode (FrameStore
gpu=True, the compositor default): pipe mkv frames decode on NVDEC (-hwaccel cuda) and shrink by 2 / 4 / 8 on the GPU
(scale_cuda bilinear) before the download, one ffmpeg reader per camera and reduce; full-size frames are bit-identical to the
CPU decode, shrunk ones differ from the CPU INTER_AREA shrink by about 1/255 on edges (51-52 dB PSNR); luma statistics (grade
floors) always use the full-size decode + INTER_AREA, so grades are identical in both modes."""
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
import threading
from collections import OrderedDict
from dataclasses import dataclass, field
from pathlib import Path

import cv2
import numpy as np

REPO = Path(__file__).resolve().parent.parent
OUT_ROOT = REPO / 'out'
FFMPEG = os.environ.get('FFMPEG') or shutil.which('ffmpeg') or 'ffmpeg'
FFPROBE = os.environ.get('FFPROBE') or shutil.which('ffprobe') or 'ffprobe'
LUMA = np.array([0.22, 0.707, 0.071], np.float32)
FRAME_RE = re.compile(r'^(\d+)\.(jpg|jpeg|png|rgba)$', re.I)
TIME_RE = re.compile(r'^@([A-Za-z0-9_.]+)\s*(?:([+-])\s*([0-9]*\.?[0-9]+))?$')
REDUCE_FLAGS = {1: cv2.IMREAD_COLOR, 2: cv2.IMREAD_REDUCED_COLOR_2, 4: cv2.IMREAD_REDUCED_COLOR_4, 8: cv2.IMREAD_REDUCED_COLOR_8}
STATS_VERSION = 3
REPLAY_MODES = ('record', 'replay', 'take', 'passes', 'golden')
REPLAY_DIRS = {'record', 'golden', 'passes', 'noise'}
SLOPE_LIMIT = 0.005


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
    try:
        if v is None or v == '':
            return default
        return float(v)
    except (TypeError, ValueError):
        return default


def inum(v, default: int = -1) -> int:
    f = fnum(v)
    return default if math.isnan(f) else int(round(f))


def parse_time_ref(value) -> tuple[str | None, float]:
    if value is None:
        return None, math.nan
    if isinstance(value, (int, float)):
        return None, float(value)
    s = str(value).strip()
    if not s.startswith('@'):
        return None, fnum(s)
    m = TIME_RE.match(s)
    if not m:
        raise TakeError(f'bad time reference {value!r} (use a number or "@event+1.5")')
    off = float(m.group(3)) if m.group(3) else 0.0
    return m.group(1), (-off if m.group(2) == '-' else off)


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
        run_cams = {c.get('name'): c for c in (self.run or {}).get('cameras', []) if isinstance(c, dict)}
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
        if self.replay_mode in ('record', 'golden') and self.kind == 'v2':
            cap = np.sort(self.t[self.captured == 1])
            d = np.diff(cap)
            d = d[d > 1e-6]
            if len(d):
                return float(max(1.0, round(1.0 / float(np.median(d)))))
        run_fps = fnum((self.run or {}).get('fps'))
        if run_fps > 0:
            return run_fps
        timing = (self.spec or {}).get('timing') or {}
        if self.kind == 'v2' and fnum(timing.get('fps')) > 0:
            return fnum(timing.get('fps'))
        if self.kind in ('v2-draft', 'v1') and fnum(timing.get('fps')) > 0 and not (self.spec or {}).get('rates'):
            return fnum(timing.get('fps'))
        if len(self.rate):
            return float(self.rate[0] / max(1, self.group[0]))
        return 30.0

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
    root = Path(root)
    if mode == 'record' or (root / 'record_meta.csv').is_file() or (root / 'scene.srec').is_file():
        return root
    run = run or {}
    cands = []
    rec = run.get('recording')
    if isinstance(rec, dict) and rec.get('dir'):
        cands.append(rec.get('dir'))
    elif isinstance(rec, str) and rec:
        cands.append(rec)
    for key, sub in (('replay', 'source'), ('record', 'dir')):
        d = run.get(key)
        if isinstance(d, dict) and d.get(sub):
            cands.append(d.get(sub))
    cands += [root / 'record', root.parent / 'record']
    for c in cands:
        p = Path(c)
        if not p.is_absolute():
            p = root / p
        if (p / 'record.json').is_file() or (p / 'record_meta.csv').is_file() or (p / 'scene.srec').is_file():
            return p.resolve()
    return None


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


def gpu_download_format(path: Path) -> str:
    p = subprocess.run([FFPROBE, '-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=pix_fmt', '-of', 'csv=p=0', str(path)],
                       capture_output=True, text=True)
    pix = p.stdout.strip()
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
    p = subprocess.run([FFPROBE, '-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=width,height', '-of', 'csv=p=0',
                        str(path)], capture_output=True, text=True)
    w, h = (p.stdout.strip().split(',') + ['0', '0'])[:2]
    return int(w or 0), int(h or 0)


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
    LOOKBACK = 24
    AHEAD = 8

    def __init__(self, path: Path, w: int, h: int, reduce: int = 1, gpu: str = ''):
        self.path, self.reduce, self.gpu = path, max(1, reduce), gpu
        self.w, self.h = (w, h) if self.reduce == 1 else (max(1, w // self.reduce), max(1, h // self.reduce))
        self.proc = None
        self.pos = 0
        self.lock = threading.Lock()
        self.recent: OrderedDict = OrderedDict()
        self.ahead: queue.Queue | None = None
        self.pump: threading.Thread | None = None
        self.stop = threading.Event()

    def command(self) -> list:
        if not self.gpu:
            return [FFMPEG, '-v', 'error', '-i', str(self.path), '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-']
        vf = (f'scale_cuda={self.w}:{self.h}:interp_algo=bilinear,' if self.reduce > 1 else '') + f'hwdownload,format={self.gpu}'
        return [FFMPEG, '-v', 'error', '-hwaccel', 'cuda', '-hwaccel_output_format', 'cuda', '-i', str(self.path), '-vf', vf, '-f', 'rawvideo',
                '-pix_fmt', 'rgb24', '-']

    def _open(self) -> None:
        self.close()
        self.proc = subprocess.Popen(self.command(), stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=0)
        self.pos = 0
        if self.gpu:
            self.stop = threading.Event()
            self.ahead = queue.Queue(self.AHEAD)
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

    def _next(self, size: int) -> bytes:
        if self.ahead is None:
            return read_exact(self.proc.stdout, size)
        item = self.ahead.get()
        return item if item is not None else b''

    def read(self, index: int) -> np.ndarray:
        with self.lock:
            hit = self.recent.get(index)
            if hit is not None:
                return hit.copy()
            if self.proc is None or index < self.pos:
                self._open()
            size = self.w * self.h * 3
            while True:
                buf = self._next(size)
                if len(buf) < size:
                    raise TakeError(f'{self.path}: frame {index} not available')
                im = np.frombuffer(buf, np.uint8).reshape(self.h, self.w, 3)
                self.recent[self.pos] = im
                while len(self.recent) > self.LOOKBACK:
                    self.recent.popitem(last=False)
                if self.pos == index:
                    self.pos += 1
                    return im.copy()
                self.pos += 1

    def close(self) -> None:
        self.stop.set()
        if self.proc is not None:
            try:
                self.proc.kill()
            except OSError:
                pass
            self.proc = None
        if self.pump is not None:
            self.pump.join(2)
            self.pump = None
        self.ahead = None


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

    def _decode(self, cam: Camera, step: int, reduce: int, exact: bool = False) -> np.ndarray:
        if cam.ext == 'mkv':
            r = reduce if self.gpu and not exact and reduce in (2, 4, 8) else 1
            key = (cam.name, r)
            with self.lock:
                reader = self.mkv.get(key)
                if reader is None:
                    w, h = probe_size(cam.video)
                    reader = self.mkv[key] = MkvReader(cam.video, w, h, r, gpu_download_format(cam.video) if self.gpu else '')
                if cam.name not in self.mkv_index:
                    cap = [int(s) for s, c in zip(self.take.step, self.take.captured) if c == 1]
                    self.mkv_index[cam.name] = {s: i for i, s in enumerate(cap)}
            idx = self.mkv_index[cam.name].get(step)
            if idx is None:
                raise TakeError(f'{cam.name}.mkv: step {step} was not captured')
            im = reader.read(idx)
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
                while self.cache_bytes > self.limit and len(self.cache) > 1:
                    _, old = self.cache.popitem(last=False)
                    self.cache_bytes -= old.nbytes
            return im
        finally:
            with self.lock:
                self.inflight.pop(key, None)
            ev.set()

    def close(self) -> None:
        for r in self.mkv.values():
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
    return OUT_ROOT / '_stats' / f'{take.root.name}_{h}'


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
            im = store._decode(c, int(s), 8, exact=True)
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
    d.mkdir(parents=True, exist_ok=True)
    try:
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


def _decode_pcm(raw: bytes, fmt: int, bits: int, channels: int) -> np.ndarray:
    if fmt == 3 and bits == 32:
        a = np.frombuffer(raw, '<f4').astype(np.float32)
    elif fmt == 3 and bits == 64:
        a = np.frombuffer(raw, '<f8').astype(np.float32)
    elif bits == 16:
        a = np.frombuffer(raw, '<i2').astype(np.float32) / 32768.0
    elif bits == 24:
        b = np.frombuffer(raw[:len(raw) // 3 * 3], np.uint8).reshape(-1, 3).astype(np.int32)
        v = b[:, 0] | (b[:, 1] << 8) | (b[:, 2] << 16)
        a = (np.where(v >= 1 << 23, v - (1 << 24), v)).astype(np.float32) / float(1 << 23)
    elif bits == 32:
        a = np.frombuffer(raw, '<i4').astype(np.float32) / float(1 << 31)
    elif bits == 8:
        a = (np.frombuffer(raw, np.uint8).astype(np.float32) - 128.0) / 128.0
    else:
        raise TakeError(f'unsupported WAV sample format {fmt}/{bits} bit')
    ch = max(1, channels)
    return a[:len(a) // ch * ch].reshape(-1, ch)


def read_wav(path: Path, with_data: bool = True) -> Wav:
    path = Path(path)
    if not path.is_file():
        raise TakeError(f'{path}: not found')
    size = path.stat().st_size
    problems: list[str] = []
    fmt = channels = rate = bits = align = 0
    data_off = data_len = -1
    cue_pos: dict[int, int] = {}
    labels: dict[int, str] = {}
    with path.open('rb') as fh:
        head = fh.read(12)
        if len(head) < 12 or head[:4] != b'RIFF' or head[8:12] != b'WAVE':
            raise TakeError(f'{path}: not a RIFF/WAVE file')
        riff = struct.unpack('<I', head[4:8])[0]
        if riff == 0xFFFFFFFF or riff + 8 != size:
            problems.append(f'RIFF size {riff} + 8 != file size {size}' + (' (unfinalized: mcam_audio.py fix)' if riff == 0xFFFFFFFF else ''))
        pos = 12
        while pos + 8 <= size:
            fh.seek(pos)
            hdr = fh.read(8)
            cid, n = hdr[:4], struct.unpack('<I', hdr[4:8])[0]
            body = pos + 8
            if cid == b'data':
                if n == 0xFFFFFFFF or body + n > size:
                    problems.append(f'data size {n} runs past the file end ({size - body} bytes left)'
                                    + (' (unfinalized: mcam_audio.py fix)' if n == 0xFFFFFFFF else ''))
                    n = size - body
                data_off, data_len = body, n
            elif cid == b'fmt ' and n >= 16:
                b = fh.read(n)
                fmt, channels, rate, _, align, bits = struct.unpack('<HHIIHH', b[:16])
                if fmt == 0xFFFE and n >= 26:
                    fmt = struct.unpack('<H', b[24:26])[0]
            elif cid == b'cue ' and n >= 4:
                b = fh.read(n)
                cnt = struct.unpack('<I', b[:4])[0]
                for k in range(min(cnt, (n - 4) // 24)):
                    cid_k, position, _, _, _, offset = struct.unpack('<II4sIII', b[4 + 24 * k:28 + 24 * k])
                    cue_pos[cid_k] = offset if offset or not position else position
            elif cid == b'LIST' and n >= 4:
                b = fh.read(n)
                if b[:4] == b'adtl':
                    i = 4
                    while i + 8 <= len(b):
                        sid, sn = b[i:i + 4], struct.unpack('<I', b[i + 4:i + 8])[0]
                        if sid == b'labl' and sn >= 4:
                            lid = struct.unpack('<I', b[i + 8:i + 12])[0]
                            labels[lid] = b[i + 12:i + 8 + sn].split(b'\0', 1)[0].decode('latin-1')
                        i += 8 + sn + (sn & 1)
            if n == 0xFFFFFFFF:
                break
            pos = body + n + (n & 1)
        if fmt == 0 or rate <= 0 or channels <= 0:
            raise TakeError(f'{path}: no fmt chunk')
        if data_off < 0:
            raise TakeError(f'{path}: no data chunk')
        align = align or channels * bits // 8
        frames = data_len // max(1, align)
        data = None
        if with_data:
            fh.seek(data_off)
            data = _decode_pcm(fh.read(frames * align), fmt, bits, channels)
    markers = [{'id': k, 'sample': int(v), 'label': labels.get(k, '')} for k, v in sorted(cue_pos.items(), key=lambda kv: kv[1])]
    cues: dict[str, int] = {}
    for m in markers:
        if m['label'] and m['label'] not in cues:
            cues[m['label']] = m['sample']
    return Wav(path, int(rate), int(channels), int(bits), int(fmt), int(frames), data, markers, cues, problems)


def write_wav(path: Path, data: np.ndarray, rate: int, pcm16: bool = False, markers: list | None = None) -> Path:
    path = Path(path)
    a = np.asarray(data, np.float32)
    if a.ndim == 1:
        a = a[:, None]
    ch = a.shape[1]
    if pcm16:
        raw = np.clip(np.round(a * 32768.0), -32768, 32767).astype('<i2').tobytes()
        fmt = struct.pack('<HHIIHH', 1, ch, rate, rate * ch * 2, ch * 2, 16)
        extra = b''
    else:
        raw = a.astype('<f4').tobytes()
        fmt = struct.pack('<HHIIHHH', 3, ch, rate, rate * ch * 4, ch * 4, 32, 0)
        extra = b'fact' + struct.pack('<II', 4, a.shape[0])
    chunks = b'fmt ' + struct.pack('<I', len(fmt)) + fmt + extra + b'data' + struct.pack('<I', len(raw)) + raw
    if len(raw) & 1:
        chunks += b'\0'
    if markers:
        cue = struct.pack('<I', len(markers)) + b''.join(struct.pack('<II4sIII', i + 1, int(s), b'data', 0, 0, int(s))
                                                         for i, (s, _) in enumerate(markers))
        adtl = b'adtl'
        for i, (_, label) in enumerate(markers):
            body = struct.pack('<I', i + 1) + label.encode('ascii') + b'\0'
            adtl += b'labl' + struct.pack('<I', len(body)) + body + (b'\0' if len(body) & 1 else b'')
        chunks += b'cue ' + struct.pack('<I', len(cue)) + cue + b'LIST' + struct.pack('<I', len(adtl)) + adtl
    path.write_bytes(b'RIFF' + struct.pack('<I', 4 + len(chunks)) + b'WAVE' + chunks)
    return path


def dbfs(x) -> float:
    if x is None or not len(x):
        return -200.0
    rms = float(np.sqrt(np.mean(np.square(np.asarray(x, np.float64)))))
    return 20.0 * math.log10(rms) if rms > 1e-10 else -200.0


def fit_markers(wav: Wav, rows: list[dict], real_of_game=None) -> dict:
    pts, missing, events = [], [], []
    for r in rows:
        label = (r.get('label') or '').strip()
        pos = wav.cues.get(label)
        if pos is None:
            missing.append(label)
            continue
        pts.append((fnum(r.get('real_time')), float(pos), label, inum(r.get('lag'), 0), fnum(r.get('game_time')), inum(r.get('sample'))))
    lag0 = [p for p in pts if p[3] == 0 and math.isfinite(p[0])]
    out = {'rate': wav.rate, 'markers': len(pts), 'lag0': len(lag0), 'missing': missing, 'ok': False}
    if len(lag0) < 2:
        out['reason'] = f'{len(lag0)} lag-0 marker(s) matched between audio_sync.csv and the WAV cue/labl chunks (2 needed)'
        return out
    x = np.array([p[0] for p in lag0], np.float64)
    y = np.array([p[1] for p in lag0], np.float64)
    if np.ptp(x) <= 1e-6:
        out['reason'] = 'lag-0 markers share one real_time'
        return out
    a, b = np.polyfit(x, y, 1)
    res = y - (a * x + b)
    ratio = a / wav.rate
    quantum = math.gcd(*(int(v) for v in y))
    out.update({'ok': True, 'a': float(a), 'b': float(b), 'ratio': float(ratio), 'slope_error': float(abs(ratio - 1.0)),
                'quantum': quantum if 256 <= quantum <= 65536 else 1,
                'residual_ms': float(np.sqrt(np.mean(res ** 2)) / wav.rate * 1000.0),
                'residual_max_ms': float(np.max(np.abs(res)) / wav.rate * 1000.0),
                'usable': bool(abs(ratio - 1.0) < SLOPE_LIMIT)})
    for p in pts:
        if p[3] == 0:
            continue
        e = {'label': p[2], 'lag': p[3], 'game_time': p[4], 'sample': p[5],
             'offset_post_ms': round((p[1] - (a * p[0] + b)) / wav.rate * 1000.0, 3)}
        if real_of_game is not None and math.isfinite(p[4]):
            r = real_of_game(p[4])
            if math.isfinite(r):
                e['offset_event_ms'] = round((p[1] - (a * r + b)) / wav.rate * 1000.0, 3)
        events.append(e)
    out['events'] = events
    return out
