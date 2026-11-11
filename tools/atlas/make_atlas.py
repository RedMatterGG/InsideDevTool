"""Build InsideDev's level atlas (data/atlas/*.tsv) from a static index of INSIDE's level files.

Object keys use the same " #n" numbering for same-named siblings as LevelCompare.Map, so a live object finds its row.
Reads only; run with: python -I make_atlas.py --index <levels dir> --audit <tables dir> --game <INSIDE dir> --out <dir>
Exit codes: 0 ok, 2 an input file is missing or not in the expected form.
"""
from __future__ import annotations

import argparse
import csv
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

TRANSFORMS = {'Transform', 'RectTransform'}
BUILTIN = {'Transform', 'RectTransform', 'MeshFilter', 'MeshRenderer', 'SkinnedMeshRenderer', 'BoxCollider', 'SphereCollider',
           'CapsuleCollider', 'MeshCollider', 'Rigidbody', 'Light', 'Animation', 'Animator', 'AudioSource', 'ParticleSystem',
           'ParticleSystemRenderer', 'LineRenderer', 'TrailRenderer', 'Camera', 'CanvasRenderer', 'Canvas', 'CharacterJoint',
           'HingeJoint', 'FixedJoint', 'SpringJoint', 'ConfigurableJoint', 'LensFlare', 'Projector', 'Cloth', 'WindZone'}
CHARACTER_TYPES = {0: 'Unknown', 1: 'Boy', 2: 'Huddle'}
UNCERTAIN_WIRE = 'created_outside_PreAwake'
KEEP_FIELDS = ('m_Children', 'm_Father', 'sender', 'receiver', 'sortedSavepoints', 'cachedSpawnPos', 'characterType', 'isCheckpointOnly')
FILE_KEYS = ('fileID', 'm_FileID', 'fileId')
LEVEL_RE = re.compile(r'level(\d+)$')


class AtlasError(Exception):
    pass


class Level:
    def __init__(self, file: str, scene: str):
        self.file = file
        self.scene = scene
        self.gos: dict[int, dict] = {}
        self.comps: dict[int, dict] = {}
        self.path: dict[int, str] = {}
        self.children: dict[int, list[int]] = defaultdict(list)

    def subtree(self, go: int) -> list[int]:
        out, stack = [], [go]
        while stack:
            g = stack.pop()
            out.append(g)
            stack.extend(self.children.get(g, ()))
        return out

    def scripts(self, gos: list[int]) -> list[str]:
        names = Counter()
        for g in gos:
            for cls, cid in self.gos[g]['components']:
                c = self.comps.get(cid, {})
                name = c.get('script') or cls
                if name not in BUILTIN and name != 'MonoBehaviour':
                    names[name] += 1
        return [n for n, _ in names.most_common(6)]


def local_ref(ref) -> int | None:
    """pathID of a reference into the same file; None for no reference or one into another file (it would name a wrong
    object of this level)."""
    if not isinstance(ref, dict) or ref.get('pathID') is None:
        return None
    for k in FILE_KEYS:
        v = ref.get(k)
        if isinstance(v, int) and not isinstance(v, bool) and v != 0:
            return None
    return ref['pathID']


def load_level(path: Path) -> Level:
    """One level index file <file>__<scene>.jsonl. Only what the tables use is kept in memory: of a game object its name and
    component list, of a component its class, script, game object and the fields named in KEEP_FIELDS."""
    file, scene = path.stem.split('__', 1)
    lv = Level(file, scene)
    with path.open(encoding='utf-8') as fh:
        for n, line in enumerate(fh, 1):
            if not line.strip():
                continue
            try:
                r = json.loads(line)
                kind, pid = r['rec'], r['pathID']
                if kind == 'gameobject':
                    lv.gos[pid] = {'name': r['name'], 'components': r['components']}
                elif kind == 'component':
                    f = r.get('fields') or {}
                    c = {'class': r['class'], 'script': r.get('script'), 'fields': {k: f[k] for k in KEEP_FIELDS if k in f}}
                    if 'go' in r:
                        c['go'] = r['go']
                    lv.comps[pid] = c
            except (ValueError, KeyError, TypeError) as e:
                raise AtlasError(f'{path.name} line {n}: {type(e).__name__}: {e}') from None
    tr_go = {pid: c['go'] for pid, c in lv.comps.items() if c['class'] in TRANSFORMS and c.get('go') in lv.gos}

    def walk(root: int) -> None:
        lv.path[tr_go[root]] = lv.gos[tr_go[root]]['name']
        stack = [root]
        while stack:
            tid = stack.pop()
            go = tr_go[tid]
            seen = Counter()
            kids = []
            for ref in lv.comps[tid]['fields'].get('m_Children') or []:
                ctid = local_ref(ref)
                if ctid not in tr_go:
                    continue
                cgo = tr_go[ctid]
                name = lv.gos[cgo]['name']
                seen[name] += 1
                if cgo in lv.path:
                    continue
                lv.path[cgo] = lv.path[go] + '/' + name + (f' #{seen[name]}' if seen[name] > 1 else '')
                lv.children[go].append(cgo)
                kids.append(ctid)
            stack.extend(reversed(kids))

    for tid, go in tr_go.items():
        if local_ref(lv.comps[tid]['fields'].get('m_Father')) not in tr_go and go not in lv.path:
            walk(tid)
    return lv


