"""Build InsideDev's level atlas (data/atlas/*.tsv) from a static index of INSIDE's level files.

Object keys use the same " #n" numbering for same-named siblings as LevelCompare.Map, so a live object finds its row.
Reads only; run with: python -I make_atlas.py --index <levels dir> --audit <tables dir> --game <INSIDE dir> --out <dir>
"""
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


def load_level(path: Path) -> Level:
    file, scene = path.stem.split('__', 1)
    lv = Level(file, scene)
    for line in path.open(encoding='utf-8'):
        r = json.loads(line)
        if r['rec'] == 'gameobject':
            lv.gos[r['pathID']] = r
        elif r['rec'] == 'component':
            lv.comps[r['pathID']] = r
    tr_go = {pid: c['go'] for pid, c in lv.comps.items() if c['class'] in TRANSFORMS and 'go' in c}

    def walk(tid: int, parent_path: str | None) -> None:
        c = lv.comps[tid]
        go = c['go']
        if parent_path is None:
            lv.path[go] = lv.gos[go]['name']
        seen = Counter()
        for ref in c['fields'].get('m_Children') or []:
            ctid = ref['pathID']
            if ctid not in tr_go:
                continue
            cgo = tr_go[ctid]
            name = lv.gos[cgo]['name']
            seen[name] += 1
            lv.path[cgo] = lv.path[go] + '/' + name + (f' #{seen[name]}' if seen[name] > 1 else '')
            lv.children[go].append(cgo)
            walk(ctid, lv.path[cgo])

    for tid, go in tr_go.items():
        father = lv.comps[tid]['fields'].get('m_Father')
        if not father or father.get('pathID') not in tr_go:
            walk(tid, None)
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


def hidden_rows(levels: dict[str, Level], audit: Path) -> list[list]:
    rows = []
    for r in read_tsv(audit / '09_unused_orphans_files' / 'dormant_gates.tsv'):
        lv = levels.get(r['file'])
        go = int(r['go_pathID'])
        if lv is None or go not in lv.path or r['status'] == 'BASELINE':
            continue
        sub = lv.subtree(go)
        rows.append([lv.file, lv.path[go], 'dormant' if r['status'] == 'DISABLED' else 'unclear', len(sub), r['reason'], '',
                     ','.join(lv.scripts(sub))])
    for r in read_tsv(audit / '09_unused_orphans_files' / 'opened_gates.tsv'):
        lv = levels.get(r['file'])
        go = int(r['go_pathID'])
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
        cid = int(r['connector_pathID'])
        comp = lv.comps.get(cid) if lv else None
        if comp is None:
            continue
        f = comp['fields']
        snd = (f.get('sender') or {}).get('pathID')
        rcv = (f.get('receiver') or {}).get('pathID')
        rows.append([lv.file, lv.path.get(comp['go'], ''), r['signalOutName'], r['signalInName'], lv.path.get(snd, ''),
                     lv.path.get(rcv, ''), r['status'], r['flags']])
    return rows, skipped


def fsm_rows(levels: dict[str, Level], audit: Path) -> list[list]:
    rows = []
    for r in read_tsv(audit / '01_fsm_reachability.tsv'):
        if not r['unreachable_states'] and not r['events_without_source']:
            continue
        lv = levels.get(r['file'])
        go = go_of(lv, int(r['fsm_pathID'])) if lv else None
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
        node = next((c for c in lv.comps.values() if c.get('script') == 'SubsceneNode' and lv.path.get(c['go']) == name), None)
        refs = [x['pathID'] for x in (node['fields'].get('sortedSavepoints') or [])] if node else []
        areas.append([idx, name, lv.file, len(refs)])
        for i, sid in enumerate(refs):
            sp = lv.comps[sid]
            f = sp['fields']
            p = f.get('cachedSpawnPos') or {}
            info = CHARACTER_TYPES.get(f.get('characterType'), str(f.get('characterType')))
            if f.get('isCheckpointOnly'):
                info += ', checkpoint-only'
            points.append([name, i, lv.file, lv.path.get(sp['go'], ''), repr(p.get('x', 0.0)), repr(p.get('y', 0.0)),
                           repr(p.get('z', 0.0)), info])
    return areas, points


def level_rows(levels: dict[str, Level], game: Path) -> list[list]:
    rows = []
    for lv in sorted(levels.values(), key=lambda v: (v.file != 'mainData', int(v.file[5:]) if v.file != 'mainData' else 0)):
        f = game / 'INSIDE_Data' / lv.file
        rows.append([lv.file, lv.scene, f.stat().st_size if f.exists() else -1, len(lv.gos)])
    return rows


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('--index', type=Path, required=True)
    ap.add_argument('--audit', type=Path, required=True)
    ap.add_argument('--game', type=Path, required=True)
    ap.add_argument('--out', type=Path, required=True)
    a = ap.parse_args()
    levels = {}
    for p in sorted(a.index.glob('*.jsonl')):
        lv = load_level(p)
        levels[lv.file] = lv
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


if __name__ == '__main__':
    sys.exit(main())
