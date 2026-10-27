# MultiCam v2 spec checker (offline). Reads the schema from src/InsideDev/Runtime/MultiCamTypes.cs and ReplayTypes.cs, binds
# the JSON exactly like SpecBind, then runs the offline lint of MULTICAM_DESIGN.md 4.13 (plus the replay lint of
# REPLAY_DESIGN.md 3.2 for specs with a `replay` block), the segment table, a cost and disk estimate, compose checks and an
# optional object lookup in the level index. --schema-md prints the schema tables for docs/MULTICAM.md.
# Usage: python -I tools/mcam_spec_check.py <spec.json>... [--types FILE...] [--replay|--v2] [--schema-md] [--quiet] [--json] [--index]
from __future__ import annotations

import argparse
import csv
import json
import math
import os
import re
import shutil
import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

REPO = Path(__file__).resolve().parent.parent
DEFAULT_TYPES = [REPO / "src" / "InsideDev" / "Runtime" / "MultiCamTypes.cs", REPO / "src" / "InsideDev" / "Runtime" / "ReplayTypes.cs"]
DEFAULT_DESIGN = [REPO / "docs" / "MULTICAM_DESIGN.md", REPO / "docs" / "REPLAY_DESIGN.md"]
DEFAULT_INDEX = Path(os.environ["INSIDE_LEVEL_INDEX"]) if os.environ.get("INSIDE_LEVEL_INDEX") else None
SCHEMA_BEGIN = "<!-- schema:begin -->"
SCHEMA_END = "<!-- schema:end -->"

GAME_FOV = 50.987
LEGACY_FOV = 40.0
CAPTURE_W, CAPTURE_H = 2560, 1440
JPEG_MB_1440P = 1.0
NVENC_SESSIONS = 8

COMMON_CAM_KEYS = {"name", "mode", "label", "enabled", "fov", "near", "focus", "playerSide", "zMax", "render", "post", "hide", "clamp", "view",
                   "motion", "backfaces", "feedForward"}
MODE_KEYS = {
    "game": {"offset", "fovScale", "clean", "roll", "shake"},
    "static": {"pos", "yaw", "pitch", "roll", "look", "lookOffset", "lookSmooth", "target", "point", "screen", "shake"},
    "follow": {"target", "point", "offset", "local", "smooth", "lookSmooth", "lag", "lead", "planar", "start", "look",
               "lookOffset", "yaw", "pitch", "roll", "screen", "shake"},
    "keys": {"keys", "interp", "ease", "start", "look", "lookOffset", "lookSmooth", "target", "point", "screen", "roll", "shake"},
    "rail": {"path", "duration", "start", "ease", "interp", "look", "lookOffset", "lookSmooth", "yaw", "pitch", "target", "point",
             "screen", "roll", "shake"},
    "orbit": {"target", "point", "offset", "radius", "height", "azimuth", "duration", "start", "ease", "look", "lookOffset",
              "lookSmooth", "yaw", "pitch", "screen", "roll", "shake"},
    "gamerel": {"orbitYaw", "orbitPitch", "dist", "distScale", "shift", "keepSize", "clean", "roll", "shake"},
    "gamelike": {"target", "point", "box", "camOffset", "aimOffset", "extrapolate", "lag", "planar", "smooth", "lookOffset",
                 "roll", "shake"},
    "frame": {"target", "point", "screen", "size", "subjectHeight", "crop", "smooth", "lead", "yaw", "pitch", "roll", "shake"},
    "director": {"shots", "cutAngle", "cutDistance", "minHold"},
    "overview": {"target", "point", "pos", "yaw", "pitch", "roll", "offset", "smooth", "lead", "pans", "start", "shake"},
    "events": {"beats", "target", "point", "offset", "offsets", "lookOffset", "lookSmooth", "smooth", "hold", "move", "late", "ease",
               "start", "roll", "shake"},
}
EASES = {"keys": {"ends", "keys", "each", "linear"}, "rail": {"ends", "linear"}, "orbit": {"ends", "linear"},
         "events": {"ends", "smooth", "smoother", "linear", "sine"}}
SHOT_EASES = {"smooth", "smoother", "linear", "sine"}
SHOT_TRANSITIONS = {"orbit", "linear", "cut"}
CLOCKS = {"game", "output"}
LAGS = {"damp", "game", "none"}
INTERPS = {"catmull", "linear"}
TAA_MODES = {"reset", "keep", "toggle"}
FORMATS = {"jpg", "raw", "pipe"}
REAL_TIMES = {"game", "wall"}
KEEP_LOADED = {"auto", "none", "list"}
PHYSICS = {"game", "fine"}
GROUP_MODES = {"centroid", "weighted", "bounds", "nearest", "fastest"}
COND_PROPS = {"x", "y", "z", "vx", "vy", "vz", "speed", "dist"}
OPS = {"<", "<=", ">", ">=", "==", "!="}
BASES = {"boy", "huddle", "main", "game", "gameaim", "probe", "none", "self"}
BOY_POINTS = {"torso", "head", "neck", "root", "pos3", "bounds", "probe"}
HUDDLE_POINTS = {"visual", "centre", "top", "bottom", "bounds"}
OBJECT_POINTS = {"pivot", "bounds", "com", "rb", "collider", "top", "bottom"}
EDGE_POINTS = {"bounds", "top", "bottom"}
EDGE_POINT_WARN = ("{} follows the renderer bounds, which shift whenever the object turns or its silhouette changes; "
                   "track one point instead: com (objects with a Rigidbody, the default), pivot, bone:<name>, or torso/head for the boy")
EVENT_COMMON = {"name", "type", "once", "count", "after", "cooldown", "expect", "log"}
EVENT_KEYS = {
    "time": {"value"},
    "signal": {"obj", "signal"},
    "signalIn": {"obj", "signal"},
    "fsm": {"obj", "fsm", "state", "fsmEvent", "edge"},
    "sound": {"sound", "obj"},
    "probe": {"obj", "edge"},
    "impact": {"target", "min"},
    "box": {"target", "center", "size", "edge"},
    "cond": {"target", "to", "prop", "op", "value"},
    "field": {"obj", "comp", "field", "op", "value", "match"},
    "boystate": {"match", "edge"},
    "visual": {"match", "edge"},
    "death": set(),
    "loaded": set(),
    "camcut": {"min", "match"},
    "posted": {"match"},
}
EVENT_REQUIRED = {
    "signal": ["obj", "signal"], "signalIn": ["obj", "signal"], "fsm": ["obj"], "sound": ["sound"], "probe": ["obj"],
    "impact": ["target"], "box": ["target", "size"], "cond": ["target", "prop", "op"], "field": ["obj", "comp", "field"],
    "boystate": ["match"], "visual": ["match"], "time": ["value"],
}
RUNNER_PRIMITIVES = {"wait", "waitfor"}
CORE_COMMANDS = {"mcam", "cam", "hud", "clean", "gamma", "seed"}
OBJECT_COMMANDS = {"call", "signal", "signalin", "fsm", "setactive", "enable", "anim", "get", "set", "sigs"}
COMPOSE_KEYS = {"fps", "blur", "grade", "labels", "angles", "layouts", "edit", "encode", "review"}
TRANSITIONS = {"cut", "dissolve", "dip"}
PRESETS = {"16x9_3": (956, 3, 480), "16x9_2": (716, 2, 720)}
REPLAY_MS = "40:60"
SIDE_ANGLE = 65.0
TOP_DOWN_PITCH = 60.0
MAX_RECORD_W, MAX_RECORD_H = 1920, 1080
COSTLY_SAMPLES = 8
BLUR_CLAMP = 2.0
RTPC_ZERO = "audio force rtpc rtpc_master_volume 0"
RR, RP, RA = "replay.record.", "replay.play.", "replay.audio."


def is_num(v: Any) -> bool:
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def f32(x: float) -> float:
    try:
        return struct.unpack("<f", struct.pack("<f", x))[0]
    except OverflowError:
        return math.copysign(math.inf, x)


def fnum(x: Any, digits: int = 3) -> str:
    if x is None:
        return "-"
    if isinstance(x, float) and math.isnan(x):
        return "unset"
    if isinstance(x, float) and math.isinf(x):
        return "inf" if x > 0 else "-inf"
    s = f"{float(x):.{digits}f}"
    if "." in s:
        s = s.rstrip("0").rstrip(".")
    return "0" if s in ("-0", "", "-") else s


def cs_double(s: str) -> float | None:
    if re.fullmatch(r"\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*", s):
        return float(s)
    return None


def cs_number_text(v: float) -> str:
    d = float(v)
    if d == int(d) and abs(d) < 1e15:
        return str(int(d))
    return repr(d)


def levenshtein(a: str, b: str) -> int:
    prev = list(range(len(b) + 1))
    for i in range(1, len(a) + 1):
        cur = [i] + [0] * len(b)
        for j in range(1, len(b) + 1):
            cur[j] = min(cur[j - 1] + 1, prev[j] + 1, prev[j - 1] + (0 if a[i - 1] == b[j - 1] else 1))
        prev = cur
    return prev[len(b)]


def even(v: float) -> int:
    return int(2 * round(v / 2.0))


def smoothstep(x: float) -> float:
    x = min(1.0, max(0.0, x))
    return x * x * (3 - 2 * x)


def euler_forward(yaw: float, pitch: float) -> tuple[float, float, float]:
    y, p = math.radians(yaw), math.radians(pitch)
    return math.cos(p) * math.sin(y), -math.sin(p), math.cos(p) * math.cos(y)


def euler_basis(yaw: float, pitch: float) -> tuple[tuple[float, float, float], tuple[float, float, float], tuple[float, float, float]]:
    y, p = math.radians(yaw), math.radians(pitch)
    fwd = (math.sin(y) * math.cos(p), -math.sin(p), math.cos(y) * math.cos(p))
    up = (math.sin(y) * math.sin(p), math.cos(p), math.cos(y) * math.sin(p))
    right = (math.cos(y), 0.0, -math.sin(y))
    return right, up, fwd


def edge_xs(pos: tuple, yaw: float, pitch: float, fov: float, aspect: float, plane: bool, depth: float,
            corners: bool = True) -> tuple[float, float] | None:
    right, up, fwd = euler_basis(yaw, pitch)
    tv = math.tan(math.radians(min(max(fov, 1.0), 170.0)) / 2)
    th = tv * aspect
    lo, hi = math.inf, -math.inf
    for sx in (-1, 1):
        for sy in ((-1, 0, 1) if corners else (0,)):
            d = [fwd[k] + right[k] * sx * th + up[k] * sy * tv for k in range(3)]
            if plane:
                if d[2] <= 1e-5:
                    continue
                t = (depth - pos[2]) / d[2]
                if t <= 0:
                    continue
                x = pos[0] + t * d[0]
            else:
                x = pos[0] + depth * d[0]
            if sx < 0:
                lo = min(lo, x)
            else:
                hi = max(hi, x)
    return None if math.isinf(lo) or math.isinf(hi) else (lo, hi)


def check_text_ok(text: str) -> str | None:
    q, sep, win = text.partition(" @")
    if not q.strip():
        return "empty check"
    if sep and ".." not in win:
        return "the window is ' @<from>..<to>' (seconds or event[+-offset])"
    return None


def block_entry_ok(raw: str) -> bool:
    path, bar, props = raw.partition("|")
    if not bar or not path.strip().strip("/") or not props.strip():
        return False
    n = 0
    for part in props.split(","):
        part = part.strip()
        if not part:
            continue
        name, colon, typ = part.partition(":")
        if not name.strip() or (colon and typ.strip().lower() not in ("float", "vector")):
            return False
        n += 1
    return n > 0


MAX_HIDE_BOXES = 31


def hide_box_error(name: str) -> str | None:
    if " box=" not in name:
        return None
    path, _, boxes = name.rpartition(" box=")
    if not path.strip():
        return "a box hide needs an object path before ' box='"
    parts_list = boxes.split(";")
    if len(parts_list) > MAX_HIDE_BOXES:
        return f"at most {MAX_HIDE_BOXES} boxes in one name"
    for k, box in enumerate(parts_list):
        tag = f"box {k + 1} " if len(parts_list) > 1 else "box "
        parts = box.split(",")
        if len(parts) != 6:
            return tag + "needs 6 numbers x0,y0,z0,x1,y1,z1 (several boxes: separate them with ';')"
        try:
            v = [float(x) for x in parts]
        except ValueError:
            return f"{tag}'{box}' has a value that is not a number"
        if any(v[i] == v[i + 3] for i in range(3)):
            return tag + "has zero size on an axis"
    return None


def hide_query(name: str) -> str:
    path = name.rpartition(" box=")[0] if " box=" in name else name
    return path.strip().strip('"').split("::")[0].strip()


def hide_instance_id_error(name: str) -> str | None:
    q = hide_query(name)
    if not q.startswith("#") or re.fullmatch(r"\s*[+-]?\d+\s*", q[1:]):
        return None
    return (f"bad instance id '{q}': the runtime reads a name that starts with '#' as an instance id (MultiCamTargets.FindObject); "
            "for a level root named '#...' give the path below it or the bare object name")


def ease_peak(ease: str) -> float:
    return {"linear": 1.0, "smoother": 1.875, "sine": math.pi / 2}.get(ease, 1.5)


def move_seconds(dist: float, turn: float, ease: str, max_speed: float, max_turn: float, ease_s: float) -> float:
    k, t = ease_peak(ease), 0.0
    if max_speed > 0 and dist > 0:
        t = max(t, k * dist / max_speed)
    if max_turn > 0 and turn > 0:
        t = max(t, k * turn / max_turn)
    if ease != "linear" and ease_s > 0 and (dist > 1e-3 or turn > 0.05):
        t = max(t, 2 * ease_s)
    return t


def vec_angle(a: tuple, b: tuple) -> float:
    na, nb = math.sqrt(sum(x * x for x in a)), math.sqrt(sum(x * x for x in b))
    if na < 1e-9 or nb < 1e-9:
        return 0.0
    return math.degrees(math.acos(max(-1.0, min(1.0, sum(x * y for x, y in zip(a, b)) / (na * nb)))))


def quat_angle(a: tuple, b: tuple) -> float:
    d = abs(sum(x * y for x, y in zip(a, b)))
    na, nb = math.sqrt(sum(x * x for x in a)), math.sqrt(sum(x * x for x in b))
    if na < 1e-12 or nb < 1e-12:
        return 0.0
    return math.degrees(2 * math.acos(min(1.0, d / (na * nb))))


def schedule(at: list[float | None], hold: list[float], late: list[float]) -> list[float | None]:
    order = sorted(range(len(at)), key=lambda i: (at[i] is None, at[i] if at[i] is not None else 0.0, i))
    start: list[float | None] = [None] * len(at)
    free = -math.inf
    for i in order:
        if at[i] is None:
            continue
        s = max(at[i], free)
        if s - at[i] > max(0.0, late[i]) + 1e-9:
            continue
        start[i] = s
        free = s + max(0.0, hold[i])
    return start


def look_angles(v: tuple[float, float, float]) -> tuple[float, float, float] | None:
    n = math.sqrt(v[0] ** 2 + v[1] ** 2 + v[2] ** 2)
    if n < 1e-6:
        return None
    yaw = math.degrees(math.atan2(v[0], v[2]))
    pitch = math.degrees(math.asin(max(-1.0, min(1.0, -v[1] / n))))
    return yaw, pitch, v[2] / n


# ---------------------------------------------------------------------------------------------------- C# schema


@dataclass
class FieldDef:
    name: str
    ctype: str
    init: str | None
    default: tuple


@dataclass
class ClassDef:
    name: str
    bases: list[str]
    fields: list[FieldDef]
    consts: dict[str, Any] = field(default_factory=dict)
    arrays: dict[str, list[str]] = field(default_factory=dict)


def mask_code(text: str, strings: bool = True) -> str:
    out = list(text)
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            j = text.find("\n", i)
            j = n if j < 0 else j
            for k in range(i, j):
                out[k] = " "
            i = j
            continue
        if text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            for k in range(i, j):
                if out[k] != "\n":
                    out[k] = " "
            i = j
            continue
        if c == '"' or (c == "@" and text.startswith('@"', i)):
            verbatim = c == "@"
            j = i + (2 if verbatim else 1)
            while j < n:
                if verbatim and text.startswith('""', j):
                    j += 2
                    continue
                if not verbatim and text[j] == "\\":
                    j += 2
                    continue
                if text[j] == '"' or (not verbatim and text[j] == "\n"):
                    break
                j += 1
            for k in range(i + (2 if verbatim else 1), min(j, n)):
                out[k] = "_" if strings else out[k]
            i = j + 1
            continue
        if c == "'":
            j = i + 1
            while j < n and text[j] != "'" and text[j] != "\n":
                j += 2 if text[j] == "\\" else 1
            for k in range(i + 1, min(j, n)):
                out[k] = "_" if strings else out[k]
            i = j + 1
            continue
        i += 1
    return "".join(out)


def match_brace(masked: str, open_index: int) -> int:
    depth = 0
    for i in range(open_index, len(masked)):
        if masked[i] == "{":
            depth += 1
        elif masked[i] == "}":
            depth -= 1
            if depth == 0:
                return i
    return len(masked)


def top_statements(masked: str, text: str, start: int, end: int) -> list[tuple[str, str]]:
    stmts: list[tuple[str, str]] = []
    depth, cur, block = 0, start, start
    for i in range(start, end):
        c = masked[i]
        if c == "{":
            if depth == 0:
                block = i
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                stmts.append((text[cur:block] + "{BLOCK}", text[block + 1:i]))
                cur = i + 1
        elif c == ";" and depth == 0:
            stmts.append((text[cur:i], ""))
            cur = i + 1
    return stmts


FIELD_RE = re.compile(
    r"^(?P<attrs>(?:\[[^\]]*\]\s*)*)public\s+(?P<mods>(?:(?:static|readonly|const|new|volatile)\s+)*)"
    r"(?P<type>[A-Za-z_][\w.]*(?:<[^=;()]*?>)?(?:\[\])?)\s+(?P<name>[A-Za-z_]\w*)\s*(?:=\s*(?P<init>.+?))?\s*$", re.S)
NUM_LIT = re.compile(r"[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?[fFdDmM]?")


def parse_literal(s: str, consts: dict[str, Any]) -> Any:
    s = s.strip()
    if NUM_LIT.fullmatch(s):
        return float(s.rstrip("fFdDmM"))
    if s in ("float.NaN", "double.NaN"):
        return math.nan
    if s.startswith('"'):
        try:
            return json.loads(s)
        except ValueError:
            return s.strip('"')
    if s in ("true", "false"):
        return s == "true"
    if s in consts:
        return consts[s]
    return None


def array_values(ctype: str, block: str) -> list:
    if ctype in ("int[]", "float[]", "double[]"):
        vals = [float(m.group(0).rstrip("fFdDmM")) for m in NUM_LIT.finditer(block)]
        return [int(v) for v in vals] if ctype == "int[]" else vals
    return re.findall(r'"([^"]*)"', block)


def parse_default(init: str | None, ctype: str, consts: dict[str, Any], arrays: dict[str, list] | None = None) -> tuple:
    if init is None:
        if ctype in ("int", "float", "double"):
            return ("value", 0.0 if ctype != "int" else 0)
        if ctype == "bool":
            return ("value", False)
        if ctype == "Vector3":
            return ("value", (0.0, 0.0, 0.0))
        if ctype == "Vector2":
            return ("value", (0.0, 0.0))
        return ("value", None)
    s = init.strip()
    m = re.fullmatch(r"new\s+([\w.]+)(?:<[^()]*>)?\s*\((.*)\)", s, re.S)
    if m:
        cname, args = m.group(1), [a for a in (x.strip() for x in m.group(2).split(",")) if a]
        if cname == "List":
            if len(args) == 1 and arrays and args[0] in arrays:
                return ("list", tuple(arrays[args[0]]))
            return ("list",)
        if cname in ("Vector3", "Vector2"):
            vals = [parse_literal(a, consts) for a in args]
            if all(is_num(v) for v in vals):
                return ("value", tuple(f32(v) for v in vals))
            return ("expr", s)
        if cname == "TimeRef":
            off = parse_literal(args[0], consts) if args else 0.0
            return ("time", float(off) if is_num(off) else 0.0)
        return ("new", cname)
    v = parse_literal(s, consts)
    if v is None:
        return ("expr", s)
    if ctype == "float" and is_num(v):
        return ("value", f32(v))
    if ctype == "int" and is_num(v):
        return ("value", int(v))
    return ("value", v)


