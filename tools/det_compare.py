"""Input replay determinism compare (REPLAY_DESIGN 7.5, 7.6) for InsideDev.Det run folders (steps.csv, events.csv, meta.json).
compare: one run against a reference, aligned by logic step k: PASS strict / PASS film / FAIL, k*, rng split, culprit window.
matrix: the standard runs of a root folder (S1-S5, P1, C1-C4, F1, V1) against their references, then the 7.6 decision rule.
Usage: python -I tools/det_compare.py compare <run> <ref> [--json f] | matrix <root> [--out f] [--json f] | selftest"""
from __future__ import annotations

import argparse
import csv
import json
import math
import sys
import tempfile
from dataclasses import asdict, dataclass, field
from pathlib import Path

POS_STRICT = 1e-4
FILM_BOY = 0.005
FILM_BALE = 0.01
FILM_BALE_DEG = 0.5
FILM_CHICK = 0.02
AFTER_LAND = 210
CHICK_AFTER_LAND = 60
EVENT_KINDS = ('baleFall', 'baleLand', 'shot', 'baleHit', 'kill')
RNG_PROBES = ('rng_fixed', 'rng_update', 'rng_late')
INPUT_COLS = ('in_x', 'in_y', 'in_jump', 'in_action')
STRICT, FILM, FAIL = 'PASS strict', 'PASS film', 'FAIL'
CULPRIT = {
    'rng_fixed': 'between the lateUpdate hook (-400) of the previous frame and the fixedUpdate hook of step k: LateUpdate after '
                 '-400 (UmbrellaExecutive: CrowdDude, Hair, FakeRagdollJoint2; HuddleLegControl, HuddleArmControl2), rendering '
                 'callbacks (OnWillRenderObject, OnPreCull/OnPreRender), FixedUpdate before -400 (GameManager input, '
                 'SoundEngine, GlobalAudio)',
    'rng_update': 'FixedUpdate phase of step k after the -400 hook: LogicManager, Chicken, ChickenGunLogic, HayBale, Rope, '
                  'VfxParticleController, physics callbacks (OnTrigger/OnCollision) and the det cues of step k',
    'rng_late': 'Update phase of the frame: Update scripts (ParticleSound, CameraScript, PlayMakerFSM), coroutines, legacy '
                'Animation events, LateUpdate before -400 (GameManager.UpdateTimeScale, SoundEngine, GlobalAudio)',
}
PAIRS = (
    ('S2', 'S1', 'base'), ('S3', 'S1', 'base'), ('S4', 'S1', 'info'), ('S5', 'S4', 'full'), ('P1', 'S1', 'camera'),
    ('C1', 'S1', 'camera'), ('C2', 'S1', 'camera'), ('C3', 'S4', 'full'), ('C4', 'S4', 'full'), ('F1', 'S4', 'fps'),
    ('V1', 'S4', 'v2'),
)


class RunError(Exception):
    pass


def num(s: str | None) -> float:
    if s is None or s == '':
        return math.nan
    try:
        return float(s)
    except ValueError:
        return math.nan


@dataclass
class Run:
    path: Path
    meta: dict
    header: list[str]
    rows: dict[int, dict[str, str]]
    events: list[tuple[int, str, str, str]]
    chicks: list[str]
    culls: list[str]

    @property
    def name(self) -> str:
        return self.path.name

    def first(self, kind: str) -> int:
        return next((k for k, kd, _, _ in self.events if kd == kind), -1)

    def kinds(self, kinds: tuple[str, ...], k_max: int) -> list[tuple[int, str]]:
        return [(k, kd) for k, kd, _, _ in self.events if kd in kinds and k <= k_max]

    def killed(self, k_max: int) -> list[str]:
        return [n for k, kd, n, _ in self.events if kd == 'kill' and k <= k_max]

    def chick_count(self) -> int:
        n = 0
        while f'c{n:02d}_x' in self.header:
            n += 1
        return n


