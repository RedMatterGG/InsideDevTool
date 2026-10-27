"""Record-pass audio tool (REPLAY_DESIGN 6.1-6.4, 8.3 AUDIO): fix (RIFF/data sizes), info (fmt, cue/labl markers), align (lag-0
marker fit p = a * real_time + b, slope check 0.5 % -> record.json audio), track (audio_track.wav on the meta.csv output
timeline: speed-1 pieces copied, other speeds stretched with pitch kept along the exact time map, wall-time gaps cut, 50 ms
crossfades; refuses unusable audio), stretch and check (RMS, peak, onset after event markers). Never plays audio (6.0 G7).
Exit codes: 0 ok, 1 check failed or audio not usable, 2 usage or input error, 3 refused."""
from __future__ import annotations

import argparse
import csv
import json
import math
import struct
import sys
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

SLOPE_LIMIT = 0.005
RESAMPLE_LIMIT = 0.001
RESIDUAL_MAX_MS = 50.0
GAP_TOL = 0.02
XFADE_HALF = 0.025
MUTE_FADE = 0.3
SPEED_ONE = 1e-3
WIN, HOP, TOL = 1024, 512, 256
AUDIO_FILE, SYNC_FILE, CAPTURE_FILE = 'audio.wav', 'audio_sync.csv', 'audio_capture.json'
RECORD_JSON, RECORD_META = 'record.json', 'record_meta.csv'
TRACK_FILE, TRACK_JSON = 'audio_track.wav', 'audio_track.json'
WINDOW_LABELS = ('wopen', 'wclose')


class AudioError(Exception):
    pass


class Refused(AudioError):
    pass


@dataclass
class Cue:
    id: int
    position: int
    offset: int
    label: str = ''

    @property
    def frame(self) -> int:
        return self.offset if self.offset or not self.position else self.position


@dataclass
class Wav:
    path: Path
    file_bytes: int = 0
    riff: bool = False
    riff_size: int = 0
    format: int = 0
    channels: int = 0
    rate: int = 0
    bits: int = 0
    block_align: int = 0
    data_offset: int = -1
    data_size: int = 0
    data_size_raw: int = 0
    riff_ok: bool = False
    data_ok: bool = False
    walk_ok: bool = False
    last_complete: int = 12
    cues: list[Cue] = field(default_factory=list)
    error: str | None = None

    @property
    def ok(self) -> bool:
        return self.riff_ok and self.data_ok and self.walk_ok and self.rate > 0 and self.data_offset >= 0

    @property
    def frames(self) -> int:
        return self.data_size // max(1, self.block_align)

    @property
    def seconds(self) -> float:
        return self.frames / self.rate if self.rate > 0 else 0.0

    def cue_frames(self, scale: int = 1) -> dict[str, list[int]]:
        out: dict[str, list[int]] = {}
        for c in sorted(self.cues, key=lambda c: (c.frame, c.id)):
            out.setdefault(c.label, []).append(c.frame // scale)
        return out


@dataclass
class SyncRow:
    marker: int
    label: str
    sample: int
    game_time: float
    real_time: float
    lag: int


@dataclass
class Capture:
    wav: Path
    sync: Path | None
    info: Path | None
    record_json: Path | None
    record_meta: Path | None
    align_out: Path


def fnum(v, default: float = math.nan) -> float:
    try:
        x = float(v)
        return x if math.isfinite(x) else default
    except (TypeError, ValueError):
        return default


def inum(v, default: int = 0) -> int:
    try:
        return int(float(v))
    except (TypeError, ValueError):
        return default


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec='seconds')


# ------------------------------------------------------------------ WAV

