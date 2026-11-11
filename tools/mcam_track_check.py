"""Subject tracking check of a replay take (MULTICAM.md 6.6): where each output camera's subject sits in what the viewer sees.
Per output frame (meta.csv frame column; all its captured sub-steps count, the worst one decides) it projects the subject
point of poses.csv (subj_x/y/z: the tracked point before damping and limits, written by cores from R3l on; a director row
carries its source camera's subject, blend rows are skipped) into the camera and measures it against the part of the frame
each output shows (spec compose layouts: band crops and cell aspects, pans; full frame for cameras of mode director, the
cameras listed in compose.angles.cams and the ones named with --cams). FAIL when it stays beyond --fail (0.8) of the visible
half-frame for more than --hold (0.3) output s, WARN beyond --warn (0.65); behind the camera counts as beyond. The capture
size comes from --res, else run.json "screen", else session.res of the spec, else the first frame of a camera.
Writes <take>/track_check.txt and .json (--no-write: nothing; --csv: also track_check.csv with frame,game_time,output,offset).
Exit 0 no FAIL, 1 a FAIL, 2 nothing could be measured (unreadable take, no subject data, no output, unknown size).
--subject "<tracks.csv target>" measures that target instead (takes before R3l, or one subject for all outputs).
Usage: python -I tools/mcam_track_check.py <take> [--subject "huddle .visual"] [--cams a,b] [--fail 0.8] [--warn 0.65] [--hold 0.3]
       [--res WxH] [--no-write] [--csv] [--json]"""
from __future__ import annotations

import argparse
import csv
import json
import math
import os
import re
import shutil
import struct
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

FRAME_RE = re.compile(r'^\d+\.(jpg|jpeg|png)$', re.I)
EXIT_NOTHING = 2


def rot(q: tuple, v: tuple) -> tuple:
    x, y, z, w = q
    vx, vy, vz = v
    tx, ty, tz = 2 * (y * vz - z * vy), 2 * (z * vx - x * vz), 2 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty), vy + w * ty + (z * tx - x * tz), vz + w * tz + (x * ty - y * tx))


def num(row: dict, key: str) -> float | None:
    try:
        v = float(row.get(key) or '')
    except (TypeError, ValueError):
        return None
    return v if math.isfinite(v) else None


def basis(row: dict) -> tuple | None:
    q = tuple(num(row, k) for k in ('qx', 'qy', 'qz', 'qw'))
    if None not in q:
        return rot(q, (1, 0, 0)), rot(q, (0, 1, 0)), rot(q, (0, 0, 1))
    pitch, yaw = num(row, 'pitch'), num(row, 'yaw')
    if pitch is None or yaw is None:
        return None
    p, y, r = math.radians(pitch), math.radians(yaw), math.radians(num(row, 'roll') or 0.0)
    sp, cp, sy, cy, sr, cr = math.sin(p), math.cos(p), math.sin(y), math.cos(y), math.sin(r), math.cos(r)
    return ((cy * cr + sy * sp * sr, cp * sr, -sy * cr + cy * sp * sr), (-cy * sr + sy * sp * cr, cp * cr, sy * sr + cy * sp * cr),
            (sy * cp, -sp, cy * cp))


def project(row: dict, s: tuple, aspect: float) -> tuple[float, float] | None | bool:
    p = tuple(num(row, k) for k in ('x', 'y', 'z'))
    fov = num(row, 'fov')
    b = basis(row)
    if None in p or b is None or not fov or not 0 < fov < 179:
        return False
    r, u, f = b
    d = tuple(s[i] - p[i] for i in range(3))
    zc = sum(d[i] * f[i] for i in range(3))
    if zc <= 0.05:
        return None
    t = math.tan(math.radians(fov) / 2)
    return sum(d[i] * r[i] for i in range(3)) / (zc * t * aspect), sum(d[i] * u[i] for i in range(3)) / (zc * t)


def image_size(path: Path) -> tuple[int, int] | None:
    try:
        with path.open('rb') as fh:
            head = fh.read(65536)
    except OSError:
        return None
    if head[:8] == b'\x89PNG\r\n\x1a\n' and len(head) >= 24:
        w, h = struct.unpack('>II', head[16:24])
        return (w, h) if w > 0 and h > 0 else None
    if head[:2] != b'\xff\xd8':
        return None
    i, n = 2, len(head)
    while i + 9 < n:
        if head[i] != 0xFF:
            i += 1
            continue
        m = head[i + 1]
        if m in (0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF):
            h, w = (head[i + 5] << 8) | head[i + 6], (head[i + 7] << 8) | head[i + 8]
            return (w, h) if w > 0 and h > 0 else None
        if m in (0xD8, 0x01) or 0xD0 <= m <= 0xD7:
            i += 2
            continue
        i += 2 + ((head[i + 2] << 8) | head[i + 3])
    return None