def read_tsv_paths(path: Path) -> list[str]:
    if not path.is_file():
        return []
    lines = path.read_text(encoding='utf-8').splitlines()[1:]
    return [ln.split('\t', 1)[1] for ln in lines if '\t' in ln]


def load_run(path: Path) -> Run:
    steps = path / 'steps.csv'
    if not steps.is_file():
        raise RunError(f'{path}: no steps.csv')
    with steps.open(encoding='utf-8', newline='') as f:
        rd = csv.reader(f)
        header = next(rd, [])
        if 'k' not in header:
            raise RunError(f'{steps}: no k column')
        rows: dict[int, dict[str, str]] = {}
        for r in rd:
            if len(r) < len(header):
                continue
            d = dict(zip(header, r))
            try:
                rows[int(d['k'])] = d
            except ValueError:
                continue
    events: list[tuple[int, str, str, str]] = []
    ev = path / 'events.csv'
    if ev.is_file():
        with ev.open(encoding='utf-8', newline='') as f:
            rd = csv.reader(f)
            next(rd, None)
            for r in rd:
                if len(r) >= 2:
                    try:
                        events.append((int(r[0]), r[1], r[2] if len(r) > 2 else '', r[3] if len(r) > 3 else ''))
                    except ValueError:
                        pass
    meta: dict = {}
    mp = path / 'meta.json'
    if mp.is_file():
        try:
            meta = json.loads(mp.read_text(encoding='utf-8'))
        except json.JSONDecodeError as e:
            meta = {'_error': str(e)}
    return Run(path, meta, header, rows, events, read_tsv_paths(path / 'chicks.tsv'), read_tsv_paths(path / 'culls.tsv'))


def vec(row: dict[str, str], prefix: str) -> tuple[float, float, float]:
    return num(row.get(prefix + 'x')), num(row.get(prefix + 'y')), num(row.get(prefix + 'z'))


def dist(a: tuple[float, ...], b: tuple[float, ...]) -> float:
    na, nb = any(math.isnan(v) for v in a), any(math.isnan(v) for v in b)
    if na and nb:
        return math.nan
    if na or nb:
        return math.inf
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def quat_angle_deg(a: tuple[float, ...], b: tuple[float, ...]) -> float:
    if any(math.isnan(v) for v in a + b):
        return math.nan
    na = math.sqrt(sum(v * v for v in a)) or 1.0
    nb = math.sqrt(sum(v * v for v in b)) or 1.0
    a = tuple(v / na for v in a)
    b = tuple(v / nb for v in b)
    d = min(math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b))), math.sqrt(sum((x + y) ** 2 for x, y in zip(a, b))))
    return math.degrees(4.0 * math.asin(min(1.0, d / 2.0)))


@dataclass
class Result:
    run: str
    ref: str
    verdict: str = FAIL
    reasons: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)
    window_end: int = -1
    chick_window_end: int = -1
    rows_compared: int = 0
    rows_missing: int = 0
    k_star: int = -1
    k_rng: dict[str, int] = field(default_factory=lambda: {p: -1 for p in RNG_PROBES})
    k_pos: int = -1
    k_input: int = -1
    k_subs: int = -1
    k_cull: int = -1
    k_fixed_n: int = -1
    max_boy: float = 0.0
    max_bale: float = 0.0
    max_bale_deg: float = 0.0
    max_chick: float = 0.0
    max_chick_film: float = 0.0
    worst_chick: str = ''
    events: dict[str, list[list[int]]] = field(default_factory=dict)
    shots: list[int] = field(default_factory=lambda: [0, 0])
    killed: list[str] = field(default_factory=lambda: ['', ''])
    culprit: str = ''

    @property
    def passed(self) -> bool:
        return self.verdict != FAIL


