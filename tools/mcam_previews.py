"""Batch previews of a recorded take: every camera of a replay spec at every beat / time, with the camera's hide set applied
(`mcam show`), as full-resolution PNGs plus one contact sheet per camera and one per beat. One tight loop over the game's
command channel (tools/inside_link.py: POST /cmd on one kept connection, bearer token, file bridge fallback): srec load,
mcam check, per time srec seek, per camera one `mcam still <cam> <t> <file>` when the game has that verb, else mcam show
<cam> <t> + settle + screenshot + wait for the PNG. No MCP client, no agent round trips. Times: numbers or @event+s (fired spec
events of <record>/record_report.json, written by srec_tool.py report when missing); beats from a scene config
(previews.beats). At the end the free camera is switched off (cam off; mcam show always takes it), and the viewer is ended
(srec end) only when this run loaded it. previews.json is written also when the run stops half-way.
Exit 0 ok, 1 some stills failed or some times were skipped, 2 not started (bad input, game not reachable, spec refused).
Usage: python -I tools/mcam_previews.py <spec> <record_dir> <out_dir> [--beats <scene.json>] [--times t,@ev+s,...]
       [--cams a,b] [--settle 0.5] [--force] [--port N | --url http://127.0.0.1:47811] [--token T] [--game DIR | --bridge DIR]"""
from __future__ import annotations

import argparse
import functools
import json
import os
import re
import shutil
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from urllib.parse import urlsplit

from PIL import Image, ImageDraw, ImageFont

TOOLS = Path(__file__).resolve().parent
REPO = TOOLS.parent
sys.path.insert(0, str(TOOLS))

import inside_link as IL  # noqa: E402
from mcam_spec_check import parse_time  # noqa: E402

THUMB = (640, 360)
PNG_END = b'IEND\xaeB`\x82'
SHOT_RE = re.compile(r'screenshot -> (.+?\.png)')
BAD_WORDS = ('unknown camera', 'usage:', 'no spec loaded', 'failed', 'error', 'refused', 'busy', 'not found', 'no viewer',
             'no recording loaded', 'must be')
STILL_FRAMES = 8


class BridgeError(RuntimeError):
    pass


class Bridge:
    def __init__(self, url: str | None = None, timeout: float = 58.0, link: IL.Link | None = None):
        if link is None:
            u = urlsplit(url if url and '//' in url else '//' + (url or ''))
            link = IL.Link(host=u.hostname or None, port=u.port or None, http_timeout=max(timeout, 58.0), user_agent='mcam_previews')
        self.link = link
        self.url = link.url
        self.timeout = timeout

    @property
    def calls(self) -> int:
        return self.link.calls

    def cmd(self, line: str, timeout: float | None = None) -> IL.Reply:
        try:
            return self.link.send(line, timeout)
        except IL.LinkError as e:
            raise BridgeError(f'command "{line}" failed: {e}') from e

    def ping(self, timeout: float = 2.0) -> str | None:
        return self.link.ping(timeout)


def first(text) -> str:
    return (str(getattr(text, 'text', text)).splitlines() or [''])[0]


def bad(reply: IL.Reply) -> bool:
    low = first(reply).lower()
    return reply.failed or not low or any(w in low for w in BAD_WORDS)


def name_tag(text: str) -> str:
    return re.sub(r'[^A-Za-z0-9_.-]', '_', str(text)) or '_'


def parse_ref(ref, events: dict[str, float]) -> tuple[float | None, str]:
    if isinstance(ref, bool) or ref is None:
        return None, 'not a time'
    if isinstance(ref, (int, float)):
        return float(ref), ''
    ok, ev, off = parse_time(str(ref))
    if not ok:
        return None, 'not a time (a number or "@event+1.5")'
    if ev is None:
        return float(off), ''
    if ev not in events:
        return None, f'event "{ev}" not fired in the record report'
    return round(events[ev] + off, 4), ''


def report_events(record: Path, spec: Path | None) -> tuple[dict[str, float], str]:
    rep = record / 'record_report.json'
    why = ''
    if not rep.is_file() and spec is not None:
        try:
            p = subprocess.run([sys.executable, '-I', str(TOOLS / 'srec_tool.py'), 'report', str(record), '--spec', str(spec)],
                               capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=600)
            if p.returncode != 0 or not rep.is_file():
                tail = ' | '.join((p.stderr or p.stdout or '').strip().splitlines()[-3:])
                why = f'srec_tool.py report failed (exit {p.returncode}): {tail}'
        except (OSError, subprocess.SubprocessError) as e:
            why = f'srec_tool.py report could not run: {e}'
    if not rep.is_file():
        return {}, why or f'{rep} not found'
    d = json.loads(rep.read_text(encoding='utf-8'))
    out: dict[str, float] = {}
    for e in (d.get('specEvents') if isinstance(d, dict) else None) or []:
        if isinstance(e, dict) and e.get('fired') and e.get('name') and e.get('time') is not None and e['name'] not in out:
            out[e['name']] = float(e['time'])
    return out, ''


