"""MultiCam QA: take checks with exit codes (qa: 0 ok, 1 errors, 2 unreadable take; replay takes add pass, recording,
timeline and view checks (views.csv: camera inside geometry, camera path through geometry); black (luma p50 < 2) and
near-black frames (more than --dark-cover of a camera frame, more than --cell-dark-cover (30 %) of a composed video's layout
cell below --dark-level/255) are errors with game / output timestamps; blurred frames of the composed videos per cell
(Laplacian variance below --blur-warn / --blur-fail of the 75th percentile within +-1.5 s) are warnings / errors), contact sheets with raw/graded A/B and cut storyboards (sheet), sync sheet of cues/events/cuts across
cameras (sync), camera path plots (paths), timeline plot (timeline), rehearsal-vs-capture track divergence (diverge), the
replay golden test (replaydiff: golden vs record game frames, PSNR/SSIM global and in tracks.csv bound masks, edge
silhouette distance at 99 % coverage judged for rope targets only (--sil-targets; reported for the rest), noise floor
min(N1, N120), REPLAY_DESIGN 9.2 R3; default output <take>/replaydiff) and the record audio sync check
(audiosync: lag-0 marker fit, slope, residuals, event markers and onsets, audio_track.wav duration and cross-correlation
against the record WAV, muxed video durations). Numeric checks only, audio is never played.
Usage: python -I tools/mcam_qa.py <qa|sheet|sync|paths|timeline|diverge> <take> [options]
       python -I tools/mcam_qa.py replaydiff <take> [--record <dir>] [--exclude <json>]
       python -I tools/mcam_qa.py audiosync <take> [--record <dir>] [--video <file>]"""
from __future__ import annotations

import argparse
import json
import math
import subprocess
import sys
from dataclasses import replace
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mcam_compose as MC
import mcam_take as MT
from mcam_take import Take, TakeError

THUMB = (384, 216)
BG = (18, 18, 20)
FG = (216, 210, 192)
DIM = (140, 136, 126)
RED = (220, 80, 70)
AMBER = (225, 170, 60)
GREEN = (120, 190, 120)
MT_PLAYER_Z = -2.0
DARK_LEVEL = 3
DARK_COVER = 0.5
CELL_DARK_COVER = 0.30
BLUR_WINDOW = 1.5
BLUR_WARN = 0.35
BLUR_FAIL = 0.2
BLUR_GAP = 3


def font(px: int, mono: bool = False) -> ImageFont.FreeTypeFont:
    path = MC.FONT_FILES['mono'] if mono else MC.FONT_FILES['oswald']
    if not path.is_file():
        path = MC.FONT_FILES['bahnschrift']
    try:
        return ImageFont.truetype(str(path), px)
    except OSError:
        return ImageFont.load_default()


def to8(img16: np.ndarray) -> np.ndarray:
    return (img16 >> 8).astype(np.uint8)


def thumb(ctx: MC.Ctx, cam: str, k: int, tl: MT.Timeline, size=THUMB) -> np.ndarray:
    return to8(MC.render_view(ctx, cam, tl[k], size[0], size[1]))


def raw_ctx(ctx: MC.Ctx) -> MC.Ctx:
    grades = {c: MC.CamGrade(c, MC.GradeSpec(off=True), 0.0) for c in ctx.take.cams}
    return replace(ctx, grades=grades)


def save(img: Image.Image, path: Path) -> Path:
    img.save(path, optimize=True)
    print(f'wrote {path}')
    return path


# ------------------------------------------------------------------------------------------------------------------- qa

class Report:
    def __init__(self):
        self.items: list[dict] = []

    def add(self, level: str, check: str, msg: str, **detail) -> None:
        self.items.append({'level': level, 'check': check, 'msg': msg, **({'detail': detail} if detail else {})})

    def count(self, level: str) -> int:
        return sum(1 for i in self.items if i['level'] == level)


def runs(idx: list[int]) -> list[tuple[int, int]]:
    out = []
    for i in sorted(idx):
        if out and i == out[-1][1] + 1:
            out[-1] = (out[-1][0], i)
        else:
            out.append((i, i))
    return out


def fmt_runs(r: list[tuple[int, int]], limit: int = 6) -> str:
    s = ', '.join(f'{a}' if a == b else f'{a}-{b}' for a, b in r[:limit])
    return s + (' ...' if len(r) > limit else '')


def time_runs(idx: list[int], time_of, limit: int = 8) -> str:
    r = runs(idx)
    parts = []
    for a, b in r[:limit]:
        ta, tb = time_of(a), time_of(b)
        parts.append(f'{ta:.2f} s' if a == b else f'{ta:.2f}-{tb:.2f} s')
    return ', '.join(parts) + (f' ... ({len(r)} runs)' if len(r) > limit else '')


def dark_frames(p50: np.ndarray, dark: np.ndarray, ok: np.ndarray, cover: float) -> tuple[np.ndarray, np.ndarray]:
    black = ok & (p50 < 2)
    near = ok & ~black & (dark > cover)
    return black, near


def blur_flags(var: np.ndarray, fps: float, window: float = BLUR_WINDOW, warn: float = BLUR_WARN, fail: float = BLUR_FAIL,
               skip: np.ndarray | None = None) -> tuple[list[int], list[int], np.ndarray]:
    n = len(var)
    ratio = np.ones(n)
    half = max(1, int(round(window * fps)))
    for i in range(n):
        lo, hi = max(0, i - half), min(n, i + half + 1)
        nb = np.concatenate([var[lo:max(lo, i - BLUR_GAP)], var[min(hi, i + BLUR_GAP + 1):hi]])
        if len(nb):
            ratio[i] = var[i] / max(float(np.percentile(nb, 75)), 1e-6)
    bad = np.zeros(n, bool) if skip is None else skip
    fails = [i for i in range(n) if ratio[i] < fail and not bad[i]]
    warns = [i for i in range(n) if fail <= ratio[i] < warn and not bad[i]]
    return warns, fails, ratio


def check_frames(ctx: MC.Ctx, rep: Report) -> dict:
    take = ctx.take
    for w in take.warnings:
        rep.add('warn', 'take', w)
    all_steps = sorted({s for f in take.base.frames for s in f.steps})
    union, inter = set(), None
    for c in take.cams.values():
        if c.ext == 'mkv':
            continue
        s = set(c.files)
        union |= s
        inter = s if inter is None else inter & s
    tail = sorted(s for s in union - (inter or set()) if s > (max(inter) if inter else -1))
    if tail:
        rep.add('warn', 'partial_last_step', f'{len(tail)} trailing step(s) exist only for some cameras (interrupted take)',
                steps=tail[:20])
    if take.kind != 'v1':
        cap = [int(s) for s, c in zip(take.step, take.captured) if c == 1]
        last = max(union) if union else -1
        beyond = [s for s in cap if s > last]
        if beyond:
            rep.add('warn', 'meta_beyond_frames', f'meta.csv lists {len(beyond)} captured steps without frame files', first=beyond[0])
    stats = {}
    for cam, c in take.cams.items():
        missing = [s for s in all_steps if c.ext != 'mkv' and s not in c.files]
        if missing:
            rep.add('error', 'missing_frames', f'{cam}: {len(missing)} missing frame files', steps=fmt_runs(runs(missing)))
        empty = [s for s, p in c.files.items() if p.stat().st_size == 0]
        if empty:
            rep.add('error', 'empty_frames', f'{cam}: {len(empty)} zero-size files', steps=empty[:20])
        st = MT.camera_stats(take, ctx.store, cam, all_steps, workers=MC.threads_of(ctx.args))
        stats[cam] = st
        bad = [int(s) for s, ok in zip(st['steps'], st['ok']) if not ok and int(s) in c.files]
        if bad:
            rep.add('error', 'corrupt_frames', f'{cam}: {len(bad)} frames do not decode', steps=fmt_runs(runs(bad)))
        level, cover = dark_args(ctx)
        hist = st['hist']
        dark = hist[:, :level].sum(1) / np.maximum(hist.sum(1), 1)
        bm, nm = dark_frames(st['p50'], dark, st['ok'], cover)
        idx_t = {int(s): float(t) for s, t in zip(take.step, take.t)}
        black = [int(s) for s in st['steps'][bm]]
        near = [int(s) for s in st['steps'][nm]]
        if black:
            rep.add('error', 'black_frames', f'{cam}: {len(black)} black frames (luma p50 < 2) at game '
                                             f'{time_runs(black, lambda s: idx_t.get(s, math.nan))}', steps=fmt_runs(runs(black)))
        if near:
            rep.add('error', 'near_black_frames', f'{cam}: {len(near)} near-black frames (more than {cover:.0%} of the frame below '
                                                  f'{level}/255) at game {time_runs(near, lambda s: idx_t.get(s, math.nan))}',
                    steps=fmt_runs(runs(near)))
        okm = st['ok']
        clip = float(np.mean(st['clip'][okm])) if okm.any() else 0.0
        crush = float(np.mean(st['crush'][okm])) if okm.any() else 0.0
        lvl = 'warn' if clip > 0.02 or crush > 0.25 else 'info'
        rep.add(lvl, 'levels', f'{cam}: clipped {clip * 100:.2f} %, crushed {crush * 100:.2f} %, median luma '
                               f'{float(np.median(st["p50"][okm])) if okm.any() else 0:.1f}, floor p0.5 '
                               f'{MT.pooled_percentile(st["hist"], 0.005):.0f}')
        lm = st['logmean']
        ev = np.log2(np.maximum(lm, 1e-7))
        jumps = [int(st['steps'][i]) for i in range(1, len(ev)) if okm[i] and okm[i - 1] and abs(ev[i] - ev[i - 1]) > 0.5]
        if jumps:
            rep.add('warn', 'exposure_jump', f'{cam}: {len(jumps)} frame-to-frame exposure jumps > 0.5 EV',
                    steps=fmt_runs(runs(jumps)))
    if len(stats) > 1 and all_steps:
        idx_t = {int(s): float(t) for s, t in zip(take.step, take.t)}
        frozen = []
        energy = np.stack([stats[c]['energy'] for c in stats])
        oks = np.stack([stats[c]['ok'] for c in stats])
        for i in range(1, len(all_steps)):
            s0, s1 = all_steps[i - 1], all_steps[i]
            if oks[:, i].all() and oks[:, i - 1].all() and (energy[:, i] < 0.01).all() and idx_t.get(s1, 0) > idx_t.get(s0, 0):
                frozen.append(s1)
        if frozen:
            rep.add('warn', 'frozen_steps', f'{len(frozen)} steps identical to the previous one on every camera while game time '
                                            f'advanced', steps=fmt_runs(runs(frozen)))
    if len(take.t) > 1:
        d = np.diff(take.t)
        clamped = (take.t[1:] <= 1e-9) if take.replay_mode else np.zeros(len(d), bool)
        stuck = [int(take.step[i + 1]) for i in np.flatnonzero((d <= 1e-7) & ~clamped)]
        if stuck:
            rep.add('error', 'no_advance', f'{len(stuck)} steps where game time did not increase', steps=fmt_runs(runs(stuck)))
        if take.kind != 'v1' and not take.replay_mode:
            r = take.rate[1:]
            dev = np.abs(d * r - 1.0)
            off = [int(take.step[i + 1]) for i in np.flatnonzero((dev > 0.25) & (d > 1e-7))]
            if off:
                rep.add('warn', 'step_dt', f'{len(off)} steps whose measured dt differs > 25 % from 1/rate (hitch, '
                                           f'maximumDeltaTime or rate change)', steps=fmt_runs(runs(off)))
    return stats


