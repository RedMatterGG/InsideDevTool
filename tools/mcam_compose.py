"""MultiCam compositor: per-camera angle videos, Blender-style layouts, edited cuts with dissolves/dips and labels, director
blocks and autocut drafts from a capture take (v2 meta.csv, v2-draft or v1 JPEG folders) or a replay take. Sub-step motion
blur in linear light, per-camera floor-anchored shadow lift and exposure with time ranges (grade.<...>.ranges: extra EV, lift,
k, saturation from `from` to `to` with `ramp` s ramps) per camera and per layout cell (cell `grade`), H.265 only (hevc_nvenc
Main10 by default, libx265 Main10 only with --codec x265; drafts hevc_nvenc at half size); no sharpening anywhere (cells
shrink with INTER_AREA, the encoder gets the rendered size). `review` writes full-resolution PNG frames of every output into
review/<output>/ (every `near` s within `around` s of the review events, every `every` s elsewhere) with an index.csv. `all` makes the angle videos and the layouts; `finals` only the
director angle (director cameras no layout shows, or --cams / compose.finals.cams) and the layouts, grade stats of all their
cameras in parallel processes, --jobs (1) videos encoded at once; a cut only from
an explicit edit (compose.edit or --edit), never an autocut (layouts and the director camera are the default edit). Audio: none by default; --audio record muxes the replay take's audio_track.wav (REPLAY_DESIGN 6.3) as AAC-LC
48 kHz 320 kb/s in the MP4 (or FLAC in MKV with --audio-codec flac), following the cut's shots, transitions and fades;
refused unless record.json audio.usable is true. Never plays audio.
Decode: pipe-mode camera videos (<cam>.mkv) decode on the GPU by default (NVDEC, -hwaccel cuda) and shrink for layout cells on
the GPU (scale_cuda) before the download (mcam_take.FrameStore gpu); grading (time-varying per-camera / per-cell LUTs),
cropping and compositing stay in numpy (cheap copies, identical grades), the RGB -> BT.709 limited 10-bit conversion stays in
ffmpeg zscale (NVENC's own RGB input path tags the stream BT.601) and hevc_nvenc encodes. --cpu is the old CPU decode path.
Outputs go to --out DIR, else <MCAM_OUT>/<take name> (environment), else <compose.output.root>/<take name> (spec.json), else
the folder an earlier version used for this take (<tools>/../out/<take name>, only when it exists), else <take>/out.
--resume skips a video that is already there, verifies and was made from the same inputs; --json prints one JSON object on
stdout and sends the progress lines to stderr; `doctor [take]` checks ffmpeg, the encoders and filters, memory and the output
folder before a first run. Exit codes: 0 ok, 1 an output failed its check or the encode failed, 2 input or usage error,
3 refused (audio not usable, another encode running, not enough memory, foreign files in the output folder).
Usage: python -I tools/mcam_compose.py <scan|angles|layout|cut|all|finals|director|autocut|grade|review> <take> [options]
       python -I tools/mcam_compose.py doctor [take] [--out DIR] [--json]"""
from __future__ import annotations

import argparse
import csv
import ctypes
import fnmatch
import hashlib
import json
import math
import os
import sys
import tempfile
import threading
import time
from collections import deque
from concurrent.futures import ProcessPoolExecutor, ThreadPoolExecutor
from dataclasses import asdict, dataclass, field, replace
from functools import partial
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mcam_audio as MA
import mcam_ff as FF
import mcam_take as MT
from mcam_take import FrameStore, OutFrame, Take, TakeError, Timeline, smoothstep

try:
    from numba import njit
    HAVE_NUMBA = True
except Exception:
    HAVE_NUMBA = False

TOOLS = Path(__file__).resolve().parent
FONTS = TOOLS / 'fonts'
SYSTEM_FONTS = Path(os.environ.get('WINDIR') or os.environ.get('SystemRoot') or os.sep) / 'Fonts'
FONT_FILES = {'oswald': FONTS / 'Oswald-Regular.ttf', 'bahnschrift': SYSTEM_FONTS / 'bahnschrift.ttf',
              'mono': SYSTEM_FONTS / 'consola.ttf'}
MOVING_MODES = {'follow', 'keys', 'rail', 'orbit', 'gamelike', 'frame', 'director', 'gamerel'}
LOCK_NAME = 'mcam_encode.lock'
COMMIT_HEADROOM_GB = 2.0
OUT_MARK = '.mcam_out'
OUT_NAMES = ('angle_*_h265*', 'layout_*_h265*', 'cut_*', 'timeline.json', 'timeline.png', 'grade.json', 'grade_*.cube',
             'edit_autocut.json', 'director_*.json', 'compose_log.json', 'qa.json', 'qa.txt', 'sheet_*', 'paths.png', 'diverge.json',
             'review', 'stills', 'audiosync.json', 'audiosync.txt', 'replaydiff.json', 'replaydiff.txt', 'replaydiff.png',
             'fast_review.json')
LOG_NAME = 'compose_log.json'
OLD_OUT_ROOT = MT.OUT_ROOT
LOG_KEEP = 50
RESUME_VERSION = 1
AUDIO_CHUNK = 1 << 20
EXIT_OK, EXIT_BAD, EXIT_INPUT, EXIT_REFUSED = FF.EXIT_OK, FF.EXIT_BAD, FF.EXIT_INPUT, FF.EXIT_REFUSED
LIN8 = ((np.arange(256, dtype=np.float64) / 255.0) ** 2.2).astype(np.float32)
WRITTEN: list[str] = []
NOTES: list[str] = []
STOP = threading.Event()


class Refused(SystemExit):
    """The tool will not do this as asked (exit code 3). Other SystemExit texts are input or usage errors (exit code 2)."""


def wrote(path) -> None:
    WRITTEN.append(str(path))


def note(text: str) -> None:
    NOTES.append(text)
    print(f'warning: {text}', file=sys.stderr, flush=True)

DEFAULT_COMPOSE = {
    'blur': {'space': 'linear', 'shutter': 180},
    'grade': {'default': {'lift': 0.5, 'k': 4, 'floor': 'auto', 'ev': 0, 'saturation': 0}},
    'labels': {'show': True, 'font': 'Oswald', 'size': 0.016, 'color': [216, 210, 192], 'alpha': 0.7, 'names': {}},
    'angles': {'cams': 'all', 'speed': 'meta', 'labels': False},
    'encode': {'codec': 'nvenc', 'cq': 18, 'nvpreset': 'p7', 'crf': 18, 'preset': 'slow', 'bits': 10,
               'draft': {'codec': 'nvenc', 'cq': 23, 'nvpreset': 'p5', 'scale': 0.5}},
}


if HAVE_NUMBA:
    @njit(nogil=True, cache=True)
    def _avg_lin(stack, weights, lin, lut, out):
        m = stack.shape[0]
        n = stack.shape[1]
        for i in range(n):
            s = 0.0
            for k in range(m):
                s += weights[k] * lin[stack[k, i]]
            v = math.sqrt(s) * 65535.0 + 0.5
            if v > 65535.0:
                v = 65535.0
            out[i] = lut[int(v)]

    @njit(nogil=True, cache=True)
    def _avg_gam(stack, weights, lut, out):
        m = stack.shape[0]
        n = stack.shape[1]
        for i in range(n):
            s = 0.0
            for k in range(m):
                s += weights[k] * stack[k, i]
            v = s * (65535.0 / 255.0) + 0.5
            if v > 65535.0:
                v = 65535.0
            out[i] = lut[int(v)]

    @njit(nogil=True, cache=True)
    def _saturate(img, s, out):
        n = img.shape[0] // 3
        f = 1.0 + s
        for p in range(n):
            r = float(img[3 * p])
            g = float(img[3 * p + 1])
            b = float(img[3 * p + 2])
            y = 0.22 * r + 0.707 * g + 0.071 * b
            for c in range(3):
                v = y + (float(img[3 * p + c]) - y) * f + 0.5
                if v < 0.0:
                    v = 0.0
                elif v > 65535.0:
                    v = 65535.0
                out[3 * p + c] = int(v)
else:
    def _avg_lin(stack, weights, lin, lut, out):
        acc = np.zeros(stack.shape[1], np.float32)
        for k in range(stack.shape[0]):
            acc += weights[k] * lin[stack[k]]
        out[:] = lut[np.clip(np.sqrt(acc) * 65535.0 + 0.5, 0, 65535).astype(np.int64)]

    def _avg_gam(stack, weights, lut, out):
        acc = np.zeros(stack.shape[1], np.float32)
        for k in range(stack.shape[0]):
            acc += weights[k] * stack[k].astype(np.float32)
        out[:] = lut[np.clip(acc * (65535.0 / 255.0) + 0.5, 0, 65535).astype(np.int64)]

    def _saturate(img, s, out):
        x = img.reshape(-1, 3).astype(np.float32)
        y = x @ MT.LUMA
        out.reshape(-1, 3)[:] = np.clip(y[:, None] + (x - y[:, None]) * (1.0 + s) + 0.5, 0, 65535).astype(np.uint16)


# ---------------------------------------------------------------------------------------------------------------- grade

@dataclass
class GradeSpec:
    lift: float = 0.5
    k: float = 4.0
    floor: object = 'auto'
    ev: float = 0.0
    saturation: float = 0.0
    shoulder: float = 0.3
    auto: bool = False
    target: object = 30.0
    maxEv: float = 0.8
    smooth: float = 1.5
    slew: float = 0.5
    band: float = 0.3
    off: bool = False
    ranges: list = field(default_factory=list)


def grade_curve(x, floor: float, lift: float, k: float, ev: float = 0.0, shoulder: float = 0.3) -> np.ndarray:
    x = np.clip(np.asarray(x, np.float64), 0.0, 1.0)
    f = float(np.clip(floor, 0.0, 0.9))
    y = x.copy()
    if abs(ev) > 1e-6:
        lf = f ** 2.2
        lin = x ** 2.2
        lin2 = np.where(lin > lf, lf + 2.0 ** ev * (lin - lf), lin)
        y = np.clip(lin2, 0.0, None) ** (1.0 / 2.2)
        if ev > 0 and shoulder > 0:
            kk = 0.2 * shoulder
            hi = (lf + 2.0 ** ev * (1.0 - lf)) ** (1.0 / 2.2)

            def sh(v):
                return v + 0.5 * ((1.0 - v) - np.sqrt((1.0 - v) ** 2 + kk))

            y = np.where(y > f, f + (sh(y) - sh(f)) / (sh(hi) - sh(f)) * (1.0 - f), y)
        y = np.clip(y, 0.0, 1.0)
    if lift > 0:
        u = (y - f) / (1.0 - f)
        g = u + lift * u * np.clip(1.0 - u, 0.0, 1.0) ** k
        y = np.where(u > 0, f + (1.0 - f) * np.clip(g, 0.0, 1.0), y)
    return np.clip(y, 0.0, 1.0)


def range_weight(t: float, t0: float, t1: float, ramp: float) -> float:
    if t0 <= t <= t1:
        return 1.0
    if ramp <= 0:
        return 0.0
    if t0 - ramp < t < t0:
        return smoothstep((t - (t0 - ramp)) / ramp)
    if t1 < t < t1 + ramp:
        return smoothstep((t1 + ramp - t) / ramp)
    return 0.0


def resolve_ranges(take: Take, ranges, where: str) -> list[dict]:
    out = []
    for i, r in enumerate(ranges or []):
        if not isinstance(r, dict):
            raise SystemExit(f'{where}.ranges[{i}]: object expected')
        try:
            t0 = take.resolve_time(r.get('from', 0), 0.0)
            t1 = take.resolve_time(r.get('to'), math.inf)
        except TakeError as e:
            raise SystemExit(f'{where}.ranges[{i}]: {e}')
        if t1 < t0:
            raise SystemExit(f'{where}.ranges[{i}]: to {t1} is before from {t0}')
        rr = {'from': float(t0), 'to': float(t1), 'ramp': max(0.0, float(r.get('ramp', 0.5))), 'ev': float(r.get('ev', 0.0))}
        for k in ('lift', 'k', 'saturation'):
            if k in r:
                rr[k] = float(r[k])
        if 'lift' in rr:
            rr['lift'] = float(np.clip(rr['lift'], 0.0, 2.0))
        if 'k' in rr:
            rr['k'] = max(1.0, rr['k'])
        out.append(rr)
    return out


class CamGrade:
    def __init__(self, cam: str, spec: GradeSpec, floor: float, ev_steps: dict | None = None, ranges: list | None = None):
        self.cam = cam
        self.spec = spec
        self.floor = floor
        self.ev_steps = ev_steps
        self.ranges = ranges or []
        self.luts: dict = {}
        self.lock = threading.Lock()

    def params(self, t: float) -> tuple[float, float, float, float]:
        ev, lift, k, sat = 0.0, self.spec.lift, self.spec.k, self.spec.saturation
        for r in self.ranges:
            w = range_weight(t, r['from'], r['to'], r['ramp'])
            if w <= 0:
                continue
            ev += w * r['ev']
            if 'lift' in r:
                lift += w * (r['lift'] - lift)
            if 'k' in r:
                k += w * (r['k'] - k)
            if 'saturation' in r:
                sat += w * (r['saturation'] - sat)
        return ev, round(lift, 2), round(k, 1), sat

    def ev_for(self, steps) -> float:
        base = self.spec.ev
        if self.ev_steps:
            vals = [self.ev_steps.get(int(s), 0.0) for s in steps]
            base += float(np.mean(vals)) if vals else 0.0
        return round(base, 2)

    def lut(self, domain: str, ev: float, lift: float | None = None, k: float | None = None) -> np.ndarray:
        lift = self.spec.lift if lift is None else lift
        k = self.spec.k if k is None else k
        key = (domain, ev, lift, k)
        lut = self.luts.get(key)
        if lut is not None:
            return lut
        n = 256 if domain == 'g8' else 65536
        x = np.arange(n, dtype=np.float64) / (n - 1)
        if domain == 's16':
            x = x ** (2.0 / 2.2)
        if self.spec.off:
            y = x
        else:
            y = grade_curve(x, self.floor, lift, k, ev, self.spec.shoulder)
        lut = np.ascontiguousarray(np.clip(y * 65535.0 + 0.5, 0, 65535).astype(np.uint16))
        with self.lock:
            self.luts[key] = lut
        return lut

    def describe(self) -> dict:
        evs = list(self.ev_steps.values()) if self.ev_steps else [0.0]
        return {'floor': round(self.floor, 5), 'floor_8bit': round(self.floor * 255, 2), 'lift': self.spec.lift, 'k': self.spec.k,
                'ev': self.spec.ev, 'saturation': self.spec.saturation, 'auto': self.spec.auto,
                'target': round(self.spec.target, 2) if isinstance(self.spec.target, float) else self.spec.target,
                'auto_ev': [round(float(np.min(evs)), 3), round(float(np.median(evs)), 3), round(float(np.max(evs)), 3)],
                'off': self.spec.off, 'ranges': [{k: (round(v, 4) if isinstance(v, float) and math.isfinite(v) else str(v))
                                                 for k, v in r.items()} for r in self.ranges]}


def grade_spec_for(cfg: dict, cam: str, mode: str) -> GradeSpec:
    g = cfg.get('grade') or {}
    spec = GradeSpec()
    for src in (g.get('default') or {}, (g.get('cams') or {}).get(cam) or {}):
        for k, v in src.items():
            if hasattr(spec, k):
                setattr(spec, k, v)
    auto = g.get('auto')
    if isinstance(auto, dict):
        cams = auto.get('cams')
        on = (cam in cams) if isinstance(cams, list) else (mode in MOVING_MODES)
        if on:
            spec.auto = True
            for k in ('maxEv', 'smooth', 'slew', 'band'):
                if k in auto:
                    setattr(spec, k, float(auto[k]))
            if 'target' in auto:
                spec.target = auto['target'] if isinstance(auto['target'], str) else float(auto['target'])
    spec.lift = float(np.clip(float(spec.lift), 0.0, 2.0))
    spec.k = float(max(1.0, float(spec.k)))
    return spec