def needs_events(items: list[tuple[str, object]]) -> bool:
    return any(isinstance(ref, str) and ref.strip().startswith('@') for _, ref in items)


def resolve_times(items: list[tuple[str, object]], events: dict[str, float]) -> tuple[list[tuple[str, float]], list[str]]:
    ok, missing = [], []
    for name, ref in items:
        t, why = parse_ref(ref, events)
        if t is None:
            missing.append(f'{name} ({ref}): {why}')
        else:
            ok.append((name, t))
    return ok, missing


def beats_from(path: Path) -> list[tuple[str, object]]:
    d = json.loads(path.read_text(encoding='utf-8'))
    if not isinstance(d, dict):
        raise ValueError(f'{path}: a JSON object expected')
    beats = (d.get('previews') or {}).get('beats') if 'previews' in d else d.get('beats', d)
    if not isinstance(beats, dict):
        raise ValueError(f'{path}: previews.beats must be an object {{name: time}}')
    return [(k, v) for k, v in beats.items() if not k.startswith('_')]


def times_from(text: str) -> list[tuple[str, object]]:
    out = []
    for i, part in enumerate(p for p in text.split(',') if p.strip()):
        part = part.strip()
        name, _, ref = part.partition('=') if '=' in part else (f'T{i:02d}', '', part)
        out.append((name.strip(), ref.strip()))
    return out


def spec_cams(spec: Path) -> list[str]:
    d = json.loads(spec.read_text(encoding='utf-8-sig'))
    if not isinstance(d, dict):
        raise ValueError(f'{spec}: a JSON object expected')
    cams = d.get('cameras') or []
    if isinstance(cams, dict):
        cams = [dict(v, name=v.get('name', k)) for k, v in cams.items() if isinstance(v, dict) and not k.startswith('_')]
    return [str(c['name']) for c in cams if isinstance(c, dict) and c.get('name') and c.get('enabled', True) is not False]


def wait_png(path: Path, timeout: float = 10.0, newer_than: float = 0.0) -> bool:
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        try:
            st = path.stat()
            if st.st_size > 64 and st.st_mtime >= newer_than:
                with path.open('rb') as f:
                    f.seek(-8, 2)
                    if f.read(8) == PNG_END:
                        return True
        except OSError:
            pass
        time.sleep(0.02)
    return False


@functools.lru_cache(maxsize=8)
def font(px: int) -> ImageFont.ImageFont:
    cands = [Path(os.environ[k]) / 'Fonts' / 'consola.ttf' for k in ('WINDIR', 'SystemRoot') if os.environ.get(k)]
    for p in [str(c) for c in cands] + ['DejaVuSansMono.ttf', 'Menlo.ttc']:
        try:
            return ImageFont.truetype(p, px)
        except OSError:
            continue
    try:
        return ImageFont.load_default(px)
    except TypeError:
        return ImageFont.load_default()