def check_poses(ctx: MC.Ctx, rep: Report) -> None:
    take = ctx.take
    poses = take.poses()
    if not poses:
        rep.add('info', 'poses', 'no poses.csv (v1 take): pose checks skipped')
        return
    game = poses.get('game')
    for cam, e in poses.items():
        mode = take.cams[cam].mode if cam in take.cams else ''
        n = len(e['step'])
        flags = e['flags']
        fb = [int(e['step'][i]) for i in range(n) if 'F' in flags[i]]
        if fb:
            rep.add('warn', 'fallback_poses', f'{cam}: {len(fb)} fallback poses (game pose captured instead)', steps=fmt_runs(runs(fb)))
        cl = [int(e['step'][i]) for i in range(n) if 'C' in flags[i]]
        if cl:
            rep.add('warn', 'clamped_poses', f'{cam}: {len(cl)} poses clamped to the player side', steps=fmt_runs(runs(cl)))
        if game is not None and cam != 'game' and mode not in ('game', 'director') and len(game['step']):
            gi = {int(s): i for i, s in enumerate(game['step'])}
            same = []
            for i in range(n):
                j = gi.get(int(e['step'][i]))
                if j is None or e['copy_of'][i]:
                    continue
                if abs(e['x'][i] - game['x'][j]) < 1e-3 and abs(e['y'][i] - game['y'][j]) < 1e-3 and abs(e['z'][i] - game['z'][j]) < 1e-3:
                    same.append(int(e['step'][i]))
            if same:
                rep.add('warn', 'equals_game', f'{cam}: {len(same)} poses equal to the game camera (silent fallback?)',
                        steps=fmt_runs(runs(same)))
        pitch = MT.signed_angle(e['pitch'])
        fwd = e['forward']
        behind = [int(e['step'][i]) for i in range(n) if e['z'][i] > MT_PLAYER_Z and pitch[i] < 45]
        back = [int(e['step'][i]) for i in range(n) if fwd[i, 2] < -0.05 and pitch[i] < 60]
        if behind:
            rep.add('error', 'player_side', f'{cam}{" (director path)" if mode == "director" else ""}: {len(behind)} poses behind '
                                            f'the line z > {MT_PLAYER_Z} without looking down', steps=fmt_runs(runs(behind)))
        if back:
            rep.add('error', 'looks_back', f'{cam}: {len(back)} poses looking back toward the screen (forward z < -0.05)',
                    steps=fmt_runs(runs(back)))
        check_motion(rep, cam, e, take.base)



def check_motion(rep: Report, cam: str, e: dict, tl: MT.Timeline) -> None:
    row = {int(s): i for i, s in enumerate(e['step'])}
    idx = [row.get(f.steps[-1], -1) for f in tl.frames]
    keep = [k for k, i in enumerate(idx) if i >= 0]
    if len(keep) < 8:
        return
    sel = np.array([idx[k] for k in keep])
    spd = np.array([tl.frames[k].speed for k in keep])
    p = np.stack([e['x'], e['y'], e['z']], 1)[sel]
    dt = 1.0 / tl.fps
    v = np.linalg.norm(np.diff(p, axis=0), axis=1) / dt
    vmax = float(np.percentile(v, 95))
    if vmax < 0.05:
        return
    moving = v > 0.2 * vmax
    win = max(2, int(round(0.5 * tl.fps)))
    dips = []
    for i in range(win, len(v) - win):
        if not (moving[i - win] and moving[i + win]) or np.ptp(spd[i - win:i + win + 2]) > 1e-6:
            continue
        ref = 0.5 * (v[i - win:i].mean() + v[i + 1:i + 1 + win].mean())
        if ref > 0.2 * vmax and v[i] < 0.5 * ref and v[i] == v[i - win:i + win + 1].min():
            dips.append(keep[i + 1])
    if dips:
        rep.add('warn', 'speed_dip', f'{cam}: {len(dips)} speed dips while moving (keys easing or follow stall), output frames',
                frames=fmt_runs(runs(dips)))
    if len(v) > 3:
        j = np.abs(np.diff(v, 2)) / dt ** 2
        med = float(np.median(j)) + 1e-6
        steady = np.array([np.ptp(spd[i:i + 4]) <= 1e-6 for i in range(len(j))])
        spikes = [keep[i + 2] for i in np.flatnonzero((j > 25 * med) & (j > 50.0) & steady)]
        lvl = 'warn' if spikes else 'info'
        rep.add(lvl, 'jerk', f'{cam}: peak speed {vmax:.2f} m per output s, jerk p50 {med:.2f} max {float(j.max()):.1f} m/s^3'
                + (f', {len(spikes)} spikes' if spikes else ''), **({'frames': fmt_runs(runs(spikes))} if spikes else {}))


def check_logs(ctx: MC.Ctx, rep: Report) -> None:
    take = ctx.take
    run = take.run or {}
    if take.kind == 'v2' and not run:
        rep.add('warn', 'run_json', 'run.json missing')
    if run:
        status = str(run.get('status', ''))
        if status and status != 'done' and not status.startswith('done:'):
            rep.add('warn' if status.startswith('stopped') else 'error', 'run_status', f'run status: {status}')
        for e in run.get('errors') or []:
            rep.add('error', 'run_error', str(e))
        for w in (run.get('warnings') or [])[:30]:
            rep.add('warn', 'run_warning', str(w))
        budgets = {}
        for c in run.get('cameras') or []:
            r = c.get('render') or {}
            if c.get('enabled', True):
                budgets[c.get('name')] = (r.get('warmup'), r.get('samples'), r.get('taa'))
        look = str((run.get('replay') or {}).get('look') or '') if isinstance(run.get('replay'), dict) else ''
        if len(set(budgets.values())) > 1 and not (take.replay_mode and look != 'clean'):
            rep.add('warn', 'render_budget', 'cameras use different render budgets (frozen frames run Update logic per render)',
                    budgets={k: list(v) for k, v in budgets.items()})
        if run.get('late_loads'):
            rep.add('warn', 'late_load', f'late subscene loads: {", ".join(map(str, run["late_loads"]))}')
    marks = {}
    for r in take.events:
        k = r.get('kind', '')
        text = f'{r.get("name", "")} {r.get("detail", "")}'.strip()
        if k == 'error':
            rep.add('error', 'events_error', f't {r.get("game_time")}: {text}')
        elif k in ('mark', 'warn'):
            key = 'late_load' if 'late load' in text else ('timescale_leak' if 'timeScale' in text else k)
            marks.setdefault(key, []).append(f't {r.get("game_time")}: {text}')
    for key, items in marks.items():
        rep.add('warn', key, f'{len(items)} {key.replace("_", " ")} entries in events.csv', first=items[:10])


def check_edit(ctx: MC.Ctx, rep: Report) -> None:
    edit = MC.current_edit(ctx, required=False)
    if not edit:
        return
    try:
        plan = MC.plan_edit(ctx, edit)
    except (SystemExit, TakeError) as e:
        rep.add('error', 'edit', f'edit does not resolve: {e}')
        return
    for w in plan.warnings:
        level = 'warn'
        check = 'hold' if 'holds' in w else ('transition' if 'transition' in w else 'edit')
        rep.add(level, check, w)
    under = [k for k, f in enumerate(plan.tl.frames) if f.flag == 'u']
    if under:
        rep.add('warn', 'undersampled', f'{len(under)} output frames of the warp timeline repeat a step (slow motion beyond the '
                                        f'capture rate)', frames=fmt_runs(runs(under)))
    rep.add('info', 'edit', f'edit {plan.name}: {plan.total} frames = {plan.total / plan.tl.fps:.2f} s, {len(plan.shots)} shots, '
                            f'holds ' + ', '.join(f'{s.n / plan.tl.fps:.1f}' for s in plan.shots))


def check_replay(ctx: MC.Ctx, rep: Report) -> None:
    take = ctx.take
    run = take.run or {}
    rp = run.get('replay') if isinstance(run.get('replay'), dict) else {}
    rec = take.record_dir()
    rep.add('info', 'replay', f'{take.mode} take: look {rp.get("look", "?")}, order {rp.get("order", "?")}, cullers '
                              f'{rp.get("cullers", "?")}, record {rec}')
    if rec is None:
        rep.add('warn', 'recording', 'record folder not found (run.json recording.dir, replay.source, <take>/record)')
    recd = run.get('recording') if isinstance(run.get('recording'), dict) else {}
    if recd:
        for key in ('conflicts', 'readbackErrors', 'monitorHits', 'unresolved'):
            v = MT.inum(recd.get(key), 0)
            if v > 0:
                rep.add('warn', f'replay_{key}', f'recording {key}: {v}')
        rep.add('info', 'recording', f'{MT.inum(recd.get("samples"), 0)} samples, {MT.fnum(recd.get("duration"), 0.0):.3f} s, '
                                     f'{MT.inum(recd.get("objects"), 0)} objects, unresolved spawned '
                                     f'{MT.inum(recd.get("unresolvedSpawned"), 0)}, activations {MT.inum(recd.get("activations"), 0)}, '
                                     f'never activated {len(recd.get("never_activated") or [])}')
    passes = [p for p in run.get('passes') or [] if isinstance(p, dict)]
    pass_cams = {p.get('camera') for p in passes}
    missing = [c for c in take.cams if c not in pass_cams]
    if missing:
        rep.add('warn', 'replay_passes', f'{len(missing)} camera folder(s) without a run.json passes[] entry', cams=missing)
    hashes = sorted({str(p.get('startHash')) for p in passes if p.get('startHash')})
    if len(hashes) > 1:
        rep.add('warn', 'pass_isolation', f'{len(hashes)} different pass start hashes (every pass starts from the same saved '
                                          f'state, REPLAY_DESIGN 2.4.9)', hashes=hashes[:8])
    if passes:
        rep.add('info', 'passes', '; '.join(f'{p.get("camera")} {MT.inum(p.get("renders"), 0)} renders, {MT.inum(p.get("warmups"), 0)} '
                                            f'warmups, {MT.inum(p.get("taaResets"), 0)} TAA resets, rng {p.get("rngStart")}->'
                                            f'{p.get("rngEnd")}' for p in passes))
    tl = take.base
    bad = []
    for k in range(len(tl) - 1):
        want = tl[k].speed / tl.fps
        if abs((tl[k + 1].t - tl[k].t) - want) > max(2e-5, 0.02 * want):
            bad.append(k + 1)
    if bad:
        rep.add('warn', 'replay_timeline', f'{len(bad)} output frames whose record-time step differs > 2 % from speed / fps',
                frames=fmt_runs(runs(bad)))
    real = np.array([f.real for f in tl.frames], np.float64)
    fin = np.isfinite(real)
    if len(tl) and not fin.any():
        rep.add('warn', 'real_time', 'meta.csv has no real_time values (the audio clock)')
    elif fin.sum() > 1:
        back = [int(i) + 1 for i in np.flatnonzero(np.diff(real[fin]) < -1e-6)]
        if back:
            rep.add('warn', 'real_time', f'{len(back)} frames where real_time decreases', frames=fmt_runs(runs(back)))
    rep.add('info', 'audio', f'audio: {take.audio_state()["verdict"]}')
    check_views(ctx, rep)


def check_views(ctx: MC.Ctx, rep: Report) -> None:
    path = ctx.take.root / 'views.csv'
    if not path.is_file():
        return
    inside: dict[str, list[float]] = {}
    hits: dict[str, list[tuple[float, str]]] = {}
    for r in MT.read_csv(path):
        cam, t = r.get('camera', ''), MT.fnum(r.get('game_time'))
        if r.get('inside') == '1':
            inside.setdefault(cam, []).append(t)
        if r.get('path_hit'):
            hits.setdefault(cam, []).append((t, r['path_hit']))
    for cam, ts in inside.items():
        rep.add('warn', 'camera_inside', f'{cam}: camera or near plane inside geometry in {len(ts)} view check(s) at game '
                                         f'{", ".join(f"{t:.2f}" for t in ts[:12])}{" ..." if len(ts) > 12 else ""} s (views.csv)')
    for cam, hs in hits.items():
        rep.add('warn', 'camera_path', f'{cam}: camera path passes through geometry in {len(hs)} view interval(s) at game '
                                       + ', '.join(f'{t:.2f} s ({w})' for t, w in hs[:6]) + (' ...' if len(hs) > 6 else ''))


def check_taa(ctx: MC.Ctx, rep: Report, folder: Path) -> None:
    sets = {}
    for d in sorted(folder.iterdir()):
        if d.is_dir() and d.name.lower().startswith('n') and d.name[1:].isdigit():
            sets[int(d.name[1:])] = d
    if not sets:
        rep.add('warn', 'taa', f'{folder}: no n<samples> subfolders')
        return
    ref_n = max(sets)
    import cv2
    for p in sorted(sets[ref_n].rglob('*.jpg')):
        rel = p.relative_to(sets[ref_n])
        ref = cv2.imdecode(np.fromfile(str(p), np.uint8), cv2.IMREAD_COLOR).astype(np.float32)
        for n, d in sets.items():
            if n == ref_n or not (d / rel).is_file():
                continue
            im = cv2.imdecode(np.fromfile(str(d / rel), np.uint8), cv2.IMREAD_COLOR).astype(np.float32)
            mse = float(np.mean((im - ref) ** 2))
            psnr = 99.0 if mse <= 1e-9 else 10 * math.log10(255 ** 2 / mse)
            rep.add('info', 'taa', f'{rel}: n={n} vs n={ref_n}: PSNR {psnr:.1f} dB')


def dark_args(ctx: MC.Ctx, cells: bool = False) -> tuple[int, float]:
    a = ctx.args
    level = int(getattr(a, 'dark_level', None) or DARK_LEVEL)
    cover = float((getattr(a, 'cell_dark_cover', None) or CELL_DARK_COVER) if cells else (getattr(a, 'dark_cover', None) or DARK_COVER))
    return max(1, min(255, level)), min(max(cover, 0.01), 1.0)