def auto_ev_series(take: Take, stats: dict, spec: GradeSpec, fps: float) -> dict:
    idx = {int(s): i for i, s in enumerate(stats['steps'])}
    lm = []
    frames = take.base.frames
    for f in frames:
        vals = [stats['logmean'][idx[s]] for s in f.steps if s in idx and np.isfinite(stats['logmean'][idx[s]])]
        lm.append(float(np.exp(np.mean(np.log(vals)))) if vals else math.nan)
    lm = np.array(lm)
    if not len(lm) or not np.isfinite(lm).any():
        return {}
    good = np.isfinite(lm)
    lm[~good] = np.interp(np.flatnonzero(~good), np.flatnonzero(good), lm[good])
    target = (spec.target / 255.0) ** 2.2
    ev = np.clip(np.log2(target / np.maximum(lm, 1e-7)), 0.0, spec.maxEv)
    sigma = max(spec.smooth * fps, 0.5)
    r = int(math.ceil(3 * sigma))
    ker = np.exp(-0.5 * (np.arange(-r, r + 1) / sigma) ** 2)
    ker /= ker.sum()
    ev = np.convolve(np.pad(ev, r, mode='reflect' if len(ev) > r else 'edge'), ker, mode='valid')
    lim = spec.slew / fps
    for rng in (range(1, len(ev)), range(len(ev) - 2, -1, -1)):
        for i in rng:
            j = i - 1 if rng.step == 1 else i + 1
            ev[i] = min(max(ev[i], ev[j] - lim), ev[j] + lim)
    med = float(np.median(ev))
    ev = np.clip(ev, med - spec.band, med + spec.band)
    out = {}
    for f, e in zip(frames, ev):
        for s in f.steps:
            out[int(s)] = float(e)
    return out


# ------------------------------------------------------------------------------------------------------------- context

@dataclass
class Ctx:
    take: Take
    store: FrameStore
    cfg: dict
    args: argparse.Namespace
    out: Path
    grades: dict = field(default_factory=dict)
    blur_space: str = 'linear'
    labels: 'Labels | None' = None
    src_size: tuple = (0, 0)
    sizes: dict = field(default_factory=dict)
    audio: 'AudioTrack | None' = None


def deep_merge(a: dict, b: dict) -> dict:
    out = dict(a)
    for k, v in (b or {}).items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = deep_merge(out[k], v)
        else:
            out[k] = v
    return out


def load_json_arg(value: str | None, base: dict | None, key: str) -> dict | None:
    if not value:
        return None
    p = Path(value)
    if p.suffix.lower() == '.json' and p.is_file():
        return json.loads(p.read_text(encoding='utf-8'))
    if base is not None:
        items = base.get(key)
        if isinstance(items, list):
            for it in items:
                if isinstance(it, dict) and it.get('name') == value:
                    return it
        elif isinstance(items, dict) and items.get('name') == value:
            return items
    raise SystemExit(f'{key}: "{value}" is neither a JSON file nor a name in the compose section')


def compose_config(take: Take, args) -> dict:
    cfg = deep_merge(DEFAULT_COMPOSE, take.compose())
    if getattr(args, 'compose', None):
        cfg = deep_merge(cfg, json.loads(Path(args.compose).read_text(encoding='utf-8')))
    if getattr(args, 'grade', None):
        g = json.loads(Path(args.grade).read_text(encoding='utf-8'))
        cfg['grade'] = deep_merge(cfg.get('grade') or {}, g.get('grade', g))
    if getattr(args, 'blur', None):
        cfg['blur'] = dict(cfg.get('blur') or {}, space=args.blur)
    if getattr(args, 'auto', None):
        auto = dict((cfg.get('grade') or {}).get('auto') or {})
        auto['cams'] = [c for c in args.auto.split(',') if c]
        cfg.setdefault('grade', {})['auto'] = auto
    if getattr(args, 'labels', None) is not None:
        cfg['labels'] = dict(cfg.get('labels') or {}, show=bool(args.labels))
    return cfg


def use_gpu(take: Take, args) -> bool:
    if getattr(args, 'cpu', False):
        return False
    mkvs = [c.video for c in take.cams.values() if c.ext == 'mkv' and c.video is not None]
    if not mkvs:
        return False
    ok = MT.cuda_ok(mkvs[0])
    if not ok:
        print(f'decode: CUDA decode test failed on {mkvs[0].name}; CPU decode (as --cpu)', flush=True)
    return ok


def make_ctx(take: Take, args) -> Ctx:
    cfg = compose_config(take, args)
    out = resolve_out(take, args)
    store = FrameStore(take, cache_mb=int(getattr(args, 'cache_mb', 1536)), gpu=use_gpu(take, args))
    ctx = Ctx(take, store, cfg, args, out)
    ctx.blur_space = str((cfg.get('blur') or {}).get('space', 'linear'))
    ctx.labels = Labels(cfg.get('labels') or {})
    ctx.src_size = take.screen()
    for c in take.cams:
        try:
            ctx.sizes[c] = take.frame_size(c)
        except Exception:
            ctx.sizes[c] = ctx.src_size
    return ctx


def out_root(take_root: Path, spec: dict | None) -> tuple[Path | None, str]:
    env = os.environ.get('MCAM_OUT')
    if env:
        return Path(env), 'MCAM_OUT'
    spec = spec if isinstance(spec, dict) else {}
    for where, sec in (('compose.output.root', spec.get('compose')), ('output.root', spec)):
        o = sec.get('output') if isinstance(sec, dict) else None
        if isinstance(o, dict) and isinstance(o.get('root'), str) and o['root']:
            p = Path(o['root'])
            return (p if p.is_absolute() else Path(take_root) / p), f'spec {where}'
    return None, ''


def default_out(take_root: Path, name: str, spec: dict | None = None) -> tuple[Path, str]:
    """Where the outputs of a take go without --out, and why (see the module text)."""
    root, why = out_root(take_root, spec)
    if root is not None:
        return (root / safe_name(name)).resolve(), why
    old = OLD_OUT_ROOT / safe_name(name)
    try:
        if old.is_dir() and any(old.iterdir()):
            return old.resolve(), 'the folder an earlier version used for this take'
    except OSError:
        pass
    return (Path(take_root) / 'out').resolve(), 'in the take folder'


def out_is_ours(out: Path) -> bool:
    if any((out / name).is_file() for name in (OUT_MARK, LOG_NAME, 'qa.json', 'fast_review.json', 'audiosync.json', 'replaydiff.json')):
        return True
    try:
        root = OLD_OUT_ROOT.resolve()
    except OSError:
        return False
    return root in out.parents


def set_stats_root(root) -> None:
    """The cache of per-camera luma statistics (mcam_take.camera_stats: <root>/_stats/<take>) follows the output place
    instead of the folder the tools are installed in. Also the initializer of the statistics worker processes."""
    MT.OUT_ROOT = Path(root)


def claim_out(out: Path, force: bool = False) -> Path:
    """Makes the output folder. An existing folder that this tool has not written to before is used as it is, unless it
    already holds files under this tool's output names: those would be overwritten, so that needs --force."""
    try:
        if out.is_dir() and not force and not out_is_ours(out):
            foreign = sorted(p.name for p in out.iterdir() if any(fnmatch.fnmatchcase(p.name, pat) for pat in OUT_NAMES))
            if foreign:
                raise Refused(f'output folder {out} holds {len(foreign)} file(s) or folder(s) under this tool\'s output names that '
                              f'it did not write ({", ".join(foreign[:4])}{" ..." if len(foreign) > 4 else ""}); choose another '
                              f'--out or pass --force to overwrite them')
        out.mkdir(parents=True, exist_ok=True)
        mark = out / OUT_MARK
        if not mark.is_file():
            mark.write_text('outputs of the InsideDevTool mcam tools (mcam_compose.py, mcam_qa.py, fast_review.py)\n', encoding='utf-8')
    except OSError as e:
        raise Refused(f'cannot write into the output folder {out} ({e.strerror or e}); pass --out DIR or set MCAM_OUT') from None
    return out


def resolve_out(take: Take, args) -> Path:
    given = getattr(args, 'out', None)
    if given and not str(given).lower().endswith('.json'):
        out = stats = Path(given).resolve()
    else:
        out, why = default_out(take.root, take.name, take.spec)
        stats = out if out.parent == Path(take.root).resolve() else out.parent
        print(f'output folder: {out} ({why})', file=sys.stderr, flush=True)
    claim_out(out, bool(getattr(args, 'force', False)))
    set_stats_root(stats)
    return out


def safe_name(s: str) -> str:
    return ''.join(ch if ch.isalnum() or ch in '-_' else '_' for ch in s) or 'take'


def read_log(path: Path, set_aside: bool = False) -> list:
    """compose_log.json as a list. A damaged log is not fatal: it reads as empty with a warning (and is renamed to
    compose_log.bad.json when the caller is about to write a new one)."""
    if not path.is_file():
        return []
    try:
        doc = json.loads(path.read_text(encoding='utf-8'))
        if isinstance(doc, list):
            return [e for e in doc if isinstance(e, dict)]
        why = 'not a list'
    except (OSError, ValueError) as e:
        why = str(e)
    bad = path.with_name(path.stem + '.bad' + path.suffix)
    if set_aside:
        try:
            os.replace(path, bad)
            why += f'; kept as {bad.name}'
        except OSError:
            pass
    note(f'{path}: unreadable ({why}); read as empty')
    return []


def result_items(res) -> list[dict]:
    """The video entries of a command result (a dict, a list, or {'angles', 'layouts', 'cut'})."""
    if isinstance(res, dict) and ('angles' in res or 'layouts' in res or 'cut' in res):
        items = list(res.get('angles') or []) + list(res.get('layouts') or []) + ([res['cut']] if isinstance(res.get('cut'), dict) else [])
    else:
        items = res if isinstance(res, list) else [res] if isinstance(res, dict) else []
    return [it for it in items if isinstance(it, dict) and it.get('file')]


def setup_grades(ctx: Ctx, cams: list[str]) -> None:
    for cam in cams:
        if cam in ctx.grades:
            continue
        mode = ctx.take.cams[cam].mode
        spec = grade_spec_for(ctx.cfg, cam, mode)
        if getattr(ctx.args, 'no_grade', False):
            spec = replace(spec, off=True, auto=False)
        floor = 0.0
        ev_steps = None
        need_stats = not spec.off and (spec.floor == 'auto' or spec.auto)
        if need_stats:
            steps = sorted({s for f in ctx.take.base.frames for s in f.steps})
            st = MT.camera_stats(ctx.take, ctx.store, cam, steps, workers=threads_of(ctx.args))
            if spec.floor == 'auto':
                floor = MT.pooled_percentile(st['hist'], 0.005) / 255.0
            if spec.auto:
                if isinstance(spec.target, str):
                    if spec.target not in ctx.take.cams:
                        raise SystemExit(f'grade auto target "{spec.target}" is not a camera of the take')
                    ref = MT.camera_stats(ctx.take, ctx.store, spec.target, steps, workers=threads_of(ctx.args))
                    lm = ref['logmean'][np.isfinite(ref['logmean'])]
                    spec = replace(spec, target=float(np.median(lm) ** (1 / 2.2) * 255.0) if len(lm) else 30.0)
                ev_steps = auto_ev_series(ctx.take, st, spec, ctx.take.fps)
        if spec.floor != 'auto':
            fv = float(spec.floor)
            floor = fv / 255.0 if fv > 1.0 else fv
        ctx.grades[cam] = CamGrade(cam, spec, floor, ev_steps, resolve_ranges(ctx.take, spec.ranges, f'compose.grade ({cam})'))


def cell_grade(ctx: Ctx, cam: str, over: dict | None, where: str) -> 'CamGrade | None':
    if not over:
        return None
    if not isinstance(over, dict):
        raise SystemExit(f'{where}: grade object expected')
    base = ctx.grades[cam]
    spec = replace(base.spec)
    for k, v in over.items():
        if k != 'ranges' and hasattr(spec, k) and not k.startswith('_'):
            setattr(spec, k, v)
    spec.lift = float(np.clip(float(spec.lift), 0.0, 2.0))
    spec.k = float(max(1.0, float(spec.k)))
    ranges = resolve_ranges(ctx.take, over['ranges'], where) if 'ranges' in over else base.ranges
    floor = base.floor
    if 'floor' in over and over['floor'] != 'auto':
        fv = float(over['floor'])
        floor = fv / 255.0 if fv > 1.0 else fv
    return CamGrade(cam, spec, floor, base.ev_steps, ranges)


def threads_of(args) -> int:
    return max(1, int(getattr(args, 'threads', 0) or min(8, os.cpu_count() or 4)))


# ------------------------------------------------------------------------------------------------------------ rendering

def render_cam(ctx: Ctx, cam: str, fr: OutFrame, reduce: int = 1, rect: tuple | None = None, grade: 'CamGrade | None' = None) -> np.ndarray:
    imgs = []
    for s in fr.steps:
        im = ctx.store.get(cam, s, reduce)
        if rect is not None:
            x0, y0, x1, y1 = rect
            im = im[y0:y1, x0:x1]
        imgs.append(im)
    grade = grade or ctx.grades[cam]
    dev, lift, kk, sat = grade.params(fr.t)
    ev = round(grade.ev_for(fr.steps) + dev, 2)
    if len(imgs) == 1:
        out = cv2.LUT(np.ascontiguousarray(imgs[0]), grade.lut('g8', ev, lift, kk).reshape(256, 1))
    else:
        h, w = imgs[0].shape[:2]
        stack = np.stack(imgs).reshape(len(imgs), -1)
        wts = np.array(fr.weights, np.float32)
        wts /= wts.sum()
        flat = np.empty(stack.shape[1], np.uint16)
        if ctx.blur_space == 'gamma':
            _avg_gam(stack, wts, grade.lut('g16', ev, lift, kk), flat)
        else:
            _avg_lin(stack, wts, LIN8, grade.lut('s16', ev, lift, kk), flat)
        out = flat.reshape(h, w, 3)
    if abs(sat) > 1e-6 and not grade.spec.off:
        flat = np.ascontiguousarray(out).reshape(-1)
        res = np.empty_like(flat)
        _saturate(flat, float(sat), res)
        out = res.reshape(out.shape)
    return out


def pan_at(pan: list, t: float) -> tuple[float, float]:
    keys = sorted([(float(k[0]), float(k[1]), float(k[2])) for k in pan], key=lambda k: k[0])
    if t <= keys[0][0]:
        return keys[0][1], keys[0][2]
    for a, b in zip(keys, keys[1:]):
        if t <= b[0]:
            w = smoothstep((t - a[0]) / max(b[0] - a[0], 1e-9))
            return a[1] + (b[1] - a[1]) * w, a[2] + (b[2] - a[2]) * w
    return keys[-1][1], keys[-1][2]


def view_rect(src_w: int, src_h: int, w: int, h: int, crop=None, anchor=None, pan=None, t: float = 0.0) -> tuple:
    rx, ry, rw, rh = (float(v) for v in (crop or (0, 0, src_w, src_h)))
    if pan:
        rx, ry = pan_at(pan, t)
    rw, rh = min(rw, src_w), min(rh, src_h)
    ax, ay = (anchor or (0.5, 0.5))
    aspect = w / h
    if rw / rh > aspect:
        sw, sh = rh * aspect, rh
        sx, sy = rx + (rw - sw) * ax, ry
    else:
        sw, sh = rw, rw / aspect
        sx, sy = rx, ry + (rh - sh) * ay
    sx = min(max(sx, 0.0), src_w - sw)
    sy = min(max(sy, 0.0), src_h - sh)
    return sx, sy, sw, sh


def pick_reduce(sw: float, sh: float, w: int, h: int) -> int:
    for r in (8, 4, 2):
        if sw / r >= w * 0.999 and sh / r >= h * 0.999:
            return r
    return 1


