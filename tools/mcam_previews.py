"""Batch previews of a recorded take: every camera of a replay spec at every beat / time, with the camera's hide set applied
(`mcam show`), as full-resolution PNGs plus one contact sheet per camera and one per beat. One tight loop over POST /cmd of the
running game (srec load, mcam check, per time srec seek, per camera mcam show <cam> <t> + settle + screenshot + wait for the
PNG): no MCP client, no agent round trips. Times: numbers or @event+s (fired spec events of <record>/record_report.json,
written by srec_tool.py report when missing); beats from a scene config (previews.beats). The viewer is ended (srec end) and
the game camera restored (cam off) only when this run loaded the viewer. Exit 0 ok, 1 some stills failed, 2 not started.
Usage: python -I tools/mcam_previews.py <spec> <record_dir> <out_dir> [--beats <scene.json>] [--times t,@ev+s,...]
       [--cams a,b] [--settle 0.5] [--url http://127.0.0.1:47811]"""
from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

TOOLS = Path(__file__).resolve().parent
REPO = TOOLS.parent
URL = 'http://127.0.0.1:47811'
THUMB = (640, 360)
PNG_END = b'IEND\xaeB`\x82'
SHOT_RE = re.compile(r'screenshot -> (.+?\.png)')
BAD_WORDS = ('unknown camera', 'usage:', 'no spec loaded', 'failed', 'error', 'refused', 'busy', 'not found', 'no viewer',
             'no recording loaded', 'must be')


class BridgeError(RuntimeError):
    pass


class Bridge:
    def __init__(self, url: str = URL, timeout: float = 58.0):
        self.url = url.rstrip('/')
        self.timeout = timeout
        self.calls = 0

    def cmd(self, line: str, timeout: float | None = None) -> str:
        self.calls += 1
        req = urllib.request.Request(self.url + '/cmd', data=line.encode('utf-8'), method='POST')
        try:
            with urllib.request.urlopen(req, timeout=timeout or self.timeout) as r:
                return r.read().decode('utf-8', 'replace').strip()
        except OSError as e:
            raise BridgeError(f'POST /cmd "{line}" failed: {e}') from e

    def ping(self, timeout: float = 2.0) -> str | None:
        try:
            with urllib.request.urlopen(self.url + '/ping', timeout=timeout) as r:
                t = r.read().decode('utf-8', 'replace').strip()
                return t if t.startswith('pong') else None
        except OSError:
            return None


def first(text: str) -> str:
    return (text.splitlines() or [''])[0]


def bad(reply: str) -> bool:
    low = first(reply).lower()
    return not low or any(w in low for w in BAD_WORDS)


def parse_ref(ref, events: dict[str, float]) -> float | None:
    if isinstance(ref, (int, float)):
        return float(ref)
    s = str(ref).strip()
    m = re.match(r'^@([A-Za-z0-9_.]+)\s*(?:([+-])\s*([0-9]*\.?[0-9]+))?$', s)
    if not m:
        try:
            return float(s)
        except ValueError:
            return None
    if m.group(1) not in events:
        return None
    off = float(m.group(3) or 0.0) * (-1 if m.group(2) == '-' else 1)
    return round(events[m.group(1)] + off, 4)


def report_events(record: Path, spec: Path | None) -> dict[str, float]:
    rep = record / 'record_report.json'
    if not rep.is_file() and spec is not None:
        subprocess.run([sys.executable, '-I', str(TOOLS / 'srec_tool.py'), 'report', str(record), '--spec', str(spec)], capture_output=True, text=True)
    if not rep.is_file():
        return {}
    d = json.loads(rep.read_text(encoding='utf-8'))
    out: dict[str, float] = {}
    for e in d.get('specEvents') or []:
        if e.get('fired') and e.get('name') and e.get('time') is not None and e['name'] not in out:
            out[e['name']] = float(e['time'])
    return out


def resolve_times(items: list[tuple[str, object]], events: dict[str, float]) -> tuple[list[tuple[str, float]], list[str]]:
    ok, missing = [], []
    for name, ref in items:
        t = parse_ref(ref, events)
        if t is None:
            missing.append(f'{name} ({ref})')
        else:
            ok.append((name, t))
    return ok, missing


def beats_from(path: Path) -> list[tuple[str, object]]:
    d = json.loads(path.read_text(encoding='utf-8'))
    beats = (d.get('previews') or {}).get('beats') if 'previews' in d else d.get('beats', d)
    return [(k, v) for k, v in (beats or {}).items() if not k.startswith('_')]