def video_luma(path: Path, scale: int = 4, gpu: bool = False):
    info = MC.ffprobe(path)
    vids = [s for s in info.get('streams', []) if s.get('codec_type') == 'video']
    if 'error' in info or not vids:
        raise TakeError(f'{path.name}: ffprobe failed ({info.get("error", "no video stream")})')
    W, H = int(vids[0]['width']), int(vids[0]['height'])
    w, h = max(2, W // scale), max(2, H // scale)
    hw = ['-hwaccel', 'cuda', '-hwaccel_output_format', 'cuda'] if gpu else []
    pre = f'hwdownload,format={MT.gpu_download_format(path)},' if gpu else ''
    cmd = [MT.FFMPEG, '-hide_banner', '-loglevel', 'error', *hw, '-i', str(path), '-vf', f'{pre}scale={w}:{h}:flags=area,format=gray', '-f',
           'rawvideo', '-']
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE)
    n = w * h
    try:
        while True:
            buf = proc.stdout.read(n)
            if len(buf) < n:
                break
            yield np.frombuffer(buf, np.uint8).reshape(h, w), W, H
    finally:
        proc.stdout.close()
        proc.wait()


def output_videos(ctx: MC.Ctx) -> list[tuple[Path, dict | None]]:
    folders = [Path(v) for v in (getattr(ctx.args, 'videos', None) or [])] or [ctx.out]
    found: dict[str, tuple[Path, dict | None]] = {}
    lays = {str(l.get('name', 'main')): l for l in MC.layouts_of(ctx) if isinstance(l, dict)}
    for d in folders:
        log = d / 'compose_log.json'
        if not log.is_file():
            continue
        for entry in json.loads(log.read_text(encoding='utf-8')):
            res = entry.get('result')
            items = res if isinstance(res, list) else [res] if isinstance(res, dict) else []
            if isinstance(res, dict) and ('angles' in res or 'layouts' in res):
                items = list(res.get('angles') or []) + list(res.get('layouts') or []) + ([res['cut']] if isinstance(res.get('cut'), dict) else [])
            for it in items:
                if not isinstance(it, dict) or not it.get('file'):
                    continue
                f = Path(it['file'])
                if not f.is_file():
                    continue
                stem = f.stem.replace('_h265', '').replace('_draft', '')
                lay = lays.get(stem[len('layout_'):]) if stem.startswith('layout_') else None
                found[str(f).lower()] = (f, lay)
    return sorted(found.values(), key=lambda x: str(x[0]))


def check_video(ctx: MC.Ctx, rep: Report, f: Path, lay: dict | None, gpu: bool = False) -> dict:
    level, cover = dark_args(ctx, True)
    a = ctx.args
    bw = float(getattr(a, 'blur_warn', None) or BLUR_WARN)
    bf = float(getattr(a, 'blur_fail', None) or BLUR_FAIL)
    fps = float(ctx.take.fps)
    flags: dict = {'frames': 0, 'black': {}, 'near_black': {}, 'blur_fail': {}, 'blur_warn': {}}
    try:
        cells = None
        black: dict[str, list[int]] = flags['black']
        near: dict[str, list[int]] = flags['near_black']
        lap: dict[str, list[float]] = {}
        n = 0
        for j, (g, W, H) in enumerate(video_luma(f, gpu=gpu)):
            if cells is None:
                cells = [('frame', 0, 0, g.shape[1], g.shape[0])]
                if lay is not None:
                    LW, LH, lc = MC.layout_geometry(lay, ctx.sizes, ctx.src_size)
                    sx, sy = g.shape[1] / LW, g.shape[0] / LH
                    cells = [(f'{c.cam}@{c.x},{c.y}', int(c.x * sx), int(c.y * sy), max(1, int(c.w * sx)), max(1, int(c.h * sy))) for c in lc]
            for name, x, y, w, h in cells:
                reg = g[y:y + h, x:x + w]
                if not reg.size:
                    continue
                p50 = float(np.median(reg))
                d = float(np.mean(reg < level))
                if p50 < 2:
                    black.setdefault(name, []).append(j)
                elif d > cover:
                    near.setdefault(name, []).append(j)
                lap.setdefault(name, []).append(float(cv2.Laplacian(reg, cv2.CV_32F).var()))
            n = j + 1
        flags['frames'] = n
        for kind, bag, what in (('black', black, 'black (luma p50 < 2)'), ('near_black', near, f'near-black (more than {cover:.0%} below {level}/255)')):
            for name, idx in bag.items():
                rep.add('error', f'output_{kind}', f'{f.name} {name}: {len(idx)} {what} frames at output {time_runs(idx, lambda j: j / fps)}',
                        frames=fmt_runs(runs(idx)))
        blurred = False
        if not getattr(a, 'no_blur', False):
            for name, vals in lap.items():
                dark = np.zeros(len(vals), bool)
                for j in black.get(name, []) + near.get(name, []):
                    if j < len(dark):
                        dark[j] = True
                warns, fails, _ = blur_flags(np.array(vals, np.float64), fps, BLUR_WINDOW, bw, bf, dark)
                flags['blur_fail'][name], flags['blur_warn'][name] = fails, warns
                for level_name, idx, what in (('error', fails, f'strongly blurred (sharpness below {bf:.0%} of its neighbours)'),
                                              ('warn', warns, f'blurred (sharpness below {bw:.0%} of its neighbours)')):
                    if idx:
                        blurred = True
                        rep.add(level_name, 'output_blur', f'{f.name} {name}: {len(idx)} {what} frames at output {time_runs(idx, lambda j: j / fps)}',
                                frames=fmt_runs(runs(idx)))
        if not black and not near and not blurred:
            rep.add('info', 'outputs', f'{f.name}: {n} frames, {len(cells or [])} region(s), no black, near-black or blurred frame')
    except (TakeError, OSError, ValueError) as e:
        rep.add('error', 'outputs', f'{f.name}: {e}')
    return flags


def check_outputs(ctx: MC.Ctx, rep: Report) -> None:
    vids = output_videos(ctx)
    if not vids:
        rep.add('info', 'outputs', f'no composed videos found (compose_log.json in {ctx.out}; --videos DIR)')
        return
    for f, lay in vids:
        check_video(ctx, rep, f, lay)

def check_track(ctx: MC.Ctx, rep: Report) -> None:
    try:
        import mcam_track_check as TC
        lines, doc, _ = TC.run(Path(ctx.take.root))
    except Exception as e:
        rep.add('warn', 'track', f'track check failed: {type(e).__name__}: {e}')
        return
    if not doc.get('outputs'):
        rep.add('info', 'track', lines[0] if lines else 'track check: no outputs')
        return
    for o in doc['outputs']:
        for r in o['runs']:
            rep.add('error' if r['level'] == 'FAIL' else 'warn', 'track',
                    f"{o['output']}: subject beyond {doc['fail'] if r['level'] == 'FAIL' else doc['warn']} of the visible half-frame "
                    f"{r['from']:.2f}-{r['to']:.2f} s ({r['seconds']:.2f} s), peak {r['peak']:.2f}")
    rep.add('info', 'track', f"track_check.txt: {doc.get('fails', 0)} FAIL, {doc.get('warnings', 0)} WARN")


def cmd_qa(ctx: MC.Ctx) -> int:
    rep = Report()
    stats = check_frames(ctx, rep)
    check_poses(ctx, rep)
    check_logs(ctx, rep)
    check_edit(ctx, rep)
    if not getattr(ctx.args, 'no_outputs', False):
        check_outputs(ctx, rep)
    if ctx.take.replay_mode in ('replay', 'take'):
        check_replay(ctx, rep)
        check_track(ctx, rep)
    if getattr(ctx.args, 'taa', None):
        check_taa(ctx, rep, Path(ctx.args.taa))
    errors, warns = rep.count('error'), rep.count('warn')
    strict = bool(getattr(ctx.args, 'strict', False))
    code = 1 if errors or (strict and warns) else 0
    doc = {'take': str(ctx.take.root), 'kind': ctx.take.kind, 'cameras': list(ctx.take.cams), 'frames': len(ctx.take.base),
           'errors': errors, 'warnings': warns, 'exit': code, 'items': rep.items}
    (ctx.out / 'qa.json').write_text(json.dumps(doc, indent=1, default=str), encoding='utf-8')
    lines = [f'QA {ctx.take.root} ({ctx.take.kind}): {len(ctx.take.cams)} cameras, {len(ctx.take.base)} output frames, '
             f'{errors} errors, {warns} warnings -> exit {code}']
    order = {'error': 0, 'warn': 1, 'info': 2}
    for it in sorted(rep.items, key=lambda i: order[i['level']]):
        det = it.get('detail')
        lines.append(f'{it["level"].upper():<5} {it["check"]:<18} {it["msg"]}' + (f'  {json.dumps(det, default=str)}' if det else ''))
    text = '\n'.join(lines)
    (ctx.out / 'qa.txt').write_text(text + '\n', encoding='utf-8')
    print(text)
    print(f'wrote {ctx.out / "qa.json"} and qa.txt')
    return code


# ---------------------------------------------------------------------------------------------------------------- sheets

def tile_grid(tiles: list[list[np.ndarray | None]], row_titles: list[str], col_titles: list[str], title: str,
              captions: list[str] | None = None, size=THUMB) -> Image.Image:
    tw, th = size
    left, top, gap = 190, 74, 6
    rows, cols = len(tiles), max(len(r) for r in tiles) if tiles else 0
    cap_h = 22 if captions else 0
    W = left + cols * (tw + gap) + 10
    H = top + rows * (th + gap + cap_h) + 10
    img = Image.new('RGB', (W, H), BG)
    d = ImageDraw.Draw(img)
    d.text((12, 12), title, fill=FG, font=font(22))
    fs, fm = font(18), font(14, mono=True)
    for c, ct in enumerate(col_titles):
        d.text((left + c * (tw + gap) + 4, top - 22), ct, fill=DIM, font=fm)
    for r, row in enumerate(tiles):
        y = top + r * (th + gap + cap_h)
        d.text((12, y + 8), row_titles[r], fill=FG, font=fs)
        for c, t in enumerate(row):
            if t is None:
                continue
            img.paste(Image.fromarray(t), (left + c * (tw + gap), y))
        if captions:
            d.text((left, y + th + 3), captions[r], fill=DIM, font=fm)
    return img


def sheet_times(ctx: MC.Ctx, tl: MT.Timeline, every: float | None, count: int | None) -> list[int]:
    n = len(tl)
    if count:
        return sorted(set(np.linspace(0, n - 1, count).round().astype(int).tolist()))
    t0, t1 = tl[0].t, tl[n - 1].t
    ks = []
    t = t0
    while t <= t1 + 1e-9:
        ks.append(tl.frame_at(t))
        t += every
    return sorted(set(ks))


def cam_caption(ctx: MC.Ctx, cam: str) -> str:
    g = ctx.grades.get(cam)
    steps = sorted({s for f in ctx.take.base.frames for s in f.steps})
    st = MT.camera_stats(ctx.take, ctx.store, cam, steps)
    ok = st['ok']
    s = (f'raw p0.5 {MT.pooled_percentile(st["hist"], 0.005):.0f}  p50 {float(np.median(st["p50"][ok])):.0f}  '
         f'p99 {float(np.median(st["p99"][ok])):.0f}  clip {float(np.mean(st["clip"][ok])) * 100:.2f}%')
    if g is not None and not g.spec.off:
        s += f'   grade: floor {g.floor * 255:.1f} lift {g.spec.lift} k {g.spec.k:g} ev {g.spec.ev:g}'
        if g.spec.auto and g.ev_steps:
            v = list(g.ev_steps.values())
            s += f' auto {min(v):.2f}..{max(v):.2f}'
    return s


def graded_stats(ctx: MC.Ctx, cam: str, ks: list[int], tl: MT.Timeline) -> str:
    ys = []
    for k in ks:
        im = MC.render_view(ctx, cam, tl[k], 320, 180).astype(np.float32) / 257.0
        ys.append(im @ MT.LUMA)
    y = np.concatenate([v.ravel() for v in ys])
    return f'graded p0.5 {np.percentile(y, 0.5):.0f}  p50 {np.percentile(y, 50):.0f}  p99 {np.percentile(y, 99):.0f}'