def chick_map(run: Run, ref: Run, res: Result) -> list[tuple[int, int, str]]:
    n_ref, n_run = ref.chick_count(), run.chick_count()
    names_ref = ref.chicks if len(ref.chicks) == n_ref else [f'chick {i}' for i in range(n_ref)]
    if ref.chicks and run.chicks and ref.chicks != run.chicks:
        idx = {p: j for j, p in enumerate(run.chicks)}
        out = [(i, idx[p], p) for i, p in enumerate(ref.chicks) if p in idx]
        lost = [p for p in ref.chicks if p not in idx]
        res.notes.append(f'chick lists differ ({n_ref} vs {n_run}); matched by path, {len(lost)} unmatched')
        if lost:
            res.reasons.append(f'{len(lost)} chick(s) of the reference missing in the run: {", ".join(lost[:3])}')
        return out
    if n_ref != n_run:
        res.reasons.append(f'chick count differs: ref {n_ref}, run {n_run}')
    return [(i, i, names_ref[i]) for i in range(min(n_ref, n_run))]


def first_set(cur: int, k: int) -> int:
    return k if cur < 0 else cur


def compare(run: Run, ref: Run) -> Result:
    res = Result(run.name, ref.name)
    if not ref.rows:
        res.reasons.append('reference has no rows')
        return res
    land_ref = ref.first('baleLand')
    res.window_end = land_ref + AFTER_LAND if land_ref >= 0 else max(ref.rows)
    res.chick_window_end = land_ref + CHICK_AFTER_LAND if land_ref >= 0 else res.window_end
    if land_ref < 0:
        res.notes.append('reference has no baleLand: window = every reference row; PASS film needs it')
    pm_run, pm_ref = run.meta.get('prep', {}), ref.meta.get('prep', {})
    if pm_run and pm_ref and pm_run != pm_ref:
        res.notes.append(f'prep differs: run {pm_run}, ref {pm_ref}')
    for key in ('fps', 'load'):
        if run.meta.get(key) != ref.meta.get(key):
            res.notes.append(f'{key} differs: run {run.meta.get(key)}, ref {ref.meta.get(key)}')
    cam_run, cam_ref = run.meta.get('cam', {}).get('mode'), ref.meta.get('cam', {}).get('mode')
    if cam_run != cam_ref:
        res.notes.append(f'camera: run {cam_run}, ref {cam_ref}')
    if run.culls and ref.culls and run.culls != ref.culls:
        res.notes.append(f'culler group lists differ ({len(run.culls)} vs {len(ref.culls)}): cull_bits not comparable')
    for w in run.meta.get('warnings', []):
        res.notes.append(f'run warning: {w}')
    chicks = chick_map(run, ref, res)
    window = [k for k in sorted(ref.rows) if k <= res.window_end]
    missing = [k for k in window if k not in run.rows]
    res.rows_missing = len(missing)
    if missing:
        res.reasons.append(f'{len(missing)} reference step(s) missing in the run (first k {missing[0]}, run ends at k '
                           f'{max(run.rows) if run.rows else -1}: {run.meta.get("end", "?")})')
    cull_ok = not (run.culls and ref.culls and run.culls != ref.culls)
    chick_max: dict[str, float] = {}
    for k in window:
        a = run.rows.get(k)
        if a is None:
            continue
        b = ref.rows[k]
        res.rows_compared += 1
        rng_diff = False
        for p in RNG_PROBES:
            va, vb = a.get(p, ''), b.get(p, '')
            if va != '' and vb != '' and va != vb:
                res.k_rng[p] = first_set(res.k_rng[p], k)
                rng_diff = True
        if any(a.get(c, '') != b.get(c, '') for c in INPUT_COLS):
            res.k_input = first_set(res.k_input, k)
        if a.get('subs_loaded') != b.get('subs_loaded') or a.get('subs_active') != b.get('subs_active'):
            res.k_subs = first_set(res.k_subs, k)
        if cull_ok and a.get('cull_bits') != b.get('cull_bits'):
            res.k_cull = first_set(res.k_cull, k)
        if a.get('fixed_n') != b.get('fixed_n'):
            res.k_fixed_n = first_set(res.k_fixed_n, k)
        dists = [dist(vec(a, 'boy_'), vec(b, 'boy_')), dist(vec(a, 'bale_'), vec(b, 'bale_'))]
        if not math.isnan(dists[0]):
            res.max_boy = max(res.max_boy, dists[0])
        if not math.isnan(dists[1]):
            res.max_bale = max(res.max_bale, dists[1])
        qa = tuple(num(a.get(f'bale_q{c}')) for c in 'xyzw')
        qb = tuple(num(b.get(f'bale_q{c}')) for c in 'xyzw')
        ang = quat_angle_deg(qa, qb)
        if not math.isnan(ang):
            res.max_bale_deg = max(res.max_bale_deg, ang)
        for i_ref, i_run, cname in chicks:
            d = dist(vec(a, f'c{i_run:02d}_'), vec(b, f'c{i_ref:02d}_'))
            if math.isnan(d):
                continue
            dists.append(d)
            chick_max[cname] = max(chick_max.get(cname, 0.0), d)
            res.max_chick = max(res.max_chick, d)
            if k <= res.chick_window_end:
                res.max_chick_film = max(res.max_chick_film, d)
        if any(d > POS_STRICT for d in dists if not math.isnan(d)):
            res.k_pos = first_set(res.k_pos, k)
        if rng_diff or res.k_pos == k:
            res.k_star = first_set(res.k_star, k)
    if chick_max:
        res.worst_chick = max(chick_max, key=chick_max.get)
    for kind in EVENT_KINDS:
        res.events[kind] = [[k for k, kd in ref.kinds((kind,), res.window_end)], [k for k, kd in run.kinds((kind,), res.window_end)]]
    res.shots = [len(res.events['shot'][0]), len(res.events['shot'][1])]
    kr, kn = ref.killed(res.window_end), run.killed(res.window_end)
    res.killed = [','.join(kr), ','.join(kn)]
    res.culprit = culprit(res)
    strict = (not missing and all(v < 0 for v in res.k_rng.values()) and res.k_pos < 0
              and all(res.events[kd][0] == res.events[kd][1] for kd in EVENT_KINDS) and res.rows_compared > 0)
    film_reasons = []
    if missing:
        film_reasons.append('run does not cover the window')
    if land_ref < 0:
        film_reasons.append('no reference baleLand')
    if res.max_boy >= FILM_BOY:
        film_reasons.append(f'boy {mm(res.max_boy)} >= {mm(FILM_BOY)}')
    if res.max_bale >= FILM_BALE:
        film_reasons.append(f'bale {mm(res.max_bale)} >= {mm(FILM_BALE)}')
    if res.max_bale_deg >= FILM_BALE_DEG:
        film_reasons.append(f'bale rotation {res.max_bale_deg:.3f} deg >= {FILM_BALE_DEG} deg')
    if res.max_chick_film >= FILM_CHICK:
        film_reasons.append(f'chick {mm(res.max_chick_film)} >= {mm(FILM_CHICK)} before baleLand + 1 s ({res.worst_chick})')
    for kd in ('baleFall', 'baleLand'):
        e = res.events[kd]
        if (e[0][:1] or [-1]) != (e[1][:1] or [-1]):
            film_reasons.append(f'{kd} step {(e[1][:1] or [-1])[0]} != ref {(e[0][:1] or [-1])[0]}')
    if res.shots[0] != res.shots[1]:
        film_reasons.append(f'shot count {res.shots[1]} != ref {res.shots[0]}')
    film = not film_reasons and res.rows_compared > 0
    res.verdict = STRICT if strict else FILM if film else FAIL
    if res.verdict != STRICT:
        res.reasons.extend(film_reasons if res.verdict == FAIL else [])
        if res.verdict == FILM:
            res.notes.append('not strict: ' + strict_gap(res))
    return res