def read_tsv(path: Path) -> list[dict]:
    with path.open(encoding='utf-8', newline='') as fh:
        return list(csv.DictReader(fh, delimiter='\t', quoting=csv.QUOTE_NONE))


def write_tsv(path: Path, header: list[str], rows: list[list]) -> None:
    clean = lambda v: str(v).replace('\t', ' ').replace('\r', ' ').replace('\n', ' ')
    with path.open('w', encoding='utf-8', newline='\n') as fh:
        fh.write('\t'.join(header) + '\n')
        for r in rows:
            fh.write('\t'.join(clean(v) for v in r) + '\n')


def go_of(lv: Level, comp_id: int) -> int | None:
    c = lv.comps.get(comp_id)
    return c.get('go') if c else None


def num(text) -> int | None:
    try:
        return int(text)
    except (TypeError, ValueError):
        return None


def hidden_rows(levels: dict[str, Level], audit: Path) -> list[list]:
    rows = []
    for r in read_tsv(audit / '09_unused_orphans_files' / 'dormant_gates.tsv'):
        lv = levels.get(r['file'])
        go = num(r['go_pathID'])
        if lv is None or go not in lv.path or r['status'] == 'BASELINE':
            continue
        sub = lv.subtree(go)
        rows.append([lv.file, lv.path[go], 'dormant' if r['status'] == 'DISABLED' else 'unclear', len(sub), r['reason'], '',
                     ','.join(lv.scripts(sub))])
    for r in read_tsv(audit / '09_unused_orphans_files' / 'opened_gates.tsv'):
        lv = levels.get(r['file'])
        go = num(r['go_pathID'])
        if lv is None or go not in lv.path:
            continue
        sub = lv.subtree(go)
        act = go_of(lv, int(r['activator_component_pathID'])) if r['activator_component_pathID'].isdigit() else None
        where = lv.path.get(act, r['activator_go_path']) if act is not None else r['activator_go_path']
        by = f"{r['activator_class']}.{r['field_or_fsm_action']}" + (' (own component)' if r['kind'] == 'self' else '')
        rows.append([lv.file, lv.path[go], 'switchable', len(sub), by, where, ','.join(lv.scripts(sub))])
    return rows


def wire_rows(levels: dict[str, Level], audit: Path) -> tuple[list[list], int]:
    rows, skipped = [], 0
    for r in read_tsv(audit / '01_signal_connections.tsv'):
        if r['status'] == 'OK' or r['scope'] != 'scene':
            continue
        if UNCERTAIN_WIRE in r['flags']:
            skipped += 1
            continue
        lv = levels.get(r['file'])
        cid = num(r['connector_pathID'])
        comp = lv.comps.get(cid) if lv else None
        if comp is None:
            continue
        f = comp['fields']
        snd, rcv = local_ref(f.get('sender')), local_ref(f.get('receiver'))
        rows.append([lv.file, lv.path.get(comp.get('go'), ''), r['signalOutName'], r['signalInName'], lv.path.get(snd, ''),
                     lv.path.get(rcv, ''), r['status'], r['flags']])
    return rows, skipped


def fsm_rows(levels: dict[str, Level], audit: Path) -> list[list]:
    rows = []
    for r in read_tsv(audit / '01_fsm_reachability.tsv'):
        if not r['unreachable_states'] and not r['events_without_source']:
            continue
        lv = levels.get(r['file'])
        cid = num(r['fsm_pathID'])
        go = go_of(lv, cid) if lv and cid is not None else None
        if go is None or go not in lv.path:
            continue
        rows.append([lv.file, lv.path[go], r['fsm_name'], r['unreachable_states'], r['events_without_source']])
    return rows


