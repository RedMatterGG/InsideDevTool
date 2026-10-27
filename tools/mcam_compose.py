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
Usage: python -I tools/mcam_compose.py <scan|angles|layout|cut|all|finals|director|autocut|grade|review> <take> [options]"""
from __future__ import annotations

import argparse
import ctypes
import fnmatch
import json
import math
import os
import subprocess
import sys
import threading
import time
from collections import deque
from concurrent.futures import ProcessPoolExecutor, ThreadPoolExecutor
from dataclasses import dataclass, field, replace
from functools import partial
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mcam_take as MT
from mcam_take import FrameStore, OutFrame, Take, TakeError, Timeline, smoothstep

try:
    from numba import njit
    HAVE_NUMBA = True
except Exception:
    HAVE_NUMBA = False

TOOLS = Path(__file__).resolve().parent
FONTS = TOOLS / 'fonts'
FONT_FILES = {'oswald': FONTS / 'Oswald-Regular.ttf', 'bahnschrift': Path('C:/Windows/Fonts/bahnschrift.ttf'),
              'mono': Path('C:/Windows/Fonts/consola.ttf')}
MOVING_MODES = {'follow', 'keys', 'rail', 'orbit', 'gamelike', 'frame', 'director', 'gamerel'}
LOCK_PATH = MT.OUT_ROOT / '.encode.lock'
MIN_COMMIT_GB = 10.0
LIN8 = ((np.arange(256, dtype=np.float64) / 255.0) ** 2.2).astype(np.float32)

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
    src_size: tuple = (2560, 1440)
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


def resolve_out(take: Take, args) -> Path:
    out = Path(args.out).resolve() if getattr(args, 'out', None) and not str(args.out).lower().endswith('.json') else (MT.OUT_ROOT / safe_name(take.name)).resolve()
    root = MT.OUT_ROOT.resolve()
    if root not in out.parents and out != root and not getattr(args, 'force', False):
        raise SystemExit(f'output {out} is outside {root}; pass --force to write there')
    out.mkdir(parents=True, exist_ok=True)
    return out


def safe_name(s: str) -> str:
    return ''.join(ch if ch.isalnum() or ch in '-_' else '_' for ch in s) or 'take'


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


def even(v: float) -> int:
    return int(2 * round(v / 2.0))


def default_layout(take: Take) -> dict:
    cams = [c for c in take.camera_names() if take.cams[c].mode != 'director']
    band = 'game' if 'game' in cams else cams[0]
    rest = [c for c in cams if c != band][:3]
    w, h = take.screen()
    ch = even(w * 1080 / 2560)
    rows = [[{'cam': band, 'crop': [0, even((h - ch) / 2), w, ch]}]]
    if rest:
        rows.append([{'cam': c} for c in rest])
    return {'name': 'main', 'width': 2560, 'gap': 4, 'rows': rows}


def layout_geometry(lay: dict, sizes: dict, default_size: tuple) -> tuple[int, int, list[Cell]]:
    W = even(int(lay.get('width', 2560)))
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
                band_h = PRESETS[preset][0]
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
                h = PRESETS[preset][2]
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
        if s.get('cam') not in take.cams and s.get('cam') != 'cut':
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
        raise SystemExit(f'--audio record refused: {take.root} is not a replay take ({kind}); v2 takes carry no sound '
                         f'(REPLAY_DESIGN 6.5)')
    st = take.audio_state()
    if not st['ok']:
        raise SystemExit(f'--audio record refused: {st["verdict"]} [record {take.record_dir()}]')
    try:
        wav = MT.read_wav(Path(st['track']))
    except TakeError as e:
        raise SystemExit(f'--audio record refused: {e}')
    if wav.problems:
        raise SystemExit(f'--audio record refused: {wav.path.name}: ' + '; '.join(wav.problems))
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
        raise SystemExit(f'--audio record refused for {what}: it uses a {tl.mode} timeline; audio_track.wav follows the meta '
                         f'timeline only (drop edit speed keys or --speed realtime)')
    if abs(tl.fps - ctx.audio.fps) > 1e-6:
        raise SystemExit(f'--audio record refused for {what}: {tl.fps:g} fps differs from the take timeline {ctx.audio.fps:g} fps')


def check_audio_plan(ctx: Ctx, command: str) -> None:
    if command in ('angles', 'all'):
        require_meta_timeline(ctx, angle_timeline(ctx), 'angles')
    uses_cut = any(any(c.get('cam') == 'cut' for row in lay.get('rows') or [] for c in row if isinstance(c, dict))
                   for lay in layouts_of(ctx) if isinstance(lay, dict))
    if command in ('cut', 'all') or (command == 'layout' and uses_cut):
        edit = current_edit(ctx, required=False)
        if edit and edit.get('speed'):
            raise SystemExit(f'--audio record refused: edit "{edit.get("name", "cut")}" has speed keys (warp timeline); '
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
    pos = np.arange(total, dtype=np.int64)
    deltas = sorted({d for m in mix for d in m})
    missing = 0
    for d in deltas:
        w = np.array([m.get(d, 0.0) for m in mix], np.float64)
        if not np.any(w > 1e-6):
            continue
        g = np.interp(pos.astype(np.float64), centers, w).astype(np.float32)
        src = pos + int(round(d * sr / fps))
        need = g > 1e-4
        ok = (src >= 0) & (src < len(track.data))
        missing += int(np.count_nonzero(need & ~ok))
        sel = need & ok
        out[sel] += track.data[src[sel]] * g[sel, None]
    if missing > sr / fps:
        raise SystemExit(f'--audio record refused: {missing} output samples ({missing / sr:.3f} s) fall outside '
                         f'{track.path.name} ({len(track.data) / sr:.3f} s); re-run mcam_audio.py track')
    peak = float(np.max(np.abs(out))) if len(out) else 0.0
    np.clip(out, -1.0, 1.0, out=out)
    return out, {'source': str(track.path), 'codec': track.codec, 'rate': sr, 'channels': track.channels, 'samples': total,
                 'seconds': round(total / sr, 6), 'streams': len(deltas), 'padded_samples': missing,
                 'peak_dbfs': round(20 * math.log10(peak), 2) if peak > 1e-10 else -200.0}


def mux_cmd(video: Path, wav: Path, out: Path, codec: str) -> list:
    cmd = [MT.FFMPEG, '-hide_banner', '-loglevel', 'error', '-y', '-i', str(video), '-i', str(wav), '-map', '0:v:0', '-map',
           '1:a:0', '-c:v', 'copy']
    if codec == 'flac':
        return cmd + ['-c:a', 'flac', '-sample_fmt', 's16', '-ar', str(AUDIO_RATE), str(out)]
    return cmd + ['-c:a', 'aac', '-profile:a', 'aac_low', '-b:a', '320k', '-ar', str(AUDIO_RATE), '-tag:v', 'hvc1',
                  '-movflags', '+faststart', str(out)]


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


def ffmpeg_cmd(cfg: EncodeCfg, W: int, H: int, fps: float, out: Path) -> tuple[list, int, int]:
    W2, H2 = (even(W * cfg.scale), even(H * cfg.scale)) if cfg.scale != 1.0 else (W, H)
    pix = 'yuv420p10le' if cfg.bits == 10 else 'yuv420p'
    tags = 'setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv'
    head = [MT.FFMPEG, '-hide_banner', '-loglevel', 'error', '-y', '-f', 'rawvideo', '-pix_fmt', 'rgb48le', '-s', f'{W}x{H}',
            '-framerate', f'{fps:g}', '-i', '-', '-an']
    if cfg.codec == 'x265':
        size = f'w={W2}:h={H2}:filter=spline36:' if (W2, H2) != (W, H) else ''
        vf = f'zscale={size}matrix=709:range=limited:dither=error_diffusion,format={pix},{tags}'
        x265 = 'log-level=error:aq-mode=3:colorprim=bt709:transfer=bt709:colormatrix=bt709:range=limited'
        codec = ['-c:v', 'libx265', '-preset', cfg.preset, '-crf', f'{cfg.crf:g}', '-x265-params', x265]
        if cfg.bits == 8:
            codec += ['-profile:v', 'main']
    elif cfg.draft:
        vf = f'scale=w={W2}:h={H2}:flags=area:out_color_matrix=bt709:out_range=tv:threads=8,format={pix},{tags}'
        codec = ['-c:v', 'hevc_nvenc', '-preset', cfg.nvpreset, '-rc', 'vbr', '-cq', f'{cfg.cq:g}', '-b:v', '0']
    else:
        size = f'w={W2}:h={H2}:filter=spline36:' if (W2, H2) != (W, H) else ''
        vf = f'zscale={size}matrix=709:range=limited:dither=error_diffusion,format={pix},{tags}'
        codec = ['-c:v', 'hevc_nvenc', '-preset', cfg.nvpreset, '-tune', 'hq', '-profile:v', 'main10' if cfg.bits == 10 else 'main',
                 '-rc', 'vbr', '-cq', f'{cfg.cq:g}', '-b:v', '0', '-spatial-aq', '1', '-temporal-aq', '1', '-aq-strength', '8',
                 '-bf', '3', '-b_ref_mode', 'middle', '-rc-lookahead', '32']
    return head + ['-vf', vf] + codec + ['-tag:v', 'hvc1', '-movflags', '+faststart', str(out)], W2, H2


def ffprobe(path: Path, count: bool = False) -> dict:
    p = subprocess.run([MT.FFPROBE, '-v', 'error', *(['-count_packets'] if count else []), '-show_entries',
                        'stream=index,codec_type,codec_name,codec_tag_string,profile,pix_fmt,width,height,nb_frames,'
                        + ('nb_read_packets,' if count else '') + 'color_space,color_transfer,color_primaries,color_range,'
                        'r_frame_rate,sample_rate,channels,duration:stream_tags=DURATION:format=duration,size', '-of', 'json',
                        str(path)], capture_output=True, text=True)
    if p.returncode != 0:
        return {'error': p.stderr.strip()}
    return json.loads(p.stdout or '{}')


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


class EncodeLock:
    def __init__(self, force: bool):
        self.force = force
        self.held = False

    def __enter__(self):
        MT.OUT_ROOT.mkdir(parents=True, exist_ok=True)
        for _ in range(2):
            try:
                fd = os.open(str(LOCK_PATH), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                os.write(fd, str(os.getpid()).encode())
                os.close(fd)
                self.held = True
                return self
            except FileExistsError:
                try:
                    pid = int(LOCK_PATH.read_text().strip() or 0)
                except (OSError, ValueError):
                    pid = 0
                if pid and pid_alive(pid) and not self.force:
                    raise SystemExit(f'another encode is running (pid {pid}, {LOCK_PATH}); one heavy encode at a time (--force to ignore)')
                try:
                    LOCK_PATH.unlink()
                except OSError:
                    pass
        if not self.force:
            raise SystemExit(f'cannot take the encode lock {LOCK_PATH}')
        return self

    def __exit__(self, *exc):
        if self.held:
            try:
                LOCK_PATH.unlink()
            except OSError:
                pass


def pid_alive(pid: int) -> bool:
    if pid == os.getpid():
        return True
    p = subprocess.run(['tasklist', '/FI', f'PID eq {pid}', '/NH'], capture_output=True, text=True)
    return str(pid) in p.stdout


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


def safety_checks(force: bool) -> None:
    gb = free_commit_gb()
    if gb < MIN_COMMIT_GB and not force:
        raise SystemExit(f'only {gb:.1f} GB free commit (< {MIN_COMMIT_GB:.0f} GB); close something or pass --force')
    try:
        p = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq INSIDE.exe', '/NH'], capture_output=True, text=True)
        if 'INSIDE.exe' in p.stdout:
            print('warning: INSIDE.exe is running; encoding competes with the game for CPU/GPU', flush=True)
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
    tmp = out.with_name(out.stem + '.partial.mp4')
    cmd, W2, H2 = ffmpeg_cmd(cfg, W, H, fps, tmp)
    still_set = set(np.linspace(0, n - 1, stills).round().astype(int).tolist()) if stills > 0 and n > 0 else set()
    still_dir = ctx.out / 'stills'
    workers = threads_of(ctx.args)
    t0 = time.time()
    print(f'{title}: {n} frames {W}x{H} -> {out.name} ({cfg.codec}{" draft" if cfg.draft else ""}, {W2}x{H2}, {fps:g} fps)', flush=True)
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stderr=subprocess.PIPE)
    err: list[bytes] = []
    drain = threading.Thread(target=lambda: err.append(proc.stderr.read()), daemon=True)
    drain.start()
    ok = False
    try:
        with ThreadPoolExecutor(workers) as pool:
            q: deque = deque()
            nxt = 0
            last = t0
            for j in range(n):
                while nxt < n and len(q) < workers * 2:
                    q.append(pool.submit(make_frame, nxt))
                    nxt += 1
                img = q.popleft().result()
                if img.shape != (H, W, 3) or img.dtype != np.uint16:
                    raise RuntimeError(f'frame {j}: got {img.shape} {img.dtype}, want {(H, W, 3)} uint16')
                proc.stdin.write(np.ascontiguousarray(img).data)
                if j in still_set:
                    still_dir.mkdir(parents=True, exist_ok=True)
                    save_png8(img, still_dir / f'{out.stem}_{j:05d}.png', max_w=1600)
                now = time.time()
                if now - last > 5 or j == n - 1:
                    last = now
                    print(f'  {title}: {j + 1}/{n} frames, {(j + 1) / max(now - t0, 1e-6):.1f} fps', flush=True)
        proc.stdin.close()
        code = proc.wait()
        drain.join(5)
        if code != 0:
            raise RuntimeError(f'ffmpeg exit {code}: {b"".join(err).decode(errors="replace")[-800:]}')
        ok = True
    finally:
        if not ok:
            try:
                proc.kill()
            except OSError:
                pass
            try:
                tmp.unlink()
            except OSError:
                pass
    if sound is not None:
        wav = out.with_name(out.stem + '.partial.wav')
        mux = out.with_name(out.stem + '.mux.partial' + out.suffix)
        muxed = False
        try:
            MT.write_wav(wav, sound[0], ctx.audio.rate)
            p = subprocess.run(mux_cmd(tmp, wav, mux, ctx.audio.codec), capture_output=True, text=True)
            if p.returncode != 0:
                raise RuntimeError(f'ffmpeg mux exit {p.returncode}: {p.stderr[-800:]}')
            muxed = True
        finally:
            for f in (wav, tmp) if muxed else (wav, tmp, mux):
                try:
                    f.unlink()
                except OSError:
                    pass
        tmp = mux
    if out.exists():
        out.unlink()
    tmp.rename(out)
    codec = ctx.audio.codec if sound is not None else None
    probs = verify_video(out, n, W2, H2, cfg.bits, codec, fps)
    dt = time.time() - t0
    res = {'file': str(out), 'frames': n, 'size': [W2, H2], 'fps': fps, 'seconds': round(n / fps, 3), 'codec': cfg.codec,
           'encode_s': round(dt, 1), 'mb': round(out.stat().st_size / 2 ** 20, 2), 'verify': probs or 'ok'}
    if sound is not None:
        res['audio'] = sound[1]
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
            with ProcessPoolExecutor(len(procs)) as pool:
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
        res = list(pool.map(lambda j: run_job(ctx, j), jobs))
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
            fh.write('frame,output_t,game_t,why,file\n')
            for r in rows:
                fh.write(','.join(str(x) for x in r) + '\n')
        doc['outputs'][name] = {'size': [W, H], 'frames': len(rows), 'folder': str(folder)}
        print(f'review {name}: {len(rows)} full-resolution frames {W}x{H} -> {folder} ({time.time() - t0:.1f} s)', flush=True)
    (root / 'review.json').write_text(json.dumps(doc, indent=1), encoding='utf-8')
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
        lines = ['TITLE "mcam grade ' + cam + '"', 'LUT_1D_SIZE 1024'] + [f'{v:.6f} {v:.6f} {v:.6f}' for v in vals]
        (ctx.out / f'grade_{safe_name(cam)}.cube').write_text('\n'.join(lines) + '\n', encoding='ascii')
        xs = np.unique(np.concatenate([np.linspace(0, 1, 17), [g.floor]]))
        pts = ' '.join(f'{x:.4f}/{lut[int(round(x * 65535))] / 65535.0:.4f}' for x in xs)
        doc[cam]['ffmpeg_curves'] = f"curves=all='{pts}'"
        print(f'{cam:<14} floor {doc[cam]["floor_8bit"]:6.2f}/255  lift {g.spec.lift} k {g.spec.k} ev {g.spec.ev}'
              f'{"  auto ev " + str(doc[cam]["auto_ev"]) if g.spec.auto else ""}')
    (ctx.out / 'grade.json').write_text(json.dumps(doc, indent=1), encoding='utf-8')
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
    root = MT.OUT_ROOT.resolve()
    if root not in dest.resolve().parents and not getattr(ctx.args, 'force', False):
        raise SystemExit(f'{dest} is outside {root}; pass --force')
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_text(text + '\n', encoding='utf-8')
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
    print(json.dumps(e, indent=1))
    print(f'wrote {p}')
    return e


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
    p.add_argument('command', choices=sorted(COMMANDS))
    p.add_argument('take')
    p.add_argument('--compose', help='compose JSON file (else <take>/spec.json "compose")')
    p.add_argument('--out', help='output folder (default <repo>/out/<take>); director: a .json file')
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
    p.add_argument('--jobs', type=int, default=0, help='finals: videos encoded at once (default 1; 2 ran slower on the E2 take)')
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
    p.add_argument('--force', action='store_true')
    p.add_argument('--cpu', action='store_true', help='old CPU path: decode the camera videos on the CPU and shrink cells with INTER_AREA (fallback)')
    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        take = Take(Path(args.take), args.rate, args.group, parse_events(args.event))
    except TakeError as e:
        print(f'error: {e}', file=sys.stderr)
        return 2
    if not len(take.base):
        print('error: take has no complete output frames', file=sys.stderr)
        return 2
    ctx = make_ctx(take, args)
    try:
        if args.command in HEAVY and args.audio == 'record':
            ctx.audio = load_audio(ctx)
            check_audio_plan(ctx, args.command)
        if args.command in HEAVY:
            print(f'decode: {"GPU (NVDEC + scale_cuda)" if ctx.store.gpu else "CPU"}', flush=True)
            safety_checks(args.force)
            with EncodeLock(args.force):
                res = COMMANDS[args.command](ctx)
        else:
            res = COMMANDS[args.command](ctx)
        if args.command in HEAVY:
            log = ctx.out / 'compose_log.json'
            hist = json.loads(log.read_text(encoding='utf-8')) if log.is_file() else []
            hist.append({'time': time.strftime('%Y-%m-%d %H:%M:%S'), 'command': args.command, 'argv': sys.argv[1:], 'result': res,
                         'grades': {c: g.describe() for c, g in ctx.grades.items()}})
            log.write_text(json.dumps(hist, indent=1), encoding='utf-8')
    except TakeError as e:
        print(f'error: {e}', file=sys.stderr)
        return 1
    finally:
        ctx.store.close()
    return 0


if __name__ == '__main__':
    sys.exit(main())