def strict_gap(res: Result) -> str:
    gaps = [f'{p} differs from k {k}' for p, k in res.k_rng.items() if k >= 0]
    if res.k_pos >= 0:
        gaps.append(f'a position differs > 1e-4 m from k {res.k_pos}')
    gaps += [f'{kd} steps differ' for kd in EVENT_KINDS if res.events[kd][0] != res.events[kd][1]]
    return '; '.join(gaps) or 'none'


def culprit(res: Result) -> str:
    order = [(k, i, p) for i, (p, k) in enumerate(res.k_rng.items()) if k >= 0]
    if order:
        k, _, p = min(order)
        return f'first random-stream divergence at k {k} in {p}: {CULPRIT[p]}'
    if res.k_pos >= 0:
        extra = ''
        if res.k_subs >= 0 and res.k_subs <= res.k_pos:
            extra = f'; loaded/active subscene counts differ from k {res.k_subs} (streaming)'
        elif res.k_cull >= 0 and res.k_cull <= res.k_pos:
            extra = f'; culler group activity differs from k {res.k_cull}'
        return f'positions diverge at k {res.k_pos} with identical random streams: physics or float order, not an extra draw{extra}'
    return ''


def mm(m: float) -> str:
    return 'inf' if math.isinf(m) else f'{m * 1000:.3f} mm'