def times_from(text: str) -> list[tuple[str, object]]:
    out = []
    for i, part in enumerate(p for p in text.split(',') if p.strip()):
        part = part.strip()
        name, _, ref = part.partition('=') if '=' in part else (f'T{i:02d}', '', part)
        out.append((name, ref))
    return out


def spec_cams(spec: Path) -> list[str]:
    d = json.loads(spec.read_text(encoding='utf-8'))
    return [c['name'] for c in d.get('cameras') or [] if isinstance(c, dict) and c.get('name') and c.get('enabled', True) is not False]


def wait_png(path: Path, timeout: float = 10.0) -> bool:
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        try:
            if path.stat().st_size > 64:
                with path.open('rb') as f:
                    f.seek(-8, 2)
                    if f.read(8) == PNG_END:
                        return True
        except OSError:
            pass
        time.sleep(0.02)
    return False


def font(px: int) -> ImageFont.ImageFont:
    for p in (TOOLS / 'fonts' / 'Oswald-Regular.ttf', Path('C:/Windows/Fonts/consola.ttf')):
        try:
            return ImageFont.truetype(str(p), px)
        except OSError:
            continue
    return ImageFont.load_default()


def thumb(path: Path, text: str) -> Image.Image:
    im = Image.open(path).convert('RGB').resize(THUMB, Image.BILINEAR)
    d = ImageDraw.Draw(im)
    f = font(20)
    d.rectangle([0, 0, int(d.textlength(text, font=f)) + 14, 30], fill=(0, 0, 0))
    d.text((7, 3), text, fill=(255, 220, 90), font=f)
    return im