def render_view(ctx: Ctx, cam: str, fr: OutFrame, w: int, h: int, crop=None, anchor=None, pan=None, grade: 'CamGrade | None' = None) -> np.ndarray:
    src_w, src_h = ctx.sizes.get(cam, ctx.src_size)
    sx, sy, sw, sh = view_rect(src_w, src_h, w, h, crop, anchor, pan, fr.t)
    r = pick_reduce(sw, sh, w, h)
    if pan:
        rr_w, rr_h = src_w // r, src_h // r
        x0 = max(0, int(math.floor(sx / r)) - 1)
        y0 = max(0, int(math.floor(sy / r)) - 1)
        x1 = min(rr_w, int(math.ceil((sx + sw) / r)) + 2)
        y1 = min(rr_h, int(math.ceil((sy + sh) / r)) + 2)
        img = render_cam(ctx, cam, fr, r, (x0, y0, x1, y1), grade)
        scale = w / (sw / r)
        m = np.array([[scale, 0, -(sx / r - x0) * scale], [0, scale, -(sy / r - y0) * scale]], np.float64)
        return cv2.warpAffine(img, m, (w, h), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
    x0, y0 = int(round(sx / r)), int(round(sy / r))
    x1, y1 = int(round((sx + sw) / r)), int(round((sy + sh) / r))
    img = render_cam(ctx, cam, fr, r, (x0, y0, x1, y1), grade)
    if img.shape[1] != w or img.shape[0] != h:
        interp = cv2.INTER_AREA if img.shape[1] > w else cv2.INTER_CUBIC
        img = cv2.resize(img, (w, h), interpolation=interp)
    return img


def mix_layers(layers: list[tuple[np.ndarray, float]]) -> np.ndarray:
    if len(layers) == 1:
        img, w = layers[0]
        if w >= 0.9999:
            return img
        return cv2.addWeighted(img, float(w), img, 0.0, 0.0)
    out = cv2.addWeighted(layers[0][0], float(layers[0][1]), layers[1][0], float(layers[1][1]), 0.0)
    for img, w in layers[2:]:
        out = cv2.addWeighted(out, 1.0, img, float(w), 0.0)
    return out


def fade(img: np.ndarray, f: float) -> np.ndarray:
    if f >= 0.9999:
        return img
    if f <= 1e-4:
        return np.zeros_like(img)
    return cv2.addWeighted(img, float(f), img, 0.0, 0.0)


# --------------------------------------------------------------------------------------------------------------- labels

class Labels:
    def __init__(self, cfg: dict):
        self.cfg = cfg
        self.show = bool(cfg.get('show', True))
        self.size = float(cfg.get('size', 0.016))
        col = cfg.get('color', [216, 210, 192])
        self.color = np.array(col[:3], np.float32) * 257.0
        self.alpha = float(cfg.get('alpha', 0.7))
        self.names = dict(cfg.get('names') or {})
        self.font_path = self._font(str(cfg.get('font', 'Oswald')))
        if self.show and self.font_path is None:
            print(f'labels: no font file found ({cfg.get("font", "Oswald")}, {FONT_FILES["oswald"]}, {FONT_FILES["bahnschrift"]}); '
                  f'the small built-in font is used (compose.labels.font takes a .ttf path)', file=sys.stderr, flush=True)
        self.cache: dict = {}
        self.lock = threading.Lock()

    @staticmethod
    def _font(name: str) -> Path | None:
        p = Path(name)
        if p.suffix.lower() in ('.ttf', '.otf') and p.is_file():
            return p
        for key in (name.lower(), 'oswald', 'bahnschrift'):
            f = FONT_FILES.get(key)
            if f and f.is_file():
                return f
        return None

    def text_for(self, take: Take, cam: str, explicit: str | None = None) -> str:
        if explicit:
            return explicit
        if cam in self.names:
            return str(self.names[cam])
        c = take.cams.get(cam)
        if c is not None and c.label:
            return c.label
        return MT.pretty_name(cam)

    def patch(self, text: str, px: int) -> tuple[np.ndarray, np.ndarray]:
        key = (text, px)
        hit = self.cache.get(key)
        if hit is not None:
            return hit
        font = ImageFont.truetype(str(self.font_path), px) if self.font_path else ImageFont.load_default()
        x0, y0, x1, y1 = font.getbbox(text)
        pad = max(2, px // 6)
        w, h = x1 - x0 + 2 * pad, y1 - y0 + 2 * pad
        im = Image.new('L', (w, h), 0)
        ImageDraw.Draw(im).text((pad - x0, pad - y0), text, fill=255, font=font)
        shadow = im.filter(ImageFilter.GaussianBlur(max(1.0, px / 14)))
        res = (np.asarray(im, np.float32) / 255.0, np.asarray(shadow, np.float32) / 255.0)
        with self.lock:
            self.cache[key] = res
        return res

    def draw(self, img: np.ndarray, text: str, x: int, y: int, px: int, opacity: float = 1.0) -> None:
        if not text or opacity <= 0.003:
            return
        a, sh = self.patch(text, px)
        h, w = a.shape
        y0 = y - h
        x0, y0 = max(0, x), max(0, y0)
        x1, y1 = min(img.shape[1], x0 + w), min(img.shape[0], y0 + h)
        if x1 <= x0 or y1 <= y0:
            return
        a = a[:y1 - y0, :x1 - x0, None] * (self.alpha * opacity)
        s = sh[:y1 - y0, :x1 - x0, None] * (0.45 * opacity)
        reg = img[y0:y1, x0:x1].astype(np.float32)
        reg = reg * (1.0 - s)
        reg = reg * (1.0 - a) + self.color * a
        img[y0:y1, x0:x1] = np.clip(reg + 0.5, 0, 65535).astype(np.uint16)


def label_px(ctx: Ctx, width: int, scale: float = 1.0) -> int:
    return max(12, int(round(ctx.labels.size * width * scale)))


# ------------------------------------------------------------------------------------------------------------- geometry

@dataclass
class Cell:
    cam: str
    x: int
    y: int
    w: int
    h: int
    crop: list | None = None
    anchor: tuple | None = None
    pan: list | None = None
    label: str | None = None
    band: bool = False
    grade: dict | None = None
    cg: object = None


PRESETS = {'16x9_3': (956, 3, 480), '16x9_2': (716, 2, 720)}
PRESET_WIDTH = 2560


def even(v: float) -> int:
    return int(2 * round(v / 2.0))


def default_layout(take: Take) -> dict:
    cams = [c for c in take.camera_names() if take.cams[c].mode != 'director']
    if not cams:
        raise SystemExit('default layout: the take has only director cameras; name a layout (compose.layouts or --layout)')
    band = 'game' if 'game' in cams else cams[0]
    rest = [c for c in cams if c != band][:3]
    w, h = take.screen()
    ch = min(even(h), even(w * 27 / 64))
    rows = [[{'cam': band, 'crop': [0, even((h - ch) / 2), w, ch]}]]
    if rest:
        rows.append([{'cam': c} for c in rest])
    return {'name': 'main', 'width': even(w), 'gap': 4, 'rows': rows}


def layout_width(lay: dict, rows: list, sizes: dict, default_size: tuple) -> int:
    """Canvas width: the layout's own `width`, else the widest source among its cells (cells never have to grow)."""
    if lay.get('width'):
        return even(int(lay['width']))
    widths = [sizes.get(c.get('cam'), default_size)[0] for row in rows for c in ([row] if isinstance(row, dict) else row)
              if isinstance(c, dict) and c.get('cam') != 'cut']
    return even(max(widths or [default_size[0]]))


def layout_geometry(lay: dict, sizes: dict, default_size: tuple) -> tuple[int, int, list[Cell]]:
    g = int(lay.get('gap', 4))
    preset = lay.get('preset')
    rows = lay.get('rows')
    if not rows and preset and lay.get('cams'):
        cams = list(lay['cams'])
        rows = [[{'cam': cams[0]}], [{'cam': c} for c in cams[1:]]]
    if not rows:
        raise SystemExit(f'layout {lay.get("name")}: no rows')
    if preset and preset not in PRESETS:
        raise SystemExit(f'layout {lay.get("name")}: unknown preset {preset} (use {", ".join(PRESETS)})')
    W = layout_width(lay, rows, sizes, default_size)
    if W <= 0:
        raise SystemExit(f'layout {lay.get("name")}: no width (the frame size of its cameras is unknown; give "width")')
    ps = W / PRESET_WIDTH
    cells: list[Cell] = []
    y = 0
    for ri, row in enumerate(rows):
        row = [row] if isinstance(row, dict) else list(row)
        n = len(row)
        first = row[0]
        is_band = n == 1 and (first.get('crop') is not None or (preset and ri == 0))
        if is_band:
            sw, sh = sizes.get(first['cam'], default_size)
            crop = list(first.get('crop') or [0, 0, sw, sh])
            if preset and ri == 0:
                band_h = even(PRESETS[preset][0] * ps)
                want_h = crop[2] * band_h / W
                cy = crop[1] + crop[3] / 2.0
                crop = [crop[0], max(0.0, min(sh - want_h, cy - want_h / 2.0)), crop[2], want_h]
                h = band_h
            else:
                h = even(crop[3] * W / crop[2])
            if first.get('height'):
                h = even(float(first['height']))
            cells.append(Cell(first['cam'], 0, y, W, h, crop, None, first.get('pan'), first.get('label'), True, first.get('grade')))
        else:
            inner = W - (n - 1) * g
            widths = [inner // n + (1 if i < inner % n else 0) for i in range(n)]
            if preset and ri == 1 and n == PRESETS[preset][1]:
                h = even(PRESETS[preset][2] * ps)
            else:
                given = next((c.get('height') for c in row if c.get('height')), None) or (lay.get('heights') or {}).get(str(ri))
                h = even(float(given)) if given else 2 * math.ceil(max(widths) * 9 / 32)
            x = 0
            for i, c in enumerate(row):
                anchor = tuple(c['anchor']) if c.get('anchor') else None
                cells.append(Cell(c['cam'], x, y, widths[i], h, c.get('crop'), anchor, c.get('pan'), c.get('label'), False, c.get('grade')))
                x += widths[i] + g
        y += h + (g if ri < len(rows) - 1 else 0)
    H = y if y % 2 == 0 else y + 1
    return W, H, cells


# ----------------------------------------------------------------------------------------------------------------- edit

@dataclass
class ShotPlan:
    cam: str
    src0: int
    src1: int
    out0: int
    label: str
    from_t: float
    tin: str = 'cut'
    tn: int = 0
    talign: str = 'center'
    tws: int = 0
    replay: bool = False

    @property
    def n(self) -> int:
        return self.src1 - self.src0


@dataclass
class EditPlan:
    name: str
    tl: Timeline
    shots: list[ShotPlan]
    total: int
    fade_in: int
    fade_out: int
    min_hold: float
    max_hold: float
    warnings: list[str]

    def shot_index(self, j: int) -> int:
        lo, hi = 0, len(self.shots) - 1
        while lo < hi:
            mid = (lo + hi + 1) // 2
            if self.shots[mid].out0 <= j:
                lo = mid
            else:
                hi = mid - 1
        return lo

    def src_of(self, si: int, j: int) -> int:
        s = self.shots[si]
        return min(max(s.src0 + (j - s.out0), 0), len(self.tl) - 1)

    def window(self, si: int) -> tuple[int, int] | None:
        if si <= 0 or si >= len(self.shots):
            return None
        s = self.shots[si]
        if s.tin == 'cut' or s.tn <= 0:
            return None
        return s.tws, s.tws + s.tn

    def at(self, j: int) -> dict:
        si = self.shot_index(j)
        layers = [(self.shots[si].cam, self.src_of(si, j), 1.0)]
        main = (self.shots[si].cam, self.src_of(si, j))
        in_trans = False
        for c in (si, si + 1):
            win = self.window(c)
            if win and win[0] <= j < win[1]:
                in_trans = True
                s = self.shots[c]
                w = (j - win[0] + 0.5) / s.tn
                a = (self.shots[c - 1].cam, self.src_of(c - 1, j))
                b = (s.cam, self.src_of(c, j))
                if s.tin == 'dip':
                    layers = [(a[0], a[1], 1.0 - smoothstep(2 * w))] if w < 0.5 else [(b[0], b[1], smoothstep(2 * w - 1))]
                else:
                    ww = smoothstep(w)
                    layers = [(a[0], a[1], 1.0 - ww), (b[0], b[1], ww)]
                main = b if w >= 0.5 else a
                break
        f = 1.0
        if self.fade_in > 0 and j < self.fade_in:
            f = min(f, smoothstep((j + 0.5) / self.fade_in))
        if self.fade_out > 0 and j >= self.total - self.fade_out:
            f = min(f, smoothstep((self.total - j - 0.5) / self.fade_out))
        label, op = None, 0.0
        s = self.shots[si]
        if s.label and not in_trans:
            win = self.window(si)
            start = max(s.out0, win[1] if win else s.out0)
            e = (j - start) / self.tl.fps
            if 0 <= e < 0.4:
                op = smoothstep(e / 0.4)
            elif 0.4 <= e < 2.4:
                op = 1.0
            elif 2.4 <= e < 3.0:
                op = 1.0 - smoothstep((e - 2.4) / 0.6)
            label = s.label
        return {'layers': layers, 'fade': f, 'label': label, 'label_op': op, 'shot': si, 'main': main}

    def to_json(self) -> dict:
        fps = self.tl.fps
        return {'name': self.name, 'fps': fps, 'frames': self.total, 'seconds': round(self.total / fps, 3), 'timeline': self.tl.mode,
                'fade_in': self.fade_in, 'fade_out': self.fade_out, 'min_hold': self.min_hold, 'max_hold': self.max_hold,
                'shots': [{'cam': s.cam, 'from': round(s.from_t, 4), 'src': [s.src0, s.src1], 'out0': s.out0,
                           'game': [round(self.tl[s.src0].t0, 4), round(self.tl[min(s.src1, len(self.tl)) - 1].t, 4)],
                           'out_seconds': round(s.n / fps, 3), 'in': {'type': s.tin, 'frames': s.tn, 'align': s.talign,
                                                                     'window': [s.tws, s.tws + s.tn]},
                           'label': s.label, 'replay': s.replay} for s in self.shots],
                'warnings': self.warnings}


def edit_timeline(ctx: Ctx, edit: dict) -> Timeline:
    if edit.get('speed'):
        keys = [[ctx.take.resolve_time(k[0]), float(k[1])] for k in edit['speed']]
        shutter = float(edit.get('shutter', (ctx.cfg.get('blur') or {}).get('shutter', 180)))
        if getattr(ctx.args, 'shutter', None):
            shutter = float(ctx.args.shutter)
        fps = float(getattr(ctx.args, 'fps', None) or ctx.cfg.get('fps') or ctx.take.fps)
        return MT.build_warp(ctx.take.base, keys, fps, shutter, str(edit.get('interp', 'nearest')),
                             step_times=MT.attach_step_times(ctx.take))
    return base_timeline(ctx)


def base_timeline(ctx: Ctx) -> Timeline:
    tl = ctx.take.base
    fps = getattr(ctx.args, 'fps', None)
    if fps:
        tl = Timeline(tl.frames, float(fps), tl.mode)
    return tl


def plan_edit(ctx: Ctx, edit: dict, tl: Timeline | None = None) -> EditPlan:
    take = ctx.take
    tl = tl or edit_timeline(ctx, edit)
    fps = tl.fps
    warnings: list[str] = []
    raw = [dict(s) for s in edit.get('shots') or []]
    if not raw:
        raise SystemExit(f'edit {edit.get("name")}: no shots')
    for s in raw:
        if s.get('cam') not in take.cams:
            raise SystemExit(f'edit {edit.get("name")}: camera "{s.get("cam")}" is not in the take ({", ".join(take.cams)})')
        s['_from'] = take.resolve_time(s.get('from', 0), 0.0)
    raw.sort(key=lambda s: s['_from'])
    shots: list[ShotPlan] = []
    for i, s in enumerate(raw):
        rp = s.get('replay')
        if rp:
            src0 = tl.frame_at_or_end(take.resolve_time(rp[0]))
            src1 = tl.frame_at_or_end(take.resolve_time(rp[1]))
        else:
            src0 = tl.frame_at_or_end(s['_from'])
            if s.get('to') is not None:
                src1 = tl.frame_at_or_end(take.resolve_time(s['to']))
            elif i + 1 < len(raw):
                src1 = tl.frame_at_or_end(raw[i + 1]['_from'])
            else:
                src1 = len(tl)
        src1 = min(src1, len(tl))
        if src1 <= src0:
            warnings.append(f'shot {i} ({s.get("cam")} from {s["_from"]:.3f}) has no frames, skipped')
            continue
        tin = s.get('in') or {'type': 'cut'}
        if isinstance(tin, str):
            tin = {'type': tin}
        label = ctx.labels.text_for(take, s['cam'], s.get('label')) if ctx.labels.show and s.get('label') is not False else ''
        shots.append(ShotPlan(s['cam'], src0, src1, 0, label, s['_from'], str(tin.get('type', 'cut')),
                              int(round(float(tin.get('dur', 1.0)) * fps)) if tin.get('type', 'cut') != 'cut' else 0,
                              str(tin.get('align', 'center')), 0, bool(rp)))
    if not shots:
        raise SystemExit(f'edit {edit.get("name")}: every shot is empty for this take')
    out0 = 0
    for s in shots:
        s.out0 = out0
        out0 += s.n
    total = out0
    for i, s in enumerate(shots):
        if s.tin not in ('cut', 'dissolve', 'dip'):
            warnings.append(f'shot {i}: unknown transition {s.tin}, using cut')
            s.tin = 'cut'
        if i == 0:
            if s.tin != 'cut':
                warnings.append('first shot: transition ignored (use fadeIn)')
            s.tin, s.tn = 'cut', 0
            continue
        if s.tin == 'cut':
            continue
        prev = shots[i - 1]
        limit = min(prev.n, s.n)
        if s.tn > limit:
            warnings.append(f'shot {i} ({s.cam}): transition {s.tn} frames clamped to {limit}')
            s.tn = limit
        a = {'center': s.tn // 2, 'start': 0, 'end': s.tn}.get(s.talign, s.tn // 2)
        s.tws = min(max(s.out0 - a, prev.out0), s.out0 + s.n - s.tn)
        if s.tn > 0.4 * min(prev.n, s.n):
            warnings.append(f'shot {i} ({s.cam}): transition {s.tn / fps:.2f} s is longer than 40 % of a neighbouring shot')
    min_hold = float(edit.get('minHold', 4.0))
    max_hold = float(edit.get('maxHold', 6.5))
    for i, s in enumerate(shots):
        d = s.n / fps
        if d < min_hold - 1e-6 and 0 < i < len(shots) - 1:
            warnings.append(f'shot {i} ({s.cam}) holds {d:.2f} s < minHold {min_hold}')
        if d > max_hold + 1e-6:
            warnings.append(f'shot {i} ({s.cam}) holds {d:.2f} s > maxHold {max_hold}')
    fi = int(round(float(edit.get('fadeIn', 0.5)) * fps))
    fo = int(round(float(edit.get('fadeOut', 1.0)) * fps))
    return EditPlan(str(edit.get('name', 'cut')), tl, shots, total, fi, fo, min_hold, max_hold, warnings)


def render_edit_view(ctx: Ctx, plan: EditPlan, j: int, w: int, h: int, crop=None, anchor=None, pan=None,
                     with_label: bool = True, label_scale: float = 1.0, apply_fade: bool = True) -> np.ndarray:
    st = plan.at(j)
    layers = []
    for cam, src, wt in st['layers']:
        if wt <= 1e-4:
            continue
        layers.append((render_view(ctx, cam, plan.tl[src], w, h, crop, anchor, pan), wt))
    img = mix_layers(layers) if layers else np.zeros((h, w, 3), np.uint16)
    f = st['fade'] if apply_fade else 1.0
    img = fade(img, f)
    if with_label and st['label'] and st['label_op'] > 0:
        img = np.array(img, copy=True) if not img.flags.writeable else img
        px = label_px(ctx, w, label_scale)
        ctx.labels.draw(img, st['label'], int(px * 0.9), h - int(px * 0.8), px, st['label_op'] * f)
    return img


# ---------------------------------------------------------------------------------------------------------------- audio

AUDIO_RATE = 48000


@dataclass
class AudioTrack:
    path: Path
    rate: int
    channels: int
    data: np.ndarray
    fps: float
    codec: str
    state: dict


def load_audio(ctx: Ctx) -> AudioTrack:
    take = ctx.take
    if take.replay_mode not in ('replay', 'take'):
        kind = f'run.json mode "{take.mode}"' if take.mode else 'no replay run.json'
        raise Refused(f'--audio record refused: {take.root} is not a replay take ({kind}); v2 takes carry no sound '
                      f'(REPLAY_DESIGN 6.5)')
    st = take.audio_state()
    if not st['ok']:
        raise Refused(f'--audio record refused: {st["verdict"]} [record {take.record_dir()}]')
    try:
        wav = MA.load_wav(Path(st['track']))
    except (MA.AudioError, OSError) as e:
        raise Refused(f'--audio record refused: {e}')
    if wav.problems:
        raise Refused(f'--audio record refused: {wav.path.name}: ' + '; '.join(wav.problems))
    fps = float(take.fps)
    want = (max((f.index for f in take.base.frames), default=-1) + 1) / fps
    if wav.seconds + 1.0 / fps < want:
        print(f'warning: {wav.path.name} holds {wav.seconds:.3f} s, the output timeline {want:.3f} s; frames past the track '
              f'end are refused when a video needs them', flush=True)
    codec = str(getattr(ctx.args, 'audio_codec', None) or 'aac')
    print(f'audio: {wav.path} ({wav.rate} Hz, {wav.channels} ch, {wav.seconds:.3f} s) -> {codec.upper()} '
          f'{"320 kb/s MP4" if codec == "aac" else "MKV"}', flush=True)
    return AudioTrack(wav.path, wav.rate, wav.channels, wav.data, fps, codec, st)


def require_meta_timeline(ctx: Ctx, tl: Timeline, what: str) -> None:
    if ctx.audio is None:
        return
    if tl.mode != 'meta' or any(f.index < 0 for f in tl.frames):
        raise Refused(f'--audio record refused for {what}: it uses a {tl.mode} timeline; audio_track.wav follows the meta '
                      f'timeline only (drop edit speed keys or --speed realtime)')
    if abs(tl.fps - ctx.audio.fps) > 1e-6:
        raise Refused(f'--audio record refused for {what}: {tl.fps:g} fps differs from the take timeline {ctx.audio.fps:g} fps')


def check_audio_plan(ctx: Ctx, command: str) -> None:
    if command in ('angles', 'all'):
        require_meta_timeline(ctx, angle_timeline(ctx), 'angles')
    uses_cut = any(any(c.get('cam') == 'cut' for row in lay.get('rows') or [] for c in row if isinstance(c, dict))
                   for lay in layouts_of(ctx) if isinstance(lay, dict))
    if command in ('cut', 'all') or (command == 'layout' and uses_cut):
        edit = current_edit(ctx, required=False)
        if edit and edit.get('speed'):
            raise Refused(f'--audio record refused: edit "{edit.get("name", "cut")}" has speed keys (warp timeline); '
                          f'audio_track.wav follows the meta timeline only')
        require_meta_timeline(ctx, base_timeline(ctx), 'the cut')


def mix_from_timeline(tl: Timeline, n: int) -> list[dict]:
    return [{tl[j].index - j: 1.0} for j in range(n)]


def mix_from_plan(plan: 'EditPlan', n: int) -> list[dict]:
    out = []
    for j in range(n):
        st = plan.at(j)
        acc: dict[int, float] = {}
        for _, src, w in st['layers']:
            d = plan.tl[src].index - j
            acc[d] = acc.get(d, 0.0) + float(w) * float(st['fade'])
        out.append(acc)
    return out


def render_audio(track: AudioTrack, mix: list[dict], n: int) -> tuple[np.ndarray, dict]:
    sr, fps = track.rate, track.fps
    total = int(round(n * sr / fps))
    out = np.zeros((total, track.channels), np.float32)
    centers = (np.arange(n, dtype=np.float64) + 0.5) * sr / fps
    deltas = sorted({d for m in mix for d in m})
    size = len(track.data)
    missing = 0
    for d in deltas:
        w = np.array([m.get(d, 0.0) for m in mix], np.float64)
        if not np.any(w > 1e-6):
            continue
        shift = int(round(d * sr / fps))
        for c0 in range(0, total, AUDIO_CHUNK):
            c1 = min(total, c0 + AUDIO_CHUNK)
            g = np.interp(np.arange(c0, c1, dtype=np.float64), centers, w).astype(np.float32)
            g[g <= 1e-4] = 0.0
            need = int(np.count_nonzero(g))
            if not need:
                continue
            lo, hi = max(c0 + shift, 0), min(c1 + shift, size)
            if hi <= lo:
                missing += need
                continue
            a, b = lo - shift - c0, hi - shift - c0
            missing += need - int(np.count_nonzero(g[a:b]))
            out[c0 + a:c0 + b] += track.data[lo:hi] * g[a:b, None]
    if missing > sr / fps:
        raise Refused(f'--audio record refused: {missing} output samples ({missing / sr:.3f} s) fall outside '
                      f'{track.path.name} ({len(track.data) / sr:.3f} s); re-run mcam_audio.py track')
    peak = float(np.max(np.abs(out))) if len(out) else 0.0
    np.clip(out, -1.0, 1.0, out=out)
    return out, {'source': str(track.path), 'codec': track.codec, 'rate': sr, 'channels': track.channels, 'samples': total,
                 'seconds': round(total / sr, 6), 'streams': len(deltas), 'padded_samples': missing,
                 'peak_dbfs': round(20 * math.log10(peak), 2) if peak > 1e-10 else -200.0}


def audio_args(codec: str | None) -> list:
    if codec is None:
        return []
    if codec == 'flac':
        return ['-c:a', 'flac', '-sample_fmt', 's16', '-ar', str(AUDIO_RATE)]
    return ['-c:a', 'aac', '-profile:a', 'aac_low', '-b:a', '320k', '-ar', str(AUDIO_RATE)]


# ------------------------------------------------------------------------------------------------------------- encoding

@dataclass
class EncodeCfg:
    codec: str = 'nvenc'
    crf: float = 18.0
    preset: str = 'slow'
    bits: int = 10
    cq: float = 23.0
    scale: float = 1.0
    nvpreset: str = 'p5'
    draft: bool = False


def encode_cfg(ctx: Ctx) -> EncodeCfg:
    e = ctx.cfg.get('encode') or {}
    a = ctx.args
    cfg = EncodeCfg(str(e.get('codec', 'nvenc')), float(e.get('crf', 18)), str(e.get('preset', 'slow')), int(e.get('bits', 10)),
                    float(e.get('cq', 18)), 1.0, str(e.get('nvpreset', 'p7')))
    if getattr(a, 'draft', False):
        d = e.get('draft') or {}
        cfg.codec = str(d.get('codec', 'nvenc'))
        cfg.cq = float(d.get('cq', 23))
        cfg.nvpreset = str(d.get('nvpreset', 'p5'))
        cfg.scale = float(d.get('scale', 0.5))
        cfg.draft = True
    if getattr(a, 'codec', None):
        cfg.codec = a.codec
    if getattr(a, 'crf', None) is not None:
        cfg.crf = float(a.crf)
        cfg.cq = float(a.crf)
    if getattr(a, 'preset', None):
        cfg.preset = a.preset
    if getattr(a, 'bits', None):
        cfg.bits = int(a.bits)
    if getattr(a, 'scale', None):
        cfg.scale = float(a.scale)
    if cfg.codec not in ('x265', 'nvenc'):
        raise SystemExit(f'codec {cfg.codec}: only x265 and nvenc (H.265) are allowed')
    return cfg


def ffmpeg_cmd(cfg: EncodeCfg, W: int, H: int, fps: float, out: Path, wav: Path | None = None, audio: str | None = None,
               threads: int = 0) -> tuple[list, int, int]:
    """The encode command: raw rgb48le frames on stdin and, with `wav`, the rendered sound as a second input, so the video
    and its audio are written in one pass."""
    W2, H2 = (even(W * cfg.scale), even(H * cfg.scale)) if cfg.scale != 1.0 else (W, H)
    pix = 'yuv420p10le' if cfg.bits == 10 else 'yuv420p'
    tags = 'setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv'
    head = [FF.ffmpeg(), '-hide_banner', '-loglevel', 'error', '-y', '-f', 'rawvideo', '-pix_fmt', 'rgb48le', '-s', f'{W}x{H}',
            '-framerate', f'{fps:g}', '-i', '-']
    head += ['-i', str(wav), '-map', '0:v:0', '-map', '1:a:0'] if wav is not None else ['-an']
    st = max(1, int(threads) or min(8, os.cpu_count() or 4))
    if cfg.codec == 'x265':
        size = f'w={W2}:h={H2}:filter=spline36:' if (W2, H2) != (W, H) else ''
        vf = f'zscale={size}matrix=709:range=limited:dither=error_diffusion,format={pix},{tags}'
        x265 = 'log-level=error:aq-mode=3:colorprim=bt709:transfer=bt709:colormatrix=bt709:range=limited'
        codec = ['-c:v', 'libx265', '-preset', cfg.preset, '-crf', f'{cfg.crf:g}', '-x265-params', x265]
        if cfg.bits == 8:
            codec += ['-profile:v', 'main']
    elif cfg.draft:
        vf = f'scale=w={W2}:h={H2}:flags=area:out_color_matrix=bt709:out_range=tv:threads={st},format={pix},{tags}'
        codec = ['-c:v', 'hevc_nvenc', '-preset', cfg.nvpreset, '-rc', 'vbr', '-cq', f'{cfg.cq:g}', '-b:v', '0']
    else:
        size = f'w={W2}:h={H2}:filter=spline36:' if (W2, H2) != (W, H) else ''
        vf = f'zscale={size}matrix=709:range=limited:dither=error_diffusion,format={pix},{tags}'
        codec = ['-c:v', 'hevc_nvenc', '-preset', cfg.nvpreset, '-tune', 'hq', '-profile:v', 'main10' if cfg.bits == 10 else 'main',
                 '-rc', 'vbr', '-cq', f'{cfg.cq:g}', '-b:v', '0', '-spatial-aq', '1', '-temporal-aq', '1', '-aq-strength', '8',
                 '-bf', '3', '-b_ref_mode', 'middle', '-rc-lookahead', '32']
    mp4 = [] if out.suffix.lower() == '.mkv' else ['-tag:v', 'hvc1', '-movflags', '+faststart']
    return head + ['-vf', vf] + codec + audio_args(audio if wav is not None else None) + mp4 + [str(out)], W2, H2


def ffprobe(path: Path, count: bool = False) -> dict:
    """Streams and format of a media file; {'error': text} when ffprobe fails."""
    try:
        return FF.probe(path, count)
    except FF.FfError as e:
        return {'error': str(e)}


def stream_seconds(s: dict) -> float:
    d = MT.fnum(s.get('duration'))
    if math.isfinite(d):
        return d
    tag = str((s.get('tags') or {}).get('DURATION') or '')
    try:
        h, m, sec = tag.split(':')
        return int(h) * 3600 + int(m) * 60 + float(sec)
    except ValueError:
        return math.nan


def verify_video(path: Path, frames: int, W: int, H: int, bits: int, audio: str | None = None, fps: float = 0.0) -> list[str]:
    mkv = path.suffix.lower() == '.mkv'
    info = ffprobe(path, count=mkv)
    if 'error' in info:
        return [f'ffprobe failed: {info["error"]}']
    streams = info.get('streams', [])
    vids = [s for s in streams if s.get('codec_type') == 'video']
    auds = [s for s in streams if s.get('codec_type') == 'audio']
    probs = []
    if audio is None and auds:
        probs.append('has an audio stream')
    if len(vids) != 1:
        return probs + [f'{len(vids)} video streams']
    v = vids[0]
    want_pix = 'yuv420p10le' if bits == 10 else 'yuv420p'
    checks = [('codec_name', 'hevc'), ('pix_fmt', want_pix), ('color_space', 'bt709'), ('color_transfer', 'bt709'),
              ('color_primaries', 'bt709'), ('color_range', 'tv')] + ([] if mkv else [('codec_tag_string', 'hvc1')])
    for k, want in checks:
        if v.get(k) != want:
            probs.append(f'{k}={v.get(k)} (want {want})')
    if int(v.get('width', 0)) != W or int(v.get('height', 0)) != H:
        probs.append(f'size {v.get("width")}x{v.get("height")} (want {W}x{H})')
    nb = v.get('nb_read_packets') if mkv else v.get('nb_frames')
    if MT.inum(nb, -1) != frames:
        probs.append(f'{"nb_read_packets" if mkv else "nb_frames"}={nb} (want {frames})')
    if audio is not None:
        if len(auds) != 1:
            return probs + [f'{len(auds)} audio streams (want 1)']
        a = auds[0]
        if a.get('codec_name') != audio:
            probs.append(f'audio codec_name={a.get("codec_name")} (want {audio})')
        if audio == 'aac' and a.get('profile') != 'LC':
            probs.append(f'audio profile={a.get("profile")} (want LC)')
        if MT.inum(a.get('sample_rate')) != AUDIO_RATE:
            probs.append(f'audio sample_rate={a.get("sample_rate")} (want {AUDIO_RATE})')
        if fps > 0:
            dv = stream_seconds(v)
            dv = dv if math.isfinite(dv) else frames / fps
            da = stream_seconds(a)
            if not math.isfinite(da) or abs(da - dv) > 1.0 / fps + 1e-6:
                probs.append(f'audio {da:.4f} s vs video {dv:.4f} s: more than one frame apart')
    return probs


def lock_path() -> Path:
    """One lock for this user's heavy encodes: in MCAM_OUT when that is set, else in the temp folder."""
    root = os.environ.get('MCAM_OUT')
    return (Path(root) if root else Path(tempfile.gettempdir())) / LOCK_NAME


def proc_start(pid: int) -> int | None:
    """Start time of a running process as an opaque number, 0 when it runs but the time cannot be read, None when no such
    process runs. Stored in the lock next to the pid, so a pid that the system gave to another program does not count."""
    if pid <= 0:
        return None
    if os.name == 'nt':
        try:
            k = ctypes.WinDLL('kernel32', use_last_error=True)
            k.OpenProcess.restype = ctypes.c_void_p
            k.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
            k.GetProcessTimes.argtypes = [ctypes.c_void_p] + [ctypes.c_void_p] * 4
            k.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
            k.CloseHandle.argtypes = [ctypes.c_void_p]
            h = k.OpenProcess(0x1000, 0, int(pid))
            if not h:
                return 0 if ctypes.get_last_error() == 5 else None
            try:
                code = ctypes.c_ulong(0)
                if k.GetExitCodeProcess(h, ctypes.byref(code)) and code.value != 259:
                    return None
                t = [ctypes.c_ulonglong(0) for _ in range(4)]
                if not k.GetProcessTimes(h, *[ctypes.byref(x) for x in t]):
                    return 0
                return int(t[0].value)
            finally:
                k.CloseHandle(h)
        except (OSError, AttributeError):
            return 0
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return None
    except OSError:
        return 0
    try:
        return int(Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()[19])
    except (OSError, ValueError, IndexError):
        return 0


def pid_alive(pid: int) -> bool:
    return pid == os.getpid() or proc_start(pid) is not None


def lock_owner(path: Path) -> tuple[int, bool]:
    """(pid, alive) of the lock file: alive only when that process runs and, where both are known, started at the time the
    lock recorded."""
    try:
        parts = path.read_text().split()
        pid, start = int(parts[0]), int(parts[1]) if len(parts) > 1 else 0
    except (OSError, ValueError, IndexError):
        return 0, False
    now = proc_start(pid)
    return pid, now is not None and (not start or not now or start == now)


class EncodeLock:
    def __init__(self, force: bool, enabled: bool = True):
        self.force = force
        self.enabled = enabled
        self.held = False
        self.path = lock_path()

    def __enter__(self):
        if not self.enabled:
            return self
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
        except OSError as e:
            raise Refused(f'cannot make the folder of the encode lock {self.path} ({e.strerror or e}); pass --no-lock') from None
        for _ in range(2):
            try:
                fd = os.open(str(self.path), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                os.write(fd, f'{os.getpid()} {proc_start(os.getpid()) or 0}'.encode())
                os.close(fd)
                self.held = True
                return self
            except FileExistsError:
                pid, alive = lock_owner(self.path)
                if alive and not self.force:
                    raise Refused(f'another encode is running (pid {pid}, {self.path}); one heavy encode at a time '
                                  f'(--no-lock to run anyway)')
                try:
                    self.path.unlink()
                except OSError:
                    pass
            except OSError as e:
                raise Refused(f'cannot write the encode lock {self.path} ({e.strerror or e}); pass --no-lock') from None
        if not self.force:
            raise Refused(f'cannot take the encode lock {self.path} (--no-lock to run without it)')
        return self

    def __exit__(self, *exc):
        if self.held:
            try:
                self.path.unlink()
            except OSError:
                pass


class MemStatus(ctypes.Structure):
    _fields_ = [('dwLength', ctypes.c_ulong), ('dwMemoryLoad', ctypes.c_ulong), ('ullTotalPhys', ctypes.c_ulonglong),
                ('ullAvailPhys', ctypes.c_ulonglong), ('ullTotalPageFile', ctypes.c_ulonglong), ('ullAvailPageFile', ctypes.c_ulonglong),
                ('ullTotalVirtual', ctypes.c_ulonglong), ('ullAvailVirtual', ctypes.c_ulonglong),
                ('ullAvailExtendedVirtual', ctypes.c_ulonglong)]


def free_commit_gb() -> float:
    try:
        m = MemStatus()
        m.dwLength = ctypes.sizeof(MemStatus)
        ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m))
        return m.ullAvailPageFile / 2 ** 30
    except Exception:
        return float('inf')


def commit_need_gb(ctx: Ctx) -> float:
    """Commit memory an encode of this take needs: the frame cache, the frames in flight per encode (two per worker in the
    queue, the canvas and a copy for ffmpeg; a canvas can be twice a source frame) and some headroom."""
    a = ctx.args
    w, h = max((s for s in ctx.sizes.values()), key=lambda s: s[0] * s[1], default=ctx.src_size)
    frame = 2 * max(1, w) * max(1, h) * 6
    jobs = max(1, int(getattr(a, 'jobs', 0) or 1))
    flight = jobs * (2 * threads_of(a) + 4) * frame
    return COMMIT_HEADROOM_GB + int(getattr(a, 'cache_mb', 1536)) / 1024.0 + flight / 2 ** 30


def safety_checks(force: bool, need_gb: float | None = None, mem_check: bool = True) -> None:
    need = COMMIT_HEADROOM_GB if need_gb is None else float(need_gb)
    gb = free_commit_gb()
    if mem_check and gb < need and not force:
        raise Refused(f'only {gb:.1f} GB free commit memory, this encode needs about {need:.1f} GB; close something, lower '
                      f'--cache-mb / --threads / --jobs, or pass --no-mem-check')
    if os.name == 'nt':
        try:
            p = FF.run(['tasklist', '/FI', 'IMAGENAME eq INSIDE.exe', '/NH', '/FO', 'CSV'], timeout=15.0, check=False, name='tasklist')
            if 'INSIDE.exe' in p.out:
                print('warning: INSIDE.exe is running; encoding competes with the game for CPU/GPU', flush=True)
        except FF.FfError:
            pass


def file_sig(path) -> list:
    try:
        st = Path(path).stat()
        return [int(st.st_size), int(st.st_mtime)]
    except OSError:
        return [-1, -1]


def take_sig(take: Take) -> list:
    out = [[n, file_sig(take.root / n)] for n in ('meta.csv', 'events.csv', 'run.json', 'spec.json')]
    for c in take.cams.values():
        if c.ext == 'mkv':
            out.append([c.name, 'mkv', file_sig(c.video)])
        else:
            steps = sorted(c.files)
            ends = [file_sig(c.files[s]) for s in (steps[:1] + steps[-1:])]
            out.append([c.name, c.ext, len(steps), steps[:1], steps[-1:], ends])
    return out


def text_sig(path) -> str:
    try:
        return hashlib.sha1(Path(path).read_bytes()).hexdigest() if path and Path(path).is_file() else str(path or '')
    except OSError:
        return str(path)


def job_key(ctx: Ctx, out: Path, cfg: EncodeCfg, W: int, H: int, fps: float, n: int, title: str, audio: bool) -> str:
    """Everything that decides the pixels and the sound of one output, as one hash (--resume compares it)."""
    a = ctx.args
    doc = {'v': RESUME_VERSION, 'out': out.name, 'title': title, 'size': [W, H], 'fps': fps, 'frames': n, 'encode': asdict(cfg),
           'compose': ctx.cfg, 'take': take_sig(ctx.take), 'gpu': bool(ctx.store.gpu), 'blur': ctx.blur_space,
           'args': {k: getattr(a, k, None) for k in ('range', 'frames', 'fps', 'speed', 'shutter', 'no_grade', 'cams', 'rate', 'group',
                                                     'event', 'labels', 'hold', 'dissolve')},
           'files': {k: text_sig(getattr(a, k, None)) for k in ('compose', 'grade', 'layout', 'edit')},
           'grades': {c: g.describe() for c, g in sorted(ctx.grades.items())},
           'audio': [ctx.audio.codec, file_sig(ctx.audio.path)] if audio and ctx.audio is not None else None}
    return hashlib.sha1(json.dumps(doc, sort_keys=True, default=str).encode('utf-8')).hexdigest()


def resume_hit(ctx: Ctx, out: Path, key: str, n: int, W2: int, H2: int, bits: int, codec: str | None, fps: float) -> dict | None:
    """The logged result of an earlier run for this output when the video is still the one that run wrote, was made from
    the same inputs and passes the check again; else None (encode)."""
    for entry in reversed(read_log(ctx.out / LOG_NAME)):
        for it in result_items(entry.get('result')):
            if it.get('inputs') != key or it.get('verify') != 'ok':
                continue
            f = Path(it['file'])
            if f.is_file() and it.get('stamp') == file_sig(f) and not verify_video(f, n, W2, H2, bits, codec, fps):
                return dict(it, resumed=True)
    return None


def place_output(tmp: Path, out: Path) -> Path:
    """Moves the finished file over the old output. When the old one is open in another program (a player) the new video
    gets the name <name>_new and that name is returned."""
    try:
        os.replace(tmp, out)
        return out
    except PermissionError:
        alt = out.with_name(out.stem + '_new' + out.suffix)
        os.replace(tmp, alt)
        note(f'{out.name} is open in another program and was not replaced: the new video is {alt.name}')
        return alt


def remove_quietly(*files) -> None:
    for f in files:
        if f is not None:
            try:
                Path(f).unlink()
            except OSError:
                pass


def encode_video(ctx: Ctx, out: Path, W: int, H: int, fps: float, n: int, make_frame, title: str,
                 stills: int = 0, audio_mix: list | None = None) -> dict:
    cfg = encode_cfg(ctx)
    if cfg.draft and '_draft' not in out.stem:
        out = out.with_name(out.stem.replace('_h265', '') + '_draft_h265' + out.suffix)
    sound = None
    if audio_mix is not None and ctx.audio is not None:
        sound = render_audio(ctx.audio, audio_mix, n)
        if ctx.audio.codec == 'flac':
            out = out.with_suffix('.mkv')
    codec = ctx.audio.codec if sound is not None else None
    tmp = out.with_name(out.stem + '.partial' + out.suffix)
    wav = out.with_name(out.stem + '.partial.wav') if sound is not None else None
    workers = threads_of(ctx.args)
    cmd, W2, H2 = ffmpeg_cmd(cfg, W, H, fps, tmp, wav, codec, workers)
    key = job_key(ctx, out, cfg, W, H, fps, n, title, sound is not None)
    if getattr(ctx.args, 'resume', False):
        hit = resume_hit(ctx, out, key, n, W2, H2, cfg.bits, codec, fps)
        if hit is not None:
            print(f'{title}: {Path(hit["file"]).name} is up to date (same inputs, check ok): skipped by --resume', flush=True)
            return hit
    still_set = set(np.linspace(0, n - 1, stills).round().astype(int).tolist()) if stills > 0 and n > 0 else set()
    still_dir = ctx.out / 'stills'
    t0 = time.time()
    print(f'{title}: {n} frames {W}x{H} -> {out.name} ({cfg.codec}{" draft" if cfg.draft else ""}, {W2}x{H2}, {fps:g} fps)', flush=True)
    ok = False
    try:
        if wav is not None:
            MA.write_wav(wav, sound[0], ctx.audio.rate, pcm16=False)
        with FF.Child(cmd, stdin=True, name='ffmpeg') as enc, ThreadPoolExecutor(workers) as pool:
            q: deque = deque()
            try:
                nxt = 0
                last = t0
                for j in range(n):
                    if STOP.is_set():
                        raise RuntimeError(f'{title}: stopped because another encode of this run failed or was interrupted')
                    while nxt < n and len(q) < workers * 2:
                        q.append(pool.submit(make_frame, nxt))
                        nxt += 1
                    img = q.popleft().result()
                    if img.shape != (H, W, 3) or img.dtype != np.uint16:
                        raise RuntimeError(f'frame {j}: got {img.shape} {img.dtype}, want {(H, W, 3)} uint16')
                    enc.write(np.ascontiguousarray(img).data)
                    if j in still_set:
                        still_dir.mkdir(parents=True, exist_ok=True)
                        save_png8(img, still_dir / f'{out.stem}_{j:05d}.png', max_w=1600)
                    now = time.time()
                    if now - last > 5 or j == n - 1:
                        last = now
                        print(f'  {title}: {j + 1}/{n} frames, {(j + 1) / max(now - t0, 1e-6):.1f} fps', flush=True)
            except BaseException:
                for fu in q:
                    fu.cancel()
                raise
        ok = True
    finally:
        remove_quietly(wav, None if ok else tmp)
    out = place_output(tmp, out)
    probs = verify_video(out, n, W2, H2, cfg.bits, codec, fps)
    dt = time.time() - t0
    res = {'file': str(out), 'frames': n, 'size': [W2, H2], 'fps': fps, 'seconds': round(n / fps, 3), 'codec': cfg.codec,
           'encode_s': round(dt, 1), 'mb': round(out.stat().st_size / 2 ** 20, 2), 'verify': probs or 'ok', 'inputs': key,
           'stamp': file_sig(out)}
    if sound is not None:
        res['audio'] = sound[1]
    wrote(out)
    print(f'  done in {dt:.1f} s, {res["mb"]} MB, {"audio " + codec + ", " if codec else ""}ffprobe: '
          f'{"ok" if not probs else "; ".join(probs)}', flush=True)
    return res


def save_png8(img16: np.ndarray, path: Path, max_w: int = 0) -> None:
    im8 = (img16 >> 8).astype(np.uint8)
    if max_w and im8.shape[1] > max_w:
        s = max_w / im8.shape[1]
        im8 = cv2.resize(im8, (max_w, even(im8.shape[0] * s)), interpolation=cv2.INTER_AREA)
    Image.fromarray(im8).save(path)


# ------------------------------------------------------------------------------------------------------------- commands

def limit_timeline(ctx: Ctx, tl: Timeline) -> Timeline:
    a = ctx.args
    k0, k1 = 0, len(tl)
    if getattr(a, 'range', None):
        t0, _, t1 = a.range.partition(':')
        if t0:
            k0 = tl.frame_at_or_end(ctx.take.resolve_time(t0))
        if t1:
            k1 = tl.frame_at_or_end(ctx.take.resolve_time(t1))
    if getattr(a, 'frames', None):
        k1 = min(k1, k0 + int(a.frames))
    return tl.sliced(k0, max(k0, k1))


def angle_timeline(ctx: Ctx) -> Timeline:
    speed = getattr(ctx.args, 'speed', None) or (ctx.cfg.get('angles') or {}).get('speed', 'meta')
    if speed == 'realtime':
        shutter = float(getattr(ctx.args, 'shutter', None) or (ctx.cfg.get('blur') or {}).get('shutter', 180))
        fps = float(getattr(ctx.args, 'fps', None) or ctx.take.fps)
        return MT.build_warp(ctx.take.base, [[0.0, 1.0]], fps, shutter, 'nearest', step_times=MT.attach_step_times(ctx.take))
    return base_timeline(ctx)


def cams_arg(ctx: Ctx, default) -> list[str]:
    raw = getattr(ctx.args, 'cams', None) or default
    names = ctx.take.camera_names() if raw in (None, 'all') else ([c for c in raw.split(',') if c] if isinstance(raw, str) else list(raw))
    bad = [c for c in names if c not in ctx.take.cams]
    if bad:
        raise SystemExit(f'unknown cameras {bad}; take has {list(ctx.take.cams)}')
    return names


def cmd_scan(ctx: Ctx) -> dict:
    take = ctx.take
    s = take.summary()
    for c in s['cameras']:
        c['size'] = list(ctx.sizes.get(c['name'], ctx.src_size))
    tl = ctx.take.base
    doc = {'summary': s, 'timeline': tl.to_json()}
    edit = current_edit(ctx, required=False)
    if edit:
        try:
            plan = plan_edit(ctx, edit)
            doc['edit'] = plan.to_json()
        except (SystemExit, TakeError) as e:
            doc['edit_error'] = str(e)
    (ctx.out / 'timeline.json').write_text(json.dumps(doc, indent=1), encoding='utf-8')
    wrote(ctx.out / 'timeline.json')
    print(f'take {take.root} ({take.kind}{", " + take.mode if take.replay_mode else ""}), {len(take.cams)} cameras, '
          f'{s["steps"]} steps, {s["frames"]} output frames at {take.fps:g} fps = {s["output_seconds"]} s, '
          f'game {s["game_seconds"][0]}..{s["game_seconds"][1]} s'
          + (f', real {s["real_seconds"][0]}..{s["real_seconds"][1]} s' if take.replay_mode and s.get('real_seconds') else ''))
    if take.replay_mode:
        print(f'  record {s.get("record_dir")}; audio: {s["audio"]["verdict"]}')
    for c in s['cameras']:
        print(f'  {c["name"]:<14} {c["mode"] or "-":<9} {c["format"]:<4} files {c["files"]} size {c["size"][0]}x{c["size"][1]}'
              f'{"" if c["enabled"] else " (disabled)"}')
    for seg in s['segments']:
        tag = f'slow x{1 / seg["speed"]:.2f}' if seg['speed'] < 0.999 else ('fast' if seg['speed'] > 1.001 else 'real time')
        print(f'  segment {seg["segment"]}: game {seg["from_t"]}..{seg["to_t"]} s, {seg["frames"]} frames, {seg["samples"]} '
              f'sample(s) per frame, speed {seg["speed"]} ({tag})')
    for t, k, n in take.event_list()[:40]:
        print(f'  {k:<8} {t:8.3f}  {n}')
    if 'edit' in doc:
        e = doc['edit']
        print(f'  edit {e["name"]}: {e["frames"]} frames = {e["seconds"]} s, {len(e["shots"])} shots')
        for w in e['warnings']:
            print(f'    warning: {w}')
    for w in take.warnings:
        print(f'  warning: {w}')
    print(f'wrote {ctx.out / "timeline.json"}')
    return doc


def angle_maker(ctx: Ctx, cam: str, tl: Timeline, show_labels: bool):
    W, H = ctx.sizes.get(cam, ctx.src_size)
    W, H = even(W), even(H)
    text = ctx.labels.text_for(ctx.take, cam)

    def make(j: int) -> np.ndarray:
        img = render_view(ctx, cam, tl[j], W, H)
        if show_labels:
            px = label_px(ctx, W)
            ctx.labels.draw(img, text, int(px * 0.9), H - int(px * 0.8), px)
        return img

    return W, H, make


def angle_jobs(ctx: Ctx, cams: list[str]) -> list[dict]:
    tl = limit_timeline(ctx, angle_timeline(ctx))
    if not len(tl):
        raise SystemExit('no frames in range')
    setup_grades(ctx, cams)
    show_labels = bool((ctx.cfg.get('angles') or {}).get('labels', False)) and ctx.labels.show
    mix = None
    if ctx.audio is not None:
        require_meta_timeline(ctx, tl, 'angles')
        mix = mix_from_timeline(tl, len(tl))
    jobs = []
    for cam in cams:
        W, H, make = angle_maker(ctx, cam, tl, show_labels)
        jobs.append({'kind': 'angle', 'out': ctx.out / f'angle_{safe_name(cam)}_h265.mp4', 'W': W, 'H': H, 'fps': tl.fps,
                     'n': len(tl), 'make': make, 'title': f'angle {cam}', 'mix': mix})
    return jobs


def run_job(ctx: Ctx, job: dict) -> dict:
    return encode_video(ctx, job['out'], job['W'], job['H'], job['fps'], job['n'], job['make'], job['title'],
                        int(getattr(ctx.args, 'stills', 0) or 0), job['mix'])


def cmd_angles(ctx: Ctx) -> list[dict]:
    cams = cams_arg(ctx, (ctx.cfg.get('angles') or {}).get('cams', 'all'))
    return [run_job(ctx, j) for j in angle_jobs(ctx, cams)]


def layouts_of(ctx: Ctx) -> list[dict]:
    arg = getattr(ctx.args, 'layout', None)
    if arg:
        lay = load_json_arg(arg, ctx.cfg, 'layouts')
        if isinstance(lay, dict) and isinstance(lay.get('layouts'), list):
            return list(lay['layouts'])
        return [lay]
    lays = ctx.cfg.get('layouts')
    if isinstance(lays, dict):
        lays = [lays]
    return list(lays) if lays else [default_layout(ctx.take)]


def current_edit(ctx: Ctx, required: bool = True) -> dict | None:
    arg = getattr(ctx.args, 'edit', None)
    if arg:
        e = load_json_arg(arg, ctx.cfg, 'edit')
        if isinstance(e, dict) and 'shots' not in e and isinstance(e.get('edit'), dict):
            return e['edit']
        return e
    e = ctx.cfg.get('edit')
    if isinstance(e, list):
        e = e[0] if e else None
    if e:
        return e
    if not required:
        return None
    print('no edit given (compose.edit or --edit); drafting one with autocut', flush=True)
    e = autocut_edit(ctx)
    (ctx.out / 'edit_autocut.json').write_text(json.dumps(e, indent=1), encoding='utf-8')
    wrote(ctx.out / 'edit_autocut.json')
    return e


def resolve_cells(ctx: Ctx, cells: list[Cell]) -> list[Cell]:
    for c in cells:
        if c.pan:
            c.pan = [[ctx.take.resolve_time(k[0]), float(k[1]), float(k[2])] for k in c.pan]
    return cells


def layout_maker(ctx: Ctx, lay: dict, auto_edit: bool) -> dict | None:
    name = str(lay.get('name', 'main'))
    W, H, cells = layout_geometry(lay, ctx.sizes, ctx.src_size)
    resolve_cells(ctx, cells)
    cams = sorted({c.cam for c in cells if c.cam != 'cut'})
    for c in cams:
        if c not in ctx.take.cams:
            raise SystemExit(f'layout {lay.get("name")}: camera "{c}" is not in the take')
    plan = None
    if any(c.cam == 'cut' for c in cells):
        edit = current_edit(ctx, required=auto_edit)
        if edit is None:
            print(f'layout {name}: skipped (it has a cut cell and the spec has no edit; no autocut in all)', flush=True)
            return None
        plan = plan_edit(ctx, edit)
        cams = sorted(set(cams) | {s.cam for s in plan.shots})
        tl = plan.tl
        n = plan.total
        if getattr(ctx.args, 'frames', None):
            n = min(n, int(ctx.args.frames))
    else:
        tl = limit_timeline(ctx, base_timeline(ctx))
        n = len(tl)
    if n <= 0:
        raise SystemExit('no frames in range')
    setup_grades(ctx, cams)
    for i, c in enumerate(cells):
        if c.cam != 'cut':
            c.cg = cell_grade(ctx, c.cam, c.grade, f'layout {name} cell {i} ({c.cam}) grade')
    show = ctx.labels.show and bool(lay.get('labels', True))
    print(f'layout {name}: canvas {W}x{H}, ' + ', '.join(f'{c.cam} {c.w}x{c.h}@{c.x},{c.y}' + (' (cell grade)' if c.cg else '')
                                                       for c in cells), flush=True)

    def make(j: int) -> np.ndarray:
        canvas = np.zeros((H, W, 3), np.uint16)
        main_src = plan.at(j)['main'][1] if plan else j
        for c in cells:
            if c.cam == 'cut':
                sub = render_edit_view(ctx, plan, j, c.w, c.h, c.crop, c.anchor, c.pan, show, 0.75 if not c.band else 1.0, False)
                canvas[c.y:c.y + c.h, c.x:c.x + c.w] = sub
                continue
            canvas[c.y:c.y + c.h, c.x:c.x + c.w] = render_view(ctx, c.cam, tl[main_src], c.w, c.h, c.crop, c.anchor, c.pan, c.cg)
            if show and c.label is not False:
                px = label_px(ctx, W, 1.0 if c.band else 0.75)
                ctx.labels.draw(canvas, ctx.labels.text_for(ctx.take, c.cam, c.label), c.x + int(px * 0.8),
                                c.y + c.h - int(px * 0.7), px)
        if plan:
            canvas = fade(canvas, plan.at(j)['fade'])
        return canvas

    return {'name': name, 'W': W, 'H': H, 'tl': tl, 'n': n, 'plan': plan, 'cells': cells, 'make': make}


def layout_jobs(ctx: Ctx, auto_edit: bool) -> list[dict]:
    jobs = []
    for lay in layouts_of(ctx):
        m = layout_maker(ctx, lay, auto_edit)
        if m is None:
            continue
        mix = None
        if ctx.audio is not None:
            require_meta_timeline(ctx, m['tl'], f'layout {m["name"]}')
            mix = mix_from_plan(m['plan'], m['n']) if m['plan'] else mix_from_timeline(m['tl'], m['n'])
        jobs.append({'kind': 'layout', 'out': ctx.out / f'layout_{safe_name(m["name"])}_h265.mp4', 'W': m['W'], 'H': m['H'],
                     'fps': m['tl'].fps, 'n': m['n'], 'make': m['make'], 'title': f'layout {m["name"]}', 'mix': mix})
    return jobs


def cmd_layout(ctx: Ctx, auto_edit: bool = True) -> list[dict]:
    return [run_job(ctx, j) for j in layout_jobs(ctx, auto_edit)]


# --------------------------------------------------------------------------------------------------------------- finals

def layout_cams(ctx: Ctx) -> set[str]:
    return {c.cam for lay in layouts_of(ctx) for c in layout_geometry(lay, ctx.sizes, ctx.src_size)[2] if c.cam != 'cut'}


def finals_cams(ctx: Ctx) -> list[str]:
    raw = getattr(ctx.args, 'cams', None) or (ctx.cfg.get('finals') or {}).get('cams')
    if raw:
        return cams_arg(ctx, raw)
    shown = layout_cams(ctx)
    return [c for c in ctx.take.camera_names() if ctx.take.cams[c].mode == 'director' and c not in shown]


def needs_stats(ctx: Ctx, cam: str) -> bool:
    spec = grade_spec_for(ctx.cfg, cam, ctx.take.cams[cam].mode)
    return not getattr(ctx.args, 'no_grade', False) and not spec.off and (spec.floor == 'auto' or bool(spec.auto))


def warm_grades(ctx: Ctx, cams: list[str]) -> float:
    t0 = time.time()
    todo = [c for c in cams if c not in ctx.grades and c in ctx.take.cams]
    procs = [c for c in todo if needs_stats(ctx, c)]
    if len(procs) > 1:
        a = ctx.args
        job = partial(MT.stats_job, str(ctx.take.root), getattr(a, 'rate', None), getattr(a, 'group', None),
                      parse_events(getattr(a, 'event', None)), gpu=ctx.store.gpu, workers=threads_of(a))
        try:
            with ProcessPoolExecutor(len(procs), initializer=set_stats_root, initargs=(str(MT.OUT_ROOT),)) as pool:
                list(pool.map(job, procs))
        except Exception as e:
            print(f'grade stats: the parallel processes failed ({type(e).__name__}: {e}); computing them here', flush=True)
    for c in todo:
        setup_grades(ctx, [c])
    ctx.store.drop_readers()
    return round(time.time() - t0, 1)


def cmd_finals(ctx: Ctx) -> dict:
    t0 = time.time()
    cams = finals_cams(ctx)
    stats_s = warm_grades(ctx, sorted(set(cams) | layout_cams(ctx)))
    jobs = (angle_jobs(ctx, cams) if cams else []) + layout_jobs(ctx, False)
    if not jobs:
        raise SystemExit('finals: nothing to compose (no director angle and no layout)')
    n = max(1, min(len(jobs), int(getattr(ctx.args, 'jobs', 0) or 1)))
    print(f'finals: {", ".join(j["title"] for j in jobs)}; grade stats {stats_s} s; {n} encode(s) at once', flush=True)
    with ThreadPoolExecutor(n) as pool:
        try:
            res = list(pool.map(lambda j: run_job(ctx, j), jobs))
        except BaseException:
            STOP.set()
            raise
    doc = {'angles': [r for j, r in zip(jobs, res) if j['kind'] == 'angle'],
           'layouts': [r for j, r in zip(jobs, res) if j['kind'] == 'layout'],
           'grade_stats_s': stats_s, 'jobs': n, 'wall_s': round(time.time() - t0, 1)}
    print(f'finals: {len(res)} video(s) in {doc["wall_s"]} s (grade stats {stats_s} s, {n} at once)', flush=True)
    return doc


# ---------------------------------------------------------------------------------------------------------------- review

def review_config(ctx: Ctx) -> dict:
    cfg = dict(ctx.cfg.get('review') or {})
    a = ctx.args
    if getattr(a, 'events', None):
        cfg['events'] = [e for e in a.events.split(',') if e]
    for k in ('around', 'near', 'every'):
        v = getattr(a, k, None)
        if v is not None:
            cfg[k] = v
    return cfg


def review_events(ctx: Ctx, pats: list) -> list[tuple[float, str]]:
    out: list[tuple[float, str]] = []
    names = [(t, n) for t, k, n in ctx.take.event_list() if k == 'event']
    for p in pats or ['*']:
        p = str(p)
        if p.startswith('@'):
            try:
                out.append((ctx.take.resolve_time(p), p[1:]))
            except TakeError as e:
                print(f'review: {p}: {e} (skipped)', flush=True)
            continue
        out += [(t, n) for t, n in names if fnmatch.fnmatchcase(n, p)]
    return sorted(set(out))


def review_picks(game_t: np.ndarray, events: list[tuple[float, str]], every: float, near: float, around: float) -> list[tuple[int, str]]:
    if not len(game_t):
        return []
    picks: dict[int, str] = {}

    def at(x: float) -> int:
        return int(np.argmin(np.abs(game_t - x)))

    t0, t1 = float(np.min(game_t)), float(np.max(game_t))
    if every > 0:
        for x in np.arange(t0, t1 + 1e-9, every):
            picks.setdefault(at(float(x)), 'every')
    if near > 0:
        for te, name in events:
            if te < t0 - around or te > t1 + around:
                continue
            for x in np.arange(te - around, te + around + 1e-9, near):
                if t0 - 1e-6 <= x <= t1 + 1e-6:
                    j = at(float(x))
                    if picks.get(j, 'every') == 'every':
                        picks[j] = f'{name}{x - te:+.2f}'
    return sorted(picks.items())


def cmd_review(ctx: Ctx) -> dict:
    cfg = review_config(ctx)
    every, near, around = float(cfg.get('every', 2.0)), float(cfg.get('near', 0.5)), float(cfg.get('around', 1.0))
    events = review_events(ctx, cfg.get('events') or [])
    want = str(getattr(ctx.args, 'outputs', None) or cfg.get('outputs', 'all'))
    outs: list[tuple[str, int, int, int, object, np.ndarray]] = []
    if want in ('all', 'angles') or want.startswith('angle'):
        cams = cams_arg(ctx, (ctx.cfg.get('angles') or {}).get('cams', 'all'))
        tl = limit_timeline(ctx, angle_timeline(ctx))
        setup_grades(ctx, cams)
        show_labels = bool((ctx.cfg.get('angles') or {}).get('labels', False)) and ctx.labels.show
        gt = np.array([f.t for f in tl.frames])
        for cam in cams:
            if want not in ('all', 'angles') and want != f'angle_{cam}':
                continue
            W, H, make = angle_maker(ctx, cam, tl, show_labels)
            outs.append((f'angle_{safe_name(cam)}', W, H, len(tl), make, gt))
    if want in ('all', 'layouts') or want.startswith('layout'):
        for lay in layouts_of(ctx):
            if want not in ('all', 'layouts') and want != f'layout_{lay.get("name", "main")}':
                continue
            m = layout_maker(ctx, lay, False)
            if m is None:
                continue
            plan, tl = m['plan'], m['tl']
            gt = np.array([tl[plan.at(j)['main'][1]].t if plan else tl[j].t for j in range(m['n'])])
            outs.append((f'layout_{safe_name(m["name"])}', m['W'], m['H'], m['n'], m['make'], gt))
    if not outs:
        raise SystemExit(f'review: no output matches "{want}" (all, angles, layouts, angle_<cam>, layout_<name>)')
    root = ctx.out / 'review'
    doc = {'every': every, 'near': near, 'around': around, 'events': [[round(t, 4), n] for t, n in events], 'outputs': {}}
    workers = threads_of(ctx.args)
    for name, W, H, n, make, gt in outs:
        folder = root / name
        folder.mkdir(parents=True, exist_ok=True)
        picks = review_picks(gt, events, every, near, around)
        t0 = time.time()
        fps = ctx.take.fps

        def one(item, make=make, folder=folder, gt=gt):
            j, why = item
            img = make(j)
            f = folder / f'{j:05d}_t{gt[j]:08.3f}_{safe_name(why)}.png'
            Image.fromarray((img >> 8).astype(np.uint8)).save(f, compress_level=3)
            return [j, round(j / fps, 4), round(float(gt[j]), 4), why, f.name]

        with ThreadPoolExecutor(workers) as pool:
            rows = list(pool.map(one, picks))
        with (folder / 'index.csv').open('w', encoding='utf-8', newline='') as fh:
            cw = csv.writer(fh, lineterminator='\n')
            cw.writerow(['frame', 'output_t', 'game_t', 'why', 'file'])
            cw.writerows(rows)
        wrote(folder)
        doc['outputs'][name] = {'size': [W, H], 'frames': len(rows), 'folder': str(folder)}
        print(f'review {name}: {len(rows)} full-resolution frames {W}x{H} -> {folder} ({time.time() - t0:.1f} s)', flush=True)
    (root / 'review.json').write_text(json.dumps(doc, indent=1), encoding='utf-8')
    wrote(root / 'review.json')
    print(f'wrote {root / "review.json"}')
    return doc


def cmd_cut(ctx: Ctx) -> dict:
    edit = current_edit(ctx)
    plan = plan_edit(ctx, edit)
    for w in plan.warnings:
        print(f'warning: {w}', flush=True)
    setup_grades(ctx, sorted({s.cam for s in plan.shots}))
    W, H = even(ctx.src_size[0]), even(ctx.src_size[1])
    n = plan.total
    if getattr(ctx.args, 'frames', None):
        n = min(n, int(ctx.args.frames))
    name = safe_name(plan.name)
    (ctx.out / f'cut_{name}.json').write_text(json.dumps(plan.to_json(), indent=1), encoding='utf-8')
    wrote(ctx.out / f'cut_{name}.json')
    print(f'cut {plan.name}: {plan.total} frames = {plan.total / plan.tl.fps:.2f} s ({plan.tl.mode} timeline), '
          + ', '.join(f'{s.cam} {s.n / plan.tl.fps:.1f}s' for s in plan.shots), flush=True)

    mix = None
    if ctx.audio is not None:
        require_meta_timeline(ctx, plan.tl, f'cut {plan.name}')
        mix = mix_from_plan(plan, n)

    def make(j: int) -> np.ndarray:
        return render_edit_view(ctx, plan, j, W, H)

    return encode_video(ctx, ctx.out / f'cut_{name}_h265.mp4', W, H, plan.tl.fps, n, make, f'cut {plan.name}',
                        int(getattr(ctx.args, 'stills', 0) or 0), mix)


def cmd_all(ctx: Ctx) -> dict:
    res = {'angles': cmd_angles(ctx), 'layouts': cmd_layout(ctx, auto_edit=False)}
    if current_edit(ctx, required=False):
        res['cut'] = cmd_cut(ctx)
    else:
        print('all: no edit in the spec (compose.edit or --edit): no cut video (layouts and the director camera are the edit; '
              'run "cut" to draft one with autocut)', flush=True)
    return res


def cmd_grade(ctx: Ctx) -> dict:
    cams = cams_arg(ctx, 'all')
    setup_grades(ctx, cams)
    doc = {}
    for cam in cams:
        g = ctx.grades[cam]
        doc[cam] = g.describe()
        lut = g.lut('g16', g.spec.ev)
        idx = np.linspace(0, 65535, 1024).round().astype(int)
        vals = lut[idx] / 65535.0
        lines = ['TITLE "mcam grade ' + cam.replace('"', "'") + '"', 'LUT_1D_SIZE 1024'] + [f'{v:.6f} {v:.6f} {v:.6f}' for v in vals]
        (ctx.out / f'grade_{safe_name(cam)}.cube').write_text('\n'.join(lines) + '\n', encoding='utf-8')
        wrote(ctx.out / f'grade_{safe_name(cam)}.cube')
        xs = np.unique(np.concatenate([np.linspace(0, 1, 17), [g.floor]]))
        pts = ' '.join(f'{x:.4f}/{lut[int(round(x * 65535))] / 65535.0:.4f}' for x in xs)
        doc[cam]['ffmpeg_curves'] = f"curves=all='{pts}'"
        print(f'{cam:<14} floor {doc[cam]["floor_8bit"]:6.2f}/255  lift {g.spec.lift} k {g.spec.k} ev {g.spec.ev}'
              f'{"  auto ev " + str(doc[cam]["auto_ev"]) if g.spec.auto else ""}')
    (ctx.out / 'grade.json').write_text(json.dumps(doc, indent=1), encoding='utf-8')
    wrote(ctx.out / 'grade.json')
    print(f'wrote {ctx.out / "grade.json"} and grade_<cam>.cube')
    return doc


def cmd_director(ctx: Ctx) -> dict:
    edit = current_edit(ctx)
    tl = edit_timeline(ctx, edit)
    shots = []
    warn = []
    prev_from = -1e9
    raw = sorted(edit.get('shots') or [], key=lambda s: ctx.take.resolve_time(s.get('from', 0), 0.0))
    for i, s in enumerate(raw):
        if s.get('replay'):
            warn.append(f'shot {i} ({s.get("cam")}): replay cannot be filmed by a director camera, skipped')
            continue
        t = ctx.take.resolve_time(s.get('from', 0), 0.0)
        tin = s.get('in') or {'type': 'cut'}
        if isinstance(tin, str):
            tin = {'type': tin}
        typ = tin.get('type', 'cut')
        out = {'cam': s['cam']}
        if i == 0 or typ == 'cut':
            out['from'] = round(t, 4)
            if i > 0:
                out['transition'] = 'cut'
        else:
            dur_out = float(tin.get('dur', 1.0))
            spd = tl.speed_at(t)
            blend = dur_out * spd
            align = tin.get('align', 'center')
            start = t - {'center': blend / 2, 'start': 0.0, 'end': blend}.get(align, blend / 2)
            out['from'] = round(start, 4)
            out['blend'] = round(blend, 4)
            out['ease'] = 'smooth'
            out['transition'] = 'orbit' if typ == 'dissolve' else 'cut'
            if typ == 'dip':
                warn.append(f'shot {i} ({s["cam"]}): dip has no in-engine equivalent, using a cut')
                out.pop('blend')
                out['from'] = round(t, 4)
        if s.get('label'):
            out['label'] = s['label']
        if out['from'] <= prev_from:
            warn.append(f'shot {i} ({s["cam"]}): blend start {out["from"]} is not after the previous shot')
        prev_from = out['from']
        shots.append(out)
    cam = {'name': 'director', 'mode': 'director', 'label': 'Director', 'minHold': float(edit.get('minHold', 4.0)), 'shots': shots}
    text = json.dumps(cam, indent=1)
    dest = Path(ctx.args.out) if getattr(ctx.args, 'out', None) and str(ctx.args.out).lower().endswith('.json') else ctx.out / f'director_{safe_name(str(edit.get("name", "cut")))}.json'
    if dest.is_file() and not getattr(ctx.args, 'force', False):
        try:
            old = json.loads(dest.read_text(encoding='utf-8'))
        except (OSError, ValueError):
            old = None
        if not (isinstance(old, dict) and old.get('mode') == 'director'):
            raise Refused(f'{dest} exists and is not a director camera block; pass --force to overwrite it')
    try:
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(text + '\n', encoding='utf-8')
    except OSError as e:
        raise Refused(f'cannot write {dest} ({e.strerror or e})') from None
    wrote(dest)
    print(text)
    for w in warn:
        print(f'warning: {w}')
    print(f'wrote {dest}')
    return cam


def motion_energy(ctx: Ctx, tl: Timeline, cams: list[str]) -> dict[str, np.ndarray]:
    steps = sorted({s for f in ctx.take.base.frames for s in f.steps})
    out = {}
    for cam in cams:
        st = MT.camera_stats(ctx.take, ctx.store, cam, steps, workers=threads_of(ctx.args))
        idx = {int(s): i for i, s in enumerate(st['steps'])}
        e = np.array([np.mean([st['energy'][idx[s]] for s in f.steps if s in idx] or [0.0]) for f in tl.frames])
        r = max(1, int(round(tl.fps * 0.5)))
        ker = np.ones(2 * r + 1) / (2 * r + 1)
        out[cam] = np.convolve(np.pad(e, r, mode='edge'), ker, mode='valid')
    return out


def autocut_edit(ctx: Ctx) -> dict:
    tl = limit_timeline(ctx, base_timeline(ctx))
    fps = tl.fps
    cams = [c for c in cams_arg(ctx, 'all') if ctx.take.cams[c].mode != 'director']
    if not cams:
        raise SystemExit('autocut: no camera to cut between (director cameras are not used; see --cams)')
    hold = str(getattr(ctx.args, 'hold', None) or '4:6')
    hmin, _, hmax = hold.partition(':')
    hmin, hmax = float(hmin), float(hmax or hmin)
    diss = float(getattr(ctx.args, 'dissolve', None) or 1.0)
    n = len(tl)
    if n == 0:
        raise SystemExit('no frames for autocut')
    energy = motion_energy(ctx, tl, cams)
    ev_frames = sorted({tl.frame_at_or_end(t) for t, k, _ in ctx.take.event_list() if k == 'event'})
    norm = {c: e / (np.percentile(np.concatenate(list(energy.values())), 90) + 1e-6) for c, e in energy.items()}
    a, b = int(round(hmin * fps)), int(round(hmax * fps))

    def window_score(c: str, k0: int, k1: int) -> float:
        seg = norm[c][k0:max(k0 + 1, k1)]
        return float(seg.mean()) if len(seg) else 0.0

    cur = 'game' if 'game' in cams else max(cams, key=lambda c: window_score(c, 0, a))
    shots = [{'cam': cur, 'from': round(tl[0].t, 4)}]
    used = [cur]
    start = 0
    while n - start > b + a // 2:
        lo, hi = start + a, min(start + b, n - a)
        if hi < lo:
            break
        evs = [k for k in ev_frames if lo <= k <= hi]
        if evs:
            cut_at, kind = evs[0], 'cut'
        else:
            best, cut_at = -1.0, hi
            for k in range(lo, hi + 1, max(1, int(fps // 6))):
                alt = max((window_score(c, k, k + a) for c in cams if c != cur), default=0.0)
                sc = alt - 0.5 * window_score(cur, k, k + a)
                if sc > best:
                    best, cut_at = sc, k
            kind = 'dissolve'

        def pick(c: str) -> float:
            s = window_score(c, cut_at, cut_at + b)
            if c in used[-2:]:
                s *= 0.6
            return s

        nxt = max((c for c in cams if c != cur), key=pick, default=cur)
        sh = {'cam': nxt, 'from': round(tl[cut_at].t, 4)}
        sh['in'] = {'type': 'cut'} if kind == 'cut' or len(cams) < 2 else {'type': 'dissolve', 'dur': diss, 'align': 'center'}
        shots.append(sh)
        used.append(nxt)
        cur, start = nxt, cut_at
    return {'name': 'autocut', 'fadeIn': 0.5, 'fadeOut': 1.0, 'minHold': hmin, 'maxHold': max(hmax, hmin) + 0.5,
            'shots': shots, '_energy': {c: round(float(np.mean(e)), 4) for c, e in energy.items()}}


def cmd_autocut(ctx: Ctx) -> dict:
    e = autocut_edit(ctx)
    p = ctx.out / 'edit_autocut.json'
    p.write_text(json.dumps(e, indent=1), encoding='utf-8')
    wrote(p)
    print(json.dumps(e, indent=1))
    print(f'wrote {p}')
    return e


# --------------------------------------------------------------------------------------------------------------- doctor

def ffmpeg_age(text: str, num: tuple | None) -> tuple[str, str]:
    if num is not None:
        return ('ok', '') if num >= (6, 1) else ('warn', 'older than 6.1: update ffmpeg (the encode options were tested on 6.1 and later)')
    y = text[:10].split('-')
    if len(y) == 3 and all(p.isdigit() for p in y):
        return ('ok', '') if (int(y[0]), int(y[1])) >= (2023, 11) else ('warn', 'a build from before 6.1: update ffmpeg')
    return 'info', 'version not recognised'


def ff_test(cmd: list) -> str:
    try:
        FF.run([FF.ffmpeg(), '-hide_banner', '-loglevel', 'error', '-threads', '2'] + cmd, timeout=90.0)
        return ''
    except FF.FfError as e:
        return str(e)


def folder_writable(path: Path) -> str:
    p = path
    while not p.exists() and p.parent != p:
        p = p.parent
    try:
        with tempfile.NamedTemporaryFile(dir=str(p), prefix='.mcam_doctor_'):
            pass
        return ''
    except OSError as e:
        return f'{p}: {e.strerror or e}'


def doctor_rows(args) -> list[dict]:
    rows: list[dict] = []

    def add(level: str, name: str, detail: str, fix: str = '') -> None:
        rows.append({'check': name, 'level': level, 'detail': detail, 'fix': fix})

    tools_ok = True
    for which in ('ffmpeg', 'ffprobe'):
        try:
            text, num = FF.version(which)
            level, why = ffmpeg_age(text, num) if which == 'ffmpeg' else ('ok', '')
            add(level, which, f'{FF.exe(which)}  version {text}', why)
        except FF.FfError as e:
            tools_ok = False
            add('fail', which, str(e), f'install ffmpeg (6.1 or later, a build with NVENC and libzimg) and put it on PATH, or set {which.upper()}')
    if tools_ok:
        try:
            enc, flt = FF.encoders(), FF.filters()
        except FF.FfError as e:
            enc, flt = set(), set()
            add('fail', 'ffmpeg lists', str(e))
        chain = 'zscale=matrix=709:range=limited:dither=error_diffusion,format=yuv420p10le'
        null = ['-f', 'null', '-']
        src = ['-f', 'lavfi', '-i', 'color=c=gray:s=256x144:r=30,format=rgb48le', '-frames:v', '2']
        z = 'zscale' in flt
        zerr = ff_test(src + ['-vf', chain] + null) if z else 'filter zscale is not in this ffmpeg'
        add('ok' if not zerr else 'fail', 'filter zscale', 'RGB to BT.709 limited 10 bit works' if not zerr else zerr,
            '' if not zerr else 'install an ffmpeg build with libzimg (zscale); only --draft encodes work without it')
        nv = 'hevc_nvenc' in enc
        nerr = ff_test(src + ['-vf', chain if not zerr else 'format=yuv420p10le', '-c:v', 'hevc_nvenc', '-preset', 'p5'] + null) if nv \
            else 'encoder hevc_nvenc is not in this ffmpeg'
        add('ok' if not nerr else 'warn', 'encoder hevc_nvenc', 'test encode ok (the default codec)' if not nerr else nerr,
            '' if not nerr else 'use --codec x265 (no NVIDIA encoder here: needs an NVIDIA card, a current driver and an ffmpeg build with NVENC)')
        xerr = ff_test(src + ['-vf', chain if not zerr else 'format=yuv420p10le', '-c:v', 'libx265', '-preset', 'ultrafast',
                              '-x265-params', 'log-level=error:pools=2'] + null) if 'libx265' in enc else 'encoder libx265 is not in this ffmpeg'
        add('ok' if not xerr else ('fail' if nerr else 'warn'), 'encoder libx265', 'test encode ok (--codec x265)' if not xerr else xerr,
            '' if not xerr else 'install an ffmpeg build with libx265' + ('' if nerr else ' (only needed for --codec x265)'))
        aerr = '' if 'aac' in enc else 'encoder aac is not in this ffmpeg'
        add('ok' if not aerr else 'warn', 'encoder aac', 'present (--audio record)' if not aerr else aerr,
            '' if not aerr else '--audio record needs it; --audio-codec flac writes MKV instead')
        cuda = 'scale_cuda' in flt and 'hwdownload' in flt
        add('ok' if cuda else 'warn', 'filter scale_cuda', 'present (GPU decode of the camera videos)' if cuda else 'not in this ffmpeg',
            '' if cuda else 'the camera videos are decoded on the CPU (same as --cpu)')
    add('ok' if HAVE_NUMBA else 'warn', 'numba', 'present (fast motion blur and saturation)' if HAVE_NUMBA else 'not installed',
        '' if HAVE_NUMBA else 'pip install numba (optional: the numpy path is several times slower with sub-step blur)')
    font = Labels._font('Oswald')
    add('ok' if font else 'warn', 'label font', str(font) if font else f'none of {FONT_FILES["oswald"]}, {FONT_FILES["bahnschrift"]}',
        '' if font else 'labels use the small built-in font; set compose.labels.font to a .ttf path or use --no-labels')
    take = None
    if getattr(args, 'take', None):
        try:
            take = Take(Path(args.take), getattr(args, 'rate', None), getattr(args, 'group', None), parse_events(getattr(args, 'event', None)))
            add('ok' if len(take.base) else 'fail', 'take', f'{take.root}: {take.kind}, {len(take.cams)} camera(s), {len(take.base)} output '
                f'frames at {take.fps:g} fps', '' if len(take.base) else 'no complete output frame: check the camera folders and meta.csv')
            for w in take.warnings[:5]:
                add('warn', 'take', w)
        except TakeError as e:
            add('fail', 'take', str(e), 'give the take folder (the one with the camera folders or <cam>.mkv files)')
    if take is not None and len(take.base):
        mkvs = [c.video for c in take.cams.values() if c.ext == 'mkv' and c.video is not None]
        if mkvs and tools_ok:
            gpu = MT.cuda_ok(mkvs[0])
            add('ok' if gpu else 'warn', 'GPU decode', f'{mkvs[0].name}: ' + ('NVDEC test ok' if gpu else 'NVDEC test failed'),
                '' if gpu else 'the compositor falls back to the CPU decode by itself (--cpu)')
        sizes = {}
        for c in take.cams:
            try:
                sizes[c] = take.frame_size(c)
            except Exception as e:
                add('fail', 'frame size', f'{c}: {type(e).__name__}: {e}', 'the first frame of this camera does not decode')
        store = FrameStore(take, cache_mb=1)
        ctx = Ctx(take, store, {}, args, Path('.'), sizes=sizes, src_size=next(iter(sizes.values()), (0, 0)))
        need, free = commit_need_gb(ctx), free_commit_gb()
        add('ok' if free >= need else 'fail', 'memory', f'{free:.1f} GB free commit memory, an encode of this take needs about {need:.1f} GB',
            '' if free >= need else 'close programs or lower --cache-mb / --threads / --jobs (--no-mem-check skips the check)')
        given = getattr(args, 'out', None)
        out, why = (Path(given).resolve(), '--out') if given else default_out(take.root, take.name, take.spec)
    else:
        free = free_commit_gb()
        add('ok' if free >= COMMIT_HEADROOM_GB else 'warn', 'memory', f'{free:.1f} GB free commit memory (give a take for the need of its encode)')
        out, why = (Path(args.out).resolve(), '--out') if getattr(args, 'out', None) else (None, '')
    if out is not None:
        werr = folder_writable(out)
        add('ok' if not werr else 'fail', 'output folder', f'{out} ({why})' + ('' if not werr else f': not writable ({werr})'),
            '' if not werr else 'pass --out DIR or set the MCAM_OUT environment variable to a folder you can write')
    pid, alive = lock_owner(lock_path())
    if alive and pid != os.getpid():
        add('warn', 'encode lock', f'an encode is running (pid {pid}, {lock_path()})', 'wait for it, or pass --no-lock')
    return rows


def cmd_doctor(args) -> tuple[int, dict]:
    rows = doctor_rows(args)
    fails = sum(1 for r in rows if r['level'] == 'fail')
    warns = sum(1 for r in rows if r['level'] == 'warn')
    for r in rows:
        print(f'{r["level"].upper():<5} {r["check"]:<20} {r["detail"]}' + (f'\n      -> {r["fix"]}' if r['fix'] else ''), flush=True)
    print(f'doctor: {fails} failed, {warns} warning(s), {len(rows) - fails - warns} ok', flush=True)
    code = EXIT_BAD if fails else EXIT_OK
    return code, {'ok': not fails, 'exit': code, 'command': 'doctor', 'failed': fails, 'warnings': warns, 'checks': rows}


COMMANDS = {'scan': cmd_scan, 'angles': cmd_angles, 'layout': cmd_layout, 'cut': cmd_cut, 'all': cmd_all, 'finals': cmd_finals,
            'director': cmd_director, 'autocut': cmd_autocut, 'grade': cmd_grade, 'review': cmd_review}
HEAVY = {'angles', 'layout', 'cut', 'all', 'finals'}


def parse_events(items: list[str] | None) -> dict:
    out = {}
    for it in items or []:
        k, _, v = it.partition('=')
        if not v:
            raise SystemExit(f'--event {it}: use name=seconds')
        out[k.strip()] = float(v)
    return out


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('command', choices=sorted(set(COMMANDS) | {'doctor'}))
    p.add_argument('take', nargs='?', help='take folder (optional for doctor)')
    p.add_argument('--compose', help='compose JSON file (else <take>/spec.json "compose")')
    p.add_argument('--out', help='output folder, anywhere (default: see the text above); director: a .json file')
    p.add_argument('--fps', type=float)
    p.add_argument('--range', help='t0:t1 game seconds or @event refs')
    p.add_argument('--frames', type=int, help='limit output frames (tests)')
    p.add_argument('--blur', choices=['linear', 'gamma'])
    p.add_argument('--shutter', type=float, help='warp-mode shutter angle')
    p.add_argument('--grade', help='grade JSON file')
    p.add_argument('--no-grade', action='store_true')
    p.add_argument('--auto', help='comma list of cameras with auto exposure')
    p.add_argument('--bits', type=int, choices=[8, 10])
    p.add_argument('--codec', choices=['x265', 'nvenc'], help='nvenc (default): hevc_nvenc Main10 p7 cq 18; x265: libx265 Main10 slow crf 18 (slow, only on request)')
    p.add_argument('--draft', action='store_true', help='hevc_nvenc, half size')
    p.add_argument('--scale', type=float)
    p.add_argument('--crf', type=float)
    p.add_argument('--preset')
    p.add_argument('--threads', type=int, default=0)
    p.add_argument('--jobs', type=int, default=0, help='finals: videos encoded at once (default 1; more is rarely faster, the '
                                                       'frame rendering already uses --threads)')
    p.add_argument('--cache-mb', type=int, default=1536)
    p.add_argument('--rate', type=float, help='v1 fallback steps per game second (30)')
    p.add_argument('--group', type=int, help='v1 fallback steps per output frame (1)')
    p.add_argument('--event', action='append', help='name=seconds event time (v1 takes, overrides)')
    p.add_argument('--cams')
    p.add_argument('--speed', choices=['meta', 'realtime'])
    p.add_argument('--layout')
    p.add_argument('--edit')
    p.add_argument('--hold', help='autocut hold range min:max seconds')
    p.add_argument('--dissolve', type=float)
    p.add_argument('--stills', type=int, default=0, help='also save N PNG stills per video into <out>/stills')
    p.add_argument('--outputs', help='review: all (default), angles, layouts, angle_<cam> or layout_<name>')
    p.add_argument('--events', help='review: comma list of event names, fnmatch patterns or @event+s (default compose.review.events, else all)')
    p.add_argument('--around', type=float, help='review: seconds around each event that get dense frames (default 1.0)')
    p.add_argument('--near', type=float, help='review: frame spacing near events (default 0.5 s)')
    p.add_argument('--every', type=float, help='review: frame spacing elsewhere (default 2.0 s)')
    p.add_argument('--labels', dest='labels', action='store_true', default=None)
    p.add_argument('--no-labels', dest='labels', action='store_false')
    p.add_argument('--audio', choices=['none', 'record'], default='none',
                   help='record: mux the replay take audio_track.wav (refused unless record.json audio.usable is true)')
    p.add_argument('--audio-codec', choices=['aac', 'flac'], default='aac', help='aac: AAC-LC 320 kb/s in MP4; flac: FLAC in MKV')
    p.add_argument('--force', action='store_true', help='overwrite foreign files in the output folder, and (as before) ignore '
                                                        'the encode lock and the memory check')
    p.add_argument('--no-lock', action='store_true', help='do not take or respect the one-encode-at-a-time lock')
    p.add_argument('--no-mem-check', action='store_true', help='skip the free commit memory check')
    p.add_argument('--min-commit-gb', type=float, help='free commit memory an encode needs (default: from the frame size, '
                                                       '--cache-mb, --threads and --jobs)')
    p.add_argument('--resume', action='store_true', help='skip a video that exists, passes its check and was made from the same '
                                                         'inputs (compose_log.json); the rest is encoded')
    p.add_argument('--json', action='store_true', help='one JSON object on stdout at the end (ok, exit, outputs, errors, '
                                                       'warnings, written); progress goes to stderr')
    p.add_argument('--cpu', action='store_true', help='old CPU path: decode the camera videos on the CPU and shrink cells with INTER_AREA (fallback)')
    return p


def write_log(ctx: Ctx, command: str, argv: list, res) -> None:
    log = ctx.out / LOG_NAME
    hist = read_log(log, set_aside=True)
    hist.append({'time': time.strftime('%Y-%m-%d %H:%M:%S'), 'command': command, 'argv': list(argv), 'result': res,
                 'grades': {c: g.describe() for c, g in ctx.grades.items()}})
    tmp = log.with_name(log.name + '.tmp')
    tmp.write_text(json.dumps(hist[-LOG_KEEP:], indent=1, default=str), encoding='utf-8')
    os.replace(tmp, log)
    wrote(log)


def run_command(args, argv: list, state: dict) -> int:
    """Runs one command; returns the exit code. state gets 'take', 'out' and 'result' for the --json summary."""
    if args.command == 'doctor':
        code, state['doctor'] = cmd_doctor(args)
        return code
    if not args.take:
        raise SystemExit(f'usage: mcam_compose.py {args.command} <take> [options] (the take folder is missing)')
    try:
        take = Take(Path(args.take), args.rate, args.group, parse_events(args.event))
    except TakeError as e:
        raise SystemExit(f'error: {e}') from None
    state['take'] = take
    if not len(take.base):
        raise SystemExit('error: take has no complete output frames')
    try:
        ctx = make_ctx(take, args)
    except (OSError, ValueError, KeyError, TypeError) as e:
        raise SystemExit(f'cannot read the compose settings: {type(e).__name__}: {e}') from None
    state['out'] = ctx.out
    try:
        heavy = args.command in HEAVY
        if heavy and args.audio == 'record':
            ctx.audio = load_audio(ctx)
            check_audio_plan(ctx, args.command)
        if heavy:
            print(f'decode: {"GPU (NVDEC + scale_cuda)" if ctx.store.gpu else "CPU"}', flush=True)
            need = args.min_commit_gb if args.min_commit_gb is not None else commit_need_gb(ctx)
            safety_checks(args.force, need, not args.no_mem_check)
            with EncodeLock(args.force, not args.no_lock):
                res = COMMANDS[args.command](ctx)
        else:
            res = COMMANDS[args.command](ctx)
        state['result'] = res
        if heavy:
            write_log(ctx, args.command, argv, res)
            bad = [it for it in result_items(res) if it.get('verify', 'ok') != 'ok']
            if bad:
                for it in bad:
                    state['errors'].append(f'{Path(it["file"]).name}: ' + '; '.join(map(str, it['verify'] if isinstance(it['verify'], list) else [it['verify']])))
                print(f'error: {len(bad)} output(s) failed the check: ' + ', '.join(Path(it['file']).name for it in bad), file=sys.stderr)
                return EXIT_BAD
        return EXIT_OK
    finally:
        ctx.store.close()


def main(argv: list[str] | None = None) -> int:
    FF.utf8_stdio()
    argv = list(sys.argv[1:] if argv is None else argv)
    args = build_parser().parse_args(argv)
    js = FF.JsonMode(args.json)
    del WRITTEN[:]
    del NOTES[:]
    STOP.clear()
    state: dict = {'errors': []}

    def fail(code: int, text: str, prefix: str = 'error: ') -> int:
        state['errors'].append(text)
        print(prefix + text, file=sys.stderr, flush=True)
        return code

    try:
        code = run_command(args, argv, state)
    except Refused as e:
        code = fail(EXIT_REFUSED, str(e.code), '')
    except SystemExit as e:
        if not isinstance(e.code, str):
            raise
        code = fail(EXIT_INPUT, e.code, '')
    except (FF.FfError, TakeError, MA.AudioError) as e:
        code = fail(EXIT_BAD, str(e))
    except KeyboardInterrupt:
        code = fail(EXIT_BAD, 'interrupted; the encoder was stopped and its partial file removed')
    except (OSError, RuntimeError) as e:
        code = fail(EXIT_BAD, f'{type(e).__name__}: {e}')
    if 'doctor' in state:
        js.finish(state['doctor'])
        return code
    take = state.get('take')
    js.finish({'ok': code == EXIT_OK, 'exit': code, 'command': args.command, 'take': str(take.root) if take is not None else args.take,
               'out': str(state['out']) if state.get('out') else None,
               'outputs': [{'file': it['file'], 'frames': it.get('frames'), 'verify': it.get('verify'), 'resumed': bool(it.get('resumed'))}
                           for it in result_items(state.get('result'))],
               'errors': state['errors'], 'warnings': (list(take.warnings) if take is not None else []) + list(NOTES),
               'written': list(dict.fromkeys(WRITTEN))})
    return code


if __name__ == '__main__':
    sys.exit(main())