def camera_size(take: Path, cam: str, run: dict) -> tuple[int, int] | None:
    folder = take / cam
    if folder.is_dir():
        try:
            with os.scandir(folder) as it:
                for e in it:
                    if FRAME_RE.match(e.name):
                        return image_size(Path(e.path))
                    if e.name.lower().endswith('.rgba'):
                        raw = run.get('raw') if isinstance(run.get('raw'), dict) else {}
                        w, h = int(raw.get('w') or 0), int(raw.get('h') or 0)
                        return (w, h) if w > 0 and h > 0 else None
        except (OSError, ValueError):
            return None
    video = take / f'{cam}.mkv'
    probe = os.environ.get('FFPROBE') or shutil.which('ffprobe')
    if video.is_file() and probe:
        try:
            p = subprocess.run([probe, '-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=width,height', '-of', 'csv=p=0', str(video)],
                               capture_output=True, text=True, timeout=30)
            w, h = (int(x) for x in p.stdout.strip().split(',')[:2])
            return (w, h) if w > 0 and h > 0 else None
        except (OSError, ValueError, subprocess.SubprocessError):
            return None
    return None


def parse_res(text: str) -> tuple[int, int]:
    m = re.fullmatch(r'\s*(\d+)\s*[xX*,:]\s*(\d+)\s*', text or '')
    if not m or int(m.group(1)) <= 0 or int(m.group(2)) <= 0:
        raise argparse.ArgumentTypeError(f"'{text}' is not WIDTHxHEIGHT (e.g. 1920x1080)")
    return int(m.group(1)), int(m.group(2))


def capture_size(take: Path, spec: dict, run: dict, cams: list[str], res: tuple[int, int] | None) -> tuple[tuple[int, int] | None, str]:
    if res:
        return res, '--res'
    sc = run.get('screen')
    if isinstance(sc, list) and len(sc) == 2 and all(isinstance(v, (int, float)) and v > 0 for v in sc):
        return (int(sc[0]), int(sc[1])), 'run.json screen'
    sr = (spec.get('session') or {}).get('res') if isinstance(spec.get('session'), dict) else None
    if isinstance(sr, list) and len(sr) == 2 and all(isinstance(v, (int, float)) and v > 0 for v in sr):
        return (int(sr[0]), int(sr[1])), 'spec session.res'
    for cam in cams:
        size = camera_size(take, cam, run)
        if size:
            return size, f'the frames of {cam}'
    return None, ''


def spec_cameras(spec: dict) -> dict[str, dict]:
    cams = spec.get('cameras') or []
    if isinstance(cams, dict):
        cams = [dict(v, name=v.get('name', k)) for k, v in cams.items() if isinstance(v, dict)]
    defaults = spec.get('defaults') if isinstance(spec.get('defaults'), dict) else {}
    return {str(c['name']): dict(defaults, **c) for c in cams if isinstance(c, dict) and c.get('name')}


def outputs(spec: dict, cams_in_take: set[str], only: list[str] | None, size: tuple[int, int], sizes: dict | None = None,
            problems: list[str] | None = None) -> list[dict]:
    out = []
    problems = problems if problems is not None else []
    comp = spec.get('compose') if isinstance(spec.get('compose'), dict) else {}
    layouts = [lay for lay in comp.get('layouts') or [] if isinstance(lay, dict)] if isinstance(comp.get('layouts'), list) else []
    geometry = view = None
    if layouts:
        try:
            import mcam_compose as MC
            geometry, view = MC.layout_geometry, MC.view_rect
        except Exception as e:
            problems.append(f'the {len(layouts)} compose layout(s) are not checked: mcam_compose cannot be imported ({type(e).__name__}: {e})')
    for lay in layouts if geometry is not None else []:
        try:
            _, _, cells = geometry(lay, sizes or {}, size)
        except (SystemExit, Exception) as e:
            problems.append(f"layout {lay.get('name', 'layout')} is not checked: {e}")
            continue
        for c in cells:
            if c.cam in cams_in_take:
                out.append({'name': f"{lay.get('name', 'layout')}:{c.cam}", 'cam': c.cam, 'w': c.w, 'h': c.h, 'crop': c.crop,
                            'anchor': c.anchor, 'pan': c.pan, 'view': view})
    full = [n for n, c in spec_cameras(spec).items() if c.get('mode') == 'director']
    angles = (comp.get('angles') or {}).get('cams') if isinstance(comp.get('angles'), dict) else None
    if isinstance(angles, list):
        full += [str(a) for a in angles]
    if 'director' in cams_in_take:
        full.append('director')
    have = {o['cam'] for o in out} | {o['name'] for o in out}
    full += [n for n in only or [] if n in cams_in_take and n not in have]
    seen = set()
    for cam in full:
        if cam in cams_in_take and cam not in seen:
            seen.add(cam)
            w, h = (sizes or {}).get(cam, size)
            out.append({'name': f'angle:{cam}', 'cam': cam, 'w': w, 'h': h, 'crop': None, 'anchor': None, 'pan': None, 'view': None})
    if only:
        out = [o for o in out if o['cam'] in only or o['name'] in only]
    return out