def savepoint_rows(levels: dict[str, Level], subscene_index: Path) -> tuple[list[list], list[list]]:
    order = []
    for line in subscene_index.read_text(encoding='utf-8').splitlines():
        m = re.match(r'(\d+) (#\S+)', line)
        if m:
            order.append((int(m.group(1)), m.group(2)))
    by_scene = {lv.scene: lv for lv in levels.values()}
    areas, points = [], []
    for idx, name in order:
        lv = by_scene.get(name)
        if lv is None:
            areas.append([idx, name, '', -1])
            continue
        node = next((c for c in lv.comps.values() if c.get('script') == 'SubsceneNode' and lv.path.get(c.get('go')) == name), None)
        refs = [local_ref(x) for x in (node['fields'].get('sortedSavepoints') or [])] if node else []
        refs = [sid for sid in refs if sid in lv.comps]
        areas.append([idx, name, lv.file, len(refs)])
        for i, sid in enumerate(refs):
            sp = lv.comps[sid]
            f = sp['fields']
            p = f.get('cachedSpawnPos') or {}
            info = CHARACTER_TYPES.get(f.get('characterType'), str(f.get('characterType')))
            if f.get('isCheckpointOnly'):
                info += ', checkpoint-only'
            points.append([name, i, lv.file, lv.path.get(sp.get('go'), ''), repr(p.get('x', 0.0)), repr(p.get('y', 0.0)),
                           repr(p.get('z', 0.0)), info])
    return areas, points


def level_order(file: str) -> tuple:
    if file == 'mainData':
        return (0, 0, file)
    m = LEVEL_RE.match(file)
    return (1, int(m.group(1)), file) if m else (2, 0, file)


def level_rows(levels: dict[str, Level], game: Path) -> list[list]:
    rows = []
    for lv in sorted(levels.values(), key=lambda v: level_order(v.file)):
        f = game / 'INSIDE_Data' / lv.file
        rows.append([lv.file, lv.scene, f.stat().st_size if f.exists() else -1, len(lv.gos)])
    return rows


def build(a: argparse.Namespace) -> int:
    if not a.index.is_dir():
        raise AtlasError(f'--index {a.index}: not a folder')
    levels = {}
    for p in sorted(a.index.glob('*.jsonl')):
        if '__' not in p.stem:
            print(f'skipped {p.name}: not <file>__<scene>.jsonl', file=sys.stderr)
            continue
        lv = load_level(p)
        levels[lv.file] = lv
    if not levels:
        raise AtlasError(f'--index {a.index}: no <file>__<scene>.jsonl level files')
    a.out.mkdir(parents=True, exist_ok=True)
    lv_rows = level_rows(levels, a.game)
    hid = hidden_rows(levels, a.audit)
    wires, skipped = wire_rows(levels, a.audit)
    fsms = fsm_rows(levels, a.audit)
    areas, points = savepoint_rows(levels, a.audit / '05_persistence_files' / 'subscene_index.txt')
    write_tsv(a.out / 'levels.tsv', ['file', 'scene', 'bytes', 'objects'], lv_rows)
    write_tsv(a.out / 'hidden.tsv', ['file', 'path', 'status', 'objects', 'reason', 'switch_from', 'scripts'], hid)
    write_tsv(a.out / 'wires.tsv', ['file', 'connector', 'out', 'in', 'sender', 'receiver', 'status', 'flags'], wires)
    write_tsv(a.out / 'fsm.tsv', ['file', 'path', 'fsm', 'unreachable_states', 'unsent_events'], fsms)
    write_tsv(a.out / 'subscenes.tsv', ['index', 'name', 'file', 'savepoints'], areas)
    write_tsv(a.out / 'savepoints.tsv', ['subscene', 'i', 'file', 'path', 'x', 'y', 'z', 'info'], points)
    st = Counter(r[2] for r in hid)
    print(f'levels {len(lv_rows)}  hidden {len(hid)} {dict(st)}  wires {len(wires)} (skipped uncertain {skipped})  fsm {len(fsms)}  '
          f'subscenes {len(areas)}  savepoints {len(points)}')
    return 0


def main() -> int:
    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(encoding='utf-8', errors='replace')
        except (AttributeError, ValueError, OSError):
            pass
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--index', type=Path, required=True, help='folder with the level index files <file>__<scene>.jsonl')
    ap.add_argument('--audit', type=Path, required=True, help='folder with the audit tables (tools/atlas/README.md)')
    ap.add_argument('--game', type=Path, required=True, help='the game folder (for the level file sizes)')
    ap.add_argument('--out', type=Path, required=True, help='folder for the six .tsv tables')
    a = ap.parse_args()
    try:
        return build(a)
    except AtlasError as e:
        print(f'make_atlas: {e}', file=sys.stderr)
        return 2
    except OSError as e:
        print(f'make_atlas: {type(e).__name__}: {e}', file=sys.stderr)
        return 2
    except KeyError as e:
        print(f'make_atlas: an audit table lacks the column {e}', file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