def cmd_sheet(ctx: MC.Ctx) -> int:
    take = ctx.take
    cams = MC.cams_arg(ctx, 'all')
    MC.setup_grades(ctx, cams)
    tl = MC.base_timeline(ctx)
    every = float(getattr(ctx.args, 'every', None) or 1.0)
    rctx = raw_ctx(ctx)
    cols = int(getattr(ctx.args, 'cols', None) or 8)
    for cam in cams:
        ks = sheet_times(ctx, tl, every, None)
        per_row = max(1, cols)
        rows = [ks[i:i + per_row] for i in range(0, len(ks), per_row)]
        tiles = [[thumb(ctx, cam, k, tl) for k in r] for r in rows]
        titles = [f't {tl[r[0]].t:.2f}..' for r in rows]
        img = tile_grid(tiles, titles, [], f'{take.name} / {cam} ({take.cams[cam].mode or "?"}): every {every:g} s, graded',
                        [cam_caption(ctx, cam)] + [''] * (len(rows) - 1))
        save(img, ctx.out / f'sheet_{MC.safe_name(cam)}.png')
    ks = sheet_times(ctx, tl, None, int(getattr(ctx.args, 'columns', None) or 6))
    ab = bool(getattr(ctx.args, 'ab', False))
    tiles, titles, caps = [], [], []
    for cam in cams:
        if ab:
            tiles.append([thumb(rctx, cam, k, tl) for k in ks])
            titles.append(f'{cam} raw')
            caps.append(cam_caption(ctx, cam))
        tiles.append([thumb(ctx, cam, k, tl) for k in ks])
        titles.append(f'{cam}{" graded" if ab else ""}')
        caps.append(graded_stats(ctx, cam, ks, tl) if ab else cam_caption(ctx, cam))
    img = tile_grid(tiles, titles, [f't {tl[k].t:.2f}  f{k}' for k in ks],
                    f'{take.name}: {len(cams)} cameras{" raw vs graded" if ab else ""}', caps)
    save(img, ctx.out / f'sheet_grid{"_ab" if ab else ""}.png')
    if getattr(ctx.args, 'edit', None) or (getattr(ctx.args, 'storyboard', False) and MC.current_edit(ctx, required=False)):
        storyboard(ctx)
    if getattr(ctx.args, 'layout', None) or getattr(ctx.args, 'layouts', False):
        layout_sheet(ctx)
    return 0


