"""Fast review of the finals (the director angle and the layouts of a compose): full-resolution PNG frames at every review event
and every --every s (2), the composed-video QA of mcam_qa (black, near-black per layout cell, blur), the frames QA flags, and
contact sheets (director, layout, events with both finals side by side, QA flags on an output-time strip), all into one folder.
Reads the final MP4s (NVDEC; --cpu decodes on the CPU), never the camera videos. Exit 0 ok, 1 QA errors, 2 nothing to review.
Usage: python -I tools/fast_review.py <take> [--videos DIR] [--out DIR] [--every 2] [--events a,b] [--around 0] [--near 0.5]"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mcam_compose as MC
import mcam_qa as MQ
import mcam_take as MT

THUMB = (640, 360)
FLAG_KINDS = ('black', 'near_black', 'blur_fail', 'blur_warn')
FLAG_COLORS = {'black': (220, 60, 60), 'near_black': (235, 140, 50), 'blur_fail': (200, 80, 200), 'blur_warn': (150, 130, 210)}
MAX_FLAG_FRAMES = 16


def compose_range(videos: Path) -> str | None:
    log = videos / 'compose_log.json'
    if not log.is_file():
        return None
    for entry in reversed(json.loads(log.read_text(encoding='utf-8'))):
        argv = list(entry.get('argv') or [])
        if '--range' in argv and argv.index('--range') + 1 < len(argv):
            return argv[argv.index('--range') + 1]
        if argv:
            return None
    return None


def finals_of(ctx: MC.Ctx) -> list[tuple[str, Path, dict | None]]:
    keep = {f'angle_{MC.safe_name(c)}' for c in MC.finals_cams(ctx)}
    out = []
    for f, lay in MQ.output_videos(ctx):
        stem = f.stem.replace('_h265', '').replace('_draft', '')
        if stem in keep or stem.startswith('layout_'):
            out.append((stem, f, lay))
    return out


def video_frames(path: Path) -> int:
    info = MC.ffprobe(path)
    v = [s for s in info.get('streams', []) if s.get('codec_type') == 'video']
    return MT.inum(v[0].get('nb_frames'), -1) if v else -1


def extract(path: Path, idx: list[int], gpu: bool):
    W, H = MT.probe_size(path)
    sel = '+'.join(f'eq(n,{i})' for i in sorted(set(idx)))
    hw = ['-hwaccel', 'cuda', '-hwaccel_output_format', 'cuda'] if gpu else []
    pre = f'hwdownload,format={MT.gpu_download_format(path)},' if gpu else ''
    vf = f"select='{sel}',{pre}scale=in_color_matrix=bt709:in_range=tv:out_range=pc,format=rgb24"
    cmd = [MT.FFMPEG, '-hide_banner', '-loglevel', 'error', *hw, '-i', str(path), '-vf', vf, '-fps_mode', 'passthrough', '-f', 'rawvideo', '-']
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE)
    size = W * H * 3
    try:
        for i in sorted(set(idx)):
            buf = proc.stdout.read(size)
            if len(buf) < size:
                break
            yield i, np.frombuffer(buf, np.uint8).reshape(H, W, 3)
    finally:
        proc.stdout.close()
        proc.wait()


def save_frames(path: Path, picks: list[tuple[int, str]], gt: np.ndarray, fps: float, folder: Path, gpu: bool,
                pool: ThreadPoolExecutor) -> dict[int, dict]:
    folder.mkdir(parents=True, exist_ok=True)
    why = dict(picks)
    rows: dict[int, dict] = {}
    futs = []

    def write(img: np.ndarray, f: Path) -> None:
        Image.fromarray(img).save(f, compress_level=1)

    for j, img in extract(path, [j for j, _ in picks], gpu):
        t = float(gt[j]) if j < len(gt) else float('nan')
        f = folder / f'{j:05d}_t{t:08.3f}_{MC.safe_name(why[j])}.png'
        futs.append(pool.submit(write, img, f))
        rows[j] = {'frame': j, 'output_t': round(j / fps, 4), 'game_t': round(t, 4), 'why': why[j], 'file': f.name,
                   'thumb': cv2.resize(img, THUMB, interpolation=cv2.INTER_AREA)}
    for fu in futs:
        fu.result()
    with (folder / 'index.csv').open('w', encoding='utf-8', newline='') as fh:
        fh.write('frame,output_t,game_t,why,file\n')
        for j in sorted(rows):
            r = rows[j]
            fh.write(f'{r["frame"]},{r["output_t"]},{r["game_t"]},{r["why"]},{r["file"]}\n')
    return rows


def label(img: np.ndarray, text: str) -> np.ndarray:
    im = Image.fromarray(img)
    d = ImageDraw.Draw(im)
    f = MQ.font(18, mono=True)
    w = int(d.textlength(text, font=f)) + 12
    d.rectangle([0, 0, w, 26], fill=(0, 0, 0))
    d.text((6, 3), text, fill=(255, 220, 90), font=f)
    return np.asarray(im)


def grid(tiles: list[np.ndarray], cols: int, title: str) -> Image.Image:
    tw, th = THUMB
    rows = max(1, (len(tiles) + cols - 1) // cols)
    top, gap = 44, 4
    img = Image.new('RGB', (cols * (tw + gap) + gap, top + rows * (th + gap) + gap), MQ.BG)
    d = ImageDraw.Draw(img)
    d.text((10, 10), title, fill=MQ.FG, font=MQ.font(22))
    for i, t in enumerate(tiles):
        img.paste(Image.fromarray(t), (gap + (i % cols) * (tw + gap), top + (i // cols) * (th + gap)))
    return img


def timeline_strip(name: str, flags: dict, n: int, fps: float, events: list[tuple[float, str]], gt: np.ndarray, width: int = 2400) -> Image.Image:
    cells = sorted({c for k in FLAG_KINDS for c in (flags.get(k) or {})})
    row_h, left, top = 22, 230, 40
    rows = max(1, len(cells))
    img = Image.new('RGB', (left + width + 20, top + rows * row_h + 40), MQ.BG)
    d = ImageDraw.Draw(img)
    fs = MQ.font(16, mono=True)
    d.text((10, 8), f'{name}: QA flags over output time ({n / fps:.1f} s); red black, orange near-black, purple blur', fill=MQ.FG, font=MQ.font(18))
    x_of = lambda j: left + int(width * j / max(1, n - 1))
    for r, cell in enumerate(cells):
        y = top + r * row_h
        d.text((10, y + 3), cell[:26], fill=MQ.DIM, font=fs)
        d.rectangle([left, y + 4, left + width, y + row_h - 4], outline=(60, 60, 64))
        for k in FLAG_KINDS:
            for j in (flags.get(k) or {}).get(cell, []):
                x = x_of(j)
                d.line([x, y + 5, x, y + row_h - 5], fill=FLAG_COLORS[k])
    yb = top + rows * row_h + 4
    for te, en in events:
        j = int(np.argmin(np.abs(gt - te))) if len(gt) else 0
        x = x_of(j)
        d.line([x, top - 4, x, yb], fill=(90, 150, 90))
    for s in range(0, int(n / fps) + 1, 5):
        x = x_of(int(s * fps))
        d.text((x - 6, yb + 4), f'{s}', fill=MQ.DIM, font=fs)
    return img


def flag_frames(flags: dict, limit: int = MAX_FLAG_FRAMES) -> list[tuple[int, str]]:
    out: dict[int, str] = {}
    for k in FLAG_KINDS:
        for cell, idx in sorted((flags.get(k) or {}).items()):
            for a, b in MQ.runs(idx):
                if len(out) >= limit:
                    break
                out.setdefault((a + b) // 2, f'{k}_{cell.split("@")[0]}')
    return sorted(out.items())


def build_args(argv: list[str] | None) -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('take')
    p.add_argument('--videos', help='folder with compose_log.json of the finals (default the take folder)')
    p.add_argument('--out', help='output folder (default <videos>/fast_review)')
    p.add_argument('--every', type=float, default=2.0)
    p.add_argument('--events', help='comma list of event names / patterns / @event+s (default compose.review.events)')
    p.add_argument('--around', type=float, default=0.0, help='seconds around each event with frames every --near (0 = the event frame only)')
    p.add_argument('--near', type=float, default=0.5)
    p.add_argument('--range', help='t0:t1 of the compose (default: the --range of the last compose in compose_log.json)')
    p.add_argument('--threads', type=int, default=8)
    p.add_argument('--cpu', action='store_true', help='decode on the CPU')
    p.add_argument('--no-blur', action='store_true')
    return p.parse_args(argv)


def ctx_for(a: argparse.Namespace, videos: Path, out: Path) -> MC.Ctx:
    ns = MC.build_parser().parse_args(['review', a.take, '--out', str(out), '--cpu', '--force'] + (['--range', a.range] if a.range else []))
    ns.videos = [str(videos)]
    ns.no_blur = a.no_blur
    for k in ('dark_level', 'dark_cover', 'cell_dark_cover', 'blur_warn', 'blur_fail'):
        setattr(ns, k, None)
    return MC.make_ctx(MT.Take(Path(a.take)), ns)


def main(argv: list[str] | None = None) -> int:
    t0 = time.time()
    a = build_args(argv)
    take_root = Path(a.take).resolve()
    videos = Path(a.videos).resolve() if a.videos else take_root
    out = Path(a.out).resolve() if a.out else videos / 'fast_review'
    if not a.range:
        a.range = compose_range(videos)
    ctx = ctx_for(a, videos, out)
    finals = finals_of(ctx)
    if not finals:
        print(f'error: no finals in {videos / "compose_log.json"} (run mcam_compose.py finals first)', file=sys.stderr)
        return 2
    gpu = not a.cpu and MT.cuda_ok(finals[0][1])
    tl = MC.limit_timeline(ctx, MC.base_timeline(ctx))
    gt = np.array([f.t for f in tl.frames])
    fps = float(tl.fps)
    pats = [e for e in a.events.split(',') if e] if a.events else (ctx.cfg.get('review') or {}).get('events') or []
    events = MC.review_events(ctx, pats)
    picks = MC.review_picks(gt, events, a.every, a.near, a.around)
    rep = MQ.Report()
    timings: dict[str, float] = {'setup_s': round(time.time() - t0, 2)}
    for name, f, _ in finals:
        nf = video_frames(f)
        if nf != len(gt):
            rep.add('warn', 'timeline', f'{f.name}: {nf} frames, the take timeline in range {a.range or "all"} has {len(gt)}; times may be off')
    t1 = time.time()
    with ThreadPoolExecutor(max(2, a.threads)) as pool, ThreadPoolExecutor(2 * len(finals)) as jobs:
        qa_f = {name: jobs.submit(MQ.check_video, ctx, rep, f, lay, gpu) for name, f, lay in finals}
        fr_f = {name: jobs.submit(save_frames, f, picks, gt, fps, out / name, gpu, pool) for name, f, _ in finals}
        flags = {k: v.result() for k, v in qa_f.items()}
        frames = {k: v.result() for k, v in fr_f.items()}
        timings['frames_and_qa_s'] = round(time.time() - t1, 2)
        t2 = time.time()
        flagged = {name: flag_frames(flags[name]) for name, _, _ in finals}
        ff = {name: jobs.submit(save_frames, f, flagged[name], gt, fps, out / name / 'flags', gpu, pool) for name, f, _ in finals if flagged[name]}
        flag_rows = {k: v.result() for k, v in ff.items()}
        timings['flag_frames_s'] = round(time.time() - t2, 2)
    t3 = time.time()
    sheets = []
    for name, _, _ in finals:
        rows = frames[name]
        tiles = [label(r['thumb'], f'{r["output_t"]:6.2f} s  game {r["game_t"]:.2f}  {r["why"]}') for j, r in sorted(rows.items()) if r['why'] == 'every']
        p = out / f'sheet_{name}.jpg'
        grid(tiles, 4, f'{name}: every {a.every:g} s ({len(tiles)} frames, full size in {name}/)').save(p, quality=90)
        sheets.append(p)
    names = [n for n, _, _ in finals]
    ev_rows = sorted({r['frame'] for r in frames[names[0]].values() if r['why'] != 'every'})
    tiles = []
    for j in ev_rows:
        for n in names:
            r = frames[n].get(j)
            tiles.append(label(r['thumb'], f'{n} {r["why"]} game {r["game_t"]:.2f}') if r else np.zeros((THUMB[1], THUMB[0], 3), np.uint8))
    if tiles:
        p = out / 'sheet_events.jpg'
        grid(tiles, 2 * len(names) if len(names) <= 2 else len(names), f'events: {", ".join(names)} side by side ({len(ev_rows)} event frames)').save(p, quality=90)
        sheets.append(p)
    strips = [timeline_strip(n, flags[n], max(1, flags[n].get('frames') or len(gt)), fps, events, gt) for n in names]
    ftiles = [label(r['thumb'], f'{n} {r["why"]} {r["output_t"]:.2f} s') for n in names for _, r in sorted(flag_rows.get(n, {}).items())]
    W = max(s.width for s in strips)
    fl = grid(ftiles, 4, f'QA flag frames (middle of each flagged run, at most {MAX_FLAG_FRAMES} per final)') if ftiles else None
    H = sum(s.height for s in strips) + (fl.height if fl else 0)
    qa_img = Image.new('RGB', (max(W, fl.width if fl else 0), H), MQ.BG)
    y = 0
    for s in strips:
        qa_img.paste(s, (0, y))
        y += s.height
    if fl:
        qa_img.paste(fl, (0, y))
    p = out / 'sheet_qa.jpg'
    qa_img.save(p, quality=90)
    sheets.append(p)
    timings['sheets_s'] = round(time.time() - t3, 2)
    timings['total_s'] = round(time.time() - t0, 2)
    errors, warns = rep.count('error'), rep.count('warn')
    order = {'error': 0, 'warn': 1, 'info': 2}
    lines = [f'fast review {take_root.name}: {", ".join(names)}; {errors} errors, {warns} warnings; {timings["total_s"]} s']
    for it in sorted(rep.items, key=lambda i: order[i['level']]):
        lines.append(f'{it["level"].upper():<5} {it["check"]:<14} {it["msg"]}')
    (out / 'qa.txt').write_text('\n'.join(lines) + '\n', encoding='utf-8')
    doc = {'take': str(take_root), 'videos': str(videos), 'range': a.range, 'decode': 'gpu' if gpu else 'cpu', 'finals': {n: str(f) for n, f, _ in finals},
           'events': [[round(t, 4), n] for t, n in events], 'frames': {n: len(frames[n]) for n in names},
           'flag_frames': {n: len(flag_rows.get(n, {})) for n in names}, 'sheets': [str(s) for s in sheets], 'timings': timings,
           'errors': errors, 'warnings': warns, 'items': rep.items,
           'flags': {n: {k: {c: MQ.fmt_runs(MQ.runs(v), 50) for c, v in (flags[n].get(k) or {}).items() if v} for k in FLAG_KINDS} for n in names}}
    (out / 'fast_review.json').write_text(json.dumps(doc, indent=1, default=str), encoding='utf-8')
    print('\n'.join(lines))
    print(f'frames {doc["frames"]}, flag frames {doc["flag_frames"]}, sheets: ' + ', '.join(s.name for s in sheets))
    print(f'timings {json.dumps(timings)} -> {out}')
    return 1 if errors else 0


if __name__ == '__main__':
    sys.exit(main())