def k_text(k: int) -> str:
    return '-' if k < 0 else str(k)


def report(res: Result) -> str:
    lines = [f'{res.run} vs {res.ref}: {res.verdict}',
             f'  window k <= {res.window_end} (chicks for film k <= {res.chick_window_end}); rows compared {res.rows_compared}, '
             f'missing {res.rows_missing}',
             f'  k* {k_text(res.k_star)}  (rng_fixed {k_text(res.k_rng["rng_fixed"])}, rng_update {k_text(res.k_rng["rng_update"])}, '
             f'rng_late {k_text(res.k_rng["rng_late"])}, pos > 1e-4 m {k_text(res.k_pos)})',
             f'  max boy {mm(res.max_boy)}, bale {mm(res.max_bale)} / {res.max_bale_deg:.4f} deg, chick {mm(res.max_chick)} '
             f'(film window {mm(res.max_chick_film)}, worst {res.worst_chick or "-"})',
             f'  first diff: inputs {k_text(res.k_input)}, subscenes {k_text(res.k_subs)}, culler groups {k_text(res.k_cull)}, '
             f'fixed steps per frame {k_text(res.k_fixed_n)}']
    ev = []
    for kd in EVENT_KINDS:
        a, b = res.events.get(kd, [[], []])
        if not a and not b:
            continue
        same = 'same' if a == b else 'DIFF'
        show = (lambda xs: ','.join(map(str, xs[:6])) + ('...' if len(xs) > 6 else ''))
        ev.append(f'{kd} ref [{show(a)}] run [{show(b)}] {same}')
    if ev:
        lines.append('  events: ' + '; '.join(ev))
    lines.append(f'  shots ref {res.shots[0]} run {res.shots[1]}; killed ref {res.killed[0] or "-"} run {res.killed[1] or "-"}')
    if res.culprit:
        lines.append('  culprit: ' + res.culprit)
    for r in res.reasons:
        lines.append('  reason: ' + r)
    for n in res.notes:
        lines.append('  note: ' + n)
    return '\n'.join(lines)


def to_json(res: Result) -> dict:
    d = asdict(res)
    for key in ('max_boy', 'max_bale', 'max_chick', 'max_chick_film'):
        if math.isinf(d[key]):
            d[key] = 'inf'
    return d


# ---------------------------------------------------------------- matrix and decision rule