def sheet(tiles: list[Image.Image | None], cols: int, title: str, path: Path) -> Path:
    tw, th = THUMB
    rows = max(1, (len(tiles) + cols - 1) // cols)
    top, gap = 46, 4
    img = Image.new('RGB', (cols * (tw + gap) + gap, top + rows * (th + gap) + gap), (18, 18, 20))
    d = ImageDraw.Draw(img)
    d.text((10, 10), title, fill=(216, 210, 192), font=font(24))
    for i, t in enumerate(tiles):
        if t is not None:
            img.paste(t, (gap + (i % cols) * (tw + gap), top + (i // cols) * (th + gap)))
    img.save(path, quality=90)
    return path


def run(spec: Path, record: Path, out: Path, times: list[tuple[str, float]], cams: list[str], bridge: Bridge,
        settle: float = 0.5, seek_settle: float = 0.15, log=print) -> dict:
    t0 = time.monotonic()
    out.mkdir(parents=True, exist_ok=True)
    if bridge.ping() is None:
        raise BridgeError(f'the game does not answer {bridge.url}/ping')
    st = bridge.cmd('srec status')
    loaded_here = False
    rec_s = str(record.resolve()).replace('\\', '/').lower()
    if any(ln.startswith(('recording', 'waiting to record')) for ln in st.splitlines()):
        raise BridgeError('a recording is still running (srec record stop keep first): ' + first(st))
    if first(st).startswith('viewer '):
        if rec_s not in first(st).replace('\\', '/').lower():
            raise BridgeError('another recording is loaded in the viewer (srec end first): ' + first(st))
    else:
        r = bridge.cmd(f'srec load {record}', timeout=120)
        if not first(r).startswith('srec loaded'):
            raise BridgeError('srec load failed: ' + r[:600])
        loaded_here = True
        log('srec load: ' + first(r)[:200])
    stills: list[dict] = []
    failures: list[str] = []
    pool = ThreadPoolExecutor(4)
    thumbs: dict[tuple[str, str], object] = {}
    try:
        r = bridge.cmd(f'mcam check {spec}', timeout=120)
        log('mcam check: ' + first(r)[:200])
        if 'spec error' in first(r).lower():
            raise BridgeError('mcam check: ' + r[:800])
        for beat, t in times:
            r = bridge.cmd(f'srec seek {t:.3f}')
            if bad(r):
                failures.append(f'{beat}: srec seek {t:.3f}: {first(r)}')
                continue
            time.sleep(seek_settle)
            for cam in cams:
                item = {'beat': beat, 't': t, 'cam': cam}
                r = bridge.cmd(f'mcam show {cam} {t:.3f}')
                item['show'] = r
                if bad(r) or not first(r).startswith('free camera'):
                    item['error'] = first(r)
                    failures.append(f'{beat} {cam}: {first(r)}')
                    stills.append(item)
                    continue
                time.sleep(settle)
                s = bridge.cmd(f'screenshot pv_{beat}_{cam}')
                m = SHOT_RE.search(s)
                if not m or not wait_png(Path(m.group(1))):
                    item['error'] = 'no screenshot: ' + first(s)
                    failures.append(f'{beat} {cam}: no screenshot ({first(s)})')
                    stills.append(item)
                    continue
                dst = out / cam / f'{beat}_{cam}_t{t:07.3f}.png'
                dst.parent.mkdir(parents=True, exist_ok=True)
                shutil.move(m.group(1), dst)
                item['file'] = str(dst)
                item['hide'] = next((ln[6:] for ln in r.splitlines() if ln.startswith('hide: ')), '')
                item['view'] = next((ln[6:] for ln in r.splitlines() if ln.startswith('view: ')), '')
                thumbs[(beat, cam)] = pool.submit(thumb, dst, f'{beat}  t {t:.2f}  {cam}')
                stills.append(item)
    finally:
        try:
            bridge.cmd('cam off')
        except BridgeError:
            pass
        if loaded_here:
            try:
                log('srec end: ' + first(bridge.cmd('srec end', timeout=120))[:200])
            except BridgeError as e:
                log(f'srec end failed: {e}')
    capture_s = round(time.monotonic() - t0, 1)
    tiles = {k: f.result() for k, f in thumbs.items()}
    pool.shutdown()
    sheets = []
    beats = [b for b, _ in times]
    tmap = dict(times)
    for cam in cams:
        row = [tiles.get((b, cam)) for b in beats]
        if any(row):
            sheets.append(str(sheet(row, 4, f'{cam}: {len(beats)} beats', out / f'sheet_cam_{cam}.jpg')))
    for b in beats:
        row = [tiles.get((b, c)) for c in cams]
        if any(row):
            sheets.append(str(sheet(row, min(4, max(1, len(cams))), f'beat {b} (t {tmap[b]:.2f}): {", ".join(cams)}', out / f'sheet_beat_{b}.jpg')))
    doc = {'spec': str(spec), 'record': str(record), 'cams': cams, 'times': times, 'stills': len([s for s in stills if 'file' in s]),
           'failures': failures, 'capture_s': capture_s, 'total_s': round(time.monotonic() - t0, 1), 'settle_s': settle,
           'calls': bridge.calls, 'sheets': sheets, 'items': [{k: v for k, v in s.items() if k != 'show'} | {'show': first(s.get('show', ''))} for s in stills]}
    (out / 'previews.json').write_text(json.dumps(doc, indent=1), encoding='utf-8')
    log(f'previews: {doc["stills"]} stills of {len(cams)} cameras x {len(times)} times in {capture_s} s (+ sheets {doc["total_s"] - capture_s:.1f} s), '
        f'{len(failures)} failure(s) -> {out}')
    return doc


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('spec')
    p.add_argument('record')
    p.add_argument('out')
    p.add_argument('--beats', help='scene config JSON with previews.beats {name: t | "@event+s"}')
    p.add_argument('--times', help='comma list: t, @event+s or name=@event+s')
    p.add_argument('--cams', help='comma list (default: the enabled cameras of the spec)')
    p.add_argument('--settle', type=float, default=0.5, help='seconds between mcam show and the screenshot (TAA settles)')
    p.add_argument('--url', default=URL)
    a = p.parse_args(argv)
    spec, record = Path(a.spec).resolve(), Path(a.record).resolve()
    items = beats_from(Path(a.beats)) if a.beats else []
    if a.times:
        items += times_from(a.times)
    if not items:
        print('error: no times (--beats or --times)', file=sys.stderr)
        return 2
    times, missing = resolve_times(items, report_events(record, spec))
    for m in missing:
        print(f'skipped: {m}: event not fired in the record report')
    cams = [c for c in a.cams.split(',') if c] if a.cams else spec_cams(spec)
    try:
        doc = run(spec, record, Path(a.out).resolve(), times, cams, Bridge(a.url), a.settle)
    except BridgeError as e:
        print(f'error: {e}', file=sys.stderr)
        return 2
    return 1 if doc['failures'] else 0


if __name__ == '__main__':
    sys.exit(main())