def storyboard(ctx: MC.Ctx) -> None:
    edit = MC.current_edit(ctx)
    plan = MC.plan_edit(ctx, edit)
    MC.setup_grades(ctx, sorted({s.cam for s in plan.shots}))
    fps = plan.tl.fps
    tiles, titles, caps = [], [], []
    for i, s in enumerate(plan.shots):
        win = plan.window(i)
        a = win[1] if win else s.out0
        nxt = plan.window(i + 1)
        b = (nxt[0] if nxt else s.out0 + s.n) - 1
        a = max(a, min(plan.fade_in, b))
        b = max(a, min(b, plan.total - plan.fade_out - 1))
        ks = [a, (a + b) // 2, b]
        row = []
        for j in ks:
            row.append(to8(MC.render_edit_view(ctx, plan, max(0, min(j, plan.total - 1)), THUMB[0], THUMB[1])))
        if win:
            row.insert(0, to8(MC.render_edit_view(ctx, plan, (win[0] + win[1]) // 2, THUMB[0], THUMB[1])))
        else:
            row.insert(0, None)
        tiles.append(row)
        titles.append(f'{i}: {s.cam}')
        hold = s.n / fps
        flag = '' if plan.min_hold - 1e-6 <= hold <= plan.max_hold + 1e-6 else '  HOLD OUT OF RANGE'
        caps.append(f'game {plan.tl[s.src0].t0:.2f}..{plan.tl[s.src1 - 1].t:.2f}  out {s.out0 / fps:.2f}+{hold:.2f}s  in {s.tin}'
                    f'{" " + format(s.tn / fps, ".2f") + "s" if s.tn else ""}{flag}')
    img = tile_grid(tiles, titles, ['transition mid', 'shot start', 'middle', 'end'],
                    f'{ctx.take.name}: cut "{plan.name}", {plan.total / fps:.2f} s, {len(plan.shots)} shots', caps)
    save(img, ctx.out / f'sheet_cut_{MC.safe_name(plan.name)}.png')


def layout_sheet(ctx: MC.Ctx) -> None:
    for lay in MC.layouts_of(ctx):
        W, H, cells = MC.layout_geometry(lay, ctx.sizes, ctx.src_size)
        MC.resolve_cells(ctx, cells)
        if any(c.cam == 'cut' for c in cells):
            print(f'layout {lay.get("name")}: contains the cut, use compose --stills for it')
            continue
        MC.setup_grades(ctx, sorted({c.cam for c in cells}))
        tl = MC.base_timeline(ctx)
        ks = sheet_times(ctx, tl, None, 3)
        scale = 0.5
        frames = []
        for k in ks:
            canvas = np.zeros((H, W, 3), np.uint16)
            for c in cells:
                canvas[c.y:c.y + c.h, c.x:c.x + c.w] = MC.render_view(ctx, c.cam, tl[k], c.w, c.h, c.crop, c.anchor, c.pan)
                if ctx.labels.show:
                    px = MC.label_px(ctx, W, 1.0 if c.band else 0.75)
                    ctx.labels.draw(canvas, ctx.labels.text_for(ctx.take, c.cam, c.label), c.x + int(px * 0.8), c.y + c.h - int(px * 0.7), px)
            im = Image.fromarray(to8(canvas)).resize((int(W * scale), int(H * scale)), Image.LANCZOS)
            frames.append((k, im))
        gap = 12
        sheet = Image.new('RGB', (frames[0][1].width + 2 * gap, len(frames) * (frames[0][1].height + 30) + gap + 40), BG)
        d = ImageDraw.Draw(sheet)
        d.text((gap, 10), f'{ctx.take.name}: layout "{lay.get("name", "main")}" {W}x{H} (shown at 50 %): '
                          + ', '.join(f'{c.cam} {c.w}x{c.h}' for c in cells), fill=FG, font=font(18))
        y = 40
        for k, im in frames:
            d.text((gap, y), f'frame {k}  game t {tl[k].t:.2f}', fill=DIM, font=font(14, True))
            sheet.paste(im, (gap, y + 20))
            y += im.height + 30
        save(sheet, ctx.out / f'sheet_layout_{MC.safe_name(str(lay.get("name", "main")))}.png')


def cmd_sync(ctx: MC.Ctx) -> int:
    take = ctx.take
    cams = MC.cams_arg(ctx, 'all')
    MC.setup_grades(ctx, cams)
    tl = MC.base_timeline(ctx)
    moments = [(t, f'{k} {n}') for t, k, n in take.event_list() if k in ('event', 'cue', 'shot', 'segment')]
    edit = MC.current_edit(ctx, required=False)
    if edit:
        for s in edit.get('shots') or []:
            try:
                moments.append((take.resolve_time(s.get('from', 0), 0.0), f'cut -> {s.get("cam")}'))
            except TakeError as e:
                print(f'warning: {e}')
    for t in getattr(ctx.args, 'times', None) or []:
        moments.append((float(t), 'mark'))
    if not moments:
        step = float(getattr(ctx.args, 'every', None) or 2.0)
        moments = [(t, 'every') for t in np.arange(tl[0].t, tl[len(tl) - 1].t + 1e-9, step)]
    moments.sort(key=lambda m: m[0])
    size = (320, 180)
    tiles, titles = [], []
    for t, what in moments[:40]:
        k = tl.frame_at(t)
        tiles.append([thumb(ctx, c, k, tl, size) for c in cams])
        titles.append(f't {t:.2f} f{k}\n{what[:22]}')
    img = tile_grid(tiles, titles, cams, f'{take.name}: sync sheet ({len(moments)} moments x {len(cams)} cameras)', None, size)
    save(img, ctx.out / 'sheet_sync.png')
    return 0


# ----------------------------------------------------------------------------------------------------------------- plots

def cmd_paths(ctx: MC.Ctx) -> int:
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    poses = ctx.take.poses()
    if not poses:
        print('no poses.csv in this take (v1): nothing to plot', file=sys.stderr)
        return 2
    tracks = ctx.take.tracks()
    fig, (top, side) = plt.subplots(2, 1, figsize=(14, 11), facecolor='#121214')
    for ax, (a, b, lab) in zip((top, side), ((0, 2, 'z (player side below)'), (0, 1, 'y'))):
        ax.set_facecolor('#18181b')
        ax.tick_params(colors='#a09a8e')
        for sp in ax.spines.values():
            sp.set_color('#444')
        ax.set_xlabel('x', color='#a09a8e')
        ax.set_ylabel(lab, color='#a09a8e')
        ax.grid(color='#2a2a2e', lw=0.6)
    cmap = plt.get_cmap('tab10')
    for i, (cam, e) in enumerate(poses.items()):
        col = cmap(i % 10)
        p = np.stack([e['x'], e['y'], e['z']], 1)
        f = e['forward']
        for ax, (a, b) in ((top, (0, 2)), (side, (0, 1))):
            ax.plot(p[:, a], p[:, b], '-', color=col, lw=1.6, label=cam if ax is top else None)
            ax.plot(p[0, a], p[0, b], 'o', color=col, ms=5)
            ax.plot(p[-1, a], p[-1, b], 's', color=col, ms=5)
            every = max(1, len(p) // 12)
            ax.quiver(p[::every, a], p[::every, b], f[::every, a], f[::every, b], color=col, angles='xy', scale_units='xy',
                      scale=1.0, width=0.002, alpha=0.7)
    for name, tr in tracks.items():
        ok = tr['ok'] > 0
        if ok.any():
            top.plot(tr['x'][ok], tr['z'][ok], '--', color='#9a9a9a', lw=1, label=f'target {name}'[:40])
            side.plot(tr['x'][ok], tr['y'][ok], '--', color='#9a9a9a', lw=1)
    top.axhline(MT_PLAYER_Z, color='#dc5046', lw=1.2, ls=':')
    lo, hi = top.get_ylim()
    top.axhspan(MT_PLAYER_Z, max(hi, MT_PLAYER_Z + 1), color='#dc5046', alpha=0.08)
    top.text(top.get_xlim()[0], MT_PLAYER_Z + 0.2, '  set side (z > -2): no cameras unless looking down', color='#dc5046', fontsize=9)
    top.set_aspect('equal', adjustable='datalim')
    side.set_aspect('equal', adjustable='datalim')
    top.legend(facecolor='#18181b', labelcolor='#d8d2c0', fontsize=9, loc='upper right')
    top.set_title(f'{ctx.take.name}: camera paths, top (x-z)', color='#d8d2c0')
    side.set_title('side (x-y)', color='#d8d2c0')
    fig.tight_layout()
    out = ctx.out / 'paths.png'
    fig.savefig(out, dpi=110, facecolor=fig.get_facecolor())
    print(f'wrote {out}')
    return 0


def cmd_timeline(ctx: MC.Ctx) -> int:
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    take = ctx.take
    tl = take.base
    fig, (ax, ax2) = plt.subplots(2, 1, figsize=(15, 7), facecolor='#121214', gridspec_kw={'height_ratios': [2, 1]})
    for a in (ax, ax2):
        a.set_facecolor('#18181b')
        a.tick_params(colors='#a09a8e')
        for sp in a.spines.values():
            sp.set_color('#444')
        a.grid(color='#2a2a2e', lw=0.6)
    segs = take.summary()['segments']
    for s in segs:
        slow = 1.0 / max(s['speed'], 1e-6)
        ax.axvspan(s['from_t'], s['to_t'], 0.75, 1.0, color=(0.3, 0.45, 0.8, min(0.85, 0.15 + 0.15 * slow)))
        ax.text(s['from_t'], 3.85, f' seg {s["segment"]}: x{s["speed"]:g}, {s["samples"]} smp', color='#d8d2c0', fontsize=8, va='center')
    colors = {'event': '#e0a83c', 'cue': '#78be78', 'shot': '#8fb3ff', 'mark': '#dc5046', 'warn': '#dc5046', 'error': '#ff2020',
              'segment': '#777'}
    for t, k, n in take.event_list():
        if k == 'segment':
            continue
        ax.axvline(t, 0, 0.72, color=colors.get(k, '#999'), lw=1)
        ax.text(t, 2.6 if k == 'event' else 2.1, f' {n[:18]}', color=colors.get(k, '#999'), fontsize=8, rotation=0)
    edit = MC.current_edit(ctx, required=False)
    cams = list(take.cams)
    cmap = plt.get_cmap('tab10')
    if edit:
        try:
            plan = MC.plan_edit(ctx, edit)
            fps = plan.tl.fps
            for i, s in enumerate(plan.shots):
                g0, g1 = plan.tl[s.src0].t0, plan.tl[s.src1 - 1].t
                hold = s.n / fps
                bad = not (plan.min_hold - 1e-6 <= hold <= plan.max_hold + 1e-6) and 0 < i < len(plan.shots) - 1 or hold > plan.max_hold + 1e-6
                ci = cams.index(s.cam) if s.cam in cams else 0
                lane = (1.15, 0.6) if s.replay else (0.2, 0.85)
                ax.broken_barh([(g0, g1 - g0)], lane, facecolors=cmap(ci % 10), edgecolor='#ff3030' if bad else '#000',
                               linewidth=2.5 if bad else 0.5, alpha=0.85, hatch='//' if s.replay else None)
                ax.text(g0 + 0.05, lane[0] + lane[1] / 2, f'{s.cam} {hold:.1f}s{" replay" if s.replay else ""}', color='#111',
                        fontsize=8, va='center')
                ax2.plot([s.out0 / fps, (s.out0 + s.n) / fps], [g0, g1], color=cmap(ci % 10), lw=2.5)
                if s.tn:
                    ax2.axvspan(s.tws / fps, (s.tws + s.tn) / fps, color='#ffffff', alpha=0.08)
            ax2.set_xlabel('output seconds (cut)', color='#a09a8e')
        except (SystemExit, TakeError) as e:
            ax.text(0, 1, f'edit error: {e}', color='#ff3030')
    else:
        out_t = np.arange(len(tl)) / tl.fps
        ax2.plot(out_t, tl.times, color='#8fb3ff', lw=2)
        ax2.set_xlabel('output seconds (meta frames)', color='#a09a8e')
    ax2.set_ylabel('game s', color='#a09a8e')
    ax.set_ylim(0, 4.1)
    ax.set_yticks([])
    if len(tl):
        ax.set_xlim(tl[0].t0, tl[len(tl) - 1].t)
    ax.set_xlabel('game seconds', color='#a09a8e')
    ax.set_title(f'{take.name}: segments (blue, darker = slower), events/cues, shots (red outline = hold out of range)',
                 color='#d8d2c0', fontsize=10)
    fig.tight_layout()
    out = ctx.out / 'timeline.png'
    fig.savefig(out, dpi=100, facecolor=fig.get_facecolor())
    print(f'wrote {out}')
    return 0


def cmd_diverge(ctx: MC.Ctx) -> int:
    other = getattr(ctx.args, 'other', None)
    if not other:
        print('diverge needs a second take: mcam_qa.py diverge <takeA> <takeB>', file=sys.stderr)
        return 2
    try:
        tb = Take(Path(other))
    except TakeError as e:
        print(f'error: {e}', file=sys.stderr)
        return 2
    a, b = ctx.take.tracks(), tb.tracks()
    if not a or not b:
        print('both takes need tracks.csv', file=sys.stderr)
        return 2
    rows = []
    for name in sorted(set(a) & set(b)):
        ta, tb_ = a[name], b[name]
        oka, okb = ta['ok'] > 0, tb_['ok'] > 0
        if oka.sum() < 2 or okb.sum() < 2:
            continue
        t = ta['game_time'][oka]
        pa = np.stack([ta[k][oka] for k in 'xyz'], 1)
        sel = (t >= tb_['game_time'][okb].min()) & (t <= tb_['game_time'][okb].max())
        t, pa = t[sel], pa[sel]
        pb = np.stack([np.interp(t, tb_['game_time'][okb], tb_[k][okb]) for k in 'xyz'], 1)
        d = np.linalg.norm(pa - pb, axis=1)
        first = t[np.argmax(d > 0.05)] if (d > 0.05).any() else None
        rows.append({'target': name, 'max_m': round(float(d.max()), 4), 'mean_m': round(float(d.mean()), 4),
                     't_max': round(float(t[np.argmax(d)]), 3), 'first_over_5cm': None if first is None else round(float(first), 3)})
    for r in rows:
        print(f'{r["target"][:40]:<40} max {r["max_m"]:.3f} m at t {r["t_max"]:.2f}  mean {r["mean_m"]:.3f}  '
              f'> 5 cm from {r["first_over_5cm"]}')
    (ctx.out / 'diverge.json').write_text(json.dumps({'a': str(ctx.take.root), 'b': str(tb.root), 'targets': rows}, indent=1),
                                          encoding='utf-8')
    print(f'wrote {ctx.out / "diverge.json"}')
    return 0


# --------------------------------------------------------------------------------------------------------- replay tests

GOLDEN_NOISE = (0, 1, 16, 120)


def standalone_out(args, name: str) -> Path:
    out = Path(args.out).resolve() if getattr(args, 'out', None) else (MT.OUT_ROOT / MC.safe_name(name)).resolve()
    base = MT.OUT_ROOT.resolve()
    if base not in out.parents and out != base and not getattr(args, 'force', False):
        raise SystemExit(f'output {out} is outside {base}; pass --force to write there')
    out.mkdir(parents=True, exist_ok=True)
    return out


def read_json(path: Path) -> dict:
    try:
        d = json.loads(path.read_text(encoding='utf-8')) if path.is_file() else {}
        return d if isinstance(d, dict) else {}
    except (OSError, ValueError):
        return {}


def load_rgb(path: Path) -> np.ndarray | None:
    if not path.is_file():
        return None
    im = cv2.imdecode(np.fromfile(str(path), np.uint8), cv2.IMREAD_COLOR)
    return None if im is None else cv2.cvtColor(im, cv2.COLOR_BGR2RGB)


def psnr(a: np.ndarray, b: np.ndarray, sel: np.ndarray | None = None) -> float:
    d = a.astype(np.float32) - b.astype(np.float32)
    se = d * d
    if sel is not None:
        se = se[sel]
    if se.size == 0:
        return math.nan
    mse = float(se.mean())
    return 99.0 if mse <= 1e-12 else 10.0 * math.log10(255.0 ** 2 / mse)


def luma(img: np.ndarray) -> np.ndarray:
    return np.ascontiguousarray(img.astype(np.float32) @ MT.LUMA)


def gauss(x: np.ndarray) -> np.ndarray:
    return cv2.GaussianBlur(x, (11, 11), 1.5)


def ssim_map(ya: np.ndarray, yb: np.ndarray) -> np.ndarray:
    c1, c2 = (0.01 * 255) ** 2, (0.03 * 255) ** 2
    ma, mb = gauss(ya), gauss(yb)
    saa = gauss(ya * ya) - ma * ma
    sbb = gauss(yb * yb) - mb * mb
    sab = gauss(ya * yb) - ma * mb
    return ((2 * ma * mb + c1) * (2 * sab + c2)) / ((ma * ma + mb * mb + c1) * (saa + sbb + c2))


def coverage_radius(d: np.ndarray, cover: float) -> int:
    s = np.sort(d.astype(np.float64))
    k = max(0, int(math.ceil(cover * len(s))) - 1)
    return int(math.ceil(s[k] - 1e-6))


def edge_silhouette(ya: np.ndarray, yb: np.ndarray, rect: tuple, keep: np.ndarray, max_r: int, cover: float = 0.99) -> dict:
    H, W = ya.shape
    x0, y0, x1, y1 = rect
    m = max_r + 2
    X0, Y0, X1, Y1 = max(0, x0 - m), max(0, y0 - m), min(W, x1 + m), min(H, y1 + m)
    ca = cv2.GaussianBlur(np.clip(ya[Y0:Y1, X0:X1], 0, 255).astype(np.uint8), (3, 3), 0)
    cb = cv2.GaussianBlur(np.clip(yb[Y0:Y1, X0:X1], 0, 255).astype(np.uint8), (3, 3), 0)
    inner = np.zeros(ca.shape, bool)
    inner[y0 - Y0:y1 - Y0, x0 - X0:x1 - X0] = True
    inner &= keep[Y0:Y1, X0:X1]
    if not inner.any():
        return {'px': None, 'edges': [0, 0]}
    mag = np.abs(cv2.Sobel(ca, cv2.CV_32F, 1, 0)) + np.abs(cv2.Sobel(ca, cv2.CV_32F, 0, 1))
    hi = max(20.0, float(np.percentile(mag[inner], 90)))
    ea = cv2.Canny(ca, hi * 0.5, hi) > 0
    eb = cv2.Canny(cb, hi * 0.5, hi) > 0
    a, b = ea & inner, eb & inner
    na, nb = int(a.sum()), int(b.sum())
    if na < 20 or nb < 20:
        return {'px': None, 'edges': [na, nb]}
    db = cv2.distanceTransform(np.where(eb, 0, 1).astype(np.uint8), cv2.DIST_C, 3)
    da = cv2.distanceTransform(np.where(ea, 0, 1).astype(np.uint8), cv2.DIST_C, 3)
    r = max(coverage_radius(db[a], cover), coverage_radius(da[b], cover))
    return {'px': min(r, max_r + 1), 'edges': [na, nb]}


def quat_matrix(qx: float, qy: float, qz: float, qw: float) -> np.ndarray:
    return np.array([[1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy - qz * qw), 2 * (qx * qz + qy * qw)],
                     [2 * (qx * qy + qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz - qx * qw)],
                     [2 * (qx * qz - qy * qw), 2 * (qy * qz + qx * qw), 1 - 2 * (qx * qx + qy * qy)]], np.float64)


def rig_poses(rows: list[dict]) -> dict:
    out = {}
    for r in rows:
        v = [MT.fnum(r.get(k)) for k in ('x', 'y', 'z', 'qx', 'qy', 'qz', 'qw', 'fov')]
        if all(math.isfinite(x) for x in v) and v[7] > 0:
            out[MT.inum(r.get('step'))] = {'pos': np.array(v[:3]), 'rot': quat_matrix(*v[3:7]), 'fov': v[7]}
    return out


def project_box(mn, mx, cam: dict, W: int, H: int, near: float = 0.05) -> tuple | None:
    corners = np.array([[x, y, z] for x in (mn[0], mx[0]) for y in (mn[1], mx[1]) for z in (mn[2], mx[2])], np.float64)
    pc = (corners - cam['pos']) @ cam['rot']
    pts = [pc[i] for i in range(8) if pc[i, 2] > near]
    for i in range(8):
        for j in range(i + 1, 8):
            if bin(i ^ j).count('1') == 1 and (pc[i, 2] > near) != (pc[j, 2] > near):
                u = (near - pc[i, 2]) / (pc[j, 2] - pc[i, 2])
                pts.append(pc[i] + u * (pc[j] - pc[i]))
    if not pts:
        return None
    p = np.array(pts)
    th = math.tan(math.radians(cam['fov']) / 2)
    u = (p[:, 0] / (p[:, 2] * th * W / H) + 1) * W / 2
    v = (1 - p[:, 1] / (p[:, 2] * th)) * H / 2
    x0, x1 = max(0, int(math.floor(u.min()))), min(W, int(math.ceil(u.max())))
    y0, y1 = max(0, int(math.floor(v.min()))), min(H, int(math.ceil(v.max())))
    return (x0, y0, x1, y1) if x1 > x0 and y1 > y0 else None


def pad_rect(r: tuple, pad: int, W: int, H: int) -> tuple:
    return max(0, r[0] - pad), max(0, r[1] - pad), min(W, r[2] + pad), min(H, r[3] + pad)


def load_exclusions(path: str | None) -> list[dict]:
    if not path:
        return []
    doc = json.loads(Path(path).read_text(encoding='utf-8'))
    items = doc.get('exclude', []) if isinstance(doc, dict) else doc
    out = []
    for i, it in enumerate(items or []):
        if not isinstance(it, dict) or not (it.get('rect') or it.get('box')):
            raise SystemExit(f'--exclude {path}: item {i} needs "rect" [x0,y0,x1,y1] or "box" {{"min", "max"}}')
        out.append({'name': str(it.get('name') or f'exclude{i}'), 'rect': it.get('rect'), 'box': it.get('box')})
    return out


def exclusion_rects(excl: list[dict], cam: dict | None, W: int, H: int) -> list[tuple]:
    out = []
    for e in excl:
        if e['rect']:
            r = [float(v) for v in e['rect']]
            if max(r) <= 1.0:
                r = [r[0] * W, r[1] * H, r[2] * W, r[3] * H]
            rr = (max(0, int(r[0])), max(0, int(r[1])), min(W, int(math.ceil(r[2]))), min(H, int(math.ceil(r[3]))))
            if rr[2] > rr[0] and rr[3] > rr[1]:
                out.append((e['name'], rr))
        elif cam is not None:
            rr = project_box(e['box']['min'], e['box']['max'], cam, W, H)
            if rr:
                out.append((e['name'], rr))
    return out


def sample_masks(tracks: dict, cam: dict | None, s: int, W: int, H: int, pad: int) -> list[tuple]:
    if cam is None:
        return []
    out = []
    for name, mn, mx in tracks.get(s, []):
        r = project_box(mn, mx, cam, W, H)
        if r is not None:
            out.append((name, pad_rect(r, pad, W, H)))
    return out


def unexplained_regions(rec: np.ndarray, gold: np.ndarray, free: np.ndarray, thr: int, min_area: int) -> list[dict]:
    diff = np.max(np.abs(rec.astype(np.int16) - gold.astype(np.int16)), axis=2)
    cand = ((diff > thr) & free).astype(np.uint8)
    cand = cv2.morphologyEx(cand, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    n, lab, st, _ = cv2.connectedComponentsWithStats(cand, connectivity=8)
    out = []
    for i in range(1, n):
        x, y, w, h, a = (int(v) for v in st[i])
        if a >= min_area:
            out.append({'rect': [x, y, x + w, y + h], 'area': a, 'mean_diff': round(float(diff[lab == i].mean()), 1)})
    return sorted(out, key=lambda r: -r['area'])[:5]


def compare_pair(rec: np.ndarray, gold: np.ndarray, masks: list[tuple], excl: list[tuple], max_r: int,
                 diff_thr: int) -> dict:
    H, W = rec.shape[:2]
    keep = np.ones((H, W), bool)
    for _, (x0, y0, x1, y1) in excl:
        keep[y0:y1, x0:x1] = False
    ya, yb = luma(rec), luma(gold)
    smap = ssim_map(ya, yb)
    res = {'psnr': round(psnr(rec, gold, keep), 3), 'ssim': round(float(smap[keep].mean()) if keep.any() else math.nan, 5),
           'masks': [], 'excluded': []}
    free = keep.copy()
    for name, r in masks:
        x0, y0, x1, y1 = r
        sel = np.zeros((H, W), bool)
        sel[y0:y1, x0:x1] = True
        free[y0:y1, x0:x1] = False
        sel &= keep
        if int(sel.sum()) < 64:
            continue
        sil = edge_silhouette(ya, yb, r, keep, max_r)
        res['masks'].append({'target': name, 'rect': list(r), 'pixels': int(sel.sum()), 'psnr': round(psnr(rec, gold, sel), 3),
                             'ssim': round(float(smap[sel].mean()), 5), 'silhouette_px': sil['px'], 'edges': sil['edges']})
    for name, (x0, y0, x1, y1) in excl:
        res['excluded'].append({'name': name, 'rect': [x0, y0, x1, y1],
                                'psnr': round(psnr(rec[y0:y1, x0:x1], gold[y0:y1, x0:x1]), 3)})
    res['unexplained'] = unexplained_regions(rec, gold, free, diff_thr, max(64, int(W * H * 0.0005)))
    return res


def replaydiff_out(args, gdir: Path) -> Path:
    if getattr(args, 'out', None):
        return standalone_out(args, 'replaydiff')
    root = gdir.parent if gdir.name == 'golden' else gdir
    out = root / 'replaydiff'
    out.mkdir(parents=True, exist_ok=True)
    return out


def golden_paths(args) -> tuple[Path, Path, dict]:
    root = Path(args.take).resolve()
    run = read_json(root / 'run.json')
    if (root / 'golden').is_dir():
        gdir = root / 'golden'
    elif str(run.get('mode') or '') == 'golden' or root.name == 'golden':
        gdir = root
    else:
        raise FileNotFoundError(f'{root}: no golden/ folder (run "mcam golden <spec> <out>" first)')
    grun = read_json(gdir / 'run.json')
    if args.record:
        rec = Path(args.record).resolve()
    else:
        rec = MT.find_record_dir(gdir, grun, 'golden') or MT.find_record_dir(root, run, str(run.get('mode') or ''))
    if rec is None or not (rec / 'game').is_dir():
        raise FileNotFoundError(f'record folder with game/ frames not found ({rec}); record with replay.record.captureGame true '
                                f'or pass --record <dir>')
    return gdir, rec, grun


def frame_files(folder: Path) -> dict:
    out = {}
    if folder.is_dir():
        for p in folder.iterdir():
            m = MT.FRAME_RE.match(p.name)
            if m and p.suffix.lower() in ('.jpg', '.jpeg', '.png'):
                out[int(m.group(1))] = p
    return out


def cmd_replaydiff(args) -> int:
    try:
        gdir, rec, grun = golden_paths(args)
    except FileNotFoundError as e:
        print(f'error: {e}', file=sys.stderr)
        return 2
    gold_files, rec_files = frame_files(gdir), frame_files(rec / 'game')
    samples = sorted(set(gold_files) & set(rec_files))
    every = max(1, int(getattr(args, 'every', None) or 1))
    samples = samples[::every]
    if not samples:
        print(f'error: no sample has both {gdir}/<sample>.jpg and {rec / "game"}/<sample>.jpg', file=sys.stderr)
        return 2
    out = replaydiff_out(args, gdir)
    excl_spec = load_exclusions(getattr(args, 'exclude', None))
    poses = rig_poses(MT.read_csv(rec / 'rig.csv'))
    tracks: dict[int, list] = {}
    for r in MT.read_csv(rec / 'tracks.csv'):
        if MT.inum(r.get('ok'), 0) != 1:
            continue
        mn = [MT.fnum(r.get(f'bmin_{k}')) for k in 'xyz']
        mx = [MT.fnum(r.get(f'bmax_{k}')) for k in 'xyz']
        if all(math.isfinite(v) for v in mn + mx):
            tracks.setdefault(MT.inum(r.get('step')), []).append((r.get('target', ''), mn, mx))
    pad, max_r = int(args.pad), int(args.max_r)
    ssim_min, sil_max = float(args.ssim), float(args.sil_px)
    sil_keys = [k.strip().lower() for k in str(getattr(args, 'sil_targets', None) or 'rope').split(',') if k.strip()]

    def sil_judged(target: str) -> bool:
        t = (target or '').lower()
        return any(k == '*' or k in t for k in sil_keys)
    probe = load_rgb(rec_files[samples[0]])
    H, W = probe.shape[:2]
    warns = []
    if not poses:
        warns.append('record/rig.csv has no camera poses: no object masks (global metrics only)')
    elif not tracks:
        warns.append('record/tracks.csv has no target bounds: no object masks (global metrics only)')

    def cam_at(s: int) -> dict | None:
        if s in poses:
            return poses[s]
        if not poses:
            return None
        keys = np.array(sorted(poses))
        return poses[int(keys[np.argmin(np.abs(keys - s))])]

    noise = {k: load_rgb(gdir / 'noise' / f'n{k:03d}.jpg') for k in GOLDEN_NOISE}
    nz = {}
    c0 = cam_at(0)
    ex0 = exclusion_rects(excl_spec, c0, W, H)
    keep0 = np.ones((H, W), bool)
    for _, (x0, y0, x1, y1) in ex0:
        keep0[y0:y1, x0:x1] = False
    base = noise.get(0)
    noise_masks = []
    noise_sil = {}
    if base is not None and base.shape[:2] == (H, W):
        for k in GOLDEN_NOISE[1:]:
            if noise.get(k) is not None and noise[k].shape == base.shape:
                nz[f'N{k}'] = round(psnr(base, noise[k], keep0), 3)
        if noise.get(1) is not None and noise[1].shape == base.shape:
            m0 = sample_masks(tracks, c0, 0, W, H, pad)
            pr = compare_pair(base, noise[1], m0, ex0, max_r, int(args.diff))
            nz['ssim1'] = pr['ssim']
            noise_masks = [{'target': m['target'], 'psnr': m['psnr'], 'silhouette_px': m['silhouette_px']} for m in pr['masks']]
    noise_sil = {m['target']: m['silhouette_px'] for m in noise_masks if m['silhouette_px'] is not None}
    floor = [v for k, v in nz.items() if k in ('N1', 'N120') and math.isfinite(v)]
    N = min(floor) if floor else math.nan
    if not floor:
        warns.append(f'no noise floor: {gdir / "noise"} lacks n000/n001/n120 (thresholds fall back to 32 / 30 dB)')
    g_min = min(32.0, N - 6.0) if math.isfinite(N) else 32.0
    m_min = min(30.0, N - 8.0) if math.isfinite(N) else 30.0
    print(f'replaydiff {gdir} vs {rec / "game"}: {len(samples)} sample(s), {W}x{H}, noise N1 {nz.get("N1")} N16 {nz.get("N16")} '
          f'N120 {nz.get("N120")} -> N {N if math.isfinite(N) else "n/a"}; thresholds global >= {g_min:.2f} dB, masks >= '
          f'{m_min:.2f} dB, SSIM >= {ssim_min}, silhouette <= {sil_max:g} px', flush=True)

    def one(s: int) -> dict:
        a, b = load_rgb(rec_files[s]), load_rgb(gold_files[s])
        if a is None or b is None:
            return {'sample': s, 'error': 'decode failed'}
        note = ''
        if b.shape != a.shape:
            b = cv2.resize(b, (a.shape[1], a.shape[0]), interpolation=cv2.INTER_AREA)
            note = 'golden resized to the record size'
        cam = cam_at(s)
        r = compare_pair(a, b, sample_masks(tracks, cam, s, W, H, pad), exclusion_rects(excl_spec, cam, W, H), max_r, int(args.diff))
        r['sample'] = s
        if note:
            r['note'] = note
        return r

    from concurrent.futures import ThreadPoolExecutor
    with ThreadPoolExecutor(MC.threads_of(args)) as pool:
        rows = list(pool.map(one, samples))
    bad_dec = [r['sample'] for r in rows if 'error' in r]
    rows = [r for r in rows if 'error' not in r]
    checks = []

    def judge(name: str, vals: list[tuple[int, float]], ok_fn, worst_fn, unit: str) -> None:
        fails = [s for s, v in vals if not ok_fn(v)]
        worst = worst_fn([v for _, v in vals]) if vals else None
        checks.append({'check': name, 'ok': not fails, 'n': len(vals), 'worst': worst, 'unit': unit, 'failed': len(fails),
                       'samples': fails[:20]})

    judge('global_psnr', [(r['sample'], r['psnr']) for r in rows], lambda v: v >= g_min, min, f'dB >= {g_min:.2f}')
    judge('global_ssim', [(r['sample'], r['ssim']) for r in rows], lambda v: v >= ssim_min, min, f'>= {ssim_min}')
    mvals = [(r['sample'], m) for r in rows for m in r['masks']]
    if mvals:
        judge('mask_psnr', [(s, m['psnr']) for s, m in mvals], lambda v: v >= m_min, min, f'dB >= {m_min:.2f}')
        judge('mask_ssim', [(s, m['ssim']) for s, m in mvals], lambda v: v >= ssim_min, min, f'>= {ssim_min}')
        sil = [(s, m['silhouette_px']) for s, m in mvals if m['silhouette_px'] is not None and sil_judged(m['target'])]
        if sil:
            judge('silhouette', sil, lambda v: v <= sil_max, max, f'px <= {sil_max:g} at 99 % coverage ({",".join(sil_keys)} targets)')
        info = sorted({m['target'] for _, m in mvals if m['silhouette_px'] is not None and not sil_judged(m['target'])})
        if info:
            warns.append(f'silhouette reported, not judged, for {len(info)} non-rope target(s) (REPLAY_DESIGN 9.2 R3 judges the rope; '
                         f'Canny edges of dark masks are TAA grain, noise-floor silhouette {noise_sil or "n/a"}): ' + ', '.join(t[:48] for t in info))
    else:
        warns.append('no visible object mask in any sample: mask checks skipped')
    rec_enc = str((read_json(rec / 'record.json').get('game_frames_encoder') or ''))
    gold_enc = str(((grun.get('golden') or {}).get('encoder') if isinstance(grun.get('golden'), dict) else '') or '')
    encoders = {'record': rec_enc, 'golden': gold_enc}
    if rec_enc and gold_enc and rec_enc.split(' (')[0] != gold_enc.split(' (')[0]:
        warns.append(f'record and golden frames come from different JPEG encoders ({rec_enc} / {gold_enc}): PSNR includes the encoder difference')
    recd = grun.get('recording') if isinstance(grun.get('recording'), dict) else {}
    if 'readbackErrors' in recd:
        rb = MT.inum(recd.get('readbackErrors'), 0)
        checks.append({'check': 'readback', 'ok': rb == 0, 'n': 1, 'worst': rb, 'unit': 'objects off by > 1 mm / 0.05 deg',
                       'failed': int(rb > 0), 'samples': []})
    else:
        warns.append('golden run.json has no recording.readbackErrors: preCull read-back not judged')
    if bad_dec:
        checks.append({'check': 'decode', 'ok': False, 'n': len(bad_dec), 'worst': None, 'unit': 'frames', 'failed': len(bad_dec),
                       'samples': bad_dec[:20]})
    targets: dict[str, dict] = {}
    for s, m in mvals:
        t = targets.setdefault(m['target'], {'target': m['target'], 'samples': 0, 'psnr_min': 99.0, 'ssim_min': 1.0, 'silhouette_max': None,
                                             'silhouette_judged': sil_judged(m['target']), 'silhouette_noise': noise_sil.get(m['target'])})
        t['samples'] += 1
        t['psnr_min'] = min(t['psnr_min'], m['psnr'])
        t['ssim_min'] = min(t['ssim_min'], m['ssim'])
        if m['silhouette_px'] is not None:
            t['silhouette_max'] = max(t['silhouette_max'] or 0, m['silhouette_px'])
    excluded: dict[str, list] = {}
    for r in rows:
        for e in r['excluded']:
            excluded.setdefault(e['name'], []).append(e['psnr'])
    unexplained = sorted(({'sample': r['sample'], **u} for r in rows for u in r['unexplained']), key=lambda u: -u['area'])[:10]
    passed = all(c['ok'] for c in checks)
    doc = {'golden': str(gdir), 'record': str(rec), 'samples': len(rows), 'size': [W, H], 'pass': passed, 'exit': 0 if passed else 1,
           'noise': {**nz, 'N': N if math.isfinite(N) else None, 'masks': noise_masks},
           'thresholds': {'global_psnr': g_min, 'mask_psnr': m_min, 'ssim': ssim_min, 'silhouette_px': sil_max, 'pad': pad,
                          'diff': int(args.diff), 'silhouette_targets': sil_keys},
           'encoders': encoders,
           'checks': checks, 'targets': list(targets.values()),
           'excluded': {k: {'samples': len(v), 'psnr_min': min(v), 'psnr_median': float(np.median(v))} for k, v in excluded.items()},
           'unexplained_top': unexplained, 'warnings': warns, 'rows': rows}
    (out / 'replaydiff.json').write_text(json.dumps(doc, indent=1, default=str), encoding='utf-8')
    lines = [f'REPLAYDIFF {"PASS" if passed else "FAIL"}: {len(rows)} sample(s), noise N {N if math.isfinite(N) else "n/a"} '
             f'(N1 {nz.get("N1")}, N16 {nz.get("N16")}, N120 {nz.get("N120")})']
    for c in checks:
        lines.append(f'{"ok  " if c["ok"] else "FAIL"} {c["check"]:<12} worst {c["worst"]} ({c["unit"]}), {c["failed"]}/{c["n"]} failed'
                     + (f', samples {c["samples"][:8]}' if c['failed'] else ''))
    for t in targets.values():
        lines.append(f'     target {t["target"][:48]:<48} {t["samples"]:4d} samples, PSNR min {t["psnr_min"]:.2f}, SSIM min '
                     f'{t["ssim_min"]:.4f}, silhouette max {t["silhouette_max"]}' + ('' if t['silhouette_judged'] else ' (reported)'))
    for k, v in doc['excluded'].items():
        lines.append(f'     excluded {k}: {v["samples"]} samples, PSNR min {v["psnr_min"]:.2f} (reported, not judged)')
    for u in unexplained[:5]:
        lines.append(f'     unexplained difference: sample {u["sample"]} rect {u["rect"]} area {u["area"]} px, mean diff {u["mean_diff"]}')
    for w in warns:
        lines.append(f'warn {w}')
    text = '\n'.join(lines)
    (out / 'replaydiff.txt').write_text(text + '\n', encoding='utf-8')
    print(text)
    replaydiff_sheet(rows, rec_files, gold_files, out / 'replaydiff.png', g_min, m_min)
    print(f'wrote {out / "replaydiff.json"}, replaydiff.txt and replaydiff.png')
    return 0 if passed else 1


def replaydiff_sheet(rows: list[dict], rec_files: dict, gold_files: dict, path: Path, g_min: float, m_min: float) -> None:
    if not rows:
        return

    def margin(r: dict) -> float:
        return min([r['psnr'] - g_min] + [m['psnr'] - m_min for m in r['masks']])

    worst = sorted(rows, key=margin)[:6]
    size = (480, 270)
    tiles, titles = [], []
    for r in worst:
        a, b = load_rgb(rec_files[r['sample']]), load_rgb(gold_files[r['sample']])
        if b.shape != a.shape:
            b = cv2.resize(b, (a.shape[1], a.shape[0]), interpolation=cv2.INTER_AREA)
        d = np.max(np.abs(a.astype(np.int16) - b.astype(np.int16)), axis=2)
        heat = cv2.cvtColor(cv2.applyColorMap(np.clip(d * 4, 0, 255).astype(np.uint8), cv2.COLORMAP_INFERNO), cv2.COLOR_BGR2RGB)
        row = []
        for im in (a, b, heat):
            im = np.ascontiguousarray(im)
            for m in r['masks']:
                x0, y0, x1, y1 = m['rect']
                cv2.rectangle(im, (x0, y0), (x1 - 1, y1 - 1), (120, 220, 120), max(1, im.shape[1] // 640))
            for e in r['excluded']:
                x0, y0, x1, y1 = e['rect']
                cv2.rectangle(im, (x0, y0), (x1 - 1, y1 - 1), (150, 150, 150), max(1, im.shape[1] // 640))
            for u in r['unexplained']:
                x0, y0, x1, y1 = u['rect']
                cv2.rectangle(im, (x0, y0), (x1 - 1, y1 - 1), (230, 70, 60), max(1, im.shape[1] // 640))
            row.append(cv2.resize(im, size, interpolation=cv2.INTER_AREA))
        tiles.append(row)
        mm = min((m['psnr'] for m in r['masks']), default=math.nan)
        titles.append(f'sample {r["sample"]}\nPSNR {r["psnr"]:.1f} / mask {mm:.1f}\nSSIM {r["ssim"]:.3f}')
    img = tile_grid(tiles, titles, ['record game', 'golden replay', '|diff| x4'],
                    f'replaydiff: worst {len(worst)} samples (green: object masks, grey: excluded, red: unexplained)', None, size)
    save(img, path)


def onset(x: np.ndarray, start: int, sr: int, thr_db: float, rise_db: float = 12.0, window_s: float = 0.5) -> dict:
    start = max(0, int(start))
    seg = np.abs(x[start:start + int(window_s * sr)]).astype(np.float64)
    pre_db = MT.dbfs(x[max(0, start - int(0.1 * sr)):start])
    if not len(seg):
        return {'abs_ms': None, 'rel_ms': None, 'pre_dbfs': round(pre_db, 1)}
    thr = 10 ** (thr_db / 20.0)
    hit = np.flatnonzero(seg > thr)
    hop = max(1, int(0.005 * sr))
    cs = np.concatenate([[0.0], np.cumsum(seg * seg)])
    env = np.sqrt((cs[hop:] - cs[:-hop]) / hop)
    rel = np.flatnonzero(env > max(thr, 10 ** ((pre_db + rise_db) / 20.0)))
    return {'abs_ms': round(hit[0] / sr * 1000.0, 2) if len(hit) else None,
            'rel_ms': round((rel[0] + hop // 2) / sr * 1000.0, 2) if len(rel) else None, 'pre_dbfs': round(pre_db, 1)}


def xcorr_offset(x: np.ndarray, y: np.ndarray, m: int) -> tuple[int, float]:
    n = len(x)
    x = x - x.mean()
    y = y - y.mean()
    nfft = 1 << int(math.ceil(math.log2(n + len(y))))
    c = np.fft.irfft(np.conj(np.fft.rfft(x, nfft)) * np.fft.rfft(y, nfft), nfft)[:2 * m + 1]
    cs = np.concatenate([[0.0], np.cumsum(y.astype(np.float64) ** 2)])
    norm = np.sqrt(np.maximum((cs[n:n + 2 * m + 1] - cs[:2 * m + 1]) * float(np.dot(x, x)), 1e-20))
    c = c / norm
    k = int(np.argmax(c))
    return k - m, float(c[k])


def meta_frames(rows: list[dict]) -> dict:
    by: dict[int, dict] = {}
    for r in rows:
        f = MT.inum(r.get('frame'))
        if f < 0 or MT.inum(r.get('captured'), 1) != 1:
            continue
        by[f] = r
    ks = sorted(by)
    return {'frame': np.array(ks, np.int64), 't': np.array([MT.fnum(by[k].get('game_time')) for k in ks]),
            'real': np.array([MT.fnum(by[k].get('real_time')) for k in ks]),
            'speed': np.array([MT.fnum(by[k].get('speed'), 1.0) for k in ks])}


def cmd_audiosync(args) -> int:
    root = Path(args.take).resolve()
    if not root.is_dir():
        print(f'error: {root} is not a folder', file=sys.stderr)
        return 2
    run = read_json(root / 'run.json')
    mode = str(run.get('mode') or '')
    rec = Path(args.record).resolve() if args.record else MT.find_record_dir(root, run, mode)
    out = standalone_out(args, (run.get('name') or root.name) + '_audiosync')
    rep = Report()
    doc: dict = {'take': str(root), 'record': str(rec) if rec else None}

    def finish(code: int) -> int:
        doc.update({'errors': rep.count('error'), 'warnings': rep.count('warn'), 'exit': code, 'items': rep.items})
        (out / 'audiosync.json').write_text(json.dumps(doc, indent=1, default=str), encoding='utf-8')
        order = {'error': 0, 'warn': 1, 'info': 2}
        lines = [f'AUDIOSYNC {root}: {rep.count("error")} errors, {rep.count("warn")} warnings -> exit {code}']
        for it in sorted(rep.items, key=lambda i: order[i['level']]):
            det = it.get('detail')
            lines.append(f'{it["level"].upper():<5} {it["check"]:<16} {it["msg"]}' + (f'  {json.dumps(det, default=str)}' if det else ''))
        text = '\n'.join(lines)
        (out / 'audiosync.txt').write_text(text + '\n', encoding='utf-8')
        print(text)
        print(f'wrote {out / "audiosync.json"} and audiosync.txt')
        return code

    if rec is None:
        rep.add('error', 'recording', 'record folder not found (pass --record <dir>)')
        return finish(2)
    rj = read_json(rec / 'record.json')
    st = MT.audio_state(root, rec, rj if rj else None)
    doc['audio_state'] = st
    if st['capture'] != 'record':
        rep.add('error', 'capture', st['verdict'])
        return finish(2)
    try:
        w = MT.read_wav(rec / 'audio.wav')
    except TakeError as e:
        rep.add('error', 'wav', str(e))
        return finish(2)
    for p in w.problems:
        rep.add('error', 'wav_riff', f'audio.wav: {p}')
    rep.add('info', 'wav', f'audio.wav: {w.rate} Hz, {w.channels} ch, {w.bits} bit, {w.seconds:.3f} s, {len(w.markers)} cue markers, '
                           f'RMS {MT.dbfs(w.mono()):.1f} dBFS')
    rm = MT.read_csv(rec / 'record_meta.csv')
    rt = np.array([MT.fnum(r.get('game_time')) for r in rm])
    rr = np.array([MT.fnum(r.get('real_time')) for r in rm])
    ok = np.isfinite(rt) & np.isfinite(rr)
    rt, rr = rt[ok], rr[ok]

    def real_of_game(t: float) -> float:
        return float(np.interp(t, rt, rr)) if len(rt) > 1 else math.nan

    if len(rt) > 1:
        gap = np.diff(rr) - np.diff(rt)
        big = np.flatnonzero(gap > 0.05)
        if len(big):
            rep.add('info', 'wall_gaps', f'{len(big)} wall-time gap(s) where real time ran ahead of game time, {float(gap[big].sum()):.3f} s '
                                         f'total (cut by mcam_audio.py track)', at=[round(float(rt[i]), 3) for i in big[:10]])
    else:
        rep.add('warn', 'record_meta', 'record_meta.csv missing or empty: event offsets need it')
    fit = MT.fit_markers(w, MT.read_csv(rec / 'audio_sync.csv'), real_of_game)
    doc['fit'] = fit
    fps = MT.fnum(run.get('fps'), 30.0)
    frame_ms = 1000.0 / fps
    if not fit['ok']:
        rep.add('error', 'fit', fit['reason'])
    else:
        lvl = 'error' if fit['slope_error'] >= MT.SLOPE_LIMIT else 'info'
        rep.add(lvl, 'slope', f'fit slope {fit["a"]:.1f} samples/s = {fit["ratio"]:.5f} x the fmt rate {w.rate} '
                              f'({fit["slope_error"] * 100:.3f} %, limit {MT.SLOPE_LIMIT * 100:g} %)')
        q = int(fit.get('quantum') or 1)
        q_ms = q / w.rate / math.sqrt(12.0) * 1000.0 if q > 1 else 0.0
        limit = max(float(args.residual_ms), 1.25 * q_ms)
        lvl = 'error' if fit['residual_ms'] >= limit else 'info'
        rep.add(lvl, 'residual', f'{fit["lag0"]} lag-0 markers, residual RMS {fit["residual_ms"]:.2f} ms, max {fit["residual_max_ms"]:.2f} ms '
                                 f'(limit {float(args.residual_ms):g} ms RMS'
                                 + (f'; markers sit on a {q}-sample grid, uniform rounding alone gives {q_ms:.2f} ms RMS, limit raised '
                                    f'to {limit:.2f} ms' if limit > float(args.residual_ms) else '') + ')')
        for e in fit['events']:
            rep.add('info', 'event_marker', f'{e["label"]} (lag {e["lag"]}): {e.get("offset_event_ms", e["offset_post_ms"])} ms from '
                                            f'p(r(t)) at game {e["game_time"]}')
    if fit.get('missing'):
        rep.add('warn', 'markers', f'{len(fit["missing"])} audio_sync.csv label(s) without a cue in audio.wav', labels=fit['missing'][:10])
    usable = st['usable']
    if usable is False:
        rep.add('error', 'usable', 'record.json audio.usable is false: compose and track refuse this audio')
    elif usable is None:
        rep.add('warn', 'usable', 'record.json audio.usable not set (run mcam_audio.py align)')
    elif fit['ok'] and not fit['usable']:
        rep.add('error', 'usable', 'record.json says usable but this fit exceeds the slope limit')
    their = MT.fnum(st.get('slope'))
    if fit['ok'] and math.isfinite(their):
        ratio = their / w.rate if their > 1000 else their
        if abs(ratio - fit['ratio']) > 5e-4:
            rep.add('warn', 'slope_mismatch', f'record.json audio.slope {their} differs from this fit ({fit["a"]:.1f} samples/s)')
    mono = w.mono()
    events = [(MT.fnum(r.get('game_time')), r.get('name', '')) for r in (MT.read_csv(root / 'events.csv') or MT.read_csv(rec / 'events.csv'))
              if r.get('kind') == 'event']
    mf = meta_frames(MT.read_csv(root / 'meta.csv'))
    track_path = root / 'audio_track.wav'
    tw = None
    if len(mf['frame']):
        want = (int(mf['frame'].max()) + 1) / fps
        if not track_path.is_file():
            rep.add('warn', 'track', 'no audio_track.wav in the take (run mcam_audio.py track)')
        else:
            try:
                tw = MT.read_wav(track_path)
            except TakeError as e:
                rep.add('error', 'track', str(e))
            if tw is not None:
                for p in tw.problems:
                    rep.add('error', 'track_riff', f'audio_track.wav: {p}')
                lvl = 'error' if abs(tw.seconds - want) > 1.0 / fps + 1e-6 else 'info'
                rep.add(lvl, 'track_duration', f'audio_track.wav {tw.seconds:.4f} s vs output timeline {want:.4f} s '
                                               f'({len(mf["frame"])} frames at {fps:g} fps)')
    if tw is not None and fit['ok'] and tw.rate == w.rate and np.isfinite(mf['real']).any():
        sr = w.rate
        tmono = tw.mono()
        win, m = int(0.5 * sr), int(0.1 * sr)
        offs, skipped = [], 0
        step = max(1, int(round(fps / 4)))
        f_idx = mf['frame']
        for k in range(0, len(f_idx), step):
            span = slice(k, k + int(math.ceil(0.5 * fps)) + 1)
            if len(mf['speed'][span]) < 2 or np.any(np.abs(mf['speed'][span] - 1.0) > 1e-6) or np.any(np.diff(f_idx[span]) != 1):
                continue
            o = int(round(f_idx[k] / fps * sr))
            x = tmono[o:o + win]
            p = int(round(fit['a'] * mf['real'][k] + fit['b']))
            if len(x) < win or p - m < 0 or p + win + m > len(mono) or not math.isfinite(mf['real'][k]):
                continue
            if MT.dbfs(x) < -60.0:
                skipped += 1
                continue
            lag, corr = xcorr_offset(x, mono[p - m:p + win + m], m)
            offs.append({'frame': int(f_idx[k]), 't': round(float(mf['t'][k]), 4), 'offset_ms': round(lag / sr * 1000.0, 2),
                         'corr': round(corr, 3)})
        doc['xcorr'] = offs
        if offs:
            worst = max(abs(o['offset_ms']) for o in offs)
            lvl = 'error' if worst > frame_ms else 'info'
            rep.add(lvl, 'track_sync', f'{len(offs)} speed-1 windows: audio_track vs the record WAV at p(r(T)), worst offset '
                                       f'{worst:.2f} ms (limit one frame {frame_ms:.1f} ms), min corr {min(o["corr"] for o in offs):.3f}'
                                       + (f', {skipped} silent windows skipped' if skipped else ''))
        else:
            rep.add('info', 'track_sync', f'no speed-1 window with signal to cross-correlate ({skipped} silent)')
    if fit['ok'] and events and len(rt) > 1:
        thr = float(args.threshold_db)
        tmono = tw.mono() if tw is not None else None
        for t_e, name in events:
            p_e = fit['a'] * real_of_game(t_e) + fit['b']
            ro = onset(mono, int(round(p_e)), w.rate, thr)
            item = {'event': name, 'game_time': round(t_e, 4), 'record': ro}
            msg = f'{name} at {t_e:.3f} s: record onset {ro["rel_ms"]} ms (abs -{abs(thr):g} dBFS {ro["abs_ms"]} ms, pre {ro["pre_dbfs"]} dBFS)'
            lvl = 'info'
            if tmono is not None and len(mf['frame']) > 1 and tw.rate == w.rate:
                o_e = float(np.interp(t_e, mf['t'], mf['frame'].astype(np.float64))) / fps
                s_e = float(np.interp(t_e, mf['t'], mf['speed']))
                to = onset(tmono, int(round(o_e * tw.rate)), tw.rate, thr, window_s=0.5 / max(s_e, 0.05))
                item['track'] = to
                msg += f'; track onset {to["rel_ms"]} ms at output {o_e:.3f} s (speed {s_e:g})'
                if ro['rel_ms'] is not None and to['rel_ms'] is not None:
                    diff = to['rel_ms'] - ro['rel_ms'] / max(s_e, 1e-6)
                    item['diff_ms'] = round(diff, 2)
                    msg += f', difference {diff:.1f} ms'
                    if abs(diff) > frame_ms:
                        lvl = 'warn'
            rep.add(lvl, 'onset', msg, **item)
    for v in getattr(args, 'video', None) or []:
        vp = Path(v)
        info = MC.ffprobe(vp)
        if 'error' in info:
            rep.add('error', 'video', f'{vp.name}: ffprobe failed: {info["error"]}')
            continue
        ss = info.get('streams', [])
        vs = [s for s in ss if s.get('codec_type') == 'video']
        aus = [s for s in ss if s.get('codec_type') == 'audio']
        if len(vs) != 1 or len(aus) != 1 or vs[0].get('codec_name') != 'hevc':
            rep.add('error', 'video', f'{vp.name}: {len(vs)} video ({vs[0].get("codec_name") if vs else "-"}) and {len(aus)} audio streams '
                                      f'(want one hevc and one audio)')
            continue
        dv, da = MC.stream_seconds(vs[0]), MC.stream_seconds(aus[0])
        lvl = 'error' if not (math.isfinite(dv) and math.isfinite(da)) or abs(dv - da) > 1.0 / fps + 1e-6 else 'info'
        rep.add(lvl, 'video', f'{vp.name}: hevc {vs[0].get("codec_tag_string")} {dv:.4f} s + {aus[0].get("codec_name")} '
                              f'{aus[0].get("profile") or ""} {aus[0].get("sample_rate")} Hz {da:.4f} s')
    return finish(1 if rep.count('error') else 0)


STANDALONE = {'replaydiff': cmd_replaydiff, 'audiosync': cmd_audiosync}


COMMANDS = {'qa': cmd_qa, 'sheet': cmd_sheet, 'sync': cmd_sync, 'paths': cmd_paths, 'timeline': cmd_timeline, 'diverge': cmd_diverge}


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('command', choices=sorted(set(COMMANDS) | set(STANDALONE)))
    p.add_argument('take')
    p.add_argument('other', nargs='?', help='diverge: second take')
    p.add_argument('--record', help='replaydiff/audiosync: record folder (default from run.json or <take>/record)')
    p.add_argument('--exclude', help='replaydiff: JSON list of {"name", "rect": [x0,y0,x1,y1]} or {"name", "box": {"min", "max"}} '
                                     '(_Time-animated regions: reported, not judged)')
    p.add_argument('--pad', type=int, default=6, help='replaydiff: mask padding in pixels')
    p.add_argument('--max-r', type=int, default=16, help='replaydiff: silhouette search radius in pixels')
    p.add_argument('--ssim', type=float, default=0.95, help='replaydiff: SSIM threshold (global and masks)')
    p.add_argument('--sil-px', type=float, default=2.0, help='replaydiff: silhouette distance limit at 99 %% coverage')
    p.add_argument('--sil-targets', default='rope', help='replaydiff: comma list of target name parts whose silhouette is judged '
                                                         '(default rope, REPLAY_DESIGN 9.2 R3; * = every mask); the rest is reported')
    p.add_argument('--diff', type=int, default=24, help='replaydiff: per-pixel difference for unexplained regions')
    p.add_argument('--video', action='append', help='audiosync: muxed video to check (repeatable)')
    p.add_argument('--threshold-db', type=float, default=-40.0, help='audiosync: onset level in dBFS')
    p.add_argument('--residual-ms', type=float, default=5.0, help='audiosync: marker fit residual RMS limit')
    p.add_argument('--out')
    p.add_argument('--compose')
    p.add_argument('--grade')
    p.add_argument('--no-grade', action='store_true')
    p.add_argument('--auto')
    p.add_argument('--edit')
    p.add_argument('--storyboard', action='store_true', help='sheet: also the cut storyboard of compose.edit')
    p.add_argument('--layout')
    p.add_argument('--layouts', action='store_true', help='sheet: also layout previews')
    p.add_argument('--every', type=float)
    p.add_argument('--columns', type=int)
    p.add_argument('--cols', type=int)
    p.add_argument('--ab', action='store_true')
    p.add_argument('--cams')
    p.add_argument('--times', nargs='*')
    p.add_argument('--strict', action='store_true')
    p.add_argument('--dark-level', type=int, help=f'qa: 8-bit luma below which a pixel is dark (default {DARK_LEVEL})')
    p.add_argument('--dark-cover', type=float, help=f'qa: dark fraction above which a camera frame is near-black (default {DARK_COVER})')
    p.add_argument('--cell-dark-cover', type=float, help=f'qa: dark fraction above which a composed video cell is near-black (default {CELL_DARK_COVER})')
    p.add_argument('--blur-warn', type=float, help=f'qa: composed cell sharpness ratio to its neighbours below which a frame is blurred (warning, default {BLUR_WARN})')
    p.add_argument('--blur-fail', type=float, help=f'qa: ratio below which a frame is strongly blurred (error, default {BLUR_FAIL})')
    p.add_argument('--no-blur', action='store_true', help='qa: skip the blur check of composed videos')
    p.add_argument('--videos', action='append', help='qa: folder with compose_log.json of the composed videos (default the QA out folder)')
    p.add_argument('--no-outputs', action='store_true', help='qa: skip the black-frame check of the composed videos')
    p.add_argument('--taa')
    p.add_argument('--rate', type=float)
    p.add_argument('--group', type=int)
    p.add_argument('--event', action='append')
    p.add_argument('--fps', type=float)
    p.add_argument('--threads', type=int, default=0)
    p.add_argument('--force', action='store_true')
    p.add_argument('--labels', dest='labels', action='store_true', default=None)
    p.add_argument('--no-labels', dest='labels', action='store_false')
    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.command in STANDALONE:
        return STANDALONE[args.command](args)
    try:
        take = Take(Path(args.take), args.rate, args.group, MC.parse_events(args.event))
        if not len(take.base):
            raise TakeError('no complete output frames')
    except (TakeError, OSError) as e:
        print(f'error: unreadable take: {e}', file=sys.stderr)
        if args.command == 'qa' and args.out:
            out = Path(args.out)
            out.mkdir(parents=True, exist_ok=True)
            (out / 'qa.json').write_text(json.dumps({'take': args.take, 'exit': 2, 'error': str(e)}, indent=1), encoding='utf-8')
        return 2
    ctx = MC.make_ctx(take, args)
    try:
        return COMMANDS[args.command](ctx)
    except TakeError as e:
        print(f'error: {e}', file=sys.stderr)
        return 1
    finally:
        ctx.store.close()


if __name__ == '__main__':
    sys.exit(main())
