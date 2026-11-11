"""Record-pass audio tool (REPLAY_DESIGN 6.1-6.4, 8.3 AUDIO): fix (RIFF/data sizes; a stale or zero data size is repaired to
the audio that is in the file, never cut; one <name>.bak before the first change and a printed statement of what changes),
info (fmt, cue/labl markers), align (lag-0 marker fit p = a * real_time + b, slope check 0.5 % -> record.json audio), track
(audio_track.wav on the meta.csv output timeline: speed-1 pieces copied and re-anchored before they run 1 ms off the time map,
other speeds stretched with pitch kept along the exact time map, wall-time gaps cut, 50 ms crossfades; refuses unusable
audio; --dry-run prints the pieces and writes nothing), stretch and check (RMS, peak, onset after event markers).
Never plays audio (6.0 G7). This file also holds the one WAV reader, marker fit, fps lookup and record-folder lookup of the
tools (read_wav / load_wav / write_wav, fit_markers, resolve_fps / take_fps, find_record_dir).
Exit codes: 0 ok, 1 check failed, audio not usable or the track drifts, 2 usage or input error, 3 refused."""
from __future__ import annotations

import argparse
import csv
import json
import math
import shutil
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
DRIFT_MAX = 0.001
XFADE_HALF = 0.025
MUTE_FADE = 0.3
SPEED_ONE = 1e-3
WIN, HOP, TOL = 1024, 512, 256
AUDIO_FILE, SYNC_FILE, CAPTURE_FILE = 'audio.wav', 'audio_sync.csv', 'audio_capture.json'
RECORD_JSON, RECORD_META = 'record.json', 'record_meta.csv'
TRACK_FILE, TRACK_JSON = 'audio_track.wav', 'audio_track.json'
WINDOW_LABELS = ('wopen', 'wclose')
REPLAY_MODES = ('record', 'replay', 'take', 'passes', 'golden')
RECORD_MARKS = (RECORD_JSON, RECORD_META, 'scene.srec')
TAIL_CHUNKS = frozenset({b'cue ', b'LIST', b'fact', b'smpl', b'bext', b'JUNK', b'junk', b'PAD ', b'iXML', b'id3 ', b'ID3 ',
                         b'inst', b'acid', b'plst', b'DISP', b'axml', b'cart', b'_PMX', b'PEAK'})
TAIL_STARTS = (b'cue ', b'LIST', b'fact', b'smpl', b'bext')
TAIL_SEARCH = 1 << 20
HEADER_REASON = 'WAV header inconsistent (run mcam_audio.py fix)'
EXIT_OK, EXIT_BAD, EXIT_INPUT, EXIT_REFUSED = 0, 1, 2, 3


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
    tail: str = 'none'
    data: np.ndarray | None = None

    @property
    def ok(self) -> bool:
        return self.riff_ok and self.data_ok and self.walk_ok and self.rate > 0 and self.data_offset >= 0

    @property
    def frames(self) -> int:
        return self.data_size // max(1, self.block_align)

    @property
    def seconds(self) -> float:
        return self.frames / self.rate if self.rate > 0 else 0.0

    @property
    def fmt(self) -> int:
        return self.format

    @property
    def markers(self) -> list[dict]:
        return [{'id': c.id, 'sample': int(c.frame), 'label': c.label} for c in sorted(self.cues, key=lambda c: (c.frame, c.id))]

    @property
    def problems(self) -> list[str]:
        out = []
        if self.riff and not self.riff_ok:
            out.append(f'RIFF size {self.riff_size} + 8 != file size {self.file_bytes}'
                       + (' (unfinalized: mcam_audio.py fix)' if self.riff_size in (0, 0xFFFFFFFF) else ''))
        if self.data_offset >= 0 and not self.data_ok:
            out.append(f'data size {self.data_size_raw} runs past the file end ({self.file_bytes - self.data_offset} bytes left)'
                       + (' (unfinalized: mcam_audio.py fix)' if self.data_size_raw == 0xFFFFFFFF else ''))
        elif self.data_offset >= 0 and self.tail == 'audio':
            out.append(f'data size {self.data_size_raw} is stale: {self.file_bytes - self.data_offset - self.data_size_raw} more '
                       f'bytes of audio follow it (unfinalized: mcam_audio.py fix)')
        elif self.data_offset >= 0 and not self.walk_ok:
            out.append(f'chunks after the audio end early at byte {self.last_complete} of {self.file_bytes} (mcam_audio.py fix)')
        return out

    def mono(self) -> np.ndarray:
        if self.data is None:
            return np.zeros(0, np.float32)
        return self.data.mean(axis=1) if self.data.ndim == 2 else self.data

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
        if v is None or v == '':
            return default
        x = float(v)
        return x if math.isfinite(x) else default
    except (TypeError, ValueError):
        return default


def inum(v, default: int = 0) -> int:
    x = fnum(v)
    return default if math.isnan(x) else int(round(x))


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec='seconds')


def say(text: str) -> None:
    print(text, file=sys.stderr, flush=True)


# ------------------------------------------------------------------ WAV

def fourcc(cid: bytes) -> bool:
    return len(cid) == 4 and all(0x20 <= b <= 0x7E for b in cid)