def window(o: dict, t: float, size: tuple[int, int]) -> tuple[float, float, float, float]:
    if o['view'] is None:
        return 0.0, 1.0, 0.0, 1.0
    src_w, src_h = size
    sx, sy, sw, sh = o['view'](src_w, src_h, o['w'], o['h'], o['crop'], o['anchor'], o['pan'], t)
    u0, u1 = 2 * sx / src_w - 1, 2 * (sx + sw) / src_w - 1
    v0, v1 = 1 - 2 * (sy + sh) / src_h, 1 - 2 * sy / src_h
    return (u0 + u1) / 2, max((u1 - u0) / 2, 1e-9), (v0 + v1) / 2, max((v1 - v0) / 2, 1e-9)


def runs(flags: list[bool]) -> list[tuple[int, int]]:
    r, start = [], None
    for i, f in enumerate(flags + [False]):
        if f and start is None:
            start = i
        elif not f and start is not None:
            r.append((start, i - 1))
            start = None
    return r


def read_json(path: Path) -> dict:
    if not path.is_file():
        return {}
    d = json.loads(path.read_text(encoding='utf-8-sig'))
    return d if isinstance(d, dict) else {}


def read_frames(take: Path) -> list[tuple[int, float, list[int]]]:
    rows = []
    with open(take / 'meta.csv', newline='', encoding='utf-8') as fh:
        rd = csv.DictReader(fh)
        has_frame = 'frame' in (rd.fieldnames or [])
        for row in rd:
            if (row.get('captured') or '1').strip() != '1':
                continue
            step, t = int(row['step']), float(row['game_time'])
            frame = int(float(row['frame'])) if has_frame and (row.get('frame') or '').strip() else -1
            rows.append((step, t, frame))
    rows.sort()
    if not has_frame or any(f < 0 for _, _, f in rows):
        return [(i, t, [s]) for i, (s, t, _) in enumerate(rows)]
    by: dict[int, list] = {}
    for s, t, f in rows:
        e = by.setdefault(f, [f, t, []])
        e[1] = t
        e[2].append(s)
    first = min(by) if by else 0
    return [(f - first, e[1], e[2]) for f, e in sorted(by.items())]


