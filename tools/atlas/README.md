# Level atlas generator

`make_atlas.py` writes `data/atlas/*.tsv` from a static index of INSIDE's level files. It reads only and needs Python
3.10+ (standard library only).

```
python -I make_atlas.py --index <levels dir> --audit <tables dir> --game <INSIDE folder> --out data/atlas
```

- `--index`: one JSON-lines file per level file (`level0__#waterLockDoors_Environment.jsonl`, ...): a record per
  GameObject (`pathID`, `name`, `components`) and per component (`class`, `script`, `go`, decoded `fields`), as read
  from the Steam build with the game's own type layouts
- `--audit`: tables from a static pass over that index and the game's code: `09_unused_orphans_files/dormant_gates.tsv`
  and `opened_gates.tsv` (who can switch a switched-off object on), `01_signal_connections.tsv` (every
  `SignalConnector`, both ends checked), `01_fsm_reachability.tsv` (states and events per PlayMaker FSM, with event
  senders searched in every level and in the game's code), `05_persistence_files/subscene_index.txt` (area order)
- `--game`: only the size of each level file is read (the build check)

Object keys are the level file plus the path below the scene root with ` #n` for the n-th same-named sibling, exactly
as `LevelCompare.Map` builds them, so InsideDev finds a row from a live object and a live object from a row.

| File | Columns |
|------|---------|
| `levels.tsv` | file, scene, bytes, objects |
| `hidden.tsv` | file, path, status (`switchable` / `dormant` / `unclear`), objects below, reason (or the switch: `Class.field`), switch_from (path of the object that switches it on), scripts |
| `wires.tsv` | file, connector, out, in, sender, receiver, status (`NO_SINK` / `NO_SOURCE` / `NO_SOURCE+NO_SINK` / `SOURCE_NEVER_FIRES`), flags |
| `fsm.tsv` | file, path, fsm, unreachable_states, unsent_events (`;`-separated) |
| `subscenes.tsv` | index, name, file, savepoints (in `SavepointManager` order) |
| `savepoints.tsv` | subscene, i (order of `SubsceneNode.sortedSavepoints`), file, path, x, y, z (`cachedSpawnPos`), info (`characterType[, checkpoint-only]`) |
