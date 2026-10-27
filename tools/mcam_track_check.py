"""Subject tracking check of a replay take (MULTICAM.md 6.6): where each output camera's subject sits in what the viewer sees.
Per captured step it projects the subject point of poses.csv (subj_x/y/z: the tracked point before damping and limits, written
by cores from R3l on; a director row carries its source camera's subject, blend rows are skipped) into the camera and measures
it against the part of the frame each output shows (spec compose layouts: band crops and cell aspects, pans; angle videos
the full frame). FAIL when it stays beyond --fail (0.8) of the visible half-frame for more than --hold (0.3) output s, WARN
beyond --warn (0.65); behind the camera counts as beyond. Writes <take>/track_check.txt and .json; exit 1 on a FAIL.
--subject "<tracks.csv target>" measures that target instead (takes before R3l, or one subject for all outputs).
Usage: python -I tools/mcam_track_check.py <take> [--subject "huddle .visual"] [--cams a,b] [--fail 0.8] [--warn 0.65] [--hold 0.3]"""
from __future__ import annotations

import argparse
import csv
import json
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

SRC_W, SRC_H = 2560, 1440


def rot(q: tuple, v: tuple) -> tuple:
    x, y, z, w = q
    vx, vy, vz = v
    tx, ty, tz = 2 * (y * vz - z * vy), 2 * (z * vx - x * vz), 2 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty), vy + w * ty + (z * tx - x * tz), vz + w * tz + (x * ty - y * tx))


def project(row: dict, s: tuple, aspect: float) -> tuple[float, float] | None:
    p = (float(row['x']), float(row['y']), float(row['z']))
    q = tuple(float(row[k]) for k in ('qx', 'qy', 'qz', 'qw'))
    d = tuple(s[i] - p[i] for i in range(3))
    r, u, f = rot(q, (1, 0, 0)), rot(q, (0, 1, 0)), rot(q, (0, 0, 1))
    zc = sum(d[i] * f[i] for i in range(3))
    if zc <= 0.05:
        return None
    t = math.tan(math.radians(float(row['fov'])) / 2)
    return sum(d[i] * r[i] for i in range(3)) / (zc * t * aspect), sum(d[i] * u[i] for i in range(3)) / (zc * t)


def outputs(spec: dict, cams_in_take: set[str], only: list[str] | None) -> list[dict]:
    out = []
    comp = spec.get('compose') or {}
    try:
        import mcam_compose as MC
        geometry = MC.layout_geometry
        view = MC.view_rect
    except Exception:
        geometry = view = None
    for lay in comp.get('layouts') or []:
        if geometry is None:
            break
        try:
            _, _, cells = geometry(lay, {}, (SRC_W, SRC_H))
        except SystemExit:
            continue
        for c in cells:
            if c.cam in cams_in_take:
                out.append({'name': f"{lay.get('name', 'layout')}:{c.cam}", 'cam': c.cam, 'w': c.w, 'h': c.h, 'crop': c.crop,
                            'anchor': c.anchor, 'pan': c.pan, 'view': view})
    if 'director' in cams_in_take:
        out.append({'name': 'angle:director', 'cam': 'director', 'w': SRC_W, 'h': SRC_H, 'crop': None, 'anchor': None, 'pan': None, 'view': None})
    if only:
        out = [o for o in out if o['cam'] in only or o['name'] in only]
    return out


def window(o: dict, t: float) -> tuple[float, float, float, float]:
    if o['view'] is None:
        return 0.0, 1.0, 0.0, 1.0
    sx, sy, sw, sh = o['view'](SRC_W, SRC_H, o['w'], o['h'], o['crop'], o['anchor'], o['pan'], t)
    u0, u1 = 2 * sx / SRC_W - 1, 2 * (sx + sw) / SRC_W - 1
    v0, v1 = 1 - 2 * (sy + sh) / SRC_H, 1 - 2 * sy / SRC_H
    return (u0 + u1) / 2, (u1 - u0) / 2, (v0 + v1) / 2, (v1 - v0) / 2


def runs(flags: list[bool]) -> list[tuple[int, int]]:
    r, start = [], None
    for i, f in enumerate(flags + [False]):
        if f and start is None:
            start = i
        elif not f and start is not None:
            r.append((start, i - 1))
            start = None
    return r