def decide(results: dict[str, Result]) -> list[str]:
    def ok(name: str) -> bool | None:
        r = results.get(name)
        return None if r is None else r.passed

    def all_ok(names: list[str]) -> bool | None:
        vals = [ok(n) for n in names]
        if any(v is None for v in vals):
            return None
        return all(vals)

    out: list[str] = []
    s_ok, full_ok = all_ok(['S2', 'S3']), all_ok(['S5'])
    p1 = ok('P1')
    c_names = ['C1', 'C2']
    if s_ok and p1 is False:
        c_names = ['C1@P1', 'C2@P1']
        out.append('P1 fails while S passes: the divergence comes from paused camera logic (CameraScript state, '
                   'camera-relative audio and POI logic), not from the camera pose; C1/C2 are read against P1.')
    c_ok, c_full = all_ok(c_names), all_ok(['C3', 'C4'])
    f1 = ok('F1')
    if s_ok is None:
        return ['incomplete: S2 and S3 (vs S1) are required for a verdict'] + out
    if not s_ok and not full_ok:
        out.insert(0, 'B dropped: S1-S3 fail (base nondeterminism: streaming, PreAwake timing, PhysX) and the full prep '
                      'does not fix it (S5 vs S4 ' + ('missing' if full_ok is None else 'fails') + '); A only.')
        return out + culprit_lines(results)
    prep = 'default'
    if not s_ok and full_ok:
        out.append('S4/S5 pass, S1-S3 fail: the prep is mandatory (anim=always cull=freeze stream=keep drip=off).')
        prep = 'full'
        c_ok = None
    if s_ok and c_ok is False and c_full:
        out.append('S pass, C1/C2 fail, C3/C4 pass: B viable only with full prep and all subscenes kept loaded.')
        prep = 'full'
    if f1 is False:
        out.append('F1 fails: B runs must be exactly 60 captured fps; slow motion only through A.')
    elif f1:
        out.append('F1 passes: 120 captured fps reproduces the 60 fps run.')
    cams = c_ok if prep == 'default' else c_full
    strict_pairs = [n for n, r in results.items() if r.verdict == STRICT]
    needed = ['S2', 'S3'] + (c_names if prep == 'default' else ['S5', 'C3', 'C4'])
    intercut = all(results.get(n) is not None and results[n].verdict == STRICT for n in needed)
    if cams is None:
        final = f'B viable for the game camera with {prep} prep; free-camera runs ' + ('missing' if prep == 'default' else 'C3/C4 missing')
    elif not cams and prep == 'default' and c_full is None:
        final = 'B fails for free cameras with default prep (C1/C2); C3/C4 with full prep not run yet'
    elif not cams:
        final = 'B dropped for multi-camera filming: other camera poses change the simulation even with ' + prep + ' prep; A only'
    else:
        final = f'B viable with {prep} prep for free cameras' + (' (PASS strict: N runs can be intercut)' if intercut
                                                               else ' (film level only: not strict, runs cannot be intercut)')
    out.insert(0, final)
    out.append('strict pairs: ' + (', '.join(sorted(strict_pairs)) or 'none'))
    return out + culprit_lines(results)


def culprit_lines(results: dict[str, Result]) -> list[str]:
    return [f'{n}: k* {k_text(r.k_star)}: {r.culprit}' for n, r in sorted(results.items()) if r.culprit]


def matrix(root: Path) -> tuple[dict[str, Result], list[str]]:
    runs: dict[str, Run] = {}
    problems: list[str] = []
    for d in sorted(p for p in root.iterdir() if p.is_dir()):
        if (d / 'steps.csv').is_file():
            try:
                runs[d.name] = load_run(d)
            except RunError as e:
                problems.append(str(e))
    results: dict[str, Result] = {}
    for run, ref, _ in PAIRS:
        if run in runs and ref in runs:
            results[run] = compare(runs[run], runs[ref])
    s_ok = all(results.get(n) is not None and results[n].passed for n in ('S2', 'S3'))
    if s_ok and 'P1' in results and not results['P1'].passed:
        for c in ('C1', 'C2'):
            if c in runs:
                results[c + '@P1'] = compare(runs[c], runs['P1'])
    missing = [r for r, _, _ in PAIRS if r not in runs]
    if 'S1' not in runs:
        problems.append('no S1 (the reference)')
    if missing:
        problems.append('runs not found: ' + ', '.join(missing))
    return results, problems