def check(take: Path, subject: str | None, only: list[str] | None, fail: float, warn: float, hold: float,
          res: tuple[int, int] | None = None) -> tuple[list[str], dict, int]:
    spec = read_json(take / 'spec.json')
    run_doc = read_json(take / 'run.json')
    comp = spec.get('compose') if isinstance(spec.get('compose'), dict) else {}
    timing = spec.get('timing') if isinstance(spec.get('timing'), dict) else {}
    fps = float(comp.get('fps') or timing.get('fps') or run_doc.get('fps') or 30)
    if not math.isfinite(fps) or fps <= 0:
        fps = 30.0
    frames = read_frames(take)
    step_t = {s: t for _, t, steps in frames for s in steps}
    fixed = {}
    if subject:
        with open(take / 'tracks.csv', newline='', encoding='utf-8') as fh:
            for row in csv.DictReader(fh):
                if row.get('target') == subject and row.get('ok') == '1':
                    fixed[int(row['step'])] = (float(row['x']), float(row['y']), float(row['z']))
    poses: dict[str, dict[int, dict]] = {}
    with open(take / 'poses.csv', newline='', encoding='utf-8') as fh:
        rd = csv.DictReader(fh)
        has_subj = 'subj_x' in (rd.fieldnames or [])
        for row in rd:
            poses.setdefault(row['camera'], {})[int(row['step'])] = row
    lines: list[str] = []
    doc = {'take': str(take), 'subject': subject or 'poses.csv subj', 'fail': fail, 'warn': warn, 'hold': hold, 'fps': fps, 'outputs': [],
           'problems': [], 'notes': [], 'offsets': []}
    if not subject and not has_subj:
        lines.append(f'track check {take}: poses.csv has no subj_* columns (a core before R3l): nothing to check (use --subject)')
        doc.update({'fails': 0, 'warnings': 0, 'exit': EXIT_NOTHING})
        return lines, doc, EXIT_NOTHING
    if subject and not fixed:
        lines.append(f"track check {take}: tracks.csv has no resolved row of target '{subject}': nothing to check")
        doc.update({'fails': 0, 'warnings': 0, 'exit': EXIT_NOTHING})
        return lines, doc, EXIT_NOTHING
    size, source = capture_size(take, spec, run_doc, sorted(poses), res)
    if size is None:
        lines.append(f'track check {take}: the capture size is not known (no run.json screen, no session.res, no frame to read): give --res WxH')
        doc.update({'fails': 0, 'warnings': 0, 'exit': EXIT_NOTHING})
        return lines, doc, EXIT_NOTHING
    doc['res'], doc['res_source'] = list(size), source
    lines.append(f'track check {take}: subject {"tracks.csv " + repr(subject) if subject else "poses.csv subj_*"}; FAIL beyond {fail} '
                 f'of the visible half-frame for more than {hold} s, WARN beyond {warn}; capture {size[0]}x{size[1]} ({source})')
    sizes = {}
    if not res:
        for cam in poses:
            cs = camera_size(take, cam, run_doc) if (take / cam).is_dir() else None
            if cs and cs != size:
                sizes[cam] = cs
    nfail = nwarn = 0
    for o in outputs(spec, set(poses), only, size, sizes, doc['problems']):
        rows = poses.get(o['cam'], {})
        src = sizes.get(o['cam'], size)
        aspect = src[0] / src[1]
        fr: list[float | None] = []
        worst, worst_t, measured, unreadable = 0.0, None, 0, 0
        for idx, t_frame, steps in frames:
            f = None
            for s in steps:
                row = rows.get(s)
                if row is None or 'B' in (row.get('flags') or ''):
                    continue
                if subject:
                    sp = fixed.get(s)
                else:
                    sp = tuple(num(row, k) for k in ('subj_x', 'subj_y', 'subj_z'))
                    sp = None if None in sp else sp
                if sp is None:
                    continue
                pr = project(row, sp, aspect)
                if pr is False:
                    unreadable += 1
                    continue
                cu, hu, cv, hv = window(o, step_t[s], src)
                fs = 9.0 if pr is None else max(abs(pr[0] - cu) / hu, abs(pr[1] - cv) / hv)
                if f is None or fs > f:
                    f = fs
                if fs > worst:
                    worst, worst_t = fs, step_t[s]
            if f is not None:
                measured += 1
                doc['offsets'].append((idx, round(t_frame, 4), o['name'], round(min(f, 9.0), 4)))
            fr.append(f)
        if unreadable:
            doc['notes'].append(f"{o['name']}: {unreadable} pose row(s) without a usable rotation or fov, skipped")
        found = []
        for level, lim in (('FAIL', fail), ('WARN', warn)):
            for a, b in runs([f is not None and f > lim for f in fr]):
                dur = (frames[b][0] - frames[a][0] + 1) / fps
                if dur <= hold:
                    continue
                if level == 'WARN' and any(not (b < x[1] or a > x[2]) for x in found if x[0] == 'FAIL'):
                    continue
                peak = max(v for v in fr[a:b + 1] if v is not None)
                found.append((level, a, b, dur, peak))
        for level, a, b, dur, peak in found:
            g0, g1 = step_t[frames[a][2][0]], frames[b][1]
            lines.append(f'{level:<4} {o["name"]:<22} subject beyond {fail if level == "FAIL" else warn} of the visible half-frame '
                         f'{g0:.2f}-{g1:.2f} s (output {frames[a][0] / fps:.2f}-{frames[b][0] / fps:.2f} s, {dur:.2f} s), peak {min(peak, 9.0):.2f}')
            nfail += level == 'FAIL'
            nwarn += level == 'WARN'
        over = sum(1 for f in fr if f is not None and f > warn)
        lines.append(f'     {o["name"]:<22} measured {measured} of {len(fr)} output frames, worst {min(worst, 9.0):.2f}'
                     + (f' at {worst_t:.2f} s' if worst_t is not None else '') + f', beyond {warn}: {over / max(1, measured) * 100:.1f} %')
        doc['outputs'].append({'output': o['name'], 'camera': o['cam'], 'measured': measured, 'frames': len(fr), 'worst': min(worst, 9.0),
                               'worst_t': worst_t,
                               'runs': [{'level': lv, 'from': step_t[frames[a][2][0]], 'to': frames[b][1], 'seconds': d, 'peak': min(p, 9.0)}
                                        for lv, a, b, d, p in found]})
    for p in doc['problems'] + doc['notes']:
        lines.append(f'note {p}')
    code = 1 if nfail else 0
    summary = f'{nfail} FAIL, {nwarn} WARN -> exit {code}'
    if not nfail and (not doc['outputs'] or all(o['measured'] == 0 for o in doc['outputs'])):
        code = EXIT_NOTHING
        why = ('no output to check: no compose.layouts cell, no director camera and no compose.angles list (name cameras with --cams)'
               if not doc['outputs'] else 'no output frame has a subject point and a pose')
        summary = f'nothing measured ({why}) -> exit {code}'
    elif not nfail and doc['problems']:
        code = EXIT_NOTHING
        summary = f'{nfail} FAIL, {nwarn} WARN, but some layouts could not be checked -> exit {code}'
    doc.update({'fails': nfail, 'warnings': nwarn, 'exit': code})
    lines.insert(1, summary)
    return lines, doc, code