def thumb(path: Path, text: str) -> Image.Image:
    with Image.open(path) as src:
        im = src.convert('RGB')
    k = min(THUMB[0] / im.size[0], THUMB[1] / im.size[1])
    im = im.resize((max(1, round(im.size[0] * k)), max(1, round(im.size[1] * k))), Image.BILINEAR, reducing_gap=2.0)
    if im.size != THUMB:
        canvas = Image.new('RGB', THUMB, (0, 0, 0))
        canvas.paste(im, ((THUMB[0] - im.size[0]) // 2, (THUMB[1] - im.size[1]) // 2))
        im = canvas
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


def unique_tags(names: list[str]) -> dict[str, str]:
    out: dict[str, str] = {}
    used: set[str] = set()
    for n in names:
        tag, k = name_tag(n), 1
        while tag.lower() in used:
            k += 1
            tag = f'{name_tag(n)}_{k}'
        used.add(tag.lower())
        out[n] = tag
    return out


class Stills:
    """One still: the game verb `mcam still` when it exists (one call, the game answers when the file is closed), else
    mcam show + settle + screenshot + wait for the PNG + move."""

    def __init__(self, bridge: Bridge, settle: float, verb: bool = True):
        self.bridge, self.settle, self.verb = bridge, settle, verb

    def take(self, cam: str, t: float, dst: Path, shot_name: str) -> tuple[IL.Reply, str]:
        if self.verb:
            r = self.bridge.cmd(f'mcam still {IL.quote(cam)} {t:.3f} {IL.quote(dst)} frames={STILL_FRAMES}', timeout=60)
            if first(r).startswith('still ->'):
                return r, '' if wait_png(dst, 2.0) else f'no PNG at {dst}'
            if first(r).lower().startswith(('usage:', 'unknown')) or 'mcam check <spec>' in first(r):
                self.verb = False
            else:
                return r, first(r) or 'no reply'
        r = self.bridge.cmd(f'mcam show {IL.quote(cam)} {t:.3f}')
        if bad(r) or not first(r).startswith('free camera'):
            return r, first(r) or 'no reply'
        time.sleep(self.settle)
        asked = time.time() - 2.0
        s = self.bridge.cmd(f'screenshot {shot_name}')
        m = SHOT_RE.search(s.text)
        if s.failed or not m or not wait_png(Path(m.group(1)), newer_than=asked):
            return r, 'no screenshot: ' + first(s)
        try:
            shutil.move(m.group(1), dst)
        except OSError as e:
            return r, f'cannot move {m.group(1)} to {dst}: {e}'
        return r, ''


def run(spec: Path, record: Path, out: Path, times: list[tuple[str, float]], cams: list[str], bridge: Bridge,
        settle: float = 0.5, seek_settle: float = 0.15, log=print, force: bool = False, missing: list[str] | None = None) -> dict:
    t0 = time.monotonic()
    out.mkdir(parents=True, exist_ok=True)
    if bridge.ping() is None:
        raise BridgeError(f'the game does not answer {bridge.url}/ping (nor the file bridge)')
    st = bridge.cmd('srec status')
    loaded_here = False
    rec_s = str(record.resolve()).replace('\\', '/').lower()
    if any(ln.startswith(('recording', 'waiting to record')) for ln in st.text.splitlines()):
        raise BridgeError('a recording is still running (srec record stop keep first): ' + first(st))
    if first(st).startswith('viewer '):
        if rec_s not in first(st).replace('\\', '/').lower():
            raise BridgeError('another recording is loaded in the viewer (srec end first): ' + first(st))
    else:
        r = bridge.cmd(f'srec load {IL.quote(record)}', timeout=120)
        if r.failed or not first(r).startswith('srec loaded'):
            raise BridgeError('srec load failed: ' + r.text[:600])
        loaded_here = True
        log('srec load: ' + first(r)[:200])
    stills: list[dict] = []
    failures: list[str] = []
    thumbs: dict[tuple[str, str], object] = {}
    tiles: dict[tuple[str, str], Image.Image | None] = {}
    sheets: list[str] = []
    beats = [b for b, _ in times]
    tmap = dict(times)
    btag, ctag = unique_tags(beats), unique_tags(cams)
    shooter = Stills(bridge, settle)
    capture_s = 0.0
    stopped = ''
    try:
        with ThreadPoolExecutor(4) as pool:
            try:
                r = bridge.cmd(f'mcam check {IL.quote(spec)}', timeout=120)
                log('mcam check: ' + first(r)[:200])
                if not first(r).rstrip().endswith(': OK'):
                    plan = first(r).startswith('mcam check ') and any(ln.startswith('spec ') for ln in r.text.splitlines()[1:3])
                    if not force or not plan:
                        raise BridgeError('mcam check: ' + r.text[:800] + ('\n(--force previews a spec that has errors)' if plan else ''))
                    log('mcam check has errors, going on (--force)')
                for beat, t in times:
                    r = bridge.cmd(f'srec seek {t:.3f}')
                    if bad(r):
                        failures.append(f'{beat}: srec seek {t:.3f}: {first(r)}')
                        continue
                    time.sleep(seek_settle)
                    for cam in cams:
                        item = {'beat': beat, 't': t, 'cam': cam}
                        dst = out / ctag[cam] / f'{btag[beat]}_{ctag[cam]}_t{t:07.3f}.png'
                        dst.parent.mkdir(parents=True, exist_ok=True)
                        r, err = shooter.take(cam, t, dst, f'pv_{btag[beat]}_{ctag[cam]}')
                        item['show'] = first(r)
                        if err:
                            item['error'] = err
                            failures.append(f'{beat} {cam}: {err}')
                            stills.append(item)
                            continue
                        item['file'] = str(dst)
                        item['hide'] = next((ln[6:] for ln in r.text.splitlines() if ln.startswith('hide: ')), '')
                        item['view'] = next((ln[6:] for ln in r.text.splitlines() if ln.startswith('view: ')), '')
                        thumbs[(beat, cam)] = pool.submit(thumb, dst, f'{beat}  t {t:.2f}  {cam}')
                        stills.append(item)
            except (BridgeError, KeyboardInterrupt) as e:
                stopped = 'interrupted' if isinstance(e, KeyboardInterrupt) else str(e)
                if not stills:
                    raise
                failures.append('stopped: ' + stopped)
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
            for key, fut in thumbs.items():
                try:
                    tiles[key] = fut.result()
                except Exception as e:
                    tiles[key] = None
                    failures.append(f'{key[0]} {key[1]}: unreadable still ({type(e).__name__}: {e})')
        for cam in cams:
            row = [tiles.get((b, cam)) for b in beats]
            if any(row):
                sheets.append(str(sheet(row, 4, f'{cam}: {len(beats)} beats', out / f'sheet_cam_{ctag[cam]}.jpg')))
        for b in beats:
            row = [tiles.get((b, c)) for c in cams]
            if any(row):
                sheets.append(str(sheet(row, min(4, max(1, len(cams))), f'beat {b} (t {tmap[b]:.2f}): {", ".join(cams)}',
                                        out / f'sheet_beat_{btag[b]}.jpg')))
    finally:
        doc = {'spec': str(spec), 'record': str(record), 'cams': cams, 'times': times, 'stills': len([s for s in stills if 'file' in s]),
               'failures': failures, 'missing': list(missing or []), 'stopped': stopped, 'capture_s': capture_s,
               'total_s': round(time.monotonic() - t0, 1), 'settle_s': settle, 'still_verb': shooter.verb, 'calls': bridge.calls,
               'sheets': sheets, 'items': stills}
        try:
            (out / 'previews.json').write_text(json.dumps(doc, indent=1), encoding='utf-8')
        except OSError as e:
            log(f'previews.json not written: {e}')
    log(f'previews: {doc["stills"]} stills of {len(cams)} cameras x {len(times)} times in {capture_s} s (+ sheets {doc["total_s"] - capture_s:.1f} s), '
        f'{len(failures)} failure(s) -> {out}')
    return doc


def main(argv: list[str] | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding='utf-8', errors='replace')
        except (AttributeError, ValueError):
            pass
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('spec')
    p.add_argument('record')
    p.add_argument('out')
    p.add_argument('--beats', help='scene config JSON with previews.beats {name: t | "@event+s"}')
    p.add_argument('--times', help='comma list: t, @event+s or name=@event+s')
    p.add_argument('--cams', help='comma list (default: the enabled cameras of the spec)')
    p.add_argument('--settle', type=float, default=0.5, help='seconds between mcam show and the screenshot (TAA settles)')
    p.add_argument('--force', action='store_true', help='go on when mcam check reports errors in the spec')
    p.add_argument('--url', default=None, help=f'the game, e.g. http://127.0.0.1:{IL.DEFAULT_PORT} (default: --port)')
    p.add_argument('--port', type=int, default=None, help=f"the game's port (env {IL.ENV_PORT}, else {IL.DEFAULT_PORT})")
    p.add_argument('--token', default=None, help=f"bearer token of the game's mcp.token (env {IL.ENV_TOKEN})")
    p.add_argument('--game', type=Path, default=None, help=f'the INSIDE folder for the file bridge fallback (env {IL.ENV_GAME})')
    p.add_argument('--bridge', type=Path, default=None, help=f'the file bridge folder <game>/_mod/cmd (env {IL.ENV_BRIDGE})')
    a = p.parse_args(argv)
    if a.settle < 0:
        p.error('--settle must be >= 0')
    spec, record = Path(a.spec).resolve(), Path(a.record).resolve()
    try:
        if not spec.is_file():
            raise ValueError(f'spec not found: {spec}')
        if not record.is_dir():
            raise ValueError(f'record folder not found: {record}')
        items = beats_from(Path(a.beats)) if a.beats else []
        if a.times:
            items += times_from(a.times)
        if not items:
            raise ValueError('no times (--beats or --times)')
        events, why = report_events(record, spec) if needs_events(items) else ({}, '')
        if why:
            raise ValueError(f'event times are needed but not available: {why}')
        cams = [c.strip() for c in a.cams.split(',') if c.strip()] if a.cams else spec_cams(spec)
        if not cams:
            raise ValueError('no cameras (--cams, or enabled cameras in the spec)')
        u = urlsplit(a.url if '//' in a.url else '//' + a.url) if a.url else None
        link = IL.Link(host=u.hostname if u else None, port=a.port or (u.port if u else None), token=a.token, bridge=a.bridge, game=a.game,
                       user_agent='mcam_previews')
    except (OSError, ValueError, KeyError, TypeError, AttributeError) as e:
        print(f'error: {e}', file=sys.stderr)
        return 2
    times, missing = resolve_times(items, events)
    for m in missing:
        print(f'skipped: {m}')
    if not times:
        print('error: no time could be resolved', file=sys.stderr)
        return 2
    try:
        doc = run(spec, record, Path(a.out).resolve(), times, cams, Bridge(link=link), a.settle, force=a.force, missing=missing)
    except BridgeError as e:
        print(f'error: {e}', file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        print('interrupted', file=sys.stderr)
        return 2
    finally:
        link.close()
    return 1 if doc['failures'] or missing else 0


if __name__ == '__main__':
    sys.exit(main())