def matrix_text(results: dict[str, Result], decision: list[str], problems: list[str]) -> str:
    lines = ['R13 input replay verdict: ' + (decision[0] if decision else 'no runs')]
    lines += ['  ' + d for d in decision[1:]]
    lines.append('')
    role = {r: g for r, _, g in PAIRS}
    lines.append(f'{"run":8} {"ref":4} {"role":6} {"verdict":12} {"k*":>6} {"boy":>11} {"bale":>11} {"deg":>8} {"chick":>11}')
    for n, r in results.items():
        lines.append(f'{n:8} {r.ref:4} {role.get(n.split("@")[0], "camera"):6} {r.verdict:12} {k_text(r.k_star):>6} {mm(r.max_boy):>11} '
                     f'{mm(r.max_bale):>11} {r.max_bale_deg:8.4f} {mm(r.max_chick_film):>11}')
    lines.append('  (S4 vs S1 is informational: the full prep changes the simulation on purpose)')
    for p in problems:
        lines.append('problem: ' + p)
    lines.append('')
    lines += [report(r) for r in results.values()]
    return '\n'.join(lines) + '\n'


# ---------------------------------------------------------------- selftest (synthetic runs)

def synth(path: Path, n: int = 600, land: int = 400, rng_shift_at: int = -1, boy_off: float = 0.0, chick_drift_at: int = -1,
          end: int | None = None, shots: int = 3) -> None:
    path.mkdir(parents=True, exist_ok=True)
    chicks = 2
    head = ['k', 'step', 'frame', 'rng_fixed', *INPUT_COLS, 'boy_x', 'boy_y', 'boy_z', 'bale_x', 'bale_y', 'bale_z',
            'bale_qx', 'bale_qy', 'bale_qz', 'bale_qw']
    for i in range(chicks):
        head += [f'c{i:02d}_x', f'c{i:02d}_y', f'c{i:02d}_z']
    head += ['cull_active', 'cull_bits', 'subs_loaded', 'subs_active', 'fixed_n', 'rng_update', 'rng_late']
    last = n if end is None else end
    with (path / 'steps.csv').open('w', encoding='utf-8', newline='') as f:
        w = csv.writer(f)
        w.writerow(head)
        for k in range(last):
            shift = 7 if 0 <= rng_shift_at <= k else 0
            row = [k, 1000 + k, 2000 + k, 11 * k + shift, 0, 0, 0, 0, 221.0 + boy_off, -221.4, 0.66,
                   230.0, -218.9 - (0.01 * max(0, k - 300) if k < land else 1.0), 0.0, 0, 0, 0, 1]
            for i in range(chicks):
                drift = 0.001 * (k - chick_drift_at) if 0 <= chick_drift_at <= k else 0.0
                row += [225.0 + i + 0.01 * k + drift, -221.0, 0.0]
            row += [1, '1', 12, 10, 1, 13 * k + shift, 17 * k + shift]
            w.writerow(row)
    ev = ['k,kind,name,detail', '-1,start,record,x'] + [f'{200 + 40 * i},shot,Chicken ({i}),chick {i}' for i in range(shots)]
    ev += [f'{land - 60},baleFall,HayBaleTrigger,probeEnter', f'{land},baleLand,HayBale,dv 3']
    (path / 'events.csv').write_text('\n'.join(ev) + '\n', encoding='utf-8')
    (path / 'chicks.tsv').write_text('i\tpath\n0\ta/Chicken (0)\n1\ta/Chicken (1)\n', encoding='utf-8')
    (path / 'culls.tsv').write_text('i\tpath\n0\tz/3_Interior_CULL\n', encoding='utf-8')
    meta = {'prep': {'anim': 'default', 'cull': 'live', 'stream': 'live', 'drip': 'on'}, 'fps': 60, 'load': [7, 0],
            'cam': {'mode': 'game'}, 'end': 'baleLand + 210', 'warnings': []}
    (path / 'meta.json').write_text(json.dumps(meta), encoding='utf-8')