class Schema:
    def __init__(self, paths: Path | list[Path]) -> None:
        self.paths = [paths] if isinstance(paths, Path) else list(paths)
        self.path = self.paths[0]
        self.classes: dict[str, ClassDef] = {}
        consts: dict[str, Any] = {}
        arrays: dict[str, list] = {}
        pending: list[tuple[str, list[str], list[tuple[str, str]]]] = []
        for path in self.paths:
            text = path.read_text(encoding="utf-8-sig")
            masked = mask_code(text)
            clean = mask_code(text, strings=False)
            for m in re.finditer(r"\b(?:class|struct)\s+(\w+)\s*(?::\s*([^{]+?))?\s*\{", masked):
                open_i = m.end() - 1
                close_i = match_brace(masked, open_i)
                bases = [b.strip() for b in (m.group(2) or "").split(",") if b.strip()]
                pending.append((m.group(1), bases, top_statements(masked, clean, open_i + 1, close_i)))
        for name, bases, stmts in pending:
            cd = ClassDef(name, bases, [])
            for stmt, block in stmts:
                st = stmt.strip()
                is_block = st.endswith("{BLOCK}")
                fm = FIELD_RE.match(re.sub(r"=\s*\{BLOCK\}$", "", st).strip() if is_block else st)
                if not fm:
                    continue
                mods = fm.group("mods")
                if is_block:
                    if "static" in mods and fm.group("type").endswith("[]"):
                        vals = array_values(re.sub(r"\s+", "", fm.group("type")), block)
                        cd.arrays[fm.group("name")] = vals
                        arrays[f"{name}.{fm.group('name')}"] = vals
                    continue
                if "const" in mods or "static" in mods or "readonly" in mods:
                    if "const" in mods and fm.group("init"):
                        v = parse_literal(fm.group("init"), {})
                        cd.consts[fm.group("name")] = v
                        consts[f"{name}.{fm.group('name')}"] = v
                    continue
                if "SpecIgnore" in fm.group("attrs"):
                    continue
                cd.fields.append(FieldDef(fm.group("name"), re.sub(r"\s+", "", fm.group("type")), fm.group("init"), ()))
            self.classes[name] = cd
        for cd in self.classes.values():
            for fd in cd.fields:
                fd.default = parse_default(fd.init, fd.ctype, consts, arrays)
        if "MultiCamSpec" not in self.classes or "CamSpec" not in self.classes:
            raise ValueError(f"{', '.join(map(str, self.paths))}: MultiCamSpec / CamSpec not found")
        const = self.classes.get("MultiCamConst")
        self.modes = (const.arrays.get("Modes") if const else None) or list(MODE_KEYS)
        self.event_types = (const.arrays.get("EventTypes") if const else None) or list(EVENT_KEYS)
        self.min_rate = int((const.consts.get("MinRate") if const else None) or 10)
        self.max_rate = int((const.consts.get("MaxRate") if const else None) or 960)
        self.player_z = float((const.consts.get("PlayerSideZ") if const else None) or -2.0)
        self.game_fov = float((const.consts.get("GameFov") if const else None) or GAME_FOV)
        self._fields: dict[str, list[FieldDef]] = {}

    def inherits(self, cname: str, base: str) -> bool:
        cd = self.classes.get(cname)
        if cd is None:
            return False
        return base in cd.bases or any(self.inherits(b, base) for b in cd.bases if b in self.classes)

    def is_node(self, cname: str) -> bool:
        return self.inherits(cname, "SpecNode")

    def is_json_value(self, cname: str) -> bool:
        return self.inherits(cname, "IJsonValue")

    def fields(self, cname: str) -> list[FieldDef]:
        if cname in self._fields:
            return self._fields[cname]
        cd = self.classes.get(cname)
        out: list[FieldDef] = []
        if cd is not None:
            out.extend(cd.fields)
            for b in cd.bases:
                if b in self.classes:
                    out.extend(self.fields(b))
        self._fields[cname] = out
        return out

    def field(self, cname: str, key: str) -> FieldDef | None:
        for f in self.fields(cname):
            if f.name == key:
                return f
        return None

    def nearest(self, cname: str, key: str) -> str | None:
        best, bd = None, 3
        for f in self.fields(cname):
            if f.name.lower() == key.lower():
                return f.name
            d = levenshtein(f.name.lower(), key.lower())
            if d < bd:
                bd, best = d, f.name
        return best

    def fresh(self, tag: tuple) -> Any:
        kind = tag[0] if tag else "value"
        if kind == "list":
            return list(tag[1]) if len(tag) > 1 else []
        if kind == "new":
            return self.new(tag[1]) if tag[1] in self.classes else None
        if kind == "time":
            return TimeVal(None, tag[1])
        if kind == "value":
            return tag[1]
        return None

    def new(self, cname: str) -> Node:
        n = Node(cname)
        for f in self.fields(cname):
            n.v[f.name] = self.fresh(f.default)
        return n

    def nested_type(self, ctype: str) -> tuple[str, bool]:
        m = re.fullmatch(r"List<(.+)>", ctype)
        return (m.group(1), True) if m else (ctype, False)


# ---------------------------------------------------------------------------------------------------- values


@dataclass
class TimeVal:
    ev: str | None
    offset: float

    @property
    def absolute(self) -> bool:
        return not self.ev

    def text(self) -> str:
        if self.absolute:
            return fnum(self.offset, 6)
        if self.offset == 0:
            return f"@{self.ev}"
        return f"@{self.ev}{'+' if self.offset > 0 else ''}{fnum(self.offset, 6)}"


class Node:
    __slots__ = ("cls", "v", "given", "where")

    def __init__(self, cls: str) -> None:
        self.cls = cls
        self.v: dict[str, Any] = {}
        self.given: list[str] = []
        self.where = ""

    def __getattr__(self, k: str) -> Any:
        if k in Node.__slots__:
            raise AttributeError(k)
        try:
            return self.v[k]
        except KeyError:
            raise AttributeError(k) from None

    def has(self, k: str) -> bool:
        return k in self.given


class JObj(dict):
    def __init__(self, pairs: list[tuple[str, Any]]) -> None:
        super().__init__()
        self.dups: list[str] = []
        for k, v in pairs:
            if k in self:
                self.dups.append(k)
            self[k] = v


def parse_time(s: str) -> tuple[bool, str | None, float]:
    s = s.strip()
    if not s:
        return False, None, 0.0
    if s[0] != "@":
        x = cs_double(s)
        return (x is not None), None, (x or 0.0)
    i = 1
    while i < len(s) and (s[i].isalnum() or s[i] in "_."):
        i += 1
    if i == 1:
        return False, None, 0.0
    name, rest = s[1:i], s[i:].replace(" ", "")
    if not rest:
        return True, name, 0.0
    if rest[0] not in "+-":
        return False, None, 0.0
    x = cs_double(rest)
    if x is None:
        return False, None, 0.0
    return True, name, x


def vec_of(v: Any, n: int) -> tuple | None:
    if not isinstance(v, list) or len(v) != n or not all(is_num(x) for x in v):
        return None
    return tuple(f32(float(x)) for x in v)


# ---------------------------------------------------------------------------------------------------- report


@dataclass
class Msg:
    level: str
    where: str
    text: str
    stage: str

    def line(self) -> str:
        return f"{self.where}: {self.text}" if self.where else self.text


class Report:
    def __init__(self) -> None:
        self.msgs: list[Msg] = []
        self.stage = "bind"

    def add(self, level: str, where: str, text: str, stage: str | None = None) -> None:
        self.msgs.append(Msg(level, where or "", text, stage or self.stage))

    def error(self, where: str, text: str) -> None:
        self.add("error", where, text)

    def warn(self, where: str, text: str) -> None:
        self.add("warn", where, text)

    def note(self, where: str, text: str) -> None:
        self.add("note", where, text)

    def of(self, level: str) -> list[Msg]:
        return [m for m in self.msgs if m.level == level]

    @property
    def ok(self) -> bool:
        return not self.of("error")


# ---------------------------------------------------------------------------------------------------- binder (SpecBind)


class Binder:
    def __init__(self, schema: Schema, report: Report) -> None:
        self.schema = schema
        self.report = report
        self.readers: dict[str, Callable[[Any, str], tuple[bool, Any]]] = {
            "TimeRef": self.read_time, "TargetRef": self.read_target, "CueSpec": self.read_cue, "RateSpec": self.read_rate}

    def fill(self, node: Node, obj: dict, where: str) -> None:
        node.where = where or ""
        for k in obj:
            if not k or k[0] == "_" or k == "comment":
                continue
            at = f"{where}.{k}" if where else k
            fd = self.schema.field(node.cls, k)
            if fd is None:
                near = self.schema.nearest(node.cls, k)
                self.report.warn(at, "unknown key, ignored" + (f" (did you mean '{near}'?)" if near else ""))
                continue
            ok, v = self.convert(obj[k], fd.ctype, at)
            if not ok:
                continue
            node.v[k] = v
            if k not in node.given:
                node.given.append(k)
        for k in getattr(obj, "dups", []):
            self.report.add("warn", f"{where}.{k}" if where else k, "duplicate key, the last value is used", "json")

    def convert(self, v: Any, ctype: str, where: str) -> tuple[bool, Any]:
        if v is None:
            return False, None
        if ctype == "object":
            return True, v
        if ctype == "Json.Obj":
            if isinstance(v, dict):
                return True, v
            self.report.error(where, "object expected")
            return False, None
        if ctype == "string":
            if isinstance(v, str):
                return True, v
            if isinstance(v, bool):
                return True, "True" if v else "False"
            if is_num(v):
                return True, cs_number_text(v)
            self.report.error(where, "string expected")
            return False, None
        if ctype == "bool":
            if isinstance(v, bool):
                return True, v
            self.report.error(where, "true or false expected")
            return False, None
        if ctype in ("double", "float", "int"):
            if not is_num(v):
                self.report.error(where, "number expected")
                return False, None
            d = float(v)
            if ctype == "double":
                return True, d
            if ctype == "float":
                return True, f32(d)
            if abs(d - round(d)) > 1e-9 or abs(d) > 2147483647:
                self.report.error(where, "integer expected")
                return False, None
            return True, int(round(d))
        if ctype == "Vector3":
            p = vec_of(v, 3)
            if p is not None:
                return True, p
            self.report.error(where, "[x, y, z] expected")
            return False, None
        if ctype == "Vector2":
            p = vec_of(v, 2)
            if p is not None:
                return True, p
            self.report.error(where, "[a, b] expected")
            return False, None
        if ctype == "float[]":
            if not isinstance(v, list):
                self.report.error(where, "list of numbers expected")
                return False, None
            out = []
            for i, x in enumerate(v):
                if not is_num(x):
                    self.report.error(f"{where}[{i}]", "number expected")
                    return False, None
                out.append(f32(float(x)))
            return True, out
        et, is_list = self.schema.nested_type(ctype)
        if is_list:
            return self.convert_list(v, et, where)
        if self.schema.is_node(ctype) or self.schema.is_json_value(ctype) or ctype in self.readers:
            return self.convert_node(v, ctype, where)
        self.report.error(where, f"unsupported field type {ctype}")
        return False, None

    def convert_node(self, v: Any, cname: str, where: str) -> tuple[bool, Any]:
        if cname in self.readers:
            return self.readers[cname](v, where)
        if not isinstance(v, dict):
            self.report.error(where, "object expected")
            return False, None
        node = self.schema.new(cname)
        self.fill(node, v, where)
        return True, node

    def convert_list(self, v: Any, et: str, where: str) -> tuple[bool, Any]:
        out: list[Any] = []
        named = self.schema.is_node(et) and not self.schema.is_json_value(et) and self.schema.field(et, "name") is not None
        if not isinstance(v, list) and isinstance(v, dict) and named:
            for k in v:
                if not k or k[0] == "_" or k == "comment":
                    continue
                ok, e = self.convert_node(v[k], et, f"{where}.{k}")
                if not ok:
                    continue
                if not e.has("name"):
                    e.v["name"] = k
                    e.given.append("name")
                out.append(e)
            return True, out
        if not isinstance(v, list):
            if et in ("string", "TargetRef"):
                ok, one = self.convert(v, et, where)
                if not ok:
                    return False, None
                return True, [one]
            self.report.error(where, "list expected")
            return False, None
        for i, x in enumerate(v):
            ok, e = self.convert(x, et, f"{where}[{i}]")
            if ok:
                out.append(e)
        return True, out

    def read_time(self, v: Any, where: str) -> tuple[bool, Any]:
        if is_num(v):
            return True, TimeVal(None, float(v))
        if isinstance(v, str):
            ok, ev, off = parse_time(v)
            if ok:
                return True, TimeVal(ev, off)
        self.report.error(where, 'time expected: game seconds or "@event", "@event+1.5", "@event-0.5"')
        return False, None

    def read_target(self, v: Any, where: str) -> tuple[bool, Any]:
        node = self.schema.new("TargetRef")
        node.where = where
        if isinstance(v, str):
            node.v["of"] = v.strip()
            node.given.append("of")
            if node.v["of"]:
                return True, node
            self.report.error(where, "empty target")
            return False, None
        if isinstance(v, list):
            p = vec_of(v, 3)
            if p is None:
                self.report.error(where, "target point [x, y, z] expected")
                return False, None
            node.v["pos"] = p
            node.given.append("pos")
            return True, node
        if isinstance(v, dict):
            self.fill(node, v, where)
            if node.v.get("of") or node.has("pos") or node.v.get("group") is not None:
                return True, node
            self.report.error(where, "target object needs 'of', 'pos' or 'group'")
            return False, None
        self.report.error(where, 'target expected: "boy" | "<path>" | [x, y, z] | {of, point, offset} | {group}')
        return False, None

    def read_cue(self, v: Any, where: str) -> tuple[bool, Any]:
        node = self.schema.new("CueSpec")
        node.where = where
        if isinstance(v, list):
            if len(v) < 2:
                self.report.error(where, 'cue [time, "command", ...] expected')
                return False, None
            ok, t = self.read_time(v[0], f"{where}[0]")
            if not ok:
                return False, None
            node.v["at"] = t
            node.given.append("at")
            for i in range(1, len(v)):
                c = v[i]
                if not isinstance(c, str):
                    self.report.error(f"{where}[{i}]", "command string expected")
                    return False, None
                if i == 1:
                    node.v["cmd"] = c
                    node.given.append("cmd")
                else:
                    node.v["cmds"].append(c)
            if node.v["cmds"]:
                node.given.append("cmds")
            return True, node
        if isinstance(v, dict):
            self.fill(node, v, where)
            if node.v.get("cmd") or node.v.get("cmds"):
                return True, node
            self.report.error(where, "cue needs 'cmd' or 'cmds'")
            return False, None
        self.report.error(where, 'cue expected: [time, "command"] or {at, cmd | cmds}')
        return False, None

    def read_rate(self, v: Any, where: str) -> tuple[bool, Any]:
        node = self.schema.new("RateSpec")
        node.where = where
        if isinstance(v, list):
            if len(v) < 2 or not is_num(v[1]):
                self.report.error(where, "rate segment [fromGameSecond, stepsPerGameSecond, stepsPerOutputFrame] expected")
                return False, None
            ok, t = self.read_time(v[0], f"{where}[0]")
            if not ok:
                return False, None
            node.v["from"] = t
            node.given.append("from")
            node.v["rate"] = f32(float(v[1]))
            node.given.append("rate")
            if len(v) > 2 and v[2] is not None:
                ok, g = self.convert(v[2], "int", f"{where}[2]")
                if not ok:
                    return False, None
                node.v["group"] = g
                node.given.append("group")
            return True, node
        if isinstance(v, dict):
            self.fill(node, v, where)
            return True, node
        self.report.error(where, "rate segment expected: [from, rate, group] or {from, rate, group}")
        return False, None


def overlay(schema: Schema, under: Node | None, over: Node | None, cname: str) -> Node | None:
    if under is None and over is None:
        return None
    r = schema.new(cname)
    if under is not None:
        for f in schema.fields(cname):
            r.v[f.name] = under.v.get(f.name)
        r.given.extend(under.given)
        r.where = under.where
    if over is not None:
        for k in over.given:
            if schema.field(cname, k) is None:
                continue
            r.v[k] = over.v.get(k)
            if k not in r.given:
                r.given.append(k)
        r.where = over.where
    return r


def walk(schema: Schema, node: Any, where: str, visit: Callable[[Any, str], None]) -> None:
    if node is None:
        return
    visit(node, where)
    if not isinstance(node, Node):
        return
    for f in schema.fields(node.cls):
        v = node.v.get(f.name)
        if v is None:
            continue
        at = f"{where}.{f.name}" if where else f.name
        if isinstance(v, (Node, TimeVal)):
            walk(schema, v, at, visit)
            continue
        if isinstance(v, list):
            for i, e in enumerate(v):
                if isinstance(e, (Node, TimeVal)):
                    walk(schema, e, f"{at}[{i}]", visit)


# ---------------------------------------------------------------------------------------------------- bridge commands


def function_cases(path: Path, signature: str) -> set[str]:
    if not path.exists():
        return set()
    text = path.read_text(encoding="utf-8-sig", errors="replace")
    masked = mask_code(text)
    i = masked.find(signature) if signature in masked else text.find(signature)
    if i < 0:
        return set()
    open_i = masked.find("{", i)
    close_i = match_brace(masked, open_i)
    body = text[open_i:close_i]
    indent = None
    names: set[str] = set()
    for m in re.finditer(r'^( *)case "([^"]+)":', body, re.M):
        if indent is None:
            indent = len(m.group(1))
        if len(m.group(1)) == indent:
            names.add(m.group(2).lower())
    return names


def load_commands(types_path: Path) -> set[str]:
    src = types_path.resolve().parent.parent
    names = function_cases(src / "Remote.cs", "static string Run(string line, DevCore core)")
    names |= function_cases(src / "DevRoot.cs", "public static void Run(string line, DevCore root, bool extensions)")
    return names


def split_command(line: str) -> list[str]:
    out, cur, q = [], [], False
    for ch in line:
        if ch == '"':
            q = not q
            continue
        if not q and ch in " \t":
            if cur:
                out.append("".join(cur))
                cur = []
            continue
        cur.append(ch)
    if cur:
        out.append("".join(cur))
    return out


# ---------------------------------------------------------------------------------------------------- check


@dataclass
class CamInfo:
    index: int
    raw: Node
    eff: Node
    render: Node
    post: Node
    shake: Node | None
    fov: float

    @property
    def name(self) -> str:
        return self.eff.name or ""

    @property
    def mode(self) -> str:
        return self.eff.mode or ""

    @property
    def where(self) -> str:
        return self.raw.where


@dataclass
class Segment:
    index: int
    label: str
    start: TimeVal
    start_t: float | None
    speed: float
    samples: int
    shutter: float
    rate: float
    legacy: bool
    where: str
    R: float | None = None
    N: int | None = None
    M: int | None = None
    s_eff: float | None = None


@dataclass
class Piece:
    seg: Segment
    a: float
    b: float

    @property
    def d(self) -> float:
        return max(0.0, self.b - self.a)


@dataclass
class Result:
    path: Path
    report: Report
    spec: Node | None = None
    tables: dict[str, Any] = field(default_factory=dict)
    bad_input: bool = False
    replay: bool = False