def check(take: Path, subject: str | None, only: list[str] | None, fail: float, warn: float, hold: float) -> tuple[list[str], dict, int]:
    spec = json.loads((take / 'spec.json').read_text(encoding='utf-8')) if (take / 'spec.json').exists() else {}
    fps = float((spec.get('compose') or {}).get('fps') or (spec.get('timing') or {}).get('fps') or 30)
    meta = {}
    with open(take / 'meta.csv', newline='', encoding='utf-8') as fh:
        for row in csv.DictReader(fh):
            meta[int(row['step'])] = (float(row['game_time']), row.get('captured', '1') == '1')
    steps = sorted(s for s, (_, cap) in meta.items() if cap)
    out_t = {s: i / fps for i, s in enumerate(steps)}
    fixed = {}
    if subject:
        with open(take / 'tracks.csv', newline='', encoding='utf-8') as fh:
            for row in csv.DictReader(fh):
                if row['target'] == subject and row['ok'] == '1':
                    fixed[int(row['step'])] = (float(row['x']), float(row['y']), float(row['z']))
    poses: dict[str, dict[int, dict]] = {}
    has_subj = False
    with open(take / 'poses.csv', newline='', encoding='utf-8') as fh:
        rd = csv.DictReader(fh)
        has_subj = 'subj_x' in (rd.fieldnames or [])
        for row in rd:
            poses.setdefault(row['camera'], {})[int(row['step'])] = row
    lines, doc, code = [], {'take': str(take), 'subject': subject or 'poses.csv subj', 'fail': fail, 'warn': warn, 'hold': hold, 'outputs': []}, 0
    if not subject and not has_subj:
        lines.append(f'track check {take}: poses.csv has no subj_* columns (a core before R3l): nothing to check (use --subject)')
        return lines, doc, 0
    lines.append(f'track check {take}: subject {"tracks.csv " + repr(subject) if subject else "poses.csv subj_*"}; FAIL beyond {fail} '
                 f'of the visible half-frame for more than {hold} s, WARN beyond {warn}')
    aspect = SRC_W / SRC_H
    nfail = nwarn = 0
    for o in outputs(spec, set(poses), only):
        rows = poses.get(o['cam'], {})
        fr, ts = [], []
        worst, worst_t, measured = 0.0, None, 0
        for s in steps:
            row = rows.get(s)
            t_out = out_t[s]
            f = None
            if row is not None and 'B' not in (row.get('flags') or ''):
                if subject:
                    sp = fixed.get(s)
                elif row.get('subj_x'):
                    sp = (float(row['subj_x']), float(row['subj_y']), float(row['subj_z']))
                else:
                    sp = None
                if sp is not None:
                    pr = project(row, sp, aspect)
                    cu, hu, cv, hv = window(o, t_out)
                    f = 9.0 if pr is None else max(abs(pr[0] - cu) / hu, abs(pr[1] - cv) / hv)
                    measured += 1
                    if f > worst:
                        worst, worst_t = f, meta[s][0]
            fr.append(f)
            ts.append(s)
        found = []
        for level, lim in (('FAIL', fail), ('WARN', warn)):
            for a, b in runs([f is not None and f > lim for f in fr]):
                dur = (b - a + 1) / fps
                if dur <= hold:
                    continue
                if level == 'WARN' and any(not (b < x[1] or a > x[2]) for x in found if x[0] == 'FAIL'):
                    continue
                peak = max(fr[a:b + 1])
                found.append((level, a, b, dur, peak))
        for level, a, b, dur, peak in found:
            g0, g1 = meta[ts[a]][0], meta[ts[b]][0]
            lines.append(f'{level:<4} {o["name"]:<22} subject beyond {fail if level == "FAIL" else warn} of the visible half-frame '
                         f'{g0:.2f}-{g1:.2f} s (output {out_t[ts[a]]:.2f}-{out_t[ts[b]]:.2f} s, {dur:.2f} s), peak {min(peak, 9.0):.2f}')
            nfail += level == 'FAIL'
            nwarn += level == 'WARN'
        over = sum(1 for f in fr if f is not None and f > warn)
        lines.append(f'     {o["name"]:<22} measured {measured} of {len(fr)} output frames, worst {min(worst, 9.0):.2f}'
                     + (f' at {worst_t:.2f} s' if worst_t is not None else '') + f', beyond {warn}: {over / max(1, measured) * 100:.1f} %')
        doc['outputs'].append({'output': o['name'], 'camera': o['cam'], 'measured': measured, 'frames': len(fr), 'worst': min(worst, 9.0),
                               'worst_t': worst_t, 'runs': [{'level': l, 'from': meta[ts[a]][0], 'to': meta[ts[b]][0], 'seconds': d, 'peak': min(p, 9.0)}
                                                            for l, a, b, d, p in found]})
    code = 1 if nfail else 0
    doc.update({'fails': nfail, 'warnings': nwarn, 'exit': code})
    lines.insert(1, f'{nfail} FAIL, {nwarn} WARN -> exit {code}')
    return lines, doc, code


def run(take: Path, subject: str | None = None, only: list[str] | None = None, fail: float = 0.8, warn: float = 0.65, hold: float = 0.3,
        write: bool = True) -> tuple[list[str], dict, int]:
    lines, doc, code = check(take, subject, only, fail, warn, hold)
    if write:
        (take / 'track_check.txt').write_text('\n'.join(lines) + '\n', encoding='utf-8')
        (take / 'track_check.json').write_text(json.dumps(doc, indent=1), encoding='utf-8')
    return lines, doc, code


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument('take', type=Path)
    ap.add_argument('--subject', help='tracks.csv target to measure instead of the poses.csv subject (e.g. "huddle .visual")')
    ap.add_argument('--cams', help='only these cameras / outputs (comma list)')
    ap.add_argument('--fail', type=float, default=0.8)
    ap.add_argument('--warn', type=float, default=0.65)
    ap.add_argument('--hold', type=float, default=0.3)
    a = ap.parse_args(argv)
    lines, _, code = run(a.take, a.subject, a.cams.split(',') if a.cams else None, a.fail, a.warn, a.hold)
    print('\n'.join(lines))
    print(f'wrote {a.take / "track_check.txt"} and track_check.json')
    return code


if __name__ == '__main__':
    sys.exit(main())