def read_wav(path: Path, data_size: int | None = None) -> Wav:
    """Header walk of a WAV (no samples are read). Never fails on a damaged file: the state is in riff_ok / data_ok /
    walk_ok / tail ('none', 'chunks', 'cut' = a chunk after the audio is incomplete, 'audio' = samples follow the size the
    data chunk gives). `data_size` reads the file as if its data chunk had that size (the view after a repair)."""
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
        pos, broken, after = 12, False, False
        while pos + 8 <= size:
            fh.seek(pos)
            hdr = fh.read(8)
            cid, n = hdr[:4], struct.unpack('<I', hdr[4:8])[0]
            body = pos + 8
            if after:
                if not fourcc(cid):
                    w.tail = 'audio'
                    broken = True
                    break
                w.tail = 'chunks'
            fits = n != 0xFFFFFFFF and body + n <= size
            if cid == b'data' and w.data_offset < 0:
                w.data_offset, w.data_size_raw = body, n
                if data_size is not None:
                    n = int(data_size)
                    fits = body + n <= size
                w.data_ok = fits
                w.data_size = n if fits else size - body
                if not fits:
                    broken = True
                    break
                after = True
            else:
                b = fh.read(min(n, size - body)) if cid in (b'fmt ', b'cue ', b'LIST') else b''
                if cid == b'fmt ' and len(b) >= 16:
                    w.format, w.channels, w.rate, _, w.block_align, w.bits = struct.unpack('<HHIIHH', b[:16])
                    if w.format == 0xFFFE and len(b) >= 26:
                        w.format = struct.unpack('<H', b[24:26])[0]
                elif cid == b'cue ' and len(b) >= 4:
                    cnt = struct.unpack('<I', b[:4])[0]
                    for k in range(min(cnt, (len(b) - 4) // 24)):
                        cue_id, position, _, _, _, offset = struct.unpack('<II4sIII', b[4 + 24 * k:28 + 24 * k])
                        w.cues.append(Cue(cue_id, position, offset))
                elif cid == b'LIST' and b[:4] == b'adtl':
                    i = 4
                    while i + 12 <= len(b):
                        sid, sn = b[i:i + 4], struct.unpack('<I', b[i + 4:i + 8])[0]
                        if sid == b'labl' and sn >= 4:
                            labels[struct.unpack('<I', b[i + 8:i + 12])[0]] = b[i + 12:i + 8 + sn].split(b'\0', 1)[0].decode('latin-1')
                        i += 8 + sn + (sn & 1)
            nxt = body + n + (n & 1)
            if not fits or (nxt > size and not (nxt == size + 1 and body + n == size)):
                if after and cid != b'data':
                    w.tail = 'cut' if cid in TAIL_CHUNKS else 'audio'
                broken = True
                break
            pos = min(nxt, size)
            w.last_complete = pos
        if not broken and pos != size and after:
            w.tail = 'cut'
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
    kinds = {(3, 32): '<f4', (3, 64): '<f8', (1, 16): '<i2', (1, 32): '<i4', (1, 8): 'u1', (1, 24): 'u1'}
    kind = kinds.get((w.format, w.bits))
    if kind is None:
        raise AudioError(f'{w.path}: unsupported sample format {w.format} / {w.bits} bit')
    width = max(1, w.bits // 8)
    count = w.frames * w.block_align // width
    with w.path.open('rb') as fh:
        fh.seek(w.data_offset)
        raw = np.fromfile(fh, kind, count * (3 if w.bits == 24 else 1))
    if w.bits == 24:
        b = raw[:len(raw) // 3 * 3].reshape(-1, 3)
        a = (b[:, 0].astype(np.int32) | (b[:, 1].astype(np.int32) << 8) | (b[:, 2].astype(np.int8).astype(np.int32) << 16)).astype(np.float32)
        a /= 8388608.0
    elif kind == '<f4':
        a = raw.astype(np.float32, copy=False)
    elif kind == '<f8':
        a = raw.astype(np.float32)
    elif kind == 'u1':
        a = raw.astype(np.float32)
        a -= 128.0
        a /= 128.0
    else:
        a = raw.astype(np.float32)
        a /= 32768.0 if w.bits == 16 else 2147483648.0
    ch = w.channels
    return a[:len(a) // ch * ch].reshape(-1, ch)


def load_wav(path: Path, with_data: bool = True) -> Wav:
    """Strict read for consumers of the samples: fails without a RIFF / fmt / data chunk; size problems are in .problems."""
    w = read_wav(path)
    if not w.riff or w.rate <= 0 or w.channels <= 0 or w.data_offset < 0:
        raise AudioError(f'{w.path}: {w.error or "no fmt chunk"}')
    if with_data:
        w.data = pcm(w)
    return w


def write_wav(path: Path, data: np.ndarray, rate: int, pcm16: bool = True, markers: list | None = None) -> Path:
    """PCM16 (default) or float32 WAV, written to <name>.tmp and then moved over the target. markers: (sample, label)."""
    a = np.asarray(data, np.float32)
    if a.ndim == 1:
        a = a[:, None]
    ch = a.shape[1]
    if pcm16:
        width = 2
        fmt = struct.pack('<HHIIHH', 1, ch, rate, rate * ch * 2, ch * 2, 16)
        extra = b''
    else:
        width = 4
        fmt = struct.pack('<HHIIHHH', 3, ch, rate, rate * ch * 4, ch * 4, 32, 0)
        extra = b'fact' + struct.pack('<II', 4, a.shape[0])
    raw_len = a.shape[0] * ch * width
    tail = b'\0' if raw_len & 1 else b''
    if markers:
        cue = struct.pack('<I', len(markers)) + b''.join(struct.pack('<II4sIII', i + 1, int(s), b'data', 0, 0, int(s))
                                                         for i, (s, _) in enumerate(markers))
        adtl = b'adtl'
        for i, (_, label) in enumerate(markers):
            body = struct.pack('<I', i + 1) + str(label).encode('ascii', 'replace') + b'\0'
            adtl += b'labl' + struct.pack('<I', len(body)) + body + (b'\0' if len(body) & 1 else b'')
        tail += b'cue ' + struct.pack('<I', len(cue)) + cue + b'LIST' + struct.pack('<I', len(adtl)) + adtl
    head = b'fmt ' + struct.pack('<I', len(fmt)) + fmt + extra + b'data' + struct.pack('<I', raw_len)
    path = Path(path)
    tmp = path.with_name(path.name + '.tmp')
    step = max(1, (1 << 22) // max(1, ch))
    with tmp.open('wb') as fh:
        fh.write(b'RIFF' + struct.pack('<I', 4 + len(head) + raw_len + len(tail)) + b'WAVE' + head)
        for i in range(0, a.shape[0], step):
            part = a[i:i + step]
            if pcm16:
                part = np.clip(np.round(part * 32767.0), -32768, 32767).astype('<i2')
            else:
                part = part.astype('<f4', copy=False)
            fh.write(part.tobytes())
        fh.write(tail)
    tmp.replace(path)
    return path


def find_all(buf: bytes, what: bytes) -> list[int]:
    out, i = [], buf.find(what)
    while i >= 0:
        out.append(i)
        i = buf.find(what, i + 1)
    return out


def find_tail(w: Wav) -> int:
    """File offset where a chain of known chunks (cue, LIST, ...) starts that ends exactly at the file end, or -1. Used when
    the data size cannot be trusted: the audio then ends where that chain starts."""
    size, start = w.file_bytes, w.data_offset
    if start < 0 or size - start < 8:
        return -1
    base = max(start, size - TAIL_SEARCH)
    with w.path.open('rb') as fh:
        fh.seek(base)
        buf = fh.read(size - base)
    for i in sorted({i for cid in TAIL_STARTS for i in find_all(buf, cid)}):
        pos = i
        while pos + 8 <= len(buf):
            cid, n = buf[pos:pos + 4], struct.unpack('<I', buf[pos + 4:pos + 8])[0]
            if cid not in TAIL_CHUNKS or pos + 8 + n > len(buf):
                break
            pos += 8 + n
            if n & 1 and pos < len(buf):
                pos += 1
        if pos == len(buf):
            return base + i
    return -1


def plan_fix(w: Wav) -> dict:
    """What `fix` would do to this file: new data size, new file length and the changes in words, without touching it."""
    align = max(1, w.block_align)
    data, new_len = w.data_size_raw, w.file_bytes
    changes: list[str] = []
    if not w.data_ok or w.tail == 'audio':
        if not w.data_ok:
            why = 'a placeholder' if w.data_size_raw == 0xFFFFFFFF else 'past the file end'
        else:
            why = 'a zero placeholder' if w.data_size_raw == 0 else 'stale'
        tail_at = find_tail(w)
        if tail_at >= 0:
            span = tail_at - w.data_offset
            if span % align and (span - 1) % align == 0 and (span - 1) & 1:
                span -= 1
            if span % align:
                tail_at = -1
        if tail_at >= 0:
            data = span
            changes.append(f'data size {w.data_size_raw} is {why}: set to {data} (the audio up to the marker chunks at byte {tail_at})')
        else:
            data = (w.file_bytes - w.data_offset) // align * align
            new_len = w.data_offset + data + (data & 1)
            changes.append(f'data size {w.data_size_raw} is {why}: set to {data} (the audio runs to the end of the file)')
            if new_len < w.file_bytes:
                changes.append(f'{w.file_bytes - new_len} trailing byte(s) of an incomplete sample frame removed')
            elif new_len > w.file_bytes:
                changes.append('one pad byte appended')
    elif not w.walk_ok:
        new_len = w.last_complete
        changes.append(f'{w.file_bytes - new_len} byte(s) of an incomplete chunk after the audio removed (the audio itself is complete)')
    if new_len - 8 != w.riff_size:
        changes.append(f'RIFF size {w.riff_size} set to {new_len - 8}')
    return {'data': int(data), 'newBytes': int(new_len), 'changes': changes}


def backup_once(path: Path) -> tuple[Path, bool]:
    bak = path.with_name(path.name + '.bak')
    if bak.exists():
        return bak, False
    tmp = path.with_name(path.name + '.bak.tmp')
    try:
        shutil.copyfile(path, tmp)
        tmp.replace(bak)
    except OSError as e:
        try:
            tmp.unlink()
        except OSError:
            pass
        raise Refused(f'{path}: cannot write the backup {bak.name} ({e}); nothing was changed') from e
    return bak, True


def fix_wav(path: Path, check_only: bool = False) -> dict:
    path = Path(path)
    w = read_wav(path)
    out = {'file': str(path), 'okBefore': w.ok, 'riffSize': w.riff_size, 'dataSizeRaw': w.data_size_raw, 'fileBytes': w.file_bytes,
           'tail': w.tail}
    if w.ok:
        out.update({'fixed': False, 'ok': True})
        return out
    if not w.riff or w.rate <= 0 or w.data_offset < 0:
        raise AudioError(f'{path}: cannot fix ({w.error or "no fmt/data chunk"})')
    plan = plan_fix(w)
    data, new_len = plan['data'], plan['newBytes']
    out.update({'dataSize': data, 'newBytes': new_len, 'truncatedBytes': max(0, w.file_bytes - new_len), 'changes': plan['changes'],
                'seconds': round(data // max(1, w.block_align) / w.rate, 6)})
    if check_only:
        out.update({'fixed': False, 'ok': False, 'wouldFix': True})
        return out
    bak, made = backup_once(path)
    out['backup'] = str(bak)
    say(f'mcam_audio fix: {path}: ' + '; '.join(plan['changes']) + f'; original kept as {bak.name}'
        + ('' if made else ' (from an earlier fix)'))
    with path.open('r+b') as fh:
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
        raise AudioError(f'{path}: still not a consistent RIFF file after the fix (the original is {bak.name})')
    return out


# ------------------------------------------------------------------ record folder, fps

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


def sync_row(r, index: int = 0) -> SyncRow:
    if isinstance(r, SyncRow):
        return r
    return SyncRow(inum(r.get('marker'), index), (r.get('label') or '').strip(), inum(r.get('sample'), -1),
                   fnum(r.get('game_time')), fnum(r.get('real_time')), inum(r.get('lag'), 0))


def read_sync(path: Path | None) -> list[SyncRow]:
    if path is None:
        return []
    rows: list[SyncRow] = []
    with Path(path).open(newline='', encoding='utf-8') as fh:
        for r in csv.DictReader(fh):
            rows.append(sync_row(r, len(rows)))
    return rows


def read_csv_columns(path: Path, *names: str) -> list[np.ndarray]:
    cols: list[list[float]] = [[] for _ in names]
    with Path(path).open(newline='', encoding='utf-8') as fh:
        for r in csv.DictReader(fh):
            for i, n in enumerate(names):
                cols[i].append(fnum(r.get(n)))
    return [np.array(c, np.float64) for c in cols]


def record_candidates(root: Path, run: dict | None) -> list[Path]:
    root = Path(root)
    run = run or {}
    cands: list = []
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
    out = []
    for c in cands:
        p = Path(str(c))
        out.append(p if p.is_absolute() else root / p)
    return out


def find_record_dir(root: Path, run: dict | None, mode: str = '') -> Path | None:
    """The record folder of a take: the take itself when it is a recording, else run.json recording.dir, replay.source,
    record.dir, <take>/record, <take>/../record (the first that holds record.json, record_meta.csv or scene.srec)."""
    root = Path(root)
    if mode == 'record' or (root / RECORD_META).is_file() or (root / 'scene.srec').is_file():
        return root
    for p in record_candidates(root, run):
        if any((p / m).is_file() for m in RECORD_MARKS):
            return p.resolve()
    return None


def find_record(take: Path, run: dict | None) -> Path:
    take = Path(take)
    rec = find_record_dir(take, run, str((run or {}).get('mode') or ''))
    if rec is not None and (rec / AUDIO_FILE).is_file():
        return rec.resolve()
    for c in record_candidates(take, run) + [take]:
        if (c / AUDIO_FILE).is_file():
            return c.resolve()
    where = f' (record folder {rec})' if rec is not None else ''
    raise Refused(f'{take}: no record/{AUDIO_FILE}{where} (audio.capture none or the capture failed)')


def resolve_fps(run: dict | None, spec: dict | None, kind: str = 'v2', replay_mode: str = '', captured_times=None,
                rate0: float | None = None, group0: float | None = None, default: float | None = None) -> float | None:
    """Output frame rate of a take, one order for every tool: record / golden runs from the median captured step, then
    run.json fps, spec timing.fps (v2; drafts and v1 only without spec rates), meta rate / group, then `default`."""
    if replay_mode in ('record', 'golden') and kind == 'v2' and captured_times is not None:
        d = np.diff(np.sort(np.asarray(captured_times, np.float64)))
        d = d[d > 1e-6]
        if len(d):
            return float(max(1.0, round(1.0 / float(np.median(d)))))
    v = fnum((run or {}).get('fps'))
    if v > 0:
        return v
    timing = (spec or {}).get('timing') or {}
    v = fnum(timing.get('fps')) if isinstance(timing, dict) else math.nan
    if v > 0 and (kind == 'v2' or not (spec or {}).get('rates')):
        return v
    if rate0 is not None and fnum(rate0) > 0:
        return float(fnum(rate0) / max(1.0, fnum(group0, 1.0)))
    return default


def take_fps(take: Path, run: dict | None = None) -> float:
    take = Path(take)
    run = run if run is not None else read_json(take / 'run.json')
    spec = read_json(take / 'spec.json') or {}
    mode = str((run or {}).get('mode') or '')
    replay_mode = mode if mode in REPLAY_MODES else ('record' if not mode and (take / RECORD_META).is_file() else '')
    kind, times, rate0, group0 = 'v1', None, None, None
    meta = take / 'meta.csv'
    if meta.is_file():
        with meta.open(newline='', encoding='utf-8') as fh:
            rd = csv.DictReader(fh)
            kind = 'v2' if 'frame' in (rd.fieldnames or []) else 'v2-draft'
            ts = []
            for i, r in enumerate(rd):
                if i == 0:
                    rate0, group0 = fnum(r.get('rate'), 30.0), max(1, inum(r.get('group'), 1))
                if inum(r.get('captured'), 1) == 1:
                    ts.append(fnum(r.get('game_time')))
            times = np.array([t for t in ts if math.isfinite(t)], np.float64)
    elif replay_mode in ('record', 'golden') and (take / RECORD_META).is_file():
        kind = 'v2'
        g = read_csv_columns(take / RECORD_META, 'game_time')[0]
        times = g[np.isfinite(g)]
    v = resolve_fps(run, spec, kind, replay_mode, times, rate0, group0, None)
    if v is None or not v > 0:
        raise AudioError(f'{take}: no fps in run.json, spec.json timing.fps or meta.csv')
    return float(v)


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

def cue_table(w, scale: int = 1) -> dict[str, list[int]]:
    out: dict[str, list[int]] = {}
    for m in sorted(w.markers, key=lambda m: (m['sample'], m.get('id', 0))):
        out.setdefault(m.get('label', ''), []).append(int(m['sample']) // max(1, scale))
    return out


def fit_rows(w, rows: list, scale: int = 1) -> dict:
    """The k-th audio_sync row of a label is paired with the k-th WAV cue of that label; the fit uses the lag-0 rows with a
    record sample (sample >= 0) and a real_time."""
    by_label = cue_table(w, scale)
    used: dict[str, int] = {}
    matched: list[tuple[SyncRow, int]] = []
    unmatched: list[str] = []
    for i, raw in enumerate(rows):
        r = sync_row(raw, i)
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
    out.update({'a': float(a), 'b': float(b), 'res': res, 'y': y})
    return out


def frame_bytes(w) -> int:
    v = int(getattr(w, 'block_align', 0) or 0)
    return v if v > 0 else int(getattr(w, 'channels', 0) or 0) * max(1, int(getattr(w, 'bits', 0) or 0) // 8)


def fit_auto(w, rows: list) -> tuple[dict, bool]:
    """fit_rows, and again with the cue positions divided by the frame size when they turn out to be byte offsets (the
    slope is then the fmt rate times the frame size)."""
    f = fit_rows(w, rows)
    ba = frame_bytes(w)
    if 'a' in f and ba > 1 and w.rate > 0 and abs(f['a'] / (w.rate * ba) - 1.0) < 0.01:
        return fit_rows(w, rows, ba), True
    return f, False


def fit_markers(w, rows: list, real_of_game=None) -> dict:
    """The marker fit as a plain dict (JSON clean): ok, a, b, ratio, slope_error, residual_ms, residual_max_ms, usable,
    quantum, lag0, markers, missing, byte_offsets, events (markers with a lag: offset to the fit). `w` needs rate and
    markers ({'sample', 'label'}); rows are audio_sync.csv rows (dicts or SyncRow)."""
    f, byte_offsets = fit_auto(w, rows)
    out = {'rate': w.rate, 'markers': len(f['matched']), 'lag0': f['points'], 'missing': list(f['unmatched']), 'ok': False,
           'byte_offsets': byte_offsets}
    if 'a' not in f:
        out['reason'] = f['reason']
        return out
    a, b, res = f['a'], f['b'], f['res']
    ratio = a / w.rate
    quantum = math.gcd(*(int(v) for v in f['y']))
    out.update({'ok': True, 'a': float(a), 'b': float(b), 'ratio': float(ratio), 'slope_error': float(abs(ratio - 1.0)),
                'quantum': quantum if 256 <= quantum <= 65536 else 1,
                'residual_ms': float(np.sqrt(np.mean(res ** 2)) / w.rate * 1000.0),
                'residual_max_ms': float(np.max(np.abs(res)) / w.rate * 1000.0),
                'usable': bool(abs(ratio - 1.0) < SLOPE_LIMIT)})
    events = []
    for r, p in f['matched']:
        if r.lag == 0 or r.label in WINDOW_LABELS or not math.isfinite(r.real_time):
            continue
        e = {'label': r.label, 'lag': r.lag, 'game_time': r.game_time, 'sample': r.sample,
             'offset_post_ms': round((p - (a * r.real_time + b)) / w.rate * 1000.0, 3)}
        if real_of_game is not None and math.isfinite(r.game_time):
            rr = real_of_game(r.game_time)
            if math.isfinite(rr):
                e['offset_event_ms'] = round((p - (a * rr + b)) / w.rate * 1000.0, 3)
        events.append(e)
    out['events'] = events
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
    reasons: list[str] = []
    if not w.ok:
        fixed = fix_wav(cap.wav, check_only=not write)
        if write:
            w = read_wav(cap.wav)
        else:
            w = read_wav(cap.wav, data_size=fixed.get('dataSize'))
            reasons.append(HEADER_REASON)
    rows = read_sync(cap.sync)
    if cap.sync is None:
        reasons.append(f'no {SYNC_FILE}')
    if info is not None and info.get('error'):
        reasons.append(f'capture error: {info.get("error")}')
    f, byte_offsets = fit_auto(w, rows)
    out.update({'fmtRate': w.rate, 'channels': w.channels, 'bits': w.bits, 'wavSeconds': round(w.seconds, 6), 'wavBytes': w.file_bytes,
                'markersTotal': len(rows), 'cues': len(w.cues), 'unmatched': len(f['unmatched']), 'markers': f['points'],
                'fixed': bool(fixed and fixed.get('fixed')), 'byteOffsets': byte_offsets})
    if fixed and fixed.get('wouldFix'):
        out['wouldFix'] = fixed.get('changes')
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
    if write and not w.ok:
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
    tmp = cap.align_out.with_name(cap.align_out.name + '.tmp')
    tmp.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    tmp.replace(cap.align_out)
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
    w = load_wav(src_path)
    data = w.data
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
    cut: bool = False


def meta_table(meta: Path) -> tuple[int, np.ndarray, np.ndarray, np.ndarray]:
    """First output frame index and, per captured output frame, record game time, speed and real time."""
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
    return frames[0], t, s, r


def meta_frames(meta: Path) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    return meta_table(meta)[1:]


def copy_pieces(ao: list[float], src: list[float], times: list[float], rate: int, speed: float) -> list[Piece]:
    """A speed-1 run as copied pieces: a new piece starts at the frame before the copy would be more than DRIFT_MAX off
    the exact time map, so no piece drifts further. One frame that alone steps further (a wall-time hitch below the gap
    limit) becomes its own piece and the step is cut at its end."""
    limit = DRIFT_MAX * rate
    out: list[Piece] = []
    i0, n = 0, len(ao)
    while i0 < n - 1:
        j = i0 + 1
        while j + 1 < n and abs((src[j + 1] - src[i0]) - (ao[j + 1] - ao[i0])) <= limit:
            j += 1
        drift = (src[j] - src[i0]) - (ao[j] - ao[i0])
        hitch = j == i0 + 1 and abs(drift) > limit
        note = f'wall-time step of {drift / rate * 1000.0:+.1f} ms inside one frame: cut at its end' if hitch else ''
        out.append(Piece('copy', int(round(ao[i0])), int(round(ao[j])), list(ao[i0:j + 1]), list(src[i0:j + 1]), speed, times[i0],
                         times[j], note, hitch))
        i0 = j
    return out


def build_pieces(t: np.ndarray, speeds: np.ndarray, fps: float, rate: int, clock: Clock, a: float, b: float,
                 slow: str, slope_err: float, first: int = 0) -> list[Piece]:
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
            ao = [(first + fr) * rate / fps for fr in frames]
            mean_speed = (times[-1] - times[0]) * fps / (c1 - c0)
            if is_one and abs(slope_err) <= RESAMPLE_LIMIT:
                pieces += copy_pieces(ao, src, times, rate, mean_speed)
                continue
            if is_one:
                kind = 'stretch'
                note = f'speed 1, fit slope {slope_err * 100:+.3f} % > {RESAMPLE_LIMIT * 100:.1f} %: stretched to fit'
            else:
                kind = 'mute' if slow == 'mute' and mean_speed < 1.0 else 'stretch'
                note = ''
            pieces.append(Piece(kind, int(round(ao[0])), int(round(ao[-1])), ao, src, mean_speed, times[0], times[-1], note))
    pieces = [p for p in pieces if p.o1 > p.o0]
    for k in range(1, len(pieces)):
        pieces[k].o0 = pieces[k - 1].o1
    return [p for p in pieces if p.o1 > p.o0]


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
    if p.kind == 'copy':
        return fetch(src, int(round(p.asrc[0] + (p.o0 - hl - p.ao[0]))), n)
    x = np.arange(p.o0 - hl, p.o1 + hr, dtype=np.float64)
    pos = extrap(x, np.array(p.ao), np.array(p.asrc))
    lo, hi = int(math.floor(pos.min())), int(math.ceil(pos.max())) + 1
    missing = max(0, -lo) + max(0, hi - len(src))
    return wsola(src, pos), min(n, missing)


def piece_drift(p: Piece) -> float:
    return (p.asrc[-1] - p.asrc[0]) - (p.ao[-1] - p.ao[0]) if p.kind == 'copy' else 0.0


def track(take: Path, slow: str = 'stretch', record: Path | None = None, out: Path | None = None, dry_run: bool = False) -> dict:
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
    al = align(rec, write=not dry_run)
    if not al['usable'] and not (dry_run and al['reason'] == HEADER_REASON):
        raise Refused(f'record audio not usable: {al["reason"]}')
    w = read_wav(cap.wav)
    if not w.ok:
        if not dry_run:
            raise Refused(f'{cap.wav}: RIFF header inconsistent (run mcam_audio.py fix)')
        w = read_wav(cap.wav, data_size=plan_fix(w)['data'])
    fps = take_fps(take, run)
    first, t, speeds, reals = meta_table(meta)
    clock = record_clock(cap, (t, reals))
    a, b, rate = float(al['slope']), float(al['intercept']), w.rate
    pieces = build_pieces(t, speeds, fps, rate, clock, a, b, slow, float(al['slopeError']), first)
    lead = int(round(first * rate / fps))
    n_out = int(round((first + len(t)) * rate / fps))
    if pieces:
        pieces[0].o0 = lead
        pieces[-1].o1 = n_out
    half = int(round(XFADE_HALF * rate))
    hs = [min(half, (pieces[k].o1 - pieces[k].o0) // 4, (pieces[k + 1].o1 - pieces[k + 1].o0) // 4) for k in range(len(pieces) - 1)]
    src = None if dry_run else pcm(w)
    y = None if dry_run else np.zeros((n_out, w.channels), np.float32)
    padded = 0
    rows = []
    for k, p in enumerate(pieces):
        hl = hs[k - 1] if k > 0 else 0
        hr = hs[k] if k < len(pieces) - 1 else 0
        if y is not None:
            seg, miss = render_piece(p, src, hl, hr)
            padded += miss
            wgt = np.ones(len(seg), np.float32)
            if hl > 0:
                wgt[:2 * hl] = (np.arange(2 * hl) + 0.5) / (2 * hl)
            if hr > 0:
                wgt[len(seg) - 2 * hr:] = 1.0 - (np.arange(2 * hr) + 0.5) / (2 * hr)
            o = p.o0 - hl
            y[o:o + len(seg)] += seg * wgt[:, None]
        rows.append({'kind': p.kind, 'outFrom': round(p.o0 / rate, 6), 'outTo': round(p.o1 / rate, 6), 'recFrom': round(p.t0, 6),
                     'recTo': round(p.t1, 6), 'wavFrom': round(p.asrc[0] / rate, 6), 'wavTo': round(p.asrc[-1] / rate, 6),
                     'speed': round(p.speed, 6), 'driftMs': round(piece_drift(p) / rate * 1000.0, 3), 'cut': p.cut,
                     'xfadeMs': [round(2 * hl / rate * 1000.0, 2), round(2 * hr / rate * 1000.0, 2)], 'note': p.note})
    if y is not None:
        fade = int(round(MUTE_FADE * rate))
        for p in pieces:
            if p.kind != 'mute':
                continue
            s0, e1 = max(0, p.o0 - fade), min(n_out, p.o1 + fade)
            if p.o0 > s0:
                y[s0:p.o0] *= np.linspace(1.0, 0.0, p.o0 - s0, dtype=np.float32)[:, None]
            if e1 > p.o1:
                y[p.o1:e1] *= np.linspace(0.0, 1.0, e1 - p.o1, dtype=np.float32)[:, None]
    peak = float(np.max(np.abs(y))) if y is not None and len(y) else 0.0
    dst = Path(out) if out is not None else take / TRACK_FILE
    max_drift = max((abs(r['driftMs']) for r in rows if r['kind'] == 'copy' and not r['cut']), default=0.0)
    limit = 500.0 / fps
    result = {'take': str(take), 'record': str(rec), 'out': str(dst), 'fps': fps, 'frames': int(len(t)), 'firstFrame': int(first),
              'seconds': round(n_out / rate, 6), 'rate': rate, 'channels': w.channels, 'slow': slow,
              'fit': {k: al.get(k) for k in ('slope', 'intercept', 'slopeError', 'residualMs', 'residualMaxMs', 'markers')},
              'gaps': clock.gaps, 'pieces': rows, 'paddedSamples': int(padded),
              'maxDriftMs': round(max_drift, 3), 'driftLimitMs': round(limit, 3), 'driftOk': max_drift <= limit,
              'cuts': sum(1 for r in rows if r['cut']),
              'peakDbfs': (round(20 * math.log10(peak), 2) if peak > 1e-10 else -200.0) if y is not None else None,
              'dryRun': bool(dry_run), 'madeBy': 'mcam_audio.py track', 'madeAt': now_iso()}
    if al.get('wouldFix'):
        result['wouldFix'] = al['wouldFix']
    if y is not None:
        write_wav(dst, y, rate)
        js = dst.parent / (TRACK_JSON if dst.name == TRACK_FILE else dst.stem + '.json')
        tmp = js.with_name(js.name + '.tmp')
        tmp.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
        tmp.replace(js)
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


def dbfs(x) -> float:
    if x is None or not len(x):
        return -200.0
    rms = float(np.sqrt(np.mean(np.square(np.asarray(x, np.float64)))))
    return 20.0 * math.log10(rms) if rms > 1e-10 else -200.0


def info(path: Path) -> dict:
    cap = locate(path)
    w = read_wav(cap.wav)
    rows = read_sync(cap.sync)
    out = {'file': str(cap.wav), 'fileBytes': w.file_bytes, 'riffSize': w.riff_size, 'riffOk': w.riff_ok, 'dataOk': w.data_ok,
           'walkOk': w.walk_ok, 'ok': w.ok, 'needsFix': not w.ok and w.data_offset >= 0, 'format': w.format, 'channels': w.channels,
           'rate': w.rate, 'bits': w.bits, 'blockAlign': w.block_align, 'dataOffset': w.data_offset, 'dataSize': w.data_size,
           'seconds': round(w.seconds, 6), 'error': w.error, 'tail': w.tail, 'problems': w.problems,
           'cues': [{'id': c.id, 'label': c.label, 'position': c.position, 'sampleOffset': c.offset, 'frame': c.frame,
                     'seconds': round(c.frame / w.rate, 6) if w.rate else None} for c in sorted(w.cues, key=lambda c: (c.frame, c.id))]}
    if out['needsFix'] and w.rate > 0:
        plan = plan_fix(w)
        out['fix'] = {'dataSize': plan['data'], 'newBytes': plan['newBytes'], 'changes': plan['changes'],
                      'seconds': round(plan['data'] // max(1, w.block_align) / w.rate, 6)}
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
    w = load_wav(cap.wav)
    data = w.data
    rate = w.rate
    out: dict = {'file': str(cap.wav), 'seconds': round(len(data) / rate, 6), 'rmsDbfs': dbfs_rms(data), 'peakDbfs': dbfs_peak(data)}
    cues = w.cue_frames()
    wo, wc = cues.get('wopen', [None])[0], cues.get('wclose', [None])[0]
    if wo is not None:
        seg = data[wo:wc if wc is not None else len(data)]
        out['window'] = {'from': round(wo / rate, 6), 'to': round((wc if wc is not None else len(data)) / rate, 6),
                         'rmsDbfs': dbfs_rms(seg), 'peakDbfs': dbfs_peak(seg)}
    rows = read_sync(cap.sync)
    f, _ = fit_auto(w, rows)
    if 'a' not in f:
        out['events'] = []
        out['fit'] = f.get('reason')
        out['ok'] = cap.sync is None and not events
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
    missing = [n for n in (events or []) if n not in {e['event'] for e in evs}]
    if missing:
        out['eventsNotFound'] = missing
    out['ok'] = all(e['onsetMs'] is not None for e in evs) and not missing
    return out


# ------------------------------------------------------------------ CLI

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest='command', required=True)
    s = sub.add_parser('fix', help='repair RIFF/data sizes from what is in the file (idempotent; <wav>.bak before the first change)')
    s.add_argument('wav', type=Path)
    s.add_argument('--check', action='store_true', help='report what would change, change nothing')
    s = sub.add_parser('info', help='fmt, sizes, RIFF state, cue/labl markers')
    s.add_argument('path', type=Path)
    s = sub.add_parser('align', help='lag-0 marker fit and slope check; writes record.json audio')
    s.add_argument('path', type=Path)
    s.add_argument('--no-write', action='store_true', help='change no file (neither record.json nor the WAV header)')
    s = sub.add_parser('track', help='<take>/audio_track.wav on the output timeline (refuses unusable audio)')
    s.add_argument('take', type=Path)
    s.add_argument('--record', type=Path)
    s.add_argument('--slow', choices=('stretch', 'mute'), default='stretch')
    s.add_argument('--out', type=Path)
    s.add_argument('--dry-run', action='store_true', help='print the pieces, gaps and the largest drift; write nothing')
    s = sub.add_parser('stretch', help='pitch-preserving stretch, factor = output / input duration')
    s.add_argument('src', type=Path)
    s.add_argument('dst', type=Path)
    s.add_argument('factor', type=float)
    s = sub.add_parser('check', help='RMS, peak, window levels, onset after event markers (exit 1: no fit or no onset)')
    s.add_argument('path', type=Path)
    s.add_argument('--threshold', type=float, default=-40.0)
    s.add_argument('--search', type=float, default=1.0)
    s.add_argument('--event', action='append')
    return p


def main(argv: list[str] | None = None) -> int:
    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(encoding='utf-8', errors='replace')
        except (AttributeError, ValueError, OSError):
            pass
    opts = build_parser().parse_args(argv)
    try:
        if opts.command == 'fix':
            res = fix_wav(opts.wav, opts.check)
            code = EXIT_OK if res.get('ok') or res.get('wouldFix') else EXIT_BAD
        elif opts.command == 'info':
            res = info(opts.path)
            code = EXIT_OK
        elif opts.command == 'align':
            res = align(opts.path, not opts.no_write)
            code = EXIT_OK if res['usable'] else EXIT_BAD
        elif opts.command == 'track':
            res = track(opts.take, opts.slow, opts.record, opts.out, opts.dry_run)
            code = EXIT_OK if res['driftOk'] else EXIT_BAD
            if not res['driftOk']:
                say(f'mcam_audio track: a copied piece drifts {res["maxDriftMs"]} ms from the time map (limit half a frame, '
                    f'{res["driftLimitMs"]} ms)')
            if not opts.dry_run:
                res = dict({k: v for k, v in res.items() if k != 'pieces'}, pieces=len(res['pieces']))
        elif opts.command == 'stretch':
            res = stretch_file(opts.src, opts.dst, opts.factor)
            code = EXIT_OK
        else:
            res = check(opts.path, opts.threshold, opts.search, opts.event)
            code = EXIT_OK if res.get('ok') else EXIT_BAD
    except Refused as e:
        say(f'mcam_audio {opts.command}: refused: {e}')
        return EXIT_REFUSED
    except AudioError as e:
        say(f'mcam_audio {opts.command}: {e}')
        return EXIT_INPUT
    except (OSError, ValueError, struct.error, OverflowError) as e:
        say(f'mcam_audio {opts.command}: {type(e).__name__}: {e}')
        return EXIT_INPUT
    print(json.dumps(res, indent=1, default=str))
    return code


if __name__ == '__main__':
    sys.exit(main())