class Check:
    def __init__(self, path: Path, schema: Schema, opts: argparse.Namespace, commands: set[str]) -> None:
        self.path = path
        self.schema = schema
        self.opts = opts
        self.commands = commands
        self.report = Report()
        self.result = Result(path, self.report)
        self.spec: Node | None = None
        self.cams: list[CamInfo] = []
        self.legacy = False
        self.root_render: Node | None = None
        self.output: Node | None = None
        self.fps = 30.0
        self.segments: list[Segment] = []
        self.pieces: list[Piece] = []
        self.predicted: dict[str, tuple[float, str]] = {}
        self.refs: dict[str, list[str]] = {}
        self.end_t = 0.0
        self.res = (CAPTURE_W, CAPTURE_H)
        self.root: dict = {}
        self.replay = False

    def run(self) -> Result:
        try:
            text = self.path.read_text(encoding="utf-8-sig")
        except OSError as e:
            self.report.error(str(self.path), f"cannot read: {e}")
            self.result.bad_input = True
            return self.result
        try:
            root = json.loads(text, object_pairs_hook=JObj, strict=False, parse_constant=self._bad_constant)
        except ValueError as e:
            self.report.error(str(self.path), f"JSON: {e}")
            self.result.bad_input = True
            return self.result
        if not isinstance(root, dict):
            self.report.error(str(self.path), "spec must be a JSON object")
            self.result.bad_input = True
            return self.result
        spec = self.schema.new("MultiCamSpec")
        Binder(self.schema, self.report).fill(spec, root, "")
        self.spec = self.result.spec = spec
        self.root = root
        lint = getattr(self.opts, "lint", "auto")
        self.replay = self.result.replay = lint == "replay" or (lint == "auto" and spec.v.get("replay") is not None)
        if self.replay and "PlaySpec" not in self.schema.classes:
            self.report.add("error", "replay", "the replay lint needs ReplayTypes.cs in --types", "lint")
            self.replay = self.result.replay = False
        if self.opts.bind_only:
            return self.result
        self.report.stage = "lint"
        self.legacy_mapping()
        self.build_cameras()
        self.collect_refs()
        self.load_predictions()
        self.lint_root()
        self.lint_events()
        self.lint_refs()
        self.lint_timing()
        self.build_timeline()
        self.lint_commands()
        self.lint_cameras()
        self.lint_motion_poses()
        self.lint_targets()
        self.lint_budgets()
        if self.replay:
            self.lint_replay()
        self.report.stage = "compose"
        self.lint_compose()
        self.report.stage = "estimate"
        if self.replay:
            self.estimate_replay()
        else:
            self.estimate()
        return self.result

    @staticmethod
    def _bad_constant(name: str) -> Any:
        raise ValueError(f"{name} is not valid JSON (the game parser rejects it)")

    def w(self, base: str, key: str) -> str:
        return f"{base}.{key}" if base else key

    # -------------------------------------------------------------------------------- legacy mapping (design 4.12)

    def legacy_mapping(self) -> None:
        s = self.spec
        self.legacy = not s.has("version") or s.version < 2
        if s.has("version") and s.version > 2:
            self.report.warn("version", f"version {s.version} is newer than this checker (2)")
        v2_keys = [k for k in ("timing", "events", "render", "output", "session", "post", "stream", "defaults", "until", "preroll",
                               "seed", "rehearsal", "interpolate", "tracks") if s.has(k)]
        if self.legacy and v2_keys:
            self.report.warn("version", f"no \"version\": 2 but v2 keys ({', '.join(v2_keys[:4])}{', ...' if len(v2_keys) > 4 else ''}): "
                                        "the v1 defaults apply (fov 40 for cameras without fov, settle 3, jpeg 92)")
        settle = s.settle if s.has("settle") else (3 if self.legacy else -1)
        jpeg = s.jpeg if s.has("jpeg") else (92 if self.legacy and not s.output.has("jpeg") else -1)
        self.root_render = s.render
        if settle >= 0 and not s.has("render"):
            r = self.schema.new("RenderSpec")
            r.v.update(warmup=max(1, settle), samples=1, taa="keep")
            r.given.extend(["warmup", "samples", "taa"])
            r.where = "settle"
            self.root_render = r
            if settle == 0:
                self.report.warn("settle", "settle 0 maps to render.warmup 1 (warmup must be >= 1)")
        elif settle >= 0 and s.has("settle"):
            self.report.warn("settle", "ignored because 'render' is given")
        self.output = overlay(self.schema, s.output, None, "OutputSpec")
        if jpeg >= 0:
            if s.has("jpeg") and s.output.has("jpeg") and s.output.jpeg != jpeg:
                self.report.warn("jpeg", f"root jpeg {jpeg} overrides output.jpeg {s.output.jpeg}")
            self.output.v["jpeg"] = jpeg
        self.fps = float(s.timing.fps)
        if s.rates and not s.timing.segments:
            if not s.timing.has("fps"):
                r0 = s.rates[0]
                self.fps = float(r0.rate) / max(1, r0.group)
                self.report.note("rates", f"timing.fps defaults to rates[0] {fnum(r0.rate)}/{r0.group} = {fnum(self.fps)}")
        elif s.rates and s.timing.segments:
            self.report.warn("rates", "ignored because timing.segments is given")

    def build_cameras(self) -> None:
        s = self.spec
        dflt = s.defaults
        if dflt is not None:
            if dflt.has("name"):
                self.report.warn(self.w(dflt.where or "defaults", "name"), "a default name would be copied to every camera without its own name")
            if dflt.has("keys") or dflt.has("shots") or dflt.has("path"):
                self.report.warn(dflt.where or "defaults", "keys / shots / path in defaults are copied to every camera")
        for i, raw in enumerate(s.cameras):
            eff = overlay(self.schema, dflt, raw, "CamSpec")
            render = overlay(self.schema, overlay(self.schema, self.root_render, dflt.render if dflt else None, "RenderSpec"),
                             raw.render, "RenderSpec")
            post = overlay(self.schema, overlay(self.schema, s.post, dflt.post if dflt else None, "PostSpec"), raw.post, "PostSpec")
            shake = overlay(self.schema, dflt.shake if dflt else None, raw.shake, "ShakeSpec")
            eff.v["motion"] = overlay(self.schema, dflt.motion if dflt else None, raw.motion, "MotionSpec")
            fov = eff.fov
            if self.legacy and not eff.has("fov") and eff.mode not in ("game", "director"):
                fov = LEGACY_FOV
            if fov <= 0:
                fov = self.schema.game_fov
            if eff.has("point") and eff.target is not None and not eff.target.v.get("point"):
                t = overlay(self.schema, eff.target, None, "TargetRef")
                t.v["point"] = eff.point
                t.given.append("point")
                eff.v["target"] = t
            self.cams.append(CamInfo(i, raw, eff, render, post, shake, fov))

    def cam(self, name: str) -> CamInfo | None:
        for c in self.cams:
            if c.name == name:
                return c
        return None

    # -------------------------------------------------------------------------------- references and predictions

    def collect_refs(self) -> None:
        def visit(node: Any, where: str) -> None:
            if isinstance(node, TimeVal) and node.ev:
                self.refs.setdefault(node.ev, []).append(where)
        walk(self.schema, self.spec, "", visit)
        for i, cmd in enumerate(self.spec.setup):
            a = split_command(cmd)
            if len(a) >= 3 and a[0].lower() == "waitfor" and a[1].lower() == "event":
                self.refs.setdefault(a[2], []).append(f"setup[{i}]")
        for ref, where in self.compose_time_refs():
            self.refs.setdefault(ref, []).append(where)

    def compose_time_refs(self) -> list[tuple[str, str]]:
        out: list[tuple[str, str]] = []
        comp = self.spec.compose
        if not isinstance(comp, dict):
            return out
        edits = comp.get("edit")
        for ei, edit in enumerate(edits if isinstance(edits, list) else [edits]):
            if not isinstance(edit, dict) or not isinstance(edit.get("shots"), list):
                continue
            for i, shot in enumerate(edit["shots"]):
                if not isinstance(shot, dict):
                    continue
                for key in ("from", "to"):
                    v = shot.get(key)
                    if isinstance(v, str):
                        ok, ev, _ = parse_time(v)
                        if ok and ev:
                            out.append((ev, f"compose.edit.shots[{i}].{key}"))
        for where, rng in self.compose_ranges():
            for key in ("from", "to"):
                v = rng.get(key)
                if isinstance(v, str):
                    ok, ev, _ = parse_time(v)
                    if ok and ev:
                        out.append((ev, f"{where}.{key}"))
        rev = comp.get("review")
        if isinstance(rev, dict) and isinstance(rev.get("events"), list):
            for i, v in enumerate(rev["events"]):
                if isinstance(v, str) and not any(ch in v for ch in "*?["):
                    ok, ev, _ = parse_time(v if v.startswith("@") else "@" + v)
                    if ok and ev:
                        out.append((ev, f"compose.review.events[{i}]"))
        return out

    def compose_ranges(self) -> list[tuple[str, dict]]:
        out: list[tuple[str, dict]] = []
        comp = self.spec.compose
        if not isinstance(comp, dict):
            return out
        grade = comp.get("grade") if isinstance(comp.get("grade"), dict) else {}
        blocks = [("compose.grade.default", grade.get("default"))]
        cams = grade.get("cams") if isinstance(grade.get("cams"), dict) else {}
        blocks += [(f"compose.grade.cams.{k}", v) for k, v in cams.items()]
        for li, lay in enumerate(comp.get("layouts") or []):
            if not isinstance(lay, dict):
                continue
            for ri, row in enumerate(lay.get("rows") or []):
                for ci, cell in enumerate(row if isinstance(row, list) else [row]):
                    if isinstance(cell, dict):
                        blocks.append((f"compose.layouts[{li}].rows[{ri}][{ci}].grade", cell.get("grade")))
        for where, g in blocks:
            if isinstance(g, dict) and isinstance(g.get("ranges"), list):
                for i, r in enumerate(g["ranges"]):
                    if isinstance(r, dict):
                        out.append((f"{where}.ranges[{i}]", r))
        return out

    def load_predictions(self) -> None:
        rehearsal: dict[str, float] = {}
        rpath = self.opts.rehearsal or (Path(self.spec.rehearsal) if self.spec.rehearsal else None)
        if rpath is not None:
            cands = [rpath] if rpath.is_absolute() else [self.path.parent / rpath, REPO / rpath, REPO / "out" / rpath]
            folder = next((c for c in cands if (c / "events.csv").exists()), None)
            if folder is None:
                self.report.warn("rehearsal", f"no events.csv under '{rpath}' (predictions fall back to expect)")
            else:
                with (folder / "events.csv").open(encoding="utf-8", newline="") as fh:
                    for row in csv.DictReader(fh):
                        if row.get("kind") == "event" and row.get("name") and row["name"] not in rehearsal:
                            try:
                                rehearsal[row["name"]] = float(row["game_time"])
                            except (TypeError, ValueError):
                                pass
                self.report.note("rehearsal", f"{len(rehearsal)} event time(s) read from {folder / 'events.csv'}")
        for e in self.spec.events:
            if not math.isnan(e.expect):
                self.predicted[e.name] = (float(e.expect), "expect")
                if e.name in rehearsal and abs(rehearsal[e.name] - e.expect) > 1e-3:
                    self.report.warn(self.w(e.where, "expect"), f"expect {fnum(e.expect)} wins over the rehearsal time {fnum(rehearsal[e.name])}; "
                                                               "delete expect to use the rehearsal")
            elif e.name in rehearsal:
                self.predicted[e.name] = (rehearsal[e.name], "rehearsal")
            elif e.type == "time" and e.has("value"):
                self.predicted[e.name] = (float(e.value), "time")

    def resolve(self, t: TimeVal | None, fallback: float | None = 0.0) -> float | None:
        if t is None:
            return fallback
        if t.absolute:
            return t.offset
        p = self.predicted.get(t.ev)
        return None if p is None else p[0] + t.offset

    # -------------------------------------------------------------------------------- root

    def lint_root(self) -> None:
        s = self.spec
        if s.seconds <= 0:
            self.report.error("seconds", "must be > 0")
        if s.preroll < 0:
            self.report.error("preroll", "must be >= 0")
        if s.seed < -1:
            self.report.warn("seed", "use -1 (no seeding) or a value >= 0")
        if not s.cameras:
            self.report.error("cameras", "no cameras")
        o = self.output
        if o.format not in FORMATS:
            self.report.error("output.format", f"'{o.format}' is not one of {sorted(FORMATS)}")
        if not 1 <= o.jpeg <= 100:
            self.report.error("output.jpeg", "JPEG quality must be 1..100")
        if o.queue < 1:
            self.report.error("output.queue", "must be >= 1")
        if o.flushEvery < 1:
            self.report.warn("output.flushEvery", "must be >= 1")
        if o.format == "pipe":
            n = sum(1 for c in self.cams if c.eff.enabled)
            if n > NVENC_SESSIONS:
                self.report.error("output.format", f"pipe mode needs one encoder per camera: {n} enabled cameras > {NVENC_SESSIONS} NVENC sessions")
            if "hevc" not in o.pipeCodec and "265" not in o.pipeCodec:
                self.report.error("output.pipeCodec", f"'{o.pipeCodec}' is not an H.265 encoder (H.265 only)")
            if not Path(o.ffmpeg).exists() and not (Path(o.ffmpeg).name == o.ffmpeg and shutil.which(o.ffmpeg)):
                self.report.warn("output.ffmpeg", f"'{o.ffmpeg}' not found on this machine (full path, or a program on PATH)")
        if o.readback not in ("auto", "native", "readpixels"):
            self.report.error("output.readback", f"'{o.readback}' is not one of auto, native, readpixels")
        elif o.readback == "native" and o.format != "pipe":
            self.report.warn("output.readback", "native readback is used only with output.format 'pipe'")
        se = s.session
        if se.realTime not in REAL_TIMES:
            self.report.error("session.realTime", f"'{se.realTime}' is not one of {sorted(REAL_TIMES)}")
        if se.res != (0.0, 0.0):
            w_, h_ = se.res
            if w_ <= 0 or h_ <= 0 or w_ != int(w_) or h_ != int(h_):
                self.report.error("session.res", "use whole positive [width, height] or [0, 0]")
            else:
                self.res = (int(w_), int(h_))
                if int(w_) % 2 or int(h_) % 2:
                    self.report.warn("session.res", "odd sizes cannot be encoded as yuv420")
        if se.gcSteps < 0:
            self.report.warn("session.gcSteps", "must be >= 0")
        if not se.lockInput:
            self.report.warn("session.lockInput", "keyboard and mouse can move the camera during the capture")
        st = s.stream
        if st.keepLoaded not in KEEP_LOADED:
            self.report.error("stream.keepLoaded", f"'{st.keepLoaded}' is not one of {sorted(KEEP_LOADED)}")
        if st.keepLoaded == "list" and not st.cullers:
            self.report.warn("stream.cullers", "keepLoaded 'list' with no culler names keeps nothing loaded")
        if st.timeout <= 0:
            self.report.warn("stream.timeout", "must be > 0")
        if st.freeze:
            self.report.warn("stream.freeze", "experimental: culling is switched off during the take")
        self.lint_post(s.post, "post")
        if s.compose is not None and not isinstance(s.compose, dict):
            self.report.error("compose", "object expected")

    def lint_post(self, p: Node | None, where: str) -> None:
        if p is None:
            return
        if not math.isnan(p.brightness) and not 0 <= p.brightness <= 1:
            self.report.error(self.w(where, "brightness"), "absolute gamma_gamma must be 0..1 (game 0.5)")
        elif not math.isnan(p.brightness) and p.brightness > 0.65:
            self.report.warn(self.w(where, "brightness"), f"{fnum(p.brightness)} lifts a lot; 0.55-0.60 is the gentle range")
        if p.blacklift > 0.025:
            self.report.warn(self.w(where, "blacklift"), f"{fnum(p.blacklift)} > 0.025 washes out the blacks")
        if p.blacklift < 0:
            self.report.warn(self.w(where, "blacklift"), "negative black lift crushes the shadows")
        if p.inWhiteScale <= 0:
            self.report.error(self.w(where, "inWhiteScale"), "must be > 0")
        if p.bloomScale < 0 or p.distortionScale < 0:
            self.report.warn(where, "bloomScale / distortionScale must be >= 0")
        if abs(p.postGamma) > 0.15:
            self.report.warn(self.w(where, "postGamma"), f"{fnum(p.postGamma)} is strong; 0.03-0.08 is the gentle range")

    # -------------------------------------------------------------------------------- events

    def lint_events(self) -> None:
        names: dict[str, int] = {}
        for i, e in enumerate(self.spec.events):
            w = e.where or f"events[{i}]"
            if not e.name:
                self.report.error(w, "event needs a name")
            elif not re.fullmatch(r"[A-Za-z0-9_.]+", e.name):
                self.report.error(self.w(w, "name"), f"'{e.name}' may only use letters, digits, '_' and '.'")
            elif e.name in names:
                self.report.error(self.w(w, "name"), f"duplicate event name '{e.name}'")
            names.setdefault(e.name, i)
            if e.type not in self.schema.event_types:
                self.report.error(self.w(w, "type"), f"unknown event type '{e.type}' (one of {', '.join(self.schema.event_types)})")
                continue
            allowed = EVENT_COMMON | EVENT_KEYS.get(e.type, set())
            for k in e.given:
                if k not in allowed:
                    self.report.warn(self.w(w, k), f"ignored by event type '{e.type}'")
            for k in EVENT_REQUIRED.get(e.type, []):
                if not e.has(k) or e.v.get(k) in ("", None):
                    level = "warn" if e.type == "time" else "error"
                    self.report.add(level, w, f"event type '{e.type}' needs '{k}'")
            if e.type == "fsm":
                edge = e.edge if e.has("edge") else ("event" if e.fsmEvent and not e.state else "enter")
                if edge == "event" and not e.fsmEvent:
                    self.report.error(w, "fsm edge 'event' needs 'fsmEvent'")
                if edge in ("enter", "exit") and not e.state:
                    self.report.error(w, f"fsm edge '{edge}' needs 'state'")
            if e.has("edge"):
                ok_edges = {"enter", "exit", "event"} if e.type == "fsm" else {"enter", "exit"}
                if e.edge not in ok_edges:
                    self.report.error(self.w(w, "edge"), f"'{e.edge}' is not one of {sorted(ok_edges)} for type '{e.type}'")
            if e.type == "cond":
                if e.prop and e.prop not in COND_PROPS:
                    self.report.error(self.w(w, "prop"), f"'{e.prop}' is not one of {sorted(COND_PROPS)}")
                if e.prop == "dist" and e.to is None:
                    self.report.error(w, "cond prop 'dist' needs 'to'")
            if e.op and e.op not in OPS:
                self.report.error(self.w(w, "op"), f"'{e.op}' is not one of {sorted(OPS)}")
            if e.type == "field" and not e.op and not e.match:
                self.report.error(w, "field watch needs 'op' + 'value' or 'match'")
            if e.type == "box" and e.has("size") and min(abs(x) for x in e.size) <= 0:
                self.report.error(self.w(w, "size"), "box size must be positive on every axis")
            if e.type == "impact" and e.min <= 0:
                self.report.warn(self.w(w, "min"), "impact min 0 fires on the smallest contact")
            if e.count < 1:
                self.report.error(self.w(w, "count"), "must be >= 1")
            if e.cooldown < 0:
                self.report.error(self.w(w, "cooldown"), "must be >= 0")
            if e.obj.startswith("#"):
                self.report.warn(self.w(w, "obj"), "instance ids are session-local; use a path")
            if e.after is not None and e.after.ev == e.name:
                self.report.error(self.w(w, "after"), "an event cannot wait for itself")
            if not e.log:
                self.report.note(w, "log false: fires are not written to events.csv (the compositor cannot resolve it)")
        self.chains_cycle()

    def chains_cycle(self) -> None:
        deps = {e.name: e.after.ev for e in self.spec.events if e.after is not None and e.after.ev and e.after.ev != e.name}
        where = {e.name: e.where for e in self.spec.events}
        reported: set[frozenset] = set()
        for start in deps:
            seen, cur = [start], deps.get(start)
            while cur and cur in deps:
                if cur in seen:
                    loop = frozenset(seen[seen.index(cur):])
                    if loop not in reported:
                        reported.add(loop)
                        self.report.error(self.w(where.get(start, ""), "after"), "after-chain cycle: " + " -> ".join(seen + [cur]) + " (never fires)")
                    break
                seen.append(cur)
                cur = deps.get(cur)

    def lint_refs(self) -> None:
        names = {e.name for e in self.spec.events}
        for ev, wheres in self.refs.items():
            if ev not in names:
                for w in wheres:
                    self.report.error(w, f"unknown event '{ev}'")
        for e in self.spec.events:
            if e.name and e.name not in self.refs:
                self.report.warn(e.where, f"event '{e.name}' is never referenced (it is only logged)")
        neg_seen: set[str] = set()

        def visit(node: Any, where: str) -> None:
            if self.replay and not (where.startswith("cues") or where == "until"):
                return
            if isinstance(node, TimeVal) and node.ev and node.offset < 0 and node.ev in names and node.ev not in self.predicted:
                if node.ev not in neg_seen:
                    neg_seen.add(node.ev)
                    self.report.warn(where, f"'{node.text()}' needs a prediction: give events.{node.ev}.expect or a rehearsal folder, "
                                            f"otherwise it resolves when the event fires, {fnum(-node.offset)} s too late")
        walk(self.schema, self.spec, "", visit)
        for e in self.spec.events:
            if e.name in self.predicted and self.predicted[e.name][1] == "expect" and e.after is not None:
                after_t = self.resolve(e.after, None)
                if after_t is not None and self.predicted[e.name][0] < after_t:
                    self.report.warn(self.w(e.where, "expect"), f"expect {fnum(e.expect)} is before its 'after' time {fnum(after_t)}")

    # -------------------------------------------------------------------------------- timing (design 3.4)

    def lint_timing(self) -> None:
        s = self.spec
        if self.fps <= 0:
            self.report.error("timing.fps", "must be > 0")
            self.fps = 30.0
        elif abs(self.fps - round(self.fps)) > 1e-6:
            self.report.warn("timing.fps", f"{fnum(self.fps)} is not a whole frame rate")
        if s.timing.physics not in PHYSICS:
            self.report.error("timing.physics", f"'{s.timing.physics}' is not one of {sorted(PHYSICS)}")
        elif s.timing.physics == "fine":
            self.report.warn("timing.physics", "experimental: fixedDeltaTime changes physics, CameraScript smoothing and LogicManager interpolation")
        segs: list[Segment] = []
        if s.timing.segments:
            for i, g in enumerate(s.timing.segments):
                segs.append(Segment(i, g.label, g.v["from"], None, float(g.speed), int(g.samples), float(g.shutter), float(g.rate),
                                    False, g.where or f"timing.segments[{i}]"))
                if g.has("rate") and g.has("speed"):
                    self.report.warn(self.w(g.where, "speed"), "ignored: with an explicit rate the speed follows from rate, samples and fps")
                if g.has("rate") and g.has("shutter"):
                    self.report.warn(self.w(g.where, "shutter"), "ignored: with an explicit rate every sub-step is captured")
        elif s.rates:
            for i, r in enumerate(s.rates):
                segs.append(Segment(i, "", r.v["from"], None, 1.0, int(r.group), 360.0, float(r.rate), True, r.where or f"rates[{i}]"))
        else:
            segs.append(Segment(0, "", TimeVal(None, 0.0), None, 1.0, 1, 180.0, 0.0, False, "timing"))
        for g in segs:
            g.start_t = self.resolve(g.start, None)
            self.derive(g)
        prev = None
        for g in segs:
            if g.start_t is None:
                self.report.note(g.where, f"segment {g.index} starts at '{g.start.text()}', which has no prediction: the estimate ignores it")
                continue
            if prev is not None and g.start_t < prev - 1e-9:
                self.report.warn(self.w(g.where, "from"), f"starts at {fnum(g.start_t)} before the previous segment ({fnum(prev)}); "
                                                         "the runner uses the last listed segment that is reached")
            prev = g.start_t
        first = next((g for g in segs if g.start_t is not None), None)
        if first is not None and first.start_t > 1e-9 and first.start.absolute:
            self.report.warn(self.w(first.where, "from"), f"no segment starts at 0 (first at {fnum(first.start_t)}); the estimate uses it from t=0")
        self.segments = segs

    def derive(self, g: Segment) -> None:
        fps = self.fps
        R: float | None = None
        if g.samples < 1:
            self.report.error(f"{g.where}[2]" if g.legacy else self.w(g.where, "samples"), "samples must be >= 1")
            return
        if self.replay:
            self.derive_replay(g)
            return
        if g.legacy or g.rate > 0:
            R, N, M = g.rate, g.samples, g.samples
            g.s_eff = M * fps / R if R > 0 else None
            if g.legacy and R <= 0:
                self.report.error(g.where, "rate must be > 0")
                return
        else:
            if g.speed <= 0:
                self.report.error(self.w(g.where, "speed"), "must be > 0")
                return
            M = g.samples
            if M == 1:
                N = 1
            else:
                if not 0 < g.shutter <= 360:
                    self.report.error(self.w(g.where, "shutter"), "shutter must be in (0, 360] degrees")
                    return
                exact = M * 360.0 / g.shutter
                N = int(round(exact))
                if abs(exact - N) > 1e-6:
                    self.report.warn(self.w(g.where, "shutter"), f"samples*360/shutter = {fnum(exact)} is not whole: {N} steps per frame "
                                                                f"(effective shutter {fnum(M * 360.0 / N, 1)} deg)")
                if N < M:
                    self.report.error(self.w(g.where, "shutter"), "shutter above 360 deg")
                    return
            R = N * fps / g.speed
            g.s_eff = g.speed
        g.N, g.M = N, M
        whole = round(R)
        if abs(R - whole) > 1e-3:
            self.report.error(g.where, f"step rate R = {fnum(R, 4)} is not whole; choose fps/speed/samples so that R is whole")
            g.R = R
            return
        if abs(R - whole) > 1e-9:
            self.report.note(g.where, f"step rate {fnum(R, 6)} is treated as {whole}; exact speeds (1, 0.5, 0.25, 0.125) avoid rounding")
        g.R = float(whole)
        if not self.schema.min_rate <= whole <= self.schema.max_rate:
            self.report.error(g.where, f"step rate R = {whole} is outside [{self.schema.min_rate}, {self.schema.max_rate}]")
        if self.spec.timing.physics != "fine" and whole > 0 and ((whole < 60 and 60 % whole) or (whole > 60 and whole % 60)):
            self.report.warn(g.where, f"step rate {whole} does not divide or multiply 60 Hz physics: uneven FixedUpdate cadence")

    def derive_replay(self, g: Segment) -> None:
        M = g.samples
        if g.legacy or g.rate > 0:
            if g.rate <= 0:
                self.report.error(g.where, "rate must be > 0")
                return
            g.s_eff = M * self.fps / g.rate
        else:
            if g.speed <= 0:
                self.report.error(self.w(g.where, "speed"), "must be > 0")
                return
            if M > 1 and not 0 < g.shutter <= 360:
                self.report.error(self.w(g.where, "shutter"), "shutter must be in (0, 360] degrees")
                return
            g.s_eff = g.speed
        g.N, g.M = M, M
        g.R = M * self.fps / g.s_eff

    def build_timeline(self) -> None:
        s = self.spec
        end = float(s.seconds)
        if s.until is not None:
            u = self.resolve(s.until, None)
            if u is None:
                self.report.note("until", f"'{s.until.text()}' has no prediction; the estimate runs to seconds = {fnum(end)}")
            elif u > end + 1e-9:
                self.report.warn("until", f"'{s.until.text()}' = {fnum(u)} is after seconds = {fnum(end)}: seconds ends the take first")
            else:
                end = max(0.0, u)
        self.end_t = end
        segs = [g for g in self.segments if g.start_t is not None and g.R]
        if not segs:
            return
        cuts = sorted({0.0, end} | {g.start_t for g in segs if 0 < g.start_t < end})
        for a, b in zip(cuts, cuts[1:]):
            active = None
            for g in segs:
                if g.start_t <= a + 1e-9:
                    active = g
            self.pieces.append(Piece(active or segs[0], a, b))

    def out_time(self, t: float) -> float:
        o = 0.0
        for p in self.pieces:
            if t <= p.a:
                break
            d = min(t, p.b) - p.a
            o += d / (p.seg.s_eff or 1.0)
        if self.pieces and t > self.pieces[-1].b:
            o += (t - self.pieces[-1].b) / (self.pieces[-1].seg.s_eff or 1.0)
        return o

    def speed_at(self, t: float) -> float:
        for p in self.pieces:
            if p.a - 1e-9 <= t < p.b:
                return p.seg.s_eff or 1.0
        return self.pieces[-1].seg.s_eff if self.pieces else 1.0

    # -------------------------------------------------------------------------------- setup and cues

    def lint_commands(self) -> None:
        s = self.spec
        self.lint_setup(s.setup, "setup")
        for i, c in enumerate(s.cues):
            w = c.where or f"cues[{i}]"
            cmds = ([c.cmd] if c.cmd else []) + list(c.cmds)
            for j, cmd in enumerate(cmds):
                self.lint_command(cmd, w if len(cmds) == 1 else f"{w}.cmd{j}", False)
            t = self.resolve(c.at, None)
            if t is not None and t > self.end_t + 1e-9:
                self.report.warn(self.w(w, "at"), f"cue at {fnum(t)} is after the take end ({fnum(self.end_t)}): it never runs")
            if t is not None and t < 0:
                self.report.warn(self.w(w, "at"), "negative time: the cue runs at the first step")

    def lint_setup(self, cmds: list[str], base: str) -> None:
        after_load = False
        for i, cmd in enumerate(cmds):
            kind = self.lint_command(cmd, f"{base}[{i}]", True)
            if kind == "load":
                after_load = True
            elif kind in ("wait", "waitfor") and after_load:
                after_load = False
            elif after_load and kind in ("tp", "call", "signal", "fsm"):
                self.report.warn(f"{base}[{i}]", "runs right after 'load' (asynchronous): add 'waitfor savepoint <name>' / 'waitfor streaming' / 'wait <s>' first")
                after_load = False

    def lint_command(self, cmd: str, where: str, setup: bool) -> str:
        a = split_command(cmd)
        if not a:
            self.report.error(where, "empty command")
            return ""
        c = a[0].lower()
        if c in RUNNER_PRIMITIVES:
            if not setup:
                self.report.error(where, f"'{c}' is a runner primitive for setup only")
                return c
            if c == "wait":
                if len(a) != 2 or cs_double(a[1]) is None or float(a[1]) < 0:
                    self.report.error(where, "usage: wait <real seconds>")
            else:
                sub = a[1].lower() if len(a) > 1 else ""
                ok = (sub in ("loaded", "streaming") and len(a) == 2) or (sub in ("savepoint", "event") and len(a) == 3)
                if not ok:
                    self.report.error(where, "usage: waitfor loaded | streaming | savepoint <name> | event <name>")
            return c
        if c == "mcam":
            self.report.error(where, "mcam commands cannot run inside a spec")
            return c
        if self.commands and c not in self.commands and c not in CORE_COMMANDS:
            self.report.warn(where, f"'{c}' is not a bridge or console command in Remote.cs / DevRoot.cs (extension commands are not checked)")
        if c == "tp":
            nums = [x for x in a[1:] if cs_double(x) is not None]
            if nums and len(a) != 4:
                self.report.error(where, "tp needs exactly three numbers: tp x y z")
            elif len(a) == 1:
                self.report.error(where, "usage: tp x y z | tp <obj>")
        elif c == "call" and len(a) < 4:
            self.report.error(where, "usage: call <object> <Component> <method>")
        elif c in ("signal", "signalin") and len(a) < 3:
            self.report.error(where, f"usage: {c} <object> <name>")
        elif c == "fsm" and len(a) < 4:
            self.report.error(where, "usage: fsm <object> [fsmName] event|state|var <name> [value]")
        elif c == "load":
            if len(a) != 3 or not all(re.fullmatch(r"\d+", x) for x in a[1:]):
                self.report.error(where, "usage: load <subsceneIndex> <savepointIndex>")
            if not setup:
                self.report.warn(where, "loading a savepoint during the take resets the scene")
        elif c in ("cam", "freecam", "zoom", "res", "fullscreen") and not setup:
            self.report.error(where, f"'{c}' moves or resizes the capture camera; not allowed as a cue")
        if c in OBJECT_COMMANDS and len(a) > 1 and a[1].startswith("#"):
            self.report.warn(where, "instance ids are session-local; use a path")
        return c

    # -------------------------------------------------------------------------------- cameras

    def lint_cameras(self) -> None:
        seen: dict[str, int] = {}
        for c in self.cams:
            w = c.where or f"cameras[{c.index}]"
            if not c.name:
                self.report.error(w, "camera needs a name")
            elif not re.fullmatch(r"[A-Za-z0-9_-]+", c.name):
                self.report.error(self.w(w, "name"), f"'{c.name}' is also a folder name: use [A-Za-z0-9_-]")
            elif c.name in seen:
                self.report.error(self.w(w, "name"), f"duplicate camera name '{c.name}'")
            elif c.name == "cut":
                self.report.warn(self.w(w, "name"), "'cut' is the compositor's name for the edit")
            seen.setdefault(c.name, c.index)
            if c.mode not in self.schema.modes:
                self.report.error(self.w(w, "mode"), f"unknown mode '{c.mode}' (one of {', '.join(self.schema.modes)})")
                continue
            allowed = COMMON_CAM_KEYS | MODE_KEYS.get(c.mode, set())
            for k in c.raw.given:
                if k not in allowed:
                    self.report.warn(self.w(w, k), f"ignored by mode '{c.mode}'")
            self.lint_camera_common(c, w)
            getattr(self, "lint_events_mode" if c.mode == "events" else f"lint_{c.mode}", lambda *_: None)(c, w)
        self.lint_director_graph()

    def lint_camera_common(self, c: CamInfo, w: str) -> None:
        e = c.eff
        if e.fov > 60:
            self.report.warn(self.w(w, "fov"), f"{fnum(e.fov)} deg is wide (the game is {fnum(self.schema.game_fov, 2)}): edges stretch")
        elif 0 < e.fov < 15:
            self.report.warn(self.w(w, "fov"), f"{fnum(e.fov)} deg is a long telephoto: TAA jitter and fog read differently")
        if 0 < e.near < 0.3:
            self.report.warn(self.w(w, "near"), "near < 0.3 loses depth precision (z-fighting)")
        if e.near < 0:
            self.report.error(self.w(w, "near"), "must be >= 0 (0 = game near 1.0)")
        r = c.render
        if r.warmup < 1:
            self.report.error(self.w(w, "render.warmup"), "warmup must be >= 1 (the first render after a pose change is culled late)")
        if r.samples < 1:
            self.report.error(self.w(w, "render.samples"), "samples must be >= 1")
        if r.taa not in TAA_MODES:
            self.report.error(self.w(w, "render.taa"), f"'{r.taa}' is not one of {sorted(TAA_MODES)}")
        elif r.taa == "toggle":
            self.report.warn(self.w(w, "render.taa"), "experimental fallback")
        if r.jitter and not 1 <= r.jitter <= 15:
            self.report.error(self.w(w, "render.jitter"), "jitter pattern ids are 1..15 (0 = auto from samples)")
        if r.taa == "keep" and r.samples > 1:
            self.report.note(self.w(w, "render.taa"), "'keep' with samples > 1 renders extra frames with the game feedback (no history reset)")
        if c.raw.post is not None:
            self.lint_post(c.raw.post, self.w(w, "post"))
        sh = c.shake
        if sh is not None:
            if sh.clock not in CLOCKS:
                self.report.error(self.w(w, "shake.clock"), f"'{sh.clock}' is not one of {sorted(CLOCKS)}")
            for k in ("hand", "fx", "replica", "hard"):
                if sh.v[k] < 0 or sh.v[k] > 3:
                    self.report.warn(self.w(w, f"shake.{k}"), f"{fnum(sh.v[k])} is outside 0..3")
        for key in ("hide", "backfaces"):
            for i, h in enumerate(getattr(e, key)):
                id_err = hide_instance_id_error(h)
                if id_err:
                    self.report.error(f"{w}.{key}[{i}]", id_err)
                elif hide_query(h).startswith("#"):
                    self.report.warn(f"{w}.{key}[{i}]", "instance ids are session-local; use a path")
                err = hide_box_error(h)
                if err:
                    self.report.error(f"{w}.{key}[{i}]", err if key == "hide" else err.replace("box hide", "box backfaces entry"))
        if e.lag not in LAGS:
            self.report.error(self.w(w, "lag"), f"'{e.lag}' is not one of {sorted(LAGS)}")
        if e.interp not in INTERPS:
            self.report.error(self.w(w, "interp"), f"'{e.interp}' is not one of {sorted(INTERPS)}")
        if c.mode in EASES and e.ease not in EASES[c.mode]:
            self.report.error(self.w(w, "ease"), f"'{e.ease}' is not one of {sorted(EASES[c.mode])} for mode '{c.mode}'")
        if e.smooth < 0 or e.lookSmooth < 0:
            self.report.error(w, "smooth / lookSmooth must be >= 0")
        k = e.clamp
        if k is not None:
            if not k.has("left") and not k.has("right"):
                self.report.warn(self.w(w, "clamp"), "neither left nor right given: no edge is clamped")
            if k.has("left") and k.has("right") and k.left >= k.right:
                self.report.error(self.w(w, "clamp"), f"left {fnum(k.left)} must be below right {fnum(k.right)}")
            if k.aspect < 0:
                self.report.error(self.w(w, "clamp.aspect"), "must be >= 0 (0 = the frame's own)")
        m = e.v.get("motion")
        if m is not None:
            for key in ("maxSpeed", "maxTurn", "ease", "blurScale"):
                if m.v[key] < 0:
                    self.report.error(self.w(w, f"motion.{key}"), "must be >= 0 (0 turns a limit off)")
            if c.mode == "game":
                self.report.warn(self.w(w, "motion"), "the game camera is never limited (only blurScale applies)")
            if m.blurScale > 2:
                self.report.warn(self.w(w, "motion.blurScale"), f"{fnum(m.blurScale)} > 2 smears more than the game's own blur")
        v = e.view
        if v is not None:
            for key in ("margin", "subjectRadius", "every"):
                if v.v[key] < 0:
                    self.report.error(self.w(w, f"view.{key}"), "must be >= 0")
            for i, layer in enumerate(v.layers):
                if not 0 <= layer < 32:
                    self.report.error(f"{w}.view.layers[{i}]", "Unity layers are 0..31")

    def euler_rules(self, where: str, yaw: float, pitch: float, z: float | None, c: CamInfo, what: str, strict: bool) -> None:
        e = c.eff
        fz = euler_forward(yaw, pitch)[2]
        back = e.playerSide and fz < -0.05 and pitch < 60
        if back:
            self.report.add("error" if strict else "warn", where, f"{what} looks back toward the screen (forward z {fnum(fz, 2)}); sets have no back side")
        elif abs(yaw) > 40:
            self.report.warn(where, f"{what} yaw {fnum(yaw, 1)}: more than 40 deg off the set axis shows set edges")
        if pitch < -20 or pitch > 35:
            self.report.warn(where, f"{what} pitch {fnum(pitch, 1)} is outside -20..35 (positive pitch looks down)")
        if not e.playerSide:
            return
        if z is not None and z > e.zMax and pitch < 45:
            self.report.add("error" if strict else "warn", where,
                            f"{what} z {fnum(z, 2)} is behind the player-side line (z must be below {fnum(e.zMax, 2)} unless looking down > 45 deg)")

    def fixed_point(self, t: Node | None) -> tuple[float, float, float] | None:
        if t is None or not t.has("pos") or t.v.get("group") is not None:
            return None
        off = t.v.get("offset") or (0.0, 0.0, 0.0)
        return tuple(p + o for p, o in zip(t.pos, off))

    def lint_game(self, c: CamInfo, w: str) -> None:
        if c.eff.fovScale <= 0:
            self.report.error(self.w(w, "fovScale"), "must be > 0")

    def lint_static(self, c: CamInfo, w: str) -> None:
        e = c.eff
        if not e.has("pos"):
            self.report.error(w, "static camera needs 'pos'")
            return
        look = e.look
        if look is None or look.v.get("of") == "none":
            self.euler_rules(w, e.yaw, e.pitch, e.pos[2], c, "static", True)
            return
        if e.has("yaw") or e.has("pitch"):
            self.report.note(w, "yaw / pitch are ignored while 'look' is set")
        lp = self.fixed_point(look)
        if lp is None:
            if e.pos[2] > e.zMax and e.playerSide:
                self.report.warn(w, f"static z {fnum(e.pos[2], 2)} is behind the player-side line; allowed only when looking down > 45 deg")
            return
        lp = tuple(a + b for a, b in zip(lp, e.lookOffset))
        v = tuple(a - b for a, b in zip(lp, e.pos))
        dist = math.sqrt(sum(x * x for x in v))
        if dist < 1.5 and not e.has("near"):
            self.report.warn(w, f"look point {fnum(dist, 2)} m away with the game near clip 1.0: set near 0.3")
        ang = look_angles(v)
        if ang:
            self.euler_rules(w, ang[0], ang[1], e.pos[2], c, "static", True)

    def lint_follow(self, c: CamInfo, w: str) -> None:
        e = c.eff
        if e.target is None:
            self.report.error(w, "follow needs 'target'")
            return
        if e.smooth == 0 and e.lag == "damp":
            self.report.note(self.w(w, "smooth"), "smooth 0: the camera is rigidly attached to the target")
        if e.lead < 0 or e.lead > 2:
            self.report.warn(self.w(w, "lead"), f"{fnum(e.lead)} s look-ahead is unusual (0..0.5)")
        look_none = e.look is not None and e.look.v.get("of") == "none"
        if look_none:
            self.euler_rules(w, e.yaw, e.pitch, None, c, "follow", True)
        elif e.look is None or e.look.v.get("of") == "self":
            v = tuple(lo - o for lo, o in zip(e.lookOffset, e.offset))
            ang = look_angles(v)
            dist = math.sqrt(sum(x * x for x in v))
            if dist < 1.5 and not e.has("near"):
                self.report.warn(w, f"camera {fnum(dist, 2)} m from its look point with the game near clip 1.0: set near 0.3")
            if ang and not e.local:
                self.euler_rules(w, ang[0], ang[1], None, c, "follow (offset -> look)", False)
        if not e.local and e.offset[2] > -1.0 and e.playerSide:
            self.report.warn(self.w(w, "offset"), f"camera z = target z {'+' if e.offset[2] >= 0 else ''}{fnum(e.offset[2])}; sets sit near z 0, "
                                                  "so this is likely behind the player-side line (the runner clamps it)")
        if e.start is not None and e.start.ev:
            self.report.note(self.w(w, "start"), "holds its Begin pose (flag W) until the start time is reached")

    def lint_keys(self, c: CamInfo, w: str) -> None:
        e = c.eff
        if not e.keys:
            self.report.error(w, "keys camera needs 'keys'")
            return
        prev = None
        for i, k in enumerate(e.keys):
            kw = f"{w}.keys[{i}]"
            if not k.has("pos"):
                self.report.error(kw, "key needs 'pos'")
            if prev is not None and k.t <= prev:
                self.report.error(self.w(kw, "t"), f"keys must be sorted by strictly increasing t ({fnum(k.t)} after {fnum(prev)})")
            prev = k.t
            if k.t < 0:
                self.report.warn(self.w(kw, "t"), "negative key time on the camera clock")
            if k.fov > 60:
                self.report.warn(self.w(kw, "fov"), f"{fnum(k.fov)} deg is wide")
            lp = self.fixed_point(k.look) if k.look is not None else None
            if k.look is not None and k.look.v.get("of") not in (None, "none") and lp is None:
                if k.pos[2] > e.zMax and e.playerSide:
                    self.report.warn(kw, f"key z {fnum(k.pos[2], 2)} is behind the player-side line")
                continue
            if lp is not None:
                v = tuple(a + b - p for a, b, p in zip(lp, k.lookOffset, k.pos))
                ang = look_angles(v)
                if ang:
                    self.euler_rules(kw, ang[0], ang[1], None, c, "key", True)
                if k.pos[2] > e.zMax and e.playerSide:
                    self.report.warn(kw, f"key z {fnum(k.pos[2], 2)} is behind the player-side line")
                continue
            self.euler_rules(kw, k.yaw, k.pitch, None, c, "key", True)
            if k.pos[2] > e.zMax and k.pitch < 45 and e.playerSide:
                self.report.warn(kw, f"key z {fnum(k.pos[2], 2)} is behind the player-side line")
        if len(e.keys) == 1:
            self.report.note(w, "one key: the camera holds that pose")
        t0 = self.resolve(e.start, 0.0)
        if t0 is not None and t0 + e.keys[0].t > self.end_t:
            self.report.warn(self.w(w, "start"), f"the move starts at {fnum(t0 + e.keys[0].t)}, after the take end ({fnum(self.end_t)})")

    def lint_rail(self, c: CamInfo, w: str) -> None:
        e = c.eff
        if len(e.path) < 2:
            self.report.error(self.w(w, "path"), "rail needs at least 2 points")
        if e.duration <= 0:
            self.report.error(self.w(w, "duration"), "rail needs duration > 0")
        for i, p in enumerate(e.path):
            if p[2] > e.zMax and e.playerSide:
                self.report.warn(f"{w}.path[{i}]", f"rail point z {fnum(p[2], 2)} is behind the player-side line")
        if e.look is None or e.look.v.get("of") == "none":
            self.euler_rules(w, e.yaw, e.pitch, None, c, "rail", True)

    def lint_orbit(self, c: CamInfo, w: str) -> None:
        e = c.eff
        if e.radius <= 0:
            self.report.error(self.w(w, "radius"), "must be > 0")
        if e.duration < 0:
            self.report.error(self.w(w, "duration"), "must be >= 0")
        if e.target is None:
            self.report.error(w, "orbit needs 'target' (the centre)")
            return
        for a in e.azimuth:
            if abs(a) > 60:
                self.report.warn(self.w(w, "azimuth"), f"azimuth {fnum(a)} deg swings past 60 deg: the set's sides show")
        centre = self.fixed_point(e.target)
        if centre is not None and e.playerSide:
            for a in (e.azimuth[0], e.azimuth[1], (e.azimuth[0] + e.azimuth[1]) / 2):
                z = centre[2] + e.offset[2] - math.cos(math.radians(a)) * e.radius
                if z > e.zMax:
                    self.report.warn(w, f"orbit passes z {fnum(z, 2)} at azimuth {fnum(a)}: behind the player-side line")
                    break

    def lint_gamerel(self, c: CamInfo, w: str) -> None:
        e = c.eff
        if abs(e.orbitYaw) > 40:
            self.report.warn(self.w(w, "orbitYaw"), f"{fnum(e.orbitYaw)} deg off the game axis shows set edges")
        if e.orbitPitch < -20 or e.orbitPitch > 45:
            self.report.warn(self.w(w, "orbitPitch"), f"{fnum(e.orbitPitch)} deg is outside -20..45")
        if e.distScale <= 0:
            self.report.error(self.w(w, "distScale"), "must be > 0")
        if e.dist < 0:
            self.report.error(self.w(w, "dist"), "must be >= 0 (0 = game distance * distScale)")

    def lint_gamelike(self, c: CamInfo, w: str) -> None:
        e = c.eff
        if e.target is None:
            self.report.warn(w, "gamelike without 'target': give the subject explicitly (\"boy\" uses the game probe)")
        if not e.has("lag"):
            self.report.note(w, "gamelike uses lag 'game' (the game smoothing law) unless lag is given")

    def lint_frame(self, c: CamInfo, w: str) -> None:
        e = c.eff
        if e.target is None:
            self.report.error(w, "frame needs 'target'")
            return
        u, v = e.screen
        if not (0 <= u <= 1 and 0 <= v <= 1):
            self.report.error(self.w(w, "screen"), "screen point must be inside [0, 1] x [0, 1]")
        if not 0 < e.size <= 1:
            self.report.error(self.w(w, "size"), "subject size is a fraction of the frame height in (0, 1]")
        if e.crop is not None:
            if len(e.crop) != 4:
                self.report.error(self.w(w, "crop"), "crop is [x, y, w, h] in capture pixels")
            else:
                x, y, cw, ch = e.crop
                if cw <= 0 or ch <= 0 or x < 0 or y < 0 or x + cw > self.res[0] or y + ch > self.res[1]:
                    self.report.error(self.w(w, "crop"), f"crop [{', '.join(fnum(q) for q in e.crop)}] is outside the {self.res[0]}x{self.res[1]} capture")
        self.euler_rules(w, e.yaw, e.pitch, None, c, "frame", True)

    def lint_director(self, c: CamInfo, w: str) -> None:
        e = c.eff
        if not e.shots:
            self.report.error(w, "director needs 'shots'")
            return
        if c.raw.has("shake"):
            self.report.warn(self.w(w, "shake"), "a director inherits shake from its sources")
        times: list[tuple[float, int]] = []
        for i, sh in enumerate(e.shots):
            sw = f"{w}.shots[{i}]"
            if not sh.cam:
                self.report.error(sw, "shot needs 'cam'")
            elif sh.cam == c.name:
                self.report.error(self.w(sw, "cam"), "a director cannot show itself")
            elif self.cam(sh.cam) is None:
                self.report.error(self.w(sw, "cam"), f"unknown camera '{sh.cam}'")
            if sh.ease not in SHOT_EASES:
                self.report.error(self.w(sw, "ease"), f"'{sh.ease}' is not one of {sorted(SHOT_EASES)}")
            if sh.transition not in SHOT_TRANSITIONS:
                self.report.error(self.w(sw, "transition"), f"'{sh.transition}' is not one of {sorted(SHOT_TRANSITIONS)}")
            if sh.clock not in CLOCKS:
                self.report.error(self.w(sw, "clock"), f"'{sh.clock}' is not one of {sorted(CLOCKS)}")
            if sh.blend < 0:
                self.report.error(self.w(sw, "blend"), "must be >= 0")
            if sh.motionBlur < 0:
                self.report.error(self.w(sw, "motionBlur"), "must be >= 0 (a scale of the pass blur)")
            for j, h in enumerate(sh.hide):
                if h.startswith("#"):
                    self.report.warn(f"{sw}.hide[{j}]", "instance ids are session-local; use a path")
                err = hide_box_error(h)
                if err:
                    self.report.error(f"{sw}.hide[{j}]", err)
            if i == 0 and sh.blend > 0:
                self.report.note(self.w(sw, "blend"), "the first shot has nothing to blend from")
            t = self.resolve(sh.v["from"], None)
            if t is not None:
                times.append((t, i))
        if e.cutAngle <= 0 or e.cutAngle > 180:
            self.report.warn(self.w(w, "cutAngle"), "use 0 < cutAngle <= 180")
        times.sort()
        if times and times[0][0] > 1e-9:
            self.report.warn(w, f"no shot at t=0 (first at {fnum(times[0][0])})")
        for k, (t, i) in enumerate(times):
            sh = e.shots[i]
            nxt = times[k + 1][0] if k + 1 < len(times) else self.end_t
            hold = self.out_time(nxt) - self.out_time(t)
            if nxt <= t + 1e-9 and k + 1 < len(times):
                self.report.error(f"{w}.shots[{times[k + 1][1]}].from", f"two shots start at {fnum(t)}")
                continue
            if hold < e.minHold - 1e-6 and (k + 1 < len(times) or hold > 0):
                self.report.warn(f"{w}.shots[{i}]", f"'{sh.cam}' holds {fnum(hold, 2)} output s, less than minHold {fnum(e.minHold)}")
            blend_out = sh.blend if sh.clock == "output" else sh.blend / max(1e-6, self.speed_at(t))
            if k > 0 and sh.transition != "cut" and blend_out > 0.6 * max(hold, 1e-6):
                self.report.warn(f"{w}.shots[{i}].blend", f"the blend ({fnum(blend_out, 2)} output s) takes most of the hold ({fnum(hold, 2)} s)")
            if k > 0 and sh.transition != "cut" and sh.blend > 0:
                a, b = self.static_pose(self.cam(e.shots[times[k - 1][1]].cam)), self.static_pose(self.cam(sh.cam))
                if a is not None and b is not None:
                    dist = math.dist(a[0], b[0])
                    self.motion_window(c, f"{w}.shots[{i}].blend", f"blend '{e.shots[times[k - 1][1]].cam}' -> '{sh.cam}'", dist,
                                       vec_angle(a[1], b[1]), sh.ease, blend_out, hold, "static poses")

    def motion_of(self, c: CamInfo) -> Node | None:
        m = c.eff.v.get("motion")
        if m is not None:
            return m
        if self.legacy or c.mode not in ("director", "events"):
            return None
        return self.schema.new("MotionSpec")

    def static_pose(self, c: CamInfo | None) -> tuple[tuple, tuple] | None:
        if c is None or c.mode != "static" or not c.eff.has("pos"):
            return None
        e = c.eff
        look = e.look
        if look is None or look.v.get("of") == "none":
            return tuple(e.pos), euler_forward(e.yaw, e.pitch)
        a = self.fixed_look(look if look.v.get("of") != "self" else e.target, tuple(e.lookOffset))
        if a is None:
            return None
        d = tuple(x - y for x, y in zip(a, e.pos))
        return (tuple(e.pos), d) if math.sqrt(sum(x * x for x in d)) > 1e-6 else None

    # Warn when a planned move (director blend, events beat move) cannot meet the camera's motion limits in its time window:
    # the runner stretches it to `need` output seconds (MULTICAM.md 6.3), which may run into the next shot or beat.
    def motion_window(self, c: CamInfo, where: str, what: str, dist: float, turn: float, ease: str, window: float, hold: float,
                      source: str) -> None:
        m = self.motion_of(c)
        if m is None:
            return
        need = move_seconds(dist, turn, ease, m.maxSpeed, m.maxTurn, m.ease)
        if need <= window + 1e-3:
            return
        text = (f"{what} moves {fnum(dist, 1)} m and turns {fnum(turn, 0)} deg ({source}): within maxSpeed {fnum(m.maxSpeed)} m/s, "
                f"maxTurn {fnum(m.maxTurn)} deg/s and ease {fnum(m.ease)} s it needs {fnum(need, 2)} output s, more than its "
                f"{fnum(window, 2)} s (the runner stretches it")
        text += f"; that runs past the hold of {fnum(hold, 2)} s)" if hold > 0 and need > hold + 1e-3 else ")"
        self.report.warn(where, text)

    # --poses <take>: the camera moves a take really made (poses.csv, meta.csv, events.csv): peak speed and turn rate per
    # output second against each camera's limits, and every director blend / events move against its time window.
    def lint_motion_poses(self) -> None:
        folder = getattr(self.opts, "poses", None)
        if folder is None:
            return
        folder = folder if folder.is_dir() else folder.parent
        pp, mp = folder / "poses.csv", folder / "meta.csv"
        if not pp.is_file() or not mp.is_file():
            self.report.warn("poses", f"no poses.csv / meta.csv in {folder}")
            return
        frame: dict[int, int] = {}
        gtime: dict[int, float] = {}
        speed: dict[int, float] = {}
        with mp.open(encoding="utf-8", newline="") as fh:
            for row in csv.DictReader(fh):
                try:
                    st = int(row["step"])
                    if int(row.get("captured") or 1) != 1:
                        continue
                    frame[st] = int(row["frame"])
                    gtime[st] = float(row["game_time"])
                    speed[st] = float(row.get("speed") or 1.0)
                except (KeyError, ValueError):
                    continue
        series: dict[str, list[tuple]] = {}
        with pp.open(encoding="utf-8", newline="") as fh:
            for row in csv.DictReader(fh):
                try:
                    st = int(row["step"])
                    if st not in frame:
                        continue
                    series.setdefault(row["camera"], []).append((frame[st], gtime[st], (float(row["x"]), float(row["y"]), float(row["z"])),
                                                                 (float(row["qx"]), float(row["qy"]), float(row["qz"]), float(row["qw"])),
                                                                 row.get("flags") or ""))
                except (KeyError, ValueError):
                    continue
        events: dict[str, float] = {}
        ep = folder / "events.csv"
        if ep.is_file():
            with ep.open(encoding="utf-8", newline="") as fh:
                for row in csv.DictReader(fh):
                    if row.get("kind") == "event" and row.get("name") and row["name"] not in events:
                        try:
                            events[row["name"]] = float(row["game_time"])
                        except (TypeError, ValueError):
                            pass
        fps = self.fps or 30.0
        self.report.note("poses", f"{len(series)} camera path(s) from {pp} ({len(frame)} output frames, {len(events)} event time(s))")

        def at(tv: TimeVal | None) -> float | None:
            if tv is None:
                return 0.0
            if tv.absolute:
                return tv.offset
            if tv.ev in events:
                return events[tv.ev] + tv.offset
            return self.resolve(tv, None)

        for c in self.cams:
            rows = sorted(series.get(c.name, []), key=lambda r: r[0])
            if len(rows) < 3:
                continue
            m = self.motion_of(c)
            lim = m if m is not None else self.schema.new("MotionSpec")
            fast = []
            vmax = wmax = 0.0
            for a, b in zip(rows, rows[1:]):
                do = (b[0] - a[0]) / fps
                if do <= 0 or "X" in b[4]:
                    continue
                dp = math.dist(a[2], b[2])
                if dp > 25.0:
                    continue
                v, wr = dp / do, quat_angle(a[3], b[3]) / do
                vmax, wmax = max(vmax, v), max(wmax, wr)
                if (lim.maxSpeed > 0 and v > lim.maxSpeed * 1.05) or (lim.maxTurn > 0 and wr > lim.maxTurn * 1.05):
                    fast.append(b[1])
            if fast:
                runs = []
                for t in fast:
                    if runs and t - runs[-1][1] <= 0.15:
                        runs[-1][1] = t
                    else:
                        runs.append([t, t])
                spans = ", ".join(f"{fnum(x, 2)}" if abs(y - x) < 1e-6 else f"{fnum(x, 2)}-{fnum(y, 2)}" for x, y in runs[:8])
                text = (f"peak {fnum(vmax, 1)} m/s and {fnum(wmax, 0)} deg/s per output s; faster than maxSpeed {fnum(lim.maxSpeed)} m/s / "
                        f"maxTurn {fnum(lim.maxTurn)} deg/s at game {spans}" + (" ..." if len(runs) > 8 else ""))
                if m is not None:
                    self.report.warn(f"{c.where}.motion", text)
                else:
                    self.report.note(c.where, text + " (no motion limits on this camera: tracking speed is the subject's)")
            if c.mode == "director":
                self.poses_blends(c, rows, at)
            elif c.mode == "events":
                self.poses_beats(c, rows, at)

    def poses_window(self, rows: list[tuple], t0: float, t1: float) -> tuple[float, float] | None:
        sel = [r for r in rows if t0 - 1e-6 <= r[1] <= t1 + 1e-6]
        if len(sel) < 2:
            return None
        dist = sum(math.dist(a[2], b[2]) for a, b in zip(sel, sel[1:]))
        return dist, quat_angle(sel[0][3], sel[-1][3])

    def poses_blends(self, c: CamInfo, rows: list[tuple], at: Callable[[TimeVal | None], float | None]) -> None:
        e = c.eff
        times = sorted((t, i) for i, sh in enumerate(e.shots) if (t := at(sh.v["from"])) is not None)
        for k, (t, i) in enumerate(times):
            sh = e.shots[i]
            if k == 0 or sh.transition == "cut" or sh.blend <= 0:
                continue
            sp = self.speed_at(t)
            blend_game = sh.blend * sp if sh.clock == "output" else sh.blend
            blend_out = blend_game / max(1e-6, sp)
            nxt = times[k + 1][0] if k + 1 < len(times) else self.end_t
            mv = self.poses_window(rows, t, t + blend_game)
            if mv is None:
                continue
            self.motion_window(c, f"{c.where}.shots[{i}].blend", f"blend -> '{sh.cam}' at {fnum(t, 2)} s", mv[0], mv[1], sh.ease, blend_out,
                               (nxt - t) / max(1e-6, sp), "take path")

    def poses_beats(self, c: CamInfo, rows: list[tuple], at: Callable[[TimeVal | None], float | None]) -> None:
        e = c.eff
        bt = [at(b.v["at"]) for b in e.beats]
        hold = [b.hold if b.hold > 0 else e.hold for b in e.beats]
        move = [b.move if b.move > 0 else e.move for b in e.beats]
        late = [b.late if b.late >= 0 else e.late for b in e.beats]
        start = schedule(bt, hold, late)
        ease = "smooth" if e.ease == "ends" else e.ease
        for i, s0 in enumerate(start):
            if s0 is None or move[i] <= 0:
                continue
            mv = self.poses_window(rows, s0, s0 + move[i])
            if mv is None:
                continue
            sp = self.speed_at(s0)
            label = e.beats[i].label or f"#{i}"
            self.motion_window(c, f"{c.where}.beats[{i}].move", f"beat {label} move at {fnum(s0, 2)} s", mv[0], mv[1], ease, move[i] / max(1e-6, sp),
                               hold[i] / max(1e-6, sp), "take path")

    def frame_aspect(self, e: Node) -> float:
        k = e.clamp
        return k.aspect if k is not None and k.aspect > 0.1 else self.res[0] / max(1, self.res[1])

    def lint_overview(self, c: CamInfo, w: str) -> None:
        e = c.eff
        if not e.has("pos"):
            self.report.error(w, "overview needs 'pos' (camera y and z; x is the start)")
            return
        if e.target is None:
            self.report.note(w, "no target: the frame stays at pos.x until a pan moves it")
        if e.smooth < 0:
            self.report.error(self.w(w, "smooth"), "must be >= 0")
        self.euler_rules(w, e.yaw, e.pitch, e.pos[2], c, "overview", True)
        fov = e.fov if e.fov > 0 else self.schema.game_fov
        k = e.clamp
        if k is not None and (k.has("left") or k.has("right")):
            plane = k.has("z")
            depth = k.z if plane else (e.focus if e.focus > 0 else 20.0)
            xs = edge_xs((0.0, e.pos[1], e.pos[2]), e.yaw, e.pitch, fov, self.frame_aspect(e), plane, depth, k.corners)
            if xs is None:
                self.report.warn(self.w(w, "clamp"), "the frame edges never reach the clamp plane (looking away from it)")
            else:
                lo = k.left - xs[0] if k.has("left") else -math.inf
                hi = k.right - xs[1] if k.has("right") else math.inf
                width = xs[1] - xs[0]
                if lo > hi:
                    self.report.warn(self.w(w, "clamp"), f"the frame is {fnum(width, 2)} m wide at the subject depth, more than the "
                                                         f"{fnum(k.right - k.left, 2)} m between the edges: it stays centred")
                else:
                    self.report.note(w, f"frame {fnum(width, 2)} m wide at the subject depth; centre x limited to "
                                        f"{fnum(lo, 2) if not math.isinf(lo) else '-inf'} .. {fnum(hi, 2) if not math.isinf(hi) else '+inf'}")
        last = None
        for i, pn in enumerate(e.pans):
            pw = f"{w}.pans[{i}]"
            if pn.duration < 0:
                self.report.error(self.w(pw, "duration"), "must be >= 0")
            if pn.ease not in SHOT_EASES:
                self.report.error(self.w(pw, "ease"), f"'{pn.ease}' is not one of {sorted(SHOT_EASES)}")
            t = self.resolve(pn.v["from"], None)
            if t is not None and last is not None and t < last:
                self.report.warn(self.w(pw, "from"), "pans are not in time order (the latest started one wins)")
            if t is not None:
                last = t

    def lint_events_mode(self, c: CamInfo, w: str) -> None:
        e = c.eff
        if not e.beats:
            self.report.error(w, "events needs 'beats'")
            return
        for key in ("hold", "move", "late"):
            if e.v[key] < 0:
                self.report.error(self.w(w, key), "must be >= 0")
        if e.move > e.hold:
            self.report.warn(self.w(w, "move"), f"move {fnum(e.move)} s is longer than hold {fnum(e.hold)} s: the camera never settles")
        for i, bt in enumerate(e.beats):
            bw = f"{w}.beats[{i}]"
            if bt.target is None and e.target is None:
                self.report.error(self.w(bw, "target"), "beat needs a target (or the camera's target)")
            for key in ("hold", "move"):
                if bt.v[key] < 0:
                    self.report.error(self.w(bw, key), "must be >= 0")
            offs = bt.offsets or ([bt.offset] if bt.has("offset") else e.offsets or [e.offset])
            front = [o for o in offs if o[2] < -1.0]
            if e.playerSide and not front:
                self.report.warn(self.w(bw, "offsets"), "no candidate offset has z < -1: with a subject near z = 0 the camera "
                                                        "ends on the player-side line (clamped)")
            for o in offs:
                ang = look_angles(tuple(lo - oo for lo, oo in zip(bt.lookOffset if bt.has("lookOffset") else e.lookOffset, o)))
                if ang and e.playerSide and euler_forward(ang[0], ang[1])[2] < -0.05 and ang[1] < 60:
                    self.report.error(self.w(bw, "offsets"), f"offset ({', '.join(fnum(q) for q in o)}) looks back toward the screen; "
                                                             "sets have no back side")
                    break
            fov = bt.fov if bt.fov > 0 else e.fov
            if fov > 60:
                self.report.warn(self.w(bw, "fov"), f"{fnum(fov)} deg is wide")

    def lint_director_graph(self) -> None:
        dirs = {c.name: [s.cam for s in c.eff.shots] for c in self.cams if c.mode == "director"}
        seen: set[frozenset] = set()
        for start in dirs:
            stack = [(start, [start])]
            while stack:
                cur, path = stack.pop()
                for nxt in dirs.get(cur, []):
                    if nxt == start and len(path) > 1:
                        if frozenset(path) not in seen:
                            seen.add(frozenset(path))
                            self.report.error(f"{self.cam(start).where}.shots", "director cycle: " + " -> ".join(path + [nxt]))
                        stack.clear()
                        break
                    if nxt in dirs and nxt not in path:
                        stack.append((nxt, path + [nxt]))

    # -------------------------------------------------------------------------------- targets (offline subset of 3.7)

    def lint_targets(self) -> None:
        def visit(node: Any, where: str) -> None:
            if isinstance(node, Node) and node.cls == "TargetRef":
                self.lint_target(node, node.where or where)
        walk(self.schema, self.spec, "", visit)

    def lint_target(self, t: Node, where: str) -> None:
        g = t.v.get("group")
        if g is not None:
            n = sum(1 for k in ("of", "find", "list") if (g.v.get(k) if k != "of" else g.of))
            if n == 0:
                self.report.error(self.w(where, "group"), "group needs 'of', 'find' or 'list'")
            elif n > 1:
                self.report.warn(self.w(where, "group"), "use one of 'of', 'find', 'list'")
            if g.mode not in GROUP_MODES:
                self.report.error(self.w(where, "group.mode"), f"'{g.mode}' is not one of {sorted(GROUP_MODES)}")
            elif g.mode == "bounds":
                self.report.warn(self.w(where, "group.mode"), EDGE_POINT_WARN.format("group mode 'bounds'"))
            if g.list and not re.fullmatch(r"[^:]+::\w+\.\w+", g.list):
                self.report.error(self.w(where, "group.list"), "list is \"<path>::<Component>.<field>\"")
            if g.find and re.search(r"(^|\s)t:\w", g.find):
                self.report.note(self.w(where, "group.find"), "t:<Type> matches every component whose type name CONTAINS the text "
                                                             "(t:Chicken also matches ChickenGunLogic, ChickenAINode, ChickenSpawner); prefer 'list'")
            if g.mode == "nearest" and g.filter.near is None:
                self.report.error(self.w(where, "group.mode"), "mode 'nearest' needs filter.near")
            if g.smooth < 0 or g.hold < 0 or g.max < 0:
                self.report.error(self.w(where, "group"), "smooth / hold / max must be >= 0")
            if g.filter.has("boxSize") and min(g.filter.boxSize) <= 0:
                self.report.error(self.w(where, "group.filter.boxSize"), "box size must be positive")
            return
        if t.has("pos"):
            if t.v.get("of"):
                self.report.warn(where, "both 'pos' and 'of' given: the fixed point wins")
            return
        of = t.v.get("of") or ""
        point = t.v.get("point") or ""
        base = of.split("::")[0]
        if of.startswith("#"):
            self.report.warn(where, f"'{of}' is an instance id: session-local, re-check after every game start")
            return
        if of in ("none", "self"):
            if not (where.endswith(".look") or where.endswith("].look")):
                self.report.error(where, f"'{of}' is only valid as a look target")
            return
        if of in ("game", "gameaim", "probe"):
            if point:
                self.report.warn(self.w(where, "point"), f"'{of}' has no points")
            return
        if not point or point.startswith("bone:"):
            if point.startswith("bone:") and len(point) <= 5:
                self.report.error(self.w(where, "point"), "bone:<name> needs a bone name")
            return
        allowed = BOY_POINTS if of == "boy" else HUDDLE_POINTS if of == "huddle" else (BOY_POINTS | HUDDLE_POINTS) if of == "main" else OBJECT_POINTS
        if point not in allowed:
            self.report.warn(self.w(where, "point"), f"'{point}' is not a known point of '{base}' ({', '.join(sorted(allowed))}, bone:<name>)")
        elif point in EDGE_POINTS:
            self.report.warn(self.w(where, "point"), EDGE_POINT_WARN.format(f"point '{point}'"))
        if "::" in of and not re.fullmatch(r"[^:]+::\w+(\[\d+\])?", of):
            self.report.error(where, "component selector is '<path>::<Component>'")
        if of.startswith("/") or of.endswith("/"):
            self.report.error(where, "paths have no leading or trailing '/'")

    # -------------------------------------------------------------------------------- render budgets

    def visited(self) -> list[CamInfo]:
        return [c for c in self.cams if c.eff.enabled and c.mode in self.schema.modes]

    def lint_budgets(self) -> None:
        if self.replay and self.replay_nodes()[2].look == "game":
            return
        budgets = {}
        for c in self.visited():
            budgets.setdefault((c.render.warmup, c.render.samples, c.render.taa), []).append(c.name)
        if len(budgets) > 1:
            text = "; ".join(f"{w}+{n} {t}: {', '.join(names[:5])}{f' +{len(names) - 5} more' if len(names) > 5 else ''}"
                             for (w, n, t), names in budgets.items())
            self.report.warn("render", f"render budgets differ between cameras ({text}); frozen frames run Update logic per render")

    # -------------------------------------------------------------------------------- replay lint (REPLAY_DESIGN 3.2)

    def replay_nodes(self) -> tuple[Node, Node, Node, Node]:
        rs = self.spec.v.get("replay") or self.schema.new("ReplaySpec")
        rec = rs.v.get("record") or self.schema.new("RecordSpec")
        play = rs.v.get("play") or self.schema.new("PlaySpec")
        au = rs.v.get("audio") or self.schema.new("AudioCapSpec")
        return rs, rec, play, au

    def replay_const(self, name: str) -> list:
        cd = self.schema.classes.get("ReplayConst")
        return list(cd.arrays.get(name, [])) if cd else []

    def one_of(self, where: str, value: Any, name: str) -> None:
        allowed = self.replay_const(name)
        if value not in allowed:
            self.report.error(where, f"unknown value '{value}' ({', '.join(map(str, allowed))})")

    def lint_replay(self) -> None:
        s = self.spec
        self.check_removed()
        _, rec, play, au = self.replay_nodes()
        if not s.has("version") or s.version < 2:
            self.report.warn("version", "replay specs are version 2 (the v1 legacy defaults apply)")
        self.replay_timing()
        self.replay_record(rec)
        self.replay_play(play)
        self.replay_audio(rec, au)
        self.replay_v2_fields(play)
        self.replay_sides(play)
        self.replay_sound_setup()

    def check_removed(self) -> None:
        keys, reasons = self.replay_const("RemovedKeys"), self.replay_const("RemovedReasons")
        for i, key in enumerate(keys):
            parts = key.split(".")
            node: Any = self.root
            for p in parts[:-1]:
                node = node.get(p) if isinstance(node, dict) else None
            if isinstance(node, dict) and parts[-1] in node:
                self.report.error(key, reasons[i] if i < len(reasons) else "removed")

    def replay_timing(self) -> None:
        s = self.spec
        if s.rates:
            self.report.error("rates", "legacy rates are not allowed in a replay spec: use timing.segments with speed (any float speed works, no whole step rate)")
        for i, g in enumerate(s.timing.segments):
            w = g.where or f"timing.segments[{i}]"
            if g.has("rate"):
                self.report.error(self.w(w, "rate"), "not allowed in a replay spec: give speed instead (any float speed works)")
            if g.samples > COSTLY_SAMPLES:
                self.report.warn(self.w(w, "samples"), f"{g.samples} sub-frames per output frame: {g.samples} renders per frame and camera (cost)")
        if s.has("settle"):
            self.report.error("settle", "v1 key, not allowed in a replay spec (replay.play.warmup sets the pass warmup)")
        if s.timing.physics == "fine":
            self.report.error("timing.physics", "'fine' is not allowed in a replay spec: the record pass runs game physics")

    def replay_record(self, rec: Node) -> None:
        self.one_of(RR + "clock", rec.clock, "Clocks")
        if rec.clock == "fixed" and rec.rate not in self.replay_const("FixedRates"):
            self.report.error(RR + "rate", f"clock 'fixed' needs rate 60, 120 or 240 (got {rec.rate})")
        if rec.clock == "real" and rec.has("rate"):
            self.report.note(RR + "rate", "ignored with clock 'real' (one sample per rendered frame at 60 Hz)")
        x, y = rec.res
        if (x > 0) != (y > 0) or x < 0 or y < 0:
            self.report.error(RR + "res", "[width, height] or [0, 0] expected")
        elif x > MAX_RECORD_W or y > MAX_RECORD_H:
            self.report.warn(RR + "res", f"{fnum(x)}x{fnum(y)} is above {MAX_RECORD_W}x{MAX_RECORD_H}: the record pass may fall below real time")
        if not rec.roots:
            self.report.error(RR + "roots", 'at least one root (default ["auto"])')
        self.one_of(RR + "particles", rec.particles, "RecordParticles")
        if rec.particles == "list" and not rec.particleSystems:
            self.report.warn(RR + "particleSystems", "particles 'list' without particleSystems: no particles recorded")
        self.one_of(RR + "forceCullers", rec.forceCullers, "ForceCullers")
        if rec.forceCullers == "list" and not rec.cullers:
            self.report.warn(RR + "cullers", "forceCullers 'list' without cullers: no culler forced")
        for k in ("posEps", "rotEps", "cut"):
            if not rec.v[k] > 0:
                self.report.error(RR + k, "must be > 0")
        for k in ("rescanEvery", "captureEvery"):
            if rec.v[k] < 1:
                self.report.error(RR + k, ">= 1 required")
        if rec.box is not None and min(rec.box.size) <= 0:
            self.report.error(RR + "box.size", "every size component must be > 0")
        if rec.keepSpawned:
            self.report.note(RR + "keepSpawned", "P2, not built yet: spawned objects destroyed during the record stay hidden in a fresh session")
        if rec.post:
            self.report.note(RR + "post", "P1, not built yet: the D11PDPostCombined values are not recorded")
        if rec.settleFrames < 0:
            self.report.error(RR + "settleFrames", ">= 0 required")
        elif rec.settleFrames > 600:
            self.report.warn(RR + "settleFrames", f"{rec.settleFrames} frames is a long wait before sample 0")
        for i, raw in enumerate(rec.blocks):
            if not block_entry_ok(raw):
                self.report.error(f"{RR}blocks[{i}]", f"'{raw}' is not <path>|<property>[:float],...")
        for i, raw in enumerate(rec.checks):
            why = check_text_ok(raw)
            if why:
                self.report.error(f"{RR}checks[{i}]", why)

    def replay_play(self, play: Node) -> None:
        s = self.spec
        for k, name in (("look", "Looks"), ("order", "Orders"), ("interp", "Interps"), ("particles", "PlayParticles"),
                        ("cullers", "Cullers"), ("shadows", "Shadows"), ("activate", "Activates"), ("realTime", "RealTimes"),
                        ("restore", "Restores")):
            self.one_of(RP + k, play.v[k], name)
        mb = play.motionBlur or ""
        if mb not in self.replay_const("MotionBlurModes"):
            v = cs_double(mb)
            if v is None or not math.isfinite(v):
                self.report.error(RP + "motionBlur", f"'game', 'angle', 'off' or a strength number expected (got '{mb}')")
            elif v < 0:
                self.report.error(RP + "motionBlur", "strength must be >= 0")
            elif v > BLUR_CLAMP:
                self.report.warn(RP + "motionBlur", f"strength {fnum(v)} > {fnum(BLUR_CLAMP)}: the shader clamp is unknown (R6)")
        elif mb == "game":
            for g in s.timing.segments:
                if not g.speed > 0 or g.samples > 1:
                    continue
                strength = 0.8 * (1 / 60) * self.fps / g.speed
                if strength > BLUR_CLAMP + 1e-6:
                    self.report.warn(RP + "motionBlur", f"'game' gives strength {strength:.2f} at speed {fnum(g.speed)} "
                                                        f"(above {fnum(BLUR_CLAMP)}: the shader clamp is unknown, R6)")
                    break
        if not 0 < play.shutterAngle <= 360:
            self.report.error(RP + "shutterAngle", "must be in (0, 360] degrees")
        if play.warmup < 0:
            self.report.error(RP + "warmup", ">= 0 required")
        elif play.warmup == 0:
            self.report.warn(RP + "warmup", "0: no TAA reset at pass start, the previous pass's history leaks into the first frames")
        if play.ramp < 0:
            self.report.error(RP + "ramp", ">= 0 required")
        if not 0 <= play.maxUnresolved <= 1:
            self.report.error(RP + "maxUnresolved", "fraction in [0, 1] expected")
        if isinstance(play.activateList, dict):
            for k, v in play.activateList.items():
                if not isinstance(v, str) or v not in self.replay_const("ActivateListModes"):
                    self.report.error(f"{RP}activateList.{k}", "'quiet' or 'normal' expected")
        if play.has("range"):
            a, b = play.range
            if a < 0 or b > s.seconds + 1e-6 or b <= a:
                self.report.error(RP + "range", f"[from, to] with 0 <= from < to <= seconds ({fnum(s.seconds, 2)}) expected")
        names = {c.name: c for c in self.cams}
        for i, n in enumerate(play.cams):
            c = names.get(n)
            if c is None:
                self.report.error(f"{RP}cams[{i}]", f"unknown camera '{n}'")
            elif not c.eff.enabled:
                self.report.warn(f"{RP}cams[{i}]", f"camera '{n}' is disabled")
        for i, n in enumerate(play.coverageOk):
            if n not in names:
                self.report.warn(f"{RP}coverageOk[{i}]", f"unknown camera '{n}'")
        if play.cullers == "live":
            self.report.warn(RP + "cullers", "'live' is a diagnostic: cullers SetActive per camera and contaminate the passes (R4, R10 fail)")
        if play.cullers == "recorded":
            other = next((c for c in self.cams if c.eff.enabled and c.mode != "game"), None)
            if other is not None:
                self.report.warn(RP + "cullers", "'recorded' applies the game camera's culling to every camera: groups the game camera culled "
                                                 f"stay hidden in '{other.name}' (use it for the golden test)")
        if play.look == "clean":
            self.report.warn(RP + "look", "'clean' is not built yet (P1): mcam replays with look 'game'")
            costly = next((c for c in self.cams if c.eff.enabled and c.render.samples > COSTLY_SAMPLES), None)
            if costly is not None:
                self.report.warn(self.w(costly.where or f"cameras.{costly.name}", "render.samples"),
                                 f"look 'clean' with {costly.render.samples} TAA samples per sub-frame (cost: warmup + samples renders per sub-frame)")
        if play.order == "step":
            self.report.warn(RP + "order", "'step' is not built yet (P1): mcam replays with order 'camera'")
        if play.particles == "match":
            self.report.note(RP + "particles", "'match' is P1 (REPLAY_DESIGN 2.4.5)")
        if play.shadows in ("dirty", "dynamic"):
            self.report.note(RP + "shadows", f"'{play.shadows}' is a diagnostic until lab test L9b (REPLAY_DESIGN 2.4.6)")
        if play.setup is not None:
            self.lint_setup(play.setup, RP + "setup")
            for i, cmd in enumerate(play.setup):
                a = split_command(cmd)
                if len(a) >= 2 and a[0].lower() == "waitfor" and a[1].lower() == "event":
                    self.report.error(f"{RP}setup[{i}]", "waitfor loaded | streaming | savepoint <name> (no events in a replay setup)")

    def replay_audio(self, rec: Node, au: Node) -> None:
        self.one_of(RA + "capture", au.capture, "AudioCaptures")
        self.one_of(RA + "suspend", au.suspend, "Suspends")
        self.one_of(RA + "onHold", au.onHold, "OnHolds")
        cap = au.capture == "record"
        if cap and rec.clock == "fixed" and rec.rate != 60:
            self.report.error(RA + "capture", f"'record' with clock 'fixed' needs rate 60 (got {rec.rate})")
        if cap and au.suspend == "off" and not au.allowMuterOnly:
            self.report.error(RA + "capture", "'record' with suspend 'off' needs allowMuterOnly true (and the S2 flag): no guarded capture path otherwise")
        if cap:
            self.report.warn(RA + "capture", "'record' is refused by AudioCapture.Preflight until a capture path has passed "
                                             "(only with the muted output, acap.output muted: S3b or S2, user consent first; "
                                             "REPLAY_DESIGN 6.0 G3); the default audible output needs no flag")
        if cap and rec.clock == "fixed":
            self.report.note(RA + "capture", "clock 'fixed' 60: the take's audio is usable only if the drift check passes (6.2)")
        if au.suspend == "on":
            self.report.warn(RA + "suspend", "'on' is a diagnostic: suspended capture renders at 0.919x real time (L5), the slope check refuses that audio")
        if au.onHold == "mark":
            self.report.warn(RA + "onHold", "'mark' continues through holds: the wall-time gap goes to audio_sync.csv and is cut by mcam_audio.py track")
        if not 0 <= au.level <= 100:
            self.report.error(RA + "level", "0..100 expected")
        if au.markerEvery < 1:
            self.report.error(RA + "markerEvery", ">= 1 required")
        if not au.maxHeartbeatAge > 0:
            self.report.error(RA + "maxHeartbeatAge", "must be > 0")
        elif au.maxHeartbeatAge > 2:
            self.report.warn(RA + "maxHeartbeatAge", f"{fnum(au.maxHeartbeatAge)} s lets an older muter heartbeat pass the G2 preflight and the G5 "
                                                     "watchdog (design value 2 s)")
        if cap and not au.heartbeat:
            self.report.error(RA + "heartbeat", "InsideMuter heartbeat file required for a capture")
        if au.allowMuterOnly:
            self.report.warn(RA + "allowMuterOnly", "muter-only capture: InsideMuter is the only guard; Preflight refuses it until lab stage L5 S2 "
                                                    "has passed (_mod/acap_muteronly_ok.json, user consent first)")
        if not au.keepSuspended:
            self.report.warn(RA + "keepSuspended", "false: Wwise wakes from Suspend after the capture; G6 allows a wake only with the rtpc forced to 0")

    def replay_v2_fields(self, play: Node) -> None:
        s = self.spec
        if s.session.has("vsync"):
            self.report.note("session.vsync", "ignored in replay modes: the record pass sets its own 60 Hz pacing (2.3.4)")
        if s.stream.has("addDuringTake"):
            self.report.note("stream.addDuringTake", "no effect in replay passes: streaming is frozen (2.4.2)")
        if s.interpolate:
            self.report.note("interpolate", "record pass only; replay passes use interpolation None")
        if s.rehearsal:
            self.report.note("rehearsal", "ignored in replay passes: event times come from the recording")
        for e in s.events:
            if e.has("expect"):
                self.report.note(self.w(e.where or f"events.{e.name}", "expect"),
                                 "ignored in replay passes: event times come from the recording (the offline estimate uses it)")
        if play.look == "game" and any(c.eff.enabled and any(c.render.has(k) for k in ("warmup", "samples", "taa")) for c in self.cams):
            self.report.note(RP + "look", "'game': one render per step, render warmup/samples/taa of the cameras are not used "
                                          "(only render.motionBlur < 0 keeps the game value)")

    def replay_sound_setup(self) -> None:
        cmds = [" ".join(split_command(c)).lower() for c in self.spec.setup]
        first_load = next((i for i, c in enumerate(cmds) if c.startswith("load ")), len(cmds))
        if RTPC_ZERO in cmds[first_load:] and RTPC_ZERO not in cmds[:first_load]:
            self.report.warn("setup", f"'{RTPC_ZERO}' should come before the first load (optional silent take, REPLAY_DESIGN 6.0 G4): "
                                      "the AudioCapture watchdog only starts with the session")

    @staticmethod
    def has_look(e: Node) -> bool:
        lk = e.look
        return lk is not None and lk.v.get("of") != "none" and not (lk.v.get("of") == "self" and e.target is None)

    @staticmethod
    def fixed_look(r: Node | None, look_offset: tuple) -> tuple | None:
        if r is None or not r.has("pos"):
            return None
        off = r.v.get("offset") or (0.0, 0.0, 0.0)
        return tuple(p + o + q for p, o, q in zip(r.pos, off, look_offset))

    def side(self, where: str, d: tuple, coverage_ok: bool) -> bool:
        n = math.sqrt(sum(x * x for x in d))
        if n < 1e-9:
            return False
        pitch = -math.degrees(math.asin(max(-1.0, min(1.0, d[1] / n))))
        ang = math.degrees(math.acos(max(-1.0, min(1.0, d[2] / n))))
        if ang <= SIDE_ANGLE + 1e-6:
            return False
        top = pitch > TOP_DOWN_PITCH
        if top and coverage_ok:
            return False
        if top:
            self.report.warn(where, f"top-down view {fnum(ang, 1)} deg from +z (pitch {fnum(pitch, 1)}): check the floor coverage with a still, "
                                    "then list the camera in replay.play.coverageOk")
        else:
            self.report.warn(where, f"forward {fnum(ang, 1)} deg from +z (more than {fnum(SIDE_ANGLE)}): the side of the set shows "
                                    "(sets have no back side; cullers 'force' makes groups visible but creates no geometry)")
        return True

    def replay_sides(self, play: Node) -> None:
        covered = set(play.coverageOk)
        for c in self.cams:
            e = c.eff
            if not e.playerSide or c.mode not in ("static", "keys", "rail", "orbit"):
                continue
            w = c.where or f"cameras.{c.name}"
            ok = c.name in covered
            look = e.look
            own = (e.target if look is not None and look.v.get("of") == "self" else look)
            if c.mode == "static":
                if not self.has_look(e):
                    self.side(w, euler_forward(e.yaw, e.pitch), ok)
                else:
                    a = self.fixed_look(own, e.lookOffset)
                    if a is not None:
                        self.side(self.w(w, "look"), tuple(p - q for p, q in zip(a, e.pos)), ok)
            elif c.mode == "keys":
                for i, k in enumerate(e.keys):
                    kw = f"{w}.keys[{i}]"
                    key_look = k.look.v.get("of") != "none" if k.look is not None else self.has_look(e)
                    if not key_look:
                        self.side(kw, euler_forward(k.yaw, k.pitch), ok)
                        continue
                    if k.look is not None:
                        a = self.fixed_look(e.target if k.look.v.get("of") == "self" else k.look, k.lookOffset)
                    else:
                        a = self.fixed_look(own, e.lookOffset)
                    if a is not None:
                        self.side(kw, tuple(p - q for p, q in zip(a, k.pos)), ok)
            elif c.mode == "rail":
                if not self.has_look(e):
                    self.side(w, euler_forward(e.yaw, e.pitch), ok)
                    continue
                a = self.fixed_look(own, e.lookOffset)
                for i, p in enumerate(e.path if a is not None else []):
                    if self.side(f"{w}.path[{i}]", tuple(x - y for x, y in zip(a, p)), ok):
                        break
            else:
                if look is not None and look.v.get("of") == "none":
                    self.side(w, euler_forward(e.yaw, e.pitch), ok)
                    continue
                for i in range(5):
                    az = math.radians(e.azimuth[0] + (e.azimuth[1] - e.azimuth[0]) * i / 4)
                    ring = (math.sin(az) * e.radius, e.height, -math.cos(az) * e.radius)
                    if look is None or look.v.get("of") == "self":
                        d = tuple(p - q for p, q in zip(e.lookOffset, ring))
                    else:
                        a = self.fixed_look(look, e.lookOffset)
                        t = e.target
                        if a is None or t is None or not t.has("pos"):
                            break
                        toff = t.v.get("offset") or (0.0, 0.0, 0.0)
                        d = tuple(x - (p + o + q + r) for x, p, o, q, r in zip(a, t.pos, toff, e.offset, ring))
                    if self.side(self.w(w, "azimuth"), d, ok):
                        break

    def replay_line(self) -> str:
        rs, rec, play, au = self.replay_nodes()
        parts = [f"replay: source {rs.source or '<out>/record'}",
                 f"record {rec.clock}{' ' + str(rec.rate) if rec.clock == 'fixed' else ''} {fnum(rec.res[0])}x{fnum(rec.res[1])} "
                 f"cullers {rec.forceCullers}{f' captureGame/{rec.captureEvery}' if rec.captureGame else ''}",
                 f"play {play.look} {play.order} warmup {play.warmup} blur {play.motionBlur} cullers {play.cullers}"
                 f"{f' ramp {fnum(play.ramp)}' if play.ramp > 0 else ''}",
                 f"audio {au.capture}{f' suspend {au.suspend} level {fnum(au.level)}' if au.capture == 'record' else ''}"]
        return "  ".join(parts)

    # -------------------------------------------------------------------------------- compose (offline subset of 7.2-7.5)

    def lint_compose(self) -> None:
        comp = self.spec.compose
        if not isinstance(comp, dict):
            self.report.note("compose", "no compose section: the compositor needs --compose or command-line options")
            return
        for k in comp:
            if k not in COMPOSE_KEYS and not k.startswith("_") and k != "comment":
                self.report.note(f"compose.{k}", "not a compose key of the design (7.3); the compositor decides")
        fps = comp.get("fps")
        if fps is not None and (not is_num(fps) or abs(float(fps) - self.fps) > 1e-6):
            self.report.warn("compose.fps", f"{fps} differs from timing.fps {fnum(self.fps)}: the compositor resamples")
        names = {c.name for c in self.cams}
        disabled = {c.name for c in self.cams if not c.eff.enabled}
        self.result.tables["layouts"] = []
        layouts = comp.get("layouts", [])
        if not isinstance(layouts, list):
            self.report.error("compose.layouts", "list expected")
            layouts = []
        for li, lay in enumerate(layouts):
            self.lint_layout(lay, f"compose.layouts[{li}]", names, disabled)
        edits = comp.get("edit")
        if edits is not None:
            for ei, edit in enumerate(edits if isinstance(edits, list) else [edits]):
                self.lint_edit(edit, "compose.edit" if not isinstance(edits, list) else f"compose.edit[{ei}]", names, disabled)
        for section in ("grade", "labels", "angles"):
            self.lint_compose_cams(comp.get(section), f"compose.{section}", names)
        enc = comp.get("encode")
        if isinstance(enc, dict):
            codec = enc.get("codec", "nvenc")
            if codec not in ("x265", "nvenc"):
                self.report.error("compose.encode.codec", "H.265 only: 'nvenc' (default, hevc_nvenc Main10) or 'x265' (libx265, only on request)")
            if enc.get("bits", 10) not in (8, 10):
                self.report.error("compose.encode.bits", "8 or 10")
            crf = enc.get("crf", 18)
            if not is_num(crf) or not 0 <= crf <= 51:
                self.report.error("compose.encode.crf", "0..51")
            elif codec == "x265" and not 18 <= crf <= 22:
                self.report.note("compose.encode.crf", "the final masters use CRF 18-22")
            cq = enc.get("cq", 18)
            if not is_num(cq) or not 0 <= cq <= 51:
                self.report.error("compose.encode.cq", "0..51")
            if codec == "x265":
                self.report.note("compose.encode.codec", "x265 final encodes run at 1.4-5 fps at 2560x1440 (R12: 65 min for one take); nvenc is the default")

    def compose_cam_ok(self, cam: Any, where: str, names: set[str], disabled: set[str]) -> bool:
        if not isinstance(cam, str) or not cam:
            self.report.error(where, "camera name expected")
            return False
        if cam == "cut":
            return True
        if cam not in names:
            self.report.error(where, f"unknown camera '{cam}'")
            return False
        if cam in disabled:
            self.report.error(where, f"camera '{cam}' is disabled (enabled false): it has no frames")
            return False
        return True

    def lint_compose_cams(self, sec: Any, where: str, names: set[str]) -> None:
        if not isinstance(sec, dict):
            return
        for key in ("cams", "names"):
            v = sec.get(key)
            if isinstance(v, dict):
                for k in v:
                    if k not in names and k not in ("cut", "default"):
                        self.report.warn(f"{where}.{key}.{k}", f"unknown camera '{k}'")
            elif isinstance(v, list):
                for i, k in enumerate(v):
                    if k not in names and k != "cut":
                        self.report.warn(f"{where}.{key}[{i}]", f"unknown camera '{k}'")
        auto = sec.get("auto")
        if isinstance(auto, dict):
            self.lint_compose_cams(auto, f"{where}.auto", names)

    def lint_layout(self, lay: Any, where: str, names: set[str], disabled: set[str]) -> None:
        if not isinstance(lay, dict):
            self.report.error(where, "layout object expected")
            return
        W, g = lay.get("width", CAPTURE_W), lay.get("gap", 4)
        if not is_num(W) or W <= 0 or int(W) % 2:
            self.report.error(f"{where}.width", "even positive width expected")
            return
        if not is_num(g) or g < 0:
            self.report.error(f"{where}.gap", "gap must be >= 0")
            return
        W, g = int(W), int(g)
        preset = lay.get("preset")
        if preset is not None and preset not in PRESETS:
            self.report.error(f"{where}.preset", f"unknown preset '{preset}' ({', '.join(PRESETS)})")
            return
        rows = lay.get("rows")
        if not rows and preset and isinstance(lay.get("cams"), list) and lay["cams"]:
            rows = [[{"cam": lay["cams"][0]}], [{"cam": c} for c in lay["cams"][1:]]]
        if not isinstance(rows, list) or not rows:
            self.report.error(f"{where}.rows", "rows: [[cell, ...], ...] expected")
            return
        heights_map = lay.get("heights") if isinstance(lay.get("heights"), dict) else {}
        y, desc = 0, []
        for ri, row in enumerate(rows):
            rw = f"{where}.rows[{ri}]"
            row = [row] if isinstance(row, dict) else row
            if not isinstance(row, list) or not row or not all(isinstance(c, dict) for c in row):
                self.report.error(rw, "a row is a non-empty list of cell objects")
                return
            for ci, cell in enumerate(row):
                self.compose_cam_ok(cell.get("cam"), f"{rw}[{ci}].cam", names, disabled)
                crop = cell.get("crop")
                if crop is not None:
                    if not (isinstance(crop, list) and len(crop) == 4 and all(is_num(x) for x in crop)) or crop[2] <= 0 or crop[3] <= 0:
                        self.report.error(f"{rw}[{ci}].crop", "crop is [x, y, w, h] in source pixels")
                        return
                    x, cy, cw, ch = crop
                    if x < 0 or cy < 0 or x + cw > self.res[0] or cy + ch > self.res[1]:
                        self.report.error(f"{rw}[{ci}].crop", f"crop {crop} is outside the {self.res[0]}x{self.res[1]} capture")
                pan = cell.get("pan")
                if pan is not None and not (isinstance(pan, list) and all(isinstance(k, list) and len(k) == 3 and all(is_num(q) for q in k) for k in pan)):
                    self.report.error(f"{rw}[{ci}].pan", "pan is [[game_t, x, y], ...]")
                if cell.get("height") is not None and (not is_num(cell["height"]) or cell["height"] <= 0):
                    self.report.error(f"{rw}[{ci}].height", "height must be > 0")
                    return
            first = row[0]
            if len(row) == 1 and (first.get("crop") is not None or (preset and ri == 0)):
                crop = first.get("crop") or [0, 0, self.res[0], self.res[1]]
                h = PRESETS[preset][0] if preset and ri == 0 else even(crop[3] * W / crop[2])
                if first.get("height"):
                    h = even(float(first["height"]))
                desc.append(f"{first.get('cam')} band {W}x{h}" + (f" from crop {crop}" if first.get("crop") is not None else ""))
            else:
                n = len(row)
                inner = W - (n - 1) * g
                widths = [inner // n + (1 if i < inner % n else 0) for i in range(n)]
                if preset and ri == 1 and n == PRESETS[preset][1]:
                    h = PRESETS[preset][2]
                else:
                    given = next((c.get("height") for c in row if c.get("height")), None) or heights_map.get(str(ri))
                    h = even(float(given)) if is_num(given) else 2 * math.ceil(max(widths) * 9 / 32)
                if min(widths) <= 0:
                    self.report.error(rw, f"{n} cells do not fit into width {W}")
                    return
                desc.append(f"{n} cells {'/'.join(map(str, widths))} x {h}")
            y += h + (g if ri < len(rows) - 1 else 0)
        total = y if y % 2 == 0 else y + 1
        name = lay.get("name", f"layout{len(self.result.tables['layouts'])}")
        self.result.tables["layouts"].append({"name": name, "size": [W, total], "rows": desc})

    def warp_out_time(self, speed: list, fps: float) -> Callable[[float], float]:
        keys = sorted((float(k[0]), float(k[1])) for k in speed)

        def sp(t: float) -> float:
            if t <= keys[0][0]:
                return keys[0][1]
            for (t0, s0), (t1, s1) in zip(keys, keys[1:]):
                if t0 <= t < t1:
                    return s0 + (s1 - s0) * smoothstep((t - t0) / (t1 - t0))
            return keys[-1][1]

        def f(t: float) -> float:
            T, k = 0.0, 0
            while T < t - 1e-9 and k < 10_000_000:
                T += max(1e-4, sp(T)) / fps
                k += 1
            return k / fps
        return f

    def lint_edit(self, edit: Any, where: str, names: set[str], disabled: set[str]) -> None:
        if not isinstance(edit, dict):
            self.report.error(where, "edit object expected")
            return
        shots = edit.get("shots")
        if not isinstance(shots, list) or not shots:
            self.report.error(f"{where}.shots", "edit needs a non-empty shots list")
            return
        fps = float(self.spec.compose.get("fps", self.fps)) if is_num(self.spec.compose.get("fps")) else self.fps
        speed = edit.get("speed")
        mapping = self.out_time
        mode = "meta timeline"
        if speed is not None:
            if isinstance(speed, list) and speed and all(isinstance(k, list) and len(k) == 2 and all(is_num(q) and (j == 0 or q > 0) for j, q in enumerate(k)) for k in speed):
                mapping = self.warp_out_time(speed, fps)
                mode = "warp (edit.speed)"
                if any(g.s_eff and g.s_eff < 1 for g in self.segments):
                    self.report.warn(f"{where}.speed", "edit.speed on a take with in-engine slow motion: the warp re-times captured steps a second time")
            else:
                self.report.error(f"{where}.speed", "speed is [[game_t, speed > 0], ...]")
        min_hold = float(edit.get("minHold", 4.0)) if is_num(edit.get("minHold", 4.0)) else 4.0
        max_hold = float(edit.get("maxHold", 6.5)) if is_num(edit.get("maxHold", 6.5)) else 6.5
        for k in ("fadeIn", "fadeOut"):
            if k in edit and (not is_num(edit[k]) or edit[k] < 0):
                self.report.error(f"{where}.{k}", "seconds >= 0 expected")
        rows: list[dict] = []
        for i, sh in enumerate(shots):
            sw = f"{where}.shots[{i}]"
            if not isinstance(sh, dict):
                self.report.error(sw, "shot object expected")
                continue
            self.compose_cam_ok(sh.get("cam"), f"{sw}.cam", names, disabled)
            t = self.compose_time(sh.get("from", 0), f"{sw}.from")
            tr = sh.get("in") or {"type": "cut"}
            tr = {"type": tr} if isinstance(tr, str) else tr
            ttype, dur = "cut", 0.0
            if isinstance(tr, dict):
                ttype = tr.get("type", "cut")
                if ttype not in TRANSITIONS:
                    self.report.error(f"{sw}.in.type", f"'{ttype}' is not one of {sorted(TRANSITIONS)}")
                dur = tr.get("dur", 0.0 if ttype == "cut" else 1.0)
                if not is_num(dur) or dur < 0:
                    self.report.error(f"{sw}.in.dur", "seconds >= 0 expected")
                    dur = 0.0
                if tr.get("align", "center") not in ("center", "start", "end"):
                    self.report.error(f"{sw}.in.align", "center | start | end")
            elif tr is not None:
                self.report.error(f"{sw}.in", "transition expected: \"dissolve\" or {type, dur, align}")
            rp = sh.get("replay")
            if rp is not None and not (isinstance(rp, list) and len(rp) == 2 and all(is_num(q) for q in rp) and rp[1] > rp[0]):
                self.report.error(f"{sw}.replay", "replay is [t0, t1] game seconds with t1 > t0")
            to = self.compose_time(sh["to"], f"{sw}.to") if "to" in sh else None
            rows.append({"i": i, "cam": sh.get("cam"), "from": sh.get("from", 0), "t": t, "to": to, "type": ttype, "dur": float(dur)})
        timed = [r for r in rows if r["t"] is not None]
        for a, b in zip(timed, timed[1:]):
            if b["t"] < a["t"] - 1e-9:
                self.report.warn(f"{where}.shots[{b['i']}].from", "shots are not in time order")
        timed.sort(key=lambda r: r["t"])
        end_o = mapping(self.end_t)
        fade_out = float(edit.get("fadeOut", 0)) if is_num(edit.get("fadeOut", 0)) else 0.0
        for k, r in enumerate(timed):
            r["o"] = mapping(r["t"])
            stop = r["to"] if r["to"] is not None else (timed[k + 1]["t"] if k + 1 < len(timed) else self.end_t)
            r["hold"] = mapping(stop) - r["o"]
            last = k + 1 == len(timed)
            if r["hold"] < min_hold - 1e-6 or r["hold"] > max_hold + 1e-6:
                self.report.warn(f"{where}.shots[{r['i']}]", f"'{r['cam']}' holds {fnum(r['hold'], 2)} s, outside [{fnum(min_hold)}, {fnum(max_hold)}]"
                                                            + (" (to the take end)" if last else ""))
            if r["type"] != "cut" and r["dur"] > 0.4 * max(r["hold"], 1e-6):
                self.report.warn(f"{where}.shots[{r['i']}].in.dur", f"{fnum(r['dur'])} s transition is longer than 40 % of the {fnum(r['hold'], 2)} s shot")
            if k == 0 and r["t"] > 1e-9:
                self.report.note(f"{where}.shots[{r['i']}].from", "the cut starts after t=0")
        if fade_out and timed and fade_out > timed[-1].get("hold", 0):
            self.report.warn(f"{where}.fadeOut", "the fade-out is longer than the last shot")
        self.result.tables.setdefault("edits", []).append({"name": edit.get("name", "cut"), "mode": mode, "duration": end_o,
                                                           "min": min_hold, "max": max_hold, "rows": timed})

    def compose_time(self, v: Any, where: str) -> float | None:
        if is_num(v):
            return float(v)
        if isinstance(v, str):
            ok, ev, off = parse_time(v)
            if ok:
                return self.resolve(TimeVal(ev, off), None)
        self.report.error(where, 'time expected: game seconds or "@event+s"')
        return None

    # -------------------------------------------------------------------------------- estimate

    def director_rendered(self, c: CamInfo) -> list[tuple[float, float]] | None:
        if c.raw.has("post") or c.raw.has("render"):
            return None
        shots = sorted(((self.resolve(s.v["from"], None), s) for s in c.eff.shots), key=lambda x: (x[0] is None, x[0] or 0))
        shots = [(t, s) for t, s in shots if t is not None]
        windows: list[tuple[float, float]] = []
        for k, (t, s) in enumerate(shots):
            nxt = shots[k + 1][0] if k + 1 < len(shots) else self.end_t
            src = self.cam(s.cam)
            if src is None or not src.eff.enabled:
                windows.append((t, nxt))
                continue
            if k == 0 or s.transition == "cut" or s.blend <= 0:
                continue
            b = s.blend * self.speed_at(t) if s.clock == "output" else s.blend
            windows.append((t, min(nxt, t + b)))
        if shots and shots[0][0] > 0:
            windows.append((0.0, shots[0][0]))
        return windows

    def captured_between(self, a: float, b: float) -> float:
        n = 0.0
        for p in self.pieces:
            lo, hi = max(a, p.a), min(b, p.b)
            if hi > lo and p.seg.R and p.seg.N:
                n += (hi - lo) * p.seg.R * p.seg.M / p.seg.N
        return n

    def estimate(self) -> None:
        tab = self.result.tables
        seg_rows = []
        for g in self.segments:
            span = [p for p in self.pieces if p.seg is g]
            d = sum(p.d for p in span)
            steps = sum(p.d * (g.R or 0) for p in span)
            captured = sum(p.d * (g.R or 0) * (g.M or 0) / (g.N or 1) for p in span)
            seg_rows.append({"index": g.index, "label": g.label, "from": g.start.text(), "start": g.start_t, "speed": g.s_eff,
                             "M": g.M, "shutter": g.shutter if (g.M or 1) > 1 and not g.legacy and not g.rate else None,
                             "R": g.R, "N": g.N, "game_s": d, "out_s": d / (g.s_eff or 1), "steps": steps, "captured": captured})
        tab["segments"] = seg_rows
        steps = sum(r["steps"] for r in seg_rows)
        captured = sum(r["captured"] for r in seg_rows)
        first = next((g for g in self.segments if g.R), None)
        pre_steps = float(self.spec.preroll) * (first.R if first else 30.0)
        cam_rows = []
        renders = 0.0
        images = 0.0
        for c in self.cams:
            wn = c.render.warmup + c.render.samples
            if not c.eff.enabled or c.mode not in self.schema.modes:
                cam_rows.append({"name": c.name, "mode": c.mode, "budget": f"{c.render.warmup}+{c.render.samples}", "visits": 0, "renders": 0,
                                 "note": "disabled" if not c.eff.enabled else "unknown mode: not visited"})
                continue
            visits, note = captured, ""
            if c.mode == "director":
                win = self.director_rendered(c)
                if win is not None:
                    visits = sum(self.captured_between(a, b) for a, b in win)
                    note = f"copies while holding ({fnum(100 * (1 - visits / captured) if captured else 0, 0)} % of frames)"
                else:
                    note = "own post/render: visited every step"
            renders += visits * wn
            images += captured
            cam_rows.append({"name": c.name, "mode": c.mode, "budget": f"{c.render.warmup}+{c.render.samples}", "visits": visits,
                             "renders": visits * wn, "note": note})
        tab["cameras"] = cam_rows
        frames = steps + pre_steps + renders
        lo, hi = self.ms_range("25:40")
        px = self.res[0] * self.res[1]
        fmt = self.output.format
        per_image = JPEG_MB_1440P * px / (CAPTURE_W * CAPTURE_H) if fmt == "jpg" else px * 4 / 1e6 if fmt == "raw" else 0.0
        out_s = sum(r["out_s"] for r in seg_rows)
        tab["estimate"] = {"game_s": self.end_t, "output_s": out_s, "frames_out": out_s * self.fps, "steps": steps,
                           "preroll_steps": pre_steps, "captured_steps": captured, "renders": renders, "engine_frames": frames,
                           "real_s": [frames * lo / 1000, frames * hi / 1000], "ms": [lo, hi], "images": images,
                           "disk_gb": images * per_image / 1000, "format": fmt, "res": list(self.res)}
        tab["events"] = [{"name": e.name, "type": e.type, "predicted": self.predicted.get(e.name, (None, ""))[0],
                          "source": self.predicted.get(e.name, (None, ""))[1], "refs": len(self.refs.get(e.name, []))}
                         for e in self.spec.events]
        if images * per_image / 1000 > 50:
            self.report.warn("output", f"about {fnum(images * per_image / 1000, 1)} GB of frames: check the free disk space")
        if frames * hi / 1000 > 4 * 3600:
            self.report.warn("render", f"up to {fnum(frames * hi / 3600000, 1)} h of capture: lower samples or cameras for a draft")

    def ms_range(self, default: str) -> tuple[float, float]:
        ms = self.opts.ms or default
        lo, hi = (float(x) for x in ms.split(":")) if ":" in ms else (float(ms),) * 2
        return lo, hi

    def per_image_mb(self) -> float:
        px = self.res[0] * self.res[1]
        fmt = self.output.format
        return JPEG_MB_1440P * px / (CAPTURE_W * CAPTURE_H) if fmt == "jpg" else px * 4 / 1e6 if fmt == "raw" else 0.0

    def estimate_replay(self) -> None:
        tab = self.result.tables
        _, rec, play, _ = self.replay_nodes()
        seg_rows = []
        for g in self.segments:
            d = sum(p.d for p in self.pieces if p.seg is g)
            out_s = d / (g.s_eff or 1.0)
            seg_rows.append({"index": g.index, "label": g.label, "from": g.start.text(), "start": g.start_t, "speed": g.s_eff, "M": g.M,
                             "shutter": g.shutter if (g.M or 1) > 1 else None, "game_s": d, "out_s": out_s, "frames": out_s * self.fps,
                             "captured": out_s * self.fps * (g.M or 1)})
        tab["segments"] = seg_rows
        frames = sum(r["frames"] for r in seg_rows)
        captured = sum(r["captured"] for r in seg_rows)
        warm = max(0, play.warmup)
        cam_rows = []
        renders = images = 0.0
        for c in self.cams:
            if not c.eff.enabled or c.mode not in self.schema.modes:
                cam_rows.append({"name": c.name, "mode": c.mode, "renders": 0, "warmups": 0, "images": 0,
                                 "note": "disabled" if not c.eff.enabled else "unknown mode: no pass"})
                continue
            if play.cams and c.name not in play.cams:
                cam_rows.append({"name": c.name, "mode": c.mode, "renders": 0, "warmups": 0, "images": 0, "note": "not in replay.play.cams"})
                continue
            per = max(1, c.render.warmup) + max(1, c.render.samples) if play.look == "clean" else 1
            sub, runs, note = captured, 1, ""
            if c.mode == "director":
                win = self.director_rendered(c) if play.copyHolds else None
                if win is not None:
                    sub = sum(self.captured_between(a, b) for a, b in win)
                    runs = len(win)
                    note = f"copies while holding ({fnum(100 * (1 - sub / captured) if captured else 0, 0)} % of frames), {runs} rendered run(s) with warmup"
                else:
                    note = "renders every step" + ("" if play.copyHolds else " (copyHolds false)")
            r = sub * per + warm * runs
            renders += r
            images += captured
            cam_rows.append({"name": c.name, "mode": c.mode, "renders": r, "warmups": warm * runs, "images": captured, "note": note})
        tab["cameras"] = cam_rows
        lo, hi = self.ms_range(REPLAY_MS)
        record_s = self.end_t + float(self.spec.preroll)
        out_s = sum(r["out_s"] for r in seg_rows)
        per_image = self.per_image_mb()
        tab["replay"] = self.replay_line()
        tab["estimate"] = {"mode": "replay", "game_s": self.end_t, "record_s": record_s, "record_res": list(rec.res), "output_s": out_s,
                           "frames_out": frames, "captured_steps": captured, "renders": renders, "look": play.look, "warmup": warm,
                           "real_s": [record_s + renders * lo / 1000, record_s + renders * hi / 1000], "ms": [lo, hi], "images": images,
                           "disk_gb": images * per_image / 1000, "format": self.output.format, "res": list(self.res)}
        tab["events"] = [{"name": e.name, "type": e.type, "predicted": self.predicted.get(e.name, (None, ""))[0],
                          "source": self.predicted.get(e.name, (None, ""))[1], "refs": len(self.refs.get(e.name, []))}
                         for e in self.spec.events]
        if images * per_image / 1000 > 50:
            self.report.warn("output", f"about {fnum(images * per_image / 1000, 1)} GB of frames: check the free disk space")
        if renders * hi / 1000 > 4 * 3600:
            self.report.warn("render", f"up to {fnum(renders * hi / 3600000, 1)} h of replay passes: fewer cameras (replay.play.cams) for a draft")

    # -------------------------------------------------------------------------------- level index lookup

    def object_queries(self) -> dict[str, list[str]]:
        q: dict[str, list[str]] = {}

        def add(path: str, where: str) -> None:
            path = re.sub(r"\[\d+\]$", "", path.split(" box=")[0].split("::")[0]).strip()
            if path and not path.startswith("#") and path not in BASES:
                q.setdefault(path, []).append(where)

        def visit(node: Any, where: str) -> None:
            if isinstance(node, Node) and node.cls == "TargetRef" and node.v.get("of") and not node.has("pos"):
                add(node.of, where)
            if isinstance(node, Node) and node.cls == "GroupSpec" and node.list:
                add(node.list, self.w(where, "list"))
        walk(self.schema, self.spec, "", visit)
        for e in self.spec.events:
            if e.obj:
                add(e.obj, self.w(e.where, "obj"))
        for c in self.cams:
            for i, h in enumerate(c.eff.hide):
                add(h, f"{c.where}.hide[{i}]")
            for i, h in enumerate(c.eff.backfaces):
                add(h, f"{c.where}.backfaces[{i}]")
        cmds = [(f"setup[{i}]", s) for i, s in enumerate(self.spec.setup)]
        for i, cue in enumerate(self.spec.cues):
            cmds += [(cue.where or f"cues[{i}]", s) for s in ([cue.cmd] if cue.cmd else []) + list(cue.cmds)]
        for where, cmd in cmds:
            a = split_command(cmd)
            if len(a) > 1 and a[0].lower() in OBJECT_COMMANDS:
                add(a[1], where)
        return q

    def index_notes(self, found: dict[str, list[tuple[str, str]]], folder: Path) -> None:
        self.report.stage = "index"
        for q, wheres in self.object_queries().items():
            hits = found.get(q, [])
            where = wheres[0]
            levels = sorted({h[0] for h in hits})
            if not hits:
                self.report.warn(where, f"index: '{q}' not found in {folder.name} (runtime-created or a typo?)")
            elif len(hits) == 1:
                self.report.note(where, f"index: '{q}' -> {hits[0][0]} {hits[0][1]}")
            elif len(levels) < len(hits):
                self.report.warn(where, f"index: '{q}' matches {len(hits)} objects ({', '.join(levels)}); strict resolution fails "
                                        "when two of them are loaded: use a longer path or [i]")
            else:
                self.report.note(where, f"index: '{q}' matches one object in each of {', '.join(levels)} (only loaded levels count)")


def scan_index(folder: Path, queries: set[str], level_filter: str) -> dict[str, list[tuple[str, str]]]:
    lasts = {q.rsplit("/", 1)[-1] for q in queries}
    found: dict[str, list[tuple[str, str]]] = {q: [] for q in queries}
    rx = re.compile(r'^\{"rec": "gameobject", "file": "([^"]*)".*?, "path": "((?:[^"\\]|\\.)*)"')
    for f in sorted(folder.glob("*.jsonl")):
        if level_filter and level_filter.lower() not in f.name.lower():
            continue
        with f.open(encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if not line.startswith('{"rec": "gameobject"'):
                    continue
                m = rx.match(line)
                if not m:
                    continue
                path = json.loads(f'"{m.group(2)}"') if "\\" in m.group(2) else m.group(2)
                last = path.rsplit("/", 1)[-1]
                if last not in lasts:
                    continue
                for q in queries:
                    if path == q or path.endswith("/" + q) or ("/" not in q and last == q):
                        found[q].append((f.stem.split("__")[0], path))
    return found


def index_lookup(checks: list[Check], folder: Path, level_filter: str) -> None:
    live = [c for c in checks if c.spec is not None and not c.result.bad_input]
    queries = set().union(*(c.object_queries().keys() for c in live)) if live else set()
    if not queries:
        return
    if folder is None or not folder.is_dir():
        for c in live:
            c.report.add("warn", "index", f"level index folder not found: {folder} (give --index-dir or set INSIDE_LEVEL_INDEX)" if folder else
                         "no level index folder: give --index-dir or set INSIDE_LEVEL_INDEX", "index")
        return
    found = scan_index(folder, queries, level_filter)
    for c in live:
        c.index_notes(found, folder)


# ---------------------------------------------------------------------------------------------------- printing


def fdur(seconds: float) -> str:
    if seconds < 120:
        return f"{seconds:.0f} s"
    if seconds < 7200:
        return f"{seconds / 60:.0f} min"
    return f"{seconds / 3600:.1f} h"


def print_events(t: dict) -> None:
    if t.get("events"):
        print("  events:")
        for e in t["events"]:
            pred = f"{fnum(e['predicted'])} ({e['source']})" if e["predicted"] is not None else "no prediction"
            print(f"    {e['name']:<14} {e['type']:<9} {pred:<22} referenced {e['refs']}x")


def print_replay(t: dict) -> None:
    if t.get("replay"):
        print(f"  {t['replay']}")
    if "segments" in t:
        print("  segments (replay timeline, any float speed):")
        print(f"    {'#':>2}  {'label':<10} {'from':<16} {'start':>7} {'speed':>6} {'M':>2} {'shutter':>7} {'game s':>7} {'out s':>7} "
              f"{'frames':>7} {'renders':>8}")
        for s in t["segments"]:
            print(f"    {s['index']:>2}  {(s['label'] or '-')[:10]:<10} {s['from'][:16]:<16} {fnum(s['start']):>7} {fnum(s['speed']):>6} "
                  f"{s['M'] or '-':>2} {fnum(s['shutter'], 0) if s['shutter'] else '-':>7} {s['game_s']:>7.2f} {s['out_s']:>7.2f} "
                  f"{s['frames']:>7.0f} {s['captured']:>8.0f}")
    if "cameras" in t:
        print("  cameras (one continuous pass each):")
        for c in t["cameras"]:
            print(f"    {c['name'][:14]:<14} {c['mode']:<9} renders {c['renders']:>7.0f} (warmups {c['warmups']:>3.0f})  {c['note']}")
    if "estimate" in t:
        e = t["estimate"]
        print(f"  take: {e['game_s']:.2f} record s -> {e['output_s']:.2f} output s ({e['frames_out']:.0f} frames per camera, "
              f"{e['captured_steps']:.0f} renders per camera with sub-frames)")
        print(f"  record pass: {e['record_s']:.1f} s real time incl. preroll at {fnum(e['record_res'][0])}x{fnum(e['record_res'][1])} (plus setup)")
        print(f"  replay passes: {e['renders']:.0f} renders (look {e['look']}, warmup {e['warmup']} per pass and director run) -> with the record "
              f"pass {fdur(e['real_s'][0])} to {fdur(e['real_s'][1])} at {fnum(e['ms'][0])}-{fnum(e['ms'][1])} ms per render")
        if e["format"] == "pipe":
            print(f"  disk: {e['images']:.0f} frames into per-camera ffmpeg pipes")
        else:
            print(f"  disk: {e['images']:.0f} images ~ {e['disk_gb']:.1f} GB ({e['format']} at {e['res'][0]}x{e['res'][1]}) plus the recording")


def print_result(r: Result, quiet: bool) -> None:
    rep = r.report
    errs, warns, notes = rep.of("error"), rep.of("warn"), rep.of("note")
    print(f"{r.path}: {len(errs)} error(s), {len(warns)} warning(s)")
    for m in errs:
        print(f"  error: {m.line()}")
    for m in warns:
        print(f"  warn:  {m.line()}")
    if quiet:
        return
    for m in notes:
        print(f"  note:  {m.line()}")
    t = r.tables
    if r.replay:
        print_replay(t)
    elif "segments" in t:
        print("  segments:")
        print(f"    {'#':>2}  {'label':<10} {'from':<16} {'start':>7} {'speed':>6} {'M':>2} {'shutter':>7} {'R':>4} {'N':>3} "
              f"{'game s':>7} {'out s':>7} {'steps':>7} {'captured':>8}")
        for s in t["segments"]:
            print(f"    {s['index']:>2}  {(s['label'] or '-')[:10]:<10} {s['from'][:16]:<16} {fnum(s['start']):>7} {fnum(s['speed']):>6} "
                  f"{s['M'] or '-':>2} {fnum(s['shutter'], 0) if s['shutter'] else '-':>7} {fnum(s['R'], 0):>4} {s['N'] or '-':>3} "
                  f"{s['game_s']:>7.2f} {s['out_s']:>7.2f} {s['steps']:>7.0f} {s['captured']:>8.0f}")
    print_events(t)
    if "cameras" in t and not r.replay:
        print("  cameras (visit order):")
        for c in t["cameras"]:
            print(f"    {c['name'][:14]:<14} {c['mode']:<9} W+N {c['budget']:<5} visits {c['visits']:>7.0f}  renders {c['renders']:>8.0f}  {c['note']}")
    if "estimate" in t and not r.replay:
        e = t["estimate"]
        print(f"  take: {e['game_s']:.2f} game s -> {e['output_s']:.2f} output s ({e['frames_out']:.0f} frames); "
              f"steps {e['steps']:.0f} (captured {e['captured_steps']:.0f}, preroll {e['preroll_steps']:.0f})")
        print(f"  cost: {e['renders']:.0f} visit renders + {e['steps'] + e['preroll_steps']:.0f} step frames = {e['engine_frames']:.0f} frames "
              f"-> {fdur(e['real_s'][0])} to {fdur(e['real_s'][1])} at {fnum(e['ms'][0])}-{fnum(e['ms'][1])} ms per frame")
        if e["format"] == "pipe":
            print(f"  disk: {e['images']:.0f} frames into per-camera ffmpeg pipes")
        else:
            print(f"  disk: {e['images']:.0f} images ~ {e['disk_gb']:.1f} GB ({e['format']} at {e['res'][0]}x{e['res'][1]})")
    for lay in t.get("layouts", []):
        print(f"  layout '{lay['name']}': {lay['size'][0]}x{lay['size'][1]}  ({'; '.join(lay['rows'])})")
    for ed in t.get("edits", []):
        print(f"  edit '{ed['name']}' ({ed['mode']}, {ed['duration']:.2f} s, holds {fnum(ed['min'])}-{fnum(ed['max'])} s):")
        for row in ed["rows"]:
            print(f"    {row['i']:>2}  {str(row['cam'])[:14]:<14} from {str(row['from'])[:16]:<16} game {row['t']:>6.2f}  out {row['o']:>6.2f}  "
                  f"hold {row['hold']:>5.2f}  in {row['type']}{' ' + fnum(row['dur']) if row['type'] != 'cut' else ''}")


def result_json(r: Result) -> dict:
    rep = r.report
    return {"spec": str(r.path), "ok": rep.ok and not r.bad_input, "bad_input": r.bad_input, "replay": r.replay,
            "errors": [m.line() for m in rep.of("error")], "warnings": [m.line() for m in rep.of("warn")],
            "notes": [m.line() for m in rep.of("note")],
            "messages": [{"level": m.level, "where": m.where, "text": m.text, "stage": m.stage} for m in rep.msgs],
            "tables": r.tables}


# ---------------------------------------------------------------------------------------------------- schema markdown


SCHEMA_ORDER = ["MultiCamSpec", "TimingSpec", "SegmentSpec", "RateSpec", "CueSpec", "EventSpec", "RenderSpec", "OutputSpec",
                "SessionSpec", "PostSpec", "StreamSpec", "CamSpec", "KeySpec", "ShotSpec", "ShakeSpec", "ClampSpec", "PanSpec", "BeatSpec",
                "ViewSpec", "TargetRef", "GroupSpec",
                "GroupFilter", "ReplaySpec", "RecordSpec", "BoxSpec", "PlaySpec", "AudioCapSpec"]
SCHEMA_TITLES = {"MultiCamSpec": "Root", "TimingSpec": "timing", "SegmentSpec": "timing.segments[]", "RateSpec": "rates[] (v1)",
                 "CueSpec": "cues[]", "EventSpec": "events[]", "RenderSpec": "render (root, defaults.render, camera render)",
                 "OutputSpec": "output", "SessionSpec": "session", "PostSpec": "post (root, defaults.post, camera post)",
                 "StreamSpec": "stream", "CamSpec": "cameras[] and defaults", "KeySpec": "cameras[].keys[]",
                 "ShotSpec": "cameras[].shots[] (director)", "ShakeSpec": "cameras[].shake", "ClampSpec": "cameras[].clamp",
                 "PanSpec": "cameras[].pans[] (overview)", "BeatSpec": "cameras[].beats[] (events)", "ViewSpec": "cameras[].view",
                 "TargetRef": "target object form",
                 "GroupSpec": "target group", "GroupFilter": "group filter", "ReplaySpec": "replay", "RecordSpec": "replay.record",
                 "BoxSpec": "replay.record.box", "PlaySpec": "replay.play", "AudioCapSpec": "replay.audio"}
CLASS_SWITCH = re.compile(r"^`[\w.]+` \(`(\w+)`\):\s*$")
DEFAULT_TEXT = {"TargetRef.of": "null", "TargetRef.point": "null", "PlaySpec.range": "null"}
EXTRA_MEANINGS = {
    "RateSpec.from": "Game time the rate starts.", "RateSpec.rate": "Steps per game second (whole number).",
    "RateSpec.group": "Steps averaged into one output frame.",
    "CueSpec.at": "Game time or \"@event+s\".", "CueSpec.cmd": "Bridge command.", "CueSpec.cmds": "More commands at the same time.",
    "CueSpec.label": "Name in events.csv.",
    "KeySpec.t": "Seconds on the camera clock (origin `start`).", "KeySpec.pos": "Camera position.",
    "KeySpec.yaw": "Euler yaw (used without `look`).", "KeySpec.pitch": "Euler pitch, positive looks down.",
    "KeySpec.roll": "Roll degrees.", "KeySpec.fov": "FOV at this key; 0 = camera fov.", "KeySpec.look": "Look-at target at this key.",
    "KeySpec.lookOffset": "Added to the key look point.",
    "ShotSpec.cam": "Source camera (required).", "ShotSpec.from": "Shot start (game time or \"@event+s\").",
    "ShotSpec.blend": "Blend seconds into this shot (0 = cut).", "ShotSpec.ease": "`smooth`, `smoother`, `linear`, `sine`.",
    "ShotSpec.transition": "`orbit` (game interpolation), `linear`, `cut`.",
    "ShotSpec.clock": "`game` seconds or `output` seconds (blend x speed in game time).", "ShotSpec.label": "Name in events.csv.",
    "CamSpec.feedForward": "Damped target tracking (smooth / lookSmooth of follow, frame, overview, events and every look) aims at the subject plus its smoothed velocity x the damping time, so a moving subject has no steady lag (MULTICAM.md 6.6); false = the old plain damping. A given lead still adds on top.",
    "CamSpec.backfaces": "Object paths whose meshes this camera also draws from behind, in replay passes and `mcam show` only: each "
                         "MeshFilter / SkinnedMeshRenderer shows a two-sided copy (`<path> box=x0,y0,z0,x1,y1,z1` = only the triangles "
                         "inside); a director takes the source camera's list of the shot on air (MULTICAM.md 6.5).",
    "ShotSpec.hide": "Object paths hidden while this shot is on air (with its source camera's `hide`; the union with the previous shot's "
                     "inside the blend), also in replay passes and `mcam show` (MULTICAM.md 6.4).",
    "ShotSpec.motionBlur": "Scale of the replay pass motion blur while this shot is on air, its blend included (1 = unchanged, 0 = off).",
    "ShakeSpec.hand": "Game handshake copied in this camera's frame (k).", "ShakeSpec.fx": "Game impact shake copied (k).",
    "ShakeSpec.pullback": "Keep the running pull-back (z) of the handshake.", "ShakeSpec.replica": "Soft handheld from the game's noise clip.",
    "ShakeSpec.hard": "Hard handheld from the game's noise clip.", "ShakeSpec.clock": "`game` or `output` clock for the replica.",
    "ShakeSpec.rate": "Replica playback rate.", "ShakeSpec.phase": "Replica phase seconds.",
    "TargetRef.of": "Base: `boy`, `huddle`, `main`, `game`, `gameaim`, `probe`, path, `#id`, `none`, `self`.",
    "TargetRef.point": "Point on the base (`torso`, `bounds`, `com`, `bone:<name>`, ...).",
    "TargetRef.offset": "World offset added after the point.", "TargetRef.pos": "Fixed world point.",
    "TargetRef.group": "Group of objects (below).",
    "GroupSpec.of": "Member targets.", "GroupSpec.find": "ObjectDatabase query (`t:Type` is a substring match).",
    "GroupSpec.list": "`<path>::<Component>.<field>` list of members.", "GroupSpec.filter": "Member filter (below).",
    "GroupSpec.mode": "`centroid`, `weighted`, `bounds`, `nearest`, `fastest`.", "GroupSpec.point": "Point used on every member.",
    "GroupSpec.smooth": "SmoothDamp seconds on the group point.", "GroupSpec.hold": "Membership hysteresis seconds.",
    "GroupSpec.max": "Keep at most this many members (fastest first); 0 = all.",
    "GroupFilter.active": "Only active members.", "GroupFilter.alive": "Skip members whose `dead` field is true.",
    "GroupFilter.notKinematic": "Skip kinematic rigidbodies.", "GroupFilter.minSpeed": "Minimum speed m/s.",
    "GroupFilter.boxCenter": "Box filter centre.", "GroupFilter.boxSize": "Box filter size (0 = off).",
    "GroupFilter.near": "Reference target for `maxDist` / `nearest`.", "GroupFilter.maxDist": "Maximum distance to `near` (0 = off).",
    "MultiCamSpec.replay": "Replay mode (REPLAY_DESIGN 3.1): record pass, replay passes, sound capture; null = all defaults. A given block "
                           "switches `mcam check` and the offline checker to the replay lint.",
    "BoxSpec.center": "Box centre (world metres).", "BoxSpec.size": "Box size (world metres, every axis > 0).",
}


def type_name(schema: Schema, ctype: str) -> str:
    et, is_list = schema.nested_type(ctype)
    if is_list:
        return type_name(schema, et) + "[]"
    return {"float": "number", "double": "number", "int": "int", "bool": "bool", "string": "string", "Vector3": "[x,y,z]",
            "Vector2": "[a,b]", "float[]": "number[]", "TimeRef": "time", "TargetRef": "target", "Json.Obj": "object"}.get(ctype, ctype)


def default_text(v: Any) -> str:
    if v is None:
        return "null"
    if isinstance(v, str):
        return f'"{v}"'
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, (int, float)):
        t = fnum(v)
        if isinstance(v, float) and math.isfinite(v) and abs(float(t) - v) > 1e-6 * max(1.0, abs(v)):
            return fnum(v, 7)
        return t
    if isinstance(v, tuple):
        return "[" + ",".join(fnum(x) for x in v) + "]"
    if isinstance(v, TimeVal):
        return v.text()
    if isinstance(v, list):
        if v and all(isinstance(x, str) for x in v):
            return json.dumps(v, separators=(",", ":"))
        return "[]" if not v else f"[{len(v)} items]"
    return "{}"


def design_meanings(paths: Path | list[Path], schema: Schema) -> dict[str, tuple[str, str]]:
    out: dict[str, tuple[str, str]] = {}
    for path in [paths] if isinstance(paths, Path) else paths:
        if not path.exists():
            continue
        classes: list[str] = []
        for line in path.read_text(encoding="utf-8").splitlines():
            if line.startswith("#"):
                classes = [c for c in re.findall(r"`(\w+)`", line) if c in schema.classes]
                continue
            sw = CLASS_SWITCH.match(line)
            if sw and sw.group(1) in schema.classes:
                classes = [sw.group(1)]
                continue
            if not line.startswith("|") or not classes:
                continue
            cells = [c.strip() for c in re.split(r"(?<!\\)\|", line.strip().strip("|"))]
            if len(cells) < 4 or cells[0] in ("Key", "") or set(cells[0]) <= {"-"}:
                continue
            keys = [k.strip().strip("`").split(".")[-1] for k in cells[0].split("/")]
            defaults = [d.strip() for d in cells[2].split(" / ")]
            per_key = len(keys) > 1 and len(defaults) == len(keys)
            meaning = cells[-1]
            if len(cells) == 5:
                meaning = f"({cells[3]}) {meaning}"
            for i, k in enumerate(keys):
                for c in classes:
                    if schema.field(c, k) is not None and f"{c}.{k}" not in out:
                        out[f"{c}.{k}"] = (defaults[i] if per_key else cells[2], meaning)
                        break
    return out


def schema_md(schema: Schema, design: Path | list[Path]) -> tuple[str, list[str]]:
    meanings = design_meanings(design, schema)
    sources = " and ".join(f"`{p.name}`" for p in schema.paths)
    lines = [f"Generated by `python -I tools/mcam_spec_check.py --schema-md` from {sources}; meanings from the design docs.", ""]
    diffs: list[str] = []
    for cname in SCHEMA_ORDER:
        if cname not in schema.classes:
            continue
        lines += [f"#### {SCHEMA_TITLES.get(cname, cname)} (`{cname}`)", "", "| Key | Type | Default | Meaning |", "|---|---|---|---|"]
        inst = schema.new(cname)
        for f in schema.fields(cname):
            key = f"{cname}.{f.name}"
            doc_default, meaning = meanings.get(key, ("", EXTRA_MEANINGS.get(key, "")))
            dt = DEFAULT_TEXT.get(key) or default_text(inst.v.get(f.name))
            lines.append(f"| {f.name} | {type_name(schema, f.ctype)} | {dt} | {re.sub(r'(?<!\\)[|]', '/', meaning)} |")
            if not meaning:
                diffs.append(f"{key}: no meaning in the design doc")
            elif doc_default and key in meanings:
                a = re.sub(r"[`\"\s]", "", doc_default).lower()
                b = re.sub(r"[`\"\s]", "", dt).lower()
                same = a == b or (a in ("{}", "null", "-") and b in ("{}", "null")) or (a == "unset" and b == "unset")
                try:
                    same = same or abs(float(a) - float(b)) < 1e-6
                except ValueError:
                    pass
                if not same:
                    diffs.append(f"{key}: design default '{doc_default}' vs C# '{dt}'")
        lines.append("")
    return "\n".join(lines), diffs


def update_doc(doc: Path, md: str) -> bool:
    text = doc.read_text(encoding="utf-8")
    if SCHEMA_BEGIN not in text or SCHEMA_END not in text:
        return False
    head, rest = text.split(SCHEMA_BEGIN, 1)
    _, tail = rest.split(SCHEMA_END, 1)
    doc.write_text(f"{head}{SCHEMA_BEGIN}\n{md.rstrip()}\n{SCHEMA_END}{tail}", encoding="utf-8", newline="\n")
    return True


# ---------------------------------------------------------------------------------------------------- main


def main(argv: list[str] | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    ap = argparse.ArgumentParser(prog="mcam_spec_check.py", description="Offline checker for MultiCam v2 capture and replay specs.")
    ap.add_argument("specs", nargs="*", type=Path, help="spec files")
    ap.add_argument("--types", type=Path, nargs="+", action="extend",
                    help="C# schema sources (default MultiCamTypes.cs and ReplayTypes.cs); several files, or the option repeated")
    ap.add_argument("--design", type=Path, nargs="+", action="extend",
                    help="design docs with the meanings for --schema-md (default MULTICAM_DESIGN.md and REPLAY_DESIGN.md)")
    lint = ap.add_mutually_exclusive_group()
    lint.add_argument("--replay", dest="lint", action="store_const", const="replay", default="auto",
                      help="apply the replay lint (REPLAY_DESIGN 3.2) also to a spec without a replay block (mcam check <spec> replay)")
    lint.add_argument("--v2", dest="lint", action="store_const", const="v2", help="v2 lint only, even with a replay block")
    ap.add_argument("--schema-md", action="store_true", help="print the schema tables as Markdown")
    ap.add_argument("--update-doc", type=Path, help="replace the schema block of this Markdown file")
    ap.add_argument("--quiet", action="store_true", help="summary, errors and warnings only")
    ap.add_argument("--json", action="store_true", help="machine-readable output")
    ap.add_argument("--bind-only", action="store_true", help="only the SpecBind messages (cross-check with the C# binder)")
    ap.add_argument("--index", action="store_true", help="look object paths up in the level index (JSONL)")
    ap.add_argument("--index-dir", type=Path, default=DEFAULT_INDEX, help="level index folder for --index (default: env INSIDE_LEVEL_INDEX)")
    ap.add_argument("--levels", default="", help="with --index: only index files whose name contains this text")
    ap.add_argument("--rehearsal", type=Path, help="rehearsal take folder (overrides the spec's rehearsal)")
    ap.add_argument("--poses", type=Path, help="take folder (poses.csv, meta.csv, events.csv): check its camera moves against the motion limits")
    ap.add_argument("--ms", default=None, help="engine milliseconds per frame (v2) or per replay render for the time estimate, "
                                               f"'lo:hi' (default 25:40 for v2, {REPLAY_MS} for replay)")
    opts = ap.parse_args(argv)
    opts.specs += [p for p in opts.types or [] if p.suffix.lower() == ".json"]
    opts.types = [p for p in opts.types or [] if p.suffix.lower() != ".json"] or list(DEFAULT_TYPES)
    opts.specs += [p for p in opts.design or [] if p.suffix.lower() == ".json"]
    opts.design = [p for p in opts.design or [] if p.suffix.lower() != ".json"] or list(DEFAULT_DESIGN)
    try:
        schema = Schema(opts.types)
    except (OSError, ValueError) as e:
        print(f"cannot read the schema: {e}", file=sys.stderr)
        return 2
    if opts.schema_md or opts.update_doc:
        md, diffs = schema_md(schema, opts.design)
        if opts.update_doc:
            if not update_doc(opts.update_doc, md):
                print(f"{opts.update_doc}: no {SCHEMA_BEGIN} ... {SCHEMA_END} block", file=sys.stderr)
                return 2
            print(f"updated the schema block of {opts.update_doc}")
        else:
            print(md)
        for d in diffs:
            print(f"doc check: {d}", file=sys.stderr)
        if not opts.specs:
            return 0
    if not opts.specs:
        ap.print_usage(sys.stderr)
        return 2
    commands = load_commands(schema.path)
    checks = [Check(p, schema, opts, commands) for p in opts.specs]
    results = [c.run() for c in checks]
    if opts.index and not opts.bind_only:
        index_lookup(checks, opts.index_dir, opts.levels)
    if opts.bind_only:
        for r in results:
            print(f"== {r.path}")
            for level in ("warn", "error"):
                for m in r.report.msgs:
                    if m.stage == "bind" and m.level == level:
                        print(f"{level}: {m.line()}")
    elif opts.json:
        out = [result_json(r) for r in results]
        print(json.dumps(out if len(out) > 1 else out[0], indent=1, default=lambda o: o.text() if isinstance(o, TimeVal) else str(o)))
    else:
        for i, r in enumerate(results):
            if i:
                print()
            print_result(r, opts.quiet)
    if any(r.bad_input for r in results):
        return 2
    return 0 if all(r.report.ok for r in results) else 1


if __name__ == "__main__":
    sys.exit(main())