def selftest() -> int:
    fails = []
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        synth(root / 'S1')
        synth(root / 'S2')
        synth(root / 'S3', boy_off=0.003)
        synth(root / 'S4', rng_shift_at=100, chick_drift_at=100)
        synth(root / 'S5', rng_shift_at=100, chick_drift_at=100)
        synth(root / 'C1', end=500)
        synth(root / 'C2', shots=4)
        synth(root / 'C3', rng_shift_at=100, chick_drift_at=100)
        synth(root / 'C4', rng_shift_at=100, chick_drift_at=100)
        synth(root / 'F1', rng_shift_at=150, chick_drift_at=150)
        runs = {n: load_run(root / n) for n in ('S1', 'S2', 'S3', 'S4', 'C1', 'C2', 'F1')}
        checks = [
            (compare(runs['S2'], runs['S1']), STRICT, 'identical run'),
            (compare(runs['S3'], runs['S1']), FILM, 'boy offset 3 mm'),
            (compare(runs['S4'], runs['S1']), FAIL, 'rng shift + chick drift 1 mm/step from k 100'),
            (compare(runs['C1'], runs['S1']), FAIL, 'run shorter than the window'),
            (compare(runs['C2'], runs['S1']), FAIL, 'one extra shot'),
        ]
        for res, want, what in checks:
            if res.verdict != want:
                fails.append(f'{what}: {res.verdict} != {want}\n{report(res)}')
        r = checks[2][0]
        if r.k_star != 100 or r.k_rng['rng_fixed'] != 100 or 'rng_fixed' not in r.culprit:
            fails.append(f'k*/culprit wrong: {r.k_star} {r.k_rng} {r.culprit}')
        if r.window_end != 610:
            fails.append(f'window end {r.window_end} != 610')
        results, problems = matrix(root)
        decision = decide(results)
        if not decision or not decision[0].startswith('B viable with full prep'):
            fails.append('decision: ' + ' | '.join(decision))
        if not any(line.startswith('S pass, C1/C2 fail, C3/C4 pass') for line in decision):
            fails.append('7.6 camera line missing: ' + ' | '.join(decision))
        if not any(line.startswith('F1 fails') for line in decision):
            fails.append('F1 line missing: ' + ' | '.join(decision))
        text = matrix_text(results, decision, problems)
        if 'R13 input replay verdict' not in text:
            fails.append('matrix text header missing')
    for f in fails:
        print('FAIL ' + f)
    print(f'selftest: {len(checks) + 5 - len(fails)} ok, {len(fails)} failed')
    return 1 if fails else 0


# ---------------------------------------------------------------- cli

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest='cmd', required=True)
    c = sub.add_parser('compare', help='one run against a reference')
    c.add_argument('run', type=Path)
    c.add_argument('ref', type=Path)
    c.add_argument('--json', type=Path)
    m = sub.add_parser('matrix', help='all standard runs of a root folder, then the 7.6 decision')
    m.add_argument('root', type=Path)
    m.add_argument('--out', type=Path, help='verdict text (default <root>/verdict.txt)')
    m.add_argument('--json', type=Path, help='results (default <root>/det_matrix.json)')
    sub.add_parser('selftest', help='synthetic runs, no game')
    return p


def main(argv: list[str] | None = None) -> int:
    a = build_parser().parse_args(argv)
    if a.cmd == 'selftest':
        return selftest()
    try:
        if a.cmd == 'compare':
            res = compare(load_run(a.run), load_run(a.ref))
            print(report(res))
            if a.json:
                a.json.write_text(json.dumps(to_json(res), indent=1), encoding='utf-8')
            return 0
        if not a.root.is_dir():
            raise RunError(f'{a.root}: not a folder')
        results, problems = matrix(a.root)
        decision = decide(results)
        text = matrix_text(results, decision, problems)
        out = a.out or a.root / 'verdict.txt'
        out.write_text(text, encoding='utf-8')
        js = a.json or a.root / 'det_matrix.json'
        js.write_text(json.dumps({'decision': decision, 'problems': problems,
                                  'results': {n: to_json(r) for n, r in results.items()}}, indent=1), encoding='utf-8')
        print(text, end='')
        print(f'written {out} and {js}')
        return 0
    except RunError as e:
        print(f'error: {e}', file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