def read_wav(path: Path) -> Wav:
    path = Path(path)
    if not path.is_file():
        raise AudioError(f'{path}: not found')
    w = Wav(path=path, file_bytes=path.stat().st_size)
    size = w.file_bytes
    labels: dict[int, str] = {}
    with path.open('rb') as fh:
        head = fh.read(12)
        if len(head) < 12 or head[:4] != b'RIFF' or head[8:12] != b'WAVE':
            w.error = 'not a RIFF/WAVE file'
            return w
        w.riff = True
        w.riff_size = struct.unpack('<I', head[4:8])[0]
        w.riff_ok = w.riff_size != 0xFFFFFFFF and w.riff_size + 8 == size
        pos, broken = 12, False
        while pos + 8 <= size:
            fh.seek(pos)
            hdr = fh.read(8)
            cid, n = hdr[:4], struct.unpack('<I', hdr[4:8])[0]
            body = pos + 8
            if cid == b'fmt ' and n >= 16:
                b = fh.read(min(n, 40))
                w.format, w.channels, w.rate, _, w.block_align, w.bits = struct.unpack('<HHIIHH', b[:16])
                if w.format == 0xFFFE and len(b) >= 26:
                    w.format = struct.unpack('<H', b[24:26])[0]
            elif cid == b'data':
                w.data_offset, w.data_size_raw = body, n
                w.data_ok = n != 0xFFFFFFFF and body + n <= size
                w.data_size = n if w.data_ok else size - body
                if not w.data_ok:
                    broken = True
                    break
            elif cid == b'cue ' and n >= 4 and n != 0xFFFFFFFF:
                b = fh.read(n)
                cnt = struct.unpack('<I', b[:4])[0]
                for k in range(min(cnt, (len(b) - 4) // 24)):
                    cue_id, position, _, _, _, offset = struct.unpack('<II4sIII', b[4 + 24 * k:28 + 24 * k])
                    w.cues.append(Cue(cue_id, position, offset))
            elif cid == b'LIST' and n >= 4 and n != 0xFFFFFFFF:
                b = fh.read(n)
                if b[:4] == b'adtl':
                    i = 4
                    while i + 8 <= len(b):
                        sid, sn = b[i:i + 4], struct.unpack('<I', b[i + 4:i + 8])[0]
                        if sid == b'labl' and sn >= 4:
                            labels[struct.unpack('<I', b[i + 8:i + 12])[0]] = b[i + 12:i + 8 + sn].split(b'\0', 1)[0].decode('latin-1')
                        i += 8 + sn + (sn & 1)
            if n == 0xFFFFFFFF:
                broken = True
                break
            nxt = body + n + (n & 1)
            if nxt > size and not (nxt == size + 1 and body + n == size):
                broken = True
                break
            pos = min(nxt, size)
            w.last_complete = pos
        w.walk_ok = not broken and pos == size
    if w.block_align <= 0 and w.channels > 0:
        w.block_align = w.channels * max(1, w.bits // 8)
    for c in w.cues:
        c.label = labels.get(c.id, '')
    if w.rate <= 0:
        w.error = 'no fmt chunk'
    elif w.data_offset < 0:
        w.error = 'no data chunk'
    return w


def pcm(w: Wav) -> np.ndarray:
    if w.data_offset < 0 or w.channels <= 0:
        raise AudioError(f'{w.path}: no audio data')
    with w.path.open('rb') as fh:
        fh.seek(w.data_offset)
        raw = fh.read(w.frames * w.block_align)
    n = len(raw) // w.block_align
    raw = raw[:n * w.block_align]
    if w.format == 3 and w.bits == 32:
        a = np.frombuffer(raw, '<f4').astype(np.float32)
    elif w.format == 1 and w.bits == 16:
        a = np.frombuffer(raw, '<i2').astype(np.float32) / 32768.0
    elif w.format == 1 and w.bits == 32:
        a = (np.frombuffer(raw, '<i4').astype(np.float64) / 2147483648.0).astype(np.float32)
    elif w.format == 1 and w.bits == 24:
        b = np.frombuffer(raw, np.uint8).reshape(-1, 3).astype(np.int32)
        v = b[:, 0] | (b[:, 1] << 8) | (b[:, 2] << 16)
        a = (np.where(v >= 1 << 23, v - (1 << 24), v) / 8388608.0).astype(np.float32)
    elif w.format == 1 and w.bits == 8:
        a = (np.frombuffer(raw, np.uint8).astype(np.float32) - 128.0) / 128.0
    else:
        raise AudioError(f'{w.path}: unsupported sample format {w.format} / {w.bits} bit')
    return a.reshape(-1, w.channels)


def write_wav(path: Path, data: np.ndarray, rate: int) -> Path:
    a = np.asarray(data, np.float32)
    if a.ndim == 1:
        a = a[:, None]
    raw = np.clip(np.round(a * 32767.0), -32768, 32767).astype('<i2').tobytes()
    ch = a.shape[1]
    fmt = struct.pack('<HHIIHH', 1, ch, rate, rate * ch * 2, ch * 2, 16)
    body = b'fmt ' + struct.pack('<I', len(fmt)) + fmt + b'data' + struct.pack('<I', len(raw)) + raw + (b'\0' if len(raw) & 1 else b'')
    path = Path(path)
    tmp = path.with_name(path.name + '.tmp')
    tmp.write_bytes(b'RIFF' + struct.pack('<I', 4 + len(body)) + b'WAVE' + body)
    tmp.replace(path)
    return path


def fix_wav(path: Path, check_only: bool = False) -> dict:
    w = read_wav(path)
    out = {'file': str(path), 'okBefore': w.ok, 'riffSize': w.riff_size, 'dataSizeRaw': w.data_size_raw, 'fileBytes': w.file_bytes}
    if w.ok:
        out.update({'fixed': False, 'ok': True})
        return out
    if not w.riff or w.rate <= 0 or w.data_offset < 0:
        raise AudioError(f'{path}: cannot fix ({w.error or "no fmt/data chunk"})')
    if not w.data_ok:
        data = (w.file_bytes - w.data_offset) // max(1, w.block_align) * max(1, w.block_align)
        end = w.data_offset + data
        new_len = end + (data & 1)
    else:
        data = w.data_size_raw
        new_len = w.last_complete if not w.walk_ok else w.file_bytes
    out.update({'dataSize': data, 'newBytes': new_len, 'truncatedBytes': w.file_bytes - new_len if new_len <= w.file_bytes else 0})
    if check_only:
        out.update({'fixed': False, 'ok': False, 'wouldFix': True})
        return out
    with Path(path).open('r+b') as fh:
        if new_len < w.file_bytes:
            fh.truncate(new_len)
        elif new_len > w.file_bytes:
            fh.seek(0, 2)
            fh.write(b'\0' * (new_len - w.file_bytes))
        fh.seek(4)
        fh.write(struct.pack('<I', new_len - 8))
        fh.seek(w.data_offset - 4)
        fh.write(struct.pack('<I', data))
    after = read_wav(path)
    out.update({'fixed': True, 'ok': after.ok, 'seconds': round(after.seconds, 6)})
    if not after.ok:
        raise AudioError(f'{path}: still not a consistent RIFF file after the fix')
    return out


# ------------------------------------------------------------------ record folder

def locate(path: Path) -> Capture:
    path = Path(path).resolve()
    if path.is_dir():
        d = path
        wav = d / AUDIO_FILE
        sync, info = d / SYNC_FILE, d / CAPTURE_FILE
        out = d / 'audio_align.json'
    else:
        d = path.parent
        wav = path
        stem = path.stem
        sync = d / f'{stem}_sync.csv' if (d / f'{stem}_sync.csv').is_file() else d / SYNC_FILE
        info = d / f'{stem}_capture.json' if (d / f'{stem}_capture.json').is_file() else d / CAPTURE_FILE
        out = d / ('audio_align.json' if path.name == AUDIO_FILE else f'{stem}_align.json')
    rj, rm = d / RECORD_JSON, d / RECORD_META
    return Capture(wav, sync if sync.is_file() else None, info if info.is_file() else None,
                   rj if rj.is_file() and wav.name == AUDIO_FILE else None, rm if rm.is_file() else None, out)


def read_json(path: Path | None) -> dict | None:
    if path is None or not Path(path).is_file():
        return None
    try:
        v = json.loads(Path(path).read_text(encoding='utf-8'))
    except (OSError, ValueError) as e:
        raise AudioError(f'{path}: {e}') from e
    return v if isinstance(v, dict) else None


def read_sync(path: Path | None) -> list[SyncRow]:
    if path is None:
        return []
    rows = []
    with Path(path).open(newline='', encoding='utf-8') as fh:
        for r in csv.DictReader(fh):
            rows.append(SyncRow(inum(r.get('marker'), len(rows)), (r.get('label') or '').strip(), inum(r.get('sample'), -1),
                                fnum(r.get('game_time')), fnum(r.get('real_time')), inum(r.get('lag'), 0)))
    return rows


def read_csv_columns(path: Path, *names: str) -> list[np.ndarray]:
    cols: list[list[float]] = [[] for _ in names]
    with Path(path).open(newline='', encoding='utf-8') as fh:
        for r in csv.DictReader(fh):
            for i, n in enumerate(names):
                cols[i].append(fnum(r.get(n)))
    return [np.array(c, np.float64) for c in cols]


class Clock:
    """Record game time -> wall seconds since t0Real with wall-time gaps removed (REPLAY_DESIGN 6.3)."""

    def __init__(self, game: np.ndarray, real: np.ndarray):
        ok = np.isfinite(game) & np.isfinite(real)
        g, r = game[ok], real[ok]
        order = np.argsort(g, kind='stable')
        g, r = g[order], r[order]
        keep = np.concatenate(([True], np.diff(g) > 1e-9)) if len(g) else np.zeros(0, bool)
        self.g, self.r = g[keep], r[keep]
        if len(self.g) == 0:
            raise AudioError('no game/real time pairs for the record clock')
        excess = np.diff(self.r) - np.diff(self.g) if len(self.g) > 1 else np.zeros(0)
        self.gap = excess > GAP_TOL
        self.gaps = [{'gameTime': round(float(self.g[k + 1]), 6), 'wallFrom': round(float(self.r[k] + (self.g[k + 1] - self.g[k])), 6),
                      'wallTo': round(float(self.r[k + 1]), 6), 'seconds': round(float(excess[k]), 6)} for k in np.nonzero(self.gap)[0]]
        self.points = [float(self.g[k + 1]) for k in np.nonzero(self.gap)[0]]

    def wall(self, t: float, left: bool = False) -> float:
        g, r = self.g, self.r
        if t <= g[0]:
            return float(r[0] + (t - g[0]))
        if t >= g[-1] and not (left and t == g[-1] and len(g) > 1):
            return float(r[-1] + (t - g[-1]))
        k = int(np.searchsorted(g, t, 'left' if left else 'right')) - 1
        k = max(0, min(k, len(g) - 2))
        dg, dr = g[k + 1] - g[k], r[k + 1] - r[k]
        if self.gap[k]:
            return float(r[k] + (t - g[k]))
        return float(r[k] + (t - g[k]) * dr / dg)


def record_clock(cap: Capture, fallback: tuple[np.ndarray, np.ndarray] | None = None) -> Clock | None:
    if cap.record_meta is not None:
        g, r = read_csv_columns(cap.record_meta, 'game_time', 'real_time')
        if len(g):
            return Clock(g, r)
    if fallback is not None:
        return Clock(*fallback)
    return None


def sample_reals(cap: Capture) -> dict[int, float]:
    if cap.record_meta is None:
        return {}
    s, r = read_csv_columns(cap.record_meta, 'sample', 'real_time')
    return {int(a): float(b) for a, b in zip(s, r) if math.isfinite(a) and math.isfinite(b)}


# ------------------------------------------------------------------ align (6.2)

def fit_rows(w: Wav, rows: list[SyncRow], scale: int = 1) -> dict:
    by_label = w.cue_frames(scale)
    used: dict[str, int] = {}
    matched: list[tuple[SyncRow, int]] = []
    unmatched: list[str] = []
    for r in rows:
        lst = by_label.get(r.label, [])
        k = used.get(r.label, 0)
        if k < len(lst):
            matched.append((r, lst[k]))
            used[r.label] = k + 1
        else:
            unmatched.append(r.label)
    pts = [(r.real_time, p) for r, p in matched if r.lag == 0 and r.sample >= 0 and math.isfinite(r.real_time)]
    out = {'matched': matched, 'unmatched': unmatched, 'points': len(pts)}
    if len(pts) < 2:
        out['reason'] = f'{len(pts)} lag-0 marker(s) matched between {SYNC_FILE} and the WAV cue/labl chunks (2 needed)'
        return out
    x = np.array([p[0] for p in pts], np.float64)
    y = np.array([p[1] for p in pts], np.float64)
    if np.ptp(x) <= 1e-6:
        out['reason'] = 'the lag-0 markers share one real_time'
        return out
    a, b = np.polyfit(x, y, 1)
    res = y - (a * x + b)
    out.update({'a': float(a), 'b': float(b), 'res': res})
    return out


def align(path: Path, write: bool = True) -> dict:
    cap = locate(path)
    info = read_json(cap.info)
    rj = read_json(cap.record_json)
    out: dict = {'capture': 'record', 'started': True, 'usable': False, 'reason': None, 'file': str(cap.wav)}
    if not cap.wav.is_file():
        raise AudioError(f'{cap.wav}: not found')
    w = read_wav(cap.wav)
    fixed = None
    if not w.ok:
        fixed = fix_wav(cap.wav)
        w = read_wav(cap.wav)
    rows = read_sync(cap.sync)
    reasons: list[str] = []
    if cap.sync is None:
        reasons.append(f'no {SYNC_FILE}')
    if info is not None and info.get('error'):
        reasons.append(f'capture error: {info.get("error")}')
    f = fit_rows(w, rows)
    byte_offsets = False
    if 'a' in f and w.block_align > 1 and abs(f['a'] / (w.rate * w.block_align) - 1.0) < 0.01:
        f = fit_rows(w, rows, w.block_align)
        byte_offsets = True
    out.update({'fmtRate': w.rate, 'channels': w.channels, 'bits': w.bits, 'wavSeconds': round(w.seconds, 6), 'wavBytes': w.file_bytes,
                'markersTotal': len(rows), 'cues': len(w.cues), 'unmatched': len(f['unmatched']), 'markers': f['points'],
                'fixed': bool(fixed and fixed.get('fixed')), 'byteOffsets': byte_offsets})
    if 'a' not in f:
        reasons.append(f['reason'])
    else:
        a, b, res = f['a'], f['b'], f['res']
        slope_err = a / w.rate - 1.0
        rms = float(np.sqrt(np.mean(res ** 2)) / w.rate * 1000.0)
        mx = float(np.max(np.abs(res)) / w.rate * 1000.0)
        out.update({'slope': round(a, 6), 'intercept': round(b, 6), 'slopeError': round(slope_err, 8), 'residualMs': round(rms, 4),
                    'residualMaxMs': round(mx, 4)})
        if abs(slope_err) >= SLOPE_LIMIT:
            reasons.append(f'fit slope {a:.1f} frames/s is {slope_err * 100:+.2f} % off the fmt rate {w.rate} (limit {SLOPE_LIMIT * 100:.1f} %)')
        if mx >= RESIDUAL_MAX_MS:
            reasons.append(f'marker residual up to {mx:.1f} ms (limit {RESIDUAL_MAX_MS:.0f} ms): markers do not follow one clock')
        reals = sample_reals(cap)
        events, window = [], {}
        for r, p in f['matched']:
            if r.label in WINDOW_LABELS:
                window[r.label] = {'wavSeconds': round(p / w.rate, 6), 'real': r.real_time}
                continue
            if r.lag == 0:
                continue
            labelled = reals.get(r.sample, math.nan)
            if not math.isfinite(labelled) and math.isfinite(r.real_time):
                labelled = r.real_time - r.lag / 60.0
            e = {'label': r.label, 'lag': r.lag, 'sample': r.sample, 'gameTime': r.game_time,
                 'offsetPostMs': round((p - (a * r.real_time + b)) / w.rate * 1000.0, 3) if math.isfinite(r.real_time) else None,
                 'offsetMs': round((p - (a * labelled + b)) / w.rate * 1000.0, 3) if math.isfinite(labelled) else None,
                 'expectedMs': round(r.lag * 1000.0 / 60.0, 3)}
            events.append(e)
        out['eventMarkers'] = events
        out['window'] = window
    if not w.ok:
        reasons.append('WAV header still inconsistent')
    out['usable'] = not reasons
    out['reason'] = '; '.join(reasons) if reasons else None
    out['captureError'] = info.get('error') if info else None
    out['alignedBy'] = 'mcam_audio.py align'
    out['alignedAt'] = now_iso()
    if write:
        write_alignment(cap, rj, out)
    return out


def write_alignment(cap: Capture, rj: dict | None, result: dict) -> Path:
    if cap.record_json is not None and rj is not None:
        au = rj.get('audio') if isinstance(rj.get('audio'), dict) else {}
        au = dict(au)
        au.update(result)
        rj['audio'] = au
        tmp = cap.record_json.with_name(cap.record_json.name + '.tmp')
        tmp.write_text(json.dumps(rj, indent=2) + '\n', encoding='utf-8')
        tmp.replace(cap.record_json)
        return cap.record_json
    cap.align_out.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    return cap.align_out


# ------------------------------------------------------------------ stretch (WSOLA along a time map)

def extrap(x: np.ndarray, xp: np.ndarray, fp: np.ndarray) -> np.ndarray:
    y = np.interp(x, xp, fp)
    if len(xp) >= 2:
        lo, hi = x < xp[0], x > xp[-1]
        y[lo] = fp[0] + (x[lo] - xp[0]) * (fp[1] - fp[0]) / (xp[1] - xp[0])
        y[hi] = fp[-1] + (x[hi] - xp[-1]) * (fp[-1] - fp[-2]) / (xp[-1] - xp[-2])
    return y


def wsola(src: np.ndarray, pos: np.ndarray, win: int = WIN, hop: int = HOP, tol: int = TOL) -> np.ndarray:
    n_out, ch = len(pos), src.shape[1]
    if n_out == 0:
        return np.zeros((0, ch), np.float32)
    pad = win + tol + 2
    lo = int(math.floor(np.min(pos))) - pad
    hi = int(math.ceil(np.max(pos))) + pad
    buf = np.zeros((hi - lo, ch), np.float32)
    a, b = max(lo, 0), min(hi, len(src))
    if b > a:
        buf[a - lo:b - lo] = src[a:b]
    mono = buf.mean(axis=1)
    sq = np.concatenate(([0.0], np.cumsum(mono.astype(np.float64) ** 2)))
    window = (0.5 - 0.5 * np.cos(2.0 * np.pi * np.arange(win) / win)).astype(np.float32)
    half = win // 2
    out = np.zeros((n_out + win, ch), np.float32)
    norm = np.zeros(n_out + win, np.float32)
    slope = (pos[-1] - pos[0]) / (n_out - 1) if n_out > 1 else 1.0
    prev = None
    for j in range(n_out // hop + 1):
        x = j * hop
        c = pos[x] if x < n_out else pos[-1] + (x - n_out + 1) * slope
        want = int(round(c)) - half - lo
        start = want
        if prev is not None:
            nat = prev + hop
            s0 = max(0, want - tol)
            s1 = min(len(mono) - win, want + tol)
            tmpl = mono[nat:nat + win]
            if s1 > s0 and len(tmpl) == win and float(np.dot(tmpl, tmpl)) > 1e-12:
                seg = mono[s0:s1 + win]
                corr = np.correlate(seg, tmpl, mode='valid')
                k = np.arange(s0, s1 + 1)
                energy = sq[k + win] - sq[k]
                score = corr / np.sqrt(energy + 1e-12)
                start = int(s0 + int(np.argmax(score)))
        start = max(0, min(start, len(buf) - win))
        out[x:x + win] += buf[start:start + win] * window[:, None]
        norm[x:x + win] += window
        prev = start
    res = out[half:half + n_out]
    nn = norm[half:half + n_out]
    good = nn > 0.1
    res[good] /= nn[good, None]
    res[~good] = 0.0
    return res


def stretch_file(src_path: Path, dst_path: Path, factor: float) -> dict:
    if not factor > 0:
        raise AudioError('factor must be > 0')
    w = read_wav(src_path)
    data = pcm(w)
    n_out = int(round(len(data) * factor))
    pos = np.arange(n_out, dtype=np.float64) / factor
    y = wsola(data, pos)
    write_wav(dst_path, y, w.rate)
    return {'in': str(src_path), 'out': str(dst_path), 'factor': factor, 'inSeconds': round(len(data) / w.rate, 6),
            'outSeconds': round(n_out / w.rate, 6), 'rate': w.rate, 'channels': w.channels}


# ------------------------------------------------------------------ track (6.3, 6.4)

@dataclass
class Piece:
    kind: str
    o0: int
    o1: int
    ao: list[float]
    asrc: list[float]
    speed: float
    t0: float
    t1: float
    note: str = ''


def take_fps(take: Path, run: dict | None) -> float:
    v = fnum((run or {}).get('fps'))
    if v > 0:
        return v
    spec = read_json(take / 'spec.json') or {}
    v = fnum((spec.get('timing') or {}).get('fps'))
    if v > 0:
        return v
    raise AudioError(f'{take}: no fps in run.json or spec.json timing.fps')


def meta_frames(meta: Path) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    best: dict[int, tuple[int, float, float, float]] = {}
    with meta.open(newline='', encoding='utf-8') as fh:
        rd = csv.DictReader(fh)
        if 'real_time' not in (rd.fieldnames or []):
            raise AudioError(f'{meta}: no real_time column (not a replay take)')
        for r in rd:
            f = inum(r.get('frame'), -1)
            if f < 0 or inum(r.get('captured'), 1) == 0:
                continue
            sub = inum(r.get('sub'), 0)
            cur = best.get(f)
            if cur is None or sub >= cur[0]:
                best[f] = (sub, fnum(r.get('game_time')), fnum(r.get('speed'), 1.0), fnum(r.get('real_time')))
    if not best:
        raise AudioError(f'{meta}: no captured frames')
    frames = sorted(best)
    if frames != list(range(frames[0], frames[0] + len(frames))):
        raise AudioError(f'{meta}: output frames are not contiguous ({frames[0]}..{frames[-1]}, {len(frames)} rows)')
    t = np.array([best[f][1] for f in frames], np.float64)
    s = np.array([best[f][2] for f in frames], np.float64)
    r = np.array([best[f][3] for f in frames], np.float64)
    if not np.all(np.isfinite(t)):
        raise AudioError(f'{meta}: game_time missing')
    return t, s, r


def find_record(take: Path, run: dict | None) -> Path:
    rec = (run or {}).get('recording')
    cands = []
    if isinstance(rec, dict) and rec.get('dir'):
        cands.append(Path(rec['dir']))
    cands += [take / 'record', take]
    for c in cands:
        c = c if c.is_absolute() else take / c
        if (c / AUDIO_FILE).is_file():
            return c.resolve()
    raise Refused(f'{take}: no record/{AUDIO_FILE} (audio.capture none or the capture failed)')


def build_pieces(t: np.ndarray, speeds: np.ndarray, fps: float, rate: int, clock: Clock, a: float, b: float,
                 slow: str, slope_err: float) -> list[Piece]:
    n = len(t)
    t_end = float(t[-1] + speeds[-1] / fps)
    tb = np.append(t, t_end)
    derived = np.diff(tb) * fps
    one = (np.abs(speeds - 1.0) < 1e-6) & (np.abs(derived - 1.0) < SPEED_ONE)
    runs: list[tuple[int, int, bool]] = []
    i = 0
    while i < n:
        j = i
        while j + 1 < n and one[j + 1] == one[i]:
            j += 1
        runs.append((i, j + 1, bool(one[i])))
        i = j + 1
    pieces: list[Piece] = []
    for f0, f1, is_one in runs:
        cuts = [f0 + 0.0]
        for g in clock.points:
            if tb[f0] < g < tb[f1]:
                k = int(np.searchsorted(tb, g, 'right')) - 1
                cuts.append(k + (g - tb[k]) / (tb[k + 1] - tb[k]))
        cuts.append(float(f1))
        for c0, c1 in zip(cuts[:-1], cuts[1:]):
            if c1 - c0 <= 1e-9:
                continue
            frames = [c0] + [float(k) for k in range(int(math.floor(c0)) + 1, int(math.ceil(c1)))] + [c1]
            times = [float(np.interp(fr, np.arange(n + 1), tb)) for fr in frames]
            src = []
            for idx, tt in enumerate(times):
                wl = clock.wall(tt, left=(idx == len(times) - 1))
                src.append(a * wl + b)
            ao = [fr * rate / fps for fr in frames]
            mean_speed = (times[-1] - times[0]) * fps / (c1 - c0)
            if is_one:
                kind = 'copy' if abs(slope_err) <= RESAMPLE_LIMIT else 'stretch'
                note = '' if kind == 'copy' else f'speed 1, fit slope {slope_err * 100:+.3f} % > {RESAMPLE_LIMIT * 100:.1f} %: stretched to fit'
            else:
                kind = 'mute' if slow == 'mute' and mean_speed < 1.0 else 'stretch'
                note = ''
            pieces.append(Piece(kind, int(round(ao[0])), int(round(ao[-1])), ao, src, mean_speed, times[0], times[-1], note))
    pieces = [p for p in pieces if p.o1 > p.o0]
    for k in range(1, len(pieces)):
        pieces[k].o0 = pieces[k - 1].o1
    return pieces


def fetch(src: np.ndarray, start: int, count: int) -> tuple[np.ndarray, int]:
    out = np.zeros((count, src.shape[1]), np.float32)
    a, b = max(start, 0), min(start + count, len(src))
    if b > a:
        out[a - start:b - start] = src[a:b]
    return out, count - max(0, b - a)


def render_piece(p: Piece, src: np.ndarray, hl: int, hr: int) -> tuple[np.ndarray, int]:
    n = p.o1 - p.o0 + hl + hr
    ch = src.shape[1]
    if p.kind == 'mute':
        return np.zeros((n, ch), np.float32), 0
    x = np.arange(p.o0 - hl, p.o1 + hr, dtype=np.float64)
    pos = extrap(x, np.array(p.ao), np.array(p.asrc))
    if p.kind == 'copy':
        return fetch(src, int(round(pos[0])), n)
    lo, hi = int(math.floor(pos.min())), int(math.ceil(pos.max())) + 1
    missing = max(0, -lo) + max(0, hi - len(src))
    return wsola(src, pos), min(n, missing)


def track(take: Path, slow: str = 'stretch', record: Path | None = None, out: Path | None = None) -> dict:
    take = Path(take).resolve()
    meta = take / 'meta.csv'
    if not meta.is_file():
        raise AudioError(f'{take}: no meta.csv')
    run = read_json(take / 'run.json')
    rec = Path(record).resolve() if record is not None else find_record(take, run)
    cap = locate(rec)
    info = read_json(cap.info)
    if info is not None and info.get('error'):
        raise Refused(f'capture error in {cap.info.name}: {info.get("error")}')
    rj = read_json(cap.record_json)
    au = (rj or {}).get('audio') if isinstance((rj or {}).get('audio'), dict) else {}
    if au.get('usable') is False and au.get('alignedBy') != 'mcam_audio.py align':
        raise Refused(f'record.json audio.usable is false ({au.get("reason") or "set by the recorder"})')
    al = align(rec, write=True)
    if not al['usable']:
        raise Refused(f'record audio not usable: {al["reason"]}')
    w = read_wav(cap.wav)
    if not w.ok:
        raise Refused(f'{cap.wav}: RIFF header inconsistent (run mcam_audio.py fix)')
    fps = take_fps(take, run)
    t, speeds, reals = meta_frames(meta)
    clock = record_clock(cap, (t, reals))
    a, b, rate = float(al['slope']), float(al['intercept']), w.rate
    src = pcm(w)
    pieces = build_pieces(t, speeds, fps, rate, clock, a, b, slow, float(al['slopeError']))
    n_out = int(round(len(t) * rate / fps))
    if pieces:
        pieces[0].o0 = 0
        pieces[-1].o1 = n_out
    half = int(round(XFADE_HALF * rate))
    hs = [min(half, (pieces[k].o1 - pieces[k].o0) // 4, (pieces[k + 1].o1 - pieces[k + 1].o0) // 4) for k in range(len(pieces) - 1)]
    y = np.zeros((n_out, w.channels), np.float32)
    padded = 0
    rows = []
    for k, p in enumerate(pieces):
        hl = hs[k - 1] if k > 0 else 0
        hr = hs[k] if k < len(pieces) - 1 else 0
        seg, miss = render_piece(p, src, hl, hr)
        padded += miss
        wgt = np.ones(len(seg), np.float32)
        if hl > 0:
            wgt[:2 * hl] = (np.arange(2 * hl) + 0.5) / (2 * hl)
        if hr > 0:
            wgt[len(seg) - 2 * hr:] = 1.0 - (np.arange(2 * hr) + 0.5) / (2 * hr)
        o = p.o0 - hl
        y[o:o + len(seg)] += seg * wgt[:, None]
        drift = (p.asrc[-1] - p.asrc[0]) - (p.ao[-1] - p.ao[0])
        rows.append({'kind': p.kind, 'outFrom': round(p.o0 / rate, 6), 'outTo': round(p.o1 / rate, 6), 'recFrom': round(p.t0, 6),
                     'recTo': round(p.t1, 6), 'wavFrom': round(p.asrc[0] / rate, 6), 'wavTo': round(p.asrc[-1] / rate, 6),
                     'speed': round(p.speed, 6), 'driftMs': round(drift / rate * 1000.0, 3) if p.kind == 'copy' else 0.0,
                     'xfadeMs': [round(2 * hl / rate * 1000.0, 2), round(2 * hr / rate * 1000.0, 2)], 'note': p.note})
    fade = int(round(MUTE_FADE * rate))
    for p in pieces:
        if p.kind != 'mute':
            continue
        s0, e1 = max(0, p.o0 - fade), min(n_out, p.o1 + fade)
        if p.o0 > s0:
            y[s0:p.o0] *= np.linspace(1.0, 0.0, p.o0 - s0, dtype=np.float32)[:, None]
        if e1 > p.o1:
            y[p.o1:e1] *= np.linspace(0.0, 1.0, e1 - p.o1, dtype=np.float32)[:, None]
    peak = float(np.max(np.abs(y))) if len(y) else 0.0
    dst = Path(out) if out is not None else take / TRACK_FILE
    write_wav(dst, y, rate)
    result = {'take': str(take), 'record': str(rec), 'out': str(dst), 'fps': fps, 'frames': int(len(t)), 'seconds': round(n_out / rate, 6),
              'rate': rate, 'channels': w.channels, 'slow': slow, 'fit': {k: al.get(k) for k in ('slope', 'intercept', 'slopeError', 'residualMs', 'residualMaxMs', 'markers')},
              'gaps': clock.gaps, 'pieces': rows, 'paddedSamples': int(padded), 'peakDbfs': round(20 * math.log10(peak), 2) if peak > 1e-10 else -200.0,
              'madeBy': 'mcam_audio.py track', 'madeAt': now_iso()}
    (dst.parent / (TRACK_JSON if dst.name == TRACK_FILE else dst.stem + '.json')).write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    return result


# ------------------------------------------------------------------ info and check

def dbfs_rms(x: np.ndarray) -> float:
    if x.size == 0:
        return -200.0
    v = float(np.sqrt(np.mean(np.square(x, dtype=np.float64))))
    return round(20.0 * math.log10(v), 3) if v > 1e-10 else -200.0


def dbfs_peak(x: np.ndarray) -> float:
    if x.size == 0:
        return -200.0
    v = float(np.max(np.abs(x)))
    return round(20.0 * math.log10(v), 3) if v > 1e-10 else -200.0


def info(path: Path) -> dict:
    cap = locate(path)
    w = read_wav(cap.wav)
    rows = read_sync(cap.sync)
    out = {'file': str(cap.wav), 'fileBytes': w.file_bytes, 'riffSize': w.riff_size, 'riffOk': w.riff_ok, 'dataOk': w.data_ok,
           'walkOk': w.walk_ok, 'ok': w.ok, 'needsFix': not w.ok and w.data_offset >= 0, 'format': w.format, 'channels': w.channels,
           'rate': w.rate, 'bits': w.bits, 'blockAlign': w.block_align, 'dataOffset': w.data_offset, 'dataSize': w.data_size,
           'seconds': round(w.seconds, 6), 'error': w.error,
           'cues': [{'id': c.id, 'label': c.label, 'position': c.position, 'sampleOffset': c.offset, 'frame': c.frame,
                     'seconds': round(c.frame / w.rate, 6) if w.rate else None} for c in sorted(w.cues, key=lambda c: (c.frame, c.id))]}
    if cap.sync is not None:
        out['sync'] = {'file': str(cap.sync), 'rows': len(rows), 'lag0': sum(1 for r in rows if r.lag == 0 and r.sample >= 0),
                       'lagged': sum(1 for r in rows if r.lag > 0), 'window': [r.label for r in rows if r.label in WINDOW_LABELS]}
    ci = read_json(cap.info)
    if ci is not None:
        out['capture'] = {k: ci.get(k) for k in ('name', 'path', 'level', 'appearedAfterS', 'windowOpenReal', 'windowCloseReal', 'wallSeconds',
                                                 'markers', 'lateMarkers', 'needsFix', 'error', 'stopReason')}
    return out


def check(path: Path, threshold: float = -40.0, search: float = 1.0, events: list[str] | None = None) -> dict:
    cap = locate(path)
    w = read_wav(cap.wav)
    data = pcm(w)
    rate = w.rate
    out: dict = {'file': str(cap.wav), 'seconds': round(len(data) / rate, 6), 'rmsDbfs': dbfs_rms(data), 'peakDbfs': dbfs_peak(data)}
    cues = w.cue_frames()
    wo, wc = cues.get('wopen', [None])[0], cues.get('wclose', [None])[0]
    if wo is not None:
        seg = data[wo:wc if wc is not None else len(data)]
        out['window'] = {'from': round(wo / rate, 6), 'to': round((wc if wc is not None else len(data)) / rate, 6),
                         'rmsDbfs': dbfs_rms(seg), 'peakDbfs': dbfs_peak(seg)}
    rows = read_sync(cap.sync)
    f = fit_rows(w, rows)
    if 'a' not in f:
        out['events'] = []
        out['fit'] = f.get('reason')
        return out
    a, b = f['a'], f['b']
    out['fit'] = {'slope': round(a, 6), 'intercept': round(b, 6), 'slopeError': round(a / rate - 1.0, 8)}
    clock = record_clock(cap)
    level = 10.0 ** (threshold / 20.0)
    mono = np.max(np.abs(data), axis=1) if len(data) else np.zeros(0)
    evs = []
    for r, p in f['matched']:
        if r.lag <= 0 or not r.label.startswith('e'):
            continue
        name = r.label[1:]
        if events and name not in events:
            continue
        real = clock.wall(r.game_time) if clock is not None and math.isfinite(r.game_time) else r.real_time - r.lag / 60.0
        pred = a * real + b
        i0 = max(0, int(math.floor(pred)))
        i1 = min(len(mono), i0 + int(round(search * rate)))
        hit = np.nonzero(mono[i0:i1] >= level)[0]
        e = {'event': name, 'label': r.label, 'lag': r.lag, 'gameTime': r.game_time, 'predictedWavSeconds': round(pred / rate, 6),
             'markerOffsetMs': round((p - pred) / rate * 1000.0, 3), 'thresholdDbfs': threshold}
        if len(hit):
            on = i0 + int(hit[0])
            e['onsetMs'] = round((on - pred) / rate * 1000.0, 3)
            e['transientPeakDbfs'] = dbfs_peak(data[on:on + int(0.1 * rate)])
        else:
            e['onsetMs'] = None
        evs.append(e)
    out['events'] = evs
    return out


# ------------------------------------------------------------------ CLI

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest='command', required=True)
    s = sub.add_parser('fix', help='rewrite RIFF/data sizes from the file size (idempotent)')
    s.add_argument('wav', type=Path)
    s.add_argument('--check', action='store_true', help='report only, change nothing')
    s = sub.add_parser('info', help='fmt, sizes, RIFF state, cue/labl markers')
    s.add_argument('path', type=Path)
    s = sub.add_parser('align', help='lag-0 marker fit and slope check; writes record.json audio')
    s.add_argument('path', type=Path)
    s.add_argument('--no-write', action='store_true')
    s = sub.add_parser('track', help='<take>/audio_track.wav on the output timeline (refuses unusable audio)')
    s.add_argument('take', type=Path)
    s.add_argument('--record', type=Path)
    s.add_argument('--slow', choices=('stretch', 'mute'), default='stretch')
    s.add_argument('--out', type=Path)
    s = sub.add_parser('stretch', help='pitch-preserving stretch, factor = output / input duration')
    s.add_argument('src', type=Path)
    s.add_argument('dst', type=Path)
    s.add_argument('factor', type=float)
    s = sub.add_parser('check', help='RMS, peak, window levels, onset after event markers')
    s.add_argument('path', type=Path)
    s.add_argument('--threshold', type=float, default=-40.0)
    s.add_argument('--search', type=float, default=1.0)
    s.add_argument('--event', action='append')
    return p


def main(argv: list[str] | None = None) -> int:
    opts = build_parser().parse_args(argv)
    try:
        if opts.command == 'fix':
            res = fix_wav(opts.wav, opts.check)
            code = 0 if res.get('ok') or res.get('wouldFix') else 1
        elif opts.command == 'info':
            res = info(opts.path)
            code = 0
        elif opts.command == 'align':
            res = align(opts.path, not opts.no_write)
            code = 0 if res['usable'] else 1
        elif opts.command == 'track':
            res = track(opts.take, opts.slow, opts.record, opts.out)
            res = {k: v for k, v in res.items() if k != 'pieces'} | {'pieces': len(res['pieces'])}
            code = 0
        elif opts.command == 'stretch':
            res = stretch_file(opts.src, opts.dst, opts.factor)
            code = 0
        else:
            res = check(opts.path, opts.threshold, opts.search, opts.event)
            code = 0
    except Refused as e:
        print(f'mcam_audio {opts.command}: refused: {e}', file=sys.stderr)
        return 3
    except AudioError as e:
        print(f'mcam_audio {opts.command}: {e}', file=sys.stderr)
        return 2
    print(json.dumps(res, indent=1, default=str))
    return code


if __name__ == '__main__':
    sys.exit(main())