def run(take: Path, subject: str | None = None, only: list[str] | None = None, fail: float = 0.8, warn: float = 0.65, hold: float = 0.3,
        write: bool = True, res: tuple[int, int] | None = None, offsets_csv: bool = False) -> tuple[list[str], dict, int]:
    take = Path(take)
    lines, doc, code = check(take, subject, only, fail, warn, hold, res)
    offsets = doc.pop('offsets', [])
    if write:
        (take / 'track_check.txt').write_text('\n'.join(lines) + '\n', encoding='utf-8')
        (take / 'track_check.json').write_text(json.dumps(doc, indent=1), encoding='utf-8')
    if offsets_csv:
        with open(take / 'track_check.csv', 'w', newline='', encoding='utf-8') as fh:
            w = csv.writer(fh)
            w.writerow(['frame', 'game_time', 'output', 'offset'])
            w.writerows(offsets)
    return lines, doc, code


def main(argv: list[str] | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding='utf-8', errors='replace')
        except (AttributeError, ValueError):
            pass
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('take', type=Path)
    ap.add_argument('--subject', help='tracks.csv target to measure instead of the poses.csv subject (e.g. "huddle .visual")')
    ap.add_argument('--cams', help='only these cameras / outputs (comma list); a camera without an output is measured full frame')
    ap.add_argument('--fail', type=float, default=0.8)
    ap.add_argument('--warn', type=float, default=0.65)
    ap.add_argument('--hold', type=float, default=0.3)
    ap.add_argument('--res', type=parse_res, default=None, help='capture size WIDTHxHEIGHT (default: run.json screen, session.res, a frame)')
    ap.add_argument('--no-write', action='store_true', help='do not write track_check.txt / .json into the take')
    ap.add_argument('--csv', action='store_true', help='also write <take>/track_check.csv: frame,game_time,output,offset')
    ap.add_argument('--json', action='store_true', help='print the result document instead of the text')
    a = ap.parse_args(argv)
    try:
        if not a.take.is_dir():
            raise OSError(f'take folder not found: {a.take}')
        lines, doc, code = run(a.take, a.subject, [c.strip() for c in a.cams.split(',') if c.strip()] if a.cams else None, a.fail, a.warn,
                               a.hold, not a.no_write, a.res, a.csv)
    except (OSError, KeyError, ValueError, csv.Error) as e:
        what = f'missing column {e}' if isinstance(e, KeyError) else str(e)
        print(f'track check {a.take}: cannot read the take: {what}', file=sys.stderr)
        return EXIT_NOTHING
    if a.json:
        print(json.dumps(doc, indent=1))
    else:
        print('\n'.join(lines))
        if not a.no_write:
            print(f'wrote {a.take / "track_check.txt"} and track_check.json' + (' and track_check.csv' if a.csv else ''))
    return code


if __name__ == '__main__':
    sys.exit(main())
